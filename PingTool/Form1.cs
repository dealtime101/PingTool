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
        private RunSettings? runSettings;   // the probe settings of the run in the list, fixed at Start
        private CancellationTokenSource? cts;
        private readonly List<HostSession> sessions = new();
        private HostSession? selected;
        private readonly PingLog log = new();
        // Every ping on disk as it happens (when "Save log to disk" is ticked); written every few seconds.
        private AutoLog? autoLog;
        private readonly List<AutoLog> retiredLogs = new();   // logs of earlier runs that still hold pings to write
        private bool autoLogWarned;
        private int droppedReported;
        private bool dropWarned;
        private readonly System.Windows.Forms.Timer autoLogTimer = new() { Interval = 5000 };
        // Takes the notification icon away again once a balloon has been shown (see ShowBalloon).
        private readonly System.Windows.Forms.Timer trayIconTimer = new() { Interval = 10_000 };
        // Alerts also go to the webhooks of settings.json, in the background (see WebhookSender); null when none.
        // Changes of this PC's own network during the run (see NetworkWatch): cyan lines on the timeline, a table in the report.
        private readonly List<NetworkEvent> networkEvents = new();
        private List<NicState> lastNetwork = new();
        private readonly System.Windows.Forms.Timer networkTimer = new() { Interval = 1500 };
        private WebhookSender? webhooks;
        private readonly HashSet<string> webhookWarned = new();
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
        private readonly NotifyIcon notifyIcon = new() { Icon = AppIcon.Load(SystemInformation.SmallIconSize), Text = "PingTool" };

        // How the program was started (command line); None when opened normally.
        private readonly StartupOptions startup;

        public MainForm() : this(null)
        {
        }

        internal MainForm(StartupOptions? startup)
        {
            this.startup = startup ?? StartupOptions.None;
            InitializeComponent();
            Icon = AppIcon.Load();   // the title bar, Alt+Tab and the taskbar
            Text = AppVersion.Title(null);
            toolTip.SetToolTip(cmbAddress, ProbeTarget.Help);
            // The label is short (the column is narrow): the unit and what happens are said here, and in the accessible name.
            const string downHelp = "Number of consecutive failed pings after which the host is reported down.";
            toolTip.SetToolTip(lblDownAfter, downHelp);
            toolTip.SetToolTip(numDownAfter, downHelp);
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
                chkAlert, chkSaveLog, lstHosts, btnAddHost, btnRemoveHost, btnExport, btnIncidents, btnReport, btnTimeline, btnOpenLog, btnImportProfiles, btnExportProfiles, cboProfile, btnSaveProfile, btnDeleteProfile, lblDiagnosis, chkCompare,
            };
            notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
            ApplyAnchors();
            ApplySettings();
            if (chkCompact.Checked)
            {
                // Started compact: the full size to come back to is the saved one, not the designer's.
                var saved = WindowSizing.Restore(settings.WindowWidth, settings.WindowHeight, FullSize) ?? FullSize;
                normalClientSize = DpiScale.Scale(saved, DeviceDpi);
            }
            else ApplyCompact(false);   // the checkbox did not change, so nothing applied the resizable full window yet
            autoLogTimer.Tick += (_, _) => FlushAutoLog();

            // The system says "something changed" several times for one real change (address, then availability, then
            // the gateway): a short wait lets them settle, and only then the cards are compared with what they were.
            networkTimer.Tick += (_, _) => ReadNetworkChange();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            if (settings.Webhooks.Count > 0)
            {
                webhooks = new WebhookSender(
                    settings.Webhooks.Select(w => WebhookPayload.TryParseUrl(w, out var url) ? url : null).OfType<Uri>(),
                    AppVersion.Number);
                // The sender reports from a background thread: the window is touched on its own thread only.
                webhooks.Failed += (label, reason) =>
                {
                    if (!closing && IsHandleCreated) BeginInvoke(() => WebhookFailed(label, reason));
                };
            }

            // A settings file that could not be read is said once the window is up, with where it went.
            if (settings.LoadProblem is string loadProblem)
                Shown += (_, _) => MessageBox.Show(loadProblem, "PingTool");

            // Host column = what the other columns and the scroll bar leave (see ColumnFit).
            lstHosts.SizeChanged += (_, _) => FitHostColumns();
            Shown += (_, _) => FitHostColumns();

            // Enter in the address box STARTS the monitoring, and only that. There is no default button any
            // more: Start and Stop are one button, so Enter used to flip the state - and could stop a run
            // from anywhere in the window. (The box is locked while running, so it never stops anything.)
            cmbAddress.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                if (isRunning) return;

                string typed = cmbAddress.Text.Trim();
                bool inList = sessions.Any(s => TargetKey.Same(s.Address, typed));
                switch (AddressBoxEnter.Decide(typed, sessions.Count == 0, inList))
                {
                    case EnterOutcome.Refuse:
                        IsValidTarget(typed);   // says why; nothing starts on a list that leaves out what was typed
                        return;
                    case EnterOutcome.AddAndStart:
                        AddHost(typed);
                        break;
                }

                btnStartStop_Click(this, EventArgs.Empty);
            };
            trayIconTimer.Tick += (_, _) => HideTrayIconIfWindowShown();

            // --minimized hides the window in the notification area (alerts still show as balloons);
            // --start then begins the monitoring: no click needed.
            // Started minimized, the window is never shown at all (see SetVisibleCore): hiding it from Shown made it flash on
            // screen first. Started normally, --start begins when the window is up.
            // this.startup, not the parameter: the parameter is null for the parameterless constructor (the field is StartupOptions.None then).
            startHidden = this.startup.Minimized;
            Shown += (_, _) =>
            {
                if (this.startup.Start && !isRunning) btnStartStop_Click(this, EventArgs.Empty);
            };

            // Right-click (or the menu key) on a host: where does the path to it stop?
            var hostMenu = new ContextMenuStrip();
            hostMenu.Items.Add("Trace route to this host").Click += (_, _) => TraceSelected();
            hostMenu.Items.Add("Name and limits of this host...").Click += (_, _) => EditSelectedTarget();
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
                trayIconTimer.Stop();
                networkTimer.Stop();
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                webhooks?.Dispose();
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

            // The sizes are in pixels at 100 %: scaled to this screen, like the controls inside.
            if (compact)
            {
                // The compact window has a size of its own and cannot be dragged: remember the one the user had.
                if (FormBorderStyle == FormBorderStyle.Sizable && WindowState == FormWindowState.Normal) normalClientSize = ClientSize;
                WindowState = FormWindowState.Normal;
                MinimumSize = Size.Empty;
                FormBorderStyle = FormBorderStyle.FixedSingle;
                MaximizeBox = false;
                ClientSize = DpiScale.Scale(CompactSize, DeviceDpi);
                return;
            }

            // Back to the full window: resizable, never smaller than what its controls need, at the size it had before
            // compact mode, or the size saved at the last close, or the default.
            var minimum = DpiScale.Scale(FullSize, DeviceDpi);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = SizeFromClientSize(minimum);
            var saved = WindowSizing.Restore(settings.WindowWidth, settings.WindowHeight, FullSize);
            ClientSize = normalClientSize ?? (saved is Size s ? DpiScale.Scale(s, DeviceDpi) : minimum);
            normalClientSize = null;
        }

        // The size the full window had when it went compact (null otherwise).
        private Size? normalClientSize;

        // Where the window can grow: the extra width goes to the right column (host list, profile box, buttons), the extra height to the
        // host list and the graph; what sits at the bottom stays at the bottom. Without this a bigger window is just empty space.
        private void ApplyAnchors()
        {
            const AnchorStyles T = AnchorStyles.Top, B = AnchorStyles.Bottom, L = AnchorStyles.Left, R = AnchorStyles.Right;
            (Control Control, AnchorStyles Anchor)[] rules =
            {
                (graphLatency, T | B | L),
                (lblInterval, B | L), (numInterval, B | L), (lblTimeout, B | L), (numTimeout, B | L), (lblSize, B | L), (numSize, B | L),
                (lblSlow, B | L), (numSlow, B | L), (lblLoss, B | L), (numLoss, B | L), (lblDownAfter, B | L), (numDownAfter, B | L),
                (chkAlert, B | L), (chkSaveLog, B | L),
                (cboProfile, T | L | R), (btnSaveProfile, T | R), (btnDeleteProfile, T | R),
                (lstHosts, T | B | L | R), (lblDiagnosis, B | L | R),
                (btnAddHost, B | L), (btnRemoveHost, B | R),
                (btnExport, B | L), (btnIncidents, B | R), (chkCompare, B | L),
                (btnReport, B | L | R), (btnTimeline, B | L), (btnOpenLog, B | R),
                (btnImportProfiles, B | L), (btnExportProfiles, B | R),
            };
            foreach (var (control, anchor) in rules) control.Anchor = anchor;
        }

        private void FitHostColumns() =>
            colHost.Width = ColumnFit.HostWidth(lstHosts.ClientSize.Width, SystemInformation.VerticalScrollBarWidth,
                colLast.Width + colAvg.Width + colLoss.Width, DeviceDpi);

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (chkCompact.Checked && WindowState == FormWindowState.Minimized)
            {
                Hide();
                notifyIcon.Visible = true;
            }
        }

        // True until the first time the window is asked to show itself, when it was started with --minimized.
        private bool startHidden;

        // What this launch has to say that a hidden window cannot (--minimized): shown as a balloon by SetVisibleCore.
        private string? launchWarning;

        // Application.Run shows the main window once: with --minimized that one request is turned down, so the window never
        // appears (not even for a frame) and the notification icon is the only trace; double-clicking it shows the window for real.
        // The handle is created anyway, so that the message loop can run what the window would have started from Shown.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                value = false;
                if (!IsHandleCreated) CreateHandle();
                notifyIcon.Visible = true;
                if (launchWarning is string warning) BeginInvoke(() => ShowBalloon(warning, ToolTipIcon.Warning));
                if (startup.Start) BeginInvoke(() => { if (!isRunning) btnStartStop_Click(this, EventArgs.Empty); });
            }

            base.SetVisibleCore(value);
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
                .Distinct(TargetKey.Comparer).ToArray<object>());
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
            IEnumerable<string> hostsOfThisLaunch = settings.Hosts;
            bool gatewayMissing = false;
            if (startup.OverridesHosts)
            {
                var discovered = new List<string>();
                if (startup.Diagnose)
                {
                    discovered = DiagnosticTargets.Discover(out bool gatewayFound);
                    gatewayMissing = !gatewayFound;
                }

                hostsOfThisLaunch = discovered.Concat(startup.Hosts);
            }

            // Started minimized, no window is shown (see SetVisibleCore): the balloon is what can say it then.
            if (gatewayMissing)
            {
                launchWarning = DiagnosticTargets.NoGatewayMessage;
                Shown += (_, _) => MessageBox.Show(DiagnosticTargets.NoGatewayMessage, "PingTool");
            }
            // Same rule as typing an address: what the address box would refuse is left out, and said once the window is up.
            var validHosts = ProbeTarget.KeepValid(hostsOfThisLaunch.Distinct(TargetKey.Comparer), out int refused);
            foreach (var host in validHosts) AddHost(host);
            if (refused > 0)
                Shown += (_, _) => MessageBox.Show($"{refused} address(es) from the settings file or the command line are not valid targets and were left out.", "PingTool");
        }

        private void SaveSettings()
        {
            settings.Address = cmbAddress.Text.Trim();
            // What the command line imposed for this launch is not what the user chose: keep the saved values.
            if (!startup.OverridesHosts) settings.Hosts = sessions.Select(s => s.Address).ToList();
            if (startup.IntervalMs is null) settings.IntervalMs = (int)numInterval.Value;
            settings.TimeoutMs = (int)numTimeout.Value;
            settings.PacketSize = (int)numSize.Value;
            settings.DegradedLatencyMs = (int)numSlow.Value;
            settings.DegradedLossPercent = (int)numLoss.Value;
            settings.DownAfter = (int)numDownAfter.Value;
            settings.Alert = chkAlert.Checked;
            settings.SaveLog = chkSaveLog.Checked;
            settings.Compact = chkCompact.Checked;
            // The size of the full window (what it was before compact mode, when compact now); a maximized window is not a size to keep.
            var full = normalClientSize ?? (WindowState == FormWindowState.Normal ? ClientSize : (Size?)null);
            if (full is Size f)
            {
                var b = WindowSizing.ToBase(f, DeviceDpi);
                settings.WindowWidth = b.Width;
                settings.WindowHeight = b.Height;
            }

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
            var existing = sessions.Find(s => TargetKey.Same(s.Address, address));
            if (existing != null) return existing;

            settings.TargetOptions.TryGetValue(address, out var options);
            var (slow, loss, down) = TargetOptions.Effective(options, (int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
            var session = new HostSession(address, slow, loss, down) { Options = options, RunLimits = options is null ? null : TargetOptions.DescribeLimits(options) };
            sessions.Add(session);
            var item = new ListViewItem(new[] { session.DisplayName, "-", "-", "-" }) { Tag = session };
            lstHosts.Items.Add(item);
            RenderRow(item);   // the hover text is there from the start, not after the first ping
            item.Selected = true;
            return session;
        }

        private void RefreshProfileList()
        {
            string typed = cboProfile.Text;
            cboProfile.Items.Clear();
            // The built-in diagnosis comes first: it is not saved, its targets are read from the network cards when it is picked.
            cboProfile.Items.Add(DiagnosticTargets.ProfileName);
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
                // The names and own limits of this profile's hosts go with it.
                TargetOptions = hosts.Where(settings.TargetOptions.ContainsKey)
                    .ToDictionary(h => h, h => settings.TargetOptions[h], TargetKey.Comparer),
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

        // Targets = default gateway, DNS servers and Internet references (see DiagnosticTargets); every other setting stays as it is.
        private void ApplyDiagnosis()
        {
            if (isRunning) return;

            var targets = DiagnosticTargets.Discover(out bool gatewayFound);
            ApplyProfile(new Profile
            {
                Name = DiagnosticTargets.ProfileName,
                Hosts = targets,
                IntervalMs = (int)numInterval.Value,
                TimeoutMs = (int)numTimeout.Value,
                PacketSize = (int)numSize.Value,
                Alert = chkAlert.Checked,
                DegradedLatencyMs = (int)numSlow.Value,
                DegradedLossPercent = (int)numLoss.Value,
                DownAfter = (int)numDownAfter.Value,
            });

            if (!gatewayFound)
                MessageBox.Show(DiagnosticTargets.NoGatewayMessage, "PingTool");
        }

        // Picking a profile replaces the target list and clears the figures. Nothing to ask when nothing would be lost; else the
        // user is told what, and "No" puts the box back on the profile that is really applied.
        private bool ConfirmReplace()
        {
            // The built-in diagnosis is not saved (its targets are read from the network again), so it never counts as lost work.
            bool diagnosisActive = string.Equals(settings.ActiveProfile, DiagnosticTargets.ProfileName, StringComparison.Ordinal);
            bool unsavedList = !diagnosisActive
                && !ProfileBook.SameTargets(sessions.Select(s => s.Address), ProfileBook.Find(settings.Profiles, settings.ActiveProfile));
            bool figures = log.Count > 0;
            if (!unsavedList && !figures) return true;

            string lost = unsavedList && figures ? "the current target list (not saved as a profile) and the figures of this run"
                : unsavedList ? "the current target list, which is not saved as a profile"
                : "the figures of this run";
            if (MessageBox.Show($"Applying this profile replaces {lost}. Continue?", "PingTool",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) return true;

            cboProfile.Text = settings.ActiveProfile;
            return false;
        }

        private void cboProfile_SelectionChangeCommitted(object? sender, EventArgs e)
        {
            if (!isRunning && !ConfirmReplace()) return;
            if (string.Equals(cboProfile.SelectedItem?.ToString(), DiagnosticTargets.ProfileName, StringComparison.Ordinal))
            {
                ApplyDiagnosis();
                return;
            }

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

            // The profile's names and limits for its hosts replace the ones of the same addresses (AddHost reads them).
            foreach (var (address, options) in profile.TargetOptions) settings.TargetOptions[address] = options;

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
                        + $".\n\nTick \"{chkSaveLog.Text}\" before Start to keep everything.", "PingTool");   // the label as it is on screen
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
                else if (chkCompact.Checked && !ConfirmCompactTarget()) return;

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
                    var (slow, loss, down) = TargetOptions.Effective(s.Options, (int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
                    s.ApplyThresholds(slow, loss, down);
                    s.RunLimits = s.Options is null ? null : TargetOptions.DescribeLimits(s.Options);
                    s.Reset();
                }
                runStart = DateTimeOffset.Now;
                runSettings = new RunSettings((int)numInterval.Value, (int)numTimeout.Value, (int)numSize.Value, (int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
                log.Clear();
                StartAutoLog();
                webhookWarned.Clear();
                noticed.Clear();
                networkEvents.Clear();
                lastNetwork = NetworkWatch.Snapshot();
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

        // Compact mode hides the list, so the address box looks like THE target. With a list already there only the list is pinged:
        // a new address typed in the box would be ignored without a word. Same rule as Enter in the box (AddressBoxEnter), but asked.
        // False = do not start.
        private bool ConfirmCompactTarget()
        {
            string typed = cmbAddress.Text.Trim();
            bool inList = sessions.Any(s => TargetKey.Same(s.Address, typed));
            switch (AddressBoxEnter.Decide(typed, listIsEmpty: false, inList))
            {
                case EnterOutcome.Refuse:
                    IsValidTarget(typed);   // says why; nothing starts on a list that leaves out what was typed
                    return false;
                case EnterOutcome.AddAndStart:
                    string watched = string.Join(", ", sessions.Take(3).Select(s => s.DisplayName)) + (sessions.Count > 3 ? $" and {sessions.Count - 3} more" : "");
                    var answer = MessageBox.Show(this,
                        $"The address box says \"{typed}\", which is not in the list that will be monitored ({watched}).\r\n\r\n"
                        + "Yes: add it to the list and start\r\nNo: start the list as it is\r\nCancel: do not start",
                        "PingTool", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return false;
                    if (answer == DialogResult.Yes) AddHost(typed);
                    return true;
                default:
                    return true;
            }
        }

        // A new run: whatever the previous run could not write yet gets a try now, and what still fails is NOT dropped (the balloon said
        // it would be retried): the same log goes on when the folder is the same, otherwise the old one is retired and still retried.
        private void StartAutoLog()
        {
            FlushAutoLog();
            string folder = settings.LogFolder.Length > 0 ? settings.LogFolder : AutoLog.DefaultFolder;
            var previous = autoLog;
            autoLog = AutoLog.Next(previous, chkSaveLog.Checked ? folder : null, retiredLogs);
            if (!ReferenceEquals(autoLog, previous))
            {
                autoLogWarned = false;
                droppedReported = 0;
                dropWarned = false;
            }

            // The timer keeps running after Stop: pings still in flight are written by the next tick.
            autoLogTimer.Enabled = autoLog is not null || retiredLogs.Count > 0;
        }

        // A file that cannot be written (open in a spreadsheet, disk full) never stops the monitoring:
        // one balloon says so, the entries wait and go out at the next tick that works.
        private void FlushAutoLog()
        {
            AutoLog.FlushRetired(retiredLogs);
            if (autoLog is null)
            {
                if (retiredLogs.Count == 0) autoLogTimer.Stop();
                return;
            }

            bool written = autoLog.Flush();
            if (written) autoLogWarned = false;
            else if (!autoLogWarned && !closing)
            {
                autoLogWarned = true;
                ShowBalloon("Log file not written, will retry: " + autoLog.LastError + (autoLog.DropNote is { } lost ? " " + lost : ""), ToolTipIcon.Warning);
            }

            // Pings that were thrown away because the folder stayed unwritable too long, never silently: said when it first happens,
            // and again with the final figures when the folder works again (not at every tick of a long failure).
            if (autoLog.Dropped > droppedReported && !closing && (written || !dropWarned))
            {
                droppedReported = autoLog.Dropped;
                dropWarned = !written;
                ShowBalloon(autoLog.DropNote!, ToolTipIcon.Warning);
            }

            if (written) dropWarned = false;
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

            using Ping ping = new Ping();   // Stop reaches a ping in flight through the token given to ProbeRunner

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
                var probeClock = Stopwatch.StartNew();
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
                    RaiseNotice(session, outcome.Warning);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    // Probes start one interval apart, whatever the answer took (see Cadence).
                    await Task.Delay(Cadence.WaitMs(interval, probeClock.ElapsedMilliseconds), token);
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
            session.Add(ping, failure, DateTimeOffset.Now);
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
            item.SubItems[0].Text = s.DisplayName;
            // A long name is cut by the narrow column: the whole of it (and the address behind a name) shows on hover.
            item.ToolTipText = s.Options?.Label is null ? s.Address : s.Options.Label + " (" + s.Address + ")";
            item.SubItems[1].Text = s.Last is null ? "-" : s.LastFailure?.Short ?? s.Last + " ms";
            item.SubItems[2].Text = s.Stats.Avg is null ? "-" : s.Stats.Avg.Value.ToString("0.#", CultureInfo.CurrentCulture);
            item.SubItems[3].Text = s.Stats.LossPercent is double loss ? loss.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "-";
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
            // What the measures were taken with, not what the boxes say now (they are editable again after Stop).
            var run = runSettings ?? new RunSettings((int)numInterval.Value, (int)numTimeout.Value, (int)numSize.Value, (int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
            var data = new ReportData(now, runStart, Environment.MachineName,
                AppVersion.Display,
                run.IntervalMs, run.TimeoutMs, run.PacketSize,
                run.DegradedLatencyMs, run.DegradedLossPercent,
                sessions.Select(s => new HostReport(s.Address, s.IpText, s.Monitor.State, s.Stats.Sent, s.Stats.Lost,
                    s.Stats.LossPercent, s.Stats.Min, s.Stats.Avg, s.Stats.Max, s.Stats.Jitter, s.History.ToArray(), s.Stats.Hours,
                    s.Options?.Label, s.RunLimits, s.Notice)).ToList(),
                Diagnosis.For(sessions.Select(s => s.ToTarget()).ToList()),
                incidents.Summary(now), incidents.Incidents.ToList(), NetworkEvents: networkEvents.ToList(), DownAfter: run.DownAfter);

            using var dialog = new SaveFileDialog
            {
                Filter = "HTML report (*.html)|*.html",
                FileName = $"pingtool-report-{DateTime.Now:yyyyMMdd-HHmmss}.html",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                File.WriteAllText(dialog.FileName, ReportBuilder.Build(data), new System.Text.UTF8Encoding(false));
                FileOpener.OfferToOpen(this, "The report", dialog.FileName);   // where it is, and the offer to open it
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

        // A readable name and limits of its own for the selected host. Kept in settings.json by address (and in the profiles
        // saved afterwards); the name shows at once, the limits from the next Start.
        private void EditSelectedTarget()
        {
            if (selected is not { } session) return;

            using var dialog = new TargetOptionsForm(session.Address, session.Options,
                (int)numSlow.Value, (int)numLoss.Value, (int)numDownAfter.Value);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            session.Options = dialog.Result;
            if (dialog.Result is null) settings.TargetOptions.Remove(session.Address);
            else settings.TargetOptions[session.Address] = dialog.Result;

            foreach (ListViewItem item in lstHosts.Items) if (ReferenceEquals(item.Tag, session)) RenderRow(item);
            RenderSelected();
            SaveSettings();
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
            var snapshot = log.Entries;   // one copy of the log for this window
            var hosts = sessions.Select(s => s.Address).Where(a => snapshot.Any(x => x.Host == a)).ToList();
            if (hosts.Count == 0)
            {
                MessageBox.Show("Nothing to show yet: start pinging first.", "PingTool");
                return;
            }

            using var dialog = new TimelineForm(snapshot, incidents.Incidents.ToList(), hosts, selected?.Address, log.DroppedNote, networkEvents.ToList());
            dialog.ShowDialog(this);
        }

        private const long MaxLogFileBytes = 200L * 1024 * 1024;

        // Looks again at a recorded night: one or several log files (the CSV of the export, or the daily files of
        // "Save the log to disk"). Reading and replaying happen off the window's thread; the result is read-only.
        private async void btnOpenLog_Click(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "PingTool log (*.csv)|*.csv|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = Directory.Exists(settings.LogFolder.Length > 0 ? settings.LogFolder : AutoLog.DefaultFolder)
                    ? (settings.LogFolder.Length > 0 ? settings.LogFolder : AutoLog.DefaultFolder) : "",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            string[] paths = dialog.FileNames;
            btnOpenLog.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                var (replay, entries, error) = await Task.Run(() => LoadLogs(paths));
                if (replay is null || entries is null)
                {
                    MessageBox.Show(error, "PingTool");
                    return;
                }

                using var viewer = new LogViewerForm(replay, entries,
                    paths.Length == 1 ? Path.GetFileName(paths[0]) : paths.Length.ToString(CultureInfo.CurrentCulture) + " log files");
                viewer.ShowDialog(this);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnOpenLog.Enabled = true;
            }
        }

        private static (ReplayResult? Replay, List<LogEntry>? Entries, string? Error) LoadLogs(string[] paths)
        {
            var all = new List<LogEntry>();
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                string text;
                try
                {
                    if (new FileInfo(path).Length > MaxLogFileBytes) return (null, null, name + " is too large to be a PingTool log.");
                    text = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (null, null, "Could not read " + name + ": " + ex.Message);
                }

                if (!PingLogReader.TryParse(text, out var part, out string error)) return (null, null, name + ": " + error);
                all.AddRange(part);
                if (all.Count > PingLogReader.MaxEntries) return (null, null, "These files hold too many pings to open together.");
            }

            return (LogReplay.Run(all), all, null);
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
            lblDiagnosis.ForeColor = sessions.Any(s => s.Monitor.State != HostState.Up) ? UiColors.SmallTextRed : Color.Silver;
        }

        // Big value, stats and graph all follow the host selected in the list.
        private void RenderSelected()
        {
            if (closing) return;
            Text = AppVersion.Title(selected?.DisplayName);
            if (chkCompare.Checked)
                graphLatency.ShowAll(sessions.Select((s, i) => new GraphSeries(s.DisplayName, HostPalette.ColorFor(i), s.History)).ToList());
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
                if (!isRunning && hasRun) lblPingResult.ForeColor = Color.Silver;
            }

            // The full sentence is on hover (the label only has room for a word); empty when there is no value.
            toolTip.SetToolTip(lblPingResult, ResultHint.For(ping, selected?.LastFailure, !isRunning && hasRun));
        }

        private void ShowPing(long ping, PingFailure? failure)
        {
            if (ping >= 0)
            {
                lblPingResult.Text = ping + " ms";

                // Steps of the host's own "slow" limit (what the state and the report use), not fixed 50 and 100 ms.
                lblPingResult.ForeColor = HostMonitor.LatencyBand(ping, selected?.Monitor.LatencyMs ?? HostMonitor.DefaultLatencyMs) switch
                {
                    0 => Color.LimeGreen,
                    1 => Color.Orange,
                    _ => Color.Tomato,
                };
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
            string text = AlertMessage.For(session.DisplayName, change, monitor.WindowLossPercent, monitor.WindowAvgMs, outage);
            webhooks?.Send(new WebhookEvent(session.Address, change, text, outage, DateTimeOffset.Now));
            var (sound, icon) = change switch
            {
                HostChange.Down => (System.Media.SystemSounds.Hand, ToolTipIcon.Error),
                HostChange.Degraded => (System.Media.SystemSounds.Exclamation, ToolTipIcon.Warning),
                _ => (System.Media.SystemSounds.Asterisk, ToolTipIcon.Info),
            };

            sound.Play();
            ShowBalloon(text, icon);
        }

        // These two events come from a system thread: the window is only touched through BeginInvoke.
        private void OnNetworkAddressChanged(object? sender, EventArgs e) => NetworkSignal();
        private void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => NetworkSignal();

        private void NetworkSignal()
        {
            if (closing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(() =>
                {
                    networkTimer.Stop();
                    networkTimer.Start();
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The window is closing.
            }
        }

        private void ReadNetworkChange()
        {
            networkTimer.Stop();
            var now = NetworkWatch.Snapshot();
            string? text = NetworkWatch.Describe(lastNetwork, now);
            lastNetwork = now;
            if (text is not null && isRunning && !closing) networkEvents.Add(new NetworkEvent(DateTimeOffset.Now, text));
        }

        // What a successful probe still has to say (a certificate about to expire): kept on the host for the report, and told ONCE
        // per host and per text, which changes with the day count - so a reminder a day, not a balloon at every probe.
        private readonly HashSet<string> noticed = new();

        private void RaiseNotice(HostSession session, string? text)
        {
            session.Notice = text;
            if (text is null || closing || !chkAlert.Checked || !noticed.Add(session.Address + "|" + text)) return;

            webhooks?.Send(new WebhookEvent(session.Address, HostChange.Notice, text, null, DateTimeOffset.Now));   // the host field is the address, as in Alert
            ShowBalloon(text, ToolTipIcon.Warning);
        }

        // One balloon per webhook and per run, not one per lost alert: a receiver that is down would otherwise
        // fill the screen. The address itself is never shown (see Settings.Webhooks).
        private void WebhookFailed(string label, string reason)
        {
            if (closing || !webhookWarned.Add(label)) return;
            ShowBalloon($"Alert not delivered to the webhook {label}: {reason}", ToolTipIcon.Warning);
        }

        // The icon exists to carry the balloon. Once the balloon has had its time it goes away again,
        // unless the window is hidden in the notification area (then the icon is the way back in).
        // A timer rather than BalloonTipClosed: Windows 10/11 do not always raise that event, and a second
        // alert must not have its balloon cut by the end of the first one (each balloon restarts the timer).
        private void ShowBalloon(string text, ToolTipIcon icon)
        {
            notifyIcon.Visible = true;
            notifyIcon.ShowBalloonTip(5000, "PingTool", text, icon);
            trayIconTimer.Stop();
            trayIconTimer.Start();
        }

        private void HideTrayIconIfWindowShown()
        {
            trayIconTimer.Stop();
            if (Visible && !closing) notifyIcon.Visible = false;
        }

        private void UpdateStatsUI()
        {
            string ms(double? v) => v is null ? "-" : v.Value.ToString("0.#", CultureInfo.CurrentCulture);
            var stats = selected?.Stats ?? new SessionStats();
            lblStats.Text =
                $"Min {ms(stats.Min)} / Avg {ms(stats.Avg)} / Max {ms(stats.Max)} ms\n" +
                $"Jitter {ms(stats.Jitter)} ms | Loss {(stats.LossPercent is null ? "-" : ms(stats.LossPercent) + $"% ({stats.Lost}/{stats.Sent})")}\n" +
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
        private int legendOffset, legendFirst, legendRows;   // the page of the legend shown, as drawn last (for the click and the tooltip)
        private float legendLeft;
        private string legendTipText = "";
        private readonly ToolTip legendTip = new();
        private IReadOnlyList<GraphSeries> series = Array.Empty<GraphSeries>();
        private bool compare;
        private GraphPalette palette = GraphPalette.For(SystemInformation.HighContrast);
        private static readonly System.Drawing.Drawing2D.DashStyle[] LinePatterns =
        {
            System.Drawing.Drawing2D.DashStyle.Solid, System.Drawing.Drawing2D.DashStyle.Dash,
            System.Drawing.Drawing2D.DashStyle.Dot, System.Drawing.Drawing2D.DashStyle.DashDot,
        };

        // The user switched to (or away from) a high-contrast theme while the program runs.
        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            palette = GraphPalette.For(SystemInformation.HighContrast);
            BackColor = palette.Back;
            Invalidate();
        }

        public LatencyGraph()
        {
            DoubleBuffered = true;
            BackColor = palette.Back;
            AccessibleName = "Latency graph";
            AccessibleRole = AccessibleRole.Chart;
        }

        // What a screen reader gets: the picture says nothing, so the figures are read out from the data - computed when a
        // reader asks, not at every ping.
        private sealed class GraphAccessibleObject(LatencyGraph owner) : ControlAccessibleObject(owner)
        {
            public override string? Description => GraphSummary.Describe(owner.series);
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new GraphAccessibleObject(this);

        // One host. Draws the host's own queue: it is repainted, never copied.
        public void Show(IReadOnlyCollection<long>? history)
        {
            compare = false;
            series = history is null ? Array.Empty<GraphSeries>() : new[] { new GraphSeries("", Color.LimeGreen, history) };
            legendRows = 0;   // the legend as drawn last belongs to the old series: no tooltip from it until the repaint
            Invalidate();
        }

        // Every host on one shared scale: all queues end at "now", so the columns line up in time.
        public void ShowAll(IReadOnlyList<GraphSeries> all)
        {
            compare = true;
            series = all;
            legendRows = 0;   // see Show
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // The labels are text like the rest of the window: ClearType, as the labels of the controls, not the grey smoothing GDI+ picks.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            long top = Math.Max(50, series.Count == 0 ? 0 : series.Max(s => s.Samples.Count == 0 ? 0 : s.Samples.Max()));
            using var grey = new SolidBrush(palette.Text);
            g.DrawString(top + " ms", Font, grey, 2, 0);
            g.DrawString("0", Font, grey, 2, Height - Font.Height);

            for (int i = 0; i < series.Count; i++) DrawSeries(g, series[i], top, i);
            if (compare) DrawLegend(g);
            else if (series.Count == 1) DrawP95(g, series[0].Samples, top);
        }

        private void DrawSeries(Graphics g, GraphSeries s, long top, int index)
        {
            // Where everything goes is computed by GraphLayout (and tested there); this only paints.
            var shapes = GraphLayout.Build(s.Samples, Width, Height, top, MaxSamples);

            var color = palette.Series(index, s.Color);
            using var line = new Pen(color, 1.5f) { DashStyle = LinePatterns[palette.Dash(index)] };
            // Alone, a loss is red; compared, it keeps its host's colour so you can tell whose it is.
            using var lost = new Pen(palette.Loss(compare, color), 2f);
            using var dot = new SolidBrush(color);

            foreach (var (from, to) in shapes.Lines) g.DrawLine(line, from.X, from.Y, to.X, to.Y);
            // A reply with no neighbour to be joined to (the first one, or one between two losses)
            // is a dot: as a line it would have no length and the reply would not show at all.
            foreach (var d in shapes.Dots) g.FillEllipse(dot, d.X - 2f, d.Y - 2f, 4f, 4f);
            var (tickBottom, tickTop) = GraphLayout.LossTick(index, series.Count, Height, compare);
            foreach (float x in shapes.LossXs) g.DrawLine(lost, x, tickBottom, x, tickTop);
        }

        // A dotted line at the recent p95: "95 % of the last pings were at or below this".
        private void DrawP95(Graphics g, IReadOnlyCollection<long> samples, long top)
        {
            if (RecentStats.From(samples).P95 is not double p95) return;

            float y = Height - 1 - (Height - 1f) * (float)p95 / top;
            using var pen = new Pen(palette.Text) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawLine(pen, 0, y, Width, y);

            string label = "p95 " + p95.ToString("0", CultureInfo.CurrentCulture);
            using var brush = new SolidBrush(palette.Text);
            g.DrawString(label, Font, brush, Width - g.MeasureString(label, Font).Width - 2, Math.Max(0, y - Font.Height));
        }

        // MaxLegend lines at a time; with more hosts a click on the legend shows the next ones. Each line carries a sample of its curve
        // (colour and pattern), and the full name of a host is in the tooltip of its line (names are cut to fit the graph).
        private void DrawLegend(Graphics g)
        {
            var (first, rows) = GraphLayout.LegendWindow(series.Count, legendOffset, MaxLegend);
            legendOffset = legendFirst = first;
            legendRows = rows;
            legendLeft = Width;
            float y = 0;
            for (int i = first; i < first + rows; i++)
            {
                var s = series[i];
                string name = FitName(g, s.Name, Width * 0.4f);
                var color = palette.Series(i, s.Color);
                using var brush = new SolidBrush(color);
                float x = Width - g.MeasureString(name, Font).Width - 2;
                g.DrawString(name, Font, brush, x, y);
                using var sample = new Pen(color, 1.5f) { DashStyle = LinePatterns[palette.Dash(i)] };
                g.DrawLine(sample, x - 22, y + Font.Height / 2f, x - 4, y + Font.Height / 2f);
                legendLeft = Math.Min(legendLeft, x - 22);
                y += Font.Height;
            }

            if (series.Count > rows)
            {
                using var grey = new SolidBrush(palette.Text);
                string more = $"{first + 1}-{first + rows} of {series.Count}: click for more";
                float x = Width - g.MeasureString(more, Font).Width - 2;
                g.DrawString(more, Font, grey, x, y);
                legendLeft = Math.Min(legendLeft, x);
            }
        }

        // The name cut (with an ellipsis) until it fits `room` pixels.
        private string FitName(Graphics g, string name, float room)
        {
            if (g.MeasureString(name, Font).Width <= room) return name;
            for (int n = name.Length - 1; n > 1; n--)
            {
                string cut = name[..n] + "…";
                if (g.MeasureString(cut, Font).Width <= room) return cut;
            }

            return "…";
        }

        private bool InLegend(Point p) =>
            compare && series.Count > 0 && p.X >= legendLeft && p.Y < (legendRows + (series.Count > legendRows ? 1 : 0)) * Font.Height;

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!InLegend(e.Location) || series.Count <= legendRows) return;
            legendOffset = legendFirst + legendRows >= series.Count ? 0 : legendFirst + legendRows;   // the next page, round to the first
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = e.Y / Math.Max(1, Font.Height);
            string text = InLegend(e.Location) && row < legendRows ? series[legendFirst + row].Name : "";
            if (text == legendTipText) return;
            legendTipText = text;
            legendTip.SetToolTip(this, text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) legendTip.Dispose();
            base.Dispose(disposing);
        }
    }

}