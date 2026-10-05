using System.Globalization;
using System.Net;
using System.Text;

namespace PingTool
{
    internal sealed record HostReport(string Address, string IpText, HostState State, long Sent, long Lost,
        double? LossPercent, double? Min, double? Avg, double? Max, double? Jitter, IReadOnlyList<long> History,
        // Pings sent and lost per clock hour, for the hour-by-day grid (null = not recorded).
        IReadOnlyList<HourCell>? Hours = null,
        // The name the user gave the target and the limits it ran with, when they are not the defaults (see TargetOptions).
        string? Label = null, string? Limits = null,
        // A warning the last probe came with although it succeeded (a certificate about to expire).
        string? Notice = null);

    // The probe settings a run was STARTED with: the boxes can be changed after Stop, and a report must describe the measures it holds.
    internal sealed record RunSettings(int IntervalMs, int TimeoutMs, int PacketSize, int DegradedLatencyMs, int DegradedLossPercent, int DownAfter);

    internal sealed record ReportData(DateTimeOffset GeneratedAt, DateTimeOffset RunStart, string Machine, string Version,
        int IntervalMs, int TimeoutMs, int PacketSize, int DegradedLatencyMs, int DegradedLossPercent,
        IReadOnlyList<HostReport> Hosts, string? Diagnosis, string IncidentSummary, IReadOnlyList<Incident> Incidents,
        // A report rebuilt from log files (see LogReplay): the period ends at the last ping of the log (not "now"), and what a log
        // does not record - the probe settings, the computer, the limits used - is not invented.
        bool FromLogFile = false, DateTimeOffset? PeriodEnd = null,
        // Changes of the PC's own network during the period (Wi-Fi, VPN, adapter, address, gateway).
        IReadOnlyList<NetworkEvent>? NetworkEvents = null,
        // Consecutive failed pings after which a host was reported down (the default when a log does not record it).
        int DownAfter = HostMonitor.DefaultDownAfter);

    // A self-contained diagnostic report: ONE html file, no script, no external resource, graphs
    // drawn as inline SVG. Meant to be sent to an ISP or an IT team who do not have PingTool.
    //
    // It is written for a reader somewhere else, so it does not use this PC's culture:
    // numbers use a decimal point and times are ISO-like with their UTC offset.
    // Everything the user typed (host names) goes through HtmlEncode. The markup is kept
    // well-formed (every tag closed) so it can also be checked by an XML parser.
    internal static class ReportBuilder
    {
        private const int GraphWidth = 640;
        private const int GraphHeight = 90;
        private const int MinScaleMs = 50;   // the top of a graph is the highest ping, never lower: a quiet host does not fill the height

        public static string Build(ReportData d)
        {
            var c = CultureInfo.InvariantCulture;
            var h = new StringBuilder();
            string E(string s) => WebUtility.HtmlEncode(s);
            string N(double? v) => v is null ? "-" : v.Value.ToString("0.#", c);
            string T(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", c);
            // "Box (192.168.1.1)" when the target has a name, its address otherwise; the address is what the pings went to.
            string Who(HostReport x) => x.Label is null ? E(x.Address) : E(x.Label) + " <small>(" + E(x.Address) + ")</small>";
            // Cut, never rounded up, to two decimals: 99.997 % with a real outage must not read "100 %" in a claim to a provider.
            string Pct(double v) => (Math.Floor(v * 100 + 1e-9) / 100).ToString("0.##", c);

            h.AppendLine("<!DOCTYPE html>");
            h.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\" /><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\" /><title>PingTool diagnostic report</title>");
            h.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;max-width:900px}"
                + "h1{font-size:22px}h2{font-size:16px;margin-top:28px;border-bottom:1px solid #ccc;padding-bottom:4px}"
                + "table{border-collapse:collapse;width:100%;font-size:13px}th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}"
                + "th{background:#f0f0f0}td.n{text-align:right}.box{background:#fff4e5;border:1px solid #e0b060;padding:8px 12px}"
                + ".ok{background:#eef8ee;border-color:#8c8}.meta th{width:200px;font-weight:600;background:none}"
                + ".down{color:#b00020;font-weight:600}.deg{color:#9a6700;font-weight:600}svg{border:1px solid #ccc;background:#fafafa;max-width:100%;height:auto}.w{overflow-x:auto}"
                + "small{color:#555}h3{font-size:14px;margin-top:18px}"
                + "table.grid{width:auto;font-size:10px}table.grid th,table.grid td{padding:0;text-align:center}table.grid th{background:none;border:none;padding:0 2px;font-weight:400}"
                + "table.grid td{width:18px;height:14px;position:relative}.sr{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}.hn{background:#e6e6e6}.h0{background:#9ed89e}.h1{background:#f2e394}.h2{background:#f0a95a}.h3{background:#d9534f}"
                + ".gl{display:inline-block;width:12px;height:12px;border:1px solid #bbb;vertical-align:middle}"
                + "pre{background:#f6f6f6;border:1px solid #ccc;padding:8px;font-size:12px;overflow-x:auto}</style></head><body>");

