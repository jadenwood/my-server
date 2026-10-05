# Handoff: continuing Realm on the owner's PC

Read this first if you are a new session picking up the project. It records the state at the
end of the cloud sessions and what was learned on the owner's real Windows 11 PC. The plan from
here to 1.0 and the live-service year, with every UNVERIFIED item as a play-test checklist, is
[`ROADMAP.md`](ROADMAP.md).

## What Realm is

A medieval-politics community revival of **Reign of Kings** (Steam app 344760; dedicated
server app 381690). Original lore: the realm of Ostreval and six great houses
(`docs/community/lore.md`). Players use their own Steam copy; game files are never modified or
redistributed (EULA, Easy Anti-Cheat). Map of every system: `docs/realm-systems.md`.

- `plugins/`: 26 Oxide 2.0.3867 plugins on `claude/great-maxwell-wrksvt` (C# 3 only; cross-plugin
  methods must be non-public). The list, with what each does and how far it is proven, is in
  `docs/realm-systems.md` section 2; every chat command is in `docs/realm-commands.md`; what players
  get, in plain words, is in `docs/community/how-to-play.md` ("What's in Realm").
- `launcher/`: two Electron apps from one codebase: **Realm Steward** (owner/admin: setup wizard,
  servers, console, Court, Doctor, Go Public, Publish) and **Realm** (the one-screen player app with
  signed news and signed updates).
- `chronicle/`, `portal/`, `bot/`, `streamkit/`, `analytics/`, `ops/`, `mods/`, `art/`,
  `docs/saga/`, `docs/legal/`, `tools/rok-sim` (fake server for tests).

## Owner's PC

- Windows 11. Steam library `G:\SteamLibrary`. Game: `G:\SteamLibrary\steamapps\common\Reign Of Kings`.
- Steam server copy (read only): `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server`.
- Test server Realm manages: `G:\RealmTest\server` (Oxide installed, plugins deployed).
- LAN address 192.168.1.75. Router port forwarding not done yet.

## Proven on the real server (2026-10-02)

- The owner joined their own server and reached character creation. Steam auth, EAC, world
  load and all 14 plugins (compiled by Oxide, loaded, Season 1 auto-started) work.
