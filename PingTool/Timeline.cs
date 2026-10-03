using System.Globalization;

namespace PingTool
{
    // One column of the timeline: everything that happened to the host in that slice of time.
    internal sealed record TimelineBucket(DateTimeOffset Start, int Sent, int Lost, double? MinMs, double? AvgMs, double? MaxMs);

    internal sealed record TimelineData(string Host, DateTimeOffset From, DateTimeOffset To, TimeSpan BucketSpan,
        IReadOnlyList<TimelineBucket> Buckets, long TopMs, int Sent, int Lost)
    {
        public bool IsEmpty => Sent == 0;
        public TimeSpan Span => To - From;
    }

    // What to draw, as plain geometry (testable without a screen):
    //   Bars       the min..max latency of each column that had replies
    //   AvgRuns    the average latency, one polyline per run of columns with replies
    //   LossCells  columns that lost pings, with the lost fraction 0..1 (drawn as red, stronger = more lost)
    //   Bands      the incidents of this host over the time axis (outage or slowdown)
    internal sealed record TimelineShapes(
        IReadOnlyList<(float X, float YMin, float YMax)> Bars,
        IReadOnlyList<IReadOnlyList<GraphPoint>> AvgRuns,
        IReadOnlyList<(float X, float Width, float Fraction)> LossCells,
        IReadOnlyList<(float X0, float X1, IncidentKind Kind)> Bands);

    // The WHOLE session of one host, squeezed into a fixed number of columns, instead of the last
    // 180 pings. The first ping is the left edge and the last one the right edge.
    internal static class Timeline
    {
        public static TimelineData Build(IEnumerable<LogEntry> entries, string host, int buckets)
        {
            buckets = Math.Max(1, buckets);
            var mine = entries.Where(e => e.Host == host).ToList();
            if (mine.Count == 0)
                return new TimelineData(host, DateTimeOffset.MinValue, DateTimeOffset.MinValue, TimeSpan.Zero, Array.Empty<TimelineBucket>(), 50, 0, 0);

            var from = mine.Min(e => e.Time);
            var to = mine.Max(e => e.Time);
            if (to - from < TimeSpan.FromSeconds(1)) to = from + TimeSpan.FromSeconds(1);   // a single instant still needs a width

            long ticks = Math.Max(1, (to - from).Ticks / buckets);
            var sent = new int[buckets];
            var lost = new int[buckets];
            var count = new int[buckets];
            var sum = new double[buckets];
            var min = new double?[buckets];
            var max = new double?[buckets];

            foreach (var e in mine)
            {
                int i = (int)Math.Min(buckets - 1, (e.Time - from).Ticks / ticks);   // the last instant belongs to the last column
                sent[i]++;
                if (e.RttMs is not long ms) { lost[i]++; continue; }
                count[i]++;
                sum[i] += ms;
                min[i] = min[i] is null ? ms : Math.Min(min[i]!.Value, ms);
                max[i] = max[i] is null ? ms : Math.Max(max[i]!.Value, ms);
            }

            var list = new List<TimelineBucket>(buckets);
            for (int i = 0; i < buckets; i++)
                list.Add(new TimelineBucket(from + TimeSpan.FromTicks(ticks * i), sent[i], lost[i], min[i], count[i] == 0 ? null : sum[i] / count[i], max[i]));

            long top = Math.Max(50, (long)Math.Ceiling(list.Max(b => b.MaxMs ?? 0)));
            return new TimelineData(host, from, to, TimeSpan.FromTicks(ticks), list, top, mine.Count, mine.Count(e => e.RttMs is null));
        }

