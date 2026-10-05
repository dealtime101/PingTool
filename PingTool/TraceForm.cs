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

            // A control does not dispose a font it was given: this one goes with the text box, not when the finalizer gets to it.
            var mono = new Font(FontFamily.GenericMonospace, 9F);
            output = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = mono,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Location = new Point(10, 10),
                Size = new Size(540, 276),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AccessibleName = "Route to the host",
                Text = "Tracing the route to " + session.Address + " (" + ip + ")...",
            };
            output.Disposed += (_, _) => mono.Dispose();
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

        private int hopsSeen;   // how many hops the window has shown so far

        // A route can take tens of seconds (a second per silent hop): show the hops as they come, so the window is seen to work.
        // Called from the trace, which resumes on this window's thread.
        private void ShowProgress(HostSession session, IPAddress ip, IReadOnlyList<Hop> hops)
        {
            if (IsDisposed) return;
            hopsSeen = hops.Count;
            var partial = new PathCapture { Host = session.Address, Target = ip, Time = DateTimeOffset.Now, Hops = hops.ToList() };
            ReplaceKeepingView("Tracing the route to " + session.Address + " (" + ip + "), " + hops.Count + " hop(s) so far...\r\n"
                + string.Join("\r\n", partial.HopLines()));
        }

        private const int EM_LINESCROLL = 0x00B6;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        // Puts new text in the box. A reader who is at the end follows it down; one who scrolled up to look at the first hops, or selected
        // something to copy, keeps the selection and the line at the top of the view (setting Text alone sends both to the start).
        private void ReplaceKeepingView(string text)
        {
            int lastLine = output.GetLineFromCharIndex(Math.Max(0, output.TextLength - 1));
            int bottom = output.GetLineFromCharIndex(output.GetCharIndexFromPosition(new Point(2, Math.Max(2, output.ClientSize.Height - 2))));
            int top = output.GetLineFromCharIndex(output.GetCharIndexFromPosition(new Point(2, 2)));
            int selStart = output.SelectionStart, selLength = output.SelectionLength;
            bool follow = LiveTextRule.Follows(selLength, bottom, lastLine);

            output.Text = text;
            if (follow)
            {
                output.SelectionStart = output.TextLength;
                output.ScrollToCaret();
            }
            else
            {
                output.Select(Math.Min(selStart, output.TextLength), selLength);
                if (output.IsHandleCreated) SendMessage(output.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)top);
            }
        }

        private async Task TraceAsync(HostSession session, IPAddress ip)
        {
            try
            {
                var path = await TraceRunner.RunAsync(session.Address, ip, PingHopProbe.Create(1000), DateTimeOffset.Now, token: cts.Token,
                    giveUpAfter: TraceRunner.MaxHops,   // asked for, watched and cancellable: no early stop on a few firewalled hops
                    progress: hops => ShowProgress(session, ip, hops));
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
                // The trace is over and failed: the reason is shown whatever the reader was looking at (it is the one line they need).
                output.Text = PathCapture.FailureText(output.Text, hopsSeen, ex.Message);   // the hops found so far stay on screen
                output.SelectionStart = output.TextLength;
                output.ScrollToCaret();
            }
        }
    }
}