- Server ready lines: `Server for N players started on port P.` then `Game has started.`
  (Unity's `Initialize engine version:` is far too early).
- The game's real log is `<server>\Logs\Log[yyMMdd-hhmmss].txt`; the client writes its own
  `Logs\Log[...].txt` in the game folder. `-logFile` only gets Unity's lines.
- The game never reads stdin. Commands go through the admin console socket (`-cport 11000`,
  unauthenticated, listens on all interfaces; the server shuts down when its last console client
  disconnects). Firewall rules must block TCP 11000-11003 inbound; never forward them.

## Real problems hit, and their status

| Problem | Cause | Status |
|---|---|---|
| `spawn Server.exe EACCES` | Server.exe needs administrator rights | Steward runs ROK.exe directly |
| "Unable to resolve host name (127.0.0.1:7350)" | `host:port` typed in the address box | Copy button copies the address only; Doctor explains |
| App crash `EBUSY ... Session.lock` | backup read a file the server locks | Backups skip locked files |
| `k_EBeginAuthSessionResultGameMismatch` on join | server registered with Steam as 381690 | ROK.exe is started with `SteamAppId=344760` (fixed, commit 1a77418) |
| "The port 7350 is already being used" | a second server running: double Start (fixed by a per-server start lock), the game client, or most likely the game's **Server.exe watchdog** running elevated and relaunching ROK.exe ~2 s after it is ended | Start lock + no auto-restart into a port clash + explanation + pre-start detection with "Stop it cleanly" / "Adopt it" (commit f7ade43). UNVERIFIED on Windows: ROADMAP PT0.5, PT0.6 |
| New world each failed start (`Saves\Slot3`, 6, 8, 9, 10; "Could not load world N") | failed starts leave a new slot | Steward now remembers and pins the world (`docs/worlds.md`, commit f7ade43). UNVERIFIED on the real server: ROADMAP PT0.4 |
| Firewall "3 of 4 Realm rules" | one rule (likely the TCP 11000 block) missing | Owner should re-run Go Public > Add firewall rules (ROADMAP PT5.1) |
| `eac_usermode ... blocked from loading into LSA` notice | Windows 11 LSA protection notice | Harmless; do not disable LSA protection |
| `Lobby query failed` | CodeHatch's old lobby service is offline | Harmless; Realm uses its own signed server list |

## Landed since the first handoff

On `claude/great-maxwell-wrksvt` (2026-10-04, head eaa25f8). None of it has run on the real server.

- **Waves 1 and 2:** launch reliability (real ready lines, leftover, watchdog and game-client
  pre-checks, Stop it cleanly / Adopt it, same world every start; f7ade43), the remaining exploit
  fixes (aa147a6), `Build-Realm.bat` with the 1.0.0 installers, `START-HERE.md` and
  `docs/troubleshooting.md` (5a92e6f), the engraved art pack (b1f5899), the launcher UI pass (61af9a5),
  and the in-game experience: one chat style, RealmHerald, popups and mood presets (f81bfbc, 845506a).
- **Wave 3** (merged 2026-10-03): `team/sculptor` (RealmSculptor, `/sculpt`, block monuments; ae72f40),
  `team/sign-painter` (RealmPainter, `/paint`, sign art and live boards; 8952056), `team/ironbreaker`
  (RealmLegendary, `/ironbreaker`; fe816b4), `team/anti-cheat` (RealmSentinel, `/sentinel`; a03f6b8),
  `team/player-launcher` (the one-screen player app with signed news and updates; 6390467),
  `team/web-and-broadcast` (the art pack in the portal, overlay, scenes and bot) and
  `team/production-roadmap` (this roadmap; 2bf0b5a). The wave-3 merge notes are done: one
  `STAFF_COMMANDS` list (`sentinel`, `paint`, `ironbreaker`, `sculpt`), `PENDING_PLUGINS` empty, the
  four staff permission rows, the `legendary` exploit case, and the `sentinel` runner in CI.
- **Wave 4** (merged 2026-10-04, except `team/steward-integration`): seven new plugins. See the next
  section for what each adds, its commands and its in-game tests.
- **The arrival site** (`team/arrival-site`, merged at a601e84): the Gate of the Unwritten design
  (`docs/arrival-design.md`), its sculptures in `art/sculptures/` (23 in all with one site plan) and
  `the-crossing` sign painting. The plugin that would use them (`team/arrival-plugin`, RealmArrival) is
  still being built and is not described here.

## Wave 4: the realm's new systems

Eight team branches finished on 2026-10-04. All eight are **merged into `claude/great-maxwell-wrksvt`**
(`team/steward-integration` came in with `team/arrival-plugin`, 3386192). All checks passed on each branch and on
the merged base. Nothing here has run on the real server: every in-game behaviour is UNVERIFIED, and
each guide's test steps are folded into [`ROADMAP.md`](ROADMAP.md) section 5 (rows marked "wave 4").

| Branch | Commit | State | What it adds | Proof |
|---|---|---|---|---|
| `team/dominion` | 88228da | Merged (fast-forward, before ef90c8b) | `RealmDominion.cs`: territorial war over seven named holdings, taken by standing in them with your house during the War Hours; garrisons, a daily payday into house vaults, `holding_taken` in the Chronicle, the `/paint dominion` board, `RealmDominionMap.json` for the web. | 196 mock checks; exploit suites dominion 37 + 13 |
| `team/quests` | bdeacaf | Merged (ef90c8b) | `RealmQuests.cs`: daily and weekly tasks, the Season 1 story *The Hollow Crown* (4 acts, 17 steps), 68 achievements, weekly house goals; content as JSON in `plugins/docs/RealmQuests/content/`. | 298 mock checks; exploit suites quests 45 + 13 |
| `team/arena` | db52822 | Merged (e0fb991) | `RealmArena.cs`: duels to the first fall (no death, no loot), stakes held by the treasury, an Elo ladder and a weekly Champion of the Ring, team duels, brackets (also for the Royal Tournament), trial by combat for RealmLaws, Hearth Dice and Twenty-One. | 312 mock checks; exploit suites arena 52, arena-events 15, arena-laws 12 |
| `team/travel` | 2818f55 | Merged (a3c34f7) | `RealmTravel.cs`: waystones found on foot, `/travel` by the game's own server-side teleport for a toll, `/home` in your own crest zone, `/road` directions, starter, house and season kits. | 301 mock checks; exploit suites travel 69 + 11 |
| `team/crafts` | bc14c8b | Merged (1298065) | `RealmCrafts.cs`: eight professions with XP from the game's own container, harvest, damage and crafting events; perks, a weekly Master Crafter, house workshops, a commission board with held marks. | 304 mock checks; exploit suite crafts 71 |
| `team/world-events` | 91a3a67 | Merged (0005ccf) | `RealmWorld.cs`: treasure hunts, the Blood Moon, the Merchant Caravan, Wandering Legends, the Harvest Fair and Midwinter, the weekly Census (`census_taken`); two new moods. | 326 mock checks; exploit suite world 48 |
| `team/heraldry` | d2b8ba2 | Merged (103a10e) | `RealmHeraldry.cs`: house colours on the game's own guild banners, crests, armour tints and name tags; council elections each season and the crown's referendums (`vote_held`), one vote per account with alt rules. | 274 mock checks; exploit suites heraldry 42, heraldry-crown 33, heraldry-treasury 14 |
| `team/steward-integration` | 8e86482 | Merged (3386192, with `team/arrival-plugin`) | Realm Steward: deploys the plugins' data files (sculptures, sign art, quest content; RealmArrival's site plan since `team/arrival-deploy`), a Realm features screen (switches for every plugin), the Sentinel screen with Kick and Ban, Publish news and Publish update forms, staff commands for every plugin in the Court; Chronicle types `blade_claimed` and `blade_lost` for the Ironbreaker. | launcher 267 tests; 40 screenshot checks; every plugin and package suite green |

