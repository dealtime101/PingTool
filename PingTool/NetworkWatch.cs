using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // A change of the PC's own network during a run (a Wi-Fi access point, a VPN, a cable unplugged, a wake from sleep),
    // dated, so that a cut seen by the pings can be put next to what happened on this side.
    internal sealed record NetworkEvent(DateTimeOffset Time, string Text);

    // One network card that is up, as far as the events need it. Addresses = its IPv4 addresses, sorted, joined.
    internal sealed record NicState(string Name, string Addresses, string Gateway);

    internal static class NetworkWatch
    {
        public const int MaxTextLength = 220;

        // The cards that are up and have an IPv4 address (loopback and tunnel pseudo-cards left out: Teredo and 6to4 change
        // for no reason that matters). The Wi-Fi name (SSID) is not read: Windows hands it out through a different interface,
        // and the card name already tells Wi-Fi from Ethernet and from a VPN adapter.
        // null when the cards could not be read (all of them or one: a list with a card missing would be read as that card being
        // unplugged). The caller keeps what it knew and ignores this reading; an EMPTY list means the PC really has no network.
        // read: the reading itself, a parameter so that a failure can be replayed in a test.
        public static List<NicState>? Snapshot(Func<List<NicState>>? read = null)
        {
            try
            {
                return (read ?? ReadCards)();
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Network cards could not be read: " + ex.Message);
                LastReadError = ex.Message;   // Debug output does not exist in a release build: the reason is kept for the window to say
                return null;
            }
        }

        // Why the last reading that failed did. Set by Snapshot (the reading runs on the window's thread).
        public static string? LastReadError { get; private set; }

        // What changed between what the window knew (null = nothing yet) and this reading (null = could not be read): the new "last
        // known" state and the sentence, if any. A failed reading changes nothing in what is known; it is SAID once per run of failures
        // (failureSaid is kept by the caller), so that a person looking for the reason of a missing change finds it in the events.
        // The first reading only sets the base.
        public static (List<NicState>? Last, string? Text) Compare(List<NicState>? last, List<NicState>? now, ref bool failureSaid)
        {
            if (now is null)
            {
                if (failureSaid) return (last, null);
                failureSaid = true;
                string why = LastReadError is { Length: > 0 } reason ? reason.Length <= 80 ? reason : reason[..77] + "..." : "no reason given";
                return (last, $"The network cards could not be read ({why}): changes of the network are not recorded until they can be.");
            }

            failureSaid = false;
            if (last is null) return (now, null);
            return (now, Describe(last, now));
        }

        private static List<NicState> ReadCards()
        {
            var states = new List<NicState>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                var addresses = props.UnicastAddresses.Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).OrderBy(a => a, StringComparer.Ordinal).ToList();
                if (addresses.Count == 0) continue;

                string gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork
                    && !g.Equals(System.Net.IPAddress.Any))?.ToString() ?? "";
                states.Add(new NicState(nic.Name, string.Join(", ", addresses), gateway));
            }

            return states;
        }

        // null when nothing that matters changed; otherwise one line saying what.
        public static string? Describe(IReadOnlyList<NicState> before, IReadOnlyList<NicState> after)
        {
            var b = before.GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var a = after.GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            static string Gw(string g) => g.Length == 0 ? "none" : g;
            var parts = new List<string>();
            foreach (string name in b.Keys.Union(a.Keys).OrderBy(n => n, StringComparer.Ordinal))
            {
                bool had = b.TryGetValue(name, out var was), has = a.TryGetValue(name, out var now);
                if (!had) parts.Add($"{name} connected ({now!.Addresses}, gateway {Gw(now.Gateway)})");
                else if (!has) parts.Add($"{name} disconnected");
                else if (was!.Addresses != now!.Addresses || was.Gateway != now.Gateway)
                {
                    string text = was.Addresses != now.Addresses ? $"{name}: {was.Addresses} -> {now.Addresses}" : $"{name}: {now.Addresses}";
                    if (was.Gateway != now.Gateway) text += $" (gateway {Gw(was.Gateway)} -> {Gw(now.Gateway)})";
                    parts.Add(text);
                }
            }

            if (parts.Count == 0) return null;
            string line = after.Count == 0 ? "Network connection lost; " + string.Join("; ", parts) : string.Join("; ", parts);
            return line.Length <= MaxTextLength ? line : line[..(MaxTextLength - 3)] + "...";
        }
    }
}
