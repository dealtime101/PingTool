using System.Drawing;

namespace PingTool
{
    // The colours of text on the dark window (see Form1.Designer: BackColor 64, 64, 64), with the contrast they must keep.
    // WCAG: 4.5:1 for normal text, 3:1 for large text (the big result is 48 points bold).
    internal static class UiColors
    {
        public static readonly Color WindowBack = Color.FromArgb(64, 64, 64);

        // Tomato is fine for the big result (3.5:1, large text) but too dim for a sentence: this one reaches 4.5:1.
        public static readonly Color SmallTextRed = Color.FromArgb(255, 140, 120);

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color c)
        {
            static double Channel(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    }
}
