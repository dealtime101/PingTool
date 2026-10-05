using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // What the network card says about itself, as far as the diagnosis needs it.
    // Addresses = the card's own IPv4 addresses: what tells which card the system routes the Internet traffic through.
    internal sealed record NicSnapshot(string Name, bool IsUp, bool IsVirtualOrLoopback, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> DnsServers,
        IReadOnlyList<IPAddress>? Addresses = null);

    // "Diagnose my connection": the targets that let the report say WHERE the fault is (this PC, the router, the provider, the
    // Internet) for someone who does not know their gateway or DNS server addresses: the default gateway and the DNS servers read
    // from the active network card, two well-known Internet addresses, a name lookup and a public web address.
    internal static class DiagnosticTargets
    {
        public const string ProfileName = "Diagnose my connection";
        public const int MaxDnsServers = 2;

        // Internet references that do not depend on the user's provider (anycast, run by large operators).
        public static readonly string[] InternetReferences = { "1.1.1.1", "8.8.8.8", "dns://www.cloudflare.com", "https://www.cloudflare.com/" };

        // The same, for a PC that has no IPv4 gateway but an IPv6 one (an IPv6-only connection): the two literal addresses are IPv6 ones
        // (they would only fail there, and a failure of those proves nothing about the connection), the name lookup and the web address are
        // the same, a name reaches whichever family there is.
        public static readonly string[] InternetReferencesIPv6 = { "2606:4700:4700::1111", "2001:4860:4860::8888", "dns://www.cloudflare.com", "https://www.cloudflare.com/" };

        // The list of targets, local ones first, no duplicates. gatewayFound says whether the first one is a real gateway.
        public static List<string> From(IEnumerable<NicSnapshot> nics, out bool gatewayFound, IPAddress? routedFrom = null)
        {
            var targets = new List<string>();
            gatewayFound = false;

            // The card the traffic leaves by: among the cards that are up, real, and have an IPv4 gateway, the one that holds the address
            // the system routes the Internet from (routedFrom: the system has weighed the routes and their metrics, the list of cards is
            // in no such order: a Wi-Fi and an Ethernet card up together, or a VPN adapter, would otherwise give the gateway of the wrong one).
            // Without that address (no route, or not asked) it is the first of them, as it always was.
            var usable = nics.Where(n => n.IsUp && !n.IsVirtualOrLoopback && n.Gateways.Any(IsUsableIPv4)).ToList();
            var nic = (routedFrom is null ? null : usable.FirstOrDefault(n => n.Addresses is not null && n.Addresses.Any(a => a.Equals(routedFrom))))
                ?? usable.FirstOrDefault();
            if (nic is not null)
            {
                gatewayFound = true;
                targets.Add(nic.Gateways.First(IsUsableIPv4).ToString());
                foreach (var dns in nic.DnsServers.Where(IsUsableIPv4).Take(MaxDnsServers)) targets.Add(dns.ToString());
            }
            else if (nics.FirstOrDefault(n => n.IsUp && !n.IsVirtualOrLoopback && n.Gateways.Any(IsUsableIPv6) && !n.DnsServers.Any(IsUsableIPv4)) is { } v6)
            {
                // An IPv6-only connection: no IPv4 gateway anywhere, a real card with an IPv6 one and no IPv4 DNS server (one that has
                // IPv4 DNS servers is handled below: those are tested). The gateway is the router; its address keeps its zone
                // (fe80::1%4: a link-local address means nothing without it), and the card's IPv6 DNS servers follow.
                gatewayFound = true;
                targets.Add(v6.Gateways.First(IsUsableIPv6).ToString());
                foreach (var dns in v6.DnsServers.Where(IsUsableIPv6).Take(MaxDnsServers)) targets.Add(dns.ToString());
                targets.AddRange(InternetReferencesIPv6);
                return targets.Distinct(TargetKey.Comparer).ToList();
            }
            else
            {
                // No IPv4 gateway on a real card (an IPv6-only gateway, or none seen): the DNS servers of the active real card are
                // still worth testing, or the report can no longer tell a fault of the provider's DNS from the rest. gatewayFound stays
                // false: there is no router in the list, and the caller says so.
                var realUp = nics.Where(n => n.IsUp && !n.IsVirtualOrLoopback && n.DnsServers.Any(IsUsableIPv4)).ToList();
                var dnsCard = (routedFrom is null ? null : realUp.FirstOrDefault(n => n.Addresses is not null && n.Addresses.Any(a => a.Equals(routedFrom))))
                    ?? realUp.FirstOrDefault();
                if (dnsCard is not null)
                    foreach (var dns in dnsCard.DnsServers.Where(IsUsableIPv4).Take(MaxDnsServers)) targets.Add(dns.ToString());
            }

            targets.AddRange(InternetReferences);
            return targets.Distinct(TargetKey.Comparer).ToList();
        }

        // Said wherever the diagnosis targets are built (the profile, --diagnose, --headless --diagnose): one wording.
        public const string NoGatewayMessage = "No network gateway was found (is the PC connected?). Only the Internet references are in the list: without the router in it, the report cannot say whether the fault is on your side.";

        public static List<string> Discover(out bool gatewayFound) => Discover(CardReaders(), out gatewayFound, LocalAddressToInternet());

        // The address the system would send from to reach the Internet: a UDP socket "connected" to a public address only asks the
        // routing table (nothing is sent), and tells the local address it chose. null when there is no route.
        internal static IPAddress? LocalAddressToInternet()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(IPAddress.Parse("1.1.1.1"), 53);
                return (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        // One reader per card, each run on its own: a card whose properties cannot be read (a virtual adapter, a faulty driver) is
        // left out, and the real card next to it is still used. The list of cards itself failing leaves no card, as before.
        internal static List<NicSnapshot> ReadCards(IEnumerable<Func<NicSnapshot>> readers)
        {
            var snapshots = new List<NicSnapshot>();
            foreach (var read in readers)
            {
                try
                {
                    snapshots.Add(read());
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
                {
                    System.Diagnostics.Debug.WriteLine("A network card could not be read: " + ex.Message);
                }
            }

            return snapshots;
        }

        internal static List<string> Discover(IEnumerable<Func<NicSnapshot>> readers, out bool gatewayFound, IPAddress? routedFrom = null) =>
            From(ReadCards(readers), out gatewayFound, routedFrom);

        // Words of a card's description that say it is not a piece of hardware: the virtual switches of Hyper-V, VirtualBox, VMware and
        // Docker, the tunnel adapters of VPNs, the pseudo-interfaces. Most of them declare the type "Ethernet", so the type alone lets them
        // through. "Hyper-V" is NOT in the list on its own: "Microsoft Hyper-V Network Adapter" is the real card of a virtual machine.
        private static readonly string[] VirtualWords =
        {
            "virtual", "vethernet", "tap-windows", "tap-win", "wintun", "wireguard", "openvpn", "nordlynx", "tailscale", "zerotier", "hamachi",
            "docker", "vmnet", "vboxnet", "loopback", "pseudo", "npcap", "teredo", "isatap", "6to4", "miniport",
        };

        // Names the system gives its virtual interfaces (Linux and macOS: docker0, veth…, virbr0, br-…, tun0, tap0, wg0, utun0, lo).
        // Long or distinctive prefixes: whatever follows (docker0, veth9a9ac9f, br-e4de740698dd, virbr0). Short ones: digits only
        // (tun0, tap1, wg0, utun3, lo), so that "Tunnel to the office" and "lounge" are not taken for them.
        private static readonly string[] VirtualNamePrefixes = { "docker", "veth", "virbr", "br-", "vmnet", "vboxnet" };
        private static readonly string[] VirtualShortNames = { "tun", "tap", "wg", "utun", "lo" };

        internal static bool LooksVirtual(string name, string description, NetworkInterfaceType type)
        {
            if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return true;
            if (VirtualWords.Any(w => description.Contains(w, StringComparison.OrdinalIgnoreCase) || name.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
            return VirtualNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || VirtualShortNames.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase) && name[p.Length..].All(char.IsDigit));
        }

        private static List<Func<NicSnapshot>> CardReaders()
        {
            var readers = new List<Func<NicSnapshot>>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var card = nic;   // each reader keeps its own card
                    readers.Add(() =>
                    {
                        var props = card.GetIPProperties();
                        return new NicSnapshot(
                            card.Name,
                            card.OperationalStatus == OperationalStatus.Up,
                            LooksVirtual(card.Name, card.Description, card.NetworkInterfaceType),
                            props.GatewayAddresses.Select(g => g.Address).ToList(),
                            props.DnsAddresses.ToList(),
                            props.UnicastAddresses.Select(u => u.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList());
                    });
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Network cards could not be listed: " + ex.Message);
            }

            return readers;
        }

        // The one rule for a gateway AND for a DNS server (they were two copies of the same line): an IPv4 address that is not 0.0.0.0
        // ("no gateway") and not the PC itself (the 127.0.0.53 stub resolver of Linux, ::1, say nothing about the network). IPv6 and
        // link-local (fe80::) addresses are left out: the targets of the list are IPv4 addresses or names.
        private static bool IsUsableIPv4(IPAddress a) =>
            a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !IPAddress.IsLoopback(a);

        // The IPv6 counterpart, for a connection that has no IPv4 at all: not "::" (no gateway), not ::1. Link-local (fe80::%zone) is
        // kept, a router announces itself that way, with the zone the address carries.
        private static bool IsUsableIPv6(IPAddress a) =>
            a.AddressFamily == AddressFamily.InterNetworkV6 && !a.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(a);
    }
}
