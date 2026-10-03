namespace PingTool
{
    // Everything one monitored host owns. The form runs one ping loop per
    // session and shows whichever session is selected in the list.
    internal sealed class HostSession
    {
        public const int HistorySize = 180;

        public HostSession(string address, int degradedLatencyMs = 150, int degradedLossPercent = 30)
        {
            Address = address;
            Monitor = new HostMonitor(degradedLatencyMs, degradedLossPercent);
        }

        public string Address { get; }
        public SessionStats Stats { get; } = new();
        public HostMonitor Monitor { get; }
        public Queue<long> History { get; } = new();

        // null until the first reply or timeout of the current run.
        public long? Last { get; private set; }

        // Why the last ping failed; null when it succeeded or none was sent.
        public PingFailure? LastFailure { get; private set; }

        // "IPv4 142.250.80.35", "IPv6 2607:f8b0::2004", "-" before resolution.
        public string IpText { get; private set; } = "-";

        public void Reset()
        {
            Stats.Reset();
            Monitor.Reset();
            History.Clear();
            Last = null;
            LastFailure = null;
            IpText = "-";
        }

        public void SetIp(System.Net.IPAddress ip) => IpText = Describe(ip);

        public void SetUnresolved() => IpText = "not resolved";

        public static string Describe(System.Net.IPAddress ip)
        {
            // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            string version = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
            return version + " " + ip;
        }

        // ping < 0 means failure; without a stated cause it is a plain timeout.
        public void Add(long ping, PingFailure? failure = null)
        {
            Last = ping;
            LastFailure = ping < 0 ? failure ?? PingFailure.Timeout : null;
            Stats.Add(ping);
            History.Enqueue(ping);
            while (History.Count > HistorySize) History.Dequeue();
        }
    }
}
