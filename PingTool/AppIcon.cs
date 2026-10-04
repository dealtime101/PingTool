namespace PingTool
{
    // The program's own icon (PingTool.ico, embedded): for the window and for the notification area, instead of the generic one
    // of Windows, which is the same for every program that has none and makes PingTool impossible to find in a crowd of icons.
    internal static class AppIcon
    {
        public const string ResourceName = "PingTool.ico";

        // size = what the place wants (the small icon of the notification area, say): the .ico holds 16 to 256 pixels and Windows
        // picks the closest frame. Falls back to the generic icon if the resource cannot be read: no icon is not a reason not to start.
        public static Icon Load(Size? size = null)
        {
            try
            {
                using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
                if (stream is not null) return size is Size s ? new Icon(stream, s) : new Icon(stream);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine("Icon not loaded: " + ex.Message);
            }

            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
