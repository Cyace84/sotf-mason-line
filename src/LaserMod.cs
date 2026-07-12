using RedLoader;
using SonsSdk;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// RedLoader mod entry. Hotkeys (dev v1):
///   L = drop a guide point at your crosshair (1st = A, 2nd = B -> line runs A->B)
///   K = toggle snap (project the build ghost onto the line)
///   J = clear the line
/// The actual snap is the Harmony postfix in <see cref="PlacePatch"/>.
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
        RLog.Msg(System.ConsoleColor.Cyan,
            "[BuildingLaser] initialized — L: drop point A then B, K: toggle snap, J: clear");
    }

    private void Tick()
    {
        if (Input.GetKeyDown(KeyCode.L)) LaserLine.DropPoint();

        if (Input.GetKeyDown(KeyCode.K))
        {
            LaserLine.SnapActive = !LaserLine.SnapActive;
            LaserLine.RefreshRopeCue();
            RLog.Msg(System.ConsoleColor.Yellow, $"[BuildingLaser] snap = {LaserLine.SnapActive}");
        }

        if (Input.GetKeyDown(KeyCode.J)) LaserLine.Clear();

        LaserLine.UpdateAimDot();
    }
}
