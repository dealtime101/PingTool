using System.Net;

namespace PingTool
{
    // "Where does the path stop?" on demand: the same route trace PingTool captures at an outage,
    // run now for the host picked in the list. It does not need the monitoring to be running.
    internal sealed class TraceForm : Form
    {
        private readonly CancellationTokenSource cts = new();
        private readonly TextBox output;

        public TraceForm(HostSession session, IPAddress ip)
        {
            // Positions and sizes below are written for 96 DPI: the form scales them to the screen (like TimelineForm and
            // IncidentsForm), and the trace box grows with the window since a long route does not fit in a fixed one.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            Text = "PingTool - Route to " + session.Address;
            ClientSize = new Size(560, 330);
            MinimumSize = SizeFromClientSize(new Size(400, 200));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(64, 64, 64);

            output = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9F),
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Location = new Point(10, 10),
                Size = new Size(540, 276),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AccessibleName = "Route to the host",
                Text = "Tracing the route to " + session.Address + " (" + ip + ")...",
            };
            var close = new Button { Text = "Close", Location = new Point(470, 294), Size = new Size(80, 28), DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            CancelButton = close;
            Controls.Add(output);
            Controls.Add(close);

            // Closing the window abandons the trace: nobody is waiting for its answer any more.
            FormClosing += (_, _) => cts.Cancel();
            Shown += async (_, _) => await TraceAsync(session, ip);
        }

        // FormClosing has already cancelled the source: a trace still running holds a token that stays valid after this.
        protected override void Dispose(bool disposing)
        {
            if (disposing) cts.Dispose();
            base.Dispose(disposing);
        }

        private async Task TraceAsync(HostSession session, IPAddress ip)
        {
            try
            {
                var path = await TraceRunner.RunAsync(session.Address, ip, PingHopProbe.Create(1000), DateTimeOffset.Now, token: cts.Token);
                if (IsDisposed) return;

                // Against the route seen while the host was healthy, when this run has one.
                var notes = PathCapture.Compare(session.BaselinePath, path);
                string text = path.Describe() + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : "");
                // A multi-line TextBox only breaks lines on CR LF: a bare LF would run the hops together.
                output.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            }
            catch (OperationCanceledException)
            {
                // The window was closed.
            }
            catch (Exception ex)
            {
                // Tested inside the block, not in a filter: a failure that lands after the window is gone must be swallowed here,
                // because nothing above this method (an async void handler) can catch it and PingTool would stop on it.
                if (IsDisposed) return;
                output.Text = "The trace failed: " + ex.Message;
            }
        }
    }
}
