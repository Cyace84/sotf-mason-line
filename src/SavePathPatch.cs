using HarmonyLib;
using Sons.Save;

namespace MasonLine;

/// <summary>
/// Works out which save slot the world belongs to, so the bundle marker can be tied to it.
///
/// The obvious sources do not work. SdkEvents.BeforeSaveLoading fires at the right moment but does
/// not expose the save directory, and GameSetupManager.GetSelectedSaveId() only returns a real id
/// in the load menu: during an in-game save it returns 0, which silently detached the marker from
/// its slot and lost the bundle. The directory path is the one source both sides agree on, so we patch
/// the same SaveGameManager.Save/Load signatures the SDK's own callbacks use and parse the id out of
/// it. The parse takes the last numeric path segment, so it is blind to the save type: single player,
/// a hosted co-op game and a client copy all land in …/&lt;SinglePlayer|Multiplayer|MultiplayerClient&gt;/&lt;id&gt;.
/// Ids are only guaranteed unique WITHIN one of those folders (SaveGameManager.IsValidNewId checks
/// against TryGetSaveGameIds for a single SaveGameType), so a marker keyed on the number alone would
/// collide if the same id ever appeared in two of them.
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
        // loaded inventory (cold-start bundle loss; see LineTool.TryRegisterEarly).
        LineTool.TryRegisterEarly("pre-load");
        LineTool.OnLoadDir(dir);
    }
}
