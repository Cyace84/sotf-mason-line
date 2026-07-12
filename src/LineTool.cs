using RedLoader;
using Sons.Crafting;
using Sons.Inventory;
using Sons.Items.Core;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// The craftable "Builder's String Line" item: stick + rope at the crafting mat.
/// ItemData is a clone of GPSLocator (529) — proven equippable/craftable/droppable template with
/// RightHand slot + sane held anim vars. The held visual is a GPS held-prefab clone with the GPS
/// behaviours stripped and a stake+knot model in their place; the game instantiates it into the
/// held locator itself (SetupHeld doc: "HeldPrefab ... will be instantiated into the locator").
/// While the tool is equipped, LaserMod enables the ghost preview + point placement.
/// </summary>
internal static class LineTool
{
    public const int ItemId = 9417;                 // free range, far above vanilla (max ~726)
    private const int TemplateItemId = 529;         // GPSLocator
    private const int StickId = 392;
    private const int RopeId = 403;

    /// <summary>True once the item pipeline initialized for this world. While false the mod
    /// falls back to ungated hotkeys, so a broken item setup never bricks the core feature.</summary>
    public static bool Ready;

    /// <summary>Is the crafted tool currently equipped? Detection via
    /// InventoryProps._gameObjectInstances (Dictionary&lt;itemId, held GameObject&gt;) — the game
    /// adds the held instance on equip (TryCreateHeldInstance) and removes it on unequip.</summary>
    public static bool IsHeld
    {
        get
        {
            if (!Ready) return true;   // dev fallback: item pipeline down -> keep hotkeys usable
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
            RegisterItemOnce();

            var data = ItemDatabaseManager.ItemById(ItemId);
            if (data == null) { RLog.Error("[BuildingLaser] item data missing after registration"); return; }

            // Per-world work: the inventory/crafting UI + player props are scene objects.
            var builder = new ItemTools.ItemBuilder(BuildModelPrefab("BuildersLineInvModel"), data);
            builder.AddInventoryItem();
            builder.AddCraftingResultItem();
            builder.SetupHeld();
            EnsureRecipe();

            Ready = true;
            RLog.Msg(System.ConsoleColor.Cyan,
                "[BuildingLaser] Builder's Line ready — craft: 1 stick + 1 rope, equip it to place the line");
        }
        catch (System.Exception ex)
        {
            Ready = false;
            RLog.Error($"[BuildingLaser] item setup failed (hotkeys stay ungated): {ex}");
        }
    }

    /// <summary>App-lifetime work: ItemData + held template survive world reloads (DontDestroyOnLoad).</summary>
    private static void RegisterItemOnce()
    {
        if (ItemTools.IsItemRegistered(ItemId)) return;

        var tpl = ItemDatabaseManager.ItemById(TemplateItemId);
        var data = Object.Instantiate(tpl);
        data.name = "BuildersLineItemData";
        data._id = ItemId;
        data._name = "BuildersLine";
        data._editorName = "BuildersLine";
        // maxAmount MUST be >1: at 1, a second craft overflows and DROPS the item as a world
        // pickup. The dropped pickup carries ObjectPhysicsInteractionSfx; when it rests on the
        // non-convex BackpackGroundMesh hull the game's TryTriggerHardSurfaceImpact -> ClosestPoint
        // throws a managed exception inside the native physics callback -> NATIVE CRASH under
        // IL2CPP/Wine (crash 2026-07-13, Player.log ReportContacts/ClosestPoint spam). Raising this
        // makes the overflow-drop far rarer; BuildPickupTemplate also strips the crash component.
        data._maxAmount = 20;
        data._uiData._itemId = ItemId;
        data._uiData._title = "Builder's String Line";
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
    }

    /// <summary>Clone the GPS held prefab (inactive-clone trick so no GPS Awake runs), strip the
    /// GPS behaviours, retarget HeldItemIdentifier, swap the visuals for a stake + rope knot.</summary>
    private static Transform BuildHeldTemplate(Transform gpsHeld)
    {
        var src = gpsHeld.gameObject;
        bool wasActive = src.activeSelf;
        src.SetActive(false);                       // restored right after — vanilla GPS unaffected
        var clone = Object.Instantiate(src);
        src.SetActive(wasActive);

        clone.name = "BuildersLineHeld";
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
    /// -> "Physics.ClosestPoint can only be used with ... convex MeshCollider" (2026-07-13).</summary>
    private static Transform? BuildPickupTemplate(Transform? gpsPickup)
    {
        if (gpsPickup == null) return null;
        var src = gpsPickup.gameObject;
        bool wasActive = src.activeSelf;
        src.SetActive(false);                       // restored right after — vanilla GPS unaffected
        var clone = Object.Instantiate(src);
        src.SetActive(wasActive);

        clone.name = "BuildersLinePickup";
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
    /// SFX spam crashes the game (crash 2026-07-12).</summary>
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
    private static readonly Vector3 MatDisplayEuler = new Vector3(74f, 0f, 16f);
    private const float MatDisplayScale = 3.4f;

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
        var wood = LaserLine.WoodMat();
        var sr = stake.GetComponent<Renderer>();
        if (wood != null && sr != null) sr.sharedMaterial = wood;

        var knotMesh = LaserLine.KnotMesh();
        var ropeMat = LaserLine.RopeMaterial();
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

    /// <summary>Add the crafting recipe unless a previous world already registered it
    /// (the recipe database may be an asset that persists across world loads).</summary>
    private static void EnsureRecipe()
    {
        var recipes = GameState.CraftingSystem?._recipeDatabase?._recipes;
        if (recipes == null) { RLog.Warning("[BuildingLaser] no recipe database; recipe skipped"); return; }

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
