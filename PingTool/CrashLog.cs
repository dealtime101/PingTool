using System.Globalization;
using System.Text;

namespace PingTool
{
    // What an exception nobody caught leaves behind: one entry in crash.log next to settings.json, so that a monitoring that stopped
    // or misbehaved in the night can be explained in the morning. Writing it must never throw (it runs inside the handler of last resort).
    internal static class CrashLog
    {
        public const long MaxBytes = 1_000_000;   // past this the file is moved aside (one generation kept): it cannot grow for ever

        private static readonly object gate = new();

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingTool", "crash.log");

        // fatal = the process is going down (a thread other than the window's); otherwise PingTool carries on after the error.
        public static string Entry(object? exception, DateTimeOffset time, string version, bool fatal)
        {
            var text = new StringBuilder();
            text.Append(time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
                .Append(" PingTool ").Append(version).Append(fatal ? " - FATAL" : " - continued").Append("\r\n")
                .Append(exception?.ToString() ?? "(no exception object)").Append("\r\n\r\n");
            return text.ToString();
        }

        // False when it could not be written (no folder, disk full...): the caller says so, nothing is thrown.
        public static bool TryAppend(string path, string entry)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Moving the old file aside is housekeeping: if it fails (crash.log.1 held by an editor or an antivirus), the entry is written
                    // all the same, the file just goes over MaxBytes this once. The entry is the one thing this method is for.
                    try
                    {
                        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                    {
                        System.Diagnostics.Debug.WriteLine("crash.log could not be rotated: " + ex.Message);
                    }

                    File.AppendAllText(path, entry, new UTF8Encoding(false));
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return false;
            }
        }
    }
}
