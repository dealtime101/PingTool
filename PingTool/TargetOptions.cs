namespace PingTool
{
    // What belongs to ONE target besides its address: a readable name ("Box", "Office VPN") and limits of its own, which replace the
    // global ones for that target only. A router at 2 ms and a server overseas at 180 ms do not share the same "normal".
    // null = use the global value.
    internal sealed record TargetOptions(string? Label, int? SlowMs, int? LossPercent, int? DownAfter)
    {
        public const int MaxLabelLength = 40;
        public const int MaxEntries = 100;

        public bool IsEmpty => Label is null && SlowMs is null && LossPercent is null && DownAfter is null;

        // Trimmed name (blank or with control characters = none), limits pulled into the range of the boxes; null when nothing is left.
        public static TargetOptions? Normalize(TargetOptions? o)
        {
            if (o is null) return null;

            // The invisible FORMAT characters (a right-to-left override, a zero width space...) are taken out, as for the name of a profile
            // (the joiners that hold an emoji together stay): a name that reads the other way round, or two names that look alike and are
            // not, would make one target pass for another in the list, the alerts and the report.
            string? label = o.Label is null ? null : ProfileBook.WithoutInvisibleFormat(o.Label).Trim();
            if (string.IsNullOrEmpty(label) || label.Any(char.IsControl)) label = null;
            else if (label.Length > MaxLabelLength) label = CutToUnits(label, MaxLabelLength).TrimEnd();

            var clean = new TargetOptions(label,
                o.SlowMs is int s ? Limits.Clamp(s, Limits.DegradedLatencyMs) : null,
                o.LossPercent is int l ? Limits.Clamp(l, Limits.DegradedLossPercent) : null,
                o.DownAfter is int d ? Limits.Clamp(d, Limits.DownAfter) : null);
            return clean.IsEmpty ? null : clean;
        }

        // At most maxUnits UTF-16 units (what the name box allows), cut BETWEEN characters as they are shown: a cut through the two halves
        // of an emoji (a surrogate pair), or between a letter and its accent, would leave a lone half that displays as a lozenge or a "?",
        // and that a JSON or UTF-8 writer may refuse or replace.
        internal static string CutToUnits(string text, int maxUnits)
        {
            if (text.Length <= maxUnits) return text;
            var starts = System.Globalization.StringInfo.ParseCombiningCharacters(text);
            int cut = 0;
            foreach (int s in starts) if (s <= maxUnits) cut = s;   // the last character boundary that does not go over
            if (cut == 0)
            {
                // A single character longer than the limit (a long sequence of marks): no boundary to use, but never inside a pair.
                cut = maxUnits;
                if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            }

            return text[..cut];
        }

        // What a file may hold: keys that are valid targets, values that say something, at most MaxEntries, one per target (TargetKey: the case of a URL path counts).
        public static Dictionary<string, TargetOptions> Clean(IDictionary<string, TargetOptions?>? raw)
        {
            var clean = new Dictionary<string, TargetOptions>(TargetKey.Comparer);
            foreach (var (key, value) in raw ?? new Dictionary<string, TargetOptions?>())
            {
                string address = (key ?? "").Trim();
                if (!ProbeTarget.TryParse(address, out _, out _) || clean.ContainsKey(address)) continue;
                if (Normalize(value) is { } options) clean[address] = options;
                if (clean.Count >= MaxEntries) break;
            }

            return clean;
        }

        // The name shown for a target: its label, or its address.
        public static string DisplayName(string address, TargetOptions? o) => o?.Label ?? address;

        // The limits a target runs with: its own where it has one, the global ones otherwise.
        public static (int SlowMs, int LossPercent, int DownAfter) Effective(TargetOptions? o, int slowMs, int lossPercent, int downAfter) =>
            (o?.SlowMs ?? slowMs, o?.LossPercent ?? lossPercent, o?.DownAfter ?? downAfter);

        // "slow above 20 ms, loss at least 10 %, down after 2 failed pings in a row" for the report (only the limits the target has of its own, in that order);
        // "global limits" when it has none.
        public static string DescribeLimits(TargetOptions? o)
        {
            var parts = new List<string>();
            if (o?.SlowMs is int s) parts.Add($"slow above {s} ms");
            if (o?.LossPercent is int l) parts.Add($"loss at least {l} %");
            if (o?.DownAfter is int d) parts.Add($"down after {d} failed ping{(d == 1 ? "" : "s")} in a row");   // the unit, as the report row "Down when" says it
            return parts.Count == 0 ? "global limits" : string.Join(", ", parts);
        }
    }
}
