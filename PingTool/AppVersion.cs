using System.Reflection;

namespace PingTool
{
    // One source of truth: <Version> in PingTool.csproj, bumped by hand at each delivery.
    // The build appends "+<commit>" (the deploy script passes SourceRevisionId, with
    // "-dirty" when the tree has uncommitted changes), so any copy of PingTool.exe says
    // which commit it was built from.
    internal static class AppVersion
    {
        public static string Full { get; } = ReadFrom(typeof(AppVersion).Assembly);

        // "1.1.0"
        public static string Number => Split(Full).Number;

        // "9d2f2f6" or "9d2f2f6-dirty"; empty when the build did not record a commit.
        public static string Commit => Split(Full).Commit;

        // "1.1.0 (9d2f2f6)" for a report; just "1.1.0" without a commit.
        public static string Display => Format(Full);

        // Window title: "PingTool v1.1.0" or "PingTool v1.1.0 - host". No host is null, empty or blank: no dangling " - ".
        public static string Title(string? host) =>
            string.IsNullOrWhiteSpace(host) ? "PingTool v" + Number : "PingTool v" + Number + " - " + host.Trim();

        public static string ReadFrom(Assembly assembly) =>
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        public static string Format(string info)
        {
            var (number, commit) = Split(info);
            return commit.Length == 0 ? number : number + " (" + commit + ")";
        }

        // "1.1.0+9d2f2f6abc...-dirty" -> ("1.1.0", "9d2f2f6-dirty"). The SDK can append a
        // full 40-character hash on its own; keep 7, as git does.
        public static (string Number, string Commit) Split(string info)
        {
            int plus = info.IndexOf('+', StringComparison.Ordinal);
            if (plus < 0) return (info, "");

            string tail = info[(plus + 1)..];
            int dash = tail.IndexOf('-', StringComparison.Ordinal);
            string hash = dash < 0 ? tail : tail[..dash];
            string suffix = dash < 0 ? "" : tail[dash..];
            return (info[..plus], (hash.Length > 7 ? hash[..7] : hash) + suffix);
        }
    }
}
