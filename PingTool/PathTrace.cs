using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;

namespace PingTool
{
    // Failed = the probe itself could not be sent (no permission, address family not supported...): not an answer of the network.
    internal enum HopStatus { Expired, Reached, Timeout, Unreachable, Failed }

    // What one probe at one TTL brought back.
    internal sealed record HopReply(HopStatus Status, IPAddress? Address, long RttMs, string? Detail = null);

    // Sends ONE probe with a limited TTL. Injected so the algorithm can be tested on a simulated network.
    internal delegate Task<HopReply> HopProbe(IPAddress target, int ttl, CancellationToken token);

    internal sealed record Hop(int Ttl, IPAddress? Address, long? RttMs, HopStatus Status, string? Detail = null);

    // The route to a target at a given moment: who answered at each distance.
    internal sealed class PathCapture
    {
        public required string Host { get; init; }
        public required IPAddress Target { get; init; }
        public required DateTimeOffset Time { get; init; }
        public required IReadOnlyList<Hop> Hops { get; init; }

        public bool Reached => Hops.Count > 0 && Hops[^1].Status == HopStatus.Reached;

        // The farthest hop that said anything: where the answers stop.
        public Hop? LastResponding => Hops.LastOrDefault(h => h.Address is not null);

        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                $"Path to {Host} ({Target}) at {Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", c)}",
            };

            foreach (var h in Hops)
            {
                string who = h.Address?.ToString() ?? "*";
                string ms = h.RttMs is null ? "" : "  " + h.RttMs.Value.ToString(c) + " ms";
                string flag = h.Status switch { HopStatus.Reached => "  (destination)", HopStatus.Unreachable => "  (reports: unreachable)", HopStatus.Failed => "  (probe failed)", _ => "" };
                lines.Add($"{h.Ttl,3}  {who}{ms}{flag}");
            }

            lines.Add(Verdict());
            return string.Join("\n", lines);
        }

        private string Verdict()
        {
            if (Reached) return $"The destination answered at hop {Hops[^1].Ttl}.";

            var last = Hops.Count > 0 ? Hops[^1] : null;
            if (last is { Status: HopStatus.Unreachable })
                return last.Address is null
                    ? $"A router reports the destination unreachable at hop {last.Ttl}."
                    : $"{last.Address} reports the destination unreachable at hop {last.Ttl}.";

            // Our own probe broke: nothing was learned about the network, and saying "the fault is on this PC" would be a guess.
            if (last is { Status: HopStatus.Failed })
                return $"The probe itself failed at hop {last.Ttl} ({last.Detail}): the route could not be traced. This says nothing about the network.";

            var farthest = LastResponding;
            return farthest is null
                ? "Nothing answered, not even the first router: the fault is on this PC or its link."
                : $"Replies stop after hop {farthest.Ttl} ({farthest.Address}): the hops beyond it do not answer.";
        }

        // What differs from the path seen while the target was healthy. Empty when nothing does.
        public static List<string> Compare(PathCapture? healthy, PathCapture during)
        {
            var notes = new List<string>();
            if (healthy is null) return notes;

            int max = Math.Max(healthy.Hops.Count, during.Hops.Count);
            for (int i = 0; i < max; i++)
            {
                var before = i < healthy.Hops.Count ? healthy.Hops[i].Address : null;
                var now = i < during.Hops.Count ? during.Hops[i].Address : null;
                int ttl = i + 1;

                if (before is not null && now is not null && !before.Equals(now))
                    notes.Add($"Hop {ttl} changed: {before} when healthy, {now} now (the route changed).");
                else if (before is not null && now is null && i < during.Hops.Count && during.Hops[i].Status != HopStatus.Failed)
                    notes.Add($"Hop {ttl} ({before}) answered when healthy and is silent now.");
            }

            return notes;
        }
    }

    // A traceroute: probes at TTL 1, 2, 3... Each router that drops a probe says who it is.
    // Gives up after GiveUpAfter silent hops in a row (a dead path would otherwise cost MaxHops
    // timeouts) and keeps those stars, so the report shows where the answers stop.
    internal static class TraceRunner
    {
        public const int MaxHops = 20;
        public const int GiveUpAfter = 3;

        // "PingException: An exception occurred during a Ping request. (SocketException: ...)" on one line.
        internal static string Why(Exception ex)
        {
            string text = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException is { } inner) text += " (" + inner.GetType().Name + ": " + inner.Message + ")";
            return string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        public static async Task<PathCapture> RunAsync(string host, IPAddress target, HopProbe probe, DateTimeOffset time,
            int maxHops = MaxHops, int giveUpAfter = GiveUpAfter, CancellationToken token = default)
        {
            var hops = new List<Hop>();
            int silent = 0;

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                token.ThrowIfCancellationRequested();

                HopReply reply;
                try
                {
                    reply = await probe(target, ttl, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // A probe that cannot even be sent is not a silent router: it is said so, and the trace stops (the next
                    // hops would fail the same way and each look like "nothing answers").
                    reply = new HopReply(HopStatus.Failed, null, 0, Why(ex));
                }

                hops.Add(new Hop(ttl, reply.Address, reply.Status is HopStatus.Timeout or HopStatus.Failed ? null : reply.RttMs, reply.Status, reply.Detail));
                if (reply.Status is HopStatus.Reached or HopStatus.Unreachable or HopStatus.Failed) break;

                silent = reply.Status == HopStatus.Timeout ? silent + 1 : 0;
                if (silent >= giveUpAfter) break;
            }

            return new PathCapture { Host = host, Target = target, Time = time, Hops = hops };
        }
    }

    // The real probe: an echo request with a limited TTL. No payload is given, because a custom
    // payload needs privileges on Linux; the default one works everywhere.
    internal static class PingHopProbe
    {
        public static HopProbe Create(int timeoutMs) => async (target, ttl, token) =>
        {
            using var ping = new Ping();
            using var cancel = token.Register(ping.SendAsyncCancel);

            var clock = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), null, new PingOptions(ttl, true));
            long ms = clock.ElapsedMilliseconds;   // RoundtripTime reads 0 for the routers on some platforms
            token.ThrowIfCancellationRequested();

            return reply.Status switch
            {
                IPStatus.Success => new HopReply(HopStatus.Reached, reply.Address, reply.RoundtripTime > 0 ? reply.RoundtripTime : ms),
                IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded
                    => new HopReply(HopStatus.Expired, reply.Address, ms),
                IPStatus.TimedOut => new HopReply(HopStatus.Timeout, null, 0),
                _ => new HopReply(HopStatus.Unreachable, reply.Address, ms),
            };
        };
    }
}
