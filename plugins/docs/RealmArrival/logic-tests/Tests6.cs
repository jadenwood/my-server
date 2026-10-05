// Behaviour tests for plugins/RealmArrival.cs, part 6: the known gaps closed on team/arrival-fixes.
//   InWorld    players online when the plugin loads, opens or resumes, whose first spawn it never saw: one whose
//              character is made (Character.HasCompletedCreation) is a returning player at once (variant C, done;
//              ArrivalStage none this session), never sent into the arrival; one on the character screen stays pending;
//              one the game says nothing about stays as before; a record left crossing whose Finish click came while the
//              plugin was not loaded is done at load.
//   MidDeath   a death during the arrival with no mercy stone stored: E (just outside the gate), else the game's spawn
//              and road mode, else written at once; counted (mid_death, mid_death_no_mercy); MercyUsed never charged.
//   Gate       a crash with the portcullis closed and the data file unreadable: the cells from RealmArrival_gate.json
//              are forced open at load; without that file, /arrival admin gate open force from the site plan, standing on
//              E outside the gate (only gate-material cells cleared); the gate file follows gate build and gate remove.
//   Config     a config file that cannot be read is copied aside (RealmArrival.json.broken-<time>), never written, one
//              error logged; config-saving admin commands are refused until /arrival admin config reset confirm.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CodeHatch.Engine.Characters;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events.Players;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using static W;

static partial class Tests
{
    // A player already online when the plugin (re)loads: no hooks seen. created 1 = character made, 0 = on the character
    // screen, -1 = the game says nothing (no CurrentCharacter).
    static Player Raw(ulong id, string name, float x, float z, int created)
    {
        var p = new Player(id, name);
        p.Entity.Position = new UnityEngine.Vector3(x, 10, z);
        if (created >= 0) p.CurrentCharacter = new Character { HasCompletedCreation = created == 1 };
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        return p;
    }

