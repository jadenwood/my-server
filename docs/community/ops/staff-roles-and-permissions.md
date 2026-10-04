# Staff roles and permissions

This page says who on the Realm staff holds which power, mapped to the **real permission names** the plugins register. If the plugins and this page ever disagree, the plugins win and this page is wrong. `tools/check-permissions.mjs` fails when a plugin registers a permission that has no row here, or when this page names a permission no plugin registers.

> Not legal advice. These are community operating rules for a volunteer-run game server.

Source of every permission name: the `PermAdmin` / `Permission` constants in `plugins/*.cs` (checked by the tool above). Source of the `oxide.*` commands: Oxide.ReignOfKings at tag 2.0.3867, `src/ReignOfKingsCore.cs` lines 94-98 and `src/ReignOfKingsCommands.cs` [SRC]. **UNVERIFIED at run time:** the group commands have never been run on the owner's server; only `oxide.grant user` has been tried, in the smoke test.

---

## 1. The four staff roles

| Role | Who | How many | Competes in the game? |
|---|---|---|---|
| **Owner** | The person who runs the server PC and Realm Steward, holds the signing key and pays the bills | 1, plus 1 named deputy who holds the break-glass notes | No, while holding any plugin admin permission |
| **Admin** | Trusted long-term staff who can change the state of the realm | 2-4 | **No.** An admin who wants to play competitively for a session has the permissions revoked first (section 5) |
| **Moderator** | Handles chat, reports and conduct. Does not change houses, the crown, laws or the economy | 2-6 | Yes, with limits (section 5) |
| **Event host** | Runs scheduled realm events and creator nights | as needed | Not in an event they are hosting |

Creators and streamers are **not** staff. They get no permission. Their rules are in [`creator-code-of-conduct.md`](creator-code-of-conduct.md).

## 2. The permission matrix

Legend: **Yes** = granted to the role's Oxide group. **On call** = the Owner grants it to a named person for a set time and revokes it after. **No** = never.

