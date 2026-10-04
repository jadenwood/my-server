# Realm systems map

One page for every part of Realm: what it does, where it lives, how it connects to the rest, and how far it has been proven. Read this before changing anything that crosses a folder boundary.

**The short version of the status (2026-10-04).** `claude/great-maxwell-wrksvt` holds 27 plugins: the wave-3 and seven wave-4 branches are merged, and so are `team/arrival-plugin` (RealmArrival, the new-player arrival) and, with it, `team/steward-integration` (Realm Steward's screens for the plugins) ([`HANDOFF.md`](HANDOFF.md#wave-4-the-realms-new-systems)). Every piece has automated tests or checks that pass here, on Linux. On the owner's real Windows 11 server (2026-10-02) the 14 plugins of that day compiled and loaded under Oxide, Season 1 started by itself, and the owner joined and reached character creation ([`HANDOFF.md`](HANDOFF.md)). **No plugin feature has been seen working in game yet**, and no chat reply has been checked. The plugins compile against the real Oxide 2.0.3867 and game DLL metadata and are tested against mocks; the Windows apps and PowerShell scripts were built or parse-checked, and only an earlier Steward build has run on Windows. The ordered proof on real hardware is the play-test checklist in [`ROADMAP.md`](ROADMAP.md) section 5, which gathers [`smoke-test.md`](smoke-test.md) and every guide's smoke steps. Anything marked UNVERIFIED below has not been seen working.

Status words used here:

| Word | Meaning |
|---|---|
| **Compile-checked** | Compiles with 0 errors at C# 3 against the shipped Oxide 2.0.3867 and patched game DLLs (`tools/plugin-compile-check/check.sh`). |
| **Mock-tested** | Behaviour tests run the real source against stand-ins for the game and Oxide. They prove the plugin's own logic, not the game's behaviour. |
| **Tested here** | Automated tests pass in this Linux environment (Node or .NET). |
| **UNVERIFIED** | Not seen working on the real thing. |
| **On a branch** | Finished and committed on the named team branch, not yet merged into `claude/great-maxwell-wrksvt`. On 2026-10-04 no branch in this map is in this state. Drop the note when it is merged. |

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

The **closed list of Chronicle event types** is the main contract between the parts. It lives in three files that a test keeps in step: `plugins/RealmChronicle.cs` (`KnownTypes`), `chronicle/server.js` (`EVENT_TYPES`) and `chronicle/public/assets/common.js` (`TYPE_META`). There are 47 types on `claude/great-maxwell-wrksvt`, the newest `holding_taken` (RealmDominion), `census_taken` (RealmWorld), `vote_held` (RealmHeraldry), and `blade_claimed` and `blade_lost` (RealmLegendary, from `team/steward-integration`). RealmArrival adds none. A new type needs all three; `tools/realm-integration/check.mjs` fails if they differ, if a plugin logs a literal type that is not registered, or if a `plugins/docs/*/EVENTS.json` request is still waiting. The portal (`portal/lib/model.mjs`), the Discord herald (`launcher/lib/discord.js`), the bot (reads `common.js` as text) and the art event map (`art/icons/event-map.json`) carry the same labels.

## 2. Server plugins (`plugins/`)

All are Oxide C# plugins, C# 3 syntax, deployed together by Realm Steward or `server/Deploy-Plugins.ps1`. All 27 are **compile-checked**. The 14 plugins present on 2026-10-02 loaded on the owner's server; no plugin feature has been seen working in game. Player commands: [`realm-commands.md`](realm-commands.md). The in-game test for every UNVERIFIED item is in [`ROADMAP.md`](ROADMAP.md) section 5 (PT rows).

| Plugin | What it does | Writes | Proof so far |
|---|---|---|---|
| `RealmHouses.cs` | Houses, ranks, oaths of fealty, treaties, oathbreaker and treaty-breaker marks. The base API every other plugin reads. | `RealmHouses.json` | Compile-checked, mock-tested (57), exploit suite (crown-houses 16). Loaded on the real server. |
| `CrownAndConsequences.cs` | Tracks the monarch on the Old Throne; decrees, council, claims and scheduled rebellion windows, tax cap, bounded ransom. | `CrownAndConsequences.json` | Compile-checked. Loaded on the real server. Throne capture, the throne gate, Royal Stores and ransom release are UNVERIFIED (PT1.8, PT1.9, PT3.6, PT3.7). With RealmHeraldry: elected council seats the monarch cannot dismiss in their term, and decree mandates from referendums (non-public `GetCouncilSeats`, `GetCouncil`, `SeatElectedCouncillor`, `GetDecreeList`, `SetDecreeMandate`). |
| `RealmChronicle.cs` | The event log (`Log`) and the realm snapshot. Rejects unknown types; per-source flood budget. | `RealmChronicle.json`, `RealmState.json` | Compile-checked; type list tested against the Chronicle service; exploit suite (chronicle 27). Loaded on the real server. |
| `RealmContracts.cs` | Bounties on public enemies, deliveries, mercenary work, with real item escrow. Takes court outlawry from RealmLaws. | `RealmContracts.json` | Compile-checked; the court hand-off is mock-tested (`tools/realm-integration/cross-tests`, 25); exploit suite (laws-contracts 24). Item escrow in game is UNVERIFIED (PT1.6). |
| `RealmSeasons.cs` | Numbered seasons, house standings, the Hall of Kings that survives wipes. | `RealmSeasons.json`, `RealmLegends.json` | Compile-checked; exploit suite (seasons 14). Season 1 auto-started on the real server. Guide: [`plugins/docs/RealmSeasons.md`](../plugins/docs/RealmSeasons.md). |
| `RealmEvents.cs` | Crown Night, Royal Tournament, King's Hunt, Truce of the Realm, with heralds and item prizes. | `RealmEvents.json` | Compile-checked, mock-tested (89), exploit suite (events 26). Truce damage blocking is UNVERIFIED (PT3.11). A prize hook for RealmLegendary; with RealmArena, the Royal Tournament can run as a bracket (`GetTournamentEntrants`, `ScoreTournamentDuel`). |
| `RealmArena.cs` | Duels to the first fall (the killing blow is turned aside: no death, no loot) in a ring no one else may enter; stakes in marks held by RealmTreasury's escrow; an Elo ladder and a weekly Champion of the Ring (RealmRenown titles); team duels; bracket tournaments and a bracket for the Royal Tournament (RealmEvents `ScoreTournamentDuel`); trial by combat for RealmLaws; Hearth Dice and Twenty-One for marks with no house edge. | `RealmArena.json` | Compile-checked, mock-tested with the real treasury (312), exploit suite (arena 52, arena-events 15, arena-laws 12). Everything in game is UNVERIFIED (guide steps U1 to U19). Guide: [`plugins/docs/RealmArena.md`](../plugins/docs/RealmArena.md). |
| `RealmLaws.cs` | Laws and zones, a public crime ledger, accusations, jury trials, trial by combat, fines, outlawry, exile, pardons. | `RealmLaws.json` | Compile-checked, mock-tested (90). Blocking acts in game is UNVERIFIED (PT3.10). With RealmArena loaded (`CombatInArena`), trial by combat is fought in its ring to the first fall, and duel blows are exempt from the peace law (`IsDuelBlow`; PT3.21). |
| `RealmDynasties.cs` | Bloodlines, heirs, succession, prestige, blood claims after a monarch falls, titles bestowed by the crown. | `RealmDynasties.json` | Compile-checked, mock-tested (92), exploit suite (dynasties 3). Pressing a claim calls CrownAndConsequences' `/claim` command method (UNVERIFIED in game, PT3.18). |
| `RealmRenown.cs` | Renown and infamy from deeds, earned titles worn in chat. | `RealmRenown.json` | Compile-checked, mock-tested (108), exploit suite (renown 11). The chat prefix is UNVERIFIED (PT3.17). |
| `RealmTreasury.cs` | Marks (a ledger currency), purses, a market with escrow, house vaults, the crown's treasury, mint and tithe. | `RealmTreasury.json`, ledger logs | Compile-checked, mock-tested (118), exploit suite (treasury 26, plus the treasury cases of the dominion, quests, travel, crafts and heraldry suites). Game-tax observation is UNVERIFIED (PT3.9). Wave 4 added non-public money paths, each counted by `/treasury audit`: `GrantHouseIncome` (RealmDominion's payday into vaults), `RewardMarks` (rewards from RealmQuests, RealmWorld and RealmCrafts, with per-call, per-day and reserve limits), `ChargeMarks` (purse to the crown's treasury: tolls, colour fees), and holds `HoldMarks`, `PayFromHold`, `ReleaseHold`, `GetHold` (duel stakes, commissions, ballot deposits); the market fee asks `RealmCrafts.GetMarketFeeDiscount`. |
| `RealmRavens.cs` | Letters between players and houses, interception by spies, moderated anonymous rumours. | `RealmRavens.json` | Compile-checked, mock-tested (95), exploit suite (ravens 6). |
| `RealmWarden.cs` | New-player protection, raid hours, combat-log flags, reports, mutes, name rules; evidence for admins. | `RealmWarden.json`, logs | Compile-checked, mock-tested (140), exploit suite (warden 4). Raid-hour blocking is proven only for block health (PT3.5). |
| `RealmStats.cs` | Pseudonymous daily player statistics with opt-out. | `RealmStats/` day files | Compile-checked, mock-tested (89). |
| `RealmCourt.cs` | Puts `/realm.save` and `/realm.players` into the game's own command table for Realm Steward's console. | nothing | Compile-checked. UNVERIFIED at run time (PT0.8). |
| `RealmHerald.cs` | Welcome, first steps, the `/realm` hub (every player command by subject), tips, message of the day, popups; the one chat style every plugin follows. | `RealmHerald.json` | Compile-checked, mock-tested (102). Popups from a dedicated server and chat colours are UNVERIFIED (PT1.1 to PT1.4). Guide: [`plugins/docs/RealmHerald.md`](../plugins/docs/RealmHerald.md). |
| `RealmSculptor.cs` | Staff-only `/sculpt`: places, paints, protects, repairs and removes Realm's monuments built from the game's own blocks and colours (23 sculptures and one site plan in `art/sculptures/`). Non-public `PlaceSculptureAt` and `RemoveSculpture` let RealmWorld raise festival decorations. | `RealmSculptor.json`, reads `RealmSculptor/*.json` | Compile-checked, mock-tested (193, including the real DLL's metadata for every reflected member). Whether server-placed blocks and colours show for clients is UNVERIFIED (PT2.10 to PT2.15). |
| `RealmPainter.cs` | Staff-only `/paint`: writes Realm's art into painted signs and keeps live boards (Chronicle, wanted, standings, proclamation, event, Ironbreaker, notice, and from wave 4 the holdings board `/paint dominion` and the world boards `/paint world clue <n>` and `/paint world festival`). | `RealmPainter.json`, reads `RealmPainterArt.json` | Compile-checked, mock-tested (126, plus 4 PNG decode checks). Whether a server-set picture reaches clients is UNVERIFIED (PT2.2 to PT2.9, PT3.2). |
| `RealmLegendary.cs` | The Ironbreaker: exactly one legendary blade, won at the Royal Tournament, taken by the bearer's slayer; staff `/ironbreaker`. | `RealmLegendary.json` | Compile-checked, mock-tested (142), exploit suite (legendary 48). Item identity and damage scaling in game are UNVERIFIED (PT2.16, PT3.12, PT4.3). Logs claims as `blade_claimed` and losses as `blade_lost` (from `team/steward-integration`; before, `title_earned` and `event_ended`). |
| `RealmSentinel.cs` | Server-side cheat watch: movement, combat, items, floods and staff-name impersonation scored with evidence; alert, freeze, kick, ban on confirm; staff `/sentinel`. Never touches the game's own anti-cheat. Ships in watch mode. | `RealmSentinel.json`, `RealmSentinelFeed.json`, logs | Compile-checked, mock-tested (173), exploit runner (24). Every limit is UNVERIFIED until tuned in game (PT1.16, PT3.13 to PT3.15, PT7.10). |
| `RealmDominion.cs` | Territorial war: named holdings (villages, keep, mine, harbour, crossroads) that houses take by holding the field in the War Hours (paused in truces, rebellions and shut raid hours); garrisons, daily marks into house vaults (`RealmTreasury.GrantHouseIncome`) and season points, `holding_taken` Chronicle entries, the `/paint dominion` board; `/dominion`. | `RealmDominion.json`, `RealmDominionMap.json` (for the portal and Chronicle; schema in the guide) | Compile-checked, mock-tested (196), exploit suite (dominion 37 + 13). Everything in game is UNVERIFIED (guide steps D1 to D12). Guide: [`plugins/docs/RealmDominion.md`](../plugins/docs/RealmDominion.md). |
| `RealmQuests.cs` | Daily and weekly tasks, the Season 1 story (The Hollow Crown, 4 acts, 17 steps), 68 deeds in five kinds with tiers, weekly house goals; `/quest`, `/achievements`. Content is JSON in `plugins/docs/RealmQuests/content/` (deployed to `oxide/data/RealmQuests/`). Pays marks (RealmTreasury `RewardMarks`), renown and titles (RealmRenown deeds), house season points. | `RealmQuests.json` | Compile-checked, mock-tested (298), exploit suite (quests 45, plus 13 for `RewardMarks`). Everything at run time is UNVERIFIED (guide QS1 to QS18). Guide: [`plugins/docs/RealmQuests.md`](../plugins/docs/RealmQuests.md). |
| `RealmCrafts.cs` | Professions and mastery: woodcutting, mining, foraging, hunting, smithing, carpentry, tailoring and cooking, with XP read from the game's own container, harvest, damage and crafting events; ranks and the Guildmaster title (RealmRenown); perks the server can grant (bonus yield and extra items into the packs, a lower market fee through RealmTreasury); a weekly Master Crafter; house workshops that pool members' XP for perks and season points; a commission board with marks held in RealmTreasury holds; `/craft`. | `RealmCrafts.json` | Compile-checked, mock-tested with the real treasury (304), exploit suite (crafts 71). Everything in game is UNVERIFIED (smoke steps C1 to C16 in [`plugins/docs/RealmCrafts.md`](../plugins/docs/RealmCrafts.md)). |
| `RealmTravel.cs` | Waystones players unlock by walking to them, `/travel` between them for a toll in marks (to the crown's treasury), `/home` in your own crest zone, `/road` directions in chat, and kits (newcomer, daily house, season). No journey in a fight, with a captive, as an outlaw, with the Ironbreaker, or near the throne in a rebellion; an arrival shield against campers. | `RealmTravel.json` | Compile-checked, mock-tested (301), exploit suite (travel 69 + 11). The server-side teleport (`CharacterTeleport.Teleport`, the game's own `/tp` call), kit items and crest checks are UNVERIFIED in game (smoke steps T1 to T14 in [`plugins/docs/RealmTravel.md`](../plugins/docs/RealmTravel.md)). |
| `RealmWorld.cs` | The living world between RealmEvents' events: treasure hunts (riddles in chat and on painted signs, a finder's chest), the Blood Moon night (fair kills for renown and house points, hungrier beasts), the Merchant Caravan (an escort between two places or waystones, raiders and their price), Wandering Legends (a tougher marked creature, or the first of its kind slain in a region), the Harvest Fair and Midwinter (house offerings, RealmSculptor decorations) and the weekly Census (`census_taken`). A schedule that never meets RealmEvents; `/world`, `/treasure`, `/caravan`, `/festival`. | `RealmWorld.json` | Compile-checked, mock-tested (326), exploit suite (world 48). Everything in game is UNVERIFIED (smoke steps W1 to W15 in [`plugins/docs/RealmWorld.md`](../plugins/docs/RealmWorld.md)); moods `harvest-festival` and `midwinter` in `mods/presets/`. |
| `RealmArrival.cs` | The Gate of the Unwritten: a newcomer's first minutes, from the game's own character screen on the raft to the Hearth fire. At the Finish click the game's loader covers a move onto one of six arrival stones in the Gatehouse (`CharacterTeleport.Teleport`, or the spawn provider behind a switch, with two arrival checks and road mode as the fallback); two lore lines, then the gate opens on the gold line (a 5 x 6 portcullis of real blocks sinking row by row, or an open arch) and the Hearth's ember band flares; six house banners on the avenue with live house data and an optional pledge (looking to a house, told to its members, never a join); the fire lines in order after RealmQuests' and RealmTravel's own; Written on `/quest`. Variants for veterans on a fresh world, resume after a log-off, Hearth's Mercy respawns, a sanctuary, eviction, a release shield and an arrival-zone guard; `/arrival` (`skip`, `tour`) and `/arrival admin`. Reads the site plan `art/sculptures/sites/arrival.json` (copied to `oxide/data/RealmArrival/site.json`) and anchors it with one command. Gives no items or marks. | `RealmArrival.json` (reads `RealmArrival/site.json`; reads `RealmHerald.json` once to seed veterans) | Compile-checked, mock-tested (559), exploit suite (arrival 87). Everything in game is UNVERIFIED (first-test plan in [`plugins/docs/RealmArrival.md`](../plugins/docs/RealmArrival.md)); design [`arrival-design.md`](arrival-design.md). Ships closed (`Open` false) with the open arch. |
| `RealmHeraldry.cs` | Realm's houses in the game's own guild system: keeps each bound guild's name and banner colours (field and emblem from `art/palette.json`; the great houses' own pairs reserved) in step with its house, so the colours show on the game's banners, crests, armour tints and name-tag icons; heads of houses choose colours (`/heraldry colours`, fee via `RealmTreasury.ChargeMarks`). Council elections each season among heads of houses (`/ballot stand`, `/vote`; deposits held by RealmTreasury; winners seated through `CrownAndConsequences.SeatElectedCouncillor`), and the monarch's referendums on decrees (`SetDecreeMandate`) and laws; one vote per Steam account with alt rules (account age, play time, time in the house, not protected, outlawed or exiled). Results to the Herald and the Chronicle (`vote_held`). | `RealmHeraldry.json` (reads `RealmHouses.json`) | Compile-checked, mock-tested (274), exploit suite (heraldry 42 + 33 against the real crown + 14 against the real treasury). Everything in game is UNVERIFIED (guide steps HR1 to HR11). Guide: [`plugins/docs/RealmHeraldry.md`](../plugins/docs/RealmHeraldry.md). |

