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
            // 
            // lblPingResult
            // 
            lblPingResult.AutoSize = true;
            lblPingResult.Font = new Font("Segoe UI", 32.25F, FontStyle.Bold, GraphicsUnit.Point, 0);
            lblPingResult.ForeColor = Color.Gray;
            lblPingResult.Location = new Point(50, 80);
            lblPingResult.Name = "lblPingResult";
            lblPingResult.Size = new Size(76, 59);
            lblPingResult.TabIndex = 3;
            lblPingResult.Text = "---";
            lblPingResult.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(64, 64, 64);
            ClientSize = new Size(284, 181);
            Controls.Add(lblPingResult);
            Controls.Add(btnStartStop);
            Controls.Add(cmbAddress);
            Controls.Add(lblAddress);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "PingTool";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblAddress;
        private ComboBox cmbAddress;
        private Button btnStartStop;
        private Label lblPingResult;
    }
}
