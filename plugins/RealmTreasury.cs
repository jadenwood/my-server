// RealmTreasury: the Crown's treasury, house vaults and the market of Ostreval.
//
//   treasury  the royal treasury holds real items (in the realm's custody) and "marks", a ledger currency.
//             It is fed by deposits (anyone may pay tribute; the king may bring in what the game's tax chest
//             collected), by house tithes the crown levies, and by the market fee.
//   vaults    each house (RealmHouses) has a vault of items and marks. Any member deposits; the head of the house
//             and up to MaxStewards named stewards withdraw, within a daily outflow budget.
//   market    /market sell|bid|buy|fill|list|history: escrowed asks (items held) and bids (marks held), with expiry,
//             a capped market fee paid to the treasury, and a public price history.
//   mint      the king's minting decree creates MARKS ONLY. Marks are pure accounting; no command in this plugin
//             creates an item. Items only leave the realm's custody as payment of a persisted "owed" ledger that
//             was filled from items previously TAKEN (measured) from players.
//
// The game's tax (read only). KingsScheme.GetTax() is the fraction of each gathered amount the client sets aside
// (ResourceTax.TaxResource: Amount * TaxCollector.Tax, TaxCollector.Tax = KingsScheme.GetTax()) [IL]. The client then
// sends the taxed stack to the server as an ItemPassEvent with Memo "Tax"; the server's TaxCollector.OnItemPass
// (subscribed with EventManager.Subscribe<ItemPassEvent>) merges it into the containers that carry a TaxContainer,
// or a hidden stash [IL; decompiled CodeHatch.Thrones.Tax.TaxCollector]. This plugin subscribes to the same event,
// read only, to OBSERVE what the realm collects. Observed tax is never credited to the treasury: the items are
// already in the game's tax chest, and the event comes from clients (it could be spoofed). The king empties the
// chest in game and pays it in with /treasury taxin, which is an ordinary measured deposit.
// UNVERIFIED (in-game): that a plugin subscriber receives the server-side ItemPassEvent, that EventHandlerOrder
// VeryEarly runs before TaxCollector merges the stack (StackAmount is read before), and the event rate.
//
// Escrow follows RealmContracts (read its header): take = ItemCollection.AutoCount + AutoSplit; give = GetInventory
// + AutoMergeAdd in chunks of ContainerManagement.StackLimit [IL: StationListener.OnStationUpgradeRequest,
// ThronesCommandHandler.Give]. Every change is by an amount MEASURED with AutoCount before/after, never the amount
// requested; state changes and the data file is written before anything is paid; payouts go through the persisted
// owed ledger, reduced only by measured deliveries. A crash between a game call and the save loses escrow, it never
// duplicates it.
//
// Vault lineage: a house vault belongs to the house that filled it (RealmHouses GetHouseFounded). If that house falls
// and a new one is founded under the same name, the old vault is set apart as "<name> (fallen <date>)" for the crown
// to escheat; the new house starts empty (tools/exploit-review/README.md).
//
// Zero-sum proof (checked by /treasury audit, on load, and by the logic tests):
//   for every item:  ItemsIn[item] - ItemsOut[item] == treasury + all vaults + open asks + owed
//   for marks:       MarksMinted == treasury + all vaults + all purses + open bid escrow
// ItemsIn only grows by measured takes from player inventories; ItemsOut only by measured gives to them.
//
// Language level: C# 3, .NET 3.5. Cross-plugin API methods are non-public instance methods (Oxide.CSharp registers
// only NonPublic|Instance methods as hooks).

