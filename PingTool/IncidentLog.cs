using System.Globalization;

namespace PingTool
{
    internal enum IncidentKind { Outage, Slowdown }

    internal sealed class Incident
    {
        public required string Host { get; init; }
        public required IncidentKind Kind { get; init; }
        public required DateTimeOffset Start { get; init; }

        // null while it is still going on.
        public DateTimeOffset? End { get; set; }

        // Outage: failed pings from the first failure of the streak to now. Slowdown: 0.
        public int FailedPings { get; set; }

        // Outage: the failure cause seen most often ("Timeout", "No host"...). Slowdown: "".
        public string Cause { get; set; } = "";

        // Slowdown only: what the 10-ping window showed when it was detected.
        public double LossPercent { get; init; }
        public double? AvgMs { get; init; }

        // 1 for the first outage of this host, 2 for the second...: how often it came back.
        public int Occurrence { get; init; }

        // Outage only: the route to the host captured when the outage was declared, and what
        // differs from the route seen while it was healthy. Filled in later (a trace takes seconds).
        public PathCapture? Path { get; set; }
        public List<string> PathNotes { get; } = new();

        public string PathText() =>
            Path is null ? "" : Path.Describe() + (PathNotes.Count > 0 ? "\n" + string.Join("\n", PathNotes) : "");

        public bool Ongoing => End is null;
        public TimeSpan Duration(DateTimeOffset now) => (End ?? now) - Start;
    }

    // Turns the stream of pings of every host into a list of INCIDENTS: periods, not probes.
    //
    // An outage starts at the FIRST failure of the run of failures (HostMonitor only says
    // "down" at the third, but the host was already silent) and ends at the first success.
    // A slowdown is only known once HostMonitor has seen ten pings, so it is dated at its
    // DETECTION; it ends at "recovered", or where an outage starts.
    internal sealed class IncidentLog
    {
        private sealed class Track
        {
            public DateTimeOffset? StreakStart;
            public int StreakCount;
            public readonly Dictionary<string, int> StreakCauses = new();
            public Incident? Outage;
            public Incident? Slow;
            // How many of each kind this host has had: the next incident's "#", without counting the whole list each time.
            public int Outages, Slowdowns;
        }

        private readonly List<Incident> incidents = new();
        private readonly Dictionary<string, Track> tracks = new();

        public IReadOnlyList<Incident> Incidents => incidents;

        public void Clear()
        {
            incidents.Clear();
            tracks.Clear();
        }

        // ping < 0 = failure (with its cause); change = what HostMonitor.Update just answered.
        public void Observe(DateTimeOffset time, string host, long ping, PingFailure? failure,
                            HostChange change, double windowLossPercent, double? windowAvgMs)
        {
            if (!tracks.TryGetValue(host, out var t)) tracks[host] = t = new Track();
            bool ok = ping >= 0;

            if (!ok)
            {
                t.StreakStart ??= time;
                t.StreakCount++;
                string cause = (failure ?? PingFailure.Timeout).Short;
                t.StreakCauses[cause] = t.StreakCauses.GetValueOrDefault(cause) + 1;

                if (t.Outage is not null)
                {
                    t.Outage.FailedPings = t.StreakCount;
                    t.Outage.Cause = Top(t.StreakCauses);
                }
            }

            switch (change)
            {
                case HostChange.Down:
                    var begin = t.StreakStart ?? time;
                    if (t.Slow is not null)
                    {
                        // The outage is dated at the first failed ping, which can come BEFORE the moment the slowdown was noticed
                        // (the first failures push the loss of the window over the limit): that "slowdown" is the beginning of
                        // the outage, not a separate event, and keeping it would give it an end before its start.
                        if (t.Slow.Start >= begin) { incidents.Remove(t.Slow); t.Slowdowns--; }   // it never counted
                        else t.Slow.End = begin;
                        t.Slow = null;
                    }
                    t.Outage = Open(t, host, IncidentKind.Outage, begin, 0, null);
                    t.Outage.FailedPings = t.StreakCount;
                    t.Outage.Cause = Top(t.StreakCauses);
                    break;

                case HostChange.Up:
                    if (t.Outage is not null) { t.Outage.End = time; t.Outage = null; }
                    break;

                case HostChange.Degraded:
                    // One slowdown at a time: a second "Degraded" without a "Recovered" in between closes the first instead of
                    // leaving it "ongoing" for ever with nothing left that could end it.
                    if (t.Slow is not null) t.Slow.End = time;
                    t.Slow = Open(t, host, IncidentKind.Slowdown, time, windowLossPercent, windowAvgMs);
                    break;

                case HostChange.Recovered:
                    if (t.Slow is not null) { t.Slow.End = time; t.Slow = null; }
                    break;
            }

            if (ok)
            {
                t.StreakStart = null;
                t.StreakCount = 0;
                t.StreakCauses.Clear();
            }
        }

