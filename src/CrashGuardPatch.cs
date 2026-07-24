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
    private static int _swallowed;
    private static bool _loggedActive;

    private static bool Prefix(Collider impactCollider)
    {
        if (!_loggedActive)
        {
            _loggedActive = true;
            Dbg.Msg(System.ConsoleColor.DarkYellow, "[BuildingLaser] crash-guard ACTIVE (TryTriggerHardSurfaceImpact prefix is running)");
        }
        if (impactCollider == null) return true;

        // WHITELIST (2026-07-24 rewrite). Prior blacklist ("skip non-convex OR vertexCount>256") had a
        // hole: with the guard PROVABLY live + firing (Latest.log "crash-guard ACTIVE" + skips on
        // MergedCollisionPart805/936), Player.log STILL logged 21x "ClosestPoint can only be used with
        // ... convex MeshCollider" from THIS method, stack TryTriggerHardSurfaceImpact←ReportContacts
        // ←OnSceneContact, right after the BackpackGroundMesh partial-hull cook. So BackpackGroundMesh
        // reached ClosestPoint despite the guard: it read as convex==true (marked convex, only cooked
        // partial) and/or sharedMesh.vertexCount was not readable under IL2CPP, so the old sub-check
        // passed it. Unity's ClosestPoint here LOGS-and-continues (no "swallowed" Finalizer line ever
        // fired) — the crash is the >1,000,000-line log spam ([ line 1038888 ] collapse) saturating
        // RLog/Unity I/O under Wine, not a thrown exception.
        //
        // New rule: ClosestPoint is only valid on Box/Sphere/Capsule or a convex, cook-clean Mesh. We
        // cannot cheaply PROVE a mesh cooked clean, so default to SKIP on any uncertainty. Allow SFX
        // only when the collider is provably safe. Cost = a cosmetic impact sound on mesh/terrain
        // surfaces; never a crash. TerrainCollider is also unsafe for ClosestPoint — skip it too.
        var mesh = impactCollider.TryCast<MeshCollider>();
        if (mesh != null)
        {
            var sm = mesh.sharedMesh;
            int vcount = sm != null ? sm.vertexCount : -1;
            bool provablySafe = mesh.convex && sm != null && vcount <= 256;
            if (!provablySafe)
            {
                if (_skipped < 8)
                    Dbg.Msg(System.ConsoleColor.DarkYellow,
                        $"[BuildingLaser] crash-guard: skipped SFX on MeshCollider '{impactCollider.name}' (convex={mesh.convex}, verts={vcount}) — not provably ClosestPoint-safe #{_skipped + 1}");
                _skipped++;
                return false;
            }
            return true;   // convex + readable mesh ≤256 verts — safe, play SFX
        }
        if (impactCollider.TryCast<TerrainCollider>() != null)
        {
            if (_skipped < 8)
                Dbg.Msg(System.ConsoleColor.DarkYellow,
                    $"[BuildingLaser] crash-guard: skipped SFX on TerrainCollider '{impactCollider.name}' (ClosestPoint-unsafe) #{_skipped + 1}");
            _skipped++;
            return false;
        }
        // Box / Sphere / Capsule (or anything TryCast<MeshCollider> can't see): ClosestPoint-safe.
        return true;
    }

    // Belt-and-suspenders: the prefix only skips MeshCollider.convex==false, but BackpackGroundMesh is
    // a PARTIAL HULL — convex==true yet ClosestPoint still throws ("Couldn't create a Convex Mesh ...
    // within the maximum polygons limit"). Any managed exception escaping this method crosses the
    // native physics callback and native-crashes the process (crash on clicking an inventory item,
    // 2026-07-14). A Harmony finalizer that returns null SWALLOWS whatever the body threw, for every
    // collider type — the throw never reaches native. Cost = one unplayed impact SFX.
    private static System.Exception? Finalizer(System.Exception? __exception)
    {
        if (__exception != null)
        {
            if (_swallowed < 3)
                RLog.Msg(System.ConsoleColor.DarkYellow,
                    $"[BuildingLaser] crash-guard: swallowed {__exception.GetType().Name} in TryTriggerHardSurfaceImpact (would native-crash) #{_swallowed + 1}");
            _swallowed++;
        }
        return null;   // suppress → nothing propagates to the native callback
    }
}
