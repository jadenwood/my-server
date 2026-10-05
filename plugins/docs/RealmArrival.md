# RealmArrival: the Gate of the Unwritten

A newcomer's first minutes in Ostreval, from the game's own character screen on the ferryman's raft to the Hearth fire, in three to five minutes. The approved design, with every beat, line and game citation, is [`docs/arrival-design.md`](../../docs/arrival-design.md); this guide is how to install, build, run and test it.

**Status.** Compile-checked at C# 3 against the real Oxide 2.0.3867 and patched game DLL metadata (`tools/plugin-compile-check/check.sh`). Mock-tested: 650 checks (`plugins/docs/RealmArrival/logic-tests/run.sh`), including the real site plan placed at all four turns, the ember band checked cell for cell against the site file at all four turns, and a full arrival walked on it. Exploit suite: 95 checks (`tools/exploit-review/run.sh arrival`). **Nothing here has been seen working on a real server.** Every game-side assumption is listed under [What is UNVERIFIED](#what-is-unverified), each with its step in the [first-test plan](#first-test-plan-10-steps).

Speaker in chat: **Hearth** (lang key `Speaker`). Permissions: `realmarrival.admin`, `realmarrival.skip`. Files: `plugins/RealmArrival.cs`, `oxide/config/RealmArrival.json`, `oxide/data/RealmArrival.json`, and the site plan `oxide/data/RealmArrival/site.json` (a copy of `art/sculptures/sites/arrival.json`, read only).

**It ships closed** (`Open: false`) with the open arch (`GateMode: open`). Nothing changes for anyone until staff build the site, run `/arrival admin check` and `/arrival admin open`.

## What a newcomer sees

| When | What happens |
|---|---|
| Join | Nothing new. The other plugins' newcomer lines (RealmHerald's welcome and broadcast, RealmWarden's "Welcome", RealmQuests' first hint and the tale's first news, RealmTravel's kit hint) wait while RealmArrival owns the newcomer. |
| Character screen (any length) | The game's own creation on the raft: the Crossing of the Grey Water. Never changed. |
| Finish click (T+0) | The game's "Finalizing World" loader covers a move onto one of six arrival stones in the Gatehouse. Up to five other newcomers may wake beside them. |
| First move, or T+12 s (never before T+4 s) | **Wake** and **Naming** (gold), five seconds apart: who they are, the Unwritten. Then the **call** to the gold line (amber) and, a second later, the one Gatehouse window ("Open the Gate"; "Step through" with the open arch). |
| Gold line, the window's button, or 60 s | The gate opens: the portcullis sinks row by row (or the arch is simply open), the Hearth's ember band flares gold 100 m away, and the Herald tells the realm (`Herald: Ada walks out of the Gatehouse of the Unwritten, onto the road to the Hearth.`, at most 6 an hour). |
| Gate + 3 s, + 8 s | The **reveal** (the throne empty, or held by the monarch; a night version) and **walk the banners**. |
| The avenue (60 to 150 s) | Within 9 m of each of six pledge stones: that house's words and live numbers from RealmHouses. The pledge hint after the first; the honest paths (`/raven`, `/house found`, walk free) after the third. Standing 3 s on a stone asks "Look to House X?"; yes tells that house's members who are awake. Never a join. |
| 40 m from the fire | RealmQuests' "You reach The Hearth." and its achievements land first; RealmArrival holds its own lines 6 s. |
| 12 m from the fire | RealmTravel discovers the waystone `the-hearth`. Six seconds later, one line at least every 4 s: **Warm** (and a heal, once), **Kit** (`/kit starter`), **Crown** (`/crown`), **Shelter** or **Unsheltered**, then the one next step, `/quest`. |
| `/quest`, 70 m from the fire, or 60 s after the last line | **Written** (green): the arrival is done, `/realm path` keeps the first steps, and RealmHerald's reminders start from now. |
| The first hour | Each once: Three Roads at the wayboard (or 10 minutes after Written) with the next event, ten minutes of shelter left, the first block outside a crest, the first dusk, the sleeper tip on the next join, and `/arrival` on the second join. |

Returning players see nothing. Veterans on a fresh world (a known record, or seeded from RealmHerald) keep the game's spawn and get one `Veteran` line (`VeteranMode` can give them the short or full walk; a veteran who leaves that walk midway, during creation or by a world reset, stays a veteran and never gets the newcomer's Herald line, quest credit or pledges). Anyone can `/arrival skip`.

