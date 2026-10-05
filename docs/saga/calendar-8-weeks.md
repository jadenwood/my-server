# Season Calendar Template: 8 Weeks

Copy this file for each season, fill in the blanks (`____`), and pin the result in Discord. Every time is **UTC**. Convert in the Discord post with Discord's own timestamp markup if you like, but keep UTC here, because the plugins run on UTC.

| Field | Value |
|---|---|
| Season | 1: *The Hollow Crown* |
| Season start (Monday, week 1) | `____-__-__` 18:00 UTC (`/season start 56 The Hollow Crown`) |
| Season end (Monday, week 9) | `____-__-__` 18:00 UTC (automatic, 56 days later) |
| Lead Steward | `____` |
| Stewards on shift (rota) | `____` |
| Old Throne (L01) | the game's ancient throne |
| Hearth (L02), zone centre and radius | `____` (set in Act I, step S6) |
| Crown Market (L03), zone centre and radius | `____` (set in Act I, step S6) |
| Other places (L04-L12) | `____` (see `locations.md`) |
| Gatehouse of the Unwritten (L13) | anchored by `/arrival admin site anchor` (`plugins/docs/RealmArrival.md`) |

## The weekly rhythm (read from the plugins)

The slots below are the shipped defaults of CrownAndConsequences (rebellion windows), RealmEvents (realm events) [this run] and RealmWarden (raid hours) [this run]. The checker compares this table with the plugin source and fails if they differ, so the table cannot drift from the code without someone noticing. **A server's own config can override the defaults.** If yours does, read `oxide/config/CrownAndConsequences.json`, `RealmEvents.json` and `RealmWarden.json`, then change the dates below (but not this table, which documents the defaults).

<!-- schedule:begin -->
| Slot | Day | Start (UTC) | Minutes | Source |
|---|---|---|---|---|
| Rebellion window | Wednesday | 19:00 | 60 | CrownAndConsequences |
| Rebellion window | Saturday | 19:00 | 90 | CrownAndConsequences |
| Crown Night | Saturday | 19:00 | 90 | RealmEvents |
| Royal Tournament | Friday | 19:00 | 60 | RealmEvents |
| King's Hunt | Wednesday | 21:00 | 60 | RealmEvents |
| Truce of the Realm | Sunday | 12:00 | 240 | RealmEvents |
| Raid hours | Wednesday | 18:00 | 300 | RealmWarden |
| Raid hours | Saturday | 18:00 | 300 | RealmWarden |
| Raid hours | Sunday | 14:00 | 360 | RealmWarden |
<!-- schedule:end -->

Notes on the rhythm:

- **Raid hours are off by default** (`RaidHours.Enabled` is `false` in RealmWarden, and the feature is UNVERIFIED in game). Leave them out of the public calendar unless you turn them on.
- **The Sunday truce and Sunday raid hours overlap (14:00-16:00) if both are on.** The truce stops blows between *players* (RealmEvents, UNVERIFIED in game). It does not protect walls. Say so in the Discord post, or move one of them in config.
- **Claims need notice.** A claim is put in the first rebellion window that starts at least 60 minutes after it is declared, and a house must wait 72 hours between claims (CrownAndConsequences defaults).
- **The King's Hunt needs a monarch.** With the throne vacant, the hunt is called off by itself. That is expected in week 1.
- **Countdowns.** RealmEvents announces each event 60, 30, 10, 5 and 1 minutes before it starts. Post saga proclamations about 2 hours before, so they don't land on top of the plugin's countdown.

## Week 1: The Empty Seat (Act I)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Mon | `__-__` | 17:00-18:00 | Pre-flight (Act I run-sheet S1-S6) | | | |
| Mon | `__-__` | 18:00 | Season start | *The Seat Stands Empty* | P01 | |
| Mon-Sun | | | Houses founded | *The Great Houses Rise* | P02 | after three of the six great houses |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | | only matters if a claim and a monarch exist |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | *The Hunt That Was Not Called* | | called off if no monarch |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | *The Founding Lists* | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | *Night of First Banners* | P09 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | *Truce of Banners* | P04 if still vacant | |

## Week 2: The Empty Seat (Act I)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Any | | | First coronation, if not yet | *The Bearer of the Hollow Crown* | P03 | |
| Any | | | Oaths, treaties, lines | | P05, P06, P07 | when the Chronicle shows them |
| Any | | | First claim declared | *A Claim at the Hearth* | P08 | |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | if a claim was fought |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | *The First Hunt* | P21 | if a monarch reigns |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | *The Second Lists* | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | **The First Crown Night** (Act I climax) | P09, then P10 / P11 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | *Truce of the First Night* | | |

## Week 3: The Charter Tested (Act II)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Mon | `__-__` | 18:00 | Act II opens | | P12 when the council is named | Act II run-sheet |
| Any | | | First law, first trial | | P13, P14, P15 | crown beats wait for a monarch |
| Any | | | Treasury and contracts | | P16, P17 | |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | | P21 | |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | **The Lists of the Charter** | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | | P09 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | | | |

## Week 4: The Charter Tested (Act II)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Any | | | Title bestowed | *A Title Bestowed* | P19 | |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | | P21 | |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | *The Lists of the Treasury* | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | | P09 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | *The Charter Truce* (Act II close) | | |

## Week 5: The Lawful Hours (Act III)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Mon | `__-__` | 18:00 | Act III opens | | P20 when two claims stand | Act III run-sheet |
| Any | | | Outlaws, bounties, ransoms, spies | | P22, P24, P23 | |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | **The Great Hunt** | P21 | |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | | P09, then P10 / P11 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | | | |

## Week 6: The Lawful Hours (Act III)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Any | | | Blood claims after a fall | *The Blood Answers* | P25 | within 72 h of a fall |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | | P21 | |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | **The Night of Bells** (Act III climax) | P26, then P10 / P11 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | *Truce of the Drowned Bell* | | |

## Week 7: The Reckoning at the Hearth (Act IV)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Mon | `__-__` | 18:00 | Act IV opens: the ledger is read | *The Ledger Is Read* | P27 | Act IV run-sheet |
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | *The Last Hunt but One* | P21 | |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | *The Lists of Reckoning* | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | | P09 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | | | |

## Week 8: The Reckoning at the Hearth (Act IV)

| Day | Date | UTC | What | Saga name | Post | Notes |
|---|---|---|---|---|---|---|
| Wed | `__-__` | 19:00-20:00 | Rebellion window | | P10 / P11 | |
| Wed | `__-__` | 21:00-22:00 | King's Hunt | *The Last Hunt* | P21 | |
| Fri | `__-__` | 19:00-20:00 | Royal Tournament | *The Last Lists* | P18 | |
| Sat | `__-__` | 19:00-20:30 | Crown Night + window | **The Last Crown Night** | P28, then P10 / P11 | |
| Sun | `__-__` | 12:00-16:00 | Truce of the Realm | **The Hearth Truce** | P29 | gathering at L02 |
| Mon (wk 9) | `__-__` | 18:00 | Season ends (automatic) | *The Crown Is Weighed* | P30 | Act IV run-sheet, final steps |
