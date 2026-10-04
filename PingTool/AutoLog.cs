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

        public string Folder => folder;

        // What the next run logs to, given the log of the run before (null = it had none) and the folder wanted (null = no log).
        // Same folder: the same log goes on, queue included (the file names are per host and day, so nothing changes). Another
        // folder, or no log at all: what the old one still holds is not thrown away, it goes into `retired` and keeps being retried.
        // ponytail: retired logs are not capped, one per run with a folder that stays unwritable (20 000 entries at most each).
        public static AutoLog? Next(AutoLog? previous, string? folder, List<AutoLog> retired)
        {
            if (previous is not null && folder is not null && string.Equals(previous.folder, folder, StringComparison.OrdinalIgnoreCase)) return previous;
            if (previous is { Pending: > 0 }) retired.Add(previous);
            return folder is null ? null : new AutoLog(folder);
        }

        // One try for each retired log; the ones that got everything out are forgotten.
        public static void FlushRetired(List<AutoLog> retired) => retired.RemoveAll(r => r.Flush());

        // Why the last Flush could not write everything; null when it did.
        public string? LastError { get; private set; }

        // The pings that were thrown away because the folder stayed unwritable too long: how many, and the time they span. The log
        // would otherwise look complete with hours missing.
        public int Dropped { get; private set; }
        public DateTimeOffset? DroppedFrom { get; private set; }
        public DateTimeOffset? DroppedTo { get; private set; }

        // The sentence for the user; null when nothing was lost.
        public string? DropNote => Dropped == 0 ? null : string.Create(CultureInfo.CurrentCulture,
            $"{Dropped} ping(s) from {DroppedFrom?.ToLocalTime():G} to {DroppedTo?.ToLocalTime():G} could not be saved to the log files: the log folder was not writable for too long. The log has a gap there.");

        public void Add(LogEntry entry)
        {
            pending.Add(entry);
            int over = pending.Count - MaxPending;
            if (over <= 0) return;

            DroppedFrom ??= pending[0].Time;
            DroppedTo = pending[over - 1].Time;
            Dropped += over;
            pending.RemoveRange(0, over);
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

        // What a file that cannot be written throws: a full disk comes out as ArgumentOutOfRangeException on some platforms.
        private static bool IsFileFailure(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

        // True when everything pending is on disk.
        public bool Flush()
        {
            LastError = null;
            if (pending.Count == 0) return true;

            // Distinct hosts differing only by what FileName replaces would share a file: that is
            // fine, rows carry their host.
            var written = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
            // FileName once per host and day, not once per pending entry: with a stuck folder this runs every few seconds
            // over up to MaxPending entries, on the UI thread.
            var names = new Dictionary<(string, DateOnly), string>();
            foreach (var group in pending.GroupBy(e =>
            {
                var key = (e.Host, DateOnly.FromDateTime(e.Time.DateTime));
                if (!names.TryGetValue(key, out var name)) names[key] = name = FileName(e.Host, e.Time);
                return name;
            }))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, group.Key);
                    // ReadWrite sharing lets a spreadsheet that does not lock the file read it meanwhile.
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    long start = stream.Length;
                    bool isNew = start == 0;
                    var text = new StringBuilder();
                    if (isNew) text.Append(PingLog.CsvHeader).Append("\r\n");
                    foreach (var e in group) text.Append(PingLog.CsvLine(e)).Append("\r\n");
                    byte[] body = new UTF8Encoding(false).GetBytes(text.ToString());
                    // The byte-order mark makes a spreadsheet read the file as UTF-8; once, at the start.
                    try
                    {
                        stream.Write(isNew ? new UTF8Encoding(true).GetPreamble().Concat(body).ToArray() : body);
                        stream.Flush();
                    }
                    catch (Exception ex) when (IsFileFailure(ex))
                    {
                        // A full disk or a share that drops can leave part of the batch on disk before it throws. The batch
                        // stays queued, so cut the file back to where it was: otherwise the retry would append the rows a
                        // second time after a half-written line (and a new file would lose its header).
                        try { stream.SetLength(start); }
                        catch (Exception cut) when (IsFileFailure(cut)) { }
                        throw;
                    }
                    foreach (var e in group) written.Add(e);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    LastError = ex.Message;
                }
            }

            pending.RemoveAll(written.Contains);
            return pending.Count == 0;
        }
    }
}
