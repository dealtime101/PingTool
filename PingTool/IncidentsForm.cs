using System.Globalization;

namespace PingTool
{
    // The incident timeline, newest first. A snapshot: it does not refresh while open.
    internal sealed class IncidentsForm : Form
    {
        public IncidentsForm(IReadOnlyList<Incident> incidents, string summary, DateTimeOffset now)
        {
            Text = "PingTool - Incidents";
            ClientSize = new Size(720, 370);
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
                Size = new Size(700, 280),
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
                }));
            }

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
                Location = new Point(10, 326),
                Size = new Size(600, 34),
            };

            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(620, 330), Size = new Size(90, 28) };
            CancelButton = close;

            Controls.Add(lblSummary);
            Controls.Add(list);
            Controls.Add(lblNote);
            Controls.Add(close);
        }
    }
}
