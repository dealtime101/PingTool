namespace PingTool
{
    internal enum EnterOutcome { Start, AddAndStart, Refuse }

    // What Enter does in the address box. Typing a new address and pressing Enter must monitor THAT address too: starting the
    // old list and quietly leaving the typed one out made the user believe they were watching something they were not.
    internal static class AddressBoxEnter
    {
        public static EnterOutcome Decide(string typed, bool listIsEmpty, bool typedIsInList)
        {
            string text = typed.Trim();
            if (text.Length == 0) return EnterOutcome.Start;       // nothing typed: the list as it is
            if (listIsEmpty) return EnterOutcome.Start;            // Start itself takes the typed address when the list is empty
            if (typedIsInList) return EnterOutcome.Start;          // already there
            return ProbeTarget.TryParse(text, out _, out _) ? EnterOutcome.AddAndStart : EnterOutcome.Refuse;
        }
    }
}
