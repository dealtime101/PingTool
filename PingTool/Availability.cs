using System.Globalization;

namespace PingTool
{
    // One clock hour of one host: the pings sent and lost in it. Hour = the start of the hour on the PC's wall clock.
    internal sealed record HourCell(DateTime Hour, long Sent, long Lost)
    {
        public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    }

    // The figures a provider's support asks for first: how much of the time the target was reachable, how many cuts, how long.
    internal sealed record AvailabilityRow(string Host, double? AvailabilityPercent, int Outages, TimeSpan TotalDown, TimeSpan? Mean, TimeSpan? Longest);

    internal static class Availability
    {
        public const int MaxGridDays = 31;

        // Availability = the share of the monitoring period NOT spent in an outage (an outage runs from the first failed ping
        // to the first success after it; a slowdown is not counted as down). An outage still going on counts up to `end`.
        public static AvailabilityRow For(string host, IEnumerable<Incident> incidents, DateTimeOffset from, DateTimeOffset end)
        {
            var outages = incidents.Where(i => i.Host == host && i.Kind == IncidentKind.Outage).ToList();
            var durations = outages.Select(i => Clamp(i.Duration(end))).ToList();
            var down = durations.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            var period = end - from;

            double? percent = period <= TimeSpan.Zero ? null : Math.Clamp(100.0 * (1 - down.Ticks / (double)period.Ticks), 0, 100);
            return new AvailabilityRow(host, percent, outages.Count, down,
                durations.Count == 0 ? null : TimeSpan.FromTicks(down.Ticks / durations.Count),
                durations.Count == 0 ? null : durations.Max());
        }

        private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        // The colour class of one cell of the hour-by-day grid: none = no ping that hour, then by the share of lost pings.
        public static string LossClass(HourCell? cell) => cell is null || cell.Sent == 0 ? "hn"
            : cell.Lost == 0 ? "h0"
            : cell.LossPercent <= 5 ? "h1"
            : cell.LossPercent <= 30 ? "h2"
            : "h3";

        // One row per day (the most recent MaxGridDays), 24 cells each (null = no ping that hour).
        public static List<(DateTime Day, HourCell?[] Hours)> Grid(IEnumerable<HourCell> cells)
        {
            var byDay = cells.GroupBy(c => c.Hour.Date).OrderBy(g => g.Key).TakeLast(MaxGridDays);
            var rows = new List<(DateTime, HourCell?[])>();
            foreach (var day in byDay)
            {
                var hours = new HourCell?[24];
                foreach (var cell in day)
                {
                    var previous = hours[cell.Hour.Hour];
                    // The repeated hour of a clock change is one cell: its pings add up.
                    hours[cell.Hour.Hour] = previous is null ? cell : new HourCell(cell.Hour, previous.Sent + cell.Sent, previous.Lost + cell.Lost);
                }

                rows.Add((day.Key, hours));
            }

            return rows;
        }

        public static string Title(DateTime day, int hour, HourCell? cell) =>
            cell is null || cell.Sent == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd} {hour:00}:00 - no ping")
                : string.Create(CultureInfo.InvariantCulture, $"{day:yyyy-MM-dd} {hour:00}:00 - {cell.LossPercent:0.#}% lost ({cell.Lost} of {cell.Sent})");
    }
}
