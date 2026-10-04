# RealmCrafts: the guilds of Ostreval

Eight professions with ranks and mastery, perks the server can really grant, a weekly Master Crafter, house workshops that pool their members' work, and a commission board where players post crafting orders others fill for marks held in escrow.

**Status.** Compile-checked at C# 3 against the real Oxide 2.0.3867 and patched game DLL metadata (`tools/plugin-compile-check/check.sh`). Mock-tested: 304 checks with the real RealmTreasury compiled in (`plugins/docs/RealmCrafts/logic-tests/run.sh`). Exploit suite: 71 checks including a 3000-step fuzz (`tools/exploit-review/run.sh crafts`). **Nothing here has been seen working on a real server.** Every game-side assumption is listed under [What is UNVERIFIED](#what-is-unverified), each with its smoke step.

Speaker in chat: **Guilds** (lang key `Speaker`). Permission: `realmcrafts.admin`. Files: `plugins/RealmCrafts.cs`, `oxide/config/RealmCrafts.json`, `oxide/data/RealmCrafts.json` (and `RealmCrafts_lastgood.json`, the last copy that loaded).

## What players see

1. **Working earns XP.** Chop wood, break stone and ore, pick flax and berries, harvest your farm, slay beasts and take their hides, work at a bench. Nothing to type: the guilds see the work. A level-up is one line (`Guilds: Smithing rises to level 12.`); a new rank says so and points at `/craft perks`.
2. **Ranks.** Novice (1), Apprentice (10), Journeyman (20), Expert (30), Artisan (40), Master (50). Reaching Expert or higher is told to the realm by the Herald (at most 6 heralds an hour). Each new rank is a RealmRenown deed (`craft_rank`, 10 renown).
3. **Mastery.** Level 50 in a profession: the player is named, for example, *Master Smith*. A window ("Master of Smithing"), a Herald line, a Chronicle entry (`title_earned`), the RealmRenown deed `craft_master` and with it the title **Guildmaster**.
4. **Perks you can feel.**
   - *Gatherers* get a bonus share of what they gather: 0.4 % per level (20 % at level 50), plus the workshop and holding points below, up to 50 %. The server gives the whole units into your packs every 30 s: `Guilds: Your skill finds you 6 more Wood, 2 more Iron Ore.`
   - *Crafters* get a chance of one more of what they just made: 0.2 % per level (10 % at 50), plus workshop and holding points, at most 10 extras a day: `Guilds: A master's touch: one more Iron Sword (Smithing).`
   - *Market fee.* From level 30 the realm market takes 10 % less fee when you sell goods of your craft, 20 % at 40, 30 % at 50 (RealmTreasury asks RealmCrafts on every sale). Commissions get the same discount.
5. **The weekly Master Crafter.** Every Sunday 20:00 UTC the player with the most XP of the week (1500 at least) is named Master Crafter: 250 marks from the treasury, the RealmRenown deed `master_crafter` and the title **Master Crafter**, a Chronicle entry, a window, and 3 season points for their house. `/craft top` shows the race.
6. **House workshops.** Every member's XP also goes into the house's workshop for the week. At 6,000 pooled XP the house keeps a *Workshop*, at 25,000 a *Guildhall*, at 75,000 a *Great Guildhall*. The tier (the better of this week and last week) gives every member +5/10/15 % XP, +2/4/6 yield points and +1/2/3 extra-item points, and at the crowning each house earns 2/4/6 season points. `/craft house` shows it.
7. **Holdings.** A house holding a RealmDominion holding of the right kind gets more: a mine +5 mining yield, a village +5 foraging yield and +2 cooking extra chance, a harbour +5 hunting yield, the keep +2 smithing, the crossroads +2 tailoring and carpentry.
8. **Commissions.** `/craft order Iron Sword 2 150` posts an order for two swords at 150 marks each; the 300 marks go into a treasury hold at once. Anyone (but the poster) fills it with goods from their packs: `/craft fill 7`. The goods go to the poster (or wait for `/craft collect`), the filler is paid from the hold less the market fee, and earns XP in the item's profession. A cancelled or lapsed order (72 h) gives the marks back. Orders of 500 marks or more are heralded.

