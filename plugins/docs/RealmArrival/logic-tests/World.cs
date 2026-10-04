// Shared test world for RealmArrival: the Gatehouse site laid out on the +z axis, players on a map, the virtual clock,
// and fake RealmSentinel, RealmTravel, RealmWarden, RealmHouses, CrownAndConsequences, RealmEvents, RealmArena,
// RealmQuests, RealmRenown and RealmHerald. Used by the logic tests (plugins/docs/RealmArrival/logic-tests) and the
// exploit suite (tools/exploit-review/arrival).
//
// The site (metres; one block is 1.2 m; the floor is block row y=7, players stand at y=10):
//   hall box (0,0)-(22,26), six stones at z 5 and 10, the gold line at (11,22), the gate opening cells x 8-12, z 22,
//   rows y 8-13 (bottom-left cell at (9.6,10,26.4)), the eject point at (11,31), the avenue along x=11 to the Hearth at
//   (11,130): pledge stones in pairs at z 46, 74 and 103 (x 3 and 19), mercy stones at the Hearth, the wayboard at
//   (11,147), the drop pad west of the hall, the Old Throne at (11,300).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Blocks;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Networking.Events.Players;
using CodeHatch.StarForge.Sleeping;
using CodeHatch.Thrones.AncientThrone;
using CodeHatch.UserInterface.Dialogues;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock;
    public static RealmArrival A;
    public static string Dir, CfgDir;
    public static RootCubeGrid Grid;

    // What the fakes answer.
    public static List<string> Graces = new List<string>(), Cancels = new List<string>(), Alerts = new List<string>(), Quests = new List<string>(), Deeds = new List<string>();
    public static HashSet<ulong> Protected = new HashSet<ulong>(), InCombat = new HashSet<ulong>();
    public static Dictionary<ulong, int> ProtectionLeft = new Dictionary<ulong, int>();
    public static Dictionary<string, int> Discovered = new Dictionary<string, int>();
    public static HashSet<string> Travelling = new HashSet<string>();
    public static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    public static Dictionary<string, string> Liege = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, ulong> Leader = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    public static List<string> Houses = new List<string>();
    public static string King, KingHouse, Story;
    public static Dictionary<string, object> NextEvent;
    public static HashSet<string> DuelPairs = new HashSet<string>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static HashSet<string> Absent = new HashSet<string>();
    public static List<Player> Everyone = new List<Player>();
    public static Func<Player, bool> Sneak;              // test hook: called inside the plugin's teleport (unused unless set)

    public static readonly UnityEngine.Vector3 Raft = new UnityEngine.Vector3(-400, 2, -400);
    public static readonly UnityEngine.Vector3 RandomSpawn = new UnityEngine.Vector3(600, 10, 600);

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
    public static object Cfg() { return F(A, "config"); }
    public static void Set(string field, object v) { SetF(Cfg(), field, v); }
    public static object Data() { return F(A, "data"); }
    public static object SiteData() { return F(Data(), "Site"); }
    public static object Rec(Player p) { var d = (IDictionary)F(Data(), "Players"); return d.Contains(p.Id.ToString()) ? d[p.Id.ToString()] : null; }
    public static string Stage(Player p) { var r = Rec(p); return r == null ? null : (string)F(r, "Stage"); }
    public static object RecF(Player p, string f) { return F(Rec(p), f); }
    public static int Counter(string k) { var d = (Dictionary<string, int>)F(F(Data(), "Stats"), "Counters"); int v; return d.TryGetValue(k, out v) ? v : 0; }
    public static string ArrivalStage(Player p) { return (string)Inv(A, "ArrivalStage", p.Id.ToString()); }
    public static bool Owns(Player p) { return (bool)Inv(A, "OwnsArrival", p.Id.ToString()); }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static void Clear() { Server.Broadcasts.Clear(); Graces.Clear(); Cancels.Clear(); Alerts.Clear(); Quests.Clear(); Deeds.Clear(); foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); p.Heals.Clear(); } }

    public static void Reset()
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        Graces.Clear(); Cancels.Clear(); Alerts.Clear(); Quests.Clear(); Deeds.Clear();
        Protected.Clear(); InCombat.Clear(); ProtectionLeft.Clear(); Discovered.Clear(); Travelling.Clear();
        HouseOf.Clear(); Liege.Clear(); Leader.Clear(); Houses.Clear();
        King = null; KingHouse = null; Story = null; NextEvent = null; DuelPairs.Clear(); PopupsOff.Clear(); Absent.Clear(); Sneak = null;
        PlayerSleeperObject.AllSleeperObjects.Clear();
        EventManager.CurrentEvent = null;
        SpawnpointManager.defaultSpawnpointProvider = new VanillaProvider();
        AncientThrone.EntityPosition = new UnityEngine.Vector3(11, 30, 300);
        GameClock.Instance = new GameClock();
        AudioController.Played.Clear();
        CodeHatch.Engine.Core.Consoles.NewsFeed.Sent.Clear();
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        TimerLib.Now = () => Clock;
        if (Dir == null)
        {
            Dir = Path.Combine(Path.GetTempPath(), "realmarrival-" + Guid.NewGuid().ToString("N"));
            CfgDir = Path.Combine(Dir, "config");
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(CfgDir);
        }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        foreach (var f in Directory.GetFiles(CfgDir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Interface.Oxide.ConfigDirectory = CfgDir;
        Grid = new RootCubeGrid();
        Grid.Now = () => Clock;
        BlockManager.DefaultCubeGrid = Grid;
        // The Gatehouse floor (block row y=7 under the hall) and a few blocks of the court.
        for (int x = 0; x <= 19; x++) for (int z = 0; z <= 22; z++) Grid.Put(x, 7, z, 1);
        // Other plugins' files the site check reads: the_hearth marked by RealmQuests, the Hearth zone, the waystones.
        File.WriteAllText(Path.Combine(Dir, "RealmQuests.json"), "{ \"Places\": { \"the_hearth\": { \"X\": 11, \"Y\": 10, \"Z\": 131, \"Radius\": 40 } } }");
        File.WriteAllText(Path.Combine(CfgDir, "RealmLaws.json"), "{ \"Zones\": [ { \"Name\": \"Hearth\", \"X\": 11, \"Z\": 130, \"Radius\": 40, \"Town\": true } ] }");
        File.WriteAllText(Path.Combine(Dir, "RealmTravel.json"), "{ \"Waystones\": { \"the-hearth\": {}, \"crown-market\": {} } }");
    }

    // The game's own provider (PlayerSpawnSelection's role): random points far from the site.
    public class VanillaProvider : ISpawnpointProvider
    {
        public int RandomCalls;
        public UnityEngine.Vector3 GetSpawnPoint(Entity player) { return RandomSpawn; }
        public UnityEngine.Vector3[] GetAllSpawnPoints(Entity player) { return new[] { RandomSpawn }; }
        public UnityEngine.Vector3 GetRandomSpawnPoint() { RandomCalls++; return RandomSpawn; }
    }

    static Plugin Fake(string name, Func<string, object[], object> h) { return new Plugin { Name = name, Handler = h }; }

    public static Plugin Sentinel = Fake("RealmSentinel", (h, a) => { if (h == "SentinelGrace") Graces.Add(a[0] + "|" + Convert.ToString(a[1], System.Globalization.CultureInfo.InvariantCulture)); return null; });
    public static Plugin Travel = Fake("RealmTravel", (h, a) =>
    {
        if (h == "CancelJourney") { Cancels.Add((string)a[0]); return false; }
        if (h == "GetDiscoveredCount") { int n; return Discovered.TryGetValue((string)a[0], out n) ? n : 0; }
        if (h == "IsTravelling") return Travelling.Contains((string)a[0]);
        return null;
    });
    public static Plugin Warden = Fake("RealmWarden", (h, a) =>
    {
        if (h == "IsNewPlayerProtected") return Protected.Contains((ulong)a[0]);
        if (h == "IsInCombat") return InCombat.Contains((ulong)a[0]);
        if (h == "RaiseWardenAlert") { Alerts.Add(a[0] + "|" + a[1] + "|" + a[2]); return Alerts.Count; }
        if (h == "GetProtectionMinutesLeft") { int n; return ProtectionLeft.TryGetValue((ulong)a[0], out n) ? n : 0; }
        return null;
    });
    public static Plugin HousesP = Fake("RealmHouses", (h, a) =>
    {
        if (h == "GetHouseSummaries")
            return Houses.Select(n => new Dictionary<string, object> { { "name", n }, { "sigil", "" }, { "liege", Liege.ContainsKey(n) ? Liege[n] : null },
                { "members", HouseOf.Count(kv => string.Equals(kv.Value, n, StringComparison.OrdinalIgnoreCase)) } }).ToList();
        if (h == "GetMembers") { string n = (string)a[0]; return HouseOf.Where(kv => string.Equals(kv.Value, n, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key.ToString()).ToList(); }
        if (h == "GetHouseLeader") { ulong l; return Leader.TryGetValue((string)a[0], out l) ? l.ToString() : null; }
        if (h == "GetLiege") { string v; return Liege.TryGetValue((string)a[0], out v) ? v : null; }
        if (h == "GetHouse") { string v; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out v) ? v : null; }
        return null;
    });
    public static Plugin Crown = Fake("CrownAndConsequences", (h, a) => h == "GetKingName" ? King : h == "GetKingHouse" ? (object)KingHouse : null);
    public static Plugin Events = Fake("RealmEvents", (h, a) => h == "GetNextEvent" ? NextEvent : null);
    public static Plugin Arena = Fake("RealmArena", (h, a) => h == "IsDuelBlow" ? (object)DuelPairs.Contains(a[0] + "|" + a[1]) : null);
    public static Plugin QuestsP = Fake("RealmQuests", (h, a) =>
    {
        if (h == "ReportQuestEvent") { Quests.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + a[3]); return true; }
        if (h == "GetStoryProgress") return Story;
        return null;
    });
    public static Plugin Renown = Fake("RealmRenown", (h, a) => { if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; });
    public static Plugin Herald = Fake("RealmHerald", (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null);

    static readonly string[][] Refs =
    {
        new[] { "RealmSentinel", "Sentinel" }, new[] { "RealmTravel", "Travel" }, new[] { "RealmWarden", "Warden" },
        new[] { "RealmHouses", "HousesP" }, new[] { "CrownAndConsequences", "Crown" }, new[] { "RealmEvents", "Events" },
        new[] { "RealmArena", "Arena" }, new[] { "RealmQuests", "QuestsP" }, new[] { "RealmRenown", "Renown" }, new[] { "RealmHerald", "Herald" },
    };

    // A plugin with the default config (tweak changes it before Init), wired to the fakes, loaded and started.
    public static RealmArrival NewArrival(Action<object> tweak = null)
    {
        var p = new RealmArrival();
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmArrival).GetNestedType("PluginConfig", BF);
            var obj = System.Text.Json.JsonSerializer.Deserialize(p.Config.Json, cfg, DataFileSystem.Opts);
            tweak(obj);
            p.Config.WriteObject(obj, true);
        }
        Inv(p, "LoadDefaultMessages");
        foreach (var r in Refs)
            SetF(p, r[0], Absent.Contains(r[0]) ? null : (Plugin)typeof(W).GetField(r[1]).GetValue(null));
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        A = p;
        Grid.PlaceHook = evt => Inv(A, "OnCubePlacement", evt);
        Inv(p, "OnServerInitialized");
        return p;
    }
    public static void Tweak(object cfg, string field, object v) { cfg.GetType().GetField(field).SetValue(cfg, v); }

    // Unload and load again, as a restart or hot reload does. Oxide keeps permissions and config outside the plugin.
    public static void Reload(Action<object> tweak = null)
    {
        var grants = new HashSet<string>(A.permission.Grants);
        string json = A.Config.Json;
        Inv(A, "Unload");
        A.timer.DestroyAll();
        var p = new RealmArrival();
        p.Config.Json = json;
        if (tweak != null)
        {
            var cfg = typeof(RealmArrival).GetNestedType("PluginConfig", BF);
            var obj = System.Text.Json.JsonSerializer.Deserialize(json, cfg, DataFileSystem.Opts);
            tweak(obj);
            p.Config.WriteObject(obj, true);
        }
        Inv(p, "LoadDefaultMessages");
        foreach (var r in Refs) SetF(p, r[0], Absent.Contains(r[0]) ? null : (Plugin)typeof(W).GetField(r[1]).GetValue(null));
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        foreach (var g in grants) p.permission.Grants.Add(g);
        A = p;
        Grid.PlaceHook = evt => Inv(A, "OnCubePlacement", evt);
        Inv(p, "OnServerInitialized");
    }

    // A crash: the plugin is gone without Unload (no save, the gate as it was), then a fresh load from the files.
    public static void Crash()
    {
        var grants = new HashSet<string>(A.permission.Grants);
        string json = A.Config.Json;
        A.timer.DestroyAll();
        var p = new RealmArrival();
        p.Config.Json = json;
        Inv(p, "LoadDefaultMessages");
        foreach (var r in Refs) SetF(p, r[0], Absent.Contains(r[0]) ? null : (Plugin)typeof(W).GetField(r[1]).GetValue(null));
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        foreach (var g in grants) p.permission.Grants.Add(g);
        A = p;
        Grid.PlaceHook = evt => Inv(A, "OnCubePlacement", evt);
        Inv(p, "OnServerInitialized");
    }

    // ---- time ----
    public static void Advance(double seconds)
    {
        DateTime until = Clock.AddSeconds(seconds);
        while (Clock < until)
        {
            double step = Math.Min(0.1, (until - Clock).TotalSeconds);
            Clock = Clock.AddSeconds(step);
            A.timer.RunDue();
        }
    }

    // ---- players ----
    public static Player Mk(ulong id, string name, float x = 300, float z = 300)
    {
        var p = new Player(id, name);
        p.Entity.Position = new UnityEngine.Vector3(x, 10, z);
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        Inv(A, "OnPlayerConnected", p);
        return p;
    }
    public static void Offline(Player p) { Inv(A, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(A, "OnPlayerConnected", p); }
    public static void Admin(Player p) { A.permission.Grants.Add(p.Id + "|realmarrival.admin"); }
    public static void At(Player p, float x, float z, float y = 10) { p.Entity.Position = new UnityEngine.Vector3(x, y, z); }
    public static UnityEngine.Vector3 Pos(Player p) { return p.Entity.Position; }
    public static float Dist(UnityEngine.Vector3 a, float x, float z) { return (float)Math.Sqrt((a.x - x) * (a.x - x) + (a.z - z) * (a.z - z)); }

    // The game's first-spawn event at a join; returns it so the tests can check it was not changed.
    public static PlayerFirstSpawnEvent FirstSpawn(Player p, bool atFirstSpawn)
    {
        var e = new PlayerFirstSpawnEvent { Player = p, AtFirstSpawn = atFirstSpawn };
        e.Position = atFirstSpawn ? Raft : p.Entity.Position;
        e.PositionSets = 0;
        if (atFirstSpawn) p.Entity.Position = Raft;
        Inv(A, "OnPlayerSpawn", e);
        return e;
    }
    // The Finish click: the game's own teleport to a random spawn point (through the provider, as
    // PlayerListener.OnPreSpawnComplete does), then the OnPlayerSpawned hook.
    public static PlayerPreSpawnCompleteEvent Finish(Player p)
    {
        var e = new PlayerPreSpawnCompleteEvent { Player = p, PreSpawnPosition = Raft };
        EventManager.CurrentEvent = e;
        e.PostSpawnPosition = SpawnpointManager.defaultSpawnpointProvider.GetRandomSpawnPoint();
        p.Entity.Position = e.PostSpawnPosition;
        Inv(A, "OnPlayerSpawned", e);
        EventManager.CurrentEvent = null;
        return e;
    }
    // A brand-new player from the join to the Finish click.
    public static Player Newcomer(ulong id, string name)
    {
        var p = Mk(id, name);
        FirstSpawn(p, true);
        Finish(p);
        return p;
    }

    // Walk in a straight line at speed m/s, the clock running.
    public static void Walk(Player p, float x, float z, float speed = 5f)
    {
        var from = Pos(p);
        double d = Math.Sqrt((x - from.x) * (x - from.x) + (z - from.z) * (z - from.z));
        int steps = Math.Max(1, (int)Math.Ceiling(d / (speed * 0.5)));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            At(p, from.x + (x - from.x) * t, from.z + (z - from.z) * t, from.y);
            Advance(0.5);
        }
    }

    // ---- the site ----
    public static readonly float[][] StoneXZ = { new[] { 5f, 5f }, new[] { 11f, 5f }, new[] { 17f, 5f }, new[] { 5f, 10f }, new[] { 11f, 10f }, new[] { 17f, 10f } };
    public static readonly Dictionary<string, float[]> BannerXZ = new Dictionary<string, float[]>
    {
        { "varrow", new[] { 3f, 46f } }, { "ashgrove", new[] { 19f, 46f } }, { "corvane", new[] { 3f, 74f } },
        { "dunmere", new[] { 19f, 74f } }, { "halloran", new[] { 3f, 103f } }, { "merrin", new[] { 19f, 103f } },
    };

    // Stores the whole site by an admin standing on each point, then opens the arrival.
    public static Player BuildSite(bool open = true, bool gate = false, bool beacon = false)
    {
        var adm = Mk(1, "Steward", 11, 40);
        Admin(adm);
        Stand(adm, 0, 0, "hall", "corner1");
        Stand(adm, 22, 26, "hall", "corner2");
        Stand(adm, -6, 2, "droppad", "corner1");
        Stand(adm, -2, 8, "droppad", "corner2");
        foreach (var s in StoneXZ) Stand(adm, s[0], s[1], "stone", "add");
        Stand(adm, 8, 125, "mercy", "add");
        Stand(adm, 14, 122, "mercy", "add");
        Stand(adm, 11, 140, "mercy", "add");
        Stand(adm, 11, 31, "eject", "set");
        Stand(adm, 11, 22, "threshold", "set");
        Stand(adm, 11, 130, "hearth", "set");
        Stand(adm, 11, 147, "wayboard", "set");
        foreach (var kv in BannerXZ) Stand(adm, kv.Value[0], kv.Value[1], "banner", "set", kv.Key);
        if (gate)
        {
            Stand(adm, 9.6f, 26.4f, "gate", "set", "5", "6");
            Cmd(adm, "admin", "gatemode", "portcullis");
            Cmd(adm, "admin", "gate", "build");
            At(adm, 400, 400);
        }
        if (beacon) { Cmd(adm, "admin", "beacon", "build", "5"); Advance(2); }
        At(adm, 400, 400);
        if (open) Cmd(adm, "admin", "open");
        adm.Messages.Clear();
        Advance(gate ? 6 : 1);
        return adm;
    }
    public static string Stand(Player adm, float x, float z, params string[] sub)
    {
        At(adm, x, z);
        return Cmd(adm, new[] { "admin" }.Concat(sub).ToArray());
    }
    public static string Cmd(Player p, params string[] args)
    {
        int before = p.Messages.Count;
        Inv(A, "CmdArrival", p, "arrival", args);
        return string.Join("\n", p.Messages.Skip(before));
    }
    public static object Command(Player p, string cmd, params string[] args) { return Inv(A, "OnPlayerCommand", p, cmd, args); }

    // Gate cells as built: x 8-12, z 22, rows y 8-13.
    public static int GateSolid() { int n = 0; for (int x = 8; x <= 12; x++) for (int y = 8; y <= 13; y++) if (Grid.Mat(x, y, 22) != 0) n++; return n; }
    public static bool RowSolid(int y) { for (int x = 8; x <= 12; x++) if (Grid.Mat(x, y, 22) == 0) return false; return true; }

    // ---- fighting ----
    public static float Hit(Player attacker, Player victim, float amount = 10f)
    {
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = attacker != null ? attacker.Entity : null } };
        Inv(A, "OnEntityHealthChange", evt);
        return evt.Cancelled ? 0f : evt.Damage.Amount;
    }
    public static float Fall(Player victim, float amount = 10f)
    {
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = null, DamageTypes = DamageType.Falling } };
        Inv(A, "OnEntityHealthChange", evt);
        return evt.Cancelled ? 0f : evt.Damage.Amount;
    }
    public static float HitEntity(Player attacker, Entity target, float amount = 10f)
    {
        var evt = new EntityDamageEvent { Entity = target, Damage = new Damage { Amount = amount, DamageSource = attacker.Entity } };
        Inv(A, "OnEntityHealthChange", evt);
        return evt.Cancelled ? 0f : evt.Damage.Amount;
    }
    public static PlayerRespawnEvent Respawn(Player p, PlayerRespawnEvent e)
    {
        e.Player = p;
        e.Position = RandomSpawn;
        e.PositionSets = 0;
        Inv(A, "OnPlayerRespawn", e);
        return e;
    }

    // ---- popups ----
    public static void Answer(Popup pop, Options o) { pop.Handler(o, null, null); }
    public static List<string> Lines(Player p) { return p.Messages.ToList(); }
    public static int Index(Player p, string fragment) { return p.Messages.FindIndex(m => m.Contains(fragment)); }
}
