// Behaviour tests for plugins/RealmArrival.cs, compiled unchanged with Mocks.cs and World.cs. Run with run.sh.
// What this proves: the plugin's own rules (records and variants, the cut, narration and its timings, the gate and the
// flare, banners and pledges, the fire, handover, follow-ups, skipping, log-off, respawns, safety, admin, data safety).
// What it does NOT prove: that the real game behaves like the mocks (see plugins/docs/RealmArrival.md, first-test plan).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Common;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Players;
using CodeHatch.StarForge.Sleeping;
using CodeHatch.UserInterface.Dialogues;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static partial class Tests
{
    const ulong N1 = 76561190000000101, N2 = 76561190000000102, N3 = 76561190000000103, N4 = 76561190000000104, N5 = 76561190000000105,
        V1 = 76561190000000201, V2 = 76561190000000202, M1 = 76561190000000301, M2 = 76561190000000302;

    static int Main(string[] argv)
    {
        if (argv.Length > 0) Repo = argv[0];
        Run("Setup", Setup); Run("Records", Records); Run("VariantB", VariantB); Run("Seeding", Seeding); Run("TheCut", TheCut);
        Run("ArrivalChecks", ArrivalChecks); Run("Narration", Narration); Run("GateTriggers", GateTriggers); Run("Reveal", Reveal);
        Run("Banners", Banners); Run("FastWalker", FastWalker); Run("Pledges", Pledges); Run("Fire", Fire); Run("Handover", Handover);
        Run("Nudges", Nudges); Run("Wander", Wander); Run("Timeout", Timeout); Run("Afk", Afk); Run("Resume", Resume);
        Run("StageApi", StageApi); Run("Stones", Stones); Run("Provider", Provider); Run("Respawns", Respawns); Run("Portcullis", Portcullis);
        Run("Flare", Flare); Run("CellGuard", CellGuard); Run("Sanctuary", Sanctuary); Run("Eviction", Eviction); Run("Shield", Shield);
        Run("ZoneGuard", ZoneGuard); Run("Popups", Popups); Run("Play", Play); Run("HeraldCap", HeraldCap); Run("SkipTour", SkipTour);
        Run("HourOne", HourOne); Run("Page", Page); Run("Admin", AdminCmds); Run("SiteCheck", SiteCheck); Run("SelfCheck", SelfCheck);
        Run("RoadMode", RoadMode); Run("Wave", Wave); Run("Reload", ReloadRebuild); Run("DataSafety", DataSafety);
        Run("SitePlan", SitePlan); Run("PlanArrival", PlanArrival); Run("ReviewFixes", ReviewFixes); Run("ChatStyle", ChatStyle);
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL " + name + " threw: " + ex); }
    }

    // A full default world: the site stored and open, gate mode open.
    static Player Site(bool gate = false, bool beacon = false, Action<object> tweak = null)
    {
        Reset();
        NewArrival(tweak);
        var adm = BuildSite(true, gate, beacon);
        Clear();
        return adm;
    }

    // From the Finish click to the start of narration (no move: the 12 s cap).
    static Player ToNarration(ulong id, string name)
    {
        var p = Newcomer(id, name);
        Advance(12.5);
        return p;
    }

    // Through the gate on the gold line.
    static Player ThroughGate(ulong id, string name)
    {
        var p = ToNarration(id, name);
        Advance(5);                       // after the Naming line
        At(p, 11, 22);
        Advance(1);
        return p;
    }

    // Walks the avenue at 1.5 m/s with a stop at each pair, to the fire.
    static void WalkToFire(Player p)
    {
        Walk(p, 11, 46, 3f); Advance(16);
        Walk(p, 11, 74, 3f); Advance(16);
        Walk(p, 11, 103, 3f); Advance(16);
        Walk(p, 11, 126, 3f);
    }

    static void Setup()
    {
        Reset();
        NewArrival();
        Ok(A.permission.Registered.Contains("realmarrival.admin") && A.permission.Registered.Contains("realmarrival.skip"), "registers realmarrival.admin and realmarrival.skip");
        Ok(A.timer.EveryCount >= 1 && A.timer.All.Any(t => t.Repeat && Math.Abs(t.Interval - 1f) < 0.01), "one tick timer, every second");
        Ok((bool)F(Cfg(), "Open") == false && (string)F(Cfg(), "RoutingMode") == "teleport" && (string)F(Cfg(), "GateMode") == "open"
            && (string)F(Cfg(), "VeteranMode") == "vanilla", "defaults: closed, teleport routing, the open arch, vanilla veterans");
        Ok((int)F(Cfg(), "AutoOpenSeconds") == 60 && (int)F(Cfg(), "LineGapSeconds") == 4 && (int)F(Cfg(), "TimeoutMinutes") == 8
            && (int)F(Cfg(), "MercyRespawns") == 1 && (int)F(Cfg(), "NewcomerBroadcastsPerHour") == 6, "defaults follow the design (6.3)");
        var lang = A.lang.Msgs;
        Ok(lang["Speaker"] == "Hearth" && lang["Herald"] == "[D6A043]Herald[FFFFFF]: ", "speaker Hearth; the one Herald voice");
        var tooLong = lang.Where(kv => Regex.Replace(kv.Value, @"\[[0-9A-Fa-f]{6}\]", "").Length > 200).Select(kv => kv.Key).ToList();
        Ok(tooLong.Count == 0, "no lang line is longer than 200 visible characters", string.Join(",", tooLong));
        Ok(lang.Count >= 65, "about 65 lang keys or more", lang.Count.ToString());
        foreach (var k in new[] { "Wake", "Naming", "CallGate", "CallOpen", "GateSelf", "HeraldGate", "ReleasedGate", "RevealEmpty", "RevealKing",
            "RevealNight", "Walk", "HouseLive", "HouseUnclaimed", "PledgeHint", "SeekRaven", "OwnBanner", "Skipped", "PledgeDwell", "PledgeSent",
            "PledgeNoneAwake", "PledgeToHouse", "PledgeLimit", "Warm", "Kit", "Crown", "Shelter", "Unsheltered", "Next", "NextNoTale", "Written",
            "Nudge", "NudgeCompass", "Wander", "RoadMode", "ResumeHall", "ResumeRoad", "Veteran", "SkipDone", "Mercy", "MidDeath", "Evicted",
            "RoadsA", "RoadsB", "NextEvent", "ProtectionSoon", "FirstBlock", "Dusk", "Sleeper", "Page" })
            if (!lang.ContainsKey(k)) Ok(false, "lang key " + k + " exists");
        Ok(GreatHousesHaveLines(lang), "every great house has its banner line (House.<id>)");
        Ok(!File.Exists(Path.Combine(Dir, "RealmHerald.json")), "seeding never creates RealmHerald.json");
    }

    static bool GreatHousesHaveLines(Dictionary<string, string> lang)
    {
        return new[] { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" }.All(h => lang.ContainsKey("House." + h) && lang["House." + h].StartsWith("{0}, the "));
    }

    static void Records()
    {
        Site();
        var n = Mk(N1, "Ada");
        Ok(Stage(n) == "pending" && n.Messages.Count == 0, "connect: a pending record, nothing sent");
        Ok(ArrivalStage(n) == "pending" && Owns(n), "ArrivalStage pending; OwnsArrival true");
        Ok(ArrivalStage(new Player(N5, "Nobody")) == "pending", "an id with no record is pending while open (order of OnPlayerConnected does not matter)");
        var e = FirstSpawn(n, true);
        Ok(Stage(n) == "crossing" && (string)RecF(n, "Variant") == "A", "AtFirstSpawn true: variant A, stage crossing");
        Ok(e.AtFirstSpawn && e.Position.x == Raft.x && e.Position.z == Raft.z, "the first-spawn event is never changed (the raft and creation stand)");
        Ok(ArrivalStage(n) == "crossing" && Owns(n), "ArrivalStage crossing");
        // A crossing never goes stale: creation can take any time, even days.
        Offline(n);
        Clock = Clock.AddHours(72);
        Online(n);
        Ok(Stage(n) == "crossing", "a crossing record never goes stale");
        FirstSpawn(n, true);
        Ok(Stage(n) == "crossing", "the Crossing replays on the next join (AtFirstSpawn again)");
        // Variant C: a returning character never seen in OnPlayerSpawn.
        var c = Mk(V1, "Old Hand");
        FirstSpawn(c, false);
        Ok(Stage(c) == "done" && (string)RecF(c, "Variant") == "C", "AtFirstSpawn false with a pending record: saved as done (a known veteran)");
        Ok(ArrivalStage(c) == "none", "ArrivalStage none on its first sight (Herald behaves as today)");
        Offline(c); Online(c);
        Ok(ArrivalStage(c) == "done", "ArrivalStage done from the next session");
        Ok(c.Messages.Count == 0 && Pos(c).x == 300, "variant C: nothing new, the game's own position");
        // An in-arrival stage with AtFirstSpawn true: the world was reset mid-arrival, so it restarts.
        var r = ToNarration(N2, "Bea");
        Ok(Stage(r) == "gatehouse", "the arrival is under way");
        Offline(r); Online(r);
        FirstSpawn(r, true);
        Ok(Stage(r) == "crossing" && (string)RecF(r, "Variant") == "A", "a world reset mid-arrival: the arrival restarts at the Crossing");
        // A forged or repeated OnPlayerSpawned does nothing.
        var d = Mk(N3, "Cid");
        Finish(d);
        Ok(Stage(d) == "pending" && Pos(d).x == RandomSpawn.x, "OnPlayerSpawned for a record not marked crossing: ignored (no move)");
        FirstSpawn(d, true);
        Finish(d);
        var at = Pos(d);
        Finish(d);
        Ok(Stage(d) == "gatehouse" && Pos(d).x == RandomSpawn.x && Graces.Count(g => g.StartsWith(N3 + "|")) == 1, "a repeated OnPlayerSpawned does nothing (one move, one grace)");
        // Staff with realmarrival.skip: vanilla spawning.
        var s = Mk(M1, "Staff");
        A.permission.Grants.Add(M1 + "|realmarrival.skip");
        FirstSpawn(s, true);
        Ok(Stage(s) == "none" && !Owns(s), "realmarrival.skip: stage none (not handled)");
        Finish(s);
        Ok(Pos(s).x == RandomSpawn.x, "realmarrival.skip: the game's own spawn");
        Set("StaffToHearth", true);
        var s2 = Mk(M2, "Staff Two");
        A.permission.Grants.Add(M2 + "|realmarrival.skip");
        FirstSpawn(s2, true);
        Finish(s2);
        Ok(Dist(Pos(s2), 11, 128) < 12 && Stage(s2) == "none", "StaffToHearth: staff go to a mercy stone at the Hearth, unhandled", Pos(s2).ToString());
        // Closed at the Finish click: stage none, Herald's normal welcome.
        var x = Mk(N4, "Dag");
        FirstSpawn(x, true);
        Set("Open", false);
        Finish(x);
        Ok(Stage(x) == "none" && Pos(x).x == RandomSpawn.x && ArrivalStage(x) == "none", "closed at the Finish click: stage none, vanilla spawn");
        var y = Mk(N5, "Eli");
        FirstSpawn(y, true);
        Ok(Stage(y) == "none" && ArrivalStage(y) == "none", "closed: a new first spawn is not handled (none)");
    }

    static void VariantB()
    {
        // vanilla: the game's random spawn stands; one Veteran line after the loader; no realm-wide line; stays done.
        Site();
        var v = Mk(V1, "Old Hand");
        FirstSpawn(v, false);
        Offline(v);
        // A wipe: the character must be made again.
        Online(v);
        FirstSpawn(v, true);
        Ok(Stage(v) == "done" && (string)RecF(v, "Variant") == "B" && !Owns(v), "variant B (vanilla): stays done, not owned");
        Finish(v);
        Ok(Pos(v).x == RandomSpawn.x && Graces.Count == 0, "variant B (vanilla): the game's own spawn, no move");
        Advance(13);
        Ok(v.Messages.Count == 1 && v.Messages[0] == "[D6A043]Hearth[FFFFFF]: Welcome back to Ostreval, Old Hand. The realm was made new; the Hall of Kings remembers.",
            "variant B (vanilla): one gold Veteran line after the loader", v.All());
        Ok(B().Length == 0, "variant B: no realm-wide line");
        Advance(120);
        Ok(v.Messages.Count == 1, "and nothing more");

        // short: the cut into the Gatehouse, Veteran at W+0, the gate opens at once, Written on leaving the hall.
        Site(false, false, c => Tweak(c, "VeteranMode", "short"));
        var w = Mk(V2, "Second Hand");
        FirstSpawn(w, false); Offline(w); Online(w);
        FirstSpawn(w, true);
        Ok(Stage(w) == "crossing" && (string)RecF(w, "Variant") == "B", "variant B (short): the cut like a newcomer");
        Finish(w);
        Ok(Stage(w) == "gatehouse" && Dist(Pos(w), 11, 13) < 14, "variant B (short): in the Gatehouse");
        Advance(13);
        Ok(w.All().Contains("Welcome back to Ostreval, Second Hand") && (bool)RecF(w, "GateDone"), "variant B (short): Veteran line and the gate open at once");
        Ok(!w.All().Contains("Stone underfoot") && B().Length == 0, "variant B (short): no lore lines, no realm-wide line");
        Walk(w, 11, 35, 5f);
        Ok(Stage(w) == "done", "variant B (short): written on leaving the hall box");
        Walk(w, 11, 126, 5f); Advance(40);
        Ok(!w.All().Contains("Stone underfoot") && !w.All().Contains("You warm your hands") && !w.All().Contains("Iron Stag"), "variant B (short): no banner or fire lines", w.All());
        Ok(Quests.Count == 0 && Deeds.Count == 0, "variant B (short): no quest reports, no deed");

        // full: the whole arrival without the realm-wide line, the pledges or the quest reports.
        Site(false, false, c => Tweak(c, "VeteranMode", "full"));
        var f = Mk(V1, "Third Hand");
        FirstSpawn(f, false); Offline(f); Online(f);
        FirstSpawn(f, true);
        Finish(f);
        Advance(13);
        Ok(f.All().Contains("Stone underfoot"), "variant B (full): the lore lines");
        At(f, 11, 22); Advance(1);
        Ok(B().Length == 0, "variant B (full): no realm-wide line");
        Walk(f, 11, 46, 3f);
        At(f, 3, 46); Advance(10);
        Ok(!f.All().Contains("Stand fast to look to House"), "variant B (full): no pledges");
        At(f, 11, 46);
        Walk(f, 11, 126, 3f); Advance(40);
        Ok(f.All().Contains("You warm your hands") && Quests.Count == 0, "variant B (full): the fire lines, no quest reports", string.Join("\n", Quests));

        // The pause/close switch: a veteran on a closed server stays done.
        Site();
        Set("Open", false);
        var g = Mk(V2, "Fourth Hand");
        FirstSpawn(g, false); Offline(g); Online(g);
        FirstSpawn(g, true);
        Finish(g);
        Advance(13);
        Ok(Stage(g) == "done" && g.Messages.Count == 0, "closed: a veteran gets no Veteran line");
    }

    static void Seeding()
    {
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmHerald.json"), "{ \"Players\": {"
            + " \"76561190000000201\": { \"Name\": \"Sworn\", \"FirstSeen\": \"2026-09-01T10:00:00Z\", \"LastSeen\": \"2026-09-01T10:10:00Z\", \"Sworn\": true },"
            + " \"76561190000000202\": { \"Name\": \"Long\", \"FirstSeen\": \"2026-09-01T10:00:00Z\", \"LastSeen\": \"2026-09-01T12:00:00Z\" },"
            + " \"76561190000000203\": { \"Name\": \"Quitter\", \"FirstSeen\": \"2026-09-01T10:00:00Z\", \"LastSeen\": \"2026-09-01T10:05:00Z\" } } }");
        string heraldBefore = File.ReadAllText(Path.Combine(Dir, "RealmHerald.json"));
        NewArrival();
        var players = (IDictionary)F(Data(), "Players");
        Ok(players.Contains("76561190000000201") && players.Contains("76561190000000202"), "seeded: a step done, and an hour of play");
        Ok(!players.Contains("76561190000000203"), "not seeded: someone who only quit during creation");
        Ok((string)F(players["76561190000000201"], "Stage") == "done" && (bool)F(players["76561190000000201"], "Seeded"), "a seeded veteran is done");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmHerald.json")) == heraldBefore, "RealmHerald.json is read-only");
        Ok((bool)F(Data(), "Seeded"), "seeding is recorded");
        File.WriteAllText(Path.Combine(Dir, "RealmHerald.json"), "{ \"Players\": { \"76561190000000204\": { \"Name\": \"Later\", \"Sworn\": true } } }");
        Reload();
        Ok(!((IDictionary)F(Data(), "Players")).Contains("76561190000000204"), "seeding runs once, on the first load");
        BuildSite();
        var v = Mk(V1, "Sworn");
        FirstSpawn(v, true);
        Ok(Stage(v) == "done" && (string)RecF(v, "Variant") == "B", "a seeded id joining a fresh world is variant B");
        Reset();
        NewArrival(c => Tweak(c, "SeedFromHerald", false));
        Ok(((IDictionary)F(Data(), "Players")).Count == 0, "SeedFromHerald false: nothing seeded");
    }

    static void TheCut()
    {
        Site();
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        Clear();
        var e = Finish(n);
        var pos = Pos(n);
        bool onStone = StoneXZ.Any(s => Dist(pos, s[0], s[1]) <= 0.01f);
        Ok(onStone && Math.Abs(pos.y - 10.5f) < 0.01f, "teleport mode: in the same tick, onto a stone + 0.5 m", pos.ToString());
        Ok(Graces.Count == 1 && Graces[0] == N1 + "|30" && Cancels.SequenceEqual(new[] { N1.ToString() }), "SentinelGrace(id, 30) and RealmTravel.CancelJourney first");
        Ok(n.Entity.Teleports.Count == 1, "one teleport");
        Ok(e.PostSpawnPosition.x == RandomSpawn.x, "PostSpawnPosition is not touched (the game's teleport already ran)");
        Ok(Stage(n) == "gatehouse" && (DateTime)RecF(n, "T0") == Clock, "stage gatehouse from T+0");
        Ok(ArrivalStage(n) == "running" && Owns(n), "ArrivalStage running");
        Ok(n.Messages.Count == 0, "nothing is said during the loader");
        Ok(Counter("routed") == 1, "counted as routed");
        // TeleportDelaySeconds 1: the move after a second.
        Site(false, false, c => Tweak(c, "TeleportDelaySeconds", 1f));
        var d = Mk(N2, "Bea");
        FirstSpawn(d, true);
        Finish(d);
        Ok(Pos(d).x == RandomSpawn.x, "TeleportDelaySeconds 1: not moved at once");
        Advance(1.1);
        Ok(StoneXZ.Any(s => Dist(Pos(d), s[0], s[1]) <= 0.01f), "and moved a second later");
        // A teleport that throws: logged, the checks fall back to road mode.
        Site();
        var t = Mk(N3, "Cid");
        t.Entity.TeleportThrows = true;
        FirstSpawn(t, true);
        Finish(t);
        Ok(A.Logged.Any(l => l.Contains("Teleport failed")), "a failing teleport is logged");
        Advance(15);
        Ok((bool)RecF(t, "RoadMode"), "and the arrival checks switch to road mode");
    }

    static void ArrivalChecks()
    {
        // The server does not follow the first teleport: T+3 re-teleports once (unconfirmed), T+10 road mode.
        Site();
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        n.Entity.TeleportIgnored = true;
        Finish(n);
        Advance(3.05);
        Ok(n.Entity.Teleports.Count == 2 && (bool)RecF(n, "Unconfirmed") && Counter("unconfirmed") == 1 && Counter("rerouted") == 1,
            "T+3 outside the hall box: one more teleport, counted unconfirmed");
        Advance(7);
        Ok((bool)RecF(n, "RoadMode") && Stage(n) == "released" && Counter("road_mode") == 1, "T+10 still outside: road mode");
        Ok(n.Entity.Teleports.Count == 2, "road mode never moves the player");
        Advance(3);
        Ok(n.All().Contains("The current carried your raft off course") && n.All().Contains("[F4C96D]/road the-hearth[FFFFFF]"), "the RoadMode line points to /road the-hearth", n.All());
        // Unstuck after T+3 (MovementStatisticCollector to a random spawn): T+10 re-teleports, T+13 confirms.
        Site();
        var u = Newcomer(N2, "Bea");
        Advance(3.05);
        Ok(u.Entity.Teleports.Count == 1 && !(bool)RecF(u, "Unconfirmed"), "T+3 inside the box: nothing to do");
        At(u, 600, 600);
        Advance(7);
        Ok(u.Entity.Teleports.Count == 2 && (bool)RecF(u, "Unconfirmed"), "moved out by the game before T+10: one re-teleport");
        Advance(3.1);
        Ok(!(bool)RecF(u, "RoadMode") && Stage(u) == "gatehouse", "back in the box at T+13: the arrival goes on");
        // A walk out through the open arch is not a failed check.
        Site();
        var w = Newcomer(N3, "Cid");
        Advance(2);
        Walk(w, 11, 22, 6f);
        Advance(3);
        Ok((bool)RecF(w, "GateDone") && !(bool)RecF(w, "Unconfirmed"), "walking to the gold line before T+10 is no failed check");
    }

    static void Narration()
    {
        Site();
        var n = Newcomer(N1, "Ada");
        Advance(11.5);
        Ok(n.Messages.Count == 0, "no lines in the first 12 s without a move (the loader may still show)");
        Advance(1);
        Ok(n.Messages.Count == 1 && n.Messages[0].StartsWith("[D6A043]Hearth[FFFFFF]: Stone underfoot, old smoke on the air."), "W+0 at T+12: the gold Wake line", n.All());
        Advance(5);
        Ok(n.Messages.Count == 2 && n.Messages[1] == "[D6A043]Hearth[FFFFFF]: Ada, the Chronicle has no page for you: no house, no oath, no debt. Under the Charter that is enough. The crown belongs to the seat, not the blood.",
            "W+5: the gold Naming line with the player's name", n.All());
        Advance(5);
        Ok(n.Messages.Count == 3 && n.Messages[2] == "[E8913A]Hearth[FFFFFF]: The gate stands open. Walk through it, into Ostreval.", "W+10: the amber call (open mode: CallOpen)", n.All());
        Advance(1);
        Ok(n.Popups.Count == 1, "W+11: the Gatehouse card");
        var pop = n.Popups[0];
        Ok(pop.Kind == "basic" && pop.Title == "The Gatehouse of the Unwritten" && pop.Confirm == "Step through" && pop.Broadcast && !pop.Interrupt,
            "the card: title, 'Step through' in open mode, broadcast true, interupt false");
        Ok(pop.Message.Contains("The ferryman has brought you over the Grey Water.") && pop.Message.Contains("\n") && !pop.Message.Contains("["), "the card's body is plain text with line breaks");
        // Narration starts on the first move, never before T+4.
        Site();
        var m = Newcomer(N2, "Bea");
        Advance(1);
        At(m, Pos(m).x + 2, Pos(m).z);
        Advance(2);
        Ok(m.Messages.Count == 0, "a move at T+1: still waits for T+4");
        Advance(1.5);
        Ok(m.Messages.Count == 1 && m.All().Contains("Stone underfoot"), "narration at T+4 after an early move");
        Site();
        var k = Newcomer(N3, "Cid");
        Advance(6);
        At(k, Pos(k).x + 1.5f, Pos(k).z);
        Advance(1);
        Ok(k.Messages.Count == 1, "a move at T+6 starts narration at once");
        // Portcullis mode: the call is CallGate and the card's button 'Open the Gate'.
        Site(true);
        var g = ToNarration(N4, "Dag");
        Advance(11);
        Ok(g.All().Contains("Lay your hand on the gate: walk to the gold line before it.") && g.Popups.Count == 1 && g.Popups[0].Confirm == "Open the Gate",
            "portcullis mode: CallGate and 'Open the Gate'", g.All());
        // Line gaps.
        Site();
        var q = ToNarration(N5, "Eli");
        At(q, 11, 22);
        Advance(30);
        var times = new List<int>();
        Ok(q.Messages.Count >= 3, "lines keep coming after the gate");
    }

    static void GateTriggers()
    {
        // The gold line.
        Site();
        var n = ToNarration(N1, "Ada");
        Advance(3);
        At(n, 11, 22);
        Advance(1);
        Ok((bool)RecF(n, "GateDone") && Stage(n) == "released", "the gold line opens the gate: stage released");
        Ok(B() == "[D6A043]Herald[FFFFFF]: Ada walks out of the Gatehouse of the Unwritten, onto the road to the Hearth.", "the realm-wide Herald line", B());
        Ok(Quests.Contains(N1 + "|custom|arrival_gate|1"), "ReportQuestEvent arrival_gate");
        Advance(20);
        Ok(!n.All().Contains("The gate stands open."), "the queued gate call is dropped once the gate is open (no backlog)", n.All());
        Ok(n.Popups.Count == 0, "the card is skipped once the gold line is crossed");
        Ok(Counter("released") == 1, "counted as released");
        // The card's button.
        Site();
        var p = ToNarration(N2, "Bea");
        Advance(11);
        Ok(p.Popups.Count == 1, "the card opened");
        Answer(p.Popups[0], Options.OK);
        Ok((bool)RecF(p, "GateDone"), "the card's answer opens the gate");
        Answer(p.Popups[0], Options.OK);
        Ok(Counter("popups_answered") == 1, "an answer counts once");
        // After AutoOpenSeconds.
        Site();
        var q = ToNarration(N3, "Cid");
        Advance(59);
        Ok(!(bool)RecF(q, "GateDone"), "not yet at W+59");
        Advance(1.5);
        Ok((bool)RecF(q, "GateDone") && q.All().Contains("[E8913A]Hearth[FFFFFF]: The gate opens on its own. The Hearth is waiting."), "W+60: GateSelf and the gate opens", q.All());
        // Leaving the hall another way (the Pilgrim's Drop) counts as the gate moment.
        Site();
        var r = ToNarration(N4, "Dag");
        At(r, -4, 5);
        Advance(1);
        Ok((bool)RecF(r, "GateDone") && Stage(r) == "released", "leaving the hall box by the Pilgrim's Drop: released");
    }

    static void Reveal()
    {
        Site();
        var n = ThroughGate(N1, "Ada");
        Advance(0.8);
        Ok(!n.All().Contains("Six banners line the road"), "the reveal waits for gate + 3 s");
        Advance(2.5);
        Ok(n.All().Contains("[D6A043]Hearth[FFFFFF]: Six banners line the road to the fire. On the hill beyond, the Old Throne stands empty, and anyone may sit it."), "gate + 3: RevealEmpty (no monarch)", n.All());
        Advance(5);
        Ok(n.All().Contains("Walk the banners. Each house will tell you what it is.") && Stage(n) == "banners", "gate + 8: Walk; stage banners");
        // With a monarch: name and tinted house.
        Site();
        King = "Hale"; KingHouse = "Varrow";
        var m = ThroughGate(N2, "Bea");
        Advance(4);
        Ok(m.All().Contains("On the hill beyond stands the Old Throne, held by Hale of [C58FC0]Varrow[FFFFFF]."), "RevealKing with CrownAndConsequences.GetKingName / GetKingHouse", m.All());
        // At night (the game clock): RevealNight.
        Site();
        GameClock.Instance.TimeOfDay = 23f;
        var k = ThroughGate(N3, "Cid");
        Advance(4);
        Ok(k.All().Contains("Fires mark the road through the dark."), "at night: RevealNight");
        GameClock.Instance = null;
        Site();
        GameClock.Instance = null;
        var j = ThroughGate(N4, "Dag");
        Advance(4);
        Ok(j.All().Contains("the Old Throne stands empty"), "no game clock: the day reveal");
    }

    static void Banners()
    {
        Site();
        Houses.AddRange(new[] { "Varrow", "House Ashgrove", "Corvane", "Dunmere", "Merrin" });
        HouseOf[M1] = "Varrow"; HouseOf[M2] = "Varrow"; HouseOf[V1] = "House Ashgrove";
        Leader["Varrow"] = M1;
        Liege["Corvane"] = "Varrow";
        var lead = Mk(M1, "Leader", 500, 500);
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Clear();
        Walk(n, 11, 46, 1.5f);
        Advance(18);
        string all = n.All();
        int iv = Index(n, "the Iron Stag"), ia = Index(n, "the White Oak");
        Ok(iv >= 0 && ia >= 0, "the facing pair (Varrow, Ashgrove) both speak", all);
        Ok(n.Messages.Contains("[D6A043]Hearth[FFFFFF]: [C58FC0]Varrow[FFFFFF], the Iron Stag. \"We Stand Our Ground.\" The crown-holders: they take the throne and hold it against the realm."),
            "line 1: the house's words in its tint", all);
        Ok(n.Messages.Contains("  [A3A6AD]2 sworn, 1 awake in the realm, leader at hand. Sworn to no one.[FFFFFF]"), "line 2: live data (sworn, awake, leader at hand, liege)", all);
        Ok(n.Messages.Contains("  [A3A6AD]1 sworn, 0 awake in the realm, leader away. Sworn to no one.[FFFFFF]"), "a house whose RealmHouses name is 'House Ashgrove' is matched too", all);
        Ok(all.Contains("[E08A5C]House Ashgrove[FFFFFF], the White Oak"), "the RealmHouses name is shown in the house tint", all);
        int ih = Index(n, "Stand on a house's stone");
        int firstHouse = Math.Min(iv, ia), secondHouse = Math.Max(iv, ia);
        Ok(ih == firstHouse + 2, "PledgeHint follows the first banner (after its live line)", all);
        Ok(Lines(n).Count(l => l.Contains("the Iron Stag")) == 1, "each banner speaks once");
        Ok(Quests.Count(q => q.Contains("arrival_banners")) == 0, "no banners report yet (2 heard)");
        Walk(n, 11, 74, 1.5f);
        Advance(18);
        all = n.All();
        Ok(all.Contains("[8FB0BF]Corvane[FFFFFF], the Black Raven") && all.Contains("Sworn to Varrow."), "the second pair: Corvane sworn to Varrow", all);
        Ok(all.Contains("[E8913A]Hearth[FFFFFF]: A banner is never taken, only given. Send word to a house: [F4C96D]/raven[FFFFFF] <house> <letter>.")
            && all.Contains("  [A3A6AD]Or raise your own:[FFFFFF] [F4C96D]/house found[FFFFFF]"), "after the third banner: the honest paths (raven, own banner)", all);
        Ok(Quests.Count(q => q == N1 + "|custom|arrival_banners|1") == 1, "arrival_banners after 3 banners, once");
        Walk(n, 11, 103, 1.5f);
        Advance(18);
        all = n.All();
        Ok(all.Contains("  [A3A6AD]No one has raised this banner yet. Great names go to companies by the claim sign-up.[FFFFFF]"), "an unclaimed great name: the honest claim line (Halloran)", all);
        Ok(Lines(n).Count(l => l.Contains("A banner is never taken")) == 1, "the paths are explained once");
        // Line gaps are at least 4 s: count lines per 4-second window.
        // Pair order is shuffled per player: over many walkers both orders appear.
        int varrowFirst = 0, ashFirst = 0;
        for (int i = 0; i < 12; i++)
        {
            var w = ThroughGate(N2 + (ulong)i * 1000, "Walker" + i);
            Advance(9);
            w.Messages.Clear();
            Walk(w, 11, 46, 1.5f);
            Advance(18);
            int a = Index(w, "the Iron Stag"), b = Index(w, "the White Oak");
            if (a >= 0 && b >= 0) { if (a < b) varrowFirst++; else ashFirst++; }
            Inv(A, "OnPlayerDisconnected", w); Server.ClientPlayers.Remove(w);
        }
        Ok(varrowFirst > 0 && ashFirst > 0, "the order within a pair is shuffled per player", varrowFirst + " / " + ashFirst);
    }

    static void FastWalker()
    {
        Site();
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Clear();
        // A sprint down the avenue: banner lines for monuments left 30 m behind are dropped, summed up at the fire.
        Walk(n, 11, 126, 12f);
        Advance(60);
        string all = n.All();
        int heard = new[] { "Iron Stag", "White Oak", "Black Raven", "Drowned Bell", "Ember Hound", "Silver Eel" }.Count(h => all.Contains(h));
        Ok(heard < 6, "a fast walker hears fewer than six banners (lines past 30 m are dropped)", heard.ToString());
        Ok(all.Contains("  [A3A6AD]You passed ") && all.Contains("without stopping.[FFFFFF] [F4C96D]/arrival tour[FFFFFF]"), "one muted Skipped line at the fire names them", all);
        Ok(all.Contains("You warm your hands"), "and the fire beats still come");
        // Lines are at least LineGapSeconds apart.
        Site();
        var g = ThroughGate(N2, "Bea");
        var stamps = new List<DateTime>();
        int seen = g.Messages.Count;
        for (int i = 0; i < 200; i++)
        {
            if (i < 60) At(g, 11, 22 + i * 1.8f);
            Advance(0.5);
            if (g.Messages.Count > seen) { for (int k = seen; k < g.Messages.Count; k++) if (!g.Messages[k].StartsWith("  ")) stamps.Add(Clock); seen = g.Messages.Count; }
        }
        bool gaps = true;
        for (int i = 1; i < stamps.Count; i++) if ((stamps[i] - stamps[i - 1]).TotalSeconds < 3.9) gaps = false;
        Ok(gaps && stamps.Count >= 5, "at least 4 s between narration lines", string.Join(" ", stamps.Select(t => t.ToString("mm:ss.f"))) + "\n" + g.All());
    }
}
