using HarmonyLib;
using Sons.Save;

namespace MasonLine;

/// <summary>
/// Kit-marker save-slot identity. SdkEvents.BeforeSaveLoading fires at the right moment but hides
/// the ONE thing we need — the save DIRECTORY. GameSetupManager.GetSelectedSaveId() is only valid
/// in the load menu (in-game saves returned 0 => marker "0 1" never matched on load, kit lost —
/// user repro 2026-07-18). So we patch the SAME SaveGameManager.Save/Load signatures the SDK's own
/// SavingCallbackPatches use (proven IL2CPP-safe) and parse the slot id from the dir path
/// (…/SinglePlayer/&lt;id&gt;) on BOTH sides.
/// </summary>
[HarmonyPatch]
internal static class SavePathPatch
{
    [HarmonyPatch(typeof(SaveGameManager), "Save", new System.Type[] { typeof(string), typeof(string), typeof(bool) })]
    [HarmonyPrefix]
    private static void BeforeSave(string dir) => LineTool.OnBeforeSave(dir);

    [HarmonyPatch(typeof(SaveGameManager), "Load", new System.Type[] { typeof(string), typeof(SaveGameType) })]
    [HarmonyPrefix]
    private static void BeforeLoad(string dir)
    {
        // Register our ItemId BEFORE the inventory deserializes: unknown ids are dropped from the
        // loaded inventory (cold-start kit loss, 2026-07-22 — see LineTool.TryRegisterEarly).
        LineTool.TryRegisterEarly("pre-load");
        LineTool.OnLoadDir(dir);
    }
}
