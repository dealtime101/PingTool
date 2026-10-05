namespace PingTool
{
    // Notice = something worth knowing that is not a change of state (a certificate about to expire): only ever raised by the
    // probe, never by HostMonitor.
    internal enum HostChange { None, Down, Up, Degraded, Recovered, Notice }

    internal enum HostState { Up, Degraded, Down }

    // Watches one host's successive pings and says when its state CHANGES.
    //
    //   Down      : downAfter consecutive failures, 3 by default (one lost packet is noise).
    //   Up        : first success after Down.
    //   Degraded  : the last WindowSize pings, window FULL, show loss >= lossPercent
    //               or an average latency >= latencyMs. A single spike cannot do it:
    //               one 500 ms ping among nine 20 ms ones averages 68 ms.
    //   Recovered : the window is back under 10 % loss (and under the loss limit, when that is lower than 10 %) and
    //               80 % of the latency limit (the margin keeps a host hovering at the limit from flapping).
    //
    // Coming back Up restarts the window, so the failures that caused the outage
    // cannot read as "degraded" the moment the host answers again.
    internal sealed class HostMonitor
    {
        public const int DefaultDownAfter = 3;
        public const int WindowSize = 10;
        public const double RecoverLossPercent = 10;
        public const double RecoverLatencyFactor = 0.8;

        private readonly double latencyMs;
        private readonly double lossPercent;
        private readonly int downAfter;
        private readonly Queue<long> window = new();
        private HostState state;
        private int failures;

        public HostState State => state;

        public const int DefaultLatencyMs = 150;
        public const int DefaultLossPercent = 30;

        public HostMonitor(int latencyMs = DefaultLatencyMs, int lossPercent = DefaultLossPercent, int downAfter = DefaultDownAfter)
        {
            this.latencyMs = Math.Max(1, latencyMs);
            this.lossPercent = Math.Clamp(lossPercent, 1, 100);
            this.downAfter = Math.Max(1, downAfter);
        }

        // The "slow" limit this host is judged by (its own, or the one in the box when it has none).
        public double LatencyMs => latencyMs;

        // The colour step of one ping against a slow limit: 0 under half of it, 1 up to it, 2 from it on (the same
        // comparison as the state above: the limit itself is already "degraded").
        public static int LatencyBand(long pingMs, double slowMs) => pingMs < slowMs / 2 ? 0 : pingMs < slowMs ? 1 : 2;

        public double WindowLossPercent =>
            window.Count == 0 ? 0 : 100.0 * window.Count(p => p < 0) / window.Count;

        public double? WindowAvgMs
        {
            get
            {
                var ok = window.Where(p => p >= 0).ToList();
                return ok.Count == 0 ? null : ok.Average();
            }
        }

        public void Reset()
        {
            state = HostState.Up;
            failures = 0;
            window.Clear();
        }

        // ping < 0 means failure.
        public HostChange Update(long ping)
        {
            if (ping >= 0)
            {
                failures = 0;
                if (state == HostState.Down)
                {
                    state = HostState.Up;
                    window.Clear();
                    window.Enqueue(ping);
                    return HostChange.Up;
                }
            }
            else
            {
                failures++;
                if (state == HostState.Down) return HostChange.None;
                if (failures >= downAfter)
                {
                    state = HostState.Down;
                    return HostChange.Down;
                }
            }

            window.Enqueue(ping);
            while (window.Count > WindowSize) window.Dequeue();
            if (window.Count < WindowSize) return HostChange.None;

            double loss = WindowLossPercent;
            double? avg = WindowAvgMs;

            if (state == HostState.Up && (loss >= lossPercent || avg >= latencyMs))
            {
                state = HostState.Degraded;
                return HostChange.Degraded;
            }

            // The way back must be STRICTLY under the way in: with a limit of 10 % or less, "back under 10 %" was already
            // true at the limit itself and the state flipped on every ping.
            if (state == HostState.Degraded && loss <= RecoverLossPercent && loss < lossPercent
                && avg is double a && a <= latencyMs * RecoverLatencyFactor)
            {
                state = HostState.Up;
                return HostChange.Recovered;
            }

            return HostChange.None;
        }
    }
}
