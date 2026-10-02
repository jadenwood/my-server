# Run-sheet, Act IV: The Reckoning at the Hearth (weeks 7-8)

**Goal of the act:** every house sees the season ledger and plays for it. Then come the last Crown Night, the Hearth Truce and the season-end ceremony.
**Act climax:** the last Crown Night (Saturday of week 8), then the season ending on Monday of week 9 at 18:00.
**Proclamations used:** P27-P30, plus P09-P11 and P18 for the weekly events.

Times are **UTC**. Run the **Every shift** block from the Act I run-sheet at the start and end of each shift. Re-run `node docs/saga/tools/check-saga.mjs` before the act starts.

---

## Before the act (Monday of week 7, 18:00)

| Step | Do | Exact command | Check |
|---|---|---|---|
| J1 | Read the standings | `/season standings` [this run] | note the leading house for P27 |
| J2 | Read the season's state and its end date | `/season status` [this run] | the end date is Monday of week 9 at 18:00 |
| J3 | Post P27 (Say) and its Discord version | (none) | `{house}` = the leader from J1 |

## Timed steps, week 7

| Step | When (UTC) | Watch for | Read it with | Post | Check |
|---|---|---|---|---|---|
| K1 | Wed 19:00 | Rebellion window | `/claim list` | P10 or P11 | |
| K2 | Wed 21:00 | King's Hunt, *the Last Hunt but One* | `/hunt` | P21 | |
| K3 | Fri 19:00 | *The Lists of Reckoning* | `/tourney standings` | P18 | |
| K4 | Sat 17:00-20:30 | Crown Night | `/claim list`, then `/crown` | P09, then P10 or P11 | |
| K5 | Sun 18:00 | Mid-act standings, for Discord | `/season standings` | Discord only: the top three, as printed | |

## Timed steps, week 8

| Step | When (UTC) | Watch for | Read it with | Post | Check |
|---|---|---|---|---|---|
| L1 | Mon-Fri | Claims for the last night (a house must declare at least an hour before the window, and 72 hours after its last claim) | `/claim list` | P08 for any notable claim | |
| L2 | Wed 21:00 | *The Last Hunt* | `/hunt` | P21 | |
| L3 | Fri 19:00 | *The Last Lists* | `/tourney standings` | P18 | |
| L4 | Sat 17:00 | **The Last Crown Night** | `/claim list` | P28 as a Notice, `{time}` = 19:00 | |
| L5 | Sat 19:00-20:30 | Watch it, and do not take part | `/crown` | none during the fight | `rebellion_started` |
| L6 | Sat 20:30 | The crown at the close | `/chronicle 10` | P10 or P11 as a Notice | `event_ended` "Crown Night ends" |
| L7 | Sun 11:30 | Discord: call the realm to the Hearth (L02) for the portrait | (none) | Discord: "The Hearth Truce at 12:00 UTC: banners, not blades." | |
| L8 | Sun 12:00 | **The Hearth Truce** begins by itself | `/truce` | P29 as a Notice, `{time}` = 16:00 | `/truce` says the truce holds |
| L9 | Sun 16:00 | The truce ends | `/chronicle 5` | none | `event_ended` "The Truce of the Realm ends" (or `truce_broken` entries) |
| L10 | Mon (week 9) 17:00 | The final ledger, an hour before the end | `/season standings` | Discord only: "The Hollow Crown closes at 18:00 UTC." | |
| L11 | Mon (week 9) 18:00 | The season ends **by itself** (`AutoEndSeason`). RealmSeasons proclaims the standings and the champion house | `/season history` | P30 as a Notice, `{house}` = the champion RealmSeasons named | `season_ended` in `/chronicle 3` |
| L12 | Mon (week 9) 18:10 | Read the Hall of Kings for the Discord recap | `/season hall` | P30's Discord version, with the top three and the Hall | |
| L13 | Mon (week 9) 18:15 | Save | `/realm.save` (console) | | |

## Branches

| Condition | Then |
|---|---|
| The season did not end at L11 (`/season status` still shows it running a few minutes after 18:00) | Wait one tick (30 s by default). Then end it by hand with `/season end`. The ceremony runs either way. |
| The season has to end early (a wipe is forced, or the server is moving) | `/season end` at a time you announce on Discord 24 hours ahead. Post P30 as normal. The ledger is closed by the ceremony, not by the calendar. |
| Two houses are tied at the top | RealmSeasons breaks ties itself: more crown days first, then house name in alphabetical order. It names that house champion. Do not overrule it. Post P30 naming the house it named, and honour the tied house by name in the Discord version. |
| The season ends **without a champion** (the Chronicle says "... ends without a champion") | Do not post P30. Post the line below as a Notice instead, and in Discord say that no house filled the crown this season. |
| The champion house broke a truce or an oath this season | Say nothing about it in P30. Infamy and the standings have already counted it. The Chronicle remembers. |
| Someone asks the Stewards to settle the lost winter | P30 settles it: the pages stay blank. Never confirm a player's theory. |

The no-champion line for the branch above:

```ingame
The season is ended and the Hollow Crown is weighed, and found empty. No house filled it. The pages of the lost winter stay blank, and so does this one. Ostreval will try again.
```

## After the season

- [ ] Keep `oxide/data/RealmLegends.json` across the wipe. It holds the Hall of Kings and every past season. Deleting `RealmSeasons.json` is safe once the season is over.
- [ ] For the next season: set `"AutoStartFirstSeason"` as needed (it applies only to a server that never ran a season), and start the next one by hand with `/season start <days> <name>`.
- [ ] Update the Roll of Legends post (`../legends.md`), then archive it with the season.
- [ ] File a short report for the saga writers: which proclamations landed, which branch was taken each act, and any command that did not behave as written. That feeds Season 2.