            h.AppendLine("<h1>PingTool diagnostic report</h1>");

            h.AppendLine("<table class=\"meta\">");
            Row(h, "Generated", T(d.GeneratedAt), E);
            var end = d.PeriodEnd ?? d.GeneratedAt;
            Row(h, "Monitoring period", $"{T(d.RunStart)} to {T(end)} ({IncidentLog.FormatDuration(end - d.RunStart)})", E);
            if (d.FromLogFile) Row(h, "Source", "rebuilt from PingTool log files (the computer and the probe settings are not recorded in them)", E);
            else Row(h, "Computer", d.Machine, E);
            Row(h, "PingTool version", d.Version, E);
            if (!d.FromLogFile) Row(h, "Probe settings", $"one echo request every {d.IntervalMs} ms, timeout {d.TimeoutMs} ms, {d.PacketSize} bytes", E);
            Row(h, d.FromLogFile ? $"Degraded when (last {HostMonitor.WindowSize} pings; default limits, the log does not record them)" : $"Degraded when (last {HostMonitor.WindowSize} pings)",$"loss at least {d.DegradedLossPercent}% or average latency at least {d.DegradedLatencyMs} ms", E);
            Row(h, d.FromLogFile ? "Down when (default limit, the log does not record it)" : "Down when", $"{d.DownAfter} failed pings in a row", E);
            h.AppendLine("</table>");

            // The figures a provider's support asks for first, before any graph.
            if (d.Hosts.Count > 0)
            {
                h.AppendLine("<h2>Availability</h2>");
                h.AppendLine("<table><tr><th>Target</th><th>Availability</th><th>Outages</th><th>Total down</th><th>Average outage</th><th>Longest outage</th><th>Ping loss</th></tr>");
                foreach (var x in d.Hosts)
                {
                    var row = Availability.For(x.Address, d.Incidents, d.RunStart, end);
                    string Dur(TimeSpan? t) => t is null ? "-" : IncidentLog.FormatDuration(t.Value);
                    h.AppendLine(c, $"<tr><td>{Who(x)}</td><td class=\"n\">{(row.AvailabilityPercent is null ? "-" : Pct(row.AvailabilityPercent.Value) + " %")}</td>"
                        + $"<td class=\"n\">{row.Outages.ToString(c)}</td><td class=\"n\">{E(Dur(row.TotalDown))}</td><td class=\"n\">{E(Dur(row.Mean))}</td><td class=\"n\">{E(Dur(row.Longest))}</td>"
                        + $"<td class=\"n\">{(x.LossPercent is null ? "-" : E(Availability.FormatLoss(x.LossPercent.Value, c)) + " %")}</td></tr>");
                }

                h.AppendLine("</table>");
                h.AppendLine("<p><small>Availability = the share of the monitoring period not spent in an outage. An outage runs from the first failed ping to the first "
                    + "reply after it (one still going on counts up to the end of the period); a slowdown is not counted as down. Ping loss = lost pings over pings sent.</small></p>");
            }

