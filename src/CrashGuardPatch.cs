using HarmonyLib;
using RedLoader;
using Sons.Gameplay;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// Game-wide crash guard (NOT specific to our item).
///
/// <c>ObjectPhysicsInteractionSfx.TryTriggerHardSurfaceImpact(Collider)</c> runs
/// <c>Physics.ClosestPoint</c> on the collider it just contacted. ClosestPoint THROWS a managed
/// exception when that collider is a <b>non-convex MeshCollider</b> (Unity restriction). The
/// backpack mat's <c>BackpackGroundMesh</c> is exactly that — a partial-hull, non-convex mesh
/// ("Couldn't create a Convex Mesh ... within the maximum polygons limit (256)" at load). When the
/// inventory mat is open, vanilla item-props resting on it contact that mesh, ClosestPoint throws,
/// and the throw propagates through the NATIVE physics callback
/// (<c>OnSceneContact → ReportContacts → TryTriggerHardSurfaceImpact</c>). Under IL2CPP/Wine a
/// managed exception crossing a native callback native-crashes the process with no managed stack —
/// Player.log shows only the ClosestPoint warning + this method, spammed, then the game is gone
/// (crashes 2026-07-12/13).
///
/// Fix: skip the SFX for the exact collider type that would throw. Cost = one unplayed impact
/// sound on a non-convex surface. Everything else runs vanilla (return true).
///
/// Decompile: <c>Sons.Gameplay.ObjectPhysicsInteractionSfx</c> — OnCollisionEnter / OnTriggerEnter
/// (<c>_impactOnTriggers</c>) → TryTriggerHardSurfaceImpact → ClosestPoint. Method is
/// <c>void(Collider)</c>: no ref/out, safe to Harmony-prefix on IL2CPP.
/// </summary>
[HarmonyPatch(typeof(ObjectPhysicsInteractionSfx), nameof(ObjectPhysicsInteractionSfx.TryTriggerHardSurfaceImpact))]
internal static class HardSurfaceImpactCrashGuard
{
    // Instrumentation: prove the prefix actually runs (a native physics-callback caller could bypass
    // the managed Harmony detour). If the loader log shows these lines AND Player.log has no
    // "ClosestPoint can only be used with ... convex" warnings, the guard is live and effective.
    private static int _skipped;
    private static bool _loggedActive;

    private static bool Prefix(Collider impactCollider)
    {
        if (!_loggedActive)
        {
            _loggedActive = true;
            RLog.Msg(System.ConsoleColor.DarkYellow, "[BuildingLaser] crash-guard ACTIVE (TryTriggerHardSurfaceImpact prefix is running)");
        }
        if (impactCollider == null) return true;
        var mesh = impactCollider.TryCast<MeshCollider>();
        if (mesh != null && !mesh.convex)
        {
            if (_skipped < 3)
                RLog.Msg(System.ConsoleColor.DarkYellow,
                    $"[BuildingLaser] crash-guard: skipped hard-surface SFX on non-convex mesh '{impactCollider.name}' (would crash via ClosestPoint) #{_skipped + 1}");
            _skipped++;
            return false;   // ClosestPoint would throw → native crash
        }
        return true;
    }
}
