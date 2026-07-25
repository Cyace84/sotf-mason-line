# Mason Line

A craftable string line for Sons of the Forest. Stretch it between two stakes, and free logs
snap to it. The first log of a wall lands dead-straight, no eyeballing.

## How to use

1. **Craft** the Mason Line: 1 stick + 1 rope (combine on the crafting mat).
2. **Equip** it and aim at the ground. A ghost stake shows where it'll land.
3. **Left-click** to plant stake A, then again for stake B. A string runs between them.
4. Place free logs near the string. The build ghost snaps onto the line automatically.
5. **Hold Dismantle** while looking at a stake to pull the line out. The kit goes back to your
   inventory. That's the vanilla action listed under Construction in the controls menu, bound to C
   by default.

Each crafted kit is one line. Craft more kits for more lines. They all work at the same time.

## How it works

Harmony postfix on `TargetInfo.CalcRelativePlacePosition` projects `PlacePosition` onto the
nearest string line (within 2 m, between the stakes). That's the single field the build preview,
validation, and final placement all read, so the ghost, the green/red check, and the placed log
all move together. No grid, no custom buildable, no snap-node registration.

## Install

Requires [RedLoader](https://github.com/ToniMacaroni/RedLoader).

Drop `MasonLine.dll` into the game's `Mods/` folder. That's it.

## Build from source

```
git clone https://github.com/Cyace84/sotf-mason-line
cd sotf-mason-line
```

`lib/` holds the compile-time references and is not in the repo. RedLoader generates them into
your game folder the first time it runs, so install RedLoader and launch the game once, then:

```
GAME="/path/to/steamapps/common/Sons Of The Forest"
mkdir -p lib
cp "$GAME"/_RedLoader/Game/*.dll lib/    # Assembly-CSharp, Endnight.*, Sons.*, Unity modules
cp "$GAME"/_RedLoader/net6/*.dll lib/    # RedLoader, SonsSdk, Il2CppInterop, 0Harmony
rm -f lib/dobby.dll lib/Splash.dll       # native, not referenceable
```

The csproj references every DLL in that folder and copies none of them into the build. If you
keep several SOTF mods around, one shared `lib/` symlinked into each repo also works.

```
dotnet build        # produces bin/MasonLine.dll
./deploy.sh         # builds + copies the DLL and manifest into the game's Mods/
```

## Notes

- **Tested in singleplayer.** Multiplayer is untested — reports welcome.
- No custom keybinds. The mod adds no hotkeys of its own and reads your existing bindings: left-click
  places a stake, the Dismantle action collects the line. Rebind Dismantle and the mod follows it.
- Includes a crash guard for a vanilla bug (ClosestPoint on non-convex MeshColliders in the
  backpack physics callback). This patch is game-wide, not specific to the string line.

## License

MIT
