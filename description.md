# Mason Line

A stake-and-rope guide line you craft from 2 sticks and 1 rope. Plant a stake where a wall should start, plant another where it should end, and a cord pulls tight between them. Set a log down beside the cord and it snaps onto it, so the wall runs stake to stake instead of wherever the first log happened to point.

That last part is the actual problem. Sons of the Forest stacks logs perfectly: every log after the first snaps onto the one before it. But the direction comes from the first log, dropped by hand with nothing to line it up against. A degree off there and ten logs later the wall is level, solid, and half a meter to the left of the rock you were building toward. The angle cannot be nudged afterwards. You tear it down and try to eyeball it better.

The sane fix is to place that first log more carefully. I wrote a Harmony patch instead.

## Controls
- Left-click while holding the Mason Line plants a stake at the crosshair. First click = stake A, second = stake B, cord runs between them.
- Hold Dismantle while looking at a stake to collect the line. The bundle returns to your inventory. That is the vanilla action under Construction in the controls menu, bound to C by default.

No custom keybinds. The mod adds no hotkeys of its own and reads your existing bindings: left-click places, and the Dismantle action collects, so rebinding Dismantle moves this too.

## Multiple lines
Each crafted bundle is one line, and they all snap at the same time: a log goes to whichever cord is closest. Aim the first stake of a new line at one already standing and it lands exactly on it, so a run can turn a corner and carry on from the same point. Nothing squares up the angle between them, though, so that part is yours to judge.

## What snaps
More than logs. Anything the game lets you set down loose on open ground follows it: logs flat or standing, standing sticks, campfires, rocks. Guidebook blueprints, the ghosts you build up piece by piece like a bed, do not snap; the line guides loose pieces only.

## Settings
The mod menu writes `UserData/MasonLine.cfg`.
- Snap distance, default 0.3 m: how far to the side of the cord a placement still gets pulled onto it. Raise it if logs miss a cord you are clearly aiming at, lower it if the line grabs logs you meant to set down beside it.
- Reach past the stakes, default 0.1 m: how far beyond each stake the cord keeps working, so a wall can finish flush with its stake.
- Stake magnet, default 0.3 m: how close to a standing stake you have to aim the first stake of a new line before it lands exactly on it, with 0 leaving every stake to your own aim.

All three apply the moment you change them. The item id is a fourth setting, kept in the file only; see Compatibility below.

## Compatibility
The bundle registers as item id 9417. If another mod already owns that id, Mason Line refuses to register rather than overwrite it, and the log says so: `item id 9417 is already taken by ...`. Open `UserData/MasonLine.cfg`, set `overrideItemId = true` and `itemId` to another whole number above 1000, then restart the game. The id is read once at startup, so nothing changes until you do. It is kept out of the settings panel on purpose: it repairs a single rare case that the log names for you, and setting it by accident costs you every bundle in your pack.

Bundles lying in your pack were written into the save under the old id and will not come back after the change. Bundles staked out as lines will: stake them out, save, then restart, and they are returned to your pack under the new id.

The recipe cannot be moved. Mason Line puts 2 sticks + 1 rope on the crafting mat, and if another mod claims the same pair, the mat holds both recipes with no way to pick between them. Nothing breaks, but you may get the other mod's item instead of the bundle.

## What it does under the hood
One Harmony postfix on `TargetInfo.CalcRelativePlacePosition`. When a free log's placement lands within 30 cm of a cord and between the two stakes, its position and axis get projected onto the line. The build preview, the check that decides whether the placement is allowed and the log that finally drops all read that one field. There is no grid involved, and nothing registers a snap node.

The mod ships three crash guards, and two of them are game-wide rather than specific to the guide line. One patches `ObjectPhysicsInteractionSfx.TryTriggerHardSurfaceImpact` against a vanilla bug where `Physics.ClosestPoint` hits a non-convex mesh collider in the backpack physics callback and kills the process. The other intercepts a renderable callback that faults on items injected from managed code. The third only ever touches this mod's own item.

## Install
1. Install [RedLoader](https://github.com/ToniMacaroni/RedLoader) and run the game once.
2. Unpack the archive into the game folder, the one holding `SonsOfTheForest.exe`. The zip carries its own `Mods/` folder, so you end up with `Mods/MasonLine.dll` and a `Mods/MasonLine/` folder holding the manifest.
3. Start the game. The recipe shows up on the crafting mat as Mason Line.

## Notes
- Co-op works, one line per player. Tested in a peer-to-peer game, the kind you start from the Multiplayer menu. Everyone can plant lines, but a line belongs to whoever planted it: you see your own stakes, and only your own placements snap to them. The logs themselves are placed by the game as usual, so the wall everyone ends up looking at is the same one. Dedicated servers are untested. Reports welcome.
- The cord is a visual guide, not a physical object. NPCs and the player walk through it.
- Lines are not part of the save. Stake one out, save, and on loading that save the line is gone while its bundle is back in your pack.
- Putting the tool away with only the first stake planted pulls that stake back out. Plant it again when you return; nothing is charged until a line is finished.

## Bug reports
Open an issue on [the GitHub tracker](https://github.com/Cyace84/sotf-mason-line/issues) or leave a comment here. Attach `_Redloader/Latest.log` from the game folder. It has the mod's diagnostic lines.

## Source
[github.com/Cyace84/sotf-mason-line](https://github.com/Cyace84/sotf-mason-line) (MIT)

## Shout outs
Thanks to ToniMacaroni for [RedLoader and SonsSdk](https://github.com/ToniMacaroni/RedLoader), and to the BepInEx team for HarmonyX and Il2CppInterop, which the loader is built on.
