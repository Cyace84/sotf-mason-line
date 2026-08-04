# Mason Line

A craftable string line for Sons of the Forest. Stretch it between two stakes, and free logs snap to it. The first log of a wall lands dead-straight, no eyeballing.

## How to use

1. **Craft** the Mason Line: 2 sticks + 1 rope (combine on the crafting mat).
2. **Equip** it and aim at the ground. A ghost stake shows where it'll land.
3. **Left-click** to plant stake A, then again for stake B. A string runs between them.
4. Place free logs near the string. The build ghost snaps onto the line automatically.
5. **Hold Dismantle** while looking at a stake to pull the line out. The bundle goes back to your inventory. That's the vanilla action listed under Construction in the controls menu, bound to C by default.

Each crafted bundle is one line. Craft more bundles for more lines; they all work at the same time. Aim the first stake of a new line at one already standing and it lands exactly on it, so a run can turn a corner and carry on from the same point, though nothing squares up the angle between them.

The cord takes more than logs. Anything the game lets you set down loose on open ground follows it: logs flat or standing, standing sticks, campfires, rocks. Guidebook blueprints, the ghosts you build up piece by piece like a bed, do not snap; the line guides loose pieces only.

## Settings

The mod menu writes `UserData/MasonLine.cfg`.

Snap distance, default 0.3 m, is how far to the side of the string a placement still gets pulled onto it. Raise it if logs miss a string you are clearly aiming at, lower it if the line grabs logs you meant to set down beside it.

Reach past the stakes, default 0.1 m, is how far beyond each stake the string keeps working, so a wall can finish flush with its stake instead of a log short.

Stake magnet, default 0.3 m, is how close to a standing stake you have to aim the first stake of a new line before it lands exactly on it, with 0 leaving every stake to your own aim.

All three apply the moment you change them.

## Compatibility

The bundle registers as item id 9417. If another mod already owns that id, Mason Line refuses to register rather than overwrite it, and the log says so: `item id 9417 is already taken by ...`. Open `UserData/MasonLine.cfg`, set `overrideItemId = true` and `itemId` to another whole number above 1000, then restart the game. The id is read once at startup, so nothing changes until you do.

That one setting is kept out of the settings panel on purpose. It repairs a single rare case that the log names for you, and setting it by accident costs you every bundle in your pack.

Bundles lying in your pack were written into the save under the old id and will not come back after the change. Bundles staked out as lines will: stake them out, save, then restart, and they are returned to your pack under the new id.

The recipe cannot be moved. Mason Line puts 2 sticks + 1 rope on the crafting mat, and if another mod claims the same pair, the mat holds both recipes with no way to pick between them. Nothing breaks, but you may get the other mod's item instead of the bundle.

## How it works

Harmony postfix on `TargetInfo.CalcRelativePlacePosition` projects `PlacePosition` onto the nearest string line (within 30 cm to the side, between the stakes). The build preview reads that field. So does the check that decides whether the placement is allowed, and so does the log that finally drops, which is why they never disagree. There's no grid involved, and nothing registers a snap node. The stakes aren't custom buildables either.

## Install

Requires [RedLoader](https://github.com/ToniMacaroni/RedLoader).

Unpack the archive into the game folder, the one holding `SonsOfTheForest.exe`. The zip carries its own `Mods/` folder, so the files land where the loader looks for them:

```
Mods/
  MasonLine.dll
  MasonLine/
    manifest.json
```

## Build from source

```
git clone https://github.com/Cyace84/sotf-mason-line
cd sotf-mason-line
```

`lib/` holds the compile-time references and is not in the repo. RedLoader generates them into your game folder the first time it runs, so install RedLoader and launch the game once, then:

```
GAME="/path/to/steamapps/common/Sons Of The Forest"
mkdir -p lib
cp "$GAME"/_RedLoader/Game/*.dll lib/    # Assembly-CSharp, Endnight.*, Sons.*, Unity modules
cp "$GAME"/_RedLoader/net6/*.dll lib/    # RedLoader, SonsSdk, Il2CppInterop, 0Harmony
rm -f lib/dobby.dll lib/Splash.dll       # native, not referenceable
```

The csproj references every DLL in that folder and copies none of them into the build. If you keep several SOTF mods around, one shared `lib/` symlinked into each repo also works.

```
dotnet build        # produces bin/Debug/MasonLine.dll
./deploy.sh         # builds + copies the DLL and manifest into the game's Mods/
```

## Notes

- **Co-op works, and lines belong to whoever plants them.** Tested in a peer-to-peer game, the kind you start from the Multiplayer menu. You see your own stakes, and only your own placements snap to them. The logs themselves are placed by the game as usual, so the wall everyone ends up looking at is the same one. Dedicated servers are untested. Reports welcome.
- Lines are not part of the save. Stake one out, save, and on loading that save the line is gone while its bundle is back in your pack.
- Putting the tool away with only the first stake planted pulls that stake back out. Plant it again when you return; nothing is charged until a line is finished.
- Ships three crash guards. Two are game-wide: one skips a vanilla impact sound when the collider would crash `Physics.ClosestPoint`, the other intercepts a renderable callback that faults on managed-injected items. The third only ever touches this mod's own item.

## Shout outs

Thanks to ToniMacaroni for [RedLoader and SonsSdk](https://github.com/ToniMacaroni/RedLoader), and to the BepInEx team for HarmonyX and Il2CppInterop, which the loader is built on.

## License

MIT
