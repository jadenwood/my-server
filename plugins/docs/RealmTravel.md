# RealmTravel: the roads of Ostreval

Waystones you unlock by walking to them and travel between for a toll, a home inside your own crest zone, a guided road in chat, and the realm's kits: a newcomer's pack, daily house provisions and a season's bounty.

**Status.** Compile-checked at C# 3 against the real Oxide 2.0.3867 and patched game DLL metadata (`tools/plugin-compile-check/check.sh`). Mock-tested: 301 checks (`plugins/docs/RealmTravel/logic-tests/run.sh`). Exploit suite: 69 checks including a 3000-step fuzz, plus 11 for the treasury door the tolls use (`tools/exploit-review/run.sh travel`). **Nothing here has been seen working on a real server.** Every game-side assumption is listed under [What is UNVERIFIED](#what-is-unverified), each with its smoke step.

Speaker in chat: **Roads** (lang key `Speaker`). Permission: `realmtravel.admin`. Files: `plugins/RealmTravel.cs`, `oxide/config/RealmTravel.json`, `oxide/data/RealmTravel.json`.

## What players see

1. **Finding a waystone.** Walk within a waystone's radius (10 m by default). Chat: `Roads: You have found the waystone Kingsreach. Return here any time: /travel kingsreach`, the count (`You know 3 of 7 waystones.`) and a window ("Waystone found", with the waystone's lore line). The window follows `/realm popups off`.
2. **Travelling.** `/travel` lists the waystones you know, nearest first, with distance, compass direction and toll (or *closed to you*). `/travel kingsreach` (or any unique start of its id or name): `You set out for Kingsreach. Stand still for 10 s: moving, fighting or being hurt breaks the journey.` and the toll. After 10 s the server moves you there, takes the toll and says so: `You arrive at Kingsreach. 17 marks paid to the crown's treasury. The road watches over you for 5 s, unless you strike first.`
3. **Home.** Stand inside your own crest zone and `/home set`. Later, `/home` takes you back the same way (12 s, toll 5).
4. **Roads.** `/road greywater` (or `/road home`) calls the way every 15 s: `Greywater: 1.2 km to the north-east.` until you arrive. It works for waystones you have not found yet, so it is also how you find them.
5. **Kits.** `/kit` shows each kit, what it holds and whether it is ready. `/kit starter` gives the Traveller's Pack straight into your packs; what does not fit waits for `/kit collect` (or your next join).
6. **Wayfarer.** Reach every waystone and the Herald tells the realm; RealmRenown records the deed and your house earns season points.

The `/realm` hub lists the four commands under **Roads, waystones and kits** (`/realm roads`).

## Commands

| Command | What it does |
|---|---|
| `/travel` | The waystones you know (and how many are still to find), your next journey, free journeys left |
| `/travel <name>` | Set out for a waystone you know (`/travel home` is `/home`) |
| `/travel all` | Every waystone of the realm (hidden ones only once found), known or not |
| `/travel info <name>` | Distance, toll, visitors, lore line, monument, who may travel there |
| `/travel cancel` | Stay where you are |
| `/home` / `/home set` / `/home info` / `/home clear` | Go home; set it here (own crest zone only); where it is and whether it is still valid; forget it |
| `/road <waystone>` / `/road home` / `/road stop` | Guided road in chat; `/road` alone repeats the current direction |
| `/kit` / `/kit <name>` / `/kit collect` | Your kits; take one; take what did not fit |

Admins (`realmtravel.admin`):

