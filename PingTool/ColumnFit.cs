namespace PingTool
{
    // The host list must never need a horizontal scroll bar: its columns have to fit in what is left of
    // the control once the vertical scroll bar (which appears as soon as the list is long enough) has taken
    // its share. The Host column takes whatever the fixed columns leave, so it follows the real width,
    // the real scroll bar and the screen scaling instead of a sum written for one of them.
    internal static class ColumnFit
    {
        public const int MinHostWidth = 60;

        public static int HostWidth(int clientWidth, int scrollBarWidth, int otherColumnsWidth) =>
            Math.Max(MinHostWidth, clientWidth - Math.Max(0, scrollBarWidth) - Math.Max(0, otherColumnsWidth));
    }
}
