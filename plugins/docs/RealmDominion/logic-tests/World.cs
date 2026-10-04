// The shared test world for RealmDominion: a clock, fake Realm plugins that answer like the real ones (RealmHouses,
// RealmChronicle, RealmSeasons, RealmRenown, RealmTreasury, RealmPainter, RealmHerald, RealmWarden, RealmEvents,
// CrownAndConsequences), players with positions, and helpers. Used by Tests.cs here and by
// tools/exploit-review/dominion/Tests.cs. NOT the real game.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events.Entities;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;

public static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public const string Admin = "realmdominion.admin";
    public static RealmDominion P;
    public static DateTime Now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);   // a Wednesday
    public static string Dir;
    static ulong nextId = 76561190000005000;

    // RealmHouses
    public static bool HousesLoaded = true;
    public static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    public static Dictionary<string, string> Founded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, string> LiegeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Treaties = new HashSet<string>();
    public static Dictionary<string, int> ExtraMembers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // offline members
    // Other plugins
    public static bool Truce, Rebellion, RaidOpen = true, WardenLoaded = true, TreasuryLoaded = true, PopupsWanted = true;
    public static double OffsetHours;
    public static long TreasuryBudget = long.MaxValue;
    public static HashSet<ulong> Protected = new HashSet<ulong>();
    public static List<string> Chron = new List<string>(), Awards = new List<string>(), Deeds = new List<string>(), Grants = new List<string>(), Refresh = new List<string>();
    public static Plugin Houses, Chronicle, Seasons, Renown, Treasury, Painter, Herald, Warden, Events, Crown;

    public static void Ok(bool c, string name, string extra = "")
    {
        if (c) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }
    public static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    public static object Inv(object o, string m, params object[] a)
    {
        var mi = o.GetType().GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return mi.Invoke(o, a);
    }
    public static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    public static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    public static object Data() { return F(P, "data"); }
    public static object Cfg() { return F(P, "config"); }
    public static void SetCfg(string f, object v) { SetF(Cfg(), f, v); }
    public static IList Holdings() { return (IList)F(Data(), "Holdings"); }
    public static object H(string id) { foreach (var h in Holdings()) if ((string)F(h, "Id") == id) return h; return null; }
    public static string Owner(string id) { return (string)F(H(id), "Owner"); }
    public static string Capturer(string id) { return (string)F(H(id), "Capturer"); }
    public static double Progress(string id) { return (double)F(H(id), "Progress"); }
    public static string State(string id) { return (string)F(H(id), "State"); }
    public static int Garrison(string id) { return (int)F(H(id), "Garrison"); }
    public static void SetOwner(string id, string house, DateTime since)
    {
        var h = H(id);
        SetF(h, "Owner", house); SetF(h, "OwnerSince", since); SetF(h, "OwnerFounded", house != null ? Founded[house] : null);
    }
    public static string File(string name) { return Path.Combine(Dir, name + ".json"); }
    public static string Heralds() { return string.Join("\n", Server.Broadcasts); }

    static string Key(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        return string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
    }
    public static void Treaty(string a, string b) { Treaties.Add(Key(a, b)); }
    public static void EndTreaty(string a, string b) { Treaties.Remove(Key(a, b)); }
    public static void House(string name, string founded) { Founded[name] = founded; }

    public static void Setup(string tag)
    {
        Dir = Path.Combine(Path.GetTempPath(), "dominion-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (!HousesLoaded) return null;
            switch (h)
            {
                case "GetHouse": { string s; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out s) ? s : null; }
                case "GetMembers":
                {
                    string house = (string)a[0];
                    var m = HouseOf.Where(kv => string.Equals(kv.Value, house, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.ToString()).ToList();
                    int extra; if (ExtraMembers.TryGetValue(house, out extra)) for (int i = 0; i < extra; i++) m.Add("offline" + i);
                    return Founded.ContainsKey(house) ? m : null;
                }
                case "GetHouseFounded": { string f; return Founded.TryGetValue((string)a[0], out f) ? f : null; }
                case "GetLiege": { string l; return LiegeOf.TryGetValue((string)a[0], out l) ? l : null; }
                case "HasTreaty": return Treaties.Contains(Key((string)a[0], (string)a[1]));
                case "GetHouseSummaries": return Founded.Keys.Select(k => new Dictionary<string, object> { { "name", k } }).ToList();
            }
            return null;
        } };
        Chronicle = new Plugin { Name = "RealmChronicle", Handler = (h, a) => { if (h == "Log") { Chron.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + string.Join(",", (string[])a[3])); return Chron.Count; } return null; } };
        Seasons = new Plugin { Name = "RealmSeasons", Handler = (h, a) => { if (h == "AwardHouse") { Awards.Add(a[0] + "|" + a[1] + "|" + (a[2] ?? "")); return true; } return null; } };
        Renown = new Plugin { Name = "RealmRenown", Handler = (h, a) => { if (h == "AddDeed") { Deeds.Add(a[0] + "|" + a[2] + "|" + a[4]); return true; } return null; } };
        Treasury = new Plugin { Name = "RealmTreasury", Handler = (h, a) =>
        {
            if (h != "GrantHouseIncome") return null;
            long want = (long)a[1];
            long n = Math.Min(want, TreasuryBudget);
            if (n <= 0) return 0L;
            if (TreasuryBudget != long.MaxValue) TreasuryBudget -= n;
            Grants.Add(a[0] + "|" + n + "|" + a[2]);
            return n;
        } };
        Painter = new Plugin { Name = "RealmPainter", Handler = (h, a) => { if (h == "RefreshBoards") Refresh.Add((string)a[0]); return null; } };
        Herald = new Plugin { Name = "RealmHerald", Handler = (h, a) => h == "PopupsWanted" ? (object)PopupsWanted : null };
        Warden = new Plugin { Name = "RealmWarden", Handler = (h, a) =>
        {
            if (h == "IsRaidHourNow") return RaidOpen;
            if (h == "IsNewPlayerProtected") return Protected.Contains((ulong)a[0]);
            return null;
        } };
        Events = new Plugin { Name = "RealmEvents", Handler = (h, a) => h == "IsTruceActive" ? (object)Truce : null };
        Crown = new Plugin { Name = "CrownAndConsequences", Handler = (h, a) =>
        {
            if (h == "IsRebellionActive") return Rebellion;
            if (h == "GetUtcOffsetHours") return OffsetHours;
            return null;
        } };
    }

    // Default config with changes, as JSON.
    public static string Config(Action<JsonObject> change)
    {
        var c = new RealmDominion();
        Inv(c, "LoadDefaultConfig");
        var doc = JsonNode.Parse(c.Config.Json).AsObject();
        if (change != null) change(doc);
        return doc.ToJsonString();
    }

    public static RealmDominion NewPlugin(string json)
    {
        var p = new RealmDominion();
        p.Config.Json = json;
        SetF(p, "clock", (Func<DateTime>)(() => Now));
        SetF(p, "RealmHouses", Houses); SetF(p, "RealmChronicle", Chronicle); SetF(p, "RealmSeasons", Seasons);
        SetF(p, "RealmRenown", Renown); SetF(p, "RealmTreasury", TreasuryLoaded ? Treasury : null); SetF(p, "RealmPainter", Painter);
        SetF(p, "RealmHerald", Herald); SetF(p, "RealmWarden", WardenLoaded ? Warden : null); SetF(p, "RealmEvents", Events);
        SetF(p, "CrownAndConsequences", Crown);
        foreach (ulong id in Admins) p.permission.Grants.Add(id + "|" + Admin);
        Inv(p, "LoadDefaultMessages");
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    public static RealmDominion Load(Action<JsonObject> change = null) { P = NewPlugin(Config(change)); return P; }
    public static void Reload(Action<JsonObject> change = null)
    {
        Inv(P, "Unload");
        string json = P.Config.Json;
        P = NewPlugin(change == null ? json : Config(change));
    }

    public static Player Mk(string name, string house, float x = 0, float z = 0, bool online = true)
    {
        var p = new Player(nextId++, name);
        p.Entity.Position = new Vector3(x, 10, z);
        if (house != null) HouseOf[p.Id] = house;
        if (online) Server.ClientPlayers.Add(p);
        return p;
    }
    public static Player MkAdmin(string name, float x = 0, float z = 0)
    {
        var p = Mk(name, null, x, z);
        P.permission.Grants.Add(p.Id + "|" + Admin);
        Admins.Add(p.Id);
        return p;
    }
    public static HashSet<ulong> Admins = new HashSet<ulong>();
    public static void Connect(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(P, "OnPlayerConnected", p); }
    public static void Leave(Player p) { Inv(P, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    public static void At(Player p, float x, float z, float y = 10) { p.Entity.Position = new Vector3(x, y, z); }
    public static void Cmd(Player p, string line)
    {
        string[] args = line.Length == 0 ? new string[0] : line.Split(' ');
        Inv(P, "CmdDominion", p, "dominion", args);
    }
    public static string Said(Player p) { string s = p.All(); p.Messages.Clear(); return s; }
    public static void Clear()
    {
        foreach (var p in Server.ClientPlayers) { p.Messages.Clear(); p.Popups.Clear(); }
        Server.Broadcasts.Clear();
        Chron.Clear(); Awards.Clear(); Deeds.Clear(); Grants.Clear(); Refresh.Clear();
    }
    public static void Tick() { Inv(P, "Tick"); }
    // n ticks of `seconds` each.
    public static void Run(int n, double seconds = 5)
    {
        for (int i = 0; i < n; i++) { Now = Now.AddSeconds(seconds); Tick(); }
    }
    public static void RunFor(double seconds) { Run((int)Math.Ceiling(seconds / 5.0), 5); }

    // Places a holding with the admin command, standing at (x, z).
    public static void Place(Player admin, string id, float x, float z, int radius)
    {
        At(admin, x, z);
        Cmd(admin, "admin move " + id + " " + radius);
    }

    // A clean world: no players, no plugin state, the clock on Wednesday noon, the three seed houses founded long ago.
    public static void Reset()
    {
        Server.ClientPlayers.Clear();
        Admins.Clear();
        HouseOf.Clear(); Founded.Clear(); LiegeOf.Clear(); Treaties.Clear(); ExtraMembers.Clear(); Protected.Clear();
        Truce = false; Rebellion = false; RaidOpen = true; WardenLoaded = true; TreasuryLoaded = true; PopupsWanted = true; HousesLoaded = true;
        OffsetHours = 0; TreasuryBudget = long.MaxValue;
        Clear();
        Now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        House("Varrow", "2026-09-01T00:00:00.0000000Z");
        House("Corvane", "2026-09-02T00:00:00.0000000Z");
        House("Ashgrove", "2026-09-03T00:00:00.0000000Z");
        House("Merrin", "2026-09-04T00:00:00.0000000Z");
        foreach (var f in Directory.GetFiles(Dir)) System.IO.File.Delete(f);
    }

    // Wednesday 20:30 UTC: the first default War Hours window opens.
    public static DateTime WarHours = new DateTime(2026, 10, 7, 20, 30, 0, DateTimeKind.Utc);

    public static EntityDamageEvent Hit(Player victim, Player attacker, float amount)
    {
        var e = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = attacker != null ? attacker.Entity : null } };
        Inv(P, "OnEntityHealthChange", e);
        return e;
    }

    // Every player-facing line must open with a speaker, continue a list (two spaces) or be the Herald.
    public static string StyleProblems(IEnumerable<string> lines)
    {
        var bad = new List<string>();
        foreach (string l in lines)
        {
            string s = l.StartsWith("ERR ") ? l.Substring(4) : l;
            if (s.StartsWith("  ") || s.StartsWith("[D6A043]Dominion[FFFFFF]: ") || s.StartsWith("[8FC97A]Dominion[FFFFFF]: ")
                || s.StartsWith("[E8913A]Dominion[FFFFFF]: ") || s.StartsWith("[E86A5C]Dominion[FFFFFF]: ") || s.StartsWith("[D6A043]Herald[FFFFFF]: ")) continue;
            bad.Add(l);
        }
        return string.Join("\n", bad);
    }
}
