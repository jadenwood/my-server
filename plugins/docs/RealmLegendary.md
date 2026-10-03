# RealmLegendary: the Ironbreaker

`plugins/RealmLegendary.cs` (Oxide 2.0.3867, C# 3) adds one legendary weapon to Ostreval: **the Ironbreaker**. Exactly one exists. It is won at the realm's events, borne by one player at a time, and taken by whoever slays its bearer. It looks like the game's own largest two-handed sword. Everything that makes it legendary (harder blows, guards and gates breaking, the bearer's title, the heralds, the Chronicle) is done by the plugin on the server. No game file is added or changed and nothing reaches the players' installs.

Status: **compiles with 0 errors against the real 2.0.3867 DLLs, behaviour-tested against mocks (142 checks) and exploit-tested (48 checks, including a 3000-step fuzz). It has never run on a live server.** See [What is UNVERIFIED](#what-is-unverified) and [Smoke test](#smoke-test-on-a-real-server).

Tags in this guide: **[CODE]** = read in the decompiled 2.0.3867 `Assembly-CSharp.dll` (type and member names only; no game code is kept in this repo). **[ASM]** = confirmed by compiling the plugin against that DLL's metadata. **[OPJ]** = the Oxide hook manifest (see [`docs/oxide-rok-api.md`](../../docs/oxide-rok-api.md)). **UNVERIFIED** = not seen working in game.

---

## What players see

- **The claim.** The champion of the Royal Tournament (by default) receives the blade in their packs. The realm hears it:
  ```
  Herald: Aldric, champion of the Royal Tournament, takes up the Ironbreaker! Every house will know the name.
  ```
  The bearer gets a window and two chat lines that say what it does and what the rules are.
- **The blade in hand.** The bearer's blows with it land harder against players (x1.3), harder still against a player who is blocking (x1.5 on top), and break gates, doors and building blocks twice as fast.
- **The title.** In global chat the bearer's name reads `Aldric (Ironbreaker) : ...`. It works next to a RealmRenown title (`Kingslayer Aldric (Ironbreaker) : ...`).
- **The hunt for the bearer.** Whoever slays the bearer takes the blade from their hand: `Herald: Brannoc has slain Aldric and takes the Ironbreaker from their hand!`
- **The Chronicle.** Every claim and every loss is a Chronicle entry, so it shows on the stream overlay, the portal's Chronicle page and in Discord (see [Chronicle](#chronicle)).
- Other plugins can show the bearer: RealmPainter's Ironbreaker poster calls `GetBearerName()`.

## How it is won

RealmEvents offers the blade through a small non-public hook, `AwardEventPrize(kind, playerId, playerName)`:

| Event | Who is offered it | Default |
|---|---|---|
| Royal Tournament (`tournament`) | the champion, after RealmEvents' own anti-feeding rules (minimum entrants, no allied kills, one victim feeds at most 4 points) | **on** |
| King's Hunt (`kings_hunt`) | a hunter at the moment they are paid for taking a quarry (so the hunt's ally and pair-cooldown rules apply first). The first such hunter takes it | off |

`PrizeEvents` in the config chooses. The blade is given **only while it rests in the crown's armoury** (nobody bears it). A champion who lost the blade within `PrizeCooldownDays` (7) is not given it; the herald says so and it stays in the armoury. An admin can also `/ironbreaker grant` it (a server's own ceremony, a saga beat).

An offline winner (possible for a hunter) has `PrizeClaimHours` (24) to log in and take it up.

## The rules of the blade

| Situation | What happens |
|---|---|
| The bearer is slain | The blade is taken out of the packs **before the corpse is filled** and passes to the slayer. With no killer (a fall, a fire), the last foe who struck the bearer within `CombatWindowSeconds` (30) counts as the slayer. |
| ...by their own house, a liege, vassal or treaty partner (`SlayerMustBeUnallied`) | It returns to the armoury. A blow from the bearer's own side never counts as a foe's. |
| ...by the player it was passed from within `PassPairCooldownHours` (72) | It returns to the armoury (no ping-pong between two players). |
| ...after `MaxPassesPerDay` (6) passes in 24 hours | It returns to the armoury (no ring of alts). |
| ...by a player who cast it away (barred) | It returns to the armoury. |
| The bearer logs out | The blade goes into the plugin's keeping at once, so the sleeping body carries nothing. Back within `LogoutGraceMinutes` (15), it returns to their hand; later, it returns to the armoury. |
| The bearer logs out within 30 s of a foe's blow (`CombatLogPassesBlade`) | It passes to that foe, as if slain. |
| The bearer puts it in a chest or trades it | It is back in the bearer's packs at the next tick (`TickSeconds`, 3 s): it will not leave their hand. |
| The bearer drops or destroys it | After `DropGraceTicks` (2) ticks: to a foe within the combat window, or else to the armoury, and the bearer is barred for `ForfeitBanDays` (7). |
| The bearer's packs are full when it is given | They are told to make room; it comes to hand when there is room. |
| A server restart or plugin reload | Unload takes it into keeping; the next load gives it back. |

