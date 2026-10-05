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

        // 1 for the first incident of this host AND of this kind, 2 for the second...: outages and slowdowns are counted
        // separately, per host (an outage is "how often it went down", a slowdown "how often it got slow").
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
            // The causes of the streak with how often each was seen, in the order they were FIRST seen (a Dictionary does not promise an
            // order of enumeration, and the tie-break below depends on it).
            public readonly List<(string Cause, int Count)> StreakCauses = new();

            public void CountCause(string cause)
            {
                int at = StreakCauses.FindIndex(c => c.Cause == cause);
                if (at >= 0) StreakCauses[at] = (cause, StreakCauses[at].Count + 1);
                else StreakCauses.Add((cause, 1));
            }
            public Incident? Outage;
            // The first ping that answered while the outage above was still open (no "Up" came with it): when that outage really ended,
            // should a new "Down" arrive before any "Up".
            public DateTimeOffset? AnsweredWhileDown;
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
                t.CountCause(cause);

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
                    // A second "Down" without an "Up" between (the monitor was replaced while the host stayed down, or it was told
                    // about no recovery): an outage already open must not be left "ongoing" for ever under a new one.
                    if (t.Outage is not null)
                    {
                        // Dated by the same first failure: the host never answered in between, so it is ONE outage that goes on.
                        if (t.Outage.Start >= begin) { t.Outage.FailedPings = t.StreakCount; t.Outage.Cause = Top(t.StreakCauses); break; }
                        // A ping answered in between (the streak restarted): the first outage ended with that answer (at the latest when the
                        // new run of failures began).
                        t.Outage.End = t.AnsweredWhileDown is { } answered && answered <= begin ? answered : begin;
                    }

                    t.AnsweredWhileDown = null;
                    t.Outage = Open(t, host, IncidentKind.Outage, begin, 0, null);
                    t.Outage.FailedPings = t.StreakCount;
                    t.Outage.Cause = Top(t.StreakCauses);
                    break;

                case HostChange.Up:
                    if (t.Outage is not null) { t.Outage.End = time; t.Outage = null; t.AnsweredWhileDown = null; }
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
                if (t.Outage is not null) t.AnsweredWhileDown ??= time;
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
        private static string Top(List<(string Cause, int Count)> causes)
        {
            string best = "";
            int most = 0;
            foreach (var (cause, count) in causes)
                if (count > most) { best = cause; most = count; }   // strictly more: on a tie the first seen keeps it

            return best;
        }

        // The "Cause / detail" of an incident, as the list shows it: an outage's cause, or what a slowdown measured.
        public static string CauseText(Incident i, CultureInfo c) => i.Kind == IncidentKind.Outage
            ? i.Cause
            : string.Format(c, "{0}% loss, avg {1} ms", Availability.FormatLoss(i.LossPercent, c), i.AvgMs?.ToString("0.#", c) ?? "-");

        // The same, whole, as the first line of the details of the selected incident: the column can be too narrow for it.
        public static string DetailLine(Incident i, CultureInfo c) => i.Kind == IncidentKind.Outage
            ? $"Cause: {CauseText(i, c)} ({i.FailedPings.ToString(c)} failed ping(s))"
            : $"Detail: {CauseText(i, c)}";

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

        // How incidents are dated, written once for the window and the report. The size of the window is the detector's own constant, so
        // this text cannot go out of date with the rule it describes.
        public static string DatingNote => string.Create(CultureInfo.InvariantCulture,
            $"An outage starts at its first failed ping. A slowdown is dated when detected (after {HostMonitor.WindowSize} pings).");

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
