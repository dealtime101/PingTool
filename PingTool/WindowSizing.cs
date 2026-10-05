namespace PingTool
{
    // The window can be resized (more hosts, a wider list, a taller graph). What the user chose is kept between launches, in pixels at
    // 100 % so that it means the same at any screen scaling (see DpiScale), and never smaller than the window needs.
    internal static class WindowSizing
    {
        public const int MaxClientWidth = 4000, MaxClientHeight = 3000;

        // The client size a maximized (or minimized) window will have when it is restored: its restore bounds (the outer size of the window
        // as it was) less what the frame and the title bar take (outer size less client size, the same either way). Never below 1.
        public static Size RestoredClient(Size restoreBounds, Size outer, Size client) =>
            new(Math.Max(1, restoreBounds.Width - (outer.Width - client.Width)), Math.Max(1, restoreBounds.Height - (outer.Height - client.Height)));

        // The saved size (0 = never saved) as a size to open with, no smaller than `minimum`; null = use the default.
        public static Size? Restore(int savedWidth, int savedHeight, Size minimum)
        {
            if (savedWidth <= 0 || savedHeight <= 0) return null;
            return new Size(Math.Clamp(savedWidth, minimum.Width, Math.Max(minimum.Width, MaxClientWidth)),
                            Math.Clamp(savedHeight, minimum.Height, Math.Max(minimum.Height, MaxClientHeight)));
        }

        // The size on screen back to pixels at 100 %, for saving.
        public static Size ToBase(Size onScreen, int dpi)
        {
            if (dpi <= 0) dpi = DpiScale.BaseDpi;
            return new Size((int)Math.Round(onScreen.Width * (double)DpiScale.BaseDpi / dpi, MidpointRounding.AwayFromZero),
                            (int)Math.Round(onScreen.Height * (double)DpiScale.BaseDpi / dpi, MidpointRounding.AwayFromZero));
        }
    }
}