Guides for the newer plugins are in [`plugins/docs/`](../plugins/docs/), each with its smoke steps and its UNVERIFIED list; the original four are described in [`community/how-to-play.md`](community/how-to-play.md) and [`oxide-rok-api.md`](oxide-rok-api.md). Staff-only commands (`/sculpt`, `/paint`, `/ironbreaker`, `/sentinel`) are listed in `STAFF_COMMANDS` in `tools/realm-integration/check.mjs` and stay out of the `/realm` hub.

## 3. How the plugins call each other

Oxide only lets one plugin call **non-public instance methods** of another, by name, through a `[PluginReference] Plugin X` field and `X.Call("Method", args)`. A missing plugin or method returns `null`, and every caller treats `null` as "not available". `tools/realm-integration/check.mjs` checks every call below against the target's source: the method exists, is non-public, and takes that many arguments. It also checks that every reference names a real plugin. Argument types were reviewed by hand (for example `GetCouncilSeat` takes the `ulong` player id; `GetHouse` takes the id as a string).

| Caller | Calls into | Methods |
|---|---|---|
| CrownAndConsequences | RealmChronicle | `Log`, `SetCrown` |
| CrownAndConsequences | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers` |
| RealmArena | RealmChronicle | `Log` |
| RealmArena | RealmEvents | `GetActiveEvents`, `GetTournamentEntrants`, `IsTruceActive`, `ScoreTournamentDuel` |
| RealmArena | RealmHerald | `PopupsWanted` |
| RealmArena | RealmHouses | `GetHouse`, `GetLiege`, `HasTreaty` |
| RealmArena | RealmLaws | `ArenaTrialResult` |
| RealmArena | RealmLegendary | `IsBearer` |
| RealmArena | RealmRenown | `AddDeed` |
| RealmArena | RealmSeasons | `AwardHouse` |
| RealmArena | RealmSentinel | `IsSentinelFrozen`, `SentinelGrace` |
| RealmArena | RealmTreasury | `GetPurse`, `HoldMarks`, `PayFromHold`, `ReleaseHold` |
| RealmArena | RealmWarden | `IsNewPlayerProtected`, `RaiseWardenAlert` |
| RealmArrival | CrownAndConsequences | `GetKingHouse`, `GetKingName` |
| RealmArrival | RealmArena | `IsDuelBlow` |
| RealmArrival | RealmEvents | `GetNextEvent` |
| RealmArrival | RealmHerald | `PopupsWanted` |
| RealmArrival | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetHouseSummaries`, `GetLiege`, `GetMembers` |
| RealmArrival | RealmQuests | `GetStoryProgress`, `ReportQuestEvent` |
| RealmArrival | RealmRenown | `AddDeed` |
| RealmArrival | RealmSentinel | `SentinelGrace` |
| RealmArrival | RealmTravel | `CancelJourney`, `GetDiscoveredCount`, `IsTravelling` |
| RealmArrival | RealmWarden | `GetProtectionMinutesLeft`, `IsInCombat`, `IsNewPlayerProtected`, `RaiseWardenAlert` |
| RealmChronicle | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetKingSince`, `GetNextRebellionWindow` |
| RealmChronicle | RealmEvents | `GetNextEvent` |
| RealmChronicle | RealmHouses | `GetHouseSummaries` |
| RealmContracts | CrownAndConsequences | `GetOpenClaims`, `IsSwornToCrown` |
| RealmContracts | RealmChronicle | `Log` |
| RealmContracts | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `HasTreaty` |
| RealmCrafts | RealmChronicle | `Log` |
| RealmCrafts | RealmDominion | `GetHoldings` |
| RealmCrafts | RealmHerald | `PopupsWanted` |
| RealmCrafts | RealmHouses | `GetHouse` |
| RealmCrafts | RealmQuests | `ReportQuestEvent` |
| RealmCrafts | RealmRenown | `AddDeed` |
| RealmCrafts | RealmSeasons | `AwardHouse` |
| RealmCrafts | RealmSentinel | `SentinelItemSource` |
| RealmCrafts | RealmTreasury | `ChargeMarks`, `GetHold`, `GetLastPrice`, `GetPurse`, `GetTreasurySummary`, `HoldMarks`, `PayFromHold`, `ReleaseHold`, `RewardMarks` |
| RealmDominion | CrownAndConsequences | `GetUtcOffsetHours`, `IsRebellionActive` |
| RealmDominion | RealmChronicle | `Log` |
| RealmDominion | RealmEvents | `IsTruceActive` |
| RealmDominion | RealmHerald | `PopupsWanted` |
| RealmDominion | RealmHouses | `GetHouse`, `GetHouseFounded`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `HasTreaty` |
| RealmDominion | RealmPainter | `RefreshBoards` |
| RealmDominion | RealmRenown | `AddDeed` |
| RealmDominion | RealmSeasons | `AwardHouse` |
| RealmDominion | RealmTreasury | `GrantHouseIncome` |
| RealmDominion | RealmWarden | `IsNewPlayerProtected`, `IsRaidHourNow` |
| RealmDynasties | CrownAndConsequences | `CmdClaim`, `GetKingHouse`, `GetOpenClaims` |
| RealmDynasties | RealmChronicle | `Log` |
| RealmDynasties | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers`, `GetReputation`, `GetVassals` |
| RealmEvents | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetOpenClaims`, `GetUtcOffsetHours`, `IsRebellionActive` |
| RealmEvents | RealmChronicle | `Log` |
| RealmEvents | RealmHouses | `GetHouse`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `GetVassals`, `HasTreaty` |
| RealmEvents | RealmLegendary | `AwardEventPrize` |
| RealmEvents | RealmSeasons | `AwardHouse` |
| RealmEvents | RealmWarden | `IsNewPlayerProtected` |
| RealmHerald | CrownAndConsequences | `GetKingHouse`, `GetKingName` |
| RealmHerald | RealmArrival | `ArrivalStage` |
| RealmHerald | RealmContracts | `HasContractHistory` |
| RealmHerald | RealmHouses | `GetHouse` |
| RealmHerald | RealmSeasons | `GetSeasonName` |
| RealmHeraldry | CrownAndConsequences | `GetCouncilSeats`, `GetDecreeList`, `SeatElectedCouncillor`, `SetDecreeMandate` |
| RealmHeraldry | RealmChronicle | `Log` |
| RealmHeraldry | RealmContracts | `IsOutlaw` |
| RealmHeraldry | RealmHerald | `PopupsWanted` |
| RealmHeraldry | RealmHouses | `GetHouse`, `GetHouseFounded`, `GetHouseLeader`, `GetHouseSummaries`, `GetMembers` |
| RealmHeraldry | RealmLaws | `GetActiveLaws`, `IsCourtOutlaw`, `IsExiled` |
| RealmHeraldry | RealmQuests | `ReportQuestEvent` |
| RealmHeraldry | RealmRenown | `GetRenown` |
| RealmHeraldry | RealmSeasons | `AwardHouse`, `GetSeasonName`, `GetSeasonNumber` |
| RealmHeraldry | RealmTravel | `GetDiscoveredCount` |
| RealmHeraldry | RealmTreasury | `ChargeMarks`, `HoldMarks`, `ReleaseHold` |
| RealmHeraldry | RealmWarden | `IsNewPlayerProtected` |
| RealmHouses | RealmChronicle | `Log` |
| RealmHouses | RealmHerald | `PopupsWanted` |
| RealmLaws | CrownAndConsequences | `GetCouncilSeat`, `GetKingHouse`, `IsRebellionActive` |
| RealmLaws | RealmArena | `IsDuelBlow`, `StageTrial` |
| RealmLaws | RealmChronicle | `Log` |
| RealmLaws | RealmContracts | `PardonOutlaw`, `ProclaimOutlaw` |
| RealmLaws | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetMembers`, `HasTreaty` |
| RealmLegendary | RealmChronicle | `Log` |
| RealmLegendary | RealmHerald | `PopupsWanted` |
| RealmLegendary | RealmHouses | `GetHouse`, `GetLiege`, `HasTreaty` |
| RealmLegendary | RealmRenown | `AddDeed` |
| RealmPainter | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetKingSince`, `GetUtcOffsetHours` |
| RealmPainter | RealmChronicle | `GetLastEventId` |
| RealmPainter | RealmContracts | `GetBountyCount` |
| RealmPainter | RealmDominion | `GetDominionBoard` |
| RealmPainter | RealmEvents | `GetActiveEvents`, `GetNextEvent` |
| RealmPainter | RealmHouses | `GetHouse` |
| RealmPainter | RealmLaws | `GetCourtOutlaws` |
| RealmPainter | RealmLegendary | `GetBearerName` |
| RealmPainter | RealmSeasons | `GetSeasonName`, `GetSeasonNumber`, `GetSeasonStandings` |
| RealmPainter | RealmWorld | `GetWorldBoard` |
| RealmQuests | RealmArrival | `ArrivalStage` |
| RealmQuests | RealmChronicle | `Log` |
| RealmQuests | RealmEvents | `GetActiveEvents` |
| RealmQuests | RealmHerald | `PopupsWanted` |
| RealmQuests | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `HasTreaty` |
| RealmQuests | RealmRenown | `AddDeed`, `GetRenown`, `GetTitles` |
| RealmQuests | RealmSeasons | `AwardHouse`, `GetSeasonNumber` |
| RealmQuests | RealmSentinel | `SentinelItemSource` |
| RealmQuests | RealmTreasury | `GetPurse`, `RewardMarks` |
| RealmQuests | RealmWarden | `IsNewPlayerProtected` |
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
| RealmSentinel | RealmWarden | `RaiseWardenAlert` |
| RealmStats | RealmHouses | `GetHouse` |
| RealmTravel | CrownAndConsequences | `GetUtcOffsetHours`, `IsDecreeActive`, `IsHeldForRansom`, `IsRebellionActive`, `IsSwornToCrown` |
| RealmTravel | RealmArrival | `ArrivalStage` |
| RealmTravel | RealmContracts | `IsOutlaw` |
| RealmTravel | RealmEvents | `IsTruceActive` |
| RealmTravel | RealmHerald | `PopupsWanted` |
| RealmTravel | RealmHouses | `GetHouse`, `GetLiege`, `GetMembers`, `HasTreaty` |
| RealmTravel | RealmLaws | `IsCourtOutlaw`, `IsExiled` |
| RealmTravel | RealmLegendary | `IsBearer` |
| RealmTravel | RealmRenown | `AddDeed` |
| RealmTravel | RealmSeasons | `AwardHouse`, `GetSeasonNumber` |
| RealmTravel | RealmSentinel | `SentinelGrace`, `SentinelItemSource` |
| RealmTravel | RealmTreasury | `ChargeMarks`, `GetPurse` |
| RealmTravel | RealmWarden | `IsInCombat`, `IsNewPlayerProtected`, `IsRaidHourNow` |
| RealmTreasury | CrownAndConsequences | `GetCouncilSeat`, `IsSwornToCrown` |
| RealmTreasury | RealmChronicle | `Log` |
| RealmTreasury | RealmCrafts | `GetMarketFeeDiscount` |
| RealmTreasury | RealmHouses | `GetHouse`, `GetHouseFounded`, `GetHouseLeader`, `GetMembers` |
| RealmWarden | CrownAndConsequences | `IsRebellionActive` |
| RealmWarden | RealmArrival | `OwnsArrival` |
| RealmWorld | CrownAndConsequences | `GetKingHouse`, `GetKingName` |
| RealmWorld | RealmArena | `IsInDuel` |
| RealmWorld | RealmChronicle | `GetLastEventId`, `Log` |
| RealmWorld | RealmEvents | `GetActiveEvents`, `GetNextEvent`, `IsTruceActive` |
| RealmWorld | RealmHerald | `PopupsWanted` |
| RealmWorld | RealmHouses | `GetHouse`, `GetHouseSummaries`, `GetLiege`, `HasTreaty` |
| RealmWorld | RealmPainter | `RefreshBoards` |
| RealmWorld | RealmQuests | `ReportQuestEvent` |
| RealmWorld | RealmRenown | `AddDeed` |
| RealmWorld | RealmSculptor | `PlaceSculptureAt`, `RemoveSculpture` |
| RealmWorld | RealmSeasons | `AwardHouse`, `GetSeasonStandings` |
| RealmWorld | RealmSentinel | `SentinelItemSource` |
| RealmWorld | RealmTravel | `CancelJourney`, `IsTravelling` |
| RealmWorld | RealmTreasury | `GetTreasurySummary`, `RewardMarks` |
| RealmWorld | RealmWarden | `IsNewPlayerProtected` |

This table is generated from `node tools/realm-integration/check.mjs --json` (2026-10-04): 27 plugins and 363 call sites. RealmSculptor and RealmCourt make no cross-plugin calls; RealmSculptor is called by RealmWorld (`PlaceSculptureAt`, `RemoveSculpture`).

**RealmArrival's calls and the guards on it.** RealmArrival reports `ReportQuestEvent` with custom `arrival_*` subjects (`arrival_gate`, `arrival_banners`, `arrival_pledge`, `arrival_hearth`, `arrival_crown`, `arrival_road`) and pays the `written` deed through `AddDeed` only with `WrittenDeed` (off by default); `GetProtectionMinutesLeft` is new in RealmWarden for it. It offers `ArrivalStage` and `OwnsArrival`, which answer from saved data alone: RealmHerald holds its welcome, RealmQuests the first hint and the tale's first news, and RealmTravel the kit hint (`ArrivalStage`), and RealmWarden its join "Welcome" (`OwnsArrival`), while RealmArrival owns a newcomer. Guide: [`plugins/docs/RealmArrival.md`](../plugins/docs/RealmArrival.md#integration).

**Court outlawry reaches the bounty board.** RealmLaws offered court outlaws to RealmContracts, but RealmContracts had no method to receive them, so the calls did nothing. RealmContracts now has two non-public methods:

- `bool ProclaimOutlaw(string playerId, string name, int hours, string by)` adds or extends an outlaw entry marked as placed by the court. A court sentence follows a trial and has its own quotas, so the crown's cooldowns and `MaxOutlaws` do not apply to it. It never shortens a sentence and writes no second Chronicle line.
- `bool PardonOutlaw(string playerId)` lifts only court-placed outlawry and withdraws open bounties on that player, as a crown pardon does. A crown proclamation stays until the crown pardons it.

`tools/realm-integration/cross-tests/run.sh` compiles RealmLaws and RealmContracts together, routes `Call` the way Oxide does, and checks the whole path: sentence, bounty with escrow, pardon, refund, and the data-file round trip (25 checks).

**Plugins that read other plugins' data files.** RealmArrival reads `RealmHerald.json` once to seed known veterans, and for `/arrival admin check` reads `RealmQuests.json` (the `the_hearth` mark), `RealmTravel.json` (waystones), `RealmDominionMap.json`, and the configs `RealmLaws.json` (the Hearth zone) and `RealmArena.json` (arena zones); it never writes any of them. RealmHeraldry reads `RealmHouses.json` (each house's bound guild and members' `Joined` dates). RealmQuests reads `RealmChronicle.json` (treaties and deeds), `RealmContracts.json` and `RealmSeasons.json` (the story's start); RealmWorld reads `RealmTravel.json` for waystone positions (ROADMAP SRV-23). RealmRenown reads `RealmContracts.json`, `RealmHouses.json` and `RealmChronicle.json` for deeds. RealmSeasons reads `RealmChronicle.json` for new entries after the last event id it has seen (`GetLastEventId`). A field rename in those files makes the reader see nothing (it does not misaward); RealmRenown reports it in `/renown admin status`.

**Load order.** Oxide does not define the order between plugins' hooks. Damage, capture and placement blocks in RealmLaws, RealmEvents (truce) and RealmWarden can therefore meet CrownAndConsequences' capture handling in either order (see each guide). RealmLaws ships its capture block switched off for this reason. RealmLegendary scales `Damage.Amount` in the same damage hooks: a blow another plugin zeroed stays zero either way, but which plugin sees a blow first is not defined. RealmSculptor's protection and RealmWarden's raid hours both act on `OnCubeTakeDamage`; either one blocking is enough.

## 4. Services and apps next to the server

| System | Where | What it does | Connects to | Proof so far |
|---|---|---|---|---|
| Chronicle service | `chronicle/` | Read-only HTTP service on `127.0.0.1:8787`: `/api/state`, `/api/events`, the OBS `/overlay` and the public `/realm` page. | Reads `RealmChronicle.json` and `RealmState.json`. Feeds streamkit, the bot and the launcher. | Tested here (15 tests, including the three-way type sync, the art pack, local fonts and the coronation and rebellion moments). UNVERIFIED in OBS (PT6.1, PT6.10). |
| Realm Steward and Realm | `launcher/` | Two Electron apps from one codebase. Steward: setup wizard, up to four servers, live admin console, Court, Connection Doctor, backups, restarts, Go Public, signed server list, Discord herald. Realm: the one-screen player app with Play or Install, status, signed news (`lib/news.js`) and signed updates (`lib/updater.js`). **From `team/steward-integration`:** Update plugins also deploys the plugins' data files (sculptures, sign art, quest content) with backups and reloads; a Realm features screen with each plugin's switches (edited in its config, then reloaded over the admin console); a Sentinel screen (the feed, evidence, Kick and Ban); Publish news and Publish update forms; staff commands of every plugin in the Court (not yet RealmArrival's). | Starts `ROK.exe`, holds its admin console, runs the Chronicle in-process, deploys `plugins/` and their data files (not yet RealmArrival's site plan: ROADMAP STW-1). | Tested here (`npm run check` and 240 unit tests; 267 and 40 screenshot checks from `team/steward-integration`; Electron walk-throughs on Linux). An earlier Steward build ran on the owner's PC; 1.0.0 and the player app are UNVERIFIED on Windows (PT0, PT5); Steward's new screens are UNVERIFIED on the real server (PT0.13, PT0.15, PT0.16, PT3.28, PT5.17). |
| Portal | `portal/` | Static website generator: home, Chronicle, houses, Hall of Kings, how to play, rules, lore, feed, `status.json`. | Reads `oxide/data` (Chronicle, State, Houses, Crown, Seasons, Legends, Events). | Tested here (35 tests, with the art pack, link previews and phone layouts). Hosting is the owner's choice (ROADMAP WEB-2). Does not yet read `RealmDominionMap.json` (WEB-9). |
| Discord bot | `bot/` | `/realm` slash commands, a live status message, optional house roles. | Chronicle API, `RealmHouses.json`, `RealmEvents.json` (data and config), labels from `common.js`. | Tested here with fakes (90 tests after `npm ci`, with art thumbnails; needs Attach Files). No real Discord call made (UNVERIFIED, PT6.3). |
| Stream scenes | `streamkit/` | Five OBS browser scenes: War Board, Throne Room, Breaking News, Countdown, Starting Soon. | Relays the Chronicle API; schedule from `schedule.json`. | Tested here (30 tests, Chromium screenshots). UNVERIFIED in OBS itself (PT6.2). |
| Analytics | `analytics/` | Builds one offline HTML dashboard from RealmStats day files. | Reads `oxide/data/RealmStats`. | Tested here (14 tests, plus 89 plugin checks). |
| Ops | `ops/` | Hosting guide, SteamCMD install and update, offsite backups, restore drills, uptime monitor, disaster recovery. | Calls `server/New-TestServer.ps1` and `server/Install-Oxide.ps1`; posts to a Discord webhook. | Behaviour-tested under PowerShell 7 on Linux by its team; parse-checked for 5.1. Never run on Windows. |
| Server scripts | `server/` | PowerShell 5.1 scripts for the test copy: copy, start, Oxide install and rollback, deploy plugins, backups, mod keys, `Set-Mood.ps1`. | Used by `Realm.bat`, ops and the smoke test. | Parse-checked in CI. Never run on Windows. |
| Atmosphere moods | `mods/` | Mood presets (Long Winter, Blood Moon, Golden Summer, Storm Season, Ashfall, Grim but Readable, Harvest Fair, Midwinter and others in `mods/presets/`) through the game's own `Mods\*.cfg` override system, and a rotation plan with event keys (`blood_moon`, `harvest_festival`, `midwinter` for RealmWorld). | `server/Set-Mood.ps1`. | Colour maths from decompiled formulas only. Nothing seen in game (UNVERIFIED). |
| ROK simulator | `tools/rok-sim/` | A fake dedicated server for Linux and CI: admin console, A2S, logs, configs, Chronicle data. | Launcher tests and CI. | Tested here (62 tests). Not compared with a real `ROK.exe`. |
| Plugin compile check | `tools/plugin-compile-check/` | Compiles every plugin at C# 3 against the real 2.0.3867 metadata. | CI `plugins` job. | 0 errors. |
| Integration check | `tools/realm-integration/` | Static check of cross-plugin calls, Chronicle types, chat-command names, the chat style, the `/realm` catalogue and popups (`check.mjs`), and the RealmLaws/RealmContracts behaviour test (`cross-tests/`). | CI `plugins` job. | Tested here (27 plugins, 363 calls, 60 commands, 47 Chronicle types, `/realm` lists 54: OK; 12 tests; cross-tests 25). |
| Exploit review | `tools/exploit-review/` | Regression suites for every exploit found and fixed: treasury, laws-contracts, renown, crown-houses, dynasties, warden, ravens, events, seasons, chronicle, legendary, and from wave 4 dominion (+ treasury), quests (+ treasury), arena (+ events, + laws), travel (+ treasury), crafts, world, heraldry (+ crown, + treasury), and arrival; `sentinel` has its own runner. | CI `plugins` job. | Tested here: 26 suites, 767 checks (680 before the arrival suite's 87), 0 failed; sentinel 24. |
| Sculptor and painter tools | `art/tools/sculptor/`, `art/tools/painter/` | Build, preview and check the sculptures (23 and one site plan); render the paintings (26), glyph atlases and the `RealmPainterArt.json` bundle (73 sprites). | Feed RealmSculptor and RealmPainter. | Tested here; the tests and `cli.mjs check` run in CI, `paint.mjs check` not yet (ROADMAP SGN-8). |
| CI | `.github/workflows/ci.yml` | Chronicle; launcher check and tests; plugins (compile check, integration check, cross-tests, every plugin logic suite, RealmStats tests, exploit review); portal; art check and tests; saga, legal and staff tools; Windows installers through `Build-Realm.bat`; PowerShell parse; simulator; bot; streamkit and analytics; ops and Set-Mood under PowerShell 7. | GitHub Actions, read-only token. | Runs on pushes to GitHub (a failing run was fixed in 142dc82). Also runs the sculptor and painter tool tests, `cli.mjs check` and the `sentinel` exploit runner. Not yet in CI: `paint.mjs check` and the mood tests for the new event keys (ROADMAP SGN-8, QA-5). |

## 5. Content, brand and policy

| System | Where | What it is | Proof so far |
|---|---|---|---|
| Lore and community docs | `docs/community/` | The realm of Ostreval, its six great houses (Varrow, Ashgrove, Corvane, Dunmere, Halloran, Merrin), rules, how to play, streamer kit, launch plan. | Written. `how-to-play.md` opens with "What's in Realm", every system in plain words for players (the portal renders it as How to play). |
| Sculptures and sign art | `art/sculptures/`, `art/paintings/` | 23 block sculptures (Herald's Pillar, the Ironbreaker, the Old Throne, six house monuments, the Tournament Arch, a shape test, and the arrival site's gatehouse, pledge stones, hearth ring, processional and wayboard) and one site plan, with previews; 26 sign paintings and the live-board fonts and sprites. | Checked by their tools' tests; nothing seen in game (PT2). |
| Art and brand | `art/`, `docs/brand.md` | Sigils, banners, shields, logos, event icons, Discord and social art, palette, and a build that checks colours against the overlay's house dyes. | `art/tools` tests (24) and `build.mjs check --skip-png` (132 SVGs) pass. Every Chronicle type has an icon mapping (the holding and ballot icons from wave 4). The portal, overlay, scenes and bot use the pack (copies kept in step by `portal/scripts/sync-art.mjs --check`). How the PNGs look on Discord is UNVERIFIED (PT6.5). |
| Saga (season 1 run-sheets) | `docs/saga/` | "The Hollow Crown": four acts of run-sheets, proclamations, locations, legends, an eight-week calendar, and a checker that every command they use exists. | Checker: 0 errors, 14 tests. Run-time behaviour UNVERIFIED; Acts I and II are rehearsed in the closed beta (ROADMAP EVT-3). |
| Legal and staff | `docs/legal/`, `docs/community/ops/` | EULA checklist, rights-holder letter, privacy notice template, data deletion, monetisation guardrails, staff roles, moderation handbook, appeals, incident response, and tools to find a player's data and check permissions. | Tools tested (12). The EULA text itself has not been read; every clause row is open, and reading it blocks 1.0 (ROADMAP COM-6). Not legal advice. |
| Roadmap | [`ROADMAP.md`](ROADMAP.md) | The production plan to 1.0 and Seasons 2 to 4: the quality bar, milestones and exit criteria, the backlog by area, the ordered play-test checklist for every UNVERIFIED item (wave 4 folded in), risks and volunteer roles. | Updated 2026-10-04 for wave 4. |

## 6. Open integration items

These cross team boundaries and are not done. Each has an ID in [`ROADMAP.md`](ROADMAP.md) section 3.

- **`team/steward-integration` is merged** (it came in with `team/arrival-plugin`); check its merge notes in [`HANDOFF.md`](HANDOFF.md#merge-notes-for-teamsteward-integration) (M0). It brought the data-file deploy (STW-1, SGN-1), the staff commands of every plugin in the Court (STW-2; RealmArrival's are not in it yet), the Sentinel screen (STW-3, SEN-5), the news and update forms (STW-4), and `blade_claimed` / `blade_lost` (SRV-6).
- `server/Deploy-Plugins.ps1` copies only `.cs` files; it must also copy `art/sculptures/*.json`, `art/paintings/RealmPainterArt.json`, `plugins/docs/RealmQuests/content/*.json` and the mood folders, with Steward's safety rules. Neither it nor Steward's deploy copies RealmArrival's site plan yet: RealmArrival needs `art/sculptures/sites/arrival.json` as `oxide/data/RealmArrival/site.json` (STW-1).
- Plugins that pay items or move players and predate wave 4 (RealmTreasury, RealmContracts, RealmEvents, CrownAndConsequences, RealmLegendary) do not yet tell RealmSentinel; the wave-4 plugins do (SRV-3).
- RealmContracts, RealmLaws and RealmLegendary do not yet call `RealmPainter.RefreshBoards`; boards catch up within their refresh time (SRV-4).
- RealmRenown has no `ironbreaker` deed (SRV-5). The holding, quest and Wayfarer titles are config snippets, not defaults with badges, and live configs do not gain new default titles by themselves (SRV-17).
- Short labels for the newer Chronicle types are missing in the bot's text, the stream kit and the launcher's notifications (SRV-18).
- Four money paths into RealmTreasury from wave 4 (`GrantHouseIncome`, `RewardMarks`, `ChargeMarks`, holds) want one design and one budget view (SRV-19, SRV-21).
- RealmWarden still flags duellists with its combat-tag heuristics (SRV-20). RealmEvents' truce has no duel exemption (SRV-16).
- Law referendums are advisory until RealmLaws offers `ApplyReferendum` (SRV-22).
- RealmQuests reads RealmChronicle's and RealmContracts' files, and RealmWorld reads `RealmTravel.json`, where direct calls would be cleaner (SRV-23).
- RealmDynasties presses blood claims by calling CrownAndConsequences' `/claim` command method (SRV-7). RealmRenown reads other plugins' data files (SRV-8). RealmWarden cuts external alert kinds at 24 characters (SRV-10).
- The portal and Chronicle do not show the holdings map (`RealmDominionMap.json`, WEB-9), achievements or house colours (WEB-10).
- `launcher/renderer/heraldry.js` keeps its own copy of the event, title and house tables that `chronicle/public/assets/realm-art.js` also holds (PLA-5).
- CI does not yet run `paint.mjs check` or the mood tests for the new event keys (SGN-8, QA-5).
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

Also:

```
node --test art/tools/sculptor/test/*.test.mjs && node art/tools/sculptor/cli.mjs check
node --test art/tools/painter/test/*.test.mjs && node art/tools/painter/paint.mjs check
bash tools/exploit-review/sentinel/run.sh
node portal/scripts/sync-art.mjs --check
```

`ops/tests/Run-OpsTests.ps1` and `mods/presets/tests/Test-SetMood.ps1` need PowerShell 7 (`pwsh`).
