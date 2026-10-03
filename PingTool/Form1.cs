using System;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;

namespace PingTool
{
    public partial class MainForm : Form
    {
        private bool isRunning;
        private bool hasRun;
        private CancellationTokenSource? cts;
        private readonly List<HostSession> sessions = new();
        private HostSession? selected;
        private readonly PingLog log = new();
        private readonly Settings settings = Settings.Load(Settings.DefaultPath);
        private static readonly string[] DefaultAddresses = { "google.ca", "8.8.8.8", "1.1.1.1", "192.168.0.1" };
        // Everything below the top block: hidden in compact mode.
        private Control[] detailControls = Array.Empty<Control>();

        private static readonly Size FullSize = new(580, 420);
        // Tall enough for address, big result, Start/Stop and the stats label.
        private static readonly Size CompactSize = new(284, 250);
        private readonly ToolTip toolTip = new();
        private readonly NotifyIcon notifyIcon = new() { Icon = SystemIcons.Application, Text = "PingTool" };

        public MainForm()
        {
            InitializeComponent();
            FormClosed += (_, _) =>
            {
                notifyIcon.Dispose();
                toolTip.Dispose();
            };

            detailControls = new Control[]
            {
                graphLatency, lblInterval, lblTimeout, lblSize, numInterval, numTimeout, numSize,
                chkAlert, lstHosts, btnAddHost, btnRemoveHost, btnExport,
            };
            notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
            ApplySettings();
            FormClosing += (_, _) => SaveSettings();
        }

        private void chkCompact_CheckedChanged(object? sender, EventArgs e) => ApplyCompact(chkCompact.Checked);

