namespace PingTool
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            lblAddress = new Label();
            cmbAddress = new ComboBox();
            btnStartStop = new Button();
            lblPingResult = new Label();
            lblStats = new Label();
            graphLatency = new LatencyGraph();
            lblInterval = new Label();
            lblTimeout = new Label();
            lblSize = new Label();
            chkAlert = new CheckBox();
            lstHosts = new ListView();
            colHost = new ColumnHeader();
            colLast = new ColumnHeader();
            colAvg = new ColumnHeader();
            colLoss = new ColumnHeader();
            btnAddHost = new Button();
            btnRemoveHost = new Button();
            btnExport = new Button();
            btnIncidents = new Button();
            btnReport = new Button();
            chkCompact = new CheckBox();
            lblState = new Label();
            lblDiagnosis = new Label();
            chkCompare = new CheckBox();
            numInterval = new NumericUpDown();
            numTimeout = new NumericUpDown();
            numSize = new NumericUpDown();
            ((System.ComponentModel.ISupportInitialize)numInterval).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numTimeout).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numSize).BeginInit();
            SuspendLayout();
            // 
            // lblAddress
            // 
            lblAddress.AutoSize = true;
            lblAddress.ForeColor = Color.White;
            lblAddress.Location = new Point(30, 20);
            lblAddress.Name = "lblAddress";
            lblAddress.Size = new Size(79, 15);
            lblAddress.TabIndex = 0;
            lblAddress.Text = "Enter Address";
            // 
            // cmbAddress
            // 
            cmbAddress.FormattingEnabled = true;
            cmbAddress.Location = new Point(30, 45);
            cmbAddress.Name = "cmbAddress";
            cmbAddress.Size = new Size(220, 23);
            cmbAddress.TabIndex = 1;
            cmbAddress.Text = "google.ca";
            // 
            // btnStartStop
            // 
            btnStartStop.Location = new Point(85, 145);
            btnStartStop.Name = "btnStartStop";
            btnStartStop.Size = new Size(100, 32);
            btnStartStop.TabIndex = 2;
            btnStartStop.Text = "Start";
            btnStartStop.UseVisualStyleBackColor = true;
            btnStartStop.Click += btnStartStop_Click;
            // 
            // lblPingResult
            // 
            lblPingResult.AutoSize = false;
            lblPingResult.Font = new Font("Segoe UI", 32.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            lblPingResult.ForeColor = Color.Silver;
            lblPingResult.Location = new Point(30, 80);
            lblPingResult.Name = "lblPingResult";
            lblPingResult.Size = new Size(224, 59);
            lblPingResult.TabIndex = 3;
            lblPingResult.Text = "---";
            lblPingResult.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // lblStats
            // 
            lblStats.AutoSize = true;
            lblStats.ForeColor = Color.White;
            lblStats.Location = new Point(30, 190);
            lblStats.Name = "lblStats";
            lblStats.Size = new Size(10, 15);
            lblStats.TabIndex = 4;
            lblStats.Text = "Min - / Avg - / Max - ms\nJitter - ms | Loss 0% (0/0)\n-";
            //
            // graphLatency
            //
            graphLatency.Location = new Point(30, 277);
            graphLatency.Name = "graphLatency";
            graphLatency.Size = new Size(224, 90);
            graphLatency.TabIndex = 5;
            //
            // lblInterval
            //
            lblInterval.AutoSize = true;
            lblInterval.ForeColor = Color.White;
            lblInterval.Location = new Point(30, 367);
            lblInterval.Name = "lblInterval";
            lblInterval.TabIndex = 6;
            lblInterval.Text = "Interval (ms)";
            //
            // lblTimeout
            //
            lblTimeout.AutoSize = true;
            lblTimeout.ForeColor = Color.White;
            lblTimeout.Location = new Point(106, 367);
            lblTimeout.Name = "lblTimeout";
            lblTimeout.TabIndex = 7;
            lblTimeout.Text = "Timeout (ms)";
            //
            // lblSize
            //
            lblSize.AutoSize = true;
            lblSize.ForeColor = Color.White;
            lblSize.Location = new Point(182, 367);
            lblSize.Name = "lblSize";
            lblSize.TabIndex = 8;
            lblSize.Text = "Size (bytes)";
            //
            // numInterval
            //
            numInterval.Increment = new decimal(new int[] { 100, 0, 0, 0 });
            numInterval.Location = new Point(30, 385);
            numInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numInterval.Minimum = new decimal(new int[] { 100, 0, 0, 0 });
            numInterval.Name = "numInterval";
            numInterval.Size = new Size(68, 23);
            numInterval.TabIndex = 9;
            numInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            //
            // numTimeout
            //
            numTimeout.Increment = new decimal(new int[] { 100, 0, 0, 0 });
            numTimeout.Location = new Point(106, 385);
            numTimeout.Maximum = new decimal(new int[] { 10000, 0, 0, 0 });
            numTimeout.Minimum = new decimal(new int[] { 100, 0, 0, 0 });
            numTimeout.Name = "numTimeout";
            numTimeout.Size = new Size(68, 23);
            numTimeout.TabIndex = 10;
            numTimeout.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            //
            // numSize
            //
            numSize.Location = new Point(182, 385);
            numSize.Maximum = new decimal(new int[] { 65500, 0, 0, 0 });
            numSize.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numSize.Name = "numSize";
            numSize.Size = new Size(72, 23);
            numSize.TabIndex = 11;
            numSize.Value = new decimal(new int[] { 32, 0, 0, 0 });
            //
            // chkAlert
            //
            chkAlert.AutoSize = true;
            chkAlert.Checked = true;
            chkAlert.CheckState = CheckState.Checked;
            chkAlert.ForeColor = Color.White;
            chkAlert.Location = new Point(30, 417);
            chkAlert.Name = "chkAlert";
            chkAlert.TabIndex = 12;
            chkAlert.Text = "Alert on outage / slowdown / recovery";
            chkAlert.UseVisualStyleBackColor = true;
            //
            // lstHosts
            //
            lstHosts.Columns.AddRange(new ColumnHeader[] { colHost, colLast, colAvg, colLoss });
            lstHosts.FullRowSelect = true;
            lstHosts.HideSelection = false;
            lstHosts.Location = new Point(300, 20);
            lstHosts.MultiSelect = false;
            lstHosts.Name = "lstHosts";
            lstHosts.Size = new Size(260, 250);
            lstHosts.TabIndex = 13;
            lstHosts.UseCompatibleStateImageBehavior = false;
            lstHosts.View = View.Details;
            lstHosts.SelectedIndexChanged += lstHosts_SelectedIndexChanged;
            //
            // colHost
            //
            colHost.Text = "Host";
            colHost.Width = 100;
            //
            // colLast
            //
            colLast.Text = "Last";
            colLast.Width = 60;
            //
            // colAvg
            //
            colAvg.Text = "Avg";
            colAvg.Width = 45;
            //
            // colLoss
            //
            colLoss.Text = "Loss";
            colLoss.Width = 45;
            //
            // btnAddHost
            //
            btnAddHost.Location = new Point(300, 328);
            btnAddHost.Name = "btnAddHost";
            btnAddHost.Size = new Size(120, 28);
            btnAddHost.TabIndex = 14;
            btnAddHost.Text = "Add address";
            btnAddHost.UseVisualStyleBackColor = true;
            btnAddHost.Click += btnAddHost_Click;
            //
            // btnRemoveHost
            //
            btnRemoveHost.Location = new Point(440, 328);
            btnRemoveHost.Name = "btnRemoveHost";
            btnRemoveHost.Size = new Size(120, 28);
            btnRemoveHost.TabIndex = 15;
            btnRemoveHost.Text = "Remove selected";
            btnRemoveHost.UseVisualStyleBackColor = true;
            btnRemoveHost.Click += btnRemoveHost_Click;
            //
            // btnExport
            //
            btnExport.Location = new Point(300, 362);
            btnExport.Name = "btnExport";
            btnExport.Size = new Size(126, 28);
            btnExport.TabIndex = 16;
            btnExport.Text = "Export CSV...";
            btnExport.UseVisualStyleBackColor = true;
            btnExport.Click += btnExport_Click;
            //
            // btnIncidents
            //
            btnIncidents.Location = new Point(434, 362);
            btnIncidents.Name = "btnIncidents";
            btnIncidents.Size = new Size(126, 28);
            btnIncidents.TabIndex = 21;
            btnIncidents.Text = "Incidents (0)";
            btnIncidents.UseVisualStyleBackColor = true;
            btnIncidents.Click += btnIncidents_Click;
            //
            // btnReport
            //
            btnReport.Location = new Point(300, 424);
            btnReport.Name = "btnReport";
            btnReport.Size = new Size(260, 28);
            btnReport.TabIndex = 22;
            btnReport.Text = "Save diagnostic report (HTML)...";
            btnReport.UseVisualStyleBackColor = true;
            btnReport.Click += btnReport_Click;
            //
            // chkCompact
            //
            chkCompact.AutoSize = true;
            chkCompact.ForeColor = Color.White;
            chkCompact.Location = new Point(125, 19);
            chkCompact.Name = "chkCompact";
            chkCompact.TabIndex = 17;
            chkCompact.Text = "Compact (on top)";
            chkCompact.UseVisualStyleBackColor = true;
            chkCompact.CheckedChanged += chkCompact_CheckedChanged;
            //
            // lblState
            //
            lblState.AutoSize = true;
            lblState.ForeColor = Color.Silver;
            lblState.Location = new Point(195, 154);
            lblState.Name = "lblState";
            lblState.TabIndex = 18;
            lblState.Text = "";
            //
            // lblDiagnosis
            //
            lblDiagnosis.AutoSize = false;
            lblDiagnosis.ForeColor = Color.Silver;
            lblDiagnosis.Location = new Point(300, 276);
            lblDiagnosis.Name = "lblDiagnosis";
            lblDiagnosis.Size = new Size(260, 46);
            lblDiagnosis.TabIndex = 19;
            lblDiagnosis.Text = "";
            //
            // chkCompare
            //
            chkCompare.AutoSize = true;
            chkCompare.ForeColor = Color.White;
            chkCompare.Location = new Point(300, 394);
            chkCompare.Name = "chkCompare";
            chkCompare.TabIndex = 20;
            chkCompare.Text = "Compare all hosts on one graph";
            chkCompare.UseVisualStyleBackColor = true;
            chkCompare.CheckedChanged += chkCompare_CheckedChanged;
            //
            // MainForm
            //
            AutoScaleDimensions = new SizeF(7F, 15F);
            AcceptButton = btnStartStop;
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(64, 64, 64);
            ClientSize = new Size(580, 460);
            Controls.Add(chkCompare);
            Controls.Add(lblDiagnosis);
            Controls.Add(lblState);
            Controls.Add(chkCompact);
            Controls.Add(btnReport);
            Controls.Add(btnIncidents);
            Controls.Add(btnExport);
            Controls.Add(btnRemoveHost);
            Controls.Add(btnAddHost);
            Controls.Add(lstHosts);
            Controls.Add(chkAlert);
            Controls.Add(numSize);
            Controls.Add(numTimeout);
            Controls.Add(numInterval);
            Controls.Add(lblSize);
            Controls.Add(lblTimeout);
            Controls.Add(lblInterval);
            Controls.Add(graphLatency);
            Controls.Add(lblStats);
            Controls.Add(lblPingResult);
            Controls.Add(btnStartStop);
            Controls.Add(cmbAddress);
            Controls.Add(lblAddress);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "PingTool";
            ((System.ComponentModel.ISupportInitialize)numInterval).EndInit();
            ((System.ComponentModel.ISupportInitialize)numTimeout).EndInit();
            ((System.ComponentModel.ISupportInitialize)numSize).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblAddress;
        private ComboBox cmbAddress;
        private Button btnStartStop;
        private Label lblPingResult;
        private Label lblStats;
        private LatencyGraph graphLatency;
        private Label lblInterval;
        private Label lblTimeout;
        private Label lblSize;
        private CheckBox chkAlert;
        private ListView lstHosts;
        private ColumnHeader colHost;
        private ColumnHeader colLast;
        private ColumnHeader colAvg;
        private ColumnHeader colLoss;
        private Button btnAddHost;
        private Button btnRemoveHost;
        private Button btnExport;
        private Button btnIncidents;
        private Button btnReport;
        private CheckBox chkCompact;
        private Label lblState;
        private Label lblDiagnosis;
        private CheckBox chkCompare;
        private NumericUpDown numInterval;
        private NumericUpDown numTimeout;
        private NumericUpDown numSize;
    }
}