A slain bearer's own swords, armour and goods drop as usual. Only the blade is handled by the plugin.

## Effects, and what the game allows

| Wanted | Done | Why |
|---|---|---|
| Damage multiplier | `DamageMultiplier` 1.3 against players | `OnEntityHealthChange(EntityDamageEvent)` [OPJ L162] runs before `EntityHealth.InvokeDamage` applies `Damage.Amount` [CODE]; the plugin scales the amount. |
| Stagger or knockback | **Not possible.** Extra damage to blocking players instead: `BlockingMultiplier` 1.5 on top | `Damage.Force` is not part of the `Damage` network codec (`Damage.Serializer` writes amount, fatal, types, damager, source, bone) [CODE], and melee stagger is decided on the clients (`MeleeParryNetworkingHelper`, `HoldableShield`). Blocking is read with `CombatUtil.IsBlocking(Entity)`: a parrying sword or a raised shield [CODE][ASM]. |
| Bonus against blocks, gates, shields | `BlockMultiplier` 2 (building blocks), `StructureMultiplier` 2 (placed objects: entities with `PlaceableBlockAssociation`), shields through `BlockingMultiplier` | Blocks: `OnCubeTakeDamage(CubeDamageEvent)` [OPJ L292] runs first in `CubeListener.OnCubeDamage` [CODE]. |
| Extra stamina drain per swing | **Not possible.** `BearerDamageTakenMultiplier` (default 1 = off) is the cost a server can set instead | `StaminaManager.Start` disables itself on every entity but the local player and logs "stamina is not networked" [CODE]. The server has no stamina to drain. |
| Creatures | unchanged | The blade is for war, not for farming. |

