using System.Globalization;

namespace PingTool
{
    // One column of the timeline: everything that happened to the host in that slice of time.
    internal sealed record TimelineBucket(DateTimeOffset Start, long Sent, long Lost, double? MinMs, double? AvgMs, double? MaxMs);

    internal sealed record TimelineData(string Host, DateTimeOffset From, DateTimeOffset To, TimeSpan BucketSpan,
        IReadOnlyList<TimelineBucket> Buckets, long TopMs, long Sent, long Lost, long PeakMs = 0, DateTimeOffset? Last = null)
    {
        // To is the right edge of the DRAWING (a single instant is given one second of width); Last is the time of the last ping
        // really observed, and what the text says about the session ends there.
        public DateTimeOffset ObservedTo => Last ?? To;
        public TimeSpan Observed => ObservedTo - From;
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
            var last = to;
            if (to - from < TimeSpan.FromSeconds(1)) to = from + TimeSpan.FromSeconds(1);   // a single instant still needs a width (to draw)

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
            return new TimelineData(host, from, to, TimeSpan.FromTicks(ticks), list, top, mine.Count, mine.Count(e => e.RttMs is null), peak, last);
        }

        // The index to select when the window opens: the host that was selected in the main window, else the first; -1 when there is none.
        internal static int InitialHost(IReadOnlyList<string> hosts, string? selected)
        {
            if (hosts.Count == 0) return -1;
            for (int i = 0; i < hosts.Count; i++) if (hosts[i] == selected) return i;
            return 0;
        }

        public const int MaxPeriods = 8;

        // The session read from left to right, as a screen reader needs it (the picture shows this at a glance): up to MaxPeriods
        // consecutive periods, each with its start, the average reply and the share of pings lost. Null when there is nothing to
        // tell apart (one column).
        internal static string? DescribePeriods(TimelineData d, int periods = MaxPeriods)
        {
            if (d.IsEmpty || d.Buckets.Count < 2) return null;

            var c = CultureInfo.CurrentCulture;
            int n = Math.Min(Math.Max(1, periods), d.Buckets.Count);
            var parts = new List<string>();
            for (int p = 0; p < n; p++)
            {
                int from = p * d.Buckets.Count / n, to = (p + 1) * d.Buckets.Count / n;
                var group = d.Buckets.Skip(from).Take(to - from).ToList();
                long sent = group.Sum(b => b.Sent), lost = group.Sum(b => b.Lost);
                if (sent == 0) continue;   // no ping in that stretch of the session

                long replies = sent - lost;
                string avg = replies == 0 ? "-" : (group.Where(b => b.AvgMs is not null).Sum(b => b.AvgMs!.Value * (b.Sent - b.Lost)) / replies).ToString("0.#", c);
                parts.Add(Loc.T("timeline.period", group[0].Start.ToLocalTime().ToString("g", c), avg, (100.0 * lost / sent).ToString("0.#", c)));
            }

            return parts.Count == 0 ? null : Loc.T("timeline.periods", string.Join("; ", parts));
        }

        // What a screen reader says about the chart: the summary, then the incidents of THIS host in order (the first few), then the
        // network changes - everything the stripes and the lines say to the eye.
        internal static string Accessible(TimelineData d, IReadOnlyList<Incident> incidents, IReadOnlyList<NetworkEvent> network, int shown = 5)
        {
            string text = Describe(d);
            if (d.IsEmpty) return text;

            var c = CultureInfo.CurrentCulture;
            string? periods = DescribePeriods(d);
            if (periods is not null) text += "\n" + periods;
            var mine = incidents.Where(i => i.Host == d.Host).OrderBy(i => i.Start).ToList();
            if (mine.Count == 0) text += "\n" + Loc.T("timeline.incidents.none");
            else
            {
                string One(Incident i)
                {
                    string start = i.Start.ToLocalTime().ToString("G", c);
                    string length = IncidentLog.FormatDuration(i.Duration(d.To));
                    string how = i.End is null ? Loc.T("timeline.ongoing", length) : length;
                    return i.Kind == IncidentKind.Outage ? Loc.T("timeline.outage", start, how) : Loc.T("timeline.slowdown", start, how);
                }

                string list = string.Join("; ", mine.Take(shown).Select(One));
                text += "\n" + Loc.T("timeline.incidents", list + (mine.Count > shown ? Loc.T("timeline.more", mine.Count - shown) : ""));
            }

            string? net = DescribeNetwork(network, d.From, d.To);
            return net is null ? text : text + "\n" + net;
        }

