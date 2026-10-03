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
                    if (t.Slow is not null) { t.Slow.End = begin; t.Slow = null; }
                    t.Outage = Open(host, IncidentKind.Outage, begin, 0, null);
                    t.Outage.FailedPings = t.StreakCount;
                    t.Outage.Cause = Top(t.StreakCauses);
                    break;

                case HostChange.Up:
                    if (t.Outage is not null) { t.Outage.End = time; t.Outage = null; }
                    break;

                case HostChange.Degraded:
                    t.Slow = Open(host, IncidentKind.Slowdown, time, windowLossPercent, windowAvgMs);
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

        private Incident Open(string host, IncidentKind kind, DateTimeOffset start, double loss, double? avg)
        {
            var incident = new Incident
            {
                Host = host,
                Kind = kind,
                Start = start,
                LossPercent = loss,
                AvgMs = avg,
                Occurrence = incidents.Count(i => i.Host == host && i.Kind == kind) + 1,
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
                var down = outages.Aggregate(TimeSpan.Zero, (sum, i) => sum + i.Duration(now));
                parts.Add($"{outages.Count} outage{(outages.Count == 1 ? "" : "s")} ({FormatDuration(down)} down)");
            }

            if (slow > 0) parts.Add($"{slow} slowdown{(slow == 1 ? "" : "s")}");

            return string.Create(CultureInfo.InvariantCulture,
                $"{incidents.Count} incident{(incidents.Count == 1 ? "" : "s")} on {hosts} host{(hosts == 1 ? "" : "s")}: {string.Join(", ", parts)}.");
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
