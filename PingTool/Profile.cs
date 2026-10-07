using System.Globalization;
using System.Text;

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
        public double DegradedMos { get; set; }   // 0 = not used (see Settings)
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
            // The length is what the user sees, in characters as they are shown ("👨‍👩‍👧" is one), not in UTF-16 units (that one is eight).
            else if (new StringInfo(name).LengthInTextElements > MaxNameLength) error = $"The name is limited to {MaxNameLength} characters.";
            else if (HasInvisible(name)) error = "The name cannot contain control or invisible formatting characters.";

            return error.Length == 0;
        }

        // Control characters and the invisible "format" ones (zero width space, bidi marks): "Home" and "Home" + U+200B look
        // the same on screen but would be two profiles. One exception: the zero width joiner INSIDE a character that is drawn as one
        // (an emoji family, a flag with a pride stripe): without it the emoji falls apart. Between two letters it joins nothing and
        // stays refused, so "Ho" + ZWJ + "me" cannot pass for "Home".
        // Read by Unicode characters (runes), not by UTF-16 units: a format character outside the basic plane (U+E0001, the "tag" characters)
        // is two units, and neither half is a format character by itself.
        private static bool HasInvisible(string name)
        {
            int i = 0;
            foreach (var rune in name.EnumerateRunes())
            {
                if (Rune.IsControl(rune)) return true;
                if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format && !IsAllowedFormat(name, i, rune)) return true;
                i += rune.Utf16SequenceLength;
            }

            return false;
        }

        // The format characters that hold one drawn character together and are kept: the zero width joiner (see IsJoiner) and the tag
        // characters of an emoji flag sequence (the flag of England, U+1F3F4 followed by tags), when they follow something in the same text
        // element. A tag character on its own, or after a letter that is not a flag, is as invisible as a zero width space.
        private static bool IsAllowedFormat(string text, int index, Rune rune) =>
            rune.Value == 0x200D ? IsJoiner(text, index)
            : rune.Value is >= 0xE0020 and <= 0xE007F && Holds(text, index, rune.Utf16SequenceLength, mayEnd: true) && ElementStartsWithBlackFlag(text, index);

        // Tags are invisible after a letter too ("Home" + a tag is a second, look-alike name): they are kept only in a flag sequence,
        // whose element begins with U+1F3F4.
        private static bool ElementStartsWithBlackFlag(string text, int index)
        {
            var starts = StringInfo.ParseCombiningCharacters(text);
            int start = starts[Array.FindLastIndex(starts, s => s <= index)];
            return char.IsHighSurrogate(text[start]) && start + 1 < text.Length && char.ConvertToUtf32(text[start], text[start + 1]) == 0x1F3F4;
        }

        // True for a zero width joiner that is inside one text element (neither the first nor the last char of it): the rules of the
        // Unicode text segmentation keep an emoji sequence together across it, and cut after it when it joins nothing.
        internal static bool IsJoiner(string text, int index) => text[index] == '‍' && Holds(text, index, 1, mayEnd: false);

        // The character at `index` (`length` units) is inside a text element, after its first char (and before its last one unless mayEnd).
        private static bool Holds(string text, int index, int length, bool mayEnd)
        {
            var starts = StringInfo.ParseCombiningCharacters(text);
            int element = Array.FindLastIndex(starts, s => s <= index);
            int start = starts[element];
            int end = element + 1 < starts.Length ? starts[element + 1] : text.Length;
            return index > start && (mayEnd || index + length < end);
        }

        // The name as it was written before the format characters were refused: they are taken out, except those that hold an emoji together.
        internal static string WithoutInvisibleFormat(string text)
        {
            var kept = new StringBuilder(text.Length);
            int i = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format || IsAllowedFormat(text, i, rune)) kept.Append(rune.ToString());
                i += rune.Utf16SequenceLength;
            }

            return kept.ToString();
        }

        // True when the list on screen is what the saved profile holds (same targets, any order, same rule as "the same
        // target" everywhere). No saved profile means the list has not been saved: it is only "the same" when it is empty.
        public static bool SameTargets(IEnumerable<string> current, Profile? saved) =>
            new HashSet<string>(current, TargetKey.Comparer).SetEquals(saved?.Hosts ?? new List<string>());

        // The names and limits the window uses for the hosts of a profile that is being loaded: ONLY what that profile defines. The table is
        // by address and shared by every profile, so the options an earlier profile left for the same host must go first, or a profile that
        // says nothing about a host would keep running it with the name and the limits of another one (instead of the global limits).
        public static void ApplyTargetOptions(Dictionary<string, TargetOptions> table, Profile profile)
        {
            foreach (var host in profile.Hosts) table.Remove(host);
            foreach (var (address, options) in profile.TargetOptions) table[address] = options;
        }

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
                p.Name = WithoutInvisibleFormat(p.Name ?? "");
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
                p.DegradedMos = Limits.ClampMos(p.DegradedMos);
                p.DownAfter = Limits.Clamp(p.DownAfter, Limits.DownAfter);
                // Same rules as an imported profile: one entry per address (case ignored), at most MaxHostsPerProfile.
                p.Hosts = (p.Hosts ?? new List<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim())
                    .Distinct(TargetKey.Comparer).Take(ProfileExchange.MaxHostsPerProfile).ToList();
                // The names and limits are those "of some of the hosts above": an entry for an address the profile does not hold (left out,
                // empty, or cut by the limit on hosts) would be applied to the table of the whole window when the profile is loaded.
                var held = new HashSet<string>(p.Hosts, TargetKey.Comparer);
                p.TargetOptions = PingTool.TargetOptions.Clean(p.TargetOptions!)
                    .Where(entry => held.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, TargetKey.Comparer);
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