        public const int MinPlotHeight = 20;

        // What to write instead of the chart, or null when it can be drawn: "no data" only when there is none, and a request to
        // enlarge the window when there is data but no room (it used to say "No data yet" there, which is false).
        internal static string? NothingToDraw(bool noData, int plotHeight) =>
            noData ? Loc.T("timeline.empty") : plotHeight < MinPlotHeight ? Loc.T("timeline.small") : null;

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
                when(d.From), when(d.ObservedTo), IncidentLog.FormatDuration(d.Observed), d.Sent, d.Lost, num(100.0 * d.Lost / d.Sent));

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

        // "Network changes of this PC (cyan lines): 18:03 Wi-Fi: 192.168.0.12 -> 10.0.0.5; 18:40 ... (+2 more)". Null when none.
        public static string? DescribeNetwork(IReadOnlyList<NetworkEvent> events, DateTimeOffset from, DateTimeOffset to, int shown = 2)
        {
            var inRange = events.Where(n => n.Time >= from && n.Time <= to).OrderBy(n => n.Time).ToList();
            if (inRange.Count == 0) return null;

            var c = CultureInfo.CurrentCulture;
            // A session over midnight: "18:03" alone does not say which day, so the date comes with the time (as on the axis).
            bool severalDays = from.ToLocalTime().Date != to.ToLocalTime().Date;
            string format = severalDays ? MonthDay(c) + " " + c.DateTimeFormat.ShortTimePattern : "t";
            string text = string.Join("; ", inRange.Take(shown).Select(n => n.Time.ToLocalTime().ToString(format, c) + " " + n.Text));
            return Loc.T("timeline.network") + text + (inRange.Count > shown ? Loc.T("timeline.more", inRange.Count - shown) : "");
        }

        // The date of a mark as the user writes it ("Oct 3", "3 oct.", "10月3日"): the month is a name, so that 03-10 is never read
        // as the 3rd of October by one person and the 10th of March by another.
        internal static string MonthDay(CultureInfo c) => c.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");

        // seconds: 1 s .. 1 min .. 1 h .. 1 day .. 1 week .. 1 month .. 1 year
        private static readonly long[] TickSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400,
            172800, 604800, 1209600, 2592000, 7776000, 31536000 };

        // Round times for the time axis (every 5 min, every hour...), in LOCAL time so that "10:00"
        // is really 10 o'clock whatever the UTC offset. At most maxTicks marks (at least 1 is allowed: a zero or negative limit means 1).
        public static IReadOnlyList<(DateTimeOffset Time, string Label)> Ticks(DateTimeOffset from, DateTimeOffset to, int maxTicks)
        {
            int max = Math.Max(1, maxTicks);
            double span = Math.Max(1, (to - from).TotalSeconds);
            long step = TickSteps.FirstOrDefault(s => span / s <= max);
            // longer than any listed step allows: whole days, as many as needed
            if (step == 0) step = (long)Math.Ceiling(span / max / 86400) * 86400;

            // The step above counts INTERVALS, and both ends carry a mark when they fall on the step: one mark more than the limit can
            // come out (10 s, limit 2: marks at 0, 5 and 10). A coarser step until what is really produced fits.
            while (true)
            {
                var ticks = TicksAt(from, to, step);
                if (ticks.Count <= max) return ticks;
                long next = TickSteps.FirstOrDefault(s => s > step);
                step = next != 0 ? next : step * 2;   // past the listed steps the step is whole days: doubling keeps it so
            }
        }

        private static List<(DateTimeOffset Time, string Label)> TicksAt(DateTimeOffset from, DateTimeOffset to, long step)
        {
            double span = Math.Max(1, (to - from).TotalSeconds);
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
        // Where a time label starts: centred under its mark, but held inside [leftEdge, rightEdge] so that the first and the last are
        // not cut by the edge of the control (the right margin is a few pixels, a label is tens). A label wider than the room starts at
        // the left edge.
        internal static float LabelStart(float tickX, float labelWidth, float leftEdge, float rightEdge) =>
            Math.Clamp(tickX - labelWidth / 2, leftEdge, Math.Max(leftEdge, rightEdge - labelWidth));

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
