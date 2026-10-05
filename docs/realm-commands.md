# Realm chat commands

Every chat command the Realm plugins register, in one place. Type them in the game's chat. Most commands show their own help when typed with no arguments (or with `help`). In game, `/realm` lists them all by subject.

This list is checked against the plugin source: `node tools/realm-integration/check.mjs --commands` prints the same commands from `plugins/*.cs` (60 from 27 plugins on 2026-10-04: 56 for players, 4 staff-only) and fails if two plugins register the same one. **No command is registered twice.** All of this is compile-checked and mock-tested only. None of it has been run on a live server yet (UNVERIFIED in game).

Full rules and examples: [`community/how-to-play.md`](community/how-to-play.md) (its "What's in Realm" section explains every system to players in plain words; the detailed sections cover houses, crown, claims, ransom, contracts, seasons and events), and the plugin guides in [`../plugins/docs/`](../plugins/docs/) for the rest. How the systems fit together: [`realm-systems.md`](realm-systems.md).

## For every player

| Command | Plugin | What it is for |
|---|---|---|
| `/house` | RealmHouses | Found, join, leave and run a house: `found "<name>" <sigil>` (or `found` alone to be asked in windows), `invite`, `join`, `leave`, `kick`, `promote`, `demote`, `info`, `list`, `link`/`unlink`, `disband` |
| `/swear` | RealmHouses | Offer your house's oath to a liege (`/swear <house>`; with popups on it asks Yes/No first, and `/swear confirm` is the chat answer), or accept or deny one (`accept`/`deny <house>`) |
| `/renounce` | RealmHouses | Break your house's oath (`/renounce`, then Yes in the window or `/renounce confirm`). Earns an oathbreaker mark |
| `/treaty` | RealmHouses | `propose <house> [days]`, `accept <house>`, `break <house>`, `list` |
| `/dominion` | RealmDominion | The holdings of the realm, their houses, garrisons and income, and the War Hours; `/dominion <holding>`, `here`, `rules`. Guide: [`RealmDominion.md`](../plugins/docs/RealmDominion.md) |
| `/heraldry` | RealmHeraldry | Your house's colours on its game guild's banner, crest, armour and name tags; `house <house>`, `colours` (the pairs), and the head of a house chooses with `colours <pair>`. Guide: [`RealmHeraldry.md`](../plugins/docs/RealmHeraldry.md) |
| `/crown` | CrownAndConsequences | Who reigns, since when, and the next rebellion window |
| `/decree` | CrownAndConsequences | The decrees and their cooldowns; the monarch issues one with `/decree <id>` |
| `/council` | CrownAndConsequences | The council seats; the monarch uses `appoint <player> <seat>` and `remove` (not on a seat the realm elected with `/ballot`, during its term) |
| `/claim` | CrownAndConsequences | Open claims (`list`); a house leader uses `declare` |
| `/ransom` | CrownAndConsequences | Captives (`list`); a captor uses `set`, `paid` and `release`; a captive uses `free` |
| `/ballot` | RealmHeraldry | What the realm votes on: `<n>`, `results`, `history`, `me` (can I vote?); heads of houses `stand <seat>` and `withdraw` in council elections; the monarch `propose decree <id>` or `propose law <id>` |
| `/vote` | RealmHeraldry | `<candidate or house>` in a council election, `yes` or `no` on the crown's question (`<n> yes` when there are several). One vote per account |
| `/chronicle` | RealmChronicle | The latest Chronicle entries (`/chronicle [count]`) |
| `/contract` | RealmContracts | Bounties, deliveries and mercenary work: `list`, `info`, `post`, `accept`, `deliver`, `confirm`, `cancel`, `enemies`, `items`, `collect`. The monarch also has `outlaw` and `pardon` |
| `/season` | RealmSeasons | The season, `standings`, `house [name]`, `hall` (the Hall of Kings) and `history` |
| `/events` | RealmEvents | What is running now and what comes next |
| `/event` | RealmEvents | `collect` prizes that did not fit in your packs |
| `/tourney` | RealmEvents | `join`, `leave` and `standings` for the Royal Tournament |
| `/duel` | RealmArena | Duels to the first fall (no death, no loot): `<player> [marks]`, `accept [player] [marks]`, `decline`, `cancel`, `yield`, `status`, `off`/`on`, team duels `2v2` and `3v3`. Stakes are held by the treasury. Guide: [`RealmArena.md`](../plugins/docs/RealmArena.md) |
| `/arena` | RealmArena | Your rating and place, `top [team]`, `me`, `<player>`, `history`, `champion` (the weekly Champion of the Ring), `rules`, `zones`, and `tourney` (`join`, `leave`) for the Lists of the Ring |
| `/hunt` | RealmEvents | The King's Hunt quarry; the monarch uses `name <player>` |
| `/truce` | RealmEvents | Whether the Truce of the Realm holds |
| `/laws` | RealmLaws | Help for `/law` and `/court` |
| `/law` | RealmLaws | The laws in force (`list`), `catalogue`, `info <id>` and the public crime ledger (`crimes [player]`); the monarch uses `proclaim` and `repeal` |
| `/court` | RealmLaws | Cases and trials: `cases`, `case`, `accuse`, `trial`, `verdict`, `combat`, `champion`, `pay`, `collect`, `outlaws`; the monarch uses `pardon` |
| `/dynasty` | RealmDynasties | Bloodlines and heirs: `found`, `info`, `list`, `tree`, `heir name/remove/order/list`, `accept`, `decline`, `leave`, `disown`, `abdicate`, `dissolve`, `claim`; the monarch uses `bestow` |
| `/renown` | RealmRenown | Your renown and infamy, or another player's (`/renown <player>`), and the `top` lists |
| `/titles` | RealmRenown | Your titles, `all`, `<title>`, `set <title>` to wear one in chat, `clear` |
| `/purse` | RealmTreasury | Your marks, and `pay <player> <n>` |
| `/market` | RealmTreasury | The realm market: `list`, `history`, `items`, `sell`, `bid`, `buy`, `fill`, `cancel`, `collect` |
| `/vault` | RealmTreasury | A house vault: view, `deposit`, `give`; heads and stewards also `withdraw`, `take`, trade and name stewards |
| `/treasury` | RealmTreasury | The crown's treasury, `tax`, `ledger` and `deposit` (tribute); the monarch also `mint` and `levy`, and the monarch or the Keeper of Coin `grant` |
| `/craft` | RealmCrafts | Your professions and ranks (`/craft`, `/craft <profession>`), `perks`, `top [houses\|<profession>]` (the weekly Master Crafter), `house` (your workshop), and commissions: `orders`, `order <item> <qty> <marks each>`, `fill <id>`, `cancel <id>`, `collect`. Guide: [`RealmCrafts.md`](../plugins/docs/RealmCrafts.md) |
| `/economy` | RealmTreasury | All the economy help, the market fee, the tithe and the game tax |
| `/dice` | RealmArena | Hearth Dice for marks against another player (`<player> <marks>`, `accept`, `decline`, `cancel`), no house edge, strict daily limits; `roll [NdM]` throws for show |
| `/cards` | RealmArena | Twenty-One for marks against another player (`<player> <marks>`, `accept`, `hit`, `stand`, `hand`, `decline`, `cancel`), both play at once, no house edge |
| `/raven` | RealmRavens | Letters between players and houses: `<house or player> <message>`, `anon`, `inbox`, `read`, `delete`, `sent`, `status`, `block`, `unblock`, `blocks`, `report`; spymasters and spies have `spymaster`, `watch` and `spy ...` |
| `/rumour` (or `/rumor`) | RealmRavens | Submit an anonymous rumour for moderation, or see the approved ones (`list`) |
| `/warden` | RealmWarden | `status`, `rules`, `report <player> <reason>`, and `protection off confirm` to give up new-player protection |
| `/stats` | RealmStats | What the server's statistics record about you (`privacy`, `me`) and `optout` / `optin` |
| `/quest` | RealmQuests | Your journal: daily and weekly tasks, the season's tale (`story`), your house's goal (`house`), `log`, `give` / `give all` (hand in goods), `collect`, `abandon <slot>`, `reroll <slot>`. Guide: [`RealmQuests.md`](../plugins/docs/RealmQuests.md) |
| `/achievements` | RealmQuests | Your deeds by kind and tier, `<kind>`, `<name>`, `top` |
| `/travel` | RealmTravel | The waystones you know; `/travel <name>` sets out (stand still, toll in marks to the crown's treasury), `all`, `info <name>`, `cancel`. Guide: [`RealmTravel.md`](../plugins/docs/RealmTravel.md) |
| `/home` | RealmTravel | `set` a home inside your own crest zone, then `/home` to travel back; `info`, `clear` |
| `/road` | RealmTravel | `/road <waystone>` or `/road home` calls the way (distance and direction) as you walk; `stop` |
| `/world` | RealmWorld | The living world: what is abroad now and next (`schedule`), `history`, `bloodmoon`, `legend`, `census`, and `collect` for rewards still owed. Guide: [`RealmWorld.md`](../plugins/docs/RealmWorld.md) |
| `/treasure` | RealmWorld | Your clue in a treasure hunt and how far along you are; `hint` names a direction once per clue |
| `/caravan` | RealmWorld | The merchant caravan: `carry` (bear it from the start), `escort` (guard it), `leave`; and any raider's price |
| `/festival` | RealmWorld | The Harvest Fair and Midwinter: the house standings and your part; `give [item\|all]` hands in goods for your house |
| `/arrival` | RealmArrival | Where you are in your arrival (the walk from the Gatehouse of the Unwritten to the Hearth) and what is next; after it, your page so far. `skip` ends it at once, `tour` tells you the banners, the fire and the roads again as you pass them. Guide: [`RealmArrival.md`](../plugins/docs/RealmArrival.md) |
| `/kit` | RealmTravel | Your kits (a newcomer's pack, daily house provisions, the season's bounty); `/kit <name>`, `collect` |
| `/realm` | RealmHerald | Every command by subject, in a window (`/realm list` in chat), `/realm <subject>`, your first steps (`path`, `skip`, `crown`), `tips on/off`, `popups on/off`, `motd`. Guide: [`RealmHerald.md`](../plugins/docs/RealmHerald.md) |

## For admins

Each plugin has its own Oxide permission, granted with `oxide.grant user <name> <permission>` (or by group; who gets which is in [`community/ops/staff-roles-and-permissions.md`](community/ops/staff-roles-and-permissions.md)): `realmhouses.admin`, `crownandconsequences.admin`, `realmcontracts.admin`, `realmseasons.admin`, `realmevents.admin`, `realmlaws.admin`, `realmdynasties.admin`, `realmrenown.admin`, `realmtreasury.admin`, `realmravens.admin`, `realmwarden.admin`, `realmstats.admin`, `realmherald.admin`, `realmsculptor.admin`, `realmpainter.admin`, `realmlegendary.admin`, `realmsentinel.admin`, `realmdominion.admin`, `realmquests.admin`, `realmarena.admin`, `realmtravel.admin`, `realmcrafts.admin`, `realmworld.admin`, `realmheraldry.admin`, `realmarrival.admin` (and `realmarrival.skip`, which lets staff and testers skip the arrival). Staff who hold a plugin's admin permission do not compete in what it runs.

The admin subcommands live under the same commands as above (for example `/law zone set`, `/court admin`, `/dynasty admin`, `/renown admin`, `/raven admin queue`, `/warden alerts`, `/stats status`, `/season start`, `/event start`, `/realm admin motd`, `/dominion admin`, `/quest admin`, `/arena admin`, `/arena tourney open`, `/travel admin set`, `/kit admin check`, `/craft admin status`, `/world admin`, `/heraldry sync`, `/ballot admin open`, `/arrival admin check`). Each plugin guide in `plugins/docs/` lists them in full. Realm Steward's Court lists the staff commands of the plugins, ready to copy (for RealmArrival the ones that do not depend on where you stand).

### Staff-only commands

These four commands serve only holders of their plugin's admin permission (anyone else gets one pointer line), so the `/realm` hub does not list them (`STAFF_COMMANDS` in `tools/realm-integration/check.mjs`).

| Command | Plugin and permission | What staff do with it |
|---|---|---|
| `/sculpt` | RealmSculptor, `realmsculptor.admin` | Realm's block monuments: `list`, `preview`, `place`, `placed`, `protect`, `repair`, `remove`, `undo`, `materials`, `status`, `reload`. Guide: [`RealmSculptor.md`](../plugins/docs/RealmSculptor.md) |
| `/paint` | RealmPainter, `realmpainter.admin` | Realm's art on painted signs and the live boards: `list`, `nearby`, `info`, `<painting>`, `crest`, `sigil`, `chronicle`, `wanted`, `standings`, `proclamation`, `event`, `ironbreaker`, `notice`, `dominion`, `world`, `face`, `fit`, `redraw`, `signs`, `unbind`, `forget`, `clear`, `status`, `reload`. Guide: [`RealmPainter.md`](../plugins/docs/RealmPainter.md) |
| `/ironbreaker` | RealmLegendary, `realmlegendary.admin` | The one legendary blade: `status`, `items`, `grant`, `revoke`, `reset`. Guide: [`RealmLegendary.md`](../plugins/docs/RealmLegendary.md) |
| `/sentinel` | RealmSentinel, `realmsentinel.admin` | The cheat watch: `status`, `report`, `peaks`, `freeze`, `unfreeze`, `clear`, `ban`, `reload`, `help`. Guide: [`RealmSentinel.md`](../plugins/docs/RealmSentinel.md) |

### Staff commands of the wave-4 plugins

Under the players' commands, for holders of the plugin's permission. Every one is UNVERIFIED in game; the ROADMAP play-test rows that use them are named.

| Plugin and permission | Staff commands | First used in |
|---|---|---|
| RealmDominion, `realmdominion.admin` | `/dominion admin status`, `create <id> <kind> [radius] <name>`, `move <id> [radius]` (marks a holding where you stand), `radius`, `rename`, `remove <id> confirm`, `enable`, `disable`, `owner <id> <house\|none>`, `reset <id>`, `open [minutes]`, `close [minutes]`, `auto`, `payday` | PT0.14, PT1.22, PT1.24 |
| RealmQuests, `realmquests.admin` | `/quest admin status`, `reload`, `places`, `place set <id> [radius]`, `place clear <id>`, `reset <player> [daily\|weekly\|story\|all]`, `complete <player> <quest>`, `creatures`, `items <word>` | PT0.14, PT1.22, PT1.26 |
| RealmArena, `realmarena.admin` | `/arena admin status`, `zone set <name> [radius]`, `zone remove`, `tavern set`, `tavern remove`, `void <duel>`, `rating <player> <n>`, `reset <player> confirm`, `bar <player> <hours>`, `unbar`, `crown`, `pairs`, `settle`; `/arena tourney open [fee]`, `start`, `cancel` | PT1.22, PT4.9 |
| RealmTravel, `realmtravel.admin` | `/travel admin set <id> <capital\|seat\|holding\|landmark> [name]`, `name`, `note`, `house`, `kind`, `toll`, `radius`, `hidden`, `enabled`, `mark <id> <sculpture>`, `remove <id> confirm`, `list`, `status`, `unlock`, `lock`, `throne`, `tp <id>`; `/kit admin items <word>`, `check`, `reset <player> <kit\|all>` | PT0.14, PT1.22, PT1.25 |
| RealmCrafts, `realmcrafts.admin` | `/craft admin status`, `watch <player> [off]` (what the plugin sees, for the smoke test), `unmapped`, `items <word>`, `xp`, `level <player> <profession> <level>`, `reset <player> confirm`, `crown`, `cancel <id>` | PT0.14, PT1.27, PT1.28 |
| RealmWorld, `realmworld.admin` | `/world admin status`, `start <treasure\|bloodmoon\|caravan\|legend\|census> [target] [minutes] [force]`, `stop`, `schedule`, `place set`, `place clear`, `places`, `hunt new\|step\|undo\|radius\|chest\|reward\|enable\|show\|remove\|list`, `route add\|remove`, `routes`, `deco add\|remove\|list`, `festival start\|stop\|cancel`, `census`, `creatures`, `legends`, `bounty` | PT0.14, PT1.29, PT2.18, PT4.10 |
| RealmHeraldry, `realmheraldry.admin` | `/heraldry sync`, `preview [house]`, `set <house> <pair>`, `reset <house>`, `banner <house> <banner> <pattern>`, `status`; `/ballot admin open`, `advance <n>`, `cancel <n>`, `strike <n> <player>`, `audit <n>`, `voter <player>` | PT0.14, PT1.30, PT3.27 |
| RealmArrival, `realmarrival.admin` | `/arrival admin status`, `site`, `site anchor [+z\|+x\|-z\|-x]`, `site plan`, `site pieces`, `site signs`, `site reload`, `check`, `runsheet`, `lot`, `lot draw`, `lot set <six houses>`, `lot clear`, `open`, `open force` (test servers only), `close`, `pause`, `resume`, `mode teleport\|provider\|road\|off`, `gatemode open\|portcullis`, `stone add\|remove <n>\|list\|clear`, `mercy add\|remove <n>\|list\|clear`, `hall corner1\|corner2`, `droppad corner1\|corner2`, `eject set`, `threshold set`, `hearth set`, `wayboard set`, `throne set` (each also `clear`), `banner set\|clear <house>`, `gate set <w> <h> [+z\|+x\|-z\|-x]`, `gate build\|open\|close\|test\|remove`, `beacon build <radius> [dy]`, `beacon clear`, `beacon test`, `evict on\|off`, `wave on <minutes>\|off`, `play <player>`, `skip <player>`, `reset <player> [pending\|done]`, `veteran <player>`, `pass <player>` | No PT row yet: the guide's [first-test plan](../plugins/docs/RealmArrival.md#first-test-plan-10-steps) (10 steps) |

### Console commands

Oxide runs these chat commands only for a player in game, not from the server console (see [`admin-console.md`](admin-console.md)). Two console-only commands come from `RealmCourt.cs`, which writes them into the game's own command table with the permission `realm.court`: `/realm.save` (save the world now) and `/realm.players` (online players with Steam IDs). Realm Steward's Court screen uses them.

## Chat style

Every Realm plugin answers in one style, so a player can tell at a glance who is speaking and whether something worked. The colours are the game chat's `[RRGGBB]` tags. `node tools/realm-integration/check.mjs` checks all of this in every plugin that talks to players (RealmCourt is exempt: it speaks a console protocol to Realm Steward).

**The speaker.** A reply opens with the plugin's speaker name in the colour of its tone, then plain white text:

```
[D6A043]Houses[FFFFFF]: House Varrow - sigil: a black stag
```

| Plugin | Speaker | Plugin | Speaker |
|---|---|---|---|
| RealmHouses | Houses | RealmDynasties | Dynasties |
| CrownAndConsequences | Crown | RealmRenown | Renown |
| RealmLaws | Court | RealmTreasury | Treasury |
| RealmContracts | Contracts | RealmRavens | Ravens |
| RealmSeasons | Seasons | RealmWarden | Warden |
| RealmEvents | Events | RealmStats | Stats |
| RealmChronicle | Chronicle | RealmHerald | Realm |
| RealmQuests | Quests | | |
| RealmArena | Arena (the tavern games: Tavern) | | |
| RealmTravel | Roads | | |
| RealmCrafts | Guilds | | |
| RealmWorld | World | | |
| RealmHeraldry | Heraldry (votes: Council) | | |
| RealmArrival | Hearth | | |

The name is the lang key `Speaker`, so a server can rename it.

**The tone** is the colour of the speaker:

| Tone | Colour | Used for |
|---|---|---|
| News | gold `D6A043` | Answers, lists, information. The default. |
| Done | green `8FC97A` | Something the player did worked: founded, sworn, paid, sent. |
| Take care | amber `E8913A` | A warning or a reminder: a confirm step, a lapsing treaty, the next first step. |
| Refused | red `E86A5C` | An error, sent with the game's `SendError`. |

**The other colours:**

| Colour | Used for |
|---|---|
| `F4C96D` (command) | Every `/command` in a line, with its subcommand words: `[F4C96D]/house found[FFFFFF]`. Every command a line names must be registered by some plugin. |
| `A3A6AD` (muted) | Quiet voices: tips, timestamps, rumours. |
| House tints | A house name, in its house colour: the six great houses use `discordRole` from `art/palette.json` (Varrow `C58FC0`, Ashgrove `E08A5C`, Corvane `8FB0BF`, Dunmere `B8B85A`, Halloran `EC8A3C`, Merrin `6FBF85`); any other house gets one of the six by a stable hash of its name. |
| `FFFFFF` | Plain text after each coloured part. |

No other colour may appear in a Realm chat line.

**Realm-wide news** is spoken by the Herald, in one gold, from every plugin: `[D6A043]Herald[FFFFFF]: House Ashgrove bends the knee to House Varrow.`

**Lists.** A line that starts with two spaces continues the reply above it and carries no speaker, so a list reads as one block:

```
Houses: Houses of the realm (2):
  Varrow (a black stag) - 4 members
  Ashgrove (an oak in flame) - 2 members - sworn to Varrow
```

**Length.** No lang line is longer than 200 visible characters; longer help is split into short lines.

**Safety.** Player text (names, house names, letters) only ever goes in as an argument, never as the format string, and every line goes out through the single-string `SendMessage`, so braces and colour tags in a name cannot break or recolour a line. A server's older lang file whose line already opens with a colour tag or with `<speaker>:` is sent as it is, so nothing is doubled.

## Popups

Some commands also open the game's own popup windows. They make the realm feel richer, but none of them has been seen on a real client yet (**UNVERIFIED in game**), so every window comes with a chat fallback that is always sent too.

| Command | Window | Chat fallback |
|---|---|---|
| `/realm` (RealmHerald) | The hub: every command by subject. | One line pointing to `/realm list`, which prints the hub in chat. |
| First join (RealmHerald) | "Welcome to Ostreval": the greeting, the message of the day, the three first steps. | The four welcome lines in chat. |
| `/swear <house>` (RealmHouses) | Yes/No: offer your house's oath? | `/swear confirm` within `PopupAnswerSeconds` (2 minutes). |
| An oath offered to your house (RealmHouses) | Accept/Refuse, for the liege's leader. | `/swear accept <house>` or `/swear deny <house>`. |
| `/renounce` (RealmHouses) | Yes/No: break the oath, with what it costs. | `/renounce confirm` within `RenounceConfirmSeconds` (60 seconds). |
| `/house found` alone, or with a name only (RealmHouses) | Input windows for the house's name, then its sigil. A bad name or sigil is refused in chat and asked again. | The full line: `/house found "<name>" <sigil>`. |
| A challenge to a duel, a team duel or a tavern game (RealmArena) | Accept/Decline, with the stake shown. | `/duel accept <player> [marks]` (or `/dice`, `/cards`): with a stake, the stake must be typed. |
| Crowned Champion of the Ring; a tournament match called (RealmArena) | A notice with an Ok button. | The same news in chat. |
| `/dominion` (RealmDominion) | The map of the holdings, their houses and the War Hours. | The same map in chat. |
| `/quest`, `/quest story`, `/achievements` (RealmQuests) | The journal, the season's tale, your deeds. | The same text in chat, always. |
| A waystone found (RealmTravel) | "Waystone found", with its lore line. | The discovery line in chat. |
| Mastery and Master Crafter (RealmCrafts) | "Master of ..." and the weekly crowning. | The Herald's line and the chat reply. |
| A clue, a hoard, a Legend (RealmWorld) | "A Clue" with the next riddle; the hoard and the Legend's news. | `/treasure` repeats the clue; the Herald's lines. |
| A ballot opens (RealmHeraldry) | "The realm votes", for each player who may vote. | The Herald's line and `/ballot`. |
| A newcomer wakes in the Gatehouse (RealmArrival) | "The Gatehouse of the Unwritten", with the button "Open the Gate" ("Step through" while the arch is open). The button opens the gate. | The call to walk to the gold line before the gate; walking there opens it. |
| A newcomer stands on a house's pledge stone (RealmArrival) | Yes/No: "Look to House X?", with "Look to X" / "Walk on". Yes tells that house's members awake in the realm. | The dwell line: standing 3 more seconds on the stone looks to the house; stepping off cancels. |

**Switches.** `UsePopups` in each plugin's config (`oxide/config/RealmHerald.json`, `RealmHouses.json` and every plugin listed above) switches that plugin's windows off for the server. With RealmHouses' popups off, `/swear <house>` offers the oath at once, as it did before popups. A player turns every Realm window off for themselves with `/realm popups off` (RealmHouses asks RealmHerald's `PopupsWanted` before each window).

