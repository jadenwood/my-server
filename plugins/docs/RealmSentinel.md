# RealmSentinel: Realm's own cheat watch

`plugins/RealmSentinel.cs` (Oxide 2.0.3867, C# 3). The Sentinel watches what the server already sees: where players are, what they hit and how hard, what appears in their inventories, how fast they chat, type commands and reconnect, and whose name they wear. Each finding adds points to the player's **suspicion score**, with **evidence** an admin can read. The score fades over time. **Responses** follow the score: alert, freeze, kick, and a ban only when an admin says so (or the owner opts in).

The Sentinel works the same whether the game's own anti-cheat runs or not. **It never touches, reads, calls or works around the game's anti-cheat in any way.** It uses only Oxide hooks and the game's own server-side types (cited below by type and member name).

Lore: the Sentinels keep the watchfires on Ostreval's walls. They do not judge; they write down what they saw, in plain numbers, and send word to the stewards.

Nothing the Sentinel records goes to the public **Chronicle**. **No new Chronicle event types are needed.**

Status: **compile-checked with 0 errors against the real 2.0.3867 DLLs; behaviour-tested against mocks (173 checks) and exploit-tested (24 checks). Never run on a live server.** It ships in **watch mode**: it logs and alerts, and says what it *would* do, but freezes and kicks nobody until the owner switches it to enforce after the smoke test. See [What is UNVERIFIED](#what-is-unverified).

---

## What it watches

| Check | What counts as suspicious | Default limit | Where the limit comes from |
|---|---|---|---|
| **Speed** | Moving faster than the limit, again and again (4 overshoots within 10 s) | 10 m/s (planar + upward); 20 m/s while falling | The game's own movement check: `TeleportationDetection.MaxVelocityWhileNotFalling` and `MaxVelocityWhileFalling` |
| **Teleport** | One sample that overshoots the movement budget by 25 m or more | 25 m | Realm |
| **Fly** | Rising more than 30 m inside 10 s without ever coming down | 30 m / 10 s | Realm |
| **Reach** | A hit from farther than a weapon reaches, after ping credit | melee 7.5 m, projectile 300 m | `MeleeVoodooModule` (7.5 m); projectile range is Realm's |
| **Damage** | One hit for more than a weapon can do | melee 75, projectile 200 | The game's raw caps are 25 (melee), 50 (arrows) and 125 (ballista) before damage effects multiply them (`MeleeVoodooModule`, `ProjectileVoodooModule`, `BallistaVoodooModule`, `DamageMultiplierListener`), so the limits leave room |
| **Fire rate** | Too many hits in a 5 s window | melee: 18 on one target; projectiles: 20 | `MeleeVoodooModule` (0.3 s per target) |
| **Kill rate** | More than 8 kills of online players in 2 min | 8 / 2 min | Realm |
| **Item jump** | One item rising by 500 or more in a 10 s sample with nothing to explain it | 500 | Realm |
| **Gather rate** | More than 3000 unexplained items in 5 min | 3000 / 5 min | Realm |
| **Craft rate** | More than 40 crafts finished in a minute | 40 / min | Realm |
| **Chat flood** | More than 12 chat messages in 10 s (RealmWarden already mutes at 5 per 8 s; this is bot-like) | 12 / 10 s | Realm |
| **Command flood** | More than 15 commands in 10 s are **refused**; 20 refusals in a minute is a finding | 15 / 10 s | Realm |
| **Reconnect cycling** | More than 5 connects in 10 min | 5 / 10 min | Realm |
| **Staff impersonation** | A name that reads as a staff member's (exact), or one letter away (near) | | Realm |

Every number is a config setting. The "Realm" limits are first guesses: `/sentinel peaks` shows the highest values honest players reached, so you can tune them (see [Tuning](#tuning-with-peaks)).

### Movement, without punishing lag

The server samples each player's position once a second (the game's own check samples every 0.75 to 1.25 s). Laggy players reach the server late and in bursts, so the Sentinel never judges one sample against one speed. It keeps a **movement budget**:

- The budget refills at the speed limit plus 10 %, and can hold up to 2 s of movement.
- **Ping credit**: the budget can hold another 2 × ping of movement (ping from `Connection.AveragePing`, the number the game's own `/ping` prints). The credit stops at 600 ms, so a lag switch buys nothing more.
- **Lag allowance**: a player the server heard nothing from (standing still in the samples) may catch up to 6 s of movement at once.
- **Grace** of 5 to 8 s: after a death and respawn, after every teleport the **server** makes (an admin's `/tp`, the game's respawn and glitch fixes, other plugins), and after a trusted `/tp` or `/tpdelay` (until its return trip).
- A new body (a new `Entity`) or a gap of more than 5 s between samples restarts sampling.

How server teleports are seen: the game's `CharacterTeleport.Teleport` raises a `TeleportEvent` through `EventManager.CallEvent`, sent by the server player. The Sentinel subscribes to that event, just as the game's own movement check does, and ignores any `TeleportEvent` a client sent. Spawn events give grace only within a window that a connect or a death opened, because a client can send them.

Players with the game's fly or teleport permissions (`rok.command.admin.fly`, `rok.command.admin.videofly`, `rok.movement.fly`, `rok.command.teleport` and its sub-permissions) skip the movement checks, as the game's own check does.

The game has its own movement check (`flyDetection` in the server settings, off by default on dedicated servers). It kicks at once with no ping allowance. The Sentinel does not need it and does not change it.

### Who reports a hit (why nobody can frame you)

In Reign of Kings the server does not decide most hits itself; a client reports them, and the game checks who sent the report. In the game's own damage checks a melee hit on a player must be **sent by the victim**, and a projectile hit **by the shooter** (`MeleeVoodooModule`, `ProjectileVoodooModule`). The Sentinel scores only the client that sent the hit:

- An arrow or a hit on a creature or object, sent by the attacker: the attacker is checked.
- A melee hit on a player, sent by the victim: **nobody is scored**. The attacker's client did not write it, so a victim (or a third party) cannot frame the attacker with fake far hits.
- Damage the server made itself (traps, fire, falls): ignored.

If the game works as its own checks assume, a melee "reach hack" against players gets nowhere anyway, because the victim's client decides those hits (UNVERIFIED; see item 6 below).

### Items, without punishing trade

Gathering happens on the client; the server has no gather hook (`docs/oxide-rok-api.md` 3.9). So the Sentinel samples each player's **Inventory and Hotbar** every 10 s (`GetContainerOfType`, `CollectionTypes.Inventory` and `Hotbar`) and asks of every gain: what explains it?

- **The player's own losses** in the last 30 min (dropped and picked up again, equipped and taken off, moved between inventory and hotbar).
- **A container they opened** (`OnPlayerInteract` on anything with a `Container`: chests, loot bags, stations), for 5 min. What a container held when first opened is trusted. A later deposit is trusted only when a watcher's own loss in the same sample put it there. So a housemate's deposit can be withdrawn by another housemate, but items spawned straight into a chest cannot be "withdrawn" clean later.
- **Their crafting** (`OnItemCrafted`): the product, for a minute.
- **A trusted `/give`**: from the server console, or by a player with the game's `rok.command.items.give` permission, for the named player.
- **A Realm command that pays items**: `/contract`, `/market`, `/vault`, `/treasury`, `/event`, `/tourney`, `/hunt` (list in config), for a minute. Plugins can also say so themselves (`SentinelItemSource`, below).

Everything else is unexplained. That is how honest gathering looks too, so the gather limit is a rate, not zero.

### Staff impersonation

Staff are learned when they join: anyone with `realmsentinel.admin` or `realmwarden.admin`, or in the Oxide `admin` group (lists in config). Add the names of staff who have not joined yet to `Names.StaffNames`. Names are folded before comparing: lower case; Cyrillic, Greek and full-width look-alike letters to Latin; leet digits to letters; colour tags, invisible characters (zero-width, right-to-left marks), punctuation and spaces removed. `[E86A5C]Ѕеr_G4v.in` reads as `sergavin`.

An exact match scores 40 and the player is asked to rename; one letter away scores 10. Both always alert the admins, whatever the score. RealmWarden's word filter (`admin`, `moderator`, ...) still covers titles.

---

## The suspicion score

Each finding adds **weight × severity** points. Severity is how far over the limit it was, from 1 to 3 (a 250 m teleport is 3; a 30 m one is about 1.2). Default weights:

| Kind | Weight | Kind | Weight |
|---|---|---|---|
| `teleport` | 25 | `gather_rate` | 10 |
| `fly` | 12 | `craft_rate` | 8 |
| `speed` | 8 | `kill_rate` | 8 |
| `reach` | 10 | `command_flood` | 6 |
| `damage` | 15 | `reconnect_cycle` | 6 |
| `fire_rate` | 10 | `chat_flood` | 4 |
| `item_jump` | 15 | `impersonation` / `impersonation_near` | 40 / 10 |

- The score **halves every 20 minutes**. A one-off glitch fades; a cheat that keeps going climbs.
- One kind scores at most **once per 15 s** per player. Repeats inside that time are counted ("+N more" in the report) but add nothing.
- The score belongs to the Steam ID and is saved. A reconnect, a rename or a kick does not reset it.

Example: one 250 m teleport scores 75. In enforce mode that freezes the player (75 ≥ 50). A second one within a few minutes takes them past 80 and they are kicked, with a freeze waiting when they come back.

## Responses

| Level | Score | Watch mode (default) | Enforce mode |
|---|---|---|---|
| Alert | 20 | alert | alert |
| Freeze | 50 | alert "would freeze" | **frozen** for 15 min, alert |
| Kick | 80 | alert "would kick" | **kicked**, frozen for when they return (kick cooldown 10 min), alert |
| Ban | 150 | alert "ban recommended" | alert "ban recommended", **banned only if `Responses.AutoBan` is true** |

An alert goes out when an action is taken, when the level rises, or 10 min after the last one; never a step down in between. Each alert goes to:

- **chat** of every online admin (at most 6 a minute; the rest are summarised),
- the **server log** and `oxide/logs/RealmSentinel/` (`realmsentinel_alerts-<date>.txt`; evidence in `realmsentinel_evidence-<date>.txt`),
- **RealmWarden's alert queue** (`/warden alerts`, kind `ext:sentinel_<kind>`), so admins keep one queue,
- **the Steward feed** `oxide/data/RealmSentinelFeed.json` (below).

**Frozen** means: the player cannot hurt anyone, break or place blocks, open containers or interact, craft, take the throne, or bind anyone. They can still talk (to explain). If `Responses.PinFrozenPosition` is on, a frozen player who moves more than 3 m away is put back where they were frozen (`CharacterTeleport.Teleport`, the call the game's own `/tp` makes). A freeze is saved: logging out does not end it. It ends by itself after its time, or when an admin lifts it.

---

## Commands

Everything is under `/sentinel`. Players who type it get one line that points them to `/warden report`; no data.

### Admins (`realmsentinel.admin`)

Grant the permission in the server console: `oxide.grant group realm_admin realmsentinel.admin` (see `docs/community/ops/staff-roles-and-permissions.md`).

| Command | What it does |
|---|---|
| `/sentinel` or `/sentinel help` | The commands and the mode. |
| `/sentinel status` | Mode, which checks are on, players watched, how many are frozen, alerts stored, the response scores, the five highest scores, and whether the feed and Warden forwarding are on. |
| `/sentinel report <player> [page]` | Score now and peak, first and last seen, detection counts by kind, freeze state, last response, kicks, ping, and the evidence lines (6 a page): time, kind, points, score after, what was measured, where. Works for offline players the Sentinel knows. |
| `/sentinel clear <player>` | Score to 0, counts reset, freeze lifted. **The evidence log is kept.** Use it after you have looked and found nothing. |
| `/sentinel reload` | Re-reads `oxide/config/RealmSentinel.json` without reloading the plugin. A broken file is refused and the old settings stay. |
| `/sentinel freeze <player> [minutes]` | Freezes a player for 1 to 1440 minutes (default 15), in watch mode too. Works for offline players (applies on join). |
| `/sentinel unfreeze <player>` | Lifts a freeze. |
| `/sentinel ban <player> confirm` | Bans with the game's own ban (`Server.Ban`) and removes the player. Without `confirm` it only explains. The evidence is kept. |
| `/sentinel peaks [reset]` | The highest values seen from players under the alert score: fastest sample, longest hit, biggest gain, and so on. |

Quote names with spaces: `/sentinel report "Old Tom"`. Every admin action is written to the evidence log as `admin_action`.

On join, admins are told how many players have a score at or above the alert score.

---

## Setting it up

1. Deploy as usual (Realm Steward or `server/Deploy-Plugins.ps1`). The plugin writes `oxide/config/RealmSentinel.json` on first load.
2. Grant `realmsentinel.admin` to your admin group.
3. List staff names that should be protected before those staff have joined: `"Names": { "StaffNames": ["Your Name"] }`.
4. Play for a week in **watch mode**. Read `/sentinel status` and `/sentinel report` for anyone it alerts on.
5. Tune with `/sentinel peaks`, run the [smoke test](#what-is-unverified), then set `"Responses": { "Mode": "enforce" }` and `/sentinel reload`.

### Tuning with peaks

`/sentinel peaks` lists the highest values from players whose score was under the alert score: `speed_mps`, `rise_m`, `melee_range_m`, `projectile_range_m`, `melee_damage`, `projectile_damage`, `melee_hits_per_target_window`, `projectile_hits_per_window`, `kills_per_window`, `item_gain_per_sample`, `item_gain_per_window`, `crafts_per_minute`, `chat_per_window`, `commands_per_window`, `connects_per_window`. Set each limit comfortably above what honest players reached. `speed_mps` is one sample, so a lag catch-up can show 50 or more: that is the budget doing its job, not a number to copy.

## The Steward feed

`oxide/data/RealmSentinelFeed.json` is rewritten at most every 5 s when something changed, and on unload and shutdown. Realm Steward (or any tool) can read it; nothing in it is secret beyond Steam IDs. Field names are stable; `Version` changes if they ever do.

| Field | Meaning |
|---|---|
| `Version` | `1` |
| `Generated` | UTC time, `yyyy-MM-ddTHH:mm:ssZ` |
| `Mode` | `watch` or `enforce` |
| `DataDamaged` | `true` if `RealmSentinel.json` could not be read (nothing is being saved) |
| `Online`, `Frozen` | counts |
| `Alerts[]` | newest first, up to 100: `Id`, `Time`, `Kind`, `PlayerId`, `PlayerName`, `Score`, `Response`, `Detail` |
| `Suspects[]` | highest score first, up to 20: `PlayerId`, `PlayerName`, `Score`, `PeakScore`, `Online`, `Frozen`, `LastSeen`, `Counts` (kind → number) |

`Response` is one of `alert`, `would freeze`, `would kick`, `ban recommended`, `frozen 15 min`, `kicked`, `kicked, frozen 15 min on return`, `banned`, `ban failed` (an enforce-mode action may carry `; ban recommended`). Names and details are cleaned: no colour tags, no line breaks, no invisible characters.

## For other plugins

Non-public methods, called with `Plugin.Call` (`[PluginReference] Plugin RealmSentinel;`):

| Method | Use |
|---|---|
| `void SentinelGrace(ulong playerId, float seconds)` | Before a plugin moves a player (an exile, an arena). Capped at 60 s. |
| `void SentinelItemSource(ulong playerId, float seconds)` | Before a plugin hands items to a player (escrow, prizes, market). Capped at 120 s. |
| `double GetSentinelScore(ulong playerId)` | The current score (0 for unknown players). |
| `bool IsSentinelFrozen(ulong playerId)` | Whether the player is frozen. |

The Sentinel calls `RealmWarden.RaiseWardenAlert(kind, playerId, detail)` for each alert when RealmWarden is loaded.

---

## Config reference

`oxide/config/RealmSentinel.json`. Out-of-range values are clamped on load; a missing section gets its defaults.

| Section | Setting | Default | Meaning |
|---|---|---|---|
| `General` | `Enabled` | `true` | All checks on or off |
| | `AdminsExempt` | `true` | `realmsentinel.admin` skips every check |
| | `ExemptIds` | `[]` | Steam IDs that skip every check (a trusted builder's alt, for example) |
| | `SaveIntervalSeconds` | `120` | |
| `Movement` | `Enabled`, `SampleSeconds` | `true`, `1` | |
| | `MaxSpeed`, `MaxSpeedFalling`, `FallVelocity` | `10`, `20`, `5` | m/s; the game's own numbers |
| | `SpeedTolerancePercent`, `BurstSeconds` | `10`, `2` | Budget refill margin and size |
| | `PingFactor`, `MaxPingCreditMs` | `1`, `600` | Ping credit and its cap |
| | `LagAllowanceSeconds` | `6` | Catch-up allowed after silence |
| | `MinExcessMeters`, `ViolationsToFlag`, `ViolationWindowSeconds` | `1`, `4`, `10` | Speed finding |
| | `TeleportMeters` | `25` | |
| | `CheckAscent`, `AscentWindowSeconds`, `MaxRiseMeters` | `true`, `10`, `30` | Fly finding |
| | `GraceAfterRespawnSeconds`, `GraceAfterTeleportSeconds` | `8`, `5` | |
| | `MaxGapSeconds` | `5` | Longer gaps restart sampling |
| | `ExemptGameFlyPermissions` | `true` | Skip players with the game's fly/teleport permissions |
| | `TeleportCommands` | `tp`, `warp`, `teleport`, `tpdelay` | Trusted when typed by the console or a player with the game permission |
| `Combat` | `MeleeRange`, `MeleeMaxDamage`, `MeleeMinIntervalPerTarget` | `7.5`, `75`, `0.3` | |
| | `ProjectileRange`, `ProjectileMaxDamage`, `ProjectileMaxHitsPerWindow` | `300`, `200`, `20` | |
| | `RateWindowSeconds`, `RangeSlackMeters`, `PingFactor` | `5`, `1.5`, `1` | |
| | `KillWindowSeconds`, `MaxKillsPerWindow` | `120`, `8` | |
| `Items` | `SampleSeconds`, `IncludeHotbar` | `10`, `true` | |
| | `JumpAmount`, `GainWindowMinutes`, `MaxGainPerWindow` | `500`, `5`, `3000` | |
| | `ContainerTrackSeconds`, `SourceGraceSeconds`, `CreditMinutes`, `CraftGraceSeconds` | `300`, `60`, `30`, `60` | |
| | `MaxCraftsPerMinute` | `40` | |
| | `GiveCommands`, `ItemSourceCommands` | see file | |
| `Flood` | `ChatWindowSeconds`, `ChatMaxPerWindow` | `10`, `12` | Scored only |
| | `CommandWindowSeconds`, `CommandMaxPerWindow`, `BlockExcessCommands`, `RefusedCommandsToFlag` | `10`, `15`, `true`, `20` | |
| `Connections` | `WindowMinutes`, `MaxConnects` | `10`, `5` | |
| | `RefuseWhileCycling`, `RefuseSeconds` | `false`, `120` | Opt-in; enforce mode only |
| `Names` | `StaffNames`, `StaffPermissions`, `StaffGroups`, `LearnStaff` | `[]`, Sentinel + Warden admin, `admin`, `true` | |
| | `NearMatchMinLength` | `5` | Shorter names are only matched exactly |
| | `KickExactMatch`, `KickDelaySeconds` | `false`, `5` | Opt-in; enforce mode only |
| `Score` | `HalfLifeMinutes`, `KindCooldownSeconds`, `Weights` | `20`, `15`, table above | |
| `Responses` | `Mode` | `watch` | `watch` or `enforce` |
| | `Alert`, `AlertScore`, `AlertCooldownMinutes` | `true`, `20`, `10` | |
| | `Freeze`, `FreezeScore`, `FreezeMinutes`, `PinFrozenPosition` | `true`, `50`, `15`, `true` | |
| | `Kick`, `KickScore`, `KickCooldownMinutes` | `true`, `80`, `10` | |
| | `AutoBan`, `BanScore` | `false`, `150` | Bans need an admin unless `AutoBan` |
| `Alerts` | `ChatToOnlineAdmins`, `MaxChatAlertsPerMinute` | `true`, `6` | |
| | `ForwardToWarden`, `LogToFile`, `WriteFeed` | `true`, `true`, `true` | |
| | `MaxStored`, `MaxEvidence`, `FeedAlerts`, `FeedSuspects` | `500`, `3000`, `100`, `20` | |

Privacy: the Sentinel stores Steam IDs, names, scores, detection counts, and evidence lines with a position and ping. It never stores IP addresses (`CanUserLogin` sees one and drops it). The evidence log keeps the newest 3000 lines; the daily files in `oxide/logs/RealmSentinel/` are kept until someone deletes them. `docs/legal/tools/find-player-data.mjs` finds a player's lines in both, as it does for RealmWarden.

Data: `oxide/data/RealmSentinel.json` (players, staff, peaks, evidence, alerts). If it exists but cannot be read, the plugin runs from memory, tells admins on join, and **never overwrites it**. Fix or remove it, then reload.

---

## What it cannot see

- **Slow hovering.** Without a ground trace the server cannot tell a player hovering 20 m up from one standing on a roof. Only sustained climbing is caught. The game's own `LevitationDetection` traces the ground, but only when `flyDetection` is on.
- **Melee reach against players**, because the victim's client reports those hits (above). Reach is checked on projectiles and on hits against creatures and objects.
- **A player who never reports damage to themselves** (a god-mode client). The server does not hear the hit at all.
- **Exactly where a gain came from.** An unexplained gain is how honest gathering looks too; only the size and rate are judged.
- **Who finished a station's craft.** It is credited to the client that sent the event.

## Tests

```
bash plugins/docs/RealmSentinel/logic-tests/run.sh     # 173 checks: every cheat, the responses, commands, feed, persistence,
                                                        # and an hour each of honest players at 40, 300 and 600 ms ping
bash tools/exploit-review/sentinel/run.sh              # 24 checks: ways to get past the Sentinel, frame someone or shed a score
bash tools/plugin-compile-check/check.sh               # C# 3 against the real 2.0.3867 DLLs
node tools/realm-integration/check.mjs --commands      # wiring, chat style, commands
```

The honest replays: three players sprint near the speed limit for an hour, turning and jumping, off a cliff every few minutes, up a 24 m staircase, with 1.5 to 5 s lag spikes every ~40 s (no position updates, then a catch-up of up to 78 m in one sample). Two 450 ms players fight for ten minutes (melee at the edge of reach, bunched hits, bow shots out to 170 m). A gatherer works for an hour at about 540 items a minute, loots and stocks a chest, takes contract payouts and crafts. None of them is flagged and no alert is raised.

What the tests prove: the plugin's own rules. What they do not prove: that the real game behaves like the mocks.

## What is UNVERIFIED

Nothing here has run on a real server. Each item has a test for the owner's server; do them in watch mode with a second account, and read `/sentinel report <player>` after each.

1. **Positions on the server track players closely enough.** Sprint in a straight line for a minute. Expected: no findings; `/sentinel peaks` shows `speed_mps` between about 5 and 10 (higher only after lag). Then set `Movement.MaxSpeed` to `3`, `/sentinel reload`, sprint again: expected `speed` findings. Put it back.
2. **Ping.** `/sentinel report <player>` shows a ping; compare with the game's `/ping` for that player.
3. **Server teleports give grace.** Teleport the test player far away with the game's `/tp` from the console. Expected: no `teleport` finding. If there is one, the `TeleportEvent` subscription did not work (the server log says "Could not watch server teleports") and only typed `/tp` commands give grace.
4. **Respawn.** Die and respawn at a far bed. Expected: no `teleport` finding.
5. **Fly check.** Climb the tallest staircase on the server. Expected: no `fly` finding. Set `Movement.MaxRiseMeters` to `5`, climb again: expected `fly`. Put it back.
6. **Who sends hits.** Shoot the test player from 50 to 100 m with a bow. Expected: `/sentinel peaks` shows `projectile_range_m` near that distance. Spar with melee. Expected: no findings, and no `melee_range_m` peak from player-on-player hits (the victim sends those). If melee peaks do appear from sparring, the attribution assumption is wrong; tell the developers.
7. **Damage numbers.** Hit with the strongest weapons, under any damage-boosting effect the server has, and fire a ballista. Expected: `melee_damage` and `projectile_damage` peaks stay under 75 and 200.
8. **Kill rate.** Set `Combat.MaxKillsPerWindow` to `1`, kill the test player twice within 2 min: expected `kill_rate`. Put it back.
9. **Inventory sampling.** Gather for two minutes: expected `item_gain_per_window` in peaks. Set `Items.JumpAmount` to `20`, gather 30 wood within 10 s: expected `item_jump`. Put it back.
10. **Containers.** Put 600 stone in a chest. With the second account open the chest and take it all: expected no finding. Kill the second account, loot the body: expected no finding. If either is flagged, opening that container did not reach `OnPlayerInteract` with a `Container`.
11. **Ground pickups.** Drop 600 stone; the second account picks it up. If that is an `item_jump`, ground pickups are not container interactions; raise `Items.JumpAmount` above a full stack or accept the finding as low weight.
12. **Crafting.** Craft a large batch of arrows. Expected: no `item_jump`.
13. **Trusted /give.** From the console, `give Stone 2000 <second account>`. Expected: no finding.
14. **Command limit.** Type `/house list` 20 times quickly. Expected: "Slow down" and the extra commands refused.
15. **Freeze** (enforce mode). `/sentinel freeze <second account> 2`. Try to hit, break and place blocks, open a chest, craft, and walk away. Expected: each refused with "held by the Sentinel", and being put back after walking 3 m. Log out and in: still frozen.
16. **Kick and ban.** Set `Responses.KickScore` low, or use `/sentinel ban <second account> confirm` on a throwaway account; check the ban in Realm Steward's Court screen and lift it there.
17. **Reconnect refusal** (opt-in, enforce). With `Connections.RefuseWhileCycling` on, reconnect six times within 10 min. Expected: the seventh attempt is refused for 2 min. The reason text is not shown to the client (Oxide does not send it).
18. **Game permission exemption.** Give the test account `rok.command.admin.fly` with the game's own permission tools, fly around: expected no findings.
19. **Warden forwarding.** After any alert, `/warden alerts` shows it as `ext:sentinel_<kind>`.
20. **Logs and feed.** `oxide/logs/RealmSentinel/` has the day's alert and evidence files; `oxide/data/RealmSentinelFeed.json` is updated.
21. **Load.** With 20 or more players online, watch the server's frame time in Steward for a few minutes after deploying. The work is small (one position read a second and one inventory read every 10 s per player) but has not been measured.

## Sources

Game metadata read in the shipped patched `Assembly-CSharp.dll` (type and member names only; no game code is in this repo): `CodeHatch.Witchcraft.TeleportationDetection` (`MaxVelocityWhileNotFalling`, `MaxVelocityWhileFalling`, `FallVelocityThreshold`, `CheckIntervalMin`/`Max`, `RefreshDisabledByServerSettings`), `LevitationDetection.MaxHeightAboveSurface`, `MeleeVoodooModule`, `ProjectileVoodooModule`, `BallistaVoodooModule`, `VoodooDetectionManager`, `DamageMultiplierListener`, `CharacterTeleport.Teleport`, `TeleportEvent`, `EventManager.Subscribe`/`Unsubscribe`, `NetworkEvent.Sender`, `Player.AveragePing`, `Connection.AveragePing`, `CoreCommandHandler` (`/ping`), `ThronesCommandHandler` (`/tp`, `/tpdelay`, `/give` and their permissions), `ServerSettingsFile` (`flyDetection`), `DamageType`, `Damage`, `ItemCollectionBase`, `InvGameItemStack.Name`/`StackAmount`, `CollectionTypes`, `ItemContainerExtensions.GetContainerOfType`, `Container.Contents`, `ItemCrafterEvent.Crafter`, `ItemCrafter.Product`, `InteractEvent.ControllerEntity`, `PlayerPreSpawnCompleteEvent`, `Server.Kick`, `Server.Ban`. Hook order from `Oxide.ReignOfKings.dll` (`ReignOfKingsCore.IOnServerCommand`: `OnServerCommand`, then `OnPlayerCommand` only for an online player). Hooks as in `docs/oxide-rok-api.md`.
