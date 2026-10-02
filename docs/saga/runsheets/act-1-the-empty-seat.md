# Run-sheet, Act I: The Empty Seat (weeks 1-2)

**Goal of the act:** the realm opens, the great houses rise, someone sits the empty throne, and the first Crown Night is fought.
**Act climax:** the Saturday Crown Night of week 2.
**Proclamations used:** P01-P11 (`../proclamations.md`).

Every time below is **UTC**. "Console" means the Realm Steward Court console, where commands run as the server. "In game" means the Steward types the command in game chat on an account that holds the Oxide permissions. The realm plugins' chat commands only work in game: Oxide does not run them from the console (`docs/admin-console.md`). Commands marked [this run] belong to plugins another team is building in this same run, so check `../COMMANDS.md` and re-run the checker before the act starts.

The Steward account that holds these permissions **does not play** for the season: no house, no seat, no captives, no throne (`docs/community/rules.md`).

---

## Pre-flight (launch day, before 18:00)

| Step | When | Do | Exact command | Check |
|---|---|---|---|---|
| S0 | T-7 days | Run the saga checker and fix any error | `node docs/saga/tools/check-saga.mjs` | prints `0 error(s)` |
| S1 | T-60 min | Stop RealmSeasons from opening its own season. In `oxide/config/RealmSeasons.json` set `"AutoStartFirstSeason": false`, then reload | `oxide.reload RealmSeasons` (console) | `/season` in game says no season is running |
| S2 | T-55 min | Grant the Steward account its permissions (console). Replace `<steward>` with the Steward's in-game name or SteamID64 | see block below | `/claim list` works, and an admin-only command such as `/season status` answers instead of refusing |
| S3 | 18:00 | Open the season: 56 days, so it ends on Monday of week 9 at 18:00 | `/season start 56 The Hollow Crown` [this run] | Herald broadcast, then `/season` shows *The Hollow Crown*, day 1 of 56. `/chronicle 3` shows `season_started` |
| S4 | 18:00 | Post P01 as a Notice (Court → Notice) and in Discord | `/notice <P01 line>` (console) | the notice shows on screen in game (UNVERIFIED at run time) |
| S5 | 18:05 | Confirm the weekly events are scheduled | `/events` [this run] | "Next:" names the Wednesday events |
| S6 | Day 1-3 | Stand at the centre of each law zone and set it (`../locations.md` L02, L03) | `/law zone set Hearth 40 town` and `/law zone set Crown Market 60 town` [this run] | `/law zone list` shows both with real coordinates, not 0/0 |
| S7 | Day 1 | Save the world after the opening | `/realm.save` (console) | the server log shows `Saving game...` |

The S2 permission grants, one line per plugin, all in the console:

```
oxide.grant user <steward> crownandconsequences.admin
oxide.grant user <steward> realmhouses.admin
oxide.grant user <steward> realmcontracts.admin
oxide.grant user <steward> realmseasons.admin
oxide.grant user <steward> realmevents.admin
oxide.grant user <steward> realmlaws.admin
oxide.grant user <steward> realmravens.admin
oxide.grant user <steward> realmtreasury.admin
oxide.grant user <steward> realmdynasties.admin
oxide.grant user <steward> realmrenown.admin
oxide.grant user <steward> realmwarden.admin
```

**If a season already started on its own** (S1 was missed): type `/season history` in game. If this server has run no season before, so the only record is the one that just opened automatically, stop the server, back up `oxide/data/RealmSeasons.json` and `oxide/data/RealmLegends.json`, delete both, set S1 and start again. Otherwise *The Hollow Crown* is entered as season 2. **Never** delete `RealmLegends.json` on a server with real past seasons. It holds the Hall of Kings.

## Every shift (start and end)

Run these in game at the start of every shift and again at the end. They only read state.

```
/crown
/claim list
/chronicle 10
/events
/season standings
/raven admin queue
/warden alerts
```

- **Rumours** (`/raven admin queue`) [this run: RealmRavens]: approve with `/raven admin approve <id>` or reject with `/raven admin reject <id> [reason]`. Approve a rumour when it is in character, names no one for an out-of-game reason, and does not make up a Chronicle event. Reject harassment, real-world information and spoilers about other players' private letters. Never write a rumour yourself.
- **Warden alerts** (`/warden alerts`) [this run: RealmWarden]: handle these under `rules.md`, then mark them done with `/warden ack <id>`. Moderation never goes into a proclamation.
- **End of shift:** `/realm.save` in the console.

## Timed steps, week 1

