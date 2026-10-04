using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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

        public static bool TryParse(string? input, [NotNullWhen(true)] out ProbeTarget? target, out string error)
        {
            target = null;
            error = "";
            string text = (input ?? "").Trim();
            if (text.Length == 0) { error = "Enter an address first, or add a host to the list."; return false; }

            int sep = text.IndexOf("://", StringComparison.Ordinal);
            if (sep < 0)
            {
                target = new ProbeTarget(ProbeKind.Icmp, text, 0, null);
                return true;
            }

            string scheme = text[..sep].ToLowerInvariant();
            string rest = text[(sep + 3)..];

            switch (scheme)
            {
                case "tcp":
                    if (!TryHostPort(rest, out string host, out int port))
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

                    if (!TryHttpOptions(url.Fragment, out string? expect, out int certDays, out error)) return false;

                    target = new ProbeTarget(ProbeKind.Http, url.Host, url.Port, url, expect, certDays);
                    return true;

                case "dns":
                    string name = rest.TrimEnd('/');
                    if (name.Length == 0 || name.Contains(':', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
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
        private static bool TryHttpOptions(string fragment, out string? expect, out int certDays, out string error)
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

            return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
        }
    }
}
