using System.Globalization;

namespace PingTool
{
    // What PingTool.exe was started with, e.g. a shortcut in the Startup folder:
    //   PingTool.exe --start --minimized 8.8.8.8 router.lan tcp://example.com:443
    // Hosts and --interval are for THIS launch only: they win over the saved settings, which stay as
    // they were (a shortcut must not overwrite the list the user built in the window).
    internal sealed record StartupOptions(IReadOnlyList<string> Hosts, bool Start, bool Minimized, int? IntervalMs, bool TestWebhooks = false, bool Diagnose = false)
    {
        // The targets of this launch are not the saved ones (and the saved list is left as it was).
        public bool OverridesHosts => Hosts.Count > 0 || Diagnose;

        public static readonly StartupOptions None = new(Array.Empty<string>(), false, false, null);

        public const string Usage =
            "PingTool.exe [options] [target ...]\r\n\r\n" +
            "  --start            start monitoring as soon as the window is up\r\n" +
            "  --minimized        start hidden in the notification area (double-click its icon to open)\r\n" +
            "  --interval <ms>    time between probes, 100 to 60000\r\n" +
            "  --diagnose         monitor the default gateway, the DNS servers and a few Internet references (to see where a fault is)\r\n" +
            "  --test-webhooks    send a test alert to the webhooks of settings.json, show the result and exit\r\n" +
            "  --help             show this text\r\n\r\n" +
            "Targets are those of the address box: host, tcp://host:port, http(s)://url, dns://name.\r\n" +
            "Given targets replace the saved list for this launch only.";

        // False with the reason in `message` (or Usage for --help): the caller shows it and does not start.
        public static bool TryParse(IReadOnlyList<string> args, out StartupOptions options, out string message)
        {
            options = None;
            message = "";
            var hosts = new List<string>();
            bool start = false, minimized = false, testWebhooks = false, diagnose = false;
            int? interval = null;

            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                string name = arg;
                string? inline = null;
                int eq = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
                if (eq > 0)
                {
                    name = arg[..eq];
                    inline = arg[(eq + 1)..];
                }

                switch (name.ToLowerInvariant())
                {
                    case "--start": start = true; break;
                    case "--minimized": minimized = true; break;
                    case "--test-webhooks": testWebhooks = true; break;
                    case "--diagnose": diagnose = true; break;
                    case "--help" or "-h" or "-?" or "/?":
                        message = Usage;
                        return false;
                    case "--interval":
                        string? text = inline ?? (i + 1 < args.Count ? args[++i] : null);
                        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int ms)
                            || ms < Limits.IntervalMs.Min || ms > Limits.IntervalMs.Max)
                        {
                            message = $"--interval needs a number of milliseconds from {Limits.IntervalMs.Min} to {Limits.IntervalMs.Max}.";
                            return false;
                        }

                        interval = ms;
                        break;
                    default:
                        if (arg.StartsWith('-'))
                        {
                            message = $"Unknown option {arg}.\r\n\r\n{Usage}";
                            return false;
                        }

                        if (!ProbeTarget.TryParse(arg, out _, out string error))
                        {
                            message = $"\"{arg}\": {error}";
                            return false;
                        }

                        string host = arg.Trim();
                        if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
                        break;
                }
            }

            options = new StartupOptions(hosts, start, minimized, interval, testWebhooks, diagnose);
            return true;
        }
    }
}
