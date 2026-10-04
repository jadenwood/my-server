// Shared test world for RealmWorld: players and creatures on a map, chests, fake RealmTreasury (RewardMarks with a cap),
// RealmRenown, RealmSeasons, RealmChronicle, RealmHouses, RealmEvents (a schedule the tests set), RealmWarden,
// RealmHerald, RealmSentinel, RealmPainter, RealmSculptor, RealmTravel, RealmQuests and CrownAndConsequences, and a clock
// the tests move. Used by the logic tests (plugins/docs/RealmWorld/logic-tests) and the exploit suite
// (tools/exploit-review/world).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.AI;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Core.Interaction.Behaviours.Networking;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events.Entities;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock;
    public static RealmWorld T;
    public static string Dir;

    // What the fakes hold.
    public static Dictionary<string, long> Purses = new Dictionary<string, long>();
    public static long RewardCap = long.MaxValue;                     // the treasury pays at most this per call
    public static long Minted;
    public static List<string> Rewards = new List<string>(), Deeds = new List<string>(), Awards = new List<string>(), Logs = new List<string>(),
        ItemSources = new List<string>(), Refreshes = new List<string>(), QuestEvents = new List<string>(), Cancelled = new List<string>(), Removed = new List<string>();
    public static List<string> Placed = new List<string>();
    public static int NextPlacement = 100;
    public static bool SculptorRefuses;
    public static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    public static Dictionary<string, string> Liege = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Treaties = new HashSet<string>();
    public static HashSet<ulong> Protected = new HashSet<ulong>();
    public static bool Truce;
    public static List<string> EventsActive = new List<string>();      // "kind|startIso|endIso"
    public static DateTime? EventsNext;
    public static string EventsNextTitle = "Crown Night";
    public static HashSet<string> Travelling = new HashSet<string>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static int ChronicleId = 10;
    public static string King, KingHouse;
    public static List<Player> Everyone = new List<Player>();
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
    public static object Active() { return D("Active"); }
    public static string ActiveKind() { var a = Active(); return a == null ? null : (string)F(a, "Kind"); }
    public static object Sub(string part) { var a = Active(); return a == null ? null : F(a, part); }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static void Clear()
    {
        Server.Broadcasts.Clear(); Rewards.Clear(); Deeds.Clear(); Awards.Clear(); Logs.Clear(); ItemSources.Clear(); Refreshes.Clear(); QuestEvents.Clear();
        Cancelled.Clear(); Removed.Clear(); Placed.Clear();
        foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); }
        if (T != null) T.Logged.Clear();
    }

    public static void Reset()
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear(); Entity.World.Clear(); Entity.ListThrows = false;
        Purses.Clear(); RewardCap = long.MaxValue; Minted = 0; Clear(); NextPlacement = 100; SculptorRefuses = false;
        HouseOf.Clear(); Liege.Clear(); Treaties.Clear(); Protected.Clear(); Truce = false; EventsActive.Clear(); EventsNext = null; EventsNextTitle = "Crown Night";
        Travelling.Clear(); PopupsOff.Clear(); ChronicleId = 10; King = null; KingHouse = null; Absent.Clear();
        GameClock.Instance = new GameClock();
        InvBlueprints.Instance = new InvBlueprints();
        Clock = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);    // a Tuesday
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmworld-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        T = null;
    }

    public static string Pair(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a + "|" + b).ToLowerInvariant() : (b + "|" + a).ToLowerInvariant(); }
    static Plugin Fake(string name, Func<string, object[], object> h) { return new Plugin { Name = name, Handler = h }; }
    static string Iso(DateTime t) { return t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"); }

    public static Plugin Treasury = Fake("RealmTreasury", (h, a) =>
    {
        if (h == "RewardMarks")
        {
            string id = (string)a[0]; long amount = (long)a[2];
            long n = Math.Min(amount, RewardCap);
            if (n <= 0 || id == null || id.Length < 17) return 0L;
            long m; Purses.TryGetValue(id, out m); Purses[id] = m + n; Minted += n;
            Rewards.Add(id + "|" + n + "|" + a[3]);
            return n;
        }
        if (h == "GetTreasurySummary") return "1200|" + (5000 + Minted) + "|5|0|2";
        return null;
    });
    public static Plugin Renown = Fake("RealmRenown", (h, a) => { if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; });
    public static Plugin Seasons = Fake("RealmSeasons", (h, a) =>
    {
        if (h == "AwardHouse") { Awards.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; }
        if (h == "GetSeasonStandings") return new List<Dictionary<string, object>> { new Dictionary<string, object> { { "rank", 1 }, { "house", "Varrow" }, { "score", 140 }, { "crownDays", 2 } } };
        return null;
    });
    public static Plugin Chronicle = Fake("RealmChronicle", (h, a) =>
    {
        if (h == "Log") { Logs.Add(a[0] + "|" + a[1] + "|" + a[2]); ChronicleId++; return ChronicleId; }
        if (h == "GetLastEventId") return ChronicleId;
        return null;
    });
    public static Plugin Houses = Fake("RealmHouses", (h, a) =>
    {
        if (h == "GetHouse") { string v; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out v) ? v : null; }
        if (h == "GetLiege") { string v; return Liege.TryGetValue((string)a[0], out v) ? v : null; }
        if (h == "HasTreaty") return Treaties.Contains(Pair((string)a[0], (string)a[1]));
        if (h == "GetHouseSummaries")
            return HouseOf.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new Dictionary<string, object> { { "name", n }, { "sigil", "x" }, { "liege", null }, { "members", HouseOf.Values.Count(v => v == n) } }).ToList();
        return null;
    });
    public static Plugin Events = Fake("RealmEvents", (h, a) =>
    {
        if (h == "IsTruceActive") return Truce;
        if (h == "GetActiveEvents") return EventsActive.ToArray();
        if (h == "GetNextEvent") return EventsNext.HasValue ? new Dictionary<string, object> { { "title", EventsNextTitle }, { "at", EventsNext.Value } } : null;
        return null;
    });
    public static Plugin Warden = Fake("RealmWarden", (h, a) => h == "IsNewPlayerProtected" ? (object)Protected.Contains((ulong)a[0]) : null);
    public static Plugin Herald = Fake("RealmHerald", (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null);
    public static Plugin Sentinel = Fake("RealmSentinel", (h, a) => { if (h == "SentinelItemSource") ItemSources.Add(a[0] + "|" + a[1]); return null; });
    public static Plugin Painter = Fake("RealmPainter", (h, a) => { if (h == "RefreshBoards") Refreshes.Add(Convert.ToString(a[0])); return null; });
    public static Plugin Sculptor = Fake("RealmSculptor", (h, a) =>
    {
        if (h == "PlaceSculptureAt") { if (SculptorRefuses) return 0; int id = NextPlacement++; Placed.Add(id + "|" + a[0] + "|" + a[4]); return id; }
        if (h == "RemoveSculpture") { Removed.Add(Convert.ToString(a[0])); return true; }
        return null;
    });
    public static Plugin Travel = Fake("RealmTravel", (h, a) =>
    {
        if (h == "IsTravelling") return Travelling.Contains((string)a[0]);
        if (h == "CancelJourney") { Cancelled.Add((string)a[0]); return Travelling.Remove((string)a[0]); }
        return null;
    });
    public static Plugin Quests = Fake("RealmQuests", (h, a) => { if (h == "ReportQuestEvent") { QuestEvents.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; } return null; });
    public static Plugin Crown = Fake("CrownAndConsequences", (h, a) =>
    {
        if (h == "GetKingName") return King;
        if (h == "GetKingHouse") return KingHouse;
        return null;
    });

    static readonly string[][] Refs =
    {
        new[] { "RealmTreasury", "Treasury" }, new[] { "RealmRenown", "Renown" }, new[] { "RealmSeasons", "Seasons" }, new[] { "RealmChronicle", "Chronicle" },
        new[] { "RealmHouses", "Houses" }, new[] { "RealmEvents", "Events" }, new[] { "RealmWarden", "Warden" }, new[] { "RealmHerald", "Herald" },
        new[] { "RealmSentinel", "Sentinel" }, new[] { "RealmPainter", "Painter" }, new[] { "RealmSculptor", "Sculptor" }, new[] { "RealmTravel", "Travel" },
        new[] { "RealmQuests", "Quests" }, new[] { "CrownAndConsequences", "Crown" },
    };

    // A plugin with the default config (tweak changes it before Init), wired to the fakes, loaded and started.
    public static RealmWorld NewWorld(Action<object> tweak = null)
    {
        var p = new RealmWorld();
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmWorld).GetNestedType("PluginConfig", BF);
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
        string json = T.Config.Json;
        Inv(T, "Unload");
        var old = T;
        NewWorld(tweak);
        foreach (var g in grants) T.permission.Grants.Add(g);
    }

    public static Player Mk(ulong id, string name, float x = 0, float z = 0, string house = null)
    {
        var p = new Player(id, name);
        p.Entity.Position = new UnityEngine.Vector3(x, 10, z);
        p.Entity.NetViewID = id % 100000;
        if (house != null) HouseOf[id] = house;
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        if (T != null) Inv(T, "OnPlayerConnected", p);
        return p;
    }
    public static void Offline(Player p) { Inv(T, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(T, "OnPlayerConnected", p); }
    public static void Admin(Player p) { T.permission.Grants.Add(p.Id + "|realmworld.admin"); }
    public static void At(Player p, float x, float z, float y = 10) { p.Entity.Position = new UnityEngine.Vector3(x, y, z); }

    static readonly Dictionary<string, string> CmdMethod = new Dictionary<string, string>
    { { "world", "CmdWorld" }, { "treasure", "CmdTreasure" }, { "caravan", "CmdCaravan" }, { "festival", "CmdFestival" } };
    public static string Cmd(Player p, string cmd, params string[] args)
    {
        int before = p.Messages.Count;
        Inv(T, CmdMethod[cmd], p, cmd, args);
        return string.Join("\n", p.Messages.Skip(before));
    }
    public static string AdminCmd(Player p, params string[] args) { return Cmd(p, "world", new[] { "admin" }.Concat(args).ToArray()); }

    // Seconds of server time, one tick every 5 s (the default TickSeconds).
    public static void Tick(int seconds = 5)
    {
        for (int i = 0; i < seconds; i += 5) { Clock = Clock.AddSeconds(5); Inv(T, "SafeTick"); }
    }
    public static void Minutes(int m) { Tick(m * 60); }
    public static void RunTimers() { T.timer.RunPending(); }

    // Creatures and chests in the world.
    static ulong nextView = 900000;
    public static Entity Creature(string label, float x, float z)
    {
        var e = new Entity { IsPlayer = false, Label = label, Position = new UnityEngine.Vector3(x, 10, z), NetViewID = nextView++ };
        e.Components.Add(new MonsterEntity());
        e.Components.Add(new MonsterMotor());
        Entity.World.Add(e);
        return e;
    }
    public static Entity Chest(float x, float z, float y = 10)
    {
        var e = new Entity { IsPlayer = false, Label = "chest_wood", Position = new UnityEngine.Vector3(x, y, z), NetViewID = nextView++ };
        e.Components.Add(new InteractableContainer());
        Entity.World.Add(e);
        return e;
    }
    public static int ChestCount(Entity chest, string item) { return ItemCollection.AutoCount(chest.TryGet<InteractableContainer>().Contents, InvBlueprints.Get(item)); }

    public static float Hit(Player attacker, Entity target, float amount = 10f)
    {
        var evt = new EntityDamageEvent { Entity = target, Damage = new Damage { Amount = amount, DamageSource = attacker.Entity } };
        Inv(T, "OnEntityHealthChange", evt);
        return evt.Cancelled ? 0f : evt.Damage.Amount;
    }
    public static float Bite(Entity beast, Player victim, float amount = 10f)
    {
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = beast } };
        Inv(T, "OnEntityHealthChange", evt);
        return evt.Damage.Amount;
    }
    public static void Kill(Player killer, Player victim) { Inv(T, "OnEntityDeath", new EntityDeathEvent { Entity = victim.Entity, KillingDamage = new Damage { DamageSource = killer == null ? null : killer.Entity } }); }
    public static void KillBy(Entity source, Player victim) { Inv(T, "OnEntityDeath", new EntityDeathEvent { Entity = victim.Entity, KillingDamage = new Damage { DamageSource = source } }); }
    public static void Slay(Player killer, Entity creature) { Inv(T, "OnEntityDeath", new EntityDeathEvent { Entity = creature, KillingDamage = new Damage { DamageSource = killer == null ? null : killer.Entity } }); Entity.World.Remove(creature); }
    public static bool Use(Player p, Entity thing)
    {
        var evt = new InteractEvent { Entity = thing, ControllerEntity = p.Entity, Sender = p };
        Inv(T, "OnPlayerInteract", evt);
        return !evt.Cancelled;
    }

    public static int Count(Player p, string item) { return ItemCollection.AutoCount(p.Inventory.Contents, InvBlueprints.Get(item)); }
    public static void Give(Player p, string item, int n) { var bp = InvBlueprints.Get(item); p.Inventory.Contents.Counts[bp.Name] = Count(p, item) + n; }
    public static long Purse(Player p) { long m; return Purses.TryGetValue(p.Id.ToString(), out m) ? m : 0; }
    public static long OwedMarks(Player p) { var d = (System.Collections.IDictionary)D("OwedMarks"); return d.Contains(p.Id.ToString()) ? (long)d[p.Id.ToString()] : 0; }
    public static int OwedUnits(Player p)
    {
        var d = (System.Collections.IDictionary)D("OwedItems");
        if (!d.Contains(p.Id.ToString())) return 0;
        return ((System.Collections.IList)d[p.Id.ToString()]).Cast<object>().Sum(o => (int)F(o, "Amount"));
    }

    // Lay out a treasure hunt with places at the given (x, z) points: the admin stands at each and adds its riddle.
    public static void LayHunt(Player admin, string id, string name, params float[] xz)
    {
        AdminCmd(admin, new[] { "hunt", "new", id }.Concat(name.Split(' ')).ToArray());
        var old = admin.Entity.Position;
        for (int i = 0; i < xz.Length; i += 2)
        {
            At(admin, xz[i], xz[i + 1]);
            AdminCmd(admin, "hunt", "step", id, "Riddle", "number", (i / 2 + 1).ToString(), "for", "the", "seeker");
        }
        admin.Entity.Position = old;
    }
    public static void Place(Player admin, string id, float x, float z, string name = null)
    {
        var old = admin.Entity.Position;
        At(admin, x, z);
        if (name == null) AdminCmd(admin, "place", "set", id); else AdminCmd(admin, new[] { "place", "set", id, "20" }.Concat(name.Split(' ')).ToArray());
        admin.Entity.Position = old;
    }

    public static void SetEventsActive(string kind, DateTime start, DateTime end) { EventsActive.Clear(); EventsActive.Add(kind + "|" + Iso(start) + "|" + Iso(end)); }
}