## Commands

**Players** (`/arrival`, listed in `/realm` under roads):

| Command | Does |
|---|---|
| `/arrival` | Your stage and the next step; after the arrival, your page so far (house, waystones known, tale) and the next event. |
| `/arrival skip` | Ends the arrival at once ("As you wish. The gate is open; the Hearth is yours."). From the hall it moves you 4 m out to the forecourt (with no eject point stored, the gate opens instead and stays open while you are inside); anywhere else it moves nobody. A skip from the arrival credits its first quest step (`arrival_gate`). |
| `/arrival tour` | The banners, the fire and the roads speak again the next time you pass them. Never moves anyone. |

**Staff** (`/arrival admin ...`, permission `realmarrival.admin`):

| Command | Does |
|---|---|
| `status` | Open, closed or paused; mode; gate state; live arrivals; counters (routed, re-routed, unconfirmed, road mode, released, skipped, written, popups sent and answered, pledges per house, evictions, self-check closures, mercy); mean seconds per stage; the last 10 timings; the block grid binding. |
| `site` | The site file, the anchor, the pair lot, then the check. |
| `site anchor [+z\|+x\|-z\|-x]` | Stand in the `gateSet` cell (the bottom-left cell of the empty gate opening) facing out toward the Hearth. Stores every point and box from the plan: stones A1-A6, mercy M1-M3, E, threshold, hearth, wayboard, the banners by the lot, the hall box Z0, the drop pad Z0b and the gate's place. The facing is where you look (`Entity.Forward`) unless given. Refused while the gate or band cells stand (they belong to the old anchor). |
| `site plan` | Every point the plan names, its world position, and whether what is stored matches (within 1.5 m). |
| `site pieces` | The run-sheet of sculptures in route order: where to stand, which way to face, and the `/sculpt place` command with the lot's house and the world turn. |
| `site signs` | The 16 sign spots with their `/paint` binding (crests by the lot) and the notice texts. |
| `site reload` | Reads the site file again. |
| `check` | Lists every problem (refuses `open` until there are none) and notes (warnings). See [the check](#the-check). |
| `runsheet` | The after-wipe list with what the plugin can see of each step. |
| `lot` / `lot draw` / `lot set <six houses>` / `lot clear` | The pair lot: which great house stands in which slot (`p1-left` ... `p3-right`). `draw` casts it on the server and the Herald tells the realm; `set` records a lot drawn elsewhere (dice on stream). The banner points follow. |
| `open` / `open force` / `close` | Open routes newcomers into the Gatehouse (only once the check passes). `open force` is for a test server: it opens despite the problems, says and logs each one, and the self-check still closes it if a stone loses its floor. `close`: vanilla spawns for new players; arrivals under way finish; `ArrivalStage` answers `none`. |
| `pause` / `resume` | Instant global off: vanilla spawns, the provider restored, the gate opened, every arrival handed back to RealmHerald's normal welcome. Nobody is moved and nobody is evicted while paused; at `resume`, a handed-back newcomer still in the hall is let out to E with `ReleasedGate` (never an eviction), and one who was offline is let out the same way when they next wake there. |
| `mode teleport\|provider\|road\|off` | Routing (see [Routing](#routing-and-its-fallbacks)). |
| `gatemode open\|portcullis` | The open arch (default) or the sinking portcullis. |
| `stone add\|remove <n>\|list\|clear`, `mercy add\|remove <n>\|list\|clear` | Store stones where you stand (when not anchoring the plan). |
| `hall corner1\|corner2`, `droppad corner1\|corner2`, `eject set`, `threshold set`, `hearth set`, `wayboard set`, `throne set` (each also `clear`) | Store the boxes and points where you stand. |
| `banner set\|clear <house>` | Stand on a pledge stone. |
| `gate set <w> <h> [+z\|+x\|-z\|-x]`, `gate build\|open\|close\|test\|remove` | The portcullis cells (plugin-owned). With the plan anchored, `gate build` writes the plan's exact 30 cells. `test` opens it and closes it again after `GateMinOpenSeconds`, even while the arrival is closed. |
| `beacon build <radius> [dy]`, `beacon clear`, `beacon test` | The ember band: exactly the site file's `cells.emberBand` (24 cells at radius 5, the octagon in its `rule`, not 15-degree steps). With the plan anchored, the plan's own cells, wherever the hearth point is stored; with the file but no anchor, its offsets round the stored Hearth; without the file, the same octagon built in. |
| `evict on\|off`, `wave on <minutes>\|off` | Hall eviction (never staff with `realmarrival.skip` or `realmarrival.admin`, never while paused); launch-wave mode (gate held open, no window, two per stone, overflow to the mercy stones). |
| `play <player>`, `skip <player>`, `reset <player> [pending\|done]`, `veteran <player>`, `pass <player>` | A staff run (no Herald line, quest credit or deed; refused in a fight); end someone's arrival; reset a record; mark a known veteran; open the gate for someone stuck, or move them out of the hall. |

Realm Steward's Court lists the staff commands that do not depend on where you stand (`status`, `check`, `site`, `site plan|pieces|signs|reload`, `runsheet`, `lot`, `lot draw`, `open`, `close`, `pause`, `resume`, `mode`, `gate test`, `beacon test`, and `skip`, `pass`, `reset`, `veteran` for a player) ready to copy (`launcher/lib/moderation.js`); they are typed in game, like every plugin command.

## The site

**The plan.** `art/sculptures/sites/arrival.json` (format `realm-site/1`, [`art/sculptures/README.md`](../../art/sculptures/README.md) "Site layouts") places the Gatehouse, the processional, six house monuments with their pledge stones, the hearth ring, the Herald's Pillar and the wayboard on one axis, and names every spot RealmArrival needs. On the server it is `oxide/data/RealmArrival/site.json`. Realm Steward's **Update plugins** (the installer packs it as `resources\realm-data\RealmArrival\arrival.json`) and `server\Deploy-Plugins.ps1` copy it there under that name, with the same rules as the other data files: a damaged source is refused and the server's copy kept, a changed `site.json` is saved to `_realm-backups\data-<time>\RealmArrival\` first, other files in the folder are never touched, and a running RealmArrival is reloaded by Steward (Deploy-Plugins prints the `oxide.reload RealmArrival` to type). After a deploy, `/arrival admin site reload` (or the reload) reads it again. By hand, without either:

```
copy art\sculptures\sites\arrival.json G:\RealmTest\server\oxide\data\RealmArrival\site.json
```

The plugin only reads it, and only the fields it needs: anything else in the file (`lights[]`, `terrain[]`, notes, fields a later version adds) is ignored, so the art side can add to the format without breaking the plugin. Without it, every point is stored by staff standing on it, and the check notes that the plan cannot be compared.

**Coordinates.** The plan is in block cells (1.2 m). `/arrival admin site anchor` finds the world cell of the plan's origin from where you stand and which way you face: world cell = anchor + turn(site cell, R), with `turn([x, y, z], 1) = [z, y, -x]`, the same quarter-turns `/sculpt place` uses. A point a player stands at is the cell's centre (`RootCubeGrid.LocalToWorldCoordinate`) lowered to just above its floor; arrival teleports go 0.5 m above that.

**Plugin-owned cells.** The portcullis (30 reinforced cells, Iron 900) and the ember band (24 clay cells, Ember deep resting, Ember hot when it flares; they rest on the hearth ring's dais, one cell above it at the Hearth centre's height, `restsOn: hearth-ring`) are never RealmSculptor cells, so `/sculpt repair` and `/sculpt remove` never touch them. RealmArrival records them, protects them (`OnCubeTakeDamage`, `OnCubePlacement`) and forces the gate open on every load.

**First build** (after PT2, on the test world first):

1. Put the staff crests over the site (two to four; crest radius UNVERIFIED).
2. `/arrival admin lot draw` in public (or `lot set` with a lot drawn on stream).
3. Stand in the `gateSet` cell on bare ground, facing toward where the Hearth will be: `/arrival admin site anchor`.
4. `/arrival admin site pieces`: walk the list in route order and place each piece with the command it prints. Then build the fire pit on the hearthstone, the fire bowls in the court and the braziers along both kerbs (ordinary building).
5. `/arrival admin site signs`: place and bind the 16 signs with `/paint` (at most 6 redraws a minute across the realm: allow three minutes).
6. Waystones: `/travel admin set the-hearth capital` at the fire, radius 12, `heralds-pillar` as its monument; `crown-market` and `listing-field` for the Three Roads.
7. RealmQuests' place `the_hearth` and RealmLaws' zone `Hearth` must be centred within 3 m of the stored hearth.
8. `/arrival admin gatemode portcullis` (only after first-test step 3), `gate build`, `beacon build 5`.
9. `/arrival admin check`, then `/arrival admin open`.

## After every wipe (run-sheet)

Sculptures, signs, crests, fire pits and the plugin's gate and band cells are world objects, so a wipe removes them. Stored points, the anchor, the lot, waystones and RealmArrival records are Oxide data, so they survive. The plugin checks itself at load and every `SiteSelfCheckMinutes` (60): if a stone's floor (the plan's `stoneFloors`) is missing it closes the arrival by itself and logs why, so a fresh world never routes newcomers onto empty ground.

`/arrival admin runsheet` prints this list with what the plugin can see of each step:

1. `/arrival admin close`.
2. Put the staff crests back.
3. Draw the pair lot in public: `/arrival admin lot draw`.
4. Place the pieces in route order (`/arrival admin site pieces`), then the fire pit and lights.
5. Rebind the 16 signs (`/arrival admin site signs`).
6. `/arrival admin gate build` and `/arrival admin beacon build 5`.
7. Re-store any point that moved (`/arrival admin site plan`).
8. `/arrival admin check`, then `/arrival admin open`.

(The design says 15 signs; its own table and the site plan list 16: G1-G4, P1-P6, W1-W5 and H1.)

## The check

`/arrival admin check` (and `open`) refuses while any of these is wrong: no stones, no mercy stones, no hall box, no eject point, gold line or hearth; portcullis mode without a built gate; the eject point inside the hall box (eviction would loop), a stone outside it (every arrival check would fail into road mode), or a mercy stone inside it (deaths would go back in); a stone without two air cells or a block floor; a mercy stone or E without two air cells; with the plan anchored, a missing stone floor; the hall box or a stone inside the Hearth town zone; the hall within 80 m of the Old Throne; any site point inside a RealmDominion holding or an arena zone; the hearth more than 3 m from RealmQuests' `the_hearth` or RealmLaws' `Hearth`; RealmSentinel or RealmTravel not loaded; no waystone `the-hearth`. It notes (without refusing): no drop pad, no wayboard, fewer than six pledge stones, the throne's position unknown, the site file missing or unusable, the lot not drawn, the plan not anchored, points more than 1.5 m off the plan, gate cells off the plan, and any zone radius in the plan that differs from the config.

## Routing and its fallbacks

| Mode | What it does |
|---|---|
| `teleport` (default) | In the same tick as `OnPlayerSpawned`: `RealmSentinel.SentinelGrace(id, 30)`, `RealmTravel.CancelJourney`, then `CharacterTeleport.Teleport` to the chosen stone + 0.5 m (after `TeleportDelaySeconds`, 0 to 2). |
| `provider` | Wraps `SpawnpointManager.defaultSpawnpointProvider`: the game's own teleport at the Finish click goes to the stone, for a marked player only and only once. Restored on Unload, pause and any mode change. Only after first-test steps 2 and 9. |
| `road` | No move: the newcomer is released where the game put them with `/road the-hearth`; the fire beats and the Herald line come at the fire. |
| `off` | Vanilla. |

Arrival checks at T+3 s and T+10 s: outside the hall box, one more teleport (counted unconfirmed); still outside, road mode. A run that has never reached the hall 8 s after the last check (its check timers died with a reload) goes to road mode too, so nobody waits in the `gatehouse` stage outside the Gatehouse. While portcullis cells are stored but the block grid is not bound (a load right after a crash that left the gate closed), newcomers go by road mode until the bind's retry forces the gate open. Stones are used least recently first, a stone with a player in arrival or any sleeper within 1.5 m is taken, each placement gets up to 1.2 m of jitter when the stone is shared, each stone takes a second player when all six are taken, and beyond twelve the newcomer goes to a mercy stone at the Hearth with the short lines.

## Config (`oxide/config/RealmArrival.json`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Off: nothing at all. |
| `Open` | false | Set by `open` / `close`. |
| `RoutingMode` | `teleport` | `teleport`, `provider`, `road`, `off`. |
| `TeleportDelaySeconds` | 0 | 0 to 2. |
| `ArrivalCheckSeconds` | [3, 10] | When the move is checked. |
| `GateMode` | `open` | `open` or `portcullis`. |
| `AutoOpenSeconds` | 60 | The gate opens by itself this long after narration starts. |
| `GateMinOpenSeconds`, `GateCycleMinSeconds`, `GateClearanceMetres`, `GateRowSeconds` | 15, 20, 3, 0.4 | The portcullis cycle: open at least 15 s, openings 20 s apart, never closing with anyone within 3 m, a row every 0.4 s. |
| `FlareSeconds`, `FlareMinIntervalSeconds` | 20, 60 | The ember band's flare. |
| `NarrationStartMoveMetres`, `NarrationStartCapSeconds`, `NarrationStartMinSeconds` | 1.0, 12, 4 | When narration starts. |
| `LineGapSeconds`, `QuietAfterHearthSeconds` | 4, 6 | Line spacing; the hold at the 40 m ring. |
| `BannerDropMetres`, `HandoverRadius`, `RouteCorridorMetres`, `WanderOffRouteMetres` | 30, 70, 20, 40 | Route rules. |
| `TimeoutMinutes`, `AfkReleaseMinutes`, `ResumeRadius`, `StaleHours` | 8, 30, 60, 48 | Caps, resume and stale records. |
| `VeteranMode` | `vanilla` | `vanilla`, `short`, `full`. |
| `StaffToHearth` | false | `realmarrival.skip` holders go to a mercy stone instead of the game's spawn. |
| `MercyRespawns` | 1 | Hearth's Mercy per Steam id while protected (0 to 3). |
| `PledgesPerHousePerHour`, `NewcomerBroadcastsPerHour` | 6, 6 | Caps. |
| `ShieldSecondsAfterRelease`, `ArrivalZoneGuardMinutes`, `EvictSeconds`, `Evict` | 30, 15, 3, true | Safety. |
| `SiteSelfCheckMinutes` | 60 | The self-check. |
| `UsePopups`, `InterruptPopups`, `PopupAnswerSeconds` | true, false, 120 | Windows (`InterruptPopups` only after first-test step 4). |
| `UseNewsToasts`, `GateSound`, `HearthBuff` | false, "", false | Extras, off until step 9. |
| `HealAtHearth` | true | Heal, nourish and hydrate once at the fire. |
| `UseCompassWords` | false | Nudges say north or east only after RealmTravel T12 confirms +z is north. |
| `HourOneTips` | true | The first-hour follow-ups. |
| `SeedFromHerald` | true | Once, on the first load: known veterans from `RealmHerald.json` (only those who really played). |
| `WrittenDeed` | "" | A RealmRenown deed kind to give on Written. Off: RealmQuests' `ex_written` pays the `written` deed; set it to `written` only without RealmQuests. |
| `HearthWaystone`, `RoadsWaystone` | `the-hearth`, `crown-market` | Waystone ids. |
| `LogHooks` | false | First-test step 1: every spawn hook and the first move with timestamps. |
| `HallHeightMetres`, `StoneRadius`, `StoneJitter`, `GoldLineRadius`, `BannerRadius`, `PledgeRadius`, `PledgeDwellSeconds`, `QuietRingRadius`, `HearthStoneRadius`, `WayboardRadius` | 16, 1.5, 1.2, 2.5, 9, 1.5, 3, 40, 12, 8 | Zones (the check compares them with the plan's zones). |
| `NudgeAfterSeconds`, `NudgesPerStage` | 30, 2 | Nudges. |
| `GateMaterialId`, `GateColour`, `BeaconMaterialId`, `EmberDeep`, `EmberHot` | 9, `#131417`, 3, `#9c6a1e`, `#f4c96d` | RealmSculptor roles `reinforced` and `clay` (UNVERIFIED until `/sculpt materials`). |
| `WaveMode`, `WaveUntil` | false, "" | Set by `wave`. |

## Integration

**Offered** (non-public, `Plugin.Call`): `ArrivalStage(string id)` returns `pending` (no record or not yet spawned, while open), `crossing`, `running` (in the Gatehouse, released, on the banners or at the Hearth), `done` or `none`; `OwnsArrival(string id)` is true for the first three. Both answer from saved data alone, so load order does not matter. One session-only exception: a pending record whose Finish click the plugin saw without seeing creation start (it was loaded mid-creation) answers `none` until that player logs off, so the other plugins' welcome is not held back; nothing is saved, and a stray event cannot turn a newcomer into a veteran.

**Guards in other plugins** (design 7.3, on this branch): RealmHerald defers its welcome and newcomer broadcast while the arrival owns a newcomer and, once done, sends the MOTD alone and starts the reminders; RealmWarden skips its join "Welcome", starts play time at the first spawn (character creation no longer uses the protected hour) and offers `GetProtectionMinutesLeft`; RealmQuests skips the first hint and holds the tale's first news; RealmTravel skips the 45 s kit hint. RealmRenown has the deed kind `written`. RealmHerald's `/realm` catalogue lists `/arrival` under roads.

**Quest credit.** Variant A only, never a staff run: `ReportQuestEvent(id, "custom", subject, 1)` with `arrival_gate`, `arrival_banners` (three banners heard), `arrival_pledge`, `arrival_hearth`, `arrival_crown` (`/crown` during the arrival or its first hour), `arrival_road` (a second waystone within 2 h). RealmQuests turns them into the `ex_written` achievement (five of six, renown only) and the **Unwritten** chain (`plugins/docs/RealmQuests/content/Arrival.json`: through the gate, warm hands, who sits the throne, the first road), shown in `/quest` for a newcomer's first week. The chain carries no reward of its own, because `ex_written` already pays the `written` deed (the design's v2 sketch put the same renown on both).

**Calls made**: `RealmSentinel.SentinelGrace`; `RealmTravel.CancelJourney`, `GetDiscoveredCount`, `IsTravelling`; `RealmWarden.IsNewPlayerProtected`, `IsInCombat`, `RaiseWardenAlert` (`arrival_camp`), `GetProtectionMinutesLeft`; `RealmHouses.GetHouseSummaries` (cached 30 s), `GetMembers`, `GetHouseLeader`, `GetLiege`, `GetHouse`; `CrownAndConsequences.GetKingName`, `GetKingHouse`; `RealmEvents.GetNextEvent`; `RealmArena.IsDuelBlow`; `RealmQuests.ReportQuestEvent`, `GetStoryProgress`; `RealmRenown.AddDeed` (only with `WrittenDeed`); `RealmHerald.PopupsWanted`. Every one is optional: a missing plugin reads as "not available".

**Fallbacks without the guards** (if a guard change is not merged): set RealmHerald `WelcomeNewPlayers`, `WelcomePopup` and `HeraldNewcomers` false and RealmTravel `Kits.NewcomerHint` false while the arrival is open; set them back when it is closed.

## Data

`oxide/data/RealmArrival.json`: one record per Steam id (it survives wipes), the stored site (points, gate and band cells, the anchor, the lot) and the counters. Saved on every stage change (debounced), on `OnServerSave` and on Unload. If the file exists but cannot be read, routing is off (vanilla spawns), `ArrivalStage` answers `none`, and the file is never overwritten: fix it or move it away, then reload. A file that reads but holds nulls or bad entries (a hand edit) is repaired on load: null points and cells dropped, unknown houses and stages dropped or read as `done`, the used-time lists matched to the stones, the turn wrapped to 0-3. The site file is only ever read.

## Tests

```
bash plugins/docs/RealmArrival/logic-tests/run.sh      # 650 checks
bash tools/exploit-review/run.sh arrival               # 95 checks (A1-A21 in tools/exploit-review/arrival/README.md)
```

The logic tests compile the plugin unchanged with `Mocks.cs` (the game and Oxide surface it touches, a grid that rounds cells as the game does, a virtual clock) and `World.cs` (a site on the +z axis and fake RealmSentinel, RealmTravel, RealmWarden, RealmHouses, CrownAndConsequences, RealmEvents, RealmArena, RealmQuests, RealmRenown and RealmHerald). `Tests4.cs` copies the real `art/sculptures/sites/arrival.json`, anchors it at all four turns, computes every expected cell from the file itself and compares, then walks a newcomer from the Finish click to Written on the anchored plan. `Tests5.cs` holds the integration review: the ember band against the file at all four turns (anchored, unanchored and the built-in fallback), a site file full of unknown fields, the stuck-in-the-gatehouse cases (reload before the first move, pause, skip with no E, a closed gate whose grid is not bound), veterans re-crossing, the check's loop guards, staff and eviction, a full arrival with RealmQuests, RealmWarden and RealmHerald (then every Realm plugin) unloaded, and a damaged but readable data file. They prove the plugin's rules, not the game's behaviour.

## First-test plan (10 steps)

On `G:\RealmTest\server`, in order, with two Steam accounts (a fresh one and a watcher). Each step names the design's play-tests it settles (`docs/arrival-design.md` 9.5). Write each result in the last column and remove the UNVERIFIED tag it settles. Steps 1 and 2 need no site.

| # | Do | Pass if | Settles | Result |
|---|---|---|---|---|
| 1 | **Hooks.** `LogHooks: true`, `Open: false`. Join with a fresh account; finish creation; walk. Reconnect; die once without a bed. Read `Mods\<level> Spawn Points.defaults.cfg`. | The log has `OnPlayerConnected`, `OnPlayerSpawn` (`AtFirstSpawn True` and the raft position), `OnPlayerSpawned` and `first move ... s after OnPlayerSpawned`, with timestamps; the respawn subclass is logged. Note how long creation takes and which greeting lines show on the character screen. | design 1, 2 | |
| 2 | **The move.** A test site: `hall corner1/2` round one stone on bare flat ground, `stone add`, `mode teleport`, `open force`. A fresh account; check its position on the server and both clients at 3 s and 10 s. Repeat with `TeleportDelaySeconds` 1, then with the stone on a placed block floor. Record the client at 60 fps round Finish. | It lands on the stone and stays; `status` shows no unconfirmed arrivals; no frame at the vanilla point (or note how long). | design 3, 4 | |
| 3 | **Gate blocks.** On a test wall: `gate set 5 6` in the opening, `gatemode portcullis`, `gate build`, `gate test`, with the watcher at 5 m and 30 m. Restart while it is closed. `hearth set`, `beacon build 5`, `beacon test` from 100 m. Try to climb the Pilgrim's ledge from outside. | Rows sink top first in about 2.4 s and come back; no collapse, salvage drop or page artefact; open after the restart; the band flares and fades; the ledge cannot be climbed. | design 6, 6b | |
| 4 | **Windows.** `/arrival admin play <you>`. Then `InterruptPopups: true` and again. Stand on a pledge stone. | The card shows with its line breaks and "Open the Gate" / "Step through"; the button opens the gate; note whether interrupt traps input; the pledge window shows "Look to X" / "Walk on" and the answer comes back. | design 7 | |
| 5 | **Build the site** (after PT2). Deploy the site file (Steward's Update plugins or `Deploy-Plugins.ps1`; `site` shows it loaded); follow [First build](#the-site): the lot, `site anchor`, `site pieces`, the fire pit and lights, `site signs`, waystones, `gate build`, `beacon build 5`, `runsheet`, `check`. | `site plan` shows every point stored; the run-sheet is all done; the check passes. Note whether one crest covers the site and how the court stands under `blockCollapsing`. | design 5, 8 | |
| 6 | **The full run.** `open`. A fresh account from Finish to Written with a stopwatch, once by day and once by night. Then pledge with a house member online, and with none online; `/raven <house>`; `/house join <house>` after an invite. | 3 to 6 minutes; at the Hearth Quests' lines at 40 m, the discovery at 12 m, then the fire lines, never interleaved; `/kit starter` delivers; Warden reports protection; the night reveal at night; the house is told. | design 9, 10 | |
| 7 | **Safety.** The watcher loiters in the hall (eviction to E), strikes in the hall, uses it mid-fight; on the avenue melee, bow and rope during the shield, with Warden unloaded (the zone guard) and afterwards. Log off in the Gatehouse, on the avenue and far away; `reset <you> done` and wake in the hall. Die while protected (Randomly and Normal), again, and at a bed. | Evicted to E after 3 s; no damage to a newcomer in the hall (sleeper too); a veteran gets none; the shield and guard as the design says; resume or release lines; the stale wake-up is let out, not evicted; one mercy, then vanilla; the bed untouched. | design 11, 12, 13 | |
| 8 | **Provider and veterans.** `mode provider`; repeat step 2's recording; two fresh accounts, one marked; respawn Randomly with each; restart with a Spawn Points mod file. Then on a new save slot: a seeded id in each `VeteranMode`; `/arrival skip`, `/arrival tour`; the self-check with the site missing. | No `SpawnerModHandler` exception; the unmarked player gets vanilla points; each veteran mode as designed; the self-check closes the arrival naming the stone. | design 14, 15 | |
| 9 | **Extras and waves.** `UseNewsToasts` (during and after the loader), each `GateSound` id, `HearthBuff`. `wave on 30` with 5 alts within a minute; then 13 alts without wave mode. | Leave off any that fail; two per stone with jitter; the thirteenth to a mercy stone. | design 16, 18 | |
| 10 | **Unload, pause, Sentinel, greetings.** With the gate closed and a newcomer inside: `oxide.unload RealmArrival`, then load; then `pause`. Read RealmSentinel's findings for all of the above. After the guard changes, a fresh account's greetings. | The gate opens, nobody is stuck, the paused newcomer gets RealmHerald's normal welcome; no Sentinel findings; exactly one welcome voice. | design 17, 19, 20 | |

## What is UNVERIFIED

Everything at run time. In particular, with its fallback (design section 10):

| Assumption | Step | If it fails |
|---|---|---|
| `OnPlayerSpawn(PlayerFirstSpawnEvent)` sees `AtFirstSpawn`; `OnPlayerSpawned(PlayerPreSpawnCompleteEvent)` fires once per new character after the game's own teleport | 1 | Nothing moves: the arrival never starts (`status` shows routed 0). Keep it closed. |
| A teleport from `OnPlayerSpawned` sticks; the loader covers it; the hall's pages load | 2 | `mode road`, or `TeleportDelaySeconds` 1 to 2; the T+10 check re-teleports once. |
| No glimpse of the vanilla point | 2, 8 | Try `provider`; else accept a sub-second glimpse. |
| The portcullis rows sync smoothly; no crush, salvage drop or page artefact; `GateMaterialId` 9 is reinforced | 3 | `gatemode open`: the gold line is the moment. |
| The ember flare shows from 100 m | 3 | `beacon clear`. |
| Windows show, keep custom labels, and answers come back | 4 | The chat lines always stand; the gate opens on the gold line; pledges confirm by standing. |
| `Entity.Forward` gives the admin's facing (`site anchor`, `gate set`) | 5 | Give the facing: `site anchor +x`. |
| `LocalToWorldCoordinate` is the cell's centre and the grid is the world's 1.2 m grid (the stand points of the plan) | 5 | Re-store the points by standing on them; `site plan` shows the difference. |
| Sculptures, colours and signs reach clients | 5 (PT2) | The board texts are already in chat. |
| `OnEntityHealthChange` covers players and sleepers in the box | 7 | Walls, Warden protection, eviction and stewards; shorter `AutoOpenSeconds`. |
| `Heal`, `Nourish`, `Hydrate` work | 6 | `HealAtHearth: false`. |
| The game clock read on the server follows the sky (night reveal, dusk tip) | 6 | Skip the dusk tip; the reveal uses the day lines. |
| `OnPlayerCommand` sees Oxide chat commands (`/quest`, `/crown`) | 6 | The 60 s and 70 m handovers do not need it. |
| Provider mode keeps vanilla behaviour for everyone else | 8 | Stay on `teleport`. |
| Toasts, audio ids, the HUD buff | 9 | Shipped off or empty. |

## Merge notes (shared files this branch touches)

- `plugins/RealmHerald.cs`, `RealmWarden.cs`, `RealmQuests.cs`, `RealmTravel.cs`, `RealmRenown.cs` and their logic tests: the guards above (design 7.3), each with tests; RealmQuests also gains the Unwritten chain and `content/Arrival.json`; `content/Achievements.json` gains `ex_written`.
- `plugins/docs/RealmHerald/logic-tests/Tests.cs`: `RealmArrival` in `AllPlugins`.
- `tools/exploit-review/run.sh`: the `arrival` suite; every suite now receives the repo path as its first argument (the others ignore it).
- `docs/saga/locations.md` (L13; `check-saga.mjs` expects 13 locations), `docs/saga/README.md`, `docs/saga/calendar-8-weeks.md`, `docs/saga/COMMANDS.md` (regenerated), `docs/community/lore.md`, `docs/community/how-to-play.md`, `docs/community/ops/staff-roles-and-permissions.md`, `docs/realm-commands.md`, `docs/realm-systems.md`, `docs/ROADMAP.md`.
- The site plan and its sculptures come from `team/arrival-site` (already on `claude/great-maxwell-wrksvt`); this branch only reads them.
