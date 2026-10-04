# RealmWorld: the living world of Ostreval

Things happen in Ostreval between the great events of RealmEvents: a hoard is hidden and its riddles are cried in the squares, the moon rises red, a merchant caravan wants a bearer and swords, a beast out of the old songs is seen in the hills, the houses bring in the harvest and keep Midwinter, and once a week the heralds count the realm.

**Status.** Compile-checked at C# 3 against the real Oxide 2.0.3867 and patched game DLL metadata (`tools/plugin-compile-check/check.sh`). Mock-tested: 326 checks (`plugins/docs/RealmWorld/logic-tests/run.sh`). Exploit suite: 48 checks including a 4000-step fuzz (`tools/exploit-review/run.sh world`). **Nothing here has been seen working on a real server.** Every game-side assumption is listed under [What is UNVERIFIED](#what-is-unverified), each with its smoke step.

Speaker in chat: **World** (lang key `Speaker`); realm-wide news is the **Herald**'s. Permission: `realmworld.admin`. Files: `plugins/RealmWorld.cs`, `oxide/config/RealmWorld.json`, `oxide/data/RealmWorld.json`. Moods: `mods/presets/blood-moon`, `harvest-festival`, `midwinter`.

## What players see

| Event | How it starts | What a player does | Who wins what |
|---|---|---|---|
| **Treasure Hunt** | `Herald: A hoard lies hidden in Ostreval: The Miller's Hoard, 4 clue(s) deep. The first clue: "..." Follow it with /treasure.` Countdown heralds at 30, 10 and 1 min. | Read the riddle, find the place it describes and stand there. The next riddle comes in chat and in a window ("A Clue"). `/treasure` repeats your clue; `/treasure hint` names a direction and a distance band once per clue after 15 min. A painted sign bound with `/paint world clue <n>` shows riddle n. | The first to stand at the last place (the dig site): 150 marks, the hunt's goods (in the chest staff bound to the dig site, opened to the finder alone for 10 min, or in the packs), the deed `treasure_found`, 15 season points for their house. |
| **The Blood Moon** | Warnings at 60, 30 and 10 min: `The moon rises red in 60 min...`; then `The Blood Moon rises!` (every other Thursday, 20:00 to 22:00 UTC). The sky is the **Blood Moon** mood if the owner applied it at the restart before. | Fight. Every **fair** kill of another player counts (`Cat falls under the Blood Moon. Your kills tonight: 1.`) and earns renown (`blood_moon_kill`, at most 5 a night). Beasts bite 25% harder. `/world bloodmoon` shows your count and the houses. | At the set: the house with the most kills (at least 3, of at least two different victims) earns 20 season points; the deadliest slayer (at least 2 kills) 60 marks. |
| **The Merchant Caravan** | `A merchant caravan musters at Kingsreach, bound for Greywater (1000 m). It leaves in 10 min.` | One player at the start bears the goods (`/caravan carry`); others guard them (`/caravan escort`). The bearer must **walk** to the other place before the deadline (45 min): no waystone journey, no jump, no duel. Sightings are heralded every 5 min, so raiders know where to ride. | Arrival: a purse of 300 marks, 40% to the bearer and the rest shared by the escorts who kept within 60 m for half the road; 15 season points for the bearer's house; the deed `caravan_escort`. A **raider** who kills the bearer fairly takes 40% of the purse, an infamous deed and a price of 100 marks on their head for 2 h, paid to whoever brings them down. |
| **A Wandering Legend** | `The Grey Widow roams Ostreval! A she-wolf grey as ash... It was last seen 400 m north-east of Greywater.` Sightings every 10 min. | Hunt it. If a living creature of its kind was found when it began, that one creature is the Legend and takes a quarter of the damage (Toughness 4). Otherwise the first creature of its kind slain in its region (or anywhere) is the one. `/world legend` says where it was last seen. | The slayer: 200 marks (less the helpers' share), the trophy (pelts, hides, fangs), the deed `legend_slain`, 20 season points; every player who dealt 15% of the damage shares 30% of the marks. |
| **The Harvest Fair** (22 to 29 September) and **Midwinter** (20 to 27 December) | `The Harvest Fair begins and runs for 7 days! The houses compete in offerings: /festival.` The houses' monuments staff marked for the festival rise (RealmSculptor) and come down at the close. Moods: **Harvest Fair** and **Midwinter**. | Give goods for your house: `/festival give` (everything that counts) or `/festival give Bread`. Each offering has points (Bread 5, Flour 3, Grain 2...); at Midwinter each wolf or bear slain earns 10 more. A player can earn their house at most 200 points a realm day. `/festival` shows the standings and what you may still give today. | At the close: 40, 25 and 10 season points to the first three houses (a house needs two givers to place); the best giver of the winning house 100 marks and the deed `festival_champion`. A painted sign bound with `/paint world festival` shows the standings. |
| **The Census** | Mondays 18:00 UTC (it waits until no event is running): `Herald: The census of Ostreval, 12 October:` and the lines below it. | `/world census` repeats the last census and this week so far. | Nobody: it is news. Souls who walked the realm (and how many were new), the most at once, the houses and the largest, the crown, the season's leader, marks struck and held by the crown, new Chronicle entries, the week's hoards, caravans, legends and Blood Moon kills, and goods offered. It is written to the Chronicle as `census_taken`. Counts only, never names. |

Every event is also a RealmQuests event (`ReportQuestEvent(id, "event", <subject>, 1)`: `treasure_step`, `treasure_hunt`, `blood_moon`, `caravan`, `wandering_legend`, `harvest_festival`, `midwinter_festival`), so a daily task like "Take part in a realm event" counts it.

Three RealmRenown titles come from the living world: **Hoardfinder** (three hoards found), **Caravan Warden** (three caravans brought home) and **Bane of Legends** (a Wandering Legend slain). Their badges are in the art pack (`art/badges/titles/`).

The `/realm` hub lists `/world`, `/treasure` and `/festival` under events and `/caravan` under roads.

## Commands

| Command | What it does |
|---|---|
| `/world` | What is abroad now, the festival, and the next world events (UTC) |
| `/world schedule` | The next ten world events, with any that will wait for a RealmEvents event |
| `/world history` | The last ten things that happened in the living world |
| `/world bloodmoon` / `legend` / `census` | The Blood Moon (your kills, the houses), the Legend's last sighting, the last census |
| `/world collect` | Marks and goods the living world still owes you (packs were full, or the treasury could not pay at once) |
| `/treasure` / `/treasure hint` | Your clue and how far along you are; a hint once per clue after `HintAfterMinutes` |
| `/caravan` / `carry` / `escort` / `leave` | The caravan's state and any raider's price; bear it; guard it; stop guarding (the bearer may step down only at the muster) |
| `/festival` / `/festival give [item\|all]` | The standings and your part; give goods for your house |

Admins (`realmworld.admin`):

| Command | What it does |
|---|---|
| `/world admin status` | On or off, what runs, festival, counts, which plugins are loaded, postponed events |
| `/world admin start <treasure\|bloodmoon\|caravan\|legend\|census> [target] [minutes] [force]` | Start one now (a hunt, route or legend id as target). Refused if it would meet a RealmEvents event, unless `force` |
| `/world admin stop` | Call off what runs (no rewards) |
| `/world admin schedule` | The schedule as players see it |
| `/world admin place set <id> [radius] [name]` / `place clear <id>` / `places` | Named places where you stand (caravan ends, legend regions); `places` also lists RealmTravel's waystones as `waystone:<id>` |
| `/world admin hunt new <id> <name>` | Begin laying out a hunt |
| `/world admin hunt step <id> <riddle>` | Stand at the next place and add the riddle that leads **to it**; the last place is the dig site (at most 12) |
| `/world admin hunt undo\|radius\|chest\|reward\|enable\|show\|remove\|list ...` | Drop the last place; one place's radius (2 to 50 m); bind the chest you stand by (within 5 m) to the hunt (`chest <id> clear` unbinds); `reward <id> marks <n> \| points <n> \| item <name> <n> \| items clear \| default`; on or off; show; remove (asks for `confirm` within 2 min) |
| `/world admin route add <id> <from> <to> [name]` / `route remove <id>` / `routes` | Caravan routes between two places or waystones (`waystone:<id>`); saved to the config |
| `/world admin deco add <festival> <sculpture>` / `remove <festival> <n>` / `list <festival>` | Mark where a RealmSculptor monument rises for a festival: stand and face as for `/sculpt place` (at most 20) |
| `/world admin festival start <id> [days]` / `stop` / `cancel` | Open a festival now; close it with its prizes; cancel it without prizes |
| `/world admin census` | Take the census now |
| `/world admin creatures` | The creature kinds within 300 m and the count in the world (to tune legend and festival `Kinds`) |
| `/world admin legends` | The legends in the config |
| `/world admin bounty [clear <name>]` | Raider prices; lift one |

Staff run the living world: while `General.AdminsCanWin` is false (the default) a holder of `realmworld.admin` wins nothing in it, because whoever lays a hunt knows its dig site.

## Setting up the living world

1. **Places.** Walk to a few landmarks and `/world admin place set <id> 30 <Name>` (e.g. `kingsreach`, `greywater`, `fenmarch`). Waystones raised with RealmTravel can be used directly as `waystone:<id>`.
2. **Caravan routes.** `/world admin route add grain-road kingsreach greywater The Grain Road`. Aim for 600 to 1500 m: a walk of 10 to 25 minutes with the default 45-minute window (10 of them for the muster).
3. **Treasure hunts.** `/world admin hunt new millers-hoard The Miller's Hoard`, then walk the trail: at each place, `/world admin hunt step millers-hoard <the riddle that describes this place>`. Riddles are plain words (no colour tags, at most 180 characters). Put a chest at the dig site, stand by it and `/world admin hunt chest millers-hoard`. Lay three or four hunts; they take turns. `/paint world clue 1` on a sign in the capital shows the first riddle while a hunt runs.
4. **Legends.** The defaults hunt "wolf", "bear" and "deer/stag/hart". Run `/world admin creatures` in the wild and put the words you see into each legend's `Kinds` in the config; give a legend a `Region` (a place id) and `RegionRadius` to keep it in one part of the map.
5. **Festival decorations.** Before the fair, stand where a monument should rise and `/world admin deco add harvest house-varrow` (any sculpture in `oxide/data/RealmSculptor/`). RealmSculptor builds them at the opening, under its own rules (inside a crest zone, on free ground) and takes them down at the close.
6. **Moods.** At the restart before a Blood Moon night or a festival, `server\Set-Mood.ps1 -Event blood_moon` (or `harvest_festival`, `midwinter`), and `-Return` at the restart after (see [`mods/moods.md`](../../mods/moods.md)). RealmWorld writes a reminder to the server log when each begins. The game reads the Mods files only at start-up, so the plugin cannot change the sky itself.
7. **Signs.** `/paint world` (what is abroad and next), `/paint world festival`, `/paint world census`, `/paint world clue <n>` (RealmPainter).

## The schedule, and never meeting RealmEvents

| When (UTC) | World event | RealmEvents (defaults) |
|---|---|---|
| Monday 18:00 | Census | |
| Monday 19:30 to 20:15 | Caravan | |
| Tuesday 19:00 to 20:00 | Treasure Hunt | |
| Wednesday 18:30 to 19:30 | Wandering Legend | King's Hunt 21:00 to 22:00 |
| Thursday 18:00 to 18:45 | Caravan | |
| Thursday 20:00 to 22:00, every other week | Blood Moon | |
| Friday | | Royal Tournament 19:00 to 20:00 |
| Saturday 15:00 to 16:00 | Treasure Hunt | Crown Night 19:00 to 20:30 |
| Sunday 18:00 to 19:00 | Wandering Legend | Truce 12:00 to 16:00 |

- **The defaults never meet.** A logic test reads RealmEvents' default schedule from its source and proves, minute by minute over two weeks, that no world event comes within `ClashBufferMinutes` (30) of a RealmEvents event and that no two world events overlap.
- **At run time, too.** Before a world event starts, and before its countdown is heralded, RealmWorld asks RealmEvents (`GetActiveEvents`, `GetNextEvent`). If an event runs, or the next one starts before this world event would end plus the buffer, the world event waits until the running one is over (at most `MaxPostponeMinutes`, 120) or is skipped, and staff online are told. No countdown is heralded for an event that would have to wait. Only one timed world event runs at a time. The census waits for quiet; a festival closes only when nothing else is running (at most `MaxCloseDelayMinutes` late). An admin `start` is refused if it would meet a RealmEvents event, unless `force`.
- A slot can be `Daily`, `Weekdays`, `Weekends` or day names; `WeekInterval` 2 with `WeekOffset` 0 or 1 makes it every other week. A server that was down at a slot's time does not start it late beyond its window, and never shouts an old census.

## The rules that keep it fair

| Rule | Where |
|---|---|
| A **fair kill** (Blood Moon, caravan plunder, raider's price): both players online and different people, not housemates, liege, vassal or treaty partners (RealmHouses), the victim not under new-player protection (RealmWarden), no Truce of the Realm (RealmEvents), neither in a duel (RealmArena), the killer not staff | `FairKill` |
| Blood Moon farming: each killer-victim pair counts once a night; a victim feeds at most `MaxDeathsFedPerVictim` (3) kills in all; at most `KillDeedsPerPlayer` (5) deeds; the house prize needs `HouseMinKills` (3) kills of two different victims; the slayer's marks need two kills | `BloodKill`, `FinishBlood` |
| Treasure: places count only in order and at least `MinSecondsBetweenSteps` (20 s) apart (a faster arrival is not counted and staff are told); the first at the dig site ends the hunt in the same tick | `TickTreasure` |
| The finder's chest opens to the finder alone for `ChestLockMinutes` (`OnPlayerInteract` refused for anyone else) | `OnPlayerInteract` |
| Caravan: the bearer must stand at the start; no protected newcomer, no player with a raider's price and no duellist may bear it; a waystone journey is called off (`RealmTravel.CancelJourney`); a jump of more than `MaxJumpMetres` (150 m) between two ticks loses it; a bearer who goes into a duel loses it; offline for more than `OfflineGraceSeconds` (60) deserts it; escorts are paid only after keeping close for `EscortMinPercent` (50%) of the road, never while in a duel; a full caravan drops an idle escort for a new one | `TickCaravan`, `CaravanArrives` |
| Raider's price: only a fair killer of the raider, never the plundered bearer, paid once, within `RaiderBountyHours` | `BountyKill` |
| Legend: helpers need `HelperMinPercent` (15%) of the damage; one blow counts at most 1000; blows another plugin cancelled are ignored | `LegendHit`, `LegendSlain` |
| Festival: only goods the day's cap will count leave the packs; what really left the packs is measured; points are kept per player and house, and the best giver is chosen from points given for the winning house | `FestivalGive`, `FestivalCredit` |
| Every reward: marks through `RealmTreasury.RewardMarks` (new marks under the treasury's caps); what it does not pay now is owed and paid on the next join or `/world collect`. Goods are written to an owed ledger **before** they are handed over, measured with `AutoCount`, and what did not fit goes back. A crash can lose a reward, never pay it twice | `PayMarks`, `OweItems`, `PayOwed` |

The griefer and duper plans, and why each fails, are in [`tools/exploit-review/world/README.md`](../../tools/exploit-review/world/README.md) (W1 to W17).

## Config (`oxide/config/RealmWorld.json`)

Every value is clamped on load; a broken config file falls back to the defaults for that run (the log says so).

| Key | Default | |
|---|---|---|
| `General.Enabled` | `true` | Switch the whole living world off (raider prices still lapse) |
| `General.TickSeconds` / `SaveEverySeconds` | `5` / `60` | |
| `General.UsePopups` | `true` | Windows for a clue, the hoard and a slain legend (chat is always sent too) |
| `General.AdminsCanWin` | `false` | Staff win nothing in the living world |
| `General.AvoidRealmEvents` / `ClashBufferMinutes` / `MaxPostponeMinutes` | `true` / `30` / `120` | |
| `General.CountdownMinutes` | `[30, 10, 1]` | |
| `General.HistoryLines` | `20` | |
| `General.NorthIsPositiveZ` | `true` | Compass words in hints and sightings (UNVERIFIED on our map) |
| `Schedule` | eight slots (above) | `Id`, `Event`, `Enabled`, `Days`, `StartUtc`, `DurationMinutes` (10 to 360; census 0), `WeekInterval`, `WeekOffset`, `Target` (a hunt, route or legend id; empty = the next in turn) |
| `Treasure.Enabled` / `StepRadius` / `MinSecondsBetweenSteps` / `HintAfterMinutes` | `true` / `8` / `20` / `15` | |
| `Treasure.RewardMarks` / `SeasonPoints` / `Items` | `150` / `15` / `IronIngot 10, Bread 5` | A hunt can override them (`/world admin hunt reward`) |
| `Treasure.UseChest` / `ChestLockMinutes` | `true` / `10` | `false`: the prize always goes to the packs |
| `BloodMoon.Enabled` / `WarningMinutes` | `true` / `[60, 30, 10]` | |
| `BloodMoon.KillDeedsPerPlayer` / `MaxDeathsFedPerVictim` / `HousePoints` / `HouseMinKills` / `TopSlayerMarks` | `5` / `3` / `20` / `3` / `60` | |
| `BloodMoon.BeastDamageMultiplier` | `1.25` | 1 to 3; `1` switches it off |
| `BloodMoon.NightOnly` | `false` | Count kills only while the game's clock says night |
| `Caravan.Enabled` / `MusterMinutes` / `StartRadius` / `ArriveRadius` | `true` / `10` / `20` / `20` | |
| `Caravan.PurseMarks` / `BearerSharePercent` / `RaidSharePercent` | `300` / `40` / `40` | Keep the raid share plus the price below an honest arrival (exploit W8) |
| `Caravan.RaiderBountyMarks` / `RaiderBountyHours` | `100` / `2` | `0` switches the price off |
| `Caravan.EscortRadius` / `EscortMinPercent` / `MaxEscorts` | `60` / `50` / `8` | |
| `Caravan.SeasonPoints` / `MaxJumpMetres` / `OfflineGraceSeconds` / `SightingMinutes` | `15` / `150` / `60` / `5` | |
| `Caravan.Routes` | none | Written by `/world admin route` |
| `Legends.Enabled` / `HintMinutes` / `HelperMinPercent` / `HelperSharePercent` | `true` / `10` / `15` / `30` | |
| `Legends.Legends` | the Grey Widow (wolf), Old Ironhide (bear), the Pale Hart (deer) | `Id`, `Name`, `Lore`, `Kinds`, `Region`, `RegionRadius`, `Toughness` (1 to 20), `RewardMarks`, `Trophy`, `SeasonPoints`, `Enabled` |
| `Festivals.Enabled` / `Decorations` / `MaxCloseDelayMinutes` | `true` / `true` / `180` | |
| `Festivals.Festivals` | the Harvest Fair (22 September, 7 days), Midwinter (20 December, 7 days, wolves and bears 10 points) | `Month`, `Day`, `Days`, `StartUtc`, `Mood`, `Offerings` (item and points), `HuntKinds`, `HuntPoints`, `DailyPlayerCap` (200), `PlacePoints` (40/25/10), `MinContributors` (2), `TopGiverMarks` (100) |
| `Census.Enabled` / `MaxKnownPlayers` | `true` / `20000` | How many player ids it remembers to tell newcomers apart |

Item names are the game's `ResourceType` names first (the lookup the game's own resource tax uses), then exact item names. A name the server does not know is reported in the log and never paid; a logic test checks every default against the mock's item list, and smoke step W1 checks them on the real server.

## Game API used (research notes)

Read in the decompiled shipped patched `Assembly-CSharp.dll` (type and member names only; no game code is in this repo) and confirmed by the compile check against its metadata:

- **Hooks:** `OnEntityHealthChange(EntityDamageEvent)` [OPJ L162], `OnEntityDeath(EntityDeathEvent)` (victim `evt.Entity`, killer `evt.KillingDamage.DamageSource.Owner`) [OPJ L188], `OnPlayerInteract(InteractEvent)` (`InteractEvent.Entity` is the thing used, `ControllerEntity` the user; `NetworkEvent.Sender` as a fallback) [OPJ L1036], `OnPlayerConnected` / `OnPlayerDisconnected` [SRC].
- **Creatures:** `Entity.TryGetAll<MonsterEntity>()` (the game's own `CodeHatch.Engine.Core.Reporting.MonsterReport` lists creatures this way), `Entity.TryGetFromViewID(ulong)`, `Entity.NetViewID`, `Entity.Position`; a creature is an entity with a `MonsterMotor` or `MonsterEntity` (as RealmQuests reads it); its kind from `Entity.ToString()`. **There is no member a server plugin can use to rename a creature or raise its health in a way this repo can prove reaches clients**, so a Legend is named in chat only and made tougher by scaling `Damage.Amount` of players' blows in `OnEntityHealthChange` (as RealmLegendary scales blows).
- **Chests:** `CodeHatch.ItemContainer.InteractableContainer` (a `Container`) on a placed object, its `Contents` (`ItemCollection`), filled with `ItemCollection.AutoMergeAdd(new InvGameItemStack(blueprint, n, null))` in stacks of `ContainerManagement.StackLimit` and measured with `ItemCollection.AutoCount` (the path of the game's `/give`, `ThronesCommandHandler.Give`). Found again by view id, or as the nearest container within 1.5 m of where it was bound (`Entity.TryGetAll()`).
- **Packs:** `PlayerExtensions.GetInventory` → `Container.Contents`; `AutoCount`, `AutoMergeAdd`, and `ItemCollection.AutoSplit(collection, blueprint, n)`, which splits the amount off the stacks from the last slot back (`ItemCollection.SplitItemAt`); the taken amount is measured before and after. Lookups `InvBlueprints.Instance.GetBlueprintForResource(ResourceType)` and `GetBlueprintForName(name, true, true)`.
- **Clock:** `GameClock.Instance.CurrentTimeBlock == GameClock.TimeBlock.Night` (for `BloodMoon.NightOnly`).
- **Facing:** `Entity.Forward` (where a staff member faced when marking a decoration; RealmSculptor's quarter-turn rule).
- **Popups:** `PlayerExtensions.ShowPopup(title, message, buttonText, handler, interupt, broadcast)`, in the `Popups` region, inside `try`, `broadcast = true`, plain text, chat always sent too.
- **The sky:** none. The Mods system is read at start-up (`UnityModManager.LoadModHandler`), so the moods are applied by the owner at a restart (`server/Set-Mood.ps1`).
- **Commands:** no game command is called `world`, `treasure`, `caravan` or `festival` (as far as the DLL's command table shows; see [`docs/realm-commands.md`](../../docs/realm-commands.md#game-commands)).

## Integration

| Calls into | Methods |
|---|---|
| RealmTreasury | `RewardMarks` (every mark paid), `GetTreasurySummary` (census) |
| RealmRenown | `AddDeed` with one dedupe key per event and player: `treasure_found`, `blood_moon_kill`, `caravan_escort`, `caravan_raid`, `legend_slain`, `festival_champion` (added to RealmRenown's default deeds, with the titles Hoardfinder, Caravan Warden and Bane of Legends) |
| RealmSeasons | `AwardHouse` (winners), `GetSeasonStandings` (census) |
| RealmChronicle | `Log` (`event_started` and `event_ended` for every world event and festival, the new `census_taken` for the census), `GetLastEventId` (census) |
| RealmHouses | `GetHouse`, `GetLiege`, `HasTreaty`, `GetHouseSummaries` |
| RealmEvents | `GetActiveEvents`, `GetNextEvent` (the schedule), `IsTruceActive` (fair kills) |
| RealmArena | `IsInDuel` (no caravan bearer, escort or fair kill in a duel) |
| RealmTravel | `IsTravelling`, `CancelJourney` (the bearer walks); waystone positions read from `oxide/data/RealmTravel.json` (read only; it has no method for them) |
| RealmQuests | `ReportQuestEvent(id, "event", subject, 1)` |
| RealmWarden | `IsNewPlayerProtected` |
| RealmSculptor | `PlaceSculptureAt`, `RemoveSculpture` (new, non-public: festival decorations, under `/sculpt place`'s own rules, never forced) |
| RealmPainter | `RefreshBoards("world")`; RealmPainter calls back `GetWorldBoard(arg)` for its new `world` board |
| RealmHerald | `PopupsWanted` |
| RealmSentinel | `SentinelItemSource` before goods are handed over |
| CrownAndConsequences | `GetKingName`, `GetKingHouse` (census) |

Offered to other plugins (`plugin.Call`, non-public): `GetWorldBoard(string arg)`, `GetActiveWorldEvent()` → `"kind|startIso|endIso"` or null, `GetNextWorldEvent()` → `{ title, at }` or null, `IsBloodMoon()` → `bool`, `IsCaravanBearer(string playerId)` → `bool`, `GetFestivalName()` → `string` or null.

**Chronicle.** Starts and ends use the existing `event_started` and `event_ended` types (titles such as "The hunt for The Miller's Hoard begins", "Rex plunders the merchant caravan", "House Varrow wins the Harvest Fair"). The weekly census is the new type `census_taken`, registered in `RealmChronicle.KnownTypes`, `chronicle/server.js`, `chronicle/public/assets/common.js`, the portal, the Discord herald, the launcher's heraldry, the art event map and its icon (`art/icons/census.svg`).

## Data

`oxide/data/RealmWorld.json`: places, hunts (with run and find counts), festival decorations, which slots have fired and been heralded, postponements, the turn of each kind, the running event and festival, raider prices, owed marks and goods, the finder's chest lock, the census week and the last census, players seen (ids only), and the history lines. The creature bound as a Legend is kept by view id and found again after a reload. If the file exists but cannot be read (truncated, empty, or written by a newer version) the plugin pauses, every command says so, and **the file is never overwritten**: fix or move it, then reload. RealmTravel's file is only ever read.

## Tests

```
bash tools/plugin-compile-check/check.sh                 # C# 3 against the real DLL metadata
bash plugins/docs/RealmWorld/logic-tests/run.sh           # 326 checks
bash tools/exploit-review/run.sh world                    # 48 checks with a 4000-step fuzz
node --test mods/presets/tests/presets.test.mjs           # the Harvest Fair and Midwinter moods with the rest
```

The logic tests compile `plugins/RealmWorld.cs` unchanged with `logic-tests/Mocks.cs` (players and creatures on a map, chests, a units-capacity inventory, the game clock, the interact event) and `logic-tests/World.cs` (fakes for every plugin it calls, and a clock the tests move). They cover the schedule (and the proof that the defaults never meet RealmEvents'), clashes and postponements, hunts and their layout, chests and their lock, hints, the Blood Moon and fair kills, the caravan, plunder, prices, failures, duels and waystone routes, bound and regional legends, festivals and decorations, the census, owed rewards, the painted board, data safety, config clamps and the chat style. They prove the plugin's rules, not the game's behaviour.

## Smoke test on a real server

Three players (A and B of different houses, C of A's house) and an admin S, on the owner's test server.

| # | Step | Expect |
|---|---|---|
| W1 | Load the plugin; read the log. `/world admin status`. | No "is not known" item warnings (or the names to fix in the config); the plugins line shows RealmEvents, RealmTreasury, RealmRenown, RealmSeasons, RealmChronicle, RealmSculptor, RealmPainter, RealmTravel, RealmQuests, RealmArena loaded. |
| W2 | S: `/world admin place set ford 30 The Ford`, walk 800 m, `place set mill 30 The Mill`; `/world admin route add r1 ford mill`. `/world schedule`. | Both places listed; the route reads "The Ford - The Mill"; the schedule shows the eight default slots in UTC. |
| W3 | S lays a 3-place hunt (`hunt new`, `hunt step` at each place), sets a wooden chest at the last place and `hunt chest`. `/paint world clue 1` on a sign. `/world admin start treasure`. | The Herald's line with the first riddle; the sign shows "Clue 1 of 3" and the riddle. |
| W4 | A walks to place 1, then 2. A runs to place 3 at once. | "You found place 1 of 3!" and the clue window (check it shows and its line breaks). Arriving faster than 20 s after the last place is not counted and S is told. |
| W5 | A reaches the dig site. B opens the chest; then A opens it. | The Herald's line; A's purse +150 (`/purse`); the goods are **in the chest** (they show in the chest's window); B is refused for 10 min with "This chest belongs to the treasure's finder"; A takes them. If the chest stays empty, set `Treasure.UseChest: false` (the packs take the prize). |
| W6 | `/world admin start bloodmoon 10`. A kills B; B kills A; A kills C. A wolf bites A. | A and B each get one "falls under the Blood Moon" line; the kill of C (housemate) counts nothing. The wolf's bite is noticeably harder (25%). At the end the set line. The kills are seen by the death hook (the core of this event). |
| W7 | Before a Blood Moon night: stop the server, `server\Set-Mood.ps1 -Event blood_moon`, start it, join at night. | A red moon, clear skies; another player is visible at 30 to 40 m. `-Return` afterwards. |
| W8 | `/world admin start caravan`. A at the ford: `/caravan carry`; C: `/caravan escort`. After 10 min A walks to the mill with C beside. | The muster, departure and sighting lines; on arrival A gets 120 marks, C 180. Try `/travel` as A on the road: "The waystones will not carry the merchant's goods." |
| W9 | Again; on the road B kills A. Then C kills B. | "B has plundered the merchant caravan..." with the price; B +120; C takes the 100-mark price. |
| W10 | Again; A rides a horse (if the server has them) the whole way. | The caravan is **not** lost to the jump watch (a horse covers less than 150 m in 5 s). If it is, raise `MaxJumpMetres`. |
| W11 | `/world admin creatures` in the wild. Put the words into a legend's `Kinds`; `/world admin start legend <id>`. Find and hit the creature. | The kinds are listed (e.g. "wolf"); the Herald's line says where it was seen. It takes clearly more blows than its kind usually does (Toughness 4). The killer gets the marks and the trophy; a helper with 15% of the damage a share. |
| W12 | S: `/world admin deco add harvest house-varrow` inside a crest zone; `/world admin festival start harvest 1`. A and C give Bread and Grain (`/festival give`); B gives alone. `/world admin festival stop`. | The monument rises (RealmSculptor) and comes down at the close; the goods leave the packs (the client's packs update at once); A's house wins (two givers), B's does not place; 40 season points; the best giver 100 marks. |
| W13 | `/world admin census`. | The census lines in chat; a `census_taken` entry in `/chronicle`, the Chronicle page and the portal, with its icon. |
| W14 | Set `Schedule` so a world event falls 20 min before Crown Night; watch the time. | It waits or is skipped, and S is told; no countdown is heralded for it. |
| W15 | Restart the server during a hunt and during a festival. | The hunt and the progress, the festival, its standings and decorations are still there. |

## What is UNVERIFIED

Everything at run time. In particular:

1. That a server-side `AutoMergeAdd` into a placed chest's `InteractableContainer.Contents` shows in the chest for clients, and that cancelling `OnPlayerInteract` keeps others out of it (W5); that a chest's `NetViewID` survives a restart (W15; the 1.5 m fallback covers it).
2. That `OnEntityDeath`'s `KillingDamage.DamageSource.Owner` names the killer of a player and of a creature (W6, W9, W11).
3. That scaling `Damage.Amount` in `OnEntityHealthChange` makes beasts bite harder and the Legend tougher (W6, W11); `GameClock.CurrentTimeBlock` for `NightOnly`.
4. That `Entity.TryGetAll<MonsterEntity>()` lists the living creatures on a dedicated server, and what `Entity.ToString()` calls them (W11).
5. That `AutoSplit` takes offered goods out of the packs and the client sees it at once; that the default item names resolve (W1, W12).
6. That RealmSculptor's server-placed monuments show for clients (its own PT2 steps) and come down cleanly (W12).
7. That `Player.Entity.Position` follows a player closely enough for 8 m treasure places and the caravan's jump watch (W4, W10); which world axis is north (`NorthIsPositiveZ`) (W3, W8).
8. The clue, hoard and legend windows on a real client (W4, W5, W11).
9. How the Blood Moon, Harvest Fair and Midwinter moods look (W7; `mods/moods.md`).
10. That RealmEvents' and RealmWorld's clocks agree at run time (W14), and that everything survives a restart (W15).

## Merge notes (shared files this branch touches)

- `plugins/RealmSculptor.cs`: a new `API` region with `PlaceSculptureAt` and `RemoveSculpture` (non-public), before `Chat style`.
- `plugins/RealmPainter.cs`: the `world` board (`RealmWorld` reference, `Boards` entry, `WorldBoard`, two lang lines).
- `plugins/RealmRenown.cs`: six default deeds and three default titles (Hoardfinder, Caravan Warden, Bane of Legends), with badges in `art/badges/titles/` and `art/src/titles.json` (and the copies in the portal, Chronicle, streamkit, launcher and bot).
- `plugins/RealmChronicle.cs`, `chronicle/server.js`, `chronicle/public/assets/common.js`, `portal/lib/model.mjs`, `launcher/lib/discord.js`, `launcher/renderer/heraldry.js`, `art/icons/event-map.json`, `docs/saga/COMMANDS.md`: the `census_taken` type and its icon `art/icons/census.svg`.
- `plugins/RealmHerald.cs`: four catalogue entries and `Cmd.*` lines.
- `docs/community/ops/staff-roles-and-permissions.md`: the `realmworld.admin` row and grant lines.
- `tools/exploit-review/run.sh`: the `world` suite.
- `mods/presets/`: the `harvest-festival` and `midwinter` moods and three event keys in `rotation.json`; `mods/moods.md`, `mods/README.md`.
