// RealmSculptor: Realm's own monuments inside the real game, built from the game's building blocks. The server places
// and paints ordinary blocks, which the game already sends to every client and saves with the world, so nobody's game
// install changes. Sculptures are made by art/tools/sculptor (Node) and copied to oxide/data/RealmSculptor/<id>.json.
//
// Commands (admins, permission realmsculptor.admin; see plugins/docs/RealmSculptor.md):
//   /sculpt list | preview <id> [turn] | place <id> [turn] [force] [unclaimed] [+N|-N] | undo | remove <n> | placed
//   /sculpt status | protect <n> on|off | repair <n> | materials | reload
// A placement is built a few blocks per tick (BlocksPerTick every TickSeconds), bottom layer first, then painted
// ColourDelaySeconds later. Every placed cell and the block that was there before is recorded in
// oxide/data/RealmSculptor.json, so undo and remove put the ground back exactly, even after a restart.
//
// Game API used (all [CODE] = read in the decompiled 2.0.3867 Assembly-CSharp.dll; plugins/docs/RealmSculptor.md has
// the details and what is still UNVERIFIED):
//   Grid      BlockManager.DefaultCubeGrid (GetGrid(0), the world's block grid). RootCubeGrid.GetCubeInfoAtLocal,
//             PlaceCubeAtLocal(Vector3Int, byte material, byte prefab, Quaternion, bool collectPreviousCube,
//             bool isOwnedByPlacer, float delayTime), ColorCubeAtLocal(Vector3Int, Color32), WorldToLocalCoordinate.
//             The game's own /build command (BlockCommandHandler.Build) places blocks the same way on the server.
//             collectPreviousCube = false: no item is handed out for a replaced block, and the place event is marked
//             CausedByDestruction, so clients skip the build scaffold (ProgressCubeListener). The server needs no
//             item: InventoryUtil.RemoveTileset returns player.IsServer for the server's own events.
//   Rotation  CubeInfo.PossibleRotations (24 quaternions). A block keeps its rotation as an index into that table.
//   Colour    A colour set before the block exists is lost: CubeInfo.UpdateMaterial resets CubeColor to the
//             tileset's DefaultColor. The server applies a placement at the end of the frame (CubeListener.
//             DelayedFinalize) and a client after the event's delay (CubeListener.PlaceCubeDelayed), so blocks are
//             painted ColourDelaySeconds later. NormalBlockLoader and BlockPrefabLoader save the colour with the world.
//   Shapes    TilesetLibrary.GetTilesetWithID(material).GetPrefabWithID(prefab); a material without the shape gets a
//             plain block instead (placing a missing shape would fail inside the game's code).
//   Land      CrestScheme.IsEmpty(world) / CurrentCrestGroup(world). Unclaimed land has decay (DecaySystem.CleanBlocks
//             sends MassCubeDestroyEvent) and salvage (SalvageSupplier pays out before any plugin hook sees the hit),
//             so sculptures go inside a crest zone unless AllowUnclaimedLand is true.
//   Unity     Quaternion and Color32 are reached by reflection from the methods above, because the compile check's
//             UnityEngine stub has only Vector3. The shipped Oxide.CSharp.dll has no namespace sandbox (it only
//             blacklists three assemblies), so System.Reflection is available. BindGame() reports what it could not find.
// Hooks: OnCubeTakeDamage(CubeDamageEvent), OnCubePlacement(CubePlaceEvent), OnCubeDestroyed(CubeDestroyEvent) (called
// by CubeListener before it acts; the game reads Damage.Amount and Cancelled afterwards, as RealmWarden documents), and
// EventManager.Subscribe<MassCubeDestroyEvent> (VeryEarly) to keep decay off protected cells.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmSculptor.md for the first-test plan.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CodeHatch.Blocks;                          // BlockManager, RootCubeGrid, CubeInfo, TilesetLibrary, OctTileset [CODE]
using CodeHatch.Blocks.Geometry;                 // OctPrefab [CODE]
using CodeHatch.Blocks.Networking.Events;        // CubeDamageEvent, CubePlaceEvent, CubeDestroyEvent, MassCubeDestroyEvent [CODE]
using CodeHatch.Common;                          // Vector3Int, PlayerExtensions.SendMessage/SendError [CODE]
using CodeHatch.Damaging;                        // Damage, DamageType [CODE]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [CODE]
using CodeHatch.Engine.Networking;               // Player, Server [CODE]
using CodeHatch.Inventory.Blueprints;            // InvBlueprints, InvItemBlueprint [CODE]
using CodeHatch.Blocks.Inventory;             // TilesetBlueprint [CODE]
using CodeHatch.Networking.Events;               // EventManager, EventSubscriber, EventHandlerOrder [CODE]
using CodeHatch.Thrones.SocialSystem;            // CrestScheme [CODE]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using UnityEngine;                               // Vector3

namespace Oxide.Plugins
{
    [Info("RealmSculptor", "Realm", "0.1.0")]
    [Description("Realm's monuments in the real game: places, paints, protects and removes block sculptures made by art/tools/sculptor")]
    public class RealmSculptor : ReignOfKingsPlugin
    {
        private const string PermAdmin = "realmsculptor.admin";
        private const string DataName = "RealmSculptor";
        private const string SculptureDir = "RealmSculptor";
        private const string MaterialsDumpName = "RealmSculptor-materials";
        private const string Format = "realm-sculpture/1";
        private const int MaxSide = 64;
        private const int FormatVersion = 1;
        private const float BlockSize = 1.2f;            // BlockManager.BLOCK_SIZE [CODE]

        // Cell record: one int[] per block of a placement (kept in the data file). Colours are 0xRRGGBB, -1 = none.
        private const int CX = 0, CY = 1, CZ = 2, CMat = 3, CPrefab = 4, CRot = 5, CRgb = 6;
        private const int CPrevMat = 7, CPrevPrefab = 8, CPrevRot = 9, CPrevRgb = 10, CPrevAlpha = 11, CFlag = 12, CellLength = 13;
        // Cell flags.
        private const int FPending = 0, FPlaced = 1, FDone = 2, FSkipped = 3, FFailed = 4, FRemoved = 5, FLeft = 6, FLost = 7;

        // The game's 24 block rotations (CubeInfo.PossibleRotations [CODE]) as Euler angles, and the index each one
        // becomes when the whole sculpture turns k quarter-turns about the vertical axis (k = 0..3). Generated from
        // integer matrices (Ry * Rx * Rz); plugins/docs/RealmSculptor/logic-tests checks both against rotations.json,
        // which art/tools/sculptor writes from its own copy.
        private static readonly int[,] Euler = new int[,]
        {
            { 0, 0, 0 }, { 0, 90, 0 }, { 0, 180, 0 }, { 0, 270, 0 }, { 180, 0, 0 }, { 180, 90, 0 }, { 180, 180, 0 }, { 180, 270, 0 },
            { 0, 90, 90 }, { 90, 90, 90 }, { 180, 90, 90 }, { 270, 90, 90 }, { 0, -90, 90 }, { -90, -90, 90 }, { 0, 90, -90 }, { 90, 180, 0 },
            { 0, 180, 90 }, { -90, 270, 0 }, { 0, 0, -90 }, { 90, 0, -90 }, { 0, 0, 90 }, { -90, 0, 90 }, { 0, 180, -90 }, { 90, 180, -90 }
        };
        private static readonly int[,] TurnTable = new int[,]
        {
            { 0, 1, 2, 3 }, { 1, 2, 3, 0 }, { 2, 3, 0, 1 }, { 3, 0, 1, 2 }, { 4, 5, 6, 7 }, { 5, 6, 7, 4 }, { 6, 7, 4, 5 }, { 7, 4, 5, 6 },
            { 8, 16, 12, 20 }, { 9, 19, 15, 23 }, { 10, 18, 14, 22 }, { 11, 17, 13, 21 }, { 12, 20, 8, 16 }, { 13, 21, 11, 17 },
            { 14, 22, 10, 18 }, { 15, 23, 9, 19 }, { 16, 12, 20, 8 }, { 17, 13, 21, 11 }, { 18, 14, 22, 10 }, { 19, 15, 23, 9 },
            { 20, 8, 16, 12 }, { 21, 11, 17, 13 }, { 22, 10, 18, 14 }, { 23, 9, 19, 15 }
        };
        // Single-block shapes a sculpture may use (CubeInfo.IsPrefabTypeSingleBlock [CODE]); doors, gates, windows and
        // multi-block parts (10-14, 16, 255) are never placed and never overwritten.
        private static readonly int[] SingleBlockPrefabs = new int[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 15 };

        private PluginConfig config;
        private StoredData data;
        private bool dataFailed;                         // the data file exists but could not be read: never write it
        private bool configFailed;
        private bool initialized;
        private string bindError;                        // why the game's block API could not be bound (null = bound)
        private Timer tickTimer;
        private object decaySubscriber;                  // EventSubscriber<MassCubeDestroyEvent>, kept to unsubscribe
        private bool selfPlacing;                        // true while this plugin is inside the game's place call
        private Func<DateTime> clock = delegate { return DateTime.UtcNow; };
        private bool dirty;
        private double lastSave;

        private readonly Dictionary<string, Sculpture> sculptures = new Dictionary<string, Sculpture>();
        private readonly List<string> sculptureProblems = new List<string>();
        private readonly Dictionary<long, Placement> cellOwner = new Dictionary<long, Placement>();
        private readonly List<Job> jobs = new List<Job>();
        private readonly Dictionary<string, double> cooldowns = new Dictionary<string, double>();

