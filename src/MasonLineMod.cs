using RedLoader;
using SonsSdk;
using UnityEngine;

namespace MasonLine;

/// <summary>
/// RedLoader mod entry. The tool is the craftable "Mason Line" (<see cref="LineTool"/>,
/// 2 sticks + rope); while it is equipped, LMB drops a guide point at the crosshair
/// (1st = stake A, 2nd = stake B -> the string line runs A->B). Free log placement near the string
/// snaps onto it; hold Dismantle on a stake to pull the line out (bundle returns to the inventory).
/// A translucent ghost stake previews where the point will land. If the item pipeline failed to
/// initialize, placement works ungated (dev fallback). The actual snap is the Harmony postfix in
/// <see cref="PlacePatch"/>.
/// </summary>
public class MasonLineMod : SonsMod
{
    public MasonLineMod()
    {
        HarmonyPatchAll = true;       // installs PlacePatch
        OnUpdateCallback = Tick;
    }

    protected override void OnInitializeMod()
    {
        RenderablePatch.Install(HarmonyInstance);   // heisen-crash guard v2: mute OnEnable Invoke for our item
        // register the ItemData as early as possible: the save's inventory deserializes BEFORE
        // OnAfterSpawn, and unknown ItemIds are dropped (cold-start bundle loss,). The
        // SaveGameManager.Load prefix (SavePathPatch) retries right before deserialize as a backstop.
        MasonLineConfig.Init();
        LineTool.ApplyConfig();
        SdkEvents.OnSdkInitialized.Subscribe(() =>
        {
            LineTool.TryRegisterEarly("sdk-init");
            SUI.SettingsRegistry.CreateSettings(this, null, typeof(MasonLineConfig));
        });
        SdkEvents.OnAfterSpawn.Subscribe(LineTool.Setup);
        SdkEvents.OnWorldExited.Subscribe(LineTool.OnWorldExited);      // DDoL lines must die with the world (dupe fix)
        // save-time bundle marker: SavePathPatch (Harmony on SaveGameManager.Save/Load) — the SDK event
        // hides the save dir, and that's the only reliable slot-id source for in-game saves
        RLog.Msg(System.ConsoleColor.Cyan,
            "[MasonLine] initialized. Craft the Mason Line (2 sticks + rope), " +
            "hold it: LMB plants a stake, hold Dismantle on a stake to collect the line");
        // NOTE: do NOT verify here — RedLoader applies HarmonyPatchAll AFTER OnInitializeMod, so an
        // init-time GetPatchInfo reports a FALSE 'MISSING' (proven: init said MISSING yet
        // the guard fired skips later same session). Verify on the first Tick instead.
    }

    private bool _guardVerified;

    /// <summary>
    /// Deterministic detour-liveness check (inventory-crash hunt). The 07-23 crash
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
            RLog.Error($"[MasonLine][{marker}] guard verification threw: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void VerifyDetour(string marker, string label, System.Reflection.MethodBase? target)
    {
        if (target == null)
        {
            RLog.Error($"[MasonLine][{marker}] {label}: target method NOT FOUND, guard is DEAD");
            return;
        }
        var info = HarmonyLib.Harmony.GetPatchInfo(target);
        int prefixes = info?.Prefixes?.Count ?? 0;
        if (prefixes > 0)
            RLog.Msg(System.ConsoleColor.Green,
                $"[MasonLine][{marker}] {label} detour VERIFIED: {prefixes} prefix on {target.Name}");
        else
            RLog.Error(
                $"[MasonLine][{marker}] {label} detour MISSING (prefixes=0) on {target.Name}; HarmonyPatchAll did not take");
    }

    private const float CollectHoldSeconds = 0.4f;   // vanilla dismantle Hold(duration=0.4) — matches the vanilla dismantle hold
    private static float _collectHold;

    private void Tick()
    {
        if (!_guardVerified) { _guardVerified = true; VerifyCrashGuardInstalled(); }
        // A language switch releases every string table, our item's name included, so watch for it.
        MasonLineStrings.CheckLocale();
        bool held = LineTool.IsHeld;
        // gameplay = mouse captured; while any UI (inventory/book/pause) owns the cursor, clicks
        // must not plant stakes
        bool gameplay = Cursor.lockState == CursorLockMode.Locked;

        // A half-defined line may not outlive the tool leaving the hands: left standing, the stake is
        // invisible state and the next click, minutes later and metres away, strings a line back to
        // it. This is a STATE rule, not an unequip event — it reads "playing, hands empty".
        // Opening the backpack counts as well (the held instance is deactivated there while the
        // cursor stays locked), so a look into the pack pulls the pending stake too. Accepted as-is:
        // the stake is free until the line completes, so nothing is lost by re-planting it.
        if (gameplay && !held) GuideLine.CancelPending();

        // place a stake = mouse click, like vanilla placement. No mod hotkey.
        if (held && gameplay && Input.GetMouseButtonDown(0))
            GuideLine.DropPoint();

        // drive the inventory hover outline (axe-style highlight) from IsHighlighted
        LineTool.UpdateInventoryHover();
        LineTool.WarnIfIdChangePending();   // the id is read at startup; say so when the setting moves

        // Aim at a stake + HOLD the vanilla Dismantle action (RU "Убрать", default C, REBINDABLE) to
        // collect the line. No mod hotkey: we read the player's own binding via Sons.Input.InputSystem,
        // so remapping Dismantle moves this too. The stakes jitter while the hold accumulates; release
        // early = nothing happens.
        bool aimingStake = gameplay && GuideLine.AimingAtStake();
        if (aimingStake && Sons.Input.InputSystem.GetButtonDown(Sons.Input.InputSystem.Actions.DismantleElement)) GuideLine.Nudge();   // per-press kick, vanilla feel
        if (aimingStake && Sons.Input.InputSystem.GetButton(Sons.Input.InputSystem.Actions.DismantleElement))
        {
            _collectHold += Time.deltaTime;
            if (_collectHold >= CollectHoldSeconds)
            {
                _collectHold = 0f;
                GuideLine.EndShake();
                GuideLine.CollectAimed();   // pulls out only the AIMED line, never the others
            }
            else if (_collectHold > 0.12f) GuideLine.Shake(_collectHold);    // brief grace = tap stays a nudge
        }
        else if (_collectHold > 0f) { _collectHold = 0f; GuideLine.EndShake(); }
        GuideLine.UpdateNudge();

        GuideLine.UpdateGhost(held && gameplay);
    }
}
