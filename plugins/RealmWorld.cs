// RealmWorld: the living world of Ostreval between the great events. Treasure hunts, the Blood Moon, the Merchant
// Caravan, Wandering Legends, the Harvest Fair and Midwinter, and the weekly Census, on a schedule that never shares
// an hour with RealmEvents (Crown Night, the Royal Tournament, the King's Hunt, the Truce of the Realm).
//
//   treasure   A hunt staff lay out in game (/world admin hunt): a chain of places, each with a riddle that leads to it.
//              The first riddle is heralded to everyone (and painted on any sign bound with /paint world clue 1); a
//              player who reaches a place hears the next riddle (chat and a window; a sign bound with /paint world clue
//              <n> shows riddle n). The first to stand at the last place, the dig site, wins: marks (RealmTreasury
//              RewardMarks), items, RealmRenown's "treasure_found" deed and season points for their house. If staff
//              bound a chest to the dig site, the items go into that chest and it opens for the finder alone for
//              ChestLockMinutes (OnPlayerInteract is refused for everyone else); otherwise, or if the chest cannot be
//              found or filled, the items go into the finder's packs through an owed ledger. Places must be reached in
//              order and no faster than MinSecondsBetweenSteps apart; /treasure hint names a direction after a while.
//   bloodmoon  A red night. Heralds warn at WarningMinutes; the mood itself is the built-in Mods preset
//              mods/presets/blood-moon (Set-Mood.ps1 -Event blood_moon, applied by the owner at a restart: the Mods
//              system is read at start-up only). Each fair player kill earns RealmRenown's "blood_moon_kill" deed; the
//              house with the most kills earns season points and the deadliest slayer marks. Beasts bite harder
//              (BeastDamageMultiplier on creature blows to players). Fair kill: both online, not housemates, liege,
//              vassal or treaty partner, no new-player protection (RealmWarden), no truce (RealmEvents), each victim
//              once per killer and at most MaxDeathsFedPerVictim times in all, at most KillDeedsPerPlayer per killer.
//   caravan    A timed escort contract between two places: RealmTravel waystones (oxide/data/RealmTravel.json, read
//              only) or RealmWorld places. One player bears the goods (/caravan carry at the start), others escort
//              (/caravan escort). The bearer must walk (no waystone journey: RealmTravel.CancelJourney; a jump of more
//              than MaxJumpMetres between ticks loses the caravan) and arrive before the deadline: the purse is shared
//              by the bearer and the escorts who kept close, the bearer's house earns season points. A player who
//              kills the bearer plunders it (a share of the purse) and a raider's bounty is set on their head for
//              RaiderBountyHours, paid to whoever kills them (never the plundered bearer, their house or allies).
//              A bearer who takes up a duel (RealmArena.IsInDuel) gives up the goods: at the muster the place is free
//              again, on the road the caravan is lost; an escort in a duel is not counted as near.
//              Housemates and allies cannot plunder; their blow only loses the caravan. RealmContracts' rules for
//              bounties are followed: a verified kill from the death hook, an online victim, no allies.
//   legend     A Wandering Legend: a named beast. If a creature of the legend's kind is found (Entity.TryGetAll of
//              MonsterEntity, the game's own MonsterReport uses it) one is bound and takes Toughness times less damage
//              from players (Damage.Amount scaled in OnEntityHealthChange, as RealmLegendary scales blows); heralds
//              tell where it was last seen. Creatures cannot be renamed or given more health from a server plugin in a
//              way this repo can prove reaches clients, so its name lives in chat only. If no creature of the kind
//              is found, the hunt falls back to "the first <kind> slain within the region" (Region = a place id).
//              The slayer wins marks, a trophy, RealmRenown's "legend_slain" deed and season points; players who dealt
//              HelperMinPercent of the damage share HelperSharePercent of the marks.
//   festivals  The Harvest Fair and Midwinter run for days on fixed dates. Houses compete: members offer goods
//              (/festival give, taken from the packs and MEASURED with ItemCollection.AutoCount/AutoSplit) and, at
//              Midwinter, hunt beasts; daily caps per player. At the close the leading houses earn season points, the
//              best giver of the winning house marks and the "festival_champion" deed. Decorations: RealmSculptor
//              monuments staff marked for the festival (/world admin deco) are raised at the opening and taken down
//              at the close (RealmSculptor.PlaceSculptureAt / RemoveSculpture). Moods: mods/presets/harvest-festival
//              and midwinter (Set-Mood.ps1 -Event harvest_festival | midwinter).
//   census     Weekly: souls seen, newcomers, the most at once, houses and the largest, the crown, the season's
//              leader, marks struck, Chronicle entries, and the week's world deeds; in chat and as a "census_taken"
//              Chronicle entry. Counts only, no names.
//
// Never clashes with RealmEvents: before a timed world event starts (and before its countdown heralds), RealmWorld asks
// RealmEvents.GetActiveEvents and GetNextEvent. If an event runs or the next one starts before this one would end plus
// ClashBufferMinutes, the world event waits until the running event is over (at most MaxPostponeMinutes) or is skipped,
// and staff are told. Only one timed world event runs at a time. The default schedules are chosen apart (a logic test
// reads both plugins' defaults and proves it). A festival closes only when nothing else is running.
//
// Game API (tags as in docs/oxide-rok-api.md; [DEC] = read in the decompiled shipped patched Assembly-CSharp.dll,
// type.member names only, no game code copied):
//   Hooks      OnEntityHealthChange(EntityDamageEvent) [OPJ L162], OnEntityDeath(EntityDeathEvent) [OPJ L188]
//              (victim evt.Entity, killer evt.KillingDamage.DamageSource.Owner [ASM; USE DeathMessages.cs:20]),
//              OnPlayerInteract(InteractEvent) [OPJ L1036] (Entity = the thing used, ControllerEntity = the user [DEC]),
//              OnPlayerConnected / OnPlayerDisconnected [SRC].
//   Creatures  Entity.TryGetAll<MonsterEntity>() [DEC; used by CodeHatch.Engine.Core.Reporting.MonsterReport],
//              Entity.TryGetFromViewID(ulong), Entity.NetViewID, Entity.Position; a creature is an entity with a
//              MonsterMotor or MonsterEntity (as RealmQuests reads it); its kind from Entity.ToString().
//   Chests     InteractableContainer (CodeHatch.ItemContainer, a Container) on a placed object [DEC], its Contents
//              (ItemCollection), filled with ItemCollection.AutoMergeAdd of InvGameItemStack and measured with AutoCount
//              (the /give path [IL ThronesCommandHandler.Give]).
//   Items      PlayerExtensions.GetInventory -> Container.Contents; AutoCount, AutoSplit, AutoMergeAdd [ASM][IL];
//              InvBlueprints.Instance.GetBlueprintForResource / GetBlueprintForName(name, true, true).
//   Clock      GameClock.Instance.CurrentTimeBlock == GameClock.TimeBlock.Night [DEC; BasicSpawner reads it the same
//              way] for the optional NightOnly Blood Moon.
//   Facing     Entity.Forward [DEC] (which way a staff member faced when marking a decoration).
//
// Cross-plugin calls (all optional; a missing plugin or method reads as "not available"):
//   RealmTreasury.RewardMarks(id, name, marks, source) -> long (what it did not pay stays owed here, paid later)
//   RealmRenown.AddDeed(id, name, kind, note, dedupeKey); RealmSeasons.AwardHouse(house, points, honour)
//   RealmChronicle.Log(type, title, detail, actors), GetLastEventId(); RealmHouses.GetHouse, GetLiege, HasTreaty,
//   GetHouseSummaries; RealmEvents.GetActiveEvents, GetNextEvent, IsTruceActive; RealmWarden.IsNewPlayerProtected;
//   RealmHerald.PopupsWanted; RealmSentinel.SentinelItemSource; RealmPainter.RefreshBoards("world");
//   RealmSculptor.PlaceSculptureAt, RemoveSculpture; RealmTravel.IsTravelling, CancelJourney; RealmQuests.
//   ReportQuestEvent(id, "event", subject, 1); CrownAndConsequences.GetKingName, GetKingHouse; RealmTreasury.
//   GetTreasurySummary; RealmSeasons.GetSeasonStandings; RealmArena.IsInDuel (no caravan bearer, escort or Blood Moon
//   kill counts in a duel).
//
// Data: oxide/data/RealmWorld.json. If it exists but cannot be read, or reads as empty (a truncated file), the plugin
// pauses and NEVER writes it. Rewards: an owed entry is saved before anything is given; a crash can lose a reward,
// never pay it twice.
// Language level: C# 3 syntax, .NET 3.5 API surface. Cross-plugin methods are non-public (Oxide calls NonPublic|Instance).
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmWorld.md for the in-game test of each part.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeHatch.AI;                                // MonsterMotor [ASM]
using CodeHatch.Common;                            // PlayerExtensions: SendMessage, SendError, GetInventory, ShowPopup [ASM]
using CodeHatch.Damaging;                          // Damage [ASM]
using CodeHatch.Engine.Core.Cache;                 // Entity, MonsterEntity [ASM]
using CodeHatch.Engine.Core.Interaction.Behaviours.Networking;   // InteractEvent [ASM]
using CodeHatch.Engine.Networking;                 // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;              // InvItemBlueprint, InvBlueprints [ASM]
using CodeHatch.Inventory.Blueprints.Components;   // ContainerManagement [ASM]
using CodeHatch.ItemContainer;                     // Container, ItemCollection, InteractableContainer [ASM]
using CodeHatch.Networking.Events.Entities;        // EntityDamageEvent, EntityDeathEvent [ASM]
using Oxide.Core;                                  // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                          // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmWorld", "Realm", "0.1.0")]
    [Description("The living world: treasure hunts, the Blood Moon, the Merchant Caravan, Wandering Legends, festivals and the weekly Census")]
    public class RealmWorld : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmEvents;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmSentinel;
        [PluginReference] private Plugin RealmPainter;
        [PluginReference] private Plugin RealmSculptor;
        [PluginReference] private Plugin RealmTravel;
        [PluginReference] private Plugin RealmQuests;
        [PluginReference] private Plugin RealmArena;
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmworld.admin";
        private const string DataName = "RealmWorld";
        private const string TravelFile = "RealmTravel";
        private const int DataFormat = 1;
        private const string ChronicleStart = "event_started";
        private const string ChronicleEnd = "event_ended";
        private const string CensusChronicleType = "census_taken";

        private const string KTreasure = "treasure";
        private const string KBlood = "bloodmoon";
        private const string KCaravan = "caravan";
        private const string KLegend = "legend";
        private const string KCensus = "census";
        private static readonly string[] Kinds = { KTreasure, KBlood, KCaravan, KLegend, KCensus };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private Timer tickTimer;
        private DateTime nextSave = DateTime.MinValue;
        private DateTime travelReadAt = DateTime.MinValue;
        private Dictionary<string, TravelStone> travelStones;

        // Session state (never saved).
        private Entity legendEntity;                                     // the bound Wandering Legend, if any
        private readonly Dictionary<string, DateTime> throttle = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, InvItemBlueprint> blueprintCache = new Dictionary<string, InvItemBlueprint>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> weekSeen = new HashSet<string>();
        private readonly Dictionary<string, DateTime> removeAsk = new Dictionary<string, DateTime>();

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        #region Config

        private class Slot
        {
            public string Id;                          // stable name of this slot
            public string Event;                       // treasure | bloodmoon | caravan | legend | census
            public bool Enabled = true;
            public List<string> Days = new List<string>();
            public string StartUtc = "19:00";
            public int DurationMinutes = 60;
            public int WeekInterval = 1;               // 2 = every other week (weeks counted from Monday 2024-01-01)
            public int WeekOffset;
            public string Target = "";                 // a hunt, route or legend id; "" = the next in turn
        }

        private class ItemReward
        {
            public string Item;                        // ResourceType name (e.g. "IronIngot") or exact item name
            public int Amount;
        }

        private class GeneralCfg
        {
            public bool Enabled = true;
            public float TickSeconds = 5f;
            public int SaveEverySeconds = 60;
            public bool UsePopups = true;
            public bool AdminsCanWin = false;          // staff run the world; by default they win nothing in it
            public bool AvoidRealmEvents = true;
            public int ClashBufferMinutes = 30;
            public int MaxPostponeMinutes = 120;
            public List<int> CountdownMinutes = new List<int>();   // set in DefaultConfig (a list initialiser would be appended to)
            public int HistoryLines = 20;
            public bool NorthIsPositiveZ = true;       // for the compass words in hints and sightings (UNVERIFIED on our map)
        }

        private class TreasureCfg
        {
            public bool Enabled = true;
            public float StepRadius = 8f;
            public int MinSecondsBetweenSteps = 20;
            public int HintAfterMinutes = 15;
            public long RewardMarks = 150;
            public int SeasonPoints = 15;
            public bool UseChest = true;
            public int ChestLockMinutes = 10;
            public List<ItemReward> Items = new List<ItemReward>();
        }

        private class BloodCfg
        {
            public bool Enabled = true;
            public List<int> WarningMinutes = new List<int>();
            public int KillDeedsPerPlayer = 5;
            public int MaxDeathsFedPerVictim = 3;
            public int HousePoints = 20;
            public int HouseMinKills = 3;
            public long TopSlayerMarks = 60;
            public float BeastDamageMultiplier = 1.25f;
            public bool NightOnly = false;             // count kills only while the game's clock says night
        }

        private class Route
        {
            public string Id;
            public string Name = "";
            public string From;                        // "waystone:<id>" (RealmTravel) or a RealmWorld place id
            public string To;
            public bool Enabled = true;
        }

        private class CaravanCfg
        {
            public bool Enabled = true;
            public int MusterMinutes = 10;
            public float StartRadius = 20f;
            public float ArriveRadius = 20f;
            public long PurseMarks = 300;
            public int BearerSharePercent = 40;
            public int RaidSharePercent = 40;
            public long RaiderBountyMarks = 100;
            public int RaiderBountyHours = 2;
            public float EscortRadius = 60f;
            public int EscortMinPercent = 50;
            public int MaxEscorts = 8;
            public int SeasonPoints = 15;
            public float MaxJumpMetres = 150f;
            public int OfflineGraceSeconds = 60;
            public int SightingMinutes = 5;
            public List<Route> Routes = new List<Route>();
        }

        private class LegendDef
        {
            public string Id;
            public string Name;
            public string Lore = "";
            public List<string> Kinds = new List<string>();   // words looked for in the creature's kind, e.g. "wolf"
            public string Region = "";                 // a RealmWorld place id; "" = anywhere
            public float RegionRadius = 400f;
            public float Toughness = 4f;
            public long RewardMarks = 200;
            public List<ItemReward> Trophy = new List<ItemReward>();
            public int SeasonPoints = 20;
            public bool Enabled = true;
        }

        private class LegendCfg
        {
            public bool Enabled = true;
            public int HintMinutes = 10;
            public int HelperMinPercent = 15;
            public int HelperSharePercent = 30;
            public List<LegendDef> Legends = new List<LegendDef>();
        }

        private class Offering
        {
            public string Item;
            public int Points;
        }

        private class FestivalDef
        {
            public string Id;
            public string Name;
            public bool Enabled = true;
            public int Month;
            public int Day;
            public int Days = 7;
            public string StartUtc = "12:00";
            public string Mood = "";                   // mods/presets id the owner applies (Set-Mood.ps1 -Event <id>)
            public List<Offering> Offerings = new List<Offering>();
            public List<string> HuntKinds = new List<string>();
            public int HuntPoints = 0;
            public int DailyPlayerCap = 200;           // points one player may earn for their house in a realm day
            public List<int> PlacePoints = new List<int>();
            public int MinContributors = 2;            // a house needs this many givers to place
            public long TopGiverMarks = 100;
        }

        private class FestivalCfg
        {
            public bool Enabled = true;
            public bool Decorations = true;
            public int MaxCloseDelayMinutes = 180;
            public List<FestivalDef> Festivals = new List<FestivalDef>();
        }

        private class CensusCfg
        {
            public bool Enabled = true;
            public int MaxKnownPlayers = 20000;
        }

        private class PluginConfig
        {
            public GeneralCfg General = new GeneralCfg();
            public List<Slot> Schedule = new List<Slot>();
            public TreasureCfg Treasure = new TreasureCfg();
            public BloodCfg BloodMoon = new BloodCfg();
            public CaravanCfg Caravan = new CaravanCfg();
            public LegendCfg Legends = new LegendCfg();
            public FestivalCfg Festivals = new FestivalCfg();
            public CensusCfg Census = new CensusCfg();
        }

        private static Slot S(string id, string ev, string day, string start, int minutes, int weeks)
        {
            return new Slot { Id = id, Event = ev, Days = new List<string> { day }, StartUtc = start, DurationMinutes = minutes, WeekInterval = weeks };
        }

        private static ItemReward I(string item, int amount)
        {
            return new ItemReward { Item = item, Amount = amount };
        }

        private static Offering O(string item, int points)
        {
            return new Offering { Item = item, Points = points };
        }

        // Defaults: RealmEvents keeps Wednesday 21:00, Friday 19:00, Saturday 19:00 and Sunday 12:00-16:00 (UTC); the
        // world takes the hours in between, at least ClashBufferMinutes clear of each (the logic tests check it).
        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.General.CountdownMinutes = new List<int> { 30, 10, 1 };
            c.BloodMoon.WarningMinutes = new List<int> { 60, 30, 10 };
            c.Schedule = new List<Slot>
            {
                S("census", KCensus, "Monday", "18:00", 0, 1),
                S("caravan-monday", KCaravan, "Monday", "19:30", 45, 1),
                S("treasure-tuesday", KTreasure, "Tuesday", "19:00", 60, 1),
                S("legend-wednesday", KLegend, "Wednesday", "18:30", 60, 1),
                S("caravan-thursday", KCaravan, "Thursday", "18:00", 45, 1),
                S("blood-moon", KBlood, "Thursday", "20:00", 120, 2),
                S("treasure-saturday", KTreasure, "Saturday", "15:00", 60, 1),
                S("legend-sunday", KLegend, "Sunday", "18:00", 60, 1)
            };
            c.Treasure.Items = new List<ItemReward> { I("IronIngot", 10), I("Bread", 5) };
            c.Legends.Legends = new List<LegendDef>
            {
                new LegendDef { Id = "grey-widow", Name = "the Grey Widow", Lore = "A she-wolf grey as ash, who took the shepherds of the Fenmarch one by one.",
                    Kinds = new List<string> { "wolf" }, Trophy = new List<ItemReward> { I("WolfPelt", 3), I("Fang", 2) } },
                new LegendDef { Id = "old-ironhide", Name = "Old Ironhide", Lore = "A bear so old the arrows of three kings are still in his hide.",
                    Kinds = new List<string> { "bear" }, Toughness = 5f, RewardMarks = 250, Trophy = new List<ItemReward> { I("BearHide", 3) }, SeasonPoints = 25 },
                new LegendDef { Id = "pale-hart", Name = "the Pale Hart", Lore = "A white stag that is seen before every change of crown, and never twice in one place.",
                    Kinds = new List<string> { "deer", "stag", "hart" }, Toughness = 3f, RewardMarks = 150, Trophy = new List<ItemReward> { I("DeerSkin", 3) }, SeasonPoints = 15 }
            };
            c.Festivals.Festivals = new List<FestivalDef>
            {
                new FestivalDef { Id = "harvest", Name = "the Harvest Fair", Month = 9, Day = 22, Days = 7, Mood = "harvest-festival",
                    Offerings = new List<Offering> { O("Grain", 2), O("Flour", 3), O("Bread", 5), O("Apple", 1), O("Berry", 1), O("Cabbage", 2), O("Carrot", 2) } },
                new FestivalDef { Id = "midwinter", Name = "Midwinter", Month = 12, Day = 20, Days = 7, Mood = "midwinter",
                    Offerings = new List<Offering> { O("Lumber", 1), O("Fat", 2), O("CookedMeat", 3), O("WolfPelt", 8), O("BearHide", 10) },
                    HuntKinds = new List<string> { "wolf", "bear" }, HuntPoints = 10 }
            };
            foreach (FestivalDef f in c.Festivals.Festivals) f.PlacePoints = new List<int> { 40, 25, 10 };
            return c;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static long ClampL(long v, long lo, long hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private void ClampConfig()
        {
            if (config == null) config = DefaultConfig();
            PluginConfig d = DefaultConfig();
            if (config.General == null) config.General = d.General;
            if (config.Schedule == null) config.Schedule = d.Schedule;
            if (config.Treasure == null) config.Treasure = d.Treasure;
            if (config.BloodMoon == null) config.BloodMoon = d.BloodMoon;
            if (config.Caravan == null) config.Caravan = d.Caravan;
            if (config.Legends == null) config.Legends = d.Legends;
            if (config.Festivals == null) config.Festivals = d.Festivals;
            if (config.Census == null) config.Census = d.Census;

            GeneralCfg g = config.General;
            g.TickSeconds = ClampF(g.TickSeconds, 1f, 30f);
            g.SaveEverySeconds = Clamp(g.SaveEverySeconds, 10, 3600);
            g.ClashBufferMinutes = Clamp(g.ClashBufferMinutes, 0, 240);
            g.MaxPostponeMinutes = Clamp(g.MaxPostponeMinutes, 0, 720);
            g.HistoryLines = Clamp(g.HistoryLines, 0, 100);
            g.CountdownMinutes = g.CountdownMinutes == null ? d.General.CountdownMinutes : CleanMinutes(g.CountdownMinutes);

            var slots = new List<Slot>();
            var ids = new HashSet<string>();
            foreach (Slot s in config.Schedule)
            {
                if (s == null || Array.IndexOf(Kinds, (s.Event ?? "").ToLowerInvariant()) < 0) continue;
                s.Event = s.Event.ToLowerInvariant();
                s.Id = NormalizeId(s.Id) ?? (s.Event + "-" + (slots.Count + 1));
                if (ids.Contains(s.Id)) continue;
                ids.Add(s.Id);
                if (s.Days == null) s.Days = new List<string>();
                TimeSpan t;
                if (!TryParseHourMinute(s.StartUtc, out t)) s.StartUtc = "19:00";
                s.DurationMinutes = s.Event == KCensus ? 0 : Clamp(s.DurationMinutes, 10, 360);
                s.WeekInterval = Clamp(s.WeekInterval, 1, 8);
                s.WeekOffset = Clamp(s.WeekOffset, 0, s.WeekInterval - 1);
                s.Target = NormalizeId(s.Target) ?? "";
                slots.Add(s);
            }
            config.Schedule = slots;

            TreasureCfg t2 = config.Treasure;
            t2.StepRadius = ClampF(t2.StepRadius, 2f, 50f);
            t2.MinSecondsBetweenSteps = Clamp(t2.MinSecondsBetweenSteps, 0, 600);
            t2.HintAfterMinutes = Clamp(t2.HintAfterMinutes, 0, 360);
            t2.RewardMarks = ClampL(t2.RewardMarks, 0, 100000);
            t2.SeasonPoints = Clamp(t2.SeasonPoints, 0, 500);
            t2.ChestLockMinutes = Clamp(t2.ChestLockMinutes, 0, 120);
            t2.Items = CleanItems(t2.Items);

            BloodCfg b = config.BloodMoon;
            b.WarningMinutes = b.WarningMinutes == null ? d.BloodMoon.WarningMinutes : CleanMinutes(b.WarningMinutes);
            b.KillDeedsPerPlayer = Clamp(b.KillDeedsPerPlayer, 0, 50);
            b.MaxDeathsFedPerVictim = Clamp(b.MaxDeathsFedPerVictim, 1, 50);
            b.HousePoints = Clamp(b.HousePoints, 0, 500);
            b.HouseMinKills = Clamp(b.HouseMinKills, 1, 100);
            b.TopSlayerMarks = ClampL(b.TopSlayerMarks, 0, 100000);
            b.BeastDamageMultiplier = ClampF(b.BeastDamageMultiplier, 1f, 3f);

            CaravanCfg c = config.Caravan;
            c.MusterMinutes = Clamp(c.MusterMinutes, 1, 60);
            c.StartRadius = ClampF(c.StartRadius, 5f, 100f);
            c.ArriveRadius = ClampF(c.ArriveRadius, 5f, 100f);
            c.PurseMarks = ClampL(c.PurseMarks, 0, 100000);
            c.BearerSharePercent = Clamp(c.BearerSharePercent, 0, 100);
            c.RaidSharePercent = Clamp(c.RaidSharePercent, 0, 100);
            c.RaiderBountyMarks = ClampL(c.RaiderBountyMarks, 0, 100000);
            c.RaiderBountyHours = Clamp(c.RaiderBountyHours, 0, 72);
            c.EscortRadius = ClampF(c.EscortRadius, 10f, 300f);
            c.EscortMinPercent = Clamp(c.EscortMinPercent, 0, 100);
            c.MaxEscorts = Clamp(c.MaxEscorts, 0, 50);
            c.SeasonPoints = Clamp(c.SeasonPoints, 0, 500);
            c.MaxJumpMetres = ClampF(c.MaxJumpMetres, 30f, 2000f);
            c.OfflineGraceSeconds = Clamp(c.OfflineGraceSeconds, 0, 900);
            c.SightingMinutes = Clamp(c.SightingMinutes, 0, 60);
            var routes = new List<Route>();
            if (c.Routes != null)
                foreach (Route r in c.Routes)
                {
                    if (r == null) continue;
                    r.Id = NormalizeId(r.Id);
                    if (r.Id == null || FindRouteIn(routes, r.Id) != null || string.IsNullOrEmpty(r.From) || string.IsNullOrEmpty(r.To)) continue;
                    r.Name = Clean(r.Name, 40);
                    r.From = Clean(r.From, 40).ToLowerInvariant();
                    r.To = Clean(r.To, 40).ToLowerInvariant();
                    routes.Add(r);
                }
            c.Routes = routes;

            LegendCfg l = config.Legends;
            l.HintMinutes = Clamp(l.HintMinutes, 1, 60);
            l.HelperMinPercent = Clamp(l.HelperMinPercent, 1, 100);
            l.HelperSharePercent = Clamp(l.HelperSharePercent, 0, 100);
            var legends = new List<LegendDef>();
            if (l.Legends != null)
                foreach (LegendDef x in l.Legends)
                {
                    if (x == null) continue;
                    x.Id = NormalizeId(x.Id);
                    if (x.Id == null || FindLegendIn(legends, x.Id) != null) continue;
                    x.Name = Clean(x.Name, 40);
                    if (x.Name.Length == 0) x.Name = Pretty(x.Id);
                    x.Lore = Clean(x.Lore, 150);
                    var kinds = new List<string>();
                    if (x.Kinds != null) foreach (string k in x.Kinds) { string w = Clean(k, 30).ToLowerInvariant(); if (w.Length > 0) kinds.Add(w); }
                    x.Kinds = kinds;
                    x.Region = NormalizeId(x.Region) ?? "";
                    x.RegionRadius = ClampF(x.RegionRadius, 20f, 5000f);
                    x.Toughness = ClampF(x.Toughness, 1f, 20f);
                    x.RewardMarks = ClampL(x.RewardMarks, 0, 100000);
                    x.SeasonPoints = Clamp(x.SeasonPoints, 0, 500);
                    x.Trophy = CleanItems(x.Trophy);
                    if (kinds.Count > 0) legends.Add(x);
                }
            l.Legends = legends;

            FestivalCfg f = config.Festivals;
            f.MaxCloseDelayMinutes = Clamp(f.MaxCloseDelayMinutes, 0, 1440);
            var fests = new List<FestivalDef>();
            if (f.Festivals != null)
                foreach (FestivalDef x in f.Festivals)
                {
                    if (x == null) continue;
                    x.Id = NormalizeId(x.Id);
                    if (x.Id == null || FindFestivalIn(fests, x.Id) != null || x.Month < 1 || x.Month > 12 || x.Day < 1 || x.Day > 28 + (x.Month == 2 ? 0 : 3)) continue;
                    x.Name = Clean(x.Name, 40);
                    if (x.Name.Length == 0) x.Name = Pretty(x.Id);
                    x.Days = Clamp(x.Days, 1, 30);
                    TimeSpan t;
                    if (!TryParseHourMinute(x.StartUtc, out t)) x.StartUtc = "12:00";
                    x.Mood = NormalizeId(x.Mood) ?? "";
                    var offs = new List<Offering>();
                    if (x.Offerings != null) foreach (Offering o in x.Offerings) if (o != null && !string.IsNullOrEmpty(o.Item) && o.Points > 0) { o.Points = Clamp(o.Points, 1, 1000); o.Item = Clean(o.Item, 40); offs.Add(o); }
                    x.Offerings = offs;
                    var hk = new List<string>();
                    if (x.HuntKinds != null) foreach (string k in x.HuntKinds) { string w = Clean(k, 30).ToLowerInvariant(); if (w.Length > 0) hk.Add(w); }
                    x.HuntKinds = hk;
                    x.HuntPoints = Clamp(x.HuntPoints, 0, 1000);
                    x.DailyPlayerCap = Clamp(x.DailyPlayerCap, 1, 100000);
                    if (x.PlacePoints == null) x.PlacePoints = new List<int>();
                    for (int i = 0; i < x.PlacePoints.Count; i++) x.PlacePoints[i] = Clamp(x.PlacePoints[i], 0, 500);
                    x.MinContributors = Clamp(x.MinContributors, 1, 50);
                    x.TopGiverMarks = ClampL(x.TopGiverMarks, 0, 100000);
                    fests.Add(x);
                }
            f.Festivals = fests;
            config.Census.MaxKnownPlayers = Clamp(config.Census.MaxKnownPlayers, 100, 200000);
        }

        private static List<int> CleanMinutes(List<int> list)
        {
            var o = new List<int>();
            if (list != null) foreach (int m in list) if (m > 0 && m <= 720 && !o.Contains(m)) o.Add(m);
            o.Sort();
            o.Reverse();
            return o;
        }

        private static List<ItemReward> CleanItems(List<ItemReward> list)
        {
            var o = new List<ItemReward>();
            if (list != null) foreach (ItemReward r in list) if (r != null && !string.IsNullOrEmpty(r.Item) && r.Amount > 0) { r.Amount = Clamp(r.Amount, 1, 10000); r.Item = Clean(r.Item, 40); o.Add(r); }
            return o;
        }

        private static Route FindRouteIn(List<Route> list, string id)
        {
            foreach (Route r in list) if (r.Id == id) return r;
            return null;
        }

        private static LegendDef FindLegendIn(List<LegendDef> list, string id)
        {
            foreach (LegendDef r in list) if (r.Id == id) return r;
            return null;
        }

        private static FestivalDef FindFestivalIn(List<FestivalDef> list, string id)
        {
            foreach (FestivalDef r in list) if (r.Id == id) return r;
            return null;
        }

        #endregion

        #region Data

        private class Place
        {
            public string Id;
            public string Name;
            public float X, Y, Z;
            public float Radius = 20f;
        }

        private class Step
        {
            public float X, Y, Z;
            public float Radius;                       // 0 = Treasure.StepRadius
            public string Riddle = "";                 // the clue that leads to this place
        }

        private class ChestRef
        {
            public float X, Y, Z;
            public ulong ViewId;
        }

        private class Hunt
        {
            public string Id;
            public string Name;
            public bool Enabled = true;
            public List<Step> Steps = new List<Step>();
            public ChestRef Chest;
            public long RewardMarks = -1;              // -1 = Treasure.RewardMarks
            public int SeasonPoints = -1;
            public List<ItemReward> Items;             // null = Treasure.Items
            public int Runs, Found;
        }

        private class Deco
        {
            public string Sculpture;
            public float X, Y, Z;
            public int Turn;
            public int Placement;                      // RealmSculptor placement id while it stands (0 = none)
        }

        private class TreasureRun
        {
            public string HuntId;
            public Dictionary<string, int> Progress = new Dictionary<string, int>();        // player -> places reached
            public Dictionary<string, DateTime> LastStep = new Dictionary<string, DateTime>();
            public Dictionary<string, int> Hinted = new Dictionary<string, int>();          // player -> step a hint was given for
            public string WinnerId, WinnerName;
        }

        private class BloodRun
        {
            public Dictionary<string, int> Kills = new Dictionary<string, int>();           // killer -> counted kills
            public Dictionary<string, int> Fed = new Dictionary<string, int>();             // victim -> counted deaths
            public List<string> Pairs = new List<string>();                                  // "killer|victim"
            public Dictionary<string, int> HouseKills = new Dictionary<string, int>();
            public Dictionary<string, List<string>> HouseVictims = new Dictionary<string, List<string>>();
            public Dictionary<string, string> Names = new Dictionary<string, string>();
        }

        private class Escort
        {
            public string Name;
            public int Near;
        }

        private class CaravanRun
        {
            public string RouteId;
            public string FromName, ToName;
            public float FX, FY, FZ, TX, TY, TZ;
            public string Phase = "muster";            // muster | road
            public DateTime Depart;
            public string BearerId, BearerName, BearerHouse;
            public Dictionary<string, Escort> Escorts = new Dictionary<string, Escort>();
            public int Samples;
            public bool HasLast;
            public float LX, LY, LZ;
            public DateTime OfflineSince = DateTime.MinValue;
            public DateTime NextSighting;
        }

        private class LegendRun
        {
            public string LegendId;
            public string Mode = "region";             // bound | region
            public ulong ViewId;
            public float X, Y, Z;                      // last seen (bound) or the region's centre
            public bool HasPos;
            public Dictionary<string, float> Damage = new Dictionary<string, float>();
            public Dictionary<string, string> Names = new Dictionary<string, string>();
            public DateTime NextHint;
        }

        private class Run
        {
            public string Kind;
            public string Slot;
            public string Key;
            public DateTime Start, End;
            public bool Manual;
            public TreasureRun Treasure;
            public BloodRun Blood;
            public CaravanRun Caravan;
            public LegendRun Legend;
        }

        private class FestivalRun
        {
            public string Id, Name, Key;
            public DateTime Start, End;
            public Dictionary<string, int> HousePoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> PlayerPoints = new Dictionary<string, int>();     // "player|house" -> points given for that house
            public Dictionary<string, string> Names = new Dictionary<string, string>();
            public Dictionary<string, int> Daily = new Dictionary<string, int>();          // "day|player" -> points
            public Dictionary<string, List<string>> Givers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public List<int> Placements = new List<int>();
        }

        private class Bounty
        {
            public string Name;
            public string House;
            public long Marks;
            public DateTime Until;
            public string BearerId;
        }

        private class OwedItem
        {
            public string Item;
            public int Amount;
            public string Why;
        }

        private class ChestLock
        {
            public float X, Y, Z;
            public ulong ViewId;
            public string WinnerId;
            public DateTime Until;
        }

        private class CensusWeek
        {
            public DateTime Since;
            public List<string> Players = new List<string>();
            public int Newcomers, Peak;
            public int Treasures, CaravansHome, CaravansLost, CaravansPlundered, Legends, BloodKills, Offerings;
            public int ChronicleId = -1;
        }

        private class CensusReport
        {
            public DateTime At;
            public List<string> Lines = new List<string>();
        }

        private class StoredData
        {
            public int Format = DataFormat;
            public Dictionary<string, Place> Places = new Dictionary<string, Place>();
            public Dictionary<string, Hunt> Hunts = new Dictionary<string, Hunt>();
            public Dictionary<string, List<Deco>> Decorations = new Dictionary<string, List<Deco>>();
            public Dictionary<string, DateTime> Fired = new Dictionary<string, DateTime>();
            public Dictionary<string, DateTime> Announced = new Dictionary<string, DateTime>();
            public Dictionary<string, DateTime> Postponed = new Dictionary<string, DateTime>();   // occurrence -> retry at
            public Dictionary<string, int> Turn = new Dictionary<string, int>();                  // rotation per kind
            public Run Active;
            public FestivalRun Festival;
            public Dictionary<string, Bounty> Bounties = new Dictionary<string, Bounty>();
            public Dictionary<string, List<OwedItem>> OwedItems = new Dictionary<string, List<OwedItem>>();
            public Dictionary<string, long> OwedMarks = new Dictionary<string, long>();
            public ChestLock Lock;
            public CensusWeek Week = new CensusWeek();
            public CensusReport LastCensus;
            public List<string> Seen = new List<string>();
            public List<string> History = new List<string>();
        }

        // Read-only view of oxide/data/RealmTravel.json (RealmTravel's waystones; field names are that file's contract).
        private class TravelStone
        {
            public string Id;
            public string Name;
            public float X, Y, Z;
            public float Radius;
            public bool Enabled = true;
        }

        private class TravelView
        {
            public Dictionary<string, TravelStone> Waystones;
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
                PrintError("Could not read oxide/data/" + DataName + ".json (" + ex.Message + "). RealmWorld is paused and will NOT "
                    + "write the file. Fix it or move it away, then reload.");
                return;
            }
            if (data == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or truncated. RealmWorld is paused and will NOT write it. "
                    + "Fix it or move it away, then reload.");
                return;
            }
            if (data.Format > DataFormat)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json was written by a newer RealmWorld (format " + data.Format + "). Paused; nothing is written.");
                data = null;
                return;
            }
            NormalizeData();
        }

        private void NormalizeData()
        {
            if (data.Places == null) data.Places = new Dictionary<string, Place>();
            if (data.Hunts == null) data.Hunts = new Dictionary<string, Hunt>();
            if (data.Decorations == null) data.Decorations = new Dictionary<string, List<Deco>>();
            if (data.Fired == null) data.Fired = new Dictionary<string, DateTime>();
            if (data.Announced == null) data.Announced = new Dictionary<string, DateTime>();
            if (data.Postponed == null) data.Postponed = new Dictionary<string, DateTime>();
            if (data.Turn == null) data.Turn = new Dictionary<string, int>();
            if (data.Bounties == null) data.Bounties = new Dictionary<string, Bounty>();
            if (data.OwedItems == null) data.OwedItems = new Dictionary<string, List<OwedItem>>();
            if (data.OwedMarks == null) data.OwedMarks = new Dictionary<string, long>();
            if (data.Week == null) data.Week = new CensusWeek();
            if (data.Week.Players == null) data.Week.Players = new List<string>();
            if (data.Seen == null) data.Seen = new List<string>();
            if (data.History == null) data.History = new List<string>();
            var places = new Dictionary<string, Place>();
            foreach (KeyValuePair<string, Place> kv in data.Places)
            {
                string id = NormalizeId(kv.Key);
                if (id == null || kv.Value == null) continue;
                kv.Value.Id = id;
                kv.Value.Name = Clean(kv.Value.Name, 40);
                if (kv.Value.Name.Length == 0) kv.Value.Name = Pretty(id);
                kv.Value.Radius = ClampF(kv.Value.Radius, 2f, 5000f);
                places[id] = kv.Value;
            }
            data.Places = places;
            var hunts = new Dictionary<string, Hunt>();
            foreach (KeyValuePair<string, Hunt> kv in data.Hunts)
            {
                string id = NormalizeId(kv.Key);
                Hunt h = kv.Value;
                if (id == null || h == null) continue;
                h.Id = id;
                h.Name = Clean(h.Name, 40);
                if (h.Name.Length == 0) h.Name = Pretty(id);
                if (h.Steps == null) h.Steps = new List<Step>();
                h.Steps.RemoveAll(delegate(Step s) { return s == null; });
                foreach (Step s in h.Steps) s.Riddle = Clean(s.Riddle, 180);
                if (h.Items != null) h.Items = CleanItems(h.Items);
                hunts[id] = h;
            }
            data.Hunts = hunts;
            foreach (KeyValuePair<string, List<Deco>> kv in new List<KeyValuePair<string, List<Deco>>>(data.Decorations))
                if (kv.Value == null) data.Decorations[kv.Key] = new List<Deco>();
                else kv.Value.RemoveAll(delegate(Deco x) { return x == null || NormalizeSculpture(x.Sculpture) == null; });
            Run a = data.Active;
            if (a != null && (Array.IndexOf(Kinds, a.Kind) < 0 || (a.Kind == KTreasure && a.Treasure == null) || (a.Kind == KBlood && a.Blood == null)
                || (a.Kind == KCaravan && a.Caravan == null) || (a.Kind == KLegend && a.Legend == null)))
            {
                PrintWarning("A damaged running event record was dropped from oxide/data/" + DataName + ".json.");
                data.Active = null;
            }
            foreach (KeyValuePair<string, List<OwedItem>> kv in new List<KeyValuePair<string, List<OwedItem>>>(data.OwedItems))
            {
                if (kv.Value == null) { data.OwedItems.Remove(kv.Key); continue; }
                kv.Value.RemoveAll(delegate(OwedItem o) { return o == null || string.IsNullOrEmpty(o.Item) || o.Amount <= 0; });
                if (kv.Value.Count == 0) data.OwedItems.Remove(kv.Key);
            }
            weekSeen.Clear();
            foreach (string id in data.Week.Players) weekSeen.Add(id);
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
                { "Speaker", "World" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Paused", "The living world is paused: its record could not be read. Staff have been told." },
                { "NoPermission", "You may not do that." },
                { "UnknownSub", "There is no '{0}' here." },
                { "NoPosition", "Your place in the world cannot be read just now. Try again in a moment." },
                { "StaffWinNothing", "Staff run the living world; you take part in nothing that pays." },
                { "Help1", "The living world: [F4C96D]/world[FFFFFF] (now and next), [F4C96D]/world schedule[FFFFFF], [F4C96D]/world history[FFFFFF], [F4C96D]/world census[FFFFFF], [F4C96D]/world collect[FFFFFF] (owed rewards)." },
                { "Help2", "  Hunts: [F4C96D]/treasure[FFFFFF] (your clue, [F4C96D]/treasure hint[FFFFFF]), [F4C96D]/world legend[FFFFFF], [F4C96D]/world bloodmoon[FFFFFF]." },
                { "Help3", "  Roads and fairs: [F4C96D]/caravan[FFFFFF] (carry, escort, leave), [F4C96D]/festival[FFFFFF] (standings, give)." },
                { "HelpAdmin", "  Staff: [F4C96D]/world admin[FFFFFF] status, start, stop, schedule, place, hunt, route, deco, festival, census, creatures, legends, bounty." },

                { "Kind.treasure", "Treasure Hunt" },
                { "Kind.bloodmoon", "the Blood Moon" },
                { "Kind.caravan", "the Merchant Caravan" },
                { "Kind.legend", "a Wandering Legend" },
                { "Kind.census", "the Census" },
                { "Title.treasure", "the hunt for {0}" },
                { "Title.caravan", "the caravan from {0} to {1}" },
                { "Title.legend", "{0} roams the realm" },
                { "Countdown.treasure", "A treasure hunt begins in {0} min: {1}. Be ready to follow the clues with [F4C96D]/treasure[FFFFFF]." },
                { "Countdown.bloodmoon", "The moon rises red in {0} min. For {1} min every fair kill is remembered, and the beasts grow bold. Bar your doors or sharpen your blades." },
                { "Countdown.caravan", "A merchant caravan musters in {0} min: {1}. A bearer and escorts are wanted: [F4C96D]/caravan[FFFFFF]." },
                { "Countdown.legend", "{1} is stirring. In {0} min the hunt for a Wandering Legend begins: [F4C96D]/world legend[FFFFFF]." },
                { "StaffPostponed", "{0} waits: {1} holds the realm. It starts at {2} if the field is clear by then." },
                { "StaffSkipped", "{0} is skipped this time: {1}." },
                { "StaffTooFast", "{0} reached place {1} of {2} faster than anyone walks; not counted. Watch them." },
                { "Busy", "{0} is already under way." },

                { "WorldOff", "The living world is switched off by the staff." },
                { "WorldNow", "Now: {0}, for {1} more. See [F4C96D]{2}[FFFFFF]." },
                { "WorldQuiet", "The realm is quiet just now." },
                { "WorldFestival", "  {0} is on for {1}: [F4C96D]/festival[FFFFFF]" },
                { "BloodNow", "  The Blood Moon is up. Every fair kill tonight is remembered." },
                { "ScheduleHead", "Coming in the living world (UTC):" },
                { "ScheduleLine", "{0} - {1} {2} {3}" },
                { "ScheduleClash", "[E8913A](waits for {0})[FFFFFF]" },
                { "ScheduleNone", "Nothing is set in the living world this week." },
                { "NotScheduled", "nothing set" },
                { "When", "in {0} ({1} UTC)" },
                { "HistoryHead", "The living world lately:" },
                { "HistoryNone", "Nothing has happened in the living world yet." },

                { "NoHuntReady", "no treasure hunt is laid out (staff: [F4C96D]/world admin hunt[FFFFFF])" },
                { "TreasureBegin", "A hoard lies hidden in Ostreval: {0}, {2} clue(s) deep. The first clue: \"{1}\" Follow it with [F4C96D]/treasure[FFFFFF]." },
                { "TreasureStep", "You found place {0} of {1}! The next clue: \"{2}\"" },
                { "TreasureStatus", "The hunt for {0}: you have found {1} of {2} place(s). {3} left." },
                { "TreasureRiddle", "  Clue {0}: \"{1}\"" },
                { "TreasureHelp", "  [F4C96D]/treasure hint[FFFFFF] names a direction once per clue, after a while. The first at the dig site wins." },
                { "TreasureNone", "No hoard is hidden just now. Next hunt: {0}." },
                { "NoTreasure", "No treasure hunt is under way for you." },
                { "HintLater", "The clues must be tried first. A hint can be had in {0}." },
                { "HintUsed", "You have had your hint for this clue." },
                { "Hint", "A hint for clue {0}: look {1}, {2}." },
                { "Band.near", "close by, within a hundred paces" },
                { "Band.close", "a short walk away" },
                { "Band.mid", "some way off" },
                { "Band.far", "far off" },
                { "Band.distant", "very far, across the realm" },
                { "TreasureFoundHerald", "{0}{1} has found the hoard of {2}!" },
                { "TreasureFoundYou", "The hoard of {0} is yours: {1}; the goods are {2}." },
                { "InTheChest", "in the chest at the dig site, opened to you alone for a while" },
                { "InYourPacks", "in your packs" },
                { "NoItems", "none" },
                { "TreasureUnfound", "The hoard of {0} stays hidden. The best seekers found {1} of {2} place(s)." },
                { "ChestLocked", "This chest belongs to the treasure's finder for {0} more." },
                { "PopupClueTitle", "A Clue" },
                { "PopupClue", "You found place {0} of {1}.\n\nThe next clue:\n{2}" },
                { "PopupFoundTitle", "The Hoard Is Yours" },
                { "PopupFound", "You found the hoard of {0}.\n\n{1}\nThe goods are {2}." },
                { "PopupOk", "Onward" },

                { "BloodBegin", "The Blood Moon rises! For {0} min every fair kill is remembered and the beasts are hungry. Go in company." },
                { "BloodKillYou", "{0} falls under the Blood Moon. Your kills tonight: {1}." },
                { "BloodEnd", "The Blood Moon sets. {0} fair kill(s) were counted.{1}{2}" },
                { "BloodTopSlayer", " The deadliest: {0} ({1})." },
                { "BloodTopHouse", " House {0} took the night ({1})." },
                { "BloodEndQuiet", "The Blood Moon sets on a quiet realm. No blood was shed that the realm could count." },
                { "BloodNone", "The moon is pale. Next Blood Moon: {0}." },
                { "BloodStatus", "The Blood Moon is up for {0}. Your counted kills: {1} (renown for the first {2})." },
                { "BloodRow", "  House {0}: {1}" },

                { "NoRouteReady", "no caravan route is set (staff: [F4C96D]/world admin route add[FFFFFF])" },
                { "RouteUnknown", "route {0}: the place '{1}' is not known" },
                { "CaravanMuster", "A merchant caravan musters at {0}, bound for {1} ({3} m). It leaves in {2} min. Bear it with [F4C96D]/caravan carry[FFFFFF] at the start, or guard it with [F4C96D]/caravan escort[FFFFFF]." },
                { "CaravanBearer", "{0}{1} takes up the merchant's goods, bound for {2}." },
                { "CaravanYouBear", "You bear the caravan to {0}. Stay close to the start; it leaves in {1}. Walk: the waystones will not carry it." },
                { "CaravanYouEscort", "You guard the caravan to {0}. Keep within sight of the bearer ({1}% of the road) to share the purse." },
                { "CaravanEscortJoined", "{0} rides with you as an escort." },
                { "CaravanAlreadyEscort", "You already guard this caravan." },
                { "CaravanYouAreBearer", "You bear the caravan; you cannot guard it as well." },
                { "CaravanHasBearer", "{0} already bears the goods. Guard them with [F4C96D]/caravan escort[FFFFFF]." },
                { "CaravanGone", "The caravan has left; the goods have their bearer." },
                { "CaravanTooFar", "Come to {0} (within {1} m) to take up the goods." },
                { "CaravanEscortFar", "Join the caravan at {0}, or come within sight of its bearer." },
                { "CaravanFull", "The caravan has all the guards it can pay ({0})." },
                { "CaravanProtected", "Newcomers under protection cannot bear the caravan: no raider could touch it." },
                { "CaravanRaiderNo", "The merchants will not trust their goods to a raider with a price on their head." },
                { "CaravanLeft", "You no longer guard the caravan." },
                { "CaravanDropped", "You were not with the caravan, so your place among its guards went to another." },
                { "CaravanBearerLeft", "The caravan at {0} needs a new bearer: [F4C96D]/caravan carry[FFFFFF]." },
                { "CaravanBearerStays", "The bearer cannot leave the road. Bring the goods home." },
                { "CaravanNotWith", "You are not with the caravan." },
                { "NoCaravan", "No caravan is on the road." },
                { "CaravanDeparts", "The caravan sets out from {1}, borne by {0} with {4} escort(s). It must reach {2} within {3} min." },
                { "CaravanNoWaystone", "The waystones will not carry the merchant's goods. Walk." },
                { "CaravanSighted", "The caravan was seen {0} {1} of {2}. {3} min of road left." },
                { "CaravanArrived", "The caravan reaches {0}! {1}{2} brought it home with {3} escort(s)." },
                { "CaravanPaidYou", "The merchants of {0} pay you: {1}." },
                { "CaravanNotClose", "You did not keep close enough to the bearer ({0}% of the road) to share the purse." },
                { "CaravanYouPlunder", "You plunder the caravan: {0}. The merchants will want your head." },
                { "CaravanPlundered", "{0}{1} has plundered the merchant caravan, cutting down its bearer {2}!{3}" },
                { "CaravanBountySet", " The merchants pay {0} marks to whoever brings the raider down within {1} h." },
                { "CaravanNoBearer", "The caravan at {2} finds no bearer and stays home." },
                { "CaravanDeserted", "The bearer {1} has left the road; the caravan to {0} is lost." },
                { "CaravanJumped", "The goods bound for {0} vanished from the road with {1}. The caravan is lost." },
                { "CaravanDuelled", "The bearer {1} left the road for a duel; the caravan to {0} is lost." },
                { "CaravanInDuel", "Finish your duel first: the merchants want a bearer on the road, not in the ring." },
                { "CaravanFell", "The bearer {1} fell on the road to {0}; the goods are scattered." },
                { "CaravanBetrayed", "The bearer {1} was cut down by friends on the road to {0}. Nobody profits; the goods are lost." },
                { "CaravanStopped", "The caravan to {0} is called off." },
                { "CaravanLate", "Night falls on the road; the caravan never reaches {0}." },
                { "CaravanNone", "No caravan is on the road. Next: {0}." },
                { "CaravanStatusMuster", "A caravan musters at {0} for {1}. Bearer: {2}, escorts: {3}. It leaves in {4}." },
                { "CaravanStatusRoad", "The caravan from {0} to {1} is on the road. Bearer: {2}, escorts: {3}. {4} left." },
                { "Nobody", "nobody yet" },
                { "CaravanHelp", "  [F4C96D]/caravan carry[FFFFFF] to bear it, [F4C96D]/caravan escort[FFFFFF] to guard it, [F4C96D]/caravan leave[FFFFFF] to stop guarding." },
                { "BountyLine", "  Wanted by the merchants: {0}, {1} marks, for {2} more." },
                { "BountyClaimed", "{0} has brought down {1}, the raider of the caravan, and claims the merchants' price." },
                { "BountyPaidYou", "The merchants pay you for {0}: {1}." },
                { "BountyNotYours", "The merchants do not pay the bearer they lost for the raider's head." },
                { "BountyLapsed", "The merchants' price on {0} lapses." },

                { "NoLegendReady", "no Wandering Legend is set (see Legends in oxide/config/RealmWorld.json)" },
                { "RegionUnknown", "legend {0}: the region '{1}' is not a known place" },
                { "LegendBegin", "{0} roams Ostreval! {1} {2}" },
                { "LegendAnywhere", "It could be anywhere: any {0} may be the one." },
                { "LegendRegion", "It haunts the land within {0} m of {1}: the first {2} slain there is the one." },
                { "LegendSeenAt", "It was last seen at {0}." },
                { "LegendSeenNear", "It was last seen {0} {1} of {2}." },
                { "LegendHint", "{0}: {1} {2} min until it slips away." },
                { "LegendSlainHerald", "{0}{1} has slain {2}!" },
                { "LegendSlainYou", "You slew {0}: {1}. Its trophy goes to your packs." },
                { "LegendHelperPaid", "You helped bring down {0}: {1}." },
                { "LegendEscaped", "{0} slips away into the wild. Perhaps another day." },
                { "LegendDied", "{0} is dead, but no hunter can claim it." },
                { "LegendStaff", "{0} fell to the staff's own hand. No one is rewarded." },
                { "LegendStopped", "The hunt for {0} is called off." },
                { "LegendNone", "No Wandering Legend roams just now. Next: {0}." },
                { "LegendStatus", "{0}: {1} {2} {3} left." },
                { "PopupLegendTitle", "A Legend Falls" },
                { "PopupLegend", "{0} is slain by your hand.\n\n{1}" },

                { "NoFestival", "No festival is on." },
                { "FestivalBegin", "{0} begins and runs for {1}! The houses compete in offerings: [F4C96D]/festival[FFFFFF]." },
                { "FestivalHow", "  Bring goods for your house with [F4C96D]/festival give[FFFFFF]. Points: {0}." },
                { "FestivalStopped", "{0} is cancelled by the staff." },
                { "FestivalEnd", "{0} ends! House {1} wins. Best giver: {2}." },
                { "FestivalEndNone", "{0} ends. No house gave enough to place." },
                { "FestivalPlace", "  {0}. House {1}: {2}" },
                { "FestivalNoHouse", "Only sworn members of a house can give at the festival: [F4C96D]/house[FFFFFF]." },
                { "FestivalNoOfferings", "This festival takes no offerings." },
                { "NoPacks", "Your packs cannot be read just now." },
                { "FestivalNotOffering", "'{0}' is not an offering here. Offerings: {1}." },
                { "FestivalNothing", "You carry nothing the festival takes. Offerings (points each): {0}." },
                { "FestivalCapped", "You have given all you can for today ({0} points). Come back tomorrow." },
                { "FestivalGave", "You give {0} {1}: {2} point(s)." },
                { "FestivalHouseNow", "House {0} now has {1} point(s) (+{2})." },
                { "FestivalHuntPoints", "+{0} for House {1} at the festival hunt." },
                { "FestivalNone", "No festival is on. Next: {0} on {1}." },
                { "FestivalStatus", "{0}: {1} left." },
                { "FestivalNoStandings", "  No house has placed yet (two givers or more are needed)." },
                { "FestivalRow", "  {0}. House {1}: {2}" },
                { "FestivalYours", "  Your house {0}: {1}. Yours: {2}; you may still give {3} point(s) today." },
                { "FestivalOfferings", "  Offerings (points each): {0}." },
                { "FestivalHuntLine", "  The hunt: each {0} slain earns {1}." },
                { "FestivalHelp", "  [F4C96D]/festival give[FFFFFF] [item|all] hands in goods for your house." },

                { "CensusHeader", "The census of Ostreval, {0}:" },
                { "CensusSouls", "{0} soul(s) walked the realm this week ({1} new), {2} at most at once." },
                { "CensusHouses", "{0} house(s); the largest is House {1} with {2}." },
                { "CensusNoHouses", "{0} house(s)." },
                { "CensusCrown", "{0}{1} sits upon the Old Throne." },
                { "CensusNoCrown", "The Old Throne stands empty." },
                { "CensusSeason", "House {0} leads the season with {1}." },
                { "CensusTreasury", "{0} marks struck in all; the crown holds {1}." },
                { "CensusChronicle", "{0} new entries in the Chronicle." },
                { "CensusDeeds", "Hoards found: {0}. Caravans home: {1}, plundered: {2}, lost: {3}. Legends slain: {4}. Blood Moon kills: {5}." },
                { "CensusOfferings", "{0} goods offered at the festival." },
                { "CensusNever", "No census has been taken yet. The next: {0}." },
                { "CensusLast", "The last census, {0}:" },
                { "CensusSoFar", "  This week so far: {0} soul(s), {1} new, {2} at most at once." },

                { "ItemsGiven", "Into your packs: {0}." },
                { "ItemsOwed", "Some of it did not fit. Make room and use [F4C96D]/world collect[FFFFFF]." },
                { "MarksOwedPaid", "The treasury pays what it owed you: {0} marks." },
                { "NothingOwed", "Nothing is owed to you." },
                { "MarksPaid", "{0} marks" },
                { "MarksPartly", "{0} marks now, {1} more owed (paid when the treasury allows)" },
                { "NoMarks", "no marks" },
                { "OfHouse", " of House {0}" },
                { "UnknownItem", "'{0}' is not an item this server knows." },

                { "AdminHelp1", "[F4C96D]/world admin[FFFFFF] status | start <kind> [target] [minutes] [force] | stop | schedule | census | creatures | legends | bounty [clear <name>]" },
                { "AdminHelp2", "  place set <id> [radius] [name] | place clear <id> | places | route add <id> <from> <to> [name] | route remove <id> | routes" },
                { "AdminHelp3", "  hunt new|step|undo|radius|chest|reward|enable|show|remove|list | deco add|remove|list <festival> | festival start <id> [days]|stop|cancel" },
                { "AdminStatus1", "World: {0}. Running: {1}. Festival: {2}." },
                { "AdminStatus2", "  Places {0}, hunts {1}, routes {2}, legends {3}, raider bounties {4}, players owed {5}." },
                { "AdminStatus3", "  {0}" },
                { "AdminPostponed", "  Waiting: {0} until {1}." },
                { "AdminStartUsage", "Usage: [F4C96D]/world admin start[FFFFFF] treasure|bloodmoon|caravan|legend|census [target] [minutes] [force]" },
                { "AdminClash", "That would meet {0} (RealmEvents). Add 'force' to start it anyway." },
                { "AdminStartFailed", "It cannot start: {0}." },
                { "AdminStarted", "{0} starts now, for {1} min." },
                { "AdminNothingRunning", "Nothing is running." },
                { "AdminStopped", "{0} is called off." },
                { "AdminDone", "Done: {0}." },
                { "AdminPlaceUsage", "Usage: [F4C96D]/world admin place[FFFFFF] set <id> [radius] [name] | clear <id>" },
                { "AdminNoPlace", "No place with the id '{0}'." },
                { "AdminPlaceSet", "Place {0} ({1}) set here at {2}, radius {3} m." },
                { "AdminPlacesHead", "Places ({0}):" },
                { "AdminWaystones", "  Waystones (RealmTravel): {0}" },
                { "AdminHuntsHead", "Treasure hunts ({0}):" },
                { "AdminHuntUsage", "Usage: [F4C96D]/world admin hunt[FFFFFF] new <id> <name> | step <id> <riddle> | undo <id> | radius <id> <n> <m> | chest <id> [clear] | reward <id> ... | enable <id> on|off | show <id> | remove <id>" },
                { "AdminHuntExists", "A hunt with the id '{0}' exists already." },
                { "AdminHuntNew", "Hunt {0} ({1}) begun. Stand at each place in order and add its riddle (the clue that leads THERE) with step." },
                { "AdminNoHunt", "No hunt with the id '{0}'." },
                { "AdminHuntRunning", "{0} is being hunted right now; change it afterwards." },
                { "AdminRiddleShort", "A riddle needs a few words." },
                { "AdminTooManySteps", "A hunt has at most {0} places." },
                { "AdminStepAdded", "Place {0} of {1} set here at {2}. The last place is the dig site." },
                { "AdminNoSteps", "That hunt has no places." },
                { "AdminRadiusUsage", "Usage: [F4C96D]/world admin hunt radius[FFFFFF] <id> <place number> <2-50 m>" },
                { "AdminNoChest", "No chest within 5 m. Stand next to it." },
                { "AdminChestBound", "The chest at {1} is bound to {0}: the finder's prize goes in it." },
                { "AdminChestFar", "  The chest is not at the dig site (the last place). Players would find it before the clues lead there." },
                { "AdminRewardUsage", "Usage: [F4C96D]/world admin hunt reward[FFFFFF] <id> marks <n> | points <n> | item <name> <n> | items clear | default" },
                { "AdminHuntShow", "{0} ({1}): {2} place(s); reward {3}; chest {4}" },
                { "AdminRemoveAsk", "This removes {0} for good. Repeat within two minutes: [F4C96D]{1}[FFFFFF]" },
                { "AdminRouteUsage", "Usage: [F4C96D]/world admin route[FFFFFF] add <id> <from> <to> [name] | remove <id>. Places: a place id or waystone:<id>." },
                { "AdminNoRoute", "No route with the id '{0}'." },
                { "AdminUnknownPlace", "'{0}' is not a place (see [F4C96D]/world admin places[FFFFFF])." },
                { "AdminRouteSame", "A route needs two different places." },
                { "AdminRouteAdded", "Route {0}: {1}." },
                { "AdminRoutesHead", "Caravan routes ({0}):" },
                { "AdminDecoUsage", "Usage: [F4C96D]/world admin deco[FFFFFF] add <festival> <sculpture> | remove <festival> <n> | list <festival>" },
                { "AdminDecoHead", "Decorations of {0} ({1}):" },
                { "AdminDecoStanding", "That decoration stands now (placement #{0}); remove it after the festival." },
                { "AdminDecoMany", "A festival has at most {0} decorations." },
                { "AdminDecoAdded", "{0} will be raised here for {1} (decoration {2}). Stand as for [F4C96D]/sculpt place[FFFFFF]: it rises in front of you." },
                { "AdminFestivalUsage", "Usage: [F4C96D]/world admin festival[FFFFFF] start <id> [days] | stop | cancel" },
                { "AdminFestivalRunning", "{0} is on already." },
                { "AdminCreatures", "{0} creature(s) in the world; within 300 m: {1}." },
                { "AdminCreaturesFailed", "The creature list could not be read: {0}" },
                { "AdminBountiesHead", "Raider bounties ({0}):" },
                { "AdminNoBounty", "No bounty on '{0}'." },
                { "On", "on" },
                { "Off", "off" },
                { "Nothing", "nothing" },
                { "Loaded", "loaded" },
                { "Absent", "absent" },

                { "HistTreasureBegin", "Hunt for {0} began" },
                { "HistTreasureFound", "{1} found the hoard of {0}" },
                { "HistTreasureUnfound", "The hoard of {0} stayed hidden" },
                { "HistBloodBegin", "The Blood Moon rose" },
                { "HistBloodEnd", "The Blood Moon set ({0} kills)" },
                { "HistCaravanBegin", "Caravan {0} - {1} mustered" },
                { "HistCaravanHome", "Caravan reached {0} (borne by {1})" },
                { "HistCaravanRaided", "Caravan plundered by {0}" },
                { "HistCaravanLost", "Caravan to {0} lost" },
                { "HistBounty", "{1} took the merchants' price on {0}" },
                { "HistLegendBegin", "{0} roamed" },
                { "HistLegendSlain", "{1} slew {0}" },
                { "HistLegendGone", "{0} escaped" },
                { "HistFestivalBegin", "{0} began" },
                { "HistFestivalEnd", "{0} ended (winner: {1})" },
                { "HistCensus", "The census was taken" },

                { "Board.Kicker", "The Living World" },
                { "Board.WorldTitle", "Abroad in Ostreval" },
                { "Board.Now", "Now: {0}, until {1} UTC" },
                { "Board.NothingSet", "The heralds will post the next wonder here." },
                { "Board.WorldFooter", "Ask the herald: [F4C96D]/world[FFFFFF]" },
                { "Board.TreasureKicker", "A treasure hunt" },
                { "Board.ColdTitle", "The Trail Is Cold" },
                { "Board.Cold", "No hoard is hidden just now." },
                { "Board.ColdNext", "The next hunt begins {0}." },
                { "Board.ClueN", "Clue {0} of {1}" },
                { "Board.TreasureFooter", "Your clue: [F4C96D]/treasure[FFFFFF]" },
                { "Board.FestivalKicker", "The festival" },
                { "Board.NoFestivalTitle", "No Fair Today" },
                { "Board.NoFestival", "The houses will gather again at the next festival." },
                { "Board.FestivalUntil", "Until {0}" },
                { "Board.FestivalEmpty", "No house has placed yet." },
                { "Board.FestivalFooter", "Give for your house: [F4C96D]/festival[FFFFFF]" },
                { "Board.CensusKicker", "By count of the heralds" },
                { "Board.CensusTitle", "The Census" },
                { "Board.CensusNone", "No census has been taken yet." },

                { "Metres", "{0} m" },
                { "Km", "{0} km" },
                { "Sec", "{0} s" },
                { "Min", "{0} min" },
                { "Hours", "{0} h" },
                { "Days", "{0} days" },
                { "Dir.0", "north" },
                { "Dir.1", "north-east" },
                { "Dir.2", "east" },
                { "Dir.3", "south-east" },
                { "Dir.4", "south" },
                { "Dir.5", "south-west" },
                { "Dir.6", "west" },
                { "Dir.7", "north-west" }
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

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "TreasureStep", "TreasureFoundYou", "CaravanYouBear", "CaravanYouEscort", "CaravanPaidYou", "BountyPaidYou", "LegendSlainYou",
            "LegendHelperPaid", "FestivalGave", "FestivalHouseNow", "FestivalHuntPoints", "ItemsGiven", "MarksOwedPaid", "BloodKillYou",
            "AdminDone", "AdminStarted", "AdminPlaceSet", "AdminHuntNew", "AdminStepAdded", "AdminChestBound", "AdminRouteAdded", "AdminDecoAdded"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "CaravanNoWaystone", "CaravanNotClose", "CaravanDropped", "CaravanYouPlunder", "ItemsOwed", "StaffWinNothing", "AdminRemoveAsk", "AdminStopped",
            "AdminChestFar", "WorldOff", "BountyNotYours", "AdminPostponed"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintError("oxide/config/RealmWorld.json could not be read (" + ex.Message + "); using the defaults for this run.");
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
            if (data.Week.Since == DateTime.MinValue) { data.Week.Since = Now(); data.Week.ChronicleId = LastChronicleId(); dirty = true; }
            foreach (Player p in OnlinePlayers()) NoteSeen(p);
            if (data.Active != null && data.Active.Kind == KLegend) Rebind(data.Active, false);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            legendEntity = null;
            SaveData();
        }

        #endregion

        #region Scheduler

        private void SafeTick()
        {
            if (loadFailed || data == null) return;
            try { Tick(); }
            catch (Exception ex) { PrintError("World tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = Now();
            if (config.General.Enabled)
            {
                Run a = data.Active;
                if (a != null)
                {
                    if (now >= a.End) Finish(a, null);
                    else TickRun(a, now);
                }
                Schedule(now);
                TickFestival(now);
            }
            TickBounties(now);
            int online = OnlinePlayers().Count;
            if (online > data.Week.Peak) { data.Week.Peak = online; dirty = true; }
            if (data.Lock != null && now >= data.Lock.Until) { data.Lock = null; dirty = true; }
            if (dirty && now >= nextSave) SaveData();
        }

        private void TickRun(Run a, DateTime now)
        {
            if (a.Kind == KTreasure) TickTreasure(a, now);
            else if (a.Kind == KCaravan) TickCaravan(a, now);
            else if (a.Kind == KLegend) TickLegend(a, now);
        }

        private bool KindEnabled(string kind)
        {
            if (kind == KTreasure) return config.Treasure.Enabled;
            if (kind == KBlood) return config.BloodMoon.Enabled;
            if (kind == KCaravan) return config.Caravan.Enabled;
            if (kind == KLegend) return config.Legends.Enabled;
            if (kind == KCensus) return config.Census.Enabled;
            return false;
        }

        private void Schedule(DateTime now)
        {
            int maxCountdown = 0;
            foreach (int m in config.General.CountdownMinutes) if (m > maxCountdown) maxCountdown = m;
            foreach (int m in config.BloodMoon.WarningMinutes) if (m > maxCountdown) maxCountdown = m;
            foreach (Slot s in config.Schedule)
            {
                if (!s.Enabled || !KindEnabled(s.Event)) continue;
                int window = Math.Max(1, s.DurationMinutes) + config.General.MaxPostponeMinutes;
                foreach (DateTime start in Occurrences(s, now.AddMinutes(-window), now.AddMinutes(maxCountdown + 1)))
                {
                    string key = s.Id + "@" + Iso(start);
                    if (data.Fired.ContainsKey(key)) continue;
                    DateTime planned = start;
                    DateTime retry;
                    if (data.Postponed.TryGetValue(key, out retry)) planned = retry;
                    DateTime end = planned.AddMinutes(s.DurationMinutes);
                    if (now < planned)
                    {
                        if (planned == start) Countdown(s, key, start, now);
                        continue;
                    }
                    if (s.DurationMinutes > 0 && now >= end) { data.Fired[key] = start; data.Postponed.Remove(key); dirty = true; continue; }
                    if (s.Event == KCensus && (now - start).TotalMinutes > config.General.MaxPostponeMinutes + 10) { data.Fired[key] = start; dirty = true; continue; }   // a server down at census time does not shout an old census
                    TryStart(s, key, start, planned, now);
                }
            }
            DateTime cutoff = now.AddDays(-20);
            if (Prune(data.Fired, cutoff) | Prune(data.Announced, cutoff) | Prune(data.Postponed, cutoff)) dirty = true;
        }

        // Starts an occurrence that is due, or puts it off while something else holds the realm's attention.
        private void TryStart(Slot s, string key, DateTime start, DateTime planned, DateTime now)
        {
            if (s.Event == KCensus)
            {
                DateTime busyUntil;
                if (Busy(now, out busyUntil) && (now - start).TotalMinutes < config.General.MaxPostponeMinutes) return;   // the census waits for quiet
                data.Fired[key] = start;
                dirty = true;
                RunCensus(null);
                return;
            }
            DateTime end = now.AddMinutes(s.DurationMinutes);
            DateTime latest = start.AddMinutes(config.General.MaxPostponeMinutes);
            string why;
            DateTime retry;
            if (data.Active != null)
            {
                why = KindName(data.Active.Kind, null);
                retry = data.Active.End;
            }
            else if (!EventsClash(now, end, out why, out retry))
            {
                data.Fired[key] = start;
                data.Postponed.Remove(key);
                dirty = true;
                string fail = Begin(s.Event, s.Id, key, s.Target, now, end, false);
                if (fail != null)
                {
                    PrintWarning("Skipped " + key + ": " + fail);
                    StaffNote(Fmt("StaffSkipped", null, KindName(s.Event, null), fail));
                }
                return;
            }
            if (retry > now && retry <= latest)
            {
                DateTime old;
                if (!data.Postponed.TryGetValue(key, out old) || old != retry)
                {
                    data.Postponed[key] = retry;
                    dirty = true;
                    PrintWarning("Postponed " + key + " to " + Iso(retry) + ": " + why);
                    StaffNote(Fmt("StaffPostponed", null, KindName(s.Event, null), why, ClockText(retry)));
                }
                return;
            }
            data.Fired[key] = start;
            data.Postponed.Remove(key);
            dirty = true;
            PrintWarning("Skipped " + key + ": " + why);
            StaffNote(Fmt("StaffSkipped", null, KindName(s.Event, null), why));
        }

        // True while a timed world event or any RealmEvents event runs (busyUntil = when it ends, if known).
        private bool Busy(DateTime now, out DateTime busyUntil)
        {
            busyUntil = DateTime.MinValue;
            if (data.Active != null) { busyUntil = data.Active.End; return true; }
            DateTime until;
            string kind;
            if (RealmEventsRunning(now, out kind, out until)) { busyUntil = until; return true; }
            return false;
        }

        // Would a world event from `start` to `end` share time with RealmEvents? why = the event; retry = when the
        // running one ends plus the buffer (DateTime.MaxValue when the next one starts too soon to fit).
        private bool EventsClash(DateTime start, DateTime end, out string why, out DateTime retry)
        {
            why = null;
            retry = DateTime.MaxValue;
            if (!config.General.AvoidRealmEvents || RealmEvents == null) return false;
            int buffer = config.General.ClashBufferMinutes;
            string kind;
            DateTime until;
            if (RealmEventsRunning(start, out kind, out until))
            {
                why = EventTitle(kind);
                retry = until == DateTime.MaxValue ? DateTime.MaxValue : until.AddMinutes(buffer);
                return true;
            }
            Dictionary<string, object> next = Ask(RealmEvents, "GetNextEvent") as Dictionary<string, object>;
            if (next != null && next.ContainsKey("at") && next["at"] is DateTime)
            {
                DateTime at = DateTime.SpecifyKind((DateTime)next["at"], DateTimeKind.Utc);
                if (at < end.AddMinutes(buffer) && at.AddMinutes(buffer) > start)
                {
                    why = next.ContainsKey("title") ? Clean(next["title"] as string, 40) : "?";
                    return true;
                }
            }
            return false;
        }

        // RealmEvents.GetActiveEvents() -> "kind|startIso|endIso"; the latest end of any running event.
        private bool RealmEventsRunning(DateTime now, out string kind, out DateTime until)
        {
            kind = null;
            until = DateTime.MinValue;
            if (RealmEvents == null) return false;
            string[] active = Ask(RealmEvents, "GetActiveEvents") as string[];
            if (active == null || active.Length == 0) return false;
            foreach (string line in active)
            {
                if (string.IsNullOrEmpty(line)) continue;
                string[] parts = line.Split('|');
                DateTime e;
                if (parts.Length < 3 || !TryIso(parts[2], out e)) e = DateTime.MaxValue;
                if (e <= now) continue;
                if (kind == null || e > until) { kind = parts[0]; until = e; }
            }
            return kind != null;
        }

        private void Countdown(Slot s, string key, DateTime start, DateTime now)
        {
            if (s.Event == KCensus) return;
            List<int> marks = s.Event == KBlood ? config.BloodMoon.WarningMinutes : config.General.CountdownMinutes;
            int minutesLeft = (int)Math.Ceiling((start - now).TotalMinutes);
            bool due = false;
            foreach (int t in marks)
            {
                if (minutesLeft > t) continue;
                string akey = key + "|" + t;
                if (data.Announced.ContainsKey(akey)) continue;
                data.Announced[akey] = start;
                dirty = true;
                due = true;                                    // several marks passed at once: one herald
            }
            if (!due) return;
            string why;
            DateTime retry;
            if (data.Active != null && data.Active.End > start) return;                     // it will wait; no promise heralded
            if (EventsClash(start, start.AddMinutes(s.DurationMinutes), out why, out retry))
            {
                if (!Throttled("clashnote|" + key, 3600)) PrintWarning("No countdown for " + key + ": it would meet " + why + ".");
                return;
            }
            Herald(Fmt("Countdown." + s.Event, null, minutesLeft, CountdownDetail(s)));
        }

        private string CountdownDetail(Slot s)
        {
            if (s.Event == KTreasure) { Hunt h = PickHunt(s.Target, false); return h != null ? h.Name : "?"; }
            if (s.Event == KCaravan) { Route r = PickRoute(s.Target, false); return r != null ? RouteText(r) : "?"; }
            if (s.Event == KLegend) { LegendDef l = PickLegend(s.Target, false); return l != null ? l.Name : "?"; }
            return s.DurationMinutes.ToString(CultureInfo.InvariantCulture);
        }

        // Occurrence starts of a slot with from <= start <= to (UTC).
        private static List<DateTime> Occurrences(Slot s, DateTime from, DateTime to)
        {
            var list = new List<DateTime>();
            TimeSpan at;
            if (!TryParseHourMinute(s.StartUtc, out at)) return list;
            for (DateTime day = from.Date.AddDays(-1); day <= to.Date; day = day.AddDays(1))
            {
                if (!DayMatches(s.Days, day.DayOfWeek) || !WeekMatches(s, day)) continue;
                DateTime t = day + at;
                if (t >= from && t <= to) list.Add(t);
            }
            return list;
        }

        private static readonly DateTime WeekZero = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);   // a Monday

        private static bool WeekMatches(Slot s, DateTime day)
        {
            if (s.WeekInterval <= 1) return true;
            int week = (int)Math.Floor((day.Date - WeekZero).TotalDays / 7.0);
            int r = ((week % s.WeekInterval) + s.WeekInterval) % s.WeekInterval;
            return r == s.WeekOffset;
        }

        private static bool DayMatches(List<string> days, DayOfWeek dow)
        {
            foreach (string raw in days)
            {
                string d = (raw ?? "").Trim();
                if (d.Equals("Daily", StringComparison.OrdinalIgnoreCase)) return true;
                if (d.Equals("Weekdays", StringComparison.OrdinalIgnoreCase) && dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday) return true;
                if (d.Equals("Weekends", StringComparison.OrdinalIgnoreCase) && (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday)) return true;
                if (d.Equals(dow.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private class Upcoming
        {
            public Slot Slot;
            public DateTime At;
        }

        // The next occurrences of every enabled slot within `days`, soonest first.
        private List<Upcoming> NextOccurrences(DateTime now, int days, int max)
        {
            var list = new List<Upcoming>();
            foreach (Slot s in config.Schedule)
            {
                if (!s.Enabled || !KindEnabled(s.Event)) continue;
                foreach (DateTime t in Occurrences(s, now, now.AddDays(days)))
                {
                    if (data.Fired.ContainsKey(s.Id + "@" + Iso(t))) continue;
                    list.Add(new Upcoming { Slot = s, At = t });
                }
            }
            list.Sort(delegate(Upcoming a, Upcoming b) { return a.At.CompareTo(b.At); });
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        // Starts a world event now; returns null, or why it cannot start.
        private string Begin(string kind, string slot, string key, string target, DateTime now, DateTime end, bool manual)
        {
            if (data.Active != null) return Fmt("Busy", null, KindName(data.Active.Kind, null));
            int serial;
            data.Turn.TryGetValue("serial", out serial);
            data.Turn["serial"] = serial + 1;                  // every run has its own key (deeds are deduplicated by it)
            var a = new Run { Kind = kind, Slot = slot ?? "manual", Key = (key ?? ("manual@" + Iso(now))) + "#" + (serial + 1), Start = now, End = end, Manual = manual };
            string fail = null;
            if (kind == KTreasure) fail = BeginTreasure(a, target);
            else if (kind == KBlood) BeginBlood(a);
            else if (kind == KCaravan) fail = BeginCaravan(a, target);
            else if (kind == KLegend) fail = BeginLegend(a, target);
            else fail = "unknown kind";
            if (fail != null) return fail;
            data.Active = a;
            dirty = true;
            SaveData();
            RefreshBoards();
            return null;
        }

        // Ends the running world event. outcome null = its time ran out.
        private void Finish(Run a, string outcome)
        {
            if (a == null || data.Active != a) return;
            data.Active = null;
            dirty = true;
            try
            {
                if (a.Kind == KTreasure) FinishTreasure(a, outcome);
                else if (a.Kind == KBlood) FinishBlood(a);
                else if (a.Kind == KCaravan) FinishCaravan(a, outcome);
                else if (a.Kind == KLegend) FinishLegend(a, outcome);
            }
            catch (Exception ex) { PrintError("Ending " + a.Kind + " failed: " + ex.Message); }
            legendEntity = a.Kind == KLegend ? null : legendEntity;
            SaveData();
            RefreshBoards();
        }

        private static bool Prune(Dictionary<string, DateTime> d, DateTime cutoff)
        {
            var old = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in d) if (kv.Value < cutoff) old.Add(kv.Key);
            foreach (string k in old) d.Remove(k);
            return old.Count > 0;
        }

        // The next of a kind's targets in turn (hunts, routes, legends rotate; a slot may name one).
        private int NextTurn(string kind, int count, bool advance)
        {
            int t;
            data.Turn.TryGetValue(kind, out t);
            if (count <= 0) return -1;
            int i = ((t % count) + count) % count;
            if (advance) { data.Turn[kind] = i + 1; dirty = true; }
            return i;
        }

        #endregion

        #region Treasure

        private static bool HuntReady(Hunt h)
        {
            return h != null && h.Enabled && h.Steps.Count >= 1 && h.Steps[0].Riddle.Length > 0;
        }

        // The hunt a slot names, or the next ready one in turn.
        private Hunt PickHunt(string target, bool advance)
        {
            Hunt h;
            if (!string.IsNullOrEmpty(target)) return data.Hunts.TryGetValue(target, out h) && HuntReady(h) ? h : null;
            var ready = new List<Hunt>();
            foreach (Hunt x in data.Hunts.Values) if (HuntReady(x)) ready.Add(x);
            ready.Sort(delegate(Hunt a, Hunt b) { return string.CompareOrdinal(a.Id, b.Id); });
            int i = NextTurn(KTreasure, ready.Count, advance);
            return i < 0 ? null : ready[i];
        }

        private string BeginTreasure(Run a, string target)
        {
            Hunt h = PickHunt(target, true);
            if (h == null) return Msg("NoHuntReady", null);
            a.Treasure = new TreasureRun { HuntId = h.Id };
            h.Runs++;
            Herald(Fmt("TreasureBegin", null, h.Name, h.Steps[0].Riddle, h.Steps.Count));
            Chronicle(ChronicleStart, "The hunt for " + h.Name + " begins", "A hoard is hidden in Ostreval. The first clue: " + h.Steps[0].Riddle, new string[0]);
            AddHistory(Fmt("HistTreasureBegin", null, h.Name));
            return null;
        }

        private Hunt RunningHunt(Run a)
        {
            Hunt h;
            return a != null && a.Treasure != null && data.Hunts.TryGetValue(a.Treasure.HuntId ?? "", out h) ? h : null;
        }

        private float StepRadius(Step s)
        {
            return s.Radius >= 2f ? s.Radius : config.Treasure.StepRadius;
        }

        private void TickTreasure(Run a, DateTime now)
        {
            Hunt h = RunningHunt(a);
            if (h == null || h.Steps.Count == 0) { Finish(a, "lost"); return; }
            TreasureRun t = a.Treasure;
            foreach (Player p in OnlinePlayers())
            {
                if (data.Active != a) return;                  // the hoard was found this tick
                if (!Eligible(p)) continue;
                UnityEngine.Vector3 pos;
                if (!TryPos(p, out pos)) continue;
                string id = p.Id.ToString();
                int done;
                t.Progress.TryGetValue(id, out done);
                if (done >= h.Steps.Count) continue;
                Step s = h.Steps[done];
                float r = StepRadius(s);
                if (FlatDist(pos.x, pos.z, s.X, s.Z) > r || Math.Abs(pos.y - s.Y) > Math.Max(10f, r)) continue;
                DateTime last;
                if (done > 0 && t.LastStep.TryGetValue(id, out last) && (now - last).TotalSeconds < config.Treasure.MinSecondsBetweenSteps)
                {
                    if (!Throttled("toofast|" + id, 600))
                    {
                        PrintWarning(Clean(p.Name, 40) + " reached step " + (done + 1) + " of " + h.Id + " " + (int)(now - last).TotalSeconds + " s after the last one; not counted yet.");
                        StaffNote(Fmt("StaffTooFast", null, Clean(p.Name, 40), done + 1, h.Name));
                    }
                    continue;
                }
                t.Progress[id] = done + 1;
                t.LastStep[id] = now;
                dirty = true;
                if (done + 1 >= h.Steps.Count) { TreasureFound(a, h, p); return; }
                string riddle = h.Steps[done + 1].Riddle;
                Reply(p, "TreasureStep", done + 1, h.Steps.Count, riddle);
                if (PopupsFor(p)) ShowInfoPopup(p, Msg("PopupClueTitle", p), Fmt("PopupClue", p, done + 1, h.Steps.Count, riddle), Msg("PopupOk", p));
                if (done == 0) Quest(id, "treasure_step");
            }
        }

        private void TreasureFound(Run a, Hunt h, Player p)
        {
            string id = p.Id.ToString(), name = Clean(p.Name, 40);
            a.Treasure.WinnerId = id;
            a.Treasure.WinnerName = name;
            h.Found++;
            string house = HouseOf(p.Id);
            long marks = h.RewardMarks >= 0 ? h.RewardMarks : config.Treasure.RewardMarks;
            int points = h.SeasonPoints >= 0 ? h.SeasonPoints : config.Treasure.SeasonPoints;
            List<ItemReward> items = h.Items ?? config.Treasure.Items;
            long paid = PayMarks(id, name, marks, "world:treasure");
            string where = null;
            if (items.Count > 0)
            {
                bool chest = config.Treasure.UseChest && h.Chest != null && FillChest(h, p, items);
                if (chest) where = Msg("InTheChest", p);
                else { OweItems(id, items, h.Name); where = Msg("InYourPacks", p); PayOwed(p); }
            }
            Deed(id, name, "treasure_found", "found " + h.Name, "world:treasure:" + a.Key);
            if (house != null && points > 0) AwardHouse(house, points, name + " found " + h.Name);
            Quest(id, "treasure_hunt");
            data.Week.Treasures++;
            Herald(Fmt("TreasureFoundHerald", null, name, HouseSuffix(house), h.Name));
            Reply(p, "TreasureFoundYou", h.Name, MarksText(paid, marks), where ?? Msg("NoItems", p));
            if (PopupsFor(p)) ShowInfoPopup(p, Msg("PopupFoundTitle", p), Fmt("PopupFound", p, h.Name, MarksText(paid, marks), where ?? Msg("NoItems", p)), Msg("PopupOk", p));
            Chronicle(ChronicleEnd, name + " finds the hoard of " + h.Name, (house != null ? name + " of House " + house : name) + " followed every clue to the dig site.", new[] { name });
            AddHistory(Fmt("HistTreasureFound", null, h.Name, name));
            Finish(a, "won");
        }

        private void FinishTreasure(Run a, string outcome)
        {
            if (outcome == "won") return;
            Hunt h = RunningHunt(a);
            string name = h != null ? h.Name : "?";
            int best = 0;
            foreach (int v in a.Treasure.Progress.Values) if (v > best) best = v;
            Herald(Fmt("TreasureUnfound", null, name, best, h != null ? h.Steps.Count : 0));
            Chronicle(ChronicleEnd, "The hoard of " + name + " stays hidden", "No one reached the dig site in time.", new string[0]);
            AddHistory(Fmt("HistTreasureUnfound", null, name));
        }

        // A direction and a distance band to the player's next place, once per place, after HintAfterMinutes.
        private void TreasureHint(Player player)
        {
            Run a = data.Active;
            Hunt h = a != null && a.Kind == KTreasure ? RunningHunt(a) : null;
            if (h == null) { ReplyError(player, "NoTreasure"); return; }
            DateTime open = a.Start.AddMinutes(config.Treasure.HintAfterMinutes);
            if (Now() < open) { ReplyError(player, "HintLater", SpanText(player, (open - Now()).TotalSeconds)); return; }
            string id = player.Id.ToString();
            int done, hinted;
            a.Treasure.Progress.TryGetValue(id, out done);
            if (done >= h.Steps.Count) { ReplyError(player, "NoTreasure"); return; }
            if (a.Treasure.Hinted.TryGetValue(id, out hinted) && hinted == done + 1) { ReplyError(player, "HintUsed"); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            Step s = h.Steps[done];
            float d = FlatDist(pos.x, pos.z, s.X, s.Z);
            a.Treasure.Hinted[id] = done + 1;
            dirty = true;
            Reply(player, "Hint", done + 1, DirText(player, pos.x, pos.z, s.X, s.Z), BandText(player, d));
        }

        private string BandText(Player player, float metres)
        {
            if (metres < 100f) return Msg("Band.near", player);
            if (metres < 300f) return Msg("Band.close", player);
            if (metres < 700f) return Msg("Band.mid", player);
            if (metres < 1500f) return Msg("Band.far", player);
            return Msg("Band.distant", player);
        }

        private void ShowTreasure(Player player)
        {
            Run a = data.Active;
            Hunt h = a != null && a.Kind == KTreasure ? RunningHunt(a) : null;
            if (h == null)
            {
                Upcoming u = NextOf(KTreasure);
                Reply(player, "TreasureNone", u != null ? WhenText(player, u.At) : Msg("NotScheduled", player));
                return;
            }
            string id = player.Id.ToString();
            int done;
            a.Treasure.Progress.TryGetValue(id, out done);
            Reply(player, "TreasureStatus", h.Name, done, h.Steps.Count, SpanText(player, (a.End - Now()).TotalSeconds));
            if (done < h.Steps.Count) Reply(player, "TreasureRiddle", done + 1, h.Steps[done].Riddle);
            if (!Eligible(player)) Reply(player, "StaffWinNothing");
            Reply(player, "TreasureHelp");
        }

        // The finder's chest: the container staff bound to the dig site, filled with the prize and opened to the
        // finder alone for ChestLockMinutes. Returns false (the packs take the prize) if it cannot be found or nothing fits.
        private bool FillChest(Hunt h, Player winner, List<ItemReward> items)
        {
            Entity chest = FindChest(h.Chest);
            if (chest == null) { PrintWarning("Hunt " + h.Id + ": the bound chest was not found; the prize goes to the finder's packs."); return false; }
            ItemCollection contents = null;
            try
            {
                InteractableContainer c = chest.TryGet<InteractableContainer>();
                contents = c != null ? c.Contents : null;
            }
            catch (Exception ex) { PrintWarning("Hunt " + h.Id + ": the chest could not be opened (" + ex.Message + ")."); }
            if (contents == null) return false;
            var rest = new List<ItemReward>();
            int placed = 0;
            foreach (ItemReward r in items)
            {
                InvItemBlueprint bp = Blueprint(r.Item);
                int given = bp != null ? AddMeasured(contents, bp, r.Amount, "chest") : 0;
                placed += given;
                if (given < r.Amount) rest.Add(new ItemReward { Item = r.Item, Amount = r.Amount - given });
            }
            if (placed == 0) return false;
            if (rest.Count > 0) { OweItems(winner.Id.ToString(), rest, h.Name); PayOwed(winner); }
            UnityEngine.Vector3 cp;
            bool hasPos = TryEntityPos(chest, out cp);
            data.Lock = new ChestLock
            {
                X = hasPos ? cp.x : h.Chest.X, Y = hasPos ? cp.y : h.Chest.Y, Z = hasPos ? cp.z : h.Chest.Z,
                ViewId = ViewIdOf(chest), WinnerId = winner.Id.ToString(), Until = Now().AddMinutes(config.Treasure.ChestLockMinutes)
            };
            Ask(RealmSentinel, "SentinelItemSource", winner.Id, 120f);
            dirty = true;
            return true;
        }

        // The bound chest: by view id when it is still where it was bound, else the nearest container within 1.5 m.
        private Entity FindChest(ChestRef c)
        {
            if (c == null) return null;
            try
            {
                if (c.ViewId != 0)
                {
                    Entity e = Entity.TryGetFromViewID(c.ViewId);
                    UnityEngine.Vector3 p;
                    if (e != null && e.TryGet<InteractableContainer>() != null && TryEntityPos(e, out p) && Dist(p.x, p.y, p.z, c.X, c.Y, c.Z) <= 2f) return e;
                }
                return NearestContainer(c.X, c.Y, c.Z, 1.5f);
            }
            catch (Exception ex)
            {
                PrintWarning("Chest lookup failed: " + ex.Message);
                return null;
            }
        }

        private Entity NearestContainer(float x, float y, float z, float radius)
        {
            List<Entity> all = Entity.TryGetAll();
            if (all == null) return null;
            Entity best = null;
            float bestD = radius;
            foreach (Entity e in all)
            {
                if (e == null || e.IsPlayer) continue;
                UnityEngine.Vector3 p;
                if (!TryEntityPos(e, out p)) continue;
                float d = Dist(p.x, p.y, p.z, x, y, z);
                if (d > bestD) continue;
                if (e.TryGet<InteractableContainer>() == null) continue;
                best = e;
                bestD = d;
            }
            return best;
        }

        // The game's interact hook [OPJ L1036]: while the finder's chest is locked, nobody else may open it.
        private object OnPlayerInteract(InteractEvent evt)
        {
            ChestLock l = data != null ? data.Lock : null;
            if (l == null || evt == null || evt.Cancelled || evt.Entity == null) return null;
            if (Now() >= l.Until) return null;
            UnityEngine.Vector3 p;
            bool same = (l.ViewId != 0 && ViewIdOf(evt.Entity) == l.ViewId) || (TryEntityPos(evt.Entity, out p) && Dist(p.x, p.y, p.z, l.X, l.Y, l.Z) <= 1f);
            if (!same) return null;
            Player who = null;
            try { who = evt.ControllerEntity != null ? evt.ControllerEntity.Owner : null; }
            catch (Exception) { who = null; }
            if (who == null) who = evt.Sender;
            if (who == null || who.IsServer || who.Id.ToString() == l.WinnerId || IsAdmin(who)) return null;
            evt.Cancel("This chest belongs to the treasure's finder for a while yet.");
            if (!Throttled("lock|" + who.Id, 5)) ReplyError(who, "ChestLocked", SpanText(who, (l.Until - Now()).TotalSeconds));
            return true;
        }

        #endregion

        #region Blood Moon

        private void BeginBlood(Run a)
        {
            a.Blood = new BloodRun();
            int minutes = (int)Math.Round((a.End - a.Start).TotalMinutes);
            Herald(Fmt("BloodBegin", null, minutes));
            Chronicle(ChronicleStart, "The Blood Moon rises", "For one night the realm counts its dead, and the beasts are hungry.", new string[0]);
            AddHistory(Msg("HistBloodBegin", null));
            Puts("The Blood Moon has begun. Its look is the Mods preset blood-moon: server\\Set-Mood.ps1 -Mood blood-moon before a restart (moods load at start-up).");
        }

        private bool IsBloodNight()
        {
            Run a = data != null ? data.Active : null;
            return a != null && a.Kind == KBlood && a.Blood != null && Now() < a.End;
        }

        // The game's clock: true unless it can be read and says it is not night.
        private bool GameNight()
        {
            try
            {
                GameClock c = GameClock.Instance;
                if (c == null) return true;
                return c.CurrentTimeBlock == GameClock.TimeBlock.Night;
            }
            catch (Exception) { return true; }
        }

        private void BloodKill(Run a, Player killer, Player victim)
        {
            BloodRun b = a.Blood;
            string kid = killer.Id.ToString(), vid = victim.Id.ToString();
            string why = FairKill(killer, victim);
            if (why == null && config.BloodMoon.NightOnly && !GameNight()) why = "day";
            string pair = kid + "|" + vid;
            int fed, kills;
            b.Fed.TryGetValue(vid, out fed);
            b.Kills.TryGetValue(kid, out kills);
            if (why == null && b.Pairs.Contains(pair)) why = "again";
            if (why == null && fed >= config.BloodMoon.MaxDeathsFedPerVictim) why = "fed";
            if (why != null) { Puts("Blood Moon kill " + Clean(killer.Name, 40) + " -> " + Clean(victim.Name, 40) + " not counted (" + why + ")."); return; }
            b.Pairs.Add(pair);
            b.Fed[vid] = fed + 1;
            b.Kills[kid] = kills + 1;
            b.Names[kid] = Clean(killer.Name, 40);
            string house = HouseOf(killer.Id);
            if (house != null)
            {
                int hk;
                b.HouseKills.TryGetValue(house, out hk);
                b.HouseKills[house] = hk + 1;
                List<string> victims;
                if (!b.HouseVictims.TryGetValue(house, out victims)) { victims = new List<string>(); b.HouseVictims[house] = victims; }
                if (!victims.Contains(vid)) victims.Add(vid);
            }
            data.Week.BloodKills++;
            dirty = true;
            if (kills < config.BloodMoon.KillDeedsPerPlayer)
                Deed(kid, Clean(killer.Name, 40), "blood_moon_kill", "slew " + Clean(victim.Name, 40) + " under the Blood Moon", "world:blood:" + a.Key + ":" + kid + ":" + vid);
            Quest(kid, "blood_moon");
            Reply(killer, "BloodKillYou", Clean(victim.Name, 40), kills + 1);
        }

        // Why a player kill is not a fair one to reward (null = fair): both online and different people, not housemates
        // or allies (liege, vassal, treaty), the victim not under new-player protection, no truce, the killer may win.
        private string FairKill(Player killer, Player victim)
        {
            if (killer == null || victim == null || killer.Id == victim.Id) return "self";
            if (!Eligible(killer)) return "staff";
            if (OnlineById(victim.Id) == null || OnlineById(killer.Id) == null) return "offline";
            if (AskBool(RealmWarden, "IsNewPlayerProtected", victim.Id)) return "protected";
            if (AskBool(RealmEvents, "IsTruceActive")) return "truce";
            if (InDuel(killer.Id.ToString()) || InDuel(victim.Id.ToString())) return "duel";
            string kh = HouseOf(killer.Id), vh = HouseOf(victim.Id);
            if (kh != null && vh != null && (SameText(kh, vh) || Allied(kh, vh))) return "allies";
            return null;
        }

        private void FinishBlood(Run a)
        {
            BloodRun b = a.Blood;
            int total = 0;
            foreach (int v in b.Kills.Values) total += v;
            string topHouse = null;
            int topHouseKills = 0;
            foreach (KeyValuePair<string, int> kv in b.HouseKills)
            {
                List<string> victims;
                int distinct = b.HouseVictims.TryGetValue(kv.Key, out victims) ? victims.Count : 0;
                if (kv.Value < config.BloodMoon.HouseMinKills || distinct < 2) continue;
                if (kv.Value > topHouseKills || (kv.Value == topHouseKills && string.CompareOrdinal(kv.Key, topHouse) < 0)) { topHouse = kv.Key; topHouseKills = kv.Value; }
            }
            string topId = null;
            int topKills = 0;
            foreach (KeyValuePair<string, int> kv in b.Kills)
                if (kv.Value > topKills || (kv.Value == topKills && string.CompareOrdinal(kv.Key, topId) < 0)) { topId = kv.Key; topKills = kv.Value; }
            if (topHouse != null && config.BloodMoon.HousePoints > 0) AwardHouse(topHouse, config.BloodMoon.HousePoints, "Deadliest house of the Blood Moon");
            string topName = topId != null && b.Names.ContainsKey(topId) ? b.Names[topId] : null;
            if (topId != null && topKills >= 2 && config.BloodMoon.TopSlayerMarks > 0) PayMarks(topId, topName, config.BloodMoon.TopSlayerMarks, "world:bloodmoon");
            if (total == 0) Herald(Msg("BloodEndQuiet", null));
            else Herald(Fmt("BloodEnd", null, total, topName != null && topKills >= 2 ? Fmt("BloodTopSlayer", null, topName, topKills) : "",
                topHouse != null ? Fmt("BloodTopHouse", null, HouseTint(topHouse), topHouseKills) : ""));
            string detail = total == 0 ? "No blood was shed that the realm could count." : total + " counted kills."
                + (topName != null && topKills >= 2 ? " The deadliest: " + topName + " (" + topKills + ")." : "")
                + (topHouse != null ? " House " + topHouse + " took the night." : "");
            Chronicle(ChronicleEnd, "The Blood Moon sets", detail, topName != null ? new[] { topName } : new string[0]);
            AddHistory(Fmt("HistBloodEnd", null, total));
        }

        #endregion

        #region Caravan

        private Route PickRoute(string target, bool advance)
        {
            if (!string.IsNullOrEmpty(target))
            {
                Route r = FindRouteIn(config.Caravan.Routes, target);
                return r != null && r.Enabled ? r : null;
            }
            var ready = new List<Route>();
            foreach (Route r in config.Caravan.Routes) if (r.Enabled) ready.Add(r);
            int i = NextTurn(KCaravan, ready.Count, advance);
            return i < 0 ? null : ready[i];
        }

        private string RouteText(Route r)
        {
            string a, b;
            float x, y, z, rad;
            a = ResolvePlace(r.From, out x, out y, out z, out rad) ?? r.From;
            b = ResolvePlace(r.To, out x, out y, out z, out rad) ?? r.To;
            return a + " - " + b;
        }

        // A place a route or legend names: "waystone:<id>" (RealmTravel's file, read only) or a RealmWorld place id.
        // Returns the place's name, or null when it is not known.
        private string ResolvePlace(string spec, out float x, out float y, out float z, out float radius)
        {
            x = y = z = 0f;
            radius = 20f;
            if (string.IsNullOrEmpty(spec)) return null;
            string s = spec.Trim().ToLowerInvariant();
            if (s.StartsWith("waystone:"))
            {
                TravelStone w;
                Dictionary<string, TravelStone> stones = TravelStones();
                if (stones == null || !stones.TryGetValue(s.Substring(9), out w) || w == null || !w.Enabled) return null;
                x = w.X; y = w.Y; z = w.Z;
                radius = w.Radius >= 2f ? w.Radius : 10f;
                return Clean(string.IsNullOrEmpty(w.Name) ? Pretty(s.Substring(9)) : w.Name, 40);
            }
            Place p;
            if (!data.Places.TryGetValue(s, out p)) return null;
            x = p.X; y = p.Y; z = p.Z; radius = p.Radius;
            return p.Name;
        }

        // RealmTravel's waystones, read from its data file at most once a minute (never written).
        private Dictionary<string, TravelStone> TravelStones()
        {
            if (travelStones != null && Now() < travelReadAt.AddSeconds(60)) return travelStones;
            travelReadAt = Now();
            travelStones = new Dictionary<string, TravelStone>();
            try
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(TravelFile)) return travelStones;
                TravelView v = Interface.Oxide.DataFileSystem.ReadObject<TravelView>(TravelFile);
                if (v != null && v.Waystones != null)
                    foreach (KeyValuePair<string, TravelStone> kv in v.Waystones) if (kv.Value != null && kv.Key != null) travelStones[kv.Key.ToLowerInvariant()] = kv.Value;
            }
            catch (Exception ex)
            {
                if (!Throttled("travelfile", 600)) PrintWarning("Could not read oxide/data/RealmTravel.json for waystones: " + ex.Message);
            }
            return travelStones;
        }

        private string BeginCaravan(Run a, string target)
        {
            Route r = PickRoute(target, true);
            if (r == null) return Msg("NoRouteReady", null);
            float fx, fy, fz, fr, tx, ty, tz, tr;
            string from = ResolvePlace(r.From, out fx, out fy, out fz, out fr);
            string to = ResolvePlace(r.To, out tx, out ty, out tz, out tr);
            if (from == null || to == null) return Fmt("RouteUnknown", null, r.Id, from == null ? r.From : r.To);
            int total = (int)Math.Round((a.End - a.Start).TotalMinutes);
            int muster = Math.Min(config.Caravan.MusterMinutes, Math.Max(1, total / 3));
            a.Caravan = new CaravanRun
            {
                RouteId = r.Id, FromName = from, ToName = to, FX = fx, FY = fy, FZ = fz, TX = tx, TY = ty, TZ = tz,
                Depart = a.Start.AddMinutes(muster)
            };
            Herald(Fmt("CaravanMuster", null, from, to, muster, (int)Math.Round(FlatDist(fx, fz, tx, tz))));
            Chronicle(ChronicleStart, "A merchant caravan musters at " + from, "Bound for " + to + ". It needs a bearer and swords to guard it.", new string[0]);
            AddHistory(Fmt("HistCaravanBegin", null, from, to));
            return null;
        }

        private CaravanRun RunningCaravan()
        {
            Run a = data != null ? data.Active : null;
            return a != null && a.Kind == KCaravan ? a.Caravan : null;
        }

        private void CaravanCarry(Player player)
        {
            CaravanRun c = RunningCaravan();
            if (c == null) { ReplyError(player, "NoCaravan"); return; }
            if (c.Phase != "muster") { ReplyError(player, "CaravanGone"); return; }
            if (c.BearerId != null) { ReplyError(player, "CaravanHasBearer", c.BearerName); return; }
            if (!Eligible(player)) { ReplyError(player, "StaffWinNothing"); return; }
            string id = player.Id.ToString();
            if (data.Bounties.ContainsKey(id)) { ReplyError(player, "CaravanRaiderNo"); return; }
            if (AskBool(RealmWarden, "IsNewPlayerProtected", player.Id)) { ReplyError(player, "CaravanProtected"); return; }
            if (InDuel(id)) { ReplyError(player, "CaravanInDuel"); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            if (FlatDist(pos.x, pos.z, c.FX, c.FZ) > config.Caravan.StartRadius) { ReplyError(player, "CaravanTooFar", c.FromName, (int)config.Caravan.StartRadius); return; }
            c.Escorts.Remove(id);
            c.BearerId = id;
            c.BearerName = Clean(player.Name, 40);
            c.BearerHouse = HouseOf(player.Id);
            dirty = true;
            Herald(Fmt("CaravanBearer", null, c.BearerName, HouseSuffix(c.BearerHouse), c.ToName));
            Reply(player, "CaravanYouBear", c.ToName, SpanText(player, (c.Depart - Now()).TotalSeconds));
        }

        private void CaravanEscort(Player player)
        {
            CaravanRun c = RunningCaravan();
            if (c == null) { ReplyError(player, "NoCaravan"); return; }
            string id = player.Id.ToString();
            if (id == c.BearerId) { ReplyError(player, "CaravanYouAreBearer"); return; }
            if (c.Escorts.ContainsKey(id)) { Reply(player, "CaravanAlreadyEscort"); return; }
            if (!Eligible(player)) { ReplyError(player, "StaffWinNothing"); return; }
            if (data.Bounties.ContainsKey(id)) { ReplyError(player, "CaravanRaiderNo"); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            bool nearStart = c.Phase == "muster" && FlatDist(pos.x, pos.z, c.FX, c.FZ) <= config.Caravan.StartRadius * 2;
            bool nearBearer = false;
            Player bearer = c.BearerId != null ? OnlineById(ParseId(c.BearerId)) : null;
            UnityEngine.Vector3 bp;
            if (bearer != null && TryPos(bearer, out bp)) nearBearer = FlatDist(pos.x, pos.z, bp.x, bp.z) <= config.Caravan.EscortRadius;
            if (!nearStart && !nearBearer) { ReplyError(player, "CaravanEscortFar", c.FromName); return; }
            // Full: an escort who is not with the caravan right now (offline, or away from the start or the bearer)
            // gives up their place, so idle names cannot hold every place against real guards.
            if (c.Escorts.Count >= config.Caravan.MaxEscorts && !DropIdleEscort(c, bearer)) { ReplyError(player, "CaravanFull", config.Caravan.MaxEscorts); return; }
            c.Escorts[id] = new Escort { Name = Clean(player.Name, 40) };
            dirty = true;
            Reply(player, "CaravanYouEscort", c.ToName, config.Caravan.EscortMinPercent);
            if (bearer != null) Reply(bearer, "CaravanEscortJoined", Clean(player.Name, 40));
        }

        private bool DropIdleEscort(CaravanRun c, Player bearer)
        {
            UnityEngine.Vector3 bp = new UnityEngine.Vector3();
            bool hasBp = bearer != null && TryPos(bearer, out bp);
            foreach (KeyValuePair<string, Escort> kv in c.Escorts)
            {
                Player e = OnlineById(ParseId(kv.Key));
                UnityEngine.Vector3 ep;
                bool here = e != null && TryPos(e, out ep)
                    && ((c.Phase == "muster" && FlatDist(ep.x, ep.z, c.FX, c.FZ) <= config.Caravan.StartRadius * 2)
                        || (hasBp && FlatDist(ep.x, ep.z, bp.x, bp.z) <= config.Caravan.EscortRadius));
                if (here) continue;
                c.Escorts.Remove(kv.Key);
                if (e != null) Reply(e, "CaravanDropped");
                dirty = true;
                return true;
            }
            return false;
        }

        private void CaravanLeave(Player player)
        {
            CaravanRun c = RunningCaravan();
            if (c == null) { ReplyError(player, "NoCaravan"); return; }
            string id = player.Id.ToString();
            if (c.Escorts.Remove(id)) { dirty = true; Reply(player, "CaravanLeft"); return; }
            if (id == c.BearerId && c.Phase == "muster")
            {
                c.BearerId = null; c.BearerName = null; c.BearerHouse = null;
                dirty = true;
                Herald(Fmt("CaravanBearerLeft", null, c.FromName));
                return;
            }
            if (id == c.BearerId) { ReplyError(player, "CaravanBearerStays"); return; }
            ReplyError(player, "CaravanNotWith");
        }

        private void TickCaravan(Run a, DateTime now)
        {
            CaravanRun c = a.Caravan;
            if (c.Phase == "muster")
            {
                if (c.BearerId != null && InDuel(c.BearerId))
                {
                    c.BearerId = null; c.BearerName = null; c.BearerHouse = null;
                    dirty = true;
                    Herald(Fmt("CaravanBearerLeft", null, c.FromName));
                }
                if (now < c.Depart) return;
                if (c.BearerId == null) { Finish(a, "nobearer"); return; }
                Player b0 = OnlineById(ParseId(c.BearerId));
                if (b0 == null) { Finish(a, "nobearer"); return; }
                c.Phase = "road";
                UnityEngine.Vector3 p0;
                c.HasLast = TryPos(b0, out p0);              // the jump watch starts where the bearer stands at departure
                if (c.HasLast) { c.LX = p0.x; c.LY = p0.y; c.LZ = p0.z; }
                c.NextSighting = now.AddMinutes(config.Caravan.SightingMinutes);
                dirty = true;
                Herald(Fmt("CaravanDeparts", null, c.BearerName, c.FromName, c.ToName, (int)Math.Ceiling((a.End - now).TotalMinutes), c.Escorts.Count));
                return;
            }
            Player bearer = OnlineById(ParseId(c.BearerId));
            if (bearer == null)
            {
                if (c.OfflineSince == DateTime.MinValue) { c.OfflineSince = now; dirty = true; }
                if ((now - c.OfflineSince).TotalSeconds >= config.Caravan.OfflineGraceSeconds) Finish(a, "deserted");
                return;
            }
            c.OfflineSince = DateTime.MinValue;
            if (InDuel(c.BearerId)) { Finish(a, "duel"); return; }      // a duel (and its ring) is no road
            if (RealmTravel != null && AskBool(RealmTravel, "IsTravelling", c.BearerId))
            {
                Ask(RealmTravel, "CancelJourney", c.BearerId);
                Reply(bearer, "CaravanNoWaystone");
            }
            UnityEngine.Vector3 pos;
            if (!TryPos(bearer, out pos)) return;
            if (c.HasLast && FlatDist(pos.x, pos.z, c.LX, c.LZ) > config.Caravan.MaxJumpMetres) { Finish(a, "jumped"); return; }
            c.LX = pos.x; c.LY = pos.y; c.LZ = pos.z; c.HasLast = true;
            c.Samples++;
            foreach (KeyValuePair<string, Escort> kv in c.Escorts)
            {
                Player e = OnlineById(ParseId(kv.Key));
                UnityEngine.Vector3 ep;
                if (e != null && TryPos(e, out ep) && FlatDist(ep.x, ep.z, pos.x, pos.z) <= config.Caravan.EscortRadius && !InDuel(kv.Key)) kv.Value.Near++;
            }
            dirty = true;
            if (FlatDist(pos.x, pos.z, c.TX, c.TZ) <= config.Caravan.ArriveRadius) { CaravanArrives(a, bearer); return; }
            if (config.Caravan.SightingMinutes > 0 && now >= c.NextSighting)
            {
                c.NextSighting = now.AddMinutes(config.Caravan.SightingMinutes);
                Herald(Fmt("CaravanSighted", null, DistText(null, FlatDist(pos.x, pos.z, c.TX, c.TZ)), DirText(null, c.TX, c.TZ, pos.x, pos.z), c.ToName,
                    (int)Math.Ceiling((a.End - now).TotalMinutes)));
            }
        }

        private void CaravanArrives(Run a, Player bearer)
        {
            CaravanRun c = a.Caravan;
            long purse = config.Caravan.PurseMarks;
            long bearerShare = purse * config.Caravan.BearerSharePercent / 100;
            var paidEscorts = new List<KeyValuePair<string, Escort>>();
            UnityEngine.Vector3 bp;
            bool hasBp = TryPos(bearer, out bp);
            foreach (KeyValuePair<string, Escort> kv in c.Escorts)
            {
                if (c.Samples <= 0 || kv.Value.Near * 100 < c.Samples * config.Caravan.EscortMinPercent) continue;
                Player e = OnlineById(ParseId(kv.Key));
                UnityEngine.Vector3 ep;
                if (e == null || !TryPos(e, out ep) || (hasBp && FlatDist(ep.x, ep.z, bp.x, bp.z) > config.Caravan.EscortRadius * 2)) continue;
                paidEscorts.Add(kv);
            }
            long escortPool = purse - bearerShare;
            long each = paidEscorts.Count > 0 ? escortPool / paidEscorts.Count : 0;
            long got = PayMarks(c.BearerId, c.BearerName, bearerShare, "world:caravan");
            Deed(c.BearerId, c.BearerName, "caravan_escort", "brought the caravan to " + c.ToName, "world:caravan:" + a.Key + ":" + c.BearerId);
            Quest(c.BearerId, "caravan");
            Reply(bearer, "CaravanPaidYou", c.ToName, MarksText(got, bearerShare));
            var names = new List<string> { c.BearerName };
            foreach (KeyValuePair<string, Escort> kv in paidEscorts)
            {
                long g = PayMarks(kv.Key, kv.Value.Name, each, "world:caravan");
                Deed(kv.Key, kv.Value.Name, "caravan_escort", "guarded the caravan to " + c.ToName, "world:caravan:" + a.Key + ":" + kv.Key);
                Quest(kv.Key, "caravan");
                Player e = OnlineById(ParseId(kv.Key));
                if (e != null) Reply(e, "CaravanPaidYou", c.ToName, MarksText(g, each));
                names.Add(kv.Value.Name);
            }
            foreach (KeyValuePair<string, Escort> kv in c.Escorts)
            {
                if (paidEscorts.Exists(delegate(KeyValuePair<string, Escort> x) { return x.Key == kv.Key; })) continue;
                Player e = OnlineById(ParseId(kv.Key));
                if (e != null) Reply(e, "CaravanNotClose", config.Caravan.EscortMinPercent);
            }
            if (c.BearerHouse != null && config.Caravan.SeasonPoints > 0) AwardHouse(c.BearerHouse, config.Caravan.SeasonPoints, "Brought the merchant caravan to " + c.ToName);
            data.Week.CaravansHome++;
            Herald(Fmt("CaravanArrived", null, c.ToName, c.BearerName, HouseSuffix(c.BearerHouse), paidEscorts.Count));
            Chronicle(ChronicleEnd, "The caravan reaches " + c.ToName, "Borne from " + c.FromName + " by " + c.BearerName + (paidEscorts.Count > 0 ? ", with " + paidEscorts.Count + " sword(s) at its side." : ", alone."), names.ToArray());
            AddHistory(Fmt("HistCaravanHome", null, c.ToName, c.BearerName));
            Finish(a, "arrived");
        }

        // The bearer fell. A fair killer plunders the caravan and a raider's bounty goes on their head; anyone else's
        // blow (a beast, a fall, a housemate or ally) only loses it.
        private void CaravanBearerDied(Run a, Player killer)
        {
            CaravanRun c = a.Caravan;
            Player bearer = OnlineById(ParseId(c.BearerId));
            string why = killer != null && bearer != null ? FairKill(killer, bearer) : "none";
            if (killer != null && why == null)
            {
                string kid = killer.Id.ToString(), kname = Clean(killer.Name, 40);
                c.Escorts.Remove(kid);
                long share = config.Caravan.PurseMarks * config.Caravan.RaidSharePercent / 100;
                long got = PayMarks(kid, kname, share, "world:caravan-raid");
                Deed(kid, kname, "caravan_raid", "plundered the merchant caravan", "world:raid:" + a.Key);
                string house = HouseOf(killer.Id);
                bool bounty = config.Caravan.RaiderBountyMarks > 0 && config.Caravan.RaiderBountyHours > 0;
                if (bounty)
                {
                    data.Bounties[kid] = new Bounty { Name = kname, House = house, Marks = config.Caravan.RaiderBountyMarks, Until = Now().AddHours(config.Caravan.RaiderBountyHours), BearerId = c.BearerId };
                    dirty = true;
                }
                data.Week.CaravansPlundered++;
                Reply(killer, "CaravanYouPlunder", MarksText(got, share));
                Herald(Fmt("CaravanPlundered", null, kname, HouseSuffix(house), c.BearerName,
                    bounty ? Fmt("CaravanBountySet", null, config.Caravan.RaiderBountyMarks, config.Caravan.RaiderBountyHours) : ""));
                Chronicle(ChronicleEnd, kname + " plunders the merchant caravan", "The bearer " + c.BearerName + " fell on the road from " + c.FromName + " to " + c.ToName + "."
                    + (bounty ? " The merchants set a price of " + config.Caravan.RaiderBountyMarks + " marks on the raider's head." : ""), new[] { kname, c.BearerName });
                AddHistory(Fmt("HistCaravanRaided", null, kname));
                Finish(a, "plundered");
                return;
            }
            Finish(a, killer != null ? "betrayed" : "fell");
        }

        private void FinishCaravan(Run a, string outcome)
        {
            if (outcome == "arrived" || outcome == "plundered") return;
            CaravanRun c = a.Caravan;
            string key = outcome == "nobearer" ? "CaravanNoBearer" : outcome == "deserted" ? "CaravanDeserted" : outcome == "jumped" ? "CaravanJumped"
                : outcome == "duel" ? "CaravanDuelled" : outcome == "fell" ? "CaravanFell" : outcome == "betrayed" ? "CaravanBetrayed" : outcome == "stopped" ? "CaravanStopped" : "CaravanLate";
            if (outcome != "nobearer") data.Week.CaravansLost++;
            Herald(Fmt(key, null, c.ToName, c.BearerName ?? "?", c.FromName));
            if (outcome != "nobearer" && outcome != "stopped")
                Chronicle(ChronicleEnd, "The caravan to " + c.ToName + " is lost", PlainText(Fmt(key, null, c.ToName, c.BearerName ?? "?", c.FromName)), new string[0]);
            AddHistory(Fmt("HistCaravanLost", null, c.ToName));
        }

        // A raider's bounty: paid once to a fair killer of the raider who is not the plundered bearer.
        private void BountyKill(Player killer, Player victim)
        {
            string vid = victim.Id.ToString();
            Bounty b;
            if (!data.Bounties.TryGetValue(vid, out b) || Now() >= b.Until) return;
            if (killer.Id.ToString() == b.BearerId) { Reply(killer, "BountyNotYours"); return; }
            string why = FairKill(killer, victim);
            if (why != null) return;
            data.Bounties.Remove(vid);
            dirty = true;
            string kname = Clean(killer.Name, 40);
            long got = PayMarks(killer.Id.ToString(), kname, b.Marks, "world:raider-bounty");
            Herald(Fmt("BountyClaimed", null, kname, b.Name));
            Reply(killer, "BountyPaidYou", b.Name, MarksText(got, b.Marks));
            Chronicle(ChronicleEnd, kname + " collects the merchants' price on " + b.Name, "The raider of the caravan is brought down.", new[] { kname, b.Name });
            AddHistory(Fmt("HistBounty", null, b.Name, kname));
        }

        private void TickBounties(DateTime now)
        {
            if (data.Bounties.Count == 0) return;
            foreach (KeyValuePair<string, Bounty> kv in new List<KeyValuePair<string, Bounty>>(data.Bounties))
            {
                if (now < kv.Value.Until) continue;
                data.Bounties.Remove(kv.Key);
                dirty = true;
                Herald(Fmt("BountyLapsed", null, kv.Value.Name));
            }
        }

        private void ShowCaravan(Player player)
        {
            CaravanRun c = RunningCaravan();
            if (c == null)
            {
                Upcoming u = NextOf(KCaravan);
                Reply(player, "CaravanNone", u != null ? WhenText(player, u.At) : Msg("NotScheduled", player));
            }
            else if (c.Phase == "muster")
                Reply(player, "CaravanStatusMuster", c.FromName, c.ToName, c.BearerName ?? Msg("Nobody", player), c.Escorts.Count, SpanText(player, (c.Depart - Now()).TotalSeconds));
            else
                Reply(player, "CaravanStatusRoad", c.FromName, c.ToName, c.BearerName, c.Escorts.Count, SpanText(player, (data.Active.End - Now()).TotalSeconds));
            if (data.Bounties.Count > 0)
                foreach (Bounty b in data.Bounties.Values) Reply(player, "BountyLine", b.Name, b.Marks, SpanText(player, (b.Until - Now()).TotalSeconds));
            Reply(player, "CaravanHelp");
        }

        #endregion

        #region Legend

        private LegendDef PickLegend(string target, bool advance)
        {
            if (!string.IsNullOrEmpty(target))
            {
                LegendDef d = FindLegendIn(config.Legends.Legends, target);
                return d != null && d.Enabled ? d : null;
            }
            var ready = new List<LegendDef>();
            foreach (LegendDef d in config.Legends.Legends) if (d.Enabled) ready.Add(d);
            int i = NextTurn(KLegend, ready.Count, advance);
            return i < 0 ? null : ready[i];
        }

        private LegendDef RunningLegendDef(Run a)
        {
            return a != null && a.Legend != null ? FindLegendIn(config.Legends.Legends, a.Legend.LegendId) : null;
        }

        private string BeginLegend(Run a, string target)
        {
            LegendDef d = PickLegend(target, true);
            if (d == null) return Msg("NoLegendReady", null);
            a.Legend = new LegendRun { LegendId = d.Id, NextHint = Now().AddMinutes(config.Legends.HintMinutes) };
            float rx, ry, rz, rr;
            if (d.Region.Length > 0 && ResolvePlace(d.Region, out rx, out ry, out rz, out rr) == null)
                return Fmt("RegionUnknown", null, d.Id, d.Region);
            Rebind(a, true);
            Herald(Fmt("LegendBegin", null, d.Name, d.Lore, WhereText(a, d)));
            Chronicle(ChronicleStart, Cap(d.Name) + " roams Ostreval", d.Lore.Length > 0 ? d.Lore : "A Wandering Legend is abroad.", new string[0]);
            AddHistory(Fmt("HistLegendBegin", null, d.Name));
            return null;
        }

        // Binds (or binds again) a living creature of the legend's kind; with none to be found the hunt is for the first
        // creature of that kind slain in the region. first: choose freshly (a reload keeps the view id it had).
        private void Rebind(Run a, bool first)
        {
            LegendDef d = RunningLegendDef(a);
            if (d == null) return;
            LegendRun l = a.Legend;
            legendEntity = null;
            float cx = 0, cy = 0, cz = 0, cr = 0;
            bool region = d.Region.Length > 0 && ResolvePlace(d.Region, out cx, out cy, out cz, out cr) != null;
            if (!first && l.Mode == "bound" && l.ViewId != 0)
            {
                Entity e = null;
                try { e = Entity.TryGetFromViewID(l.ViewId); }
                catch (Exception) { e = null; }
                if (e != null && IsCreature(e) && KindMatches(d.Kinds, CreatureLabel(e))) { legendEntity = e; return; }
            }
            Entity pick = null;
            List<Entity> all = null;
            try { all = Entity.TryGetAll<MonsterEntity>(); }
            catch (Exception ex) { PrintWarning("Creature list unavailable: " + ex.Message); }
            if (all != null)
            {
                var hits = new List<Entity>();
                foreach (Entity e in all)
                {
                    if (e == null || e.IsPlayer || !KindMatches(d.Kinds, CreatureLabel(e))) continue;
                    UnityEngine.Vector3 p;
                    if (!TryEntityPos(e, out p)) continue;
                    if (region && FlatDist(p.x, p.z, cx, cz) > d.RegionRadius) continue;
                    hits.Add(e);
                }
                if (hits.Count > 0) pick = hits[(int)(((uint)Now().Ticks / 7919u) % (uint)hits.Count)];
            }
            if (pick != null)
            {
                legendEntity = pick;
                l.Mode = "bound";
                l.ViewId = ViewIdOf(pick);
                UnityEngine.Vector3 p;
                if (TryEntityPos(pick, out p)) { l.X = p.x; l.Y = p.y; l.Z = p.z; l.HasPos = true; }
            }
            else
            {
                l.Mode = "region";
                l.ViewId = 0;
                l.HasPos = region;
                if (region) { l.X = cx; l.Y = cy; l.Z = cz; }
                if (first) PrintWarning("No living " + string.Join("/", d.Kinds.ToArray()) + " found for " + d.Name + "; the first one slain" + (region ? " in " + d.Region : "") + " counts.");
            }
            dirty = true;
        }

        private string WhereText(Run a, LegendDef d)
        {
            LegendRun l = a.Legend;
            if (!l.HasPos) return Fmt("LegendAnywhere", null, string.Join("/", d.Kinds.ToArray()));
            string near = NearestPlaceName(l.X, l.Z);
            if (l.Mode == "region") return Fmt("LegendRegion", null, (int)d.RegionRadius, near ?? d.Region, string.Join("/", d.Kinds.ToArray()));
            if (near == null) return Fmt("LegendSeenAt", null, PosText(l.X, l.Y, l.Z));
            float px, py, pz, pr;
            ResolveNearest(l.X, l.Z, out px, out py, out pz, out pr);
            return Fmt("LegendSeenNear", null, DistText(null, FlatDist(l.X, l.Z, px, pz)), DirText(null, px, pz, l.X, l.Z), near);
        }

        private void TickLegend(Run a, DateTime now)
        {
            LegendDef d = RunningLegendDef(a);
            if (d == null) { Finish(a, "stopped"); return; }
            LegendRun l = a.Legend;
            if (l.Mode == "bound")
            {
                bool alive = false;
                try { alive = legendEntity != null && TryEntityPos(legendEntity, out tmpPos); }
                catch (Exception) { alive = false; }
                if (!alive) Rebind(a, false);
                if (legendEntity != null && TryEntityPos(legendEntity, out tmpPos)) { l.X = tmpPos.x; l.Y = tmpPos.y; l.Z = tmpPos.z; l.HasPos = true; }
            }
            if (now >= l.NextHint)
            {
                l.NextHint = now.AddMinutes(config.Legends.HintMinutes);
                dirty = true;
                Herald(Fmt("LegendHint", null, d.Name, WhereText(a, d), (int)Math.Ceiling((a.End - now).TotalMinutes)));
            }
        }

        private UnityEngine.Vector3 tmpPos;

        private bool IsLegend(Run a, Entity e)
        {
            if (a == null || a.Kind != KLegend || e == null || e.IsPlayer) return false;
            LegendRun l = a.Legend;
            if (l.Mode == "bound") return (legendEntity != null && ReferenceEquals(e, legendEntity)) || (l.ViewId != 0 && ViewIdOf(e) == l.ViewId);
            LegendDef d = RunningLegendDef(a);
            if (d == null || !IsCreature(e) || !KindMatches(d.Kinds, CreatureLabel(e))) return false;
            if (!l.HasPos) return true;
            UnityEngine.Vector3 p;
            return TryEntityPos(e, out p) && FlatDist(p.x, p.z, l.X, l.Z) <= d.RegionRadius;
        }

        private void LegendHit(Run a, Player attacker, EntityDamageEvent evt)
        {
            LegendDef d = RunningLegendDef(a);
            if (d == null) return;
            float amount = evt.Damage.Amount;
            if (amount <= 0) return;
            string id = attacker.Id.ToString();
            float had;
            a.Legend.Damage.TryGetValue(id, out had);
            a.Legend.Damage[id] = had + Math.Min(amount, 1000f);
            a.Legend.Names[id] = Clean(attacker.Name, 40);
            if (a.Legend.Mode == "bound") evt.Damage.Amount = amount / d.Toughness;
            dirty = true;
        }

        private void LegendSlain(Run a, Player killer)
        {
            LegendDef d = RunningLegendDef(a);
            if (d == null) { Finish(a, "stopped"); return; }
            if (killer == null || !Eligible(killer))
            {
                Finish(a, killer == null ? "died" : "staff");
                return;
            }
            string kid = killer.Id.ToString(), kname = Clean(killer.Name, 40);
            float total = 0;
            foreach (float v in a.Legend.Damage.Values) total += v;
            var helpers = new List<string>();
            if (total > 0)
                foreach (KeyValuePair<string, float> kv in a.Legend.Damage)
                    if (kv.Key != kid && kv.Value * 100f >= total * config.Legends.HelperMinPercent && OnlineById(ParseId(kv.Key)) != null) helpers.Add(kv.Key);
            long helperPool = helpers.Count > 0 ? d.RewardMarks * config.Legends.HelperSharePercent / 100 : 0;
            long killerMarks = d.RewardMarks - helperPool;
            long got = PayMarks(kid, kname, killerMarks, "world:legend");
            if (d.Trophy.Count > 0) { OweItems(kid, d.Trophy, d.Name); PayOwed(killer); }
            Deed(kid, kname, "legend_slain", "slew " + d.Name, "world:legend:" + a.Key);
            string house = HouseOf(killer.Id);
            if (house != null && d.SeasonPoints > 0) AwardHouse(house, d.SeasonPoints, kname + " slew " + d.Name);
            Quest(kid, "wandering_legend");
            var names = new List<string> { kname };
            foreach (string h in helpers)
            {
                long each = helperPool / helpers.Count;
                string hn = a.Legend.Names.ContainsKey(h) ? a.Legend.Names[h] : h;
                long g = PayMarks(h, hn, each, "world:legend");
                Player hp = OnlineById(ParseId(h));
                if (hp != null) Reply(hp, "LegendHelperPaid", d.Name, MarksText(g, each));
                names.Add(hn);
            }
            data.Week.Legends++;
            Reply(killer, "LegendSlainYou", d.Name, MarksText(got, killerMarks));
            if (PopupsFor(killer)) ShowInfoPopup(killer, Msg("PopupLegendTitle", killer), Fmt("PopupLegend", killer, Cap(d.Name), MarksText(got, killerMarks)), Msg("PopupOk", killer));
            Herald(Fmt("LegendSlainHerald", null, kname, HouseSuffix(house), d.Name, helpers.Count));
            Chronicle(ChronicleEnd, kname + " slays " + d.Name, (house != null ? kname + " of House " + house : kname) + " brought down the Wandering Legend"
                + (helpers.Count > 0 ? " with " + helpers.Count + " at their side." : "."), names.ToArray());
            AddHistory(Fmt("HistLegendSlain", null, d.Name, kname));
            Finish(a, "slain");
        }

        private void FinishLegend(Run a, string outcome)
        {
            if (outcome == "slain") return;
            LegendDef d = RunningLegendDef(a);
            string name = d != null ? d.Name : "?";
            string key = outcome == "died" ? "LegendDied" : outcome == "staff" ? "LegendStaff" : outcome == "stopped" ? "LegendStopped" : "LegendEscaped";
            Herald(Fmt(key, null, Cap(name)));
            if (outcome != "stopped") Chronicle(ChronicleEnd, Cap(name) + " escapes the hunters", PlainText(Fmt(key, null, Cap(name))), new string[0]);
            AddHistory(Fmt("HistLegendGone", null, name));
        }

        private void ShowLegend(Player player)
        {
            Run a = data.Active;
            LegendDef d = a != null && a.Kind == KLegend ? RunningLegendDef(a) : null;
            if (d == null)
            {
                Upcoming u = NextOf(KLegend);
                Reply(player, "LegendNone", u != null ? WhenText(player, u.At) : Msg("NotScheduled", player));
                return;
            }
            Reply(player, "LegendStatus", Cap(d.Name), d.Lore, WhereText(a, d), SpanText(player, (a.End - Now()).TotalSeconds));
        }

        // A creature, as the game's own SalvageSupplier tells one [CODE]: a MonsterMotor or a MonsterEntity attribute.
        private static bool IsCreature(Entity e)
        {
            try { return e != null && !e.IsPlayer && (e.TryGet<MonsterMotor>() != null || e.TryGet<MonsterEntity>() != null); }
            catch (Exception) { return false; }
        }

        // The creature's kind from Entity.ToString() ("name (Type)"), cleaned, lower case (as RealmQuests reads it).
        private static string CreatureLabel(Entity e)
        {
            string s;
            try { s = e.ToString(); }
            catch (Exception) { s = null; }
            if (string.IsNullOrEmpty(s)) return "creature";
            int paren = s.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) s = s.Substring(0, paren);
            s = s.Replace("(Clone)", "").Replace("_", " ").Trim();
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c) || c == ' ' || c == '-') sb.Append(char.ToLowerInvariant(c));
            string t = sb.ToString().Trim();
            if (t.Length > 40) t = t.Substring(0, 40);
            return t.Length == 0 ? "creature" : t;
        }

        private static bool KindMatches(List<string> kinds, string label)
        {
            if (kinds == null || string.IsNullOrEmpty(label)) return false;
            foreach (string k in kinds) if (k.Length > 0 && label.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        #endregion

        #region Festivals

        private FestivalDef RunningFestivalDef()
        {
            return data.Festival != null ? FindFestivalIn(config.Festivals.Festivals, data.Festival.Id) : null;
        }

        // The festival's window that holds `now` or begins next, in the year `year`.
        private static bool FestivalWindow(FestivalDef f, int year, out DateTime start, out DateTime end)
        {
            start = end = DateTime.MinValue;
            TimeSpan at;
            if (!TryParseHourMinute(f.StartUtc, out at)) return false;
            int day = Math.Min(f.Day, DateTime.DaysInMonth(year, f.Month));
            start = new DateTime(year, f.Month, day, 0, 0, 0, DateTimeKind.Utc) + at;
            end = start.AddDays(f.Days);
            return true;
        }

        private void TickFestival(DateTime now)
        {
            if (!config.Festivals.Enabled) return;
            FestivalRun r = data.Festival;
            if (r != null)
            {
                if (now < r.End) return;
                DateTime busyUntil;
                if (Busy(now, out busyUntil) && (now - r.End).TotalMinutes < config.Festivals.MaxCloseDelayMinutes) return;   // closes when the realm is quiet
                CloseFestival(false);
                return;
            }
            foreach (FestivalDef f in config.Festivals.Festivals)
            {
                if (!f.Enabled) continue;
                for (int y = now.Year - 1; y <= now.Year; y++)
                {
                    DateTime s, e;
                    if (!FestivalWindow(f, y, out s, out e) || now < s || now >= e) continue;
                    string key = f.Id + "@" + y;
                    if (data.Fired.ContainsKey(key)) continue;
                    data.Fired[key] = now;               // pruned after 20 days: the window has closed by then
                    OpenFestival(f, key, now, e, false);
                    return;
                }
            }
        }

        private void OpenFestival(FestivalDef f, string key, DateTime now, DateTime end, bool manual)
        {
            var r = new FestivalRun { Id = f.Id, Name = f.Name, Key = key, Start = now, End = end };
            data.Festival = r;
            dirty = true;
            int raised = RaiseDecorations(f.Id, r);
            Herald(Fmt("FestivalBegin", null, Cap(f.Name), DaysText(null, end - now)));
            Herald(Fmt("FestivalHow", null, OfferingsText(f)));
            Chronicle(ChronicleStart, Cap(f.Name) + " begins", "The houses of Ostreval compete in offerings" + (f.HuntPoints > 0 ? " and in the hunt" : "") + " until " + end.ToString("d MMMM", CultureInfo.InvariantCulture) + ".", new string[0]);
            AddHistory(Fmt("HistFestivalBegin", null, f.Name));
            if (f.Mood.Length > 0) Puts(f.Name + " has begun. Its look is the Mods preset " + f.Mood + ": server\\Set-Mood.ps1 -Mood " + f.Mood + " before a restart (moods load at start-up).");
            if (raised > 0) Puts(f.Name + ": " + raised + " decoration(s) are being raised by RealmSculptor.");
            SaveData();
            RefreshBoards();
        }

        private int RaiseDecorations(string festival, FestivalRun r)
        {
            List<Deco> decos;
            if (!config.Festivals.Decorations || !data.Decorations.TryGetValue(festival, out decos) || decos.Count == 0) return 0;
            if (RealmSculptor == null) { PrintWarning("RealmSculptor is not loaded; the decorations of " + festival + " are not raised."); return 0; }
            int n = 0;
            foreach (Deco d in decos)
            {
                object res = Ask(RealmSculptor, "PlaceSculptureAt", d.Sculpture, d.X, d.Y, d.Z, d.Turn, "RealmWorld");
                int id = res is int ? (int)res : 0;
                d.Placement = id > 0 ? id : 0;
                if (id > 0) { r.Placements.Add(id); n++; }
                else PrintWarning("RealmSculptor did not raise " + d.Sculpture + " at " + PosText(d.X, d.Y, d.Z) + " (occupied ground, unclaimed land, a missing sculpture or the placement limit; see /sculpt status).");
            }
            dirty = true;
            return n;
        }

        private int LowerDecorations(FestivalRun r)
        {
            if (r.Placements.Count == 0) return 0;
            int n = 0;
            foreach (int id in r.Placements) if (AskBool(RealmSculptor, "RemoveSculpture", id)) n++;
            if (n < r.Placements.Count) PrintWarning((r.Placements.Count - n) + " decoration(s) of " + r.Name + " could not be taken down; remove them with /sculpt remove <n> (" + JoinInts(r.Placements) + ").");
            List<Deco> decos;
            if (data.Decorations.TryGetValue(r.Id, out decos)) foreach (Deco d in decos) d.Placement = 0;
            r.Placements.Clear();
            return n;
        }

        private void CloseFestival(bool stopped)
        {
            FestivalRun r = data.Festival;
            if (r == null) return;
            FestivalDef f = RunningFestivalDef();
            data.Festival = null;
            dirty = true;
            LowerDecorations(r);
            if (stopped)
            {
                Herald(Fmt("FestivalStopped", null, Cap(r.Name)));
                AddHistory(Fmt("HistFestivalEnd", null, r.Name, "-"));
                SaveData();
                RefreshBoards();
                return;
            }
            List<KeyValuePair<string, int>> ranked = RankHouses(r, f);
            var lines = new List<string>();
            for (int i = 0; i < ranked.Count; i++)
            {
                int pts = f != null && i < f.PlacePoints.Count ? f.PlacePoints[i] : 0;
                if (pts > 0) AwardHouse(ranked[i].Key, pts, Cap(r.Name) + ": place " + (i + 1));
                if (i < 3) lines.Add(Fmt("FestivalPlace", null, i + 1, HouseTint(ranked[i].Key), ranked[i].Value));
            }
            string winner = ranked.Count > 0 ? ranked[0].Key : null;
            string champId = null;
            int champPts = 0;
            if (winner != null)
                foreach (KeyValuePair<string, int> kv in r.PlayerPoints)
                {
                    int bar = kv.Key.IndexOf('|');
                    if (bar <= 0 || !SameText(kv.Key.Substring(bar + 1), winner)) continue;     // points given for the winning house only
                    string pid = kv.Key.Substring(0, bar);
                    if (kv.Value > champPts || (kv.Value == champPts && string.CompareOrdinal(pid, champId) < 0)) { champId = pid; champPts = kv.Value; }
                }
            string champName = champId != null && r.Names.ContainsKey(champId) ? r.Names[champId] : null;
            if (champId != null)
            {
                if (f != null && f.TopGiverMarks > 0) PayMarks(champId, champName, f.TopGiverMarks, "world:festival");
                Deed(champId, champName, "festival_champion", "led House " + winner + " at " + r.Name, "world:festival:" + r.Key);
            }
            if (winner == null) Herald(Fmt("FestivalEndNone", null, Cap(r.Name)));
            else
            {
                Herald(Fmt("FestivalEnd", null, Cap(r.Name), HouseTint(winner), champName ?? "?"));
                foreach (string l in lines) Herald(l);
            }
            Chronicle(ChronicleEnd, winner != null ? "House " + winner + " wins " + r.Name : Cap(r.Name) + " ends",
                winner != null ? "Best giver: " + (champName ?? "?") + " (" + champPts + " points)." : "No house gave enough to place.", champName != null ? new[] { champName } : new string[0]);
            AddHistory(Fmt("HistFestivalEnd", null, r.Name, winner ?? "-"));
            SaveData();
            RefreshBoards();
        }

        // Houses by festival points, best first; a house needs MinContributors givers to place.
        private List<KeyValuePair<string, int>> RankHouses(FestivalRun r, FestivalDef f)
        {
            var list = new List<KeyValuePair<string, int>>();
            int min = f != null ? f.MinContributors : 1;
            foreach (KeyValuePair<string, int> kv in r.HousePoints)
            {
                List<string> g;
                if (kv.Value <= 0 || !r.Givers.TryGetValue(kv.Key, out g) || g.Count < min) continue;
                list.Add(kv);
            }
            list.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        // Points for a player's house, within the player's daily cap; returns what was counted.
        private int FestivalCredit(FestivalRun r, FestivalDef f, Player p, string house, int points)
        {
            string id = p.Id.ToString();
            string dkey = Now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + id;
            int today;
            r.Daily.TryGetValue(dkey, out today);
            int n = Math.Min(points, f.DailyPlayerCap - today);
            if (n <= 0) return 0;
            r.Daily[dkey] = today + n;
            int hp, pp;
            r.HousePoints.TryGetValue(house, out hp);
            r.HousePoints[house] = hp + n;
            string pkey = id + "|" + house.Trim().ToLowerInvariant();
            r.PlayerPoints.TryGetValue(pkey, out pp);
            r.PlayerPoints[pkey] = pp + n;
            r.Names[id] = Clean(p.Name, 40);
            List<string> g;
            if (!r.Givers.TryGetValue(house, out g)) { g = new List<string>(); r.Givers[house] = g; }
            if (!g.Contains(id)) g.Add(id);
            dirty = true;
            return n;
        }

        private void FestivalGive(Player player, string what)
        {
            FestivalRun r = data.Festival;
            FestivalDef f = RunningFestivalDef();
            if (r == null || f == null) { ReplyError(player, "NoFestival"); return; }
            if (!Eligible(player)) { ReplyError(player, "StaffWinNothing"); return; }
            string house = HouseOf(player.Id);
            if (house == null) { ReplyError(player, "FestivalNoHouse"); return; }
            if (f.Offerings.Count == 0) { ReplyError(player, "FestivalNoOfferings"); return; }
            Container inv = null;
            try { inv = player.GetInventory(); }
            catch (Exception) { inv = null; }
            ItemCollection packs = inv != null ? inv.Contents : null;
            if (packs == null) { ReplyError(player, "NoPacks"); return; }
            bool all = string.IsNullOrEmpty(what) || what.Equals("all", StringComparison.OrdinalIgnoreCase);
            int counted = 0;
            bool any = false, matched = false;
            string dkey = Now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + player.Id;
            foreach (Offering o in f.Offerings)
            {
                InvItemBlueprint bp = Blueprint(o.Item);
                if (bp == null) continue;
                if (!all && !SameText(what, o.Item) && !SameText(what, bp.Name)) continue;
                matched = true;
                int today;
                r.Daily.TryGetValue(dkey, out today);
                int room = f.DailyPlayerCap - today;
                if (room <= 0) break;
                int carry = ItemCollection.AutoCount(packs, bp);
                int want = Math.Min(carry, room / o.Points);           // never take goods the cap would not count
                if (want <= 0) continue;
                int taken = TakeMeasured(packs, bp, want);
                if (taken <= 0) continue;
                any = true;
                int got = FestivalCredit(r, f, player, house, taken * o.Points);
                counted += got;
                data.Week.Offerings += taken;
                Reply(player, "FestivalGave", taken, bp.Name, got);
            }
            if (!matched && !all) { ReplyError(player, "FestivalNotOffering", Clean(what, 30), OfferingsText(f)); return; }
            int left;
            r.Daily.TryGetValue(dkey, out left);
            if (!any)
            {
                if (left >= f.DailyPlayerCap) ReplyError(player, "FestivalCapped", f.DailyPlayerCap);
                else ReplyError(player, "FestivalNothing", OfferingsText(f));
                return;
            }
            int hp;
            r.HousePoints.TryGetValue(house, out hp);
            Reply(player, "FestivalHouseNow", HouseTint(house), hp, counted);
            Quest(player.Id.ToString(), r.Id + "_festival");
            SaveData();
            RefreshBoards();
        }

        private void FestivalHunt(Player killer, Entity creature)
        {
            FestivalRun r = data.Festival;
            FestivalDef f = RunningFestivalDef();
            if (r == null || f == null || f.HuntPoints <= 0 || !Eligible(killer)) return;
            if (!KindMatches(f.HuntKinds, CreatureLabel(creature))) return;
            string house = HouseOf(killer.Id);
            if (house == null) return;
            int got = FestivalCredit(r, f, killer, house, f.HuntPoints);
            if (got > 0) Reply(killer, "FestivalHuntPoints", got, HouseTint(house));
        }

        private void ShowFestival(Player player)
        {
            FestivalRun r = data.Festival;
            FestivalDef f = RunningFestivalDef();
            if (r == null || f == null)
            {
                DateTime next = DateTime.MaxValue;
                string name = null;
                foreach (FestivalDef x in config.Festivals.Festivals)
                {
                    if (!x.Enabled) continue;
                    for (int y = Now().Year; y <= Now().Year + 1; y++)
                    {
                        DateTime s, e;
                        if (FestivalWindow(x, y, out s, out e) && s > Now() && s < next) { next = s; name = x.Name; }
                    }
                }
                Reply(player, "FestivalNone", name != null ? Cap(name) : "?", name != null ? next.ToString("d MMMM", CultureInfo.InvariantCulture) : "?");
                return;
            }
            Reply(player, "FestivalStatus", Cap(r.Name), DaysText(player, r.End - Now()));
            List<KeyValuePair<string, int>> ranked = RankHouses(r, f);
            if (ranked.Count == 0) Reply(player, "FestivalNoStandings");
            for (int i = 0; i < ranked.Count && i < 6; i++) Reply(player, "FestivalRow", i + 1, HouseTint(ranked[i].Key), ranked[i].Value);
            string house = HouseOf(player.Id);
            if (house != null)
            {
                int hp, mine, today;
                r.HousePoints.TryGetValue(house, out hp);
                r.PlayerPoints.TryGetValue(player.Id + "|" + house.Trim().ToLowerInvariant(), out mine);
                r.Daily.TryGetValue(Now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + player.Id, out today);
                Reply(player, "FestivalYours", HouseTint(house), hp, mine, Math.Max(0, f.DailyPlayerCap - today));
            }
            Reply(player, "FestivalOfferings", OfferingsText(f));
            if (f.HuntPoints > 0) Reply(player, "FestivalHuntLine", string.Join("/", f.HuntKinds.ToArray()), f.HuntPoints);
            Reply(player, "FestivalHelp");
        }

        private string OfferingsText(FestivalDef f)
        {
            var parts = new List<string>();
            foreach (Offering o in f.Offerings) parts.Add(o.Item + " " + o.Points);
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "-";
        }

        #endregion

        #region Census

        private void NoteSeen(Player p)
        {
            if (p == null || p.IsServer || data == null) return;
            string id = p.Id.ToString();
            if (weekSeen.Add(id)) { data.Week.Players.Add(id); dirty = true; }
            if (!data.Seen.Contains(id))
            {
                if (data.Seen.Count >= config.Census.MaxKnownPlayers) data.Seen.RemoveAt(0);
                data.Seen.Add(id);
                if (data.Week.Since != DateTime.MinValue) data.Week.Newcomers++;
                dirty = true;
            }
        }

        // The weekly census: counts only. Spoken by the Herald and written to the Chronicle as census_taken.
        private void RunCensus(Player admin)
        {
            CensusWeek w = data.Week;
            w.Peak = Math.Max(w.Peak, OnlinePlayers().Count);
            var lines = new List<string>();
            lines.Add(Fmt("CensusSouls", null, w.Players.Count, w.Newcomers, w.Peak));
            List<Dictionary<string, object>> houses = Ask(RealmHouses, "GetHouseSummaries") as List<Dictionary<string, object>>;
            if (houses != null)
            {
                string big = null;
                int bigN = 0;
                foreach (Dictionary<string, object> h in houses)
                {
                    int n = h.ContainsKey("members") && h["members"] is int ? (int)h["members"] : 0;
                    string name = h.ContainsKey("name") ? h["name"] as string : null;
                    if (name != null && (n > bigN || (n == bigN && string.CompareOrdinal(name, big) < 0))) { big = name; bigN = n; }
                }
                lines.Add(big != null ? Fmt("CensusHouses", null, houses.Count, HouseTint(Clean(big, 40)), bigN) : Fmt("CensusNoHouses", null, houses.Count));
            }
            string king = AskString(CrownAndConsequences, "GetKingName");
            if (CrownAndConsequences != null)
            {
                string kh = AskString(CrownAndConsequences, "GetKingHouse");
                lines.Add(!string.IsNullOrEmpty(king) ? Fmt("CensusCrown", null, Clean(king, 40), HouseSuffix(string.IsNullOrEmpty(kh) ? null : Clean(kh, 40))) : Msg("CensusNoCrown", null));
            }
            List<Dictionary<string, object>> standings = Ask(RealmSeasons, "GetSeasonStandings") as List<Dictionary<string, object>>;
            if (standings != null && standings.Count > 0 && standings[0].ContainsKey("house"))
                lines.Add(Fmt("CensusSeason", null, HouseTint(Clean(standings[0]["house"] as string, 40)), standings[0].ContainsKey("score") ? standings[0]["score"] : 0));
            string treasury = AskString(RealmTreasury, "GetTreasurySummary");
            if (!string.IsNullOrEmpty(treasury))
            {
                string[] t = treasury.Split('|');
                if (t.Length >= 2) lines.Add(Fmt("CensusTreasury", null, t[1], t[0]));
            }
            int lastId = LastChronicleId();
            if (lastId >= 0 && w.ChronicleId >= 0) lines.Add(Fmt("CensusChronicle", null, Math.Max(0, lastId - w.ChronicleId)));
            lines.Add(Fmt("CensusDeeds", null, w.Treasures, w.CaravansHome, w.CaravansPlundered, w.CaravansLost, w.Legends, w.BloodKills));
            if (w.Offerings > 0) lines.Add(Fmt("CensusOfferings", null, w.Offerings));
            Herald(Fmt("CensusHeader", null, Now().ToString("d MMMM", CultureInfo.InvariantCulture)));
            foreach (string l in lines) Server.BroadcastMessage("  " + l);
            var detail = new StringBuilder();
            foreach (string l in lines) { if (detail.Length > 0) detail.Append(' '); detail.Append(PlainText(l)); }
            Chronicle(CensusChronicleType, "The census of Ostreval, " + Now().ToString("d MMMM yyyy", CultureInfo.InvariantCulture), detail.ToString(), new string[0]);
            data.LastCensus = new CensusReport { At = Now(), Lines = lines };
            data.Week = new CensusWeek { Since = Now(), Peak = OnlinePlayers().Count, ChronicleId = lastId };
            weekSeen.Clear();
            foreach (Player p in OnlinePlayers()) { string id = p.Id.ToString(); if (weekSeen.Add(id)) data.Week.Players.Add(id); }
            dirty = true;
            AddHistory(Msg("HistCensus", null));
            SaveData();
            RefreshBoards();
            if (admin != null) Reply(admin, "AdminDone", "census");
        }

        private void ShowCensus(Player player)
        {
            CensusReport c = data.LastCensus;
            if (c == null) { Reply(player, "CensusNever", WhenOrNot(player, NextOf(KCensus))); return; }
            Reply(player, "CensusLast", c.At.ToString("d MMMM", CultureInfo.InvariantCulture));
            foreach (string l in c.Lines) player.SendMessage("  " + l);
            Reply(player, "CensusSoFar", data.Week.Players.Count, data.Week.Newcomers, data.Week.Peak);
        }

        private int LastChronicleId()
        {
            object r = Ask(RealmChronicle, "GetLastEventId");
            return r is int ? (int)r : -1;
        }

        #endregion

        #region Hooks

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            NoteSeen(player);
            ulong id = player.Id;
            timer.Once(5f, delegate { PayOwedOnJoin(id); });
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (loadFailed || data == null || player == null) return;
            dirty = true;
        }

        // Blows [OPJ L162]: the Wandering Legend's toughness and its damage ledger; the Blood Moon's hungrier beasts.
        // A blow another plugin already cancelled is left alone.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Cancelled || evt.Damage == null || evt.Entity == null) return null;
            Run a = data.Active;
            if (a == null || Now() >= a.End) return null;
            try
            {
                Entity src = evt.Damage.DamageSource;
                Player attacker = src != null && src.IsPlayer ? src.Owner : null;
                if (a.Kind == KLegend && attacker != null && !attacker.IsServer && !evt.Entity.IsPlayer && IsLegend(a, evt.Entity)) LegendHit(a, attacker, evt);
                else if (a.Kind == KBlood && evt.Entity.IsPlayer && src != null && !src.IsPlayer && IsCreature(src) && evt.Damage.Amount > 0)
                    evt.Damage.Amount = evt.Damage.Amount * config.BloodMoon.BeastDamageMultiplier;
            }
            catch (Exception ex)
            {
                if (!Throttled("dmg", 300)) PrintWarning("Damage hook: " + ex.Message);
            }
            return null;
        }

        // Deaths [OPJ L188]: victim evt.Entity, killer evt.KillingDamage.DamageSource.Owner [ASM; USE DeathMessages.cs:20].
        private void OnEntityDeath(EntityDeathEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Entity == null) return;
            try
            {
                Entity src = evt.KillingDamage != null ? evt.KillingDamage.DamageSource : null;
                Player killer = src != null && src.IsPlayer ? src.Owner : null;
                if (killer != null && killer.IsServer) killer = null;
                Run a = data.Active;
                bool live = a != null && Now() < a.End;
                if (evt.Entity.IsPlayer)
                {
                    Player victim = evt.Entity.Owner;
                    if (victim == null || victim.IsServer) return;
                    if (live && a.Kind == KCaravan && a.Caravan.BearerId == victim.Id.ToString())
                    {
                        if (a.Caravan.Phase == "road") CaravanBearerDied(a, killer);
                        else { a.Caravan.BearerId = null; a.Caravan.BearerName = null; a.Caravan.BearerHouse = null; dirty = true; Herald(Fmt("CaravanBearerLeft", null, a.Caravan.FromName)); }
                    }
                    if (killer != null && killer.Id != victim.Id)
                    {
                        if (live && a.Kind == KBlood) BloodKill(a, killer, victim);
                        BountyKill(killer, victim);
                    }
                    return;
                }
                if (live && a.Kind == KLegend && IsLegend(a, evt.Entity)) { LegendSlain(a, killer); return; }
                if (killer != null && data.Festival != null && IsCreature(evt.Entity)) FestivalHunt(killer, evt.Entity);
            }
            catch (Exception ex)
            {
                PrintWarning("Death hook: " + ex.Message);
            }
        }

        #endregion

        #region Rewards and items

        // Marks through RealmTreasury.RewardMarks (new marks under the treasury's own reward caps). What it does not pay
        // now stays owed here and is asked for again when the player next joins. Returns what was paid now.
        private long PayMarks(string playerId, string name, long amount, string source)
        {
            if (amount <= 0 || string.IsNullOrEmpty(playerId)) return 0;
            long paid = 0;
            object r = Ask(RealmTreasury, "RewardMarks", playerId, name ?? playerId, amount, source);
            if (r is long) paid = Math.Max(0, Math.Min(amount, (long)r));
            if (paid < amount)
            {
                long had;
                data.OwedMarks.TryGetValue(playerId, out had);
                data.OwedMarks[playerId] = Math.Min(1000000, had + amount - paid);
                dirty = true;
            }
            return paid;
        }

        private void OweItems(string playerId, List<ItemReward> items, string why)
        {
            List<OwedItem> list;
            if (!data.OwedItems.TryGetValue(playerId, out list)) { list = new List<OwedItem>(); data.OwedItems[playerId] = list; }
            foreach (ItemReward r in items)
            {
                OwedItem o = list.Find(delegate(OwedItem x) { return SameText(x.Item, r.Item); });
                if (o != null) o.Amount = Math.Min(100000, o.Amount + r.Amount);
                else list.Add(new OwedItem { Item = r.Item, Amount = r.Amount, Why = Clean(why, 40) });
            }
            dirty = true;
            SaveData();                                        // owed is on disk before anything is handed over
        }

        // Pays what the ledger owes, measured. Each line leaves the ledger and the file is saved BEFORE the give; what
        // did not fit goes back. A crash in between loses items, it never pays them twice.
        private string PayOwed(Player player)
        {
            if (player == null || player.Entity == null) return "";
            string id = player.Id.ToString();
            List<OwedItem> list;
            if (!data.OwedItems.TryGetValue(id, out list) || list.Count == 0) return "";
            var paying = new List<OwedItem>(list);
            data.OwedItems.Remove(id);
            dirty = true;
            SaveData();
            Ask(RealmSentinel, "SentinelItemSource", player.Id, 30f);
            var parts = new List<string>();
            var back = new List<OwedItem>();
            Container inv = null;
            try { inv = player.GetInventory(); }
            catch (Exception) { inv = null; }
            ItemCollection items = inv != null ? inv.Contents : null;
            foreach (OwedItem o in paying)
            {
                InvItemBlueprint bp = Blueprint(o.Item);
                int given = bp != null && items != null ? AddMeasured(items, bp, o.Amount, Clean(player.Name, 40)) : 0;
                if (given > 0) parts.Add(given + " " + bp.Name);
                if (given < o.Amount && bp != null) back.Add(new OwedItem { Item = o.Item, Amount = o.Amount - given, Why = o.Why });
                if (bp == null) PrintWarning("The item '" + o.Item + "' is not known to this server; it cannot be paid. Fix the name in oxide/config/RealmWorld.json.");
            }
            if (back.Count > 0) data.OwedItems[id] = back;
            dirty = true;
            SaveData();
            if (parts.Count > 0) Reply(player, "ItemsGiven", string.Join(", ", parts.ToArray()));
            if (back.Count > 0) Reply(player, "ItemsOwed");
            return string.Join(", ", parts.ToArray());
        }

        private void PayOwedOnJoin(ulong id)
        {
            if (loadFailed || data == null) return;
            Player p = OnlineById(id);
            if (p == null) return;
            string sid = id.ToString();
            long owed;
            if (data.OwedMarks.TryGetValue(sid, out owed) && owed > 0)
            {
                data.OwedMarks.Remove(sid);
                long got = PayMarks(sid, Clean(p.Name, 40), owed, "world:owed");
                if (got > 0) Reply(p, "MarksOwedPaid", got);
            }
            PayOwed(p);
        }

        private void CollectOwed(Player player)
        {
            string id = player.Id.ToString();
            bool any = data.OwedItems.ContainsKey(id) || data.OwedMarks.ContainsKey(id);
            if (!any) { Reply(player, "NothingOwed"); return; }
            PayOwedOnJoin(player.Id);
        }

        // Server-side grant, the same calls as the game's /give (ThronesCommandHandler.Give) [IL]: stacks capped at
        // ContainerManagement.StackLimit, ItemCollection.AutoMergeAdd; what was added is MEASURED with AutoCount.
        private int AddMeasured(ItemCollection items, InvItemBlueprint bp, int amount, string to)
        {
            try
            {
                if (items == null || bp == null || amount <= 0) return 0;
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
                PrintWarning("Giving " + amount + " " + bp.Name + " to " + to + " failed: " + ex.Message);
                return 0;
            }
        }

        // Takes up to n of an item from the packs and returns what really left them [ASM AutoCount/AutoSplit].
        private static int TakeMeasured(ItemCollection packs, InvItemBlueprint bp, int n)
        {
            int before = ItemCollection.AutoCount(packs, bp);
            try { ItemCollection.AutoSplit(packs, bp, Math.Min(n, before)); }
            catch (Exception) { }
            int taken = before - ItemCollection.AutoCount(packs, bp);
            return taken < 0 ? 0 : Math.Min(taken, n);
        }

        // ResourceType name first (the lookup ResourceTax.TaxResource itself uses [IL]), then an exact item name.
        private InvItemBlueprint Blueprint(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            InvItemBlueprint bp;
            if (blueprintCache.TryGetValue(name, out bp)) return bp;
            try
            {
                if (InvBlueprints.Instance == null) return null;
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

        private void Deed(string playerId, string name, string kind, string note, string dedupe)
        {
            if (RealmRenown == null) return;
            Ask(RealmRenown, "AddDeed", playerId, name ?? playerId, kind, note, dedupe);
        }

        private void AwardHouse(string house, int points, string honour)
        {
            if (RealmSeasons == null || string.IsNullOrEmpty(house) || points == 0) return;
            Ask(RealmSeasons, "AwardHouse", house, points, honour);
        }

        private void Quest(string playerId, string subject)
        {
            if (RealmQuests == null) return;
            Ask(RealmQuests, "ReportQuestEvent", playerId, "event", subject, 1);
        }

        private string MarksText(long paid, long asked)
        {
            if (asked <= 0) return Msg("NoMarks", null);
            if (paid >= asked) return Fmt("MarksPaid", null, paid);
            return Fmt("MarksPartly", null, paid, asked - paid);
        }

        #endregion

        #region Commands

        [ChatCommand("world")]
        private void CmdWorld(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "": case "status": ShowWorld(player); return;
                case "schedule": case "next": ShowSchedule(player, false); return;
                case "treasure": case "hunt": ShowTreasure(player); return;
                case "caravan": ShowCaravan(player); return;
                case "legend": ShowLegend(player); return;
                case "bloodmoon": case "blood": ShowBlood(player); return;
                case "festival": ShowFestival(player); return;
                case "census": ShowCensus(player); return;
                case "history": ShowHistory(player); return;
                case "collect": CollectOwed(player); return;
                case "admin": CmdAdmin(player, args); return;
                case "help": break;
                default: ReplyError(player, "UnknownSub", Clean(sub, 20)); break;
            }
            Reply(player, "Help1");
            Reply(player, "Help2");
            Reply(player, "Help3");
            if (IsAdmin(player)) Reply(player, "HelpAdmin");
        }

        [ChatCommand("treasure")]
        private void CmdTreasure(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "hint") { TreasureHint(player); return; }
            if (sub != "" && sub != "status") { ReplyError(player, "UnknownSub", Clean(sub, 20)); Reply(player, "TreasureHelp"); return; }
            ShowTreasure(player);
        }

        [ChatCommand("caravan")]
        private void CmdCaravan(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "carry": case "bear": CaravanCarry(player); return;
                case "escort": case "guard": CaravanEscort(player); return;
                case "leave": CaravanLeave(player); return;
                case "": case "status": ShowCaravan(player); return;
            }
            ReplyError(player, "UnknownSub", Clean(sub, 20));
            Reply(player, "CaravanHelp");
        }

        [ChatCommand("festival")]
        private void CmdFestival(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "give" || sub == "offer") { FestivalGive(player, args.Length > 1 ? JoinFrom(args, 1) : "all"); return; }
            if (sub != "" && sub != "status" && sub != "standings") { ReplyError(player, "UnknownSub", Clean(sub, 20)); Reply(player, "FestivalHelp"); return; }
            ShowFestival(player);
        }

        private void ShowWorld(Player player)
        {
            Run a = data.Active;
            if (!config.General.Enabled) Reply(player, "WorldOff");
            if (a != null) Reply(player, "WorldNow", ActiveTitle(a, player), SpanText(player, (a.End - Now()).TotalSeconds), "/" + CommandOf(a.Kind));
            else Reply(player, "WorldQuiet");
            if (data.Festival != null) Reply(player, "WorldFestival", Cap(data.Festival.Name), DaysText(player, data.Festival.End - Now()));
            if (IsBloodNight()) Reply(player, "BloodNow");
            ShowSchedule(player, true);
        }

        private string CommandOf(string kind)
        {
            if (kind == KTreasure) return "treasure";
            if (kind == KCaravan) return "caravan";
            return "world " + (kind == KBlood ? "bloodmoon" : kind);
        }

        private string ActiveTitle(Run a, Player player)
        {
            if (a.Kind == KTreasure) { Hunt h = RunningHunt(a); return Fmt("Title.treasure", player, h != null ? h.Name : "?"); }
            if (a.Kind == KCaravan) return Fmt("Title.caravan", player, a.Caravan.FromName, a.Caravan.ToName);
            if (a.Kind == KLegend) { LegendDef d = RunningLegendDef(a); return Fmt("Title.legend", player, d != null ? Cap(d.Name) : "?"); }
            return KindName(a.Kind, player);
        }

        private void ShowSchedule(Player player, bool brief)
        {
            List<Upcoming> next = NextOccurrences(Now(), 8, brief ? 4 : 10);
            if (next.Count == 0) { Reply(player, "ScheduleNone"); return; }
            Reply(player, "ScheduleHead");
            foreach (Upcoming u in next)
            {
                string why;
                DateTime retry;
                string note = config.General.AvoidRealmEvents && u.Slot.DurationMinutes > 0 && EventsClash(u.At, u.At.AddMinutes(u.Slot.DurationMinutes), out why, out retry)
                    ? Fmt("ScheduleClash", player, why) : "";
                player.SendMessage("  " + Fmt("ScheduleLine", player, WhenText(player, u.At), KindName(u.Slot.Event, player), CountdownDetailFor(u.Slot), note).TrimEnd());
            }
        }

        private string CountdownDetailFor(Slot s)
        {
            if (s.Event == KBlood || s.Event == KCensus) return "";
            string d = CountdownDetail(s);
            return d == "?" ? "" : "(" + d + ")";
        }

        private void ShowBlood(Player player)
        {
            Run a = data.Active;
            if (a == null || a.Kind != KBlood)
            {
                Reply(player, "BloodNone", WhenOrNot(player, NextOf(KBlood)));
                return;
            }
            int mine;
            a.Blood.Kills.TryGetValue(player.Id.ToString(), out mine);
            Reply(player, "BloodStatus", SpanText(player, (a.End - Now()).TotalSeconds), mine, config.BloodMoon.KillDeedsPerPlayer);
            var houses = new List<KeyValuePair<string, int>>(a.Blood.HouseKills);
            houses.Sort(delegate(KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
            for (int i = 0; i < houses.Count && i < 5; i++) Reply(player, "BloodRow", HouseTint(houses[i].Key), houses[i].Value);
        }

        private void ShowHistory(Player player)
        {
            if (data.History.Count == 0) { Reply(player, "HistoryNone"); return; }
            Reply(player, "HistoryHead");
            for (int i = data.History.Count - 1, n = 0; i >= 0 && n < 10; i--, n++) player.SendMessage("  [A3A6AD]" + data.History[i] + "[FFFFFF]");
        }

        private Upcoming NextOf(string kind)
        {
            foreach (Upcoming u in NextOccurrences(Now(), 15, 100)) if (u.Slot.Event == kind) return u;
            return null;
        }

        private string WhenOrNot(Player player, Upcoming u)
        {
            return u != null ? WhenText(player, u.At) : Msg("NotScheduled", player);
        }

        #endregion

        #region Admin

        private void CmdAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "status": AdminStatus(player); return;
                case "start": AdminStart(player, args); return;
                case "stop": AdminStop(player); return;
                case "schedule": ShowSchedule(player, false); return;
                case "place": AdminPlace(player, args); return;
                case "places": AdminPlaces(player); return;
                case "hunt": AdminHunt(player, args); return;
                case "route": AdminRoute(player, args); return;
                case "routes": AdminRoutes(player); return;
                case "deco": AdminDeco(player, args); return;
                case "festival": AdminFestival(player, args); return;
                case "census": RunCensus(player); return;
                case "creatures": AdminCreatures(player); return;
                case "legends": AdminLegends(player); return;
                case "bounty": AdminBounty(player, args); return;
            }
            Reply(player, "AdminHelp1");
            Reply(player, "AdminHelp2");
            Reply(player, "AdminHelp3");
        }

        private void AdminStatus(Player player)
        {
            Reply(player, "AdminStatus1", config.General.Enabled ? Msg("On", player) : Msg("Off", player), data.Active != null ? ActiveTitle(data.Active, player) : Msg("Nothing", player),
                data.Festival != null ? data.Festival.Name : Msg("Nothing", player));
            Reply(player, "AdminStatus2", data.Places.Count, data.Hunts.Count, config.Caravan.Routes.Count, config.Legends.Legends.Count, data.Bounties.Count, data.OwedItems.Count + data.OwedMarks.Count);
            var plugins = new List<string>();
            AddLoaded(plugins, "RealmEvents", RealmEvents);
            AddLoaded(plugins, "RealmTreasury", RealmTreasury);
            AddLoaded(plugins, "RealmRenown", RealmRenown);
            AddLoaded(plugins, "RealmSeasons", RealmSeasons);
            AddLoaded(plugins, "RealmChronicle", RealmChronicle);
            AddLoaded(plugins, "RealmSculptor", RealmSculptor);
            AddLoaded(plugins, "RealmPainter", RealmPainter);
            AddLoaded(plugins, "RealmTravel", RealmTravel);
            AddLoaded(plugins, "RealmQuests", RealmQuests);
            AddLoaded(plugins, "RealmArena", RealmArena);
            Reply(player, "AdminStatus3", string.Join(", ", plugins.ToArray()));
            foreach (KeyValuePair<string, DateTime> kv in data.Postponed) Reply(player, "AdminPostponed", kv.Key, ClockText(kv.Value));
        }

        private void AddLoaded(List<string> list, string name, Plugin p)
        {
            list.Add(name + " " + (p != null ? Msg("Loaded", null) : Msg("Absent", null)));
        }

        // /world admin start <kind> [target] [minutes] [force]
        private void AdminStart(Player player, string[] args)
        {
            string kind = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (kind == "blood") kind = KBlood;
            if (Array.IndexOf(Kinds, kind) < 0) { ReplyError(player, "AdminStartUsage"); return; }
            if (kind == KCensus) { RunCensus(player); return; }
            string target = "";
            int minutes = 0;
            bool force = false;
            for (int i = 3; i < args.Length; i++)
            {
                string t = args[i].ToLowerInvariant();
                int n;
                if (t == "force") force = true;
                else if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 5 && n <= 360) minutes = n;
                else target = NormalizeId(t) ?? "";
            }
            if (minutes == 0) minutes = kind == KBlood ? 120 : kind == KCaravan ? 45 : 60;
            DateTime now = Now();
            DateTime end = now.AddMinutes(minutes);
            string why;
            DateTime retry;
            if (!force && EventsClash(now, end, out why, out retry)) { ReplyError(player, "AdminClash", why); return; }
            string fail = Begin(kind, "manual", null, target, now, end, true);
            if (fail != null) { ReplyError(player, "AdminStartFailed", fail); return; }
            Reply(player, "AdminStarted", KindName(kind, player), minutes);
        }

        private void AdminStop(Player player)
        {
            if (data.Active == null) { ReplyError(player, "AdminNothingRunning"); return; }
            string name = KindName(data.Active.Kind, player);
            Finish(data.Active, "stopped");
            Reply(player, "AdminStopped", name);
        }

        // /world admin place set <id> [radius] [name...] | clear <id>
        private void AdminPlace(Player player, string[] args)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            string id = args.Length > 3 ? NormalizeId(args[3]) : null;
            if ((op != "set" && op != "clear") || id == null) { ReplyError(player, "AdminPlaceUsage"); return; }
            if (op == "clear")
            {
                if (!data.Places.Remove(id)) { ReplyError(player, "AdminNoPlace", id); return; }
                dirty = true;
                SaveData();
                Reply(player, "AdminDone", "place " + id + " cleared");
                return;
            }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            float radius = 20f;
            int nameAt = 4;
            float r;
            if (args.Length > 4 && float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) { radius = ClampF(r, 2f, 5000f); nameAt = 5; }
            string name = Clean(JoinFrom(args, nameAt), 40);
            Place p;
            if (!data.Places.TryGetValue(id, out p)) { p = new Place { Id = id }; data.Places[id] = p; }
            p.X = pos.x; p.Y = pos.y; p.Z = pos.z; p.Radius = radius;
            if (name.Length > 0) p.Name = name;
            if (string.IsNullOrEmpty(p.Name)) p.Name = Pretty(id);
            dirty = true;
            SaveData();
            Reply(player, "AdminPlaceSet", p.Name, id, PosText(p.X, p.Y, p.Z), (int)radius);
        }

        private void AdminPlaces(Player player)
        {
            Reply(player, "AdminPlacesHead", data.Places.Count);
            foreach (Place p in data.Places.Values) player.SendMessage("  " + p.Id + " '" + p.Name + "' " + PosText(p.X, p.Y, p.Z) + " r" + (int)p.Radius);
            Dictionary<string, TravelStone> stones = TravelStones();
            if (stones != null && stones.Count > 0)
            {
                var names = new List<string>();
                foreach (string k in stones.Keys) names.Add("waystone:" + k);
                names.Sort(StringComparer.Ordinal);
                Reply(player, "AdminWaystones", string.Join(", ", names.ToArray()));
            }
        }

        // /world admin hunt new <id> <name...> | step <id> <riddle...> | undo <id> | radius <id> <step> <m> | chest <id> [clear]
        //                   | reward <id> marks <n> | points <n> | item <name> <n> | items clear | enable <id> on|off | show <id> | list | remove <id> [confirm]
        private void AdminHunt(Player player, string[] args)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (op == "list" || op == "")
            {
                Reply(player, "AdminHuntsHead", data.Hunts.Count);
                foreach (Hunt x in data.Hunts.Values)
                    player.SendMessage("  " + x.Id + " '" + x.Name + "' " + x.Steps.Count + " place(s)" + (x.Chest != null ? ", chest" : "") + (x.Enabled ? "" : ", off") + ", run " + x.Runs + ", found " + x.Found);
                if (op == "") Reply(player, "AdminHuntUsage");
                return;
            }
            string id = args.Length > 3 ? NormalizeId(args[3]) : null;
            if (id == null) { ReplyError(player, "AdminHuntUsage"); return; }
            Hunt h;
            data.Hunts.TryGetValue(id, out h);
            if (op == "new")
            {
                if (h != null) { ReplyError(player, "AdminHuntExists", id); return; }
                string name = Clean(JoinFrom(args, 4), 40);
                h = new Hunt { Id = id, Name = name.Length > 0 ? name : Pretty(id) };
                data.Hunts[id] = h;
                dirty = true;
                SaveData();
                Reply(player, "AdminHuntNew", h.Name, id);
                return;
            }
            if (h == null) { ReplyError(player, "AdminNoHunt", id); return; }
            Run a = data.Active;
            bool running = a != null && a.Kind == KTreasure && a.Treasure.HuntId == id;
            if (running && op != "show") { ReplyError(player, "AdminHuntRunning", h.Name); return; }
            UnityEngine.Vector3 pos;
            switch (op)
            {
                case "step":
                {
                    string riddle = Clean(JoinFrom(args, 4), 180);
                    if (riddle.Length < 5) { ReplyError(player, "AdminRiddleShort"); return; }
                    if (h.Steps.Count >= 12) { ReplyError(player, "AdminTooManySteps", 12); return; }
                    if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
                    h.Steps.Add(new Step { X = pos.x, Y = pos.y, Z = pos.z, Riddle = riddle });
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminStepAdded", h.Steps.Count, h.Name, PosText(pos.x, pos.y, pos.z));
                    return;
                }
                case "undo":
                    if (h.Steps.Count == 0) { ReplyError(player, "AdminNoSteps"); return; }
                    h.Steps.RemoveAt(h.Steps.Count - 1);
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminDone", h.Id + ": " + h.Steps.Count + " place(s)");
                    return;
                case "radius":
                {
                    int n;
                    float r;
                    if (args.Length < 6 || !int.TryParse(args[4], out n) || n < 1 || n > h.Steps.Count || !float.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r < 2f || r > 50f)
                    { ReplyError(player, "AdminRadiusUsage"); return; }
                    h.Steps[n - 1].Radius = r;
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminDone", h.Id + " place " + n + " radius " + r.ToString("0.#", CultureInfo.InvariantCulture));
                    return;
                }
                case "chest":
                {
                    if (args.Length > 4 && args[4].ToLowerInvariant() == "clear") { h.Chest = null; dirty = true; SaveData(); Reply(player, "AdminDone", h.Id + " chest cleared"); return; }
                    if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
                    Entity chest = null;
                    try { chest = NearestContainer(pos.x, pos.y, pos.z, 5f); }
                    catch (Exception ex) { PrintWarning("Chest search failed: " + ex.Message); }
                    if (chest == null) { ReplyError(player, "AdminNoChest"); return; }
                    UnityEngine.Vector3 cp;
                    TryEntityPos(chest, out cp);
                    h.Chest = new ChestRef { X = cp.x, Y = cp.y, Z = cp.z, ViewId = ViewIdOf(chest) };
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminChestBound", h.Name, PosText(cp.x, cp.y, cp.z));
                    if (h.Steps.Count > 0)
                    {
                        Step last = h.Steps[h.Steps.Count - 1];
                        if (FlatDist(cp.x, cp.z, last.X, last.Z) > StepRadius(last) + 10f) Reply(player, "AdminChestFar");
                    }
                    return;
                }
                case "reward":
                {
                    string what = args.Length > 4 ? args[4].ToLowerInvariant() : "";
                    long n;
                    if (what == "marks" && args.Length > 5 && long.TryParse(args[5], out n) && n >= 0 && n <= 100000) h.RewardMarks = n;
                    else if (what == "points" && args.Length > 5 && long.TryParse(args[5], out n) && n >= 0 && n <= 500) h.SeasonPoints = (int)n;
                    else if (what == "item" && args.Length > 6 && long.TryParse(args[args.Length - 1], out n) && n > 0 && n <= 10000)
                    {
                        string item = Clean(JoinRange(args, 5, args.Length - 2), 40);
                        if (Blueprint(item) == null) { ReplyError(player, "UnknownItem", item); return; }
                        if (h.Items == null) h.Items = new List<ItemReward>();
                        h.Items.Add(new ItemReward { Item = item, Amount = (int)n });
                    }
                    else if (what == "items" && args.Length > 5 && args[5].ToLowerInvariant() == "clear") h.Items = new List<ItemReward>();
                    else if (what == "default") { h.RewardMarks = -1; h.SeasonPoints = -1; h.Items = null; }
                    else { ReplyError(player, "AdminRewardUsage"); return; }
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminDone", h.Id + " reward " + RewardText(h));
                    return;
                }
                case "enable":
                {
                    string v = args.Length > 4 ? args[4].ToLowerInvariant() : "";
                    if (v != "on" && v != "off") { ReplyError(player, "AdminHuntUsage"); return; }
                    h.Enabled = v == "on";
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminDone", h.Id + " " + v);
                    return;
                }
                case "show":
                    Reply(player, "AdminHuntShow", h.Name, h.Id, h.Steps.Count, RewardText(h), h.Chest != null ? PosText(h.Chest.X, h.Chest.Y, h.Chest.Z) : "-");
                    for (int i = 0; i < h.Steps.Count; i++)
                        player.SendMessage("  " + (i + 1) + ". " + PosText(h.Steps[i].X, h.Steps[i].Y, h.Steps[i].Z) + " r" + (int)StepRadius(h.Steps[i]) + ": " + h.Steps[i].Riddle);
                    return;
                case "remove":
                {
                    string key = player.Id + "|hunt|" + id;
                    DateTime asked;
                    bool confirm = args.Length > 4 && args[4].ToLowerInvariant() == "confirm";
                    if (!confirm || !removeAsk.TryGetValue(key, out asked) || (Now() - asked).TotalMinutes > 2)
                    {
                        removeAsk[key] = Now();
                        Reply(player, "AdminRemoveAsk", h.Name, "/world admin hunt remove " + id + " confirm");
                        return;
                    }
                    removeAsk.Remove(key);
                    data.Hunts.Remove(id);
                    dirty = true;
                    SaveData();
                    Reply(player, "AdminDone", id + " removed");
                    return;
                }
            }
            ReplyError(player, "AdminHuntUsage");
        }

        private string RewardText(Hunt h)
        {
            long marks = h.RewardMarks >= 0 ? h.RewardMarks : config.Treasure.RewardMarks;
            int pts = h.SeasonPoints >= 0 ? h.SeasonPoints : config.Treasure.SeasonPoints;
            var parts = new List<string>();
            foreach (ItemReward r in h.Items ?? config.Treasure.Items) parts.Add(r.Amount + " " + r.Item);
            return marks + " marks, " + pts + " points" + (parts.Count > 0 ? ", " + string.Join(", ", parts.ToArray()) : "");
        }

        // /world admin route add <id> <from> <to> [name...] | remove <id>
        private void AdminRoute(Player player, string[] args)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            string id = args.Length > 3 ? NormalizeId(args[3]) : null;
            if (id == null || (op != "add" && op != "remove")) { ReplyError(player, "AdminRouteUsage"); return; }
            Route r = FindRouteIn(config.Caravan.Routes, id);
            if (op == "remove")
            {
                if (r == null) { ReplyError(player, "AdminNoRoute", id); return; }
                config.Caravan.Routes.Remove(r);
                Config.WriteObject(config, true);
                Reply(player, "AdminDone", "route " + id + " removed");
                return;
            }
            if (args.Length < 6) { ReplyError(player, "AdminRouteUsage"); return; }
            string from = args[4].ToLowerInvariant(), to = args[5].ToLowerInvariant();
            float x, y, z, rad;
            if (ResolvePlace(from, out x, out y, out z, out rad) == null) { ReplyError(player, "AdminUnknownPlace", Clean(from, 40)); return; }
            if (ResolvePlace(to, out x, out y, out z, out rad) == null) { ReplyError(player, "AdminUnknownPlace", Clean(to, 40)); return; }
            if (from == to) { ReplyError(player, "AdminRouteSame"); return; }
            if (r == null) { r = new Route { Id = id }; config.Caravan.Routes.Add(r); }
            r.From = from; r.To = to; r.Name = Clean(JoinFrom(args, 6), 40); r.Enabled = true;
            Config.WriteObject(config, true);
            Reply(player, "AdminRouteAdded", id, RouteText(r));
        }

        private void AdminRoutes(Player player)
        {
            Reply(player, "AdminRoutesHead", config.Caravan.Routes.Count);
            foreach (Route r in config.Caravan.Routes) player.SendMessage("  " + r.Id + ": " + RouteText(r) + (r.Enabled ? "" : " (off)"));
        }

        // /world admin deco add <festival> <sculpture> | remove <festival> <n> | list <festival>
        private void AdminDeco(Player player, string[] args)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            string fid = args.Length > 3 ? NormalizeId(args[3]) : null;
            FestivalDef f = fid != null ? FindFestivalIn(config.Festivals.Festivals, fid) : null;
            if (f == null || (op != "add" && op != "remove" && op != "list")) { ReplyError(player, "AdminDecoUsage"); return; }
            List<Deco> list;
            if (!data.Decorations.TryGetValue(f.Id, out list)) { list = new List<Deco>(); data.Decorations[f.Id] = list; }
            if (op == "list")
            {
                Reply(player, "AdminDecoHead", Cap(f.Name), list.Count);
                for (int i = 0; i < list.Count; i++) player.SendMessage("  " + (i + 1) + ". " + list[i].Sculpture + " at " + PosText(list[i].X, list[i].Y, list[i].Z) + " turn " + list[i].Turn + (list[i].Placement > 0 ? " (standing: #" + list[i].Placement + ")" : ""));
                return;
            }
            if (op == "remove")
            {
                int n;
                if (args.Length < 5 || !int.TryParse(args[4], out n) || n < 1 || n > list.Count) { ReplyError(player, "AdminDecoUsage"); return; }
                if (list[n - 1].Placement > 0) { ReplyError(player, "AdminDecoStanding", list[n - 1].Placement); return; }
                list.RemoveAt(n - 1);
                dirty = true;
                SaveData();
                Reply(player, "AdminDone", f.Id + " decoration " + n + " removed");
                return;
            }
            string sculpture = args.Length > 4 ? NormalizeSculpture(args[4]) : null;
            if (sculpture == null) { ReplyError(player, "AdminDecoUsage"); return; }
            if (list.Count >= 20) { ReplyError(player, "AdminDecoMany", 20); return; }
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            int turn = 0;
            try { UnityEngine.Vector3 fw = player.Entity.Forward; turn = Quadrant(fw.x, fw.z); }
            catch (Exception) { turn = 0; }
            list.Add(new Deco { Sculpture = sculpture, X = pos.x, Y = pos.y, Z = pos.z, Turn = turn });
            dirty = true;
            SaveData();
            Reply(player, "AdminDecoAdded", sculpture, Cap(f.Name), list.Count);
        }

        // The direction a player faces as a quarter-turn (RealmSculptor's rule): 0 = +z, 1 = +x, 2 = -z, 3 = -x.
        private static int Quadrant(float fx, float fz)
        {
            if (Math.Abs(fx) >= Math.Abs(fz)) return fx >= 0 ? 1 : 3;
            return fz >= 0 ? 0 : 2;
        }

        // /world admin festival start <id> [days] | stop
        private void AdminFestival(Player player, string[] args)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (op == "stop")
            {
                if (data.Festival == null) { ReplyError(player, "NoFestival"); return; }
                CloseFestival(false);
                Reply(player, "AdminDone", "festival closed");
                return;
            }
            if (op == "cancel")
            {
                if (data.Festival == null) { ReplyError(player, "NoFestival"); return; }
                CloseFestival(true);
                Reply(player, "AdminDone", "festival cancelled");
                return;
            }
            string fid = args.Length > 3 ? NormalizeId(args[3]) : null;
            FestivalDef f = fid != null ? FindFestivalIn(config.Festivals.Festivals, fid) : null;
            if (op != "start" || f == null) { ReplyError(player, "AdminFestivalUsage"); return; }
            if (data.Festival != null) { ReplyError(player, "AdminFestivalRunning", data.Festival.Name); return; }
            int days = f.Days;
            int n;
            if (args.Length > 4 && int.TryParse(args[4], out n) && n >= 1 && n <= 30) days = n;
            DateTime now = Now();
            OpenFestival(f, f.Id + "@manual@" + Iso(now), now, now.AddDays(days), true);
            Reply(player, "AdminDone", f.Id + " opened for " + days + " day(s)");
        }

        // The creature kinds near the admin (to tune legend and festival Kinds), with counts.
        private void AdminCreatures(Player player)
        {
            UnityEngine.Vector3 pos;
            if (!TryPos(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            List<Entity> all = null;
            try { all = Entity.TryGetAll<MonsterEntity>(); }
            catch (Exception ex) { ReplyError(player, "AdminCreaturesFailed", Clean(ex.Message, 60)); return; }
            var counts = new Dictionary<string, int>();
            int total = 0;
            if (all != null)
                foreach (Entity e in all)
                {
                    UnityEngine.Vector3 p;
                    if (e == null || e.IsPlayer || !TryEntityPos(e, out p)) continue;
                    total++;
                    if (FlatDist(p.x, p.z, pos.x, pos.z) > 300f) continue;
                    string k = CreatureLabel(e);
                    int c;
                    counts.TryGetValue(k, out c);
                    counts[k] = c + 1;
                }
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts) parts.Add(kv.Key + " x" + kv.Value);
            parts.Sort(StringComparer.Ordinal);
            Reply(player, "AdminCreatures", total, parts.Count > 0 ? string.Join(", ", parts.ToArray()) : Msg("Nothing", player));
        }

        private void AdminLegends(Player player)
        {
            foreach (LegendDef d in config.Legends.Legends)
                player.SendMessage("  " + d.Id + " '" + d.Name + "' " + string.Join("/", d.Kinds.ToArray()) + " x" + d.Toughness.ToString("0.#", CultureInfo.InvariantCulture)
                    + (d.Region.Length > 0 ? " in " + d.Region + " r" + (int)d.RegionRadius : " anywhere") + (d.Enabled ? "" : " (off)"));
        }

        private void AdminBounty(Player player, string[] args)
        {
            string op = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (op == "clear" && args.Length > 3)
            {
                string name = JoinFrom(args, 3);
                foreach (KeyValuePair<string, Bounty> kv in data.Bounties)
                    if (SameText(kv.Value.Name, name) || kv.Key == name)
                    {
                        data.Bounties.Remove(kv.Key);
                        dirty = true;
                        SaveData();
                        Reply(player, "AdminDone", "bounty on " + kv.Value.Name + " lifted");
                        return;
                    }
                ReplyError(player, "AdminNoBounty", Clean(name, 40));
                return;
            }
            Reply(player, "AdminBountiesHead", data.Bounties.Count);
            foreach (Bounty b in data.Bounties.Values) Reply(player, "BountyLine", b.Name, b.Marks, SpanText(player, (b.Until - Now()).TotalSeconds));
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
            object wanted = Ask(RealmHerald, "PopupsWanted", player.Id.ToString());
            return !(wanted is bool) || (bool)wanted;
        }

        private bool ShowInfoPopup(Player player, string title, string message, string button)
        {
            try
            {
                player.ShowPopup(PlainText(title), PlainText(message), PlainText(button), null, false, true);
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("ShowPopup failed (" + ex.Message + "); the chat version stands.");
                return false;
            }
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // RealmPainter's "world" board (/paint world [clue <n> | festival | census]):
        // { title, kicker, subtitle, body, footer, rows: [{ name, right, sub }] }. Plain text only.
        private Dictionary<string, object> GetWorldBoard(string arg)
        {
            if (loadFailed || data == null) return null;
            string a = (arg ?? "").Trim().ToLowerInvariant();
            var d = new Dictionary<string, object>();
            var rows = new List<Dictionary<string, object>>();
            d["rows"] = rows;
            d["kicker"] = Msg("Board.Kicker", null);
            if (a.StartsWith("clue"))
            {
                int n = 1;
                string rest = a.Substring(4).Trim();
                if (rest.Length > 0 && (!int.TryParse(rest, out n) || n < 1)) n = 1;
                Run r = data.Active;
                Hunt h = r != null && r.Kind == KTreasure ? RunningHunt(r) : null;
                d["kicker"] = Msg("Board.TreasureKicker", null);
                if (h == null || n > h.Steps.Count)
                {
                    Upcoming u = NextOf(KTreasure);
                    d["title"] = Msg("Board.ColdTitle", null);
                    d["body"] = u != null ? Fmt("Board.ColdNext", null, PlainWhen(u.At)) : Msg("Board.Cold", null);
                    return d;
                }
                d["title"] = Cap(h.Name);
                d["subtitle"] = Fmt("Board.ClueN", null, n, h.Steps.Count);
                d["body"] = h.Steps[n - 1].Riddle;
                d["footer"] = Msg("Board.TreasureFooter", null);
                return d;
            }
            if (a == "festival")
            {
                FestivalRun fr = data.Festival;
                FestivalDef f = RunningFestivalDef();
                d["kicker"] = Msg("Board.FestivalKicker", null);
                if (fr == null || f == null) { d["title"] = Msg("Board.NoFestivalTitle", null); d["body"] = Msg("Board.NoFestival", null); return d; }
                d["title"] = Cap(fr.Name);
                d["subtitle"] = Fmt("Board.FestivalUntil", null, fr.End.ToString("d MMMM", CultureInfo.InvariantCulture));
                List<KeyValuePair<string, int>> ranked = RankHouses(fr, f);
                for (int i = 0; i < ranked.Count && i < 6; i++)
                    rows.Add(new Dictionary<string, object> { { "name", "House " + ranked[i].Key }, { "right", ranked[i].Value.ToString(CultureInfo.InvariantCulture) }, { "owner", ranked[i].Key } });
                if (rows.Count == 0) d["body"] = Msg("Board.FestivalEmpty", null);
                d["footer"] = Msg("Board.FestivalFooter", null);
                return d;
            }
            if (a == "census")
            {
                d["kicker"] = Msg("Board.CensusKicker", null);
                CensusReport c = data.LastCensus;
                if (c == null) { d["title"] = Msg("Board.CensusTitle", null); d["body"] = Msg("Board.CensusNone", null); return d; }
                d["title"] = Msg("Board.CensusTitle", null);
                d["subtitle"] = c.At.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
                var body = new StringBuilder();
                for (int i = 0; i < c.Lines.Count && i < 4; i++) { if (body.Length > 0) body.Append(' '); body.Append(PlainText(c.Lines[i])); }
                d["body"] = body.ToString();
                return d;
            }
            Run run = data.Active;
            d["title"] = Msg("Board.WorldTitle", null);
            if (run != null) d["subtitle"] = Fmt("Board.Now", null, PlainText(ActiveTitle(run, null)), run.End.ToString("HH:mm", CultureInfo.InvariantCulture));
            else if (data.Festival != null) d["subtitle"] = Cap(data.Festival.Name);
            foreach (Upcoming u in NextOccurrences(Now(), 8, 5))
                rows.Add(new Dictionary<string, object> { { "name", PlainText(KindName(u.Slot.Event, null)) }, { "right", u.At.ToString("ddd HH:mm", CultureInfo.InvariantCulture) }, { "sub", CountdownDetailFor(u.Slot) } });
            if (rows.Count == 0) d["body"] = Msg("Board.NothingSet", null);
            d["footer"] = Msg("Board.WorldFooter", null);
            return d;
        }

        // The running world event as "kind|startIso|endIso" (as RealmEvents.GetActiveEvents speaks), or null.
        private string GetActiveWorldEvent()
        {
            Run a = data != null ? data.Active : null;
            return a != null ? a.Kind + "|" + Iso(a.Start) + "|" + Iso(a.End) : null;
        }

        // The next scheduled world event: { "title": string, "at": DateTime (UTC) }, or null.
        private Dictionary<string, object> GetNextWorldEvent()
        {
            if (loadFailed || data == null) return null;
            List<Upcoming> next = NextOccurrences(Now(), 8, 1);
            if (next.Count == 0) return null;
            var d = new Dictionary<string, object>();
            d["title"] = PlainText(KindName(next[0].Slot.Event, null));
            d["at"] = DateTime.SpecifyKind(next[0].At, DateTimeKind.Utc);
            return d;
        }

        private bool IsBloodMoon()
        {
            return data != null && IsBloodNight();
        }

        // RealmTravel or others may ask: is this player bearing the merchant caravan right now?
        private bool IsCaravanBearer(string playerId)
        {
            CaravanRun c = data != null ? RunningCaravan() : null;
            return c != null && c.Phase == "road" && playerId != null && c.BearerId == playerId;
        }

        private string GetFestivalName()
        {
            return data != null && data.Festival != null ? data.Festival.Name : null;
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

        // Staff run the world: unless AdminsCanWin, they take part in nothing that pays.
        private bool Eligible(Player player)
        {
            return player != null && !player.IsServer && (config.General.AdminsCanWin || !IsAdmin(player));
        }

        // RealmArena: is the player in a duel (gathering, counting down or fighting)? Absent arena: no.
        private bool InDuel(string playerId)
        {
            return RealmArena != null && !string.IsNullOrEmpty(playerId) && AskBool(RealmArena, "IsInDuel", playerId);
        }

        private string HouseOf(ulong playerId)
        {
            string h = AskString(RealmHouses, "GetHouse", playerId.ToString());
            return string.IsNullOrEmpty(h) ? null : h;
        }

        // Liege, vassal or treaty partner (RealmHouses).
        private bool Allied(string a, string b)
        {
            if (RealmHouses == null || a == null || b == null) return false;
            string la = AskString(RealmHouses, "GetLiege", a), lb = AskString(RealmHouses, "GetLiege", b);
            if (SameText(la, b) || SameText(lb, a)) return true;
            return AskBool(RealmHouses, "HasTreaty", a, b);
        }

        private string HouseSuffix(string house)
        {
            return string.IsNullOrEmpty(house) ? "" : Fmt("OfHouse", null, HouseTint(house));
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

        private static ulong ParseId(string id)
        {
            ulong u;
            return id != null && ulong.TryParse(id, out u) ? u : 0;
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

        private static bool TryEntityPos(Entity e, out UnityEngine.Vector3 pos)
        {
            pos = new UnityEngine.Vector3();
            try
            {
                if (e == null) return false;
                pos = e.Position;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static ulong ViewIdOf(Entity e)
        {
            try { return e != null ? e.NetViewID : 0; }
            catch (Exception) { return 0; }
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

        // The named place (RealmWorld place or RealmTravel waystone) nearest to a point.
        private string ResolveNearest(float x, float z, out float px, out float py, out float pz, out float pr)
        {
            px = py = pz = pr = 0;
            string best = null;
            float bestD = float.MaxValue;
            foreach (Place p in data.Places.Values)
            {
                float d = FlatDist(x, z, p.X, p.Z);
                if (d < bestD) { bestD = d; best = p.Name; px = p.X; py = p.Y; pz = p.Z; pr = p.Radius; }
            }
            Dictionary<string, TravelStone> stones = TravelStones();
            if (stones != null)
                foreach (KeyValuePair<string, TravelStone> kv in stones)
                {
                    TravelStone w = kv.Value;
                    if (w == null || !w.Enabled) continue;
                    float d = FlatDist(x, z, w.X, w.Z);
                    if (d < bestD) { bestD = d; best = Clean(string.IsNullOrEmpty(w.Name) ? Pretty(kv.Key) : w.Name, 40); px = w.X; py = w.Y; pz = w.Z; pr = w.Radius; }
                }
            return best;
        }

        private string NearestPlaceName(float x, float z)
        {
            float px, py, pz, pr;
            return ResolveNearest(x, z, out px, out py, out pz, out pr);
        }

        private string DistText(Player player, float metres)
        {
            if (metres < 1000f) return Fmt("Metres", player, ((int)(Math.Round(metres / 10f) * 10)).ToString(CultureInfo.InvariantCulture));
            return Fmt("Km", player, (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture));
        }

        // Compass point from (fromX, fromZ) to (toX, toZ): +x is east; north is +z unless General.NorthIsPositiveZ is false.
        private string DirText(Player player, float fromX, float fromZ, float toX, float toZ)
        {
            double dx = toX - fromX, dz = toZ - fromZ;
            if (!config.General.NorthIsPositiveZ) dz = -dz;
            double deg = Math.Atan2(dx, dz) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            int sector = (int)Math.Floor((deg + 22.5) / 45.0) % 8;
            return Msg("Dir." + sector, player);
        }

        private string SpanText(Player player, double seconds)
        {
            if (seconds < 60) return Fmt("Sec", player, Math.Max(0, (int)Math.Ceiling(seconds)));
            if (seconds < 3600 * 2) return Fmt("Min", player, (int)Math.Ceiling(seconds / 60));
            if (seconds < 86400 * 2) return Fmt("Hours", player, (int)Math.Ceiling(seconds / 3600));
            return Fmt("Days", player, (int)Math.Ceiling(seconds / 86400));
        }

        private string DaysText(Player player, TimeSpan t)
        {
            return SpanText(player, Math.Max(0, t.TotalSeconds));
        }

        // "in 2 h (Tue 19:00 UTC)"
        private string WhenText(Player player, DateTime at)
        {
            return Fmt("When", player, SpanText(player, (at - Now()).TotalSeconds), at.ToString("ddd HH:mm", CultureInfo.InvariantCulture));
        }

        private static string PlainWhen(DateTime at)
        {
            return at.ToString("dddd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        private static string ClockText(DateTime at)
        {
            return at.ToString("ddd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        private static string PosText(float x, float y, float z)
        {
            return ((int)Math.Round(x)) + "," + ((int)Math.Round(y)) + "," + ((int)Math.Round(z));
        }

        private static bool SameText(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Cap(string s)
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

        private static string JoinRange(string[] args, int first, int last)
        {
            if (args == null || first > last || first >= args.Length) return "";
            return string.Join(" ", args, first, Math.Min(last, args.Length - 1) - first + 1).Trim();
        }

        private static string JoinInts(List<int> list)
        {
            var parts = new List<string>();
            foreach (int i in list) parts.Add("#" + i);
            return string.Join(", ", parts.ToArray());
        }

        // Ids: 2 to 24 of a-z, 0-9 and '-', lower case.
        private static string NormalizeId(string s)
        {
            if (s == null) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.Length < 2 || t.Length > 24) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return null;
            return t;
        }

        // A RealmSculptor sculpture id (its file name in art/sculptures without .json).
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

        // Chat colour tags out (for windows, the Chronicle and signs).
        private static string PlainText(string text)
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

        private static bool TryParseHourMinute(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Trim().Split(':');
            int h, m;
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            value = new TimeSpan(h, m, 0);
            return true;
        }

        private static string Iso(DateTime t)
        {
            return t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static bool TryIso(string s, out DateTime t)
        {
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);
        }

        private string KindName(string kind, Player player)
        {
            return Msg("Kind." + kind, player);
        }

        private string EventTitle(string kind)
        {
            if (kind == "crown_night") return "Crown Night";
            if (kind == "tournament") return "the Royal Tournament";
            if (kind == "kings_hunt") return "the King's Hunt";
            if (kind == "truce") return "the Truce of the Realm";
            return Clean(kind, 30);
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
        }

        // A line for the staff online (and the log).
        private void StaffNote(string text)
        {
            Puts(PlainText(text));
            foreach (Player p in OnlinePlayers()) if (IsAdmin(p)) p.SendMessage(Styled(Msg("Speaker", p), ChatWarn, text));
        }

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            if (RealmChronicle == null) return;
            Ask(RealmChronicle, "Log", type, title, detail ?? "", actors ?? new string[0]);
        }

        private void AddHistory(string line)
        {
            if (config.General.HistoryLines <= 0) return;
            data.History.Add(Now().ToString("MMM d HH:mm", CultureInfo.InvariantCulture) + " " + PlainText(line));
            while (data.History.Count > config.General.HistoryLines) data.History.RemoveAt(0);
            dirty = true;
        }

        private void RefreshBoards()
        {
            if (RealmPainter != null) Ask(RealmPainter, "RefreshBoards", "world");
        }

        #endregion
    }
}