        // Bound by reflection in BindGame().
        private RootCubeGrid grid;
        private MethodInfo placeMethod, colourMethod, rotationIndexMethod;
        private PropertyInfo rotationProperty, colourProperty;
        private Array rotations;
        private Type colour32Type;
        private FieldInfo c32r, c32g, c32b, c32a;

        #region Config and data

        private class PluginConfig
        {
            public bool Enabled = true;                  // false: nothing is placed or removed (protection still works)
            public int BlocksPerTick = 25;               // game calls per tick, placing and painting together (1..200)
            public float TickSeconds = 0.2f;             // 0.05..2
            public float PlaceDelaySeconds = 0.05f;      // the build delay sent to clients with each block (0.02..5)
            public float ColourDelaySeconds = 1.5f;      // time between placing a block and painting it (0.2..30)
            public bool Paint = true;                    // false: blocks keep their material's own colour
            public bool SimplifyShapes = false;          // true: every slope, stair or peak is placed as a plain block
            public bool ProtectSculptures = true;        // new placements are protected (per placement: /sculpt protect)
            public bool AllowUnclaimedLand = false;      // true: "unclaimed" in /sculpt place may build outside crest zones
            public bool GuardDecay = true;               // keep the game's block decay off protected cells
            public bool AnnounceRaised = false;          // tell everyone when a placement is finished
            public int DistanceAhead = 2;                // empty cells between the admin and the sculpture (0..20)
            public int MaxBlocksPerSculpture = 4000;     // 1..20000
            public int MaxPlacements = 200;              // standing placements kept on record (10..2000)
            public int MessageCooldownSeconds = 10;      // "this is a monument" reply to a player hitting one
            public int SaveIntervalSeconds = 20;         // while a job runs (5..600)
            public Dictionary<string, int> MaterialIds = DefaultMaterialIds();
        }

        // Material roles -> the game's material (tileset) ids. Ids 1-9 are UNVERIFIED on our server: /sculpt materials
        // writes the real table; fix a wrong id here without rebuilding any sculpture.
        private static Dictionary<string, int> DefaultMaterialIds()
        {
            Dictionary<string, int> m = new Dictionary<string, int>();
            m["cobblestone"] = 1; m["stone"] = 2; m["clay"] = 3; m["sod"] = 4; m["thatch"] = 5;
            m["spruce"] = 6; m["wood"] = 7; m["log"] = 8; m["reinforced"] = 9;
            return m;
        }

        private class StoredData
        {
            public int Version = FormatVersion;
            public int NextId = 1;
            public List<Placement> Placements = new List<Placement>();
        }

        private class Placement
        {
            public int Id;
            public string Sculpture = "";
            public string Name = "";
            public int X, Y, Z;                          // grid cell of the sculpture's corner after turning
            public int Turn;                             // quarter-turns
            public string By = "";
            public string ById = "";
            public string At = "";                       // UTC, ISO 8601
            public string State = "placing";             // placing | standing | removing | removed
            public bool Protected = true;
            public bool Forced;
            public List<int[]> Cells = new List<int[]>();
        }

        // A sculpture file, as art/tools/sculptor writes it. Blocks: [x, y, z, material, prefab, rotation, "#rrggbb"].
        private class SculptureFile
        {
            public string format;
            public string id;
            public string name;
            public string description;
            public List<int> size;
            public Dictionary<string, string> materials;
            public string license;
            public string source;
            public List<List<object>> blocks;
        }

        private class Sculpture
        {
            public string Id, Name, Description, License, Source;
            public int SX, SY, SZ;
            public List<int[]> Blocks = new List<int[]>();     // x, y, z, material, prefab, rotation, rgb (-1 none)
            public Dictionary<int, string> Roles = new Dictionary<int, string>();
        }

        private class Job
        {
            public Placement P;
            public bool Removing;
            public int[] Order;                          // cell indices in working order
            public int Cursor;                           // next cell to place or remove
            public int PaintCursor;                      // next cell to check and paint
            public double[] Due;                         // when each cell (by order position) may be painted
            public Player Owner;                         // who started it, told when it ends (may be offline)
            public int Acted;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private void ClampConfig()
        {
            config.BlocksPerTick = Clamp(config.BlocksPerTick, 1, 200);
            config.TickSeconds = ClampF(config.TickSeconds, 0.05f, 2f);
            config.PlaceDelaySeconds = ClampF(config.PlaceDelaySeconds, 0.02f, 5f);
            config.ColourDelaySeconds = ClampF(config.ColourDelaySeconds, 0.2f, 30f);
            config.DistanceAhead = Clamp(config.DistanceAhead, 0, 20);
            config.MaxBlocksPerSculpture = Clamp(config.MaxBlocksPerSculpture, 1, 20000);
            config.MaxPlacements = Clamp(config.MaxPlacements, 10, 2000);
            config.MessageCooldownSeconds = Clamp(config.MessageCooldownSeconds, 0, 3600);
            config.SaveIntervalSeconds = Clamp(config.SaveIntervalSeconds, 5, 600);
            if (config.MaterialIds == null) config.MaterialIds = DefaultMaterialIds();
            foreach (KeyValuePair<string, int> kv in DefaultMaterialIds())
                if (!config.MaterialIds.ContainsKey(kv.Key)) config.MaterialIds[kv.Key] = kv.Value;
        }

        private void LoadData()
        {
            data = null;
            dataFailed = false;
            try
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(DataName))
                    data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                dataFailed = true;
                PrintError("Could not read oxide/data/RealmSculptor.json: " + ex.Message + ". Placing and removing are switched off and the file is NOT rewritten; fix or remove it and reload.");
            }
            if (data == null) data = new StoredData();
            if (data.Placements == null) data.Placements = new List<Placement>();
            cellOwner.Clear();
            jobs.Clear();
            List<Placement> broken = new List<Placement>();
            foreach (Placement p in data.Placements)
            {
                if (p == null || p.Cells == null) { broken.Add(p); continue; }
                bool bad = false;
                foreach (int[] c in p.Cells) if (c == null || c.Length != CellLength) { bad = true; break; }
                if (bad) { broken.Add(p); continue; }
                if (p.Id >= data.NextId) data.NextId = p.Id + 1;
                foreach (int[] c in p.Cells) if (c[CFlag] == FPlaced || c[CFlag] == FDone) cellOwner[Key(c[CX], c[CY], c[CZ])] = p;
            }
            foreach (Placement p in broken)
            {
                data.Placements.Remove(p);
                PrintWarning("Dropped a damaged placement record" + (p != null ? " #" + p.Id : "") + " from oxide/data/RealmSculptor.json.");
            }
            // Jobs that were running when the server stopped carry on, oldest first.
            foreach (Placement p in data.Placements)
            {
                if (p.State == "placing") jobs.Add(NewJob(p, false, null));
                else if (p.State == "removing") jobs.Add(NewJob(p, true, null));
            }
        }

        private void SaveData()
        {
            if (dataFailed || data == null) return;
            try
            {
                Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
                dirty = false;
                lastSave = Now();
            }
            catch (Exception ex)
            {
                PrintError("Could not save oxide/data/RealmSculptor.json: " + ex.Message);
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                configFailed = true;
                PrintError("Could not read oxide/config/RealmSculptor.json: " + ex.Message + ". Using defaults in memory; the file is NOT rewritten.");
            }
            if (config == null) { config = new PluginConfig(); if (!configFailed) Config.WriteObject(config, true); }
            ClampConfig();
            LoadData();
            LoadSculptures();
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                     // re-sent on hot load; keep idempotent
            initialized = true;
            BindGame();
            if (bindError != null) PrintError("Block API not available (" + bindError + "). Placing and removing are switched off; protection of recorded cells still works.");
            if (config.GuardDecay) SubscribeDecay();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            if (jobs.Count > 0) Puts("Resuming " + jobs.Count + " unfinished sculpture job(s).");
        }

        private void OnServerSave()
        {
            if (dirty) SaveData();
        }

        private void Unload()
        {
            if (tickTimer != null) { tickTimer.Destroy(); tickTimer = null; }
            UnsubscribeDecay();
            SaveData();
        }

        #endregion

        #region Game binding

        // Finds the game's block calls once. Quaternion and Color32 are only ever handled as object, so the plugin does
        // not name a Unity type the compile check's stub lacks.
        private void BindGame()
        {
            bindError = null;
            try
            {
                grid = BlockManager.DefaultCubeGrid;
                if (grid == null) { bindError = "BlockManager.DefaultCubeGrid is null"; return; }
                Type g = typeof(RootCubeGrid);
                placeMethod = FindMethod(g, "PlaceCubeAtLocal", 7);
                colourMethod = FindMethod(g, "ColorCubeAtLocal", 2);
                Type ci = typeof(CubeInfo);
                FieldInfo rf = ci.GetField("PossibleRotations", BindingFlags.Public | BindingFlags.Static);
                rotations = rf != null ? rf.GetValue(null) as Array : null;
                rotationIndexMethod = ci.GetMethod("GetIndexOfRotation", BindingFlags.Public | BindingFlags.Static);
                rotationProperty = ci.GetProperty("Rotation", BindingFlags.Public | BindingFlags.Instance);
                colourProperty = ci.GetProperty("CubeColor", BindingFlags.Public | BindingFlags.Instance);
                if (placeMethod == null) { bindError = "RootCubeGrid.PlaceCubeAtLocal (7 parameters) not found"; return; }
                if (colourMethod == null) { bindError = "RootCubeGrid.ColorCubeAtLocal not found"; return; }
                if (rotations == null || rotations.Length != 24) { bindError = "CubeInfo.PossibleRotations is not a table of 24"; return; }
                if (rotationIndexMethod == null || rotationProperty == null || colourProperty == null) { bindError = "CubeInfo rotation or colour members not found"; return; }
                colour32Type = colourMethod.GetParameters()[1].ParameterType;
                c32r = colour32Type.GetField("r"); c32g = colour32Type.GetField("g"); c32b = colour32Type.GetField("b"); c32a = colour32Type.GetField("a");
                if (c32r == null || c32g == null || c32b == null || c32a == null) { bindError = "Color32 fields r, g, b, a not found"; return; }
            }
            catch (Exception ex)
            {
                bindError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static MethodInfo FindMethod(Type t, string name, int parameters)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == name && m.GetParameters().Length == parameters) return m;
            return null;
        }

