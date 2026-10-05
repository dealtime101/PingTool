using System.Drawing;
using System.Globalization;

namespace PingTool
{
    // One line of the latency graph: a target, its colour and the last pings (negative = lost).
    // Samples are OLDEST FIRST, the newest last: the contract of every reader (the line drawn, the summary read aloud, the percentiles).
    // The collection type cannot say it; the two producers hand over the queue of a session (HostSession.History), which is in that order.
    // Times = when each sample was made (same order and count), when the graph should place the samples in time and not by rank.
    internal sealed record GraphSeries(string Name, Color Color, IReadOnlyCollection<long> Samples, IReadOnlyCollection<DateTimeOffset>? Times = null);

    // What a screen reader says about the graph, which is only a picture: for each target the last answer, the 95th percentile of
    // the recent pings and how many were lost - the same figures the eye reads off the drawing.
    internal static class GraphSummary
    {
        public static string Describe(IReadOnlyList<GraphSeries> series)
        {
            if (series.Count == 0) return Loc.T("graph.empty");

            var c = CultureInfo.CurrentCulture;
            // Every target, by its full name: a reader asks for this on purpose and cannot see which ones "and N more" would be.
            bool alone = series.Count == 1;   // one target on screen, named or not: its curve is told too
            return string.Join("; ", series.Select(s => One(s, c, alone)));
        }

        private static string One(GraphSeries s, CultureInfo c, bool alone)
        {
            string who = s.Name.Length == 0 ? Loc.T("graph.selected") : s.Name;
            if (s.Samples.Count == 0) return Loc.T("graph.noping", who);

            long last = s.Samples.Last();
            var recent = RecentStats.From(s.Samples);
            string lastText = last < 0 ? Loc.T("graph.last.lost") : Loc.T("graph.last", last.ToString(c));
            string p95 = recent.P95 is double p ? Loc.T("graph.p95", p.ToString("0.#", c)) : "";
            // The one target on screen also gets its curve in words: the window in quarters, oldest first (a compared set would be too long).
            string trend = alone ? Trend(s.Samples, c) : "";
            // "ping" or "pings" with the count: a screen reader says this sentence aloud, and the first one it reads at the start of a run is "of the last 1 ping".
            // The words come from the language of the window (Loc), the numbers from the regional format.
            return Loc.T(recent.Samples == 1 ? "graph.line.one" : "graph.line.many", who, lastText, p95, recent.Lost.ToString(c), recent.Samples.ToString(c), trend);
        }

        public const int TrendParts = 4;

        // "; over the window, oldest first: 21 ms 0 lost / 22 ms 0 lost / 95 ms 3 lost / 40 ms 1 lost": what the line of the graph shows.
        // Nothing when there are too few pings to cut into at least two parts of two.
        internal static string Trend(IReadOnlyCollection<long> samples, CultureInfo c)
        {
            var list = samples.ToList();
            int parts = Math.Min(TrendParts, list.Count / 2);
            if (parts < 2) return "";

            var texts = new List<string>();
            for (int p = 0; p < parts; p++)
            {
                int from = p * list.Count / parts, to = (p + 1) * list.Count / parts;
                var slice = list.Skip(from).Take(to - from).ToList();
                var replies = slice.Where(v => v >= 0).ToList();
                string avg = replies.Count == 0 ? Loc.T("graph.noreply") : Loc.T("graph.ms", replies.Average().ToString("0.#", c));
                texts.Add(Loc.T("graph.trend.part", avg, Loc.Lost(slice.Count - replies.Count)));
            }

            return Loc.T("graph.trend", string.Join(" / ", texts));
        }
    }
}
