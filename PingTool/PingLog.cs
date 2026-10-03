using System.Globalization;

namespace PingTool
{
    internal sealed record LogEntry(DateTimeOffset Time, string Host, string Status, long? RttMs, string Detail);

    // Timestamped record of every ping since Start, for export. Capped so a
    // session left running for days cannot eat the memory: the oldest go first.
    internal sealed class PingLog
    {
        public const int MaxEntries = 100_000;
        private static readonly char[] CsvSpecials = { ',', '"', '\r', '\n' };
        private readonly Queue<LogEntry> entries = new();

        public int Count => entries.Count;

        // Oldest first. Read-only: the timeline draws from it.
        public IReadOnlyCollection<LogEntry> Entries => entries;

        public void Clear() => entries.Clear();

        // Returns the entry, so the automatic log writes exactly what the in-memory log holds.
        public LogEntry Add(DateTimeOffset time, string host, string status, long? rttMs, string detail = "")
        {
            var entry = new LogEntry(time, host, status, rttMs, detail);
            entries.Enqueue(entry);
            while (entries.Count > MaxEntries) entries.Dequeue();
            return entry;
        }

        public const string CsvHeader = "timestamp,host,status,rtt_ms,detail";

        public void WriteCsv(TextWriter writer)
        {
            writer.NewLine = "\r\n";
            writer.WriteLine(CsvHeader);
            foreach (var e in entries) writer.WriteLine(CsvLine(e));
        }

        // One row, shared by the export and the automatic log so the two files read the same.
        public static string CsvLine(LogEntry e) => string.Join(",",
            e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            Field(e.Host),
            Field(e.Status),
            e.RttMs?.ToString(CultureInfo.InvariantCulture) ?? "",
            Field(e.Detail));

        // A spreadsheet runs a field as a formula when it begins with = + - @, or with a tab or a CR.
        // Looking at the very first character is not enough: spaces, a no-break space, a line feed, a
        // zero-width space or a byte-order mark in front hide the formula character from a check on
        // value[0] while a spreadsheet may still skip them. So: the first VISIBLE character decides.
        private static bool StartsLikeFormula(string value)
        {
            if (value.Length > 0 && (value[0] == '\t' || value[0] == '\r')) return true;

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                return "=+-@".Contains(c, StringComparison.Ordinal);
            }

            return false;
        }

        // RFC 4180 quoting, plus a leading ' on anything a spreadsheet would
        // run as a formula: the host is whatever the user typed.
        private static string Field(string value)
        {
            if (StartsLikeFormula(value)) value = "'" + value;
            if (value.IndexOfAny(CsvSpecials) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
