# RealmTreasury: the Crown's treasury, house vaults and the market of Ostreval

`plugins/RealmTreasury.cs` (Oxide 2.0.3867, C# 3) adds an economy to the realm:

- **The royal treasury.** It holds real goods and **marks**, a ledger currency. Anyone may pay tribute into it. The crown fills it through the **house tithe** and the **market fee**. The monarch can also bring in what the game's own tax chest collected.
- **House vaults.** Every house (from RealmHouses) has a vault of goods and marks. Any member may deposit. Only the head of the house and up to three named **stewards** may take things out, and only within a daily budget.
- **The market.** Players post **asks** (`/market sell`, the goods are held in escrow) and **bids** (`/market bid`, the marks are held in escrow). Others trade against them with `/market buy` and `/market fill`. Orders expire, a capped fee goes to the treasury, and every trade goes into a public **price history**.
- **The minting decree.** The monarch strikes new marks into the treasury. This is **accounting only**: no command in this plugin creates an item.

Lore: the Hearth Charter says the crown's power is *lent, not owned*. The crown may strike coin, but only a little at a time and in public. It may tithe its sworn houses, but never above the Charter's ceiling. The **Keeper of Coin** (a CrownAndConsequences council seat) keeps the treasury's books beside the monarch.

Status: **compile-checked against the real 2.0.3867 DLLs (0 errors, 0 warnings) and behaviour-tested against mocks (118 checks, including a 4000-step randomized zero-sum fuzz). Never run on a live server.** See [What is UNVERIFIED](#what-is-unverified).

---

## Commands

Item names may contain spaces and need no quotes (`/market sell 10 5 Iron Ingot`). Item names are exact but not case-sensitive. Use `/market items <search>` to find one. Order ids may be written `12` or `#12`.

### Help and purse

| Command | Who | What it does |
|---|---|---|
| `/economy` | anyone | Shows all the help, plus the current market fee, tithe and game tax. Admins also see the admin lines. `/market help`, `/vault help` and `/treasury help` each show their own part. |
| `/purse` | anyone | Shows your marks. |
| `/purse pay <player> <n>` | anyone | Pays marks to an online player. Limits: `PayMaxPerDay` (rolling 24 h) and `PayCooldownSeconds`. |

### `/market`

| Command | What it does |
|---|---|
| `/market list [item\|mine]` | Lists the open orders: asks cheapest first, then bids dearest first. At most `MaxListLines` lines are shown. |
| `/market history <item>` | Shows the last price, the 24 h and 7 d volume-weighted averages, the low and high, the trade count, and the last 5 trades. |
| `/market items <search>` | Finds item names. |
| `/market sell <qty> <price each> <item> [NNh]` | Posts an ask. The goods are **taken from your inventory now** (measured) and held until they are sold or the order closes. `NNh` sets the expiry (default `DefaultListingHours`, at most `MaxListingHours`). |
| `/market bid <qty> <price each> <item> [NNh]` | Posts a buy order. `qty × price` marks are **taken from your purse now** and held. |
| `/market buy <id> [qty]` | Buys from an ask, all of it or `qty`. Your marks go to the seller, less the fee, which goes to the treasury. The goods are delivered to your inventory. If your packs are full, the rest is owed (see `collect`). |
| `/market fill <id> [qty]` | Sells into a bid. The goods are taken from your inventory (measured) and you are paid from the bid's escrow, less the fee. |
| `/market cancel <id>` | Withdraws your order. The goods or marks it still holds come back. |
| `/market collect` | Delivers goods the realm owes you (bought while your packs were full, or an order returned). This also happens on its own every 30 s while you are online. |

You cannot trade with your own order. A steward or treasurer also cannot trade with an order they posted for a vault or the treasury.

### `/vault` (house vaults; needs RealmHouses)

| Command | Who | What it does |
|---|---|---|
| `/vault [house]` | anyone | Shows a house vault: its marks, its goods (top 15) and its stewards. Vaults are public, like the Chronicle. |
| `/vault deposit <qty> <item>` | any member | Moves goods from your inventory into your house vault. Capacity is checked first. |
| `/vault give <n>` | any member | Moves marks from your purse into the vault. |
| `/vault withdraw <qty> <item>` | head or steward | Moves goods from the vault to you. Counts toward `VaultItemsOutPerDay`. |
| `/vault take <n>` | head or steward | Moves marks from the vault to your purse. Counts toward `VaultMarksOutPerDay`. |
| `/vault sell\|bid <qty> <price> <item> [NNh]` | head or steward | Posts a market order **for the house**. The goods or marks come from the vault and the proceeds go back to it. |
| `/vault buy\|fill <id> [qty]` | head or steward | Trades for the house. Vault marks pay; goods land in the vault. |
| `/vault cancel <id>` | head or steward | Withdraws a house order. |
| `/vault steward add\|remove <player>` | head | Names or removes a steward. The steward must be of the house (at most `MaxStewards`). |

### `/treasury`

| Command | Who | What it does |
|---|---|---|
| `/treasury` | anyone | Shows the treasury's marks and goods, the market fee, the tithe and its ceiling, the marks in existence and their cap, and the **game tax** (`KingsScheme.GetTax()`, shown as a percent like the game does) with its maximum (`KingsRealm.TaxMaximum`). |
| `/treasury tax` | anyone | Shows the game tax rate, what the plugin has **observed** reaching the game's tax chest, and what has been **paid into** the treasury with `taxin`. |
| `/treasury ledger [n]` | anyone | Shows the last `n` (at most 25) journal entries. The books are public. |
| `/treasury deposit <qty> <item>` | anyone | Pays tribute: goods from your inventory into the treasury. |
| `/treasury taxin <qty> <item>` | monarch, Keeper of Coin | The same as a deposit, but recorded as tax income. Use it after emptying the game's tax chest in game. |
| `/treasury withdraw <qty> <item>` | monarch, Keeper of Coin | Goods to yourself. Counts toward `TreasuryItemsOutPerDay`. |
| `/treasury award <player> <qty> <item>` | monarch, Keeper of Coin | Goods to an online player. Same budget. Chronicled. |
| `/treasury grant <player> <n>` | monarch, Keeper of Coin | Marks to an online player's purse. Counts toward `TreasuryMarksOutPerDay`. Chronicled. |
| `/treasury sell\|bid\|buy\|fill\|cancel …` | monarch, Keeper of Coin | Market orders for the crown, as with `/vault`. The treasury pays **no fee to itself**. |
| `/treasury mint <n>` | monarch | **Minting decree.** Strikes `n` marks into the treasury. Limits: `MintMaxPerDecree`, `MintMaxPerDay` (rolling), `MintSupplyCap` (total ever) and `MintCooldownMinutes`. Broadcast and chronicled. |
| `/treasury tithe <percent>` | monarch | Sets the house tithe, from 0 up to `TitheMaxPercent`. Limited by `TitheChangeCooldownHours`. |
| `/treasury levy` | monarch | Gathers the tithe now (at most once per `TitheLevyIntervalHours`). Each eligible house pays `tithe%` of each item in its vault (capped at `TitheMaxUnitsPerItemPerHouse` per item) and of its marks (capped at `TitheMaxMarksPerHouse`). The crown's own house is exempt. By default only houses **sworn to the crown** pay (CrownAndConsequences `IsSwornToCrown`), so rebels do not. |
| `/treasury fee <percent>` | monarch | Sets the market fee, from 0 up to `MarketFeeMaxPercent`. Limited by `FeeChangeCooldownHours`. |
| `/treasury escheat <house>` | monarch or admin | A house that **no longer exists** in RealmHouses (no head and no members) forfeits its vault to the crown. Its open orders are cancelled first, so their escrow joins the vault. |
| `/treasury audit` | admin (`realmtreasury.admin`) | Runs the zero-sum audit (below). Mismatches go to the server log. |
| `/treasury freeze` / `unfreeze` | admin | Closes or reopens the market to new orders and trades. Cancels and expiry still work. |
| `/treasury cancel <id>` | admin: any order; treasurer: crown orders | Cancels an order and returns its escrow to the owner. |

Admins get no command that adds goods or marks. They skip cooldowns, but not caps or budgets.

---

## How it works

### Custody, and why it is zero-sum

Every good the plugin holds is in the **realm's custody**, and custody has exactly four places: the treasury, the house vaults, the open asks, and the **owed** ledger (goods waiting for a player whose packs were full). Goods enter custody **only** by a measured take from a player inventory. They leave **only** by a measured give to a player inventory. Every other move goes from one custody place to another.

The plugin keeps two counters per item: `ItemsIn` (units measured out of inventories) and `ItemsOut` (units measured into inventories). The audit checks, for every item:

```
ItemsIn[item] - ItemsOut[item]  ==  treasury + Σ vaults + Σ open asks + Σ owed
```

and, for marks:

```
MarksMinted  ==  treasury + Σ vaults + Σ purses + Σ open bid escrow
```

It also checks that no closed order still holds anything and nothing is negative. The audit runs on every load (a mismatch is logged and **nothing is changed**), on `/treasury audit`, and after every step of the tests. In the tests, a stronger check also holds after every step: *sum of all player inventories + custody == the goods the test created*.

### Escrow (the dup-safe pattern from RealmContracts)

- **Take:** `ItemCollection.AutoCount`, then `AutoSplit`, then `AutoCount` again. Only the measured difference counts. If fewer units come out than asked, nothing is posted and the units that did come out are returned through the owed ledger.
- **Give:** `GetInventory()` + `AutoMergeAdd`, in chunks of `ContainerManagement.StackLimit`, measured each time. These are the calls the game's own `/give` makes.
- **Order of work:** state changes first, then the data file is written, and only then are goods paid out. Owed balances drop only by what was measured as delivered, and the file is written after each delivery. A crash between a game call and the save can **lose** escrow. It can never **duplicate** it.
- Capacity and budgets are checked **before** anything is taken. Returned escrow may push a vault over its cap, so nothing is ever lost.

### The game's tax (read only)

The tax rate comes from `KingsScheme.GetTax()`, the fraction of each gathered amount. The cap comes from `KingsRealm.TaxMaximum`, and the setting from CrownAndConsequences, which keeps capping it. The plugin never changes the tax.

The game's taxed goods do **not** pass through this plugin. In the shipped DLL, the gathering **client** sets the taxed share aside and sends it to the server as an `ItemPassEvent` with memo `"Tax"`. The server's `TaxCollector` puts it into the containers fitted with a `TaxContainer` (the tax chest), or into a hidden stash. This plugin subscribes to the same event **read only**, with `EventManager.Subscribe<ItemPassEvent>(…, EventHandlerOrder.VeryEarly)`, and adds the stacks up under "observed tax". Observed tax is **never credited**, for two reasons: the goods are already in the game's chest (crediting them would duplicate them), and the event comes from clients, so it could be spoofed. Each event is also clamped to `TaxObservedMaxPerEvent`. The monarch empties the chest in game and pays the goods in with `/treasury taxin`, which is an ordinary measured deposit. `/treasury tax` shows observed against paid in, which shows the realm whether the crown is skimming.

### Marks

Marks are a pure ledger currency. They are created only by `/treasury mint`, and the caps above bound their total supply. Nothing destroys them: market fees and tithes move them to the treasury. They reach players through grants, crown bids and market sales. Marks can never be turned into items by the plugin. They only buy goods that other parties put in escrow.

### Logging

Every movement is a journal entry: sequence, time, kind, actor, asset, amount, from, to and note. The last `JournalMax` entries are kept in the data file for `/treasury ledger`. **Every** entry also goes to `oxide/logs/RealmTreasury/realmtreasury_ledger-YYYY-MM-DD.txt` as tab-separated text (`LedgerToLogFile`). Entry kinds: `tribute`, `tax_in`, `vault_deposit`, `vault_withdraw`, `treasury_withdraw`, `treasury_award`, `escrow_in`, `escrow_out`, `trade`, `market_fee`, `paid_out`, `return`, `pay`, `marks_in`, `vault_take`, `treasury_grant`, `mint`, `tithe`, `tithe_rate`, `fee_rate`, `escheat`, `steward_added`, `steward_removed`, `market_frozen`, `market_open`.

### Data and safety

- `oxide/data/RealmTreasury.json` holds everything. It is written after every escrow change and on server save.
- **Corruption-safe load.** If the file fails to parse, or exists but is empty or `null`, the plugin logs an error, **does not load** and **never writes** the file. Fix it, or restore `oxide/data/RealmTreasury_lastgood.json`, then `oxide.reload RealmTreasury`.
- `RealmTreasury_lastgood.json` is written after every load that passes the audit.
- Bounded growth: at most `ClosedListingsKept` closed orders, `TradeHistoryMax` trades and `JournalMax` journal entries. There is one owed entry per (player, item). Spend records and cooldowns are pruned after 24 h.

---

## Configuration (`oxide/config/RealmTreasury.json`)

Values are clamped on load, and the clamped config is written back.

| Key | Default | Meaning |
|---|---|---|
| `CurrencyName` | `marks` | Name shown in chat. |
| `TreasurerSeat` | `Keeper of Coin` | The CrownAndConsequences council seat that may act for the treasury. Empty means the monarch only. |
| `ObserveGameTax` | `true` | Subscribe to the game's tax event (read only). |
| `TaxObservedMaxPerEvent` | 1000 | Clamp per observed tax event. |
| `MaxPerCommand` | 1000 | Most units of one item a single command moves. |
| `TreasuryMaxUnitsPerItem` / `TreasuryMaxItemTypes` | 100000 / 200 | Treasury capacity. |
| `TreasuryItemsOutPerDay` / `TreasuryMarksOutPerDay` | 1000 / 5000 | Rolling 24 h outflow budget (withdraw, award, crown asks / grants, crown bids and buys). |
| `TreasuryActionCooldownSeconds` | 10 | Between treasury deposits and withdrawals. |
| `MintMaxPerDecree` / `MintMaxPerDay` / `MintSupplyCap` / `MintCooldownMinutes` | 1000 / 2000 / 100000 / 60 | Mint limits. The supply cap is at most 10^9. |
| `TitheMaxPercent` / `TitheDefaultPercent` | 10 / 0 | The Charter's ceiling (at most 50) and the starting rate. |
| `TitheChangeCooldownHours` / `TitheLevyIntervalHours` | 24 / 24 | |
| `TitheItems` / `TitheMarks` | true / true | What a levy takes. |
| `TitheOnlySwornHouses` | true | Only houses sworn to the crown pay. This needs CrownAndConsequences; without it the levy is refused. |
| `TitheMaxUnitsPerItemPerHouse` / `TitheMaxMarksPerHouse` | 500 / 2000 | Per-levy caps. |
| `VaultMaxUnitsPerItem` / `VaultMaxItemTypes` | 10000 / 40 | House vault capacity. |
| `VaultItemsOutPerDay` / `VaultMarksOutPerDay` | 1000 / 5000 | Rolling 24 h outflow budget per house. This limits the damage from a rogue head or a stolen account. |
| `VaultActionCooldownSeconds` / `MaxStewards` | 5 / 3 | |
| `MarketFeeDefaultPercent` / `MarketFeeMaxPercent` / `FeeChangeCooldownHours` | 2 / 10 / 12 | The fee is rounded down. The treasury pays no fee. |
| `MaxOpenPerOwner` / `MaxOpenTotal` | 5 / 200 | Open orders per owner (a player, the treasury or a house) and in all. |
| `PostCooldownSeconds` / `TradeCooldownSeconds` | 30 / 2 | |
| `MaxPricePerUnit` | 100000 | At most 10^6. |
| `DefaultListingHours` / `MaxListingHours` | 24 / 72 | |
| `TradeHistoryMax` / `ClosedListingsKept` | 500 / 100 | |
| `PayMaxPerDay` / `PayCooldownSeconds` | 5000 / 10 | `/purse pay` limits. |
| `JournalMax` / `LedgerToLogFile` | 1000 / true | |
| `MaxListLines` | 12 | Chat output cap. |
| `ChronicleMaxPerHour` / `ChronicleTradeMinMarks` | 10 / 1000 | Chronicle throttle, and the smallest trade (in marks) that is chronicled. |
| `AllowedItems` / `BlockedItems` | `[]` / `[]` | Exact item names. An empty `AllowedItems` allows any item. |

Permission: `realmtreasury.admin` (`oxide.grant user <name> realmtreasury.admin`).

---

## Integration

### Chronicle event types

> **Registered.** These types are now in `plugins/RealmChronicle.cs` `KnownTypes`, `chronicle/server.js` `EVENT_TYPES` and `chronicle/public/assets/common.js` `TYPE_META` (a chronicle test keeps the three in step). The fallback described here only applies to an older RealmChronicle.

They are:

| type | label | icon | when |
|---|---|---|---|
| `treasury_mint` | The Crown Strikes Coin | coins | `/treasury mint` |
| `treasury_grant` | Royal Largesse | coins | `/treasury grant` and `award` |
| `tithe_levied` | The Tithe Gathered | scroll | `/treasury levy` |
| `great_trade` | A Great Sale | seal | a trade worth at least `ChronicleTradeMinMarks` |

Until they are registered, RealmChronicle rejects them (`Log` returns 0). The plugin then re-sends mints, grants and levies as `decree`, and drops great trades. Rate and fee changes and escheats always use `decree`.

### Calls this plugin makes (all optional; a missing plugin returns null)

| Plugin | Method | Used for |
|---|---|---|
| RealmHouses | `GetHouse(playerId)`, `GetHouseLeader(house)`, `GetMembers(house)` | Vault membership, the head, escheat |
| CrownAndConsequences | `GetCouncilSeat(ulong)`, `IsSwornToCrown(house)` | The Keeper of Coin; tithe eligibility |
| RealmChronicle | `Log(type, title, detail, actors)` | Public record |

### API for other plugins (`plugin.Call`, all non-public)

| Method | Returns |
|---|---|
| `GetPurse(string playerId)` | `long` marks |
| `GetTreasuryMarks()` | `long` |
| `GetTreasuryItem(string item)` | `int` units held |
| `GetLastPrice(string item)` | `long` last price per unit, or 0 |
| `GetTreasurySummary()` | `"marks\|minted\|feePct\|tithePct\|openOrders"` |

---

## Tests run here

1. `tools/plugin-compile-check/check.sh`: compiles all of `plugins/*.cs` together at `-langversion:3` against the real Oxide.ReignOfKings 2.0.3867 metadata (zip sha256 `6c35c623…c72c6c8`). Result: **OK, 0 errors, 0 warnings.** This proves the game and Oxide calls exist with these signatures: `EventManager.Subscribe/Unsubscribe<ItemPassEvent>`, `EventSubscriber<T>`, `EventHandlerOrder.VeryEarly`, `ItemPassEvent.Memo`/`ItemStack`, `InvGameItemStack.Blueprint`/`StackAmount`, `KingsScheme.GetTax/HasKing/IsKing/GetKingID`, `KingsRealm.TaxMaximum`, `DataFileSystem.ExistsDatafile`, `LogToFile`, `PrintToChat`, and the escrow calls.
2. `plugins/docs/RealmTreasury/logic-tests/run.sh`: compiles the **unchanged** plugin with `Mocks.cs` and `Tests.cs` and runs **118 checks, all passing**. They cover:
   - mint caps and roles
   - grants, purses and pay
   - asks, bids, fees, partial buys and fills
   - owed and collect with full packs
   - stack-limited chunked gives
   - partial-take faults
   - expiry
   - vault deposit, withdraw and capacity, stewards, daily budgets, house and crown market orders, and self-dealing guards
   - tribute, `taxin`, withdraw and award
   - tax observation (memo filter, clamp, never credited)
   - tithe ceiling, roles, exemptions, interval and amounts
   - freeze, ledger and log file, audit
   - escheat
   - cooldowns
   - a **4000-step randomized fuzz** over every command, with random full packs and random short takes. After every step: inventories + custody == seeded, and the audit balances.
   - tamper detection
   - reload from disk
   - corrupt and empty data files refused and never overwritten
   - config clamps

   What the tests do **not** prove: that the real game behaves like the mocks.

---

## Smoke test (owner's PC, after `docs/smoke-test.md` stage C passes)

You need a monarch **K** (head of a house, sitting on the throne), a player **A** of another house sworn to K's house, and a player **B**. Give K `realmtreasury.admin`. Have RealmHouses, CrownAndConsequences and RealmChronicle loaded. Make A the Keeper of Coin with `/council` if you want to test the treasurer role.

| # | Do | Expect |
|---|---|---|
| T1 | `oxide.reload RealmTreasury`, then `/economy` | Help prints. `oxide/config/RealmTreasury.json`, `oxide/data/RealmTreasury.json` and `RealmTreasury_lastgood.json` exist. No "Audit on load" warning. |
| T2 | `/treasury` | The game tax shows the same percent as the throne's tax UI. *(Tests GetTax units.)* |
| T3 | Gather wood with B (not of K's house) while the tax is above 0, then `/treasury tax` | Observed Wood grows by about tax × gathered, and the tax chest holds about the same. *(Tests the ItemPassEvent subscription, UNVERIFIED.)* If nothing is observed, set `ObserveGameTax` to `false`; the rest still works. |
| T4 | As K: `/treasury mint 500`, then `/treasury grant B 200` | A broadcast, and a chronicle line (`treasury_mint`, or `decree` before registration). `/purse` for B shows 200. |
| T5 | Give A 20 Wood. As A: `/market sell 20 3 Wood` | A's inventory loses **exactly** 20 Wood **at once**. *(Tests take + client refresh, UNVERIFIED.)* |
| T6 | As B: `/market buy <id> 5` | B gets 5 Wood at once, and B's purse drops by 15. `/market history Wood` shows the trade. |
| T7 | Fill B's packs (no free slots), then `/market buy <id> 5` | B is told the goods wait. After freeing slots and running `/market collect`, B receives exactly 5. No extra Wood appears at any point. |
| T8 | Put 5 Wood on B's **hotbar** only and try `/market sell 5 1 Wood` | Expected: "you need 5 (you have 0)". *(Tests the hotbar question, UNVERIFIED.)* |
| T9 | As A: `/vault deposit 10 Wood`, then as B (not a steward) `/vault withdraw 1 Wood` | The deposit succeeds. The withdraw is refused. |
| T10 | As K: `/treasury tithe 10`, then `/treasury levy` | A's house vault pays 1 Wood. A chronicle line appears. |
| T11 | As K: `/treasury audit` | "every item and every marks is accounted for". |
| T12 | Stop the server, put `{ broken` into `oxide/data/RealmTreasury.json`, start it | An error naming `RealmTreasury_lastgood.json`. The plugin is not loaded. After a server save the file still holds `{ broken`. Restore the good file and reload. |
| T13 | Check `oxide/logs/RealmTreasury/` | A dated ledger file has one tab-separated line per movement above. |

To run the logic tests yourself (Linux x64): `plugins/docs/RealmTreasury/logic-tests/run.sh`.

---

## What is UNVERIFIED

1. **Nothing has run on a live server.** Every game call is proven to exist (compile check) and was read in the decompiled game code, but none was executed.
2. **Tax observation.** It is not proven that a plugin's `EventManager.Subscribe<ItemPassEvent>` subscriber receives the client-sent tax events on the dedicated server. It is also not proven that `EventHandlerOrder.VeryEarly` runs before `TaxCollector.OnItemPass` merges the stack (if it runs later, `StackAmount` may read as 0 or 1 after the merge), or how often the events arrive. The statistic is informational only and never moves goods (smoke T3).
3. **Items:** that server-side `AutoSplit`/`AutoMergeAdd` refresh the client inventory at once (smoke T5–T7), and whether hotbar items count. Only the `Inventory` container is read (smoke T8). This is the same caveat as RealmContracts.
4. **Tax units:** that `GetTax()` is a 0–1 fraction. The decompiled `ResourceTax` and `TaxCollector.FORMAT_TAX` say so (smoke T2).
5. **Newtonsoft round-trip** of the data file. The tests serialize with System.Text.Json. The data shapes (dictionaries, lists, `DateTime`) are the same kind RealmContracts already stores.
6. **Chat command names** `/market`, `/vault`, `/treasury`, `/purse` and `/economy` may collide with a plugin another team adds later. Oxide warns on a duplicate.

### Known limits (by design)

- **Wash trading** between two accounts of the same person can move the price history. The history shows trade counts and volume-weighted averages, but it cannot tell alts apart.
- **Marks are fiat.** A monarch who mints to the caps and buys up the market is playing the game. The Chronicle, the broadcast and `/treasury ledger` make it public. The caps bound it.
- A crash between a measured game call and the save can **lose** escrowed goods. By design, it never duplicates them.
