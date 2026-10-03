namespace PingTool
{
    // The sentence under the big result, on hover. One place decides it, so that a stale sentence cannot
    // outlive its value: after Stop then Start the value is "---" and the hint must be empty, not
    // "Stopped: last value, no longer live".
    internal static class ResultHint
    {
        public const string Stopped = "Stopped: last value, no longer live";

        // ping = the last result shown (null = nothing measured yet in this run); stopped = a run happened and is over.
        public static string For(long? ping, PingFailure? failure, bool stopped) =>
            ping is null ? "" : stopped ? Stopped : failure?.Detail ?? "";
    }
}
