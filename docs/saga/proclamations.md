# Proclamations of the Hearth-Herald (Season 1)

Thirty herald lines for *The Hollow Crown*, in story order. Each entry has:

- **When:** the moment to post it, and the run-sheet step that calls for it.
- an ```` ```ingame ```` block: one line to paste into Realm Steward → Court → **Say**, **Notice** or **Popup**, or into game chat. It is plain ASCII with no quotes or apostrophes (`README.md` explains why) and fits in 300 characters with every placeholder filled.
- a ```` ```discord ```` block: a longer version for the announcements channel. It never pings anyone.

**Placeholders.** Replace each `{placeholder}` before posting: `{monarch}`, `{house}`, `{claimant}`, `{crown_house}`, `{champion}`, `{quarry}`, `{line}`, `{outlaw}`, `{day}`, `{time}`, `{location}`, `{n}`. Copy names exactly as `/crown`, `/house list`, `/chronicle` or `/dynasty list` print them. Never name a player for something the Chronicle does not show.

**What the plugins already announce.** The plugins' own Herald already announces decrees, claims, rebellion starts, Crown Night countdowns, tournament results, hunt kills and truce breaches. The Steward's Discord herald already relays Chronicle events. These proclamations add the *story* around those moments, so do not paste plugin output back into chat.

---

## Act I: The Empty Seat

### P01 The Seat Stands Empty

**When:** Day 1, right after `/season start 56 The Hollow Crown` (Act I run-sheet, step S3). Use Notice.

```ingame
Hear the Hearth-Herald. The Old Throne stands empty and the crown upon it is hollow. By the Charter any hand may take an empty seat. Season One, The Hollow Crown, begins now.
```

```discord
**The Seat Stands Empty**

Hear the Hearth-Herald. Last winter's pages are lost, and the Old Throne stands empty. The crown that waits on it is hollow: whoever takes it will hold little authority until the realm's patience fills it.

By the Hearth Charter, any hand may take an empty seat. **Season One: The Hollow Crown** begins now and runs eight weeks.

Found or claim your house, swear your oaths, and watch the Chronicle.
```

### P02 The Great Houses Rise

**When:** Day 1-3, once at least three of the six great houses are founded (`/house list`). Use Say.

```ingame
The banners rise again in Ostreval. Varrow, Ashgrove, Corvane, Dunmere, Halloran and Merrin are called to the Hearth, and every new house beside them. Know your sigil, know your words.
```

```discord
**The Great Houses Rise**

The banners rise again in Ostreval. The Iron Stag, the White Oak, the Black Raven, the Drowned Bell, the Ember Hound and the Silver Eel are called to the Hearth, and so is every new house that dares to stand beside them.

Use `/house list` to see who has raised a banner, and `/house info <house>` to see a house's record. Ashgrove's clean record and Corvane's loopholes start from zero this season.
```

### P03 The Bearer of the Hollow Crown

**When:** The first `coronation` of the season. Check with `/crown`, then post within the hour. Use Notice.

```ingame
The empty seat is taken. {monarch} of House {crown_house} sits the Old Throne and bears the Hollow Crown. Its authority is small and the realm is watching. Long may it last, or not.
```

```discord
**The Bearer of the Hollow Crown**

The empty seat is taken. **{monarch}** of **House {crown_house}** sits the Old Throne.

The crown is hollow. Its bearer starts with little authority, and every decree must wait for the realm's patience to return. Houses may now swear to the crown, or begin counting the days to the Lawful Hours.

`/crown` shows who reigns and the next rebellion window.
```

### P04 The Seat Still Waits

**When:** Sunday of week 1, **only if** `/crown` still says the throne is vacant. Use Say.

```ingame
A week has passed and the Old Throne is still empty. The Stewards will not fill it. The Charter waits for a hand bold enough, and the first to sit it will be remembered as first.
```

```discord
**The Seat Still Waits**

A week has passed and the Old Throne is still empty. The Stewards of the Seat keep the Charter and do not sit the throne. Only the realm can fill it.

While the seat is empty, anyone may take it. The first to sit it is crowned and will be remembered as the first of this season in the Hall of Kings.
```

