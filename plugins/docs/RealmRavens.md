# RealmRavens: sealed letters, spymasters, spies and rumours

`plugins/RealmRavens.cs` (Oxide 2.0.3867, C# 3). This plugin adds secret diplomacy to Ostreval:

- **Ravens.** `/raven <house|player> <message>` sends a sealed letter. It flies for a while before it lands. A player who is offline finds it waiting on login. Letters can also be sent **unsigned**.
- **Spymasters.** A house leader appoints one spymaster. The spymaster watches one other house and has a chance to secretly copy ravens to or from that house. This has a cooldown and a daily cap.
- **Sworn spies.** A house leader swears up to N members as spies. A spy (or the spymaster) can learn another house's treaties, fealty, raven traffic and secret service, once per cooldown. A spy can be caught.
- **Rumours.** `/rumour <text>` goes into a moderation queue. When an admin approves it, it spreads anonymously in chat and in the Realm Chronicle.
- **Hard limits on harassment.** Rate limits, block lists (anonymous senders included), a report queue, mutes, and an admin audit log that is never kept longer than 7 days.

Lore: the Hearth Charter says that "all of this is written down", and every great house learned that a letter is never as private as its seal. The rookeries below the throne hill carry the realm's secrets, and the realm's spymasters read some of them.

Status: **compile-checked and logic-tested, never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

---

## Commands

Names that contain spaces must be in double quotes (`"Iron Stag"`). The server's chat parser supports quoted arguments. A bare name is tried as a **house first**, then as a player. Use `h:<name>` to mean a house or `p:<name>` to mean a player (for example `/raven p:Varrow hello` writes to a player named Varrow, not the house).

### Letters

| Command | What it does |
|---|---|
| `/raven` or `/raven help` | Shows the help. Admins also see the admin line. |
| `/raven <house\|player> <message>` (or `/raven send ...`) | Sends a signed letter. It lands after a random flight of `TravelSecondsMin`-`TravelSecondsMax` seconds. Writing to a house sends a copy to every member except you (`HouseLettersToAllMembers`), capped at `MaxHouseRecipients`, or only to the leader when that setting is `false`. A player target can be online or anyone the rookery has seen before. |
| `/raven anon <house\|player> <message>` | The same, but unsigned. Recipients see "an unknown hand". It flies `AnonymousExtraSeconds` longer. |
| `/raven inbox` | Your letters, newest first (`InboxListLines`), with unread and intercepted tags. |
| `/raven read <#>` | Reads letter `#` in full and marks it read. |
| `/raven delete <#\|read\|all>` | Deletes one letter, all read letters, or everything. |
| `/raven sent` | Your ravens from the last day: still in the air (with an ETA) or "flown". Senders are **never** told if their letter was intercepted or blocked. |
| `/raven status` | How many ravens you have left this hour and today, how many unsigned, how many are in the air, any mute, and your secret office (spymaster or spy) if you hold one. |

### Blocks and reports

| Command | What it does |
|---|---|
| `/raven block <player>` / `block <house>` | Stops all ravens from that player, or from any member of that house. Blocked letters are **dropped silently**: the sender sees "flown" as usual. Ravens already in the air from a blocked player are cancelled. |
| `/raven block #<letter>` | Blocks whoever sent that letter, **even if it was unsigned**, without telling you who it was. Hidden blocks are listed as "unknown hand(s)", are never matched by name, and never answer "already blocked" (which would unmask the sender). |
| `/raven block anon` | Stops all unsigned ravens. |
| `/raven unblock <player\|house\|anon\|hidden>` | Lifts a block. `hidden` lifts every block you placed through unsigned letters. |
| `/raven blocks` | Your block list. At most `MaxBlocks` entries. |
| `/raven report <#> [reason]` | Sends the letter, with its true sender (even if unsigned), to the admins' report queue. Online admins are told (`NotifyAdminsOfReports`). The reporter is not told who the sender was. |

### Intrigue (needs RealmHouses)

| Command | Who | What it does |
|---|---|---|
| `/raven spymaster <player\|none>` | house leader | Appoints a member as spymaster, or none. The leader may appoint themself. Cooldown `SpymasterChangeCooldownHours` per house (admins skip it). A new spymaster starts watching no one. Only the house is told. |
| `/raven watch <house\|none>` | spymaster | Chooses the one house whose ravens your agents watch. Cooldown `WatchChangeCooldownMinutes`. You cannot watch your own house. |
| `/raven spy swear <player>` | house leader | Offers a member a secret oath as a spy. The member must be online and answer within `SpyOfferExpireSeconds`. There are at most `MaxSpiesPerHouse` spies; the spymaster does not count toward that limit. |
| `/raven spy accept` / `/raven spy decline` | the offered member | Answers the offer. |
| `/raven spy dismiss <player>` | house leader | Releases a spy. |
| `/raven spy list` | leader, spymaster, spies | Shows the house's secret service: spymaster, spies and the watched house. |
| `/raven spy report <house>` | spy or spymaster | Learns about another house (see below). There is a personal cooldown of `SpyCooldownHours`, and a house-wide cooldown of `SpySameTargetCooldownHours` per target. |

### Rumours

| Command | What it does |
|---|---|
| `/rumour <text>` (or `/rumor`) | Puts an anonymous rumour of `MinRumourLength`-`MaxRumourLength` characters into the moderation queue. You may have `MaxPendingRumoursPerPlayer` pending at once, with a cooldown of `RumourCooldownMinutes` between rumours (a refused attempt also uses the cooldown). The whole queue holds at most `MaxPendingRumours`. You are told when your rumour is approved, rejected or expires (after `RumourQueueExpireHours`), even if you were offline at the time. |
| `/rumour list` | The latest approved rumours (`RumourListCount`). |
| `/rumour help` | Usage. |

### Admin commands

All admin commands need the Oxide permission **`realmravens.admin`**:

```
oxide.grant user <name|steamid> realmravens.admin
```

| Command | What it does |
|---|---|
| `/raven admin queue` | Pending rumours, with their submitter. |
| `/raven admin approve <id>` | Approves a rumour. It is broadcast (`BroadcastApprovedRumours`) and logged to the Chronicle as type `rumour` with **no actors** (`ChronicleRumours`). If the Chronicle refuses it (plugin not loaded or the type not registered yet), the admin is told and the rumour still counts as approved locally (`/rumour list`). |
| `/raven admin reject <id> [reason]` | Rejects it. The submitter is told the reason. |
| `/raven admin reports` / `resolve <report id>` | The open report queue, and marking a report handled. |
| `/raven admin audit <player> [hours]` | Audit entries where the player acted or was involved, over the last `hours` (default 24, at most the retention). |
| `/raven admin letter <#>` | Shows a letter's real sender, recipients and text from the audit (if `AuditStoreLetterText`). Reading it is itself audited. |
| `/raven admin mute <player> <hours>` / `unmute <player>` | Bars a player from sending ravens and rumours. |
| `/raven admin purge <player>` | Cancels every raven that player has in the air. |
| `/raven admin save` | Writes the data file now. |

Online admins are told when a rumour enters the queue or a letter is reported. On login, an admin sees how many rumours and reports are waiting.

---

## How it works

### Flight and delivery

- A letter is stored "in the air" with its arrival time. Every 5 seconds, letters that have landed are delivered.
- On delivery, each recipient is checked again against their **current** block list. A block placed while the raven is still flying still stops it.
- A recipient who is online is told at once. A recipient who is offline is told on next login, about 8 seconds after connecting, together with any queued notices (rumour decisions, spy alerts, spymaster appointments).
- Inboxes hold at most `MaxInboxSize` letters. When an inbox is full, the oldest *read* letter is dropped first, then the oldest unread one. Letters older than `InboxRetentionDays` are deleted.

### Interception

When a raven lands, the plugin checks every house *H* that has a spymaster watching house *W*. *H*'s spymaster can intercept the raven if all of these are true:

- The raven was sent by a member of *W*, or was written to *W*. For a letter to a player, that means the recipient was in *W* when it was sent.
- The raven is not from or to *H* itself, and the spymaster is neither its sender nor a recipient.
- The spymaster is **still a member of *H*** (checked live through RealmHouses).
- The spymaster made no interception in the last `InterceptCooldownMinutes`, and fewer than `MaxInterceptsPerDay` in the last 24 hours.
- A random roll is below `InterceptChance`. If several houses qualify, they are tried in a rotating order and at most one intercepts.

The spymaster gets a copy in their inbox, tagged `[intercepted]`, showing the sender and the destination. For an unsigned letter, the copy shows only the sender's house ("sealed with the mark of House X", `RevealAnonHouseOnIntercept`), never the player.

- With `InterceptedLettersStillDelivered` (the default), the raven is read and resealed, and still arrives. With `TamperNoticeChance`, the recipients see "the seal looks as if it has been lifted".
- If that setting is `false`, an intercepted raven never arrives.
- The sender is never told.

The help text warns every player that ravens can be read.

### Spy reports

`/raven spy report <house>` tells the spy:

- **Treaties.** The other house's current treaties (`HasTreaty` against every house).
- **Fealty.** Its liege and its vassals.
- **Raven traffic.** How many ravens its members sent in the last `TrafficWindowHours`, and to which houses (top 5, plus "the unhoused"). The plugin records only *house to house* counts, never names or text.
- **Secret service.** Whether the house has a spymaster, which house it watches, and how many sworn spies it has. It never reveals *who* they are.

Treaties, liege and vassals are also visible through `/house info` in RealmHouses. The spy report gathers them in one place, and adds the traffic and secret-service intelligence, which only this plugin knows.

With `SpyCaughtChance`, the spy is seen. The spy is told, and the target's leader and spymaster get a notice, now or on login: "a spy of House X". The spy's name is included only with `RevealSpyNameWhenCaught`.

### Rumours and the Chronicle

An approved rumour is sent to `RealmChronicle.Call("Log", "rumour", <title>, <text>, [])`. The title is one of a few fixed phrases ("Whispers in the taverns", "A rumour spreads", ...). The rumour text is the detail, and there are **no actors**, so no player name reaches the Chronicle from this plugin. The admin who approves a rumour is responsible for its text naming no one unfairly (see `docs/community/` rules).

**`rumour` is a new Chronicle event type.** **It is now registered** in all three lists, so the fallback below only applies to an older RealmChronicle. It is in `plugins/RealmChronicle.cs` (`KnownTypes`), `chronicle/server.js` (`EVENT_TYPES`) and `chronicle/public/assets/common.js` (`TYPE_META`). With an older RealmChronicle that lacks it, the Chronicle rejects the event with a console warning. Approval still works, the rumour is broadcast and listed in `/rumour list`, and the admin sees a note that the Chronicle did not take it.

### Without RealmHouses

Player-to-player letters, unsigned letters, blocks by player or letter number, reports, rumours and admin tools all work without RealmHouses.

- Writing to a house, blocking a house, spymasters, watching, spies and interception are unavailable. They reply "needs the RealmHouses plugin".
- Without a house, no traffic is recorded.

---

## Privacy, retention and anti-harassment

| Data | Kept | Where |
|---|---|---|
| Letters in the air | until they land (at most `TravelSecondsMax` + `AnonymousExtraSeconds`) | `oxide/data/RealmRavens.json` |
| Inbox letters | `InboxRetentionDays` (default 7, max 30), at most `MaxInboxSize` per player | same |
| Audit log (who sent what to whom, with letter text if `AuditStoreLetterText`; interceptions; blocks; spy actions; moderation; admin reads, mutes and purges) | **`AuditRetentionDays`, clamped to 1-7 days**, and at most `MaxAuditEntries` | same |
| Reports (with letter text and true sender) | same as the audit (7 days max) | same |
| House-to-house traffic counts (no names, no text) | `max(TrafficWindowHours, 24)` hours | same |
| Rejected or expired rumours with submitter | 7 days max | same |
| Approved rumours | the newest 50. The submitter's id and name are erased after the audit window | same |
| Send records (for rate limits and `/raven sent`) | 1 day | same |
| Player rows with no inbox, blocks, mute or notices | deleted after 60 days unseen | same |

The plugin writes nothing with `LogToFile`, so no log file outlives the retention.

Limits (defaults):

- 30 s between ravens, 6 per hour, 20 per day.
- 3 per day to the same target.
- 2 unsigned per day.
- 5 in the air at once.
- At most 3 unread letters from one sender in any inbox (letters in the air count toward this).
- Letters of 2-300 characters.
- Unsigned letters and rumours are refused for the first `NewPlayerGraceMinutes` (30) after the rookery first sees a player.
- Colour tags and control characters are neutralised. Player text is sent only through the single-string chat overloads, so braces are safe.

---

## Config (`oxide/config/RealmRavens.json`)

The plugin writes defaults on first load. Out-of-range values are clamped on load (for example, the chances are clamped to 0-1, and the audit retention to 1-7 days).

| Key | Default | Meaning |
|---|---|---|
| `MaxLetterLength` / `MinLetterLength` | 300 / 2 | Letter length bounds (characters, after cleaning). |
| `TravelSecondsMin` / `TravelSecondsMax` | 60 / 180 | Random flight time. |
| `AnonymousExtraSeconds` | 60 | Extra flight for unsigned letters. |
| `AllowAnonymous` | true | Unsigned letters on or off. |
| `HouseLettersToAllMembers` / `MaxHouseRecipients` | true / 30 | Who receives a letter to a house. |
| `SendCooldownSeconds`, `MaxLettersPerHour`, `MaxLettersPerDay`, `MaxLettersToSameTargetPerDay`, `MaxAnonymousPerDay`, `MaxInFlightPerSender`, `MaxUnreadFromSameSender` | 30, 6, 20, 3, 2, 5, 3 | Rate limits (see above). |
| `MaxInboxSize`, `InboxRetentionDays`, `InboxListLines` | 30, 7, 8 | Inbox. |
| `MaxBlocks` | 50 | Block list size (players plus houses). |
| `NewPlayerGraceMinutes` | 30 | New-player wait before unsigned letters and rumours. |
| `InterceptionEnabled`, `InterceptChance`, `InterceptCooldownMinutes`, `MaxInterceptsPerDay` | true, 0.2, 30, 6 | Interception. |
| `InterceptedLettersStillDelivered`, `TamperNoticeChance`, `RevealAnonHouseOnIntercept` | true, 0.1, true | What interception does. |
| `SpymasterChangeCooldownHours`, `WatchChangeCooldownMinutes` | 24, 60 | Spymaster churn. |
| `SpiesEnabled`, `MaxSpiesPerHouse`, `SpyCooldownHours`, `SpySameTargetCooldownHours`, `SpyCaughtChance`, `RevealSpyNameWhenCaught`, `SpyOfferExpireSeconds`, `TrafficWindowHours` | true, 2, 12, 24, 0.15, false, 300, 24 | Spies. |
| `RumoursEnabled`, `MinRumourLength`, `MaxRumourLength`, `RumourCooldownMinutes`, `MaxPendingRumoursPerPlayer`, `MaxPendingRumours`, `RumourQueueExpireHours` | true, 10, 200, 120, 1, 25, 48 | Rumour queue. |
| `BroadcastApprovedRumours`, `ChronicleRumours`, `RumourListCount` | true, true, 5 | Where approved rumours go. |
| `RumourBlockedWords` | `[]` | Case-insensitive substrings that make a rumour refused before it reaches the queue. The attempt is audited. |
| `AuditRetentionDays`, `MaxAuditEntries`, `AuditStoreLetterText`, `AdminListLines`, `NotifyAdminsOfReports` | 7, 3000, true, 15, true | Audit and admin views. |

## Data file safety

`oxide/data/RealmRavens.json` is read with `DataFileSystem`.

- If the file exists but cannot be parsed, or parses to nothing, the plugin logs an error and every command answers "The rookery is closed". The file is **never written** in that state, so a damaged file is not replaced by an empty one. Fix or remove the file, then run `oxide.reload RealmRavens`.
- Broken rows (nulls, missing fields) are dropped on load, and the id counters continue past every stored id.

## Cross-plugin use

The plugin reads from RealmHouses with `Call`: `GetHouse`, `GetMembers`, `GetHouseLeader`, `GetHouseSummaries`, `GetLiege`, `GetVassals` and `HasTreaty`. It writes to RealmChronicle with `Call("Log", ...)`, for approved rumours only. It exposes no API of its own.

---

## Smoke test (owner's PC, after `Deploy-Plugins.ps1`)

You need two Steam accounts (A and B) for most steps. A third account (C) is needed for interception. Grant A admin first: `oxide.grant user A realmravens.admin`. To make the tests quick, set `TravelSecondsMin` 5, `TravelSecondsMax` 10, `AnonymousExtraSeconds` 0, `InterceptChance` 1, `InterceptCooldownMinutes` 0, `SpyCaughtChance` 1 and `NewPlayerGraceMinutes` 0 in the config, then `oxide.reload RealmRavens`. Put the defaults back afterwards.

| # | Step | Pass if |
|---|---|---|
| R1 | Server console: `oxide.plugins` | RealmRavens is listed with no compile error. `oxide/config/RealmRavens.json` and (after the first command) `oxide/data/RealmRavens.json` exist. |
| R2 | A: `/raven` | The help lines show, plus the admin line for A. |
| R3 | A: `/raven B hello there` | "Your raven takes wing toward B...". After 5-10 s, B sees "A raven lands bearing a letter from A". |
| R4 | B: `/raven inbox`, then `/raven read <#>` | The letter is listed `[unread]`, then shown in full. The inbox no longer tags it. |
| R5 | B disconnects. A: `/raven B second`. Wait 15 s, then B reconnects. | About 8 s after B joins, B sees "1 unread letter(s) wait for you". |
| R6 | A: send a letter to B six more times quickly | Refused with the cooldown, then the same-target or roost limit, with a wait time. |
| R7 | A: `/raven anon B who am I` | B sees "an unknown hand". `/raven admin letter <#>` on A shows A as the sender. |
| R8 | B: `/raven block #<that #>`, then `/raven blocks` | It shows "1 unknown hand(s)". A's next raven to B never arrives, and A's `/raven sent` still says "flown". `/raven admin audit B` shows `letter_blocked`. |
| R9 | B: `/raven unblock hidden`. B: `/raven report <#> rude` | A (admin) sees the report notice. `/raven admin reports` lists it, and `/raven admin resolve <id>` clears it. |
| R10 | With RealmHouses: A founds House Varrow, C founds House Halloran (`/house found ...`). B joins neither. C: `/raven spymaster C`, then `/raven watch Varrow`. | Both replies confirm. |
| R11 | A (Varrow): `/raven B intercepted?` | C sees "Your agents copied a raven from A of House Varrow to B". `/raven read <#>` on C shows `[intercepted]` and the text. B still receives it. |
| R12 | C: `/raven spy report Varrow` | The report shows treaties, fealty, traffic ("Ravens sent in the last 24h: n") and the secret service. Because the chance is 1, A (Varrow's leader) is told that a spy of House Halloran was caught. A second report at once is refused with the cooldown. |
| R13 | B: `/rumour The miller of the east vale waters his flour` | B is told it is queued, and A is told about the queue. A: `/raven admin queue`, then `/raven admin approve <id>`. Everyone sees "Whispers in the taverns: ...". B is told it spread. `/rumour list` shows it. |
| R14 | Check the Chronicle after R13 | **If `rumour` is registered:** the event appears in `RealmChronicle.json` with `actors: []`. **If not:** the server console shows "Rejected chronicle event with unknown type 'rumour'" and A saw the "chronicle did not take it" note. |
| R15 | A: `/raven admin mute B 1`. B: `/raven A hi` and `/rumour testing a rumour` | Both are refused with "will not serve you". `/raven admin unmute B` lifts the mute. |
| R16 | Stop the server, put `{ broken` into `oxide/data/RealmRavens.json`, and start the server | The console shows "Could not read ... The rookery is closed". `/raven` answers "closed". The file is unchanged after `save` and shutdown. Restore it afterwards. |
| R17 | `oxide.reload RealmRavens` while a raven is in the air | It still lands after the reload (the in-flight list is saved). |

---

## Checks run here

- `tools/plugin-compile-check/check.sh`: **OK, 0 errors, 0 warnings**. All `plugins/*.cs`, including RealmRavens, compile together at C# 3 against the shipped Oxide 2.0.3867 and patched game DLL metadata.
- `plugins/docs/RealmRavens/logic-tests/run.sh`: **95 passed, 0 failed**. It compiles the real plugin source together with `RulesTests.cs` and runs it on .NET. The tests cover:
  - text cleaning and travel time
  - every rate limit, mute, and pruning of send records
  - player, house, unsigned and hidden blocks
  - the inbox cap order and unread counting
  - interception: from and to the watched house, own house, recipient spymaster, spymaster who left the house, cooldown, daily cap, the off switch, and two competing houses
  - spy cooldowns and traffic counting
  - the rumour cooldown, queue cap, new-player wait and blocked words
  - name matching and config clamps (including the 7-day audit ceiling)
  - retention of audit, reports, inboxes, rumours, idle players and intel timers
  - repair of damaged rows
  - a JSON round trip with Oxide's Newtonsoft, and empty or truncated files

## What is UNVERIFIED

Nothing here has run on a live Reign of Kings server.

- **Command flows that touch game types**: sending, delivery, notices, the login greeting and admin commands. They compile against the real metadata, but they have not been executed, because `Player` and `Server` cannot be built off-server. Smoke test R2-R17 covers them.
- **Login timing.** The 8-second delay before the login greeting is a guess at when the client is ready to show chat. If players miss the greeting, raise it.
- **`OnPlayerDisconnected`** is used only to stamp the last-seen time. That it fires reliably is from Oxide source, not tested here.
- **Chat output.** That `PlayerExtensions.SendMessage(string)` / `SendError(string)` and `Server.BroadcastMessage(string)` show the text as-is, with `[RRGGBB]` colours (docs/oxide-rok-api.md 4.2). Player text has `[` and `]` replaced, so it cannot inject colours either way.
- **Cross-plugin return types.** That `RealmHouses.Call("GetMembers"/"GetVassals")` arrives as `List<string>` and `GetHouseSummaries` as `List<Dictionary<string, object>>` through `Plugin.Call` (it should: same process, same types).
- **The `rumour` Chronicle type** is registered (integration pass); a server still running an older RealmChronicle.cs rejects it until that plugin is updated.
- **Name matching** uses `Player.Name`. Players who rename are matched under their newest name once they reconnect.
- **Balance.** All the chances and cooldowns are first guesses and need tuning with real players.
