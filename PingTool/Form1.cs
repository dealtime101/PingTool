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
        // Set when the window starts closing: late callbacks must leave the screen alone.
        private bool closing;
        private DateTimeOffset runStart = DateTimeOffset.Now;
        private CancellationTokenSource? cts;
        private readonly List<HostSession> sessions = new();
        private HostSession? selected;
        private readonly PingLog log = new();
        // Every ping on disk as it happens (when "Save log to disk" is ticked); written every few seconds.
        private AutoLog? autoLog;
        private bool autoLogWarned;
        private readonly System.Windows.Forms.Timer autoLogTimer = new() { Interval = 5000 };
        private readonly IncidentLog incidents = new();
        // Route captures running in the background; the run waits for them before releasing its token.
        private List<Task> pathCaptures = new();
        private readonly Settings settings = Settings.Load(Settings.DefaultPath);
        private static readonly string[] DefaultAddresses = { "google.ca", "8.8.8.8", "1.1.1.1", "192.168.0.1" };
        // Everything below the top block: hidden in compact mode.
        private Control[] detailControls = Array.Empty<Control>();

        private static readonly Size FullSize = new(580, 519);
        // Tall enough for address, big result, Start/Stop and the stats label.
        private static readonly Size CompactSize = new(284, 282);
        private readonly ToolTip toolTip = new();
        private readonly NotifyIcon notifyIcon = new() { Icon = SystemIcons.Application, Text = "PingTool" };

        // How the program was started (command line); None when opened normally.
        private readonly StartupOptions startup;

        public MainForm() : this(null)
        {
        }

        internal MainForm(StartupOptions? startup)
        {
            this.startup = startup ?? StartupOptions.None;
            InitializeComponent();
            Text = AppVersion.Title(null);
            toolTip.SetToolTip(cmbAddress, ProbeTarget.Help);
            toolTip.SetToolTip(cboProfile, "Profile = the target list and all settings, under a name. Pick one to load it; type a name and press Save to keep the current setup.");
            FormClosed += (_, _) =>
            {
                notifyIcon.Dispose();
                toolTip.Dispose();
            };

            detailControls = new Control[]
            {
                graphLatency, lblInterval, lblTimeout, lblSize, numInterval, numTimeout, numSize,
                lblSlow, numSlow, lblLoss, numLoss, lblDownAfter, numDownAfter,
                chkAlert, chkSaveLog, lstHosts, btnAddHost, btnRemoveHost, btnExport, btnIncidents, btnReport, btnTimeline, btnImportProfiles, btnExportProfiles, cboProfile, btnSaveProfile, btnDeleteProfile, lblDiagnosis, chkCompare,
            };
            notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
            ApplySettings();
            autoLogTimer.Tick += (_, _) => FlushAutoLog();

            // --minimized hides the window in the notification area (alerts still show as balloons);
            // --start then begins the monitoring: no click needed.
            Shown += (_, _) =>
            {
                if (startup.Minimized)
                {
                    notifyIcon.Visible = true;
                    Hide();
                }

                if (startup.Start && !isRunning) btnStartStop_Click(this, EventArgs.Empty);
            };

            // Right-click (or the menu key) on a host: where does the path to it stop?
            var hostMenu = new ContextMenuStrip();
            hostMenu.Items.Add("Trace route to this host").Click += (_, _) => TraceSelected();
            hostMenu.Opening += (_, e) => e.Cancel = selected is null;
            lstHosts.ContextMenuStrip = hostMenu;
            FormClosing += (_, _) =>
            {
                // Stop the loops BEFORE FormClosed disposes the notification icon and the
                // tooltip: a ping answering during the close would touch them (and the
                // controls) after disposal.
                closing = true;
                cts?.Cancel();
                autoLogTimer.Stop();
                FlushAutoLog();
                SaveSettings();
            };
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
            numSlow.Value = Math.Clamp(settings.DegradedLatencyMs, (int)numSlow.Minimum, (int)numSlow.Maximum);
            numLoss.Value = Math.Clamp(settings.DegradedLossPercent, (int)numLoss.Minimum, (int)numLoss.Maximum);
            numDownAfter.Value = Math.Clamp(settings.DownAfter, (int)numDownAfter.Minimum, (int)numDownAfter.Maximum);
            chkAlert.Checked = settings.Alert;
            chkSaveLog.Checked = settings.SaveLog;
            toolTip.SetToolTip(chkSaveLog, "Every ping is appended to a CSV file per host and per day, in " +
                (settings.LogFolder.Length > 0 ? settings.LogFolder : AutoLog.DefaultFolder) + ". Choose before Start.");
            chkCompact.Checked = settings.Compact;
            RefreshProfileList();
            cboProfile.Text = settings.ActiveProfile;

            // Targets and interval from the command line win for this launch (and are not saved: see SaveSettings).
            if (startup.IntervalMs is int ms)
                numInterval.Value = Math.Clamp(ms, (int)numInterval.Minimum, (int)numInterval.Maximum);
            foreach (var host in startup.Hosts.Count > 0 ? startup.Hosts : settings.Hosts) AddHost(host);
        }

        private void SaveSettings()
        {
            settings.Address = cmbAddress.Text.Trim();
            // What the command line imposed for this launch is not what the user chose: keep the saved values.
            if (startup.Hosts.Count == 0) settings.Hosts = sessions.Select(s => s.Address).ToList();
            if (startup.IntervalMs is null) settings.IntervalMs = (int)numInterval.Value;
            settings.TimeoutMs = (int)numTimeout.Value;
            settings.PacketSize = (int)numSize.Value;
            settings.DegradedLatencyMs = (int)numSlow.Value;
            settings.DegradedLossPercent = (int)numLoss.Value;
            settings.DownAfter = (int)numDownAfter.Value;
            settings.Alert = chkAlert.Checked;
            settings.SaveLog = chkSaveLog.Checked;
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

            var session = new HostSession(address, (int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
            sessions.Add(session);
            var item = new ListViewItem(new[] { address, "-", "-", "-" }) { Tag = session };
            lstHosts.Items.Add(item);
            item.Selected = true;
            return session;
        }

        private void RefreshProfileList()
        {
            string typed = cboProfile.Text;
            cboProfile.Items.Clear();
            cboProfile.Items.AddRange(settings.Profiles.Select(p => p.Name).ToArray<object>());
            cboProfile.Text = typed;
        }

        // Keeps the current setup (targets and every setting) under the name typed in the box.
        private void btnSaveProfile_Click(object? sender, EventArgs e)
        {
            if (!ProfileBook.TryName(cboProfile.Text, out string name, out string error))
            {
                MessageBox.Show(error, "PingTool");
                return;
            }

            var hosts = sessions.Select(s => s.Address).ToList();
            if (hosts.Count == 0)
            {
                string typed = cmbAddress.Text.Trim();
                if (!IsValidTarget(typed)) return;
                hosts.Add(typed);
            }

            if (ProfileBook.Find(settings.Profiles, name) is not null
                && MessageBox.Show($"Replace the profile \"{name}\"?", "PingTool", MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;

            var profile = new Profile
            {
                Name = name,
                Hosts = hosts,
                IntervalMs = (int)numInterval.Value,
                TimeoutMs = (int)numTimeout.Value,
                PacketSize = (int)numSize.Value,
                Alert = chkAlert.Checked,
                DegradedLatencyMs = (int)numSlow.Value,
                DegradedLossPercent = (int)numLoss.Value,
                DownAfter = (int)numDownAfter.Value,
            };

            if (!ProfileBook.Upsert(settings.Profiles, profile))
            {
                MessageBox.Show($"At most {ProfileBook.MaxProfiles} profiles: delete one first.", "PingTool");
                return;
            }

            settings.ActiveProfile = name;
            RefreshProfileList();
            cboProfile.Text = name;
            SaveSettings();
        }

        private void btnDeleteProfile_Click(object? sender, EventArgs e)
        {
            var profile = ProfileBook.Find(settings.Profiles, cboProfile.Text);
            if (profile is null)
            {
                MessageBox.Show("Pick the profile to delete in the list.", "PingTool");
                return;
            }

            if (MessageBox.Show($"Delete the profile \"{profile.Name}\"?", "PingTool", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            ProfileBook.Remove(settings.Profiles, profile.Name);
            settings.ActiveProfile = "";
            cboProfile.Text = "";
            RefreshProfileList();
            SaveSettings();
        }

        // Every saved profile, as a file to keep or to give to a colleague.
        private void btnExportProfiles_Click(object? sender, EventArgs e)
        {
            if (settings.Profiles.Count == 0)
            {
                MessageBox.Show("No profile saved yet: set up the targets and settings, then Save a profile first.", "PingTool");
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Filter = "PingTool profiles (*.json)|*.json",
                FileName = "PingTool-profiles.json",
                DefaultExt = "json",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                File.WriteAllText(dialog.FileName, ProfileExchange.Export(settings.Profiles), new System.Text.UTF8Encoding(false));
                MessageBox.Show($"{settings.Profiles.Count} profile(s) written to {dialog.FileName}.", "PingTool");
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

        // Reads a profiles file. Nothing of yours is replaced before the user has been shown which
        // profiles would be.
        private void btnImportProfiles_Click(object? sender, EventArgs e)
        {
            if (isRunning) return;

            using var dialog = new OpenFileDialog { Filter = "PingTool profiles (*.json)|*.json|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            string text;
            try
            {
                if (new FileInfo(dialog.FileName).Length > ProfileExchange.MaxFileBytes)
                {
                    MessageBox.Show("That file is too large to be a list of profiles.", "PingTool");
                    return;
                }

                text = File.ReadAllText(dialog.FileName);
            }
            catch (IOException ex)
            {
                MessageBox.Show("Could not read the file: " + ex.Message, "PingTool");
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                MessageBox.Show("Could not read the file: " + ex.Message, "PingTool");
                return;
            }

            if (!ProfileExchange.TryParse(text, out var imported, out string error))
            {
                MessageBox.Show(error, "PingTool");
                return;
            }

            var replaced = ProfileExchange.Replaced(settings.Profiles, imported.Profiles);
            string left = imported.ProfilesDropped + imported.TargetsDropped > 0
                ? $"\n\nLeft out as unusable: {imported.ProfilesDropped} profile(s), {imported.TargetsDropped} target(s)."
                : "";
            string question = $"Import {imported.Profiles.Count} profile(s)?"
                + (replaced.Count > 0 ? "\n\nThese of yours will be REPLACED: " + string.Join(", ", replaced) + "." : "")
                + left;
            if (MessageBox.Show(question, "PingTool", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            int notFitting = ProfileExchange.Merge(settings.Profiles, imported.Profiles);
            RefreshProfileList();
            SaveSettings();
            if (notFitting > 0)
                MessageBox.Show($"{notFitting} profile(s) did not fit: at most {ProfileBook.MaxProfiles} profiles, delete some first.", "PingTool");
        }

        private void cboProfile_SelectionChangeCommitted(object? sender, EventArgs e)
        {
            var profile = ProfileBook.Find(settings.Profiles, cboProfile.SelectedItem?.ToString());
            if (profile is not null) ApplyProfile(profile);
        }

        // Replaces the target list and the settings with the profile's. Only while stopped: the box
        // is locked during a run.
        private void ApplyProfile(Profile profile)
        {
            if (isRunning) return;

            numSlow.Value = Math.Clamp(profile.DegradedLatencyMs, (int)numSlow.Minimum, (int)numSlow.Maximum);
            numLoss.Value = Math.Clamp(profile.DegradedLossPercent, (int)numLoss.Minimum, (int)numLoss.Maximum);
            numDownAfter.Value = Math.Clamp(profile.DownAfter, (int)numDownAfter.Minimum, (int)numDownAfter.Maximum);
            numInterval.Value = Math.Clamp(profile.IntervalMs, (int)numInterval.Minimum, (int)numInterval.Maximum);
            numTimeout.Value = Math.Clamp(profile.TimeoutMs, (int)numTimeout.Minimum, (int)numTimeout.Maximum);
            numSize.Value = Math.Clamp(profile.PacketSize, (int)numSize.Minimum, (int)numSize.Maximum);
            chkAlert.Checked = profile.Alert;

            sessions.Clear();
            lstHosts.Items.Clear();
            selected = null;
            int skipped = 0;
            foreach (var host in profile.Hosts)
            {
                if (ProbeTarget.TryParse(host, out _, out _)) AddHost(host);
                else skipped++;
            }

            // The figures of the previous setup mean nothing for this one.
            log.Clear();
            incidents.Clear();
            UpdateIncidentButton();
            hasRun = false;
            ShowRunState();
            RenderSelected();

            settings.ActiveProfile = profile.Name;
            SaveSettings();

            if (skipped > 0)
                MessageBox.Show($"{skipped} address(es) of this profile are not valid and were skipped.", "PingTool");
        }

        // False (with a message) when the text is not a valid target.
        private static bool IsValidTarget(string address)
        {
            if (ProbeTarget.TryParse(address, out _, out string problem)) return true;
            MessageBox.Show(problem, "PingTool");
            return false;
        }

        private void btnAddHost_Click(object? sender, EventArgs e)
        {
            string address = cmbAddress.Text.Trim();
            if (IsValidTarget(address)) AddHost(address);
        }

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
                using (var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true)))
                    log.WriteCsv(writer);

                // The file is not the whole session once the log has let its oldest pings go: say so.
                if (log.DroppedNote is string dropped)
                    MessageBox.Show("Exported, but incomplete.\n\n" + dropped + "\nThe file starts at "
                        + log.Entries.First().Time.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)
                        + ".\n\nTick \"Save the log to disk\" before Start to keep everything.", "PingTool");
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
                    if (!IsValidTarget(address)) return;

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

                // The limits may have been changed since the hosts were added: each session takes the
                // ones in the boxes (it builds a fresh monitor with them).
                foreach (var s in sessions)
                {
                    s.ApplyThresholds((int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
                    s.Reset();
                }
                runStart = DateTimeOffset.Now;
                log.Clear();
                StartAutoLog();
                incidents.Clear();
                UpdateIncidentButton();
                foreach (ListViewItem item in lstHosts.Items) RenderRow(item);
                RenderSelected();
                // One source per run, owned by this call: a quick Stop then Start
                // must not have the old run dispose the new run's source.
                var runCts = new CancellationTokenSource();
                cts = runCts;
                // This run's own route captures: a newer run (quick Stop then Start) gets its own list,
                // so this one never waits for, or clears, someone else's.
                var runCaptures = new List<Task>();
                pathCaptures = runCaptures;

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
                    // The loops ended by themselves (every host failed), not through Stop: the run
                    // is over, so the screen says so NOW. Only for THIS run: after a quick Stop then
                    // Start, the old run finishing must not end the new one. It comes BEFORE waiting
                    // for the captures: nobody cancelled them, and a route trace can last ~20 s, during
                    // which the screen would have stayed on "Running" with nothing being measured.
                    if (ReferenceEquals(cts, runCts) && isRunning) FinishRun();

                    // A capture still tracing uses the token: let it end (Stop cancels it) before the
                    // token's source is released.
                    await Task.WhenAll(runCaptures.ToArray());

                    runCts.Dispose();
                    if (ReferenceEquals(cts, runCts)) cts = null;   // not a newer run's token
                }
            }
            else
            {
                FinishRun();
                cts?.Cancel();
            }
        }

        // A new run starts a new log. Whatever the previous run could not write yet gets one last try.
        private void StartAutoLog()
        {
            FlushAutoLog();
            string folder = settings.LogFolder.Length > 0 ? settings.LogFolder : AutoLog.DefaultFolder;
            autoLog = chkSaveLog.Checked ? new AutoLog(folder) : null;
            autoLogWarned = false;
            // The timer keeps running after Stop: pings still in flight are written by the next tick.
            autoLogTimer.Enabled = autoLog is not null;
        }

        // A file that cannot be written (open in a spreadsheet, disk full) never stops the monitoring:
        // one balloon says so, the entries wait and go out at the next tick that works.
        private void FlushAutoLog()
        {
            if (autoLog is null) return;

            if (autoLog.Flush()) autoLogWarned = false;
            else if (!autoLogWarned && !closing)
            {
                autoLogWarned = true;
                notifyIcon.Visible = true;
                notifyIcon.ShowBalloonTip(5000, "PingTool", "Log file not written, will retry: " + autoLog.LastError, ToolTipIcon.Warning);
            }
        }

        // Back to the idle screen: button, locked settings, state word, stale-value cue.
        private void FinishRun()
        {
            if (closing) return;
            isRunning = false;
            btnStartStop.Text = "Start";
            SetSettingsEnabled(true);
            ShowRunState();
            RenderSelected();
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
            numSlow.Enabled = enabled;
            numLoss.Enabled = enabled;
            numDownAfter.Enabled = enabled;
            chkSaveLog.Enabled = enabled;
            btnAddHost.Enabled = enabled;
            btnRemoveHost.Enabled = enabled;
            cboProfile.Enabled = enabled;
            btnSaveProfile.Enabled = enabled;
            btnDeleteProfile.Enabled = enabled;
            btnImportProfiles.Enabled = enabled;
        }

        // One host's loop. An error it cannot survive (0.0.0.0 and :: make Dns throw
        // ArgumentException, a 300-character name ArgumentOutOfRangeException) ends THAT
        // host and shows why. It must not escape: Task.WhenAll would fail and the screen
        // would stay on "Running" with nothing being measured.
        private async Task StartPinging(HostSession session, CancellationToken token)
        {
            try
            {
                await PingLoop(session, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ping loop for {session.Address} ended: {ex}");
                UpdatePingUI(session, -1, PingFailure.From(ex));
            }
        }

        private async Task PingLoop(HostSession session, CancellationToken token)
        {
            string address = session.Address;
            // The box validates what is typed, but a hand-edited settings.json can hold anything:
            // a bad address ends THIS host with the reason (see StartPinging).
            if (!ProbeTarget.TryParse(address, out var target, out string problem))
                throw new ArgumentException(problem);

            // The numeric boxes are locked while running, so one read is enough.
            int interval = (int)numInterval.Value;
            int timeout = (int)numTimeout.Value;
            byte[] buffer = new byte[(int)numSize.Value];

            using Ping ping = new Ping();
            // Stop must not wait out a ping already in flight (up to the timeout).
            using var cancelPing = token.Register(ping.SendAsyncCancel);

            // Resolve first so the IP shows even for a host that never answers.
            // Not for dns://, whose probe IS the lookup and sets the address itself.
            if (target.Kind != ProbeKind.Dns)
            {
                try
                {
                    var resolved = await Dns.GetHostAddressesAsync(target.Host, token);
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
            }

            if (session == selected) RenderSelected();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Ping, TCP connect, web request or name lookup, depending on the prefix typed.
                    var outcome = await ProbeRunner.RunAsync(target, timeout, ping, buffer, token);

                    // A reply that lands after Stop must not touch the display.
                    if (token.IsCancellationRequested) break;

                    if (outcome.Error is not null) Debug.WriteLine($"Probe of {address} threw: {outcome.Error}");

                    // What actually answered beats what DNS listed first.
                    if (outcome.Ip is not null) session.SetIp(outcome.Ip);
                    UpdatePingUI(session, outcome.Rtt, outcome.Failure);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
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
            if (closing) return;
            session.Add(ping, failure);
            var why = session.LastFailure;
            var entry = log.Add(DateTimeOffset.Now, session.Address, why?.Short ?? "OK", ping >= 0 ? ping : null, why?.Detail ?? "");
            autoLog?.Add(entry);
            var change = session.Monitor.Update(ping);
            incidents.Observe(DateTimeOffset.Now, session.Address, ping, session.LastFailure, change,
                session.Monitor.WindowLossPercent, session.Monitor.WindowAvgMs);
            if (change != HostChange.None) UpdateIncidentButton();
            // The outage the host just came back from, already closed by Observe above.
            TimeSpan? outage = change == HostChange.Up
                ? incidents.Incidents.LastOrDefault(i => i.Host == session.Address && i.Kind == IncidentKind.Outage)?.Duration(DateTimeOffset.Now)
                : null;
            Alert(session, change, outage);

            // Where do the answers stop? Trace the route the moment an outage is declared, and once
            // while the host is healthy, so the two can be compared.
            var token = cts?.Token ?? CancellationToken.None;
            if (change == HostChange.Down)
                StartPathCapture(session, incidents.Incidents.LastOrDefault(i => i.Host == session.Address && i.Kind == IncidentKind.Outage && i.Ongoing), token);
            else if (ping >= 0 && !session.BaselineRequested)
                StartPathCapture(session, null, token);

            foreach (ListViewItem item in lstHosts.Items)
                if (item.Tag == session) RenderRow(item);

            // The comparison graph and the diagnosis read every host, not just the selected one.
            if (session == selected || chkCompare.Checked) RenderSelected();
            else RenderDiagnosis();
        }

        private static void RenderRow(ListViewItem item)
        {
            var s = (HostSession)item.Tag!;
            item.SubItems[1].Text = s.Last is null ? "-" : s.LastFailure?.Short ?? s.Last + " ms";
            item.SubItems[2].Text = s.Stats.Avg is null ? "-" : s.Stats.Avg.Value.ToString("0.#", CultureInfo.CurrentCulture);
            item.SubItems[3].Text = s.Stats.Sent == 0 ? "-" : s.Stats.LossPercent.ToString("0.#", CultureInfo.CurrentCulture) + "%";
        }

        // A report for someone who does not have PingTool: one self-contained HTML file.
        private void btnReport_Click(object? sender, EventArgs e)
        {
            if (sessions.All(s => s.Last is null))
            {
                MessageBox.Show("Nothing to report yet: start pinging first.", "PingTool");
                return;
            }

            var now = DateTimeOffset.Now;
            var data = new ReportData(now, runStart, Environment.MachineName,
                AppVersion.Display,
                (int)numInterval.Value, (int)numTimeout.Value, (int)numSize.Value,
                (int)numSlow.Value, (int)numLoss.Value,
                sessions.Select(s => new HostReport(s.Address, s.IpText, s.Monitor.State, s.Stats.Sent, s.Stats.Lost,
                    s.Stats.LossPercent, s.Stats.Min, s.Stats.Avg, s.Stats.Max, s.Stats.Jitter, s.History.ToArray())).ToList(),
                Diagnosis.For(sessions.Select(s => s.ToTarget()).ToList()),
                incidents.Summary(now), incidents.Incidents.ToList());

            using var dialog = new SaveFileDialog
            {
                Filter = "HTML report (*.html)|*.html",
                FileName = $"pingtool-report-{DateTime.Now:yyyyMMdd-HHmmss}.html",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                File.WriteAllText(dialog.FileName, ReportBuilder.Build(data), new System.Text.UTF8Encoding(false));
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

        // outage = the incident to attach the route to; null = the healthy baseline of the host.
        private void StartPathCapture(HostSession session, Incident? outage, CancellationToken token)
        {
            if (session.Ip is not { } ip) return;   // no address yet: try again at the next reply
            if (outage is null) session.BaselineRequested = true;
            pathCaptures.Add(CapturePathAsync(session, ip, outage, token));
        }

        private static async Task CapturePathAsync(HostSession session, IPAddress ip, Incident? outage, CancellationToken token)
        {
            try
            {
                var path = await TraceRunner.RunAsync(session.Address, ip, PingHopProbe.Create(1000), DateTimeOffset.Now, token: token);
                if (outage is null)
                {
                    session.BaselinePath = path;
                }
                else
                {
                    outage.Path = path;
                    outage.PathNotes.AddRange(PathCapture.Compare(session.BaselinePath, path));
                }
            }
            catch (OperationCanceledException)
            {
                // Stop or close: the trace is simply abandoned.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Path capture for {session.Address} failed: {ex}");
            }
        }

        private void TraceSelected()
        {
            if (selected is null) return;
            if (selected.Ip is not { } ip)
            {
                MessageBox.Show("No address yet for " + selected.Address + ": start pinging first, so that it is resolved.", "PingTool");
                return;
            }

            using var dialog = new TraceForm(selected, ip);
            dialog.ShowDialog(this);
        }

        // The whole session of a host, not just the last 180 pings of the live graph.
        private void btnTimeline_Click(object? sender, EventArgs e)
        {
            var hosts = sessions.Select(s => s.Address).Where(a => log.Entries.Any(x => x.Host == a)).ToList();
            if (hosts.Count == 0)
            {
                MessageBox.Show("Nothing to show yet: start pinging first.", "PingTool");
                return;
            }

            using var dialog = new TimelineForm(log.Entries, incidents.Incidents.ToList(), hosts, selected?.Address, log.DroppedNote);
            dialog.ShowDialog(this);
        }

        private void UpdateIncidentButton() =>
            btnIncidents.Text = "Incidents (" + incidents.Incidents.Count.ToString(CultureInfo.CurrentCulture) + ")";

        private void btnIncidents_Click(object? sender, EventArgs e)
        {
            var now = DateTimeOffset.Now;
            using var dialog = new IncidentsForm(incidents.Incidents, incidents.Summary(now), now);
            dialog.ShowDialog(this);
        }

        private void chkCompare_CheckedChanged(object? sender, EventArgs e) => RenderSelected();

        // The sentence that compares the targets: where the fault most likely is.
        private void RenderDiagnosis()
        {
            lblDiagnosis.Text = Diagnosis.For(sessions.Select(s => s.ToTarget()).ToList()) ?? "";
            lblDiagnosis.ForeColor = sessions.Any(s => s.Monitor.State != HostState.Up) ? Color.Tomato : Color.Silver;
        }

        // Big value, stats and graph all follow the host selected in the list.
        private void RenderSelected()
        {
            if (closing) return;
            Text = AppVersion.Title(selected?.Address);
            if (chkCompare.Checked)
                graphLatency.ShowAll(sessions.Select((s, i) => new GraphSeries(s.Address, HostPalette.ColorFor(i), s.History)).ToList());
            else
                graphLatency.Show(selected?.History);
            RenderDiagnosis();
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

        private void Alert(HostSession session, HostChange change, TimeSpan? outage)
        {
            if (closing || change == HostChange.None || !chkAlert.Checked) return;

            var monitor = session.Monitor;
            string text = AlertMessage.For(session.Address, change, monitor.WindowLossPercent, monitor.WindowAvgMs, outage);
            var (sound, icon) = change switch
            {
                HostChange.Down => (System.Media.SystemSounds.Hand, ToolTipIcon.Error),
                HostChange.Degraded => (System.Media.SystemSounds.Exclamation, ToolTipIcon.Warning),
                _ => (System.Media.SystemSounds.Asterisk, ToolTipIcon.Info),
            };

            sound.Play();
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(5000, "PingTool", text, icon);
        }

        private void UpdateStatsUI()
        {
            string ms(double? v) => v is null ? "-" : v.Value.ToString("0.#", CultureInfo.CurrentCulture);
            var stats = selected?.Stats ?? new SessionStats();
            lblStats.Text =
                $"Min {ms(stats.Min)} / Avg {ms(stats.Avg)} / Max {ms(stats.Max)} ms\n" +
                $"Jitter {ms(stats.Jitter)} ms | Loss {stats.LossPercent:0.#}% ({stats.Lost}/{stats.Sent})\n" +
                (selected?.IpText ?? "-") + "\n" +
                RecentStats.From(selected?.History ?? new Queue<long>()).Describe();
        }
    }

    // Live latency trace of one host's history (HostSession.HistorySize pings,
    // so three minutes at one per second). A timeout is a red tick on the
    // baseline, not a zero, so a lost packet cannot be mistaken for a fast one.
    internal sealed class LatencyGraph : Control
    {
        private const int MaxSamples = HostSession.HistorySize;
        private const int MaxLegend = 5;
        private IReadOnlyList<GraphSeries> series = Array.Empty<GraphSeries>();
        private bool compare;

        public LatencyGraph()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(40, 40, 40);
        }

        // One host. Draws the host's own queue: it is repainted, never copied.
        public void Show(IReadOnlyCollection<long>? history)
        {
            compare = false;
            series = history is null ? Array.Empty<GraphSeries>() : new[] { new GraphSeries("", Color.LimeGreen, history) };
            Invalidate();
        }

        // Every host on one shared scale: all queues end at "now", so the columns line up in time.
        public void ShowAll(IReadOnlyList<GraphSeries> all)
        {
            compare = true;
            series = all;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            long top = Math.Max(50, series.Count == 0 ? 0 : series.Max(s => s.Samples.Count == 0 ? 0 : s.Samples.Max()));
            using var grey = new SolidBrush(Color.Silver);
            g.DrawString(top + " ms", Font, grey, 2, 0);
            g.DrawString("0", Font, grey, 2, Height - Font.Height);

            foreach (var s in series) DrawSeries(g, s, top);
            if (compare) DrawLegend(g);
            else if (series.Count == 1) DrawP95(g, series[0].Samples, top);
        }

        private void DrawSeries(Graphics g, GraphSeries s, long top)
        {
            // Where everything goes is computed by GraphLayout (and tested there); this only paints.
            var shapes = GraphLayout.Build(s.Samples, Width, Height, top, MaxSamples);

            using var line = new Pen(s.Color, 1.5f);
            // Alone, a loss is red; compared, it keeps its host's colour so you can tell whose it is.
            using var lost = new Pen(compare ? s.Color : Color.Red, 2f);
            using var dot = new SolidBrush(s.Color);

            foreach (var (from, to) in shapes.Lines) g.DrawLine(line, from.X, from.Y, to.X, to.Y);
            // A reply with no neighbour to be joined to (the first one, or one between two losses)
            // is a dot: as a line it would have no length and the reply would not show at all.
            foreach (var d in shapes.Dots) g.FillEllipse(dot, d.X - 2f, d.Y - 2f, 4f, 4f);
            foreach (float x in shapes.LossXs) g.DrawLine(lost, x, Height - 1, x, Height - 8);
        }

        // A dotted line at the recent p95: "95 % of the last pings were at or below this".
        private void DrawP95(Graphics g, IReadOnlyCollection<long> samples, long top)
        {
            if (RecentStats.From(samples).P95 is not double p95) return;

            float y = Height - 1 - (Height - 1f) * (float)p95 / top;
            using var pen = new Pen(Color.Silver) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawLine(pen, 0, y, Width, y);

            string label = "p95 " + p95.ToString("0", CultureInfo.CurrentCulture);
            using var brush = new SolidBrush(Color.Silver);
            g.DrawString(label, Font, brush, Width - g.MeasureString(label, Font).Width - 2, Math.Max(0, y - Font.Height));
        }

        private void DrawLegend(Graphics g)
        {
            float y = 0;
            foreach (var s in series.Take(MaxLegend))
            {
                string name = s.Name.Length > 14 ? s.Name[..13] + "…" : s.Name;
                using var brush = new SolidBrush(s.Color);
                g.DrawString(name, Font, brush, Width - g.MeasureString(name, Font).Width - 2, y);
                y += Font.Height;
            }

            if (series.Count > MaxLegend)
            {
                using var grey = new SolidBrush(Color.Silver);
                string more = "+" + (series.Count - MaxLegend) + " more";
                g.DrawString(more, Font, grey, Width - g.MeasureString(more, Font).Width - 2, y);
            }
        }
    }

    internal sealed record GraphSeries(string Name, Color Color, IReadOnlyCollection<long> Samples);
}