**How they connect.** Every new plugin pays and charges marks only through RealmTreasury's new
non-public methods (`GrantHouseIncome` for Dominion's payday, `RewardMarks` for Quests, World and
Crafts, `ChargeMarks` for tolls and colour fees, `HoldMarks`/`PayFromHold`/`ReleaseHold`/`GetHold` for
duel stakes, commissions and ballot deposits), so `/treasury audit` still balances. Titles and deeds go
through `RealmRenown.AddDeed`; house points through `RealmSeasons.AwardHouse`; news through the Herald
and `RealmChronicle.Log`; windows through `RealmHerald.PopupsWanted`. Quests counts what the others
report (`ReportQuestEvent` from Crafts, World and Heraldry). World keeps clear of RealmEvents' schedule
and asks Arena and Travel whether a player is in a duel or on a journey. Arena hooks into RealmEvents
(the tournament bracket) and RealmLaws (trial by combat, `IsDuelBlow` under the peace law). Heraldry
seats elected councillors through new CrownAndConsequences methods. The full call table is in
[`realm-systems.md`](realm-systems.md) section 3.

**Counts on the merged base:** 26 plugins, 326 cross-plugin call sites, 59 chat commands, 45 Chronicle
types (47 with `team/steward-integration`), the `/realm` hub lists 53 commands.

### Merge notes for `team/steward-integration`

- It is based on eaa25f8 (the current base head), so the merge is clean except for the docs this
  branch (`team/production-wave4`) rewrites: in `docs/realm-systems.md` keep this branch's text and set
  the Chronicle paragraph to 47 types (`blade_claimed`, `blade_lost`); drop the "on its branch" notes for
  Steward in `realm-systems.md`, `ROADMAP.md` and this file.
- After the merge: 47 Chronicle types in the three lists, the portal, bot and streamkit art tests and
  `docs/saga/COMMANDS.md`; 75 painter sprites in the RealmPainter logic test; launcher 267 tests.
- Mark STW-1 (Steward part), STW-2, STW-3/SEN-5, STW-4/PLA-3 and SRV-6 done in `ROADMAP.md`, and remove
  the "Steward: list /x admin in moderation.js" follow-ups from the Dominion, World and Heraldry guides.
