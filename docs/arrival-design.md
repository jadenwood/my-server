# Arrival design: the Gate of the Unwritten

Status: design only. Nothing here is built. Everything a player would see is **UNVERIFIED in game**, because so far the real server has only proven join and plugin load (`docs/HANDOFF.md`). Game facts cite `type.member` from the decompile of the patched `Assembly-CSharp.dll`. Plugin facts cite `plugins/*.cs`. No decompiled code is quoted.

New plugin: `plugins/RealmArrival.cs`, speaker **Hearth**, player command `/arrival`.

---

## 1. Summary

A new player makes their character on the game's own raft. In the Realm story, that is the end of the **Crossing of the Grey Water**. When they press Finish, the game's own "Finalizing World" loading screen covers a move into the **Gatehouse of the Unwritten**. This is a walled stone court about 100 m from the Hearth (L02). Up to five other newcomers may be waking beside them.

Two lines tell them who they are: unwritten, with no house, no oath and no debt. Then they walk to a gold line in front of the gate. The gate sinks into the ground row by row, built from real synced blocks. Through it they see one composed view: an avenue between the six 30 m house monuments, the Hearth fire at its end, and the Old Throne on the hill beyond. The ember ring round the fire flares gold. The Herald tells the realm that someone has come through, which brings house recruiters to the fire.

On the walk, each house introduces itself with live data. A pledge stone lets the newcomer "look to" a house, and that house's online members are told. Near the fire, things that already exist land in a planned order: RealmQuests' Hearth visit (at 40 m), then RealmTravel's discovery of the first waystone (at 12 m), then the arrival's own fire lines pointing to the starter bundle. The arrival ends on one command, `/quest`, about 3 to 5 minutes after Finish.

Returning players see nothing new. Veterans on a fresh world keep the game's normal spawn and get one line, unless staff choose the short version. Anyone can `/arrival skip`. If the gate, the move or the popups fail, the design falls back to cheaper versions. The arrival never gives items or marks of its own. The existing RealmQuests achievements for visiting the Hearth still pay their normal marks, from the existing shared budget.

---

## 2. Story framing and place

### The Unwritten

The lost winter blanked the Chronicle's pages (in story terms, the wipe). The people who come now have no page in it. The Charter's first clause says the crown belongs to the seat, not the blood, so a person with no page may still rise. The arrival promises that by the end of the first hour, the Chronicle can write you. This ties into the Season 1 prologue and epilogue ("the realm must write its own") and RealmHerald's line "the realm remembers what you do".

### The Crossing of the Grey Water

The game's character creation already puts a first-timer on a raft at a `PlayerPreSpawner` point (`FirstCharacterCreation.OnPlayerFirstSpawn`, `RaftPrefab`). Realm calls this the Crossing. The ferrymen of the Grey Water have carried for every side since the Long Thaw and are sworn to no house. They ask for your face and your banner, which is the customize screen. Their raft leaves you at the Gatehouse. The design never moves the raft: creation stays exactly as the game makes it.

### The Hearth Calls

Since the Charter, the Stewards of the Seat have kept a gate below the Hearth for anyone who wants to be counted. When the throne stands empty, they feed the fire high, and its smoke is the summons. That is why everyone arrives at the same fire. The Herald's Pillar and the fire pit are the physical summons.

### New place: L13 The Gatehouse of the Unwritten

This entry goes into `docs/saga/locations.md`.

- **Lore:** a walled court of pale stone below the Hearth, older than the Charter. Its gate opens only for the Unwritten. The Stewards say that anyone who walks out of it is written from that moment on.
- **Place it at:** flat ground on the far side of the Hearth from the throne hill, so that the line from gate to fire to Old Throne is roughly straight.
  - The gate stands about 100 m from the fire.
  - The Gatehouse itself (hall box and stones) stays outside the Hearth's 40 m town zone. The far end of the avenue, the hearth ring, the pillar and the wayboard lie inside that zone on purpose.
  - No part of the site may sit inside a RealmDominion holding circle or an arena zone.
- **Mechanics:** RealmArrival only. It is not a waystone, a town zone, a quest place or a holding.
- **Story use:** every newcomer's first minute. The Hearth Truce on the season's last Sunday brings the realm back to the place each newcomer first arrived.

The Hearth (L02) stays the hub. It is already the RealmLaws zone `Hearth` (radius 40), the RealmQuests place `the_hearth` (radius 40) and the site of Season 1 step 1. The fire pit must stand at the centre of both circles. The Old Throne (L01) is the view at the end of the avenue, not the hub: protection is lost there, rebellions are fought there, and travel is blocked within 80 m of it during a rebellion.

---

## 3. Beat-by-beat flow

T starts at the Finish click. W starts when narration begins (see beat 4).

### Records and variants

Each Steam id has one RealmArrival record. It survives wipes. Its `Stage` is one of:

| Stage | Meaning |
|---|---|
| `pending` | connected, not yet seen in `OnPlayerSpawn` |
| `crossing` | in character creation |
| `gatehouse`, `released`, `banners`, `hearth` | in the arrival |
| `done` | finished, or a known veteran |
| `none` | not handled (closed, paused, staff skip) |

`PlayerFirstSpawnEvent.AtFirstSpawn` is the inverse of `Character.HasCompletedCreation` and is stored per world save. The variant is decided in `OnPlayerSpawn`:

| Variant | Test | What they get |
|---|---|---|
| A, new to Realm | `AtFirstSpawn` true, and either no record or a stage of `pending`, `crossing` or any in-arrival stage | the full arrival (an in-arrival stage with `AtFirstSpawn` true means the world was reset mid-arrival, so it restarts) |
| B, veteran on a fresh world | `AtFirstSpawn` true and the stage is `done` (or the id was seeded from RealmHerald) | by default the game's normal spawn and one line (see Variant B) |
| C, returning player | `AtFirstSpawn` false | nothing new, or a resume if they left mid-arrival. An id with no record, or a `pending` record, is saved as `done` (a known veteran), so a later wipe treats them as B. |

### 0. Before joining (optional, launcher)

The coach card (`launcher/renderer/coach.html`, shown for 3 minutes while the game loads) gets one line that reads well for everyone: "Every crossing ends at the Hearth. Once ashore, follow the banners." The app has no player id, so it cannot address first-timers.

### 1. Connect (`OnPlayerConnected`)

RealmArrival creates a `pending` record if none exists. It sends nothing.

While the arrival is open, `OwnsArrival(id)` is true for an id with no record or with a `pending`, `crossing` or in-arrival stage. That answer comes from saved data alone, so it does not matter which plugin's `OnPlayerConnected` runs first (Oxide does not define the order).

The other plugins' newcomer lines each check this answer and stay silent (section 7.3):

- RealmHerald's welcome (in its delayed timer) and its newcomer broadcast (at connect);
- RealmWarden's "Welcome" (at connect);
- RealmQuests' "FirstHint" and "TaleBegins" (at connect);
- RealmTravel's 45 s kit hint (in its timer).

The server's `greeting` setting stays empty, because the game sends it at every session's first spawn, to returning players too.

### 2. The Crossing (vanilla character creation, any length)

In `OnPlayerSpawn(PlayerFirstSpawnEvent e)`, RealmArrival reads `e.AtFirstSpawn` and chooses the variant (table above).

- **Variant A:** the stage becomes `crossing` (in memory and on disk), and the session is noted as the one in which creation started.
- **Never changed:** `e.Position` and `e.AtFirstSpawn`. Changing the position would move the raft. Setting `AtFirstSpawn` to false breaks creation (the client never sends `PlayerPreSpawnCompleteEvent`).

### 3. The cut (T+0, `OnPlayerSpawned(PlayerPreSpawnCompleteEvent)`)

This hook fires once per new character. By then `PlayerListener.OnPreSpawnComplete` has already teleported the player to `SpawnpointManager.GetRandomSpawnPoint()` and set `HasCompletedCreation`. The client is entering the game's "Finalizing World" loader (`SpawnColumnHandler.OnPlayerPreSpawnComplete`, 2 to 10 s).

RealmArrival acts only if the record's stage is `crossing`. Any other stage, a repeat, or an event for an id it did not mark is ignored. If the arrival is closed or paused at this moment, the stage becomes `none` and RealmHerald welcomes the player as it does today.

**Choosing a stone.** Stones are used least-recently-used first. A stone counts as taken if an online player in arrival, or any sleeper, is within 1.5 m of it. Each placement gets up to 1.2 m of jitter. If all six are taken, each stone takes a second player with jitter. Beyond 12 players, the newcomer goes to a mercy stone at the Hearth with the short lines (as in wave mode).

- **`teleport` mode (default).** RealmArrival calls `RealmSentinel.SentinelGrace(id, 30f)` and `RealmTravel.CancelJourney(id)`. Then, in the same tick, it calls `CharacterTeleport.Teleport` to the chosen stone + 0.5 m. `TeleportDelaySeconds` is 0, configurable from 0 to 2. It does not touch `e.PostSpawnPosition`: the game's teleport has already run, so the field does nothing.
- **`provider` mode** (switched on only after play-tests 4 and 14). The game's own teleport already went to the stone (section 7.1), so nothing more is needed.
- **Arrival checks at T+3 s and T+10 s.** If the server position is not inside the hall box, RealmArrival teleports once more and counts the arrival as unconfirmed. A second teleport after the loader is a visible cut, which is accepted.
  - Two checks are needed for different reasons. `MovementStatisticCollector` sends a player found inside a block to a random spawn. A client whose hall pages are not loaded yet may also briefly stand in the floor (UNVERIFIED, play-test 3).
  - If the player is still outside the box after the second check, the arrival switches to **road mode** (section 5.1).

From T+0 the stage is `gatehouse`. The game's own `SpawnProtection` (30 s) is running: it hides newcomers from each other until they walk. The hall box is a sanctuary for players in arrival (section 8).

### 4. Waking (narration start)

The server cannot see when the client's loader ends. Narration therefore starts at whichever comes first: the player moves more than 1 m from where they were placed, or 12 s pass after T+0. It never starts before T+4 s.

- **W+0, gold:** Wake line.
- **W+5, gold:** Naming line. These are the only two lore lines sent before the player does anything.
- **W+10, amber:** Call line ("walk to the gold line before the gate").
- **W+11:** the one RealmArrival popup, the Gatehouse card (section 5.3), with the button "Open the Gate". It is skipped if the player has already crossed the gold line.

The newcomer looks round a pale stone court. They see:

- six parchment-white stones and the sky above;
- fire bowls in the corners (staff-built, so the court is readable at night);
- the painting `the-crossing` and the live Chronicle board on the back wall;
- at the front, a dark iron portcullis (or an open arch) with a gold line inlaid in the floor before it.

