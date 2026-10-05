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
            ["timeline.summary"] = ("{0} to {1} ({2}) | {3} pings, {4} lost ({5}%)", "De {0} à {1} ({2}) | {3} pings, {4} perdu(s) ({5} %)"),
            ["timeline.slowest"] = ("Highest average: {0} ms around {1}", "Moyenne la plus haute : {0} ms vers {1}"),
            ["timeline.lossiest"] = ("Most losses: {0}% of the pings ({1} of {2}) around {3}", "Le plus de pertes : {0} % des pings ({1} sur {2}) vers {3}"),
            ["timeline.peak"] = ("Highest reply: {0} ms, above the scale of the chart ({1} ms): those columns are cut at the top and marked.", "Réponse la plus haute : {0} ms, au-dessus de l'échelle du graphique ({1} ms) : ces colonnes sont coupées en haut et marquées."),
            ["timeline.axis.clipped"] = ("{0} ms (peak {1})", "{0} ms (pic {1})"),
            ["timeline.incidents.none"] = ("No incident on this host in this period.", "Aucun incident sur cet hôte pendant cette période."),
            ["timeline.incidents"] = ("Incidents: {0}.", "Incidents : {0}."),
            // "depuis" in French says that it still goes on: a finished incident is "le {0}" (a date and a time), and one that is still going on
            // says so itself, with "timeline.ongoing" in the brackets.
            ["timeline.outage"] = ("outage from {0} ({1})", "panne le {0} ({1})"),
            ["timeline.slowdown"] = ("slowdown from {0} ({1})", "ralentissement le {0} ({1})"),
            ["timeline.ongoing"] = ("still going on, {0}", "toujours en cours, {0}"),
            ["timeline.small"] = ("The window is too small to draw the chart: make it taller.", "La fenêtre est trop petite pour dessiner le graphique : agrandissez-la."),
            ["timeline.network"] = ("Network changes of this PC (cyan lines): ", "Changements du réseau de ce PC (traits cyan) : "),
            ["timeline.more"] = (" (+{0} more)", " (+{0} de plus)"),
            ["timeline.periods"] = ("Over the session, period by period: {0}.", "Au fil de la session, période par période : {0}."),
            ["timeline.period"] = ("from {0}: average {1} ms, {2}% lost", "dès {0} : moyenne {1} ms, {2} % perdus"),
            ["timeline.title"] = ("PingTool - Session timeline", "PingTool - Chronologie de la session"),
            ["timeline.legend"] = ("Each column is a slice of the session: grey = lowest to highest reply, green = average, red at the bottom = lost pings (stronger = more), striped red background = outage, plain orange tint = slowdown, dotted cyan line = network change of this PC, red-orange triangle at the top = highest reply above the scale (the column is cut).",
                "Chaque colonne est une tranche de la session : gris = de la réponse la plus basse à la plus haute, vert = moyenne, rouge en bas = pings perdus (plus fort = plus), fond rouge rayé = panne, teinte orange unie = ralentissement, trait cyan pointillé = changement de réseau de ce PC, triangle rouge-orange en haut = réponse la plus haute au-dessus de l'échelle (la colonne est coupée)."),
            ["timeline.close"] = ("Close", "Fermer"),
            ["timeline.target"] = ("Target", "Cible"),
            ["timeline.chart"] = ("Session timeline", "Chronologie de la session"),
            ["graph.empty"] = ("No data yet: start pinging.", "Aucune donnée pour l'instant : lancez les pings."),
            ["graph.selected"] = ("Selected target", "Cible sélectionnée"),
            ["graph.noping"] = ("{0}: no ping yet", "{0} : aucun ping pour l'instant"),
            ["graph.last.lost"] = ("last ping lost", "dernier ping perdu"),
            ["graph.last"] = ("last {0} ms", "dernier ping : {0} ms"),
            ["graph.p95"] = (", 95th percentile {0} ms", ", 95e centile {0} ms"),
            // {0} target, {1} last ping, {2} percentile, {3} lost, {4} how many pings, {5} trend; one ping and several pings are two sentences
            ["graph.line.one"] = ("{0}: {1}{2}, {3} of the last {4} ping lost{5}", "{0} : {1}{2}, {3} perdu sur {4} ping{5}"),
            ["graph.line.many"] = ("{0}: {1}{2}, {3} of the last {4} pings lost{5}", "{0} : {1}{2}, {3} perdus sur les {4} derniers pings{5}"),
            ["graph.trend"] = ("; over the window, oldest first: {0}", " ; sur la fenêtre, du plus ancien au plus récent : {0}"),
            ["graph.trend.part"] = ("{0} {1} lost", "{0}, {1} perdu(s)"),
            ["graph.noreply"] = ("no reply", "aucune réponse"),
            ["graph.ms"] = ("{0} ms", "{0} ms"),
            ["stats.waiting"] =("Last pings: waiting for data", "Derniers pings : en attente de données"),
            ["stats.need"] = ("Last {0}: need {1} replies for percentiles", "Derniers {0} : il faut {1} réponses pour les percentiles"),
            ["stats.last"] = ("Last {0}: p50 {1}", "Derniers {0} : p50 {1}"),
            ["stats.p95needs"] = (" (p95 needs {0} replies)", " (le p95 demande {0} réponses)"),
            ["stats.jitter"] = ("Recent jitter {0} ms | loss {1}% ({2}/{3})", "Gigue récente {0} ms | perte {1} % ({2}/{3})"),
            ["stats.voice"] = (" | voice {0} ({1})", " | voix {0} ({1})"),
            ["stats.voice.good"] = ("good", "bonne"),
            ["stats.voice.fair"] = ("fair", "correcte"),
            ["stats.voice.poor"] = ("poor", "médiocre"),
            ["stats.voice.bad"] = ("bad", "mauvaise"),
            ["alert.down"] = ("{0} is down", "{0} ne répond plus"),
            ["alert.up"] = ("{0} is back up", "{0} répond de nouveau"),
            ["alert.up.after"] = ("{0} is back up after {1}", "{0} répond de nouveau après {1}"),
            ["alert.degraded"] = ("{0} is degraded: {1:0.#}% loss, average {2} ms over the last {3} pings", "{0} est dégradé : {1:0.#} % de perte, moyenne {2} ms sur les {3} derniers pings"),
            ["alert.degraded.noreply"] = ("{0} is degraded: {1:0.#}% loss, no reply in the last {2} pings", "{0} est dégradé : {1:0.#} % de perte, aucune réponse sur les {2} derniers pings"),
            ["alert.recovered"] = ("{0} is back to normal", "{0} est revenu à la normale"),
        };

        // The text for a key in the current language, with its arguments formatted in the current regional format.
        public static string T(string key, params object[] args)
        {
            if (!Table.TryGetValue(key, out var pair)) return key;   // a missing key shows itself: ugly, and found at once
            string text = Language == "fr" && pair.Fr.Length > 0 ? pair.Fr : pair.En;
            if (args.Length == 0) return text;
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, args);
            }
            catch (FormatException ex)
            {
                // A caller that gives fewer arguments than the sentence has places (or a sentence with a broken place) must not take the
                // alert or the timeline down with it: the sentence as it is, with its key, shows the fault at once and stays readable.
                System.Diagnostics.Debug.WriteLine($"Loc.T(\"{key}\"): {ex.Message}");
                return text + " [" + key + "]";
            }
        }
    }
}
