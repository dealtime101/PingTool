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

    // Address = the first router that answered at this distance; Also = every distinct address seen there (load-balanced paths answer from
    // several routers for the same TTL), the first included. RttMs = the best of the replies.
    internal sealed record Hop(int Ttl, IPAddress? Address, long? RttMs, HopStatus Status, string? Detail = null, IReadOnlyList<IPAddress>? Also = null)
    {
        public IReadOnlyList<IPAddress> Seen => Also ?? (Address is null ? Array.Empty<IPAddress>() : new[] { Address });
    }

    // The route to a target at a given moment: who answered at each distance.
    internal sealed class PathCapture
    {
        public required string Host { get; init; }
        public required IPAddress Target { get; init; }
        public required DateTimeOffset Time { get; init; }
        public required IReadOnlyList<Hop> Hops { get; init; }

        // Set when the trace stopped by itself after this many silent hops in a row: the hops beyond were NOT probed, so silence there
        // is not an answer. Null when it ran to the destination, to the hop limit, or was never meant to give up.
        public int? GaveUpAfter { get; init; }

        // Set when the trace ran out of hops (this limit) without reaching the destination or giving up: the hops beyond the limit were
        // NOT probed either. Null otherwise.
        public int? HopLimit { get; init; }

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

            lines.AddRange(HopLines());
            lines.Add(Verdict());
            return string.Join("\n", lines);
        }

        // One line per hop, without the header or the verdict: what is known so far of a trace still running.
        public IEnumerable<string> HopLines()
        {
            var c = CultureInfo.InvariantCulture;
            foreach (var h in Hops)
            {
                string who = h.Address?.ToString() ?? "*";
                if (h.Seen.Count > 1) who += " (also " + string.Join(", ", h.Seen.Skip(1)) + ")";
                string ms = h.RttMs is null ? "" : "  " + h.RttMs.Value.ToString(c) + " ms";
                string flag = h.Status switch
                {
                    HopStatus.Reached => "  (destination)",
                    HopStatus.Unreachable => "  (reports: unreachable)",
                    HopStatus.Failed => "  (probe failed)",
                    HopStatus.Expired when h.Detail is { Length: > 0 } => "  (" + h.Detail + ")",   // a router that answered something unusual
                    _ => "",
                };
                yield return $"{h.Ttl,3}  {who}{ms}{flag}";
            }
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
            if (farthest is null)
                return "Nothing answered, not even the first router: this PC, its link, or a router or firewall that does not answer these probes can be the cause (many never do), so this alone does not tell which.";

            // The trace gave up: the hops beyond were never tried, so "they do not answer" would be a claim nobody checked.
            if (GaveUpAfter is int silent)
                return $"No reply after hop {farthest.Ttl} ({farthest.Address}); the trace stopped there after {silent} silent hops in a row and did not try the hops beyond. "
                    + "Routers and firewalls that do not answer these probes are common, so this does not show that the path ends there.";

            // The trace used all its hops: what is beyond the limit was never tried, so the path may well go on.
            if (HopLimit is int limit)
                return $"The trace ended at its limit of {limit} hops without reaching the destination; the last reply came from hop {farthest.Ttl} ({farthest.Address}). "
                    + "The hops beyond the limit were not probed, so this does not show where the path ends.";

            return $"Replies stop after hop {farthest.Ttl} ({farthest.Address}): the hops beyond it do not answer.";
        }

        private static string Said(HopStatus s) => s switch
        {
            HopStatus.Reached => "answered as the destination",
            HopStatus.Unreachable => "reported the destination unreachable",
            HopStatus.Expired => "answered as a router on the way",
            HopStatus.Timeout => "was silent",
            _ => "could not be probed",
        };

        // What differs from the path seen while the target was healthy. Empty when nothing does.
        public static List<string> Compare(PathCapture? healthy, PathCapture during)
        {
            var notes = new List<string>();
            if (healthy is null) return notes;

            int max = Math.Max(healthy.Hops.Count, during.Hops.Count);
            for (int i = 0; i < max; i++)
            {
                var before = i < healthy.Hops.Count ? healthy.Hops[i].Seen : Array.Empty<IPAddress>();
                var now = i < during.Hops.Count ? during.Hops[i].Seen : Array.Empty<IPAddress>();
                int ttl = i + 1;

                // Load balancing answers from several routers for one distance: it is a change only when no router is common to both.
                if (before.Count > 0 && now.Count > 0 && !before.Intersect(now).Any())
                    notes.Add($"Hop {ttl} changed: {string.Join(" / ", before)} when healthy, {string.Join(" / ", now)} now (the route changed).");
                else if (before.Count > 0 && now.Count == 0 && i < during.Hops.Count && during.Hops[i].Status != HopStatus.Failed)
                    notes.Add($"Hop {ttl} ({string.Join(" / ", before)}) answered when healthy and is silent now.");
                else if (before.Count > 0 && now.Count > 0 && i < healthy.Hops.Count && i < during.Hops.Count)
                {
                    // Same router, but not the same answer: the destination that answered now reports itself unreachable (or the
                    // reverse). The addresses alone say nothing changed.
                    var was = healthy.Hops[i].Status;
                    var is_ = during.Hops[i].Status;
                    if (was != is_ && (was is HopStatus.Reached or HopStatus.Unreachable || is_ is HopStatus.Reached or HopStatus.Unreachable))
                        notes.Add($"Hop {ttl} ({string.Join(" / ", now)}) {Said(was)} when healthy and {Said(is_)} now.");
                }
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

        // Routers often rate-limit their "time exceeded" replies, so one lost probe proves nothing: each hop gets this many probes
        // (like traceroute) and is called silent only when every one went unanswered. All of them are sent even when the first
        // answers, because a load-balanced path may answer from a different router each time and the set of routers is what is compared.
        public const int ProbesPerHop = 3;

        // "PingException: An exception occurred during a Ping request. (SocketException: ...)" on one line.
        internal static string Why(Exception ex)
        {
            string text = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException is { } inner) text += " (" + inner.GetType().Name + ": " + inner.Message + ")";
            return string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        public static async Task<PathCapture> RunAsync(string host, IPAddress target, HopProbe probe, DateTimeOffset time,
            int maxHops = MaxHops, int giveUpAfter = GiveUpAfter, CancellationToken token = default, int probesPerHop = ProbesPerHop,
            Action<IReadOnlyList<Hop>>? progress = null)
        {
            var hops = new List<Hop>();
            int silent = 0;
            bool gaveUp = false;

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                token.ThrowIfCancellationRequested();

                var replies = new List<HopReply>();
                for (int attempt = 0; attempt < Math.Max(1, probesPerHop); attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        replies.Add(await probe(target, ttl, token));
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        // A probe that cannot even be sent is not a silent router: it is said so, and the trace stops (the next
                        // hops would fail the same way and each look like "nothing answers"). Trying again would not help.
                        replies.Clear();
                        replies.Add(new HopReply(HopStatus.Failed, null, 0, Why(ex)));
                        break;
                    }
                }

                var answered = replies.Where(x => x.Status != HopStatus.Timeout).ToList();
                var reply = answered.Count == 0 ? replies[0] : answered[0];   // the first answer gives the status
                var also = answered.Where(x => x.Address is not null).Select(x => x.Address!).Distinct().ToList();
                long? best = answered.Count == 0 || reply.Status == HopStatus.Failed ? null : answered.Min(x => x.RttMs);
                hops.Add(new Hop(ttl, reply.Address, best, reply.Status, reply.Detail, also));
                progress?.Invoke(hops);   // the hops known so far; the caller reads them before the next await
                if (reply.Status is HopStatus.Reached or HopStatus.Unreachable or HopStatus.Failed) break;

                silent = reply.Status == HopStatus.Timeout ? silent + 1 : 0;
                if (silent >= giveUpAfter) { gaveUp = true; break; }
            }

            bool hitLimit = !gaveUp && hops.Count > 0 && hops.Count == maxHops
                && hops[^1].Status is not (HopStatus.Reached or HopStatus.Unreachable or HopStatus.Failed);
            return new PathCapture { Host = host, Target = target, Time = time, Hops = hops, GaveUpAfter = gaveUp ? giveUpAfter : null, HopLimit = hitLimit ? maxHops : null };
        }
    }

    // The real probe: an echo request with a limited TTL. No payload is given, because a custom
    // payload needs privileges on Linux; the default one works everywhere.
    internal static class PingHopProbe
    {
        public static HopProbe Create(int timeoutMs) => async (target, ttl, token) =>
        {
            using var ping = new Ping();

            // The token goes to the call itself: a Stop that arrives before the probe is on the wire cancels it too (the
            // SendAsyncCancel method did nothing then, and the probe waited its whole timeout).
            var clock = Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), null, new PingOptions(ttl, true), token);
            long ms = clock.ElapsedMilliseconds;   // RoundtripTime reads 0 for the routers on some platforms
            token.ThrowIfCancellationRequested();

            return Classify(reply.Status, reply.Address, reply.RoundtripTime, ms);
        };

        // What an ICMP status means for the trace. Only the "destination unreachable" family says a router declares the destination
        // unreachable (and ends the trace). Another error from a router (a parameter problem, a source quench...) is still a router at
        // that distance: the trace goes on, with the status written. No real answer (no resources, hardware error, unknown) is the
        // probe's own failure, said as such, not a verdict on the network.
        internal static HopReply Classify(IPStatus status, IPAddress? address, long roundtripMs, long measuredMs) => status switch
        {
            IPStatus.Success => new HopReply(HopStatus.Reached, address, roundtripMs > 0 ? roundtripMs : measuredMs),
            IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded => new HopReply(HopStatus.Expired, address, measuredMs),
            IPStatus.TimedOut => new HopReply(HopStatus.Timeout, null, 0),
            IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationHostUnreachable or IPStatus.DestinationProtocolUnreachable
                or IPStatus.DestinationPortUnreachable or IPStatus.DestinationUnreachable or IPStatus.DestinationScopeMismatch
                or IPStatus.BadRoute or IPStatus.BadDestination => new HopReply(HopStatus.Unreachable, address, measuredMs),
            IPStatus.NoResources or IPStatus.HardwareError or IPStatus.Unknown => new HopReply(HopStatus.Failed, null, 0, "ICMP status " + status),
            _ when address is not null => new HopReply(HopStatus.Expired, address, measuredMs, "answered " + status),
            _ => new HopReply(HopStatus.Failed, null, 0, "ICMP status " + status),
        };
    }
}