        private bool GameReady() { return bindError == null && grid != null && placeMethod != null; }

        private struct BlockState
        {
            public int Mat, Prefab, Rot, Rgb, Alpha;
        }

        private BlockState Get(int x, int y, int z)
        {
            BlockState s = new BlockState();
            CubeInfo info = grid.GetCubeInfoAtLocal(new Vector3Int(x, y, z));
            s.Mat = info.MaterialID;
            s.Prefab = info.PrefabID;
            s.Rot = 0;
            s.Rgb = -1;
            s.Alpha = 255;
            if (s.Mat == 0) return s;
            object boxed = info;
            try
            {
                if (s.Prefab != 0) s.Rot = Convert.ToInt32(rotationIndexMethod.Invoke(null, new object[] { rotationProperty.GetValue(boxed, null) }));
                object c = colourProperty.GetValue(boxed, null);
                s.Rgb = (Convert.ToInt32(c32r.GetValue(c)) << 16) | (Convert.ToInt32(c32g.GetValue(c)) << 8) | Convert.ToInt32(c32b.GetValue(c));
                s.Alpha = Convert.ToInt32(c32a.GetValue(c));
            }
            catch (Exception) { }
            return s;
        }

        private int MaterialAt(int x, int y, int z)
        {
            return grid.GetCubeInfoAtLocal(new Vector3Int(x, y, z)).MaterialID;
        }

        private void Place(int x, int y, int z, int mat, int prefab, int rot, float delay)
        {
            object q = rotations.GetValue(prefab == 0 || mat == 0 ? 0 : rot);
            selfPlacing = true;
            try
            {
                placeMethod.Invoke(grid, new object[] { new Vector3Int(x, y, z), (byte)mat, (byte)prefab, q, false, false, delay });
            }
            finally { selfPlacing = false; }
        }

        private void Paint(int x, int y, int z, int rgb, int alpha)
        {
            object c = Activator.CreateInstance(colour32Type, new object[] { (byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255), (byte)(alpha & 255) });
            colourMethod.Invoke(grid, new object[] { new Vector3Int(x, y, z), c });
        }

        private static bool HasMaterial(int mat)
        {
            try { return mat > 0 && mat < 255 && TilesetLibrary.HasCubePrefab((byte)mat); }
            catch (Exception) { return false; }
        }

        private static bool HasShape(int mat, int prefab)
        {
            if (prefab == 0) return true;
            try
            {
                OctTileset t = TilesetLibrary.GetTilesetWithID((byte)mat, false);
                return t != null && t.GetPrefabWithID(prefab) != null;
            }
            catch (Exception) { return false; }
        }

        // Grid cell -> world position (the centre of the cell).
        private Vector3 ToWorld(int x, int y, int z)
        {
            return grid.LocalToWorldCoordinate(new Vector3Int(x, y, z));
        }

        private static CrestScheme Crests()
        {
            try { return SocialAPI.Get<CrestScheme>(); }
            catch (Exception) { return null; }
        }

        #endregion

        #region Sculptures

