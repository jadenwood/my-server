# Season 1: The Hollow Crown

> *A crown is only hollow metal until the realm fills it with oaths, with law, with coin or with blood.*
> (spoken at the Hearth when a season opens)

This is the storyline for the first eight-week season on the Realm server. It is written for Stewards (admins), writers and streamers. Players meet it through herald lines, Discord posts and the Chronicle, and they change it through what they do in game.

The rule of the saga: **the story never decides an outcome.** Stewards don't pick winners, don't play, and don't move the crown. Every turn in the plot is something players do with a real command, and the Chronicle records it. The saga names those moments, gives them stakes and tells the realm about them.

## The premise

At the end of the Long Thaw the Hearth Charter gave the Old Throne to whoever holds the seat (`lore.md`). Since then the throne has passed from hand to hand. Last winter it went empty again. No one knows who sat it last, because the Chronicle's pages for that winter were lost when the realm was made new (the server wipe, in plain words).

When Season 1 opens, the realm finds three things:

1. **The seat is empty.** Under the Charter, anyone may sit an empty throne. Whoever does is crowned.
2. **The crown is hollow.** A new monarch starts with little authority: 30 of a possible 100, rising half a point each minute (CrownAndConsequences defaults; `/crown` shows it). The heralds call this *the Hollow Crown*. Until the realm's patience fills it, a monarch can decree little.
3. **The Stewards of the Seat are watching.** The Stewards are the keepers of the Charter, the in-world voice of the admins. They hold no land and wear no colours, and they never sit the throne. They speak through the Hearth-Herald, and in this pack every proclamation is in that voice.

The season asks one question: **who fills the Hollow Crown, and how?** A house can do it by holding the throne, by oaths that hold, by laws that are obeyed, by coin, or by the blade in the Lawful Hours. At season end RealmSeasons proclaims the champion house from the standings: crown days, rebellions won or defended, treaties kept or broken, oaths broken, contracts fulfilled and realm-event points. The house that tops them has filled the crown. The Hall of Kings remembers every reign beyond the wipe.

## The voices

| Voice | Who speaks it | Where it appears |
|---|---|---|
| **The Hearth-Herald** | The Steward on shift, by pasting lines from `proclamations.md` | Server chat, `/notice`, Discord |
| **The Herald** (plugin) | CrownAndConsequences, RealmEvents, RealmLaws and others, automatically | Server chat with the `Herald:` prefix |
| **The Chronicle** | RealmChronicle, automatically | `/chronicle`, the overlay, the portal, the Discord herald |
| **Rumour** | Players through `/rumour <text>`. A Steward approves each one first | Chronicle type `rumour` [this run: RealmRavens] |

Never put words in a player's mouth. A proclamation names what a player *did* (what the Chronicle shows). It never names what they meant to do.

## The shape of the season

| Act | Weeks | Title | The question | Mechanics in the spotlight |
|---|---|---|---|---|
| I | 1-2 | **The Empty Seat** | Who sits first, and who kneels to whom? | `/house`, the vacant-throne rule, `/swear`, `/treaty`, `/dynasty found`, `/raven` |
| II | 3-4 | **The Charter Tested** | Can the crown make law, keep a court and fill a treasury? | `/law`, `/court`, `/council`, `/decree`, `/treasury`, `/market`, `/contract`, the Royal Tournament |
| III | 5-6 | **The Lawful Hours** | Will the crown hold when the realm rises against it? | `/claim declare`, rebellion windows, Crown Night, the King's Hunt, `/ransom`, outlaws and bounties, spymasters, `/dynasty claim` |
| IV | 7-8 | **The Reckoning at the Hearth** | Who has filled the crown when the season closes? | `/season standings`, the last Crown Night, the Hearth Truce, the season-end ceremony, the Hall of Kings |

The plugins set the weekly rhythm, and every act uses the same rhythm (`calendar-8-weeks.md`). With the shipped defaults, all times UTC:

- **Wednesday:** a rebellion window 19:00-20:00, then the King's Hunt 21:00-22:00.
- **Friday:** the Royal Tournament 19:00-20:00.
- **Saturday:** a rebellion window and Crown Night together, 19:00-20:30.
- **Sunday:** the Truce of the Realm, 12:00-16:00.

The saga does not add events. It **names** the ones that happen anyway. The first Friday tournament is the Founding Lists, the Saturday of week 6 is the Night of Bells, and so on.

---

## Act I: The Empty Seat (weeks 1-2)

