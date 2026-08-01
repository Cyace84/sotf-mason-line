using System.Collections.Generic;
using RedLoader;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace MasonLine;

/// <summary>
/// The item's name and description in the player's own language.
///
/// The SDK already writes one translation entry when an item is registered, but only into the table
/// of the locale that happens to be active, and it builds the plural by sticking an "s" on the end.
/// In a Russian game that leaves an English name with an English plural sitting in the inventory.
/// So we write our own entries into EVERY locale's table instead, under the keys the SDK chose:
/// I_&lt;itemId&gt;, I_&lt;itemId&gt;_PLURAL and I_&lt;itemId&gt;_DESC.
///
/// A mason line is a real tool with a real name in most languages, so these are the trade terms as
/// they appear on hardware store sites in each country, not word-for-word translations. Languages
/// that are not listed fall back to English.
/// </summary>
internal static class MasonLineStrings
{
    private readonly record struct Text(string Title, string Plural, string Description);

    private const string EnTitle = "Mason Line";
    private const string EnDescription =
        "Plant two stakes and stretch a string line between them. Free log placement snaps to the line.";

    /// <summary>Keyed by the language part of the locale code, so "pt-BR" and "pt" both match.</summary>
    private static readonly Dictionary<string, Text> ByLanguage = new()
    {
        ["en"] = new(EnTitle, "Mason Lines", EnDescription),
        ["ru"] = new("Строительный шнур", "Строительные шнуры",
            "Вбейте два колышка и натяните между ними шнур. Свободные брёвна будут вставать по этой линии."),
        ["de"] = new("Maurerschnur", "Maurerschnüre",
            "Zwei Pflöcke einschlagen und die Schnur dazwischen spannen. Frei platzierte Stämme richten sich an ihr aus."),
        ["fr"] = new("Cordeau de maçon", "Cordeaux de maçon",
            "Plantez deux piquets et tendez le cordeau entre eux. Les rondins libres s'alignent dessus."),
        ["es"] = new("Hilo para albañil", "Hilos para albañil",
            "Clava dos estacas y tensa el hilo entre ellas. Los troncos libres se alinean con él."),
        ["it"] = new("Filo da muratore", "Fili da muratore",
            "Pianta due picchetti e tendi il filo tra loro. I tronchi liberi si allineano ad esso."),
        ["pt"] = new("Fio de pedreiro", "Fios de pedreiro",
            "Finque duas estacas e estique o fio entre elas. Os troncos livres se alinham por ele."),
        ["pl"] = new("Sznurek murarski", "Sznurki murarskie",
            "Wbij dwa kołki i naciągnij między nimi sznurek. Swobodnie stawiane kłody ustawiają się wzdłuż niego."),
        ["ja"] = new("水糸", "水糸",
            "杭を二本打ち、その間に糸を張ります。自由配置の丸太が糸に沿って並びます。"),
    };

    /// <summary>Writes the item's strings into every locale the game offers. Called once, right after
    /// the item is registered, so the tables are ready before any inventory UI is built and switching
    /// language mid-game needs no extra work.</summary>
    internal static void Apply(int itemId)
    {
        try
        {
            var provider = LocalizationSettings.AvailableLocales;
            var locales = provider?.Locales;
            if (locales == null || locales.Count == 0)
            {
                RLog.Warning("[MasonLine] no locales available; the item keeps the SDK's English name");
                return;
            }

            string key = $"I_{itemId}";
            int done = 0;
            var missing = new List<string>();

            for (int i = 0; i < locales.Count; i++)
            {
                var locale = locales[i];
                if (locale == null) continue;
                string code = locale.Identifier.Code ?? "";
                string language = code.Split('-')[0].ToLowerInvariant();
                if (!ByLanguage.TryGetValue(language, out var text))
                {
                    missing.Add(code);
                    text = ByLanguage["en"];
                }

                // GetTable with an explicit locale, not LocalizationTools.ItemsTable: that property
                // hands back the ACTIVE locale's table, which is exactly the limitation we are here
                // to fix. Same casts the SDK uses — interop generics need the trip through object.
                var table = ((LocalizedDatabase<StringTable, StringTableEntry>)(object)
                    LocalizationSettings.StringDatabase).GetTable((TableReference)"Items", locale);
                if (table == null) continue;

                var detailed = (DetailedLocalizationTable<StringTableEntry>)(object)table;
                detailed.AddEntry(key, text.Title);
                detailed.AddEntry(key + "_PLURAL", text.Plural);
                detailed.AddEntry(key + "_DESC", text.Description);
                done++;
            }

            RLog.Msg($"[MasonLine] item strings written for {done} locale(s)" +
                     (missing.Count > 0 ? $"; English used for: {string.Join(", ", missing)}" : ""));
        }
        catch (System.Exception ex)
        {
            RLog.Warning($"[MasonLine] could not localize the item name: {ex.Message}");
        }
    }
}
