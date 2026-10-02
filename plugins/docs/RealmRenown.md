# RealmRenown: renown, infamy and the titles of Ostreval

`plugins/RealmRenown.cs` (Oxide 2.0.3867, C# 3). It keeps the realm's **roll of honour**:

- Every deed is scored as **renown** (glory), **infamy** (shame), or both. Deeds are read only from verified game hooks, from other Realm plugins' calls, or from their data files read without writing. Chat is never parsed.
- Deeds earn **titles**: *Kingslayer*, *Usurper*, *Kingmaker*, *The Unbowed*, *Warden of Roads*, *Sellsword*, *Oathbreaker* and more. A title is kept once earned.
- A player can **wear one title** in global chat as a coloured prefix: `[Kingslayer] Brannoc : well met`.
- `/renown top` shows the ten most renowned players, and `/renown top infamy` the ten most infamous.

Lore: the Hearth Charter promises that *all of this is written down*. The Chronicle records what happened, and the roll of honour records **who did it**. Ashgrove "never forgets who broke an oath". The roll makes sure nobody else forgets either. Infamy fades slowly, but a title such as *Oathbreaker* is kept for good.

Status: **compile-checked against the real 2.0.3867 DLLs (0 errors, 0 warnings) and behaviour-tested against mocks (108 checks). It has never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

---

## Commands

| Command | Who | What it does |
|---|---|---|
| `/renown help` (also `/titles help`) | anyone | Shows the help. Admins also see the admin line. |
| `/renown` | anyone | Shows your renown, infamy, standing (renown − infamy), rank, titles, the title you wear and your last four deeds. |
| `/renown <player>` | anyone | Shows the same for another player. Matches an exact known name first, then a unique partial match among online players. |
| `/renown top` | anyone | Shows the top `TopCount` (10) players by renown. Ties go to the player with less infamy. The worn title is shown. |
| `/renown top infamy` | anyone | Shows the top 10 players by infamy. |
| `/titles` | anyone | Lists your titles. `>` marks the one you wear. |
| `/titles all` | anyone | Lists every title and what earns it. `*` marks the ones you hold. |
| `/titles <title>` | anyone | Shows what one title means and what earns it. `unbowed`, `the unbowed` and the id `unbowed` all work. |
| `/titles set <title>` | holder | Wears the title in global chat. Subject to `TitleChangeCooldownSeconds` (60). |
| `/titles clear` | anyone | Stops wearing a title. Same cooldown. |
| `/renown admin grant <player> renown\|infamy <+/-n> [reason]` | admin | Adjusts points by at most `AdminMaxAdjust` (1000) at a time. Points never drop below 0. The change is logged to the console and to the player's deed log, and titles are re-checked. |
| `/renown admin title give\|take <player> <title>` | admin | Gives a title (no announcement) or takes one away. Taking the worn title also removes the chat prefix. |
| `/renown admin reset <player> [confirm]` | admin | Erases a player's record. Needs `confirm` within 60 s. |
| `/renown admin status` | admin | Shows roll size, watched rebellions, the chronicle cursor, which Realm plugins are loaded, and the last result of each feed. |
| `/renown admin save` | admin | Saves now. |

Admin permission: **`realmrenown.admin`**. Grant it in the server console with `oxide.grant user <name|id> realmrenown.admin`.

Players get a short cooldown between commands (`CommandCooldownSeconds`, 2 s). Admins have none.

---

## Deeds: where every point comes from

| Deed (config key) | Default | Source | Tag |
|---|---|---|---|
| `reign_day` | +40 renown | The reigning monarch, for every `ReignDayHours` (24 h) on the throne. `KingsScheme.GetKingID()` is polled every `TickSeconds`. Time only counts while the server runs; gaps are capped at 3 ticks. | [ASM][USE RaidBoss.cs:496] |
| `kingslayer` | +60 renown, +40 infamy, 6 h cooldown per victim | `OnEntityDeath(EntityDeathEvent)`. The victim (`evt.Entity.Owner`) is the monarch, or was the monarch within `KingslayerGraceSeconds` (60). The killer is `evt.KillingDamage.DamageSource.Owner`. Suicide does not count. | [OPJ L188][ASM][USE DeathMessages.cs:20] |
| `crown_seized` | +120 | A declared rebellion ends and the claimant house wears the crown. Goes to the new monarch. | CrownAndConsequences `GetOpenClaims`, `GetKingHouse` |
| `kingmaker` | +60 | Same rebellion: every **other** member of the claimant house, or of a house sworn to it, who was **online during the Lawful Hours**. | + RealmHouses `GetHouse`, `GetLiege` |
| `crown_defended` | +100 | A rebellion ends and the crown house still holds the throne. Goes to the monarch. | same |
| `rebellion_defended` | +40 | Same rebellion: crown-side members and vassals who were online during the window. | same |
| `contract_bounty` / `contract_delivery` / `contract_merc` | +25 / +15 / +30 (cooldowns 20 / 30 / 0 min) | A contract reaches `Status: "done"`. Goes to its `FulfillerId`. | `oxide/data/RealmContracts.json`, read only |
| `oath_broken` / `treaty_broken` | +60 / +40 infamy | The per-player `OathsBroken` / `TreatiesBroken` counters in RealmHouses rise. These are the players who typed `/renounce` or broke the treaty. | `oxide/data/RealmHouses.json`, read only |
| `outlawed` | +50 infamy, 12 h cooldown | A player newly appears in RealmLaws `GetCourtOutlaws` or RealmContracts `IsOutlaw(id)`. | plugin calls |
| `tournament_win` / `quarry_taken` / `truce_broken` | +80 / +30 renown / +30 infamy | A new chronicle event `tournament_champion` / `hunt_kill` / `truce_broken`. The doer is `actors[0]`, matched by name (see below). | `oxide/data/RealmChronicle.json`, read only |

**Why some sources are data files.** RealmContracts and RealmHouses have no API that names who fulfilled a contract or who broke an oath. RealmEvents reports tournament and hunt winners only to the Chronicle. This plugin may not edit those plugins, so it reads their files. It checks `ExistsDatafile` first, because `DataFileSystem.ReadObject` would otherwise *create* a missing file. It **never writes** another plugin's file, and it skips a file it cannot parse for that poll. If those plugins later gain an API, they can call `AddDeed` (below) instead.

**Name matching (chronicle feed only).** Chronicle actors are names, not Steam ids. A name is matched, case-insensitively and with colour tags stripped, against the players this plugin has seen. If the name is unknown, or two known players share it, no deed is recorded and a line goes to the server log.

**First run.** Every feed **baselines** on its first poll: existing contracts, oath marks, outlaws and chronicle events are not scored retroactively. Set `CountHistoryOnFirstRun: true` *before* the first start to score history. A contracts or chronicle file that is reset (ids start again) is re-baselined, never replayed. A RealmContracts outlaw is checked only while online, so a player's first check after install is their baseline.

**Daily caps.** Each player gains at most `MaxRenownPerDay` (400) renown and `MaxInfamyPerDay` (400) infamy per UTC day. Over the cap the deed still **counts toward titles** but scores no points, and the player is told. **Infamy fades** by `InfamyDecayPerDay` (5) at each UTC day change (at most 7 days are caught up). Renown does not fade.

---

## Titles (defaults)

| Title | Earned by |
|---|---|
| Kingslayer | 1 `kingslayer` |
| Usurper | 1 `crown_seized` |
| Kingmaker | 1 `kingmaker` |
| The Unbowed | 1 `crown_defended` |
| Shield of the Crown | 3 `rebellion_defended` |
| The Long Reign | 7 `reign_day` |
| Warden of Roads | 5 `contract_delivery` |
| Sellsword | 3 `contract_merc` |
| Headtaker | 3 `contract_bounty` |
| Champion of the Lists | 1 `tournament_win` |
| Crown's Huntsman | 3 `quarry_taken` |
| the Renowned | 500 renown |
| Legend of Ostreval | 2000 renown |
| Oathbreaker *(infamous)* | 1 `oath_broken` |
| The Faithless *(infamous)* | 2 `treaty_broken` |
| Trucebreaker *(infamous)* | 1 `truce_broken` |
| The Hunted *(infamous)* | 1 `outlawed` |
| Black Name *(infamous)* | 300 infamy at once |

A new title is told to the player and announced realm-wide, at most `MaxAnnouncementsPerHour` (6) times per hour. It is also logged to the Chronicle as `title_earned`, at most `MaxChronicleTitlesPerHour` (4) times per hour. That event type is now registered (see [Integration](#integration)). On a server with an older RealmChronicle.cs, the Chronicle rejects the first one and this plugin stops trying until it is reloaded.

---

## The chat prefix

The game builds every global chat line from the sender's `Player.ChatFormat`. The default is `%name% : %message%`, set in `CodeHatch.Permissions.Permission`. **[IL]** `PlayerChatEvent.ChatMessage` returns `ChatFormat.Replace("%name%", DisplayName).Replace("%message%", Message)`, and `PlayerChatEvent.ChatFormat` falls back to `Player.ChatFormat`. `Player.ChatFormat` is a public property marked `[Syncable]`. The game changes it at runtime itself: `CoreServer.UpdatePlayerData` calls `set_ChatFormat` after the admin `chatformat` command, and every player is registered with `SyncManager` on join.

The plugin **prepends** `ChatPrefixFormat` (`[C8A050]{title}[-] `), or `InfamousPrefixFormat` (`[B04040]{title}[-] `) for infamous titles, to the player's own format. It does not hook chat and does not re-broadcast messages, so local, guild and whisper chat are untouched, and so are mutes, colour permissions and other plugins' chat hooks. It re-applies the prefix every tick in case the game reset the format, it never stacks the prefix, it keeps any custom format underneath, it leaves a format without `%message%` alone, and it restores every format on unload. Title names are limited to letters, digits, spaces, `'` and `-`, so a title can never inject `%name%`/`%message%` or colour tags. Set `ChatPrefixEnabled: false` to turn the prefix off.

---

## Config (`oxide/config/RealmRenown.json`)

| Key | Default | Meaning |
|---|---|---|
| `TickSeconds` | 30 | Crown and rebellion polling, chat prefix refresh. 5 to 300. |
| `FeedPollSeconds` | 60 | How often the contract, house, chronicle and outlaw feeds are read. |
| `MaxRenownPerDay`, `MaxInfamyPerDay` | 400, 400 | Daily point caps per player. |
| `InfamyDecayPerDay` | 5 | Infamy removed from everyone at each UTC day change. |
| `ReignDayHours` | 24 | Hours on the throne per `reign_day`. |
| `KingslayerGraceSeconds` | 60 | How long after the throne empties the former monarch still counts as the monarch for `kingslayer`. |
| `RebellionResolveGraceMinutes` | 30 | A watched rebellion is resolved only if its end is seen within this window. Otherwise, for example after downtime, it is dropped without deeds. |
| `CountHistoryOnFirstRun` | false | Score existing history on the first poll of each feed. |
| `ChatPrefixEnabled`, `ChatPrefixFormat`, `InfamousPrefixFormat` | true, see above | Must contain `{title}`, no `%`, 60 characters at most. |
| `TitleChangeCooldownSeconds`, `CommandCooldownSeconds` | 60, 2 | |
| `TopCount` | 10 | 1 to 25. |
| `AnnounceTitles`, `MaxAnnouncementsPerHour` | true, 6 | |
| `ChronicleTitles`, `MaxChronicleTitlesPerHour` | true, 4 | |
| `MaxPlayers` | 5000 | Roll size. The longest-unseen players without points or titles are pruned first. |
| `DeedLogPerPlayer` | 10 | The newest deeds kept per player. |
| `MaxAwardsPerPoll`, `MaxMarkDeltaPerPoll` | 100, 3 | Feed caps: deeds per poll, and oath/treaty marks per player per poll. |
| `AdminMaxAdjust` | 1000 | |
| `FeedReign`, `FeedKingslayer`, `FeedRebellions`, `FeedContracts`, `FeedHouses`, `FeedOutlaws`, `FeedChronicle` | true | Turn individual sources off. |
| `ChronicleDeeds` | `tournament_champion→tournament_win`, `hunt_kill→quarry_taken`, `truce_broken→truce_broken` | Chronicle event type → deed kind. The doer is `actors[0]`. Add a mapping here when a new event type names its doer first. |
| `Deeds` | see the deeds table | `{ "Label", "Renown", "Infamy", "CooldownMinutes" }` per kind. Values 0 to 10000. Built-in kinds are always present. Extra kinds can only be awarded through `AddDeed`. |
| `Titles` | see the titles table | `{ "Id", "Name", "Description", "Infamous", "Requires": { "<deed kind>\|renown\|infamy": n } }`. All requirements must be met. An invalid title (bad name, unknown requirement, duplicate) is dropped with a warning. |

Delete the file and reload to get the defaults back. If the file cannot be parsed, the plugin runs on defaults for that session and **does not overwrite** it.

## Data (`oxide/data/RealmRenown.json`)

The file holds players (name, points, stats, titles, worn title, cooldowns, deed log), watched rebellions, and the feed cursors and baselines. It is saved at most once a minute when something changed, and also on server save and unload. **If the file exists but cannot be parsed, or is `null`, renown is paused, every command says so, and the file is never written.** Fix or remove it, then reload.

---

## API (for other plugins, via `[PluginReference] Plugin RealmRenown` + `Call`)

All methods are non-public on purpose (Oxide's `Call` reaches only non-public methods).

| Call | Returns |
|---|---|
| `GetRenown(string playerId)` / `GetInfamy(string playerId)` | `int` (0 if unknown) |
| `GetChosenTitle(string playerId)` | The worn title's display name, or null |
| `GetTitles(string playerId)` | `string[]` of title ids |
| `HasTitle(string playerId, string titleId)` | `bool` |
| `AddDeed(string playerId, string playerName, string kind, string note, string dedupeKey)` | `bool`: true if the deed counted. `kind` must be a configured deed. Cooldowns and daily caps apply. A non-empty `dedupeKey` (e.g. `"realmevents:tournament:42"`) is never counted twice. |

---

## Integration

- **New chronicle event type:** `title_earned` ("A Title Earned", icon `seal`). It is registered in `plugins/RealmChronicle.cs` `KnownTypes`, `chronicle/server.js` `EVENT_TYPES` and `chronicle/public/assets/common.js` `TYPE_META`. With an older RealmChronicle.cs, titles are announced in chat only.
- **Suggested (optional) changes in other plugins**, for their owners to decide: RealmContracts, RealmHouses and RealmEvents could call `RealmRenown.Call("AddDeed", id, name, "<kind>", note, "<plugin>:<id>")` at the moment of the deed. The matching feed can then be switched off (`FeedContracts`, `FeedHouses`, or a `ChronicleDeeds` entry). That would replace file reading and name matching with exact ids.

---

## Smoke test (local test server, two clients or one client plus admin console)

Before you start: `tools/plugin-compile-check/check.sh` shows OK, and `plugins/docs/RealmRenown/logic-tests/run.sh` reports `0 failed`. For a fast test, set `"ReignDayHours": 1` and `"FeedPollSeconds": 10` in the config, then `oxide.reload RealmRenown`.

1. **Load.** Copy `RealmRenown.cs` into `oxide/plugins/` together with the other Realm plugins. The console shows it compiled, and `oxide/data/RealmRenown.json` and `oxide/config/RealmRenown.json` exist. `oxide/data/RealmContracts.json`, `RealmHouses.json` and `RealmChronicle.json` are **not** created by this plugin if they were absent.
2. **Help.** `/renown help` shows three lines. With `realmrenown.admin` granted, a fourth (admin) line appears.
3. **Reign.** Player A takes the Old Throne. After one hour (test setting), A is told "A day upon the Old Throne (+40 renown)", and `/renown` shows 40.
4. **Kingslayer.** Player B kills A while A reigns. B is told about the kingslaying, gets +60 renown and +40 infamy, the server announces "B has earned the title Kingslayer", and B gets the title. Killing A again at once gives nothing (cooldown).
5. **Chat prefix.** B types `/titles set kingslayer`, then says something in **global** chat. **Expected: both clients show `Kingslayer B : ...` with the title in gold.** Check that local and guild chat are unchanged. `/titles clear`, then speak again: no prefix. *(This is the main UNVERIFIED item: whether the synced format reaches clients.)* Also run `oxide.unload RealmRenown` and confirm chat is back to normal.
6. **Rebellion.** With CrownAndConsequences and RealmHouses loaded, house X declares a claim. During its window, at least two members of X are online, and one takes the throne. When the window closes: the new monarch gets *Usurper*, the other online member gets *Kingmaker*. Run the other way round (the crown holds) to get *The Unbowed*.
7. **Contracts.** Fulfil a delivery contract with RealmContracts. Within `FeedPollSeconds` the deliverer is told "+15 renown".
8. **Oaths.** A vassal house leader types `/renounce` then `/renounce confirm`. Within a poll that player gets +60 infamy and *Oathbreaker*. `/titles set oathbreaker` shows the title in red.
9. **Chronicle.** If RealmEvents runs a Royal Tournament, the champion gets *Champion of the Lists* within a poll. Otherwise an admin can test with `/renown admin title give <player> champion`.
10. **Rolls.** `/renown top`, `/renown top infamy`, `/renown <name>`.
11. **Admin.** `/renown admin status` shows each feed's last result. `/renown admin grant <player> renown 50 test` changes the total. `/renown admin reset <player>` asks for `confirm`.
12. **Damaged data.** Stop the server, put `{ broken` in `oxide/data/RealmRenown.json`, start it. The console shows an ERROR, `/renown` says the roll is paused, and after a server save the file still reads `{ broken`.

---

## What is UNVERIFIED

- **Chat prefix on clients.** The IL shows the format is applied from `Player.ChatFormat`, a `[Syncable]` property, and that the game itself changes it at runtime. It has not been seen on a live client that a server-side change reaches clients and is used for that player's global chat lines (smoke step 5). If it does not work, set `ChatPrefixEnabled: false`. Titles still appear in `/renown`, `/renown top` and the announcements.
- **Kingslayer timing.** The relative order of `OnEntityDeath` and the throne release on the king's death was not traced. The grace window (`KingslayerGraceSeconds`) covers either order. A monarch killed by environmental damage, with no player `DamageSource`, gives no deed.
- **Rebellion outcome.** This is read from CrownAndConsequences' state up to one tick (30 s) after the window closes. If the crown changes hands in that gap, the result follows the new holder. Participation means *online during the window*. Someone online who never fought still counts. This is deliberate, because the game exposes no "fought in the rebellion" signal.
- **Data-file feeds** depend on the current field names in RealmContracts (`Contracts[].Id/Type/Status/FulfillerId/FulfillerName`, `NextId`), RealmHouses (`Players{id: OathsBroken, TreatiesBroken}`) and RealmChronicle (`[{id,type,title,actors}]`). If those plugins rename fields, the feed reads nothing (no wrong awards) and `/renown admin status` shows it.
- **Name matching** for chronicle deeds can credit the wrong player if someone takes a name another player used before and the first player has since been pruned from the roll. Ambiguous names are skipped.
- Nothing here has been run on a live Reign of Kings server. The behaviour tests run the plugin's real code against **mocks** of the game and Oxide (`plugins/docs/RealmRenown/logic-tests/`). The compile check proves only that the calls match the shipped 2.0.3867 metadata.
