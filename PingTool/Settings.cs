using System.Text.Json;

namespace PingTool
{
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

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PingTool", "settings.json");

        public static Settings Load(string path)
        {
            try
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
                // "Recent": null in the file would otherwise throw later, far from the cause.
                s.Recent ??= new();
                s.Hosts ??= new();
                s.Address ??= "";
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

        // Written beside the target and moved over it, so a crash mid-write
        // leaves the old file instead of half of a new one.
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, WriteOptions));
            File.Move(temp, path, overwrite: true);
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
