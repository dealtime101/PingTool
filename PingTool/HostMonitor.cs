namespace PingTool
{
    internal enum HostChange { None, Down, Up, Degraded, Recovered }

    // Watches one host's successive pings and says when its state CHANGES.
    //
    //   Down      : DownAfter consecutive failures (one lost packet is noise).
    //   Up        : first success after Down.
    //   Degraded  : the last WindowSize pings, window FULL, show loss >= lossPercent
    //               or an average latency >= latencyMs. A single spike cannot do it:
    //               one 500 ms ping among nine 20 ms ones averages 68 ms.
    //   Recovered : the window is back under 10 % loss and 80 % of the latency limit
    //               (the margin keeps a host hovering at the limit from flapping).
    //
    // Coming back Up restarts the window, so the failures that caused the outage
    // cannot read as "degraded" the moment the host answers again.
    internal sealed class HostMonitor
    {
        public const int DownAfter = 3;
        public const int WindowSize = 10;
        public const double RecoverLossPercent = 10;
        public const double RecoverLatencyFactor = 0.8;

        private enum State { Up, Degraded, Down }

        private readonly double latencyMs;
        private readonly double lossPercent;
        private readonly Queue<long> window = new();
        private State state;
        private int failures;

        public HostMonitor(int latencyMs = 150, int lossPercent = 30)
        {
            this.latencyMs = Math.Max(1, latencyMs);
            this.lossPercent = Math.Clamp(lossPercent, 1, 100);
        }

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
            state = State.Up;
            failures = 0;
            window.Clear();
        }

        // ping < 0 means failure.
        public HostChange Update(long ping)
        {
            if (ping >= 0)
            {
                failures = 0;
                if (state == State.Down)
                {
                    state = State.Up;
                    window.Clear();
                    window.Enqueue(ping);
                    return HostChange.Up;
                }
            }
            else
            {
                failures++;
                if (state == State.Down) return HostChange.None;
                if (failures >= DownAfter)
                {
                    state = State.Down;
                    return HostChange.Down;
                }
            }

            window.Enqueue(ping);
            while (window.Count > WindowSize) window.Dequeue();
            if (window.Count < WindowSize) return HostChange.None;

            double loss = WindowLossPercent;
            double? avg = WindowAvgMs;

            if (state == State.Up && (loss >= lossPercent || avg >= latencyMs))
            {
                state = State.Degraded;
                return HostChange.Degraded;
            }

            if (state == State.Degraded && loss <= RecoverLossPercent
                && avg is double a && a <= latencyMs * RecoverLatencyFactor)
            {
                state = State.Up;
                return HostChange.Recovered;
            }

            return HostChange.None;
        }
    }
}
