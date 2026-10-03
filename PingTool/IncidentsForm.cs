using System.Globalization;

namespace PingTool
{
    // The incident timeline, newest first. A snapshot: it does not refresh while open.
    internal sealed class IncidentsForm : Form
    {
        public IncidentsForm(IReadOnlyList<Incident> incidents, string summary, DateTimeOffset now)
        {
            Text = "PingTool - Incidents";
            ClientSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
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
                Location = new Point(10, 40),
                Size = new Size(700, 190),
            };
            list.Columns.Add("Host", 120);
            list.Columns.Add("Type", 70);
            list.Columns.Add("Start", 130);
            list.Columns.Add("End", 130);
            list.Columns.Add("Duration", 80);
            list.Columns.Add("Failed", 50);
            list.Columns.Add("Cause / detail", 100);
            list.Columns.Add("#", 25);

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
                Location = new Point(10, 238),
                Size = new Size(700, 168),
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
                Text = "An outage starts at its first failed ping. A slowdown is dated when detected (after 10 pings). # = how many times this host had that kind of incident.",
                ForeColor = Color.Silver,
                AutoSize = false,
                Location = new Point(10, 414),
                Size = new Size(600, 34),
            };

            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(620, 418), Size = new Size(90, 28) };
            CancelButton = close;

            Controls.Add(lblSummary);
            Controls.Add(list);
            Controls.Add(details);
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
                : i.PathText().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }
}
