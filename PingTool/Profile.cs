namespace PingTool
{
    // A named, reusable way of monitoring: the targets and every setting that shapes the probing.
    // Switching from a quick check to a long watch is picking another name.
    internal sealed class Profile
    {
        public string Name { get; set; } = "";
        public List<string> Hosts { get; set; } = new();
        public int IntervalMs { get; set; } = 1000;
        public int TimeoutMs { get; set; } = 1000;
        public int PacketSize { get; set; } = 32;
        public bool Alert { get; set; } = true;
        public int DegradedLatencyMs { get; set; } = 150;
        public int DegradedLossPercent { get; set; } = 30;
        public int DownAfter { get; set; } = HostMonitor.DefaultDownAfter;

        // The name and own limits of some of the hosts above, by address (see TargetOptions).
        public Dictionary<string, TargetOptions> TargetOptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // The rules for the list of profiles kept in settings.json. Names are compared without
    // regard to case ("Home" and "home" are one profile), the way Windows treats file names.
    internal static class ProfileBook
    {
        public const int MaxProfiles = 20;
        public const int MaxNameLength = 40;

        public static bool TryName(string? input, out string name, out string error)
        {
            name = (input ?? "").Trim();
            error = "";

            if (name.Length == 0) error = "Give the profile a name.";
            else if (string.Equals(name, DiagnosticTargets.ProfileName, StringComparison.OrdinalIgnoreCase))
                error = "That name is reserved for the built-in diagnosis: choose another.";
            else if (name.Length > MaxNameLength) error = $"The name is limited to {MaxNameLength} characters.";
            else if (name.Any(char.IsControl)) error = "The name cannot contain control characters.";

            return error.Length == 0;
        }

        public static Profile? Find(IEnumerable<Profile> book, string? name) =>
            book.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        // Replaces the profile of the same name where it is, or appends. False when the book is full
        // (and the name is new): the caller says so, nothing is lost silently.
        public static bool Upsert(List<Profile> book, Profile profile)
        {
            int at = book.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                book[at] = profile;
                return true;
            }

            if (book.Count >= MaxProfiles) return false;
            book.Add(profile);
            return true;
        }

        public static bool Remove(List<Profile> book, string? name) =>
            book.RemoveAll(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;

        // What a hand-edited or damaged settings.json may contain: nulls, blank or repeated names,
        // a missing host list, more profiles than allowed. Keeps what is usable, first one wins.
        public static List<Profile> Sanitize(IEnumerable<Profile?>? loaded)
        {
            var clean = new List<Profile>();
            foreach (var p in loaded ?? Enumerable.Empty<Profile?>())
            {
                if (p is null || !TryName(p.Name, out string name, out _)) continue;
                if (Find(clean, name) is not null) continue;

                p.Name = name;
                p.IntervalMs = Limits.Clamp(p.IntervalMs, Limits.IntervalMs);
                p.TimeoutMs = Limits.Clamp(p.TimeoutMs, Limits.TimeoutMs);
                p.PacketSize = Limits.Clamp(p.PacketSize, Limits.PacketSize);
                p.DegradedLatencyMs = Limits.Clamp(p.DegradedLatencyMs, Limits.DegradedLatencyMs);
                p.DegradedLossPercent = Limits.Clamp(p.DegradedLossPercent, Limits.DegradedLossPercent);
                p.DownAfter = Limits.Clamp(p.DownAfter, Limits.DownAfter);
                p.TargetOptions = PingTool.TargetOptions.Clean(p.TargetOptions!);
                // Same rules as an imported profile: one entry per address (case ignored), at most MaxHostsPerProfile.
                p.Hosts = (p.Hosts ?? new List<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(ProfileExchange.MaxHostsPerProfile).ToList();
                clean.Add(p);
                if (clean.Count == MaxProfiles) break;
            }

            return clean;
        }
    }
}
