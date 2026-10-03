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

            Application.Run(new MainForm(startup));
        }
    }
}