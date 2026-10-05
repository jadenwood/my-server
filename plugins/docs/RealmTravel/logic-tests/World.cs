// Shared test world for RealmTravel: players standing on a map, fake RealmTreasury (purses and the crown's treasury),
// RealmHouses, CrownAndConsequences, RealmLaws, RealmContracts, RealmLegendary, RealmWarden, RealmHerald, RealmSeasons,
// RealmRenown and RealmSentinel, crest zones, and a clock the tests move. Used by the logic tests
// (plugins/docs/RealmTravel/logic-tests) and the exploit suite (tools/exploit-review/travel).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Thrones.AncientThrone;
using CodeHatch.Thrones.Capture;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock;
    public static RealmTravel T;
    public static string Dir;

    // The realm the fakes answer from.
    public static Dictionary<string, long> Purses = new Dictionary<string, long>();
    public static long TreasuryMarks;
    public static bool TreasuryRefuses;
    public static List<string> Charges = new List<string>();
    public static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    public static Dictionary<string, string> Liege = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Treaties = new HashSet<string>();
    public static bool Rebellion;
    public static HashSet<ulong> Ransom = new HashSet<ulong>();
    public static HashSet<string> Sworn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Decrees = new HashSet<string>();
    public static double UtcOffset;
    public static HashSet<string> CourtOutlaws = new HashSet<string>(), Exiles = new HashSet<string>(), ContractOutlaws = new HashSet<string>();
    public static string Bearer;
    public static HashSet<ulong> Protected = new HashSet<ulong>(), WardenFight = new HashSet<ulong>();
    public static bool RaidHours, Truce;
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static int Season;
    public static List<string> Awards = new List<string>(), Deeds = new List<string>(), Graces = new List<string>(), ItemSources = new List<string>();
    public static CrestScheme Crests;
    public static List<Player> Everyone = new List<Player>();

    // Which fakes are "loaded" (a null [PluginReference]).
    public static HashSet<string> Absent = new HashSet<string>();

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
    public static object D(string field) { return F(F(T, "data"), field); }
    public static object Cfg(string section) { return F(F(T, "config"), section); }
    public static void Set(string section, string field, object v) { SetF(Cfg(section), field, v); }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static void Clear() { Server.Broadcasts.Clear(); Charges.Clear(); Awards.Clear(); Deeds.Clear(); Graces.Clear(); ItemSources.Clear(); foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); } }

    public static void Reset()
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        Purses.Clear(); TreasuryMarks = 0; TreasuryRefuses = false; Charges.Clear();
        HouseOf.Clear(); Liege.Clear(); Treaties.Clear(); Rebellion = false; Ransom.Clear(); Sworn.Clear(); Decrees.Clear(); UtcOffset = 0;
        CourtOutlaws.Clear(); Exiles.Clear(); ContractOutlaws.Clear(); Bearer = null; Protected.Clear(); WardenFight.Clear(); RaidHours = false;
        PopupsOff.Clear(); Truce = false; Season = 0; Awards.Clear(); Deeds.Clear(); Graces.Clear(); ItemSources.Clear(); Absent.Clear();
        SocialAPI.Registry.Clear(); SocialAPI.GroupOf.Clear();
        Crests = new CrestScheme();
        SocialAPI.Registry[typeof(CrestScheme)] = Crests;
        AncientThrone.EntityPosition = new UnityEngine.Vector3(0, 0, 0);
        InvBlueprints.Instance = new InvBlueprints();
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmtravel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
    }

    public static string Pair(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a + "|" + b).ToLowerInvariant() : (b + "|" + a).ToLowerInvariant(); }

    static Plugin Fake(string name, Func<string, object[], object> h) { return new Plugin { Name = name, Handler = h }; }

    public static Plugin Treasury = Fake("RealmTreasury", (h, a) =>
    {
        if (h == "GetPurse") { long m; return Purses.TryGetValue((string)a[0], out m) ? m : 0L; }
        if (h == "ChargeMarks")
        {
            string id = (string)a[0]; long amount = (long)a[2];
            Charges.Add(id + "|" + a[1] + "|" + amount + "|" + a[3] + "|" + a[4]);
            long m; Purses.TryGetValue(id, out m);
            if (TreasuryRefuses || amount <= 0 || m < amount) return false;
            Purses[id] = m - amount; TreasuryMarks += amount;
            return true;
        }
        return null;
    });
    public static Plugin Houses = Fake("RealmHouses", (h, a) =>
    {
        if (h == "GetHouse") { string v; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out v) ? v : null; }
        if (h == "GetLiege") { string v; return Liege.TryGetValue((string)a[0], out v) ? v : null; }
        if (h == "HasTreaty") return Treaties.Contains(Pair((string)a[0], (string)a[1]));
        if (h == "GetMembers") { string house = (string)a[0]; return HouseOf.Where(kv => string.Equals(kv.Value, house, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.ToString()).ToList(); }
        return null;
    });
    public static Plugin Crown = Fake("CrownAndConsequences", (h, a) =>
    {
        if (h == "IsRebellionActive") return Rebellion;
        if (h == "IsHeldForRansom") return Ransom.Contains((ulong)a[0]);
        if (h == "IsSwornToCrown") return Sworn.Contains((string)a[0]);
        if (h == "IsDecreeActive") return Decrees.Contains((string)a[0]);
        if (h == "GetUtcOffsetHours") return UtcOffset;
        return null;
    });
    public static Plugin Laws = Fake("RealmLaws", (h, a) =>
    {
        if (h == "IsCourtOutlaw") return CourtOutlaws.Contains((string)a[0]);
        if (h == "IsExiled") return Exiles.Contains((string)a[0]);
        return null;
    });
    public static Plugin Contracts = Fake("RealmContracts", (h, a) => h == "IsOutlaw" ? (object)ContractOutlaws.Contains((string)a[0]) : null);
    public static Plugin Legendary = Fake("RealmLegendary", (h, a) => h == "IsBearer" ? (object)(Bearer != null && Bearer == (string)a[0]) : null);
    public static Plugin Warden = Fake("RealmWarden", (h, a) =>
    {
        if (h == "IsNewPlayerProtected") return Protected.Contains((ulong)a[0]);
        if (h == "IsInCombat") return WardenFight.Contains((ulong)a[0]);
        if (h == "IsRaidHourNow") return RaidHours;
        return null;
    });
    public static Plugin Events = Fake("RealmEvents", (h, a) => h == "IsTruceActive" ? (object)Truce : null);
    public static Plugin Herald = Fake("RealmHerald", (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null);
    public static Plugin Seasons = Fake("RealmSeasons", (h, a) =>
    {
        if (h == "GetSeasonNumber") return Season;
        if (h == "AwardHouse") { if (Season <= 0) return false; Awards.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; }
        return null;
    });
    public static Plugin Renown = Fake("RealmRenown", (h, a) => { if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; });
    public static Plugin Sentinel = Fake("RealmSentinel", (h, a) =>
    {
        if (h == "SentinelGrace") { Graces.Add(a[0] + "|" + a[1]); return null; }
        if (h == "SentinelItemSource") { ItemSources.Add(a[0] + "|" + a[1]); return null; }
        return null;
    });

    static readonly string[][] Refs =
    {
        new[] { "RealmTreasury", "Treasury" }, new[] { "RealmHouses", "Houses" }, new[] { "CrownAndConsequences", "Crown" },
        new[] { "RealmLaws", "Laws" }, new[] { "RealmContracts", "Contracts" }, new[] { "RealmLegendary", "Legendary" },
        new[] { "RealmWarden", "Warden" }, new[] { "RealmHerald", "Herald" }, new[] { "RealmSeasons", "Seasons" },
        new[] { "RealmRenown", "Renown" }, new[] { "RealmSentinel", "Sentinel" }, new[] { "RealmEvents", "Events" },
    };

    // A plugin with the default config (tweak changes it before Init), wired to the fakes, loaded and started.
    public static RealmTravel NewTravel(Action<object> tweak = null)
    {
        var p = new RealmTravel();
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmTravel).GetNestedType("PluginConfig", BF);
            var obj = System.Text.Json.JsonSerializer.Deserialize(p.Config.Json, cfg, DataFileSystem.Opts);
            tweak(obj);
            p.Config.WriteObject(obj, true);
        }
        Inv(p, "LoadDefaultMessages");
        foreach (var r in Refs)
            SetF(p, r[0], Absent.Contains(r[0]) ? null : (Plugin)typeof(W).GetField(r[1]).GetValue(null));
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        T = p;
        return p;
    }
    public static void Section(object cfg, string section, string field, object v) { SetF(F(cfg, section), field, v); }

    // Unload and load again, as a restart or hot reload does. Oxide keeps permissions outside the plugin, so they stay.
    public static void Reload(Action<object> tweak = null)
    {
        var grants = new HashSet<string>(T.permission.Grants);
        Inv(T, "Unload");
        NewTravel(tweak);
        foreach (var g in grants) T.permission.Grants.Add(g);
    }

    public static Player Mk(ulong id, string name, float x = 0, float z = 0, string house = null)
    {
        var p = new Player(id, name);
        p.Entity.Position = new UnityEngine.Vector3(x, 10, z);
        if (house != null) HouseOf[id] = house;
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        if (T != null) Inv(T, "OnPlayerConnected", p);
        return p;
    }
    public static void Offline(Player p) { Inv(T, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(T, "OnPlayerConnected", p); }
    public static void Admin(Player p) { T.permission.Grants.Add(p.Id + "|realmtravel.admin"); }
    public static void At(Player p, float x, float z, float y = 10) { p.Entity.Position = new UnityEngine.Vector3(x, y, z); }
    public static UnityEngine.Vector3 Pos(Player p) { return p.Entity.Position; }

    static readonly Dictionary<string, string> CmdMethod = new Dictionary<string, string>
    { { "travel", "CmdTravel" }, { "home", "CmdHome" }, { "road", "CmdRoad" }, { "kit", "CmdKit" } };
    public static string Cmd(Player p, string cmd, params string[] args)
    {
        int before = p.Messages.Count;
        Inv(T, CmdMethod[cmd], p, cmd, args);
        return string.Join("\n", p.Messages.Skip(before));
    }

    // Seconds of server time, one tick a second.
    public static void Tick(int seconds = 1)
    {
        for (int i = 0; i < seconds; i++) { Clock = Clock.AddSeconds(1); Inv(T, "SafeTick"); }
    }

    // Raise a waystone where the admin stands (the admin is moved there and back).
    public static void Raise(Player admin, string id, string kind, float x, float z, string name = null)
    {
        var old = Pos(admin);
        At(admin, x, z);
        if (name == null) Cmd(admin, "travel", "admin", "set", id, kind); else Cmd(admin, "travel", new[] { "admin", "set", id, kind }.Concat(name.Split(' ')).ToArray());
        admin.Entity.Position = old;
    }

    // Walk a player to a waystone so they learn it, then back to where they were.
    public static void Visit(Player p, float x, float z)
    {
        var old = Pos(p);
        At(p, x, z);
        Tick(3);
        p.Entity.Position = old;
    }

    public static float Hit(Player attacker, Player victim, float amount = 10f)
    {
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = attacker.Entity } };
        Inv(T, "OnEntityHealthChange", evt);
        return evt.Cancelled ? 0f : evt.Damage.Amount;
    }
    public static void Beast(Player victim, float amount = 10f)
    {
        var wolf = new Entity { IsPlayer = false };
        Inv(T, "OnEntityHealthChange", new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = wolf } });
    }
    public static void Fall(Player victim, float amount = 10f)
    {
        Inv(T, "OnEntityHealthChange", new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = null, DamageTypes = DamageType.Falling } });
    }
    // A rope or chain thrown by captor at target; true when the bind is allowed.
    public static bool Bind(Player captor, Player target)
    {
        var evt = new CodeHatch.Networking.Events.PlayerCaptureEvent { Captor = captor.Entity, Target = target };
        Inv(T, "OnPlayerCapture", evt);
        return !evt.Cancelled;
    }
    // A blow another plugin (RealmWarden, the truce) has already cancelled.
    public static void BlockedHit(Player attacker, Player victim)
    {
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = 10f, DamageSource = attacker.Entity } };
        evt.Cancel("someone else");
        Inv(T, "OnEntityHealthChange", evt);
    }
    public static void Die(Player victim) { Inv(T, "OnEntityDeath", new EntityDeathEvent { Entity = victim.Entity }); }

    public static CrestScheme.Zone Crest(float x, float z, float r, ulong group, ulong owner) { var c = new CrestScheme.Zone { X = x, Z = z, R = r, Group = group, Owner = owner }; Crests.Zones.Add(c); return c; }
    public static PlayerCaptureManager Capture(Player p) { var m = p.Entity.TryGet<PlayerCaptureManager>(); if (m == null) { m = new PlayerCaptureManager(); p.Entity.Components.Add(m); } return m; }

    public static int Count(Player p, string item) { return ItemCollection.AutoCount(p.Inventory.Contents, InvBlueprints.Get(item)); }
    public static object Rec(Player p) { var players = (System.Collections.IDictionary)D("Players"); return players.Contains(p.Id.ToString()) ? players[p.Id.ToString()] : null; }
    public static List<string> Unlocked(Player p) { var r = Rec(p); return r == null ? new List<string>() : (List<string>)F(r, "Unlocked"); }
    public static int OwedUnits(Player p) { var r = Rec(p); return r == null ? 0 : ((System.Collections.IList)F(r, "Owed")).Cast<object>().Sum(o => (int)F(o, "Amount")); }
    public static bool Travelling(Player p) { return (bool)Inv(T, "IsTravelling", p.Id.ToString()); }
}
