# Building Laser (Sons of the Forest)

A dead-straight guide line for building. Lay a "laser" from your aim, and the build ghost of a free
log snaps to it — so the **first** log of a wall lays perfectly straight (the rest snap to it natively).
No grid, no custom buildable; one placement-position override.

## Controls (dev v1)
- **L** — lay the guide line from where you're aiming
- **K** — toggle snap (ghost sticks to the line)
- **J** — clear the line

## How it works
Harmony postfix on `Construction.TargetInfo.CalcRelativePlacePosition` overrides `TargetInfo.PlacePosition`
— the single field that the build preview, validation, and final placement all read — projecting it onto
the guide line. Ground placement is allowed everywhere, so no snap-node registration or validation
hacking is needed.

## Build
`./deploy.sh` builds and copies `BuildingLaser.dll` + manifest into the game `Mods/` folder.
`lib/` is a symlink to the game's managed DLLs.
