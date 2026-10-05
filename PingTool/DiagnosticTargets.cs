using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // What the network card says about itself, as far as the diagnosis needs it.
    internal sealed record NicSnapshot(string Name, bool IsUp, bool IsVirtualOrLoopback, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> DnsServers);

    // "Diagnose my connection": the targets that let the report say WHERE the fault is (this PC, the router, the provider, the
    // Internet) for someone who does not know their gateway or DNS server addresses: the default gateway and the DNS servers read
    // from the active network card, two well-known Internet addresses, a name lookup and a public web address.
    internal static class DiagnosticTargets
    {
        public const string ProfileName = "Diagnose my connection";
        public const int MaxDnsServers = 2;

        // Internet references that do not depend on the user's provider (anycast, run by large operators).
        public static readonly string[] InternetReferences = { "1.1.1.1", "8.8.8.8", "dns://www.cloudflare.com", "https://www.cloudflare.com/" };

        // The list of targets, local ones first, no duplicates. gatewayFound says whether the first one is a real gateway.
        public static List<string> From(IEnumerable<NicSnapshot> nics, out bool gatewayFound)
        {
            var targets = new List<string>();
            gatewayFound = false;

            // The first card that is up, is a real one, and has an IPv4 gateway: the one the traffic leaves by.
            var nic = nics.FirstOrDefault(n => n.IsUp && !n.IsVirtualOrLoopback && n.Gateways.Any(IsUsableGateway));
            if (nic is not null)
            {
                gatewayFound = true;
                targets.Add(nic.Gateways.First(IsUsableGateway).ToString());
                foreach (var dns in nic.DnsServers.Where(IsUsableDns).Take(MaxDnsServers)) targets.Add(dns.ToString());
            }

            targets.AddRange(InternetReferences);
            return targets.Distinct(TargetKey.Comparer).ToList();
        }

        // Said wherever the diagnosis targets are built (the profile, --diagnose, --headless --diagnose): one wording.
        public const string NoGatewayMessage = "No network gateway was found (is the PC connected?). Only the Internet references are in the list: without the router in it, the report cannot say whether the fault is on your side.";

        public static List<string> Discover(out bool gatewayFound)
        {
            var snapshots = new List<NicSnapshot>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var props = nic.GetIPProperties();
                    snapshots.Add(new NicSnapshot(
                        nic.Name,
                        nic.OperationalStatus == OperationalStatus.Up,
                        nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel,
                        props.GatewayAddresses.Select(g => g.Address).ToList(),
                        props.DnsAddresses.ToList()));
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Network cards could not be read: " + ex.Message);
            }

            return From(snapshots, out gatewayFound);
        }

        // 0.0.0.0 is "no gateway"; only IPv4 ones are used (the targets of the list are IPv4 addresses or names).
        private static bool IsUsableGateway(IPAddress a) =>
            a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !IPAddress.IsLoopback(a);

        // A resolver on the PC itself (127.0.0.53 of a Linux stub, ::1) says nothing about the network; IPv6 and link-local
        // (fe80::) servers are left out: the targets are IPv4.
        private static bool IsUsableDns(IPAddress a) =>
            a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any) && !IPAddress.IsLoopback(a);
    }
}
