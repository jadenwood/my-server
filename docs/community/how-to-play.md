# How to play on Realm

A one-page guide for players. Every command and limit below comes from the plugin source (`plugins/RealmHouses.cs`, `plugins/CrownAndConsequences.cs`, `plugins/RealmChronicle.cs`) at its **default config**. Server operators can change these limits in `oxide/config/*.json`. If what you see in game differs, the server config wins. The lore is in [`lore.md`](lore.md) and the rules are in [`rules.md`](rules.md).

**Getting in:** use your own Steam copy of Reign of Kings (app 344760). The Realm launcher only opens Steam and shows the address to type into direct connect. Nothing on your PC is modified.

**Quick help in game:** type `/house` with no arguments for the house and treaty help line. `/decree`, `/council`, `/claim`, `/ransom`, `/crown` and `/chronicle` with no arguments each show their current state.

Arguments in `<angle brackets>` are required and arguments in `[square brackets]` are optional. To pass a name with spaces as **one** argument, wrap it in double quotes, for example `/house found "Grey Water" Heron`. The Oxide RoK chat parser supports this (`docs/oxide-rok-api.md`).

---

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
| `stores` | Royal Stores | 40 | 240 min | Every 15 minutes while it is in force, each **online** member of the crown's house, or of a house sworn **directly** to the crown's house, receives 25 Wood. *UNVERIFIED in game: the item grant and how fast your inventory updates. Being added to the plugin right now. Check `/decree` on the live server.* |
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

Each player can hold one seat. The council can be changed once a minute, and it is **emptied when the crown changes hands**. Only the Voice of the Crown has a mechanical power, issuing proclamations. The Keeper of Coin and the Marshal are titles, and what they mean is up to the court.

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

Event types: `coronation`, `abdication`, `claim_declared`, `rebellion_started`, `rebellion_ended`, `house_founded`, `oath_sworn`, `oath_broken`, `treaty_signed`, `treaty_broken`, `decree`, `ransom_set`, `ransom_paid`, `released`.

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

## Admin-only commands (for reference)

These need the `realmhouses.admin` / `crownandconsequences.admin` permission. Their use is governed by `rules.md`.

`/house disband <house>`, `/house pardon <house>` (clears marks and the oath cooldown), `/house unlink` (on any house the admin belongs to), `/house sync`, `/claim cancel <house>` (recorded as "set aside by the realm's stewards"), `/council appoint|remove` without being monarch. Admin permission also **bypasses the throne-capture gate**.