        private void LoadSculptures()
        {
            sculptures.Clear();
            sculptureProblems.Clear();
            string[] files;
            try { files = Interface.Oxide.DataFileSystem.GetFiles(SculptureDir, "*.json"); }
            catch (Exception) { files = new string[0]; }
            if (files == null) files = new string[0];
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string f in files)
            {
                string name = FileStem(f);
                if (name.Length == 0) continue;
                SculptureFile raw;
                try { raw = Interface.Oxide.DataFileSystem.ReadObject<SculptureFile>(SculptureDir + "/" + name); }
                catch (Exception ex) { sculptureProblems.Add(name + ": not readable JSON (" + Short(ex.Message, 80) + ")"); continue; }
                string why;
                Sculpture s = ParseSculpture(raw, name, out why);
                if (s == null) { sculptureProblems.Add(name + ": " + why); continue; }
                sculptures[s.Id] = s;
            }
        }

        private static string FileStem(string path)
        {
            if (path == null) return "";
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            string n = slash >= 0 ? path.Substring(slash + 1) : path;
            return n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 5) : n;
        }

        private static bool ToInt(object o, out int v)
        {
            v = 0;
            if (o == null) return false;
            if (o is string) return int.TryParse((string)o, out v);
            try
            {
                double d = Convert.ToDouble(o);
                if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;
                v = (int)d;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool IsSingleBlock(int prefab)
        {
            return Array.IndexOf(SingleBlockPrefabs, prefab) >= 0;
        }

        // Checks a sculpture file the way art/tools/sculptor's validate() does (the parts the plugin relies on).
        private Sculpture ParseSculpture(SculptureFile f, string fileName, out string why)
        {
            why = null;
            if (f == null) { why = "empty file"; return null; }
            if (f.format != Format) { why = "format is not " + Format; return null; }
            if (string.IsNullOrEmpty(f.id) || f.id != fileName) { why = "id must match the file name"; return null; }
            if (f.id.Length > 40) { why = "id is longer than 40 characters"; return null; }
            foreach (char ch in f.id) if (!((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-')) { why = "id may only use a-z, 0-9 and -"; return null; }
            if (string.IsNullOrEmpty(f.name)) { why = "name is missing"; return null; }
            if (string.IsNullOrEmpty(f.license) || string.IsNullOrEmpty(f.source)) { why = "license and source are required"; return null; }
            if (f.size == null || f.size.Count != 3) { why = "size must be [x, y, z]"; return null; }
            foreach (int v in f.size) if (v < 1 || v > MaxSide) { why = "size must be 1-" + MaxSide + " on each side"; return null; }
            if (f.blocks == null || f.blocks.Count == 0) { why = "no blocks"; return null; }
            if (f.blocks.Count > config.MaxBlocksPerSculpture) { why = f.blocks.Count + " blocks is over MaxBlocksPerSculpture (" + config.MaxBlocksPerSculpture + ")"; return null; }
            Sculpture s = new Sculpture();
            s.Id = f.id; s.Name = Short(f.name, 60); s.Description = Short(f.description ?? "", 200);
            s.License = f.license; s.Source = f.source;
            s.SX = f.size[0]; s.SY = f.size[1]; s.SZ = f.size[2];
            if (f.materials != null)
                foreach (KeyValuePair<string, string> kv in f.materials)
                {
                    int id;
                    if (int.TryParse(kv.Key, out id) && !string.IsNullOrEmpty(kv.Value)) s.Roles[id] = kv.Value;
                }
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            for (int i = 0; i < f.blocks.Count; i++)
            {
                List<object> b = f.blocks[i];
                if (b == null || b.Count != 7) { why = "block " + i + " must have 7 fields"; return null; }
                int[] v = new int[7];
                for (int k = 0; k < 6; k++) if (!ToInt(b[k], out v[k])) { why = "block " + i + " field " + k + " is not a whole number"; return null; }
                if (v[0] < 0 || v[1] < 0 || v[2] < 0 || v[0] >= s.SX || v[1] >= s.SY || v[2] >= s.SZ) { why = "block " + i + " is outside size"; return null; }
                if (v[3] < 1 || v[3] > 254) { why = "block " + i + " material " + v[3] + " is not 1-254"; return null; }
                if (!IsSingleBlock(v[4])) { why = "block " + i + " shape " + v[4] + " is not a single-block shape"; return null; }
                if (v[5] < 0 || v[5] > 23) { why = "block " + i + " rotation " + v[5] + " is not 0-23"; return null; }
                if (v[4] == 0) v[5] = 0;
                string col = b[6] as string;
                if (b[6] == null || col == "") v[6] = -1;
                else if (!ParseHex(col, out v[6])) { why = "block " + i + " colour must be \"#rrggbb\""; return null; }
                long key = Key(v[0], v[1], v[2]);
                if (seen.ContainsKey(key)) { why = "block " + i + " repeats a position"; return null; }
                seen[key] = true;
                s.Blocks.Add(v);
            }
            return s;
        }

        private static bool ParseHex(string s, out int rgb)
        {
            rgb = 0;
            if (s == null || s.Length != 7 || s[0] != '#') return false;
            for (int i = 1; i < 7; i++)
            {
                int d = "0123456789abcdef".IndexOf(char.ToLowerInvariant(s[i]));
                if (d < 0) return false;
                rgb = rgb * 16 + d;
            }
            return true;
        }

        #endregion

        #region Planning

        private static long Key(int x, int y, int z)
        {
            return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
        }

        // Turns a position k quarter-turns about the vertical axis: Ry(90k) applied to (x, z).
        private static void TurnXZ(int x, int z, int k, out int tx, out int tz)
        {
            switch (((k % 4) + 4) % 4)
            {
                case 1: tx = z; tz = -x; return;
                case 2: tx = -x; tz = -z; return;
                case 3: tx = -z; tz = x; return;
                default: tx = x; tz = z; return;
            }
        }

        private static int TurnRotation(int rot, int k)
        {
            if (rot < 0 || rot > 23) return 0;
            return TurnTable[rot, ((k % 4) + 4) % 4];
        }

        // The direction a player faces as a quarter-turn: 0 = +z, 1 = +x, 2 = -z, 3 = -x.
        private static int Quadrant(float fx, float fz)
        {
            if (Math.Abs(fx) >= Math.Abs(fz)) return fx >= 0 ? 1 : 3;
            return fz >= 0 ? 0 : 2;
        }

        private class Plan
        {
            public Sculpture S;
            public int Turn, Facing;
            public int X0, Y0, Z0;                       // the corner cell
            public int WX, WZ;                           // footprint after turning
            public List<int[]> Cells = new List<int[]>();
            public int Occupied, OtherSculpture, Doors, Unclaimed, ShapesSimplified;
            public List<ulong> CrestGroups = new List<ulong>();
            public string MaterialProblem;
        }

        private class PlaceArgs
        {
            public int Turn = -1;                        // -1 = auto: the front faces the admin
            public bool Force, Unclaimed;
            public int Lift;
            public string Error;
        }

        private static PlaceArgs ParsePlaceArgs(string[] args, int from)
        {
            PlaceArgs a = new PlaceArgs();
            for (int i = from; i < args.Length; i++)
            {
                string t = (args[i] ?? "").ToLowerInvariant();
                int n;
                if (t == "auto") a.Turn = -1;
                else if (t == "force") a.Force = true;
                else if (t == "unclaimed") a.Unclaimed = true;
                else if ((t.StartsWith("+") || t.StartsWith("-")) && int.TryParse(t.Substring(1), out n) && n <= 20) a.Lift = t[0] == '-' ? -n : n;
                else if (int.TryParse(t, out n) && n >= 0 && n <= 3) a.Turn = n;
                else { a.Error = t; return a; }
            }
            return a;
        }

        // Works out where every block of a sculpture goes for an admin standing at feet (a grid cell) facing quadrant q.
        private Plan MakePlan(Sculpture s, int fx, int fy, int fz, int q, PlaceArgs a)
        {
            Plan p = new Plan();
            p.S = s;
            p.Facing = q;
            p.Turn = a.Turn < 0 ? q : a.Turn;
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (int[] b in s.Blocks)
            {
                int tx, tz;
                TurnXZ(b[0], b[2], p.Turn, out tx, out tz);
                if (tx < minX) minX = tx; if (tx > maxX) maxX = tx;
                if (tz < minZ) minZ = tz; if (tz > maxZ) maxZ = tz;
            }
            p.WX = maxX - minX + 1;
            p.WZ = maxZ - minZ + 1;
            int gap = config.DistanceAhead + 1;
            switch (q)
            {
                case 1: p.X0 = fx + gap; p.Z0 = fz - p.WZ / 2; break;
                case 2: p.X0 = fx - p.WX / 2; p.Z0 = fz - gap - p.WZ + 1; break;
                case 3: p.X0 = fx - gap - p.WX + 1; p.Z0 = fz - p.WZ / 2; break;
                default: p.X0 = fx - p.WX / 2; p.Z0 = fz + gap; break;
            }
            p.Y0 = fy + a.Lift;
            Dictionary<int, bool> missingMaterial = new Dictionary<int, bool>();
            foreach (int[] b in s.Blocks)
            {
                int tx, tz;
                TurnXZ(b[0], b[2], p.Turn, out tx, out tz);
                int[] c = new int[CellLength];
                c[CX] = p.X0 + tx - minX; c[CY] = p.Y0 + b[1]; c[CZ] = p.Z0 + tz - minZ;
                c[CMat] = MaterialFor(s, b[3]);
                c[CPrefab] = b[4];
                c[CRot] = b[4] == 0 ? 0 : TurnRotation(b[5], p.Turn);
                c[CRgb] = config.Paint && b[6] >= 0 ? b[6] : -1;
                c[CPrevMat] = 0; c[CPrevPrefab] = 0; c[CPrevRot] = 0; c[CPrevRgb] = -1; c[CPrevAlpha] = 255;
                c[CFlag] = FPending;
                if (!HasMaterial(c[CMat])) missingMaterial[c[CMat]] = true;
                else if (c[CPrefab] != 0 && (config.SimplifyShapes || !HasShape(c[CMat], c[CPrefab])))
                {
                    c[CPrefab] = 0; c[CRot] = 0; p.ShapesSimplified++;
                }
                p.Cells.Add(c);
            }
            if (missingMaterial.Count > 0)
            {
                List<string> names = new List<string>();
                foreach (int m in missingMaterial.Keys) names.Add(m + RoleSuffix(s, m));
                p.MaterialProblem = string.Join(", ", names.ToArray());
            }
            Survey(p);
            return p;
        }

        private int MaterialFor(Sculpture s, int id)
        {
            string role;
            int mapped;
            if (s.Roles.TryGetValue(id, out role) && config.MaterialIds.TryGetValue(role, out mapped)) return mapped;
            return id;
        }

        private string RoleSuffix(Sculpture s, int mappedId)
        {
            foreach (KeyValuePair<string, int> kv in config.MaterialIds) if (kv.Value == mappedId) return " (" + kv.Key + ")";
            return "";
        }

        // What is already in the target cells, and whose land it is.
        private void Survey(Plan p)
        {
            CrestScheme crests = Crests();
            Dictionary<long, bool> columns = new Dictionary<long, bool>();
            foreach (int[] c in p.Cells)
            {
                Placement owner;
                if (cellOwner.TryGetValue(Key(c[CX], c[CY], c[CZ]), out owner)) { p.OtherSculpture++; continue; }
                BlockState b = Get(c[CX], c[CY], c[CZ]);
                if (b.Mat != 0)
                {
                    p.Occupied++;
                    if (!IsSingleBlock(b.Prefab)) p.Doors++;
                }
                long col = Key(c[CX], 0, c[CZ]);
                if (columns.ContainsKey(col)) continue;
                columns[col] = true;
                if (crests == null) continue;
                Vector3 w = ToWorld(c[CX], p.Y0, c[CZ]);
                try
                {
                    if (crests.IsEmpty(w)) p.Unclaimed++;
                    else
                    {
                        ulong group = crests.CurrentCrestGroup(w);
                        if (group != 0 && !p.CrestGroups.Contains(group)) p.CrestGroups.Add(group);
                    }
                }
                catch (Exception) { }
            }
        }

        private bool TryFeet(Player player, out int fx, out int fy, out int fz, out int q)
        {
            fx = fy = fz = q = 0;
            try
            {
                if (player == null || player.Entity == null) return false;
                Vector3Int feet = grid.WorldToLocalCoordinate(player.Entity.Position);
                fx = feet.x; fy = feet.y; fz = feet.z;
                Vector3 f = player.Entity.Forward;
                q = Quadrant(f.x, f.z);
                return true;
            }
            catch (Exception) { return false; }
        }

        #endregion

        #region Jobs

        private Job NewJob(Placement p, bool removing, Player owner)
        {
            Job j = new Job();
            j.P = p;
            j.Removing = removing;
            j.Owner = owner;
            int n = p.Cells.Count;
            j.Order = new int[n];
            for (int i = 0; i < n; i++) j.Order[i] = removing ? n - 1 - i : i;   // bottom-up to build, top-down to remove
            j.Due = new double[n];
            return j;
        }

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            if (jobs.Count == 0)
            {
                if (dirty && Now() - lastSave >= config.SaveIntervalSeconds) SaveData();
                return;
            }
            if (!config.Enabled || !GameReady() || dataFailed) return;
            Job j = jobs[0];
            int budget = config.BlocksPerTick;
            double now = Now();
            int n = j.Order.Length;
            // 1. Check and paint the cells whose wait is over.
            while (budget > 0 && j.PaintCursor < j.Cursor && j.Due[j.PaintCursor] <= now)
            {
                int[] c = j.P.Cells[j.Order[j.PaintCursor]];
                budget -= j.Removing ? FinishRemove(c) : FinishPlace(j.P, c);
                j.PaintCursor++;
            }
            // 2. Place or remove the next cells.
            while (budget > 0 && j.Cursor < n)
            {
                int[] c = j.P.Cells[j.Order[j.Cursor]];
                bool acted = j.Removing ? StartRemove(j.P, c) : StartPlace(j.P, c);
                j.Due[j.Cursor] = now + config.ColourDelaySeconds;
                j.Cursor++;
                if (acted) { budget--; j.Acted++; dirty = true; }
            }
            if (j.PaintCursor >= n) FinishJob(j);
            else if (dirty && Now() - lastSave >= config.SaveIntervalSeconds) SaveData();
        }

        // Returns true when a game call was made.
        private bool StartPlace(Placement p, int[] c)
        {
            if (c[CFlag] != FPending) return false;
            long key = Key(c[CX], c[CY], c[CZ]);
            Placement owner;
            if (cellOwner.TryGetValue(key, out owner) && owner != p) { c[CFlag] = FSkipped; return false; }
            BlockState b = Get(c[CX], c[CY], c[CZ]);
            if (b.Mat != 0)
            {
                if (!p.Forced || !IsSingleBlock(b.Prefab)) { c[CFlag] = FSkipped; return false; }
                c[CPrevMat] = b.Mat; c[CPrevPrefab] = b.Prefab; c[CPrevRot] = b.Rot; c[CPrevRgb] = b.Rgb; c[CPrevAlpha] = b.Alpha;
            }
            Place(c[CX], c[CY], c[CZ], c[CMat], c[CPrefab], c[CRot], config.PlaceDelaySeconds);
            c[CFlag] = FPlaced;
            cellOwner[key] = p;
            return true;
        }

        // Checks that the game really placed the block, then paints it. Returns the game calls made.
        private int FinishPlace(Placement p, int[] c)
        {
            if (c[CFlag] != FPlaced) return 0;
            BlockState b = Get(c[CX], c[CY], c[CZ]);
            if (b.Mat != c[CMat] || b.Prefab != c[CPrefab])
            {
                c[CFlag] = FFailed;
                Placement owner;
                long key = Key(c[CX], c[CY], c[CZ]);
                if (cellOwner.TryGetValue(key, out owner) && owner == p) cellOwner.Remove(key);
                dirty = true;
                return 0;
            }
            c[CFlag] = FDone;
            dirty = true;
            if (c[CRgb] < 0) return 0;
            Paint(c[CX], c[CY], c[CZ], c[CRgb], 255);
            return 1;
        }

        private bool StartRemove(Placement p, int[] c)
        {
            if (c[CFlag] != FPlaced && c[CFlag] != FDone) return false;
            long key = Key(c[CX], c[CY], c[CZ]);
            Placement owner;
            if (cellOwner.TryGetValue(key, out owner) && owner == p) cellOwner.Remove(key);
            BlockState b = Get(c[CX], c[CY], c[CZ]);
            if (b.Mat != c[CMat] || b.Prefab != c[CPrefab]) { c[CFlag] = FLeft; return false; }   // changed since: leave it
            if (c[CPrevMat] == 0) Place(c[CX], c[CY], c[CZ], 0, 0, 0, 0f);
            else Place(c[CX], c[CY], c[CZ], c[CPrevMat], c[CPrevPrefab], c[CPrevRot], config.PlaceDelaySeconds);
            c[CFlag] = FRemoved;
            return true;
        }

        private int FinishRemove(int[] c)
        {
            if (c[CFlag] != FRemoved || c[CPrevMat] == 0 || c[CPrevRgb] < 0) return 0;
            BlockState b = Get(c[CX], c[CY], c[CZ]);
            if (b.Mat != c[CPrevMat]) return 0;
            Paint(c[CX], c[CY], c[CZ], c[CPrevRgb], c[CPrevAlpha]);
            return 1;
        }

        private void FinishJob(Job j)
        {
            jobs.Remove(j);
            Placement p = j.P;
            int done = 0, skipped = 0, failed = 0, left = 0, removed = 0;
            foreach (int[] c in p.Cells)
            {
                switch (c[CFlag])
                {
                    case FDone: done++; break;
                    case FSkipped: skipped++; break;
                    case FFailed: failed++; break;
                    case FLeft: left++; break;
                    case FRemoved: removed++; break;
                }
            }
            if (j.Removing)
            {
                p.State = "removed";
                Notify(j.Owner, "Removed", p.Id, p.Name, removed, left);
                Puts("Removed #" + p.Id + " " + p.Sculpture + ": " + removed + " cells restored, " + left + " changed since and left alone.");
            }
            else
            {
                p.State = "standing";
                Notify(j.Owner, "Raised", p.Id, p.Name, done, skipped, failed);
                Puts("Raised #" + p.Id + " " + p.Sculpture + " at " + p.X + "," + p.Y + "," + p.Z + ": " + done + " blocks, " + skipped + " skipped, " + failed + " failed.");
                if (config.AnnounceRaised) Server.BroadcastMessage(Msg("Herald", null) + Fmt("Announce", null, new object[] { p.Name }));
            }
            PruneRecords();
            SaveData();
        }

        // Keeps the newest MaxPlacements records; removed placements go first.
        private void PruneRecords()
        {
            while (data.Placements.Count > config.MaxPlacements)
            {
                int victim = -1;
                for (int i = 0; i < data.Placements.Count; i++) if (data.Placements[i].State == "removed") { victim = i; break; }
                if (victim < 0) break;
                data.Placements.RemoveAt(victim);
            }
        }

        private void Notify(Player p, string key, params object[] args)
        {
            if (p == null) return;
            bool online = false;
            foreach (Player q in Server.ClientPlayers) if (q == p) { online = true; break; }
            if (online) Reply(p, key, args);
        }

        private bool Busy(Placement p)
        {
            foreach (Job j in jobs) if (j.P == p) return true;
            return false;
        }

        #endregion

        #region Protection hooks

        private Placement ProtectedAt(ushort gridId, Vector3Int pos)
        {
            if (gridId != 0) return null;
            Placement p;
            if (!cellOwner.TryGetValue(Key(pos.x, pos.y, pos.z), out p) || p == null || !p.Protected) return null;
            return p;
        }

        // Called by CubeListener.OnCubeDamage before it subtracts Damage.Amount [CODE]. Zero damage + Cancel, as
        // RealmWarden does. In unclaimed land the game's salvage has already paid out for the hit, so a salvage hit is
        // let through there: a protected block would otherwise be an endless resource node.
        private void OnCubeTakeDamage(CubeDamageEvent evt)
        {
            if (!config.ProtectSculptures || evt == null || evt.Cancelled) return;
            try
            {
                Placement p = ProtectedAt(evt.GridID, evt.Position);
                if (p == null) return;
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f) return;
                if ((d.DamageTypes & DamageType.Salvage) != 0 && InUnclaimedLand(evt.Position)) return;
                d.Amount = 0f;
                d.ImpactDamage = 0f;
                d.MiscDamage = 0f;
                evt.Cancel("Realm monument");
                Player attacker = d.DamageSource != null ? d.DamageSource.Owner : null;
                if (attacker != null && !attacker.IsServer) Warn(attacker, p);
            }
            catch (Exception ex) { PrintError("Damage check failed: " + ex.Message); }
        }

        // Called by CubeListener.OnCubePlace before the crest check [CODE]. Players may not build into, replace or
        // remove a protected cell. This plugin's own calls are let through (selfPlacing).
        private void OnCubePlacement(CubePlaceEvent evt)
        {
            if (selfPlacing || !config.ProtectSculptures || evt == null || evt.Cancelled) return;
            try
            {
                Player sender = evt.Sender;
                if (sender == null || sender.IsServer) return;
                Placement p = ProtectedAt(evt.GridID, evt.Position);
                if (p == null) return;
                evt.Cancel("Realm monument");
                Warn(sender, p);
            }
            catch (Exception ex) { PrintError("Placement check failed: " + ex.Message); }
        }

        // A recorded cell was destroyed anyway (protection off, or a path no hook covers): note it for /sculpt repair.
        private void OnCubeDestroyed(CubeDestroyEvent evt)
        {
            if (evt == null || evt.GridID != 0) return;
            try
            {
                Vector3Int pos = evt.Position;
                long key = Key(pos.x, pos.y, pos.z);
                Placement p;
                if (!cellOwner.TryGetValue(key, out p)) return;
                foreach (int[] c in p.Cells)
                    if (c[CX] == pos.x && c[CY] == pos.y && c[CZ] == pos.z && (c[CFlag] == FDone || c[CFlag] == FPlaced)) { c[CFlag] = FLost; break; }
                cellOwner.Remove(key);
                dirty = true;
            }
            catch (Exception ex) { PrintError("Destroy record failed: " + ex.Message); }
        }

        private bool InUnclaimedLand(Vector3Int pos)
        {
            CrestScheme crests = Crests();
            if (crests == null || grid == null) return false;
            try { return crests.IsEmpty(ToWorld(pos.x, pos.y, pos.z)); }
            catch (Exception) { return false; }
        }

        private void Warn(Player player, Placement p)
        {
            if (config.MessageCooldownSeconds > 0 && Cooldown("warn|" + player.Id, config.MessageCooldownSeconds)) return;
            Reply(player, "Monument", p.Name);
        }

        // Decay sends one MassCubeDestroyEvent per unclaimed crest cell [CODE DecaySystem.CleanBlocks]; protected cells
        // are taken out of it, and an event left empty is cancelled.
        private void SubscribeDecay()
        {
            try
            {
                EventSubscriber<MassCubeDestroyEvent> s = new EventSubscriber<MassCubeDestroyEvent>(OnMassCubeDestroy);
                EventManager.Subscribe<MassCubeDestroyEvent>(s, EventHandlerOrder.VeryEarly);
                decaySubscriber = s;
            }
            catch (Exception ex)
            {
                decaySubscriber = null;
                PrintWarning("Could not guard sculptures against decay: " + ex.Message);
            }
        }

        private void UnsubscribeDecay()
        {
            EventSubscriber<MassCubeDestroyEvent> s = decaySubscriber as EventSubscriber<MassCubeDestroyEvent>;
            if (s == null) return;
            try { EventManager.Unsubscribe<MassCubeDestroyEvent>(s); }
            catch (Exception ex) { PrintWarning("Unsubscribe failed: " + ex.Message); }
            decaySubscriber = null;
        }

        private void OnMassCubeDestroy(MassCubeDestroyEvent evt)
        {
            if (!config.ProtectSculptures || evt == null || evt.Cancelled || evt.Position == null || cellOwner.Count == 0) return;
            try
            {
                List<Vector3Int> keep = new List<Vector3Int>();
                int guarded = 0;
                foreach (Vector3Int v in evt.Position)
                {
                    if (ProtectedAt(evt.GridID, v) != null) guarded++;
                    else keep.Add(v);
                }
                if (guarded == 0) return;
                if (keep.Count == 0) evt.Cancel("Realm monument");
                else evt.Position = keep.ToArray();
            }
            catch (Exception ex) { PrintError("Decay guard failed: " + ex.Message); }
        }

        #endregion

        #region Commands

        [ChatCommand("sculpt")]
        private void CmdSculpt(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string sub = args != null && args.Length > 0 ? (args[0] ?? "").ToLowerInvariant() : "help";
            if (args == null) args = new string[0];
            switch (sub)
            {
                case "list": CmdList(player); return;
                case "preview": CmdPreview(player, args); return;
                case "place": CmdPlace(player, args); return;
                case "undo": CmdUndo(player); return;
                case "remove": CmdRemove(player, args); return;
                case "placed": CmdPlaced(player); return;
                case "status": CmdStatus(player); return;
                case "protect": CmdProtect(player, args); return;
                case "repair": CmdRepair(player, args); return;
                case "materials": CmdMaterials(player); return;
                case "reload":
                    LoadSculptures();
                    Reply(player, "Reloaded", sculptures.Count, sculptureProblems.Count);
                    return;
                default:
                    Reply(player, "Help1");
                    Reply(player, "Help2");
                    Reply(player, "Help3");
                    return;
            }
        }

        private void CmdList(Player player)
        {
            if (sculptures.Count == 0 && sculptureProblems.Count == 0) { Reply(player, "ListEmpty"); return; }
            Reply(player, "ListHead", sculptures.Count);
            List<string> ids = new List<string>(sculptures.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                Sculpture s = sculptures[id];
                Reply(player, "ListLine", s.Id, s.Name, s.SX, s.SY, s.SZ, s.Blocks.Count);
            }
            foreach (string why in sculptureProblems) Reply(player, "ListBad", why);
        }

        private bool Ready(Player player)
        {
            if (dataFailed) { ReplyError(player, "DataFailed"); return false; }
            if (!config.Enabled) { ReplyError(player, "Disabled"); return false; }
            if (!GameReady()) { ReplyError(player, "NoGame", bindError ?? "not started"); return false; }
            return true;
        }

        private bool PlanFor(Player player, string[] args, out Plan plan, out PlaceArgs pa)
        {
            plan = null;
            pa = null;
            if (args.Length < 2) { ReplyError(player, "NeedId"); return false; }
            Sculpture s;
            if (!sculptures.TryGetValue(args[1].ToLowerInvariant(), out s)) { ReplyError(player, "Unknown", Short(args[1], 40)); return false; }
            pa = ParsePlaceArgs(args, 2);
            if (pa.Error != null) { ReplyError(player, "BadArg", Short(pa.Error, 20)); return false; }
            int fx, fy, fz, q;
            if (!TryFeet(player, out fx, out fy, out fz, out q)) { ReplyError(player, "NoPosition"); return false; }
            plan = MakePlan(s, fx, fy, fz, q, pa);
            return true;
        }

        private void CmdPreview(Player player, string[] args)
        {
            if (!GameReady()) { ReplyError(player, "NoGame", bindError ?? "not started"); return; }
            Plan p; PlaceArgs a;
            if (!PlanFor(player, args, out p, out a)) return;
            Reply(player, "Preview1", p.S.Name, p.S.Blocks.Count, p.WX, p.S.SY, p.WZ, Metres(p.WX), Metres(p.S.SY), Metres(p.WZ));
            Reply(player, "Preview2", p.X0, p.Y0, p.Z0, p.Turn, FacingWord(p.Facing), Seconds(p.S.Blocks.Count));
            ReportLand(player, p);
            if (p.MaterialProblem != null) ReplyError(player, "NoMaterial", p.MaterialProblem);
            if (p.ShapesSimplified > 0) Reply(player, "Simplified", p.ShapesSimplified);
            if (p.Occupied > 0 || p.OtherSculpture > 0) Reply(player, "Conflicts", p.Occupied, p.OtherSculpture);
            else Reply(player, "Clear");
        }

        private void ReportLand(Player player, Plan p)
        {
            int columns = p.WX * p.WZ;
            if (p.Unclaimed > 0) Reply(player, "LandUnclaimed", p.Unclaimed);
            if (p.CrestGroups.Count > 0)
            {
                List<string> g = new List<string>();
                foreach (ulong id in p.CrestGroups) g.Add(id.ToString());
                Reply(player, "LandCrest", string.Join(", ", g.ToArray()));
            }
            if (p.Unclaimed == 0 && p.CrestGroups.Count == 0 && columns > 0) Reply(player, "LandUnknown");
        }

        private void CmdPlace(Player player, string[] args)
        {
            if (!Ready(player)) return;
            Plan p; PlaceArgs a;
            if (!PlanFor(player, args, out p, out a)) return;
            if (p.MaterialProblem != null) { ReplyError(player, "NoMaterial", p.MaterialProblem); return; }
            if (p.Unclaimed > 0)
            {
                if (!config.AllowUnclaimedLand) { ReplyError(player, "UnclaimedOff", p.Unclaimed); return; }
                if (!a.Unclaimed) { ReplyError(player, "UnclaimedAsk", p.Unclaimed); return; }
            }
            if ((p.Occupied > 0 || p.OtherSculpture > 0) && !a.Force) { ReplyError(player, "Occupied", p.Occupied, p.OtherSculpture); return; }
            int standing = 0;
            foreach (Placement x in data.Placements) if (x.State != "removed") standing++;
            if (standing >= config.MaxPlacements) { ReplyError(player, "TooMany", config.MaxPlacements); return; }

            Placement pl = new Placement();
            pl.Id = data.NextId++;
            pl.Sculpture = p.S.Id;
            pl.Name = p.S.Name;
            pl.X = p.X0; pl.Y = p.Y0; pl.Z = p.Z0;
            pl.Turn = p.Turn;
            pl.By = Short(player.Name ?? "", 40);
            pl.ById = player.Id.ToString();
            pl.At = clock().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            pl.State = "placing";
            pl.Protected = config.ProtectSculptures;
            pl.Forced = a.Force;
            pl.Cells = p.Cells;
            data.Placements.Add(pl);
            jobs.Add(NewJob(pl, false, player));
            SaveData();
            Reply(player, "Placing", pl.Id, pl.Name, p.S.Blocks.Count, Seconds(p.S.Blocks.Count));
            if (jobs.Count > 1) Reply(player, "Queued", jobs.Count - 1);
            if (a.Force && p.Occupied > 0) Reply(player, "ForceNote", p.Occupied);
            Puts(player.Name + " placed #" + pl.Id + " " + pl.Sculpture + " at " + pl.X + "," + pl.Y + "," + pl.Z + " (turn " + pl.Turn + ").");
        }

        private void CmdUndo(Player player)
        {
            if (!Ready(player)) return;
            string me = player.Id.ToString();
            for (int i = data.Placements.Count - 1; i >= 0; i--)
            {
                Placement p = data.Placements[i];
                if (p.ById == me && (p.State == "placing" || p.State == "standing")) { StartRemoval(player, p); return; }
            }
            ReplyError(player, "NothingToUndo");
        }

        private void CmdRemove(Player player, string[] args)
        {
            if (!Ready(player)) return;
            Placement p = FindPlacement(player, args);
            if (p == null) return;
            if (p.State == "removed" || p.State == "removing") { ReplyError(player, "AlreadyRemoved", p.Id); return; }
            StartRemoval(player, p);
        }

        private void StartRemoval(Player player, Placement p)
        {
            Job running = null;
            foreach (Job j in jobs) if (j.P == p) running = j;
            if (running != null) jobs.Remove(running);       // a build in progress stops where it is
            p.State = "removing";
            jobs.Add(NewJob(p, true, player));
            SaveData();
            Reply(player, "Removing", p.Id, p.Name);
        }

        private Placement FindPlacement(Player player, string[] args)
        {
            if (args.Length < 2) { ReplyError(player, "NeedNumber"); return null; }
            string t = args[1].TrimStart('#');
            int id;
            if (!int.TryParse(t, out id)) { ReplyError(player, "NeedNumber"); return null; }
            foreach (Placement p in data.Placements) if (p.Id == id) return p;
            ReplyError(player, "NoPlacement", id);
            return null;
        }

        private void CmdPlaced(Player player)
        {
            int shown = 0;
            for (int i = data.Placements.Count - 1; i >= 0 && shown < 12; i--)
            {
                Placement p = data.Placements[i];
                if (p.State == "removed") continue;
                int done = 0, lost = 0;
                foreach (int[] c in p.Cells) { if (c[CFlag] == FDone) done++; else if (c[CFlag] == FLost) lost++; }
                if (shown == 0) Reply(player, "PlacedHead");
                Reply(player, "PlacedLine", p.Id, p.Name, p.X, p.Y, p.Z, p.State, done, lost, p.Protected ? Msg("On", player) : Msg("Off", player), p.By);
                shown++;
            }
            if (shown == 0) Reply(player, "PlacedNone");
        }

        private void CmdStatus(Player player)
        {
            int standing = 0, cells = cellOwner.Count;
            foreach (Placement p in data.Placements) if (p.State == "standing" || p.State == "placing") standing++;
            Reply(player, "Status1", sculptures.Count, standing, cells, jobs.Count);
            Reply(player, "Status2", GameReady() ? Msg("On", player) : Msg("Off", player) + " (" + (bindError ?? "not started") + ")",
                config.Enabled ? Msg("On", player) : Msg("Off", player), config.ProtectSculptures ? Msg("On", player) : Msg("Off", player),
                decaySubscriber != null ? Msg("On", player) : Msg("Off", player), dataFailed ? Msg("Damaged", player) : "OK");
            Reply(player, "Status3", config.BlocksPerTick, config.TickSeconds, config.ColourDelaySeconds, config.Paint ? Msg("On", player) : Msg("Off", player), config.SimplifyShapes ? Msg("On", player) : Msg("Off", player));
            if (jobs.Count > 0)
            {
                Job j = jobs[0];
                Reply(player, "StatusJob", j.P.Id, j.P.Name, j.Removing ? Msg("JobRemoving", player) : Msg("JobPlacing", player), j.PaintCursor, j.Order.Length);
            }
        }

        private void CmdProtect(Player player, string[] args)
        {
            Placement p = FindPlacement(player, args);
            if (p == null) return;
            string v = args.Length > 2 ? (args[2] ?? "").ToLowerInvariant() : "";
            if (v != "on" && v != "off") { ReplyError(player, "OnOff"); return; }
            p.Protected = v == "on";
            dirty = true;
            SaveData();
            Reply(player, p.Protected ? "ProtectOn" : "ProtectOff", p.Id, p.Name);
        }

        private void CmdRepair(Player player, string[] args)
        {
            if (!Ready(player)) return;
            Placement p = FindPlacement(player, args);
            if (p == null) return;
            if (p.State != "standing") { ReplyError(player, "NotStanding", p.Id); return; }
            if (Busy(p)) { ReplyError(player, "Busy", p.Id); return; }
            // Blocks can also vanish without a hook seeing it (a collapse or decay removes them directly), so every
            // standing cell is checked against the grid first. A cell someone else has built into is left alone.
            int again = 0;
            foreach (int[] c in p.Cells)
            {
                if ((c[CFlag] == FDone || c[CFlag] == FPlaced) && MaterialAt(c[CX], c[CY], c[CZ]) == 0)
                {
                    c[CFlag] = FLost;
                    cellOwner.Remove(Key(c[CX], c[CY], c[CZ]));
                }
                if (c[CFlag] == FLost || c[CFlag] == FFailed) { c[CFlag] = FPending; c[CPrevMat] = 0; c[CPrevPrefab] = 0; c[CPrevRot] = 0; c[CPrevRgb] = -1; c[CPrevAlpha] = 255; again++; }
            }
            if (again == 0) { Reply(player, "NothingToRepair", p.Id); return; }
            p.State = "placing";
            jobs.Add(NewJob(p, false, player));
            SaveData();
            Reply(player, "Repairing", p.Id, again);
        }

        // Writes the game's own material and shape table to oxide/data/RealmSculptor-materials.json, so the ids in
        // MaterialIds and art/tools/sculptor/materials.json can be checked against the real server.
        private void CmdMaterials(Player player)
        {
            MaterialDump dump = new MaterialDump();
            dump.Note = "Written by /sculpt materials from the running server. Compare with MaterialIds in oxide/config/RealmSculptor.json.";
            try
            {
                OctTileset[] sets = TilesetLibrary.Singleton != null ? TilesetLibrary.Singleton.Tilesets : null;
                if (sets != null)
                    for (int i = 0; i < sets.Length; i++)
                    {
                        OctTileset t = sets[i];
                        if (t == null) continue;
                        MaterialRow r = new MaterialRow();
                        r.Id = i + 1;                    // TilesetLibrary.GetOctTilesetWithID: Tilesets[id - 1] [CODE]
                        r.TilesetId = t.TilesetID;
                        r.Name = UnityName(t);
                        r.DefaultColour = ColourHex(Member(t, "DefaultColor"));
                        r.BuildTime = t.BuildTime;
                        r.Quality = t.Quality;
                        r.Solid = t.Solid;
                        if (t.TilesetMaterialBinding != null)
                            foreach (CodeHatch.ModTools.Textures.TilesetMaterialInfo m in t.TilesetMaterialBinding)
                                if (m != null) { if (m.Colorable) r.ColourableParts++; else r.FixedParts++; }
                        if (t.SpecialPeices != null)
                            foreach (OctPrefab o in t.SpecialPeices)
                            {
                                if (o == null) continue;
                                ShapeRow s = new ShapeRow();
                                s.PrefabId = o.PrefabID;
                                s.Name = UnityName(o);
                                s.MultiBlock = o.IsMultiBlock();
                                s.Offsets = o.BlockOffsets != null ? o.BlockOffsets.Length : 0;
                                s.PermittedRotations = o.PermittedRotations;
                                r.Shapes.Add(s);
                            }
                        dump.Materials.Add(r);
                    }
                if (InvBlueprints.Instance != null && InvBlueprints.Instance.AllDefinedBlueprints != null)
                    foreach (InvItemBlueprint bp in InvBlueprints.Instance.AllDefinedBlueprints)
                    {
                        if (bp == null || !bp.Has<TilesetBlueprint>()) continue;
                        TilesetBlueprint tb = bp.Get<TilesetBlueprint>();
                        ItemRow it = new ItemRow();
                        it.Item = bp.Name;
                        it.Material = tb.MaterialID;
                        it.Prefab = tb.PrefabID;
                        dump.Items.Add(it);
                    }
            }
            catch (Exception ex)
            {
                dump.Error = ex.GetType().Name + ": " + ex.Message;
            }
            try { Interface.Oxide.DataFileSystem.WriteObject(MaterialsDumpName, dump); }
            catch (Exception ex) { ReplyError(player, "DumpFailed", Short(ex.Message, 80)); return; }
            Reply(player, "Dumped", dump.Materials.Count, dump.Items.Count);
            foreach (MaterialRow r in dump.Materials)
            {
                List<string> shapes = new List<string>();
                foreach (ShapeRow s in r.Shapes) shapes.Add(s.PrefabId.ToString());
                Reply(player, "DumpLine", r.Id, Short(r.Name, 30), r.ColourableParts, r.FixedParts, shapes.Count > 0 ? string.Join(" ", shapes.ToArray()) : "-");
            }
            if (dump.Error != null) ReplyError(player, "DumpFailed", Short(dump.Error, 80));
        }

        private class MaterialDump
        {
            public string Note;
            public string Error;
            public List<MaterialRow> Materials = new List<MaterialRow>();
            public List<ItemRow> Items = new List<ItemRow>();
        }

        private class MaterialRow
        {
            public int Id, TilesetId;
            public string Name, DefaultColour;
            public float BuildTime, Quality;
            public bool Solid;
            public int ColourableParts, FixedParts;
            public List<ShapeRow> Shapes = new List<ShapeRow>();
        }

        private class ShapeRow
        {
            public int PrefabId, Offsets, PermittedRotations;
            public string Name;
            public bool MultiBlock;
        }

        private class ItemRow
        {
            public string Item;
            public int Material, Prefab;
        }

        // UnityEngine.Object.name, by reflection (the compile check's stub has no name property).
        private static string UnityName(object o)
        {
            try
            {
                PropertyInfo pi = o.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                return pi != null ? Convert.ToString(pi.GetValue(o, null)) : "";
            }
            catch (Exception) { return ""; }
        }

        private static object Member(object o, string property)
        {
            try
            {
                PropertyInfo pi = o.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                return pi != null ? pi.GetValue(o, null) : null;
            }
            catch (Exception) { return null; }
        }

        // A UnityEngine.Color (floats r, g, b) as #rrggbb, by reflection.
        private static string ColourHex(object c)
        {
            if (c == null) return "";
            try
            {
                Type t = c.GetType();
                float r = Convert.ToSingle(t.GetField("r").GetValue(c)), g = Convert.ToSingle(t.GetField("g").GetValue(c)), b = Convert.ToSingle(t.GetField("b").GetValue(c));
                return "#" + Byte(r).ToString("x2") + Byte(g).ToString("x2") + Byte(b).ToString("x2");
            }
            catch (Exception) { return ""; }
        }

        private static int Byte(float f) { return Clamp((int)Math.Round(f * 255f), 0, 255); }

        #endregion

        #region Chat style

        // Realm chat style, the same block in every Realm plugin (docs/realm-commands.md, "Chat style";
        // tools/realm-integration/check.mjs checks it). A reply opens with its speaker in the colour of its tone:
        // gold for news and answers, green for done, amber for take care, red for refused. A line that starts with
        // a space continues a list and carries no speaker. A text that already opens with a colour tag or with
        // "<speaker>:" (a server's older lang file, or a line with a voice of its own) is sent as it is.
        private const string ChatGold = "D6A043";
        private const string ChatOk = "8FC97A";
        private const string ChatWarn = "E8913A";
        private const string ChatError = "E86A5C";

        private static string Styled(string speaker, string tone, string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(speaker) || text[0] == ' ') return text;
            if (text.StartsWith(speaker + ":", StringComparison.OrdinalIgnoreCase)) return text;
            if (text.Length >= 8 && text[0] == '[' && text[7] == ']' && IsChatHex(text.Substring(1, 6))) return text;
            return "[" + tone + "]" + speaker + "[FFFFFF]: " + text;
        }

        private static bool IsChatHex(string s)
        {
            foreach (char c in s) if ("0123456789ABCDEFabcdef".IndexOf(c) < 0) return false;
            return true;
        }

        #endregion

        #region Lang and helpers

        protected override void LoadDefaultMessages()
        {
            Dictionary<string, string> m = new Dictionary<string, string>();
            m["Speaker"] = "Sculptor";
            m["Herald"] = "[D6A043]Herald[FFFFFF]: ";
            m["Help1"] = "Realm monuments from the game's own blocks. [F4C96D]/sculpt list[FFFFFF] - what can be placed.  [F4C96D]/sculpt preview[FFFFFF] <id> - a dry run where you stand.";
            m["Help2"] = "  [F4C96D]/sculpt place[FFFFFF] <id> [0-3] [force] [unclaimed] [+N|-N] - build it in front of you, facing you.  [F4C96D]/sculpt undo[FFFFFF] - take down your last one.";
            m["Help3"] = "  [F4C96D]/sculpt placed[FFFFFF], [F4C96D]/sculpt remove[FFFFFF] <n>, [F4C96D]/sculpt protect[FFFFFF] <n> on|off, [F4C96D]/sculpt repair[FFFFFF] <n>, [F4C96D]/sculpt status[FFFFFF], [F4C96D]/sculpt materials[FFFFFF], [F4C96D]/sculpt reload[FFFFFF].";
            m["NoPermission"] = "Only the realm's builders may do that.";
            m["ListEmpty"] = "No sculptures found. Copy them to oxide/data/RealmSculptor/ (node art/tools/sculptor/cli.mjs export) and run [F4C96D]/sculpt reload[FFFFFF].";
            m["ListHead"] = "{0} sculpture(s) ready:";
            m["ListLine"] = "  {0} - {1}, {2} x {3} x {4} blocks, {5} pieces";
            m["ListBad"] = "  not loaded: {0}";
            m["Reloaded"] = "Reloaded: {0} sculpture(s) ready, {1} file(s) with problems.";
            m["NeedId"] = "Which sculpture? See [F4C96D]/sculpt list[FFFFFF].";
            m["Unknown"] = "There is no sculpture \"{0}\". See [F4C96D]/sculpt list[FFFFFF].";
            m["BadArg"] = "I do not understand \"{0}\". Use a turn 0-3, auto, force, unclaimed, or +N / -N to raise or sink it.";
            m["NoPosition"] = "I cannot tell where you are standing. Try again in a moment.";
            m["NoGame"] = "The game's block tools are not available ({0}). Nothing can be placed.";
            m["Disabled"] = "Placing is switched off (Enabled is false in oxide/config/RealmSculptor.json).";
            m["DataFailed"] = "oxide/data/RealmSculptor.json could not be read. Fix or remove it and reload the plugin.";
            m["Preview1"] = "{0}: {1} pieces, {2} x {3} x {4} blocks ({5} x {6} x {7} m).";
            m["Preview2"] = "  Corner at {0},{1},{2}, turn {3}, in front of you to the {4}. About {5} s to build.";
            m["LandUnclaimed"] = "  {0} column(s) stand on unclaimed land, where blocks decay and can be salvaged.";
            m["LandCrest"] = "  Inside the crest zone of group(s) {0}.";
            m["LandUnknown"] = "  Land: the crest map could not be read.";
            m["NoMaterial"] = "This server has no block material {0}. Run [F4C96D]/sculpt materials[FFFFFF] and fix MaterialIds in the config.";
            m["Simplified"] = "  {0} sloped piece(s) will be plain blocks (the material has no such shape, or SimplifyShapes is on).";
            m["Conflicts"] = "  In the way: {0} existing block(s), {1} cell(s) of another sculpture. Move, or add force to replace the existing blocks.";
            m["Clear"] = "  The space is clear.";
            m["UnclaimedOff"] = "{0} column(s) are on unclaimed land. Place sculptures inside a crest zone (AllowUnclaimedLand is false).";
            m["UnclaimedAsk"] = "{0} column(s) are on unclaimed land, where decay and salvage wear blocks away. Add unclaimed to build there anyway.";
            m["Occupied"] = "Not placed: {0} existing block(s) and {1} cell(s) of another sculpture are in the way. Add force to replace existing blocks.";
            m["TooMany"] = "There are already {0} placements on record. Remove some first.";
            m["Placing"] = "Raising #{0} {1}: {2} pieces, about {3} s. [F4C96D]/sculpt undo[FFFFFF] takes it down again.";
            m["Queued"] = "  It waits behind {0} other job(s).";
            m["ForceNote"] = "  {0} existing block(s) will be replaced and put back on undo.";
            m["Raised"] = "#{0} {1} stands: {2} blocks placed, {3} skipped (something was in the way), {4} not taken by the game.";
            m["Removing"] = "Taking down #{0} {1}.";
            m["Removed"] = "#{0} {1} is gone: {2} cells put back as they were, {3} changed since and left alone.";
            m["NothingToUndo"] = "You have no standing placement to undo.";
            m["NeedNumber"] = "Which placement? Give its number from [F4C96D]/sculpt placed[FFFFFF].";
            m["NoPlacement"] = "There is no placement #{0}.";
            m["AlreadyRemoved"] = "#{0} is already taken down.";
            m["PlacedHead"] = "Placements (newest first):";
            m["PlacedLine"] = "  #{0} {1} at {2},{3},{4}: {5}, {6} blocks, {7} lost, protection {8}, by {9}";
            m["PlacedNone"] = "Nothing is placed.";
            m["Status1"] = "{0} sculpture(s) loaded, {1} placement(s) standing, {2} cells guarded, {3} job(s) waiting.";
            m["Status2"] = "  block tools {0} | placing {1} | protection {2} | decay guard {3} | data {4}";
            m["Status3"] = "  {0} blocks per {1} s, paint after {2} s, paint {3}, plain shapes {4}";
            m["StatusJob"] = "  now: #{0} {1}, {2}, {3} of {4} cells";
            m["JobPlacing"] = "raising";
            m["JobRemoving"] = "taking down";
            m["OnOff"] = "Say on or off.";
            m["ProtectOn"] = "#{0} {1} is protected: it takes no damage and no one can build into it.";
            m["ProtectOff"] = "#{0} {1} is no longer protected.";
            m["NotStanding"] = "#{0} is not standing, so there is nothing to repair.";
            m["Busy"] = "#{0} is being worked on. Wait until it is done.";
            m["NothingToRepair"] = "#{0} is whole.";
            m["Repairing"] = "Repairing #{0}: {1} block(s) to put back.";
            m["Dumped"] = "Wrote oxide/data/RealmSculptor-materials.json: {0} material(s), {1} block item(s).";
            m["DumpLine"] = "  material {0} {1}: {2} paintable part(s), {3} fixed, shapes {4}";
            m["DumpFailed"] = "Could not read the material table: {0}";
            m["Monument"] = "{0} is a monument of the realm. It cannot be damaged or built over.";
            m["Announce"] = "A new monument stands in the realm: {0}.";
            m["On"] = "on";
            m["Off"] = "off";
            m["Damaged"] = "DAMAGED (not written)";
            m["North"] = "+z";
            m["East"] = "+x";
            m["South"] = "-z";
            m["West"] = "-x";
            lang.RegisterMessages(m, this);
        }

        private string FacingWord(int q)
        {
            switch (q)
            {
                case 1: return Msg("East", null);
                case 2: return Msg("South", null);
                case 3: return Msg("West", null);
                default: return Msg("North", null);
            }
        }

        private static string Metres(int blocks)
        {
            return (blocks * BlockSize).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        private int Seconds(int blocks)
        {
            double perSecond = config.BlocksPerTick / (double)config.TickSeconds;
            return (int)Math.Ceiling(blocks * 2 / perSecond + config.ColourDelaySeconds);   // place + paint
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
        }

        private string Fmt(string key, Player player, object[] args)
        {
            string text = Msg(key, player);
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); }
            catch (FormatException) { return text; }
        }

        private void Reply(Player player, string key, params object[] args)
        {
            string tone = key == "Raised" || key == "Removed" || key == "ProtectOn" || key == "Dumped" ? ChatOk
                : key == "Monument" || key == "Placing" || key == "Removing" || key == "Conflicts" || key == "LandUnclaimed" || key == "ForceNote" ? ChatWarn : ChatGold;
            player.SendMessage(Styled(Msg("Speaker", player), tone, Fmt(key, player, args)));   // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        private bool IsAdmin(Player player)
        {
            return player != null && !player.IsServer && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        // True when the key is still cooling down; otherwise starts the cooldown and returns false.
        private bool Cooldown(string key, int seconds)
        {
            double now = Now();
            double until;
            if (cooldowns.TryGetValue(key, out until) && now < until) return true;
            if (cooldowns.Count > 2000) cooldowns.Clear();
            cooldowns[key] = now + seconds;
            return false;
        }

        private double Now()
        {
            return (clock() - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static string Short(string s, int max)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            foreach (char c in s) if (c != '[' && c != ']' && c >= ' ') b.Append(c);
            string t = b.ToString();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        #endregion
    }
}
