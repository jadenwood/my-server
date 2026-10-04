# Realm systems map

One page for every part of Realm: what it does, where it lives, how it connects to the rest, and how far it has been proven. Read this before changing anything that crosses a folder boundary.

**The short version of the status (2026-10-03).** Every piece has automated tests or checks that pass here, on Linux. On the owner's real Windows 11 server (2026-10-02) the 14 plugins of that day compiled and loaded under Oxide, Season 1 started by itself, and the owner joined and reached character creation ([`HANDOFF.md`](HANDOFF.md)). **No plugin feature has been seen working in game yet**, and no chat reply has been checked. The plugins compile against the real Oxide 2.0.3867 and game DLL metadata and are tested against mocks; the Windows apps and PowerShell scripts were built or parse-checked, and only an earlier Steward build has run on Windows. The ordered proof on real hardware is the play-test checklist in [`ROADMAP.md`](ROADMAP.md) section 5, which gathers [`smoke-test.md`](smoke-test.md) and every guide's smoke steps. Anything marked UNVERIFIED below has not been seen working.

Status words used here:

| Word | Meaning |
|---|---|
| **Compile-checked** | Compiles with 0 errors at C# 3 against the shipped Oxide 2.0.3867 and patched game DLLs (`tools/plugin-compile-check/check.sh`). |
| **Mock-tested** | Behaviour tests run the real source against stand-ins for the game and Oxide. They prove the plugin's own logic, not the game's behaviour. |
| **Tested here** | Automated tests pass in this Linux environment (Node or .NET). |
| **UNVERIFIED** | Not seen working on the real thing. |
| **On a branch** | Finished and committed on the named team branch, not yet merged into `claude/great-maxwell-wrksvt` (2026-10-03). Drop the note when it is merged. |

## 1. The big picture

```
                         players' own Steam copy of Reign of Kings (never modified)
                                              │ join (steam:// links, direct connect)
                                              ▼
 Realm Steward ──starts/stops──► ROK.exe dedicated server(s) + Oxide 2.0.3867 + plugins/*.cs
 (launcher/)    ◄─admin console─┤        │
   │  Court, Doctor, backups    │        │ plugins talk to each other with Plugin.Call (section 3)
   │                            │        ▼
   │                            │   oxide/data/*.json   (RealmChronicle.json, RealmState.json, RealmHouses.json, ...)
   │                            │        │ read-only
   ├─ in-process Chronicle ◄────┴────────┼──────────────► chronicle/server.js  127.0.0.1:8787
   │  + Discord herald                   │                 /api/state /api/events /overlay /realm
   │                                     │                       │
   │                                     ├─► portal/ (static site) │──► streamkit/ (5 OBS scenes, :8790)
   │                                     ├─► bot/ (Discord bot)  ◄─┘
   │                                     └─► analytics/ (reads oxide/data/RealmStats day files)
   │
 Realm (player app, launcher/) ── signed servers.json ── live status, Join through Steam
```