### 5. The gate opens (about W+15 to W+60)

The gate opens on the first of three triggers:

- the player steps on the gold line;
- the popup button reply arrives (checked by token, kind and deadline);
- 60 s pass after W+0 (`AutoOpenSeconds`).

**`GateMode: portcullis`** (the intended experience, switched on after play-test 6):

- RealmArrival removes the 5 x 6 portcullis one row at a time, top row first, every 0.4 s, using `RootCubeGrid.PlaceCubeAtLocal` with material 0 and `collectPreviousCube` false (so no salvage is dropped). The gate sinks into the ground in 2.4 s.
- At the same moment, the 24-cell ember band round the Hearth fire, about 100 m away, is recoloured from Ember deep to Ember hot (`ColorCubeAtLocal`). It fades back 20 s later.

**`GateMode: open`** (the shipped default until play-test 6 passes): the arch is always open, and crossing the gold line is the moment. The flare still runs if the beacon is built.

Either way:

- The realm-wide Herald line is sent, capped at 6 an hour (any above the cap are dropped silently). It replaces RealmHerald's connect-time "arrives for the first time" broadcast.
- The optional `GateSound` (`AudioController.Play(id, entity)` with no player list, so bystanders hear it) is empty by default.
- `RealmQuests.ReportQuestEvent(id, "custom", "arrival_gate", 1)` is called.
- The stage becomes `released`.

### 6. The reveal (gate + 3 s, gate + 8 s)

The site is laid out on the line from gate to fire to throne, so the open gate frames all three.

- **Gold:** the Reveal line, using `CrownAndConsequences.GetKingName` and `GetKingHouse`. There is one version for an empty throne and one with the monarch. At night (if the game clock is readable on the server, which is inferred), `RevealNight` is used instead, because the throne cannot be seen in the dark.
- **Gold:** the Walk line. The stage becomes `banners`.

### 7. The Banner Walk (about 60 to 150 s, at the player's own pace)

The six monuments stand in three facing pairs.

- **Banner lines.** Coming within 9 m of a monument's pledge stone queues that house's two lines, once per player.
  - Line 1 is the house's words and its play-style hook (from `docs/community/lore.md`), with the house name in its tint.
  - Line 2 is live data from RealmHouses: sworn members, how many are online, whether the leader is online, and their liege.
  - A great name that no one has claimed gets an honest line about the claim sign-up instead.
  - The two houses of a pair face each other across the path, so both trigger together. Their order within the pair is shuffled for each player.
- **Pair order.** Which pair stands nearest the gate is drawn by lot in public each time the site is built (that is, after every wipe) and written in the run-sheet. No house is always first.
- **Fast walkers.** A banner line still queued when the player is more than 30 m past that monument is dropped. Dropped houses are summed up in one `Skipped` line at the fire.
- **Explanations.** After the first banner, one muted line explains pledge stones. After the third banner, or on reaching the fire, two lines give the honest paths: send a letter (`/raven <house> <letter>`), or raise your own banner (`/house found`), or walk free.
- **Pledge stone (optional).** Standing still for 3 s on a stone opens the pledge confirm window: "Look to House X?" (section 5.3).
  - On confirm, that house's online members get one line naming the newcomer and `/house invite`. If none are online, the newcomer is told to write with `/raven`.
  - Limits: at most 2 pledges per arrival (one first choice and one change), 6 pledges per house per hour, variant A only.
  - Nothing ever joins a house automatically.
- **Quest reports.** `ReportQuestEvent` is called with `arrival_banners` after 3 banners, and with `arrival_pledge` on a pledge.

### 8. The fire

**Entering 40 m of the fire (Z3q, the third pair of the avenue).** This is the radius of the RealmQuests place `the_hearth`. Within one 5 s visit check, RealmQuests sends its own lines:

- "You reach The Hearth.";
- the `ex_hearth` and `ex_wayfarer` tier-1 achievements (10 marks each, held until 30 active minutes);
- the first objective of the Story step `s1_hearth_ash`, if a season's story is running. That step completes only after 15 more active minutes.

RealmArrival holds its own queue for 6 s (`QuietAfterHearthSeconds`) when the player enters this ring, so the two do not interleave.

**Entering 12 m of the fire (Z3).** RealmTravel's `TickDiscovery` (every 2 s) discovers the capital waystone `the-hearth`. It sends "You have found the waystone…" and the "Waystone found" popup. RealmArrival waits another 6 s, then sends one line at least every 4 s:

- **Gold:** Warm line. If `HealAtHearth` is on, it also calls `PlayerExtensions.Heal`, `Nourish` and `Hydrate`, once per arrival.
- **Gold:** Kit line, `/kit starter`. This is RealmTravel's Traveller's Pack, with its existing guards. `ReportQuestEvent arrival_hearth` is called here.
- **Gold:** Crown line, `/crown`. Typing it marks RealmHerald's path step 2 through Herald's existing `OnPlayerCommand` check, and RealmArrival reports `arrival_crown`.
- **Amber:** the Shelter line, sent **only if** `RealmWarden.IsNewPlayerProtected(id)` is true. Otherwise the Unsheltered line.
- **Amber:** the one next step. This is `Next` (`/quest`) when `RealmQuests.GetStoryProgress(id)` shows a running tale, otherwise `NextNoTale` (`/quest`, the quest board). The stage becomes `hearth`.

### 9. Written (handover)

The arrival is Written at whichever of these comes first, counted only once the stage is `hearth`:

- the player types `/quest`;
- the player moves more than 70 m from the fire;
- 60 s pass after the `/quest` line.

There is also an overall cap of 8 minutes from T+0 (section 5.1 explains what the cap does in each stage).

On Written:

- **Green:** Written line, `/realm path`.
- The stage becomes `done` and `OwnsArrival` turns false. RealmHerald's first-steps reminders start from now, not from connect, and Herald sends its MOTD once.
- If configured, `RealmRenown.AddDeed(id, name, "written", "Walked out of the Gatehouse", "arrival:" + id)` is called.

Typical total from Finish:

| Part | Time |
|---|---|
| Loader | 5 to 10 s |
| Gatehouse | 25 to 60 s |
| Reveal and banners | 60 to 150 s |
| Fire | 30 to 45 s |
| **Total** | **3 to 5 minutes** |

### 10. Hour one and day one (quiet follow-ups, each sent once)

| When | Line | Source |
|---|---|---|
| The Wayboard is first reached, or Written + 10 min | Three Roads (2 lines, `/road crown-market`), plus the next event as a relative time if it is within 48 h | `RealmEvents.GetNextEvent` |
| 10 min before Warden protection ends | ProtectionSoon (private, amber). The end of protection is never announced. | new `RealmWarden.GetProtectionMinutesLeft` (section 7.3); skipped until it exists |
| First block placed in the first 2 h | FirstBlock (raise a crest) | `OnCubePlacement` |
| First dusk while protected | Dusk (carry a torch) | the game clock's time of day, read on the server (inferred) |
| Next join after the first logout | Sleeper tip | own data |
| Second join | `/arrival` "your page so far", mentioned once | own data |
| Second waystone discovered | `ReportQuestEvent arrival_road` | polls `RealmTravel.GetDiscoveredCount` every 60 s for 2 h, online players only |

### Variant B: veteran on a fresh world

On wipe day most veterans join within minutes. Sending them all through the Gatehouse would empty the stones and crowd every fresh start at the Hearth. So the config `VeteranMode` offers:

- **`vanilla` (default):** the game's random spawn stands. After the loader, one gold line (`Veteran`) is sent. There is no realm-wide line. The record stays `done`.
- **`short`:** the same cut into the Gatehouse. At W+0 one `Veteran` line is sent and the gate opens at once. There are no banner, fire or road lines unless they type `/arrival tour`. They are Written when they leave the hall box.
- **`full`:** the whole arrival, without the realm-wide line, the pledges or the quest reports.

### Variant C: returning player

The game's logout or sleeper position stands. Nothing is sent, except a resume if the arrival was not finished.

### Skipping

- `/arrival skip` works at any stage. It drops all queued lines and marks the player Written with one line, "As you wish. The gate is open; the Hearth is yours." A player still in the hall box is moved to the forecourt eject point just outside the gate. This is not travel: it is 5 m.
- Walking straight to the gold line opens the gate in seconds, with no popup.
- `/arrival tour` replays the banner, fire and roads beats the next time the player passes those triggers. It never moves anyone.
- Staff with `realmarrival.skip` get vanilla spawning, or the Hearth if `StaffToHearth` is set.

### Logging off midway

The stage is saved. A sleeper inside the hall box of a player in arrival is inside the sanctuary. On the next join (`AtFirstSpawn` false):

| Saved stage | On rejoin |
|---|---|
| `pending` or `crossing` (quit before or during creation) | The game gives `AtFirstSpawn` again. The Crossing replays, and the arrival starts when `OnPlayerSpawned` finally fires. These stages never go stale. |
| `gatehouse`, and the player wakes in the hall | 10 s after spawn: Resume line. The gate opens on the gold line, or 20 s later. |
| `released` to `hearth`, within 60 m of the route | One line pointing to the next beat. The rest fire on their triggers. |
| `released` to `hearth`, farther away | Released with one summary line (`/road the-hearth`). |
| Any in-arrival stage, not back within 48 h | Marked `done` silently. If they then wake in the hall, they are released to the forecourt eject point with `ReleasedGate`. This is not an eviction and is never counted toward an alert. |

---

## 4. The site

One block is 1.2 m. All pieces are `realm-sculpture/1` files, which means:

- at most 64 cells a side and at most 4,000 blocks;
- every block face-connected to a flat bottom layer;
- single-block shapes only.

They are placed with `/sculpt place` from a marked standing spot, inside staff crest zones, with protection on. RealmLaws `no_building_towns` does not block them, because its `OnCubePlacement` check skips the server's own events (`plugins/RealmLaws.cs`, the `builder.IsServer` check). Material names are RealmSculptor roles (`cobblestone`, `stone`, `clay`, `sod`, `thatch`, `spruce`, `wood`, `log`, `reinforced`). The real ids, and which materials take paint, are UNVERIFIED until `/sculpt materials` runs. Colours come from `art/palette.json`.

### 4.1 Layout

L is the distance along the axis in metres, from the Gatehouse's back wall toward the throne.