| Command | What it does |
|---|---|
| `/travel admin set <id> <capital\|seat\|holding\|landmark> [name]` | Raise a waystone where you stand (or move an existing one here). Ids are 2 to 24 of `a-z 0-9 -` |
| `/travel admin name\|note <id> <text>` | Rename; set the lore line (`note <id> none` clears it) |
| `/travel admin house <id> <house\|none>` | The house whose seat it is |
| `/travel admin kind <id> <kind>` / `toll <id> <marks\|auto>` / `radius <id> <2-100>` | Change kind; fixed toll or by distance; unlock radius |
| `/travel admin hidden <id> on\|off` / `enabled <id> on\|off` | Hidden until found; closed |
| `/travel admin mark <id> <sculpture\|none>` | Record which RealmSculptor monument marks it (shown in `/travel info`) |
| `/travel admin remove <id> confirm` | Remove (asks first; the confirm counts for 2 minutes) and forget who found it |
| `/travel admin list` / `status` | Every waystone with position, radius, toll, flags, visitors; counters and integrations |
| `/travel admin unlock\|lock <player> <id\|all>` | Give or take a player's knowledge of waystones |
| `/travel admin throne [clear]` | Set the Old Throne's point where you stand (for the rebellion rule), or go back to the game's |
| `/travel admin tp <id>` | Go to a waystone (no toll, no cooldown) |
| `/kit admin items <word>` | Search the game's item list for real item names |
| `/kit admin check` | Check every kit's items against the game's item list |
| `/kit admin reset <player> <kit\|all>` | Clear a player's kit claims (a testing aid) |

## The rules of the road

Checked when you set out **and again on arrival** (so becoming an outlaw, emptying your purse or a rebellion starting while you wait closes the road):

| Rule | Source | Config |
|---|---|---|
| Not in a fight: 30 s after dealing or taking a player's blow or rope, 10 s after a creature | own damage watch (`OnEntityHealthChange`, `OnPlayerCapture`) and `RealmWarden.IsInCombat` | `CombatSeconds`, `BeastCombatSeconds`, `UseWardenCombat` |
| Not a captive, not holding one, not held for ransom | `PlayerCaptureManager.Captured` / `HoldingCaptive` / `CaptivePlayerID`; `CrownAndConsequences.IsHeldForRansom` | `BlockCaptives` |
| Not bearing the Ironbreaker | `RealmLegendary.IsBearer` | `BlockIronbreaker` |
| Not an outlaw (crown or court) | `RealmContracts.IsOutlaw`, `RealmLaws.IsCourtOutlaw` | `BlockOutlaws` |
| Not to or from the Old Throne while a rebellion is under way | `CrownAndConsequences.IsRebellionActive`; the throne point (below) | `BlockNearThroneInRebellion`, `ThroneRadius` (80 m) |
| A seat only for its house and allies (liege, vassals, fellow vassals of one liege, treaty partners) | `RealmHouses.GetHouse`, `GetLiege`, `HasTreaty` | `SeatAccess`: `allies` / `house` / `all` |
| The capital is closed to exiles | `RealmLaws.IsExiled` | `ExilesBarredFromCapital` |
| Holdings close in the raid hours (march there) | `RealmWarden.IsRaidHourNow` | `HoldingsClosedInRaidHours` |
| Closer than 40 m: walk | | `MinDistance` |
| A cooldown between journeys (10 min; home 15 min) | saved, so a relog or restart does not reset it | `CooldownMinutes`, `Home.CooldownMinutes` |

While you wait, moving more than 1.5 m, being hurt, striking a blow or throwing a rope breaks the journey. A blow the realm does not allow (a protected newcomer's, during the Truce of the Realm, or one another plugin already cancelled) is **no fight**: it breaks nothing, so nobody can keep a newcomer off the roads by swinging at them.

**The throne point** is, in order: the point an admin set with `/travel admin throne`, the game's own throne position (`AncientThrone.EntityPosition`), or where a throne capture last completed.

**Arrival shield.** For `ArrivalShieldSeconds` (5) after arriving, other players' blows and ropes on you are cancelled, so a waystone cannot be camped. Striking or binding someone ends your own shield at once.

## Tolls

- **Amount:** a fixed toll per waystone (`/travel admin toll`), or `BaseToll` (10) + `TollPer100m` (1) for every 100 m, at most `MaxToll` (50). `/home` costs `Home.Toll` (5).
- **Discounts:** while the crown's **Open Roads** decree is in force (`CrownAndConsequences.IsDecreeActive("roads")`) the toll is `OpenRoadsTollPercent` (0: free). Members of a house sworn to the crown (`IsSwornToCrown`) pay `SwornTollPercent` (50). Each player's first `FreeTrips` (3) waystone journeys are free.
- **Where it goes:** to the crown's treasury, through `RealmTreasury.ChargeMarks(playerId, name, amount, "RealmTravel", note)`: purse to treasury, all or nothing, nothing minted, in the ledger. The purse is checked when you set out and again on arrival; the charge is made right after the move, in the same server tick as that check.
- Without RealmTreasury, roads are toll-free (`TollFreeWithoutTreasury: true`) or refused (`false`). If the treasury refuses a charge after the move (it should not), the journey stands and `/travel admin status` counts it under "tolls not collected".

## Home

`/home set` works only inside **your own crest zone**: the crest's group is your group (`CrestScheme.CurrentCrestGroup(position) == SocialAPI.GetGroupId(you)`, the same test the game's own siege check and RealmWarden use), or the crest is one you raised (`CrestScheme.GetCrestPlayer`). Moving the home has a cooldown (`SetCooldownMinutes`, 10). Going home checks again: a home in a crest you no longer belong to, or one that is gone, is void; no journey home into a siege (`CrestScheme.IsUnderSiege`); if the land cannot be read the answer is no, never a guess. `RequireOwnCrest: false` allows a home anywhere.

