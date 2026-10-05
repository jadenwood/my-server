# How to play on Realm

A guide for players. It opens with **What's in Realm**, every system in plain words. The numbered sections after it give the exact commands and limits for houses, the crown and the Chronicle, contracts, seasons and events, taken from the plugin source (`plugins/RealmHouses.cs`, `plugins/CrownAndConsequences.cs`, `plugins/RealmChronicle.cs`, `plugins/RealmContracts.cs`, `plugins/RealmSeasons.cs`, `plugins/RealmEvents.cs`) at its **default config**. Server operators can change these limits in `oxide/config/*.json`. If what you see in game differs, the server config wins. The lore is in [`lore.md`](lore.md) and the rules are in [`rules.md`](rules.md).

**Getting in:** use your own Steam copy of Reign of Kings (app 344760). The Realm app's **Play** button opens the game through your own Steam and joins the server; if the game stops at its menu, the app shows the address and port to type into direct connect. Nothing on your PC is modified.

**Quick help in game:** `/realm` lists every command by subject. Type `/house` with no arguments for the house and treaty help line. `/decree`, `/council`, `/claim`, `/ransom`, `/crown` and `/chronicle` with no arguments each show their current state.

Arguments in `<angle brackets>` are required and arguments in `[square brackets]` are optional. To pass a name with spaces as **one** argument, wrap it in double quotes, for example `/house found "Grey Water" Heron`. The Oxide RoK chat parser supports this (`docs/oxide-rok-api.md`).

---

## What's in Realm

Realm is Reign of Kings with a realm around it: houses that swear and betray, one crown worth fighting for, laws and a court, coin, letters, seasons that end and a history that does not. Everything below is done by the server, so you need nothing but your own copy of the game. Type **`/realm`** in game for every command by subject; most commands explain themselves when typed alone.

*Realm is still being tested. Not everything here has been seen working on the live server yet, and the numbers are first guesses that will be tuned. If something behaves differently from this page, tell the staff: the server's settings win.*

### Your first hour

**The arrival.** After you make your character on the ferryman's raft (the game's own character screen), you wake in the **Gatehouse of the Unwritten**, a stone court below the Hearth. Walk to the gold line before the gate and it opens. Six house banners line the road to the Hearth: each house tells you what it is as you pass, and standing still on a house's stone lets its members know you are interested (it is never an oath; only they can invite you). At the fire you are pointed to your starter bundle (`/kit starter`), the crown (`/crown`) and your first deed (`/quest`). It takes three to five minutes. `/arrival` says where you are and what is next, `/arrival skip` ends it at once, and `/arrival tour` tells you the banners and the fire again as you pass them. Returning players never see it. (RealmArrival, `plugins/docs/RealmArrival.md`. Not yet tried on a live server.)

**Survival basics.**

- **Tools first.** Your starter bundle has wood, stone and food. Make a stone tool before anything else; gathering by hand is slow.
- **Carry a torch.** Ostreval's nights are dark. A torch, or staying by a fire, keeps you from walking into trouble.
- **Eat and drink.** Hunger and thirst wear you down over time. Cook what you hunt; raw meat is a poor meal.
- **Raise a crest before you build.** Blocks outside a crest's land decay. A crest marks your land and keeps what you build there.
- **Place a bed.** Without one you wake at a random spot after a death. A newcomer who falls while still sheltered gets one return to the Hearth (Hearth's Mercy), once.
- **Log off behind walls.** Your body sleeps where you stood when you leave. Anyone can find it, so leave it behind a locked door.

**Realm's first steps.**

- **The Herald** welcomes you (after the arrival, when it runs), shows the message of the day and three first steps: swear to a house, see who holds the crown, and take a contract (`/realm path` shows the way). `/realm` opens the hub; `/realm popups off` keeps everything in chat if you prefer.
- **New-player protection** (the Warden) keeps other players from hurting you for your first hour of play (and at most 48 hours). Strike, bind or raid a crest, and the protection ends at once. You can give it up early with `/warden protection off confirm`.
- **Kits.** Newcomers can take a starter pack once with `/kit starter`; members of a house draw daily provisions, and everyone gets a season's bounty once per season. `/kit` shows what you may take.
- **Your journal.** `/quest` gives you three small tasks a day and two a week, plus the season's story. Finishing them pays marks (Realm's coin), renown and sometimes goods.

### Houses, oaths and treaties

