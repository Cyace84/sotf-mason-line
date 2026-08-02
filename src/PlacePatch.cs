using Construction;
using HarmonyLib;

namespace MasonLine;

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
        if (_busy || !GuideLine.HasLine || __instance == null) return;

        try
        {
            // Gate: warp placements that stand on the world itself, and leave structure-snapped
            // ones at their vanilla position. Terrain alone is not enough — a flat shelf of
            // mountain rock takes logs perfectly well but is a mesh on its own layer, so the line
            // used to be ignored there. All three properties compare the hit transform's layer
            // against the game's own ground layers, and the two rock ones additionally require the
            // hit to belong to no structure element, which is the distinction we care about.
            // NOTE: the game's background snap-point predictor (PredictedSnapPointsUpdater →
            // PlaceLeaningBeamStructureModule fake pilars) also calls CalcRelativePlacePosition on
            // half-built TargetInfos, where reading these throws. Those calls are not the player's
            // active placement, so swallow the exception and leave them vanilla.
            if (!__instance.IsTerrain && !__instance.IsValidTerrainRock && !__instance.IsValidCaveGround) return;

            // Only capture placements that are actually AT a string (within the configured snap
            // distance sideways, between the stakes; nearest line wins). Anything else on the map
            // must build vanilla.
            if (!GuideLine.TryProject(__instance.PlacePosition, out var snapped, out var dir)) return;

            _busy = true;
            __instance.SetPlacePosition(snapped);
            // Lay the log along the line it just snapped to.
            __instance.SetPlaceAxis(dir);
        }
        catch { }
        finally
        {
            _busy = false;
        }
    }
}
