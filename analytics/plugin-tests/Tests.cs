// RealmStats behaviour tests. Compiles plugins/RealmStats.cs UNCHANGED with Mocks.cs and drives it through its hooks,
// timer tick and chat command, then checks the data files it writes. Optionally exports a simulated multi-day data
// set (RS_EXPORT=<dir>) that the Node dashboard tests read, so the plugin's real output format is what gets aggregated.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Plugins;

public static class Tests
{
    static int pass, fail;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;

    static bool IsHex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }

    static void Check(bool ok, string what)
    {
        if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + what); }
    }

    // ---- harness -------------------------------------------------------------------------------------------------
    static string dataDir;
    static string configJson;                 // survives "restarts" like oxide/config/RealmStats.json
    static Dictionary<ulong, string> houses = new Dictionary<ulong, string>();
    static bool realmHousesLoaded = true;

    static object Call(RealmStats p, string name, params object[] args)
    {
        MethodInfo m = typeof(RealmStats).GetMethod(name, BF);
        if (m == null) throw new Exception("no method " + name);
        return m.Invoke(p, args);
    }
    static T Get<T>(RealmStats p, string field) { return (T)typeof(RealmStats).GetField(field, BF).GetValue(p); }
    static void Set(RealmStats p, string field, object v) { typeof(RealmStats).GetField(field, BF).SetValue(p, v); }

    static DateTime clock;
    static void SetTime(RealmStats p, DateTime t)
    {
        clock = t;
        long real = (long)(DateTime.UtcNow - Epoch).TotalSeconds;
        long want = (long)(t - Epoch).TotalSeconds;
        Set(p, "clockOffset", want - real);
    }
    static void Advance(RealmStats p, double seconds) { SetTime(p, clock.AddSeconds(seconds)); }

    static RealmStats Boot(DateTime t, bool serverInit = true)
    {
        Interface.Oxide.DataFileSystem.Dir = dataDir;
        var p = new RealmStats();
        p.Name = "RealmStats";
        var rh = new Oxide.Core.Plugins.Plugin { Name = "RealmHouses" };
        rh.Handler = (hook, a) =>
        {
            if (hook != "GetHouse") return null;
            string h; return houses.TryGetValue(ulong.Parse((string)a[0]), out h) ? h : null;
        };
        if (realmHousesLoaded) Set(p, "RealmHouses", rh);
        if (configJson == null) Call(p, "LoadDefaultConfig");
        else p.Config.Json = configJson;
        Call(p, "LoadDefaultMessages");
        SetClockBeforeInit(p, t);
        Call(p, "Init");
        configJson = p.Config.Json;
        if (serverInit) Call(p, "OnServerInitialized");
        return p;
    }
    static void SetClockBeforeInit(RealmStats p, DateTime t) { SetTime(p, t); }

    static void Join(RealmStats p, Player pl) { if (!Server.ClientPlayers.Contains(pl)) Server.ClientPlayers.Add(pl); Call(p, "OnPlayerConnected", pl); }
    static void Leave(RealmStats p, Player pl) { Server.ClientPlayers.Remove(pl); Call(p, "OnPlayerDisconnected", pl); }
    static void Tick(RealmStats p) { Call(p, "Tick"); }
    static void Cmd(RealmStats p, Player pl, params string[] args) { Call(p, "CmdStats", pl, "stats", args); }
    static void Kill(RealmStats p, Player victim, Player killer, DamageType t, bool creature = false)
    {
        Entity src = killer != null ? killer.Entity : (creature ? new Entity { IsPlayer = false } : null);
        var evt = new EntityDeathEvent { Entity = victim.Entity, KillingDamage = new Damage { DamageSource = src, DamageTypes = t } };
        object r = Call(p, "OnEntityDeath", evt);
        Check(r == null, "OnEntityDeath returns null");
    }

    static string DayPath(string date, int suffix = 0) { return Path.Combine(dataDir, "RealmStats", "day-" + date + (suffix > 0 ? "-r" + suffix : "") + ".json"); }
    static JsonElement ReadJson(string path) { return JsonDocument.Parse(File.ReadAllText(path)).RootElement; }
    static List<string> Strs(JsonElement e) { return e.EnumerateArray().Select(x => x.GetString()).ToList(); }
    static int Sum(JsonElement arr) { return arr.EnumerateArray().Sum(x => x.GetInt32()); }
    static string KeyOf(RealmStats p, Player pl) { return (string)Call(p, "KeyOf", pl.Id); }

    static void Fresh()
    {
        dataDir = Path.Combine(Path.GetTempPath(), "realmstats-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        configJson = null;
        houses.Clear();
        realmHousesLoaded = true;
        Server.ClientPlayers.Clear();
    }

    // ---- tests ---------------------------------------------------------------------------------------------------
    public static int Main()
    {
        TestBasics();
        TestRolloverAndRetentionData();
        TestOptOutAndScrub();
        TestCorruptState();
        TestCorruptDay();
        TestCrashRecoveryAndHotReload();
        TestPruneAndCaps();
        TestCommandsAndPermissions();
        TestSaltChangeAndBadConfig();
        TestGuildFallback();
        string export = Environment.GetEnvironmentVariable("RS_EXPORT");
        if (!string.IsNullOrEmpty(export)) ExportSimulation(export);
        Console.WriteLine((fail == 0 ? "OK" : "FAILED") + ": " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static void TestBasics()
    {
        Fresh();
        var t0 = new DateTime(2026, 9, 1, 18, 0, 30, DateTimeKind.Utc);
        var p = Boot(t0);
        var cfg = JsonDocument.Parse(configJson).RootElement;
        string salt = cfg.GetProperty("Salt").GetString();
        Check(salt.Length == 64 && salt.All(IsHex), "first load generates a 64-hex secret salt");
        Check(p.Log.Any(l => l.Contains("Generated")) == false, "default config salt is used as is (no regeneration)");

        var a = new Player(76561198000000001, "Aldric"); houses[a.Id] = "Varrow";
        var b = new Player(76561198000000002, "Brienne"); houses[b.Id] = "Ashgrove";
        var srv = new Player(9999999999, "Server") { IsServer = true };
        Join(p, a); Join(p, b); Call(p, "OnPlayerConnected", srv);
        string ka = KeyOf(p, a);
        Check(ka.Length == 16 && ka.All(IsHex), "player key is 16 hex chars");
        Check(!ka.Contains("76561198") && ka != KeyOf(p, b), "key does not contain the Steam id and differs per player");
        Check(a.All().Contains("/stats privacy"), "first join shows the privacy notice");

        Advance(p, 300); Tick(p);          // sample + accrue
        Advance(p, 60);
        Kill(p, b, a, DamageType.Melee);                   // pvp
        Kill(p, b, a, DamageType.Melee);                   // duplicate within 3 s: ignored
        Advance(p, 10); Kill(p, a, null, DamageType.Falling);
        Advance(p, 10); Kill(p, a, null, DamageType.Melee, creature: true);
        Advance(p, 10); Kill(p, a, null, DamageType.Hunger);
        Advance(p, 10); Kill(p, a, a, DamageType.Suicide); // own source: not pvp
        Advance(p, 200); Tick(p);                          // second sample
        Leave(p, b);
        Advance(p, 2); Call(p, "OnPlayerConnected", b); Server.ClientPlayers.Add(b); Advance(p, 1); Leave(p, b); // 1 s session
        Call(p, "SaveAll");

        string date = "2026-09-01";
        Check(File.Exists(DayPath(date)), "day file is written as RealmStats/day-YYYY-MM-DD.json");
        var d = ReadJson(DayPath(date));
        string raw = File.ReadAllText(DayPath(date));
        Check(!raw.Contains("Aldric") && !raw.Contains("76561198000000001"), "day file has no names or Steam ids");
        Check(Strs(d.GetProperty("Active")).Count == 2 && Strs(d.GetProperty("New")).Count == 2, "two active, two new");
        Check(d.GetProperty("Joins")[18].GetInt32() == 3 && Sum(d.GetProperty("Joins")) == 3, "joins counted in UTC hour 18 (server excluded)");
        Check(Sum(d.GetProperty("Leaves")) == 2, "leaves counted");
        var deaths = d.GetProperty("Deaths");
        Check(deaths.GetProperty("pvp").GetInt32() == 1, "pvp death counted once (dedupe)");
        Check(deaths.GetProperty("fall").GetInt32() == 1 && deaths.GetProperty("creature").GetInt32() == 1
              && deaths.GetProperty("hunger").GetInt32() == 1 && deaths.GetProperty("suicide").GetInt32() == 1, "death causes classified");
        var conc = d.GetProperty("Concurrency");
        Check(conc.GetArrayLength() == 2 && conc[0].GetProperty("N").GetInt32() == 2, "concurrency sampled every 5 min");
        long t0b = (long)(t0 - Epoch).TotalSeconds;
        Check(conc[0].GetProperty("T").GetInt64() % 300 == 0, "sample time is aligned to the bucket");
        var sessions = d.GetProperty("Sessions");
        Check(sessions.GetArrayLength() == 1, "one session recorded; the 1-second session is dropped");
        Check(sessions[0].GetProperty("D").GetInt32() > 500 && sessions[0].GetProperty("E").GetString() == "leave", "session length and end reason");
        Check(!sessions[0].ToString().Contains(KeyOf(p, b)), "sessions carry no key");
        Check(d.GetProperty("Dropped").GetProperty("short_sessions").GetInt32() == 1, "short session counted as dropped");
        var hv = d.GetProperty("Houses").GetProperty("Varrow");
        var hs = d.GetProperty("Houses").GetProperty("Ashgrove");
        Check(hv.GetProperty("Kills").GetInt32() == 1 && hs.GetProperty("Deaths").GetInt32() == 1, "house kills and deaths");
        Check(hv.GetProperty("Deaths").GetInt32() == 4, "house deaths include environmental deaths");
        Check(hs.GetProperty("Sessions").GetInt32() == 2 && hv.GetProperty("Sessions").GetInt32() == 1, "house sessions");
        Check(hv.GetProperty("Seconds").GetInt64() >= 500, "house online seconds accrue");
        Check(Strs(hv.GetProperty("Active")).SequenceEqual(new[] { ka }), "house active keys");
        Check(d.GetProperty("SaltId").GetString().Length == 8 && d.GetProperty("Degraded").GetBoolean() == false, "salt id and health");
        var st = ReadJson(Path.Combine(dataDir, "RealmStats", "state.json"));
        Check(st.GetProperty("Players").EnumerateObject().Count() == 2, "state keeps first-seen per key");
        Check(st.GetProperty("Open").EnumerateObject().Count() == 1, "state keeps one open session (Aldric)");
        var sum = (Dictionary<string, object>)Call(p, "GetStatsSummary");
        Check((int)sum["online"] == 1 && (int)sum["active"] == 2 && (int)sum["peak"] == 2, "GetStatsSummary API");
    }

    static void TestRolloverAndRetentionData()
    {
        Fresh();
        var p = Boot(new DateTime(2026, 9, 1, 23, 50, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000011, "A"); var b = new Player(76561198000000012, "B");
        houses[a.Id] = "Corvane";
        Join(p, a); Join(p, b);
        Advance(p, 300); Tick(p);
        Leave(p, b);
        Advance(p, 600); Tick(p);           // now 2026-09-02 00:05:30 -> rollover
        Check(File.Exists(DayPath("2026-09-01")) && File.Exists(DayPath("2026-09-02")), "rollover writes yesterday and opens today");
        var d2 = ReadJson(DayPath("2026-09-02"));
        Check(Strs(d2.GetProperty("Active")).Contains(KeyOf(p, a)), "player online across midnight is active on the new day");
        Check(Strs(d2.GetProperty("New")).Count == 0, "...but not new");
        Check(Strs(d2.GetProperty("Houses").GetProperty("Corvane").GetProperty("Active")).Count == 1, "house activity carries over midnight");
        Join(p, b);
        Call(p, "SaveAll");
        d2 = ReadJson(DayPath("2026-09-02"));
        Check(Strs(d2.GetProperty("Active")).Count == 2 && Strs(d2.GetProperty("New")).Count == 0, "returning player is active, not new (D1 data)");
        var st = ReadJson(Path.Combine(dataDir, "RealmStats", "state.json"));
        Check(Strs(st.GetProperty("Days")).Count == 2, "state indexes both day files");
    }

    static void TestOptOutAndScrub()
    {
        Fresh();
        var p = Boot(new DateTime(2026, 9, 1, 12, 0, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000021, "A"); houses[a.Id] = "Merrin";
        var b = new Player(76561198000000022, "B");
        Join(p, a); Join(p, b);
        string ka = KeyOf(p, a);
        Advance(p, 86400); Tick(p);          // next day; both still online
        Advance(p, 86400); Tick(p);          // third day
        Call(p, "SaveAll");
        Check(File.ReadAllText(DayPath("2026-09-01")).Contains(ka), "key present in old file before opt-out");
        Cmd(p, a, "optout");
        Check(a.All().Contains("Done."), "opt-out confirmed");
        var d3 = ReadJson(DayPath("2026-09-03"));
        Check(!Strs(d3.GetProperty("Active")).Contains(ka), "opt-out removes key from today immediately");
        for (int i = 0; i < 5; i++) { Advance(p, 15); Tick(p); }
        Call(p, "SaveAll");
        Check(!File.ReadAllText(DayPath("2026-09-01")).Contains(ka) && !File.ReadAllText(DayPath("2026-09-02")).Contains(ka), "scrub removes key from older day files");
        Check(File.ReadAllText(DayPath("2026-09-01")).Contains(KeyOf(p, b)), "scrub leaves other keys");
        var st = File.ReadAllText(Path.Combine(dataDir, "RealmStats", "state.json"));
        var stj = JsonDocument.Parse(st).RootElement;
        Check(stj.GetProperty("ScrubQueue").GetArrayLength() == 0 && !stj.GetProperty("Players").TryGetProperty(ka, out _), "scrub queue drained, first-seen removed");
        Leave(p, a); Join(p, a);
        Advance(p, 300); Tick(p); Call(p, "SaveAll");
        d3 = ReadJson(DayPath("2026-09-03"));
        Check(!d3.ToString().Contains(ka), "opted-out player's rejoin and house time are not keyed");
        Check(Sum(d3.GetProperty("Joins")) >= 1, "opted-out player still counts in anonymous joins");
        Cmd(p, a, "optout");
        Check(a.All().Contains("already opted out"), "second opt-out is idempotent");
        Advance(p, 61); a.Messages.Clear(); Cmd(p, a, "optin");
        Check(a.All().Contains("Welcome back"), "opt-in works");
        Advance(p, 5); a.Messages.Clear(); Cmd(p, a, "optout");
        Check(a.All().Contains("wait"), "opt toggle cooldown blocks a quick flip back");
        Call(p, "SaveAll");
        Check(Strs(ReadJson(DayPath("2026-09-03")).GetProperty("New")).Contains(ka), "opted-in player is counted as new again");
    }

    static void TestCorruptState()
    {
        Fresh();
        Directory.CreateDirectory(Path.Combine(dataDir, "RealmStats"));
        string sp = Path.Combine(dataDir, "RealmStats", "state.json");
        File.WriteAllText(sp, "{ this is not json");
        var p = Boot(new DateTime(2026, 9, 5, 10, 0, 30, DateTimeKind.Utc));
        Check(p.Log.Any(l => l.StartsWith("ERROR") && l.Contains("NOT be overwritten")), "corrupt state is reported");
        var a = new Player(76561198000000031, "A");
        Join(p, a); Advance(p, 300); Tick(p); Call(p, "SaveAll"); Call(p, "Unload");
        Check(File.ReadAllText(sp) == "{ this is not json", "corrupt state.json is never overwritten");
        var d = ReadJson(DayPath("2026-09-05"));
        Check(d.GetProperty("Degraded").GetBoolean() && Strs(d.GetProperty("New")).Count == 0 && Strs(d.GetProperty("Active")).Count == 1,
              "degraded day: active kept, new unknown");
        var admin = new Player(76561198000000039, "Adm");
        p.permission.Grants.Add(admin.Id + "|realmstats.admin");
        Cmd(p, admin, "status");
        Check(admin.All().Contains("DEGRADED"), "status shows degraded data");
    }

    static void TestCorruptDay()
    {
        Fresh();
        Directory.CreateDirectory(Path.Combine(dataDir, "RealmStats"));
        File.WriteAllText(DayPath("2026-09-06"), "[1,2,");
        var p = Boot(new DateTime(2026, 9, 6, 10, 0, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000041, "A");
        Join(p, a); Call(p, "SaveAll");
        Check(File.ReadAllText(DayPath("2026-09-06")) == "[1,2,", "corrupt day file is kept unchanged");
        Check(File.Exists(DayPath("2026-09-06", 1)), "today's data goes to day-...-r1");
        Check(Strs(ReadJson(DayPath("2026-09-06", 1)).GetProperty("Active")).Count == 1, "recovery file holds today's data");
        // A restart the same day reads the recovery file and keeps adding to it.
        Call(p, "Unload");
        var p2 = Boot(new DateTime(2026, 9, 6, 11, 0, 30, DateTimeKind.Utc));
        var b = new Player(76561198000000042, "B");
        Join(p2, b); Call(p2, "SaveAll");
        Check(Strs(ReadJson(DayPath("2026-09-06", 1)).GetProperty("Active")).Count == 2, "restart continues the recovery file");
    }

    static void TestCrashRecoveryAndHotReload()
    {
        Fresh();
        var p = Boot(new DateTime(2026, 9, 7, 20, 0, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000051, "A"); houses[a.Id] = "Dunmere";
        Join(p, a);
        Advance(p, 600); Tick(p); Call(p, "SaveAll");   // heartbeat at +600
        // Hot reload while A is online: the session continues.
        Call(p, "Unload");
        var p2 = Boot(clock.AddSeconds(30));
        Check(Get<System.Collections.IDictionary>(p2, "online").Count == 1, "hot reload resumes online player");
        Advance(p2, 300); Leave(p2, a); Call(p2, "SaveAll");
        var d = ReadJson(DayPath("2026-09-07"));
        var s = d.GetProperty("Sessions");
        Check(s.GetArrayLength() == 1 && s[0].GetProperty("D").GetInt32() >= 900, "resumed session keeps its original start");
        Check(d.GetProperty("Houses").GetProperty("Dunmere").GetProperty("Sessions").GetInt32() == 1, "resumed session is not counted twice");
        // Crash: A joins, a save happens, then the process dies without Unload.
        Join(p2, a); Advance(p2, 400); Tick(p2); Call(p2, "SaveAll");
        long hb = (long)(clock - Epoch).TotalSeconds;
        Server.ClientPlayers.Clear();
        var p3 = Boot(clock.AddSeconds(3600));
        d = ReadJson(DayPath("2026-09-07"));
        Call(p3, "SaveAll");
        d = ReadJson(DayPath("2026-09-07"));
        var last = d.GetProperty("Sessions").EnumerateArray().Last();
        Check(last.GetProperty("E").GetString() == "recovered" && last.GetProperty("S").GetInt64() + last.GetProperty("D").GetInt32() == hb,
              "crashed session is closed at the last heartbeat");
        var st = ReadJson(Path.Combine(dataDir, "RealmStats", "state.json"));
        Check(st.GetProperty("Open").EnumerateObject().Count() == 0, "no open sessions left after recovery");
        // Shutdown closes sessions with reason shutdown.
        Join(p3, a); Advance(p3, 120); Call(p3, "OnServerShutdown"); Call(p3, "Unload");
        d = ReadJson(DayPath("2026-09-07"));
        Check(d.GetProperty("Sessions").EnumerateArray().Last().GetProperty("E").GetString() == "shutdown", "shutdown ends sessions");
        Server.ClientPlayers.Clear();
    }

    static void TestPruneAndCaps()
    {
        Fresh();
        var p = Boot(new DateTime(2026, 1, 1, 12, 0, 30, DateTimeKind.Utc));
        var cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson);
        // Reboot with a short retention and a house cap of 2.
        var cfgObj = JsonDocument.Parse(configJson).RootElement;
        string newCfg = configJson.Replace("\"RetentionDays\": 120", "\"RetentionDays\": 7").Replace("\"MaxHousesPerDay\": 100", "\"MaxHousesPerDay\": 2");
        Check(newCfg != configJson, "config edit applied");
        configJson = newCfg;
        Call(p, "Unload");
        p = Boot(new DateTime(2026, 1, 1, 12, 0, 30, DateTimeKind.Utc));
        var pl = new List<Player>();
        string[] names = { "Varrow", "Ashgrove", "Corvane", "Dunmere" };
        for (int i = 0; i < 4; i++) { var x = new Player(76561198000000100 + (ulong)i, "P" + i); houses[x.Id] = names[i]; pl.Add(x); Join(p, x); }
        Call(p, "SaveAll");
        var d = ReadJson(DayPath("2026-01-01"));
        var hk = d.GetProperty("Houses").EnumerateObject().Select(o => o.Name).ToList();
        Check(hk.Count == 3 && hk.Contains("(other)"), "houses past the cap fold into (other) (" + string.Join(",", hk) + ")");
        Check(Strs(d.GetProperty("Houses").GetProperty("(other)").GetProperty("Active")).Count == 2, "(other) tracks the active keys of both folded houses");
        for (int day = 1; day <= 10; day++) { Advance(p, 86400); Tick(p); }
        Check(!File.Exists(DayPath("2026-01-01")) && !File.Exists(DayPath("2026-01-03")), "day files older than RetentionDays are deleted");
        Check(File.Exists(DayPath("2026-01-04")) && File.Exists(DayPath("2026-01-11")), "recent day files are kept");
        var st = ReadJson(Path.Combine(dataDir, "RealmStats", "state.json"));
        Check(Strs(st.GetProperty("Days")).Count == 8, "index matches kept files");
        foreach (var x in pl) Leave(p, x);
    }

    static void TestCommandsAndPermissions()
    {
        Fresh();
        var p = Boot(new DateTime(2026, 9, 8, 9, 0, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000061, "A");
        Join(p, a);
        a.Messages.Clear(); Cmd(p, a);
        Check(a.All().Contains("/stats privacy") && !a.All().Contains("Admin:"), "help for players");
        Advance(p, 5); a.Messages.Clear(); Cmd(p, a, "status");
        Check(a.All().Contains("may not"), "status needs realmstats.admin");
        Advance(p, 5); a.Messages.Clear(); Cmd(p, a, "save");
        Check(a.All().Contains("may not"), "save needs realmstats.admin");
        a.Messages.Clear(); Cmd(p, a, "me");
        Check(a.All().Contains("wait"), "player command cooldown");
        Advance(p, 5); a.Messages.Clear(); Cmd(p, a, "me");
        Check(a.All().Contains(KeyOf(p, a).Substring(0, 6)) && a.All().Contains("2026-09-08"), "/stats me shows key prefix and first seen");
        Advance(p, 5); a.Messages.Clear(); Cmd(p, a, "privacy");
        Check(a.All().Contains("Never recorded") && a.All().Contains("120 days"), "/stats privacy");
        var adm = new Player(76561198000000069, "Adm");
        p.permission.Grants.Add(adm.Id + "|realmstats.admin");
        Cmd(p, adm, "help");
        Check(adm.All().Contains("Admin:"), "admin help");
        adm.Messages.Clear(); Cmd(p, adm, "status");
        Check(adm.All().Contains("online 1") && adm.All().Contains("data OK"), "admin status");
        adm.Messages.Clear(); Cmd(p, adm, "save");
        Check(adm.All().Contains("Saved"), "admin save");
        adm.Messages.Clear(); Cmd(p, adm, "save");
        Check(adm.All().Contains("wait"), "admin save cooldown");
        Leave(p, a);
    }

    static void TestSaltChangeAndBadConfig()
    {
        Fresh();
        var p = Boot(new DateTime(2026, 9, 9, 9, 0, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000071, "A");
        Join(p, a); string k1 = KeyOf(p, a); Leave(p, a); Call(p, "Unload");
        var cfg = JsonDocument.Parse(configJson).RootElement;
        configJson = configJson.Replace(cfg.GetProperty("Salt").GetString(), new string('a', 64));
        p = Boot(new DateTime(2026, 9, 9, 10, 0, 30, DateTimeKind.Utc));
        Check(p.Log.Any(l => l.Contains("salt in the config changed")), "salt change is detected");
        Join(p, a);
        Check(KeyOf(p, a) != k1, "new salt gives a new key");
        Call(p, "SaveAll");
        var st = ReadJson(Path.Combine(dataDir, "RealmStats", "state.json"));
        Check(st.GetProperty("Players").EnumerateObject().Count() == 1, "first-seen table restarts on salt change");
        Leave(p, a); Call(p, "Unload");

        // Placeholder salt is replaced and saved.
        configJson = configJson.Replace(new string('a', 64), "CHANGE_ME");
        p = Boot(new DateTime(2026, 9, 9, 11, 0, 30, DateTimeKind.Utc));
        Check(!configJson.Contains("CHANGE_ME") && p.Log.Any(l => l.Contains("Generated a new secret salt")), "placeholder salt is replaced");
        Call(p, "Unload");

        // Broken config: nothing collected, config not rewritten.
        configJson = "{ broken";
        p = Boot(new DateTime(2026, 9, 9, 12, 0, 30, DateTimeKind.Utc));
        Check(p.Config.Writes == 0 && configJson == "{ broken", "broken config is not rewritten");
        int before = Directory.GetFiles(Path.Combine(dataDir, "RealmStats")).Length;
        string dayBefore = File.ReadAllText(DayPath("2026-09-09"));
        Join(p, a); Advance(p, 300); Tick(p); Call(p, "SaveAll");
        Check(File.ReadAllText(DayPath("2026-09-09")) == dayBefore, "nothing written while the config is broken");
        a.Messages.Clear(); Cmd(p, a, "optout");
        Check(a.All().Contains("paused"), "players are told collection is paused");
        Leave(p, a);
    }

    static void TestGuildFallback()
    {
        Fresh();
        realmHousesLoaded = false;
        var p = Boot(new DateTime(2026, 9, 10, 9, 0, 30, DateTimeKind.Utc));
        var a = new Player(76561198000000081, "A") { Guild = new Guild { Name = "[FF0000]Halloran\n" } };
        Join(p, a); Call(p, "SaveAll");
        var d = ReadJson(DayPath("2026-09-10"));
        Check(d.GetProperty("Houses").TryGetProperty("FF0000Halloran", out _) || d.GetProperty("Houses").TryGetProperty("Halloran", out _),
              "without RealmHouses the game guild name is used (cleaned)");
        Leave(p, a);
    }

    // ---- simulation export (feeds analytics/test) ---------------------------------------------------------------
    static void ExportSimulation(string outDir)
    {
        Fresh();
        var rnd = new Random(7);
        var p = Boot(new DateTime(2026, 9, 14, 0, 0, 30, DateTimeKind.Utc));
        string[] hs = { "Varrow", "Ashgrove", "Corvane", "Dunmere", "Halloran", "Merrin" };
        var pool = new List<Player>();
        for (int i = 0; i < 60; i++)
        {
            var x = new Player(76561198100000000 + (ulong)i, "P" + i);
            if (i % 5 != 4) houses[x.Id] = hs[i % hs.Length];
            pool.Add(x);
        }
        // 14 days, 15-minute steps; players arrive in waves and come back with decaying probability.
        for (int step = 0; step < 14 * 96; step++)
        {
            DateTime now = clock;
            int hour = now.Hour;
            int dayIdx = step / 96;
            double prime = (hour >= 18 && hour <= 22) ? 0.35 : (hour >= 12 ? 0.12 : 0.03);
            for (int i = 0; i < pool.Count; i++)
            {
                var x = pool[i];
                bool on = Server.ClientPlayers.Contains(x);
                int arrival = i / 5;                       // a new wave of 5 every day
                if (arrival > dayIdx) continue;
                double loyalty = (i % 3 == 0) ? 1.0 : 0.35;
                if (!on && rnd.NextDouble() < prime * loyalty) Join(p, x);
                else if (on && rnd.NextDouble() < 0.25) Leave(p, x);
            }
            if (Server.ClientPlayers.Count > 1 && rnd.NextDouble() < 0.3)
            {
                var v = Server.ClientPlayers[rnd.Next(Server.ClientPlayers.Count)];
                var k = Server.ClientPlayers[rnd.Next(Server.ClientPlayers.Count)];
                DamageType[] causes = { DamageType.Melee, DamageType.Falling, DamageType.Hunger, DamageType.Fire };
                var c = causes[rnd.Next(causes.Length)];
                Kill(p, v, c == DamageType.Melee ? k : null, c, creature: rnd.NextDouble() < 0.2);
            }
            for (int t = 0; t < 60; t++) { Advance(p, 15); Tick(p); }
        }
        foreach (var x in Server.ClientPlayers.ToList()) Leave(p, x);
        Call(p, "SaveAll");
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(Path.Combine(dataDir, "RealmStats")))
            File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), true);
        Console.WriteLine("exported simulation to " + outDir);
    }
}
