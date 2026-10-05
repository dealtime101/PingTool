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

        // A power cut or a crash in the middle of an earlier write leaves the file ending in half a line (no line end after it). The
        // next rows would continue that line and spoil two rows at once, so what follows the last complete line is cut off before
        // appending: it was a half row that nothing can read, and the rows it belonged to were lost with that crash anyway. The check
        // reads the end of the file only; the append itself keeps FileMode.Append (two windows can log the same host at once).
        // ponytail: a half row cut INSIDE a quoted field that holds a line feed (rare) leaves a partial row; a line feed always ends the check.
        internal static void CutPartialLine(string path)
        {
            if (!File.Exists(path)) return;

            long keep;
            byte[]? tail = null;   // what follows the last line end, when it is short enough to be a row
            using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long length = read.Length;
                if (length == 0) return;

                keep = 0;
                var block = new byte[4096];
                for (long end = length; end > 0 && keep == 0; end -= block.Length)
                {
                    long from = Math.Max(0, end - block.Length);
                    read.Position = from;
                    int n = read.Read(block, 0, (int)(end - from));
                    int at = Array.LastIndexOf(block, (byte)'\n', n - 1, n);
                    if (at >= 0) keep = from + at + 1;
                }

                if (keep == length) return;   // ends on a line end: nothing partial

                if (length - keep <= MaxRowBytes)
                {
                    tail = new byte[length - keep];
                    read.Position = keep;
                    read.ReadExactly(tail);
                }
            }

            // A last row that is COMPLETE but has no line end (the file was opened and saved by an editor or a spreadsheet that does
            // not write a final one) is a real ping: it gets its line end instead of being cut. Only what does not read as a whole row
            // is a half row of a crash. ponytail: a crash that cut only the end of the last field (the detail) leaves a row that still
            // reads as whole, and is kept with its shortened detail.
            if (tail is not null && IsWholeRow(tail))
            {
                using var end = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                end.Write(tail[^1] == (byte)'\r' ? "\n"u8 : "\r\n"u8);
                return;
            }

            using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            write.SetLength(keep);
        }

        // More than this after the last line end is not a row of ours (a row is a timestamp, a host, a status, a time and a short detail).
        private const int MaxRowBytes = 64 * 1024;

        // The text reads as exactly one row of a PingTool log (the five fields, a date, a number or nothing, a host and a status).
        private static bool IsWholeRow(byte[] text) =>
            text.Length > 0
            && PingLogReader.TryParse(PingLog.CsvHeader + "\r\n" + new UTF8Encoding(false).GetString(text), out var entries, out _)
            && entries.Count == 1;

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
                    CutPartialLine(path);
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