        private Incident Open(Track t, string host, IncidentKind kind, DateTimeOffset start, double loss, double? avg)
        {
            var incident = new Incident
            {
                Host = host,
                Kind = kind,
                Start = start,
                LossPercent = loss,
                AvgMs = avg,
                Occurrence = kind == IncidentKind.Outage ? ++t.Outages : ++t.Slowdowns,
            };
            incidents.Add(incident);
            return incident;
        }

        // Most frequent cause; ties go to the one seen first.
        private static string Top(Dictionary<string, int> causes) =>
            causes.Count == 0 ? "" : causes.MaxBy(kv => kv.Value).Key;

        public string Summary(DateTimeOffset now)
        {
            if (incidents.Count == 0) return "No incident.";

            var outages = incidents.Where(i => i.Kind == IncidentKind.Outage).ToList();
            int slow = incidents.Count - outages.Count;
            int hosts = incidents.Select(i => i.Host).Distinct().Count();
            var parts = new List<string>();

            if (outages.Count > 0)
            {
                var down = TimeWithAnOutage(outages, now);
                // With one outage the longest is the total: saying it twice adds nothing.
                string longest = outages.Count > 1 ? $", longest {FormatDuration(outages.Max(i => i.Duration(now)))}" : "";
                // Several hosts down at once (the box went off) count once: the time during which at least one was down.
                string what = outages.Select(i => i.Host).Distinct().Count() > 1 ? "with a target down" : "down";
                parts.Add($"{outages.Count} outage{(outages.Count == 1 ? "" : "s")} ({FormatDuration(down)} {what}{longest})");
            }

            if (slow > 0) parts.Add($"{slow} slowdown{(slow == 1 ? "" : "s")}");

            return string.Create(CultureInfo.InvariantCulture,
                $"{incidents.Count} incident{(incidents.Count == 1 ? "" : "s")} on {hosts} host{(hosts == 1 ? "" : "s")}: {string.Join(", ", parts)}.");
        }

        // The length of the union of the outages' intervals: two hosts down for the same ten minutes are ten minutes, not twenty.
        internal static TimeSpan TimeWithAnOutage(IEnumerable<Incident> outages, DateTimeOffset now)
        {
            var total = TimeSpan.Zero;
            DateTimeOffset? from = null, to = null;
            foreach (var i in outages.OrderBy(o => o.Start))
            {
                var end = i.End ?? now;
                if (end < i.Start) end = i.Start;
                if (to is null || i.Start > to) { if (from is not null) total += to!.Value - from.Value; from = i.Start; to = end; }
                else if (end > to) to = end;
            }

            if (from is not null) total += to!.Value - from.Value;
            return total;
        }

        public static string FormatDuration(TimeSpan d)
        {
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            long s = (long)d.TotalSeconds;
            if (s < 60) return s + " s";
            if (s < 3600) return $"{s / 60} min {s % 60:00} s";
            return $"{s / 3600} h {s % 3600 / 60:00} min";
        }
    }
}
