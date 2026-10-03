using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingTool
{
    // One probe's answer. Rtt < 0 = failure, and then Failure says why. Error keeps the raw
    // exception when there was one, for the debug trace.
    internal sealed record ProbeOutcome(long Rtt, PingFailure? Failure, IPAddress? Ip, Exception? Error = null);

    // Runs ONE probe against a target. The caller loops. Cancelling the token (Stop, Close)
    // throws OperationCanceledException; every other failure comes back as a ProbeOutcome.
    internal static class ProbeRunner
    {
        // One client for the whole program (a client per probe leaks sockets). No redirects: a 301
        // from the server means it answered, which is all this probe asks.
        private static readonly HttpClient Web = CreateWebClient();

        private static HttpClient CreateWebClient()
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PingTool/" + AppVersion.Number);
            return client;
        }

        public static async Task<ProbeOutcome> RunAsync(ProbeTarget target, int timeoutMs, Ping ping, byte[]? buffer, CancellationToken token)
        {
            try
            {
                return target.Kind switch
                {
                    ProbeKind.Icmp => await IcmpAsync(target, timeoutMs, ping, buffer),
                    ProbeKind.Tcp => await TcpAsync(target, timeoutMs, token),
                    ProbeKind.Http => await HttpAsync(target, timeoutMs, token),
                    ProbeKind.Dns => await DnsAsync(target, timeoutMs, token),
                    _ => throw new ArgumentOutOfRangeException(nameof(target)),
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Our own deadline fired, not the user's Stop.
                return new ProbeOutcome(-1, PingFailure.Timeout, null, ex);
            }
            catch (Exception ex)
            {
                return new ProbeOutcome(-1, PingFailure.From(ex), null, ex);
            }
        }

        private static async Task<ProbeOutcome> IcmpAsync(ProbeTarget t, int timeoutMs, Ping ping, byte[]? buffer)
        {
            var reply = buffer is null
                ? await ping.SendPingAsync(t.Host, timeoutMs)
                : await ping.SendPingAsync(t.Host, timeoutMs, buffer);

            return reply.Status == IPStatus.Success
                ? new ProbeOutcome(reply.RoundtripTime, null, reply.Address)
                : new ProbeOutcome(-1, PingFailure.From(reply.Status), null);
        }

        // The time to open the connection, nothing more: that is "the port is reachable".
        private static async Task<ProbeOutcome> TcpAsync(ProbeTarget t, int timeoutMs, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var client = new TcpClient();

            var clock = Stopwatch.StartNew();
            await client.ConnectAsync(t.Host, t.Port, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            return new ProbeOutcome(rtt, null, peer is { IsIPv4MappedToIPv6: true } ? peer.MapToIPv4() : peer);
        }

        // The time until the status line and headers arrive, on a NEW connection each time (so the
        // figure includes the TCP and TLS handshakes, like a first visit). 4xx and 5xx are failures.
        private static async Task<ProbeOutcome> HttpAsync(ProbeTarget t, int timeoutMs, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Get, t.Url);
            request.Headers.ConnectionClose = true;

            var clock = Stopwatch.StartNew();
            using var response = await Web.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            int code = (int)response.StatusCode;
            if (code >= 400)
                return new ProbeOutcome(-1, new PingFailure("HTTP " + code, "The server answered " + code + " " + response.ReasonPhrase), null);

            return new ProbeOutcome(rtt, null, null);
        }

        // Through the operating system's resolver, so its cache applies: a name looked up a moment
        // ago answers in 0 ms. It still catches a resolver that stopped answering.
        private static async Task<ProbeOutcome> DnsAsync(ProbeTarget t, int timeoutMs, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);

            var clock = Stopwatch.StartNew();
            var addresses = await Dns.GetHostAddressesAsync(t.Host, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            return addresses.Length == 0
                ? new ProbeOutcome(-1, new PingFailure("No host", "The name resolved to no address"), null)
                : new ProbeOutcome(rtt, null, addresses[0]);
        }
    }
}
