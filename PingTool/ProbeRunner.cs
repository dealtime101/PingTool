using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PingTool
{
    // One probe's answer. Rtt < 0 = failure, and then Failure says why. Error keeps the raw
    // exception when there was one, for the debug trace.
    // Warning = something to say although the probe succeeded (a certificate about to expire).
    internal sealed record ProbeOutcome(long Rtt, PingFailure? Failure, IPAddress? Ip, Exception? Error = null, string? Warning = null);

    // How close a certificate is to its end, in words. null = nothing to say.
    internal static class CertWatch
    {
        public static string? Warning(string host, DateTime notAfterUtc, DateTime nowUtc, int warnDays)
        {
            if (warnDays <= 0) return null;

            double days = (notAfterUtc - nowUtc).TotalDays;
            if (days >= warnDays) return null;

            string when = notAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (days < 0) return $"The certificate of {host} expired on {when}";
            int whole = (int)Math.Floor(days);
            return whole == 0 ? $"The certificate of {host} expires today or tomorrow ({when})"
                : $"The certificate of {host} expires in {whole.ToString(CultureInfo.InvariantCulture)} day{(whole == 1 ? "" : "s")} ({when})";
        }
    }

    // Runs ONE probe against a target. The caller loops. Cancelling the token (Stop, Close)
    // throws OperationCanceledException; every other failure comes back as a ProbeOutcome.
    internal static class ProbeRunner
    {
        // One client for the whole program (a client per probe leaks sockets). No redirects: a 301
        // from the server means it answered, which is all this probe asks.
        private static readonly HttpClient Web = CreateWebClient();

        private static HttpClient CreateWebClient()
        {
            // HttpClientHandler (not SocketsHttpHandler.SslOptions): only its callback is handed the request, which is where
            // the end date of the certificate is kept.
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = ValidateServerCertificate,
            }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PingTool/" + AppVersion.Number);
            return client;
        }

        public static async Task<ProbeOutcome> RunAsync(ProbeTarget target, int timeoutMs, Ping ping, byte[]? buffer, CancellationToken token)
        {
            try
            {
                return target.Kind switch
                {
                    ProbeKind.Icmp => await IcmpAsync(target, timeoutMs, ping, buffer, token),
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

        // The token goes to the call: Stop or Close cancels a ping in flight, like the other probes (the class promises it). A null buffer
        // is the system's default one.
        private static async Task<ProbeOutcome> IcmpAsync(ProbeTarget t, int timeoutMs, Ping ping, byte[]? buffer, CancellationToken token)
        {
            var reply = await ping.SendPingAsync(t.Host, TimeSpan.FromMilliseconds(timeoutMs), buffer, null, token);

            return reply.Status == IPStatus.Success
                ? new ProbeOutcome(reply.RoundtripTime, null, reply.Address)
                : new ProbeOutcome(-1, PingFailure.From(reply.Status, reply.Address?.AddressFamily), null);
        }

        // The time to open the connection, nothing more: that is "the port is reachable".
        private static Task<ProbeOutcome> TcpAsync(ProbeTarget t, int timeoutMs, CancellationToken token) => TcpAsync(t, timeoutMs, Dns.GetHostAddressesAsync, token);

        // The name is resolved FIRST and the clock starts after it: a slow resolver (or a first lookup that is not cached yet) is not a slow
        // port. The whole probe, resolution included, still has to fit in the timeout. `resolve` is injected so that a test can make it slow.
        internal static async Task<ProbeOutcome> TcpAsync(ProbeTarget t, int timeoutMs,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken token)   // the token last (CA1068)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var client = new TcpClient();

            var addresses = await resolve(t.Host, deadline.Token);
            if (addresses.Length == 0) return new ProbeOutcome(-1, new PingFailure("No host", "The name resolved to no address"), null);

            // The addresses are tried in the order the system gave them, as it does itself: what a slow first one costs before the
            // second one answers is part of what a connection to this name costs.
            var clock = Stopwatch.StartNew();
            await client.ConnectAsync(addresses, t.Port, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            return new ProbeOutcome(rtt, null, peer is { IsIPv4MappedToIPv6: true } ? peer.MapToIPv4() : peer);
        }

        // Where the certificate's end date of a request is kept while the request is in flight (see ValidateServerCertificate).
        private static readonly HttpRequestOptionsKey<DateTime> CertificateEnd = new("PingTool.CertificateEnd");

        // "404 Not Found", and just "599" when the server gave no phrase (HTTP/2 has none on the wire, and a status the framework does not
        // know has none to give): never "599 " with a space and nothing after it.
        internal static string StatusText(int code, string? reasonPhrase)
        {
            string number = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(reasonPhrase) ? number : number + " " + reasonPhrase.Trim();
        }

        // How much of a page is read when an expected text is looked for (see Search): the first 64 KiB.
        public const int MaxBodyBytes = 64 * 1024;

        // The default validation, plus a note of when the server's certificate ends. Returning "no policy error" is exactly what the
        // default check does, so nothing is trusted that was not before.
        public static bool ValidateServerCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is not null) request.Options.Set(CertificateEnd, certificate.NotAfter.ToUniversalTime());
            return errors == SslPolicyErrors.None;
        }

        // The time until the status line and headers arrive, on a NEW connection each time (so the
        // figure includes the TCP and TLS handshakes, like a first visit). 4xx and 5xx are failures.
        private static Task<ProbeOutcome> HttpAsync(ProbeTarget t, int timeoutMs, CancellationToken token) => HttpAsync(t, timeoutMs, token, Web);

        internal static async Task<ProbeOutcome> HttpAsync(ProbeTarget t, int timeoutMs, CancellationToken token, HttpClient client)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Get, t.Url);
            request.Headers.ConnectionClose = true;

            var clock = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            long rtt = clock.ElapsedMilliseconds;

            int code = (int)response.StatusCode;
            if (code >= 400)
                return new ProbeOutcome(-1, new PingFailure("HTTP " + code, "The server answered " + StatusText(code, response.ReasonPhrase)), null);

            // The page must contain the expected text: a redirect (to a login page, as a captive portal does) or a maintenance
            // page that answers "200" is then a failure, not a success.
            if (t.ExpectText is { } expected)
            {
                if (code is >= 300 and < 400)
                    return new ProbeOutcome(-1, new PingFailure("Redirect", $"The server redirected (HTTP {code}) instead of serving the page with the expected text: a login page of a captive portal does this"), null);

                var (found, truncated) = await ReadStart(response, expected, deadline.Token);
                if (!found)
                    return new ProbeOutcome(-1, new PingFailure("Content", truncated
                        ? $"The text was not found in the first {MaxBodyBytes / 1024} KiB of the page, which is all that is searched; the page is longer, so the text may be further down"
                        : "The page answered but does not contain the expected text (a maintenance page, an error page or a captive portal)"), null);
            }

            string? warning = request.Options.TryGetValue(CertificateEnd, out DateTime end)
                ? CertWatch.Warning(t.Host, end, DateTime.UtcNow, t.CertWarnDays) : null;
            return new ProbeOutcome(rtt, null, null, Warning: warning);
        }

        private static async Task<(bool Found, bool Truncated)> ReadStart(HttpResponseMessage response, string expected, CancellationToken token)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await Search(stream, response.Content.Headers.ContentType?.CharSet, expected, token);
        }

        // Looks for `expected` in the first MaxBodyBytes of the stream, as the page arrives: it stops at the first read that brings the text
        // (a page that has already said what we wait for and then sends the rest slowly must not turn into a timeout). What was read is
        // decoded again whole after each read, so a text cut in two by two reads is found, at the price of a few decodings of 64 KiB.
        // Truncated: the page goes on beyond the limit, and the text was not in what was read - "not found" then may simply mean
        // "further down", which the message must not call a maintenance page.
        internal static async Task<(bool Found, bool Truncated)> Search(Stream stream, string? charset, string expected, CancellationToken token)
        {
            var buffer = new byte[MaxBodyBytes];
            int total = 0, read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), token)) > 0)
            {
                total += read;
                if (Decode(buffer, total, charset).Contains(expected, StringComparison.OrdinalIgnoreCase)) return (true, false);
            }

            bool more = total == buffer.Length && await stream.ReadAsync(new byte[1], token) > 0;
            return (false, more);
        }

        // The text of a page in the encoding it says it has: a byte order mark first (it is certain), then the charset of the
        // Content-Type, and UTF-8 when there is neither or the name is not one this system knows. A page in ISO-8859-1 or UTF-16 read
        // as UTF-8 would not contain an expected text with an accent, and a good answer would count as a failure.
        internal static string Decode(byte[] buffer, int count, string? charset)
        {
            var bom = buffer.AsSpan(0, count);
            if (bom.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(buffer, 3, count - 3);
            if (bom.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(buffer, 2, count - 2);
            if (bom.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(buffer, 2, count - 2);

            Encoding encoding = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // windows-1252 and the other code pages (idempotent)
                    encoding = Encoding.GetEncoding(charset.Trim().Trim('"', '\''));
                }
                catch (ArgumentException) { }   // a name we do not know: UTF-8, as before
            }

            return encoding.GetString(buffer, 0, count);
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
