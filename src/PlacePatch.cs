using Construction;
using HarmonyLib;

namespace BuildingLaser;

/// <summary>
/// The whole feature, in one postfix.
///
/// <see cref="TargetInfo.PlacePosition"/> (native offset 0x64) is the single source every placement
/// stage reads: preview (<c>PlaceBeamOnGroundModule.GetPreviewUiPositioningInfo</c> @0x182bebad0),
/// validate (<c>TargetInfo.CalcRelativePlacePosition</c> @0x182a75b10), and place
/// (<c>PlaceBeamOnGroundModule.OnPlace</c> @0x182bed0b0) — all decompile-proven to copy targetInfo[0x64].
/// So overriding it here moves the ghost, the validation, and the finally-placed log together.
///
/// <c>CalcRelativePlacePosition</c> is <c>void</c> with no parameters, so the postfix is Harmony-safe on
/// IL2CPP — no ref/out params, sidestepping the HarmonyX out-param corruption bug; no native detour.
/// It runs after the vanilla calc (which itself sets PlaceAxis), so our SetPlaceAxis wins.
/// </summary>
[HarmonyPatch(typeof(TargetInfo), "CalcRelativePlacePosition")]
internal static class PlacePatch
{
    // RE-ENTRANCY GUARD. SetPlacePosition internally calls CalcRelativePlacePosition (dump token
    // 40133 -> CalcRelativePlacePosition), so calling it from this postfix re-enters the patched
    // method -> unbounded recursion -> hard freeze. While we're applying our override, the nested
    // CalcRelativePlacePosition postfix must be a no-op.
    [System.ThreadStatic] private static bool _busy;

    private static void Postfix(TargetInfo __instance)
    {
        if (_busy || !LaserLine.SnapActive || !LaserLine.HasLine || __instance == null) return;

        // v1 gate: only warp ground (terrain) placements — that's the free-log-on-ground case.
        // Structure-snapped placements keep their vanilla position. If non-log terrain placements
        // get warped too, narrow this to active module == PlaceBeamOnGroundModule.
        if (!__instance.IsTerrain) return;

        _busy = true;
        try
        {
            __instance.SetPlacePosition(LaserLine.Project(__instance.PlacePosition));
            // Orient the log along the line. If it comes out perpendicular, swap to Cross(up, Dir).
            __instance.SetPlaceAxis(LaserLine.Dir);
        }
        finally
        {
            _busy = false;
        }
    }
}
