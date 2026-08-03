using RedLoader;
using Sons.Crafting;
using Sons.Inventory;
using Sons.Items.Core;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace MasonLine;

/// <summary>LineTool, bundle-economy half: consume/refund of the crafted bundle and the
/// per-save-slot marker file that restores staked-out bundles across a game restart.</summary>
internal static partial class LineTool
{
    // ---- bundle economy (user-approved design): one craft = one bundle = one active line,
    // unlimited length, NO ingredient burn. Physicality: while the line stands in the world the bundle
    // is OUT of the inventory (RemoveItem instantDestroy:true — the same flag the game itself uses
    // for placement-consume, memento-mori flag telemetry); collecting the line (hold Dismantle) returns it.
    // A marker file survives a game restart so a saved-with-line-out inventory gets the bundle back
    // (the line itself is not in the save). Signatures observed: PlayerInventory.AddItem/RemoveItem/
    // AmountOf decompile.
    /// <summary>Upper bound for a refund, mirroring the item's stack size at _maxAmount. Guards the
    /// refund loop against a hand-edited or corrupt marker.</summary>
    private const int MaxKits = 20;

    private static int _kitsOut;   // kits currently staked out as standing lines
    // UserData is where RedLoader mods keep their own files; persistentDataPath is the game's save
    // folder and has no business holding ours.
    private static string KitMarkerPath =>
        System.IO.Path.Combine(RedLoader.Utils.LoaderEnvironment.UserDataDirectory, "MasonLine.line-out");

    /// <summary>The marker used to live in the save folder. Move it once, so a bundle staked out before
    /// this update is still refunded instead of silently lost.</summary>
    private static void MigrateMarkerFromSaveFolder()
    {
        try
        {
            var old = System.IO.Path.Combine(Application.persistentDataPath, "MasonLine.line-out");
            if (!System.IO.File.Exists(old) || System.IO.File.Exists(KitMarkerPath)) return;
            System.IO.File.Copy(old, KitMarkerPath);
            System.IO.File.Delete(old);
            RLog.Msg($"[MasonLine] moved the bundle marker to {KitMarkerPath}");
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] bundle marker move failed: {ex.Message}"); }
    }

    // Save-slot id source: GameSetupManager.GetSelectedSaveId() is only valid in the LOAD MENU —
    // during an in-game save it returns 0 (observed: marker "0 1", refund missed, bundle lost — user
    // repro). The dir argument of SaveGameManager.Save/Load ends in the numeric save id
    // (…/SinglePlayer/<id>), so BOTH sides parse the id from the same source: the path.
    private static uint _loadedSaveId;   // id of the save the current world was loaded from (0 = new game)

    internal static uint ParseSaveIdFromDir(string dir)
    {
        try
        {
            var parts = dir.Replace('\\', '/').TrimEnd('/').Split('/');
            for (int i = parts.Length - 1; i >= 0; i--)
                if (uint.TryParse(parts[i], out var id) && id > 0) return id;
        }
        catch { }
        return 0;
    }

    /// <summary>Harmony prefix on SaveGameManager.Load: remember which slot this world comes from.</summary>
    internal static void OnLoadDir(string dir) => _loadedSaveId = ParseSaveIdFromDir(dir);

    /// <summary>Harmony prefix on SaveGameManager.Save: stamp the marker with THIS save slot's id +
    /// bundles currently staked out. Written at save time, the marker matches the save's inventory
    /// EXACTLY — so the load-time refund is unconditional, no AmountOf heuristics.</summary>
    internal static void OnBeforeSave(string dir)
    {
        try
        {
            uint id = ParseSaveIdFromDir(dir);
            // Rewrite OUR slot's line only. A single global line meant saving slot B overwrote slot
            // A's pending refund, so loading A found no marker and the staked-out bundle was gone for
            // good. The delete branch already knew this and compared
            // ids; the write branch did not.
            var lines = ReadMarkerLines();
            if (lines == null)
            {
                RLog.Error($"[MasonLine] the bundle marker is unreadable, so {_kitsOut} staked-out bundle(s) " +
                           "cannot be recorded for this save; collect your lines before quitting");
                return;
            }
            lines.RemoveAll(l => SlotOfMarkerLine(l) == id);
            if (_kitsOut > 0) lines.Add($"{id} {_kitsOut}");
            if (!WriteMarkerLines(lines) && _kitsOut > 0)
                RLog.Error($"[MasonLine] {_kitsOut} staked-out bundle(s) could not be recorded; " +
                           "collect your lines before quitting or they are lost");
        }
        catch (System.Exception ex) { RLog.Error($"[MasonLine] saving the bundle marker failed: {ex}"); }
    }