    static void InWorldAtLoad()
    {
        // First load with players online (an open arrival).
        Site();
        var w = Raw(N1, "Ada", 300, 300, 1);
        var c = Raw(N2, "Bea", -400, -400, 0);
        var u = Raw(N3, "Cid", 300, 310, -1);
        Reload();
        Ok(Stage(w) == "done" && (string)RecF(w, "Variant") == "C" && (bool)RecF(w, "Finished"),
            "online at load with a character made: a returning player at once (variant C, done)", Stage(w) + " " + RecF(w, "Variant"));
        Ok(ArrivalStage(w) == "none" && !Owns(w), "ArrivalStage none for this session (not pending until a relog)", ArrivalStage(w));
        Ok(Stage(c) == "pending" && ArrivalStage(c) == "pending" && Owns(c), "online at load on the character screen: still pending");
        Ok(Stage(u) == "pending" && ArrivalStage(u) == "pending", "online at load and the game says nothing: left as before");
        Ok(Counter("resolved_in_world") == 1, "counted once (resolved_in_world)");
        // Never into the arrival: a stray or forged Finish click moves nothing.
        Inv(A, "OnPlayerSpawned", new PlayerPreSpawnCompleteEvent { Player = w });
        Advance(15);
        Ok(w.Entity.Teleports.Count == 0 && Stage(w) == "done" && !w.All().Contains("Stone underfoot"), "a Finish click for them later moves nothing and says nothing");
        // The one on the character screen finishes: the Finish click of a creation the plugin did not see start.
        c.CurrentCharacter.HasCompletedCreation = true;
        Finish(c);
        Advance(15);
        Ok(c.Entity.Teleports.Count == 0 && ArrivalStage(c) == "none" && !c.All().Contains("Stone underfoot"),
            "the one on the character screen: its Finish click starts nothing, ArrivalStage none this session");
        // Next session: a known veteran; on a fresh world, variant B (never the newcomer's Herald line or quest credit).
        Offline(w); Online(w);
        FirstSpawn(w, false);
        Ok(ArrivalStage(w) == "done", "next session: done, a known veteran");
        Offline(w); Online(w);
        w.CurrentCharacter.HasCompletedCreation = false;
        FirstSpawn(w, true);
        Finish(w);
        Advance(15);
        Ok((string)RecF(w, "Variant") == "B" && Stage(w) == "done" && w.Entity.Teleports.Count == 0 && !B().Contains("Ada walks out")
            && !Quests.Any(q => q.StartsWith(N1.ToString())), "on a fresh world: a veteran (variant B, vanilla), never the newcomer's arrival");

        // Opened later: a player the game said nothing about at load, known by the time the arrival opens.
        Reset();
        NewArrival();
        var adm = BuildSite(false);
        var x = Raw(N4, "Dag", 300, 300, -1);
        Reload();
        Ok(Stage(x) == "pending" && ArrivalStage(x) == "none", "closed: none (and nothing known at load)");
        x.CurrentCharacter = new Character { HasCompletedCreation = true };
        Cmd(adm, "admin", "open");
        Ok((bool)F(Cfg(), "Open") && Stage(x) == "done" && ArrivalStage(x) == "none" && !Owns(x), "at open: in the world, so resolved (not pending until a relog)");
        // Resume after a pause, and a mode switched back on, do the same.
        var y = Raw(N5, "Eli", 300, 320, -1);
        Reload();
        Cmd(adm, "admin", "pause");
        y.CurrentCharacter = new Character { HasCompletedCreation = true };
        Ok(ArrivalStage(y) == "none", "paused: none");
        Cmd(adm, "admin", "resume");
        Ok(Stage(y) == "done" && ArrivalStage(y) == "none", "at resume: resolved");
        var z = Raw(V1, "Fen", 300, 330, -1);
        Reload();
        Cmd(adm, "admin", "mode", "off");
        z.CurrentCharacter = new Character { HasCompletedCreation = true };
        Cmd(adm, "admin", "mode", "teleport");
        Ok(Stage(z) == "done" && ArrivalStage(z) == "none", "at a mode switched back on: resolved");
        // Staff who reset someone to pending this session keep what they set.
        Cmd(adm, "admin", "close");
        Ok(Cmd(adm, "admin", "reset", "Fen").Contains("reset to pending") && Stage(z) == "pending", "staff reset Fen to pending");
        Cmd(adm, "admin", "open");
        Ok(Stage(z) == "pending", "and open leaves a staff reset alone");

        // A record left crossing whose Finish click came while the plugin was not loaded: done at load, never moved.
        Site();
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        Ok(Stage(n) == "crossing", "crossing");
        n.CurrentCharacter = new Character { HasCompletedCreation = true };
        n.Entity.Position = RandomSpawn;
        Reload();
        Advance(15);
        Ok(Stage(n) == "done" && n.Entity.Teleports.Count == 0 && !n.All().Contains("Stone underfoot"), "crossing and the character made while unloaded: done at load, nothing moves");
        // Still on the character screen at load: stays crossing, and the arrival starts at its Finish click.
        var m = Mk(N2, "Bea");
        FirstSpawn(m, true);
        m.CurrentCharacter = new Character { HasCompletedCreation = false };
        Reload();
        Ok(Stage(m) == "crossing", "crossing and still on the character screen at load: left crossing");
        m.CurrentCharacter.HasCompletedCreation = true;
        Finish(m);
        Ok(Stage(m) == "gatehouse" && m.Entity.Teleports.Count == 1, "and its Finish click starts the arrival as usual");
    }

