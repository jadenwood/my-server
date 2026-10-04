// Behaviour tests for plugins/RealmDominion.cs against Mocks.cs and World.cs. Run with run.sh (see there for what this
// does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeHatch.Engine.Networking;
using Oxide.Core;
using static W;

static class Tests
{
    static int Main(string[] argv)
    {
        Setup("logic");
        Reset();
        FreshLoad();
        Reset(); Windows();
        Reset(); CaptureBasics();
        Reset(); CrowdAndGarrison();
        Reset(); ContestRules();
        Reset(); BannersAndDecay();
        Reset(); Eligibility();
        Reset(); Payday();
        Reset(); AbandonAndLineage();
        Reset(); GarrisonDamage();
        Reset(); Commands();
        Reset(); AdminCommands();
        Reset(); MapFileAndApi();
        Reset(); DamagedData();
        Reset(); Switches();
        return Done();
    }

    // Holds Varrow's man at the Tollbridge (placed at 0,0 radius 40) and opens the War Hours.
    static Player PrepareField(out Player admin)
    {
        Load();
        admin = MkAdmin("Steward", 500, 500);
        Place(admin, "tollbridge", 0, 0, 40);
        Place(admin, "greywatch", 1000, 1000, 40);
        At(admin, 500, 500);
        Now = WarHours.AddMinutes(1);
        Tick();
        return null;
    }

    static void FreshLoad()
    {
        Load();
        Ok(Holdings().Count == 7, "a fresh install seeds the seven holdings of Ostreval", Holdings().Count.ToString());
        Ok(Holdings().Cast<object>().All(h => !(bool)F(h, "Placed")), "seeded holdings wait to be marked on the land");
        Ok(System.IO.File.Exists(File("RealmDominion")), "the data file is written");
        Ok(P.permission.Registered.Contains(Admin), "registers realmdominion.admin");
        Ok(P.timer.Timers.Count == 1 && P.timer.Timers[0].Interval == 5f, "one tick timer every TickSeconds");
        Inv(P, "OnServerInitialized");
        Ok(P.timer.Timers.Count(t => !t.Destroyed) == 1, "a second OnServerInitialized (hot load) keeps one timer");
        var names = Holdings().Cast<object>().Select(h => (string)F(h, "Name")).ToList();
        Ok(names.Contains("The Tollbridge") && names.Contains("The Drowned Harbour") && names.Contains("The Ember Mines"), "names come from the saga's places");
        var kinds = Holdings().Cast<object>().Select(h => (string)F(h, "Kind")).Distinct().ToList();
        Ok(kinds.Count == 5, "every kind is present: village, keep, mine, harbour, crossroads", string.Join(",", kinds));
        var p = Mk("Ada", "Varrow");
        Cmd(p, "");
        string s = Said(p);
        Ok(s.Contains("not yet marked on the land") && s.Contains("The next War Hours: Wednesday 20:30"), "/dominion: unmarked holdings and the next War Hours", s);
        Ok(StyleProblems(p.Messages).Length == 0, "chat style");
    }

