// RealmArrival: the Gate of the Unwritten. A newcomer's first minutes in Ostreval, from the ferryman's raft (the game's
// own character creation, untouched) to the Hearth fire, in about three to five minutes. Design: docs/arrival-design.md.
// Speaker "Hearth", command /arrival. Guide and first-test plan: plugins/docs/RealmArrival.md.
//
//   Records    one per Steam id, kept across wipes (oxide/data/RealmArrival.json). Stage pending -> crossing -> gatehouse
//              -> released -> banners -> hearth -> done (or none: not handled). The variant is chosen in OnPlayerSpawn:
//              A (new to Realm: the full arrival), B (a known veteran on a fresh world: VeteranMode vanilla|short|full),
//              C (a returning player: nothing new, or a resume of an arrival left midway).
//   The cut    at the Finish click (OnPlayerSpawned) the game has already moved the player to a random spawn and is
//              showing its own "Finalizing World" loader; RealmArrival moves them, in the same tick, onto one of six
//              arrival stones in the Gatehouse (RoutingMode teleport), or the game's own move already went there
//              (RoutingMode provider, a wrapper around SpawnpointManager.defaultSpawnpointProvider). Two arrival checks
//              (T+3 s, T+10 s) re-teleport once and fall back to road mode.
//   Narration  starts on the first move or 12 s after the cut (never before 4 s): two lore lines, the call to the gate,
//              one popup. The gate opens on the gold line, the popup's button or after AutoOpenSeconds; in portcullis
//              mode its 30 plugin-owned cells sink row by row and the ember band round the Hearth fire flares.
//   The walk   six banners (pledge stones of the great houses): two lines each, live data from RealmHouses, an optional
//              pledge ("look to a house", told to that house's members; never a join). At the fire: RealmQuests' and
//              RealmTravel's own lines land first, then the fire lines (warm, kit, crown, shelter, the next step).
//   Written    on /quest, on walking 70 m from the fire or 60 s after the last line: the arrival is done.
//   Safety     a sanctuary in the hall box for players in arrival (and their sleepers), eviction of others to the eject
//              point, a 30 s release shield and an arrival-zone guard; Hearth's Mercy respawns; skip, tour, resume.
//
// Game API ([DEC] = read in the decompiled shipped patched Assembly-CSharp.dll, type.member names only; [ASM] = in its
// metadata; [OPJ]/[SRC] as in docs/oxide-rok-api.md). No game code is copied here.
//   Hooks      OnPlayerSpawn(PlayerFirstSpawnEvent) [OPJ L240]: AtFirstSpawn is set by PlayerListener.
//              OnPlayerFirstSpawnVeryEarly before the hook [DEC]; neither it nor Position is ever changed here.
//              OnPlayerSpawned(PlayerPreSpawnCompleteEvent) [OPJ L1244]: after PlayerListener.OnPreSpawnComplete has
//              teleported to SpawnpointManager.GetRandomSpawnPoint() and set HasCompletedCreation [DEC].
//              OnPlayerRespawn(PlayerRespawnEvent) [OPJ L344-L422]: the four subclasses PlayerRespawnRandomlyEvent,
//              PlayerRespawnNormalEvent, PlayerRespawnAtBedEvent, PlayerRespawnAtBaseEvent [ASM]; only the
//              PlayerSpawnEvent.Position setter is used [DEC]. OnEntityHealthChange [OPJ L162], OnCubePlacement and
//              OnCubeTakeDamage [OPJ L266, L292], OnPlayerCommand [SRC], OnPlayerConnected / OnPlayerDisconnected [SRC].
//   Teleport   Entity.GetOrCreate<CharacterTeleport>().Teleport(Vector3) [DEC], as RealmTravel; SentinelGrace first.
//   Provider   SpawnpointManager.defaultSpawnpointProvider (static ISpawnpointProvider with GetSpawnPoint(Entity),
//              GetAllSpawnPoints(Entity), GetRandomSpawnPoint()) [DEC]; EventManager.CurrentEvent [DEC] tells the
//              wrapper which event asks. Restored on Unload and pause.
//   Blocks     BlockManager.DefaultCubeGrid, RootCubeGrid.GetCubeInfoAtLocal, WorldToLocalCoordinate,
//              LocalToWorldCoordinate, PlaceCubeAtLocal (7 parameters; material 0 = air; collectPreviousCube false),
//              ColorCubeAtLocal, CubeInfo.PossibleRotations [DEC]; Quaternion and Color32 by reflection, as RealmSculptor.
//   Sleepers   PlayerSleeperObject.AllSleeperObjects (static Dictionary<ulong, Entity>) [DEC].
//   Popups     PlayerExtensions.ShowPopup / ShowConfirmPopup [ASM]; Dialogue.OnSubmit, Options [ASM].
//   Fire       PlayerExtensions.Heal / Nourish / Hydrate (float amount, -1 = full) [ASM].
//   Extras     NewsFeed.SendNews(string, List<Player>, Severity, bool, string) [DEC] (toasts, off), AudioController.Play(
//              string, Entity, params Player[]) [DEC] (gate sound, empty), EffectDefinition.HealthRegenerationMultiplier.
//              Apply(Entity) [DEC] (hearth buff, off), GameClock.Instance.TimeOfDay / HourOfSunsetStart / HourOfSunsetEnd /
//              HourOfSunriseStart [DEC] (dusk tip and the night reveal; inferred that the server's clock follows the sky).
//   Throne     AncientThrone.EntityPosition [DEC] (the site check), unless an admin stored the point.
//
// Cross-plugin (all optional; null = not available): RealmSentinel.SentinelGrace; RealmTravel.CancelJourney,
// GetDiscoveredCount, IsTravelling; RealmWarden.IsNewPlayerProtected, IsInCombat, RaiseWardenAlert,
// GetProtectionMinutesLeft; RealmHouses.GetHouseSummaries, GetMembers, GetHouseLeader, GetLiege, GetHouse;
// CrownAndConsequences.GetKingName, GetKingHouse; RealmEvents.GetNextEvent; RealmArena.IsDuelBlow;
// RealmQuests.ReportQuestEvent, GetStoryProgress; RealmRenown.AddDeed; RealmHerald.PopupsWanted.
// Offered (non-public): OwnsArrival(string id) -> bool, ArrivalStage(string id) -> pending|crossing|running|done|none.
// Read-only files of other plugins (never written): oxide/data/RealmHerald.json (seeding, once), RealmQuests.json
// (the_hearth mark), RealmTravel.json (waystones), RealmDominionMap.json; oxide/config/RealmLaws.json (the Hearth zone)
// and RealmArena.json (arena zones), for /arrival admin check.
//
// The arrival gives no items and no marks. Data: if oxide/data/RealmArrival.json exists but cannot be read, routing is
// off (vanilla spawns), ArrivalStage answers none and the file is never written.
// Language level: C# 3 syntax, .NET 3.5 API surface. Cross-plugin methods are non-public (Oxide calls NonPublic|Instance).
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmArrival.md for the first-test plan.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using CodeHatch.Blocks;                            // BlockManager, RootCubeGrid, CubeInfo [DEC]
using CodeHatch.Blocks.Networking.Events;          // CubeDamageEvent, CubePlaceEvent [ASM]
using CodeHatch.Common;                            // Vector3Int, PlayerExtensions [ASM]
using CodeHatch.Damaging;                          // Damage, DamageType [ASM]
using CodeHatch.Engine.Behaviours;                 // CharacterTeleport [DEC]
using CodeHatch.Engine.Core.Cache;                 // Entity [ASM]
using CodeHatch.Engine.Core.Consoles;              // NewsFeed, Severity [DEC]
using CodeHatch.Engine.Modules.PeriodicEffects;    // EffectDefinition [DEC]
using CodeHatch.Engine.Networking;                 // Player, Server [ASM]
using CodeHatch.Networking.Events;                 // EventManager [DEC]
using CodeHatch.Networking.Events.Entities;        // EntityDamageEvent [ASM]
using CodeHatch.Networking.Events.Players;         // PlayerFirstSpawnEvent, PlayerPreSpawnCompleteEvent, PlayerRespawn*Event [DEC]
using CodeHatch.StarForge.Sleeping;                // PlayerSleeperObject [DEC]
using CodeHatch.Thrones.AncientThrone;             // AncientThrone [DEC]
using CodeHatch.UserInterface.Dialogues;           // Dialogue, Options [ASM]
using Oxide.Core;                                  // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                          // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmArrival", "Realm", "0.1.0")]
    [Description("The Gate of the Unwritten: a newcomer's first minutes, from the ferryman's raft to the Hearth fire")]
    public class RealmArrival : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmSentinel;
        [PluginReference] private Plugin RealmTravel;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmEvents;
        [PluginReference] private Plugin RealmArena;
        [PluginReference] private Plugin RealmQuests;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmHerald;

        private const string PermAdmin = "realmarrival.admin";
        private const string PermSkip = "realmarrival.skip";
        private const string DataName = "RealmArrival";
        private const int DataFormat = 1;
        private const string ChatCmdColour = "F4C96D";
        private const string ChatMuted = "A3A6AD";

        // Stages.
        private const string SPending = "pending";
        private const string SCrossing = "crossing";
        private const string SGatehouse = "gatehouse";
        private const string SReleased = "released";
        private const string SBanners = "banners";
        private const string SHearth = "hearth";
        private const string SDone = "done";
        private const string SNone = "none";

        // Variants.
        private const string VNew = "A";
        private const string VVeteran = "B";
        private const string VReturning = "C";

        // The six great houses, in the order of art/palette.json; their pledge stones are stored by these ids.
        private static readonly string[] GreatHouses = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private bool popupsClosed;                       // set on Unload: answers to windows still open are ignored
        private Timer tickTimer;
        private Timer bindTimer;
        private DateTime nextSave = DateTime.MinValue;
        private DateTime nextMinute = DateTime.MinValue;
        private DateTime nextSelfCheck = DateTime.MinValue;
        private int sessionCounter;
        private readonly System.Random rng = new System.Random();

        // Session state (never saved).
        private readonly Dictionary<ulong, Run> runs = new Dictionary<ulong, Run>();
        private readonly Dictionary<ulong, DateTime> shieldUntil = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> inHallSince = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, List<DateTime>> evictions = new Dictionary<ulong, List<DateTime>>();
        private readonly Dictionary<ulong, DateTime> guardAlerted = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<string, List<DateTime>> pledgeTimes = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DateTime> heraldTimes = new List<DateTime>();
        private readonly HashSet<ulong> staleWake = new HashSet<ulong>();          // woke in the hall from an old arrival
        private readonly HashSet<string> firstSightC = new HashSet<string>();      // variant C seen for the first time this session
        private readonly HashSet<ulong> providerMarks = new HashSet<ulong>();      // one-shot marks for the provider wrapper
        private readonly Dictionary<ulong, UnityEngine.Vector3> providerAnswers = new Dictionary<ulong, UnityEngine.Vector3>();
        private readonly Dictionary<ulong, int> sessionOf = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, DateTime> joinedAt = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> placedAt = new Dictionary<ulong, DateTime>();   // stone assignments still landing
        private readonly Dictionary<ulong, int> placedStone = new Dictionary<ulong, int>();
        private readonly Dictionary<string, DateTime> throttle = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, PopupAsk> popupAsks = new Dictionary<string, PopupAsk>();
        private int popupToken;
        private List<Dictionary<string, object>> houseCache;
        private DateTime houseCacheAt = DateTime.MinValue;
        private HashSet<string> waystoneCache;
        private DateTime waystoneCacheAt = DateTime.MinValue;

        // Gate and flare (plugin-owned cells).
        private bool gateClosed;                         // the cells are solid now
        private bool gateBusy;                           // a row animation is running
        private int gateRows;                            // rows currently solid (0..H)
        private bool gateWantOpen;
        private bool gateWantClose;
        private DateTime gateOpenedAt = DateTime.MinValue;
        private DateTime gateCycleAt = DateTime.MinValue;
        private DateTime lastFlare = DateTime.MinValue;
        private bool flaring;
        private bool selfPlacing;

        // Provider mode.
        private ISpawnpointProvider savedProvider;
        private ArrivalProvider installedProvider;

        // Bound by reflection in BindGrid().
        private RootCubeGrid grid;
        private MethodInfo placeMethod, colourMethod;
        private Array rotations;
        private Type colour32Type;
        private string bindError = "not bound yet";

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        #region Config

        private class PluginConfig
        {
            public bool Enabled = true;
            public bool Open = false;                    // staff open it after /arrival admin check passes
            public string RoutingMode = "teleport";      // teleport | provider | road | off
            public float TeleportDelaySeconds = 0f;      // 0..2
            public List<float> ArrivalCheckSeconds = new List<float> { 3f, 10f };
            public string GateMode = "open";             // open | portcullis (after play-test 6)
            public int AutoOpenSeconds = 60;
            public int GateMinOpenSeconds = 15;
            public int GateCycleMinSeconds = 20;
            public float GateClearanceMetres = 3f;
            public float GateRowSeconds = 0.4f;
            public int FlareSeconds = 20;
            public int FlareMinIntervalSeconds = 60;
            public float NarrationStartMoveMetres = 1.0f;
            public int NarrationStartCapSeconds = 12;
            public int NarrationStartMinSeconds = 4;
            public int LineGapSeconds = 4;
            public int QuietAfterHearthSeconds = 6;
            public float BannerDropMetres = 30f;
            public float HandoverRadius = 70f;
            public float RouteCorridorMetres = 20f;
            public float WanderOffRouteMetres = 40f;
            public int TimeoutMinutes = 8;
            public int AfkReleaseMinutes = 30;
            public float ResumeRadius = 60f;
            public int StaleHours = 48;
            public string VeteranMode = "vanilla";       // vanilla | short | full
            public bool StaffToHearth = false;
            public int MercyRespawns = 1;                // 0..3
            public int PledgesPerHousePerHour = 6;
            public int NewcomerBroadcastsPerHour = 6;
            public int ShieldSecondsAfterRelease = 30;
            public int ArrivalZoneGuardMinutes = 15;
            public int EvictSeconds = 3;
            public bool Evict = true;
            public int SiteSelfCheckMinutes = 60;
            public bool UsePopups = true;
            public bool InterruptPopups = false;
            public bool UseNewsToasts = false;
            public string GateSound = "";
            public bool HealAtHearth = true;
            public bool HearthBuff = false;
            public bool UseCompassWords = false;
            public bool HourOneTips = true;
            public bool SeedFromHerald = true;
            public string WrittenDeed = "written";       // RealmRenown deed kind ("" = none)
            public string HearthWaystone = "the-hearth";
            public string RoadsWaystone = "crown-market";
            public bool LogHooks = false;                // play-test 1: log every spawn hook with a timestamp
            public float HallHeightMetres = 16f;
            public float StoneRadius = 1.5f;
            public float StoneJitter = 1.2f;
            public float GoldLineRadius = 2.5f;
            public float BannerRadius = 9f;
            public float PledgeRadius = 1.5f;
            public int PledgeDwellSeconds = 3;
            public float QuietRingRadius = 40f;
            public float HearthStoneRadius = 12f;
            public float WayboardRadius = 8f;
            public int NudgeAfterSeconds = 30;
            public int NudgesPerStage = 2;
            public int PopupAnswerSeconds = 120;
            public int GateMaterialId = 9;               // RealmSculptor role "reinforced" (UNVERIFIED until /sculpt materials)
            public string GateColour = "#131417";        // Iron 900
            public int BeaconMaterialId = 3;             // RealmSculptor role "clay"
            public string EmberDeep = "#9c6a1e";
            public string EmberHot = "#f4c96d";
            public bool WaveMode = false;
            public string WaveUntil = "";                // UTC ISO; wave mode ends by itself
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void ClampConfig()
        {
            if (config == null) config = new PluginConfig();
            if (config.RoutingMode != "teleport" && config.RoutingMode != "provider" && config.RoutingMode != "road" && config.RoutingMode != "off") config.RoutingMode = "teleport";
            if (config.GateMode != "open" && config.GateMode != "portcullis") config.GateMode = "open";
            if (config.VeteranMode != "vanilla" && config.VeteranMode != "short" && config.VeteranMode != "full") config.VeteranMode = "vanilla";
            config.TeleportDelaySeconds = Clamp(config.TeleportDelaySeconds, 0f, 2f);
            if (config.ArrivalCheckSeconds == null || config.ArrivalCheckSeconds.Count == 0) config.ArrivalCheckSeconds = new List<float> { 3f, 10f };
            for (int i = 0; i < config.ArrivalCheckSeconds.Count; i++) config.ArrivalCheckSeconds[i] = Clamp(config.ArrivalCheckSeconds[i], 1f, 60f);
            config.ArrivalCheckSeconds.Sort();
            config.AutoOpenSeconds = ClampI(config.AutoOpenSeconds, 10, 600);
            config.GateMinOpenSeconds = ClampI(config.GateMinOpenSeconds, 5, 300);
            config.GateCycleMinSeconds = ClampI(config.GateCycleMinSeconds, 5, 600);
            config.GateClearanceMetres = Clamp(config.GateClearanceMetres, 1f, 10f);
            config.GateRowSeconds = Clamp(config.GateRowSeconds, 0.1f, 5f);
            config.FlareSeconds = ClampI(config.FlareSeconds, 2, 300);
            config.FlareMinIntervalSeconds = ClampI(config.FlareMinIntervalSeconds, 10, 3600);
            config.NarrationStartMoveMetres = Clamp(config.NarrationStartMoveMetres, 0.2f, 10f);
            config.NarrationStartMinSeconds = ClampI(config.NarrationStartMinSeconds, 0, 30);
            config.NarrationStartCapSeconds = ClampI(config.NarrationStartCapSeconds, config.NarrationStartMinSeconds, 60);
            config.LineGapSeconds = ClampI(config.LineGapSeconds, 1, 30);
            config.QuietAfterHearthSeconds = ClampI(config.QuietAfterHearthSeconds, 0, 60);
            config.BannerDropMetres = Clamp(config.BannerDropMetres, 5f, 200f);
            config.HandoverRadius = Clamp(config.HandoverRadius, 20f, 500f);
            config.RouteCorridorMetres = Clamp(config.RouteCorridorMetres, 5f, 200f);
            config.WanderOffRouteMetres = Clamp(config.WanderOffRouteMetres, 10f, 1000f);
            config.TimeoutMinutes = ClampI(config.TimeoutMinutes, 2, 60);
            config.AfkReleaseMinutes = ClampI(config.AfkReleaseMinutes, 2, 600);
            config.ResumeRadius = Clamp(config.ResumeRadius, 10f, 500f);
            config.StaleHours = ClampI(config.StaleHours, 1, 24 * 30);
            config.MercyRespawns = ClampI(config.MercyRespawns, 0, 3);
            config.PledgesPerHousePerHour = ClampI(config.PledgesPerHousePerHour, 0, 100);
            config.NewcomerBroadcastsPerHour = ClampI(config.NewcomerBroadcastsPerHour, 0, 60);
            config.ShieldSecondsAfterRelease = ClampI(config.ShieldSecondsAfterRelease, 0, 300);
            config.ArrivalZoneGuardMinutes = ClampI(config.ArrivalZoneGuardMinutes, 0, 120);
            config.EvictSeconds = ClampI(config.EvictSeconds, 1, 60);
            config.SiteSelfCheckMinutes = ClampI(config.SiteSelfCheckMinutes, 5, 24 * 60);
            config.HallHeightMetres = Clamp(config.HallHeightMetres, 4f, 60f);
            config.StoneRadius = Clamp(config.StoneRadius, 0.5f, 5f);
            config.StoneJitter = Clamp(config.StoneJitter, 0f, 3f);
            config.GoldLineRadius = Clamp(config.GoldLineRadius, 0.5f, 10f);
            config.BannerRadius = Clamp(config.BannerRadius, 2f, 40f);
            config.PledgeRadius = Clamp(config.PledgeRadius, 0.5f, 5f);
            config.PledgeDwellSeconds = ClampI(config.PledgeDwellSeconds, 1, 30);
            config.QuietRingRadius = Clamp(config.QuietRingRadius, 5f, 200f);
            config.HearthStoneRadius = Clamp(config.HearthStoneRadius, 2f, 100f);
            config.WayboardRadius = Clamp(config.WayboardRadius, 2f, 50f);
            config.NudgeAfterSeconds = ClampI(config.NudgeAfterSeconds, 10, 600);
            config.NudgesPerStage = ClampI(config.NudgesPerStage, 0, 10);
            config.PopupAnswerSeconds = ClampI(config.PopupAnswerSeconds, 10, 600);
            config.GateMaterialId = ClampI(config.GateMaterialId, 1, 254);
            config.BeaconMaterialId = ClampI(config.BeaconMaterialId, 1, 254);
            if (ParseRgb(config.GateColour) < 0) config.GateColour = "#131417";
            if (ParseRgb(config.EmberDeep) < 0) config.EmberDeep = "#9c6a1e";
            if (ParseRgb(config.EmberHot) < 0) config.EmberHot = "#f4c96d";
            if (config.GateSound == null) config.GateSound = "";
            if (config.WrittenDeed == null) config.WrittenDeed = "";
            if (NormalizeId(config.HearthWaystone) == null) config.HearthWaystone = "the-hearth";
            if (NormalizeId(config.RoadsWaystone) == null) config.RoadsWaystone = "crown-market";
            if (config.WaveUntil == null) config.WaveUntil = "";
        }

        private void SaveConfigNow()
        {
            Config.WriteObject(config, true);
        }

        #endregion

        #region Data

        private class Point
        {
            public float X, Y, Z;
        }

        private class Cell
        {
            public int X, Y, Z;
        }

        private class Site
        {
            public List<Point> Stones = new List<Point>();
            public List<DateTime> StoneUsed = new List<DateTime>();
            public List<Point> Mercy = new List<Point>();
            public List<DateTime> MercyUsed = new List<DateTime>();
            public Point Hall1, Hall2, Pad1, Pad2, Eject, Threshold, Hearth, Wayboard, Throne;
            public Dictionary<string, Point> Banners = new Dictionary<string, Point>();
            public bool GateSet;
            public int GateX, GateY, GateZ, GateW, GateH, SpanX, SpanZ;
            public List<Cell> GateCells = new List<Cell>();   // built cells (bottom row first)
            public int BeaconRadius;
            public List<Cell> BeaconCells = new List<Cell>();
            public bool Paused;
            public string PairOrder = "";
        }

        private class Rec
        {
            public string Name = "";
            public string Stage = SPending;
            public string Variant = "";
            public bool Play;                             // /arrival admin play: no Herald line, quests or deed
            public bool Staff;
            public bool Seeded;                           // a veteran seeded from RealmHerald.json
            public DateTime Created;
            public DateTime StageAt;
            public DateTime LastSeen;
            public DateTime T0;                           // the Finish click (OnPlayerSpawned)
            public DateTime ReleasedAt;
            public DateTime WrittenAt;
            public int Sessions;
            public int Banners;                           // bitmask of GreatHouses heard
            public int Pledges;
            public string PledgeHouse = "";
            public bool Healed;
            public bool HeraldSent;
            public bool GateDone;
            public bool RoadMode;
            public bool Unconfirmed;
            public bool TimedOut;
            public bool ToFire;                           // mercy overflow or a death mid-arrival: the fire beats come next
            public bool Struck;                           // struck another player since release (ends shield and guard)
            public bool Tour;
            public int MercyUsed;
            public int Deaths;
            public bool HasLogout;
            public float LX, LY, LZ;
            public int LogoutsSinceArrival;
            public bool RoadsSent, ProtectionSoonSent, FirstBlockSent, DuskSent, SleeperTipSent, PageHintSent, RoadReported, CrownReported;
            public bool BannersReported, HearthReported;
        }

        private class Stats
        {
            public Dictionary<string, int> Counters = new Dictionary<string, int>();
            public Dictionary<string, double> StageSeconds = new Dictionary<string, double>();
            public Dictionary<string, int> StageCounts = new Dictionary<string, int>();
            public Dictionary<string, int> PledgesByHouse = new Dictionary<string, int>();
            public List<int> Timings = new List<int>();   // seconds from Finish to Written, last 10
        }

        private class StoredData
        {
            public int Format = DataFormat;
            public bool Seeded;
            public Dictionary<string, Rec> Players = new Dictionary<string, Rec>();
            public Site Site = new Site();
            public Stats Stats = new Stats();
        }

        private void LoadData()
        {
            loadFailed = false;
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                data = null;
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json (" + ex.Message + "). Arrivals are off (vanilla spawns) and the "
                    + "file will NOT be written. Fix it or move it away, then reload.");
                return;
            }
            if (data == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or truncated. Arrivals are off (vanilla spawns) and the file will NOT be written.");
                return;
            }
            if (data.Format > DataFormat)
            {
                loadFailed = true;
                data = null;
                PrintError("oxide/data/" + DataName + ".json was written by a newer RealmArrival. Arrivals are off; nothing is written.");
                return;
            }
            NormalizeData();
        }

        private void NormalizeData()
        {
            if (data.Players == null) data.Players = new Dictionary<string, Rec>();
            if (data.Site == null) data.Site = new Site();
            if (data.Stats == null) data.Stats = new Stats();
            Site s = data.Site;
            if (s.Stones == null) s.Stones = new List<Point>();
            if (s.Mercy == null) s.Mercy = new List<Point>();
            if (s.StoneUsed == null) s.StoneUsed = new List<DateTime>();
            if (s.MercyUsed == null) s.MercyUsed = new List<DateTime>();
            s.Stones.RemoveAll(delegate(Point p) { return p == null; });
            s.Mercy.RemoveAll(delegate(Point p) { return p == null; });
            while (s.StoneUsed.Count < s.Stones.Count) s.StoneUsed.Add(DateTime.MinValue);
            while (s.MercyUsed.Count < s.Mercy.Count) s.MercyUsed.Add(DateTime.MinValue);
            if (s.StoneUsed.Count > s.Stones.Count) s.StoneUsed.RemoveRange(s.Stones.Count, s.StoneUsed.Count - s.Stones.Count);
            if (s.MercyUsed.Count > s.Mercy.Count) s.MercyUsed.RemoveRange(s.Mercy.Count, s.MercyUsed.Count - s.Mercy.Count);
            if (s.Banners == null) s.Banners = new Dictionary<string, Point>();
            var banners = new Dictionary<string, Point>();
            foreach (KeyValuePair<string, Point> kv in s.Banners)
                if (kv.Value != null && kv.Key != null && Array.IndexOf(GreatHouses, kv.Key.ToLowerInvariant()) >= 0) banners[kv.Key.ToLowerInvariant()] = kv.Value;
            s.Banners = banners;
            if (s.GateCells == null) s.GateCells = new List<Cell>();
            if (s.BeaconCells == null) s.BeaconCells = new List<Cell>();
            s.GateCells.RemoveAll(delegate(Cell c) { return c == null; });
            s.BeaconCells.RemoveAll(delegate(Cell c) { return c == null; });
            if (s.PairOrder == null) s.PairOrder = "";
            Stats st = data.Stats;
            if (st.Counters == null) st.Counters = new Dictionary<string, int>();
            if (st.StageSeconds == null) st.StageSeconds = new Dictionary<string, double>();
            if (st.StageCounts == null) st.StageCounts = new Dictionary<string, int>();
            if (st.PledgesByHouse == null) st.PledgesByHouse = new Dictionary<string, int>();
            if (st.Timings == null) st.Timings = new List<int>();
            var players = new Dictionary<string, Rec>();
            foreach (KeyValuePair<string, Rec> kv in data.Players)
            {
                ulong u;
                if (kv.Value == null || !ulong.TryParse(kv.Key, out u)) continue;
                Rec r = kv.Value;
                if (!IsStage(r.Stage)) r.Stage = SDone;
                if (r.Name == null) r.Name = "";
                if (r.Variant == null) r.Variant = "";
                if (r.PledgeHouse == null) r.PledgeHouse = "";
                players[kv.Key] = r;
            }
            data.Players = players;
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite a file that could not be read
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            nextSave = Now().AddSeconds(2);
        }

        // Seeds known veterans from RealmHerald.json, read-only, once: only records that show real play (a first step done,
        // or last seen at least an hour after first seen), so someone who only quit during creation is not a veteran.
        private class HeraldRec
        {
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public bool Sworn;
            public bool SawCrown;
            public bool TookContract;
            public bool PathDone;
        }

        private class HeraldFile
        {
            public Dictionary<string, HeraldRec> Players;
        }

        private void SeedFromHerald()
        {
            if (loadFailed || data == null || data.Seeded) return;
            data.Seeded = true;
            dirty = true;
            if (!config.SeedFromHerald) return;
            int n = 0;
            try
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile("RealmHerald")) return;     // never create another plugin's file
                HeraldFile f = Interface.Oxide.DataFileSystem.ReadObject<HeraldFile>("RealmHerald");
                if (f == null || f.Players == null) return;
                foreach (KeyValuePair<string, HeraldRec> kv in f.Players)
                {
                    ulong u;
                    HeraldRec h = kv.Value;
                    if (h == null || !ulong.TryParse(kv.Key, out u) || data.Players.ContainsKey(kv.Key)) continue;
                    bool played = h.Sworn || h.SawCrown || h.TookContract || h.PathDone || (h.LastSeen - h.FirstSeen).TotalHours >= 1.0;
                    if (!played) continue;
                    Rec r = new Rec();
                    r.Name = Clean(h.Name, 40);
                    r.Stage = SDone;
                    r.Seeded = true;
                    r.Created = Now();
                    r.StageAt = r.Created;
                    r.LastSeen = h.LastSeen;
                    data.Players[kv.Key] = r;
                    n++;
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read RealmHerald.json for seeding (" + ex.Message + "); no veterans seeded.");
                return;
            }
            if (n > 0) Puts("Seeded " + n + " known veteran(s) from RealmHerald.json.");
        }

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

        // A house name in its chat colour. The six great houses of Ostreval keep their own (art/palette.json
        // "discordRole", chosen for dark backgrounds); any other house gets one of the six by a stable hash of its
        // name, so it always shows in the same colour. Same table in every plugin that uses it (check.mjs).
        private static readonly string[] HouseTintNames = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };
        private static readonly string[] HouseTintColours = { "C58FC0", "E08A5C", "8FB0BF", "B8B85A", "EC8A3C", "6FBF85" };

        private static string HouseTint(string house)
        {
            if (string.IsNullOrEmpty(house)) return house;
            string key = house.Trim().ToLowerInvariant();
            if (key.StartsWith("house ")) key = key.Substring(6).Trim();
            int i = Array.IndexOf(HouseTintNames, key);
            if (i < 0)
            {
                uint h = 2166136261;
                foreach (char c in key) { h ^= c; h *= 16777619; }
                i = (int)(h % (uint)HouseTintColours.Length);
            }
            return "[" + HouseTintColours[i] + "]" + house + "[FFFFFF]";
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Hearth" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },

                // The Gatehouse and the gate
                { "Wake", "Stone underfoot, old smoke on the air. The ferryman's raft is gone. You stand in the Gatehouse of the Unwritten, below the Hearth." },
                { "Naming", "{0}, the Chronicle has no page for you: no house, no oath, no debt. Under the Charter that is enough. The crown belongs to the seat, not the blood." },
                { "CallGate", "Lay your hand on the gate: walk to the gold line before it." },
                { "CallOpen", "The gate stands open. Walk through it, into Ostreval." },
                { "GateSelf", "The gate opens on its own. The Hearth is waiting." },
                { "HeraldGate", "[D6A043]Herald[FFFFFF]: {0} walks out of the Gatehouse of the Unwritten, onto the road to the Hearth." },
                { "ReleasedGate", "You wake by the Gatehouse. The road to the Hearth runs ahead." },

                // Reveal and banners
                { "RevealEmpty", "Six banners line the road to the fire. On the hill beyond, the Old Throne stands empty, and anyone may sit it." },
                { "RevealKing", "Six banners line the road to the fire. On the hill beyond stands the Old Throne, held by {0} of {1}." },
                { "RevealNight", "Fires mark the road through the dark. Six banners line it to the Hearth; beyond, unseen tonight, stands the Old Throne." },
                { "Walk", "Walk the banners. Each house will tell you what it is. Choose none yet; listen first." },
                { "House.varrow", "{0}, the Iron Stag. \"We Stand Our Ground.\" The crown-holders: they take the throne and hold it against the realm." },
                { "House.ashgrove", "{0}, the White Oak. \"Deep Roots, Long Memory.\" Builders and oath-keepers: a steady liege or a loyal vassal." },
                { "House.corvane", "{0}, the Black Raven. \"Every Secret Has a Price.\" Intrigue: many treaties, and the right one broken at the right time." },
                { "House.dunmere", "{0}, the Drowned Bell. \"The Tide Returns.\" The rebels: they declare against whoever reigns and fight in the Lawful Hours." },
                { "House.halloran", "{0}, the Ember Hound. \"Loyal Until the Last Coal.\" Hired swords and fair ransomers, paid by both sides." },
                { "House.merrin", "{0}, the Silver Eel. \"Slip the Net.\" Traders and go-betweens: they run the roads and carry for every side." },
                { "HouseLive", "  [A3A6AD]{0} sworn, {1} awake in the realm, leader {2}. {3}[FFFFFF]" },
                { "LeaderHere", "at hand" },
                { "LeaderAway", "away" },
                { "NoLiege", "Sworn to no one." },
                { "Liege", "Sworn to {0}." },
                { "HouseUnclaimed", "  [A3A6AD]No one has raised this banner yet. Great names go to companies by the claim sign-up.[FFFFFF]" },
                { "PledgeHint", "  [A3A6AD]Stand on a house's stone to look to its banner. It is not an oath; only they can invite you.[FFFFFF]" },
                { "SeekRaven", "A banner is never taken, only given. Send word to a house: [F4C96D]/raven[FFFFFF] <house> <letter>." },
                { "OwnBanner", "  [A3A6AD]Or raise your own:[FFFFFF] [F4C96D]/house found[FFFFFF][A3A6AD]. Or walk free and sell your sword.[FFFFFF]" },
                { "Skipped", "  [A3A6AD]You passed {0} without stopping.[FFFFFF] [F4C96D]/arrival tour[FFFFFF] [A3A6AD]tells you them again.[FFFFFF]" },

                // Pledges
                { "PledgeDwell", "Stand fast to look to House {0}. Step off to walk on." },
                { "PledgeSent", "Word goes to House {0}. If they want you, an officer will invite you; then say [F4C96D]/house join[FFFFFF] {0}." },
                { "PledgeNoneAwake", "No one of House {0} is awake in the realm. Leave a letter: [F4C96D]/raven[FFFFFF] {0} <letter>." },
                { "PledgeToHouse", "{0}, one of the Unwritten, looks to your banner on the Gatehouse road. [F4C96D]/house invite[FFFFFF] {0} brings them in." },
                { "PledgeLimit", "You have looked to a banner already. You may change your mind once." },
                { "PledgeBusy", "House {0} has heard from many of the Unwritten this hour. Send a letter instead: [F4C96D]/raven[FFFFFF] {0} <letter>." },
                { "PledgeCancelled", "  [A3A6AD]You step off the stone and walk on.[FFFFFF]" },

                // The fire and handover
                { "Warm", "You warm your hands at the fire that has not gone out in a hundred winters." },
                { "Kit", "The Hearthkeepers leave a bundle for the Unwritten: wood, stone and food. Take yours: [F4C96D]/kit starter[FFFFFF]." },
                { "Crown", "Whoever sits the Old Throne is the Crown. A sitter can be challenged only by a declared claim, in the Lawful Hours: [F4C96D]/crown[FFFFFF]." },
                { "Shelter", "For your first hour no other player can wound or bind you. Strike, bind or raid a crest, and the shelter ends." },
                { "Unsheltered", "Walk carefully. Out here the blades are real." },
                { "Next", "Your tale has begun at the Hearth. Your first deed waits in [F4C96D]/quest[FFFFFF]." },
                { "NextNoTale", "Tasks wait on the quest-board. Your first deed waits in [F4C96D]/quest[FFFFFF]." },
                { "Written", "You are written. What the Chronicle says of you next is yours. [F4C96D]/realm path[FFFFFF] keeps your first steps." },

                // Nudges, roads and resumes
                { "Nudge", "The fire lies {0} m ahead, at the end of the banners." },
                { "NudgeCompass", "The fire lies {0} m to the {1}." },
                { "Wander", "The Hearth will keep. [F4C96D]/road the-hearth[FFFFFF] leads back." },
                { "RoadMode", "The current carried your raft off course, {0} m from the Hearth. [F4C96D]/road the-hearth[FFFFFF] shows the way to the fire." },
                { "ResumeHall", "You wake again in the Gatehouse. The gate still waits for you." },
                { "ResumeRoad", "You wake again on the road. The fire is {0} m on." },
                { "Veteran", "Welcome back to Ostreval, {0}. The realm was made new; the Hall of Kings remembers." },
                { "SkipDone", "As you wish. The gate is open; the Hearth is yours." },
                { "Mercy", "The Hearth takes you back, once. Raise a bed before you fall again." },
                { "MidDeath", "The Hearth's smoke led you back. The fire is before you." },
                { "Evicted", "The Gatehouse is for the Unwritten." },
                { "RoadsA", "Three roads leave the Hearth: the Crown Market for coin and contracts, the Listing Field for the Ring, the seats for the houses." },
                { "RoadsB", "Walk one: [F4C96D]/road crown-market[FFFFFF]. Your first 3 waystone journeys are free." },
                { "RoadsBNone", "Walk one, and learn its waystone; [F4C96D]/travel[FFFFFF] lists the stones you know." },
                { "NextEvent", "  [A3A6AD]Next in the realm: {0}, in {1}.[FFFFFF]" },

                // Hour one and the page
                { "ProtectionSoon", "Ten minutes of shelter left. Walls, a crest and a bed before dark." },
                { "FirstBlock", "Blocks outside a crest's land decay. Raise a crest before you build much." },
                { "Dusk", "Night is coming, and Ostreval's nights are dark. Carry a torch, or stay by a fire." },
                { "Sleeper", "Your body slept where you stood. Log off behind walls, or it may not be there when you return." },
                { "PageHint", "Your page in the Chronicle so far: [F4C96D]/arrival[FFFFFF]." },
                { "Page", "Your page so far:" },
                { "PageLine", "  House: {0}. Waystones known: {1}. Tale: {2}." },
                { "PageNone", "none yet" },
                { "PageNoTale", "not begun" },

                // /arrival
                { "Closed", "The Gatehouse is closed for now." },
                { "Paused", "Arrivals are off: oxide/data/RealmArrival.json could not be read. An admin must fix or move it, then reload." },
                { "NoPermission", "You may not do that." },
                { "PlayerNotFound", "No such person is online (or the name is ambiguous)." },
                { "Help1", "[F4C96D]/arrival[FFFFFF] - where you are in your arrival and what is next." },
                { "Help2", "  [F4C96D]/arrival skip[FFFFFF] ends it now; [F4C96D]/arrival tour[FFFFFF] tells you the banners, the fire and the roads again." },
                { "HelpAdmin", "  Staff: [F4C96D]/arrival admin[FFFFFF] status | check | open | close | pause | resume | mode | gatemode | stone | mercy | hall | gate | beacon | banner | play" },
                { "StageNow", "Your arrival: {0}." },
                { "Stage.pending", "not begun" },
                { "Stage.crossing", "on the crossing" },
                { "Stage.gatehouse", "in the Gatehouse" },
                { "Stage.released", "through the gate" },
                { "Stage.banners", "on the road of banners" },
                { "Stage.hearth", "at the Hearth" },
                { "Stage.done", "written" },
                { "Stage.none", "not kept by the Hearth" },
                { "NextIs", "  Next: {0}" },
                { "NextGate", "walk to the gold line before the gate." },
                { "NextFire", "walk the banners to the fire." },
                { "NextQuest", "your first deed, in [F4C96D]/quest[FFFFFF]." },
                { "TourOn", "The banners, the fire and the roads will speak again as you pass them." },
                { "NothingToSkip", "You have no arrival to skip." },
                { "NotNow", "Not while your arrival is still being made." },

                // Admin
                { "Usage", "Usage: {0}" },
                { "AdminDone", "Done: {0}" },
                { "AdminRefused", "Refused: {0}" },
                { "AdminStatus1", "Arrival: {0}, mode {1}, gate {2} ({3}), wave {4}, evict {5}, live arrivals {6}." },
                { "AdminStatus2", "  Routed {0}, re-routed {1}, unconfirmed {2}, road mode {3}, released {4}, skipped {5}, written {6}." },
                { "AdminStatus3", "  Popups sent {0}, answered {1}; pledges {2}; evictions {3}; self-check closures {4}; mercy {5}." },
                { "AdminStatus4", "  Mean seconds: gatehouse {0}, banners {1}, hearth {2}. Last timings (s): {3}" },
                { "AdminStatusBind", "  Block grid: {0}" },
                { "CheckOk", "Site check passed: {0} stones, {1} mercy stones, gate {2}, beacon {3} cells." },
                { "CheckProblem", "  [E8913A]Problem:[FFFFFF] {0}" },
                { "CheckWarning", "  [A3A6AD]Note: {0}[FFFFFF]" },
                { "CheckFailed", "Site check: {0} problem(s). [F4C96D]/arrival admin open[FFFFFF] waits until they are fixed." },
                { "Opened", "The Gatehouse is open: newcomers arrive there." },
                { "ClosedNow", "The Gatehouse is closed: new players spawn as the game spawns them. Arrivals under way finish." },
                { "PausedNow", "Arrivals paused: vanilla spawns, the gate held open, every arrival handed back to the realm's normal welcome." },
                { "Resumed", "Arrivals resumed." },
                { "SelfCheckClosed", "The Gatehouse closed itself: {0}" },
                { "PointList", "  {0}. {1}" },
                { "PlayStarted", "{0} is in the Gatehouse (a staff run: no Herald line, quest credit or deed)." },
                { "PlayRefused", "{0} is in a fight or travelling; try again in a moment." }
            }, this);
        }

        // Tone of a key (chat style): done, take care, or news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "PledgeSent", "Written", "SkipDone", "AdminDone", "Opened", "Resumed", "PlayStarted", "TourOn", "CheckOk"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "CallGate", "CallOpen", "GateSelf", "SeekRaven", "PledgeDwell", "PledgeNoneAwake", "PledgeLimit", "PledgeBusy",
            "Shelter", "Unsheltered", "Next", "NextNoTale", "Wander", "RoadMode", "Mercy", "MidDeath", "Evicted", "RoadsB",
            "RoadsBNone", "ProtectionSoon", "FirstBlock", "Dusk", "Sleeper", "ClosedNow", "PausedNow", "SelfCheckClosed",
            "CheckFailed", "AdminRefused", "PlayRefused", "NotNow", "Closed"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
        }

        private string Fmt(string key, Player player, params object[] args)
        {
            string m = Msg(key, player);
            if (args == null || args.Length == 0) return m;
            try { return string.Format(m, args); }
            catch (FormatException) { return m; }               // a server's lang file with a bad placeholder
        }

        private void Reply(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendMessage(Styled(Msg("Speaker", player), ToneOf(key), Fmt(key, player, args)));   // single-string: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        private void Broadcast(string text)
        {
            Server.BroadcastMessage(text);                                       // single-string overload [ASM]
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintError("oxide/config/RealmArrival.json could not be read (" + ex.Message + "); using the defaults for this run.");
                config = null;
            }
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermSkip, this);
            LoadData();
            if (!loadFailed) SeedFromHerald();
        }

        private void OnServerInitialized()
        {
            // Re-sent on hot reload (docs/oxide-rok-api.md 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(1f, SafeTick);
            popupsClosed = false;
            BindGrid();
            if (GridReady()) AfterBind();
            else
            {
                PrintWarning("Block grid not ready (" + bindError + "); retrying every 10 s. Until then the gate stays as the world left it.");
                if (bindTimer != null && !bindTimer.Destroyed) bindTimer.Destroy();
                bindTimer = timer.Every(10f, RetryBind);
            }
            if (loadFailed || data == null) return;
            UpdateProvider();
            // Rebuild each online player's arrival from the saved stage. Queued lines are not restored; the next beat
            // fires on its trigger.
            foreach (Player p in OnlinePlayers())
            {
                Rec rec = RecOf(p);
                if (!sessionOf.ContainsKey(p.Id)) { sessionCounter++; sessionOf[p.Id] = sessionCounter; }
                if (IsRunning(rec.Stage) && !runs.ContainsKey(p.Id)) Rebuild(p, rec);
            }
        }

        private void RetryBind()
        {
            BindGrid();
            if (!GridReady()) return;
            if (bindTimer != null && !bindTimer.Destroyed) bindTimer.Destroy();
            bindTimer = null;
            AfterBind();
        }

        // The gate is always forced open at load: a crash or a world saved while it was closed never traps anyone.
        private void AfterBind()
        {
            ForceGateOpen();
            if (loadFailed || data == null) return;
            SelfCheck();
            nextSelfCheck = Now().AddMinutes(config.SiteSelfCheckMinutes);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            popupsClosed = true;
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            if (bindTimer != null && !bindTimer.Destroyed) bindTimer.Destroy();
            ForceGateOpen();
            EndFlare();
            RestoreProvider();
            shieldUntil.Clear();
            runs.Clear();
            popupAsks.Clear();
            SaveData();
        }

        #endregion

        #region Records and the variant

        private Rec RecOf(Player p)
        {
            string id = p.Id.ToString();
            Rec r;
            if (!data.Players.TryGetValue(id, out r) || r == null)
            {
                r = new Rec();
                r.Created = Now();
                r.StageAt = r.Created;
                r.LastSeen = r.Created;
                data.Players[id] = r;
                dirty = true;
            }
            string name = Clean(p.Name, 40);
            if (name.Length > 0 && r.Name != name) { r.Name = name; dirty = true; }
            return r;
        }

        private Rec FindRec(string id)
        {
            Rec r;
            return data != null && id != null && data.Players.TryGetValue(id, out r) ? r : null;
        }

        private static bool IsStage(string s)
        {
            return s == SPending || s == SCrossing || s == SGatehouse || s == SReleased || s == SBanners || s == SHearth || s == SDone || s == SNone;
        }

        private static bool IsRunning(string s)
        {
            return s == SGatehouse || s == SReleased || s == SBanners || s == SHearth;
        }

        // The arrival routes newcomers now: loaded, enabled, open, not paused, a routing mode.
        private bool Handling()
        {
            return !loadFailed && data != null && config.Enabled && config.Open && !data.Site.Paused && config.RoutingMode != "off";
        }

        private void StageTo(Rec rec, string stage)
        {
            if (rec.Stage == stage) return;
            DateTime now = Now();
            if (IsRunning(rec.Stage) && rec.StageAt != DateTime.MinValue)
            {
                string k = rec.Stage == SReleased ? SBanners : rec.Stage;
                double secs = (now - rec.StageAt).TotalSeconds;
                if (secs >= 0 && secs < 3600)
                {
                    double sum; int n;
                    data.Stats.StageSeconds.TryGetValue(k, out sum);
                    data.Stats.StageCounts.TryGetValue(k, out n);
                    data.Stats.StageSeconds[k] = sum + secs;
                    data.Stats.StageCounts[k] = n + 1;
                }
            }
            rec.Stage = stage;
            rec.StageAt = now;
            dirty = true;
        }

        private void Count(string counter)
        {
            int n;
            data.Stats.Counters.TryGetValue(counter, out n);
            data.Stats.Counters[counter] = n + 1;
            dirty = true;
        }

        private int Counter(string counter)
        {
            int n;
            return data != null && data.Stats.Counters.TryGetValue(counter, out n) ? n : 0;
        }

        private void ResetArrival(Rec rec)
        {
            rec.Play = false; rec.Staff = false; rec.T0 = DateTime.MinValue; rec.ReleasedAt = DateTime.MinValue; rec.WrittenAt = DateTime.MinValue;
            rec.Banners = 0; rec.Pledges = 0; rec.PledgeHouse = ""; rec.Healed = false; rec.HeraldSent = false; rec.GateDone = false;
            rec.RoadMode = false; rec.Unconfirmed = false; rec.TimedOut = false; rec.ToFire = false; rec.Struck = false; rec.Tour = false;
            rec.LogoutsSinceArrival = 0; rec.BannersReported = false; rec.HearthReported = false;
        }

        #endregion

        #region Connect, spawn and the cut

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            try
            {
                DateTime now = Now();
                Rec rec = RecOf(player);
                rec.Sessions++;
                sessionCounter++;
                sessionOf[player.Id] = sessionCounter;
                joinedAt[player.Id] = now;
                // An arrival left midway and not resumed within StaleHours is marked done silently. If the player then
                // wakes in the hall, they are let out to the forecourt (never counted as an eviction).
                if (IsRunning(rec.Stage) && rec.LastSeen != DateTime.MinValue && (now - rec.LastSeen).TotalHours >= config.StaleHours)
                {
                    StageTo(rec, SDone);
                    staleWake.Add(player.Id);
                }
                rec.LastSeen = now;
                dirty = true;
                LogHook("OnPlayerConnected", player, "stage " + rec.Stage);
            }
            catch (Exception ex) { PrintError("OnPlayerConnected: " + ex.Message); }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null) return;
            ulong id = player.Id;
            runs.Remove(id);
            inHallSince.Remove(id);
            placedAt.Remove(id);
            placedStone.Remove(id);
            providerMarks.Remove(id);
            providerAnswers.Remove(id);
            staleWake.Remove(id);
            firstSightC.Remove(id.ToString());
            popupAsks.Remove(id.ToString());
            if (loadFailed || data == null || player.IsServer) return;
            Rec rec = FindRec(id.ToString());
            if (rec == null) return;
            rec.LastSeen = Now();
            UnityEngine.Vector3 pos;
            if (TryPos(player, out pos)) { rec.HasLogout = true; rec.LX = pos.x; rec.LY = pos.y; rec.LZ = pos.z; }
            if (rec.T0 != DateTime.MinValue) rec.LogoutsSinceArrival++;
            dirty = true;
            SaveData();
        }

        // The game's first-spawn event, every session [OPJ L240]. AtFirstSpawn is true while the character still has to be
        // made (the inverse of Character.HasCompletedCreation, per world save) [DEC]. Neither field is ever changed here:
        // moving Position would move the raft, and AtFirstSpawn false breaks creation.
        private void OnPlayerSpawn(PlayerFirstSpawnEvent e)
        {
            if (loadFailed || data == null || e == null) return;
            try
            {
                Player player = e.Player;
                if (player == null || player.IsServer) return;
                Rec rec = RecOf(player);
                ulong id = player.Id;
                string idS = id.ToString();
                LogHook("OnPlayerSpawn", player, "AtFirstSpawn " + e.AtFirstSpawn + ", position " + PosText(e.Position) + ", stage " + rec.Stage);
                if (e.AtFirstSpawn) FirstSpawn(player, rec);
                else Returning(player, rec, e.Position);
                dirty = true;
            }
            catch (Exception ex) { PrintError("OnPlayerSpawn: " + ex.Message); }
        }

        private readonly HashSet<ulong> pendingVet = new HashSet<ulong>();

        private void FirstSpawn(Player player, Rec rec)
        {
            ulong id = player.Id;
            pendingVet.Remove(id);
            providerMarks.Remove(id);
            providerAnswers.Remove(id);
            runs.Remove(id);
            if (HasPerm(player, PermSkip))
            {
                ResetArrival(rec);
                rec.Staff = true;
                StageTo(rec, config.StaffToHearth && Handling() ? SCrossing : SNone);
                return;
            }
            if (rec.Stage == SDone || rec.Seeded && rec.Stage != SCrossing && !IsRunning(rec.Stage) && rec.Stage != SNone)
            {
                // Variant B: a known veteran on a fresh world.
                rec.Variant = VVeteran;
                if (!Handling()) { StageTo(rec, SDone); return; }
                if (config.VeteranMode == "vanilla") { pendingVet.Add(id); StageTo(rec, SDone); return; }
                ResetArrival(rec);
                rec.Variant = VVeteran;
                StageTo(rec, SCrossing);
                providerMarks.Add(id);
                return;
            }
            // Variant A: no record, pending, crossing, an in-arrival stage (the world was reset mid-arrival: restart) or none.
            if (!Handling()) { StageTo(rec, SNone); return; }
            ResetArrival(rec);
            rec.Variant = VNew;
            StageTo(rec, SCrossing);
            providerMarks.Add(id);
        }

        private void Returning(Player player, Rec rec, UnityEngine.Vector3 at)
        {
            ulong id = player.Id;
            string idS = id.ToString();
            if (rec.Stage == SPending)
            {
                // Variant C seen for the first time: a known veteran from now on (a later wipe treats them as B).
                rec.Variant = VReturning;
                StageTo(rec, SDone);
                firstSightC.Add(idS);
                return;
            }
            if (rec.Stage == SCrossing)
            {
                // Creation finished while nobody was watching (the plugin was not loaded at the Finish click).
                StageTo(rec, SDone);
                return;
            }
            if (IsRunning(rec.Stage))
            {
                timer.Once(10f, delegate { Resume(id); });
                return;
            }
            if (staleWake.Contains(id)) timer.Once(2f, delegate { StaleWake(id); });
            ScheduleJoinTips(player, rec);
        }

        // OnPlayerSpawned [OPJ L1244]: once per new character, after the game's own teleport to a random spawn point,
        // while the client shows its "Finalizing World" loader (SpawnColumnHandler.OnPlayerPreSpawnComplete) [DEC].
        // Acts only for a record this plugin marked crossing in OnPlayerSpawn; any repeat or forged event does nothing.
        private void OnPlayerSpawned(PlayerPreSpawnCompleteEvent e)
        {
            if (loadFailed || data == null || e == null) return;
            try
            {
                Player player = e.Player;
                if (player == null || player.IsServer) return;
                Rec rec = FindRec(player.Id.ToString());
                LogHook("OnPlayerSpawned", player, "stage " + (rec != null ? rec.Stage : "(no record)"));
                if (rec == null) return;
                ulong id = player.Id;
                if (pendingVet.Remove(id))
                {
                    if (Handling()) StartVeteranLine(player, rec);
                    return;
                }
                if (rec.Stage != SCrossing) return;
                providerMarks.Remove(id);
                if (!Handling())
                {
                    StageTo(rec, rec.Variant == VVeteran ? SDone : SNone);
                    return;
                }
                if (rec.Staff)
                {
                    StageTo(rec, SNone);
                    int m = PickMercy();
                    if (m >= 0) MoveTo(player, data.Site.Mercy[m], 30f, 0f);
                    return;
                }
                StartArrival(player, rec, false);
            }
            catch (Exception ex) { PrintError("OnPlayerSpawned: " + ex.Message); }
        }

        // T+0: the cut into the Gatehouse.
        private void StartArrival(Player player, Rec rec, bool play)
        {
            DateTime now = Now();
            ulong id = player.Id;
            rec.T0 = now;
            rec.Play = play;
            if (play) { rec.Variant = VNew; }
            StageTo(rec, SGatehouse);
            Count("routed");
            Run run = new Run();
            run.Id = id;
            runs[id] = run;
            run.Since = now;
            run.LastMoveAt = now;
            run.LastProgressAt = now;
            run.Mode = rec.Variant == VVeteran ? config.VeteranMode : "";
            if (config.RoutingMode == "road" || data.Site.Stones.Count == 0 || !HallSet())
            {
                if (data.Site.Stones.Count == 0 || !HallSet()) PrintWarning("No arrival stones or hall box stored: " + Clean(player.Name, 40) + " goes by road mode.");
                StartRoadMode(player, rec, run, false);
                return;
            }
            UnityEngine.Vector3 target;
            bool mercy;
            if (!ChooseTarget(id, out target, out mercy))
            {
                StartRoadMode(player, rec, run, false);
                return;
            }
            run.Placed = target;
            run.HasPlaced = true;
            if (mercy)
            {
                // More newcomers than the stones hold (12): a mercy stone at the Hearth with the short lines.
                rec.ToFire = true;
                rec.GateDone = true;
                rec.ReleasedAt = now;
                StageTo(rec, SBanners);
                Count("overflow");
                MoveNow(player, target, 30f);
                return;
            }
            UnityEngine.Vector3 provided;
            if (config.RoutingMode == "provider" && providerAnswers.TryGetValue(id, out provided))
            {
                providerAnswers.Remove(id);
                run.Placed = provided;                       // the game's own teleport already went there
            }
            else MoveNow(player, target, 30f);
            for (int i = 0; i < config.ArrivalCheckSeconds.Count; i++)
            {
                int k = i;
                timer.Once(config.ArrivalCheckSeconds[i], delegate { ArrivalCheck(id, k); });
            }
        }

        // SentinelGrace and RealmTravel.CancelJourney first, then the teleport in the same tick (or after
        // TeleportDelaySeconds). Never touches PostSpawnPosition: the game's teleport has already run.
        private void MoveNow(Player player, UnityEngine.Vector3 target, float grace)
        {
            Ask(RealmSentinel, "SentinelGrace", player.Id, grace);
            Ask(RealmTravel, "CancelJourney", player.Id.ToString());
            if (config.TeleportDelaySeconds <= 0.01f) { Teleport(player, target); return; }
            ulong id = player.Id;
            timer.Once(config.TeleportDelaySeconds, delegate
            {
                Player p = OnlineById(id);
                if (p != null) Teleport(p, target);
            });
        }

        private void MoveTo(Player player, Point p, float grace, float lift)
        {
            if (p == null) return;
            Ask(RealmSentinel, "SentinelGrace", player.Id, grace);
            Ask(RealmTravel, "CancelJourney", player.Id.ToString());
            Teleport(player, V(p.X, p.Y + lift, p.Z));
        }

        // Arrival checks at T+3 s and T+10 s: outside the hall box, one more teleport (counted unconfirmed); still
        // outside after that, road mode. MovementStatisticCollector sends a player found inside a block to a random
        // spawn [DEC], and a client whose pages are not loaded may stand in the floor for a moment (UNVERIFIED).
        private void ArrivalCheck(ulong id, int k)
        {
            if (loadFailed || data == null) return;
            Run run;
            if (!runs.TryGetValue(id, out run) || run.Road) return;
            Rec rec = FindRec(id.ToString());
            Player p = OnlineById(id);
            if (rec == null || p == null || rec.Stage != SGatehouse || rec.GateDone) return;
            UnityEngine.Vector3 pos;
            if (!TryPos(p, out pos)) return;
            if (InHall(pos, 3f)) return;
            if (!run.Retried)
            {
                run.Retried = true;
                rec.Unconfirmed = true;
                Count("rerouted");
                Count("unconfirmed");
                MoveNow(p, run.Placed, 30f);
                if (k >= config.ArrivalCheckSeconds.Count - 1) timer.Once(3f, delegate { ArrivalCheck(id, k + 1); });
                return;
            }
            StartRoadMode(p, rec, run, true);
        }

        // Road mode: no move (or the move did not hold). The player is released at once and /road the-hearth leads them;
        // the fire beats play if they reach the Hearth within two hours, and the Herald line goes then.
        private void StartRoadMode(Player player, Rec rec, Run run, bool now)
        {
            rec.RoadMode = true;
            rec.GateDone = true;
            rec.ReleasedAt = Now();
            run.Road = true;
            StageTo(rec, SReleased);
            Count("road_mode");
            if (now) Narrate(player, rec, run);
        }

        private void StartVeteranLine(Player player, Rec rec)
        {
            Run run = new Run();
            run.Id = player.Id;
            run.VetOnly = true;
            run.LastMoveAt = Now();
            run.LastProgressAt = run.LastMoveAt;
            UnityEngine.Vector3 pos;
            if (TryPos(player, out pos)) { run.Placed = pos; run.HasPlaced = true; }
            rec.T0 = Now();
            runs[player.Id] = run;
        }

        // Resume after a logoff mid-arrival (variant C with a saved in-arrival stage), 10 s after the spawn.
        private void Resume(ulong id)
        {
            if (loadFailed || data == null) return;
            Player p = OnlineById(id);
            Rec rec = FindRec(id.ToString());
            if (p == null || rec == null || !IsRunning(rec.Stage) || runs.ContainsKey(id)) return;
            Rebuild(p, rec);
            Run run;
            if (!runs.TryGetValue(id, out run)) return;
            UnityEngine.Vector3 pos;
            if (!TryPos(p, out pos)) return;
            DateTime now = Now();
            if (rec.Stage == SGatehouse && InHall(pos, 0f))
            {
                run.Narrating = true;
                run.W0 = now;
                run.AutoOpenAt = now.AddSeconds(20);
                Say(p, "ResumeHall");
                return;
            }
            if (rec.Stage == SGatehouse) { GateMoment(p, rec, run, "resume"); return; }
            if (rec.RoadMode) { run.Narrating = true; return; }
            if (HearthSet() && CorridorOutside(pos) <= config.ResumeRadius - config.RouteCorridorMetres)
            {
                run.Narrating = true;
                Say(p, "ResumeRoad", Round10(FlatDist(pos, HearthV())));
                return;
            }
            runs.Remove(id);
            Say(p, "Wander");
            Handover(p, rec, false);
        }

        // After a reload or a resume: a run for a saved in-arrival stage. Lines already sent are not repeated.
        private void Rebuild(Player p, Rec rec)
        {
            Run run = new Run();
            run.Id = p.Id;
            run.Narrating = rec.Stage != SGatehouse;
            run.W0 = Now();
            run.Since = Now();
            run.AutoOpenAt = Now().AddSeconds(config.AutoOpenSeconds);
            run.LastMoveAt = Now();
            run.LastProgressAt = Now();
            run.Road = rec.RoadMode;
            run.Mode = rec.Variant == VVeteran ? config.VeteranMode : "";
            UnityEngine.Vector3 pos;
            if (TryPos(p, out pos)) { run.Placed = pos; run.HasPlaced = true; }
            if (rec.T0 == DateTime.MinValue) rec.T0 = Now();
            runs[p.Id] = run;
        }

        private void StaleWake(ulong id)
        {
            staleWake.Remove(id);
            Player p = OnlineById(id);
            UnityEngine.Vector3 pos;
            if (p == null || !TryPos(p, out pos) || !InHall(pos, 0f) || data.Site.Eject == null) return;
            MoveTo(p, data.Site.Eject, 20f, 0.5f);
            Say(p, "ReleasedGate");
        }

        #endregion

        #region Respawn

        // OnPlayerRespawn [OPJ L344-L422]: Position only (the game's respawn loader covers the move). Beds and bases are
        // never touched. No items here: InvEquipment.OnPlayerRespawn (VeryLate) rebuilds the inventory afterwards [DEC].
        // Cancel() is never used (it does not stop the game's teleport).
        private void OnPlayerRespawn(PlayerRespawnEvent e)
        {
            if (loadFailed || data == null || e == null) return;
            try
            {
                if (e is PlayerRespawnAtBedEvent || e is PlayerRespawnAtBaseEvent) return;
                Player player = e.Player;
                if (player == null || player.IsServer) return;
                Rec rec = FindRec(player.Id.ToString());
                LogHook("OnPlayerRespawn", player, e.GetType().Name + ", stage " + (rec != null ? rec.Stage : "(no record)"));
                if (rec == null || data.Site.Mercy.Count == 0) return;
                ulong id = player.Id;
                DateTime now = Now();
                if (IsRunning(rec.Stage))
                {
                    rec.Deaths++;
                    int m = PickMercy();
                    if (m < 0) return;
                    Point mp = data.Site.Mercy[m];
                    Ask(RealmSentinel, "SentinelGrace", id, 20f);
                    e.Position = V(mp.X, mp.Y + 0.5f, mp.Z);
                    // Never back into the Gatehouse: the fire beats come next.
                    rec.ToFire = true;
                    if (!rec.GateDone) { rec.GateDone = true; rec.ReleasedAt = now; }
                    if (rec.Stage == SGatehouse || rec.Stage == SReleased) StageTo(rec, SBanners);
                    Run run;
                    if (runs.TryGetValue(id, out run))
                    {
                        run.Queue.Clear();
                        run.AtFire = false;
                        run.InQuiet = false;
                        run.Road = false;
                        run.Narrating = true;
                    }
                    rec.RoadMode = false;
                    Count("mid_death");
                    timer.Once(0.1f, delegate { Say(OnlineById(id), "MidDeath"); });
                    dirty = true;
                    return;
                }
                if (rec.Stage == SDone && rec.Variant == VNew && !rec.Play && rec.WrittenAt != DateTime.MinValue
                    && rec.MercyUsed < config.MercyRespawns && AskBool(RealmWarden, "IsNewPlayerProtected", id))
                {
                    int m = PickMercy();
                    if (m < 0) return;
                    Point mp = data.Site.Mercy[m];
                    rec.MercyUsed++;
                    rec.Deaths++;
                    Ask(RealmSentinel, "SentinelGrace", id, 20f);
                    e.Position = V(mp.X, mp.Y + 0.5f, mp.Z);
                    Count("mercy");
                    timer.Once(0.1f, delegate { Say(OnlineById(id), "Mercy"); });
                    dirty = true;
                }
            }
            catch (Exception ex) { PrintError("OnPlayerRespawn: " + ex.Message); }
        }

        #endregion

        #region Damage: sanctuary, shield and the arrival-zone guard

        // OnEntityHealthChange [OPJ L162]. Blocking follows RealmWarden: evt.Cancel + Damage.Amount = 0 + return true.
        //   Sanctuary  all damage to a player whose stage is gatehouse (or to their sleeper) inside the hall box, and to a
        //              player in arrival on the drop pad. Nobody else gets it, so the hall is no refuge from a fight.
        //   Shield     ShieldSecondsAfterRelease after the gate: other players' blows are turned aside, until the
        //              newcomer strikes someone.
        //   Guard      variant A, released within ArrivalZoneGuardMinutes, on the avenue or at the Hearth, while
        //              RealmWarden does not report protection: other players' blows are turned aside (never a duel blow).
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Entity == null || evt.Damage == null) return null;
            try
            {
                Damage d = evt.Damage;
                if (d.Amount <= 0f || (d.DamageTypes & DamageType.Healing) != 0) return null;
                Player attacker = null;
                Entity src = d.DamageSource;
                if (src != null && src.IsPlayer && src.Owner != null && !src.Owner.IsServer) attacker = src.Owner;
                Player victim = evt.Entity.IsPlayer ? evt.Entity.Owner : null;
                if (victim != null && victim.IsServer) victim = null;
                if (attacker != null && victim != null && attacker.Id == victim.Id) attacker = null;
                DateTime now = Now();
                if (attacker != null && (victim != null || SleeperIdOf(evt.Entity) != 0))
                {
                    shieldUntil.Remove(attacker.Id);              // striking first ends your own shield
                    Rec ar = FindRec(attacker.Id.ToString());
                    if (ar != null && ar.ReleasedAt != DateTime.MinValue && !ar.Struck) { ar.Struck = true; dirty = true; }
                }
                if (evt.Cancelled) return null;
                if (victim == null)
                {
                    ulong sleeper = SleeperIdOf(evt.Entity);
                    if (sleeper == 0) return null;
                    Rec sr = FindRec(sleeper.ToString());
                    UnityEngine.Vector3 sp = evt.Entity.Position;
                    if (sr != null && sr.Stage == SGatehouse && InHall(sp, 0f)) return Block(evt, "RealmArrival sanctuary");
                    return null;
                }
                Rec vr = FindRec(victim.Id.ToString());
                if (vr == null) return null;
                UnityEngine.Vector3 pos;
                if (!TryPos(victim, out pos)) return null;
                if (vr.Stage == SGatehouse && InHall(pos, 0f)) return Block(evt, "RealmArrival sanctuary");
                if (IsRunning(vr.Stage) && InPad(pos)) return Block(evt, "RealmArrival sanctuary");
                if (attacker == null) return null;
                DateTime until;
                if (shieldUntil.TryGetValue(victim.Id, out until) && until > now) return Block(evt, "RealmArrival release shield");
                if (Guarded(vr, victim.Id, pos, now) && !AskBool(RealmArena, "IsDuelBlow", attacker.Id.ToString(), victim.Id.ToString()))
                {
                    DateTime last;
                    if (!guardAlerted.TryGetValue(attacker.Id, out last) || (now - last).TotalMinutes >= 10)
                    {
                        guardAlerted[attacker.Id] = now;
                        Ask(RealmWarden, "RaiseWardenAlert", "arrival_camp", attacker.Id,
                            "struck a newcomer on the Gatehouse road (" + Clean(victim.Name, 40) + "); blow turned aside");
                    }
                    Count("guard_blocks");
                    return Block(evt, "RealmArrival zone guard");
                }
            }
            catch (Exception ex)
            {
                if (!Throttled("dmg", 60)) PrintWarning("Damage watch failed: " + ex.Message);
            }
            return null;
        }

        private static object Block(EntityDamageEvent evt, string why)
        {
            evt.Cancel(why);
            evt.Damage.Amount = 0f;
            return true;
        }

        // Variant A only (never staff runs or veterans), released in the last ArrivalZoneGuardMinutes, has not struck
        // anyone, on the avenue or in the Hearth area, and RealmWarden does not report protection (missing, or its clock
        // ran out). When Warden protects, Warden does the job.
        private bool Guarded(Rec r, ulong id, UnityEngine.Vector3 pos, DateTime now)
        {
            if (r.Variant != VNew || r.Play || r.Struck || r.ReleasedAt == DateTime.MinValue || config.ArrivalZoneGuardMinutes <= 0) return false;
            if ((now - r.ReleasedAt).TotalMinutes >= config.ArrivalZoneGuardMinutes) return false;
            if (!HearthSet() || CorridorOutside(pos) > 0f) return false;
            return !AskBool(RealmWarden, "IsNewPlayerProtected", id);
        }

        // [DEC] PlayerSleeperObject.AllSleeperObjects: sleeper id -> its entity.
        private static ulong SleeperIdOf(Entity e)
        {
            try
            {
                if (e == null || PlayerSleeperObject.AllSleeperObjects == null) return 0;
                foreach (KeyValuePair<ulong, Entity> kv in PlayerSleeperObject.AllSleeperObjects)
                    if (kv.Value != null && ReferenceEquals(kv.Value, e)) return kv.Key;
            }
            catch (Exception) { }
            return 0;
        }

        #endregion

        #region Blocks: protecting the plugin's cells, the first-block tip

        // OnCubeTakeDamage [OPJ L292]: the gate and ember cells take no damage.
        private void OnCubeTakeDamage(CubeDamageEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Cancelled || evt.GridID != 0) return;
            try
            {
                if (!PluginCell(evt.Position)) return;
                Damage d = evt.Damage;
                if (d != null) d.Amount = 0f;
                evt.Cancel("RealmArrival gate");
            }
            catch (Exception ex) { if (!Throttled("cubedmg", 60)) PrintWarning("Cube damage check failed: " + ex.Message); }
        }

        // OnCubePlacement [OPJ L266]: no player builds into the gate or ember cells; this plugin's own calls pass
        // (selfPlacing). The first block a newcomer places in their first two hours earns the FirstBlock tip.
        private void OnCubePlacement(CubePlaceEvent evt)
        {
            if (selfPlacing || loadFailed || data == null || evt == null || evt.Cancelled) return;
            try
            {
                Player sender = evt.Sender;
                if (sender == null || sender.IsServer) return;
                if (evt.GridID == 0 && PluginCell(evt.Position)) { evt.Cancel("RealmArrival gate"); return; }
                if (!config.HourOneTips) return;
                Rec rec = FindRec(sender.Id.ToString());
                if (rec == null || rec.FirstBlockSent || rec.Variant != VNew || rec.Play || rec.T0 == DateTime.MinValue) return;
                if ((Now() - rec.T0).TotalHours >= 2) return;
                rec.FirstBlockSent = true;
                dirty = true;
                Say(sender, "FirstBlock");
            }
            catch (Exception ex) { if (!Throttled("cubeplace", 60)) PrintWarning("Cube placement check failed: " + ex.Message); }
        }

        private bool PluginCell(Vector3Int p)
        {
            foreach (Cell c in data.Site.GateCells) if (c.X == p.x && c.Y == p.y && c.Z == p.z) return true;
            foreach (Cell c in data.Site.BeaconCells) if (c.X == p.x && c.Y == p.y && c.Z == p.z) return true;
            return false;
        }

        #endregion

        #region Commands seen: /quest and /crown

        // OnPlayerCommand [SRC]: observes only, always null. /quest at the fire ends the arrival (Written); /crown during
        // the arrival or its first hour reports arrival_crown. UNVERIFIED that Oxide chat commands pass this hook; the
        // 60 s and 70 m handovers do not depend on it.
        private object OnPlayerCommand(Player player, string command, string[] args)
        {
            if (loadFailed || data == null || player == null || player.IsServer || string.IsNullOrEmpty(command)) return null;
            try
            {
                string c = command.TrimStart('/').ToLowerInvariant();
                Rec rec = FindRec(player.Id.ToString());
                if (rec == null) return null;
                if (c == "quest" && rec.Stage == SHearth) Handover(player, rec, true);
                else if (c == "crown") ReportCrown(player, rec);
            }
            catch (Exception ex) { PrintWarning("OnPlayerCommand: " + ex.Message); }
            return null;
        }

        private void ReportCrown(Player player, Rec rec)
        {
            if (rec.CrownReported || rec.Variant != VNew || rec.Play) return;
            bool inArrival = IsRunning(rec.Stage);
            bool hourOne = rec.Stage == SDone && rec.WrittenAt != DateTime.MinValue && (Now() - rec.WrittenAt).TotalHours < 1;
            if (!inArrival && !hourOne) return;
            rec.CrownReported = true;
            dirty = true;
            Quest(player, rec, "arrival_crown");
        }

        #endregion

        #region The run: narration, gate, banners, fire, handover

        private class Line
        {
            public string Key;
            public object[] Args;
            public string Text;                          // pre-built text (house lines); else Key + Args at send time
            public string Beat = "";                     // gate | banner | fire | ""
            public string House = "";
            public float Proj;                           // banner: the monument's distance along the avenue
            public DateTime NotBefore;
            public string After = "";                    // what sending it does: banners | warm | kit | next | vetdone | heard
        }

        private class Run
        {
            public ulong Id;
            public string Mode = "";                     // B: short | full
            public bool VetOnly;                         // B vanilla: one Veteran line after the loader
            public bool Road;
            public UnityEngine.Vector3 Placed;
            public bool HasPlaced;
            public bool Narrating;
            public DateTime W0;
            public DateTime AutoOpenAt;
            public DateTime PopupAt;
            public bool PopupDone;
            public int Popups;
            public bool Retried;
            public List<Line> Queue = new List<Line>();
            public DateTime NextLineAt;
            public DateTime HoldUntil;
            public UnityEngine.Vector3 Anchor;
            public bool HasAnchor;
            public DateTime LastMoveAt;
            public DateTime LastProgressAt;
            public DateTime LastNudgeAt;
            public DateTime Since;                       // start of this run (the Finish click, or a resume): the timeout counts from here
            public string NudgeStage = "";
            public int Nudges;
            public bool InQuiet;
            public bool AtFire;
            public bool AtWayboard;
            public DateTime NextSentAt;
            public bool NextSent;
            public HashSet<string> Queued = new HashSet<string>();
            public int TourMask;
            public bool ExplainedPledge;
            public bool ExplainedPaths;
            public string DwellHouse = "";
            public DateTime DwellStart;
            public bool DwellAsked;
            public bool DwellDone;
            public bool TourOnly;                        // /arrival tour after the arrival: lines only, no credit
        }

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { if (!Throttled("tick", 60)) PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            if (loadFailed || data == null) return;
            DateTime now = Now();
            if (config.WaveMode && config.WaveUntil.Length > 0)
            {
                DateTime until;
                if (DateTime.TryParse(config.WaveUntil, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until) && now >= until)
                {
                    config.WaveMode = false;
                    config.WaveUntil = "";
                    SaveConfigNow();
                }
            }
            foreach (ulong id in new List<ulong>(runs.Keys))
            {
                Run run;
                if (!runs.TryGetValue(id, out run)) continue;
                Player p = OnlineById(id);
                Rec rec = FindRec(id.ToString());
                if (p == null || rec == null) { runs.Remove(id); continue; }
                try { Step(p, rec, run, now); }
                catch (Exception ex) { if (!Throttled("step|" + id, 60)) PrintError("Arrival step failed for " + Clean(p.Name, 40) + ": " + ex.Message); }
            }
            if (HallSet()) EvictTick(now);
            GateTick(now);
            if (now >= nextMinute)
            {
                nextMinute = now.AddSeconds(60);
                MinuteTick(now);
            }
            if (GridReady() && now >= nextSelfCheck && nextSelfCheck != DateTime.MinValue)
            {
                nextSelfCheck = now.AddMinutes(config.SiteSelfCheckMinutes);
                SelfCheck();
            }
            if (dirty && now >= nextSave) SaveData();
        }

        private void Step(Player p, Rec rec, Run run, DateTime now)
        {
            UnityEngine.Vector3 pos;
            if (!TryPos(p, out pos)) return;
            Moved(run, pos, now);

            if (run.VetOnly)
            {
                if (!run.Narrating && NarrationDue(run, pos, now)) { run.Narrating = true; Say(p, "Veteran", Clean(p.Name, 40)); }
                if (run.Narrating) runs.Remove(p.Id);
                return;
            }
            if (rec.Tour && rec.Stage == SDone) { TourStep(p, rec, run, pos, now); Pump(p, rec, run, now, pos); return; }

            if (!IsRunning(rec.Stage)) { runs.Remove(p.Id); return; }

            if (!run.Narrating)
            {
                if (rec.Stage == SGatehouse || run.Road || rec.ToFire)
                {
                    if (NarrationDue(run, pos, now)) Narrate(p, rec, run);
                }
                else run.Narrating = true;
            }
            if (!run.Narrating) return;                           // nothing is said or triggered under the loader

            // AFK in the hall: after AfkReleaseMinutes without moving, out to the forecourt, written. (A logged-off
            // sleeper stays where it is: the server has no verified way to move a sleeper.)
            if (InHall(pos, 0f) && (now - run.LastMoveAt).TotalMinutes >= config.AfkReleaseMinutes && data.Site.Eject != null)
            {
                MoveTo(p, data.Site.Eject, 20f, 0.5f);
                Count("afk");
                GateMoment(p, rec, run, "afk");
                Handover(p, rec, true);
                return;
            }

            // Overall cap: 8 minutes from the Finish click (two hours in road mode). A player still in the hall gets the
            // gate opened and keeps the hall's sanctuary; they are written as they leave.
            double since = (now - (run.Since != DateTime.MinValue ? run.Since : rec.T0)).TotalMinutes;
            if (run.Road ? since >= 120 : since >= config.TimeoutMinutes)
            {
                if (!run.Road && InHall(pos, 0f))
                {
                    if (!rec.TimedOut) { rec.TimedOut = true; dirty = true; RequestGateOpen(true); }
                }
                else
                {
                    Count("timeout");
                    if (run.Road) { runs.Remove(p.Id); StageTo(rec, SDone); rec.WrittenAt = now; return; }
                    Handover(p, rec, true);
                    return;
                }
            }
            if (rec.TimedOut && rec.Stage != SGatehouse && !InHall(pos, 1f)) { Handover(p, rec, true); return; }

            if (rec.Stage == SGatehouse) GatehouseStep(p, rec, run, pos, now);
            else
            {
                if (!run.Road) BannerStep(p, rec, run, pos, now);
                FireStep(p, rec, run, pos, now);
                if (!IsRunning(rec.Stage)) return;
                if (rec.Stage == SHearth)
                {
                    float fd = FlatDist(pos, HearthV());
                    if (fd > config.HandoverRadius || (run.NextSent && (now - run.NextSentAt).TotalSeconds >= 60)) { Handover(p, rec, true); return; }
                }
                else if (!run.Road && !rec.ToFire && HearthSet() && CorridorOutside(pos) > config.WanderOffRouteMetres)
                {
                    Count("wander");
                    runs.Remove(p.Id);
                    Say(p, "Wander");
                    Handover(p, rec, false);
                    return;
                }
                if (rec.Variant == VVeteran && run.Mode == "short" && !InHall(pos, 1f)) { Handover(p, rec, false); return; }
            }
            if (!IsRunning(rec.Stage)) return;
            Nudge(p, rec, run, pos, now);
            Pump(p, rec, run, now, pos);
        }

        private bool NarrationDue(Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            Rec rec = FindRec(run.Id.ToString());
            DateTime t0 = rec != null && rec.T0 != DateTime.MinValue ? rec.T0 : now;
            double s = (now - t0).TotalSeconds;
            if (s < config.NarrationStartMinSeconds) return false;
            // In the Gatehouse, narration waits until the player is really there (the arrival checks handle the rest).
            if (rec != null && rec.Stage == SGatehouse && !run.Road && !rec.ToFire && HallSet() && !InHall(pos, 3f)) return false;
            if (s >= config.NarrationStartCapSeconds) return true;
            return run.HasPlaced && Dist(pos, run.Placed) > config.NarrationStartMoveMetres;
        }

        private void Narrate(Player p, Rec rec, Run run)
        {
            DateTime now = Now();
            run.Narrating = true;
            run.W0 = now;
            run.AutoOpenAt = now.AddSeconds(config.AutoOpenSeconds);
            run.NextLineAt = now;
            if (run.Road)
            {
                Queue(run, "RoadMode", now, "", HearthSet() ? Round10(FlatDist(PosOf(p), HearthV())) : 0);
                return;
            }
            if (rec.ToFire)
            {
                Queue(run, "Naming", now, "", Clean(p.Name, 40));
                return;
            }
            if (rec.Variant == VVeteran && run.Mode == "short")
            {
                Queue(run, "Veteran", now, "", Clean(p.Name, 40));
                GateMoment(p, rec, run, "veteran");
                return;
            }
            Queue(run, "Wake", now, "gate");
            Queue(run, "Naming", now.AddSeconds(5), "", Clean(p.Name, 40));
            Queue(run, config.GateMode == "open" ? "CallOpen" : "CallGate", now.AddSeconds(10), "gate");
            run.PopupAt = now.AddSeconds(11);
        }

        private void GatehouseStep(Player p, Rec rec, Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            if (!run.Narrating) return;
            bool inHall = InHall(pos, 1f);
            if (!inHall && BoxFlatDistance(pos) > 30f)
            {
                // Far from the hall: not a walk out. Leave it to the arrival checks; once they are over, road mode.
                float last = config.ArrivalCheckSeconds[config.ArrivalCheckSeconds.Count - 1];
                if ((now - rec.T0).TotalSeconds > last + 5) StartRoadMode(p, rec, run, true);
                return;
            }
            if (rec.TimedOut && !inHall) { GateMoment(p, rec, run, "timeout"); Handover(p, rec, true); return; }
            if (data.Site.Threshold != null && Dist(pos, PointV(data.Site.Threshold)) <= config.GoldLineRadius) { GateMoment(p, rec, run, "line"); return; }
            if (!inHall) { GateMoment(p, rec, run, "left"); return; }
            if (now >= run.AutoOpenAt && !rec.TimedOut) { Say(p, "GateSelf"); GateMoment(p, rec, run, "auto"); return; }
            if (!run.PopupDone && run.PopupAt != DateTime.MinValue && now >= run.PopupAt)
            {
                run.PopupDone = true;
                if (!config.WaveMode && run.Popups < 2 && GateCard(p, run)) { run.Popups++; Count("popups_sent"); }
            }
        }

        // The gate moment: the gold line, the card's button, AutoOpenSeconds, or leaving the hall another way.
        private void GateMoment(Player p, Rec rec, Run run, string why)
        {
            if (rec.GateDone) return;
            DateTime now = Now();
            rec.GateDone = true;
            rec.ReleasedAt = now;
            dirty = true;
            Count("released");
            run.Queue.RemoveAll(delegate(Line l) { return l.Beat == "gate"; });
            run.LastProgressAt = now;
            RequestGateOpen(false);
            Flare();
            if (config.GateSound.Length > 0) PlaySound(p);
            if (config.ShieldSecondsAfterRelease > 0) shieldUntil[p.Id] = now.AddSeconds(config.ShieldSecondsAfterRelease);
            if (rec.Variant == VNew && !rec.Play)
            {
                HeraldLine(p, rec);
                Quest(p, rec, "arrival_gate");
            }
            if (rec.TimedOut) return;
            StageTo(rec, SReleased);
            if (rec.Variant == VVeteran && run.Mode == "short") return;
            Queue(run, RevealKey(), now.AddSeconds(3), "", KingName(), KingHouseTinted());
            Line walk = Queue(run, "Walk", now.AddSeconds(8), "");
            walk.After = "banners";
            if (config.UseNewsToasts) Toast(p, Fmt("Walk", p));
        }

        private void HeraldLine(Player p, Rec rec)
        {
            if (rec.HeraldSent || config.NewcomerBroadcastsPerHour <= 0) return;
            rec.HeraldSent = true;
            DateTime now = Now();
            heraldTimes.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
            if (heraldTimes.Count >= config.NewcomerBroadcastsPerHour) return;   // above the cap: dropped silently
            heraldTimes.Add(now);
            Broadcast(Fmt("HeraldGate", null, Clean(p.Name, 40)));
        }

        private string RevealKey()
        {
            if (IsNight()) return "RevealNight";
            return string.IsNullOrEmpty(KingName()) ? "RevealEmpty" : "RevealKing";
        }

        private string KingName()
        {
            string k = AskString(CrownAndConsequences, "GetKingName");
            return string.IsNullOrEmpty(k) ? null : Clean(k, 40);
        }

        private string KingHouseTinted()
        {
            string h = AskString(CrownAndConsequences, "GetKingHouse");
            return string.IsNullOrEmpty(h) ? "no house" : HouseTint(Clean(h, 40));
        }

        // Banners: within BannerRadius of a pledge stone, that house's two lines, once per player (the two houses of a
        // facing pair trigger together, in a shuffled order). Pledge stones: stand still on one to look to the house.
        private void BannerStep(Player p, Rec rec, Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            if (rec.Stage != SReleased && rec.Stage != SBanners && rec.Stage != SHearth) return;
            if (rec.Variant == VVeteran && run.Mode == "short") return;
            var hits = new List<string>();
            foreach (KeyValuePair<string, Point> kv in data.Site.Banners)
            {
                int bit = 1 << Array.IndexOf(GreatHouses, kv.Key);
                if ((rec.Banners & bit) != 0 || run.Queued.Contains(kv.Key)) continue;
                if (Dist(pos, PointV(kv.Value)) <= config.BannerRadius) hits.Add(kv.Key);
            }
            if (hits.Count > 1 && rng.Next(2) == 1) hits.Reverse();
            foreach (string h in hits) QueueBanner(p, run, h, now);
            PledgeStep(p, rec, run, pos, now);
        }

        private void QueueBanner(Player p, Run run, string house, DateTime now)
        {
            run.Queued.Add(house);
            float proj = AxisProj(PointV(data.Site.Banners[house]));
            string display = DisplayHouse(house);
            Line a = new Line();
            a.Text = "[" + ChatGold + "]" + Msg("Speaker", p) + "[FFFFFF]: " + Fmt("House." + house, p, HouseTint(display));   // opens with the tint: style it here
            a.Beat = "banner"; a.House = house; a.Proj = proj; a.NotBefore = now; a.After = "heard";
            run.Queue.Add(a);
            Line b = new Line();
            b.Text = LiveLine(p, house);
            b.Beat = "banner"; b.House = house; b.Proj = proj; b.NotBefore = now;
            run.Queue.Add(b);
        }

        // Line 2 of a banner: live data from RealmHouses (cached 30 s), or the honest line about the claim sign-up.
        private string LiveLine(Player p, string house)
        {
            string name = HouseName(house);
            if (name == null) return Msg("HouseUnclaimed", p);
            int members = 0;
            foreach (Dictionary<string, object> s in Houses())
            {
                object n;
                if (SameText(s.ContainsKey("name") ? s["name"] as string : null, name) && s.TryGetValue("members", out n) && n is int) members = (int)n;
            }
            int awake = 0;
            List<string> ids = AskList(RealmHouses, "GetMembers", name);
            if (ids != null) foreach (string m in ids) { ulong u; if (ulong.TryParse(m, out u) && OnlineById(u) != null) awake++; }
            string leader = AskString(RealmHouses, "GetHouseLeader", name);
            ulong lu;
            bool here = leader != null && ulong.TryParse(leader, out lu) && OnlineById(lu) != null;
            string liege = AskString(RealmHouses, "GetLiege", name);
            string liegeText = string.IsNullOrEmpty(liege) ? Msg("NoLiege", p) : Fmt("Liege", p, Clean(liege, 40));
            return Fmt("HouseLive", p, members, awake, Msg(here ? "LeaderHere" : "LeaderAway", p), liegeText);
        }

        // The RealmHouses name of a great house (the house whose name is the id, or "House <id>"), or null if unclaimed.
        private string HouseName(string house)
        {
            foreach (Dictionary<string, object> s in Houses())
            {
                string n = s.ContainsKey("name") ? s["name"] as string : null;
                if (n == null) continue;
                string k = n.Trim().ToLowerInvariant();
                if (k == house || k == "house " + house) return n;
            }
            return null;
        }

        private string DisplayHouse(string house)
        {
            string n = HouseName(house);
            return n != null ? Clean(n, 40) : Pretty(house);
        }

        private List<Dictionary<string, object>> Houses()
        {
            DateTime now = Now();
            if (houseCache != null && (now - houseCacheAt).TotalSeconds < 30) return houseCache;
            houseCache = Ask(RealmHouses, "GetHouseSummaries") as List<Dictionary<string, object>>;
            if (houseCache == null) houseCache = new List<Dictionary<string, object>>();
            houseCacheAt = now;
            return houseCache;
        }

        // Pledge stones: stand PledgeDwellSeconds to be asked (popup and chat), PledgeDwellSeconds more to confirm by
        // standing; step off to cancel. At most 2 pledges per arrival, PledgesPerHousePerHour per house, variant A only.
        private void PledgeStep(Player p, Rec rec, Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            if (rec.Variant != VNew) return;
            string on = "";
            foreach (KeyValuePair<string, Point> kv in data.Site.Banners)
                if (Dist(pos, PointV(kv.Value)) <= config.PledgeRadius) { on = kv.Key; break; }
            if (on.Length == 0)
            {
                if (run.DwellHouse.Length > 0 && run.DwellAsked && !run.DwellDone) p.SendMessage(Msg("PledgeCancelled", p));
                run.DwellHouse = ""; run.DwellAsked = false; run.DwellDone = false;
                return;
            }
            if (run.DwellHouse != on) { run.DwellHouse = on; run.DwellStart = now; run.DwellAsked = false; run.DwellDone = false; return; }
            if (run.DwellDone) return;
            double held = (now - run.DwellStart).TotalSeconds;
            if (!run.DwellAsked && held >= config.PledgeDwellSeconds)
            {
                run.DwellAsked = true;
                run.LastProgressAt = now;
                if (SameText(rec.PledgeHouse, on)) { run.DwellDone = true; return; }
                string refusal = PledgeRefusal(rec, on);
                if (refusal != null) { run.DwellDone = true; Say(p, refusal, HouseTint(DisplayHouse(on))); return; }
                Say(p, "PledgeDwell", HouseTint(DisplayHouse(on)));
                if (run.Popups < 2 && PledgeCard(p, on)) { run.Popups++; Count("popups_sent"); }
                return;
            }
            if (run.DwellAsked && held >= config.PledgeDwellSeconds * 2)
            {
                run.DwellDone = true;
                DoPledge(p, rec, on);
            }
        }

        private string PledgeRefusal(Rec rec, string house)
        {
            if (rec.Pledges >= 2) return "PledgeLimit";
            List<DateTime> times;
            DateTime now = Now();
            if (pledgeTimes.TryGetValue(house, out times))
            {
                times.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
                if (times.Count >= config.PledgesPerHousePerHour) return "PledgeBusy";
            }
            return null;
        }

        private void DoPledge(Player p, Rec rec, string house)
        {
            if (rec.Variant != VNew || !IsRunning(rec.Stage) || SameText(rec.PledgeHouse, house)) return;
            string refusal = PledgeRefusal(rec, house);
            string tinted = HouseTint(DisplayHouse(house));
            if (refusal != null) { Say(p, refusal, tinted); return; }
            rec.Pledges++;
            rec.PledgeHouse = house;
            dirty = true;
            List<DateTime> times;
            if (!pledgeTimes.TryGetValue(house, out times)) { times = new List<DateTime>(); pledgeTimes[house] = times; }
            times.Add(Now());
            int n;
            data.Stats.PledgesByHouse.TryGetValue(house, out n);
            data.Stats.PledgesByHouse[house] = n + 1;
            Count("pledges");
            if (rec.Play) { Say(p, "PledgeSent", tinted); return; }   // a staff run tells no house
            int told = 0;
            string name = HouseName(house);
            List<string> ids = name != null ? AskList(RealmHouses, "GetMembers", name) : null;
            if (ids != null)
                foreach (string m in ids)
                {
                    ulong u;
                    if (!ulong.TryParse(m, out u) || u == p.Id) continue;
                    Player member = OnlineById(u);
                    if (member == null) continue;
                    Say(member, "PledgeToHouse", Clean(p.Name, 40));
                    told++;
                }
            Say(p, told > 0 ? "PledgeSent" : "PledgeNoneAwake", tinted);
            Quest(p, rec, "arrival_pledge");
        }

        // The fire: the quiet ring (RealmQuests' place radius) holds the queue; the Hearth stone (RealmTravel's waystone
        // radius) starts the fire lines QuietAfterHearthSeconds later, after RealmTravel's own discovery lines.
        private void FireStep(Player p, Rec rec, Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            if (!HearthSet()) return;
            float d = FlatDist(pos, HearthV());
            if (!run.InQuiet && d <= config.QuietRingRadius)
            {
                run.InQuiet = true;
                DateTime hold = now.AddSeconds(config.QuietAfterHearthSeconds);
                if (hold > run.HoldUntil) run.HoldUntil = hold;
            }
            if (run.AtFire || d > config.HearthStoneRadius) return;
            if (rec.Variant == VVeteran && run.Mode == "short") return;
            run.AtFire = true;
            run.LastProgressAt = now;
            if (rec.Variant == VNew && !rec.Play && !rec.HeraldSent && (rec.RoadMode || rec.ToFire)) HeraldLine(p, rec);
            DateTime at = now.AddSeconds(config.QuietAfterHearthSeconds);
            run.Queue.RemoveAll(delegate(Line l) { return l.Beat == "gate"; });
            Queue(run, "*Skipped", at, "fire");                  // decided when it is due: lines still queued may yet be heard
            if (!run.ExplainedPaths)
            {
                run.ExplainedPaths = true;
                Queue(run, "SeekRaven", at, "fire");
                Queue(run, "OwnBanner", at, "fire");
            }
            Queue(run, "Warm", at, "fire").After = "warm";
            Queue(run, "Kit", at, "fire").After = "kit";
            Queue(run, "Crown", at, "fire");
            Queue(run, "*Shelter", at, "fire");
            Queue(run, "*Next", at, "fire").After = "next";
            if (config.UseNewsToasts) Toast(p, Fmt("Warm", p));
        }

        private string SkippedHouses(Rec rec, Run run)
        {
            var names = new List<string>();
            foreach (string h in GreatHouses)
            {
                if (!data.Site.Banners.ContainsKey(h)) continue;
                int bit = 1 << Array.IndexOf(GreatHouses, h);
                if ((rec.Banners & bit) == 0) names.Add(HouseTint(DisplayHouse(h)));
            }
            if (names.Count == 0) return null;
            return string.Join(", ", names.ToArray());
        }

        // The narration queue: at least LineGapSeconds between lines, held while QuietAfterHearthSeconds runs, lines whose
        // beat has passed dropped (a gate line once the gate is open; a banner line once the player is BannerDropMetres
        // beyond that monument along the avenue), so there is never a backlog.
        private void Pump(Player p, Rec rec, Run run, DateTime now, UnityEngine.Vector3 pos)
        {
            float proj = AxisSet() ? AxisProj(pos) : 0f;
            for (int i = run.Queue.Count - 1; i >= 0; i--)
            {
                Line l = run.Queue[i];
                if (l.Beat == "gate" && rec.GateDone) { run.Queue.RemoveAt(i); continue; }
                if (l.Beat == "banner" && AxisSet() && proj > l.Proj + config.BannerDropMetres)
                {
                    run.Queue.RemoveAt(i);
                    run.Queued.Remove(l.House);
                }
            }
            if (run.Queue.Count == 0 || now < run.HoldUntil || now < run.NextLineAt) return;
            Line head = run.Queue[0];
            if (head.NotBefore > now) return;
            run.Queue.RemoveAt(0);
            if (head.Key == "*Skipped")
            {
                string skipped = SkippedHouses(rec, run);
                if (skipped == null) { Pump(p, rec, run, now, pos); return; }   // nothing passed by: no line, no gap
                head.Key = "Skipped";
                head.Args = new object[] { skipped };
            }
            run.NextLineAt = now.AddSeconds(config.LineGapSeconds);
            Send(p, rec, run, head);
        }

        private void Send(Player p, Rec rec, Run run, Line l)
        {
            DateTime now = Now();
            string key = l.Key;
            if (key == "*Shelter") key = AskBool(RealmWarden, "IsNewPlayerProtected", p.Id) ? "Shelter" : "Unsheltered";
            if (key == "*Next") key = TaleRunning(p) ? "Next" : "NextNoTale";
            if (l.Text != null) p.SendMessage(l.Text);
            else Say(p, key, l.Args);
            switch (l.After)
            {
                case "banners":
                    if (rec.Stage == SReleased) StageTo(rec, SBanners);
                    break;
                case "heard":
                    HeardBanner(p, rec, run, l.House);
                    break;
                case "warm":
                    if (config.HealAtHearth && !rec.Healed && !run.TourOnly) { rec.Healed = true; dirty = true; Heal(p); }
                    break;
                case "kit":
                    if (!run.TourOnly && !rec.HearthReported) { rec.HearthReported = true; Quest(p, rec, "arrival_hearth"); }
                    break;
                case "next":
                    run.NextSent = true;
                    run.NextSentAt = now;
                    run.LastProgressAt = now;
                    if (run.TourOnly) { rec.Tour = false; runs.Remove(p.Id); break; }
                    StageTo(rec, SHearth);
                    break;
            }
        }

        private void HeardBanner(Player p, Rec rec, Run run, string house)
        {
            int idx = Array.IndexOf(GreatHouses, house);
            if (idx < 0) return;
            run.Queued.Remove(house);
            run.LastProgressAt = Now();
            if (run.TourOnly) { run.TourMask |= 1 << idx; return; }
            rec.Banners |= 1 << idx;
            dirty = true;
            int heard = Bits(rec.Banners);
            if (heard >= 1 && !run.ExplainedPledge && rec.Variant == VNew)
            {
                run.ExplainedPledge = true;
                QueueAfterHouse(run, house, "PledgeHint");
            }
            if (heard >= 3 && !run.ExplainedPaths)
            {
                run.ExplainedPaths = true;
                Queue(run, "SeekRaven", Now(), "");
                Queue(run, "OwnBanner", Now(), "");
            }
            if (heard >= 3 && !rec.BannersReported) { rec.BannersReported = true; Quest(p, rec, "arrival_banners"); }
        }

        private Line Queue(Run run, string key, DateTime notBefore, string beat, params object[] args)
        {
            Line l = new Line();
            l.Key = key; l.Args = args; l.NotBefore = notBefore; l.Beat = beat;
            run.Queue.Add(l);
            return l;
        }

        // A muted continuation that belongs right after the banner just heard (after its live line, still queued).
        private void QueueAfterHouse(Run run, string house, string key)
        {
            Line l = new Line();
            l.Key = key; l.Args = new object[0]; l.NotBefore = Now();
            int at = 0;
            while (at < run.Queue.Count && run.Queue[at].House == house) at++;
            run.Queue.Insert(at, l);
        }

        // Exactly one current next step; after NudgeAfterSeconds standing still with no progress it is repeated, at most
        // NudgesPerStage times a stage.
        private void Nudge(Player p, Rec rec, Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            if (!run.Narrating || run.Queue.Count > 0 || run.DwellHouse.Length > 0) return;
            if (run.NudgeStage != rec.Stage) { run.NudgeStage = rec.Stage; run.Nudges = 0; }
            if (run.Nudges >= config.NudgesPerStage) return;
            if ((now - run.LastMoveAt).TotalSeconds < config.NudgeAfterSeconds || (now - run.LastProgressAt).TotalSeconds < config.NudgeAfterSeconds
                || (now - run.LastNudgeAt).TotalSeconds < config.NudgeAfterSeconds) return;
            run.Nudges++;
            run.LastNudgeAt = now;
            if (rec.Stage == SGatehouse) { Say(p, rec.GateDone || config.GateMode == "open" ? "CallOpen" : "CallGate"); return; }
            if (rec.Stage == SHearth) { Say(p, TaleRunning(p) ? "Next" : "NextNoTale"); return; }
            if (!HearthSet()) return;
            int metres = Round10(FlatDist(pos, HearthV()));
            if (config.UseCompassWords) Say(p, "NudgeCompass", metres, Compass(pos, HearthV()));
            else Say(p, "Nudge", metres);
        }

        private void Moved(Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            if (!run.HasAnchor) { run.Anchor = pos; run.HasAnchor = true; run.LastMoveAt = now; return; }
            if (Dist(pos, run.Anchor) > 1f) { run.Anchor = pos; run.LastMoveAt = now; }
        }

        // Written: the arrival is done. OwnsArrival turns false; RealmHerald's first steps start from now.
        private void Handover(Player p, Rec rec, bool writtenLine)
        {
            DateTime now = Now();
            runs.Remove(p.Id);
            if (rec.Stage == SDone) return;
            bool variantA = rec.Variant == VNew && !rec.Play;
            StageTo(rec, SDone);
            rec.WrittenAt = now;
            dirty = true;
            Count("written");
            if (rec.T0 != DateTime.MinValue)
            {
                int secs = (int)Math.Round((now - rec.T0).TotalSeconds);
                data.Stats.Timings.Add(secs);
                while (data.Stats.Timings.Count > 10) data.Stats.Timings.RemoveAt(0);
            }
            if (writtenLine && !(rec.Variant == VVeteran && config.VeteranMode == "short")) Say(p, "Written");
            if (variantA && config.WrittenDeed.Length > 0)
                Ask(RealmRenown, "AddDeed", p.Id.ToString(), Clean(p.Name, 40), config.WrittenDeed, "Walked out of the Gatehouse", "arrival:" + p.Id);
        }

        private bool TaleRunning(Player p)
        {
            string s = AskString(RealmQuests, "GetStoryProgress", p.Id.ToString());
            return !string.IsNullOrEmpty(s) && s != "complete";
        }

        // Quest subjects: variant A only, never a staff run.
        private void Quest(Player p, Rec rec, string subject)
        {
            if (rec.Variant != VNew || rec.Play) return;
            Ask(RealmQuests, "ReportQuestEvent", p.Id.ToString(), "custom", subject, 1);
        }

        private void Heal(Player p)
        {
            try
            {
                p.Heal(-1f);
                p.Nourish(-1f);
                p.Hydrate(-1f);
                if (config.HearthBuff && p.Entity != null) EffectDefinition.HealthRegenerationMultiplier.Apply(p.Entity);
            }
            catch (Exception ex) { if (!Throttled("heal", 300)) PrintWarning("Heal at the Hearth failed (" + ex.Message + "); set HealAtHearth false."); }
        }

        private void PlaySound(Player p)
        {
            // [DEC] AudioController.Play(string, Entity, params Player[]): no player list, so bystanders hear it too.
            // Reached by reflection: its other overloads name Unity types the compile check's stub lacks.
            try
            {
                if (p.Entity == null) return;
                MethodInfo m = typeof(AudioController).GetMethod("Play", BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(string), typeof(Entity), typeof(Player[]) }, null);
                if (m != null) m.Invoke(null, new object[] { config.GateSound, p.Entity, new Player[0] });
            }
            catch (Exception ex) { if (!Throttled("sound", 300)) PrintWarning("Gate sound failed: " + ex.Message); }
        }

        private void Toast(Player p, string text)
        {
            try { NewsFeed.SendNews(PopupText(text), new List<Player> { p }, Severity.Low, false, ""); }
            catch (Exception ex) { if (!Throttled("toast", 300)) PrintWarning("Toast failed: " + ex.Message); }
        }

        #endregion

        #region Tour, eviction and hour one

        // /arrival tour after the arrival: the banner, fire and roads beats speak again on their triggers. Never moves anyone.
        private void TourStep(Player p, Rec rec, Run run, UnityEngine.Vector3 pos, DateTime now)
        {
            run.TourOnly = true;
            run.Narrating = true;
            foreach (KeyValuePair<string, Point> kv in data.Site.Banners)
            {
                int bit = 1 << Array.IndexOf(GreatHouses, kv.Key);
                if ((run.TourMask & bit) != 0 || run.Queued.Contains(kv.Key)) continue;
                if (Dist(pos, PointV(kv.Value)) <= config.BannerRadius) QueueBanner(p, run, kv.Key, now);
            }
            if (HearthSet() && !run.AtFire && FlatDist(pos, HearthV()) <= config.HearthStoneRadius)
            {
                run.AtFire = true;
                Queue(run, "Warm", now, "fire").After = "warm";
                Queue(run, "Kit", now, "fire");
                Queue(run, "Crown", now, "fire");
                Queue(run, "*Shelter", now, "fire");
                Queue(run, "*Next", now, "fire").After = "next";
            }
            if (data.Site.Wayboard != null && !run.AtWayboard && Dist(pos, PointV(data.Site.Wayboard)) <= config.WayboardRadius)
            {
                run.AtWayboard = true;
                SendRoads(p);
            }
            if ((now - run.W0).TotalMinutes >= 30) { rec.Tour = false; runs.Remove(p.Id); }
        }

        // Eviction: an online player with no gatehouse stage and no realmarrival.skip who stays in the hall box more than
        // EvictSeconds is moved to the eject point (4 m outside the gate; never a trip). Three in ten minutes raise a
        // Warden alert. A player waking there from an old arrival is let out with ReleasedGate and never counted.
        private void EvictTick(DateTime now)
        {
            foreach (Player p in OnlinePlayers())
            {
                UnityEngine.Vector3 pos;
                if (!TryPos(p, out pos) || !InHall(pos, 0f)) { inHallSince.Remove(p.Id); continue; }
                Rec rec = FindRec(p.Id.ToString());
                if (rec != null && (IsRunning(rec.Stage) || rec.Stage == SCrossing)) { inHallSince.Remove(p.Id); continue; }
                if (staleWake.Contains(p.Id)) continue;          // let out by StaleWake, not evicted
                if (!config.Evict || HasPerm(p, PermSkip) || data.Site.Eject == null) continue;
                DateTime since;
                if (!inHallSince.TryGetValue(p.Id, out since)) { inHallSince[p.Id] = now; continue; }
                if ((now - since).TotalSeconds < config.EvictSeconds) continue;
                inHallSince.Remove(p.Id);
                MoveTo(p, data.Site.Eject, 20f, 0.5f);
                Say(p, "Evicted");
                Count("evictions");
                List<DateTime> list;
                if (!evictions.TryGetValue(p.Id, out list)) { list = new List<DateTime>(); evictions[p.Id] = list; }
                list.RemoveAll(delegate(DateTime t) { return (now - t).TotalMinutes >= 10; });
                list.Add(now);
                if (list.Count >= 3)
                {
                    list.Clear();
                    Ask(RealmWarden, "RaiseWardenAlert", "arrival_camp", p.Id, "evicted from the Gatehouse 3 times in 10 minutes");
                }
            }
        }

        // Once a minute: the quiet hour-one follow-ups (each sent once), for online newcomers (variant A, not staff runs).
        private void MinuteTick(DateTime now)
        {
            foreach (string k in new List<string>(throttle.Keys)) if (throttle[k] < now) throttle.Remove(k);
            foreach (ulong k in new List<ulong>(shieldUntil.Keys)) if (shieldUntil[k] < now) shieldUntil.Remove(k);
            foreach (KeyValuePair<string, PopupAsk> kv in new List<KeyValuePair<string, PopupAsk>>(popupAsks)) if (kv.Value.Expires < now) popupAsks.Remove(kv.Key);
            if (!config.HourOneTips) return;
            foreach (Player p in OnlinePlayers())
            {
                Rec rec = FindRec(p.Id.ToString());
                if (rec == null || rec.Variant != VNew || rec.Play || rec.T0 == DateTime.MinValue) continue;
                if ((now - rec.T0).TotalHours >= 48) continue;
                if (rec.Stage == SDone && rec.WrittenAt != DateTime.MinValue && !rec.RoadsSent && (now - rec.WrittenAt).TotalMinutes >= 10 && !runs.ContainsKey(p.Id))
                {
                    rec.RoadsSent = true;
                    dirty = true;
                    SendRoads(p);
                }
                if (!rec.RoadReported && (now - rec.T0).TotalHours < 2)
                {
                    object n = Ask(RealmTravel, "GetDiscoveredCount", p.Id.ToString());
                    if (n is int && (int)n >= 2) { rec.RoadReported = true; dirty = true; Quest(p, rec, "arrival_road"); }
                }
                bool prot = AskBool(RealmWarden, "IsNewPlayerProtected", p.Id);
                if (prot && !rec.ProtectionSoonSent)
                {
                    object left = Ask(RealmWarden, "GetProtectionMinutesLeft", p.Id);
                    if (left is int && (int)left > 0 && (int)left <= 10) { rec.ProtectionSoonSent = true; dirty = true; Say(p, "ProtectionSoon"); }
                }
                if (prot && !rec.DuskSent && IsDusk()) { rec.DuskSent = true; dirty = true; Say(p, "Dusk"); }
            }
        }

        // The Wayboard reached during or after the arrival (or Written + 10 min): Three Roads, and the next event if it
        // is within 48 h (always as a relative time). Without the crown-market waystone the second line points to /travel.
        private void SendRoads(Player p)
        {
            Say(p, "RoadsA");
            Say(p, WaystoneExists(config.RoadsWaystone) ? "RoadsB" : "RoadsBNone");
            Dictionary<string, object> next = Ask(RealmEvents, "GetNextEvent") as Dictionary<string, object>;
            if (next == null || !next.ContainsKey("at") || !(next["at"] is DateTime)) return;
            double hours = ((DateTime)next["at"] - Now()).TotalHours;
            if (hours <= 0 || hours > 48) return;
            p.SendMessage(Fmt("NextEvent", p, Clean(next.ContainsKey("title") ? next["title"] as string : "", 60), Span(hours)));
        }

        private void ScheduleJoinTips(Player player, Rec rec)
        {
            if (!config.HourOneTips || rec.Variant != VNew || rec.Play || rec.T0 == DateTime.MinValue) return;
            ulong id = player.Id;
            if (rec.LogoutsSinceArrival >= 1 && !rec.SleeperTipSent)
            {
                rec.SleeperTipSent = true;
                dirty = true;
                timer.Once(15f, delegate { Say(OnlineById(id), "Sleeper"); });
            }
            if (rec.Sessions >= 2 && !rec.PageHintSent && rec.Stage == SDone)
            {
                rec.PageHintSent = true;
                dirty = true;
                timer.Once(25f, delegate { Say(OnlineById(id), "PageHint"); });
            }
        }

        #endregion

        #region The gate and the ember flare (plugin-owned cells)

        // Finds the game's block calls once (as RealmSculptor does). Quaternion and Color32 are only ever handled as
        // object, so the plugin does not name a Unity type the compile check's stub lacks.
        private void BindGrid()
        {
            bindError = null;
            try
            {
                grid = BlockManager.DefaultCubeGrid;
                if (grid == null) { bindError = "BlockManager.DefaultCubeGrid is null"; return; }
                Type g = typeof(RootCubeGrid);
                placeMethod = FindMethod(g, "PlaceCubeAtLocal", 7);
                colourMethod = FindMethod(g, "ColorCubeAtLocal", 2);
                FieldInfo rf = typeof(CubeInfo).GetField("PossibleRotations", BindingFlags.Public | BindingFlags.Static);
                rotations = rf != null ? rf.GetValue(null) as Array : null;
                if (placeMethod == null) { bindError = "RootCubeGrid.PlaceCubeAtLocal (7 parameters) not found"; return; }
                if (colourMethod == null) { bindError = "RootCubeGrid.ColorCubeAtLocal not found"; return; }
                if (rotations == null || rotations.Length == 0) { bindError = "CubeInfo.PossibleRotations not found"; return; }
                colour32Type = colourMethod.GetParameters()[1].ParameterType;
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

        private bool GridReady()
        {
            return bindError == null && grid != null && placeMethod != null;
        }

        private int MaterialAt(Cell c)
        {
            return grid.GetCubeInfoAtLocal(new Vector3Int(c.X, c.Y, c.Z)).MaterialID;
        }

        private bool PlaceCell(Cell c, int material)
        {
            selfPlacing = true;
            try
            {
                placeMethod.Invoke(grid, new object[] { new Vector3Int(c.X, c.Y, c.Z), (byte)material, (byte)0, rotations.GetValue(0), false, false, 0.05f });
                return true;
            }
            catch (Exception ex)
            {
                if (!Throttled("place", 60)) PrintWarning("Block write failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return false;
            }
            finally { selfPlacing = false; }
        }

        private void PaintCell(Cell c, string hex)
        {
            int rgb = ParseRgb(hex);
            if (rgb < 0 || colour32Type == null) return;
            try
            {
                object col = Activator.CreateInstance(colour32Type, new object[] { (byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255), (byte)255 });
                colourMethod.Invoke(grid, new object[] { new Vector3Int(c.X, c.Y, c.Z), col });
            }
            catch (Exception ex) { if (!Throttled("paint", 60)) PrintWarning("Block colour failed: " + ex.Message); }
        }

        private Cell CellAt(UnityEngine.Vector3 world)
        {
            Vector3Int v = grid.WorldToLocalCoordinate(world);
            Cell c = new Cell();
            c.X = v.x; c.Y = v.y; c.Z = v.z;
            return c;
        }

        private UnityEngine.Vector3 WorldOf(Cell c)
        {
            return grid.LocalToWorldCoordinate(new Vector3Int(c.X, c.Y, c.Z));
        }

        private bool PortcullisMode()
        {
            return config.GateMode == "portcullis" && data != null && data.Site.GateCells.Count > 0 && GridReady();
        }

        // Rows of the stored gate cells, bottom row first.
        private List<List<Cell>> GateRows()
        {
            var rows = new List<List<Cell>>();
            var byY = new SortedDictionary<int, List<Cell>>();
            foreach (Cell c in data.Site.GateCells)
            {
                List<Cell> l;
                if (!byY.TryGetValue(c.Y, out l)) { l = new List<Cell>(); byY[c.Y] = l; }
                l.Add(c);
            }
            foreach (KeyValuePair<int, List<Cell>> kv in byY) rows.Add(kv.Value);
            return rows;
        }

        // Unload, pause, load and staff: every gate cell becomes air at once. Never traps anyone.
        private void ForceGateOpen()
        {
            gateWantClose = false;
            gateWantOpen = false;
            gateBusy = false;
            if (data == null || data.Site.GateCells.Count == 0 || !GridReady()) { gateClosed = false; gateRows = 0; return; }
            foreach (Cell c in data.Site.GateCells) if (MaterialAt(c) != 0) PlaceCell(c, 0);
            gateClosed = false;
            gateRows = 0;
            gateOpenedAt = Now();
        }

        // Asks the gate to open: now if the cycle allows (GateCycleMinSeconds between openings), else as soon as it does.
        private void RequestGateOpen(bool force)
        {
            if (!PortcullisMode()) return;
            gateWantClose = false;
            if (!gateClosed && gateRows == 0) { gateOpenedAt = Now(); return; }
            gateWantOpen = true;
            if (force) gateCycleAt = DateTime.MinValue;
        }

        private void RequestGateClose()
        {
            if (!PortcullisMode()) return;
            gateWantOpen = false;
            gateWantClose = true;
        }

        // Newcomers inside the hall box: waiting (gatehouse, not timed out) and released (the gate already opened for
        // them, or their 8 minutes ran out). The gate is held open while any released one is still inside, and while two
        // or more wait, so it never closes behind someone who has no way left to open it.
        private void ArrivalsInHall(out int waiting, out int released)
        {
            waiting = 0; released = 0;
            foreach (KeyValuePair<ulong, Run> kv in runs)
            {
                Rec r = FindRec(kv.Key.ToString());
                Player p = OnlineById(kv.Key);
                UnityEngine.Vector3 pos;
                if (r == null || p == null || !IsRunning(r.Stage) || !TryPos(p, out pos) || !InHall(pos, 1f)) continue;
                if (r.Stage == SGatehouse && !r.GateDone && !r.TimedOut) waiting++;
                else released++;
            }
        }

        private bool GateClear()
        {
            foreach (Player p in OnlinePlayers())
            {
                UnityEngine.Vector3 pos;
                if (!TryPos(p, out pos)) continue;
                foreach (Cell c in data.Site.GateCells)
                    if (Dist(pos, WorldOf(c)) <= config.GateClearanceMetres) return false;
            }
            return true;
        }

        // Once a second: start an opening or a closing when one is due. The rows move on their own timer.
        private void GateTick(DateTime now)
        {
            if (!PortcullisMode() || gateBusy) return;
            if (gateWantOpen && gateRows > 0)
            {
                if (now < gateCycleAt) return;
                gateWantOpen = false;
                gateCycleAt = now.AddSeconds(config.GateCycleMinSeconds);
                gateBusy = true;
                timer.Once(0.01f, OpenRow);
                return;
            }
            if (gateWantOpen) { gateWantOpen = false; gateOpenedAt = now; }
            if (gateRows > 0 || data.Site.Paused) return;
            // Open: close again after GateMinOpenSeconds, unless two or more newcomers are inside, wave mode holds it, or
            // the newcomer who opened it is still within reach. A staff "gate close" asks directly.
            bool due = gateWantClose || (now - gateOpenedAt).TotalSeconds >= config.GateMinOpenSeconds;
            int waiting, released;
            ArrivalsInHall(out waiting, out released);
            if (!due || config.WaveMode || waiting >= 2 || released > 0 || !config.Open) return;
            if (!GateClear()) return;                     // retried every tick (at least 2 s apart below)
            gateWantClose = false;
            gateBusy = true;
            timer.Once(0.01f, CloseRow);
        }

        // Top row first, every GateRowSeconds: the gate sinks into the ground.
        private void OpenRow()
        {
            if (data == null || !GridReady()) { gateBusy = false; return; }
            List<List<Cell>> rows = GateRows();
            if (gateRows <= 0 || gateRows > rows.Count) { gateRows = 0; gateClosed = false; gateBusy = false; gateOpenedAt = Now(); return; }
            foreach (Cell c in rows[gateRows - 1]) PlaceCell(c, 0);
            gateRows--;
            if (gateRows == 0) { gateClosed = false; gateBusy = false; gateOpenedAt = Now(); return; }
            timer.Once(config.GateRowSeconds, OpenRow);
        }

        // Bottom row first, with the clearance checked before every row; blocked, it pauses 2 s and tries again. A newcomer
        // who asks while it closes turns it round.
        private void CloseRow()
        {
            if (data == null || !GridReady()) { gateBusy = false; return; }
            if (gateWantOpen) { gateBusy = false; return; }
            List<List<Cell>> rows = GateRows();
            if (gateRows >= rows.Count) { gateRows = rows.Count; gateClosed = true; gateBusy = false; return; }
            if (!GateClear()) { timer.Once(2f, CloseRow); return; }
            foreach (Cell c in rows[gateRows]) { PlaceCell(c, config.GateMaterialId); PaintLater(c, config.GateColour); }
            gateRows++;
            gateClosed = true;
            if (gateRows >= rows.Count) { gateBusy = false; return; }
            timer.Once(config.GateRowSeconds, CloseRow);
        }

        // A colour set before the block exists is lost (CubeInfo.UpdateMaterial resets it) [DEC], so paint a moment later.
        private void PaintLater(Cell c, string hex)
        {
            timer.Once(1.5f, delegate { if (GridReady() && MaterialAt(c) != 0) PaintCell(c, hex); });
        }

        // The ember band flares Ember hot at a gate moment (at most once a FlareMinIntervalSeconds) and fades back.
        private void Flare()
        {
            if (data == null || data.Site.BeaconCells.Count == 0 || !GridReady()) return;
            DateTime now = Now();
            if ((now - lastFlare).TotalSeconds < config.FlareMinIntervalSeconds) return;
            lastFlare = now;
            flaring = true;
            foreach (Cell c in data.Site.BeaconCells) PaintCell(c, config.EmberHot);
            Count("flares");
            timer.Once(config.FlareSeconds, EndFlare);
        }

        private void EndFlare()
        {
            if (!flaring || data == null || !GridReady()) return;
            flaring = false;
            foreach (Cell c in data.Site.BeaconCells) PaintCell(c, config.EmberDeep);
        }

        #endregion

        #region Provider mode

        // RoutingMode provider: the game's own teleport at the Finish click goes to the stone. The wrapper answers
        // GetRandomSpawnPoint() with a stone only while EventManager.CurrentEvent is a PlayerPreSpawnCompleteEvent for a
        // player this plugin marked crossing, once; everything else is the saved provider's answer. The original is put
        // back on Unload, pause and any mode change (SpawnerModHandler.Start and PlayerSpawner.ApplyMod cast the field
        // to PlayerSpawnSelection [DEC]).
        private class ArrivalProvider : ISpawnpointProvider
        {
            public RealmArrival Owner;
            public ISpawnpointProvider Inner;

            public UnityEngine.Vector3 GetSpawnPoint(Entity player) { return Inner.GetSpawnPoint(player); }
            public UnityEngine.Vector3[] GetAllSpawnPoints(Entity player) { return Inner.GetAllSpawnPoints(player); }
            public UnityEngine.Vector3 GetRandomSpawnPoint()
            {
                UnityEngine.Vector3 v;
                if (Owner != null && Owner.ProviderAnswer(out v)) return v;
                return Inner.GetRandomSpawnPoint();
            }
        }

        private bool ProviderAnswer(out UnityEngine.Vector3 v)
        {
            v = new UnityEngine.Vector3();
            try
            {
                if (!Handling() || config.RoutingMode != "provider") return false;
                PlayerPreSpawnCompleteEvent e = EventManager.CurrentEvent as PlayerPreSpawnCompleteEvent;
                if (e == null) return false;
                ulong id = e.PlayerId;
                if (!providerMarks.Remove(id)) return false;          // one shot
                Rec rec = FindRec(id.ToString());
                if (rec == null || rec.Stage != SCrossing || rec.Staff || data.Site.Stones.Count == 0 || !HallSet()) return false;
                bool mercy;
                if (!ChooseTarget(id, out v, out mercy) || mercy) return false;
                providerAnswers[id] = v;
                return true;
            }
            catch (Exception ex)
            {
                if (!Throttled("provider", 60)) PrintWarning("Provider answer failed: " + ex.Message);
                return false;
            }
        }

        private void UpdateProvider()
        {
            if (Handling() && config.RoutingMode == "provider") InstallProvider();
            else RestoreProvider();
        }

        private void InstallProvider()
        {
            try
            {
                ISpawnpointProvider current = SpawnpointManager.defaultSpawnpointProvider;
                if (current == null || current is ArrivalProvider) return;
                ArrivalProvider w = new ArrivalProvider();
                w.Owner = this;
                w.Inner = current;
                savedProvider = current;
                installedProvider = w;
                SpawnpointManager.defaultSpawnpointProvider = w;
            }
            catch (Exception ex) { PrintWarning("Could not install the spawn provider (" + ex.Message + "); teleport mode stands."); }
        }

        private void RestoreProvider()
        {
            try
            {
                if (installedProvider != null && savedProvider != null && ReferenceEquals(SpawnpointManager.defaultSpawnpointProvider, installedProvider))
                    SpawnpointManager.defaultSpawnpointProvider = savedProvider;
            }
            catch (Exception ex) { PrintWarning("Could not restore the spawn provider: " + ex.Message); }
            if (installedProvider != null) installedProvider.Owner = null;
            installedProvider = null;
            savedProvider = null;
        }

        #endregion

        #region Stones

        // Least recently used first. A stone is taken while a player in the Gatehouse stands within StoneRadius of it,
        // or was just put there, or any sleeper lies there. All six taken: each stone takes a second player (with
        // jitter); beyond twelve (or with every stone full), a mercy stone at the Hearth. Wave mode: two per stone at once.
        private bool ChooseTarget(ulong id, out UnityEngine.Vector3 target, out bool mercy)
        {
            target = new UnityEngine.Vector3();
            mercy = false;
            List<Point> stones = data.Site.Stones;
            if (stones.Count == 0) return false;
            int[] occ = new int[stones.Count];
            DateTime now = Now();
            // Each player counts once: one just put on a stone (the move may still be landing) by that stone, everyone
            // else in the Gatehouse and every sleeper by where they stand.
            foreach (ulong k in new List<ulong>(placedAt.Keys)) if ((now - placedAt[k]).TotalSeconds > 5) { placedAt.Remove(k); placedStone.Remove(k); }
            var occupants = new List<UnityEngine.Vector3>();
            foreach (KeyValuePair<ulong, Run> kv in runs)
            {
                if (kv.Key == id) continue;
                int si;
                if (placedStone.TryGetValue(kv.Key, out si) && si < occ.Length) { occ[si]++; continue; }
                Rec r = FindRec(kv.Key.ToString());
                Player p = OnlineById(kv.Key);
                UnityEngine.Vector3 pos;
                if (r != null && r.Stage == SGatehouse && p != null && TryPos(p, out pos)) occupants.Add(pos);
            }
            foreach (UnityEngine.Vector3 s in SleeperPositions()) occupants.Add(s);
            foreach (UnityEngine.Vector3 o in occupants)
                for (int i = 0; i < stones.Count; i++)
                    if (FlatDist(o, PointV(stones[i])) <= config.StoneRadius + config.StoneJitter) { occ[i]++; break; }
            int cap = config.WaveMode ? 2 : 1;
            int pick = PickStone(occ, cap);
            if (pick < 0 && cap == 1) pick = PickStone(occ, 2);
            if (pick < 0)
            {
                int m = PickMercy();
                if (m < 0) return false;
                Point mp = data.Site.Mercy[m];
                target = V(mp.X, mp.Y + 0.5f, mp.Z);
                mercy = true;
                return true;
            }
            Point sp = stones[pick];
            data.Site.StoneUsed[pick] = now;
            placedAt[id] = now;
            placedStone[id] = pick;
            float jx = 0f, jz = 0f;
            if (occ[pick] > 0 || config.WaveMode)
            {
                double a = rng.NextDouble() * Math.PI * 2, r = config.StoneJitter * (0.5 + 0.5 * rng.NextDouble());
                jx = (float)(Math.Cos(a) * r); jz = (float)(Math.Sin(a) * r);
            }
            target = V(sp.X + jx, sp.Y + 0.5f, sp.Z + jz);
            dirty = true;
            return true;
        }

        private int PickStone(int[] occ, int cap)
        {
            int best = -1;
            for (int i = 0; i < occ.Length; i++)
            {
                if (occ[i] >= cap) continue;
                if (best < 0 || occ[i] < occ[best] || occ[i] == occ[best] && data.Site.StoneUsed[i] < data.Site.StoneUsed[best]) best = i;
            }
            return best;
        }

        private int PickMercy()
        {
            List<Point> m = data.Site.Mercy;
            if (m.Count == 0) return -1;
            int best = 0;
            for (int i = 1; i < m.Count; i++) if (data.Site.MercyUsed[i] < data.Site.MercyUsed[best]) best = i;
            data.Site.MercyUsed[best] = Now();
            dirty = true;
            return best;
        }

        private static List<UnityEngine.Vector3> SleeperPositions()
        {
            var list = new List<UnityEngine.Vector3>();
            try
            {
                if (PlayerSleeperObject.AllSleeperObjects == null) return list;
                foreach (KeyValuePair<ulong, Entity> kv in PlayerSleeperObject.AllSleeperObjects)
                    if (kv.Value != null) list.Add(kv.Value.Position);
            }
            catch (Exception) { }
            return list;
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): the game's own windows from CodeHatch.Common.PlayerExtensions
        //   MessageDialogue ShowPopup(this Player, string title, string message, string buttonText, Dialogue.OnSubmit
        //       handler, bool interupt, bool broadcast)
        //   void ShowConfirmPopup(this Player, string title, string message, string confirmText, string cancelText,
        //       Dialogue.OnSubmit handler, bool interupt, bool broadcast)   [ASM]
        // broadcast = true or a dedicated server never sends them. At most two per arrival: the Gatehouse card and the
        // pledge. The chat lines are always sent too. An answer is only a request: it counts once, and only if it matches
        // the token, kind and deadline of the one question open for that player. A forged answer can at most open a gate
        // the player could open by walking 10 m, or confirm a pledge the player was asked about.
        // UNVERIFIED in game: that the windows show, keep their custom labels, and that the answers come back.
        private class PopupAsk
        {
            public int Token;
            public string Kind;
            public string Data;
            public DateTime Expires;
        }

        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.UsePopups || popupsClosed) return false;
            if (RealmHerald == null) return true;
            object wanted = RealmHerald.Call("PopupsWanted", player.Id.ToString());
            return !(wanted is bool) || (bool)wanted;
        }

        private int OpenAsk(string id, string kind, string data2)
        {
            PopupAsk ask = new PopupAsk();
            ask.Token = ++popupToken;
            ask.Kind = kind;
            ask.Data = data2;
            ask.Expires = Now().AddSeconds(config.PopupAnswerSeconds);
            popupAsks[id] = ask;
            return ask.Token;
        }

        private PopupAsk TakeAsk(string id, int token, string kind)
        {
            PopupAsk ask;
            if (popupsClosed || !popupAsks.TryGetValue(id, out ask) || ask.Token != token || ask.Kind != kind) return null;
            popupAsks.Remove(id);
            return ask.Expires >= Now() ? ask : null;
        }

        private bool GateCard(Player p, Run run)
        {
            if (!PopupsFor(p)) return false;
            string id = p.Id.ToString();
            int token = OpenAsk(id, "gate", "");
            string body = "The ferryman has brought you over the Grey Water.\n\nBeyond this gate burns the Hearth, where the houses swore the Charter. "
                + "Six banners line the road to it.\n\nThe crown belongs to the seat, not the blood.";
            try
            {
                p.ShowPopup("The Gatehouse of the Unwritten", body, config.GateMode == "open" ? "Step through" : "Open the Gate",
                    Answered(id, token, "gate"), config.InterruptPopups, true);
                return true;
            }
            catch (Exception ex)
            {
                popupAsks.Remove(id);
                PrintWarning("ShowPopup failed (" + ex.Message + "); the chat line stands.");
                return false;
            }
        }

        private bool PledgeCard(Player p, string house)
        {
            if (!PopupsFor(p)) return false;
            string id = p.Id.ToString();
            int token = OpenAsk(id, "pledge", house);
            string name = PopupText(DisplayHouse(house));
            try
            {
                p.ShowConfirmPopup("Look to House " + name + "?", "Their sworn who are in the realm now will hear that you seek their banner.\n\n"
                    + "This is not an oath. Only they can invite you.", "Look to " + name, "Walk on", Answered(id, token, "pledge"), config.InterruptPopups, true);
                return true;
            }
            catch (Exception ex)
            {
                popupAsks.Remove(id);
                PrintWarning("ShowConfirmPopup failed (" + ex.Message + "); the dwell fallback stands.");
                return false;
            }
        }

        // The game calls this when a window is answered. Never throws back into the game.
        private Dialogue.OnSubmit Answered(string id, int token, string kind)
        {
            return delegate(Options selection, Dialogue dialogue, object context)
            {
                try
                {
                    PopupAsk ask = TakeAsk(id, token, kind);
                    if (ask == null) return;
                    ulong u;
                    if (!ulong.TryParse(id, out u)) return;
                    Player p = OnlineById(u);
                    Rec rec = FindRec(id);
                    if (p == null || rec == null) return;
                    Count("popups_answered");
                    Run run;
                    if (kind == "gate")
                    {
                        if (rec.Stage == SGatehouse && runs.TryGetValue(u, out run) && run.Narrating) GateMoment(p, rec, run, "popup");
                    }
                    else if (kind == "pledge" && (selection & (Options.Yes | Options.OK)) != 0)
                    {
                        if (runs.TryGetValue(u, out run)) run.DwellDone = true;
                        DoPledge(p, rec, ask.Data);
                    }
                }
                catch (Exception ex) { PrintError("Popup answer (" + kind + ") failed: " + ex.Message); }
            };
        }

        // Chat colour tags out, so the window shows plain text.
        private static string PopupText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0) return text;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[' && i + 7 < text.Length && text[i + 7] == ']' && IsChatHex(text.Substring(i + 1, 6))) { i += 7; continue; }
                if (text[i] == '[' && i + 2 < text.Length && text[i + 1] == '-' && text[i + 2] == ']') { i += 2; continue; }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        #endregion

        #region /arrival

        [ChatCommand("arrival")]
        private void CmdArrival(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Rec rec = RecOf(player);
            switch (sub)
            {
                case "":
                case "status":
                    ShowMine(player, rec);
                    return;
                case "skip":
                    Skip(player, rec, false);
                    return;
                case "tour":
                    Tour(player, rec);
                    return;
                case "help":
                    Reply(player, "Help1");
                    player.SendMessage(Msg("Help2", player));
                    if (IsAdmin(player)) player.SendMessage(Msg("HelpAdmin", player));
                    return;
                case "admin":
                    if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
                    CmdAdmin(player, args);
                    return;
                default:
                    Reply(player, "Help1");
                    player.SendMessage(Msg("Help2", player));
                    return;
            }
        }

        private void ShowMine(Player player, Rec rec)
        {
            string stage = rec.Stage;
            if (stage == SDone || stage == SNone)
            {
                Say(player, "Page");
                string house = AskString(RealmHouses, "GetHouse", player.Id.ToString());
                object known = Ask(RealmTravel, "GetDiscoveredCount", player.Id.ToString());
                string tale = AskString(RealmQuests, "GetStoryProgress", player.Id.ToString());
                player.SendMessage(Fmt("PageLine", player, string.IsNullOrEmpty(house) ? Msg("PageNone", player) : HouseTint(Clean(house, 40)),
                    known is int ? (int)known : 0, string.IsNullOrEmpty(tale) ? Msg("PageNoTale", player) : Clean(tale, 80)));
                Dictionary<string, object> next = Ask(RealmEvents, "GetNextEvent") as Dictionary<string, object>;
                if (next != null && next.ContainsKey("at") && next["at"] is DateTime)
                {
                    double hours = ((DateTime)next["at"] - Now()).TotalHours;
                    if (hours > 0 && hours <= 48) player.SendMessage(Fmt("NextEvent", player, Clean(next.ContainsKey("title") ? next["title"] as string : "", 60), Span(hours)));
                }
                return;
            }
            Reply(player, "StageNow", Msg("Stage." + stage, player));
            string nextKey = stage == SGatehouse ? "NextGate" : stage == SHearth ? "NextQuest" : IsRunning(stage) ? "NextFire" : null;
            if (nextKey != null) player.SendMessage(Fmt("NextIs", player, Msg(nextKey, player)));
            player.SendMessage(Msg("Help2", player));
        }

        // Ends the arrival at any stage: queued lines dropped, Written with one line. A player in the hall box is moved to
        // the eject point just outside the gate (5 m; this is not travel).
        private void Skip(Player player, Rec rec, bool byStaff)
        {
            bool running = IsRunning(rec.Stage);
            bool before = rec.Stage == SPending || rec.Stage == SCrossing;
            if (!running && !before) { if (!byStaff) Reply(player, "NothingToSkip"); return; }
            runs.Remove(player.Id);
            providerMarks.Remove(player.Id);
            UnityEngine.Vector3 pos;
            if (TryPos(player, out pos) && InHall(pos, 0f) && data.Site.Eject != null) MoveTo(player, data.Site.Eject, 20f, 0.5f);
            if (!rec.GateDone && running) { rec.GateDone = true; rec.ReleasedAt = Now(); }
            Count("skipped");
            Handover(player, rec, false);
            if (before) { StageTo(rec, SDone); rec.WrittenAt = Now(); }
            Say(player, "SkipDone");
        }

        private void Tour(Player player, Rec rec)
        {
            if (rec.Stage == SPending || rec.Stage == SCrossing) { Reply(player, "NotNow"); return; }
            rec.Tour = true;
            dirty = true;
            Run run;
            if (IsRunning(rec.Stage) && runs.TryGetValue(player.Id, out run))
            {
                rec.Banners = 0;                              // heard again as they pass; quest credit is not repeated
                run.Queued.Clear();
            }
            else if (!IsRunning(rec.Stage))
            {
                run = new Run();
                run.Id = player.Id;
                run.TourOnly = true;
                run.Narrating = true;
                run.W0 = Now();
                run.LastMoveAt = Now();
                run.LastProgressAt = Now();
                runs[player.Id] = run;
            }
            Reply(player, "TourOn");
        }

        #endregion

        #region /arrival admin

        private void CmdAdmin(Player player, string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            string a2 = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            Site s = data.Site;
            UnityEngine.Vector3 here;
            bool hasPos = TryPos(player, out here);
            switch (sub)
            {
                case "status": AdminStatus(player); return;
                case "site":
                case "check": AdminCheck(player); return;
                case "open":
                    {
                        List<string> problems = SiteProblems(new List<string>());
                        if (problems.Count > 0) { foreach (string q in problems) player.SendMessage(Fmt("CheckProblem", player, q)); Reply(player, "CheckFailed", problems.Count); return; }
                        config.Open = true; SaveConfigNow(); UpdateProvider();
                        nextSelfCheck = Now().AddMinutes(config.SiteSelfCheckMinutes);
                        Reply(player, "Opened");
                        return;
                    }
                case "close":
                    config.Open = false; SaveConfigNow(); UpdateProvider();
                    Reply(player, "ClosedNow");
                    return;
                case "pause": Pause(); Reply(player, "PausedNow"); return;
                case "resume":
                    s.Paused = false; dirty = true; UpdateProvider();
                    Reply(player, "Resumed");
                    return;
                case "mode":
                    if (a2 != "teleport" && a2 != "provider" && a2 != "road" && a2 != "off") { Usage(player, "/arrival admin mode teleport|provider|road|off"); return; }
                    config.RoutingMode = a2; SaveConfigNow(); UpdateProvider();
                    Done(player, "mode " + a2);
                    return;
                case "gatemode":
                    if (a2 != "open" && a2 != "portcullis") { Usage(player, "/arrival admin gatemode open|portcullis"); return; }
                    config.GateMode = a2; SaveConfigNow();
                    if (a2 == "open") ForceGateOpen();
                    Done(player, "gatemode " + a2);
                    return;
                case "stone": AdminPoints(player, args, s.Stones, s.StoneUsed, "stone", hasPos, here); return;
                case "mercy": AdminPoints(player, args, s.Mercy, s.MercyUsed, "mercy", hasPos, here); return;
                case "hall":
                case "droppad":
                    {
                        if (!hasPos || (a2 != "corner1" && a2 != "corner2")) { Usage(player, "/arrival admin " + sub + " corner1|corner2"); return; }
                        Point p = P(here);
                        if (sub == "hall") { if (a2 == "corner1") s.Hall1 = p; else s.Hall2 = p; }
                        else { if (a2 == "corner1") s.Pad1 = p; else s.Pad2 = p; }
                        dirty = true;
                        Done(player, sub + " " + a2 + " at " + PosText(here));
                        return;
                    }
                case "eject":
                case "threshold":
                case "hearth":
                case "wayboard":
                case "throne":
                    {
                        if (!hasPos || (a2 != "set" && a2 != "clear")) { Usage(player, "/arrival admin " + sub + " set|clear"); return; }
                        Point p = a2 == "set" ? P(here) : null;
                        if (sub == "eject") s.Eject = p; else if (sub == "threshold") s.Threshold = p; else if (sub == "hearth") s.Hearth = p;
                        else if (sub == "wayboard") s.Wayboard = p; else s.Throne = p;
                        dirty = true;
                        Done(player, sub + " " + (p != null ? "at " + PosText(here) : "cleared"));
                        return;
                    }
                case "banner": AdminBanner(player, args, hasPos, here); return;
                case "gate": AdminGate(player, args, hasPos, here); return;
                case "beacon": AdminBeacon(player, args); return;
                case "evict":
                    if (a2 != "on" && a2 != "off") { Usage(player, "/arrival admin evict on|off"); return; }
                    config.Evict = a2 == "on"; SaveConfigNow();
                    Done(player, "evict " + a2);
                    return;
                case "wave":
                    {
                        int minutes;
                        if (a2 == "off") { config.WaveMode = false; config.WaveUntil = ""; SaveConfigNow(); Done(player, "wave off"); return; }
                        if (a2 != "on" || args.Length < 4 || !int.TryParse(args[3], out minutes) || minutes < 1 || minutes > 600) { Usage(player, "/arrival admin wave on <minutes>|off"); return; }
                        config.WaveMode = true;
                        config.WaveUntil = Now().AddMinutes(minutes).ToString("o", CultureInfo.InvariantCulture);
                        SaveConfigNow();
                        RequestGateOpen(true);
                        Done(player, "wave on for " + minutes + " min");
                        return;
                    }
                case "pairs":
                    s.PairOrder = Clean(JoinFrom(args, 2), 120);
                    dirty = true;
                    Done(player, "pair order '" + s.PairOrder + "'");
                    return;
                case "play":
                case "skip":
                case "reset":
                case "veteran":
                case "pass":
                    AdminPlayer(player, sub, args);
                    return;
                default:
                    Usage(player, "/arrival admin status|check|open|close|pause|resume|mode|gatemode|stone|mercy|hall|droppad|eject|threshold|hearth|wayboard|throne|banner|gate|beacon|evict|wave|pairs|play|skip|reset|veteran|pass");
                    return;
            }
        }

        // Instant global off: vanilla spawns (provider restored), the gate held open, every active arrival handed back
        // (stage none), so RealmHerald's normal welcome reaches them.
        private void Pause()
        {
            data.Site.Paused = true;
            dirty = true;
            RestoreProvider();
            ForceGateOpen();
            EndFlare();
            foreach (KeyValuePair<string, Rec> kv in data.Players)
                if (IsRunning(kv.Value.Stage) || kv.Value.Stage == SCrossing) StageTo(kv.Value, SNone);
            runs.Clear();
            shieldUntil.Clear();
            providerMarks.Clear();
            pendingVet.Clear();
            SaveData();
        }

        private void AdminPoints(Player player, string[] args, List<Point> list, List<DateTime> used, string what, bool hasPos, UnityEngine.Vector3 here)
        {
            string a2 = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            int n;
            switch (a2)
            {
                case "add":
                    if (!hasPos) return;
                    if (list.Count >= 12) { Reply(player, "AdminRefused", "at most 12"); return; }
                    list.Add(P(here)); used.Add(DateTime.MinValue); dirty = true;
                    Done(player, what + " " + list.Count + " at " + PosText(here));
                    return;
                case "remove":
                    if (args.Length < 4 || !int.TryParse(args[3], out n) || n < 1 || n > list.Count) { Usage(player, "/arrival admin " + what + " remove <n>"); return; }
                    list.RemoveAt(n - 1); used.RemoveAt(n - 1); dirty = true;
                    Done(player, what + " " + n + " removed");
                    return;
                case "clear":
                    list.Clear(); used.Clear(); dirty = true;
                    Done(player, what + " list cleared");
                    return;
                case "list":
                    Reply(player, "AdminDone", what + " (" + list.Count + "):");
                    for (int i = 0; i < list.Count; i++) player.SendMessage(Fmt("PointList", player, i + 1, PosText(PointV(list[i]))));
                    return;
                default:
                    Usage(player, "/arrival admin " + what + " add|remove <n>|list|clear");
                    return;
            }
        }

        private void AdminBanner(Player player, string[] args, bool hasPos, UnityEngine.Vector3 here)
        {
            string a2 = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            string house = args.Length > 3 ? args[3].Trim().ToLowerInvariant() : "";
            if (house.StartsWith("house ")) house = house.Substring(6);
            if ((a2 != "set" && a2 != "clear") || Array.IndexOf(GreatHouses, house) < 0)
            {
                Usage(player, "/arrival admin banner set|clear <" + string.Join("|", GreatHouses) + ">");
                return;
            }
            if (a2 == "set") { if (!hasPos) return; data.Site.Banners[house] = P(here); Done(player, "banner " + house + " at " + PosText(here)); }
            else { data.Site.Banners.Remove(house); Done(player, "banner " + house + " cleared"); }
            dirty = true;
        }

        // gate set <w> <h>: stand on the bottom-left cell inside the opening, facing out (away from the hall, toward the
        // Hearth). The gate runs w cells to the right and h cells up. gate build places the 5 x 6 portcullis (only into
        // empty cells), open/close/test move it, remove takes it away (air).
        private void AdminGate(Player player, string[] args, bool hasPos, UnityEngine.Vector3 here)
        {
            string a2 = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            Site s = data.Site;
            if (!GridReady() && a2 != "set") { Reply(player, "AdminRefused", "block grid not ready (" + bindError + ")"); return; }
            switch (a2)
            {
                case "set":
                    {
                        int w, h;
                        if (!hasPos || args.Length < 5 || !int.TryParse(args[3], out w) || !int.TryParse(args[4], out h) || w < 1 || w > 12 || h < 1 || h > 12)
                        { Usage(player, "/arrival admin gate set <w> <h> (stand on the bottom-left cell inside the opening, facing out)"); return; }
                        if (!GridReady()) { Reply(player, "AdminRefused", "block grid not ready (" + bindError + ")"); return; }
                        if (s.GateCells.Count > 0) { Reply(player, "AdminRefused", "remove the built gate first"); return; }
                        int sx, sz;
                        if (!GateSpan(here, out sx, out sz)) { Reply(player, "AdminRefused", "store the hall corners and the hearth first (the gate faces the Hearth)"); return; }
                        Cell c = CellAt(here);
                        s.GateSet = true; s.GateX = c.X; s.GateY = c.Y; s.GateZ = c.Z; s.GateW = w; s.GateH = h; s.SpanX = sx; s.SpanZ = sz;
                        dirty = true;
                        Done(player, "gate " + w + " x " + h + " from cell " + c.X + "," + c.Y + "," + c.Z + " along " + (sx != 0 ? (sx > 0 ? "+x" : "-x") : (sz > 0 ? "+z" : "-z")));
                        return;
                    }
                case "build":
                    {
                        if (!s.GateSet) { Usage(player, "/arrival admin gate set <w> <h> first"); return; }
                        if (s.GateCells.Count > 0) { Reply(player, "AdminRefused", "the gate is built; gate remove first"); return; }
                        int skipped = 0;
                        for (int y = 0; y < s.GateH; y++)
                            for (int i = 0; i < s.GateW; i++)
                            {
                                Cell c = new Cell();
                                c.X = s.GateX + i * s.SpanX; c.Y = s.GateY + y; c.Z = s.GateZ + i * s.SpanZ;
                                if (MaterialAt(c) != 0) { skipped++; continue; }
                                s.GateCells.Add(c);
                            }
                        dirty = true;
                        gateRows = 0; gateClosed = false;
                        Done(player, "gate cells " + s.GateCells.Count + (skipped > 0 ? " (" + skipped + " cells were not empty and are left alone)" : "") + "; closing now");
                        gateOpenedAt = DateTime.MinValue;
                        RequestGateClose();
                        return;
                    }
                case "open": RequestGateOpen(true); Done(player, "gate opening"); return;
                case "close": RequestGateClose(); Done(player, "gate closing (waits while anyone stands within " + config.GateClearanceMetres + " m)"); return;
                case "test":
                    RequestGateOpen(true);
                    Done(player, "gate test: opens now, closes after " + config.GateMinOpenSeconds + " s");
                    return;
                case "remove":
                    ForceGateOpen();
                    s.GateCells.Clear();
                    dirty = true;
                    Done(player, "gate removed (its cells are air)");
                    return;
                default:
                    Usage(player, "/arrival admin gate set <w> <h>|build|open|close|test|remove");
                    return;
            }
        }

        // Facing out = from the hall's centre toward the Hearth, rounded to an axis; the gate runs to the right of that.
        private bool GateSpan(UnityEngine.Vector3 at, out int sx, out int sz)
        {
            sx = 0; sz = 0;
            if (!HallSet() || !HearthSet()) return false;
            UnityEngine.Vector3 c = HallCentre();
            double fx = data.Site.Hearth.X - c.x, fz = data.Site.Hearth.Z - c.z;
            if (Math.Abs(fx) >= Math.Abs(fz)) { fx = Math.Sign(fx); fz = 0; } else { fz = Math.Sign(fz); fx = 0; }
            sx = (int)fz; sz = (int)-fx;                          // right of facing (+z facing: right is +x)
            return sx != 0 || sz != 0;
        }

        // beacon build <radius> [dy]: 24 clay cells on a ring round the stored Hearth centre (cell k at angle 15k degrees,
        // rounded to the grid), resting Ember deep; only empty cells are used. clear: air again. test: one flare.
        private void AdminBeacon(Player player, string[] args)
        {
            string a2 = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            Site s = data.Site;
            if (!GridReady()) { Reply(player, "AdminRefused", "block grid not ready (" + bindError + ")"); return; }
            switch (a2)
            {
                case "build":
                    {
                        int r, dy = 0;
                        if (args.Length < 4 || !int.TryParse(args[3], out r) || r < 2 || r > 20 || (args.Length > 4 && !int.TryParse(args[4], out dy))) { Usage(player, "/arrival admin beacon build <radius 2-20> [dy]"); return; }
                        if (!HearthSet()) { Reply(player, "AdminRefused", "store the hearth first (/arrival admin hearth set)"); return; }
                        if (s.BeaconCells.Count > 0) { Reply(player, "AdminRefused", "the band is built; beacon clear first"); return; }
                        Cell centre = CellAt(HearthV());
                        int skipped = 0;
                        foreach (Cell c in RingCells(centre, r, dy))
                        {
                            if (MaterialAt(c) != 0) { skipped++; continue; }
                            if (PlaceCell(c, config.BeaconMaterialId)) { s.BeaconCells.Add(c); PaintLater(c, config.EmberDeep); }
                        }
                        s.BeaconRadius = r;
                        dirty = true;
                        Done(player, "ember band " + s.BeaconCells.Count + " cells at radius " + r + (skipped > 0 ? " (" + skipped + " not empty, left alone)" : ""));
                        return;
                    }
                case "clear":
                    foreach (Cell c in s.BeaconCells) PlaceCell(c, 0);
                    s.BeaconCells.Clear();
                    dirty = true;
                    Done(player, "ember band cleared");
                    return;
                case "test":
                    lastFlare = DateTime.MinValue;
                    Flare();
                    Done(player, "flare for " + config.FlareSeconds + " s");
                    return;
                default:
                    Usage(player, "/arrival admin beacon build <radius> [dy]|clear|test");
                    return;
            }
        }

        // The ember band's ring: 24 cells, cell k at round(r cos 15k), round(r sin 15k); duplicates dropped. The
        // hearth-ring sculpture leaves the same 24 cells empty at radius 5.
        private static List<Cell> RingCells(Cell centre, int r, int dy)
        {
            var list = new List<Cell>();
            var seen = new HashSet<string>();
            for (int k = 0; k < 24; k++)
            {
                double a = k * Math.PI / 12.0;
                int x = (int)Math.Round(r * Math.Cos(a), MidpointRounding.AwayFromZero), z = (int)Math.Round(r * Math.Sin(a), MidpointRounding.AwayFromZero);
                if (!seen.Add(x + "," + z)) continue;
                Cell c = new Cell();
                c.X = centre.X + x; c.Y = centre.Y + dy; c.Z = centre.Z + z;
                list.Add(c);
            }
            return list;
        }

        private void AdminPlayer(Player player, string sub, string[] args)
        {
            string name = JoinFrom(args, 2);
            Player target = null;
            if (name.Length > 0)
            {
                foreach (Player p in OnlinePlayers())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || p.Id.ToString() == name) { target = p; break; }
                if (target == null)
                {
                    Player hit = null; int hits = 0;
                    foreach (Player p in OnlinePlayers()) if (p.Name != null && p.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { hit = p; hits++; }
                    if (hits == 1) target = hit;
                }
            }
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            Rec rec = RecOf(target);
            string tn = Clean(target.Name, 40);
            switch (sub)
            {
                case "play":
                    if (AskBool(RealmWarden, "IsInCombat", target.Id) || AskBool(RealmTravel, "IsTravelling", target.Id.ToString())) { Reply(player, "PlayRefused", tn); return; }
                    if (data.Site.Stones.Count == 0 || !HallSet()) { Reply(player, "AdminRefused", "store the stones and the hall box first"); return; }
                    runs.Remove(target.Id);
                    ResetArrival(rec);
                    rec.Variant = VNew;
                    StartArrival(target, rec, true);
                    Reply(player, "PlayStarted", tn);
                    return;
                case "skip":
                    Skip(target, rec, true);
                    Done(player, tn + " skipped");
                    return;
                case "reset":
                    {
                        string to = args.Length > 3 && args[args.Length - 1].ToLowerInvariant() == "done" ? SDone : SPending;
                        runs.Remove(target.Id);
                        ResetArrival(rec);
                        rec.Variant = "";
                        rec.Stage = to; rec.StageAt = Now();
                        dirty = true;
                        Done(player, tn + " reset to " + to);
                        return;
                    }
                case "veteran":
                    runs.Remove(target.Id);
                    rec.Stage = SDone; rec.StageAt = Now(); rec.Variant = VReturning;
                    dirty = true;
                    Done(player, tn + " is a known veteran");
                    return;
                case "pass":
                    {
                        Run run;
                        if (rec.Stage == SGatehouse && runs.TryGetValue(target.Id, out run)) { run.Narrating = true; GateMoment(target, rec, run, "staff"); Done(player, "gate opened for " + tn); return; }
                        UnityEngine.Vector3 pos;
                        if (TryPos(target, out pos) && InHall(pos, 0f) && data.Site.Eject != null) { MoveTo(target, data.Site.Eject, 20f, 0.5f); Done(player, tn + " moved to the forecourt"); return; }
                        Reply(player, "AdminRefused", tn + " is not in the Gatehouse");
                        return;
                    }
            }
        }

        private void AdminStatus(Player player)
        {
            int live = 0;
            foreach (KeyValuePair<string, Rec> kv in data.Players) if (IsRunning(kv.Value.Stage)) live++;
            string state = data.Site.Paused ? "paused" : config.Open ? "open" : "closed";
            string gate = data.Site.GateCells.Count == 0 ? "not built" : gateRows > 0 ? (gateBusy ? "moving" : "closed") : (gateBusy ? "moving" : "open");
            Reply(player, "AdminStatus1", state, config.RoutingMode, config.GateMode, gate, config.WaveMode ? "on" : "off", config.Evict ? "on" : "off", live);
            player.SendMessage(Fmt("AdminStatus2", player, Counter("routed"), Counter("rerouted"), Counter("unconfirmed"), Counter("road_mode"),
                Counter("released"), Counter("skipped"), Counter("written")));
            var pl = new List<string>();
            foreach (KeyValuePair<string, int> kv in data.Stats.PledgesByHouse) pl.Add(kv.Key + " " + kv.Value);
            player.SendMessage(Fmt("AdminStatus3", player, Counter("popups_sent"), Counter("popups_answered"), pl.Count > 0 ? string.Join(", ", pl.ToArray()) : "0",
                Counter("evictions"), Counter("self_check_closures"), Counter("mercy")));
            var t = new List<string>();
            foreach (int x in data.Stats.Timings) t.Add(x.ToString(CultureInfo.InvariantCulture));
            player.SendMessage(Fmt("AdminStatus4", player, Mean(SGatehouse), Mean(SBanners), Mean(SHearth), t.Count > 0 ? string.Join(" ", t.ToArray()) : "-"));
            player.SendMessage(Fmt("AdminStatusBind", player, GridReady() ? "bound" : bindError));
        }

        private string Mean(string stage)
        {
            double sum; int n;
            if (!data.Stats.StageSeconds.TryGetValue(stage, out sum) || !data.Stats.StageCounts.TryGetValue(stage, out n) || n == 0) return "-";
            return Math.Round(sum / n).ToString(CultureInfo.InvariantCulture);
        }

        private void AdminCheck(Player player)
        {
            var warnings = new List<string>();
            List<string> problems = SiteProblems(warnings);
            foreach (string q in problems) player.SendMessage(Fmt("CheckProblem", player, q));
            foreach (string w in warnings) player.SendMessage(Fmt("CheckWarning", player, w));
            if (problems.Count > 0) { Reply(player, "CheckFailed", problems.Count); return; }
            Reply(player, "CheckOk", data.Site.Stones.Count, data.Site.Mercy.Count, data.Site.GateCells.Count > 0 ? "built" : "open arch", data.Site.BeaconCells.Count);
        }

        private void Done(Player player, string what) { Reply(player, "AdminDone", what); }

        private void Usage(Player player, string usage) { ReplyError(player, "Usage", "[" + ChatCmdColour + "]" + usage + "[FFFFFF]"); }

        #endregion

        #region Site check and self-check

        // /arrival admin check, and open: every stored point, the floors, and the site's place in the realm.
        private List<string> SiteProblems(List<string> warnings)
        {
            var problems = new List<string>();
            Site s = data.Site;
            if (s.Stones.Count == 0) problems.Add("no arrival stones (stone add; 6 recommended)");
            if (s.Mercy.Count == 0) problems.Add("no mercy stones (mercy add; 3 recommended)");
            if (!HallSet()) problems.Add("hall box not set (hall corner1, hall corner2)");
            if (s.Eject == null) problems.Add("eject point not set (eject set)");
            if (s.Threshold == null) problems.Add("gold line not set (threshold set)");
            if (s.Hearth == null) problems.Add("hearth not set (hearth set)");
            if (s.Pad1 == null || s.Pad2 == null) warnings.Add("drop pad not set: the Pilgrim's Drop has no sanctuary");
            if (s.Wayboard == null) warnings.Add("wayboard not set: Three Roads comes 10 minutes after the arrival instead");
            if (s.Banners.Count < 6) warnings.Add(s.Banners.Count + " of 6 pledge stones set (banner set <house>)");
            if (config.GateMode == "portcullis" && s.GateCells.Count == 0) problems.Add("gatemode portcullis but no gate built (gate set, gate build)");
            if (GridReady())
            {
                for (int i = 0; i < s.Stones.Count; i++) FloorCheck("stone " + (i + 1), s.Stones[i], true, problems, warnings);
                for (int i = 0; i < s.Mercy.Count; i++) FloorCheck("mercy stone " + (i + 1), s.Mercy[i], false, problems, warnings);
                if (s.Eject != null) FloorCheck("eject point", s.Eject, false, problems, warnings);
            }
            else problems.Add("block grid not ready (" + bindError + "): floors cannot be checked");
            if (HallSet() && HearthSet())
            {
                UnityEngine.Vector3 hc = HallCentre();
                float zone = HearthZoneRadius();
                if (FlatDist(hc, HearthV()) <= zone) problems.Add("the hall box lies inside the Hearth town zone");
                foreach (Point st in s.Stones) if (FlatDist(PointV(st), HearthV()) <= zone) { problems.Add("an arrival stone lies inside the Hearth town zone"); break; }
            }
            UnityEngine.Vector3 throne;
            if (HallSet() && ThronePoint(out throne))
            {
                if (FlatDist(HallCentre(), throne) <= 80f) problems.Add("the hall box is within 80 m of the Old Throne");
            }
            else if (HallSet()) warnings.Add("the Old Throne's position is unknown (throne set): the 80 m rule was not checked");
            foreach (UnityEngine.Vector3 pt in SitePoints())
            {
                string why = ForeignCircle(pt);
                if (why != null) { problems.Add("a site point lies inside " + why); break; }
            }
            if (HearthSet())
            {
                Point qp = QuestPlace("the_hearth");
                if (qp == null) warnings.Add("RealmQuests place the_hearth is not marked (/quest admin place set the_hearth)");
                else if (FlatDist(PointV(qp), HearthV()) > 3f) problems.Add("the hearth is " + Math.Round(FlatDist(PointV(qp), HearthV())) + " m from RealmQuests' the_hearth (must be within 3 m)");
                Point lz = LawsZone("Hearth");
                if (lz == null) warnings.Add("RealmLaws zone Hearth not found");
                else if (FlatDist(PointV(lz), HearthV()) > 3f) problems.Add("the hearth is " + Math.Round(FlatDist(PointV(lz), HearthV())) + " m from RealmLaws' Hearth zone centre (must be within 3 m)");
            }
            if (RealmSentinel == null) problems.Add("RealmSentinel is not loaded");
            if (RealmTravel == null) problems.Add("RealmTravel is not loaded");
            else if (!WaystoneExists(config.HearthWaystone)) problems.Add("waystone " + config.HearthWaystone + " does not exist (/travel admin set " + config.HearthWaystone + " capital)");
            if (!WaystoneExists(config.RoadsWaystone)) warnings.Add("waystone " + config.RoadsWaystone + " does not exist: the Roads line points to /travel");
            return problems;
        }

        private void FloorCheck(string what, Point p, bool required, List<string> problems, List<string> warnings)
        {
            Cell c = CellAt(PointV(p));
            Cell below = new Cell(); below.X = c.X; below.Y = c.Y - 1; below.Z = c.Z;
            Cell up = new Cell(); up.X = c.X; up.Y = c.Y + 1; up.Z = c.Z;
            if (MaterialAt(c) != 0 || MaterialAt(up) != 0) problems.Add(what + " has no two air cells to stand in");
            if (MaterialAt(below) == 0)
            {
                if (required) problems.Add(what + " has no block floor");
                else warnings.Add(what + " has no block floor (fine on bare ground)");
            }
        }

        // At load and every SiteSelfCheckMinutes: if any stone's floor cell is gone (a wipe, a removed sculpture), the
        // arrival closes itself and says why, so a fresh world never routes newcomers onto empty ground.
        private void SelfCheck()
        {
            if (loadFailed || data == null || !config.Open || !GridReady()) return;
            string why = null;
            for (int i = 0; i < data.Site.Stones.Count && why == null; i++)
            {
                Cell c = CellAt(PointV(data.Site.Stones[i]));
                Cell below = new Cell(); below.X = c.X; below.Y = c.Y - 1; below.Z = c.Z;
                if (MaterialAt(below) == 0) why = "stone " + (i + 1) + " has no floor (a wipe or a removed Gatehouse?)";
            }
            if (why == null && data.Site.Stones.Count == 0) why = "no arrival stones are stored";
            if (why == null) return;
            config.Open = false;
            SaveConfigNow();
            UpdateProvider();
            Count("self_check_closures");
            PrintWarning("Self-check closed the Gatehouse: " + why + ". Run the after-wipe run-sheet, then /arrival admin check and open.");
            foreach (Player p in OnlinePlayers()) if (IsAdmin(p)) Reply(p, "SelfCheckClosed", why);
        }

        private List<UnityEngine.Vector3> SitePoints()
        {
            var list = new List<UnityEngine.Vector3>();
            Site s = data.Site;
            if (HallSet()) { list.Add(PointV(s.Hall1)); list.Add(PointV(s.Hall2)); list.Add(HallCentre()); }
            foreach (Point p in s.Stones) list.Add(PointV(p));
            foreach (Point p in s.Banners.Values) list.Add(PointV(p));
            if (s.Eject != null) list.Add(PointV(s.Eject));
            if (s.Threshold != null) list.Add(PointV(s.Threshold));
            return list;
        }

        // Read-only views of other plugins' files (never written; a missing file is never created).
        private class DominionMap { public List<DominionHolding> holdings; }
        private class DominionHolding { public string name; public bool placed; public float x; public float z; public float radius; }
        private class QuestData { public Dictionary<string, QuestMark> Places; }
        private class QuestMark { public float X, Y, Z, Radius; }
        private class LawsConfig { public List<LawZone> Zones; }
        private class LawZone { public string Name; public float X, Z, Radius; public bool Town; }
        private class ArenaConfig { public List<ArenaZone> Arenas; public List<ArenaZone> Taverns; }
        private class ArenaZone { public string Name; public float X, Y, Z, Radius; }
        private class TravelData { public Dictionary<string, object> Waystones; }

        private T ReadForeign<T>(DataFileSystem fs, string name) where T : class, new()
        {
            try
            {
                if (fs == null || !fs.ExistsDatafile(name)) return null;
                return fs.ReadObject<T>(name);
            }
            catch (Exception) { return null; }
        }

        private DataFileSystem ConfigFiles()
        {
            try { return new DataFileSystem(Interface.Oxide.ConfigDirectory); }
            catch (Exception) { return null; }
        }

        private string ForeignCircle(UnityEngine.Vector3 p)
        {
            DominionMap map = ReadForeign<DominionMap>(Interface.Oxide.DataFileSystem, "RealmDominionMap");
            if (map != null && map.holdings != null)
                foreach (DominionHolding h in map.holdings)
                    if (h != null && h.placed && h.radius > 0 && FlatDist(p.x, p.z, h.x, h.z) <= h.radius) return "the Dominion holding " + Clean(h.name, 40);
            ArenaConfig ac = ReadForeign<ArenaConfig>(ConfigFiles(), "RealmArena");
            if (ac != null)
            {
                var zones = new List<ArenaZone>();
                if (ac.Arenas != null) zones.AddRange(ac.Arenas);
                if (ac.Taverns != null) zones.AddRange(ac.Taverns);
                foreach (ArenaZone z in zones)
                    if (z != null && z.Radius > 0 && FlatDist(p.x, p.z, z.X, z.Z) <= z.Radius) return "the arena zone " + Clean(z.Name, 40);
            }
            return null;
        }

        private Point QuestPlace(string id)
        {
            QuestData q = ReadForeign<QuestData>(Interface.Oxide.DataFileSystem, "RealmQuests");
            QuestMark m;
            if (q == null || q.Places == null || !q.Places.TryGetValue(id, out m) || m == null) return null;
            Point p = new Point(); p.X = m.X; p.Y = m.Y; p.Z = m.Z;
            return p;
        }

        private Point LawsZone(string name)
        {
            LawsConfig lc = ReadForeign<LawsConfig>(ConfigFiles(), "RealmLaws");
            if (lc == null || lc.Zones == null) return null;
            foreach (LawZone z in lc.Zones)
                if (z != null && SameText(z.Name, name)) { Point p = new Point(); p.X = z.X; p.Z = z.Z; p.Y = data.Site.Hearth != null ? data.Site.Hearth.Y : 0f; return p; }
            return null;
        }

        private float HearthZoneRadius()
        {
            LawsConfig lc = ReadForeign<LawsConfig>(ConfigFiles(), "RealmLaws");
            if (lc != null && lc.Zones != null)
                foreach (LawZone z in lc.Zones) if (z != null && SameText(z.Name, "Hearth") && z.Radius > 0) return z.Radius;
            return 40f;
        }

        // Waystone ids from RealmTravel's data file, read-only, cached for 10 minutes.
        private bool WaystoneExists(string id)
        {
            DateTime now = Now();
            if (waystoneCache == null || (now - waystoneCacheAt).TotalMinutes >= 10)
            {
                waystoneCache = new HashSet<string>();
                waystoneCacheAt = now;
                TravelData t = ReadForeign<TravelData>(Interface.Oxide.DataFileSystem, "RealmTravel");
                if (t != null && t.Waystones != null) foreach (string k in t.Waystones.Keys) waystoneCache.Add(k);
            }
            return waystoneCache.Contains(id);
        }

        private bool ThronePoint(out UnityEngine.Vector3 pos)
        {
            pos = new UnityEngine.Vector3();
            if (data.Site.Throne != null) { pos = PointV(data.Site.Throne); return true; }
            try
            {
                UnityEngine.Vector3 g = AncientThrone.EntityPosition;
                if (g.x != 0f || g.y != 0f || g.z != 0f) { pos = g; return true; }
            }
            catch (Exception) { }
            return false;
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // pending | crossing | running | done | none. Answers from saved data alone, so the order in which plugins'
        // OnPlayerConnected run does not matter.
        private string ArrivalStage(string playerId)
        {
            if (loadFailed || data == null || playerId == null) return SNone;
            Rec r = FindRec(playerId);
            bool open = config.Enabled && config.Open && !data.Site.Paused && config.RoutingMode != "off";
            if (r == null || r.Stage == SPending) return open ? SPending : SNone;
            if (r.Stage == SCrossing) return data.Site.Paused ? SNone : SCrossing;
            if (IsRunning(r.Stage)) return data.Site.Paused ? SNone : "running";
            if (r.Stage == SDone) return firstSightC.Contains(playerId) ? SNone : SDone;
            return SNone;
        }

        private bool OwnsArrival(string playerId)
        {
            string s = ArrivalStage(playerId);
            return s == SPending || s == SCrossing || s == "running";
        }

        #endregion

        #region Helpers

        private DateTime Now()
        {
            return clock();
        }

        private void LogHook(string hook, Player p, string detail)
        {
            if (!config.LogHooks) return;
            Puts("[hook] " + Now().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + hook + " " + Clean(p.Name, 40) + " (" + p.Id + "): " + detail);
        }

        private void Say(Player p, string key, params object[] args)
        {
            if (p == null) return;
            string text = Fmt(key, p, args);
            if (text.StartsWith(" ")) p.SendMessage(text);
            else Reply(p, key, args);
        }

        private bool IsAdmin(Player player)
        {
            return player != null && !player.IsServer && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private bool HasPerm(Player player, string perm)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), perm);
        }

        private bool HallSet() { return data.Site.Hall1 != null && data.Site.Hall2 != null; }
        private bool HearthSet() { return data.Site.Hearth != null; }
        private bool AxisSet() { return data.Site.Threshold != null && data.Site.Hearth != null; }
        private UnityEngine.Vector3 HearthV() { return PointV(data.Site.Hearth); }

        private UnityEngine.Vector3 HallCentre()
        {
            Site s = data.Site;
            return V((s.Hall1.X + s.Hall2.X) / 2f, (s.Hall1.Y + s.Hall2.Y) / 2f, (s.Hall1.Z + s.Hall2.Z) / 2f);
        }

        // Inside the hall box (x/z between the corners, y from 3 m below the lower corner to HallHeightMetres above the
        // higher), grown by margin metres.
        private bool InHall(UnityEngine.Vector3 p, float margin)
        {
            if (data == null || !HallSet()) return false;
            return InBox(p, data.Site.Hall1, data.Site.Hall2, margin);
        }

        private bool InPad(UnityEngine.Vector3 p)
        {
            if (data.Site.Pad1 == null || data.Site.Pad2 == null) return false;
            return InBox(p, data.Site.Pad1, data.Site.Pad2, 0f);
        }

        private bool InBox(UnityEngine.Vector3 p, Point a, Point b, float margin)
        {
            float x0 = Math.Min(a.X, b.X) - margin, x1 = Math.Max(a.X, b.X) + margin;
            float z0 = Math.Min(a.Z, b.Z) - margin, z1 = Math.Max(a.Z, b.Z) + margin;
            float y0 = Math.Min(a.Y, b.Y) - 3f - margin, y1 = Math.Max(a.Y, b.Y) + config.HallHeightMetres + margin;
            return p.x >= x0 && p.x <= x1 && p.z >= z0 && p.z <= z1 && p.y >= y0 && p.y <= y1;
        }

        private float BoxFlatDistance(UnityEngine.Vector3 p)
        {
            Site s = data.Site;
            float x0 = Math.Min(s.Hall1.X, s.Hall2.X), x1 = Math.Max(s.Hall1.X, s.Hall2.X);
            float z0 = Math.Min(s.Hall1.Z, s.Hall2.Z), z1 = Math.Max(s.Hall1.Z, s.Hall2.Z);
            float dx = p.x < x0 ? x0 - p.x : p.x > x1 ? p.x - x1 : 0f;
            float dz = p.z < z0 ? z0 - p.z : p.z > z1 ? p.z - z1 : 0f;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        // Metres along the avenue from the gold line toward the fire.
        private float AxisProj(UnityEngine.Vector3 p)
        {
            Point a = data.Site.Threshold, b = data.Site.Hearth;
            double dx = b.X - a.X, dz = b.Z - a.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.01) return 0f;
            return (float)(((p.x - a.X) * dx + (p.z - a.Z) * dz) / len);
        }

        // How far outside the route corridor (the hall box, the avenue +/- RouteCorridorMetres, the HandoverRadius round
        // the fire) a point lies; zero or less is inside.
        private float CorridorOutside(UnityEngine.Vector3 p)
        {
            float best = FlatDist(p, HearthV()) - config.HandoverRadius;
            if (HallSet()) best = Math.Min(best, BoxFlatDistance(p));
            if (data.Site.Threshold != null)
            {
                Point a = data.Site.Threshold, b = data.Site.Hearth;
                double dx = b.X - a.X, dz = b.Z - a.Z, len2 = dx * dx + dz * dz;
                double t = len2 < 0.0001 ? 0 : ((p.x - a.X) * dx + (p.z - a.Z) * dz) / len2;
                if (t < 0) t = 0; if (t > 1) t = 1;
                double cx = a.X + t * dx, cz = a.Z + t * dz;
                float seg = (float)Math.Sqrt((p.x - cx) * (p.x - cx) + (p.z - cz) * (p.z - cz));
                best = Math.Min(best, seg - config.RouteCorridorMetres);
            }
            return best;
        }

        private string Compass(UnityEngine.Vector3 from, UnityEngine.Vector3 to)
        {
            double deg = Math.Atan2(to.x - from.x, to.z - from.z) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            string[] names = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return names[(int)Math.Floor((deg + 22.5) / 45.0) % 8];
        }

        // The game clock read on the server (inferred that it follows the sky): night is after HourOfSunsetEnd or before
        // HourOfSunriseStart; dusk is between HourOfSunsetStart and HourOfSunsetEnd [DEC GameClock].
        private bool IsNight()
        {
            try
            {
                GameClock c = GameClock.Instance;
                if (c == null) return false;
                return c.TimeOfDay >= c.HourOfSunsetEnd || c.TimeOfDay < c.HourOfSunriseStart;
            }
            catch (Exception) { return false; }
        }

        private bool IsDusk()
        {
            try
            {
                GameClock c = GameClock.Instance;
                if (c == null) return false;
                return c.TimeOfDay >= c.HourOfSunsetStart && c.TimeOfDay < c.HourOfSunsetEnd;
            }
            catch (Exception) { return false; }
        }

        // Cross-plugin calls: a missing plugin, a missing method or a throw all read as "not available" (null).
        private object Ask(Plugin plugin, string method, params object[] args)
        {
            if (plugin == null) return null;
            try { return plugin.Call(method, args); }
            catch (Exception ex)
            {
                if (!Throttled("call|" + method, 300)) PrintWarning(method + " failed: " + ex.Message);
                return null;
            }
        }

        private bool AskBool(Plugin plugin, string method, params object[] args)
        {
            object r = Ask(plugin, method, args);
            return r is bool && (bool)r;
        }

        private string AskString(Plugin plugin, string method, params object[] args)
        {
            return Ask(plugin, method, args) as string;
        }

        private List<string> AskList(Plugin plugin, string method, params object[] args)
        {
            return Ask(plugin, method, args) as List<string>;
        }

        private bool Throttled(string key, int seconds)
        {
            DateTime now = Now();
            DateTime until;
            if (throttle.TryGetValue(key, out until) && until > now) return true;
            throttle[key] = now.AddSeconds(seconds);
            return false;
        }

        private static List<Player> OnlinePlayers()
        {
            var list = new List<Player>();
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private static Player OnlineById(ulong id)
        {
            if (id == 0) return null;
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer && p.Id == id) return p;
            return null;
        }

        private static bool TryPos(Player p, out UnityEngine.Vector3 pos)
        {
            pos = new UnityEngine.Vector3();
            try
            {
                if (p == null || p.Entity == null) return false;
                pos = p.Entity.Position;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static UnityEngine.Vector3 PosOf(Player p)
        {
            UnityEngine.Vector3 v;
            TryPos(p, out v);
            return v;
        }

        // [DEC] CharacterTeleport.Teleport(Vector3), the call the game's own /tp makes on the server.
        private bool Teleport(Player player, UnityEngine.Vector3 to)
        {
            try
            {
                if (player == null || player.Entity == null) return false;
                CharacterTeleport tp = player.Entity.GetOrCreate<CharacterTeleport>();
                if (tp == null) return false;
                tp.Teleport(to);
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("Teleport failed for " + Clean(player.Name, 40) + ": " + ex.Message);
                return false;
            }
        }

        private static UnityEngine.Vector3 V(float x, float y, float z)
        {
            var v = new UnityEngine.Vector3();
            v.x = x; v.y = y; v.z = z;
            return v;
        }

        private static UnityEngine.Vector3 PointV(Point p)
        {
            return V(p.X, p.Y, p.Z);
        }

        private static Point P(UnityEngine.Vector3 v)
        {
            Point p = new Point();
            p.X = v.x; p.Y = v.y; p.Z = v.z;
            return p;
        }

        private static float Dist(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float FlatDist(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
        {
            return FlatDist(a.x, a.z, b.x, b.z);
        }

        private static float FlatDist(float ax, float az, float bx, float bz)
        {
            double dx = ax - bx, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static int Round10(float metres)
        {
            return (int)(Math.Round(metres / 10f) * 10);
        }

        private static int Bits(int mask)
        {
            int n = 0;
            for (int i = 0; i < 6; i++) if ((mask & (1 << i)) != 0) n++;
            return n;
        }

        private string Span(double hours)
        {
            if (hours < 1) return Math.Max(1, (int)Math.Ceiling(hours * 60)).ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)Math.Round(hours)).ToString(CultureInfo.InvariantCulture) + " h";
        }

        private static string PosText(UnityEngine.Vector3 v)
        {
            return ((int)Math.Round(v.x)) + "," + ((int)Math.Round(v.y)) + "," + ((int)Math.Round(v.z));
        }

        private static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
        private static int ClampI(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        private static int ParseRgb(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return -1;
            string h = hex.Trim().TrimStart('#');
            int v;
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return -1;
            return v;
        }

        private static bool SameText(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string JoinFrom(string[] args, int start)
        {
            if (args == null || start >= args.Length) return "";
            var parts = new string[args.Length - start];
            Array.Copy(args, start, parts, 0, parts.Length);
            return string.Join(" ", parts).Trim();
        }

        private static string NormalizeId(string s)
        {
            if (s == null) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.Length < 2 || t.Length > 24) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return null;
            return t;
        }

        private static string Pretty(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            return char.ToUpperInvariant(id[0]) + id.Substring(1);
        }

        // Display-safe text: no colour tags, braces or line breaks, capped length.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsControl(c) || c == '[' || c == ']' || c == '{' || c == '}') continue;
                sb.Append(c);
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim();
        }

        #endregion
    }
}
