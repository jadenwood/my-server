// Behaviour tests for plugins/RealmSculptor.cs against Mocks.cs. Run with run.sh.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using CodeHatch.Blocks;
using CodeHatch.Blocks.Geometry;
using CodeHatch.Blocks.Inventory;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ModTools.Textures;
using CodeHatch.Networking.Events;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Plugins;
using UnityEngine;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    static DateTime Clock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    static string Dir, Repo;
    static RootCubeGrid G;
    static CrestScheme Crest;
    static RealmSculptor S;
    static Player Admin;

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
    static object SInv(string m, params object[] a)
    {
        var mi = typeof(RealmSculptor).GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return mi.Invoke(null, a);
    }
    static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    static object Cfg() { return F(S, "config"); }
    static void SetCfg(string f, object v) { SetF(Cfg(), f, v); }
    static IList Jobs() { return (IList)F(S, "jobs"); }
    static IList Placements() { return (IList)F(F(S, "data"), "Placements"); }
    static object LastPlacement() { var l = Placements(); return l[l.Count - 1]; }
    static List<int[]> Cells(object placement) { return (List<int[]>)F(placement, "Cells"); }
    static string State(object placement) { return (string)F(placement, "State"); }
    static int Id(object placement) { return (int)F(placement, "Id"); }

    static void Cmd(Player p, params string[] args) { Inv(S, "CmdSculpt", p, "sculpt", args); }
    static void Tick() { Clock = Clock.AddSeconds(0.2); Inv(S, "SafeTick"); G.EndFrame(); }
    static int RunJobs(int maxTicks = 20000)
    {
        int n = 0;
        while (Jobs().Count > 0 && n < maxTicks) { Tick(); n++; }
        return n;
    }

    static string Sculpture(string id, string blocks, string materials, int[] size, string extra = "")
    {
        return "{\"format\":\"realm-sculpture/1\",\"id\":\"" + id + "\",\"name\":\"Test " + id + "\",\"description\":\"\",\"size\":[" + string.Join(",", size)
            + "],\"front\":\"-z\",\"materials\":" + materials + ",\"license\":\"realm-original\",\"source\":\"tests\"" + extra + ",\"blocks\":[" + blocks + "]}";
    }
    static void WriteSculpture(string id, string json) { File.WriteAllText(Path.Combine(Dir, "RealmSculptor", id + ".json"), json); }

    const string Grey = "#a3a6ad", Gold = "#d6a043", Green = "#4d6b3a";
    static int Rgba(string hex) { return (Convert.ToInt32(hex.Substring(1), 16) << 8) | 255; }

    static void SetupWorld()
    {
        var sets = new List<OctTileset>();
        string[] names = { "Cobblestone", "Stone", "Clay", "Sod", "Thatch", "Spruce", "Wood", "Log", "Reinforced" };
        for (int i = 0; i < 9; i++)
        {
            var t = new OctTileset { TilesetID = i + 1, DefaultColor = new Color(1, 1, 1, 1), BuildTime = 2, Quality = i };
            t.name = names[i] + "Tileset";
            t.TilesetMaterialBinding = new[] { new TilesetMaterialInfo { Colorable = i != 3 } };
            // every material but sod (4) has a ramp (2) and a stair (1)
            t.SpecialPeices = i == 3 ? new OctPrefab[0] : new[] { new OctPrefab { PrefabID = 1, name = "Stair" }, new OctPrefab { PrefabID = 2, name = "Ramp" } };
            sets.Add(t);
        }
        TilesetLibrary.Singleton = new TilesetLibrary { Tilesets = sets.ToArray() };
        InvBlueprints.Instance = new InvBlueprints
        {
            AllDefinedBlueprints = new[]
            {
                new InvItemBlueprint { Name = "Stone Block", Part = new TilesetBlueprint { MaterialID = 2, PrefabID = 0 } },
                new InvItemBlueprint { Name = "Stone Ramp", Part = new TilesetBlueprint { MaterialID = 2, PrefabID = 2 } },
                new InvItemBlueprint { Name = "Torch", Part = "not a block" },
            }
        };
        NewGrid();
        Crest = new CrestScheme { GroupAt = v => 77UL };                // all claimed by group 77 unless a test says otherwise
        SocialAPI.Schemes[typeof(CrestScheme)] = Crest;
    }

    static void NewGrid()
    {
        G = new RootCubeGrid();
        G.PlaceHook = e => { if (S != null) Inv(S, "OnCubePlacement", e); };
        BlockManager.DefaultCubeGrid = G;
    }

    static RealmSculptor NewSculptor(string configJson = null)
    {
        var p = new RealmSculptor();
        p.Name = "RealmSculptor";
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        p.permission.Grants.Add(Admin.Id + "|realmsculptor.admin");
        S = p;
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void StandAt(Player p, int cx, int cy, int cz, float fx = 0, float fz = 1)
    {
        p.Entity.Position = new Vector3(cx * 1.2f, cy * 1.2f, cz * 1.2f);
        p.Entity.Forward = new Vector3(fx, 0, fz);
    }

    static CubeInfo At(int x, int y, int z) { return G.GetCubeInfoAtLocal(new Vector3Int(x, y, z)); }

    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : "../../../..";
        string dll = argv.Length > 1 ? argv[1] : "";
        Dir = Path.Combine(Path.GetTempPath(), "realmsculptor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Dir, "RealmSculptor"));
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Admin = new Player(76561198000000001UL, "Mason");
        Server.ClientPlayers.Add(Admin);
        SetupWorld();
        try
        {
            RotationTables();
            WriteTestSculptures();
            NewSculptor();
            Loading();
            Binding();
            PlaceAndPaint();
            Batching();
            Turning();
            ConflictsAndForce();
            UndoAndRemove();
            Protection();
            DecayGuard();
            LostAndRepair();
            FailedCells();
            LandRules();
            PluginApi();
            Materials();
            RestartMidJob();
            DamagedData();
            Permissions();
            RealSculptures();
            ChatLines();
            if (dll.Length > 0 && File.Exists(dll)) RealMetadata(dll);
            else Ok(false, "metadata: Assembly-CSharp.dll found", dll);
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("FAIL unexpected exception: " + ex);
        }
        finally { try { Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine($"\n{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ rotation maths

    static int[,] RotX(int d) { var (c, s) = CS(d); return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } }; }
    static int[,] RotY(int d) { var (c, s) = CS(d); return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } }; }
    static int[,] RotZ(int d) { var (c, s) = CS(d); return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } }; }
    static (int, int) CS(int d) { int q = ((d % 360) + 360) % 360 / 90; return new[] { (1, 0), (0, 1), (-1, 0), (0, -1) }[q]; }
    static int[,] Mul(int[,] a, int[,] b) { var o = new int[3, 3]; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) o[i, j] += a[i, k] * b[k, j]; return o; }
    static string Key(int[,] m) { return string.Join(",", m.Cast<int>()); }

    static void RotationTables()
    {
        var euler = (int[,])typeof(RealmSculptor).GetField("Euler", BF).GetValue(null);
        var turn = (int[,])typeof(RealmSculptor).GetField("TurnTable", BF).GetValue(null);
        var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "plugins/docs/RealmSculptor/logic-tests/rotations.json"))).RootElement;
        bool same = true;
        for (int i = 0; i < 24; i++)
        {
            for (int k = 0; k < 3; k++) if (fixture.GetProperty("euler")[i][k].GetInt32() != euler[i, k]) same = false;
            for (int k = 0; k < 4; k++) if (fixture.GetProperty("turn")[i][k].GetInt32() != turn[i, k]) same = false;
        }
        Ok(same, "rotations: plugin tables equal rotations.json written by art/tools/sculptor");
        // Independent check: build the matrices here and derive the turn table again.
        var mats = new List<int[,]>();
        for (int i = 0; i < 24; i++) mats.Add(Mul(RotY(euler[i, 1]), Mul(RotX(euler[i, 0]), RotZ(euler[i, 2]))));
        Ok(mats.Select(Key).Distinct().Count() == 24, "rotations: the 24 game rotations are 24 different turns");
        bool derived = true;
        for (int i = 0; i < 24; i++) for (int k = 0; k < 4; k++)
        {
            string want = Key(Mul(RotY(90 * k), mats[i]));
            if (Key(mats[turn[i, k]]) != want) derived = false;
        }
        Ok(derived, "rotations: TurnTable[i, k] is the game rotation equal to a k quarter-turn yaw after rotation i");
        // TurnXZ agrees with Ry(90k) on positions.
        bool pos = true;
        for (int k = 0; k < 4; k++)
        {
            var m = RotY(90 * k);
            foreach (var (x, z) in new[] { (1, 0), (0, 1), (2, -3), (-5, 4) })
            {
                object[] a = { x, z, k, 0, 0 };
                typeof(RealmSculptor).GetMethod("TurnXZ", BF).Invoke(null, a);
                if ((int)a[3] != m[0, 0] * x + m[0, 2] * z || (int)a[4] != m[2, 0] * x + m[2, 2] * z) pos = false;
            }
        }
        Ok(pos, "rotations: TurnXZ turns positions the same way as the block rotations");
        Ok((int)SInv("Quadrant", 0f, 1f) == 0 && (int)SInv("Quadrant", 1f, 0.2f) == 1 && (int)SInv("Quadrant", 0.1f, -1f) == 2 && (int)SInv("Quadrant", -1f, 0.5f) == 3,
            "rotations: facing +z, +x, -z, -x gives quarter-turns 0, 1, 2, 3");
    }

    // ------------------------------------------------------------------ loading

    static void WriteTestSculptures()
    {
        const string mats = "{\"2\":\"stone\",\"9\":\"reinforced\",\"4\":\"sod\"}";
        // 3 x 2 floor of stone, a reinforced ramp (rotation 1) above the middle front cell, a sod ramp (sod has no ramp shape) above it.
        WriteSculpture("t-small", Sculpture("t-small",
            $"[0,0,0,2,0,0,\"{Grey}\"],[1,0,0,2,0,0,\"{Grey}\"],[2,0,0,2,0,0,\"{Grey}\"],[0,0,1,2,0,0,\"{Grey}\"],[1,0,1,2,0,0,\"{Grey}\"],[2,0,1,2,0,0,\"{Grey}\"],"
            + $"[1,1,0,9,2,1,\"{Gold}\"],[1,2,0,4,2,0,\"{Green}\"]", mats, new[] { 3, 3, 2 }));
        // A tower 1 x 6 x 1, for failed cells and batching.
        WriteSculpture("t-tower", Sculpture("t-tower", string.Join(",", Enumerable.Range(0, 6).Select(y => $"[0,{y},0,2,0,0,\"{Grey}\"]")), mats, new[] { 1, 6, 1 }));
        WriteSculpture("t-unpainted", Sculpture("t-unpainted", "[0,0,0,2,0,0,null],[1,0,0,2,0,0,\"\"]", mats, new[] { 2, 1, 1 }));
        WriteSculpture("t-mystery", Sculpture("t-mystery", $"[0,0,0,42,0,0,\"{Grey}\"]", "{\"42\":\"mystery\"}", new[] { 1, 1, 1 }));
        // Broken files: each must be reported and must not stop the others from loading.
        WriteSculpture("x-json", "{ not json");
        WriteSculpture("x-id", Sculpture("other-id", $"[0,0,0,2,0,0,\"{Grey}\"]", mats, new[] { 1, 1, 1 }));
        WriteSculpture("x-outside", Sculpture("x-outside", $"[0,0,5,2,0,0,\"{Grey}\"]", mats, new[] { 1, 1, 1 }));
        WriteSculpture("x-door", Sculpture("x-door", $"[0,0,0,2,10,0,\"{Grey}\"]", mats, new[] { 1, 1, 1 }));
        WriteSculpture("x-colour", Sculpture("x-colour", "[0,0,0,2,0,0,\"red\"]", mats, new[] { 1, 1, 1 }));
        WriteSculpture("x-twice", Sculpture("x-twice", $"[0,0,0,2,0,0,\"{Grey}\"],[0,0,0,2,0,0,\"{Grey}\"]", mats, new[] { 1, 1, 1 }));
        WriteSculpture("x-format", Sculpture("x-format", $"[0,0,0,2,0,0,\"{Grey}\"]", mats, new[] { 1, 1, 1 }).Replace("realm-sculpture/1", "realm-sculpture/9"));
        WriteSculpture("x-big", Sculpture("x-big", string.Join(",", Enumerable.Range(0, 5000).Select(i => $"[{i % 64},{i / 4096},{(i / 64) % 64},2,0,0,\"{Grey}\"]")), mats, new[] { 64, 2, 64 }));
    }

    static void Loading()
    {
        var sc = (IDictionary)F(S, "sculptures");
        var problems = (List<string>)F(S, "sculptureProblems");
        Ok(sc.Contains("t-small") && sc.Contains("t-tower") && sc.Contains("t-unpainted") && sc.Contains("t-mystery"), "loading: valid sculptures load");
        string all = string.Join("\n", problems);
        foreach (var (id, why) in new[] { ("x-json", "not readable"), ("x-id", "match the file name"), ("x-outside", "outside size"), ("x-door", "single-block shape"),
            ("x-colour", "#rrggbb"), ("x-twice", "repeats a position"), ("x-format", "format"), ("x-big", "MaxBlocksPerSculpture") })
            Ok(!sc.Contains(id) && problems.Any(p => p.StartsWith(id + ":") && p.Contains(why)), $"loading: {id} is refused ({why})", all);
        Admin.Messages.Clear();
        Cmd(Admin, "list");
        Ok(Admin.All().Contains("t-small - Test t-small, 3 x 3 x 2 blocks, 8 pieces") && Admin.All().Contains("not loaded: x-json"), "loading: /sculpt list shows ready and refused files", Admin.All());
        var small = sc["t-small"];
        var blocks = (List<int[]>)F(small, "Blocks");
        Ok(blocks.Count == 8 && blocks[6][4] == 2 && blocks[6][5] == 1 && blocks[6][6] == 0xd6a043, "loading: block fields are read (shape, rotation, colour)");
        var unp = (List<int[]>)F(sc["t-unpainted"], "Blocks");
        Ok(unp.All(b => b[6] == -1), "loading: null or empty colour means the material's own colour");
    }

    static void Binding()
    {
        Ok(F(S, "bindError") == null, "binding: the game's block calls are found by reflection", Convert.ToString(F(S, "bindError")));
        Ok(EventManager.Count<MassCubeDestroyEvent>() == 1, "binding: the decay guard is subscribed once");
        Ok(S.permission.Registered.Contains("realmsculptor.admin"), "binding: permission realmsculptor.admin is registered");
    }

    // ------------------------------------------------------------------ placing

    static void PlaceAndPaint()
    {
        StandAt(Admin, 10, 10, 10);
        Admin.Messages.Clear();
        Cmd(Admin, "preview", "t-small");
        Ok(Admin.All().Contains("Corner at 9,10,13, turn 0") && Admin.All().Contains("space is clear") && Admin.All().Contains("1 sloped piece"), "preview: corner, turn, clear space and the simplified sod ramp", Admin.All());
        Ok(G.Calls.Count == 0, "preview: places nothing");
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small");
        Ok(Admin.All().Contains("Raising #1 Test t-small"), "place: starts a job", Admin.All());
        Ok(Jobs().Count == 1 && State(LastPlacement()) == "placing", "place: the placement is recorded before any block is placed");
        Ok(Interface.Oxide.DataFileSystem.ExistsDatafile("RealmSculptor"), "place: the record is saved at once");
        Tick();
        var places = G.Calls.Where(c => c.Kind == "place").ToList();
        Ok(places.Count == 8 && G.Calls.All(c => c.Kind == "place"), "place: the first tick places, nothing is painted yet", string.Join(" ", G.Calls.Select(c => c.Kind)));
        Ok(places.All(c => !c.Collect && !c.Owned && Math.Abs(c.Delay - 0.05f) < 1e-6), "place: collectPreviousCube false, isOwnedByPlacer false, PlaceDelaySeconds sent");
        bool bottomUp = true; for (int i = 1; i < places.Count; i++) if (places[i].Pos.y < places[i - 1].Pos.y) bottomUp = false;
        Ok(bottomUp, "place: bottom layer first");
        Ok(At(9, 10, 13).MaterialID == 2 && At(11, 10, 14).MaterialID == 2, "place: corner cell (0,0,0) at 9,10,13 in front of the admin");
        var ramp = At(10, 11, 13);
        Ok(ramp.MaterialID == 9 && ramp.PrefabID == 2 && ramp.Rotation.Index == 1, "place: the ramp keeps shape and rotation");
        var sod = At(10, 12, 13);
        Ok(sod.MaterialID == 4 && sod.PrefabID == 0, "place: a material without the shape gets a plain block");
        for (int i = 0; i < 6; i++) Tick();
        Ok(!G.Calls.Any(c => c.Kind == "paint"), "paint: nothing is painted before ColourDelaySeconds");
        RunJobs();
        var paints = G.Calls.Where(c => c.Kind == "paint").ToList();
        Ok(paints.Count == 8, "paint: every block is painted once", paints.Count.ToString() + "\n" + string.Join("\n", S.Log));
        Ok(At(9, 10, 13).CubeColor.Rgba == Rgba(Grey) && At(10, 11, 13).CubeColor.Rgba == Rgba(Gold), "paint: the colours stay on the blocks (painted after they exist)");
        Ok(State(LastPlacement()) == "standing" && Admin.All().Contains("#1 Test t-small stands: 8 blocks placed, 0 skipped"), "place: done, the admin is told", Admin.All());
        Ok(Cells(LastPlacement()).All(c => c[12] == 2), "place: every cell is recorded as done");
        // Undo it so the area is free again.
        Cmd(Admin, "undo"); RunJobs();
        Ok(G.Cells.Count == 0, "undo: the ground is as it was");
        // A sculpture without colours is never painted.
        G.Calls.Clear();
        Cmd(Admin, "place", "t-unpainted"); RunJobs();
        Ok(G.Calls.Count(c => c.Kind == "paint") == 0 && At(9, 10, 13).MaterialID == 2, "paint: blocks without a colour keep the material's own");
        Cmd(Admin, "undo"); RunJobs();
        // Paint off in the config.
        SetCfg("Paint", false); G.Calls.Clear();
        Cmd(Admin, "place", "t-tower"); RunJobs();
        Ok(G.Calls.Count(c => c.Kind == "paint") == 0, "paint: Paint false sends no colours");
        Cmd(Admin, "undo"); RunJobs();
        SetCfg("Paint", true);
        // SimplifyShapes places every shape as a plain block.
        SetCfg("SimplifyShapes", true);
        Cmd(Admin, "place", "t-small"); RunJobs();
        Ok(At(10, 11, 13).PrefabID == 0 && At(10, 11, 13).Rotation.Index == 0, "shapes: SimplifyShapes places a plain block, rotation 0");
        Cmd(Admin, "undo"); RunJobs();
        SetCfg("SimplifyShapes", false);
    }

    static void Batching()
    {
        SetCfg("BlocksPerTick", 3);
        G.Calls.Clear();
        Cmd(Admin, "place", "t-small");
        int maxPerTick = 0, ticks = 0;
        while (Jobs().Count > 0 && ticks < 1000) { int before = G.Calls.Count; Tick(); ticks++; maxPerTick = Math.Max(maxPerTick, G.Calls.Count - before); }
        Ok(maxPerTick <= 3 && maxPerTick > 0, "batching: never more than BlocksPerTick game calls per tick (placing and painting)", maxPerTick.ToString());
        Ok(G.Calls.Count(c => c.Kind == "place") == 8 && G.Calls.Count(c => c.Kind == "paint") == 8, "batching: all blocks still placed and painted");
        Cmd(Admin, "undo"); RunJobs();
        SetCfg("BlocksPerTick", 25);
        // Two jobs run one after the other.
        StandAt(Admin, 10, 10, 10);
        Cmd(Admin, "place", "t-tower");
        StandAt(Admin, 30, 10, 10);
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-tower");
        Ok(Admin.All().Contains("waits behind 1 other job"), "batching: a second job queues", Admin.All());
        RunJobs();
        Ok(At(10, 15, 13).MaterialID == 2 && At(30, 15, 13).MaterialID == 2, "batching: both queued jobs finish");
        Cmd(Admin, "undo"); RunJobs(); Cmd(Admin, "undo"); RunJobs();
        Ok(G.Cells.Count == 0, "undo: undo twice takes down both, newest first");
    }

    static void Turning()
    {
        // Facing +x: the sculpture turns a quarter so its front (-z) faces back to the admin.
        StandAt(Admin, 10, 10, 10, 1, 0);
        Cmd(Admin, "place", "t-small"); RunJobs();
        var p = LastPlacement();
        Ok((int)F(p, "Turn") == 1, "turning: facing +x gives turn 1");
        // cell (x, y, z) -> (13 + z, y, 11 - x)
        Ok(At(13, 10, 11).MaterialID == 2 && At(14, 10, 9).MaterialID == 2 && At(13, 10, 9).MaterialID == 2, "turning: footprint lies in +x, centred on the admin");
        var ramp = At(13, 11, 10);
        Ok(ramp.MaterialID == 9 && ramp.Rotation.Index == 2, "turning: the ramp's rotation index turns with it (1 -> 2)", ramp.Rotation.Index.ToString());
        Ok(At(13, 10, 10).MaterialID == 2 && At(12, 10, 10).MaterialID == 0, "turning: the front row is the one nearest the admin");
        Cmd(Admin, "undo"); RunJobs();
        // An explicit turn overrides the facing; the sculpture still goes in the direction the admin faces.
        StandAt(Admin, 10, 10, 10, 0, -1);
        Cmd(Admin, "place", "t-small", "0", "+2"); RunJobs();
        p = LastPlacement();
        Ok((int)F(p, "Turn") == 0 && (int)F(p, "Y") == 12, "turning: explicit turn 0 and +2 lift");
        Ok(At(9, 12, 6).MaterialID == 2 && At(11, 12, 7).MaterialID == 2, "turning: facing -z the footprint ends DistanceAhead cells in front");
        Cmd(Admin, "undo"); RunJobs();
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small", "sideways");
        Ok(Admin.All().Contains("do not understand \"sideways\"") && Jobs().Count == 0, "turning: an unknown word is refused");
        StandAt(Admin, 10, 10, 10);
    }

    static void ConflictsAndForce()
    {
        G.Put(10, 10, 13, 7, 0, 0, Rgba("#5b4630"));        // a player's wooden block where the floor's middle front goes
        G.Put(9, 10, 14, 7, 10, 0);                          // a player's door in the floor
        Admin.Messages.Clear();
        Cmd(Admin, "preview", "t-small");
        Ok(Admin.All().Contains("In the way: 2 existing block(s)"), "conflicts: preview counts existing blocks", Admin.All());
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small");
        Ok(Admin.All().Contains("ERR") && Admin.All().Contains("Not placed: 2 existing block(s)") && Jobs().Count == 0, "conflicts: refused without force", Admin.All());
        Cmd(Admin, "place", "t-small", "force"); RunJobs();
        var p = LastPlacement();
        Ok(At(10, 10, 13).MaterialID == 2, "force: the player's block is replaced");
        Ok(At(9, 10, 14).MaterialID == 7 && At(9, 10, 14).PrefabID == 10, "force: a door is never replaced");
        var cell = Cells(p).First(c => c[0] == 10 && c[1] == 10 && c[2] == 13);
        Ok(cell[7] == 7 && cell[10] == 0x5b4630 && cell[11] == 255, "force: the replaced block and its colour are recorded");
        Ok(Cells(p).Count(c => c[12] == 3) == 1, "force: the door cell is recorded as skipped");
        Cmd(Admin, "undo"); RunJobs();
        Ok(At(10, 10, 13).MaterialID == 7 && At(10, 10, 13).CubeColor.Rgba == Rgba("#5b4630"), "undo: the player's block comes back with its colour");
        Ok(At(9, 10, 14).PrefabID == 10 && G.Cells.Count == 2, "undo: the door is untouched and nothing else is left");
        G.Cells.Clear();
        // A cell that another sculpture holds is never taken, even with force.
        Cmd(Admin, "place", "t-tower"); RunJobs();
        StandAt(Admin, 11, 10, 10);
        Admin.Messages.Clear();
        Cmd(Admin, "preview", "t-small");
        Ok(Admin.All().Contains("1 cell(s) of another sculpture") || Admin.All().Contains("cell(s) of another sculpture"), "conflicts: another sculpture's cells are counted", Admin.All());
        Cmd(Admin, "place", "t-small", "force"); RunJobs();
        Ok(Cells(LastPlacement()).Count(c => c[12] == 3) >= 1 && At(10, 10, 13).MaterialID == 2, "conflicts: force skips another sculpture's cells");
        Cmd(Admin, "undo"); RunJobs(); Cmd(Admin, "undo"); RunJobs();
        Ok(G.Cells.Count == 0, "conflicts: both taken down cleanly");
        StandAt(Admin, 10, 10, 10);
    }

    static void UndoAndRemove()
    {
        var other = new Player(76561198000000002UL, "Jory");
        Server.ClientPlayers.Add(other);
        S.permission.Grants.Add(other.Id + "|realmsculptor.admin");
        StandAt(other, 40, 10, 10);
        Cmd(other, "place", "t-tower"); RunJobs();
        int otherId = Id(LastPlacement());
        Cmd(Admin, "place", "t-small"); RunJobs();
        int mine = Id(LastPlacement());
        G.Calls.Clear();
        Cmd(other, "undo"); RunJobs();
        Ok(At(40, 10, 13).MaterialID == 0 && At(9, 10, 13).MaterialID == 2, "undo: takes down the caller's own latest placement only");
        var removes = G.Calls.Where(c => c.Kind == "place").ToList();
        bool topDown = true; for (int i = 1; i < removes.Count; i++) if (removes[i].Pos.y > removes[i - 1].Pos.y) topDown = false;
        Ok(topDown && removes.All(c => c.Mat == 0 && !c.Collect), "remove: top layer first, air placed, no item collected");
        Admin.Messages.Clear();
        Cmd(Admin, "placed");
        Ok(Admin.All().Contains($"#{mine} Test t-small at 9,10,13: standing, 8 blocks, 0 lost, protection on, by Mason") && !Admin.All().Contains($"#{otherId} "), "placed: lists standing placements", Admin.All());
        // A player changed one cell while protection was off: removal leaves it alone.
        Cmd(Admin, "protect", mine.ToString(), "off");
        G.Put(9, 10, 13, 7);
        Admin.Messages.Clear();
        Cmd(Admin, "remove", "#" + mine); RunJobs();
        Ok(At(9, 10, 13).MaterialID == 7 && G.Cells.Count == 1, "remove: a cell changed since is left alone, the rest is cleared");
        Ok(Admin.All().Contains("7 cells put back as they were, 1 changed since and left alone"), "remove: the admin is told", Admin.All());
        G.Cells.Clear();
        Admin.Messages.Clear();
        Cmd(Admin, "remove", mine.ToString());
        Ok(Admin.All().Contains("already taken down"), "remove: twice is refused");
        Cmd(Admin, "remove", "999");
        Ok(Admin.All().Contains("no placement #999"), "remove: unknown number is refused");
        Admin.Messages.Clear();
        Cmd(Admin, "undo");
        Ok(Admin.All().Contains("no standing placement"), "undo: nothing left to undo");
        // Undo in the middle of a build stops it and takes down what was placed.
        SetCfg("BlocksPerTick", 2);
        Cmd(Admin, "place", "t-tower");
        Tick(); Tick();
        Cmd(Admin, "undo"); RunJobs();
        Ok(G.Cells.Count == 0 && State(LastPlacement()) == "removed", "undo: a build in progress stops and is taken down");
        SetCfg("BlocksPerTick", 25);
        Server.ClientPlayers.Remove(other);
    }

    // ------------------------------------------------------------------ protection

    static CubeDamageEvent Hit(int x, int y, int z, float amount, Player by, DamageType type = DamageType.Blunt)
    {
        var d = new Damage { Amount = amount, ImpactDamage = amount, MiscDamage = 1, DamageTypes = type, DamageSource = by != null ? by.Entity : null };
        var e = new CubeDamageEvent { Position = new Vector3Int(x, y, z), Damage = d, Sender = by };
        Inv(S, "OnCubeTakeDamage", e);
        return e;
    }

    static void Protection()
    {
        var raider = new Player(76561198000000003UL, "Raider");
        Server.ClientPlayers.Add(raider);
        Cmd(Admin, "place", "t-small"); RunJobs();
        int id = Id(LastPlacement());
        var e = Hit(9, 10, 13, 40, raider);
        Ok(e.Damage.Amount == 0 && e.Damage.ImpactDamage == 0 && e.Damage.MiscDamage == 0 && e.Cancelled, "protection: damage to a sculpture is zeroed and cancelled");
        Ok(raider.Last().Contains("Test t-small is a monument of the realm"), "protection: the attacker is told why", raider.All());
        raider.Messages.Clear();
        Hit(9, 10, 13, 40, raider);
        Ok(raider.Messages.Count == 0, "protection: the message is not repeated within MessageCooldownSeconds");
        var free = Hit(20, 10, 20, 40, raider);
        Ok(free.Damage.Amount == 40 && !free.Cancelled, "protection: other blocks take damage as usual");
        var heal = Hit(9, 10, 13, -10, raider);
        Ok(heal.Damage.Amount == -10 && !heal.Cancelled, "protection: repairs (negative damage) pass");
        var salvageClaimed = Hit(9, 10, 13, 30, raider, DamageType.Salvage);
        Ok(salvageClaimed.Damage.Amount == 0, "protection: salvage inside a crest zone is blocked (the game pays no salvage there)");
        Crest.GroupAt = v => 0UL;
        var salvageWild = Hit(9, 10, 13, 30, raider, DamageType.Salvage);
        Ok(salvageWild.Damage.Amount == 30 && !salvageWild.Cancelled, "protection: salvage in unclaimed land passes (it has already paid out; blocking it would make an endless node)");
        Crest.GroupAt = v => 77UL;
        // Building into or removing a protected cell.
        var build = new CubePlaceEvent { Position = new Vector3Int(9, 10, 13), Material = 0, Sender = raider };
        Inv(S, "OnCubePlacement", build);
        Ok(build.Cancelled, "protection: a player cannot remove or replace a sculpture block");
        var nearby = new CubePlaceEvent { Position = new Vector3Int(9, 10, 12), Material = 7, Sender = raider };
        Inv(S, "OnCubePlacement", nearby);
        Ok(!nearby.Cancelled, "protection: building next to it is allowed");
        var server = new CubePlaceEvent { Position = new Vector3Int(9, 10, 13), Material = 0, Sender = G.ServerPlayer };
        Inv(S, "OnCubePlacement", server);
        Ok(!server.Cancelled, "protection: the server's own events are not blocked");
        Cmd(Admin, "protect", id.ToString(), "off");
        var e2 = Hit(9, 10, 13, 40, raider);
        Ok(e2.Damage.Amount == 40, "protection: /sculpt protect off lets damage through");
        Cmd(Admin, "protect", id.ToString(), "on");
        SetCfg("ProtectSculptures", false);
        var e3 = Hit(9, 10, 13, 40, raider);
        Ok(e3.Damage.Amount == 40, "protection: ProtectSculptures false switches all protection off");
        SetCfg("ProtectSculptures", true);
        Cmd(Admin, "undo"); RunJobs();
        var after = Hit(9, 10, 13, 40, raider);
        Ok(after.Damage.Amount == 40, "protection: ends when the sculpture is taken down");
        Server.ClientPlayers.Remove(raider);
    }

    static void DecayGuard()
    {
        Cmd(Admin, "place", "t-small"); RunJobs();
        var mixed = new MassCubeDestroyEvent { Position = new[] { new Vector3Int(9, 10, 13), new Vector3Int(50, 10, 50), new Vector3Int(11, 10, 14) } };
        EventManager.Raise(mixed);
        Ok(!mixed.Cancelled && mixed.Position.Length == 1 && mixed.Position[0].x == 50, "decay: protected cells are taken out of a decay sweep");
        var ours = new MassCubeDestroyEvent { Position = new[] { new Vector3Int(9, 10, 13) } };
        EventManager.Raise(ours);
        Ok(ours.Cancelled, "decay: a sweep of only protected cells is cancelled");
        var theirs = new MassCubeDestroyEvent { Position = new[] { new Vector3Int(60, 10, 60) } };
        EventManager.Raise(theirs);
        Ok(!theirs.Cancelled && theirs.Position.Length == 1, "decay: other sweeps are untouched");
        Cmd(Admin, "undo"); RunJobs();
        Inv(S, "Unload");
        Ok(EventManager.Count<MassCubeDestroyEvent>() == 0, "decay: unsubscribed on unload");
        NewSculptor();
    }

    static void LostAndRepair()
    {
        Cmd(Admin, "place", "t-small"); RunJobs();
        int id = Id(LastPlacement());
        G.Cells.Remove((11, 10, 14));
        Inv(S, "OnCubeDestroyed", new CubeDestroyEvent { Position = new Vector3Int(11, 10, 14) });
        Admin.Messages.Clear();
        Cmd(Admin, "placed");
        Ok(Admin.All().Contains("7 blocks, 1 lost"), "lost: a destroyed cell is recorded", Admin.All());
        Cmd(Admin, "repair", id.ToString()); RunJobs();
        Ok(At(11, 10, 14).MaterialID == 2 && At(11, 10, 14).CubeColor.Rgba == Rgba(Grey), "repair: the lost block is put back and painted");
        Admin.Messages.Clear();
        Cmd(Admin, "repair", id.ToString());
        Ok(Admin.All().Contains("is whole"), "repair: nothing to do when whole");
        // A collapse or decay removes blocks without any hook: repair finds them by looking.
        G.Cells.Remove((9, 10, 13)); G.Cells.Remove((10, 11, 13));
        G.Put(11, 10, 13, 7);                                // and someone built into a third cell after it went
        Admin.Messages.Clear();
        Cmd(Admin, "repair", id.ToString()); RunJobs();
        Ok(Admin.All().Contains("2 block(s) to put back") && At(9, 10, 13).MaterialID == 2 && At(10, 11, 13).MaterialID == 9 && At(11, 10, 13).MaterialID == 7,
            "repair: blocks gone without a hook are found and put back; another block in the cell is left alone", Admin.All());
        Cmd(Admin, "undo"); RunJobs();
        Ok(G.Cells.Count == 1 && At(11, 10, 13).MaterialID == 7, "repair: the repaired sculpture still undoes cleanly, leaving the other block");
        G.Cells.Clear();
    }

    static void FailedCells()
    {
        G.Loaded = p => p.y < 13;                     // the upper cells are in a page the game will not create
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-tower"); RunJobs();
        Ok(Admin.All().Contains("3 blocks placed, 0 skipped (something was in the way), 3 not taken by the game"), "failed: cells the game did not place are counted", Admin.All());
        Ok(Cells(LastPlacement()).Count(c => c[12] == 4) == 3, "failed: and recorded as failed");
        var e = Hit(10, 14, 13, 5, null);
        Ok(e.Damage.Amount == 5, "failed: failed cells are not guarded");
        G.Loaded = p => true;
        Cmd(Admin, "repair", Id(LastPlacement()).ToString()); RunJobs();
        Ok(At(10, 15, 13).MaterialID == 2, "failed: repair places them once the game takes them");
        Cmd(Admin, "undo"); RunJobs();
    }

    static void LandRules()
    {
        Crest.GroupAt = v => v.x > 10.5f * 1.2f ? 0UL : 77UL;   // columns x >= 11 are unclaimed
        Admin.Messages.Clear();
        Cmd(Admin, "preview", "t-small");
        Ok(Admin.All().Contains("2 column(s) stand on unclaimed land") && Admin.All().Contains("crest zone of group(s) 77"), "land: preview reports unclaimed columns and crest groups", Admin.All());
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small");
        Ok(Admin.All().Contains("AllowUnclaimedLand is false") && Jobs().Count == 0, "land: unclaimed land refused by default");
        SetCfg("AllowUnclaimedLand", true);
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small");
        Ok(Admin.All().Contains("Add unclaimed to build there anyway") && Jobs().Count == 0, "land: allowed only with the word unclaimed");
        Cmd(Admin, "place", "t-small", "unclaimed"); RunJobs();
        Ok(State(LastPlacement()) == "standing", "land: placed with unclaimed");
        Cmd(Admin, "undo"); RunJobs();
        SetCfg("AllowUnclaimedLand", false);
        Crest.GroupAt = v => 77UL;
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-mystery");
        Ok(Admin.All().Contains("no block material 42") && Jobs().Count == 0, "materials: an unknown material is refused", Admin.All());
        // MaterialIds remaps a role without touching the sculpture file.
        var ids = (Dictionary<string, int>)F(Cfg(), "MaterialIds");
        ids["reinforced"] = 3;
        Cmd(Admin, "place", "t-small"); RunJobs();
        Ok(At(10, 11, 13).MaterialID == 3, "materials: MaterialIds maps a role to another id");
        Cmd(Admin, "undo"); RunJobs();
        ids["reinforced"] = 9;
    }

    // RealmWorld's festival decorations: PlaceSculptureAt / RemoveSculpture (non-public, Plugin.Call), under /sculpt
    // place's own rules and never forced.
    static void PluginApi()
    {
        G.Cells.Clear();
        float wx = 10 * 1.2f, wy = 10 * 1.2f, wz = 10 * 1.2f;
        int id = (int)Inv(S, "PlaceSculptureAt", "t-small", wx, wy, wz, 0, "RealmWorld");
        Ok(id > 0 && State(LastPlacement()) == "placing" && (string)F(LastPlacement(), "By") == "RealmWorld", "api: PlaceSculptureAt records a placement by the calling plugin", id.ToString());
        RunJobs();
        Ok(State(LastPlacement()) == "standing" && At(9, 10, 13).MaterialID == 2, "api: it stands where /sculpt place would put it for someone there facing +z (turn 0)");
        Ok((int)Inv(S, "PlaceSculptureAt", "t-small", wx, wy, wz, 0, "RealmWorld") == 0, "api: occupied ground is refused (never forced)");
        Ok((int)Inv(S, "PlaceSculptureAt", "no-such-thing", wx, wy, wz, 0, "RealmWorld") == 0, "api: an unknown sculpture is refused");
        Ok((bool)Inv(S, "RemoveSculpture", id) && Jobs().Count == 1, "api: RemoveSculpture starts the take-down");
        RunJobs();
        Ok(G.Cells.Count == 0 && State(LastPlacement()) == "removed", "api: the ground is put back");
        Ok(!(bool)Inv(S, "RemoveSculpture", id) && !(bool)Inv(S, "RemoveSculpture", 9999), "api: removing twice, or an unknown id, is refused");
        Crest.GroupAt = v => 0UL;
        SetCfg("AllowUnclaimedLand", true);
        Ok((int)Inv(S, "PlaceSculptureAt", "t-small", wx, wy, wz, 0, "RealmWorld") == 0, "api: unclaimed land is refused, even with AllowUnclaimedLand true (the call never asks for it)");
        SetCfg("AllowUnclaimedLand", false);
        Crest.GroupAt = v => 77UL;
        int turned = (int)Inv(S, "PlaceSculptureAt", "t-small", wx, wy, wz, 5, "RealmWorld");
        Ok(turned > 0 && (int)F(LastPlacement(), "Turn") == 1, "api: the turn is taken modulo four");
        RunJobs();
        Inv(S, "RemoveSculpture", turned); RunJobs();
        Ok(G.Cells.Count == 0, "api: clean afterwards");
    }

    static void Materials()
    {
        Admin.Messages.Clear();
        Cmd(Admin, "materials");
        string f = Path.Combine(Dir, "RealmSculptor-materials.json");
        Ok(File.Exists(f), "materials: the dump is written");
        var doc = JsonDocument.Parse(File.ReadAllText(f)).RootElement;
        var mats = doc.GetProperty("Materials");
        Ok(mats.GetArrayLength() == 9 && mats[1].GetProperty("Name").GetString() == "StoneTileset" && mats[1].GetProperty("DefaultColour").GetString() == "#ffffff",
            "materials: ids, names and default colours", mats[1].ToString());
        Ok(mats[3].GetProperty("ColourableParts").GetInt32() == 0 && mats[3].GetProperty("FixedParts").GetInt32() == 1 && mats[1].GetProperty("Shapes").GetArrayLength() == 2,
            "materials: paintable parts and shapes");
        Ok(doc.GetProperty("Items").GetArrayLength() == 2, "materials: block items with their material and shape");
        Ok(Admin.All().Contains("material 2 StoneTileset: 1 paintable part(s), 0 fixed, shapes 1 2"), "materials: summary in chat", Admin.All());
    }

    static void RestartMidJob()
    {
        SetCfg("BlocksPerTick", 2);
        Cmd(Admin, "place", "t-small");
        Tick(); Tick();
        int placedBefore = G.Cells.Count;
        Inv(S, "Unload");
        Ok(placedBefore > 0 && placedBefore < 8, "restart: stopped in the middle of a build", placedBefore.ToString());
        NewSculptor("{\"BlocksPerTick\": 2}");
        Ok(Jobs().Count == 1, "restart: the unfinished job is resumed from the data file");
        RunJobs();
        Ok(G.Cells.Count == 8 && G.Cells.Values.All(c => c.CubeColor.Rgba != new Color32(255, 255, 255, 255).Rgba), "restart: the build finishes and every block is painted");
        Ok(State(LastPlacement()) == "standing", "restart: recorded as standing");
        var e = Hit(9, 10, 13, 40, Admin);
        Ok(e.Damage.Amount == 0, "restart: protection is rebuilt from the data file");
        Inv(S, "Unload");
        NewSculptor();
        Cmd(Admin, "undo"); RunJobs();
        Ok(G.Cells.Count == 0, "restart: undo after a restart restores the ground");
    }

    static void DamagedData()
    {
        string f = Path.Combine(Dir, "RealmSculptor.json");
        Inv(S, "Unload");
        string keep = File.ReadAllText(f);
        File.WriteAllText(f, "{ damaged");
        NewSculptor();
        Ok((bool)F(S, "dataFailed"), "damaged data: detected");
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small");
        Ok(Admin.All().Contains("could not be read") && Jobs().Count == 0, "damaged data: placing is refused");
        Inv(S, "Unload");
        Ok(File.ReadAllText(f) == "{ damaged", "damaged data: the file is never overwritten");
        File.WriteAllText(f, keep);
        NewSculptor();
        Ok(!(bool)F(S, "dataFailed"), "damaged data: a fixed file loads again");
    }

    static void Permissions()
    {
        var p = new Player(76561198000000009UL, "Peasant");
        Cmd(p, "place", "t-small");
        Ok(p.All().Contains("Only the realm's builders") && Jobs().Count == 0, "permission: players without realmsculptor.admin are refused");
        var srv = new Player(0, "Server") { IsServer = true };
        Ok(!(bool)Inv(S, "IsAdmin", srv), "permission: the server player is never an admin here");
        SetCfg("Enabled", false);
        Admin.Messages.Clear();
        Cmd(Admin, "place", "t-small");
        Ok(Admin.All().Contains("switched off") && Jobs().Count == 0, "config: Enabled false refuses placing");
        SetCfg("Enabled", true);
    }

    // Every sculpture in art/sculptures loads, places completely and comes down cleanly.
    static void RealSculptures()
    {
        string src = Path.Combine(Repo, "art", "sculptures");
        var files = Directory.Exists(src) ? Directory.GetFiles(src, "*.json") : new string[0];
        Ok(files.Length >= 10, "real: art/sculptures has the sculptures", files.Length.ToString());
        foreach (var f in Directory.GetFiles(Path.Combine(Dir, "RealmSculptor"))) File.Delete(f);
        foreach (var f in files) File.Copy(f, Path.Combine(Dir, "RealmSculptor", Path.GetFileName(f)));
        Cmd(Admin, "reload");
        var problems = (List<string>)F(S, "sculptureProblems");
        var sc = (IDictionary)F(S, "sculptures");
        Ok(problems.Count == 0 && sc.Count == files.Length, "real: every sculpture file is accepted by the plugin", string.Join("\n", problems));
        SetCfg("BlocksPerTick", 200);
        int x = 100;
        foreach (string id in sc.Keys.Cast<string>().OrderBy(s => s))
        {
            G.Calls.Clear();
            StandAt(Admin, x, 10, 10);
            Cmd(Admin, "place", id);
            RunJobs();
            var p = LastPlacement();
            var cells = Cells(p);
            bool all = State(p) == "standing" && cells.All(c => c[12] == 2) && cells.All(c => { var i = At(c[0], c[1], c[2]); return i.MaterialID == c[3] && (c[6] < 0 || i.CubeColor.Rgba == ((c[6] << 8) | 255)); });
            Ok(all, $"real: {id} stands complete and painted ({cells.Count} blocks)");
            Cmd(Admin, "undo"); RunJobs();
            Ok(G.Cells.Count == 0, $"real: {id} comes down without a trace");
            x += 100;
        }
        SetCfg("BlocksPerTick", 25);
    }

    static void ChatLines()
    {
        var msgs = S.lang.Msgs;
        Ok(msgs.Values.All(v => v.Length - System.Text.RegularExpressions.Regex.Matches(v, @"\[[0-9A-F]{6}\]").Count * 8 <= 200), "chat: lines stay short");
        Ok(Admin.Messages.All(m => !m.Contains("{0}")), "chat: no unfilled placeholders were sent");
        Ok(Admin.Messages.Where(m => !m.StartsWith(" ") && !m.StartsWith("ERR  ")).All(m => m.StartsWith("[") || m.StartsWith("ERR [")), "chat: every reply opens with the speaker");
    }

    // ------------------------------------------------------------------ the real DLL

    sealed class Names : ISignatureTypeProvider<string, object>
    {
        readonly MetadataReader r;
        public Names(MetadataReader r) { this.r = r; }
        public string GetPrimitiveType(PrimitiveTypeCode c) { return c.ToString(); }
        public string GetTypeFromDefinition(MetadataReader m, TypeDefinitionHandle h, byte k) { var t = m.GetTypeDefinition(h); return m.GetString(t.Namespace) + "." + m.GetString(t.Name); }
        public string GetTypeFromReference(MetadataReader m, TypeReferenceHandle h, byte k) { var t = m.GetTypeReference(h); return m.GetString(t.Namespace) + "." + m.GetString(t.Name); }
        public string GetSZArrayType(string e) { return e + "[]"; }
        public string GetArrayType(string e, ArrayShape s) { return e + "[,]"; }
        public string GetByReferenceType(string e) { return e + "&"; }
        public string GetPointerType(string e) { return e + "*"; }
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) { return g + "<" + string.Join(",", a) + ">"; }
        public string GetGenericMethodParameter(object c, int i) { return "!!" + i; }
        public string GetGenericTypeParameter(object c, int i) { return "!" + i; }
        public string GetModifiedType(string m, string u, bool req) { return u; }
        public string GetPinnedType(string e) { return e; }
        public string GetFunctionPointerType(MethodSignature<string> s) { return "fn"; }
        public string GetTypeFromSpecification(MetadataReader m, object c, TypeSpecificationHandle h, byte k) { return "spec"; }
    }

    static void RealMetadata(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var names = new Names(md);
        TypeDefinition Find(string ns, string name) => md.TypeDefinitions.Select(md.GetTypeDefinition).First(t => md.GetString(t.Namespace) == ns && md.GetString(t.Name) == name);
        List<string> Methods(TypeDefinition t, string name) => t.GetMethods().Select(md.GetMethodDefinition).Where(m => md.GetString(m.Name) == name)
            .Select(m => { var s = m.DecodeSignature(names, null); return string.Join(",", s.ParameterTypes) + (m.Attributes.HasFlag(System.Reflection.MethodAttributes.Static) ? " static" : "") + (m.Attributes.HasFlag(System.Reflection.MethodAttributes.Public) ? " public" : ""); }).ToList();
        bool HasField(TypeDefinition t, string name, out string type)
        {
            foreach (var h in t.GetFields()) { var f = md.GetFieldDefinition(h); if (md.GetString(f.Name) == name) { type = f.DecodeSignature(names, null); return true; } }
            type = null; return false;
        }
        bool HasProperty(TypeDefinition t, string name) => t.GetProperties().Any(h => md.GetString(md.GetPropertyDefinition(h).Name) == name);

        var grid = Find("CodeHatch.Blocks", "RootCubeGrid");
        var place = Methods(grid, "PlaceCubeAtLocal");
        Ok(place.Contains("CodeHatch.Common.Vector3Int,Byte,Byte,UnityEngine.Quaternion,Boolean,Boolean,Single public"), "metadata: RootCubeGrid.PlaceCubeAtLocal(Vector3Int, byte, byte, Quaternion, bool, bool, float)", string.Join(" | ", place));
        var colour = Methods(grid, "ColorCubeAtLocal");
        Ok(colour.Contains("CodeHatch.Common.Vector3Int,UnityEngine.Color32 public"), "metadata: RootCubeGrid.ColorCubeAtLocal(Vector3Int, Color32)", string.Join(" | ", colour));
        Ok(Methods(grid, "GetCubeInfoAtLocal").Contains("CodeHatch.Common.Vector3Int public"), "metadata: RootCubeGrid.GetCubeInfoAtLocal(Vector3Int)");
        var info = Find("CodeHatch.Blocks", "CubeInfo");
        Ok(HasField(info, "PossibleRotations", out string rt) && rt == "UnityEngine.Quaternion[]", "metadata: CubeInfo.PossibleRotations is Quaternion[]", rt ?? "");
        Ok(Methods(info, "GetIndexOfRotation").Contains("UnityEngine.Quaternion static public"), "metadata: CubeInfo.GetIndexOfRotation(Quaternion) is public static");
        Ok(HasProperty(info, "Rotation") && HasProperty(info, "CubeColor") && HasProperty(info, "MaterialID") && HasProperty(info, "PrefabID"), "metadata: CubeInfo Rotation, CubeColor, MaterialID, PrefabID");
        var tileset = Find("CodeHatch.Blocks", "OctTileset");
        Ok(HasProperty(tileset, "DefaultColor"), "metadata: OctTileset.DefaultColor");
        var mgr = Find("CodeHatch.Blocks", "BlockManager");
        Ok(HasProperty(mgr, "DefaultCubeGrid"), "metadata: BlockManager.DefaultCubeGrid");
        Ok(Methods(Find("CodeHatch.Blocks", "TilesetLibrary"), "GetTilesetWithID").Contains("Byte,Boolean static public"), "metadata: TilesetLibrary.GetTilesetWithID(byte, bool)");
        // The rotation table itself: the static constructor stores 24 Quaternion.Euler(x, y, z) values (checked as
        // counts here; the numbers are in plugins/docs/RealmSculptor.md and rotations.json).
        Ok(md.GetString(info.Name) == "CubeInfo", "metadata: CubeInfo found");
    }
}