- Done on `team/arrival-deploy`: `server/Deploy-Plugins.ps1` copies the same data files as Steward with
  Steward's rules (JSON-object check, backup to `_realm-backups\data-<time>\`, owner files left alone,
  `.realm-part` then rename), and both now copy RealmArrival's site plan `art/sculptures/sites/arrival.json`
  as `oxide\data\RealmArrival\site.json` (the installer packs it as `realm-data\RealmArrival\arrival.json`).
  The Court lists RealmArrival's staff commands. The mood folders are deployed to `<server>\realm-moods\` by Update plugins and `Deploy-Plugins.ps1` since `team/steward-fixes` (ROADMAP STW-1).

### Things the owner must do before the wave-4 tests mean anything

- **Mark the places.** Holdings start unmarked (`/dominion admin move <id>`), quest places too
  (`/quest admin place set <id>`), waystones (`/travel admin set`), world places and caravan routes
  (`/world admin place set`, `route add`), arenas and taverns (`/arena admin zone set`, `tavern set`).
  ROADMAP PT1.22 does this on the test world; WLD-2 plans it for the real one.
- **Copy the quest content** to `oxide\data\RealmQuests\` (Steward's Update plugins on
  `team/steward-integration` does it; otherwise by hand), or `/quest` says the board is not posted.
- **Paste the new titles** into an existing `oxide\config\RealmRenown.json` (snippets in the Arena,
  Quests and Travel guides), or delete it so it regenerates; old configs do not gain new defaults.
- **Watch the money.** Dominion can mint about 1,500 marks a day, Quests up to 3,000, World and Crafts
  more, against a default `MintSupplyCap` of 100,000 (ROADMAP SRV-21).

## Next steps

Follow [`ROADMAP.md`](ROADMAP.md). In short:

1. **Finish M0 Integrate.** `team/steward-integration` is merged (check its notes above) and
   `team/arrival-deploy` and `team/steward-fixes` (the mood folders) close STW-1; the open M0
   items: `paint.mjs check` and the new mood
   keys in CI (SGN-8, QA-5), the removed player screens in the screenshot scripts and docs (PLA-1).
   All checks green.
2. **M1 Owner proof.** On `G:\RealmTest\server`, run play-test sessions PT0 (build, install, run),
   PT1 (one player: chat, items, the crown, damaged files, moods, then the wave-4 rows from PT1.22:
   places, teleport, quests, crafts, the living world, holdings, guild colours), PT2 (signs, monuments
   and the new boards) and PT3 (two players, with duels, travel rules, commissions and ballots). The
   **deciding tests** of wave 4 are the teleport (PT1.25), gathering seen by plugins (PT1.27), the
   duel's turned-aside blow (PT3.20) and guild colours from the server (PT1.30). Write each result into
   the guide it settles.
3. **Then M2.** Fix the firewall rule, forward UDP 7350, TCP 7350 and UDP 27015 to 192.168.1.75,
   publish the signed list, and run the friends alpha (PT4, PT5) with RealmSentinel in watch mode.
4. Keep the play-test results, the systems map statuses and the roadmap's gates in step.

## How to check your work

```
tools\plugin-compile-check\check.sh        (bash; compiles all plugins at C# 3 against the shipped DLLs)
node tools\realm-integration\check.mjs     (cross-plugin calls, commands, chronicle types, chat style)
plugins\docs\<Plugin>\logic-tests\run.sh   (bash; each plugin against mocks)
tools\exploit-review\run.sh                (bash; exploit regression suites; tools\exploit-review\sentinel\run.sh too)
cd launcher && npm run check && npm test
cd chronicle && npm test                   (also portal, bot, streamkit, analytics, tools\rok-sim)
node art\tools\build.mjs check --skip-png   (and node --test art\tools\test\*.test.mjs)
node --test docs\saga\tools\check-saga.test.mjs docs\legal\tools\*.test.mjs docs\community\ops\tools\*.test.mjs
```
The full list is in `docs/realm-systems.md` section 7.
On the owner's PC you can also run the real server and read `G:\RealmTest\server\Logs\Log[...].txt`.