A capped hit: `MaxDamagePerHit` (0 = no cap). Healing and cancelled events (a truce, raid hours, the King's Peace) are never touched.

## How the plugin knows it is the blade (research notes)

Read in the 2.0.3867 `Assembly-CSharp.dll`. All of it is compile-checked [ASM] by `tools/plugin-compile-check/check.sh`; none of it has been seen at run time.

**Identifying one item.** [CODE] `InvGameItemStack` has `UniqueID` (a per-session counter), `Blueprint`, `StackAmount`, components, and `Collection` (the `ItemCollection` it sits in, or null once its container is gone). `ItemCache` maps a stack to a server GUID so moves between containers keep the same object (`CachedInvGameItemStackData.GetStack`). None of this is saved: `InvGameItemStack.Serialize` writes only the blueprint, the amount and components (`Ammo`, `FishStackComponent`), and `ItemCache.Serialize` saves only its counter. A plugin cannot add a component either (clients would not know its id). So:

- while the server runs, the blade **is the stack object** the plugin put in the bearer's packs; `stack.Collection` finds it in any container (packs, hotbar, chest, corpse, another player);
- across a restart, `Unload` takes it into keeping and the next load gives it back (see [Restarts](#restarts)).

**Which weapon dealt the damage.** [CODE] `HoldableMelee.UpdateCollisionDetection` builds `new Damage(this, HolderEntity, hitbox)`: `Damage.Damager` is the melee holdable and `Damage.DamageSource` its holder. The `Damage` codec sends the damager as a network view id and the server turns it back into the holdable's game object. `BipedHoldable.Stack` is the held stack (set by `EntityEquipment.OnInstanceCreated`). The plugin reads, in order:

1. `GameObjectUtility.TryGetEntity(Damage.Damager)` -> `BipedHoldable.Stack` ("damager");
2. `Damage.TryGetFromSource<BipedHoldable>()`: the right-hand holdable of the source ("right-hand");
3. neither (UNVERIFIED whether the server fills them): `WhenWeaponUnknown` "melee" counts the bearer's melee-typed hits (`DamageType` Melee, Slash, Pierce, Bash or Cut, not Projectile); "never" counts none.

`HeldMatch` "stack" (default) accepts only the blade's own stack; "name" accepts any base item in the bearer's hand, in case the server turns out to hold a copy of the stack in the holdable. `DebugStrikes` logs which path each strike took.

**Death.** [CODE] On the server `PlayerHealth.Kill` raises `PlayerDeathEvent` and then `EntityHealth.InvokeDeath` (where `OnEntityDeath` [OPJ L188] fires). `AncientThroneListener` subscribes to `PlayerDeathEvent` at `EventHandlerOrder.Early` and calls `OnKingDeath` [OPJ L1140] for **every** player death; `CreateCorpseOnDeath` subscribes at the default order and moves every container into the corpse (`InvEquipment.DropInventory`). So `OnKingDeath` sees the bearer's packs before the corpse is filled; the plugin takes the blade out there and returns null (non-null would skip the throne's king-death handling). `OnEntityDeath` is the fallback: it runs after the corpse is filled, and the blade's stack is then taken out of the corpse. The killer is `KillingDamage.DamageSource.Owner` [ASM].

**Stamina.** [CODE] `StaminaManager` (the melee stamina) and `Stamina` exist, but `StaminaManager.Start` disables the component on non-local entities. Not reachable from the server.

**Giving and taking items.** [CODE][ASM] `ItemContainerExtensions.GetContainerOfType(entity, CollectionTypes.Inventory | Hotbar)` -> `Container.Contents`. `ItemCollection.AutoMergeAdd` adds a non-stackable stack as the object itself (`AddItem`), so the plugin keeps that reference. `ItemCollection.RemoveItem(stack, true)` takes it out with a broadcast to the holder's client. The game's own `/give` uses the same calls (`ThronesCommandHandler.Give`).

**The base item.** Item names live in the game's data, not in the DLL. `BaseItem` empty (the default) takes the first item whose name holds a word from `BaseItemSearch` ("Greatsword", "Great Sword", "Claymore", "Two-Handed Sword", "Bastard Sword", "Longsword", "Long Sword") from `InvBlueprints.AllBlueprintNames` [ASM], and logs it: `The Ironbreaker takes the shape of '...'`. **UNVERIFIED** which item that is on a real server. Confirm it in game (smoke step L1) and set `BaseItem` to the exact name. The tooltip of the game's two-handed weapons says "Two-Handed" (`ItemTooltip`, from the holdable's occupied hand slots [CODE]); the largest is the one with the longest blade.

## Commands

All staff-only, gated by `realmlegendary.admin` (see [`docs/community/ops/staff-roles-and-permissions.md`](../../docs/community/ops/staff-roles-and-permissions.md)). Players learn about the blade from the heralds, the Chronicle and the bearer's title, so `/ironbreaker` is not in the players' `/realm` hub (`STAFF_COMMANDS` in `tools/realm-integration/check.mjs`).

| Command | What it does |
|---|---|
| `/ironbreaker` or `/ironbreaker status` | Who bears it (or who last did, and why they lost it), since when, whether it is in hand, the base item, the prize events, the last foe, and the audit. |
| `/ironbreaker grant <player> [force]` | Gives it to an online player. Only from the armoury unless `force`, which first returns it from the current bearer. Heralded as "By the crown's leave". |
| `/ironbreaker revoke` | Takes it from the bearer back to the armoury. |
| `/ironbreaker reset confirm` | Forgets the bearer, passes and bars (the audit counters are kept). |
| `/ironbreaker items [word]` | Lists item names that hold a word (default "sword"), to set `BaseItem`. |

## Config (`oxide/config/RealmLegendary.json`)

| Key | Default | Meaning |
|---|---|---|
| `BaseItem` | `""` | Exact item name of the blade's look. Empty = first match of `BaseItemSearch`. |
| `BaseItemSearch` | see above | Words tried in order when `BaseItem` is empty. |
| `PrizeEvents` | `["tournament"]` | `tournament`, `kings_hunt`, both or none. |
| `DamageMultiplier` | 1.3 | Blade strikes against players (0.1 to 5). |
| `BlockingMultiplier` | 1.5 | In addition, against a blocking player. |
| `StructureMultiplier` | 2 | Against placed objects (gates, doors). |
| `BlockMultiplier` | 2 | Against building blocks. |
| `BearerDamageTakenMultiplier` | 1 | Damage the bearer takes from players (a cost; 1 = none). |
| `MaxDamagePerHit` | 0 | Cap on a boosted hit (0 = none). |
| `WhenWeaponUnknown` | `melee` | `melee` or `never` (see research notes). |
| `HeldMatch` | `stack` | `stack` or `name`. |
| `PassToSlayer` | true | false = every death returns it to the armoury. |
| `SlayerMustBeUnallied` | true | Own house, liege, vassals and treaty partners never take it. |
| `CombatWindowSeconds` | 30 | How long a foe's blow counts for a fall, a flight or a cast-away blade. |
| `CombatLogPassesBlade` | true | Logging off in a fight passes it to the foe. |
| `LogoutGraceMinutes` | 15 | Time to come back after logging off. |
| `PrizeClaimHours` | 24 | Time for an offline winner to log in. |
| `PassPairCooldownHours` | 72 | The same two players pass it at most once in this window. |
| `MaxPassesPerDay` | 6 | More passes in 24 h and it returns to the armoury. |
| `ForfeitBanDays` | 7 | A bearer who casts it away may not bear it again this long. |
| `PrizeCooldownDays` | 7 | A bearer who lost it may not win it as a prize this soon. |
| `TickSeconds` | 3 | How often the blade is checked (1 to 30). |
| `DropGraceTicks` | 2 | Ticks a blade may be out of every container before it counts as cast away. |
| `ChatTitleEnabled`, `ChatTitleFormat` | true, `%name% [D6A043](Ironbreaker)[-]` | The bearer's chat title. Must hold `%name%`. |
| `UsePopups` | true | The window for a new bearer (a player's `/realm popups off` is honoured through RealmHerald). |
| `RenownDeed` | `ironbreaker` | RealmRenown deed kind added on each claim ("" = off). It counts only if RealmRenown's config has a deed of that kind (add one, and a title that needs it, to make "Ironbreaker" a lasting title). |
| `DebugStrikes` | false | Log how each bearer strike was resolved (for the smoke test). |

## Chronicle

No new Chronicle type is needed. A claim is written as `title_earned` ("A Title Earned"), with the bearer (and the former bearer) as actors; a loss as `event_ended` ("Event Ends"), titled "The Ironbreaker returns to the crown's armoury". An older RealmChronicle that rejects a type gets a `decree` line instead. Both types reach the overlay, the portal and the bot today; the Discord herald posts them when the owner ticks those types in Steward (they are not in its default list). Dedicated `blade_claimed` / `blade_lost` types with their own icons are a follow-up (they need the art team's icons and the Discord herald's list).

## Restarts

`Unload` takes the blade into keeping and notes how many base items the bearer still holds. The next load gives it back at the first tick. If the world was saved with the blade still in the packs (a save before `Unload`), the bearer holds one more base item than noted, and that stack is bound instead of a new one being made. A hard crash (no `Unload`) leaves the blade in the packs: the plugin finds it again from the bearer's first strike with a base item from their own packs, or at the first tick (it binds the first base item in their hotbar or packs, which after a crash may be one of their own swords; the count is still one blade).

## Zero-sum and the audit

The plugin makes one base item when the blade comes into a bearer's hand and takes that same stack back when it leaves. Data is saved **before** a stack is made and **after** one is taken, so a crash can lose the blade but never make a second. Any other stack it ever made that turns up in a container is a copy and is removed. Swords the players made themselves are never touched. `/ironbreaker status` shows:

`Minted + Restored = Reclaimed + Unrecovered + (1 if in hand)`

"Unrecovered" counts stacks that left every container (dropped, destroyed) before they could be taken back. They are ordinary swords, with no power. If one turns up in a container later in the same session, it is removed and the counter goes down.

## API (`plugin.Call`, non-public)

| Method | Returns |
|---|---|
| `GetBearerName()` | the bearer's name, or null in the armoury (also while the bearer is away within the grace) |
| `GetBearerId()` | the bearer's SteamID64 string, or null |
| `IsBearer(string playerId)` | bool |
| `AwardEventPrize(string kind, string playerId, string playerName)` | true when the blade was given (RealmEvents calls it) |

Calls out: RealmChronicle `Log`, RealmHouses `GetHouse` / `GetLiege` / `HasTreaty`, RealmRenown `AddDeed`, RealmHerald `PopupsWanted`. All optional; a missing plugin only turns off the part that needs it (without RealmHouses, only the same game guild counts as "own side").

## Data

`oxide/data/RealmLegendary.json`: state (`keeping`, `borne`, `away`), the bearer, how they came by it, the away deadline, whether the blade is in keeping, the audit counters, recent passes (pruned after `PassPairCooldownHours`), bars and prize cooldowns, and the last 20 history lines. If the file exists but cannot be read (or is `null`), the plugin does nothing and never writes it; fix or move it away and reload.

## Tests

```
bash plugins/docs/RealmLegendary/logic-tests/run.sh      # 142 checks
bash tools/exploit-review/run.sh legendary               # 48 checks, fuzz included
```

Both compile `plugins/RealmLegendary.cs` and `plugins/RealmEvents.cs` unchanged with `logic-tests/Mocks.cs` (an item world in which stacks move between containers as objects, as [CODE] shows) and `logic-tests/World.cs` (players, a death that fills a corpse like the game, the audit). They prove the plugin's rules, not the game's behaviour.

## Smoke test on a real server

Do these on the owner's test server with two players (A and B, different houses) and an admin. Set `"DebugStrikes": true` first.

| # | Step | Expect |
|---|---|---|
| L1 | Load the plugin. Read the server log. `/ironbreaker items sword` | "The Ironbreaker takes the shape of '<name>'". Check in game that this is the largest two-handed sword (tooltip "Two-Handed"). If not, set `BaseItem` and reload. |
| L2 | `/ironbreaker grant A` | One sword in A's packs, the herald line, a window, `(Ironbreaker)` after A's name in global chat. |
| L3 | A hits B with the blade, then with another sword, then with fists. Read the log. | Strike lines name the path (`damager`, `right-hand` or `unknown`). Damage with the blade is larger (compare B's health loss). If the path is `unknown` for every weapon, decide `WhenWeaponUnknown`; if the blade is resolved but says "blade no", try `HeldMatch: "name"`. |
| L4 | B raises a shield; A strikes it with the blade. A hits a wooden gate and a block, with the blade and without. | More damage through the guard; gate and block break about twice as fast. |
| L5 | A puts the blade in a chest; A gives it to B. | It is back in A's packs within 3 s each time; B keeps nothing. |
| L6 | B kills A. Open A's corpse. | No blade in the corpse; one in B's packs; herald and Chronicle lines. |
| L7 | B logs out. Kill B's sleeping body; loot it. B logs back in within 15 min. | Nothing on the body. The blade is back in B's packs. |
| L8 | A hits B; B logs out at once. | The blade passes to A. |
| L9 | B drops the blade on the ground. | After about 6 s: back to the armoury, herald "cast the Ironbreaker away"; picking it up gives an ordinary sword (and if the same stack, it is removed). |
| L10 | Restart the server with a bearer online. | The bearer has exactly one blade after the restart. `/ironbreaker status` audit balances. |
| L11 | Run a Royal Tournament with three entrants (`/event start tournament 10`). | The champion receives the blade and the herald names it. |

## What is UNVERIFIED

Everything at run time. In particular:

1. Which item `BaseItemSearch` picks, and that it is the largest two-handed sword (L1).
2. That the server fills `Damage.Damager` or the right-hand holdable for a client's swing, and that `BipedHoldable.Stack` is the same object as the stack in the packs (L3).
3. That scaling `Damage.Amount` in `OnEntityHealthChange` and `OnCubeTakeDamage` changes the damage applied (the same basis as RealmEvents' truce and RealmWarden's raid hours) (L3, L4).
4. That `CombatUtil.IsBlocking` is up to date on the server for a remote player (L4), and that gates carry `PlaceableBlockAssociation` (L4).
5. That `OnKingDeath` fires for every player death before the corpse is filled; on the server `AncientThroneListener.OnPlayerDeath` cancels a `PlayerDeathEvent` that is not its own (`IsSender` false and `Sender` not the dying player) before calling the hook, which would skip it (the `OnEntityDeath` fallback then takes the blade out of the corpse) (L6).
6. That the packs are still reachable in `OnPlayerDisconnected` (if not, the next tick takes the stack off the sleeping body) (L7).
7. That `ItemCollection.RemoveItem(stack, true)` and `AutoMergeAdd` show at once on the client (L2, L5).
8. That a dropped stack keeps no `Collection` and that picking it up gives the same object (L9).
9. That the chat format change shows on clients (the same as RealmRenown's title prefix) (L2).
10. That the popup shows (L2).