## Roads, and the fallback if the teleport does not work

`/road` needs nothing from the game but positions, so it always works. Directions: +x is east, north is +z (`Roads.NorthIsPositiveZ`; UNVERIFIED, smoke step T12). If the server-side teleport turns out not to work in game, set `Travel.Mode` to `"road"`: `/travel <name>` then starts a guided road instead of moving anyone, and the kits, homes and discovery keep working.

## Kits

Kits are defined in config (`Kits.List`), each with an `Id`, a `Name`, a `Kind` and up to 20 `Items` (`Item`, `Amount` 1 to 10000):

| Kind | Who, how often |
|---|---|
| `starter` | Once per Steam id, for a **newcomer**: a player RealmWarden protects as new (`IsNewPlayerProtected`). Seen once as a newcomer is enough: the pack can be taken within `StarterClaimDays` (7) of their first day. At most `StarterMaxPerDay` (40) for the whole realm in 24 h, which slows alt farms. Without RealmWarden nobody counts as new unless `StarterFallbackHours` is set |
| `house` | Once per realm day (CrownAndConsequences' `UtcOffsetHours`), for a member of a house of at least `HouseKitMinMembers` (2), who has been in that house for `HouseKitMinMemberHours` (12; counted from when this plugin first saw them in it) |
| `season` | Once per RealmSeasons season (`GetSeasonNumber`), after `SeasonKitMinPlayMinutes` (30) of play in that season |

No kit is given in a fight. A newcomer is told about the pack once, `NewcomerHintDelaySeconds` (45) after joining, after RealmHerald's welcome: `A traveller's pack waits for every newcomer: /kit starter.`

**Item names** live in the game's data, not in the DLL. Every kit item is looked up at start-up against the game's own item list: first as a `ResourceType` name (`InvBlueprints.GetBlueprintForResource`, the lookup the game's tax uses), then as an exact item name (`GetBlueprintForName(name, true, true)`). An unknown name is logged and skipped; a kit with no known item is "unavailable" and cannot be claimed. The defaults use `ResourceType` names only, the one item list the DLL itself holds (`Wood`, `Stone`, `Bread`, `Apple`, `IronOre`):

| Kit | Default contents |
|---|---|
| Traveller's Pack (`starter`) | 150 Wood, 100 Stone, 4 Bread, 6 Apple |
| House Provisions (`house`) | 100 Wood, 100 Stone, 3 Bread |
| Season's Bounty (`season`) | 300 Wood, 300 Stone, 40 IronOre |

To add tools or bandages, find the real names in game with `/kit admin items hatchet` (or `torch`, `bandage`, `bow`), add them to `oxide/config/RealmTravel.json`, reload, and run `/kit admin check`. Keep the starter pack modest: every alt gets one.

**Delivery** follows the game's own `/give`: items go into the packs in chunks of the item's stack limit, and every amount is **measured**. The claim and everything owed are saved first; then each line leaves the owed ledger, the file is saved, and the items move; what did not fit goes back into the ledger. A crash can lose a kit, never pay it twice. RealmSentinel is told the items are explained (`SentinelItemSource`).

## Wayfarer and Pathfinder

Reaching every open, unhidden waystone (at least `WayfarerMinWaystones`, 4):

- the Herald tells the realm once per player (at most `WayfarerHeraldsPerHour`, 4);
- `RealmRenown.AddDeed(id, name, "wayfarer", ..., "wayfarer")` records the deed once (40 renown; the deed is in RealmRenown's defaults);
- once per season per player, their house earns `PathfinderPoints` (2) through `RealmSeasons.AwardHouse`, at most `PathfinderAwardsPerHousePerSeason` (3) times per house per season, so alts cannot farm standings.

To give the deed a title, add this to `Titles` in `oxide/config/RealmRenown.json` (RealmRenown keeps unknown entries):

```json
{ "Id": "wayfarer", "Name": "the Wayfarer", "Description": "Reached every waystone of the realm.", "Infamous": false, "Requires": { "wayfarer": 1 } }
```

## Setting up the realm's roads

1. Stand at the capital's gate: `/travel admin set kingsreach capital Kingsreach`, then `/travel admin note kingsreach Where the six roads meet.` Raise the Herald's Pillar there if you like (`/sculpt place heralds-pillar`, then `/travel admin mark kingsreach heralds-pillar`).
2. For each house seat: `/travel admin set varrow-hall seat Varrow Hall`, `/travel admin house varrow-hall Varrow`. The six great houses each have a monument in `art/sculptures` (`house-varrow` and so on); the plugin offers the right one.
3. Holdings and landmarks: `/travel admin set greywater holding Greywater`, `/travel admin set hermits-cave landmark Hermit's Cave`, `/travel admin hidden hermits-cave on` for a secret.
4. Stand on the Old Throne: `/travel admin throne` (only if `/travel admin status` does not already show a `(game)` throne point).
5. `/travel admin list` and `/travel admin status`; walk to one waystone as a player to see the discovery.

## Config (`oxide/config/RealmTravel.json`)

Every value is clamped to a safe range on load. A broken config file runs on the defaults for that load (logged).

| Section.Key | Default | Meaning |
|---|---|---|
| `General.Enabled` | `true` | Master switch. Off: every player command says the roads are closed; admins can still set up |
| `General.UsePopups` | `true` | The discovery window |
| `General.AdminsExempt` | `false` | Admins skip channel time, cooldowns and tolls (testing) |
| `General.TickSeconds` / `SaveEverySeconds` / `MaxPlayersKept` | `1` / `60` / `20000` | |
| `Travel.Enabled` | `true` | `/travel` |
| `Travel.Mode` | `"teleport"` | `"road"`: `/travel` shows the way instead of moving anyone |
| `Travel.ChannelSeconds` / `MoveTolerance` | `10` / `1.5` | Stand still this long, within this many metres |
| `Travel.CooldownMinutes` | `10` | |
| `Travel.BaseToll` / `TollPer100m` / `MaxToll` | `10` / `1` / `50` | Marks |
| `Travel.FreeTrips` | `3` | Free waystone journeys per player |
| `Travel.SwornTollPercent` | `50` | Toll share for houses sworn to the crown |
| `Travel.OpenRoadsDecree` / `OpenRoadsTollPercent` | `"roads"` / `0` | The crown's decree that opens the roads, and the toll share while it runs |
| `Travel.TollFreeWithoutTreasury` | `true` | |
| `Travel.MinDistance` / `ArrivalLift` | `40` / `0.5` | Metres |
| `Travel.ArrivalShieldSeconds` | `5` | `0` = no shield |
| `Travel.CombatSeconds` / `BeastCombatSeconds` / `UseWardenCombat` | `30` / `10` / `true` | |
| `Travel.BlockOutlaws` / `BlockCaptives` / `BlockIronbreaker` | `true` | |
| `Travel.BlockNearThroneInRebellion` / `ThroneRadius` | `true` / `80` | |
| `Travel.ExilesBarredFromCapital` / `HoldingsClosedInRaidHours` | `true` / `true` | |
| `Travel.SeatAccess` | `"allies"` | `house`, `allies` or `all` |
| `Home.Enabled` / `RequireOwnCrest` / `BlockUnderSiege` | `true` | |
| `Home.ChannelSeconds` / `CooldownMinutes` / `Toll` / `SetCooldownMinutes` | `12` / `15` / `5` / `10` | |
| `Discovery.Enabled` / `CheckSeconds` / `DefaultRadius` / `PopupOnDiscovery` | `true` / `2` / `10` / `true` | |
| `Discovery.WayfarerMinWaystones` / `WayfarerDeed` / `WayfarerHerald` / `WayfarerHeraldsPerHour` | `4` / `"wayfarer"` / `true` / `4` | |
| `Discovery.PathfinderPoints` / `PathfinderAwardsPerHousePerSeason` | `2` / `3` | |
| `Roads.Enabled` / `UpdateSeconds` / `MaxMinutes` / `ArrivalRadius` / `NorthIsPositiveZ` | `true` / `15` / `30` / `12` / `true` | |
| `Kits.Enabled` | `true` | |
| `Kits.StarterClaimDays` / `StarterFallbackHours` / `StarterMaxPerDay` | `7` / `0` / `40` | |
| `Kits.NewcomerHint` / `NewcomerHintDelaySeconds` | `true` / `45` | |
| `Kits.HouseKitMinMemberHours` / `HouseKitMinMembers` / `SeasonKitMinPlayMinutes` | `12` / `2` / `30` | |
| `Kits.MaxOwedLines` | `30` | Lines waiting for `/kit collect` before new kits are refused |
| `Kits.List` | three kits | See [Kits](#kits). A kit with a duplicate id or an unknown kind is ignored with a warning |

## Game API used (research notes)

Read in the decompiled shipped patched `Assembly-CSharp.dll` (type and member names only; no game code is in this repo) and confirmed by the compile check against its metadata:

- **Teleport:** `CodeHatch.Engine.Behaviours.CharacterTeleport.Teleport(Vector3)`, reached with `Entity.GetOrCreate<CharacterTeleport>()`. On the server (`Player.IsLocalServer`) it raises `CodeHatch.Networking.Events.Entities.TeleportEvent` through `EventManager.CallEvent`; `CharacterTeleport.OnTeleportEvent` accepts it only from the server and sets the body's position. The game's own server-side admin command `/tp` (`CodeHatch.Thrones.ThronesCommandHandler.tp`) moves players with exactly this call, as does `PlayerListener` after a spawn and RealmSentinel's freeze. **Teleporting from a plugin is therefore possible**, and the guided road is the fallback, not the plan. RealmSentinel already grants movement grace on every server `TeleportEvent`; `SentinelGrace` is asked first too.
- **Items:** the `/give` path (`ThronesCommandHandler.Give`): `PlayerExtensions.GetInventory`, `Container.Contents`, `new InvGameItemStack(blueprint, n, null)`, `ContainerManagement.StackLimit`, `ItemCollection.AutoMergeAdd`, `ItemCollection.AutoCount`; lookups `InvBlueprints.Instance.GetBlueprintForResource(ResourceType)`, `GetBlueprintForName(string, bool, bool)`, `InvBlueprints.GetBlueprintsContaining(string)`; `CodeHatch.ResourceType`.
- **Land:** `CodeHatch.Thrones.SocialSystem.CrestScheme.CurrentCrestGroup(Vector3)`, `GetCrestPlayer(Vector3)`, `IsUnderSiege(Vector3)` via `SocialAPI.Get<CrestScheme>()`; `SocialAPI.GetGroupId(ulong)` (0 when the player is in no group).
- **Throne:** `CodeHatch.Thrones.AncientThrone.AncientThrone.EntityPosition` (static; `Vector3.zero` when no throne instance is awake), `AncientThroneCaptureEvent.State`.
- **Captives:** `CodeHatch.Thrones.Capture.PlayerCaptureManager.Captured`, `HoldingCaptive`, `CaptivePlayerID`, read with `Entity.TryGet<PlayerCaptureManager>()`.
- **Hooks:** `OnEntityHealthChange(EntityDamageEvent)` [OPJ L162], `OnPlayerCapture(PlayerCaptureEvent)` [OPJ L711], `OnEntityDeath(EntityDeathEvent)` [OPJ L188], `OnThroneCaptured(AncientThroneCaptureEvent)` [OPJ L607], `OnPlayerConnected` / `OnPlayerDisconnected` [SRC]. Blocking (arrival shield) follows RealmWarden: `evt.Cancel(...)`, `Damage.Amount = 0`, `return true`.
- **Popups:** `PlayerExtensions.ShowPopup(title, message, buttonText, handler, interupt, broadcast)`, in the `Popups` region, inside `try`, `broadcast = true`, chat always sent too.
- **Commands:** no game command is called `travel`, `home`, `road` or `kit` (every `[Command]` attribute in the DLL was listed; the game has `/tp` with aliases `warp` and `teleport`).

## Integration

| Calls into | Methods |
|---|---|
| RealmTreasury | `GetPurse`, `ChargeMarks` (new, non-public: purse to treasury, all or nothing) |
| RealmHouses | `GetHouse`, `GetLiege`, `HasTreaty`, `GetMembers` |
| CrownAndConsequences | `IsRebellionActive`, `IsHeldForRansom`, `IsSwornToCrown`, `IsDecreeActive`, `GetUtcOffsetHours` |
| RealmLaws / RealmContracts | `IsCourtOutlaw`, `IsExiled` / `IsOutlaw` |
| RealmLegendary | `IsBearer` |
| RealmWarden | `IsNewPlayerProtected`, `IsInCombat`, `IsRaidHourNow` |
| RealmEvents | `IsTruceActive` |
| RealmSeasons | `GetSeasonNumber`, `AwardHouse` |
| RealmRenown | `AddDeed` (deed `wayfarer`, added to RealmRenown's defaults) |
| RealmHerald | `PopupsWanted` |
| RealmSentinel | `SentinelGrace` before every move, `SentinelItemSource` before every kit |

Offered to other plugins (`plugin.Call`, non-public): `GetDiscoveredCount(string playerId)` → `int`, `HasDiscovered(string playerId, string waystoneId)` → `bool`, `IsTravelling(string playerId)` → `bool`, `CancelJourney(string playerId)` → `bool` (an arena or a court that moves or binds a player can call a journey off).

**Chronicle.** No entries: a journey or a kit is not realm history. A Wayfarer title, once an admin adds it to RealmRenown (above), reaches the Chronicle as RealmRenown's own `title_earned`. No new Chronicle type is needed.

**RealmSculptor and RealmPainter.** RealmSculptor has no API; the plugin records which monument marks a waystone and gives the admin the `/sculpt place` line. A "roads" sign board in RealmPainter is a follow-up.

## Data

`oxide/data/RealmTravel.json`: waystones; per player the waystones found, home, cooldowns, free journeys used, kit claims, items owed, house first seen in, play time this season; the throne point; counters. Journeys in progress and roads are never saved: a reload cancels them and nothing is charged. If the file exists but cannot be read (truncated, empty, or written by a newer version) the plugin pauses, every command says so, and **the file is never overwritten**. Fix or move it, then reload.

## Tests

```
bash tools/plugin-compile-check/check.sh                 # C# 3 against the real DLL metadata
bash plugins/docs/RealmTravel/logic-tests/run.sh          # 301 checks
bash tools/exploit-review/run.sh travel                   # 69 checks with a 3000-step fuzz, then 11 for RealmTreasury.ChargeMarks
```

The logic tests compile `plugins/RealmTravel.cs` unchanged with `logic-tests/Mocks.cs` (players on a map, a server-side teleport with fault knobs, a units-capacity inventory, crest zones, the throne) and `logic-tests/World.cs` (fakes for every plugin it calls and a clock). They cover admin set-up, discovery and its window, journeys, tolls and discounts, breaking, every blocker, seat access, re-checks on arrival, the arrival shield (blows and ropes), teleport faults, home, roads and the road mode, the three kit kinds, delivery and the owed ledger, item resolution, Wayfarer and Pathfinder, data safety, config clamps and switches, the API and the chat style. They prove the plugin's rules, not the game's behaviour. The griefer and duper plans are in [`tools/exploit-review/travel/README.md`](../../tools/exploit-review/travel/README.md).

## Smoke test on a real server

Two players (A, B; different houses) and an admin, on the owner's test server. Give A some marks first (`/treasury grant`, or a market sale).

| # | Step | Expect |
|---|---|---|
| T1 | Load the plugin; read the log. `/kit admin check` | No "is not known" warnings, or a list of item names to fix. Every default item resolves; note the real names. |
| T2 | Admin: raise two waystones 500 m apart (`/travel admin set ...`); `/travel admin status` | Both listed; the throne point says `(game)` with a position (or `none`). |
| T3 | A walks into one waystone's radius. | The discovery line and window ("Waystone found"). Check the window shows and its line breaks. |
| T4 | A: `/travel <the other>`; stand still. | After 10 s A is at the other waystone, on the ground (not stuck in it, not falling). Other players see A arrive. The toll left A's purse (`/purse`) and reached `/treasury`. |
| T5 | After several journeys (T4 and T9): `/travel admin status`. | "arrivals not confirmed 0". If it counts up, the server's position does not follow the teleport: set `Mode: "road"`. |
| T6 | A sets out; B hits A while A waits. A sets out again and walks away. | Both journeys break; A cannot set out for 30 s after B's blow. |
| T7 | A arrives; B (waiting at the waystone) hits A at once, then after 6 s. | The first blow does no damage; the second does. B tries a rope in the first seconds: refused. |
| T8 | A binds B with a rope (or B is bound); either tries `/travel`. | Refused ("hold a captive" / "A captive cannot travel"). |
| T9 | A places a crest and `/home set` inside it; outside it; then `/home` from far away. | Refused outside; inside set; `/home` brings A back inside the crest. |
| T10 | B starts a siege on A's crest; A tries `/home`. | "Your hold is under siege." |
| T11 | A new Steam account joins (RealmWarden protects it). Wait 45 s. `/kit starter` | The hint line; the items appear in the packs at once (and RealmSentinel stays quiet). With full packs: "the rest waits"; `/kit collect` after making room. |
| T12 | `/road <waystone>` and walk toward it, comparing the game's compass or map. | The direction words match the game's north (if north and south are swapped, set `NorthIsPositiveZ: false`). |
| T13 | Declare a rebellion window (CrownAndConsequences); stand near the throne; `/travel`. | "A rebellion is under way". |
| T14 | Restart the server. | Found waystones, homes, cooldowns and kit claims are still there; `/travel admin status` counters kept. |

## What is UNVERIFIED

Everything at run time. In particular:

1. That `CharacterTeleport.Teleport` called by a plugin moves the player on every client and on the server, onto solid ground at `ArrivalLift` 0.5 m above the stored point (the game's own `/tp` relies on the same call) (T4, T5).
2. That the server's own position of the player follows the teleport at once (counted by "arrivals not confirmed") (T5).
3. That `OnEntityHealthChange` sees every blow a player takes, so waiting breaks and the fight window holds (T6); that cancelling it (arrival shield) stops the damage, the basis RealmWarden and RealmEvents use (T7).
4. That cancelling `OnPlayerCapture` stops a rope or chain (T7), and that `PlayerCaptureManager.Captured` / `HoldingCaptive` are set on the captive's and the captor's own managers (T8).
5. `CrestScheme.CurrentCrestGroup` against `SocialAPI.GetGroupId` for a player in a guild, and `GetCrestPlayer` for one in none (T9); `IsUnderSiege` during a real siege (T10).
6. That the default kit names (`Wood`, `Stone`, `Bread`, `Apple`, `IronOre`) resolve to carried items through their `ResourceType` keys, and that server-side `AutoMergeAdd` shows in the client's packs at once (T1, T11).
7. That RealmWarden's protection is already known when the hint fires 45 s after joining (T11).
8. Which world axis the game calls north (T12).
9. `AncientThrone.EntityPosition` on a dedicated server (T2, T13).
10. The discovery window on a real client (T3).
11. That the restart keeps everything (T14).

## Merge notes (shared files this branch touches)

- `plugins/RealmTreasury.cs`: one new non-public method, `ChargeMarks`, after `GetTreasuryItem` (additive; other branches add `GrantHouseIncome`, `RewardMarks` and holds elsewhere in the same region).
- `plugins/RealmRenown.cs`: one default deed line, `wayfarer`, after `rebellion_defended`.
- `plugins/RealmHerald.cs`: subject `roads` in `Subjects`, four catalogue entries and five lang lines after the `rumor` entries; `plugins/docs/RealmHerald/logic-tests/Tests.cs`: `RealmTravel` in `AllPlugins`, and hub counts computed from the subjects.
- `docs/community/ops/staff-roles-and-permissions.md`: the `realmtravel.admin` row and grant line.
- `tools/exploit-review/run.sh` and `README.md`: the `travel` suite.
