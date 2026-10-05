namespace PingTool
{
    // Everything one monitored host owns. The form runs one ping loop per
    // session and shows whichever session is selected in the list.
    internal sealed class HostSession
    {
        public const int HistorySize = 180;

        public HostSession(string address, int degradedLatencyMs = HostMonitor.DefaultLatencyMs, int degradedLossPercent = HostMonitor.DefaultLossPercent, int downAfter = HostMonitor.DefaultDownAfter)
        {
            Address = address;
            Monitor = new HostMonitor(degradedLatencyMs, degradedLossPercent, downAfter);
        }

        public string Address { get; }

        // A readable name and limits of its own, when the user gave them (see TargetOptions).
        public TargetOptions? Options { get; set; }

        // The limits of its own it was monitored with (Options may be edited later; the monitor keeps the old ones until the next Start).
        public string? RunLimits { get; set; }

        // What the list, the alerts and the report call it: its name, or its address.
        public string DisplayName => TargetOptions.DisplayName(Address, Options);
        public SessionStats Stats { get; } = new();
        public HostMonitor Monitor { get; private set; }

        // The user can change the thresholds between two runs: the monitor is built with them, so
        // it is replaced (starting from a clean state), not edited.
        public void ApplyThresholds(int degradedLatencyMs, int degradedLossPercent, int downAfter) =>
            Monitor = new HostMonitor(degradedLatencyMs, degradedLossPercent, downAfter);
        public Queue<long> History { get; } = new();

        // When each ping of History was made, in the same order (always as many as History): what lets the comparison graph put the
        // pings of several hosts on ONE time axis, and lets a host that stopped being measured end where it stopped.
        public Queue<DateTimeOffset> HistoryTimes { get; } = new();

        // null until the first reply or timeout of the current run.
        public long? Last { get; private set; }

        // Why the last ping failed; null when it succeeded or none was sent.
        public PingFailure? LastFailure { get; private set; }

        // Something worth knowing although the last probe succeeded (a certificate about to expire); null when nothing.
        public string? Notice { get; set; }

        // "IPv4 142.250.80.35", "IPv6 2607:f8b0::2004", "-" before resolution.
        public string IpText { get; private set; } = "-";

        // The address behind IpText, for telling a local target from an Internet one.
        public System.Net.IPAddress? Ip { get; private set; }

        public void Reset()
        {
            Stats.Reset();
            Monitor.Reset();
            History.Clear();
            HistoryTimes.Clear();
            Last = null;
            LastFailure = null;
            Notice = null;
            IpText = "-";
            Ip = null;
            BaselinePath = null;
            BaselineRequested = false;
            baselineFailures = 0;
            pendingCapture = null;
        }

        // The route to this host while it was healthy, to compare with the one at an outage.
        // Captured once per run, at the first reply.
        public PathCapture? BaselinePath { get; set; }
        public bool BaselineRequested { get; set; }

        // An outage that was declared before the address of the host was known: the route cannot be traced without it, so the capture waits
        // here and is started at the first ping after the address is there (TakePendingCapture). One at a time: a new outage replaces it.
        private Incident? pendingCapture;

        public void CaptureWhenAddressKnown(Incident outage) => pendingCapture = outage;

        // The outage that was waiting for the address, if the address is known now and that outage is still going on (the route of an outage
        // that is over says nothing about it); it is handed over once. null otherwise.
        public Incident? TakePendingCapture()
        {
            if (pendingCapture is not { } waiting || Ip is null) return null;
            pendingCapture = null;
            return waiting.Ongoing ? waiting : null;
        }

        public const int MaxBaselineTries = 3;
        private int baselineFailures;

        // The trace of the baseline failed: ask again at the next reply, but not for ever (a refused permission would
        // start a trace at every ping). False when the tries are used up and the baseline stays missing for this run.
        public bool BaselineFailed()
        {
            if (++baselineFailures >= MaxBaselineTries) return false;
            BaselineRequested = false;
            return true;
        }

        public void SetIp(System.Net.IPAddress ip)
        {
            Ip = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            IpText = Describe(ip);
        }

        public void SetUnresolved()
        {
            Ip = null;
            IpText = "not resolved";
        }

        // What the comparison reads: only a host that has been pinged this run has data.
        public Target ToTarget() => new(Address, Ip, Monitor.State, Last is not null);

        public static string Describe(System.Net.IPAddress ip)
        {
            // A dual-stack socket reports an IPv4 peer as ::ffff:a.b.c.d.
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            string version = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
            return version + " " + ip;
        }

        // ping < 0 means failure; without a stated cause it is a plain timeout.
        public void Add(long ping, PingFailure? failure = null, DateTimeOffset? at = null)
        {
            Last = ping;
            LastFailure = ping < 0 ? failure ?? PingFailure.Timeout : null;
            Stats.Add(ping, at);
            History.Enqueue(ping);
            HistoryTimes.Enqueue(at ?? DateTimeOffset.Now);
            while (History.Count > HistorySize) { History.Dequeue(); HistoryTimes.Dequeue(); }
        }
    }
}
