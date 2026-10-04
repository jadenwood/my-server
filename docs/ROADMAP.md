# Realm Edition roadmap: from today to 1.0 and a live-service year

This is the production plan for **Realm Edition of Reign of Kings**, written 2026-10-03 and updated 2026-10-04 for the wave-4 systems (territorial war, quests, the arena, travel and kits, crafts, the living world, heraldry and council elections, and Steward's integration). It covers the work from today to a public **1.0** with Season 1, and the first live-service year (Seasons 2 to 4). It says what "AAA" means for this project, the milestones and their exit criteria, the backlog by area, every UNVERIFIED item as a play-test checklist, the risks, and the volunteer roles.

Read [`HANDOFF.md`](HANDOFF.md) for the current state and [`realm-systems.md`](realm-systems.md) for the map of every part. Update this file at every milestone gate.

---

## 0. Scope

Realm is a community revival of Reign of Kings (Steam app 344760; dedicated server app 381690), set in the original realm of Ostreval ([`community/lore.md`](community/lore.md)). **Everything we build must be usable or visible in the real game or in Realm's two apps.** There is no standalone-engine track and no plan for one.

What reaches players, and how:

| Channel | What Realm puts there | Where it lives |
|---|---|---|
| Server-side Oxide 2.0.3867 plugins (C# 3) | Houses, crown, laws, events, economy, seasons, anti-grief, cheat watch, the legendary blade; territorial war, quests and achievements, duels and tavern games, crafts and professions, the living world, council elections | `plugins/*.cs` |
| Blocks and block colours, which the game already syncs and saves | Monuments in the capital and at the house seats; festival decorations (RealmWorld through RealmSculptor) | `plugins/RealmSculptor.cs`, `art/sculptures/` |
| Painted signs, which the game already syncs and saves | House crests, posters, the Chronicle board, wanted posters, standings, the holdings board, treasure clues and festival standings | `plugins/RealmPainter.cs`, `art/paintings/` |
| Chat, notices and popups | The Herald, the `/realm` hub, first steps, event heralds, every plugin's windows | `plugins/RealmHerald.cs` and every plugin's chat style |
| The game's own guild data (name and banner colours), which the game already syncs | House colours on guild banners, crest flags, armour tints and name-tag icons | `plugins/RealmHeraldry.cs` |
| Server-side moves and items the game already supports | Waystone travel with the game's own teleport call; kits, rewards and prizes into the packs | `plugins/RealmTravel.cs` and the plugins that pay goods |
| The built-in `Mods\*.cfg` system | Atmosphere moods (Long Winter, Blood Moon, Golden Summer, Storm Season, Ashfall, Harvest Fair, Midwinter and more) | `mods/`, `server/Set-Mood.ps1` |
| Realm (player app) | Play, server status, signed news, signed updates | `launcher/` (player build) |
| Realm Steward (owner app) | Server setup, console, Court, Doctor, backups, publishing; with `team/steward-integration`, the Realm features switches, the Sentinel screen, news and update publishing, and data-file deploy | `launcher/` (Steward build) |
| Outside the game | Chronicle overlay and `/realm`, portal, Discord bot and herald, stream scenes, analytics | `chronicle/`, `portal/`, `bot/`, `streamkit/`, `analytics/` |

Hard limits that shape every item below:

- We never add, patch or redistribute Reign of Kings client or server files or assets, and never touch players' installs. The only server file Oxide replaces is in the owner's server copy.
- We never read, analyse, disable or work around Easy Anti-Cheat.
- The game cannot load new models, textures, UI or fonts from a server ([`in-game-art.md`](in-game-art.md)). Visual quality inside the game comes from **what we build with blocks, what we paint on signs, what we say in chat and popups, and the moods**. Everything else is outside the game.
- Original lore only, no franchise names. Third-party assets only under CC0 or a similar licence, with a licence record.
- Anything not seen working on the real server is **UNVERIFIED** and has a concrete test step.

---

## 1. What AAA means for Realm

"AAA" here does not mean new graphics. It means a server and apps that feel deliberate and trustworthy from the first click to the end of a season. Each bar below has a measure, so a gate can be passed or failed.

| Pillar | The bar | How it is measured |
|---|---|---|
| **Reliable** | The server runs a whole Crown Night without a crash. A crash or restart loses no items, no escrow and no Chronicle entries. | 0 unplanned restarts in the last 3 sessions before each gate. Steward's crash log. `/treasury audit` and `/ironbreaker status` balance after every session. Every plugin's damaged-file test (play-test PT1.12) passes. |
| **Fair** | Nobody can duplicate items, farm points, or frame another player. Staff do not play with admin powers. Honest players are never punished by the cheat watch. | `tools/exploit-review/run.sh` green. 0 Sentinel freezes or kicks of honest players in 2 weeks of beta watch mode. Admins hold no plugin admin permission while competing ([`staff-roles-and-permissions.md`](community/ops/staff-roles-and-permissions.md)). |
| **Consistent** | One voice, one look, one set of names everywhere: chat, signs, monuments, launcher, portal, overlay, bot, Discord. | `tools/realm-integration/check.mjs` (chat style, Chronicle types, `/realm` catalogue), `portal/scripts/sync-art.mjs --check`, `art/tools/build.mjs check` (palette, trademark guard). |
| **Polished** | Every player-facing screen, popup, sign and monument has been looked at on the real thing, at the distances and sizes players see it. No text is cut off. | A dated "Seen" note per item in its guide. Screenshot sets for both apps, the portal and the scenes are reviewed after every change. |
| **Delightful** | The big moments land everywhere at once: a coronation or rebellion has a herald line, a popup, a Chronicle entry, an overlay moment, a Discord post and a board that changes. The capital looks like a capital. A newcomer knows what to do in their first ten minutes. | Play-test rows PT4.3, PT4.7 and PT5.15 walk each big moment. New-player test: 3 of 4 first-time players swear to a house or found one in their first session. |
| **Tested** | Every package has automated tests that run in CI. Every claim about the game is either seen working or marked UNVERIFIED with a test step. | CI green on every merge. No P0 backlog item open at a gate. The UNVERIFIED count in [section 5](#5-play-test-checklist-every-unverified-item-in-order) goes down at every gate. |
| **Operable** | The owner can run the realm in about 2 hours a week, and a named deputy can take over. | Weekly upkeep log. A restore drill ([`ops/restore-drill.md`](../ops/restore-drill.md)) passes once a month. The deputy has done one full restart and one restore alone. |
| **Accessible** | The apps work with the keyboard, text has enough contrast, motion can be turned down, and in-game text is readable at 1080p. | Launcher a11y checks, art contrast check, `motion=0` on the overlay and scenes, sign and popup review in PT2. |
| **Lawful and private** | Nothing ships that the rights holder's terms forbid. Players' data is minimal, explained and deletable. No paid perks. | The EULA checklist ([`legal/eula-compliance-checklist.md`](legal/eula-compliance-checklist.md)) has every clause row filled before 1.0. The privacy notice is published. A deletion request is handled within 30 days. |

---

## 2. Milestones

Dates are targets. **A milestone ends when its exit criteria pass, not when its date arrives.** If a gate fails, fix the cause and repeat the same milestone (the rollback rule of [`community/launch-plan.md`](community/launch-plan.md)).

| Milestone | Target | Who plays | Launch-plan phase |
|---|---|---|---|
| **M0 Integrate** | 2026-10-05 to 10-18 | nobody (repo work) | before Phase 1 |
| **M1 Owner proof** | 2026-10-12 to 11-01 | the owner, plus a second account or one friend | before Phase 1 |
| **M2 Owner and friends alpha** | 2026-11-02 to 11-29 | 5 to 10 friends | Phase 1 |
| **M3 Closed beta** | 2026-12-01 to 2027-01-31 | 30 to 60 invited players, 2 to 4 creators | Phases 2 and 3 |
| **M4 1.0 and Season 1** | target 2027-02-13 (Season 1 runs 8 weeks) | the public | after Checkpoint 3 |
| **Live year** | Season 2 from 2027-05-08, Season 3 from 2027-07-31, Season 4 from 2027-10-23 (8 weeks each, then the Interregnum); year review after 2027-12-18 | the public | live service |

### M0 Integrate (repo only)

Goal: one main branch with every finished team branch, green CI, and the deploy carrying everything the new plugins need.

Work: merge every finished team branch (merge notes in [`HANDOFF.md`](HANDOFF.md#wave-4-the-realms-new-systems)), then the P0 items marked M0 in [section 3](#3-backlog-by-area).

State on 2026-10-04: wave 3 (`team/sculptor`, `team/sign-painter`, `team/ironbreaker`, `team/anti-cheat`, `team/player-launcher`, `team/web-and-broadcast`) and seven wave-4 branches (`team/dominion`, `team/quests`, `team/arena`, `team/travel`, `team/crafts`, `team/world-events`, `team/heraldry`) are merged into `claude/great-maxwell-wrksvt`. `team/steward-integration` is on its branch, merging next. Ticked criteria are met on the base; the others are open.

Exit criteria:

1. [x] All 26 plugins are on the main branch. `bash tools/plugin-compile-check/check.sh` reports 0 errors.
2. [x] `node tools/realm-integration/check.mjs --commands` reports 0 problems with one `STAFF_COMMANDS` list (`sentinel`, `paint`, `ironbreaker`, `sculpt`) and `PENDING_PLUGINS` empty.
3. [x] Every `plugins/docs/*/logic-tests/run.sh`, `tools/realm-integration/cross-tests/run.sh`, `tools/exploit-review/run.sh` (with the `legendary`, `dominion`, `quests`, `arena`, `travel`, `crafts`, `world` and `heraldry` suites) and `tools/exploit-review/sentinel/run.sh` pass, here and in CI.
4. [ ] CI runs the sculptor and painter tool tests and `cli.mjs check` (done) and `paint.mjs check` (open: SGN-8), and the mood tests for the new event keys under PowerShell 7 (QA-5).
5. [ ] Steward's **Update plugins** and `server/Deploy-Plugins.ps1` copy `art/sculptures/*.json` to `oxide\data\RealmSculptor\`, `art/paintings/RealmPainterArt.json` to `oxide\data\` and `plugins/docs/RealmQuests/content/*.json` to `oxide\data\RealmQuests\` (STW-1: Steward's part is on `team/steward-integration`; the PowerShell script is open).
6. [ ] `launcher` `npm run check` and `npm test` pass (done); the player screenshot script passes; the design and onboarding screenshot scripts no longer test removed player screens (PLA-1, open).
7. [x] `docs/realm-systems.md`, `docs/realm-commands.md`, `docs/HANDOFF.md`, this file and `docs/community/how-to-play.md` describe the 26 plugins and the wave-4 branch still to merge.
8. [ ] `team/steward-integration` is merged with its notes in [`HANDOFF.md`](HANDOFF.md#merge-notes-for-teamsteward-integration).

### M1 Owner proof

Goal: every UNVERIFIED item one or two people can settle is settled on `G:\RealmTest\server`.

Work: play-test sessions **PT0 to PT3** in [section 5](#5-play-test-checklist-every-unverified-item-in-order). Write each result into the plugin's guide (replace the UNVERIFIED tag with "Seen 2026-mm-dd" or with what was seen instead) and into the status column of [`realm-systems.md`](realm-systems.md).

Exit criteria:

1. Every PT0, PT1 and PT2 row has a result. Every PT3 row has a result, or is moved to PT4 with a reason.
2. Each **deciding test** has passed or has its fallback switched on and written down:
   - server-placed blocks show for clients with their colours (PT2.10), or RealmSculptor is parked;
   - a server-written sign picture reaches a second client (PT3.2), or RealmPainter is parked;
   - plugin damage blocking works (PT3.5, PT3.11), or the matching `Enforced`/`Block` switches are off and the rules are announce-only;
   - server-side item moves show in the client at once (PT1.6), or the affected plugins run in honour mode;
   - wave 4: the game's own teleport moves a player for everyone and the server follows (PT1.25), or RealmTravel runs with `Travel.Mode: "road"`;
   - wave 4: a client's gathering reaches plugins as container events (PT1.27), or RealmCrafts' gathering XP and bonus yield are switched off and the guide says so (kills, crafting and commissions still work);
   - wave 4: a guild renamed and recoloured by the server shows on clients (PT1.30), or RealmHeraldry's colour sync is off and only elections run;
   - wave 4: a duel's killing blow is turned aside with no death and no loot (PT3.20), or duels are switched off until `Duels.FatalMargin` or `PreventDeathFlag` is settled.
3. The capital's first pieces stand in a crest zone on the test world: the Herald's Pillar, one Chronicle board, one house crest sign.
4. Steward builds, installs and runs the server on the owner's PC with no step done by hand that `START-HERE.md` does not describe.
5. No P0 item open.

### M2 Owner and friends alpha

Goal: people outside the owner's PC join, play the house and crown loop for 2 to 3 sessions, and come back.

Work: play-test sessions **PT4 and PT5**; [`launch-plan.md`](community/launch-plan.md) Phase 1; RealmSentinel in watch mode the whole time; first balance pass (SRV-9).

Exit criteria (Checkpoint 1 of the launch plan, plus):

1. 100% of friends who tried got in, with every failure understood. They joined with the **player app** (Play), not only by typing the address.
2. 0 crashes in the last session; 0 unexplained plugin errors in `oxide/logs`.
3. Every Chronicle type the session used appeared on the overlay, the portal and in Discord.
4. 0 items duplicated or lost: `/treasury audit` and `/ironbreaker status` balance after every session; contract and market escrow counts match ([`smoke-test.md`](smoke-test.md) C11); marks held for duel stakes, commissions and ballot deposits are all accounted for in `/treasury audit`; no kit, quest, festival or event reward was paid twice.
5. Sentinel: 0 alerts on honest players at the freeze score; `/sentinel peaks` recorded for tuning.
6. At least 60% of friends come back for session 2.
7. The capital stands: Old Throne monument, Herald's Pillar at the Hearth, Tournament Arch at the Listing Field, Chronicle board, wanted board, standings board (WLD-3, SGN-4).
8. Wave 4 is set up on the alpha world: the seven holdings, the waystones, the twelve quest places, the world places and caravan routes, the Proving Ring and a tavern are marked (WLD-2), and the friends played at least one War Hours, one duel night and one world event (PT4.9 to PT4.12).
9. The marks minted per day by Dominion, Quests, World and Crafts are recorded and the economy budget is set (SRV-21).

### M3 Closed beta

Goal: prove the server, the staff and the story at the size of a real community, and rehearse a season.

Work: play-test sessions **PT6 and PT7**; launch-plan Phases 2 (creator night) and 3 (open weekends, by invitation through the signed list); a 4-week rehearsal of Season 1 Acts I and II from [`saga/`](saga/README.md); staff recruited and trained; RealmSentinel switched to enforce after 2 clean weeks; a full wipe at the end.

Exit criteria (Checkpoints 2 and 3 of the launch plan, plus):

1. Join failures under 5% of attempts across the beta, each with a logged cause.
2. At most 1 crash per weekend, and it recovered by itself (Steward's restart ladder).
3. A 30-player Crown Night ran with all big moments landing (herald, popup, Chronicle, overlay moment, Discord, boards).
4. A house monument was placed with 20 or more players online without a lag complaint (PT7.9).
5. Sentinel in enforce mode for at least 2 weeks with 0 wrongful freezes or kicks; every alert reviewed within 24 hours.
6. Moderation: 0 cases open longer than 7 days; appeals answered in the time [`ban-appeals.md`](community/ops/ban-appeals.md) sets.
7. The restore drill passed on the beta world; the deputy did one restart and one restore alone.
8. At least 30% of unique players returned the following weekend; at least 4 houses with 3 or more active members.
9. The six house seats are mapped and each has its crest sign; at least two house monuments stand (WLD-4).

### M4 1.0 and Season 1, "The Hollow Crown"

Goal: open to the public with Season 1 and keep it open.

Work: the clean-slate wipe (OPS-9), the signed 1.0 installers and news feed, the trailer and creator kit, the launch post, the Season 1 run-sheets.

Exit criteria for **1.0 itself** (checked on launch day):

1. Every **P0 and P1** backlog item is done or explicitly moved to the live year by the owner, in writing, in this file.
2. Every play-test row in sections PT0 to PT7 has a result. Any row that failed has its fallback switched on and the feature's guide says so.
3. The EULA checklist has every clause row filled; the privacy notice is published on the portal; staff have read the moderation handbook (COM-6, COM-7).
4. The player installer is code-signed, or the "More info, Run anyway" path is shown on the download page with a screenshot (PLA-4).
5. Backups run on a schedule and copy offsite; the uptime monitor posts to the staff channel (OPS-3, OPS-4).
6. The capital and all six house seats are built (WLD-3, WLD-4); the Ironbreaker monument and blade are ready for the first tournament.
7. Season numbering starts at Season 1 for the public world; beta legends are archived, not mixed in (OPS-9).

Exit criteria for **Season 1** (checked at its end): the season ended on its planned date with `season_ended` in the Chronicle, the Hall of Kings carried every reign past the wipe (smoke test E13), and the season review ([section 2.1](#21-each-season-in-the-live-year)) is written.

### 2.1 Each season in the live year

Every season follows the same rhythm: **8 weeks of play, then a 3 to 4 week Interregnum** for the wipe, build-out, tuning and the next saga pack. Each season adds one headline feature, one mood, one set of monuments and one set of signs, all within the channels in [section 0](#0-scope).

| Season | Name and premise | Headline feature | Mood | World and art | New events |
|---|---|---|---|---|---|
| 1 | **The Hollow Crown.** The throne is empty and the crown is hollow until oaths, law, coin or blood fill it ([`saga/season-1-the-hollow-crown.md`](saga/season-1-the-hollow-crown.md)). | The full political loop: houses, crown, claims, laws, court, treasury, ravens, dynasties. | Grim but Readable | The capital (L01-L06), the six seats (L07-L12). Herald's Pillar, Old Throne, Tournament Arch. Crest, banner and event signs. | Crown Night, Royal Tournament, King's Hunt, Hearth Truce |
| 2 | **The Iron Oath.** The Ironbreaker is drawn from the stone at the Listing Field; whoever bears it carries the realm's oldest grudge. | The Ironbreaker (RealmLegendary) and a tournament circuit: a Royal Tournament every second week, with the blade as the grand prize. Dedicated Chronicle types `blade_claimed` and `blade_lost` (SRV-6). | Golden Summer | The Ironbreaker monument at the Listing Field. **Seat monuments earned:** the house that tops a fortnight's standings raises its monument at its seat (EVT-6). Ironbreaker board in the capital. | The Grand Melee (tournament final), Bearer's Challenge (EVT-7) |
| 3 | **The Long Winter.** Snow closes the high roads; the ravens fly more than the riders. Food and coin decide more than swords. | Intrigue and scarcity: raven interception and spy reports matter, the market and vaults carry the season, the Hearth Truce becomes a weekly fixture. Winter decrees as RealmTreasury and CrownAndConsequences config, no new plugin unless a spike proves it (SRV-12). | Long Winter, with Storm Season nights | Beacon towers on the roads between the seats (new sculptures), winter posters, a "roads closed" board. | Hearth Truce weekly, the Frost Hunt (a King's Hunt variant), Raven Night (EVT-8) |
| 4 | **The Builder's Return.** Masons find Merewin the Builder's marks under the Old Throne; the realm rebuilds its capital for the year's last crowning. | Community monuments: players design sculptures with `art/tools/sculptor` (original work or CC0 sources only), the realm votes, staff place the winners (WLD-8). The year book: a Hall of Kings archive on the portal (WEB-7). | Ashfall for the finale, Blood Moon for the last Crown Night | The rebuilt capital with the winning monuments; a year-in-review board. | The Builder's Contest, the Last Crown Night of the year |

Season exit criteria (every season):

1. The saga pack for the season passes `node docs/saga/tools/check-saga.mjs` before Week 1 (EVT-9).
2. The season ends on its planned date; the Hall of Kings and season history carry over the wipe.
3. The Interregnum rebuilt the capital and seats on the new world in one evening from the layout manifest (WLD-7).
4. Retention: at least 40% of Season N's active players are active in Week 2 of Season N+1. (A starting proposal; set the real target from Season 1's numbers.)
5. A written season review: what players did (Chronicle counts by type), what broke, what to change. Stored with the season's archive.

---

## 3. Backlog by area

**Priority:** **P0** blocks the next gate. **P1** is needed for 1.0. **P2** belongs to the live year. **P3** is nice to have.
**Size**, for one volunteer: **S** up to a day, **M** 2 to 5 days, **L** 1 to 3 weeks, **XL** more than 3 weeks.
**When** names the milestone that needs it. **Role** names the volunteer role from [section 7](#7-volunteer-roles).

### 3.1 Server features (plugins)

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| SRV-1 | **Done 2026-10-03** (wave-3 merges; one list in `check.mjs`). Merge the four plugin branches. Resolve `STAFF_COMMANDS` in `tools/realm-integration/check.mjs` and its test to `['sculpt', 'paint', 'ironbreaker', 'sentinel']`; keep one copy of the RealmHerald logic-test exemption (identical on three branches; `team/sculptor` lacks it). | P0 | S | M0 | Plugin dev |
| SRV-2 | **Done 2026-10-03** (`PENDING_PLUGINS` is empty). Remove the RealmLegendary entry from `PENDING_PLUGINS` once `team/ironbreaker` is merged. | P0 | S | M0 | Plugin dev |
| SRV-3 | **Partly done:** the wave-4 plugins that pay goods or move players (RealmArena, RealmCrafts, RealmQuests, RealmTravel, RealmWorld) already call it; the five named here do not. Every plugin that pays items tells RealmSentinel first: `RealmSentinel.Call("SentinelItemSource", playerId, 30f)` in RealmTreasury, RealmContracts, RealmEvents, CrownAndConsequences (Royal Stores) and RealmLegendary. Any plugin that moves a player calls `SentinelGrace`. Until then Sentinel trusts those commands by name only. | P1 | M | M2 | Plugin dev |
| SRV-4 | RealmContracts and RealmLaws call `RealmPainter.Call("RefreshBoards", "wanted")` after a proclamation or pardon; RealmLegendary calls `RefreshBoards("ironbreaker")` when the bearer changes. | P1 | S | M2 | Plugin dev |
| SRV-5 | RealmRenown default config: an `ironbreaker` deed and a lasting "Ironbreaker" title, so RealmLegendary's `AddDeed` call counts. | P1 | S | M2 | Plugin dev |
| SRV-6 | **On `team/steward-integration`** (`blade_claimed`, `blade_lost` with icons; RealmLegendary switched; `monument_raised` still open). Chronicle types `blade_claimed`, `blade_lost` (and `monument_raised` for RealmSculptor): the three Chronicle lists, `portal/lib/model.mjs`, `launcher/lib/discord.js`, `art/icons/event-map.json` with two new icons, then switch RealmLegendary's two type constants. | P2 | M | Season 2 | Plugin dev, Artist |
| SRV-7 | A non-public claim API in CrownAndConsequences, so RealmDynasties stops calling the `/claim` command method (`CmdClaim`). | P1 | M | M3 | Plugin dev |
| SRV-8 | Non-public `AddDeed` calls from RealmContracts, RealmHouses and RealmChronicle into RealmRenown, replacing RealmRenown's reads of their data files. | P2 | M | Season 2 | Plugin dev |
| SRV-9 | **Small-server balance profile.** Claim and jury minimums, cooldowns, raven chances, Sentinel limits and event prizes tuned for 10 to 30 players, shipped as a documented config set; a second profile for 60+. Based on alpha numbers. | P1 | M | M2 | Plugin dev, Play-test lead |
| SRV-10 | RealmWarden accepts external alert kinds longer than 24 characters (`impersonation_near` is cut today). | P1 | S | M2 | Plugin dev |
| SRV-11 | A player-facing `/ironbreaker` view (who holds the blade, since when), listed in the `/realm` catalogue, and removed from `STAFF_COMMANDS`. | P2 | S | Season 2 | Plugin dev |
| SRV-12 | Season 3 winter decrees: design spike for config-only scarcity (Royal Stores, tithe, market caps). A new plugin only if the spike shows config cannot do it. | P2 | M | Season 3 | Plugin dev, Saga writer |
| SRV-13 | `tools/plugin-compile-check/UnityEngine.cs`: stubs for `Quaternion`, `Color32`, `Color`, `Vector2`, `Transform`, `Collider`, `Physics`, so RealmSculptor and RealmPainter can call the game directly instead of by reflection. Only after PT2 proves the reflected paths; keep the logic tests' metadata guard. | P2 | M | Season 2 | Plugin dev |
| SRV-14 | Write every play-test result into the guide it settles, and remove the UNVERIFIED tag it settles. | P0 | S per session | M1 onward | Play-test lead |
| SRV-15 | A2S name and slots: a design spike for a plugin that sets the Steam server name and max players (`docs/join-and-scale.md` section 4, option 3), so server browsers stop showing "Another ROK Server". UNVERIFIED that the Oxide sandbox allows it at run time. | P3 | M | live year | Plugin dev |
| SRV-16 | **Partly met** by RealmArena: trial by combat is staged in its ring and duel blows are exempt from the peace law (`IsDuelBlow`); RealmEvents' truce still has no duel exemption. Event-time trial by combat: RealmLaws exposes an "in duel" call so RealmEvents' truce can exempt duels. | P2 | S | Season 2 | Plugin dev |
| SRV-17 | **Wave-4 titles as defaults.** A RealmRenown default title for `holding_taken` (for example *Marcher Lord*), the six quest titles (`plugins/docs/RealmQuests/renown-titles.json`) and *Wayfarer*, each with its badge in `art/src/titles.json` and the copies (bot, launcher, portal). Plus a config upgrade note or tool so live `RealmRenown.json` files gain the Arena, Quests and Travel titles without a paste. | P1 | M | M2 | Plugin dev, Artist |
| SRV-18 | Short labels for the new Chronicle types (`holding_taken`, `census_taken`, `vote_held`, and `blade_claimed`, `blade_lost` after the Steward merge) in `bot/src/text.js`, `streamkit/public/assets/kit.js` and `launcher/lib/shared/notify.js`; they fall back to defaults today. | P1 | S | M2 | Web maintainer |
| SRV-19 | **One treasury API.** Wave 4 added `GrantHouseIncome`, `RewardMarks`, `ChargeMarks` and the holds (`HoldMarks`, `PayFromHold`, `ReleaseHold`, `GetHold`). Design spike: one charge path, one mint path with one daily budget per source shown in `/treasury audit`, and `/purse` showing marks held in escrow. | P2 | M | Season 2 | Plugin dev |
| SRV-20 | RealmWarden skips its combat-tag and grief heuristics for duel blows (`RealmArena.IsDuelBlow`), so a duellist is not flagged for a duel. | P1 | S | M2 | Plugin dev |
| SRV-21 | **Economy budget for wave 4.** Dominion's payday (up to about 1,500 marks a day with seven holdings), Quests (up to 3,000 a day), World and Crafts rewards against `MintSupplyCap` 100,000 and the 20,000 crown reserve. Set `IncomeByKind`, `RewardMintPerDay`, `RewardScale` and the cap per season from PT7.17; part of the small-server profile (SRV-9). | P1 | M | M2 | Plugin dev, Owner |
| SRV-22 | RealmLaws non-public `ApplyReferendum` (proclaim or repeal), so the crown's law referendums bind as decree referendums do. Today they are advisory and defiance is heralded and chronicled. | P2 | S | Season 2 | Plugin dev |
| SRV-23 | Direct reports instead of file reads: RealmContracts and RealmEvents call `RealmQuests.ReportQuestEvent`; RealmTravel offers a non-public waystone lookup so RealmWorld stops reading `RealmTravel.json`; RealmTravel refuses journeys while `RealmWorld.IsCaravanBearer` (World already cancels them). | P2 | M | Season 2 | Plugin dev |
| SRV-24 | House names: decide whether a player house may take a great house's name, which would give it that house's reserved colours in RealmHeraldry; enforce the decision in RealmHouses. | P1 | S | M2 | Owner, Plugin dev |
| SRV-25 | Quest content for wave 4: weekly tasks and deeds for world events (treasure, caravan, legends), votes (`vote_cast`, `council_elected`), duels, holdings and crafting mastery, in `plugins/docs/RealmQuests/content/`. | P2 | M | Season 2 | Saga writer, Plugin dev |
| SRV-26 | **Wave-4 tuning** from play: Arena `Duels.FatalMargin` and `PreventDeathFlag` (PT3.20, PT3.21), Dominion capture times, radii, garrison and cooldowns (PT4.12), Crafts XP and `Commissions.XpWithinHouse`, Heraldry `Voters.*`, Quests' caps, World rewards. Write the numbers into each guide. | P1 | M | M2 to M3 | Plugin dev, Play-test lead |

### 3.2 Sculptures and world build-out (the capital and the house seats)

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| WLD-1 | Run the shape test in game (PT2.13) and fix `art/tools/sculptor/shapes.json` and `shapes.mjs` with the real shapes; rebuild and re-preview every sculpture. | P0 | M | M1 | Builder |
| WLD-2 | **World plan.** Map the twelve saga locations (L01-L12, [`saga/locations.md`](saga/locations.md)) onto the real map: coordinates, facing, crest zone owner, which monument and which signs go where, and a screenshot of each site. Kept as a plan file next to the sculptures. Wave 4 adds what staff mark by standing on the spot: the seven holdings (`/dominion admin move`), the waystones (`/travel admin set`), the twelve quest places (`/quest admin place set`), RealmWorld's places, caravan routes and festival decorations, and the Proving Ring and taverns (`/arena admin zone set`, `tavern set`). One plan, so a holding, its waystone and its quest place agree. | P0 | M | M1 | Builder, Saga writer |
| WLD-3 | **The capital.** A staff crest zone over the throne hill. Old Throne monument by the Old Throne (L01), Herald's Pillar at the Hearth (L02), notice boards at the Crown Market (L03) and the Tollbridge (L04), Tournament Arch at the Listing Field (L05). Seen from 10 m, 50 m and 150 m. | P1 | L | M2 | Builder |
| WLD-4 | **The six house seats** (L07-L12): crest and banner signs at each seat at 1.0, monuments as houses earn them from Season 2 (EVT-6). Six house monuments already exist (`art/sculptures/house-*.json`). | P1 | L | M3 | Builder |
| WLD-5 | Waymarks: Herald's Pillar variants and direction signs on the roads between the capital and the seats. | P2 | M | M4 | Builder, Artist |
| WLD-6 | Sculpture review rule: every new monument is previewed from four sides, placed on the test world first, and seen in game at three distances before it reaches the live world. | P1 | S | M2 | Art director |
| WLD-7 | **Layout manifest.** One command per plugin that exports every placed sculpture and bound sign with position and facing, and one that re-places them on a new world, so a wipe rebuilds the capital in one evening. Needs RealmSculptor and RealmPainter work. | P1 | L | M3 | Plugin dev |
| WLD-8 | Community monuments for Season 4: a submission guide (original designs, or CC0 sources through `cli.mjs voxelize` with `--license` and `--source`), a vote on the portal or Discord, staff placement. | P2 | L | Season 4 | Builder, Community manager |
| WLD-9 | Season sculpture sets: Ironbreaker monument at the Listing Field (Season 2), beacon towers (Season 3), the rebuilt capital (Season 4). | P2 | L each | live year | Builder |
| WLD-10 | Network budget for big builds: settle `BlocksPerTick` from PT7.9 and write the rule "house monuments only with fewer than N players online" into the builder guide. | P1 | S | M3 | Builder |
| WLD-11 | Holding banners and waystone markers: RealmSculptor pieces or sign art at each holding and waystone (the guides suggest `heralds-pillar` for the capital and `house-<name>` at the seats), and RealmDominion holdings raising or retiring their waystones once both are proven. | P2 | M | M4 | Builder, Plugin dev |

### 3.3 Sign art

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| SGN-1 | **On `team/steward-integration`** (Steward part). Deploy carries `art/paintings/RealmPainterArt.json` (with STW-1). | P0 | S | M0 | Launcher dev |
| SGN-2 | Which placeables are paintable, and the real face of each: record `DefaultFace` and `FacesByName` from PT2.2 and PT2.6 into the RealmPainter guide and config. | P0 | S | M1 | Builder |
| SGN-3 | Tune redraw limits and `BoardTexture` from PT7.8. | P1 | S | M3 | Plugin dev |
| SGN-4 | Capital boards: the Chronicle at the Hearth, wanted posters at the Market and the Tollbridge, standings at the Listing Field, the proclamation by the throne, the event poster at the Hearth. | P1 | M | M2 | Builder |
| SGN-5 | Seat signs: crest and banner signs at each house seat (with WLD-4). | P1 | M | M3 | Builder |
| SGN-6 | A capital map sign and direction signs (new paintings in `art/tools/painter`). | P2 | M | M4 | Artist |
| SGN-7 | Season sign sets: a poster set and season medal per season, rendered by the painter tool and previewed in the guide. | P2 | M each | live year | Artist |
| SGN-8 | **Half done:** the painter tests run in CI; `paint.mjs check` does not yet. CI runs `node --test art/tools/painter/test/*.test.mjs` and `node art/tools/painter/paint.mjs check` (with QA-1). | P0 | S | M0 | QA lead |
| SGN-9 | New live boards: the arena ladder, a roads board of waystones, a guild board (`RealmCrafts.GetMasterCrafter`, `GetWorkshopTier`), a ballot board (`RealmHeraldry.GetBallotSummary`), a house-goal board (`RealmQuests.GetHouseGoalText`), and a preview PNG of the dominion board. | P3 | M | live year | Plugin dev, Artist |
| SGN-10 | Per-holding sign paintings under `art/paintings/dominion/`, once RealmPainter is proven in game. | P3 | S | live year | Artist |

### 3.4 Events

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| EVT-1 | Set prize items in `oxide/config/RealmEvents.json` and Royal Stores' item to the real names from PT1.5. | P0 | S | M1 | Owner |
| EVT-2 | One event calendar: Steward, the portal, the bot and the stream scenes read the schedule from RealmEvents (`GetNextEvent`, `RealmEvents.json`) instead of keeping their own copies (`streamkit` `schedule.json`). | P2 | M | M4 | Web maintainer |
| EVT-3 | Dry-run Season 1 Acts I and II run-sheets during the beta; fix every line that does not match what the plugins do. | P1 | M | M3 | Event host, Saga writer |
| EVT-4 | Event times that suit the community's time zones; rebellion windows set in `CrownAndConsequences.json` (`RebellionWindows`, `UtcOffsetHours`). | P1 | S | M2 | Owner |
| EVT-5 | Big-moment checklist: each of coronation, rebellion start and end, tournament champion, blade claimed: herald, popup, Chronicle, overlay moment, Discord, board. One play-test row each (PT5). | P1 | S | M2 | Play-test lead |
| EVT-6 | Seat monuments earned: when a house tops a fortnight's standings, staff (later a plugin call from RealmSeasons to RealmSculptor) raise its monument at its seat, with a herald line. | P2 | M | Season 2 | Plugin dev, Builder |
| EVT-7 | Season 2 events: the Grand Melee (a tournament final with the blade as prize) and the Bearer's Challenge. Config of RealmEvents and RealmLegendary first; new code only if needed. | P2 | M | Season 2 | Plugin dev, Event host |
| EVT-8 | Season 3 events: weekly Hearth Truce, the Frost Hunt, Raven Night. | P2 | M | Season 3 | Event host, Plugin dev |
| EVT-9 | Saga packs for Seasons 2, 3 and 4 (storyline, run-sheets, proclamations, calendar), each passing `check-saga.mjs`. | P2 | L each | Interregnum before each season | Saga writer |

### 3.5 Anti-cheat tuning (RealmSentinel)

RealmSentinel uses only Oxide hooks and the game's own server-side types. Tuning never touches the game's own anti-cheat.

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| SEN-1 | **Done differently:** CI runs `tools/exploit-review/sentinel/run.sh` as its own step; `run.sh` still has no `sentinel` case. Add the `sentinel` suite to `tools/exploit-review/run.sh` and a row to its README (it runs `tools/exploit-review/sentinel/Tests.cs` with the Sentinel mocks). | P0 | S | M0 | QA lead |
| SEN-2 | Run the 21 Sentinel steps in watch mode (PT1.16, PT3.13 to PT3.16, PT7.10). | P0 | M | M1 to M2 | Play-test lead |
| SEN-3 | One week of watch mode in the alpha and two in the beta. Record `/sentinel peaks`; set every limit comfortably above the honest peaks; write the numbers into the guide. | P1 | M | M2 to M3 | Plugin dev |
| SEN-4 | Switch to `"Mode": "enforce"` after 2 beta weeks with 0 alerts at the freeze score on honest players. Keep AutoBan off for 1.0. | P1 | S | M3 | Owner |
| SEN-5 | **On `team/steward-integration`** (the Sentinel screen with Kick and Ban; staff commands copied for pasting in game). Steward shows `oxide/data/RealmSentinelFeed.json` in the Court (schema in the Sentinel guide), and `launcher/lib/moderation.js` lists the `/sentinel` admin commands. | P1 | M | M3 | Launcher dev |
| SEN-6 | Privacy notice and data-deletion process mention Sentinel's evidence (Steam IDs, names, positions, ping; no IP addresses; daily logs). | P1 | S | M3 | Legal reader |
| SEN-7 | Appeals: a Sentinel-based action is appealable under [`ban-appeals.md`](community/ops/ban-appeals.md), and the evidence lines are what the appeal reviews. | P1 | S | M3 | Moderator lead |
| SEN-8 | Re-tune after every game-mode change that changes movement or damage (moods do not; new events might). | P2 | S each | live year | Plugin dev |

### 3.6 Player launcher (Realm)

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| PLA-1 | After merging `team/player-launcher`: drop the allegiance check from `launcher/scripts/design-screens.mjs`, keep only the Steward part of `onboarding-screens.mjs`, fix the links in `launcher/README.md`, `docs/community/trailer-script.md` and `launcher/renderer/assets/README.md`. | P0 | S | M0 | Launcher dev |
| PLA-2 | Windows proof of Play, Install, Update, portable and large downloads (PT5.6 to PT5.12). | P0 | M | M2 | Owner, Launcher dev |
| PLA-3 | The forms are **on `team/steward-integration`**; hosting the first signed feeds is open. Publishing news and updates from Steward (STW-4); the first signed `news.json` and `update.json` hosted next to `servers.json`. | P1 | M | M3 | Launcher dev |
| PLA-4 | Installer trust: decide on Authenticode signing (cost, who holds the certificate). If not signed for 1.0, the download page shows the SmartScreen path with a screenshot. | P1 | M | M4 | Owner |
| PLA-5 | `launcher/renderer/heraldry.js` reads or is checked against `chronicle/public/assets/realm-art.js`, so the event, title and house tables cannot drift. | P1 | S | M3 | Launcher dev |
| PLA-6 | News content rhythm: one news item per event night and one per season beat, written from the saga pack. | P2 | S each | live year | Community manager |
| PLA-7 | Accessibility pass on the one-screen player app with a screen reader on Windows (Narrator). | P2 | S | M4 | Launcher dev |

### 3.7 Realm Steward (owner app)

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| STW-1 | **Steward part on `team/steward-integration`** (with backup, atomic writes and plugin reloads; the installer packs the files); `server/Deploy-Plugins.ps1` is open and should also copy the mood folders. **Deploy data files.** `launcher/lib/realm.js` `deployPlugins` and `server/Deploy-Plugins.ps1` also copy `art/sculptures/*.json` to `oxide\data\RealmSculptor\` and `art/paintings/RealmPainterArt.json` to `oxide\data\`, and `plugins/docs/RealmQuests/content/*.json` to `oxide\data\RealmQuests\`, and the release packaging carries them. Steward shows their versions. | P0 | M | M0 | Launcher dev |
| STW-2 | **On `team/steward-integration`**, extended to every wave-4 plugin's staff commands with argument checks. `launcher/lib/moderation.js`: admin commands for RealmLaws (`/court admin`, `/law zone`), `/paint redraw all`, `/paint status`, `/paint signs`, `/sculpt placed`, `/sculpt status`, `/ironbreaker status`, `/sentinel status`, `/sentinel report`. | P1 | S | M2 | Launcher dev |
| STW-3 | **On `team/steward-integration`**. Sentinel feed in the Court (SEN-5). | P1 | M | M3 | Launcher dev |
| STW-4 | **On `team/steward-integration`** (`publish-feeds.js`). "Publish news" and "Publish update" forms calling `lib/news.js` (`buildNews`, `signNews`) and `lib/updater.js` (`buildUpdate`, `signUpdate`) with the stored key; the update form reads size and SHA-256 from the release folder. | P1 | M | M3 | Launcher dev |
| STW-5 | Realm health panel: each plugin loaded or not, each plugin's damaged-file state, Chronicle freshness, frame time or CPU, players online. | P2 | L | M4 | Launcher dev |
| STW-6 | Prove worlds, pre-start, Stop it cleanly and Adopt it on Windows (PT0.4 to PT0.7) and fix what differs from the simulator. | P0 | M | M1 | Owner, Launcher dev |
| STW-7 | Multi-server: prove two servers side by side (PT7.7) before offering Server II to anyone. | P2 | M | live year | Owner |
| STW-8 | Clean-slate tool for 1.0: archive and reset the beta's season and legends files on purpose, with a confirmation (supports OPS-9). | P1 | S | M4 | Launcher dev |
| STW-9 | Steward applies the right mood at the planned restart before a Blood Moon or festival (`RealmWorld.GetNextWorldEvent`, `GetFestivalName`), instead of a manual `Set-Mood.ps1 -Event` step. | P2 | M | Season 2 | Launcher dev |
| STW-10 | RealmSentinel server-console commands (freeze, unfreeze, clear, report) like RealmCourt's `/realm.save`, so the Sentinel screen acts in one click instead of copying a command to paste in game. | P2 | M | M4 | Plugin dev, Launcher dev |

### 3.8 Web and broadcast (portal, Chronicle pages, overlay, stream scenes, bot)

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| WEB-1 | **Merged 2026-10-03.** Merge `team/web-and-broadcast`; afterwards every change in `art/` runs `node portal/scripts/sync-art.mjs`. Update `art/README.md`'s "What has not been seen working". | P0 | S | M0 | Web maintainer |
| WEB-2 | Choose and set up portal hosting (static host with https), set `siteUrl`, publish the privacy notice and rules there. | P1 | M | M3 | Web maintainer |
| WEB-3 | Remote overlay for creators: decide whether the Chronicle service is published behind a reverse proxy, and if so set it up and test it before the creator night (launch plan Phase 2). | P1 | M | M3 | Broadcast producer, Ops |
| WEB-4 | OBS, Discord and phone proof (PT6). | P1 | M | M3 | Broadcast producer |
| WEB-5 | The bot runs as a service on the server PC (Task Scheduler) with the Attach Files permission in its status channel. | P1 | S | M3 | Ops |
| WEB-6 | The portal reads RealmSeasons' real `ScoreWeights` from its config, so its numbers match `/season standings`. | P2 | S | M4 | Web maintainer |
| WEB-7 | The year book: a season archive per season and a Hall of Kings year page on the portal. | P2 | M | Season 4 | Web maintainer |
| WEB-8 | Remove the leftover `streamkit/public/assets/favicon.svg` and its test. | P3 | S | any | Web maintainer |
| WEB-9 | **Holdings on the web.** The portal and the Chronicle read `oxide/data/RealmDominionMap.json` (schema in the Dominion guide): a holdings map page and an `/api/state` section; the bot adds `/realm holdings`. | P2 | M | M4 | Web maintainer |
| WEB-10 | Achievements and house colours outside the game: a public data file from RealmQuests for the portal and the player app's news, and house colours from `RealmHeraldry.json` (`GetHouseArms`) on the portal. | P3 | M | live year | Web maintainer, Plugin dev |

### 3.9 Ops

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| OPS-1 | Re-run Go Public > Add firewall rules (4 rules, with the TCP 11000-11003 block); forward UDP 7350, TCP 7350, UDP 27015. | P0 | S | M2 | Owner |
| OPS-2 | Hosting decision for 1.0: the owner's PC or a rented Windows box, from PT7 numbers (CPU, RAM, upload bandwidth per player). | P1 | M | M3 | Owner, Ops |
| OPS-3 | Scheduled backups with offsite copies (`ops/`), and a monthly restore drill. | P1 | M | M3 | Ops |
| OPS-4 | Uptime monitor posting to the staff Discord channel. | P1 | S | M3 | Ops |
| OPS-5 | **Game-update runbook.** A Steam update to the dedicated server overwrites the Oxide-patched DLL: stop, update the master copy, refresh the test copy, reinstall Oxide 2.0.3867, run the compile check and a short smoke test before opening. Covered in [`ops/disaster-recovery.md`](../ops/disaster-recovery.md); rehearse it once. | P1 | S | M3 | Ops |
| OPS-6 | Load test: 20, then 50 players; 120 only if 50 is comfortable (PT7.11 to PT7.13). Set Max players from the result. | P1 | L | M3 | Ops, Play-test lead |
| OPS-7 | A deputy with the break-glass notes: how to restart, restore, ban and roll back Oxide without the owner. | P1 | S | M3 | Owner |
| OPS-8 | Server PC hygiene: Windows Update schedule outside play hours, power settings, automatic sign-in decision, Steward starting the servers after a reboot (PT7.3). | P1 | S | M3 | Ops |
| OPS-9 | Clean slate for 1.0: back up the beta world and data, archive `RealmLegends.json` and `RealmSeasons.json` with the beta's season, start a fresh world, check `/season` says Season 1. | P0 | S | M4 | Owner |

### 3.10 Community, staff and legal

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| COM-1 | House claim sign-up for the six great houses before each phase ([`lore.md`](community/lore.md)). | P0 | S | M2, M3, M4 | Community manager |
| COM-2 | Discord structure (announcements, news, Chronicle feed, appeals, staff), the herald webhook and the bot live. | P1 | M | M2 | Community manager |
| COM-3 | Recruit and train staff: 2 to 4 admins, 2 to 6 moderators, event hosts. Everyone reads the moderation handbook and the staff roles page; grants follow the matrix. | P1 | L | M3 | Owner, Moderator lead |
| COM-4 | Creator night and creator kit: `streamer-kit.md`, `creator-code-of-conduct.md`, a brief for each creator. | P1 | M | M3 | Community manager, Broadcast producer |
| COM-5 | Trailer: refresh `trailer-script.md` with the new screens, capture in game footage of the capital, signs and monuments. | P1 | L | M4 | Broadcast producer |
| COM-6 | **Read the EULA** and the Steam terms; fill every clause row in the EULA checklist; decide on the rights-holder letter. Blocks going public at 1.0. | P0 | M | M3 | Owner, Legal reader |
| COM-7 | Publish the privacy notice and the data-deletion process; add Sentinel's data (SEN-6). | P1 | S | M3 | Legal reader |
| COM-8 | Feedback loop: a "couldn't join?" form, a bug channel, a weekly triage into this backlog. | P1 | S | M2 | Play-test lead |
| COM-9 | No paid perks of any kind until COM-6 is done and [`monetisation-guardrails.md`](legal/monetisation-guardrails.md) is followed. | P0 | — | always | Owner |
| COM-10 | Privacy notice and deletion process cover the wave-4 data files: `RealmArena.json` (ratings, results, wagers, pairs), `RealmQuests.json` (progress; addresses only as a salted hash in memory), `RealmCrafts.json`, `RealmTravel.json`, `RealmDominion.json`, `RealmWorld.json` and `RealmHeraldry.json` (play time, votes until the count, deposits); `find-player-data.mjs` finds them. | P1 | S | M3 | Legal reader |

### 3.11 Quality and CI (across all areas)

| ID | Item | Pri | Size | When | Role |
|---|---|---|---|---|---|
| QA-1 | **Mostly done:** CI runs the sculptor and painter tests and `cli.mjs check`; `paint.mjs check` is open (SGN-8). CI runs `node --test art/tools/sculptor/test/*.test.mjs`, `node art/tools/sculptor/cli.mjs check`, `node --test art/tools/painter/test/*.test.mjs` and `node art/tools/painter/paint.mjs check`; `art/tools/package.json` `test` runs them too. The plugin logic-test loop already picks up new `plugins/docs/*/logic-tests/run.sh`. | P0 | S | M0 | QA lead |
| QA-2 | Play-test results log: each session's sheet (section 5) is copied, dated and filled in, and each result goes into its guide (SRV-14). | P0 | S | M1 | Play-test lead |
| QA-3 | Screenshot review after every UI change: `launcher` player and Steward scripts, `portal/scripts/screens.mjs`, `streamkit/tools/screenshots.mjs`, `chronicle-shots.mjs`. | P1 | S each | always | Launcher dev, Web maintainer |
| QA-4 | A regression rule: every bug found in a play-test gets a mock test in its plugin's logic tests or an exploit-review case before the fix is merged. | P1 | — | M1 onward | Plugin dev |
| QA-5 | CI and tooling after wave 4: `paint.mjs check` in CI (SGN-8); `mods/presets/tests/Test-SetMood.ps1` for the `blood_moon`, `harvest_festival` and `midwinter` keys under PowerShell 7; the art build's `svgo` found through `ART_NODE_MODULES`; the Steward screenshot run fetches the Electron binary (`node node_modules/electron/install.js`). | P1 | S | M0 | QA lead |

---

## 4. Order of work up to 1.0

1. **M0**: done: SRV-1, SRV-2, WEB-1, and the wave-3 and seven wave-4 merges. Open: merge `team/steward-integration` (which brings STW-1's Steward part, STW-2, STW-3, STW-4, SEN-5, SGN-1 and SRV-6), then the rest of STW-1 (`Deploy-Plugins.ps1`), SGN-8, QA-5, PLA-1 and SEN-1's `run.sh` case. Then update the docs (HANDOFF, systems map, commands) again.
2. **M1**: PT0 to PT3 (with the wave-4 rows: PT0.13 to PT0.16, PT1.22 to PT1.32, PT2.17 to PT2.19, PT3.20 to PT3.28), with WLD-1, WLD-2 (now including the wave-4 places), SGN-2, EVT-1, STW-6, SRV-14, QA-2 alongside.
3. **M2**: OPS-1, PT4 and PT5, COM-1, COM-2, COM-8, SRV-3, SRV-4, SRV-5, SRV-9, SRV-10, SRV-17, SRV-18, SRV-20, SRV-21, SRV-24, SRV-26, EVT-4, EVT-5, WLD-3, SGN-4, SEN-3.
4. **M3**: PT6 and PT7, COM-3, COM-4, COM-6, COM-7, COM-10, OPS-2 to OPS-8, SEN-4, SEN-6, SEN-7, PLA-3 (hosting), PLA-5, WEB-2 to WEB-5, WLD-4, WLD-7, WLD-10, SGN-3, SGN-5, SRV-7, EVT-3.
5. **M4**: PLA-4, STW-8, OPS-9, COM-5, the remaining P1 items, launch.

---

## 5. Play-test checklist: every UNVERIFIED item, in order

This is every UNVERIFIED item in the repo as of 2026-10-04, as steps on the real server, in the order the owner should run them. The order goes from what one person can do alone to what needs friends, the public internet, broadcast tools and a crowd. Each session assumes the one before it passed.

The **wave-4 rows** (RealmDominion, RealmQuests, RealmArena, RealmTravel, RealmCrafts, RealmWorld, RealmHeraldry and Realm Steward's integration) are folded into the same sessions, after the wave-3 rows of each session and marked *(wave 4)*. They take the next free numbers so older references stay valid; in PT1 they sit before PT1.21, which ends the session by wiping the season. Run them in the order they appear.

How to use it:

- Work on `G:\RealmTest\server` (or a copy). **Back up before every session** (Steward: Servers > Backup).
- Each row says what to do and what a pass looks like. **Settles** names the UNVERIFIED item and the guide that has the full step. When a row needs a config change for the test, put the default back afterwards.
- Record the result in the guide the row names (SRV-14). A failure is a result too: write what you saw and switch on the fallback the guide gives.
- Guides from the wave-3 merges: [`RealmSculptor.md`](../plugins/docs/RealmSculptor.md), [`RealmPainter.md`](../plugins/docs/RealmPainter.md), [`RealmLegendary.md`](../plugins/docs/RealmLegendary.md), [`RealmSentinel.md`](../plugins/docs/RealmSentinel.md), and the player app's [`player-launcher.md`](player-launcher.md). From wave 4: [`RealmDominion.md`](../plugins/docs/RealmDominion.md) (steps D1 to D12), [`RealmQuests.md`](../plugins/docs/RealmQuests.md) (QS1 to QS18), [`RealmArena.md`](../plugins/docs/RealmArena.md) (U1 to U19), [`RealmTravel.md`](../plugins/docs/RealmTravel.md) (T1 to T14), [`RealmCrafts.md`](../plugins/docs/RealmCrafts.md) (C1 to C16), [`RealmWorld.md`](../plugins/docs/RealmWorld.md) (W1 to W15), [`RealmHeraldry.md`](../plugins/docs/RealmHeraldry.md) (HR1 to HR11), and Steward's tests F1, F2, S1, S2, D1, C1, P1 in `launcher/README.md` ("Not verified") on `team/steward-integration`. Step letters repeat between guides (RealmDominion D1 is not RealmDynasties D1 or Steward D1), so every row names its guide.
- Wave-4 set-up for a test session: grant yourself the seven new admin permissions (`realmdominion.admin`, `realmquests.admin`, `realmarena.admin`, `realmtravel.admin`, `realmcrafts.admin`, `realmworld.admin`, `realmheraldry.admin`; [`staff-roles-and-permissions.md`](community/ops/staff-roles-and-permissions.md)) and lower the minimums the guides name for small tests (house age and size in RealmDominion, play-time minimums in RealmArena and RealmQuests, `Voters.*` in RealmHeraldry). Put the defaults back afterwards: an admin who holds these permissions does not compete (RealmWorld pays them nothing while `General.AdminsCanWin` is false).
- Abbreviations: **Smoke** = [`smoke-test.md`](smoke-test.md); **GP** = [`going-public.md`](going-public.md) section 5; plugin names refer to `plugins/docs/<Plugin>.md` and their numbered smoke or first-test steps; "U3" means item 3 of that guide's "What is UNVERIFIED" list, except in RealmArena, whose U1 to U19 are its own numbered test rows.

### PT0. Build, install and run (the owner alone, no game client needed until PT0.11)

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT0.1 | On the owner's PC, double-click `Build-Realm.bat` in a fresh clone on G:. | Steps `[1/5]` to `[5/5]`, **Done**, and `release\1.0.0\` holds both installers and `SHA256SUMS.txt` whose hashes match the files. | `START-HERE.md`, `troubleshooting.md`: Build-Realm.bat on real Windows |
| PT0.2 | Run `Realm-Steward-Setup-1.0.0.exe` over the old Steward. Note SmartScreen's exact text. | Installs per user with no administrator prompt; settings and the signing key are kept; the desktop shortcut opens 1.0.0. | `START-HERE.md` step 2 |
| PT0.3 | In Steward, Update plugins. Then (until STW-1 lands; with `team/steward-integration` merged, PT0.13 replaces the hand copies) run `node art/tools/sculptor/cli.mjs export G:\RealmTest\server\oxide\data`, copy `art/paintings/RealmPainterArt.json` to `oxide\data\` and `plugins/docs/RealmQuests/content/*.json` to `oxide\data\RealmQuests\`. | `oxide\plugins` holds 26 `.cs` files; `oxide\data\RealmSculptor\` holds 23 sculptures; `RealmPainterArt.json` and the six quest content files are there. | Deploy path for the new plugins |
| PT0.4 | Start Server I from Steward with nothing else running. | Ready only after `Server for N players started on port 7350.` then `Game has started.`; Steward remembers this world; the next start loads the same world with no new `Saves\Slot` folder. | `worlds.md`: world memory, pin, choice on the real server; "empty slot" size rule |
| PT0.5 | Stop the server. Start `ROK.exe` by hand from the server folder (or leave a stray one running), then press Start in Steward. Try **Stop it cleanly**; repeat and try **Adopt it**. | Steward names the leftover correctly, stops it cleanly the first time, adopts it the second time with the console working. | `worlds.md`: pre-start classification, Stop it cleanly, Adopt it on Windows |
| PT0.6 | Start `Server.exe` once as administrator, let it start `ROK.exe`, then send `/shutdown` from Steward's console. Watch Task Manager for 10 s. | Write down whether `ROK.exe` comes back. If it does, Steward's pre-start check catches it on the next Start. | `worlds.md`: Server.exe relaunch after a clean `/shutdown`; `Session.lock` window |
| PT0.7 | Look for `Admin console enabled.` in the game log. Type `/list` in Steward's console. Close Steward while the server runs. | The log line is there; `/list` answers in the console; closing Steward saves and stops the server (the world is intact on the next start). | `admin-console.md`: admin console active in the dedicated scene; `join-and-scale.md` section 5 |
| PT0.8 | In Court: list players (`/realm.players`), Save world (`/realm.save`), **Say** a line, send a **Notice** and a **Popup**, kick a test name with a two-word reason. | Each answers. `/realm.players` and `/realm.save` work from RealmCourt. The reason arrives as one argument. | RealmCourt at run time; `saga/README.md` Say, Notice, Popup; `admin-console.md` kick matching |
| PT0.9 | Task Manager > Details: add the Platform column; note `ROK.exe`'s platform and memory at idle. | Written down. A 32-bit process caps RAM near 4 GB; note it for OPS-2. | `ops/hosting-guide.md`: 32 or 64 bit |
| PT0.10 | Check where Oxide made its folder: `oxide\` in the server root or `Saves\oxide\`. Start `node chronicle\server.js --data <that>\data`; open `/api/state`, `/api/events?since=0`, `/overlay`, `/realm`. | One `oxide\` folder with `plugins`, `config`, `data`, `lang`, `logs`; the Chronicle pages show the realm. | Smoke B4, C5; `RealmStats.md` data paths; Oxide data path in the launch plan |
| PT0.11 | `oxide.plugins` in the console. Read `oxide\logs` for the start. Note any outbound web request Oxide makes. | All 26 plugins listed, no compile error, no repeated error. Requests written down. | Smoke B6, C1; every plugin's "loads" step |
| PT0.12 | Join, place one block, `/realm.save`, restart, rejoin. Then Steward > Backup, and look inside the zip. | The block is still there. The zip holds `Saves\`. No new firewall rule appeared before Go Public. | Smoke A7, A8, A10 on the Steward path |
| PT0.13 | *(wave 4, after `team/steward-integration` is merged)* In Steward, Update plugins on a clean `oxide\data`; change one byte of a deployed sculpture and Update again. | The sculptures, `RealmPainterArt.json` and the RealmQuests content arrive; a changed file is backed up to `_realm-backups\data-<time>\` first; damaged source files are refused; the affected plugins reload. | Steward D1 (`launcher/README.md`); STW-1 |
| PT0.14 | *(wave 4)* In game as admin: `/dominion admin status`, `/quest admin status`, `/arena admin status`, `/travel admin status`, `/kit admin check`, `/craft admin status`, `/world admin status`, `/heraldry status`. | Each answers. Quests: every content file `ok`, no unknown item. Kits, Crafts and World: no "is not known" item names, or a list to fix (write the real names down). Crafts: "Container events subscribed: yes". Dominion: "7 holdings are not marked on the land yet". Heraldry: `Colour binding: ok`. Each lists its partner plugins as loaded. | RealmDominion D1; RealmQuests QS1, QS2; RealmTravel T1 (U6); RealmCrafts C1; RealmWorld W1; RealmHeraldry HR1 |
| PT0.15 | *(wave 4, Steward)* On Steward's Realm features screen, turn RealmEvents "Truce of the Realm" off with the live console open; then restart the server. | The console shows `Unloaded plugin RealmEvents` then `Loaded plugin RealmEvents`; `oxide\config\RealmEvents.json` has `"EnableTruce": false`, and still has it after the restart. Turn it back on. | Steward F1, F2 |
| PT0.16 | *(wave 4, Steward)* From the Court's staff-command folds, copy `/arena admin status`, `/quest admin status` and `/world admin status`; paste each in game as admin. | Each answers as typed. | Steward C1; STW-2 |

### PT1. One player in game: foundations, chat, items and the crown

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT1.1 | Join at `127.0.0.1` / `7350` with an account the server has never seen (or reset it: `/realm admin reset <you>`). | Within about 20 s: the "Welcome to Ostreval" window and four welcome lines. Note line breaks, length and anything cut off. | RealmHerald 1, 5; Herald U2 (`ShowPopup` from a dedicated server, `\n`, length) |
| PT1.2 | `/realm`, close the window, `/realm list`, `/realm popups off`, `/realm`, `/realm popups on`. Then `/season`, `/house list`, `/crown`, `/events`. | Hub window, then 9 lines; chat-only hub with popups off; every command answers. | RealmHerald 2, 3; `HANDOFF.md` next step 1 |
| PT1.3 | Read several plugins' replies in chat, including an error and a herald broadcast. | Tone colours and the command colour read clearly on the game's chat background; no stray `[RRGGBB]` text. | Herald U4 (chat colours); `PlayerExtensions.SendMessage` colours (RealmRavens U4) |
| PT1.4 | Wait a minute for "Your next step". Join or found a house; type `/crown`. | "Step 1 of 3 done"; step 2 is marked by `/crown`. If not, `/realm crown` marks it. | RealmHerald 4; Herald U3 (`OnPlayerCommand` sees Oxide commands) |
| PT1.5 | `/contract items wood` and `/contract items stone`. | Real item names listed; write them down. Set RealmEvents prizes and Royal Stores' `Item` to them (EVT-1). | Smoke C10; RealmEvents U5 |
| PT1.6 | Escrow: note your Stone, `/contract post delivery 1 "Wood" 5 "Stone"`, then `/contract cancel <id>`. Twice. Then post again, `oxide.reload RealmContracts`, cancel; then again across a full restart. | Stone drops by 5 **at once** and comes back to the exact count each time; the contract survives reload and restart. | Smoke C11, C14; item sync to the client (`oxide-rok-api.md` 3.9), shared by RealmTreasury U3, RealmLaws U5, RealmEvents U4 |
| PT1.7 | As admin, post and cancel bounties until more than `ChronicleMaxPerHour` lines. | Extra contract lines go only to the server log. | Smoke C19; RealmChronicle flood budget |
| PT1.8 | Take the empty Old Throne. `/crown`; open `/overlay`. | `/crown` names you and the overlay shows the same monarch. | Smoke C9; `OnThroneCaptured` State (`oxide-rok-api.md` section 10 item 4); RealmEvents U2; Warden U4 throne part |
| PT1.9 | As monarch, with your house crown-sworn: `/decree stores`. Wait 15 minutes. | Each online member gets 25 of the item, again after 15 min; the end line totals it. | Smoke C12 |
| PT1.10 | Hold the throne: `/season standings` after 10 min; with `ReignDayHours: 1` in RealmRenown, wait an hour; after 31 min `/dynasty info <line>` (found a line first). Then leave the throne; `/season hall`. | Crown days grow; "A day upon the Old Throne (+40 renown)"; reigns 1 and growing hours; the reign ends with "left the throne". | Smoke E2, E3; RealmSeasons U1 (`OnThroneReleased` timing); RealmRenown 3; RealmDynasties D12; Dynasties U1 (crown poll timing) |
| PT1.11 | Seasons and events alone: Smoke E1 and E11 (a schedule entry 65 min ahead; reload during the event). | `/season` shows the season; countdown heralds at 60, 30, 10, 5, 1 min, each once; no double start after reload. | Smoke E1, E11 |
| PT1.12 | **Damaged-file guard, all at once.** Stop the server. Put `{ broken` into `RealmLaws.json`, `RealmRavens.json`, `RealmRenown.json`, `RealmTreasury.json`, `RealmWarden.json`, `RealmHerald.json`, `RealmSculptor.json`, `RealmPainter.json`, `RealmLegendary.json` and `RealmSentinel.json`; truncate `RealmDynasties.json` to half; add a stray `{` to `RealmStats\state.json`; make `RealmChronicle.json` 0 bytes. Start, check, save, stop. Restore the good files. | Each plugin refuses or pauses with its own message; after a save each damaged file is unchanged; the Chronicle treats the empty file as empty. | RealmLaws L10, RealmRavens R16, RealmRenown 12, RealmTreasury T12, RealmWarden 10, RealmDynasties D11, RealmStats 13, the damaged-file rule in the RealmHerald, RealmSculptor, RealmPainter, RealmLegendary and RealmSentinel guides; RealmChronicle U2 (`ReadObject` on an empty file) |
| PT1.13 | **Data round trip.** After a normal save, open each plugin's data file, then `oxide.reload` each plugin. | Each reloads with its data; field names match the formats in the guides. | Newtonsoft round trip: RealmLaws U10, RealmTreasury U5, RealmStats U8 |
| PT1.14 | Treasury alone: T1, T2, T4 (grant to yourself), T5, T8, T11, T13. | As in the guide: tax percent matches the throne UI; the sell takes exactly 20 at once; hotbar items do not count; audit balances; ledger file written. | RealmTreasury T1-T2, T4-T5, T8, T11, T13; Treasury U3, U4 |
| PT1.15 | Laws alone: L1, L2, L3, L8. | Help, zone saved with your x/z, proclamation herald and Chronicle line, curfew warning then record. | RealmLaws L1-L3, L8; Laws U6 (positions), U7 (curfew clock) |
| PT1.16 | **Sentinel alone, watch mode:** steps 1 (sprint; then MaxSpeed 3), 2 (ping vs `/ping`), 3 (console `/tp` of yourself), 4 (respawn at a far bed), 5 (tallest staircase; then MaxRiseMeters 5), 9 (gather; then JumpAmount 20), 12 (craft arrows), 13 (console `give Stone 2000 <you>`), 14 (`/house list` 20 times), 18 (the game's fly permission), 20 (logs and feed). | Each as the guide says; no finding where none is expected. | RealmSentinel U1-U5, U9, U12-U14, U18, U20 |
| PT1.17 | Stats alone: steps 1-6, 7 (fall and hunger), 8, 9, 10 (end `ROK.exe` in Task Manager, start again), 11, 12. | As in the guide; record which cause each death produced. | RealmStats 1-12; Stats U2 (`DamageType` per death), U3 (one `OnEntityDeath` per death), U4 (crypto on the game's Mono), U5 |
| PT1.18 | Dynasties alone: D1-D4, D18, D19. | As in the guide. | RealmDynasties D1-D4, D18-D19; Dynasties U3, U4 |
| PT1.19 | Ravens and Renown alone: RealmRavens R1, R2; RealmRenown 1, 2, 10, 11. | As in the guides. | RealmRavens R1-R2; RealmRenown 1-2, 10-11 |
| PT1.20 | Moods: stop the server; `Apply-Preset.ps1 -WhatIf` for Grim but Readable, then apply, start, look; revert. Then `Set-Mood.ps1` with Long Winter, Blood Moon, Golden Summer, Storm Season and Ashfall, one at a time, `-Return` between. | Smoke D1-D6 pass; write down the Mods file name and real defaults; each mood is visibly different and nights stay readable. | Smoke D1-D6; `mods/README.md` "Still UNVERIFIED" (file name, headless keys, defaults, locale, weather length, build match) |
| PT1.22 | *(wave 4)* **Mark the places on the test world** (as admin, standing on each spot): two waystones about 500 m apart (`/travel admin set crossing landmark The Crossing`, then `set mill landmark The Mill`); the Tollbridge holding at the first (`/dominion admin move tollbridge 20`); the Hearth quest place there (`/quest admin place set the_hearth 40`); two world places and a route (`/world admin place set ford 30 The Ford`, `place set mill 30 The Mill`, `route add r1 ford mill`); an arena and a tavern (`/arena admin zone set Proving Ring 20`, `tavern set Hearth Inn 10`). Then `/dominion here` inside the holding and 25 m out; `/travel admin status`. | Each plugin's status lists its places with your position; `/world schedule` shows the eight default slots in UTC; "You stand in The Tollbridge", then "You stand in no holding"; the throne point reads `(game)` with a position, or set it with `/travel admin throne`. | RealmDominion D2; RealmTravel T2 (U9); RealmQuests QS7 (set-up); RealmWorld W2; RealmArena set-up; WLD-2 on the test world |
| PT1.23 | *(wave 4)* **The new windows:** `/dominion`, `/quest`, `/quest story`, `/achievements`, `/world`, `/heraldry colours`; walk into the second waystone's radius. Then `/realm popups off` and repeat. | Each window shows (note its size, its line breaks, what is cut off); "Waystone found" opens on discovery; with popups off only chat, and the chat lines come either way. | RealmDominion D3; RealmQuests QS3; RealmTravel T3 (U10); popup rules in `realm-commands.md` |
| PT1.24 | *(wave 4)* **Holdings alone** (lower `MinHouseAgeHours` 0, `MinHouseMembers` 1, `MinMembershipHours` 0, `MinHeldHoursForIncome` 0): `/dominion admin open 30`; stand in the Tollbridge for 5 minutes; `/dominion admin payday`; open the field again and `/event start truce`; read `oxide\data\RealmDominionMap.json`. | The banner rises about every 30 s, then "House X takes The Tollbridge!"; `holding_taken` in `/chronicle`, 5 points in `/season house`, the deed in `/renown`; the payday reaches `/vault` and `/treasury audit` balances; the Herald pauses the War Hours for the truce and banners freeze; the map file has the guide's schema with live values. | RealmDominion D4, D8, D10, D11 |
| PT1.25 | *(wave 4)* **Deciding: the teleport.** `/travel mill` and stand still 10 s; travel back and forth several times; `/travel admin status`. `/home set` outside, then inside your own crest zone; `/home` from far away. `/road mill` and walk, comparing the game's compass. | You arrive on the ground at the other waystone (not stuck, not falling); the toll leaves `/purse` and reaches `/treasury`; "arrivals not confirmed 0"; `/home` is refused outside the crest and brings you back inside it; the direction words match north. **Fallback:** if the move fails or arrivals are not confirmed, set `Travel.Mode: "road"` (journeys become the guided road) and write it in the guide; if north is flipped, set `Roads.NorthIsPositiveZ: false`. | RealmTravel T4, T5, T9, T12 (U1, U2, U5, U8) |
| PT1.26 | *(wave 4)* **Quests alone** (content deployed, Season 1 running): take tasks that craft, slay, build, visit and deliver (`/quest admin reset <you> daily` to redraw); craft at a bench and by hand; kill a wolf, a deer and a chicken; place stone and wood blocks, break one and place it again; `/quest give all` with 150 wood; finish a task after 30 active minutes; finish one with full packs, then `/quest collect`; walk the story with `/quest admin complete <you> <step>`; watch 04:00 UTC. | Progress per product (if nothing moves, `ItemCrafterItemEvent` does not reach plugins: report it); `/quest admin creatures` names the kinds (change `slay_creature` targets to them if needed); one count per new cell only; "You reach the Hearth."; the goods leave the packs at once; marks paid and `/treasury audit` balances; the goods arrive once with no RealmSentinel alert; +5 renown (`quest_daily`); the story ends with the Herald and `title_earned`; the dailies change at 04:00 UTC, the weeklies on Monday, and a relog shows the same board. | RealmQuests QS4 to QS8, QS11 to QS14, QS18 |
| PT1.27 | *(wave 4)* **Deciding: gathering seen by plugins.** `/craft admin watch <you>`. Chop a tree, break stone and iron ore, pick wild flax; move wood into a chest and back, drag a chest stack onto a carried one, split a stack; drop wood and pick it up; salvage a placed object and break a crate; plant and harvest a plot; kill and loot a wolf, then hit the corpse; craft at a smithy, a tannery, a campfire and by hand; `/craft admin unmapped`. | The watch says "gathered" with XP for real gathering only; "moved (not gathered)" for chest moves and pick-ups; "handed over by the game (Loot)" with no XP for salvage; "harvested", then Foraging XP; "slew wolf: Hunting +40" and corpse goods capped at 60 a creature; "made 1 <product>" at each station. Note the real creature, product and station names. **Fallback:** if gathering is not seen, set `Gathering.Enabled: false` and write it in the guide; kills, crafting and commissions still work. | RealmCrafts C2 to C8 (U1 to U7) |
| PT1.28 | *(wave 4)* **Crafts perks and windows:** `/craft admin level <you> mining 50` and mine for a minute; `level <you> smithing 50` and craft many items; reach level 50 with popups on (`level 49` and some work); `/craft admin crown`. | The "Your skill finds you N more Stone" line every 30 s and the stone in the packs at once (RealmSentinel quiet); about 1 craft in 10 gives "A master's touch", at most 10 a day; the "Master of ..." window, the Herald line, the Guildmaster title and the Chronicle entry; the Master Crafter crowning with 250 marks and season points. | RealmCrafts C9, C10, C13, C14 (U8, U10) |
| PT1.29 | *(wave 4)* **The living world alone:** lay a 3-place hunt with a wooden chest at the dig site (`/world admin hunt new`, `hunt step` at each place, `hunt chest`), `/world admin start treasure`, walk the places (run to the last one once), open the chest. `/world admin creatures` in the wild, put a kind into a legend's `Kinds`, `/world admin start legend <id>` and hunt it. `/world admin census`. Set a world event 20 minutes before Crown Night. | The Herald's first riddle; "You found place 1 of 3!" and the clue window; a place reached less than 20 s after the last is not counted; at the dig site +150 marks and the goods **in the chest** (else set `Treasure.UseChest: false`); the kinds are listed; the Legend takes clearly more blows (Toughness 4) and pays its slayer; `census_taken` in `/chronicle`; the clashing event waits or is skipped and no countdown is heralded for it. | RealmWorld W3, W4, W5 (finder), W11, W13, W14 (U1, U3, U4, U8, U10) |
| PT1.30 | *(wave 4)* **Deciding: guild colours from the server.** Found a house from inside a game guild (or `/house link`); `/heraldry sync`; look at the guild menu, a placed crest's flags, your armour and the name-tag icon; relog. `/heraldry preview`, then `/heraldry colours moss-gold`. Count the banners and emblems in the game's banner menu, enter them as `Heraldry.BannerCount` and `PatternCount`, reload, `/heraldry banner <house> 1 1`. | The guild takes the house's name and colours; write down which parts change at once, after a relog, or never; the new pair shows at once; the banner and emblem change with no error in the client's log. **Fallback:** if the guild does not change, set `Heraldry.Enabled: false` and keep the elections. | RealmHeraldry HR2, HR3, HR5, HR6 |
| PT1.31 | *(wave 4)* **Damaged-file guard, wave 4.** Stop the server; cut `RealmDominion.json`, `RealmQuests.json`, `RealmArena.json`, `RealmTravel.json`, `RealmCrafts.json`, `RealmWorld.json` and `RealmHeraldry.json` in half; start; try each plugin's command; save; stop. Restore the good files. | Each plugin refuses or pauses with its own message (`/dominion` paused, `/quest` says the board is closed, and so on); after the save every damaged file is unchanged. | RealmDominion D12; RealmQuests QS17; the data sections of the Arena, Travel, Crafts, World and Heraldry guides |
| PT1.32 | *(wave 4)* **Restart, wave 4.** With a waystone found, a home set, a kit taken, crafting levels and an open commission, a treasure hunt running and a ballot open (`/ballot admin open`, `/ballot stand marshal` with `Voters.*` lowered): save and restart. | Found waystones, homes, cooldowns and kit claims; levels, the week, the commission and its hold in `/treasury`; the hunt and its progress; the candidate, the deposit and the ballot's close time. All still there. | RealmTravel T14 (U11); RealmCrafts C16 (U11); RealmWorld W15 (hunt); RealmHeraldry HR11 |
| PT1.21 | End of session (destructive, after a backup): `/season end`, then Smoke E13 (delete `RealmSeasons.json`, keep `RealmLegends.json`, restart). Restore the backup afterwards. | E12 and E13 pass. | Smoke E12, E13 |

### PT2. Realm's art in the world (the owner alone)

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT2.1 | Place a staff crest near spawn so the tests are inside a crest zone. | `/sculpt preview` later reports the crest zone. | RealmSculptor first test 3 |
| PT2.2 | Place a sign. `/paint status`, then `/paint nearby` in front of it. | No "No art" warning; the sign is listed with a name and face size (write both down). | RealmPainter 1, 2; Painter U3 (which placeables are paintable) |
| PT2.3 | Look at the sign: `/paint sigil-varrow`. | The reply says `look`. The picture shows on your screen. If not, follow the guide's step 4 branches (`SendMode: apply`, reconnect, server log). | RealmPainter 3, 4; Painter U1 (server `LoadImage` and the update event), U5 (look-at targeting) |
| PT2.4 | Check the picture's orientation and the face around a fitted picture. | Right way up (else set `FlipX` / `FlipY`); outside the picture the sign shows plain wood. | Painter U4 |
| PT2.5 | Reconnect; restart the server; `/paint signs`. | The picture is still there both times; s1 shows `ok`, not `missing`. | RealmPainter 5; Painter U2, U6 (view ids across restart) |
| PT2.6 | Calibrate the face: dot the four corners with the game's brush as admin, `/paint face calibrate`, `/paint face save`. | The next painting fills the face; the name and face are recorded (SGN-2). | RealmPainter 6; Painter U3 |
| PT2.7 | Second sign: `/paint chronicle`; found a house or issue a decree. | Within about two minutes the board shows it; `/paint status` counts a redraw. | RealmPainter 7 |
| PT2.8 | On a sign painted with relief brushes, bind a painting. | The relief is gone. | RealmPainter 10; Painter U8 |
| PT2.9 | Set `Compression: system`, `/paint redraw`, `/paint status`. | Either it works or it falls back to the plugin's encoder, and the status says which. | Painter U10 |
| PT2.10 | `/sculpt materials`; read `oxide\data\RealmSculptor-materials.json`. Then `/sculpt preview heralds-pillar` facing open ground and `/sculpt place heralds-pillar`. | Ids 1-9 match the guide's names (fix `MaterialIds` if not); preview reports a clear space, the crest zone, a corner about 3 cells ahead in the direction you face; the pillar rises in about 4 s with 0 skipped; colours show (note which materials take paint). No build scaffold or delay. | RealmSculptor 1, 2, 4, 5; Sculptor U1-U5 |
| PT2.11 | Reconnect; then `/realm.save`, restart, `/sculpt placed`; hit the pillar. | The pillar stands with its colours; #1 is listed with protection on; no damage. | RealmSculptor 7, 8; Sculptor U3 |
| PT2.12 | Damage it with a weapon, a siege weapon if available, and a salvage tool; try to place a block into it and remove one. Then `/sculpt protect 1 off`, break a block, `/sculpt placed`, `/sculpt repair 1`, `/sculpt protect 1 on`, and finally `/sculpt undo`. | No health lost and the monument line; placing and removing refused; one lost then repaired; undo takes it down top first and the ground is as before. | RealmSculptor 9-11; Sculptor U6 |
| PT2.13 | **Shape test:** face +z, `/sculpt place shape-test 0`, compare each row with its preview; record what rotation 0 really looks like; `/sculpt undo`. Then WLD-1. | Each shape's real look written down; tools fixed and sculptures rebuilt. | RealmSculptor shape test; Sculptor U7 (shapes, `PermittedRotations`) |
| PT2.14 | Place `ironbreaker`, then a house monument; restart the server in the middle of the house monument's build. | Both stand; the interrupted build finishes after the restart. | Sculptor U8 |
| PT2.15 | Place `heralds-pillar` on a wooden floor; place a test sculpture with `unclaimed` (with `AllowUnclaimedLand`), set `blockDecay` short, wait. Undo both. | Write down whether the floor counts as ground; the protected test sculpture does not decay. | Sculptor U10, U6 (decay guard) |
| PT2.16 | Ironbreaker alone: L1 (`/ironbreaker items sword`, check the tooltip says Two-Handed), L2 (`/ironbreaker grant <you>`), then `/paint ironbreaker` on a sign, L9 (drop it), L10 (restart with yourself as bearer, `/ironbreaker status`). | As the guide says; set `BaseItem` if the wrong sword was picked. The board reads "Borne by <you>", then "Unclaimed" after the drop. | RealmLegendary L1, L2, L9, L10; Legendary U1, U7, U8, U9, U10; Painter U11 (`GetBearerName`) |

### PT3. Two players (the owner and one friend, or a second Steam account on a second PC; LAN is fine)

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT3.1 | The second player joins for the first time. | They get the welcome; you see one Herald line about them. | RealmHerald 1 |
| PT3.2 | With the second player watching, `/paint sigil-ashgrove` on a new sign. | They see it appear without reconnecting. | Painter U1 (reaches other clients); RealmPainter 4 |
| PT3.3 | The second player, standing far away, comes back to the pillar; then reconnects. | The pillar and its colours are the same for them. | RealmSculptor 6; Sculptor U3 (far clients) |
| PT3.4 | Give the second player a sign, bind it to a painting, have them paint over it. | The realm's picture comes back within a tick; your own admin strokes are left alone. | RealmPainter 9; Painter U7 |
| PT3.5 | Warden: steps 3 to 9 (protection, forfeit, combat log, chat flood, name rule, raid hours, report). | As in the guide; blocks lose no health in raid hours. | RealmWarden 3-9; Warden U1-U6 |
| PT3.6 | Throne gate: with the throne held and no window open, the second player (no claim) tries to take it. | They are blocked. | Launch plan Checkpoint 1 "Throne gate" |
| PT3.7 | Ransom: rope the second player; `/ransom list`; set `RansomMaxMinutes` to 1 and reload; wait. | They are untied by themselves with a `released` event. | Smoke C8; `OnPlayerEscape` / `OnPlayerRelease` (`oxide-rok-api.md` section 10 item 4) |
| PT3.8 | Contracts with two: C15 (delivery while the poster is offline), C16 (outlaw cooldowns, refund on pardon), C17 (honour mode). | As in the smoke test. | Smoke C15-C17 |
| PT3.9 | Treasury with two: T3 (gather while taxed), T6, T7, T9, T10. | As in the guide; if no tax is observed, set `ObserveGameTax` to false. | RealmTreasury T3, T6-T7, T9-T10; Treasury U2 |
| PT3.10 | Laws with two (set `JuryMin` 1, `MinVotes` 1, `JurorsMustLeadHouse` false): L4, L5, L6, L7, L9, L11. | As in the guide: the blow is stayed inside the zone, the fine takes items at once, trial by combat decides, the block is not placed, exile is enforced, the old laws lapse. | RealmLaws L4-L7, L9, L11; Laws U2, U3, U4, U5, U8, U9 |
| PT3.11 | Events with two: E4 (treaty break and lapse), E5 (truce: fist, sword, arrow, fire), E6 (breach with `TruceEnforced` false). | No damage lands in the truce; one `truce_broken` and -15 points. | Smoke E4-E6; RealmEvents U1; RealmSeasons U3 (treaty limits) |
| PT3.12 | Ironbreaker with two: L3, L4, L5, L6, L7, L8. Use `DebugStrikes`. | As in the guide; the log names the weapon path; exactly one blade at every point. | RealmLegendary L3-L8; Legendary U2-U6 |
| PT3.13 | Sentinel with two: step 6 (bow from 50 to 100 m; melee sparring), 7 (strongest weapons and a ballista), 8 (kill rate). | Projectile range peak near the distance; no melee range peak from sparring; damage peaks under 75 and 200; `kill_rate` with the lowered limit. | RealmSentinel U6-U8 |
| PT3.14 | Sentinel with two: step 10 (take 600 stone from a chest; loot a body), 11 (ground pickup), 19 (`/warden alerts` shows `ext:sentinel_<kind>`). | No finding for the chest and the body; ground pickup result written down. | RealmSentinel U10, U11, U19 |
| PT3.15 | Sentinel enforce mode on the second account: step 15 (freeze), 16 (kick and ban a throwaway account; lift the ban in Steward's Court), 17 (reconnect refusal). Back to watch mode afterwards. | Each action refused and the player pulled back; the freeze survives a relog; ban lifted in the Court; the seventh reconnect refused. | RealmSentinel U15-U17 |
| PT3.16 | Ravens with two: R3-R9, R13-R15, R17. | As in the guide; the login greeting is seen. | RealmRavens R3-R9, R13-R15, R17; Ravens U1-U4 |
| PT3.17 | Renown with two: 4 (kingslayer), 5 (title prefix in global chat on both clients), 7 (contract deed), 8 (oathbreaker). | The prefix shows on both clients in global chat only; `oxide.unload RealmRenown` restores normal chat. The Ironbreaker's chat title (L2) shows the same way. | RealmRenown 4, 5, 7, 8; Renown U1, U2; Legendary U9 |
| PT3.18 | Dynasties with two: D5-D10, D13-D17. | As in the guide. | RealmDynasties D5-D10, D13-D17; Dynasties U2 (`CmdClaim` through `plugin.Call`), U6, U7 |
| PT3.19 | Stats with two: a PvP death (step 7). | `Deaths` shows `pvp`. | RealmStats 7 |

### PT4. Three or more players (the first friends night)

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT4.1 | Bounty with A, B, C: Smoke C13. | The reward reaches C; `contract_fulfilled` appears. | Smoke C13; killer attribution by weapon (RealmEvents U3, RealmLaws U8) |
| PT4.2 | Mercenary: Smoke C18 during a declared claim's window. | Kills counted as described; reward after the window. | Smoke C18 |
| PT4.3 | Royal Tournament: Smoke E7, E8, then RealmLegendary L11 with nobody holding the blade. | Kills counted with the caps; prizes exact; the champion receives the blade. | Smoke E7, E8; RealmLegendary L11 |
| PT4.4 | King's Hunt and Crown Night: Smoke E9, E10. | As in the smoke test. | Smoke E9, E10; RealmEvents U7 (sleeping bodies score nothing), U8 |
| PT4.5 | Interception with a third account: RealmRavens R10-R12. | As in the guide. | RealmRavens R10-R12; Ravens U5 (return types) |
| PT4.6 | Jury with real defaults: three online house heads sit a trial. | The verdict follows the votes. | RealmLaws jury defaults |
| PT4.7 | Rebellion: a declared claim, a real window, two or more of the claimant house online; take the throne; let the window close. | `rebellion_started` and `rebellion_ended` within 15 s of the window edges; *Usurper* and *Kingmaker* titles. Run once the other way for *The Unbowed*. | Launch plan Checkpoint 2 timing; RealmRenown 6, Renown U3; RealmDynasties D14-D15 |
| PT4.8 | Champion title: after the tournament, check RealmRenown. | *Champion of the Lists* within a poll. | RealmRenown 9 |

### PT5. Going public (outside friends, the player app on Windows)

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT5.1 | Go Public > Add firewall rules; Doctor > Run checks. | "4 Realm rules found", scoped to `ROK.exe`; the TCP 11000-11003 block is there. | `going-public.md` firewall row; `connection-doctor.md` |
| PT5.2 | Public network setting, restart; `netstat -ano` for 7350 and 27015. | The PID is `ROK.exe`. | GP T8 |
| PT5.3 | Forward UDP 7350, TCP 7350, UDP 27015; a friend outside joins by typing the address and port. | They join; `Authentication verified for <name>` in the log with `bindIP` 0.0.0.0. | `HANDOFF.md` next step 2; launch plan (bindIP and Steam auth) |
| PT5.4 | Steam launch options `-ip 127.0.0.1 -port 7350` on the owner's game, then Play. Clear them afterwards. | The game skips the menu and joins. | GP T1; `join-and-scale.md` (launch arguments reach the game; the quick-join scene) |
| PT5.5 | Publish the signed `servers.json` (Steward > Publish), host it over https, build the player app with the published `player-config.json`. | The friend's player app shows the server Online with a ping. | GP T9; `START-HERE.md` published list; A2S answer (`troubleshooting.md`) |
| PT5.6 | Friend: install `Realm-Setup`, press **Play**. Then with a DNS name in the list. | Steam asks about launch options once; the game joins. If it stops at the menu, the card's paste steps work. | GP T2, T3; player-launcher U1; `troubleshooting.md` main-menu row |
| PT5.7 | On a Steam account without the game, press **Install**. | Steam's install dialog opens; Play comes back after install. | player-launcher U2 |
| PT5.8 | Note what SmartScreen shows for both installers on a clean PC. | Written down with a screenshot (PLA-4). | player-launcher U4 |
| PT5.9 | Host `news.json` and `update.json` next to `servers.json`. Start an older Realm; Update, Restart and update. | The installer runs, Realm closes, the new Realm opens; What's new shows once; the news shows. | player-launcher U3, U7 |
| PT5.10 | Run the portable build and offer an update. | Show installer opens Explorer with the verified installer selected. | player-launcher U5 |
| PT5.11 | Download a real ~80 MB update from the chosen host, including a GitHub Releases redirect. | It completes and verifies. | player-launcher U6 |
| PT5.12 | A `realm://join/s1` link on the friend's PC. | Realm opens and asks before joining. | `going-public.md` realm:// links |
| PT5.13 | Whitelist on, a friend added by the documented line format; a friend not on it tries. | Only the listed friend joins. | Launch plan (whitelist line format, `server-reference.md` 4.3) |
| PT5.14 | Steam offline mode on the owner's PC; run Doctor. | Doctor's Steam line says what it sees; write it down. | `connection-doctor.md` item 5 |
| PT5.15 | Big-moment walk with friends (EVT-5): a coronation, a rebellion start and end, a tournament champion, a blade claim. | Each has a herald line, a popup, a Chronicle entry, an overlay moment, a Discord post and a board that changes. | Delight bar, section 1 |

### PT6. Broadcast and community surfaces

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT6.1 | OBS on the server PC: browser source `http://127.0.0.1:8787/overlay` at 1920 x 1080 over the game; load `?replay=coronation` and `?replay=rebellion_started`. | Moments read well, stay inside the title-safe area, background transparent. | web-and-broadcast U1 |
| PT6.2 | OBS: `/throne-room?replay=1`, `/breaking-news?types=rebellion_started&replay=1`, `/starting-soon`; Windows "Show animations" off once. | Ray and spark burst, red edge, shaking card, key art; local fonts load in OBS; reduced motion fades only. | web-and-broadcast U2; `streamkit/README.md` (OBS, reduced motion) |
| PT6.3 | Discord: start the bot with a real token; register commands; `/realm king`, `/realm chronicle`, `/realm whois Corvane`; let it post the status message; assign a house role. | Replies with the emblem and thumbnails; status message posts and edits; roles assigned. Remove Attach Files from the status channel: it still posts, one warning. | web-and-broadcast U3; `bot/README.md` UNVERIFIED (sign-in, commands, posts, roles, permission integers, realm:// text) |
| PT6.4 | Steward's Discord herald with a real webhook during an event night. | Chronicle events post at a steady pace with no rate-limit errors. | `discord-herald.md` |
| PT6.5 | Upload the Discord banner, splash and icon PNGs. | They look right (the banner and splash need a boosted server). | `art/README.md` Discord PNGs |
| PT6.6 | Host the portal with `siteUrl`; paste the home page and `houses/varrow.html` into Discord. | Large key-art card; the Varrow sigil card. | web-and-broadcast U4 |
| PT6.7 | Open the hosted home page and `kings.html` on two real phones; add to home screen. | Hero, monarch plate, podium and titles grid fit; the emblem is the icon. | web-and-broadcast U5 |
| PT6.8 | After a real `title_earned` and season start: check the Chronicle, the overlay seal and the Hall of Kings roll; compare the portal's season points with `/season standings`. | Badges appear; points match (or WEB-6). | web-and-broadcast U6; `portal/README.md` season weights |
| PT6.9 | Run the bot from a Task Scheduler task "At log on". | It starts after a sign-in and stays up. | `bot/README.md` Task Scheduler |

### PT7. Load, endurance and operations (beta)

| # | Do | Pass if | Settles |
|---|---|---|---|
| PT7.1 | Daily restart set 15 minutes ahead. | Countdown notices in chat; the server exits and Steward restarts it after an `-auto` backup. | GP T7; `launcher/README.md` daily restart |
| PT7.2 | End `ROK.exe` in Task Manager during play. | Steward backs up and restarts on its ladder; every plugin comes back with its data; Warden combat-log handling as documented. | Steward crash restart; Warden U5 (`OnPlayerDisconnected` on crash, `OnServerShutdown` order); RealmStats 10 |
| PT7.3 | Reboot the server PC. | Write down whether Steward starts the servers by itself; the uptime monitor reports the outage. | `ops/hosting-guide.md` reboot |
| PT7.4 | Scheduled backup during play; check the backup's `consistent` flag; run the restore drill on a copy. | A consistent copy, or a retried one marked so; the drill passes. | `ops/README.md` hot snapshot; `ops/restore-drill.md` |
| PT7.5 | Register the ops tasks with Task Scheduler; leave them a week. | Repeating tasks repeat; offsite copies arrive. | `ops/README.md` Task Scheduler |
| PT7.6 | Install and update the dedicated server with the real SteamCMD into the master folder, then rehearse OPS-5. | SteamCMD's signature check passes; the update, Oxide reinstall and compile check work. | `ops/README.md` SteamCMD; `ops/disaster-recovery.md` game build change |
| PT7.7 | Run Server I and Server II together. | Both consoles print `Steam game server started … Secure: True`; a player joins each. | GP T6; `join-and-scale.md` Steam port 8766; `launcher/README.md` side-by-side servers |
| PT7.8 | With players on, `/paint redraw all` on 6 boards. | No lag spike reported; if there is one, lower `MaxRedrawsPerMinute` or turn off `BoardTexture`. | RealmPainter 8; Painter U9 |
| PT7.9 | With 20 or more players near, place a house monument (about 1,100 blocks, 18 s). | No lag complaint; else lower `BlocksPerTick` (WLD-10). | Sculptor U9 |
| PT7.10 | Sentinel step 21: watch CPU and frame time for a few minutes after deploying with 20+ players; run the week in watch mode and read `/sentinel peaks`. | No measurable load; peaks recorded (SEN-3). | RealmSentinel U21 |
| PT7.11 | 20 players for an evening: CPU, RAM, upload bandwidth per player. | Written down for OPS-2. | `ops/hosting-guide.md` bandwidth per player |
| PT7.12 | `timeBetweenPlayerJoin` 3 with a queue at a launch event. | Players get in about every 3 s without a load spike. | `launcher/README.md` join queue |
| PT7.13 | Fill toward 50, then 120 only if 50 is comfortable. | Max players set from the result. | `launcher/README.md` and `join-and-scale.md` 120 players |
| PT7.14 | Over the beta: watch the logs for RealmSeasons read retries, RealmStats timer drift and RealmWarden playtime after crashes. | Retries happen rarely and nothing is lost. | RealmSeasons U2; RealmStats U6; Warden playtime |
| PT7.15 | Oxide permission groups: `oxide.group add realm_admin`, grant by group, `oxide.usergroup add`. | Group grants work as the staff roles page says. | `staff-roles-and-permissions.md` group commands |

Items that cannot be settled by one test and stay open by design: balance numbers (RealmRavens U8, every cooldown and chance), name matching after renames (RealmRavens U7, RealmRenown U5), hook-name collisions with future plugins (RealmDynasties U9), chat brace safety (RealmDynasties U5, RealmLaws U11; `oxide-rok-api.md` section 10 item 6) and Oxide `webrequest` over HTTPS on the game's Mono (no Realm plugin depends on it; section 10 item 8). Review them at each season's end.

---

## 6. Risks and mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| A deciding hook does not behave in the real game (server-placed blocks invisible, sign pictures refused, damage blocking leaks, item moves not shown). | Medium | High: a headline feature is lost. | Each has a fallback already built: park RealmSculptor or RealmPainter, announce-only switches for truce, laws and raid hours, honour mode for escrow. M1 runs these first so plans change early. |
| A Steam update to the dedicated server overwrites the Oxide DLL or changes the game build so Oxide 2.0.3867 no longer loads. | Low (no update in years) | High: the realm is down. | OPS-5 runbook; keep the old master copy; never update on an event day; the uptime monitor; announce downtime in the news feed. |
| Oxide 2.0.3867 is old and unmaintained; a bug in it cannot be fixed upstream. | Medium | Medium | Stay within proven hooks; work around in plugins; the compile check pins the metadata; every workaround is tested. |
| Too few players: the political loop needs houses and rivals. | High | High | Small-server profile (SRV-9); a schedule built around event nights; creators; open weekends before permanent opening; the season rhythm gives reasons to come back. |
| The owner is the only key holder and the only operator (bus factor 1). | High | High | OPS-7 deputy and break-glass notes; staff roles; runbooks; the signing key backed up offline. |
| Home hosting: power, upload bandwidth, Windows Update reboots, the PC being needed for other things. | Medium | Medium | OPS-2 hosting decision from measured numbers; OPS-8 hygiene; the restart ladder; the uptime monitor. |
| Network load from big monuments and sign redraws. | Medium | Medium | Rate limits in both plugins; build big pieces in the Interregnum or with few players on; PT7.8 and PT7.9 set the limits. |
| Sentinel punishes an honest player. | Medium | High: trust is lost. | Watch mode first; enforce only after 2 clean weeks; no AutoBan; evidence lines for every finding; appeals (SEN-7). |
| Item duplication or loss ruins the economy. | Low (zero-sum designs, exploit suites) | High | Audits after every session; QA-4 regression rule; escrow honour-mode fallback; backups before every event. |
| Save or data corruption. | Low | High | Damaged-file guards never overwrite; `_lastgood` copies; backups before restarts; restore drill monthly. |
| The rights holder objects, or the EULA forbids something we do. | Low to medium | Very high | COM-6 before 1.0; no money involved; no client changes; the outreach letter ready; a plan to close the server cleanly if asked. |
| Trademark or franchise confusion. | Low | Medium | Original lore; the art build's trademark guard; the writing guidelines in `lore.md`. |
| Players' data: privacy complaints. | Low | Medium | Minimal data, pseudonymous stats, the privacy notice, the deletion process, `find-player-data.mjs`. |
| Toxicity and griefing drive players away. | Medium | High | RealmWarden, the rules, moderators on duty at events, appeals, the creator code of conduct. |
| Scope creep: new systems before the existing ones are proven. | High | Medium | This roadmap's gates; P2 and P3 work waits until 1.0; each season adds one headline feature only. |
| Volunteer burnout. | Medium | High | Small, clear roles with time budgets (section 7); the Interregnum as a real break; nobody on call two event nights in a row. |
| Unsigned installers scare players away. | Medium | Medium | PLA-4 decision; the download page explains SmartScreen; the SHA-256 sums are published. |
| Time zones: rebellion windows and events at bad hours for part of the community. | Medium | Medium | EVT-4; rotate event times across a season; publish times in the player's local time in the app and on the portal. |

---

## 7. Volunteer roles

Each role has an owner of record in the staff channel. One person may hold several roles, except that **staff who hold plugin admin permissions do not compete** while holding them ([`staff-roles-and-permissions.md`](community/ops/staff-roles-and-permissions.md)).

| Role | What they own | Time | Needed from | Start here |
|---|---|---|---|---|
| **Owner** | The server PC, Steward, the signing key, final decisions, gates in this file | 3-5 h a week | now | [`START-HERE.md`](../START-HERE.md), [`HANDOFF.md`](HANDOFF.md) |
| **Deputy owner** | Break-glass access: restart, restore, ban, roll back Oxide | 1 h a week, on call | M3 | `ops/disaster-recovery.md` |
| **Plugin developer** (1-2) | `plugins/`, logic tests, exploit suites, the integration check; C# 3 only | 4-8 h a week | M0 | [`oxide-rok-api.md`](oxide-rok-api.md), [`realm-systems.md`](realm-systems.md) |
| **Launcher developer** | `launcher/` (both apps), release packaging, screenshots | 3-6 h a week | M0 | `launcher/README.md`, [`player-launcher.md`](player-launcher.md) |
| **Play-test lead (QA)** | Section 5 of this file, the results log, bug triage, the big-moment walks | 3-5 h a week plus sessions | M1 | [`smoke-test.md`](smoke-test.md) and each plugin guide's smoke steps |
| **Builder** (1-3) | The world plan, monuments, sign placement, the layout manifest; `art/sculptures/` | 3-6 h a week, more in each Interregnum | M1 | `art/sculptures/README.md`, `plugins/docs/RealmSculptor.md`, `plugins/docs/RealmPainter.md` |
| **Artist / art director** | `art/`, sign paintings, season sets, icons; the palette and licence records | 2-5 h a week | M2 | [`brand.md`](brand.md), `art/README.md` |
| **Saga writer** | `docs/saga/`, proclamations, season packs, news text; lore consistency | 2-4 h a week, more before a season | M3 | [`saga/README.md`](saga/README.md), [`community/lore.md`](community/lore.md) |
| **Event host** (several) | Running event nights from the run-sheets; not competing in their own events | the event night | M2 | `docs/saga/runsheets/`, `plugins/docs/RealmEvents.md` |
| **Moderators** (2-6) and moderator lead | Chat, reports, Warden alerts, appeals; Sentinel alerts with an admin | shifts | M2 | [`moderation-handbook.md`](community/ops/moderation-handbook.md), [`ban-appeals.md`](community/ops/ban-appeals.md) |
| **Community manager** | Discord, house claim sign-ups, news, creators, surveys | 2-4 h a week | M2 | [`launch-plan.md`](community/launch-plan.md), [`streamer-kit.md`](community/streamer-kit.md) |
| **Broadcast producer** | OBS scenes, the overlay, the trailer, creator nights | event nights | M3 | `streamkit/README.md`, `chronicle/README.md` |
| **Web maintainer** | The portal and its hosting, the bot, sync-art | 1-3 h a week | M3 | `portal/README.md`, `bot/README.md` |
| **Ops** | Backups, the monitor, SteamCMD updates, the restore drill, the PC | 1-2 h a week | M3 | [`ops/README.md`](../ops/README.md) |
| **Legal reader** | The EULA checklist, privacy notice, deletion requests, the guardrails | a few hours before each gate | M3 | [`legal/README.md`](legal/README.md) |

---

## 8. Keeping this file true

- At each gate: tick the exit criteria here, update the milestone table's dates, move done backlog rows to a "Done" note with the commit, and record play-test results in the guides and in [`realm-systems.md`](realm-systems.md).
- A new feature enters the backlog with an ID, a priority, a size, its milestone and a role, and it must name the channel in section 0 that carries it to players.
- A new UNVERIFIED claim anywhere in the repo gets a row in section 5.