        // Compact = small, always on top, and minimizing goes to the notification
        // area instead of the taskbar (see OnResize).
        private void ApplyCompact(bool compact)
        {
            TopMost = compact;
            foreach (var c in detailControls) c.Visible = !compact;
            ClientSize = compact ? CompactSize : FullSize;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (chkCompact.Checked && WindowState == FormWindowState.Minimized)
            {
                Hide();
                notifyIcon.Visible = true;
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            notifyIcon.Visible = false;
        }

        // Recent addresses first, then the built-in ones not already listed.
        private void RefreshAddressList()
        {
            string typed = cmbAddress.Text;
            cmbAddress.Items.Clear();
            cmbAddress.Items.AddRange(settings.Recent.Concat(DefaultAddresses)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray<object>());
            cmbAddress.Text = typed;
        }

        private void ApplySettings()
        {
            RefreshAddressList();
            if (settings.Address.Length > 0) cmbAddress.Text = settings.Address;

            numInterval.Value = Math.Clamp(settings.IntervalMs, (int)numInterval.Minimum, (int)numInterval.Maximum);
            numTimeout.Value = Math.Clamp(settings.TimeoutMs, (int)numTimeout.Minimum, (int)numTimeout.Maximum);
            numSize.Value = Math.Clamp(settings.PacketSize, (int)numSize.Minimum, (int)numSize.Maximum);
            chkAlert.Checked = settings.Alert;
            chkCompact.Checked = settings.Compact;

            foreach (var host in settings.Hosts) AddHost(host);
        }

        private void SaveSettings()
        {
            settings.Address = cmbAddress.Text.Trim();
            settings.Hosts = sessions.Select(s => s.Address).ToList();
            settings.IntervalMs = (int)numInterval.Value;
            settings.TimeoutMs = (int)numTimeout.Value;
            settings.PacketSize = (int)numSize.Value;
            settings.Alert = chkAlert.Checked;
            settings.Compact = chkCompact.Checked;

            try
            {
                settings.Save(Settings.DefaultPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the preferences must not stop the app from closing.
                Debug.WriteLine("Settings not saved: " + ex.Message);
            }
        }

        private HostSession? AddHost(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            var existing = sessions.Find(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var session = new HostSession(address);
            sessions.Add(session);
            var item = new ListViewItem(new[] { address, "-", "-", "-" }) { Tag = session };
            lstHosts.Items.Add(item);
            item.Selected = true;
            return session;
        }

        private void btnAddHost_Click(object? sender, EventArgs e) => AddHost(cmbAddress.Text.Trim());

        private void btnRemoveHost_Click(object? sender, EventArgs e)
        {
            if (selected == null) return;
            var item = lstHosts.SelectedItems.Count > 0 ? lstHosts.SelectedItems[0] : null;
            sessions.Remove(selected);
            if (item != null) lstHosts.Items.Remove(item);
            selected = null;
            RenderSelected();
        }

        private void btnExport_Click(object? sender, EventArgs e)
        {
            if (log.Count == 0)
            {
                MessageBox.Show("Nothing to export yet: start pinging first.", "PingTool");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "CSV file (*.csv)|*.csv",
                FileName = $"pingtool-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                // UTF-8 with BOM so Excel reads accented host names correctly.
                using var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true));
                log.WriteCsv(writer);
            }
            catch (IOException ex)
            {
                MessageBox.Show("Could not write the file: " + ex.Message, "PingTool");
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show("Could not write the file: " + ex.Message, "PingTool");
            }
        }

        private void lstHosts_SelectedIndexChanged(object? sender, EventArgs e)
        {
            selected = lstHosts.SelectedItems.Count > 0 ? (HostSession)lstHosts.SelectedItems[0].Tag! : null;
            RenderSelected();
        }

        private async void btnStartStop_Click(object sender, EventArgs e)
        {
            if (!isRunning)
            {
                // The list is what gets pinged. The box only matters when the list is
                // empty (old behaviour: ping the typed address); it is read once, here,
                // and the loops use the address stored in their session.
                if (sessions.Count == 0)
                {
                    string address = cmbAddress.Text.Trim();
                    if (string.IsNullOrWhiteSpace(address))
                    {
                        MessageBox.Show("No address");
                        return;
                    }

                    AddHost(address);
                }

                foreach (var s in sessions) settings.AddRecent(s.Address);
                RefreshAddressList();
                SaveSettings();

                isRunning = true;
                hasRun = true;
                btnStartStop.Text = "Stop";
                SetSettingsEnabled(false);
                ShowRunState();

                foreach (var s in sessions) s.Reset();
                log.Clear();
                foreach (ListViewItem item in lstHosts.Items) RenderRow(item);
                RenderSelected();
                // One source per run, owned by this call: a quick Stop then Start
                // must not have the old run dispose the new run's source.
                var runCts = new CancellationTokenSource();
                cts = runCts;

                try
                {
                    await Task.WhenAll(sessions.ToArray().Select(s => StartPinging(s, runCts.Token)));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ERROR in ping run: " + ex);
                }
                finally
                {
                    runCts.Dispose();
                    if (ReferenceEquals(cts, runCts)) cts = null;
                }
            }
            else
            {
                isRunning = false;
                btnStartStop.Text = "Start";
                SetSettingsEnabled(true);
                ShowRunState();
                RenderSelected();

                cts?.Cancel();
            }
        }

        // Word and colour: the value on screen is only live while "Running".
        private void ShowRunState()
        {
            lblState.Text = isRunning ? "Running" : hasRun ? "Stopped" : "";
            lblState.ForeColor = isRunning ? Color.LimeGreen : Color.Silver;
        }

        private void SetSettingsEnabled(bool enabled)
        {
            cmbAddress.Enabled = enabled;
            numInterval.Enabled = enabled;
            numTimeout.Enabled = enabled;
            numSize.Enabled = enabled;
            btnAddHost.Enabled = enabled;
            btnRemoveHost.Enabled = enabled;
        }

        private async Task StartPinging(HostSession session, CancellationToken token)
        {
            string address = session.Address;
            // The numeric boxes are locked while running, so one read is enough.
            int interval = (int)numInterval.Value;
            int timeout = (int)numTimeout.Value;
            byte[] buffer = new byte[(int)numSize.Value];

            using Ping ping = new Ping();
            // Stop must not wait out a ping already in flight (up to the timeout).
            using var cancelPing = token.Register(ping.SendAsyncCancel);

            // Resolve first so the IP shows even for a host that never answers.
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(address, token);
                if (resolved.Length > 0) session.SetIp(resolved[0]);
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"DNS lookup of {address} failed: {ex.SocketErrorCode}");
                session.SetUnresolved();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (session == selected) RenderSelected();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var reply = await ping.SendPingAsync(address, timeout, buffer);

                    // A reply that lands after Stop must not touch the display.
                    if (token.IsCancellationRequested) break;

                    if (reply.Status == IPStatus.Success)
                    {
                        // What actually answered beats what DNS listed first.
                        session.SetIp(reply.Address);
                        UpdatePingUI(session, reply.RoundtripTime);
                    }
                    else
                        UpdatePingUI(session, -1, PingFailure.From(reply.Status));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    Debug.WriteLine($"Ping {address} threw: {ex}");
                    UpdatePingUI(session, -1, PingFailure.From(ex));
                }

