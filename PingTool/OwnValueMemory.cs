namespace PingTool
{
    // The own value of one limit while its box is unticked (the number then shows the global value): kept, to be put back when the box
    // is ticked again. Without it an accidental click on the box threw away what had been typed, and re-ticking offered the global value
    // as if it were the host's own.
    internal sealed class OwnValueMemory
    {
        private int? kept;

        // own: the value the host already had when the dialog opened (null: none).
        public OwnValueMemory(int? own) => kept = own;

        // The box was unticked: what the number showed is the own value, keep it.
        public void Remember(int shown) => kept = shown;

        // The box was ticked: the value to show, the one kept or, when the host never had one, the fallback (the global value).
        public int Restore(int fallback) => kept ?? fallback;
    }
}
