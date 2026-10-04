using System.Globalization;

namespace PingTool
{
    // How the host behaves RECENTLY: the last Window pings, not the whole session. A spike from an
    // hour ago stops weighing on the figures, and an improvement shows up within a minute.
    //
    // Percentiles use the nearest-rank method on the successful pings only (a lost ping has no
    // latency; loss is reported on its own). They need MinForPercentiles successes: with fewer,
    // p95 and p99 would just be the maximum under another name.
    // Jitter is the mean absolute difference between two pings that follow each other and both answered:
    // a loss breaks the chain (the same definition as the session figure).
    internal readonly record struct RecentStats(int Samples, int Lost, double? P50, double? P95, double? P99, double? Jitter)
    {
        public const int Window = 60;
        public const int MinForPercentiles = 10;

        public static RecentStats From(IReadOnlyCollection<long> history, int window = Window)
        {
            var recent = history.Skip(Math.Max(0, history.Count - window)).ToList();
            var ok = recent.Where(p => p >= 0).ToList();

            // Only between two pings that follow each other and both answered (same definition as the session
            // figure): a loss breaks the chain.
            double? jitter = null;
            double sum = 0;
            int pairs = 0;
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i] < 0 || recent[i - 1] < 0) continue;
                sum += Math.Abs(recent[i] - recent[i - 1]);
                pairs++;
            }

            if (pairs > 0) jitter = sum / pairs;

            if (ok.Count < MinForPercentiles)
                return new RecentStats(recent.Count, recent.Count - ok.Count, null, null, null, jitter);

            ok.Sort();
            return new RecentStats(recent.Count, recent.Count - ok.Count,
                Rank(ok, 50), Rank(ok, 95), Rank(ok, 99), jitter);
        }

        // Nearest rank: the ceil(p/100 * n)-th smallest value.
        private static double Rank(List<long> sorted, int percent)
        {
            int rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
            return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
        }

        // Two short lines (one line is wider than the window):
        //   "Last 60: p50 12 / p95 40 / p99 55 ms"
        //   "Recent jitter 3.1 ms | loss 5% (3/60)"   (numbers in the user's culture)
        public string Describe()
        {
            var c = CultureInfo.CurrentCulture;
            string N(double? v) => v is null ? "-" : v.Value.ToString("0.#", c);

            if (Samples == 0) return "Last pings: waiting for data";

            // The loss line is there even without percentiles: a host that answers nothing is exactly when it matters.
            string first = P50 is null
                ? string.Format(c, "Last {0}: need {1} replies for percentiles", Samples, MinForPercentiles)
                : string.Format(c, "Last {0}: p50 {1} / p95 {2} / p99 {3} ms", Samples, N(P50), N(P95), N(P99));
            return first + string.Format(c, "\nRecent jitter {0} ms | loss {1}% ({2}/{3})", N(Jitter), N(100.0 * Lost / Samples), Lost, Samples);
        }
    }
}
