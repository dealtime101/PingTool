namespace PingTool
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // A bad option is said in a box and nothing starts: a shortcut that silently ignored a typo
            // would monitor the wrong thing without anybody noticing.
            if (!StartupOptions.TryParse(args, out var startup, out string message))
            {
                MessageBox.Show(message, "PingTool");
                return;
            }

            if (startup.TestWebhooks)
            {
                MessageBox.Show(TestWebhooks(), "PingTool");
                return;
            }

            Application.Run(new MainForm(startup));
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