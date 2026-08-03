using RedLoader;
using Sons.Crafting;
using Sons.Inventory;
using Sons.Items.Core;
using SonsSdk;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;

namespace MasonLine;

/// <summary>LineTool, presentation half: held/pickup/mat templates cloned from the GPS
/// locator, the inventory-mat hover wiring, and the renderable crash-hole sanitizing.</summary>
internal static partial class LineTool
{
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

    // The freshly crafted stick sat too low on the mat. Live-tuned (./tune.sh matpos) to +0.2 along the
    // wrapper's local Z, which is "up the mat" once MatDisplayEuler has laid it flat. This prefab is
    // shared with the backpack model, but ApplyWobblePose re-pins that instance to InvStakePos on every
    // backpack opening, so the offset only ever shows on the crafting-result copy.
    private static readonly Vector3 MatDisplayOffset = new Vector3(0f, 0f, 0.2f);

    /// <summary>The tool's look: a stake with a rope knot — the in-game branch mesh on the mat,
    /// a wood cylinder in hand and as the fallback.
    /// matDisplay=true nests the visual under a wrapper laid flat + enlarged for the inventory mat.</summary>
    private static void AddStakeVisual(Transform parent, bool keepTriggerCollider = false, bool matDisplay = false)
    {
        Transform host = parent;
        if (matDisplay)
        {
            var wrapper = new GameObject("StakeVisual");
            wrapper.transform.SetParent(parent, false);
            wrapper.transform.localPosition = MatDisplayOffset;
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
}
