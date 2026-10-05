using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace PingTool
{
    internal enum ProbeKind { Icmp, Tcp, Http, Dns }

    // What to probe, parsed from what the user typed in the address box:
    //   google.ca                 ping (ICMP), as before
    //   tcp://example.com:443     can a TCP connection be opened to that port?
    //   http://host/path          does the web server answer? (https:// too)
    //   dns://example.com         does the name resolve?
    //
    // A web address may carry options after a #, which is never sent to the server:
    //   https://example.com/#contains=Welcome        the page must contain that text (a captive portal or a maintenance page
    //                                                answers "200" or redirects, but does not contain it)
    //   https://example.com/#cert=30                 warn when the certificate expires in less than 30 days (default 14, 0 = never)
    //   https://example.com/#contains=a%20b&cert=7   both (spaces and special characters written %20 etc.)
    internal sealed record ProbeTarget(ProbeKind Kind, string Host, int Port, Uri? Url, string? ExpectText = null, int CertWarnDays = ProbeTarget.DefaultCertWarnDays)
    {
        public const int DefaultCertWarnDays = 14;
        public const int MaxCertWarnDays = 365;
        public const int MaxExpectLength = 200;

        public const string Help =
            "No prefix = ping.  tcp://host:port = open a TCP connection.  http://url or https://url = web request.  dns://name = name lookup.";

        // The addresses a file or the command line gave, minus those the address box would refuse (a hand-edited
        // settings.json, a mistyped argument): the same rule as typing them, and the count says how many were left out.
        public static List<string> KeepValid(IEnumerable<string> addresses, out int skipped)
        {
            var kept = new List<string>();
            skipped = 0;
            foreach (var address in addresses)
            {
                if (TryParse(address, out _, out _)) kept.Add(address);
                else skipped++;
            }

            return kept;
        }

        public static bool TryParse(string? input, [NotNullWhen(true)] out ProbeTarget? target, out string error)
        {
            target = null;
            error = "";
            string text = (input ?? "").Trim();
            if (text.Length == 0) { error = "Enter an address first, or add a host to the list."; return false; }

            int sep = text.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0)
            {
                // A ping takes a host name or an IP address, nothing else. "example.com:443" or "www.example.com/page" would only
                // come back as an unresolved name, which does not tell the user that a prefix was missing. A colon is fine in an IPv6 address.
                if (text.Any(char.IsWhiteSpace) || text.Contains('/', StringComparison.Ordinal)
                    || (text.Contains(':', StringComparison.Ordinal) && !IPAddress.TryParse(text, out _)))
                {
                    error = "Without a prefix the address is pinged, so it must be just a host name or an IP address. " +
                        "For a port use tcp://example.com:443, for a web page https://example.com/page";
                    return false;
                }

                target = new ProbeTarget(ProbeKind.Icmp, text, 0, null);
                return true;
            }

            string scheme = text[..sep].ToLowerInvariant();
            string rest = text[(sep + 3)..];

            switch (scheme)
            {
                case "tcp":
                    // A trailing slash is what a pasted address often ends with; dns:// already accepts it.
                    if (!TryHostPort(rest.TrimEnd('/'), out string host, out int port))
                    {
                        error = "tcp:// needs a host and a port from 1 to 65535, for example tcp://example.com:443";
                        return false;
                    }

                    target = new ProbeTarget(ProbeKind.Tcp, host, port, null);
                    return true;

                case "http":
                case "https":
                    if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Host.Length == 0)
                    {
                        error = scheme + "://: that is not a valid web address, for example https://example.com/";
                        return false;
                    }

                    if (!TryHttpOptions(url.Fragment, url.Scheme == Uri.UriSchemeHttps, out string? expect, out int certDays, out error)) return false;

                    target = new ProbeTarget(ProbeKind.Http, url.Host, url.Port, url, expect, certDays);
                    return true;

                case "dns":
                    string name = rest.TrimEnd('/');
                    // Just a name: the rest of what a URL can carry (?query, #fragment, user@, spaces) is not part of it, and would be sent to
                    // the resolver as if it were. CheckHostName also refuses a leading dash or an empty label; internationalised names pass.
                    if (name.Length == 0 || name.Contains(':', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal)
                        || Uri.CheckHostName(name) == UriHostNameType.Unknown)
                    {
                        error = "dns:// needs just a name, for example dns://example.com";
                        return false;
                    }

                    target = new ProbeTarget(ProbeKind.Dns, name, 0, null);
                    return true;

                default:
                    error = string.Format(CultureInfo.CurrentCulture,
                        "Unknown prefix \"{0}://\". Use tcp://, http://, https:// or dns://, or no prefix to ping.", scheme);
                    return false;
            }
        }

        // The part after # of a web address: "contains=text" and/or "cert=days", separated by &. Anything else is refused: a typo
        // ("#contain=...") silently ignored would leave the check the user thinks they set up doing nothing.
        private static bool TryHttpOptions(string fragment, bool secure, out string? expect, out int certDays, out string error)
        {
            expect = null;
            certDays = DefaultCertWarnDays;
            error = "";
            string text = fragment.TrimStart('#');
            if (text.Length == 0) return true;

            bool sawContains = false, sawCert = false;
            foreach (string part in text.Split('&'))
            {
                int eq = part.IndexOf('=', StringComparison.Ordinal);
                string key = (eq < 0 ? part : part[..eq]).ToLowerInvariant();
                string value = eq < 0 ? "" : part[(eq + 1)..];

                if (key == "contains" && !sawContains)
                {
                    sawContains = true;
                    string decoded;
                    try { decoded = Uri.UnescapeDataString(value); }
                    catch (UriFormatException) { decoded = ""; }
                    if (decoded.Length == 0 || decoded.Length > MaxExpectLength || decoded.Any(char.IsControl))
                    {
                        error = $"#contains= needs a text of 1 to {MaxExpectLength} characters (write spaces as %20), for example https://example.com/#contains=Welcome";
                        return false;
                    }

                    expect = decoded;
                }
                else if (key == "cert" && !sawCert)
                {
                    sawCert = true;
                    // An option that can never do anything is refused, like the unknown ones: a person who wrote it believes a certificate is watched.
                    if (!secure)
                    {
                        error = "#cert= only applies to https:// addresses: an http:// address has no certificate to check. Use https://, or leave #cert= out.";
                        return false;
                    }

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out certDays) || certDays > MaxCertWarnDays)
                    {
                        error = $"#cert= needs a number of days from 0 (never warn) to {MaxCertWarnDays}, for example https://example.com/#cert=30";
                        return false;
                    }
                }
                else
                {
                    error = "Unknown option after # in the web address: use #contains=text and/or #cert=days (separated by &), each once.";
                    return false;
                }
            }

            return true;
        }

        // "host:443", "1.2.3.4:80", "[2001:db8::1]:443"
        private static bool TryHostPort(string text, out string host, out int port)
        {
            host = "";
            port = 0;
            string portText;

            if (text.StartsWith('['))
            {
                int close = text.IndexOf(']', StringComparison.Ordinal);
                if (close < 2 || close + 1 >= text.Length || text[close + 1] != ':') return false;
                host = text[1..close];
                portText = text[(close + 2)..];
            }
            else
            {
                int colon = text.LastIndexOf(':');
                if (colon < 1) return false;
                host = text[..colon];
                portText = text[(colon + 1)..];
                if (host.Contains(':', StringComparison.Ordinal)) return false;   // a bare IPv6 address needs brackets
            }

            // "host/x" or "my host" would only fail at the connection, with an error that does not say why.
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;

            return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
        }
    }
}
