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

        try
        {
            // v1 gate: only warp ground (terrain) placements — that's the free-log-on-ground case.
            // Structure-snapped placements keep their vanilla position. If non-log terrain placements
            // get warped too, narrow this to active module == PlaceBeamOnGroundModule.
            // NOTE: the game's background snap-point predictor (PredictedSnapPointsUpdater →
            // PlaceLeaningBeamStructureModule fake pilars) also calls CalcRelativePlacePosition on
            // half-built TargetInfos where get_IsTerrain THROWS an Il2CppException (Latest.log
            // 2026-07-16 04:4x, 12 stack-trace spams). Those calls are not the player's active
            // placement — swallow and leave them vanilla.
            if (!__instance.IsTerrain) return;

            // Only capture placements that are actually AT the string (≤2m sideways, between the
            // stakes). Anything else on the map must build vanilla — an armed line is not a magnet.
            if (!LaserLine.TryProject(__instance.PlacePosition, out var snapped)) return;

            _busy = true;
            __instance.SetPlacePosition(snapped);
            // Orient the log along the line. If it comes out perpendicular, swap to Cross(up, Dir).
            __instance.SetPlaceAxis(LaserLine.Dir);
        }
        catch { }
        finally
        {
            _busy = false;
        }
    }
}