    static void Windows()
    {
        Load();
        Now = new DateTime(2026, 10, 7, 20, 29, 0, DateTimeKind.Utc);
        string reason; DateTime until;
        Func<string> st = () => { var a = new object[] { Now, null, null }; var mi = P.GetType().GetMethod("WindowStateNow", BF); var r = (string)mi.Invoke(P, a); reason = (string)a[1]; until = (DateTime)a[2]; return r; };
        reason = null; until = DateTime.MinValue;
        Ok(st() == "closed" && reason == "none", "Wednesday 20:29: closed");
        Now = new DateTime(2026, 10, 7, 20, 30, 0, DateTimeKind.Utc);
        Ok(st() == "open" && reason == "window" && until == new DateTime(2026, 10, 7, 22, 30, 0), "Wednesday 20:30: the War Hours are open until 22:30", until.ToString("o"));
        Now = new DateTime(2026, 10, 7, 22, 30, 0, DateTimeKind.Utc);
        Ok(st() == "closed", "22:30: closed again");
        Ok(until == new DateTime(2026, 10, 10, 21, 0, 0), "the next window is Saturday 21:00", until.ToString("o"));
        Now = new DateTime(2026, 10, 10, 22, 59, 0, DateTimeKind.Utc);
        Ok(st() == "open", "Saturday 22:59: open");
        Now = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);
        Ok(st() == "open", "Sunday 17:00: open");
        // Crown offset: realm time = UTC + 2, so 20:30 realm is 18:30 UTC.
        OffsetHours = 2;
        Now = new DateTime(2026, 10, 7, 18, 30, 0, DateTimeKind.Utc);
        Ok(st() == "open", "the crown's UtcOffsetHours moves the War Hours (UTC+2: open at 18:30 UTC)");
        OffsetHours = 0;
        Now = WarHours.AddMinutes(5);
        Truce = true;
        Ok(st() == "paused" && reason == "truce", "the Truce of the Realm pauses the War Hours");
        Truce = false; Rebellion = true;
        Ok(st() == "paused" && reason == "rebellion", "a rebellion pauses them (DuringRebellion pause)");
        SetCfg("DuringRebellion", "open");
        Now = new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc);
        Ok(st() == "open" && reason == "rebellion", "DuringRebellion open: a rebellion opens the field even outside the War Hours");
        SetCfg("DuringRebellion", "normal");
        Ok(st() == "closed", "DuringRebellion normal: a rebellion changes nothing outside the War Hours");
        Rebellion = false;
        Now = WarHours.AddMinutes(5);
        RaidOpen = false;
        Ok(st() == "paused" && reason == "raid", "shut raid hours pause the War Hours (RequireRaidHours)");
        SetCfg("RequireRaidHours", false);
        Ok(st() == "open", "RequireRaidHours false: raid hours do not matter");
        SetCfg("RequireRaidHours", true);
        WardenLoaded = false; SetF(P, "RealmWarden", null);
        Ok(st() == "open", "without RealmWarden the raid-hour gate is open");
        RaidOpen = true;
        // Stewards
        var admin = MkAdmin("Steward");
        Now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        Cmd(admin, "admin open 30");
        Ok(st() == "open" && reason == "forced", "/dominion admin open opens the field outside the War Hours");
        Now = Now.AddMinutes(31);
        Ok(st() == "closed", "and it closes again by itself");
        Now = WarHours.AddDays(7).AddMinutes(1);
        Cmd(admin, "admin close 60");
        Ok(st() == "closed" && reason == "stewards", "/dominion admin close shuts the War Hours");
        Cmd(admin, "admin auto");
        Ok(st() == "open", "/dominion admin auto: back to the schedule");
        // Announcements on the transitions.
        Reset(); Load();
        Now = WarHours.AddMinutes(-1); Tick();
        Clear();
        Now = WarHours.AddSeconds(5); Tick();
        Ok(Heralds().Contains("The War Hours begin") && Heralds().Contains("22:30"), "the herald opens the War Hours", Heralds());
        Clear(); Truce = true; Run(1);
        Ok(Heralds().Contains("pause") && Heralds().Contains("Truce"), "and tells the realm when a truce pauses them", Heralds());
        Clear(); Truce = false; Run(1);
        Ok(Heralds().Contains("resume"), "and when they resume", Heralds());
        Clear(); Now = new DateTime(2026, 10, 7, 22, 30, 5, DateTimeKind.Utc); Tick();
        Ok(Heralds().Contains("The War Hours end"), "and closes them", Heralds());
    }

    static void CaptureBasics()
    {
        Player admin;
        PrepareField(out admin);
        var ada = Mk("Ada", "Varrow", 5, 5);
        var bryn = Mk("Bryn", "Varrow", 900, 900);   // a second member far away: the house has 2 members
        Clear();
        Run(1);
        Ok(Capturer("tollbridge") == "Varrow" && Progress("tollbridge") > 1.5 && Progress("tollbridge") < 1.8, "one capturer raises the banner: 5 s of 300 s", Progress("tollbridge").ToString());
        Ok(Heralds().Contains("House [C58FC0]Varrow[FFFFFF] raises its banner at The Tollbridge"), "the herald tells of a new banner", Heralds());
        Ok(ada.All().Contains("You enter The Tollbridge"), "the player is told they entered a holding", ada.All());
        Run(57);
        Ok(Owner("tollbridge") == null && Progress("tollbridge") > 95, "not yet taken after 290 s", Progress("tollbridge").ToString());
        Clear();
        Run(2);
        Ok(Owner("tollbridge") == "Varrow", "taken after 300 s", Owner("tollbridge") ?? "null");
        Ok(Progress("tollbridge") == 0 && Capturer("tollbridge") == null && State("tollbridge") == "secured", "the banner is planted; the holding is secure");
        Ok(Heralds().Contains("takes The Tollbridge!"), "the herald tells the realm", Heralds());
        Ok(Chron.Count == 1 && Chron[0].StartsWith("holding_taken|House Varrow takes The Tollbridge|") && Chron[0].EndsWith("|Ada"), "the Chronicle records holding_taken with the capturers", string.Join("\n", Chron));
        Ok(Awards.Contains("Varrow|5|Took The Tollbridge"), "the house earns CapturePoints with an honour", string.Join(";", Awards));
        Ok(Deeds.Count == 1 && Deeds[0].StartsWith(ada.Id + "|holding_taken|dominion:tollbridge:"), "each capturer present gets the renown deed, with a dedupe key", string.Join(";", Deeds));
        Ok(!Deeds.Any(d => d.StartsWith(bryn.Id.ToString())), "a member who was not in the field gets no deed");
        Run(7);
        Ok(Refresh.Contains("dominion"), "the painted board is asked to redraw", string.Join(",", Refresh));
        var cory = Mk("Cory", "Corvane", 0, 0); Mk("Cadoc", "Corvane", 800, 0);
        At(ada, 400, 400);
        Clear();
        Run(6);
        Ok(Owner("tollbridge") == "Varrow" && Progress("tollbridge") == 0, "secure for SecureMinutes: no banner rises");
        Ok(cory.All().Contains("secure for"), "the attacker is told why", cory.All());
        Now = Now.AddMinutes(30);
        Clear();
        Run(1);
        Ok(Capturer("tollbridge") == "Corvane", "after SecureMinutes the holding can be attacked again");
        Ok(ada.All().Contains("Your holding The Tollbridge is under attack by House [8FB0BF]Corvane"), "the holder's members are warned", ada.All());
        Ok(cory.All().Contains("The Tollbridge: the banner of House [8FB0BF]Corvane[FFFFFF] is at"), "the attacker sees the banner rise", cory.All());
        var all = Server.ClientPlayers.SelectMany(p => p.Messages).Concat(Server.Broadcasts);
        Ok(StyleProblems(all).Length == 0, "every line follows the chat style", StyleProblems(all));
    }

    static void CrowdAndGarrison()
    {
        Player admin;
        PrepareField(out admin);
        Mk("A1", "Varrow", 1, 1); Mk("A2", "Varrow", 2, 2); Mk("A3", "Varrow", 3, 3);
        Run(1);
        double three = Progress("tollbridge");
        Ok(Math.Abs(three - 5 * (100.0 / 300) * 1.5) < 0.01, "three capturers: 50% faster (ExtraCapturerPercent 25 each)", three.ToString());
        for (int i = 0; i < 8; i++) Mk("V" + i, "Varrow", 4, 4);
        Run(1);
        double crowd = Progress("tollbridge") - three;
        Ok(Math.Abs(crowd - 5 * (100.0 / 300) * 2.0) < 0.01, "a crowd counts at most MaxCountedPerHouse (5)", crowd.ToString());
        // Garrison: Corvane attacks a Merrin holding with garrison 2.
        Reset(); PrepareField(out admin);
        SetOwner("greywatch", "Merrin", Now.AddDays(-3));
        SetF(H("greywatch"), "Garrison", 2);
        Mk("M1", "Merrin", 2000, 2000); Mk("M2", "Merrin", 2000, 2100);
        Mk("C1", "Corvane", 1000, 1000); Mk("C2", "Corvane", 3000, 3000);
        Run(1);
        double g = Progress("greywatch");
        Ok(Math.Abs(g - 5 * (100.0 / (300 * 1.5))) < 0.01, "garrison II: a capture takes 50% longer", g.ToString());
    }

    static void ContestRules()
    {
        Player admin;
        PrepareField(out admin);
        SetOwner("tollbridge", "Merrin", Now.AddDays(-1));
        var m1 = Mk("M1", "Merrin", 0, 0); Mk("M2", "Merrin", 3000, 0);
        var c1 = Mk("C1", "Corvane", 5, 0); Mk("C2", "Corvane", 3000, 50);
        Clear();
        Run(3);
        Ok(State("tollbridge") == "contested" && Progress("tollbridge") == 0, "a defender and an attacker together: contested, no banner moves");
        Ok(c1.All().Contains("contested") && c1.All().Split('\n').Count(l => l.Contains("contested")) == 1, "the field is told once", c1.All());
        // Treaty allies of the holder count for no one.
        At(m1, 3000, 0);
        var a1 = Mk("Ash1", "Ashgrove", 0, 5); Mk("Ash2", "Ashgrove", 3000, 80);
        Treaty("Ashgrove", "Merrin");
        Run(1);
        Ok(State("tollbridge") == "capturing" && Capturer("tollbridge") == "Corvane", "the holder's ally does not defend: the attacker's banner rises", State("tollbridge"));
        At(c1, 3000, 0);
        double before = Progress("tollbridge");
        Run(3);
        Ok(Capturer("tollbridge") == "Corvane" && Progress("tollbridge") <= before, "the holder's ally alone cannot take it either (no banner of its own rises)", Progress("tollbridge") + " " + before);
        EndTreaty("Ashgrove", "Merrin");
        // Two hostile attacking houses contest each other.
        Reset(); PrepareField(out admin);
        Mk("C1", "Corvane", 0, 0); Mk("C2", "Corvane", 3000, 50);
        Mk("Ash1", "Ashgrove", 0, 5); Mk("Ash2", "Ashgrove", 3000, 80);
        Run(2);
        Ok(State("tollbridge") == "contested" && Progress("tollbridge") == 0, "two hostile attackers contest an unclaimed holding");
        // ... but sworn allies do not; the house with more men leads.
        LiegeOf["Ashgrove"] = "Corvane";
        Mk("C3", "Corvane", 1, 1);
        Run(1);
        Ok(State("tollbridge") == "capturing" && Capturer("tollbridge") == "Corvane", "a vassal and its liege do not contest; the larger house leads", State("tollbridge") + " " + Capturer("tollbridge"));
        LiegeOf.Clear();
        LiegeOf["Ashgrove"] = "Varrow"; LiegeOf["Corvane"] = "Varrow";
        Run(1);
        Ok(State("tollbridge") == "capturing", "two vassals of the same liege do not contest");
        LiegeOf.Clear();
        // MaxHoldingsPerHouse
        Reset(); PrepareField(out admin);
        SetCfg("MaxHoldingsPerHouse", 1);
        SetOwner("greywatch", "Corvane", Now.AddDays(-1));
        var c = Mk("C1", "Corvane", 0, 0); Mk("C2", "Corvane", 3000, 50);
        Run(2);
        Ok(State("tollbridge") == "capped" && Progress("tollbridge") == 0, "a house at MaxHoldingsPerHouse cannot take another");
        Ok(c.All().Contains("already holds 1 holdings"), "and is told", c.All());
    }

    static void BannersAndDecay()
    {
        Player admin;
        PrepareField(out admin);
        var c1 = Mk("C1", "Corvane", 0, 0); Mk("C2", "Corvane", 3000, 50);
        Run(30);
        double p = Progress("tollbridge");
        Ok(Capturer("tollbridge") == "Corvane" && p > 49 && p < 51, "Corvane's banner at 50%", p.ToString());
        // A rival tears it down before raising its own.
        At(c1, 3000, 0);
        var a1 = Mk("Ash1", "Ashgrove", 0, 0); Mk("Ash2", "Ashgrove", 3000, 80);
        Run(15);
        Ok(Capturer("tollbridge") == "Corvane" && Progress("tollbridge") < p - 20, "a rival first tears the old banner down", Progress("tollbridge").ToString());
        Run(20);
        Ok(Capturer("tollbridge") == "Ashgrove", "then raises its own", Capturer("tollbridge") ?? "null");
        // Empty field: the banner holds DecayDelaySeconds, then falls DecayPerMinute.
        At(a1, 3000, 0);
        double q = Progress("tollbridge");
        Run(11);
        Ok(Math.Abs(Progress("tollbridge") - q) < 0.01, "an empty field: the banner holds for DecayDelaySeconds", Progress("tollbridge") + " vs " + q);
        Run(12);
        Ok(Progress("tollbridge") < q && Progress("tollbridge") > q - 11, "then falls slowly (10 per minute)", Progress("tollbridge") + " vs " + q);
        // Defenders tear down an attacker's banner on their holding.
        Reset(); PrepareField(out admin);
        SetOwner("tollbridge", "Merrin", Now.AddDays(-1));
        var m1 = Mk("M1", "Merrin", 3000, 0); Mk("M2", "Merrin", 3000, 60);
        c1 = Mk("C1", "Corvane", 0, 0); Mk("C2", "Corvane", 3000, 50);
        Run(30);
        p = Progress("tollbridge");
        At(c1, 3000, 0); At(m1, 0, 0);
        Run(10);
        Ok(State("tollbridge") == "defending" && Progress("tollbridge") < p - 10, "defenders alone tear the attacker's banner down", State("tollbridge") + " " + Progress("tollbridge"));
        Run(30);
        Ok(Progress("tollbridge") == 0 && Capturer("tollbridge") == null, "until it is gone");
        // The window closing drops every unfinished banner.
        At(c1, 0, 0); At(m1, 3000, 0);
        Run(10);
        Ok(Progress("tollbridge") > 0, "a banner rises again");
        Now = new DateTime(2026, 10, 7, 22, 30, 1, DateTimeKind.Utc);
        Tick();
        Ok(Progress("tollbridge") == 0 && Capturer("tollbridge") == null && Owner("tollbridge") == "Merrin", "the War Hours end: unfinished banners fall, the holder keeps it");
        Run(10);
        Ok(Progress("tollbridge") == 0, "outside the War Hours nothing rises");
        // Pauses freeze, they do not reset.
        Now = WarHours.AddDays(3).AddMinutes(30);    // Saturday 21:00
        Now = new DateTime(2026, 10, 10, 21, 1, 0, DateTimeKind.Utc);
        Tick();
        Run(30);
        p = Progress("tollbridge");
        Truce = true;
        Run(30);
        Ok(Math.Abs(Progress("tollbridge") - p) < 0.01, "a truce freezes the banner where it stands", Progress("tollbridge") + " vs " + p);
        Truce = false;
        // A server hitch never jumps a banner.
        Now = Now.AddMinutes(10);
        double before = Progress("tollbridge");
        Tick();
        Ok(Progress("tollbridge") - before < 5 * 3 * (100.0 / 300) + 0.01, "a 10-minute hitch counts as at most three ticks", (Progress("tollbridge") - before).ToString());
    }

    static void Eligibility()
    {
        Player admin;
        PrepareField(out admin);
        var nobody = Mk("Loner", null, 0, 0);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a player of no house counts for no one");
        Cmd(nobody, "here");
        Ok(Said(nobody).Contains("you belong to no house"), "/dominion here says why", "");
        // A young house.
        House("Thornwick", Now.AddHours(-2).ToString("o"));
        var t1 = Mk("T1", "Thornwick", 0, 0); Mk("T2", "Thornwick", 0, 1);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a house founded 2 h ago (MinHouseAgeHours 24) cannot make war");
        Cmd(t1, "here");
        Ok(Said(t1).Contains("too young or too small"), "and is told", "");
        Server.ClientPlayers.Remove(t1); Server.ClientPlayers.RemoveAll(p => p.Name == "T2");
        // A one-man house.
        var solo = Mk("Solo", "Merrin", 0, 0);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a house of one (MinHouseMembers 2) cannot make war");
        ExtraMembers["Merrin"] = 1;
        Run(1);
        Ok(Capturer("tollbridge") == "Merrin", "an offline second member is enough");
        Server.ClientPlayers.Remove(solo);
        // House hopping: outside the install grace, a player seen in a new house counts after MinMembershipHours.
        Reset(); PrepareField(out admin);
        SetF(Data(), "InstalledAt", Now.AddDays(-10));
        var hop = Mk("Hopper", "Corvane", 0, 0); Mk("C2", "Corvane", 3000, 0);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a player first seen in a house now counts only after MinMembershipHours");
        Cmd(hop, "here");
        Ok(Said(hop).Contains("joined House [8FB0BF]Corvane[FFFFFF] too lately; you count in 12 h"), "and is told when", "");
        var seen = (IDictionary)F(Data(), "Members");
        SetF(seen[hop.Id.ToString()], "Since", Now.AddHours(-13));
        Run(1);
        Ok(Capturer("tollbridge") == "Corvane", "after 12 h in the house they count");
        HouseOf[hop.Id] = "Ashgrove";
        Run(1);
        Ok(((string)F(seen[hop.Id.ToString()], "House")) == "Ashgrove" && (DateTime)F(seen[hop.Id.ToString()], "Since") == Now, "changing house restarts the clock");
        Server.ClientPlayers.Remove(hop);
        // New-player protection, staff, the dead, the offline, the reconnecting, height.
        Reset(); PrepareField(out admin);
        var v1 = Mk("V1", "Varrow", 0, 0); Mk("V2", "Varrow", 3000, 0);
        Protected.Add(v1.Id);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a player under new-player protection counts for no one");
        Cmd(v1, "here"); Ok(Said(v1).Contains("new-player protection"), "and is told", "");
        Protected.Clear();
        P.permission.Grants.Add(v1.Id + "|" + Admin);
        Run(2);
        Ok(Progress("tollbridge") == 0, "staff count for no one (AdminsCount false)");
        P.permission.Grants.Clear();
        P.permission.Grants.Add(admin.Id + "|" + Admin);
        v1.Alive = false;
        Run(2);
        Ok(Progress("tollbridge") == 0, "the dead count for no one");
        v1.Alive = true;
        At(v1, 0, 0, 80);
        Run(2);
        Ok(Progress("tollbridge") == 0, "60 m above the centre is outside (VerticalRange 40)");
        At(v1, 0, 0, 10);
        Leave(v1);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a player who logged off (a sleeper) counts for no one");
        Connect(v1);
        Run(2);
        Ok(Progress("tollbridge") == 0, "a player who just logged in inside counts only after ReconnectGraceSeconds");
        Cmd(v1, "here"); Ok(Said(v1).Contains("you just arrived; you count in"), "and is told", "");
        Run(11);
        Ok(Capturer("tollbridge") == "Varrow", "after 60 s they count");
        At(v1, 39, 0);
        double p = Progress("tollbridge");
        Run(1);
        Ok(Progress("tollbridge") > p, "39 m from the centre is inside");
        At(v1, 41, 0);
        p = Progress("tollbridge");
        Run(1);
        Ok(Progress("tollbridge") == p, "41 m is outside");
        // RealmHouses not loaded: nobody counts.
        At(v1, 0, 0);
        HousesLoaded = false;
        p = Progress("tollbridge");
        Run(1);
        Ok(Progress("tollbridge") == p, "without RealmHouses nobody counts");
        HousesLoaded = true;
    }

    static void Payday()
    {
        Player admin;
        PrepareField(out admin);
        SetOwner("tollbridge", "Varrow", Now.AddDays(-2));
        SetOwner("greywatch", "Varrow", Now.AddDays(-2));
        SetOwner("ember-mines", "Corvane", Now.AddHours(-1));    // too fresh for income
        var v = Mk("V1", "Varrow", 3000, 3000); Mk("V2", "Varrow", 3100, 3000);
        Clear();
        Now = new DateTime(2026, 10, 7, 20, 59, 0, DateTimeKind.Utc);
        Run(1);
        Ok(Grants.Count == 0, "no payday before IncomeTime");
        Now = new DateTime(2026, 10, 7, 21, 0, 30, DateTimeKind.Utc);
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Tick();
        Ok(Grants.Count == 2 && Grants.Contains("Varrow|120|dominion") && Grants.Contains("Varrow|150|dominion"), "at IncomeTime each held holding pays its house through RealmTreasury", string.Join(";", Grants));
        Ok(Awards.Contains("Varrow|2|") && Awards.Contains("Varrow|3|"), "and season points (PointsPerDayByKind)", string.Join(";", Awards));
        Ok(!Grants.Any(g => g.StartsWith("Corvane")), "a holding taken under MinHeldHoursForIncome ago pays nothing yet");
        Ok(Garrison("tollbridge") == 1 && Garrison("ember-mines") == 0, "the garrison rises with each payday held");
        Ok(v.All().Contains("The Tollbridge paid House [C58FC0]Varrow[FFFFFF] 120 marks into the vault"), "the members are told", v.All());
        Ok(Heralds().Contains("The holdings pay their dues: House [C58FC0]Varrow[FFFFFF] 270 marks"), "the herald sums it up", Heralds());
        Clear();
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Run(1);
        Reload();
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Run(1);
        Inv(P, "CmdDominion", admin, "dominion", new[] { "admin", "payday" });
        Ok(Grants.Count == 0 && admin.All().Contains("Today's payday was made already"), "never twice in a day, even across a reload or an admin payday", string.Join(";", Grants) + admin.All());
        // Next day: garrison II pays 10% more each level.
        Clear();
        Now = new DateTime(2026, 10, 8, 21, 0, 30, DateTimeKind.Utc);
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Tick();
        Ok(Grants.Contains("Varrow|132|dominion"), "garrison I pays 10% more", string.Join(";", Grants));
        // An idle house is not paid.
        Clear();
        Server.ClientPlayers.RemoveAll(p => p.Name.StartsWith("V"));
        Now = new DateTime(2026, 10, 10, 21, 0, 30, DateTimeKind.Utc);
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Tick();
        Ok(!Grants.Any(g => g.StartsWith("Varrow")), "a house with no one seen for a day is not paid (IncomeRequiresActivity)", string.Join(";", Grants));
        // The treasury absent, or refusing.
        Reset(); PrepareField(out admin);
        SetOwner("tollbridge", "Varrow", Now.AddDays(-2));
        v = Mk("V1", "Varrow", 3000, 3000); Mk("V2", "Varrow", 3100, 3000);
        Run(1);
        TreasuryBudget = 0;
        Now = new DateTime(2026, 10, 7, 21, 0, 30, DateTimeKind.Utc);
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Clear();
        Tick();
        Ok(Grants.Count == 0 && v.All().Contains("The vault was not paid (the treasury refused"), "a refusing treasury: the members are told, points still paid", v.All());
        Ok(Awards.Contains("Varrow|2|"), "season points still count");
    }

    static void AbandonAndLineage()
    {
        Player admin;
        PrepareField(out admin);
        SetOwner("tollbridge", "Merrin", Now.AddDays(-5));
        SetOwner("greywatch", "Corvane", Now.AddDays(-5));
        Mk("C1", "Corvane", 3000, 3000);
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Run(1);
        Ok(Owner("tollbridge") == "Merrin", "a house not yet seen gets a fresh activity clock");
        Now = Now.AddDays(3).AddMinutes(1);
        Clear();
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Run(1);
        Ok(Owner("tollbridge") == null, "after AbandonAfterDays with no member online it returns to no one");
        Ok(Heralds().Contains("The Tollbridge lies abandoned"), "the herald tells", Heralds());
        Ok(Owner("greywatch") == "Corvane", "a house whose members play keeps its holdings");
        // The house falls.
        Founded.Remove("Corvane");
        Clear();
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Run(1);
        Ok(Owner("greywatch") == null && Heralds().Contains("House [8FB0BF]Corvane[FFFFFF] is no more"), "a disbanded house loses its holdings", Heralds());
        // A new house under a fallen house's name does not inherit.
        House("Corvane", "2026-10-09T00:00:00.0000000Z");
        SetOwner("greywatch", "Corvane", Now.AddDays(-1));
        SetF(H("greywatch"), "OwnerFounded", "2026-09-02T00:00:00.0000000Z");
        SetF(P, "lastSlowCheck", DateTime.MinValue);
        Run(1);
        Ok(Owner("greywatch") == null, "a house refounded under the same name does not inherit the holding");
    }

    static void GarrisonDamage()
    {
        Player admin;
        PrepareField(out admin);
        SetOwner("tollbridge", "Merrin", Now.AddDays(-1));
        var m = Mk("M1", "Merrin", 0, 0); Mk("M2", "Merrin", 3000, 0);
        var c = Mk("C1", "Corvane", 5, 0);
        Run(1);
        var e = Hit(m, c, 100f);
        Ok(Math.Abs(e.Damage.Amount - 90f) < 0.01, "a defender in their own holding takes 10% less from a foe", e.Damage.Amount.ToString());
        e = Hit(c, m, 100f);
        Ok(e.Damage.Amount == 100f, "the attacker gets no such shield");
        Treaty("Ashgrove", "Merrin");
        var a = Mk("Ash", "Ashgrove", 3, 3);
        e = Hit(m, a, 100f);
        Ok(e.Damage.Amount == 100f, "a blow from an ally is not reduced (it is not the garrison's fight)");
        At(m, 500, 0);
        e = Hit(m, c, 100f);
        Ok(e.Damage.Amount == 100f, "outside the holding: no shield");
        At(m, 0, 0);
        var ev = new CodeHatch.Networking.Events.Entities.EntityDamageEvent { Entity = m.Entity, Damage = new CodeHatch.Damaging.Damage { Amount = 100f, DamageSource = c.Entity }, Cancelled = true };
        Inv(P, "OnEntityHealthChange", ev);
        Ok(ev.Damage.Amount == 100f, "a blow another plugin cancelled is left alone");
        e = Hit(m, null, 100f);
        Ok(e.Damage.Amount == 100f, "damage with no player source is left alone");
        Now = new DateTime(2026, 10, 7, 23, 0, 0, DateTimeKind.Utc);
        Tick();
        e = Hit(m, c, 100f);
        Ok(e.Damage.Amount == 100f, "outside the War Hours: no shield");
    }

    static void Commands()
    {
        Player admin;
        PrepareField(out admin);
        SetOwner("greywatch", "Varrow", Now.AddDays(-2));
        SetF(H("greywatch"), "Garrison", 2);
        var p = Mk("Ada", "Corvane", 1000, 1030); Mk("Cadoc", "Corvane", 3000, 3000);
        Run(1);
        Clear();
        Cmd(p, "");
        string s = Said(p);
        Ok(s.Contains("The War Hours are on") && s.Contains("The holdings of Ostreval (7):"), "/dominion: the War Hours and the holdings", s);
        Ok(s.Contains("Greywatch Keep (keep) - House [C58FC0]Varrow[FFFFFF], garrison II, 180 marks a day"), "a held holding: house, garrison, income", s);
        Ok(s.Contains("The Tollbridge (crossroads) - unclaimed, 120 marks a day"), "an unclaimed holding", s);
        Ok(p.Popups.Count == 1 && p.Popups[0].StartsWith("The Holdings of Ostreval|") && p.Popups[0].EndsWith("|True") && !p.Popups[0].Contains("[C58FC0]"),
            "a popup with the same map, plain text, broadcast = true", string.Join("\n", p.Popups));
        Ok(StyleProblems(p.Messages).Length == 0, "chat style", StyleProblems(p.Messages));
        PopupsWanted = false; p.Popups.Clear();
        Cmd(p, ""); Said(p);
        Ok(p.Popups.Count == 0, "no popup for a player who turned popups off (RealmHerald PopupsWanted)");
        PopupsWanted = true;
        CodeHatch.Common.PlayerExtensions.ThrowOnPopup = true;
        Cmd(p, "");
        Ok(Said(p).Contains("The holdings of Ostreval") && P.Log.Any(l => l.Contains("ShowPopup failed")), "a failing popup leaves the chat version");
        CodeHatch.Common.PlayerExtensions.ThrowOnPopup = false;
        Cmd(p, "greywatch");
        s = Said(p);
        Ok(s.Contains("Greywatch Keep (keep)") && s.Contains("roofless watch-keep") && s.Contains("Garrison II of IV: captures take 50% longer"), "/dominion <id>: the holding in full", s);
        Ok(s.Contains("You are 30 m from it") && s.Contains("80 m across"), "with where it lies and how far you are", s);
        Cmd(p, "the ember"); s = Said(p);
        Ok(s.Contains("The Ember Mines (mine)") && s.Contains("Not yet marked"), "names match without 'The' and by prefix", s);
        Cmd(p, "drowned harbour"); Ok(Said(p).Contains("The Drowned Harbour"), "a full name with spaces");
        Cmd(p, "nowhere"); s = Said(p);
        Ok(s.StartsWith("ERR ") && s.Contains("No holding is called 'nowhere'"), "an unknown holding is refused", s);
        Cmd(p, "the"); Ok(Said(p).Contains("No holding is called"), "an ambiguous prefix is refused");
        Cmd(p, "here"); s = Said(p);
        Ok(s.Contains("You stand in Greywatch Keep") && s.Contains("In the field now: [8FB0BF]Corvane[FFFFFF] 1") && s.Contains("you count for House"), "/dominion here: the field and you", s);
        At(p, 5000, 5000);
        Cmd(p, "here"); Ok(Said(p).Contains("You stand in no holding"), "/dominion here outside every holding");
        Cmd(p, "rules"); s = Said(p);
        Ok(s.Contains("only the holder's own members defend") && s.Contains("2 members and 24 h") && s.Contains("10% less harm") && s.Contains("3 days"), "/dominion rules reads its numbers from the config", s);
        Ok(StyleProblems(p.Messages).Length == 0, "chat style");
        Cmd(p, "admin status"); s = Said(p);
        Ok(s.StartsWith("ERR ") && s.Contains("You may not do that"), "players may not use admin commands", s);
        // Enter notices are throttled.
        At(p, 0, 0); Run(1);
        At(p, 3000, 3000); Run(1);
        At(p, 0, 0); Run(1);
        Ok(p.All().Split('\n').Count(l => l.Contains("You enter The Tollbridge")) == 1, "entering a holding is told once per 5 minutes", p.All());
    }

    static void AdminCommands()
    {
        Load();
        var admin = MkAdmin("Steward", 100, 200);
        Cmd(admin, "admin"); Ok(Said(admin).Contains("/dominion admin[FFFFFF] status"), "/dominion admin alone shows the help");
        Cmd(admin, "admin create harbour2 harbour 30 The Salt Quay"); string s = Said(admin);
        var h = H("harbour2");
        Ok(h != null && (string)F(h, "Name") == "The Salt Quay" && (float)F(h, "X") == 100 && (float)F(h, "Z") == 200 && (float)F(h, "Radius") == 30 && (bool)F(h, "Placed"), "create: a new holding where the admin stands", s);
        Cmd(admin, "admin create harbour2 village The Other"); Ok(Said(admin).Contains("exists already"), "create: a taken id is refused");
        Cmd(admin, "admin create Bad_Id village Name"); Ok(Said(admin).Contains("is not"), "create: a bad id is refused");
        Cmd(admin, "admin create fort castle Name"); Ok(Said(admin).Contains("'castle' is not a kind"), "create: an unknown kind is refused");
        Cmd(admin, "admin create fort keep 500 Name"); Ok(Said(admin).Contains("A holding is 20 to 300 m across"), "create: a radius out of range is refused");
        Cmd(admin, "admin create fort keep 30"); Ok(Said(admin).Contains("Give the holding a name"), "create: a name is required");
        Cmd(admin, "admin create fort keep [FF0000]Red{0} Fort"); s = Said(admin);
        Ok((string)F(H("fort"), "Name") == "FF0000Red0 Fort", "create: colour tags and braces are cleaned out of a name", (string)F(H("fort"), "Name"));
        At(admin, 7, 8);
        Cmd(admin, "admin move tollbridge 25"); s = Said(admin);
        h = H("tollbridge");
        Ok((float)F(h, "X") == 7 && (float)F(h, "Radius") == 25 && (bool)F(h, "Placed") && s.Contains("now lies here, 50 m across"), "move: centre and radius", s);
        Cmd(admin, "admin radius tollbridge 45"); Ok((float)F(H("tollbridge"), "Radius") == 45, "radius");
        Cmd(admin, "admin radius tollbridge 2"); Ok(Said(admin).Contains("A holding is"), "radius: out of range refused");
        Cmd(admin, "admin rename tollbridge The Old Toll"); Ok((string)F(H("tollbridge"), "Name") == "The Old Toll", "rename");
        Cmd(admin, "admin owner tollbridge Corvane"); Ok(Owner("tollbridge") == "Corvane", "owner: set a house");
        Cmd(admin, "admin owner tollbridge corvane"); Ok(Owner("tollbridge") == "Corvane", "owner: the house's own spelling is kept");
        Cmd(admin, "admin owner tollbridge Nobody"); Ok(Said(admin).Contains("No house is called 'Nobody'") && Owner("tollbridge") == "Corvane", "owner: an unknown house is refused");
        Cmd(admin, "admin owner tollbridge none"); Ok(Owner("tollbridge") == null, "owner none");
        Cmd(admin, "admin disable tollbridge"); Ok(!(bool)F(H("tollbridge"), "Enabled"), "disable");
        Cmd(admin, ""); Ok(Said(admin).Contains("closed by the stewards"), "a disabled holding shows as closed");
        Cmd(admin, "admin enable tollbridge"); Ok((bool)F(H("tollbridge"), "Enabled"), "enable");
        SetF(H("tollbridge"), "Progress", 40.0); SetF(H("tollbridge"), "Capturer", "Varrow");
        Cmd(admin, "admin reset tollbridge"); Ok(Progress("tollbridge") == 0 && Capturer("tollbridge") == null, "reset clears banners");
        Cmd(admin, "admin remove fort"); Ok(Said(admin).Contains("remove fort confirm") && H("fort") != null, "remove asks first");
        Cmd(admin, "admin remove harbour2 confirm"); Ok(H("harbour2") != null && Said(admin).Contains("remove harbour2 confirm"), "confirm for another holding than the one asked about only asks again");
        Cmd(admin, "admin remove fort confirm"); Ok(H("fort") != null, "the first question was replaced");
        Cmd(admin, "admin remove fort"); Cmd(admin, "admin remove fort confirm"); Ok(H("fort") == null, "remove confirm");
        Cmd(admin, "admin move nowhere"); Ok(Said(admin).Contains("No holding is called 'nowhere'"), "an unknown holding is refused");
        Cmd(admin, "admin open 0"); Ok(Said(admin).Contains("is not a number from 1 to 1440"), "open: minutes out of range refused");
        Cmd(admin, "admin status"); s = Said(admin);
        Ok(s.Contains("Field: closed (none)") && s.Contains("Treasury: loaded") && s.Contains("Payday at 21:00"), "status", s);
        SetCfg("MaxHoldings", 9);
        Cmd(admin, "admin create a1 village One");
        Cmd(admin, "admin create a2 village Two"); Ok(Said(admin).Contains("at most 9 holdings") && H("a1") != null && H("a2") == null, "MaxHoldings caps the map");
        Reload();
        Ok(H("a1") != null && (string)F(H("tollbridge"), "Name") == "The Old Toll", "admin changes survive a reload");
    }

    static void MapFileAndApi()
    {
        Player admin;
        PrepareField(out admin);
        SetOwner("greywatch", "Varrow", Now.AddDays(-2));
        Mk("C1", "Corvane", 0, 0); Mk("C2", "Corvane", 3000, 0);
        Run(4);
        Inv(P, "WriteMap");
        var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File("RealmDominionMap"))).RootElement;
        Ok(doc.GetProperty("schema").GetInt32() == 1 && doc.GetProperty("window").GetProperty("state").GetString() == "open", "map file: schema 1 and the window", doc.ToString().Substring(0, 120));
        var hs = doc.GetProperty("holdings");
        Ok(hs.GetArrayLength() == 7, "map file: every holding");
        var toll = hs.EnumerateArray().First(x => x.GetProperty("id").GetString() == "tollbridge");
        Ok(toll.GetProperty("capturer").GetString() == "Corvane" && toll.GetProperty("progress").GetInt32() >= 6 && toll.GetProperty("state").GetString() == "capturing"
            && toll.GetProperty("placed").GetBoolean() && toll.GetProperty("radius").GetSingle() == 40 && toll.GetProperty("incomePerDay").GetInt64() == 120,
            "map file: banner, state, place and income", toll.ToString());
        var houses = doc.GetProperty("houses");
        Ok(houses.GetArrayLength() == 1 && houses[0].GetProperty("name").GetString() == "Varrow" && houses[0].GetProperty("holdings").GetInt32() == 1, "map file: houses with their holdings", houses.ToString());
        var list = (List<Dictionary<string, object>>)Inv(P, "GetHoldings");
        Ok(list.Count == 7 && list.Any(d => (string)d["id"] == "greywatch" && (string)d["owner"] == "Varrow"), "GetHoldings");
        Ok((string)Inv(P, "GetHoldingOwner", "greywatch") == "Varrow" && Inv(P, "GetHoldingOwner", "nowhere") == null, "GetHoldingOwner");
        Ok((int)Inv(P, "GetHouseHoldingCount", "varrow") == 1 && (int)Inv(P, "GetHouseHoldingCount", "Corvane") == 0, "GetHouseHoldingCount");
        string w = (string)Inv(P, "GetDominionWindow");
        Ok(w.StartsWith("The War Hours are on") && !w.Contains("["), "GetDominionWindow: plain text", w);
        var b = (Dictionary<string, object>)Inv(P, "GetDominionBoard");
        var rows = (List<Dictionary<string, object>>)b["rows"];
        Ok((string)b["title"] == "Dominion" && rows.Count == 2 && rows.Any(r => (string)r["right"] == "Corvane 8%" || ((string)r["right"]).StartsWith("Corvane ")) && rows.Any(r => (string)r["right"] == "0"),
            "GetDominionBoard: placed holdings with banner or garrison", string.Join(";", rows.Select(r => r["name"] + "=" + r["right"])));
        // PublishMap false: no file.
        System.IO.File.Delete(File("RealmDominionMap"));
        SetCfg("PublishMap", false);
        Inv(P, "WriteMap");
        Ok(!System.IO.File.Exists(File("RealmDominionMap")), "PublishMap false writes no map file");
    }

    static void DamagedData()
    {
        Load();
        var admin = MkAdmin("Steward", 1, 2);
        Place(admin, "tollbridge", 1, 2, 40);
        Inv(P, "Unload");
        string good = System.IO.File.ReadAllText(File("RealmDominion"));
        string cut = good.Substring(0, good.Length / 2);
        System.IO.File.WriteAllText(File("RealmDominion"), cut);
        P = NewPlugin(P.Config.Json);
        Ok((bool)F(P, "loadFailed") && P.Log.Any(l => l.StartsWith("ERROR") && l.Contains("will not run")), "a truncated data file: the plugin refuses to run");
        var p = Mk("Ada", "Varrow");
        Cmd(p, ""); Ok(Said(p).Contains("Dominion is paused"), "and says so");
        Tick(); Inv(P, "OnServerSave"); Inv(P, "Unload");
        Ok(System.IO.File.ReadAllText(File("RealmDominion")) == cut, "and never overwrites the file");
        Ok(Inv(P, "GetHoldings") is List<Dictionary<string, object>> l && l.Count == 0 && Inv(P, "GetDominionBoard") == null, "its API answers empty");
        System.IO.File.WriteAllText(File("RealmDominion"), "null");
        P = NewPlugin(P.Config.Json);
        Ok((bool)F(P, "loadFailed"), "a file holding null: refused");
        System.IO.File.WriteAllText(File("RealmDominion"), "{}");
        P = NewPlugin(P.Config.Json);
        Ok((bool)F(P, "loadFailed") && System.IO.File.ReadAllText(File("RealmDominion")) == "{}", "an empty object: refused, not reseeded over");
        System.IO.File.WriteAllText(File("RealmDominion"), good);
        P = NewPlugin(P.Config.Json);
        Ok(!(bool)F(P, "loadFailed") && (bool)F(H("tollbridge"), "Placed"), "the good file loads again");
        // Bad config values are clamped.
        P = NewPlugin(Config(c => { c["CaptureSeconds"] = 1; c["DuringRebellion"] = "riot"; c["IncomeTime"] = "25:99"; c["MaxCountedPerHouse"] = 0; c["GarrisonDamageReductionPercent"] = 90; }));
        Ok((int)F(Cfg(), "CaptureSeconds") == 10 && (string)F(Cfg(), "DuringRebellion") == "pause" && (string)F(Cfg(), "IncomeTime") == "21:00"
            && (int)F(Cfg(), "MaxCountedPerHouse") == 1 && (float)F(Cfg(), "GarrisonDamageReductionPercent") == 50f, "bad config values are clamped to safe ones");
        P = NewPlugin(Config(c => { c.Remove("IncomeByKind"); c["Windows"] = null; }));
        Ok(((IDictionary)F(Cfg(), "IncomeByKind")).Count == 5 && ((IList)F(Cfg(), "Windows")).Count == 3, "missing tables come back with their defaults");
    }

    static void Switches()
    {
        Player admin;
        PrepareField(out admin);
        SetCfg("Enabled", false);
        var p = Mk("Ada", "Varrow", 0, 0); Mk("Bryn", "Varrow", 3000, 0);
        Run(5);
        Ok(Progress("tollbridge") == 0, "Enabled false: nothing is counted");
        Cmd(p, ""); Ok(Said(p).Contains("switched off"), "and players are told");
        SetCfg("Enabled", true);
        SetCfg("AnnounceCaptures", false);
        SetCfg("CaptureSeconds", 10);
        Clear();
        Run(4);
        Ok(Owner("tollbridge") == "Varrow" && !Heralds().Contains("takes"), "AnnounceCaptures false: captures happen without the herald", Heralds());
        Ok(Chron.Count == 1, "the Chronicle still records it");
        SetCfg("GarrisonDamageReduction", false);
        var c = Mk("C", "Corvane", 1, 1);
        var e = Hit(p, c, 50f);
        Ok(e.Damage.Amount == 50f, "GarrisonDamageReduction false: no shield");
        SetCfg("RenownDeed", "");
        SetCfg("CapturePoints", 0);
        Reset(); PrepareField(out admin);
        SetCfg("RenownDeed", ""); SetCfg("CapturePoints", 0); SetCfg("CaptureSeconds", 10);
        Mk("Ada", "Varrow", 0, 0); Mk("Bryn", "Varrow", 3000, 0);
        Run(4);
        Ok(Owner("tollbridge") == "Varrow" && Deeds.Count == 0 && Awards.Count == 0, "RenownDeed empty and CapturePoints 0: no rewards");
        SetCfg("ShowEnterNotices", false);
        var q = Mk("Quill", "Corvane", 3000, 3000);
        At(q, 1000, 1000); Run(1);
        Ok(!q.All().Contains("You enter"), "ShowEnterNotices false: no enter notice");
        SetCfg("UsePopups", false);
        Cmd(q, ""); Ok(q.Popups.Count == 0, "UsePopups false: no window");
    }
}
