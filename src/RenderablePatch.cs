using RedLoader;
using UnityEngine;

namespace BuildingLaser;

/// <summary>
/// Kills the inventory heisen-crash (AV at UnityEvent`1.Invoke ← CustomItemRenderable.OnEnable;
/// ErrorLog.log 2026-07-16 04:14, 07-17 00:48 and 07-17 01:12 — the last one WITH the v1 guard
/// installed, proving the ancestor-name scope check loses: clones fire OnEnable during Instantiate,
/// BEFORE being parented under the BuildersLine layout, so the walk up found nothing and the
/// poisoned Invoke ran anyway).
///
/// Mechanism: the SDK never subscribes to _onRenderableLoaded — only the GAME does, and it is that
/// interop AddCall (RankException, the craft-vanish hole) that leaves the event's native call-list
/// corrupt. OnEnable's only body is Invoke(event), which our mod does not need at all: pose and
/// loaded-flags are managed by LineTool itself.
///
/// v2 guard: identify OUR renderable via the managed private field _gameObject (set in Init()
/// synchronously at AddComponent time — exists before the first OnEnable, no parenting race), then
/// SKIP OnEnable entirely (return false) + best-effort replace the poisoned event. Other mods'
/// custom items keep vanilla behaviour.
/// </summary>
internal static class RenderablePatch
{
    private static System.Reflection.FieldInfo? _gameObjectField;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            var t = typeof(SonsSdk.ItemTools).GetNestedType(
                "CustomItemRenderable",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            var m = t?.GetMethod(
                "OnEnable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            _gameObjectField = t?.GetField(
                "_gameObject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (m == null || _gameObjectField == null)
            {
                RLog.Error("[BuildingLaser] OnEnable guard NOT installed — CustomItemRenderable members not found (SDK changed?)");
                return;
            }
            harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(
                typeof(RenderablePatch).GetMethod(nameof(Prefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
            RLog.Msg(System.ConsoleColor.Cyan, "[BuildingLaser] OnEnable guard v2 installed (skip-Invoke for our item)");
        }
        catch (System.Exception e)
        {
            RLog.Error($"[BuildingLaser] OnEnable guard install failed: {e.Message}");
        }
    }

    private static bool Prefix(object __instance)
    {
        try
        {
            // _gameObject is a managed field of the injected class, set in Init() right after
            // AddComponent — reliable BEFORE the first OnEnable, unlike the transform hierarchy.
            var go = _gameObjectField!.GetValue(__instance) as GameObject;
            if (go == null || !go.name.Contains("BuildersLine")) return true;   // not ours → vanilla

            // ours: best-effort defuse the event too, then NEVER Invoke — nothing we rely on listens
            var ar = __instance as Endnight.Rendering.AssetReferenceRenderable;
            if (ar != null && LineTool.SanitizeRenderable(ar))
                RLog.Msg(System.ConsoleColor.Cyan,
                    $"[BuildingLaser] OnEnable guard: sanitized + muted Invoke for {go.name}");
            return false;                                                        // skip original
        }
        catch (System.Exception e)
        {
            RLog.Error($"[BuildingLaser] OnEnable guard threw ({e.GetType().Name}: {e.Message}) — running original");
            return true;
        }
    }
}
