# RealmDominion: territorial war

`plugins/RealmDominion.cs` (Oxide 2.0.3867, C# 3) gives Ostreval a map worth fighting over. Named **holdings** lie across the land: villages, a keep, a mine, the harbour, the crossroads. A house takes one by holding the field with its own sworn members during the **War Hours**, keeps it against rivals, and is paid for it every day in marks and season points. Every capture is told by the herald, written into the Chronicle and drawn on a painted board.

Status: **compiles with 0 errors against the real 2.0.3867 DLLs, behaviour-tested against mocks (196 checks) and exploit-tested (37 checks, plus 13 for the treasury door its income pays through). It has never run on a live server.** See [What is UNVERIFIED](#what-is-unverified) and [Smoke test](#smoke-test-on-a-real-server).

Tags: **[ASM]** = confirmed by compiling against the 2.0.3867 `Assembly-CSharp.dll` metadata (`tools/plugin-compile-check/check.sh`). **[CODE]** = read in the decompiled DLL (member names only; no game code is kept in this repo). **UNVERIFIED** = not seen working in game.

---

## What players see

- **`/dominion`**: the War Hours (open, paused and why, or when they open next) and every holding with its holder, garrison, daily income and any banner rising on it. With popups on, the same map opens in the game's own window; the chat version is always sent.
  ```
  Dominion: The War Hours are on: holdings may be taken until 22:30 (realm time).
  Dominion: The holdings of Ostreval (7):
    The Tollbridge (crossroads) - House Varrow, garrison II, 144 marks a day - banner of Corvane rising, 45%
    Greywatch Keep (keep) - unclaimed, 150 marks a day
  ```
- **`/dominion <holding>`**: one holding in full: its lore line, holder and since when, garrison and what it costs an attacker, income and points, where it lies (centre, size, your distance), any rising banner, the last capture. Names match by id, by name with or without "The", or by a unique prefix (`/dominion ember`).
- **`/dominion here`**: the holding you stand in, who in the field counts for which house, and whether **you** count (and if not, why: no house, a house too young or small, joined too lately, new-player protection, staff, just logged in).
- **`/dominion rules`**: the rules in five lines, with the server's own numbers.
- **In the field**: walking into a holding tells you whose it is (once per 5 minutes). While a banner rises, the players inside see its progress; the holder's members anywhere are warned (`Your holding The Tollbridge is under attack by House Corvane: their banner is at 45%.`).
- **The herald**: the War Hours opening, pausing, resuming and ending; a new banner raised; a holding taken (`Herald: House Corvane takes The Tollbridge from House Varrow!`); the daily payday; a holding abandoned or left by a fallen house.
- **The Chronicle**: every capture is a `holding_taken` entry ("A Holding Taken", the keep-and-banner icon), so it reaches the stream overlay, the portal, the Discord herald and the bot.
- **The board**: a painted sign bound with `/paint dominion` (RealmPainter) shows the holdings, their houses' sigils and any rising banner.
- **RealmRenown**: each capturer present earns the `holding_taken` deed (30 renown; built in, so every server has it). A server may add a title for it in `oxide/config/RealmRenown.json`, for example `{ "Id": "marcher", "Name": "Marcher Lord", "Description": "Took five holdings for their house.", "Infamous": false, "Requires": { "holding_taken": 5 } }`.

## How war is made

Every `TickSeconds` (5 s) the plugin counts the online, living players inside each holding (a circle of `Radius` metres round its centre, `VerticalRange` 40 m up or down), by house. Only [eligible](#anti-abuse) players count, at most `MaxCountedPerHouse` (5) per house.

| In the field | What happens |
|---|---|
| One attacking house, nobody else | Its banner rises. 300 s (`CaptureSeconds`) for one man on an unclaimed holding; each extra man is 25% faster (`ExtraCapturerPercent`); each garrison level 25% slower (`GarrisonPercentPerLevel`). |
| A rival banner already up | It must be torn down to 0 first, at the same rate, before the new one rises. |
| The holder's own members and attackers together | **Contested**: no banner moves. |
| Two attacking houses that are not allies | **Contested**. |
| Attacking houses that are allies (treaty, liege and vassal, the same liege) | Not contested. The house with the most men leads; only its own men count for speed. |
| The holder's allies (treaty, liege, vassal, the same liege) | Count for **no one**: they cannot defend it, and cannot take it either (`AllowCaptureFromAllies` false). |
| Only the holder's members | They tear down any attacker's banner. |
| Nobody | A banner holds for `DecayDelaySeconds` (60), then falls by `DecayPerMinute` (10). |
| The attacking house already holds `MaxHoldingsPerHouse` (3) | Its banner does not rise ("capped"). |

At 100 the holding is taken: the holder changes, the garrison starts at 0, the herald and the Chronicle tell it, the house earns `CapturePoints` (5) season points with the honour "Took <holding>", each capturer present earns the renown deed, and the holding is **secure** for `SecureMinutes` (30): no banner rises on it.

### The War Hours

Holdings change hands only in the War Hours, set in realm time (`Windows`; realm time is CrownAndConsequences' `UtcOffsetHours` when `UseCrownUtcOffset`, else `UtcOffsetMinutes`). The defaults sit inside RealmWarden's default raid hours and clear of the rebellion windows, Crown Night and the Sunday truce:

| Day | Time | Length |
|---|---|---|
| Wednesday | 20:30 | 2 h |
| Saturday | 21:00 | 2 h |
| Sunday | 17:00 | 2 h |

Inside a window the field **pauses** (banners freeze where they stand) while:

- RealmEvents' **Truce of the Realm** holds (`PauseDuringTruce`);
- CrownAndConsequences has a **rebellion** under way (`DuringRebellion`: `pause`, the default, keeps the realm's war for the crown undivided; `open` instead opens every holding for the whole rebellion, inside or outside the War Hours; `normal` ignores rebellions);
- RealmWarden's **raid hours** are shut (`RequireRaidHours`; with RealmWarden's raid hours switched off, as they ship, this never pauses).

When the War Hours end every unfinished banner falls; holdings stay with their holders. Stewards can open the field by hand (`/dominion admin open [minutes]`, for an event or a saga beat), close it (`close [minutes]`) and hand it back to the schedule (`auto`).

### The garrison

A holding's garrison rises by one level at each payday its holder keeps it, up to `MaxGarrisonLevel` (4). Each level:

- makes a capture take `GarrisonPercentPerLevel` (25%) longer;
- adds `IncomeGarrisonPercentPerLevel` (10%) to its daily income.

While the field is open, members of the holding house **standing inside their own holding** take `GarrisonDamageReductionPercent` (10%) less damage from players of hostile houses (`GarrisonDamageReduction`). Blows from their own house or an ally, outside the holding, outside the War Hours, with no player behind them, or already cancelled by another plugin are left alone. The plugin scales `Damage.Amount` in `OnEntityHealthChange` [ASM], as RealmLegendary does; Oxide does not order the plugins' damage hooks, so the two multiply in either order.

### Payday

Once a day at `IncomeTime` (21:00 realm time) every placed, open holding pays its house:

| Kind | Marks a day (`IncomeByKind`) | Season points (`PointsPerDayByKind`) |
|---|---|---|
| village | 100 | 2 |
| keep | 150 | 3 |
| mine | 200 | 3 |
| harbour | 180 | 3 |
| crossroads | 120 | 2 |

Marks go into the **house vault** through `RealmTreasury.GrantHouseIncome` (below), points through `RealmSeasons.AwardHouse`. A holding pays only if its house took it at least `MinHeldHoursForIncome` (6) hours ago and, with `IncomeRequiresActivity`, a member of the house was online within the last day. The house's members online are told what each holding paid, and the herald sums the day up. A day is paid once: a restart after the hour pays that day; a day the server was down is not paid twice; a server clock set back never pays a day again.

**How the marks are made.** RealmTreasury's new non-public `GrantHouseIncome(house, marks, source, note)` strikes new marks straight into the house vault and counts them in `MarksMinted`, so the treasury's zero-sum audit holds. It is capped by the treasury's `MintSupplyCap` (all marks that may ever exist) and by `PluginIncomeMaxPerDay` (3000 marks per source in a rolling 24 h), refuses a house RealmHouses does not know (and everything while RealmHouses is not loaded), and honours vault lineage (a refounded house's income goes to its new vault). The seven default holdings at full garrison pay at most about 1,500 marks a day; at the default supply cap of 100,000 that leaves the cap as the realm's long-run brake: watch `/treasury` and raise `MintSupplyCap` or lower `IncomeByKind` as the economy needs.

## Anti-abuse

| Abuse | Rule | Switch |
|---|---|---|
| **Alt houses** | A house counts only with `MinHouseMembers` (2) members (offline ones count) and `MinHouseAgeHours` (24) after its founding (RealmHouses `GetHouseFounded`). | both |
| **House hopping** | A player counts only `MinMembershipHours` (12) after this plugin first saw them in that house; changing house restarts the clock. For the first 12 hours after installing, everyone already in a house counts at once. | `MinMembershipHours` |
| **Alts under protection** | Never counts while RealmWarden's new-player protection covers them (they cannot be fought). | `CountNewPlayerProtected` |
| **Staff** | Holders of `realmdominion.admin` count for no one. | `AdminsCount` |
| **Zone camping by allies** | The holder's allies count for no one: they cannot freeze an attack or hand the holding to each other. | `AllowCaptureFromAllies` |
| **Flip farming** | Within `PairCooldownHours` (24): retaking a holding the house lost, or two houses trading again after a rewarded trade, earns no rewards (the holding still changes hands). At most `MaxRewardedCapturesPerDay` (3) rewarded captures per house a day. Two colluding houses earn one reward each per window, however often they trade. | both |
| **Instant flip-back** | A fresh capture is secure for `SecureMinutes` (30). | `SecureMinutes` |
| **Hoarding** | `MaxHoldingsPerHouse` (3). | |
| **Capture during truces** | No banner moves in the Truce of the Realm, a paused rebellion or shut raid hours. | `PauseDuringTruce`, `DuringRebellion`, `RequireRaidHours` |
| **Pre-capture** | Banners fall when the War Hours end; nothing is finished outside them. | |
| **Log-off holding** | Only online, living players count; sleepers never do. A player who logs in inside a holding counts after `ReconnectGraceSeconds` (60), so logging off to dodge a fight and back in to finish gains nothing (the banner falls meanwhile). | `ReconnectGraceSeconds` |
| **Holding without playing** | No income without a member online in the last day; the holding returns to no one after `AbandonAfterDays` (3). A house that is disbanded, or refounded under the same name, loses its holdings. | `IncomeRequiresActivity`, `AbandonAfterDays` |
| **Payday sniping** | No income for a holding taken under `MinHeldHoursForIncome` (6) hours ago. | |
| **Sky towers and tunnels** | Only `VerticalRange` (40 m) above or below the centre counts. | |
| **Hostile names** | House and holding names are cleaned of colour tags, braces and line breaks before they reach a chat line, a popup or a sign. | |

Each rule has a regression test in `tools/exploit-review/dominion` (D1 to D22, listed in `tools/exploit-review/README.md`).

## Setting it up (owner)

1. Deploy `RealmDominion.cs` with the other plugins. On first load it writes `oxide/config/RealmDominion.json` and seeds `oxide/data/RealmDominion.json` with the seven holdings of [`docs/saga/locations.md`](../../docs/saga/locations.md), **unmarked**: no code knows the real map, so none can be taken yet.
2. Grant yourself `realmdominion.admin` (`oxide.grant user <you> realmdominion.admin`; staff page: `docs/community/ops/staff-roles-and-permissions.md`).
3. Walk to each place and stand at its centre:

   | Id | Holding | Kind | Place it at |
   |---|---|---|---|
   | `tollbridge` | The Tollbridge | crossroads | the bridge or crossing over the largest river near the throne (L04) |
   | `greywatch` | Greywatch Keep | keep | high ground on the road between Varrow's Stair and Ashgrove Vale; build a ruined keep there if none stands |
   | `ember-mines` | The Ember Mines | mine | rock walls of the southern pass (L11), where ore can be mined |
   | `drowned-harbour` | The Drowned Harbour | harbour | the lakeshore or coast of Drowned Dunmere (L10) |
   | `ferrymans-rest` | Ferryman's Rest | village | below Merrin's Ford (L12) |
   | `wold-lodge` | The Huntsman's Lodge | village | the edge of the Crown Wold (L06) |
   | `scarred-orchards` | The Scarred Orchards | village | the farmland of Ashgrove Vale (L08) |

   Then `/dominion admin move <id> [radius]` (radius in metres, 10 to 150; the defaults are 35 to 45). Keep holdings away from the law towns (Crown Market, Hearth) and from the house seats themselves, so a fight for a holding is never a fight inside someone's base.
4. Add your own with `/dominion admin create <id> <kind> [radius] <name>` where you stand, and `/paint dominion` on a sign to put up the live board.
5. Check `/dominion admin status` and `/dominion`.

## Commands

Players: `/dominion`, `/dominion <holding>`, `/dominion here`, `/dominion rules` (in the `/realm` hub under Houses and oaths).

Staff (`realmdominion.admin`), all under `/dominion admin`:

| Command | Does |
|---|---|
| `status` | Field state and why, holdings marked and held, last payday, whether RealmTreasury and RealmHouses answer, realm time, next War Hours. |
| `create <id> <kind> [radius] <name>` | A new holding centred where you stand. Ids are 2 to 24 letters, digits or dashes. At most `MaxHoldings` (30). |
| `move <id> [radius]` | Moves a holding to where you stand (and marks it). Clears any banner. |
| `radius <id> <m>` / `rename <id> <name>` | |
| `remove <id>` then `remove <id> confirm` | Forgets a holding and its holder. |
| `enable <id>` / `disable <id>` | A disabled holding cannot be fought for and pays no one. |
| `owner <id> <house or none>` | Settles a holder by hand (a saga beat, a dispute). |
| `reset <id>` | Clears banners and the secure time. |
| `open [minutes]` / `close [minutes]` / `auto` | Opens (default 60) or closes (default 120) the field by hand; `auto` returns to the schedule. |
| `payday` | Makes today's payday now if it has not been made. |

## Config (`oxide/config/RealmDominion.json`)

Every value is clamped on load; missing tables come back with their defaults.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | false: nothing is counted, captured or paid; players are told it is off |
| `TickSeconds` | 5 | field count interval (1 to 30) |
| `Windows` | Wed 20:30, Sat 21:00, Sun 17:00, 120 min | `Days` (names, 3-letter forms, or `daily`), `Start` (HH:mm realm time), `DurationMinutes` |
| `UseCrownUtcOffset` / `UtcOffsetMinutes` | true / 0 | realm time |
| `DuringRebellion` | pause | pause, open or normal (above) |
| `PauseDuringTruce` / `RequireRaidHours` | true / true | |
| `CaptureSeconds`, `ExtraCapturerPercent`, `MaxCountedPerHouse`, `MinCapturers` | 300, 25, 5, 1 | capture speed |
| `DecayPerMinute`, `DecayDelaySeconds` | 10, 60 | an abandoned banner |
| `SecureMinutes` | 30 | |
| `GarrisonPercentPerLevel`, `MaxGarrisonLevel` | 25, 4 | |
| `GarrisonDamageReduction`, `GarrisonDamageReductionPercent` | true, 10 (max 50) | |
| `VerticalRange` | 40 | |
| `MinHouseMembers`, `MinHouseAgeHours`, `MinMembershipHours`, `MaxHoldingsPerHouse` | 2, 24, 12, 3 | |
| `AllowCaptureFromAllies`, `CountNewPlayerProtected`, `AdminsCount` | false, false, false | |
| `ReconnectGraceSeconds` | 60 | |
| `PairCooldownHours`, `MaxRewardedCapturesPerDay` | 24, 3 | |
| `AbandonAfterDays`, `IncomeRequiresActivity` | 3, true | 0 days = never abandoned |
| `IncomeTime`, `MinHeldHoursForIncome` | 21:00, 6 | |
| `IncomeByKind`, `IncomeGarrisonPercentPerLevel`, `PointsPerDayByKind` | table above, 10 | |
| `CapturePoints`, `RenownDeed` | 5, `holding_taken` | `""` = no renown deed |
| `AnnounceCaptures`, `AnnounceWindows`, `ShowEnterNotices`, `AnnounceCooldownSeconds` | true, true, true, 120 | herald and notices |
| `UsePopups` | true | the `/dominion` window (a player turns all Realm windows off with `/realm popups off`) |
| `PaintBoards`, `PublishMap` | true, true | RealmPainter refresh, the map file |
| `MaxHoldings`, `MinRadius`, `MaxRadius` | 30, 10, 150 | |

## Map file (`oxide/data/RealmDominionMap.json`)

Written for the Chronicle service, the portal and the bot whenever a holder, a banner or the field changes (at most every 15 s), and on unload. Read only for them; the plugin never reads it back. Times are ISO 8601 UTC.

```json
{
  "schema": 1,
  "generated": "2026-10-07T20:41:05Z",
  "window": { "state": "open", "reason": "window", "until": "2026-10-07T22:30:00Z", "nextOpen": "2026-10-10T21:00:00Z" },
  "holdings": [
    {
      "id": "tollbridge", "name": "The Tollbridge", "kind": "crossroads",
      "place": "The Builders' bridge over the great river, where every monarch claims the toll.",
      "placed": true, "enabled": true, "x": 1520.5, "z": -330.2, "radius": 35,
      "owner": "Varrow", "ownerSince": "2026-10-05T21:14:40Z", "garrison": 2,
      "incomePerDay": 144, "pointsPerDay": 2,
      "state": "capturing", "capturer": "Corvane", "progress": 45,
      "secureUntil": null, "captures": 3, "lastTaken": "2026-10-05T21:14:40Z"
    }
  ],
  "houses": [ { "name": "Varrow", "holdings": 2, "incomePerDay": 294, "incomeTotal": 1830 } ]
}
```

| Field | Meaning |
|---|---|
| `window.state` | `open`, `paused` (inside the War Hours but frozen) or `closed` |
| `window.reason` | open: `window`, `forced`, `rebellion`; paused: `truce`, `rebellion`, `raid`; closed: `none`, `stewards`, `off` |
| `window.until` | open/paused: when it closes; closed by stewards: when they reopen; closed: the next window (as `nextOpen`) |
| `holdings[].x`, `z`, `radius` | world position in metres (the game's x and z); `placed` false = not on the land yet |
| `holdings[].state` | `quiet`, `capturing`, `contested`, `defending`, `secured`, `capped` |
| `holdings[].capturer`, `progress` | the house whose banner is rising and its percent (0 to 100) |
| `houses[]` | each holding house: holdings, today's income (placed, open holdings) and all marks paid so far |

The holder's internal record (garrison, capture log, membership clocks) is in `oxide/data/RealmDominion.json`; other tools should read the map file, whose shape is the contract.

## Integration

| Calls | Why |
|---|---|
| `RealmHouses.GetHouse`, `GetMembers`, `GetHouseFounded`, `GetLiege`, `HasTreaty`, `GetHouseSummaries` | houses, eligibility, alliances, lineage, canonical names |
| `CrownAndConsequences.IsRebellionActive`, `GetUtcOffsetHours` | rebellion pauses, realm time |
| `RealmEvents.IsTruceActive` | truce pauses |
| `RealmWarden.IsRaidHourNow`, `IsNewPlayerProtected` | raid hours, protected players |
| `RealmChronicle.Log("holding_taken", ...)` | the capture record |
| `RealmSeasons.AwardHouse` | capture and daily points |
| `RealmRenown.AddDeed(id, name, "holding_taken", note, key)` | renown per capturer (dedupe key `dominion:<holding>:<seq>:<player>`) |
| `RealmTreasury.GrantHouseIncome` | daily marks into the vault |
| `RealmPainter.RefreshBoards("dominion")` | redraw the board (at most every 30 s) |
| `RealmHerald.PopupsWanted` | the player's popup choice |

API for other plugins (non-public, `plugin.Call`): `GetHoldings()` -> list of `{id, name, kind, owner, state, capturer, progress, garrison, income, placed}`; `GetHoldingOwner(string id)` -> house or null; `GetHouseHoldingCount(string house)` -> int; `GetDominionWindow()` -> one plain-text line; `GetDominionBoard()` -> what RealmPainter's board draws.

New shared pieces this plugin brought: the Chronicle type `holding_taken` (in `RealmChronicle.cs`, `chronicle/server.js`, `common.js`, the portal model, the Discord herald, the launcher heraldry, the art event map with its icon `art/icons/holding.svg` and PNGs, and the synced copies); `RealmTreasury.GrantHouseIncome` with `PluginIncomeMaxPerDay`; the `holding_taken` deed in RealmRenown; RealmPainter's `dominion` board; `/dominion` in RealmHerald's hub.

## Data

`oxide/data/RealmDominion.json`: holdings (place, holder, garrison, banner, history), membership clocks, house activity, the capture log (kept 48 h or `PairCooldownHours` + 24 h), marks paid per house, the last payday, forced open/close times. Saved every minute when changed, at once on a capture or an admin change, on server save and on unload. **If the file exists but cannot be read (damaged, truncated, `null`, or an empty object), the plugin pauses, says so to players and in the log, and never writes it** until it is fixed or moved away.

## Tests

- `plugins/docs/RealmDominion/logic-tests/run.sh`: 196 checks against mocks (`Mocks.cs`, the test world `World.cs`): seeding, the War Hours and every pause, capture, crowd and garrison speed, contest and ally rules, banners and decay, eligibility, payday, abandonment and lineage, the damage shield, every player and admin command, chat style, popups, the map file and API, damaged data and clamped config, switches.
- `tools/exploit-review/run.sh dominion`: 37 abuse scenarios (D1 to D22) and 13 checks of `GrantHouseIncome` against the real RealmTreasury with its mocks (vault, audit, per-source budget, supply cap, unknown and fallen houses).
- `tools/plugin-compile-check/check.sh` and `node tools/realm-integration/check.mjs` cover compilation at C# 3 against the real DLLs and every cross-plugin call, the Chronicle type, the command and the chat style.

What the tests do not prove: that the game behaves like the mocks.

## Smoke test on a real server

Two accounts (A, B) in two houses founded more than a day ago with two members each, or lower `MinHouseAgeHours`, `MinHouseMembers` and `MinMembershipHours` to 0 / 1 / 0 for the test. Grant yourself `realmdominion.admin`.

| Step | Do | Expect |
|---|---|---|
| D1 | Load the plugin. | Log: "7 holdings are not marked on the land yet". `oxide/data/RealmDominion.json` and the config exist. |
| D2 | Stand somewhere, `/dominion admin move tollbridge 20`, then `/dominion here`. | "You stand in The Tollbridge"; your position is read (`Entity.Position`). Walk 25 m out: "You stand in no holding". |
| D3 | `/dominion` | The map in chat and, with popups on, in a window. |
| D4 | `/dominion admin open 30`; A stands in the Tollbridge. | Herald opens the field. A sees the banner rise every ~30 s; after 5 min "House A takes The Tollbridge!". The Chronicle has `holding_taken`; `/season house A` shows 5 points; `/renown` shows the deed. |
| D5 | After `SecureMinutes` (or `admin reset tollbridge`), B enters while A stands there. | "contested"; no banner moves. A leaves: B's banner rises. |
| D6 | B logs off inside at 50%, waits 2 min, logs back in. | The banner fell meanwhile; B counts again only after 60 s. |
| D7 | During the open field, B hits A inside A's holding with a known weapon; then outside it. | Inside, A loses about 10% less health than outside (Damage.Amount scaling). |
| D8 | `/dominion admin payday` with A's house holding the Tollbridge for 6 h (or set `MinHeldHoursForIncome` 0). | A's members are told the payment; `/vault` shows the marks; `/treasury audit` is balanced. |
| D9 | `/paint dominion` on a sign. | The board shows the holdings with A's sigil; it redraws within ~30 s of a capture. |
| D10 | Start a RealmEvents truce (`/event start truce`) during an open field. | Herald: "The War Hours pause: the Truce of the Realm holds". Banners freeze. |
| D11 | Check `oxide/data/RealmDominionMap.json`. | The schema above, with live values. |
| D12 | Stop the server, cut `RealmDominion.json` in half, start. | The plugin refuses and logs; `/dominion` says it is paused; the file is unchanged. |

## What is UNVERIFIED

Nothing has run on a live server. In particular:

- That `Player.Entity.Position` is the player's live position on the dedicated server (as RealmWarden and RealmSentinel also assume) and that `PlayerExtensions.IsAlive` is false for a dead player awaiting respawn (D2, D6).
- That a server-side scale of `Damage.Amount` in `OnEntityHealthChange` is what the victim loses (D7; RealmLegendary depends on the same).
- That the popup window shows on a client and how much of the map fits (D3).
- That `Server.ClientPlayers` lists only connected players, so sleepers never count (D6).
- The default numbers (capture time, radii, income, garrison) are guesses until a weekend of play tunes them.
- That the timings of the herald and field notices read well in a real fight (D4, D5).

## Follow-ups

- **Portal and Chronicle map section** (owned by the web team): read `RealmDominionMap.json`, draw the holdings on a map page and in `/api/state`; the Discord bot can add `/realm holdings`.
- **Badge for a holdings title**: add a default RealmRenown title for `holding_taken` together with its badge in `art/src/titles.json` (the art test requires one per default title).
- **Steward**: list `/dominion admin` in `launcher/lib/moderation.js` (STW-2).
- **Labels**: `bot/src/text.js`, `streamkit/public/assets/kit.js` and `launcher/lib/shared/notify.js` carry their own short labels for some types; `holding_taken` falls back to their defaults until added.
- Optional: sign art for each holding under `art/paintings/dominion/` (a holding banner painted at the site) once RealmPainter is proven in game.
