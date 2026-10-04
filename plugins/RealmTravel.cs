// RealmTravel: the roads of Ostreval. Waystones (named fast-travel points a player unlocks by walking to them), a home
// inside the player's own crest zone, guided roads in chat, and the realm's kits (a newcomer's pack, daily house
// provisions and a season's bounty).
//
//   Waystones  Admins raise them where they stand (/travel admin set): the capital, each house seat, the great holdings
//              and landmarks, optionally marked by one of RealmSculptor's monuments (/sculpt place). A player learns a
//              waystone by coming within its radius. /travel lists the ones they know; /travel <name> sets out: the
//              player stands still for ChannelSeconds (moving, striking or being hurt breaks it), then the server moves
//              them and takes the toll in marks. The toll goes to the crown's treasury (RealmTreasury.ChargeMarks: purse
//              -> treasury, nothing minted). No journey while in a fight (own damage watch + RealmWarden.IsInCombat),
//              while captive or holding a captive (PlayerCaptureManager, CrownAndConsequences.IsHeldForRansom), while
//              bearing the Ironbreaker (RealmLegendary.IsBearer), as an outlaw (RealmContracts.IsOutlaw,
//              RealmLaws.IsCourtOutlaw), or to or from the Old Throne while a rebellion is under way
//              (CrownAndConsequences.IsRebellionActive). Seats are open to their house and its allies (RealmHouses);
//              the capital is closed to exiles (RealmLaws.IsExiled); holdings close in the raid hours
//              (RealmWarden.IsRaidHourNow). The crown's Open Roads decree waives the toll (IsDecreeActive), houses
//              sworn to the crown pay less (IsSwornToCrown), and each player's first FreeTrips journeys are free.
//              A traveller who arrives is shielded from other players' blows for ArrivalShieldSeconds unless they
//              strike first, so nobody can camp a waystone.
//   Home       /home set inside your own crest zone (CrestScheme.CurrentCrestGroup(position) == SocialAPI.GetGroupId
//              (player), or the crest is the player's own), /home to return: the same rules, and the home must still be
//              yours and not under siege (CrestScheme.IsUnderSiege).
//   Roads      /road <waystone> or /road home calls the way in chat (distance and compass direction) every
//              UpdateSeconds until you arrive. Travel.Mode "road" turns /travel itself into a road: the fallback if the
//              server-side teleport turns out not to work in game.
//   Kits       Defined in config (Kits.List): "starter" once per Steam id for newcomers (seen under RealmWarden's
//              new-player protection), "house" once per realm day for a member of a house of HouseKitMinMembers or
//              more who has been in it HouseKitMinMemberHours, "season" once per RealmSeasons season after
//              SeasonKitMinPlayMinutes of play in it. Item names are checked against the game's own item list at
//              run time (ResourceType name, then exact item name); unknown names are reported and skipped.
//   Wayfarer   Reaching every waystone (at least WayfarerMinWaystones) is told to the realm, earns the RealmRenown deed
//              "wayfarer" once, and earns the player's house RealmSeasons points (capped per house per season).
//
// Game API ([DEC] = read in the decompiled shipped patched Assembly-CSharp.dll, type.member names only; [ASM] = in its
// metadata; other tags as in docs/oxide-rok-api.md). No game code is copied here.
//   Teleport   [DEC] CharacterTeleport.Teleport(Vector3), reached with Entity.GetOrCreate<CharacterTeleport>(). The game's
//              own server-side /tp (ThronesCommandHandler.tp) moves players with exactly this call; on the server
//              (Player.IsLocalServer) it raises TeleportEvent through EventManager.CallEvent and the clients follow.
//              RealmSentinel already treats a server TeleportEvent as movement grace; SentinelGrace is asked first too.
//   Items      the game's own /give path (ThronesCommandHandler.Give) [IL]: PlayerExtensions.GetInventory ->
//              Container.Contents, new InvGameItemStack(blueprint, n, null) in chunks of ContainerManagement.StackLimit,
//              ItemCollection.AutoMergeAdd; every amount is measured with ItemCollection.AutoCount. Lookups:
//              InvBlueprints.Instance.GetBlueprintForResource(ResourceType) and GetBlueprintForName(name, true, true);
//              search with InvBlueprints.GetBlueprintsContaining(word). RealmSentinel is told (SentinelItemSource).
//   Land       [ASM] CrestScheme.CurrentCrestGroup(Vector3), GetCrestPlayer(Vector3), IsUnderSiege(Vector3);
//              SocialAPI.GetGroupId(ulong) (the test the game's own crest siege check uses, as in RealmWarden).
//   Throne     [DEC] AncientThrone.EntityPosition (static; Vector3.zero when no throne is loaded), unless an admin set the
//              point with /travel admin throne; else where a capture completed (OnThroneCaptured [OPJ L607]).
//   Captives   [ASM] PlayerCaptureManager.Captured / HoldingCaptive / CaptivePlayerID, read from the player's entity.
//   Hooks      OnEntityHealthChange [OPJ L162] (combat watch, breaks journeys, arrival shield), OnEntityDeath [OPJ L188],
//              OnPlayerConnected / OnPlayerDisconnected [SRC], OnThroneCaptured [OPJ L607].
//
// Data: oxide/data/RealmTravel.json. Journeys in progress are never saved: a reload cancels them and nothing is charged.
// If the file exists but cannot be read the plugin pauses and NEVER writes it. Kits: the claim and the items owed are
// saved before anything is given, and items leave the owed ledger (saved) before each give; a shortfall goes back.
// A crash can lose a kit, never duplicate one. Tolls are taken only after the move, in the same server tick as the purse
// check, so a purse cannot be emptied in between.
// Language level: C# 3 syntax, .NET 3.5 API surface. Cross-plugin methods are non-public (Oxide calls NonPublic|Instance).
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmTravel.md for the in-game test steps.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeHatch.Common;                            // PlayerExtensions: SendMessage, SendError, GetInventory, ShowPopup [ASM]
using CodeHatch.Damaging;                          // Damage, DamageType [ASM]
using CodeHatch.Engine.Behaviours;                 // CharacterTeleport [DEC]
using CodeHatch.Engine.Core.Cache;                 // Entity [ASM]
using CodeHatch.Engine.Modules.SocialSystem;       // SocialAPI [ASM]
using CodeHatch.Engine.Networking;                 // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;              // InvItemBlueprint, InvBlueprints [ASM]
using CodeHatch.Inventory.Blueprints.Components;   // ContainerManagement [ASM; IL ThronesCommandHandler.Give]
using CodeHatch.ItemContainer;                     // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events.Entities;        // EntityDamageEvent, EntityDeathEvent [ASM]
using CodeHatch.Thrones.AncientThrone;             // AncientThrone, AncientThroneCaptureEvent [ASM; DEC]
using CodeHatch.Thrones.Capture;                   // PlayerCaptureManager [ASM]
using CodeHatch.Thrones.SocialSystem;              // CrestScheme [ASM]
using Oxide.Core;                                  // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                          // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmTravel", "Realm", "0.1.0")]
    [Description("Waystones and fast travel, a home in your own crest zone, guided roads, and the realm's kits")]
    public class RealmTravel : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmLaws;
        [PluginReference] private Plugin RealmContracts;
        [PluginReference] private Plugin RealmLegendary;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmSentinel;

        private const string PermAdmin = "realmtravel.admin";
        private const string DataName = "RealmTravel";
        private const int DataFormat = 1;
        private const int MaxWaystones = 200;
        private const long MaxExplicitToll = 100000;

        private const string KCapital = "capital";
        private const string KSeat = "seat";
        private const string KHolding = "holding";
        private const string KLandmark = "landmark";
        private static readonly string[] Kinds = { KCapital, KSeat, KHolding, KLandmark };

        private const string KitStarter = "starter";
        private const string KitHouse = "house";
        private const string KitSeason = "season";
        private static readonly string[] KitKinds = { KitStarter, KitHouse, KitSeason };

        private const string JWaystone = "waystone";
        private const string JHome = "home";

        // The six great houses' monuments in art/sculptures (RealmSculptor ids), offered when a seat is raised.
        private static readonly string[] GreatHouses = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private Timer tickTimer;
        private DateTime nextDiscovery = DateTime.MinValue;
        private DateTime nextMinute = DateTime.MinValue;
        private DateTime nextSave = DateTime.MinValue;

        // Session state (never saved).
        private readonly Dictionary<ulong, Journey> journeys = new Dictionary<ulong, Journey>();
        private readonly Dictionary<ulong, Road> roads = new Dictionary<ulong, Road>();
        private readonly Dictionary<ulong, DateTime> lastPvp = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> lastBeast = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<ulong, DateTime> shieldUntil = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<string, DateTime> throttle = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> removeConfirm = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, InvItemBlueprint> blueprintCache = new Dictionary<string, InvItemBlueprint>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DateTime> wayfarerHeralds = new List<DateTime>();

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        #region Config

        private class GeneralSection
        {
            public bool Enabled = true;                 // master switch: off = every command answers "closed", no ticks
            public bool UsePopups = true;               // the discovery window (chat is always sent too)
            public bool AdminsExempt = false;           // true: admins skip channel time, cooldowns and tolls (testing)
            public float TickSeconds = 1f;
            public int SaveEverySeconds = 60;
            public int MaxPlayersKept = 20000;          // oldest records (by last seen) are dropped beyond this
        }

        private class TravelSection
        {
            public bool Enabled = true;
            public string Mode = "teleport";            // teleport | road (road: /travel shows the way instead of moving)
            public float ChannelSeconds = 10f;          // stand still this long before the road takes you
            public float MoveTolerance = 1.5f;          // metres you may drift while waiting
            public int CooldownMinutes = 10;
            public long BaseToll = 10;                  // marks; plus TollPer100m for every 100 m, at most MaxToll
            public long TollPer100m = 1;
            public long MaxToll = 50;
            public int FreeTrips = 3;                   // each player's first journeys cost nothing
            public int SwornTollPercent = 50;           // houses sworn to the crown pay this share of the toll
            public string OpenRoadsDecree = "roads";    // CrownAndConsequences decree id ("" = none)
            public int OpenRoadsTollPercent = 0;        // share of the toll while that decree is in force
            public bool TollFreeWithoutTreasury = true; // RealmTreasury not loaded: free (true) or refused (false)
            public float MinDistance = 40f;             // closer than this, walk
            public float ArrivalLift = 0.5f;            // metres above the stored point (the game's /tp adds 0.5)
            public int ArrivalShieldSeconds = 5;        // no player damage on arrival unless the traveller strikes first
            public int CombatSeconds = 30;              // after dealing or taking a player's blow
            public int BeastCombatSeconds = 10;         // after being hurt by a creature (or striking one)
            public bool UseWardenCombat = true;         // also ask RealmWarden.IsInCombat
            public bool BlockOutlaws = true;
            public bool BlockCaptives = true;
            public bool BlockIronbreaker = true;
            public bool BlockNearThroneInRebellion = true;
            public float ThroneRadius = 80f;
            public bool ExilesBarredFromCapital = true;
            public string SeatAccess = "allies";        // house | allies | all
            public bool HoldingsClosedInRaidHours = true;
        }

        private class HomeSection
        {
            public bool Enabled = true;
            public bool RequireOwnCrest = true;
            public float ChannelSeconds = 12f;
            public int CooldownMinutes = 15;
            public long Toll = 5;
            public int SetCooldownMinutes = 10;
            public bool BlockUnderSiege = true;
        }

        private class DiscoverySection
        {
            public bool Enabled = true;
            public float CheckSeconds = 2f;
            public float DefaultRadius = 10f;
            public bool PopupOnDiscovery = true;
            public int WayfarerMinWaystones = 4;
            public string WayfarerDeed = "wayfarer";    // RealmRenown deed kind ("" = none)
            public bool WayfarerHerald = true;
            public int WayfarerHeraldsPerHour = 4;
            public int PathfinderPoints = 2;            // RealmSeasons points for the wayfarer's house (0 = none)
            public int PathfinderAwardsPerHousePerSeason = 3;
        }

        private class RoadSection
        {
            public bool Enabled = true;
            public float UpdateSeconds = 15f;
            public int MaxMinutes = 30;
            public float ArrivalRadius = 12f;
            public bool NorthIsPositiveZ = true;        // UNVERIFIED: which world axis the game's north is
        }

        private class KitItem
        {
            public string Item;
            public int Amount;
        }

        private class KitDef
        {
            public string Id;
            public string Name;
            public string Kind;                         // starter | house | season
            public bool Enabled = true;
            public List<KitItem> Items;
        }

        private class KitSection
        {
            public bool Enabled = true;
            public int StarterClaimDays = 7;            // a newcomer has this long from their first day to take it
            public int StarterFallbackHours = 0;        // RealmWarden not loaded: "newcomer" = first seen this recently (0 = never)
            public int StarterMaxPerDay = 40;           // the whole realm, rolling 24 h (slows alt farms)
            public bool NewcomerHint = true;
            public float NewcomerHintDelaySeconds = 45f;// after RealmHerald's welcome
            public int HouseKitMinMemberHours = 12;
            public int HouseKitMinMembers = 2;
            public int SeasonKitMinPlayMinutes = 30;
            public int MaxOwedLines = 30;
            public List<KitDef> List;                   // null in the class, filled by DefaultKits (Newtonsoft appends to lists)
        }

        private class PluginConfig
        {
            public GeneralSection General = new GeneralSection();
            public TravelSection Travel = new TravelSection();
            public HomeSection Home = new HomeSection();
            public DiscoverySection Discovery = new DiscoverySection();
            public RoadSection Roads = new RoadSection();
            public KitSection Kits = new KitSection();
        }

        private static KitItem I(string item, int amount)
        {
            return new KitItem { Item = item, Amount = amount };
        }

        // Defaults use ResourceType names only ([ASM] CodeHatch.ResourceType), the one item list the DLL itself holds.
        // UNVERIFIED that each resolves to a carried item on a real server: /kit admin check shows what resolved.
        private static List<KitDef> DefaultKits()
        {
            return new List<KitDef>
            {
                new KitDef { Id = "starter", Name = "Traveller's Pack", Kind = KitStarter,
                    Items = new List<KitItem> { I("Wood", 150), I("Stone", 100), I("Bread", 4), I("Apple", 6) } },
                new KitDef { Id = "house", Name = "House Provisions", Kind = KitHouse,
                    Items = new List<KitItem> { I("Wood", 100), I("Stone", 100), I("Bread", 3) } },
                new KitDef { Id = "season", Name = "Season's Bounty", Kind = KitSeason,
                    Items = new List<KitItem> { I("Wood", 300), I("Stone", 300), I("IronOre", 40) } }
            };
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.Kits.List = DefaultKits();
            return c;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static long ClampL(long v, long lo, long hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private void ClampConfig()
        {
            if (config == null) config = DefaultConfig();
            if (config.General == null) config.General = new GeneralSection();
            if (config.Travel == null) config.Travel = new TravelSection();
            if (config.Home == null) config.Home = new HomeSection();
            if (config.Discovery == null) config.Discovery = new DiscoverySection();
            if (config.Roads == null) config.Roads = new RoadSection();
            if (config.Kits == null) config.Kits = new KitSection();

            GeneralSection g = config.General;
            g.TickSeconds = ClampF(g.TickSeconds, 0.5f, 5f);
            g.SaveEverySeconds = Clamp(g.SaveEverySeconds, 10, 3600);
            g.MaxPlayersKept = Clamp(g.MaxPlayersKept, 100, 1000000);

            TravelSection t = config.Travel;
            t.Mode = (t.Mode ?? "").Trim().ToLowerInvariant() == "road" ? "road" : "teleport";
            t.ChannelSeconds = ClampF(t.ChannelSeconds, 0f, 120f);
            t.MoveTolerance = ClampF(t.MoveTolerance, 0.2f, 20f);
            t.CooldownMinutes = Clamp(t.CooldownMinutes, 0, 10080);
            t.BaseToll = ClampL(t.BaseToll, 0, MaxExplicitToll);
            t.TollPer100m = ClampL(t.TollPer100m, 0, 10000);
            t.MaxToll = ClampL(t.MaxToll, 0, MaxExplicitToll);
            t.FreeTrips = Clamp(t.FreeTrips, 0, 1000);
            t.SwornTollPercent = Clamp(t.SwornTollPercent, 0, 100);
            t.OpenRoadsTollPercent = Clamp(t.OpenRoadsTollPercent, 0, 100);
            t.OpenRoadsDecree = (t.OpenRoadsDecree ?? "").Trim();
            t.MinDistance = ClampF(t.MinDistance, 0f, 10000f);
            t.ArrivalLift = ClampF(t.ArrivalLift, 0f, 10f);
            t.ArrivalShieldSeconds = Clamp(t.ArrivalShieldSeconds, 0, 60);
            t.CombatSeconds = Clamp(t.CombatSeconds, 0, 3600);
            t.BeastCombatSeconds = Clamp(t.BeastCombatSeconds, 0, 3600);
            t.ThroneRadius = ClampF(t.ThroneRadius, 0f, 5000f);
            string sa = (t.SeatAccess ?? "").Trim().ToLowerInvariant();
            t.SeatAccess = sa == "house" || sa == "all" ? sa : "allies";

            HomeSection h = config.Home;
            h.ChannelSeconds = ClampF(h.ChannelSeconds, 0f, 120f);
            h.CooldownMinutes = Clamp(h.CooldownMinutes, 0, 10080);
            h.Toll = ClampL(h.Toll, 0, MaxExplicitToll);
            h.SetCooldownMinutes = Clamp(h.SetCooldownMinutes, 0, 10080);

            DiscoverySection d = config.Discovery;
            d.CheckSeconds = ClampF(d.CheckSeconds, 0.5f, 60f);
            d.DefaultRadius = ClampF(d.DefaultRadius, 2f, 100f);
            d.WayfarerMinWaystones = Clamp(d.WayfarerMinWaystones, 1, MaxWaystones);
            d.WayfarerDeed = (d.WayfarerDeed ?? "").Trim();
            d.WayfarerHeraldsPerHour = Clamp(d.WayfarerHeraldsPerHour, 0, 60);
            d.PathfinderPoints = Clamp(d.PathfinderPoints, 0, 100);
            d.PathfinderAwardsPerHousePerSeason = Clamp(d.PathfinderAwardsPerHousePerSeason, 0, 100);

            RoadSection r = config.Roads;
            r.UpdateSeconds = ClampF(r.UpdateSeconds, 3f, 300f);
            r.MaxMinutes = Clamp(r.MaxMinutes, 1, 600);
            r.ArrivalRadius = ClampF(r.ArrivalRadius, 2f, 200f);

            KitSection k = config.Kits;
            k.StarterClaimDays = Clamp(k.StarterClaimDays, 1, 365);
            k.StarterFallbackHours = Clamp(k.StarterFallbackHours, 0, 8760);
            k.StarterMaxPerDay = Clamp(k.StarterMaxPerDay, 0, 100000);
            k.NewcomerHintDelaySeconds = ClampF(k.NewcomerHintDelaySeconds, 0f, 600f);
            k.HouseKitMinMemberHours = Clamp(k.HouseKitMinMemberHours, 0, 8760);
            k.HouseKitMinMembers = Clamp(k.HouseKitMinMembers, 1, 1000);
            k.SeasonKitMinPlayMinutes = Clamp(k.SeasonKitMinPlayMinutes, 0, 100000);
            k.MaxOwedLines = Clamp(k.MaxOwedLines, 5, 500);
            if (k.List == null) k.List = DefaultKits();
            var seen = new List<string>();
            var good = new List<KitDef>();
            foreach (KitDef kit in k.List)
            {
                if (kit == null) continue;
                string id = NormalizeId(kit.Id);
                string kind = (kit.Kind ?? "").Trim().ToLowerInvariant();
                if (id == null || Array.IndexOf(KitKinds, kind) < 0 || seen.Contains(id))
                {
                    PrintWarning("Kit '" + kit.Id + "' ignored: it needs a unique id (a-z, 0-9, -) and a kind of starter, house or season.");
                    continue;
                }
                kit.Id = id;
                kit.Kind = kind;
                kit.Name = Clean(kit.Name, 40);
                if (kit.Name.Length == 0) kit.Name = id;
                if (kit.Items == null) kit.Items = new List<KitItem>();
                kit.Items.RemoveAll(delegate(KitItem it) { return it == null || string.IsNullOrEmpty(it.Item) || it.Item.Trim().Length == 0 || it.Amount <= 0; });
                foreach (KitItem it in kit.Items) { it.Item = it.Item.Trim(); it.Amount = Clamp(it.Amount, 1, 10000); }
                if (kit.Items.Count > 20) kit.Items.RemoveRange(20, kit.Items.Count - 20);
                seen.Add(id);
                good.Add(kit);
            }
            k.List = good;
        }

        #endregion

        #region Data

        private class Waystone
        {
            public string Id;
            public string Name;
            public string Kind;
            public string House;                        // for a seat: the house whose seat it is
            public string Monument;                     // RealmSculptor sculpture id that marks it ("" = none)
            public string Note;                         // a line of lore shown on discovery and in /travel info
            public float X, Y, Z;
            public float Radius;
            public long Toll = -1;                      // -1 = by distance (BaseToll + TollPer100m, at most MaxToll)
            public bool Hidden;                         // not listed until found
            public bool Enabled = true;
            public string CreatedBy;
            public DateTime CreatedAt;
            public int Visitors;
        }

        private class Owed
        {
            public string Item;                         // the blueprint name resolved at claim time
            public int Amount;
            public string Kit;
        }

        private class Traveller
        {
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public List<string> Unlocked = new List<string>();
            public DateTime TravelReadyAt;
            public DateTime HomeReadyAt;
            public DateTime HomeSetReadyAt;
            public bool HasHome;
            public float HomeX, HomeY, HomeZ;
            public int FreeTripsUsed;
            public int Trips;
            public Dictionary<string, string> KitClaims = new Dictionary<string, string>();   // kit id -> claim key
            public string HouseSeen;
            public DateTime HouseSince;
            public int PlaySeason;
            public int PlaySeasonMinutes;
            public bool WasNewcomer;
            public bool HintSent;
            public bool Wayfarer;
            public int WayfarerSeason;
            public List<Owed> Owed = new List<Owed>();
        }

        private class StoredData
        {
            public int Format = DataFormat;
            public Dictionary<string, Waystone> Waystones = new Dictionary<string, Waystone>();
            public Dictionary<string, Traveller> Players = new Dictionary<string, Traveller>();
            public bool HasThrone;                      // an admin set the throne point
            public float ThroneX, ThroneY, ThroneZ;
            public bool LearnedThrone;                  // learned where a capture completed
            public float LearnedX, LearnedY, LearnedZ;
            public List<DateTime> StarterClaims = new List<DateTime>();
            public Dictionary<string, int> PathfinderAwards = new Dictionary<string, int>();   // "season|house" -> awards
            public long TollsCollected;
            public int Trips;
            public int UnpaidTolls;
            public int KitsGiven;
            public int ArrivalsUnconfirmed;
        }

        // Session only: a journey being waited out, and a road being followed.
        private class Journey
        {
            public string Kind;                         // waystone | home
            public string Target;                       // waystone id
            public string TargetName;
            public float SX, SY, SZ;                    // where the traveller stood when they set out
            public DateTime Ends;
            public long Toll;
            public bool FreeTrip;
        }

        private class Road
        {
            public string Name;
            public string Target;                       // waystone id, or "" for home
            public float X, Y, Z;
            public DateTime Next;
            public DateTime Until;
        }

        private class Refusal
        {
            public string Key;
            public object[] Args;
            public Refusal(string key, params object[] args) { Key = key; Args = args; }
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
                PrintError("Could not read oxide/data/" + DataName + ".json (" + ex.Message + "). RealmTravel is paused and will NOT "
                    + "write the file. Fix it or move it away, then reload.");
                return;
            }
            if (data == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or truncated. RealmTravel is paused and will NOT write it. "
                    + "Fix it or move it away, then reload.");
                return;
            }
            if (data.Format > DataFormat)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json was written by a newer RealmTravel (format " + data.Format + "). Paused; nothing is written.");
                data = null;
                return;
            }
            NormalizeData();
        }

        private void NormalizeData()
        {
            if (data.Waystones == null) data.Waystones = new Dictionary<string, Waystone>();
            if (data.Players == null) data.Players = new Dictionary<string, Traveller>();
            if (data.StarterClaims == null) data.StarterClaims = new List<DateTime>();
            if (data.PathfinderAwards == null) data.PathfinderAwards = new Dictionary<string, int>();
            var stones = new Dictionary<string, Waystone>();
            foreach (KeyValuePair<string, Waystone> kv in data.Waystones)
            {
                string id = NormalizeId(kv.Key);
                Waystone w = kv.Value;
                if (id == null || w == null || stones.ContainsKey(id)) continue;
                w.Id = id;
                if (Array.IndexOf(Kinds, w.Kind) < 0) w.Kind = KLandmark;
                w.Name = Clean(w.Name, 40);
                if (w.Name.Length == 0) w.Name = Pretty(id);
                w.House = Clean(w.House, 40);
                w.Monument = NormalizeSculpture(w.Monument) ?? "";
                w.Note = Clean(w.Note, 150);
                if (w.Radius < 2f || w.Radius > 100f) w.Radius = config.Discovery.DefaultRadius;
                if (w.Toll < -1) w.Toll = -1;
                if (w.Toll > MaxExplicitToll) w.Toll = MaxExplicitToll;
                stones[id] = w;
            }
            data.Waystones = stones;
            var players = new Dictionary<string, Traveller>();
            foreach (KeyValuePair<string, Traveller> kv in data.Players)
            {
                ulong u;
                if (kv.Value == null || !ulong.TryParse(kv.Key, out u)) continue;
                Traveller r = kv.Value;
                if (r.Unlocked == null) r.Unlocked = new List<string>();
                var ids = new List<string>();
                foreach (string s in r.Unlocked) { string n = NormalizeId(s); if (n != null && !ids.Contains(n)) ids.Add(n); }
                r.Unlocked = ids;
                if (r.KitClaims == null) r.KitClaims = new Dictionary<string, string>();
                if (r.Owed == null) r.Owed = new List<Owed>();
                r.Owed.RemoveAll(delegate(Owed o) { return o == null || string.IsNullOrEmpty(o.Item) || o.Amount <= 0; });
                if (r.Name == null) r.Name = kv.Key;
                players[kv.Key] = r;
            }
            data.Players = players;
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite a file that could not be read
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            nextSave = Now().AddSeconds(config.General.SaveEverySeconds);
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
                { "Speaker", "Roads" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Paused", "The roads are closed: oxide/data/RealmTravel.json could not be read. An admin must fix or move it, then reload." },
                { "Closed", "The roads of the realm are closed for now." },
                { "PartClosed", "That part of the roads is closed on this server." },
                { "NoPermission", "You may not do that." },
                { "NoBody", "You cannot travel in your present state." },
                { "PlayerNotFound", "No such person is known (or the name is ambiguous)." },

                // /travel
                { "TravelHelp1", "Waystones: [F4C96D]/travel[FFFFFF] lists those you know; [F4C96D]/travel[FFFFFF] <name> sets out (stand still {0} s, toll in marks)." },
                { "TravelHelp2", "  [F4C96D]/travel all[FFFFFF] | [F4C96D]/travel info[FFFFFF] <name> | [F4C96D]/travel cancel[FFFFFF] | [F4C96D]/road[FFFFFF] <name> shows the way on foot" },
                { "TravelHelp3", "  Walk to a waystone to learn it. No journey in a fight, with a captive, as an outlaw, or with the Ironbreaker." },
                { "TravelHelpAdmin", "  Admin: [F4C96D]/travel admin[FFFFFF] set | name | note | house | kind | toll | radius | hidden | enabled | mark | remove | list | unlock | lock | throne | tp | status" },
                { "ListHeader", "Waystones you know ({0} of {1}):" },
                { "ListLine", "  {0} ({1}) - {2} {3} - {4}" },
                { "ListNone", "You know no waystone yet. Walk to one to learn it; [F4C96D]/travel all[FFFFFF] names them." },
                { "ListMore", "  {0} more to find. [F4C96D]/road[FFFFFF] <name> shows the way." },
                { "ListReady", "  Your next journey: {0}." },
                { "ListFree", "  Free journeys left: {0}." },
                { "AllHeader", "Waystones of the realm ({0}):" },
                { "AllLine", "  {0} ({1}) - {2} {3} - {4}" },
                { "AllNone", "No waystone has been raised in the realm yet." },
                { "Known", "known" },
                { "NotKnown", "not yet found" },
                { "Now", "now" },
                { "In", "in {0}" },
                { "TollFree", "free" },
                { "TollN", "toll {0}" },
                { "ClosedToYou", "[E8913A]closed to you[FFFFFF]" },
                { "Kind.capital", "the capital" },
                { "Kind.seat", "a house seat" },
                { "Kind.holding", "a holding" },
                { "Kind.landmark", "a landmark" },
                { "SeatOf", "seat of {0}" },
                { "InfoHeader", "{0} ({1}):" },
                { "InfoWhere", "  {0} {1} from you. {2}. Found by {3} traveller(s)." },
                { "InfoNote", "  {0}" },
                { "InfoMark", "  Marked by the monument {0}." },
                { "Access.all", "  Open to every traveller." },
                { "Access.seat", "  Open to House {0} and its allies." },
                { "Access.house", "  Open to House {0} only." },
                { "Access.capital", "  Closed to those exiled from the towns." },
                { "Access.holding", "  Closed to travellers in the raid hours." },
                { "Unknown", "No waystone called '{0}'. [F4C96D]/travel all[FFFFFF] names them." },
                { "Ambiguous", "Which one: {0}?" },
                { "NotDiscovered", "You have not been to {0} yet. [F4C96D]/road[FFFFFF] {1} shows the way." },
                { "RoadMode", "The waystones sleep for now; follow the road instead." },
                { "SettingOut", "You set out for {0}. Stand still for {1} s: moving, fighting or being hurt breaks the journey." },
                { "SetOutToll", "  Toll on arrival: {0} marks, paid to the crown's treasury." },
                { "SetOutFree", "  This journey is free: {0}." },
                { "Free.trip", "one of your first journeys" },
                { "Free.roads", "the crown has opened the roads" },
                { "Free.none", "this road takes no toll" },
                { "Free.treasury", "the toll-keepers are away" },
                { "Free.admin", "staff" },
                { "Already", "You are already on the road to {0}. [F4C96D]/travel cancel[FFFFFF] stops." },
                { "Cancelled", "You stay where you are." },
                { "NotTravelling", "You are not on the road." },
                { "Broken.moved", "You moved, and the journey is broken." },
                { "Broken.hurt", "You were hurt, and the journey is broken." },
                { "Broken.fought", "You struck a blow, and the journey is broken." },
                { "Broken.other", "The journey is broken." },
                { "Arrived", "You arrive at {0}." },
                { "ArrivedToll", "  {0} marks paid to the crown's treasury." },
                { "ArrivedFree", "  Free journeys left: {0}." },
                { "ArrivedShield", "  The road watches over you for {0} s, unless you strike first." },
                { "RoadFailed", "The road failed you: you have not moved and paid nothing. Tell an admin if it happens again." },

                // Refusals
                { "B.combat", "You are in a fight. Wait {0} s after the last blow." },
                { "B.combatWarden", "You are in a fight. Wait until it is over." },
                { "B.captive", "A captive cannot travel." },
                { "B.holding", "You cannot travel while you hold a captive." },
                { "B.ironbreaker", "The Ironbreaker will not be carried along the roads. You must walk." },
                { "B.outlaw", "Outlaws may not use the realm's roads." },
                { "B.throne", "A rebellion is under way: no one travels to or from the Old Throne until it ends." },
                { "B.exiled", "You are exiled from the towns and may not travel to the capital." },
                { "B.seat", "{0} is the seat of House {1}, open only to its house and allies." },
                { "B.seatHouse", "{0} is the seat of House {1}, open only to its own." },
                { "B.seatNone", "That seat has no house named; it is closed." },
                { "B.raid", "Holdings are closed to travellers in the raid hours. March there." },
                { "B.near", "{0} is close enough to walk ({1})." },
                { "B.cooldown", "Your next journey is {0}." },
                { "B.toll", "The toll is {0} marks; your purse holds {1}. [F4C96D]/purse[FFFFFF]" },
                { "B.notreasury", "The toll-keepers are away (the treasury is not open). Try again later." },
                { "B.disabled", "That waystone is closed." },

                // Discovery
                { "Discovered", "You have found the waystone {0}. Return here any time: [F4C96D]/travel[FFFFFF] {1}" },
                { "DiscoveredCount", "  You know {0} of {1} waystones." },
                { "PopupTitle", "Waystone found" },
                { "PopupBody", "{0}\n{1}\nReturn here any time: [F4C96D]/travel[FFFFFF] {2}" },
                { "PopupButton", "Onward" },
                { "Wayfarer", "{0} has reached every waystone of the realm." },
                { "WayfarerYou", "You have reached every waystone of the realm." },
                { "Pathfinder", "  House {0} earns {1} season point(s) for it." },

                // /home
                { "HomeHelp", "Home: [F4C96D]/home[FFFFFF] takes you home. [F4C96D]/home set[FFFFFF] (in your own crest zone) | [F4C96D]/home info[FFFFFF] | [F4C96D]/home clear[FFFFFF]" },
                { "HomeSet", "Your home is here now. [F4C96D]/home[FFFFFF] brings you back ({0} s, toll {1})." },
                { "HomeSetCrest", "A home must be inside your own crest zone. Raise a crest, or stand inside your house's." },
                { "HomeSetCooldown", "You moved your home lately. Try again {0}." },
                { "HomeNone", "You have no home. Stand inside your own crest zone and type [F4C96D]/home set[FFFFFF]." },
                { "HomeLost", "Your home is no longer inside your own crest zone. Set a new one with [F4C96D]/home set[FFFFFF]." },
                { "HomeSiege", "Your hold is under siege. You must fight your way home on foot." },
                { "HomeUnknownLand", "The land cannot be read right now. Try again later." },
                { "HomeInfo", "Your home is {0} {1} from here. {2}" },
                { "HomeValid", "It is inside your crest zone." },
                { "HomeInvalid", "It is no longer inside your crest zone." },
                { "HomeCleared", "Your home is forgotten." },
                { "HomeName", "your home" },
                { "HomeReady", "  Your next journey home: {0}." },

                // /road
                { "RoadHelp", "Follow a road: [F4C96D]/road[FFFFFF] <waystone> or [F4C96D]/road home[FFFFFF] calls the way as you walk. [F4C96D]/road stop[FFFFFF] ends it." },
                { "RoadStart", "The road to {0}: {1} to the {2}. The way is called every {3} s." },
                { "RoadLine", "{0}: {1} to the {2}." },
                { "RoadArrived", "You have reached {0}." },
                { "RoadStopped", "You leave the road." },
                { "RoadNone", "You are not following a road." },
                { "RoadExpired", "The road to {0} fades. [F4C96D]/road[FFFFFF] {1} to follow it again." },
                { "RoadHere", "You are already at {0}." },
                { "Dir.0", "north" }, { "Dir.1", "north-east" }, { "Dir.2", "east" }, { "Dir.3", "south-east" },
                { "Dir.4", "south" }, { "Dir.5", "south-west" }, { "Dir.6", "west" }, { "Dir.7", "north-west" },
                { "Metres", "{0} m" },
                { "Km", "{0} km" },
                { "Sec", "{0} s" },
                { "Min", "{0} min" },
                { "Hours", "{0} h" },

                // /kit
                { "KitHelp", "Kits: [F4C96D]/kit[FFFFFF] lists yours, [F4C96D]/kit[FFFFFF] <name> takes one, [F4C96D]/kit collect[FFFFFF] takes what did not fit." },
                { "KitHelpAdmin", "  Admin: [F4C96D]/kit admin[FFFFFF] items <word> | check | reset <player> <kit|all>" },
                { "KitHeader", "Your kits:" },
                { "KitLine", "  {0} ({1}) - {2}" },
                { "KitContents", "    {0}" },
                { "KitNone", "No kits are offered on this server." },
                { "KitUnknown", "No kit called '{0}'. [F4C96D]/kit[FFFFFF] lists them." },
                { "Kit.ready", "ready: [F4C96D]/kit[FFFFFF] {0}" },
                { "Kit.taken", "taken" },
                { "Kit.today", "taken today; again {0}" },
                { "Kit.season", "taken this season" },
                { "Kit.noseason", "no season is running" },
                { "Kit.nohouse", "for sworn members of a house" },
                { "Kit.newmember", "after {0} in your house ({1} left)" },
                { "Kit.smallhouse", "for houses of {0} or more" },
                { "Kit.play", "after {0} min of play this season ({1} so far)" },
                { "Kit.newcomers", "for newcomers only" },
                { "Kit.late", "a newcomer's kit, and your first days are past" },
                { "Kit.busy", "many newcomers took one today; try again later" },
                { "Kit.unavailable", "unavailable (no item this server knows)" },
                { "Kit.off", "closed" },
                { "KitNotNow", "The {0} is not for you now: {1}." },
                { "KitCombat", "Not in the middle of a fight." },
                { "KitNoBody", "You cannot take a kit in your present state." },
                { "KitTaken", "You take the {0}: {1}." },
                { "KitPending", "  Your packs are full: the rest waits. Make room, then [F4C96D]/kit collect[FFFFFF]." },
                { "KitOwedFull", "Too much is already waiting for you. [F4C96D]/kit collect[FFFFFF] first." },
                { "CollectNone", "Nothing is waiting for you." },
                { "Collected", "You collect: {0}." },
                { "CollectFull", "Your packs are still full. Make room and try again." },
                { "OwedOnJoin", "Items from your kits still wait for you: [F4C96D]/kit collect[FFFFFF]." },
                { "Hint", "A traveller's pack waits for every newcomer: [F4C96D]/kit {0}[FFFFFF]. Waystones you find can be reached again with [F4C96D]/travel[FFFFFF]." },

                // Admin
                { "AdminSetNew", "Waystone {0} ({1}) raised here, radius {2} m." },
                { "AdminSetMoved", "Waystone {0} moved here." },
                { "AdminSetHouse", "  Name its house: [F4C96D]/travel admin house[FFFFFF] {0} <house>" },
                { "AdminSetMark", "  Mark it with a monument: [F4C96D]/sculpt place[FFFFFF] {0} (then [F4C96D]/travel admin mark[FFFFFF] {1} {0})" },
                { "AdminSetUsage", "Usage: [F4C96D]/travel admin set[FFFFFF] <id> <capital|seat|holding|landmark> [name]" },
                { "AdminBadId", "A waystone id is 2 to 24 letters, digits or dashes." },
                { "AdminBadKind", "The kind is capital, seat, holding or landmark." },
                { "AdminTooMany", "The realm already has {0} waystones." },
                { "AdminNoSuch", "No waystone with the id '{0}'. [F4C96D]/travel admin list[FFFFFF]" },
                { "AdminDone", "Done: {0}." },
                { "AdminRemoveAsk", "This removes {0} and forgets who found it. Type [F4C96D]/travel admin remove[FFFFFF] {1} confirm" },
                { "AdminRemoved", "Waystone {0} is removed." },
                { "AdminListHeader", "Waystones ({0}):" },
                { "AdminListLine", "  {0} '{1}' {2}{3} at {4} r{5} {6}{7} - found by {8}" },
                { "AdminUnlocked", "{0} now knows {1} waystone(s)." },
                { "AdminThroneSet", "The Old Throne's point is set here (radius {0} m in rebellions)." },
                { "AdminThroneCleared", "The admin throne point is cleared; the game's throne position is used if it is known." },
                { "AdminThroneUnknown", "  The throne's position: {0}." },
                { "AdminTp", "You are at {0}." },
                { "AdminUsage", "Usage: {0}" },
                { "StatusHeader", "Roads: {0} waystone(s), {1} traveller(s) on record, {2} journey(s) under way, {3} road(s) followed." },
                { "StatusTrips", "  Journeys {0}, tolls collected {1} marks, tolls not collected {2}, arrivals not confirmed {3}, kits given {4}." },
                { "StatusThrone", "  Throne point: {0}. Mode: {1}. Treasury: {2}. Warden: {3}. Seasons: {4}." },
                { "StatusKits", "  Kits: {0}." },
                { "ItemsHeader", "Item names holding '{0}' ({1}):" },
                { "ItemsLine", "  {0}" },
                { "ItemsNone", "No item name holds '{0}'." },
                { "ItemsNotReady", "The game's item list is not loaded yet. Try again once the world is up." },
                { "CheckHeader", "Kits checked against the game's item list:" },
                { "CheckLine", "  {0} ({1}): {2}" },
                { "CheckBad", "    unknown item '{0}' (skipped). Find the real name: [F4C96D]/kit admin[FFFFFF] items <word>" },
                { "KitReset", "{0}'s kit claims cleared: {1}." },
                { "Yes", "yes" },
                { "No", "no" },
                { "None", "none" },
                { "Loaded", "loaded" },
                { "Absent", "absent" }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
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

        private void Refuse(Player player, Refusal r)
        {
            ReplyError(player, r.Key, r.Args);
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Discovered", "Arrived", "HomeSet", "HomeCleared", "KitTaken", "Collected", "RoadArrived", "WayfarerYou",
            "AdminSetNew", "AdminSetMoved", "AdminDone", "AdminRemoved", "AdminUnlocked", "AdminThroneSet", "AdminTp", "KitReset"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "SettingOut", "Already", "OwedOnJoin", "Hint", "RoadExpired", "HomeLost", "AdminRemoveAsk", "Cancelled",
            "RoadMode", "AdminThroneCleared"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintError("oxide/config/RealmTravel.json could not be read (" + ex.Message + "); using the defaults for this run.");
                config = null;
            }
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (loadFailed) return;
            // Re-sent on hot reload (docs/oxide-rok-api.md 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.General.TickSeconds, SafeTick);
            CheckKitItems(null);
            foreach (Player p in OnlinePlayers()) Rec(p);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            journeys.Clear();                                   // nothing is charged for a journey cut short
            roads.Clear();
            SaveData();
        }

        #endregion

        #region Hooks

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            try
            {
                Traveller rec = Rec(player);
                rec.LastSeen = Now();
                TrackHouse(player, rec);
                dirty = true;
                ulong id = player.Id;
                if (config.General.Enabled && config.Kits.Enabled && config.Kits.NewcomerHint && !rec.HintSent && StarterKit() != null)
                    timer.Once(config.Kits.NewcomerHintDelaySeconds, delegate { SendHint(id); });
                if (rec.Owed.Count > 0) timer.Once(15f, delegate { PayOwedOnJoin(id); });
            }
            catch (Exception ex) { PrintError("OnPlayerConnected: " + ex.Message); }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null) return;
            journeys.Remove(player.Id);
            roads.Remove(player.Id);
            shieldUntil.Remove(player.Id);
            if (loadFailed || data == null || player.IsServer) return;
            Traveller rec;
            if (data.Players.TryGetValue(player.Id.ToString(), out rec)) { rec.LastSeen = Now(); dirty = true; }
        }

        // RB 1 [OPJ L162]. Watches fights (no journey while in one), breaks journeys on a blow, and holds the arrival
        // shield: a traveller who just arrived takes no damage from players until the shield ends or they strike first.
        // Blocking follows RealmWarden: evt.Cancel + Damage.Amount = 0 + return true. Everything else returns null.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Entity == null || evt.Damage == null) return null;
            try
            {
                Damage d = evt.Damage;
                if (d.Amount <= 0f || (d.DamageTypes & DamageType.Healing) != 0) return null;
                Player victim = evt.Entity.IsPlayer ? evt.Entity.Owner : null;
                if (victim != null && victim.IsServer) victim = null;
                Player attacker = null;
                Entity src = d.DamageSource;
                if (src != null && src.IsPlayer && src.Owner != null && !src.Owner.IsServer) attacker = src.Owner;
                if (attacker != null && victim != null && attacker.Id == victim.Id) attacker = null;   // falling on your own sword
                DateTime now = Now();

                if (attacker != null)
                {
                    shieldUntil.Remove(attacker.Id);                 // striking first ends your own shield
                    if (victim != null) lastPvp[attacker.Id] = now; else lastBeast[attacker.Id] = now;
                    BreakJourney(attacker, "Broken.fought");
                }
                if (victim == null) return null;
                if (attacker != null && !evt.Cancelled && Shielded(victim.Id, now))
                {
                    evt.Cancel("RealmTravel arrival shield");
                    d.Amount = 0f;
                    return true;
                }
                if (attacker != null) lastPvp[victim.Id] = now;
                else if (src != null) lastBeast[victim.Id] = now;   // a creature; a fall or the weather has no source
                BreakJourney(victim, "Broken.hurt");
            }
            catch (Exception ex)
            {
                if (!Throttled("dmg", 60)) PrintWarning("Damage watch failed: " + ex.Message);
            }
            return null;
        }

        // RB 1 [OPJ L188]; always null (never changes a death).
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            try
            {
                if (evt == null || evt.Entity == null || !evt.Entity.IsPlayer) return null;
                Player p = evt.Entity.Owner;
                if (p == null || p.IsServer) return null;
                journeys.Remove(p.Id);
                shieldUntil.Remove(p.Id);
                lastPvp.Remove(p.Id);
                lastBeast.Remove(p.Id);
            }
            catch (Exception) { }
            return null;
        }

        // Learn where the throne is from a completed capture, when neither an admin nor the game has said.
        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Player == null) return;
            try
            {
                if (evt.State != AncientThroneCaptureEvent.States.Completed) return;
                UnityEngine.Vector3 pos;
                if (!TryPos(evt.Player, out pos)) return;
                data.LearnedThrone = true;
                data.LearnedX = pos.x; data.LearnedY = pos.y; data.LearnedZ = pos.z;
                dirty = true;
            }
            catch (Exception) { }
        }

        #endregion

        #region Ticks

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { if (!Throttled("tick", 60)) PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            if (loadFailed || data == null || !config.General.Enabled) return;
            DateTime now = Now();
            TickJourneys(now);
            if (now >= nextDiscovery)
            {
                nextDiscovery = now.AddSeconds(config.Discovery.CheckSeconds);
                if (config.Discovery.Enabled) TickDiscovery();
            }
            TickRoads(now);
            if (now >= nextMinute)
            {
                bool first = nextMinute == DateTime.MinValue;
                nextMinute = now.AddSeconds(60);
                TickMinute(now, !first);
            }
            if (dirty && now >= nextSave) SaveData();
        }

        private void TickJourneys(DateTime now)
        {
            if (journeys.Count == 0) return;
            foreach (ulong id in new List<ulong>(journeys.Keys))
            {
                Journey j;
                if (!journeys.TryGetValue(id, out j)) continue;
                Player p = OnlineById(id);
                UnityEngine.Vector3 pos;
                if (p == null || !TryPos(p, out pos)) { journeys.Remove(id); continue; }
                if (Dist(pos.x, pos.y, pos.z, j.SX, j.SY, j.SZ) > config.Travel.MoveTolerance)
                {
                    journeys.Remove(id);
                    ReplyError(p, "Broken.moved");
                    continue;
                }
                if (now >= j.Ends) Complete(p, j);
            }
        }

        private void TickDiscovery()
        {
            if (data.Waystones.Count == 0) return;
            foreach (Player p in OnlinePlayers())
            {
                UnityEngine.Vector3 pos;
                if (!TryPos(p, out pos)) continue;
                Traveller rec = Rec(p);
                foreach (Waystone w in data.Waystones.Values)
                {
                    if (!w.Enabled || rec.Unlocked.Contains(w.Id)) continue;
                    if (Dist(pos.x, pos.y, pos.z, w.X, w.Y, w.Z) <= w.Radius) Discover(p, rec, w);
                }
            }
        }

        private void TickRoads(DateTime now)
        {
            if (roads.Count == 0) return;
            foreach (ulong id in new List<ulong>(roads.Keys))
            {
                Road r;
                if (!roads.TryGetValue(id, out r)) continue;
                Player p = OnlineById(id);
                if (p == null) { roads.Remove(id); continue; }
                if (now > r.Until)
                {
                    roads.Remove(id);
                    Reply(p, "RoadExpired", r.Name, r.Target.Length > 0 ? r.Target : "home");
                    continue;
                }
                if (now < r.Next) continue;
                UnityEngine.Vector3 pos;
                if (!TryPos(p, out pos)) continue;
                r.Next = now.AddSeconds(config.Roads.UpdateSeconds);
                float d = FlatDist(pos.x, pos.z, r.X, r.Z);
                if (d <= config.Roads.ArrivalRadius)
                {
                    roads.Remove(id);
                    Reply(p, "RoadArrived", r.Name);
                    continue;
                }
                Reply(p, "RoadLine", r.Name, DistText(p, d), DirText(p, pos.x, pos.z, r.X, r.Z));
            }
        }

        // Once a minute: who is in which house (for the house kit), play time this season (for the season kit),
        // who is a newcomer (for the starter kit), and housekeeping. countMinute is false on the first pass after a
        // load, so a reload does not credit a minute twice.
        private void TickMinute(DateTime now, bool countMinute)
        {
            int season = SeasonNumber();
            foreach (Player p in OnlinePlayers())
            {
                Traveller rec = Rec(p);
                rec.LastSeen = now;
                TrackHouse(p, rec);
                if (rec.PlaySeason != season) { rec.PlaySeason = season; rec.PlaySeasonMinutes = 0; }
                if (countMinute && season > 0 && rec.PlaySeasonMinutes < 1000000) rec.PlaySeasonMinutes++;
                NoteNewcomer(p, rec);
                dirty = true;
            }
            data.StarterClaims.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 24; });
            foreach (string k in new List<string>(throttle.Keys)) if (throttle[k] < now) throttle.Remove(k);
            foreach (ulong k in new List<ulong>(lastPvp.Keys)) if ((now - lastPvp[k]).TotalSeconds > 3600) lastPvp.Remove(k);
            foreach (ulong k in new List<ulong>(lastBeast.Keys)) if ((now - lastBeast[k]).TotalSeconds > 3600) lastBeast.Remove(k);
            foreach (ulong k in new List<ulong>(shieldUntil.Keys)) if (shieldUntil[k] < now) shieldUntil.Remove(k);
            PrunePlayers();
        }

        private void PrunePlayers()
        {
            int over = data.Players.Count - config.General.MaxPlayersKept;
            if (over <= 0) return;
            var list = new List<KeyValuePair<string, Traveller>>(data.Players);
            list.Sort(delegate(KeyValuePair<string, Traveller> a, KeyValuePair<string, Traveller> b) { return a.Value.LastSeen.CompareTo(b.Value.LastSeen); });
            for (int i = 0; i < over && i < list.Count; i++)
            {
                if (list[i].Value.Owed.Count > 0) continue;     // never forget items still owed
                data.Players.Remove(list[i].Key);
            }
            dirty = true;
        }

        #endregion

        #region Travel

        [ChatCommand("travel")]
        private void CmdTravel(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "admin") { CmdTravelAdmin(player, args); return; }
            if (!config.General.Enabled) { ReplyError(player, "Closed"); return; }
            if (!config.Travel.Enabled) { ReplyError(player, "PartClosed"); return; }
            Traveller rec = Rec(player);
            switch (sub)
            {
                case "":
                case "list":
                    ShowKnown(player, rec);
                    return;
                case "help":
                    ShowTravelHelp(player);
                    return;
                case "all":
                    ShowAll(player, rec);
                    return;
                case "cancel":
                case "stop":
                    if (journeys.Remove(player.Id)) Reply(player, "Cancelled"); else ReplyError(player, "NotTravelling");
                    return;
                case "info":
                    {
                        if (args.Length < 2) { ShowTravelHelp(player); return; }
                        string amb;
                        Waystone w = FindWaystone(JoinFrom(args, 1), rec, false, out amb);
                        if (w == null) { ReplyMissing(player, JoinFrom(args, 1), amb); return; }
                        ShowInfo(player, rec, w);
                        return;
                    }
                default:
                    {
                        string amb;
                        string text = JoinFrom(args, 0);
                        Waystone w = FindWaystone(text, rec, false, out amb);
                        if (w == null && sub == "home" && args.Length == 1 && config.Home.Enabled) { GoHome(player, rec); return; }
                        if (w == null) { ReplyMissing(player, text, amb); return; }
                        if (!rec.Unlocked.Contains(w.Id)) { ReplyError(player, "NotDiscovered", w.Name, w.Id); return; }
                        if (config.Travel.Mode == "road") { Reply(player, "RoadMode"); StartRoad(player, w.Name, w.Id, w.X, w.Y, w.Z); return; }
                        StartWaystone(player, rec, w);
                        return;
                    }
            }
        }

        private void ShowTravelHelp(Player player)
        {
            Reply(player, "TravelHelp1", Secs(config.Travel.ChannelSeconds));
            Reply(player, "TravelHelp2");
            Reply(player, "TravelHelp3");
            if (IsAdmin(player)) Reply(player, "TravelHelpAdmin");
        }

        private void ShowKnown(Player player, Traveller rec)
        {
            List<Waystone> visible = Visible(rec);
            var known = new List<Waystone>();
            foreach (Waystone w in visible) if (rec.Unlocked.Contains(w.Id)) known.Add(w);
            if (known.Count == 0)
            {
                Reply(player, "ListNone");
                ShowTravelHelp(player);
                return;
            }
            UnityEngine.Vector3 pos;
            bool hasPos = TryPos(player, out pos);
            SortByDistance(known, pos, hasPos);
            Reply(player, "ListHeader", known.Count, visible.Count);
            foreach (Waystone w in known)
            {
                float d = hasPos ? Dist(pos.x, pos.y, pos.z, w.X, w.Y, w.Z) : 0f;
                string status;
                Refusal r = WaystoneBlocker(player, w);
                if (r != null) status = Msg("ClosedToYou", player);
                else
                {
                    long toll = Discounted(player, WaystoneToll(w, d));
                    status = toll > 0 ? Fmt("TollN", player, toll) : Msg("TollFree", player);
                }
                Reply(player, "ListLine", w.Name, KindText(player, w), hasPos ? DistText(player, d) : "?",
                    hasPos ? DirText(player, pos.x, pos.z, w.X, w.Z) : "", status);
            }
            if (visible.Count > known.Count) Reply(player, "ListMore", visible.Count - known.Count);
            Reply(player, "ListReady", ReadyText(player, rec.TravelReadyAt));
            int free = config.Travel.FreeTrips - rec.FreeTripsUsed;
            if (free > 0) Reply(player, "ListFree", free);
        }

        private void ShowAll(Player player, Traveller rec)
        {
            List<Waystone> visible = Visible(rec);
            if (visible.Count == 0) { Reply(player, "AllNone"); return; }
            UnityEngine.Vector3 pos;
            bool hasPos = TryPos(player, out pos);
            SortByDistance(visible, pos, hasPos);
            Reply(player, "AllHeader", visible.Count);
            foreach (Waystone w in visible)
            {
                float d = hasPos ? Dist(pos.x, pos.y, pos.z, w.X, w.Y, w.Z) : 0f;
                Reply(player, "AllLine", w.Name, KindText(player, w), hasPos ? DistText(player, d) : "?",
                    hasPos ? DirText(player, pos.x, pos.z, w.X, w.Z) : "", Msg(rec.Unlocked.Contains(w.Id) ? "Known" : "NotKnown", player));
            }
        }

        private void ShowInfo(Player player, Traveller rec, Waystone w)
        {
            UnityEngine.Vector3 pos;
            bool hasPos = TryPos(player, out pos);
            float d = hasPos ? Dist(pos.x, pos.y, pos.z, w.X, w.Y, w.Z) : 0f;
            long toll = Discounted(player, WaystoneToll(w, d));
            Reply(player, "InfoHeader", w.Name, KindText(player, w));
            Reply(player, "InfoWhere", hasPos ? DistText(player, d) : "?", hasPos ? DirText(player, pos.x, pos.z, w.X, w.Z) : "",
                CapFirst(toll > 0 ? Fmt("TollN", player, toll) : Msg("TollFree", player)), w.Visitors);
            if (!string.IsNullOrEmpty(w.Note)) Reply(player, "InfoNote", w.Note);
            if (!string.IsNullOrEmpty(w.Monument)) Reply(player, "InfoMark", w.Monument);
            if (w.Kind == KSeat && !string.IsNullOrEmpty(w.House) && config.Travel.SeatAccess != "all")
                Reply(player, config.Travel.SeatAccess == "house" ? "Access.house" : "Access.seat", HouseTint(w.House));
            else if (w.Kind == KCapital && config.Travel.ExilesBarredFromCapital) Reply(player, "Access.capital");
            else if (w.Kind == KHolding && config.Travel.HoldingsClosedInRaidHours) Reply(player, "Access.holding");
            else Reply(player, "Access.all");
        }

        private void ReplyMissing(Player player, string text, string ambiguous)
        {
            if (ambiguous != null) ReplyError(player, "Ambiguous", ambiguous);
            else ReplyError(player, "Unknown", Clean(text, 30));
        }

        private void StartWaystone(Player player, Traveller rec, Waystone w)
        {
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
            Journey current;
            if (journeys.TryGetValue(player.Id, out current)) { ReplyError(player, "Already", current.TargetName); return; }
            bool exempt = AdminExempt(player);
            DateTime now = Now();
            if (!exempt && rec.TravelReadyAt > now) { ReplyError(player, "B.cooldown", ReadyText(player, rec.TravelReadyAt)); return; }
            Refusal r = PersonBlocker(player, pos);
            if (r == null) r = WaystoneBlocker(player, w);
            if (r != null) { Refuse(player, r); return; }
            float d = Dist(pos.x, pos.y, pos.z, w.X, w.Y, w.Z);
            if (d < config.Travel.MinDistance) { ReplyError(player, "B.near", w.Name, DistText(player, d)); return; }

            string freeWhy;
            bool freeTrip;
            long toll = Quote(player, rec, WaystoneToll(w, d), true, exempt, out freeWhy, out freeTrip);
            if (!CanPay(player, toll)) return;
            var j = new Journey { Kind = JWaystone, Target = w.Id, TargetName = w.Name, SX = pos.x, SY = pos.y, SZ = pos.z, Toll = toll, FreeTrip = freeTrip };
            BeginJourney(player, j, exempt ? 0f : config.Travel.ChannelSeconds, freeWhy);
        }

        private void BeginJourney(Player player, Journey j, float seconds, string freeWhy)
        {
            j.Ends = Now().AddSeconds(seconds);
            journeys[player.Id] = j;
            roads.Remove(player.Id);
            Reply(player, "SettingOut", j.TargetName, Secs(seconds));
            if (j.Toll > 0) Reply(player, "SetOutToll", j.Toll);
            else if (freeWhy != null) Reply(player, "SetOutFree", freeWhy);
            if (seconds <= 0f) Complete(player, j);
        }

        // The toll a player would pay now: the crown's decree first, then the sworn houses' share, then a free trip.
        private long Quote(Player player, Traveller rec, long baseToll, bool mayUseFreeTrip, bool exempt, out string freeWhy, out bool freeTrip)
        {
            freeWhy = null;
            freeTrip = false;
            if (exempt) { freeWhy = Msg("Free.admin", player); return 0; }
            if (baseToll <= 0) { freeWhy = Msg("Free.none", player); return 0; }
            if (RealmTreasury == null && config.Travel.TollFreeWithoutTreasury) { freeWhy = Msg("Free.treasury", player); return 0; }
            long toll = Discounted(player, baseToll);
            if (toll <= 0) { freeWhy = Msg("Free.roads", player); return 0; }
            if (mayUseFreeTrip && rec.FreeTripsUsed < config.Travel.FreeTrips)
            {
                freeTrip = true;
                freeWhy = Msg("Free.trip", player);
                return 0;
            }
            return toll;
        }

        private long WaystoneToll(Waystone w, float distance)
        {
            if (w.Toll >= 0) return Math.Min(w.Toll, MaxExplicitToll);
            long t = config.Travel.BaseToll + (long)Math.Floor(distance / 100f) * config.Travel.TollPer100m;
            return ClampL(t, 0, config.Travel.MaxToll);
        }

        private long Discounted(Player player, long toll)
        {
            if (toll <= 0) return 0;
            if (OpenRoads()) return toll * config.Travel.OpenRoadsTollPercent / 100;
            if (SwornToCrown(player)) return toll * config.Travel.SwornTollPercent / 100;
            return toll;
        }

        private bool CanPay(Player player, long toll)
        {
            if (toll <= 0) return true;
            if (RealmTreasury == null) { ReplyError(player, "B.notreasury"); return false; }
            long purse = Purse(player);
            if (purse < toll) { ReplyError(player, "B.toll", toll, purse); return false; }
            return true;
        }

        // Rules about the traveller themselves (both ends of every journey, checked at the start and again on arrival).
        private Refusal PersonBlocker(Player player, UnityEngine.Vector3 pos)
        {
            string id = player.Id.ToString();
            if (config.Travel.BlockCaptives)
            {
                int c = CaptiveState(player);
                if (c == 1 || AskBool(CrownAndConsequences, "IsHeldForRansom", player.Id)) return new Refusal("B.captive");
                if (c == 2) return new Refusal("B.holding");
            }
            int left = CombatSecondsLeft(player.Id);
            if (left > 0) return new Refusal("B.combat", left);
            if (config.Travel.UseWardenCombat && AskBool(RealmWarden, "IsInCombat", player.Id)) return new Refusal("B.combatWarden");
            if (config.Travel.BlockIronbreaker && AskBool(RealmLegendary, "IsBearer", id)) return new Refusal("B.ironbreaker");
            if (config.Travel.BlockOutlaws && (AskBool(RealmContracts, "IsOutlaw", id) || AskBool(RealmLaws, "IsCourtOutlaw", id)))
                return new Refusal("B.outlaw");
            if (NearThroneInRebellion(pos)) return new Refusal("B.throne");
            return null;
        }

        // Rules about a waystone as a destination.
        private Refusal WaystoneBlocker(Player player, Waystone w)
        {
            if (!w.Enabled) return new Refusal("B.disabled");
            if (w.Kind == KSeat && config.Travel.SeatAccess != "all")
            {
                if (string.IsNullOrEmpty(w.House)) return new Refusal("B.seatNone");
                if (!MayEnterSeat(player, w.House))
                    return new Refusal(config.Travel.SeatAccess == "house" ? "B.seatHouse" : "B.seat", w.Name, HouseTint(w.House));
            }
            if (w.Kind == KCapital && config.Travel.ExilesBarredFromCapital && AskBool(RealmLaws, "IsExiled", player.Id.ToString()))
                return new Refusal("B.exiled");
            if (w.Kind == KHolding && config.Travel.HoldingsClosedInRaidHours && AskBool(RealmWarden, "IsRaidHourNow"))
                return new Refusal("B.raid");
            if (NearThroneInRebellion(V(w.X, w.Y, w.Z))) return new Refusal("B.throne");
            return null;
        }

        // A seat is open to its own house and, with SeatAccess "allies", to its liege, its vassals, fellow vassals of
        // the same liege and houses it holds a treaty with (RealmHouses). Without RealmHouses nobody has a house.
        private bool MayEnterSeat(Player player, string seatHouse)
        {
            string mine = HouseOf(player.Id);
            if (mine == null) return false;
            if (SameText(mine, seatHouse)) return true;
            if (config.Travel.SeatAccess == "house") return false;
            string myLiege = AskString(RealmHouses, "GetLiege", mine);
            string theirLiege = AskString(RealmHouses, "GetLiege", seatHouse);
            if (SameText(myLiege, seatHouse) || SameText(theirLiege, mine)) return true;
            if (myLiege != null && SameText(myLiege, theirLiege)) return true;
            return AskBool(RealmHouses, "HasTreaty", mine, seatHouse);
        }

        private void Complete(Player player, Journey j)
        {
            journeys.Remove(player.Id);
            Traveller rec = Rec(player);
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) return;
            bool exempt = AdminExempt(player);
            Refusal r = PersonBlocker(player, pos);
            float dx, dy, dz;
            string name = j.TargetName;
            if (j.Kind == JHome)
            {
                if (r == null) r = HomeBlocker(player, rec);
                dx = rec.HomeX; dy = rec.HomeY; dz = rec.HomeZ;
            }
            else
            {
                Waystone w;
                if (!data.Waystones.TryGetValue(j.Target, out w)) { ReplyError(player, "B.disabled"); return; }
                if (r == null) r = WaystoneBlocker(player, w);
                dx = w.X; dy = w.Y; dz = w.Z;
                name = w.Name;
            }
            if (r != null) { Refuse(player, r); return; }

            long toll = j.Toll;
            if (toll > 0 && RealmTreasury == null)
            {
                if (!config.Travel.TollFreeWithoutTreasury) { ReplyError(player, "B.notreasury"); return; }
                toll = 0;
            }
            if (toll > 0)
            {
                long purse = Purse(player);
                if (purse < toll) { ReplyError(player, "B.toll", toll, purse); return; }
            }

            if (RealmSentinel != null) Ask(RealmSentinel, "SentinelGrace", player.Id, 10f);
            if (!Teleport(player, dx, dy + config.Travel.ArrivalLift, dz)) { ReplyError(player, "RoadFailed"); return; }

            // Charged in the same tick as the purse check above: nothing can empty the purse in between.
            bool paid = false;
            if (toll > 0)
            {
                paid = AskBool(RealmTreasury, "ChargeMarks", player.Id.ToString(), player.Name, toll, "RealmTravel",
                    (j.Kind == JHome ? "road toll: home" : "road toll: " + j.Target));
                if (paid) data.TollsCollected += toll;
                else
                {
                    data.UnpaidTolls++;
                    PrintWarning("Toll of " + toll + " marks for " + Clean(player.Name, 40) + " was not collected (RealmTreasury refused ChargeMarks).");
                }
            }
            DateTime now = Now();
            if (j.FreeTrip) rec.FreeTripsUsed++;
            rec.Trips++;
            data.Trips++;
            if (!exempt)
            {
                if (j.Kind == JHome) rec.HomeReadyAt = now.AddMinutes(config.Home.CooldownMinutes);
                else rec.TravelReadyAt = now.AddMinutes(config.Travel.CooldownMinutes);
            }
            if (config.Travel.ArrivalShieldSeconds > 0) shieldUntil[player.Id] = now.AddSeconds(config.Travel.ArrivalShieldSeconds);
            lastBeast.Remove(player.Id);
            dirty = true;
            SaveData();

            Reply(player, "Arrived", name);
            if (paid) Reply(player, "ArrivedToll", toll);
            if (j.FreeTrip) Reply(player, "ArrivedFree", Math.Max(0, config.Travel.FreeTrips - rec.FreeTripsUsed));
            if (config.Travel.ArrivalShieldSeconds > 0) Reply(player, "ArrivedShield", config.Travel.ArrivalShieldSeconds);
            ulong id = player.Id;
            float ax = dx, ay = dy, az = dz;
            timer.Once(3f, delegate { CheckArrival(id, ax, ay, az); });
        }

        // UNVERIFIED in game that the server's own view of the position follows CharacterTeleport.Teleport at once:
        // this counts the journeys after which the traveller is not near the waystone 3 s later (/travel admin status).
        private void CheckArrival(ulong id, float x, float y, float z)
        {
            if (loadFailed || data == null) return;
            Player p = OnlineById(id);
            UnityEngine.Vector3 pos;
            if (p == null || !TryPos(p, out pos)) return;
            if (Dist(pos.x, pos.y, pos.z, x, y, z) <= 8f) return;
            data.ArrivalsUnconfirmed++;
            dirty = true;
            if (!Throttled("arrival", 300))
                PrintWarning("Arrival not confirmed: " + Clean(p.Name, 40) + " is " + Math.Round(Dist(pos.x, pos.y, pos.z, x, y, z)) + " m from the destination 3 s after the move.");
        }

        private void BreakJourney(Player player, string key)
        {
            if (player == null || !journeys.Remove(player.Id)) return;
            ReplyError(player, key);
        }

        private bool Shielded(ulong id, DateTime now)
        {
            DateTime until;
            return shieldUntil.TryGetValue(id, out until) && until > now;
        }

        private int CombatSecondsLeft(ulong id)
        {
            DateTime now = Now();
            double left = 0;
            DateTime t;
            if (lastPvp.TryGetValue(id, out t)) left = Math.Max(left, config.Travel.CombatSeconds - (now - t).TotalSeconds);
            if (lastBeast.TryGetValue(id, out t)) left = Math.Max(left, config.Travel.BeastCombatSeconds - (now - t).TotalSeconds);
            return left > 0 ? (int)Math.Ceiling(left) : 0;
        }

        // [ASM] PlayerCaptureManager on the player's own entity: 1 = held captive, 2 = holding one, 0 = neither.
        // UNVERIFIED which manager carries Captured (expected: the captive's own, as CrownAndConsequences assumes).
        private static int CaptiveState(Player p)
        {
            try
            {
                if (p == null || p.Entity == null) return 0;
                PlayerCaptureManager m = p.Entity.TryGet<PlayerCaptureManager>();
                if (m == null) return 0;
                if (m.Captured) return 1;
                if (m.HoldingCaptive || m.CaptivePlayerID != 0) return 2;
            }
            catch (Exception) { }
            return 0;
        }

        private bool NearThroneInRebellion(UnityEngine.Vector3 pos)
        {
            if (!config.Travel.BlockNearThroneInRebellion || config.Travel.ThroneRadius <= 0f) return false;
            if (!AskBool(CrownAndConsequences, "IsRebellionActive")) return false;
            UnityEngine.Vector3 throne;
            string source;
            if (!ThronePoint(out throne, out source)) return false;
            return FlatDist(pos.x, pos.z, throne.x, throne.z) <= config.Travel.ThroneRadius;
        }

        // The admin's point first, then the game's own throne entity, then where a capture last completed.
        private bool ThronePoint(out UnityEngine.Vector3 pos, out string source)
        {
            pos = new UnityEngine.Vector3();
            source = "unknown";
            if (data.HasThrone) { pos = V(data.ThroneX, data.ThroneY, data.ThroneZ); source = "admin"; return true; }
            try
            {
                UnityEngine.Vector3 g = AncientThrone.EntityPosition;              // [DEC] static; zero when not loaded
                if (g.x != 0f || g.y != 0f || g.z != 0f) { pos = g; source = "game"; return true; }
            }
            catch (Exception) { }
            if (data.LearnedThrone) { pos = V(data.LearnedX, data.LearnedY, data.LearnedZ); source = "capture"; return true; }
            return false;
        }

        // [DEC] CharacterTeleport.Teleport(Vector3), the call the game's own /tp makes on the server.
        private bool Teleport(Player player, float x, float y, float z)
        {
            try
            {
                if (player == null || player.Entity == null) return false;
                CharacterTeleport tp = player.Entity.GetOrCreate<CharacterTeleport>();
                if (tp == null) return false;
                tp.Teleport(V(x, y, z));
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("Teleport failed for " + Clean(player.Name, 40) + ": " + ex.Message);
                return false;
            }
        }

        #endregion

        #region Discovery

        private void Discover(Player player, Traveller rec, Waystone w)
        {
            rec.Unlocked.Add(w.Id);
            w.Visitors++;
            dirty = true;
            int total = 0, known = 0;
            foreach (Waystone o in data.Waystones.Values)
            {
                if (!o.Enabled || o.Hidden) continue;
                total++;
                if (rec.Unlocked.Contains(o.Id)) known++;
            }
            Reply(player, "Discovered", w.Name, w.Id);
            if (!w.Hidden) Reply(player, "DiscoveredCount", known, total);
            if (config.Discovery.PopupOnDiscovery && PopupsFor(player))
                ShowInfoPopup(player, Msg("PopupTitle", player), Fmt("PopupBody", player, w.Name, string.IsNullOrEmpty(w.Note) ? KindText(player, w) : w.Note, w.Id),
                    Msg("PopupButton", player));
            if (!w.Hidden && total >= config.Discovery.WayfarerMinWaystones && known >= total) Wayfarer(player, rec);
        }

        // Every (unhidden, open) waystone reached: the realm hears of it once per player, RealmRenown records the deed
        // once (dedupe key), and once per season the player's house earns season points, at most a few per house.
        private void Wayfarer(Player player, Traveller rec)
        {
            if (!rec.Wayfarer)
            {
                rec.Wayfarer = true;
                Reply(player, "WayfarerYou");
                if (config.Discovery.WayfarerDeed.Length > 0 && RealmRenown != null)
                    Ask(RealmRenown, "AddDeed", player.Id.ToString(), player.Name, config.Discovery.WayfarerDeed, "reached every waystone", "wayfarer");
                if (config.Discovery.WayfarerHerald && HeraldAllowed()) Herald(Fmt("Wayfarer", null, Clean(player.Name, 40)));
            }
            int season = SeasonNumber();
            if (season <= 0 || rec.WayfarerSeason == season || config.Discovery.PathfinderPoints <= 0) return;
            rec.WayfarerSeason = season;
            string house = HouseOf(player.Id);
            if (house == null) return;
            string key = season + "|" + house.ToLowerInvariant();
            int n;
            data.PathfinderAwards.TryGetValue(key, out n);
            if (n >= config.Discovery.PathfinderAwardsPerHousePerSeason) return;
            if (!AskBool(RealmSeasons, "AwardHouse", house, config.Discovery.PathfinderPoints, "Pathfinder: " + Clean(player.Name, 40))) return;
            data.PathfinderAwards[key] = n + 1;
            Reply(player, "Pathfinder", HouseTint(house), config.Discovery.PathfinderPoints);
        }

        private bool HeraldAllowed()
        {
            DateTime now = Now();
            wayfarerHeralds.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
            if (wayfarerHeralds.Count >= config.Discovery.WayfarerHeraldsPerHour) return false;
            wayfarerHeralds.Add(now);
            return true;
        }

        #endregion

        #region Home

        [ChatCommand("home")]
        private void CmdHome(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            if (!config.General.Enabled) { ReplyError(player, "Closed"); return; }
            if (!config.Home.Enabled) { ReplyError(player, "PartClosed"); return; }
            Traveller rec = Rec(player);
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "":
                case "go":
                    GoHome(player, rec);
                    return;
                case "set":
                    SetHome(player, rec);
                    return;
                case "clear":
                case "forget":
                    rec.HasHome = false;
                    dirty = true;
                    SaveData();
                    Reply(player, "HomeCleared");
                    return;
                case "info":
                    HomeInfo(player, rec);
                    return;
                case "road":
                    StartHomeRoad(player, rec);
                    return;
                default:
                    Reply(player, "HomeHelp");
                    return;
            }
        }

        private void SetHome(Player player, Traveller rec)
        {
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
            DateTime now = Now();
            if (!AdminExempt(player) && rec.HomeSetReadyAt > now) { ReplyError(player, "HomeSetCooldown", ReadyText(player, rec.HomeSetReadyAt)); return; }
            if (config.Home.RequireOwnCrest)
            {
                int own = CrestOwnership(player, pos);
                if (own < 0) { ReplyError(player, "HomeUnknownLand"); return; }
                if (own == 0) { ReplyError(player, "HomeSetCrest"); return; }
            }
            rec.HasHome = true;
            rec.HomeX = pos.x; rec.HomeY = pos.y; rec.HomeZ = pos.z;
            rec.HomeSetReadyAt = now.AddMinutes(config.Home.SetCooldownMinutes);
            dirty = true;
            SaveData();
            Reply(player, "HomeSet", Secs(config.Home.ChannelSeconds), config.Home.Toll);
        }

        private void GoHome(Player player, Traveller rec)
        {
            if (!rec.HasHome) { Reply(player, "HomeHelp"); ReplyError(player, "HomeNone"); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
            Journey current;
            if (journeys.TryGetValue(player.Id, out current)) { ReplyError(player, "Already", current.TargetName); return; }
            bool exempt = AdminExempt(player);
            DateTime now = Now();
            if (!exempt && rec.HomeReadyAt > now) { ReplyError(player, "B.cooldown", ReadyText(player, rec.HomeReadyAt)); return; }
            Refusal r = PersonBlocker(player, pos);
            if (r == null) r = HomeBlocker(player, rec);
            if (r != null) { Refuse(player, r); return; }
            float d = Dist(pos.x, pos.y, pos.z, rec.HomeX, rec.HomeY, rec.HomeZ);
            string homeName = Msg("HomeName", player);
            if (d < config.Travel.MinDistance) { ReplyError(player, "B.near", CapFirst(homeName), DistText(player, d)); return; }
            if (config.Travel.Mode == "road") { Reply(player, "RoadMode"); StartHomeRoad(player, rec); return; }
            string freeWhy;
            bool freeTrip;
            long toll = Quote(player, rec, config.Home.Toll, false, exempt, out freeWhy, out freeTrip);
            if (!CanPay(player, toll)) return;
            var j = new Journey { Kind = JHome, Target = "", TargetName = homeName, SX = pos.x, SY = pos.y, SZ = pos.z, Toll = toll };
            BeginJourney(player, j, exempt ? 0f : config.Home.ChannelSeconds, freeWhy);
        }

        private Refusal HomeBlocker(Player player, Traveller rec)
        {
            if (!rec.HasHome) return new Refusal("HomeNone");
            UnityEngine.Vector3 home = V(rec.HomeX, rec.HomeY, rec.HomeZ);
            if (config.Home.RequireOwnCrest)
            {
                int own = CrestOwnership(player, home);
                if (own < 0) return new Refusal("HomeUnknownLand");
                if (own == 0) return new Refusal("HomeLost");
            }
            if (config.Home.BlockUnderSiege && UnderSiege(home)) return new Refusal("HomeSiege");
            if (NearThroneInRebellion(home)) return new Refusal("B.throne");
            return null;
        }

        private void HomeInfo(Player player, Traveller rec)
        {
            if (!rec.HasHome) { ReplyError(player, "HomeNone"); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
            UnityEngine.Vector3 home = V(rec.HomeX, rec.HomeY, rec.HomeZ);
            bool valid = !config.Home.RequireOwnCrest || CrestOwnership(player, home) == 1;
            Reply(player, "HomeInfo", DistText(player, Dist(pos.x, pos.y, pos.z, home.x, home.y, home.z)),
                DirText(player, pos.x, pos.z, home.x, home.z), Msg(valid ? "HomeValid" : "HomeInvalid", player));
            Reply(player, "HomeReady", ReadyText(player, rec.HomeReadyAt));
        }

        // 1 = the player's own crest zone, 0 = not, -1 = the land cannot be read. Own = the crest's group is the
        // player's group ([ASM] CrestScheme.CurrentCrestGroup vs SocialAPI.GetGroupId), or the crest is the player's own
        // (CrestScheme.GetCrestPlayer). UNVERIFIED in game: what a player outside any guild gets from GetGroupId.
        private int CrestOwnership(Player player, UnityEngine.Vector3 pos)
        {
            try
            {
                CrestScheme crests = SocialAPI.Get<CrestScheme>();
                if (crests == null) return -1;
                ulong group = crests.CurrentCrestGroup(pos);
                if (group == 0UL) return 0;
                ulong mine = SocialAPI.GetGroupId(player.Id);
                if (mine != 0UL && mine == group) return 1;
                return crests.GetCrestPlayer(pos) == player.Id ? 1 : 0;
            }
            catch (Exception ex)
            {
                if (!Throttled("crest", 300)) PrintWarning("Crest check failed: " + ex.Message);
                return -1;
            }
        }

        private bool UnderSiege(UnityEngine.Vector3 pos)
        {
            try
            {
                CrestScheme crests = SocialAPI.Get<CrestScheme>();
                return crests != null && crests.IsUnderSiege(pos);
            }
            catch (Exception) { return false; }
        }

        #endregion

        #region Roads

        [ChatCommand("road")]
        private void CmdRoad(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            if (!config.General.Enabled) { ReplyError(player, "Closed"); return; }
            if (!config.Roads.Enabled) { ReplyError(player, "PartClosed"); return; }
            Traveller rec = Rec(player);
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "" || sub == "help")
            {
                Road r;
                UnityEngine.Vector3 pos;
                if (roads.TryGetValue(player.Id, out r) && TryPos(player, out pos))
                    Reply(player, "RoadLine", r.Name, DistText(player, FlatDist(pos.x, pos.z, r.X, r.Z)), DirText(player, pos.x, pos.z, r.X, r.Z));
                Reply(player, "RoadHelp");
                return;
            }
            if (sub == "stop" || sub == "cancel")
            {
                if (roads.Remove(player.Id)) Reply(player, "RoadStopped"); else ReplyError(player, "RoadNone");
                return;
            }
            if (sub == "home") { StartHomeRoad(player, rec); return; }
            string amb;
            string text = JoinFrom(args, 0);
            Waystone w = FindWaystone(text, rec, false, out amb);
            if (w == null || !w.Enabled) { ReplyMissing(player, text, amb); return; }
            StartRoad(player, w.Name, w.Id, w.X, w.Y, w.Z);
        }

        private void StartHomeRoad(Player player, Traveller rec)
        {
            if (!rec.HasHome) { ReplyError(player, "HomeNone"); return; }
            StartRoad(player, Msg("HomeName", player), "", rec.HomeX, rec.HomeY, rec.HomeZ);
        }

        private void StartRoad(Player player, string name, string target, float x, float y, float z)
        {
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
            float d = FlatDist(pos.x, pos.z, x, z);
            if (d <= config.Roads.ArrivalRadius) { Reply(player, "RoadHere", name); return; }
            DateTime now = Now();
            roads[player.Id] = new Road { Name = name, Target = target, X = x, Y = y, Z = z,
                Next = now.AddSeconds(config.Roads.UpdateSeconds), Until = now.AddMinutes(config.Roads.MaxMinutes) };
            Reply(player, "RoadStart", name, DistText(player, d), DirText(player, pos.x, pos.z, x, z), (int)config.Roads.UpdateSeconds);
        }

        #endregion

        #region Kits

        [ChatCommand("kit")]
        private void CmdKit(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "admin") { CmdKitAdmin(player, args); return; }
            if (!config.General.Enabled) { ReplyError(player, "Closed"); return; }
            if (!config.Kits.Enabled) { ReplyError(player, "PartClosed"); return; }
            Traveller rec = Rec(player);
            if (sub == "" || sub == "list") { ShowKits(player, rec); return; }
            if (sub == "help")
            {
                Reply(player, "KitHelp");
                if (IsAdmin(player)) Reply(player, "KitHelpAdmin");
                return;
            }
            if (sub == "collect") { Collect(player, rec); return; }
            KitDef kit = FindKit(sub);
            if (kit == null) { ReplyError(player, "KitUnknown", Clean(args[0], 30)); return; }
            ClaimKit(player, rec, kit);
        }

        private void ShowKits(Player player, Traveller rec)
        {
            if (config.Kits.List.Count == 0) { Reply(player, "KitNone"); return; }
            Reply(player, "KitHeader");
            foreach (KitDef kit in config.Kits.List)
            {
                string why = KitBlocker(player, rec, kit);
                Reply(player, "KitLine", kit.Name, kit.Id, why ?? Fmt("Kit.ready", player, kit.Id));
                Reply(player, "KitContents", ItemsText(kit));
            }
            Reply(player, "KitHelp");
            if (IsAdmin(player)) Reply(player, "KitHelpAdmin");
        }

        // Null when the player may take the kit now, else why not (a lang line).
        private string KitBlocker(Player player, Traveller rec, KitDef kit)
        {
            if (!kit.Enabled) return Msg("Kit.off", player);
            DateTime now = Now();
            string claim;
            rec.KitClaims.TryGetValue(kit.Id, out claim);
            if (kit.Kind == KitStarter)
            {
                if (claim != null) return Msg("Kit.taken", player);
                NoteNewcomer(player, rec);
                if (!rec.WasNewcomer) return Msg("Kit.newcomers", player);
                if ((now - rec.FirstSeen).TotalDays > config.Kits.StarterClaimDays) return Msg("Kit.late", player);
                if (StarterClaimsToday(now) >= config.Kits.StarterMaxPerDay) return Msg("Kit.busy", player);
            }
            else if (kit.Kind == KitHouse)
            {
                string day = DayKey(now);
                if (claim == day) return Fmt("Kit.today", player, ReadyText(player, NextDayStart(now)));
                string house = HouseOf(player.Id);
                if (house == null) return Msg("Kit.nohouse", player);
                TrackHouse(player, rec);
                double hours = (now - rec.HouseSince).TotalHours;
                if (hours < config.Kits.HouseKitMinMemberHours)
                    return Fmt("Kit.newmember", player, HoursText(player, config.Kits.HouseKitMinMemberHours),
                        HoursText(player, (int)Math.Ceiling(config.Kits.HouseKitMinMemberHours - hours)));
                List<string> members = AskList(RealmHouses, "GetMembers", house);
                if (members == null || members.Count < config.Kits.HouseKitMinMembers) return Fmt("Kit.smallhouse", player, config.Kits.HouseKitMinMembers);
            }
            else if (kit.Kind == KitSeason)
            {
                int season = SeasonNumber();
                if (season <= 0) return Msg("Kit.noseason", player);
                if (claim == "s" + season) return Msg("Kit.season", player);
                int played = rec.PlaySeason == season ? rec.PlaySeasonMinutes : 0;
                if (played < config.Kits.SeasonKitMinPlayMinutes) return Fmt("Kit.play", player, config.Kits.SeasonKitMinPlayMinutes, played);
            }
            if (ResolveKit(kit, null).Count == 0) return Msg("Kit.unavailable", player);
            return null;
        }

        private void ClaimKit(Player player, Traveller rec, KitDef kit)
        {
            if (player.Entity == null) { ReplyError(player, "KitNoBody"); return; }
            if (CombatSecondsLeft(player.Id) > 0 || (config.Travel.UseWardenCombat && AskBool(RealmWarden, "IsInCombat", player.Id)))
            { ReplyError(player, "KitCombat"); return; }
            string why = KitBlocker(player, rec, kit);
            if (why != null) { ReplyError(player, "KitNotNow", kit.Name, why); return; }
            if (rec.Owed.Count >= config.Kits.MaxOwedLines) { ReplyError(player, "KitOwedFull"); return; }
            List<KeyValuePair<InvItemBlueprint, int>> items = ResolveKit(kit, null);
            DateTime now = Now();

            // The claim and everything owed are saved first; the items then leave the ledger before each give.
            rec.KitClaims[kit.Id] = kit.Kind == KitStarter ? "taken" : kit.Kind == KitHouse ? DayKey(now) : "s" + SeasonNumber();
            if (kit.Kind == KitStarter) data.StarterClaims.Add(now);
            foreach (KeyValuePair<InvItemBlueprint, int> kv in items)
                rec.Owed.Add(new Owed { Item = kv.Key.Name, Amount = kv.Value, Kit = kit.Id });
            data.KitsGiven++;
            dirty = true;
            SaveData();
            Puts(Clean(player.Name, 40) + " (" + player.Id + ") takes the kit '" + kit.Id + "'.");

            string given = PayOwed(player, rec);
            Reply(player, "KitTaken", kit.Name, given.Length > 0 ? given : ItemsText(kit));
            if (rec.Owed.Count > 0) Reply(player, "KitPending");
        }

        private void Collect(Player player, Traveller rec)
        {
            if (rec.Owed.Count == 0) { ReplyError(player, "CollectNone"); return; }
            if (player.Entity == null) { ReplyError(player, "KitNoBody"); return; }
            string given = PayOwed(player, rec);
            if (given.Length == 0) { ReplyError(player, "CollectFull"); return; }
            Reply(player, "Collected", given);
            if (rec.Owed.Count > 0) Reply(player, "KitPending");
        }

        private void PayOwedOnJoin(ulong id)
        {
            if (loadFailed || data == null) return;
            Player p = OnlineById(id);
            if (p == null) return;
            Traveller rec = Rec(p);
            if (rec.Owed.Count == 0) return;
            string given = PayOwed(p, rec);
            if (given.Length > 0) Reply(p, "Collected", given);
            if (rec.Owed.Count > 0) Reply(p, "OwedOnJoin");
        }

        // Pays what the ledger owes, measured. Each line leaves the ledger and the file is saved BEFORE the give; what
        // did not fit goes back and is saved again. A crash in between loses items, it never pays them twice.
        private string PayOwed(Player player, Traveller rec)
        {
            if (rec.Owed.Count == 0 || player == null || player.Entity == null) return "";
            var paying = new List<Owed>(rec.Owed);
            rec.Owed.Clear();
            dirty = true;
            SaveData();
            if (RealmSentinel != null) Ask(RealmSentinel, "SentinelItemSource", player.Id, 30f);
            var parts = new List<string>();
            foreach (Owed o in paying)
            {
                InvItemBlueprint bp = Blueprint(o.Item);
                int given = bp != null ? GiveItems(player, bp, o.Amount) : 0;
                if (given > 0) parts.Add(given + " " + bp.Name);
                if (given < o.Amount) rec.Owed.Add(new Owed { Item = o.Item, Amount = o.Amount - given, Kit = o.Kit });
            }
            dirty = true;
            SaveData();
            return string.Join(", ", parts.ToArray());
        }

        // Server-side grant, the same calls as the game's /give (ThronesCommandHandler.Give) [IL]: the Inventory
        // container (PlayerExtensions.GetInventory), stacks capped at ContainerManagement.StackLimit,
        // ItemCollection.AutoMergeAdd. What was added is measured with ItemCollection.AutoCount, never assumed.
        // UNVERIFIED in game: that the client's inventory shows it at once (the game's own /give relies on it).
        private int GiveItems(Player player, InvItemBlueprint bp, int amount)
        {
            try
            {
                if (player == null || player.Entity == null || bp == null || amount <= 0) return 0;
                Container inv = player.GetInventory();
                ItemCollection items = inv != null ? inv.Contents : null;
                if (items == null) return 0;
                ContainerManagement cm = bp.TryGet<ContainerManagement>();
                int limit = cm != null && cm.StackLimit > 0 ? cm.StackLimit : amount;
                int given = 0;
                for (int guard = 0; given < amount && guard < 1000; guard++)
                {
                    int chunk = Math.Min(limit, amount - given);
                    int before = ItemCollection.AutoCount(items, bp);
                    ItemCollection.AutoMergeAdd(items, new InvGameItemStack(bp, chunk, null));
                    int added = ItemCollection.AutoCount(items, bp) - before;
                    if (added <= 0) break;
                    given += Math.Min(added, chunk);
                    if (added < chunk) break;
                }
                return given;
            }
            catch (Exception ex)
            {
                PrintWarning("Giving " + amount + " " + bp.Name + " to " + Clean(player.Name, 40) + " failed: " + ex.Message);
                return 0;
            }
        }

        private List<KeyValuePair<InvItemBlueprint, int>> ResolveKit(KitDef kit, List<string> unknown)
        {
            var list = new List<KeyValuePair<InvItemBlueprint, int>>();
            foreach (KitItem it in kit.Items)
            {
                InvItemBlueprint bp = Blueprint(it.Item);
                if (bp != null) list.Add(new KeyValuePair<InvItemBlueprint, int>(bp, it.Amount));
                else if (unknown != null) unknown.Add(it.Item);
            }
            return list;
        }

        // ResourceType name first (the lookup ResourceTax.TaxResource itself uses [IL]), then an exact item name,
        // case-insensitive: InvBlueprints.GetBlueprintForResource / GetBlueprintForName(name, true, true) [ASM].
        private InvItemBlueprint Blueprint(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            InvItemBlueprint bp;
            if (blueprintCache.TryGetValue(name, out bp)) return bp;
            try
            {
                if (InvBlueprints.Instance == null) return null;          // the world is not up yet: do not cache
                foreach (CodeHatch.ResourceType rt in Enum.GetValues(typeof(CodeHatch.ResourceType)))
                {
                    if (rt == CodeHatch.ResourceType.Count || !string.Equals(rt.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                    bp = InvBlueprints.Instance.GetBlueprintForResource(rt);
                    break;
                }
                if (bp == null) bp = InvBlueprints.Instance.GetBlueprintForName(name, true, true);
            }
            catch (Exception ex)
            {
                if (!Throttled("bp", 300)) PrintWarning("Item lookup failed: " + ex.Message);
                return null;
            }
            blueprintCache[name] = bp;
            return bp;
        }

        // Start-up (and /kit admin check): every kit item against the game's own item list.
        private void CheckKitItems(Player admin)
        {
            blueprintCache.Clear();
            if (admin != null) Reply(admin, "CheckHeader");
            bool ready;
            try { ready = InvBlueprints.Instance != null; }
            catch (Exception) { ready = false; }
            if (!ready)
            {
                if (admin != null) ReplyError(admin, "ItemsNotReady");
                return;
            }
            foreach (KitDef kit in config.Kits.List)
            {
                var unknown = new List<string>();
                List<KeyValuePair<InvItemBlueprint, int>> ok = ResolveKit(kit, unknown);
                var names = new List<string>();
                foreach (KeyValuePair<InvItemBlueprint, int> kv in ok) names.Add(kv.Value + " " + kv.Key.Name);
                if (admin != null) Reply(admin, "CheckLine", kit.Name, kit.Id, names.Count > 0 ? string.Join(", ", names.ToArray()) : Msg("None", admin));
                foreach (string u in unknown)
                {
                    if (admin != null) Reply(admin, "CheckBad", u);
                    else PrintWarning("Kit '" + kit.Id + "': the item '" + u + "' is not known to this server and is skipped. Find the real name with /kit admin items <word>.");
                }
            }
        }

        private KitDef FindKit(string id)
        {
            string n = NormalizeId(id);
            if (n == null) return null;
            foreach (KitDef k in config.Kits.List) if (k.Id == n) return k;
            foreach (KitDef k in config.Kits.List) if (string.Equals(k.Name, id, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        private KitDef StarterKit()
        {
            foreach (KitDef k in config.Kits.List) if (k.Kind == KitStarter && k.Enabled) return k;
            return null;
        }

        private static string ItemsText(KitDef kit)
        {
            var parts = new List<string>();
            foreach (KitItem it in kit.Items) parts.Add(it.Amount + " " + it.Item);
            return string.Join(", ", parts.ToArray());
        }

        // A newcomer is a player RealmWarden protects as new (or, without RealmWarden, one first seen within
        // StarterFallbackHours). Seen once is enough: the pack can still be taken within StarterClaimDays.
        private void NoteNewcomer(Player player, Traveller rec)
        {
            if (rec.WasNewcomer) return;
            bool isNew;
            if (RealmWarden != null) isNew = AskBool(RealmWarden, "IsNewPlayerProtected", player.Id);
            else isNew = config.Kits.StarterFallbackHours > 0 && (Now() - rec.FirstSeen).TotalHours <= config.Kits.StarterFallbackHours;
            if (!isNew) return;
            rec.WasNewcomer = true;
            dirty = true;
        }

        private void SendHint(ulong id)
        {
            if (loadFailed || data == null || !config.General.Enabled || !config.Kits.Enabled) return;
            Player p = OnlineById(id);
            if (p == null) return;
            Traveller rec = Rec(p);
            KitDef starter = StarterKit();
            if (rec.HintSent || starter == null) return;
            if (KitBlocker(p, rec, starter) != null) return;
            rec.HintSent = true;
            dirty = true;
            Reply(p, "Hint", starter.Id);
        }

        private int StarterClaimsToday(DateTime now)
        {
            int n = 0;
            foreach (DateTime t in data.StarterClaims) if ((now - t).TotalHours < 24) n++;
            return n;
        }

        private void TrackHouse(Player player, Traveller rec)
        {
            string house = HouseOf(player.Id);
            if (house == null)
            {
                if (rec.HouseSeen != null) { rec.HouseSeen = null; dirty = true; }
                return;
            }
            if (SameText(rec.HouseSeen, house)) return;
            rec.HouseSeen = house;
            rec.HouseSince = Now();
            dirty = true;
        }

        // The realm's day, in CrownAndConsequences' realm time (UtcOffsetHours) when it is loaded.
        private string DayKey(DateTime utc)
        {
            return utc.AddHours(UtcOffset()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private DateTime NextDayStart(DateTime utc)
        {
            double off = UtcOffset();
            DateTime local = utc.AddHours(off);
            return local.Date.AddDays(1).AddHours(-off);
        }

        private double UtcOffset()
        {
            if (CrownAndConsequences == null) return 0;
            object r = Ask(CrownAndConsequences, "GetUtcOffsetHours");
            return r is double ? Math.Max(-14, Math.Min(14, (double)r)) : 0;
        }

        #endregion

        #region Admin

        // Admin subcommands whose third word is a waystone id.
        private static readonly string[] AdminIdSubs = { "name", "note", "house", "kind", "toll", "radius", "hidden", "enabled", "mark", "remove", "tp" };

        private void CmdTravelAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            Waystone w = null;
            if (args.Length > 2 && Array.IndexOf(AdminIdSubs, sub) >= 0)
            {
                w = Stone(args[2]);
                if (w == null) { ReplyError(player, "AdminNoSuch", Clean(args[2], 30)); return; }
            }
            switch (sub)
            {
                case "set": AdminSet(player, args); return;
                case "name":
                    if (w == null || args.Length < 4) { Usage(player, "/travel admin name <id> <name>"); return; }
                    w.Name = Clean(JoinFrom(args, 3), 40);
                    if (w.Name.Length == 0) w.Name = Pretty(w.Id);
                    Done(player, w.Id + " = " + w.Name);
                    return;
                case "note":
                    if (w == null) { Usage(player, "/travel admin note <id> <text|none>"); return; }
                    w.Note = args.Length < 4 || args[3].ToLowerInvariant() == "none" ? "" : Clean(JoinFrom(args, 3), 150);
                    Done(player, w.Id + " note");
                    return;
                case "house":
                    if (w == null || args.Length < 4) { Usage(player, "/travel admin house <id> <house|none>"); return; }
                    w.House = args[3].ToLowerInvariant() == "none" ? "" : Clean(JoinFrom(args, 3), 40);
                    Done(player, w.Id + " house " + (w.House.Length > 0 ? w.House : Msg("None", player)));
                    if (w.House.Length > 0) SuggestMonument(player, w);
                    return;
                case "kind":
                    {
                        string kind = args.Length > 3 ? args[3].ToLowerInvariant() : "";
                        if (w == null || Array.IndexOf(Kinds, kind) < 0) { ReplyError(player, "AdminBadKind"); return; }
                        w.Kind = kind;
                        Done(player, w.Id + " " + kind);
                        return;
                    }
                case "toll":
                    {
                        long t;
                        string v = args.Length > 3 ? args[3].ToLowerInvariant() : "";
                        if (w == null) { Usage(player, "/travel admin toll <id> <marks|auto>"); return; }
                        if (v == "auto") w.Toll = -1;
                        else if (long.TryParse(v, out t) && t >= 0 && t <= MaxExplicitToll) w.Toll = t;
                        else { Usage(player, "/travel admin toll <id> <0-" + MaxExplicitToll + "|auto>"); return; }
                        Done(player, w.Id + " toll " + (w.Toll < 0 ? "auto" : w.Toll.ToString(CultureInfo.InvariantCulture)));
                        return;
                    }
                case "radius":
                    {
                        float rad;
                        if (w == null || args.Length < 4 || !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out rad) || rad < 2f || rad > 100f)
                        { Usage(player, "/travel admin radius <id> <2-100>"); return; }
                        w.Radius = rad;
                        Done(player, w.Id + " radius " + rad.ToString("0.#", CultureInfo.InvariantCulture));
                        return;
                    }
                case "hidden":
                case "enabled":
                    {
                        string v = args.Length > 3 ? args[3].ToLowerInvariant() : "";
                        if (w == null || (v != "on" && v != "off")) { Usage(player, "/travel admin " + sub + " <id> on|off"); return; }
                        if (sub == "hidden") w.Hidden = v == "on"; else w.Enabled = v == "on";
                        Done(player, w.Id + " " + sub + " " + v);
                        return;
                    }
                case "mark":
                    {
                        if (w == null || args.Length < 4) { Usage(player, "/travel admin mark <id> <sculpture|none>"); return; }
                        string m = args[3].ToLowerInvariant() == "none" ? "" : NormalizeSculpture(args[3]);
                        if (m == null) { Usage(player, "/travel admin mark <id> <sculpture|none>"); return; }
                        w.Monument = m;
                        Done(player, w.Id + " mark " + (m.Length > 0 ? m : Msg("None", player)));
                        return;
                    }
                case "remove":
                    {
                        if (w == null) { Usage(player, "/travel admin remove <id> confirm"); return; }
                        string key = player.Id + "|" + w.Id;
                        DateTime asked;
                        bool confirmed = args.Length > 3 && args[3].ToLowerInvariant() == "confirm"
                            && removeConfirm.TryGetValue(key, out asked) && (Now() - asked).TotalSeconds <= 120;
                        if (!confirmed) { removeConfirm[key] = Now(); Reply(player, "AdminRemoveAsk", w.Name, w.Id); return; }
                        removeConfirm.Remove(key);
                        data.Waystones.Remove(w.Id);
                        foreach (Traveller t in data.Players.Values) t.Unlocked.Remove(w.Id);
                        foreach (ulong jid in new List<ulong>(journeys.Keys)) if (journeys[jid].Target == w.Id) journeys.Remove(jid);
                        foreach (ulong rid in new List<ulong>(roads.Keys)) if (roads[rid].Target == w.Id) roads.Remove(rid);
                        dirty = true;
                        SaveData();
                        Reply(player, "AdminRemoved", w.Name);
                        return;
                    }
                case "list": AdminList(player); return;
                case "unlock":
                case "lock": AdminUnlock(player, args, sub == "unlock"); return;
                case "throne": AdminThrone(player, args); return;
                case "tp":
                    {
                        if (w == null) { Usage(player, "/travel admin tp <id>"); return; }
                        if (RealmSentinel != null) Ask(RealmSentinel, "SentinelGrace", player.Id, 10f);
                        if (Teleport(player, w.X, w.Y + config.Travel.ArrivalLift, w.Z)) Reply(player, "AdminTp", w.Name);
                        else ReplyError(player, "RoadFailed");
                        return;
                    }
                case "status": AdminStatus(player); return;
                default:
                    Reply(player, "TravelHelpAdmin");
                    return;
            }
        }

        private void Done(Player player, string what)
        {
            dirty = true;
            SaveData();
            Reply(player, "AdminDone", what);
        }

        private void Usage(Player player, string usage)
        {
            ReplyError(player, "AdminUsage", usage);
        }

        private void AdminSet(Player player, string[] args)
        {
            if (args.Length < 4) { ReplyError(player, "AdminSetUsage"); return; }
            string id = NormalizeId(args[2]);
            if (id == null) { ReplyError(player, "AdminBadId"); return; }
            string kind = args[3].ToLowerInvariant();
            if (Array.IndexOf(Kinds, kind) < 0) { ReplyError(player, "AdminBadKind"); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
            string name = args.Length > 4 ? Clean(JoinFrom(args, 4), 40) : "";
            Waystone w;
            if (data.Waystones.TryGetValue(id, out w))
            {
                w.X = pos.x; w.Y = pos.y; w.Z = pos.z;
                w.Kind = kind;
                if (name.Length > 0) w.Name = name;
                Done(player, id);
                Reply(player, "AdminSetMoved", w.Name);
                return;
            }
            if (data.Waystones.Count >= MaxWaystones) { ReplyError(player, "AdminTooMany", MaxWaystones); return; }
            w = new Waystone
            {
                Id = id, Name = name.Length > 0 ? name : Pretty(id), Kind = kind, House = "", Monument = "", Note = "",
                X = pos.x, Y = pos.y, Z = pos.z, Radius = config.Discovery.DefaultRadius, Toll = -1, Enabled = true,
                CreatedBy = Clean(player.Name, 40), CreatedAt = Now()
            };
            data.Waystones[id] = w;
            dirty = true;
            SaveData();
            Reply(player, "AdminSetNew", w.Name, id, w.Radius.ToString("0.#", CultureInfo.InvariantCulture));
            if (kind == KSeat) Reply(player, "AdminSetHouse", id);
            SuggestMonument(player, w);
        }

        // The sculptures in art/sculptures (RealmSculptor): the herald's pillar for the capital, a house's own
        // monument for the seat of one of the six great houses.
        private void SuggestMonument(Player player, Waystone w)
        {
            string s = null;
            if (w.Kind == KCapital) s = "heralds-pillar";
            else if (w.Kind == KSeat && !string.IsNullOrEmpty(w.House) && Array.IndexOf(GreatHouses, w.House.Trim().ToLowerInvariant()) >= 0)
                s = "house-" + w.House.Trim().ToLowerInvariant();
            if (s != null) Reply(player, "AdminSetMark", s, w.Id);
        }

        private void AdminList(Player player)
        {
            Reply(player, "AdminListHeader", data.Waystones.Count);
            var list = new List<Waystone>(data.Waystones.Values);
            list.Sort(delegate(Waystone a, Waystone b) { return string.CompareOrdinal(a.Id, b.Id); });
            foreach (Waystone w in list)
            {
                string flags = (w.Hidden ? " hidden" : "") + (w.Enabled ? "" : " closed");
                Reply(player, "AdminListLine", w.Id, w.Name, w.Kind, string.IsNullOrEmpty(w.House) ? "" : " (" + w.House + ")",
                    PosText(w.X, w.Y, w.Z), w.Radius.ToString("0", CultureInfo.InvariantCulture),
                    w.Toll < 0 ? "toll auto" : "toll " + w.Toll, flags, w.Visitors);
            }
        }

        private void AdminUnlock(Player player, string[] args, bool unlock)
        {
            if (args.Length < 4) { Usage(player, "/travel admin " + (unlock ? "unlock" : "lock") + " <player> <id|all>"); return; }
            string id;
            string name;
            Traveller t = FindTraveller(args[2], out id, out name);
            if (t == null) { ReplyError(player, "PlayerNotFound"); return; }
            string what = args[3].ToLowerInvariant();
            if (what == "all")
            {
                if (unlock) { foreach (string k in data.Waystones.Keys) if (!t.Unlocked.Contains(k)) t.Unlocked.Add(k); }
                else t.Unlocked.Clear();
            }
            else
            {
                Waystone w = Stone(what);
                if (w == null) { ReplyError(player, "AdminNoSuch", Clean(args[3], 30)); return; }
                if (unlock) { if (!t.Unlocked.Contains(w.Id)) t.Unlocked.Add(w.Id); }
                else t.Unlocked.Remove(w.Id);
            }
            dirty = true;
            SaveData();
            Reply(player, "AdminUnlocked", name, t.Unlocked.Count);
        }

        private void AdminThrone(Player player, string[] args)
        {
            if (args.Length > 2 && args[2].ToLowerInvariant() == "clear")
            {
                data.HasThrone = false;
                dirty = true;
                SaveData();
                Reply(player, "AdminThroneCleared");
            }
            else
            {
                UnityEngine.Vector3 pos;
                if (!TryPos(player, out pos)) { ReplyError(player, "NoBody"); return; }
                data.HasThrone = true;
                data.ThroneX = pos.x; data.ThroneY = pos.y; data.ThroneZ = pos.z;
                dirty = true;
                SaveData();
                Reply(player, "AdminThroneSet", config.Travel.ThroneRadius.ToString("0", CultureInfo.InvariantCulture));
            }
            Reply(player, "AdminThroneUnknown", ThroneText(player));
        }

        private string ThroneText(Player player)
        {
            UnityEngine.Vector3 t;
            string source;
            return ThronePoint(out t, out source) ? PosText(t.x, t.y, t.z) + " (" + source + ")" : Msg("None", player);
        }

        private void AdminStatus(Player player)
        {
            Reply(player, "StatusHeader", data.Waystones.Count, data.Players.Count, journeys.Count, roads.Count);
            Reply(player, "StatusTrips", data.Trips, data.TollsCollected, data.UnpaidTolls, data.ArrivalsUnconfirmed, data.KitsGiven);
            Reply(player, "StatusThrone", ThroneText(player), config.Travel.Mode, Msg(RealmTreasury != null ? "Loaded" : "Absent", player),
                Msg(RealmWarden != null ? "Loaded" : "Absent", player), Msg(RealmSeasons != null ? "Loaded" : "Absent", player));
            var kits = new List<string>();
            foreach (KitDef k in config.Kits.List) kits.Add(k.Id + " (" + k.Kind + (k.Enabled ? "" : ", off") + ")");
            Reply(player, "StatusKits", kits.Count > 0 ? string.Join(", ", kits.ToArray()) : Msg("None", player));
        }

        private void CmdKitAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "items":
                    {
                        string word = args.Length > 2 ? Clean(JoinFrom(args, 2), 30) : "";
                        if (word.Length < 2) { Usage(player, "/kit admin items <word>"); return; }
                        InvItemBlueprint[] found;
                        try { found = InvBlueprints.Instance != null ? InvBlueprints.GetBlueprintsContaining(word) : null; }
                        catch (Exception) { found = null; }
                        if (found == null) { ReplyError(player, "ItemsNotReady"); return; }
                        if (found.Length == 0) { Reply(player, "ItemsNone", word); return; }
                        Reply(player, "ItemsHeader", word, found.Length);
                        for (int i = 0; i < found.Length && i < 30; i++) if (found[i] != null) Reply(player, "ItemsLine", found[i].Name);
                        return;
                    }
                case "check":
                    CheckKitItems(player);
                    return;
                case "reset":
                    {
                        if (args.Length < 4) { Usage(player, "/kit admin reset <player> <kit|all>"); return; }
                        string id, name;
                        Traveller t = FindTraveller(args[2], out id, out name);
                        if (t == null) { ReplyError(player, "PlayerNotFound"); return; }
                        string what = args[3].ToLowerInvariant();
                        if (what == "all") t.KitClaims.Clear();
                        else
                        {
                            KitDef k = FindKit(what);
                            if (k == null) { ReplyError(player, "KitUnknown", Clean(args[3], 30)); return; }
                            t.KitClaims.Remove(k.Id);
                            what = k.Id;
                        }
                        dirty = true;
                        SaveData();
                        Reply(player, "KitReset", name, what);
                        return;
                    }
                default:
                    Reply(player, "KitHelpAdmin");
                    return;
            }
        }

        // An online player by name, or a player on record by exact name or Steam id.
        private Traveller FindTraveller(string text, out string id, out string name)
        {
            id = null;
            name = null;
            Player online = FindOnline(text);
            if (online != null) { id = online.Id.ToString(); name = online.Name; return Rec(online); }
            Traveller t;
            if (data.Players.TryGetValue(text, out t)) { id = text; name = t.Name; return t; }
            Traveller match = null;
            foreach (KeyValuePair<string, Traveller> kv in data.Players)
            {
                if (!string.Equals(kv.Value.Name, text, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) return null;                 // two on record with that name
                match = kv.Value; id = kv.Key; name = kv.Value.Name;
            }
            return match;
        }

        private static Player FindOnline(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            List<Player> matches = Server.MatchPlayerByName(name);
            Player only = null;
            if (matches != null)
                foreach (Player p in matches)
                {
                    if (p == null || p.IsServer) continue;
                    if (only != null) return null;
                    only = p;
                }
            return only;
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): the game's own window, opened with
        //   ShowPopup(this Player, string title, string message, string buttonText, Dialogue.OnSubmit handler,
        //             bool interupt, bool broadcast)   [ASM CodeHatch.Common.PlayerExtensions]
        // broadcast = true or a dedicated server never sends it. The chat lines are always sent as well.
        // UNVERIFIED in game: that the window shows and that "\n" breaks lines in it.
        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.General.UsePopups) return false;
            if (RealmHerald == null) return true;
            object wanted = RealmHerald.Call("PopupsWanted", player.Id.ToString());
            return !(wanted is bool) || (bool)wanted;
        }

        private bool ShowInfoPopup(Player player, string title, string message, string button)
        {
            try
            {
                player.ShowPopup(PopupText(title), PopupText(message), PopupText(button), null, false, true);
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("ShowPopup failed (" + ex.Message + "); the chat version stands.");
                return false;
            }
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

        #region API (plugin.Call) - non-public on purpose (see header)

        // How many waystones a player has found (RealmQuests and others may reward exploring).
        private int GetDiscoveredCount(string playerId)
        {
            Traveller t;
            return data != null && playerId != null && data.Players.TryGetValue(playerId, out t) ? t.Unlocked.Count : 0;
        }

        private bool HasDiscovered(string playerId, string waystoneId)
        {
            Traveller t;
            string id = NormalizeId(waystoneId);
            return data != null && playerId != null && id != null && data.Players.TryGetValue(playerId, out t) && t.Unlocked.Contains(id);
        }

        private bool IsTravelling(string playerId)
        {
            ulong u;
            return playerId != null && ulong.TryParse(playerId, out u) && journeys.ContainsKey(u);
        }

        // Another plugin that moves or binds a player (an arena, a court) can call off a journey in progress.
        private bool CancelJourney(string playerId)
        {
            ulong u;
            return playerId != null && ulong.TryParse(playerId, out u) && journeys.Remove(u);
        }

        #endregion

        #region Helpers

        private DateTime Now()
        {
            return clock();
        }

        private bool IsAdmin(Player player)
        {
            return player != null && !player.IsServer && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private bool AdminExempt(Player player)
        {
            return config.General.AdminsExempt && IsAdmin(player);
        }

        private Traveller Rec(Player p)
        {
            string id = p.Id.ToString();
            Traveller r;
            if (!data.Players.TryGetValue(id, out r) || r == null)
            {
                r = new Traveller();
                r.FirstSeen = Now();
                r.LastSeen = r.FirstSeen;
                data.Players[id] = r;
                dirty = true;
            }
            string name = Clean(p.Name, 40);
            if (r.Name != name) { r.Name = name; dirty = true; }
            return r;
        }

        private List<Waystone> Visible(Traveller rec)
        {
            var list = new List<Waystone>();
            foreach (Waystone w in data.Waystones.Values)
                if (w.Enabled && (!w.Hidden || rec.Unlocked.Contains(w.Id))) list.Add(w);
            return list;
        }

        private Waystone Stone(string id)
        {
            string n = NormalizeId(id);
            Waystone w;
            return n != null && data.Waystones.TryGetValue(n, out w) ? w : null;
        }

        // A waystone a player can name: exact id, exact name, then a unique start of an id or name. Hidden waystones the
        // player has not found are never matched.
        private Waystone FindWaystone(string text, Traveller rec, bool knownOnly, out string ambiguous)
        {
            ambiguous = null;
            string t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            List<Waystone> pool = Visible(rec);
            if (knownOnly) pool.RemoveAll(delegate(Waystone w) { return !rec.Unlocked.Contains(w.Id); });
            string id = NormalizeId(t);
            foreach (Waystone w in pool) if (id != null && w.Id == id) return w;
            foreach (Waystone w in pool) if (string.Equals(w.Name, t, StringComparison.OrdinalIgnoreCase)) return w;
            var hits = new List<Waystone>();
            foreach (Waystone w in pool)
                if (w.Id.StartsWith(t, StringComparison.OrdinalIgnoreCase) || w.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) hits.Add(w);
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                var names = new List<string>();
                for (int i = 0; i < hits.Count && i < 6; i++) names.Add(hits[i].Name);
                ambiguous = string.Join(", ", names.ToArray());
            }
            return null;
        }

        private static void SortByDistance(List<Waystone> list, UnityEngine.Vector3 pos, bool hasPos)
        {
            if (!hasPos) { list.Sort(delegate(Waystone a, Waystone b) { return string.CompareOrdinal(a.Name, b.Name); }); return; }
            list.Sort(delegate(Waystone a, Waystone b)
            {
                return Dist(pos.x, pos.y, pos.z, a.X, a.Y, a.Z).CompareTo(Dist(pos.x, pos.y, pos.z, b.X, b.Y, b.Z));
            });
        }

        private string KindText(Player player, Waystone w)
        {
            if (w.Kind == KSeat && !string.IsNullOrEmpty(w.House)) return Fmt("SeatOf", player, HouseTint(w.House));
            return Msg("Kind." + w.Kind, player);
        }

        private string HouseOf(ulong playerId)
        {
            string h = AskString(RealmHouses, "GetHouse", playerId.ToString());
            return string.IsNullOrEmpty(h) ? null : h;
        }

        private bool SwornToCrown(Player player)
        {
            if (config.Travel.SwornTollPercent >= 100 || CrownAndConsequences == null) return false;
            string house = HouseOf(player.Id);
            return house != null && AskBool(CrownAndConsequences, "IsSwornToCrown", house);
        }

        private bool OpenRoads()
        {
            return config.Travel.OpenRoadsDecree.Length > 0 && config.Travel.OpenRoadsTollPercent < 100
                && AskBool(CrownAndConsequences, "IsDecreeActive", config.Travel.OpenRoadsDecree);
        }

        private int SeasonNumber()
        {
            if (RealmSeasons == null) return 0;
            object r = Ask(RealmSeasons, "GetSeasonNumber");
            return r is int ? (int)r : 0;
        }

        private long Purse(Player player)
        {
            object r = Ask(RealmTreasury, "GetPurse", player.Id.ToString());
            return r is long ? (long)r : 0;
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
            if (id == 0 || !Server.PlayerIsOnline(id)) return null;
            Player p = Server.GetPlayerById(id);
            return p != null && !p.IsServer ? p : null;
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

        private static UnityEngine.Vector3 V(float x, float y, float z)
        {
            var v = new UnityEngine.Vector3();
            v.x = x; v.y = y; v.z = z;
            return v;
        }

        private static float Dist(float ax, float ay, float az, float bx, float by, float bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float FlatDist(float ax, float az, float bx, float bz)
        {
            double dx = ax - bx, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private string DistText(Player player, float metres)
        {
            if (metres < 1000f) return Fmt("Metres", player, ((int)(Math.Round(metres / 10f) * 10)).ToString(CultureInfo.InvariantCulture));
            return Fmt("Km", player, (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture));
        }

        // Compass point from (fromX, fromZ) to (toX, toZ): +x is east; north is +z unless Roads.NorthIsPositiveZ is false.
        private string DirText(Player player, float fromX, float fromZ, float toX, float toZ)
        {
            double dx = toX - fromX, dz = toZ - fromZ;
            if (!config.Roads.NorthIsPositiveZ) dz = -dz;
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            int sector = (int)Math.Floor((deg + 22.5) / 45.0) % 8;
            return Msg("Dir." + sector, player);
        }

        private string Secs(float seconds)
        {
            return ((int)Math.Ceiling(seconds)).ToString(CultureInfo.InvariantCulture);
        }

        private string ReadyText(Player player, DateTime when)
        {
            double s = (when - Now()).TotalSeconds;
            if (s <= 0) return Msg("Now", player);
            return Fmt("In", player, SpanText(player, s));
        }

        private string SpanText(Player player, double seconds)
        {
            if (seconds < 60) return Fmt("Sec", player, (int)Math.Ceiling(seconds));
            if (seconds < 3600 * 2) return Fmt("Min", player, (int)Math.Ceiling(seconds / 60));
            return Fmt("Hours", player, (int)Math.Ceiling(seconds / 3600));
        }

        private string HoursText(Player player, int hours)
        {
            return Fmt("Hours", player, hours);
        }

        private static string PosText(float x, float y, float z)
        {
            return ((int)Math.Round(x)) + "," + ((int)Math.Round(y)) + "," + ((int)Math.Round(z));
        }

        private static bool SameText(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string CapFirst(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string JoinFrom(string[] args, int start)
        {
            if (args == null || start >= args.Length) return "";
            var parts = new string[args.Length - start];
            Array.Copy(args, start, parts, 0, parts.Length);
            return string.Join(" ", parts).Trim();
        }

        // Waystone and kit ids: 2 to 24 of a-z, 0-9 and '-', lower case.
        private static string NormalizeId(string s)
        {
            if (s == null) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.Length < 2 || t.Length > 24) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return null;
            return t;
        }

        // A RealmSculptor sculpture id (its file name in art/sculptures without .json): the same rules, up to 40.
        private static string NormalizeSculpture(string s)
        {
            if (s == null) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.EndsWith(".json")) t = t.Substring(0, t.Length - 5);
            if (t.Length < 2 || t.Length > 40) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return null;
            return t;
        }

        private static string Pretty(string id)
        {
            var sb = new StringBuilder();
            bool up = true;
            foreach (char c in id)
            {
                if (c == '-') { sb.Append(' '); up = true; continue; }
                sb.Append(up ? char.ToUpperInvariant(c) : c);
                up = false;
            }
            return sb.ToString();
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
