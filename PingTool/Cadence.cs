namespace PingTool
{
    // How long to wait after a probe so that probes START one interval apart. Waiting a full interval
    // after the answer made the real rhythm "interval + reply time" - and "interval + timeout" for a host
    // that stops answering, which is sampled less often exactly when it matters.
    internal static class Cadence
    {
        // elapsedMs = how long the probe took. A probe slower than the interval leaves no wait at all
        // (the next one starts at once): the rhythm can only be as fast as the probe itself.
        public static int WaitMs(int intervalMs, long elapsedMs)
        {
            if (intervalMs <= 0) return 0;
            if (elapsedMs <= 0) return intervalMs;   // a clock that went backwards cannot lengthen the wait
            return elapsedMs >= intervalMs ? 0 : intervalMs - (int)elapsedMs;
        }
    }
}
