# Realm systems map

One page for every part of Realm: what it does, where it lives, how it connects to the rest, and how far it has been proven. Read this before changing anything that crosses a folder boundary.

**The short version of the status.** Every piece has automated tests or checks that pass here, on Linux. **Nothing has run against a real Reign of Kings server or client yet.** The plugins compile against the real Oxide 2.0.3867 and game DLL metadata and are tested against mocks; the Windows apps and PowerShell scripts were built or parse-checked but not run on Windows. The step-by-step proof on real hardware is [`smoke-test.md`](smoke-test.md) plus the smoke steps in each plugin guide. Anything marked UNVERIFIED below has not been seen working.

Status words used here:

| Word | Meaning |
|---|---|
| **Compile-checked** | Compiles with 0 errors at C# 3 against the shipped Oxide 2.0.3867 and patched game DLLs (`tools/plugin-compile-check/check.sh`). |
| **Mock-tested** | Behaviour tests run the real source against stand-ins for the game and Oxide. They prove the plugin's own logic, not the game's behaviour. |
| **Tested here** | Automated tests pass in this Linux environment (Node or .NET). |
| **UNVERIFIED** | Not seen working on the real thing. |

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

The **closed list of Chronicle event types** is the main contract between the parts. It lives in three files that a test keeps in step: `plugins/RealmChronicle.cs` (`KnownTypes`), `chronicle/server.js` (`EVENT_TYPES`) and `chronicle/public/assets/common.js` (`TYPE_META`). There are 42 types. A new type needs all three; `tools/realm-integration/check.mjs` fails if they differ, if a plugin logs a literal type that is not registered, or if a `plugins/docs/*/EVENTS.json` request is still waiting. The portal (`portal/lib/model.mjs`), the Discord herald (`launcher/lib/discord.js`), the bot (reads `common.js` as text) and the art event map (`art/icons/event-map.json`) carry the same labels.

## 2. Server plugins (`plugins/`)

All are Oxide C# plugins, C# 3 syntax, deployed together by Realm Steward or `server/Deploy-Plugins.ps1`. All are **compile-checked**. None has run in game. Player commands: [`realm-commands.md`](realm-commands.md).

| Plugin | What it does | Writes | Proof so far |
|---|---|---|---|
| `RealmHouses.cs` | Houses, ranks, oaths of fealty, treaties, oathbreaker and treaty-breaker marks. The base API every other plugin reads. | `RealmHouses.json` | Compile-checked. |
| `CrownAndConsequences.cs` | Tracks the monarch on the Old Throne; decrees, council, claims and scheduled rebellion windows, tax cap, bounded ransom. | `CrownAndConsequences.json` | Compile-checked. |
| `RealmChronicle.cs` | The event log (`Log`) and the realm snapshot. Rejects unknown types. | `RealmChronicle.json`, `RealmState.json` | Compile-checked; its type list is tested against the Chronicle service. |
| `RealmContracts.cs` | Bounties on public enemies, deliveries, mercenary work, with real item escrow. Now also takes court outlawry from RealmLaws. | `RealmContracts.json` | Compile-checked; the court hand-off is mock-tested (`tools/realm-integration/cross-tests`). Item escrow in game is UNVERIFIED. |
| `RealmSeasons.cs` | Numbered seasons, house standings, the Hall of Kings that survives wipes. | `RealmSeasons.json`, `RealmLegends.json` | Compile-checked. Guide: [`plugins/docs/RealmSeasons.md`](../plugins/docs/RealmSeasons.md). |
| `RealmEvents.cs` | Crown Night, Royal Tournament, King's Hunt, Truce of the Realm, with heralds and item prizes. | `RealmEvents.json` | Compile-checked, mock-tested (89 checks). Truce damage blocking is UNVERIFIED. |
| `RealmLaws.cs` | Laws and zones, a public crime ledger, accusations, jury trials, trial by combat, fines, outlawry, exile, pardons. | `RealmLaws.json` | Compile-checked, mock-tested (90). Blocking acts in game is UNVERIFIED. |
| `RealmDynasties.cs` | Bloodlines, heirs, succession, prestige, blood claims after a monarch falls, titles bestowed by the crown. | `RealmDynasties.json` | Compile-checked, mock-tested (92). Pressing a claim calls CrownAndConsequences' `/claim` command method (UNVERIFIED in game). |
| `RealmRenown.cs` | Renown and infamy from deeds, earned titles worn in chat. | `RealmRenown.json` | Compile-checked, mock-tested (108). The chat prefix is UNVERIFIED. |
| `RealmTreasury.cs` | Marks (a ledger currency), purses, a market with escrow, house vaults, the crown's treasury, mint and tithe. | `RealmTreasury.json`, ledger logs | Compile-checked, mock-tested (118). Game-tax observation is UNVERIFIED. |
| `RealmRavens.cs` | Letters between players and houses, interception by spies, moderated anonymous rumours. | `RealmRavens.json` | Compile-checked, mock-tested (95). |
| `RealmWarden.cs` | New-player protection, raid hours, combat-log flags, reports, mutes, name rules; evidence for admins. | `RealmWarden.json`, logs | Compile-checked, mock-tested (140). Raid-hour blocking is proven only for block health. |
| `RealmStats.cs` | Pseudonymous daily player statistics with opt-out. | `RealmStats/` day files | Compile-checked, mock-tested (89). |
| `RealmCourt.cs` | Puts `/realm.save` and `/realm.players` into the game's own command table for Realm Steward's console. | nothing | Compile-checked. UNVERIFIED at run time. |

