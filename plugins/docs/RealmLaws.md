# RealmLaws: the laws of Ostreval and the crown's court

`plugins/RealmLaws.cs` (Oxide 2.0.3867, C# 3). It gives the crown a **law book** and the realm a **court**:

- The reigning monarch **proclaims** up to `MaxActiveLaws` laws (default 3) from a catalogue kept in the config: the King's Peace in the Crown Market, a curfew, a ban on building in towns, and so on.
- A law the game can observe is **enforced**. Breaking it writes a record in the **crime ledger** through a verified hook, and some laws also **stop the act**. A law the game cannot observe, such as banned weapons or a bridge toll, is **declared**: it is only a crime when someone is accused of it in court.
- The monarch or the **council** brings an **accusation**. A **jury of sworn lords** (online heads of houses not party to the case) judges it with `/court verdict`. The accused may instead demand **trial by combat**, whose outcome is read only from the game's death hook.
- Sentences are a **fine** (real items taken from the convict's inventory), **outlawry** or **exile** from the towns. All are capped.
- **Abuse limits** keep the crown from persecuting anyone endlessly (see [Abuse limits](#abuse-limits)).

Lore: the Hearth Charter says the crown's power is *lent, not owned*. The Charter lets the monarch make law, but only a few laws at a time and slowly. The lords judge, not the crown. A lord acquitted by his peers cannot be dragged back at once.

Status: **compile-checked against the real 2.0.3867 DLLs and behaviour-tested against mocks (90 checks). Never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

---

## Commands

Names with spaces may be typed plainly (`/court accuse Old Tom kings_peace`) or in double quotes. The server's chat parser supports quoted arguments.

### `/laws` and `/law`

| Command | Who | What it does |
|---|---|---|
| `/laws` or `/law help` | anyone | Shows the help for both `/law` and `/court`. |
| `/law` or `/law list` | anyone | Lists the laws in force and who proclaimed them. A law in its grace period shows "binds in N min". |
| `/law catalogue` | anyone | Lists every law the crown may proclaim, with its kind and zone. |
| `/law info <id>` | anyone | Shows the law's text, kind, zone, whether it blocks the act, and its default sentence. |
| `/law proclaim <id>` | monarch (or admin) | Proclaims a law. It binds after `LawGraceMinutes`. This is broadcast and written to the Chronicle. Checks: active-law cap, `LawProclaimCooldownMinutes`, `LawChangesPerDay`, and the law's zone must exist. Admins bypass the cooldown and the daily cap, but not the active-law cap. |
| `/law repeal <id>` | monarch (or admin) | Repeals a law. There is no cooldown: repealing reduces the crown's power. It still counts toward `LawChangesPerDay`. |
| `/law crimes [player]` | anyone | Shows the newest crime records (your own, or the named player's). The ledger is public. Each line shows `(case #N)` if the record was charged. |
| `/law zone list` | anyone | Lists the zones (centre x/z, radius, town flag). |
| `/law zone set <name> <radius> [town]` | admin | Sets (or creates) the zone **centred where you stand**, then saves it to the config file. Use `town` for zones exiles may not enter and for laws whose zone is `*`. |
| `/law zone remove <name>` | admin | Removes a zone. Laws that name it stop being enforced. |

### `/court`

| Command | Who | What it does |
|---|---|---|
| `/court cases` | anyone | Lists the open cases. |
| `/court case <id>` | anyone | Shows a case: parties, law, sentence sought, evidence count, jury size, number of votes cast (ballots are secret), combat window, outcome, and any unpaid fine. |
| `/court accuse <player> <law> [sentence]` | monarch, council member, admin | Brings a case. The sentence is `fine <n> "<item>"`, `outlaw <hours>` or `exile <hours>`. If it is left out, the law's default sentence is used. Every uncharged ledger record of that law against the player is attached as evidence. See the checks under [Abuse limits](#abuse-limits). The player may be offline if the court has a record of them by name. |
| `/court trial <case>` | monarch, council, **the accused**, admin | Seats a jury. By default the accused must be online. If fewer than `JuryMin` eligible lords are online, the trial is refused. |
| `/court verdict <case> guilty\|innocent` | sworn jurors | Casts a secret vote. Synonyms: `g`/`i`/`yes`/`no`. |
| `/court combat <case>` | the accused | Demands **trial by combat** once per case, before any juror has voted. The crown's champion must be online. |
| `/court champion <case> <player>` | the accuser (or admin) | Names a champion for the crown once per case. The default champion is the accuser. The champion cannot be the accused, a member of the accused's house, a juror or an outlaw. |
| `/court pay <case>` | the convict | Pays the rest of a fine from your inventory. It takes as much as you carry, up to what is owed. |
| `/court collect` | a victim | Collects fines paid to you (only with `"FineDestination": "victim"`). |
| `/court outlaws` (or `exiles`) | anyone | Lists current court outlaws and exiles, with expiry and case. |
| `/court pardon <player>` | monarch (or admin) | Lifts the court's outlawry, exile and unpaid fines on a player. Limited to `PardonsPerDay`. |
| `/court admin dismiss <case>` | admin | Dismisses an open case. |
| `/court admin verdict <case> guilty\|innocent` | admin | Rules on an open case directly. Use this for testing or disputes. |
| `/court admin clear <player>` | admin | Removes all sentences and dismisses all open cases of a player. |

Admin permission: **`realmlaws.admin`** (Oxide permission). Grant it in the server console with `oxide.grant user <name|id> realmlaws.admin`.

---

## How it works

### Laws: enforced and declared

Each catalogue entry has a `Kind`:

| Kind | What is observed | Real effect | Verified by |
|---|---|---|---|
| `peace` | A player damages another player while the victim, or else the attacker, stands in the zone. | A crime record for the attacker, at most one per `CrimeCooldownSeconds`. With `"Block": true` (default) the blow is stayed: `evt.Cancel()`, `Damage.Amount = 0` and the hook returns non-null. | `OnEntityHealthChange` [OPJ L162]. [IL] `EntityHealth.InvokeDamage` calls the hook and returns before invoking its `OnDamage` handler when the hook returns non-null. Members of `Damage` and `EntityDamageEvent` [ASM]. |
| `curfew` | A player stands in the zone between `CurfewStartHourUtc` and `CurfewEndHourUtc` (UTC wall clock; wraps past midnight). | A warning, then a crime record if the player is still inside after `CurfewGraceSeconds`. It cannot remove anyone. | Polling `Server.ClientPlayers` + `Entity.Position` every `ZoneCheckSeconds` [ASM]. |
| `no_building` | A cube is placed inside the zone. | A crime record. With `"Block": true` (default) the placement is cancelled. Admins, and (by default) members of the crown's house, are exempt. | `OnCubePlacement` [OPJ L266]. [IL] `CubeListener.OnCubePlace` calls the hook first, identifies the placer by `NetworkEvent.SenderId` for its own crest check, reads `Cancelled` afterwards, and maps the cube to world space with `Grid.LocalToWorldCoordinate(Position)`. The plugin uses the same calls. |
| `no_capture` | A player is roped, chained or caged while the target stands in the zone. Taking a court outlaw is lawful. | A crime record for the captor. `"Block": true` would also cancel the capture, but it is **off by default** (see the note below). | `OnPlayerCapture` [OPJ L711]; `Captor`, `Target` [ASM]. |
| `declared` | Nothing. | None until an accusation; the crime exists only in court. | None needed. |

`Zone` is a zone name, or `*` for every zone flagged `Town`. A law whose zone does not exist cannot be proclaimed. If its zone is removed later, it is no longer enforced.

**Why `no_capture` does not block by default.** CrownAndConsequences also handles `OnPlayerCapture`, to bound ransoms. Oxide calls plugins in load order. If CrownAndConsequences runs first, it records the capture before RealmLaws cancels it, which leaves a captivity record for a capture that never happened. CrownAndConsequences skips events that are already cancelled, so the problem only appears in that order. Turn `Block` on only after testing it on your server (smoke test L7).

**Exemptions from the King's Peace:**
- the two duellists of an open trial by combat, for blows between each other;
- court outlaws and exiles, who lose the peace's protection (`OutlawsAndExilesLosePeace`);
- everyone, while CrownAndConsequences reports a rebellion window open (`PeaceSuspendedDuringRebellion`).

### The crime ledger

Records hold the offender, law, time, a short detail ("struck Dora", "in Crown Market after curfew") and the victim if any. They expire after `CrimeRecordDays`, except records attached to an open case. The ledger is capped at `MaxCrimeRecords`.

When a case ends without conviction (acquitted, dismissed, expired), its records stay on the ledger but are marked as tried. **They can never be cited again**, so no one is tried twice on the same evidence.

### The court

```
accuse ──► accused ──/court trial──► trial (jury votes) ──► guilty ──► sentence
             │  ▲                        │  mistrial (too few votes): back to accused, at most MaxTrialAttempts
             │  └── combat window ends without blood (CombatTimeoutResult = jury)
             └──/court combat──► combat ──► champion kills accused: guilty
                                          ├► accused kills champion: acquitted
                                          └► a duellist disconnects: that side loses (CombatFleeLoses)
          accused ──(CaseExpiryHours without a trial)──► expired
```

**Jury.** Up to `JurySize` jurors (default 5) are drawn at random from online players who meet all of these:
- head of a house (RealmHouses leader; without RealmHouses, the game guild owner), unless `JurorsMustLeadHouse` is false;
- not the accused, the accuser, the champion or the victim;
- not the monarch or a council member (they serve the prosecution);
- not of the accused's house;
- not of the crown's house (`ExcludeCrownHouseFromJury`, default on);
- not a court outlaw or exile.

The trial needs at least `JuryMin` jurors (default 3) and closes when all have voted or after `TrialMinutes`. The verdict is the majority of votes cast, and **a tie acquits**. Fewer than `MinVotes` votes is a mistrial.

**Trial by combat.** The accused demands it once per case, before any vote. Both duellists must be online. The window is `CombatWindowMinutes`, and only a kill between the two duellists decides it. The killer is read from the death hook: `EntityDeathEvent.KillingDamage.DamageSource.Owner` [OPJ L188; IL; USE DeathMessages.cs:20]. If the window ends without a kill, `CombatTimeoutResult` applies: `jury` (default: back to a jury trial), `acquit` or `guilty`.

### Sentences

| Sentence | Effect |
|---|---|
| `fine <n> <item>` | On the verdict, if the convict is online, the plugin takes as many of the item as they carry, up to *n*. It uses `ItemCollection.AutoCount`/`AutoSplit`, measures before and after, and never trusts the requested number. This is the same pattern as RealmContracts [IL `StationListener.OnStationUpgradeRequest`]. The rest is owed, and the convict pays it with `/court pay` within `FinePayHours`. Unpaid at the deadline means outlawry for `UnpaidFineOutlawHours` (0 = it just stays on the record). With `"FineDestination": "burn"` (default) the items leave the realm, so the crown gains nothing from convictions. With `"victim"` they go to the victim of the cited crime: paid at once if there is room, otherwise through `/court collect` or automatically when the victim next connects. |
| `outlaw <hours>` | A court outlaw: listed in `/court outlaws`, not sheltered by the King's Peace, lawful to capture under `no_capture`, and barred from juries. Outlawry never shortens an existing longer term and replaces exile. The plugin also offers it to RealmContracts (see [Integration](#integration)). |
| `exile <hours>` | The exile may not enter any **town** zone. Inside one, they get a warning. Still inside after `ExileBreachGraceSeconds`, the breach is recorded and they are outlawed for `ExileBreachOutlawHours`. Exiles also lose the King's Peace. |

### Abuse limits

| Limit | Default |
|---|---|
| Laws in force at once | `MaxActiveLaws` 3 |
| Time between two proclamations | `LawProclaimCooldownMinutes` 30 |
| Law changes (proclaim and repeal) per 24 h | `LawChangesPerDay` 4 |
| Grace before a new law binds | `LawGraceMinutes` 5 |
| Accusations by the whole crown (monarch and council) per 24 h | `CrownAccusationsPerDay` 6. **Each acquittal in the last 24 h counts as `AcquittalPenalty` extra accusations.** |
| Accusations per accuser per 24 h | `AccusationsPerAccuserPerDay` 3 |
| Accusations against one player per 7 days | `AccusationsPerTargetPerWeek` 2 |
| Open cases per player | 1 |
| Open cases in total | `MaxOpenCases` 20 |
| No new accusation after an acquittal | `AcquittalImmunityHours` 24 |
| The monarch cannot be accused | `MonarchImmune` true |
| No accusation of a law that is neither in force nor on the ledger for that player | always |
| Accusation of an enforced law without ledger evidence | allowed. Set `RequireEvidenceForEnforcedLaws` to forbid it. |
| Sentence caps | `MaxFineAmount` 200, `MaxOutlawHours` 48, `MaxExileHours` 48; `AllowedFineItems` (empty = any item) |
| Pardons per 24 h | `PardonsPerDay` 2 |
| Succession | laws lapse (`LawsLapseOnSuccession`), and open cases brought by the fallen crown are dismissed (`DismissCrownCasesOnSuccession`). Admin cases stay. A vacant throne changes nothing. |
| Chronicle lines per hour | `ChronicleMaxPerHour` 15 |
| Records per offender and law | one per `CrimeCooldownSeconds` (120 s) |

Admins bypass the crown quotas, but their accusations still go through the jury.

### Data and safety

- Data: `oxide/data/RealmLaws.json`. It is saved after every item movement and verdict, otherwise on the 10-second tick when something changed, on server save and on unload.
- **Corruption-safe load.** The file may exist but fail to parse (or be empty or `null`). In that case the plugin logs an error, **closes the court** (every `/law`/`/court` command answers that the records are damaged; hooks do nothing) and **never writes the file**. Fix or remove it, then `oxide.reload RealmLaws`.
- Config: `oxide/config/RealmLaws.json`, written by `LoadDefaultConfig` on first load. Every number is clamped to a sane range on load. Unknown law kinds become `declared` and duplicate law ids are dropped, both with a warning.
- Crash safety for fines: the take is measured and saved immediately. The failure mode of a crash between the game call and the save is a lost fine, never duplicated items.

---

## Configuration (`oxide/config/RealmLaws.json`)

All keys and defaults are listed in `PluginConfig` in the source, and the abuse-limit keys are in the table above. The others are:

| Key | Default | Meaning |
|---|---|---|
| `PeaceSuspendedDuringRebellion` | true | No peace blocking while CrownAndConsequences reports a rebellion window. |
| `OutlawsAndExilesLosePeace` | true | See above. |
| `CrownHouseMayBuildInZones` | true | Members of the crown's house are exempt from `no_building`. |
| `ZoneCheckSeconds` | 10 | Position polling for curfew and exile. |
| `CurfewGraceSeconds` / `ExileBreachGraceSeconds` | 60 / 60 | Warning before a record is written. |
| `CrimeRecordDays` / `MaxCrimeRecords` | 7 / 500 | Ledger retention. |
| `CaseExpiryHours` | 48 | An accusation not tried in time expires. |
| `MaxClosedCases` | 100 | Closed cases kept for `/court case`. |
| `JurySize` / `JuryMin` / `MinVotes` / `TrialMinutes` / `MaxTrialAttempts` | 5 / 3 / 2 / 10 / 2 | Jury settings. |
| `JurorsMustLeadHouse` | true | Only heads of houses sit. Set false on a small server so any house member may sit. |
| `TrialRequiresAccusedOnline` | true | No trial in absentia. |
| `TrialByCombat` / `CombatWindowMinutes` / `CombatTimeoutResult` / `CombatFleeLoses` | true / 15 / `jury` / true | Trial by combat. |
| `FineDestination` | `burn` | `burn` or `victim`. |
| `FinePayHours` / `UnpaidFineOutlawHours` | 24 / 12 | Unpaid fines. |
| `OfferOutlawryToContracts` | true | Calls RealmContracts (see below). |
| `MaxListLines` | 12 | Chat output cap. |
| `Zones` | Crown Market (0,0) r60 town; Hearth (0,150) r40 town | **Placeholder coordinates.** Set them in game with `/law zone set`. |
| `Catalogue` | 7 laws (below) | Each entry has `Id`, `Name`, `Text`, `Kind`, `Zone`, `CurfewStartHourUtc`, `CurfewEndHourUtc`, `Block`, `DefaultSentence` (`fine`/`outlaw`/`exile`), `DefaultAmount`, `DefaultItem`. |

The default catalogue:

| Id | Name | Kind | Zone | Block | Default sentence |
|---|---|---|---|---|---|
| `kings_peace` | The King's Peace | peace | Crown Market | yes | fine 20 Wood |
| `market_curfew` | Curfew of the Market | curfew 22-6 UTC | Crown Market | n/a | fine 10 Wood |
| `no_building_towns` | The Builder's Reserve | no_building | all towns | yes | fine 30 Stone |
| `no_binding_towns` | Free Streets | no_capture | all towns | **no** | exile 12 h |
| `banned_weapons` | Edict of Sheathed Steel | declared | n/a | n/a | fine 25 Wood |
| `bridge_toll` | The Bridge Toll | declared | n/a | n/a | fine 10 Wood |
| `harbouring` | Harbouring Outlaws | declared | n/a | n/a | outlaw 12 h |

"Banned weapons" is declared because nothing verified identifies the weapon behind a blow. `Damage.Damager` is a `UnityEngine.Object` whose meaning was not traced.

Item names (`DefaultItem`, fines) resolve with `InvBlueprints.GetBlueprintForName(name, true, true)`, an exact, case-insensitive match [ASM; IL]. `Wood` and `Stone` are `ResourceType` names [ASM]. **UNVERIFIED** that they resolve to the inventory items players carry; check on your server with a test fine (smoke test L5).

---

## Integration

### Chronicle event types

> **Registered.** These types are now in `plugins/RealmChronicle.cs` `KnownTypes`, `chronicle/server.js` `EVENT_TYPES` and `chronicle/public/assets/common.js` `TYPE_META` (a chronicle test keeps the three in step). The fallback described here only applies to an older RealmChronicle.

They are:

| type | label | icon |
|---|---|---|
| `law_proclaimed` | A Law Proclaimed | scroll |
| `law_repealed` | Law Repealed | scrollX |
| `accusation` | Accused | flag |
| `trial_by_combat` | Trial by Combat | swords |
| `verdict` | Verdict | seal |
| `pardon` | Pardoned | chainX |

Until they are added to `plugins/RealmChronicle.cs` `KnownTypes`, `chronicle/server.js` `EVENT_TYPES` and `chronicle/public/assets/common.js` `TYPE_META`, RealmChronicle rejects them (it returns 0 and logs a warning). RealmLaws then re-sends the same line as a `decree`, so nothing is lost. Once a type has been accepted, it is never downgraded.

### Calls this plugin makes (all optional; a missing plugin or method returns null)

| Plugin | Call | Used for |
|---|---|---|
| RealmChronicle | `Log(type, title, detail, actors)` | Chronicle lines |
| RealmHouses | `GetHouse(playerId)`, `GetHouseLeader(house)` | Jury eligibility, house exclusions, crown-house exemption. Without it, the game guild (`GuildScheme.TryGetGuildByMember`, `Guild.OwnerId`) is used. |
| CrownAndConsequences | `GetCouncilSeat(ulong)`, `GetKingHouse()`, `IsRebellionActive()` | Council accusations and jury exclusion; crown house; suspending the peace during rebellions |
| RealmContracts | `ProclaimOutlaw(string playerId, string name, int hours, string by)` → `bool`; `PardonOutlaw(string playerId)` | **Wired.** RealmContracts implements both (non-public). A court sentence of outlawry is mirrored into RealmContracts' outlaw list (marked as placed by the court), so while a monarch reigns the outlaw is a public enemy and bounties can be posted on them. The crown's proclamation cooldowns and `MaxOutlaws` cap do not apply to court sentences; an existing sentence is never shortened. A court pardon lifts only court-placed outlawry there and withdraws open bounties, like a crown pardon. UNVERIFIED in game (static check: `tools/realm-integration/check.mjs`). |

### API for other plugins (`plugin.Call`, all non-public)

| Method | Returns |
|---|---|
| `IsCourtOutlaw(string playerId)` | `bool` |
| `IsExiled(string playerId)` | `bool` |
| `GetCourtOutlaws()` | `string[]` of `id\|name\|untilIso\|caseId` |
| `GetActiveLaws()` | `string[]` of `id\|name\|kind` (including laws still in their grace period) |
| `GetCrimeCount(string playerId)` | `int` (records on the ledger) |

---

## Tests run here

1. **Compile check against the real DLLs:** `tools/plugin-compile-check/check.sh`. This compiles every `plugins/*.cs` at C# 3 against the shipped Oxide.ReignOfKings 2.0.3867 `Assembly-CSharp.dll` and `Oxide.*.dll` (zip sha256 `6c35c623…c6c8`). Result: **OK, 0 errors.** The compiled `plugins.dll` was inspected: RealmLaws declares no public method.
2. **Behaviour tests:** `plugins/docs/RealmLaws/logic-tests/run.sh`. This compiles the unchanged `plugins/RealmLaws.cs` together with `Mocks.cs`, a minimal stand-in for the game and Oxide types it touches, and runs `Tests.cs`. Result: **90 passed, 0 failed.** Covered:
   - damaged and empty data files: court closed, file never overwritten;
   - proclamation rights, cooldown, cap and admin bypass;
   - peace blocking, record cooldown, out-of-zone, outlaws and duellists unprotected;
   - building block and crown-house exemption;
   - curfew warn-then-record;
   - accusation rights, monarch immunity, one case per target, sentence caps, declared-law rule;
   - jury composition and exclusions, secret votes, majority and tie, mistrial twice then dismissed;
   - measured partial fine, `/court pay`, victim payout with a full inventory, then `/court collect`;
   - acquittal immunity, per-accuser and crown quotas including the acquittal penalty;
   - trial by combat: champion kill, flight on disconnect, timeout back to jury;
   - exile breach leading to outlawry, unpaid fine leading to outlawry, pardons and their quota;
   - succession lapse and case dismissal, vacant throne;
   - Chronicle fallback to `decree`, RealmContracts offer, data round trip, zone set from position.

   These tests prove the plugin's own logic. They do not prove that the game behaves like the mocks. They also serialize with System.Text.Json, not Oxide's Newtonsoft.
3. **IL reads** of the shipped patched `Assembly-CSharp.dll`, with a small System.Reflection.Metadata call-site dumper: `CubeListener.OnCubePlace`, `EntityHealth.InvokeDamage`, `EntityHealth.InvokeDeath` and `PlayerCaptureManager.OnCaptureEvent`. The findings are cited in the table above.

---

## Smoke test (owner's PC, after the steps in `docs/smoke-test.md`)

You need two or three players: the monarch **K**, a lord **A** (head of a house) and a second lord or player **B**. For the jury you also need three online heads of other houses. Alternatively, set `"JuryMin": 1, "MinVotes": 1, "JurorsMustLeadHouse": false` for the test. Grant yourself `realmlaws.admin`.

| # | Do | Expect |
|---|---|---|
| L1 | `oxide.reload RealmLaws`, then `/laws` | The help prints, and `oxide/config/RealmLaws.json` and `oxide/data/RealmLaws.json` exist. |
| L2 | Stand in the market. `/law zone set Crown Market 40 town`, then `/law zone list` | The zone shows your x/z, and the config file holds it. |
| L3 | As K: `/law proclaim kings_peace`. Wait `LawGraceMinutes` (or set it to 0 and reload). | A Herald broadcast. A `decree` (or `law_proclaimed`) line appears in the Chronicle. |
| L4 | B hits A inside the zone, then outside it. | **Inside:** A takes no damage, B sees "Your blow is stayed" and "record #1", and `/law crimes B` shows it. **Outside:** normal damage. *(Tests the damage block, UNVERIFIED in game.)* |
| L5 | As K: `/court accuse B kings_peace fine 5 Wood`. Give B 3 Wood. `/court trial 1`. Jurors vote guilty. | Case #1 is guilty. B's inventory loses exactly 3 Wood **at once** and B is told 2 are owed. `/court pay 1` with 2 Wood clears it. *(Tests item take and client refresh, UNVERIFIED.)* |
| L6 | Accuse again (another law or another player) with `outlaw 1`. The accused runs `/court combat <id>`. K kills the accused. | "GUILTY ... by combat", and `/court outlaws` lists them. *(Tests the death-hook killer attribution.)* |
| L7 | Proclaim `no_building_towns`. A non-crown player places a block in the zone. | The block is not placed (or vanishes) and a record is written. *(Tests Cancel on cube placement.)* Optionally set `no_binding_towns` `"Block": true`, rope a player in town, and check that `/ransom` (CrownAndConsequences) shows no phantom captive. |
| L8 | Set the market curfew hours to include now and wait in the zone for `CurfewGraceSeconds` + `ZoneCheckSeconds`. | A warning, then a record. |
| L9 | Sentence someone to `exile 1`, then walk them into a town zone. | A warning, then "broke exile and is outlawed". |
| L10 | Stop the server, put `{ broken` into `oxide/data/RealmLaws.json`, and start it. | An error in the console. `/law` says the records are damaged. The file is unchanged after a server save. |
| L11 | A different player takes the throne. | "The old laws lapse." `/law` shows none, and crown cases are dismissed. |

---

## What is UNVERIFIED

1. **Nothing has run on a live server.** All hook behaviour is inferred from the OPJ, metadata, IL and old plugins.
2. **Damage blocking:** that `Cancel()` + `Amount = 0` + a non-null return stays the blow, client-side too. [IL] shows `InvokeDamage` returns before invoking `OnDamage`, but what `OnDamage` and other listeners do was not traced (smoke L4).
3. **Cube placement:** that cancelling in `OnCubePlacement` stops the cube for the client too. [IL] shows the game reads `Cancelled` after the hook. Also that `Grid.LocalToWorldCoordinate` returns the same world units as `Entity.Position` (smoke L7).
4. **Capture blocking** (`no_capture` with `Block: true`) and its interaction with CrownAndConsequences' ransom record (see the note above).
5. **Items:** that server-side `AutoSplit` refreshes the client inventory at once, and that hotbar items count. Only the `Inventory` container is read, the same as RealmContracts. Also that `Wood`/`Stone` resolve to the carried items (smoke L5).
6. **Positions:** that `Entity.Position` of a connected player is current on the server, which matters for curfew, exile and peace by attacker position.
7. **Curfew hours** are real UTC clock hours, not the game's day/night cycle. No verified API for in-game time was used.
8. **Killer attribution** in trial by combat. `KillingDamage.DamageSource.Owner` may be null for some kill types (fire, falls, bleeding), and such deaths do not decide the combat.
9. **Disconnect as flight.** A crash or timeout of a duellist counts as fleeing when `CombatFleeLoses` is on (the default).
10. **Newtonsoft round trip** of the data file (tests used System.Text.Json). The shapes are plain public fields, lists and string-keyed dictionaries, as in the other Realm plugins.
11. **Chat brace safety.** Player names go through `string.Format` only as arguments, never as the format string, and are sent with the single-string `SendMessage`/`BroadcastMessage` overloads.
