# RealmDynasties: bloodlines and succession

`plugins/RealmDynasties.cs` (Oxide 2.0.3867, C# 3). It adds **lines** (dynasties) on top of houses and the crown:

- A player founds a line and is its **head**.
- The head names **heirs**, other players who must accept. Heirs stand in a declared **line of succession**.
- When the head **abdicates** or is **unseen for N days**, the line and its **titles** pass to the first active heir.
- When a monarch of the line **loses the throne**, the heirs may press a **blood claim** for a limited time. It rides on CrownAndConsequences' own `/claim` rules.
- Each line earns a **prestige** score from reigns held, oaths kept, titles, generations and restorations.
- `/dynasty tree` shows the family tree.

Lore: in Ostreval, the crown belongs to the seat, not the blood (Hearth Charter, article 1). A line therefore has no *right* to the throne. A blood claim is a public grievance pressed through the lawful channel, the Lawful Hours of a declared rebellion. It is never a shortcut around that channel.

Status: **compile-checked and logic-tested, never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

---

## Commands

All commands are subcommands of `/dynasty`. Names that contain spaces must be in double quotes (`"Iron Stag"`). The server's chat parser supports quoted arguments.

| Command | Who | What it does |
|---|---|---|
| `/dynasty` or `/dynasty help` | anyone | Shows the help. Admins also see the admin line. |
| `/dynasty found <name>` | a player in no line | Founds a line. You become its head (generation 1). The name is 3-24 characters: letters, digits, single spaces, `'` or `-`, starting with a letter. Cooldown `FoundCooldownHours` per player. |
| `/dynasty info [line or player]` | anyone | Shows the head, the line of succession, titles, prestige and its parts, the seat house, any blood right, and the latest history entry. |
| `/dynasty list` (or `top`) | anyone | Lines ranked by prestige. Capped at `MaxListLines` rows. Marks the line that holds the crown. |
| `/dynasty tree [line]` | anyone | Family tree from the founder down: role (head, heir n, elder, kin) and generation. Capped at `MaxTreeLines` lines. |
| `/dynasty heir name <player>` | head | Names an online player as heir. They have `OfferExpireSeconds` to answer. If the name matches kin already in the line, that kin goes straight back into the succession. |
| `/dynasty accept` / `/dynasty decline` | the named player | Accept: you join the line as a child of the head (generation +1), at the end of the succession. |
| `/dynasty heir remove <player>` | head | Takes an heir out of the succession. They stay in the family as kin. |
| `/dynasty heir order <player> <position>` | head | Moves an heir to a position (1 = next in line). |
| `/dynasty heir list` | member | Shows the line of succession. Heirs unseen for N days are flagged; they are skipped. |
| `/dynasty disown <player>` | head | Casts a member out. Their children are re-parented to the member's parent. Cooldown `DisownCooldownMinutes`. The disowned player has `RejoinCooldownHours` before joining or founding again. |
| `/dynasty leave` then `/dynasty leave confirm` | non-head member | Leaves the line. Rejoin cooldown applies. |
| `/dynasty abdicate` then `/dynasty abdicate confirm` | head | Passes the line and its titles to the first active heir. You become an elder. Blocked for `AbdicateCooldownHours` after any succession. |
| `/dynasty dissolve` then `/dynasty dissolve confirm` | head, sole member | Ends the line. Its titles return to the crown. |
| `/dynasty claim` | head or a named heir | Presses the line's blood claim after its monarch fell (see below). Cooldown `ClaimCommandCooldownSeconds`. |
| `/dynasty bestow "<line>" <title>` | the reigning monarch | Gives a title to another line. If another line holds that title, it is taken from them (`BestowTransfersTitles`). Crown-wide cooldown `BestowCooldownMinutes`. At most `MaxBestowsPerReign` per reign. Not allowed on the monarch's own line. |

Admin commands need the Oxide permission **`realmdynasties.admin`**:

```
oxide.grant user <name|steamid> realmdynasties.admin
```

| Command | What it does |
|---|---|
| `/dynasty admin pass "<line>" [member]` | Forces a succession to the named member, or to the first heir regardless of activity. |
| `/dynasty admin dissolve "<line>"` | Ends a line (squatted names, abandoned lines). |
| `/dynasty admin title add "<line>" <title>` / `title remove "<line>" <title>` | Grants or removes a title. `add` refuses a title another line already holds. |
| `/dynasty admin prestige "<line>" <+n or -n>` | Adds n to the line's adjustment, clamped to ±`AdminAdjustLimit`. |
| `/dynasty admin check` | Runs the succession, crown and claim checks now. |
| `/dynasty admin save` | Writes the data file now. |

Admins also skip the found and rejoin cooldowns when founding a line, and the abdicate and disown cooldowns. They do not skip the rejoin cooldown when accepting an heir offer.

---

## How it works

### Succession

- **The line of succession is declared.** The head chooses heirs and their order. Accepted heirs are the head's children in the tree, one generation down.
- **Abdication.** The first heir in order who is online, or was seen within `InactiveDaysForSuccession`, becomes head. The previous head stays as an *elder*. The remaining heirs keep their order, so the new head's siblings stay in line.
- **Inactivity.** On every tick (`TickSeconds`), each line whose head is offline and unseen for `InactiveDaysForSuccession` days passes to the first active heir.
  - If no heir is active, the line goes *dormant*. It is logged once, and nothing changes until someone returns.
- **Downtime does not count.** If the gap between two ticks is longer than `DowntimeGapMinutes` (the server was off), every last-seen time is moved forward by the gap (`FreezeInactivityDuringDowntime`). A week-long server outage therefore never deposes anyone.
- **Titles pass with the headship.** Titles belong to the line and are held by its current head.
- **House leadership is advised, not changed.** RealmHouses has no call that changes a house's leader. When the old head led a house that the new head also belongs to, both are told to use `/house promote <new head> leader`.
- Every succession goes to the chat, the line's history and the Chronicle (`succession`).

### Reigns, the fall of a monarch, and blood claims

- **The crown is tracked by polling** the game's `KingsScheme` every tick. `OnThroneCaptured` and `OnThroneReleased` only trigger an earlier poll.
- **A reign counts for a line** once a member of the line has held the throne for `MinReignMinutesToCount`.
  - Only time while the king is in the line counts.
  - A line that re-sits the throne within `ReignRecountCooldownHours` of its last counted reign does not get a second "reign held". Passing an empty throne around inside one line is one reign. Reign hours still accrue.
- **The fall.** When a counted reign of a line ends and the throne passes outside the line (or stands empty), the line gets a **blood right** for `BloodClaimWindowHours`.
- **`/dynasty claim`** (head or heir; not the fallen monarch unless `AllowFallenMonarchToClaim`):
  1. Reads the claimant's house from RealmHouses. A blood claim must go through a house.
  2. If CrownAndConsequences already lists an open claim for that house (`GetOpenClaims`), the blood claim is **linked** to it and its rebellion window.
  3. Otherwise, if `DelegateToCrownClaim` is on, it runs CrownAndConsequences' own `/claim declare` for the player. That plugin applies **all** of its rules: house head only, claim cooldown, notice period, next window. Its messages go straight to the player. `GetOpenClaims` is then read again, and only a claim that actually exists counts.
  4. If there is still no claim, the blood claim waits (**awaiting**) until the house's head declares, before the blood right expires. Each tick checks `GetOpenClaims`.
  5. If CrownAndConsequences is not loaded, the claim is recorded in honour mode, with no rebellion gating.
- **Restoration.** The line *restores* itself when a line member takes the throne, or when a linked claim's house holds the crown after its window opens. It earns `PrestigePerRestoration` and the Chronicle `blood_restored` only if all of these hold:
  - the claim was pressed,
  - someone outside the line reigned between the fall and the return (stepping off an empty throne and sitting back down earns nothing),
  - `RestorationCooldownHours` has passed since the line's last restoration,
  - the crown came by a *new reign*. A sitting monarch who joins the line settles the right with no bonus, so a line cannot simply recruit the king.
- A linked claim **lapses** if its window ends without the crown, or if CrownAndConsequences no longer lists it (for example, an admin cancelled it). An open or awaiting claim lapses at the blood right's expiry.

### Prestige

```
prestige = max(0,
    PrestigePerReign       * reigns held
  + PrestigePerReignHour   * min(hours on the throne, ReignHoursCap)
  + PrestigePerOathDay     * min(days of oaths kept, OathDaysCap)
  - PenaltyPerOathBroken   * oaths broken
  - PenaltyPerTreatyBroken * treaties broken
  + PrestigePerTitle       * titles held
  + PrestigePerGeneration  * (generations reached - 1)      (capped at MaxGenerationsCounted)
  + PrestigePerRestoration * restorations
  + admin adjustment)
```

- **Oaths kept.** The line's *seat* is the head's house in RealmHouses. Every tick, while the seat is sworn to a liege or holds at least one vassal, the elapsed time is added. It is capped at two ticks per step, so downtime or clock jumps add nothing.
- **Oaths and treaties broken.** These are the increase of the seat's `oathsBroken` / `treatiesBroken` (RealmHouses `GetReputation`) while it is the seat. Changing seat starts from the new house's current record; earlier marks are not inherited.
- **Generations.** A generation is credited only when the previous head held the line for `MinHeadshipHoursForGeneration`. This stops quick abdication chains from farming generations.

### Data and safety

- **Data file:** `oxide/data/RealmDynasties.json`.
  - If it exists but cannot be parsed, the plugin **refuses to load and never writes it**. The same applies if it parses to nothing (an empty or truncated file deserializes to null with Oxide's Newtonsoft; the logic tests prove this).
  - Fix or remove the file, then `oxide.reload RealmDynasties`.
  - Hand-edited files are repaired on load:
    - duplicate names are dropped
    - a player found in two lines stays in the first
    - a missing head becomes the first member
    - ghost heirs are removed
    - dangling parents are cleared
    - a missing last-seen is set to "now", never to "gone"
- **Write frequency:** the file is written every tick, because last-seen times move. It is also written on every change, on `OnServerSave` and on unload.
- **Memory only** (dropped on reload): heir offers, confirmations and command throttles.
- **Caps:**
  - lines (`MaxDynasties`), members per line (`MaxMembersPerDynasty`), heirs in line (`MaxHeirsInLine`), titles (`MaxTitlesPerDynasty`), open offers per line (`MaxPendingOffersPerDynasty`)
  - history (`MaxHistoryEntries`), list and tree output (`MaxListLines`, `MaxTreeLines`)
  - Chronicle lines per hour (`ChronicleMaxPerHour`), a 1 s per-player command throttle (`CommandThrottleSeconds`)
  - cooldowns on found, rejoin, offer, abdicate, disown, claim and bestow
- **Messages:**
  - Line names and titles may contain only letters, digits, single spaces, `'` and `-`, and must start with a letter (`char.IsLetter`, so non-English letters are allowed). They therefore cannot contain braces or `[RRGGBB]` colour tags.
  - Steam player names are not restricted. Like all player text, they are only ever a `string.Format` *argument*, never the format string, and are sent through the single-string chat overloads.
- **Events log:** every Chronicle-worthy event is also written to `oxide/logs/RealmDynasties/realmdynasties_events-<date>.txt`.

---

## Configuration (`oxide/config/RealmDynasties.json`)

Created with these defaults on first load. Out-of-range values are clamped on load.

| Key | Default | Meaning |
|---|---|---|
| `TickSeconds` | 60 | Check interval (minimum 15). |
| `NameMinLength` / `NameMaxLength` | 3 / 24 | Line name length. |
| `TitleMaxLength` | 40 | Title length (minimum 3). |
| `MaxDynasties` | 200 | Lines in the realm. |
| `MaxMembersPerDynasty` | 24 | Members per line (maximum 100). |
| `MaxHeirsInLine` | 8 | Heirs in the line of succession (maximum 20). |
| `MaxTitlesPerDynasty` | 6 | Titles per line. |
| `MaxPendingOffersPerDynasty` | 3 | Unanswered heir offers per line. |
| `OfferExpireSeconds` | 300 | Time to accept an heir offer. |
| `FoundCooldownHours` | 24 | Per player, between founding lines. |
| `RejoinCooldownHours` | 12 | After leaving, being disowned or dissolving. |
| `HeirOfferCooldownSeconds` | 30 | Per head, between heir offers. |
| `AbdicateCooldownHours` | 24 | After any succession. |
| `DisownCooldownMinutes` | 30 | Per line. |
| `InactiveDaysForSuccession` | 14 | The **N days**: a head unseen this long passes the line on; heirs unseen this long are skipped. |
| `FreezeInactivityDuringDowntime` | true | Server downtime does not count as absence. |
| `DowntimeGapMinutes` | 10 | A tick gap longer than this is treated as downtime. |
| `MinHeadshipHoursForGeneration` | 24 | Headship needed before the next generation is credited. |
| `MaxGenerationsCounted` | 10 | Cap for generation prestige. |
| `MinReignMinutesToCount` | 30 | Minimum reign that counts as a reign held. |
| `ReignRecountCooldownHours` | 24 | A line's next reign within this time is the same reign. |
| `BloodClaimWindowHours` | 72 | How long after the fall a blood claim may be pressed (or wait for its house claim). |
| `RestorationCooldownHours` | 72 | Per line, between rewarded restorations. |
| `DelegateToCrownClaim` | true | `/dynasty claim` runs CrownAndConsequences' `/claim declare` for the player. |
| `AllowFallenMonarchToClaim` | false | Whether the deposed monarch may press the claim personally. |
| `ClaimCommandCooldownSeconds` | 60 | Per player. |
| `BestowCooldownMinutes` | 60 | Crown-wide, between bestowals. |
| `MaxBestowsPerReign` | 5 | Bestowals per reign. |
| `BestowTransfersTitles` | true | Bestowing a held title takes it from its holder. |
| `PrestigePerReign` | 100 | |
| `PrestigePerReignHour` / `ReignHoursCap` | 2 / 500 | |
| `PrestigePerOathDay` / `OathDaysCap` | 5 / 365 | |
| `PenaltyPerOathBroken` | 75 | |
| `PenaltyPerTreatyBroken` | 25 | |
| `PrestigePerTitle` | 25 | |
| `PrestigePerGeneration` | 30 | |
| `PrestigePerRestoration` | 150 | |
| `AdminAdjustLimit` | 1000 | Bound for `/dynasty admin prestige`. |
| `MaxListLines` / `MaxTreeLines` / `MaxHistoryEntries` | 15 / 30 / 25 | Output and history caps. |
| `BroadcastEvents` | true | Herald lines in global chat. |
| `ChronicleEnabled` | true | Send events to RealmChronicle. |
| `ChronicleHeirNamed` | true | Also chronicle and broadcast heir namings. |
| `ChronicleMaxPerHour` | 12 | All dynasty Chronicle lines together. |
| `CommandThrottleSeconds` | 1 | Per-player throttle on changing commands. |

---

## Integration

### Chronicle event types

> **Registered.** These types are now in `plugins/RealmChronicle.cs` `KnownTypes`, `chronicle/server.js` `EVENT_TYPES` and `chronicle/public/assets/common.js` `TYPE_META` (a chronicle test keeps the three in step). The fallback described here only applies to an older RealmChronicle.

RealmChronicle rejects unknown types: `Log` returns 0. Until the integration team adds these to `plugins/RealmChronicle.cs` `KnownTypes`, `chronicle/server.js` `EVENT_TYPES` and `chronicle/public/assets/common.js` `TYPE_META`, the events go only to the server console and `oxide/logs`. The plugin warns once per type.

Icons are from the existing overlay icon set. These were the suggested `tone`/`group` values; as registered, all six sit in the Chronicle's `law` group ("Law & Dynasty" filter):

| type | label | icon | tone | group |
|---|---|---|---|---|
| `dynasty_founded` | A Line Is Founded | people | gold | dynasties |
| `heir_named` | An Heir Is Named | oath | moss | dynasties |
| `succession` | Succession | scroll | gold | dynasties |
| `blood_claim` | Blood Claim | flag | blood | war |
| `blood_restored` | The Line Restored | crown | gold | crown |
| `title_bestowed` | Title Bestowed | seal | gold | crown |

Actors are public player names only, as the Chronicle contract requires.

### Calls this plugin makes (all optional)

| Plugin | Call | Used for |
|---|---|---|
| CrownAndConsequences | `GetOpenClaims()` → `string[]` `"house\|status\|startIso\|endIso"` | Linking blood claims, detecting a cancelled claim. |
| CrownAndConsequences | `GetKingHouse()` → `string` | Restoration through the claimant house. |
| CrownAndConsequences | `CmdClaim(Player, "claim", ["declare"])` | Its own `/claim declare` chat command (`DelegateToCrownClaim`). It is a non-public instance method, which Oxide registers as a callable hook (Oxide.CSharp `CSharpPlugin.cs` constructor scans `NonPublic\|Instance` methods; read at @49500b8). The result is judged only by `GetOpenClaims` afterwards. If that plugin renames the method, the call returns null and the claim simply waits for the house head's own `/claim declare`. |
| RealmHouses | `GetHouse(id)`, `GetHouseLeader(house)`, `GetLiege(house)`, `GetVassals(house)`, `GetReputation(house)` | Seat house, oaths kept/broken, house-leadership advice. |
| RealmChronicle | `Log(type, title, detail, actors)` → `int` | Chronicle. |

### API for other plugins (`plugin.Call`, all non-public)

| Call | Returns |
|---|---|
| `GetDynasty(string playerId)` | The line name, or null. |
| `GetDynastyHead(string line)` | The head's SteamID64 string, or null. |
| `GetDynastyHeirs(string line)` | `List<string>`: heir ids in order of succession, or null. |
| `GetDynastyPrestige(string line)` | `int` (0 if unknown). |
| `GetDynastyTitles(string line)` | `List<string>`, or null. |
| `GetDynastySummaries()` | `List<Dictionary<string, object>>` of `{name, head, members, heirs, prestige, titles, reigns}`. Names only, no ids. |

---

## Tests run here

1. **Compile check:** `tools/plugin-compile-check/check.sh` → `OK: plugins/*.cs compile at C# 3 against Oxide.ReignOfKings 2.0.3867 metadata.` All plugins, 0 errors.
   - This file alone with `-warn:4` gives only the three expected CS0649 warnings for the `[PluginReference]` fields.
2. **Logic tests:** `plugins/docs/RealmDynasties/logic-tests/run.sh` → `92 passed, 0 failed`.
   - The script compiles the real `plugins/RealmDynasties.cs` together with `RulesTests.cs` at C# 3 against the shipped DLLs and runs it on the cached .NET runtime.
   - It covers:
     - names
     - inactivity and successor choice, including dormant lines and dangling ids
     - succession, generation credit and its cap
     - remove, re-parent and line reordering
     - the prestige formula, caps and floor
     - the family tree, including the cap and a corrupt parent cycle
     - the downtime freeze
     - parsing CrownAndConsequences' `GetOpenClaims` format (the exact `ToString("o")` it writes)
     - accrual caps
     - reign recount, usurpation and restoration rules, including the cooldown
     - repair of a damaged data file
     - a JSON round trip with the Newtonsoft build inside `Oxide.References.dll`
     - **an empty data file deserializing to null and a truncated one throwing**, which is why `LoadData` refuses both
   - A mutation check (disabling the inactive-heir skip) made 3 tests fail, so the suite does detect regressions.
   - The tests live outside `plugins/*.cs`, so `Deploy-Plugins.ps1` never copies them.

---

## Smoke test (owner's PC, after the steps in `docs/smoke-test.md`)

You need two Steam accounts (A and B), or a friend. Grant A `realmdynasties.admin`. RealmHouses, CrownAndConsequences and RealmChronicle should be loaded.

| # | Step | Expected |
|---|---|---|
| D1 | `oxide.reload RealmDynasties`; check the console | Loads without errors. `oxide/config/RealmDynasties.json` and `oxide/data/RealmDynasties.json` are created. |
| D2 | A: `/dynasty` | Four help lines plus the admin line. |
| D3 | A: `/dynasty found Varrow` | "The line of Varrow is founded." Herald broadcast. Console shows `[dynasty_founded]`. A RealmChronicle warning about the unregistered type is expected until registration. |
| D4 | A: `/dynasty found Other` | Refused: already in a line. |
| D5 | A: `/dynasty heir name <B>`; B: `/dynasty accept` | B is heir 1. `/dynasty tree` shows B under A, generation 2. |
| D6 | A: `/dynasty heir name <B>` again within 30 s | Refused (already heir), with no new offer. |
| D7 | A: `/dynasty abdicate`, then `/dynasty abdicate confirm` | B is head. Succession broadcast. `/dynasty info` shows A as elder. If A led a house that B is in, both see the `/house promote` advice. |
| D8 | B: `/dynasty abdicate` | Refused by `AbdicateCooldownHours`. |
| D9 | Inactivity: set `InactiveDaysForSuccession` to 1 and reload. Make B head with A as heir. Stop the server. In `oxide/data/RealmDynasties.json`, set B's `LastSeen` 3 days back and `LastTickAt` to the current UTC time. Start the server within `DowntimeGapMinutes` (10), with B offline and A online. | Within one tick A becomes head ("the head was unseen for 3 days"). |
| D10 | Downtime freeze: as D9, but leave `LastTickAt` 3 days back too | No succession. The console says the server was down; clocks are paused. |
| D11 | Corrupt file: stop the server, truncate the data file to half, start | The plugin does not load; the console shows "Could not read oxide/data/RealmDynasties.json"; the file is unchanged (compare its size). Repeat with a 0-byte file: "exists but holds no data". |
| D12 | Crown: B (in line Varrow) takes the vacant throne and holds it for 31 min | `/dynasty info Varrow` shows reigns 1 and reign hours growing. |
| D13 | Fall: a player outside the line takes the throne in a rebellion window (or B leaves it and an outsider sits) | Varrow members are told the crown fell, with a 72 h claim. `/dynasty info` shows the blood right. |
| D14 | A (heir, house leader of a RealmHouses house): `/dynasty claim` | CrownAndConsequences' own claim messages appear. Then "Your blood claim rides on House X's claim…" plus a herald broadcast. `/claim list` shows the house's claim. If A is not the house leader: CrownAndConsequences refuses, then the claim shows **awaiting**; once the leader runs `/claim declare`, the next tick links it. |
| D15 | During the window, a Varrow member or the claimant house takes the throne | "The line of Varrow regains the crown." Restorations 1. |
| D16 | Bestow: the reigning monarch runs `/dynasty bestow "Ashgrove" Warden of the Hill Road`, then within 60 min bestows again | The first gives the title; the second is refused by the cooldown. The title shows in `/dynasty info Ashgrove`. |
| D17 | Oaths kept: make the head's house swear to another (`/swear`, accept), wait several ticks | Oath days rise slowly (`/dynasty info`). `/renounce confirm` adds 1 oath broken (-75). |
| D18 | `/dynasty admin prestige Varrow +5000` | Clamped to +1000. |
| D19 | Hot reload during play: `oxide.reload RealmDynasties` | Data is kept. Pending heir offers are dropped (by design). |

---

## What is UNVERIFIED

These depend on the live game or server and cannot be proven from metadata or IL:

1. **The crown poll.** It assumes `KingsScheme.HasKing()/GetKingID()/GetKingName()` reflect the throne promptly on a dedicated server. The calls are [ASM]-verified and used by real plugins [USE `RaidBoss.cs`]; their timing is not.
2. **Running CrownAndConsequences' `/claim` through `plugin.Call("CmdClaim", …)`.**
   - The dispatch path is read in Oxide source: non-public instance methods are hooks; `HookMethod.HasMatchingSignature` matches `Player, string, string[]`.
   - It has not been exercised in game. If it fails, the claim falls back to *awaiting* and the house head declares by hand.
3. **`OnPlayerConnected` / `OnPlayerDisconnected` timing** for last-seen. A crash leaves last-seen at the last tick (at most `TickSeconds` stale).
4. **Whether `Server.ClientPlayers` excludes the server pseudo-player.** The plugin filters `IsServer` anyway.
5. **Brace safety of the single-string chat overloads** (docs/oxide-rok-api.md §10 item 6). Line names and titles cannot contain braces. Steam player names can, but they only ever reach chat as format arguments.
6. **House leadership is not transferred** (RealmHouses has no API for it). Only advice is given.
7. **Restoration through the claimant house** relies on CrownAndConsequences' `GetKingHouse()`, which comes from RealmHouses at coronation time.
8. **Chronicle events are rejected until the six new types are registered** (see above).
9. **Hook name collisions.** Oxide treats every non-public instance method as a potential hook. This plugin's helper names (`Tick`, `Seen`, `Broadcast`, …) do not match any hook in docs/oxide-rok-api.md §2.10. Their collision with other, future plugins' broadcast hooks is not checked.
