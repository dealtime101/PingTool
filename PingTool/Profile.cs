using System.Globalization;

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
        public Dictionary<string, TargetOptions> TargetOptions { get; set; } = new(TargetKey.Comparer);
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
            else if (name.Any(IsInvisible)) error = "The name cannot contain control or invisible formatting characters.";

            return error.Length == 0;
        }

        // Control characters and the invisible "format" ones (zero width space, bidi marks): "Home" and "Home" + U+200B look
        // the same on screen but would be two profiles.
        private static bool IsInvisible(char c) => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format;

        // True when the list on screen is what the saved profile holds (same targets, any order, same rule as "the same
        // target" everywhere). No saved profile means the list has not been saved: it is only "the same" when it is empty.
        public static bool SameTargets(IEnumerable<string> current, Profile? saved) =>
            new HashSet<string>(current, TargetKey.Comparer).SetEquals(saved?.Hosts ?? new List<string>());

        public static Profile? Find(IEnumerable<Profile> book, string? name) =>
            book.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        // Replaces the profile of the same name where it is, or appends. False when the book is full
        // (and the name is new): the caller says so, nothing is lost silently.
        public static bool Upsert(List<Profile> book, Profile profile)
        {
            profile.Name = profile.Name.Trim();   // Find and Remove compare trimmed: the same profile must not be added twice
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
        // leftOut: when given, receives one line per profile that was not kept and why, so that the caller can say it (nothing is lost
        // silently: a profile of a hand-edited file that vanishes with all its hosts, with no word, is worse than a refusal).
        public static List<Profile> Sanitize(IEnumerable<Profile?>? loaded, List<string>? leftOut = null)
        {
            var clean = new List<Profile>();
            foreach (var p in loaded ?? Enumerable.Empty<Profile?>())
            {
                if (p is null) { leftOut?.Add("an empty entry"); continue; }
                // A profile saved before the format characters were refused keeps its place, under the name as it looked.
                p.Name = string.Concat((p.Name ?? "").Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format));
                if (!TryName(p.Name, out string name, out string why))
                {
                    leftOut?.Add($"\"{Clip(p.Name)}\": {why.TrimEnd('.')}");
                    continue;
                }

                if (Find(clean, name) is not null)
                {
                    leftOut?.Add($"\"{Clip(name)}\": another profile already has that name");
                    continue;
                }

                if (clean.Count >= MaxProfiles)
                {
                    leftOut?.Add($"\"{Clip(name)}\": at most {MaxProfiles} profiles are kept");
                    continue;
                }

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
                    .Distinct(TargetKey.Comparer).Take(ProfileExchange.MaxHostsPerProfile).ToList();
                clean.Add(p);
            }

            return clean;
        }

        private static string Clip(string text) => text.Length <= 20 ? text : text[..20] + "...";

        // The sentence for the start-up message: how many, then the first reasons (three at most, the rest counted).
        public static string DescribeLeftOut(IReadOnlyList<string> leftOut) =>
            $"{leftOut.Count} profile(s) in settings.json were left out: {string.Join("; ", leftOut.Take(3))}{(leftOut.Count > 3 ? $" (and {leftOut.Count - 3} more)" : "")}.";
    }
}
