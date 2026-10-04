// Shared test world for RealmCrafts: players with real packs, chests, corpses and dropped item packs, the game's container
// events raised the way a client and the server raise them, crafting stations, creatures, the REAL RealmTreasury holding
// commission marks, and stand-ins for RealmRenown, RealmChronicle, RealmHouses, RealmSeasons, RealmHerald, RealmQuests,
// RealmDominion and RealmSentinel. Used by the logic tests (plugins/docs/RealmCrafts/logic-tests) and the exploit suite
// (tools/exploit-review/crafts).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.AI;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Behaviours;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Containers;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Networking.Events.Item;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);   // a Monday
    public static RealmCrafts C;
    public static RealmTreasury T;
    public static Plugin Renown, Chron, Houses, Seasons, Herald, Quests, Dominion, Sentinel;
    public static Dictionary<string, string> HouseOf = new Dictionary<string, string>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static List<string> Deeds = new List<string>();
    public static List<string> ChronLog = new List<string>();
    public static List<string> Awards = new List<string>();
    public static List<string> QuestReports = new List<string>();
    public static List<string> ItemSources = new List<string>();
    public static List<Dictionary<string, object>> Holdings = new List<Dictionary<string, object>>();
    public static double NextRoll = 0.999;                          // the dice: 0.999 = no extra item unless a test says so
    public static long Seeded;
    public static string Dir;
    public static List<Player> Everyone = new List<Player>();
    public static Player ServerPlayer = new Player(9999999999, "Server") { IsServer = true };

    public static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }
    public static object Inv(object o, string m, params object[] a)
    {
        var mi = o.GetType().GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        try { return mi.Invoke(o, a); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    public static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    public static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    public static object Data() { return F(C, "data"); }
    public static object D(string field) { return F(Data(), field); }
    public static object Cfg(string section) { return F(F(C, "config"), section); }
    public static void Set(string section, string field, object v) { SetF(Cfg(section), field, v); }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static void Clear()
    {
        Server.Broadcasts.Clear(); Deeds.Clear(); ChronLog.Clear(); Awards.Clear(); QuestReports.Clear(); ItemSources.Clear();
        foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); }
    }

    public static void Blueprints()
    {
        InvBlueprints.All.Clear();
        InvBlueprints.ResourceNames.Clear();
        foreach (var n in new[] { "Wood", "Stone", "Iron", "Clay", "Flax", "Berry", "Cabbage", "Carrot", "Raw Meat", "Fat", "Bone", "Leather Hide", "Wolf Pelt",
            "Iron Sword", "Iron Ingot", "Steel Sword", "Wooden Bow", "Wooden Chair", "Leather Helmet", "Bandage", "Cooked Meat", "Bread", "Flour",
            "Stone Hatchet", "Torch", "Lumber", "Strange Trinket", "Apple", "Grain" })
            InvBlueprints.Add(n);
        InvBlueprints.Add("Arrow", 100);
        InvBlueprints.ResourceNames[CodeHatch.ResourceType.IronOre] = "Iron";
        InvBlueprints.ResourceNames[CodeHatch.ResourceType.RawMeat] = "Raw Meat";
        InvBlueprints.ResourceNames[CodeHatch.ResourceType.LeatherHide] = "Leather Hide";
        InvBlueprints.ResourceNames[CodeHatch.ResourceType.WolfPelt] = "Wolf Pelt";
        InvBlueprints.ResourceNames[CodeHatch.ResourceType.CookedMeat] = "Cooked Meat";
    }

    public static void Reset(Action<object> tweak = null)
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        HouseOf.Clear(); PopupsOff.Clear(); Holdings.Clear();
        NextRoll = 0.999; Seeded = 0;
        PlayerExtensions.PopupsFail = false;
        ItemCollection.Capacity = int.MaxValue;
        EventManager.Subs.Clear();
        Clear();
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        Blueprints();
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmcrafts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Renown = new Plugin { Name = "RealmRenown", Handler = (h, a) => { if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; } };
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) => { if (h != "Log") return null; ChronLog.Add(a[0] + "|" + a[1] + "|" + a[2]); return ChronLog.Count; } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (h == "GetHouse") { string v; return HouseOf.TryGetValue((string)a[0], out v) ? v : null; }
            if (h == "GetHouseFounded") return HouseOf.ContainsValue((string)a[0]) ? "2026-01-01" : null;
            if (h == "GetMembers") return HouseOf.Where(kv => kv.Value == (string)a[0]).Select(kv => kv.Key).ToList();
            return null;
        } };
        Seasons = new Plugin { Name = "RealmSeasons", Handler = (h, a) => { if (h == "AwardHouse") { Awards.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; } return null; } };
        Herald = new Plugin { Name = "RealmHerald", Handler = (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null };
        Quests = new Plugin { Name = "RealmQuests", Handler = (h, a) => { if (h == "ReportQuestEvent") { QuestReports.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; } };
        Dominion = new Plugin { Name = "RealmDominion", Handler = (h, a) => h == "GetHoldings" ? Holdings : null };
        Sentinel = new Plugin { Name = "RealmSentinel", Handler = (h, a) => { if (h == "SentinelItemSource") ItemSources.Add(Convert.ToString(a[0])); return null; } };
        T = NewTreasury();
        C = NewCrafts(tweak);
    }

    public static RealmTreasury NewTreasury()
    {
        var t = new RealmTreasury();
        t.Name = "RealmTreasury";
        Inv(t, "LoadDefaultConfig");
        Inv(t, "LoadDefaultMessages");
        SetF(t, "RealmChronicle", Chron); SetF(t, "RealmHouses", Houses);
        Inv(t, "Init");
        Inv(t, "OnServerInitialized");
        return t;
    }

    public static RealmCrafts NewCrafts(Action<object> tweak = null, bool treasury = true)
    {
        EventManager.Subs.Clear();                                   // a reload unsubscribes; a fresh world has none left
        var p = new RealmCrafts();
        p.Name = "RealmCrafts";
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmCrafts).GetNestedType("PluginConfig", BF);
            var obj = System.Text.Json.JsonSerializer.Deserialize(p.Config.Json, cfg, DataFileSystem.Opts);
            tweak(obj);
            p.Config.WriteObject(obj, true);
        }
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmTreasury", treasury ? (Plugin)T : null);
        SetF(p, "RealmRenown", Renown); SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "RealmSeasons", Seasons);
        SetF(p, "RealmHerald", Herald); SetF(p, "RealmQuests", Quests); SetF(p, "RealmDominion", Dominion); SetF(p, "RealmSentinel", Sentinel);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        SetF(p, "roll", (Func<double>)(() => NextRoll));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        if (T != null) SetF(T, "RealmCrafts", p);
        C = p;
        return p;
    }

    // A section of the config object of a tweak (for NewCrafts(tweak)).
    public static object Sec(object cfg, string section) { return cfg.GetType().GetField(section, BF).GetValue(cfg); }
    public static void Tw(object cfg, string section, string field, object v) { var s = Sec(cfg, section); s.GetType().GetField(field, BF).SetValue(s, v); }

    // A player, online, with `marks` in their purse, an address and maybe a house.
    public static Player Mk(ulong id, string name, string house = null, string ip = null, long marks = 1000)
    {
        var p = new Player(id, name);
        if (ip != null) p.Connection.IpAddress = ip; else p.Connection.IpAddress = "10.1." + (id % 250) + "." + (id % 7);
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        if (house != null) HouseOf[id.ToString()] = house;
        if (marks > 0) Give(p, marks);
        Inv(C, "OnPlayerConnected", p);
        return p;
    }

    // New marks for a player, minted in the treasury's own books so its audit stays balanced.
    public static void Give(Player p, long marks)
    {
        var purses = (IDictionary)F(F(T, "data"), "Purses");
        string k = p.Id.ToString();
        purses[k] = (purses.Contains(k) ? (long)purses[k] : 0) + marks;
        var td = F(T, "data");
        SetF(td, "MarksMinted", (long)F(td, "MarksMinted") + marks);
        Seeded += marks;
    }
    public static long Purse(Player p) { return (long)Inv(T, "GetPurse", p.Id.ToString()); }
    public static long TreasuryMarks() { return (long)Inv(T, "GetTreasuryMarks"); }
    public static long Held() { var h = (IDictionary)F(F(T, "data"), "Holds"); long n = 0; foreach (var v in h.Values) n += (long)F(v, "Marks"); return n; }
    public static int Holds() { return ((IDictionary)F(F(T, "data"), "Holds")).Count; }
    public static List<string> Audit() { return (List<string>)Inv(T, "Audit"); }
    // Every mark the tests gave out is in a purse, a hold or the crown's treasury (fees), and the treasury's audit agrees.
    public static string ZeroSum()
    {
        var bad = new List<string>(Audit());
        long purses = 0;
        foreach (var v in ((IDictionary)F(F(T, "data"), "Purses")).Values) purses += (long)v;
        long minted = (long)F(F(T, "data"), "MarksMinted");
        if (purses + Held() + TreasuryMarks() != minted) bad.Add("purses " + purses + " + held " + Held() + " + treasury " + TreasuryMarks() + " != minted " + minted);
        return string.Join("; ", bad);
    }

    public static void Admin(Player p) { C.permission.Grants.Add(p.Id + "|realmcrafts.admin"); }
    public static void Offline(Player p) { Server.ClientPlayers.Remove(p); Inv(C, "OnPlayerDisconnected", p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(C, "OnPlayerConnected", p); }

    public static void Cmd(Player p, params string[] args) { Inv(C, "CmdCraft", p, "craft", args); }

    // Time passes; the tick runs every 5 s and the treasury's every 10 s.
    public static void Tick(int seconds = 5)
    {
        for (int i = 0; i < seconds; i += 5)
        {
            Clock = Clock.AddSeconds(5);
            Inv(C, "SafeTick");
            if (Clock.Second % 10 == 0) Inv(T, "Tick");
        }
    }
    public static void Advance(TimeSpan by) { DateTime until = Clock + by; while (Clock < until) { Clock = Clock.AddSeconds(Math.Min(600, (until - Clock).TotalSeconds)); Inv(C, "SafeTick"); Inv(T, "Tick"); } }

    // ---- Items ----------------------------------------------------------------------------------------------------
    public static InvItemBlueprint Bp(string name) { return InvBlueprints.All[name]; }
    public static int Has(Player p, string item) { return ItemCollection.AutoCount(p.Inventory.Contents, Bp(item)); }
    public static int In(Container c, string item) { return ItemCollection.AutoCount(c.Contents, Bp(item)); }
    // Goods the server puts into someone's packs (no client event): a gift, a kit, an admin /give.
    public static void Stock(Player p, string item, int n) { ItemCollection.AutoMergeAdd(p.Inventory.Contents, new InvGameItemStack(Bp(item), n, null)); }
    public static Container Chest(string label = "Chest") { return new Container { Entity = new Entity { IsPlayer = false, Label = label }, Name = label }; }
    public static LootableCreatureContainer Corpse(string label, params (string, int)[] goods)
    {
        var c = new LootableCreatureContainer { Entity = new Entity { IsPlayer = false, Label = label }, Name = label };
        foreach (var g in goods) c.Contents.Put(new InvGameItemStack(Bp(g.Item1), g.Item2, null));
        return c;
    }
    static InvGameItemStack StackIn(Container c, string item) { return c.Contents.Items.FirstOrDefault(s => s.Blueprint.Name == item); }

    // The client gathers: a brand new stack, put into the player's own packs (a new slot).
    public static ContainerItemAddEvent Gather(Player p, string item, int n, Player sender = null)
    {
        var s = new InvGameItemStack(Bp(item), n, null);
        var e = new ContainerItemAddEvent { Sender = sender ?? p, Entity = p.Entity, Container = p.Inventory, ItemStack = s };
        EventManager.Raise(e, () => p.Inventory.Contents.Put(s));
        return e;
    }
    // The client gathers onto a stack it already carries: a merge whose source is in no collection.
    public static ContainerItemMergeEvent GatherMerge(Player p, string item, int n)
    {
        var target = StackIn(p.Inventory, item);
        if (target == null) { Gather(p, item, n); return null; }
        var s = new InvGameItemStack(Bp(item), n, null);
        var e = new ContainerItemMergeEvent { Sender = p, Entity = p.Entity, Container = p.Inventory, ItemStack = s, TargetStack = target, Quantity = n };
        EventManager.Raise(e, () => { target.StackAmount += s.StackAmount; s.StackAmount = 0; });
        return e;
    }
    // The client takes a whole stack out of a container (Remove) and puts it into another (Add).
    public static void Move(Player p, Container from, Container to, string item)
    {
        var s = StackIn(from, item);
        if (s == null) return;
        var r = new ContainerItemRemoveEvent { Sender = p, Entity = from.Entity, Container = from, ItemStack = s };
        EventManager.Raise(r, () => from.Contents.Take(s));
        var a = new ContainerItemAddEvent { Sender = p, Entity = to.Entity, Container = to, ItemStack = s };
        EventManager.Raise(a, () => to.Contents.Put(s));
    }
    // The client splits n off a stack in a container (Split) and puts the new half into another (Add).
    public static void SplitMove(Player p, Container from, Container to, string item, int n)
    {
        var s = StackIn(from, item);
        if (s == null) return;
        var half = new InvGameItemStack(Bp(item), n, null);
        var sp = new ContainerItemSplitEvent { Sender = p, Entity = from.Entity, Container = from, ItemStack = s, Quantity = n, ResultStack = half };
        EventManager.Raise(sp, () => { s.StackAmount -= n; if (s.StackAmount <= 0) from.Contents.Take(s); });
        var a = new ContainerItemAddEvent { Sender = p, Entity = to.Entity, Container = to, ItemStack = half };
        EventManager.Raise(a, () => to.Contents.Put(half));
    }
    // The client drags a stack from a container straight onto one in its packs (a merge whose source is in the container).
    public static void MergeFrom(Player p, Container from, string item, int n)
    {
        var s = StackIn(from, item);
        var target = StackIn(p.Inventory, item);
        if (s == null || target == null) return;
        var e = new ContainerItemMergeEvent { Sender = p, Entity = p.Entity, Container = p.Inventory, ItemStack = s, TargetStack = target, Quantity = n };
        EventManager.Raise(e, () => { int q = Math.Min(n, s.StackAmount); target.StackAmount += q; s.StackAmount -= q; if (s.StackAmount <= 0) from.Contents.Take(s); });
    }
    // Dropping: the server takes the stack out of the packs (no client event) and puts it in an item pack on the ground.
    public static Container Drop(Player p, string item)
    {
        var pack = Chest("Item Pack");
        int n = 0;
        foreach (var s in p.Inventory.Contents.Items.Where(x => x.Blueprint.Name == item).ToList()) { p.Inventory.Contents.Take(s); n += s.StackAmount; }
        if (n > 0) pack.Contents.Put(new InvGameItemStack(Bp(item), n, null));
        return pack;
    }
    // The server hands a stack over (salvage, a broken crate, a farm's harvest): the client then puts it away.
    public static void Pass(Player p, string item, int n, string memo = "Loot")
    {
        var s = new InvGameItemStack(Bp(item), n, null);
        var e = new ItemPassEvent { Sender = ServerPlayer, IsSender = true, Recipient = p, Memo = memo, ItemStack = s };
        EventManager.Raise(e, null);
        var a = new ContainerItemAddEvent { Sender = p, Entity = p.Entity, Container = p.Inventory, ItemStack = s };
        EventManager.Raise(a, () => p.Inventory.Contents.Put(s));
    }
    // A server-side change to the packs that still raises an event (a give with broadcast): the server is the sender.
    public static void ServerAdd(Player p, string item, int n)
    {
        var s = new InvGameItemStack(Bp(item), n, null);
        var a = new ContainerItemAddEvent { Sender = ServerPlayer, IsSender = true, Entity = p.Entity, Container = p.Inventory, ItemStack = s };
        EventManager.Raise(a, () => p.Inventory.Contents.Put(s));
    }

    // ---- Crafting and hunting -------------------------------------------------------------------------------------
    public static ItemCrafter Station(string label = "Smithy") { return new ItemCrafter { Label = label }; }
    public static void Craft(Player p, string product, int n = 1, ItemCrafter at = null)
    {
        at = at ?? Station();
        var req = new ItemCrafterCraftEvent { Sender = p, Crafter = at, Product = Bp(product), Quantity = 1 };
        EventManager.Raise(req, null);
        var made = new ItemCrafterItemEvent { Crafter = at, Stack = new InvGameItemStack(Bp(product), n, null), Cycles = 1 };
        EventManager.Raise(made, null);
        Inv(C, "OnItemCrafted", new ItemCrafterFinishEvent { Crafter = at });
    }
    public static void Kill(Player p, string creature)
    {
        var beast = new Entity { IsPlayer = false, Label = creature + "(Clone)" };
        beast.Components.Add(new MonsterMotor());
        Inv(C, "OnEntityDeath", new EntityDeathEvent { Entity = beast, KillingDamage = new Damage { Amount = 50, DamageSource = p.Entity } });
    }

    // ---- Reading the plugin ---------------------------------------------------------------------------------------
    public static object Rec(Player p) { return Inv(C, "Rec", p.Id.ToString(), null, false); }
    public static object Prof(Player p, string prof)
    {
        var r = Rec(p);
        if (r == null) return null;
        var d = (IDictionary)F(r, "Profs");
        return d.Contains(prof) ? d[prof] : null;
    }
    public static double Xp(Player p, string prof) { var x = Prof(p, prof); return x == null ? 0 : (double)F(x, "Xp"); }
    public static int Level(Player p, string prof) { var x = Prof(p, prof); return x == null ? 1 : (int)F(x, "Level"); }
    public static double Today(Player p, string prof) { var x = Prof(p, prof); return x == null ? 0 : (double)F(x, "Today"); }
    public static double WeekXp(Player p) { var r = Rec(p); return r == null ? 0 : (double)F(r, "WeekXp"); }
    public static void SetXp(Player p, string prof, double xp)
    {
        Inv(C, "Rec", p.Id.ToString(), p.Name, true);
        var r = Rec(p);
        var pr = Inv(C, "ProfOf", r, prof);
        SetF(pr, "Xp", xp);
        SetF(pr, "Level", (int)Inv(C, "LevelFor", xp));
    }
    public static long XpFor(int level) { return (long)Inv(C, "XpFor", level); }
    public static IList Orders() { return (IList)D("Commissions"); }
    public static object Commission(int id) { foreach (var c in Orders()) if ((int)F(c, "Id") == id) return c; return null; }
    public static string Status(int id) { var c = Commission(id); return c == null ? null : (string)F(c, "Status"); }
    public static int Owed(Player p, string item) { var r = Rec(p); if (r == null) return 0; int n = 0; foreach (var o in (IList)F(r, "Owed")) if ((string)F(o, "Item") == item) n += (int)F(o, "Amount"); return n; }
    public static object House(string name) { var d = (IDictionary)D("Houses"); string k = name.ToLowerInvariant(); return d.Contains(k) ? d[k] : null; }
    public static double HouseWeek(string name) { var h = House(name); return h == null ? 0 : (double)F(h, "WeekXp"); }
    // Make a player a member of long standing of their house (past MinMemberHours).
    public static void Settle(Player p) { var r = Inv(C, "Rec", p.Id.ToString(), p.Name, true); Inv(C, "TrackHouse", p.Id.ToString(), r); SetF(r, "HouseSince", Clock.AddDays(-10)); }
}
