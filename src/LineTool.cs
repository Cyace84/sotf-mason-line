using RedLoader;
using Sons.Crafting;
using Sons.Inventory;
using Sons.Items.Core;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace MasonLine;

/// <summary>
/// The craftable "Mason Line" item: stick + rope at the crafting mat.
/// ItemData is a clone of GPSLocator (529) — proven equippable/craftable/droppable template with
/// RightHand slot + sane held anim vars. The held visual is a GPS held-prefab clone with the GPS
/// behaviours stripped and a stake+knot model in their place; the game instantiates it into the
/// held locator itself (SetupHeld doc: "HeldPrefab ... will be instantiated into the locator").
/// While the tool is equipped, MasonLineMod enables the ghost preview + point placement.
/// </summary>
internal static class LineTool
{
    /// <summary>Inventory id the kit is registered under. Well above the vanilla range (max ~726),
    /// but nothing stops another mod from picking the same number, so the player can move ours. Read
    /// once at startup: changing it mid-session would strand everything already counted under the
    /// old id.</summary>
    public static int ItemId { get; private set; } = MasonLineConfig.DefaultItemId;

    /// <summary>ItemData name we stamp on our own item, and the way we tell it apart from a foreign
    /// item that happens to sit on the same id.</summary>
    internal const string ItemName = "MasonLine";

    internal static void ApplyConfig()
    {
        ItemId = MasonLineConfig.ItemIdValue;
        if (MasonLineConfig.ItemIdRejected)
            RLog.Warning($"[MasonLine] custom item id '{MasonLineConfig.ItemId?.Value}' is not a " +
                         $"whole number above {MasonLineConfig.MinItemId}; using {ItemId} instead");
    }

    private const int TemplateItemId = 529;         // GPSLocator
    private const int StickId = 392;
    private const int RopeId = 403;

    /// <summary>True once the item pipeline initialized for this world. Until then the tool is
    /// inert: no item means no stakes.</summary>
    public static bool Ready;

    // Inventory hover: the game DOES detect hover on our item (it sets InventoryLayoutItem.IsHighlighted
    // and plays the wobble), but its InitMeshOutliner never collects our MeshOutliners into the layout
    // item's list, so the vanilla outline never lights. We drive the outline ourselves from IsHighlighted.
    private static Sons.Inventory.InventoryLayoutItem? _invItem;
    private static Sons.Inventory.InventoryLayoutItemGroup? _invGroup;
    private static readonly System.Collections.Generic.List<MeshOutliner> _invOutliners = new();

    // Hover-wobble pose, user-tuned (tune.sh: `wob 0 0 90  90 10 0` + `wobpivot -0.37`).
    // The wobble clip is shared by all items and tilts around the group origin; the frame roll (Z=90)
    // aims the tilt, the StakeVisual offset moves the stick OFF the pivot so one end lifts axe-style
    // instead of see-sawing. Values are the SETTLED LOCALS read live with the backpack open
    // (eval bake_1784156854) — never bake world-space poses computed at load time (lesson: twisted
    // stick + I-key crash on). Applied ONCE per backpack opening, ~0.75 s in, so the
    // layout animation has finished and we never fight it per-frame.
    private static readonly Vector3 InvGroupPos = new(1.20f, 0.02f, 0.45f);   // hand-tuned: the anchor-derived (1.0868,0.0126,0.6592) sat ~7 cm left on a virgin session; eyes > anchors
    private static readonly Vector3 InvGroupEuler = new(0f, 0f, 90f);
    private static readonly Vector3 InvStakePos = new(-0.0452f, -0.0010f, 0.3672f);
    private static readonly Vector3 InvStakeEuler = new(0f, 262.98f, 269.85f);
    private const int PoseApplyFrame = 45;   // ~0.75 s at 60 fps
    private static int _poseFrames = -1;     // frames since the group went active; -1 = inactive

    /// <summary>Is the crafted tool currently equipped? Detection via
    /// InventoryProps._gameObjectInstances (Dictionary&lt;itemId, held GameObject&gt;) — the game
    /// adds the held instance on equip (TryCreateHeldInstance) and removes it on unequip.</summary>
    public static bool IsHeld
    {
        get
        {
            // No item, no tool. This used to answer "held" whenever setup had thrown, which turned a
            // broken setup into every left-click planting a stake.
            if (!Ready) return false;
            try
            {
                var props = LocalPlayer.Inventory?.InventoryProps;
                var dict = props?._gameObjectInstances;
                if (dict == null || !dict.ContainsKey(ItemId)) return false;
                var go = dict[ItemId];
                return go != null && go.activeInHierarchy;
            }
            catch { return false; }
        }
    }

