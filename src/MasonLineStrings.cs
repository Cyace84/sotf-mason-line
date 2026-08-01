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
/// So we write our own entries under the keys the SDK chose: I_&lt;itemId&gt;, I_&lt;itemId&gt;_PLURAL
/// and I_&lt;itemId&gt;_DESC.
///
/// Those entries do not survive a language change. Switching language runs
/// LocalizedDatabase.OnLocaleChanged, which calls ReleaseAllTables: every table is dropped and later
/// reloaded from the game's own assets, where our keys do not exist. The item then has no name at
/// all — the lookup misses and returns an empty string rather than falling back. So the entries are
/// rewritten whenever the selected locale turns out to be a different one; the mod's own tick does
/// the watching, because it runs after the release, while the ordering of the locale-changed event
/// against that release is not something we can prove.
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
        // The game ships Brazilian Portuguese only, and Brazil says "linha", not "fio".
        ["pt"] = new("Linha de pedreiro", "Linhas de pedreiro",
            "Finque duas estacas e estique a linha entre elas. Os troncos livres se alinham por ela."),
        ["pl"] = new("Sznurek murarski", "Sznurki murarskie",
            "Wbij dwa kołki i naciągnij między nimi sznurek. Swobodnie stawiane kłody ustawiają się wzdłuż niego."),
        ["ja"] = new("水糸", "水糸",
            "杭を二本打ち、その間に糸を張ります。自由配置の丸太が糸に沿って並びます。"),
        ["cs"] = new("Zednická šňůra", "Zednické šňůry",
            "Zatlučte dva kolíky a napněte mezi nimi šňůru. Volně pokládané kmeny se srovnají podle ní."),
        ["fi"] = new("Muurausnaru", "Muurausnarut",
            "Lyö kaksi paalua ja kiristä naru niiden väliin. Vapaasti aseteltavat tukit asettuvat sen mukaan."),
        ["sv"] = new("Murarsnöre", "Murarsnören",
            "Slå ner två pinnar och spänn snöret mellan dem. Fritt placerade stockar rätar in sig efter det."),
        ["tr"] = new("Duvarcı ipi", "Duvarcı ipleri",
            "İki kazık çakın ve aralarına ipi gerin. Serbestçe yerleştirilen kütükler ipe göre hizalanır."),
        ["ko"] = new("수평실", "수평실",
            "말뚝 두 개를 박고 그 사이에 실을 팽팽하게 당깁니다. 자유롭게 놓는 통나무가 그 선에 맞추어 정렬됩니다."),
        // Both Chinese locales are listed in full: the language part alone would send Taiwan the
        // simplified characters.
        ["zh-hans"] = new("瓦工线", "瓦工线",
            "钉下两根桩，在两桩之间拉紧线。自由放置的原木会自动对齐到这条线上。"),
        ["zh"] = new("瓦工线", "瓦工线",
            "钉下两根桩，在两桩之间拉紧线。自由放置的原木会自动对齐到这条线上。"),
        ["zh-hant"] = new("瓦工線", "瓦工線",
            "釘下兩根樁，在兩樁之間拉緊線。自由放置的原木會自動對齊到這條線上。"),
    };

    private static int _itemId;
    private static string _writtenFor = "";
    private static float _nextCheck;
    private static int _repairsLogged;

    /// <summary>Called once, right after the item is registered.</summary>
    internal static void Apply(int itemId)
    {
        _itemId = itemId;
        WriteForSelectedLocale();
    }

    /// <summary>Called from the mod tick. Rewrites the entries when the player has switched to a
    /// language we have not written since — that is also the case after switching away and back,
    /// because the table was released in the meantime.</summary>
    internal static void CheckLocale()
    {
        if (UnityEngine.Time.unscaledTime < _nextCheck) return;
        _nextCheck = UnityEngine.Time.unscaledTime + 0.5f;
        WriteForSelectedLocale();
    }

    private static void WriteForSelectedLocale()
    {
        try
        {
            var locale = LocalizationSettings.SelectedLocale;
            if (locale == null) return;

            string code = (locale.Identifier.Code ?? "").ToLowerInvariant();
            if (code.Length == 0) return;

            // Full code first: zh-Hant and zh-Hans differ in script, and matching on "zh" alone
            // would hand Taiwan the simplified characters.
            bool translated = ByLanguage.TryGetValue(code, out var text) ||
                              ByLanguage.TryGetValue(code.Split('-')[0], out text);
            if (!translated) text = ByLanguage["en"];

            // GetTable with an explicit locale, not LocalizationTools.ItemsTable: that property
            // hands back the ACTIVE locale's table, which is the limitation we are here to fix.
            // Same casts the SDK uses — interop generics need the trip through object.
            var table = ((LocalizedDatabase<StringTable, StringTableEntry>)(object)
                LocalizationSettings.StringDatabase).GetTable((TableReference)"Items", locale);
            if (table == null) return;   // not loaded yet; the next tick tries again

            string key = $"I_{_itemId}";
            var detailed = (DetailedLocalizationTable<StringTableEntry>)(object)table;

            // Read what is actually under the key instead of trusting that our last write survived.
            // Two things overwrite it: the table is thrown away and reloaded from the game's assets
            // on every language change, and the SDK seeds the key with its own English title when
            // the item is registered, which can land after us during startup.
            var entry = detailed.GetEntry(key);
            if (entry != null && entry.Value == text.Title) { _writtenFor = code; return; }

            detailed.AddEntry(key, text.Title);
            detailed.AddEntry(key + "_PLURAL", text.Plural);
            detailed.AddEntry(key + "_DESC", text.Description);

            bool sameLocale = code == _writtenFor;
            _writtenFor = code;
            if (!sameLocale || _repairsLogged++ < 5)
                RLog.Msg($"[MasonLine] item name for {code}: {text.Title}" +
                         (translated ? "" : " (no translation for this language, English used)") +
                         (sameLocale ? " (rewritten, the table had been reloaded)" : ""));
        }
        catch (System.Exception ex)
        {
            RLog.Warning($"[MasonLine] could not localize the item name: {ex.Message}");
        }
    }
}
