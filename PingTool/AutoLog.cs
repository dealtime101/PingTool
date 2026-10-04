using System.Globalization;
using System.Text;

namespace PingTool
{
    // Writes every ping to disk as it happens, so a night of monitoring or a crash loses nothing.
    // One CSV per host per day (a new file at midnight), same columns as the manual export.
    //
    // Entries wait in memory and are written by Flush (the form calls it every few seconds, at Stop
    // and on exit). A file that cannot be written right now - a spreadsheet holding it, a full
    // disk, a missing share - is not an error that stops monitoring: its entries stay queued and
    // go out, in order, at the next Flush that works.
    internal sealed class AutoLog
    {
        // What a stuck folder can cost in memory: 20 000 entries, i.e. about 5 h 33 min of ONE host at one ping a second (1 000 entries
        // = 16.7 min), shared by all the hosts watched: with 4 hosts it is about 1 h 23 min. The oldest are dropped past this, like the
        // in-memory log does.
        public const int MaxPending = 20_000;
        private const int MaxNameLength = 80;

        private readonly string folder;
        private readonly List<LogEntry> pending = new();

        public AutoLog(string folder) => this.folder = folder;

        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingTool", "logs");

        public int Pending => pending.Count;

        // Why the last Flush could not write everything; null when it did.
        public string? LastError { get; private set; }

        public void Add(LogEntry entry)
        {
            pending.Add(entry);
            if (pending.Count > MaxPending) pending.RemoveRange(0, pending.Count - MaxPending);
        }

        // "PingTool-8.8.8.8-2026-10-03.csv". The host is whatever the user typed (tcp://x:443,
        // http://x/a?b): anything a file name cannot hold becomes "_".
        public static string FileName(string host, DateTimeOffset time)
        {
            var name = new StringBuilder();
            foreach (char c in host.Trim())
                name.Append(char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal) ? '_' : c);

            string safe = name.ToString().Trim('.', ' ');
            if (safe.Length > MaxNameLength) safe = safe[..MaxNameLength].TrimEnd('.', ' ');
            if (safe.Length == 0) safe = "host";

            return "PingTool-" + safe + "-" + time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        }

        // True when everything pending is on disk.
        public bool Flush()
        {
            LastError = null;
            if (pending.Count == 0) return true;

            // Distinct hosts differing only by what FileName replaces would share a file: that is
            // fine, rows carry their host.
            var written = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
            foreach (var group in pending.GroupBy(e => FileName(e.Host, e.Time)))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, group.Key);
                    // ReadWrite sharing lets a spreadsheet that does not lock the file read it meanwhile.
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    bool isNew = stream.Length == 0;
                    // The whole batch goes out in ONE write, so a failure leaves the batch either on disk
                    // or not at all (a half-written batch would come out twice at the retry).
                    var text = new StringBuilder();
                    if (isNew) text.Append(PingLog.CsvHeader).Append("\r\n");
                    foreach (var e in group) text.Append(PingLog.CsvLine(e)).Append("\r\n");
                    byte[] body = new UTF8Encoding(false).GetBytes(text.ToString());
                    // The byte-order mark makes a spreadsheet read the file as UTF-8; once, at the start.
                    stream.Write(isNew ? new UTF8Encoding(true).GetPreamble().Concat(body).ToArray() : body);
                    stream.Flush();
                    foreach (var e in group) written.Add(e);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    LastError = ex.Message;
                }
            }

            pending.RemoveAll(written.Contains);
            return pending.Count == 0;
        }
    }
}
