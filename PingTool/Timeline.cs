using System.Globalization;

namespace PingTool
{
    // One column of the timeline: everything that happened to the host in that slice of time.
    internal sealed record TimelineBucket(DateTimeOffset Start, int Sent, int Lost, double? MinMs, double? AvgMs, double? MaxMs);

    internal sealed record TimelineData(string Host, DateTimeOffset From, DateTimeOffset To, TimeSpan BucketSpan,
        IReadOnlyList<TimelineBucket> Buckets, long TopMs, int Sent, int Lost, long PeakMs = 0)
    {
        // TopMs is the scale of the chart; PeakMs is the true highest reply. They differ when a rare spike would flatten the rest.
        public bool IsClipped => PeakMs > TopMs;
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
        IReadOnlyList<(float X0, float X1, IncidentKind Kind)> Bands,
        // X of each network change of this PC that falls inside the time range (drawn as a vertical line).
        IReadOnlyList<float>? NetworkMarkers = null,
        // X of each column whose highest reply is above the scale (drawn as a mark at the top: the bar is cut there).
        IReadOnlyList<float>? Clipped = null);

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

            long peak = (long)Math.Ceiling(list.Max(b => b.MaxMs ?? 0));
            long top = Scale(list.Where(b => b.MaxMs is not null).Select(b => b.MaxMs!.Value).ToList());
            return new TimelineData(host, from, to, TimeSpan.FromTicks(ticks), list, top, mine.Count, mine.Count(e => e.RttMs is null), peak);
        }

        public const double ScalePercentile = 0.98;

        // The top of the chart: the 98th percentile of the columns' highest replies, rounded up to 1, 2 or 5 times a power of ten,
        // at least 50 ms. One reply of 3000 ms in a long session would otherwise squeeze every other column into a few pixels;
        // the columns above the scale are cut and marked instead (see TimelineShapes.Clipped), and the true peak is written out.
        internal static long Scale(IReadOnlyList<double> columnMaxima)
        {
            if (columnMaxima.Count == 0) return 50;
            var sorted = columnMaxima.OrderBy(v => v).ToList();
            double p = sorted[Math.Max(0, (int)Math.Ceiling(ScalePercentile * sorted.Count - 1e-9) - 1)];
            double top = 50;
            for (double decade = 10; ; decade *= 10)
            {
                foreach (double m in new[] { 1, 2, 5 })
                    if (m * decade >= p) return (long)Math.Max(top, m * decade);
            }
        }

        // Two lines under the chart: how long and how much, and WHEN it was worst.
        public static string Describe(TimelineData d)
        {
            if (d.IsEmpty) return Loc.T("timeline.empty");

            var c = CultureInfo.CurrentCulture;
            string when(DateTimeOffset t) => t.ToLocalTime().ToString("G", c);
            string num(double v) => v.ToString("0.#", c);

            string lines = Loc.T("timeline.summary",
                when(d.From), when(d.To), IncidentLog.FormatDuration(d.Span), d.Sent, d.Lost, num(100.0 * d.Lost / d.Sent));

            var slowest = d.Buckets.Where(b => b.AvgMs is not null).OrderByDescending(b => b.AvgMs).FirstOrDefault();
            if (slowest is not null)
                lines += "\n" + Loc.T("timeline.slowest", num(slowest.AvgMs!.Value), when(slowest.Start));

            // The most pings lost, then the larger share: "1 of 1 lost" must not hide "50 of 60 lost" (the earliest column wins a tie).
            var lossiest = d.Buckets.Where(b => b.Lost > 0).OrderByDescending(b => b.Lost).ThenByDescending(b => (double)b.Lost / b.Sent).FirstOrDefault();
            if (lossiest is not null)
                lines += "\n" + Loc.T("timeline.lossiest", num(100.0 * lossiest.Lost / lossiest.Sent), lossiest.Lost, lossiest.Sent, when(lossiest.Start));

            if (d.IsClipped) lines += "\n" + Loc.T("timeline.peak", d.PeakMs, d.TopMs);

            return lines;
        }

        // Round times for the time axis (every 5 min, every hour...), in LOCAL time so that "10:00"
        // is really 10 o'clock whatever the UTC offset. At most maxTicks marks.
        // "Network changes of this PC (cyan lines): 18:03 Wi-Fi: 192.168.0.12 -> 10.0.0.5; 18:40 ... (+2 more)". Null when none.
        public static string? DescribeNetwork(IReadOnlyList<NetworkEvent> events, DateTimeOffset from, DateTimeOffset to, int shown = 2)
        {
            var inRange = events.Where(n => n.Time >= from && n.Time <= to).OrderBy(n => n.Time).ToList();
            if (inRange.Count == 0) return null;

            var c = CultureInfo.CurrentCulture;
            string text = string.Join("; ", inRange.Take(shown).Select(n => n.Time.ToLocalTime().ToString("t", c) + " " + n.Text));
            return Loc.T("timeline.network") + text + (inRange.Count > shown ? Loc.T("timeline.more", inRange.Count - shown) : "");
        }

        // The date of a mark as the user writes it ("Oct 3", "3 oct.", "10月3日"): the month is a name, so that 03-10 is never read
        // as the 3rd of October by one person and the 10th of March by another.
        internal static string MonthDay(CultureInfo c) => c.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");

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
            if (withDate) format = MonthDay(CultureInfo.CurrentCulture) + " " + format;

            // Whole days are counted on the local calendar: a day across a daylight-saving change lasts 23 or 25 hours,
            // and "midnight + 86400 s" would land at 23:00 or 01:00.
            bool days = step % 86400 == 0;
            long stepDays = step / 86400;
            var ticks = new List<(DateTimeOffset, string)>();
            for (double k = days ? (local.TimeOfDay == TimeSpan.Zero ? 0 : 1) : firstIndex; ; k++)
            {
                DateTimeOffset t;
                if (days)
                {
                    var day = local.Date.AddDays(k * stepDays);   // unspecified kind: a calendar date, no offset yet
                    t = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
                }
                else
                {
                    // The instant is exact; what is shown is that instant on the local clock of THAT moment, not with the
                    // offset the session started with.
                    t = (origin + TimeSpan.FromSeconds(k * step)).ToLocalTime();
                }

                if (t > to) break;
                ticks.Add((t, t.ToString(format, CultureInfo.CurrentCulture)));
            }

            return ticks;
        }
    }

    internal static class TimelineLayout
    {
        public static TimelineShapes Build(TimelineData d, IReadOnlyList<Incident> incidents, int width, int plotHeight,
                                           IReadOnlyList<NetworkEvent>? networkEvents = null)
        {
            var bars = new List<(float, float, float)>();
            var runs = new List<IReadOnlyList<GraphPoint>>();
            var cells = new List<(float, float, float)>();
            var bands = new List<(float, float, IncidentKind)>();
            if (d.IsEmpty) return new TimelineShapes(bars, runs, cells, bands);

            float cell = Math.Max(1f, (float)width / d.Buckets.Count);
            float Y(double v) => (float)(plotHeight - 1 - (plotHeight - 1.0) * Math.Min(v, d.TopMs) / d.TopMs);   // above the scale = the top edge
            var clipped = new List<float>();
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
                if (b.MaxMs!.Value > d.TopMs) clipped.Add(x + cell / 2);
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
                if (x1 - x0 < 2) x0 = Math.Max(0, x1 - 2);   // at the right edge there is no room to the right: widen to the left
                bands.Add((x0, x1, i.Kind));
            }

            // A change at the very edge is still a line (clamped inside the plot); one outside the time range is not drawn.
            var markers = (networkEvents ?? Array.Empty<NetworkEvent>())
                .Where(n => n.Time >= d.From && n.Time <= d.To)
                .Select(n => Math.Clamp(X(n.Time), 0f, width - 1f)).ToList();

            return new TimelineShapes(bars, runs, cells, bands, markers, clipped);
        }
    }
}