### P05 The Bent Knee

**When:** After the first two or three `oath_sworn` entries (`/chronicle 10`). Use Say.

```ingame
Oaths are sworn at the Hearth. Houses bend the knee and lieges take them in. Remember that the Charter forbids no betrayal. It only makes sure all of Ostreval knows of it.
```

```discord
**The Bent Knee**

Oaths are sworn at the Hearth. Houses bend the knee with `/swear <house>`, and lieges accept them.

Remember the Charter: it forbids no betrayal. It only makes sure that everyone knows. A house that renounces its oath carries the oathbreaker's mark in `/house info` for the rest of the season, and in the season standings too.
```

### P06 Sealed Truces

**When:** After the first `treaty_signed`. Use Say.

```ingame
The first treaties of the season are sealed. A treaty kept to its term is honour in the season ledger. A treaty torn up is remembered longer than the war it ended.
```

```discord
**Sealed Truces**

The first treaties of the season are sealed with `/treaty propose` and `/treaty accept`.

A treaty kept to its term counts for the house in the season standings. A treaty broken counts against it. Choose your days carefully.
```

### P07 The Bloodlines Begin

**When:** After the first `dynasty_founded` [this run: RealmDynasties]. Use Say.

```ingame
A line is founded in Ostreval: the line of {line}. Houses rise and fall, but blood remembers. A line whose monarch loses the throne may press the blood claim within three days.
```

```discord
**The Bloodlines Begin**

The line of **{line}** is founded. Houses are banners, but lines are blood. Heirs are named with `/dynasty heir name <player>`, and the family tree grows with every generation (`/dynasty tree`).

If the crown ever falls from a monarch of a line, that line's heirs may press the **blood claim** (`/dynasty claim`) within 72 hours, through their house's own claim.
```

### P08 A Claim at the Hearth

**When:** The first `claim_declared` of the season (`/claim list`). Use Say.

```ingame
A claim is raised at the Hearth. House {claimant} declares against the crown and will fight in the Lawful Hours on {day} at {time} UTC. The realm will see whether the crown holds.
```

```discord
**A Claim at the Hearth**

**House {claimant}** has declared its claim to the Old Throne. By the Charter, the throne may be fought for only in the Lawful Hours. This claim is set for **{day} at {time} UTC**.

Sworn houses: choose your side. Hired swords: `/contract list merc` shows who is paying.
```

### P09 Crown Night Falls

**When:** About 2 hours before any Saturday Crown Night (19:00 UTC by default). RealmEvents' own countdown starts 60 minutes before. Use Say.

```ingame
Tonight is Crown Night. At {time} UTC the Lawful Hours open and declared houses may take the Old Throne. Check /claim list and choose your banner. The Hearth will be watching.
```

```discord
**Crown Night Falls**

Tonight at **{time} UTC** the Lawful Hours open. Only houses with a declared claim may contest the throne (`/claim list`). If no claim stands, the night passes in peace.

The house that holds the crown when the night ends earns the hold points in the season standings. Every house that takes the throne during the night earns points too.
```

### P10 The Crown Held

**When:** After a Crown Night or rebellion window that ends with "The crown held" (`rebellion_ended`). Use Say.

```ingame
The Lawful Hours are over and the crown held. {monarch} of House {crown_house} still sits the Old Throne, and House {claimant} withdraws with its claim spent. The Hollow Crown grows heavier.
```

```discord
**The Crown Held**

The Lawful Hours are over and **the crown held**. {monarch} of **House {crown_house}** still sits the Old Throne. **House {claimant}**'s claim is spent, and it must wait before it may press another.

Defenders who stood for the crown are written in the Chronicle, and the crown's house gains in the season standings.
```

### P11 The Crown Taken

**When:** After a rebellion that ends with "House X prevailed" (`rebellion_ended`). Use Notice.

