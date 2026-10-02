// Behaviour tests for plugins/RealmLaws.cs. Run with run.sh (see there for what this does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Damaging;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static RealmLaws P;
    static KingsScheme Kings;
    static Plugin Chron, Houses, Crown, Contracts;
    static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    static Dictionary<string, ulong> Leader = new Dictionary<string, ulong>();
    static Dictionary<ulong, string> Seat = new Dictionary<ulong, string>();
    static List<string> ChronLog = new List<string>();
    static string Dir;

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
    static IList L(string f) { return (IList)F(Data(), f); }
    static IDictionary D(string f) { return (IDictionary)F(Data(), f); }
    static object Cfg() { return F(P, "config"); }

    static Player Mk(ulong id, string name, string house, float x = 500, float z = 500)
    {
        var p = new Player(id, name);
        p.Entity.Position = new Vector3(x, 0, z);
        if (house != null) { HouseOf[id] = house; p.Guild = new Guild { Name = house }; }
        Server.ClientPlayers.Add(p);
        return p;
    }
    static void Cmd(string cmd, Player p, string line)
    {
        string[] args = line.Length == 0 ? new string[0] : line.Split(' ');
        Inv(P, cmd == "law" ? "CmdLaw" : "CmdCourt", p, cmd, args);
    }
    static void Clear() { Server.Broadcasts.Clear(); foreach (var p in Server.ClientPlayers) p.Messages.Clear(); }
    static string B() { return string.Join("\n", Server.Broadcasts); }

    static RealmLaws NewPlugin(string configJson)
    {
        var p = new RealmLaws();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "CrownAndConsequences", Crown); SetF(p, "RealmContracts", Contracts);
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "lawtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Kings = new KingsScheme();
        SocialAPI.Registry[typeof(KingsScheme)] = Kings;
        SocialAPI.Registry[typeof(GuildScheme)] = new GuildScheme();
        var known = new HashSet<string> { "decree", "coronation" };
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) => { if (h != "Log") return null; if (!known.Contains((string)a[0])) return 0; ChronLog.Add(a[0] + ": " + a[1]); return ChronLog.Count; } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (h == "GetHouse") { string s; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out s) ? s : null; }
            if (h == "GetHouseLeader") { ulong id; return Leader.TryGetValue((string)a[0], out id) ? id.ToString() : null; }
            return null;
        } };
        Crown = new Plugin { Name = "CrownAndConsequences", Handler = (h, a) =>
        {
            if (h == "GetCouncilSeat") { string s; return Seat.TryGetValue((ulong)a[0], out s) ? s : null; }
            if (h == "IsRebellionActive") return false;
            if (h == "GetKingHouse") { string s; return HouseOf.TryGetValue(Kings.King, out s) ? s : null; }
            return null;
        } };
        Contracts = new Plugin { Name = "RealmContracts", Handler = (h, a) => h == "ProclaimOutlaw" ? (object)true : null };

        // ---------- corruption-safe load ----------
        string dataPath = Path.Combine(Dir, "RealmLaws.json");
        File.WriteAllText(dataPath, "{ \"Laws\": [ broken");
        P = NewPlugin(null);
        var admin0 = Mk(9001, "Probe", null);
        Cmd("law", admin0, "list");
        Ok(Data() == null && (bool)F(P, "loadFailed"), "damaged data file -> court closed");
        Ok(admin0.All().Contains("records are damaged"), "commands answer 'closed'", admin0.All());
        Inv(P, "Unload"); Inv(P, "OnServerSave"); Inv(P, "Tick");
        Ok(File.ReadAllText(dataPath) == "{ \"Laws\": [ broken", "damaged file is never overwritten");
        Ok(P.Log.Any(l => l.StartsWith("ERROR")), "load failure logged as error");
        File.WriteAllText(dataPath, "");
        P = NewPlugin(null);
        Ok(Data() == null, "empty existing data file also refuses to load");
        File.Delete(dataPath);
        Server.ClientPlayers.Clear();

        // ---------- fresh start ----------
        P = NewPlugin(null);
        Ok(Data() != null && File.Exists(dataPath), "missing data file -> fresh data");
        SetF(Cfg(), "LawGraceMinutes", 0);
        // Crown Market at (0,0) r60 town; Hearth (0,150) r40 town (defaults)
        var king = Mk(1, "Aldric", "Varrow", 500, 500);
        var marshal = Mk(2, "Brannoc", "Corvane", 500, 500);
        var thug = Mk(3, "Cutter", "Dunmere", 10, 10);
        var victim = Mk(4, "Dora", "Merrin", 12, 10);
        var lordA = Mk(11, "LordA", "Ashgrove");
        var lordB = Mk(12, "LordB", "Halloran");
        var lordC = Mk(13, "LordC", "Merrin");     // same house as victim, not accused
        var commoner = Mk(14, "Pleb", "Halloran");  // not a lord
        var varrowLord = Mk(15, "VarrowLord", "Varrow");
        var adminP = Mk(99, "Admin", null);
        P.permission.Grants.Add("99|realmlaws.admin");
        Leader["Ashgrove"] = 11; Leader["Halloran"] = 12; Leader["Merrin"] = 13; Leader["Varrow"] = 1; Leader["Dunmere"] = 3; Leader["Corvane"] = 2;
        Seat[2] = "Marshal";
        Kings.King = 1; Kings.KingName = "Aldric";
        Inv(P, "Tick");
        Ok((string)F(Data(), "CrownId") == "1", "first crown seen without lapse");

        // ---------- proclamations ----------
        Clear();
        Cmd("law", thug, "proclaim kings_peace");
        Ok(thug.All().Contains("Only the reigning monarch"), "non-monarch cannot proclaim");
        Cmd("law", king, "proclaim kings_peace");
        Ok(L("Laws").Count == 1 && B().Contains("proclaims The King's Peace"), "monarch proclaims law", B());
        Ok(ChronLog.Any(c => c.StartsWith("decree: Aldric proclaims")), "unknown chronicle type falls back to decree", string.Join("|", ChronLog));
        Ok(Chron.Calls.Any(c => c.StartsWith("Log(law_proclaimed")), "new type tried first");
        Clear();
        Cmd("law", king, "proclaim no_building_towns");
        Ok(king.All().Contains("heralds are hoarse"), "proclamation cooldown", king.All());
        Cmd("law", adminP, "proclaim no_building_towns");
        Cmd("law", adminP, "proclaim market_curfew");
        Ok(L("Laws").Count == 3, "admin bypasses cooldown");
        Clear();
        Cmd("law", adminP, "proclaim bridge_toll");
        Ok(adminP.All().Contains("At most 3 laws"), "active law cap", adminP.All());
        Cmd("law", king, "list");
        Ok(king.All().Contains("kings_peace") && king.All().Contains("market_curfew"), "law list shows laws", king.All());

        // ---------- King's Peace ----------
        Clear();
        var dmg = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = 25f, DamageSource = thug.Entity } };
        object r = Inv(P, "OnEntityHealthChange", dmg);
        Ok(r is bool && (bool)r && dmg.Cancelled && dmg.Damage.Amount == 0f, "peace blocks damage in zone");
        Ok(L("Crimes").Count == 1 && thug.All().Contains("court's ledger"), "peace breach recorded", thug.All());
        var dmg2 = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = 25f, DamageSource = thug.Entity } };
        Inv(P, "OnEntityHealthChange", dmg2);
        Ok(dmg2.Cancelled && L("Crimes").Count == 1, "second blow still blocked, one record (cooldown)");
        var far = new EntityDamageEvent { Entity = lordA.Entity, Damage = new Damage { Amount = 5f, DamageSource = lordB.Entity } };
        Ok(Inv(P, "OnEntityHealthChange", far) == null && !far.Cancelled, "outside zone: no effect");

        // ---------- building ----------
        var cube = new CubePlaceEvent { SenderId = 3, Grid = new Grid { World = new Vector3(5, 0, 5) } };
        Inv(P, "OnCubePlacement", cube);
        Ok(cube.Cancelled && L("Crimes").Count == 2, "building in town blocked + recorded");
        var cubeK = new CubePlaceEvent { SenderId = 15, Grid = new Grid { World = new Vector3(5, 0, 5) } };
        Inv(P, "OnCubePlacement", cubeK);
        Ok(!cubeK.Cancelled, "crown house may build in zones");
        var cubeOut = new CubePlaceEvent { SenderId = 3, Grid = new Grid { World = new Vector3(900, 0, 900) } };
        Inv(P, "OnCubePlacement", cubeOut);
        Ok(!cubeOut.Cancelled, "building outside zones allowed");

        // ---------- curfew (polling) ----------
        // make the curfew cover every hour except one -> set start=now, end=now-1
        int h = DateTime.UtcNow.Hour;
        foreach (var l in (IList)F(Cfg(), "Catalogue"))
            if ((string)F(l, "Id") == "market_curfew") { SetF(l, "CurfewStartHourUtc", h); SetF(l, "CurfewEndHourUtc", (h + 23) % 24); }
        SetF(Cfg(), "CurfewGraceSeconds", 0);
        Clear();
        Inv(P, "Tick");
        Ok(victim.All().Contains("Curfew"), "curfew warns first", victim.All());
        int before = L("Crimes").Count;
        Inv(P, "Tick");
        Ok(L("Crimes").Count == before + 2, "curfew breach recorded after grace (thug + victim)", L("Crimes").Count.ToString());
        Ok(!lordA.All().Contains("Curfew"), "players outside zone untouched");

        // ---------- accusations ----------
        Clear();
        Cmd("court", commoner, "accuse Cutter kings_peace");
        Ok(commoner.All().Contains("Only the monarch or a member of the council"), "commoner cannot accuse");
        Cmd("court", marshal, "accuse Aldric kings_peace");
        Ok(marshal.All().Contains("cannot be tried"), "monarch immune", marshal.All());
        Clear();
        thug.Inventory.Contents.Counts["Wood"] = 15;
        Cmd("court", marshal, "accuse Cutter kings_peace fine 20 wood");
        var cases = L("Cases");
        Ok(cases.Count == 1 && B().Contains("accuses Cutter"), "council accuses with fine", B() + marshal.All());
        var c1 = cases[0];
        Ok(((IList)F(c1, "CrimeIds")).Count == 1 && (string)F(c1, "VictimId") == "4", "evidence + victim linked");
        Ok(thug.All().Contains("trial by combat"), "accused notified");
        Clear();
        Cmd("court", marshal, "accuse Cutter no_building_towns");
        Ok(marshal.All().Contains("already faces case"), "one open case per target", marshal.All());
        Cmd("court", marshal, "accuse Pleb bridge_toll fine 999 wood");
        Ok(marshal.All().Contains("not a whole number from 1 to 200"), "fine cap enforced", marshal.All());
        Clear();
        Cmd("court", marshal, "accuse Pleb harbouring");
        Ok(marshal.All().Contains("not in force"), "declared law must be in force to accuse without record", marshal.All());

        // ---------- jury trial: guilty, fine ----------
        Clear();
        Cmd("court", commoner, "trial 1");
        Ok(commoner.All().Contains("Only the crown"), "outsider cannot open trial");
        Cmd("court", thug, "trial 1");
        Ok((string)F(c1, "Status") == "trial", "accused may call the court to sit", thug.All());
        var jurors = (List<string>)F(c1, "Jurors");
        Ok(jurors.Count == 3 && jurors.Contains("11") && jurors.Contains("12") && jurors.Contains("13"), "jury = online lords not party/crown house", string.Join(",", jurors));
        Ok(!jurors.Contains("14") && !jurors.Contains("15") && !jurors.Contains("1") && !jurors.Contains("2"), "commoner/crown house/monarch/accuser excluded");
        Clear();
        Cmd("court", commoner, "verdict 1 guilty");
        Ok(commoner.All().Contains("not sworn"), "non-juror cannot vote");
        Cmd("court", lordA, "verdict 1 guilty");
        Cmd("court", lordB, "verdict 1 guilty");
        Ok((string)F(c1, "Status") == "trial", "verdict waits for all votes or the clock");
        Cmd("court", lordC, "verdict 1 innocent");
        Ok((string)F(c1, "Status") == "guilty" && B().Contains("GUILTY"), "majority guilty", B());
        Ok(thug.Inventory.Contents.Counts["Wood"] == 0 && (int)F(c1, "FineRemaining") == 5, "fine: carried 15 taken (measured), 5 owed", thug.All());
        thug.Inventory.Contents.Counts["Wood"] = 9;
        Cmd("court", thug, "pay 1");
        Ok((int)F(c1, "FineRemaining") == 0 && thug.Inventory.Contents.Counts["Wood"] == 4 && thug.All().Contains("paid in full"), "pay remaining fine", thug.All());
        Ok(ChronLog.Any(c => c.Contains("found guilty")), "verdict chronicled (fallback)");

        // ---------- acquittal: immunity + quota penalty ----------
        Clear();
        Cmd("court", king, "accuse Dora market_curfew fine 5 wood");
        var c2 = L("Cases")[1];
        Cmd("court", king, "trial 2");
        Ok((string)F(c2, "Status") == "trial", "trial 2 opened", king.All());
        var j2 = (List<string>)F(c2, "Jurors");
        Ok(!j2.Contains("13"), "accused's own house excluded from jury", string.Join(",", j2));
        Ok(j2.Count == 3 && j2.Contains("3") && !j2.Contains("2"), "convicted lord may sit; council member may not", string.Join(",", j2));
        foreach (var j in j2) Cmd("court", Server.GetPlayerById(ulong.Parse(j)), "verdict 2 innocent");
        Ok((string)F(c2, "Status") == "acquitted", "unanimous innocent -> acquitted");
        Clear();
        Cmd("court", king, "accuse Dora kings_peace");
        Ok(king.All().Contains("acquitted recently"), "acquittal immunity", king.All());

        // ---------- per-accuser quota ----------
        SetF(Cfg(), "MaxActiveLaws", 4);
        Cmd("law", adminP, "proclaim bridge_toll");
        SetF(Cfg(), "AccusationsPerAccuserPerDay", 1);
        Clear();
        Cmd("court", king, "accuse Pleb bridge_toll");
        Ok(king.All().Contains("limit"), "per-accuser daily quota (king used 1)", king.All());
        SetF(Cfg(), "AccusationsPerAccuserPerDay", 10);
        SetF(Cfg(), "CrownAccusationsPerDay", 3);
        Clear();
        Cmd("court", king, "accuse Pleb bridge_toll");
        Ok(king.All().Contains("used its 3 accusations"), "crown quota counts acquittal penalty (2 accusations + 1 acquittal)", king.All());
        SetF(Cfg(), "CrownAccusationsPerDay", 20);

        // ---------- trial by combat ----------
        Clear();
        Cmd("court", king, "accuse Pleb bridge_toll outlaw 6");
        var c3 = L("Cases")[2];
        Ok((string)F(c3, "Status") == "accused", "case 3 opened", king.All());
        Cmd("court", king, "champion 3 Brannoc");
        Ok((string)F(c3, "ChampionId") == "2", "accuser names champion");
        Cmd("court", commoner, "combat 3");
        Ok((string)F(c3, "Status") == "combat" && B().Contains("TRIAL BY COMBAT"), "accused demands combat", commoner.All());
        // peace does not protect duellists from each other: move them into the market
        commoner.Entity.Position = new Vector3(1, 0, 1); marshal.Entity.Position = new Vector3(2, 0, 2);
        var duel = new EntityDamageEvent { Entity = commoner.Entity, Damage = new Damage { Amount = 30f, DamageSource = marshal.Entity } };
        Inv(P, "OnEntityHealthChange", duel);
        Ok(!duel.Cancelled, "duel damage not blocked by peace");
        Cmd("court", commoner, "combat 3");
        Ok(commoner.All().Contains("not waiting") || commoner.All().Contains("already"), "combat only once");
        var death = new EntityDeathEvent { Entity = commoner.Entity, KillingDamage = new Damage { DamageSource = marshal.Entity } };
        Inv(P, "OnEntityDeath", death);
        Ok((string)F(c3, "Status") == "guilty" && D("Outlaws").Contains("14"), "champion slays accused -> guilty -> outlaw", B());
        Ok(Contracts.Calls.Any(c => c.StartsWith("ProclaimOutlaw(14,Pleb,6")), "outlawry offered to RealmContracts", string.Join("|", Contracts.Calls));
        // outlaw loses the peace
        var hitOutlaw = new EntityDamageEvent { Entity = commoner.Entity, Damage = new Damage { Amount = 10f, DamageSource = lordA.Entity } };
        lordA.Entity.Position = new Vector3(3, 0, 3);
        Inv(P, "OnEntityHealthChange", hitOutlaw);
        Ok(!hitOutlaw.Cancelled, "outlaws are not sheltered by the King's Peace");

        // ---------- combat flee ----------
        Clear();
        Cmd("court", marshal, "accuse LordB bridge_toll exile 4");
        var c4 = L("Cases")[3];
        Cmd("court", lordB, "combat 4");
        Ok((string)F(c4, "Status") == "combat", "combat 4 open (champion = accuser)", lordB.All() + B());
        Inv(P, "OnPlayerDisconnected", lordB);
        Ok((string)F(c4, "Status") == "guilty" && D("Exiles").Contains("12"), "accused flees -> guilty -> exiled", B());

        // ---------- exile breach ----------
        SetF(Cfg(), "ExileBreachGraceSeconds", 0);
        lordB.Entity.Position = new Vector3(0, 0, 150);  // Hearth (town)
        Clear();
        Inv(P, "Tick");
        Ok(lordB.All().Contains("exiled from the towns"), "exile warned in town", lordB.All());
        Inv(P, "Tick");
        Ok(!D("Exiles").Contains("12") && D("Outlaws").Contains("12") && B().Contains("broke exile"), "exile breach -> outlawed", B());

        // ---------- combat timeout -> jury ----------
        Clear();
        lordC.Entity.Position = new Vector3(700, 0, 700);
        Cmd("court", marshal, "accuse LordC bridge_toll");
        var c5 = L("Cases")[4];
        Cmd("court", lordC, "combat 5");
        Ok((string)F(c5, "Status") == "combat", "combat 5 open", lordC.All() + marshal.All());
        SetF(c5, "CombatEnds", DateTime.UtcNow.AddMinutes(-1));
        Inv(P, "Tick");
        Ok((string)F(c5, "Status") == "accused", "combat timeout -> back to jury (default)");

        // ---------- mistrial + too few jurors ----------
        Clear();
        Server.ClientPlayers.Remove(lordA);
        Cmd("court", marshal, "trial 5");
        Ok(marshal.All().Contains("Too few sworn lords"), "too few jurors refused", marshal.All());
        Server.ClientPlayers.Add(lordA);

        // ---------- mistrial twice -> dismissed ----------
        Clear();
        SetF(Cfg(), "JuryMin", 2); SetF(Cfg(), "MinVotes", 1);
        Cmd("court", marshal, "trial 5");
        Ok((string)F(c5, "Status") == "trial", "trial 5 opened", marshal.All());
        SetF(c5, "TrialEnds", DateTime.UtcNow.AddMinutes(-1));
        Inv(P, "Tick");
        Ok((string)F(c5, "Status") == "accused" && B().Contains("Mistrial") && (DateTime)F(c5, "ExpiresAt") > DateTime.UtcNow.AddHours(1), "no votes -> mistrial, clock refreshed", B());
        Cmd("court", marshal, "trial 5");
        SetF(c5, "TrialEnds", DateTime.UtcNow.AddMinutes(-1));
        Inv(P, "Tick");
        Ok((string)F(c5, "Status") == "dismissed", "second mistrial -> dismissed", B());
        SetF(Cfg(), "JuryMin", 3); SetF(Cfg(), "MinVotes", 2);

        // ---------- fines paid to the victim ----------
        SetF(Cfg(), "FineDestination", "victim");
        Clear();
        lordA.Entity.Position = new Vector3(5, 0, 5); victim.Entity.Position = new Vector3(6, 0, 6);
        var hitV = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = 9f, DamageSource = lordA.Entity } };
        Inv(P, "OnEntityHealthChange", hitV);
        lordA.Inventory.Contents.Counts["Stone"] = 50;
        Cmd("court", adminP, "accuse LordA kings_peace fine 12 stone");
        var cv = L("Cases")[L("Cases").Count - 1];
        Ok((string)F(cv, "VictimId") == "4", "victim case opened", adminP.All() + B());
        victim.Inventory.Contents.Capacity = victim.Inventory.Contents.Counts.Values.Sum() + 5;   // room for 5 only
        Cmd("court", adminP, "admin verdict " + F(cv, "Id") + " guilty");
        Ok(lordA.Inventory.Contents.Counts["Stone"] == 38, "fine taken from convict (12 stone)");
        Ok(victim.Inventory.Contents.Counts["Stone"] == 5 && L("Owed").Count == 1, "victim paid 5 measured, 7 left owed", victim.All());
        victim.Inventory.Contents.Capacity = 100000;
        Clear();
        Cmd("court", victim, "collect");
        Ok(victim.Inventory.Contents.Counts["Stone"] == 12 && L("Owed").Count == 0 && victim.All().Contains("You receive 7 Stone"), "collect pays the rest", victim.All());
        Cmd("court", victim, "collect");
        Ok(victim.All().Contains("Nothing is owed"), "collect with nothing owed");
        SetF(Cfg(), "FineDestination", "burn");

        // ---------- pardon ----------
        Clear();
        Cmd("court", thug, "pardon Pleb");
        Ok(thug.All().Contains("Only the reigning monarch"), "only monarch pardons");
        SetF(Cfg(), "PardonsPerDay", 1);
        Cmd("court", king, "pardon Pleb");
        Ok(!D("Outlaws").Contains("14") && B().Contains("pardons Pleb"), "monarch pardons outlaw");
        Clear();
        Cmd("court", king, "pardon LordB");
        Ok(king.All().Contains("used its 1 pardons"), "pardon quota", king.All());

        // ---------- unpaid fine -> outlaw ----------
        Clear();
        victim.Inventory.Contents.Counts["Wood"] = 0;
        Cmd("court", adminP, "accuse Dora bridge_toll fine 7 wood");
        var c6 = L("Cases")[L("Cases").Count - 1];
        Cmd("court", adminP, "admin verdict " + F(c6, "Id") + " guilty");
        Ok((int)F(c6, "FineRemaining") == 7, "admin verdict, unpaid fine recorded", adminP.All() + B());
        SetF(c6, "FineDue", DateTime.UtcNow.AddMinutes(-1));
        Inv(P, "Tick");
        Ok(D("Outlaws").Contains("4") && (int)F(c6, "FineRemaining") == 0, "unpaid fine past due -> outlawry");

        // ---------- crime listing / case view ----------
        Clear();
        Cmd("law", lordA, "crimes Cutter");
        Ok(lordA.All().Contains("The King's Peace") && lordA.All().Contains("case #1"), "crime ledger visible", lordA.All());
        Cmd("court", lordA, "case 1");
        Ok(lordA.All().Contains("Case #1 [guilty]"), "case view", lordA.All());

        // ---------- API ----------
        Ok((bool)Inv(P, "IsCourtOutlaw", "4") && ((string[])Inv(P, "GetCourtOutlaws")).Length >= 1, "API: outlaws");
        Ok(((string[])Inv(P, "GetActiveLaws")).Length == 4, "API: active laws");

        // ---------- persistence round trip ----------
        Inv(P, "SaveData");
        int lawsBefore = L("Laws").Count, casesBefore = L("Cases").Count, crimesBefore = L("Crimes").Count;
        string cfgJson = P.Config.Json;
        P = NewPlugin(cfgJson);
        P.permission.Grants.Add("99|realmlaws.admin");
        Ok(L("Laws").Count == lawsBefore && L("Cases").Count == casesBefore && L("Crimes").Count == crimesBefore && D("Outlaws").Contains("4"),
            "data survives reload");

        // ---------- succession ----------
        Clear();
        Ok((int)F(Cfg(), "CrownAccusationsPerDay") == 6, "reloaded config keeps file values (quota 6)");
        SetF(Cfg(), "CrownAccusationsPerDay", 50);
        Cmd("court", marshal, "accuse Brannoc kings_peace");
        Cmd("court", king, "accuse Brannoc bridge_toll");
        int open = 0; foreach (var c in L("Cases")) if ((string)F(c, "Status") == "accused") open++;
        Ok(open >= 1, "open crown case exists before succession", king.All());
        Kings.King = 3; Kings.KingName = "Cutter";
        Inv(P, "Tick");
        Ok(L("Laws").Count == 0 && B().Contains("old laws lapse"), "laws lapse on succession", B());
        open = 0; foreach (var c in L("Cases")) if ((string)F(c, "Status") == "accused") open++;
        Ok(open == 0, "crown cases dismissed on succession");
        Kings.King = 0;
        Inv(P, "Tick");
        Ok((string)F(Data(), "CrownId") == "3", "vacant throne changes nothing");

        // ---------- zones admin ----------
        Clear();
        adminP.Entity.Position = new Vector3(321, 0, 654);
        Cmd("law", adminP, "zone set Old Ford 30 town");
        Ok(adminP.All().Contains("Zone 'Old Ford' set at x 321, z 654, radius 30 (town)") && P.Config.Json.Contains("Old Ford"), "zone set from position + saved to config", adminP.All());
        Cmd("law", thug, "zone set Bad 30");
        Ok(thug.All().Contains("may not"), "zone set needs admin");

        Console.WriteLine();
        Console.WriteLine("RESULT: " + pass + " passed, " + fail + " failed");
        Directory.Delete(Dir, true);
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
