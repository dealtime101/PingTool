namespace PingTool
{
    // One session = one Start..Stop. Jitter is the mean absolute difference between two round trips
    // that FOLLOW EACH OTHER, both successful (unsmoothed): a lost ping breaks the chain, so a reply
    // is never compared with one from before the loss.
    internal sealed class SessionStats
    {
        private long sum, count, last = -1;
        private double jitterSum;
        private long jitterCount;

        public long Sent { get; private set; }
        public long Lost { get; private set; }
        public double? Min { get; private set; }
        public double? Max { get; private set; }
        public double? Avg => count == 0 ? null : (double)sum / count;
        public double? Jitter => jitterCount == 0 ? null : jitterSum / jitterCount;
        // null before the first ping: 0 % would read as a measurement of "no loss" when nothing was measured.
        public double? LossPercent => Sent == 0 ? null : 100.0 * Lost / Sent;

        public void Reset()
        {
            sum = count = jitterCount = 0;
            last = -1;
            jitterSum = 0;
            Sent = Lost = 0;
            Min = Max = null;
            hours.Clear();
        }

        // Pings sent and lost per clock hour (for the pings that came with their time), oldest first.
        private readonly SortedDictionary<DateTime, (long Sent, long Lost)> hours = new();
        public IReadOnlyList<HourCell> Hours => hours.Select(kv => new HourCell(kv.Key, kv.Value.Sent, kv.Value.Lost)).ToList();

        // ping < 0 means timeout / failure.
        // at = when the ping was made: it feeds the hour-by-day figures of the report (no time given = not counted there).
        public void Add(long ping, DateTimeOffset? at = null)
        {
            Sent++;
            if (at is DateTimeOffset when)
            {
                var local = when.ToLocalTime();
                var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
                hours.TryGetValue(hour, out var h);
                hours[hour] = (h.Sent + 1, h.Lost + (ping < 0 ? 1 : 0));
            }

            // A loss breaks the chain: the next reply is not compared with one from before the gap, which may
            // be minutes old (an outage then a slower route would read as a huge jitter that is not one).
            if (ping < 0) { Lost++; last = -1; return; }

            sum += ping;
            count++;
            Min = Min is null ? ping : Math.Min(Min.Value, ping);
            Max = Max is null ? ping : Math.Max(Max.Value, ping);
            if (last >= 0) { jitterSum += Math.Abs(ping - last); jitterCount++; }
            last = ping;
        }
    }
}
