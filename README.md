# Mason Line

A craftable string line for Sons of the Forest. Stretch it between two stakes, and free logs snap to it. The first log of a wall lands dead-straight, no eyeballing.

## How to use

1. **Craft** the Mason Line: 1 stick + 1 rope (combine on the crafting mat).
2. **Equip** it and aim at the ground. A ghost stake shows where it'll land.
3. **Left-click** to plant stake A, then again for stake B. A string runs between them.
4. Place free logs near the string. The build ghost snaps onto the line automatically.
5. **Hold Dismantle** while looking at a stake to pull the line out. The kit goes back to your inventory. That's the vanilla action listed under Construction in the controls menu, bound to C by default.

Each crafted kit is one line. Craft more kits for more lines. They all work at the same time.

## How it works

Harmony postfix on `TargetInfo.CalcRelativePlacePosition` projects `PlacePosition` onto the nearest string line (within 2 m, between the stakes). The build preview reads that field. So does the green/red validation, and so does the log that finally drops, which is why they never disagree. There's no grid involved, and nothing registers a snap node. The stakes aren't custom buildables either.

## Install

Requires [RedLoader](https://github.com/ToniMacaroni/RedLoader).

Drop `MasonLine.dll` into the game's `Mods/` folder, and `manifest.json` into `Mods/MasonLine/` next to it:

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

- **Tested in singleplayer.** Multiplayer is untested. Reports welcome.
- Ships three crash guards. Two are game-wide: one skips a vanilla impact sound when the collider would crash `Physics.ClosestPoint`, the other intercepts a renderable callback that faults on managed-injected items. The third only ever touches this mod's own item.

## License

MIT
