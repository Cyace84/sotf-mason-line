# Mason Line

A craftable string line for Sons of the Forest. Stretch it between two stakes, and free logs
snap to it. The first log of a wall lands dead-straight, no eyeballing.

## How to use

1. **Craft** the Mason Line: 1 stick + 1 rope (combine on the crafting mat).
2. **Equip** it and aim at the ground. A ghost stake shows where it'll land.
3. **Left-click** to plant stake A, then again for stake B. A string runs between them.
4. Place free logs near the string. The build ghost snaps onto the line automatically.
5. **Hold your Dismantle key** (default C) while looking at a stake to pull the line out. The kit goes back to your inventory.

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

`lib/` must point to a directory containing the game's managed DLLs and RedLoader assemblies
(Il2CppInterop.Runtime, RedLoader, SonsSdk, etc.). The repo ships a symlink; adjust it to
your local paths or copy the DLLs in.

```
dotnet build        # produces bin/MasonLine.dll
./deploy.sh         # builds + copies the DLL and manifest into the game's Mods/
```

## Notes

- **Tested in singleplayer.** Multiplayer is untested — reports welcome.
- No custom keybinds: controls read your own vanilla bindings. Left-click places; your Dismantle key
  (default C) collects. Rebind Dismantle in the game's controls and the mod follows.
- Includes a crash guard for a vanilla bug (ClosestPoint on non-convex MeshColliders in the
  backpack physics callback). This patch is game-wide, not specific to the string line.

## License

MIT