**The game's methods.** The windows come from the `Player` extension methods in `CodeHatch.Common.PlayerExtensions`. Their signatures were read from the `Assembly-CSharp.dll` metadata that `tools/plugin-compile-check/check.sh` downloads, and the plugins compile against it:

| Method | Parameters (defaults) |
|---|---|
| `MessageDialogue ShowPopup` | `title, message, buttonText = "Ok", Dialogue.OnSubmit handler = null, bool interupt = false, bool broadcast = true` |
| `void ShowConfirmPopup` | `title, message, confirmText = "Confirm", cancelText = "Cancel", handler = null, interupt = false, broadcast = true` |
| `void ShowInputPopup` | `title, message, initialInput = "Confirm", confirmText = "Confirm", cancelText = "Cancel", handler = null, interupt = false, broadcast = true` |
| `delegate void Dialogue.OnSubmit` | `Options selection, Dialogue dialogue, object contextData` |

The confirm button answers `Options.Yes`, the cancel button `Options.No`; an input window's text comes back in `dialogue.ValueMessage`. On a dedicated server a window reaches the client only with `broadcast = true`: the server then sends the `codehatch.ui.popup.*` event to that player and waits for the reply event.

**Rules every Realm window follows** (`check.mjs` checks the first three):

1. The game's methods are called only inside the plugin's `Popups` region, inside `try`, with every argument spelled out and `broadcast = true`. If the game throws, the plugin logs a warning and the chat path takes over.
2. The plugin has a `UsePopups` switch and asks `PopupsFor(player)` before each window.
3. A plugin that waits for answers ignores every answer after `Unload` (a reload).
4. An answer is only a request. The game keeps the handler in a table keyed by a timestamp, and the reply event carries no proof of who sent it (read from the game's code, not seen running). So an answer counts only if it matches the one question the plugin has open for that player (token, kind and deadline), it is used once, and it then goes through the same checks as the chat command it stands in for. A forged or late answer can at most confirm a question the player was asked and has not answered, within its deadline.
5. A window is plain text: chat colour tags are stripped.

**UNVERIFIED in game:** that the windows show on a client, how they look (size, line breaks, how much text fits), and that the answers come back to the server.

## Game commands

The game has its own commands, such as `/kick`, `/ban`, `/unban`, `/banlist`, `/mute`, `/whitelist`, `/give`, `/notice`, `/popup`, `/list`, `/name`, `/logout`, `/quit`, `/restart`, `/shutdown`, `/suicide`, `/time` and `/videofly`. No Realm command uses any of these names. The game's full command table is in DLLs this repo does not have, so this list is not complete (UNVERIFIED). A Realm command that shared a name with a game command the list misses would need an in-game check.
