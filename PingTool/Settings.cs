using System.Text.Json;

namespace PingTool
{
    // The range each numeric setting may take: the same limits as the boxes in the window.
    // A settings.json that is valid JSON can still hold -5 or 2147483647; those are pulled
    // back to the nearest limit when loaded, instead of travelling on to the code that probes.
    internal static class Limits
    {
        public static readonly (int Min, int Max) IntervalMs = (100, 60_000);
        public static readonly (int Min, int Max) TimeoutMs = (100, 10_000);
        public static readonly (int Min, int Max) PacketSize = (1, 65_500);
        public static readonly (int Min, int Max) DegradedLatencyMs = (1, 60_000);
        public static readonly (int Min, int Max) DegradedLossPercent = (1, 100);
        public static readonly (int Min, int Max) DownAfter = (1, 20);

        public static int Clamp(int value, (int Min, int Max) range) => Math.Clamp(value, range.Min, range.Max);
    }

    // What survives between two launches. A missing, unreadable or hand-broken
    // file must never stop the app from starting: it just means defaults.
    internal sealed class Settings
    {
        public const int MaxRecent = 10;
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public string Address { get; set; } = "google.ca";
        public List<string> Recent { get; set; } = new();
        public List<string> Hosts { get; set; } = new();
        public int IntervalMs { get; set; } = 1000;
        public int TimeoutMs { get; set; } = 1000;
        public int PacketSize { get; set; } = 32;
        public bool Alert { get; set; } = true;
        public bool Compact { get; set; }

        // "Save log to disk": every ping appended to a daily CSV per host (see AutoLog), in this
        // folder; blank = %APPDATA%\PingTool\logs. The folder is edited in settings.json.
        public bool SaveLog { get; set; }
        public string LogFolder { get; set; } = "";

        // A host is "degraded" when the last 10 pings show this much loss or this
        // average latency (see HostMonitor). Edited in settings.json.
        public int DegradedLatencyMs { get; set; } = 150;
        public int DegradedLossPercent { get; set; } = 30;
        public int DownAfter { get; set; } = HostMonitor.DefaultDownAfter;

        // Named monitoring profiles (see Profile) and the one last used.
        public List<Profile> Profiles { get; set; } = new();
        public string ActiveProfile { get; set; } = "";

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PingTool", "settings.json");

        public static Settings Load(string path)
        {
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
                s.Normalize();
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or JsonException or NotSupportedException)
            {
                // Not silent: a broken file is replaced by defaults at the next save.
                System.Diagnostics.Debug.WriteLine($"Settings not loaded from {path}: {ex}");
                return new Settings();
            }
        }

        // Makes whatever was read usable: numbers pulled into range, lists free of nulls, blanks,
        // duplicates and excess ("Recent": null would otherwise throw later, far from the cause).
        private void Normalize()
        {
            IntervalMs = Limits.Clamp(IntervalMs, Limits.IntervalMs);
            TimeoutMs = Limits.Clamp(TimeoutMs, Limits.TimeoutMs);
            PacketSize = Limits.Clamp(PacketSize, Limits.PacketSize);
            DegradedLatencyMs = Limits.Clamp(DegradedLatencyMs, Limits.DegradedLatencyMs);
            DegradedLossPercent = Limits.Clamp(DegradedLossPercent, Limits.DegradedLossPercent);
            DownAfter = Limits.Clamp(DownAfter, Limits.DownAfter);

            Address = (Address ?? "").Trim();
            LogFolder = (LogFolder ?? "").Trim();
            ActiveProfile ??= "";
            Recent = (Recent ?? new List<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRecent).ToList();
            Hosts = (Hosts ?? new List<string>())
                .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList();
            Profiles = ProfileBook.Sanitize(Profiles);
        }

        // Written beside the target and moved over it, so a crash mid-write
        // leaves the old file instead of half of a new one.
        public void Save(string path)
        {
            // A bare name like "settings.json" has directory "" (not null), which
            // CreateDirectory refuses: resolve against the current folder first.
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);   // null only for a root, which is no file path
            string temp = full + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, WriteOptions));
            File.Move(temp, full, overwrite: true);
        }

        // Most recent first, no duplicates (case-insensitive), capped.
        public void AddRecent(string address)
        {
            address = address.Trim();
            if (address.Length == 0) return;
            Recent.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
            Recent.Insert(0, address);
            if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }
    }
}
