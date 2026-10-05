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
            // The handler of last resort comes first, so that nothing below can fail without leaving a trace in crash.log.
            AppDomain.CurrentDomain.UnhandledException += (_, e) => OnFatal(e.ExceptionObject);

            bool headlessAsked = args.Any(a => string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase));
            if (!StartupOptions.TryParse(args, out var startup, out string message))
            {
                if (headlessAsked) Console.Error.WriteLine(message);
                else
                {
                    // --help comes back here too, with the usage text: that is not an error and must not wear the error icon.
                    bool help = message == StartupOptions.Usage;
                    MessageBox.Show(message, help ? "PingTool - help" : "PingTool - invalid command line", MessageBoxButtons.OK,
                        help ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                }

                return HeadlessRunner.ErrorCode;
            }

            // No window at all: not even the WinForms initialisation.
            if (startup.Headless) return RunHeadless(startup);

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            // The windows are dark: the system controls (lists, combo boxes, buttons, scroll bars) follow (PIN461.21, .53, .67, .96).
#pragma warning disable WFO5001 // SetColorMode is still marked experimental
            Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001

            // An error in the window's own thread (an event handler, an await that resumed there) is logged and PingTool carries on:
            // a monitoring stopped by one stray exception would be worse than the error. Before any window exists, as required.
            guiStarted = true;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => OnWindowThreadError(e.Exception);

            if (startup.TestWebhooks)
            {
                MessageBox.Show(TestWebhooks(), "PingTool");
                return 0;
            }

            Application.Run(new MainForm(startup));
            return 0;
        }

        private static bool guiStarted;
        private static bool windowErrorShown;

        // The process is going down (an exception nobody caught on another thread): write what is known, say so where someone can read it.
        private static void OnFatal(object? exception)
        {
            string entry = CrashLog.Entry(exception, DateTimeOffset.Now, AppVersion.Display, fatal: true);
            bool saved = CrashLog.TryAppend(CrashLog.DefaultPath, entry);
            string what = exception is Exception ex ? ex.GetType().Name + ": " + ex.Message : "unknown error";
            string where = saved ? "Details: " + CrashLog.DefaultPath : "The details could not be saved.";
            if (guiStarted) MessageBox.Show("PingTool hit an unexpected error and must close.\r\n\r\n" + what + "\r\n\r\n" + where, "PingTool - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine("Unexpected error: " + what + ". " + where);
        }

        // Every error is written to the log; the box is shown once per run (a loop failing every second must not bury the window in boxes).
        private static void OnWindowThreadError(Exception exception)
        {
            bool saved = CrashLog.TryAppend(CrashLog.DefaultPath, CrashLog.Entry(exception, DateTimeOffset.Now, AppVersion.Display, fatal: false));
            if (windowErrorShown) return;
            windowErrorShown = true;
            MessageBox.Show("PingTool hit an unexpected error and carries on; the monitoring may be incomplete. Later errors of this kind are only logged.\r\n\r\n"
                + exception.GetType().Name + ": " + exception.Message + "\r\n\r\n"
                + (saved ? "Details: " + CrashLog.DefaultPath : "The details could not be saved."),
                "PingTool - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // "PingTool.exe --headless --duration 8h --report night.html [targets]": see HeadlessRunner. Everything it has to say goes to
        // the standard output (the summary) or the error output (problems); the exit code is 0 / 1 / 2 as documented.
        private static int RunHeadless(StartupOptions o) => HeadlessRunner.Guarded(() => RunHeadlessCore(o), ex =>
            CrashLog.TryAppend(CrashLog.DefaultPath, CrashLog.Entry(ex, DateTimeOffset.Now, AppVersion.Display, fatal: true)));

        private static int RunHeadlessCore(StartupOptions o)
        {
            try
            {
                var settings = Settings.Load(Settings.DefaultPath);
                if (o.IntervalMs is int ms) settings.IntervalMs = Limits.Clamp(ms, Limits.IntervalMs);
                if (settings.LoadProblem is { } loadProblem) Console.Error.WriteLine(loadProblem);

                var found = new List<string>();
                if (o.Diagnose)
                {
                    found = DiagnosticTargets.Discover(out bool gatewayFound);
                    if (!gatewayFound) Console.Error.WriteLine(DiagnosticTargets.NoGatewayMessage);
                }

                var hosts = found.Concat(o.Hosts).ToList();
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

                // Ctrl+C (or Ctrl+Break) ends the monitoring early instead of killing the process: the report is written with what was
                // seen so far, the summary is printed, and the exit code says what was observed, as at the end of the duration.
                using var interrupted = new CancellationTokenSource();
                ConsoleCancelEventHandler onInterrupt = (_, e) =>
                {
                    e.Cancel = true;   // the process goes on to finish properly
                    if (!interrupted.IsCancellationRequested) Console.Error.WriteLine("Interrupted: writing the report with what was seen so far.");
                    interrupted.Cancel();
                };
                Console.CancelKeyPress += onInterrupt;
                HeadlessResult result;
                try
                {
                    result = HeadlessRunner.RunAsync(hosts, settings, o.Duration!.Value, Probe, WriteReport, alert: webhooks is null ? null : webhooks.Send,
                        stop: interrupted.Token).GetAwaiter().GetResult();
                }
                finally
                {
                    Console.CancelKeyPress -= onInterrupt;
                }
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
                return WebhookTest.Summary(settings.LoadProblem, settings.WebhooksIgnored, 0, Array.Empty<WebhookResult>(), Settings.DefaultPath);

            var urls = settings.Webhooks.Select(w => WebhookPayload.TryParseUrl(w, out var url) ? url : null).OfType<Uri>();
            using var sender = new WebhookSender(urls, AppVersion.Number);
            var test = new WebhookEvent("webhook-test", HostChange.None,
                "PingTool webhook test: if you read this, alerts will reach this channel.", null, DateTimeOffset.Now);
            var results = sender.SendNowAsync(test).GetAwaiter().GetResult();
            return WebhookTest.Summary(settings.LoadProblem, settings.WebhooksIgnored, settings.Webhooks.Count, results, Settings.DefaultPath);
        }
    }
}