```ingame
The Lawful Hours have spoken. House {claimant} has taken the Old Throne, and {monarch} now bears the Hollow Crown. The old council is dismissed and the old laws are tested. Ostreval turns.
```

```discord
**The Crown Taken**

The Lawful Hours have spoken. **House {claimant}** has taken the Old Throne, and **{monarch}** now bears the Hollow Crown.

By the Charter, the council seats empty when the crown changes hands, and the old crown's laws lapse. If the fallen monarch founded a line, their heirs have 72 hours to press the blood claim.
```

## Act II: The Charter Tested

### P12 The Council Is Named

**When:** After the monarch fills at least two seats (`/council` shows them). Use Say.

```ingame
The council of the crown is named. A Voice of the Crown, a Keeper of Coin, a Marshal. They serve at the pleasure of the throne and fall with it. Ostreval has a government.
```

```discord
**The Council Is Named**

The crown has filled its council (`/council`): the **Voice of the Crown**, the **Keeper of Coin** and the **Marshal**.

The Voice may proclaim in the monarch's name. The Keeper of Coin may act for the treasury. Every seat empties when the crown changes hands.
```

### P13 The First Law

**When:** After the first `law_proclaimed` [this run: RealmLaws]. Use Notice.

```ingame
The crown has made law. Read it with /law list and the full text with /law info and the law id. A breach is written in the crime ledger. Ignorance of the law is no defence at the Hearth.
```

```discord
**The First Law**

The crown has proclaimed the season's first law. Read the laws in force with `/law list`, and the full text of one with `/law info <id>`.

Some laws are **enforced**: the realm sees the breach and writes it down, and some are blocked outright. Others are **declared**: they are tried only on accusation. Either way, the court may sit.
```

### P14 The Court Is Called

**When:** After the first `accusation`, when `/court cases` shows a case going to trial. Use Say.

```ingame
The court of the crown is called. {outlaw} stands accused. Lords of the realm may be summoned to sit as jurors. The accused may demand trial by combat.
```

```discord
**The Court Is Called**

The court of the crown is called. **{outlaw}** stands accused under the laws of Ostreval (`/court cases`).

The jury is drawn from the heads of houses who are not party to the case, and the crown cannot choose it. The accused may demand **trial by combat** (`/court combat <case>`), and the accuser then names a champion.
```

### P15 The Verdict

**When:** After the first `verdict`. Use Say.

```ingame
The jurors have spoken and the verdict is written. Whatever the sentence, the court has sat and the Charter is tested. Let every house remember that the crown can judge.
```

```discord
**The Verdict**

The jurors have spoken. The verdict is written in the Chronicle, and so is the sentence, if there is one.

An acquittal costs the crown some of its right to accuse. A conviction proves the crown can judge. Either way, the first trial of *The Hollow Crown* is history.
```

### P16 The Treasury Opens

**When:** After the first `tithe_levied`, or once the market has several listings (`/market list`) [this run: RealmTreasury]. Use Say.

```ingame
The treasury of Ostreval is open. Houses keep vaults, traders post wares at the market, and the crown may levy a tithe within the ceiling of the Charter. Coin is a kind of crown too.
```

```discord
**The Treasury Opens**

The treasury of Ostreval is open for business.

- `/market list`: what is for sale and what is wanted.
- `/vault`: your house's vault, run by its head and stewards.
- `/treasury`: the crown's coffers, kept in marks.

The Charter caps the tithe. Every mark that is minted, levied or paid shows in the ledger (`/treasury ledger`).
```

### P17 The Contract Board

**When:** When `/contract list` shows three or more open contracts. Use Say.

```ingame
The contract board fills. Deliveries wanted, swords for hire, prices on heads. Small houses can matter here. Read /contract list and take what you can carry.
```

```discord
**The Contract Board**

The contract board at the Hearth is filling up. `/contract list` shows open bounties, deliveries and mercenary contracts.

Escrow is real: whatever the poster offers is held until the work is proven. Small houses and lone blades can make a name for themselves here, and a name is worth renown.
```

### P18 The Lists of the Charter