            h.AppendLine("<h2>Where is the fault?</h2>");
            if (d.Diagnosis is null)
                h.AppendLine("<p>Not enough targets to compare yet: at least two must have been pinged.</p>");
            else
                h.AppendLine(c, $"<p class=\"box{(d.Hosts.All(x => x.State == HostState.Up) ? " ok" : "")}\">{E(d.Diagnosis)}</p>");

            h.AppendLine("<h2>Targets</h2>");
            h.AppendLine("<table><tr><th>Target</th><th>Address</th><th>State</th><th>Sent</th><th>Lost</th><th>Loss %</th>"
                + "<th>Min ms</th><th>Avg ms</th><th>Max ms</th><th>Jitter ms</th><th>Limits</th></tr>");
            foreach (var x in d.Hosts)
            {
                string state = x.State switch { HostState.Down => "<td class=\"down\">Down</td>", HostState.Degraded => "<td class=\"deg\">Degraded</td>", _ => "<td>Up</td>" };
                h.AppendLine(c, $"<tr><td>{Who(x)}</td><td>{E(x.IpText)}</td>{state}<td class=\"n\">{x.Sent.ToString(c)}</td><td class=\"n\">{x.Lost.ToString(c)}</td>"
                    + $"<td class=\"n\">{(x.LossPercent is null ? "-" : E(Availability.FormatLoss(x.LossPercent.Value, c)))}</td><td class=\"n\">{N(x.Min)}</td><td class=\"n\">{N(x.Avg)}</td><td class=\"n\">{N(x.Max)}</td><td class=\"n\">{N(x.Jitter)}</td><td>{E(x.Limits ?? (d.FromLogFile ? "not recorded" : "global limits"))}</td></tr>");   // a log does not record the limits: do not claim the global ones
            }
            h.AppendLine("</table>");

            foreach (var x in d.Hosts.Where(x => x.Notice is not null))
                h.AppendLine(c, $"<p class=\"box\"><b>{Who(x)}</b>: {E(x.Notice!)}.</p>");

            h.AppendLine("<h2>Latency over the last pings</h2>");
            h.AppendLine(c, $"<p><small>Each graph has its own scale (top = the highest value of that target, but never less than {MinScaleMs.ToString(c)} ms). A red tick on the baseline is a lost ping.</small></p>");
            foreach (var x in d.Hosts)
            {
                long top = Math.Max(MinScaleMs, x.History.Count == 0 ? 0 : x.History.Max());
                h.AppendLine(c, $"<p><b>{Who(x)}</b> <small>(0 to {top.ToString(c)} ms, {x.History.Count.ToString(c)} pings)</small><br />");
                h.AppendLine(Svg(x.History, top, c, GraphAlt(x.Label ?? x.Address, x.History, top, c)));
                h.AppendLine("</p>");
                // The same pings as text, in time order, for who cannot read the curve (the red ticks are the only mark of a loss).
                if (x.History.Count > 0)
                    h.AppendLine(c, $"<details><summary>The {x.History.Count.ToString(c)} pings of {Who(x)} as text, oldest first</summary><p>{E(SamplesText(x.History, c))}</p></details>");
            }

