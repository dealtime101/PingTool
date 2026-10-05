namespace PingTool
{
    internal readonly record struct GraphPoint(float X, float Y);

    // What to draw for one series, as plain geometry:
    //   Lines     a segment between two consecutive successful pings
    //   Dots      a successful ping that has no neighbour to be joined to (the first one, or one
    //             between two losses): without a dot it would be a reply that never shows
    //   LossXs    the column of each lost ping (drawn as a tick on the baseline)
    internal sealed record GraphShapes(
        IReadOnlyList<(GraphPoint From, GraphPoint To)> Lines,
        IReadOnlyList<GraphPoint> Dots,
        IReadOnlyList<float> LossXs);

    internal static class GraphLayout
    {
        public const int AlonePixels = 7;      // length of a lost-ping tick when one host is shown
        public const int SharedPixels = 12;    // height of the band shared by all hosts when several are compared

        // The legend has room for `max` lines: which ones are shown when `total` hosts are compared and the user has scrolled by `offset`
        // (the offset is held to what exists, so a wheel that keeps turning, or hosts removed meanwhile, never leaves an empty legend).
        public static (int First, int Rows) LegendWindow(int total, int offset, int max)
        {
            int rows = Math.Clamp(Math.Min(total, max), 0, Math.Max(0, total));
            return (Math.Clamp(offset, 0, Math.Max(0, total - rows)), rows);
        }

        // Where the tick of a lost ping is drawn (from the baseline upwards), as (y of the bottom end, y of the top end).
        // One host: as it always was. Several compared hosts end at the same "now": drawn in the same place, the last would hide
        // the others, so each host gets its OWN band of the shared strip above the baseline (host 0 lowest), the bands side by
        // side and never overlapping, so that the colour says whose loss it is.
        public static (float Bottom, float Top) LossTick(int index, int count, int height, bool compare)
        {
            if (!compare || count <= 1) return (height - 1, height - 1 - AlonePixels);

            // The tick is as long as its band, so never less than one pixel: with 7 hosts or more the band is 1 and a tick from
            // "bottom" to "bottom - 1 + 1" was a line from a point to itself, which the pen (flat ends) does not draw at all. The bands
            // follow each other without a gap or an overlap (the top of one is the bottom of the next).
            int band = Math.Clamp(SharedPixels / count, 1, 4);
            float bottom = Math.Max(0, height - 1 - index * band);
            return (bottom, Math.Max(0, bottom - band));
        }

        // The newest ping is on the right edge; one ping = one column of width/(maxSamples-1).
        // 0 ms is the bottom line, `top` ms the top line.
        public static GraphShapes Build(IReadOnlyCollection<long> samples, int width, int height, long top, int maxSamples)
        {
            var lines = new List<(GraphPoint, GraphPoint)>();
            var dots = new List<GraphPoint>();
            var losses = new List<float>();

            top = Math.Max(1, top);   // Y divides by it: 0 gave NaN and infinity (the form passes at least 50, but this is public)
            float step = (width - 1f) / Math.Max(1, maxSamples - 1);
            float x0 = width - 1 - (samples.Count - 1) * step;
            float Y(long v) => height - 1 - (height - 1f) * v / top;

            GraphPoint? prev = null;
            bool joined = false;   // has `prev` been joined by a line to the ping before it?
            int i = 0;
            foreach (var v in samples)
            {
                float x = x0 + i++ * step;
                if (v < 0)
                {
                    if (prev is GraphPoint alone && !joined) dots.Add(alone);
                    losses.Add(x);
                    prev = null;
                    continue;
                }

                var p = new GraphPoint(x, Y(v));
                if (prev is GraphPoint q)
                {
                    lines.Add((q, p));
                    joined = true;
                }
                else
                {
                    joined = false;
                }

                prev = p;
            }

            if (prev is GraphPoint last && !joined) dots.Add(last);
            return new GraphShapes(lines, dots, losses);
        }
    }
}
