using System.ComponentModel;
using System.Diagnostics;

namespace PingTool
{
    internal static class FileOpener
    {
        // What the box says after a file was written: where it is, and the offer to open it - nothing else tells the user the
        // file exists (the report is for a provider or a colleague: it is usually looked at at once).
        public static string SavedMessage(string what, string path) => $"{what} saved:\n{path}\n\nOpen it now?";

        // Writing succeeded: say where, offer to open with the program Windows associates with the file.
        public static void OfferToOpen(IWin32Window owner, string what, string path)
        {
            if (MessageBox.Show(owner, SavedMessage(what, path), "PingTool", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                MessageBox.Show(owner, "Could not open the file: " + ex.Message + "\n\nIt is saved at " + path, "PingTool");
            }
        }
    }
}
