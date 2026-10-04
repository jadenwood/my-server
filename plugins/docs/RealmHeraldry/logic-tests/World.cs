// Shared test world for RealmHeraldry: players, game guilds with banners, and fake RealmHouses (with its data file),
// CrownAndConsequences (council seats and decree mandates), RealmChronicle, RealmTreasury (purses, holds and the crown's
// treasury, zero-sum), RealmSeasons, RealmRenown, RealmHerald, RealmWarden, RealmLaws, RealmContracts, RealmQuests and
// RealmTravel, and a clock the tests move. Used by the logic tests (plugins/docs/RealmHeraldry/logic-tests) and the
// exploit suite (tools/exploit-review/heraldry).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Thrones.Banner;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock;
    public static RealmHeraldry H;
    public static string Dir;

    public class HouseF { public string Name; public ulong Leader; public List<ulong> Members = new List<ulong>(); public DateTime Founded; public ulong GuildId; public Dictionary<ulong, DateTime> Joined = new Dictionary<ulong, DateTime>(); }
    public static List<HouseF> Houses = new List<HouseF>();
    public static GuildScheme Guilds;
    public static KingsScheme Kings;

    public static List<string> CrownSeats = new List<string>();
    public static List<string> Seated = new List<string>();
    public static bool SeatRefuses;
    public static List<string> Mandates = new List<string>();
    public static List<string> Logs = new List<string>();
    public static Dictionary<string, long> Purses = new Dictionary<string, long>();
    public class HoldF { public string Player; public long Marks; public string Source; }
    public static Dictionary<string, HoldF> Holds = new Dictionary<string, HoldF>();
    public static long TreasuryMarks;
    public static List<string> Charges = new List<string>();
    public static int Season;
    public static List<string> Awards = new List<string>();
    public static Dictionary<string, int> Renown = new Dictionary<string, int>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static HashSet<ulong> Protected = new HashSet<ulong>();
    public static HashSet<string> CourtOutlaws = new HashSet<string>(), Exiles = new HashSet<string>(), Outlaws = new HashSet<string>();
    public static List<string> ActiveLaws = new List<string>();
    public static List<string> QuestReports = new List<string>();
    public static Dictionary<string, int> Waystones = new Dictionary<string, int>();
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
    public static object D(string field) { return F(F(H, "data"), field); }
    public static object Cfg(string section) { return F(F(H, "config"), section); }
    public static void Set(string section, string field, object v) { SetF(Cfg(section), field, v); }
    public static void Section(object cfg, string section, string field, object v) { SetF(F(cfg, section), field, v); }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static long Total() { return Purses.Values.Sum() + Holds.Values.Sum(h => h.Marks) + TreasuryMarks; }

    public static void Clear()
    {
        Server.Broadcasts.Clear(); Charges.Clear(); Awards.Clear(); Logs.Clear(); QuestReports.Clear(); EventManager.Events.Clear();
        foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); }
    }

    public static void Reset()
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        Houses.Clear(); CrownSeats = new List<string> { "Voice of the Crown", "Keeper of Coin", "Marshal" }; Seated.Clear(); SeatRefuses = false;
        Mandates.Clear(); Logs.Clear(); Purses.Clear(); Holds.Clear(); TreasuryMarks = 0; Charges.Clear(); Season = 0; Awards.Clear();
        Renown.Clear(); PopupsOff.Clear(); Protected.Clear(); CourtOutlaws.Clear(); Exiles.Clear(); Outlaws.Clear(); ActiveLaws.Clear();
        QuestReports.Clear(); Waystones.Clear(); Absent.Clear();
        EventManager.Events.Clear(); EventManager.Refuse = false; EventManager.Throws = false;
        SocialAPI.Registry.Clear();
        Guilds = new GuildScheme(); Kings = new KingsScheme();
        SocialAPI.Registry[typeof(GuildScheme)] = Guilds;
        SocialAPI.Registry[typeof(KingsScheme)] = Kings;
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmheraldry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        H = null;
    }

    static Plugin Fake(string name, Func<string, object[], object> h) { return new Plugin { Name = name, Handler = h }; }
    public static HouseF HouseNamed(string n) { return Houses.FirstOrDefault(h => string.Equals(h.Name, n, StringComparison.OrdinalIgnoreCase)); }
    public static HouseF HouseOfId(ulong id) { return Houses.FirstOrDefault(h => h.Members.Contains(id)); }

    public static Plugin HousesP = Fake("RealmHouses", (m, a) =>
    {
        if (m == "GetHouse") { var h = HouseOfId(ulong.Parse((string)a[0])); return h != null ? h.Name : null; }
        if (m == "GetHouseLeader") { var h = HouseNamed((string)a[0]); return h != null && h.Leader != 0 ? h.Leader.ToString() : null; }
        if (m == "GetMembers") { var h = HouseNamed((string)a[0]); return h != null ? h.Members.Select(x => x.ToString()).ToList() : null; }
        if (m == "GetHouseFounded") { var h = HouseNamed((string)a[0]); return h != null ? h.Founded.ToString("o") : null; }
        if (m == "GetHouseSummaries")
            return Houses.Select(h => new Dictionary<string, object> { { "name", h.Name }, { "sigil", "x" }, { "liege", null }, { "members", h.Members.Count } }).ToList();
        return null;
    });
    public static Plugin Crown = Fake("CrownAndConsequences", (m, a) =>
    {
        if (m == "GetCouncilSeats") return new List<string>(CrownSeats);
        if (m == "SeatElectedCouncillor") { if (SeatRefuses) return false; Seated.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + a[3]); return true; }
        if (m == "GetDecreeList") return new[] { "open_roads|Open Roads", "royal_stores|Royal Stores", "tax_relief|Tax Relief" };
        if (m == "SetDecreeMandate") { Mandates.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; }
        return null;
    });
    public static Plugin Chronicle = Fake("RealmChronicle", (m, a) =>
    {
        if (m == "Log") { Logs.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + string.Join(",", (string[])a[3])); return 1; }
        return null;
    });
    public static Plugin Treasury = Fake("RealmTreasury", (m, a) =>
    {
        if (m == "ChargeMarks")
        {
            string id = (string)a[0]; long amount = (long)a[2];
            Charges.Add(id + "|" + amount + "|" + a[3] + "|" + a[4]);
            long p; Purses.TryGetValue(id, out p);
            if (amount <= 0 || p < amount) return false;
            Purses[id] = p - amount; TreasuryMarks += amount;
            return true;
        }
        if (m == "HoldMarks")
        {
            string hold = (string)a[0], id = (string)a[1]; long amount = (long)a[3];
            long p; Purses.TryGetValue(id, out p);
            if (Holds.ContainsKey(hold) || amount <= 0 || p < amount) return 0L;
            Purses[id] = p - amount; Holds[hold] = new HoldF { Player = id, Marks = amount, Source = (string)a[4] };
            return amount;
        }
        if (m == "ReleaseHold")
        {
            HoldF h;
            if (!Holds.TryGetValue((string)a[0], out h) || h.Source != (string)a[1]) return 0L;
            Holds.Remove((string)a[0]);
            long p; Purses.TryGetValue(h.Player, out p); Purses[h.Player] = p + h.Marks;
            return h.Marks;
        }
        return null;
    });
    public static Plugin Seasons = Fake("RealmSeasons", (m, a) =>
    {
        if (m == "GetSeasonNumber") return Season;
        if (m == "GetSeasonName") return Season > 0 ? "Season " + Season + ": The Hollow Crown" : null;
        if (m == "AwardHouse") { Awards.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; }
        return null;
    });
    public static Plugin RenownP = Fake("RealmRenown", (m, a) => { if (m == "GetRenown") { int r; return Renown.TryGetValue((string)a[0], out r) ? r : 0; } return null; });
    public static Plugin Herald = Fake("RealmHerald", (m, a) => m == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null);
    public static Plugin Warden = Fake("RealmWarden", (m, a) => m == "IsNewPlayerProtected" ? (object)Protected.Contains((ulong)a[0]) : null);
    public static Plugin Laws = Fake("RealmLaws", (m, a) =>
    {
        if (m == "IsCourtOutlaw") return CourtOutlaws.Contains((string)a[0]);
        if (m == "IsExiled") return Exiles.Contains((string)a[0]);
        if (m == "GetActiveLaws") return ActiveLaws.ToArray();
        return null;
    });
    public static Plugin Contracts = Fake("RealmContracts", (m, a) => m == "IsOutlaw" ? (object)Outlaws.Contains((string)a[0]) : null);
    public static Plugin Quests = Fake("RealmQuests", (m, a) =>
    {
        if (m == "ReportQuestEvent") { QuestReports.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + a[3]); return true; }
        return null;
    });
    public static Plugin Travel = Fake("RealmTravel", (m, a) => { if (m == "GetDiscoveredCount") { int n; return Waystones.TryGetValue((string)a[0], out n) ? n : 0; } return null; });

    static readonly string[][] Refs =
    {
        new[] { "RealmHouses", "HousesP" }, new[] { "CrownAndConsequences", "Crown" }, new[] { "RealmChronicle", "Chronicle" },
        new[] { "RealmTreasury", "Treasury" }, new[] { "RealmSeasons", "Seasons" }, new[] { "RealmRenown", "RenownP" },
        new[] { "RealmHerald", "Herald" }, new[] { "RealmWarden", "Warden" }, new[] { "RealmLaws", "Laws" },
        new[] { "RealmContracts", "Contracts" }, new[] { "RealmQuests", "Quests" }, new[] { "RealmTravel", "Travel" },
    };

    // A plugin with the default config (tweak changes it before Init), wired to the fakes, loaded and started.
    public static RealmHeraldry NewHeraldry(Action<object> tweak = null)
    {
        var p = new RealmHeraldry();
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmHeraldry).GetNestedType("PluginConfig", BF);
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
        H = p;
        return p;
    }

    // Unload and load again, as a restart or hot reload does. Oxide keeps permissions outside the plugin, so they stay.
    public static void Reload(Action<object> tweak = null)
    {
        var grants = new HashSet<string>(H.permission.Grants);
        Inv(H, "Unload");
        NewHeraldry(tweak);
        foreach (var g in grants) H.permission.Grants.Add(g);
    }

    public static Player Mk(ulong id, string name, string house = null, long purse = 0)
    {
        var p = new Player(id, name);
        if (house != null) Join(p, house);
        if (purse > 0) Purses[id.ToString()] = purse;
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        if (H != null) Inv(H, "OnPlayerConnected", p);
        return p;
    }
    public static void Offline(Player p) { Inv(H, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(H, "OnPlayerConnected", p); }
    public static void Admin(Player p) { H.permission.Grants.Add(p.Id + "|realmheraldry.admin"); }

    // A house (founded `days` ago), optionally bound to a game guild that holds the same members.
    public static HouseF House(string name, ulong guild, int days = 30, ulong banner = 1, ulong pattern = 2)
    {
        var h = new HouseF { Name = name, Founded = Clock.AddDays(-days), GuildId = guild };
        Houses.Add(h);
        if (guild != 0 && Guilds.TryGetGuild(guild) == null)
        {
            var g = new Guild(guild) { Name = "Guild " + guild };
            g.Banner.CurrentBanner = (int)banner; g.Banner.CurrentPattern = (int)pattern;
            Guilds.Guilds.Add(g);
        }
        WriteHousesFile();
        return h;
    }
    public static void Join(Player p, string house, int daysAgo = 30)
    {
        var h = HouseNamed(house);
        foreach (var o in Houses) o.Members.Remove(p.Id);
        h.Members.Add(p.Id);
        h.Joined[p.Id] = Clock.AddDays(-daysAgo);
        if (h.Leader == 0) h.Leader = p.Id;
        if (h.GuildId != 0) { var g = Guilds.TryGetGuild(h.GuildId); if (!g.Members().Has(p.Id)) g.Members().List.Add(new Member(p.Id)); }
        WriteHousesFile();
    }
    public static void Leave(Player p)
    {
        foreach (var h in Houses) { h.Members.Remove(p.Id); h.Joined.Remove(p.Id); if (h.Leader == p.Id) h.Leader = h.Members.FirstOrDefault(); }
        foreach (var g in Guilds.Guilds) g.Members().List.RemoveAll(m => m.PlayerId == p.Id);
        WriteHousesFile();
    }
    public static bool WriteFile = true;
    public static void WriteHousesFile()
    {
        if (!WriteFile) return;
        var obj = new Dictionary<string, object>
        {
            { "Houses", Houses.Select(h => new Dictionary<string, object> { { "Name", h.Name }, { "GuildId", h.GuildId },
                { "Members", h.Members.Select(m => new Dictionary<string, object> { { "Id", m.ToString() }, { "Joined", h.Joined.ContainsKey(m) ? h.Joined[m] : Clock } }).ToList() } }).ToList() },
            { "Treaties", new List<object>() }
        };
        File.WriteAllText(Path.Combine(Dir, "RealmHouses.json"), System.Text.Json.JsonSerializer.Serialize(obj));
    }
    public static Guild G(ulong id) { return Guilds.TryGetGuild(id); }
    public static string Hex(UnityEngine.Color c) { return "#" + ((int)Math.Round(c.r * 255)).ToString("x2") + ((int)Math.Round(c.g * 255)).ToString("x2") + ((int)Math.Round(c.b * 255)).ToString("x2"); }
    public static string Field(ulong guild) { return Hex(G(guild).Banner.CurrentColor); }
    public static string Charge(ulong guild) { return Hex(G(guild).Banner.CurrentPaternColor); }

    static readonly Dictionary<string, string> CmdMethod = new Dictionary<string, string> { { "heraldry", "CmdHeraldry" }, { "ballot", "CmdBallot" }, { "vote", "CmdVote" } };
    public static string Cmd(Player p, string cmd, params string[] args)
    {
        int before = p.Messages.Count;
        Inv(H, CmdMethod[cmd], p, cmd, args);
        return string.Join("\n", p.Messages.Skip(before));
    }

    // Minutes of server time, one tick a minute (the default TickSeconds).
    public static void Tick(int minutes = 1)
    {
        for (int i = 0; i < minutes; i++) { Clock = Clock.AddSeconds(60); Inv(H, "SafeTick"); }
    }
    // Moves the clock without ticking (a long quiet stretch), then ticks once.
    public static void Skip(TimeSpan t) { Clock = Clock.Add(t - TimeSpan.FromMinutes(1)); Tick(1); }

    public static object Voter(Player p) { var v = (System.Collections.IDictionary)D("Voters"); return v.Contains(p.Id.ToString()) ? v[p.Id.ToString()] : null; }
    // Makes a player a seasoned voter: seen long ago, long play, in their house for long (as if the heralds had watched).
    public static void Seasoned(params Player[] ps)
    {
        foreach (var p in ps)
        {
            Inv(H, "Observe", p, 0L);
            var r = Voter(p);
            SetF(r, "FirstSeen", Clock.AddDays(-20));
            SetF(r, "PlaySeconds", 20L * 3600);
            if (F(r, "House") != null) SetF(r, "HouseSince", Clock.AddDays(-20));
        }
    }
    public static System.Collections.IList Ballots() { return (System.Collections.IList)D("Ballots"); }
    public static object Ballot(int id) { foreach (var b in Ballots()) if ((int)F(b, "Id") == id) return b; return null; }
    public static string Status(int id) { return (string)F(Ballot(id), "Status"); }
}
