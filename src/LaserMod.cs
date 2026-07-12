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
        SdkEvents.OnAfterSpawn.Subscribe(LineTool.Setup);
        RLog.Msg(System.ConsoleColor.Cyan,
            "[BuildingLaser] initialized — craft the Builder's String Line (stick + rope), " +
            "hold it: L = drop point A/B, K = toggle snap, J = clear");
    }

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

        LaserLine.UpdateGhost(held && gameplay);
    }
}
