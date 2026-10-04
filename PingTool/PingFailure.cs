using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // Why a ping failed. Short fits the big result label (about 8 characters
    // at 32 pt), Detail is the full sentence for the tooltip and the CSV log.
    internal sealed record PingFailure(string Short, string Detail)
    {
        public static readonly PingFailure Timeout = new("Timeout", "No reply within the timeout");

        // family = the address family of the target, when known: status 11004 means two different
        // things depending on it (see ProtocolOrProhibited).
        public static PingFailure From(IPStatus status, AddressFamily? family = null) => status switch
        {
            IPStatus.TimedOut => Timeout,
            IPStatus.DestinationNetworkUnreachable => new("No route", "Destination network unreachable"),
            IPStatus.DestinationHostUnreachable => new("Unreach", "Destination host unreachable"),
            IPStatus.DestinationUnreachable => new("Unreach", "Destination unreachable"),
            IPStatus.DestinationProtocolUnreachable => ProtocolOrProhibited(family),
            IPStatus.DestinationPortUnreachable => new("Refused", "Destination port unreachable"),
            IPStatus.DestinationScopeMismatch => new("Bad dest", "Source and destination address scopes do not match"),
            IPStatus.BadDestination => new("Bad dest", "Bad destination address"),
            IPStatus.PacketTooBig => new("Too big", "Packet too big for the path: lower the packet size"),
            // ICMP "Time Exceeded" has two causes. .NET names them apart for IPv4: the TTL ran out on
            // the way, or the fragments of a packet did not all arrive in time (a different problem:
            // nothing to do with the number of hops).
            IPStatus.TtlExpired => new("TTL", "TTL expired in transit"),
            IPStatus.TtlReassemblyTimeExceeded
                => new("Frag", "Fragment reassembly time exceeded: the pieces of the packet did not all arrive in time (not a hop-count problem)"),
            // The IPv6 status covers both causes without saying which; the TTL is the usual one.
            IPStatus.TimeExceeded
                => new("TTL", "Time exceeded in transit: usually the TTL (hop limit) ran out; this status can also mean a fragment-reassembly timeout"),
            IPStatus.NoResources => new("Busy", "Not enough resources on the path"),
            // Source Quench can come from ANY router on the way, not only from the host that was pinged.
            // It is also obsolete (RFC 6633): seeing it at all is unusual.
            IPStatus.SourceQuench => new("Busy", "A router on the path, or the destination, asked the sender to slow down (ICMP source quench, obsolete). The host you pinged is not necessarily the one that asked."),
            _ => new("Error", "ICMP error: " + status),
        };

        // .NET gives ONE status code (11004, named both DestinationProtocolUnreachable and
        // DestinationProhibited) to two different errors: ICMPv4 "protocol unreachable" (the host
        // does not implement the protocol: not a filtering rule at all) and ICMPv6 "communication
        // administratively prohibited" (a filtering rule). Saying "refused" for both pointed IPv4
        // users at a firewall that is not the cause. The address family tells them apart.
        private static PingFailure ProtocolOrProhibited(AddressFamily? family) => family switch
        {
            AddressFamily.InterNetwork
                => new("No proto", "The destination host does not support the protocol (ICMP protocol unreachable). This is not a filtering rule."),
            AddressFamily.InterNetworkV6
                => new("Blocked", "Communication with the destination is administratively prohibited: a filtering rule is in the way."),
            _ => new("Rejected", "The destination reports the protocol as unreachable or the traffic as prohibited (one status code covers both). If the host is known to be up, look for a filtering rule."),
        };

        public static PingFailure From(Exception ex)
        {
            // Ping wraps the socket error that explains it.
            var socket = ex as SocketException ?? ex.InnerException as SocketException;
            return socket?.SocketErrorCode switch
            {
                SocketError.HostNotFound
                    => new("No host", "Name could not be resolved"),
                // The name EXISTS but has no address of the kind asked for (no IPv6 record, say): calling it "no host" would send the
                // user to correct a good name.
                SocketError.NoData
                    => new("No addr", "The name exists but has no address of the requested type (for example no IPv6 record)"),
                // A passing failure of the DNS server, not a wrong name: say so, or the user "fixes" a good name.
                SocketError.TryAgain
                    => new("DNS fail", "The DNS server failed for the moment (temporary): the name may be right, try again"),
                // The same two words as the ICMP statuses above, so one fault has one label whether it came as an
                // exception or as a reply (the big label and the CSV can then be filtered on it).
                SocketError.NetworkUnreachable
                    => new("No route", "Destination network unreachable: " + socket.Message),
                SocketError.HostUnreachable
                    => new("Unreach", "Destination host unreachable: " + socket.Message),
                SocketError.ConnectionRefused
                    => new("Refused", "Connection refused: nothing is listening on that port"),
                SocketError.TimedOut => Timeout,
                SocketError.ConnectionReset
                    => new("Reset", "The connection was closed by the other side"),
                // Aborted is the OTHER direction: the software of THIS computer (a firewall, an antivirus, a time limit of its own, a
                // network change) cut the connection. Saying "the other side closed it" would send the user to the wrong machine.
                SocketError.ConnectionAborted
                    => new("Aborted", "The connection was aborted on this computer (its software or network settings), not necessarily by the other side"),
                // A deadline that ran out is a Timeout whether the socket reported it or the HTTP layer did (a request that exceeds
                // HttpClient.Timeout is a TaskCanceledException WITH a TimeoutException inside). A bare cancellation is not one:
                // that is somebody pressing Stop.
                _ when ex is TimeoutException || ex.InnerException is TimeoutException => Timeout,
                _ when ex is ArgumentException => new("Bad addr", "Invalid address: " + ex.Message),
                // Raised by SslStream itself, or wrapped by the HTTP layer: both levels, as for the socket error above.
                _ when (ex as System.Security.Authentication.AuthenticationException ?? ex.InnerException as System.Security.Authentication.AuthenticationException) is { } tls
                    => new("TLS", "The secure connection could not be set up: " + tls.Message),
                _ => new("Error", ex.InnerException?.Message ?? ex.Message),
            };
        }
    }
}
