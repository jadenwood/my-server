# Run-sheet, Act III: The Lawful Hours (weeks 5-6)

**Goal of the act:** the realm answers the crown with claims, rebellions, the King's Hunt, outlaws and bounties, spies and ransoms. If the crown falls from a line, the blood claim follows.
**Act climax:** the Night of Bells, the Saturday Crown Night of week 6.
**Proclamations used:** P20-P26, plus P09-P11 for every Crown Night and rebellion window.

Times are **UTC**. Run the **Every shift** block from the Act I run-sheet at the start and end of each shift. Re-run `node docs/saga/tools/check-saga.mjs` before the act starts.

Act III is the act where Stewards are most tempted to step in. Don't. Rebellions, captures and ransoms are fought under the Charter's own rules: claim notice, windows, capped ransom terms and recapture protection. The plugins enforce those rules. Stewards act only on `rules.md` breaches.

---

## Before the act (Monday of week 5)

| Step | Do | Exact command | Check |
|---|---|---|---|
| F1 | Read the claims and their windows | `/claim list` | note each `{claimant}` and window for P08 and P20 |
| F2 | Read who is held for ransom | `/ransom list` | nobody is past their term. If someone is, see the branch below |
| F3 | Read the public enemies | `/contract enemies` | outlaws and rebels, as the bounty board sees them |
| F4 | Read the court's outlaws | `/court outlaws` [this run] | |
| F5 | Read the lines and any open blood right | `/dynasty list` [this run] | |
| F6 | Discord: post the Act III opener | (none) | "Act III, *The Lawful Hours*. The realm answers the crown." |

## What to tell players (paste in Discord at the start of the act)

```
/claim declare
/claim list
/contract list merc
/contract post merc <amount> "<item>"
/contract accept <id>
/contract enemies
/contract post bounty <player> <amount> "<item>"
/hunt
/ransom list
/ransom set <player> <amount>
/raven spymaster <player>
/raven watch <house>
/raven spy swear <player>
/raven spy report <house>
/rumour <text>
/dynasty claim
```

The monarch's extra tools in this act: `/hunt name <player>` during the King's Hunt, `/contract outlaw <player>` and `/contract pardon <player>`, and `/court pardon <player>`.

## Timed steps, week 5

| Step | When (UTC) | Watch for | Read it with | Post | Check |
|---|---|---|---|---|---|
| G1 | Any time | Two or more open claims at once | `/claim list` | P20 as a Notice, `{n}` = the number of claims | `claim_declared` |
| G2 | Any time | An outlaw declared by the crown or the court | `/contract enemies` | P22 | `contract_posted` (bounties follow) |
| G3 | Any time | The first ransom paid in this act | `/chronicle 10` | P24 | `ransom_paid` |
| G4 | Any time | Spymasters named (players will talk about it, since it is private) | none: it is private | P23, once in the act | |
| G5 | Wed 19:00 | Rebellion window | `/claim list` | P10 or P11 | `rebellion_ended` |
| G6 | Wed 21:00 | **The Great Hunt**: the monarch names quarry | `/hunt` | P21 when the quarry is named | `hunt_kill`, then `event_ended` |
| G7 | Fri 19:00 | Royal Tournament | `/tourney standings` | P18 | |
| G8 | Sat 17:00 | Crown Night | `/claim list` | P09 | |
| G9 | Sat 20:30 | Crown Night ends | `/crown` | P10 or P11 | `rebellion_ended`, `event_ended` |
| G10 | After any fall | A line lost the crown: its heirs have 72 hours | `/dynasty info <line>` | P25 when `blood_claim` appears | `blood_claim` |

## Timed steps, week 6

| Step | When (UTC) | Watch for | Read it with | Post | Check |
|---|---|---|---|---|---|
| H1 | Any time | Blood claims, outlaws, ransoms, as in week 5 | `/chronicle 10` | P22, P24 or P25 as needed | |
| H2 | Wed 19:00 | Rebellion window | `/claim list` | P10 or P11 | |
| H3 | Wed 21:00 | King's Hunt | `/hunt` | P21 | |
| H4 | Fri 19:00 | Royal Tournament | `/tourney standings` | P18 | |
| H5 | Sat 17:00 | **The Night of Bells**: plug it as a Notice and in Discord | `/claim list` | P26, `{time}` = 19:00 | |
| H6 | Sat 19:00 | The Lawful Hours open: post P26 again as Say | `/crown` | P26 | `rebellion_started` |
| H7 | Sat 20:30 | The act's climax resolves | `/chronicle 10` | P10 or P11 as a Notice, and P25 if a blood claim follows | `rebellion_ended`, `event_ended` |
| H8 | Sat 21:00 | Save and close the act | `/realm.save` (console) | Discord: "Act IV, *The Reckoning at the Hearth*, begins Monday." | |

## Branches

| Condition | Then |
|---|---|
| A captive is past their term and still held (`/ransom list` shows them, or they complain) | The captive can type `/ransom free` once the term is over. If that fails, the plugin alerts admins. Help them under `rules.md`. This is not a story beat. |
| No claim stands on a Saturday | The night runs anyway. P09 still reads true. Skip P10 and P11. |
| The crown falls and the fallen line has heirs | Post P11 at once. Watch `/dynasty info <line>` for the next 72 hours. If an heir presses the claim, post P25. If the right lapses, say nothing: the Chronicle records it. |
| A rebellion is won outside the Lawful Hours (an exploit) | CrownAndConsequences gates captures to declared windows, so this should not be possible. If it happens, it is a `rules.md` matter. Record it for the plugin owners. No proclamation. |
| A spy is caught and a house is furious | Good. That is the act working as intended. No proclamation names the spy, because the plugin keeps spies anonymous by default. |
| A rumour names a real person, or leaks a private letter | Reject it with `/raven admin reject <id> [reason]`. For a pattern of abuse, `/raven admin mute <player> <hours>` [this run]. |
| A truce falls during a rebellion window | It doesn't with the defaults: the truce is on Sunday and the windows are on Wednesday and Saturday, and RealmEvents suspends a truce while a rebellion is fought. If you moved the schedule, read `/truce` to see whether it is suspended. |

## Close-out of Act III

- [ ] `/chronicle 25`: copy the rebellions, hunts and the Night of Bells into the Discord recap.
- [ ] `/season standings`: this sets up P27 on Monday.
- [ ] `/renown top` and `/renown top infamy` [this run: RealmRenown]: the act's heroes and villains, for the recap. List names only, as the plugin prints them.
- [ ] `/realm.save` (console). Re-run the checker before Act IV.
