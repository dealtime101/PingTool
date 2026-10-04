using System.Globalization;

namespace PingTool
{
    // The balloon text for a state change. Numbers use the user's culture, like
    // everything else on screen (the CSV is the one place that is invariant).
    internal static class AlertMessage
    {
        // outage = how long the host was silent, when known: "back up after 2 min 14 s".
        // The words are in Loc (English and French, by the display language of Windows); the numbers follow the regional format.
        public static string For(string host, HostChange change, double lossPercent, double? avgMs, TimeSpan? outage = null) => change switch
        {
            HostChange.Down => Loc.T("alert.down", host),
            HostChange.Up => outage is TimeSpan d ? Loc.T("alert.up.after", host, IncidentLog.FormatDuration(d)) : Loc.T("alert.up", host),
            HostChange.Degraded => Loc.T("alert.degraded", host, lossPercent,
                avgMs?.ToString("0.#", CultureInfo.CurrentCulture) ?? "-", HostMonitor.WindowSize),
            HostChange.Recovered => Loc.T("alert.recovered", host),
            _ => "",
        };
    }
}
