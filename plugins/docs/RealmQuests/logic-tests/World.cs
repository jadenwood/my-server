// Shared test world for RealmQuests: players with packs, the shipped content copied into a temporary oxide/data, and
// stand-ins for RealmTreasury, RealmRenown, RealmHouses, RealmSeasons, RealmChronicle, RealmHerald, RealmEvents,
// RealmWarden and RealmSentinel answering the way their real non-public methods do. Used by the logic tests
// (plugins/docs/RealmQuests/logic-tests) and the exploit suite (tools/exploit-review/quests).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.AI;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Behaviours;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock;                                       // Monday 2026-10-05 12:00 UTC
    public static RealmQuests Q;
    public static string Repo, Dir;
    public static Plugin Treasury, Renown, Houses, Seasons, Chron, Herald, Events, Warden, Sentinel;
    public static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    public static Dictionary<string, string> Liege = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Treaties = new HashSet<string>();
    public static HashSet<ulong> Leaders = new HashSet<ulong>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static Dictionary<string, long> Purse = new Dictionary<string, long>();
    public static long TreasuryLeft;                                     // what RewardMarks will still pay
    public static bool TreasuryThrows;
    public static List<string> RewardCalls = new List<string>();
    public static List<string> Deeds = new List<string>();
    public static Dictionary<string, int> RenownOf = new Dictionary<string, int>();
    public static Dictionary<string, string[]> TitlesOf = new Dictionary<string, string[]>();
    public static int SeasonNumber;
    public static List<string> SeasonAwards = new List<string>();
    public static List<string> ChronLog = new List<string>();
    public static List<string> ActiveEvents = new List<string>();
    public static HashSet<ulong> Protected = new HashSet<ulong>();
    public static List<string> SentinelCalls = new List<string>();
    public static List<Player> Everyone = new List<Player>();

    public static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }
    public static object Inv(object o, string m, params object[] a)
    {
        var t = o as Type ?? o.GetType();
        var mi = t.GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        try { return mi.Invoke(o is Type ? null : o, a); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    public static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    public static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    public static void Cfg(string field, object v) { SetF(F(Q, "config"), field, v); }
    public static object Data { get { return F(Q, "data"); } }

    public static void Reset(bool content = true)
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        HouseOf.Clear(); Liege.Clear(); Treaties.Clear(); Leaders.Clear(); PopupsOff.Clear(); Purse.Clear(); RewardCalls.Clear(); Deeds.Clear();
        RenownOf.Clear(); TitlesOf.Clear(); SeasonAwards.Clear(); ChronLog.Clear(); ActiveEvents.Clear(); Protected.Clear(); SentinelCalls.Clear();
        EventManager.Subs.Clear();
        TreasuryLeft = 1000000; TreasuryThrows = false; SeasonNumber = 1;
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmquests-" + Guid.NewGuid().ToString("N")); }
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        Directory.CreateDirectory(Path.Combine(Dir, "RealmQuests"));
        Interface.Oxide.DataFileSystem.Dir = Dir;
        if (content)
            foreach (var f in Directory.GetFiles(Path.Combine(Repo, "plugins", "docs", "RealmQuests", "content"), "*.json"))
                File.Copy(f, Path.Combine(Dir, "RealmQuests", Path.GetFileName(f)));
        Treasury = new Plugin { Name = "RealmTreasury", Handler = (h, a) =>
        {
            if (h == "GetPurse") { long v; return Purse.TryGetValue((string)a[0], out v) ? v : 0L; }
            if (h == "RewardMarks")
            {
                if (TreasuryThrows) throw new InvalidOperationException("treasury down");
                long want = (long)a[2];
                long n = Math.Max(0, Math.Min(want, TreasuryLeft));
                TreasuryLeft -= n;
                long v; Purse.TryGetValue((string)a[0], out v); Purse[(string)a[0]] = v + n;
                RewardCalls.Add(a[0] + "|" + want + "|" + n);
                return n;
            }
            return null;
        } };
        Renown = new Plugin { Name = "RealmRenown", Handler = (h, a) =>
        {
            if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; }
            if (h == "GetRenown") { int v; return RenownOf.TryGetValue((string)a[0], out v) ? v : 0; }
            if (h == "GetTitles") { string[] v; return TitlesOf.TryGetValue((string)a[0], out v) ? v : new string[0]; }
            return null;
        } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (h == "GetHouse") { string v; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out v) ? v : null; }
            if (h == "GetLiege") { string v; return Liege.TryGetValue((string)a[0], out v) ? v : null; }
            if (h == "HasTreaty") return Treaties.Contains(Pair((string)a[0], (string)a[1]));
            if (h == "GetMembers") { var m = HouseOf.Where(kv => string.Equals(kv.Value, (string)a[0], StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.ToString()).ToList(); return m.Count > 0 ? m : null; }
            if (h == "GetHouseLeader") { var l = HouseOf.Where(kv => string.Equals(kv.Value, (string)a[0], StringComparison.OrdinalIgnoreCase) && Leaders.Contains(kv.Key)).Select(kv => kv.Key.ToString()).FirstOrDefault(); return l; }
            if (h == "GetHouseSummaries")
                return HouseOf.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => { string l; Liege.TryGetValue(n, out l); return new Dictionary<string, object> { { "name", n }, { "liege", l }, { "members", HouseOf.Values.Count(v => v == n) } }; }).ToList();
            return null;
        } };
        Seasons = new Plugin { Name = "RealmSeasons", Handler = (h, a) =>
        {
            if (h == "GetSeasonNumber") return SeasonNumber;
            if (h == "AwardHouse") { SeasonAwards.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; }
            return null;
        } };
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) => { if (h == "Log") { ChronLog.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + string.Join(";", (string[])a[3])); return ChronLog.Count; } return null; } };
        Herald = new Plugin { Name = "RealmHerald", Handler = (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null };
        Events = new Plugin { Name = "RealmEvents", Handler = (h, a) => h == "GetActiveEvents" ? ActiveEvents.ToArray() : null };
        Warden = new Plugin { Name = "RealmWarden", Handler = (h, a) => h == "IsNewPlayerProtected" ? (object)Protected.Contains((ulong)a[0]) : null };
        Sentinel = new Plugin { Name = "RealmSentinel", Handler = (h, a) => { if (h == "SentinelItemSource") SentinelCalls.Add(a[0] + "|" + a[1]); return null; } };
    }

    public static string Pair(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a + "|" + b).ToLowerInvariant() : (b + "|" + a).ToLowerInvariant(); }

    public static RealmQuests NewQuests(Action<object> tweak = null)
    {
        var p = new RealmQuests();
        Inv(p, "LoadDefaultConfig");
        var cfgType = typeof(RealmQuests).GetNestedType("PluginConfig", BF);
        var obj = System.Text.Json.JsonSerializer.Deserialize(p.Config.Json, cfgType, DataFileSystem.Opts);
        // Test defaults: no wait for new accounts, so rewards can be followed; tests that need the gate set it.
        cfgType.GetField("MinActiveMinutesForMarks").SetValue(obj, 0);
        if (tweak != null) tweak(obj);
        p.Config.WriteObject(obj, true);
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmTreasury", Treasury); SetF(p, "RealmRenown", Renown); SetF(p, "RealmHouses", Houses); SetF(p, "RealmSeasons", Seasons);
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHerald", Herald); SetF(p, "RealmEvents", Events); SetF(p, "RealmWarden", Warden);
        SetF(p, "RealmSentinel", Sentinel);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        // A fixed salt on a fresh record, so the seeded draws are the same on every run of the tests.
        var data = F(p, "data");
        if (data != null && ((IDictionary)F(data, "Players")).Count == 0) SetF(data, "Salt", "realm-tests");
        Inv(p, "OnServerInitialized");
        Q = p;
        return p;
    }
    public static void Reload(Action<object> tweak = null) { Inv(Q, "Unload"); NewQuests(tweak); foreach (var p in Server.ClientPlayers.ToList()) Inv(Q, "OnPlayerConnected", p); }
    public static void Set(object obj, string field, object v) { obj.GetType().GetField(field).SetValue(obj, v); }

    public static Player Mk(ulong id, string name, string house = null, int packSlots = 24, string ip = null)
    {
        var p = new Player(id, name, packSlots);
        p.Entity.Position = new UnityEngine.Vector3(id % 1000, 0, 0);
        if (ip != null) p.Connection.IpAddress = ip;
        if (house != null) HouseOf[id] = house;
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        Inv(Q, "OnPlayerConnected", p);
        return p;
    }
    public static void Offline(Player p) { Inv(Q, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(Q, "OnPlayerConnected", p); }
    public static void Admin(Player p) { Q.permission.Grants.Add(p.Id + "|realmquests.admin"); }
    public static void Clear() { Server.Broadcasts.Clear(); ChronLog.Clear(); Deeds.Clear(); RewardCalls.Clear(); SeasonAwards.Clear(); foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); } }
    public static string B() { return string.Join("\n", Server.Broadcasts); }

    // ---- the player's record ----
    public static object P(Player p) { return ((IDictionary)F(Data, "Players"))[p.Id.ToString()]; }
    public static object P(ulong id) { return ((IDictionary)F(Data, "Players"))[id.ToString()]; }
    public static IList Daily(Player p) { return (IList)F(P(p), "Daily"); }
    public static IList Weekly(Player p) { return (IList)F(P(p), "Weekly"); }
    public static List<string> DailyIds(Player p) { return Daily(p).Cast<object>().Select(s => (string)F(s, "QuestId")).ToList(); }
    public static List<string> WeeklyIds(Player p) { return Weekly(p).Cast<object>().Select(s => (string)F(s, "QuestId")).ToList(); }
    public static void SetDaily(Player p, int i, string questId) { Daily(p)[i] = Inv(typeof(RealmQuests), "NewSlot", questId); }
    public static void SetWeekly(Player p, int i, string questId) { Weekly(p)[i] = Inv(typeof(RealmQuests), "NewSlot", questId); }
    public static int Prog(IList slots, int i, int obj) { var pr = (List<int>)F(slots[i], "Progress"); return obj < pr.Count ? pr[obj] : 0; }
    public static bool Done(IList slots, int i) { return (bool)F(slots[i], "Done"); }
    public static long Pending(Player p) { return (long)F(P(p), "PendingMarks"); }
    public static IList Owed(Player p) { return (IList)F(P(p), "Owed"); }
    public static int OwedOf(Player p, string item) { return Owed(p).Cast<object>().Where(o => string.Equals((string)F(o, "Item"), item, StringComparison.OrdinalIgnoreCase)).Sum(o => (int)F(o, "Amount")); }
    public static void Active(Player p, double minutes) { SetF(P(p), "ActiveMinutes", minutes); }
    public static object Story(Player p) { return F(P(p), "Story"); }
    public static List<string> Finished(Player p) { return (List<string>)F(Story(p), "Finished"); }
    public static long AchCount(Player p, string id) { var d = (IDictionary)F(P(p), "Ach"); return d.Contains(id) ? (long)F(d[id], "Count") : 0; }
    public static int AchTier(Player p, string id) { var d = (IDictionary)F(P(p), "Ach"); return d.Contains(id) ? (int)F(d[id], "Tier") : 0; }
    public static int Count(Player p, string key) { var d = (Dictionary<string, int>)F(P(p), "Counts"); int v; return d.TryGetValue(key, out v) ? v : 0; }

    // ---- what players do ----
    public static void Cmd(Player p, params string[] args) { Inv(Q, "CmdQuest", p, "quest", args); }
    public static void Ach(Player p, params string[] args) { Inv(Q, "CmdAchievements", p, "achievements", args); }
    public static void Tick(int n = 1, bool move = true)
    {
        float secs = (float)F(F(Q, "config"), "TickSeconds");
        for (int i = 0; i < n; i++)
        {
            Clock = Clock.AddSeconds(secs);
            if (move) foreach (var p in Server.ClientPlayers) p.Entity.Position = new UnityEngine.Vector3(p.Entity.Position.x + 3f, p.Entity.Position.y, p.Entity.Position.z);
            Inv(Q, "SafeTick");
        }
    }
    public static void Minutes(double m, bool move = true) { float secs = (float)F(F(Q, "config"), "TickSeconds"); Tick((int)Math.Ceiling(m * 60 / secs), move); }
    public static void Poll() { SetF(Q, "lastFeedPoll", DateTime.MinValue); Tick(1); }
    public static Entity Creature(string name, bool motor = true)
    {
        var e = new Entity { IsPlayer = false, name = name };
        if (motor) e.Components.Add(new MonsterMotor()); else e.Components.Add(new MonsterEntity());
        return e;
    }
    public static void Kill(Player killer, Entity victim)
    {
        var dmg = new Damage { Amount = 100, DamageSource = killer != null ? killer.Entity : null };
        Inv(Q, "OnEntityDeath", new EntityDeathEvent { Entity = victim, KillingDamage = dmg });
    }
    public static void Hunt(Player killer, string creature, int n) { for (int i = 0; i < n; i++) Kill(killer, Creature(creature + "(Clone)")); }
    public static ItemCrafter Craft(Player p, string product, int stacks, int perStack = 1, ItemCrafter crafter = null)
    {
        crafter = crafter ?? new ItemCrafter { Label = "bench" };
        var bp = InvBlueprints.Get(product);
        EventManager.Raise(new ItemCrafterCraftEvent { Sender = p, Crafter = crafter, Product = bp, Quantity = stacks });
        for (int i = 0; i < stacks; i++) EventManager.Raise(new ItemCrafterItemEvent { Crafter = crafter, Stack = new InvGameItemStack(bp, perStack, null), Cycles = 1 });
        return crafter;
    }
    public static int Placed;
    public static CubePlaceEvent Place(Player p, int x, int y, int z, byte material = 2, bool cancelLater = false)
    {
        var e = new CubePlaceEvent { Sender = p, GridID = 0, Position = new Vector3Int(x, y, z), Material = material };
        Inv(Q, "OnCubePlacement", e);
        if (cancelLater) e.Cancel("another plugin");
        Q.RunTicks();
        return e;
    }
    public static void Build(Player p, int n, byte material = 2) { for (int i = 0; i < n; i++) Place(p, Placed++, 1, 1, material); }
    public static void Give(Player p, string item, int n) { ItemCollection.Give(p.Packs, item, n); }
    public static int Has(Player p, string item) { return ItemCollection.AutoCount(p.Packs, InvBlueprints.Get(item)); }
    public static void MarkPlace(string id, float x, float z, float r = 40f)
    {
        var places = (IDictionary)F(Data, "Places");
        var t = typeof(RealmQuests).GetNestedType("PlaceMark", BF);
        var m = Activator.CreateInstance(t);
        t.GetField("X").SetValue(m, x); t.GetField("Z").SetValue(m, z); t.GetField("Radius").SetValue(m, r);
        places[id] = m;
    }
    public static void Goto(Player p, float x, float z) { p.Entity.Position = new UnityEngine.Vector3(x, 0, z); }
    public static void WriteJson(string name, string json) { File.WriteAllText(Path.Combine(Dir, name + ".json"), json); }
    public static void Season(int day, int number = 1)
    {
        SeasonNumber = number;
        WriteJson("RealmSeasons", "{\"Number\":" + number + ",\"Active\":true,\"StartedAt\":\"" + Clock.AddDays(-day).AddMinutes(-1).ToString("o") + "\"}");
        SetF(Q, "seasonDayCache", -2);
    }
}