    static void MidDeathNoMercy()
    {
        // No mercy stone, E stored: E, and the run goes on to the banners and the fire.
        var adm = Site();
        Ok(Cmd(adm, "admin", "mercy", "clear").Contains("mercy list cleared"), "the mercy stones cleared while open");
        Protected.Add(N1);
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Walk(n, 11, 60, 3f);
        Clear();
        var e = Respawn(n, new PlayerRespawnNormalEvent());
        Ok(Dist(e.Position, 11, 31) < 0.1f && Math.Abs(e.Position.y - 10.5f) < 0.01f, "a death mid-arrival with no mercy stone: the eject point E, +0.5 m", e.Position.ToString());
        Ok(Graces.Contains(N1 + "|20") && !e.Cancelled && n.Heals.Count == 0, "SentinelGrace(id, 20) first; never Cancel(), no items");
        Ok(!(bool)Inv(A, "InHall", e.Position, 0f), "never back into the hall");
        Ok(Stage(n) == "banners" && (bool)RecF(n, "ToFire") && (bool)RecF(n, "GateDone") && (int)RecF(n, "Deaths") == 1,
            "the run goes on (banners, then the fire); the death is counted");
        Ok((int)RecF(n, "MercyUsed") == 0, "MercyUsed is not charged (design 6.1: deaths in the arrival have their own rule)");
        Ok(Counter("mid_death") == 1 && Counter("mid_death_no_mercy") == 1, "counted: mid_death and mid_death_no_mercy");
        Advance(0.2);
        Ok(n.All().Contains("You wake by the Gatehouse. The road to the Hearth runs ahead."), "ReleasedGate a tick later", n.All());
        n.Entity.Position = e.Position;
        WalkToFire(n);
        Advance(45);
        Ok(n.All().Contains("You warm your hands"), "the fire beats follow");
        Command(n, "quest");
        Ok(Stage(n) == "done", "and the handover: nothing left half-finished");
        var after = Respawn(n, new PlayerRespawnRandomlyEvent());
        Ok(after.Position.x == RandomSpawn.x && (int)RecF(n, "MercyUsed") == 0, "after Written with no mercy stone: the game's respawn, nothing used up");
        string st = Cmd(adm, "admin", "status");
        Ok(st.Contains("deaths in arrival 1 (1 with no mercy stone)"), "status shows them", st);

        // No mercy stone and no E: the game's spawn stands; road mode; the fire beats and the Herald line at the fire.
        adm = Site();
        Cmd(adm, "admin", "mercy", "clear");
        Cmd(adm, "admin", "eject", "clear");
        var g = ToNarration(N2, "Bea");
        Clear();
        var ge = Respawn(g, new PlayerRespawnRandomlyEvent());
        Ok(ge.Position.x == RandomSpawn.x && Stage(g) == "released" && (bool)RecF(g, "RoadMode") && (bool)RecF(g, "GateDone"),
            "no mercy stone and no E: the game's respawn stands and the run goes on in road mode", Stage(g));
        Advance(0.2);
        Ok(g.All().Contains("The Hearth's smoke is 750 m off.") && g.All().Contains("/road the-hearth"), "MidDeathRoad: how far the fire is, and the road", g.All());
        g.Entity.Position = ge.Position;
        Advance(5);
        At(g, 11, 126);
        Advance(45);
        Ok(g.All().Contains("You warm your hands") && B().Contains("Bea walks out of the Gatehouse"), "at the fire: the fire beats and the Herald line, as in road mode", g.All());
        Command(g, "quest");
        Ok(Stage(g) == "done", "written");

        // No mercy stone, no E and no Hearth: written at once.
        adm = Site();
        Cmd(adm, "admin", "mercy", "clear");
        Cmd(adm, "admin", "eject", "clear");
        Cmd(adm, "admin", "hearth", "clear");
        var h = ThroughGate(N3, "Cid");
        Clear();
        var he = Respawn(h, new PlayerRespawnNormalEvent());
        Advance(0.2);
        Ok(he.Position.x == RandomSpawn.x && Stage(h) == "done" && (DateTime)RecF(h, "WrittenAt") != DateTime.MinValue && h.All().Contains("The Hearth will keep"),
            "and with no Hearth either: written at once, with the way back", h.All());
        Ok(!((IDictionary)F(A, "runs")).Contains(h.Id) && !Owns(h), "no run is left behind");
        // A veteran, and a bed, are untouched as before.
        var v = Mk(V1, "Old Hand");
        FirstSpawn(v, false);
        Ok(Respawn(v, new PlayerRespawnNormalEvent()).Position.x == RandomSpawn.x, "a veteran's respawn stays vanilla");
    }