using System;
using System.Collections.Generic;
using System.Globalization;
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError, GetInventory [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;            // InvItemBlueprint, InvBlueprints [ASM]
using CodeHatch.Inventory.Blueprints.Components; // ContainerManagement [ASM; IL ThronesCommandHandler.Give]
using CodeHatch.ItemContainer;                   // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events;               // EventManager, EventSubscriber<T>, EventHandlerOrder [ASM]
using CodeHatch.Networking.Events.Item;          // ItemPassEvent (Memo, ItemStack) [ASM]
using CodeHatch.Thrones.SocialSystem;            // KingsScheme, KingsRealm [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmTreasury", "Realm", "0.1.0")]
    [Description("Royal treasury, house vaults and an escrowed market with zero-sum item flows")]
    public class RealmTreasury : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmtreasury.admin";
        private const string DataName = "RealmTreasury";
        private const string BackupName = "RealmTreasury_lastgood";
        private const float TickSeconds = 30f;

        private const string KPlayer = "player";
        private const string KTreasury = "treasury";
        private const string KHouse = "house";

        private const string SideSell = "sell";
        private const string SideBid = "bid";

        private const string SOpen = "open";
        private const string SDone = "done";
        private const string SCancelled = "cancelled";
        private const string SExpired = "expired";

        private const string MarksAsset = "#marks";

        private PluginConfig config;
        private StoredData data;
        private bool initialized;
        private bool dirty;
        private EventSubscriber<ItemPassEvent> taxSubscriber;
        private readonly Dictionary<string, DateTime> lastFullWarning = new Dictionary<string, DateTime>();
        private readonly Queue<DateTime> chronicleTimes = new Queue<DateTime>();

        #region Config

        private class PluginConfig
        {
            public string CurrencyName = "marks";
            public string TreasurerSeat = "Keeper of Coin";     // CrownAndConsequences council seat that may act for the treasury
            public bool ObserveGameTax = true;                 // subscribe to the game's tax ItemPassEvent (read only)
            public int TaxObservedMaxPerEvent = 1000;          // clamp per event (client-sent; spoof guard for the statistic)

            public int MaxPerCommand = 1000;                   // units of one item moved by one command
            public int TreasuryMaxUnitsPerItem = 100000;
            public int TreasuryMaxItemTypes = 200;
            public int TreasuryItemsOutPerDay = 1000;          // rolling 24 h: units leaving the treasury (withdraw, award, asks)
            public long TreasuryMarksOutPerDay = 5000;         // rolling 24 h: marks leaving the treasury (grants, bids, buys)
            public int TreasuryActionCooldownSeconds = 10;

            public long MintMaxPerDecree = 1000;
            public long MintMaxPerDay = 2000;
            public long MintSupplyCap = 100000;                // total marks that may ever exist
            public int MintCooldownMinutes = 60;
            public long PluginIncomeMaxPerDay = 3000;          // rolling 24 h per source: marks other plugins strike into house vaults (GrantHouseIncome)

            public int TitheMaxPercent = 10;                   // the Charter's ceiling on the tithe
            public int TitheDefaultPercent = 0;
            public int TitheChangeCooldownHours = 24;
            public int TitheLevyIntervalHours = 24;
            public bool TitheItems = true;
            public bool TitheMarks = true;
            public bool TitheOnlySwornHouses = true;           // only houses sworn to the crown (CrownAndConsequences)
            public int TitheMaxUnitsPerItemPerHouse = 500;
            public long TitheMaxMarksPerHouse = 2000;

            public int VaultMaxUnitsPerItem = 10000;
            public int VaultMaxItemTypes = 40;
            public int VaultItemsOutPerDay = 1000;             // rolling 24 h per house
            public long VaultMarksOutPerDay = 5000;            // rolling 24 h per house
            public int VaultActionCooldownSeconds = 5;
            public int MaxStewards = 3;

            public int MarketFeeDefaultPercent = 2;
            public int MarketFeeMaxPercent = 10;
            public int FeeChangeCooldownHours = 12;
            public int MaxOpenPerOwner = 5;
            public int MaxOpenTotal = 200;
            public int PostCooldownSeconds = 30;
            public int TradeCooldownSeconds = 2;
            public long MaxPricePerUnit = 100000;
            public int DefaultListingHours = 24;
            public int MaxListingHours = 72;
            public int TradeHistoryMax = 500;
            public int ClosedListingsKept = 100;

            public long PayMaxPerDay = 5000;                   // rolling 24 h per player, /purse pay
            public int PayCooldownSeconds = 10;

            // Rewards for deeds (RealmQuests calls RewardMarks): new marks struck straight into a purse, under caps of their own.
            public bool RewardsEnabled = true;
            public long RewardMintPerDay = 3000;               // rolling 24 h, every player together
            public long RewardMaxPerCall = 500;
            public long RewardCrownReserve = 20000;            // rewards stop this far below MintSupplyCap, so the crown can still mint

            public int JournalMax = 1000;                      // in the data file; the full ledger goes to oxide/logs
            public bool LedgerToLogFile = true;
            public int MaxListLines = 12;
            public int ChronicleMaxPerHour = 10;
            public long ChronicleTradeMinMarks = 1000;         // trades worth at least this many marks reach the chronicle
            public List<string> AllowedItems;                  // empty = any item
            public List<string> BlockedItems;
        }

        protected override void LoadDefaultConfig()
        {
            var c = new PluginConfig();
            c.AllowedItems = new List<string>();
            c.BlockedItems = new List<string>();
            Config.WriteObject(c, true);
        }

        private void ClampConfig()
        {
            PluginConfig c = config;
            if (c.AllowedItems == null) c.AllowedItems = new List<string>();
            if (c.BlockedItems == null) c.BlockedItems = new List<string>();
            if (string.IsNullOrEmpty(c.CurrencyName)) c.CurrencyName = "marks";
            if (c.TaxObservedMaxPerEvent < 1) c.TaxObservedMaxPerEvent = 1;
            if (c.MaxPerCommand < 1) c.MaxPerCommand = 1;
            if (c.TreasuryMaxUnitsPerItem < 1) c.TreasuryMaxUnitsPerItem = 1;
            if (c.TreasuryMaxItemTypes < 1) c.TreasuryMaxItemTypes = 1;
            if (c.TreasuryItemsOutPerDay < 0) c.TreasuryItemsOutPerDay = 0;
            if (c.TreasuryMarksOutPerDay < 0) c.TreasuryMarksOutPerDay = 0;
            if (c.TreasuryActionCooldownSeconds < 0) c.TreasuryActionCooldownSeconds = 0;
            if (c.MintMaxPerDecree < 0) c.MintMaxPerDecree = 0;
            if (c.MintMaxPerDay < 0) c.MintMaxPerDay = 0;
            if (c.MintSupplyCap < 0) c.MintSupplyCap = 0;
            if (c.MintSupplyCap > 1000000000L) c.MintSupplyCap = 1000000000L;  // keeps every sum far from overflow
            if (c.MintCooldownMinutes < 0) c.MintCooldownMinutes = 0;
            if (c.PluginIncomeMaxPerDay < 0) c.PluginIncomeMaxPerDay = 0;
            if (c.TitheMaxPercent < 0) c.TitheMaxPercent = 0;
            if (c.TitheMaxPercent > 50) c.TitheMaxPercent = 50;
            if (c.TitheDefaultPercent < 0) c.TitheDefaultPercent = 0;
            if (c.TitheDefaultPercent > c.TitheMaxPercent) c.TitheDefaultPercent = c.TitheMaxPercent;
            if (c.TitheChangeCooldownHours < 0) c.TitheChangeCooldownHours = 0;
            if (c.TitheLevyIntervalHours < 1) c.TitheLevyIntervalHours = 1;
            if (c.TitheMaxUnitsPerItemPerHouse < 0) c.TitheMaxUnitsPerItemPerHouse = 0;
            if (c.TitheMaxMarksPerHouse < 0) c.TitheMaxMarksPerHouse = 0;
            if (c.VaultMaxUnitsPerItem < 1) c.VaultMaxUnitsPerItem = 1;
            if (c.VaultMaxItemTypes < 1) c.VaultMaxItemTypes = 1;
            if (c.VaultItemsOutPerDay < 0) c.VaultItemsOutPerDay = 0;
            if (c.VaultMarksOutPerDay < 0) c.VaultMarksOutPerDay = 0;
            if (c.VaultActionCooldownSeconds < 0) c.VaultActionCooldownSeconds = 0;
            if (c.MaxStewards < 0) c.MaxStewards = 0;
            if (c.MarketFeeMaxPercent < 0) c.MarketFeeMaxPercent = 0;
            if (c.MarketFeeMaxPercent > 50) c.MarketFeeMaxPercent = 50;
            if (c.MarketFeeDefaultPercent < 0) c.MarketFeeDefaultPercent = 0;
            if (c.MarketFeeDefaultPercent > c.MarketFeeMaxPercent) c.MarketFeeDefaultPercent = c.MarketFeeMaxPercent;
            if (c.FeeChangeCooldownHours < 0) c.FeeChangeCooldownHours = 0;
            if (c.MaxOpenPerOwner < 1) c.MaxOpenPerOwner = 1;
            if (c.MaxOpenTotal < 1) c.MaxOpenTotal = 1;
            if (c.PostCooldownSeconds < 0) c.PostCooldownSeconds = 0;
            if (c.TradeCooldownSeconds < 0) c.TradeCooldownSeconds = 0;
            if (c.MaxPricePerUnit < 1) c.MaxPricePerUnit = 1;
            if (c.MaxPricePerUnit > 1000000L) c.MaxPricePerUnit = 1000000L;
            if (c.MaxListingHours < 1) c.MaxListingHours = 1;
            if (c.DefaultListingHours < 1 || c.DefaultListingHours > c.MaxListingHours) c.DefaultListingHours = c.MaxListingHours;
            if (c.TradeHistoryMax < 10) c.TradeHistoryMax = 10;
            if (c.ClosedListingsKept < 0) c.ClosedListingsKept = 0;
            if (c.PayMaxPerDay < 0) c.PayMaxPerDay = 0;
            if (c.PayCooldownSeconds < 0) c.PayCooldownSeconds = 0;
            if (c.JournalMax < 50) c.JournalMax = 50;
            if (c.MaxListLines < 1) c.MaxListLines = 1;
            if (c.ChronicleMaxPerHour < 0) c.ChronicleMaxPerHour = 0;
            if (c.ChronicleTradeMinMarks < 1) c.ChronicleTradeMinMarks = 1;
        }

        #endregion

        #region Data

        private class Vault
        {
            public Dictionary<string, int> Items = new Dictionary<string, int>();
            public long Marks;
            public List<string> Stewards = new List<string>();       // player ids (house vaults only)
            public string Founded;         // house vaults: RealmHouses GetHouseFounded of the house that owns it (lineage)
        }

        private class Listing
        {
            public int Id;
            public string Side;            // sell (ask: items held) | bid (buy order: marks held)
            public string Status;
            public string OwnerKind;       // player | treasury | house
            public string OwnerId;         // player id, "treasury", or house name
            public string OwnerName;
            public string PostedBy;        // player id of whoever posted it (a steward or treasurer may not trade with it)
            public string Item;            // blueprint name
            public int Initial;
            public int Remaining;          // sell: units held in escrow; bid: units still wanted
            public long Price;             // marks per unit
            public long EscrowMarks;       // bid: marks held
            public DateTime PostedAt;
            public DateTime ExpiresAt;
            public string Outcome;
        }

        private class Owed
        {
            public string PlayerId;
            public string PlayerName;
            public string Item;
            public int Amount;
            public string Ref;
        }

        private class Trade
        {
            public DateTime At;
            public string Item;
            public int Qty;
            public long Price;             // per unit
            public string Seller;
            public string Buyer;
            public int ListingId;
        }

        private class PriceStat
        {
            public long Last;
            public DateTime LastAt;
            public long Trades;
            public long Units;
            public long Marks;
            public long Low;
            public long High;
        }

        private class Entry
        {
            public long Seq;
            public DateTime At;
            public string Kind;
            public string Actor;
            public string Asset;           // item name, or #marks
            public long Amount;
            public string From;
            public string To;
            public string Note;
        }

        private class Spend
        {
            public DateTime At;
            public string Key;
            public long Amount;
        }

        private class StoredData
        {
            public int Version = 1;
            public long Seq;
            public int NextListingId = 1;
            public Vault Treasury = new Vault();
            public Dictionary<string, Vault> Houses = new Dictionary<string, Vault>();
            public Dictionary<string, long> Purses = new Dictionary<string, long>();
            public Dictionary<string, string> Names = new Dictionary<string, string>();      // player id -> last name
            public List<Listing> Listings = new List<Listing>();
            public List<Owed> Owed = new List<Owed>();
            public List<Trade> Trades = new List<Trade>();
            public Dictionary<string, PriceStat> Prices = new Dictionary<string, PriceStat>();
            public List<Entry> Journal = new List<Entry>();
            public Dictionary<string, long> ItemsIn = new Dictionary<string, long>();
            public Dictionary<string, long> ItemsOut = new Dictionary<string, long>();
            public long MarksMinted;
            public Dictionary<string, long> TaxObserved = new Dictionary<string, long>();
            public Dictionary<string, long> TaxDeposited = new Dictionary<string, long>();
            public long TaxEvents;
            public DateTime TaxSince = DateTime.MinValue;
            public DateTime LastTaxAt = DateTime.MinValue;
            public List<Spend> Spends = new List<Spend>();
            public Dictionary<string, DateTime> Cooldowns = new Dictionary<string, DateTime>();
            public int TithePercent = -1;                  // -1 = not set yet (config default applies)
            public int MarketFeePercent = -1;
            public DateTime LastTitheSet = DateTime.MinValue;
            public DateTime LastFeeSet = DateTime.MinValue;
            public DateTime LastLevy = DateTime.MinValue;
            public DateTime LastMint = DateTime.MinValue;
            public bool MarketFrozen;
        }

        private void SaveData()
        {
            if (data == null) return;                         // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
        }

        // Rebuilds the item dictionaries case-insensitively (JSON loses the comparer) and merges duplicates.
        private static Dictionary<string, int> CleanItems(Dictionary<string, int> src)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return d;
            foreach (KeyValuePair<string, int> kv in src)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value <= 0) continue;
                int have;
                d.TryGetValue(kv.Key, out have);
                d[kv.Key] = have + kv.Value;
            }
            return d;
        }

        private static Dictionary<string, long> CleanLongs(Dictionary<string, long> src, bool dropZero)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (src == null) return d;
            foreach (KeyValuePair<string, long> kv in src)
            {
                if (string.IsNullOrEmpty(kv.Key) || (dropZero && kv.Value == 0)) continue;
                long have;
                d.TryGetValue(kv.Key, out have);
                d[kv.Key] = have + kv.Value;
            }
            return d;
        }

        private static Vault CleanVault(Vault v)
        {
            if (v == null) v = new Vault();
            v.Items = CleanItems(v.Items);
            if (v.Stewards == null) v.Stewards = new List<string>();
            if (v.Marks < 0) v.Marks = 0;
            return v;
        }

        private void Normalize()
        {
            data.Treasury = CleanVault(data.Treasury);
            var houses = new Dictionary<string, Vault>(StringComparer.OrdinalIgnoreCase);
            if (data.Houses != null)
                foreach (KeyValuePair<string, Vault> kv in data.Houses)
                    if (!string.IsNullOrEmpty(kv.Key) && !houses.ContainsKey(kv.Key)) houses[kv.Key] = CleanVault(kv.Value);
            data.Houses = houses;
            data.Purses = CleanLongs(data.Purses, true);
            if (data.Names == null) data.Names = new Dictionary<string, string>();
            if (data.Listings == null) data.Listings = new List<Listing>();
            data.Listings.RemoveAll(delegate(Listing l) { return l == null || l.Side == null || l.Status == null || l.Item == null || l.OwnerKind == null; });
            if (data.Owed == null) data.Owed = new List<Owed>();
            data.Owed.RemoveAll(delegate(Owed o) { return o == null || o.PlayerId == null || o.Item == null || o.Amount <= 0; });
            if (data.Trades == null) data.Trades = new List<Trade>();
            data.Trades.RemoveAll(delegate(Trade t) { return t == null || t.Item == null; });
            if (data.Prices == null) data.Prices = new Dictionary<string, PriceStat>();
            var prices = new Dictionary<string, PriceStat>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PriceStat> kv in data.Prices) if (kv.Value != null && !prices.ContainsKey(kv.Key)) prices[kv.Key] = kv.Value;
            data.Prices = prices;
            if (data.Journal == null) data.Journal = new List<Entry>();
            data.ItemsIn = CleanLongs(data.ItemsIn, false);
            data.ItemsOut = CleanLongs(data.ItemsOut, false);
            data.TaxObserved = CleanLongs(data.TaxObserved, true);
            data.TaxDeposited = CleanLongs(data.TaxDeposited, true);
            if (data.Spends == null) data.Spends = new List<Spend>();
            data.Spends.RemoveAll(delegate(Spend s) { return s == null || s.Key == null; });
            if (data.Cooldowns == null) data.Cooldowns = new Dictionary<string, DateTime>();
            foreach (Listing l in data.Listings) if (l.Id >= data.NextListingId) data.NextListingId = l.Id + 1;
            // One owed entry per (player, item).
            var merged = new List<Owed>();
            foreach (Owed o in data.Owed)
            {
                Owed same = null;
                foreach (Owed m in merged)
                    if (m.PlayerId == o.PlayerId && string.Equals(m.Item, o.Item, StringComparison.OrdinalIgnoreCase)) { same = m; break; }
                if (same == null) merged.Add(o); else same.Amount += o.Amount;
            }
            data.Owed = merged;
            if (data.TithePercent < 0) data.TithePercent = config.TitheDefaultPercent;
            if (data.TithePercent > config.TitheMaxPercent) data.TithePercent = config.TitheMaxPercent;
            if (data.MarketFeePercent < 0) data.MarketFeePercent = config.MarketFeeDefaultPercent;
            if (data.MarketFeePercent > config.MarketFeeMaxPercent) data.MarketFeePercent = config.MarketFeeMaxPercent;
        }

        #endregion

        #region Chat style

        // Realm chat style, the same block in every Realm plugin (docs/realm-commands.md, "Chat style";
        // tools/realm-integration/check.mjs checks it). A reply opens with its speaker in the colour of its tone:
        // gold for news and answers, green for done, amber for take care, red for refused. A line that starts with
        // a space continues a list and carries no speaker. A text that already opens with a colour tag or with
        // "<speaker>:" (a server's older lang file, or a line with a voice of its own) is sent as it is.
        private const string ChatGold = "D6A043";
        private const string ChatOk = "8FC97A";
        private const string ChatWarn = "E8913A";
        private const string ChatError = "E86A5C";

        private static string Styled(string speaker, string tone, string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(speaker) || text[0] == ' ') return text;
            if (text.StartsWith(speaker + ":", StringComparison.OrdinalIgnoreCase)) return text;
            if (text.Length >= 8 && text[0] == '[' && text[7] == ']' && IsChatHex(text.Substring(1, 6))) return text;
            return "[" + tone + "]" + speaker + "[FFFFFF]: " + text;
        }

        private static bool IsChatHex(string s)
        {
            foreach (char c in s) if ("0123456789ABCDEFabcdef".IndexOf(c) < 0) return false;
            return true;
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Treasury" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Help0", "The coffers of Ostreval. Currency: {0}. Market fee {1}%, house tithe {2}%, game tax {3}." },
                { "HelpMarket1", "  [F4C96D]/market list[FFFFFF] [item|mine] | [F4C96D]/market history[FFFFFF] <item> | [F4C96D]/market items[FFFFFF] <search> | [F4C96D]/market collect[FFFFFF]" },
                { "HelpMarket2", "  [F4C96D]/market sell[FFFFFF] <qty> <price each> <item> [24h] | [F4C96D]/market bid[FFFFFF] <qty> <price each> <item> [24h]" },
                { "HelpMarket3", "  [F4C96D]/market buy[FFFFFF] <id> [qty] | [F4C96D]/market fill[FFFFFF] <id> [qty] | [F4C96D]/market cancel[FFFFFF] <id> | [F4C96D]/purse[FFFFFF] | [F4C96D]/purse pay[FFFFFF] <player> <n>" },
                { "HelpVault1", "  [F4C96D]/vault[FFFFFF] [house] | [F4C96D]/vault deposit[FFFFFF] <qty> <item> | [F4C96D]/vault withdraw[FFFFFF] <qty> <item> | [F4C96D]/vault give[FFFFFF] <n> | [F4C96D]/vault take[FFFFFF] <n>" },
                { "HelpVault2", "  [F4C96D]/vault sell|bid[FFFFFF] <qty> <price> <item> | [F4C96D]/vault buy|fill[FFFFFF] <id> [qty] | [F4C96D]/vault cancel[FFFFFF] <id> | [F4C96D]/vault steward add|remove[FFFFFF] <player>" },
                { "HelpTreasury1", "  [F4C96D]/treasury[FFFFFF] | [F4C96D]/treasury deposit[FFFFFF] <qty> <item> | [F4C96D]/treasury taxin[FFFFFF] <qty> <item> | [F4C96D]/treasury ledger[FFFFFF] [n] | [F4C96D]/treasury tax[FFFFFF]" },
                { "HelpTreasury2", "  Crown & Keeper of Coin: [F4C96D]/treasury withdraw[FFFFFF] <qty> <item> | award <player> <qty> <item> | grant <player> <n> | sell|bid|buy|fill|cancel" },
                { "HelpTreasury3", "  Crown: [F4C96D]/treasury mint[FFFFFF] <n> | [F4C96D]/treasury tithe[FFFFFF] <percent> | [F4C96D]/treasury levy[FFFFFF] | [F4C96D]/treasury fee[FFFFFF] <percent> | [F4C96D]/treasury escheat[FFFFFF] <house>" },
                { "HelpAdmin", "  Admin: [F4C96D]/treasury audit[FFFFFF] | [F4C96D]/treasury freeze[FFFFFF] | [F4C96D]/treasury unfreeze[FFFFFF] | [F4C96D]/treasury cancel[FFFFFF] <id> (any listing) | [F4C96D]/treasury restore[FFFFFF] <house> (fallen <date>)" },
                { "Restored", "The sealed vault '{0}' is returned to House {1}." },
                { "NotRestorable", "'{0}' is not a sealed vault of a fallen house, or no house of that name stands now." },
                { "NoPermission", "You may not do that." },
                { "NotCrown", "Only the reigning monarch may do that." },
                { "NotTreasurer", "Only the monarch or the {0} may do that." },
                { "NoKing", "The throne is vacant." },
                { "NeedHouse", "You must belong to a house." },
                { "NoHousePlugin", "House vaults need the RealmHouses plugin." },
                { "NotSteward", "Only the head of House {0} or its stewards may do that." },
                { "NotLeader", "Only the head of House {0} may do that." },
                { "BadNumber", "'{0}' is not a whole number from {1} to {2}." },
                { "UnknownItem", "No item is named '{0}'. Try [F4C96D]/market items[FFFFFF] <search>." },
                { "ItemNotAllowed", "'{0}' may not be traded or stored on this server." },
                { "ItemsFound", "Items matching '{0}': {1}" },
                { "ItemsNone", "No item matches '{0}'." },
                { "NotEnoughItems", "You need {0} {1} in your inventory (you have {2})." },
                { "TakeFailed", "The realm could not take the items. Nothing changed; anything taken is returned ([F4C96D]/market collect[FFFFFF])." },
                { "NotEnoughMarks", "{0} has {1} {2}; {3} are needed." },
                { "NotEnoughStock", "{0} holds only {1} {2}." },
                { "Cooldown", "Wait {0} s." },
                { "Budget", "{0} may move only {1} more {2} today (rolling 24 h)." },
                { "Full", "{0} cannot hold that much {1} (limit {2} units, {3} kinds of goods)." },
                { "PlayerNotFound", "No such person is online." },
                { "NoSelf", "You cannot do that with yourself." },
                { "Frozen", "The market is closed by the realm's stewards." },
                { "TooManyMine", "{0} already has {1} open orders." },
                { "TooManyTotal", "The market board is full. Try again later." },
                { "NotFound", "There is no open order #{0}." },
                { "WrongSide", "Order #{0} is a {1} order; use [F4C96D]/market[FFFFFF] {2}." },
                { "OwnOrder", "You cannot trade with your own order." },
                { "NotOwner", "That order is not yours to cancel." },
                { "Posted", "Order #{0} posted: {1}." },
                { "Bought", "Bought {0} {1} for {2} {3} (order #{4})." },
                { "Filled", "Sold {0} {1} into order #{2}; {3} {4} paid (fee {5})." },
                { "OwnerNotice", "Order #{0}: {1} {2} at {3} each with {4}." },
                { "Cancelled", "Order #{0} withdrawn; the escrow returns to {1}." },
                { "Line", "  #{0} {1} {2} {3} @ {4} - {5} ({6})" },
                { "ListNone", "No open orders." },
                { "ListMore", "  ... and {0} more. Filter with [F4C96D]/market list[FFFFFF] <item>." },
                { "HistoryNone", "No trades of {0} are recorded." },
                { "History", "{0}: last {1} ({2} ago); 24h avg {3} on {4} units; 7d avg {5}; low {6} high {7}; {8} trades in all." },
                { "HistoryLine", "  {0} {1} x{2} @ {3}  {4} -> {5}" },
                { "Purse", "Your purse holds {0} {1}." },
                { "Paid", "You paid {0} {1} to {2}." },
                { "PaidYou", "{0} paid you {1} {2}." },
                { "Received", "You receive {0} {1}." },
                { "StillOwed", "Your packs are full. {0} {1} wait for you: [F4C96D]/market collect[FFFFFF]" },
                { "NothingOwed", "Nothing is owed to you." },
                { "Deposited", "{0} {1} placed in {2}." },
                { "DepositedTithe", "{0} {1} placed in {2}." },
                { "Withdrawn", "{0} {1} released from {2}." },
                { "MarksMoved", "{0} {1} moved from {2} to {3}." },
                { "VaultHead", "{0}: {1} {2}; goods: {3}" },
                { "Empty", "nothing" },
                { "Stewards", "Stewards of House {0}: {1}" },
                { "StewardAdded", "{0} is now a steward of House {1}'s vault." },
                { "StewardRemoved", "{0} is no longer a steward of House {1}'s vault." },
                { "StewardsFull", "A house may name at most {0} stewards." },
                { "NotMember", "{0} is not of House {1}." },
                { "TreasuryHead", "The Crown's treasury: {0} {1}; goods: {2}" },
                { "TreasuryRates", "Market fee {0}%, house tithe {1}% (Charter ceiling {2}%), {3} in existence (cap {4}), game tax {5} (max {6})." },
                { "TaxReport", "Game tax {0} of each gathered amount. Observed reaching the tax chest since {1}: {2} ({3} receipts). Paid into the treasury: {4}." },
                { "TaxNone", "none" },
                { "Minted", "The crown strikes {0} {1}. In existence: {2}." },
                { "MintCap", "The realm may hold at most {0} {1}; {2} exist." },
                { "MintTooMuch", "One decree may strike at most {0}; today {1} more." },
                { "MintCooldown", "The mint may strike again in {0} min." },
                { "RateSet", "{0} is now {1}%." },
                { "RateCooldown", "{0} may change again in {1} min." },
                { "RateCap", "{0} may be at most {1}%." },
                { "Levied", "The tithe of {0}% is gathered from {1} houses: {2}." },
                { "LevyNone", "No house owed a tithe." },
                { "LevyCooldown", "The next tithe may be gathered in {0} min." },
                { "LevyZero", "The tithe is 0%. Set it with [F4C96D]/treasury tithe[FFFFFF] <percent>." },
                { "LevyNeedCrownPlugin", "Only sworn houses pay; that needs the CrownAndConsequences plugin." },
                { "Granted", "{0} {1} granted from the treasury to {2}." },
                { "Awarded", "{0} {1} awarded from the treasury to {2}." },
                { "GrantedYou", "The crown grants you {0} {1}." },
                { "Escheated", "The vault of House {0} passes to the crown." },
                { "NotEscheat", "House {0} still stands, or has no vault." },
                { "LedgerLine", "  #{0} {1} {2} {3} {4}: {5} -> {6} ({7})" },
                { "AuditOk", "Audit: every item and every {0} is accounted for ({1} item kinds checked)." },
                { "AuditBad", "Audit: {0} mismatches. See the server log." },
                { "FrozenSet", "The market is now {0}." },
                { "AdminDone", "Done: {0}." }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        private string Fmt(string key, Player player, object[] args)
        {
            return args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
        }

        private void Reply(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendMessage(Styled(Msg("Speaker", player), ToneOf(key), Fmt(key, player, args)));   // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Restored", "Posted", "Bought", "Filled", "Cancelled", "Paid", "Received", "Deposited", "DepositedTithe", "Withdrawn",
            "MarksMoved", "StewardAdded", "StewardRemoved", "RateSet", "Levied", "Granted", "Awarded", "Escheated", "AuditOk",
            "FrozenSet", "AdminDone", "Minted", "GrantedYou", "PaidYou"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : ChatGold;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            if (config == null) config = new PluginConfig();
            ClampConfig();
            Config.WriteObject(config, true);                // writes newly added keys and clamped values
            permission.RegisterPermission(PermAdmin, this);

            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            StoredData loaded;
            try
            {
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                // Refuse to run on a damaged file rather than overwrite the realm's holdings.
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". Nothing was written. Fix the file or restore oxide/data/" + BackupName + ".json, then reload.");
                data = null;
                throw;
            }
            if (loaded == null && existed)
            {
                PrintError("oxide/data/" + DataName + ".json exists but holds no data. Nothing was written. Restore "
                    + BackupName + ".json or delete the file to start empty, then reload.");
                data = null;
                throw new InvalidOperationException("RealmTreasury data file is empty");
            }
            data = loaded ?? new StoredData();
            Normalize();
            List<string> problems = Audit();
            if (problems.Count > 0)
            {
                foreach (string p in problems) PrintWarning("Audit on load: " + p);
                PrintWarning("Audit on load found " + problems.Count + " mismatches; the holdings were NOT changed.");
            }
            else
            {
                Interface.Oxide.DataFileSystem.WriteObject(BackupName, data);   // last known good copy
            }
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                         // re-sent on hot load; keep idempotent
            initialized = true;
            if (config.ObserveGameTax)
            {
                try
                {
                    taxSubscriber = new EventSubscriber<ItemPassEvent>(OnGameItemPass);
                    EventManager.Subscribe<ItemPassEvent>(taxSubscriber, EventHandlerOrder.VeryEarly);
                }
                catch (Exception ex)
                {
                    taxSubscriber = null;
                    PrintWarning("Could not observe the game's tax: " + ex.Message);
                }
            }
            timer.Every(TickSeconds, Tick);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            if (taxSubscriber != null)
            {
                try { EventManager.Unsubscribe<ItemPassEvent>(taxSubscriber); }
                catch (Exception ex) { PrintWarning("Unsubscribe failed: " + ex.Message); }
                taxSubscriber = null;
            }
            SaveData();
        }

        // Read only. See the header: the game's TaxCollector does the real work; this only counts it.
        private void OnGameItemPass(ItemPassEvent e)
        {
            try
            {
                if (data == null || e == null || e.Memo != "Tax") return;
                InvGameItemStack stack = e.ItemStack;
                if (stack == null || stack.Blueprint == null) return;
                int n = stack.StackAmount;
                if (n <= 0) return;
                if (n > config.TaxObservedMaxPerEvent) n = config.TaxObservedMaxPerEvent;
                string item = stack.Blueprint.Name;
                long have;
                data.TaxObserved.TryGetValue(item, out have);
                data.TaxObserved[item] = have + n;
                data.TaxEvents++;
                DateTime now = DateTime.UtcNow;
                if (data.TaxSince == DateTime.MinValue) data.TaxSince = now;
                data.LastTaxAt = now;
                dirty = true;                                // statistics only: saved on the next tick
            }
            catch (Exception ex)
            {
                PrintWarning("Tax observation failed: " + ex.Message);
            }
        }

        private void Tick()
        {
            if (data == null) return;
            DateTime now = DateTime.UtcNow;
            foreach (string house in new List<string>(data.Houses.Keys)) CheckLineage(house);
            foreach (Listing l in data.Listings.ToArray())
                if (l.Status == SOpen && now >= l.ExpiresAt) CloseListing(l, SExpired, "No one took it up in time.", null);
            int before = data.Spends.Count;
            data.Spends.RemoveAll(delegate(Spend s) { return (now - s.At).TotalHours >= 24; });
            if (data.Spends.Count != before) dirty = true;
            foreach (string k in new List<string>(data.Cooldowns.Keys))
                if (data.Cooldowns[k] <= now) { data.Cooldowns.Remove(k); dirty = true; }
            PruneClosedListings();
            if (dirty) SaveData();
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && HasOwed(p.Id.ToString())) PayOwed(p);
        }

        private void PruneClosedListings()
        {
            int closed = 0;
            foreach (Listing l in data.Listings) if (l.Status != SOpen) closed++;
            while (closed > config.ClosedListingsKept)
            {
                int i = data.Listings.FindIndex(delegate(Listing l) { return l.Status != SOpen; });
                if (i < 0) break;
                data.Listings.RemoveAt(i);
                closed--;
                dirty = true;
            }
        }

        #endregion

        #region Parties, marks and goods (the only places holdings change)

        private class Party
        {
            public string Kind;
            public string Id;
            public string Name;
        }

        private static Party PlayerParty(Player p)
        {
            return new Party { Kind = KPlayer, Id = p.Id.ToString(), Name = p.Name };
        }

        private static Party PlayerParty(string id, string name)
        {
            return new Party { Kind = KPlayer, Id = id, Name = name ?? id };
        }

        private static Party TreasuryParty()
        {
            return new Party { Kind = KTreasury, Id = "treasury", Name = "the Crown's treasury" };
        }

        private static Party HouseParty(string house)
        {
            return new Party { Kind = KHouse, Id = house, Name = "House " + house };
        }

        private static Party OwnerOf(Listing l)
        {
            return new Party { Kind = l.OwnerKind, Id = l.OwnerId, Name = l.OwnerName };
        }

        private static bool SameParty(Party a, Party b)
        {
            return a.Kind == b.Kind && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        }

        private static string Label(Party p)
        {
            if (p.Kind == KPlayer) return "player:" + p.Name + "(" + p.Id + ")";
            if (p.Kind == KHouse) return "house:" + p.Id;
            return "treasury";
        }

        private Vault VaultOf(Party p, bool create)
        {
            if (p.Kind == KTreasury) return data.Treasury;
            if (p.Kind != KHouse) return null;
            Vault v;
            if (data.Houses.TryGetValue(p.Id, out v)) return v;
            if (!create) return null;
            v = new Vault();
            v.Items = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            v.Founded = HouseFounded(p.Id);
            data.Houses[p.Id] = v;
            return v;
        }

        private long MarksOf(Party p)
        {
            if (p.Kind == KPlayer)
            {
                long m;
                return data.Purses.TryGetValue(p.Id, out m) ? m : 0;
            }
            Vault v = VaultOf(p, false);
            return v != null ? v.Marks : 0;
        }

        private bool DebitMarks(Party p, long n)
        {
            if (n <= 0 || MarksOf(p) < n) return false;
            if (p.Kind == KPlayer)
            {
                long left = data.Purses[p.Id] - n;
                if (left == 0) data.Purses.Remove(p.Id); else data.Purses[p.Id] = left;
            }
            else VaultOf(p, false).Marks -= n;
            return true;
        }

        private void CreditMarks(Party p, long n)
        {
            if (n <= 0) return;
            if (p.Kind == KPlayer)
            {
                data.Purses[p.Id] = MarksOf(p) + n;
                if (p.Name != null) data.Names[p.Id] = p.Name;
            }
            else VaultOf(p, true).Marks += n;
        }

        private static int Held(Vault v, string item)
        {
            int n;
            return v != null && v.Items.TryGetValue(item, out n) ? n : 0;
        }

        private static void AddToVault(Vault v, string item, int n)
        {
            if (n <= 0) return;
            v.Items[item] = Held(v, item) + n;
        }

        private static bool RemoveFromVault(Vault v, string item, int n)
        {
            int have = Held(v, item);
            if (n <= 0 || have < n) return false;
            if (have == n) v.Items.Remove(item); else v.Items[item] = have - n;
            return true;
        }

        private bool VaultCanHold(Party p, string item, int n)
        {
            Vault v = VaultOf(p, false);
            int maxUnits = p.Kind == KTreasury ? config.TreasuryMaxUnitsPerItem : config.VaultMaxUnitsPerItem;
            int maxKinds = p.Kind == KTreasury ? config.TreasuryMaxItemTypes : config.VaultMaxItemTypes;
            int have = Held(v, item);
            if ((long)have + n > maxUnits) return false;
            if (have == 0 && v != null && v.Items.Count >= maxKinds) return false;
            return true;
        }

        private void ReplyFull(Player player, Party p, string item)
        {
            bool t = p.Kind == KTreasury;
            ReplyError(player, "Full", p.Name, item, t ? config.TreasuryMaxUnitsPerItem : config.VaultMaxUnitsPerItem,
                t ? config.TreasuryMaxItemTypes : config.VaultMaxItemTypes);
        }

        // Moves goods out of a party into the realm's custody. Player: measured from the inventory (exactly n or 0;
        // a partial take is returned through the owed ledger). Vault: decremented. Returns the units taken.
        private int TakeGoods(Party from, Player actor, InvItemBlueprint bp, int n, string kind, string note)
        {
            if (from.Kind == KPlayer)
            {
                int taken = TakeItems(actor, bp, n);
                if (taken <= 0) return 0;
                AddFlow(data.ItemsIn, bp.Name, taken);
                Journal(kind, actor.Name, bp.Name, taken, Label(from), "custody", note);
                if (taken < n)
                {
                    AddOwed(from.Id, from.Name, bp.Name, taken, "returned: " + note);
                    Journal("return", actor.Name, bp.Name, taken, "custody", Label(from), "partial take returned");
                    SaveData();
                    PayOwed(actor);
                    return 0;
                }
                return taken;
            }
            Vault v = VaultOf(from, false);
            if (!RemoveFromVault(v, bp.Name, n)) return 0;
            Journal(kind, actor != null ? actor.Name : "realm", bp.Name, n, Label(from), "custody", note);
            return n;
        }

        // Moves goods from the realm's custody to a party. Player: into the owed ledger (paid by measured gives).
        // Vault: added (capacity is checked by the caller BEFORE anything moves; returns of escrow may exceed it).
        private void DeliverGoods(Party to, string item, int n, string kind, string actor, string note)
        {
            if (n <= 0) return;
            if (to.Kind == KPlayer) AddOwed(to.Id, to.Name, item, n, note);
            else AddToVault(VaultOf(to, true), item, n);
            Journal(kind, actor, item, n, "custody", Label(to), note);
        }

        private static void AddFlow(Dictionary<string, long> flow, string item, long n)
        {
            long have;
            flow.TryGetValue(item, out have);
            flow[item] = have + n;
        }

        #endregion

        #region Budgets and cooldowns

        private long SpentToday(string key)
        {
            DateTime now = DateTime.UtcNow;
            long sum = 0;
            foreach (Spend s in data.Spends)
                if (s.Key == key && (now - s.At).TotalHours < 24) sum += s.Amount;
            return sum;
        }

        // True and recorded if `amount` fits in the rolling-24h budget `limit` under `key`.
        private bool SpendBudget(Player player, string key, long limit, long amount, string who, string what)
        {
            long left = limit - SpentToday(key);
            if (amount > left)
            {
                ReplyError(player, "Budget", who, Math.Max(0, left), what);
                return false;
            }
            data.Spends.Add(new Spend { At = DateTime.UtcNow, Key = key, Amount = amount });
            return true;
        }

        private bool OnCooldown(Player player, string key)
        {
            DateTime until;
            if (IsAdmin(player) || !data.Cooldowns.TryGetValue(key, out until)) return false;
            double left = (until - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) return false;
            ReplyError(player, "Cooldown", (int)Math.Ceiling(left));
            return true;
        }

        private void SetCooldown(string key, int seconds)
        {
            if (seconds > 0) data.Cooldowns[key] = DateTime.UtcNow.AddSeconds(seconds);
        }

        private string ItemsBudgetKey(Party p) { return p.Kind == KTreasury ? "treasury.items" : "house:" + p.Id.ToLowerInvariant() + ".items"; }
        private string MarksBudgetKey(Party p) { return p.Kind == KTreasury ? "treasury.marks" : "house:" + p.Id.ToLowerInvariant() + ".marks"; }
        private long ItemsBudget(Party p) { return p.Kind == KTreasury ? config.TreasuryItemsOutPerDay : config.VaultItemsOutPerDay; }
        private long MarksBudget(Party p) { return p.Kind == KTreasury ? config.TreasuryMarksOutPerDay : config.VaultMarksOutPerDay; }

        // Vault outflows are budgeted; a player's own items and marks are not.
        private bool SpendItems(Player player, Party p, long n)
        {
            if (p.Kind == KPlayer) return true;
            return SpendBudget(player, ItemsBudgetKey(p), ItemsBudget(p), n, p.Name, "goods");
        }

        private bool SpendMarks(Player player, Party p, long n)
        {
            if (p.Kind == KPlayer) return true;
            return SpendBudget(player, MarksBudgetKey(p), MarksBudget(p), n, p.Name, config.CurrencyName);
        }

        #endregion

        #region Chat: help and purse

        [ChatCommand("economy")]
        private void CmdEconomy(Player player, string command, string[] args)
        {
            ShowHelp(player, true, true, true);
        }

        private void ShowHelp(Player player, bool market, bool vault, bool treasury)
        {
            Reply(player, "Help0", config.CurrencyName, data.MarketFeePercent, data.TithePercent, TaxText());
            if (market) for (int i = 1; i <= 3; i++) player.SendMessage(Msg("HelpMarket" + i, player));
            if (vault) for (int i = 1; i <= 2; i++) player.SendMessage(Msg("HelpVault" + i, player));
            if (treasury)
            {
                for (int i = 1; i <= 3; i++) player.SendMessage(Msg("HelpTreasury" + i, player));
                if (IsAdmin(player)) player.SendMessage(Msg("HelpAdmin", player));
            }
        }

        [ChatCommand("purse")]
        private void CmdPurse(Player player, string command, string[] args)
        {
            if (args.Length == 0 || args[0].ToLowerInvariant() != "pay")
            {
                Reply(player, "Purse", MarksOf(PlayerParty(player)), config.CurrencyName);
                return;
            }
            if (args.Length < 3) { player.SendMessage(Msg("HelpMarket3", player)); return; }
            long n;
            if (!ParseLong(player, args[args.Length - 1], config.PayMaxPerDay, out n)) return;
            Player target = FindOnline(JoinRange(args, 1, args.Length - 2));
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            if (target.Id == player.Id) { ReplyError(player, "NoSelf"); return; }
            string id = player.Id.ToString();
            if (OnCooldown(player, "pay:" + id)) return;
            Party from = PlayerParty(player);
            if (MarksOf(from) < n) { ReplyError(player, "NotEnoughMarks", "Your purse", MarksOf(from), config.CurrencyName, n); return; }
            if (!SpendBudget(player, "pay:" + id, config.PayMaxPerDay, n, "You", config.CurrencyName)) return;
            Party to = PlayerParty(target);
            DebitMarks(from, n);
            CreditMarks(to, n);
            Journal("pay", player.Name, MarksAsset, n, Label(from), Label(to), null);
            SetCooldown("pay:" + id, config.PayCooldownSeconds);
            SaveData();
            Reply(player, "Paid", n, config.CurrencyName, target.Name);
            Reply(target, "PaidYou", player.Name, n, config.CurrencyName);
        }

        #endregion

        #region Chat: market

        [ChatCommand("market")]
        private void CmdMarket(Player player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            Party me = PlayerParty(player);
            switch (sub)
            {
                case "list": CmdList(player, JoinFrom(args, 1)); return;
                case "history": CmdHistory(player, JoinFrom(args, 1)); return;
                case "items": CmdItems(player, JoinFrom(args, 1)); return;
                case "collect": CmdCollect(player); return;
                case "sell": PostOrder(player, me, SideSell, args); return;
                case "bid": PostOrder(player, me, SideBid, args); return;
                case "buy": TradeOrder(player, me, SideSell, args); return;
                case "fill": TradeOrder(player, me, SideBid, args); return;
                case "cancel": CancelOrder(player, me, args); return;
            }
            ShowHelp(player, true, false, false);
        }

        private void CmdList(Player player, string filter)
        {
            string id = player.Id.ToString();
            bool mine = filter.ToLowerInvariant() == "mine";
            InvItemBlueprint bp = null;
            if (filter.Length > 0 && !mine)
            {
                bp = FindItem(filter);
                if (bp == null) { ReplyError(player, "UnknownItem", filter); return; }
            }
            var open = new List<Listing>();
            foreach (Listing l in data.Listings)
            {
                if (l.Status != SOpen) continue;
                if (mine && !(l.OwnerKind == KPlayer && l.OwnerId == id)) continue;
                if (bp != null && !string.Equals(l.Item, bp.Name, StringComparison.OrdinalIgnoreCase)) continue;
                open.Add(l);
            }
            // Asks cheapest first, then bids dearest first.
            open.Sort(delegate(Listing a, Listing b)
            {
                if (a.Side != b.Side) return a.Side == SideSell ? -1 : 1;
                int byItem = string.Compare(a.Item, b.Item, StringComparison.OrdinalIgnoreCase);
                if (byItem != 0) return byItem;
                return a.Side == SideSell ? a.Price.CompareTo(b.Price) : b.Price.CompareTo(a.Price);
            });
            int shown = 0;
            foreach (Listing l in open)
            {
                if (shown >= config.MaxListLines) break;
                shown++;
                Reply(player, "Line", l.Id, l.Side == SideSell ? "SELL" : "BUY", l.Remaining, l.Item, l.Price, l.OwnerName,
                    MinutesUntil(l.ExpiresAt) + " min");
            }
            if (shown == 0) Reply(player, "ListNone");
            else if (open.Count > shown) Reply(player, "ListMore", open.Count - shown);
        }

        private void CmdHistory(Player player, string itemText)
        {
            InvItemBlueprint bp = FindItem(itemText);
            if (bp == null) { ReplyError(player, "UnknownItem", itemText); return; }
            PriceStat st;
            if (!data.Prices.TryGetValue(bp.Name, out st) || st.Trades == 0) { Reply(player, "HistoryNone", bp.Name); return; }
            DateTime now = DateTime.UtcNow;
            long u1 = 0, m1 = 0, u7 = 0, m7 = 0;
            var recent = new List<Trade>();
            foreach (Trade t in data.Trades)
            {
                if (!string.Equals(t.Item, bp.Name, StringComparison.OrdinalIgnoreCase)) continue;
                double h = (now - t.At).TotalHours;
                if (h < 24) { u1 += t.Qty; m1 += t.Qty * t.Price; }
                if (h < 168) { u7 += t.Qty; m7 += t.Qty * t.Price; }
                recent.Add(t);
            }
            Reply(player, "History", bp.Name, st.Last, Ago(st.LastAt), u1 > 0 ? (m1 / u1).ToString() : "-", u1,
                u7 > 0 ? (m7 / u7).ToString() : "-", st.Low, st.High, st.Trades);
            for (int i = Math.Max(0, recent.Count - 5); i < recent.Count; i++)
            {
                Trade t = recent[i];
                player.SendMessage(Fmt("HistoryLine", player, new object[] { t.At.ToString("MM-dd HH:mm"), t.Item, t.Qty, t.Price, t.Seller, t.Buyer }));
            }
        }

        private void CmdItems(Player player, string search)
        {
            if (search.Length == 0 || InvBlueprints.Instance == null) { player.SendMessage(Msg("HelpMarket1", player)); return; }
            InvItemBlueprint[] found = InvBlueprints.GetBlueprintsContaining(search);
            if (found == null || found.Length == 0) { Reply(player, "ItemsNone", search); return; }
            var names = new List<string>();
            foreach (InvItemBlueprint bp in found) { if (bp != null) names.Add(bp.Name); if (names.Count >= 10) break; }
            Reply(player, "ItemsFound", search, string.Join(", ", names.ToArray()));
        }

        private void CmdCollect(Player player)
        {
            if (!HasOwed(player.Id.ToString())) { Reply(player, "NothingOwed"); return; }
            lastFullWarning.Remove(player.Id.ToString());
            PayOwed(player);
        }

        // /market sell|bid <qty> <price each> <item words...> [NNh]   (args[0] is the verb)
        private void PostOrder(Player player, Party owner, string side, string[] args)
        {
            if (data.MarketFrozen) { ReplyError(player, "Frozen"); return; }
            if (args.Length < 4) { player.SendMessage(Msg("HelpMarket2", player)); return; }
            int qty;
            long price;
            if (!ParseInt(player, args[1], config.MaxPerCommand, out qty)) return;
            if (!ParseLong(player, args[2], config.MaxPricePerUnit, out price)) return;
            int hours = config.DefaultListingHours;
            int last = args.Length - 1;
            string tail = args[last].ToLowerInvariant();
            if (last > 3 && tail.Length > 1 && tail.EndsWith("h"))
            {
                int h;
                if (int.TryParse(tail.Substring(0, tail.Length - 1), out h))
                {
                    if (h < 1 || h > config.MaxListingHours) { ReplyError(player, "BadNumber", args[last], 1, config.MaxListingHours); return; }
                    hours = h;
                    last--;
                }
            }
            InvItemBlueprint bp = ResolveItem(player, JoinRange(args, 3, last));
            if (bp == null) return;

            string cdKey = "post:" + Label(owner);
            if (OnCooldown(player, cdKey)) return;
            int mine = 0, total = 0;
            foreach (Listing l in data.Listings)
            {
                if (l.Status != SOpen) continue;
                total++;
                if (SameParty(OwnerOf(l), owner)) mine++;
            }
            if (mine >= config.MaxOpenPerOwner) { ReplyError(player, "TooManyMine", owner.Name, mine); return; }
            if (total >= config.MaxOpenTotal) { ReplyError(player, "TooManyTotal"); return; }

            long cost = qty * price;
            var l2 = new Listing
            {
                Side = side, Status = SOpen, OwnerKind = owner.Kind, OwnerId = owner.Id, OwnerName = owner.Name, PostedBy = player.Id.ToString(),
                Item = bp.Name, Initial = qty, Remaining = qty, Price = price,
                PostedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(hours)
            };
            string note = (side == SideSell ? "ask " : "bid ") + qty + " " + bp.Name + " @" + price;
            if (side == SideSell)
            {
                if (owner.Kind != KPlayer && Held(VaultOf(owner, false), bp.Name) < qty)
                {
                    ReplyError(player, "NotEnoughStock", owner.Name, Held(VaultOf(owner, false), bp.Name), bp.Name);
                    return;
                }
                if (owner.Kind == KPlayer)
                {
                    int have = CountItems(player, bp);
                    if (have < qty) { ReplyError(player, "NotEnoughItems", qty, bp.Name, have); return; }
                }
                if (!SpendItems(player, owner, qty)) return;
                l2.Id = data.NextListingId++;
                int taken = TakeGoods(owner, player, bp, qty, "escrow_in", "order #" + l2.Id + " " + note);
                if (taken < qty) { ReplyError(player, "TakeFailed"); SaveData(); return; }
            }
            else
            {
                if (MarksOf(owner) < cost) { ReplyError(player, "NotEnoughMarks", owner.Name, MarksOf(owner), config.CurrencyName, cost); return; }
                if (!SpendMarks(player, owner, cost)) return;
                l2.Id = data.NextListingId++;
                DebitMarks(owner, cost);
                l2.EscrowMarks = cost;
                Journal("escrow_in", player.Name, MarksAsset, cost, Label(owner), "custody", "order #" + l2.Id + " " + note);
            }
            data.Listings.Add(l2);
            SetCooldown(cdKey, config.PostCooldownSeconds);
            SaveData();                                          // escrow is recorded before anything else happens
            Reply(player, "Posted", l2.Id, (side == SideSell ? "selling " : "buying ") + qty + " " + bp.Name + " at " + price + " " + config.CurrencyName + " each");
        }

        // buy: take from an ask (wantSide = sell). fill: sell into a bid (wantSide = bid).
        private void TradeOrder(Player player, Party me, string wantSide, string[] args)
        {
            if (data.MarketFrozen) { ReplyError(player, "Frozen"); return; }
            Listing l = args.Length > 1 ? FindListing(args[1]) : null;
            if (l == null || l.Status != SOpen || DateTime.UtcNow >= l.ExpiresAt) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            if (l.Side != wantSide) { ReplyError(player, "WrongSide", l.Id, l.Side, l.Side == SideSell ? "buy" : "fill"); return; }
            Party owner = OwnerOf(l);
            // No trading with yourself: not your own order, not one you posted for a vault, not your own order through a vault.
            string myId = player.Id.ToString();
            if (SameParty(owner, me) || l.PostedBy == myId || (owner.Kind == KPlayer && owner.Id == myId)) { ReplyError(player, "OwnOrder"); return; }
            int qty = l.Remaining;
            if (args.Length > 2 && !ParseInt(player, args[2], l.Remaining, out qty)) return;
            if (qty > config.MaxPerCommand) qty = config.MaxPerCommand;
            string cdKey = "trade:" + player.Id;
            if (OnCooldown(player, cdKey)) return;
            if (wantSide == SideSell) Buy(player, me, l, qty); else Fill(player, me, l, qty);
            SetCooldown(cdKey, config.TradeCooldownSeconds);
        }

        private long FeeOn(Party payee, long total)
        {
            if (payee.Kind == KTreasury) return 0;               // the crown does not tax itself
            return total * data.MarketFeePercent / 100;
        }

        private void Buy(Player player, Party buyer, Listing l, int qty)
        {
            long total = qty * l.Price;
            Party seller = OwnerOf(l);
            if (MarksOf(buyer) < total) { ReplyError(player, "NotEnoughMarks", buyer.Name, MarksOf(buyer), config.CurrencyName, total); return; }
            if (buyer.Kind != KPlayer && !VaultCanHold(buyer, l.Item, qty)) { ReplyFull(player, buyer, l.Item); return; }
            if (!SpendMarks(player, buyer, total)) return;
            long fee = FeeOn(seller, total);
            // State first, then the transfers; all in memory and saved together before any item is paid out.
            DebitMarks(buyer, total);
            CreditMarks(seller, total - fee);
            CreditMarks(TreasuryParty(), fee);
            l.Remaining -= qty;
            string reference = "order #" + l.Id;
            Journal("trade", player.Name, MarksAsset, total - fee, Label(buyer), Label(seller), reference);
            if (fee > 0) Journal("market_fee", player.Name, MarksAsset, fee, Label(buyer), "treasury", reference);
            DeliverGoods(buyer, l.Item, qty, "escrow_out", player.Name, reference);
            RecordTrade(l, qty, seller.Name, buyer.Name);
            if (l.Remaining <= 0) { l.Status = SDone; l.Outcome = "Sold out."; }
            SaveData();
            Reply(player, "Bought", qty, l.Item, total, config.CurrencyName, l.Id);
            NotifyOwner(l, seller, "sold " + qty, buyer.Name);
            if (buyer.Kind == KPlayer) PayOwed(player);
            ChronicleTrade(l, qty, total, seller.Name, buyer.Name);
        }

        private void Fill(Player player, Party seller, Listing l, int qty)
        {
            long total = qty * l.Price;
            Party bidder = OwnerOf(l);
            InvItemBlueprint bp = FindItem(l.Item);
            if (bp == null) { ReplyError(player, "UnknownItem", l.Item); return; }
            if (total > l.EscrowMarks) { ReplyError(player, "NotFound", l.Id); return; }   // never pay out more than is held
            if (bidder.Kind != KPlayer && !VaultCanHold(bidder, l.Item, qty)) { ReplyFull(player, bidder, l.Item); return; }
            if (seller.Kind == KPlayer)
            {
                int have = CountItems(player, bp);
                if (have < qty) { ReplyError(player, "NotEnoughItems", qty, bp.Name, have); return; }
            }
            else if (Held(VaultOf(seller, false), bp.Name) < qty)
            {
                ReplyError(player, "NotEnoughStock", seller.Name, Held(VaultOf(seller, false), bp.Name), bp.Name);
                return;
            }
            if (!SpendItems(player, seller, qty)) return;
            string reference = "order #" + l.Id;
            int taken = TakeGoods(seller, player, bp, qty, "escrow_in", reference);
            if (taken < qty) { ReplyError(player, "TakeFailed"); SaveData(); return; }
            long fee = FeeOn(seller, total);
            l.EscrowMarks -= total;
            l.Remaining -= qty;
            CreditMarks(seller, total - fee);
            CreditMarks(TreasuryParty(), fee);
            Journal("trade", player.Name, MarksAsset, total - fee, "custody", Label(seller), reference);
            if (fee > 0) Journal("market_fee", player.Name, MarksAsset, fee, "custody", "treasury", reference);
            DeliverGoods(bidder, l.Item, qty, "escrow_out", player.Name, reference);
            RecordTrade(l, qty, seller.Name, bidder.Name);
            if (l.Remaining <= 0)
            {
                l.Status = SDone;
                l.Outcome = "Filled.";
                if (l.EscrowMarks > 0) { CreditMarks(bidder, l.EscrowMarks); Journal("escrow_out", player.Name, MarksAsset, l.EscrowMarks, "custody", Label(bidder), reference); l.EscrowMarks = 0; }
            }
            SaveData();
            Reply(player, "Filled", qty, l.Item, l.Id, total - fee, config.CurrencyName, fee);
            NotifyOwner(l, bidder, "bought " + qty, seller.Name);
            ChronicleTrade(l, qty, total, seller.Name, bidder.Name);
        }

        private void NotifyOwner(Listing l, Party owner, string what, string counterparty)
        {
            if (owner.Kind != KPlayer) return;
            Player p = OnlineById(owner.Id);
            if (p == null) return;
            Reply(p, "OwnerNotice", l.Id, what, l.Item, l.Price, counterparty);
            PayOwed(p);
        }

        private void CancelOrder(Player player, Party me, string[] args)
        {
            Listing l = args.Length > 1 ? FindListing(args[1]) : null;
            if (l == null || l.Status != SOpen) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            if (!SameParty(OwnerOf(l), me)) { ReplyError(player, "NotOwner"); return; }
            CloseListing(l, SCancelled, "Withdrawn by " + player.Name + ".", player);
            Reply(player, "Cancelled", l.Id, l.OwnerName);
        }

        // Returns whatever escrow the order still holds to its owner. State changes first.
        private void CloseListing(Listing l, string status, string outcome, Player actor)
        {
            if (l.Status != SOpen) return;
            l.Status = status;
            l.Outcome = outcome;
            Party owner = OwnerOf(l);
            string who = actor != null ? actor.Name : "realm";
            string reference = "order #" + l.Id + " " + status;
            if (l.Side == SideSell && l.Remaining > 0)
            {
                int back = l.Remaining;
                l.Remaining = 0;
                DeliverGoods(owner, l.Item, back, "escrow_out", who, reference);
            }
            if (l.Side == SideBid && l.EscrowMarks > 0)
            {
                long back = l.EscrowMarks;
                l.EscrowMarks = 0;
                l.Remaining = 0;
                CreditMarks(owner, back);
                Journal("escrow_out", who, MarksAsset, back, "custody", Label(owner), reference);
            }
            SaveData();
            if (owner.Kind == KPlayer)
            {
                Player p = OnlineById(owner.Id);
                if (p != null) PayOwed(p);
            }
        }

        private void RecordTrade(Listing l, int qty, string seller, string buyer)
        {
            DateTime now = DateTime.UtcNow;
            data.Trades.Add(new Trade { At = now, Item = l.Item, Qty = qty, Price = l.Price, Seller = seller, Buyer = buyer, ListingId = l.Id });
            if (data.Trades.Count > config.TradeHistoryMax) data.Trades.RemoveRange(0, data.Trades.Count - config.TradeHistoryMax);
            PriceStat st;
            if (!data.Prices.TryGetValue(l.Item, out st))
            {
                st = new PriceStat { Low = l.Price, High = l.Price };
                data.Prices[l.Item] = st;
            }
            st.Last = l.Price;
            st.LastAt = now;
            st.Trades++;
            st.Units += qty;
            st.Marks += qty * l.Price;
            if (l.Price < st.Low) st.Low = l.Price;
            if (l.Price > st.High) st.High = l.Price;
        }

        #endregion

        #region Chat: vaults

        [ChatCommand("vault")]
        private void CmdVault(Player player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "help") { ShowHelp(player, false, true, false); return; }
            if (RealmHouses == null) { ReplyError(player, "NoHousePlugin"); return; }
            string house = HouseOf(player.Id);
            if (house != null) { CheckLineage(CanonicalHouse(house)); house = CanonicalHouse(house); }
            if (sub == "" || (args.Length >= 1 && !IsVaultVerb(sub)))
            {
                string which = args.Length > 0 ? JoinFrom(args, 0) : house;
                if (which == null) { ReplyError(player, "NeedHouse"); return; }
                CheckLineage(CanonicalHouse(which));
                ShowVault(player, HouseParty(CanonicalHouse(which)));
                return;
            }
            if (house == null) { ReplyError(player, "NeedHouse"); return; }
            Party hp = HouseParty(house);
            switch (sub)
            {
                case "deposit": VaultDeposit(player, hp, args, "vault_deposit"); return;
                case "withdraw": if (RequireSteward(player, house)) VaultWithdraw(player, hp, PlayerParty(player), args, 1, "vault_withdraw"); return;
                case "give": MarksIntoVault(player, hp, args); return;
                case "take": if (RequireSteward(player, house)) MarksOutOfVault(player, hp, PlayerParty(player), args, 1, "vault_take"); return;
                case "steward": CmdSteward(player, house, args); return;
                case "sell": if (RequireSteward(player, house)) PostOrder(player, hp, SideSell, args); return;
                case "bid": if (RequireSteward(player, house)) PostOrder(player, hp, SideBid, args); return;
                case "buy": if (RequireSteward(player, house)) TradeOrder(player, hp, SideSell, args); return;
                case "fill": if (RequireSteward(player, house)) TradeOrder(player, hp, SideBid, args); return;
                case "cancel": if (RequireSteward(player, house)) CancelOrder(player, hp, args); return;
            }
        }

        private static bool IsVaultVerb(string s)
        {
            switch (s)
            {
                case "deposit": case "withdraw": case "give": case "take": case "steward": case "sell": case "bid":
                case "buy": case "fill": case "cancel": return true;
            }
            return false;
        }

        private void ShowVault(Player player, Party p)
        {
            Vault v = VaultOf(p, false);
            if (p.Kind == KTreasury)
                Reply(player, "TreasuryHead", v.Marks, config.CurrencyName, GoodsText(player, v));
            else
                Reply(player, "VaultHead", p.Name, v != null ? v.Marks : 0, config.CurrencyName, GoodsText(player, v));
            if (p.Kind == KHouse && v != null && v.Stewards.Count > 0)
            {
                var names = new List<string>();
                foreach (string id in v.Stewards) names.Add(NameOf(id));
                Reply(player, "Stewards", p.Id, string.Join(", ", names.ToArray()));
            }
        }

        private string GoodsText(Player player, Vault v)
        {
            if (v == null || v.Items.Count == 0) return Msg("Empty", player);
            var keys = new List<string>(v.Items.Keys);
            keys.Sort(delegate(string a, string b) { return v.Items[b].CompareTo(v.Items[a]); });
            var parts = new List<string>();
            for (int i = 0; i < keys.Count && i < 15; i++) parts.Add(v.Items[keys[i]] + " " + keys[i]);
            if (keys.Count > 15) parts.Add("+" + (keys.Count - 15) + " more");
            return string.Join(", ", parts.ToArray());
        }

        private bool RequireSteward(Player player, string house)
        {
            if (IsHouseLeader(player, house)) return true;
            Vault v = VaultOf(HouseParty(house), false);
            if (v != null && v.Stewards.Contains(player.Id.ToString())) return true;
            ReplyError(player, "NotSteward", house);
            return false;
        }

        // /vault deposit <qty> <item>   or   /treasury deposit|taxin <qty> <item>
        private void VaultDeposit(Player player, Party vault, string[] args, string kind)
        {
            if (args.Length < 3) { ShowHelp(player, false, vault.Kind == KHouse, vault.Kind == KTreasury); return; }
            int qty;
            if (!ParseInt(player, args[1], config.MaxPerCommand, out qty)) return;
            InvItemBlueprint bp = ResolveItem(player, JoinFrom(args, 2));
            if (bp == null) return;
            string cdKey = "deposit:" + player.Id;
            if (OnCooldown(player, cdKey)) return;
            if (!VaultCanHold(vault, bp.Name, qty)) { ReplyFull(player, vault, bp.Name); return; }
            int have = CountItems(player, bp);
            if (have < qty) { ReplyError(player, "NotEnoughItems", qty, bp.Name, have); return; }
            int taken = TakeGoods(PlayerParty(player), player, bp, qty, kind, "into " + Label(vault));
            if (taken < qty) { ReplyError(player, "TakeFailed"); SaveData(); return; }
            AddToVault(VaultOf(vault, true), bp.Name, taken);
            Journal(kind, player.Name, bp.Name, taken, "custody", Label(vault), null);
            if (kind == "tax_in") AddFlow(data.TaxDeposited, bp.Name, taken);
            SetCooldown(cdKey, vault.Kind == KTreasury ? config.TreasuryActionCooldownSeconds : config.VaultActionCooldownSeconds);
            SaveData();
            Reply(player, "Deposited", taken, bp.Name, vault.Name);
        }

        // Moves goods out of a vault to a player (the actor or a named recipient) via the owed ledger.
        private void VaultWithdraw(Player player, Party vault, Party to, string[] args, int qtyIdx, string kind)
        {
            if (args.Length < qtyIdx + 2) { ShowHelp(player, false, vault.Kind == KHouse, vault.Kind == KTreasury); return; }
            int qty;
            if (!ParseInt(player, args[qtyIdx], config.MaxPerCommand, out qty)) return;
            InvItemBlueprint bp = ResolveItem(player, JoinFrom(args, qtyIdx + 1));
            if (bp == null) return;
            string cdKey = "withdraw:" + Label(vault);
            if (OnCooldown(player, cdKey)) return;
            int held = Held(VaultOf(vault, false), bp.Name);
            if (held < qty) { ReplyError(player, "NotEnoughStock", vault.Name, held, bp.Name); return; }
            if (!SpendItems(player, vault, qty)) return;
            if (TakeGoods(vault, player, bp, qty, kind, "to " + Label(to)) < qty) { ReplyError(player, "NotEnoughStock", vault.Name, held, bp.Name); return; }
            DeliverGoods(to, bp.Name, qty, kind, player.Name, "from " + Label(vault));
            SetCooldown(cdKey, vault.Kind == KTreasury ? config.TreasuryActionCooldownSeconds : config.VaultActionCooldownSeconds);
            SaveData();
            if (to.Id == player.Id.ToString()) Reply(player, "Withdrawn", qty, bp.Name, vault.Name);
            else
            {
                Reply(player, "Awarded", qty, bp.Name, to.Name);
                Chronicle("treasury_grant", "decree", "The crown rewards " + to.Name, player.Name + " awards " + qty + " " + bp.Name
                    + " from the treasury to " + to.Name + ".", new[] { player.Name, to.Name });
            }
            Player recipient = OnlineById(to.Id);
            if (recipient != null) PayOwed(recipient);
        }

        private void MarksIntoVault(Player player, Party vault, string[] args)
        {
            long n;
            if (args.Length < 2 || !ParseLong(player, args[1], Math.Max(1, config.MintSupplyCap), out n)) { if (args.Length < 2) ShowHelp(player, false, true, false); return; }
            Party me = PlayerParty(player);
            string cdKey = "marksin:" + player.Id;            // every give writes the ledger and the data file
            if (OnCooldown(player, cdKey)) return;
            if (MarksOf(me) < n) { ReplyError(player, "NotEnoughMarks", "Your purse", MarksOf(me), config.CurrencyName, n); return; }
            DebitMarks(me, n);
            CreditMarks(vault, n);
            Journal("marks_in", player.Name, MarksAsset, n, Label(me), Label(vault), null);
            SetCooldown(cdKey, config.VaultActionCooldownSeconds);
            SaveData();
            Reply(player, "MarksMoved", n, config.CurrencyName, "your purse", vault.Name);
        }

        private void MarksOutOfVault(Player player, Party vault, Party to, string[] args, int nIdx, string kind)
        {
            long n;
            if (args.Length <= nIdx) { ShowHelp(player, false, vault.Kind == KHouse, vault.Kind == KTreasury); return; }
            if (!ParseLong(player, args[nIdx], Math.Max(1, MarksBudget(vault)), out n)) return;
            string cdKey = "marks:" + Label(vault);
            if (OnCooldown(player, cdKey)) return;
            if (MarksOf(vault) < n) { ReplyError(player, "NotEnoughMarks", vault.Name, MarksOf(vault), config.CurrencyName, n); return; }
            if (!SpendMarks(player, vault, n)) return;
            DebitMarks(vault, n);
            CreditMarks(to, n);
            Journal(kind, player.Name, MarksAsset, n, Label(vault), Label(to), null);
            SetCooldown(cdKey, vault.Kind == KTreasury ? config.TreasuryActionCooldownSeconds : config.VaultActionCooldownSeconds);
            SaveData();
            if (to.Id == player.Id.ToString()) Reply(player, "MarksMoved", n, config.CurrencyName, vault.Name, "your purse");
            else
            {
                Reply(player, "Granted", n, config.CurrencyName, to.Name);
                Player r = OnlineById(to.Id);
                if (r != null) Reply(r, "GrantedYou", n, config.CurrencyName);
                Chronicle("treasury_grant", "decree", "The crown rewards " + to.Name, player.Name + " grants " + n + " "
                    + config.CurrencyName + " from the treasury to " + to.Name + ".", new[] { player.Name, to.Name });
            }
        }

        private void CmdSteward(Player player, string house, string[] args)
        {
            if (!IsHouseLeader(player, house)) { ReplyError(player, "NotLeader", house); return; }
            if (args.Length < 3) { ShowHelp(player, false, true, false); return; }
            string op = args[1].ToLowerInvariant();
            Player target = FindOnline(JoinFrom(args, 2));
            Vault v = VaultOf(HouseParty(house), true);
            if (op == "remove")
            {
                string name = JoinFrom(args, 2);
                string tid = target != null ? target.Id.ToString() : null;
                foreach (string id in v.Stewards.ToArray())
                    if (id == tid || string.Equals(NameOf(id), name, StringComparison.OrdinalIgnoreCase)) { tid = id; break; }
                if (tid == null || !v.Stewards.Remove(tid)) { ReplyError(player, "PlayerNotFound"); return; }
                Journal("steward_removed", player.Name, "-", 0, "house:" + house, NameOf(tid), null);
                SaveData();
                Reply(player, "StewardRemoved", NameOf(tid), house);
                return;
            }
            if (op != "add") { ShowHelp(player, false, true, false); return; }
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            if (!string.Equals(HouseOf(target.Id), house, StringComparison.OrdinalIgnoreCase)) { ReplyError(player, "NotMember", target.Name, house); return; }
            string tId = target.Id.ToString();
            if (!v.Stewards.Contains(tId))
            {
                if (v.Stewards.Count >= config.MaxStewards) { ReplyError(player, "StewardsFull", config.MaxStewards); return; }
                v.Stewards.Add(tId);
                data.Names[tId] = target.Name;
                Journal("steward_added", player.Name, "-", 0, "house:" + house, target.Name, null);
                SaveData();
            }
            Reply(player, "StewardAdded", target.Name, house);
        }

        #endregion

        #region Chat: treasury

        [ChatCommand("treasury")]
        private void CmdTreasury(Player player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Party t = TreasuryParty();
            switch (sub)
            {
                case "":
                    ShowVault(player, t);
                    Reply(player, "TreasuryRates", data.MarketFeePercent, data.TithePercent, config.TitheMaxPercent, data.MarksMinted + " " + config.CurrencyName,
                        config.MintSupplyCap, TaxText(), PercentText(SafeTaxMaximum()));
                    return;
                case "help": ShowHelp(player, false, false, true); return;
                case "tax": CmdTax(player); return;
                case "ledger": CmdLedger(player, args); return;
                case "deposit": VaultDeposit(player, t, args, "tribute"); return;
                case "taxin": if (RequireTreasurer(player)) VaultDeposit(player, t, args, "tax_in"); return;
                case "withdraw": if (RequireTreasurer(player)) VaultWithdraw(player, t, PlayerParty(player), args, 1, "treasury_withdraw"); return;
                case "award": CmdAward(player, args); return;
                case "grant": CmdGrant(player, args); return;
                case "sell": if (RequireTreasurer(player)) PostOrder(player, t, SideSell, args); return;
                case "bid": if (RequireTreasurer(player)) PostOrder(player, t, SideBid, args); return;
                case "buy": if (RequireTreasurer(player)) TradeOrder(player, t, SideSell, args); return;
                case "fill": if (RequireTreasurer(player)) TradeOrder(player, t, SideBid, args); return;
                case "cancel": CmdTreasuryCancel(player, args); return;
                case "mint": CmdMint(player, args); return;
                case "tithe": CmdSetRate(player, args, true); return;
                case "fee": CmdSetRate(player, args, false); return;
                case "levy": CmdLevy(player); return;
                case "escheat": CmdEscheat(player, args); return;
                case "restore": CmdRestore(player, args); return;
                case "audit": CmdAudit(player); return;
                case "freeze": CmdFreeze(player, true); return;
                case "unfreeze": CmdFreeze(player, false); return;
            }
            ShowHelp(player, false, false, true);
        }

        private bool RequireCrown(Player player)
        {
            KingsScheme ks = Crown();
            if (ks == null || !ks.HasKing()) { ReplyError(player, "NoKing"); return false; }
            if (!ks.IsKing(player)) { ReplyError(player, "NotCrown"); return false; }
            return true;
        }

        private bool RequireTreasurer(Player player)
        {
            KingsScheme ks = Crown();
            if (ks != null && ks.HasKing() && ks.IsKing(player)) return true;
            if (ks != null && ks.HasKing() && IsTreasurerSeat(player)) return true;
            ReplyError(player, "NotTreasurer", config.TreasurerSeat);
            return false;
        }

        private bool IsTreasurerSeat(Player player)
        {
            if (CrownAndConsequences == null || string.IsNullOrEmpty(config.TreasurerSeat)) return false;
            string seat = CrownAndConsequences.Call("GetCouncilSeat", player.Id) as string;
            return seat != null && string.Equals(seat, config.TreasurerSeat, StringComparison.OrdinalIgnoreCase);
        }

        private void CmdTax(Player player)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, long> kv in data.TaxObserved) parts.Add(kv.Value + " " + kv.Key);
            var dep = new List<string>();
            foreach (KeyValuePair<string, long> kv in data.TaxDeposited) dep.Add(kv.Value + " " + kv.Key);
            Reply(player, "TaxReport", TaxText(), data.TaxSince == DateTime.MinValue ? "-" : data.TaxSince.ToString("yyyy-MM-dd HH:mm") + " UTC",
                parts.Count > 0 ? string.Join(", ", parts.ToArray()) : Msg("TaxNone", player), data.TaxEvents,
                dep.Count > 0 ? string.Join(", ", dep.ToArray()) : Msg("TaxNone", player));
        }

        private void CmdLedger(Player player, string[] args)
        {
            int n = 8;
            if (args.Length > 1 && !ParseInt(player, args[1], 25, out n)) return;
            for (int i = Math.Max(0, data.Journal.Count - n); i < data.Journal.Count; i++)
            {
                Entry e = data.Journal[i];
                player.SendMessage(Fmt("LedgerLine", player, new object[] { e.Seq, e.At.ToString("MM-dd HH:mm"), e.Kind, e.Amount,
                    e.Asset == MarksAsset ? config.CurrencyName : e.Asset, e.From, e.To, e.Actor }));
            }
        }

        // /treasury award <player> <qty> <item>
        private void CmdAward(Player player, string[] args)
        {
            if (!RequireTreasurer(player)) return;
            if (args.Length < 4) { ShowHelp(player, false, false, true); return; }
            Player target = FindOnline(args[1]);
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            VaultWithdraw(player, TreasuryParty(), PlayerParty(target), args, 2, "treasury_award");
        }

        // /treasury grant <player> <n>
        private void CmdGrant(Player player, string[] args)
        {
            if (!RequireTreasurer(player)) return;
            if (args.Length < 3) { ShowHelp(player, false, false, true); return; }
            Player target = FindOnline(JoinRange(args, 1, args.Length - 2));
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            MarksOutOfVault(player, TreasuryParty(), PlayerParty(target), args, args.Length - 1, "treasury_grant");
        }

        private void CmdTreasuryCancel(Player player, string[] args)
        {
            if (IsAdmin(player))
            {
                Listing l = args.Length > 1 ? FindListing(args[1]) : null;
                if (l == null || l.Status != SOpen) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
                CloseListing(l, SCancelled, "Set aside by the realm's stewards.", player);
                Reply(player, "AdminDone", "#" + l.Id + " " + l.Status);
                return;
            }
            if (RequireTreasurer(player)) CancelOrder(player, TreasuryParty(), args);
        }

        // The king's minting decree: marks only, never items. Caps: per decree, rolling day, total supply, cooldown.
        private void CmdMint(Player player, string[] args)
        {
            if (!RequireCrown(player)) return;
            long n;
            if (args.Length < 2 || !ParseLong(player, args[1], Math.Max(1, config.MintMaxPerDecree), out n)) { if (args.Length < 2) ShowHelp(player, false, false, true); return; }
            DateTime now = DateTime.UtcNow;
            DateTime ready = data.LastMint.AddMinutes(config.MintCooldownMinutes);
            if (data.LastMint != DateTime.MinValue && ready > now) { ReplyError(player, "MintCooldown", MinutesUntil(ready)); return; }
            if (data.MarksMinted + n > config.MintSupplyCap) { ReplyError(player, "MintCap", config.MintSupplyCap, config.CurrencyName, data.MarksMinted); return; }
            long left = config.MintMaxPerDay - SpentToday("mint");
            if (n > left) { ReplyError(player, "MintTooMuch", config.MintMaxPerDecree, Math.Max(0, left)); return; }
            data.Spends.Add(new Spend { At = now, Key = "mint", Amount = n });
            data.MarksMinted += n;
            data.Treasury.Marks += n;
            data.LastMint = now;
            Journal("mint", player.Name, MarksAsset, n, "mint", "treasury", "supply " + data.MarksMinted);
            SaveData();
            Reply(player, "Minted", n, config.CurrencyName, data.MarksMinted);
            Broadcast(Fmt("Minted", null, new object[] { n, config.CurrencyName, data.MarksMinted }));
            Chronicle("treasury_mint", "decree", "The crown strikes " + n + " " + config.CurrencyName,
                player.Name + " orders " + n + " " + config.CurrencyName + " struck into the treasury. " + data.MarksMinted + " now exist.",
                new[] { player.Name });
        }

        private void CmdSetRate(Player player, string[] args, bool tithe)
        {
            if (!RequireCrown(player)) return;
            string what = tithe ? "The house tithe" : "The market fee";
            int max = tithe ? config.TitheMaxPercent : config.MarketFeeMaxPercent;
            if (args.Length < 2) { Reply(player, "RateSet", what, tithe ? data.TithePercent : data.MarketFeePercent); return; }
            int pct;
            if (!int.TryParse(args[1].TrimEnd('%'), out pct) || pct < 0) { ReplyError(player, "BadNumber", args[1], 0, max); return; }
            if (pct > max) { ReplyError(player, "RateCap", what, max); return; }
            DateTime last = tithe ? data.LastTitheSet : data.LastFeeSet;
            DateTime ready = last.AddHours(tithe ? config.TitheChangeCooldownHours : config.FeeChangeCooldownHours);
            if (last != DateTime.MinValue && ready > DateTime.UtcNow && !IsAdmin(player)) { ReplyError(player, "RateCooldown", what, MinutesUntil(ready)); return; }
            int old = tithe ? data.TithePercent : data.MarketFeePercent;
            if (tithe) { data.TithePercent = pct; data.LastTitheSet = DateTime.UtcNow; }
            else { data.MarketFeePercent = pct; data.LastFeeSet = DateTime.UtcNow; }
            Journal(tithe ? "tithe_rate" : "fee_rate", player.Name, "%", pct, old + "%", pct + "%", null);
            SaveData();
            Reply(player, "RateSet", what, pct);
            Chronicle("decree", null, what + " is set at " + pct + "%", player.Name + " sets " + what.ToLowerInvariant() + " at " + pct
                + "% (it was " + old + "%).", new[] { player.Name });
        }

        // The crown gathers TithePercent of each eligible house vault (goods and marks), capped per house.
        private void CmdLevy(Player player)
        {
            if (!RequireCrown(player)) return;
            if (data.TithePercent <= 0) { ReplyError(player, "LevyZero"); return; }
            DateTime now = DateTime.UtcNow;
            DateTime ready = data.LastLevy.AddHours(config.TitheLevyIntervalHours);
            if (data.LastLevy != DateTime.MinValue && ready > now) { ReplyError(player, "LevyCooldown", MinutesUntil(ready)); return; }
            if (config.TitheOnlySwornHouses && CrownAndConsequences == null) { ReplyError(player, "LevyNeedCrownPlugin"); return; }
            KingsScheme ks = Crown();
            string kingHouse = ks != null && ks.HasKing() ? HouseOf(ks.GetKingID()) : null;
            Party t = TreasuryParty();
            int houses = 0;
            long marksTotal = 0, unitsTotal = 0;
            foreach (string house in new List<string>(data.Houses.Keys)) CheckLineage(house);
            foreach (string house in new List<string>(data.Houses.Keys))
            {
                if (kingHouse != null && string.Equals(house, kingHouse, StringComparison.OrdinalIgnoreCase)) continue;
                if (config.TitheOnlySwornHouses && !IsSwornToCrown(house)) continue;
                Vault v = data.Houses[house];
                Party hp = HouseParty(house);
                bool paid = false;
                if (config.TitheItems)
                {
                    foreach (string item in new List<string>(v.Items.Keys))
                    {
                        int due = (int)Math.Min((long)v.Items[item] * data.TithePercent / 100, config.TitheMaxUnitsPerItemPerHouse);
                        if (due <= 0 || !VaultCanHold(t, item, due)) continue;
                        RemoveFromVault(v, item, due);
                        AddToVault(data.Treasury, item, due);
                        Journal("tithe", player.Name, item, due, Label(hp), "treasury", null);
                        unitsTotal += due;
                        paid = true;
                    }
                }
                if (config.TitheMarks)
                {
                    long due = Math.Min(v.Marks * data.TithePercent / 100, config.TitheMaxMarksPerHouse);
                    if (due > 0)
                    {
                        v.Marks -= due;
                        data.Treasury.Marks += due;
                        Journal("tithe", player.Name, MarksAsset, due, Label(hp), "treasury", null);
                        marksTotal += due;
                        paid = true;
                    }
                }
                if (paid) houses++;
            }
            data.LastLevy = now;
            SaveData();
            if (houses == 0) { Reply(player, "LevyNone"); return; }
            string summary = unitsTotal + " goods and " + marksTotal + " " + config.CurrencyName;
            Reply(player, "Levied", data.TithePercent, houses, summary);
            Chronicle("tithe_levied", "decree", "The crown gathers its tithe", player.Name + " gathers a tithe of " + data.TithePercent
                + "% from " + houses + " houses: " + summary + ".", new[] { player.Name });
        }

        // A house that no longer exists (RealmHouses knows no leader and no members) forfeits its vault to the crown.
        private void CmdEscheat(Player player, string[] args)
        {
            if (!IsAdmin(player) && !RequireCrown(player)) return;
            if (args.Length < 2) { ShowHelp(player, false, false, true); return; }
            string house = CanonicalHouse(JoinFrom(args, 1));
            Vault v;
            if (RealmHouses == null || !data.Houses.TryGetValue(house, out v)
                || RealmHouses.Call("GetHouseLeader", house) != null || RealmHouses.Call("GetMembers", house) != null)
            {
                ReplyError(player, "NotEscheat", house);
                return;
            }
            Party hp = HouseParty(house);
            foreach (Listing l in data.Listings.ToArray())
                if (l.Status == SOpen && l.OwnerKind == KHouse && string.Equals(l.OwnerId, house, StringComparison.OrdinalIgnoreCase))
                    CloseListing(l, SCancelled, "House " + house + " is no more.", player);
            v = data.Houses[house];
            foreach (KeyValuePair<string, int> kv in new List<KeyValuePair<string, int>>(v.Items))
            {
                AddToVault(data.Treasury, kv.Key, kv.Value);            // may exceed the treasury cap: nothing is lost
                Journal("escheat", player.Name, kv.Key, kv.Value, Label(hp), "treasury", null);
            }
            if (v.Marks > 0)
            {
                data.Treasury.Marks += v.Marks;
                Journal("escheat", player.Name, MarksAsset, v.Marks, Label(hp), "treasury", null);
            }
            data.Houses.Remove(house);
            SaveData();
            Reply(player, "Escheated", house);
            Chronicle("decree", null, "The vault of House " + house + " passes to the crown", "House " + house
                + " is no more; its stores pass to the treasury.", new[] { player.Name });
        }

        // Admin only: a house that fell by accident (its last member left) and was founded again by the same people gets
        // its sealed vault back. Items and marks are merged into the current house's vault; nothing is created.
        private void CmdRestore(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string key = args.Length > 1 ? CanonicalHouse(JoinFrom(args, 1)) : "";
            int cut = key.IndexOf(" (fallen ", StringComparison.Ordinal);
            Vault sealedVault;
            string house = cut > 0 ? key.Substring(0, cut) : null;
            if (house == null || !data.Houses.TryGetValue(key, out sealedVault) || HouseFounded(house) == null)
            {
                ReplyError(player, "NotRestorable", key);
                return;
            }
            CheckLineage(CanonicalHouse(house));
            house = CanonicalHouse(house);
            Vault target = VaultOf(HouseParty(house), true);
            Party hp = HouseParty(house);
            foreach (KeyValuePair<string, int> kv in new List<KeyValuePair<string, int>>(sealedVault.Items))
            {
                AddToVault(target, kv.Key, kv.Value);                  // may exceed the vault cap: nothing is lost
                Journal("vault_restored", player.Name, kv.Key, kv.Value, "house:" + key, Label(hp), null);
            }
            if (sealedVault.Marks > 0)
            {
                target.Marks += sealedVault.Marks;
                Journal("vault_restored", player.Name, MarksAsset, sealedVault.Marks, "house:" + key, Label(hp), null);
            }
            data.Houses.Remove(key);
            SaveData();
            Reply(player, "Restored", key, house);
        }

        private void CmdAudit(Player player)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            List<string> problems = Audit();
            foreach (string p in problems) PrintWarning("Audit: " + p);
            if (problems.Count == 0) Reply(player, "AuditOk", config.CurrencyName, AuditedItemCount());
            else ReplyError(player, "AuditBad", problems.Count);
        }

        private void CmdFreeze(Player player, bool freeze)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            data.MarketFrozen = freeze;
            Journal(freeze ? "market_frozen" : "market_open", player.Name, "-", 0, "admin", "market", null);
            SaveData();
            Reply(player, "FrozenSet", freeze ? "closed" : "open");
        }

        #endregion

        #region Audit (zero-sum proof)

        private Dictionary<string, long> HeldNow()
        {
            var held = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> kv in data.Treasury.Items) AddFlow(held, kv.Key, kv.Value);
            foreach (Vault v in data.Houses.Values) foreach (KeyValuePair<string, int> kv in v.Items) AddFlow(held, kv.Key, kv.Value);
            foreach (Listing l in data.Listings) if (l.Status == SOpen && l.Side == SideSell) AddFlow(held, l.Item, l.Remaining);
            foreach (Owed o in data.Owed) AddFlow(held, o.Item, o.Amount);
            return held;
        }

        private int AuditedItemCount()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in data.ItemsIn.Keys) keys.Add(k);
            foreach (string k in HeldNow().Keys) keys.Add(k);
            return keys.Count;
        }

        // Returns one line per mismatch; empty means every item and every mark is accounted for.
        private List<string> Audit()
        {
            var problems = new List<string>();
            Dictionary<string, long> held = HeldNow();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in data.ItemsIn.Keys) keys.Add(k);
            foreach (string k in data.ItemsOut.Keys) keys.Add(k);
            foreach (string k in held.Keys) keys.Add(k);
            foreach (string k in keys)
            {
                long inn, outt, h;
                data.ItemsIn.TryGetValue(k, out inn);
                data.ItemsOut.TryGetValue(k, out outt);
                held.TryGetValue(k, out h);
                if (inn - outt != h) problems.Add(k + ": taken " + inn + " - paid out " + outt + " = " + (inn - outt) + ", but " + h + " are held");
            }
            long marks = data.Treasury.Marks;
            foreach (Vault v in data.Houses.Values) marks += v.Marks;
            foreach (long p in data.Purses.Values) marks += p;
            foreach (Listing l in data.Listings)
            {
                if (l.Status == SOpen && l.Side == SideBid) marks += l.EscrowMarks;
                if (l.Status != SOpen && l.EscrowMarks != 0) problems.Add("closed order #" + l.Id + " still holds " + l.EscrowMarks + " marks");
                if (l.Status != SOpen && l.Side == SideSell && l.Remaining != 0) problems.Add("closed order #" + l.Id + " still holds " + l.Remaining + " " + l.Item);
                if (l.Remaining < 0 || l.EscrowMarks < 0) problems.Add("order #" + l.Id + " is negative");
            }
            if (marks != data.MarksMinted) problems.Add("marks: minted " + data.MarksMinted + " but " + marks + " are held");
            return problems;
        }

        #endregion

        #region Items (escrow; same calls as RealmContracts)

        private InvItemBlueprint FindItem(string name)
        {
            if (string.IsNullOrEmpty(name) || InvBlueprints.Instance == null) return null;
            return InvBlueprints.Instance.GetBlueprintForName(name.Trim(), true, true);    // exact, case-insensitive [ASM; IL]
        }

        private InvItemBlueprint ResolveItem(Player player, string name)
        {
            InvItemBlueprint bp = FindItem(name);
            if (bp == null) { ReplyError(player, "UnknownItem", name); return null; }
            if ((config.AllowedItems.Count > 0 && !ContainsIgnoreCase(config.AllowedItems, bp.Name)) || ContainsIgnoreCase(config.BlockedItems, bp.Name))
            {
                ReplyError(player, "ItemNotAllowed", bp.Name);
                return null;
            }
            return bp;
        }

        private static ItemCollection InventoryOf(Player player)
        {
            if (player == null || player.Entity == null) return null;
            Container inv = player.GetInventory();
            return inv != null ? inv.Contents : null;
        }

        private static int CountItems(Player player, InvItemBlueprint bp)
        {
            ItemCollection items = InventoryOf(player);
            return items != null && bp != null ? ItemCollection.AutoCount(items, bp) : 0;
        }

        // Returns the number of units actually removed (measured).
        private static int TakeItems(Player player, InvItemBlueprint bp, int amount)
        {
            ItemCollection items = InventoryOf(player);
            if (items == null || bp == null || amount <= 0) return 0;
            int before = ItemCollection.AutoCount(items, bp);
            if (before < amount) return 0;
            ItemCollection.AutoSplit(items, bp, amount);
            int taken = before - ItemCollection.AutoCount(items, bp);
            return taken < 0 ? 0 : Math.Min(taken, amount);
        }

        // Returns the number of units actually added (measured).
        private static int GiveItems(Player player, InvItemBlueprint bp, int amount)
        {
            ItemCollection items = InventoryOf(player);
            if (items == null || bp == null || amount <= 0) return 0;
            ContainerManagement cm = bp.TryGet<ContainerManagement>();
            int limit = cm != null && cm.StackLimit > 0 ? cm.StackLimit : amount;
            int given = 0;
            while (given < amount)
            {
                int chunk = Math.Min(limit, amount - given);
                int before = ItemCollection.AutoCount(items, bp);
                ItemCollection.AutoMergeAdd(items, new InvGameItemStack(bp, chunk, null));
                int added = ItemCollection.AutoCount(items, bp) - before;
                if (added <= 0) break;
                given += Math.Min(added, chunk);
                if (added < chunk) break;
            }
            return given;
        }

        private void AddOwed(string playerId, string playerName, string item, int amount, string reference)
        {
            if (amount <= 0 || playerId == null) return;
            if (playerName != null) data.Names[playerId] = playerName;
            foreach (Owed o in data.Owed)
                if (o.PlayerId == playerId && string.Equals(o.Item, item, StringComparison.OrdinalIgnoreCase))
                {
                    o.Amount += amount;
                    o.Ref = reference;
                    if (playerName != null) o.PlayerName = playerName;
                    return;
                }
            data.Owed.Add(new Owed { PlayerId = playerId, PlayerName = playerName, Item = item, Amount = amount, Ref = reference });
        }

        private bool HasOwed(string playerId)
        {
            foreach (Owed o in data.Owed) if (o.PlayerId == playerId) return true;
            return false;
        }

        // Pays what is owed, reducing each entry only by the measured amount delivered, saving after each delivery.
        private void PayOwed(Player player)
        {
            if (data == null || player == null || player.IsServer || player.Entity == null) return;
            string id = player.Id.ToString();
            bool full = false;
            foreach (Owed o in data.Owed.ToArray())
            {
                if (o.PlayerId != id) continue;
                InvItemBlueprint bp = FindItem(o.Item);
                if (bp == null) continue;
                int given = GiveItems(player, bp, o.Amount);
                if (given > 0)
                {
                    o.Amount -= given;
                    AddFlow(data.ItemsOut, o.Item, given);
                    Journal("paid_out", player.Name, o.Item, given, "custody", Label(PlayerParty(player)), o.Ref);
                    if (o.Amount <= 0) data.Owed.Remove(o);
                    SaveData();                              // record each delivery as it happens
                    Reply(player, "Received", given, bp.Name);
                }
                if (o.Amount > 0) full = true;
            }
            if (!full) return;
            DateTime last;
            DateTime now = DateTime.UtcNow;
            if (lastFullWarning.TryGetValue(id, out last) && (now - last).TotalMinutes < 5) return;
            lastFullWarning[id] = now;
            foreach (Owed o in data.Owed)
                if (o.PlayerId == id) { ReplyError(player, "StillOwed", o.Amount, o.Item); break; }
        }

        #endregion

        #region Journal and chronicle

        private void Journal(string kind, string actor, string asset, long amount, string from, string to, string note)
        {
            var e = new Entry { Seq = ++data.Seq, At = DateTime.UtcNow, Kind = kind, Actor = actor, Asset = asset, Amount = amount, From = from, To = to, Note = note };
            data.Journal.Add(e);
            if (data.Journal.Count > config.JournalMax) data.Journal.RemoveRange(0, data.Journal.Count - config.JournalMax);
            dirty = true;
            if (config.LedgerToLogFile)
            {
                string line = e.Seq + "\t" + e.At.ToString("o") + "\t" + kind + "\t" + (actor ?? "") + "\t" + asset + "\t" + amount + "\t"
                    + (from ?? "") + "\t" + (to ?? "") + "\t" + (note ?? "");
                try { LogToFile("ledger", line, this, true, false); }
                catch (Exception ex) { PrintWarning("Ledger log write failed: " + ex.Message); }
            }
        }

        private void ChronicleTrade(Listing l, int qty, long total, string seller, string buyer)
        {
            if (total < config.ChronicleTradeMinMarks) return;
            Chronicle("great_trade", null, "A great sale at the market", seller + " sells " + qty + " " + l.Item + " to " + buyer + " for "
                + total + " " + config.CurrencyName + ".", new[] { seller, buyer });
        }

        // Capped per hour so trade churn never pushes political history out of the chronicle's retention window.
        // New event types are tried first; if the chronicle rejects them (not registered yet, returns 0), the line is
        // re-sent as `fallback` when one is given. Every line also goes to the server log.
        private void Chronicle(string type, string fallback, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            DateTime now = DateTime.UtcNow;
            while (chronicleTimes.Count > 0 && (now - chronicleTimes.Peek()).TotalHours >= 1) chronicleTimes.Dequeue();
            if (chronicleTimes.Count >= config.ChronicleMaxPerHour) return;
            chronicleTimes.Enqueue(now);
            object r = RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
            if (r is int && (int)r == 0 && fallback != null)
                RealmChronicle.Call("Log", fallback, title, detail, actors ?? new string[0]);
        }

        private void Broadcast(string text)
        {
            PrintToChat("{0}", Msg("Herald", null) + text);
        }

        #endregion

        #region API (plugin.Call; non-public on purpose)

        // Marks in a player's purse.
        private long GetPurse(string playerId)
        {
            long m;
            return data != null && playerId != null && data.Purses.TryGetValue(playerId, out m) ? m : 0;
        }

        // Rewards for deeds (RealmQuests): strikes up to `amount` new marks into a player's purse and returns how many it
        // paid (0 when refused). Not the crown's mint: RewardMintPerDay (rolling 24 h, all players), RewardMaxPerCall,
        // and never within RewardCrownReserve of MintSupplyCap, so rewards cannot use up what the crown may still strike.
        // Counted in MarksMinted, so the zero-sum audit holds. The caller keeps any shortfall owed and asks again later.
        private long RewardMarks(string playerId, string playerName, long amount, string source)
        {
            ulong u;
            if (data == null || !config.RewardsEnabled || amount <= 0 || playerId == null || playerId.Length < 17 || !ulong.TryParse(playerId, out u)) return 0;
            long n = Math.Min(amount, Math.Max(0, config.RewardMaxPerCall));
            n = Math.Min(n, Math.Max(0, config.RewardMintPerDay) - SpentToday("reward"));
            n = Math.Min(n, config.MintSupplyCap - Math.Max(0, config.RewardCrownReserve) - data.MarksMinted);
            if (n <= 0) return 0;
            string name = string.IsNullOrEmpty(playerName) ? NameOf(playerId) : playerName;
            data.Spends.Add(new Spend { At = DateTime.UtcNow, Key = "reward", Amount = n });
            data.MarksMinted += n;
            CreditMarks(PlayerParty(playerId, name), n);
            Journal("reward", source ?? "reward", MarksAsset, n, "reward", name, "supply " + data.MarksMinted);
            SaveData();
            return n;
        }

        // Marks held by the treasury.
        private long GetTreasuryMarks()
        {
            return data != null ? data.Treasury.Marks : 0;
        }

        // Units of an item held by the treasury.
        private int GetTreasuryItem(string item)
        {
            return data != null && item != null ? Held(data.Treasury, item) : 0;
        }

        // Last traded price per unit of an item, or 0.
        private long GetLastPrice(string item)
        {
            PriceStat st;
            return data != null && item != null && data.Prices.TryGetValue(item, out st) ? st.Last : 0;
        }

        // Income for a house from another Realm plugin (RealmDominion's holdings). New marks are struck straight into the
        // house vault and counted in MarksMinted, so the zero-sum audit holds. Limits: the supply cap and a rolling 24 h
        // budget per source (PluginIncomeMaxPerDay). The house must exist in RealmHouses (refused while it is not loaded).
        // Returns the marks credited (possibly fewer than asked), 0 when refused.
        private long GrantHouseIncome(string house, long marks, string source, string note)
        {
            if (data == null || string.IsNullOrEmpty(house) || marks <= 0 || string.IsNullOrEmpty(source)) return 0;
            if (HouseFounded(house) == null) return 0;
            string key = "income:" + source.ToLowerInvariant();
            long n = Math.Min(marks, config.PluginIncomeMaxPerDay - SpentToday(key));
            n = Math.Min(n, config.MintSupplyCap - data.MarksMinted);
            if (n <= 0) return 0;
            house = CanonicalHouse(house);
            CheckLineage(house);
            data.Spends.Add(new Spend { At = DateTime.UtcNow, Key = key, Amount = n });
            data.MarksMinted += n;
            CreditMarks(HouseParty(house), n);
            Journal("income", source, MarksAsset, n, "mint", "house:" + house, note ?? "");
            SaveData();
            return n;
        }

        // Summary for the chronicle/state page: "marks|minted|fee|tithe|openOrders".
        private string GetTreasurySummary()
        {
            if (data == null) return null;
            int open = 0;
            foreach (Listing l in data.Listings) if (l.Status == SOpen) open++;
            return data.Treasury.Marks + "|" + data.MarksMinted + "|" + data.MarketFeePercent + "|" + data.TithePercent + "|" + open;
        }

        #endregion

        #region Helpers

        private KingsScheme Crown()
        {
            return SocialAPI.Get<KingsScheme>();
        }

        private string TaxText()
        {
            KingsScheme ks = Crown();
            return ks != null ? PercentText(ks.GetTax()) : "?";
        }

        private static float SafeTaxMaximum()
        {
            try { return KingsRealm.TaxMaximum; }
            catch (Exception) { return 0f; }
        }

        // The game shows the tax as GetTax() * 100 with "{0:0.##}%" (TaxCollector.FORMAT_TAX, MULTIPLIER_TAX) [IL].
        private static string PercentText(float fraction)
        {
            return (fraction * 100f).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }

        private string HouseOf(ulong playerId)
        {
            if (RealmHouses == null) return null;
            string h = RealmHouses.Call("GetHouse", playerId.ToString()) as string;
            return string.IsNullOrEmpty(h) ? null : h;
        }

        private bool IsHouseLeader(Player player, string house)
        {
            if (RealmHouses == null || house == null) return false;
            string leader = RealmHouses.Call("GetHouseLeader", house) as string;
            return leader != null && leader == player.Id.ToString();
        }

        private bool IsSwornToCrown(string house)
        {
            if (house == null || CrownAndConsequences == null) return false;
            object r = CrownAndConsequences.Call("IsSwornToCrown", house);
            return r is bool && (bool)r;
        }

        // RealmHouses' founding date of the house now bearing this name, or null (plugin absent, or no such house).
        private string HouseFounded(string house)
        {
            if (RealmHouses == null || string.IsNullOrEmpty(house)) return null;
            string f = RealmHouses.Call("GetHouseFounded", house) as string;
            return string.IsNullOrEmpty(f) ? null : f;
        }

        // A house vault belongs to the house that filled it, not to its name. If that house fell and a new one was
        // founded under the same name (RealmHouses gives it a new founding date), the old vault is set apart under
        // "<name> (fallen <date>)": its open orders are withdrawn into it first, its stewards are dropped, and it can
        // only be escheated to the crown (or restored by an admin editing the data file). Nothing is created or lost.
        // A vault whose house is unknown right now (disbanded, or RealmHouses not loaded) is left as it is.
        private void CheckLineage(string house)
        {
            Vault v;
            if (data == null || house == null || !data.Houses.TryGetValue(house, out v)) return;
            if (house.IndexOf(" (fallen ", StringComparison.Ordinal) >= 0) return;
            string founded = HouseFounded(house);
            if (founded == null) return;
            if (string.IsNullOrEmpty(v.Founded)) { v.Founded = founded; dirty = true; return; }   // first sight (older data)
            if (string.Equals(v.Founded, founded, StringComparison.Ordinal)) return;
            foreach (Listing l in data.Listings.ToArray())
                if (l.Status == SOpen && l.OwnerKind == KHouse && string.Equals(l.OwnerId, house, StringComparison.OrdinalIgnoreCase))
                    CloseListing(l, SCancelled, "House " + house + " fell; a new house bears its name.", null);
            string day = v.Founded.Length >= 10 ? v.Founded.Substring(0, 10) : v.Founded;
            string key = house + " (fallen " + day + ")";
            for (int i = 2; data.Houses.ContainsKey(key); i++) key = house + " (fallen " + day + " #" + i + ")";
            data.Houses.Remove(house);
            v.Stewards.Clear();
            data.Houses[key] = v;
            Journal("vault_sealed", "realm", "-", 0, "house:" + house, "house:" + key, "new house of the same name founded " + founded);
            SaveData();
            PrintWarning("House " + house + " was founded again (" + founded + "); the fallen house's vault is kept apart as '" + key
                + "'. The crown may escheat it: /treasury escheat " + key);
        }

        private string CanonicalHouse(string name)
        {
            foreach (string k in data.Houses.Keys) if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return k;
            return name;
        }

        private string NameOf(string id)
        {
            string n;
            return data.Names.TryGetValue(id, out n) ? n : id;
        }

        private Listing FindListing(string idText)
        {
            int id;
            if (!int.TryParse((idText ?? "").TrimStart('#'), out id)) return null;
            foreach (Listing l in data.Listings) if (l.Id == id) return l;
            return null;
        }

        private bool ParseInt(Player player, string text, int max, out int value)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 1 || value > max)
            {
                ReplyError(player, "BadNumber", text, 1, max);
                return false;
            }
            return true;
        }

        private bool ParseLong(Player player, string text, long max, out long value)
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 1 || value > max)
            {
                ReplyError(player, "BadNumber", text, 1, max);
                return false;
            }
            return true;
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private static Player OnlineById(string id)
        {
            ulong n;
            if (id == null || !ulong.TryParse(id, out n)) return null;
            Player p = Server.GetPlayerById(n);
            return p != null && !p.IsServer && Server.PlayerIsOnline(n) ? p : null;
        }

        private static Player FindOnline(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Player exact = Server.GetPlayerByName(name);
            if (exact != null && !exact.IsServer) return exact;
            List<Player> matches = Server.MatchPlayerByName(name);
            if (matches == null) return null;
            Player found = null;
            foreach (Player p in matches)
            {
                if (p == null || p.IsServer) continue;
                if (found != null) return null;                  // ambiguous
                found = p;
            }
            return found;
        }

        private static string JoinFrom(string[] args, int start)
        {
            return start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start);
        }

        private static string JoinRange(string[] args, int first, int last)
        {
            return first > last || first >= args.Length ? "" : string.Join(" ", args, first, last - first + 1);
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int MinutesUntil(DateTime when)
        {
            return Math.Max(0, (int)Math.Ceiling((when - DateTime.UtcNow).TotalMinutes));
        }

        private static string Ago(DateTime when)
        {
            TimeSpan t = DateTime.UtcNow - when;
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + " min";
            if (t.TotalHours < 48) return (int)t.TotalHours + " h";
            return (int)t.TotalDays + " d";
        }

        #endregion
    }
}