```
                         [ Old Throne on the hill ]            (the view, not the hub)
                                     ^
                                     |  open ground
  L 146   +===== wayboard (5 signs) =====+
  L 141            heralds-pillar
  L 129   M1      ( fire pit )  W      M2     hearth-ring 18 m, ember band (plugin cells)
          M3                                  W = waystone the-hearth, radius 12 m
  L 120   +--------- hearth-ring edge -------+       Hearth town zone and quest place: fire +/- 40 m (from L 89)
                    |                |
  L 91-115  [mon]=P |   processional | P=[mon]   pair 3  (pair order drawn by lot at each build)
  L 62-86   [mon]=P |      7 wide    | P=[mon]   pair 2   braziers on both kerbs every ~12 m
  L 34-58   [mon]=P |                | P=[mon]   pair 1
                    +--- forecourt --+  E = eject point, 4 m outside the gate
  L 27.6  +----TT====[ portcullis 5x6 ]====TT----+
  L 24    |     ======= gold line =======        |   T = tower
          |                                      |
          |  A1   A2   A3     open court         |   A = arrival stones (6)
          |  A4   A5   A6     eaves 2 cells      |   Pilgrim's Stair in the left tower,
          |                                      |   drop pad D outside the left wall
  L 0     +---- the-crossing | chronicle | notice+
                     gatehouse-unwritten 19 x 23 x 13
```

- Distances: stone to gold line about 15 m; gate to the first pair 6 m; avenue 92 m from the gate to the ring edge; ring edge to fire 9 m.
- The stones are about 115 to 120 m from the fire. This is why no distance-from-the-Hearth rule applies before the stage is `hearth` (section 5.1).
- Total walk from stone to fire is about 120 m: roughly 30 s at a jog, or 90 to 150 s with stops.
- The avenue is 13 cells (15.6 m) wide between the monument fronts: a 7-cell path, with pledge stones 2 cells out from each monument.
- The site is about 33 m wide and 150 m long. One staff crest will probably not cover it (crest radius UNVERIFIED), so plan for 2 to 4 crests.

### 4.2 Sculptures

**New pieces.** Generators go in `art/sculptures/src` and use `src/lib/kit.mjs`.

**`gatehouse-unwritten`:** 19 x 23 cells, 13 high at the towers with walls 9 high, about 3,200 blocks.

| Part | Material | Colour |
|---|---|---|
| Floor | cobblestone | Iron 700 `#26282d` |
| Walls, 2 thick | stone | Parchment 2 `#e0cfa4` |
| Buttresses and quoins | stone | Parchment edge `#b99a62` |
| String course | clay | Ember `#d6a043` |
| Gate arch trim | reinforced | Iron 600 `#34363c` |
| Tower caps | stone | Iron 800 `#1b1c20` |
| Eaves, 2 cells inward on top of the walls | wood | Ink soft `#5b4630` |
| Six arrival stones: 1 x 1 inlays, all the same so no house is favoured | clay | Parchment `#ecdfbf`, with 3 x 3 Iron 900 `#131417` borders |
| Gold line: 5 x 1 inlay, 2 cells inside the gate | clay | Ember hot `#f4c96d` |

Other features of the gatehouse:

- The gate gap is 5 wide x 6 high and left empty.
- The open court centre is 11 x 15, with no roof span.
- **Pilgrim's Stair:** 1-cell stone steps inside the left tower up to a 2-cell-wide outer ledge, 2 cells above the drop pad. Play-test 6b checks that players cannot climb back in from outside.

**Other new pieces:**

| Id | Footprint W x D (cells) | Height | Blocks | Materials and colours |
|---|---|---|---|---|
| `processional-a`, `processional-b` | 7 x 39 and 7 x 38 | 1 | about 270 each | Path in cobblestone, unpainted. Kerbs in stone, Iron 600 `#34363c`. Optional: skip if the ground is not flat to within 1 block over 45 m. |
| `pledge-stone-<house>` x 6 | 3 x 3 | 2 | about 10 each | Plinth in stone, the house `fieldDark`. Raised centre cell in clay, the house `metal`. For example: Varrow plinth `#2e162c`, centre `#9aa0a8`; Merrin plinth `#162c1c`, centre `#c9ced4`. One generator using `houseStyles`. |
| `hearth-ring` | 15 x 15 | 2 | about 400 | Two stepped seating rings in stone, Iron 600 `#34363c` and Iron 700 `#26282d`. A ring of 24 cells at radius 5 is left **empty** for the plugin's ember band. The centre is left open for the staff fire pit. |
| `wayboard` | 15 x 2 | 5 | about 140 | Base in stone, Iron 600. Posts in spruce, Ink `#2a1c0f`. Top rail in clay, Ember `#d6a043`. Five sign faces. |

**Existing pieces.** `house-varrow`, `house-ashgrove`, `house-corvane`, `house-dunmere`, `house-halloran` and `house-merrin` (20 x 7 cells footprint, 24 to 28 cells high, 1,044 to 1,118 blocks each, 29 to 34 m tall) stand in pairs with their fronts to the road. `heralds-pillar` (5 x 13 x 5, 132 blocks) stands on the throne side of the fire.

**Plugin-owned cells.** These are never Sculptor cells, so `/sculpt repair` and `/sculpt remove` never clash with them:

- The **portcullis**: 30 reinforced cells, Iron 900 `#131417`, placed by `/arrival admin gate build`.
- The **ember band**: 24 clay cells, resting colour Ember deep `#9c6a1e` and flare colour Ember hot `#f4c96d`, placed by `/arrival admin beacon build 5`.

RealmArrival records the previous state of these cells and protects them (`OnCubeTakeDamage`, `OnCubePlacement`).

**Totals.** About 4,600 new blocks, plus about 6,600 for the monuments and 132 for the pillar: about 11,300 in all. At 125 calls a second (place plus paint), that is about 3 minutes of Sculptor time across 17 jobs, run one at a time in a quiet hour.

**Staff-built (ordinary building):**

- the Hearth fire pit (L02 already asks for one);
- fire bowls in the court;
- braziers or torches on both kerbs about every 12 m, so the avenue reads at night.

### 4.3 Signs

Staff place the signs and bind them with `/paint`. Every sign looks the same to everyone. The server-wide painter budget is 6 redraws a minute, so binding all 15 signs takes at least 3 minutes. The four live boards here then use at most 2 of the realm's 6 redraws a minute (each sign at most once every 120 s).

| # | Where | Binding | Text or art |
|---|---|---|---|
| G1 | Gatehouse back wall, behind the stones | art `the-crossing` (**new**, 320 x 256) | A raft on grey water under a pale gate, with smoke rising beyond |
| G2 | Back wall (the Chronicle Wall) | board `chronicle` | live |
| G3 | Back wall | `notice` | `The Unwritten \| You have no page in the Chronicle yet. Walk through the gate, and the Chronicle opens.` |
| G4 | Inner face beside the gate | art `poster-welcome` | existing |
| P1-P6 | A post beside each pledge stone | art `crest-<house>` | existing |
| W1 | Wayboard | `notice` | `Three Roads \| Crown Market: coin and contracts. Listing Field: the Ring. The seats: the houses. /road shows the way.` |
| W2 | Wayboard | board `proclamation` | live |
| W3 | Wayboard | board `event` | live |
| W4 | Wayboard | board `standings` | live |
| W5 | Wayboard | `notice` | `Other Banners \| Houses beyond the six recruit too. /house list names them; /raven <house> <letter> asks.` |
| H1 | Facing the avenue end | `notice` | `The Hearth \| Raise a crest before you build. Log off behind walls. Nights are dark: carry a torch.` |

That is 15 signs, with one new artwork. Each notice is under 180 characters.

### 4.4 Waystones

These use lore names, not `Kingsreach` or `Greywater`.

