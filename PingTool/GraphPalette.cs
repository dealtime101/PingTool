using System.Drawing;

namespace PingTool
{
    // The colours of the latency graph. Normally the dark palette of the window; when Windows is in a HIGH CONTRAST theme the
    // user has chosen the colours they can read, so the graph takes the system's (window, text, highlight, hot-track, grey text)
    // and tells the targets apart by line style too, since colour is all a high-contrast theme leaves.
    internal sealed class GraphPalette
    {
        private readonly bool highContrast;

        private GraphPalette(bool highContrast) => this.highContrast = highContrast;

        public static GraphPalette For(bool highContrast) => new(highContrast);

        public Color Back => highContrast ? SystemColors.Window : Color.FromArgb(40, 40, 40);
        public Color Text => highContrast ? SystemColors.WindowText : Color.Silver;

        // The colour of the n-th line: its own in the dark palette; one of four system colours in high contrast.
        public Color Series(int index, Color own) => !highContrast ? own : (index % 4) switch
        {
            0 => SystemColors.WindowText,
            1 => SystemColors.HotTrack,
            2 => SystemColors.Highlight,
            _ => SystemColors.GrayText,
        };

        // A lost ping: red when one host is shown, its host's colour when several are compared (so you can tell whose it is).
        public Color Loss(bool compare, Color seriesColor) => compare ? seriesColor : highContrast ? SystemColors.HotTrack : Color.Red;

        // The line pattern of the n-th line, as an index into Patterns (0 solid, 1 dash, 2 dot, 3 dash-dot); always solid in the dark palette.
        public int Dash(int index) => highContrast ? index % Patterns : 0;
        public const int Patterns = 4;
    }
}
