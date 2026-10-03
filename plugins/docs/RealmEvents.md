# RealmEvents: Crown Night, the Royal Tournament, the King's Hunt and the Truce of the Realm

`plugins/RealmEvents.cs` (Oxide 2.0.3867, C# 3). It runs scheduled realm events. Each one has countdown heralds, Chronicle entries, real item prizes and season points (through RealmSeasons).

Status: **compile-checked against the real 2.0.3867 DLLs and behaviour-tested against mocks.** `plugins/docs/RealmEvents/logic-tests/run.sh` runs 89 checks across both RealmSeasons and RealmEvents, wired together the way Oxide wires them. The abuse limits below have their own regression suite, `tools/exploit-review/run.sh events` (26 checks). It has never run on a live server. Smoke test: `docs/smoke-test.md` part E.

## The events

| Kind | Default slot (UTC) | Rests on |
|---|---|---|
| `crown_night` | Saturday 19:00, 90 min. This is the same slot as CrownAndConsequences' default Saturday rebellion window | `GetOpenClaims` for the herald. `OnThroneCaptured` (State `Completed`) [OPJ L607] for capture points. The crown at the end for hold points. It never opens or gates the throne itself |
| `tournament` | Friday 19:00, 60 min | `OnEntityDeath` [OPJ L188; IL]. The victim is `evt.Entity.Owner` and the killer is `evt.KillingDamage.DamageSource.Owner` [ASM; USE DeathMessages.cs:20] |
| `kings_hunt` | Wednesday 21:00, 60 min | `KingsScheme.IsKing` [ASM][USE] to name quarry, and the same death hook |
| `truce` | Sunday 12:00, 240 min | `OnEntityHealthChange` [OPJ L162], RB 1: `Cancel()` + `Damage.Amount = 0` + `return true` [USE NoFriendlyFire.cs:116-117]. [IL] `InvokeDamage` returns early on non-null, the same basis as RealmLaws' King's Peace |

Prizes use the game's own server `/give` path, as in RealmContracts: `AutoMergeAdd` of an `InvGameItemStack`, capped at `StackLimit`, with every amount **measured** with `AutoCount` [IL ThronesCommandHandler.Give]. Prizes are recorded in a persisted owed ledger **before** any item moves. What does not fit waits for `/event collect`, or is paid on rejoin. A crash between the move and the save can lose a prize but can never duplicate one.

## Scheduling rules

- `Schedule` entries: `Id`, `Event`, `Enabled`, `Days` (weekday names, `Daily`, `Weekdays`, `Weekends`), `StartUtc` `"HH:mm"`, `DurationMinutes`. There is also a global per-kind flag: `EnableCrownNight`, `EnableTournament`, `EnableKingsHunt`, `EnableTruce`.
- Times are **UTC with no daylight saving**, so a slot moves by an hour on local clocks when DST changes. CrownAndConsequences' rebellion windows are in realm time (UTC + its `UtcOffsetHours`). If that offset is not 0, shift every `StartUtc` by the opposite amount so Crown Night still lands inside the Saturday window; RealmEvents logs a warning with the corrected time at start-up when the two disagree.
- Countdown heralds go out at each value in `CountdownMinutes` (60, 30, 10, 5, 1). Thresholds missed while the server was down collapse into one herald.
- If the server starts inside a window, the event starts late for the time that is left. Each occurrence runs **once**, even across reloads: fired occurrences are kept in `oxide/data/RealmEvents.json` for nine days.
- A truce never overlaps a fighting event, and an event never overlaps another of its own kind. A clash is skipped and logged. An admin `/event start` is refused with the reason.

## Abuse limits

"Allied" below means the same house, liege or vassal of each other, or bound by a treaty (RealmHouses `GetLiege`, `HasTreaty`). Without RealmHouses only the same house name counts.

**Royal Tournament** (against alts, housemates and allies feeding wins):

- Entrants only (`TournamentRequireJoin`). Housemates never count (`TournamentHousematesCount` false), and nor do allies (`TournamentAlliesCount` false).
- Houses are compared both as they were when each entrant joined and as they are now, so leaving a house for the evening does not make a housemate fair game.
- The victim must be online. A logged-out player's sleeping body is no fight and does not count as a death either.
- Each victim counts at most `TournamentMaxKillsPerVictim` (2) times per killer, and feeds at most `TournamentMaxScoredDeathsPerVictim` (4) points in all, whoever the killers are. One alt cannot feed three mains into the prize places.
- An entrant who leaves cannot enter the same tournament again.
- Prizes and season points need at least `TournamentMinEntrantsForPrizes` (3) entrants who fought (killed or died). A main and an alt alone win nothing.
- None of this needs a house of a given size: a two-member house that fights other houses wins as before.

**King's Hunt** (against farming the prize, the points and the `quarry_taken` renown deed):

- Only the monarch (or an admin) names quarry, up to `HuntMaxTargets` (3), and never themselves or another monarch.
- With `HuntExcludeAllies` (true): no quarry from the crown's own house or houses allied with it (they would hide and "survive" for points). The quarry cannot be claimed by its own house or an allied house, as they are at the kill, and the members of those houses at the moment of naming stay barred for the whole hunt, so leaving the house for the kill does not help.
- `HuntSkipProtectedPlayers` (true): a player under RealmWarden's new-player protection cannot be named. They cannot be harmed, so they would always survive, and naming them is harassment.
- The quarry must be online when slain. A logged-out quarry's body is not a claim.
- The same hunter is paid for the same quarry at most once per `HuntPairCooldownDays` (14). Inside the cooldown the quarry is still taken (it does not survive) but there is no prize, no points and no `hunt_kill` line.
- A quarry "survives the hunt" only if online for at least `HuntSurviveMinOnlinePercent` (75%) of the hunt after being named. Logging off is fleeing: the herald names who fled and their house earns nothing. Survival points go to the house the quarry was named with, and only if they still belong to it.
- Each quarry is claimed once. The claim is saved before the prize is paid, and the prize goes through the owed ledger, so a reconnect and `/event collect` at the same moment pay it exactly once.

**Crown Night:** capture points go to a house once per night.

**Truce:** a breach is chronicled and penalised once per killer per truce. A kill while the truce yields to an open rebellion (`TruceYieldsToRebellion`) is lawful war.

Season points from one call are capped by RealmSeasons' `MaxEventAwardPerCall`.

## Commands

Players: `/events`, `/tourney join|leave|standings`, `/hunt`, `/hunt name <player>` (monarch), `/truce`, `/event collect`.
Admin (`realmevents.admin`): `/event start <kind> [minutes]` (up to `MaxManualMinutes`), `/event stop <kind>` (ends it now with results and prizes), `/event cancel <kind>` (no results).

## Config defaults (`oxide/config/RealmEvents.json`)

| Setting | Default |
|---|---|
| Crown Night | `CrownNightCapturePoints` 10, `CrownNightHoldPoints` 30 |
| Tournament | `TournamentRequireJoin` true, `TournamentHousematesCount` false, `TournamentAlliesCount` false, `TournamentMaxKillsPerVictim` 2, `TournamentMaxScoredDeathsPerVictim` 4 (0 = no cap), `TournamentMinEntrantsForPrizes` 3 (0 = off), `TournamentMinKillsToPlace` 1, `TournamentPlacePoints` [30, 20, 10], `TournamentPrizes` 300 / 200 / 100 Stone |
| Hunt | `HuntMaxTargets` 3, `HuntNamingMinutes` 10, `HuntKillPoints` 15, `HuntSurvivePoints` 10, `HuntPrizes` 200 Wood per quarry, `HuntExcludeAllies` true, `HuntSkipProtectedPlayers` true, `HuntPairCooldownDays` 14 (0 = off), `HuntSurviveMinOnlinePercent` 75 |
| Truce | `TruceEnforced` true, `TruceYieldsToRebellion` true, `TruceBreachPoints` −15 |

Item names are checked at start-up. An unknown name is logged once, and that prize is skipped.

Small servers: with fewer than three people in a tournament, `TournamentMinEntrantsForPrizes` 3 means no prizes. Lower it to 2 (or 0) rather than removing the other limits.

Cross-plugin calls used for the limits: RealmHouses `GetLiege`, `GetVassals`, `HasTreaty`, `GetHouseSummaries`, `GetMembers`; RealmWarden `IsNewPlayerProtected`. Each is optional: without RealmHouses only the same house name counts as allied, and without RealmWarden nobody is treated as protected.

## API (`plugin.Call`, non-public)

- `IsTruceActive() -> bool`
- `GetActiveEvents() -> string[]`, each entry `"kind|startIso|endIso"`

## What is UNVERIFIED

- **Truce enforcement in game:** that cancelling `OnEntityHealthChange` stops every kind of player damage, including arrows, fire and siege weapons (smoke test E5). The fallback is `"TruceEnforced": false`, which turns it into announce-only. Breaches are still chronicled and penalised.
- That `OnThroneCaptured` fires with `State == Completed` for a real capture (shared with CrownAndConsequences, smoke test C9/E10).
- Killer attribution for every weapon type (shared with RealmContracts, smoke test C13).
- That prize items show in the client inventory at once (shared with RealmContracts, `docs/oxide-rok-api.md` 3.9).
- The default prize item names `Stone` and `Wood` resolve through `GetBlueprintForName(name, true, true)` as ResourceType names. Royal Stores relies on the same lookup. Smoke test C10 lists the real names.
- RealmLaws' trial by combat is not exempt from a truce (RealmLaws exposes no "in duel" call). If the two coincide, stop the truce, or schedule them apart.
- Whether the game reports the death of a logged-out player's sleeping body at all, and with which `Entity.Owner`. RealmEvents counts a kill only when the victim is online (`Server.GetPlayerById` finds them), so either way a sleeping body scores nothing.
- That `Server.GetPlayerById` is the right "online" test for the quarry's online share (it is what RealmEvents and RealmContracts already use to pay prizes).