    /// <summary>Runs on SdkEvents.OnAfterSpawn (LocalPlayer alive, item DB + crafting system up).</summary>
    public static void Setup()
    {
        try
        {
            GuideLine.ResetWorld();   // belt-and-braces: OnWorldExited may not fire on every load path
            RegisterItemOnce();

            // Registration refuses to overwrite a foreign item on our id, and ItemById would happily
            // hand us that foreign item to dress up as ours. Stop here instead: no item, no tool.
            if (!OwnsRegisteredItem())
            {
                ReportItemIdConflict();
                return;
            }

            var data = ItemDatabaseManager.ItemById(ItemId);
            if (data == null) { RLog.Error("[MasonLine] item data missing after registration"); return; }

            // Per-world work: the inventory/crafting UI + player props are scene objects.
            var builder = new ItemTools.ItemBuilder(BuildModelPrefab("MasonLineInvModel"), data);

            // SDK AddInventoryItem() with NO position clones the DevilsClub(449) group and never sets
            // localPosition -> our group lands ON TOP of DevilsClub in the herbs area, buried/invisible
            // (decompile SonsSdk ItemBuilder.AddInventoryItem: positions[0] = group localPosition).
            // Live-tuned (tune.sh) to sit in the tools/spears zone: mat plane = X,Z ; up = Y.
            builder.AddInventoryItem(new Vector3(1.2f, 0.015f, 0.9f));
            builder.AddCraftingResultItem();
            builder.SetupHeld();
            EnsureRecipe();

            Ready = true;
            RestoreKitsFromMarker();
            RLog.Msg(System.ConsoleColor.Cyan,
                "[MasonLine] Mason Line ready, craft: 1 stick + 1 rope, equip it to place the line");
        }
        catch (System.Exception ex)
        {
            Ready = false;
            RLog.Error($"[MasonLine] item setup failed, the tool stays inert this world: {ex}");
        }
    }

    // ---- kit economy (user-approved design): one craft = one kit = one active line,
    // unlimited length, NO ingredient burn. Physicality: while the line stands in the world the kit
    // is OUT of the inventory (RemoveItem instantDestroy:true — the same flag the game itself uses
    // for placement-consume, memento-mori flag telemetry); collecting the line (hold Dismantle) returns it.
    // A marker file survives a game restart so a saved-with-line-out inventory gets the kit back
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

    /// <summary>The marker used to live in the save folder. Move it once, so a kit staked out before
    /// this update is still refunded instead of silently lost.</summary>
    private static void MigrateMarkerFromSaveFolder()
    {
        try
        {
            var old = System.IO.Path.Combine(Application.persistentDataPath, "MasonLine.line-out");
            if (!System.IO.File.Exists(old) || System.IO.File.Exists(KitMarkerPath)) return;
            System.IO.File.Copy(old, KitMarkerPath);
            System.IO.File.Delete(old);
            RLog.Msg($"[MasonLine] moved the kit marker to {KitMarkerPath}");
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] kit marker move failed: {ex.Message}"); }
    }

    /// <summary>SdkEvents.OnWorldExited (quit to menu): lines are NOT in the save and must not leak
    /// into the next world — DDoL carried them across reloads => user-repro'd kit dupe.</summary>
    public static void OnWorldExited()
    {
        GuideLine.ResetWorld();
        _kitsOut = 0;
        // New Game does not go through SaveGameManager.Load, so a stale id from the slot we just quit
        // would make THAT slot's marker match and hand out a free kit in the fresh world (and, since
        // markers are kept, again in the original slot = dupe).
        _loadedSaveId = 0;
        // per-world pipeline state: without this, menu-time Ticks kept scanning dead scene objects
        // (Ready stayed true) and the doc on Ready ("for this world") was a lie
        Ready = false;
        _invItem = null;
        _invGroup = null;
        _invOutliners.Clear();
        _craftRoot = null;
        _poseFrames = -1;
        // Unity recycles instance ids, so a stale set would make us skip sanitizing a NEW renderable
        // that happens to reuse a dead id.
        _sanitizedRenderables.Clear();
    }

    // Save-slot id source: GameSetupManager.GetSelectedSaveId() is only valid in the LOAD MENU —
    // during an in-game save it returns 0 (observed: marker "0 1", refund missed, kit lost — user
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
    /// kits currently staked out. Written at save time, the marker matches the save's inventory
    /// EXACTLY — so the load-time refund is unconditional, no AmountOf heuristics.</summary>
    internal static void OnBeforeSave(string dir)
    {
        try
        {
            uint id = ParseSaveIdFromDir(dir);
            // Rewrite OUR slot's line only. A single global line meant saving slot B overwrote slot
            // A's pending refund, so loading A found no marker and the staked-out kit was gone for
            // good. The delete branch already knew this and compared
            // ids; the write branch did not.
            var lines = ReadMarkerLines();
            if (lines == null)
            {
                RLog.Error($"[MasonLine] the kit marker is unreadable, so {_kitsOut} staked-out kit(s) " +
                           "cannot be recorded for this save; collect your lines before quitting");
                return;
            }
            lines.RemoveAll(l => SlotOfMarkerLine(l) == id);
            if (_kitsOut > 0) lines.Add($"{id} {_kitsOut}");
            if (!WriteMarkerLines(lines) && _kitsOut > 0)
                RLog.Error($"[MasonLine] {_kitsOut} staked-out kit(s) could not be recorded; " +
                           "collect your lines before quitting or they are lost");
        }
        catch (System.Exception ex) { RLog.Error($"[MasonLine] saving the kit marker failed: {ex}"); }
    }