**When:** After a Friday Royal Tournament ends with a champion (`tournament_champion`). Use Notice.

```ingame
The lists are closed. {champion} is Champion of the Lists, and House {house} carries the honour into the season ledger. Let the blades rest until the next Friday.
```

```discord
**The Lists of the Charter**

The Royal Tournament is over. **{champion}** is Champion of the Lists, and **House {house}** carries the honour into the season standings.

The champion may now wear the title *Champion of the Lists* with `/titles set Champion of the Lists`. Next Friday, the lists open again.
```

### P19 A Title Bestowed

**When:** After a `title_bestowed` [this run: RealmDynasties]. Use Say.

```ingame
The crown has raised a line. The line of {line} receives a title from the Old Throne. Favour given is favour noticed, and every title bestowed is a title another line wanted.
```

```discord
**A Title Bestowed**

The crown has bestowed a title on the line of **{line}** (`/dynasty info {line}`).

A title adds to a line's prestige. The crown may give a title held by another line, and so take it from them, and the Chronicle records who lost it.
```

## Act III: The Lawful Hours

### P20 The Realm Rises

**When:** The first time `/claim list` shows two or more open claims. Use Notice.

```ingame
The Lawful Hours are crowded. {n} houses have declared against the crown. Sworn houses must choose their side, and hired swords their price. The Hollow Crown will be tested.
```

```discord
**The Realm Rises**

**{n} houses** have declared against the crown (`/claim list`). Every claim is fought in a Lawful Hour, and every declared rebel is a public enemy while the claim stands.

Houses sworn to the crown fight for it. Others fight for the claimant, or for whoever pays (`/contract list merc`).
```

### P21 The King's Hunt

**When:** When the monarch names quarry at the start of a Wednesday hunt. RealmEvents announces each name. Use Say.

```ingame
The Hunt of the Crown rides out. The crown has named its quarry and every hunter outside the quarry house may claim the prize. Run, hide, or stand. Survivors are remembered too.
```

```discord
**The King's Hunt**

The crown has named its quarry (`/hunt` shows who). Any hunter outside the quarry's house who takes them earns a prize and season points for their house.

Quarry still free at the end of the hunt survive it, and their house earns points too.
```

### P22 Outlaws of the Crown

**When:** After the crown or the court declares an outlaw (`/contract enemies` or `/court outlaws` lists them). Use Say.

```ingame
{outlaw} is declared outlaw by the crown. Bounties on an outlaw are lawful and paid on proof of the deed. Any who shelter them may answer for it at court.
```

```discord
**Outlaws of the Crown**

**{outlaw}** is declared outlaw. Bounties on public enemies of the crown may now be posted (`/contract post bounty <player> <amount> "<item>"`), and are paid only when the death is proven.

Where the crown has proclaimed the *Harbouring Outlaws* law, sheltering an outlaw is a crime that can be tried.
```

### P23 Ravens in the Dark

**When:** Once in Act III, after spymasters are named. You can only see this from players talking about it, since spymasters are private. Use Say.

```ingame
Ravens fly over Ostreval, and not all of them arrive unread. Spymasters watch rival rookeries. Write nothing in a letter you could not bear the realm to read.
```

```discord
**Ravens in the Dark**

Ravens fly over Ostreval, and not all of them arrive unread. A house's spymaster can watch one rival house and sometimes read its ravens. A sworn spy can learn another house's treaties and fealty, but may be caught.

Write nothing in a letter you could not bear the whole realm to read. (`/raven help` for the details.)
```

### P24 The Ransom-Glass

**When:** After the first `ransom_paid` of Act III. Use Say.

```ingame
A ransom is paid and a captive walks free. The ransom-glass of the Charter runs short for every captive, and none may be taken again at once. Hold your prisoners fairly.
```

```discord
**The Ransom-Glass**

A ransom has been paid and a captive walks free. The Charter keeps the ransom-glass short: a capped amount, a capped time, and protection from being taken again straight away.

`/ransom list` shows who is held, and when the law frees them.
```

