# RealmSculptor: Realm's monuments in the real game

`plugins/RealmSculptor.cs` (Oxide 2.0.3867, C# 3) builds Realm's own 3D monuments out of the game's ordinary building blocks. The server places and paints blocks the way the game's own `/build` command does, and the game already sends blocks and their colours to every client and saves them with the world. Nobody's game install changes. The sculptures are made by code in `art/sculptures/src` with the tools in `art/tools/sculptor` (see [`art/sculptures/README.md`](../../art/sculptures/README.md)).

Status: **compiles with 0 errors against the real 2.0.3867 DLLs and is behaviour-tested against mocks (159 checks pass, including every real sculpture file placed and taken down, and a check that every member it binds by reflection exists in the real `Assembly-CSharp.dll`). It has never run on a live server.** Every fact below is tagged **[CODE]** (read in the decompiled 2.0.3867 `Assembly-CSharp.dll`; only type and member names are cited, no code is copied) or **UNVERIFIED** with the in-game step that settles it. The [first test](#first-test-on-the-owners-server) is the place to start.

RealmSculptor writes nothing to the Chronicle, so **no new Chronicle event types are needed**. Its one permission is `realmsculptor.admin`; `/sculpt` is a staff-only command and is not listed in the player hub (`/realm`).

---

## Quick start

1. Copy the sculptures to the server: `node art/tools/sculptor/cli.mjs export <server>\oxide\data` (writes `oxide\data\RealmSculptor\*.json`). Realm Steward's plugin deploy does not copy them yet.
2. Grant the permission: `oxide.grant user <SteamID64> realmsculptor.admin` (or the `realm_admin` group, see `docs/community/ops/staff-roles-and-permissions.md`).
3. In game: `/sculpt list`, stand where the monument's front should face you, `/sculpt preview heralds-pillar`, then `/sculpt place heralds-pillar`.
4. `/sculpt undo` takes your last placement down and puts the ground back as it was.

Sculptures belong **inside a crest zone** (a staff crest in the capital is ideal). In unclaimed land the game decays blocks and pays out salvage for them; see [Land](#land-crests-decay-and-salvage).

## Commands

All need `realmsculptor.admin`.

| Command | What it does |
|---|---|
| `/sculpt list` | The sculptures loaded from `oxide/data/RealmSculptor/`, with size and block count, and every file that was refused and why. |
| `/sculpt preview <id> [turn] [+N\|-N]` | A dry run where you stand: size in blocks and metres, the corner cell, the turn, the direction, the build time, whose land it stands on, and how many existing blocks or cells of another sculpture are in the way. Places nothing. |
| `/sculpt place <id> [turn] [force] [unclaimed] [+N\|-N]` | Builds it in front of you. `turn` is 0-3 quarter-turns or `auto` (default: the front faces you). `+N`/`-N` raises or sinks it N blocks. `force` replaces existing blocks (recorded and put back on undo; doors, gates, windows and other sculptures are never replaced). `unclaimed` is needed to build outside a crest zone, and only works with `AllowUnclaimedLand`. |
| `/sculpt undo` | Takes down your most recent standing (or still building) placement. |
| `/sculpt remove <n>` | Takes down placement `#n` (`/sculpt placed` shows the numbers). |
| `/sculpt placed` | The 12 newest standing placements: number, name, corner, state, blocks, lost blocks, protection, who placed it. |
| `/sculpt status` | Totals, the running job, and whether the block API, placing, protection and the decay guard are on. |
| `/sculpt protect <n> on\|off` | Protection for one placement. |
| `/sculpt repair <n>` | Checks every cell of a standing placement against the world and puts back what is missing (lost to a collapse, decay, protection being off, or not taken by the game). A cell someone has built into since is left alone. |
| `/sculpt materials` | Writes the server's real material and shape table to `oxide/data/RealmSculptor-materials.json` and prints a summary. Run it once on a new server. |
| `/sculpt reload` | Reads the sculpture files again. |

## Where a sculpture goes

You stand on the cell `RootCubeGrid.WorldToLocalCoordinate(your position)`; the direction you face picks one of four quarter-turns (`Entity.Forward`, nearest axis). The footprint is centred on your line of sight, `DistanceAhead` (2) empty cells in front of you, and its bottom layer is the cell your feet are in, so the plinth sits a little into the ground. With `turn` = `auto` the sculpture's front (`-z` in its file) faces you; an explicit turn keeps the position and turns it. Every block's own rotation turns with it (the table in the plugin and in `art/tools/sculptor/rotations.mjs`, checked against each other).

## How a placement runs

1. `place` works out every cell, checks the material and shape of each against the server's tilesets, checks the land, and refuses if blocks are in the way (unless `force`). The plan is written to `oxide/data/RealmSculptor.json` before the first block.
2. Every `TickSeconds` (0.2 s) the plugin makes at most `BlocksPerTick` (25) game calls, placing and painting together, bottom layer first. That is 125 calls a second; the Herald's Pillar takes about 4 seconds, a house monument about 18.
3. Each block is placed with `RootCubeGrid.PlaceCubeAtLocal(cell, material, shape, rotation, collectPreviousCube: false, isOwnedByPlacer: false, delayTime: PlaceDelaySeconds)`.
4. `ColourDelaySeconds` (1.5 s) later the plugin checks that the game really holds the block there, then paints it with `RootCubeGrid.ColorCubeAtLocal`. A block the game did not take is recorded as failed (shown in the finish line; `/sculpt repair` tries again).
5. When every cell is done the placement is `standing`, the admin is told, and (with `AnnounceRaised`) the realm hears it.

Jobs run one at a time, oldest first. A restart in the middle carries on from the data file. `undo` in the middle of a build stops it and takes down what was placed. Removal runs top layer first and restores each cell to exactly what was there: air, or the replaced block with its shape, rotation and colour. A cell that has changed since it was placed is left alone and counted.

## Protection

New placements are protected (`ProtectSculptures`). For a protected cell:

| Threat | What the plugin does | Evidence |
|---|---|---|
| Weapons, siege | `OnCubeTakeDamage`: `Damage.Amount`, `ImpactDamage`, `MiscDamage` = 0 and `Cancel()`. The attacker is told once per `MessageCooldownSeconds` that it is a monument. | [CODE] `CubeListener.OnCubeDamage` calls the hook, then subtracts `Damage.Amount` from `BlockHealth.CurrentHealth`; RealmWarden blocks raid damage the same way. |
| Removing, replacing, building into it | `OnCubePlacement`: `Cancel()` for any player's event on that cell (the plugin's own calls pass). | [CODE] `CubeListener.OnCubePlace` calls the hook first and reads `Cancelled` after; removing a block is a place event with material 0 (`CollectModule`). |
| Decay | An `EventManager` subscriber (VeryEarly) takes protected cells out of each `MassCubeDestroyEvent`, and cancels one that is left empty. | [CODE] `DecaySystem.CleanBlocks` removes unclaimed blocks only through that event; `CubeListener.OnMassCubeDestroy` skips a cancelled one. |
| Salvage in unclaimed land | **Let through.** | [CODE] `SalvageSupplier` (VeryEarly) has already given the resources by the time any plugin hook runs. Blocking the damage would make a protected block an endless resource node, so in unclaimed land a salvage hit wears the sculpture down like any other build. Inside a crest zone the game pays no salvage at all (`SalvageSupplier.CheckCrest`). |
| Collapse | Nothing to block; `/sculpt repair` puts blocks back. | [CODE] `BlockCollapsing` removes floating chunks with `Bypass_PlaceCubeAtLocal`, which no hook sees. Every sculpture is face-connected to its bottom layer (the tools check it). |

`OnCubeDestroyed` records a protected cell that was destroyed anyway (protection off, or a path no hook covers), so `/sculpt placed` shows it as lost.

## Land: crests, decay and salvage

| Fact | Tag |
|---|---|
| A place, colour, collect or destroy event **sent by a player** is cancelled unless the player owns the location or it is a transition area: `CrestScheme.OwnsLocation(sender, position * 1.2, ...)`. Events the server raises itself skip that check (`!theEvent.IsSender`). So the server may build anywhere, and inside a crest zone only that crest's group can change blocks. | [CODE] `CubeListener.OnCubePlace`, `OnCubeColor`, `OnCubeCollect`, `OnCubeDestroy` |
| Blocks outside every crest zone decay when the server setting `decay` is on: each placement pushes that crest cell's timestamp forward, and when it runs out all its blocks go in one `MassCubeDestroyEvent`. `blockDecay` (seconds) sets the time; below zero uses the game's default. | [CODE] `DecaySystem.HandleBlockPlaceEvent`, `CheckTimeout`, `CleanBlocks`; `ServerSettingsFile` keys `decay`, `blockDecay` |
| Salvage only happens in unclaimed land. | [CODE] `SalvageSupplier.OnCubeDamage`, `CheckCrest` |
| The plugin reads land with `SocialAPI.Get<CrestScheme>().IsEmpty(world)` and `CurrentCrestGroup(world)` for every footprint column. `place` refuses unclaimed columns unless `AllowUnclaimedLand` is true **and** the admin adds `unclaimed`. | [CODE] `CrestScheme.IsEmpty(Vector3)`, `CurrentCrestGroup(Vector3)` |
| Placing inside another group's crest zone is allowed (the preview names the group). That group cannot then change the protected blocks, but they stand on its land. | Policy |

## Game facts used

### The block grid

| Fact | Tag |
|---|---|
| The world's blocks live in `BlockManager.DefaultCubeGrid` = `BlockManager.GetGrid(0)`, the first `RootCubeGrid` (a `PageGrid`) registered in `BlockManager.AllCubeGrids`. A plugin reads it as a static property. | [CODE] `BlockManager.DefaultCubeGrid`, `RootCubeGrid.Start` |
| One block is 1.2 m: `BlockManager.BLOCK_SIZE` = 1.2. | [CODE] |
| Grid cells are integers (`Vector3Int`). `WorldToLocalCoordinate` rounds (`Mathf.RoundToInt`), so cell n spans (n - 0.5) to (n + 0.5) blocks; `LocalToWorldCoordinate` is the cell centre (`Transform.TransformPoint`). | [CODE] `RootCubeGrid.WorldToLocalCoordinate`, `Vector3Int(Vector3)` |
| The default grid sits at the world origin with a scale of 1.2: the game's own crest checks use `position * 1.2f` directly. | Inferred from [CODE] `CubeListener`; UNVERIFIED (compare `/sculpt preview` corner with your position) |
| `PlaceCubeAtLocal` does nothing when the cell's page cannot be created (`PageGrid.CanPlaceCubeAtLocal` → `GetCellAt(..., shouldCreate: true)` is null), for example outside the world. The plugin finds such cells when it checks before painting. | [CODE] |
| On a dedicated server a placement raises `CubePlaceLocalEvent` (applied at the end of the frame, `CubeListener.DelayedFinalize`) and `CubePlaceEvent`, which goes to the clients near the cell (`EventReceiverController.GetEventRecievers`); a client applies it after the event's `Time` (`CubeListener.PlaceCubeDelayed`). Players who come later get the block from the page data. | [CODE] `RootCubeGrid.PlaceCubeAtLocal`, `CubeListener` |
| `delayTime` 0 means "use the material's build time" (`CubeDelayProvider.GetDelay`); the plugin sends `PlaceDelaySeconds` (0.05 s) instead, as the game does for removals (`TilesetColliderCube.HandleDeath`: 0.017 s). | [CODE] |
| `collectPreviousCube: false` raises no `CubeCollectEvent` (no item for a replaced block) and marks the event `CausedByDestruction`, which makes clients skip the build scaffold (`ProgressCubeListener`) and the inventory listener return early. | [CODE] `RootCubeGrid.PlaceCubeAtLocal`, `BlockInventoryListener.OnCubePlaceEarly` |
| The server needs no block item: `InventoryUtil.RemoveTileset` returns `player.IsServer` when the sender has none. Events the server raises have `Sender = Player.Local`, the server player. The game's own `/build` command (`BlockCommandHandler.Build`, permission `codehatch.blocks.build`) places blocks this way. | [CODE] |
| `CubePlaceEvent` carries the grid id, position, material, prefab, rotation (as an index into the rotation table, written only for prefab blocks), time and `CausedByDestruction`; its permission string is `rok.blocks.place` (checked for events players send). | [CODE] `CubePlaceEvent.Write`, `PermissionString` |
| Colour: `ColorCubeAtLocal` sets the colour and raises `CubeColorEvent` to nearby clients. A colour set before the block exists is lost: `CubeInfo.UpdateMaterial` resets `CubeColor` to the tileset's `DefaultColor` whenever the block changes. Hence the delay before painting. | [CODE] `RootCubeGrid.ColorCubeAtLocal`, `CubeInfo.UpdateMaterial`, `CubeListener.OnCubeColor` |
| Colours are saved with the world: `NormalBlockLoader` and `BlockPrefabLoader` write R, G, B, A for every block, and block health too (`CubeInfo.Serialize` → `BlockHealth.Serialize`). | [CODE] |
| Players paint blocks through `ColorableGrid.SetColor`, which keeps the block's alpha. The plugin paints with alpha 255 and restores a replaced block's own alpha. | [CODE] |

### Materials

A block's material is a tileset id: `TilesetLibrary.GetTilesetWithID(id)` returns `Tilesets[id - 1]`, so ids start at 1 and 0 is air [CODE]. The tilesets, their names and which ones exist are **scene data**, not code, so the ids below are UNVERIFIED on our server. They come from a third-party Oxide plugin for this game, `LevelSystem.cs` (`GetCubePlaceLevel`, lines 1575-1597 in github.com/RustServers-IO/oxideplugins at commit 4de21d4), which maps block placements to levels by material id:

| Id | Role in `art/tools/sculptor/materials.json` | Name in that plugin | Used by sculptures for |
|---|---|---|---|
| 1 | `cobblestone` | Cobblestone | plinths, dark stone |
| 2 | `stone` | Stone | stone, black stone, house metals |
| 3 | `clay` | Clay | house fields, cloth, parchment, red |
| 4 | `sod` | Sod | (unused) |
| 5 | `thatch` | Thatch | gold |
| 6 | `spruce` | Spruce branches | (unused) |
| 7 | `wood` | Wood | frames |
| 8 | `log` | Log | leather, trunks |
| 9 | `reinforced` | Reinforced (iron) | iron and steel |

The plugin never trusts these blindly: a sculpture file names each material by role, and the plugin maps the role through `MaterialIds` in its config. `/sculpt materials` writes the real table (tileset name, default colour, build time, quality, paintable parts, shapes, and the block items with their material and shape from `InvBlueprints` / `TilesetBlueprint`), so a wrong id is fixed in the config without rebuilding anything. A material the server does not have is refused at `place`.

**Which materials take colour:** each tileset's materials carry `TilesetMaterialInfo.Colorable`; `TilesetLibrary.ColorBinding` leaves a material that is not colourable unpainted [CODE]. Which tilesets that is lives in scene data: UNVERIFIED until `/sculpt materials` (the `ColourableParts` / `FixedParts` counts). A block whose material does not take paint keeps its texture's colour, which is what the `unpainted` previews show. `Paint: false` places everything unpainted.

### Shapes (prefabs)

| Id | `CubeInfo.PrefabType` [CODE] | Used by sculptures |
|---|---|---|
| 0 | Block | yes |
| 1 | Stair | shape test only |
| 2 | Ramp | yes (the shape fitter's slope) |
| 3-8 | RampVar1-6 | shape test only |
| 9 | Peak | shape test only |
| 10-14, 16 | Door, GarageDoor, Window, DoorVar1, DoorVar2, DoorVertical | never |
| 15 | ThinWall | shape test only |
| 255 | MultiBlock (a cell taken by a door's other parts) | never |

- Single-block shapes are 0-9 and 15 (`CubeInfo.IsPrefabTypeSingleBlock`) [CODE]. Each tileset has its own set (`OctTileset.SpecialPeices`, looked up by `GetPrefabWithID`; `OctPrefab.IsMultiBlock()` and `BlockOffsets` describe multi-block ones) [CODE]. A material without a shape gets a plain block: placing a missing shape would fail inside the game's code (`CubeInfo.CentralPrefab` instantiates the prefab). `SimplifyShapes: true` places every shape as a plain block.
- Rotations: `CubeInfo.PossibleRotations` is a table of 24 `Quaternion.Euler` values; `CubeInfo.GetIndexOfRotation` finds the nearest within 10 degrees [CODE]. Turning a sculpture maps each index to another (both tables are in the plugin and in `rotations.json`, and the tests check them three ways).
- `OctPrefab.PermittedRotations` limits what players may pick; whether the server's own placements are held to it is UNVERIFIED (the shape test shows it).
- What each shape looks like is scene data. `art/tools/sculptor/shapes.json` records what the tools **assume** for rotation 0 (the ramp rises towards +z with its flat base down). UNVERIFIED: the [shape test](#shape-test).

### Server blocks and player blocks

| Question | Answer | Tag |
|---|---|---|
| Ownership | Blocks have no owner. Who may change a block is decided by the crest zone it stands in (above). `isOwnedByPlacer` only feeds two client-side filters (`LastPlacedFilter`, `CrestRemoveBlockFilter`) that let a player take back the block they just placed; the plugin passes `false`. | [CODE] |
| Decay | Same as player blocks: decays in unclaimed land when `decay` is on (the decay guard keeps protected cells), never inside a crest zone. | [CODE] `DecaySystem` |
| Damage | Same health as player blocks (`BlockHealth`, saved with the world). `BlockHealth.IsInvincible` exists but is not saved (`Serialize` writes only `CurrentHealth`) and a full-health block's collider object is pooled away (`TilesetColliderCube.CanCollapse`), so the plugin protects with the damage hook instead. | [CODE] |
| Crest claims | Placing a crest near a sculpture puts it inside that group's zone: its members could then remove unprotected sculpture blocks. Protected ones they cannot. | [CODE] `CubeListener`, plugin |
| Floating | The server keeps scanning pages for chunks not grounded (`PageGrid.CheckForFloatingRoutine` → `BlockCollapsing.BlockPath`): a block is grounded if a 1.2 m sphere around it touches the ground layers (`BlockCollapsing.IsBlockGrounded`). With `blockCollapsing` on, floating chunks fall. A sculpture's bottom layer must touch the ground: do not lift it into the air with `+N`. | [CODE] |

### Hooks and calls

| Use | Member | Tag |
|---|---|---|
| Damage | `OnCubeTakeDamage(CubeDamageEvent)`: `Position`, `GridID`, `Damage.Amount`, `ImpactDamage`, `MiscDamage`, `DamageTypes`, `DamageSource.Owner` | [CODE] `CubeListener.OnCubeDamage`; hook name in `Assembly-CSharp.dll` |
| Building | `OnCubePlacement(CubePlaceEvent)`: `Position`, `GridID`, `Sender` | [CODE] `CubeListener.OnCubePlace` |
| Destroyed | `OnCubeDestroyed(CubeDestroyEvent)` | [CODE] `CubeListener.OnCubeDestroy` |
| Decay | `EventManager.Subscribe<MassCubeDestroyEvent>(EventSubscriber, EventHandlerOrder.VeryEarly)`; `MassCubeDestroyEvent.Position` has a setter | [CODE]; RealmTreasury subscribes to `ItemPassEvent` the same way |
| Unity types | `Quaternion` and `Color32` are handled as `object` through reflection (`PlaceCubeAtLocal`'s and `ColorCubeAtLocal`'s parameter types, `CubeInfo.PossibleRotations`, `CubeInfo.Rotation`, `CubeInfo.CubeColor`, `CubeInfo.GetIndexOfRotation`), because the compile check's UnityEngine stub has only `Vector3`. The logic tests read the real DLL's metadata and fail if any of these names or signatures is different. The shipped `Oxide.CSharp.dll` has no namespace sandbox (its only list is a blacklist of three assembly names), so `System.Reflection` compiles on the server. | [CODE]; reflection at run time UNVERIFIED |

## Config (`oxide/config/RealmSculptor.json`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | false: nothing is placed or removed (protection still works). |
| `BlocksPerTick` | 25 | Game calls (place + paint) per tick, 1-200. |
| `TickSeconds` | 0.2 | 0.05-2. |
| `PlaceDelaySeconds` | 0.05 | Build delay sent to clients with each block (0.02-5). Larger values show the game's build pacing. |
| `ColourDelaySeconds` | 1.5 | Wait between placing and painting (0.2-30). Raise it if colours are missing for distant or slow clients. |
| `Paint` | true | false: every block keeps its material's own colour. |
| `SimplifyShapes` | false | true: slopes and other shapes become plain blocks. |
| `ProtectSculptures` | true | Protection for new placements, and the master switch for all protection. |
| `AllowUnclaimedLand` | false | Allows `place ... unclaimed` outside crest zones. |
| `GuardDecay` | true | Subscribe the decay guard at start. |
| `AnnounceRaised` | false | "Herald: A new monument stands in the realm: ..." when a placement is finished. |
| `DistanceAhead` | 2 | Empty cells between the admin and the sculpture (0-20). |
| `MaxBlocksPerSculpture` | 4000 | Larger files are refused (1-20000). |
| `MaxPlacements` | 200 | Standing placements on record; old removed records are pruned first (10-2000). |
| `MessageCooldownSeconds` | 10 | Between "this is a monument" replies to one player. |
| `SaveIntervalSeconds` | 20 | How often a running job saves its progress. |
| `MaterialIds` | `cobblestone` 1 ... `reinforced` 9 | Role → the server's material id. |

## Data files

- `oxide/data/RealmSculptor/<id>.json`: the sculptures (format in [`art/sculptures/README.md`](../../art/sculptures/README.md)). A file whose `id` is not its name, has a wrong format, a block outside its size, a shape that is not a single block, a bad colour, a repeated cell or too many blocks is refused and listed in `/sculpt list`.
- `oxide/data/RealmSculptor.json`: every placement with all its cells. A cell is `[x, y, z, material, shape, rotation, colour, previous material, previous shape, previous rotation, previous colour, previous alpha, state]` (colours `0xRRGGBB`, -1 = none; state 0 pending, 1 placed, 2 done, 3 skipped, 4 failed, 5 removed, 6 left alone, 7 lost). If the file exists but cannot be read, placing and removing are switched off and the file is **never** overwritten; fix or remove it and reload.
- `oxide/data/RealmSculptor-materials.json`: written by `/sculpt materials`.

## Shape test

`shape-test` puts every single-block shape (rows, front to back: Stair 1, Ramp 2, RampVar1-6 3-8, Peak 9, ThinWall 15) in rotations 0, 1, 2, 3, 4 and 8 (columns, left to right) on a slab, each row its own colour, with a gold post at the back right (+x, +z) corner.

1. Face +z (the way `/sculpt preview` says `+z`), then `/sculpt place shape-test 0`.
2. Compare each shape with `art/sculptures/preview/shape-test-three-quarter.png`. Write down for each row what rotation 0 really looks like (which side is high, which face is flat).
3. Fix `kind` (and `fit`) in `art/tools/sculptor/shapes.json` and the geometry in `shapes.mjs`, rebuild (`cli.mjs build`) and re-preview. Until this is done the tools fit only plain blocks and ramps, and a wrong ramp guess shows as slopes facing the wrong way.
4. `/sculpt undo`.

## First test on the owner's server

On `G:\RealmTest\server`, with at least one player client.

1. **Prepare.** `node art/tools/sculptor/cli.mjs export G:\RealmTest\server\oxide\data`. Deploy the plugins. In the server console: `oxide.grant user <your SteamID64> realmsculptor.admin`. Check the server log for `[RealmSculptor]` errors: a "Block API not available" line means a reflected name is wrong on this build.
2. **Materials.** In game, `/sculpt materials`. Open `oxide\data\RealmSculptor-materials.json`: ids 1-9 should be cobblestone, stone, clay, sod, thatch, spruce, wood, log and reinforced. Note which have paintable parts. Fix `MaterialIds` if needed and reload the plugin.
3. **Land.** Near spawn, place a crest for a staff guild (or use one), so the test is inside a crest zone.
4. **Preview.** Stand on flat ground in that zone facing open space: `/sculpt preview heralds-pillar`. Expect "The space is clear" and "Inside the crest zone of group(s) ...". Check that the corner it names is about 3 cells in front of you.
5. **Place.** `/sculpt place heralds-pillar`. Expect the column to rise in about 4 seconds, and a green "#1 Herald's Pillar stands: 132 blocks placed, 0 skipped, 0 not taken by the game". Look at it from the client: grey fluted column, parchment notice with a red seal on the side facing you, gold band, pennants, dark cap, brazier on top. Note whether the colours show, and on which materials.
6. **A second client** (or the same client after walking 200 m away and back): the pillar is there with the same colours.
7. **Reconnect.** Log out and back in: same.
8. **Restart.** `/realm.save` (or Steward's Save), stop the server, start it again: the pillar stands, colours kept, and `/sculpt placed` still lists #1 with protection on. Hit it: no damage.
9. **Damage.** With a weapon, a siege weapon if you have one, and a salvage tool: the blocks lose no health and you get the "is a monument" line. Try to place a block into it and to remove one of its blocks: refused.
10. **Protection off.** `/sculpt protect 1 off`, break one block, `/sculpt placed` (shows 1 lost), `/sculpt repair 1` (puts it back), `/sculpt protect 1 on`.
11. **Undo.** `/sculpt undo`: the pillar goes, top first, and the ground is as it was. `/sculpt placed` shows nothing.
12. **Then:** the shape test (above), then the Ironbreaker. Write down what you saw in this file and remove the UNVERIFIED tags it settles.

## What is UNVERIFIED

Each with the step that settles it (numbers from the first test).

1. That reflection binds on the server and blocks appear for clients at all: steps 1 and 5.
2. Material ids 1-9 and their names; which materials take paint: step 2.
3. That colours show on clients, survive reconnect and restart, and reach clients who were far away: steps 5-8. If colours are missing nearby, raise `ColourDelaySeconds`; if they are missing for far clients only, check whether the page data carries them (it should, [CODE] `NormalBlockLoader`).
4. That `PlaceDelaySeconds` 0.05 shows no build scaffold and no delay: step 5.
5. That the default grid sits at the origin with scale 1.2 and that `Entity.Forward` is where you face: step 4 (the corner and direction it reports).
6. The protection: damage, salvage inside a crest, placing and removing: step 9. The decay guard: place a test sculpture with `unclaimed` (needs `AllowUnclaimedLand`), set `blockDecay` short, wait, and check it stands.
7. What each shape looks like, and whether server placements ignore `PermittedRotations`: the shape test.
8. Restart in the middle of a build: place a house monument, restart the server during the build, and check it finishes after the restart.
9. Network load: place a house monument (about 1100 blocks, 18 s) with players near and watch for lag; lower `BlocksPerTick` if needed.
10. Whether the ground layers (`BlockCollapsing.GroundLayers`) are only terrain: place on a wooden floor and see if it stands.

## Tests

```
bash plugins/docs/RealmSculptor/logic-tests/run.sh      # 159 checks: the plugin against mocks, plus the real DLL's metadata
node --test art/tools/sculptor/test/*.test.mjs          # the Node tools and every sculpture
node art/tools/sculptor/cli.mjs check                   # sculptures valid, up to date, previewed
bash tools/plugin-compile-check/check.sh                 # C# 3 against the real DLLs
node tools/realm-integration/check.mjs                   # wiring, chat style
```

The logic tests compile `plugins/RealmSculptor.cs` unchanged with `Mocks.cs`, whose block grid copies the timing the decompiled code shows (end-of-frame placement, default colour on a new block, a colour sent too early is lost). They cover loading (every real sculpture file and eight kinds of broken file), the rotation tables against `rotations.json` and against matrices built in the test, turning and anchoring, batching, painting only after the block exists, conflicts and `force`, doors never replaced, undo and remove (top-down, exact restore, cells changed since left alone), protection (damage, salvage by land, building, the server's own events, switches), the decay guard, lost cells and repair (including blocks gone without a hook), cells the game did not take, land rules, `MaterialIds`, the material dump, a restart in the middle of a build, a damaged data file, permissions, every real sculpture placed complete and painted and taken down without a trace, and the reflected members in the real `Assembly-CSharp.dll`.
