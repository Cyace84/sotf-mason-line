# Builder's String Line

A craftable string line for Sons of the Forest. Stretch it between two stakes, and free logs
snap to it — the first log of a wall lays dead-straight without eyeballing.

## How to use

1. **Craft** the Builder's String Line: 1 stick + 1 rope (combine on the crafting mat).
2. **Equip** it and aim at the ground — a ghost stake shows where it'll land.
3. **Left-click** (or L) to plant stake A, then again for stake B. A string runs between them.
4. Place free logs near the string. The build ghost snaps onto the line automatically.
5. **Hold C** while looking at a stake to pull the line out. The kit goes back to your inventory.

Each crafted kit is one line. Craft more kits for more lines — they all work at the same time.

## How it works

Harmony postfix on `TargetInfo.CalcRelativePlacePosition` projects `PlacePosition` onto the
nearest string line (within 2 m, between the stakes). That's the single field the build preview,
validation, and final placement all read — so the ghost, the green/red check, and the placed log
all move together. No grid, no custom buildable, no snap-node registration.

## Install

Requires [RedLoader](https://github.com/ToniMacaroni/RedLoader).

Drop `BuildingLaser.dll` into the game's `Mods/` folder. That's it.

## Build from source

```
git clone https://github.com/Cyace84/sotf-building-laser
cd sotf-building-laser
```

`lib/` must point to a directory containing the game's managed DLLs and RedLoader assemblies
(Il2CppInterop.Runtime, RedLoader, SonsSdk, etc.). The repo ships a symlink — adjust it to
your local paths or copy the DLLs in.

```
dotnet build        # produces bin/BuildingLaser.dll
./deploy.sh         # builds + copies the DLL and manifest into the game's Mods/
```

## Notes

- **Tested in singleplayer.** Multiplayer is untested — reports welcome.
- Hotkeys (left-click / L / hold-C) are hardcoded for now.
- Hold-C also crouches. The crouch happens, the line still collects — no conflict in practice.
- Includes a crash guard for a vanilla bug (ClosestPoint on non-convex MeshColliders in the
  backpack physics callback). This patch is game-wide, not specific to the string line.

## License

MIT
