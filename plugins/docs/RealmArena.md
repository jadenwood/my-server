# RealmArena: the Proving Ring

`plugins/RealmArena.cs` (Oxide 2.0.3867, C# 3) brings honourable combat to Ostreval: **duels by challenge**, fought to the first fall in a ring no one else may enter, with **stakes in marks held in escrow by the treasury**, an **Elo ladder** and a **weekly Champion of the Ring**, **team duels** (2v2, 3v3), **bracket tournaments** (and a bracket for the Royal Tournament), **trial by combat** for the court, and two **tavern games** played in chat for marks with no house edge. Everything happens on the server through Oxide: chat, the game's own popup windows, health and damage events, and (optionally) the game's own teleport. No game file is added or changed and nothing reaches the players' installs.

Status: **compiles with 0 errors against the real 2.0.3867 DLLs; behaviour-tested against mocks with the real `RealmTreasury.cs` in the loop (312 checks); exploit-tested (52 arena checks, 15 on the Royal Tournament hook in the real `RealmEvents.cs`, 12 on the trial hook in the real `RealmLaws.cs`). It has never run on a live server.** See [What is UNVERIFIED](#what-is-unverified-and-how-to-test-it-in-game).

Tags: **[DEC]** = read in the decompiled 2.0.3867 `Assembly-CSharp.dll` to learn what the game does (type and member names only; no game code is kept in this repo). **[ASM]** = confirmed by compiling against that DLL's metadata (`tools/plugin-compile-check/check.sh`). **[OPJ]** = the Oxide hook manifest ([`docs/oxide-rok-api.md`](../../docs/oxide-rok-api.md)). **UNVERIFIED** = not seen working in game.

---

## The lore

Below the Listing Field lies an older circle of beaten sand: **the Proving Ring**. Under the Builders, houses settled quarrels there "to the first fall": the Hearth Charter forbids killing in an honour duel, and the heralds' surgeons stand at the rope. *First blood is the realm's, last blood is no one's.* The ring keeps its own ladder, and every week the realm crowns its **Champion of the Ring**. In the taverns, the same houses settle smaller debts over **Hearth Dice** and **Twenty-One**, with cards whose suits are the Stag, the Oak, the Raven and the Bell.

## What players see

```
Arena: You challenge Bram to a duel for 100 marks each; your stake is held by the treasury. They have 60 s to answer.
  Ranked: the result moves the ladder.
```
Bram sees the challenge in chat and in a **window** with *Accept* and *Decline* (the window shows the stake). They stand together; the ring is drawn where they meet:
```
Arena: The ring is drawn here, 15 m wide. Out of it for 8 s is fleeing.
Arena: 3...  2...  1...
Arena: Fight! To the first fall. 5 min.
Herald: Ada and Bram meet in the ring, 100 marks a head on the outcome!
```
The blow that would kill is **turned aside**: no one dies, nothing is dropped.
```
Arena: Bram is felled by Ada!
Arena: Victory over Bram! 200 marks are yours. The herald's surgeons tend you.
  Rating +20, now 1020.
```
Both are healed by half and shielded for 15 s, so nobody can pick off the loser.

## Commands

| Command | What it does |
|---|---|
| `/duel <player> [marks]` | Challenge a player (optionally for a stake). `/duel challenge <player> [marks]` if the name is a sub-command word. |
| `/duel accept [player] [marks]` | Accept. With a stake, **the stake must be typed**: the answer names what it accepts. |
| `/duel decline [player]` · `/duel cancel` | Refuse a challenge · withdraw your own. Every held stake goes back. |
| `/duel 2v2 <ally> <foe> <foe> [marks]` · `/duel 3v3 <ally> <ally> <foe> <foe> <foe> [marks]` | Team duels. Everyone must accept; the stake is per head. |
| `/duel yield` | Concede (before the fight: withdraw without a loss). |
| `/duel status` · `/duel off` · `/duel on` | What is waiting for you · refuse all challenges · take them again. |
| `/arena` | Your rating and place, the champion, the next crowning, what is happening in the ring. |
| `/arena top [team]` · `/arena me` · `/arena <player>` | The ladder (top 10) · your record · another fighter's. |
| `/arena history` · `/arena champion` · `/arena rules` · `/arena zones` | The latest duels · the champion and past champions · the rules in four lines · arenas and taverns. |
| `/arena tourney` · `join` · `leave` | The bracket (or the sign-up) · enter · withdraw. |
| `/dice <player> <marks>` · `/dice accept [player] <marks>` · `/dice decline` · `/dice cancel` | Hearth Dice for marks. |
| `/dice roll [NdM]` | A throw for show (no marks), told to the players near you. |
| `/cards <player> <marks>` · `accept` · `decline` · `cancel` · `hit` · `stand` · `hand` | Twenty-One for marks. |

All four commands are in RealmHerald's `/realm` hub (duel and arena under *Seasons and realm events*, dice and cards under *Coin, trade and contracts*). Replies use the Realm chat style; the arena speaks as **Arena**, the tavern as **Tavern**, realm-wide news as the **Herald**.

## How a duel works

1. **Challenge.** Refused if either player is: in a duel or at a card table, down, under RealmWarden's new-player protection (no blow could reach them, so the duel would be one-sided), frozen by RealmSentinel, barred from the ring, in the running bracket, or **in a fight outside the ring in the last 30 s** (`CombatTagSeconds`: no escaping a fight into a duel). Also refused during the **Truce of the Realm** (it blocks every blow). A challenger has at most 3 open challenges, waits 15 s between them, and may not challenge the same player again for 60 s after a decline, cancel or lapse. The stake, if any, is **held by the treasury at once**.
2. **Answer** (window or chat) within 60 s. Every check is run again for everyone involved: if the challenger has taken another fight meanwhile, the challenge is called off and every stake goes back. The acceptor's stake is held.
3. **Meeting.** The duellists have 90 s (`GatherSeconds`) to stand together: the ring forms around them (15 m; 25 m for teams), or an **arena zone** becomes the ring if they all stand in one. With `RequireArena`, duels are only fought in an arena. With `Teleport` (UNVERIFIED), they are brought into the first arena at once.
4. **The count** (5 s): no blow lands between the duellists yet.
5. **The fight** (5 min). Inside the ring, from the count until the shield lapses: **no one outside the duel can strike a duellist, and duellists strike only their foes** (in team duels, never their own side). No ropes on or by duellists; no building inside a drawn ring. Out of the ring for 8 s is fleeing. Until the ring is drawn (while the duellists are still meeting) they are in the world like anyone else, but a duellist in a drawn ring cannot strike them, nor they the duellist.
6. **The first fall.** A blow that would kill (or leave less than 5% of full health) is turned aside and its target is **felled**. A deadly fall, fire or beast fells a duellist the same way, and is turned aside for a fighter already felled while their side fights on. A team is beaten when every member is felled.
7. **After.** Both sides are tended (half their health) and shielded for 15 s; stakes are paid; ratings move; the herald speaks for a stake of 100 marks or more, or when the Champion fights (at most 6 times an hour).

**Ends that are not a fall.** Time runs out: a **draw** (stakes back, no rating change). Logging off or leaving the ring in the fight: **forfeit** (stake and rating lost). Logging off before the fight: void (stakes back; logging off during the count also counts as a flight). Fleeing three times in a day bars the player from the ring for 12 h. A reload, a server shutdown, staff, or a truce that begins mid-fight: void, every stake back.

### Why duels never drop loot

[DEC] `PlayerHealth.OnEntityDamage` calls `EntityHealth.InvokeDamage` (where Oxide's `OnEntityHealthChange` [OPJ L162] runs) **before** it takes `Damage.Amount` off the hit region; the player dies (`PlayerHealth.Kill`) when the head or the torso reaches 0; a leg hit beyond the legs' health spills into the torso; a blow with no hit bone is spread over the three regions by their `MaxHealth`. The death raises `PlayerDeathEvent`, and only then does [DEC] `CreateCorpseOnDeath` move the packs into a corpse. RealmArena judges every blow in a duel with the same rule, reading `PlayerExtensions.GetHealth(Player)` → `PlayerHealth.HeadHealth` / `TorsoHealth` / `LegsHealth` (`HealthRegion.CurrentHealth`, `MaxHealth`) [ASM] and the blow's `Damage.HitBoxBone` against `HealthRegion.Bones` [ASM, read by reflection because the compile check's UnityEngine stub has no `HumanBodyBones`]. A killing blow is cancelled (`evt.Cancel` + `Damage.Amount = 0` + `return true`, the pattern RealmLaws and RealmEvents use) and the duel ends: **no death, no corpse, no loot**.

Margins, because Oxide does not order plugins and another plugin may change `Damage.Amount` after this one: every blow is judged `FatalMargin` (1.25) times harder, and `BearerMargin` (2) times more again when the Ironbreaker's bearer is in the blow (RealmLegendary scales the bearer's blows by up to 1.95 with its defaults). If the hit bone cannot be read, the blow is judged against the weaker vital region.

Fallback: if a duellist dies anyway (`OnEntityDeath` [OPJ L188]), the duel is decided and a warning is logged so the margins can be raised. That death drops loot like any death; no item is moved by this plugin. `PreventDeathFlag` (off by default, UNVERIFIED) also sets `PlayerHealth.PreventDeath` [ASM; DEC `PlayerHealth.Kill` returns false while it is set] on fighting duellists; it is cleared at the end of the fight, on logout and on unload. Leave it off until test U11 has been run: a client may still draw a death the server refused.

## Stakes, held by the treasury

Stakes are **marks only** (never items), held in escrow by RealmTreasury through three non-public methods added for the arena:

| RealmTreasury method | What it does |
|---|---|
| `long HoldMarks(holdId, playerId, playerName, amount, source, minutes)` | Moves the stake from the purse into a hold, all or nothing. Returns what it held (0 = refused). |
| `long PayFromHold(holdId, toPlayerId, toName, amount, source)` | Pays out of a hold into a purse; never more than the hold has. |
| `long ReleaseHold(holdId, source)` | Gives whatever is left back to the player it came from. |
| `long GetHold(holdId)` | What is left in a hold. |

Only the source that made a hold (`RealmArena`) may move it. A hold still open after its time (the longest a duel can take, plus 30 minutes) **goes back to its owner by itself** in the treasury's tick, so no mark can be stranded even if the arena is removed. Holds are part of the treasury's zero-sum audit (`MarksMinted == treasury + vaults + purses + bid escrow + holds`), and every hold, payout and return is in the treasury's journal and ledger log.

The arena writes each payout or refund down (`Settlements` in its data file) **before** asking the treasury, and retries until the treasury answers: a crash between the two cannot pay twice (a hold never pays out more than it holds), and a missing treasury only delays payment. With RealmTreasury not running, stakes are refused and honour duels go on.

**The winners take the whole pot**: no fee, no house. In a team duel each winner gets their own stake back plus one loser's. Limits: 5 to 500 marks a head, at most 2000 marks staked a day per player, at most 1000 between the same two players a day, and only after 60 minutes in the realm.

## The ladder

Ranked duels move an Elo rating (start 1000). K is 40 while a rating is **provisional** (fewer than 10 ranked duels), 24 after, and 16 at 1600 and above. Provisional fighters are off the ladder; the ladder also leaves out anyone without a ranked duel in 28 days. Team duels have their own team rating and ladder (`/arena top team`).

A duel is **friendly** (off the ladder, and the challenge says why) when: a fighter has spent less than 120 minutes in the realm; the fighters are housemates or allied houses (liege, vassal or treaty: RealmHouses); the Ironbreaker's bearer is in it; or the same two players have already had 2 ranked duels today or 6 this week. Trials and bracket matches are never ranked.

## The Champion of the Ring

Every **Sunday at 20:00 UTC** the best rating among established fighters with **at least 3 ranked duels that week** is crowned **Champion of the Ring**: a herald line, a window for the champion, a Chronicle entry (`title_earned`), 10 season points for their house (RealmSeasons `AwardHouse`), and the RealmRenown deed `arena_champion` (once per week). RealmRenown turns that deed into titles: **Champion of the Ring** (once) and **Master of the Ring** (three times). The reigning champion is marked on the ladder and in `/arena`. With nobody eligible, the herald says so. Staff can crown early with `/arena admin crown`.

RealmRenown also gets `duel_won` for each ranked win that moved the ladder (its own 10-minute cooldown and daily cap apply; title **the Duelist** at 25) and `arena_tourney` for winning the Lists (title **Victor of the Ring**).

> **Existing servers:** RealmRenown merges new deeds into an existing `oxide/config/RealmRenown.json` by itself, but not new titles. Add these four to its `Titles` list (or delete the file and reload RealmRenown to regenerate it):
> ```json
> { "Id": "duelist", "Name": "the Duelist", "Description": "Won twenty-five ranked duels in the Proving Ring.", "Infamous": false, "Requires": { "duel_won": 25 } },
> { "Id": "ring_champion", "Name": "Champion of the Ring", "Description": "Was crowned the Proving Ring's champion of the week.", "Infamous": false, "Requires": { "arena_champion": 1 } },
> { "Id": "ring_master", "Name": "Master of the Ring", "Description": "Was crowned champion of the week three times.", "Infamous": false, "Requires": { "arena_champion": 3 } },
> { "Id": "ring_victor", "Name": "Victor of the Ring", "Description": "Won the Lists of the Ring.", "Infamous": false, "Requires": { "arena_tourney": 1 } }
> ```

## Tournaments

**The Lists of the Ring** (staff with `realmarena.admin`): `/arena tourney open [fee]` opens a sign-up for 10 minutes (fee up to 200 marks, held by the treasury). At the end (or `/arena tourney start`), a single-elimination bracket is drawn by rating, with byes for the top seeds; 4 to 32 entrants. Each match is called (chat and a window) and must be fought in the ring within 6 minutes. A match nobody fights goes to whoever was there (in an arena zone, if there is one); with both or neither there, a fair coin decides. A fight that runs out of time goes to whoever struck harder, then the higher seed. Withdrawing (`/arena tourney leave`) gives the match away. The champion takes 70% of the pot and the runner-up 30% (`PrizeSplit`), the champion's house 15 season points and the runner-up's 8, and the Chronicle records it as `event_ended` (never `tournament_champion`: RealmRenown counts that type as a Royal Tournament win). Too few entrants or a staff cancel returns every fee.

**The Royal Tournament bracket** (`RoyalBracket`, on): while RealmEvents' Royal Tournament runs, the ring draws a bracket of its entrants (`GetTournamentEntrants`) five minutes after it begins. Players enter with `/tourney join` as before. Ring duels end without a death, so each bracket win is reported to RealmEvents (`ScoreTournamentDuel`) and **scores like a kill** under RealmEvents' own anti-feeding rules (housemates and allies never count, per-victim caps). The melee goes on beside it; RealmEvents ranks, pays prizes and offers the Ironbreaker as before. When the Royal Tournament ends, its bracket closes where it stood. Entrants under new-player protection are left out of the bracket.

## Trial by combat

When an accused demands trial by combat (`/court combat <case>`), RealmLaws (with `CombatInArena`, on) calls `StageTrial(case, accused, champion, minutes)`: the two are told to meet in the ring, the fight lasts as long as the court's window, and the first fall is reported back with `ArenaTrialResult(case, winnerId, detail)`: the champion's win is a guilty verdict, the accused's an acquittal. Nobody dies. A peace law in the place does not stop the duel (RealmLaws asks `IsDuelBlow`). If the ring cannot take the trial (a fighter offline or already in a duel), or the arena is not loaded, the court's own trial by combat runs unchanged; logging off and the window's time-out are still decided by RealmLaws.

## Tavern games

Both games are **player against player, with equal stakes and the same rules for both: the house takes nothing (house edge zero)**. Dice and shuffles come from `System.Security.Cryptography.RandomNumberGenerator` (no modulo bias).

- **Hearth Dice:** each throws two dice; the higher total takes the pot. A tie is thrown again, up to five times; then both stakes go back.
- **Twenty-One:** both are dealt two cards and **play at the same time** (so neither moves second with an edge): `/cards hit` or `/cards stand`, 45 s per decision, then the hand stands. Ace counts 1 or 11, Knight, Queen and King 10. Closest to 21 without going over takes the pot; level hands, or both over, take back their stakes. Each player sees only their own hand. Leaving the table stands your hand.

Strict limits: 1 to 100 marks a game; at most 500 marks staked and 250 lost (net) per player per UTC day, and no game may risk going over the loss limit; 10 games a day between the same two players; 10 s between games; one open game per player; 60 minutes in the realm first. `RequireTavernZone` limits games to tavern zones. `/dice roll 2d6` throws for show, told to the players within 30 m.

Marks are an in-game ledger currency that is never sold ([`docs/legal/monetisation-guardrails.md`](../../docs/legal/monetisation-guardrails.md), tier A): the tavern is for marks only. Owners who want no games of chance switch `Tavern.Enabled` off.

## Against abuse

| Abuse | Defence |
|---|---|
| Win-trading with alts | 2 ranked duels a day and 6 a week per pair; each repeat in the week halves the rating change; housemates and allied houses never ranked; 120 minutes in the realm before duels are ranked; **no contest** when the loser falls within 15 s without striking a blow (an alt that yields at once feeds nothing); RealmWarden alert (`arena_pair`) for a pair with 6 ranked duels in 7 days; `/arena admin pairs` lists the pairs that meet most. |
| Elo farming of new players | Provisional ratings (off the ladder); an established fighter beating only provisional ones gets a quarter of the change; at most +120 rating a day; brand-new accounts are never ranked. |
| Wager scams | Stakes are escrowed when made and when accepted (no "I'll pay you later"); the window shows the stake from the server, and a chat answer must type the stake; an answer is bound to its challenge (a re-issued, dearer challenge has a new id, and the same pair cannot be re-challenged for 60 s); a challenge is called off if anyone involved took another fight; winners are paid by the treasury, not by the loser. |
| Leaving to avoid a loss | Logging off or leaving the ring in the fight forfeits stake and rating; three flights a day bar the player for 12 h; logging off during the count counts as a flight. |
| Escaping a fight into a duel's protection | No challenge or answer within 30 s of a fight outside the ring; protection only from the count, inside the ring, for at most 5 minutes; leaving the ring forfeits. |
| Interference and vultures | Blows, ropes and building from outside are refused; the shield holds 15 s after the duel. |
| One-sided duels | No duels for protected newcomers, frozen players, or during the truce; the Ironbreaker makes a duel friendly. |
| Tavern loss chasing and laundering | Daily stake and loss limits, games per pair per day, cooldown, one table at a time. |
| Duping stakes | All-or-nothing holds; every stake re-held per challenge; settlements written before payment and idempotent; holds lapse back to their owners; the treasury audit covers holds. |

## For staff

Permission **`realmarena.admin`** (row and grant lines in [`staff-roles-and-permissions.md`](../../docs/community/ops/staff-roles-and-permissions.md); Admin and Event host):

| Command | What it does |
|---|---|
| `/arena admin status` | Duels, challenges, games, settlements waiting, fighters; whether the treasury is running; marks paid out; next crowning; tournament. |
| `/arena admin zone set <name> [radius]` · `zone remove <name>` | Mark an arena where you stand (default 20 m) · remove it. Written to the config. |
| `/arena admin tavern set <name> [radius]` · `tavern remove <name>` | The same for taverns (default 10 m). |
| `/arena admin void <duel>` | Call a duel off: every stake back. |
| `/arena admin rating <player> <n>` · `reset <player> confirm` | Set a rating · wipe an arena record (time in the realm is kept). |
| `/arena admin bar <player> <hours>` · `unbar <player>` | Bar from the ring (voids their duel) · lift a bar. |
| `/arena admin crown` | Crown this week's champion now; the next crowning moves a week on. |
| `/arena admin pairs` · `settle` | The pairs that meet most · retry waiting payouts now. |
| `/arena tourney open [fee]` · `start` · `cancel` | Open the Lists · draw the bracket now · call it off (fees back). |

**Setting up the Proving Ring:** stand in the middle of the sand ring near the Listing Field (saga L05) and type `/arena admin zone set Proving Ring 20`. Duels whose fighters all stand in it use it as the ring; others still form a ring where they meet unless `RequireArena` is on. A tavern: `/arena admin tavern set Hearth Inn 10`.

## Config (`oxide/config/RealmArena.json`)

Top level: `Enabled` (true; off closes the arena for everyone but staff), `UsePopups` (true), `TickSeconds` (10), `MaxFighters` (5000), `HistoryKept` (100), `Arenas` and `Taverns` (zones: `Name`, `X`, `Y`, `Z`, `Radius`).

| Section | Keys (defaults) |
|---|---|
| `Duels` | `Enabled` true, `ChallengeSeconds` 60, `GatherSeconds` 90, `CountdownSeconds` 5, `MaxDuelMinutes` 5, `RingRadius` 15, `LeaveRingSeconds` 8, `YieldHealthPercent` 5, `FatalMargin` 1.25, `BearerMargin` 2, `PreventDeathFlag` false, `HealAfterPercent` 50, `ShieldSeconds` 15, `ChallengeCooldownSeconds` 15, `RechallengeSeconds` 60, `MaxOpenChallenges` 3, `CombatTagSeconds` 30, `FleeBanCount` 3, `FleeBanHours` 12, `RequireArena` false, `Teleport` false, `AnnounceMinWager` 100, `HeraldsPerHour` 6, `BlockBuildingInRing` true |
| `Ranked` | `Enabled` true, `StartRating` 1000, `KProvisional` 40, `K` 24, `KHigh` 16, `HighRating` 1600, `ProvisionalGames` 10, `MinRating` 100, `MinPlayMinutes` 120, `PairPerDay` 2, `PairPerWeek` 6, `PairRepeatFactor` 0.5, `ProvisionalOpponentFactor` 0.25, `MaxGainPerDay` 120, `MinDuelSeconds` 15, `SameHouseRanked` false, `AlliesRanked` false, `BearerRanked` false, `WinTradeAlertGames` 6, `LadderSize` 10, `LadderActiveDays` 28, `RenownDeed` "duel_won" |
| `Wagers` | `Enabled` true, `Min` 5, `Max` 500, `DailyLimit` 2000, `PairDailyLimit` 1000, `MinPlayMinutes` 60 |
| `Teams` | `Enabled` true, `MaxSize` 3, `Ranked` true, `RingRadius` 25, `ChallengeSeconds` 90 |
| `Champion` | `Enabled` true, `Day` "Sunday", `HourUtc` 20, `MinWeeklyGames` 3, `RenownDeed` "arena_champion", `HousePoints` 10, `Chronicle` true, `Popup` true |
| `Tournament` | `Enabled` true, `MaxEntryFee` 200, `PrizeSplit` [70, 30], `MinEntrants` 4, `MaxEntrants` 32, `SignupMinutes` 10, `MatchMinutes` 6, `Ranked` false, `RoyalBracket` true, `RoyalSeedMinutes` 5, `ChampionHousePoints` 15, `RunnerUpHousePoints` 8, `RenownDeed` "arena_tourney", `Chronicle` true |
| `Tavern` | `Enabled` true, `Dice` true, `Cards` true, `MinStake` 1, `MaxStake` 100, `DailyStakeLimit` 500, `DailyLossLimit` 250, `PairGamesPerDay` 10, `CooldownSeconds` 10, `MinPlayMinutes` 60, `ChallengeSeconds` 60, `CardTurnSeconds` 45, `RequireTavernZone` false, `RollRadius` 30 |
| `Trials` | `Enabled` true (RealmLaws may stage trials in the ring) |

Every value is clamped on load (for example `RingRadius` 4 to 100, `FatalMargin` 1 to 5, a `PrizeSplit` over 100% falls back to 70/30); a missing section takes its defaults. Time in the realm is counted by this plugin from when it is installed: at launch, lower `Ranked.MinPlayMinutes`, `Wagers.MinPlayMinutes` and `Tavern.MinPlayMinutes` if veterans should not wait.

## How it connects

| Direction | Plugin | Methods |
|---|---|---|
| calls | RealmTreasury | `GetPurse`, `HoldMarks`, `PayFromHold`, `ReleaseHold` |
| calls | RealmRenown | `AddDeed` (`duel_won`, `arena_champion`, `arena_tourney`) |
| calls | RealmChronicle | `Log` (`title_earned`, `event_started`, `event_ended`) |
| calls | RealmSeasons | `AwardHouse` |
| calls | RealmHouses | `GetHouse`, `GetLiege`, `HasTreaty` |
| calls | RealmHerald | `PopupsWanted` |
| calls | RealmWarden | `IsNewPlayerProtected`, `RaiseWardenAlert` |
| calls | RealmEvents | `IsTruceActive`, `GetActiveEvents`, `GetTournamentEntrants`, `ScoreTournamentDuel` |
| calls | RealmLaws | `ArenaTrialResult` |
| calls | RealmLegendary | `IsBearer` |
| calls | RealmSentinel | `IsSentinelFrozen`, `SentinelGrace` |
| called by | RealmLaws | `StageTrial`, `IsDuelBlow` |
| offers | anyone | `IsInDuel(id)`, `IsDuelBlow(attackerId, victimId)`, `GetArenaRating(id)`, `GetArenaChampion()`, `GetArenaLadder(count)` → `"name\|rating\|wins\|losses"` |

Every call treats a missing plugin (null) as "not available": without RealmTreasury no stakes; without RealmEvents no truce check and no Royal bracket; without RealmWarden no protection check (a protected newcomer's blows could then be one-sided, so keep RealmWarden loaded).

Hooks: `OnEntityHealthChange` [OPJ L162], `OnEntityDeath` [OPJ L188], `OnPlayerCapture` [OPJ L711], `OnCubePlacement` [OPJ L266], `OnPlayerConnected` / `OnPlayerDisconnected`, `OnServerSave`, `OnServerShutdown`. Game members: `Player.Id`, `Player.Name`, `Player.Entity`, `Entity.Position`, `Entity.IsPlayer`, `Entity.Owner`, `Entity.GetOrCreate<T>`, `Damage.Amount`, `Damage.DamageSource`, `Damage.HitBoxBone`, `EntityDamageEvent.Entity` / `Damage`, `EntityDeathEvent.Entity` / `KillingDamage`, `BaseEvent.Cancel` / `Cancelled`, `PlayerCaptureEvent.Captor` / `Target`, `CubePlaceEvent.SenderId` / `Grid` / `Position`, `Grid.LocalToWorldCoordinate`, `PlayerExtensions.SendMessage` / `SendError` / `GetHealth` / `Heal` / `IsAlive` / `ShowPopup` / `ShowConfirmPopup`, `PlayerHealth.CurrentHealth` / `MaxHealth` / `HeadHealth` / `TorsoHealth` / `LegsHealth` / `PreventDeath`, `HealthRegion.CurrentHealth` / `MaxHealth` / `Bones`, `CharacterTeleport.Teleport`, `Server.ClientPlayers` / `GetPlayerById` / `BroadcastMessage`, `Dialogue.OnSubmit`, `Options`, `Dialogue.ValueMessage`, all [ASM] (compiled against the shipped metadata).

**Other plugins' hooks.** Oxide does not order plugins. A blow RealmWarden, RealmEvents' truce or a peace law cancels first never reaches the arena (it skips cancelled events), which is why protected players, the truce and peace laws are handled as above. RealmWarden tags duel blows as combat like any blow: **wait 30 s after a duel before logging off**, or the Warden flags a combat log (a flag for staff, no penalty; follow-up below). RealmSentinel scores damage and kill rates as usual.

## Data and recovery

`oxide/data/RealmArena.json`: fighters (ratings, records, daily counters, flights, bars), pair records (7 days), challenges, duels, tavern games, waiting settlements, the history, champions, the tournament. A copy is written to `RealmArena_lastgood.json` at every successful load. **If the file exists but cannot be read (cut off, damaged, empty), the plugin refuses to run and never writes it**: fix it or restore the last-good copy, then `oxide.reload RealmArena`. Stakes it held stay in the treasury and lapse back to their owners. Anything running at a reload or a crash (challenges, duels, card games) is void on load and every stake goes back; an open tournament survives, and called matches are called again. RealmArena.json holds Steam IDs and names: list it with the other plugin files in the server's privacy notice (follow-up below).

## What is UNVERIFIED, and how to test it in game

Nothing here has been seen on a live server. Run these with two or three accounts on the test server (`G:\RealmTest\server`), RealmTreasury, RealmWarden, RealmEvents, RealmLaws and RealmHerald loaded. Give the test accounts marks (sit one account on the Old Throne, then `/treasury mint 1000` and `/treasury grant <player> <n>`), and set `Ranked.MinPlayMinutes`, `Wagers.MinPlayMinutes` and `Tavern.MinPlayMinutes` to 0 for the session.

| # | What | Steps | Expected |
|---|---|---|---|
| U1 | The first fall: no death, no loot | A `/duel B`, B accepts in the window. Fight with swords until one is nearly dead; land the last blow. | The last blow does no damage; "B is felled by A!"; B stays standing; **no corpse or dropped pack**; B's inventory unchanged. No "died in the ring" warning in the server log. |
| U2 | The hit regions | Repeat U1 finishing with a bow headshot, a leg hit, and a fall from height inside the ring. | No death in any case. If anyone dies, raise `Duels.FatalMargin` (1.5, then 2) and note the case. |
| U3 | Tending and the shield | After U1 watch both health bars; C attacks B at once. | Both rise by about half; C's blows do nothing for 15 s, then land. |
| U4 | Interference | During a duel, C strikes A and B with a sword, arrows and fire; A strikes C. | Nothing lands; each gets one refusal line every few seconds. |
| U5 | The ring | During a duel walk B 20 m out, back in after 5 s; then out for 10 s. | Warnings every 2 s; back in keeps the fight; staying out: "B fled the ring and forfeits!" |
| U6 | Windows | Challenge, team challenge, champion crowning (`/arena admin crown`), a tournament match. | Each window shows with its buttons; Accept starts the duel, Decline declines. If no window shows, set `UsePopups` false and note it. |
| U7 | Stakes | `/purse` before; `/duel B 50`; `/purse`; B `/duel accept A 50`; fight to a fall; `/purse` both; `/treasury audit`. | A -50 at the challenge, B -50 at the answer, winner +100 after; the audit is clean. Decline and cancel return the stake at once. |
| U8 | Ropes | During a duel C tries to rope A. | Refused. |
| U9 | Building | During a duel C places a block inside the ring, then 30 m away. | Inside refused, outside placed. |
| U10 | Teleport (if used) | Set `Duels.Teleport` true with an arena zone; challenge from far away. | Both appear in the arena, one each side; 3 s after the duel both are back; RealmSentinel raises no movement alert. |
| U11 | PreventDeathFlag (if used) | Set it true; fight with RealmLegendary's bearer striking. | No death; afterwards health is normal on both clients (no "dead" body on the other client). If a client shows a death the server refused, turn it off. |
| U12 | The Royal Tournament bracket | `/event start tournament 30`; four accounts `/tourney join`; wait 5 min; `/arena tourney`; fight the matches. | The bracket is drawn and called; each win adds a kill in `/tourney standings`; at the end RealmEvents names the champion. |
| U13 | Trial by combat | Proclaim a law, `/court accuse`, the accused `/court combat <case>`; fight in the ring. | Both are told the trial is in the ring; the first fall closes the case (guilty if the champion wins); nobody dies. |
| U14 | Peace law | Duel inside the Crown Market zone with `kings_peace` in force. | Duel blows land and no crime is recorded; a third player's blow is stayed as usual. |
| U15 | Tavern | `/dice B 10`, B accepts; `/cards B 10`, play a hand; `/dice roll 2d6` near C and far from D. | Marks move by exactly the pot; C sees the throw, D does not. |
| U16 | Logging off | During a fight B quits the game. | A wins and gets the pot; B's rating drops on return. RealmWarden flags a combat log (expected). |
| U17 | The game's own `/suicide` in a duel | B types `/suicide` mid-fight. | B dies for real (their choice; loot drops as for any death) and loses the duel; the server log notes the death. |
| U18 | Restart | Mid-duel with stakes, save and restart the server. | On load the duel is void and both stakes are back; ratings and an open tournament survive. |
| U19 | The crowning | Play 3 ranked duels between two accounts on different days (or raise `Ranked.PairPerDay`); wait for Sunday 20:00 UTC or `/arena admin crown`. | Herald line, window, Chronicle entry, house points, and RealmRenown's *Champion of the Ring* (after adding the titles if the config predates them). |

## Tests

```
bash plugins/docs/RealmArena/logic-tests/run.sh   # 312 checks: RealmArena.cs + the real RealmTreasury.cs against mocks
bash tools/exploit-review/run.sh arena            # arena (52), arena-events (15, real RealmEvents.cs), arena-laws (12, real RealmLaws.cs)
bash tools/plugin-compile-check/check.sh          # C# 3 against the real 2.0.3867 metadata
node tools/realm-integration/check.mjs            # cross-plugin calls, chat style, popups, the /realm catalogue
```

They prove the plugin's own rules, that every stake balances in the treasury's real escrow code, and that the RealmEvents and RealmLaws hooks keep their anti-feeding and single-verdict rules. They do not prove that the game behaves like the mocks; that is the table above.

## Follow-ups

- RealmWarden could skip combat tags for sanctioned duel blows (`RealmArena.IsDuelBlow`), so a duellist may log off right after a duel without a combat-log flag.
- RealmPainter could show the ladder and the champion on a board (`GetArenaLadder`, `GetArenaChampion`); the Chronicle snapshot and the portal could show them too.
- A dedicated Chronicle type (`duel_won` for high-stake duels and title fights) would put great duels on the overlay; today only crownings and the Lists are chronicled.
- `/purse` could show marks held in escrow.
- `docs/legal/privacy-notice.md` should list `oxide/data/RealmArena.json` (Steam IDs, names, ratings, stakes; kept until a wipe).
- After U1 and U2, tune `FatalMargin` and decide on `PreventDeathFlag`.
