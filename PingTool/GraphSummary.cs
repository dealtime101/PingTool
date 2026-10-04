using System.Drawing;
using System.Globalization;

namespace PingTool
{
    // One line of the latency graph: a target, its colour and the last pings (negative = lost).
    internal sealed record GraphSeries(string Name, Color Color, IReadOnlyCollection<long> Samples);

    // What a screen reader says about the graph, which is only a picture: for each target the last answer, the 95th percentile of
    // the recent pings and how many were lost - the same figures the eye reads off the drawing.
    internal static class GraphSummary
    {
        public const int MaxTargets = 5;

        public static string Describe(IReadOnlyList<GraphSeries> series)
        {
            if (series.Count == 0) return "No data yet: start pinging.";

            var c = CultureInfo.CurrentCulture;
            var parts = series.Take(MaxTargets).Select(s => One(s, c)).ToList();
            string text = string.Join("; ", parts);
            return series.Count > MaxTargets ? text + $"; and {(series.Count - MaxTargets).ToString(c)} more" : text;
        }

        private static string One(GraphSeries s, CultureInfo c)
        {
            string who = s.Name.Length == 0 ? "Selected target" : s.Name;
            if (s.Samples.Count == 0) return who + ": no ping yet";

            long last = s.Samples.Last();
            var recent = RecentStats.From(s.Samples);
            string lastText = last < 0 ? "last ping lost" : "last " + last.ToString(c) + " ms";
            string p95 = recent.P95 is double p ? ", 95th percentile " + p.ToString("0.#", c) + " ms" : "";
            return $"{who}: {lastText}{p95}, {recent.Lost.ToString(c)} of the last {recent.Samples.ToString(c)} pings lost";
        }
    }
}
