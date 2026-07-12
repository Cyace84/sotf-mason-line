using RedLoader;
using Sons.Crafting;
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
        data._maxAmount = 1;
        data._uiData._itemId = ItemId;
        data._uiData._title = "Builder's String Line";
        data._uiData._translationKey = null;
        data._uiData._description =
            "Plant two stakes and stretch a string line between them. Free log placement snaps to the line.";
        var (icon, outline) = ItemTools.GetIcon(RopeId);   // rope icon reads as "string line" well enough
        if (icon != null) data._uiData._icon = icon;
        if (outline != null) data._uiData._outlineIcon = outline;

        data._heldPrefab = BuildHeldTemplate(tpl._heldPrefab);
        // _pickupPrefab stays the GPS one (only matters if the item is dropped on the ground; v1 ok)

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

        clone.transform.position = new Vector3(0f, -2000f, 0f);   // parked out of sight
        clone.SetActive(true);   // template must be active: instantiated copies inherit the state
        return clone.transform;
    }

    /// <summary>Model prefab for the inventory mat / crafting result display (active, parked).</summary>
    private static GameObject BuildModelPrefab(string name)
    {
        var root = new GameObject(name);
        Object.DontDestroyOnLoad(root);
        AddStakeVisual(root.transform);
        root.transform.position = new Vector3(0f, -2000f, 0f);
        return root;
    }

    /// <summary>Small stake (wood cylinder) with a rope knot at the top — the tool's look.</summary>
    private static void AddStakeVisual(Transform parent)
    {
        var stake = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        stake.name = "Stake";
        var col = stake.GetComponent<Collider>();
        if (col != null) Object.DestroyImmediate(col);
        stake.transform.SetParent(parent, false);
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
            knot.transform.SetParent(parent, false);
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