**Story.** The realm wakes. The six great houses of `lore.md` are founded by the groups that won the claim sign-up, and new houses rise beside them. The throne stands empty, and the Charter's oldest exception applies: anyone may sit it. The first to do so is crowned *Bearer of the Hollow Crown*. Oaths are sworn, treaties are sealed and the first ravens fly. Behind it all a question grows: who sat the throne last winter? (See *The lost winter* below.)

**Beats and the mechanic behind each one**

| Beat | What players do | What proves it | Proclamation |
|---|---|---|---|
| The realm opens | Stewards start the season (`/season start 56 The Hollow Crown`) [this run: RealmSeasons] | `season_started` in the Chronicle | P01 |
| The great houses rise | `/house found <name> <sigil>` by the sign-up winners | `house_founded` | P02 |
| The first sitting | A player takes the empty Old Throne. Capture is open while the throne is vacant (`AllowCaptureWhenThroneVacant`) | `coronation` | P03 (or P04 if nobody sits by the end of week 1) |
| Bending the knee | `/swear <house>` then `/swear accept <house>` | `oath_sworn` | P05 |
| Sealed truces | `/treaty propose <house> [days]` then `/treaty accept <house>` | `treaty_signed` | P06 |
| Bloodlines | `/dynasty found <name>` [this run: RealmDynasties] | `dynasty_founded` | P07 |
| The first claim | A house leader types `/claim declare` | `claim_declared` | P08 |
| The first Crown Night (Saturday, week 2) | Declared claimants fight for the throne in the Lawful Hours | `rebellion_started`, `rebellion_ended`, `event_ended` | P09, then P10 or P11 |

**Act I ends** after the Saturday Crown Night of week 2. At that point the realm either has its first proven monarch or a crown that was taken from them.

## Act II: The Charter Tested (weeks 3-4)

**Story.** A crown that only sits is still hollow. Now the monarch must govern. Laws are proclaimed and the court sits for the first time. The treasury is opened, and the houses learn what the tithe costs. Merchants post their first contracts. In the Founding Lists, and then in the Lists of the Charter, the realm's best blades show what they are worth. The monarch may raise a line by bestowing a title on it, and every bestowal makes an enemy.

| Beat | What players do | What proves it | Proclamation |
|---|---|---|---|
| The council is named | `/council appoint <player> <seat>` (the three seats) | Herald line from CrownAndConsequences | P12 |
| The first law | The monarch types `/law proclaim <id>`, for example `/law proclaim kings_peace` for the Crown Market [this run: RealmLaws] | `law_proclaimed` | P13 |
| The first trial | `/court accuse ...`, then `/court trial <case>`, then the jurors' `/court verdict <case> guilty` or `innocent` [this run] | `accusation`, `verdict` | P14, then P15 |
| The treasury opens | `/treasury tithe <percent>`, `/treasury levy`, `/market sell ...` [this run: RealmTreasury] | `tithe_levied`, `great_trade` | P16 |
| Contracts on the board | `/contract post delivery ...` | `contract_posted`, `contract_fulfilled` | P17 |
| The Lists of the Charter (Friday, week 3 or 4) | `/tourney join`, then fight | `tournament_champion` | P18 |
| A title bestowed | `/dynasty bestow "<line>" <title>` [this run] | `title_bestowed` | P19 |

**Act II ends** on the Sunday Truce of week 4.

## Act III: The Lawful Hours (weeks 5-6)

**Story.** The realm has seen how this crown governs, and now it answers. Houses that were taxed, tried or passed over declare their claims. Rebellion windows that stood empty in Act I now fill. The monarch sends the King's Hunt after their enemies. The court declares outlaws, and bounties go up on the board. Spymasters read the ravens of rival houses. If the crown falls from a line, that line's heirs have 72 hours to press the blood claim and win it back.

| Beat | What players do | What proves it | Proclamation |
|---|---|---|---|
| The claims pile up | Several leaders type `/claim declare` | `claim_declared` (check with `/claim list`) | P20 |
| The King's Hunt (Wednesday) | The monarch types `/hunt name <player>`. Hunters kill the quarry | `hunt_kill`, `event_ended` | P21 |
| Outlaws and bounties | `/contract outlaw <player>` (monarch), `/contract post bounty ...` | `contract_posted`, `contract_fulfilled` | P22 |
| Spies in the rookery | `/raven spymaster <player>`, `/raven watch <house>` [this run: RealmRavens] | Private to players (letters are not chronicled). Only approved rumours (`rumour`) are | P23 |
| Ransom | `/ransom set <player> <amount>` | `ransom_set`, `ransom_paid`, `released` | P24 |
| The crown falls, the blood answers | An heir of the fallen line types `/dynasty claim` [this run] | `blood_claim`, then later `blood_restored` | P25 |
| The Night of Bells (Saturday, week 6) | The biggest Crown Night of the season | `rebellion_ended` | P26 |

