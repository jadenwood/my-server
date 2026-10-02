# RealmWarden: anti-grief and fair play

`plugins/RealmWarden.cs` (Oxide 2.0.3867, C# 3). The Warden watches for the most common ways a few players spoil a server for everyone else. It **flags** the problem, keeps **evidence**, and tells the **admins**. Its only actions are soft ones that you can configure. **It never bans anyone.**

| Rule | What the Warden does |
|---|---|
| **New-player protection** | For a new player's first 60 minutes of play (and never longer than 48 hours after they first join), other players cannot wound or bind them. A protected player who attacks someone, binds someone, damages another group's base or takes the throne loses the protection at once, so it cannot be used to grief. |
| **Combat-log detection** | A player-on-player hit puts both players "in combat" for 30 s. Anyone who leaves the game during that time is flagged. The Warden records evidence, queues an admin alert and tells the opponent. Players are warned when they enter combat, and told that their sleeping body stays in the world and can still be killed. |
| **Chat flood and spam** | The limits are: messages per window, repeats of the same text, message length, and (optional) capitals. Each blocked message is a strike. Three strikes in 2 minutes bring a timed Warden mute. The mute doubles on each repeat, up to a cap. Repeated mutes alert the admins. |
| **Name filter** | Names are checked when a player joins, against a list you configure. Before matching, the Warden lowers the case, maps leet (`4dm1n` reads as `admin`), and strips punctuation and colour tags. The default action is flag + alert + ask the player to rename. Kicking is opt-in. |
| **Raid hours** (opt-in) | Outside the configured windows, damage to blocks inside a crest zone is stopped. The crest's own group may still break its own blocks. A declared rebellion window (CrownAndConsequences) opens raiding. |
| **Admin queue and evidence** | Every flag goes to an alert queue that admins page through and acknowledge. Each one also goes to an evidence log, which is capped and also written to `oxide/logs/RealmWarden/`. Players can file `/warden report`. |

Lore: under the Hearth Charter the **Warden of the Roads** keeps the peace between the great houses. The Warden judges no one; the lords and the crown do that. The Warden only writes down what happened and makes sure the right people hear of it.

Nothing the Warden records goes to the public **Chronicle**. Moderation records are private to admins. **No new Chronicle event types are needed.**

Status: **compile-checked with 0 errors against the real 2.0.3867 DLLs, and behaviour-tested against mocks (140 checks pass). Never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

---

## Commands

Everything is under `/warden`. Quote names that contain spaces: `/warden report "Old Tom" broke my door`. The server's chat parser supports double quotes.

### Players

| Command | What it does |
|---|---|
| `/warden` or `/warden help` | Shows the help. Admins also see the admin commands. |
| `/warden status` | Shows your protection time left, any mute, whether you are in combat (and for how long), and whether raid hours are open now or when the next window opens. |
| `/warden rules` | Shows the fair-play rules with the server's real numbers, plus the raid-hour status. |
| `/warden report <player> <reason>` | Sends a report to the admins. The player can be online, or offline and known to the Warden. Partial names work if they are unique. The reason must be 3 to 200 characters. Limits: one report per 120 s, 10 per day, and the same player once per 60 min. |
| `/warden protection off confirm` | Gives up your new-player protection early. Without `confirm` it only explains. It cannot be undone. It can be turned off with `AllowOptOut`. |

### Admins (`realmwarden.admin`)

Grant the permission in the server console: `oxide.grant user <name|id> realmwarden.admin`.

| Command | What it does |
|---|---|
| `/warden alerts [all] [page]` | Lists unread alerts, newest first, 8 per page. With `all`, acknowledged alerts are listed too, marked `[ack <admin>]`. |
| `/warden ack <id\|all>` | Acknowledges one alert (`ack 12` or `ack #12`) or all of them. |
| `/warden player <player>` | Shows first and last seen, playtime, every flag counter, protection, mute, combat state, and the 3 newest evidence lines about the player. |
| `/warden evidence [player] [page]` | Pages through the evidence log, for everyone or for one player (as actor or as the other party). |
| `/warden protect <player> <minutes\|off>` | Grants protection for 1 to 10080 minutes, overriding the normal window. `off` removes it for good. |
| `/warden mute <player> <minutes>` | Mutes a player for 1 to 1440 minutes. |
| `/warden unmute <player>` | Lifts a mute. |
| `/warden clear <player>` | Resets the player's flag counters and mute history. **The evidence log is kept.** |
| `/warden raid` | Shows the raid-hour settings and whether raiding is open now. |

Every admin action from `protect`, `mute`, `unmute` and `clear` is written to the evidence log as `admin_action`, with the admin's name.

On join, admins see the number of unread alerts. While they are online, new alerts are sent to their chat, at most 6 a minute. Alerts over that limit are summarised as "+N more alerts were queued".

---

## How each rule works (and what it rests on)

Tags are as in `docs/oxide-rok-api.md`. **[DEC]** means the claim was read in the decompiled shipped patched `Assembly-CSharp.dll`. **[IL]** means it was read in that DLL's IL bodies.

### New-player protection

- **Who is protected.** The Warden records a player the first time it sees them. They are protected while both of these hold:
  - online playtime is under `PlaytimeMinutes`. Playtime is added every 15 s, and a stalled timer can add at most 60 s at once.
  - less than `MaxWallClockHours` have passed since they first joined.

  An admin grant (`/warden protect`) overrides both.
- **What it blocks:**
  - PvP damage, through `OnEntityHealthChange(EntityDamageEvent)` [OPJ L162, RB 1]. The victim is `evt.Entity.Owner` and the attacker `evt.Damage.DamageSource.Owner` [ASM]. The hit is blocked with `evt.Cancel()`, `Damage.Amount = 0` and `return true` [USE `NoFriendlyFire.cs:116-117`]. The same pattern is used by RealmLaws (peace zones) and RealmEvents (truce).
  - Binding with rope, chain or cage, through `OnPlayerCapture` [OPJ L711, RB 1], with `evt.Cancel()` and `return true`. Controlled by `BlockCaptureOfProtected`.
- **What ends it early:**
  - attacking or binding a player who is not protected (`AttackingEndsProtection`)
  - damaging blocks in another group's crest zone (`StructureDamageEndsProtection`, through `OnCubeTakeDamage`, below)
  - completing a throne capture (`OnThroneCaptured`, State `Completed`) (`ThroneEndsProtection`)

  A forfeit is written to the evidence log as `protection`.
- **Rebellions.** With `SuspendDuringRebellion: true`, protection does not apply while `CrownAndConsequences.IsRebellionActive` returns true.
- **Existing servers.** The first time the Warden runs, it has never seen anyone, so every player gets protection once on their next join. Veterans lose it the moment they attack anyone, or they can type `/warden protection off confirm`. An admin can also use `/warden protect <player> off`. To skip this phase entirely, start with `"NewPlayerProtection": {"Enabled": false}` for a day, then turn it on. Players seen while it was off already have playtime recorded.

### Combat-log detection

- Every player-on-player hit that is not cancelled tags the victim as in combat. The attacker is tagged too if `TagAttacker` is on. The tag lasts `WindowSeconds`. A hit that was already cancelled by another plugin (a peace zone, a truce, or this plugin's protection) does not count.
- If a tagged player disconnects inside the window (`OnPlayerDisconnected`, core-dispatched [SRC]), the Warden does all of the following:
  - adds a `combat_log` flag
  - writes evidence naming the opponent and the player's position
  - queues an alert, at most one per player per `AlertCooldownMinutes`. The alert shows the running total of combat logs.
  - tells the opponent, if `TellOpponent` is on
- **Not flagged:**
  - a player who **died** first (`OnEntityDeath` [OPJ L188] clears the tag)
  - disconnects after `OnServerShutdown` [OPJ L946], such as a restart
  - a disconnect caused by the Warden's own name kick
- **Sleeper note.** The game leaves a sleeping body when a player logs out ([DEC] `CreateSleeperOnLogout.OnPlayerLeave` → `ISleeper.Sleep`). That body can die while the player is away: the game has the message "Your character died while you were away" ([DEC] `CreateSleeperOnLogout.Start`). The Warden does not change sleepers. With `SleeperNote` on, it tells players about the sleeper when they enter combat and adds the same note to the admin alert.

### Chat flood and spam

- The Warden uses `OnPlayerChat(PlayerMessageEvent)` [SRC `ReignOfKingsHooks.cs:62-83`]. When it returns non-null, the core cancels the message.
- [DEC] `PlayerListener.OnPlayerMessage` calls the internal hook in an `if / else if` (guild chat **or** other chat), so each message is counted once.
- The checks run in this order:
  1. length (`MaxLength`)
  2. banned words (`FilterWords`, off by default; it uses the name list)
  3. capitals (`CapsMaxPercent`, off by default)
  4. rate (`MaxMessages` per `WindowSeconds`)
  5. repeats: `MaxDuplicates` copies of the same text within `DuplicateWindowSeconds`. Texts are compared lower-cased, letters and digits only, so `buy gold!` equals `BUY  GOLD`.
- A blocked message is a **strike**. `StrikesToMute` strikes within `StrikeWindowSeconds` give a mute of `MuteSeconds`. The next mute within `MuteLevelResetHours` doubles it, up to `MaxMuteSeconds`. Each mute is a `chat_flood` flag and an evidence line (with up to 80 characters of the message). From the `AlertAfterMutes`-th mute in 24 h, an alert is raised.
- Mutes are stored in the data file, so they **survive reconnects**.
- Admins are exempt while `AdminsExempt` is on.
- These mutes belong to the Warden. They are separate from the game's own `ChatMuteManager`, so unloading the Warden lifts them.

### Name filter

- The check runs on `OnPlayerConnected` [SRC]. Each entry of `Words` is normalised:
  - lower case
  - leet mapped to letters: `0→o 1→i !→i |→i 3→e 4→a @→a 5→s $→s 7→t +→t 8→b 9→g`
  - letters only
- An entry such as `grief` matches **anywhere** in the name, so `xXGr13fLordXx` matches.
- An entry with a leading `=` such as `=admin` matches a **whole word** only. Words are split at punctuation, spaces and camel case, so `TheAdmin` and `Ad.Min` match but `Badminton Bob` does not.
- The default list targets **staff impersonation**: `=admin =administrator =moderator =server =owner =gm`. **Add the slurs and terms your community will not accept.** None are shipped in the repo.
- With `Action: "flag"` (the default), the Warden adds an `offensive_name` flag, writes evidence, raises an alert and asks the player to rename. Repeats are limited to once per player per hour.
- With `Action: "kick"`, the player is also kicked after `KickDelaySeconds` with `Server.Kick(Player, string)` [ASM; DEC `Server.Kick`]. **There is no ban option.**
- Admins and the Steam IDs in `ExemptIds` are skipped.

### Raid hours (opt-in, `"RaidHours": {"Enabled": true}`)

- **The hook.** `OnCubeTakeDamage(CubeDamageEvent)` [OPJ L292] is RB 0, and **the game does not read `Cancelled` there**.
  - [IL] `CubeListener.OnCubeDamage` calls the hook at `IL_0000`, then runs `BlockHealth.CurrentHealth -= evt.Damage.Amount` (`IL_005a`–`IL_0074`). It raises `CubeDestroyEvent` only when that drops the block to dead.
  - So the Warden blocks a hit by setting **`evt.Damage.Amount = 0`**, which is the value the game's listener reads, and also calls `evt.Cancel()`.
  - For a cancelled event, [DEC] `EventManager.ServerSendEvent` sends the event back to its sender only, not to every nearby client.
- **Left alone:**
  - hits that are already cancelled. Salvage hits arrive already cancelled: [DEC] `SalvageSupplier.OnCubeDamage`, subscribed `VeryEarly`, cancels `DamageType.Salvage`.
  - `Salvage` and `Healing` types
  - non-positive amounts: repairs are negative, see [DEC] `RepairOnHit.GetDamageAmount`
- **Whose zone.** The Warden uses the game's own siege test ([DEC] `CrestSupplier.OnCubeDamage`): it compares `CrestScheme.CurrentCrestGroup(grid.LocalToWorldCoordinate(pos))` with `SocialAPI.GetGroupId(attacker)`.
  - Blocks outside every crest zone are not protected unless `ProtectUnclaimed` is on.
  - The crest's own group may break its own blocks (`OwnersMayDamageOwn`).
  - Admins are exempt.
- **The attacker** is `Damage.DamageSource.Owner`, or else the event's `Sender`. If neither is a real player, `BlockUnknownAttacker` decides.
- **Windows.** Each window is `{Days, Start, End}` in **UTC + `UtcOffsetMinutes`**, for example `-300` for UTC−5.
  - Days are `Mon`…`Sun` or `*`.
  - When `End` is not after `Start`, the window crosses midnight and belongs to the day it starts on. `Start == End` means 24 h.
  - Start is inclusive and end is exclusive.
  - Bad entries are skipped with a console warning.
- **Rebellions.** With `AllowDuringRebellion`, raiding is open whenever `CrownAndConsequences.IsRebellionActive` returns true.
- **`Block: false`** only records and alerts; it does not block.
- **Records.** The attacker is told at most every `NotifySeconds`. Evidence is written at most every `EvidenceCooldownMinutes` per attacker. `AlertAfterBlockedHits` hits within 10 minutes add a `raid_hours` flag and an alert, at most once per 30 minutes per attacker.

---

## Config (`oxide/config/RealmWarden.json`)

Written with defaults on first load. Every number is clamped to a safe range on load, and a missing or `null` section is restored with defaults.

| Section.key | Default | Range / meaning |
|---|---|---|
| `General.AdminsExempt` | `true` | Admins skip chat limits, the name filter and raid hours. |
| `General.SaveIntervalSeconds` | `120` | 30–3600. The file is also saved on world save, shutdown and unload. |
| `NewPlayerProtection.Enabled` | `true` | |
| `NewPlayerProtection.PlaytimeMinutes` | `60` | 0–1440 minutes of online play |
| `NewPlayerProtection.MaxWallClockHours` | `48` | 1–720 |
| `NewPlayerProtection.AttackingEndsProtection` | `true` | Hitting or binding an unprotected player |
| `NewPlayerProtection.StructureDamageEndsProtection` | `true` | Hitting blocks in another group's crest zone |
| `NewPlayerProtection.ThroneEndsProtection` | `true` | |
| `NewPlayerProtection.BlockCaptureOfProtected` | `true` | |
| `NewPlayerProtection.SuspendDuringRebellion` | `false` | |
| `NewPlayerProtection.AllowOptOut` | `true` | |
| `CombatLog.Enabled` | `true` | |
| `CombatLog.WindowSeconds` | `30` | 5–300 |
| `CombatLog.TagAttacker` | `true` | |
| `CombatLog.WarnOnCombatStart` | `true` | At most once a minute per player |
| `CombatLog.SleeperNote` | `true` | |
| `CombatLog.TellOpponent` | `true` | |
| `CombatLog.AlertCooldownMinutes` | `10` | 0–1440, per player. Flags are always counted. |
| `Chat.Enabled` | `true` | |
| `Chat.MaxMessages` / `Chat.WindowSeconds` | `5` / `8` | 1–100 / 1–300 |
| `Chat.MaxDuplicates` / `Chat.DuplicateWindowSeconds` | `3` / `30` | 1–100 / 1–3600 |
| `Chat.MaxLength` | `300` | 10–2000 |
| `Chat.CapsMaxPercent` / `Chat.CapsMinLetters` | `0` / `12` | 0 = off |
| `Chat.FilterWords` | `false` | Also block chat that contains a `Names.Words` entry |
| `Chat.StrikesToMute` / `Chat.StrikeWindowSeconds` | `3` / `120` | |
| `Chat.MuteSeconds` / `Chat.MaxMuteSeconds` | `60` / `900` | The mute doubles each repeat, up to the max |
| `Chat.MuteLevelResetHours` | `24` | |
| `Chat.AlertAfterMutes` | `2` | Mutes within 24 h before an alert |
| `Names.Enabled` | `true` | |
| `Names.Words` | `["=admin","=administrator","=moderator","=server","=owner","=gm"]` | At most 500 entries. `=` means whole word. Entries shorter than 2 letters after normalising are ignored. |
| `Names.NormalizeLeet` | `true` | |
| `Names.Action` | `"flag"` | `"flag"` or `"kick"`. Anything else falls back to `"flag"`. |
| `Names.KickDelaySeconds` | `5` | 1–60 |
| `Names.WarnPlayer` | `true` | |
| `Names.ExemptIds` | `[]` | SteamID64 strings |
| `RaidHours.Enabled` | `false` | Opt in |
| `RaidHours.Block` | `true` | `false` = record and alert only |
| `RaidHours.UtcOffsetMinutes` | `0` | −720 to 840 |
| `RaidHours.Windows` | Wed + Sat 18:00–23:00, Sun 14:00–20:00 | At most 50 |
| `RaidHours.AllowDuringRebellion` | `true` | |
| `RaidHours.ProtectUnclaimed` | `false` | |
| `RaidHours.OwnersMayDamageOwn` | `true` | |
| `RaidHours.BlockUnknownAttacker` | `true` | |
| `RaidHours.NotifySeconds` | `15` | |
| `RaidHours.EvidenceCooldownMinutes` | `5` | |
| `RaidHours.AlertAfterBlockedHits` | `30` | Within 10 minutes |
| `Alerts.MaxStored` | `500` | 10–5000. Acknowledged alerts are dropped first. |
| `Alerts.MaxEvidence` | `2000` | 10–20000. The oldest entries are dropped. The log files keep everything. |
| `Alerts.ChatToOnlineAdmins` | `true` | |
| `Alerts.MaxChatAlertsPerMinute` | `6` | |
| `Alerts.RemindOnAdminJoin` | `true` | |
| `Alerts.LogToFile` | `true` | `oxide/logs/RealmWarden/realmwarden_alerts-<date>.txt` and `..._evidence-<date>.txt` |
| `Reports.Enabled` | `true` | |
| `Reports.CooldownSeconds` / `Reports.MaxPerDay` | `120` / `10` | |
| `Reports.SameTargetCooldownMinutes` | `60` | |
| `Reports.MinReasonLength` / `Reports.MaxReasonLength` | `3` / `200` | |

Fixed caps in code:
- the player table is capped at 20000; the longest-unseen players with no flags are dropped first
- other plugins may raise at most 30 alerts per hour through `RaiseWardenAlert`
- names in records are cut to 64 characters and details to 300
- colour tags `[RRGGBB]` are neutralised in every displayed name and detail

---

## Data, logs and safety

- **Data:** `oxide/data/RealmWarden.json`, through `Interface.Oxide.DataFileSystem` [SRC]. It holds players (first and last seen, playtime, protection, flags, mutes, report limits), alerts and evidence. Timestamps are Unix seconds (UTC).
- **Corruption-safe load.** If the file exists but cannot be parsed (or is `null`), the Warden logs an error. It keeps running from memory: chat limits, name filter, raid hours and alerts all still work. Two things change:
  - new-player protection is **off**, because it would otherwise give protection to everyone again
  - the Warden **never writes the file**, so the damaged record is not overwritten

  Admins see a warning on join and in `/warden help`. Fix or remove the file, then `oxide.reload RealmWarden`.
- **Logs:** with `Alerts.LogToFile`, alerts and evidence are also appended to `oxide/logs/RealmWarden/` through `LogToFile` [SRC]. This is the permanent audit trail, since the in-file evidence list is capped.
- **Privacy:** evidence holds positions and Steam IDs. It is visible only to `realmwarden.admin` and in the server's own files. Nothing is sent to the Chronicle, a web hook or any outside service.

## Cross-plugin API

These methods are non-public on purpose: Oxide's `Call` only finds NonPublic|Instance methods. Reach them with `[PluginReference] Plugin RealmWarden;` and `RealmWarden.Call(...)`. If the Warden is not loaded, the call returns `null`.

| Call | Returns |
|---|---|
| `Call("IsNewPlayerProtected", ulong playerId)` | `bool` |
| `Call("IsInCombat", ulong playerId)` | `bool`, true within `CombatLog.WindowSeconds` of the last hit |
| `Call("GetWardenFlagCount", ulong playerId)` | `int`, the total of all flag counters |
| `Call("IsRaidHourNow")` | `bool`, true when raid hours are off or a window or rebellion is open |
| `Call("RaiseWardenAlert", string kind, ulong playerId, string detail)` | `int`, the alert id, or 0 when the hourly cap refused it. The kind is stored as `ext:<kind>`. |

Uses its own reference: `CrownAndConsequences.Call("IsRebellionActive")`, which returns false when that plugin is not loaded.

## Interplay with other Realm plugins

- **RealmLaws** (peace zones) and **RealmEvents** (truce) also cancel PvP damage in `OnEntityHealthChange`. The Warden skips events that are already cancelled, so a blocked hit never starts combat. Oxide's hook order between plugins is **not** defined. If the Warden runs first and another plugin cancels afterwards, that hit still tags combat. This is harmless: at worst it flags a logout 30 s after a blocked hit.
- **CrownAndConsequences** also handles `OnPlayerCapture`. If it runs before the Warden, it may record a capture of a protected player that the Warden then cancels. The capture is still blocked. This is the same ordering caveat RealmLaws documents.
- **RealmHouses' house names** are not the game's crest groups. Raid-hour ownership uses the game's own crest and group ids, exactly as the game's siege code does.

---

## Smoke test (on the owner's PC, after `docs/smoke-test.md` stage C works)

Use two Steam accounts (A = admin, B = player) on the local test server. Record pass or fail for each step.

1. **Load.** Run `Deploy-Plugins.ps1`. The console shows `Loaded plugin RealmWarden`, and `oxide/config/RealmWarden.json` and `oxide/data/RealmWarden.json` exist. Grant A: `oxide.grant user <A> realmwarden.admin`.
2. **Help.** B types `/warden`: three help lines. A types `/warden`: also the two admin lines.
3. **Protection.** B is new, so B sees the welcome notice. A hits B with a weapon: **B takes no damage** and A is told "cannot be harmed". A tries to rope B: the bind fails.
4. **Forfeit.** B hits A. B is told protection has ended. A then hits B: B takes damage. `/warden evidence` shows a `protection` line.
5. **Combat log.** A and B (unprotected) trade a hit. B closes the game within 30 s. A is told "left the game in the middle of the fight", and A sees an alert in chat. `/warden alerts` lists `combat_log`, and `/warden player B` shows `combat_log x1`. B rejoins. Then repeat, but wait 40 s before closing: there is no new flag.
6. **Chat.** B sends 6 messages quickly: the 6th is refused. B keeps spamming: after 3 strikes, B is muted for 1 min, and both `/warden status` (B) and `/warden player B` (A) show it. B reconnects: still muted. A runs `/warden unmute B`.
7. **Name.** Rename B's Steam profile to `TheAdmin` and rejoin. B is asked to rename, and A gets an `offensive_name` alert. Set `"Action": "kick"`, reload, and rejoin: B is kicked after 5 s and can rejoin (not banned).
8. **Raid hours.** Set `"RaidHours": {"Enabled": true, "Windows": [{"Days": ["*"], "Start": "00:00", "End": "00:01"}]}` (closed nearly all day) and reload. B places a crest and a few blocks. A (another group, admin rights temporarily revoked) hits B's blocks: **the blocks lose no health and do not break**, and A sees the raid-hours message. B hits B's own blocks: they take damage. Set `"Block": false`: A's hits damage again, and evidence is still recorded.
9. **Report.** B: `/warden report A testing reports`. A sees the alert. B tries again at once and gets the cooldown message.
10. **Corruption.** Stop the server, write `{ broken` into `oxide/data/RealmWarden.json`, then start. The console shows the error, and admins see the warning. After a save or shutdown the file is still `{ broken`.

---

## What is UNVERIFIED

Nothing here has run on a real server. In particular:

- **Raid-hour blocking.** The IL proves the **server's** block health ignores a zeroed `Damage.Amount`. Still unverified:
  - whether the attacker's own client briefly shows damage (the cancelled event goes back to the sender only)
  - whether another server path also damages blocks (none was found among the `CubeDamageEvent` subscribers)
  - whether the game's siege timer (`CrestSupplier.OnCubeDamage`, `Siege` damage) has already started before the Warden's zeroing runs. Subscription order inside the `Normal` slot is not defined.
- **The attacker of a block hit.** It is unverified whether `Damage.DamageSource.Owner` (or `Sender`) is the real player for every weapon, including siege engines, fire and arrows. Hits with no known player inside a crest zone are blocked by default (`BlockUnknownAttacker`). If that blocks something legitimate, set it to `false`.
- **Crest ownership.** Unverified: that `SocialAPI.GetGroupId` matches `CrestScheme.CurrentCrestGroup` for a player's own crest, including solo players with no guild. The comparison is copied from the game's own siege code.
- **New-player protection hooks.** That `Cancel()` + `Damage.Amount = 0` + `return true` fully stops PvP damage and every bind type (rope, chain, cage), and that `OnThroneCaptured` fires with `State == Completed` on a real capture. These are the same open items as RealmLaws, CrownAndConsequences and `docs/oxide-rok-api.md` §10.
- **Combat log.**
  - Unverified: that `OnPlayerDisconnected` fires for crashes and timeouts as well as clean quits.
  - Unverified: that the server's own restart path calls `OnServerShutdown` before players are dropped.
  - Players who time out because of real network trouble are flagged like anyone else. Admins should read the evidence, which is why there are no automatic penalties.
- **Chat.**
  - Unverified: that the cancelled message is really not shown to others. The core cancels it; the sender may still see their own line.
  - Unverified: that `evt.Message` is the raw text.
  - The `if / else if` single-call reading comes from decompiled code, not a live test.
- **Name kick.** Unverified: `Server.Kick` behaviour and the message the client sees.
- **Playtime** counts the time a player is connected as seen by this plugin. It is lost across a crash between saves, at most `SaveIntervalSeconds`.
- **`LogToFile`** paths and file names follow Oxide's source; they were not observed on disk.

## Tests

- **Compile:** `tools/plugin-compile-check/check.sh` compiles all plugins with Roslyn `-langversion:3` against the shipped Oxide 2.0.3867 and patched game DLLs. 0 errors, and no warnings from this file.
- **Behaviour:** `plugins/docs/RealmWarden/logic-tests/run.sh` compiles `plugins/RealmWarden.cs` **unchanged** with `Mocks.cs` (stand-ins for the game and Oxide types it touches) and `Tests.cs`, then runs **140 scenario checks** covering:
  - config clamping
  - every protection path
  - combat-log timing, death and shutdown
  - rate, repeat, length, caps and word limits, and mute escalation
  - name matching (whole word, leet, camel case, colour tags) and the kick
  - raid windows across midnight with a UTC offset; own group, unclaimed, salvage, repair, rebellion and record-only cases
  - report limits
  - the alert queue: paging, ack, caps, admin chat rate limit
  - the API
  - persistence and the corruption-safe load
  - hot reload

  It proves the plugin's own logic, not that the real game behaves like the mocks.
