// Behaviour tests for plugins/RealmTreasury.cs. Run with run.sh (see there for what this does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Item;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static RealmTreasury P;
    static KingsScheme Kings;
    static Plugin Chron, Houses, Crown;
    static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    static Dictionary<string, ulong> Leader = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    static HashSet<string> Sworn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<ulong, string> Seat = new Dictionary<ulong, string>();
    static HashSet<string> KnownTypes = new HashSet<string> { "decree" };
    static List<string> ChronLog = new List<string>();
    static string Dir;
    static readonly string[] Items = { "Wood", "Stone", "Iron Ingot", "Gold Ore" };
    static Dictionary<string, long> Seeded = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }

    static object Inv(object o, string m, params object[] a)
    {
        var mi = o.GetType().GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return mi.Invoke(o, a);
    }
    static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    static object Data() { return F(P, "data"); }
    static object Cfg() { return F(P, "config"); }
    static IList L(string f) { return (IList)F(Data(), f); }
    static IDictionary D(string f) { return (IDictionary)F(Data(), f); }
    static long Lg(object o, string f) { return Convert.ToInt64(F(o, f)); }
    static object Treasury() { return F(Data(), "Treasury"); }
    static int VaultItem(object vault, string item) { var d = (IDictionary)F(vault, "Items"); return d.Contains(item) ? (int)d[item] : 0; }
    static object House(string h) { var d = D("Houses"); return d.Contains(h) ? d[h] : null; }
    static long Purse(Player p) { var d = D("Purses"); string k = p.Id.ToString(); return d.Contains(k) ? (long)d[k] : 0; }
    static int Has(Player p, string item) { int n; return p.Inventory.Contents.Counts.TryGetValue(item, out n) ? n : 0; }
    static void Give(Player p, string item, int n) { p.Inventory.Contents.Counts[item] = Has(p, item) + n; long s; Seeded.TryGetValue(item, out s); Seeded[item] = s + n; }
    static List<string> Audit() { return (List<string>)Inv(P, "Audit"); }
    static object LastListing() { var l = L("Listings"); return l[l.Count - 1]; }
    static int LastId() { return (int)F(LastListing(), "Id"); }

    // The strongest check: every unit that ever existed is either in a player's inventory or in the realm's custody.
    static string ZeroSum()
    {
        var held = (IDictionary)Inv(P, "HeldNow");
        var bad = new List<string>();
        foreach (string item in Items)
        {
            long inv = Server.ClientPlayers.Sum(p => (long)Has(p, item)) + Offline.Sum(p => (long)Has(p, item));
            long h = held.Contains(item) ? (long)held[item] : 0;
            long s; Seeded.TryGetValue(item, out s);
            if (inv + h != s) bad.Add(item + ": inventories " + inv + " + custody " + h + " != seeded " + s);
        }
        var audit = Audit();
        bad.AddRange(audit);
        return string.Join("; ", bad);
    }
    static List<Player> Offline = new List<Player>();

    static Player Mk(ulong id, string name, string house)
    {
        var p = new Player(id, name);
        if (house != null) HouseOf[id] = house;
        Server.ClientPlayers.Add(p);
        return p;
    }
    static void Cmd(Player p, string cmd, string line)
    {
        string[] args = line.Length == 0 ? new string[0] : line.Split(' ');
        string m = cmd == "market" ? "CmdMarket" : cmd == "vault" ? "CmdVault" : cmd == "treasury" ? "CmdTreasury" : cmd == "purse" ? "CmdPurse" : "CmdEconomy";
        p.Messages.Clear();
        Inv(P, m, p, cmd, args);
    }
    static bool Err(Player p) { return p.Messages.Any(m => m.StartsWith("ERR ")); }
    static bool Said(Player p, string s) { return p.All().IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0; }

    static string ZeroCooldownConfig()
    {
        var c = new RealmTreasury();
        Inv(c, "LoadDefaultConfig");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(c.Config.Json).AsObject();
        foreach (var k in new[] { "TreasuryActionCooldownSeconds", "MintCooldownMinutes", "TitheChangeCooldownHours", "FeeChangeCooldownHours",
                                  "VaultActionCooldownSeconds", "PostCooldownSeconds", "TradeCooldownSeconds", "PayCooldownSeconds" })
            doc[k] = 0;
        return doc.ToJsonString();
    }

    static RealmTreasury NewPlugin(string configJson)
    {
        var p = new RealmTreasury();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "CrownAndConsequences", Crown);
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "treasurytest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Kings = new KingsScheme();
        SocialAPI.Registry[typeof(KingsScheme)] = Kings;
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) => { if (h != "Log") return null; if (!KnownTypes.Contains((string)a[0])) return 0; ChronLog.Add(a[0] + ": " + a[1] + " | " + a[2]); return ChronLog.Count; } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (h == "GetHouse") { string s; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out s) ? s : null; }
            if (h == "GetHouseLeader") { ulong id; return Leader.TryGetValue((string)a[0], out id) ? id.ToString() : null; }
            if (h == "GetMembers") { var m = HouseOf.Where(kv => string.Equals(kv.Value, (string)a[0], StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.ToString()).ToList(); return m.Count > 0 ? m : null; }
            return null;
        } };
        Crown = new Plugin { Name = "CrownAndConsequences", Handler = (h, a) =>
        {
            if (h == "GetCouncilSeat") { string s; return Seat.TryGetValue((ulong)a[0], out s) ? s : null; }
            if (h == "IsSwornToCrown") return Sworn.Contains((string)a[0]);
            return null;
        } };

        // Cast. Varrow holds the crown; Ashgrove is sworn; Dunmere is not.
        var king = Mk(1, "Aldric", "Varrow"); Leader["Varrow"] = 1;
        var keeper = Mk(2, "Brannoc", "Varrow");
        var ash = Mk(3, "Cerys", "Ashgrove"); Leader["Ashgrove"] = 3;
        var ash2 = Mk(4, "Dafydd", "Ashgrove");
        var dun = Mk(5, "Elowen", "Dunmere"); Leader["Dunmere"] = 5;
        var merc = Mk(6, "Fenwick", null);
        var admin = Mk(7, "Steward", null);
        Sworn.Add("Varrow"); Sworn.Add("Ashgrove");
        Kings.King = 1;
        Seat[2] = "Keeper of Coin";

        P = NewPlugin(ZeroCooldownConfig());
        P.permission.Grants.Add("7|realmtreasury.admin");
        Ok(Audit().Count == 0, "fresh load: audit balanced");
        Ok(EventManager.Count<ItemPassEvent>() == 1, "subscribed to the game's tax ItemPassEvent");
        Ok(File.Exists(Interface.Oxide.DataFileSystem.P("RealmTreasury_lastgood")), "last-good backup written after a clean load");

        foreach (var p in new[] { king, keeper, ash, ash2, dun, merc }) { Give(p, "Wood", 200); Give(p, "Stone", 200); Give(p, "Iron Ingot", 60); }

        // ---- help
        Cmd(merc, "economy", "");
        Ok(Said(merc, "/market sell") && Said(merc, "/vault deposit") && Said(merc, "/treasury mint") && !Said(merc, "Admin:"), "/economy shows help (no admin lines for players)");
        Cmd(admin, "economy", "");
        Ok(Said(admin, "Admin:"), "/economy shows admin lines to admins");
        Cmd(merc, "treasury", "");
        Ok(Said(merc, "game tax 10%") && Said(merc, "(max 50%)"), "/treasury shows the game tax read from KingsScheme.GetTax and KingsRealm.TaxMaximum", merc.All());

        // ---- mint
        Cmd(keeper, "treasury", "mint 100");
        Ok(Err(keeper) && Lg(Data(), "MarksMinted") == 0, "mint: only the monarch (Keeper of Coin refused)");
        Cmd(king, "treasury", "mint 5000");
        Ok(Err(king) && Lg(Data(), "MarksMinted") == 0, "mint: per-decree cap (1000)");
        Cmd(king, "treasury", "mint 1000");
        Ok(Lg(Data(), "MarksMinted") == 1000 && Lg(Treasury(), "Marks") == 1000, "mint 1000 into the treasury");
        Ok(ChronLog.Any(c => c.StartsWith("decree: The crown strikes 1000")), "mint chronicled (falls back to 'decree' until treasury_mint is registered)");
        Ok(Server.Broadcasts.Any(b => b.Contains("strikes 1000")), "mint broadcast to the realm");
        Cmd(king, "treasury", "mint 1000");
        Cmd(king, "treasury", "mint 1");
        Ok(Lg(Data(), "MarksMinted") == 2000 && Err(king), "mint: rolling daily cap (2000)");
        Ok(ZeroSum() == "", "zero-sum after minting", ZeroSum());

        // ---- grants and purses
        Cmd(merc, "treasury", "grant Fenwick 100");
        Ok(Err(merc) && Purse(merc) == 0, "grant: refused to a non-treasurer");
        Cmd(keeper, "treasury", "grant Fenwick 300");
        Ok(Purse(merc) == 300 && Lg(Treasury(), "Marks") == 1700, "grant: Keeper of Coin grants 300 marks");
        Cmd(king, "treasury", "grant Cerys 400");
        Cmd(king, "treasury", "grant Elowen 400");
        Cmd(king, "treasury", "grant Dafydd 400");
        Ok(Purse(ash) == 400 && Purse(dun) == 400 && Purse(ash2) == 400, "grant: monarch grants");
        Cmd(king, "treasury", "grant Brannoc 4000");
        Ok(Err(king) && Purse(keeper) == 0, "grant: more than the treasury holds is refused");
        Cmd(merc, "purse", "pay Cerys 50");
        Ok(Purse(merc) == 250 && Purse(ash) == 450, "/purse pay moves marks between purses");
        Cmd(merc, "purse", "pay Cerys 999");
        Ok(Err(merc) && Purse(merc) == 250, "/purse pay: cannot pay more than you hold");
        Cmd(merc, "purse", "pay Fenwick 5");
        Ok(Err(merc), "/purse pay: not to yourself");

        // ---- market: ask
        Cmd(dun, "market", "sell 20 3 Wood");
        int ask = LastId();
        Ok(Has(dun, "Wood") == 180 && (int)F(LastListing(), "Remaining") == 20, "sell: 20 Wood measured into escrow");
        Ok(ZeroSum() == "", "zero-sum after posting an ask", ZeroSum());
        Cmd(dun, "market", "buy " + ask + " 5");
        Ok(Err(dun) && Has(dun, "Wood") == 180, "buy: not your own order");
        Cmd(merc, "market", "buy " + ask + " 5");
        Ok(Has(merc, "Wood") == 205 && Purse(merc) == 235 && (int)F(LastListing(), "Remaining") == 15, "buy 5 @3: items delivered, 15 marks paid", merc.All());
        long fee0 = Lg(Treasury(), "Marks");
        Ok(Purse(dun) == 400 + 15, "seller credited (fee 2% of 15 rounds to 0)");
        Cmd(merc, "market", "list Wood");
        Ok(Said(merc, "#" + ask + " SELL 15 Wood @ 3"), "/market list shows the ask", merc.All());
        Cmd(merc, "market", "history Wood");
        Ok(Said(merc, "Wood: last 3") && Said(merc, "1 trades"), "/market history records the trade", merc.All());

        // full inventory -> owed -> collect
        merc.Inventory.Contents.Capacity = merc.Inventory.Contents.Counts.Values.Sum() + 4;
        Cmd(merc, "market", "buy " + ask + " 10");
        Ok(Has(merc, "Wood") == 209 && L("Owed").Count == 1 && (int)F(L("Owed")[0], "Amount") == 6, "buy into a full pack: 4 delivered, 6 owed (measured)");
        Ok(ZeroSum() == "", "zero-sum with an owed balance", ZeroSum());
        merc.Inventory.Contents.Capacity = 100000;
        Cmd(merc, "market", "collect");
        Ok(Has(merc, "Wood") == 215 && L("Owed").Count == 0, "/market collect pays the rest");
        Cmd(merc, "market", "collect");
        Ok(Said(merc, "Nothing is owed"), "/market collect with nothing owed");

        // big order paid in stack-limited chunks
        Give(ash, "Wood", 0);
        Cmd(ash, "market", "sell 120 1 Stone");
        int stoneAsk = LastId();
        merc.Inventory.Contents.MergeCalls = 0;
        Cmd(merc, "market", "buy " + stoneAsk);
        Ok(Has(merc, "Stone") == 320 && merc.Inventory.Contents.MergeCalls == 3, "120 Stone given in 3 chunks of StackLimit 50", "merges=" + merc.Inventory.Contents.MergeCalls);
        Ok((string)F(L("Listings")[L("Listings").Count - 1], "Status") == "done", "sold-out ask closes");

        // ---- market: bid
        Cmd(king, "treasury", "fee 5");
        Ok((int)F(Data(), "MarketFeePercent") == 5, "monarch sets the market fee to 5%");
        Cmd(king, "treasury", "fee 50");
        Ok(Err(king) && (int)F(Data(), "MarketFeePercent") == 5, "market fee above the ceiling refused");
        Cmd(ash2, "market", "bid 10 20 Iron Ingot");
        int bid = LastId();
        Ok(Purse(ash2) == 200 && Lg(LastListing(), "EscrowMarks") == 200, "bid: 10 x 20 marks escrowed");
        Cmd(ash2, "market", "bid 100 20 Iron Ingot");
        Ok(Err(ash2), "bid: refused beyond the purse");
        long dunBefore = Purse(dun);
        Cmd(dun, "market", "fill " + bid + " 4");
        long treasuryBefore = Lg(Treasury(), "Marks");
        Ok(Has(dun, "Iron Ingot") == 56 && Has(ash2, "Iron Ingot") == 64, "fill 4: measured from the seller, delivered to the bidder");
        Ok(Purse(dun) == dunBefore + 80 - 4, "seller paid 80 minus 5% fee", "purse=" + Purse(dun));
        Ok(Lg(LastListing(), "EscrowMarks") == 120 && (int)F(LastListing(), "Remaining") == 6, "bid escrow reduced by exactly what was paid");
        Ok(ZeroSum() == "", "zero-sum after a fill with fee", ZeroSum());
        Cmd(dun, "market", "buy " + bid);
        Ok(Err(dun) && Said(dun, "fill"), "buy on a bid points to /market fill");
        Cmd(merc, "market", "fill " + bid + " 7");
        Ok(Err(merc), "fill more than the bid wants is refused");
        Cmd(ash2, "market", "cancel " + bid);
        Ok(Purse(ash2) == 320 && (string)F(LastListing(), "Status") == "cancelled" && Lg(LastListing(), "EscrowMarks") == 0, "cancel bid: escrow returned");
        Cmd(dun, "market", "cancel " + ask);
        Ok(!Err(dun) && Has(dun, "Wood") == 185, "cancel an ask: the 5 unsold Wood come back", dun.All());
        Cmd(dun, "market", "cancel " + ask);
        Ok(Err(dun), "cancel a closed order: not open");

        // partial take fault: AutoSplit removes fewer than asked
        dun.Inventory.Contents.SplitShortBy = 3;
        int listingsBefore = L("Listings").Count;
        Cmd(dun, "market", "sell 10 2 Stone");
        dun.Inventory.Contents.SplitShortBy = 0;
        Ok(Err(dun) && Said(dun, "could not take"), "partial take: nothing posted");
        Ok(L("Listings").Count == listingsBefore && Has(dun, "Stone") == 200, "partial take: the 7 units taken are returned at once", "stone=" + Has(dun, "Stone"));
        Ok(ZeroSum() == "", "zero-sum after a failed partial take", ZeroSum());

        // expiry
        Cmd(dun, "market", "sell 30 4 Stone 2h");
        var exp = LastListing();
        Ok(Has(dun, "Stone") == 170 && ((DateTime)F(exp, "ExpiresAt") - DateTime.UtcNow).TotalHours < 2.1, "sell with a 2h expiry");
        SetF(exp, "ExpiresAt", DateTime.UtcNow.AddMinutes(-1));
        Inv(P, "Tick");
        Ok((string)F(exp, "Status") == "expired" && Has(dun, "Stone") == 200, "expired ask returns its goods on tick");
        Cmd(dun, "market", "sell 1 4 Stone 999h");
        Ok(Err(dun), "listing hours above MaxListingHours refused");
        Cmd(dun, "market", "sell 1 4 Unobtainium");
        Ok(Err(dun) && Said(dun, "No item"), "unknown item refused");

        // ---- vaults
        Cmd(ash2, "vault", "deposit 50 Wood");
        object av = House("Ashgrove");
        Ok(av != null && VaultItem(av, "Wood") == 50 && Has(ash2, "Wood") == 150, "member deposits 50 Wood into House Ashgrove's vault");
        Cmd(ash2, "vault", "withdraw 10 Wood");
        Ok(Err(ash2) && VaultItem(av, "Wood") == 50, "non-steward cannot withdraw");
        Cmd(ash, "vault", "withdraw 10 Wood");
        Ok(VaultItem(av, "Wood") == 40 && Has(ash, "Wood") == 210, "head of house withdraws 10");
        Cmd(ash, "vault", "steward add Dafydd");
        Cmd(ash2, "vault", "withdraw 5 Wood");
        Ok(VaultItem(av, "Wood") == 35 && Has(ash2, "Wood") == 155, "named steward may withdraw");
        Cmd(ash, "vault", "steward add Elowen");
        Ok(Err(ash), "steward must be of the house");
        Cmd(ash, "vault", "steward remove Dafydd");
        Cmd(ash2, "vault", "withdraw 5 Wood");
        Ok(Err(ash2), "removed steward cannot withdraw");
        Cmd(merc, "vault", "deposit 5 Wood");
        Ok(Err(merc) && Said(merc, "must belong"), "houseless player has no vault");
        Cmd(ash2, "vault", "give 100");
        Ok(Lg(av, "Marks") == 100 && Purse(ash2) == 220, "member gives 100 marks to the vault");
        long ashBefore = Purse(ash);
        Cmd(ash, "vault", "take 30");
        Ok(Lg(av, "Marks") == 70 && Purse(ash) == ashBefore + 30, "head takes 30 marks");
        Cmd(dun, "vault", "Ashgrove");
        Ok(Said(dun, "House Ashgrove: 70 marks") && Said(dun, "35 Wood"), "vaults are public to read", dun.All());
        // capacity
        SetF(Cfg(), "VaultMaxUnitsPerItem", 40);
        Cmd(ash2, "vault", "deposit 10 Wood");
        Ok(Err(ash2) && VaultItem(av, "Wood") == 35 && Has(ash2, "Wood") == 155, "vault capacity checked before anything is taken");
        SetF(Cfg(), "VaultMaxUnitsPerItem", 10000);
        // daily outflow budget
        SetF(Cfg(), "VaultItemsOutPerDay", 25);
        Cmd(ash, "vault", "withdraw 20 Wood");
        Ok(Err(ash) && VaultItem(av, "Wood") == 35, "vault daily outflow budget (10+5 used, 20 more refused)");
        SetF(Cfg(), "VaultItemsOutPerDay", 1000);
        // house market order from the vault
        Cmd(ash, "vault", "sell 10 2 Wood");
        int houseAsk = LastId();
        Ok(VaultItem(av, "Wood") == 25 && (string)F(LastListing(), "OwnerKind") == "house", "head lists vault goods on the market");
        Cmd(ash, "market", "buy " + houseAsk + " 1");
        Ok(Err(ash), "the head cannot buy the house order he posted (self-dealing guard)");
        Cmd(merc, "market", "buy " + houseAsk + " 10");
        Ok(Lg(av, "Marks") == 70 + 20 - 1 && Has(merc, "Wood") == 225, "proceeds (minus 5% fee) go to the house vault", "marks=" + Lg(av, "Marks"));
        Ok(ZeroSum() == "", "zero-sum after vault trading", ZeroSum());

        // ---- treasury deposits, tax and withdrawals
        Cmd(merc, "treasury", "deposit 40 Stone");
        Ok(VaultItem(Treasury(), "Stone") == 40, "anyone may pay tribute into the treasury");
        Cmd(merc, "treasury", "taxin 10 Stone");
        Ok(Err(merc), "taxin is for the crown and treasurer");
        Cmd(king, "treasury", "taxin 25 Wood");
        Ok(VaultItem(Treasury(), "Wood") == 25, "monarch pays emptied tax-chest goods in (measured deposit)");
        EventManager.Raise(new ItemPassEvent { Memo = "Tax", ItemStack = new InvGameItemStack(InvBlueprints.All["Wood"], 7, null) });
        EventManager.Raise(new ItemPassEvent { Memo = "Tax", ItemStack = new InvGameItemStack(InvBlueprints.All["Wood"], 999999, null) });
        EventManager.Raise(new ItemPassEvent { Memo = "Gift", ItemStack = new InvGameItemStack(InvBlueprints.All["Wood"], 50, null) });
        var obs = D("TaxObserved");
        Ok(obs.Contains("Wood") && (long)obs["Wood"] == 1007 && Lg(Data(), "TaxEvents") == 2, "tax observation: counts Memo=Tax only, clamps each event to 1000");
        Ok(VaultItem(Treasury(), "Wood") == 25, "observed tax is never credited (the game keeps it in the tax chest)");
        Cmd(merc, "treasury", "tax");
        Ok(Said(merc, "1007 Wood") && Said(merc, "Paid into the treasury: 25 Wood"), "/treasury tax reports observed vs paid in", merc.All());
        Cmd(keeper, "treasury", "withdraw 15 Stone");
        Ok(VaultItem(Treasury(), "Stone") == 25 && Has(keeper, "Stone") == 215, "Keeper of Coin withdraws");
        Cmd(king, "treasury", "award Fenwick 5 Stone");
        Ok(VaultItem(Treasury(), "Stone") == 20 && Has(merc, "Stone") == 285, "monarch awards goods to a player", "merc stone=" + Has(merc, "Stone"));
        SetF(Cfg(), "TreasuryItemsOutPerDay", 25);
        Cmd(keeper, "treasury", "withdraw 10 Stone");
        Ok(Err(keeper) && VaultItem(Treasury(), "Stone") == 20, "treasury daily outflow budget (15+5 used, 10 more refused)");
        SetF(Cfg(), "TreasuryItemsOutPerDay", 1000);
        Cmd(keeper, "treasury", "sell 10 9 Stone");
        int crownAsk = LastId();
        Cmd(keeper, "market", "buy " + crownAsk + " 1");
        Ok(Err(keeper), "treasurer cannot buy the crown order he posted with his own purse");
        long tb = Lg(Treasury(), "Marks");
        Cmd(dun, "market", "buy " + crownAsk + " 10");
        Ok(Lg(Treasury(), "Marks") == tb + 90, "crown sales pay the treasury in full (no fee on itself)");
        Cmd(king, "treasury", "bid 5 4 Iron Ingot");
        int crownBid = LastId();
        Cmd(dun, "market", "fill " + crownBid);
        Ok(VaultItem(Treasury(), "Iron Ingot") == 5, "crown bid filled: goods land in the treasury");
        Ok(ZeroSum() == "", "zero-sum after crown trading", ZeroSum());

        // ---- tithe
        Cmd(king, "treasury", "levy");
        Ok(Err(king) && Said(king, "0%"), "levy at 0% refused");
        Cmd(king, "treasury", "tithe 25");
        Ok(Err(king) && (int)F(Data(), "TithePercent") == 0, "tithe above the Charter ceiling (10%) refused");
        Cmd(keeper, "treasury", "tithe 5");
        Ok(Err(keeper), "only the monarch sets the tithe");
        Cmd(king, "treasury", "tithe 10");
        Ok((int)F(Data(), "TithePercent") == 10, "monarch sets the tithe to 10%");
        // Varrow (crown house) and Dunmere (not sworn) get vaults too.
        Cmd(keeper, "vault", "deposit 100 Wood");
        Cmd(dun, "vault", "deposit 100 Wood");
        int ashWood = VaultItem(av, "Wood"); long ashMarks = Lg(av, "Marks");
        int tWood = VaultItem(Treasury(), "Wood");
        Cmd(king, "treasury", "levy");
        Ok(VaultItem(av, "Wood") == ashWood - ashWood / 10 && Lg(av, "Marks") == ashMarks - ashMarks / 10, "sworn Ashgrove pays 10% of goods and marks", "wood=" + VaultItem(av, "Wood"));
        Ok(VaultItem(House("Varrow"), "Wood") == 100, "the crown's own house is exempt");
        Ok(VaultItem(House("Dunmere"), "Wood") == 100, "unsworn houses are exempt (TitheOnlySwornHouses)");
        Ok(VaultItem(Treasury(), "Wood") == tWood + ashWood / 10, "tithe lands in the treasury");
        Cmd(king, "treasury", "levy");
        Ok(Err(king) && Said(king, "min"), "levy interval enforced");
        Ok(ZeroSum() == "", "zero-sum after the tithe", ZeroSum());

        // ---- admin freeze, ledger, audit
        Cmd(merc, "treasury", "freeze");
        Ok(Err(merc), "freeze is admin-only");
        Cmd(admin, "treasury", "freeze");
        Cmd(merc, "market", "sell 1 1 Wood");
        Ok(Err(merc) && Said(merc, "closed"), "frozen market refuses orders");
        Cmd(admin, "treasury", "unfreeze");
        Cmd(merc, "treasury", "ledger 5");
        Ok(merc.Messages.Count == 5, "/treasury ledger shows the last entries");
        Ok(P.FileLog.Count > 20 && P.FileLog.All(l => l.StartsWith("ledger: ")), "every ledger entry is written to the log file", "lines=" + P.FileLog.Count);
        Cmd(admin, "treasury", "audit");
        Ok(Said(admin, "accounted for"), "admin audit passes", admin.All());

        // ---- escheat
        Cmd(dun, "vault", "sell 20 3 Wood");
        int dunAsk = LastId();
        Cmd(king, "treasury", "escheat Dunmere");
        Ok(Err(king), "escheat refused while the house stands");
        HouseOf.Remove(5); Leader.Remove("Dunmere");
        int tw = VaultItem(Treasury(), "Wood");
        Cmd(king, "treasury", "escheat Dunmere");
        Ok(House("Dunmere") == null && VaultItem(Treasury(), "Wood") == tw + 100, "fallen house's vault (incl. its cancelled order) passes to the crown");
        Ok((string)L("Listings").Cast<object>().First(l => (int)F(l, "Id") == dunAsk).GetType().GetField("Status", BF).GetValue(L("Listings").Cast<object>().First(l => (int)F(l, "Id") == dunAsk)) == "cancelled", "the fallen house's open order is cancelled");
        Ok(ZeroSum() == "", "zero-sum after escheat", ZeroSum());

        // ---- cooldowns
        SetF(Cfg(), "PostCooldownSeconds", 60);
        Cmd(merc, "market", "sell 1 1 Wood");
        Cmd(merc, "market", "sell 1 1 Wood");
        Ok(Err(merc) && Said(merc, "Wait"), "post cooldown");
        SetF(Cfg(), "PostCooldownSeconds", 0);
        D("Cooldowns").Clear();

        // ---- randomized zero-sum fuzz over every command path
        var rnd = new Random(12345);
        var actors = new[] { king, keeper, ash, ash2, merc };
        HouseOf[5] = "Dunmere"; Leader["Dunmere"] = 5; actors = actors.Concat(new[] { dun }).ToArray();
        SetF(Cfg(), "MaxOpenTotal", 1000); SetF(Cfg(), "MaxOpenPerOwner", 1000);
        string fuzzFail = null;
        long seqBefore = Lg(Data(), "Seq");
        int okSteps = 0;
        for (int i = 0; i < 4000 && fuzzFail == null; i++)
        {
            var a = actors[rnd.Next(actors.Length)];
            string item = Items[rnd.Next(3)];
            int q = rnd.Next(1, 40);
            int price = rnd.Next(1, 30);
            var open = L("Listings").Cast<object>().Where(l => (string)F(l, "Status") == "open").ToList();
            string anyId = open.Count > 0 ? F(open[rnd.Next(open.Count)], "Id").ToString() : "1";
            a.Inventory.Contents.Capacity = rnd.Next(10) == 0 ? a.Inventory.Contents.Counts.Values.Sum() + rnd.Next(5) : 100000;
            a.Inventory.Contents.SplitShortBy = rnd.Next(25) == 0 ? 1 : 0;
            switch (rnd.Next(22))
            {
                case 0: Cmd(a, "market", "sell " + q + " " + price + " " + item); break;
                case 1: Cmd(a, "market", "bid " + q + " " + price + " " + item); break;
                case 2: Cmd(a, "market", "buy " + anyId + " " + q); break;
                case 3: Cmd(a, "market", "fill " + anyId + " " + q); break;
                case 4: Cmd(a, "market", "cancel " + anyId); break;
                case 5: Cmd(a, "market", "collect"); break;
                case 6: Cmd(a, "vault", "deposit " + q + " " + item); break;
                case 7: Cmd(a, "vault", "withdraw " + q + " " + item); break;
                case 8: Cmd(a, "vault", "give " + q); break;
                case 9: Cmd(a, "vault", "take " + q); break;
                case 10: Cmd(a, "vault", "sell " + q + " " + price + " " + item); break;
                case 11: Cmd(a, "vault", "bid " + q + " " + price + " " + item); break;
                case 12: Cmd(a, "treasury", "deposit " + q + " " + item); break;
                case 13: Cmd(a, "treasury", "withdraw " + q + " " + item); break;
                case 14: Cmd(a, "treasury", "sell " + q + " " + price + " " + item); break;
                case 15: Cmd(a, "treasury", "bid " + q + " " + price + " " + item); break;
                case 16: Cmd(a, "treasury", "grant " + actors[rnd.Next(actors.Length)].Name + " " + q); break;
                case 17: Cmd(a, "purse", "pay " + actors[rnd.Next(actors.Length)].Name + " " + q); break;
                case 18: Cmd(a, "treasury", "award " + actors[rnd.Next(actors.Length)].Name + " " + q + " " + item); break;
                case 19:
                    if (open.Count > 0) SetF(open[rnd.Next(open.Count)], "ExpiresAt", DateTime.UtcNow.AddMinutes(-1));
                    a.Inventory.Contents.Capacity = 100000; Inv(P, "Tick"); break;
                case 20: SetF(Data(), "LastLevy", DateTime.MinValue); Cmd(king, "treasury", "levy"); break;
                case 21: SetF(Data(), "LastMint", DateTime.MinValue); L("Spends").Clear(); Cmd(king, "treasury", "mint " + q); break;
            }
            a.Inventory.Contents.SplitShortBy = 0;
            if (!Err(a)) okSteps++;
            string z = ZeroSum();
            if (z != "") fuzzFail = "step " + i + " by " + a.Name + ": " + z + "\n" + a.All();
        }
        Ok(fuzzFail == null, "fuzz: 4000 random operations keep inventories + custody == seeded, and the audit balanced", fuzzFail ?? "");
        Console.WriteLine("     fuzz: " + okSteps + " of 4000 steps succeeded, " + (Lg(Data(), "Seq") - seqBefore) + " ledger entries");
        Ok(okSteps > 1000 && Lg(Data(), "Seq") - seqBefore > 2000, "fuzz exercised real flows, not only refusals");
        foreach (var a in actors) a.Inventory.Contents.Capacity = 100000;
        foreach (var a in actors) Inv(P, "PayOwed", a);
        Ok(L("Owed").Count == 0, "all owed goods are paid out once packs have room");
        int trades = L("Trades").Count;
        Ok(trades > 0 && trades <= 500, "price history kept and capped (" + trades + " trades)");

        // ---- tamper detection
        var tv = (IDictionary)F(Treasury(), "Items");
        tv["Gold Ore"] = 5;
        Ok(Audit().Any(p => p.StartsWith("Gold Ore")), "audit detects goods that appeared from nowhere");
        tv.Remove("Gold Ore");
        SetF(Treasury(), "Marks", Lg(Treasury(), "Marks") + 1);
        Ok(Audit().Any(p => p.StartsWith("marks")), "audit detects marks that appeared from nowhere");
        SetF(Treasury(), "Marks", Lg(Treasury(), "Marks") - 1);

        // ---- persistence: reload from disk
        Inv(P, "Unload");
        Ok(EventManager.Count<ItemPassEvent>() == 0, "unload unsubscribes from the game event");
        long minted = Lg(Data(), "MarksMinted");
        var oldHeld = (IDictionary)Inv(P, "HeldNow");
        P = NewPlugin(ZeroCooldownConfig());
        var newHeld = (IDictionary)Inv(P, "HeldNow");
        Ok(Lg(Data(), "MarksMinted") == minted && Items.All(i => (oldHeld.Contains(i) ? (long)oldHeld[i] : 0) == (newHeld.Contains(i) ? (long)newHeld[i] : 0)), "reload restores every holding");
        Ok(Audit().Count == 0 && ZeroSum() == "", "audit balanced after reload", ZeroSum());
        Ok(!P.Log.Any(l => l.StartsWith("WARN Audit")), "no audit warning on a clean reload");
        Inv(P, "Unload");

        // ---- corruption-safe load
        string file = Interface.Oxide.DataFileSystem.P("RealmTreasury");
        string good = File.ReadAllText(file);
        File.WriteAllText(file, "{ \"Treasury\": { \"Marks\": 12, ");
        int writes = Interface.Oxide.DataFileSystem.Writes;
        bool threw = false;
        RealmTreasury broken = new RealmTreasury();
        try
        {
            broken.Config.Json = ZeroCooldownConfig();
            Inv(broken, "LoadDefaultMessages");
            Inv(broken, "Init");
        }
        catch (TargetInvocationException) { threw = true; }
        Ok(threw, "a corrupt data file stops the plugin from loading");
        Inv(broken, "SaveData");
        Inv(broken, "Unload");
        Ok(File.ReadAllText(file) == "{ \"Treasury\": { \"Marks\": 12, " && Interface.Oxide.DataFileSystem.Writes == writes, "the corrupt file is never overwritten");
        Ok(broken.Log.Any(l => l.StartsWith("ERROR") && l.Contains("RealmTreasury_lastgood")), "the error names the last-good backup");
        File.WriteAllText(file, "null");
        threw = false;
        broken = new RealmTreasury();
        try { broken.Config.Json = ZeroCooldownConfig(); Inv(broken, "LoadDefaultMessages"); Inv(broken, "Init"); }
        catch (TargetInvocationException) { threw = true; }
        Ok(threw && File.ReadAllText(file) == "null", "an existing but empty data file is refused, not replaced");
        File.WriteAllText(file, good);

        // ---- config clamps
        var cfgDoc = System.Text.Json.Nodes.JsonNode.Parse(ZeroCooldownConfig()).AsObject();
        cfgDoc["TitheMaxPercent"] = 90; cfgDoc["MarketFeeMaxPercent"] = -3; cfgDoc["MaxPricePerUnit"] = 0; cfgDoc["MintSupplyCap"] = 5000000000L;
        P = NewPlugin(cfgDoc.ToJsonString());
        Ok((int)F(Cfg(), "TitheMaxPercent") == 50 && (int)F(Cfg(), "MarketFeeMaxPercent") == 0 && (long)F(Cfg(), "MaxPricePerUnit") == 1
            && (long)F(Cfg(), "MintSupplyCap") == 1000000000L, "config values are clamped to safe ranges");
        Ok((int)F(Data(), "MarketFeePercent") == 0, "stored fee is clamped to the configured ceiling");
        Inv(P, "Unload");

        Directory.Delete(Dir, true);
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
