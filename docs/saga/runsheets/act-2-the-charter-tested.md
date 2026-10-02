# Run-sheet, Act II: The Charter Tested (weeks 3-4)

**Goal of the act:** the crown governs, or fails to. It names a council, proclaims laws, holds the first trial, opens the treasury and the contract board, and the Lists of the Charter are fought.
**Act climax:** the Lists of the Charter (Friday of week 3), and the first verdict, whenever it comes.
**Proclamations used:** P12-P19, plus P09-P11 for every Crown Night.

Times are **UTC**. Run the **Every shift** block from the Act I run-sheet at the start and end of each shift. Check `../COMMANDS.md` and re-run `node docs/saga/tools/check-saga.mjs` before the act starts: most Act II mechanics belong to plugins built this run (RealmLaws, RealmTreasury, RealmDynasties).

**Stewards do not govern.** Laws, council seats, tithes, accusations and bestowals are the **monarch's** commands. The run-sheet tells you what to *watch for* and what to *post*. It never tells you to do these things yourself.

---

## Before the act (Monday of week 3, before 18:00)

| Step | Do | Exact command | Check |
|---|---|---|---|
| C1 | Is anyone crowned? | `/crown` | If vacant, follow the **Vacant crown** branch below |
| C2 | Both law zones must be set (Act I step S6). The default zones sit at placeholder coordinates (0, 0) until set, so a law proclaimed before then guards the wrong place | `/law zone list` [this run] | Hearth and Crown Market have real coordinates |
| C3 | Read the law catalogue so you can explain it to players | `/law catalogue` [this run] | seven laws: `kings_peace`, `market_curfew`, `no_building_towns`, `no_binding_towns`, `banned_weapons`, `bridge_toll`, `harbouring` |
| C4 | Check the treasury's books | `/treasury audit` [this run] | the audit reports no mismatch |
| C5 | Discord: post the Act II opener | (none) | "Act II, *The Charter Tested*. The crown must now govern: council, law, court, coin." |

## What to tell players (paste in Discord at the start of the act)

The crown's tools in Act II, all used **by the monarch or council**, never by Stewards:

```
/council appoint <player> <seat>
/decree roads
/decree stores
/law proclaim kings_peace
/law proclaim bridge_toll
/court accuse <player> <law>
/treasury tithe <percent>
/treasury levy
/dynasty bestow "<line>" <title>
```

Everyone's tools:

```
/law list
/law info kings_peace
/court cases
/court verdict <case> guilty
/court verdict <case> innocent
/market list
/market sell <qty> <price each> <item>
/vault
/contract list
/contract post delivery <qty> "<item wanted>" <amount> "<reward item>"
/tourney join
```

Remind players that `/decree peace`, *the King's Peace*, is a **proclamation only** and no code stops a fight during it. The law `kings_peace` is different: it blocks blows inside the Crown Market zone (RealmLaws, UNVERIFIED in game).

## Timed steps, week 3

| Step | When (UTC) | Watch for | Read it with | Post | Check |
|---|---|---|---|---|---|
| D1 | Any time | The council named (two or more seats) | `/council` | P12 | seats filled |
| D2 | Any time | The first law proclaimed | `/law list` | P13 as a Notice | `law_proclaimed` |
| D3 | Any time | The first accusation | `/court cases` | P14 with `{outlaw}` = the accused's name as the case shows it | `accusation` |
| D4 | Any time | The trial: `/court trial <case>` summons jurors; jurors vote with `/court verdict <case> guilty` or `innocent` | `/court case <id>` | none until the verdict | |
| D5 | When it lands | The first verdict | `/chronicle 5` | P15 | `verdict` |
| D6 | Any time | Treasury: first tithe levied, or the market busy | `/treasury ledger` and `/market list` | P16 | `tithe_levied` or `great_trade` |
| D7 | Any time | Three or more open contracts | `/contract list` | P17 | `contract_posted` |
| D8 | Wed 19:00 | Rebellion window | `/claim list` | P10 or P11 if fought | `rebellion_ended` |
| D9 | Wed 21:00 | King's Hunt | `/hunt` | P21 optional (it is Act III's beat) | |
| D10 | Fri 17:00 | **The Lists of the Charter**: plug it | `/events` | Discord only: "The Lists of the Charter at 19:00 UTC: `/tourney join`." | |
| D11 | Fri 20:00 | The champion | `/chronicle 5` | P18 as a Notice | `tournament_champion` |
| D12 | Sat 17:00 | Crown Night | `/claim list` | P09 | |
| D13 | Sat 20:30 | Crown Night ends | `/crown` | P10 or P11 if fought | `rebellion_ended` |

## Timed steps, week 4

| Step | When (UTC) | Watch for | Read it with | Post | Check |
|---|---|---|---|---|---|
| E1 | Any time | A title bestowed by the crown | `/dynasty list` | P19 | `title_bestowed` |
| E2 | Any time | Any further law, trial, verdict | `/law list`, `/court cases` | none (P13-P15 are for the *first* of each) | |
| E3 | Wed 19:00 | Rebellion window | `/claim list` | P10 or P11 | |
| E4 | Fri 19:00 | Royal Tournament, *the Lists of the Treasury* | `/tourney standings` | P18 | |
| E5 | Sat 17:00-20:30 | Crown Night | `/claim list`, then `/crown` | P09, then P10 or P11 | |
| E6 | Sun 12:00-16:00 | *The Charter Truce* (the regular Sunday truce) | `/truce` | Discord: "Act II closes with the Charter Truce." | `event_ended` "The Truce of the Realm ends" |
| E7 | Sun 16:00 | Close the act | `/realm.save` (console) | Discord: "Act III, *The Lawful Hours*, begins Monday." | |

## Branches

| Condition | Then |
|---|---|
| **Vacant crown** (`/crown` says vacant on Monday of week 3) | Run only D7, D8-D13 and E3-E7: contracts, the tournament and Crown Nights. Players can still use `/market` and `/vault` without a crown. Skip P12-P16 and P19 until someone is crowned, then post them as the beats happen. Stewards do **not** proclaim laws in the crown's place. RealmLaws lets an admin proclaim, but the first coronation after a fresh data file does not make the old laws lapse (`CheckSuccession` lapses laws only when a previous crown was recorded), so an admin-made law would outlive the regency. |
| The crown changes hands in Act II | The council seats empty and the old laws lapse on that succession (CrownAndConsequences `ClearCouncilOnSuccession`, RealmLaws `LawsLapseOnSuccession`). Crown cases are dismissed. Post P11, then watch the new crown repeat D1-D2. Check with `/council` and `/law list` that they really emptied (UNVERIFIED in game). |
| An accused player is offline when tried | This is the court's business, not the saga's. Do not post P14 or P15 until the Chronicle shows the outcome. |
| A law blocks something it should not (for example, a house cannot build in its own seat) | Check `/law zone list`. A house seat must never be a town zone (`../locations.md`). If a zone is wrong, move it with `/law zone set <name> <radius> town` while standing in the right place, or remove it with `/law zone remove <name>`. |
| The treasury audit reports a mismatch | Freeze with `/treasury freeze`, tell the plugin's owners, and post nothing about the treasury until `/treasury unfreeze`. |

## Close-out of Act II

- [ ] `/chronicle 20`: copy the first law, the first verdict and the Lists of the Charter into the Discord recap.
- [ ] `/season standings`: note the leader.
- [ ] `/treasury audit` passes.
- [ ] `/realm.save` (console). Re-run the checker before Act III.
