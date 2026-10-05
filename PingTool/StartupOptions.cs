using System.Globalization;

namespace PingTool
{
    // What PingTool.exe was started with, e.g. a shortcut in the Startup folder:
    //   PingTool.exe --start --minimized 8.8.8.8 router.lan tcp://example.com:443
    // Hosts and --interval are for THIS launch only: they win over the saved settings, which stay as
    // they were (a shortcut must not overwrite the list the user built in the window).
    internal sealed record StartupOptions(IReadOnlyList<string> Hosts, bool Start, bool Minimized, int? IntervalMs, bool TestWebhooks = false, bool Diagnose = false,
        bool Headless = false, TimeSpan? Duration = null, string? ReportPath = null)
    {
        public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(30);

        // "30s", "10m", "8h", "2d": a number and its unit, 5 seconds to 30 days.
        public static bool TryParseDuration(string? text, out TimeSpan duration)
        {
            duration = default;
            string t = (text ?? "").Trim().ToLowerInvariant();
            if (t.Length < 2 || !int.TryParse(t[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;

            // In whole seconds, as a 64-bit number, and compared with the limits BEFORE any TimeSpan is built: TimeSpan.FromDays(999999999)
            // throws (it does not fit), and an exception here ended the program at start-up instead of refusing the option.
            long perUnit = t[^1] switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, _ => 0 };
            if (perUnit == 0) return false;
            long seconds = n * perUnit;   // n is below 2^31 and perUnit at most 86400: it fits
            if (seconds < MinDuration.TotalSeconds || seconds > MaxDuration.TotalSeconds) return false;
            duration = TimeSpan.FromSeconds(seconds);
            return true;
        }

        // The targets of this launch are not the saved ones (and the saved list is left as it was).
        public bool OverridesHosts => Hosts.Count > 0 || Diagnose;

        public static readonly StartupOptions None = new(Array.Empty<string>(), false, false, null);

        public static readonly string Usage =
            "PingTool.exe [options] [target ...]\r\n\r\n" +
            "  --start            start monitoring as soon as the window is up\r\n" +
            "  --minimized        start hidden in the notification area (double-click its icon to open)\r\n" +
            $"  --interval <ms>    time between probes, {Limits.IntervalMs.Min} to {Limits.IntervalMs.Max}\r\n" +
            "  --headless         no window: probe for --duration, write --report, exit (exit code 0 = no incident, 1 = an outage or a slowdown, 2 = error)\r\n" +
            "  --duration <t>     with --headless: how long to probe, 5s to 30d (30s, 10m, 8h, 2d)\r\n" +
            "  --report <file>    with --headless: where to write the HTML report at the end (refreshed every 10 minutes meanwhile)\r\n" +
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
            bool start = false, minimized = false, testWebhooks = false, diagnose = false, headless = false;
            int? interval = null;
            TimeSpan? duration = null;
            string? reportPath = null;

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

                // A switch takes no value, and "--start=false" is REFUSED (not read as "off"): accepted, it would be easy to take for
                // "off" what the program reads as written, that is ON. A switch is on when written, off when left out.
                if (inline is not null && name.ToLowerInvariant() is "--start" or "--minimized" or "--test-webhooks" or "--diagnose" or "--headless" or "--help")
                {
                    message = $"{name} takes no value: write it to switch it on, leave it out to switch it off.";
                    return false;
                }

                switch (name.ToLowerInvariant())
                {
                    case "--start": start = true; break;
                    case "--minimized": minimized = true; break;
                    case "--test-webhooks": testWebhooks = true; break;
                    case "--diagnose": diagnose = true; break;
                    case "--headless": headless = true; break;
                    case "--duration":
                        if (!TryParseDuration(inline ?? (i + 1 < args.Count ? args[++i] : null), out var d))
                        {
                            message = "--duration needs a number and a unit from 5s to 30d, for example --duration 8h (30s, 10m, 8h, 2d).";
                            return false;
                        }

                        duration = d;
                        break;
                    case "--report":
                        string? path = inline ?? (i + 1 < args.Count ? args[++i] : null);
                        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-') || path.Any(char.IsControl))
                        {
                            message = "--report needs the file to write, for example --report C:\\temp\\night.html";
                            return false;
                        }

                        reportPath = path.Trim();
                        break;
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
                        if (!hosts.Contains(host, TargetKey.Comparer)) hosts.Add(host);
                        break;
                }
            }

            if (headless && duration is null)
            {
                message = "--headless needs --duration (how long to probe), for example --headless --duration 8h --report night.html";
                return false;
            }

            if (!headless && (duration is not null || reportPath is not null))
            {
                message = "--duration and --report only work with --headless.";
                return false;
            }

            options = new StartupOptions(hosts, start, minimized, interval, testWebhooks, diagnose, headless, duration, reportPath);
            return true;
        }
    }
}
