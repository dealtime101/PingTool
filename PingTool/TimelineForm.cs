namespace PingTool
{
    // Paints a TimelineData: all of a session in one picture, with a time axis.
    // Where things go is decided by TimelineLayout (tested); this only paints.
    internal sealed class TimelineChart : Control
    {
        // Margins in pixels at 100 %, scaled to the screen like the text they hold.
        private int Px(int at100) => DpiScale.Scale(new Size(at100, 0), DeviceDpi).Width;
        private int TopMargin => Px(6);
        private int BottomMargin => Px(24);
        private int RightMargin => Px(8);

        // The room on the left for the top value ("50 ms", or "5000 ms (peak 12000)"): as wide as that text, never narrower than 46 px at 100 %.
        private int measuredLeft;
        private int LeftMargin => Math.Max(Px(46), measuredLeft);
        private TimelineData data = Timeline.Build(Array.Empty<LogEntry>(), "", 1);
        private IReadOnlyList<Incident> incidents = Array.Empty<Incident>();
        private IReadOnlyList<NetworkEvent> networkEvents = Array.Empty<NetworkEvent>();

        public TimelineChart()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(40, 40, 40);
            // Reachable with Tab, so that a screen reader can land on it and read what it shows (see TimelineAccessibleObject).
            TabStop = true;
            AccessibleRole = AccessibleRole.Chart;
        }

        // What a screen reader gets: the picture says nothing, so the figures are written out - computed when asked, not at each redraw.
        private sealed class TimelineAccessibleObject(TimelineChart owner) : ControlAccessibleObject(owner)
        {
            public override string? Description => Timeline.Accessible(owner.data, owner.incidents, owner.networkEvents);
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new TimelineAccessibleObject(this);

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        private static string TopLabel(TimelineData d) => d.IsClipped ? Loc.T("timeline.axis.clipped", d.TopMs, d.PeakMs) : d.TopMs + " ms";

        public int PlotWidth => Math.Max(20, Width - LeftMargin - RightMargin);

        public void Show(TimelineData d, IReadOnlyList<Incident> inc, IReadOnlyList<NetworkEvent>? network = null)
        {
            data = d;
            incidents = inc;
            networkEvents = network ?? Array.Empty<NetworkEvent>();
            measuredLeft = TextRenderer.MeasureText(TopLabel(d), Font).Width + Px(10);
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
            PaintChart(e.Graphics);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
        }

        private void PaintChart(Graphics g)
        {
            int plotW = PlotWidth, plotH = Height - TopMargin - BottomMargin;
            using var grey = new SolidBrush(Color.Silver);

            string? nothing = Timeline.NothingToDraw(data.IsEmpty, plotH);
            if (nothing is not null)
            {
                g.DrawString(nothing, Font, grey, LeftMargin, TopMargin + 4);
                return;
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;   // like the labels of the controls
            var shapes = TimelineLayout.Build(data, incidents, plotW, plotH, networkEvents);
            float cell = Math.Max(1f, (float)plotW / data.Buckets.Count);

            g.TranslateTransform(LeftMargin, TopMargin);

            foreach (var (x0, x1, kind) in shapes.Bands)
            {
                // Two codings, not one: an outage is HATCHED red, a slowdown is a plain orange tint. Red against orange at this opacity
                // is almost the same for a red-green colour-blind eye; a stripe pattern is not.
                using Brush band = kind == IncidentKind.Outage
                    ? new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.WideUpwardDiagonal, Color.FromArgb(170, 255, 99, 71), Color.FromArgb(50, 255, 99, 71))
                    : new SolidBrush(Color.FromArgb(60, 255, 165, 0));
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
            float labelsEnd = float.NegativeInfinity;   // where the last label drawn ends: the next one must start after it
            foreach (var (time, label) in Timeline.Ticks(data.From, data.To, Math.Max(2, plotW / 90)))
            {
                float x = (float)((time - data.From).Ticks / (double)data.Span.Ticks * plotW);
                g.DrawLine(axis, x, plotH, x, plotH + 4);
                float w = g.MeasureString(label, Font).Width;
                // Inside the control (the plot is drawn shifted by the left margin): a mark at the very end must not lose its label.
                float start = TimelineLayout.LabelStart(x, w, 2 - LeftMargin, plotW + RightMargin - 1);
                if (start < labelsEnd + 4) continue;   // held in by an edge it would print over its neighbour: the mark alone says it
                g.DrawString(label, Font, grey, start, plotH + 5);
                labelsEnd = start + w;
            }

            // A column whose highest reply is above the scale is cut at the top: a small mark says so.
            using var cutBrush = new SolidBrush(Color.OrangeRed);
            foreach (float x in shapes.Clipped ?? Array.Empty<float>())
                g.FillPolygon(cutBrush, new[] { new PointF(x - 3, 0), new PointF(x + 3, 0), new PointF(x, 5) });

            g.ResetTransform();
            g.DrawString(TopLabel(data), Font, grey, 2, TopMargin);
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
        private readonly TimelineChart chart = new() { Location = new Point(10, 42), Size = new Size(740, 246), AccessibleName = "Session timeline", Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        // Room for six lines: the summary (up to four with the peak and the incidents), the network changes and, when the log let pings go, the note about it.
        private readonly Label summary = new() { ForeColor = Color.White, AutoSize = false, Location = new Point(10, 294), Size = new Size(740, 106), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };

        // droppedNote: what to say when the log has let its oldest pings go (null = nothing lost).
        public TimelineForm(IReadOnlyCollection<LogEntry> entries, IReadOnlyList<Incident> incidents, IEnumerable<string> hosts, string? selected,
                            string? droppedNote = null, IReadOnlyList<NetworkEvent>? networkEvents = null)
        {
            this.networkEvents = networkEvents ?? Array.Empty<NetworkEvent>();
            this.droppedNote = droppedNote;
            this.entries = entries;
            this.incidents = incidents;

            // Positions and sizes below are written for 96 DPI: the form scales them to the screen (like MainForm does), so
            // that the text, which does grow with the scaling, still has its room at 125 %, 150 % or 200 %.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "PingTool - Session timeline";
            ClientSize = new Size(760, 440);
            MinimumSize = SizeFromClientSize(new Size(560, 400));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            var note = new Label
            {
                Text = "Each column is a slice of the session: grey = lowest to highest reply, green = average, red at the bottom = lost pings (stronger = more), striped red background = outage, plain orange tint = slowdown.",
                ForeColor = Color.Silver, AutoSize = false, Location = new Point(10, 402), Size = new Size(640, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(660, 408), Size = new Size(90, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            CancelButton = close;

            var list = hosts.ToArray();
            hostBox.Items.AddRange(list.Cast<object>().ToArray());
            hostBox.SelectedIndexChanged += (_, _) => Redraw();
            chart.SizeChanged += (_, _) => Redraw();   // a wider chart gets more columns
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
