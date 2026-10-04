// Behaviour tests for plugins/RealmArrival.cs, part 3: the portcullis and the flare, the plugin's own cells, safety
// (sanctuary, eviction, shield, zone guard), popups, staff runs, the Herald cap, skip and tour, hour one, the page,
// admin, the site check and self-check, road mode, wave mode, reloads, data safety and chat style.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Blocks;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
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
    static void Portcullis()
    {
        Reset();
        NewArrival();
        var adm = Mk(1, "Steward", 9.6f, 26.4f);
        Admin(adm);
        adm.Entity.Forward = new UnityEngine.Vector3(0, 0, 0);    // facing not known
        Ok(Cmd(adm, "admin", "gate", "set", "5", "6").Contains("say which way the gate faces"), "gate set: without a facing, the hall and the hearth are needed (the gate faces the Hearth)");
        adm.Entity.Forward = new UnityEngine.Vector3(-0.2f, 0, -0.9f);
        Ok(Cmd(adm, "admin", "gate", "set", "5", "6").Contains("along -x"), "gate set: facing out from where the admin looks (Entity.Forward -z: the gate runs to -x)");
        Ok(Cmd(adm, "admin", "gate", "set", "5", "6", "+x").Contains("along -z"), "gate set: an explicit facing (+x: the gate runs to -z)");
        adm.Entity.Forward = new UnityEngine.Vector3(0, 0, 1);
        Ok(Cmd(adm, "admin", "gate", "set", "5", "6").Contains("along +x"), "gate set: facing +z runs the gate to +x");
        Grid.Put(12, 13, 22, 2);                                 // one cell of the opening is not empty
        BuildSite(true, true);
        Ok(((IList)F(SiteData(), "GateCells")).Count == 29, "gate build: 5 x 6 cells, only into empty cells (29 here)", ((IList)F(SiteData(), "GateCells")).Count.ToString());
        Ok(Grid.Mat(12, 13, 22) == 2, "a cell that was not empty is left alone");
        Grid.Cells.Remove((12, 13, 22));
        Reset();
        NewArrival();
        BuildSite(true, true);
        var places = Grid.Calls.Where(c => c.Kind == "place" && c.Mat == 9).ToList();
        Ok(GateSolid() == 30 && places.Count == 30, "the portcullis closes: 30 reinforced cells (material 9)", GateSolid().ToString());
        Ok(places.Select(c => c.Pos.y).SequenceEqual(places.Select(c => c.Pos.y).OrderBy(y => y)), "closing rises bottom row first");
        Ok(places.All(c => !c.Collect && !c.Owned), "collectPreviousCube false (no salvage), not owned by a placer");
        Ok(Grid.Rgb(10, 10, 22) == 0x131417, "painted Iron 900 after it lands", Grid.Rgb(10, 10, 22).ToString("x6"));
        Clear();
        var n = ToNarration(N1, "Ada");
        Ok(GateSolid() == 30, "a newcomer waits behind the closed gate");
        Advance(5);
        int before = Grid.Calls.Count;
        At(n, 11, 22);
        Advance(1.05);
        var opens = Grid.Calls.Skip(before).Where(c => c.Kind == "place" && c.Mat == 0).ToList();
        Ok(opens.Count >= 5 && opens.Take(5).All(c => c.Pos.y == 13), "the gold line: the top row goes first", string.Join(" ", opens.Select(c => c.Pos.y)));
        Advance(2.5);
        opens = Grid.Calls.Skip(before).Where(c => c.Kind == "place" && c.Mat == 0).ToList();
        Ok(GateSolid() == 0 && opens.Count == 30, "the gate sinks into the ground row by row (30 cells to air)");
        var rowTimes = opens.GroupBy(c => c.Pos.y).OrderByDescending(g => g.Key).Select(g => g.First().At).ToList();
        bool spaced = true;
        for (int i = 1; i < rowTimes.Count; i++) { double d = (rowTimes[i] - rowTimes[i - 1]).TotalSeconds; if (d < 0.35 || d > 0.55) spaced = false; }
        Ok(spaced && (rowTimes.Last() - rowTimes.First()).TotalSeconds <= 2.4, "one row every 0.4 s: open in about 2 s", string.Join(" ", rowTimes.Select(t => t.ToString("ss.f"))));
        Ok(opens.All(c => !c.Collect), "opening drops no salvage");
        // Held open while the newcomer it opened for is still inside.
        Advance(30);
        Ok(GateSolid() == 0, "held open while the released newcomer is still in the hall");
        Walk(n, 11, 40, 4f);
        Advance(3);
        Ok(GateSolid() == 30, "closes again once they are out (after GateMinOpenSeconds)", GateSolid().ToString());
        // Clearance: never closes while anyone is within 3 m of a gate cell.
        Site(true);
        var n2 = ThroughGate(N2, "Bea");
        Walk(n2, 11, 29, 4f);                                     // in the gateway, outside the hall
        Advance(40);
        Ok(GateSolid() == 0, "never closes while someone stands within 3 m of the gate");
        Walk(n2, 11, 45, 4f);
        Advance(5);
        Ok(GateSolid() == 30, "then closes");
        // GateCycleMinSeconds between openings.
        Site(true);
        var a = ThroughGate(N3, "Cid");
        DateTime firstOpen = Clock;
        Walk(a, 11, 45, 6f);
        var b = ToNarration(N4, "Dag");
        while (GateSolid() < 30 && (Clock - firstOpen).TotalSeconds < 60) Advance(0.5);
        At(b, 11, 22);
        var asked = Clock;
        while (GateSolid() == 30 && (Clock - asked).TotalSeconds < 30) Advance(0.2);
        Ok((bool)RecF(b, "GateDone"), "the second newcomer's gate moment");
        Ok((Clock - firstOpen).TotalSeconds >= 19.5, "a new opening waits for GateCycleMinSeconds after the last", (Clock - firstOpen).TotalSeconds.ToString());
        // Two or more newcomers waiting: held open.
        Site(true);
        var adm2 = Mk(2, "Steward2", 400, 400);
        Admin(adm2);
        Cmd(adm2, "admin", "gate", "open");
        Advance(4);
        var w1 = ToNarration(N1, "Ada");
        var w2 = ToNarration(N2, "Bea");
        Advance(30);
        Ok(GateSolid() == 0, "two newcomers waiting inside: the gate is held open");
        // Unload opens the gate; a crash and a load open it too.
        Site(true);
        Ok(GateSolid() == 30, "closed");
        Inv(A, "Unload");
        Ok(GateSolid() == 0, "Unload opens the gate (nobody is ever left trapped)");
        Site(true);
        Crash();
        Ok(GateSolid() == 0, "after a crash, the load opens the gate");
        // gate remove: all air and forgotten.
        var adm3 = Mk(3, "Steward3", 400, 400);
        Admin(adm3);
        Cmd(adm3, "admin", "gate", "remove");
        Ok(GateSolid() == 0 && ((IList)F(SiteData(), "GateCells")).Count == 0, "gate remove: the cells are air and forgotten");
        // Open mode never builds or closes anything.
        Site();
        Ok(GateSolid() == 0 && !Grid.Calls.Any(c => c.Kind == "place" && c.Mat == 9), "GateMode open: no portcullis cells");
    }

    static void Flare()
    {
        Site(false, true);
        var cells = (IList)F(SiteData(), "BeaconCells");
        Ok(cells.Count == 24, "beacon build 5: the 24-cell ember band round the hearth", cells.Count.ToString());
        var c0 = cells[0];
        int x = (int)F(c0, "X"), y = (int)F(c0, "Y"), z = (int)F(c0, "Z");
        Ok(Grid.Mat(x, y, z) == 3 && Grid.Rgb(x, y, z) == 0x9c6a1e, "clay (material 3), resting Ember deep", Grid.Rgb(x, y, z).ToString("x6"));
        var n = ThroughGate(N1, "Ada");
        Ok(cells.Cast<object>().All(c => Grid.Rgb((int)F(c, "X"), (int)F(c, "Y"), (int)F(c, "Z")) == 0xf4c96d), "the gate moment: the band flares Ember hot (open mode too)", string.Join("\n", A.Logged) + " rgb " + Grid.Rgb(x, y, z).ToString("x6"));
        Ok(Counter("flares") == 1, "counted");
        Advance(21);
        Ok(Grid.Rgb(x, y, z) == 0x9c6a1e, "and fades back after FlareSeconds");
        ThroughGate(N2, "Bea");
        Ok(Counter("flares") == 1 && Grid.Rgb(x, y, z) == 0x9c6a1e, "at most one flare a minute");
        Advance(60);
        ThroughGate(N3, "Cid");
        Ok(Counter("flares") == 2, "a minute later it flares again");
        var adm = Mk(2, "Steward2", 400, 400);
        Admin(adm);
        Advance(70);
        Ok(Cmd(adm, "admin", "beacon", "test").Contains("flare for 20 s") && Grid.Rgb(x, y, z) == 0xf4c96d, "beacon test");
        Cmd(adm, "admin", "beacon", "clear");
        Ok(Grid.Mat(x, y, z) == 0 && ((IList)F(SiteData(), "BeaconCells")).Count == 0, "beacon clear: the cells are air");
        Ok(Cmd(adm, "admin", "beacon", "build", "40").Contains("Usage"), "the radius is 2-20");
    }

    static void CellGuard()
    {
        Site(true, true);
        var v = Mk(V1, "Vandal", 11, 30);
        var dmg = new CubeDamageEvent { GridID = 0, Position = new Vector3Int(10, 10, 22), Damage = new Damage { Amount = 50f, DamageSource = v.Entity }, Sender = v };
        Inv(A, "OnCubeTakeDamage", dmg);
        Ok(dmg.Cancelled && dmg.Damage.Amount == 0f, "a gate cell takes no damage");
        var other = new CubeDamageEvent { GridID = 0, Position = new Vector3Int(1, 7, 1), Damage = new Damage { Amount = 50f, DamageSource = v.Entity }, Sender = v };
        Inv(A, "OnCubeTakeDamage", other);
        Ok(!other.Cancelled && other.Damage.Amount == 50f, "other blocks are not this plugin's business");
        var cells = (IList)F(SiteData(), "BeaconCells");
        var bc = cells[3];
        var place = new CubePlaceEvent { GridID = 0, Position = new Vector3Int((int)F(bc, "X"), (int)F(bc, "Y"), (int)F(bc, "Z")), Sender = v, Material = 2 };
        Inv(A, "OnCubePlacement", place);
        Ok(place.Cancelled, "no player builds into an ember cell");
        Ok(GateSolid() == 30 && cells.Count == 24, "the plugin's own placements pass its own guard", GateSolid() + " " + cells.Count + " " + string.Join("\n", A.Logged));
    }

    static void Sanctuary()
    {
        Site();
        var n = ToNarration(N1, "Ada");
        var m = ToNarration(N2, "Bea");
        var v = Mk(V1, "Veteran", 11, 15);
        FirstSpawn(v, false);
        Ok(Hit(v, n) == 0f, "inside the hall box, a player in the Gatehouse takes no damage from others");
        Ok(Fall(n) == 0f, "nor from a fall");
        Ok(Hit(m, n) == 0f, "newcomers cannot hurt each other there");
        Ok(Hit(n, v) == 10f, "a veteran in the hall gets no sanctuary (no refuge from a fight)");
        var v2 = Mk(V2, "Other", 12, 15);
        FirstSpawn(v2, false);
        Ok(Hit(v2, v) == 10f && Hit(v, v2) == 10f, "players not in arrival fight as normal inside the hall");
        // A sleeper of a player in the Gatehouse.
        var s = ToNarration(N3, "Cid");
        var body = new Entity { IsPlayer = false, Position = Pos(s) };
        Offline(s);
        PlayerSleeperObject.AllSleeperObjects[N3] = body;
        Ok(HitEntity(v, body) == 0f, "the sleeper of a player in the Gatehouse is inside the sanctuary");
        var vb = new Entity { IsPlayer = false, Position = new UnityEngine.Vector3(11, 10, 15) };
        PlayerSleeperObject.AllSleeperObjects[V2] = vb;
        Ok(HitEntity(v, vb) == 10f, "anyone else's sleeper is not");
        // Released players: the drop pad is a sanctuary for arrivals only.
        var r = ThroughGate(N4, "Dag");
        At(r, -4, 5);
        Ok(Fall(r) == 0f, "the drop pad: no fall damage for a player in arrival (the Pilgrim's Drop)");
        At(v, -4, 5);
        Ok(Fall(v) == 10f, "the drop pad is no sanctuary for anyone else");
        Ok(Hit(null, n, -5f) == -5f, "healing passes");
    }

    static void Eviction()
    {
        Site();
        var v = Mk(V1, "Loiterer", 11, 40);
        FirstSpawn(v, false);
        At(v, 11, 15);
        Advance(2.5);
        Ok(Dist(Pos(v), 11, 15) < 0.1, "a few seconds in the hall: nothing yet");
        Advance(2);
        Ok(Dist(Pos(v), 11, 31) < 0.1 && v.All().Contains("[E8913A]Hearth[FFFFFF]: The Gatehouse is for the Unwritten.") && Counter("evictions") == 1,
            "more than 3 s in the hall: moved to the eject point E with Evicted", Pos(v).ToString());
        Ok(Graces.Contains(V1 + "|20"), "SentinelGrace before the move (4 m: not travel)");
        At(v, 11, 15); Advance(5);
        At(v, 11, 15); Advance(5);
        Ok(Alerts.Count == 1 && Alerts[0].StartsWith("arrival_camp|" + V1), "three evictions in ten minutes: RaiseWardenAlert(arrival_camp)", string.Join("\n", Alerts));
        var s = Mk(M1, "Tester", 11, 15);
        A.permission.Grants.Add(M1 + "|realmarrival.skip");
        Advance(6);
        Ok(Dist(Pos(s), 11, 15) < 0.1, "realmarrival.skip: never evicted");
        var n = ThroughGate(N1, "Ada");
        Advance(10);
        Ok(Dist(Pos(n), 11, 22) < 0.1, "a newcomer in arrival is never evicted");
        Set("Evict", false);
        At(v, 11, 15); Advance(6);
        Ok(Dist(Pos(v), 11, 15) < 0.1, "evict off: nobody is moved");
    }

    static void Shield()
    {
        Site();
        Protected.Clear();
        var v = Mk(V1, "Camper", 11, 35);
        FirstSpawn(v, false);
        var n = ThroughGate(N1, "Ada");
        At(n, 11, 30);
        Ok(Hit(v, n) == 0f, "the release shield: no player damage right after the gate");
        Advance(31);
        Ok(Hit(v, n) == 0f, "after 30 s the zone guard still covers a variant A newcomer on the avenue (no Warden protection)");
        Ok(Hit(n, v) == 10f, "the newcomer strikes first");
        Ok(Hit(v, n) == 10f, "and loses both the shield and the guard");
        Site();
        var w = ThroughGate(N2, "Bea");
        var c = Mk(V2, "Camper2", 11, 35);
        FirstSpawn(c, false);
        Ok(Hit(w, c) == 10f && Hit(c, w) == 10f, "striking during the shield ends it at once");
    }

    static void ZoneGuard()
    {
        Site();
        var v = Mk(V1, "Camper", 11, 60);
        FirstSpawn(v, false);
        var n = ThroughGate(N1, "Ada");
        Advance(31);
        At(n, 11, 60);
        Ok(Hit(v, n) == 0f && Alerts.Count == 1 && Alerts[0].StartsWith("arrival_camp|" + V1), "on the avenue within 15 minutes: the blow is turned aside, a Warden alert", string.Join("\n", Alerts));
        Ok(Hit(v, n) == 0f && Alerts.Count == 1, "at most one alert per attacker per 10 minutes");
        DuelPairs.Add(V1 + "|" + N1);
        Ok(Hit(v, n) == 10f, "a duel blow (RealmArena.IsDuelBlow) is never cancelled");
        DuelPairs.Clear();
        At(n, 200, 60);
        Ok(Hit(v, n) == 10f, "off the route: no guard");
        At(n, 11, 125);
        Ok(Hit(v, n) == 0f, "at the Hearth: guarded");
        Protected.Add(N1);
        Ok(Hit(v, n) == 10f, "while RealmWarden reports protection the guard stays out of the way (Warden does the job)");
        Protected.Remove(N1);
        Clock = Clock.AddMinutes(16);
        Ok(Hit(v, n) == 10f, "after ArrivalZoneGuardMinutes: no guard");
        // Veterans never get the guard.
        Site(false, false, c => Tweak(c, "VeteranMode", "full"));
        var b = Mk(V2, "Returning");
        FirstSpawn(b, false); Offline(b); Online(b); FirstSpawn(b, true); Finish(b);
        Advance(13); At(b, 11, 22); Advance(1); Advance(31);
        At(b, 11, 60);
        var c2 = Mk(M1, "Camper2", 11, 61);
        FirstSpawn(c2, false);
        Ok(Hit(c2, b) == 10f, "variant B: no zone guard (wipe-day fights between veterans are unaffected)");
    }

    static void Popups()
    {
        // UsePopups false: no window, the chat lines stand.
        Site(false, false, c => Tweak(c, "UsePopups", false));
        var n = ToNarration(N1, "Ada");
        Advance(12);
        Ok(n.Popups.Count == 0 && n.All().Contains("The gate stands open."), "UsePopups false: no card, the chat call stands");
        // A window that throws: logged, chat stands.
        Site();
        var t = Newcomer(N2, "Bea");
        t.PopupsThrow = true;
        Advance(24);
        Ok(A.Logged.Any(l => l.Contains("ShowPopup failed")) && t.All().Contains("The gate stands open."), "a failing window is logged; the chat line stands");
        // Deadline.
        Site(false, false, c => { Tweak(c, "AutoOpenSeconds", 600); Tweak(c, "PopupAnswerSeconds", 30); });
        var d = ToNarration(N3, "Cid");
        Advance(12);
        var card = d.Popups.Single();
        Advance(31);
        Answer(card, Options.OK);
        Ok(!(bool)RecF(d, "GateDone"), "an answer after its deadline counts for nothing");
        // Wrong kind: the card's answer while a pledge question is open.
        Site();
        var k = ToNarration(N4, "Dag");
        Advance(12);
        var card2 = k.Popups.Single();
        At(k, 11, 22); Advance(6);
        Walk(k, 11, 46, 3f); Advance(14);
        OnStone(k, "varrow", 4.5);
        int before = (int)RecF(k, "Pledges");
        Answer(card2, Options.OK);
        Ok((int)RecF(k, "Pledges") == before && k.Popups.Count == 2, "an old card's answer cannot answer the open pledge (token and kind); at most two windows", k.Popups.Count.ToString());
        // After Unload.
        Site(false, false, c => Tweak(c, "AutoOpenSeconds", 600));
        var u = ToNarration(N5, "Eli");
        Advance(12);
        var card3 = u.Popups.Single();
        var old = A;
        Inv(A, "Unload");
        Answer(card3, Options.OK);
        Ok(!(bool)F(((IDictionary)F(F(old, "data"), "Players"))[N5.ToString()], "GateDone"), "after Unload, an answer opens nothing");
    }

    static void Play()
    {
        Site();
        Houses.Add("Varrow");
        HouseOf[M1] = "Varrow";
        var m = Mk(M1, "Member", 500, 500);
        var adm = Mk(1, "Steward", 400, 400);
        Admin(adm);
        var v = Mk(V1, "Tester", 300, 300);
        FirstSpawn(v, false);
        InCombat.Add(V1);
        Ok(Cmd(adm, "admin", "play", "Tester").Contains("is in a fight or travelling"), "play: refused for a player in a fight");
        InCombat.Clear();
        Travelling.Add(V1.ToString());
        Ok(Cmd(adm, "admin", "play", "Tester").Contains("is in a fight or travelling"), "play: refused while travelling");
        Travelling.Clear();
        Ok(Cmd(adm, "admin", "play", "Nobody").Contains("No such person"), "play: an unknown name");
        string r = Cmd(adm, "admin", "play", "Tester");
        Ok(r.Contains("Tester is in the Gatehouse") && Stage(v) == "gatehouse" && (bool)RecF(v, "Play"), "play: the full arrival on an online player", r);
        Ok(StoneXZ.Any(s => Dist(Pos(v), s[0], s[1]) < 0.01f), "play: moved onto a stone");
        Advance(13);
        At(v, 11, 22); Advance(6);
        Ok(B().Length == 0, "play: never the realm-wide line");
        Walk(v, 11, 46, 3f); Advance(14);
        OnStone(v, "varrow", 8);
        Ok(!m.All().Contains("looks to your banner"), "play: no house is told of a staff run's pledge");
        At(v, 11, 46);
        WalkToFire(v);
        Advance(40);
        Command(v, "quest");
        Ok(Stage(v) == "done" && v.All().Contains("You are written."), "play: to the end");
        Ok(Quests.Count == 0 && Deeds.Count == 0, "play: never quest reports or the renown deed", string.Join("\n", Quests.Concat(Deeds)));
        Ok(B().Length == 0, "play: no Herald line at all");
    }

    static void HeraldCap()
    {
        Site();
        for (int i = 0; i < 7; i++) { ThroughGate(N1 + (ulong)i * 1000, "W" + i); Advance(61); }
        Ok(Server.Broadcasts.Count == 6, "at most NewcomerBroadcastsPerHour (6) Herald lines an hour; the seventh is dropped silently", Server.Broadcasts.Count.ToString());
        Clock = Clock.AddHours(1);
        ThroughGate(N2, "Late");
        Ok(Server.Broadcasts.Count == 7, "an hour later the Herald speaks again");
        Ok(Server.Broadcasts.All(b => b.StartsWith("[D6A043]Herald[FFFFFF]: ") && b.EndsWith("walks out of the Gatehouse of the Unwritten, onto the road to the Hearth.")), "the one Herald voice");
    }

    static void SkipTour()
    {
        Site();
        var n = ToNarration(N1, "Ada");
        Clear();
        string r = Cmd(n, "skip");
        Ok(r == "[8FC97A]Hearth[FFFFFF]: As you wish. The gate is open; the Hearth is yours.", "/arrival skip: one green line", r);
        Ok(Stage(n) == "done" && !Owns(n) && Dist(Pos(n), 11, 31) < 0.1, "written; a player in the hall box is moved to the forecourt eject point");
        Ok(B().Length == 0 && !n.All().Contains("You are written"), "no Herald line, no Written line");
        Advance(30);
        Ok(n.Messages.Count == 1, "every queued line is dropped");
        Ok(Cmd(n, "skip").Contains("You have no arrival to skip."), "nothing left to skip");
        Ok(Counter("skipped") == 1, "counted");
        // Skip on the avenue: no move.
        var m = ThroughGate(N2, "Bea");
        Walk(m, 11, 60, 4f);
        var at = Pos(m);
        Cmd(m, "skip");
        Ok(Stage(m) == "done" && Pos(m).z == at.z, "skip outside the hall: never moves anyone");
        // Tour after the arrival: the banners speak again as they pass; the fire beats too, with no credit.
        Clear();
        Ok(Cmd(m, "tour").Contains("The banners, the fire and the roads will speak again as you pass them."), "/arrival tour");
        Walk(m, 11, 74, 2f); Advance(20);
        Ok(m.All().Contains("the Black Raven"), "a banner replayed on its trigger", m.All());
        Walk(m, 11, 126, 4f); Advance(30);
        Ok(m.All().Contains("You warm your hands") && m.Heals.Count == 0 && !Quests.Any(q => q.Contains("arrival_hearth")), "the fire beats replayed, no heal or quest credit again");
        Walk(m, 11, 147, 4f); Advance(2);
        Ok(m.All().Contains("Three roads leave the Hearth"), "the Wayboard's Three Roads on the tour");
        Ok(Pos(m).z == 147, "tour never moves anyone");
        // Skip during creation: the arrival is not run; vanilla spawn.
        var c = Mk(N3, "Cid");
        FirstSpawn(c, true);
        Cmd(c, "skip");
        Finish(c);
        Ok(Stage(c) == "done" && Pos(c).x == RandomSpawn.x, "skip during the Crossing: vanilla spawn, done");
        Ok(Cmd(Mk(N4, "Dag"), "tour").Contains("Not while your arrival is still being made"), "tour before the arrival: not yet");
    }

    static void HourOne()
    {
        Site();
        Protected.Add(N1);
        NextEvent = new Dictionary<string, object> { { "title", "Crown Night" }, { "at", Clock.AddHours(5) } };
        var n = AtFireDone(N1, "Ada");
        Command(n, "quest");
        Clear();
        Walk(n, 11, 145, 3f);
        Advance(2);
        Ok(n.All().Contains("[D6A043]Hearth[FFFFFF]: Three roads leave the Hearth: the Crown Market for coin and contracts, the Listing Field for the Ring, the seats for the houses.")
            && n.All().Contains("[E8913A]Hearth[FFFFFF]: Walk one: [F4C96D]/road crown-market[FFFFFF]. Your first 3 waystone journeys are free."), "the Wayboard: Three Roads", n.All());
        Ok(n.All().Contains("  [A3A6AD]Next in the realm: Crown Night, in 5 h.[FFFFFF]"), "the next event within 48 h, as a relative time", n.All());
        Walk(n, 11, 120, 3f); Walk(n, 11, 146, 3f);
        Ok(Lines(n).Count(l => l.Contains("Three roads")) == 1, "once");
        // ProtectionSoon (GetProtectionMinutesLeft).
        ProtectionLeft[N1] = 9;
        Advance(61);
        Ok(n.All().Contains("[E8913A]Hearth[FFFFFF]: Ten minutes of shelter left. Walls, a crest and a bed before dark."), "ten minutes of shelter left: ProtectionSoon (private, amber)", n.All());
        Advance(61);
        Ok(Lines(n).Count(l => l.Contains("Ten minutes of shelter")) == 1, "once");
        Ok(B().Length == 0, "the end of protection is never announced");
        // Dusk while protected.
        GameClock.Instance.TimeOfDay = 20.5f;
        Advance(61);
        Ok(n.All().Contains("Night is coming, and Ostreval's nights are dark."), "the first dusk while protected: Dusk");
        // The first block.
        Inv(A, "OnCubePlacement", new CubePlaceEvent { GridID = 0, Position = new Vector3Int(200, 8, 200), Sender = n, Material = 2 });
        Inv(A, "OnCubePlacement", new CubePlaceEvent { GridID = 0, Position = new Vector3Int(201, 8, 200), Sender = n, Material = 2 });
        Ok(Lines(n).Count(l => l.Contains("Blocks outside a crest's land decay.")) == 1, "the first block placed: FirstBlock, once");
        // A second waystone: arrival_road.
        Discovered[N1.ToString()] = 2;
        Advance(61);
        Ok(Quests.Count(q => q == N1 + "|custom|arrival_road|1") == 1, "a second waystone within 2 h: arrival_road");
        // /crown in hour one.
        Command(n, "crown");
        Command(n, "crown");
        Ok(Quests.Count(q => q.Contains("arrival_crown")) == 1, "/crown in the first hour: arrival_crown, once");
        // The next join after a logout: the sleeper tip; the second join: the page.
        Offline(n); Online(n); FirstSpawn(n, false);
        Advance(26);
        Ok(n.All().Contains("Your body slept where you stood.") && n.All().Contains("Your page in the Chronicle so far: [F4C96D]/arrival[FFFFFF]."), "the next join: Sleeper and the /arrival page, once each", n.All());
        Offline(n); Online(n); FirstSpawn(n, false);
        Advance(26);
        Ok(Lines(n).Count(l => l.Contains("Your body slept")) == 1 && Lines(n).Count(l => l.Contains("Your page in the Chronicle")) == 1, "not repeated");
        // Without a wayboard visit: Written + 10 min. Without crown-market: the /travel line.
        Site();
        File.WriteAllText(Path.Combine(Dir, "RealmTravel.json"), "{ \"Waystones\": { \"the-hearth\": {} } }");
        var m = AtFireDone(N2, "Bea");
        Command(m, "quest");
        Advance(9 * 60);
        Ok(!m.All().Contains("Three roads"), "not before 10 minutes");
        Advance(130);
        Ok(m.All().Contains("Three roads leave the Hearth") && m.All().Contains("[F4C96D]/travel[FFFFFF] lists the stones you know"), "Written + 10 min; crown-market missing: RoadsBNone", m.All());
        // HourOneTips false.
        Site(false, false, c => Tweak(c, "HourOneTips", false));
        Protected.Add(N3);
        var q = AtFireDone(N3, "Cid");
        Command(q, "quest");
        ProtectionLeft[N3] = 5;
        Advance(15 * 60);
        Ok(!q.All().Contains("Three roads") && !q.All().Contains("Ten minutes of shelter"), "HourOneTips false: no follow-ups");
    }

    static void Page()
    {
        Site();
        Story = "Act 1: The Empty Seat - Ash at the Hearth";
        HouseOf[N1] = "Varrow";
        Discovered[N1.ToString()] = 2;
        NextEvent = new Dictionary<string, object> { { "title", "the Royal Tournament" }, { "at", Clock.AddMinutes(30) } };
        var n = ThroughGate(N1, "Ada");
        string r = Cmd(n);
        Ok(r.Contains("Your arrival: through the gate.") || r.Contains("Your arrival: on the road of banners."), "/arrival during the arrival: the stage", r);
        Ok(r.Contains("Next: walk the banners to the fire."), "and the one next step", r);
        Cmd(n, "skip");
        r = Cmd(n);
        Ok(r.Contains("[D6A043]Hearth[FFFFFF]: Your page so far:") && r.Contains("  House: [C58FC0]Varrow[FFFFFF]. Waystones known: 2. Tale: Act 1: The Empty Seat - Ash at the Hearth."),
            "after it: your page so far (house, waystones, tale)", r);
        Ok(r.Contains("Next in the realm: the Royal Tournament, in 30 min."), "and the next event", r);
        var g = ToNarration(N2, "Bea");
        Ok(Cmd(g).Contains("Next: walk to the gold line before the gate."), "in the Gatehouse: the gold line is next");
        Ok(Cmd(g, "help").Contains("[F4C96D]/arrival skip[FFFFFF] ends it now"), "/arrival help");
    }

    static void AdminCmds()
    {
        Reset();
        NewArrival();
        var p = Mk(N1, "Player", 10, 10);
        Ok(Cmd(p, "admin", "status").Contains("ERR") && Cmd(p, "admin", "status").Contains("You may not do that."), "a player cannot use /arrival admin");
        var adm = BuildSite(false);
        string r = Cmd(adm, "admin", "status");
        Ok(r.Contains("Arrival: closed, mode teleport, gate open (not built)") && r.Contains("Routed 0"), "status: state, mode, gate, counters", r);
        Ok(r.Contains("Block grid: bound"), "status: the block grid is bound");
        r = Cmd(adm, "admin", "stone", "list");
        Ok(r.Contains("stone (6):") && r.Contains("  6. 17,10,10"), "stone list", r);
        Ok(Cmd(adm, "admin", "stone", "remove", "9").Contains("Usage"), "stone remove: a bad number");
        Cmd(adm, "admin", "stone", "remove", "6");
        Ok(((IList)F(SiteData(), "Stones")).Count == 5 && ((IList)F(SiteData(), "StoneUsed")).Count == 5, "stone remove");
        Ok(Cmd(adm, "admin", "banner", "set", "house", "Mordane").Contains("Usage"), "banner set: only the six great houses");
        Ok(Cmd(adm, "admin", "mode", "warp").Contains("Usage"), "mode: teleport|provider|road|off");
        Ok(Cmd(adm, "admin", "gatemode", "drawbridge").Contains("Usage"), "gatemode: open|portcullis");
        Ok(Cmd(adm, "admin", "wave", "on", "30").Contains("wave on for 30 min") && (bool)F(Cfg(), "WaveMode"), "wave on <minutes>");
        Ok(Cmd(adm, "admin", "wave", "off").Contains("wave off") && !(bool)F(Cfg(), "WaveMode"), "wave off");
        Ok(Cmd(adm, "admin", "evict", "off").Contains("evict off") && !(bool)F(Cfg(), "Evict"), "evict off");
        Ok(Cmd(adm, "admin", "lot", "set", "merrin", "halloran", "varrow", "ashgrove", "corvane", "dunmere").Contains("p1-left Merrin, p1-right Halloran"), "lot set: the public lot is written down in slot order");
        Ok(Cmd(adm, "admin", "lot", "set", "merrin", "merrin", "varrow", "ashgrove", "corvane", "dunmere").Contains("Usage"), "lot set: six different great houses");
        var v = Mk(V1, "Vet", 300, 300);
        FirstSpawn(v, false);
        Ok(Cmd(adm, "admin", "reset", "Vet").Contains("reset to pending") && Stage(v) == "pending", "reset <player>: pending");
        Ok(Cmd(adm, "admin", "reset", "Vet", "done").Contains("reset to done") && Stage(v) == "done", "reset <player> done");
        Ok(Cmd(adm, "admin", "veteran", "Vet").Contains("known veteran") && Stage(v) == "done", "veteran <player>");
        Cmd(adm, "admin", "open");
        var n = ToNarration(N2, "Stuck");
        Ok(Cmd(adm, "admin", "pass", "Stuck").Contains("gate opened for Stuck") && (bool)RecF(n, "GateDone"), "pass: opens the gate for someone stuck");
        At(v, 11, 15);
        Ok(Cmd(adm, "admin", "pass", "Vet").Contains("moved to the forecourt") && Dist(Pos(v), 11, 31) < 0.1, "pass: or sends them to E");
        var k = ToNarration(N3, "Skipper");
        Ok(Cmd(adm, "admin", "skip", "Skipper").Contains("Skipper skipped") && Stage(k) == "done", "skip <player>");
        Ok(Cmd(adm, "admin", "nonsense").Contains("Usage"), "an unknown subcommand shows the usage");
        Ok(Cmd(adm, "admin", "hall", "corner3").Contains("Usage"), "hall corner1|corner2");
    }

    static void SiteCheck()
    {
        Reset();
        NewArrival();
        var adm = Mk(1, "Steward", 400, 400);
        Admin(adm);
        string r = Cmd(adm, "admin", "check");
        Ok(r.Contains("no arrival stones") && r.Contains("no mercy stones") && r.Contains("hall box not set") && r.Contains("eject point not set")
            && r.Contains("gold line not set") && r.Contains("hearth not set") && r.Contains("problem(s)"), "check lists every missing point", r);
        Ok(Cmd(adm, "admin", "open").Contains("waits until they are fixed") && !(bool)F(Cfg(), "Open"), "open is refused until the check passes");
        BuildSite(false);
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("Site check passed: 6 stones, 3 mercy stones, gate open arch, beacon 0 cells."), "a complete site passes", r);
        Ok(r.Contains("Note: mercy stone 1 has no block floor (fine on bare ground)"), "mercy stones on bare ground are a note, not a problem", r);
        // A stone with no floor, or no room to stand.
        Grid.Cells.Remove((4, 7, 4));
        Grid.Put(14, 8, 4, 2);
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("stone 1 has no block floor") && r.Contains("stone 3 has no two air cells to stand in"), "floor and air checks", r);
        Grid.Put(4, 7, 4, 1); Grid.Cells.Remove((14, 8, 4));
        // The Hearth must sit on RealmQuests' the_hearth and RealmLaws' Hearth zone (within 3 m).
        File.WriteAllText(Path.Combine(Dir, "RealmQuests.json"), "{ \"Places\": { \"the_hearth\": { \"X\": 11, \"Y\": 10, \"Z\": 140 } } }");
        File.WriteAllText(Path.Combine(CfgDir, "RealmLaws.json"), "{ \"Zones\": [ { \"Name\": \"Hearth\", \"X\": 30, \"Z\": 130, \"Radius\": 40, \"Town\": true } ] }");
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("10 m from RealmQuests' the_hearth") && r.Contains("19 m from RealmLaws' Hearth zone"), "the Hearth centre within 3 m of the_hearth and the Hearth zone", r);
        // A Dominion holding or an arena zone over the site.
        File.WriteAllText(Path.Combine(CfgDir, "RealmLaws.json"), "{ \"Zones\": [ { \"Name\": \"Hearth\", \"X\": 11, \"Z\": 130, \"Radius\": 40, \"Town\": true } ] }");
        File.WriteAllText(Path.Combine(Dir, "RealmQuests.json"), "{ \"Places\": { \"the_hearth\": { \"X\": 11, \"Y\": 10, \"Z\": 130 } } }");
        File.WriteAllText(Path.Combine(Dir, "RealmDominionMap.json"), "{ \"holdings\": [ { \"name\": \"Gate Village\", \"placed\": true, \"x\": 10, \"z\": 10, \"radius\": 30 } ] }");
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("inside the Dominion holding Gate Village"), "no part of the site inside a Dominion holding", r);
        File.Delete(Path.Combine(Dir, "RealmDominionMap.json"));
        File.WriteAllText(Path.Combine(CfgDir, "RealmArena.json"), "{ \"Arenas\": [ { \"Name\": \"Pit\", \"X\": 11, \"Y\": 10, \"Z\": 50, \"Radius\": 10 } ] }");
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("inside the arena zone Pit"), "or an arena zone", r);
        File.Delete(Path.Combine(CfgDir, "RealmArena.json"));
        // The throne, the town zone, the plugins and the waystones.
        AncientThrone_Set(11, 40);
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("within 80 m of the Old Throne"), "more than 80 m from the throne", r);
        AncientThrone_Set(11, 300);
        Stand(adm, 11, 30, "hearth", "set");
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("the hall box lies inside the Hearth town zone"), "outside the Hearth town zone", r);
        Stand(adm, 11, 130, "hearth", "set");
        File.WriteAllText(Path.Combine(Dir, "RealmTravel.json"), "{ \"Waystones\": { } }");
        SetF(A, "waystoneCache", null);
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("waystone the-hearth does not exist") && r.Contains("waystone crown-market does not exist"), "the-hearth must exist; crown-market is a note", r);
        File.WriteAllText(Path.Combine(Dir, "RealmTravel.json"), "{ \"Waystones\": { \"the-hearth\": {} } }");
        SetF(A, "waystoneCache", null);
        SetF(A, "RealmSentinel", null);
        r = Cmd(adm, "admin", "check");
        Ok(r.Contains("RealmSentinel is not loaded"), "RealmSentinel must be loaded", r);
        Ok(!File.Exists(Path.Combine(Dir, "RealmDominionMap.json")) && !File.Exists(Path.Combine(CfgDir, "RealmArena.json")), "the check never creates other plugins' files");
    }

    static void AncientThrone_Set(float x, float z) { CodeHatch.Thrones.AncientThrone.AncientThrone.EntityPosition = new UnityEngine.Vector3(x, 30, z); }

    static void SelfCheck()
    {
        Site();
        var admin = Mk(1, "Steward", 400, 400);
        Admin(admin);
        Grid.Cells.Remove((4, 7, 4));                            // a wipe took the floor under stone 1
        Advance(59 * 60);
        Ok((bool)F(Cfg(), "Open"), "open until the next self-check");
        Advance(62);
        Ok(!(bool)F(Cfg(), "Open") && Counter("self_check_closures") == 1, "SiteSelfCheckMinutes: a stone without its floor closes the arrival");
        Ok(A.Logged.Any(l => l.Contains("Self-check closed the Gatehouse: stone 1 has no floor")) && admin.All().Contains("The Gatehouse closed itself"), "logged, and online staff are told");
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        Finish(n);
        Ok(Pos(n).x == RandomSpawn.x, "a freshly wiped world never routes newcomers onto empty ground");
        // At load.
        Site();
        Grid.Cells.Remove((4, 7, 4));
        Reload();
        Ok(!(bool)F(Cfg(), "Open"), "the self-check also runs at load");
    }

    static void RoadMode()
    {
        Site(false, false, c => Tweak(c, "RoutingMode", "road"));
        var n = Newcomer(N1, "Ada");
        Ok(Pos(n).x == RandomSpawn.x && n.Entity.Teleports.Count == 0, "mode road: no move");
        Ok(Stage(n) == "released" && (bool)RecF(n, "RoadMode"), "released at once");
        Advance(13);
        Ok(n.All().Contains("The current carried your raft off course, 750 m from the Hearth. [F4C96D]/road the-hearth[FFFFFF] shows the way to the fire."), "RoadMode line with the distance", n.All());
        Advance(10 * 60);
        Ok(Stage(n) == "released", "no wander or 8-minute handover in road mode");
        Walk(n, 11, 125, 40f);
        Advance(40);
        Ok(n.All().Contains("You warm your hands") && B().Contains("Ada walks out of the Gatehouse"), "reaching the fire within 2 h: the fire beats and the Herald line there");
        Ok(!n.All().Contains("Iron Stag"), "no banner beats in road mode");
        var m = Newcomer(N2, "Bea");
        Advance(2 * 3600 + 5);
        Ok(Stage(m) == "done" && !m.All().Contains("You are written"), "not at the fire within 2 h: done quietly");
    }

    static void Wave()
    {
        Site(true);
        var adm = Mk(2, "Steward2", 400, 400);
        Admin(adm);
        Cmd(adm, "admin", "wave", "on", "30");
        Advance(4);
        Ok(GateSolid() == 0, "wave mode: the gate is held open");
        var ps = new List<Player>();
        for (int i = 0; i < 12; i++) ps.Add(Newcomer(N1 + (ulong)i * 1000, "W" + i));
        var hits = new int[6];
        foreach (var p in ps) { int idx = Array.FindIndex(StoneXZ, s => Dist(Pos(p), s[0], s[1]) <= 1.25f); if (idx >= 0) hits[idx]++; }
        Ok(hits.All(h => h == 2), "wave mode: two per stone", string.Join(",", hits));
        Ok(ps.All(p => StoneXZ.Select(s => Dist(Pos(p), s[0], s[1])).Min() > 0.01f), "wave mode: every placement with jitter");
        var over = Newcomer(N5, "Overflow");
        Ok(Dist(Pos(over), 11, 128) < 12, "wave mode: the thirteenth goes to a mercy stone with the short lines");
        Advance(30);
        Ok(ps.All(p => p.Popups.Count == 0), "wave mode: no popup card");
        Advance(30 * 60);
        Ok(!(bool)F(Cfg(), "WaveMode"), "wave mode ends by itself");
    }

    static void ReloadRebuild()
    {
        Site();
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Walk(n, 11, 50, 3f);
        Reload();
        Ok(Stage(n) == "banners", "a reload keeps the stage");
        Ok(((IDictionary)F(A, "runs")).Contains(N1), "OnServerInitialized rebuilds the online player's arrival");
        Clear();
        WalkToFire(n);
        Advance(40);
        Ok(n.All().Contains("You warm your hands") && !n.All().Contains("Stone underfoot"), "the next beats fire on their triggers; nothing is repeated");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmArrival.json")).Contains("\"Stage\": \"hearth\"") || Stage(n) == "hearth", "stages are saved");
        // Saves are debounced: a stage change is on disk within two seconds.
        Site();
        var m = ToNarration(N2, "Bea");
        Advance(3);
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmArrival.json")).Contains("\"Stage\": \"gatehouse\""), "a stage change is saved (debounced)");
    }

    static void DataSafety()
    {
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmArrival.json"), "{ this is not json");
        NewArrival();
        Ok((bool)F(A, "loadFailed"), "a damaged data file: load failed");
        Ok(A.Logged.Any(l => l.Contains("vanilla spawns")), "logged: arrivals off, vanilla spawns");
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        Finish(n);
        Ok(Pos(n).x == RandomSpawn.x && n.Entity.Teleports.Count == 0, "routing off: vanilla spawn");
        Ok(ArrivalStage(n) == "none" && !Owns(n), "ArrivalStage none");
        Ok(Cmd(n).Contains("Arrivals are off"), "/arrival says so");
        Inv(A, "OnServerSave");
        Inv(A, "Unload");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmArrival.json")) == "{ this is not json", "the file is never overwritten");
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmArrival.json"), "");
        NewArrival();
        Ok((bool)F(A, "loadFailed") && File.ReadAllText(Path.Combine(Dir, "RealmArrival.json")) == "", "an empty file: the same");
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmArrival.json"), "{ \"Format\": 99 }");
        NewArrival();
        Ok((bool)F(A, "loadFailed"), "a newer format: the same");
        Reset();
        A = null;
        NewArrival(c => Tweak(c, "LineGapSeconds", 999));
        Ok((int)F(Cfg(), "LineGapSeconds") == 30 && (float)F(Cfg(), "TeleportDelaySeconds") == 0f, "config values are clamped");
        Reset();
        var p = new RealmArrival();
        p.Config.Json = "{ \"RoutingMode\": \"warp\", \"GateMode\": \"moat\", \"VeteranMode\": \"royal\" }";
        Inv(p, "LoadDefaultMessages");
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        var cfg = F(p, "config");
        Ok((string)F(cfg, "RoutingMode") == "teleport" && (string)F(cfg, "GateMode") == "open" && (string)F(cfg, "VeteranMode") == "vanilla", "unknown modes fall back to the defaults");
    }

    static void ChatStyle()
    {
        Site();
        Houses.Add("Varrow");
        Protected.Add(N1);
        var n = Newcomer(N1, "[FF0000]Ev{0}il");
        Advance(18);
        Ok(n.All().Contains("FF0000Ev0il, the Chronicle has no page for you"), "a name with colour tags and braces is cleaned (an argument, never a format string)", n.All());
        At(n, 11, 22); Advance(6);
        Ok(B().Contains("[D6A043]Herald[FFFFFF]: FF0000Ev0il walks out"), "and in the Herald line");
        Advance(9);
        WalkToFire(n);
        Advance(40);
        Command(n, "quest");
        var allowed = new HashSet<string> { "D6A043", "8FC97A", "E8913A", "E86A5C", "F4C96D", "A3A6AD", "FFFFFF", "C58FC0", "E08A5C", "8FB0BF", "B8B85A", "EC8A3C", "6FBF85" };
        bool palette = n.Messages.Concat(Server.Broadcasts).All(m => Regex.Matches(m, @"\[([0-9A-Fa-f]{6})\]").Cast<Match>().All(x => allowed.Contains(x.Groups[1].Value.ToUpperInvariant())));
        Ok(palette, "every colour in every line is in the chat palette");
        bool voiced = n.Messages.All(m => m.StartsWith("  ") || Regex.IsMatch(m, @"^\[(D6A043|8FC97A|E8913A|E86A5C)\]Hearth\[FFFFFF\]: "));
        Ok(voiced, "every line opens with the Hearth speaker in its tone, or continues a list", string.Join("\n", n.Messages.Where(m => !(m.StartsWith("  ") || Regex.IsMatch(m, @"^\[(D6A043|8FC97A|E8913A|E86A5C)\]Hearth\[FFFFFF\]: ")))));
        Ok(n.Messages.All(m => Regex.Replace(m, @"\[[0-9A-Fa-f]{6}\]", "").Length <= 230), "lines stay short (200 visible characters plus a name)");
        Ok(n.Messages.All(m => Regex.Matches(m, @"\[F4C96D\]/[a-z]").Count <= 1), "at most one /command per line");
    }
}
