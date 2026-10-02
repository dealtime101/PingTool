namespace PingTool
{
    internal enum HostChange { None, Down, Up }

    // Down after DownAfter consecutive failures (one lost packet is noise),
    // back up on the first success. Starts "up": a host that never answers
    // still alerts once, after DownAfter tries.
    internal sealed class HostMonitor
    {
        public const int DownAfter = 3;
        private int failures;
        private bool down;

        public void Reset() { failures = 0; down = false; }

        public HostChange Update(bool ok)
        {
            if (ok)
            {
                failures = 0;
                if (!down) return HostChange.None;
                down = false;
                return HostChange.Up;
            }

            failures++;
            if (down || failures < DownAfter) return HostChange.None;
            down = true;
            return HostChange.Down;
        }
    }
}