            // When the cuts happen: one grid per target, a row per day, a column per hour, coloured by the share of lost pings.
            var grids = d.Hosts.Where(x => x.Hours is { Count: > 0 }).ToList();
            if (grids.Count > 0)
            {
                h.AppendLine("<h2>Loss by hour</h2>");
                h.AppendLine("<p><small>Each square is one clock hour: <span class=\"hn gl\"></span> no ping, <span class=\"h0 gl\"></span> no loss, "
                    + "<span class=\"h1 gl\"></span> up to 5 %, <span class=\"h2 gl\"></span> up to 30 %, <span class=\"h3 gl\"></span> more than 30 %. "
                    + $"The {Availability.MaxGridDays.ToString(c)} most recent days are shown. Hover a square for its figures.</small></p>");
                foreach (var x in grids)
                {
                    h.AppendLine(c, $"<p><b>{Who(x)}</b></p>");
                    h.Append("<table class=\"grid\"><tr><th></th>");
                    for (int hour = 0; hour < 24; hour++) h.Append(c, $"<th>{hour}</th>");
                    h.AppendLine("</tr>");
                    foreach (var (day, hours) in Availability.Grid(x.Hours!))
                    {
                        h.Append(c, $"<tr><th>{day:yyyy-MM-dd}</th>");
                        for (int hour = 0; hour < 24; hour++)
                        {
                            // The figures are in the cell as text too (hidden on screen, read by a screen reader, kept when the colours
                            // are not shown): the colour and the hover text alone say nothing to everyone who cannot use them.
                            string figures = E(Availability.Title(day, hour, hours[hour]));
                            h.Append(c, $"<td class=\"{Availability.LossClass(hours[hour])}\" title=\"{figures}\"><span class=\"sr\">{figures}</span></td>");
                        }
                        h.AppendLine("</tr>");
                    }

                    h.AppendLine("</table>");
                }
            }

            h.AppendLine("<h2>Incidents</h2>");
            h.AppendLine(c, $"<p>{E(d.IncidentSummary)}</p>");
            if (d.Incidents.Count > 0)
            {
                h.AppendLine("<table><tr><th>Target</th><th>Type</th><th>Start</th><th>End</th><th>Duration</th><th>Failed pings</th><th>Cause / detail</th><th>#</th></tr>");
                foreach (var i in d.Incidents)
                {
                    bool outage = i.Kind == IncidentKind.Outage;
                    string detail = outage ? i.Cause : $"{Availability.FormatLoss(i.LossPercent, c)}% loss, average {N(i.AvgMs)} ms";
                    h.AppendLine(c, $"<tr><td>{E(i.Host)}</td><td>{(outage ? "Outage" : "Slowdown")}</td><td>{E(T(i.Start))}</td>"
                        + $"<td>{(i.End is null ? "ongoing" : E(T(i.End.Value)))}</td><td>{E(IncidentLog.FormatDuration(i.Duration(end)))}</td>"
                        + $"<td class=\"n\">{(outage ? i.FailedPings.ToString(c) : "-")}</td><td>{E(detail)}</td><td class=\"n\">{i.Occurrence.ToString(c)}</td></tr>");
                }

                h.AppendLine("</table>");
                h.AppendLine("<p><small>" + E(IncidentLog.DatingNote) + " "
                    + "# = how many times this target had that kind of incident.</small></p>");

                // The route to the target when each outage began, hop by hop, with what changed.
                foreach (var i in d.Incidents.Where(x => x.Path is not null))
                    h.AppendLine(c, $"<h3>Network path when {E(i.Host)} went down ({E(T(i.Start))})</h3><pre>{E(i.PathText())}</pre>");
            }

            // What happened on THIS side of the line: a cut that matches a Wi-Fi or VPN change is not the provider's.
            var network = (d.NetworkEvents ?? Array.Empty<NetworkEvent>()).OrderBy(n => n.Time).ToList();
            if (network.Count > 0)
            {
                h.AppendLine("<h2>Changes of this PC's network</h2>");
                h.AppendLine("<table><tr><th>Time</th><th>Change</th></tr>");
                foreach (var n in network) h.AppendLine(c, $"<tr><td>{E(T(n.Time))}</td><td>{E(n.Text)}</td></tr>");
                h.AppendLine("</table>");
                h.AppendLine("<p><small>An outage that starts when the PC's own network changes (a Wi-Fi access point, a VPN, a cable, a wake from sleep) "
                    + "comes from this side, not from the provider.</small></p>");
            }