    static void GateRecovery()
    {
        string gf = Path.Combine(Dir, "RealmArrival_gate.json");
        string df = Path.Combine(Dir, "RealmArrival.json");
        var adm = Site(true);
        Ok(GateSolid() == 30, "the portcullis stands closed", GateSolid().ToString());
        Ok(File.Exists(gf) && JsonDocument.Parse(File.ReadAllText(gf)).RootElement.GetProperty("Cells").GetArrayLength() == 30,
            "gate build keeps its 30 cells in RealmArrival_gate.json");
        // Data OK: gate open force opens at once (no cycle).
        Ok(Cmd(adm, "admin", "gate", "open", "force").Contains("gate forced open: 30 of 30 cells cleared") && GateSolid() == 0, "gate open force: every cell to air at once");
        Advance(30);
        Ok(GateSolid() == 30, "and the normal cycle closes it again");
        // The crash: the data file truncated, the gate closed.
        string keptGate = File.ReadAllText(gf);
        File.WriteAllText(df, "{ \"Players\": { trunc");
        Crash();
        Ok((bool)F(A, "loadFailed"), "after the crash the data file cannot be read");
        Ok(GateSolid() == 0, "the gate's cells from RealmArrival_gate.json are forced open at load", GateSolid().ToString());
        Ok(A.Logged.Any(l => l.Contains("from oxide/data/RealmArrival_gate.json are open (30 were solid)")), "and the log says so", string.Join("\n", A.Logged));
        Ok(Cmd(adm, "admin", "gate", "open", "force").Contains("(the cells from RealmArrival_gate.json)"), "gate open force works in that state too");
        Inv(A, "OnServerSave");
        Inv(A, "Unload");
        Ok(File.ReadAllText(df) == "{ \"Players\": { trunc" && File.ReadAllText(gf) == keptGate, "neither file is written while the data file is unreadable");

        // The gate file gone too, and no site file: closed, the log says how, staff are told to clear it by hand.
        adm = Site(true);
        File.Delete(gf);
        File.WriteAllText(df, "{ \"Players\": { trunc");
        Crash();
        Ok(GateSolid() == 30 && A.Logged.Any(l => l.Contains("stand on the eject point E outside it, facing the Hearth, and /arrival admin gate open force")),
            "no gate file: the cells are unknown, and the log says how to open it", string.Join("\n", A.Logged));
        var s = Mk(N1, "Ada", 11, 31);
        Ok(Cmd(s, "admin", "gate", "open", "force").Contains("Arrivals are off") && GateSolid() == 30, "a player without realmarrival.admin cannot");
        string r = Cmd(adm, "admin", "gate", "open", "force");
        Ok(r.Contains("no site file") && r.Contains("clear the portcullis by hand") && GateSolid() == 30, "no gate file and no site file: refused, by hand", r);
        Ok(Cmd(adm, "status").Contains("Arrivals are off") && Cmd(adm).Contains("/arrival admin gate open force"), "anything else: off, with staff's way out");
        Ok(!File.Exists(gf), "and the gate file is not written while the data file is unreadable");

        // With the site plan: stand on E outside the closed gate, facing the Hearth.
        for (int t = 0; t < 4; t += 3)
        {
            var pa = PlanWorld(t, true, true);
            var want = new List<int[]>();
            foreach (var row in SiteJson.GetProperty("cells").GetProperty("gate").GetProperty("rows").EnumerateArray())
                foreach (var cc in row.EnumerateArray()) want.Add(WCell(CellOf(cc)));
            Ok(want.All(k => Grid.Mat(k[0], k[1], k[2]) == 9), "the plan's gate closed (turn " + t + ")");
            File.Delete(gf);
            File.WriteAllText(df, "{ broken");
            int before = Grid.Cells.Count;
            Crash();
            Ok(want.All(k => Grid.Mat(k[0], k[1], k[2]) == 9), "after the crash, with neither file: still closed (turn " + t + ")");
            pa.Entity.Forward = Facings[t];
            string far = Cmd(pa, "admin", "gate", "open", "force");
            Ok(far.Contains("0 of 30") && want.All(k => Grid.Mat(k[0], k[1], k[2]) == 9) && Grid.Cells.Count == before,
                "standing anywhere else clears nothing (turn " + t + ")", far);
            var ev = WStand(PointCell("E"));
            At(pa, ev.x, ev.z, ev.y);
            string ok = Cmd(pa, "admin", "gate", "open", "force");
            Ok(ok.Contains("from the site plan (where you stand): 30 of 30") && want.All(k => Grid.Mat(k[0], k[1], k[2]) == 0) && Grid.Cells.Count == before - 30,
                "on E facing the Hearth: exactly the plan's 30 gate cells are cleared, nothing else (turn " + t + ")", ok);
        }
        // Data readable but its gate list lost (an older copy restored), the plan anchored: the stored anchor.
        var pb = PlanWorld(0, true, true);
        ((IList)F(SiteData(), "GateCells")).Clear();
        string viaAnchor = Cmd(pb, "admin", "gate", "open", "force");
        Ok(viaAnchor.Contains("from the site plan (the stored anchor): 30 of 30"), "a gate list lost but the plan anchored: the stored anchor", viaAnchor);

        // The gate file follows the data: re-made at a load that finds it missing, emptied by gate remove.
        adm = Site(true);
        File.Delete(gf);
        Reload();
        Ok(File.Exists(gf) && JsonDocument.Parse(File.ReadAllText(gf)).RootElement.GetProperty("Cells").GetArrayLength() == 30, "a load with the gate built writes a missing gate file");
        Cmd(adm, "admin", "gate", "remove");
        Ok(JsonDocument.Parse(File.ReadAllText(gf)).RootElement.GetProperty("Cells").GetArrayLength() == 0, "gate remove empties it");
        Site();
        Ok(!File.Exists(gf), "never created while no gate is built");
    }

