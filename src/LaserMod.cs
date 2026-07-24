using RedLoader;
using SonsSdk;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// RedLoader mod entry. The tool is the craftable "Builder's String Line" (<see cref="LineTool"/>,
/// stick + rope); while it is equipped, LMB (or L) drops a guide point at the crosshair
/// (1st = stake A, 2nd = stake B -> the string line runs A->B). Free log placement near the string
/// snaps onto it; hold C on a stake to pull the line out (kit returns to the inventory).
/// A translucent ghost stake previews where the point will land. If the item pipeline failed to
/// initialize, the hotkeys work ungated (dev fallback). The actual snap is the Harmony postfix in
/// <see cref="PlacePatch"/>.
/// </summary>
public class LaserMod : SonsMod
{
    public LaserMod()
    {
        HarmonyPatchAll = true;       // installs PlacePatch
        OnUpdateCallback = Tick;
    }

    protected override void OnInitializeMod()
    {
        RenderablePatch.Install(HarmonyInstance);   // heisen-crash guard v2: mute OnEnable Invoke for our item
        // register the ItemData as early as possible: the save's inventory deserializes BEFORE
        // OnAfterSpawn, and unknown ItemIds are dropped (cold-start kit loss, 2026-07-22). The
        // SaveGameManager.Load prefix (SavePathPatch) retries right before deserialize as a backstop.
        SdkEvents.OnSdkInitialized.Subscribe(() => LineTool.TryRegisterEarly("sdk-init"));
        SdkEvents.OnAfterSpawn.Subscribe(LineTool.Setup);
        SdkEvents.OnWorldExited.Subscribe(LineTool.OnWorldExited);      // DDoL lines must die with the world (dupe fix)
        // save-time kit marker: SavePathPatch (Harmony on SaveGameManager.Save/Load) — the SDK event
        // hides the save dir, and that's the only reliable slot-id source for in-game saves
        RLog.Msg(System.ConsoleColor.Cyan,
            "[BuildingLaser] initialized — craft the Builder's String Line (stick + rope), " +
            "hold it: LMB/L plants a stake, hold C on a stake to collect the line");
        // NOTE: do NOT verify here — RedLoader applies HarmonyPatchAll AFTER OnInitializeMod, so an
        // init-time GetPatchInfo reports a FALSE 'MISSING' (proven 2026-07-24: init said MISSING yet
        // the guard fired skips later same session). Verify on the first Tick instead.
    }

    private bool _guardVerified;

    /// <summary>
    /// Deterministic detour-liveness check (inventory-crash hunt 2026-07-23). The 07-23 crash
    /// session logged 12x "ClosestPoint can only be used with..." FROM INSIDE
    /// TryTriggerHardSurfaceImpact — statically impossible if the prefix runs (ISIL: ClosestPoint
    /// is invoked on impactCollider only, the exact object the prefix filters; both call sites —
    /// OnTriggerEnter call and OnCollisionEnter tail-jmp — enter the patched first byte). So the
    /// open question is WHETHER the Harmony detour was installed at all that session. This logs the
    /// answer at startup, before any gameplay contact.
    /// </summary>
    private void VerifyCrashGuardInstalled()
    {
        const string marker = "guard-diag-2";   // build marker — Cheapest-Probe rule
        try
        {
            VerifyDetour(marker, "sfx-guard",
                HarmonyLib.AccessTools.Method(
                    typeof(Sons.Gameplay.ObjectPhysicsInteractionSfx),
                    nameof(Sons.Gameplay.ObjectPhysicsInteractionSfx.TryTriggerHardSurfaceImpact)));
            VerifyDetour(marker, "listener-guard",
                HarmonyLib.AccessTools.Method(
                    typeof(Endnight.Rendering.AssetReferenceRenderable),
                    nameof(Endnight.Rendering.AssetReferenceRenderable.AddOnRenderableLoadedListener)));
        }
        catch (System.Exception e)
        {
            RLog.Error($"[BuildingLaser][{marker}] guard verification threw: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void VerifyDetour(string marker, string label, System.Reflection.MethodBase? target)
    {
        if (target == null)
        {
            RLog.Error($"[BuildingLaser][{marker}] {label}: target method NOT FOUND — guard is DEAD");
            return;
        }
        var info = HarmonyLib.Harmony.GetPatchInfo(target);
        int prefixes = info?.Prefixes?.Count ?? 0;
        if (prefixes > 0)
            RLog.Msg(System.ConsoleColor.Green,
                $"[BuildingLaser][{marker}] {label} detour VERIFIED: {prefixes} prefix on {target.Name}");
        else
            RLog.Error(
                $"[BuildingLaser][{marker}] {label} detour MISSING (prefixes=0) on {target.Name} — HarmonyPatchAll did not take");
    }

    private const float CollectHoldSeconds = 0.4f;   // vanilla dismantle Hold(duration=0.4) — recon 2026-07-17
    private static float _collectHold;

    private void Tick()
    {
        if (!_guardVerified) { _guardVerified = true; VerifyCrashGuardInstalled(); }
        bool held = LineTool.IsHeld;
        // gameplay = mouse captured; while any UI (inventory/book/pause) owns the cursor, clicks
        // must not plant stakes
        bool gameplay = Cursor.lockState == CursorLockMode.Locked;

        if (held && gameplay && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.L)))
            LaserLine.DropPoint();

        // drive the inventory hover outline (axe-style highlight) from IsHighlighted
        LineTool.UpdateInventoryHover();

        // C on a stake = HOLD-to-collect, vanilla loose-object dismantle feel (user 2026-07-16: only
        // structures get the icon+gauge UI; a bed just SHAKES while you hold C — copy the bed). The
        // stakes jitter while the hold accumulates; release early = nothing happens. J = instant clear.
        bool aimingStake = gameplay && LaserLine.AimingAtStake();
        if (aimingStake && Input.GetKeyDown(KeyCode.C)) LaserLine.Nudge();   // per-press kick, vanilla feel
        if (aimingStake && Input.GetKey(KeyCode.C))
        {
            _collectHold += Time.deltaTime;
            if (_collectHold >= CollectHoldSeconds)
            {
                _collectHold = 0f;
                LaserLine.EndShake();
                LaserLine.CollectAimed();   // pulls out only the AIMED line; J still clears everything
            }
            else if (_collectHold > 0.12f) LaserLine.Shake(_collectHold);    // brief grace = tap stays a nudge
        }
        else if (_collectHold > 0f) { _collectHold = 0f; LaserLine.EndShake(); }
        LaserLine.UpdateNudge();

        LaserLine.UpdateGhost(held && gameplay);
    }
}