The `/realm` hub lists `/craft` under **Coin, trade and contracts** (`/realm coin`).

## Commands

| Command | What it does |
|---|---|
| `/craft` | Your eight professions: rank, level, XP to the next level, today's XP against the daily cap; your house workshop; goods waiting |
| `/craft <profession>` | One profession in full: how it earns XP, the next rank, today and this week, your yield or extra-item chance, your market fee discount. Short names work (`smith`, `wood`, `cook`, `hunt` ...) |
| `/craft perks` | What each rank brings, the workshop tiers and the holding perks |
| `/craft top` | The week's ranking and the last Master Crafter; `top houses` the workshops; `top <profession>` the realm's best at it |
| `/craft house` | Your house's workshop: tier, pooled XP, next tier, top members, when you start to count |
| `/craft orders [mine\|<word>\|<profession>]` | Open commissions, best paid first; your own with their state |
| `/craft order <item> <qty> <marks each> [min level]` | Post a commission; the marks are held by the treasury at once |
| `/craft fill <id> [qty]` | Fill a commission (all you carry, up to what is left) |
| `/craft cancel <id>` | Cancel your own open commission; the hold comes back |
| `/craft collect` | Goods waiting for you (commission goods that did not fit) |
| `/craft help` | The command list |

Admin (`realmcrafts.admin`):

| Command | What it does |
|---|---|
| `/craft admin status` | Counts, which gathering items resolved to game items (and which did not), the next crowning, which partner plugins are loaded, whether the game's events are subscribed |
| `/craft admin watch <player> [off]` | Prints, to you, every gathering, transfer, hand-out and craft the plugin sees for that player and how it judged it (40 lines a minute at most). **The tool for the smoke test** |
| `/craft admin unmapped` | Products and stations made since the load that matched no profession (add them to `Crafting.Products` or the rules) |
| `/craft admin items <word>` | Item names in the game holding a word (for the config) |
| `/craft admin xp <player> <profession> <+/-n>` / `level <player> <profession> <level>` | Adjust a profession (logged to `oxide/logs`) |
| `/craft admin reset <player> confirm` | Wipe a player's professions (asks first, 60 s to confirm; logged) |
| `/craft admin crown` | Close the week now (logged) |
| `/craft admin cancel <id>` | Cancel any commission; the hold goes back to its poster (logged) |

## How the server sees the work

The game has no gather hook (`docs/oxide-rok-api.md` 3.9): the client gathers into its own packs and tells the server. RealmCrafts reads the game's own container events (`EventManager.Subscribe`, as RealmTreasury reads `ItemPassEvent`) and decides what is new:

- **Gathered** = goods a player's own client puts into a container on that player's own entity that came from nowhere the server knows. Not out of a container (a chest, a dropped item pack, a corpse, a station: seen a moment before as a Remove or Split by the same client, or as a merge whose source stack is still in a collection), and not handed over by the server (`ItemPassEvent`: salvage, broken crates, blocks picked up).
- **A farm's harvest** is the one hand-out that counts: the game hands the crops over inside the harvester's own `PlotCollectEvent`, so a `Loot` pass of a crop in `Gathering.LootCountsAsGather` counts only while that player's harvest is running. Salvaging a flax or grain good is a hand-out like any other.
- **Hunting**: a creature slain (`OnEntityDeath`, 5 to 80 XP by kind, 80 kills a day), and its goods taken from the corpse, or handed over while hitting it, at most 60 units per creature.
- **Crafting**: the station's `ItemCrafterCraftEvent` names who asked it to start; every product stack it makes (`ItemCrafterItemEvent`) is credited to that player. The profession is read from the product's name (`Crafting.ProductRules`, `Crafting.Products`) or else the station's name (`Crafting.StationRules`); `/craft admin unmapped` lists what matched nothing.