    static readonly string[][] RefFields =
    {
        new[] { "RealmSentinel", "Sentinel" }, new[] { "RealmTravel", "Travel" }, new[] { "RealmWarden", "Warden" },
        new[] { "RealmHouses", "HousesP" }, new[] { "CrownAndConsequences", "Crown" }, new[] { "RealmEvents", "Events" },
        new[] { "RealmArena", "Arena" }, new[] { "RealmQuests", "QuestsP" }, new[] { "RealmRenown", "Renown" }, new[] { "RealmHerald", "Herald" },
    };

    // Loads the plugin on a config file with this text (written to the config folder, as Oxide keeps it).
    static string LoadOnConfig(string text)
    {
        string file = Path.Combine(CfgDir, "RealmArrival.json");
        File.WriteAllText(file, text);
        var p = new RealmArrival();
        p.Config.Json = text;
        p.Config.Filename = file;
        Inv(p, "LoadDefaultMessages");
        foreach (var r in RefFields) SetF(p, r[0], (Plugin)typeof(W).GetField(r[1]).GetValue(null));
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        A = p;
        Grid.PlaceHook = evt => Inv(A, "OnCubePlacement", evt);
        Inv(p, "OnServerInitialized");
        return file;
    }

    static void BrokenConfig()
    {
        Reset();
        string broken = "{ \"Enabled\": true, \"Open\": tru";
        string file = LoadOnConfig(broken);
        Ok((bool)F(A, "configFailed") && (bool)F(Cfg(), "Open") == false && (string)F(Cfg(), "RoutingMode") == "teleport", "a broken config: the defaults in memory (closed)");
        var errors = A.Logged.Where(l => l.StartsWith("ERROR") && l.Contains("RealmArrival.json")).ToList();
        Ok(errors.Count == 1 && errors[0].Contains("will NOT write") && errors[0].Contains("RealmArrival.json.broken-20261005-120000")
            && errors[0].Contains("/arrival admin config reset confirm"), "one clear error: not written, where the copy is, the way out", string.Join("\n", A.Logged));
        var copies = Directory.GetFiles(CfgDir, "RealmArrival.json.broken-*");
        Ok(copies.Length == 1 && Path.GetFileName(copies[0]) == "RealmArrival.json.broken-20261005-120000" && File.ReadAllText(copies[0]) == broken,
            "copied aside as RealmArrival.json.broken-<UTC time>, byte for byte");
        var adm = BuildSite(false);
        Ok(File.ReadAllText(file) == broken && A.Config.Writes == 0, "storing the site saves the data file, never the config");
        foreach (var c in new[] { new[] { "open" }, new[] { "open", "force" }, new[] { "close" }, new[] { "mode", "road" }, new[] { "gatemode", "portcullis" },
            new[] { "evict", "off" }, new[] { "wave", "on", "5" }, new[] { "wave", "off" } })
        {
            string reply = Cmd(adm, new[] { "admin" }.Concat(c).ToArray());
            Ok(reply.Contains("Refused: oxide/config/RealmArrival.json could not be read, so nothing is saved to it (copy: RealmArrival.json.broken-20261005-120000)"),
                "/arrival admin " + string.Join(" ", c) + " is refused while the config is broken", reply);
        }
        Ok(File.ReadAllText(file) == broken && A.Config.Writes == 0 && !(bool)F(Cfg(), "Open") && (string)F(Cfg(), "RoutingMode") == "teleport"
            && (bool)F(Cfg(), "Evict") && (string)F(Cfg(), "GateMode") == "open", "and nothing changed, in memory or on disk");
        Ok(Cmd(adm, "admin", "status").Contains("could not be read (") && Cmd(adm, "admin", "stone", "list").Contains("stone (6)"), "status says so; the other admin commands work");
        var n = Newcomer(N1, "Ada");
        Ok(n.Entity.Teleports.Count == 0 && ArrivalStage(n) == "none", "the arrival stays closed: vanilla spawns");
        Advance(61);
        Ok(File.ReadAllText(file) == broken && A.Config.Writes == 0, "the tick never writes it either");
        Ok(Cmd(adm, "admin", "config").Contains("could not be read"), "config says so");
        Ok(Cmd(adm, "admin", "config", "reset").Contains("Usage") && File.ReadAllText(file) == broken, "config reset without confirm: only the usage");
        // A reload on the same broken file: still refused, still never written.
        Reload();
        Ok((bool)F(A, "configFailed") && File.ReadAllText(file) == broken, "a reload on the broken file: the same");
        Ok(Directory.GetFiles(CfgDir, "RealmArrival.json.broken-*").Length == 1, "a later load on the same broken file uses the same copy (no pile of copies)");
        string reset = Cmd(adm, "admin", "config", "reset", "confirm");
        Ok(reset.Contains("config reset to the defaults (the arrival is closed); the broken file is kept as RealmArrival.json.broken-20261005-120000"), "config reset confirm", reset);
        var written = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Ok(written.GetProperty("Open").GetBoolean() == false && written.GetProperty("RoutingMode").GetString() == "teleport" && !(bool)F(A, "configFailed"),
            "the defaults are written over it; saves work again");
        Ok(File.ReadAllText(Path.Combine(CfgDir, "RealmArrival.json.broken-20261005-120000")) == broken, "the broken copy is kept");
        Ok(Cmd(adm, "admin", "mode", "road").Contains("Done: mode road") && JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("RoutingMode").GetString() == "road",
            "after the reset, admin commands save again");
        Ok(Cmd(adm, "admin", "config", "reset", "confirm").Contains("the config was read"), "a second reset is refused: nothing to reset");

        // An empty config file is broken too; a readable one is never copied.
        Reset();
        LoadOnConfig("");
        Ok((bool)F(A, "configFailed") && Directory.GetFiles(CfgDir, "RealmArrival.json.broken-*").Length == 1, "an empty config file: the same (copied, not written)");
        Reset();
        LoadOnConfig("{ \"Open\": false, \"LineGapSeconds\": 5 }");
        Ok(!(bool)F(A, "configFailed") && Directory.GetFiles(CfgDir, "RealmArrival.json.broken-*").Length == 0 && (int)F(Cfg(), "LineGapSeconds") == 5,
            "a readable config: read, never copied");
        var a2 = Mk(1, "Steward", 400, 400);
        Admin(a2);
        string ok2 = Cmd(a2, "admin", "config");
        Ok(ok2.Contains("was read") && !ok2.Contains("ERR"), "config on a readable file says it was read", ok2);
    }

