namespace PingTool
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            // A bad option is said in a box and nothing starts: a shortcut that silently ignored a typo
            // would monitor the wrong thing without anybody noticing. With --headless nobody is there to click the
            // box (a scheduled task would wait for ever): the message goes to the error output, and the exit code says it.
            bool headlessAsked = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            if (!StartupOptions.TryParse(args, out var startup, out string message))
            {
                if (headlessAsked) Console.Error.WriteLine(message);
                else MessageBox.Show(message, "PingTool");
                return HeadlessRunner.ErrorCode;
            }

            // No window at all: not even the WinForms initialisation.
            if (startup.Headless) return RunHeadless(startup);

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            if (startup.TestWebhooks)
            {
                MessageBox.Show(TestWebhooks(), "PingTool");
                return 0;
            }

            Application.Run(new MainForm(startup));
            return 0;
        }

        // "PingTool.exe --headless --duration 8h --report night.html [targets]": see HeadlessRunner. Everything it has to say goes to
        // the standard output (the summary) or the error output (problems); the exit code is 0 / 1 / 2 as documented.
        private static int RunHeadless(StartupOptions o)
        {
            try
            {
                var settings = Settings.Load(Settings.DefaultPath);
                if (o.IntervalMs is int ms) settings.IntervalMs = Limits.Clamp(ms, Limits.IntervalMs);
                if (settings.LoadProblem is { } loadProblem) Console.Error.WriteLine(loadProblem);

                var hosts = (o.Diagnose ? DiagnosticTargets.Discover(out _) : new List<string>()).Concat(o.Hosts).ToList();
                if (hosts.Count == 0) hosts = settings.Hosts;

                using var webhooks = settings.Webhooks.Count == 0 ? null
                    : new WebhookSender(settings.Webhooks.Select(w => WebhookPayload.TryParseUrl(w, out var url) ? url : null).OfType<Uri>(), AppVersion.Number);
                if (webhooks is not null) webhooks.Failed += (label, reason) => Console.Error.WriteLine($"Alert not delivered to the webhook {label}: {reason}");

                string? reportPath = o.ReportPath is null ? null : Path.GetFullPath(o.ReportPath);
                bool reportFailed = false;
                void WriteReport(ReportData data)
                {
                    if (reportPath is null) return;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                        File.WriteAllText(reportPath, ReportBuilder.Build(data), new System.Text.UTF8Encoding(false));
                        reportFailed = false;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                        reportFailed = true;
                        Console.Error.WriteLine("Could not write the report: " + ex.Message);
                    }
                }

                async Task<ProbeOutcome> Probe(ProbeTarget target, int timeoutMs, CancellationToken token)
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    return await ProbeRunner.RunAsync(target, timeoutMs, ping, new byte[settings.PacketSize], token);
                }

                var result = HeadlessRunner.RunAsync(hosts, settings, o.Duration!.Value, Probe, WriteReport, alert: webhooks is null ? null : webhooks.Send)
                    .GetAwaiter().GetResult();
                Console.WriteLine(result.Summary + (reportPath is null ? "" : " Report: " + reportPath));
                return reportFailed ? HeadlessRunner.ErrorCode : result.ExitCode;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return HeadlessRunner.ErrorCode;
            }
        }

        // "PingTool.exe --test-webhooks": one test alert to every webhook of settings.json, the answer of each
        // in a box. The alert goes out exactly as a real one does (same body, same retries), so a typo in an
        // address or a refused request shows up here and not in the middle of the night. Up to ~20 s when a
        // receiver does not answer (time limit and retries); the addresses themselves are never shown.
        private static string TestWebhooks()
        {
            var settings = Settings.Load(Settings.DefaultPath);
            if (settings.Webhooks.Count == 0)
                return "No webhook is set. Put the addresses in \"Webhooks\" in " + Settings.DefaultPath + " (see the README).";

            var urls = settings.Webhooks.Select(w => WebhookPayload.TryParseUrl(w, out var url) ? url : null).OfType<Uri>();
            using var sender = new WebhookSender(urls, AppVersion.Number);
            var test = new WebhookEvent("webhook-test", HostChange.None,
                "PingTool webhook test: if you read this, alerts will reach this channel.", null, DateTimeOffset.Now);
            var results = sender.SendNowAsync(test).GetAwaiter().GetResult();
            return string.Join("\r\n", results.Select(r => (r.Ok ? "OK      " : "FAILED  ") + r.Label + " (" + r.Detail + ")"));
        }
    }
}