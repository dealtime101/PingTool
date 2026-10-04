namespace PingTool
{
    // Paints a TimelineData: all of a session in one picture, with a time axis.
    // Where things go is decided by TimelineLayout (tested); this only paints.
    internal sealed class TimelineChart : Control
    {
        private const int LeftMargin = 46, TopMargin = 6, BottomMargin = 24, RightMargin = 8;
        private TimelineData data = Timeline.Build(Array.Empty<LogEntry>(), "", 1);
        private IReadOnlyList<Incident> incidents = Array.Empty<Incident>();
        private IReadOnlyList<NetworkEvent> networkEvents = Array.Empty<NetworkEvent>();

        public TimelineChart()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(40, 40, 40);
        }

        public int PlotWidth => Math.Max(20, Width - LeftMargin - RightMargin);

        public void Show(TimelineData d, IReadOnlyList<Incident> inc, IReadOnlyList<NetworkEvent>? network = null)
        {
            data = d;
            incidents = inc;
            networkEvents = network ?? Array.Empty<NetworkEvent>();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int plotW = PlotWidth, plotH = Height - TopMargin - BottomMargin;
            using var grey = new SolidBrush(Color.Silver);

            if (data.IsEmpty || plotH < 20)
            {
                g.DrawString("No data yet: start pinging this host.", Font, grey, LeftMargin, TopMargin + 4);
                return;
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;   // like the labels of the controls
            var shapes = TimelineLayout.Build(data, incidents, plotW, plotH, networkEvents);
            float cell = Math.Max(1f, (float)plotW / data.Buckets.Count);

            g.TranslateTransform(LeftMargin, TopMargin);

            foreach (var (x0, x1, kind) in shapes.Bands)
            {
                using var band = new SolidBrush(kind == IncidentKind.Outage ? Color.FromArgb(70, 255, 99, 71) : Color.FromArgb(60, 255, 165, 0));
                g.FillRectangle(band, x0, 0, x1 - x0, plotH);
            }

            // A change of this PC's own network (Wi-Fi, VPN, cable, wake from sleep): a cyan dotted line, to be read against the outages.
            using var netPen = new Pen(Color.Cyan, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            foreach (float x in shapes.NetworkMarkers ?? Array.Empty<float>()) g.DrawLine(netPen, x, 0, x, plotH);

            using var barBrush = new SolidBrush(Color.FromArgb(90, 192, 192, 192));
            foreach (var (x, yMin, yMax) in shapes.Bars)
                g.FillRectangle(barBrush, x, yMax, Math.Max(1f, cell - 1), Math.Max(1f, yMin - yMax + 1));

            // The stronger the red, the larger the share of lost pings in that column.
            foreach (var (x, w, fraction) in shapes.LossCells)
            {
                using var loss = new SolidBrush(Color.FromArgb((int)(70 + 185 * fraction), 255, 99, 71));
                g.FillRectangle(loss, x, plotH - 8, w, 8);
            }

            using var avg = new Pen(Color.LimeGreen, 1.5f);
            using var dot = new SolidBrush(Color.LimeGreen);
            foreach (var run in shapes.AvgRuns)
            {
                if (run.Count == 1) g.FillEllipse(dot, run[0].X - 2f, run[0].Y - 2f, 4f, 4f);
                else g.DrawLines(avg, run.Select(p => new PointF(p.X, p.Y)).ToArray());
            }

            using var axis = new Pen(Color.FromArgb(120, 192, 192, 192));
            g.DrawLine(axis, 0, plotH, plotW, plotH);
            foreach (var (time, label) in Timeline.Ticks(data.From, data.To, Math.Max(2, plotW / 90)))
            {
                float x = (float)((time - data.From).Ticks / (double)data.Span.Ticks * plotW);
                g.DrawLine(axis, x, plotH, x, plotH + 4);
                g.DrawString(label, Font, grey, x - g.MeasureString(label, Font).Width / 2, plotH + 5);
            }

            // A column whose highest reply is above the scale is cut at the top: a small mark says so.
            using var cutBrush = new SolidBrush(Color.OrangeRed);
            foreach (float x in shapes.Clipped ?? Array.Empty<float>())
                g.FillPolygon(cutBrush, new[] { new PointF(x - 3, 0), new PointF(x + 3, 0), new PointF(x, 5) });

            g.ResetTransform();
            g.DrawString(data.IsClipped ? Loc.T("timeline.axis.clipped", data.TopMs, data.PeakMs) : data.TopMs + " ms", Font, grey, 2, TopMargin);
            g.DrawString("0", Font, grey, 2, TopMargin + plotH - Font.Height);
        }
    }

    // The whole session of one host, from the first ping to the last. A snapshot of the log.
    internal sealed class TimelineForm : Form
    {
        private readonly IReadOnlyCollection<LogEntry> entries;
        private readonly string? droppedNote;
        private readonly IReadOnlyList<NetworkEvent> networkEvents;
        private readonly IReadOnlyList<Incident> incidents;
        private readonly ComboBox hostBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(10, 10), Size = new Size(260, 23), AccessibleName = "Target" };
        private readonly TimelineChart chart = new() { Location = new Point(10, 42), Size = new Size(740, 290), AccessibleName = "Session timeline" };
        // Room for four lines: the three of the summary and, when the log let pings go, the note about it.
        private readonly Label summary = new() { ForeColor = Color.White, AutoSize = false, Location = new Point(10, 340), Size = new Size(740, 62) };

        // droppedNote: what to say when the log has let its oldest pings go (null = nothing lost).
        public TimelineForm(IReadOnlyCollection<LogEntry> entries, IReadOnlyList<Incident> incidents, IEnumerable<string> hosts, string? selected,
                            string? droppedNote = null, IReadOnlyList<NetworkEvent>? networkEvents = null)
        {
            this.networkEvents = networkEvents ?? Array.Empty<NetworkEvent>();
            this.droppedNote = droppedNote;
            this.entries = entries;
            this.incidents = incidents;

            Text = "PingTool - Session timeline";
            ClientSize = new Size(760, 440);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            var note = new Label
            {
                Text = "Each column is a slice of the session: grey = lowest to highest reply, green = average, red at the bottom = lost pings (stronger = more), tinted background = outage (red) or slowdown (orange).",
                ForeColor = Color.Silver, AutoSize = false, Location = new Point(10, 402), Size = new Size(640, 34),
            };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(660, 408), Size = new Size(90, 28) };
            CancelButton = close;

            var list = hosts.ToArray();
            hostBox.Items.AddRange(list.Cast<object>().ToArray());
            hostBox.SelectedIndexChanged += (_, _) => Redraw();
            Controls.AddRange(new Control[] { hostBox, chart, summary, note, close });

            int first = Timeline.InitialHost(list, selected);
            if (first >= 0) hostBox.SelectedIndex = first;
            else
            {
                // No target at all: an empty box has no index 0 to select (that threw). Say so, as the chart does without data.
                hostBox.Enabled = false;
                summary.Text = Timeline.Describe(Timeline.Build(Array.Empty<LogEntry>(), "", 10));
            }
        }

        private void Redraw()
        {
            if (hostBox.SelectedItem is not string host) return;

            // About one column per 4 pixels.
            var data = Timeline.Build(entries, host, Math.Max(10, chart.PlotWidth / 4));
            chart.Show(data, incidents, networkEvents);
            string? network = data.IsEmpty ? null : Timeline.DescribeNetwork(networkEvents, data.From, data.To);
            summary.Text = Timeline.Describe(data) + (network is null ? "" : "\n" + network) + (droppedNote is null ? "" : "\n" + droppedNote);
        }
    }
}
