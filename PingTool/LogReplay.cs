using System.Globalization;
using System.Text;

namespace PingTool
{
    // Reads back the CSV that PingLog.WriteCsv and AutoLog write, so that a night recorded on disk can be looked at
    // again after PingTool was closed (or crashed): the same incident detection and the same report as live.
    internal static class PingLogReader
    {
        public const int MaxEntries = 2_000_000;   // about 23 days of one ping a second: a bigger file is not a PingTool log

        // Several files at once (one per host and day) are fine: the caller concatenates the entries.
        public static bool TryParse(string csv, out List<LogEntry> entries, out string error)
        {
            entries = new List<LogEntry>();
            error = "";
            var rows = Rows(csv.TrimStart('﻿'));

            if (rows.Count == 0 || string.Join(",", rows[0]) != PingLog.CsvHeader)
            {
                error = "This is not a PingTool log: the first line should be \"" + PingLog.CsvHeader + "\".";
                return false;
            }

            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Count == 1 && r[0].Length == 0) continue;   // blank line
                if (r.Count != 5) { error = $"Line {i + 1}: expected 5 fields, found {r.Count}."; return false; }
                if (!DateTimeOffset.TryParseExact(r[0], "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                { error = $"Line {i + 1}: the date \"{Clip(r[0])}\" is not in the log format."; return false; }

                long? rtt = null;
                if (r[3].Length > 0)
                {
                    if (!long.TryParse(r[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ms))
                    { error = $"Line {i + 1}: the round-trip time \"{Clip(r[3])}\" is not a number."; return false; }
                    rtt = ms;
                }

                if (r[1].Length == 0 || r[2].Length == 0) { error = $"Line {i + 1}: the host or the status is empty."; return false; }
                if (entries.Count >= MaxEntries) { error = "This file has more than " + MaxEntries.ToString("N0", CultureInfo.CurrentCulture) + " lines: too many to be a PingTool log."; return false; }
                entries.Add(new LogEntry(time, Unprotect(r[1]), Unprotect(r[2]), rtt, Unprotect(r[4])));
            }

            if (entries.Count == 0) { error = "This log has no ping in it."; return false; }
            return true;
        }

        // PingLog protects a field a spreadsheet would run as a formula by putting ' in front: take it off again,
        // but only when what follows would indeed have been protected (a host that really starts with ' keeps it).
        private static string Unprotect(string field) =>
            field.Length > 1 && field[0] == '\'' && PingLog.StartsLikeFormula(field[1..]) ? field[1..] : field;

        private static string Clip(string s) => s.Length <= 30 ? s : s[..30] + "...";

        // RFC 4180: quoted fields may hold commas, quotes ("") and line breaks.
        private static List<List<string>> Rows(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, wasQuoted = false;

            void EndField() { row.Add(field.ToString()); field.Clear(); wasQuoted = false; }
            void EndRow() { EndField(); rows.Add(row); row = new List<string>(); }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else quoted = false;
                    }
                    else field.Append(c);
                }
                else if (c == '"' && field.Length == 0 && !wasQuoted) { quoted = true; wasQuoted = true; }
                else if (c == ',') EndField();
                else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
                else if (c == '\n') EndRow();
                else field.Append(c);
            }

            if (field.Length > 0 || row.Count > 0 || wasQuoted) EndRow();   // last line without a line break
            return rows;
        }
    }

    // What a replay gives back: one session per host (statistics, last 180 pings, state), the incidents, the period.
    internal sealed record ReplayResult(Dictionary<string, HostSession> Sessions, IncidentLog Incidents, DateTimeOffset From, DateTimeOffset To);

    internal static class LogReplay
    {
        // The pings go through the same HostMonitor and IncidentLog as live ones, in time order (a stable sort: two pings
        // of one instant keep the order of the file). The thresholds are the defaults: the log does not say which were used.
        public static ReplayResult Run(IReadOnlyCollection<LogEntry> entries)
        {
            var ordered = entries.Select((e, i) => (e, i)).OrderBy(x => x.e.Time).ThenBy(x => x.i).Select(x => x.e).ToList();
            var sessions = new Dictionary<string, HostSession>(StringComparer.Ordinal);
            var incidents = new IncidentLog();

            foreach (var e in ordered)
            {
                if (!sessions.TryGetValue(e.Host, out var session)) sessions[e.Host] = session = new HostSession(e.Host);

                bool ok = e.Status == "OK" && e.RttMs is not null;
                long ping = ok ? e.RttMs!.Value : -1;
                PingFailure? failure = ok ? null : new PingFailure(e.Status, e.Detail);
                session.Add(ping, failure);
                var change = session.Monitor.Update(ping);
                incidents.Observe(e.Time, e.Host, ping, session.LastFailure, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs);
            }

            return new ReplayResult(sessions, incidents, ordered[0].Time, ordered[^1].Time);
        }
    }
}
