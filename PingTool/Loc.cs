using System.Globalization;

namespace PingTool
{
    // The words PingTool shows, in English (the default) and French. A key is a CODE, never the sentence itself, so that a
    // new language is one more column and a missing translation is found by a test (every key used in the source exists,
    // both columns are filled and carry the same {n} placeholders), not by a user reading the wrong language.
    // The language is the one of the Windows display language; numbers and dates keep following the regional format.
    internal static class Loc
    {
        // Set by tests; null = follow the display language of Windows.
        internal static string? ForcedLanguage;

        internal static string Language =>
            (ForcedLanguage ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "fr" ? "fr" : "en";

        internal static readonly IReadOnlyDictionary<string, (string En, string Fr)> Table = new Dictionary<string, (string, string)>
        {
            ["timeline.empty"] = ("No data yet: start pinging this host.", "Aucune donnée pour l'instant : lancez les pings sur cet hôte."),
            ["timeline.summary"] = ("{0} to {1} ({2}) | {3} pings, {4} lost ({5}%)", "{0} au {1} ({2}) | {3} pings, {4} perdu(s) ({5} %)"),
            ["timeline.slowest"] = ("Highest average: {0} ms around {1}", "Moyenne la plus haute : {0} ms vers {1}"),
            ["timeline.lossiest"] = ("Most losses: {0}% of the pings ({1} of {2}) around {3}", "Plus de pertes : {0} % des pings ({1} sur {2}) vers {3}"),
            ["timeline.peak"] = ("Highest reply: {0} ms, above the scale of the chart ({1} ms): those columns are cut at the top and marked.", "Réponse la plus haute : {0} ms, au-dessus de l'échelle du graphique ({1} ms) : ces colonnes sont coupées en haut et marquées."),
            ["timeline.axis.clipped"] = ("{0} ms (peak {1})", "{0} ms (pic {1})"),
            ["timeline.incidents.none"] = ("No incident on this host in this period.", "Aucun incident sur cet hôte pendant cette période."),
            ["timeline.incidents"] = ("Incidents: {0}.", "Incidents : {0}."),
            ["timeline.outage"] = ("outage from {0} ({1})", "panne depuis {0} ({1})"),
            ["timeline.slowdown"] = ("slowdown from {0} ({1})", "ralentissement depuis {0} ({1})"),
            ["timeline.ongoing"] = ("still going on, {0}", "toujours en cours, {0}"),
            ["timeline.small"] = ("The window is too small to draw the chart: make it taller.", "La fenêtre est trop petite pour dessiner le graphique : agrandissez-la."),
            ["timeline.network"] = ("Network changes of this PC (cyan lines): ", "Changements du réseau de ce PC (traits cyan) : "),
            ["timeline.more"] = (" (+{0} more)", " (+{0} de plus)"),
        };

        // The text for a key in the current language, with its arguments formatted in the current regional format.
        public static string T(string key, params object[] args)
        {
            if (!Table.TryGetValue(key, out var pair)) return key;   // a missing key shows itself: ugly, and found at once
            string text = Language == "fr" && pair.Fr.Length > 0 ? pair.Fr : pair.En;
            return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
        }
    }
}
