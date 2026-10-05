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

        // The queue is touched by the window (Add, on its thread) and by a flush running in the background: this lock covers every
        // use of `pending`, and only that, never the disk. Flushes take turns on the other lock, so two of them never write the same rows.
        private readonly object queue = new();
        private readonly object flushing = new();
        private int running;   // 1 while a background flush is in flight

        private readonly Action<string>? beforeDiskAccess;   // called before each file is touched: a seam to make the disk slow in a test
        private readonly Action<FileStream>? afterWrite;      // called after the rows were written: a seam to make the disk fail then
        private readonly Action<FileStream, long> cutBack;    // brings a file back to a length (a seam to make that fail)

        // A write that failed AFTER part of its rows reached the disk, and could not be cut back (the disk was still failing): where the file
        // was before, and what was being written. The batch stays queued, so the next write to that file first takes these rows back, or the
        // retry would append them a second time. Only touched under `flushing`.
        private readonly Dictionary<string, (long Start, byte[] Written)> unrecovered = new();

        public AutoLog(string folder, Action<string>? beforeDiskAccess = null, Action<FileStream>? afterWrite = null, Action<FileStream, long>? cutBack = null)
        {
            this.folder = folder;
            this.beforeDiskAccess = beforeDiskAccess;
            this.afterWrite = afterWrite;
            this.cutBack = cutBack ?? ((stream, length) => stream.SetLength(length));
        }

        // Takes back the rows of a failed write that stayed on disk. True when the file can be written again. Under the file's gate.
        // The rows are cut only if what follows `start` is still exactly (the beginning of) what was written: if another window added
        // rows meanwhile, cutting would destroy them, so the rows are left (a duplicate is the lesser harm) and the user is told.
        private bool TakeBack(string path, (long Start, byte[] Written) failed, out string? note)
        {
            note = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                long after = stream.Length - failed.Start;
                if (after <= 0) { unrecovered.Remove(path); return true; }   // nothing of it left on disk
                var tail = new byte[after];
                stream.Position = failed.Start;
                stream.ReadExactly(tail);
                if (after > failed.Written.Length || !failed.Written.AsSpan(0, tail.Length).SequenceEqual(tail))
                {
                    unrecovered.Remove(path);
                    note = "Rows of an earlier failed write could not be taken back from " + Path.GetFileName(path) + " because the file changed meanwhile: some rows may appear twice.";
                    return true;
                }

                cutBack(stream, failed.Start);
                unrecovered.Remove(path);
                return true;
            }
            catch (FileNotFoundException) { unrecovered.Remove(path); return true; }
            catch (Exception ex) when (IsFileFailure(ex)) { note = ex.Message; return false; }
        }

        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PingTool", "logs");

        public int Pending { get { lock (queue) return pending.Count; } }

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

        // Why the last Flush could not write everything; null when it did. Written by a flush that may run in the background.
        private volatile string? lastError;
        public string? LastError => lastError;

        // The pings that were thrown away because the folder stayed unwritable too long: how many, and the time they span. The log
        // would otherwise look complete with hours missing.
        public int Dropped { get; private set; }
        public DateTimeOffset? DroppedFrom { get; private set; }   // the first and the last ping dropped, whatever was written between
        public DateTimeOffset? DroppedTo { get; private set; }

        // The gaps themselves: a folder blocked, repaired, blocked again leaves two, and what was written in between is on the disk. A drop
        // that follows the previous one with nothing written between them goes on the same gap. Touched on the window's thread only.
        private readonly List<(DateTimeOffset From, DateTimeOffset To)> gaps = new();
        private volatile bool writtenSinceDrop;

        public int Gaps => gaps.Count;

        // The sentence for the user; null when nothing was lost.
        public string? DropNote
        {
            get
            {
                if (Dropped == 0) return null;
                string Span((DateTimeOffset From, DateTimeOffset To) g) => string.Create(CultureInfo.CurrentCulture, $"from {g.From.ToLocalTime():G} to {g.To.ToLocalTime():G}");
                if (gaps.Count == 1)
                    return $"{Dropped} ping(s) {Span(gaps[0])} could not be saved to the log files: the log folder was not writable for too long. The log has a gap there.";

                // At most three are named; the rest is counted, so the sentence stays one a balloon can hold.
                string named = string.Join("; ", gaps.Take(3).Select(Span)) + (gaps.Count > 3 ? $"; and {gaps.Count - 3} more" : "");
                return $"{Dropped} ping(s) could not be saved to the log files: the log folder was not writable for too long. The log has {gaps.Count} gaps, {named}; what was written between them is in the files.";
            }
        }

        // On the window's thread: it is also the only one that writes Dropped, DroppedFrom and DroppedTo.
        public void Add(LogEntry entry)
        {
            lock (queue)
            {
                pending.Add(entry);
                int over = pending.Count - MaxPending;
                if (over <= 0) return;

                DroppedFrom ??= pending[0].Time;
                DroppedTo = pending[over - 1].Time;
                // Same gap when nothing was written since the last drop; a new one when some pings reached the disk in between.
                if (gaps.Count > 0 && !writtenSinceDrop) gaps[^1] = (gaps[^1].From, pending[over - 1].Time);
                else gaps.Add((pending[0].Time, pending[over - 1].Time));
                writtenSinceDrop = false;
                Dropped += over;
                pending.RemoveRange(0, over);
            }
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

        // A lock between the windows (processes) of one user that write the same file: a named mutex per file, taken for the length of a
        // flush of that file, which is milliseconds. FileMode.Append alone does not do it: the position is kept by each process, so two of
        // them appending at once write over each other. ponytail: Local (this user's session) is the scope, as the logs are in the user's own folder.
        private sealed class FileGate : IDisposable
        {
            private readonly Mutex mutex;
            private FileGate(Mutex mutex) => this.mutex = mutex;

            public static FileGate? TryEnter(string path, TimeSpan wait)
            {
                string name = "PingTool.AutoLog." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())));
                var mutex = new Mutex(false, name);
                try
                {
                    if (!mutex.WaitOne(wait)) { mutex.Dispose(); return null; }
                }
                catch (AbandonedMutexException)
                {
                    // The other window died holding it: the lock is ours, and CutPartialLine deals with a row it left half written.
                }

                return new FileGate(mutex);
            }

            public void Dispose()
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }

        // A flush that does not hold the window up: the disk work (which a share that went away makes last tens of seconds, once per
        // file) runs on a pool thread, one at a time. Null = one is already running, nothing was started; else whether everything pending
        // is on disk when it ends. The caller reads the result back on its own thread (await).
        public Task<bool?> FlushInBackground()
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return Task.FromResult<bool?>(null);
            return Task.Run<bool?>(() =>
            {
                try { return Flush(); }
                finally { Volatile.Write(ref running, 0); }
            });
        }

        // True when everything pending is on disk. Waits for a flush in progress (Stop and exit want what is queued written NOW).
        public bool Flush()
        {
            lock (flushing) return FlushOnce();
        }

        private bool FlushOnce()
        {
            lastError = null;
            // What is queued now; what arrives while the disk is being written waits for the next flush.
            List<LogEntry> batch;
            lock (queue) batch = pending.ToList();
            if (batch.Count == 0) return true;

            // Distinct hosts differing only by what FileName replaces would share a file: that is
            // fine, rows carry their host.
            var written = new HashSet<LogEntry>(ReferenceEqualityComparer.Instance);
            // FileName once per host and day, not once per pending entry: with a stuck folder this runs every few seconds
            // over up to MaxPending entries.
            var names = new Dictionary<(string, DateOnly), string>();
            foreach (var group in batch.GroupBy(e =>
            {
                var key = (e.Host, DateOnly.FromDateTime(e.Time.DateTime));
                if (!names.TryGetValue(key, out var name)) names[key] = name = FileName(e.Host, e.Time);
                return name;
            }))
            {
                try
                {
                    beforeDiskAccess?.Invoke(group.Key);
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, group.Key);
                    // Two windows can log the same host into the same file: what one does (cut a half row, append, take its batch back
                    // after a failure) must not interleave with what the other does. A window that cannot get its turn keeps its rows
                    // for the next flush, as for a file a spreadsheet holds.
                    using var gate = FileGate.TryEnter(path, TimeSpan.FromMilliseconds(250));
                    if (gate is null)
                    {
                        lastError = "Another PingTool window is writing this file; these rows wait for the next flush.";
                        continue;
                    }

                    if (unrecovered.TryGetValue(path, out var failed))
                    {
                        bool back = TakeBack(path, failed, out string? why);
                        if (!back)
                        {
                            lastError = "An earlier write to " + Path.GetFileName(path) + " left rows on disk that cannot be taken back yet (" + why + "); these rows wait so that they are not written twice.";
                            continue;
                        }

                        if (why is not null) lastError = why;
                    }

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
                    byte[] bytes = isNew ? new UTF8Encoding(true).GetPreamble().Concat(body).ToArray() : body;
                    try
                    {
                        stream.Write(bytes);
                        stream.Flush();
                        afterWrite?.Invoke(stream);
                    }
                    catch (Exception ex) when (IsFileFailure(ex))
                    {
                        // A full disk or a share that drops can leave part of the batch on disk before it throws. The batch
                        // stays queued, so cut the file back to where it was: otherwise the retry would append the rows a
                        // second time after a half-written line (and a new file would lose its header). If that fails too,
                        // it is remembered (`unrecovered`): the next write to this file takes the rows back first, or does not happen.
                        try { cutBack(stream, start); }
                        catch (Exception cut) when (IsFileFailure(cut))
                        {
                            unrecovered[path] = (start, bytes);
                            throw new IOException(ex.Message + " (and the rows already written could not be taken back: the next write will first try again)", ex);
                        }

                        throw;
                    }
                    foreach (var e in group) written.Add(e);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    lastError = ex.Message;
                }
            }

            lock (queue)
            {
                if (written.Count > 0) writtenSinceDrop = true;   // what is dropped after this is a new gap
                pending.RemoveAll(written.Contains);
                return pending.Count == 0;
            }
        }
    }
}