### P25 The Blood Answers

**When:** After a `blood_claim` [this run: RealmDynasties]. Post P11 first if the fall itself has not been announced yet. Use Notice.

```ingame
The crown fell from the line of {line}, and the blood answers. An heir presses the blood claim through House {claimant}. Win the Lawful Hours and the line is restored.
```

```discord
**The Blood Answers**

The crown fell from the line of **{line}**, and the blood answers: an heir has pressed the **blood claim**, which rides on **House {claimant}**'s claim at the Hearth.

If the house takes the throne in the Lawful Hours, the line is **restored**. That is written in the Chronicle and adds to the line's prestige.
```

### P26 The Night of Bells

**When:** Saturday of week 6, about 2 hours before Crown Night. This is the biggest night of the season. Use Notice, then Say at the start.

```ingame
Tonight is the Night of Bells. They say the drowned bell of Dunmere rings when the throne will change hands. At {time} UTC the Lawful Hours open. Every declared house, to the hill.
```

```discord
**The Night of Bells**

Tonight is the **Night of Bells**. The old tale says the drowned bell of Dunmere rings at low tide whenever the Old Throne is about to change hands. *(This is a story. No code rings any bell.)*

At **{time} UTC** the Lawful Hours open. Every declared house (`/claim list`), to the throne hill. Crown Night points go to every house that takes the throne, and the biggest prize goes to the house that holds it at the end.
```

## Act IV: The Reckoning at the Hearth

### P27 The Ledger Is Read

**When:** Monday of week 7, after reading `/season standings`. Use Say.

```ingame
Two weeks remain. The ledger of the season is read at the Hearth: House {house} leads. Crown days, kept treaties, won rebellions and contracts can still turn it. See /season standings.
```

```discord
**The Ledger Is Read**

Two weeks remain in *The Hollow Crown*. The season ledger is read aloud at the Hearth, and **House {house}** leads.

Points come from crown days, rebellions won or defended, treaties kept, contracts fulfilled and the realm's events. Broken treaties and broken oaths cost points. `/season standings` shows the board, and `/season house <name>` shows any house's line.
```

### P28 The Last Crown Night

**When:** Saturday of week 8, about 2 hours before Crown Night. Use Notice.

```ingame
The last Crown Night of the season falls tonight at {time} UTC. Whoever holds the Old Throne when it ends takes the hold points into the final ledger. There is no next Saturday.
```

```discord
**The Last Crown Night**

The last Crown Night of *The Hollow Crown* falls tonight at **{time} UTC**.

Whoever holds the Old Throne when the night ends takes the hold points into the final ledger. Every claim still standing is fought tonight. There is no next Saturday this season.
```

### P29 The Hearth Truce

**When:** Sunday of week 8, at the start of the Truce of the Realm (12:00 UTC by default). Use Notice.

```ingame
The Hearth Truce is proclaimed. Until {time} UTC no blood is shed between the houses of Ostreval. Come to the Hearth under your banners. A truce breaker is remembered for a season.
```

```discord
**The Hearth Truce**

The last truce of the season is proclaimed. Until **{time} UTC**, no blood is shed between players. Truce breakers lose season points for their house and earn the *Trucebreaker* mark.

Come to the Hearth under your banners for the realm's portrait of the season. Bring your sigils, not your siege.
```

### P30 The Crown Is Weighed

**When:** At `season_ended`, after RealmSeasons' own ceremony lines. Use Notice, then post the Discord version with the final standings.

```ingame
The season is ended and the Hollow Crown is weighed. House {house} has filled it best and is named champion of the season. The pages of the lost winter stay blank. The realm wrote its own.
```

```discord
**The Crown Is Weighed**

*The Hollow Crown* is over. The ledger is closed and **House {house}** is named champion of the season.

Every reign of the season is written in the Hall of Kings (`/season hall`), which outlives the wipe. The pages of the lost winter stay blank. Ostreval wrote its own instead.

Thank you to every house, every hired sword and every raven. Season Two will be announced here.
```
