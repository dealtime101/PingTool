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
        public static List<NicState> Snapshot()
        {
            var states = new List<NicState>();
            try
            {
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
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Network cards could not be read: " + ex.Message);
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
