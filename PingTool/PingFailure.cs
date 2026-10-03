using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // Why a ping failed. Short fits the big result label (about 8 characters
    // at 32 pt), Detail is the full sentence for the tooltip and the CSV log.
    internal sealed record PingFailure(string Short, string Detail)
    {
        public static readonly PingFailure Timeout = new("Timeout", "No reply within the timeout");

        public static PingFailure From(IPStatus status) => status switch
        {
            IPStatus.TimedOut => Timeout,
            IPStatus.DestinationNetworkUnreachable => new("No route", "Destination network unreachable"),
            IPStatus.DestinationHostUnreachable => new("Unreach", "Destination host unreachable"),
            IPStatus.DestinationUnreachable => new("Unreach", "Destination unreachable"),
            IPStatus.DestinationProtocolUnreachable => new("Refused", "Destination refused the protocol (or prohibited)"),
            IPStatus.DestinationPortUnreachable => new("Refused", "Destination port unreachable"),
            IPStatus.DestinationScopeMismatch => new("Bad dest", "Source and destination address scopes do not match"),
            IPStatus.BadDestination => new("Bad dest", "Bad destination address"),
            IPStatus.PacketTooBig => new("Too big", "Packet too big for the path: lower the packet size"),
            IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded
                => new("TTL", "TTL expired in transit"),
            IPStatus.NoResources => new("Busy", "Not enough resources on the path"),
            IPStatus.SourceQuench => new("Busy", "Destination asked the sender to slow down"),
            _ => new("Error", "ICMP error: " + status),
        };

        public static PingFailure From(Exception ex)
        {
            // Ping wraps the socket error that explains it.
            var socket = ex as SocketException ?? ex.InnerException as SocketException;
            return socket?.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                    => new("No host", "Name could not be resolved"),
                SocketError.NetworkUnreachable or SocketError.HostUnreachable
                    => new("No route", "No route to the destination: " + socket.Message),
                SocketError.ConnectionRefused
                    => new("Refused", "Connection refused: nothing is listening on that port"),
                SocketError.TimedOut => Timeout,
                SocketError.ConnectionReset or SocketError.ConnectionAborted
                    => new("Reset", "The connection was closed by the other side"),
                _ when ex is ArgumentException => new("Bad addr", "Invalid address: " + ex.Message),
                _ when ex.InnerException is System.Security.Authentication.AuthenticationException tls
                    => new("TLS", "The secure connection could not be set up: " + tls.Message),
                _ => new("Error", ex.InnerException?.Message ?? ex.Message),
            };
        }
    }
}
