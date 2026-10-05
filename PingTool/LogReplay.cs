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
            // Row by row: a file that is far too big (or that is not a log at all) is refused at the limit, not after every row of it has
            // been turned into a list of strings.
            using var rows = Rows(csv.TrimStart('﻿')).GetEnumerator();

            if (!rows.MoveNext() || string.Join(",", rows.Current.Fields) != PingLog.CsvHeader)
            {
                error = "This is not a PingTool log: the first line should be \"" + PingLog.CsvHeader + "\".";
                return false;
            }

            while (rows.MoveNext())
            {
                // `line` is the line of the FILE where the record starts, as an editor numbers it (a field with a line break in it
                // makes the records after it come later than their rank).
                var (r, line) = rows.Current;
                if (r.Count == 1 && r[0].Length == 0) continue;   // blank line
                if (r.Count != 5) { error = $"Line {line}: expected 5 fields, found {r.Count}."; return false; }
                if (!DateTimeOffset.TryParseExact(r[0], "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                { error = $"Line {line}: the date \"{Clip(r[0])}\" is not in the log format."; return false; }

                long? rtt = null;
                if (r[3].Length > 0)
                {
                    if (!long.TryParse(r[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ms))
                    { error = $"Line {line}: the round-trip time \"{Clip(r[3])}\" is not a number."; return false; }
                    rtt = ms;
                }

                if (r[1].Length == 0 || r[2].Length == 0) { error = $"Line {line}: the host or the status is empty."; return false; }
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
        // One row at a time, as the reader asks for it (a row is handed over as soon as its line ends).
        // Each record comes with the line of the FILE where it starts (from 1, a CRLF or a lone CR or LF is one line break, inside a
        // quoted field too): a record is not a line once a field holds a line break.
        private static IEnumerable<(List<string> Fields, int Line)> Rows(string text)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, wasQuoted = false;
            (List<string> Fields, int Line)? finished = null;
            int line = 1, rowStart = 1;

            void EndField() { row.Add(field.ToString()); field.Clear(); wasQuoted = false; }
            void EndRow() { EndField(); finished = (row, rowStart); row = new List<string>(); }

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
                    else
                    {
                        field.Append(c);
                        // A line break held in a field: the physical line goes on although the record does not end.
                        if (c == '\n' || (c == '\r' && !(i + 1 < text.Length && text[i + 1] == '\n'))) line++;
                    }
                }
                else if (c == '"' && field.Length == 0 && !wasQuoted) { quoted = true; wasQuoted = true; }
                else if (c == ',') EndField();
                else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); line++; rowStart = line; }
                else if (c == '\n') { EndRow(); line++; rowStart = line; }
                else field.Append(c);

                if (finished is { } done) { yield return done; finished = null; }
            }

            if (field.Length > 0 || row.Count > 0 || wasQuoted)   // last line without a line break
            {
                EndRow();
                yield return finished!.Value;
            }
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
            var gapOf = GapLimits(ordered);
            var lastSeen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            foreach (var e in ordered)
            {
                if (!sessions.TryGetValue(e.Host, out var session)) sessions[e.Host] = session = new HostSession(e.Host);

                // A hole in the log (PingTool was closed, the PC slept, one file per day): the pings on either side of it are not
                // consecutive. Live, the run would have ended; here the incident open at the last ping before the hole ends there, and
                // the monitor starts again, instead of one incident that also counts the time when nothing was measured.
                if (lastSeen.TryGetValue(e.Host, out var before) && e.Time - before > gapOf[e.Host])
                {
                    incidents.CloseOpen(e.Host, before);
                    session.ApplyThresholds(HostMonitor.DefaultLatencyMs, HostMonitor.DefaultLossPercent, HostMonitor.DefaultDownAfter);
                }

                lastSeen[e.Host] = e.Time;

                bool ok = e.Status == "OK" && e.RttMs is not null;
                long ping = ok ? e.RttMs!.Value : -1;
                PingFailure? failure = ok ? null : new PingFailure(e.Status, e.Detail);
                session.Add(ping, failure, e.Time);
                var change = session.Monitor.Update(ping);
                incidents.Observe(e.Time, e.Host, ping, session.LastFailure, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs);
            }

            return new ReplayResult(sessions, incidents, ordered[0].Time, ordered[^1].Time);
        }

        // The silence after which two pings of one host are no longer consecutive: ten times its usual interval (the median of the
        // gaps between its pings), and never less than five minutes, so that a few missed pings are not a hole.
        public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5);
        public const int GapFactor = 10;

        internal static Dictionary<string, TimeSpan> GapLimits(IReadOnlyList<LogEntry> ordered)
        {
            var limits = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            foreach (var host in ordered.GroupBy(e => e.Host, StringComparer.Ordinal))
            {
                var times = host.Select(e => e.Time).ToList();
                var steps = new List<TimeSpan>(times.Count);
                for (int i = 1; i < times.Count; i++) steps.Add(times[i] - times[i - 1]);
                steps.Sort();
                var usual = steps.Count == 0 ? TimeSpan.Zero : steps[steps.Count / 2];
                limits[host.Key] = usual * GapFactor > MinGap ? usual * GapFactor : MinGap;
            }

            return limits;
        }
    }
}