    /// <summary>Marker file body: one "&lt;slotId&gt; &lt;bundles&gt;" line per save slot that has bundles in the
    /// field. Missing file = nobody has anything staked out.</summary>
    /// <summary>Returns null when the file exists but could not be read. A missing file is an empty
    /// list; an unreadable one must NOT look the same, or a rewrite would drop the other slots.</summary>
    private static System.Collections.Generic.List<string>? ReadMarkerLines()
    {
        var list = new System.Collections.Generic.List<string>();
        try
        {
            if (!System.IO.File.Exists(KitMarkerPath)) return list;
            foreach (var raw in System.IO.File.ReadAllLines(KitMarkerPath))
            {
                var line = raw.Trim();
                if (line.Length > 0) list.Add(line);
            }
        }
        catch (System.Exception ex)
        {
            RLog.Error($"[MasonLine] cannot read {KitMarkerPath}: {ex.Message}");
            return null;
        }
        return list;
    }

    /// <summary>Writes through a temp file so a crash or a full disk cannot leave a half-written
    /// marker: the old one survives intact instead. False means the bundles are NOT recorded.</summary>
    private static bool WriteMarkerLines(System.Collections.Generic.List<string> lines)
    {
        try
        {
            if (lines.Count == 0)
            {
                if (System.IO.File.Exists(KitMarkerPath)) System.IO.File.Delete(KitMarkerPath);
                return true;
            }
            var tmp = KitMarkerPath + ".tmp";
            System.IO.File.WriteAllText(tmp, string.Join("\n", lines) + "\n");
            System.IO.File.Move(tmp, KitMarkerPath, true);
            return true;
        }
        catch (System.Exception ex)
        {
            RLog.Error($"[MasonLine] cannot write {KitMarkerPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Slot id a marker line belongs to, or uint.MaxValue when the line is not "&lt;id&gt; &lt;n&gt;"
    /// (legacy shapes are left alone here and handled once at restore time).</summary>
    private static uint SlotOfMarkerLine(string line) =>
        ParseMarkerLine(line, out var id, out _) ? id : uint.MaxValue;

    /// <summary>The one parser for "&lt;slotId&gt; &lt;bundles&gt;". Splits on any run of whitespace, demands
    /// exactly two fields, and caps the count at a stack of bundles so a hand-edited file cannot spin
    /// the refund loop.</summary>
    private static bool ParseMarkerLine(string line, out uint slot, out int kits)
    {
        slot = uint.MaxValue; kits = 0;
        var parts = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!uint.TryParse(parts[0], out slot)) { slot = uint.MaxValue; return false; }
        if (!int.TryParse(parts[1], out kits) || kits < 0) { kits = 0; return false; }
        if (kits > MaxKits)
        {
            RLog.Warning($"[MasonLine] bundle marker claims {kits} bundles for slot {slot}; capped at {MaxKits}");
            kits = MaxKits;
        }
        return true;
    }

    /// <summary>Is a bundle available to start a new line? With no registered item there is nothing to
    /// spend, so the answer is no rather than the old free-for-all.</summary>
    public static bool HasKit()
    {
        if (!Ready) return false;
        try
        {
            var inv = LocalPlayer.Inventory;
            return inv == null || inv.AmountOf(ItemId) > 0;
        }
        catch { return true; }
    }

    /// <summary>Line completed (stake B planted): take ONE bundle out of the inventory. Several bundles
    /// = several simultaneous lines. False means nothing was taken and the line must not be built,
    /// or the player would keep the bundle AND get the line.</summary>
    public static bool ConsumeKit()
    {
        if (!Ready) return false;
        try
        {
            var inv = LocalPlayer.Inventory;
            if (inv == null) return false;
            if (inv.RemoveItem(ItemId, 1, false, true, true, null, true))
            {
                _kitsOut++;
                RLog.Msg(System.ConsoleColor.Yellow, $"[MasonLine] bundle staked out ({_kitsOut} in the field). Collect the line (hold Dismantle) to get it back");
                return true;
            }
            RLog.Warning("[MasonLine] the bundle could not be taken from the pack, so no line was set");
            return false;
        }
        catch (System.Exception ex)
        {
            RLog.Warning($"[MasonLine] the bundle could not be taken from the pack: {ex.Message}");
            return false;
        }
    }

    /// <summary>One line collected/cleared: put ONE bundle back. Gated by the out-counter, so a C on
    /// a lone pending stake (nothing consumed yet) refunds nothing.</summary>
    public static void RefundKit()
    {
        if (_kitsOut <= 0) return;
        try
        {
            var inv = LocalPlayer.Inventory;
            if (inv != null && inv.AddItem(ItemId))
            {
                _kitsOut--;
                FixRenderableLoadedFlags();   // re-add touches the layout item; make sure its renderable is safe
                RLog.Msg(System.ConsoleColor.Green, "[MasonLine] bundle returned to the inventory");
            }
            else RLog.Warning("[MasonLine] bundle refund failed (AddItem=false): count kept, the save-time marker will restore it");
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] bundle refund failed: {ex.Message}"); }
    }

    /// <summary>World load: lines never survive a reload (ResetWorld), so bundles stamped as staked-out
    /// in THIS save's marker go back to the inventory. The slot id gates cross-slot refunds; markers
    /// are KEPT after refunding (they belong to the save on disk — reloading the same save again
    /// without saving must refund again; the next save rewrites/deletes them).</summary>
    private static void RestoreKitsFromMarker()
    {
        _kitsOut = 0;
        try
        {
            MigrateMarkerFromSaveFolder();
            var lines = ReadMarkerLines();
            // Unreadable is not the same as empty: refunding nothing is right for an absent marker and
            // wrong for one we simply could not open, so say so instead of quietly skipping.
            if (lines == null)
            {
                RLog.Error("[MasonLine] the bundle marker could not be read; staked-out bundles are not " +
                           "refunded this load");
                return;
            }
            if (lines.Count == 0) return;
            var inv = LocalPlayer.Inventory;
            if (inv == null) return;

            // Legacy shapes only ever occupied a whole one-line file: a bare count (pre-slot-id
            // builds) or slot id 0 (the broken GetSelectedSaveId build). Retire on sight under the
            // old one-shot anti-dupe gate; a multi-line file is always the current format.
            if (lines.Count == 1)
            {
                var legacyParts = lines[0].Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                bool bareCount = legacyParts.Length < 2;
                bool zeroSlot = !bareCount && legacyParts[0] == "0";
                if (bareCount || zeroSlot)
                {
                    var countText = bareCount ? legacyParts[0] : legacyParts[1];
                    if (!int.TryParse(countText, out var legacy) || legacy < 1)
                    {
                        RLog.Warning($"[MasonLine] bundle marker '{lines[0]}' is not readable; leaving it in place");
                        return;
                    }
                    if (legacy > MaxKits) legacy = MaxKits;
                    if (inv.AmountOf(ItemId) > 0) return;   // already refunded once
                    int back = 0;
                    for (int i = 0; i < legacy; i++) if (inv.AddItem(ItemId)) back++;
                    // Only now: the file is the sole record, so it goes away once its bundles are in hand.
                    if (back == legacy) WriteMarkerLines(new System.Collections.Generic.List<string>());
                    else RLog.Warning($"[MasonLine] only {back} of {legacy} bundle(s) fit; the marker stays for the rest");
                    return;
                }
            }

            if (_loadedSaveId == 0) return;   // New Game: no slot owns these bundles
            int n = 0;
            foreach (var line in lines)
                if (ParseMarkerLine(line, out var id, out var kits) && id == _loadedSaveId) { n = kits; break; }
            if (n < 1) return;   // this slot has nothing staked out (other slots' lines stay untouched)
            int given = 0;
            for (int i = 0; i < n; i++) if (inv.AddItem(ItemId)) given++;
            // Whatever would not fit stays owed, so the next save records it again instead of
            // rewriting the slot's line to nothing.
            if (given < n)
            {
                _kitsOut = n - given;
                RLog.Warning($"[MasonLine] only {given} of {n} bundle(s) fit in the pack; the rest stay on the marker");
            }
            if (given > 0)
                RLog.Msg(System.ConsoleColor.Green, $"[MasonLine] {given} bundle(s) were staked out when this save was made, and they are back in your pack");
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] bundle marker restore failed: {ex.Message}"); }
    }
}