**What reaches players inside the game** comes only through what the game already carries from a server: chat, notices and popups (every plugin, with RealmHerald's hub), building blocks and their colours (RealmSculptor's monuments), painted signs (RealmPainter's art and live boards) and the built-in `Mods\*.cfg` moods. The game cannot load new models, textures or UI from a server ([`in-game-art.md`](in-game-art.md)).

The **closed list of Chronicle event types** is the main contract between the parts. It lives in three files that a test keeps in step: `plugins/RealmChronicle.cs` (`KnownTypes`), `chronicle/server.js` (`EVENT_TYPES`) and `chronicle/public/assets/common.js` (`TYPE_META`). There are 47 types (`holding_taken` from RealmDominion, `census_taken` from RealmWorld, `vote_held` from RealmHeraldry, `blade_claimed` and `blade_lost` from RealmLegendary). A new type needs all three; `tools/realm-integration/check.mjs` fails if they differ, if a plugin logs a literal type that is not registered, or if a `plugins/docs/*/EVENTS.json` request is still waiting. The portal (`portal/lib/model.mjs`), the Discord herald (`launcher/lib/discord.js`), the bot (reads `common.js` as text) and the art event map (`art/icons/event-map.json`) carry the same labels.

## 2. Server plugins (`plugins/`)

All are Oxide C# plugins, C# 3 syntax, deployed together by Realm Steward or `server/Deploy-Plugins.ps1`. All are **compile-checked**. The 14 plugins present on 2026-10-02 loaded on the owner's server; no plugin feature has been seen working in game. Player commands: [`realm-commands.md`](realm-commands.md). The in-game test for every UNVERIFIED item is in [`ROADMAP.md`](ROADMAP.md) section 5 (PT rows).

| Plugin | What it does | Writes | Proof so far |
|---|---|---|---|
| `RealmHouses.cs` | Houses, ranks, oaths of fealty, treaties, oathbreaker and treaty-breaker marks. The base API every other plugin reads. | `RealmHouses.json` | Compile-checked, mock-tested (57), exploit suite (crown-houses 16). Loaded on the real server. |
| `CrownAndConsequences.cs` | Tracks the monarch on the Old Throne; decrees, council, claims and scheduled rebellion windows, tax cap, bounded ransom. | `CrownAndConsequences.json` | Compile-checked. Loaded on the real server. Throne capture, the throne gate, Royal Stores and ransom release are UNVERIFIED (PT1.8, PT1.9, PT3.6, PT3.7). With `team/heraldry`: elected council seats the monarch cannot dismiss in their term, and decree mandates from referendums (non-public `GetCouncilSeats`, `GetCouncil`, `SeatElectedCouncillor`, `GetDecreeList`, `SetDecreeMandate`). |
| `RealmChronicle.cs` | The event log (`Log`) and the realm snapshot. Rejects unknown types; per-source flood budget. | `RealmChronicle.json`, `RealmState.json` | Compile-checked; type list tested against the Chronicle service; exploit suite (chronicle 27). Loaded on the real server. |
| `RealmContracts.cs` | Bounties on public enemies, deliveries, mercenary work, with real item escrow. Takes court outlawry from RealmLaws. | `RealmContracts.json` | Compile-checked; the court hand-off is mock-tested (`tools/realm-integration/cross-tests`, 25); exploit suite (laws-contracts 24). Item escrow in game is UNVERIFIED (PT1.6). |
| `RealmSeasons.cs` | Numbered seasons, house standings, the Hall of Kings that survives wipes. | `RealmSeasons.json`, `RealmLegends.json` | Compile-checked; exploit suite (seasons 14). Season 1 auto-started on the real server. Guide: [`plugins/docs/RealmSeasons.md`](../plugins/docs/RealmSeasons.md). |
| `RealmEvents.cs` | Crown Night, Royal Tournament, King's Hunt, Truce of the Realm, with heralds and item prizes. | `RealmEvents.json` | Compile-checked, mock-tested (89), exploit suite (events 26). Truce damage blocking is UNVERIFIED (PT3.11). `team/ironbreaker` adds a 15-line prize hook for RealmLegendary. |
| `RealmArena.cs` | Duels to the first fall (the killing blow is turned aside: no death, no loot) in a ring no one else may enter; stakes in marks held by RealmTreasury's escrow; an Elo ladder and a weekly Champion of the Ring (RealmRenown titles); team duels; bracket tournaments and a bracket for the Royal Tournament (RealmEvents `ScoreTournamentDuel`); trial by combat for RealmLaws; Hearth Dice and Twenty-One for marks with no house edge. | `RealmArena.json` | **On a branch** (`team/arena`). Compile-checked, mock-tested with the real treasury (312), exploit suite (arena 52, arena-events 15, arena-laws 12). Everything in game is UNVERIFIED (guide steps U1 to U19). Guide: [`plugins/docs/RealmArena.md`](../plugins/docs/RealmArena.md). |
| `RealmLaws.cs` | Laws and zones, a public crime ledger, accusations, jury trials, trial by combat, fines, outlawry, exile, pardons. | `RealmLaws.json` | Compile-checked, mock-tested (90). Blocking acts in game is UNVERIFIED (PT3.10). |
| `RealmDynasties.cs` | Bloodlines, heirs, succession, prestige, blood claims after a monarch falls, titles bestowed by the crown. | `RealmDynasties.json` | Compile-checked, mock-tested (92), exploit suite (dynasties 3). Pressing a claim calls CrownAndConsequences' `/claim` command method (UNVERIFIED in game, PT3.18). |
| `RealmRenown.cs` | Renown and infamy from deeds, earned titles worn in chat. | `RealmRenown.json` | Compile-checked, mock-tested (108), exploit suite (renown 11). The chat prefix is UNVERIFIED (PT3.17). |
| `RealmTreasury.cs` | Marks (a ledger currency), purses, a market with escrow, house vaults, the crown's treasury, mint and tithe. | `RealmTreasury.json`, ledger logs | Compile-checked, mock-tested (118), exploit suite (treasury 26). Game-tax observation is UNVERIFIED (PT3.9). |
| `RealmRavens.cs` | Letters between players and houses, interception by spies, moderated anonymous rumours. | `RealmRavens.json` | Compile-checked, mock-tested (95), exploit suite (ravens 6). |
| `RealmWarden.cs` | New-player protection, raid hours, combat-log flags, reports, mutes, name rules; evidence for admins. | `RealmWarden.json`, logs | Compile-checked, mock-tested (140), exploit suite (warden 4). Raid-hour blocking is proven only for block health (PT3.5). |
| `RealmStats.cs` | Pseudonymous daily player statistics with opt-out. | `RealmStats/` day files | Compile-checked, mock-tested (89). |
| `RealmCourt.cs` | Puts `/realm.save` and `/realm.players` into the game's own command table for Realm Steward's console. | nothing | Compile-checked. UNVERIFIED at run time (PT0.8). |
| `RealmHerald.cs` | Welcome, first steps, the `/realm` hub (every player command by subject), tips, message of the day, popups; the one chat style every plugin follows. | `RealmHerald.json` | Compile-checked, mock-tested (102). Popups from a dedicated server and chat colours are UNVERIFIED (PT1.1 to PT1.4). Guide: [`plugins/docs/RealmHerald.md`](../plugins/docs/RealmHerald.md). |
| `RealmSculptor.cs` | Staff-only `/sculpt`: places, paints, protects, repairs and removes Realm's monuments built from the game's own blocks and colours (11 sculptures in `art/sculptures/`). | `RealmSculptor.json`, reads `RealmSculptor/*.json` | **On a branch** (`team/sculptor`, 716fba0). Compile-checked, mock-tested (159, including the real DLL's metadata for every reflected member). Whether server-placed blocks and colours show for clients is UNVERIFIED (PT2.10 to PT2.15). |
| `RealmPainter.cs` | Staff-only `/paint`: writes Realm's art into painted signs and keeps live boards (Chronicle, wanted, standings, proclamation, event, Ironbreaker, notice). | `RealmPainter.json`, reads `RealmPainterArt.json` | **On a branch** (`team/sign-painter`, ff5dc1e). Compile-checked, mock-tested (122, plus 4 PNG decode checks). Whether a server-set picture reaches clients is UNVERIFIED (PT2.2 to PT2.9, PT3.2). |
| `RealmLegendary.cs` | The Ironbreaker: exactly one legendary blade, won at the Royal Tournament, taken by the bearer's slayer; staff `/ironbreaker`. | `RealmLegendary.json` | **On a branch** (`team/ironbreaker`, 93b8cfd). Compile-checked, mock-tested (142), exploit suite (legendary 48). Item identity and damage scaling in game are UNVERIFIED (PT2.16, PT3.12, PT4.3). |
| `RealmSentinel.cs` | Server-side cheat watch: movement, combat, items, floods and staff-name impersonation scored with evidence; alert, freeze, kick, ban on confirm; staff `/sentinel`. Never touches the game's own anti-cheat. Ships in watch mode. | `RealmSentinel.json`, `RealmSentinelFeed.json`, logs | **On a branch** (`team/anti-cheat`, 8ac7c18). Compile-checked, mock-tested (173), exploit runner (24). Every limit is UNVERIFIED until tuned in game (PT1.16, PT3.13 to PT3.15, PT7.10). |
| `RealmDominion.cs` | Territorial war: named holdings (villages, keep, mine, harbour, crossroads) that houses take by holding the field in the War Hours (paused in truces, rebellions and shut raid hours); garrisons, daily marks into house vaults (`RealmTreasury.GrantHouseIncome`) and season points, `holding_taken` Chronicle entries, the `/paint dominion` board; `/dominion`. | `RealmDominion.json`, `RealmDominionMap.json` (for the portal and Chronicle; schema in the guide) | **On a branch** (`team/dominion`). Compile-checked, mock-tested (196), exploit suite (dominion 37 + 13). Everything in game is UNVERIFIED (guide steps D1 to D12). Guide: [`plugins/docs/RealmDominion.md`](../plugins/docs/RealmDominion.md). |
| `RealmQuests.cs` | Daily and weekly tasks, the Season 1 story (The Hollow Crown, 4 acts, 17 steps), 68 deeds in five kinds with tiers, weekly house goals; `/quest`, `/achievements`. Content is JSON in `plugins/docs/RealmQuests/content/` (deployed to `oxide/data/RealmQuests/`). Pays marks (RealmTreasury `RewardMarks`), renown and titles (RealmRenown deeds), house season points. | `RealmQuests.json` | Compile-checked, mock-tested (298), exploit suite (quests 45, plus 13 for `RewardMarks`). Everything at run time is UNVERIFIED (guide QS1 to QS18). Guide: [`plugins/docs/RealmQuests.md`](../plugins/docs/RealmQuests.md). |
| `RealmCrafts.cs` | Professions and mastery: woodcutting, mining, foraging, hunting, smithing, carpentry, tailoring and cooking, with XP read from the game's own container, harvest, damage and crafting events; ranks and the Guildmaster title (RealmRenown); perks the server can grant (bonus yield and extra items into the packs, a lower market fee through RealmTreasury); a weekly Master Crafter; house workshops that pool members' XP for perks and season points; a commission board with marks held in RealmTreasury holds; `/craft`. | `RealmCrafts.json` | **On a branch** (`team/crafts`). Compile-checked, mock-tested with the real treasury (304), exploit suite (crafts 71). Everything in game is UNVERIFIED (smoke steps C1 to C16 in [`plugins/docs/RealmCrafts.md`](../plugins/docs/RealmCrafts.md)). |
| `RealmTravel.cs` | Waystones players unlock by walking to them, `/travel` between them for a toll in marks (to the crown's treasury), `/home` in your own crest zone, `/road` directions in chat, and kits (newcomer, daily house, season). No journey in a fight, with a captive, as an outlaw, with the Ironbreaker, or near the throne in a rebellion; an arrival shield against campers. | `RealmTravel.json` | **On a branch** (`team/travel`). Compile-checked, mock-tested (301), exploit suite (travel 69 + 11). The server-side teleport (`CharacterTeleport.Teleport`, the game's own `/tp` call), kit items and crest checks are UNVERIFIED in game (smoke steps T1 to T14 in [`plugins/docs/RealmTravel.md`](../plugins/docs/RealmTravel.md)). |
| `RealmWorld.cs` | The living world between RealmEvents' events: treasure hunts (riddles in chat and on painted signs, a finder's chest), the Blood Moon night (fair kills for renown and house points, hungrier beasts), the Merchant Caravan (an escort between two places or waystones, raiders and their price), Wandering Legends (a tougher marked creature, or the first of its kind slain in a region), the Harvest Fair and Midwinter (house offerings, RealmSculptor decorations) and the weekly Census (`census_taken`). A schedule that never meets RealmEvents; `/world`, `/treasure`, `/caravan`, `/festival`. | `RealmWorld.json` | **On a branch** (`team/world-events`). Compile-checked, mock-tested (326), exploit suite (world 48). Everything in game is UNVERIFIED (smoke steps W1 to W15 in [`plugins/docs/RealmWorld.md`](../plugins/docs/RealmWorld.md)); moods `harvest-festival` and `midwinter` in `mods/presets/`. |
| `RealmArrival.cs` | The Gate of the Unwritten: a newcomer's first minutes, from the game's own character screen on the raft to the Hearth fire. At the Finish click the game's loader covers a move onto one of six arrival stones in the Gatehouse (`CharacterTeleport.Teleport`, or the spawn provider behind a switch, with two arrival checks and road mode as the fallback); two lore lines, then the gate opens on the gold line (a 5 x 6 portcullis of real blocks sinking row by row, or an open arch) and the Hearth's ember band flares; six house banners on the avenue with live house data and an optional pledge (looking to a house, told to its members, never a join); the fire lines in order after RealmQuests' and RealmTravel's own; Written on `/quest`. Variants for veterans on a fresh world, resume after a log-off, Hearth's Mercy respawns, a sanctuary, eviction, a release shield and an arrival-zone guard; `/arrival` (`skip`, `tour`) and `/arrival admin`. Reads the site plan `art/sculptures/sites/arrival.json` (copied to `oxide/data/RealmArrival/site.json`) and anchors it with one command. Gives no items or marks. | `RealmArrival.json` (reads `RealmArrival/site.json`; reads `RealmHerald.json` once to seed veterans) | **On a branch** (`team/arrival-plugin`). Compile-checked, mock-tested (554), exploit suite (arrival 87). Everything in game is UNVERIFIED (first-test plan in [`plugins/docs/RealmArrival.md`](../plugins/docs/RealmArrival.md)); design [`arrival-design.md`](arrival-design.md). Ships closed (`Open` false) with the open arch. |
| `RealmHeraldry.cs` | Realm's houses in the game's own guild system: keeps each bound guild's name and banner colours (field and emblem from `art/palette.json`; the great houses' own pairs reserved) in step with its house, so the colours show on the game's banners, crests, armour tints and name-tag icons; heads of houses choose colours (`/heraldry colours`, fee via `RealmTreasury.ChargeMarks`). Council elections each season among heads of houses (`/ballot stand`, `/vote`; deposits held by RealmTreasury; winners seated through `CrownAndConsequences.SeatElectedCouncillor`), and the monarch's referendums on decrees (`SetDecreeMandate`) and laws; one vote per Steam account with alt rules (account age, play time, time in the house, not protected, outlawed or exiled). Results to the Herald and the Chronicle (`vote_held`). | `RealmHeraldry.json` (reads `RealmHouses.json`) | **On a branch** (`team/heraldry`). Compile-checked, mock-tested (274), exploit suite (heraldry 42 + 33 against the real crown + 14 against the real treasury). Everything in game is UNVERIFIED (guide steps HR1 to HR11). Guide: [`plugins/docs/RealmHeraldry.md`](../plugins/docs/RealmHeraldry.md). |