| Permission | Plugin | What it unlocks | Gameplay advantage while held | Owner | Admin | Moderator | Event host |
|---|---|---|---|---|---|---|---|
| `realm.court` | `RealmCourt.cs` | `/realm.save`, `/realm.players` in the game's own command table, used by Realm Steward through the admin console. The server player always has it | Lists every online Steam ID | No (server only) | No | No | No |
| `realmwarden.admin` | `RealmWarden.cs` | `/warden alerts`, `ack`, `player`, `evidence`, `protect`, `mute`, `unmute`, `clear`, `raid` | With `General.AdminsExempt: true` (the default): **skips raid hours**, chat limits and the name filter | Yes | Yes | Yes | No |
| `realmravens.admin` | `RealmRavens.cs` | `/raven admin queue`, `approve`, `reject`, `reports`, `resolve`, `audit`, `letter`, `mute`, `unmute`, `purge`, `save`. **`letter` shows a private letter's real sender and text**; every read is audited | Skips the spymaster change cooldown | Yes | Yes | Yes (privacy rule in section 4) | No |
| `realmstats.admin` | `RealmStats.cs` | `/stats status`, `/stats save` | None | Yes | Yes | No | No |
| `realmherald.admin` | `RealmHerald.cs` | `/realm admin motd add\|clear\|list`, `tip`, `reset <player>`, `status` | None (changes the welcome text and restarts a player's first steps) | Yes | Yes | No | No |
| `realmpainter.admin` | `RealmPainter.cs` | `/paint <artwork>` (binds the sign you look at and paints it), `list`, `info`, `signs`, `redraw`, `unbind`, `clear`, `forget`, `face`, `fit`, `notice`, `status`, `reload` | None (changes what signs show; touches no house, crown, law or item) | Yes | Yes | No | Yes |
| `realmevents.admin` | `RealmEvents.cs` | `/event start`, `stop`, `cancel`; may name King's Hunt quarry | Can name quarry and end events with prizes | Yes | Yes | No | Yes |
| `realmarena.admin` | `RealmArena.cs` | `/arena admin status`, `zone set\|remove`, `tavern set\|remove`, `void <duel>`, `rating <player> <n>`, `reset <player> confirm`, `bar <player> <hours>`, `unbar`, `crown`, `pairs`, `settle`; `/arena tourney open [fee]`, `start`, `cancel`: arenas and taverns on the land, ratings, bars for fleeing, the weekly crowning and the Lists of the Ring | **Can set any fighter's rating** and crown the week's champion early; voiding a duel or a tournament returns every stake | Yes | Yes | No | Yes |
| `realmseasons.admin` | `RealmSeasons.cs` | `/season start`, `/season end`, `/season status` | Ends a season and its standings | Yes | On call | No | No |
| `realmhouses.admin` | `RealmHouses.cs` | `/house disband`, `pardon`, `unlink`, `sync` | Can dissolve rival houses and clear marks | Yes | Yes | No | No |
| `crownandconsequences.admin` | `CrownAndConsequences.cs` | `/claim cancel`, `/council appoint\|remove` without being monarch; stuck-captive alerts | **Bypasses the throne-capture gate** (can take the throne outside rebellion windows) | Yes | Yes | No | No |
| `realmcontracts.admin` | `RealmContracts.cs` | `/contract admin cancel\|refund\|pay <id>` | Skips the posting cooldown; moves escrowed items | Yes | Yes | No | No |
| `realmlaws.admin` | `RealmLaws.cs` | `/law proclaim\|repeal` as if monarch (skipping cooldown and daily cap), `/law zone set\|remove`, `/court accuse`, `trial`, `champion`, `pardon`, `/court admin dismiss\|verdict\|clear` | **Exempt from `no_building` zones**; can bring and rule on cases | Yes | Yes | No | No |
| `realmdynasties.admin` | `RealmDynasties.cs` | `/dynasty admin pass`, `dissolve`, `title`, `prestige`, `check`, `save` | Skips disown and abdicate cooldowns | Yes | On call | No | No |
| `realmrenown.admin` | `RealmRenown.cs` | `/renown admin grant`, `title give\|take`, `reset`, `status`, `save` | Skips the command cooldown; can grant points and titles | Yes | On call | No | No |
| `realmtreasury.admin` | `RealmTreasury.cs` | `/treasury audit`, `freeze`, `unfreeze`, `cancel` (any order), `escheat` | Skips trade rate cooldowns; can cancel any market order | Yes | On call | No | No |
| `realmsentinel.admin` | `RealmSentinel.cs` | `/sentinel status`, `report`, `clear`, `reload`, `freeze`, `unfreeze`, `ban <player> confirm`, `peaks`: suspicion scores, cheat evidence and responses. Holders also count as staff for the name-impersonation check | With `General.AdminsExempt: true` (the default): **skips every cheat check** | Yes | Yes | No | No |
| `realmlegendary.admin` | `RealmLegendary.cs` | `/ironbreaker status`, `grant <player> [force]`, `revoke`, `reset confirm`, `items [word]`: who bears the Ironbreaker, and its audit | **Can put the legendary blade in anyone's hands**, including their own | Yes | Yes | No | No |
| `realmsculptor.admin` | `RealmSculptor.cs` | `/sculpt list`, `preview`, `place`, `undo`, `remove`, `placed`, `status`, `protect`, `repair`, `materials`, `reload`: place, protect and take down the realm's block monuments | `place ... force` replaces players' blocks (put back on undo); a protected monument can block a road or a door | Yes | Yes | No | No |
| `realmdominion.admin` | `RealmDominion.cs` | `/dominion admin status`, `create`, `move`, `radius`, `rename`, `remove`, `enable`, `disable`, `owner`, `reset`, `open`, `close`, `auto`, `payday`: mark the holdings on the land, open or close the War Hours, settle an owner | **Can hand a holding (and its daily marks) to any house** and open the field at will; holders never count in the field while `AdminsCount` is false (the default) | Yes | Yes | No | Yes |
| `realmquests.admin` | `RealmQuests.cs` | `/quest admin status`, `reload`, `places`, `place set\|clear`, `reset <player>`, `complete <player> <quest>`, `creatures`, `items`: quest content, named places and a player's journal | `complete` finishes a task with its reward (a testing aid): **can pay marks and goods to anyone**, including themselves | Yes | On call | No | No |
| `realmtravel.admin` | `RealmTravel.cs` | `/travel admin set`, `name`, `note`, `house`, `kind`, `toll`, `radius`, `hidden`, `enabled`, `mark`, `remove`, `list`, `unlock`, `lock`, `throne`, `tp`, `status`; `/kit admin items`, `check`, `reset`: raise and run the realm's waystones and kits | `/travel admin tp` moves them to any waystone; `unlock` and `/kit admin reset` can favour a player. With `General.AdminsExempt: true`: **no channel time, cooldowns or tolls** | Yes | Yes | No | No |
| `realmworld.admin` | `RealmWorld.cs` | `/world admin status`, `start`, `stop`, `schedule`, `place set\|clear`, `places`, `hunt ...`, `route add\|remove`, `routes`, `deco add\|remove\|list`, `festival start\|stop\|cancel`, `census`, `creatures`, `legends`, `bounty [clear]`: lay out treasure hunts, caravan routes and festival decorations, start or call off world events | `start ... force` can run a world event over a RealmEvents event; a hunt's riddles and dig site are known to whoever lays them out, so holders win nothing in the world while `General.AdminsCanWin` is false (the default) | Yes | Yes | No | Yes |

Things that are **not** Oxide permissions but are staff powers all the same:

| Power | Where it lives | Owner | Admin | Moderator | Event host |
|---|---|---|---|---|---|
| Kick, ban, unban, mute, whitelist, notices, popups, save, stop | Realm Steward **Court** screen, through the game's admin console as the server player (`docs/admin-console.md`) | Yes | Yes, only on the server PC (remote desktop) | Ask an admin | No |
| Oxide `oxide.grant`, `oxide.revoke`, `oxide.group`, `oxide.usergroup`, `oxide.show` | Server console (the server player passes every check). In game they need the permissions of the same names | Yes | No | No | No |
| Server-list signing key, `realm-ops.json`, `rclone.conf`, Discord bot token, webhook URLs, RDP password | Server PC only (`docs/going-public.md` §3, `ops/`, `bot/README.md`) | Yes | No | No | No |
| Moderation evidence: `oxide/logs/RealmWarden/`, `%APPDATA%\Realm\court\court-log.jsonl`, `/raven admin audit` | Server PC and in-game admin views | Yes | Yes | Read in game only | No |
| Discord: ban, timeout, delete messages | Discord roles | Yes | Yes | Yes | Timeout only, in event channels |

## 3. Setting it up with Oxide groups

Run these in the **server console** (Steward's Servers console). Groups mean a person changes role with one line, and the grant list stays in one place.

```
oxide.group add realm_admin "Admin" 3
oxide.group add realm_mod "Moderator" 2
oxide.group add realm_host "Event host" 1

oxide.grant group realm_admin realmwarden.admin
oxide.grant group realm_admin realmravens.admin
oxide.grant group realm_admin realmstats.admin
oxide.grant group realm_admin realmherald.admin
oxide.grant group realm_admin realmpainter.admin
oxide.grant group realm_admin realmevents.admin
oxide.grant group realm_admin realmarena.admin
oxide.grant group realm_admin realmhouses.admin
oxide.grant group realm_admin crownandconsequences.admin
oxide.grant group realm_admin realmcontracts.admin
oxide.grant group realm_admin realmlaws.admin
oxide.grant group realm_admin realmsentinel.admin
oxide.grant group realm_admin realmlegendary.admin
oxide.grant group realm_admin realmsculptor.admin
oxide.grant group realm_admin realmdominion.admin
oxide.grant group realm_admin realmquests.admin
oxide.grant group realm_admin realmtravel.admin
oxide.grant group realm_admin realmworld.admin

oxide.grant group realm_mod realmwarden.admin
oxide.grant group realm_mod realmravens.admin

oxide.grant group realm_host realmevents.admin
oxide.grant group realm_host realmarena.admin
oxide.grant group realm_host realmworld.admin
oxide.grant group realm_host realmpainter.admin
oxide.grant group realm_host realmdominion.admin

oxide.usergroup add <SteamID64> realm_mod
oxide.usergroup remove <SteamID64> realm_mod
oxide.show group realm_mod
```

- Use the **SteamID64**, not the name: names can be changed to look like someone else.
- "On call" permissions go to a person, not a group, and come off afterwards: `oxide.grant user <SteamID64> realmtreasury.admin`, then `oxide.revoke user <SteamID64> realmtreasury.admin`. Log both in the staff log.
- Never grant `realm.court` to anyone. Steward does not need it granted.
- Syntax is from the Oxide 2.0.3867 source [SRC]. **UNVERIFIED** on the live server. If `oxide.group` replies with a usage line, fall back to `oxide.grant user` per person.

## 4. Rules that come with the power

1. **Admins and the Owner do not compete** while they hold any permission in the "Admin" column (this extends `docs/community/rules.md` §6 to every plugin, because each one gives a real advantage, listed in the matrix).
2. **Moderators may play**, but `realmwarden.admin` lets them break blocks outside raid hours. Either set `"General": {"AdminsExempt": false}` in `oxide/config/RealmWarden.json` (recommended once you have moderators who play), or moderators never damage another group's blocks outside raid hours. Breaking this is abuse of power (moderation handbook, level 4 staff misconduct).
3. **Private letters.** `/raven admin letter <#>` is only used on a letter that a player has reported, or on a named, logged harassment case. It is never used out of curiosity, and never on a letter about your own house. Every read is already in the audit, and the Owner reviews that audit weekly (it is kept 7 days at most).
4. **No conflicts.** Staff never act on a case involving their own house, friends, stream or Discord DMs. Hand it over.
5. **Every state change is logged** in the staff log with the reason: who, what command, why, link to the case. Plugin actions that reach the Chronicle (for example `/claim cancel`, "set aside by the realm's stewards") are public anyway.
6. **Two-person rule** for: permanent bans, house disbands, season end, treasury cancels over 1000 marks, `/renown admin reset`, `/dynasty admin dissolve`, and any restore of a backup. One proposes in the staff channel, a second approves, then it is done.
7. **Event hosts** start and stop only the events on the published schedule or an announced creator night. Starting a Crown Night by surprise to help a friend's house is misconduct.

## 5. Joining, leaving and playing

| When | Do this |
|---|---|
| New moderator | 2 weeks as a trial moderator: Discord powers only, shadow an admin on 3 cases. Then `oxide.usergroup add <id> realm_mod`. Read this page, the handbook and the privacy notice; confirm in writing in the staff channel |
| Admin wants to play competitively for a session | `oxide.usergroup remove <id> realm_admin` before joining. Post it in the admin log. Add back afterwards. Their own house's open cases go to another admin |
| Staff leaves or goes quiet for 30 days | Remove from every group the same day, remove Discord staff roles, rotate any shared secret they might have seen (`ops/disaster-recovery.md` G) |
| Staff removed for misconduct | As above, and the Owner reviews their last 30 days of actions in the staff log, the Court log and the Warden evidence |

Every quarter the Owner runs `oxide.show group realm_admin`, `realm_mod` and `realm_host` and compares them with the staff list. Any name that should not be there is removed and treated as an incident ([`incident-response.md`](incident-response.md), level 2).

## 6. Keeping this page true

```
node docs/community/ops/tools/check-permissions.mjs
node --test docs/community/ops/tools/*.test.mjs
```

Run both after any plugin adds or renames a permission.