    // The four fixes with no other Realm plugin loaded: nothing throws, nothing waits on them.
    static void FixesAlone()
    {
        string[] all = { "RealmQuests", "RealmWarden", "RealmHerald", "RealmSentinel", "RealmTravel", "RealmHouses", "CrownAndConsequences", "RealmEvents", "RealmArena", "RealmRenown" };
        Reset();
        foreach (var s in all) Absent.Add(s);
        NewArrival();
        var adm = BuildSite(false, true);
        Cmd(adm, "admin", "open", "force");
        Ok((bool)F(Cfg(), "Open"), "alone: the arrival opens by force", Cmd(adm, "admin", "check"));
        var w = Raw(N1, "Ada", 300, 300, 1);
        Reload();
        Ok(Stage(w) == "done" && ArrivalStage(w) == "none", "alone: an in-world player at load is resolved");
        Cmd(adm, "admin", "mercy", "clear");
        var n = ThroughGate(N2, "Bea");
        var e = Respawn(n, new PlayerRespawnNormalEvent());
        Advance(1);
        Ok(Dist(e.Position, 11, 31) < 0.1f && Stage(n) == "banners", "alone: a death with no mercy stone goes to E");
        Advance(20);
        File.WriteAllText(Path.Combine(Dir, "RealmArrival.json"), "{ broken");
        Crash();
        Ok(GateSolid() == 0, "alone: the gate opens after a crash from RealmArrival_gate.json");
        Ok(!A.Logged.Any(l => l.Contains("failed") && !l.Contains("could not be read")), "alone: no failures logged", string.Join("\n", A.Logged));
    }
}
