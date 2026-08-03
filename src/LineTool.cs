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
/// The craftable "Mason Line" item: 2 sticks + rope at the crafting mat.
/// ItemData is a clone of GPSLocator (529) — proven equippable/craftable/droppable template with
/// RightHand slot + sane held anim vars. The held visual is a GPS held-prefab clone with the GPS
/// behaviours stripped and a stake+knot model in their place; the game instantiates it into the
/// held locator itself (SetupHeld doc: "HeldPrefab ... will be instantiated into the locator").
/// While the tool is equipped, MasonLineMod enables the ghost preview + point placement.
/// </summary>
internal static partial class LineTool
{
    /// <summary>Inventory id the bundle is registered under. Well above the vanilla range (max ~726),
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

    private static bool _idChangePending;

    /// <summary>The settings panel accepts a new item id the moment it is typed, but registration
    /// happened at startup, so the running session keeps the old one. Without this the player edits
    /// the id, sees nothing happen, and has no way to tell whether it took (observed: id showed 9424
    /// in the menu while the session ran on 9417).</summary>
    internal static void WarnIfIdChangePending()
    {
        bool pending = MasonLineConfig.ItemIdValue != ItemId;
        if (pending == _idChangePending) return;
        _idChangePending = pending;
        if (pending)
            RLog.Warning($"[MasonLine] item id {MasonLineConfig.ItemIdValue} takes effect after a game " +
                         $"restart; this session still runs on {ItemId}. Bundles in the pack are saved under " +
                         $"{ItemId} and will not carry over, but lines staked out now will come back.");
        else
            RLog.Msg($"[MasonLine] item id is back to {ItemId}, the one this session runs on");
    }

    private const int TemplateItemId = 529;         // GPSLocator
    private const int StickId = 392;
    private const int RopeId = 403;

    /// <summary>True once the item pipeline initialized for this world. Until then the tool is
    /// inert: no item means no stakes.</summary>
    public static bool Ready;

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
            // Live-tuned (~/tools/sotf-tune.sh) to sit in the tools/spears zone: mat plane = X,Z ; up = Y.
            builder.AddInventoryItem(new Vector3(1.2f, 0.015f, 0.9f));
            builder.AddCraftingResultItem();
            builder.SetupHeld();
            EnsureRecipe();

            Ready = true;
            RestoreKitsFromMarker();
            RLog.Msg(System.ConsoleColor.Cyan,
                "[MasonLine] Mason Line ready, craft: 2 sticks + 1 rope, equip it to place the line");
        }
        catch (System.Exception ex)
        {
            Ready = false;
            RLog.Error($"[MasonLine] item setup failed, the tool stays inert this world: {ex}");
        }
    }

    /// <summary>SdkEvents.OnWorldExited (quit to menu): lines are NOT in the save and must not leak
    /// into the next world — DDoL carried them across reloads => user-repro'd bundle dupe.</summary>
    public static void OnWorldExited()
    {
        GuideLine.ResetWorld();
        _kitsOut = 0;
        // New Game does not go through SaveGameManager.Load, so a stale id from the slot we just quit
        // would make THAT slot's marker match and hand out a free bundle in the fresh world (and, since
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
    /// move ours. Staying quiet here is what turns a collision into "my bundle does nothing".</summary>
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
                   "register its bundle, to avoid corrupting that item. Set overrideItemId = true and " +
                   "itemId to another number in UserData/MasonLine.cfg, then restart the game.");
    }

    /// <summary>Register the ItemData BEFORE the save deserializes, or a stored bundle is lost.
    /// Inventory deserialization runs during world load, and registration used to happen later, at
    /// OnAfterSpawn — an ItemId the database does not know at deserialize time
    /// is silently DROPPED from the loaded inventory (save file had ItemBlock 9417, in-game
    /// inventory came up without it, zero bundle log lines). In-process world reloads were immune
    /// (ItemData already registered from the first spawn), only a COLD game start lost the bundle.
    /// Called from OnSdkInitialized (title) and from the SaveGameManager.Load prefix (last line of
    /// defence, fires right before deserialize). Spawn-time Setup stays as the fallback.</summary>
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
                $"[MasonLine] item {ItemId} registered early ({context}): saved bundles survive a cold start");
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
            // two sticks, one rope: the bundle is literally two stakes and a cord, and a single
            // stick + rope is the most crowded pair on the mat for other mods to claim
            .AddIngredient(StickId, 2)
            .AddIngredient(RopeId, 1)
            .AddResult(ItemId)
            // The bow assembly animation instead of the builder's default herb-mix mashing. Each
            // ingredient plays this state on its own mat renderable, so it fits what actually lies
            // there: vanilla's bow is stick + rope (+ tape), and both have authored bow moves —
            // stick bent, cord strung. Club was tried first and only the rope moved: the club
            // recipe knows a single stick, and our stack of two lays out differently.
            .Animation(ItemTools.CraftAnimations.CraftedBow)
            .BuildAndAdd();
    }
}
