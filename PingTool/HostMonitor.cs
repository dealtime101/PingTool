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
    //   Degraded  : ... or, when a "MOS below" limit is set, the voice quality (MOS) of that window is under it.
    //   Recovered : the window is back to at most 10 % loss (one lost ping in ten still counts; and STRICTLY under the loss limit, when
    //               that is lower than 10 %) and an average latency of at most 80 % of the latency limit (the margin keeps a host
    //               hovering at the limit from flapping).
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
        private readonly double mosBelow;
        // The way back from a MOS alarm must be above the way in, or a host at the limit would flap.
        public const double RecoverMosMargin = 0.1;
        private readonly Queue<long> window = new();
        private HostState state;
        private int failures;

        public HostState State => state;

        public const int DefaultLatencyMs = 150;
        public const int DefaultLossPercent = 30;

        public HostMonitor(int latencyMs = DefaultLatencyMs, int lossPercent = DefaultLossPercent, int downAfter = DefaultDownAfter, double mosBelow = 0)
        {
            this.mosBelow = Limits.ClampMos(mosBelow);
            this.latencyMs = Math.Max(1, latencyMs);
            this.lossPercent = Math.Clamp(lossPercent, 1, 100);
            this.downAfter = Math.Max(1, downAfter);
        }

        // The "slow" limit this host is judged by (its own, or the one in the box when it has none).
        public double LatencyMs => latencyMs;

        // The colour step of one ping against a slow limit: 0 under half of it, 1 up to it, 2 from it on (the same
        // comparison as the state above: the limit itself is already "degraded").
        public static int LatencyBand(long pingMs, double slowMs) => pingMs < slowMs / 2 ? 0 : pingMs < slowMs ? 1 : 2;

        // The "MOS below" limit this host is judged by (0 = none).
        public double MosBelow => mosBelow;

        // The voice quality of the window (same model as the "last 60" line), null with no reply in it.
        // Worked out here, not through RecentStats.From: that one gives no mean under 10 replies, and a window of 10 with two lost pings
        // has 8 - exactly the host this limit is for.
        public double? WindowMos
        {
            get
            {
                if (WindowAvgMs is not double mean) return null;
                double sum = 0;
                int pairs = 0;
                long? before = null;
                foreach (long ping in window)
                {
                    if (ping >= 0 && before is long b) { sum += Math.Abs(ping - b); pairs++; }
                    before = ping >= 0 ? ping : null;   // a loss breaks the chain, as for the jitter shown
                }

                return RecentStats.MosOf(mean, pairs > 0 ? sum / pairs : 0, WindowLossPercent);
            }
        }

        // True while the window is under the MOS limit (always false when there is none).
        public bool MosAlarm => mosBelow > 0 && WindowMos is double m && m < mosBelow;

        // What AlertMessage says when the MOS limit is what degraded the host; null otherwise.
        public (double Mos, double Limit)? Voice => MosAlarm && WindowMos is double m ? (m, mosBelow) : null;

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

        private void Push(long ping)
        {
            window.Enqueue(ping);
            while (window.Count > WindowSize) window.Dequeue();
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
                // The failures of a host that is down are in the window too (it was left frozen on what came before the outage, so the
                // loss read "0 %" for a host that answers nothing): the first reply after the outage starts it again (above).
                if (state == HostState.Down) { Push(ping); return HostChange.None; }
                if (failures >= downAfter)
                {
                    Push(ping);
                    state = HostState.Down;
                    return HostChange.Down;
                }
            }

            Push(ping);
            if (window.Count < WindowSize) return HostChange.None;

            double loss = WindowLossPercent;
            double? avg = WindowAvgMs;

            if (state == HostState.Up && (loss >= lossPercent || avg >= latencyMs || MosAlarm))
            {
                state = HostState.Degraded;
                return HostChange.Degraded;
            }

            // The way back must be STRICTLY under the way in: with a limit of 10 % or less, "back under 10 %" was already
            // true at the limit itself and the state flipped on every ping.
            if (state == HostState.Degraded && loss <= RecoverLossPercent && loss < lossPercent
                && avg is double a && a <= latencyMs * RecoverLatencyFactor
                && (mosBelow <= 0 || WindowMos is not double mos || mos >= mosBelow + RecoverMosMargin))
            {
                state = HostState.Up;
                return HostChange.Recovered;
            }

            return HostChange.None;
        }
    }
}
