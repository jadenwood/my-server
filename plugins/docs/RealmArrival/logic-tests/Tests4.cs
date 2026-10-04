// Behaviour tests for plugins/RealmArrival.cs, part 4: the site plan. The REAL art/sculptures/sites/arrival.json
// (realm-site/1) is copied to oxide/data/RealmArrival/site.json and placed on the ground at each of the four turns; the
// tests compute every expected cell from the file themselves (world cell = anchor + turn(site cell, R), turn([x,y,z],1) =
// [z,y,-x]) and compare with what the plugin stores and builds: the stones A1-A6, mercy M1-M3, E, threshold, hearth,
// wayboard, the banners by the pair lot, the hall box Z0, the gate rows, the ember band and the stone floors. Then a
// newcomer walks the whole arrival on the anchored plan. Last, the review fixes (variant B after a closed arrival, the
// pledge window's Walk on, the gate test while closed, provider stones, a reload in the Gatehouse, the first-move log).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeHatch.Common;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Players;
using CodeHatch.UserInterface.Dialogues;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static partial class Tests
{
    static string Repo = ".";
    static JsonElement SiteJson;
    static int[] Anchor = new int[3];
    static int R;
    static readonly int[] GateSetWorld = { 80, 8, 40 };                // the cell the admin stands in for site anchor
    static readonly UnityEngine.Vector3[] Facings =
        { new UnityEngine.Vector3(0, 0, 1), new UnityEngine.Vector3(1, 0, 0), new UnityEngine.Vector3(0, 0, -1), new UnityEngine.Vector3(-1, 0, 0) };
    static readonly string[] PreviewLot = { "varrow", "dunmere", "ashgrove", "corvane", "halloran", "merrin" };   // the file's lot.preview

    static string SitePath() { return Path.Combine(Repo, "art", "sculptures", "sites", "arrival.json"); }
    static int[] CellOf(JsonElement e) { return new[] { e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32() }; }
    static int[] PointCell(string id) { return CellOf(SiteJson.GetProperty("points").GetProperty(id).GetProperty("cell")); }
    static int[] TurnC(int[] c, int r)
    {
        int x = c[0], z = c[2];
        switch (((r % 4) + 4) % 4)
        {
            case 1: return new[] { z, c[1], -x };
            case 2: return new[] { -x, c[1], -z };
            case 3: return new[] { -z, c[1], x };
            default: return new[] { x, c[1], z };
        }
    }
    static int[] WCell(int[] siteCell) { var t = TurnC(siteCell, R); return new[] { Anchor[0] + t[0], Anchor[1] + t[1], Anchor[2] + t[2] }; }
    static UnityEngine.Vector3 WStand(int[] siteCell) { var w = WCell(siteCell); return new UnityEngine.Vector3(w[0] * 1.2f, w[1] * 1.2f - 0.54f, w[2] * 1.2f); }
    static string Key(int[] c) { return c[0] + "," + c[1] + "," + c[2]; }
    static string KeyOf(object cell) { return F(cell, "X") + "," + F(cell, "Y") + "," + F(cell, "Z"); }
    static bool Near(object point, UnityEngine.Vector3 v, float tol = 0.05f)
    {
        if (point == null) return false;
        float x = (float)F(point, "X"), y = (float)F(point, "Y"), z = (float)F(point, "Z");
        return Math.Abs(x - v.x) < tol && Math.Abs(y - v.y) < tol && Math.Abs(z - v.z) < tol;
    }
    static void DropSiteDir() { string d = Path.Combine(Dir, "RealmArrival"); if (Directory.Exists(d)) Directory.Delete(d, true); }
    static void WriteSiteFile()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "RealmArrival"));
        File.Copy(SitePath(), Path.Combine(Dir, "RealmArrival", "site.json"), true);
    }

    // A fresh world with the site file, the plan anchored at turn r by an admin standing in GateSetWorld, the preview lot
    // recorded, the stone floors and the gold line laid, and the other plugins' files pointing at the plan's Hearth.
    static Player PlanWorld(int r, bool open = true, bool gate = false, bool beacon = false, Action<object> tweak = null)
    {
        Reset();
        DropSiteDir();
        Grid.Cells.Clear();                                           // no default floor: only what the plan needs
        WriteSiteFile();
        NewArrival(tweak);
        R = r;
        var gs = PointCell("gateSet");
        var t = TurnC(gs, r);
        Anchor = new[] { GateSetWorld[0] - t[0], GateSetWorld[1] - t[1], GateSetWorld[2] - t[2] };
        foreach (var f in SiteJson.GetProperty("cells").GetProperty("stoneFloors").GetProperty("cells").EnumerateArray()) { var w = WCell(CellOf(f)); Grid.Put(w[0], w[1], w[2], 1); }
        foreach (var f in SiteJson.GetProperty("cells").GetProperty("goldLine").GetProperty("cells").EnumerateArray()) { var w = WCell(CellOf(f)); Grid.Put(w[0], w[1], w[2], 3); }
        var h = WStand(PointCell("hearth"));
        File.WriteAllText(Path.Combine(Dir, "RealmQuests.json"), "{ \"Places\": { \"the_hearth\": { \"X\": " + h.x + ", \"Y\": " + h.y + ", \"Z\": " + h.z + ", \"Radius\": 40 } } }");
        File.WriteAllText(Path.Combine(CfgDir, "RealmLaws.json"), "{ \"Zones\": [ { \"Name\": \"Hearth\", \"X\": " + h.x + ", \"Z\": " + h.z + ", \"Radius\": 40, \"Town\": true } ] }");
        CodeHatch.Thrones.AncientThrone.AncientThrone.EntityPosition = new UnityEngine.Vector3(h.x + 3000, 30, h.z + 3000);
        var adm = Mk(1, "Steward", GateSetWorld[0] * 1.2f, GateSetWorld[2] * 1.2f);
        Admin(adm);
        At(adm, GateSetWorld[0] * 1.2f, GateSetWorld[2] * 1.2f, GateSetWorld[1] * 1.2f - 0.3f);
        adm.Entity.Forward = Facings[r];
        Cmd(adm, "admin", "site", "anchor");
        Cmd(adm, new[] { "admin", "lot", "set" }.Concat(PreviewLot).ToArray());
        if (gate) { Cmd(adm, "admin", "gatemode", "portcullis"); Cmd(adm, "admin", "gate", "build"); }
        if (beacon) Cmd(adm, "admin", "beacon", "build", "5");
        At(adm, h.x + 500, h.z + 500);
        Advance(gate ? 8 : 2);
        if (open) Cmd(adm, "admin", "open");
        Advance(1);
        Clear();
        return adm;
    }

    static void SitePlan()
    {
        SiteJson = JsonDocument.Parse(File.ReadAllText(SitePath())).RootElement;
        Ok(SiteJson.GetProperty("format").GetString() == "realm-site/1" && SiteJson.GetProperty("id").GetString() == "arrival", "the real site file is realm-site/1, id arrival");

        // No file: the plugin says so, works by hand, and never creates it.
        Reset();
        DropSiteDir();
        NewArrival();
        var a0 = Mk(1, "Steward", 400, 400);
        Admin(a0);
        string s0 = Cmd(a0, "admin", "site");
        Ok(s0.Contains("Site file: missing (copy art/sculptures/sites/arrival.json to oxide/data/RealmArrival/site.json)"), "no site file: site says where it comes from", s0);
        Ok(!File.Exists(Path.Combine(Dir, "RealmArrival", "site.json")) && !Directory.Exists(Path.Combine(Dir, "RealmArrival")), "the site file is never created");
        Ok(Cmd(a0, "admin", "site", "anchor").Contains("no site file"), "site anchor needs the file");
        Ok(Cmd(a0, "admin", "check").Contains("site file missing"), "check notes the missing file");
        // A file of the wrong kind is refused and left alone.
        Directory.CreateDirectory(Path.Combine(Dir, "RealmArrival"));
        string bad = "{ \"format\": \"realm-sculpture/1\", \"id\": \"heralds-pillar\" }";
        File.WriteAllText(Path.Combine(Dir, "RealmArrival", "site.json"), bad);
        Ok(Cmd(a0, "admin", "site", "reload").Contains("not usable: format 'realm-sculpture/1' is not realm-site/1"), "a sculpture file in its place is refused");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmArrival", "site.json")) == bad, "and never rewritten");
        File.WriteAllText(Path.Combine(Dir, "RealmArrival", "site.json"), "{ \"format\": \"realm-site/1\", \"id\": \"arrival\", \"points\": { \"A1\": { \"kind\": \"stone\", \"cell\": [0, 1] } } }");
        Ok(Cmd(a0, "admin", "site", "reload").Contains("point A1 has no cell"), "a point without three coordinates is refused");
        WriteSiteFile();
        Ok(Cmd(a0, "admin", "site", "reload").Contains("site file ok"), "site reload reads the real file");

        // The plan at each of the four turns.
        for (int r = 0; r < 4; r++)
        {
            var adm = PlanWorld(r, true, true, true);
            string tag = " (turn " + r + ")";
            var sd = SiteData();
            Ok((bool)F(sd, "Anchored") && (int)F(sd, "Turn") == r && (int)F(sd, "AnchorX") == Anchor[0] && (int)F(sd, "AnchorY") == Anchor[1] && (int)F(sd, "AnchorZ") == Anchor[2],
                "site anchor: standing in gateSet facing out gives the anchor and the turn" + tag, F(sd, "AnchorX") + "," + F(sd, "AnchorY") + "," + F(sd, "AnchorZ") + " turn " + F(sd, "Turn"));
            var stones = (IList)F(sd, "Stones");
            bool stonesOk = stones.Count == 6;
            for (int i = 0; i < 6 && stonesOk; i++) stonesOk = Near(stones[i], WStand(PointCell("A" + (i + 1))));
            Ok(stonesOk, "the six stones are A1-A6 of the plan, in order" + tag);
            var mercy = (IList)F(sd, "Mercy");
            Ok(mercy.Count == 3 && Near(mercy[0], WStand(PointCell("M1"))) && Near(mercy[1], WStand(PointCell("M2"))) && Near(mercy[2], WStand(PointCell("M3"))), "mercy stones M1-M3" + tag);
            Ok(Near(F(sd, "Eject"), WStand(PointCell("E"))) && Near(F(sd, "Threshold"), WStand(PointCell("threshold"))) && Near(F(sd, "Hearth"), WStand(PointCell("hearth")))
                && Near(F(sd, "Wayboard"), WStand(PointCell("wayboard"))), "E, threshold, hearth and wayboard" + tag);
            var banners = (IDictionary)F(sd, "Banners");
            string[] slots = { "p1-left", "p1-right", "p2-left", "p2-right", "p3-left", "p3-right" };
            bool bOk = banners.Count == 6;
            for (int i = 0; i < 6 && bOk; i++) bOk = Near(banners[PreviewLot[i]], WStand(PointCell("banner." + slots[i])));
            Ok(bOk, "each house's banner point is its slot's (banner.<slot>) by the lot" + tag);
            bool inHall = true;
            for (int i = 1; i <= 6; i++) inHall &= (bool)Inv(A, "InHall", WStand(PointCell("A" + i)), 0f);
            inHall &= (bool)Inv(A, "InHall", WStand(PointCell("threshold")), 0f);
            Ok(inHall && !(bool)Inv(A, "InHall", WStand(PointCell("E")), 0f) && !(bool)Inv(A, "InHall", WStand(PointCell("banner.p1-left")), 0f),
                "the hall box Z0 holds the stones and the gold line, not E or the banners" + tag);
            Ok((bool)Inv(A, "InPad", WStand(new[] { -11, 1, 20 })) && !(bool)Inv(A, "InPad", WStand(PointCell("A1"))), "the drop pad Z0b lies outside the left wall" + tag);
            var rows = SiteJson.GetProperty("cells").GetProperty("gate").GetProperty("rows");
            var want = new HashSet<string>();
            foreach (var row in rows.EnumerateArray()) foreach (var c in row.EnumerateArray()) want.Add(Key(WCell(CellOf(c))));
            var built = ((IList)F(sd, "GateCells")).Cast<object>().Select(KeyOf).ToList();
            Ok(built.Count == 30 && new HashSet<string>(built).SetEquals(want), "gate build writes exactly the plan's 30 gate cells" + tag, built.Count.ToString());
            Ok(want.All(k => { var p = k.Split(',').Select(int.Parse).ToArray(); return Grid.Mat(p[0], p[1], p[2]) == 9; }), "and the gate is closed (reinforced) while nobody waits" + tag);
            var gsw = WCell(PointCell("gateSet"));
            Ok((int)F(sd, "GateX") == gsw[0] && (int)F(sd, "GateY") == gsw[1] && (int)F(sd, "GateZ") == gsw[2] && (int)F(sd, "GateW") == 5 && (int)F(sd, "GateH") == 6,
                "the gate set is gateSet, 5 x 6" + tag);
            var band = SiteJson.GetProperty("cells").GetProperty("emberBand").GetProperty("cells");
            var wantBand = new HashSet<string>();
            foreach (var c in band.EnumerateArray()) wantBand.Add(Key(WCell(CellOf(c))));
            var builtBand = ((IList)F(sd, "BeaconCells")).Cast<object>().Select(KeyOf).ToList();
            Ok(builtBand.Count == 24 && new HashSet<string>(builtBand).SetEquals(wantBand), "beacon build 5 writes exactly the plan's 24 ember band cells" + tag, builtBand.Count.ToString());
            Ok((bool)F(Cfg(), "Open"), "the anchored site passes the check and opens" + tag, Cmd(adm, "admin", "check"));
        }

        // The rule the band follows, without the file: the same 24 offsets round the Hearth.
        Reset();
        DropSiteDir();
        NewArrival();
        var b0 = Mk(1, "Steward", 0, 0);
        Admin(b0);
        At(b0, 60, 60, 8 * 1.2f - 0.3f);
        Cmd(b0, "admin", "hearth", "set");
        Cmd(b0, "admin", "beacon", "build", "5");
        var centre = SiteJson.GetProperty("cells").GetProperty("emberBand").GetProperty("centre");
        var rel = new HashSet<string>();
        foreach (var c in SiteJson.GetProperty("cells").GetProperty("emberBand").GetProperty("cells").EnumerateArray())
            rel.Add((c[0].GetInt32() - centre[0].GetInt32()) + ",0," + (c[2].GetInt32() - centre[2].GetInt32()));
        var got = ((IList)F(SiteData(), "BeaconCells")).Cast<object>().Select(c => ((int)F(c, "X") - 50) + "," + ((int)F(c, "Y") - 8) + "," + ((int)F(c, "Z") - 50)).ToList();
        Ok(got.Count == 24 && new HashSet<string>(got).SetEquals(rel), "without the file, beacon build 5 follows the plan's rule: the same 24-cell octagon", string.Join(" ", got));

        // Admin views of the plan (turn 1).
        var adm1 = PlanWorld(1, false, true, true);
        string plan = Cmd(adm1, "admin", "site", "plan");
        Ok(plan.Contains("  A1 (stone): ") && plan.Contains(" - stored") && plan.Contains("  M3 (mercy): ") && plan.Contains("  banner.p3-right (banner): "), "site plan lists every point by its id", plan);
        string pieces = Cmd(adm1, "admin", "site", "pieces");
        int sculptorPieces = SiteJson.GetProperty("pieces").EnumerateArray().Count(p => p.GetProperty("by").GetString() == "sculptor");
        var pieceLines = pieces.Split('\n').Where(l => l.Contains("/sculpt place")).ToList();
        Ok(pieceLines.Count == sculptorPieces && sculptorPieces == 18, "site pieces: one line per sculptor piece, in route order (18)", pieceLines.Count + "\n" + pieces);
        Ok(pieceLines[0].StartsWith("  1. gatehouse: stand at ") && pieceLines[0].EndsWith("[F4C96D]/sculpt place[FFFFFF] gatehouse-unwritten 3"),
            "the gatehouse is first, with its world turn ((2 + 1) mod 4)", pieceLines[0]);
        Ok(pieceLines.Any(l => l.Contains("p1-left-monument") && l.EndsWith("house-varrow 0")) && pieceLines.Any(l => l.Contains("p1-right-stone") && l.EndsWith("pledge-stone-dunmere 1")),
            "{house} filled from the lot, turns made world turns", string.Join("\n", pieceLines));
        Ok(pieceLines.Any(l => l.Contains("processional-a") && l.EndsWith(" (optional)")), "optional pieces say so");
        var gsStand = WStand(SiteJson.GetProperty("pieces")[0].GetProperty("stand").GetProperty("cell").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Ok(pieceLines[0].Contains("stand at " + (int)Math.Round(gsStand.x) + "," + (int)Math.Round(gsStand.y) + "," + (int)Math.Round(gsStand.z) + " facing -x"),
            "the stand spot in world metres, facing turned (-z turned once is -x)", pieceLines[0]);
        string signs = Cmd(adm1, "admin", "site", "signs");
        var signLines = signs.Split('\n').Where(l => l.Contains("Bind with")).ToList();
        int signCount = SiteJson.GetProperty("signs").GetArrayLength();
        Ok(signLines.Count == signCount && signCount == 16, "site signs: every sign spot of the plan (16: G1-G4, P1-P6, W1-W5, H1)", signLines.Count.ToString() + "\n" + signs);
        Ok(signLines.Any(l => l.StartsWith("  P1 art crest-varrow: at ")) && signLines.Any(l => l.StartsWith("  G2 board chronicle: at ")), "sign bindings, crests by the lot", signs);
        Ok(signs.Contains("    [A3A6AD]Three Roads | Crown Market: coin and contracts."), "notice texts are listed under their sign");
        string sheet = Cmd(adm1, "admin", "runsheet");
        var sheetLines = sheet.Split('\n');
        Ok(sheetLines.Length == 9 && sheetLines[0].Contains("After a wipe, in order:"), "runsheet: a head and the 8 steps", sheet);
        Ok(sheetLines[1].EndsWith(" - done") && sheetLines[3].Contains(" - done (p1-left Varrow") && sheetLines[4].Contains("done (6 stone floors)")
            && sheetLines[6].Contains(" - done (gate 30 cells, band 24 cells)") && sheetLines[7].EndsWith(" - done") && sheetLines[8].Contains("the check passes"),
            "runsheet: closed, the lot, the floors, the gate and band, the points and the check", sheet);
        Ok(sheetLines.All(l => System.Text.RegularExpressions.Regex.Replace(l, @"\[[0-9A-Fa-f]{6}\]", "").Length <= 200), "every run-sheet line is short");
        // A point that moved is noted; standing on it again fixes it.
        var hearthW = WStand(PointCell("hearth"));
        At(adm1, hearthW.x + 5, hearthW.z, hearthW.y);
        Cmd(adm1, "admin", "hearth", "set");
        string ch = Cmd(adm1, "admin", "check");
        Ok(ch.Contains("against the plan: hearth 5 m off"), "check notes a stored point 5 m off the plan", ch);
        Ok(Cmd(adm1, "admin", "runsheet").Contains("7. Re-store any point that moved") && Cmd(adm1, "admin", "runsheet").Contains("to do (1 off the plan, 0 not stored)"), "runsheet step 7 shows it");
        At(adm1, hearthW.x, hearthW.z, hearthW.y);
        Cmd(adm1, "admin", "hearth", "set");
        Ok(!Cmd(adm1, "admin", "check").Contains("against the plan"), "re-stored: the note goes");
        Ok(Cmd(adm1, "admin", "site", "anchor").Contains("gate remove and beacon clear first"), "no re-anchor while the plugin's cells stand (they belong to the old anchor)");

        // The self-check reads the plan's stone floors.
        var adm2 = PlanWorld(2, true);
        Ok((bool)F(Cfg(), "Open"), "open on the anchored plan (turn 2)");
        var a3 = WCell(CellOf(SiteJson.GetProperty("cells").GetProperty("stoneFloors").GetProperty("cells")[2]));
        Grid.Cells.Remove((a3[0], a3[1], a3[2]));
        Advance(61 * 60);
        Ok(!(bool)F(Cfg(), "Open") && A.Logged.Any(l => l.Contains("Self-check closed the Gatehouse: A3 has no floor")), "a missing stone floor (A3) closes the Gatehouse by itself, naming the stone",
            string.Join("\n", A.Logged));

        // The pair lot: drawn in public.
        var adm3 = PlanWorld(0, false);
        Server.Broadcasts.Clear();
        string drawn = Cmd(adm3, "admin", "lot", "draw");
        var lot = (IDictionary<string, string>)F(SiteData(), "Lot");
        Ok(lot.Count == 6 && new HashSet<string>(lot.Values).SetEquals(new[] { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" }), "lot draw: each great house in one slot", drawn);
        Ok(Server.Broadcasts.Count == 1 && Server.Broadcasts[0].StartsWith("[D6A043]Herald[FFFFFF]: The lot is cast for the Gatehouse road. Nearest the gate stand ["), "the Herald tells the realm the draw", B());
        Ok(A.Logged.Any(l => l.StartsWith("The pair lot for the Gatehouse road: p1-left ")), "and it is written in the log for the run-sheet");
        string p1 = lot["p1-left"];
        Ok(Near(((IDictionary)F(SiteData(), "Banners"))[p1], WStand(PointCell("banner.p1-left"))), "the banners follow the new lot");
        Ok(Cmd(adm3, "admin", "lot", "set", "varrow", "dunmere").Contains("Usage"), "lot set needs all six");
        string ln = Cmd(adm3, "admin", "lot", "clear");
        Ok(((IDictionary<string, string>)F(SiteData(), "Lot")).Count == 0 && Cmd(adm3, "admin", "check").Contains("the pair lot is not drawn"), "lot clear; check notes the missing lot");
    }

    // A newcomer walks the whole arrival on the anchored plan (turn 3, portcullis, ember band).
    static void PlanArrival()
    {
        var adm = PlanWorld(3, true, true, true);
        var stones = Enumerable.Range(1, 6).Select(i => WStand(PointCell("A" + i))).ToList();
        var n = Newcomer(N1, "Ada");
        Ok(stones.Any(s => Math.Abs(Pos(n).x - s.x) < 1.3 && Math.Abs(Pos(n).z - s.z) < 1.3 && Math.Abs(Pos(n).y - (s.y + 0.5f)) < 0.01), "the cut puts the newcomer on an arrival stone of the plan (+0.5 m)", Pos(n).ToString());
        Advance(12.5);
        Ok(n.All().Contains("Stone underfoot"), "narration starts in the Gatehouse");
        Advance(6);
        var th = WStand(PointCell("threshold"));
        Walk(n, th.x, th.z, 3f);
        Advance(1);
        var top = SiteJson.GetProperty("cells").GetProperty("gate").GetProperty("rows")[0].EnumerateArray().Select(c => WCell(CellOf(c))).ToList();
        var bottom = SiteJson.GetProperty("cells").GetProperty("gate").GetProperty("rows")[5].EnumerateArray().Select(c => WCell(CellOf(c))).ToList();
        Ok(Stage(n) == "released" || Stage(n) == "banners", "the gold line of the plan opens the gate", Stage(n));
        Ok(top.All(c => Grid.Mat(c[0], c[1], c[2]) == 0), "the plan's top row went first");
        Advance(3);
        Ok(bottom.All(c => Grid.Mat(c[0], c[1], c[2]) == 0), "and the gate has sunk into the ground");
        Ok(Server.Broadcasts.Any(b => b.Contains("Ada walks out of the Gatehouse of the Unwritten")), "the Herald line");
        var bandCell = WCell(CellOf(SiteJson.GetProperty("cells").GetProperty("emberBand").GetProperty("cells")[0]));
        Ok(Grid.Rgb(bandCell[0], bandCell[1], bandCell[2]) == 0xf4c96d, "the plan's ember band flares");
        Advance(10);
        // Down the avenue: the first pair is varrow (left) and dunmere (right) by the lot.
        var e = WStand(PointCell("E"));
        Walk(n, e.x, e.z, 3f);
        var p1 = WStand(new[] { 0, 1, 37 });
        Walk(n, p1.x, p1.z, 2f);
        Advance(14);
        Ok(n.All().Contains("[C58FC0]Varrow[FFFFFF], the Iron Stag") && n.All().Contains("[B8B85A]Dunmere[FFFFFF], the Drowned Bell"), "the first pair speaks: Varrow and Dunmere, as drawn", n.All());
        var p2 = WStand(new[] { 0, 1, 61 });
        Walk(n, p2.x, p2.z, 2f); Advance(14);
        var p3 = WStand(new[] { 0, 1, 85 });
        Walk(n, p3.x, p3.z, 2f); Advance(14);
        Ok(n.All().Contains("Halloran[FFFFFF], the Ember Hound") && n.All().Contains("Merrin[FFFFFF], the Silver Eel"), "the third pair by the fire: Halloran and Merrin");
        var near = WStand(new[] { 0, 1, 99 });
        Walk(n, near.x, near.z, 2f);
        Advance(40);
        Ok(n.All().Contains("You warm your hands at the fire") && Stage(n) == "hearth", "the fire beats at the plan's hearth", n.All());
        Command(n, "quest");
        Ok(Stage(n) == "done" && n.All().Contains("You are written."), "Written on /quest");
        Ok(Quests.Contains(N1 + "|custom|arrival_gate|1") && Quests.Contains(N1 + "|custom|arrival_banners|1") && Quests.Contains(N1 + "|custom|arrival_hearth|1"), "quest credit for the gate, the banners and the hearth");
        // Somebody lingers in the plan's hall: evicted to the plan's E.
        var loiter = Mk(V2, "Loiterer", 0, 0);
        FirstSpawn(loiter, false);
        var a5 = WStand(PointCell("A5"));
        At(loiter, a5.x, a5.z, a5.y);
        Advance(5);
        Ok(Math.Abs(Pos(loiter).x - e.x) < 0.01 && Math.Abs(Pos(loiter).z - e.z) < 0.01 && loiter.All().Contains("The Gatehouse is for the Unwritten."), "a loiterer in Z0 is moved to the plan's E", Pos(loiter).ToString());
    }

    // The review fixes.
    static void ReviewFixes()
    {
        // A player who made a character while the arrival was closed is a veteran after a wipe (stage none + Finished).
        Reset();
        NewArrival();
        var adm = BuildSite(false);
        var c = Mk(N1, "Ada");
        FirstSpawn(c, true);
        Ok(Stage(c) == "none" && ArrivalStage(c) == "none", "closed: a new player is not handled (none)");
        Finish(c);
        Ok((bool)RecF(c, "Finished"), "the character is made: Finished");
        var q = Mk(N2, "Quitter");
        FirstSpawn(q, true);
        Offline(q);                                                  // quit during creation while closed
        Cmd(adm, "admin", "open");
        Ok((bool)F(Cfg(), "Open"), "opened");
        Offline(c);
        Online(c);
        FirstSpawn(c, true);                                         // a wipe: the game asks for a new character
        Ok(Stage(c) == "done" && (string)RecF(c, "Variant") == "B", "after a wipe they are variant B (a veteran), not a newcomer", Stage(c) + " " + RecF(c, "Variant"));
        Finish(c);
        Advance(13);
        Ok(c.All().Contains("Welcome back to Ostreval, Ada."), "the Veteran line after the loader");
        Online(q);
        FirstSpawn(q, true);
        Ok(Stage(q) == "crossing" && (string)RecF(q, "Variant") == "A", "a player who never finished a character is still new (variant A)", Stage(q));

        // The pledge window's "Walk on" ends the dwell; standing on does not turn it into a yes.
        Site();
        Houses.Add("Varrow");
        var n = ThroughGate(N3, "Bea");
        Advance(9);
        Walk(n, 11, 46, 3f);
        Advance(14);
        Clear();
        OnStone(n, "varrow", 3.5);
        var pop = n.Popups.LastOrDefault();
        Ok(pop != null && pop.Kind == "confirm", "the pledge window opens after 3 s");
        Answer(pop, Options.No);
        Advance(8);
        Ok((int)RecF(n, "Pledges") == 0 && !n.All().Contains("Word goes to House"), "Walk on: no pledge, however long they stand", n.All());
        At(n, 11, 46);
        Advance(1.5);
        OnStone(n, "varrow", 3.5);
        Ok(n.All().Split('\n').Count(l => l.Contains("Stand fast to look to House")) == 2, "stepping off and on asks again (in chat; the two windows are used)");
        Advance(3.5);
        Ok((int)RecF(n, "Pledges") == 1, "and standing 3 s more pledges by the chat fallback");

        // gate build and gate test close the gate while the arrival is closed (play-test 6 on a test wall).
        Reset();
        NewArrival();
        var g = BuildSite(false, true);
        Ok(!(bool)F(Cfg(), "Open") && GateSolid() == 30, "closed arrival: gate build closes the 30 cells", GateSolid().ToString());
        Cmd(g, "admin", "gate", "test");
        Advance(4);
        Ok(GateSolid() == 0, "gate test: it opens", GateSolid().ToString());
        Advance(20);
        Ok(GateSolid() == 30, "and closes again after GateMinOpenSeconds, the arrival still closed", GateSolid().ToString());
        Advance(60);
        Ok(GateSolid() == 30, "and stays closed (no auto cycle while closed)");

        // Provider mode marks one stone, not two.
        Site();
        var adm2 = Mk(2, "Steward2", 400, 400);
        Admin(adm2);
        Cmd(adm2, "admin", "mode", "provider");
        var p = Mk(N4, "Cid");
        FirstSpawn(p, true);
        Finish(p);
        var used = ((List<DateTime>)F(SiteData(), "StoneUsed")).Count(t => t != DateTime.MinValue);
        Ok(used == 1, "provider mode: exactly one stone is marked used", used.ToString());

        // A reload in the Gatehouse does not say the Wake and Naming lines again.
        Site();
        var w = ToNarration(N5, "Dag");
        Advance(6);
        Ok(w.All().Contains("Stone underfoot") && w.All().Contains("Dag, the Chronicle has no page for you"), "Wake and Naming said");
        Reload();
        Clear();
        Advance(10);
        Ok(!w.All().Contains("Stone underfoot") && !w.All().Contains("no page for you"), "after a reload they are not said again", w.All());
        Advance(12);
        Ok(Stage(w) == "released" && w.All().Contains("The gate opens on its own."), "the gate opens 20 s after the reload, as after a resume", w.All());

        // LogHooks: the first move after the Finish click is logged with its delay (play-test 1).
        Site(tweak: cfg => Tweak(cfg, "LogHooks", true));
        var lg = Newcomer(N1 + 50, "Eir");
        Advance(2);
        At(lg, Pos(lg).x + 3, Pos(lg).z, Pos(lg).y);
        Advance(1.2);
        Ok(A.Logged.Any(l => l.Contains("[hook]") && l.Contains("first move") && l.Contains("s after OnPlayerSpawned")), "LogHooks: the first move is logged with its delay", string.Join("\n", A.Logged));
        Ok(A.Logged.Any(l => l.Contains("OnPlayerSpawn ") && l.Contains("AtFirstSpawn True")) && A.Logged.Any(l => l.Contains("OnPlayerSpawned")), "and the spawn hooks with timestamps");
    }
}
