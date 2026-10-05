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
                .Append(MaskProfile(Describe(exception), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))).Append("\r\n\r\n");
            return text.ToString();
        }

        // The file is the one a person attaches to a bug report: the folder of the user's profile (C:\Users\name\...) appears in the message
        // of a file error and in a stack trace with source paths, and it holds the user's name. It is written as %USERPROFILE% instead.
        internal static string MaskProfile(string text, string profileFolder)
        {
            string folder = profileFolder.TrimEnd('\\', '/');
            return folder.Length < 3 ? text : text.Replace(folder, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        // "TypeName: message" for a box or a console line; the type alone when the message cannot be read (a Message that throws), and a
        // fixed word when there is no exception object. Never throws: it is called while the program is going down.
        public static string Summary(object? exception)
        {
            if (exception is not Exception ex) return exception is null ? "unknown error" : "unknown error (" + exception.GetType().Name + ")";
            string type = ex.GetType().Name;
            try
            {
                return type + ": " + ex.Message;
            }
            catch (Exception)
            {
                return type + " (its message could not be read)";
            }
        }

        // The text of the exception. This runs in the handler of last resort, where a throw is a trace lost: an exception whose own ToString,
        // Message or StackTrace throws is described by its type (and what can still be read of it) instead of breaking the entry.
        private static string Describe(object? exception)
        {
            if (exception is null) return "(no exception object)";
            try
            {
                return exception.ToString() ?? "(no text)";
            }
            catch (Exception ex)
            {
                string type = exception.GetType().FullName ?? exception.GetType().Name;
                string why = ex.GetType().FullName ?? ex.GetType().Name;
                return $"{type} (its own text could not be read: {why})";
            }
        }

        private static string? lastSignature, lastPath;
        private static int repeats;

        // What identifies an error: the entry without its first line (the time and the version).
        private static string SignatureOf(string entry)
        {
            int at = entry.IndexOf("\r\n", StringComparison.Ordinal);
            return at < 0 ? entry : entry[(at + 2)..];
        }

        // A repeat is written to the file at the 2nd time, then at the 10th, the 100th, the 1000th...
        internal static bool IsMilestone(int n)
        {
            if (n == 2) return true;
            if (n < 10) return false;
            while (n % 10 == 0) n /= 10;
            return n == 1;
        }

        private static string RepeatNote(int count) =>
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
            + " The same error again: " + count.ToString(CultureInfo.InvariantCulture) + " times so far (see the entry above).\r\n\r\n";

        // Forgets the last error (what the tests use to start from nothing).
        internal static void ForgetLast() { lock (gate) { lastSignature = null; lastPath = null; repeats = 0; } }

        // False when it could not be written (no folder, disk full...): the caller says so, nothing is thrown. WHATEVER the cause: this is
        // the last-resort handler, called from the code that handles what nobody else caught, and an exception leaving it (a
        // SecurityException from a locked-down profile, a path the system refuses in a way not foreseen here) would replace the error
        // that is being reported by another one and end the program. `write` is a seam for the tests: what appends the text.
        public static bool TryAppend(string path, string entry, Action<string, string>? write = null)
        {
            try
            {
                lock (gate)
                {
                    // The same error over and over (a loop that fails every second) would fill the file, and two rotations later the FIRST
                    // entry, the cause, would be gone. The first one is written in full; the repeats are counted and said at 2, 10, 100...
                    string signature = SignatureOf(entry);
                    bool repeat = signature.Length > 0 && signature == lastSignature && path == lastPath;
                    if (repeat)
                    {
                        repeats++;
                        if (!IsMilestone(repeats)) return true;   // counted, not written: it is in the entry above
                        entry = RepeatNote(repeats);
                    }

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

                    (write ?? ((p, text) => File.AppendAllText(p, text, new UTF8Encoding(false))))(path, entry);

                    // Only an entry that REACHED the file is the one the repeats refer to: after a failed write the next one must be written.
                    if (!repeat)
                    {
                        lastSignature = signature.Length > 0 ? signature : null;
                        lastPath = path;
                        repeats = 1;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
