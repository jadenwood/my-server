// RealmQuests: daily and weekly tasks, the Season 1 story, achievements and house goals for the players of Ostreval.
//
// Content is data. Every quest, story chapter, achievement, house goal and named place is written as JSON in the
// realm's own voice (source: plugins/docs/RealmQuests/content/, deployed to oxide/data/RealmQuests/). The plugin only
// reads those files (ExistsDatafile first, so DataFileSystem.ReadObject never creates them); a file that is missing
// or does not parse switches off only its own part, and /quest admin status says which. Nothing in content is code.
//
// What players do, and how the server sees it (tags as in docs/oxide-rok-api.md):
//   craft          The game crafts on the server: ItemCrafter.Update makes each product stack and raises
//                  ItemCrafterItemEvent (Crafter, Stack, Cycles) [ASM; CODE ItemCrafter.Update/CraftItem]. The crafting
//                  player is the Sender of the ItemCrafterCraftEvent that asked the crafter to start (Crafter, Product,
//                  Quantity) [ASM; CODE ItemCrafterListener.OnItemCrafterCraft], or the crafter's own entity owner for
//                  hand crafting. Both events are read with EventManager.Subscribe (the way RealmTreasury reads
//                  ItemPassEvent). OnItemCrafted(ItemCrafterFinishEvent) [OPJ L1088] cannot name the product: on the
//                  server ItemCrafter.CraftFinish clears Product before it raises the event [CODE], so it is used only
//                  to forget whose craft a station was running.
//   gather         NO server hook exists for gathering (docs/oxide-rok-api.md 3.9: every gather path runs on the client).
//                  OnGadgetCollect is picking a placed object back up [CODE ItemPrefabCollector.Collect], not gathering.
//                  So gathering is a delivery: /quest give takes the goods from the packs, MEASURED with
//                  ItemCollection.AutoCount before and after ItemCollection.AutoSplit [ASM], and the goods are spent.
//   slay           OnEntityDeath(EntityDeathEvent) [OPJ L188]: victim evt.Entity (IsPlayer, Owner), killer
//                  evt.KillingDamage.DamageSource.Owner [ASM; USE DeathMessages.cs:20]. A creature is an entity with a
//                  MonsterMotor or MonsterEntity (the test the game's own SalvageSupplier uses [CODE]); its kind is read
//                  from Entity.ToString() (UnityEngine.Object: "name (Type)"), cleaned. UNVERIFIED: the creature names;
//                  /quest admin creatures lists the last ones seen so content targets can be tuned.
//   build          OnCubePlacement(CubePlaceEvent) [OPJ L266]: Sender, GridID, Position, Material, CausedByDestruction
//                  [ASM]. Checked again on the next tick so a placement another plugin cancelled does not count.
//   visit          the player's Entity.Position [ASM] near a named place a steward has marked (/quest admin place set).
//   oaths          RealmHouses.GetHouseSummaries() polled: a house whose liege changes has sworn; its liege accepted.
//   treaties       oxide/data/RealmChronicle.json read-only (treaty_signed; the house names are in the title).
//   contracts      oxide/data/RealmContracts.json read-only (Status "done", FulfillerId, Type), as RealmRenown does.
//   events         RealmEvents.GetActiveEvents() polled: being online and active for EventAttendMinutes during an event.
//   chronicle      oxide/data/RealmChronicle.json read-only by event id cursor: the doer is actors[0], matched by name
//                  to a player this plugin has seen (ambiguous names are skipped).
//   state          RealmHouses.GetHouse/GetHouseLeader, RealmRenown.GetRenown/GetTitles, RealmTreasury.GetPurse polled.
//
// Rewards: marks through RealmTreasury.RewardMarks (new marks under the treasury's own reward caps; whatever it cannot
// pay yet stays owed here and is paid later), renown and titles through RealmRenown.AddDeed (deed kinds quest_daily,
// quest_weekly, quest_story, story_complete, achievement, achievement_gold, house_goal), house season points through
// RealmSeasons.AwardHouse, and a few items through a persisted owed ledger: the owed entry is removed and saved BEFORE
// the stack is given, the gain is measured with AutoCount, and only the shortfall goes back, so a crash can lose a
// reward but never duplicate one. RealmSentinel is told about each item gift (SentinelItemSource).
//
// Anti-abuse: daily quests are drawn from a seed of the player and the day (relogging changes nothing); one free
// reroll a day; an abandoned slot stays empty until the reset; marks pay only after MinActiveMinutesForMarks of active
// play and to at most MaxRewardedAccountsPerAddress accounts from one address a day; player kills count only against
// online, established, unallied victims, once per pair (either way round) per PvpPairCooldownHours, never against a
// victim fresh from a death or under new-player protection; crafted goods count once per stack from a craft the player
// asked for, with a daily cap and an ignore list; a building cell counts once a day; contracts between the same two
// players count once a day and never within a house; house goals count only members of HouseMemberMinHours, each
// capped at HouseGoalMemberCapPercent of the goal, and need HouseGoalMinContributors; reward items can never be handed
// in for a delivery (checked when content loads).
//
// Commands: /quest (journal, log, abandon, reroll, give, collect, story, house, admin) and /achievements.
// Admin (realmquests.admin): /quest admin status | reload | places | place set <id> [radius] | place clear <id> |
//   reset <player> [daily|weekly|story|all] | complete <player> <quest> | creatures | items <word>.
// API (non-public, Plugin.Call): ReportQuestEvent(string playerId, string type, string subject, int amount) -> bool,
//   GetAchievementCount(string playerId) -> int, GetStoryProgress(string playerId) -> string,
//   GetHouseGoalText(string house) -> string.
// Data: oxide/data/RealmQuests.json. If it exists but cannot be parsed (a truncated file, say) the plugin does nothing
// and NEVER writes it, so the record is not overwritten; a copy of the last good file is kept as RealmQuests_lastgood.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmQuests.md for the in-game test of each part.

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.AI;                                  // MonsterMotor [ASM]
using CodeHatch.Blocks.Networking.Events;            // CubePlaceEvent [ASM]
using CodeHatch.Common;                              // PlayerExtensions: SendMessage, SendError, GetInventory, ShowPopup [ASM]
using CodeHatch.Damaging;                            // Damage [ASM]
using CodeHatch.Engine.Behaviours;                   // ItemCrafter [ASM]
using CodeHatch.Engine.Core.Cache;                   // Entity, MonsterEntity [ASM]
using CodeHatch.Engine.Networking;                   // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;                // InvItemBlueprint, InvBlueprints, InvGameItemStack [ASM]
using CodeHatch.Inventory.Blueprints.Components;     // ContainerManagement [ASM]
using CodeHatch.ItemContainer;                       // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events;                   // EventManager, EventSubscriber<T>, EventHandlerOrder [ASM]
using CodeHatch.Networking.Events.Entities;          // EntityDeathEvent, ItemCrafterCraftEvent, ItemCrafterItemEvent, ItemCrafterFinishEvent [ASM]
using Oxide.Core;                                    // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                            // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmQuests", "Realm", "0.1.0")]
    [Description("Daily and weekly tasks, the Season 1 story, achievements and house goals, tracked from real game hooks")]
    public class RealmQuests : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmEvents;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmSentinel;

        private const string PermAdmin = "realmquests.admin";
        private const string DataName = "RealmQuests";
        private const string BackupName = "RealmQuests_lastgood";
        private const string ContentDir = "RealmQuests/";
        private const string ChronicleFile = "RealmChronicle";
        private const string ContractsFile = "RealmContracts";
        private const string SeasonsFile = "RealmSeasons";
        private const string StoryChronicleType = "title_earned";

        private const string KDaily = "daily";
        private const string KWeekly = "weekly";
        private const string KStory = "story";

        // Objective types. Counted ones add up what the player does; state ones follow a value the realm already keeps.
        private const string TCraft = "craft";
        private const string TDeliver = "deliver";
        private const string TSlayCreature = "slay_creature";
        private const string TSlayPlayer = "slay_player";
        private const string TBuild = "build";
        private const string TVisit = "visit";
        private const string TOath = "oath";
        private const string TTreaty = "treaty";
        private const string TContract = "contract";
        private const string TEvent = "event";
        private const string TChronicle = "chronicle";
        private const string TPlaytime = "playtime";
        private const string TQuests = "quests";
        private const string TAchievements = "achievements";
        private const string THouseGoal = "house_goal";
        private const string TCustom = "custom";
        private const string THouse = "house";
        private const string TRenown = "renown";
        private const string TMarks = "marks";
        private const string TTitle = "title";
        private const string TSurvive = "survive";

        private static readonly string[] CountedTypes =
        {
            TCraft, TDeliver, TSlayCreature, TSlayPlayer, TBuild, TVisit, TOath, TTreaty, TContract, TEvent, TChronicle,
            TPlaytime, TQuests, TAchievements, THouseGoal, TCustom
        };
        private static readonly string[] StateTypes = { THouse, TRenown, TMarks, TTitle, TSurvive };
        private static readonly string[] HouseGoalTypes = { TCraft, TDeliver, TSlayCreature, TSlayPlayer, TBuild, TContract, TEvent, TPlaytime, TCustom };
        private static readonly string[] Categories = { "survival", "war", "politics", "economy", "exploration" };
        private static readonly string[] ExternalTypes = { TContract, TEvent, TCustom };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private bool dirty;
        private Timer tickTimer;
        private Timer visitTimer;
        private DateTime lastFeedPoll = DateTime.MinValue;
        private DateTime lastSave = DateTime.MinValue;
        private int depth;                                       // guards achievement-of-achievement recursion
        private int storyChain;                                  // guards story steps closing one another
        private DateTime treasuryDryUntil = DateTime.MinValue;   // RealmTreasury paid nothing: wait before asking again

        // Content (read-only, reloaded with /quest admin reload).
        private readonly Dictionary<string, QuestDef> dailies = new Dictionary<string, QuestDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, QuestDef> weeklies = new Dictionary<string, QuestDef>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> dailyOrder = new List<string>();
        private readonly List<string> weeklyOrder = new List<string>();
        private StoryFile story;
        private readonly List<AchievementDef> achievements = new List<AchievementDef>();
        private readonly Dictionary<string, AchievementDef> achievementById = new Dictionary<string, AchievementDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HouseGoalDef> houseGoals = new Dictionary<string, HouseGoalDef>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> houseGoalOrder = new List<string>();
        private readonly Dictionary<string, PlaceDef> places = new Dictionary<string, PlaceDef>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> placeOrder = new List<string>();
        private readonly Dictionary<string, string> contentStatus = new Dictionary<string, string>();
        private readonly List<string> contentProblems = new List<string>();

        // Session state (never saved: losing it on a reload is harmless).
        private EventSubscriber<ItemCrafterCraftEvent> craftSubscriber;
        private EventSubscriber<ItemCrafterItemEvent> craftItemSubscriber;
        private readonly Dictionary<object, CraftClaim> craftClaims = new Dictionary<object, CraftClaim>(new RefComparer());
        private readonly Dictionary<ulong, Sample> samples = new Dictionary<ulong, Sample>();
        private readonly Dictionary<string, Dictionary<string, bool>> buildCells = new Dictionary<string, Dictionary<string, bool>>();
        private string buildCellsDay;
        private readonly Dictionary<string, double> attendSeconds = new Dictionary<string, double>();
        private readonly Dictionary<ulong, string> addressOf = new Dictionary<ulong, string>();
        private readonly Dictionary<string, List<string>> paidAddresses = new Dictionary<string, List<string>>();
        private string paidAddressesDay;
        private readonly Dictionary<string, DateTime> lastNotice = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> abandonAsk = new Dictionary<string, DateTime>();
        private readonly Queue<DateTime> announceTimes = new Queue<DateTime>();
        private readonly Queue<DateTime> chronicleTimes = new Queue<DateTime>();
        private readonly List<string> recentCreatures = new List<string>();
        private readonly Dictionary<string, string> feedStatus = new Dictionary<string, string>();
        private int seasonDayCache = -2;
        private DateTime seasonDayRead = DateTime.MinValue;

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        private class CraftClaim
        {
            public ulong PlayerId;
            public string Product;
            public DateTime At;
        }

        private class Sample
        {
            public float X, Y, Z;
            public bool Has;
            public DateTime LastAction = DateTime.MinValue;
            public double Fraction;                        // active seconds not yet counted as a whole minute
        }

        // Reference identity for game objects (Unity overrides Equals and GetHashCode).
        private class RefComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        #region Config

        private class PluginConfig
        {
            public bool Enabled = true;
            public bool DailiesEnabled = true;
            public bool WeekliesEnabled = true;
            public bool StoryEnabled = true;
            public bool AchievementsEnabled = true;
            public bool HouseGoalsEnabled = true;
            public int DailyCount = 3;
            public int WeeklyCount = 2;
            public int DailyRerolls = 1;
            public int ResetHourUtc = 4;                    // the quest day turns at this hour (UTC); the week turns on Monday
            public bool StoryRequiresSeason = true;         // the story runs only while RealmSeasons runs its season
            public bool StoryTimeGates = true;              // chapters wait for their UnlockDay of the season
            public bool UsePopups = true;
            public bool PopupOnComplete = true;
            public bool MarksRewards = true;
            public bool ItemRewards = true;
            public bool RenownRewards = true;
            public bool SeasonPointRewards = true;
            public float RewardScale = 1f;                  // multiplies every marks reward
            public int MarksPayPerTick = 500;               // marks asked of the treasury per player per tick
            public int MinActiveMinutesForMarks = 30;       // an account plays this long before marks are paid (alts)
            public int MaxRewardedAccountsPerAddress = 3;   // accounts from one address paid marks per day (0 = no limit)
            public int PvpPairCooldownHours = 24;           // the same two players: one counted kill, either way round
            public int PvpMinVictimLifeSeconds = 120;       // a victim this fresh from a death does not count
            public int PvpMinVictimActiveMinutes = 30;      // nor a victim with less active play than this (fodder alts)
            public int PvpCreditsPerDay = 10;
            public bool PvpAlliesCount = false;
            public int CreatureCreditsPerDay = 150;
            public int CraftCreditsPerDay = 300;
            public List<string> CraftIgnore;                // products that never count (cheap loops)
            public int BuildCreditsPerDay = 400;
            public int ContractCreditsPerDay = 3;
            public int ContractPairCooldownHours = 24;
            public int EventAttendMinutes = 10;
            public int HouseGoalCount = 1;
            public int HouseGoalMinMembers = 2;
            public int HouseGoalMinContributors = 2;
            public int HouseGoalMemberCapPercent = 50;
            public int HouseMemberMinHours = 12;
            public float TickSeconds = 30f;
            public float FeedPollSeconds = 60f;
            public float VisitSeconds = 5f;                 // how often positions are checked against marked places
            public float ActiveMoveMeters = 1.5f;
            public int ActionKeepsActiveSeconds = 120;
            public int ProgressNoticeSeconds = 15;
            public bool AnnounceAchievements = true;        // gold tiers and the story's end are told to the realm
            public int MaxAnnouncementsPerHour = 6;
            public bool ChronicleStory = true;
            public int MaxChroniclePerHour = 4;
            public int MaxPlayers = 5000;
            public int JournalPopupMaxLines = 18;
            public Dictionary<string, int> MaterialIds;     // build roles -> the game's material ids (RealmSculptor's table)
            public List<string> TierNames;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.CraftIgnore = new List<string> { "Torch", "Bandage", "Stick", "Firewood", "Wood Shield" };
            c.MaterialIds = DefaultMaterialIds();
            c.TierNames = new List<string> { "Bronze", "Silver", "Gold", "Ostreval" };
            return c;
        }

        // Material roles -> the game's material (tileset) ids, as RealmSculptor ships them. UNVERIFIED: /sculpt materials
        // writes the real table; fix a wrong id here.
        private static Dictionary<string, int> DefaultMaterialIds()
        {
            var m = new Dictionary<string, int>();
            m["cobblestone"] = 1; m["stone"] = 2; m["clay"] = 3; m["sod"] = 4; m["thatch"] = 5;
            m["spruce"] = 6; m["wood"] = 7; m["log"] = 8; m["reinforced"] = 9;
            return m;
        }

        private void ClampConfig()
        {
            if (config == null) config = DefaultConfig();
            PluginConfig d = DefaultConfig();
            config.DailyCount = Clamp(config.DailyCount, 0, 6);
            config.WeeklyCount = Clamp(config.WeeklyCount, 0, 4);
            config.DailyRerolls = Clamp(config.DailyRerolls, 0, 5);
            config.ResetHourUtc = Clamp(config.ResetHourUtc, 0, 23);
            if (float.IsNaN(config.RewardScale) || config.RewardScale < 0f) config.RewardScale = 1f;
            if (config.RewardScale > 10f) config.RewardScale = 10f;
            config.MarksPayPerTick = Clamp(config.MarksPayPerTick, 1, 100000);
            config.MinActiveMinutesForMarks = Clamp(config.MinActiveMinutesForMarks, 0, 10000);
            config.MaxRewardedAccountsPerAddress = Clamp(config.MaxRewardedAccountsPerAddress, 0, 100);
            config.PvpPairCooldownHours = Clamp(config.PvpPairCooldownHours, 0, 720);
            config.PvpMinVictimLifeSeconds = Clamp(config.PvpMinVictimLifeSeconds, 0, 3600);
            config.PvpMinVictimActiveMinutes = Clamp(config.PvpMinVictimActiveMinutes, 0, 10000);
            config.PvpCreditsPerDay = Clamp(config.PvpCreditsPerDay, 0, 1000);
            config.CreatureCreditsPerDay = Clamp(config.CreatureCreditsPerDay, 0, 10000);
            config.CraftCreditsPerDay = Clamp(config.CraftCreditsPerDay, 0, 100000);
            config.BuildCreditsPerDay = Clamp(config.BuildCreditsPerDay, 0, 100000);
            config.ContractCreditsPerDay = Clamp(config.ContractCreditsPerDay, 0, 100);
            config.ContractPairCooldownHours = Clamp(config.ContractPairCooldownHours, 0, 720);
            config.EventAttendMinutes = Clamp(config.EventAttendMinutes, 1, 240);
            config.HouseGoalCount = Clamp(config.HouseGoalCount, 0, 3);
            config.HouseGoalMinMembers = Clamp(config.HouseGoalMinMembers, 1, 50);
            config.HouseGoalMinContributors = Clamp(config.HouseGoalMinContributors, 1, 50);
            config.HouseGoalMemberCapPercent = Clamp(config.HouseGoalMemberCapPercent, 1, 100);
            config.HouseMemberMinHours = Clamp(config.HouseMemberMinHours, 0, 720);
            if (float.IsNaN(config.TickSeconds) || config.TickSeconds < 5f) config.TickSeconds = 5f;
            if (config.TickSeconds > 300f) config.TickSeconds = 300f;
            if (float.IsNaN(config.FeedPollSeconds) || config.FeedPollSeconds < 10f) config.FeedPollSeconds = 10f;
            if (float.IsNaN(config.VisitSeconds) || config.VisitSeconds < 2f) config.VisitSeconds = 2f;
            if (config.VisitSeconds > 300f) config.VisitSeconds = 300f;
            if (config.FeedPollSeconds > 3600f) config.FeedPollSeconds = 3600f;
            if (float.IsNaN(config.ActiveMoveMeters) || config.ActiveMoveMeters < 0f) config.ActiveMoveMeters = 1.5f;
            config.ActionKeepsActiveSeconds = Clamp(config.ActionKeepsActiveSeconds, 0, 3600);
            config.ProgressNoticeSeconds = Clamp(config.ProgressNoticeSeconds, 0, 3600);
            config.MaxAnnouncementsPerHour = Clamp(config.MaxAnnouncementsPerHour, 0, 120);
            config.MaxChroniclePerHour = Clamp(config.MaxChroniclePerHour, 0, 60);
            config.MaxPlayers = Clamp(config.MaxPlayers, 50, 100000);
            config.JournalPopupMaxLines = Clamp(config.JournalPopupMaxLines, 4, 40);
            if (config.CraftIgnore == null) config.CraftIgnore = d.CraftIgnore;
            if (config.MaterialIds == null) config.MaterialIds = d.MaterialIds;
            if (config.TierNames == null || config.TierNames.Count == 0) config.TierNames = d.TierNames;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        #endregion

        #region Content

        private class ObjectiveDef
        {
            public string Type;
            public List<string> Targets;                    // empty = anything of the type; "~word" = name contains word
            public int Count = 1;
            public string Text;                             // what the journal shows ("slay wolves")
            public bool Distinct;                           // count each victim, place or subject once
        }

        private class RewardItem
        {
            public string Item;
            public int Count;
        }

        private class RewardDef
        {
            public int Marks;
            public List<RewardItem> Items;
            public string Renown;                           // RealmRenown deed kind ("" = none)
            public int SeasonPoints;                        // to the player's house (RealmSeasons.AwardHouse)
        }

        private class QuestDef
        {
            public string Id;
            public string Title;
            public string Text;
            public string Done;
            public int Weight = 10;
            public bool NeedsHouse;
            public int MinActiveMinutes;
            public bool AnyOne;                             // done when any one objective is done (else all of them)
            public List<ObjectiveDef> Objectives;
            public RewardDef Reward;
        }

        private class QuestFile
        {
            public int Version;
            public List<QuestDef> Quests;
        }

        private class ChapterDef
        {
            public string Id;
            public int Act;
            public string Title;
            public string Intro;
            public string Outro;
            public int UnlockDay;
            public List<QuestDef> Steps;
            public RewardDef Reward;
        }

        private class StoryFile
        {
            public int Version;
            public int Season;
            public string Title;
            public string Prologue;
            public string Epilogue;
            public List<ChapterDef> Chapters;
            public RewardDef Reward;
        }

        private class TierDef
        {
            public int Count;
            public RewardDef Reward;
        }

        private class AchievementDef
        {
            public string Id;
            public string Name;
            public string Category;
            public string Text;
            public ObjectiveDef Objective;
            public List<TierDef> Tiers;
        }

        private class AchievementFile
        {
            public int Version;
            public List<AchievementDef> Achievements;
        }

        private class HouseGoalDef
        {
            public string Id;
            public string Title;
            public string Text;
            public string Done;
            public ObjectiveDef Objective;
            public int Points;
            public int Marks;
            public string Renown;
            public int Weight = 10;
        }

        private class HouseGoalFile
        {
            public int Version;
            public List<HouseGoalDef> Goals;
        }

        private class PlaceDef
        {
            public string Id;
            public string Name;
            public string Lore;
            public float Radius = 40f;
        }

        private class PlaceFile
        {
            public int Version;
            public List<PlaceDef> Places;
        }

        private T ReadContent<T>(string name) where T : class, new()
        {
            string path = ContentDir + name;
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(path)) { contentStatus[name] = "missing"; return null; }
            T obj;
            try { obj = Interface.Oxide.DataFileSystem.ReadObject<T>(path); }
            catch (Exception ex) { contentStatus[name] = "unreadable (" + ex.Message + ")"; return null; }
            if (obj == null) { contentStatus[name] = "empty"; return null; }
            contentStatus[name] = "ok";
            return obj;
        }

        // Reads every content file, keeps what is valid and lists the rest. Never writes a content file.
        private void LoadContent()
        {
            dailies.Clear(); weeklies.Clear(); dailyOrder.Clear(); weeklyOrder.Clear();
            achievements.Clear(); achievementById.Clear(); houseGoals.Clear(); houseGoalOrder.Clear();
            places.Clear(); placeOrder.Clear(); contentStatus.Clear(); contentProblems.Clear();
            story = null;

            PlaceFile pf = ReadContent<PlaceFile>("Places");
            if (pf != null && pf.Places != null)
                foreach (PlaceDef p in pf.Places)
                {
                    string why = p == null ? "empty entry" : !ValidId(p.Id) ? "bad id" : places.ContainsKey(p.Id) ? "duplicate id"
                        : !ValidText(p.Name, 40) ? "bad name" : null;
                    if (why != null) { Problem("Places: " + (p != null ? p.Id : "?") + ": " + why); continue; }
                    if (float.IsNaN(p.Radius) || p.Radius < 5f) p.Radius = 5f;
                    if (p.Radius > 500f) p.Radius = 500f;
                    places[p.Id] = p;
                    placeOrder.Add(p.Id);
                }

            var rewardItems = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            QuestFile df = ReadContent<QuestFile>("Dailies");
            QuestFile wf = ReadContent<QuestFile>("Weeklies");
            StoryFile sf = ReadContent<StoryFile>("Story");
            AchievementFile af = ReadContent<AchievementFile>("Achievements");
            HouseGoalFile hf = ReadContent<HouseGoalFile>("HouseGoals");

            // Every item any reward can give, so no delivery can be fed from a reward (an item loop).
            if (df != null && df.Quests != null) foreach (QuestDef q in df.Quests) CollectRewardItems(q != null ? q.Reward : null, rewardItems);
            if (wf != null && wf.Quests != null) foreach (QuestDef q in wf.Quests) CollectRewardItems(q != null ? q.Reward : null, rewardItems);
            if (sf != null && sf.Chapters != null)
            {
                CollectRewardItems(sf.Reward, rewardItems);
                foreach (ChapterDef c in sf.Chapters)
                {
                    if (c == null) continue;
                    CollectRewardItems(c.Reward, rewardItems);
                    if (c.Steps != null) foreach (QuestDef q in c.Steps) CollectRewardItems(q != null ? q.Reward : null, rewardItems);
                }
            }
            if (af != null && af.Achievements != null)
                foreach (AchievementDef a in af.Achievements)
                    if (a != null && a.Tiers != null) foreach (TierDef t in a.Tiers) CollectRewardItems(t != null ? t.Reward : null, rewardItems);

            var allIds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (df != null && df.Quests != null)
                foreach (QuestDef q in df.Quests)
                    if (CheckQuest("Dailies", q, allIds, rewardItems, false)) { dailies[q.Id] = q; dailyOrder.Add(q.Id); }
            if (wf != null && wf.Quests != null)
                foreach (QuestDef q in wf.Quests)
                    if (CheckQuest("Weeklies", q, allIds, rewardItems, false)) { weeklies[q.Id] = q; weeklyOrder.Add(q.Id); }

            if (sf != null)
            {
                var chapters = new List<ChapterDef>();
                if (sf.Chapters != null)
                    foreach (ChapterDef c in sf.Chapters)
                    {
                        if (c == null || !ValidId(c.Id) || allIds.ContainsKey(c.Id) || !ValidText(c.Title, 60)) { Problem("Story: chapter " + (c != null ? c.Id : "?") + ": bad id or title"); continue; }
                        allIds[c.Id] = true;
                        var steps = new List<QuestDef>();
                        if (c.Steps != null)
                            foreach (QuestDef q in c.Steps)
                                if (CheckQuest("Story/" + c.Id, q, allIds, rewardItems, true)) steps.Add(q);
                        if (steps.Count == 0) { Problem("Story: chapter " + c.Id + " has no valid step"); continue; }
                        c.Steps = steps;
                        if (c.UnlockDay < 0) c.UnlockDay = 0;
                        if (!OptionalText(c.Intro, 200) || !OptionalText(c.Outro, 200)) { Problem("Story: chapter " + c.Id + ": intro or outro is over 200 characters or holds [ ] { }"); c.Intro = null; c.Outro = null; }
                        CheckReward("Story/" + c.Id, c.Reward);
                        chapters.Add(c);
                    }
                if (chapters.Count > 0)
                {
                    sf.Chapters = chapters;
                    CheckReward("Story", sf.Reward);
                    if (!ValidText(sf.Title, 60)) sf.Title = "The Story";
                    if (!OptionalText(sf.Prologue, 200) || !OptionalText(sf.Epilogue, 200)) { Problem("Story: prologue or epilogue is over 200 characters or holds [ ] { }"); sf.Prologue = null; sf.Epilogue = null; }
                    story = sf;
                }
                else Problem("Story: no valid chapter");
            }

            if (af != null && af.Achievements != null)
                foreach (AchievementDef a in af.Achievements)
                {
                    string why = CheckAchievement(a, allIds, rewardItems);
                    if (why != null) { Problem("Achievements: " + (a != null ? a.Id : "?") + ": " + why); continue; }
                    allIds[a.Id] = true;
                    achievements.Add(a);
                    achievementById[a.Id] = a;
                }

            if (hf != null && hf.Goals != null)
                foreach (HouseGoalDef g in hf.Goals)
                {
                    string why = g == null ? "empty entry" : !ValidId(g.Id) || allIds.ContainsKey(g.Id) ? "bad or duplicate id"
                        : !ValidText(g.Title, 60) || !ValidText(g.Text, 200) ? "bad title or text"
                        : CheckObjective(g.Objective, rewardItems, true);
                    if (why == null && Array.IndexOf(HouseGoalTypes, g.Objective.Type) < 0) why = "objective type " + g.Objective.Type + " cannot be a house goal";
                    if (why != null) { Problem("HouseGoals: " + (g != null ? g.Id : "?") + ": " + why); continue; }
                    allIds[g.Id] = true;
                    if (g.Weight < 1) g.Weight = 1;
                    g.Points = Clamp(g.Points, 0, 1000);
                    g.Marks = Clamp(g.Marks, 0, 10000);
                    houseGoals[g.Id] = g;
                    houseGoalOrder.Add(g.Id);
                }

            Puts("Content: " + dailies.Count + " daily, " + weeklies.Count + " weekly, " + (story != null ? story.Chapters.Count : 0)
                + " story chapters, " + achievements.Count + " achievements, " + houseGoals.Count + " house goals, " + places.Count
                + " places" + (contentProblems.Count > 0 ? "; " + contentProblems.Count + " problem(s), see /quest admin status" : ""));
        }

        // Item names in content are checked against the game's blueprints once the world has loaded. An unknown name is
        // reported (the delivery or reward cannot work) but the content stays, so a typo is fixed without a reload race.
        private void CheckItems()
        {
            if (InvBlueprints.Instance == null) return;
            var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var all = new List<QuestDef>(dailies.Values);
            all.AddRange(weeklies.Values);
            if (story != null) foreach (ChapterDef c in story.Chapters) { all.AddRange(c.Steps); CollectRewardItems(c.Reward, names); }
            if (story != null) CollectRewardItems(story.Reward, names);
            foreach (QuestDef q in all)
            {
                CollectRewardItems(q.Reward, names);
                foreach (ObjectiveDef o in q.Objectives) if (o.Type == TDeliver) foreach (string t in o.Targets) names[t.TrimStart('~')] = true;
            }
            foreach (HouseGoalDef g in houseGoals.Values) if (g.Objective.Type == TDeliver) foreach (string t in g.Objective.Targets) names[t.TrimStart('~')] = true;
            foreach (AchievementDef a in achievements) foreach (TierDef t in a.Tiers) CollectRewardItems(t.Reward, names);
            foreach (string n in names.Keys) if (FindBlueprint(n) == null) Problem("item '" + n + "' is not known to this server (/quest admin items <word>)");
        }

        private void Problem(string text)
        {
            if (contentProblems.Count < 200) contentProblems.Add(text);
            PrintWarning("Content: " + text);
        }

        private static void CollectRewardItems(RewardDef r, Dictionary<string, bool> into)
        {
            if (r == null || r.Items == null) return;
            foreach (RewardItem i in r.Items) if (i != null && !string.IsNullOrEmpty(i.Item)) into[i.Item.Trim()] = true;
        }

        private bool CheckQuest(string where, QuestDef q, Dictionary<string, bool> allIds, Dictionary<string, bool> rewardItems, bool storyStep)
        {
            string why = null;
            if (q == null) why = "empty entry";
            else if (!ValidId(q.Id)) why = "bad id";
            else if (allIds.ContainsKey(q.Id)) why = "duplicate id";
            else if (!ValidText(q.Title, 60)) why = "bad title (1-60 characters, no colour tags)";
            else if (!ValidText(q.Text, 200)) why = "bad text (1-200 characters, no colour tags)";
            else if (q.Done != null && q.Done.Length > 0 && !ValidText(q.Done, 200)) why = "bad done text";
            else if (q.Objectives == null || q.Objectives.Count == 0 || q.Objectives.Count > 4) why = "needs 1-4 objectives";
            else
                foreach (ObjectiveDef o in q.Objectives)
                {
                    why = CheckObjective(o, rewardItems, false);
                    if (why == null && o.Type == TAchievements) why = "a task cannot count achievements";
                    if (why == null && o.Type == TQuests)
                    {
                        // Only a story step may count finished daily or weekly tasks (never story steps: no loop).
                        if (!storyStep || o.Targets.Count == 0) why = "only a story step may count tasks, and it must name daily or weekly";
                        else foreach (string t in o.Targets) if (t != KDaily && t != KWeekly) { why = "a story step may count only daily or weekly tasks"; break; }
                    }
                    if (why != null) break;
                }
            if (why == null) why = CheckReward(null, q.Reward);
            if (why != null) { Problem(where + ": " + (q != null ? q.Id : "?") + ": " + why); return false; }
            allIds[q.Id] = true;
            if (q.Weight < 1) q.Weight = 1;
            if (q.MinActiveMinutes < 0) q.MinActiveMinutes = 0;
            if (q.Reward == null) q.Reward = new RewardDef();
            return true;
        }

        private string CheckObjective(ObjectiveDef o, Dictionary<string, bool> rewardItems, bool house)
        {
            if (o == null) return "empty objective";
            if (o.Type == null || (Array.IndexOf(CountedTypes, o.Type) < 0 && Array.IndexOf(StateTypes, o.Type) < 0)) return "unknown objective type '" + o.Type + "'";
            if (o.Targets == null) o.Targets = new List<string>();
            o.Targets.RemoveAll(delegate(string t) { return string.IsNullOrEmpty(t) || t.Trim().Length == 0; });
            int max = house ? 1000000 : 100000;
            if (o.Count < 1 || o.Count > max) return "count must be 1-" + max;
            if (!ValidText(o.Text, 80)) return "bad objective text (1-80 characters)";
            if (o.Type == TDeliver)
            {
                if (o.Targets.Count == 0) return "a delivery must name its goods";
                foreach (string t in o.Targets) if (rewardItems.ContainsKey(t.Trim())) return "'" + t + "' is also a reward item (a reward could be handed straight back in)";
            }
            if (o.Type == TVisit)
            {
                foreach (string t in o.Targets) if (!places.ContainsKey(t)) return "unknown place '" + t + "'";
                if (o.Targets.Count > 0 && o.Count > o.Targets.Count) return "a visit cannot ask for more places than it names";
                o.Distinct = true;                          // each place counts once
            }
            return null;
        }

        private string CheckReward(string where, RewardDef r)
        {
            if (r == null) return null;
            string why = null;
            if (r.Marks < 0 || r.Marks > 10000) why = "marks must be 0-10000";
            else if (r.SeasonPoints < 0 || r.SeasonPoints > 100) why = "season points must be 0-100";
            else if (r.Items != null)
            {
                if (r.Items.Count > 4) why = "at most 4 reward items";
                else foreach (RewardItem i in r.Items) if (i == null || string.IsNullOrEmpty(i.Item) || i.Count < 1 || i.Count > 500) { why = "bad reward item"; break; }
            }
            if (r.Renown == null) r.Renown = "";
            if (why != null && where != null) Problem(where + ": " + why);
            return why;
        }

        private string CheckAchievement(AchievementDef a, Dictionary<string, bool> allIds, Dictionary<string, bool> rewardItems)
        {
            if (a == null) return "empty entry";
            if (!ValidId(a.Id) || allIds.ContainsKey(a.Id)) return "bad or duplicate id";
            if (!ValidText(a.Name, 40)) return "bad name";
            if (!ValidText(a.Text, 160)) return "bad text";
            if (a.Category == null || Array.IndexOf(Categories, a.Category) < 0) return "category must be one of " + string.Join(", ", Categories);
            string o = CheckObjective(a.Objective, rewardItems, false);
            if (o != null) return o;
            if (a.Tiers == null || a.Tiers.Count == 0 || a.Tiers.Count > 4) return "needs 1-4 tiers";
            int last = 0;
            foreach (TierDef t in a.Tiers)
            {
                if (t == null || t.Count <= last) return "tiers must rise";
                last = t.Count;
                string r = CheckReward(null, t.Reward);
                if (r != null) return r;
                if (t.Reward == null) t.Reward = new RewardDef();
            }
            return null;
        }

        private static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40) return false;
            foreach (char c in id) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        private static bool OptionalText(string s, int max)
        {
            return string.IsNullOrEmpty(s) || ValidText(s, max);
        }

        // Content text is plain: no colour tags, no braces (it is sent as an argument, but keep it clean), bounded.
        private static bool ValidText(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Trim().Length == 0 || s.Length > max) return false;
            foreach (char c in s) if (char.IsControl(c) || c == '[' || c == ']' || c == '{' || c == '}') return false;
            return true;
        }

        #endregion

        #region Data

        private class Slot
        {
            public string QuestId;
            public List<int> Progress = new List<int>();
            public List<string> Seen = new List<string>();  // distinct victims or places, "objective|subject"
            public bool Done;
            public bool Abandoned;
        }

        private class StoryState
        {
            public string StepId;                           // the step Progress belongs to
            public List<int> Progress = new List<int>();
            public List<string> Seen = new List<string>();
            public List<string> Finished = new List<string>();   // step ids, then chapter ids as "chapter:<id>"
            public bool Complete;
            public DateTime CompletedAt;
            public string IntroShown;                       // the last chapter whose intro window was shown
            public int Season;                              // the season whose tale this is
        }

        private class AchState
        {
            public long Count;
            public int Tier;
            public List<string> Seen;
        }

        private class OwedItem
        {
            public string Item;
            public int Amount;
            public string Source;
        }

        private class PlayerQ
        {
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public double ActiveMinutes;
            public double LifeMinutes;                      // active minutes since the last death
            public DateTime LastDeath = DateTime.MinValue;
            public string DayKey;
            public List<Slot> Daily = new List<Slot>();
            public List<string> LastDaily = new List<string>();
            public int Rerolls;
            public string WeekKey;
            public List<Slot> Weekly = new List<Slot>();
            public StoryState Story = new StoryState();
            public Dictionary<string, AchState> Ach = new Dictionary<string, AchState>();
            public List<OwedItem> Owed = new List<OwedItem>();
            public long PendingMarks;
            public long MarksEarned;
            public Dictionary<string, int> Counts = new Dictionary<string, int>();   // daily, weekly, story, achievements, house_goal
            public string CreditDay;
            public Dictionary<string, int> Credit = new Dictionary<string, int>();   // per-day caps: pvp, creature, craft, build, contract
            public List<string> VisitedToday = new List<string>();
            public string House;
            public DateTime HouseSince;
            public bool MarksHeldNotice;
        }

        private class HouseGoalState
        {
            public string GoalId;
            public int Progress;
            public Dictionary<string, int> Contrib = new Dictionary<string, int>();
            public bool Done;
        }

        private class HouseQ
        {
            public string Name;
            public string WeekKey;
            public List<HouseGoalState> Goals = new List<HouseGoalState>();
        }

        private class PlaceMark
        {
            public float X, Y, Z;
            public float Radius;
            public string By;
            public DateTime At;
        }

        private class StoredData
        {
            public int Version = 1;
            public string Salt;
            public Dictionary<string, PlayerQ> Players = new Dictionary<string, PlayerQ>();
            public Dictionary<string, HouseQ> Houses = new Dictionary<string, HouseQ>();
            public Dictionary<string, PlaceMark> Places = new Dictionary<string, PlaceMark>();
            public Dictionary<string, DateTime> PvpPairs = new Dictionary<string, DateTime>();
            public Dictionary<string, DateTime> ContractPairs = new Dictionary<string, DateTime>();
            public Dictionary<string, string> Lieges = new Dictionary<string, string>();   // house -> liege, as last polled
            public bool LiegesBaselined;
            public int ChronicleCursor = -1;
            public int ContractsNextId;
            public bool ContractsBaselined;
            public List<int> CreditedContracts = new List<int>();
            public List<string> EventCredits = new List<string>();   // "kind|day|player"
            public string FirstStoryName;
            public int FirstStorySeason;
        }

        private void LoadData()
        {
            bool existed;
            StoredData loaded;
            try
            {
                existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message + ". RealmQuests will not track or write anything"
                    + " until the file is fixed, or replaced by " + BackupName + ".json, and the plugin reloaded.");
                return;
            }
            if (loaded == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json " + (existed ? "is empty" : "could not be made") + ". RealmQuests will not write it until it is fixed or moved away.");
                return;
            }
            data = loaded;
            Normalize();
            if (existed)
            {
                try { Interface.Oxide.DataFileSystem.WriteObject(BackupName, data); }
                catch (Exception ex) { PrintWarning("Could not write " + BackupName + ": " + ex.Message); }
            }
        }

        private void Normalize()
        {
            if (string.IsNullOrEmpty(data.Salt)) data.Salt = Guid.NewGuid().ToString("N");
            if (data.Players == null) data.Players = new Dictionary<string, PlayerQ>();
            if (data.Houses == null) data.Houses = new Dictionary<string, HouseQ>();
            if (data.Places == null) data.Places = new Dictionary<string, PlaceMark>();
            if (data.PvpPairs == null) data.PvpPairs = new Dictionary<string, DateTime>();
            if (data.ContractPairs == null) data.ContractPairs = new Dictionary<string, DateTime>();
            if (data.Lieges == null) data.Lieges = new Dictionary<string, string>();
            if (data.CreditedContracts == null) data.CreditedContracts = new List<int>();
            if (data.EventCredits == null) data.EventCredits = new List<string>();
            var houses = new Dictionary<string, HouseQ>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, HouseQ> kv in data.Houses)
                if (kv.Value != null && !string.IsNullOrEmpty(kv.Key) && !houses.ContainsKey(kv.Key)) houses[kv.Key] = kv.Value;
            data.Houses = houses;
            foreach (HouseQ h in data.Houses.Values)
            {
                if (h.Goals == null) h.Goals = new List<HouseGoalState>();
                h.Goals.RemoveAll(delegate(HouseGoalState g) { return g == null || g.GoalId == null; });
                foreach (HouseGoalState g in h.Goals) if (g.Contrib == null) g.Contrib = new Dictionary<string, int>();
            }
            var places2 = new Dictionary<string, PlaceMark>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, PlaceMark> kv in data.Places) if (kv.Value != null && !string.IsNullOrEmpty(kv.Key)) places2[kv.Key] = kv.Value;
            data.Places = places2;
            var drop = new List<string>();
            foreach (KeyValuePair<string, PlayerQ> kv in data.Players)
            {
                PlayerQ p = kv.Value;
                if (p == null || !IsSteamId(kv.Key)) { drop.Add(kv.Key); continue; }
                FixPlayer(p);
            }
            foreach (string k in drop) data.Players.Remove(k);
        }

        private static void FixPlayer(PlayerQ p)
        {
            if (p.Name == null) p.Name = "";
            if (p.Daily == null) p.Daily = new List<Slot>();
            if (p.Weekly == null) p.Weekly = new List<Slot>();
            p.Daily.RemoveAll(delegate(Slot s) { return s == null; });
            p.Weekly.RemoveAll(delegate(Slot s) { return s == null; });
            foreach (Slot s in p.Daily) FixSlot(s);
            foreach (Slot s in p.Weekly) FixSlot(s);
            if (p.LastDaily == null) p.LastDaily = new List<string>();
            if (p.Story == null) p.Story = new StoryState();
            if (p.Story.Progress == null) p.Story.Progress = new List<int>();
            if (p.Story.Seen == null) p.Story.Seen = new List<string>();
            if (p.Story.Finished == null) p.Story.Finished = new List<string>();
            if (p.Ach == null) p.Ach = new Dictionary<string, AchState>();
            var dropA = new List<string>();
            foreach (KeyValuePair<string, AchState> kv in p.Ach) if (kv.Value == null) dropA.Add(kv.Key);
            foreach (string k in dropA) p.Ach.Remove(k);
            if (p.Owed == null) p.Owed = new List<OwedItem>();
            p.Owed.RemoveAll(delegate(OwedItem o) { return o == null || string.IsNullOrEmpty(o.Item) || o.Amount <= 0; });
            if (p.PendingMarks < 0) p.PendingMarks = 0;
            if (p.Counts == null) p.Counts = new Dictionary<string, int>();
            if (p.Credit == null) p.Credit = new Dictionary<string, int>();
            if (p.VisitedToday == null) p.VisitedToday = new List<string>();
            if (double.IsNaN(p.ActiveMinutes) || p.ActiveMinutes < 0) p.ActiveMinutes = 0;
            if (double.IsNaN(p.LifeMinutes) || p.LifeMinutes < 0) p.LifeMinutes = 0;
        }

        private static void FixSlot(Slot s)
        {
            if (s.Progress == null) s.Progress = new List<int>();
            if (s.Seen == null) s.Seen = new List<string>();
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            lastSave = Now();
        }

        private PlayerQ FindPlayer(string id)
        {
            PlayerQ p;
            return id != null && data != null && data.Players.TryGetValue(id, out p) ? p : null;
        }

        private PlayerQ GetPlayer(string id, string name, bool create)
        {
            PlayerQ p = FindPlayer(id);
            string clean = name != null ? Clean(name) : null;
            if (p == null)
            {
                if (!create || data == null || !IsSteamId(id)) return null;
                if (data.Players.Count >= config.MaxPlayers) Prune();
                p = new PlayerQ { Name = clean ?? id, FirstSeen = Now(), LastSeen = Now() };
                data.Players[id] = p;
                dirty = true;
            }
            else if (!string.IsNullOrEmpty(clean) && p.Name != clean) { p.Name = clean; dirty = true; }
            return p;
        }

        // Keeps the record bounded: the longest-unseen players with nothing owed go first.
        private void Prune()
        {
            var ids = new List<KeyValuePair<string, PlayerQ>>(data.Players);
            ids.Sort(delegate(KeyValuePair<string, PlayerQ> a, KeyValuePair<string, PlayerQ> b) { return a.Value.LastSeen.CompareTo(b.Value.LastSeen); });
            int remove = Math.Max(1, data.Players.Count - config.MaxPlayers + config.MaxPlayers / 20);
            foreach (KeyValuePair<string, PlayerQ> kv in ids)
            {
                if (remove <= 0) break;
                if (kv.Value.PendingMarks > 0 || kv.Value.Owed.Count > 0) continue;
                data.Players.Remove(kv.Key);
                remove--;
            }
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
        private const string ChatCommand = "F4C96D";
        private const string ChatMuted = "A3A6AD";

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

        private static string Ok(string s) { return "[" + ChatOk + "]" + s + "[FFFFFF]"; }
        private static string Muted(string s) { return "[" + ChatMuted + "]" + s + "[FFFFFF]"; }
        private static string Gold(string s) { return "[" + ChatGold + "]" + s + "[FFFFFF]"; }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Quests" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Help1", "[F4C96D]/quest[FFFFFF] your journal; [F4C96D]/quest log[FFFFFF] the full text of every task; [F4C96D]/quest story[FFFFFF] the tale of the season." },
                { "Help2", "  [F4C96D]/quest give[FFFFFF] hands in goods a task asks for; [F4C96D]/quest collect[FFFFFF] takes reward goods that did not fit in your packs." },
                { "Help3", "  [F4C96D]/quest abandon[FFFFFF] <slot> gives up a task (its slot stays empty until the reset); [F4C96D]/quest reroll[FFFFFF] <slot> draws another daily ({0} a day)." },
                { "Help4", "  [F4C96D]/quest house[FFFFFF] your house's goal this week; [F4C96D]/achievements[FFFFFF] your deeds, by kind and tier." },
                { "HelpAdmin", "  Stewards: [F4C96D]/quest admin[FFFFFF] status, reload, places, place set|clear, reset, complete, creatures, items." },
                { "Paused", "The quest-board is closed: its record could not be read. The stewards have been told." },
                { "Disabled", "The quest-board is closed by the realm's stewards." },
                { "NoPermission", "Only the realm's stewards may do that." },
                { "JournalHeader", "Your journal. The day turns in {0}, the week in {1}." },
                { "JournalDaily", "  Daily {0}: {1}" },
                { "JournalWeekly", "  Weekly {0}: {1}" },
                { "JournalStory", "  Story: {0}" },
                { "JournalHouse", "  House {0}: {1}" },
                { "JournalEmpty", "  The quest-board holds nothing for you yet." },
                { "JournalFoot", "  [F4C96D]/quest log[FFFFFF] the full text, [F4C96D]/quest give[FFFFFF] to hand in goods, [F4C96D]/achievements[FFFFFF] your deeds." },
                { "JournalOwed", "  {0} reward(s) wait for room in your packs: [F4C96D]/quest collect[FFFFFF]." },
                { "JournalMarksHeld", "  {0} marks are held for you until the treasury can pay them." },
                { "SlotDone", "done" },
                { "SlotAbandoned", "abandoned" },
                { "SlotGone", "withdrawn by the stewards" },
                { "StoryWaitSeason", "the season's tale waits for the season to open" },
                { "StoryWaitDay", "the next chapter opens on day {0} of the season" },
                { "StoryComplete", "the tale of {0} is told; you saw it to its end" },
                { "StoryOff", "no tale is told this season" },
                { "HouseNone", "you are sworn to no house; [F4C96D]/house[FFFFFF] to found or join one" },
                { "HouseTooSmall", "a house needs {0} members before the realm sets it a weekly goal" },
                { "HouseGoalDone", "met this week" },
                { "LogHeader", "{0}: {1}" },
                { "LogText", "  {0}" },
                { "LogObjective", "  - {0}: {1}/{2}" },
                { "LogReward", "  Reward: {0}" },
                { "RewardMarks", "{0} marks" },
                { "RewardRenown", "renown" },
                { "RewardPoints", "{0} season points for your house" },
                { "RewardNothing", "the realm's thanks" },
                { "Progress", "{0}: {1} {2}/{3}" },
                { "Completed", "Task done: {0}. {1}" },
                { "CompletedReward", "  You receive {0}." },
                { "StepDone", "The tale moves on: {0} is done." },
                { "ChapterDone", "Chapter done: {0}." },
                { "StoryDone", "You have seen the tale of {0} to its end." },
                { "StoryNext", "  Next: {0}. [F4C96D]/quest story[FFFFFF] to read on." },
                { "AchUnlocked", "Deed recorded: {0} ({1}). {2}" },
                { "AchAnnounce", "{0} has earned the deed {1} ({2})." },
                { "StoryAnnounce", "{0} has seen the tale of {1} to its end." },
                { "StoryFirstAnnounce", "{0} is the first in the realm to see the tale of {1} to its end." },
                { "HouseGoalMet", "House {0} has met its goal for the week: {1}." },
                { "HouseGoalYou", "Your house met its goal: {0}. You receive {1}." },
                { "HouseGoalSmall", "Your house met its goal: {0}. Your share was too small for a reward this time." },
                { "HouseGoalNeedMore", "Your house has done the work of {0}, but the realm counts it only when {1} members have helped." },
                { "Abandoned", "You give up {0}. The slot stays empty until the reset." },
                { "AbandonAsk", "Give up {0}? Its progress is lost and the slot stays empty until the reset. Type [F4C96D]/quest abandon[FFFFFF] {1} confirm." },
                { "AbandonWhich", "Which task? [F4C96D]/quest abandon[FFFFFF] d1, d2, d3 for dailies, w1, w2 for weeklies. The story cannot be abandoned." },
                { "AbandonDone", "That task is done already; there is nothing to give up." },
                { "BadSlot", "There is no task in slot {0}. Slots: d1, d2, d3 (daily) and w1, w2 (weekly)." },
                { "Rerolled", "The board shows another task in slot {0}: {1}." },
                { "RerollNone", "You have drawn all {0} of today's rerolls." },
                { "RerollDoneSlot", "That task is done or abandoned; only an open daily can be redrawn." },
                { "RerollEmpty", "There is no other daily left to draw today." },
                { "RerollWhich", "Which daily? [F4C96D]/quest reroll[FFFFFF] d1, d2 or d3." },
                { "GiveNothing", "None of your tasks asks for goods you carry." },
                { "GiveList", "Goods your tasks ask for (type [F4C96D]/quest give all[FFFFFF] to hand them in):" },
                { "GiveLine", "  {0}: {1} wanted, you carry {2}" },
                { "GiveTaken", "You hand in {0} {1} for {2}." },
                { "GiveFailed", "The goods could not be taken from your packs. Nothing was counted." },
                { "GiveNoInventory", "Your packs cannot be reached right now; try again in a moment." },
                { "CollectNone", "No reward goods wait for you." },
                { "CollectGot", "You collect {0} {1}." },
                { "CollectFull", "{0} {1} still wait: make room in your packs, then [F4C96D]/quest collect[FFFFFF] again." },
                { "CollectUnknown", "{0} is not an item this realm knows; the stewards have been told." },
                { "OwedWaiting", "{0} reward(s) wait for you: [F4C96D]/quest collect[FFFFFF]." },
                { "MarksPaid", "The treasury pays you {0} marks for your deeds." },
                { "MarksHeld", "Your marks are held for now: {0}. They are paid as soon as the treasury can." },
                { "MarksHeldNew", "you have not yet played long enough in this realm" },
                { "MarksHeldAddress", "too many accounts from one hearth were paid today" },
                { "MarksHeldTreasury", "the treasury's reward purse is spent for today" },
                { "StoryHeader", "{0}, the tale of Season {1}." },
                { "StoryPrologue", "  {0}" },
                { "StoryChapter", "Act {0}: {1}" },
                { "StoryIntro", "  {0}" },
                { "StoryStep", "  Now: {0}. {1}" },
                { "HouseHeader", "House {0}, this week:" },
                { "HouseLine", "  {0} - {1}/{2}{3}" },
                { "HouseText", "  {0}" },
                { "HouseTop", "  Most help: {0}" },
                { "HouseReward", "  When met: {0} season points for the house and up to {1} marks for each who helped." },
                { "AchHeader", "Your deeds: {0} of {1} recorded, {2} tiers in all." },
                { "AchCategory", "  {0}: {1}/{2} deeds, {3} tiers" },
                { "AchNear", "  Closest: {0}" },
                { "AchFoot", "  [F4C96D]/achievements[FFFFFF] <kind> lists one kind: survival, war, politics, economy, exploration. [F4C96D]/achievements top[FFFFFF] the realm's best." },
                { "AchListHeader", "Deeds of {0}:" },
                { "AchLine", "  {0} ({1}): {2} - {3}" },
                { "AchDetail", "{0} ({1}): {2}" },
                { "AchDetailTier", "  {0}: {1}{2}" },
                { "AchUnknown", "No deed or kind is called '{0}'." },
                { "AchTopHeader", "The realm's most accomplished:" },
                { "AchTopLine", "  {0}. {1} - {2} tiers" },
                { "AchTopNone", "  No deeds are recorded yet." },
                { "AchOff", "Deeds are not recorded on this server." },
                { "Category.survival", "Survival" },
                { "Category.war", "War" },
                { "Category.politics", "Politics" },
                { "Category.economy", "Economy" },
                { "Category.exploration", "Exploration" },
                { "NotYet", "not yet" },
                { "Unmarked", "not yet marked by the stewards" },
                { "FirstHint", "Tasks wait for you on the quest-board: [F4C96D]/quest[FFFFFF]. Your deeds are kept in [F4C96D]/achievements[FFFFFF]." },
                { "NewDay", "A new day on the quest-board: {0} new tasks. [F4C96D]/quest[FFFFFF] to see them." },
                { "TaleBegins", "The tale of the season begins. Act {0}: {1}. [F4C96D]/quest story[FFFFFF] to read it." },
                { "ChapterOpens", "A new chapter of the tale opens. Act {0}: {1}. [F4C96D]/quest story[FFFFFF] to read on." },
                { "PopupJournal", "Your Journal" },
                { "PopupDone", "Task Done" },
                { "PopupDeed", "Deed Recorded" },
                { "PopupStory", "The Hollow Crown" },
                { "PopupDeeds", "Your Deeds" },
                { "PopupButton", "Onward" },
                { "AdminStatus", "Content: {0} daily, {1} weekly, {2} story chapters, {3} deeds, {4} house goals, {5} places ({6} marked)." },
                { "AdminFiles", "  Files: {0}" },
                { "AdminProblem", "  Problem: {0}" },
                { "AdminFeeds", "  Feeds: {0}" },
                { "AdminTotals", "  {0} players, {1} marks owed, {2} reward goods owed, season day {3}." },
                { "AdminReloaded", "Quest content read again." },
                { "AdminPlaceSet", "{0} is marked here (radius {1} m)." },
                { "AdminPlaceCleared", "{0} is no longer marked." },
                { "AdminPlaceUnknown", "No place is called '{0}'. [F4C96D]/quest admin places[FFFFFF] lists them." },
                { "AdminPlaceLine", "  {0} ({1}): {2}" },
                { "AdminPlaceUnset", "not marked" },
                { "AdminNoPosition", "Your position cannot be read right now." },
                { "AdminReset", "{0}'s {1} quests are reset." },
                { "AdminCompleted", "{0} completed for {1}." },
                { "AdminUnknownQuest", "No open task called '{0}' for {1}." },
                { "AdminPlayerNotFound", "No player matches that name (online, or once seen by the quest-board)." },
                { "AdminCreatures", "Creature deaths seen lately: {0}" },
                { "AdminCreaturesNone", "No creature death has been seen since the last reload." },
                { "AdminItems", "Items matching '{0}': {1}" },
                { "AdminItemsNone", "No item matches '{0}'." },
                { "AdminHelp", "[F4C96D]/quest admin[FFFFFF] status | reload | places | place set <id> [radius] | place clear <id> | reset <player> [daily|weekly|story|all] | complete <player> <quest> | creatures | items <word>" },
                { "Visited", "You reach {0}." },
                { "NoContent", "The quest-board is not posted yet. The stewards must copy the quest content into oxide/data/RealmQuests." }
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
            catch (FormatException) { return m; }
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Completed", "StepDone", "ChapterDone", "StoryDone", "AchUnlocked", "HouseGoalYou", "Rerolled", "GiveTaken", "CollectGot",
            "MarksPaid", "AdminReloaded", "AdminPlaceSet", "AdminReset", "AdminCompleted", "Visited"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "AbandonAsk", "Abandoned", "CollectFull", "OwedWaiting", "MarksHeld", "HouseGoalNeedMore", "HouseGoalSmall", "NoContent", "AdminPlaceCleared"
        };

        private static string ToneOf(string key)
        {
            if (OkKeys.Contains(key)) return ChatOk;
            if (WarnKeys.Contains(key)) return ChatWarn;
            return ChatGold;
        }

        private void Reply(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendMessage(Styled(Msg("Speaker", player), ToneOf(key), Fmt(key, player, args)));   // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
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
            catch (Exception ex) { PrintWarning("Config unreadable (" + ex.Message + "); defaults used for this run."); config = null; }
            ClampConfig();
            Config.WriteObject(config, true);                // writes newly added keys and clamped values
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            LoadContent();
        }

        private void OnServerInitialized()
        {
            if (loadFailed || initialized) return;           // re-sent on hot load (doc 2.1): keep it idempotent
            initialized = true;
            try
            {
                craftSubscriber = new EventSubscriber<ItemCrafterCraftEvent>(OnCraftRequested);
                EventManager.Subscribe<ItemCrafterCraftEvent>(craftSubscriber, EventHandlerOrder.VeryLate);
                craftItemSubscriber = new EventSubscriber<ItemCrafterItemEvent>(OnCraftProduct);
                EventManager.Subscribe<ItemCrafterItemEvent>(craftItemSubscriber, EventHandlerOrder.VeryLate);
            }
            catch (Exception ex)
            {
                PrintWarning("Could not watch crafting (" + ex.Message + "); craft tasks will not move.");
                craftSubscriber = null;
                craftItemSubscriber = null;
            }
            CheckItems();
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            if (visitTimer != null && !visitTimer.Destroyed) visitTimer.Destroy();
            if (config.VisitSeconds < config.TickSeconds) visitTimer = timer.Every(config.VisitSeconds, SafeVisits);
            foreach (Player p in OnlinePlayers()) Seen(p);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            try
            {
                if (craftSubscriber != null) EventManager.Unsubscribe<ItemCrafterCraftEvent>(craftSubscriber);
                if (craftItemSubscriber != null) EventManager.Unsubscribe<ItemCrafterItemEvent>(craftItemSubscriber);
            }
            catch (Exception ex) { PrintWarning("Unsubscribe failed: " + ex.Message); }
            craftSubscriber = null;
            craftItemSubscriber = null;
            SaveData();
        }

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            try
            {
                try
                {
                    string ip = player.Connection != null ? player.Connection.IpAddress : null;   // [ASM Connection.IpAddress]
                    if (!string.IsNullOrEmpty(ip)) addressOf[player.Id] = Hash(data.Salt + "|" + ip);
                }
                catch (Exception) { }
                PlayerQ p = Seen(player);
                if (p == null) return;
                if (p.Owed.Count > 0) Reply(player, "OwedWaiting", p.Owed.Count);
                PayMarks(player.Id.ToString(), p, player);
            }
            catch (Exception ex) { PrintError("Connect handling failed: " + ex.Message); }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null) return;
            samples.Remove(player.Id);
            addressOf.Remove(player.Id);
            if (dirty) SaveData();
        }

        // A player the board has seen: name, last seen, the day and week rolled over, the house watched.
        private PlayerQ Seen(Player player)
        {
            if (data == null || player == null || player.IsServer) return null;
            string id = player.Id.ToString();
            bool known = FindPlayer(id) != null;
            PlayerQ p = GetPlayer(id, player.Name, true);
            if (p == null) return null;
            p.LastSeen = Now();
            string before = p.DayKey;
            Roll(id, p);
            if (!known && HasContent()) Reply(player, "FirstHint");
            else if (before != null && before != p.DayKey && config.DailiesEnabled && dailies.Count > 0) Reply(player, "NewDay", p.Daily.Count);
            return p;
        }

        // A chapter of the tale that has opened since the player last looked: told once, with its intro.
        private void ChapterNews(Player player, PlayerQ p)
        {
            if (story == null || !config.StoryEnabled) return;
            ChapterDef ch;
            string wait;
            QuestDef step = CurrentStep(p, out ch, out wait);
            if (step == null || ch == null || ch.Id == p.Story.IntroShown) return;
            bool firstAct = p.Story.IntroShown == null && p.Story.Finished.Count == 0;
            p.Story.IntroShown = ch.Id;
            dirty = true;
            Reply(player, firstAct ? "TaleBegins" : "ChapterOpens", ch.Act, ch.Title);
            if (!firstAct && PopupsFor(player))
                ShowInfoPopup(player, story.Title, Fmt("StoryChapter", player, ch.Act, ch.Title) + "\n\n" + (ch.Intro ?? "") + "\n\n" + step.Title + ": " + step.Text, Msg("PopupButton", player));
        }

        #endregion

        #region Time

        // The quest day turns at ResetHourUtc; the week turns on the Monday of that hour.
        private string DayKey(DateTime now)
        {
            return now.AddHours(-config.ResetHourUtc).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        private string WeekKey(DateTime now)
        {
            DateTime d = now.AddHours(-config.ResetHourUtc).Date;
            int back = ((int)d.DayOfWeek + 6) % 7;
            return d.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }

        private TimeSpan UntilDayTurns(DateTime now)
        {
            DateTime start = now.AddHours(-config.ResetHourUtc).Date.AddHours(config.ResetHourUtc);
            return start.AddDays(1) - now;
        }

        private TimeSpan UntilWeekTurns(DateTime now)
        {
            DateTime d = now.AddHours(-config.ResetHourUtc).Date;
            int back = ((int)d.DayOfWeek + 6) % 7;
            return d.AddDays(-back).AddHours(config.ResetHourUtc).AddDays(7) - now;
        }

        private static string Dur(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "a moment";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h " + t.Minutes + "m";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private DateTime Now()
        {
            return clock();
        }

        #endregion

        #region Assignment: the day, the week, the story, the house

        // Rolls the player's day and week over and draws their tasks. The draw is seeded by the player and the day,
        // so logging out and in, or a server restart, shows the same board.
        private void Roll(string id, PlayerQ p)
        {
            DateTime now = Now();
            string day = DayKey(now);
            string week = WeekKey(now);
            if (p.CreditDay != day)
            {
                p.CreditDay = day;
                p.Credit.Clear();
                p.VisitedToday.Clear();
                dirty = true;
            }
            if (p.DayKey != day)
            {
                var last = new List<string>();
                foreach (Slot s in p.Daily) if (s.QuestId != null) last.Add(s.QuestId);
                p.LastDaily = last;
                p.DayKey = day;
                p.Rerolls = 0;
                p.Daily = new List<Slot>();
                dirty = true;
            }
            if (p.WeekKey != week)
            {
                p.WeekKey = week;
                p.Weekly = new List<Slot>();
                dirty = true;
            }
            // Fill empty places on the board (a new day, or content that arrived later). Abandoned slots stay.
            if (config.DailiesEnabled && p.Daily.Count < config.DailyCount)
                foreach (string q in Draw(dailyOrder, dailies, p, id + "|daily|" + day + "|" + p.Daily.Count, config.DailyCount - p.Daily.Count, SlotIds(p.Daily), p.LastDaily))
                { p.Daily.Add(NewSlot(q)); dirty = true; }
            if (config.WeekliesEnabled && p.Weekly.Count < config.WeeklyCount)
                foreach (string q in Draw(weeklyOrder, weeklies, p, id + "|weekly|" + week + "|" + p.Weekly.Count, config.WeeklyCount - p.Weekly.Count, SlotIds(p.Weekly), null))
                { p.Weekly.Add(NewSlot(q)); dirty = true; }
        }

        private static Slot NewSlot(string questId)
        {
            return new Slot { QuestId = questId };
        }

        private static List<string> SlotIds(List<Slot> slots)
        {
            var ids = new List<string>();
            foreach (Slot s in slots) if (s.QuestId != null) ids.Add(s.QuestId);
            return ids;
        }

        // Weighted draw without replacement from a seeded stream. Quests the player cannot take yet are left out;
        // yesterday's quests are avoided while the pool is big enough.
        private List<string> Draw(List<string> order, Dictionary<string, QuestDef> pool, PlayerQ p, string seedKey, int count, List<string> exclude, List<string> avoid)
        {
            var picked = new List<string>();
            if (count <= 0) return picked;
            var cands = new List<QuestDef>();
            foreach (string qid in order)
            {
                QuestDef q;
                if (!pool.TryGetValue(qid, out q)) continue;
                if (exclude != null && exclude.Contains(q.Id)) continue;
                if (q.NeedsHouse && string.IsNullOrEmpty(p.House)) continue;
                if (q.MinActiveMinutes > p.ActiveMinutes) continue;
                if (!Drawable(q)) continue;
                cands.Add(q);
            }
            if (avoid != null && avoid.Count > 0)
            {
                var fresh = cands.FindAll(delegate(QuestDef q) { return !avoid.Contains(q.Id); });
                if (fresh.Count >= count) cands = fresh;
            }
            uint state = Seed(data.Salt + "|" + seedKey);
            while (picked.Count < count && cands.Count > 0)
            {
                int total = 0;
                foreach (QuestDef q in cands) total += q.Weight;
                int r = (int)(NextRand(ref state) % (uint)Math.Max(1, total));
                int i = 0;
                for (; i < cands.Count - 1; i++) { r -= cands[i].Weight; if (r < 0) break; }
                picked.Add(cands[i].Id);
                cands.RemoveAt(i);
            }
            return picked;
        }

        private static uint Seed(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h == 0 ? 1u : h;
        }

        // xorshift32: the same stream on every runtime, unlike System.Random.
        private static uint NextRand(ref uint state)
        {
            uint x = state;
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            state = x == 0 ? 1u : x;
            return state;
        }

        private bool StoryRunning()
        {
            if (story == null || !config.StoryEnabled) return false;
            if (!config.StoryRequiresSeason || RealmSeasons == null) return true;
            object n = RealmSeasons.Call("GetSeasonNumber");
            return n is int && (int)n == story.Season;
        }

        // Days since the season began (RealmSeasons.json, read only), or -1 when unknown (then no chapter waits).
        private int SeasonDay()
        {
            DateTime now = Now();
            if (seasonDayCache != -2 && (now - seasonDayRead).TotalSeconds < 60) return seasonDayCache;
            seasonDayRead = now;
            seasonDayCache = -1;
            if (RealmSeasons == null || !Interface.Oxide.DataFileSystem.ExistsDatafile(SeasonsFile)) return seasonDayCache;
            try
            {
                SeasonLite s = Interface.Oxide.DataFileSystem.ReadObject<SeasonLite>(SeasonsFile);
                if (s != null && s.Active && s.StartedAt > DateTime.MinValue && s.StartedAt <= now)
                    seasonDayCache = (int)Math.Floor((now - s.StartedAt).TotalDays);
            }
            catch (Exception ex) { feedStatus["season"] = "unreadable " + SeasonsFile + ".json (" + ex.Message + ")"; }
            return seasonDayCache;
        }

        private class SeasonLite
        {
            public int Number;
            public bool Active;
            public DateTime StartedAt;
        }

        // The step the player is on, or null (with the reason) when the tale waits or is done.
        private QuestDef CurrentStep(PlayerQ p, out ChapterDef chapter, out string wait)
        {
            chapter = null;
            wait = null;
            if (story == null) { wait = "off"; return null; }
            // A new season's tale starts afresh; a record without a season is taken to be this one's.
            if (p.Story.Season == 0) p.Story.Season = story.Season;
            else if (p.Story.Season != story.Season) { p.Story = new StoryState { Season = story.Season }; dirty = true; }
            if (p.Story.Complete) { wait = "complete"; return null; }
            if (!StoryRunning()) { wait = "season"; return null; }
            int day = config.StoryTimeGates ? SeasonDay() : -1;
            foreach (ChapterDef c in story.Chapters)
            {
                foreach (QuestDef s in c.Steps)
                {
                    if (p.Story.Finished.Contains(s.Id)) continue;
                    if (day >= 0 && c.UnlockDay > day) { chapter = c; wait = "day"; return null; }
                    chapter = c;
                    if (p.Story.StepId != s.Id)
                    {
                        p.Story.StepId = s.Id;
                        p.Story.Progress = new List<int>();
                        p.Story.Seen = new List<string>();
                        dirty = true;
                    }
                    return s;
                }
            }
            return null;
        }

        private HouseQ HouseState(string house)
        {
            if (string.IsNullOrEmpty(house) || data == null) return null;
            HouseQ h;
            if (!data.Houses.TryGetValue(house, out h))
            {
                if (data.Houses.Count > 2000) return null;
                h = new HouseQ { Name = house };
                data.Houses[house] = h;
            }
            string week = WeekKey(Now());
            if (h.WeekKey != week || (h.Goals.Count < config.HouseGoalCount && houseGoals.Count > h.Goals.Count))
            {
                if (h.WeekKey != week) h.Goals = new List<HouseGoalState>();
                h.WeekKey = week;
                var have = new List<string>();
                foreach (HouseGoalState g in h.Goals) have.Add(g.GoalId);
                uint state = Seed(data.Salt + "|house|" + house.ToLowerInvariant() + "|" + week + "|" + h.Goals.Count);
                var cands = new List<HouseGoalDef>();
                foreach (string gid in houseGoalOrder) if (!have.Contains(gid)) cands.Add(houseGoals[gid]);
                while (h.Goals.Count < config.HouseGoalCount && cands.Count > 0)
                {
                    int total = 0;
                    foreach (HouseGoalDef g in cands) total += g.Weight;
                    int r = (int)(NextRand(ref state) % (uint)Math.Max(1, total));
                    int i = 0;
                    for (; i < cands.Count - 1; i++) { r -= cands[i].Weight; if (r < 0) break; }
                    h.Goals.Add(new HouseGoalState { GoalId = cands[i].Id });
                    cands.RemoveAt(i);
                }
                dirty = true;
            }
            return h;
        }

        private int HouseSize(string house)
        {
            if (RealmHouses == null || house == null) return 0;
            List<string> m = RealmHouses.Call("GetMembers", house) as List<string>;
            return m != null ? m.Count : 0;
        }

        #endregion

        #region Progress: one engine for tasks, the story, deeds and house goals

        // Something a player did, counted once for every task, story step, deed and house goal that asks for it.
        // Returns true when anything moved.
        private bool Record(string id, string name, string type, string subject, int amount, string distinct)
        {
            return Advance(id, name, type, subject ?? "", amount, distinct, false);
        }

        // A level the realm already keeps (renown, marks, titles, house, a life's length): progress follows it upwards.
        private bool RecordState(string id, string type, string subject, long value)
        {
            return Advance(id, null, type, subject ?? "", value, null, true);
        }

        private bool Advance(string id, string name, string type, string subject, long amount, string distinct, bool state)
        {
            if (!config.Enabled || loadFailed || data == null || amount <= 0 || depth > 10) return false;   // chains are finite (tiers end); this is a backstop
            PlayerQ p = name != null ? GetPlayer(id, name, true) : FindPlayer(id);
            if (p == null) return false;
            depth++;
            bool moved = false;
            try
            {
                if (depth == 1) Roll(id, p);
                Player online = OnlineById(id);
                if (config.DailiesEnabled)
                    foreach (Slot s in new List<Slot>(p.Daily)) moved |= AdvanceSlot(id, p, online, s, dailies, KDaily, type, subject, amount, distinct, state);
                if (config.WeekliesEnabled)
                    foreach (Slot s in new List<Slot>(p.Weekly)) moved |= AdvanceSlot(id, p, online, s, weeklies, KWeekly, type, subject, amount, distinct, state);
                moved |= AdvanceStory(id, p, online, type, subject, amount, distinct, state);
                if (config.AchievementsEnabled) moved |= AdvanceAchievements(id, p, online, type, subject, amount, distinct, state);
                if (config.HouseGoalsEnabled && !state && type != TVisit) moved |= AdvanceHouse(id, p, online, type, subject, (int)Math.Min(amount, int.MaxValue));
                if (moved) dirty = true;
            }
            finally { depth--; }
            return moved;
        }

        private static bool TargetMatch(string type, string target, string subject)
        {
            string t = target.Trim();
            if (type == TSlayCreature) return subject.IndexOf(t.TrimStart('~'), StringComparison.OrdinalIgnoreCase) >= 0;
            if (t.StartsWith("~")) return t.Length > 1 && subject.IndexOf(t.Substring(1), StringComparison.OrdinalIgnoreCase) >= 0;
            if (string.Equals(t, subject, StringComparison.OrdinalIgnoreCase)) return true;
            // Goods may be named by the game's ResourceType name ("WolfPelt"); the subject is the item's display name.
            return type == TDeliver && string.Equals(ItemName(t), subject, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(ObjectiveDef o, string type, string subject)
        {
            if (o == null || o.Type != type) return false;
            if (type == TTitle) return true;                  // titles: the value is worked out per objective
            if (o.Targets == null || o.Targets.Count == 0) return true;
            foreach (string t in o.Targets) if (TargetMatch(type, t, subject)) return true;
            return false;
        }

        // Titles held (comma list in subject) that this objective counts.
        private static long TitleValue(ObjectiveDef o, string subject)
        {
            if (string.IsNullOrEmpty(subject)) return 0;
            string[] held = subject.Split(',');
            if (o.Targets == null || o.Targets.Count == 0) return held.Length;
            long n = 0;
            foreach (string h in held) foreach (string t in o.Targets) if (string.Equals(h.Trim(), t.Trim(), StringComparison.OrdinalIgnoreCase)) { n++; break; }
            return n;
        }

        // Moves one objective. progress and seen belong to the slot (or story step, or deed) that holds it.
        private static bool Step(ObjectiveDef o, int index, List<int> progress, List<string> seen, string type, string subject, long amount, string distinct, bool state)
        {
            if (!Matches(o, type, subject)) return false;
            while (progress.Count <= index) progress.Add(0);
            int before = progress[index];
            if (before >= o.Count) return false;
            if (state)
            {
                long v = type == TTitle ? TitleValue(o, subject) : amount;
                if (v > before) progress[index] = (int)Math.Min(o.Count, v);
            }
            else if (o.Distinct)
            {
                string key = index + "|" + (distinct ?? subject).ToLowerInvariant();
                if (seen.Contains(key)) return false;
                if (seen.Count < 400) seen.Add(key);
                progress[index] = Math.Min(o.Count, before + 1);
            }
            else progress[index] = (int)Math.Min(o.Count, before + amount);
            return progress[index] != before;
        }

        // waive: a visit to an unmarked place counts as done (story steps only: a daily or weekly that needs a place is
        // never drawn while it is unmarked, and a place cleared later must not pay out the tasks already drawn).
        private bool AllDone(QuestDef q, List<int> progress, bool waive)
        {
            int done = 0;
            for (int i = 0; i < q.Objectives.Count; i++)
                if ((i < progress.Count && progress[i] >= q.Objectives[i].Count) || (waive && Waived(q.Objectives[i]))) done++;
            return q.AnyOne ? done > 0 : done == q.Objectives.Count;
        }

        // A visit to places no steward has marked yet cannot be made; in the story it is waived so the tale never stalls.
        private bool Waived(ObjectiveDef o)
        {
            if (o.Type != TVisit || data == null) return false;
            if (o.Targets.Count == 0) return data.Places.Count < o.Count;
            int marked = 0;
            foreach (string t in o.Targets) if (data.Places.ContainsKey(t)) marked++;
            return marked < o.Count;
        }

        // Daily and weekly tasks that need an unmarked place are not drawn at all (no free rewards).
        private bool Drawable(QuestDef q)
        {
            foreach (ObjectiveDef o in q.Objectives) if (Waived(o)) return false;
            return true;
        }

        private bool AdvanceSlot(string id, PlayerQ p, Player online, Slot s, Dictionary<string, QuestDef> pool, string kind, string type, string subject, long amount, string distinct, bool state)
        {
            QuestDef q;
            if (s.Done || s.Abandoned || s.QuestId == null || !pool.TryGetValue(s.QuestId, out q)) return false;
            bool moved = false;
            for (int i = 0; i < q.Objectives.Count; i++)
                if (Step(q.Objectives[i], i, s.Progress, s.Seen, type, subject, amount, distinct, state))
                {
                    moved = true;
                    NoticeProgress(online, kind + q.Id + i, q.Title, q.Objectives[i], s.Progress[i]);
                }
            if (moved && AllDone(q, s.Progress, false)) CompleteSlot(id, p, online, s, q, kind);
            return moved;
        }

        private void NoticeProgress(Player online, string key, string title, ObjectiveDef o, int now)
        {
            if (online == null) return;
            if (now < o.Count && !Throttle(online.Id + "|" + key, config.ProgressNoticeSeconds)) return;
            Reply(online, "Progress", title, o.Text, now, o.Count);
        }

        private void CompleteSlot(string id, PlayerQ p, Player online, Slot s, QuestDef q, string kind)
        {
            s.Done = true;
            Bump(p, kind);
            string period = kind == KDaily ? p.DayKey : p.WeekKey;
            string reward = GiveReward(id, p, q.Reward, kind + ":" + q.Id + ":" + period, q.Title, kind == KDaily ? "quest_daily" : "quest_weekly");
            Puts(p.Name + " (" + id + ") completed " + kind + " " + q.Id);
            if (online != null)
            {
                Reply(online, "Completed", q.Title, q.Done ?? "");
                Reply(online, "CompletedReward", reward);
                if (config.PopupOnComplete && PopupsFor(online))
                    ShowInfoPopup(online, Msg("PopupDone", online), q.Title + "\n\n" + (q.Done ?? "") + "\n\n" + Fmt("CompletedReward", online, reward).Trim(), Msg("PopupButton", online));
            }
            Record(id, null, TQuests, kind, 1, null);
        }

        private bool AdvanceStory(string id, PlayerQ p, Player online, string type, string subject, long amount, string distinct, bool state)
        {
            ChapterDef ch;
            string wait;
            QuestDef q = CurrentStep(p, out ch, out wait);
            if (q == null) return false;
            bool moved = false;
            for (int i = 0; i < q.Objectives.Count; i++)
                if (Step(q.Objectives[i], i, p.Story.Progress, p.Story.Seen, type, subject, amount, distinct, state))
                {
                    moved = true;
                    NoticeProgress(online, "story" + q.Id + i, q.Title, q.Objectives[i], p.Story.Progress[i]);
                }
            if (moved && AllDone(q, p.Story.Progress, true)) CompleteStep(id, p, online, ch, q);
            return moved;
        }

        private void CompleteStep(string id, PlayerQ p, Player online, ChapterDef ch, QuestDef q)
        {
            p.Story.Finished.Add(q.Id);
            p.Story.StepId = null;
            p.Story.Progress = new List<int>();
            p.Story.Seen = new List<string>();
            Bump(p, KStory);
            string reward = GiveReward(id, p, q.Reward, "story:" + q.Id, q.Title, "quest_story");
            Puts(p.Name + " (" + id + ") finished story step " + q.Id);
            var popup = new StringBuilder();
            if (online != null)
            {
                Reply(online, "StepDone", q.Title);
                if (!string.IsNullOrEmpty(q.Done)) Reply(online, "LogText", q.Done);
                Reply(online, "CompletedReward", reward);
                popup.Append(q.Title).Append("\n\n").Append(q.Done ?? "").Append("\n\n").Append(Fmt("CompletedReward", online, reward).Trim());
            }
            bool chapterDone = true;
            foreach (QuestDef s in ch.Steps) if (!p.Story.Finished.Contains(s.Id)) { chapterDone = false; break; }
            if (chapterDone && !p.Story.Finished.Contains("chapter:" + ch.Id))
            {
                p.Story.Finished.Add("chapter:" + ch.Id);
                string cr = GiveReward(id, p, ch.Reward, "chapter:" + ch.Id, ch.Title, "none");
                if (online != null)
                {
                    Reply(online, "ChapterDone", ch.Title);
                    if (!string.IsNullOrEmpty(ch.Outro)) Reply(online, "LogText", ch.Outro);
                    if (ch.Reward != null) Reply(online, "CompletedReward", cr);
                    popup.Append("\n\n").Append(Fmt("ChapterDone", online, ch.Title)).Append("\n").Append(ch.Outro ?? "");
                }
            }
            bool allDone = true;
            foreach (ChapterDef c in story.Chapters) if (!p.Story.Finished.Contains("chapter:" + c.Id)) { allDone = false; break; }
            ChapterDef next = null;
            QuestDef n = null;
            if (allDone) FinishStory(id, p, online, popup);
            else
            {
                string wait;
                n = CurrentStep(p, out next, out wait);
                if (online != null && n != null && next != null && next.Id != ch.Id)
                {
                    // A new act opens: its intro, in chat and in the window.
                    Reply(online, "StoryChapter", next.Act, next.Title);
                    if (!string.IsNullOrEmpty(next.Intro)) Reply(online, "StoryIntro", next.Intro);
                    popup.Append("\n\n").Append(Fmt("StoryChapter", online, next.Act, next.Title)).Append("\n").Append(next.Intro ?? "");
                    p.Story.IntroShown = next.Id;
                }
                if (online != null && n != null) Reply(online, "StoryNext", n.Title);
                else if (online != null && wait == "day" && next != null) Reply(online, "LogText", Fmt("StoryWaitDay", online, next.UnlockDay + 1));
            }
            if (online != null && config.PopupOnComplete && PopupsFor(online)) ShowInfoPopup(online, story.Title, popup.ToString(), Msg("PopupButton", online));
            Record(id, null, TQuests, KStory, 1, null);
            // The next step may already be done (a waived visit, an AnyOne step): close it too, a few at most.
            if (n != null && storyChain < 5 && p.Story.StepId == n.Id && AllDone(n, p.Story.Progress, true))
            {
                storyChain++;
                try { CompleteStep(id, p, online, next, n); }
                finally { storyChain--; }
            }
        }

        private void FinishStory(string id, PlayerQ p, Player online, StringBuilder popup)
        {
            p.Story.Complete = true;
            p.Story.CompletedAt = Now();
            string reward = GiveReward(id, p, story.Reward, "story:complete:" + story.Season, story.Title, "story_complete");
            bool first = string.IsNullOrEmpty(data.FirstStoryName) || data.FirstStorySeason != story.Season;
            if (first) { data.FirstStoryName = p.Name; data.FirstStorySeason = story.Season; }
            if (online != null)
            {
                Reply(online, "StoryDone", story.Title);
                if (!string.IsNullOrEmpty(story.Epilogue)) Reply(online, "LogText", story.Epilogue);
                Reply(online, "CompletedReward", reward);
                popup.Append("\n\n").Append(story.Epilogue ?? "");
            }
            DateTime now = Now();
            if (config.AnnounceAchievements && UnderHourlyCap(announceTimes, config.MaxAnnouncementsPerHour, now))
                Herald(Fmt(first ? "StoryFirstAnnounce" : "StoryAnnounce", null, p.Name, story.Title));
            if (config.ChronicleStory && UnderHourlyCap(chronicleTimes, config.MaxChroniclePerHour, now))
                Chronicle(StoryChronicleType, p.Name + (first ? " is the first to see " : " sees ") + story.Title + " to its end",
                    p.Name + " has followed the tale of " + story.Title + " through every act" + (first ? ", the first in the realm to do so." : "."), new[] { p.Name });
        }

        private bool AdvanceAchievements(string id, PlayerQ p, Player online, string type, string subject, long amount, string distinct, bool state)
        {
            bool moved = false;
            foreach (AchievementDef a in achievements)
            {
                ObjectiveDef o = a.Objective;
                if (!Matches(o, type, subject)) continue;
                AchState st;
                if (!p.Ach.TryGetValue(a.Id, out st)) { st = new AchState(); p.Ach[a.Id] = st; }
                long before = st.Count;
                if (state)
                {
                    long v = type == TTitle ? TitleValue(o, subject) : amount;
                    if (v > st.Count) st.Count = v;
                }
                else if (o.Distinct)
                {
                    if (st.Seen == null) st.Seen = new List<string>();
                    string key = (distinct ?? subject).ToLowerInvariant();
                    if (st.Seen.Contains(key)) continue;
                    if (st.Seen.Count < 400) st.Seen.Add(key);
                    st.Count++;
                }
                else st.Count += amount;
                if (st.Count == before) continue;
                moved = true;
                while (st.Tier < a.Tiers.Count && st.Count >= a.Tiers[st.Tier].Count)
                {
                    st.Tier++;
                    UnlockTier(id, p, online, a, st.Tier - 1);
                }
            }
            return moved;
        }

        private string TierName(int index)
        {
            return index < config.TierNames.Count ? config.TierNames[index] : "Tier " + (index + 1);
        }

        private void UnlockTier(string id, PlayerQ p, Player online, AchievementDef a, int tier)
        {
            Bump(p, "achievements");
            TierDef t = a.Tiers[tier];
            bool gold = tier >= 2;
            string reward = GiveReward(id, p, t.Reward, "ach:" + a.Id + ":" + tier, a.Name + " (" + TierName(tier) + ")", gold ? "achievement_gold" : "achievement");
            Puts(p.Name + " (" + id + ") earned " + a.Id + " tier " + (tier + 1));
            if (online != null)
            {
                Reply(online, "AchUnlocked", a.Name, TierName(tier), a.Text);
                Reply(online, "CompletedReward", reward);
                if (config.PopupOnComplete && PopupsFor(online))
                    ShowInfoPopup(online, Msg("PopupDeed", online), a.Name + " (" + TierName(tier) + ")\n\n" + a.Text + "\n\n" + Fmt("CompletedReward", online, reward).Trim(), Msg("PopupButton", online));
            }
            if (gold && config.AnnounceAchievements && UnderHourlyCap(announceTimes, config.MaxAnnouncementsPerHour, Now()))
                Herald(Fmt("AchAnnounce", null, p.Name, a.Name, TierName(tier)));
            Record(id, null, TAchievements, a.Category, 1, null);
        }

        private bool AdvanceHouse(string id, PlayerQ p, Player online, string type, string subject, int amount)
        {
            if (string.IsNullOrEmpty(p.House) || Array.IndexOf(HouseGoalTypes, type) < 0 || type == TDeliver) return false;
            if ((Now() - p.HouseSince).TotalHours < config.HouseMemberMinHours) return false;
            HouseQ h = HouseState(p.House);
            if (h == null) return false;
            bool moved = false;
            foreach (HouseGoalState g in h.Goals)
            {
                HouseGoalDef def;
                if (g.Done || !houseGoals.TryGetValue(g.GoalId, out def) || !Matches(def.Objective, type, subject)) continue;
                if (Contribute(id, p, online, h, g, def, amount) > 0) moved = true;
            }
            return moved;
        }

        // What one member may still add to a house goal (their share is capped).
        private int ShareLeft(string id, HouseGoalState g, HouseGoalDef def)
        {
            int mine;
            g.Contrib.TryGetValue(id, out mine);
            int cap = Math.Max(1, (int)((long)def.Objective.Count * config.HouseGoalMemberCapPercent / 100));
            return Math.Max(0, cap - mine);
        }

        private int Contribute(string id, PlayerQ p, Player online, HouseQ h, HouseGoalState g, HouseGoalDef def, int amount)
        {
            if (HouseSize(h.Name) < config.HouseGoalMinMembers && RealmHouses != null) return 0;
            int add = Math.Min(amount, ShareLeft(id, g, def));
            if (add <= 0) return 0;
            int mine;
            g.Contrib.TryGetValue(id, out mine);
            g.Contrib[id] = mine + add;
            int before = g.Progress;
            g.Progress = Math.Min(def.Objective.Count, g.Progress + add);
            dirty = true;
            if (online != null && g.Progress < def.Objective.Count && g.Progress != before)
                NoticeProgress(online, "house" + def.Id, def.Title, def.Objective, g.Progress);
            if (g.Progress >= def.Objective.Count)
            {
                int helpers = 0;
                foreach (int v in g.Contrib.Values) if (v > 0) helpers++;
                if (helpers >= config.HouseGoalMinContributors) CompleteHouseGoal(h, g, def);
                else if (online != null && Throttle(online.Id + "|housemore" + def.Id, 300))
                    Reply(online, "HouseGoalNeedMore", def.Title, config.HouseGoalMinContributors);
            }
            return add;
        }

        private void CompleteHouseGoal(HouseQ h, HouseGoalState g, HouseGoalDef def)
        {
            g.Done = true;
            dirty = true;
            if (config.SeasonPointRewards && def.Points > 0 && RealmSeasons != null)
                RealmSeasons.Call("AwardHouse", h.Name, def.Points, "Weekly goal: " + def.Title);
            if (config.AnnounceAchievements && UnderHourlyCap(announceTimes, config.MaxAnnouncementsPerHour, Now()))
                Herald(Fmt("HouseGoalMet", null, h.Name, def.Title));
            int floor = Math.Max(1, def.Objective.Count / 20);       // a real share: at least 5% of the goal
            foreach (KeyValuePair<string, int> kv in new Dictionary<string, int>(g.Contrib))
            {
                PlayerQ m = FindPlayer(kv.Key);
                if (m == null) continue;
                Player on = OnlineById(kv.Key);
                if (kv.Value < floor)
                {
                    if (on != null) Reply(on, "HouseGoalSmall", def.Title);
                    continue;
                }
                Bump(m, THouseGoal);
                var r = new RewardDef { Marks = def.Marks, Renown = string.IsNullOrEmpty(def.Renown) ? "" : def.Renown };
                string reward = GiveReward(kv.Key, m, r, "house:" + h.Name.ToLowerInvariant() + ":" + h.WeekKey + ":" + def.Id, def.Title, "house_goal");
                if (on != null) Reply(on, "HouseGoalYou", def.Title, reward);
                Record(kv.Key, null, THouseGoal, def.Id, 1, null);
            }
            Puts("House " + h.Name + " met its weekly goal " + def.Id);
        }

        private static void Bump(PlayerQ p, string key)
        {
            int n;
            p.Counts.TryGetValue(key, out n);
            p.Counts[key] = n + 1;
        }

        // Grants up to `want` of a daily allowance (pvp, creature, craft, build, contract); returns what was granted.
        private int TakeCredit(PlayerQ p, string key, int want, int cap)
        {
            string day = DayKey(Now());
            if (p.CreditDay != day) { p.CreditDay = day; p.Credit.Clear(); p.VisitedToday.Clear(); }
            int have;
            p.Credit.TryGetValue(key, out have);
            int grant = Math.Max(0, Math.Min(want, cap - have));
            if (grant > 0) { p.Credit[key] = have + grant; dirty = true; }
            return grant;
        }

        #endregion

        #region Rewards: marks, renown, season points, goods

        // Books a reward and returns how it reads ("15 marks, 2 Bandage, renown"). Marks and goods are owed first and
        // saved, then paid; renown and season points go straight to their plugins.
        private string GiveReward(string id, PlayerQ p, RewardDef r, string key, string label, string defaultDeed)
        {
            var parts = new List<string>();
            if (r != null)
            {
                int marks = (int)Math.Round(r.Marks * config.RewardScale);
                if (marks > 0 && config.MarksRewards)
                {
                    p.PendingMarks += marks;
                    parts.Add(Fmt("RewardMarks", null, marks));
                }
                if (config.ItemRewards && r.Items != null)
                    foreach (RewardItem i in r.Items)
                    {
                        AddOwed(p, i.Item, i.Count, label);
                        parts.Add(i.Count + " " + ItemName(i.Item));
                    }
                if (r.SeasonPoints > 0 && config.SeasonPointRewards && !string.IsNullOrEmpty(p.House) && RealmSeasons != null)
                {
                    object ok = RealmSeasons.Call("AwardHouse", p.House, r.SeasonPoints, Clean(p.Name) + ": " + label);
                    if (ok is bool && (bool)ok) parts.Add(Fmt("RewardPoints", null, r.SeasonPoints));
                }
            }
            string deed = r != null && !string.IsNullOrEmpty(r.Renown) ? r.Renown : defaultDeed;
            if (config.RenownRewards && RealmRenown != null && !string.IsNullOrEmpty(deed) && deed != "none")
            {
                object ok = RealmRenown.Call("AddDeed", id, p.Name, deed, label, "quests:" + key);
                if (ok is bool && (bool)ok) parts.Add(Msg("RewardRenown", null));
            }
            dirty = true;
            SaveData();
            Player online = OnlineById(id);
            if (online != null)
            {
                if (p.Owed.Count > 0) DeliverOwed(online, p, false);
                PayMarks(id, p, online);
            }
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : Msg("RewardNothing", null);
        }

        private void AddOwed(PlayerQ p, string item, int amount, string source)
        {
            if (string.IsNullOrEmpty(item) || amount <= 0) return;
            foreach (OwedItem o in p.Owed)
                if (string.Equals(o.Item, item, StringComparison.OrdinalIgnoreCase)) { o.Amount += amount; return; }
            if (p.Owed.Count >= 30) { PrintWarning(p.Name + " has 30 kinds of reward goods waiting; " + amount + " " + item + " not added."); return; }
            p.Owed.Add(new OwedItem { Item = item, Amount = amount, Source = Clean(source) });
        }

        // Marks owed to a player, asked of RealmTreasury.RewardMarks. The owed sum is lowered and saved before the call
        // and only what was not paid goes back, so a crash can lose marks but never pay them twice.
        private void PayMarks(string id, PlayerQ p, Player online)
        {
            if (p == null || p.PendingMarks <= 0 || !config.MarksRewards || RealmTreasury == null) return;
            string held = null;
            if (Now() < treasuryDryUntil) held = "MarksHeldTreasury";        // the reward purse was spent lately: do not ask (and write) every tick
            else if (p.ActiveMinutes < config.MinActiveMinutesForMarks) held = "MarksHeldNew";
            else if (!AddressAllows(online, id)) held = "MarksHeldAddress";
            if (held == null)
            {
                long want = Math.Min(p.PendingMarks, (long)config.MarksPayPerTick);
                p.PendingMarks -= want;
                SaveData();
                long paid = 0;
                try
                {
                    object r = RealmTreasury.Call("RewardMarks", id, p.Name, want, "RealmQuests");
                    if (r is long) paid = (long)r;
                    else if (r is int) paid = (int)r;
                }
                catch (Exception ex) { PrintWarning("RewardMarks failed: " + ex.Message); }
                if (paid < 0) paid = 0;
                if (paid > want) paid = want;
                p.PendingMarks += want - paid;
                p.MarksEarned += paid;
                dirty = true;
                SaveData();
                if (paid > 0)
                {
                    NoteAddressPaid(online, id);
                    p.MarksHeldNotice = false;
                    if (online != null) Reply(online, "MarksPaid", paid);
                }
                if (paid < want) held = "MarksHeldTreasury";
                if (paid == 0) treasuryDryUntil = Now().AddMinutes(10);
            }
            if (held != null && online != null && !p.MarksHeldNotice)
            {
                p.MarksHeldNotice = true;
                Reply(online, "MarksHeld", Msg(held, online));
            }
        }

        private bool AddressAllows(Player online, string id)
        {
            if (config.MaxRewardedAccountsPerAddress <= 0) return true;
            if (online == null) return false;                // marks are paid only to players who are here
            string addr;
            if (!addressOf.TryGetValue(online.Id, out addr)) return true;
            string day = DayKey(Now());
            if (paidAddressesDay != day) { paidAddresses.Clear(); paidAddressesDay = day; }
            List<string> ids;
            if (!paidAddresses.TryGetValue(addr, out ids)) return true;
            return ids.Contains(id) || ids.Count < config.MaxRewardedAccountsPerAddress;
        }

        private void NoteAddressPaid(Player online, string id)
        {
            string addr;
            if (online == null || !addressOf.TryGetValue(online.Id, out addr)) return;
            string day = DayKey(Now());
            if (paidAddressesDay != day) { paidAddresses.Clear(); paidAddressesDay = day; }
            List<string> ids;
            if (!paidAddresses.TryGetValue(addr, out ids)) { ids = new List<string>(); paidAddresses[addr] = ids; }
            if (!ids.Contains(id)) ids.Add(id);
        }

        // Gives owed goods into the packs. Each entry is taken off the ledger and saved first; the gain is measured with
        // AutoCount and only the shortfall is put back [ASM ItemCollection.AutoCount/AutoMergeAdd; IL ThronesCommandHandler.Give].
        private void DeliverOwed(Player player, PlayerQ p, bool manual)
        {
            if (player == null || p == null || p.Owed.Count == 0) { if (manual && player != null) Reply(player, "CollectNone"); return; }
            Container inv = null;
            try { inv = player.GetInventory(); }
            catch (Exception) { inv = null; }
            if (inv == null || inv.Contents == null) { if (manual) ReplyError(player, "GiveNoInventory"); return; }
            ItemCollection packs = inv.Contents;
            if (RealmSentinel != null) RealmSentinel.Call("SentinelItemSource", player.Id, 30f);
            foreach (OwedItem o in new List<OwedItem>(p.Owed))
            {
                InvItemBlueprint bp = FindBlueprint(o.Item);
                if (bp == null)
                {
                    if (manual) ReplyError(player, "CollectUnknown", o.Item);
                    PrintWarning("Reward item '" + o.Item + "' is not known to this server (owed to " + p.Name + ").");
                    continue;
                }
                int plan = o.Amount;
                p.Owed.Remove(o);
                SaveData();
                int before = ItemCollection.AutoCount(packs, bp);
                int limit = StackLimit(bp);
                int left = plan;
                try
                {
                    while (left > 0)
                    {
                        int n = Math.Min(left, limit);
                        var stack = new InvGameItemStack(bp, n, null);
                        if (!ItemCollection.AutoMergeAdd(packs, stack)) break;
                        left -= n;
                    }
                }
                catch (Exception ex) { PrintWarning("Giving " + o.Item + " failed: " + ex.Message); }
                int got = ItemCollection.AutoCount(packs, bp) - before;
                if (got < 0) got = 0;
                if (got > plan) got = plan;
                if (got < plan) AddOwed(p, bp.Name, plan - got, o.Source);
                SaveData();
                if (got > 0) Reply(player, "CollectGot", got, bp.Name);
                if (got < plan) Reply(player, "CollectFull", plan - got, bp.Name);
            }
        }

        private static InvItemBlueprint FindBlueprint(string name)
        {
            if (string.IsNullOrEmpty(name) || InvBlueprints.Instance == null) return null;
            try
            {
                // The display name, any case; then the game's ResourceType name ("IronIngot"), which the lookup matches only
                // case-sensitively and only when ignoreCase is false [ASM InvBlueprints.GetBlueprintForName; CODE].
                InvItemBlueprint bp = InvBlueprints.Instance.GetBlueprintForName(name.Trim(), false, true);
                if (bp == null) bp = InvBlueprints.Instance.GetBlueprintForName(name.Replace(" ", ""), true, false);
                return bp;
            }
            catch (Exception) { return null; }
        }

        private static string ItemName(string name)
        {
            InvItemBlueprint bp = FindBlueprint(name);
            return bp != null && !string.IsNullOrEmpty(bp.Name) ? bp.Name : name;
        }

        private static int StackLimit(InvItemBlueprint bp)
        {
            try
            {
                ContainerManagement cm = bp.TryGet<ContainerManagement>();
                return cm != null && cm.StackLimit > 0 ? cm.StackLimit : 1;
            }
            catch (Exception) { return 1; }
        }

        #endregion

        #region Hooks: deaths, building, crafting

        // RB 1 [OPJ L188]: always null, so the game's own death handling continues.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (loadFailed || data == null || !config.Enabled || evt == null || evt.Entity == null) return null;
            try { HandleDeath(evt); }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        private void HandleDeath(EntityDeathEvent evt)
        {
            Entity e = evt.Entity;
            Damage d = evt.KillingDamage;
            Player killer = null;
            if (d != null && d.DamageSource != null && d.DamageSource.IsPlayer) killer = d.DamageSource.Owner;
            if (killer != null && killer.IsServer) killer = null;
            if (e.IsPlayer)
            {
                Player victim = e.Owner;
                if (victim == null || victim.IsServer) return;
                PlayerQ vq = FindPlayer(victim.Id.ToString());
                DateTime lastDeath = vq != null ? vq.LastDeath : DateTime.MinValue;
                double victimActive = vq != null ? vq.ActiveMinutes : 0;
                if (vq != null) { vq.LastDeath = Now(); vq.LifeMinutes = 0; dirty = true; }
                if (killer != null && killer.Id != victim.Id) CreditKill(killer, victim, lastDeath, victimActive);
                return;
            }
            if (killer == null || !IsCreature(e)) return;
            string label = CreatureLabel(e);
            NoteCreature(label);
            string kid = killer.Id.ToString();
            PlayerQ kq = GetPlayer(kid, killer.Name, true);
            if (kq == null || TakeCredit(kq, "creature", 1, config.CreatureCreditsPerDay) == 0) return;
            MarkAction(killer);
            Record(kid, killer.Name, TSlayCreature, label, 1, null);
        }

        // A kill counts for tasks and deeds only when it is a real fight between real foes (see the header).
        private bool CreditKill(Player killer, Player victim, DateTime victimLastDeath, double victimActive)
        {
            string kid = killer.Id.ToString(), vid = victim.Id.ToString();
            if (OnlineById(vid) == null) return false;                                        // a sleeping body is no fight
            DateTime now = Now();
            if (victimLastDeath > DateTime.MinValue && (now - victimLastDeath).TotalSeconds < config.PvpMinVictimLifeSeconds) return false;
            if (victimActive < config.PvpMinVictimActiveMinutes) return false;
            if (RealmWarden != null)
            {
                object prot = RealmWarden.Call("IsNewPlayerProtected", victim.Id);
                if (prot is bool && (bool)prot) return false;
            }
            string kh = HouseOf(kid), vh = HouseOf(vid);
            if (kh != null && vh != null && SameName(kh, vh)) return false;
            if (!config.PvpAlliesCount && Allied(kh, vh)) return false;
            string pair = string.CompareOrdinal(kid, vid) < 0 ? kid + "|" + vid : vid + "|" + kid;
            DateTime last;
            if (config.PvpPairCooldownHours > 0 && data.PvpPairs.TryGetValue(pair, out last) && (now - last).TotalHours < config.PvpPairCooldownHours) return false;
            PlayerQ kq = GetPlayer(kid, killer.Name, true);
            if (kq == null || TakeCredit(kq, "pvp", 1, config.PvpCreditsPerDay) == 0) return false;
            data.PvpPairs[pair] = now;
            if (data.PvpPairs.Count > 5000) PrunePairs(data.PvpPairs, config.PvpPairCooldownHours);
            dirty = true;
            MarkAction(killer);
            Record(kid, killer.Name, TSlayPlayer, "player", 1, vid);
            return true;
        }

        private void PrunePairs(Dictionary<string, DateTime> pairs, int hours)
        {
            DateTime now = Now();
            var old = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in pairs) if ((now - kv.Value).TotalHours >= Math.Max(1, hours)) old.Add(kv.Key);
            foreach (string k in old) pairs.Remove(k);
            if (pairs.Count > 5000) pairs.Clear();
        }

        // A creature, as the game's own SalvageSupplier tells one [CODE]: a MonsterMotor or a MonsterEntity attribute.
        private static bool IsCreature(Entity e)
        {
            try { return e != null && !e.IsPlayer && (e.TryGet<MonsterMotor>() != null || e.TryGet<MonsterEntity>() != null); }
            catch (Exception) { return false; }
        }

        // "Wolf(Clone) (CodeHatch.Engine.Core.Cache.Entity)" -> "wolf". UNVERIFIED: the real names (see /quest admin creatures).
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

        private void NoteCreature(string label)
        {
            recentCreatures.Remove(label);
            recentCreatures.Add(label);
            if (recentCreatures.Count > 15) recentCreatures.RemoveAt(0);
        }

        // Called by CubeListener.OnCubePlace [OPJ L266]. Counted on the next tick, when every plugin has had its say.
        private void OnCubePlacement(CubePlaceEvent evt)
        {
            if (loadFailed || data == null || !config.Enabled || evt == null || evt.Cancelled) return;
            try
            {
                if (evt.CausedByDestruction) return;
                Player p = evt.Sender;
                if (p == null || p.IsServer) return;
                string cell = evt.GridID + ":" + evt.Position.x + ":" + evt.Position.y + ":" + evt.Position.z;
                int material = evt.Material;
                CubePlaceEvent placed = evt;
                NextTick(delegate { CountPlacement(placed, p, cell, material); });
            }
            catch (Exception ex) { PrintError("Placement handling failed: " + ex.Message); }
        }

        private void CountPlacement(CubePlaceEvent evt, Player p, string cell, int material)
        {
            if (evt.Cancelled || data == null) return;
            string day = DayKey(Now());
            if (buildCellsDay != day) { buildCells.Clear(); buildCellsDay = day; }
            string id = p.Id.ToString();
            Dictionary<string, bool> cells;
            if (!buildCells.TryGetValue(id, out cells)) { cells = new Dictionary<string, bool>(); buildCells[id] = cells; }
            if (cells.ContainsKey(cell)) return;                       // the same cell counts once a day (place, break, place)
            if (cells.Count >= 20000) return;
            cells[cell] = true;
            PlayerQ q = GetPlayer(id, p.Name, true);
            if (q == null || TakeCredit(q, "build", 1, config.BuildCreditsPerDay) == 0) return;
            MarkAction(p);
            Record(id, p.Name, TBuild, MaterialRole(material), 1, null);
        }

        private string MaterialRole(int material)
        {
            foreach (KeyValuePair<string, int> kv in config.MaterialIds) if (kv.Value == material) return kv.Key;
            return "block";
        }

        // The player who asks a crafter to start (ItemCrafterCraftEvent.Sender), so its products can be credited.
        private void OnCraftRequested(ItemCrafterCraftEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Cancelled || evt.Crafter == null) return;
            try
            {
                Player p = evt.Sender;
                if (p == null || p.IsServer) return;
                if (craftClaims.Count > 500) PruneClaims();
                craftClaims[evt.Crafter] = new CraftClaim { PlayerId = p.Id, Product = evt.Product != null ? evt.Product.Name : null, At = Now() };
            }
            catch (Exception ex) { PrintError("Craft request handling failed: " + ex.Message); }
        }

        // Each product stack the server makes [CODE ItemCrafter.Update -> CraftItem -> ItemCrafterItemEvent].
        private void OnCraftProduct(ItemCrafterItemEvent evt)
        {
            if (loadFailed || data == null || !config.Enabled || evt == null || evt.Cancelled || evt.Stack == null || evt.Stack.Blueprint == null) return;
            try
            {
                ulong pid = 0;
                CraftClaim c;
                if (evt.Crafter != null && craftClaims.TryGetValue(evt.Crafter, out c)) pid = c.PlayerId;
                else if (evt.Entity != null && evt.Entity.IsPlayer && evt.Entity.Owner != null && !evt.Entity.Owner.IsServer) pid = evt.Entity.Owner.Id;
                if (pid == 0) return;
                string product = evt.Stack.Blueprint.Name;
                if (string.IsNullOrEmpty(product) || IsIgnoredCraft(product)) return;
                int n = evt.Stack.StackAmount > 0 ? evt.Stack.StackAmount : 1;
                string id = pid.ToString();
                Player online = OnlineById(id);
                PlayerQ p = GetPlayer(id, online != null ? online.Name : null, online != null);
                if (p == null) return;
                int granted = TakeCredit(p, "craft", n, config.CraftCreditsPerDay);
                if (granted == 0) return;
                if (online != null) MarkAction(online);
                Record(id, p.Name, TCraft, product, granted, null);
            }
            catch (Exception ex) { PrintError("Craft handling failed: " + ex.Message); }
        }

        // RB 1 [OPJ L1088]. The run is over: forget whose craft the crafter was running. Always null.
        private object OnItemCrafted(ItemCrafterFinishEvent evt)
        {
            try { if (evt != null && evt.Crafter != null) craftClaims.Remove(evt.Crafter); }
            catch (Exception) { }
            return null;
        }

        private void PruneClaims()
        {
            DateTime now = Now();
            var old = new List<object>();
            foreach (KeyValuePair<object, CraftClaim> kv in craftClaims) if ((now - kv.Value.At).TotalHours > 2) old.Add(kv.Key);
            foreach (object k in old) craftClaims.Remove(k);
            if (craftClaims.Count > 500) craftClaims.Clear();
        }

        private bool IsIgnoredCraft(string product)
        {
            foreach (string s in config.CraftIgnore) if (SameName(s, product)) return true;
            return false;
        }

        private void MarkAction(Player p)
        {
            Sample s;
            if (!samples.TryGetValue(p.Id, out s)) { s = new Sample(); samples[p.Id] = s; }
            s.LastAction = Now();
        }

        #endregion

        #region Tick: presence, places, events, state, feeds

        // Places are checked more often than the tick, so a rider passing through a marked place is seen.
        private void SafeVisits()
        {
            if (loadFailed || data == null || !config.Enabled || data.Places.Count == 0) return;
            try
            {
                foreach (Player pl in OnlinePlayers())
                {
                    PlayerQ p = FindPlayer(pl.Id.ToString());
                    if (p != null) Visits(pl, p);
                }
            }
            catch (Exception ex) { PrintError("Visit check failed: " + ex.Message); }
        }

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            if (loadFailed || data == null || !config.Enabled) return;
            DateTime now = Now();
            List<Player> online = OnlinePlayers();
            string[] events = ActiveEvents();
            foreach (Player pl in online)
            {
                PlayerQ p = Seen(pl);
                if (p == null) continue;
                string id = pl.Id.ToString();
                if (Active(pl, now))
                {
                    double minutes = config.TickSeconds / 60.0;
                    p.ActiveMinutes += minutes;
                    p.LifeMinutes += minutes;
                    Sample s = samples[pl.Id];
                    s.Fraction += config.TickSeconds;
                    int whole = (int)(s.Fraction / 60.0);
                    if (whole > 0)
                    {
                        s.Fraction -= whole * 60.0;
                        Record(id, pl.Name, TPlaytime, "", whole, null);
                        RecordState(id, TSurvive, "", (long)p.LifeMinutes);
                    }
                    if (events.Length > 0) Attend(pl, p, events);
                    dirty = true;
                }
                Visits(pl, p);
                if (p.PendingMarks > 0) PayMarks(id, p, pl);
            }
            if ((now - lastFeedPoll).TotalSeconds >= config.FeedPollSeconds)
            {
                lastFeedPoll = now;
                PollStates(online);
                PollHouses();
                PollChronicle();
                PollContracts();
            }
            if (Array.IndexOf(events, "truce") < 0) SettleTruce();
            if (dirty && (now - lastSave).TotalSeconds >= 60) SaveData();
        }

        // Active: moved at least ActiveMoveMeters since the last tick, or did something counted lately. AFK time is not play.
        private bool Active(Player pl, DateTime now)
        {
            Sample s;
            if (!samples.TryGetValue(pl.Id, out s)) { s = new Sample(); samples[pl.Id] = s; }
            float x, y, z;
            if (!TryPosition(pl, out x, out y, out z)) return false;
            bool moved = false;
            if (s.Has)
            {
                float dx = x - s.X, dy = y - s.Y, dz = z - s.Z;
                moved = dx * dx + dy * dy + dz * dz >= config.ActiveMoveMeters * config.ActiveMoveMeters;
            }
            s.X = x; s.Y = y; s.Z = z;
            bool first = !s.Has;
            s.Has = true;
            if (first) return false;
            return moved || (now - s.LastAction).TotalSeconds <= config.ActionKeepsActiveSeconds;
        }

        private static bool TryPosition(Player p, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            try
            {
                if (p == null || p.Entity == null) return false;
                UnityEngine.Vector3 v = p.Entity.Position;                 // [ASM Entity.Position]
                x = v.x; y = v.y; z = v.z;
                return !(float.IsNaN(x) || float.IsNaN(z));
            }
            catch (Exception) { return false; }
        }

        private void Visits(Player pl, PlayerQ p)
        {
            if (data.Places.Count == 0) return;
            float x, y, z;
            if (!TryPosition(pl, out x, out y, out z)) return;
            foreach (KeyValuePair<string, PlaceMark> kv in data.Places)
            {
                PlaceDef def;
                if (!places.TryGetValue(kv.Key, out def) || p.VisitedToday.Contains(kv.Key)) continue;
                float dx = x - kv.Value.X, dz = z - kv.Value.Z;
                float r = kv.Value.Radius > 0 ? kv.Value.Radius : def.Radius;
                if (dx * dx + dz * dz > r * r) continue;
                p.VisitedToday.Add(kv.Key);
                dirty = true;
                if (Record(pl.Id.ToString(), pl.Name, TVisit, kv.Key, 1, kv.Key)) Reply(pl, "Visited", def.Name);
            }
        }

        private string[] ActiveEvents()
        {
            if (RealmEvents == null) return new string[0];
            string[] kinds = RealmEvents.Call("GetActiveEvents") as string[];
            return kinds ?? new string[0];
        }

        // Present and active for EventAttendMinutes while a realm event runs: once per event kind per day.
        private void Attend(Player pl, PlayerQ p, string[] events)
        {
            string id = pl.Id.ToString();
            string day = DayKey(Now());
            foreach (string kind in events)
            {
                if (string.IsNullOrEmpty(kind)) continue;
                string key = kind + "|" + day + "|" + id;
                if (data.EventCredits.Contains(key)) continue;
                double secs;
                attendSeconds.TryGetValue(key, out secs);
                secs += config.TickSeconds;
                attendSeconds[key] = secs;
                if (secs < config.EventAttendMinutes * 60.0) continue;
                attendSeconds.Remove(key);
                data.EventCredits.Add(key);
                if (data.EventCredits.Count > 3000) data.EventCredits.RemoveRange(0, 1000);
                // Keeping the truce is only known when it is over: credited then, unless the Chronicle named a breach.
                if (kind == "truce") { data.EventCredits.Add("pending|" + key); continue; }
                Record(id, pl.Name, TEvent, kind, 1, null);
            }
            if (attendSeconds.Count > 5000) attendSeconds.Clear();
        }

        // The truce is over: everyone who kept it for EventAttendMinutes, and whom the Chronicle did not name as breaking it
        // that day, is credited now.
        private void SettleTruce()
        {
            List<string> pending = data.EventCredits.FindAll(delegate(string k) { return k.StartsWith("pending|truce|"); });
            if (pending.Count == 0) return;
            foreach (string k in pending)
            {
                data.EventCredits.Remove(k);
                string[] parts = k.Split('|');                   // pending|truce|day|id
                if (parts.Length != 4 || data.EventCredits.Contains("breaker|truce|" + parts[2] + "|" + parts[3])) continue;
                Record(parts[3], null, TEvent, "truce", 1, null);
            }
            dirty = true;
        }

        // Levels other plugins keep: house membership and leadership, renown, titles, marks.
        private void PollStates(List<Player> online)
        {
            foreach (Player pl in online)
            {
                string id = pl.Id.ToString();
                PlayerQ p = FindPlayer(id);
                if (p == null) continue;
                string house = RefreshHouse(id, p);
                if (house != null)
                {
                    RecordState(id, THouse, "member", 1);
                    if (RealmHouses != null && (RealmHouses.Call("GetHouseLeader", house) as string) == id) RecordState(id, THouse, "leader", 1);
                }
                if (RealmRenown != null)
                {
                    object r = RealmRenown.Call("GetRenown", id);
                    if (r is int && (int)r > 0) RecordState(id, TRenown, "", (int)r);
                    string[] titles = RealmRenown.Call("GetTitles", id) as string[];
                    if (titles != null && titles.Length > 0) RecordState(id, TTitle, string.Join(",", titles), titles.Length);
                }
                if (RealmTreasury != null)
                {
                    object m = RealmTreasury.Call("GetPurse", id);
                    if (m is long && (long)m > 0) RecordState(id, TMarks, "", (long)m);
                }
                Sweep(id, p, pl);
                ChapterNews(pl, p);
            }
        }

        // Completes whatever is done but was not closed: a waived visit, an AnyOne task, content that changed.
        private void Sweep(string id, PlayerQ p, Player online)
        {
            if (config.DailiesEnabled)
                foreach (Slot s in new List<Slot>(p.Daily))
                {
                    QuestDef q;
                    if (!s.Done && !s.Abandoned && s.QuestId != null && dailies.TryGetValue(s.QuestId, out q) && AllDone(q, s.Progress, false)) CompleteSlot(id, p, online, s, q, KDaily);
                }
            if (config.WeekliesEnabled)
                foreach (Slot s in new List<Slot>(p.Weekly))
                {
                    QuestDef q;
                    if (!s.Done && !s.Abandoned && s.QuestId != null && weeklies.TryGetValue(s.QuestId, out q) && AllDone(q, s.Progress, false)) CompleteSlot(id, p, online, s, q, KWeekly);
                }
            for (int guard = 0; guard < 3; guard++)
            {
                ChapterDef ch;
                string wait;
                QuestDef step = CurrentStep(p, out ch, out wait);
                if (step == null || !AllDone(step, p.Story.Progress, true)) break;
                CompleteStep(id, p, online, ch, step);
            }
        }

        // The player's house as RealmHouses has it now; a change restarts HouseSince (house hopping gains nothing).
        private string RefreshHouse(string id, PlayerQ p)
        {
            if (RealmHouses == null) return p.House;
            string house = HouseOf(id);
            if (!SameName(house, p.House))
            {
                p.House = house;
                p.HouseSince = Now();
                dirty = true;
            }
            return house;
        }

        // Oaths: a house whose liege changed has sworn; the liege accepted. Credited to members of a real house.
        private void PollHouses()
        {
            if (RealmHouses == null) { feedStatus["oaths"] = "RealmHouses not loaded"; return; }
            List<Dictionary<string, object>> list = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
            if (list == null) { feedStatus["oaths"] = "no answer from RealmHouses"; return; }
            var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> h in list)
            {
                object n, l;
                if (h == null || !h.TryGetValue("name", out n) || !(n is string)) continue;
                h.TryGetValue("liege", out l);
                now[(string)n] = l as string;
            }
            int sworn = 0;
            if (data.LiegesBaselined)
                foreach (KeyValuePair<string, string> kv in now)
                {
                    string before;
                    data.Lieges.TryGetValue(kv.Key, out before);
                    if (string.IsNullOrEmpty(kv.Value) || SameName(before, kv.Value)) continue;
                    if (HouseSize(kv.Key) < 2 || HouseSize(kv.Value) < 2) continue;   // a lone account's house swearing or accepting is no oath
                    CreditHouse(kv.Key, TOath, "sworn", "oath");
                    CreditHouse(kv.Value, TOath, "accepted", "oath");
                    sworn++;
                }
            data.Lieges = now;
            data.LiegesBaselined = true;
            dirty = true;
            feedStatus["oaths"] = "ok, " + now.Count + " houses" + (sworn > 0 ? ", " + sworn + " new oath(s)" : "");
        }

        // Every long-standing member of a house is credited once a day for something the house did.
        private void CreditHouse(string house, string type, string subject, string creditKey)
        {
            if (RealmHouses == null || string.IsNullOrEmpty(house)) return;
            List<string> members = RealmHouses.Call("GetMembers", house) as List<string>;
            if (members == null) return;
            foreach (string mid in members)
            {
                PlayerQ m = FindPlayer(mid);
                // Only members the board has seen in this house for HouseMemberMinHours (a fresh recruit is not credited).
                if (m == null || !SameName(m.House, house) || (Now() - m.HouseSince).TotalHours < config.HouseMemberMinHours) continue;
                if (TakeCredit(m, creditKey, 1, 1) == 0) continue;
                Record(mid, null, type, subject, 1, null);
            }
        }

        private class ChronEventLite
        {
            public int id;
            public string type;
            public string title;
            public string[] actors;
        }

        private T ReadForeign<T>(string name, string feed) where T : class, new()
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(name)) { feedStatus[feed] = "no oxide/data/" + name + ".json"; return null; }
            T obj = null;
            try { obj = Interface.Oxide.DataFileSystem.ReadObject<T>(name); }
            catch (Exception ex) { feedStatus[feed] = "unreadable " + name + ".json (" + ex.Message + ")"; return null; }
            if (obj == null) feedStatus[feed] = "empty " + name + ".json";
            return obj;
        }

        // The Chronicle: deeds whose doer is actors[0] (coronations, claims, laws, dynasties, champions...) and treaties.
        private void PollChronicle()
        {
            List<ChronEventLite> events = ReadForeign<List<ChronEventLite>>(ChronicleFile, "chronicle");
            if (events == null) return;
            int maxId = 0;
            foreach (ChronEventLite e in events) if (e != null && e.id > maxId) maxId = e.id;
            if (data.ChronicleCursor < 0 || maxId < data.ChronicleCursor)
            {
                // First look, or the Chronicle was reset: start from now, never replay history.
                data.ChronicleCursor = maxId;
                dirty = true;
                feedStatus["chronicle"] = "baselined at #" + maxId;
                return;
            }
            events.Sort(delegate(ChronEventLite a, ChronEventLite b) { return (a == null ? 0 : a.id).CompareTo(b == null ? 0 : b.id); });
            Dictionary<string, string> byName = NameIndex();
            int n = 0;
            foreach (ChronEventLite e in events)
            {
                if (e == null || e.id <= data.ChronicleCursor) continue;
                data.ChronicleCursor = e.id;
                dirty = true;
                if (n >= 100) continue;
                if (e.type == "treaty_signed" && e.title != null) { TreatyFromTitle(e.title); n++; continue; }
                if (e.type == "truce_broken" && e.actors != null && e.actors.Length > 0 && e.actors[0] != null)
                {
                    // Whoever broke the Truce of the Realm today has not kept it: no attendance credit for it today.
                    string breaker;
                    if (byName.TryGetValue(e.actors[0].Trim().ToLowerInvariant(), out breaker) && breaker != null)
                    {
                        string key = "breaker|truce|" + DayKey(Now()) + "|" + breaker;
                        if (!data.EventCredits.Contains(key)) data.EventCredits.Add(key);
                    }
                }
                if (e.type == null || e.actors == null || e.actors.Length == 0 || string.IsNullOrEmpty(e.actors[0])) continue;
                string id;
                if (!byName.TryGetValue(e.actors[0].Trim().ToLowerInvariant(), out id) || id == null) continue;
                Record(id, null, TChronicle, e.type, 1, null);
                n++;
            }
            feedStatus["chronicle"] = "ok at #" + data.ChronicleCursor;
        }

        // "House A and House B sign a treaty" (RealmHouses) -> both houses' members.
        private void TreatyFromTitle(string title)
        {
            if (RealmHouses == null) return;
            List<Dictionary<string, object>> list = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
            if (list == null) return;
            foreach (Dictionary<string, object> h in list)
            {
                object n;
                if (h == null || !h.TryGetValue("name", out n) || !(n is string)) continue;
                string name = (string)n;
                if (NamesHouse(title, name)) CreditHouse(name, TTreaty, "signed", "treaty");
            }
        }

        private static bool NamesHouse(string title, string house)
        {
            string key = "House " + house;
            int i = title.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                int end = i + key.Length;
                if (end == title.Length || !char.IsLetterOrDigit(title[end])) return true;
                i = title.IndexOf(key, end, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        // Player names the board has seen -> ids; a name two players share maps to null (never guessed).
        private Dictionary<string, string> NameIndex()
        {
            var map = new Dictionary<string, string>();
            foreach (KeyValuePair<string, PlayerQ> kv in data.Players)
            {
                if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                string k = kv.Value.Name.Trim().ToLowerInvariant();
                if (map.ContainsKey(k)) map[k] = null; else map[k] = kv.Key;
            }
            return map;
        }

        private class ContractLite
        {
            public int Id;
            public string Type;
            public string Status;
            public string FulfillerId;
            public string PosterId;
        }

        private class ContractsFileLite
        {
            public int NextId;
            public List<ContractLite> Contracts;
        }

        // Finished contracts (RealmContracts.json, read only). Never between the same two players twice a day, never
        // within one house, never a player's own.
        private void PollContracts()
        {
            ContractsFileLite f = ReadForeign<ContractsFileLite>(ContractsFile, "contracts");
            if (f == null || f.Contracts == null) return;
            if (f.NextId < data.ContractsNextId) { data.CreditedContracts.Clear(); data.ContractsBaselined = false; }
            data.ContractsNextId = f.NextId;
            if (!data.ContractsBaselined)
            {
                foreach (ContractLite c in f.Contracts) if (c != null && c.Status == "done") data.CreditedContracts.Add(c.Id);
                data.ContractsBaselined = true;
                TrimCredited();
                dirty = true;
                feedStatus["contracts"] = "baselined " + data.CreditedContracts.Count + " finished contract(s)";
                return;
            }
            int n = 0;
            DateTime now = Now();
            foreach (ContractLite c in f.Contracts)
            {
                if (c == null || c.Status != "done" || data.CreditedContracts.Contains(c.Id)) continue;
                data.CreditedContracts.Add(c.Id);
                dirty = true;
                if (!IsSteamId(c.FulfillerId) || c.FulfillerId == c.PosterId) continue;
                if (c.PosterId != null)
                {
                    string ph = HouseOf(c.PosterId), fh = HouseOf(c.FulfillerId);
                    if (ph != null && fh != null && SameName(ph, fh)) continue;
                    string pair = string.CompareOrdinal(c.PosterId, c.FulfillerId) < 0 ? c.PosterId + "|" + c.FulfillerId : c.FulfillerId + "|" + c.PosterId;
                    DateTime last;
                    if (config.ContractPairCooldownHours > 0 && data.ContractPairs.TryGetValue(pair, out last) && (now - last).TotalHours < config.ContractPairCooldownHours) continue;
                    data.ContractPairs[pair] = now;
                    if (data.ContractPairs.Count > 5000) PrunePairs(data.ContractPairs, config.ContractPairCooldownHours);
                }
                PlayerQ p = FindPlayer(c.FulfillerId);
                if (p == null || TakeCredit(p, "contract", 1, config.ContractCreditsPerDay) == 0) continue;
                Record(c.FulfillerId, null, TContract, c.Type ?? "", 1, null);
                n++;
            }
            TrimCredited();
            feedStatus["contracts"] = "ok, " + n + " new";
        }

        private void TrimCredited()
        {
            if (data.CreditedContracts.Count > 4000) data.CreditedContracts.RemoveRange(0, data.CreditedContracts.Count - 4000);
        }

        #endregion

        #region Commands: /quest

        [ChatCommand("quest")]
        private void CmdQuest(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (args == null) args = new string[0];
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "admin") { Admin(player, args); return; }
            if (!config.Enabled) { ReplyError(player, "Disabled"); return; }
            PlayerQ p = Seen(player);
            if (p == null) return;
            RefreshHouse(player.Id.ToString(), p);
            switch (sub)
            {
                case "":
                case "journal": ShowJournal(player, p); break;
                case "log": ShowLog(player, p); break;
                case "abandon": Abandon(player, p, args); break;
                case "reroll": Reroll(player, p, args); break;
                case "give":
                case "turnin": Give(player, p, args.Length > 1 && args[1].ToLowerInvariant() == "all"); break;
                case "collect": DeliverOwed(player, p, true); break;
                case "story": ShowStory(player, p); break;
                case "house": ShowHouse(player, p); break;
                default: ShowHelp(player); break;
            }
        }

        private void ShowHelp(Player player)
        {
            Reply(player, "Help1");
            Reply(player, "Help2");
            Reply(player, "Help3", config.DailyRerolls);
            Reply(player, "Help4");
            if (IsAdmin(player)) Reply(player, "HelpAdmin");
        }

        private bool HasContent()
        {
            return dailies.Count + weeklies.Count + achievements.Count + houseGoals.Count > 0 || story != null;
        }

        private string ObjectiveSummary(List<ObjectiveDef> objectives, List<int> progress)
        {
            var parts = new List<string>();
            for (int i = 0; i < objectives.Count; i++)
            {
                int have = i < progress.Count ? progress[i] : 0;
                if (Waived(objectives[i])) parts.Add(objectives[i].Text + " " + Muted("(" + Msg("Unmarked", null) + ")"));
                else parts.Add(objectives[i].Text + " " + have + "/" + objectives[i].Count);
            }
            return string.Join("; ", parts.ToArray());
        }

        private string SlotLine(Player player, Slot s, Dictionary<string, QuestDef> pool)
        {
            QuestDef q;
            if (s.QuestId == null || !pool.TryGetValue(s.QuestId, out q)) return Muted(Msg("SlotGone", player));
            if (s.Done) return q.Title + " - " + Ok(Msg("SlotDone", player));
            if (s.Abandoned) return q.Title + " - " + Muted(Msg("SlotAbandoned", player));
            string reward = q.Reward != null && q.Reward.Marks > 0 && config.MarksRewards ? " " + Muted("(" + Fmt("RewardMarks", player, (int)Math.Round(q.Reward.Marks * config.RewardScale)) + ")") : "";
            return q.Title + " - " + ObjectiveSummary(q.Objectives, s.Progress) + reward;
        }

        private string StoryLine(Player player, PlayerQ p, out QuestDef step, out ChapterDef ch)
        {
            string wait;
            step = CurrentStep(p, out ch, out wait);
            if (step != null) return Fmt("StoryChapter", player, ch.Act, ch.Title) + " - " + step.Title + ": " + ObjectiveSummary(step.Objectives, p.Story.Progress);
            if (wait == "complete") return Ok(Fmt("StoryComplete", player, story != null ? story.Title : ""));
            if (wait == "day" && ch != null) return Muted(Fmt("StoryWaitDay", player, ch.UnlockDay + 1));
            if (wait == "season") return Muted(Msg("StoryWaitSeason", player));
            return Muted(Msg("StoryOff", player));
        }

        private string HouseLine(Player player, PlayerQ p)
        {
            if (string.IsNullOrEmpty(p.House)) return Msg("HouseNone", player);
            if (RealmHouses != null && HouseSize(p.House) < config.HouseGoalMinMembers) return Muted(Fmt("HouseTooSmall", player, config.HouseGoalMinMembers));
            HouseQ h = HouseState(p.House);
            if (h == null || h.Goals.Count == 0) return Muted(Msg("StoryOff", player));
            var parts = new List<string>();
            foreach (HouseGoalState g in h.Goals)
            {
                HouseGoalDef def;
                if (!houseGoals.TryGetValue(g.GoalId, out def)) continue;
                parts.Add(def.Title + " - " + (g.Done ? Ok(Msg("HouseGoalDone", player)) : def.Objective.Text + " " + g.Progress + "/" + def.Objective.Count));
            }
            return string.Join("; ", parts.ToArray());
        }

        private void ShowJournal(Player player, PlayerQ p)
        {
            if (!HasContent()) { Reply(player, "NoContent"); return; }
            DateTime now = Now();
            var popup = new List<string>();
            Reply(player, "JournalHeader", Dur(UntilDayTurns(now)), Dur(UntilWeekTurns(now)));
            popup.Add(Fmt("JournalHeader", player, Dur(UntilDayTurns(now)), Dur(UntilWeekTurns(now))));
            bool any = false;
            if (config.DailiesEnabled)
                for (int i = 0; i < p.Daily.Count; i++)
                {
                    string line = Fmt("JournalDaily", player, i + 1, SlotLine(player, p.Daily[i], dailies));
                    player.SendMessage(line);
                    popup.Add(line.Trim());
                    any = true;
                }
            if (config.WeekliesEnabled)
                for (int i = 0; i < p.Weekly.Count; i++)
                {
                    string line = Fmt("JournalWeekly", player, i + 1, SlotLine(player, p.Weekly[i], weeklies));
                    player.SendMessage(line);
                    popup.Add(line.Trim());
                    any = true;
                }
            if (story != null && config.StoryEnabled)
            {
                QuestDef step;
                ChapterDef ch;
                string line = Fmt("JournalStory", player, StoryLine(player, p, out step, out ch));
                player.SendMessage(line);
                popup.Add(line.Trim());
                any = true;
            }
            if (config.HouseGoalsEnabled && houseGoals.Count > 0)
            {
                string line = Fmt("JournalHouse", player, string.IsNullOrEmpty(p.House) ? "-" : p.House, HouseLine(player, p));
                player.SendMessage(line);
                popup.Add(line.Trim());
                any = true;
            }
            if (!any) Reply(player, "JournalEmpty");
            if (p.Owed.Count > 0) Reply(player, "JournalOwed", p.Owed.Count);
            if (p.PendingMarks > 0) Reply(player, "JournalMarksHeld", p.PendingMarks);
            Reply(player, "JournalFoot");
            if (PopupsFor(player))
            {
                if (popup.Count > config.JournalPopupMaxLines) popup.RemoveRange(config.JournalPopupMaxLines, popup.Count - config.JournalPopupMaxLines);
                ShowInfoPopup(player, Msg("PopupJournal", player), string.Join("\n\n", popup.ToArray()), Msg("PopupButton", player));
            }
        }

        private void LogQuest(Player player, string label, QuestDef q, List<int> progress)
        {
            Reply(player, "LogHeader", label, q.Title);
            Reply(player, "LogText", q.Text);
            for (int i = 0; i < q.Objectives.Count; i++)
                Reply(player, "LogObjective", q.Objectives[i].Text, i < progress.Count ? progress[i] : 0, q.Objectives[i].Count);
            Reply(player, "LogReward", RewardText(player, q.Reward));
        }

        private string RewardText(Player player, RewardDef r)
        {
            var parts = new List<string>();
            if (r != null)
            {
                if (r.Marks > 0 && config.MarksRewards) parts.Add(Fmt("RewardMarks", player, (int)Math.Round(r.Marks * config.RewardScale)));
                if (r.Items != null && config.ItemRewards) foreach (RewardItem i in r.Items) parts.Add(i.Count + " " + ItemName(i.Item));
                if (r.SeasonPoints > 0 && config.SeasonPointRewards) parts.Add(Fmt("RewardPoints", player, r.SeasonPoints));
            }
            if (config.RenownRewards && RealmRenown != null && (r == null || r.Renown != "none")) parts.Add(Msg("RewardRenown", player));
            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : Msg("RewardNothing", player);
        }

        private void ShowLog(Player player, PlayerQ p)
        {
            if (!HasContent()) { Reply(player, "NoContent"); return; }
            for (int i = 0; i < p.Daily.Count && config.DailiesEnabled; i++)
            {
                QuestDef q;
                Slot s = p.Daily[i];
                if (s.Done || s.Abandoned || s.QuestId == null || !dailies.TryGetValue(s.QuestId, out q)) continue;
                LogQuest(player, "d" + (i + 1), q, s.Progress);
            }
            for (int i = 0; i < p.Weekly.Count && config.WeekliesEnabled; i++)
            {
                QuestDef q;
                Slot s = p.Weekly[i];
                if (s.Done || s.Abandoned || s.QuestId == null || !weeklies.TryGetValue(s.QuestId, out q)) continue;
                LogQuest(player, "w" + (i + 1), q, s.Progress);
            }
            ChapterDef ch;
            string wait;
            QuestDef step = CurrentStep(p, out ch, out wait);
            if (step != null) LogQuest(player, Fmt("StoryChapter", player, ch.Act, ch.Title), step, p.Story.Progress);
        }

        // "d2" / "w1" (or a bare number for a daily) -> the slot list and index.
        private bool ParseSlot(PlayerQ p, string text, out List<Slot> list, out int index, out bool daily)
        {
            list = null;
            index = -1;
            daily = true;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            if (t[0] == 'w') { daily = false; t = t.Substring(1); }
            else if (t[0] == 'd') t = t.Substring(1);
            int n;
            if (!int.TryParse(t, out n)) return false;
            list = daily ? p.Daily : p.Weekly;
            index = n - 1;
            return index >= 0 && index < list.Count;
        }

        private void Abandon(Player player, PlayerQ p, string[] args)
        {
            List<Slot> list;
            int index;
            bool daily;
            if (args.Length < 2) { Reply(player, "AbandonWhich"); return; }
            if (!ParseSlot(p, args[1], out list, out index, out daily)) { ReplyError(player, "BadSlot", args[1]); return; }
            Slot s = list[index];
            QuestDef q;
            if (s.Done || s.Abandoned || s.QuestId == null || !(daily ? dailies : weeklies).TryGetValue(s.QuestId, out q)) { ReplyError(player, "AbandonDone"); return; }
            string key = player.Id + "|" + (daily ? "d" : "w") + index;
            bool confirmed = args.Length > 2 && args[2].ToLowerInvariant() == "confirm";
            DateTime asked;
            if (!confirmed || !abandonAsk.TryGetValue(key, out asked) || (Now() - asked).TotalSeconds > 120)
            {
                abandonAsk[key] = Now();
                if (abandonAsk.Count > 500) abandonAsk.Clear();
                Reply(player, "AbandonAsk", q.Title, args[1]);
                return;
            }
            abandonAsk.Remove(key);
            s.Abandoned = true;
            s.Progress = new List<int>();
            s.Seen = new List<string>();
            dirty = true;
            SaveData();
            Reply(player, "Abandoned", q.Title);
        }

        private void Reroll(Player player, PlayerQ p, string[] args)
        {
            List<Slot> list;
            int index;
            bool daily;
            if (args.Length < 2) { Reply(player, "RerollWhich"); return; }
            if (!ParseSlot(p, args[1], out list, out index, out daily) || !daily) { ReplyError(player, "BadSlot", args[1]); return; }
            Slot s = list[index];
            if (s.Done || s.Abandoned) { ReplyError(player, "RerollDoneSlot"); return; }
            if (p.Rerolls >= config.DailyRerolls) { ReplyError(player, "RerollNone", config.DailyRerolls); return; }
            var exclude = SlotIds(p.Daily);
            exclude.AddRange(p.LastDaily);
            List<string> pick = Draw(dailyOrder, dailies, p, player.Id + "|reroll|" + p.DayKey + "|" + p.Rerolls, 1, exclude, null);
            if (pick.Count == 0) { ReplyError(player, "RerollEmpty"); return; }
            p.Rerolls++;
            list[index] = NewSlot(pick[0]);
            dirty = true;
            SaveData();
            Reply(player, "Rerolled", args[1], dailies[pick[0]].Title);
        }

        private class Need
        {
            public string Label;
            public ObjectiveDef Obj;
            public int Remaining;
            public Func<bool> Valid;                        // still open? (an earlier hand-in may have moved things on)
            public Action<int> Apply;
        }

        // Every delivery the player could make now: open tasks, the story step, and the house goal (within their share).
        private List<Need> Needs(Player player, PlayerQ p)
        {
            var needs = new List<Need>();
            string id = player.Id.ToString();
            AddSlotNeeds(needs, id, p, player, p.Daily, dailies, KDaily);
            AddSlotNeeds(needs, id, p, player, p.Weekly, weeklies, KWeekly);
            ChapterDef ch;
            string wait;
            QuestDef step = CurrentStep(p, out ch, out wait);
            if (step != null)
                for (int i = 0; i < step.Objectives.Count; i++)
                {
                    ObjectiveDef o = step.Objectives[i];
                    if (o.Type != TDeliver) continue;
                    int have = i < p.Story.Progress.Count ? p.Story.Progress[i] : 0;
                    if (have >= o.Count) continue;
                    int idx = i;
                    QuestDef q = step;
                    ChapterDef c = ch;
                    needs.Add(new Need
                    {
                        Label = q.Title, Obj = o, Remaining = o.Count - have,
                        Valid = delegate { return p.Story.StepId == q.Id && !p.Story.Finished.Contains(q.Id); },
                        Apply = delegate(int n)
                        {
                            if (p.Story.StepId != q.Id || p.Story.Finished.Contains(q.Id)) return;
                            while (p.Story.Progress.Count <= idx) p.Story.Progress.Add(0);
                            p.Story.Progress[idx] = Math.Min(o.Count, p.Story.Progress[idx] + n);
                            if (AllDone(q, p.Story.Progress, true)) CompleteStep(id, p, player, c, q);
                        }
                    });
                }
            if (config.HouseGoalsEnabled && !string.IsNullOrEmpty(p.House) && (Now() - p.HouseSince).TotalHours >= config.HouseMemberMinHours
                && (RealmHouses == null || HouseSize(p.House) >= config.HouseGoalMinMembers))
            {
                HouseQ h = HouseState(p.House);
                if (h != null)
                    foreach (HouseGoalState g in h.Goals)
                    {
                        HouseGoalDef def;
                        if (g.Done || !houseGoals.TryGetValue(g.GoalId, out def) || def.Objective.Type != TDeliver) continue;
                        int room = ShareLeft(id, g, def);
                        int open = def.Objective.Count - g.Progress;
                        int mine;
                        g.Contrib.TryGetValue(id, out mine);
                        // Never more than the goal still needs; when it is full but short of helpers, a new helper may
                        // still give a real share (5%), so goods are not handed in for nothing.
                        room = open > 0 ? Math.Min(room, open) : (mine == 0 ? Math.Min(room, Math.Max(1, def.Objective.Count / 20)) : 0);
                        if (room <= 0) continue;
                        HouseGoalState gs = g;
                        HouseGoalDef gd = def;
                        needs.Add(new Need { Label = "House " + h.Name + ": " + def.Title, Obj = def.Objective, Remaining = room, Valid = delegate { return !gs.Done; }, Apply = delegate(int n) { Contribute(id, p, player, h, gs, gd, n); } });
                    }
            }
            return needs;
        }

        private void AddSlotNeeds(List<Need> needs, string id, PlayerQ p, Player player, List<Slot> slots, Dictionary<string, QuestDef> pool, string kind)
        {
            foreach (Slot s in slots)
            {
                QuestDef q;
                if (s.Done || s.Abandoned || s.QuestId == null || !pool.TryGetValue(s.QuestId, out q)) continue;
                for (int i = 0; i < q.Objectives.Count; i++)
                {
                    ObjectiveDef o = q.Objectives[i];
                    if (o.Type != TDeliver) continue;
                    int have = i < s.Progress.Count ? s.Progress[i] : 0;
                    if (have >= o.Count) continue;
                    int idx = i;
                    Slot slot = s;
                    QuestDef quest = q;
                    needs.Add(new Need
                    {
                        Label = q.Title, Obj = o, Remaining = o.Count - have,
                        Valid = delegate { return !slot.Done && !slot.Abandoned; },
                        Apply = delegate(int n)
                        {
                            if (slot.Done || slot.Abandoned) return;
                            while (slot.Progress.Count <= idx) slot.Progress.Add(0);
                            slot.Progress[idx] = Math.Min(o.Count, slot.Progress[idx] + n);
                            if (AllDone(quest, slot.Progress, false)) CompleteSlot(id, p, player, slot, quest, kind);
                        }
                    });
                }
            }
        }

        // /quest give lists; /quest give all hands in. Goods are taken measured and spent (a sink, not a store).
        private void Give(Player player, PlayerQ p, bool all)
        {
            List<Need> needs = Needs(player, p);
            Container inv = null;
            try { inv = player.GetInventory(); }
            catch (Exception) { inv = null; }
            if (inv == null || inv.Contents == null) { ReplyError(player, "GiveNoInventory"); return; }
            ItemCollection packs = inv.Contents;
            bool listed = false, gave = false;
            foreach (Need n in needs)
            {
                foreach (string target in n.Obj.Targets)
                {
                    if (n.Remaining <= 0) break;
                    InvItemBlueprint bp = FindBlueprint(target.TrimStart('~'));
                    if (bp == null) continue;
                    int carry = ItemCollection.AutoCount(packs, bp);
                    if (!all)
                    {
                        if (!listed) { Reply(player, "GiveList"); listed = true; }
                        Reply(player, "GiveLine", n.Label + " (" + bp.Name + ")", n.Remaining, carry);
                        continue;
                    }
                    int want = Math.Min(carry, n.Remaining);
                    if (want <= 0 || (n.Valid != null && !n.Valid())) continue;
                    int taken = TakeMeasured(packs, bp, want);
                    if (taken <= 0) { ReplyError(player, "GiveFailed"); continue; }
                    n.Remaining -= taken;
                    gave = true;
                    Reply(player, "GiveTaken", taken, bp.Name, n.Label);
                    MarkAction(player);
                    n.Apply(taken);
                    // Deeds count every hand-in too (only deeds: the task that asked for the goods was credited above).
                    if (config.AchievementsEnabled) AdvanceAchievements(player.Id.ToString(), p, player, TDeliver, bp.Name, taken, null, false);
                }
            }
            if (gave) { dirty = true; SaveData(); }
            else if (!listed) Reply(player, "GiveNothing");
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

        private void ShowStory(Player player, PlayerQ p)
        {
            if (story == null || !config.StoryEnabled) { Reply(player, "LogText", Msg("StoryOff", player)); return; }
            Reply(player, "StoryHeader", story.Title, story.Season);
            var popup = new StringBuilder();
            if (!string.IsNullOrEmpty(story.Prologue) && p.Story.Finished.Count == 0)
            {
                Reply(player, "StoryPrologue", story.Prologue);
                popup.Append(story.Prologue).Append("\n\n");
            }
            ChapterDef ch;
            string wait;
            QuestDef step = CurrentStep(p, out ch, out wait);
            if (ch != null)
            {
                Reply(player, "StoryChapter", ch.Act, ch.Title);
                if (!string.IsNullOrEmpty(ch.Intro)) Reply(player, "StoryIntro", ch.Intro);
                popup.Append(Fmt("StoryChapter", player, ch.Act, ch.Title)).Append("\n").Append(ch.Intro ?? "").Append("\n\n");
            }
            if (step != null)
            {
                Reply(player, "StoryStep", step.Title, step.Text);
                for (int i = 0; i < step.Objectives.Count; i++)
                    Reply(player, "LogObjective", step.Objectives[i].Text, i < p.Story.Progress.Count ? p.Story.Progress[i] : 0, step.Objectives[i].Count);
                popup.Append(step.Title).Append(": ").Append(step.Text);
                p.Story.IntroShown = ch.Id;
            }
            else
            {
                QuestDef dummy;
                ChapterDef dc;
                string line = StoryLine(player, p, out dummy, out dc);
                Reply(player, "LogText", line);
                popup.Append(PopupText(line));
                if (p.Story.Complete && !string.IsNullOrEmpty(story.Epilogue)) { Reply(player, "LogText", story.Epilogue); popup.Append("\n\n").Append(story.Epilogue); }
            }
            if (PopupsFor(player)) ShowInfoPopup(player, story.Title, popup.ToString(), Msg("PopupButton", player));
        }

        private void ShowHouse(Player player, PlayerQ p)
        {
            if (!config.HouseGoalsEnabled || houseGoals.Count == 0) { Reply(player, "LogText", Msg("StoryOff", player)); return; }
            if (string.IsNullOrEmpty(p.House)) { Reply(player, "LogText", Msg("HouseNone", player)); return; }
            if (RealmHouses != null && HouseSize(p.House) < config.HouseGoalMinMembers) { Reply(player, "LogText", Fmt("HouseTooSmall", player, config.HouseGoalMinMembers)); return; }
            HouseQ h = HouseState(p.House);
            if (h == null) return;
            Reply(player, "HouseHeader", h.Name);
            foreach (HouseGoalState g in h.Goals)
            {
                HouseGoalDef def;
                if (!houseGoals.TryGetValue(g.GoalId, out def)) continue;
                Reply(player, "HouseLine", def.Title, g.Progress, def.Objective.Count, g.Done ? " - " + Ok(Msg("HouseGoalDone", player)) : "");
                Reply(player, "HouseText", def.Text);
                Reply(player, "HouseText", def.Objective.Text);
                var top = new List<KeyValuePair<string, int>>(g.Contrib);
                top.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                var names = new List<string>();
                for (int i = 0; i < top.Count && i < 5; i++)
                {
                    PlayerQ m = FindPlayer(top[i].Key);
                    names.Add((m != null ? m.Name : "?") + " " + top[i].Value);
                }
                if (names.Count > 0) Reply(player, "HouseTop", string.Join(", ", names.ToArray()));
                Reply(player, "HouseReward", def.Points, (int)Math.Round(def.Marks * config.RewardScale));
            }
        }

        #endregion

        #region Commands: /achievements

        [ChatCommand("achievements")]
        private void CmdAchievements(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (args == null) args = new string[0];
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            if (!config.Enabled || !config.AchievementsEnabled) { ReplyError(player, "AchOff"); return; }
            if (achievements.Count == 0) { Reply(player, "NoContent"); return; }
            PlayerQ p = Seen(player);
            if (p == null) return;
            string arg = args.Length > 0 ? string.Join(" ", args).Trim() : "";
            string low = arg.ToLowerInvariant();
            if (arg.Length == 0) { AchSummary(player, p); return; }
            if (low == "top") { AchTop(player); return; }
            if (Array.IndexOf(Categories, low) >= 0) { AchList(player, p, low); return; }
            AchievementDef a = FindAchievement(arg);
            if (a == null) { ReplyError(player, "AchUnknown", Clean(arg)); return; }
            AchDetail(player, p, a);
        }

        private AchievementDef FindAchievement(string text)
        {
            AchievementDef a;
            if (achievementById.TryGetValue(text, out a)) return a;
            AchievementDef hit = null;
            foreach (AchievementDef d in achievements)
            {
                if (string.Equals(d.Name, text, StringComparison.OrdinalIgnoreCase)) return d;
                if (d.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { if (hit != null) return null; hit = d; }
            }
            return hit;
        }

        private int TiersOf(PlayerQ p)
        {
            int n = 0;
            foreach (AchievementDef a in achievements) { AchState st; if (p.Ach.TryGetValue(a.Id, out st)) n += Math.Min(st.Tier, a.Tiers.Count); }
            return n;
        }

        private void AchSummary(Player player, PlayerQ p)
        {
            int got = 0;
            foreach (AchievementDef a in achievements) { AchState st; if (p.Ach.TryGetValue(a.Id, out st) && st.Tier > 0) got++; }
            Reply(player, "AchHeader", got, achievements.Count, TiersOf(p));
            var popup = new List<string>();
            popup.Add(Fmt("AchHeader", player, got, achievements.Count, TiersOf(p)));
            foreach (string c in Categories)
            {
                int total = 0, have = 0, tiers = 0;
                foreach (AchievementDef a in achievements)
                {
                    if (a.Category != c) continue;
                    total++;
                    AchState st;
                    if (p.Ach.TryGetValue(a.Id, out st) && st.Tier > 0) { have++; tiers += st.Tier; }
                }
                if (total == 0) continue;
                string line = Fmt("AchCategory", player, Msg("Category." + c, player), have, total, tiers);
                player.SendMessage(line);
                popup.Add(line.Trim());
            }
            // The three deeds nearest their next tier.
            var near = new List<KeyValuePair<double, string>>();
            foreach (AchievementDef a in achievements)
            {
                AchState st;
                p.Ach.TryGetValue(a.Id, out st);
                int tier = st != null ? st.Tier : 0;
                if (tier >= a.Tiers.Count) continue;
                long count = st != null ? st.Count : 0;
                double frac = (double)count / a.Tiers[tier].Count;
                if (count > 0) near.Add(new KeyValuePair<double, string>(frac, a.Name + " " + count + "/" + a.Tiers[tier].Count));
            }
            near.Sort(delegate(KeyValuePair<double, string> x, KeyValuePair<double, string> y) { return y.Key.CompareTo(x.Key); });
            if (near.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < near.Count && i < 3; i++) names.Add(near[i].Value);
                Reply(player, "AchNear", string.Join("; ", names.ToArray()));
                popup.Add(Fmt("AchNear", player, string.Join("; ", names.ToArray())).Trim());
            }
            Reply(player, "AchFoot");
            if (PopupsFor(player)) ShowInfoPopup(player, Msg("PopupDeeds", player), string.Join("\n", popup.ToArray()), Msg("PopupButton", player));
        }

        private void AchList(Player player, PlayerQ p, string category)
        {
            Reply(player, "AchListHeader", Msg("Category." + category, player));
            foreach (AchievementDef a in achievements)
            {
                if (a.Category != category) continue;
                AchState st;
                p.Ach.TryGetValue(a.Id, out st);
                int tier = st != null ? st.Tier : 0;
                long count = st != null ? st.Count : 0;
                string tierText = tier > 0 ? TierName(tier - 1) : Msg("NotYet", player);
                string next = tier < a.Tiers.Count ? count + "/" + a.Tiers[tier].Count : Ok(Msg("SlotDone", player));
                Reply(player, "AchLine", a.Name, tierText, a.Text, next);
            }
        }

        private void AchDetail(Player player, PlayerQ p, AchievementDef a)
        {
            AchState st;
            p.Ach.TryGetValue(a.Id, out st);
            long count = st != null ? st.Count : 0;
            int tier = st != null ? st.Tier : 0;
            Reply(player, "AchDetail", a.Name, Msg("Category." + a.Category, player), a.Text);
            for (int i = 0; i < a.Tiers.Count; i++)
                Reply(player, "AchDetailTier", TierName(i), Math.Min(count, a.Tiers[i].Count) + "/" + a.Tiers[i].Count, i < tier ? " - " + Ok(Msg("SlotDone", player)) : "");
        }

        private void AchTop(Player player)
        {
            var rows = new List<KeyValuePair<int, string>>();
            foreach (KeyValuePair<string, PlayerQ> kv in data.Players)
            {
                int t = TiersOf(kv.Value);
                if (t > 0) rows.Add(new KeyValuePair<int, string>(t, kv.Value.Name));
            }
            rows.Sort(delegate(KeyValuePair<int, string> x, KeyValuePair<int, string> y) { int c = y.Key.CompareTo(x.Key); return c != 0 ? c : string.CompareOrdinal(x.Value, y.Value); });
            Reply(player, "AchTopHeader");
            if (rows.Count == 0) { Reply(player, "AchTopNone"); return; }
            for (int i = 0; i < rows.Count && i < 10; i++) Reply(player, "AchTopLine", i + 1, rows[i].Value, rows[i].Key);
        }

        #endregion

        #region Admin

        private void Admin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status": AdminStatus(player); break;
                case "reload": LoadContent(); CheckItems(); foreach (Player pl in OnlinePlayers()) Seen(pl); Reply(player, "AdminReloaded"); AdminStatus(player); break;
                case "places": AdminPlaces(player); break;
                case "place": AdminPlace(player, args); break;
                case "reset": AdminReset(player, args); break;
                case "complete": AdminComplete(player, args); break;
                case "creatures":
                    if (recentCreatures.Count == 0) Reply(player, "AdminCreaturesNone");
                    else Reply(player, "AdminCreatures", string.Join(", ", recentCreatures.ToArray()));
                    break;
                case "items": AdminItems(player, args); break;
                default: Reply(player, "AdminHelp"); break;
            }
        }

        private void AdminStatus(Player player)
        {
            Reply(player, "AdminStatus", dailies.Count, weeklies.Count, story != null ? story.Chapters.Count : 0, achievements.Count, houseGoals.Count, places.Count, data.Places.Count);
            var files = new List<string>();
            foreach (KeyValuePair<string, string> kv in contentStatus) files.Add(kv.Key + " " + kv.Value);
            Reply(player, "AdminFiles", files.Count > 0 ? string.Join(", ", files.ToArray()) : "-");
            for (int i = 0; i < contentProblems.Count && i < 8; i++) Reply(player, "AdminProblem", contentProblems[i]);
            var feeds = new List<string>();
            foreach (KeyValuePair<string, string> kv in feedStatus) feeds.Add(kv.Key + ": " + kv.Value);
            Reply(player, "AdminFeeds", feeds.Count > 0 ? string.Join("; ", feeds.ToArray()) : "-");
            long marks = 0;
            int goods = 0;
            foreach (PlayerQ p in data.Players.Values) { marks += p.PendingMarks; foreach (OwedItem o in p.Owed) goods += o.Amount; }
            int day = SeasonDay();
            Reply(player, "AdminTotals", data.Players.Count, marks, goods, day >= 0 ? (day + 1).ToString() : "?");
        }

        private void AdminPlaces(Player player)
        {
            foreach (string id in placeOrder)
            {
                PlaceDef d = places[id];
                PlaceMark m;
                string where = data.Places.TryGetValue(id, out m)
                    ? string.Format("{0:0} {1:0} {2:0}, r {3:0} m", m.X, m.Y, m.Z, m.Radius > 0 ? m.Radius : d.Radius)
                    : Msg("AdminPlaceUnset", player);
                Reply(player, "AdminPlaceLine", d.Name, id, where);
            }
        }

        private void AdminPlace(Player player, string[] args)
        {
            if (args.Length < 4) { Reply(player, "AdminHelp"); return; }
            string action = args[2].ToLowerInvariant();
            string id = args[3].ToLowerInvariant();
            PlaceDef def;
            if (!places.TryGetValue(id, out def)) { ReplyError(player, "AdminPlaceUnknown", Clean(id)); return; }
            if (action == "clear")
            {
                data.Places.Remove(id);
                SaveData();
                Reply(player, "AdminPlaceCleared", def.Name);
                return;
            }
            if (action != "set") { Reply(player, "AdminHelp"); return; }
            float x, y, z;
            if (!TryPosition(player, out x, out y, out z)) { ReplyError(player, "AdminNoPosition"); return; }
            float r = def.Radius;
            float given;
            if (args.Length > 4 && float.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out given) && given >= 5f && given <= 500f) r = given;
            data.Places[id] = new PlaceMark { X = x, Y = y, Z = z, Radius = r, By = Clean(player.Name), At = Now() };
            SaveData();
            Reply(player, "AdminPlaceSet", def.Name, (int)r);
        }

        private string FindKnownId(string name, out string display)
        {
            display = null;
            Player online = FindOnline(name);
            if (online != null) { display = online.Name; return online.Id.ToString(); }
            if (IsSteamId(name) && data.Players.ContainsKey(name)) { display = data.Players[name].Name; return name; }
            string hit = null;
            foreach (KeyValuePair<string, PlayerQ> kv in data.Players)
                if (SameName(kv.Value.Name, name)) { if (hit != null) return null; hit = kv.Key; display = kv.Value.Name; }
            return hit;
        }

        private void AdminReset(Player player, string[] args)
        {
            if (args.Length < 3) { Reply(player, "AdminHelp"); return; }
            string what = args.Length > 3 ? args[args.Length - 1].ToLowerInvariant() : "all";
            bool named = what == "daily" || what == "weekly" || what == "story" || what == "all";
            if (!named) what = "all";
            string name = JoinFrom(args, 2, named && args.Length > 3 ? args.Length - 1 : args.Length);
            string display;
            string id = FindKnownId(name, out display);
            if (id == null) { ReplyError(player, "AdminPlayerNotFound"); return; }
            PlayerQ p = FindPlayer(id) ?? GetPlayer(id, display, true);
            if (what == "daily" || what == "all") { p.DayKey = null; p.Daily = new List<Slot>(); p.Rerolls = 0; }
            if (what == "weekly" || what == "all") { p.WeekKey = null; p.Weekly = new List<Slot>(); }
            if (what == "story" || what == "all") p.Story = new StoryState();
            Roll(id, p);
            dirty = true;
            SaveData();
            Puts("Quests of " + display + " (" + id + ") reset (" + what + ") by " + player.Name);
            Reply(player, "AdminReset", display, what);
        }

        // Testing aid: completes an open task (or the current story step) for a player, rewards and all.
        private void AdminComplete(Player player, string[] args)
        {
            if (args.Length < 4) { Reply(player, "AdminHelp"); return; }
            string questId = args[args.Length - 1].ToLowerInvariant();
            string display;
            string id = FindKnownId(JoinFrom(args, 2, args.Length - 1), out display);
            if (id == null) { ReplyError(player, "AdminPlayerNotFound"); return; }
            PlayerQ p = FindPlayer(id);
            if (p == null) { ReplyError(player, "AdminPlayerNotFound"); return; }
            Player online = OnlineById(id);
            foreach (Slot s in p.Daily)
                if (!s.Done && !s.Abandoned && s.QuestId == questId && dailies.ContainsKey(questId)) { CompleteSlot(id, p, online, s, dailies[questId], KDaily); Reply(player, "AdminCompleted", questId, display); SaveData(); return; }
            foreach (Slot s in p.Weekly)
                if (!s.Done && !s.Abandoned && s.QuestId == questId && weeklies.ContainsKey(questId)) { CompleteSlot(id, p, online, s, weeklies[questId], KWeekly); Reply(player, "AdminCompleted", questId, display); SaveData(); return; }
            ChapterDef ch;
            string wait;
            QuestDef step = CurrentStep(p, out ch, out wait);
            if (step != null && step.Id == questId) { CompleteStep(id, p, online, ch, step); Reply(player, "AdminCompleted", questId, display); SaveData(); return; }
            ReplyError(player, "AdminUnknownQuest", Clean(questId), display);
        }

        private void AdminItems(Player player, string[] args)
        {
            string word = args.Length > 2 ? JoinFrom(args, 2, args.Length) : "";
            var hits = new List<string>();
            List<string> names = null;
            try { names = InvBlueprints.Instance != null ? InvBlueprints.Instance.AllBlueprintNames : null; }
            catch (Exception) { names = null; }
            if (names != null)
                foreach (string n in names) if (n != null && (word.Length == 0 || n.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)) hits.Add(n);
            if (hits.Count == 0) { Reply(player, "AdminItemsNone", Clean(word)); return; }
            hits.Sort(StringComparer.OrdinalIgnoreCase);
            if (hits.Count > 25) hits.RemoveRange(25, hits.Count - 25);
            Reply(player, "AdminItems", Clean(word), string.Join(", ", hits.ToArray()));
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // Lets another Realm plugin report something a player did (types contract, event or custom), e.g.
        // RealmQuests.Call("ReportQuestEvent", id, "custom", "my_deed", 1). Daily credit caps apply per caller type.
        private bool ReportQuestEvent(string playerId, string type, string subject, int amount)
        {
            if (loadFailed || data == null || !IsSteamId(playerId) || type == null || Array.IndexOf(ExternalTypes, type) < 0) return false;
            if (amount <= 0) return false;
            PlayerQ p = FindPlayer(playerId);
            if (p == null) return false;
            int granted = TakeCredit(p, "external:" + type, Math.Min(amount, 100), 200);
            if (granted == 0) return false;
            return Record(playerId, null, type, Clean(subject), granted, null);
        }

        // Deeds (tiers) a player holds.
        private int GetAchievementCount(string playerId)
        {
            PlayerQ p = FindPlayer(playerId);
            return p != null ? TiersOf(p) : 0;
        }

        // "Act 2: The Charter Tested - <step title>", "complete", or null.
        private string GetStoryProgress(string playerId)
        {
            PlayerQ p = FindPlayer(playerId);
            if (p == null || story == null) return null;
            if (p.Story.Complete) return "complete";
            ChapterDef ch;
            string wait;
            QuestDef step = CurrentStep(p, out ch, out wait);
            return step != null ? "Act " + ch.Act + ": " + ch.Title + " - " + step.Title : null;
        }

        // A house's goal this week, as one plain line (for a painted board), or null.
        private string GetHouseGoalText(string house)
        {
            if (data == null || string.IsNullOrEmpty(house) || !config.HouseGoalsEnabled) return null;
            HouseQ h = HouseState(house);
            if (h == null || h.Goals.Count == 0) return null;
            var parts = new List<string>();
            foreach (HouseGoalState g in h.Goals)
            {
                HouseGoalDef def;
                if (houseGoals.TryGetValue(g.GoalId, out def)) parts.Add(def.Title + " " + g.Progress + "/" + def.Objective.Count + (g.Done ? " (met)" : ""));
            }
            return parts.Count > 0 ? string.Join("; ", parts.ToArray()) : null;
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): the game's own window, opened with
        //   ShowPopup(this Player, string title, string message, string buttonText, Dialogue.OnSubmit handler,
        //             bool interupt, bool broadcast)   [ASM CodeHatch.Common.PlayerExtensions]
        // broadcast = true or a dedicated server never sends it. The chat lines are always sent as well.
        // UNVERIFIED in game: that the window shows, how much text fits, and that "\n" breaks lines in it.
        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.UsePopups) return false;
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

        #region Helpers

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title);
            if (RealmChronicle == null) return;
            RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
        }

        private static bool UnderHourlyCap(Queue<DateTime> q, int cap, DateTime now)
        {
            while (q.Count > 0 && (now - q.Peek()).TotalHours >= 1) q.Dequeue();
            if (q.Count >= cap) return false;
            q.Enqueue(now);
            return true;
        }

        // True when this key has not fired within the last `seconds` (and records it).
        private bool Throttle(string key, int seconds)
        {
            DateTime now = Now();
            DateTime last;
            if (seconds > 0 && lastNotice.TryGetValue(key, out last) && (now - last).TotalSeconds < seconds) return false;
            lastNotice[key] = now;
            if (lastNotice.Count > 2000) lastNotice.Clear();
            return true;
        }

        private string HouseOf(string id)
        {
            if (RealmHouses == null || id == null) return null;
            string h = RealmHouses.Call("GetHouse", id) as string;
            return string.IsNullOrEmpty(h) ? null : h;
        }

        // Liege or vassal of each other, or bound by a treaty.
        private bool Allied(string a, string b)
        {
            if (a == null || b == null || RealmHouses == null) return false;
            if (SameName(RealmHouses.Call("GetLiege", a) as string, b) || SameName(RealmHouses.Call("GetLiege", b) as string, a)) return true;
            object r = RealmHouses.Call("HasTreaty", a, b);
            return r is bool && (bool)r;
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private static List<Player> OnlinePlayers()
        {
            var list = new List<Player>();
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private static Player OnlineById(string id)
        {
            ulong u;
            if (id == null || !ulong.TryParse(id, out u)) return null;
            Player p = Server.GetPlayerById(u);
            return p != null && !p.IsServer ? p : null;
        }

        private static Player FindOnline(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Player exact = Server.GetPlayerByName(name);
            if (exact != null && !exact.IsServer) return exact;
            List<Player> matches = Server.MatchPlayerByName(name);
            if (matches == null) return null;
            Player found = null;
            foreach (Player p in matches)
            {
                if (p == null || p.IsServer) continue;
                if (found != null) return null;                        // ambiguous
                found = p;
            }
            return found;
        }

        private static bool SameName(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSteamId(string id)
        {
            ulong u;
            return id != null && id.Length >= 17 && id.Length <= 20 && ulong.TryParse(id, out u);
        }

        private static string JoinFrom(string[] args, int start, int end)
        {
            if (args == null || start >= end) return "";
            var parts = new List<string>();
            for (int i = start; i < end && i < args.Length; i++) parts.Add(args[i]);
            return string.Join(" ", parts.ToArray()).Trim();
        }

        // Names and player text go into chat and logs: no colour tags, no control characters, at most 40 characters.
        private static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c) && c != '[' && c != ']' && c != '{' && c != '}') sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > 40 ? t.Substring(0, 40) : t;
        }

        // Addresses are kept only as a salted hash, in memory, for the one-hearth limit.
        private static string Hash(string s)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        #endregion
    }
}
