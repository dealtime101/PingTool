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
        // The newest ping is on the right edge; one ping = one column of width/(maxSamples-1).
        // 0 ms is the bottom line, `top` ms the top line.
        public static GraphShapes Build(IReadOnlyCollection<long> samples, int width, int height, long top, int maxSamples)
        {
            var lines = new List<(GraphPoint, GraphPoint)>();
            var dots = new List<GraphPoint>();
            var losses = new List<float>();

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
