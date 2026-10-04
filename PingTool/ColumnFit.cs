namespace PingTool
{
    // The host list must never need a horizontal scroll bar: its columns have to fit in what is left of
    // the control once the vertical scroll bar (which appears as soon as the list is long enough) has taken
    // its share. The Host column takes whatever the fixed columns leave, so it follows the real width,
    // the real scroll bar and the screen scaling instead of a sum written for one of them.
    internal static class ColumnFit
    {
        public const int MinHostWidth = 60;

        // MinHostWidth is written for 96 DPI like every size here: dpi (the control's DeviceDpi) scales it with the screen.
        public static int HostWidth(int clientWidth, int scrollBarWidth, int otherColumnsWidth, int dpi = DpiScale.BaseDpi) =>
            Fill(clientWidth, scrollBarWidth, otherColumnsWidth, DpiScale.Scale(new Size(MinHostWidth, 0), dpi).Width);

        // The width of the one column that takes what the others leave (never below `min`).
        public static int Fill(int clientWidth, int scrollBarWidth, int otherColumnsWidth, int min) =>
            Math.Max(min, clientWidth - Math.Max(0, scrollBarWidth) - Math.Max(0, otherColumnsWidth));
    }
}
