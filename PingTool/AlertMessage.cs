using System.Globalization;

namespace PingTool
{
    // The balloon text for a state change. Numbers use the user's culture, like
    // everything else on screen (the CSV is the one place that is invariant).
    internal static class AlertMessage
    {
        public static string For(string host, HostChange change, double lossPercent, double? avgMs) => change switch
        {
            HostChange.Down => host + " is down",
            HostChange.Up => host + " is back up",
            HostChange.Degraded => string.Format(CultureInfo.CurrentCulture,
                "{0} is degraded: {1:0.#}% loss, average {2} ms over the last {3} pings",
                host, lossPercent, avgMs?.ToString("0.#", CultureInfo.CurrentCulture) ?? "-", HostMonitor.WindowSize),
            HostChange.Recovered => host + " is back to normal",
            _ => "",
        };
    }
}