    /// <summary>Marker file body: one "&lt;slotId&gt; &lt;kits&gt;" line per save slot that has kits in the
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
    /// marker: the old one survives intact instead. False means the kits are NOT recorded.</summary>
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

    /// <summary>The one parser for "&lt;slotId&gt; &lt;kits&gt;". Splits on any run of whitespace, demands
    /// exactly two fields, and caps the count at a stack of kits so a hand-edited file cannot spin
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
            RLog.Warning($"[MasonLine] kit marker claims {kits} kits for slot {slot}; capped at {MaxKits}");
            kits = MaxKits;
        }
        return true;
    }

    /// <summary>Is a kit available to start a new line? With no registered item there is nothing to
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

    /// <summary>Line completed (stake B planted): take ONE kit out of the inventory. Several kits
    /// = several simultaneous lines. False means nothing was taken and the line must not be built,
    /// or the player would keep the kit AND get the line.</summary>
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
                RLog.Msg(System.ConsoleColor.Yellow, $"[MasonLine] kit staked out ({_kitsOut} in the field). Collect the line (hold Dismantle) to get it back");
                return true;
            }
            RLog.Warning("[MasonLine] the kit could not be taken from the pack, so no line was set");
            return false;
        }
        catch (System.Exception ex)
        {
            RLog.Warning($"[MasonLine] the kit could not be taken from the pack: {ex.Message}");
            return false;
        }
    }

    /// <summary>One line collected/cleared: put ONE kit back. Gated by the out-counter, so a C on
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
                RLog.Msg(System.ConsoleColor.Green, "[MasonLine] kit returned to the inventory");
            }
            else RLog.Warning("[MasonLine] kit refund failed (AddItem=false): count kept, the save-time marker will restore it");
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] kit refund failed: {ex.Message}"); }
    }

    /// <summary>World load: lines never survive a reload (ResetWorld), so kits stamped as staked-out
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
                RLog.Error("[MasonLine] the kit marker could not be read; staked-out kits are not " +
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
                        RLog.Warning($"[MasonLine] kit marker '{lines[0]}' is not readable; leaving it in place");
                        return;
                    }
                    if (legacy > MaxKits) legacy = MaxKits;
                    if (inv.AmountOf(ItemId) > 0) return;   // already refunded once
                    int back = 0;
                    for (int i = 0; i < legacy; i++) if (inv.AddItem(ItemId)) back++;
                    // Only now: the file is the sole record, so it goes away once its kits are in hand.
                    if (back == legacy) WriteMarkerLines(new System.Collections.Generic.List<string>());
                    else RLog.Warning($"[MasonLine] only {back} of {legacy} kit(s) fit; the marker stays for the rest");
                    return;
                }
            }

            if (_loadedSaveId == 0) return;   // New Game: no slot owns these kits
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
                RLog.Warning($"[MasonLine] only {given} of {n} kit(s) fit in the pack; the rest stay on the marker");
            }
            if (given > 0)
                RLog.Msg(System.ConsoleColor.Green, $"[MasonLine] {given} kit(s) were staked out when this save was made, and they are back in your pack");
        }
        catch (System.Exception ex) { RLog.Warning($"[MasonLine] kit marker restore failed: {ex.Message}"); }
    }

    /// <summary>Register the ItemData BEFORE the save deserializes, or a stored kit is lost.
    /// Inventory deserialization runs during world load, and registration used to happen later, at
    /// OnAfterSpawn — an ItemId the database does not know at deserialize time
    /// is silently DROPPED from the loaded inventory (save file had ItemBlock 9417, in-game
    /// inventory came up without it, zero kit log lines). In-process world reloads were immune
    /// (ItemData already registered from the first spawn), only a COLD game start lost the kit.
    /// Called from OnSdkInitialized (title) and from the SaveGameManager.Load prefix (last line of
    /// defence, fires right before deserialize). Spawn-time Setup stays as the fallback.</summary>
    /// <summary>Is the item currently sitting on our id actually ours? Compared by the name we stamp
    /// on it, since the id alone proves nothing.</summary>
    private static bool OwnsRegisteredItem()
    {
        try
        {
            var data = ItemDatabaseManager.ItemById(ItemId);
            return data != null && data._name == ItemName;
        }
        catch { return false; }
    }

    private static bool _conflictReported;

    /// <summary>Another mod owns our item id. Say so once, loudly, with the fix: the mod menu can
    /// move ours. Staying quiet here is what turns a collision into "my kit does nothing".</summary>
    private static void ReportItemIdConflict()
    {
        if (_conflictReported) return;
        _conflictReported = true;
        string other = "another mod";
        try
        {
            var data = ItemDatabaseManager.ItemById(ItemId);
            if (data != null && !string.IsNullOrEmpty(data._name)) other = $"'{data._name}'";
        }
        catch { }
        RLog.Error($"[MasonLine] item id {ItemId} is already taken by {other}. Mason Line will not " +
                   "register its kit, to avoid corrupting that item. Pick a different Item id in the " +
                   "Mason Line settings and restart the game.");
    }

    internal static void TryRegisterEarly(string context)
    {
        try
        {
            // IsItemRegistered only answers "an item with this id exists", not "it is ours". If
            // another mod got there first we must NOT carry on: every AmountOf/AddItem/RemoveItem
            // below would silently operate on THEIR item.
            if (ItemTools.IsItemRegistered(ItemId))
            {
                if (!OwnsRegisteredItem()) ReportItemIdConflict();
                return;
            }
            if (ItemDatabaseManager._itemsCache == null || ItemDatabaseManager._instance == null)
            {
                RLog.Msg($"[MasonLine] item DB not ready at {context}, deferring registration");
                return;
            }
            if (!ItemDatabaseManager.TryFindItemById(TemplateItemId, out var tpl) || tpl == null)
            {
                RLog.Msg($"[MasonLine] template item {TemplateItemId} not in DB at {context}; registration deferred");
                return;
            }
            RegisterItemOnce();
            RLog.Msg(System.ConsoleColor.Cyan,
                $"[MasonLine] item {ItemId} registered early ({context}): saved kits survive a cold start");
        }
        catch (System.Exception ex)
        {
            RLog.Warning($"[MasonLine] early item registration failed at {context}: {ex.Message}. Spawn-time fallback stays");
        }
    }

    /// <summary>App-lifetime work: ItemData + held template survive world reloads (DontDestroyOnLoad).</summary>
    private static void RegisterItemOnce()
    {
        if (ItemTools.IsItemRegistered(ItemId))
        {
            if (!OwnsRegisteredItem()) ReportItemIdConflict();
            return;
        }

        var tpl = ItemDatabaseManager.ItemById(TemplateItemId);
        var data = Object.Instantiate(tpl);
        data.name = "MasonLineItemData";
        data._id = ItemId;
        data._name = ItemName;
        data._editorName = ItemName;
        // maxAmount MUST be >1: at 1, a second craft overflows and DROPS the item as a world
        // pickup. The dropped pickup carries ObjectPhysicsInteractionSfx; when it rests on the
        // non-convex BackpackGroundMesh hull the game's TryTriggerHardSurfaceImpact -> ClosestPoint
        // throws a managed exception inside the native physics callback -> NATIVE CRASH under
        // IL2CPP/Wine (crash, Player.log ReportContacts/ClosestPoint spam). Raising this
        // makes the overflow-drop far rarer; BuildPickupTemplate also strips the crash component.
        data._maxAmount = 20;
        data._uiData._itemId = ItemId;
        data._uiData._title = "Mason Line";
        data._uiData._translationKey = null;
        data._uiData._description =
            "Plant two stakes and stretch a string line between them. Free log placement snaps to the line.";
        var (icon, outline) = ItemTools.GetIcon(RopeId);   // rope icon reads as "string line" well enough
        if (icon != null) data._uiData._icon = icon;
        if (outline != null) data._uiData._outlineIcon = outline;

        data._heldPrefab = BuildHeldTemplate(tpl._heldPrefab);
        // Replace the GPS pickup with a crash-safe clone: the GPS pickup carries
        // ObjectPhysicsInteractionSfx, which native-crashes the game when the dropped item touches
        // the non-convex backpack mesh (see _maxAmount note + BuildPickupTemplate).
        var safePickup = BuildPickupTemplate(tpl._pickupPrefab);
        if (safePickup != null) data._pickupPrefab = safePickup;

        ItemTools.RegisterItem(data);
        // RegisterItem seeds the active locale's table with the English title; overwrite that and
        // fill in the other languages.
        MasonLineStrings.Apply(ItemId);
    }

    /// <summary>Clone the GPS held prefab (inactive-clone trick so no GPS Awake runs), strip the
    /// GPS behaviours, retarget HeldItemIdentifier, swap the visuals for a stake + rope knot.</summary>
    private static Transform BuildHeldTemplate(Transform gpsHeld)
    {
        var src = gpsHeld.gameObject;
        bool wasActive = src.activeSelf;
        // Cloned inactive so the copy never runs Awake/OnEnable. The finally matters: a throw in
        // Instantiate would otherwise leave the game's own GPS template switched off for good.
        GameObject clone;
        src.SetActive(false);
        try { clone = Object.Instantiate(src); }
        finally { src.SetActive(wasActive); }

        clone.name = "MasonLineHeld";
        Object.DontDestroyOnLoad(clone);

        foreach (var c in clone.GetComponents<Component>())
        {
            var tn = c.GetIl2CppType().Name;
            if (tn.IndexOf("gps", System.StringComparison.OrdinalIgnoreCase) >= 0)
                Object.DestroyImmediate(c);         // GPSLocator + GpsLocatorController
        }
        var hid = clone.GetComponent<HeldItemIdentifier>();
        if (hid != null) hid._itemId = ItemId;

        for (int i = clone.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(clone.transform.GetChild(i).gameObject);
        AddStakeVisual(clone.transform);
        StripImpactSfx(clone);   // defensive: no crash component rides along from the GPS clone

        clone.transform.position = new Vector3(0f, -2000f, 0f);   // parked out of sight
        clone.SetActive(true);   // template must be active: instantiated copies inherit the state
        return clone.transform;
    }

    /// <summary>Crash-safe pickup: inactive-clone the GPS pickup (so no GPS Awake runs), then strip
    /// ObjectPhysicsInteractionSfx. That component's OnCollisionEnter/OnTriggerEnter ->
    /// TryTriggerHardSurfaceImpact -> Physics.ClosestPoint throws on a non-convex MeshCollider
    /// (BackpackGroundMesh partial hull); the managed throw crosses the native physics callback and
    /// native-crashes the game under IL2CPP/Wine. Decompile: Sons.Gameplay.ObjectPhysicsInteractionSfx.
    /// Verified crash trace: Player.log OnSceneContact -> ReportContacts -> TryTriggerHardSurfaceImpact
    /// -> "Physics.ClosestPoint can only be used with... convex MeshCollider".</summary>
    private static Transform? BuildPickupTemplate(Transform? gpsPickup)
    {
        if (gpsPickup == null) return null;
        var src = gpsPickup.gameObject;
        bool wasActive = src.activeSelf;
        // Cloned inactive so the copy never runs Awake/OnEnable. The finally matters: a throw in
        // Instantiate would otherwise leave the game's own GPS template switched off for good.
        GameObject clone;
        src.SetActive(false);
        try { clone = Object.Instantiate(src); }
        finally { src.SetActive(wasActive); }

        clone.name = "MasonLinePickup";
        Object.DontDestroyOnLoad(clone);
        StripImpactSfx(clone);

        clone.transform.position = new Vector3(0f, -2000f, 0f);   // parked template
        return clone.transform;
    }

    /// <summary>DestroyImmediate every ObjectPhysicsInteractionSfx in the object tree (name-match,
    /// no hard type ref). This is the component whose ClosestPoint call native-crashes on the
    /// non-convex backpack mesh — our GPS-derived clones must not carry it.</summary>
    private static void StripImpactSfx(GameObject root)
    {
        if (root == null) return;
        var comps = root.GetComponentsInChildren(Il2CppInterop.Runtime.Il2CppType.Of<Component>(), true);
        foreach (var c in comps)
        {
            if (c == null) continue;
            if (c.GetIl2CppType().Name == "ObjectPhysicsInteractionSfx")
                Object.DestroyImmediate(c);
        }
    }

    /// <summary>Model prefab for the inventory mat / crafting result display (active, parked).
    /// keepTriggerCollider=true: the game's own LayoutItem.RefreshInteractionComponents (decompile
    /// 0x1831E8DA0) wires Inventory layer + MouseEventsProxy onto every renderer GO itself, but it
    /// expects a COLLIDER to already exist on the renderable for hover/click raycasts. A TRIGGER
    /// collider is mandatory — a plain one physically contacts BackpackGroundMesh and the contact-
    /// SFX spam crashes the game (crash).</summary>
    private static GameObject BuildModelPrefab(string name)
    {
        var root = new GameObject(name);
        Object.DontDestroyOnLoad(root);
        AddStakeVisual(root.transform, keepTriggerCollider: true, matDisplay: true);
        root.transform.position = new Vector3(0f, -2000f, 0f);
        return root;
    }

    // Mat-display orientation/scale (live-tunable via the StakeVisual wrapper). The inventory mat is
    // viewed near-top-down, so an upright stake reads as a tiny end-on dot. Lay it along the mat with
    // a slight tilt and enlarge it so it's findable among full-size items. Held-in-hand keeps upright
    // (matDisplay=false), which the user already approved.
    // Lay the stake FLAT in the mat plane (horizontal). At identity the cylinder's long axis (local Y)
    // points along the mat normal -> end-on top-down (invisible dot); 90° about X drops it into the
    // plane so it reads as a stake and doesn't dip its base into the mat/other items. Live-tunable.
    // Live-tuned (tune.sh) so the INVENTORY browse model reads world-euler ~(90,10,0) — stake lying
    // flat on the mat, long axis "up" the screen like the spears/tools. This local euler is relative
    // to the DevilsClub-derived layout-item frame; the crafting-result popup (different parent frame)
    // inherits the same local euler and will differ — tune that separately if it bothers.
    // Live-tuned (tune.sh) + user-approved: StakeVisual world euler (90,10,0) resolves to this LOCAL
    // euler under the DevilsClub-derived layout-item frame. Branch top reads "up the mat", not sideways.
    private static readonly Vector3 MatDisplayEuler = new Vector3(83.7f, 234.2f, 0.6f);
    private const float MatDisplayScale = 1.4f;   // layout item already scales up ~1.7x; keep this modest

    /// <summary>Small stake (wood cylinder) with a rope knot at the top — the tool's look.
    /// matDisplay=true nests the visual under a wrapper laid flat + enlarged for the inventory mat.</summary>
    private static void AddStakeVisual(Transform parent, bool keepTriggerCollider = false, bool matDisplay = false)
    {
        Transform host = parent;
        if (matDisplay)
        {
            var wrapper = new GameObject("StakeVisual");
            wrapper.transform.SetParent(parent, false);
            wrapper.transform.localRotation = Quaternion.Euler(MatDisplayEuler);
            wrapper.transform.localScale = Vector3.one * MatDisplayScale;
            host = wrapper.transform;
        }

        // For the inventory/crafting mat display use the SAME planted-stick branch as the world stakes
        // (BranchABMeshLOD0 + BranchA/BloodReveal). It reads correctly ONLY once the renderable is on the
        // Inventory layer (23) — done post-spawn in TryWireInventoryHover; InventoryCamera cullingMask
        // excludes layer 0, which is why any mesh here looked "invisible" before. Held-in-hand keeps the
        // procedural cylinder (its orientation was tuned separately).
        var branchMesh = matDisplay ? GuideLine.StakeMesh() : null;
        var branchMat = matDisplay ? GuideLine.StakeMat() : null;
        if (branchMesh != null)
        {
            var stake = new GameObject("Stake");
            stake.transform.SetParent(host, false);
            stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
            stake.GetComponent<MeshFilter>().mesh = branchMesh;
            var brend = stake.GetComponent<Renderer>();
            if (branchMat != null) brend.sharedMaterial = branchMat;
            stake.transform.localScale = Vector3.one;                       // real mesh = real size; do NOT shrink
            stake.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);  // branch long axis (Z) -> "up" the mat
            if (keepTriggerCollider)
            {
                // thin capsule hugging the branch. A primitive's default radius 0.5/height 2 would blow up
                // into a ~1m dead-zone that blocks selecting neighbouring items. It stays SOLID rather
                // than a trigger; the reason is at the isTrigger assignment below.
                var cap = stake.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<CapsuleCollider>()).TryCast<CapsuleCollider>();
                if (cap != null)
                {
                    cap.direction = 2;     // along the branch (local Z)
                    cap.radius = 0.04f;
                    cap.height = 0.75f;
                    cap.center = Vector3.zero;
                    // NOT a trigger: the inventory hover raycaster (CameraMouseEvents.ManagedUpdate ->
                    // SendMessage("OnMouseEnterCollider")) does not fire on trigger colliders — vanilla items
                    // (StunGun etc.) all use solid colliders on layer 23. No rigidbody here, so no contact
                    // SFX events; the ClosestPoint crash class is covered by the CrashGuardPatch finalizer.
                    cap.isTrigger = false;
                }
            }
        }
        else
        {
            var stake = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stake.name = "Stake";
            var col = stake.GetComponent<Collider>();
            if (col != null)
            {
                if (keepTriggerCollider) col.isTrigger = true;
                else Object.DestroyImmediate(col);
            }
            stake.transform.SetParent(host, false);
            stake.transform.localScale = new Vector3(0.045f, 0.22f, 0.045f);   // 44cm long stake
            stake.transform.localPosition = Vector3.zero;
            var wood = GuideLine.WoodMat();
            var sr = stake.GetComponent<Renderer>();
            if (wood != null && sr != null) sr.sharedMaterial = wood;
        }

        var knotMesh = GuideLine.KnotMesh();
        var ropeMat = GuideLine.RopeMaterial();
        if (knotMesh != null)
        {
            var knot = new GameObject("Knot");
            knot.transform.SetParent(host, false);
            knot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>());
            knot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshRenderer>());
            knot.GetComponent<MeshFilter>().mesh = knotMesh;
            if (ropeMat != null) knot.GetComponent<Renderer>().sharedMaterial = ropeMat;
            knot.transform.localScale = Vector3.one * 0.5f;
            knot.transform.localPosition = new Vector3(0f, 0.16f, 0f);
        }
    }

    /// <summary>Per-frame (from MasonLineMod.Tick): outline = IsHighlighted, giving the vanilla axe-style
    /// hover highlight. Lazily wires the visual the first time the inventory is opened, because the SDK's
    /// CustomItemRenderable doesn't instantiate our model until the (inactive) inventory group first
    /// activates. Sets the Inventory layer (23) so InventoryCamera can see the branch at all.</summary>
    public static void UpdateInventoryHover()
    {
        if (!Ready) return;
        try
        {
            UpdateCraftPose();   // independent of the backpack wiring: fires on the crafting-mat popup too
            if (_invItem == null || _invOutliners.Count == 0) { TryWireInventoryHover(); return; }
            if (_invGroup == null || !_invGroup.gameObject.activeInHierarchy) _poseFrames = -1;
            else if (_poseFrames < PoseApplyFrame && ++_poseFrames == PoseApplyFrame) ApplyWobblePose();
            bool hl = _invItem.IsHighlighted;
            foreach (var mo in _invOutliners) if (mo != null) mo.Enable(hl);
        }
        catch { _invItem = null; _invGroup = null; _invOutliners.Clear(); _craftRoot = null; }   // world reload dropped the group -> re-wire
    }

    // Crafting-result pose: the SAME shared model prefab is instantiated under the crafting-mat result
    // frame (…/CraftingSystem/CraftingResultLayoutGroups/MasonLineLayoutGroup/HealthMixCrafting-
    // ResultLayoutItem/MasonLineInvModel(Clone)) — a different parent chain than the backpack layout
    // item, so the backpack-approved local euler reads crooked there. Live-tuned
    // (tune.sh mat 90 0 85, user-approved "вот это четкое"): world (90,0,85) resolves to this LOCAL
    // under the craft frame. Scale stays 1.4 (prefab). Set-once per clone is enough: matshow showed the
    // baked local UNTOUCHED on a live craft popup — nothing animates StakeVisual locals.
    private static readonly Vector3 CraftDisplayEuler = new Vector3(90f, 269.9f, 0f);
    private static Transform? _craftRoot;   // CraftingResultLayoutGroups; cached until world reload
    private static int _craftPosedId;       // instanceID of the clone already posed (each craft spawns a new one)
    private static int _craftScanFrame;

    private static void UpdateCraftPose()
    {
        if (++_craftScanFrame < 30) return;   // 1/30-frame duty cycle: Find + subtree scan are not free
        _craftScanFrame = 0;
        if (_craftRoot == null)
        {
            var go = GameObject.Find("CraftingResultLayoutGroups");   // findable only while active (mat UI open)
            if (go == null) return;
            _craftRoot = go.transform;
        }
        foreach (var t in _craftRoot!.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "StakeVisual") continue;
            int id = t.GetInstanceID();
            if (id == _craftPosedId) return;
            t.localEulerAngles = CraftDisplayEuler;
            _craftPosedId = id;
            // a new clone appeared => a new CustomItemRenderable may exist un-fixed; flag+sanitize it
            // BEFORE its next SetItemInstance (poison) or OnEnable (detonation). crash window.
            FixRenderableLoadedFlags();
            Dbg.Msg(System.ConsoleColor.Cyan, $"[MasonLine] craft-mat pose applied (SV {id})");
            return;
        }
    }

    private static void TryWireInventoryHover()
    {
        var g = ItemTools.GetInventoryLayoutItemGroup(ItemId);
        if (g == null) return;
        var filters = g.GetComponentsInChildren<MeshFilter>(true);
        if (filters == null || filters.Length == 0) return;   // model not instantiated yet (inventory never opened)

        int invLayer = LayerMask.NameToLayer("Inventory");
        if (invLayer < 0) invLayer = 23;
        foreach (var t in g.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = invLayer;

        Sons.Inventory.InventoryLayoutItem? li = null;
        foreach (var t in g.GetComponentsInChildren<Transform>(true))
        {
            var c = t.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<Sons.Inventory.InventoryLayoutItem>());
            if (c != null) { li = c.TryCast<Sons.Inventory.InventoryLayoutItem>(); break; }
        }
        if (li == null) return;

        // THE key call (decompiled @0x1831E8DA0, live-proven): vanilla wiring for a replaced model.
        // For models without an AssetReferenceRenderableCollisionLink it walks the renderer nodes and
        // (a) sets the Inventory layer, (b) adds a MouseEventsProxy per node, (c) subscribes its
        // _mouseEnter/Exit/OverEvent to LayoutItem.OnMouseEnter/Exit/Over. Without it the group's
        // CameraMouseEvents SendMessage lands on a node with no receiver and IsHighlighted never fires.
        // Protected on LayoutItem -> reflection.
        typeof(Sons.Inventory.LayoutItem)
            .GetMethod("RefreshInteractionComponents",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)
            ?.Invoke(li, null);

        _invOutliners.Clear();
        foreach (var mf in filters)
        {
            var go = mf.gameObject;
            var rd = go.GetComponent<Renderer>();
            if (rd == null) continue;
            var existing = go.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshOutliner>());
            var mo = existing != null
                ? existing.TryCast<MeshOutliner>()
                : go.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<MeshOutliner>()).TryCast<MeshOutliner>();
            if (mo == null) continue;
            mo._renderer = rd;
            mo._thickness = 4f;
            mo._color = Color.white;
            mo.Initialise();          // builds the stencil outline material from Sons/Outline shader
            mo.Enable(false);
            _invOutliners.Add(mo);
        }
        _invItem = li;
        _invGroup = g;
        _poseFrames = -1;
        FixRenderableLoadedFlags();
        bool proxied = false;
        foreach (var mf in filters)
            if (mf.GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<Endnight.Utilities.MouseEventsProxy>()) != null) { proxied = true; break; }
        Dbg.Msg(System.ConsoleColor.Cyan,
            $"[MasonLine] inventory hover wired ({_invOutliners.Count} outliners, layer {invLayer}, proxy={proxied})");
    }

    /// <summary>Re-apply the tuned wobble pose (frame roll + pivot offset). Called once per backpack
    /// opening, after the layout animation settled; all values are locals, so no world-space timing traps.</summary>
    private static void ApplyWobblePose()
    {
        if (_invGroup == null) return;
        var gt = _invGroup.transform;
        gt.localPosition = InvGroupPos;
        gt.localEulerAngles = InvGroupEuler;
        foreach (var t in _invGroup.GetComponentsInChildren<Transform>(true))
            if (t.name == "StakeVisual")
            {
                t.localPosition = InvStakePos;
                t.localEulerAngles = InvStakeEuler;
                break;
            }
    }

    /// <summary>SDK hole (decompiled SonsSdk ItemTools.CustomItemRenderable + AssetReferenceRenderable
    /// .get_IsObjectLoaded @0x180B5FDD0): custom items never set _cachedLoadedObject, so IsObjectLoaded
    /// stays FALSE forever and LayoutItem.SetItemInstance (@0x1831E89A0) always takes the
    /// AddOnRenderableLoadedListener branch — which explodes with a RankException inside
    /// UnityEventBase.AddCall on managed-injected renderables. Result: AddItem of the crafted item
    /// fails, the fallback DropItem NREs in TransferOnRenderableLoadedCallbackFrom, the item VANISHES.
    /// Setting the field flips SetItemInstance onto the direct "already loaded" path.
    /// Why the event object is replaced rather than cleared: an AddCall that was interrupted
    /// mid-way leaves the renderable's _onRenderableLoaded call-list in a corrupt state. OnEnable
    /// fires on every inventory open, Invoke walks that list, and the process dies in native code.
    /// Clearing the event would walk the same broken array, so we swap in a fresh empty
    /// UnityEvent&lt;Transform&gt; through the interop field setter and never touch the old one.
    /// Nothing legitimate is lost: with IsObjectLoaded true the game never takes the listener
    /// branch, and the SDK does not subscribe either.</summary>
    private static readonly System.Collections.Generic.HashSet<int> _sanitizedRenderables = new();

    private static void FixRenderableLoadedFlags()
    {
        // SURGICAL (lesson: a broad Setup-time sweep marked 3 nodes "loaded" incl. template objects and
        // crashed the game on craft). Only the managed-injected CustomItemRenderable explodes on AddCall —
        // native ItemRenderables (GPS-clone mat/pickup nodes) never needed the fix (mat display worked
        // un-fixed on). So: exact component type + the INSTANTIATED model clone only.
        var arr = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Endnight.Rendering.AssetReferenceRenderable>());
        int patched = 0, sanitized = 0;
        foreach (var o in arr)
        {
            var ar = o.TryCast<Endnight.Rendering.AssetReferenceRenderable>();
            if (ar == null) continue;
            if (ar.GetIl2CppType().Name != "CustomItemRenderable") continue;
            bool ours = false;
            var p = ar.transform;
            for (int i = 0; p != null && i < 8; i++) { if (p.name.Contains("MasonLine")) { ours = true; break; } p = p.parent; }
            if (!ours) continue;
            var model = FindOurInvModel(ar);
            if (model == null) continue;
            if (!ar.IsObjectLoaded)
            {
                ar._cachedLoadedObject = model;
                patched++;
                Dbg.Msg(System.ConsoleColor.Cyan,
                    $"[MasonLine] renderable loaded-flag fixed: {ar.name} -> {model.name}");
            }
            if (SanitizeRenderable(ar)) sanitized++;
        }
        if (sanitized > 0)
            Dbg.Msg(System.ConsoleColor.Cyan, $"[MasonLine] renderable events sanitized: {sanitized}");
    }

    /// <summary>Replace a renderable's _onRenderableLoaded with a fresh event, once per instance.
    /// Called from the sweep above AND synchronously from <see cref="RenderablePatch"/> before every
    /// CustomItemRenderable.OnEnable — the sweep alone loses the race against clones the game creates
    /// lazily on the first inventory open (crash 00:48 despite 'sanitized: 1').</summary>
    internal static bool SanitizeRenderable(Endnight.Rendering.AssetReferenceRenderable ar)
    {
        if (!_sanitizedRenderables.Add(ar.GetInstanceID())) return false;
        ar.__onRenderableLoaded_k__BackingField = new UnityEngine.Events.UnityEvent<Transform>();
        return true;
    }

    /// <summary>The one place that knows how to locate our injected inventory model under a
    /// CustomItemRenderable (Unity's "(Clone)" suffix + our prefab name). Shared by the sweep and
    /// the crash-site guard so the brittle name-match lives in exactly one spot.</summary>
    internal const string InvModelCloneName = "MasonLineInvModel(Clone)";
    internal static GameObject? FindOurInvModel(Endnight.Rendering.AssetReferenceRenderable ar)
    {
        foreach (var t in ar.GetComponentsInChildren<Transform>(true))
            if (t.name == InvModelCloneName) return t.gameObject;
        return null;
    }

    /// <summary>Add the crafting recipe unless a previous world already registered it
    /// (the recipe database may be an asset that persists across world loads).</summary>
    private static void EnsureRecipe()
    {
        var recipes = GameState.CraftingSystem?._recipeDatabase?._recipes;
        if (recipes == null) { RLog.Warning("[MasonLine] no recipe database; recipe skipped"); return; }

        for (int i = 0; i < recipes.Count; i++)
        {
            var r = recipes[i];
            var results = r?._resultingItems;
            if (results != null && results.Count > 0 && results[0].Id == ItemId) return;   // already there
        }

        new ItemTools.RecipeBuilder()
            .AddIngredient(StickId, 1)
            .AddIngredient(RopeId, 1)
            .AddResult(ItemId)
            .BuildAndAdd();
    }
}
