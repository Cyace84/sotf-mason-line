using RedLoader;
using SonsSdk.Attributes;
using SUI;

namespace MasonLine;

/// <summary>
/// Settings shown in the mod menu, stored in UserData/MasonLine.cfg.
///
/// The capture zone is the only thing worth exposing: how close a placement has to be before the
/// string takes it over. Too wide and you cannot put anything down beside a standing line; too
/// narrow and a log misses the string you are clearly aiming at.
/// </summary>
public static class MasonLineConfig
{
    public const float DefaultSnapRadius = 0.3f;
    public const float DefaultSnapEndMargin = 0.1f;
    public const float DefaultStakeMagnet = 0.3f;
    public const int DefaultItemId = 9417;

    /// <summary>Floor for a hand-typed id. Vanilla items sit far below this, and an id that lands on
    /// one of them would have us fighting the game over the same slot.</summary>
    public const int MinItemId = 1000;

    public static ConfigEntry<float> SnapRadius { get; private set; } = null!;
    public static ConfigEntry<float> SnapEndMargin { get; private set; } = null!;
    public static ConfigEntry<float> StakeMagnet { get; private set; } = null!;
    /// <summary>Guards the id below. Off means the default is used no matter what the file says, so
    /// a half-typed number cannot take the kit away from someone who was only looking.
    ///
    /// Kept out of the settings panel ([SettingsUiIgnore], honoured by SUI's reflection over this
    /// class): it is a repair knob for one rare case the log announces by name, and getting it wrong
    /// costs the player every kit in the pack. A row in the panel invites a poke; a line in
    /// MasonLine.cfg takes intent.</summary>
    [SettingsUiIgnore]
    public static ConfigEntry<bool> OverrideItemId { get; private set; } = null!;

    // A string, not an int, on purpose: an int entry in the config file is fine either way, but the
    // value is hand-edited, and a string keeps a typo a typo instead of a silent 0.
    [SettingsUiIgnore]
    public static ConfigEntry<string> ItemId { get; private set; } = null!;

    public static void Init()
    {
        var cat = ConfigSystem.CreateFileCategory("MasonLine", "Mason Line", "MasonLine.cfg");

        SnapRadius = cat.CreateEntry("snapRadius", DefaultSnapRadius, "Snap distance (m)",
            "How far to the side of the string a placement is still pulled onto it. " +
            "Lower this if the line grabs things you wanted to put down next to it.");
        SnapRadius.SetRange(0.1f, 3f);

        SnapEndMargin = cat.CreateEntry("snapEndMargin", DefaultSnapEndMargin, "Reach past the stakes (m)",
            "How far beyond each stake the string keeps working, so a wall can end flush with its stake.");
        SnapEndMargin.SetRange(0f, 3f);

        StakeMagnet = cat.CreateEntry("stakeMagnet", DefaultStakeMagnet, "Stake magnet (m)",
            "Aim the first stake of a new line this close to a standing stake and it lands exactly " +
            "on it, so corners and continuations share a point. 0 turns the magnet off.");
        StakeMagnet.SetRange(0f, 2f);

        OverrideItemId = cat.CreateEntry("overrideItemId", false, "Use a custom item id",
            $"Leave this off unless the log reports that another mod already owns item id " +
            $"{DefaultItemId}. While it is off, the id below is ignored.");

        ItemId = cat.CreateEntry("itemId", DefaultItemId.ToString(), "Custom item id",
            $"Only used when the line above is true. Write a whole number above {MinItemId} and " +
            "restart the game. Kits sitting in your pack were saved under the old id and will not " +
            "come back, but kits staked out as lines will: plant your lines, save, then change this.");
    }

    // A placement can happen before the settings exist (pipeline-down dev run), so fall back to the
    // built-in values rather than dereferencing a null entry.
    public static float Radius => SnapRadius?.Value ?? DefaultSnapRadius;
    public static float EndMargin => SnapEndMargin?.Value ?? DefaultSnapEndMargin;
    public static float Magnet => StakeMagnet?.Value ?? DefaultStakeMagnet;
    public static bool OverrideEnabled => OverrideItemId?.Value ?? false;

    /// <summary>The typed id, or the default when the override is off, the text is not a number, or
    /// the number sits in the vanilla range. Rejection is reported by the caller, not here.</summary>
    public static int ItemIdValue =>
        OverrideEnabled && int.TryParse(ItemId?.Value, out var id) && id >= MinItemId
            ? id
            : DefaultItemId;

    /// <summary>True when the override is on but the text cannot be used.</summary>
    public static bool ItemIdRejected =>
        OverrideEnabled && !(int.TryParse(ItemId?.Value, out var id) && id >= MinItemId);
}
