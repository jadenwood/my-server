# Realm chat commands

Every chat command the Realm plugins register, in one place. Type them in the game's chat. Most commands show their own help when typed with no arguments (or with `help`).

This list is checked against the plugin source: `node tools/realm-integration/check.mjs --commands` prints the same commands from `plugins/*.cs` and fails if two plugins register the same one. **No command is registered twice.** All of this is compile-checked and mock-tested only. None of it has been run on a live server yet (UNVERIFIED in game).

Full rules and examples: [`community/how-to-play.md`](community/how-to-play.md) for houses, crown, claims, ransom, contracts, seasons and events, and the plugin guides in [`../plugins/docs/`](../plugins/docs/) for the rest. How the systems fit together: [`realm-systems.md`](realm-systems.md).

## For every player

| Command | Plugin | What it is for |
|---|---|---|
| `/house` | RealmHouses | Found, join, leave and run a house: `found "<name>" <sigil>`, `invite`, `join`, `leave`, `kick`, `promote`, `demote`, `info`, `list`, `link`/`unlink`, `disband` |
| `/swear` | RealmHouses | Offer your house's oath to a liege (`/swear <house>`), or accept or deny one (`accept`/`deny <house>`) |
| `/renounce` | RealmHouses | Break your house's oath (`/renounce`, then `/renounce confirm`). Earns an oathbreaker mark |
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

## For admins

Each plugin has its own Oxide permission, granted with `oxide.grant user <name> <permission>`: `realmhouses.admin`, `crownandconsequences.admin`, `realmcontracts.admin`, `realmseasons.admin`, `realmevents.admin`, `realmlaws.admin`, `realmdynasties.admin`, `realmrenown.admin`, `realmtreasury.admin`, `realmravens.admin`, `realmwarden.admin`, `realmstats.admin`. The admin subcommands live under the same commands as above (for example `/law zone set`, `/court admin`, `/dynasty admin`, `/renown admin`, `/raven admin queue`, `/warden alerts`, `/stats status`, `/season start`, `/event start`). Each plugin guide in `plugins/docs/` lists them.

Oxide runs these chat commands only for a player in game, not from the server console (see [`admin-console.md`](admin-console.md)). Two console-only commands come from `RealmCourt.cs`, which writes them into the game's own command table with the permission `realm.court`: `/realm.save` (save the world now) and `/realm.players` (online players with Steam IDs). Realm Steward's Court screen uses them.

## Game commands

The game has its own commands, such as `/kick`, `/ban`, `/unban`, `/banlist`, `/mute`, `/whitelist`, `/give`, `/notice`, `/popup`, `/list`, `/name`, `/logout`, `/quit`, `/restart`, `/shutdown`, `/suicide`, `/time` and `/videofly`. No Realm command uses any of these names. The game's full command table is in DLLs this repo does not have, so this list is not complete (UNVERIFIED). A Realm command that shared a name with a game command the list misses would need an in-game check.
