# Realm chat commands

Every chat command the Realm plugins register, in one place. Type them in the game's chat. Most commands show their own help when typed with no arguments (or with `help`). In game, `/realm` lists them all by subject.

This list is checked against the plugin source: `node tools/realm-integration/check.mjs --commands` prints the same commands from `plugins/*.cs` and fails if two plugins register the same one. **No command is registered twice.** All of this is compile-checked and mock-tested only. None of it has been run on a live server yet (UNVERIFIED in game).

Full rules and examples: [`community/how-to-play.md`](community/how-to-play.md) for houses, crown, claims, ransom, contracts, seasons and events, and the plugin guides in [`../plugins/docs/`](../plugins/docs/) for the rest. How the systems fit together: [`realm-systems.md`](realm-systems.md).

## For every player

| Command | Plugin | What it is for |
|---|---|---|
| `/house` | RealmHouses | Found, join, leave and run a house: `found "<name>" <sigil>` (or `found` alone to be asked in windows), `invite`, `join`, `leave`, `kick`, `promote`, `demote`, `info`, `list`, `link`/`unlink`, `disband` |
| `/swear` | RealmHouses | Offer your house's oath to a liege (`/swear <house>`; with popups on it asks Yes/No first, and `/swear confirm` is the chat answer), or accept or deny one (`accept`/`deny <house>`) |
| `/renounce` | RealmHouses | Break your house's oath (`/renounce`, then Yes in the window or `/renounce confirm`). Earns an oathbreaker mark |
| `/treaty` | RealmHouses | `propose <house> [days]`, `accept <house>`, `break <house>`, `list` |
| `/crown` | CrownAndConsequences | Who reigns, since when, and the next rebellion window |
| `/decree` | CrownAndConsequences | The decrees and their cooldowns; the monarch issues one with `/decree <id>` |
| `/council` | CrownAndConsequences | The council seats; the monarch uses `appoint <player> <seat>` and `remove` |
| `/claim` | CrownAndConsequences | Open claims (`list`); a house leader uses `declare` |
| `/ransom` | CrownAndConsequences | Captives (`list`); a captor uses `set`, `paid` and `release`; a captive uses `free` |
| `/chronicle` | RealmChronicle | The latest Chronicle entries (`/chronicle [count]`) |
| `/contract` | RealmContracts | Bounties, deliveries and mercenary work: `list`, `info`, `post`, `accept`, `deliver`, `confirm`, `cancel`, `enemies`, `items`, `collect`. The monarch also has `outlaw` and `pardon` |
| `/season` | RealmSeasons | The season, `standings`, `house [name]`, `hall` (the Hall of Kings) and `history` |
| `/events` | RealmEvents | What is running now and what comes next |
| `/event` | RealmEvents | `collect` prizes that did not fit in your packs |
| `/tourney` | RealmEvents | `join`, `leave` and `standings` for the Royal Tournament |
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
| `/economy` | RealmTreasury | All the economy help, the market fee, the tithe and the game tax |
| `/raven` | RealmRavens | Letters between players and houses: `<house or player> <message>`, `anon`, `inbox`, `read`, `delete`, `sent`, `status`, `block`, `unblock`, `blocks`, `report`; spymasters and spies have `spymaster`, `watch` and `spy ...` |
| `/rumour` (or `/rumor`) | RealmRavens | Submit an anonymous rumour for moderation, or see the approved ones (`list`) |
| `/warden` | RealmWarden | `status`, `rules`, `report <player> <reason>`, and `protection off confirm` to give up new-player protection |
| `/stats` | RealmStats | What the server's statistics record about you (`privacy`, `me`) and `optout` / `optin` |
| `/realm` | RealmHerald | Every command by subject, in a window (`/realm list` in chat), `/realm <subject>`, your first steps (`path`, `skip`, `crown`), `tips on/off`, `popups on/off`, `motd`. Guide: [`RealmHerald.md`](../plugins/docs/RealmHerald.md) |

## For admins

Each plugin has its own Oxide permission, granted with `oxide.grant user <name> <permission>`: `realmhouses.admin`, `crownandconsequences.admin`, `realmcontracts.admin`, `realmseasons.admin`, `realmevents.admin`, `realmlaws.admin`, `realmdynasties.admin`, `realmrenown.admin`, `realmtreasury.admin`, `realmravens.admin`, `realmwarden.admin`, `realmstats.admin`, `realmherald.admin`. The admin subcommands live under the same commands as above (for example `/law zone set`, `/court admin`, `/dynasty admin`, `/renown admin`, `/raven admin queue`, `/warden alerts`, `/stats status`, `/season start`, `/event start`, `/realm admin motd`). Each plugin guide in `plugins/docs/` lists them.

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

**Switches.** `UsePopups` in `oxide/config/RealmHerald.json` and `oxide/config/RealmHouses.json` switches each plugin's windows off for the server. With RealmHouses' popups off, `/swear <house>` offers the oath at once, as it did before popups. A player turns every Realm window off for themselves with `/realm popups off` (RealmHouses asks RealmHerald's `PopupsWanted` before each window).

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
