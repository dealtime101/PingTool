namespace PingTool
{
    // THE rule for "the same target": scheme and host (or whole address, for a ping, tcp:// or dns:// target) ignore the case,
    // the path and query of an http(s) URL do not: https://site/Status and https://site/status can be two resources (PIN461.89, .153, .180).
    internal sealed class TargetKey : IEqualityComparer<string>
    {
        public static readonly TargetKey Comparer = new();

        // Where the host of an http(s) address ends: built once (the key is computed for every comparison of two addresses), not a new
        // array at each call.
        private static readonly System.Buffers.SearchValues<char> HostEnd = System.Buffers.SearchValues.Create("/?#");

        public static string Of(string? address)
        {
            string text = (address ?? "").Trim();
            int sep = text.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0) return text.ToLowerInvariant();

            string scheme = text[..sep].ToLowerInvariant();
            if (scheme is not ("http" or "https")) return text.ToLowerInvariant();

            string rest = text[(sep + 3)..];
            int end = rest.AsSpan().IndexOfAny(HostEnd);
            return end < 0 ? scheme + "://" + rest.ToLowerInvariant() : scheme + "://" + rest[..end].ToLowerInvariant() + rest[end..];
        }

        public bool Equals(string? a, string? b) => string.Equals(Of(a), Of(b), StringComparison.Ordinal);

        public int GetHashCode(string address) => Of(address).GetHashCode();

        public static bool Same(string? a, string? b) => Comparer.Equals(a, b);
    }
}
