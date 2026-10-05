namespace PingTool
{
    // A text box that fills up while someone reads it (the route being traced): when does new text pull the view down?
    internal static class LiveTextRule
    {
        // Only for a reader who is at the end and has nothing selected: the last line (or the one before, a half line may show) is on
        // screen. Someone who scrolled up to read the first hops, or selected text to copy it, is left where they are.
        public static bool Follows(int selectionLength, int lastVisibleLine, int lastLine) =>
            selectionLength == 0 && lastVisibleLine >= lastLine - 1;
    }
}
