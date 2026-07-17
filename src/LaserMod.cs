using RedLoader;
using SonsSdk;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// RedLoader mod entry. The tool is the craftable "Builder's String Line" (<see cref="LineTool"/>,
/// stick + rope); while it is equipped:
///   L = drop a guide point at your crosshair (1st = A, 2nd = B -> line runs A->B)
///   K = toggle snap (project the build ghost onto the line)
///   J = clear the line
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
        SdkEvents.OnAfterSpawn.Subscribe(LineTool.Setup);
        RLog.Msg(System.ConsoleColor.Cyan,
            "[BuildingLaser] initialized — craft the Builder's String Line (stick + rope), " +
            "hold it: L = drop point A/B, K = toggle snap, J = clear");
    }

    private const float CollectHoldSeconds = 0.4f;   // vanilla dismantle Hold(duration=0.4) — recon 2026-07-17
    private static float _collectHold;

    private void Tick()
    {
        bool held = LineTool.IsHeld;
        // gameplay = mouse captured; while any UI (inventory/book/pause) owns the cursor, clicks
        // must not plant stakes
        bool gameplay = Cursor.lockState == CursorLockMode.Locked;

        if (held && gameplay && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.L)))
            LaserLine.DropPoint();

        if (Input.GetKeyDown(KeyCode.K))
        {
            LaserLine.SnapActive = !LaserLine.SnapActive;
            LaserLine.RefreshRopeCue();
            RLog.Msg(System.ConsoleColor.Yellow, $"[BuildingLaser] snap = {LaserLine.SnapActive}");
        }

        if (Input.GetKeyDown(KeyCode.J)) LaserLine.Clear();

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
                LaserLine.PlayCollectSound();
                LaserLine.Clear();
            }
            else if (_collectHold > 0.12f) LaserLine.Shake(_collectHold);    // brief grace = tap stays a nudge
        }
        else if (_collectHold > 0f) { _collectHold = 0f; LaserLine.EndShake(); }
        LaserLine.UpdateNudge();

        LaserLine.UpdateGhost(held && gameplay);
    }
}
