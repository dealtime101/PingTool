namespace PingTool
{
    // Window sizes are written for 96 DPI (100 %). WinForms scales the controls to the screen but not a
    // size assigned in code, which at 150 % or 200 % then no longer holds the controls it was drawn for.
    internal static class DpiScale
    {
        public const int BaseDpi = 96;

        public static Size Scale(Size size, int dpi)
        {
            if (dpi <= 0) dpi = BaseDpi;   // an unknown DPI is "no scaling", never a zero-size window
            return new Size(
                (int)Math.Round(size.Width * (double)dpi / BaseDpi, MidpointRounding.AwayFromZero),
                (int)Math.Round(size.Height * (double)dpi / BaseDpi, MidpointRounding.AwayFromZero));
        }
    }
}