            h.AppendLine("<hr /><p><small>Generated by PingTool. A ping measures whether a target answers and how long the round trip takes; "
                + "it cannot tell a dead service from a firewall rule, so the diagnosis above is a hint, not a proof.</small></p>");
            h.AppendLine("</body></html>");
            // A wide table scrolls inside its own box on a phone instead of widening the page (every cell text is HTML-encoded,
            // so a literal "<table" can only be ours).
            return Accessible(h.ToString().Replace("<table", "<div class=\"w\"><table").Replace("</table>", "</table></div>"));
        }

        // Lets assistive technology link a cell to its headings: a row made only of <th> is the header (<thead>, scope="col"),
        // and a <th> that opens a row of cells names that row (scope="row"). Headings are our literals, cell text is encoded.
        internal static string Accessible(string html)
        {
            html = System.Text.RegularExpressions.Regex.Replace(html, "<tr>((?:<th>[^<]*</th>)+)</tr>",
                m => "<thead><tr>" + m.Groups[1].Value.Replace("<th>", "<th scope=\"col\">") + "</tr></thead>");
            return System.Text.RegularExpressions.Regex.Replace(html, "<tr><th>([^<]*)</th><td", "<tr><th scope=\"row\">$1</th><td");
        }

        private static void Row(StringBuilder h, string label, string value, Func<string, string> e) =>
            h.AppendLine(CultureInfo.InvariantCulture, $"<tr><th scope=\"row\">{e(label)}</th><td>{e(value)}</td></tr>");

        // What the picture says, for a screen reader or a text-only mail client: the figures the eye reads off the curve.
        internal static string GraphAlt(string name, IReadOnlyList<long> history, long top, CultureInfo c)
        {
            var ok = history.Where(v => v >= 0).ToList();
            int lost = history.Count - ok.Count;
            string head = $"Latency of {name}: ";
            if (history.Count == 0) return head + "no ping yet.";
            string figures = ok.Count == 0 ? "no ping answered"
                : $"minimum {ok.Min().ToString(c)} ms, average {ok.Average().ToString("0.#", c)} ms, maximum {ok.Max().ToString(c)} ms";
            return $"{head}{figures}, {lost.ToString(c)} of {history.Count.ToString(c)} pings lost; scale 0 to {top.ToString(c)} ms, oldest ping on the left.";
        }

        // "12, 14, lost, 13 (ms; oldest first)": every ping in time order, a loss in words.
        internal static string SamplesText(IReadOnlyList<long> history, CultureInfo c) =>
            string.Join(", ", history.Select(v => v < 0 ? "lost" : v.ToString(c))) + " (ms, oldest first).";

        // Oldest on the left. A lost ping breaks the line and leaves a red tick; a lone success is a dot.
        private static string Svg(IReadOnlyList<long> history, long top, CultureInfo c, string alt)
        {
            var s = new StringBuilder();
            string a = WebUtility.HtmlEncode(alt);
            s.Append(c, $"<svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"{a}\" width=\"{GraphWidth}\" height=\"{GraphHeight}\" viewBox=\"0 0 {GraphWidth} {GraphHeight}\"><title>{a}</title>");
            int n = history.Count;
            double step = n > 1 ? (GraphWidth - 8.0) / (n - 1) : 0;
            double X(int i) => 4 + i * step;
            double Y(long v) => GraphHeight - 4 - (GraphHeight - 8.0) * v / top;
            string F(double v) => v.ToString("0.#", c);

            var run = new List<string>();
            void Flush()
            {
                if (run.Count == 1) s.Append(c, $"<circle cx=\"{run[0].Split(',')[0]}\" cy=\"{run[0].Split(',')[1]}\" r=\"1.5\" fill=\"#0b6bcb\" />");
                else if (run.Count > 1) s.Append(c, $"<polyline points=\"{string.Join(" ", run)}\" fill=\"none\" stroke=\"#0b6bcb\" stroke-width=\"1.5\" />");
                run.Clear();
            }

            for (int i = 0; i < n; i++)
            {
                if (history[i] < 0)
                {
                    Flush();
                    s.Append(c, $"<line x1=\"{F(X(i))}\" y1=\"{GraphHeight - 4}\" x2=\"{F(X(i))}\" y2=\"{GraphHeight - 14}\" stroke=\"#c62828\" stroke-width=\"2\" />");
                }
                else run.Add($"{F(X(i))},{F(Y(history[i]))}");
            }

            Flush();
            s.Append("</svg>");
            return s.ToString();
        }
    }
}
