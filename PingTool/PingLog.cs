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

        public void Add(DateTimeOffset time, string host, string status, long? rttMs, string detail = "")
        {
            entries.Enqueue(new LogEntry(time, host, status, rttMs, detail));
            while (entries.Count > MaxEntries) entries.Dequeue();
        }

        public void WriteCsv(TextWriter writer)
        {
            writer.NewLine = "\r\n";
            writer.WriteLine("timestamp,host,status,rtt_ms,detail");
            foreach (var e in entries)
            {
                writer.WriteLine(string.Join(",",
                    e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    Field(e.Host),
                    Field(e.Status),
                    e.RttMs?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Field(e.Detail)));
            }
        }

        // RFC 4180 quoting, plus a leading ' on anything a spreadsheet would
        // run as a formula: the host is whatever the user typed.
        private static string Field(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            if (value.IndexOfAny(CsvSpecials) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