Guides for the newer plugins are in [`plugins/docs/`](../plugins/docs/) (the four on branches bring their own guides); the original four are described in [`community/how-to-play.md`](community/how-to-play.md) and [`oxide-rok-api.md`](oxide-rok-api.md). Staff-only commands (`/sculpt`, `/paint`, `/ironbreaker`, `/sentinel`) are listed in `STAFF_COMMANDS` in `tools/realm-integration/check.mjs` and stay out of the `/realm` hub.

## 3. How the plugins call each other

Oxide only lets one plugin call **non-public instance methods** of another, by name, through a `[PluginReference] Plugin X` field and `X.Call("Method", args)`. A missing plugin or method returns `null`, and every caller treats `null` as "not available". `tools/realm-integration/check.mjs` checks every call below against the target's source: the method exists, is non-public, and takes that many arguments. It also checks that every reference names a real plugin. Argument types were reviewed by hand (for example `GetCouncilSeat` takes the `ulong` player id; `GetHouse` takes the id as a string).

| Caller | Calls into | Methods |
|---|---|---|
| CrownAndConsequences | RealmChronicle | `Log`, `SetCrown` |
| CrownAndConsequences | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers` |
| RealmArena | RealmChronicle, RealmEvents, RealmHerald, RealmHouses, RealmLaws, RealmLegendary | `Log`; `GetActiveEvents`, `GetTournamentEntrants`, `IsTruceActive`, `ScoreTournamentDuel`; `PopupsWanted`; `GetHouse`, `GetLiege`, `HasTreaty`; `ArenaTrialResult`; `IsBearer` (on a branch: `team/arena`) |
| RealmArena | RealmRenown, RealmSeasons, RealmSentinel, RealmTreasury, RealmWarden | `AddDeed`; `AwardHouse`; `IsSentinelFrozen`, `SentinelGrace`; `GetPurse`, `HoldMarks`, `PayFromHold`, `ReleaseHold`; `IsNewPlayerProtected`, `RaiseWardenAlert` (on a branch: `team/arena`) |
| RealmChronicle | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetKingSince`, `GetNextRebellionWindow` |
| RealmChronicle | RealmEvents | `GetNextEvent` |
| RealmChronicle | RealmHouses | `GetHouseSummaries` |
| RealmContracts | CrownAndConsequences | `GetOpenClaims`, `IsSwornToCrown` |
| RealmContracts | RealmChronicle | `Log` |
| RealmContracts | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `HasTreaty` |
| RealmDominion | CrownAndConsequences | `GetUtcOffsetHours`, `IsRebellionActive` (on a branch: `team/dominion`) |
| RealmDominion | RealmChronicle | `Log` (type `holding_taken`) (on a branch: `team/dominion`) |
| RealmDominion | RealmEvents | `IsTruceActive` (on a branch: `team/dominion`) |
| RealmDominion | RealmHerald | `PopupsWanted` (on a branch: `team/dominion`) |
| RealmDominion | RealmHouses | `GetHouse`, `GetHouseFounded`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `HasTreaty` (on a branch: `team/dominion`) |
| RealmDominion | RealmPainter | `RefreshBoards` (on a branch: `team/dominion`) |
| RealmDominion | RealmRenown | `AddDeed` (on a branch: `team/dominion`) |
| RealmDominion | RealmSeasons | `AwardHouse` (on a branch: `team/dominion`) |
| RealmDominion | RealmTreasury | `GrantHouseIncome` (new; on a branch: `team/dominion`) |
| RealmDominion | RealmWarden | `IsNewPlayerProtected`, `IsRaidHourNow` (on a branch: `team/dominion`) |
| RealmDynasties | CrownAndConsequences | `CmdClaim` (a chat-command method, not a documented API), `GetKingHouse`, `GetOpenClaims` |
| RealmDynasties | RealmChronicle | `Log` |
| RealmDynasties | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers`, `GetReputation`, `GetVassals` |
| RealmEvents | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetOpenClaims`, `GetUtcOffsetHours`, `IsRebellionActive` |
| RealmEvents | RealmChronicle | `Log` |
| RealmEvents | RealmHouses | `GetHouse`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `GetVassals`, `HasTreaty` |
| RealmEvents | RealmLegendary | `AwardEventPrize` (on a branch: `team/ironbreaker`) |
| RealmEvents | RealmSeasons | `AwardHouse` |
| RealmEvents | RealmWarden | `IsNewPlayerProtected` |
| RealmHerald | CrownAndConsequences | `GetKingHouse`, `GetKingName` |
| RealmHerald | RealmContracts | `HasContractHistory` |
| RealmHerald | RealmHouses | `GetHouse` |
| RealmHerald | RealmSeasons | `GetSeasonName` |
| RealmHouses | RealmChronicle | `Log` |
| RealmHouses | RealmHerald | `PopupsWanted` |
| RealmLaws | CrownAndConsequences | `GetCouncilSeat`, `GetKingHouse`, `IsRebellionActive` |
| RealmLaws | RealmChronicle | `Log` |
| RealmLaws | RealmContracts | `PardonOutlaw`, `ProclaimOutlaw` |
| RealmLaws | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers`, `HasTreaty` |
| RealmLaws | RealmArena | `StageTrial`, `IsDuelBlow` (on a branch: `team/arena`) |
| RealmLegendary | RealmChronicle | `Log` (on a branch: `team/ironbreaker`) |
| RealmLegendary | RealmHerald | `PopupsWanted` (on a branch: `team/ironbreaker`) |
| RealmLegendary | RealmHouses | `GetHouse`, `GetLiege`, `HasTreaty` (on a branch: `team/ironbreaker`) |
| RealmLegendary | RealmRenown | `AddDeed` (on a branch: `team/ironbreaker`) |
| RealmPainter | RealmDominion | `GetDominionBoard` (on a branch: `team/dominion`) |
| RealmPainter | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetKingSince`, `GetUtcOffsetHours` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmChronicle | `GetLastEventId` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmContracts | `GetBountyCount` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmEvents | `GetActiveEvents`, `GetNextEvent` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmHouses | `GetHouse` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmLaws | `GetCourtOutlaws` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmLegendary | `GetBearerName` (on a branch: `team/sign-painter`) |
| RealmPainter | RealmSeasons | `GetSeasonName`, `GetSeasonNumber`, `GetSeasonStandings` (on a branch: `team/sign-painter`) |
| RealmRavens | RealmChronicle | `Log` |
| RealmRavens | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `GetVassals`, `HasTreaty` |
| RealmRenown | CrownAndConsequences | `GetKingHouse`, `GetOpenClaims` |
| RealmRenown | RealmChronicle | `Log` |
| RealmRenown | RealmContracts | `IsOutlaw` |
| RealmRenown | RealmHouses | `GetHouse`, `GetLiege` |
| RealmRenown | RealmLaws | `GetCourtOutlaws` |
| RealmSeasons | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetOpenClaims` |
| RealmSeasons | RealmChronicle | `GetLastEventId`, `Log` |
| RealmSeasons | RealmHouses | `GetHouse`, `GetHouseFounded`, `GetHouseSummaries`, `GetMemberNames`, `HasTreaty` |
| RealmQuests | RealmChronicle | `Log` |
| RealmQuests | RealmEvents | `GetActiveEvents` |
| RealmQuests | RealmHerald | `PopupsWanted` |
| RealmQuests | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `HasTreaty` |
| RealmQuests | RealmRenown | `AddDeed`, `GetRenown`, `GetTitles` |
| RealmQuests | RealmSeasons | `AwardHouse`, `GetSeasonNumber` |
| RealmQuests | RealmSentinel | `SentinelItemSource` |
| RealmQuests | RealmTreasury | `GetPurse`, `RewardMarks` |
| RealmQuests | RealmWarden | `IsNewPlayerProtected` |
| RealmSentinel | RealmWarden | `RaiseWardenAlert` (on a branch: `team/anti-cheat`) |
| RealmCrafts | RealmChronicle, RealmDominion, RealmHerald, RealmHouses | `Log` (type `title_earned`); `GetHoldings`; `PopupsWanted`; `GetHouse` (on a branch: `team/crafts`) |
| RealmCrafts | RealmQuests, RealmRenown, RealmSeasons, RealmSentinel | `ReportQuestEvent`; `AddDeed`; `AwardHouse`; `SentinelItemSource` (on a branch: `team/crafts`) |
| RealmCrafts | RealmTreasury | `ChargeMarks`, `GetHold`, `GetLastPrice`, `GetPurse`, `GetTreasurySummary`, `HoldMarks`, `PayFromHold`, `ReleaseHold`, `RewardMarks` (on a branch: `team/crafts`) |
| RealmTreasury | RealmCrafts | `GetMarketFeeDiscount` (new; on a branch: `team/crafts`) |
| RealmTravel | CrownAndConsequences | `GetUtcOffsetHours`, `IsDecreeActive`, `IsHeldForRansom`, `IsRebellionActive`, `IsSwornToCrown` (on a branch: `team/travel`) |
| RealmTravel | RealmContracts, RealmLaws, RealmLegendary | `IsOutlaw`; `IsCourtOutlaw`, `IsExiled`; `IsBearer` (on a branch: `team/travel`) |
| RealmTravel | RealmEvents, RealmHerald, RealmHouses | `IsTruceActive`; `PopupsWanted`; `GetHouse`, `GetLiege`, `GetMembers`, `HasTreaty` (on a branch: `team/travel`) |
| RealmTravel | RealmRenown, RealmSeasons, RealmSentinel | `AddDeed`; `AwardHouse`, `GetSeasonNumber`; `SentinelGrace`, `SentinelItemSource` (on a branch: `team/travel`) |
| RealmTravel | RealmTreasury, RealmWarden | `ChargeMarks`, `GetPurse`; `IsInCombat`, `IsNewPlayerProtected`, `IsRaidHourNow` (on a branch: `team/travel`) |
| RealmWorld | RealmTreasury, RealmRenown, RealmSeasons | `RewardMarks`, `GetTreasurySummary`; `AddDeed`; `AwardHouse`, `GetSeasonStandings` (on a branch: `team/world-events`) |
| RealmWorld | RealmChronicle, RealmHouses, RealmEvents | `Log` (type `census_taken`), `GetLastEventId`; `GetHouse`, `GetLiege`, `HasTreaty`, `GetHouseSummaries`; `GetActiveEvents`, `GetNextEvent`, `IsTruceActive` (on a branch: `team/world-events`) |
| RealmWorld | RealmArena, RealmTravel, RealmQuests, RealmWarden | `IsInDuel`; `IsTravelling`, `CancelJourney`; `ReportQuestEvent`; `IsNewPlayerProtected` (on a branch: `team/world-events`) |
| RealmWorld | RealmSculptor, RealmPainter, RealmHerald, RealmSentinel, CrownAndConsequences | `PlaceSculptureAt`, `RemoveSculpture` (new); `RefreshBoards`; `PopupsWanted`; `SentinelItemSource`; `GetKingName`, `GetKingHouse` (on a branch: `team/world-events`) |
| RealmPainter | RealmWorld | `GetWorldBoard` (on a branch: `team/world-events`) |
| RealmHeraldry | CrownAndConsequences, RealmChronicle, RealmHerald | `GetCouncilSeats`, `GetDecreeList`, `SeatElectedCouncillor`, `SetDecreeMandate` (new); `Log` (type `vote_held`, new); `PopupsWanted` (on a branch: `team/heraldry`) |
| RealmHeraldry | RealmHouses, RealmTreasury | `GetHouse`, `GetHouseFounded`, `GetHouseLeader`, `GetHouseSummaries`, `GetMembers`; `ChargeMarks`, `HoldMarks`, `ReleaseHold` (on a branch: `team/heraldry`) |
| RealmHeraldry | RealmSeasons, RealmRenown, RealmQuests | `AwardHouse`, `GetSeasonName`, `GetSeasonNumber`; `GetRenown`; `ReportQuestEvent` (on a branch: `team/heraldry`) |
| RealmHeraldry | RealmWarden, RealmContracts, RealmLaws, RealmTravel | `IsNewPlayerProtected`; `IsOutlaw`; `GetActiveLaws`, `IsCourtOutlaw`, `IsExiled`; `GetDiscoveredCount` (on a branch: `team/heraldry`) |
| RealmArrival | RealmSentinel, RealmTravel, RealmWarden | `SentinelGrace`; `CancelJourney`, `GetDiscoveredCount`, `IsTravelling`; `IsNewPlayerProtected`, `IsInCombat`, `RaiseWardenAlert`, `GetProtectionMinutesLeft` (new) (on a branch: `team/arrival-plugin`) |
| RealmArrival | RealmHouses, CrownAndConsequences, RealmEvents, RealmArena | `GetHouseSummaries`, `GetMembers`, `GetHouseLeader`, `GetLiege`, `GetHouse`; `GetKingName`, `GetKingHouse`; `GetNextEvent`; `IsDuelBlow` (on a branch: `team/arrival-plugin`) |
| RealmArrival | RealmQuests, RealmRenown, RealmHerald | `ReportQuestEvent` (custom `arrival_*` subjects), `GetStoryProgress`; `AddDeed` (`written`, off by default); `PopupsWanted` (on a branch: `team/arrival-plugin`) |
| RealmHerald, RealmQuests, RealmTravel | RealmArrival | `ArrivalStage` (new): the welcome, the first hint, the tale's first news and the kit hint wait while RealmArrival owns a newcomer (on a branch: `team/arrival-plugin`) |
| RealmWarden | RealmArrival | `OwnsArrival` (new): the join "Welcome" waits while RealmArrival owns a newcomer (on a branch: `team/arrival-plugin`) |
| RealmStats | RealmHouses | `GetHouse` |
| RealmTreasury | CrownAndConsequences | `GetCouncilSeat`, `IsSwornToCrown` |
| RealmTreasury | RealmChronicle | `Log` |
| RealmTreasury | RealmHouses | `GetHouse`, `GetHouseFounded`, `GetHouseLeader`, `GetMembers` |
| RealmWarden | CrownAndConsequences | `IsRebellionActive` |

RealmSculptor makes no cross-plugin calls. With the four branches merged there are 19 plugins and 137 call sites; on `claude/great-maxwell-wrksvt` today, 15 plugins and 113.

**Court outlawry reaches the bounty board.** RealmLaws offered court outlaws to RealmContracts, but RealmContracts had no method to receive them, so the calls did nothing. RealmContracts now has two non-public methods:

- `bool ProclaimOutlaw(string playerId, string name, int hours, string by)` adds or extends an outlaw entry marked as placed by the court. A court sentence follows a trial and has its own quotas, so the crown's cooldowns and `MaxOutlaws` do not apply to it. It never shortens a sentence and writes no second Chronicle line.
- `bool PardonOutlaw(string playerId)` lifts only court-placed outlawry and withdraws open bounties on that player, as a crown pardon does. A crown proclamation stays until the crown pardons it.

`tools/realm-integration/cross-tests/run.sh` compiles RealmLaws and RealmContracts together, routes `Call` the way Oxide does, and checks the whole path: sentence, bounty with escrow, pardon, refund, and the data-file round trip (25 checks).

**Plugins that read other plugins' data files.** RealmArrival reads `RealmHerald.json` once to seed known veterans, and for `/arrival admin check` reads `RealmQuests.json` (the `the_hearth` mark), `RealmTravel.json` (waystones), `RealmDominionMap.json`, and the configs `RealmLaws.json` (the Hearth zone) and `RealmArena.json` (arena zones); it never writes any of them (on `team/arrival-plugin`). RealmHeraldry reads `RealmHouses.json` (each house's bound guild and members' `Joined` dates; on `team/heraldry`). RealmRenown reads `RealmContracts.json`, `RealmHouses.json` and `RealmChronicle.json` for deeds. RealmSeasons reads `RealmChronicle.json` for new entries after the last event id it has seen (`GetLastEventId`). A field rename in those files makes the reader see nothing (it does not misaward); RealmRenown reports it in `/renown admin status`.

**Load order.** Oxide does not define the order between plugins' hooks. Damage, capture and placement blocks in RealmLaws, RealmEvents (truce) and RealmWarden can therefore meet CrownAndConsequences' capture handling in either order (see each guide). RealmLaws ships its capture block switched off for this reason. RealmLegendary scales `Damage.Amount` in the same damage hooks: a blow another plugin zeroed stays zero either way, but which plugin sees a blow first is not defined. RealmSculptor's protection and RealmWarden's raid hours both act on `OnCubeTakeDamage`; either one blocking is enough.

## 4. Services and apps next to the server

| System | Where | What it does | Connects to | Proof so far |
|---|---|---|---|---|
| Chronicle service | `chronicle/` | Read-only HTTP service on `127.0.0.1:8787`: `/api/state`, `/api/events`, the OBS `/overlay` and the public `/realm` page. | Reads `RealmChronicle.json` and `RealmState.json`. Feeds streamkit, the bot and the launcher. | Tested here (11 tests, including the three-way type sync; 15 with `team/web-and-broadcast`, which adds the art pack, local fonts and the coronation and rebellion moments). UNVERIFIED in OBS (PT6.1). |
| Realm Steward and Realm | `launcher/` | Two Electron apps from one codebase. Steward: setup wizard, up to four servers, live admin console, Court, Connection Doctor, backups, restarts, Go Public, signed server list, Discord herald. Realm: the player app with the signed list, status and Join through Steam; on `team/player-launcher` it becomes one screen with Play or Install, signed news (`lib/news.js`) and signed updates (`lib/updater.js`). | Starts `ROK.exe`, holds its admin console, runs the Chronicle in-process, deploys `plugins/` (not yet the sculptures or sign art: ROADMAP STW-1). | Tested here (`npm run check` and 209 unit tests; 240 with `team/player-launcher`; Electron walk-throughs on Linux). An earlier Steward build ran on the owner's PC; 1.0.0 and the player app are UNVERIFIED on Windows (PT0, PT5). |
| Portal | `portal/` | Static website generator: home, Chronicle, houses, Hall of Kings, how to play, rules, lore, feed, `status.json`. | Reads `oxide/data` (Chronicle, State, Houses, Crown, Seasons, Legends, Events). | Tested here (28 tests; 35 with `team/web-and-broadcast`, which adds the art pack, link previews and phone layouts). Hosting is the owner's choice (ROADMAP WEB-2). |
| Discord bot | `bot/` | `/realm` slash commands, a live status message, optional house roles. | Chronicle API, `RealmHouses.json`, `RealmEvents.json` (data and config), labels from `common.js`. | Tested here with fakes (85 tests; 87 plus 3 skipped with `team/web-and-broadcast`, which adds art thumbnails and needs Attach Files). No real Discord call made (UNVERIFIED, PT6.3). |
| Stream scenes | `streamkit/` | Five OBS browser scenes: War Board, Throne Room, Breaking News, Countdown, Starting Soon. | Relays the Chronicle API; schedule from `schedule.json`. | Tested here (27 tests, Chromium screenshots; 30 with `team/web-and-broadcast`). UNVERIFIED in OBS itself (PT6.2). |
| Analytics | `analytics/` | Builds one offline HTML dashboard from RealmStats day files. | Reads `oxide/data/RealmStats`. | Tested here (14 tests, plus 89 plugin checks). |
| Ops | `ops/` | Hosting guide, SteamCMD install and update, offsite backups, restore drills, uptime monitor, disaster recovery. | Calls `server/New-TestServer.ps1` and `server/Install-Oxide.ps1`; posts to a Discord webhook. | Behaviour-tested under PowerShell 7 on Linux by its team; parse-checked for 5.1. Never run on Windows. |
| Server scripts | `server/` | PowerShell 5.1 scripts for the test copy: copy, start, Oxide install and rollback, deploy plugins, backups, mod keys, `Set-Mood.ps1`. | Used by `Realm.bat`, ops and the smoke test. | Parse-checked in CI. Never run on Windows. |
| Atmosphere moods | `mods/` | Mood presets (Long Winter, Blood Moon, Golden Summer, Storm Season, Ashfall, Grim but Readable) through the game's own `Mods\*.cfg` override system, and a rotation plan. | `server/Set-Mood.ps1`. | Colour maths from decompiled formulas only. Nothing seen in game (UNVERIFIED). |
| ROK simulator | `tools/rok-sim/` | A fake dedicated server for Linux and CI: admin console, A2S, logs, configs, Chronicle data. | Launcher tests and CI. | Tested here (62 tests). Not compared with a real `ROK.exe`. |
| Plugin compile check | `tools/plugin-compile-check/` | Compiles every plugin at C# 3 against the real 2.0.3867 metadata. | CI `plugins` job. | 0 errors. |
| Integration check | `tools/realm-integration/` | Static check of cross-plugin calls, Chronicle types, chat-command names, the chat style, the `/realm` catalogue and popups (`check.mjs`), and the RealmLaws/RealmContracts behaviour test (`cross-tests/`). | CI `plugins` job. | Tested here (15 plugins, 113 calls, 36 commands, 42 Chronicle types: OK; 11 tests; cross-tests 25). |
| Exploit review | `tools/exploit-review/` | Regression suites for every exploit found and fixed: treasury, laws-contracts, renown, crown-houses, dynasties, warden, ravens, events, seasons, chronicle (`legendary` on `team/ironbreaker`; `sentinel` has its own runner on `team/anti-cheat`). | CI `plugins` job. | Tested here (all suites pass). |
| Sculptor and painter tools | `art/tools/sculptor/`, `art/tools/painter/` | Build, preview and check the sculptures; render the paintings, glyph atlases and the `RealmPainterArt.json` bundle. | Feed RealmSculptor and RealmPainter. | **On branches** (`team/sculptor`: 16 tests; `team/sign-painter`: 12 tests). Not in CI yet (ROADMAP QA-1). |
| CI | `.github/workflows/ci.yml` | Chronicle; launcher check and tests; plugins (compile check, integration check, cross-tests, every plugin logic suite, RealmStats tests, exploit review); portal; art check and tests; saga, legal and staff tools; Windows installers through `Build-Realm.bat`; PowerShell parse; simulator; bot; streamkit and analytics; ops and Set-Mood under PowerShell 7. | GitHub Actions, read-only token. | Runs on pushes to GitHub (a failing run was fixed in 142dc82). Not yet in CI: the sculptor and painter tool tests and checks, the `sentinel` exploit suite (ROADMAP QA-1, SEN-1). |

## 5. Content, brand and policy

| System | Where | What it is | Proof so far |
|---|---|---|---|
| Lore and community docs | `docs/community/` | The realm of Ostreval, its six great houses (Varrow, Ashgrove, Corvane, Dunmere, Halloran, Merrin), rules, how to play, streamer kit, launch plan. | Written; `how-to-play.md` is being updated by another team. |
| Sculptures and sign art | `art/sculptures/`, `art/paintings/` | **On branches.** 11 block sculptures (Herald's Pillar, the Ironbreaker, the Old Throne, six house monuments, the Tournament Arch, a shape test) with previews; 25 sign paintings and the live-board fonts and sprites. | Checked by their tools' tests; nothing seen in game (PT2). |
| Art and brand | `art/`, `docs/brand.md` | Sigils, banners, shields, logos, event icons, Discord and social art, palette, and a build that checks colours against the overlay's house dyes. | `art/tools` tests (24) and `build.mjs check --skip-png` (118 SVGs) pass. Every Chronicle type has an icon mapping. With `team/web-and-broadcast` the portal, overlay, scenes and bot use the pack (copies kept in step by `portal/scripts/sync-art.mjs --check`). How the PNGs look on Discord is UNVERIFIED (PT6.5). |
| Saga (season 1 run-sheets) | `docs/saga/` | "The Hollow Crown": four acts of run-sheets, proclamations, locations, legends, an eight-week calendar, and a checker that every command they use exists. | Checker: 0 errors, 14 tests. Run-time behaviour UNVERIFIED; Acts I and II are rehearsed in the closed beta (ROADMAP EVT-3). |
| Legal and staff | `docs/legal/`, `docs/community/ops/` | EULA checklist, rights-holder letter, privacy notice template, data deletion, monetisation guardrails, staff roles, moderation handbook, appeals, incident response, and tools to find a player's data and check permissions. | Tools tested (12). The EULA text itself has not been read; every clause row is open, and reading it blocks 1.0 (ROADMAP COM-6). Not legal advice. |
| Roadmap | [`ROADMAP.md`](ROADMAP.md) | The production plan to 1.0 and Seasons 2 to 4: the quality bar, milestones and exit criteria, the backlog by area, the ordered play-test checklist for every UNVERIFIED item, risks and volunteer roles. | Updated at every milestone gate. |

## 6. Open integration items

These cross team boundaries and are not done. Each has an ID in [`ROADMAP.md`](ROADMAP.md) section 3.

- **Merging this wave** (M0): the four plugin branches, `team/player-launcher` and `team/web-and-broadcast`, with the merge notes in [`HANDOFF.md`](HANDOFF.md#in-flight): one `STAFF_COMMANDS` list, `PENDING_PLUGINS` emptied, one RealmHerald test exemption (SRV-1, SRV-2).
- Steward's deploy and `server/Deploy-Plugins.ps1` copy only `.cs` files; RealmSculptor needs `art/sculptures/*.json` in `oxide/data/RealmSculptor/`, RealmPainter needs `art/paintings/RealmPainterArt.json` in `oxide/data/`, and RealmArrival needs `art/sculptures/sites/arrival.json` as `oxide/data/RealmArrival/site.json` (STW-1).
- `launcher/lib/moderation.js` lists admin commands per plugin but not RealmLaws' `/court admin` and `/law zone`, nor `/paint`, `/sculpt`, `/ironbreaker` and `/sentinel` (STW-2). Steward does not show RealmSentinel's feed yet (SEN-5).
- Plugins that pay items do not yet tell RealmSentinel (`SentinelItemSource`), and plugins that move players do not call `SentinelGrace` (SRV-3).
- RealmContracts, RealmLaws and RealmLegendary do not yet call `RealmPainter.RefreshBoards`; boards catch up within their refresh time (SRV-4).
- RealmRenown has no `ironbreaker` deed, so RealmLegendary's `AddDeed` call adds nothing (SRV-5).
- The Ironbreaker logs claims as `title_earned` and losses as `event_ended`; dedicated `blade_claimed` / `blade_lost` types are planned for Season 2 (SRV-6).
- RealmDynasties presses blood claims by calling CrownAndConsequences' `/claim` command method. A small non-public claim API in CrownAndConsequences would be cleaner (SRV-7).
- RealmRenown reads other plugins' data files; a non-public `AddDeed` call from those plugins would remove the coupling (SRV-8).
- RealmWarden cuts external alert kinds at 24 characters, so `impersonation_near` shows truncated (SRV-10).
- `launcher/renderer/heraldry.js` keeps its own copy of the event, title and house tables that `chronicle/public/assets/realm-art.js` also holds (PLA-5).
- Steward has no forms yet to publish the signed `news.json` and `update.json` the new player app reads (STW-4).
- The sculptor and painter tool tests and checks, and the `sentinel` exploit suite, are not in CI (QA-1, SEN-1).
- `docs/in-game-art.md` ends with a pointer to a standalone game in a modern engine. Realm has no such track; that paragraph should go (its owner).
- Everything in the play-test checklist (`ROADMAP.md` section 5) still has to be done on the real Windows server.

## 7. How to re-check everything

```
bash tools/plugin-compile-check/check.sh                 # 0 errors at C# 3
node tools/realm-integration/check.mjs --commands         # wiring, Chronicle types, command names
node --test tools/realm-integration/check.test.mjs
bash tools/realm-integration/cross-tests/run.sh           # RealmLaws + RealmContracts, mocks
for t in plugins/docs/*/logic-tests/run.sh analytics/plugin-tests/run.sh; do bash "$t"; done
bash tools/exploit-review/run.sh                           # exploit regression suites
node art/tools/build.mjs check --skip-png                  # art pack: palette, contrast, trademark guard
(cd chronicle && npm test); (cd portal && npm test); (cd bot && npm test); (cd streamkit && npm test)
(cd analytics && npm test); (cd tools/rok-sim && npm test); (cd launcher && npm test); (cd art/tools && npm test)
node --test docs/saga/tools/check-saga.test.mjs docs/legal/tools/*.test.mjs docs/community/ops/tools/*.test.mjs
```

Once the branches are merged, also:

```
node --test art/tools/sculptor/test/*.test.mjs && node art/tools/sculptor/cli.mjs check
node --test art/tools/painter/test/*.test.mjs && node art/tools/painter/paint.mjs check
bash tools/exploit-review/sentinel/run.sh
node portal/scripts/sync-art.mjs --check
```

`ops/tests/Run-OpsTests.ps1` and `mods/presets/tests/Test-SetMood.ps1` need PowerShell 7 (`pwsh`).
