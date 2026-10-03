using System.Globalization;
using System.Net;
using System.Text;

namespace PingTool
{
    internal sealed record HostReport(string Address, string IpText, HostState State, int Sent, int Lost,
        double LossPercent, double? Min, double? Avg, double? Max, double? Jitter, IReadOnlyList<long> History);

    internal sealed record ReportData(DateTimeOffset GeneratedAt, DateTimeOffset RunStart, string Machine, string Version,
        int IntervalMs, int TimeoutMs, int PacketSize, int DegradedLatencyMs, int DegradedLossPercent,
        IReadOnlyList<HostReport> Hosts, string? Diagnosis, string IncidentSummary, IReadOnlyList<Incident> Incidents);

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

        public static string Build(ReportData d)
        {
            var c = CultureInfo.InvariantCulture;
            var h = new StringBuilder();
            string E(string s) => WebUtility.HtmlEncode(s);
            string N(double? v) => v is null ? "-" : v.Value.ToString("0.#", c);
            string T(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", c);

            h.AppendLine("<!DOCTYPE html>");
            h.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\" /><title>PingTool diagnostic report</title>");
            h.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;max-width:900px}"
                + "h1{font-size:22px}h2{font-size:16px;margin-top:28px;border-bottom:1px solid #ccc;padding-bottom:4px}"
                + "table{border-collapse:collapse;width:100%;font-size:13px}th,td{border:1px solid #ccc;padding:4px 8px;text-align:left}"
                + "th{background:#f0f0f0}td.n{text-align:right}.box{background:#fff4e5;border:1px solid #e0b060;padding:8px 12px}"
                + ".ok{background:#eef8ee;border-color:#8c8}.meta td:first-child{width:200px;font-weight:600}"
                + ".down{color:#b00020;font-weight:600}.deg{color:#9a6700;font-weight:600}svg{border:1px solid #ccc;background:#fafafa}"
                + "small{color:#555}h3{font-size:14px;margin-top:18px}"
                + "pre{background:#f6f6f6;border:1px solid #ccc;padding:8px;font-size:12px;overflow-x:auto}</style></head><body>");

            h.AppendLine("<h1>PingTool diagnostic report</h1>");

            h.AppendLine("<table class=\"meta\">");
            Row(h, "Generated", T(d.GeneratedAt), E);
            Row(h, "Monitoring period", $"{T(d.RunStart)} to {T(d.GeneratedAt)} ({IncidentLog.FormatDuration(d.GeneratedAt - d.RunStart)})", E);
            Row(h, "Computer", d.Machine, E);
            Row(h, "PingTool version", d.Version, E);
            Row(h, "Probe settings", $"one echo request every {d.IntervalMs} ms, timeout {d.TimeoutMs} ms, {d.PacketSize} bytes", E);
            Row(h, "Degraded when (last 10 pings)", $"loss at least {d.DegradedLossPercent}% or average latency at least {d.DegradedLatencyMs} ms", E);
            h.AppendLine("</table>");

            h.AppendLine("<h2>Where is the fault?</h2>");
            if (d.Diagnosis is null)
                h.AppendLine("<p>Not enough targets to compare yet: at least two must have been pinged.</p>");
            else
                h.AppendLine(c, $"<p class=\"box{(d.Hosts.All(x => x.State == HostState.Up) ? " ok" : "")}\">{E(d.Diagnosis)}</p>");

            h.AppendLine("<h2>Targets</h2>");
            h.AppendLine("<table><tr><th>Target</th><th>Address</th><th>State</th><th>Sent</th><th>Lost</th><th>Loss %</th>"
                + "<th>Min ms</th><th>Avg ms</th><th>Max ms</th><th>Jitter ms</th></tr>");
            foreach (var x in d.Hosts)
            {
                string state = x.State switch { HostState.Down => "<td class=\"down\">Down</td>", HostState.Degraded => "<td class=\"deg\">Degraded</td>", _ => "<td>Up</td>" };
                h.AppendLine(c, $"<tr><td>{E(x.Address)}</td><td>{E(x.IpText)}</td>{state}<td class=\"n\">{x.Sent.ToString(c)}</td><td class=\"n\">{x.Lost.ToString(c)}</td>"
                    + $"<td class=\"n\">{N(x.LossPercent)}</td><td class=\"n\">{N(x.Min)}</td><td class=\"n\">{N(x.Avg)}</td><td class=\"n\">{N(x.Max)}</td><td class=\"n\">{N(x.Jitter)}</td></tr>");
            }
            h.AppendLine("</table>");

            h.AppendLine("<h2>Latency over the last pings</h2>");
            h.AppendLine("<p><small>Each graph has its own scale (top = the highest value of that target). A red tick on the baseline is a lost ping.</small></p>");
            foreach (var x in d.Hosts)
            {
                long top = Math.Max(50, x.History.Count == 0 ? 0 : x.History.Max());
                h.AppendLine(c, $"<p><b>{E(x.Address)}</b> <small>(0 to {top.ToString(c)} ms, {x.History.Count.ToString(c)} pings)</small><br />");
                h.AppendLine(Svg(x.History, top, c));
                h.AppendLine("</p>");
            }

            h.AppendLine("<h2>Incidents</h2>");
            h.AppendLine(c, $"<p>{E(d.IncidentSummary)}</p>");
            if (d.Incidents.Count > 0)
            {
                h.AppendLine("<table><tr><th>Target</th><th>Type</th><th>Start</th><th>End</th><th>Duration</th><th>Failed pings</th><th>Cause / detail</th><th>#</th></tr>");
                foreach (var i in d.Incidents)
                {
                    bool outage = i.Kind == IncidentKind.Outage;
                    string detail = outage ? i.Cause : $"{N(i.LossPercent)}% loss, average {N(i.AvgMs)} ms";
                    h.AppendLine(c, $"<tr><td>{E(i.Host)}</td><td>{(outage ? "Outage" : "Slowdown")}</td><td>{E(T(i.Start))}</td>"
                        + $"<td>{(i.End is null ? "ongoing" : E(T(i.End.Value)))}</td><td>{E(IncidentLog.FormatDuration(i.Duration(d.GeneratedAt)))}</td>"
                        + $"<td class=\"n\">{(outage ? i.FailedPings.ToString(c) : "-")}</td><td>{E(detail)}</td><td class=\"n\">{i.Occurrence.ToString(c)}</td></tr>");
                }

                h.AppendLine("</table>");
                h.AppendLine("<p><small>An outage starts at its first failed ping. A slowdown is dated when detected (after 10 pings). "
                    + "# = how many times this target had that kind of incident.</small></p>");

                // The route to the target when each outage began, hop by hop, with what changed.
                foreach (var i in d.Incidents.Where(x => x.Path is not null))
                    h.AppendLine(c, $"<h3>Network path when {E(i.Host)} went down ({E(T(i.Start))})</h3><pre>{E(i.PathText())}</pre>");
            }

            h.AppendLine("<hr /><p><small>Generated by PingTool. A ping measures whether a target answers and how long the round trip takes; "
                + "it cannot tell a dead service from a firewall rule, so the diagnosis above is a hint, not a proof.</small></p>");
            h.AppendLine("</body></html>");
            return h.ToString();
        }

        private static void Row(StringBuilder h, string label, string value, Func<string, string> e) =>
            h.AppendLine(CultureInfo.InvariantCulture, $"<tr><td>{e(label)}</td><td>{e(value)}</td></tr>");

        // Oldest on the left. A lost ping breaks the line and leaves a red tick; a lone success is a dot.
        private static string Svg(IReadOnlyList<long> history, long top, CultureInfo c)
        {
            var s = new StringBuilder();
            s.Append(c, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{GraphWidth}\" height=\"{GraphHeight}\" viewBox=\"0 0 {GraphWidth} {GraphHeight}\">");
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
