using System.Drawing;
using System.Net;
using System.Net.Sockets;

namespace PingTool
{
    // What the comparison needs to know about one monitored host.
    // OnLink: the address is in the network of one of this PC's own cards (same prefix): it is reached without the Internet even when
    // its address is a global one (a LAN machine with a global IPv6 address, a public IPv4 range handed out on the LAN).
    internal sealed record Target(string Name, IPAddress? Ip, HostState State, bool HasData, bool OnLink = false);

    // The networks this PC is directly attached to, read from its cards (address and prefix length), kept for a while: the diagnosis is
    // asked for at every refresh and the cards do not change that often.
    internal static class LocalNetworks
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

        // A seam for the tests: what the cards say.
        internal static Func<IReadOnlyList<(IPAddress Address, int Prefix)>> Read = ReadCards;
        private static IReadOnlyList<(IPAddress Address, int Prefix)>? cached;
        private static DateTime cachedAt;
        private static readonly object gate = new();

        private static IReadOnlyList<(IPAddress Address, int Prefix)> ReadCards()
        {
            var found = new List<(IPAddress, int)>();
            try
            {
                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                        || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var u in nic.GetIPProperties().UnicastAddresses)
                        if (u.PrefixLength > 0) found.Add((u.Address, u.PrefixLength));
                }
            }
            catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or PlatformNotSupportedException)
            {
                // Cards that cannot be read: nothing is known to be on the link, the address ranges alone decide (as before).
            }

            return found;
        }

        // True when `ip` is in the same network as one of the (address, prefix) pairs: same family and the same first `prefix` bits.
        internal static bool Contains(IPAddress ip, IEnumerable<(IPAddress Address, int Prefix)> networks)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            byte[] target = ip.GetAddressBytes();
            foreach (var (address, prefix) in networks)
            {
                var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                byte[] mine = a.GetAddressBytes();
                // A prefix shorter than 8 bits would take in a large part of the Internet (a tunnel's odd mask): not a local network.
                if (a.AddressFamily != ip.AddressFamily || prefix < 8 || prefix > mine.Length * 8) continue;

                int whole = prefix / 8, rest = prefix % 8;
                bool same = target.AsSpan(0, whole).SequenceEqual(mine.AsSpan(0, whole));
                if (same && rest > 0)
                {
                    int mask = 0xFF << (8 - rest) & 0xFF;
                    same = (target[whole] & mask) == (mine[whole] & mask);
                }

                if (same) return true;
            }

            return false;
        }

        public static bool IsOnLink(IPAddress ip)
        {
            IReadOnlyList<(IPAddress Address, int Prefix)> networks;
            lock (gate)
            {
                if (cached is null || DateTime.UtcNow - cachedAt > Lifetime) { cached = Read(); cachedAt = DateTime.UtcNow; }
                networks = cached;
            }

            return Contains(ip, networks);
        }

        internal static void Forget() { lock (gate) cached = null; }
    }

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

            // Local: a private or link-local address, or one in the network of this PC's own cards (a global address on the LAN).
            var local = targets.Where(t => t.Ip is not null && (t.OnLink || IsLocal(t.Ip))).ToList();
            var remote = targets.Where(t => t.Ip is not null && !(t.OnLink || IsLocal(t.Ip))).ToList();

            // A target whose address is not known (a local name that does not resolve, "nas.local") is neither local nor Internet:
            // counting it as Internet would blame the router or the link for a device that may be off. Without an address for
            // every target the local / Internet reading is not made, and the general sentence below is used.
            bool allKnown = targets.All(t => t.Ip is not null);
            if (allKnown && local.Count > 0 && remote.Count > 0)
            {
                // The number follows the targets, as everywhere else in this file: one router is "the local target", not "the local ones".
                if (local.All(t => t.State != HostState.Down) && remote.All(t => t.State == HostState.Down))
                    return $"{(local.Count == 1 ? "The local target answers" : "Local targets answer")} but {(remote.Count == 1 ? "the Internet target is" : "every Internet target is")} down: likely the router or the Internet link.";
                if (local.All(t => t.State == HostState.Down) && remote.All(t => t.State != HostState.Down))
                    return $"{(remote.Count == 1 ? "The Internet target answers" : "Internet targets answer")} but "
                        + (local.Count == 1 ? "the local target is down: likely that local device." : "the local ones are down: likely those local devices.");
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
