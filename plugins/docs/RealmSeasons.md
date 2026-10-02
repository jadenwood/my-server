# RealmSeasons: seasons, house standings and the Hall of Kings

`plugins/RealmSeasons.cs` (Oxide 2.0.3867, C# 3). It turns the political sandbox into a competition with a memory:

- **Seasons** are numbered, with a start and an end date (UTC). The first one starts by itself on the first load. After that, an admin starts and ends them, and a season also ends by itself when its end date passes.
- **Standings.** Each house earns points for crown days, rebellions won and defended, treaties kept, and contracts fulfilled. It loses points for treaties and oaths broken. Realm events (RealmEvents) award points too.
- **Ceremony.** When a season ends, the herald proclaims the top houses, the champion and the longest reign. The season goes into the Chronicle (`season_ended`) and into the legends.
- **Hall of Kings.** Every reign is kept: monarch, house, start, end, length and how it ended (left the throne, fell, overthrown by a rebellion, the crown passed, cut short by a wipe).

Status: **compile-checked against the real 2.0.3867 DLLs and behaviour-tested against mocks** (`plugins/docs/RealmEvents/logic-tests/run.sh`, shared with RealmEvents). It has never run on a live server. Smoke test: `docs/smoke-test.md` part E.

## Data files and wipes

| File | Holds | On a wipe |
|---|---|---|
| `oxide/data/RealmSeasons.json` | The running season: standings, chronicle cursor, tracked treaties | You may delete it; the season simply starts fresh |
| `oxide/data/RealmLegends.json` | The Hall of Kings, the open reign, every past season | **Keep it.** It is the realm's memory. If it cannot be parsed, the plugin refuses to run and never writes it |

## Commands

| Command | Who | What it does |
|---|---|---|
| `/season` | anyone | Season, day, end date, leader, your house's place |
| `/season standings` | anyone | Top `StandingsShown` houses with their breakdown |
| `/season house [name]` | anyone | One house's standing and honours |
| `/season hall [page]` | anyone | The Hall of Kings, newest first |
| `/season history` | anyone | Past seasons and champions |
| `/season start [days] [name]` | `realmseasons.admin` | Starts the next season (1–365 days, optional name) |
| `/season end` | `realmseasons.admin` | Holds the ceremony now |
| `/season status` | `realmseasons.admin` | Cursor, counts and which source plugins are loaded |

## Where the numbers come from

| Standing | Source | Verified by |
|---|---|---|
| Crown days | The crown is polled every `TickSeconds`. The name and house come from `CrownAndConsequences.GetKingName/GetKingHouse`. Without that plugin, `KingsScheme` plus `RealmHouses.GetHouse` or the game guild is used. One tick credits at most `MaxCreditSecondsPerTick` seconds, so downtime is never credited | [ASM][USE] KingsScheme; repo APIs |
| How a reign ended | `OnThroneReleased(evt)`, using `evt.IsDeath` [OPJ L633]. An active claim from `GetOpenClaims` whose house now holds the crown means "overthrown by rebellion" | [OPJ][USE RaidBoss.cs:626] |
| Rebellions, treaties broken, oaths broken, contracts | New events in `oxide/data/RealmChronicle.json`. The file is only ever **read**, and only when `RealmChronicle.GetLastEventId()` has moved. The texts are matched against what the repo's plugins write (see the header of the `.cs` file). The behaviour tests fail if those texts change | repo plugins |
| Treaties kept | A treaty seen signed this season that `RealmHouses.HasTreaty` no longer reports, and that was never broken | repo API. UNVERIFIED corner: a treaty dissolved because a house disbanded also counts as kept |
| Event points | `AwardHouse(house, points, honour)`, called by RealmEvents, capped at `MaxEventAwardPerCall` per call | repo API |

## Config (`oxide/config/RealmSeasons.json`)

`DefaultSeasonDays` 28, `AutoStartFirstSeason` true, `AutoEndSeason` true, `AutoStartNextSeason` false, `SeasonNameFormat` "Season {0}", `TickSeconds` 30, `MaxCreditSecondsPerTick` 300, `ReadChronicle` true, `TreatyCheckSeconds` 300, `StandingsShown` 5, `CeremonyTopHouses` 3, `HallPageSize` 6, `MaxHallEntries` 1000, `MaxEventAwardPerCall` 100.

`Weights`: `CrownDay` 10 (per day), `RebellionWon` 25, `RebellionDefended` 15, `TreatyKept` 5, `TreatyBroken` −10, `OathBroken` −10, `ContractFulfilled` 2, `EventPointsFactor` 1.0.

## API (`plugin.Call`, non-public on purpose)

`AwardHouse(string house, int points, string honour) -> bool`, `IsSeasonActive() -> bool`, `GetSeasonNumber() -> int`, `GetSeasonName() -> string`, `GetSeasonStandings() -> List<Dictionary>` (`rank, house, score, crownDays`), `GetHallOfKings() -> List<Dictionary>` (`monarch, house, start, end, ending`). These return names only, never Steam ids.

## What is UNVERIFIED

- The exact moment `OnThroneReleased` fires relative to CrownAndConsequences' own crown change. Only the wording of the ending depends on it (smoke test E3).
- That `Interface.Oxide.DataFileSystem.ReadObject` reading `RealmChronicle.json` while RealmChronicle writes it never catches a half-written file. A failed read is retried on the next tick and nothing is lost, because the cursor only moves after a good read.
