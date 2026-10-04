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

            string? label = o.Label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Any(char.IsControl)) label = null;
            else if (label.Length > MaxLabelLength) label = label[..MaxLabelLength].TrimEnd();

            var clean = new TargetOptions(label,
                o.SlowMs is int s ? Limits.Clamp(s, Limits.DegradedLatencyMs) : null,
                o.LossPercent is int l ? Limits.Clamp(l, Limits.DegradedLossPercent) : null,
                o.DownAfter is int d ? Limits.Clamp(d, Limits.DownAfter) : null);
            return clean.IsEmpty ? null : clean;
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

        // "slow 20 ms, loss 10 %, down after 2" for the report; "global limits" when it has none of its own.
        public static string DescribeLimits(TargetOptions? o)
        {
            var parts = new List<string>();
            if (o?.SlowMs is int s) parts.Add($"slow above {s} ms");
            if (o?.LossPercent is int l) parts.Add($"loss at least {l} %");
            if (o?.DownAfter is int d) parts.Add($"down after {d}");
            return parts.Count == 0 ? "global limits" : string.Join(", ", parts);
        }
    }
}
