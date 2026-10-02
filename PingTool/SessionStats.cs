namespace PingTool
{
    // One session = one Start..Stop. Jitter is the mean absolute difference
    // between consecutive successful round trips (RFC 3550 style, unsmoothed).
    internal sealed class SessionStats
    {
        private long sum, count, last = -1;
        private double jitterSum;
        private long jitterCount;

        public int Sent { get; private set; }
        public int Lost { get; private set; }
        public double? Min { get; private set; }
        public double? Max { get; private set; }
        public double? Avg => count == 0 ? null : (double)sum / count;
        public double? Jitter => jitterCount == 0 ? null : jitterSum / jitterCount;
        public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;

        public void Reset()
        {
            sum = count = jitterCount = 0;
            last = -1;
            jitterSum = 0;
            Sent = Lost = 0;
            Min = Max = null;
        }

        // ping < 0 means timeout / failure.
        public void Add(long ping)
        {
            Sent++;
            if (ping < 0) { Lost++; return; }

            sum += ping;
            count++;
            Min = Min is null ? ping : Math.Min(Min.Value, ping);
            Max = Max is null ? ping : Math.Max(Max.Value, ping);
            if (last >= 0) { jitterSum += Math.Abs(ping - last); jitterCount++; }
            last = ping;
        }
    }
}