**Act III ends** after the Night of Bells.

## Act IV: The Reckoning at the Hearth (weeks 7-8)

**Story.** The season's ledger is nearly closed. Every house can see the standings (`/season standings`), and every house knows what one more crown day, one kept treaty or one won rebellion is worth. The last Crown Night falls on Saturday of week 8. On the Sunday after, the Hearth Truce gathers the realm at the great fire, where every house stands under its banner without blades. When the season ends, RealmSeasons proclaims the champion house and the Hall of Kings takes in every reign of the season.

| Beat | What players do | What proves it | Proclamation |
|---|---|---|---|
| The ledger is read | Everyone types `/season standings` | (read only) | P27 |
| The last Crown Night (Saturday, week 8) | The final fight for the throne | `rebellion_ended`, `event_ended` | P28 |
| The Hearth Truce (Sunday, week 8) | No blood is shed. A player kill is a breach | `event_ended`, or `truce_broken` | P29 |
| The season closes | Season end, automatic at 56 days, or a Steward types `/season end` | `season_ended`. The Hall of Kings is in `/season hall` | P30 |

## The lost winter (the season's mystery)

The mystery is a framing device, not a mechanic, and it never decides who wins. It gives roleplayers, rumour-mongers and streamers something to chase.

- **The question:** who sat the Old Throne last winter, and why did they leave it empty?
- **How it is fed:** through rumours. Players post `/rumour <text>`, and a Steward approves the good ones with `/raven admin approve <id>` [this run: RealmRavens]. Stewards **never** write rumours themselves and never confirm or deny one.
- **How it is answered:** it never is, officially. In P30, the season-end proclamation, the Hearth-Herald says only that *the pages of the lost winter stay blank, and the realm must write its own*. The realm's real history is what the Chronicle recorded this season.
- **Why it works:** every faction can claim the lost winter. Dunmere says it was theirs (*The Tide Returns*). Corvane says it holds the letters (*Every Secret Has a Price*). Varrow says it kept the seat warm. These claims bring roleplay, rumours and grudges, and they cost the Stewards nothing to referee.

## Branches the Stewards follow

The run-sheets give exact checks. In short:

| If, when the run-sheet checks... | Then |
|---|---|
| no one has sat the throne by the end of week 1 (`/crown` says the throne is vacant) | Post P04 (the seat still waits). Change nothing else. The story waits for the realm. |
| the throne is vacant at the start of Act II | Act II keeps its tournament, contracts, treasury vaults and market beats. The crown beats (law, council, court, tithe, bestow) wait until someone is crowned. Do **not** proclaim laws as admin. |
| a Crown Night ends with the crown held (`rebellion_ended`: "The crown held...") | Post P10 (the crown held). |
| a Crown Night ends with the crown taken (`rebellion_ended`: "House X prevailed...") | Post P11 (the crown taken). In Act III, also watch for the fallen line's blood claim (P25). |
| no claim is declared before a Crown Night | The night still runs. Post P09 with "no claim stands", which RealmEvents' own countdown also says. |
| a truce is broken (`truce_broken`) | Post nothing extra: RealmEvents' Herald already names the breaker. Do not punish the player. The plugin already takes points from their house, and renown already marks them. |
| a rule is broken (an exploit, harassment, a throne taken outside the Charter) | Leave the saga alone. Follow `docs/community/rules.md`. `/claim cancel <house>` exists for a claim that was raised against the sign-up, but it is a moderation tool, not a story tool. |

## What the saga must never do

- Promise a mechanic that does not exist. For example, the Peace of the Crown decree (`/decree peace`) is a proclamation only. No code stops combat during it. The RealmLaws law `kings_peace` and the Truce of the Realm are the ones that do block damage, and both are UNVERIFIED in game.
- Use names from any book, film, TV series or game other than this server's own.
- Name a player in a proclamation for something the Chronicle does not show.
- Have a Steward hold a house, a council seat, a captive or the throne (`rules.md`).