| Step | When (UTC) | Do | Exact command | Post | Check |
|---|---|---|---|---|---|
| A1 | Mon-Wed | Watch the great houses being founded. Disband a squatter only under the sign-up rules | `/house list` | P02 once three of the six are founded | `/house list` shows them |
| A2 | Any time | Watch for the first sitting of the empty throne | `/crown` | P03 within the hour, as a Notice | `/chronicle 5` shows `coronation` |
| A3 | Wed 19:00 | The first rebellion window. It matters only if a monarch reigns and a claim stands | `/claim list` | P10 or P11 if a claim was fought | `rebellion_ended` in `/chronicle 5` |
| A4 | Wed 21:00 | The King's Hunt. With no monarch it calls itself off, which is expected | `/events` | none | `event_ended` "called off", or a hunt in progress |
| A5 | Fri 17:00 | Plug *The Founding Lists* in Discord | (none) | Discord only: "The Founding Lists open at 19:00 UTC, enter with `/tourney join`" | |
| A6 | Fri 19:00-20:00 | The Royal Tournament runs by itself | `/tourney standings` | P18 after it ends | `tournament_champion` |
| A7 | Sat 17:00 | Night of First Banners: post the Crown Night proclamation | `/claim list` | P09 (if no claim stands, its line still reads true) | |
| A8 | Sat 20:30 | Crown Night ends | `/crown` | P10 or P11 if a claim was fought | `event_ended` "Crown Night ends" |
| A9 | Sun 12:00 | The Truce of the Realm starts by itself | `/truce` [this run] | none | `/truce` says the truce holds |
| A10 | Sun 18:00 | Is the throne still empty? | `/crown` | **only if vacant:** P04 | |

## Timed steps, week 2

| Step | When (UTC) | Do | Exact command | Post | Check |
|---|---|---|---|---|---|
| B1 | Any time | The first oaths | `/chronicle 10` | P05 after two or three `oath_sworn` | |
| B2 | Any time | The first treaties | `/chronicle 10` | P06 after the first `treaty_signed` | |
| B3 | Any time | The first bloodlines | `/dynasty list` [this run] | P07 after the first `dynasty_founded` | |
| B4 | Any time | The first claim. Note its window from `/claim list` | `/claim list` | P08 with `{day}`/`{time}` from the claim line | `claim_declared` |
| B5 | Wed 19:00 | Rebellion window | `/claim list` | P10 or P11 | `rebellion_ended` |
| B6 | Wed 21:00 | King's Hunt (if a monarch reigns) | `/hunt` | none (RealmEvents announces it) | `hunt_kill` or `event_ended` |
| B7 | Fri 19:00 | Royal Tournament, *the Second Lists* | `/tourney standings` | P18 | `tournament_champion` |
| B8 | Sat 17:00 | **The First Crown Night** | `/claim list` | P09 as a Notice, `{time}` = 19:00 | |
| B9 | Sat 19:00-20:30 | Watch it, and do not take part | `/crown` | none during the fight | `rebellion_started` |
| B10 | Sat 20:30 | The act's climax resolves | `/chronicle 10` | P10 (held) or P11 (taken) as a Notice | `rebellion_ended`, `event_ended` |
| B11 | Sat 21:00 | Save, and mark the end of the act | `/realm.save` (console) | Discord: "Act I is over. Act II, *The Charter Tested*, begins Monday." | |

## Branches

| Condition (how to read it) | Then |
|---|---|
| `/crown` still says the throne is vacant at A10 | Post P04. Change nothing else, because the story waits for the realm. If the throne is still empty on Monday of week 3, Act II runs its crown-free beats only (see the Act II run-sheet). |
| A great-house name was founded against the sign-up | This is moderation, not story: follow `rules.md` and `launch-plan.md`. `/house disband <house>` is the admin tool. Do not mention it in a proclamation. |
| A claim was raised by a house that broke the rules | Use `/claim cancel <house>` (moderation only). The Chronicle records that "the realm's stewards" set it aside. Say nothing more in the saga. |
| The first Crown Night has no claim | The night still runs, and RealmEvents says no claim stands. Post P09 as written. Skip P10 and P11. |
| A command answers "unknown" or its usage has changed | Its plugin was changed this run. Re-run the checker, read `../COMMANDS.md`, and use the plugin's own help (`/<command> help`, or the command with no arguments). |

## Close-out of Act I

- [ ] `/chronicle 20`: copy the act's main entries (first coronation, first claim, the First Crown Night) into the Discord recap.
- [ ] `/season standings`: note the leader for P27 later.
- [ ] `/realm.save` (console).
- [ ] Run the checker again before Act II.