- `the-hearth`: kind capital, at the fire, radius 12 (`/travel admin set`, `/travel admin radius`), with `heralds-pillar` as its monument (`/travel admin mark`). The arrival stones are **not** inside its radius: discovery is the beat at the fire.
- `crown-market` and `listing-field`: the Three Roads targets. If they do not exist yet, the Roads lines drop their `/road` command (section 10).
- The six seats, when houses raise them.
- The Gatehouse is never a waystone and can never be a `/home` (`/home` needs the player's own crest).

### 4.5 After every wipe (run-sheet)

Sculptures, signs, crests, fire pits and the plugin's gate and band cells are world objects, so a wipe removes them. Stored points, waystones and RealmArrival records are Oxide data, so they survive. After a wipe:

1. Run `/arrival admin close`.
2. Put the staff crests back.
3. Draw the pair order by lot in public.
4. Place the pieces in route order, then build the fire pit and lights.
5. Rebind the 15 signs.
6. Run `/arrival admin gate build` and `beacon build`.
7. Re-store any point that moved.
8. Run `/arrival admin check`, then `open`.

The plugin also checks itself, at load and every `SiteSelfCheckMinutes` (60). If any stone's floor cell is missing (`GetCubeInfoAtLocal`), it closes the arrival by itself and logs why. A freshly wiped world therefore never routes newcomers onto empty ground.

---

## 5. Guidance system

### 5.1 Zones, triggers and route rules

Every point is stored by staff standing on it (`/arrival admin ...`). One 1 s timer runs only while someone has an active stage, and compares `player.Entity.Position` with these zones:

| Zone | Shape | Effect |
|---|---|---|
| Z0 hall box | two corner cells and a height range | Sanctuary for players in arrival and their sleepers. Eviction of others to the eject point E. |
| Z0b drop pad | two corners, outside the left wall | Sanctuary for players in arrival only, so the Pilgrim's Drop never hurts |
| E eject point | point, 4 m outside the gate | Where eviction, skip-from-hall and stale wake-ups put players |
| Z1 gold line | 2.5 m | Opens the gate |
| Z2 banners x 6 | 9 m around each pledge stone | Two lines per house, once (bitmask) |
| Z2p pledge stones x 6 | 1.5 m, dwell 3 s | Pledge confirm |
| Z3q Hearth quiet ring | 40 m around the fire (the RealmQuests place radius) | Holds RealmArrival's queue for 6 s on entry |
| Z3 Hearth stone | 12 m | Fire beats |
| Z4 Wayboard | 8 m | Three Roads (once) |
| Z5 Hearth outer | 70 m | Leaving it means Written, **only once the stage is `hearth`** |
| Route corridor | hall box, plus the avenue axis +/- 20 m, plus Z5 | Used by the wander rule |
| Stones x 6, mercy stones x 3 | points | Arrival and respawn targets |

**Narration queue rules:**

- At least 4 s between lines per player, and at most one `/command` per line.
- A queued line is dropped once its beat is passed. A banner line is passed when the player is 30 m beyond that monument. A gate line is passed once the gate is open. So there is never a backlog.
- Stations are suggested, not gated. Skipped banners are summed up in one muted line at the fire.
- There is always exactly one current next step. After 30 s standing still with no progress, a nudge repeats it, at most twice per stage.
- Distances are given in metres. Compass words are used only after RealmTravel T12 confirms that +z is north (`UseCompassWords`).

**Wander, timeout and AFK:**

- **Wander:** more than 40 m outside the route corridor before `done` sends the Wander line and hands over at once. The plugin never pulls anyone back.
- **Timeout:** at 8 minutes from T+0, a player outside the hall is handed over with the Written line. A player still in the hall gets the gate opened and keeps the `gatehouse` stage and its sanctuary. They are Written as they leave.
- **AFK in the hall:** after `AfkReleaseMinutes` (30) with no movement in the hall, the player is moved to E as Written. A logged-off sleeper stays where it is: the server has no verified way to move a sleeper.

**Road mode.** Road mode runs when the arrival checks fail or `mode road` is set. The player stays where the game put them and is released at once with the `RoadMode` line, which points to `/road the-hearth` (RealmTravel's own compass guide). The gate, reveal and banner beats are skipped. If they reach Z3 within 2 h, the fire beats play there and the Herald line is sent.

### 5.2 Chat lines

Line rules:

- Every line has the speaker `Hearth` (lang key `Speaker`) in its tone colour, except continuation lines, which start with two spaces.
- House names are arguments, pre-tinted with their `discordRole` colour from `art/palette.json` (Varrow `C58FC0`, Ashgrove `E08A5C`, Corvane `8FB0BF`, Dunmere `B8B85A`, Halloran `EC8A3C`, Merrin `6FBF85`).
- Commands, with their subcommand words, are in `F4C96D`.
- Player text only ever goes in as an argument, through single-string `SendMessage`.
- Every line is at most 200 visible characters. The longest below is about 160 plus a name.

**The Gatehouse and the gate**

| Key | Tone | Text (lang value; `{n}` are arguments) |
|---|---|---|
| `Wake` | gold | `Stone underfoot, old smoke on the air. The ferryman's raft is gone. You stand in the Gatehouse of the Unwritten, below the Hearth.` |
| `Naming` | gold | `{0}, the Chronicle has no page for you: no house, no oath, no debt. Under the Charter that is enough. The crown belongs to the seat, not the blood.` |
| `CallGate` | amber | `Lay your hand on the gate: walk to the gold line before it.` |
| `CallOpen` | amber | `The gate stands open. Walk through it, into Ostreval.` |
| `GateSelf` | amber | `The gate opens on its own. The Hearth is waiting.` |
| `HeraldGate` (realm-wide) | Herald | `[D6A043]Herald[FFFFFF]: {0} walks out of the Gatehouse of the Unwritten, onto the road to the Hearth.` |
| `ReleasedGate` | gold | `You wake by the Gatehouse. The road to the Hearth runs ahead.` |

**Reveal and banners**

| Key | Tone | Text (lang value; `{n}` are arguments) |
|---|---|---|
| `RevealEmpty` | gold | `Six banners line the road to the fire. On the hill beyond, the Old Throne stands empty, and anyone may sit it.` |
| `RevealKing` | gold | `Six banners line the road to the fire. On the hill beyond stands the Old Throne, held by {0} of {1}.` |
| `RevealNight` | gold | `Fires mark the road through the dark. Six banners line it to the Hearth; beyond, unseen tonight, stands the Old Throne.` |
| `Walk` | gold | `Walk the banners. Each house will tell you what it is. Choose none yet; listen first.` |
| `House.varrow` | gold | `{0}, the Iron Stag. "We Stand Our Ground." The crown-holders: they take the throne and hold it against the realm.` |
| `House.ashgrove` | gold | `{0}, the White Oak. "Deep Roots, Long Memory." Builders and oath-keepers: a steady liege or a loyal vassal.` |
| `House.corvane` | gold | `{0}, the Black Raven. "Every Secret Has a Price." Intrigue: many treaties, and the right one broken at the right time.` |
| `House.dunmere` | gold | `{0}, the Drowned Bell. "The Tide Returns." The rebels: they declare against whoever reigns and fight in the Lawful Hours.` |
| `House.halloran` | gold | `{0}, the Ember Hound. "Loyal Until the Last Coal." Hired swords and fair ransomers, paid by both sides.` |
| `House.merrin` | gold | `{0}, the Silver Eel. "Slip the Net." Traders and go-betweens: they run the roads and carry for every side.` |
| `HouseLive` | continuation | `  [A3A6AD]{0} sworn, {1} awake in the realm, leader {2}. {3}[FFFFFF]` (`{2}` is "at hand" or "away"; `{3}` is "Sworn to no one." or "Sworn to {house}.") |
| `HouseUnclaimed` | continuation | `  [A3A6AD]No one has raised this banner yet. Great names go to companies by the claim sign-up.[FFFFFF]` |
| `PledgeHint` | continuation | `  [A3A6AD]Stand on a house's stone to look to its banner. It is not an oath; only they can invite you.[FFFFFF]` |
| `SeekRaven` | amber | `A banner is never taken, only given. Send word to a house: [F4C96D]/raven[FFFFFF] <house> <letter>.` |
| `OwnBanner` | continuation | `  [A3A6AD]Or raise your own:[FFFFFF] [F4C96D]/house found[FFFFFF][A3A6AD]. Or walk free and sell your sword.[FFFFFF]` |
| `Skipped` | continuation | `  [A3A6AD]You passed {0} without stopping.[FFFFFF] [F4C96D]/arrival tour[FFFFFF] [A3A6AD]tells you them again.[FFFFFF]` |

**Pledges**

| Key | Tone | Text (lang value; `{n}` are arguments) |
|---|---|---|
| `PledgeDwell` | amber | `Stand fast to look to House {0}. Step off to walk on.` |
| `PledgeSent` | green | `Word goes to House {0}. If they want you, an officer will invite you; then say [F4C96D]/house join[FFFFFF] {0}.` |
| `PledgeNoneAwake` | amber | `No one of House {0} is awake in the realm. Leave a letter: [F4C96D]/raven[FFFFFF] {0} <letter>.` |
| `PledgeToHouse` (to that house's online members) | gold | `{0}, one of the Unwritten, looks to your banner on the Gatehouse road. [F4C96D]/house invite[FFFFFF] {0} brings them in.` |
| `PledgeLimit` | amber | `You have looked to a banner already. You may change your mind once.` |

**The fire and handover**

| Key | Tone | Text (lang value; `{n}` are arguments) |
|---|---|---|
| `Warm` | gold | `You warm your hands at the fire that has not gone out in a hundred winters.` |
| `Kit` | gold | `The Hearthkeepers leave a bundle for the Unwritten: wood, stone and food. Take yours: [F4C96D]/kit starter[FFFFFF].` |
| `Crown` | gold | `Whoever sits the Old Throne is the Crown. A sitter can be challenged only by a declared claim, in the Lawful Hours: [F4C96D]/crown[FFFFFF].` |
| `Shelter` | amber | `For your first hour no other player can wound or bind you. Strike, bind or raid a crest, and the shelter ends.` |
| `Unsheltered` | amber | `Walk carefully. Out here the blades are real.` |
| `Next` | amber | `Your tale has begun at the Hearth. Your first deed waits in [F4C96D]/quest[FFFFFF].` |
| `NextNoTale` | amber | `Tasks wait on the quest-board. Your first deed waits in [F4C96D]/quest[FFFFFF].` |
| `Written` | green | `You are written. What the Chronicle says of you next is yours. [F4C96D]/realm path[FFFFFF] keeps your first steps.` |

**Nudges, roads and resumes**

| Key | Tone | Text (lang value; `{n}` are arguments) |
|---|---|---|
| `Nudge` | gold | `The fire lies {0} m ahead, at the end of the banners.` |
| `NudgeCompass` | gold | `The fire lies {0} m to the {1}.` |
| `Wander` | amber | `The Hearth will keep. [F4C96D]/road the-hearth[FFFFFF] leads back.` |
| `RoadMode` | amber | `The current carried your raft off course, {0} m from the Hearth. [F4C96D]/road the-hearth[FFFFFF] shows the way to the fire.` |
| `ResumeHall` | gold | `You wake again in the Gatehouse. The gate still waits for you.` |
| `ResumeRoad` | gold | `You wake again on the road. The fire is {0} m on.` |
| `Veteran` | gold | `Welcome back to Ostreval, {0}. The realm was made new; the Hall of Kings remembers.` |
| `SkipDone` | green | `As you wish. The gate is open; the Hearth is yours.` |
| `Mercy` | amber | `The Hearth takes you back, once. Raise a bed before you fall again.` |
| `MidDeath` | amber | `The Hearth's smoke led you back. The fire is before you.` |
| `Evicted` | amber | `The Gatehouse is for the Unwritten.` |
| `RoadsA` | gold | `Three roads leave the Hearth: the Crown Market for coin and contracts, the Listing Field for the Ring, the seats for the houses.` |
| `RoadsB` | amber | `Walk one: [F4C96D]/road crown-market[FFFFFF]. Your first 3 waystone journeys are free.` |
| `NextEvent` | continuation | `  [A3A6AD]Next in the realm: {0}, in {1}.[FFFFFF]` (always a relative time, never a bare UTC time) |

**Hour one and the page**

| Key | Tone | Text (lang value; `{n}` are arguments) |
|---|---|---|
| `ProtectionSoon` | amber | `Ten minutes of shelter left. Walls, a crest and a bed before dark.` |
| `FirstBlock` | amber | `Blocks outside a crest's land decay. Raise a crest before you build much.` |
| `Dusk` | amber | `Night is coming, and Ostreval's nights are dark. Carry a torch, or stay by a fire.` |
| `Sleeper` | amber | `Your body slept where you stood. Log off behind walls, or it may not be there when you return.` |
| `Page` | gold | `Your page so far:` then the continuation `  House: {0}. Waystones known: {1}. Tale: {2}.` |
| `Cmd.arrival` (Herald catalogue) | - | `[F4C96D]/arrival[FFFFFF] - where you are in your arrival and what is next; skip or tour.` |

`#region Chat style` holds `ChatGold`, `ChatOk`, `ChatWarn`, `ChatError` and the Herald voice exactly as `tools/realm-integration/check.mjs` requires.

### 5.3 Popups (at most two per arrival)

Both windows go in `#region Popups`. Each sits inside `try`, passes every argument (ShowPopup 6, ShowConfirmPopup 7, as the check lists them) with `broadcast=true`, and is behind the `UsePopups` switch and a `PopupsFor` gate that calls `RealmHerald.PopupsWanted`. The chat fallback is always sent. An answer counts only once, and only if it matches the token, kind and deadline of the one question open for that player.

1. **The Gatehouse card**, `PlayerExtensions.ShowPopup`. `interupt` is false (`InterruptPopups` false) until play-test 7 shows how a modal interrupt behaves.
   - Title: `The Gatehouse of the Unwritten`
   - Body (plain text; colour tags are stripped):
     - `The ferryman has brought you over the Grey Water.`
     - `Beyond this gate burns the Hearth, where the houses swore the Charter. Six banners line the road to it.`
     - `The crown belongs to the seat, not the blood.`
   - Button: `Open the Gate` (in open mode: `Step through`). The reply handler opens the gate.
   - Chat fallback: `CallGate` or `CallOpen`.
2. **The pledge** (optional), `PlayerExtensions.ShowConfirmPopup`.
   - Title: `Look to House {house}?`
   - Body: `Their sworn who are in the realm now will hear that you seek their banner.` / `This is not an oath. Only they can invite you.`
   - Buttons: `Look to {house}` / `Walk on`.
   - Fallback with popups off: `PledgeDwell`. Standing 3 more seconds on the stone confirms it, and stepping off cancels it.

The only other window in the arrival is RealmTravel's own "Waystone found" at the fire. `NewsFeed` toasts (`UseNewsToasts`) are off until play-test 16.

### 5.4 Quest steps

RealmQuests has no per-player, untimed questline type today, so the arrival's quest credit ships in two stages.

**v1, data only (works with today's code).** Add this to `plugins/docs/RealmQuests/content/Achievements.json` (the `Achievements` array; same shape as `wa_many_foes`). It is fed by `ReportQuestEvent(id, "custom", "<subject>", 1)`, which is limited to 200 a day per type per player. The `Distinct` count (`RealmQuests` `CheckObjective`) counts each subject once.

```json
{
  "Id": "ex_written",
  "Name": "Written",
  "Category": "exploration",
  "Text": "The first steps of the Unwritten: the gate, the banners, the fire, the crown and the first road.",
  "Objective": {
    "Type": "custom",
    "Count": 1,
    "Text": "Arrival steps",
    "Distinct": true,
    "Targets": ["arrival_gate", "arrival_banners", "arrival_pledge", "arrival_hearth", "arrival_crown", "arrival_road"]
  },
  "Tiers": [
    { "Count": 5, "Reward": { "Renown": "written" } }
  ]
}
```

- The reward is renown only (`RewardDef.Renown`), never marks, so the shared 3,000-a-day reward budget is not touched by this achievement. The existing `ex_hearth` and `ex_wayfarer` already pay marks for reaching the Hearth. That is unchanged and is not new spending.
- `"written"` must be added to RealmRenown's `DefaultDeeds()`. Built-in kinds are merged into every server's config at load (`plugins/RealmRenown.cs`, config validation), so no manual config edit is needed.
- No `Places.json` entry is added: `the_hearth` already exists. The Gatehouse is deliberately not a place, so it does not hand out a free Wayfarer tier.

**v2, ordered arrival chain (needs a RealmQuests change).** This is a new content file, `Arrival.json`, read by `LoadContent`, with no season, `UnlockDay` or time gate. It is per player and shown in `/quest` while it is open. It reuses the Story step format and existing objective types:

```json
{
  "Version": 1,
  "Title": "The Unwritten",
  "Steps": [
    { "Id": "ar_gate", "Title": "Through the Gate",
      "Text": "The ferryman has left you at the Gatehouse. Walk out of it, and the Chronicle opens.",
      "Done": "The gate is behind you. From here on, what you do is written.",
      "Objectives": [ { "Type": "custom", "Targets": ["arrival_gate"], "Count": 1, "Text": "Walk out of the Gatehouse" } ] },
    { "Id": "ar_hearth", "Title": "Warm Hands",
      "Text": "Walk the banners to the fire where the Charter was sworn.",
      "Done": "The fire has not gone out in a hundred winters.",
      "Objectives": [ { "Type": "visit", "Targets": ["the_hearth"], "Count": 1, "Text": "Reach the Hearth" } ] },
    { "Id": "ar_crown", "Title": "Who Sits the Throne",
      "Text": "Learn who holds the Old Throne, and how a crown is lost.",
      "Done": "Now you know whose seat it is, for now.",
      "Objectives": [ { "Type": "custom", "Targets": ["arrival_crown"], "Count": 1, "Text": "Ask after the crown" } ] },
    { "Id": "ar_road", "Title": "The First Road",
      "Text": "Leave the Hearth by one of its three roads and find a second waystone.",
      "Done": "Two stones known. The realm is larger than one fire.",
      "Objectives": [ { "Type": "custom", "Targets": ["arrival_road"], "Count": 1, "Text": "Find a second waystone" } ] }
  ],
  "Reward": { "Renown": "written" }
}
```

House membership stays with Story step `s1_banner`, so it is not repeated here.

**Subjects reported by RealmArrival.** Variant A only; never by `/arrival admin play`.

| Subject | When it is reported |
|---|---|
| `arrival_gate` | gate opened or crossed |
| `arrival_banners` | 3 banners seen |
| `arrival_pledge` | a pledge confirmed |
| `arrival_hearth` | Z3 reached |
| `arrival_crown` | `/crown` seen in `OnPlayerCommand`, during the arrival or hour one |
| `arrival_road` | `RealmTravel.GetDiscoveredCount(id) >= 2` within 2 h |

### 5.5 Waystone and kit hand-off

- **Waystone.** RealmArrival unlocks nothing itself. RealmTravel's own `TickDiscovery` discovers `the-hearth` at the fire. The Three Roads lines point to `/road crown-market`; `/road` works for undiscovered waystones.
- **Kit.** RealmArrival gives no items. The Kit line points to RealmTravel's `/kit starter` (kit id `starter`) with its guards unchanged:
  - once per Steam id;
  - only after Warden protection has been seen, through `Traveller.WasNewcomer`;
  - within `StarterClaimDays` (7);
  - at most 40 a day across the realm;
  - through the owed ledger and `SentinelItemSource`.
- RealmTravel sets `WasNewcomer` from its once-a-minute tick and at claim time (`NoteNewcomer`), not from the hint, so silencing the 45 s hint does not change who can claim.
- RealmTravel's own 45 s hint is silenced while `OwnsArrival` is true and after `done`.
- Ask that the Traveller's Pack gain a torch and a stone tool once `/kit admin items` has confirmed the real item names. This is a config change.
- A later optional RealmTravel `GrantKit(string id, string kitId)` would turn the kit line into an automatic bundle at the fire.

---

## 6. Respawn rules and admin controls

### 6.1 Respawn

Respawns are handled in `OnPlayerRespawn(PlayerRespawnEvent e)`, by setting `e.Position` only. The game's respawn loader covers the move (`SpawnColumnHandler.OnPlayerRespawn`).

| Case | Rule |
|---|---|
| `PlayerRespawnAtBedEvent`, `PlayerRespawnAtBaseEvent` | Never touched: by hook time the bed has been consumed or warmed. |
| Death during the arrival (stage `gatehouse` to `hearth`), Randomly or Normal | The least-recently-used **mercy stone** at the Hearth, never back into the Gatehouse. The stage jumps to the fire beats, and `MidDeath` is sent a tick later. Missed banners are available through `/arrival tour`. |
| After Written, Randomly or Normal, while `IsNewPlayerProtected` is true and `MercyUsed < MercyRespawns` (default 1, max 3) | A mercy stone, then `Mercy` a tick later (Hearth's Mercy). |
| Everything else | Vanilla. |

Rules for every case:

- `SentinelGrace(id, 20f)` is called before `e.Position` is set.
- No items are given in the hook: `InvEquipment.OnPlayerRespawn` (VeryLate) rebuilds the inventory afterwards.
- Deaths are counted in RealmArrival's own data. `CharacterRespawn.RespawnCount` is per entity and in memory only.
- `Cancel()` is never used: it does not stop the game's teleport.
- The Normal path is the common one: `RespawnMsg` respawns Normal on any key for the first deaths.

### 6.2 Commands

**Player commands** (`/arrival`, in RealmHerald's Catalogue under the subject `roads`):

- `/arrival`: the current stage and next step. After `done` it shows "your page so far" (`RealmHouses.GetHouse`, `RealmTravel.GetDiscoveredCount`, `RealmQuests.GetStoryProgress`) and the next event.
- `/arrival skip`: ends the arrival (section 3).
- `/arrival tour`: replays the banner, fire and roads beats on their triggers.

**Staff commands** (`/arrival admin ...`, permission `realmarrival.admin`):

| Command | Does |
|---|---|
| `status` | Mode, gate state, live arrivals, counters (routed, rerouted, unconfirmed, road mode, released, skipped, popups sent and answered, pledges per house, evictions, self-check closures), mean seconds per stage, last 10 timings |
| `site` / `check` | Lists any missing point. `open` is refused until this passes. See the check list below. |
| `open` / `close` | Close means vanilla spawning for new players and `ArrivalStage` returns `none` for new ids, so Herald behaves as it does today. Active arrivals finish. |
| `pause` / `resume` | Instant global off. Restores the vanilla provider and opens the gate. Every active arrival becomes `none`, so RealmHerald's normal welcome reaches them. |
| `mode teleport\|provider\|road\|off` | Routing mode |
| `gatemode open\|portcullis` | Gate behaviour |
| `stone add\|remove <n>\|list\|clear` | Arrival stones (6 recommended), stored where the admin stands |
| `mercy add\|clear` | Mercy stones (3) |
| `hall corner1\|corner2`, `droppad corner1\|corner2`, `eject set` | Boxes and the eject point |
| `gate set <w> <h>` | Stand on the bottom-left cell inside the opening, facing out |
| `gate build\|open\|close\|test\|remove` | Portcullis cells (plugin-owned) |
| `threshold set` | The gold line |
| `beacon build <radius>\|clear\|test` | Ember band (plugin-owned), around the stored Hearth centre |
| `banner set <house>\|clear <house>` | Stand on a pledge stone |
| `hearth set`, `wayboard set` | Points |
| `play <player>` | Runs the full arrival on an online player who is not in combat (tests, launch-day ceremony). It never sends the realm-wide line, never reports quest subjects and never gives the renown deed. |
| `skip <player>`, `reset <player>`, `veteran <player>` | Records |
| `pass <player>` | Opens the gate for someone stuck, or sends them to E |
| `evict on\|off` | Hall eviction |
| `wave on <minutes>\|off` | Launch-wave mode: gate held open, popup off, banner lines only on approach, 2 per stone with jitter, overflow to mercy stones with the short lines |

`check` verifies that:

- each stone, mercy stone and E has a solid floor cell and two air cells above it;
- the hall box and stones are outside every circle in `oxide/data/RealmDominionMap.json`, outside the Hearth town zone, and more than 80 m from the throne;
- no point of the site is inside a Dominion circle or an arena zone;
- the Hearth centre is within 3 m of the RealmQuests place `the_hearth` and the RealmLaws zone `Hearth`;
- RealmSentinel and RealmTravel are loaded and the waystone `the-hearth` exists.

**Permissions:** `realmarrival.admin`, and `realmarrival.skip` (vanilla spawn for staff and testers). Both are added to `docs/community/ops/staff-roles-and-permissions.md`.

### 6.3 Config (`oxide/config/RealmArrival.json`)

```json
{
  "Enabled": true,
  "Open": false,
  "RoutingMode": "teleport",
  "TeleportDelaySeconds": 0,
  "ArrivalCheckSeconds": [3, 10],
  "GateMode": "open",
  "AutoOpenSeconds": 60,
  "GateMinOpenSeconds": 15,
  "GateCycleMinSeconds": 20,
  "GateClearanceMetres": 3,
  "FlareSeconds": 20,
  "FlareMinIntervalSeconds": 60,
  "NarrationStartMoveMetres": 1.0,
  "NarrationStartCapSeconds": 12,
  "LineGapSeconds": 4,
  "QuietAfterHearthSeconds": 6,
  "BannerDropMetres": 30,
  "HandoverRadius": 70,
  "RouteCorridorMetres": 20,
  "WanderOffRouteMetres": 40,
  "TimeoutMinutes": 8,
  "AfkReleaseMinutes": 30,
  "ResumeRadius": 60,
  "StaleHours": 48,
  "VeteranMode": "vanilla",
  "StaffToHearth": false,
  "MercyRespawns": 1,
  "PledgesPerHousePerHour": 6,
  "NewcomerBroadcastsPerHour": 6,
  "ShieldSecondsAfterRelease": 30,
  "ArrivalZoneGuardMinutes": 15,
  "EvictSeconds": 3,
  "SiteSelfCheckMinutes": 60,
  "UsePopups": true,
  "InterruptPopups": false,
  "UseNewsToasts": false,
  "GateSound": "",
  "HealAtHearth": true,
  "HearthBuff": false,
  "UseCompassWords": false,
  "HourOneTips": true,
  "SeedFromHerald": true
}
```

---

## 7. Integration

Status columns:

- **Code:** `verified` means read in the decompile or the plugin source; `inferred` means reasoned from code but not shown by it.
- **In game:** everything is UNVERIFIED unless noted.

### 7.1 Game API

| Use | Member | Code | In game |
|---|---|---|---|
| Mark first spawn and variant | Oxide `OnPlayerSpawn(PlayerFirstSpawnEvent)`. `PlayerFirstSpawnEvent.AtFirstSpawn` is set in `PlayerListener.OnPlayerFirstSpawnVeryEarly` before the hook. | verified | UNVERIFIED |
| Arrival trigger | Oxide `OnPlayerSpawned(PlayerPreSpawnCompleteEvent)`, called after `PlayerListener.OnPreSpawnComplete` teleports and sets `HasCompletedCreation`. Fires once per new character. | verified | UNVERIFIED (play-test 1) |
| Loader covers the move | `SpawnColumnHandler.OnPlayerPreSpawnComplete` -> `Game.RegisterLoader`; `EnableLoadingScreen` | verified | UNVERIFIED |
| Loader waits for the hall's pages, not the first point's | `SpawnColumnHandler.HasLoaded` | inferred | UNVERIFIED (play-test 3). Hence the T+10 check. |
| Teleport | `Entity.GetOrCreate<CharacterTeleport>().Teleport(Vector3)`; the server sets the position synchronously in `CharacterTeleport.OnTeleportEvent` | verified | UNVERIFIED (RealmTravel T4/T5) |
| No frame at the vanilla point | two moves in one tick, then the loader | inferred | UNVERIFIED (play-test 4) |
| Provider mode | See the note below this table. | verified | UNVERIFIED (play-test 14) |
| Movement checker reset | `TeleportationDetection` resets on `TeleportEvent`, `PlayerSpawnEvent`, `PlayerRespawnEvent` | verified | UNVERIFIED |
| Unstick risk | `MovementStatisticCollector` moves a player found inside solid terrain or blocks to `GetRandomSpawnPoint`. Hence stones at +0.5 m, the air check, gate clearance and the two arrival checks. | verified | UNVERIFIED |
| Respawn routing | Oxide `OnPlayerRespawn(PlayerRespawnEvent)`; the `PlayerSpawnEvent.Position` setter; the subclasses `PlayerRespawnRandomlyEvent` / `NormalEvent` / `AtBedEvent` / `AtBaseEvent`; `CharacterRespawn.RespawnInternal` | verified | UNVERIFIED |
| No items in the respawn hook | `InvEquipment.OnPlayerRespawn` (VeryLate) | verified | - |
| Gate and flare blocks | `BlockManager.DefaultCubeGrid`; `RootCubeGrid.PlaceCubeAtLocal` (7 parameters; material 0 is air; `collectPreviousCube` false); `ColorCubeAtLocal`; `GetCubeInfoAtLocal`. Reached by reflection as `plugins/RealmSculptor.cs` does, because the compile stub has only `Vector3`. Bound in `OnServerInitialized`, retried if the grid is not ready. | verified (bound and mock-tested in RealmSculptor) | UNVERIFIED (Sculptor PT2, play-test 6) |
| Town-zone building law does not block server placement | `RealmLaws.OnCubePlacement` returns early for `builder.IsServer` | verified | UNVERIFIED |
| Protect plugin cells | Oxide `OnCubeTakeDamage`, `OnCubePlacement` | verified (RealmSculptor) | UNVERIFIED |
| Sanctuary and shields | Oxide `OnEntityHealthChange` cancel (the RealmWarden and RealmTravel pattern) | verified | UNVERIFIED (Travel T7; sleepers unknown) |
| Game spawn protection | `SpawnProtection` (30 s, also on `PlayerPreSpawnCompleteEvent`), left at default | verified | UNVERIFIED |
| Popups | `PlayerExtensions.ShowPopup`; `ShowConfirmPopup(title, message, confirmText, cancelText, handler, interupt, broadcast)` (custom labels, server-side reply handler; each call sleeps the main thread 1 ms) | verified | UNVERIFIED |
| Heal at the fire | `PlayerExtensions.Heal`, `Nourish`, `Hydrate` | verified | UNVERIFIED |
| Toasts (off) | `NewsFeed.SendNews(string, List<Player>, Severity, bool, string)`, called directly | verified | UNVERIFIED |
| Gate sound (empty) | `AudioController.Play(string, Entity, params Player[])`; the ids are client assets | verified (the method) | UNVERIFIED (the ids) |
| HUD buff (off) | `EffectDefinition.HealthRegenerationMultiplier.Apply(Entity)` | verified | UNVERIFIED |
| Kit and crown beats | Oxide `OnPlayerCommand`, as RealmHerald uses it | verified | - |
| Dusk tip, night reveal | the game clock's time of day, read on the server | inferred | UNVERIFIED |
| Not used | `FlyCameraEvent`, `MenuOpenEvent`, `AtFirstSpawn=false`, setting `e.Position` in `OnPlayerSpawn`, setting `e.PostSpawnPosition`, virtual beds (`SavedSpawnPointsManager.RegisterPointForID`), per-player time or weather, Mods spawners, `SetGodMode`, `SetImmune`, `CharacterImmunity.IsHidden` | - | - |

**Provider mode in detail.**

- `SpawnpointManager.defaultSpawnpointProvider` is a public static `ISpawnpointProvider`, with `Vector3 GetSpawnPoint(Entity)`, `Vector3[] GetAllSpawnPoints(Entity)` and `Vector3 GetRandomSpawnPoint()`. All three compile against today's stub.
- The wrapper delegates to the saved `PlayerSpawnSelection`. It returns a stone only when `EventManager.CurrentEvent` is a `PlayerPreSpawnCompleteEvent` for a player whose record is `crossing` and whose one-shot mark has not been used. The mark is used up on the first answer, so a stale `CurrentEvent` seen later by `MovementStatisticCollector` or a respawn gets a vanilla point.
- The original provider is restored on Unload and pause, because `SpawnerModHandler.Start` and `PlayerSpawner.ApplyMod` cast the field to `PlayerSpawnSelection`.

### 7.2 Realm plugins (non-public, via `Plugin.Call`)

| Call | Use | Code |
|---|---|---|
| `RealmSentinel.SentinelGrace(ulong, float<=60)` | before every move | verified |
| `RealmTravel.CancelJourney(string)`, `GetDiscoveredCount(string)`, `HasDiscovered(string, string)`, `IsTravelling(string)` | moves, the road beat, the page | verified |
| `RealmWarden.IsNewPlayerProtected(ulong)`, `IsInCombat(ulong)` | Shelter line, Mercy, tips, zone guard, `play` | verified |
| `RealmWarden.RaiseWardenAlert(string, ulong, string)` | `arrival_camp` (Warden caps it at 30 an hour) | verified |
| `RealmHouses.GetHouseSummaries()`, `GetMembers(string)`, `GetHouseLeader(string)`, `GetLiege(string)`, `GetHouse(string)` | banner lines (cached 30 s), pledge targets, page | verified |
| `CrownAndConsequences.GetKingName()`, `GetKingHouse()` | the reveal | verified |
| `RealmEvents.GetNextEvent()`, `IsTruceActive()` | Three Roads, page | verified |
| `RealmArena.IsDuelBlow(string, string)` | zone guard never cancels a duel blow | verified |
| `RealmQuests.ReportQuestEvent(string, "custom", string, int)`, `GetStoryProgress(string)` | quest credit, `Next` or `NextNoTale`, page | verified |
| `RealmRenown.AddDeed(string, string, string, string, string)` | `written`, dedupe key `arrival:<id>` | verified |
| `RealmHerald.PopupsWanted(string)` | popup gate | verified |
| `/raven <house> <letter>` (a player command, not a call) | RealmRavens resolves a house name first (`ResolveTarget`, `FindHouseName`) and refuses if the house has no other members | verified |
| `/house join <house>` (a player command) | works only after an officer's invite (`HouseJoin`) | verified |

**Offered by RealmArrival** (non-public): `OwnsArrival(string id) -> bool` and `ArrivalStage(string id) -> string`.

| Value | Meaning |
|---|---|
| `pending` | no record, or a record not yet seen in `OnPlayerSpawn`, while the arrival is open |
| `crossing` | in character creation |
| `running` | any of `gatehouse`, `released`, `banners`, `hearth` |
| `done` | finished, or a known veteran |
| `none` | not handled: variant C on its first sight, closed, paused, or a staff skip |

`OwnsArrival` is true for `pending`, `crossing` and `running`.

RealmArrival raises no custom hooks and adds no Chronicle type: an arrival is not realm history.

### 7.3 Small changes in other plugins

Each is a separate change of about 20 to 40 lines plus tests. RealmArrival does not depend on them; the config-only fallback is in section 10.

**Merge order matters.** `tools/realm-integration/check.mjs` fails on any `Call` to a method that does not exist. So:

1. RealmArrival lands first, with no call to `GetProtectionMinutesLeft`.
2. Each guard change lands after it.
3. The `ProtectionSoon` call is added to RealmArrival in the same change that adds the Warden method.

**RealmHerald:**

- In the delayed welcome timer and in `Tick`:
  - if `ArrivalStage` is `pending`, `crossing` or `running`, defer to the next Tick;
  - if it is `done`, skip the welcome popup and lines, send the MOTD alone (when `ShowMotdOnJoin` is on), and start the path reminders from now;
  - if it is `none` or null (RealmArrival missing), behave as today.
- Skip the connect-time newcomer broadcast when the stage is `pending` (RealmArrival sends `HeraldGate`).
- Add the Catalogue entry `Cmd.arrival` (subject `roads`) and update the Herald logic test.

**RealmWarden:**

- Skip the "Welcome" line while `OwnsArrival` is true.
- For new records, start playtime accrual at the first `OnPlayerSpawned`, not at connect, so character creation does not use up the 60 minutes. The 48 h wall clock still starts at the first join.
- Add the non-public `GetProtectionMinutesLeft(ulong) -> int`.

**RealmQuests:**

- Skip "FirstHint" while `OwnsArrival` is true or once the stage is `done` (`Next` replaces it).
- Defer `ChapterNews` ("TaleBegins") until the stage is `done`.
- Later: `Arrival.json` (section 5.4).

**RealmTravel:**

- Skip the 45 s kit hint while `OwnsArrival` is true or once the stage is `done`.
- Config: add a torch and a stone tool to the starter kit once the item names are confirmed.
- Optional later: `GrantKit` and `StartRoadFor`.

**RealmRenown:** add the deed kind `written` (small renown, no cooldown) to `DefaultDeeds()`.

---

## 8. Safety and abuse

**Camping newcomers.** There are five layers:

1. **Game spawn protection:** the game's `SpawnProtection`, about 30 s.
2. **Gatehouse sanctuary:** all damage to a player whose stage is `gatehouse` (or to their sleeper) inside the hall box or the drop pad is cancelled. This includes newcomers hitting each other. Players who are not in arrival get no sanctuary, so running into the hall is no escape from a fight.
3. **Eviction:** an online player is evicted if they have no `gatehouse` stage, do not hold `realmarrival.skip`, and stay in the hall box more than 3 s.
   - They are moved to the eject point E, 4 m outside the gate (SentinelGrace, then teleport), with `Evicted`. This is never a trip to the Hearth, so it is not free travel.
   - Three evictions in 10 minutes raise `RaiseWardenAlert("arrival_camp", ...)`.
   - Players who wake in the hall from an old arrival are released with `ReleasedGate`, not evicted, and are never counted.
4. **Release shield:** for 30 s after the gate, damage to the newcomer is cancelled. It ends at once if they strike someone.
5. **Arrival-zone guard:** for variant A only, and only while RealmWarden does not report protection (Warden missing, or its 48 h clock run out).
   - Inside the avenue and the Hearth area, damage from another player to someone released in the last 15 minutes is cancelled, as long as the victim has not struck anyone.
   - Duel blows (`RealmArena.IsDuelBlow`) are never cancelled.
   - Each blocked attempt counts toward a Warden alert, at most 1 per attacker per 10 minutes.
   - When Warden protection is present, Warden does this job and the guard stays out of the way. Veterans never get the guard, so fights between veterans at the Hearth on wipe day are unaffected.
6. **RealmWarden protection:** 60 minutes, with accrual starting at landing after the Warden change.

In addition:

- The portcullis is closed except in short cycles.
- Stones follow the occupancy and overflow rules in section 3.
- The Herald line names the newcomer only once they are out, and is capped at 6 an hour.
- The flare shows only that someone arrived.
- The end of protection is never announced.

**Griefing the site.**

- Every sculpture is a protected RealmSculptor placement inside staff crest zones: no decay, no salvage, no player building against it.
- Walls are 9 to 13 cells high with no outside ledges, except the Pilgrim's ledge. Play-test 6b checks that it cannot be climbed from outside.
- RealmArrival protects its own 54 cells.
- A player crest near the edge could claim land next to the site, so stewards keep a buffer, and the rules forbid building against the Hearth and the Gatehouse.
- After each restart, check `/sculpt placed`. The self-check (section 4.5) closes the arrival if the stones lose their floor.

**Trapped or stuck.**

- The Pilgrim's Stair and Drop always work, even without the plugin. `/arrival skip` and `/arrival admin pass` also exist.
- Unload and pause **open** the gate, end shields and restore the provider. On load, `OnServerInitialized` always sets the gate open, so a crash or a world save taken while the gate was closed leaves no trap.
- Nothing is ever left toggled on the client, because no god mode, immunity or fly camera is used.
- The gate never closes while any player is within 3 m of a gate cell. Clearance is checked before every row; if it is blocked, closing pauses 2 s and retries.
- While two or more newcomers are inside, or in wave mode, the gate is held open.

**Restarts and reloads.**

- Stages are saved on every stage change (debounced) and on `OnServerSave` and Unload.
- After a plugin reload with players online, `OnServerInitialized` rebuilds each online player's active arrival from their saved stage. Queued lines are not restored; the next beat fires on its trigger.
- Shields and pledge windows are in memory only and simply end.

**Kit and reward farming.**

- RealmArrival grants no items and no marks. The kit is RealmTravel's, with its guards.
- Quest credit is limited by RealmQuests, and the new achievement pays renown only.
- The renown deed is deduped per Steam id.
- `play` never reports quests or deeds.
- Heal and nourish happen once per arrival.

**Alts.**

- An alt gets a walk worth nothing new.
- Pledges are limited to 2 per arrival, 6 per house per hour, variant A only.
- Gate cycles are at least 20 s apart, and flares at most one a minute.

**Forged events and answers.**

- The arrival starts only for a record in `crossing`. That stage is set in `OnPlayerSpawn` when the game reports `AtFirstSpawn`, and used up on the first `OnPlayerSpawned`.
- A repeated or client-raised `PlayerPreSpawnCompleteEvent` (`rok.player.prespawncomplete`) or `PlayerFirstSpawnEvent` for any other stage does nothing.
- Teleports only ever go to stored stones, mercy stones or E.
- A forged popup answer can at most open a gate the player could open by walking 10 m.

**Not free travel.**

- Mercy respawns go only to the Hearth: one by default, and only while protected.
- Eviction and skip-from-hall move a player 4 m out of the gate.
- `/arrival tour` never moves anyone, and `play` is admin-only.

**Lag and a full server.**

- The tick touches only active arrivals (distance checks).
- A gate cycle is at most 30 block writes and 30 colour writes; a flare is 24 colour writes.
- There are at most 2 popups per arrival, and lines are at least 4 s apart.
- Banner data is cached for 30 s.
- Sculpture placement is a one-off job in a quiet hour.
- A burst of newcomers uses the stone overflow and, when planned, wave mode.

**Data safety.** If `oxide/data/RealmArrival.json` exists but cannot be read, routing is switched off (vanilla spawns), `ArrivalStage` returns `none`, and the file is never overwritten. `SeedFromHerald` reads `RealmHerald.json` read-only, once, on the first load. It seeds only Herald records that show real play: a path step done, or `LastSeen` at least an hour after `FirstSeen`. Someone who only ever quit during creation is not counted as a veteran.

---

## 9. Build plan

### 9.1 Files to create

**`plugins/RealmArrival.cs`** (Oxide 2.0.3867, C# 3, about 2,200 to 2,600 lines). Contents:

- records, the variant decision and the stage machine;
- teleport routing with the two arrival checks, road mode, and provider mode behind a switch (one-shot mark, delegate and restore);
- stone occupancy and overflow;
- the 1 s trigger tick, the route corridor and the narration queue (drop passed beats);
- the gate and flare writer by reflection, forced open at load;
- the site self-check;
- sanctuary, eviction to E, shields and the zone guard;
- Hearth's Mercy;
- pledge stones;
- `#region Popups` and `#region Chat style`;
- lang (about 65 keys);
- `/arrival` and `/arrival admin`;
- the APIs `OwnsArrival` and `ArrivalStage`;
- read-only, filtered seeding from `RealmHerald.json`.

**`plugins/docs/RealmArrival.md`:** guide, run-sheet (including the after-wipe list) and the play-test table.

**`plugins/docs/RealmArrival/logic-tests/`** (`Mocks.cs`, `Tests.cs`, `run.sh`), about 170 checks. They cover:

- variants A, B (each `VeteranMode`) and C, the record table, seeding and its filter;
- crossing never going stale, and the restart of an in-arrival stage on a fresh world;
- ignoring repeated or forged events;
- narration start on move or at the cap, line gaps, dropping passed beats, the banner drop distance;
- out-of-order stations and the skipped summary;
- the Hearth quiet ring; the next-step choice with and without a running tale;
- nudges, the off-route wander rule (and no wander or handover inside the Gatehouse), timeout in and out of the hall, AFK release;
- logoff and resume per stage, stale marking, stale wake-ups in the hall;
- `ArrivalStage` values, including pause and close;
- the two arrival checks, retry and road mode;
- stone occupancy with sleepers, and overflow;
- the provider: it answers only for the marked event, only once, delegates otherwise, and restores on Unload;
- respawn routing by subclass: bed and base untouched, mercy count and protection gate, no items in the hook;
- gate clearance, cycles, multi-newcomer hold, unload and load open the gate, the self-check closes the arrival;
- flare interval;
- sanctuary only for players in arrival, eviction to E, alert caps;
- the release shield and its end on a blow; the zone guard (variant A only, off while Warden protects, duel blows pass);
- pledge caps and the house notify;
- popup token, kind and deadline, and the popups-off fallbacks;
- `play` grants nothing;
- the Herald line cap, chat style and lengths, and a damaged data file.

**`tools/exploit-review/arrival/`:** covers these cases, and is added to `tools/exploit-review/run.sh`:

- hall camping and hall-as-refuge;
- eviction as travel;
- forged spawn events and popup answers;
- pledge spam with alts;
- gate crush;
- the reload and crash trap;
- mercy farming;
- skip as travel;
- `play` as a reward source;
- a kit double-claim through other plugins.

### 9.2 Files to change

- The plugins in section 7.3, each with its logic tests updated, in the merge order given there.
- `plugins/docs/RealmQuests/content/Achievements.json`: add `ex_written`.
- `tools/realm-integration/check.mjs`: no `STAFF_COMMANDS` change, because `/arrival` goes in RealmHerald's Catalogue.
- Docs:
  - `docs/realm-commands.md`: the speaker table (`RealmArrival | Hearth`), an `/arrival` row, the popup rows;
  - `docs/saga/locations.md`: L13;
  - `docs/community/lore.md`: the Unwritten, the Crossing, the Gatehouse;
  - `docs/saga/README.md`: fix the "On-screen notice" label (`CoreServer.Notice` is a chat line);
  - `docs/community/how-to-play.md`: a survival-basics section (tools, torch, food and water, crest, bed, log off behind walls);
  - `docs/realm-systems.md`: the RealmArrival row and its calls;
  - `docs/ROADMAP.md`: PT-ARR rows;
  - `docs/community/ops/staff-roles-and-permissions.md`: the two permissions.
- `launcher/renderer/coach.html`: the coach-card line (optional).

### 9.3 Sculptures and sign art

- Generators in `art/sculptures/src`: `gatehouse-unwritten.mjs`, `processional.mjs` (a and b), `pledge-stones.mjs` (six files), `hearth-ring.mjs` and `wayboard.mjs`.
  1. Run `node art/tools/sculptor/cli.mjs` build, preview and check (64 cells a side, 4,000 blocks, face-connected, single-block shapes).
  2. Add tool tests.
  3. Run `node portal/scripts/sync-art.mjs`.
- New painting `the-crossing` (320 x 256) in `art/paintings/RealmPainterArt.json`, made with `art/tools/painter`.
- Reused art and boards: `poster-welcome`, `crest-<house>` x 6, and the boards `chronicle`, `proclamation`, `event`, `standings` and `notice`.

### 9.4 Checks to pass

- `tools/plugin-compile-check/check.sh`
- `node tools/realm-integration/check.mjs`
- each logic-test `run.sh`
- `tools/exploit-review/run.sh`
- `node art/tools/build.mjs check --skip-png`, with the art tool tests

### 9.5 First-test plan on the real server (`G:\RealmTest\server`)

Run these in order with two Steam accounts (a fresh one and a watcher). Write each result into `plugins/docs/RealmArrival.md`. Tests 1 to 4 need no site.

1. **Hook probe.**
   - Install with `Open` false and hook logging on, and join with a fresh account.
   - Log timestamps for `OnPlayerConnected`, `OnPlayerSpawn` (`AtFirstSpawn`, and `e.Position`, which is the pre-spawner point), `OnPlayerSpawned` and the first movement.
   - Note how long creation takes, and which current greeting lines (Herald, Warden, Quests, Travel) appear on the customize screen, from the client log and screenshots.
   - Reconnect, then die once without a bed, and log which respawn subclass arrives.
2. **Spawn data.** Read `Mods\<level> Spawn Points.defaults.cfg`, and record the pre-spawner positions from test 1.
3. **Teleport stick.**
   - Put one stone on bare flat ground, then `mode teleport` and `open`.
   - With a fresh account, check the position on the server and on both clients at 3 s and 10 s. Repeat with `TeleportDelaySeconds` 1.
   - Repeat with the stone on a placed block floor, to see whether the client ever stands in the floor before its pages load.
4. **Glimpse.** Record the client at 60 fps around Finish, and step through looking for any frame at the vanilla point.
5. **Sculptor and Painter basics** (ROADMAP PT2): run `/sculpt materials`, place `heralds-pillar`, check the colours, run `/paint poster-welcome`, then rejoin and restart.
6. **Gate blocks.**
   - On a test wall: `gate build`, then `gate test` (open and close), with the watcher at 5 m and 30 m. Note the visual delay per row, check for collapse, salvage drops or page artefacts, and restart while closed (expect the gate open after the load).
   - Then `beacon build` and `beacon test`, seen from 100 m.
   - 6b: try to climb onto the Pilgrim's ledge from outside, without building.
7. **Popups.**
   - Run `/arrival admin play` on yourself.
   - Check that the card shows, that line breaks work, that the custom label shows and that the reply opens the gate.
   - Then try `InterruptPopups` true and note whether it traps input.
   - Test the pledge confirm labels and reply.
8. **Site build.** Run the run-sheet: crests, the pair lot, pieces in route order, the fire pit and lights, 15 signs, waystones, all points, then `/arrival admin check`.
9. **Full run.**
   - Fresh account, Finish to Written with a stopwatch (target 3 to 6 minutes), once by day and once by night.
   - Check the order at the Hearth: Quests' lines at 40 m, discovery at 12 m, then the fire lines, with no interleaving.
   - Check that `/kit starter` delivers and that Warden reports protection.
10. **Pledge.** Pledge with a house member online, then with none online. Check `/raven <house>` and `/house join <house>` after an invite.
11. **Camping.**
    - The watcher tries to enter and loiter in the hall (expect eviction to E), to strike in the hall, and to use the hall as a refuge mid-fight (expect no protection).
    - On the avenue, try melee, bow and rope during the shield, the zone guard (with Warden unloaded) and afterwards.
12. **Logoff midway.** Log off in `gatehouse`, on the avenue and far away. Check resume or release, the sleeper in the hall, and a stale wake-up after `reset` with stage `done`.
13. **Respawns.** Mercy (Randomly and Normal) while protected, a second death (vanilla), and a bed respawn (untouched).
14. **Provider mode.**
    - Repeat test 4 in provider mode.
    - With two fresh accounts, one marked: Respawn Randomly with each, then restart with a Spawn Points mod file present.
    - Check the log for `SpawnerModHandler` exceptions, and check that the unmarked player gets vanilla points.
15. **Veterans.** On a new save slot or after a wipe of the test server: a seeded id gets variant B in each `VeteranMode`. Test `/arrival skip` and `/arrival tour`. Check that the self-check closes the arrival while the site is missing.
16. **Extras.** Toasts (Medium and Low, during and after the loader), each `AudioController` id, the HUD buff. Leave any that fail switched off.
17. **Unload and pause** while the gate is closed and a newcomer is inside: the gate opens, nobody is stuck, and the paused newcomer gets Herald's normal welcome.
18. **Wave mode** with 5 alts arriving within a minute, then 13 alts without wave mode (to test overflow).
19. **RealmSentinel:** expect no findings for any of the above.
20. **Greetings.** After the section 7.3 changes, a fresh account hears exactly one welcome voice.

---

## 10. Fallbacks for each UNVERIFIED dependency

| Dependency | If it fails |
|---|---|
| A teleport from `OnPlayerSpawned` sticks (Travel T4/T5) | `mode road`: no move. `/road the-hearth` leads them there, the fire beats play on arrival, and the Herald line is sent at the fire. |
| The loader also covers the hall's pages | The T+10 check re-teleports once. If unconfirmed arrivals stay common, raise `TeleportDelaySeconds` to 1 to 2, or use road mode. |
| No glimpse of the vanilla point | Try provider mode (test 14). If that misbehaves, accept a sub-second glimpse under the loader. |
| The provider wrapper keeps vanilla behaviour | Stay on `teleport`. |
| Gate rows sync smoothly; no crush, salvage drop or page artefacts | `GateMode open`: the gold line is the moment, and the Herald line still fires. |
| The ember flare is visible from 100 m | `beacon clear`; no flare. |
| Popups show, keep custom labels, and replies run | Chat lines are always sent. The gate opens on the gold line, and the pledge confirms by dwell. |
| Chat sent during the loader is lost | Narration starts on the first move, capped at 12 s. Tune the cap from the `status` timings. |
| `OnEntityHealthChange` covers players and sleepers in the box | Closed walls, Warden protection, eviction and stewards. Shorten the hall stay with `AutoOpenSeconds`. |
| Sculptures and colours reach clients; material ids are right | Fix `MaterialIds`, or place plain stone (`Paint: false`). |
| The court with 2-cell eaves stands under `blockCollapsing` | Remove the eaves: a plain walled court. |
| The 2-cell Pilgrim's Drop does no fall damage, and the ledge cannot be climbed from outside | The drop pad is a sanctuary for players in arrival. If it is still a problem, add a final step, or remove the ledge and rely on unload-opens. |
| One staff crest covers the site | Use 2 to 4 crests, or shorten the avenue by closing the monument gaps. |
| Server-painted signs reach clients (PT2) | The board text is already in chat (Roads, Next event, Crown). |
| Toasts, audio ids, the HUD buff | Shipped off or empty. |
| Heal, Nourish and Hydrate work | Set `HealAtHearth` false. |
| Warden protection is already true at the fire, and `/kit starter` is accepted | The kit line still points to it, and RealmTravel's own refusal text explains the rule. Admins can `/warden protect`. |
| `GetProtectionMinutesLeft` not added | No `ProtectionSoon` line; the call is not compiled in. |
| +z is north (Travel T12) | Nudges use metres and "at the end of the banners". |
| The game clock is readable on the server | Skip the `Dusk` tip, and always use `RevealEmpty` or `RevealKing`. The braziers carry the night view. |
| The Herald, Warden, Quests and Travel guards are not merged | Config: RealmHerald `WelcomeNewPlayers`, `WelcomePopup` and `HeraldNewcomers` false; RealmTravel `Kits.NewcomerHint` false. Warden's "Welcome" and Quests' "FirstHint" and "TaleBegins" still fire during creation, probably unseen. Set them back when RealmArrival is closed. |
| RealmQuests accepts the custom achievement and RealmRenown has the `written` deed | Remove `ex_written`. The arrival still works without quest credit. |
| No season story is running | `NextNoTale` points to the quest board. |
| `crown-market` or `listing-field` waystones are missing | `RoadsB` falls back to `/road the-hearth`-style help (`/travel` lists what is known), and the W1 notice is left unbound. |
| The gate, fire and throne line fits the real terrain | Place the Gatehouse for the best view of the fire alone. The throne reveal becomes the Reveal line. |
| Sleepers are protected | `Sleeper` tip on the next join; stewards watch `/arrival admin status`. |

**Launch days.** For a scheduled "Hearth Calls" wave (launch or season start), stewards run `wave on`, restart with the Golden Summer mood, and may use `/time` set to dawn as the gates open. These levers are global, so they are used only for scheduled waves, never for one newcomer. Hearthwardens (volunteers with no admin rights) and house recruiters wait at the fire.