A **house** is your faction: a name, a sigil, a leader, officers and members. Found one with `/house found`, or ask to be invited. Houses can **swear an oath** to a stronger house and become its vassals, and can sign **treaties** that last a set number of days. Nothing stops a vassal or an ally from turning on you, but the realm remembers: breaking an oath or a treaty marks you and your house as oathbreakers or treaty-breakers for everyone to see. Details in sections 1 to 3 below.

**House colours.** If your house is bound to a game guild (`/house link`), the server keeps the guild's name and banner in your house's colours, so they show on your banner, your crest's flags, your armour and the little icon by your name. The six great houses have their own colours; other houses get an open pair, and the head of the house can choose another with `/heraldry colours` (the first choice is free, later changes cost marks and wait three days). `/heraldry` shows yours.

### The crown

Whoever sits on the **Old Throne** is the monarch. The monarch issues **decrees** (Royal Stores, the King's Peace, Open Roads, Tax Relief), appoints a **council** and can name outlaws. The throne cannot be taken by surprise: once someone holds it, only a house with a declared **claim** may take it, and only during a **rebellion window**. Captives can be held for a capped **ransom** for a limited time. Details in sections 4 to 8.

**Elections and the realm's voice.** Each new season the realm **elects** two council seats, the Keeper of Coin and the Marshal, from heads of houses who stand with `/ballot stand` (with a deposit). You vote with `/vote <name or house>`; the count stays hidden until the close, and the winners serve 28 days that the monarch cannot cut short. The monarch can also **ask the realm** about a decree or a law (`/vote yes` or `/vote no`): a decree the realm approves costs the crown nothing next time, and one it refuses is blocked for three days. One vote per Steam account, and only for players who have played a while and been in their house a few days; `/ballot me` tells you if you can vote and why not.

### Law and the court

The crown proclaims **laws** (for example the King's Peace in the market or a bridge toll) and draws **zones** where they hold. Break a law and it goes on a public crime ledger; anyone can **accuse** you, and a **jury** of house heads tries the case. A verdict can mean a fine, outlawry or exile, and a case can be settled by **trial by combat** in the ring. The monarch can pardon. `/law`, `/court` and `/laws` explain it all.

### Bloodlines, renown and titles

Found a **dynasty**, name your heirs and pass your line's claim down when a monarch falls (`/dynasty`). Great deeds and dark ones earn **renown** and **infamy**: taking the throne, keeping a treaty, bringing down an outlaw, betraying an oath. Deeds earn **titles** such as Kingslayer, Kingmaker, Champion of the Ring or Guildmaster, which you can wear in chat with `/titles set`. `/renown top` shows the realm's most famous and most infamous.

### Coin: marks, the market and the treasury

Realm's coin is the **mark**, kept in your purse (`/purse`). You earn it from tasks, events, duels, crafting commissions, caravans and treasure, and you can pay other players. The **market** (`/market`) lets you sell and buy goods for marks with the goods held safely until the trade completes. Each house has a **vault** its heads and stewards manage (`/vault`). The **crown's treasury** collects tolls, fees and tribute; the monarch and the Keeper of Coin decide grants. Every mark is accounted for in a public audit, so nobody can print money.

### Contracts and bounties

Post a **bounty** on a public enemy of the crown, a **delivery** order for goods you need, or (in a rebellion) hire a **mercenary**. The reward is held in escrow and paid only when the work is done, or refunded. `/contract`.

### Seasons and the Hall of Kings

The realm is played in **seasons**. Every house earns points for holding the crown, winning or defending rebellions, keeping treaties, contracts, events, holdings, festivals and house goals. At the end the champion house is proclaimed, and every reign goes into the **Hall of Kings**, which survives server wipes (`/season`, `/season hall`). Season 1 is called *The Hollow Crown*.

### Realm events

Scheduled nights with countdowns: **Crown Night** (the throne is fought for), the **Royal Tournament** (a ranked fight with prizes, sometimes as a bracket in the ring), **the King's Hunt** (the monarch names quarry) and the **Truce of the Realm** (no blows land). `/events` shows what is next.

### Holdings and the War Hours

Seven named places of Ostreval (a crossroads, a keep, a mine, a harbour and three villages) can be **held** by a house. During the **War Hours** (by default Wednesday, Saturday and Sunday evenings, two hours each, in realm time) a house takes a holding by **standing in it with its own members** until its banner is raised; a rival's banner must come down first, and if two houses stand there at once nothing moves. A house that holds a place earns marks into its vault each day, season points, and a **garrison** that grows each day it keeps it and makes it harder to take. A house may hold at most three, and the War Hours pause during a truce or a rebellion. `/dominion` shows the map, `/dominion here` tells you whether you count where you stand, and `/dominion rules` explains the rest.

### Tasks, the season's story and achievements

`/quest` is your journal: **three daily tasks** (new each day at 04:00 UTC; one reroll a day) and **two weekly tasks** (new each Monday), such as crafting, hunting, building, visiting a place or delivering goods. Hand goods in with `/quest give`. The **season's story**, *The Hollow Crown*, opens act by act through the season (`/quest story`). Your **house** has a shared weekly goal (`/quest house`) that pays season points and every member who helped. **Achievements** (`/achievements`) record 68 deeds in five kinds (survival, war, politics, economy, exploration), most with bronze to gold tiers. Marks from tasks are paid once you have played about half an hour.

### Duels, the ladder and the tavern

Challenge anyone to a **duel to the first fall** with `/duel <player>`, with or without a stake in marks. A ring forms where you meet (or in the Proving Ring); nobody else can strike, rope or build inside it. The killing blow is **turned aside**: the loser is felled, not killed, so **nobody dies and nothing is dropped**, and both fighters are tended afterwards. Stakes are held by the treasury and paid to the winner. Ranked duels move your place on the **ladder** (`/arena top`), and each Sunday evening the best fighter of the week is crowned **Champion of the Ring**. There are 2v2 and 3v3 duels, bracket tournaments (`/arena tourney`) and trials by combat. Leaving the ring or logging off mid-fight forfeits.

In the **tavern**, play **Hearth Dice** (`/dice`) or **Twenty-One** (`/cards`) against another player for a few marks. The house takes nothing, the dice are fair, and strict daily limits keep it a pastime; `/dice roll` throws for show.

### Roads, waystones and home

**Waystones** stand at the capital, the house seats, the holdings and landmarks. Walk up to one to learn it. Then `/travel <name>` carries you to any waystone you know: stand still for ten seconds and you are there. A journey costs a small **toll** to the crown's treasury (your first three are free, the Open Roads decree makes them free for a while, and houses sworn to the crown pay half). You cannot travel in a fight, with a captive, as an outlaw, while bearing the Ironbreaker, or towards the throne during a rebellion, and a house's seat is open only to that house and its allies. For a few seconds after you arrive, nobody can strike or rope you, so nobody can camp a waystone. `/home set` marks a home inside your own crest's land and `/home` takes you back. `/road <name>` gives you directions in chat as you walk.

### Crafts and the guilds

Everything you gather and make builds one of **eight professions**: woodcutting, mining, foraging, hunting, smithing, carpentry, tailoring and cooking (`/craft`). Rising through the ranks brings **perks**: a share of extra goods when you gather, a chance of an extra item when you craft, and a lower market fee on your own goods. Reaching mastery earns the **Guildmaster** title, and each week the realm's best crafter is crowned **Master Crafter**. A house's members pool their work into a **workshop** that gives them all a bonus. On the **commission board** (`/craft orders`) you can order goods with the marks held by the treasury, or fill others' orders for pay.

### The living world

Between the realm's events, the world stirs on its own (`/world` shows what is abroad and what comes next):

- **Treasure hunts.** The Herald gives a riddle; find the place it describes and stand there for the next one (`/treasure`). The first to reach the last place wins marks and the hoard.
- **The Blood Moon.** Every other Thursday night: fair kills earn renown and points for your house, and the beasts bite harder.
- **The Merchant Caravan.** One player carries the goods on foot from one place to another while others escort them (`/caravan carry`, `/caravan escort`); raiders who bring the bearer down take a share, with a price on their head.
- **Wandering Legends.** A named, tougher beast roams a region; its slayer and those who helped share the reward.
- **The Harvest Fair** (late September) and **Midwinter** (late December): houses compete in offerings (`/festival give`), and the great houses' monuments rise for the festival.
- **The Census.** Every Monday the Herald reads out the realm's numbers: how many walked the realm, the houses, the crown, the hoards and caravans. Counts only, never names.

### The Ironbreaker

There is exactly **one legendary blade** in the realm. It is won at the Royal Tournament, its bearer is known to all, and whoever slays the bearer takes it.

### Monuments, signs and moods

As the realm grows, you will see Realm's **monuments** built from the game's own blocks (the Herald's Pillar, the Old Throne, the Tournament Arch, the six houses' monuments), **painted signs** that show house crests, wanted posters, the Chronicle, the season standings and the holdings, and the realm's **moods**: the server changes the sky and light for seasons and events, such as a red Blood Moon or a snowy Midwinter.

### Fair play

The **Warden** protects newcomers, keeps raid hours, flags combat-logging and takes your reports (`/warden report <player> <reason>`). The **Sentinel** watches, on the server only, for movement, combat and item patterns that honest play does not produce, and passes the evidence to staff; it never touches your game or your PC. Staff who run a system do not compete in it. The server's statistics are pseudonymous, and you can opt out with `/stats optout`.

### Letters and rumours

Send a **raven** to a player or a whole house (`/raven`), sign it or send it anonymously. Spies may intercept letters. Share an anonymous **rumour** with `/rumour`; staff approve it before the realm hears it.

### Following the realm outside the game

Every public act goes into the **Chronicle**: `/chronicle` in game, and on the realm's web pages, stream overlays and Discord. The **Realm app** shows whether the server is up, who holds the throne and the realm's news, and its Play button joins through your own Steam.

---

The sections below give the exact commands and limits for houses, the crown, claims, ransom, the Chronicle, contracts, seasons and events. Every other system has a guide for staff in `plugins/docs/`.

## 1. Houses

A house is your faction. It has a name, a sigil, a leader, up to 3 officers and its members.

| Command | Who | What it does |
|---|---|---|
| `/house found "<name>" <sigil>` | anyone not in a house | Founds a house and makes you its leader. The name is a single argument (use quotes if it has spaces). It must be 3–24 characters and start with a letter. The sigil is everything after the name, up to 32 characters. Only letters, digits, spaces, `'` and `-` are allowed. You can found one house per 60 minutes. |
| `/house invite <player>` | leader or officer | Invites an online player. The invite lasts 5 minutes. |
| `/house join <house>` | invited player | Joins the house that invited you. |
| `/house leave` | member | Leaves your house. If the leader leaves, an officer (or the longest-standing member) takes over. If the last member leaves, the house is disbanded. |
| `/house kick <player>` | leader or officer | Casts out a member of lower rank. |
| `/house promote <player>` | leader | Makes a member an officer (3 at most). |
| `/house promote <player> leader` | leader | Hands over leadership. You become an officer. |
| `/house demote <player>` | leader | Officer → member. |
| `/house info [house]` | anyone | Shows sigil, leader, officers, liege, vassals, treaties and **marks** (oathbreaker / treaty-breaker counts for the house and for its leader personally). |
| `/house list` | anyone | Lists every house, its sigil, its member count and who it is sworn to. |
| `/house link` / `/house unlink` | leader | Binds your house to your in-game guild, or unbinds it. |
| `/house disband` | leader | Disbands your house. |

**Guilds.** If you are in a game guild when you found a house, the house is bound to that guild automatically. In a guild with more than one member, only the guild owner can do this. For a bound house, membership follows the in-game guild menu, so `/house invite`, `join`, `leave` and `kick` are turned off. A house that is not bound to a guild holds at most 20 members.

## 2. Oaths (fealty)

A house can swear to a **liege** house and become its **vassal**. Oaths cannot form a loop: you cannot swear to a house that already owes fealty to you, directly or through its lieges.

| Command | Who | What it does |
|---|---|---|
| `/swear <house>` | your house's leader | Offers your oath. The liege's leader must be **online** and has 10 minutes to answer. |
| `/swear accept <house>` | the liege's leader | Accepts the oath. It is announced and recorded in the Chronicle as `oath_sworn`. |
| `/swear deny <house>` | the liege's leader | Refuses the oath. |
| `/renounce`, then `/renounce confirm` within 60 seconds | vassal's leader | Breaks your oath. Your house **and you personally** get an oathbreaker mark, and your house cannot swear again for **24 hours**. It is announced and recorded as `oath_broken`. |

Being sworn to the house that holds the crown matters: see *Royal Stores* under Decrees.

## 3. Treaties

A treaty is a timed truce between two houses. Each house can hold at most 5.

| Command | Who | What it does |
|---|---|---|
| `/treaty propose <house> [days]` | leader | Proposes a treaty. It lasts 7 days by default; the allowed range is 1–30. The other leader must be online and has 10 minutes to accept. |
| `/treaty accept <house>` | leader | Signs the treaty. Recorded as `treaty_signed`. |
| `/treaty break <house>` | leader | Breaks the treaty early. Your house and you get a treaty-breaker mark, and the same two houses cannot sign again for 24 hours. Recorded as `treaty_broken`. |
| `/treaty list` | any member | Shows your house's treaties and the time left on each. |

Treaties end on their own when their time runs out. **A treaty is a promise, not a force field.** No code stops a treaty partner from attacking you. What a treaty does is make betrayal *public*.

## 4. The crown

The monarch is whoever holds the Old Throne (the game's ancient throne). `/crown` shows:
- the monarch, their house and when their reign began (UTC)
- crown **authority** (for example `Authority 30/100`)
- the decrees in force and how many minutes each has left
- the next rebellion window

**Authority.** A new reign starts with at least 30 authority. The plugin hands out this starting grant at most once every 6 hours, so passing the throne back and forth does not refill it. Authority regenerates at 0.5 per minute up to 100. Decrees spend it.

**Tax.** The monarch can set the realm's tax through the game UI. The plugin caps it at 50% of the game's maximum and pulls back any higher setting. *UNVERIFIED: the runtime value of the game's tax maximum, so the exact cap number is only known once it has been seen in game.*

## 5. Decrees

`/decree` lists every decree with its id, cost, cooldown and whether it is in force. `/decree <id>` issues one. Every decree lasts 60 minutes. After any decree, the crown must wait **15 minutes** before issuing another.

| Id | Name | Cost | Cooldown | Effect |
|---|---|---|---|---|
| `stores` | Royal Stores | 40 | 240 min | Every 15 minutes while it is in force, each **online** member of the crown's house, or of a house sworn **directly** to the crown's house, receives 25 Wood. *UNVERIFIED in game: the item grant and how fast your inventory updates. Check `/decree` on the live server.* |
| `peace` | King's Peace | 20 | 180 min | A proclamation only. Nothing stops fighting. |
| `roads` | Open Roads | 10 | 120 min | A proclamation only. |
| `relief` | Tax Relief | 30 | 360 min | Sets the crown's tax to 0 for 60 minutes, then restores it (unless the monarch changes the tax by hand in the meantime). |

Older configs had a `tithe` (Harvest Tithe) decree. The plugin now replaces it with Royal Stores when it loads.

**Who can decree:** the monarch can issue any decree. A council member in the **Voice of the Crown** seat can issue *proclamation* decrees (`peace`, `roads`) in the monarch's name. Each decree is broadcast by the Herald and recorded as `decree`.

## 6. The council

| Command | Who | What it does |
|---|---|---|
| `/council` | anyone | Lists the seats and who holds them. |
| `/council appoint <player> <seat>` | monarch | Appoints an online player. Seats: `Voice of the Crown`, `Keeper of Coin`, `Marshal`. The first letters of a seat name are enough, for example `/council appoint Bram voice`. |
| `/council remove <player\|seat>` | monarch | Dismisses a council member, by player name or by seat. |

Each player can hold one seat. The council can be changed once a minute, and appointed seats are **emptied when the crown changes hands**. A seat the realm **elected** (the Keeper of Coin and the Marshal, with `/ballot` and `/vote`, see *Elections and the realm's voice* above) is held for its 28-day term: the monarch cannot dismiss that councillor, and the seat survives a change of monarch. Only the Voice of the Crown has a mechanical power, issuing proclamations. The Keeper of Coin and the Marshal are titles, and what they mean is up to the court.

## 7. Claims and rebellion windows

You cannot take the throne by surprise. When someone holds it, the throne can only be captured during a **rebellion window**, and only by a house with an **active claim**. An **empty** throne can be taken by anyone at any time.

| Command | Who | What it does |
|---|---|---|
| `/claim` or `/claim list` | anyone | Shows open claims, their status (`pending` / `active`) and their window. |
| `/claim declare` | the house **leader** | Declares your house's claim. It is scheduled into the first rebellion window that starts **at least 60 minutes from now**. Recorded as `claim_declared` and broadcast. |

Default windows (all **UTC**): **Wednesday 19:00–20:00** and **Saturday 19:00–20:30**. The server operator can change them. `/crown` always shows the next one.

The rules:
- The house that holds the crown cannot declare against itself.
- Each house can have one open claim at a time, and must wait **72 hours** between claims.
- When the window opens, the claim becomes `active` (recorded as `rebellion_started`). Until the window closes, only houses with an active claim can capture the throne.
- When the window closes, the outcome is recorded as `rebellion_ended`: the claimant prevailed, the crown held, the throne ended empty, or a third house took the crown.
- *UNVERIFIED: that blocking a capture fully stops it in game. Smoke test C confirms this.*

## 8. Captives and ransom

The game lets you capture players. Realm limits how long a captive can be held and how much can be asked for them.

| Command | Who | What it does |
|---|---|---|
| `/ransom` or `/ransom list` | anyone | Lists who is held, by whom, for how much, and the minutes left. |
| `/ransom set <player> <amount>` | captor | Names a ransom from 1 to **500** gold. Once a ransom is set, it can only be **lowered**. It can be changed at most 3 times. Recorded as `ransom_set`. |
| `/ransom paid <player>` | captor | Records the payment. The captive is then free within 2 minutes at most. Recorded as `ransom_paid`. |
| `/ransom release <player>` | captor | Frees the captive now. |
| `/ransom free` | captive | After your term is over, frees you if your captor has not. *UNVERIFIED: if the normal release fails, this may kill your character to free you, and what you lose by dying is not confirmed. Admins are alerted if this fails too.* |

The limits:
- **10 minutes** is the longest hold, counted from the first capture. Re-binding someone does not reset the clock.
- After release, the former captive cannot be captured again for **15 minutes**.
- Payment is handed over **in person**. The plugin does not move gold; the captor's `/ransom paid` is their word. Whatever happens, the law frees the captive when the term ends, and the release is recorded as `released`.

## 9. The Chronicle

Every public act is written to the Chronicle. You can read it:
- In game: `/chronicle [count]` shows the latest entries (default 5, max 15).
- On the public page `/realm` and the stream overlay, served by the Chronicle service. These are currently local to the server PC; see [`streamer-kit.md`](streamer-kit.md).

Event types include the crown and war (`coronation`, `abdication`, `claim_declared`, `rebellion_started`, `rebellion_ended`), houses (`house_founded`, `oath_sworn`, `oath_broken`, `treaty_signed`, `treaty_broken`), the crown's acts (`decree`, `ransom_set`, `ransom_paid`, `released`), contracts, seasons and events (`season_started`, `season_ended`, `event_started`, `event_ended`, `tournament_champion`, `hunt_kill`, `truce_broken`), the court and dynasties, titles, the treasury, rumours, and newer ones: a holding taken (`holding_taken`), the weekly census (`census_taken`) and the realm's votes (`vote_held`).

The Chronicle shows **only public player names**. It never shows locations or inventories. Reading it is never stream sniping.

## 10. Contracts (bounties, deliveries, mercenaries)

Contracts let small groups and lone blades earn a place in the realm. Rewards are held in escrow: the items leave your packs when you post and are paid out only when the contract is fulfilled, or refunded when it expires or is cancelled.

| Command | What it does |
|---|---|
| `/contract list [bounty\|delivery\|merc]` | Open contracts (up to 15 lines) |
| `/contract info <id>` | Details of one contract |
| `/contract post bounty <player> <amount> "<item>" [hours]` | Bounty on a **public enemy of the crown** only (see `/contract enemies`) |
| `/contract post delivery <qty> "<item wanted>" <amount> "<reward item>" [hours]` | A supply order: whoever delivers the goods gets the reward |
| `/contract post merc <amount> "<item>"` | House leaders only, during their house's pending or open rebellion window: hire a sword |
| `/contract accept <id>` / `deliver <id>` | Take a contract / hand in a delivery |
| `/contract confirm <id>` / `cancel <id>` | Poster confirms an honour-mode delivery, or cancels (refund) |
| `/contract enemies` | Who currently counts as a public enemy |
| `/contract items <search>` | Look up exact item names |
| `/contract collect` | Collect rewards that arrived while your packs were full or you were offline |

**The monarch** can name outlaws with `/contract outlaw <player>` and lift it with `/contract pardon <player>` (limited and on cooldown; a pardon refunds open bounties).

Rules worth knowing: you can't collect a bounty you posted or one on your own housemate; a mercenary's kills of their own housemates don't count, and each victim counts once per contract; bounties pay only while the target is still a public enemy, and none pay while the throne is empty.

## 11. Seasons and the Hall of Kings

The realm is played in numbered **seasons** (default 28 days). During a season every house earns points:

| What | Points (default) |
|---|---|
| Each day your house holds the crown (counted by the minute) | +10 per day |
| A rebellion your house **won** (your claim took the crown) | +25 |
| A rebellion your house **defended** (you held the crown) | +15 |
| A treaty kept to the end of its term (both houses) | +5 |
| A treaty you broke / an oath you renounced | −10 each |
| A contract fulfilled by one of your members | +2 |
| Realm events (below) | as listed there |

| Command | What it does |
|---|---|
| `/season` | The season, its day and end date, the leading house and your house's place |
| `/season standings` | The top houses with their crown days, rebellions, treaties, contracts and event points |
| `/season house [name]` | One house's standing and honours (your own by default) |
| `/season hall [page]` | **The Hall of Kings**: every reign, newest first, with its house, length and how it ended |
| `/season history` | Past seasons, their champion houses and longest reigns |

When a season ends, the herald proclaims the standings and the **champion house**; it goes into the Chronicle (`season_ended`) and into the realm's legends. **The Hall of Kings and past seasons survive server wipes.** Only the running standings start again.

## 12. Realm events

Scheduled events with countdown heralds (60, 30, 10, 5 and 1 minute before). Default times, all **UTC**; the server operator can change or disable each one. `/events` always shows what is on now and what comes next.

| Event | Default | What happens |
|---|---|---|
| **Crown Night** | Saturday 19:00, 90 min (the Saturday rebellion window) | The night the throne is fought for. The countdown names the houses with a claim. A house whose member takes the throne during the night earns **+10** (once); the house holding the crown when the night ends earns **+30**. The normal claim rules (section 7) still decide who may capture. |
| **Royal Tournament** | Friday 19:00, 60 min | A PvP ranking. `/tourney join` (you can join during the countdown). Each kill of another entrant scores 1; the same victim counts at most **twice** for you; housemates never count. Places 1–3 earn your house **+30 / +20 / +10** and prizes (default 300 / 200 / 100 Stone). |
| **The King's Hunt** | Wednesday 21:00, 60 min | The monarch names up to **3** quarry with `/hunt name <player>` within the first 10 minutes (or the hunt is called off). Whoever slays a quarry (not one of the quarry's housemates) gets the prize (default 200 Wood) and **+15** for their house. Quarry still free at the end earn **+10** for their house. Each quarry can be taken once. |
| **Truce of the Realm** | Sunday 12:00, 240 min | No player may harm another: blows simply do not land. Killing during the truce is a breach: it is chronicled (`truce_broken`) and costs your house **−15**. The truce **yields to an open rebellion**. *UNVERIFIED: that every kind of damage (arrows, fire, siege) is stopped; if not, the server runs the truce as announce-only and breaches are still punished.* |

| Command | What it does |
|---|---|
| `/events` | What is running now, what comes next, and the last result |
| `/tourney join` / `leave` / `standings` | Enter, withdraw from, or see the Royal Tournament |
| `/hunt` | The quarry and who has taken them; `/hunt name <player>` (monarch only) |
| `/truce` | Whether a truce holds and until when |
| `/event collect` | Prizes that did not fit in your packs, or arrived while you were offline |

Prizes are real items. If your packs are full, the rest waits for you: rejoin or use `/event collect`. Nothing is lost and nothing is paid twice.

## Admin-only commands (for reference)

These need the `realmhouses.admin` / `crownandconsequences.admin` permission. Their use is governed by `rules.md`.

`/house disband <house>`, `/house pardon <house>` (clears marks and the oath cooldown), `/house unlink` (on any house the admin belongs to), `/house sync`, `/claim cancel <house>` (recorded as "set aside by the realm's stewards"), `/council appoint|remove` without being monarch. Admin permission also **bypasses the throne-capture gate**.

Seasons and events (permissions `realmseasons.admin` / `realmevents.admin`): `/season start [days] [name]`, `/season end` (holds the ceremony now), `/season status`; `/event start <crown_night|tournament|kings_hunt|truce> [minutes]`, `/event stop <kind>` (ends it now, with results and prizes), `/event cancel <kind>` (ends it with no results). An admin may also name hunt quarry.
