using System.Drawing;
using System.Net;
using System.Net.Sockets;

namespace PingTool
{
    // What the comparison needs to know about one monitored host.
    internal sealed record Target(string Name, IPAddress? Ip, HostState State, bool HasData);

    // Reads the states of SEVERAL targets side by side and says where the fault
    // most likely is. It is a hint from the pattern, not a proof: pinging cannot
    // tell a dead service from a firewall rule, and the sentence says so ("likely").
    internal static class Diagnosis
    {
        private const int MaxNames = 3;

        private static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);

        // Private, loopback and link-local ranges: reachable without the Internet.
        public static bool IsLocal(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254);
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }

        // null = nothing useful to say (fewer than two targets have answered or timed out yet).
        public static string? For(IReadOnlyList<Target> all)
        {
            // A loopback target (127.0.0.1, ::1) answers from this PC's own stack whatever its network card does: it proves nothing about
            // the link, the router or the Internet, and would make "Local targets answer" true with the cable unplugged. It is left
            // out of the comparison (the window still shows its state).
            var targets = all.Where(t => t.HasData && !(t.Ip is { } ip && IsLoopback(ip))).ToList();
            int n = targets.Count;
            if (n < 2) return null;

            var down = targets.Where(t => t.State == HostState.Down).ToList();
            var slow = targets.Where(t => t.State == HostState.Degraded).ToList();

            if (down.Count == 0)
            {
                if (slow.Count == 0) return $"All {n} targets answer normally.";
                if (slow.Count == n) return $"All {n} targets are slow or losing packets: likely this PC's link or the path they share.";
                return $"{Names(slow)} slow or losing packets while {Others(n - slow.Count, "is fine", "are fine")}: likely specific to {Specific(slow.Count)}.";
            }

            if (down.Count == n)
                // Possibilities, not a verdict: this PC, its link, or a path (or a fault) the targets share can each give this; the targets
                // may even be one device. Ruling the targets out would need independent ones and a check point that answers.
                return $"All {n} targets are down: this PC, its network link, or something the targets share (a path, a provider) can be the cause; pinging alone cannot tell which.";

            // Some are down and EVERY other one is slow or losing packets: nothing answers well, whatever the target: that points at what they
            // share (this PC's link, the path), not at the one that is down. (It used to say that the others "answer".)
            if (slow.Count > 0 && down.Count + slow.Count == n)
                return $"{down.Count} of {n} targets {(down.Count == 1 ? "is" : "are")} down and the other {(slow.Count == 1 ? "one is" : slow.Count + " are")} slow or losing packets: likely this PC's link or the path they share.";

            var local = targets.Where(t => t.Ip is not null && IsLocal(t.Ip)).ToList();
            var remote = targets.Where(t => t.Ip is not null && !IsLocal(t.Ip)).ToList();

            // A target whose address is not known (a local name that does not resolve, "nas.local") is neither local nor Internet:
            // counting it as Internet would blame the router or the link for a device that may be off. Without an address for
            // every target the local / Internet reading is not made, and the general sentence below is used.
            bool allKnown = targets.All(t => t.Ip is not null);
            if (allKnown && local.Count > 0 && remote.Count > 0)
            {
                if (local.All(t => t.State != HostState.Down) && remote.All(t => t.State == HostState.Down))
                    return "Local targets answer but every Internet target is down: likely the router or the Internet link.";
                if (local.All(t => t.State == HostState.Down) && remote.All(t => t.State != HostState.Down))
                    return "Internet targets answer but the local ones are down: likely those local devices.";
            }

            // Slow ones among the rest are said, not counted as answering normally.
            if (slow.Count > 0)
                return $"Only {Names(down)} down and {Names(slow)} slow or losing packets while {Others(n - down.Count - slow.Count, "answers", "answer")}: likely specific to {Specific(down.Count + slow.Count)}.";

            return $"Only {Names(down)} down while {Others(n - down.Count, "answers", "answer")}: likely specific to {Specific(down.Count)}.";
        }

        // "that target or its path" / "those targets or their paths": the possessive follows the number too.
        private static string Specific(int count) => count == 1 ? "that target or its path" : "those targets or their paths";

        private static string Others(int count, string one, string many) =>
            count == 1 ? $"the other target {one}" : $"the other {count} targets {many}";

        private static string Names(List<Target> list)
        {
            var shown = list.Take(MaxNames).Select(t => t.Name).ToList();
            string text = string.Join(", ", shown);
            if (list.Count > MaxNames) text += $" +{list.Count - MaxNames}";
            return text + (list.Count == 1 ? " is" : " are");
        }
    }

    // One colour per host on the comparison graph. Okabe-Ito colours that stay
    // apart for the common colour-blindness types, all at least 3:1 on the graph's
    // 40,40,40 background; they repeat after six hosts, so the legend carries the names.
    internal static class HostPalette
    {
        private static readonly Color[] Colors =
        {
            Color.FromArgb(86, 180, 233),   // sky blue
            Color.FromArgb(230, 159, 0),    // orange
            Color.FromArgb(0, 158, 115),    // bluish green
            Color.FromArgb(240, 228, 66),   // yellow
            Color.FromArgb(204, 121, 167),  // reddish purple
            Color.FromArgb(213, 94, 0),     // vermillion
        };

        public static int Count => Colors.Length;

        public static Color ColorFor(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
    }
}