                try
                {
                    await Task.Delay(interval, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void UpdatePingUI(HostSession session, long ping, PingFailure? failure = null)
        {
            session.Add(ping, failure);
            var why = session.LastFailure;
            log.Add(DateTimeOffset.Now, session.Address, why?.Short ?? "OK", ping >= 0 ? ping : null, why?.Detail ?? "");
            Alert(session, session.Monitor.Update(ping >= 0));

            foreach (ListViewItem item in lstHosts.Items)
                if (item.Tag == session) RenderRow(item);

            if (session == selected) RenderSelected();
        }

        private static void RenderRow(ListViewItem item)
        {
            var s = (HostSession)item.Tag!;
            item.SubItems[1].Text = s.Last is null ? "-" : s.LastFailure?.Short ?? s.Last + " ms";
            item.SubItems[2].Text = s.Stats.Avg is null ? "-" : s.Stats.Avg.Value.ToString("0.#", CultureInfo.CurrentCulture);
            item.SubItems[3].Text = s.Stats.Sent == 0 ? "-" : s.Stats.LossPercent.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        }

        // Big value, stats and graph all follow the host selected in the list.
        private void RenderSelected()
        {
            Text = selected == null ? "PingTool" : "PingTool - " + selected.Address;
            graphLatency.Show(selected?.History);
            UpdateStatsUI();

            long? ping = selected?.Last;
            if (ping is null)
            {
                lblPingResult.Text = "---";
                lblPingResult.ForeColor = Color.Silver;
            }
            else
            {
                ShowPing(ping.Value, selected?.LastFailure);

                // Stopped: keep the number but drop the green/orange/red verdict,
                // which would pass an old reading off as a live one.
                if (!isRunning && hasRun)
                {
                    lblPingResult.ForeColor = Color.Silver;
                    toolTip.SetToolTip(lblPingResult, "Stopped: last value, no longer live");
                }
            }
        }

        private void ShowPing(long ping, PingFailure? failure)
        {
            // The full sentence is on hover; the label only has room for a word.
            toolTip.SetToolTip(lblPingResult, failure?.Detail ?? "");

            if (ping >= 0)
            {
                lblPingResult.Text = ping + " ms";

                if (ping < 50)
                    lblPingResult.ForeColor = Color.LimeGreen;
                else if (ping < 100)
                    lblPingResult.ForeColor = Color.Orange;
                else
                    lblPingResult.ForeColor = Color.Tomato;
            }
            else
            {
                lblPingResult.Text = (failure ?? PingFailure.Timeout).Short;
                lblPingResult.ForeColor = Color.Tomato;
            }
        }

        private void Alert(HostSession session, HostChange change)
        {
            if (change == HostChange.None || !chkAlert.Checked) return;

            string host = session.Address;
            bool down = change == HostChange.Down;
            (down ? System.Media.SystemSounds.Hand : System.Media.SystemSounds.Asterisk).Play();
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(5000, "PingTool",
                down ? $"{host} is down" : $"{host} is back up",
                down ? ToolTipIcon.Error : ToolTipIcon.Info);
        }

        private void UpdateStatsUI()
        {
            string ms(double? v) => v is null ? "-" : v.Value.ToString("0.#");
            var stats = selected?.Stats ?? new SessionStats();
            lblStats.Text =
                $"Min {ms(stats.Min)} / Avg {ms(stats.Avg)} / Max {ms(stats.Max)} ms\n" +
                $"Jitter {ms(stats.Jitter)} ms | Loss {stats.LossPercent:0.#}% ({stats.Lost}/{stats.Sent})\n" +
                (selected?.IpText ?? "-");
        }
    }

    // Live latency trace of one host's history (HostSession.HistorySize pings,
    // so three minutes at one per second). A timeout is a red tick on the
    // baseline, not a zero, so a lost packet cannot be mistaken for a fast one.
    internal sealed class LatencyGraph : Control
    {
        private const int MaxSamples = HostSession.HistorySize;
        private IReadOnlyCollection<long> samples = Array.Empty<long>();

        public LatencyGraph()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(40, 40, 40);
        }

        // Draws the host's own queue: it is repainted, never copied.
        public void Show(IReadOnlyCollection<long>? history)
        {
            samples = history ?? Array.Empty<long>();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            long top = Math.Max(50, samples.Count == 0 ? 0 : samples.Max());
            using var grey = new SolidBrush(Color.Silver);
            g.DrawString(top + " ms", Font, grey, 2, 0);
            g.DrawString("0", Font, grey, 2, Height - Font.Height);

            float step = (Width - 1f) / (MaxSamples - 1);
            float x0 = Width - 1 - (samples.Count - 1) * step;
            float Y(long v) => Height - 1 - (Height - 1f) * v / top;

            using var line = new Pen(Color.LimeGreen, 1.5f);
            using var lost = new Pen(Color.Red, 2f);
            PointF? prev = null;
            int i = 0;
            foreach (var v in samples)
            {
                float x = x0 + i++ * step;
                if (v < 0)
                {
                    g.DrawLine(lost, x, Height - 1, x, Height - 8);
                    prev = null;
                    continue;
                }
                var p = new PointF(x, Y(v));
                if (prev is PointF q) g.DrawLine(line, q, p);
                prev = p;
            }
        }
    }
}