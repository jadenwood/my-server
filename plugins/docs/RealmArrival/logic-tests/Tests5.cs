// Behaviour tests for plugins/RealmArrival.cs, part 5: the integration review (team/arrival-review).
//   Band       the ember band is exactly the site file's cells.emberBand at all four turns: from the anchored plan (even
//              with the hearth point stored off the plan), from the file's offsets, and from the built-in octagon.
//   Lenient    the site reader ignores unknown and additive fields (lights[], terrain[], future keys anywhere).
//   Stuck      nobody waits forever in the gatehouse stage outside the Gatehouse; a pause hands newcomers back without
//              evicting them; a skip in a hall with no eject point opens the gate; no routing into a hall whose
//              portcullis cannot be opened (grid not bound).
//   Returning  a veteran's short or full walk left midway stays variant B; a Finish click the plugin did not see start
//              leaves the record alone and ArrivalStage answers none.
//   Check      the eject point inside the hall box, a stone outside it, a mercy stone inside it.
//   Staff      admins are never evicted; nobody is while paused.
//   Missing    RealmQuests, RealmWarden and RealmHerald not loaded (and then every plugin): a full arrival, no errors.
//   Data       a parseable data file full of nulls and bad entries is normalised, not failed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeHatch.Blocks;
using CodeHatch.Engine.Networking;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static partial class Tests
{
    static string Keys(IEnumerable cells) { return string.Join(" ", cells.Cast<object>().Select(KeyOf).OrderBy(k => k)); }
    static HashSet<string> KeySet(IEnumerable cells) { return new HashSet<string>(cells.Cast<object>().Select(KeyOf)); }

    // The site file's band in world cells for the current anchor and turn (R).
    static HashSet<string> FileBandWorld()
    {
        var want = new HashSet<string>();
        foreach (var c in SiteJson.GetProperty("cells").GetProperty("emberBand").GetProperty("cells").EnumerateArray()) want.Add(Key(WCell(CellOf(c))));
        return want;
    }

    static void BandAllTurns()
    {
        SiteJson = JsonDocument.Parse(File.ReadAllText(SitePath())).RootElement;
        var band = SiteJson.GetProperty("cells").GetProperty("emberBand");
        var centre = CellOf(band.GetProperty("centre"));
        Ok(band.GetProperty("cells").GetArrayLength() == 24 && Key(centre) == Key(PointCell("hearth")) && band.GetProperty("radius").GetInt32() == 5,
            "the site file's ember band: 24 cells at radius 5 round points.hearth");
        // The file's offsets are the octagon of its rule, and not the 15-degree set (round(5 cos 15k), round(5 sin 15k)).
        var rel = new HashSet<string>();
        foreach (var c in band.GetProperty("cells").EnumerateArray()) rel.Add((c[0].GetInt32() - centre[0]) + "," + (c[2].GetInt32() - centre[2]));
        var octagon = new HashSet<string>();
        for (int dx = -5; dx <= 5; dx++) for (int dz = -5; dz <= 5; dz++)
        {
            int m = Math.Max(Math.Abs(dx), Math.Abs(dz)), s = Math.Abs(dx) + Math.Abs(dz);
            if (m <= 5 && s <= 6 && !(m <= 4 && s <= 5)) octagon.Add(dx + "," + dz);
        }
        var degrees = new HashSet<string>();
        for (int k = 0; k < 24; k++) degrees.Add((int)Math.Round(5 * Math.Cos(k * Math.PI / 12)) + "," + (int)Math.Round(5 * Math.Sin(k * Math.PI / 12)));
        Ok(rel.SetEquals(octagon) && !rel.SetEquals(degrees) && rel.Contains("4,2") && !rel.Contains("4,3"), "the file's band is the rule's octagon, not 15-degree steps rounded to the grid");

        for (int r = 0; r < 4; r++)
        {
            string tag = " (turn " + r + ")";
            var adm = PlanWorld(r, false);
            var want = FileBandWorld();
            object hearthCell = Inv(A, "CellAt", Inv(A, "HearthV"));
            var direct = (IList)Inv(A, "BandCells", hearthCell, 5, 0);
            Ok(direct.Count == 24 && KeySet(direct).SetEquals(want), "BandCells on the anchored plan is exactly the file's 24 band cells" + tag, Keys(direct));
            // The hearth point re-stored 2 m off the plan (the fire pit moved): the band still stands on the plan's dais.
            var hw = WStand(PointCell("hearth"));
            At(adm, hw.x + 2.4f, hw.z, hw.y);
            Cmd(adm, "admin", "hearth", "set");
            Cmd(adm, "admin", "beacon", "build", "5");
            var built = (IList)F(SiteData(), "BeaconCells");
            Ok(built.Count == 24 && KeySet(built).SetEquals(want), "beacon build 5 with the hearth stored off the plan: still the plan's cells" + tag, Keys(built));
            Ok(want.All(k => { var p = k.Split(',').Select(int.Parse).ToArray(); return Grid.Mat(p[0], p[1], p[2]) == 3; }), "and every one of them is clay in the grid" + tag);
            // The site file read but not anchored: the file's offsets round the stored Hearth's cell.
            SetF(SiteData(), "Anchored", false);
            var unanchored = (IList)Inv(A, "BandCells", Inv(A, "CellAt", PointWorldOf(PointCell("hearth"))), 5, 0);
            Ok(KeySet(unanchored).SetEquals(want), "the file's offsets round the Hearth's cell, unanchored" + tag);
            SetF(SiteData(), "Anchored", true);
            // No file at all: the built-in rule at this turn gives the same cells.
            var file = F(A, "site");
            SetF(A, "site", null);
            var builtIn = (IList)Inv(A, "BandCells", Inv(A, "CellAt", PointWorldOf(PointCell("hearth"))), 5, 0);
            SetF(A, "site", file);
            Ok(builtIn.Count == 24 && KeySet(builtIn).SetEquals(want), "the built-in fallback (no site file) matches the file's band" + tag, Keys(builtIn));
            // The order runs round the ring: each cell touches the next (an unbroken band for the flare).
            bool touching = true;
            var ordered = builtIn.Cast<object>().Select(c => new[] { (int)F(c, "X"), (int)F(c, "Z") }).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var a = ordered[i]; var b = ordered[(i + 1) % ordered.Count];
                if (Math.Max(Math.Abs(a[0] - b[0]), Math.Abs(a[1] - b[1])) != 1) touching = false;
            }
            Ok(touching, "the fallback's cells run round the ring, each touching the next" + tag);
        }
    }

    // The world metres of a site cell's stand point (the cell centre lowered to its floor), as the plugin stores points.
    static UnityEngine.Vector3 PointWorldOf(int[] siteCell) { return WStand(siteCell); }

    static void LenientSite()
    {
        SiteJson = JsonDocument.Parse(File.ReadAllText(SitePath())).RootElement;
        Ok(SiteJson.TryGetProperty("lights", out var lights) && lights.GetArrayLength() > 0 && SiteJson.TryGetProperty("terrain", out _),
            "the real site file carries lights[] and terrain[], which the plugin has no field for (every site test reads it)");
        var node = JsonNode.Parse(File.ReadAllText(SitePath())).AsObject();
        node["lights"]!.AsArray().Add(JsonNode.Parse("{ \"key\": \"X9\", \"kind\": \"lantern\", \"cell\": [1, 2, 3], \"glow\": { \"radius\": 4, \"colour\": \"#ffaa00\" } }"));
        node["lights"]!.AsArray().Add(JsonNode.Parse("\"a plain string\""));
        node["futureField"] = JsonNode.Parse("{ \"nested\": [1, \"two\", { \"three\": null }], \"flag\": true }");
        node["version"] = 7;
        node["points"]!["A1"]!["note"] = "an additive note";
        node["points"]!["A1"]!["tags"] = JsonNode.Parse("[\"court\", 1, null]");
        node["cells"]!["emberBand"]!["pulse"] = JsonNode.Parse("{ \"seconds\": 2 }");
        node["cells"]!["moat"] = JsonNode.Parse("{ \"cells\": [[0, 0, 0]], \"material\": \"water\" }");
        node["boxes"]!["Z9"] = JsonNode.Parse("{ \"min\": [0, 0, 0], \"max\": [1, 1, 1], \"why\": \"future\" }");
        node["zones"]![0]!["shape"] = "circle";
        node["signs"]![0]!["font"] = "uncial";
        node["pieces"]![0]!["hidden"] = JsonNode.Parse("{ \"by\": [\"nobody\"] }");
        node["lot"]!["notes"] = JsonNode.Parse("[\"drawn on stream\"]");
        string text = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Reset();
        DropSiteDir();
        Directory.CreateDirectory(Path.Combine(Dir, "RealmArrival"));
        File.WriteAllText(Path.Combine(Dir, "RealmArrival", "site.json"), text);
        NewArrival();
        var a = Mk(1, "Steward", 400, 400);
        Admin(a);
        Ok(Cmd(a, "admin", "site", "reload").Contains("site file ok"), "a site file with unknown fields everywhere (lights, futureField, notes, a moat, Z9...) reads as ok");
        Ok(!A.Logged.Any(l => l.Contains("site.json is")), "and nothing is logged against it", string.Join("\n", A.Logged));
        var stones = (IList)F(A, "siteStones");
        Ok(stones.Count == 6 && (string)stones[0] == "A1", "its stones are still A1-A6");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmArrival", "site.json")) == text, "the file is left exactly as it was");
        // Anchored on the ground, it gives the same points as the plain file.
        Grid.Cells.Clear();
        R = 1;
        var t = TurnC(PointCell("gateSet"), 1);
        Anchor = new[] { GateSetWorld[0] - t[0], GateSetWorld[1] - t[1], GateSetWorld[2] - t[2] };
        At(a, GateSetWorld[0] * 1.2f, GateSetWorld[2] * 1.2f, GateSetWorld[1] * 1.2f - 0.3f);
        a.Entity.Forward = Facings[1];
        Ok(Cmd(a, "admin", "site", "anchor").Contains("site anchored"), "site anchor works on it");
        var sd = SiteData();
        Ok(Near(((IList)F(sd, "Stones"))[0], WStand(PointCell("A1"))) && Near(F(sd, "Hearth"), WStand(PointCell("hearth"))), "A1 and the hearth land where the plan says");
        Ok(!((IDictionary)F(sd, "Banners")).Contains("z9") && F(sd, "Pad1") != null, "the extra box is ignored; Z0b still stores the drop pad");
    }

    static void StuckAndReturning()
    {
        // A Finish click for a pending record (the plugin loaded mid-creation, or a stray event): nothing saved or moved,
        // ArrivalStage none for the session (no welcome held back), and the next session is variant C.
        Site();
        var h = Mk(N1, "Hal");
        Finish(h);
        Ok(Stage(h) == "pending" && Pos(h).x == RandomSpawn.x && h.Entity.Teleports.Count == 0, "Finish for a pending record: nothing saved or moved");
        Ok(ArrivalStage(h) == "none" && !Owns(h), "ArrivalStage none for the rest of the session: the other plugins' welcome is not held back");
        var fresh = Mk(N2, "Fen");
        Ok(ArrivalStage(fresh) == "pending" && Owns(fresh), "another newcomer still reads pending");
        Offline(h); Online(h);
        Ok(ArrivalStage(h) == "pending", "the mark is for one session only");
        FirstSpawn(h, false);
        Ok(Stage(h) == "done" && (string)RecF(h, "Variant") == "C", "next session: a returning player (C), a known veteran");

        // Variant B (short): the veteran quits during creation and comes back; and a world reset mid-walk (full).
        Site(tweak: c => Tweak(c, "VeteranMode", "short"));
        var v = Mk(V1, "Old Hand", 200, 200);
        FirstSpawn(v, false);
        FirstSpawn(v, true);
        Ok(Stage(v) == "crossing" && (string)RecF(v, "Variant") == "B", "a veteran on a fresh world, short mode: crossing as B");
        Offline(v); Online(v);
        FirstSpawn(v, true);
        Ok(Stage(v) == "crossing" && (string)RecF(v, "Variant") == "B", "quit during creation and back: still B, never the newcomer's walk");
        Clear();
        Finish(v);
        Advance(13);
        Ok(Server.Broadcasts.Count == 0 && Quests.Count == 0, "no Herald line and no quest credit for the veteran", B());
        Site(tweak: c => Tweak(c, "VeteranMode", "full"));
        var w = Mk(V2, "Older Hand", 200, 200);
        FirstSpawn(w, false);
        FirstSpawn(w, true);
        Finish(w);
        Advance(13);
        Ok(Stage(w) == "gatehouse" && (string)RecF(w, "Variant") == "B", "full mode: the veteran walks as B");
        Offline(w); Online(w);
        FirstSpawn(w, true);                                       // the world was reset mid-walk
        Ok(Stage(w) == "crossing" && (string)RecF(w, "Variant") == "B", "a world reset mid-walk: the walk restarts, still B");

        // A reload two seconds after the cut, the player no longer in the hall: the arrival checks died with the old
        // plugin. The run falls back to road mode instead of waiting in the gatehouse stage for ever.
        Site();
        var n = Newcomer(N3, "Ida");
        Advance(1);
        At(n, 300, 300);
        Advance(1);
        Reload();
        Advance(10);
        Ok(Stage(n) == "gatehouse" && !(bool)RecF(n, "RoadMode"), "just after the reload: still waiting on the move");
        Advance(15);
        Ok((bool)RecF(n, "RoadMode") && Stage(n) == "released" && n.All().Contains("/road the-hearth"), "the checks long over and never in the hall: road mode, with its line", n.All());

        // Pause with a newcomer in the hall and one asleep there: handed back, never evicted or counted.
        var adm = Site(gate: true);
        var p1 = ToNarration(N1, "Ada");
        var p2 = ToNarration(N2, "Bea");
        At(p2, 6, 12);
        Offline(p2);
        Clear();
        Cmd(adm, "admin", "pause");
        Ok(Stage(p1) == "none" && GateSolid() == 0, "pause: the newcomer is handed back (none), the gate is open");
        Advance(10);
        Ok(Counter("evictions") == 0 && !p1.All().Contains("for the Unwritten") && Dist(Pos(p1), 5, 5) < 1.5, "while paused nobody is evicted or moved", Pos(p1).ToString());
        At(p1, 11, 12);
        Cmd(adm, "admin", "resume");
        Ok(Dist(Pos(p1), 11, 31) < 0.1 && p1.All().Contains("You wake by the Gatehouse. The road to the Hearth runs ahead."), "resume: the one still inside is let out to E (ReleasedGate)", p1.All());
        Advance(10);
        Ok(Counter("evictions") == 0 && Alerts.Count == 0, "never as an eviction, never toward an alert");
        Online(p2);
        At(p2, 6, 12);
        FirstSpawn(p2, false);
        Advance(5);
        Ok(Dist(Pos(p2), 11, 31) < 0.1 && p2.All().Contains("You wake by the Gatehouse.") && Counter("evictions") == 0, "the sleeper's player wakes in the hall later: let out the same way", p2.All());

        // A skip in a hall with no eject point (open force on a test site), the portcullis closed: the gate opens.
        adm = Site(gate: true);
        var k = ToNarration(N4, "Kit");
        Ok(GateSolid() == 30, "a lone newcomer waits behind the closed portcullis");
        Cmd(adm, "admin", "eject", "clear");
        Cmd(k, "skip");
        Advance(4);
        Ok(Stage(k) == "done" && GateSolid() == 0, "skip in the hall with no E: the gate opens instead", GateSolid().ToString());
        Ok(Quests.Any(q => q.StartsWith(N4 + "|custom|arrival_gate")), "and the Unwritten chain's first step is credited (the gate is behind them)", string.Join("\n", Quests));
        Advance(60);
        Ok(GateSolid() == 0, "it stays open while they are still inside");
        At(k, 11, 40);
        Advance(30);
        Ok(GateSolid() == 30, "and closes again once they are out");

        // A crash left the portcullis closed and the grid will not bind: nobody is routed into the hall.
        adm = Site(gate: true);
        Ok(GateSolid() == 30, "the portcullis is closed");
        BlockManager.DefaultCubeGrid = null;
        Crash();
        Ok(!(bool)Inv(A, "GridReady"), "after the crash the grid is not bound yet");
        var g = Newcomer(N5, "Gil");
        Ok(g.Entity.Teleports.Count == 0 && (bool)RecF(g, "RoadMode") && A.Logged.Any(l => l.Contains("the gate cannot be opened")),
            "a newcomer is not routed behind a gate nothing can open: road mode", string.Join("\n", A.Logged));
        BlockManager.DefaultCubeGrid = Grid;
        Advance(11);
        Ok((bool)Inv(A, "GridReady") && GateSolid() == 0, "the bind succeeds on its retry and forces the gate open");
    }

    static void CheckLoops()
    {
        var adm = Site();
        Stand(adm, 11, 20, "eject", "set");
        string c = Cmd(adm, "admin", "check");
        Ok(c.Contains("the eject point E lies inside the hall box"), "check: E inside the hall box is a problem (eviction would loop)", c);
        Ok(Cmd(adm, "admin", "close").Length > 0 && Cmd(adm, "admin", "open").Contains("problem") && !(bool)F(Cfg(), "Open"), "open is refused");
        Stand(adm, 11, 31, "eject", "set");
        Stand(adm, 40, 40, "stone", "add");
        c = Cmd(adm, "admin", "check");
        Ok(c.Contains("stone 7 lies outside the hall box"), "check: a stone outside the hall box (every arrival check would fail)", c);
        Cmd(adm, "admin", "stone", "remove", "7");
        Stand(adm, 11, 15, "mercy", "add");
        c = Cmd(adm, "admin", "check");
        Ok(c.Contains("mercy stone 4 lies inside the hall box"), "check: a mercy stone inside the hall box (deaths would go back in)", c);
        Cmd(adm, "admin", "mercy", "remove", "4");
        Ok(!Cmd(adm, "admin", "check").Contains("Problem") && Cmd(adm, "admin", "open").Length > 0 && (bool)F(Cfg(), "Open"), "fixed: the check passes and it opens", Cmd(adm, "admin", "check"));
    }

    static void StaffAndPause()
    {
        var adm = Site();
        At(adm, 11, 10);
        Advance(8);
        Ok(Dist(Pos(adm), 11, 10) < 0.1 && Counter("evictions") == 0, "an admin storing points in the hall is never evicted");
        var v = Mk(V1, "Loiterer", 11, 40);
        FirstSpawn(v, false);
        Cmd(adm, "admin", "pause");
        At(v, 11, 15);
        Advance(8);
        Ok(Dist(Pos(v), 11, 15) < 0.1 && Counter("evictions") == 0, "paused: nobody is evicted (nobody is in an arrival)");
        Cmd(adm, "admin", "resume");
        Advance(5);
        Ok(Dist(Pos(v), 11, 31) < 0.1 && Counter("evictions") == 1, "resumed: a loiterer is evicted again");
    }

    // RealmQuests, RealmWarden and RealmHerald not loaded; then every other plugin too (open force).
    static void MissingPlugins()
    {
        foreach (var set in new[] { new[] { "RealmQuests", "RealmWarden", "RealmHerald" },
            new[] { "RealmQuests", "RealmWarden", "RealmHerald", "RealmSentinel", "RealmTravel", "RealmHouses", "CrownAndConsequences", "RealmEvents", "RealmArena", "RealmRenown" } })
        {
            bool all = set.Length > 3;
            string tag = all ? " (no Realm plugin at all)" : " (no Quests, Warden or Herald)";
            Reset();
            foreach (var s in set) Absent.Add(s);
            NewArrival(c => Tweak(c, "WrittenDeed", "written"));
            var adm = BuildSite(false);
            if (all) Cmd(adm, "admin", "open", "force"); else Cmd(adm, "admin", "open");
            Ok((bool)F(Cfg(), "Open"), "the arrival opens" + tag, Cmd(adm, "admin", "check"));
            Houses.Add("Varrow");
            var m = Mk(M1, "Member", 500, 500);
            HouseOf[M1] = "Varrow";
            Clear();
            var n = ToNarration(N1, "Ada");
            Advance(12);
            Ok(n.Popups.Count == 1, "the Gatehouse window still shows" + tag);
            At(n, 11, 22); Advance(1);
            var x = Mk(V1, "Brute", 11, 30);
            FirstSpawn(x, false);
            Advance(40);
            float dmg = Hit(x, n);
            Ok(Stage(n) == "banners" && dmg == 0f, "through the gate; the release shield or zone guard holds" + tag, Stage(n) + " " + dmg);
            Walk(n, 3, 46, 3f); Advance(10);
            At(n, 3, 46); Advance(8);
            WalkToFire(n);
            Advance(60);
            Command(n, "quest");
            Advance(2);
            Ok(Stage(n) == "done" && n.All().Contains("You are written."), "a full arrival to Written" + tag, n.All());
            Ok(n.All().Contains("Walk carefully. Out here the blades are real."), "no Warden: the Unsheltered line" + tag);
            var e = Respawn(n, new CodeHatch.Networking.Events.Players.PlayerRespawnRandomlyEvent());
            Ok(e.Position.x == RandomSpawn.x, "no Warden: no Hearth's Mercy (protection cannot be seen)" + tag);
            Cmd(n, "");
            Cmd(adm, "admin", "status");
            At(x, 11, 15); Advance(5); At(x, 11, 15); Advance(5); At(x, 11, 15); Advance(5);
            Ok(Counter("evictions") == 3, "eviction and its alert path run without Warden" + tag);
            Advance(120);
            var errors = A.Logged.Where(l => l.StartsWith("ERROR") || l.Contains("failed") || l.Contains("Exception")).ToList();
            Ok(errors.Count == 0, "nothing failed or threw" + tag, string.Join("\n", errors));
            Absent.Clear();
        }
    }

    static void DamagedButReadable()
    {
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmArrival.json"),
            "{ \"Format\": 1, \"Seeded\": true, \"Players\": { \"" + N1 + "\": null, \"abc\": { \"Stage\": \"gatehouse\" }, \"" + N2 + "\": { \"Stage\": \"weird\", \"Name\": null, \"PledgeHouse\": null, \"Variant\": null } },"
            + " \"Site\": { \"Stones\": [ null, { \"X\": 5, \"Y\": 10, \"Z\": 5 } ], \"StoneUsed\": null, \"Mercy\": null, \"MercyUsed\": [ \"2026-01-01T00:00:00Z\", \"2026-01-01T00:00:00Z\" ],"
            + " \"Banners\": { \"varrow\": null, \"Nope\": { \"X\": 1, \"Y\": 1, \"Z\": 1 }, \"ASHGROVE\": { \"X\": 19, \"Y\": 10, \"Z\": 46 } }, \"GateCells\": [ null ], \"BeaconCells\": null,"
            + " \"Lot\": { \"p1-left\": \"nobody\", \"p1-right\": \"Merrin\" }, \"PairOrder\": null, \"Turn\": -5 }, \"Stats\": null }");
        NewArrival();
        Ok(!(bool)F(A, "loadFailed"), "a readable file full of nulls and bad entries loads (it is repaired, not refused)");
        var sd = SiteData();
        Ok(((IList)F(sd, "Stones")).Count == 1 && ((IList)F(sd, "StoneUsed")).Count == 1 && ((IList)F(sd, "Mercy")).Count == 0 && ((IList)F(sd, "MercyUsed")).Count == 0,
            "null stones dropped; the used-times lists follow the stones");
        var banners = (IDictionary)F(sd, "Banners");
        Ok(banners.Count == 1 && banners.Contains("ashgrove"), "banners: null and unknown houses dropped, names lower-cased");
        var lot = (IDictionary<string, string>)F(sd, "Lot");
        Ok(lot.Count == 1 && lot["p1-right"] == "merrin" && (int)F(sd, "Turn") == 3 && ((IList)F(sd, "GateCells")).Count == 0, "lot, turn and gate cells normalised");
        var players = (IDictionary)F(Data(), "Players");
        Ok(players.Count == 1 && (string)F(players[N2.ToString()], "Stage") == "done", "a null record and a bad id dropped; an unknown stage reads as done");
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        Finish(n);
        Advance(30);
        Ok(!A.Logged.Any(l => l.StartsWith("ERROR")), "the plugin runs on it without errors", string.Join("\n", A.Logged));
        Inv(A, "OnServerSave");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmArrival.json")).Contains("\"Counters\""), "and saves it back whole");
    }
}
