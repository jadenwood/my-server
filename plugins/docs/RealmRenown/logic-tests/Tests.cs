// Behaviour tests for plugins/RealmRenown.cs. Run with run.sh (see there for what this does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeHatch.Damaging;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static RealmRenown P;
    static KingsScheme Kings;
    static Plugin Chron, Houses, Crown, Contracts, Laws;
    static Dictionary<string, string> HouseOf = new Dictionary<string, string>();     // player id -> house
    static Dictionary<string, string> LiegeOf = new Dictionary<string, string>();     // house -> liege
    static List<string> Claims = new List<string>();                                  // GetOpenClaims lines
    static string CrownHouseName;
    static HashSet<string> ContractOutlaws = new HashSet<string>();
    static List<string> CourtOutlaws = new List<string>();
    static List<string> ChronLog = new List<string>();
    static bool ChronAcceptsTitles = true;
    static DateTime Now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    static string Dir;

    const string DEFAULT_FMT = "%name% : %message%";

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
    static object Pd(string id) { var d = (IDictionary)F(Data(), "Players"); return d.Contains(id) ? d[id] : null; }
    static int Ren(string id) { var p = Pd(id); return p == null ? 0 : (int)F(p, "Renown"); }
    static int Inf(string id) { var p = Pd(id); return p == null ? 0 : (int)F(p, "Infamy"); }
    static int Stat(string id, string k) { var p = Pd(id); if (p == null) return 0; var s = (IDictionary)F(p, "Stats"); return s.Contains(k) ? (int)s[k] : 0; }
    static bool HasT(string id, string t) { var p = Pd(id); return p != null && ((IDictionary)F(p, "Titles")).Contains(t); }

    static Player Mk(string id, string name, string house)
    {
        var p = new Player(ulong.Parse(id), name);
        if (house != null) HouseOf[id] = house;
        Server.ClientPlayers.Add(p);
        Inv(P, "OnPlayerConnected", p);
        return p;
    }
    static void Offline(Player p) { Server.ClientPlayers.Remove(p); Inv(P, "OnPlayerDisconnected", p); }
    static void Cmd(string cmd, Player p, string line)
    {
        string[] args = line.Length == 0 ? new string[0] : line.Split(' ');
        Inv(P, cmd == "renown" ? "CmdRenown" : "CmdTitles", p, cmd, args);
    }
    static void Clear() { Server.Broadcasts.Clear(); foreach (var p in Server.ClientPlayers) p.Messages.Clear(); }
    static string B() { return string.Join("\n", Server.Broadcasts); }
    static void Tick(int seconds) { Now = Now.AddSeconds(seconds); Inv(P, "Tick"); }
    static void Poll() { Now = Now.AddSeconds(61); Inv(P, "Tick"); }
    static void Kill(Player killer, Player victim)
    {
        var evt = new EntityDeathEvent { Entity = victim.Entity, KillingDamage = new Damage { DamageSource = killer.Entity } };
        Inv(P, "OnEntityDeath", evt);
    }
    static string Write(string name, object o) { string path = Path.Combine(Dir, name + ".json"); File.WriteAllText(path, JsonSerializer.Serialize(o)); return path; }

    static string TestConfig(Action<JsonObject> tweak)
    {
        var tmp = new RealmRenown();
        Inv(tmp, "LoadDefaultConfig");
        var node = JsonNode.Parse(tmp.Config.Json).AsObject();
        node["ReignDayHours"] = 1.0;               // one "reign day" per hour keeps the loop short
        if (tweak != null) tweak(node);
        return node.ToJsonString();
    }

    static RealmRenown NewPlugin(string configJson)
    {
        var p = new RealmRenown();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "CrownAndConsequences", Crown);
        SetF(p, "RealmContracts", Contracts); SetF(p, "RealmLaws", Laws);
        SetF(p, "clock", (Func<DateTime>)(() => Now));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "renowntest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Kings = new KingsScheme();
        SocialAPI.Registry[typeof(KingsScheme)] = Kings;
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) =>
        {
            if (h != "Log") return null;
            if ((string)a[0] == "title_earned" && !ChronAcceptsTitles) return 0;
            ChronLog.Add(a[0] + ": " + a[1]); return ChronLog.Count;
        } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            string s;
            if (h == "GetHouse") return HouseOf.TryGetValue((string)a[0], out s) ? s : null;
            if (h == "GetLiege") return LiegeOf.TryGetValue((string)a[0], out s) ? s : null;
            return null;
        } };
        Crown = new Plugin { Name = "CrownAndConsequences", Handler = (h, a) =>
        {
            if (h == "GetOpenClaims") return Claims.ToArray();
            if (h == "GetKingHouse") return CrownHouseName;
            return null;
        } };
        Contracts = new Plugin { Name = "RealmContracts", Handler = (h, a) => h == "IsOutlaw" ? (object)ContractOutlaws.Contains((string)a[0]) : null };
        Laws = new Plugin { Name = "RealmLaws", Handler = (h, a) => h == "GetCourtOutlaws" ? CourtOutlaws.ToArray() : null };

        // ---------- corruption-safe load ----------
        string dataPath = Path.Combine(Dir, "RealmRenown.json");
        File.WriteAllText(dataPath, "{ \"Players\": { broken");
        P = NewPlugin(TestConfig(null));
        var probe = new Player(76561190000000099, "Probe"); Server.ClientPlayers.Add(probe);
        Cmd("renown", probe, "");
        Ok(Data() == null && (bool)F(P, "loadFailed"), "damaged data file -> renown paused");
        Ok(probe.All().Contains("damaged and paused"), "commands answer 'paused'", probe.All());
        Inv(P, "Tick"); Inv(P, "OnServerSave"); Inv(P, "Unload");
        Ok(File.ReadAllText(dataPath) == "{ \"Players\": { broken", "damaged file is never overwritten");
        Ok(P.Log.Any(l => l.StartsWith("ERROR")), "load failure logged as error");
        File.WriteAllText(dataPath, "null");
        P = NewPlugin(TestConfig(null));
        Ok(Data() == null, "a 'null' data file also refuses to load");
        File.Delete(dataPath);
        Server.ClientPlayers.Clear();

        // ---------- corrupt config -> defaults in memory, file untouched ----------
        var badCfg = new RealmRenown();
        badCfg.Config.Json = "{ not json";
        Inv(badCfg, "LoadDefaultMessages");
        SetF(badCfg, "clock", (Func<DateTime>)(() => Now));
        Inv(badCfg, "Init");
        Ok(F(badCfg, "config") != null && badCfg.Config.Json == "{ not json", "unreadable config -> defaults for the session, file kept");
        File.Delete(dataPath);

        // ---------- fresh start ----------
        P = NewPlugin(TestConfig(null));
        Ok(Data() != null && File.Exists(dataPath), "fresh start creates RealmRenown.json");
        Ok(P.permission.Registered.Contains("realmrenown.admin"), "admin permission realmrenown.admin registered");
        var titles = (IList)F(F(P, "config"), "Titles");
        int defaultTitles = titles.Count;
        Ok(titles.Count >= 24, "the default titles (18, and 6 for RealmQuests) survive validation", titles.Count.ToString());
        Ok(P.timer.EveryCount == 1, "one tick timer");
        Inv(P, "OnServerInitialized");
        Ok(P.timer.EveryCount == 1, "OnServerInitialized is idempotent (hot reload)");
        Ok(!File.Exists(Path.Combine(Dir, "RealmContracts.json")) && !File.Exists(Path.Combine(Dir, "RealmHouses.json"))
            && !File.Exists(Path.Combine(Dir, "RealmChronicle.json")), "feeds never create other plugins' files");

        const string KING = "76561198000000001", SLAYER = "76561198000000002", ALLY = "76561198000000003",
                     VASSAL = "76561198000000004", OFFL = "76561198000000005", GUARD = "76561198000000006",
                     MERC = "76561198000000007", ADMIN = "76561198000000008", TWIN1 = "76561198000000009", TWIN2 = "76561198000000010";
        var king = Mk(KING, "Aldric", "Varrow");
        var slayer = Mk(SLAYER, "Brannoc", "Dunmere");
        var ally = Mk(ALLY, "Cerys", "Dunmere");
        var vassal = Mk(VASSAL, "Daven", "Halloran");
        var guard = Mk(GUARD, "Edric", "Varrow");
        var merc = Mk(MERC, "Fenna", "Merrin");
        var admin = Mk(ADMIN, "Steward", null);
        P.permission.Grants.Add(ADMIN + "|realmrenown.admin");
        LiegeOf["Halloran"] = "Dunmere";

        // ---------- reign days (KingsScheme) ----------
        Kings.King = ulong.Parse(KING); Kings.KingName = "Aldric"; CrownHouseName = "Varrow";
        for (int i = 0; i < 119; i++) Tick(30);
        Ok(Stat(KING, "reign_day") == 0, "no reign day before ReignDayHours");
        Tick(30);
        Ok(Stat(KING, "reign_day") == 1 && Ren(KING) == 40, "a reign day after one (test) day on the throne: +40", Ren(KING).ToString());
        Ok(king.All().Contains("A day upon the Old Throne (+40 renown)"), "the monarch is told", king.All());
        Now = Now.AddHours(10); Inv(P, "Tick");
        Ok(Stat(KING, "reign_day") == 1, "server downtime / long gaps are not counted as reign");

        // ---------- kingslayer (OnEntityDeath) ----------
        Clear();
        Kill(slayer, king);
        Ok(Stat(SLAYER, "kingslayer") == 1 && Ren(SLAYER) == 60 && Inf(SLAYER) == 40, "slaying the monarch: kingslayer +60 renown +40 infamy");
        Ok(HasT(SLAYER, "kingslayer"), "title Kingslayer earned");
        Ok(B().Contains("Brannoc has earned the title Kingslayer."), "title announced realm-wide", B());
        Ok(ChronLog.Any(l => l.StartsWith("title_earned: Brannoc is named Kingslayer")), "title chronicled as title_earned", string.Join("\n", ChronLog));
        Kill(slayer, king);
        Ok(Stat(SLAYER, "kingslayer") == 1, "same killer, same monarch: cooldown, no second deed");
        Kill(ally, ally);
        Kill(ally, guard);
        Ok(Stat(ALLY, "kingslayer") == 0, "suicide and killing a non-monarch are not kingslaying");
        // Throne released just before the death hook: the cached king still counts within the grace period.
        Kings.King = 0; Kings.KingName = null;
        Now = Now.AddSeconds(20);
        Kill(ally, king);
        Ok(Stat(ALLY, "kingslayer") == 1, "king killed just after the throne emptied (grace) still counts");
        Now = Now.AddSeconds(120);
        Kill(vassal, king);
        Ok(Stat(VASSAL, "kingslayer") == 0, "after the grace period the former monarch is just a player");

        // ---------- chat prefix (Player.ChatFormat) ----------
        Clear();
        Cmd("titles", slayer, "");
        Ok(slayer.All().Contains("Kingslayer"), "/titles lists own titles", slayer.All());
        Now = Now.AddSeconds(5);
        Cmd("titles", slayer, "set Oathbreaker");
        Ok(slayer.All().Contains("You have not earned the title Oathbreaker"), "cannot wear an unearned title", slayer.All());
        Now = Now.AddSeconds(5);
        Cmd("titles", slayer, "set kingslayer");
        Ok(slayer.ChatFormat == "[D6A043]Kingslayer[-] " + DEFAULT_FMT, "chosen title prefixes the global chat format", slayer.ChatFormat);
        Ok((string)Inv(P, "GetChosenTitle", SLAYER) == "Kingslayer", "API GetChosenTitle");
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("titles", slayer, "clear");
        Ok(slayer.All().Contains("change your title again") && slayer.ChatFormat.StartsWith("[D6A043]Kingslayer"), "title change cooldown", slayer.All());
        slayer.ChatFormat = DEFAULT_FMT;                      // the game resets it (CoreServer.UpdatePlayerData)
        Tick(30);
        Ok(slayer.ChatFormat == "[D6A043]Kingslayer[-] " + DEFAULT_FMT, "a format reset by the game is prefixed again on the next tick", slayer.ChatFormat);
        Tick(30);
        Ok(slayer.ChatFormat == "[D6A043]Kingslayer[-] " + DEFAULT_FMT, "the prefix is never stacked", slayer.ChatFormat);
        slayer.ChatFormat = "[FF0000]%name%[-]: %message%";  // admin gives a custom format
        Tick(30);
        Ok(slayer.ChatFormat == "[D6A043]Kingslayer[-] [FF0000]%name%[-]: %message%", "custom formats are kept under the prefix", slayer.ChatFormat);
        Now = Now.AddSeconds(61);
        Cmd("titles", slayer, "clear");
        Ok(slayer.ChatFormat == "[FF0000]%name%[-]: %message%", "clearing restores the format", slayer.ChatFormat);
        var odd = Mk("76561198000000011", "Odd", null);
        odd.ChatFormat = "broken";
        Inv(P, "AddDeed", "76561198000000011", "Odd", "tournament_win", "test", "x:1");
        Now = Now.AddSeconds(5);
        Cmd("titles", odd, "set champion");
        Ok(odd.ChatFormat == "broken", "a format without %message% is never touched");

        // ---------- rebellion: claimant wins ----------
        Kings.King = ulong.Parse(KING); Kings.KingName = "Aldric"; CrownHouseName = "Varrow";
        var offl = Mk(OFFL, "Gwyn", "Dunmere");
        Offline(offl);
        DateTime ws = Now.AddMinutes(-1), we = Now.AddMinutes(60);
        string claim = "Dunmere|active|" + ws.ToString("o") + "|" + we.ToString("o");
        Claims.Add(claim);
        Tick(30);
        var watches = (IList)F(Data(), "Claims");
        Ok(watches.Count == 1, "active claim is watched");
        for (int i = 0; i < 10; i++) Tick(30);
        // Dunmere takes the throne, then the window closes and the claim leaves the open list.
        Kings.King = ulong.Parse(SLAYER); Kings.KingName = "Brannoc"; CrownHouseName = "Dunmere";
        Now = we.AddSeconds(10);
        Claims.Clear();
        Clear();
        Inv(P, "Tick");
        Ok(Stat(SLAYER, "crown_seized") == 1 && HasT(SLAYER, "usurper"), "the new monarch: crown_seized + Usurper");
        Ok(Stat(ALLY, "kingmaker") == 1 && HasT(ALLY, "kingmaker"), "online house member: kingmaker + Kingmaker");
        Ok(Stat(VASSAL, "kingmaker") == 1, "online sworn vassal (liege = claimant) counts as claimant side");
        Ok(Stat(OFFL, "kingmaker") == 0, "a member who was offline during the Lawful Hours gets nothing");
        Ok(Stat(GUARD, "rebellion_defended") == 0 && Stat(KING, "crown_defended") == 0, "the losing crown side gets nothing");
        Ok(((IList)F(Data(), "Claims")).Count == 0, "resolved claim is forgotten");

        // ---------- rebellion: crown holds; early withdrawal ----------
        ws = Now.AddMinutes(-1); we = Now.AddMinutes(30);
        Claims.Add("Varrow|active|" + ws.ToString("o") + "|" + we.ToString("o"));
        Tick(30);
        Now = we.AddSeconds(5); Claims.Clear(); Inv(P, "Tick");
        Ok(Stat(SLAYER, "crown_defended") == 1 && HasT(SLAYER, "unbowed"), "monarch holds the crown: crown_defended + The Unbowed");
        Ok(Stat(ALLY, "rebellion_defended") == 1, "crown-side member: rebellion_defended");
        Ok(Stat(GUARD, "kingmaker") == 0, "failed claimants get nothing");
        ws = Now.AddMinutes(-1); we = Now.AddMinutes(60);
        Claims.Add("Merrin|active|" + ws.ToString("o") + "|" + we.ToString("o"));
        Tick(30);
        Claims.Clear(); Tick(30);
        Ok(Stat(SLAYER, "crown_defended") == 1 && ((IList)F(Data(), "Claims")).Count == 0, "a claim withdrawn before its window ends awards nothing");

        // ---------- contracts feed (RealmContracts.json, read-only) ----------
        var contracts = new List<object> {
            new { Id = 1, Type = "bounty", Status = "done", FulfillerId = MERC, FulfillerName = "Fenna" },
            new { Id = 2, Type = "delivery", Status = "open", FulfillerId = (string)null, FulfillerName = (string)null } };
        string cpath = Write("RealmContracts", new { NextId = 3, Contracts = contracts });
        Poll();
        Ok(Stat(MERC, "contract_bounty") == 0, "first contracts poll only baselines existing history");
        contracts.Add(new { Id = 3, Type = "delivery", Status = "done", FulfillerId = MERC, FulfillerName = "Fenna" });
        contracts.Add(new { Id = 4, Type = "delivery", Status = "done", FulfillerId = MERC, FulfillerName = "Fenna" });
        contracts.Add(new { Id = 5, Type = "merc", Status = "done", FulfillerId = MERC, FulfillerName = "Fenna" });
        contracts.Add(new { Id = 6, Type = "bounty", Status = "cancelled", FulfillerId = MERC, FulfillerName = "Fenna" });
        string before = File.ReadAllText(cpath);
        Write("RealmContracts", new { NextId = 7, Contracts = contracts });
        before = File.ReadAllText(cpath);
        Poll();
        Ok(Stat(MERC, "contract_delivery") == 1, "delivery credited once; a second delivery inside its cooldown is not", Stat(MERC, "contract_delivery").ToString());
        Ok(Stat(MERC, "contract_merc") == 1 && Stat(MERC, "contract_bounty") == 0, "merc credited; cancelled bounty not");
        Ok(Ren(MERC) == 45, "contract renown 15 + 30", Ren(MERC).ToString());
        Poll();
        Ok(Stat(MERC, "contract_merc") == 1, "contracts are credited once only");
        Ok(File.ReadAllText(cpath) == before, "RealmContracts.json is never written");
        Write("RealmContracts", new { NextId = 2, Contracts = new List<object> { new { Id = 1, Type = "merc", Status = "done", FulfillerId = MERC, FulfillerName = "Fenna" } } });
        Poll();
        Ok(Stat(MERC, "contract_merc") == 1, "a reset contracts file is re-baselined, not re-credited");
        File.WriteAllText(cpath, "{ garbage");
        Poll();
        Ok(Data() != null && File.ReadAllText(cpath) == "{ garbage", "an unreadable contracts file is skipped and left alone");

        // ---------- houses feed (RealmHouses.json player records) ----------
        string hpath = Write("RealmHouses", new { Players = new Dictionary<string, object> { { GUARD, new { Name = "Edric", OathsBroken = 1, TreatiesBroken = 0 } } } });
        Poll();
        Ok(Stat(GUARD, "oath_broken") == 0, "first houses poll baselines old marks");
        Write("RealmHouses", new { Players = new Dictionary<string, object> {
            { GUARD, new { Name = "Edric", OathsBroken = 2, TreatiesBroken = 9 } },
            { VASSAL, new { Name = "Daven", OathsBroken = 1, TreatiesBroken = 0 } } } });
        Poll();
        Ok(Stat(GUARD, "oath_broken") == 1 && HasT(GUARD, "oathbreaker"), "a new broken oath: oath_broken + Oathbreaker");
        Ok(Stat(GUARD, "treaty_broken") == 3, "mark deltas are capped per poll (MaxMarkDeltaPerPoll 3)", Stat(GUARD, "treaty_broken").ToString());
        Ok(Stat(VASSAL, "oath_broken") == 1, "a player new to the records after the baseline is counted from zero");
        Ok(Inf(GUARD) == 60 + 120, "infamy 60 + 3x40", Inf(GUARD).ToString());
        Clear();
        Now = Now.AddSeconds(5);
        Cmd("titles", guard, "set Oathbreaker");
        Ok(guard.ChatFormat == "[E86A5C]Oathbreaker[-] " + DEFAULT_FMT, "an infamous title uses the infamy colour", guard.ChatFormat);

        // ---------- chronicle feed (tournament, hunt, truce) ----------
        var twin1 = Mk(TWIN1, "Hale", null);
        var twin2 = Mk(TWIN2, "Hale", null);
        var events = new List<object> { new { id = 7, type = "tournament_champion", title = "old", actors = new[] { "Fenna" } } };
        Write("RealmChronicle", events);
        Poll();
        Ok(Stat(MERC, "tournament_win") == 0, "first chronicle poll baselines (no replay of history)");
        events.Add(new { id = 8, type = "tournament_champion", title = "Fenna wins the Royal Tournament", actors = new[] { "Fenna" } });
        events.Add(new { id = 9, type = "hunt_kill", title = "x", actors = new[] { "Hale", "Fenna" } });
        events.Add(new { id = 10, type = "truce_broken", title = "y", actors = new[] { "Nobody Known" } });
        events.Add(new { id = 11, type = "coronation", title = "z", actors = new[] { "Fenna" } });
        events.Add(new { id = 12, type = "truce_broken", title = "w", actors = new[] { "[FF0000]Cerys" } });
        Write("RealmChronicle", events);
        Poll();
        Ok(Stat(MERC, "tournament_win") == 1 && HasT(MERC, "champion"), "tournament_champion -> tournament_win + Champion of the Lists");
        Ok(Stat(TWIN1, "quarry_taken") == 0 && Stat(TWIN2, "quarry_taken") == 0, "an ambiguous name gets no deed");
        Ok(P.Log.Any(l => l.Contains("'Nobody Known' is unknown")), "unknown names are logged and skipped");
        Ok(Stat(MERC, "coronation") == 0, "unmapped chronicle types are ignored");
        Ok(Stat(ALLY, "truce_broken") == 1, "colour tags in chronicle names are stripped before matching");
        Poll();
        Ok(Stat(MERC, "tournament_win") == 1, "chronicle events count once (cursor)");
        Write("RealmChronicle", new List<object> { new { id = 1, type = "tournament_champion", title = "t", actors = new[] { "Fenna" } } });
        Poll();
        Ok(Stat(MERC, "tournament_win") == 1, "a reset chronicle is not replayed");

        // ---------- outlaws (RealmLaws + RealmContracts) ----------
        CourtOutlaws.Add(OFFL + "|Gwyn|2026-10-09T00:00:00Z|3");
        Poll();
        Ok(Stat(OFFL, "outlawed") == 1, "a court outlaw is seen even while offline (GetCourtOutlaws)");
        Poll();
        Ok(Stat(OFFL, "outlawed") == 1, "a standing outlawry is counted once");
        ContractOutlaws.Add(VASSAL);
        Poll();
        Ok(Stat(VASSAL, "outlawed") == 1 && HasT(VASSAL, "hunted"), "newly outlawed (contracts): outlawed + The Hunted");
        Offline(vassal);
        Poll();
        Server.ClientPlayers.Add(vassal);
        Poll();
        Ok(Stat(VASSAL, "outlawed") == 1, "an outlaw who logs off and back is not counted again");
        CourtOutlaws.Add(ALLY + "|Cerys|2026-10-09T00:00:00Z|4");
        Poll();
        Ok(Stat(ALLY, "outlawed") == 1, "newly outlawed by the court");

        // ---------- daily caps and decay ----------
        int capBefore = Ren(GUARD);
        for (int i = 0; i < 12; i++) Inv(P, "AddDeed", GUARD, "Edric", "tournament_win", "cap test", "cap:" + i);
        Ok(Ren(GUARD) - capBefore == 400, "renown per day is capped (MaxRenownPerDay 400)", (Ren(GUARD) - capBefore).ToString());
        Ok(Stat(GUARD, "tournament_win") == 12, "deeds still count toward titles when points are capped");
        Ok(!(bool)Inv(P, "AddDeed", GUARD, "Edric", "tournament_win", "dup", "cap:3"), "AddDeed dedupe key stops a repeat");
        Ok(!(bool)Inv(P, "AddDeed", GUARD, "Edric", "made_up", "x", ""), "AddDeed rejects unknown kinds");
        int infBefore = Inf(GUARD);
        Now = Now.Date.AddDays(1).AddHours(1);
        Inv(P, "Tick");
        Ok(Inf(GUARD) == infBefore - 5, "infamy decays by InfamyDecayPerDay at the day change", Inf(GUARD) + " vs " + infBefore);
        Ok(HasT(GUARD, "oathbreaker"), "earned titles are kept");
        Inv(P, "AddDeed", GUARD, "Edric", "tournament_win", "new day", "cap:new");
        Ok(Ren(GUARD) - capBefore == 480, "the daily cap resets the next day");

        // ---------- commands ----------
        Clear();
        Now = Now.AddSeconds(5);
        Cmd("renown", merc, "");
        Ok(merc.All().Contains("Fenna:") && merc.All().Contains("renown") && merc.All().Contains("Recent deeds"), "/renown summary", merc.All());
        Clear();
        Cmd("renown", merc, "top");
        Ok(merc.All().Contains("Wait a moment"), "command cooldown", merc.All());
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("renown", merc, "top");
        var lines = merc.Messages.Where(m => m.Contains("#")).ToList();
        Ok(lines.Count >= 2 && lines[0].Contains("#1 Edric") && lines[1].Contains("#2 "), "/renown top ranks by renown", merc.All());
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("renown", merc, "top infamy");
        Ok(merc.All().Contains("roll of infamy") && merc.Messages.Any(m => m.Contains("#1 Edric")), "/renown top infamy", merc.All());
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("renown", merc, "Brannoc");
        Ok(merc.All().Contains("Brannoc:") && merc.All().Contains("Kingslayer"), "/renown <player>", merc.All());
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("renown", merc, "Hale");
        Ok(merc.All().Contains("More than one player"), "ambiguous name refused", merc.All());
        Clear();
        Cmd("renown", merc, "help");
        Ok(merc.All().Contains("/titles set") && !merc.All().Contains("Admin:"), "help (no admin line for players)", merc.All());
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("titles", merc, "all");
        Ok(merc.Messages.Count == 19 && merc.All().Contains("* Champion of the Lists"), "/titles all lists every title, own ones marked", merc.Messages.Count.ToString());
        Now = Now.AddSeconds(5);
        Clear();
        Cmd("titles", merc, "the unbowed");
        Ok(merc.All().Contains("held the crown against a rebellion"), "/titles <title> shows how it is earned", merc.All());

        // ---------- admin ----------
        Clear();
        Cmd("renown", merc, "admin grant Fenna renown 100");
        Ok(merc.All().Contains("may not"), "admin commands need realmrenown.admin");
        Clear();
        Cmd("renown", admin, "help");
        Ok(admin.All().Contains("Admin:"), "admins see the admin help line");
        int r0 = Ren(MERC);
        Cmd("renown", admin, "admin grant Fenna renown 100 good service");
        Ok(Ren(MERC) == r0 + 100, "admin grant renown");
        Cmd("renown", admin, "admin grant Fenna renown 5000");
        Ok(Ren(MERC) == r0 + 100 && admin.All().Contains("At most 1000"), "admin adjustment cap");
        Cmd("renown", admin, "admin grant Fenna infamy -50");
        Ok(Inf(MERC) == 0, "infamy never goes below zero");
        Cmd("renown", admin, "admin title give Fenna Kingslayer");
        Ok(HasT(MERC, "kingslayer"), "admin title give");
        Cmd("renown", admin, "admin title take Brannoc kingslayer");
        Ok(!HasT(SLAYER, "kingslayer"), "admin title take");
        Clear();
        Cmd("renown", admin, "admin reset Fenna");
        Ok(Ren(MERC) > 0 && admin.All().Contains("confirm"), "reset asks for confirmation");
        Cmd("renown", admin, "admin reset Fenna confirm");
        Ok(Ren(MERC) == 0 && !HasT(MERC, "champion"), "reset erases the record");
        Clear();
        Cmd("renown", admin, "admin status");
        Ok(admin.All().Contains("Feed contracts") && admin.All().Contains("Feed chronicle"), "admin status shows feeds", admin.All());

        // ---------- API ----------
        Ok((int)Inv(P, "GetRenown", SLAYER) == Ren(SLAYER) && (int)Inv(P, "GetInfamy", GUARD) == Inf(GUARD), "API GetRenown / GetInfamy");
        Ok((bool)Inv(P, "HasTitle", GUARD, "oathbreaker") && ((string[])Inv(P, "GetTitles", GUARD)).Contains("oathbreaker"), "API HasTitle / GetTitles");
        foreach (var m in typeof(RealmRenown).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            Ok(false, "no public methods on the plugin (Call cannot reach them): " + m.Name);

        // ---------- chronicle type not registered ----------
        ChronAcceptsTitles = false;
        int calls = Chron.Calls.Count;
        Inv(P, "AddDeed", TWIN1, "Hale", "quarry_taken", "a", "q1");
        Inv(P, "AddDeed", TWIN1, "Hale", "quarry_taken", "b", "q2");
        Inv(P, "AddDeed", TWIN1, "Hale", "quarry_taken", "c", "q3");      // earns Crown's Huntsman
        Inv(P, "AddDeed", TWIN1, "Hale", "tournament_win", "d", "q4");    // earns Champion
        Ok(Chron.Calls.Count == calls + 1, "a rejected title_earned is not retried", (Chron.Calls.Count - calls).ToString());

        // ---------- unload restores formats; data round-trips ----------
        Now = Now.AddSeconds(120);
        Cmd("titles", guard, "set oathbreaker");
        Inv(P, "Unload");
        Ok(guard.ChatFormat == DEFAULT_FMT, "unload gives players back the game's format", guard.ChatFormat);
        int renSlayer = Ren(SLAYER);
        P = NewPlugin(TestConfig(null));
        Ok(Ren(SLAYER) == renSlayer && HasT(SLAYER, "usurper"), "data survives a reload");

        // ---------- pruning ----------
        Inv(P, "Unload");
        P = NewPlugin(TestConfig(c => c["MaxPlayers"] = 50));
        Server.ClientPlayers.Clear();
        for (int i = 0; i < 60; i++) Inv(P, "AddDeed", (76561199000000000 + i).ToString(), "Filler" + i, "contract_merc", null, "f" + i);
        Inv(P, "Tick");
        Ok(((IDictionary)F(Data(), "Players")).Count == 50, "the roll is pruned to MaxPlayers");
        Ok(Pd(SLAYER) != null && Pd(GUARD) != null, "titled players are kept when pruning");

        // ---------- bad config titles are dropped ----------
        P = NewPlugin(TestConfig(c =>
        {
            var arr = c["Titles"].AsArray();
            arr.Add(JsonNode.Parse("{\"Id\":\"bad\",\"Name\":\"%name% hack\",\"Requires\":{\"renown\":1}}"));
            arr.Add(JsonNode.Parse("{\"Id\":\"bad2\",\"Name\":\"Ghost\",\"Requires\":{\"nonsense\":1}}"));
        }));
        Ok(((IList)F(F(P, "config"), "Titles")).Count == defaultTitles && P.Log.Count(l => l.StartsWith("WARN Title")) == 2, "invalid titles in config are rejected with a warning");

        // ---------- first run on a server with history: every feed baselines ----------
        string fresh = Path.Combine(Dir, "fresh");
        Directory.CreateDirectory(fresh);
        Interface.Oxide.DataFileSystem.Dir = fresh;
        File.Copy(Path.Combine(Dir, "RealmHouses.json"), Path.Combine(fresh, "RealmHouses.json"));
        File.WriteAllText(Path.Combine(fresh, "RealmContracts.json"), JsonSerializer.Serialize(new { NextId = 9, Contracts = new[] { new { Id = 8, Type = "merc", Status = "done", FulfillerId = MERC, FulfillerName = "Fenna" } } }));
        P = NewPlugin(TestConfig(null));
        Server.ClientPlayers.Add(vassal);
        Poll();
        Ok(Stat(OFFL, "outlawed") == 0 && Stat(VASSAL, "outlawed") == 0 && Stat(ALLY, "outlawed") == 0, "existing outlaws are baselined on first run");
        Ok(Stat(GUARD, "oath_broken") == 0 && Stat(MERC, "contract_merc") == 0, "existing marks and contracts are baselined on first run");
        Interface.Oxide.DataFileSystem.Dir = Dir;

        Directory.Delete(Dir, true);
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
