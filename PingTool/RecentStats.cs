using System.Globalization;

namespace PingTool
{
    // How the host behaves RECENTLY: the last Window pings, not the whole session. A spike from an
    // hour ago stops weighing on the figures, and an improvement shows up within a minute.
    //
    // Percentiles use the nearest-rank method on the successful pings only (a lost ping has no
    // latency; loss is reported on its own). Each needs enough successes (MinForPercentiles, MinForP95, MinForP99): with fewer,
    // that percentile would just be the maximum under another name, so it is not given.
    // Jitter is the mean absolute difference between two pings that follow each other and both answered:
    // a loss breaks the chain (the same definition as the session figure).
    // Mean is the mean reply time of the window; Mos is the voice quality estimated from it, the jitter and the loss.
    internal readonly record struct RecentStats(int Samples, int Lost, double? P50, double? P95, double? P99, double? Jitter, double? Mean = null)
    {
        // MOS 1..5 from the simplified ITU-T G.107 E-model (Cole and Rosenbluth): effective one-way latency =
        // mean + 2 x jitter + 10 ms, R = 93.2 minus the delay term minus 2.5 per percent of loss. Given only once there
        // are MinForPercentiles replies, like the percentiles: fewer would be a guess about a call nobody made.
        public double? Mos => Mean is double mean && Samples > 0 ? MosOf(mean, Jitter ?? 0, 100.0 * Lost / Samples) : null;

        internal static double MosOf(double meanMs, double jitterMs, double lossPercent)
        {
            double latency = meanMs + 2 * jitterMs + 10;
            double r = (latency < 160 ? 93.2 - latency / 40 : 93.2 - (latency - 120) / 10) - 2.5 * lossPercent;
            if (r <= 0) return 1;
            if (r >= 100) return 4.5;
            // The polynomial dips under 1 for a very small R (about 1 to 6): a MOS has a floor.
            return Math.Max(1, 1 + 0.035 * r + 7e-6 * r * (r - 60) * (100 - r));
        }

        // The word that goes with a MOS (the usual ITU bands): a non-specialist reads "poor", not 3.2.
        public static string Verdict(double mos) => mos >= 4.0 ? "good" : mos >= 3.6 ? "fair" : mos >= 3.1 ? "poor" : "bad";

        public const int Window = 60;

        // Replies needed before a percentile says more than the maximum (nearest rank): p50 from 10 (a median of fewer is too loose),
        // p95 from 20 (below that the 95th is the largest reply), p99 from 100 (below that it is the largest reply: with the 60 pings
        // of the window it never appears).
        public const int MinForPercentiles = 10;
        public const int MinForP95 = 20;
        public const int MinForP99 = 100;

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
                Rank(ok, 50), ok.Count >= MinForP95 ? Rank(ok, 95) : null, ok.Count >= MinForP99 ? Rank(ok, 99) : null, jitter, ok.Average());
        }

        // Nearest rank: the ceil(p/100 * n)-th smallest value. In whole numbers: 7 / 100.0 * 100 is 7.000000000000001 in floating
        // point, whose ceiling is 8, one rank too far (the percents in use, 50, 95 and 99, happen to be exact, but the method takes any).
        internal static double Rank(List<long> sorted, int percent)
        {
            int rank = (int)(((long)percent * sorted.Count + 99) / 100);
            return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
        }

        // Two short lines (one line is wider than the window), as the window of 60 pings produces them:
        //   "Last 60: p50 12 / p95 40 ms"
        //   "Recent jitter 3.1 ms | loss 5% (3/60) | voice good (4.3)"   (numbers in the user's culture)
        // The p99 ("... / p99 55 ms") only shows with at least MinForP99 replies, which a window of 60 never holds: it appears when
        // From is given a bigger window.
        public string Describe()
        {
            var c = CultureInfo.CurrentCulture;
            string N(double? v) => v is null ? "-" : v.Value.ToString("0.#", c);

            if (Samples == 0) return Loc.T("stats.waiting");

            // The words follow the display language (Loc), the numbers the regional format, as everywhere else in the program.
            // The loss line is there even without percentiles: a host that answers nothing is exactly when it matters.
            string first = P50 is null
                ? Loc.T("stats.need", Samples, MinForPercentiles)
                : Loc.T("stats.last", Samples, N(P50))
                    + (P95 is null ? "" : " / p95 " + N(P95)) + (P99 is null ? "" : " / p99 " + N(P99)) + " ms"
                    + (P95 is null ? Loc.T("stats.p95needs", MinForP95) : "");
            string voice = Mos is double mos ? Loc.T("stats.voice", Loc.T("stats.voice." + Verdict(mos)), mos.ToString("0.0", c)) : "";
            return first + "\n" + Loc.T("stats.jitter", N(Jitter), N(100.0 * Lost / Samples), Lost, Samples) + voice;
        }
    }
}