Guides for the newer plugins are in [`plugins/docs/`](../plugins/docs/); the original four are described in [`community/how-to-play.md`](community/how-to-play.md) and [`oxide-rok-api.md`](oxide-rok-api.md).

## 3. How the plugins call each other

Oxide only lets one plugin call **non-public instance methods** of another, by name, through a `[PluginReference] Plugin X` field and `X.Call("Method", args)`. A missing plugin or method returns `null`, and every caller treats `null` as "not available". `tools/realm-integration/check.mjs` checks every call below against the target's source: the method exists, is non-public, and takes that many arguments. It also checks that every reference names a real plugin. Argument types were reviewed by hand (for example `GetCouncilSeat` takes the `ulong` player id; `GetHouse` takes the id as a string).

| Caller | Calls into | Methods |
|---|---|---|
| CrownAndConsequences | RealmChronicle | `Log`, `SetCrown` |
| CrownAndConsequences | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege` |
| RealmChronicle | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetKingSince` |
| RealmChronicle | RealmHouses | `GetHouseSummaries` |
| RealmContracts | CrownAndConsequences | `GetOpenClaims`, `IsSwornToCrown` |
| RealmContracts | RealmChronicle | `Log` |
| RealmContracts | RealmHouses | `GetHouse`, `GetHouseLeader` |
| RealmDynasties | CrownAndConsequences | `CmdClaim` (a chat-command method, not a documented API), `GetKingHouse`, `GetOpenClaims` |
| RealmDynasties | RealmChronicle | `Log` |
| RealmDynasties | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetLiege`, `GetReputation`, `GetVassals` |
| RealmEvents | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetOpenClaims`, `GetUtcOffsetHours`, `IsRebellionActive` |
| RealmEvents | RealmChronicle | `Log` |
| RealmEvents | RealmHouses | `GetHouse` |
| RealmEvents | RealmSeasons | `AwardHouse` |
| RealmHouses | RealmChronicle | `Log` |
| RealmLaws | CrownAndConsequences | `GetCouncilSeat`, `GetKingHouse`, `IsRebellionActive` |
| RealmLaws | RealmChronicle | `Log` |
| RealmLaws | RealmContracts | `ProclaimOutlaw`, `PardonOutlaw` |
| RealmLaws | RealmHouses | `GetHouse`, `GetHouseLeader` |
| RealmRavens | RealmChronicle | `Log` |
| RealmRavens | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetHouseSummaries`, `GetLiege`, `GetMembers`, `GetVassals`, `HasTreaty` |
| RealmRenown | CrownAndConsequences | `GetKingHouse`, `GetOpenClaims` |
| RealmRenown | RealmChronicle | `Log` |
| RealmRenown | RealmContracts | `IsOutlaw` |
| RealmRenown | RealmHouses | `GetHouse`, `GetLiege` |
| RealmRenown | RealmLaws | `GetCourtOutlaws` |
| RealmSeasons | CrownAndConsequences | `GetKingHouse`, `GetKingName`, `GetOpenClaims` |
| RealmSeasons | RealmChronicle | `GetLastEventId`, `Log` |
| RealmSeasons | RealmHouses | `GetHouse`, `GetHouseSummaries`, `GetMemberNames`, `HasTreaty` |
| RealmStats | RealmHouses | `GetHouse` |
| RealmTreasury | CrownAndConsequences | `GetCouncilSeat`, `IsSwornToCrown` |
| RealmTreasury | RealmChronicle | `Log` |
| RealmTreasury | RealmHouses | `GetHouse`, `GetHouseLeader`, `GetMembers` |
| RealmWarden | CrownAndConsequences | `IsRebellionActive` |

**Court outlawry reaches the bounty board.** RealmLaws offered court outlaws to RealmContracts, but RealmContracts had no method to receive them, so the calls did nothing. RealmContracts now has two non-public methods:

- `bool ProclaimOutlaw(string playerId, string name, int hours, string by)` adds or extends an outlaw entry marked as placed by the court. A court sentence follows a trial and has its own quotas, so the crown's cooldowns and `MaxOutlaws` do not apply to it. It never shortens a sentence and writes no second Chronicle line.
- `bool PardonOutlaw(string playerId)` lifts only court-placed outlawry and withdraws open bounties on that player, as a crown pardon does. A crown proclamation stays until the crown pardons it.

`tools/realm-integration/cross-tests/run.sh` compiles RealmLaws and RealmContracts together, routes `Call` the way Oxide does, and checks the whole path: sentence, bounty with escrow, pardon, refund, and the data-file round trip (25 checks).

**Plugins that read other plugins' data files.** RealmRenown reads `RealmContracts.json`, `RealmHouses.json` and `RealmChronicle.json` for deeds. RealmSeasons reads `RealmChronicle.json` for new entries after the last event id it has seen (`GetLastEventId`). A field rename in those files makes the reader see nothing (it does not misaward); RealmRenown reports it in `/renown admin status`.

**Load order.** Oxide does not define the order between plugins' hooks. Damage, capture and placement blocks in RealmLaws, RealmEvents (truce) and RealmWarden can therefore meet CrownAndConsequences' capture handling in either order (see each guide). RealmLaws ships its capture block switched off for this reason.

## 4. Services and apps next to the server

| System | Where | What it does | Connects to | Proof so far |
|---|---|---|---|---|
| Chronicle service | `chronicle/` | Read-only HTTP service on `127.0.0.1:8787`: `/api/state`, `/api/events`, the OBS `/overlay` and the public `/realm` page. | Reads `RealmChronicle.json` and `RealmState.json`. Feeds streamkit, the bot and the launcher. | Tested here (8 tests, including the three-way type sync). |
| Realm Steward and Realm | `launcher/` | Two Electron apps from one codebase. Steward: setup wizard, up to four servers, live admin console, Court, Connection Doctor, backups, restarts, Go Public, signed server list, Discord herald. Realm: the player app with the signed list, status and Join through Steam. | Starts `ROK.exe`, holds its admin console, runs the Chronicle in-process, deploys `plugins/`. | Tested here (152 unit tests, Electron walk-throughs on Linux). Never run on Windows. |
| Portal | `portal/` | Static website generator: home, Chronicle, houses, Hall of Kings, how to play, rules, lore, feed, `status.json`. | Reads `oxide/data` (Chronicle, State, Houses, Crown, Seasons, Legends, Events). | Tested here (28 tests). Hosting is the owner's choice. |
| Discord bot | `bot/` | `/realm` slash commands, a live status message, optional house roles. | Chronicle API, `RealmHouses.json`, `RealmEvents.json` (data and config), labels from `common.js`. | Tested here with fakes (84 tests). No real Discord call made (UNVERIFIED). |
| Stream scenes | `streamkit/` | Five OBS browser scenes: War Board, Throne Room, Breaking News, Countdown, Starting Soon. | Relays the Chronicle API; schedule from `schedule.json`. | Tested here (27 tests, Chromium screenshots). UNVERIFIED in OBS itself. |
| Analytics | `analytics/` | Builds one offline HTML dashboard from RealmStats day files. | Reads `oxide/data/RealmStats`. | Tested here (13 tests, plus 89 plugin checks). |
| Ops | `ops/` | Hosting guide, SteamCMD install and update, offsite backups, restore drills, uptime monitor, disaster recovery. | Calls `server/New-TestServer.ps1` and `server/Install-Oxide.ps1`; posts to a Discord webhook. | Behaviour-tested under PowerShell 7 on Linux by its team; parse-checked for 5.1. Never run on Windows. |
| Server scripts | `server/` | PowerShell 5.1 scripts for the test copy: copy, start, Oxide install and rollback, deploy plugins, backups, mod keys, `Set-Mood.ps1`. | Used by `Realm.bat`, ops and the smoke test. | Parse-checked in CI. Never run on Windows. |
| Atmosphere moods | `mods/` | Mood presets (Long Winter, Blood Moon, Golden Summer, Storm Season, Ashfall, Grim but Readable) through the game's own `Mods\*.cfg` override system, and a rotation plan. | `server/Set-Mood.ps1`. | Colour maths from decompiled formulas only. Nothing seen in game (UNVERIFIED). |
| ROK simulator | `tools/rok-sim/` | A fake dedicated server for Linux and CI: admin console, A2S, logs, configs, Chronicle data. | Launcher tests and CI. | Tested here (52 tests). Not compared with a real `ROK.exe`. |
| Plugin compile check | `tools/plugin-compile-check/` | Compiles every plugin at C# 3 against the real 2.0.3867 metadata. | CI `plugins` job. | 0 errors. |
| Integration check | `tools/realm-integration/` | Static check of cross-plugin calls, Chronicle types and chat-command names (`check.mjs`), and the RealmLaws/RealmContracts behaviour test (`cross-tests/`). | CI `plugins` job. | Tested here. |
| CI | `.github/workflows/ci.yml` | Chronicle, launcher, plugin compile and integration checks, PowerShell parse, simulator. | GitHub Actions, read-only token. | Steps run by hand here; the workflow itself has not run on GitHub. The bot, portal, analytics, streamkit, art, saga and legal suites are not in CI yet. |

## 5. Content, brand and policy

| System | Where | What it is | Proof so far |
|---|---|---|---|
| Lore and community docs | `docs/community/` | The realm of Ostreval, its six great houses (Varrow, Ashgrove, Corvane, Dunmere, Halloran, Merrin), rules, how to play, streamer kit, launch plan. | Written; `how-to-play.md` is being updated by another team. |
| Art and brand | `art/`, `docs/brand.md` | Sigils, banners, shields, logos, event icons, Discord and social art, palette, and a build that checks colours against the overlay's house dyes. | `art/tools` tests (12) and `build.mjs check --skip-png` pass. Every Chronicle type has an icon mapping. |
| Saga (season 1 run-sheets) | `docs/saga/` | "The Hollow Crown": four acts of run-sheets, proclamations, locations, legends, an eight-week calendar, and a checker that every command they use exists. | Checker: 0 errors, 14 tests. Run-time behaviour UNVERIFIED. |
| Legal and staff | `docs/legal/`, `docs/community/ops/` | EULA checklist, rights-holder letter, privacy notice template, data deletion, monetisation guardrails, staff roles, moderation handbook, appeals, incident response, and tools to find a player's data and check permissions. | Tools tested (12). The EULA text itself has not been read; every clause row is open. Not legal advice. |

## 6. Open integration items

These cross team boundaries and are not done:

- `launcher/lib/moderation.js` lists admin commands per plugin but not RealmLaws' `/court admin` and `/law zone` commands (launcher team).
- Steward's "ready" flag fires on Unity's engine banner; the simulator team suggests also treating "Server for N players started on port P." as ready (launcher team).
- `README.md` says the setup "respects the EULA"; the legal team asks that it link [`legal/eula-compliance-checklist.md`](legal/eula-compliance-checklist.md) until the EULA has been read (README owner).
- RealmDynasties presses blood claims by calling CrownAndConsequences' `/claim` command method. A small non-public claim API in CrownAndConsequences would be cleaner.
- RealmRenown reads other plugins' data files; a non-public `AddDeed` call from those plugins would remove the coupling.
- The bot, portal, analytics, streamkit, art, saga and legal test suites could be added to CI.
- Everything in `smoke-test.md` and the per-plugin smoke steps still has to be done on a real Windows server.

## 7. How to re-check everything

```
bash tools/plugin-compile-check/check.sh                 # 0 errors at C# 3
node tools/realm-integration/check.mjs --commands         # wiring, Chronicle types, command names
node --test tools/realm-integration/check.test.mjs
bash tools/realm-integration/cross-tests/run.sh           # RealmLaws + RealmContracts, mocks
for t in plugins/docs/*/logic-tests/run.sh analytics/plugin-tests/run.sh; do bash "$t"; done
(cd chronicle && npm test); (cd portal && npm test); (cd bot && npm test); (cd streamkit && npm test)
(cd analytics && npm test); (cd tools/rok-sim && npm test); (cd launcher && npm test); (cd art/tools && npm test)
node --test docs/saga/tools/check-saga.test.mjs docs/legal/tools/*.test.mjs docs/community/ops/tools/*.test.mjs
```

`ops/tests/Run-OpsTests.ps1` and `mods/presets/tests/Test-SetMood.ps1` need PowerShell 7 (`pwsh`).
