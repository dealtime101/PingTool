using System;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;

namespace PingTool
{
    public partial class MainForm : Form
    {
        private bool isRunning = false;
        private CancellationTokenSource? cts;

        public MainForm()
        {
            InitializeComponent();

            cmbAddress.Items.AddRange(new string[]
            {
        "google.ca",
        "8.8.8.8",
        "1.1.1.1",
        "192.168.0.1"
            });
        }

        private async void btnStartStop_Click(object sender, EventArgs e)
        {
            Debug.WriteLine("CLICK");

            if (!isRunning)
            {
                Debug.WriteLine("START MODE");

                string address = cmbAddress.Text.Trim();
                Debug.WriteLine("Address: " + address);

                if (string.IsNullOrWhiteSpace(address))
                {
                    MessageBox.Show("No address");
                    return;
                }

                isRunning = true;
                btnStartStop.Text = "Stop";
                cmbAddress.Enabled = false;

                cts = new CancellationTokenSource();

                try
                {
                    Debug.WriteLine("Calling StartPinging...");
                    await StartPinging(cts.Token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("ERROR: " + ex.Message);
                }
            }
            else
            {
                Debug.WriteLine("STOP MODE");

                isRunning = false;
                btnStartStop.Text = "Start";
                cmbAddress.Enabled = true;

                cts?.Cancel();
            }
        }

        private async Task StartPinging(CancellationToken token)
        {
            string address = cmbAddress.Text.Trim();

            using Ping ping = new Ping();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var reply = await ping.SendPingAsync(address, 1000);

                    if (reply.Status == IPStatus.Success)
                        UpdatePingUI(reply.RoundtripTime);
                    else
                        UpdatePingUI(-1);
                }
                catch
                {
                    UpdatePingUI(-1);
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void UpdatePingUI(long ping)
        {
            if (ping >= 0)
            {
                lblPingResult.Text = ping + " ms";

                if (ping < 50)
                    lblPingResult.ForeColor = Color.LimeGreen;
                else if (ping < 100)
                    lblPingResult.ForeColor = Color.Orange;
                else
                    lblPingResult.ForeColor = Color.Red;
            }
            else
            {
                lblPingResult.Text = "Timeout";
                lblPingResult.ForeColor = Color.Red;
            }
        }
    }
}