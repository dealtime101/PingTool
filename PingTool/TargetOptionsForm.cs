namespace PingTool
{
    // "Name and limits of this host": a readable name, and limits of its own instead of the global ones. A limit left unticked
    // follows the global value (shown greyed). Limits apply from the next Start; the name at once.
    internal sealed class TargetOptionsForm : Form
    {
        private readonly TextBox label;
        private readonly (CheckBox Own, NumericUpDown Value)[] limits;

        public TargetOptions? Result { get; private set; }

        public TargetOptionsForm(string address, TargetOptions? current, int globalSlow, int globalLoss, int globalDown)
        {
            Text = "PingTool - Name and limits";
            ClientSize = new Size(380, 232);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            Controls.Add(new Label { Text = address, ForeColor = Color.Silver, AutoSize = false, Location = new Point(12, 10), Size = new Size(356, 18) });
            Controls.Add(new Label { Text = "Name (shown in the list, the alerts and the report)", ForeColor = Color.White, AutoSize = true, Location = new Point(12, 36) });
            label = new TextBox
            {
                Location = new Point(12, 56), Size = new Size(356, 23), MaxLength = TargetOptions.MaxLabelLength,
                Text = current?.Label ?? "", AccessibleName = "Name of the target",
            };
            Controls.Add(label);

            limits = new[]
            {
                Row(0, "Slow above (ms)", Limits.DegradedLatencyMs, current?.SlowMs, globalSlow),
                Row(1, "Loss at least (%)", Limits.DegradedLossPercent, current?.LossPercent, globalLoss),
                Row(2, "Down after (failures)", Limits.DownAfter, current?.DownAfter, globalDown),
            };

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(190, 192), Size = new Size(84, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(284, 192), Size = new Size(84, 28) };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ok.Click += (_, _) => Result = TargetOptions.Normalize(new TargetOptions(
                label.Text,
                limits[0].Own.Checked ? (int)limits[0].Value.Value : null,
                limits[1].Own.Checked ? (int)limits[1].Value.Value : null,
                limits[2].Own.Checked ? (int)limits[2].Value.Value : null));
        }

        // One limit: "own value" box + the number (greyed and showing the global value while unticked).
        private (CheckBox, NumericUpDown) Row(int index, string text, (int Min, int Max) range, int? own, int global)
        {
            int y = 92 + index * 30;
            var box = new CheckBox { Text = text, ForeColor = Color.White, AutoSize = true, Location = new Point(12, y + 2), Checked = own is not null, AccessibleName = text + ": own value" };
            var number = new NumericUpDown
            {
                Minimum = range.Min, Maximum = range.Max, Location = new Point(250, y), Size = new Size(118, 23),
                Value = Math.Clamp(own ?? global, range.Min, range.Max), Enabled = own is not null, AccessibleName = text,
            };
            box.CheckedChanged += (_, _) =>
            {
                number.Enabled = box.Checked;
                if (!box.Checked) number.Value = Math.Clamp(global, range.Min, range.Max);
            };
            Controls.Add(box);
            Controls.Add(number);
            return (box, number);
        }
    }
}