        // Two lines under the chart: how long and how much, and WHEN it was worst.
        public static string Describe(TimelineData d)
        {
            if (d.IsEmpty) return "No data yet: start pinging this host.";

            var c = CultureInfo.CurrentCulture;
            string when(DateTimeOffset t) => t.ToLocalTime().ToString("G", c);
            string num(double v) => v.ToString("0.#", c);

            string lines = string.Format(c, "{0} to {1} ({2}) | {3} pings, {4} lost ({5}%)",
                when(d.From), when(d.To), IncidentLog.FormatDuration(d.Span), d.Sent, d.Lost, num(100.0 * d.Lost / d.Sent));

            var slowest = d.Buckets.Where(b => b.AvgMs is not null).OrderByDescending(b => b.AvgMs).FirstOrDefault();
            if (slowest is not null)
                lines += "\n" + string.Format(c, "Highest average: {0} ms around {1}", num(slowest.AvgMs!.Value), when(slowest.Start));

            var lossiest = d.Buckets.Where(b => b.Lost > 0).OrderByDescending(b => (double)b.Lost / b.Sent).FirstOrDefault();
            if (lossiest is not null)
                lines += "\n" + string.Format(c, "Most losses: {0}% of the pings around {1}", num(100.0 * lossiest.Lost / lossiest.Sent), when(lossiest.Start));

            return lines;
        }

        // Round times for the time axis (every 5 min, every hour...), in LOCAL time so that "10:00"
        // is really 10 o'clock whatever the UTC offset. At most maxTicks marks.
        public static IReadOnlyList<(DateTimeOffset Time, string Label)> Ticks(DateTimeOffset from, DateTimeOffset to, int maxTicks)
        {
            // seconds: 1 s .. 1 min .. 1 h .. 1 day .. 1 week .. 1 month .. 1 year
            var steps = new long[] { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400,
                172800, 604800, 1209600, 2592000, 7776000, 31536000 };
            double span = Math.Max(1, (to - from).TotalSeconds);
            long step = steps.FirstOrDefault(s => span / s <= Math.Max(1, maxTicks));
            // longer than any listed step allows: whole days, as many as needed
            if (step == 0) step = (long)Math.Ceiling(span / Math.Max(1, maxTicks) / 86400) * 86400;

            var local = from.ToLocalTime();
            var origin = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
            double firstIndex = Math.Ceiling((local - origin).TotalSeconds / step);

            bool withDate = to.ToLocalTime().Date != local.Date || span >= 86400;
            string format = step < 60 ? "HH:mm:ss" : "HH:mm";
            if (withDate) format = "MM-dd " + format;

            var ticks = new List<(DateTimeOffset, string)>();
            for (double k = firstIndex; ; k++)
            {
                var t = origin + TimeSpan.FromSeconds(k * step);
                if (t > to) break;
                ticks.Add((t, t.ToString(format, CultureInfo.CurrentCulture)));
            }

            return ticks;
        }
    }

    internal static class TimelineLayout
    {
        public static TimelineShapes Build(TimelineData d, IReadOnlyList<Incident> incidents, int width, int plotHeight)
        {
            var bars = new List<(float, float, float)>();
            var runs = new List<IReadOnlyList<GraphPoint>>();
            var cells = new List<(float, float, float)>();
            var bands = new List<(float, float, IncidentKind)>();
            if (d.IsEmpty) return new TimelineShapes(bars, runs, cells, bands);

            float cell = Math.Max(1f, (float)width / d.Buckets.Count);
            float Y(double v) => (float)(plotHeight - 1 - (plotHeight - 1.0) * v / d.TopMs);
            float X(DateTimeOffset t) => (float)((t - d.From).Ticks / (double)d.Span.Ticks * width);

            List<GraphPoint>? run = null;
            foreach (var b in d.Buckets)
            {
                float x = X(b.Start);
                if (b.Lost > 0) cells.Add((x, cell, (float)b.Lost / b.Sent));

                if (b.AvgMs is null)
                {
                    run = null;   // a column without a reply breaks the average line
                    continue;
                }

                bars.Add((x, Y(b.MinMs!.Value), Y(b.MaxMs!.Value)));
                if (run is null) runs.Add(run = new List<GraphPoint>());
                run.Add(new GraphPoint(x + cell / 2, Y(b.AvgMs.Value)));
            }

            foreach (var i in incidents.Where(i => i.Host == d.Host))
            {
                var start = i.Start > d.From ? i.Start : d.From;
                var end = (i.End ?? d.To) < d.To ? (i.End ?? d.To) : d.To;
                if (end < start) continue;   // entirely outside this session's time range

                float x0 = X(start);
                float x1 = Math.Min(width, Math.Max(X(end), x0 + 2));   // a short incident must still be visible
                bands.Add((x0, x1, i.Kind));
            }

            return new TimelineShapes(bars, runs, cells, bands);
        }
    }
}
