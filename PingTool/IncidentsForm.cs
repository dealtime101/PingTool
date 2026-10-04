using System.Globalization;

namespace PingTool
{
    // The incident timeline, newest first. A snapshot: it does not refresh while open.
    internal sealed class IncidentsForm : Form
    {
        public IncidentsForm(IReadOnlyList<Incident> incidents, string summary, DateTimeOffset now)
        {
            // Positions and sizes are written for 96 DPI: the form scales them to the screen, so that the text, which does grow
            // with the scaling, keeps its room at 125 %, 150 % or 200 % (as TimelineForm and MainForm do).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "PingTool - Incidents";
            ClientSize = new Size(900, 480);
            MinimumSize = SizeFromClientSize(new Size(640, 360));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            var culture = CultureInfo.CurrentCulture;
            string When(DateTimeOffset t) => t.ToLocalTime().ToString("G", culture);

            var list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
            };
            // Column widths are not scaled by the form: written for 96 DPI too, and scaled here.
            int W(int at100) => DpiScale.Scale(new Size(at100, 0), DeviceDpi).Width;
            list.Columns.Add("Host", W(120));
            list.Columns.Add("Type", W(70));
            list.Columns.Add("Start", W(130));
            list.Columns.Add("End", W(130));
            list.Columns.Add("Duration", W(80));
            list.Columns.Add("Failed", W(50));
            var causeColumn = list.Columns.Add("Cause / detail", W(100));
            list.Columns.Add("#", W(25));

            // "Cause / detail" is the most informative column: it takes everything the others leave, now and whenever the list
            // is resized, instead of a width written for one window (a fixed 100 px cut every cause after a few letters).
            void FitCause()
            {
                int others = list.Columns.Cast<ColumnHeader>().Where(c => c != causeColumn).Sum(c => c.Width);
                causeColumn.Width = ColumnFit.Fill(list.ClientSize.Width, SystemInformation.VerticalScrollBarWidth, others, W(120));
            }

            list.HandleCreated += (_, _) => FitCause();
            list.SizeChanged += (_, _) => FitCause();
            FitCause();

            foreach (var i in incidents.Reverse())
            {
                bool outage = i.Kind == IncidentKind.Outage;
                string detail = outage
                    ? i.Cause
                    : string.Format(culture, "{0:0.#}% loss, avg {1} ms", i.LossPercent,
                        i.AvgMs?.ToString("0.#", culture) ?? "-");

                list.Items.Add(new ListViewItem(new[]
                {
                    i.Host,
                    outage ? "Outage" : "Slowdown",
                    When(i.Start),
                    i.End is null ? "ongoing" : When(i.End.Value),
                    IncidentLog.FormatDuration(i.Duration(now)),
                    outage ? i.FailedPings.ToString(culture) : "-",
                    detail,
                    i.Occurrence.ToString(culture),
                }) { Tag = i });
            }

            // The route to the host at the moment of the selected outage.
            var details = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9F),
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
            };
            list.SelectedIndexChanged += (_, _) =>
                details.Text = list.SelectedItems.Count == 0 ? "" : DetailsOf((Incident)list.SelectedItems[0].Tag!);
            if (list.Items.Count > 0) list.Items[0].Selected = true;

            var lblSummary = new Label
            {
                Text = summary,
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(10, 12),
            };

            var lblNote = new Label
            {
                Text = IncidentLog.DatingNote + " # = how many times this host had that kind of incident.",
                ForeColor = Color.Silver,
                AutoSize = false,
                Location = new Point(10, 414),
                Size = new Size(780, 34),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };

            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(800, 418), Size = new Size(90, 28), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            CancelButton = close;

            // The list and the route text share the height: more incidents or a longer route, drag the bar (or enlarge the window).
            var split = new SplitContainer
            {
                Orientation = Orientation.Horizontal,
                Location = new Point(10, 40),
                Size = new Size(880, 366),
                SplitterDistance = 190,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(64, 64, 64),
                Panel1MinSize = 80,
                Panel2MinSize = 60,
            };
            split.Panel1.Controls.Add(list);
            split.Panel2.Controls.Add(details);

            Controls.Add(lblSummary);
            Controls.Add(split);
            Controls.Add(lblNote);
            Controls.Add(close);
        }

        private static string DetailsOf(Incident i)
        {
            if (i.Kind == IncidentKind.Slowdown)
                return "A slowdown has no route capture: the host still answers, only slowly.";

            return i.Path is null
                ? "No route was captured for this outage: the host's address was not known yet, the run was stopped first, or the trace is still running."
                // A multi-line TextBox only breaks lines on CR LF: the route text uses a bare LF.
                : i.PathText().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }
    }
}