Server-made changes (gifts, kits, payouts, the plugin's own bonus) never count.

## Fair play

| Abuse | What stops it |
|---|---|
| Gather, drop, pick up again; chest shuffles; split and merge out of chests | Transfers, never gathers (above) |
| Craft and salvage loops | Salvage yields are hand-outs; per product a day 20 crafts at full XP, 40 at half, then none; every profession has a daily cap (2500 XP) |
| A flax or grain good salvaged back as "harvest" | A crop counts only inside the harvester's own `PlotCollectEvent` |
| Extra items as free inputs | 10 a day; never for ingots, flour or lumber (`Perks.NoExtraItems`) |
| Alts feeding a main through commissions | No commission XP between accounts from one address (salted hash, in memory only), once per pair every 12 h, none below half the market's last price, 300 a fill and 1500 a day |
| Alt armies pooling into a workshop | Members count after 24 h in the house (restarts on any change), 5000 XP each a week, 2 accounts from one address per house a week |
| Commission marks or goods duplicated | Marks live only in a RealmTreasury hold; a commission is closed and saved before the hold is released; owed goods leave the saved ledger before they are given, and only the shortfall returns |
| Staff naming themselves Master Crafter | Holders of `realmcrafts.admin` are never named while `General.AdminsCountForWeekly` is false |

The full list (K1 to K21) with its regression checks: [`tools/exploit-review/crafts/README.md`](../../tools/exploit-review/crafts/README.md).

## Config (`oxide/config/RealmCrafts.json`)

Every value is clamped to a safe range on load. A broken config file runs on the defaults for that load (logged). Every part has its own switch.

| Section.Key | Default | Meaning |
|---|---|---|
| `General.Enabled` | `true` | Master switch. Off: `/craft` says the guilds are closed and nothing is counted; admins keep `/craft admin` |
| `General.UsePopups` | `true` | The mastery and Master Crafter windows (the chat line is always sent) |
| `General.TickSeconds` / `SaveEverySeconds` / `MaxPlayersKept` / `AdminMaxAdjust` | `5` / `60` / `20000` / `100000` | |
| `General.AdminsCountForWeekly` | `false` | |
| `Levels.MaxLevel` / `XpCurveBase` / `XpCurveExponent` | `50` / `40` / `2.0` | XP to reach level L = 40 × (L − 1)²: level 10 at 3,240, level 50 at 96,040 |
| `Levels.DailyXpCap` | `2500` | Per profession per day (UTC). A profession takes at least 39 days to master |
| `Levels.RankLevels` / `HeraldFromRank` / `HeraldsPerHour` | `[1,10,20,30,40,50]` / `3` (Expert) / `6` | |
| `Gathering.Enabled` | `true` | Gathering and hunting XP |
| `Gathering.Items` | 31 goods | Item (a `ResourceType` name or an exact item name), profession, XP per unit |
| `Gathering.LootCountsAsGather` | Cabbage, Carrot, BrownBeans, Grain, Flax | Crops a farm's harvest counts for; `[]` turns farm XP off |
| `Gathering.Creatures` / `CreatureDefaultXp` / `CreatureCreditsPerDay` | 8 kinds / `15` / `80` | A word in the creature's name and its XP |
| `Gathering.TransitSeconds` / `PassSeconds` | `15` / `30` | How long goods taken out of a container, or handed over, count as on the move |
| `Gathering.UnitsPerItemPerDay` / `CorpseCreditPerContainer` | `8000` / `60` | |
| `Crafting.Enabled` / `DefaultCraftXp` | `true` / `15` | XP per product stack, times the largest `XpWeights` word in its name |
| `Crafting.FullXpCraftsPerProductPerDay` / `HalfXpCraftsPerProductPerDay` | `20` / `40` | Diminishing returns per product |
| `Crafting.Products` / `ProductRules` / `StationRules` / `Ignore` | `{}` / 4 rules / 4 rules / `[]` | Exact names win, then a word in the product's name, then a word in the station's name |
| `Perks.BonusYieldPercentPerLevel` / `BonusYieldMaxPerItemPerDay` / `BonusEverySeconds` | `0.4` / `500` / `30` | |
| `Perks.ExtraItemChancePerLevel` / `ExtraItemsPerDay` / `ExtraItemMaxUnits` / `NoExtraItems` | `0.2` / `10` / `20` / Ingot, Flour, Lumber | |
| `Perks.MarketDiscount` | 30: 10 %, 40: 20 %, 50: 30 % | Percent off the market fee from a level on (at most 50) |
| `Workshops.Enabled` / `MinMemberHours` / `MemberCapXpPerWeek` / `AccountsPerAddress` | `true` / `24` / `5000` / `2` | |
| `Workshops.TierXp` / `XpBonusPercent` / `YieldBonusPoints` / `ExtraChancePoints` / `SeasonPoints` | `[6000,25000,75000]` / `[5,10,15]` / `[2,4,6]` / `[1,2,3]` / `[2,4,6]` | |
| `Weekly.Enabled` / `CrownDay` / `CrownHourUtc` / `MinWeeklyXp` | `true` / `Sunday` / `20` / `1500` | |
| `Weekly.RewardMarks` / `HousePoints` / `TopCount` | `250` / `3` / `10` | A reward the treasury cannot pay yet is owed and paid later |
| `Dominion.Enabled` / `PollSeconds` / `Perks` | `true` / `300` / 7 perks | Holding kind, profession, yield or extra points |
| `Commissions.Enabled` / `MaxOpenPerPlayer` / `MaxOpen` | `true` / `5` / `300` | |
| `Commissions.MaxQty` / `MaxPrice` / `MaxTotal` / `DurationHours` / `PostCooldownSeconds` | `500` / `100000` / `1000000` / `72` / `30` | |
| `Commissions.FeePercent` | `-1` | `-1` = the market fee RealmTreasury charges |
| `Commissions.XpPerMark` / `XpMaxPerFill` / `XpPerDay` / `PairCooldownHours` / `MinPriceRatioForXp` / `XpWithinHouse` | `0.5` / `300` / `1500` / `12` / `0.5` / `true` | |
| `Commissions.MaxOwedLines` | `50` | Goods lines waiting for a poster before fills are refused |
| `Rewards.RankDeed` / `MasterDeed` / `WeeklyDeed` / `CommissionDeed` | `craft_rank` / `craft_master` / `master_crafter` / `commission_filled` | RealmRenown deed kinds; `""` = none |
| `Rewards.ChronicleMasters` / `QuestReports` | `true` / `true` | |

**Item and creature names.** The defaults use `ResourceType` names, the one item list the game's DLL holds. Whether each resolves to a carried item on a real server is UNVERIFIED: `/craft admin status` says which did not, and `/craft admin items <word>` finds the real names.

## Game API used (research notes)

Read in the decompiled shipped patched `Assembly-CSharp.dll` (type and member names only; no game code is in this repo) and confirmed by the compile check against its metadata:

- **Container events:** `CodeHatch.Networking.Events.Containers.ContainerItemAddEvent` (`ItemStack`, `Slot`), `ContainerItemMergeEvent` (`ItemStack`, `TargetStack`, `Quantity`), `ContainerItemRemoveEvent`, `ContainerItemSplitEvent` (`ResultStack`, `Quantity`); `NetworkEvent.Sender`, `IsSender` (`Sender == Player.Local`), `BaseEvent.Cancelled`; `EntityEvent.Entity`, `ContainerEvent.Container`; `InvGameItemStack.CollectionInterface`, `ItemCollection.Container`; `CodeHatch.ItemContainer.LootableCreatureContainer`; `Container.Entity` (a `Container` is an `EntityBehaviour`). Read by `ContainerListener.OnContainerItemAdd` / `OnContainerItemMerge`.
- **Hand-outs:** `CodeHatch.Networking.Events.Item.ItemPassEvent` (`Recipient`, `Memo`, `ItemStack`); raised with the memo `"Loot"` by `SalvageSupplier.AddToInventory`, `DamagableContainer.GiveItem`, `FarmManager.AddToInventory` and `InventoryUtil` (block pick-ups).
- **Harvest and blows:** `CodeHatch.Farming.PlotCollectEvent` (handled by `FarmListener.OnPlotCollect`, which calls `FarmManager.CollectPlot` with the event's `Sender`); `CodeHatch.Networking.Events.Entities.EntityDamageEvent` (handled by `DamagableContainer.OnEntityDamage`). Subscribed at `EventHandlerOrder.VeryEarly` and `VeryLate` with `EventCancelFlags.InvokeHandler` (so a cancelled event still clears the mark; `Subscription` invokes such handlers on cancelled events).
- **Crafting:** `ItemCrafterCraftEvent` (`Sender`, `Crafter`), `ItemCrafterItemEvent` (`Stack`, `Crafter`), the hook `OnItemCrafted(ItemCrafterFinishEvent)` [OPJ L1088].
- **Creatures:** `OnEntityDeath(EntityDeathEvent)` [OPJ L188]; a creature as `SalvageSupplier` tells one: `Entity.TryGet<MonsterMotor>()` or `TryGet<MonsterEntity>()`; the killer `KillingDamage.DamageSource.Owner`.
- **Items:** the `/give` path (`ThronesCommandHandler.Give`): `PlayerExtensions.GetInventory`, `Container.Contents`, `new InvGameItemStack(blueprint, n, null)`, `ContainerManagement.StackLimit`, `ItemCollection.AutoMergeAdd`, `AutoCount`, `AutoSplit`; lookups `InvBlueprints.Instance.GetBlueprintForResource(ResourceType)`, `GetBlueprintForName(string, bool, bool)`, `InvBlueprints.GetBlueprintsContaining(string)`.
- **Addresses:** `Player.Connection.IpAddress`, kept only as a salted SHA-256 prefix in memory.
- **Popups:** `PlayerExtensions.ShowPopup(title, message, buttonText, handler, interupt, broadcast)` in the `Popups` region, inside `try`, `broadcast = true`; notices only, no answers awaited.
- **Commands:** no game command is called `craft`.

**What the game does not offer, and the design around it.** There is no gather hook and no per-node yield to scale, so bonus yield is a server gift in batches after the fact, not a bigger swing. There is no item flag for "made by", so the market discount is by the item's profession, not by who made that very item. There is no guild-hall object, so the workshop is a house's shared ledger, not a place.

## Integration

| Calls into | Methods |
|---|---|
| RealmTreasury | `HoldMarks`, `PayFromHold`, `ReleaseHold`, `GetHold` (commission escrow), `ChargeMarks` (the commission fee to the crown), `RewardMarks` (the weekly reward), `GetPurse`, `GetLastPrice` (the price floor for commission XP), `GetTreasurySummary` (the market fee) |
| RealmRenown | `AddDeed` (`craft_rank`, `craft_master`, `master_crafter`, `commission_filled`; added to RealmRenown's defaults with the titles **Guildmaster** and **Master Crafter**, each with a badge in the art pack) |
| RealmHouses | `GetHouse` |
| RealmSeasons | `AwardHouse` (Master Crafter's house, workshop tiers) |
| RealmChronicle | `Log` with the existing type `title_earned` (a new master, the Master Crafter). No new Chronicle type |
| RealmQuests | `ReportQuestEvent(id, "custom", "commission" or "mastery", 1)`. The weekly task **The Guild's Book** (`w_guild_book` in `plugins/docs/RealmQuests/content/Weeklies.json`) counts filled commissions, or 40 crafted goods |
| RealmDominion | `GetHoldings` (holding kinds per house, every 5 minutes) |
| RealmHerald | `PopupsWanted` |
| RealmSentinel | `SentinelItemSource` before every gift (bonus yield, extra items, commission goods) |

Asked by another plugin: **RealmTreasury** calls `GetMarketFeeDiscount(string playerId, string item)` → `int` (0 to 50) in its market fee (a three-line change in `FeeOn`; 0 without RealmCrafts).

Offered to other plugins (`plugin.Call`, non-public): `GetMarketFeeDiscount(string playerId, string item)` → `int`, `GetProfessionLevel(string playerId, string profession)` → `int`, `GetMasterCrafter()` → `string` (or null), `GetWorkshopTier(string house)` → `int` (0 to 3), `GetCraftSummary(string playerId)` → `"smithing:23|mining:5"`.

**Not duplicated.** RealmQuests already counts crafts and deliveries for its tasks; RealmCrafts only reports what RealmQuests cannot see (commissions, mastery). RealmTravel's kits and RealmArena have no crafting part; a guild kit or a crafters' tourney would be content for those plugins, not code here. The commission board is its own small board because RealmTreasury's market sells goods that exist; a commission asks for goods that do not exist yet, so it holds only marks (through RealmTreasury's own hold API) and takes the goods at the fill.

## Data

`oxide/data/RealmCrafts.json`: per player the professions (XP, level, today, this week), the day's counters, bonus fractions owed, the house and since when, Master Crafter wins and goods owed; per house the workshop's week; the commissions; the weekly history (52 weeks); weekly rewards the treasury still owes. Session state (goods on the move, crafts in progress, addresses) is never saved. If the file exists but cannot be read (cut off, empty, or written by a newer version) the plugin pauses, `/craft` says so, and **the file is never overwritten**; commission marks stay in the treasury's holds and lapse back to their posters. Restore `RealmCrafts_lastgood.json` (written at every good load) or move the file away, then reload.

## Tests

```
bash tools/plugin-compile-check/check.sh                 # C# 3 against the real DLL metadata
bash plugins/docs/RealmCrafts/logic-tests/run.sh          # 304 checks, with the real RealmTreasury
bash tools/exploit-review/run.sh crafts                   # 71 checks with a 3000-step fuzz
```

The logic tests compile `plugins/RealmCrafts.cs` and `plugins/RealmTreasury.cs` unchanged with `logic-tests/Mocks.cs` (packs, chests, corpses and dropped item packs holding real stacks; the game's container, item-pass, harvest, damage and crafting events raised in their handler order, with the game's rule for cancelled events) and `logic-tests/World.cs` (stand-ins for the other plugins and a clock). They cover gathering and every transfer, farm harvests, corpses and blows, hunting, caps, levels and ranks, mastery, bonus yield, crafting rules and diminishing returns, extra items, the market fee discount through the real treasury, workshops, holding perks, the weekly crowning, commissions with their escrow and the treasury's zero-sum audit, commission XP rules, commands, admin, popups, switches, reloads, damaged data and config clamps. They prove the plugin's rules, not the game's behaviour.

## Smoke test on a real server

Two players (A, B; different houses, different networks) and an admin, on the owner's test server. The admin runs `/craft admin watch A` for steps C2 to C8 and reads what the plugin saw. Give A some marks first.

| # | Step | Expect |
|---|---|---|
| C1 | Load the plugin; read the log. `/craft admin status` | No errors. "Container events subscribed: yes. Harvest and creature events: yes." Gathering items resolved: note any unresolved names and fix them with `/craft admin items <word>`. |
| C2 | A chops a tree, breaks stone and iron ore, picks flax in the wild. | Watch: "N Wood gathered: Woodcutting +XP" and the same for each; `/craft` shows the XP. If nothing is seen, the container events do not reach plugins: gathering XP cannot work as built (report it). |
| C3 | A puts wood in a chest and takes it back; drags a chest stack onto a carried one; splits a stack out of a chest. | Watch: "moved (not gathered)" each time; no XP. |
| C4 | A drops wood on the ground and picks it up; B picks up a stack A dropped. | "moved (not gathered)" for both; no XP. If a pick-up shows "gathered", note what event it came as. |
| C5 | A salvages a placed object (a bed, a wall) with a salvage hammer; breaks a crate. | "handed over by the game (Loot)"; no XP when the goods land. |
| C6 | A plants and harvests a farm plot (cabbage or flax). | "harvested (counts when it lands)", then "gathered: Foraging +XP". |
| C7 | A kills a wolf; loots its corpse; then hits the corpse (if the game gives goods by blows). | "slew wolf: Hunting +40"; corpse goods "corpse: Hunting +XP", at most 60 units per creature. Note the creature's name as the watch prints it (fix `Gathering.Creatures` if the words do not match). |
| C8 | A crafts at a smithy, a tannery, a campfire and by hand. | "made 1 <product>: Smithing +XP" (and so on); `/craft admin unmapped` lists products or stations that matched nothing: add them to the config. |
| C9 | Admin: `/craft admin level A mining 50`; A mines stone for a minute. | The "Your skill finds you N more Stone" line every 30 s and the stone appears in the packs at once; RealmSentinel stays quiet. |
| C10 | Admin: `/craft admin level A smithing 50`; A crafts many items. | About 1 in 10 gives "A master's touch: one more ..." and the item appears; at most 10 a day. |
| C11 | A (smithing 50) sells an iron sword on `/market`; B buys it. | The fee is 30 % lower than for a level 1 seller (`/treasury ledger`). |
| C12 | A: `/craft order Iron Ingot 5 20`; B: `/craft fill <id>`. A: `/purse` before and after. | 100 marks leave A at once; B is paid less the fee; the ingots reach A's packs (or `/craft collect`); B gets smithing XP (different networks) and A sees "filled". Cancel one and let one lapse: the marks come back. |
| C13 | A reaches level 50 (admin `level 49` and some work), with popups on. | The "Master of ..." window, the Herald line, `/titles` shows Guildmaster, `/chronicle` shows the entry. |
| C14 | Admin: `/craft admin crown` with A on top of `/craft top`. | The Herald names A; 250 marks to A; the Master Crafter window and title; season points for A's house (`/season standings`). |
| C15 | Two members of one house earn XP for a day; `/craft house`. | The pooled XP and, past 6,000, "a Workshop"; members get +5 % XP. |
| C16 | Restart the server. | Levels, the week, commissions (and their holds in `/treasury`) and goods owed are all still there. |

## What is UNVERIFIED

Everything at run time. In particular:

1. That a client's gathering reaches the server as `ContainerItemAddEvent` / `ContainerItemMergeEvent` with the client as `Sender`, and that plugins subscribed to `EventManager` see them (C2). This is the foundation: if it fails, gathering XP and bonus yield do nothing, while hunting kills, crafting and commissions still work.
2. That a chest move arrives as a Remove then an Add of the same stack, a split as a Split, and a drag from a chest as a merge whose source is still in its collection (C3).
3. That a dropped item becomes an item pack container the pick-up is taken from (C4).
4. That salvage, broken crates and block pick-ups go through `ItemPassEvent` with the stack the client then adds (C5).
5. That a farm hands its crops over inside the harvester's `PlotCollectEvent` (C6).
6. That corpses are `LootableCreatureContainer`s, that goods given by blows come inside the `EntityDamageEvent`, and the creatures' real names (C7).
7. That `ItemCrafterCraftEvent.Sender` names the player who started a station, and the real product and station names (C8).
8. That the default gathering names resolve to carried items, and that server-side `AutoMergeAdd` shows in the client's packs at once (C1, C9, C10).
9. That RealmTreasury's fee change and holds behave the same on the server as in the tests (C11, C12).
10. The windows on a real client (C13, C14).
11. That a restart keeps everything (C16).
