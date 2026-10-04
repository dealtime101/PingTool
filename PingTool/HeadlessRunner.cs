using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PingTool
{
    internal sealed record HeadlessResult(int ExitCode, string Summary, ReportData Report);

    // PingTool without a window ("--headless --duration 8h --report night.html"): for the Task Scheduler or a script. It probes the targets
    // with the same probes, monitor, incident detection and report as the window, for the given time, writes the report and says by its
    // exit code whether anything went wrong: 0 = no incident, 1 = at least one outage or slowdown, 2 = it could not run.
    internal static class HeadlessRunner
    {
        public const int OkCode = 0, IncidentCode = 1, ErrorCode = 2;
        public static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(10);

        public delegate Task<ProbeOutcome> ProbeFunc(ProbeTarget target, int timeoutMs, CancellationToken token);

        // addresses: what to probe. Limits, interval, timeout and names come from `settings`. probe: the probe itself (injected: tests do not
        // touch the network). checkpoint: called with a fresh report every `checkpointEvery` and at the very end (so that a process killed
        // in the night leaves a report that is at most that old). alert: each state change, as the window would raise it.
        public static async Task<HeadlessResult> RunAsync(IReadOnlyList<string> addresses, Settings settings, TimeSpan duration, ProbeFunc probe,
            Action<ReportData>? checkpoint = null, TimeSpan? checkpointEvery = null, Action<WebhookEvent>? alert = null,
            Func<DateTimeOffset>? clock = null, CancellationToken stop = default)
        {
            clock ??= () => DateTimeOffset.Now;
            var targets = new List<(string Address, ProbeTarget Target)>();
            foreach (string address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!ProbeTarget.TryParse(address, out var t, out string problem))
                    throw new ArgumentException($"\"{address}\": {problem}");
                targets.Add((address, t));
            }

            if (targets.Count == 0) throw new ArgumentException("No target to probe: give addresses, --diagnose, or save some in the window first.");

            var sessions = new List<HostSession>();
            foreach (var (address, _) in targets)
            {
                settings.TargetOptions.TryGetValue(address, out var options);
                var (slow, loss, down) = TargetOptions.Effective(options, settings.DegradedLatencyMs, settings.DegradedLossPercent, settings.DownAfter);
                sessions.Add(new HostSession(address, slow, loss, down) { Options = options });
            }

            var incidents = new IncidentLog();
            var gate = new object();   // the probes run side by side; what they feed is not made for that
            var start = clock();

            ReportData Build()
            {
                var now = clock();
                lock (gate)
                    return new ReportData(now, start, Environment.MachineName, AppVersion.Display, settings.IntervalMs, settings.TimeoutMs, settings.PacketSize,
                        settings.DegradedLatencyMs, settings.DegradedLossPercent,
                        sessions.Select(s => new HostReport(s.Address, s.IpText, s.Monitor.State, s.Stats.Sent, s.Stats.Lost, s.Stats.LossPercent,
                            s.Stats.Min, s.Stats.Avg, s.Stats.Max, s.Stats.Jitter, s.History.ToArray(), s.Stats.Hours,
                            s.Options?.Label, s.Options is null ? null : TargetOptions.DescribeLimits(s.Options), s.Notice)).ToList(),
                        Diagnosis.For(sessions.Select(s => s.ToTarget()).ToList()), incidents.Summary(now), incidents.Incidents.ToList());
            }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
            run.CancelAfter(duration);

            async Task Loop(HostSession session, ProbeTarget target)
            {
                while (!run.Token.IsCancellationRequested)
                {
                    var clockWatch = Stopwatch.StartNew();
                    long ping; PingFailure? failure; string? warning = null;
                    try
                    {
                        var outcome = await probe(target, settings.TimeoutMs, run.Token);
                        if (run.Token.IsCancellationRequested) break;   // a reply that lands after the end is not counted
                        ping = outcome.Rtt; failure = outcome.Failure; warning = outcome.Warning;
                    }
                    catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // Whatever a probe throws is a failed ping of THIS host, not the end of the run.
                        ping = -1; failure = PingFailure.From(ex);
                    }

                    var now = clock();
                    lock (gate)
                    {
                        session.Add(ping, failure, now);
                        session.Notice = warning;
                        var change = session.Monitor.Update(ping);
                        incidents.Observe(now, session.Address, ping, session.LastFailure, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs);
                        if (change != HostChange.None && alert is not null)
                        {
                            TimeSpan? outage = change == HostChange.Up
                                ? incidents.Incidents.LastOrDefault(i => i.Host == session.Address && i.Kind == IncidentKind.Outage)?.Duration(now) : null;
                            alert(new WebhookEvent(session.Address, change,
                                AlertMessage.For(session.DisplayName, change, session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs, outage), outage, now));
                        }
                    }

                    // Probes start one interval apart (see Cadence).
                    try { await Task.Delay(Cadence.WaitMs(settings.IntervalMs, clockWatch.ElapsedMilliseconds), run.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            async Task Checkpoints()
            {
                if (checkpoint is null) return;
                try
                {
                    while (true)
                    {
                        await Task.Delay(checkpointEvery ?? CheckpointEvery, run.Token);
                        checkpoint(Build());
                    }
                }
                catch (OperationCanceledException) { /* the end */ }
            }

            var loops = targets.Select((t, i) => Loop(sessions[i], t.Target)).ToList();
            loops.Add(Checkpoints());
            await Task.WhenAll(loops);

            var report = Build();
            checkpoint?.Invoke(report);

            long sent = sessions.Sum(s => s.Stats.Sent);
            string summary = sent == 0
                ? "No probe was made."
                : $"{sent} probes on {sessions.Count} target(s) in {IncidentLog.FormatDuration(report.GeneratedAt - start)}. {report.IncidentSummary}";
            int code = sent == 0 ? ErrorCode : incidents.Incidents.Count > 0 ? IncidentCode : OkCode;
            return new HeadlessResult(code, summary, report);
        }
    }
}
