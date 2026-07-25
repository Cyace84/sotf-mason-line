using HarmonyLib;
using RedLoader;
using UnityEngine;

namespace MasonLine;

/// <summary>
/// Crash-site guard for the 2026-07-24 04:54 inventory crash — the first one with a NAMED corpse.
///
/// winedbg backtrace (full, user-provided):
/// <code>
///   InputSystem Tap → PlayerInventory::Open → InventoryCutscene::Play → Cutscene::Play
///   → LayoutItem::SetItemInstance → InventoryLayoutItem::Initialize → LayoutItem::Initialize
///   → ItemInstance::RefreshItemObject
///   → AssetReferenceRenderable::AddOnRenderableLoadedListener (+0x1b0)
///   → UnityEventBase::AddCall (+0x46)   CRASH: `incl 0x1c(%rcx)`, rcx=0x1f40 (garbage near-null)
/// </code>
///
/// Root cause: SDK's managed-injected <c>ItemTools.CustomItemRenderable</c> has a
/// <c>_onRenderableLoaded</c> UnityEvent that Unity never deserialized (the component type does not
/// exist in any asset) — its internals are junk. ANY AddCall/Invoke on it dereferences garbage and
/// page-faults natively (no managed exception, no dump). Our two existing defenses (flag-fix sweep
/// + pre-OnEnable sanitize) both lose the race against clones the game creates lazily DURING
/// inventory open: RefreshItemObject subscribes a listener on the fresh clone before either defense
/// has touched it. State-dependence explained: the race only opens when a new clone spawns
/// mid-session (re-add / save / layout rebuild), never right after a fresh load.
///
/// Fix: prefix the EXACT crash site. For CustomItemRenderable (the only managed-injected kind —
/// native renderables pass through untouched) never let the native body run:
///  - sanitize the junk event first (so a later OnEnable→Invoke can't blow either),
///  - if the model is already available, honor the contract by invoking the callback directly,
///  - otherwise drop the subscription — for managed-injected renderables the async load never
///    completes anyway (nothing ever sets _cachedLoadedObject except our sweep).
/// The backtrace itself proves callers enter through the real function body (frame +0x1b0), so a
/// first-byte detour catches every call site (24 of them, [CallerCount(24)]).
/// </summary>
[HarmonyPatch(typeof(Endnight.Rendering.AssetReferenceRenderable),
    nameof(Endnight.Rendering.AssetReferenceRenderable.AddOnRenderableLoadedListener))]
internal static class RenderableListenerCrashGuard
{
    private static int _handled;

    private static bool Prefix(
        Endnight.Rendering.AssetReferenceRenderable __instance,
        UnityEngine.Events.UnityAction<Transform> callback)
    {
        if (__instance == null) return true;
        if (__instance.GetIl2CppType().Name != "CustomItemRenderable") return true;   // vanilla path

        // Managed-injected renderable: its event is junk — never reach AddCall on it.
        LineTool.SanitizeRenderable(__instance);   // also protects the OnEnable→Invoke path

        var loaded = __instance._cachedLoadedObject;

        // COLD clone: prefix won the race against the 1/30 sweep — model not cached yet. Self-heal by
        // caching the child model right here (same action the sweep does) so the callback still gets a
        // real Transform instead of being dropped. Proven unreproducible by hand (two aggressive
        // craft+inventory-spam sessions, 0 hits) — kept as by-construction safety, silent in release.
        if (loaded == null)
        {
            loaded = LineTool.FindOurInvModel(__instance);
            if (loaded != null) __instance._cachedLoadedObject = loaded;
            Dbg.Msg(System.ConsoleColor.Magenta,
                $"[MasonLine] listener-guard: COLD clone (sweep lost race) on '{__instance.name}' " +
                $"-> self-healed model={(loaded != null ? loaded.name : "NOT-FOUND")}");
        }

        if (_handled < 8)
            Dbg.Msg(System.ConsoleColor.DarkYellow,
                $"[MasonLine] listener-guard: intercepted AddOnRenderableLoadedListener on " +
                $"CustomItemRenderable '{__instance.name}' (loaded={(loaded != null ? loaded.name : "null")}) #{_handled + 1}");
        _handled++;

        if (loaded != null && callback != null)
        {
            try { callback.Invoke(loaded.transform); }
            catch (System.Exception e)
            {
                RLog.Warning($"[MasonLine] listener-guard: direct callback failed: {e.Message}");
            }
        }
        return false;   // never run the native body → never AddCall on the junk event
    }
}
