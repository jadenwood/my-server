// RealmCrafts: the guilds of Ostreval. Eight professions with ranks and mastery, perks the server can really grant,
// a weekly Master Crafter, house workshops that pool their members' work, and a commission board with escrow.
//
//   Professions  Gathering: woodcutting, mining, foraging, hunting. Crafting: smithing, carpentry, tailoring, cooking.
//                Experience (XP) comes from what the server itself sees (below). Levels 1 to MaxLevel (50) on a rising
//                curve; ranks Novice, Apprentice (10), Journeyman (20), Expert (30), Artisan (40) and Master (50). A day
//                has a cap per profession (DailyXpCap), so nobody grinds a season away in a weekend.
//   Perks        Bonus yield: a gatherer's level (plus their house workshop and a RealmDominion holding of the right
//                kind) adds a share of every gathered good, given by the server into their packs in batches.
//                Extra item: a crafter's level gives a chance of one more of what they just made, into their packs.
//                Market fee: from Expert on, RealmTreasury takes a smaller market fee when the player sells goods of
//                their own craft (RealmTreasury asks GetMarketFeeDiscount); commissions get the same discount.
//                Recognition: every rank is told to the player; Expert and above to the realm; Master earns the
//                RealmRenown deed craft_master (the title Guildmaster), a Chronicle entry and a window.
//   Weekly       The player with the most XP of the week (MinWeeklyXp at least) is named Master Crafter at the weekly
//                crowning: marks from RealmTreasury.RewardMarks, the RealmRenown deed master_crafter (the title Master
//                Crafter), a Chronicle entry and season points for their house. /craft top shows the week.
//   Workshops    A house's members pool the XP they earn into its workshop for the week. The workshop's tier (the better
//                of this week and last week) gives every member more XP, more yield and a better extra-item chance;
//                at the crowning each house that reached a tier earns RealmSeasons points.
//   Commissions  /craft order <item> <qty> <marks each>: the marks go into a RealmTreasury hold (HoldMarks) at once.
//                Another player fills it with goods from their packs (/craft fill), measured; the goods go to the
//                poster (or wait for /craft collect) and the filler is paid from the hold (PayFromHold) less the
//                market fee (ChargeMarks to the crown). Cancelled or lapsed: the hold goes back (ReleaseHold).
//
// How the server sees the work ([DEC] = read in the decompiled shipped patched Assembly-CSharp.dll, type.member names
// only; [ASM] = in its metadata; other tags as in docs/oxide-rok-api.md). No game code is copied here.
//   Gathering  There is no gather hook (docs/oxide-rok-api.md 3.9): the client gathers into its own packs and then
//              tells the server, which checks and applies it [DEC ContainerListener.OnContainerItemAdd /
//              OnContainerItemMerge]. So gathering is read from those container events, subscribed with
//              EventManager.Subscribe (as RealmTreasury reads ItemPassEvent): ContainerItemAddEvent (ItemStack, Slot),
//              ContainerItemMergeEvent (ItemStack, TargetStack, Quantity), ContainerItemRemoveEvent and
//              ContainerItemSplitEvent (ResultStack, Quantity), each with Sender, IsSender, Cancelled and the
//              container's Entity [ASM]. A good counts as gathered only when a player's own client puts it into a
//              container on the player's own entity AND it came from nowhere the server knows: not out of a container
//              (a chest, a dropped item pack, a corpse, a station; seen as a Remove or Split by the same client a moment
//              before, or as a merge whose source stack is still in a collection), and not handed to them by the server
//              (ItemPassEvent: salvage, broken containers, block pick-ups [DEC SalvageSupplier.AddToInventory,
//              DamagableContainer, InventoryUtil]; only crops from a farm, LootCountsAsGather, count). Server-made
//              changes (gifts, payouts, kits, this plugin's own bonus) are never counted.
//   Corpses    Goods taken out of a creature's corpse (LootableCreatureContainer [ASM]) count for hunting, at most
//              CorpseCreditPerContainer units per corpse.
//   Hunting    Creature kills: OnEntityDeath [OPJ L188], a creature as the game's SalvageSupplier tells one (MonsterMotor
//              or MonsterEntity), killer evt.KillingDamage.DamageSource.Owner (as RealmQuests).
//   Crafting   ItemCrafterCraftEvent (Sender asks Crafter to start) and ItemCrafterItemEvent (each product Stack the
//              server makes), as RealmQuests reads them; OnItemCrafted [OPJ L1088] forgets the claim. The profession of a
//              product is read from its name (config rules) or else the station's name; /craft admin unmapped lists
//              what matched nothing.
//   Items      Gifts (bonus yield, extra items, commission goods): the game's own /give path, PlayerExtensions.GetInventory
//              -> Container.Contents, new InvGameItemStack(blueprint, n, null) in chunks of ContainerManagement.StackLimit,
//              ItemCollection.AutoMergeAdd; takes with ItemCollection.AutoSplit. Every amount is measured with
//              ItemCollection.AutoCount before and after, never assumed. RealmSentinel is told (SentinelItemSource).
//
// Anti-abuse: gather-drop-regather, chest shuffles, merges and splits out of containers are transfers (above), never
// gathers. Craft-and-salvage loops: salvage yields are server hand-outs (not gathers); each product gives full XP for its
// first FullXpCraftsPerProductPerDay crafts a day, half for the next HalfXpCraftsPerProductPerDay, then none; and every
// profession has a daily cap. Alt feeding: goods passed between players are transfers; commission XP needs a poster from
// another address (salted hash, memory only), a price of at least MinPriceRatioForXp of the market's last price, and the
// same two players earn it once per PairCooldownHours; workshop XP counts only from members of WorkshopMinMemberHours, at
// most MemberCapXpPerWeek each and AccountsPerAddress accounts from one address. Bonus items and extra items have
// daily caps.
//
// Commands: /craft (your professions), /craft <profession>, perks, top [week|<profession>|houses], house,
//   orders [mine|<word>], order <item> <qty> <marks each> [min level], fill <id> [qty], cancel <id>, collect, help.
// Admin (realmcrafts.admin): /craft admin status | watch <player> [off] | unmapped | items <word> | xp <player>
//   <profession> <amount> | level <player> <profession> <level> | reset <player> confirm | crown | cancel <id>.
// API (non-public, Plugin.Call): GetMarketFeeDiscount(string playerId, string item) -> int (0-50),
//   GetProfessionLevel(string playerId, string profession) -> int, GetMasterCrafter() -> string,
//   GetWorkshopTier(string house) -> int, GetCraftSummary(string playerId) -> string.
// Calls out (all optional; a missing plugin or method reads as "not available"):
//   RealmTreasury.HoldMarks / PayFromHold / ReleaseHold / GetHold / ChargeMarks / RewardMarks / GetPurse / GetLastPrice /
//     GetTreasurySummary      RealmRenown.AddDeed      RealmHouses.GetHouse      RealmSeasons.AwardHouse
//   RealmChronicle.Log (title_earned)   RealmHerald.PopupsWanted   RealmQuests.ReportQuestEvent (custom: commission,
//   mastery)   RealmDominion.GetHoldings   RealmSentinel.SentinelItemSource
// Data: oxide/data/RealmCrafts.json. If it exists but cannot be read (a cut-off file, say) the plugin does nothing and
// NEVER writes it; a copy of the last good file is kept as RealmCrafts_lastgood. Gifts leave the owed ledger (saved)
// before they are given, and only the shortfall goes back: a crash can lose a gift, never duplicate one.
// Language level: C# 3 syntax, .NET 3.5 API surface. Cross-plugin methods are non-public (Oxide calls NonPublic|Instance).
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmCrafts.md for the in-game test of each part.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeHatch.AI;                                  // MonsterMotor [ASM]
using CodeHatch.Common;                              // PlayerExtensions: SendMessage, SendError, GetInventory, ShowPopup [ASM]
using CodeHatch.Damaging;                            // Damage [ASM]
using CodeHatch.Engine.Behaviours;                   // ItemCrafter [ASM]
using CodeHatch.Engine.Core.Cache;                   // Entity, MonsterEntity [ASM]
using CodeHatch.Engine.Networking;                   // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;                // InvItemBlueprint, InvBlueprints [ASM]
using CodeHatch.Inventory.Blueprints.Components;     // ContainerManagement [ASM]
using CodeHatch.ItemContainer;                       // Container, ItemCollection, LootableCreatureContainer [ASM]
using CodeHatch.Networking.Events;                   // EventManager, EventSubscriber<T>, EventHandlerOrder [ASM]
using CodeHatch.Networking.Events.Containers;        // ContainerItemAddEvent, ...MergeEvent, ...RemoveEvent, ...SplitEvent [ASM]
using CodeHatch.Networking.Events.Entities;          // EntityDeathEvent, ItemCrafterCraftEvent, ItemCrafterItemEvent, ItemCrafterFinishEvent [ASM]
using CodeHatch.Networking.Events.Item;              // ItemPassEvent [ASM]
using Oxide.Core;                                    // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                            // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmCrafts", "Realm", "0.1.0")]
    [Description("Professions and mastery: gathering and crafting ranks, perks, a weekly Master Crafter, house workshops and commissions")]
    public class RealmCrafts : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmQuests;
        [PluginReference] private Plugin RealmDominion;
        [PluginReference] private Plugin RealmSentinel;

        private const string PermAdmin = "realmcrafts.admin";
        private const string DataName = "RealmCrafts";
        private const string BackupName = "RealmCrafts_lastgood";
        private const int DataFormat = 1;
        private const string HoldSource = "RealmCrafts";
        private const string TitleChronicleType = "title_earned";

        private const string PWood = "woodcutting";
        private const string PMine = "mining";
        private const string PForage = "foraging";
        private const string PHunt = "hunting";
        private const string PSmith = "smithing";
        private const string PCarpentry = "carpentry";
        private const string PTailor = "tailoring";
        private const string PCook = "cooking";
        private static readonly string[] Professions = { PWood, PMine, PForage, PHunt, PSmith, PCarpentry, PTailor, PCook };
        private static readonly string[] GatherProfs = { PWood, PMine, PForage, PHunt };
        private static readonly string[] CraftProfs = { PSmith, PCarpentry, PTailor, PCook };

        private const string SOpen = "open";
        private const string SDone = "done";
        private const string SCancelled = "cancelled";
        private const string SExpired = "expired";

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private Timer tickTimer;
        private DateTime nextSave = DateTime.MinValue;
        private DateTime nextHouseCheck = DateTime.MinValue;
        private DateTime nextDominion = DateTime.MinValue;
        private DateTime nextBonus = DateTime.MinValue;
        private long[] xpTable;

        // Session state (never saved).
        private readonly Dictionary<ulong, Tracker> trackers = new Dictionary<ulong, Tracker>();
        private readonly Dictionary<object, Early> early = new Dictionary<object, Early>();
        private readonly Dictionary<object, CraftClaim> craftClaims = new Dictionary<object, CraftClaim>();
        private readonly Dictionary<object, int> corpseTaken = new Dictionary<object, int>();
        private readonly Dictionary<ulong, string> addressOf = new Dictionary<ulong, string>();
        private readonly Dictionary<string, Dictionary<string, List<string>>> weekAddresses = new Dictionary<string, Dictionary<string, List<string>>>();
        private string weekAddressesKey;
        private readonly Dictionary<string, List<string>> holdingKinds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InvItemBlueprint> blueprintCache = new Dictionary<string, InvItemBlueprint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GatherDef> gatherByItem = new Dictionary<string, GatherDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> throttle = new Dictionary<string, DateTime>();
        private readonly Dictionary<ulong, ulong> watchers = new Dictionary<ulong, ulong>();          // watched player -> admin
        private readonly Dictionary<string, DateTime> resetConfirm = new Dictionary<string, DateTime>();
        private readonly List<string> unmapped = new List<string>();
        private readonly List<DateTime> heralds = new List<DateTime>();
        private bool giving;                                     // our own gift is going into someone's packs

        private EventSubscriber<ContainerItemAddEvent> addEarlySub, addLateSub;
        private EventSubscriber<ContainerItemMergeEvent> mergeEarlySub, mergeLateSub;
        private EventSubscriber<ContainerItemRemoveEvent> removeSub;
        private EventSubscriber<ContainerItemSplitEvent> splitSub;
        private EventSubscriber<ItemPassEvent> passSub;
        private EventSubscriber<ItemCrafterCraftEvent> craftSub;
        private EventSubscriber<ItemCrafterItemEvent> craftItemSub;

        // Clock and dice indirection so the behaviour tests can move time and fix the rolls; on a server always
        // DateTime.UtcNow and System.Random.
        private Func<DateTime> clock = DefaultClock;
        private Func<double> roll = DefaultRoll;
        private static readonly System.Random Dice = new System.Random();

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private static double DefaultRoll()
        {
            return Dice.NextDouble();
        }

        #region Config

        private class GeneralSection
        {
            public bool Enabled = true;                 // master switch: off = /craft answers "closed", nothing is counted
            public bool UsePopups = true;               // the mastery and Master Crafter windows (chat is always sent too)
            public float TickSeconds = 5f;
            public int SaveEverySeconds = 60;
            public int MaxPlayersKept = 20000;          // the longest-unseen records with no XP go first beyond this
            public int AdminMaxAdjust = 100000;         // the most /craft admin xp may add or take at once
            public bool AdminsCountForWeekly = false;   // holders of realmcrafts.admin can be named Master Crafter
        }

        private class LevelSection
        {
            public int MaxLevel = 50;
            public double XpCurveBase = 40;             // XP to reach level L = XpCurveBase * (L - 1) ^ XpCurveExponent
            public double XpCurveExponent = 2.0;
            public int DailyXpCap = 2500;               // per profession per realm day (UTC)
            public List<int> RankLevels;                // Novice, Apprentice, Journeyman, Expert, Artisan, Master
            public int HeraldFromRank = 3;              // reaching this rank (Expert) or higher is told to the realm
            public int HeraldsPerHour = 6;
        }

        private class GatherDef
        {
            public string Item;                         // a ResourceType name or an exact item name
            public string Profession;
            public double Xp;                           // per unit gathered
        }

        private class CreatureDef
        {
            public string Match;                        // a word in the creature's name (UNVERIFIED names: /quest admin creatures)
            public double Xp;
        }

        private class GatherSection
        {
            public bool Enabled = true;
            public int TransitSeconds = 15;             // goods out of a container this long ago still count as moved
            public int PassSeconds = 30;                // goods the server handed over this long ago still count as handed
            public int UnitsPerItemPerDay = 8000;       // gathered units credited per item per day
            public int CorpseCreditPerContainer = 60;   // units taken from one creature's corpse that count for hunting
            public double CreatureDefaultXp = 15;
            public int CreatureCreditsPerDay = 80;
            public List<GatherDef> Items;
            public List<string> LootCountsAsGather;     // a farm's harvest (an ItemPassEvent "Loot") counts for these
            public List<CreatureDef> Creatures;
        }

        private class KeywordRule
        {
            public string Profession;
            public List<string> Words;
        }

        private class CraftSection
        {
            public bool Enabled = true;
            public double DefaultCraftXp = 15;          // per product stack made
            public int FullXpCraftsPerProductPerDay = 20;
            public int HalfXpCraftsPerProductPerDay = 40;
            public int ClaimMinutes = 120;              // how long a station remembers who asked it to craft
            public Dictionary<string, string> Products; // exact product name -> profession (wins over the rules)
            public Dictionary<string, double> XpWeights;// a word in the product name -> XP multiplier (the largest applies)
            public List<KeywordRule> ProductRules;      // in order: the first rule with a word in the product name wins
            public List<KeywordRule> StationRules;      // then a word in the station's name
            public List<string> Ignore;                 // products that give no XP at all
        }

        private class TierLevel
        {
            public int Level;
            public int Percent;
        }

        private class PerkSection
        {
            public double BonusYieldPercentPerLevel = 0.4;  // level 50: +20 % of every gathered good
            public int BonusYieldMaxPerItemPerDay = 500;
            public int BonusEverySeconds = 30;          // bonus goods are given in batches, one line each time
            public double ExtraItemChancePerLevel = 0.2;// percent per level: level 50 = 10 %
            public int ExtraItemsPerDay = 10;
            public int ExtraItemMaxUnits = 20;          // a stackable product's extra is at most this many
            public List<string> NoExtraItems;           // words: products never doubled
            public List<TierLevel> MarketDiscount;      // from Level on, Percent off the market fee for your own goods
        }

        private class WorkshopSection
        {
            public bool Enabled = true;
            public int MinMemberHours = 24;             // a member counts once they have been in the house this long
            public int MemberCapXpPerWeek = 5000;
            public int AccountsPerAddress = 2;          // accounts from one address that count for one house a week
            public List<int> TierXp;                    // pooled weekly XP for tiers 1, 2, 3
            public List<int> XpBonusPercent;
            public List<int> YieldBonusPoints;
            public List<int> ExtraChancePoints;
            public List<int> SeasonPoints;              // RealmSeasons points at the crowning, by tier reached that week
        }

        private class WeeklySection
        {
            public bool Enabled = true;
            public string CrownDay = "Sunday";
            public int CrownHourUtc = 20;
            public int MinWeeklyXp = 1500;
            public long RewardMarks = 250;
            public int HousePoints = 3;
            public int TopCount = 10;
        }

        private class HoldingPerk
        {
            public string Kind;                         // a RealmDominion holding kind
            public string Profession;
            public int YieldPoints;                     // gathering: bonus yield percentage points
            public int ExtraChancePoints;               // crafting: extra item percentage points
        }

        private class DominionSection
        {
            public bool Enabled = true;
            public int PollSeconds = 300;
            public List<HoldingPerk> Perks;
        }

        private class CommissionSection
        {
            public bool Enabled = true;
            public int MaxOpenPerPlayer = 5;
            public int MaxOpen = 300;
            public int MaxQty = 500;
            public long MaxPrice = 100000;
            public long MaxTotal = 1000000;
            public int DurationHours = 72;
            public int PostCooldownSeconds = 30;
            public int FeePercent = -1;                 // -1 = the market fee RealmTreasury charges
            public double XpPerMark = 0.5;
            public int XpMaxPerFill = 300;
            public int XpPerDay = 1500;
            public int PairCooldownHours = 12;
            public double MinPriceRatioForXp = 0.5;     // of the market's last price for the item, when there is one
            public bool XpWithinHouse = true;
            public int MaxOwedLines = 50;
        }

        private class RewardSection
        {
            public string RankDeed = "craft_rank";          // RealmRenown deed kinds ("" = none)
            public string MasterDeed = "craft_master";
            public string WeeklyDeed = "master_crafter";
            public string CommissionDeed = "commission_filled";
            public bool ChronicleMasters = true;
            public bool QuestReports = true;            // RealmQuests ReportQuestEvent custom "commission" and "mastery"
        }

        private class PluginConfig
        {
            public GeneralSection General = new GeneralSection();
            public LevelSection Levels = new LevelSection();
            public GatherSection Gathering = new GatherSection();
            public CraftSection Crafting = new CraftSection();
            public PerkSection Perks = new PerkSection();
            public WorkshopSection Workshops = new WorkshopSection();
            public WeeklySection Weekly = new WeeklySection();
            public DominionSection Dominion = new DominionSection();
            public CommissionSection Commissions = new CommissionSection();
            public RewardSection Rewards = new RewardSection();
        }

        private static GatherDef G(string item, string prof, double xp)
        {
            return new GatherDef { Item = item, Profession = prof, Xp = xp };
        }

        private static KeywordRule K(string prof, params string[] words)
        {
            return new KeywordRule { Profession = prof, Words = new List<string>(words) };
        }

        // Defaults use ResourceType names ([ASM] CodeHatch.ResourceType), the one item list the DLL itself holds.
        // UNVERIFIED that each resolves to a carried item on a real server: /craft admin status shows what resolved.
        private static List<GatherDef> DefaultGatherItems()
        {
            return new List<GatherDef>
            {
                G("Wood", PWood, 0.5), G("Stick", PWood, 0.3),
                G("Stone", PMine, 0.5), G("IronOre", PMine, 2), G("Clay", PMine, 1), G("Sulphur", PMine, 1.5),
                G("Flax", PForage, 2), G("Berry", PForage, 1), G("Apple", PForage, 1), G("Grain", PForage, 1),
                G("Fiber", PForage, 0.5), G("Grass", PForage, 0.3), G("Flowers", PForage, 1), G("Roses", PForage, 1.5),
                G("Fern", PForage, 0.5), G("Cabbage", PForage, 1.5), G("Carrot", PForage, 1.5), G("BrownBeans", PForage, 1.5),
                G("RawMeat", PHunt, 2), G("Fat", PHunt, 2), G("Bone", PHunt, 1), G("LeatherHide", PHunt, 3),
                G("BearHide", PHunt, 6), G("DeerSkin", PHunt, 4), G("WolfPelt", PHunt, 5), G("RabbitPelt", PHunt, 2),
                G("Feather", PHunt, 1), G("RawBird", PHunt, 2), G("Heart", PHunt, 3), G("Liver", PHunt, 3), G("Fang", PHunt, 3)
            };
        }

        private static List<CreatureDef> DefaultCreatures()
        {
            return new List<CreatureDef>
            {
                new CreatureDef { Match = "bear", Xp = 80 }, new CreatureDef { Match = "wolf", Xp = 40 },
                new CreatureDef { Match = "stag", Xp = 30 }, new CreatureDef { Match = "deer", Xp = 25 },
                new CreatureDef { Match = "boar", Xp = 30 }, new CreatureDef { Match = "rabbit", Xp = 8 },
                new CreatureDef { Match = "chicken", Xp = 5 }, new CreatureDef { Match = "duck", Xp = 5 }
            };
        }

        // Product names are the game's (UNVERIFIED on a real server); /craft admin unmapped lists what matched nothing.
        private static List<KeywordRule> DefaultProductRules()
        {
            return new List<KeywordRule>
            {
                K(PCook, "Cooked", "Bread", "Pie", "Stew", "Soup", "Flour", "Baked", "Roast", "Smoked", "Jerky", "Cake", "Fire Water"),
                K(PTailor, "Leather", "Cloth", "Bandage", "Hide", "Fur", "Rope", "Banner", "Wool", "Bed", "Pelt", "Tapestry", "Flag"),
                K(PSmith, "Iron", "Steel", "Ingot", "Sword", "Mace", "Flail", "Halberd", "Morning Star", "Axe", "Pick", "Hammer",
                    "Plate", "Chain", "Helmet", "Lock", "Nail", "Dagger", "Spear", "Sickle", "Trap", "Cage", "Shackle"),
                K(PCarpentry, "Wood", "Wooden", "Log", "Lumber", "Plank", "Bow", "Arrow", "Chair", "Table", "Bench", "Torch", "Chest",
                    "Door", "Gate", "Ladder", "Shelf", "Stool", "Sign", "Crate", "Barrel", "Club", "Shield", "Stake", "Spikes")
            };
        }

        private static List<KeywordRule> DefaultStationRules()
        {
            return new List<KeywordRule>
            {
                K(PCook, "Fire", "Stove", "Oven", "Cook", "Bakery", "Mill", "Pot"),
                K(PTailor, "Tannery", "Tanning", "Loom", "Spinning", "Sewing", "Weav"),
                K(PSmith, "Smith", "Anvil", "Forge", "Smelter", "Furnace"),
                K(PCarpentry, "Carpent", "Saw", "Wood", "Fletch", "Workbench")
            };
        }

        private static Dictionary<string, double> DefaultXpWeights()
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            d.Add("Steel", 3); d.Add("Iron", 2); d.Add("Plate", 3); d.Add("Ingot", 1.5); d.Add("Arrow", 0.5);
            d.Add("Bandage", 0.5); d.Add("Torch", 0.5); d.Add("Stick", 0.3); d.Add("Flour", 0.5); d.Add("Lumber", 0.5);
            return d;
        }

        private static List<TierLevel> DefaultDiscount()
        {
            return new List<TierLevel> { new TierLevel { Level = 30, Percent = 10 }, new TierLevel { Level = 40, Percent = 20 }, new TierLevel { Level = 50, Percent = 30 } };
        }

        private static List<HoldingPerk> DefaultHoldingPerks()
        {
            return new List<HoldingPerk>
            {
                new HoldingPerk { Kind = "mine", Profession = PMine, YieldPoints = 5 },
                new HoldingPerk { Kind = "village", Profession = PForage, YieldPoints = 5 },
                new HoldingPerk { Kind = "village", Profession = PCook, ExtraChancePoints = 2 },
                new HoldingPerk { Kind = "harbour", Profession = PHunt, YieldPoints = 5 },
                new HoldingPerk { Kind = "keep", Profession = PSmith, ExtraChancePoints = 2 },
                new HoldingPerk { Kind = "crossroads", Profession = PTailor, ExtraChancePoints = 2 },
                new HoldingPerk { Kind = "crossroads", Profession = PCarpentry, ExtraChancePoints = 2 }
            };
        }

        // Lists and dictionaries are null in the classes and filled here: Newtonsoft appends to a list a class already
        // holds, so a default list in a field initializer would double on every load.
        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            FillDefaults(c);
            return c;
        }

        private static void FillDefaults(PluginConfig c)
        {
            if (c.Levels.RankLevels == null) c.Levels.RankLevels = new List<int> { 1, 10, 20, 30, 40, 50 };
            if (c.Gathering.Items == null) c.Gathering.Items = DefaultGatherItems();
            if (c.Gathering.LootCountsAsGather == null) c.Gathering.LootCountsAsGather = new List<string> { "Cabbage", "Carrot", "BrownBeans", "Grain", "Flax" };
            if (c.Gathering.Creatures == null) c.Gathering.Creatures = DefaultCreatures();
            if (c.Crafting.Products == null) c.Crafting.Products = new Dictionary<string, string>();
            if (c.Crafting.XpWeights == null) c.Crafting.XpWeights = DefaultXpWeights();
            if (c.Crafting.ProductRules == null) c.Crafting.ProductRules = DefaultProductRules();
            if (c.Crafting.StationRules == null) c.Crafting.StationRules = DefaultStationRules();
            if (c.Crafting.Ignore == null) c.Crafting.Ignore = new List<string>();
            if (c.Perks.NoExtraItems == null) c.Perks.NoExtraItems = new List<string> { "Ingot", "Flour", "Lumber" };
            if (c.Perks.MarketDiscount == null) c.Perks.MarketDiscount = DefaultDiscount();
            if (c.Workshops.TierXp == null) c.Workshops.TierXp = new List<int> { 6000, 25000, 75000 };
            if (c.Workshops.XpBonusPercent == null) c.Workshops.XpBonusPercent = new List<int> { 5, 10, 15 };
            if (c.Workshops.YieldBonusPoints == null) c.Workshops.YieldBonusPoints = new List<int> { 2, 4, 6 };
            if (c.Workshops.ExtraChancePoints == null) c.Workshops.ExtraChancePoints = new List<int> { 1, 2, 3 };
            if (c.Workshops.SeasonPoints == null) c.Workshops.SeasonPoints = new List<int> { 2, 4, 6 };
            if (c.Dominion.Perks == null) c.Dominion.Perks = DefaultHoldingPerks();
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static long ClampL(long v, long lo, long hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static double ClampD(double v, double lo, double hi) { return double.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v)); }

        private static List<int> ThreeTiers(List<int> list, int lo, int hi, bool rising)
        {
            var o = new List<int>();
            if (list != null) foreach (int v in list) if (o.Count < 3) o.Add(Clamp(v, lo, hi));
            while (o.Count < 3) o.Add(o.Count == 0 ? lo : o[o.Count - 1]);
            if (rising) for (int i = 1; i < 3; i++) if (o[i] < o[i - 1]) o[i] = o[i - 1];
            return o;
        }

        private void ClampConfig()
        {
            if (config == null) config = DefaultConfig();
            if (config.General == null) config.General = new GeneralSection();
            if (config.Levels == null) config.Levels = new LevelSection();
            if (config.Gathering == null) config.Gathering = new GatherSection();
            if (config.Crafting == null) config.Crafting = new CraftSection();
            if (config.Perks == null) config.Perks = new PerkSection();
            if (config.Workshops == null) config.Workshops = new WorkshopSection();
            if (config.Weekly == null) config.Weekly = new WeeklySection();
            if (config.Dominion == null) config.Dominion = new DominionSection();
            if (config.Commissions == null) config.Commissions = new CommissionSection();
            if (config.Rewards == null) config.Rewards = new RewardSection();
            FillDefaults(config);

            GeneralSection g = config.General;
            g.TickSeconds = ClampF(g.TickSeconds, 1f, 60f);
            g.SaveEverySeconds = Clamp(g.SaveEverySeconds, 10, 3600);
            g.MaxPlayersKept = Clamp(g.MaxPlayersKept, 100, 1000000);
            g.AdminMaxAdjust = Clamp(g.AdminMaxAdjust, 1, 10000000);

            LevelSection l = config.Levels;
            l.MaxLevel = Clamp(l.MaxLevel, 5, 200);
            l.XpCurveBase = ClampD(l.XpCurveBase, 1, 100000);
            l.XpCurveExponent = ClampD(l.XpCurveExponent, 1, 4);
            l.DailyXpCap = Clamp(l.DailyXpCap, 0, 10000000);
            var ranks = new List<int>();
            foreach (int r in l.RankLevels) if (r >= 1 && r <= l.MaxLevel && (ranks.Count == 0 || r > ranks[ranks.Count - 1])) ranks.Add(r);
            if (ranks.Count == 0 || ranks[0] != 1) ranks.Insert(0, 1);
            if (ranks[ranks.Count - 1] != l.MaxLevel) ranks.Add(l.MaxLevel);
            while (ranks.Count > 6) ranks.RemoveAt(ranks.Count - 2);
            l.RankLevels = ranks;
            l.HeraldFromRank = Clamp(l.HeraldFromRank, 1, 99);
            l.HeraldsPerHour = Clamp(l.HeraldsPerHour, 0, 120);

            GatherSection ga = config.Gathering;
            ga.TransitSeconds = Clamp(ga.TransitSeconds, 2, 300);
            ga.PassSeconds = Clamp(ga.PassSeconds, 2, 600);
            ga.UnitsPerItemPerDay = Clamp(ga.UnitsPerItemPerDay, 0, 10000000);
            ga.CorpseCreditPerContainer = Clamp(ga.CorpseCreditPerContainer, 0, 10000);
            ga.CreatureDefaultXp = ClampD(ga.CreatureDefaultXp, 0, 10000);
            ga.CreatureCreditsPerDay = Clamp(ga.CreatureCreditsPerDay, 0, 100000);
            var items = new List<GatherDef>();
            var seenItems = new List<string>();
            foreach (GatherDef d in ga.Items)
            {
                if (d == null || string.IsNullOrEmpty(d.Item) || d.Item.Trim().Length == 0) continue;
                string prof = NormalizeProfession(d.Profession);
                if (prof == null || Array.IndexOf(GatherProfs, prof) < 0 || seenItems.Contains(d.Item.Trim().ToLowerInvariant()))
                {
                    PrintWarning("Gathering item '" + d.Item + "' ignored: it needs a gathering profession (woodcutting, mining, foraging, hunting) and must be listed once.");
                    continue;
                }
                d.Item = d.Item.Trim();
                d.Profession = prof;
                d.Xp = ClampD(d.Xp, 0, 1000);
                seenItems.Add(d.Item.ToLowerInvariant());
                items.Add(d);
            }
            ga.Items = items;
            ga.LootCountsAsGather.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s) || s.Trim().Length == 0; });
            ga.Creatures.RemoveAll(delegate(CreatureDef c) { return c == null || string.IsNullOrEmpty(c.Match) || c.Match.Trim().Length == 0; });
            foreach (CreatureDef c in ga.Creatures) { c.Match = c.Match.Trim().ToLowerInvariant(); c.Xp = ClampD(c.Xp, 0, 10000); }

            CraftSection cr = config.Crafting;
            cr.DefaultCraftXp = ClampD(cr.DefaultCraftXp, 0, 10000);
            cr.FullXpCraftsPerProductPerDay = Clamp(cr.FullXpCraftsPerProductPerDay, 0, 100000);
            cr.HalfXpCraftsPerProductPerDay = Clamp(cr.HalfXpCraftsPerProductPerDay, 0, 100000);
            cr.ClaimMinutes = Clamp(cr.ClaimMinutes, 1, 1440);
            var products = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in cr.Products)
            {
                string prof = NormalizeProfession(kv.Value);
                if (string.IsNullOrEmpty(kv.Key) || prof == null || Array.IndexOf(CraftProfs, prof) < 0) { PrintWarning("Crafting product '" + kv.Key + "' ignored: it needs a crafting profession."); continue; }
                products[kv.Key.Trim()] = prof;
            }
            cr.Products = products;
            var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> kv in cr.XpWeights) if (!string.IsNullOrEmpty(kv.Key)) weights[kv.Key.Trim()] = ClampD(kv.Value, 0, 100);
            cr.XpWeights = weights;
            cr.ProductRules = CleanRules(cr.ProductRules, "ProductRules");
            cr.StationRules = CleanRules(cr.StationRules, "StationRules");
            cr.Ignore.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s) || s.Trim().Length == 0; });

            PerkSection p = config.Perks;
            p.BonusYieldPercentPerLevel = ClampD(p.BonusYieldPercentPerLevel, 0, 5);
            p.BonusYieldMaxPerItemPerDay = Clamp(p.BonusYieldMaxPerItemPerDay, 0, 1000000);
            p.BonusEverySeconds = Clamp(p.BonusEverySeconds, 5, 3600);
            p.ExtraItemChancePerLevel = ClampD(p.ExtraItemChancePerLevel, 0, 2);
            p.ExtraItemsPerDay = Clamp(p.ExtraItemsPerDay, 0, 10000);
            p.ExtraItemMaxUnits = Clamp(p.ExtraItemMaxUnits, 1, 1000);
            p.NoExtraItems.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s) || s.Trim().Length == 0; });
            var tiers = new List<TierLevel>();
            foreach (TierLevel t in p.MarketDiscount)
                if (t != null && t.Level >= 1) tiers.Add(new TierLevel { Level = Clamp(t.Level, 1, l.MaxLevel), Percent = Clamp(t.Percent, 0, 50) });
            tiers.Sort(delegate(TierLevel a, TierLevel b) { return a.Level.CompareTo(b.Level); });
            p.MarketDiscount = tiers;

            WorkshopSection w = config.Workshops;
            w.MinMemberHours = Clamp(w.MinMemberHours, 0, 8760);
            w.MemberCapXpPerWeek = Clamp(w.MemberCapXpPerWeek, 0, 10000000);
            w.AccountsPerAddress = Clamp(w.AccountsPerAddress, 0, 100);
            w.TierXp = ThreeTiers(w.TierXp, 1, 100000000, true);
            w.XpBonusPercent = ThreeTiers(w.XpBonusPercent, 0, 100, false);
            w.YieldBonusPoints = ThreeTiers(w.YieldBonusPoints, 0, 50, false);
            w.ExtraChancePoints = ThreeTiers(w.ExtraChancePoints, 0, 50, false);
            w.SeasonPoints = ThreeTiers(w.SeasonPoints, 0, 100, false);

            WeeklySection wk = config.Weekly;
            DayOfWeek dow;
            if (!TryDay(wk.CrownDay, out dow)) wk.CrownDay = "Sunday";
            wk.CrownHourUtc = Clamp(wk.CrownHourUtc, 0, 23);
            wk.MinWeeklyXp = Clamp(wk.MinWeeklyXp, 0, 100000000);
            wk.RewardMarks = ClampL(wk.RewardMarks, 0, 100000);
            wk.HousePoints = Clamp(wk.HousePoints, 0, 100);
            wk.TopCount = Clamp(wk.TopCount, 1, 25);

            DominionSection dm = config.Dominion;
            dm.PollSeconds = Clamp(dm.PollSeconds, 30, 86400);
            var perks = new List<HoldingPerk>();
            foreach (HoldingPerk hp in dm.Perks)
            {
                string prof = hp != null ? NormalizeProfession(hp.Profession) : null;
                if (hp == null || prof == null || string.IsNullOrEmpty(hp.Kind)) continue;
                hp.Kind = hp.Kind.Trim().ToLowerInvariant();
                hp.Profession = prof;
                hp.YieldPoints = Clamp(hp.YieldPoints, 0, 50);
                hp.ExtraChancePoints = Clamp(hp.ExtraChancePoints, 0, 50);
                perks.Add(hp);
            }
            dm.Perks = perks;

            CommissionSection cm = config.Commissions;
            cm.MaxOpenPerPlayer = Clamp(cm.MaxOpenPerPlayer, 0, 100);
            cm.MaxOpen = Clamp(cm.MaxOpen, 0, 1500);                 // RealmTreasury keeps at most 2000 holds for everyone
            cm.MaxQty = Clamp(cm.MaxQty, 1, 10000);
            cm.MaxPrice = ClampL(cm.MaxPrice, 1, 1000000);
            cm.MaxTotal = ClampL(cm.MaxTotal, 1, 1000000);
            cm.DurationHours = Clamp(cm.DurationHours, 1, 160);       // a RealmTreasury hold lasts at most 7 days
            cm.PostCooldownSeconds = Clamp(cm.PostCooldownSeconds, 0, 3600);
            cm.FeePercent = Clamp(cm.FeePercent, -1, 50);
            cm.XpPerMark = ClampD(cm.XpPerMark, 0, 100);
            cm.XpMaxPerFill = Clamp(cm.XpMaxPerFill, 0, 100000);
            cm.XpPerDay = Clamp(cm.XpPerDay, 0, 1000000);
            cm.PairCooldownHours = Clamp(cm.PairCooldownHours, 0, 8760);
            cm.MinPriceRatioForXp = ClampD(cm.MinPriceRatioForXp, 0, 10);
            cm.MaxOwedLines = Clamp(cm.MaxOwedLines, 5, 500);

            RewardSection rw = config.Rewards;
            rw.RankDeed = (rw.RankDeed ?? "").Trim();
            rw.MasterDeed = (rw.MasterDeed ?? "").Trim();
            rw.WeeklyDeed = (rw.WeeklyDeed ?? "").Trim();
            rw.CommissionDeed = (rw.CommissionDeed ?? "").Trim();

            BuildXpTable();
        }

        private List<KeywordRule> CleanRules(List<KeywordRule> rules, string what)
        {
            var o = new List<KeywordRule>();
            foreach (KeywordRule r in rules)
            {
                string prof = r != null ? NormalizeProfession(r.Profession) : null;
                if (r == null || prof == null || r.Words == null) { PrintWarning(what + " entry ignored: it needs a profession and words."); continue; }
                var words = new List<string>();
                foreach (string s in r.Words) if (!string.IsNullOrEmpty(s) && s.Trim().Length > 0) words.Add(s.Trim());
                o.Add(new KeywordRule { Profession = prof, Words = words });
            }
            return o;
        }

        private void BuildXpTable()
        {
            int max = config.Levels.MaxLevel;
            xpTable = new long[max + 2];
            for (int lv = 1; lv <= max; lv++)
                xpTable[lv] = lv == 1 ? 0 : (long)Math.Round(config.Levels.XpCurveBase * Math.Pow(lv - 1, config.Levels.XpCurveExponent));
            xpTable[max + 1] = long.MaxValue;
        }

        private static bool TryDay(string s, out DayOfWeek day)
        {
            day = DayOfWeek.Sunday;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
                if (string.Equals(d.ToString(), s.Trim(), StringComparison.OrdinalIgnoreCase)) { day = d; return true; }
            return false;
        }

        #endregion

        #region Data

        private class Prof
        {
            public double Xp;
            public int Level = 1;
            public double Today;
            public double Week;
        }

        private class Owed
        {
            public string Item;                         // the blueprint name
            public int Amount;
            public string Note;
        }

        private class CraftRec
        {
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public Dictionary<string, Prof> Profs = new Dictionary<string, Prof>();
            public string DayKey = "";
            public string WeekKey = "";
            public double WeekXp;
            public Dictionary<string, int> ProductsToday = new Dictionary<string, int>();
            public Dictionary<string, int> GatheredToday = new Dictionary<string, int>();
            public Dictionary<string, int> BonusToday = new Dictionary<string, int>();
            public Dictionary<string, double> BonusOwed = new Dictionary<string, double>();   // fractions not yet given
            public int ExtrasToday;
            public int CreaturesToday;
            public double CommissionXpToday;
            public string House;
            public DateTime HouseSince;
            public int Crowns;                          // weeks named Master Crafter
            public List<Owed> Owed = new List<Owed>();
        }

        private class HouseWs
        {
            public string Name;
            public string WeekKey = "";
            public double WeekXp;
            public double LastWeekXp;
            public Dictionary<string, double> Members = new Dictionary<string, double>();   // player id -> XP pooled this week
            public DateTime LastActive;
        }

        private class Commission
        {
            public int Id;
            public string PosterId;
            public string PosterName;
            public string Item;                         // the blueprint name
            public string Profession;                   // null when the item belongs to no profession
            public int Qty;
            public int Filled;
            public long Price;                          // marks each
            public int MinLevel;
            public string HoldId;
            public DateTime PostedAt;
            public DateTime Expires;
            public string Status = SOpen;
            public long PaidOut;
        }

        private class WeekResult
        {
            public string WeekKey;
            public string WinnerId;
            public string WinnerName;
            public int Xp;
            public string House;
            public DateTime At;
        }

        private class StoredData
        {
            public int Format = DataFormat;
            public Dictionary<string, CraftRec> Players = new Dictionary<string, CraftRec>();
            public Dictionary<string, HouseWs> Houses = new Dictionary<string, HouseWs>();
            public List<Commission> Commissions = new List<Commission>();
            public int NextCommissionId = 1;
            public DateTime NextCrowning;
            public string WeekKey = "";
            public string PrevWeekKey = "";
            public List<WeekResult> History = new List<WeekResult>();
            public Dictionary<string, DateTime> CommissionPairs = new Dictionary<string, DateTime>();
            public Dictionary<string, long> OwedMarks = new Dictionary<string, long>();   // weekly rewards the treasury could not pay yet
            public Dictionary<string, DateTime> LastPost = new Dictionary<string, DateTime>();
            public long CommissionsFilled;
            public long CommissionMarks;
            public long GatheredUnits;
            public long CraftsCounted;
            public long BonusUnitsGiven;
            public long ExtrasGiven;
        }

        // Session only.
        private class Transit
        {
            public int Units;
            public DateTime Until;
            public bool Corpse;
        }

        private class Tracker
        {
            public readonly Dictionary<object, Transit> Stacks = new Dictionary<object, Transit>();
            public readonly Dictionary<string, List<Transit>> Pool = new Dictionary<string, List<Transit>>(StringComparer.OrdinalIgnoreCase);
        }

        private class Early
        {
            public object Stack;
            public string Item;
            public int Amount;
            public bool SourceInCollection;
            public bool SourceCorpse;
            public object SourceContainer;
            public int TargetBefore = -1;
        }

        private class CraftClaim
        {
            public ulong PlayerId;
            public string Station;
            public DateTime At;
        }

        private void LoadData()
        {
            loadFailed = false;
            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            StoredData loaded;
            try
            {
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                data = null;
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json (" + ex.Message + "). RealmCrafts is paused and will NOT write it. "
                    + "Fix it or restore oxide/data/" + BackupName + ".json, then reload. Commission marks stay in the treasury and lapse back to their owners.");
                return;
            }
            if (loaded == null && existed)
            {
                data = null;
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or truncated. RealmCrafts is paused and will NOT write it. Restore "
                    + BackupName + ".json or delete the file to start empty, then reload.");
                return;
            }
            if (loaded != null && loaded.Format > DataFormat)
            {
                data = null;
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json was written by a newer RealmCrafts (format " + loaded.Format + "). Paused; nothing is written.");
                return;
            }
            data = loaded ?? new StoredData();
            NormalizeData();
            Interface.Oxide.DataFileSystem.WriteObject(BackupName, data);     // last known good copy
        }

        private void NormalizeData()
        {
            if (data.Players == null) data.Players = new Dictionary<string, CraftRec>();
            if (data.Houses == null) data.Houses = new Dictionary<string, HouseWs>();
            if (data.Commissions == null) data.Commissions = new List<Commission>();
            if (data.History == null) data.History = new List<WeekResult>();
            if (data.CommissionPairs == null) data.CommissionPairs = new Dictionary<string, DateTime>();
            if (data.OwedMarks == null) data.OwedMarks = new Dictionary<string, long>();
            if (data.LastPost == null) data.LastPost = new Dictionary<string, DateTime>();
            if (data.WeekKey == null) data.WeekKey = "";
            if (data.PrevWeekKey == null) data.PrevWeekKey = "";
            if (data.NextCommissionId < 1) data.NextCommissionId = 1;
            var players = new Dictionary<string, CraftRec>();
            foreach (KeyValuePair<string, CraftRec> kv in data.Players)
            {
                if (kv.Value == null || !IsSteamId(kv.Key)) continue;
                CraftRec r = kv.Value;
                if (r.Profs == null) r.Profs = new Dictionary<string, Prof>();
                var profs = new Dictionary<string, Prof>();
                foreach (KeyValuePair<string, Prof> p in r.Profs)
                {
                    string id = NormalizeProfession(p.Key);
                    if (id == null || p.Value == null || profs.ContainsKey(id)) continue;
                    Prof pr = p.Value;
                    if (double.IsNaN(pr.Xp) || pr.Xp < 0) pr.Xp = 0;
                    if (double.IsNaN(pr.Today) || pr.Today < 0) pr.Today = 0;
                    if (double.IsNaN(pr.Week) || pr.Week < 0) pr.Week = 0;
                    pr.Level = LevelFor(pr.Xp);
                    profs[id] = pr;
                }
                r.Profs = profs;
                if (r.ProductsToday == null) r.ProductsToday = new Dictionary<string, int>();
                if (r.GatheredToday == null) r.GatheredToday = new Dictionary<string, int>();
                if (r.BonusToday == null) r.BonusToday = new Dictionary<string, int>();
                if (r.BonusOwed == null) r.BonusOwed = new Dictionary<string, double>();
                if (r.Owed == null) r.Owed = new List<Owed>();
                r.Owed.RemoveAll(delegate(Owed o) { return o == null || string.IsNullOrEmpty(o.Item) || o.Amount <= 0; });
                if (r.DayKey == null) r.DayKey = "";
                if (r.WeekKey == null) r.WeekKey = "";
                if (double.IsNaN(r.WeekXp) || r.WeekXp < 0) r.WeekXp = 0;
                if (r.Name == null) r.Name = kv.Key;
                players[kv.Key] = r;
            }
            data.Players = players;
            var houses = new Dictionary<string, HouseWs>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, HouseWs> kv in data.Houses)
            {
                if (kv.Value == null || string.IsNullOrEmpty(kv.Key)) continue;
                HouseWs h = kv.Value;
                if (h.Members == null) h.Members = new Dictionary<string, double>();
                if (h.WeekKey == null) h.WeekKey = "";
                if (h.Name == null) h.Name = kv.Key;
                houses[kv.Key.ToLowerInvariant()] = h;
            }
            data.Houses = houses;
            data.Commissions.RemoveAll(delegate(Commission c) { return c == null || c.Id <= 0 || !IsSteamId(c.PosterId) || string.IsNullOrEmpty(c.Item); });
            foreach (Commission c in data.Commissions)
            {
                if (c.Status != SOpen && c.Status != SDone && c.Status != SCancelled && c.Status != SExpired) c.Status = SExpired;
                if (c.Filled < 0) c.Filled = 0;
                if (c.Filled > c.Qty) c.Filled = c.Qty;
                if (c.Id >= data.NextCommissionId) data.NextCommissionId = c.Id + 1;
            }
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
                { "Speaker", "Guilds" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Paused", "The guilds are closed: oxide/data/RealmCrafts.json could not be read. An admin must fix or restore it, then reload." },
                { "Closed", "The guilds of the realm are closed for now." },
                { "PartClosed", "That part of the guilds is closed on this server." },
                { "NoPermission", "You may not do that." },
                { "PlayerNotFound", "No such person is known (or the name is ambiguous)." },
                { "UnknownProfession", "There is no profession '{0}'. Professions: {1}." },

                { "Prof.woodcutting", "Woodcutting" },
                { "Prof.mining", "Mining" },
                { "Prof.foraging", "Foraging" },
                { "Prof.hunting", "Hunting" },
                { "Prof.smithing", "Smithing" },
                { "Prof.carpentry", "Carpentry" },
                { "Prof.tailoring", "Tailoring" },
                { "Prof.cooking", "Cooking" },
                { "Master.woodcutting", "Master Woodcutter" },
                { "Master.mining", "Master Miner" },
                { "Master.foraging", "Master Forager" },
                { "Master.hunting", "Master Huntsman" },
                { "Master.smithing", "Master Smith" },
                { "Master.carpentry", "Master Carpenter" },
                { "Master.tailoring", "Master Tailor" },
                { "Master.cooking", "Master Cook" },
                { "Rank.0", "Novice" },
                { "Rank.1", "Apprentice" },
                { "Rank.2", "Journeyman" },
                { "Rank.3", "Expert" },
                { "Rank.4", "Artisan" },
                { "Rank.5", "Master" },
                { "Tier.0", "no workshop" },
                { "Tier.1", "a Workshop" },
                { "Tier.2", "a Guildhall" },
                { "Tier.3", "a Great Guildhall" },
                { "Earn.woodcutting", "felling trees and gathering wood" },
                { "Earn.mining", "breaking stone, ore and clay" },
                { "Earn.foraging", "picking flax, berries, crops and herbs" },
                { "Earn.hunting", "slaying beasts and taking their hides, meat and bone" },
                { "Earn.smithing", "forging iron and steel: tools, arms and armour" },
                { "Earn.carpentry", "working wood: furniture, bows, arrows, doors and shields" },
                { "Earn.tailoring", "leather, cloth, rope, bandages and banners" },
                { "Earn.cooking", "cooking, baking and milling" },

                // /craft
                { "Help1", "Professions: [F4C96D]/craft[FFFFFF] your ranks | [F4C96D]/craft[FFFFFF] <profession> | [F4C96D]/craft perks[FFFFFF] | [F4C96D]/craft top[FFFFFF] [week|houses|<profession>] | [F4C96D]/craft house[FFFFFF]" },
                { "Help2", "  Commissions: [F4C96D]/craft orders[FFFFFF] [mine|<word>] | [F4C96D]/craft order[FFFFFF] <item> <qty> <marks each> [min level] | [F4C96D]/craft fill[FFFFFF] <id> [qty]" },
                { "Help3", "  [F4C96D]/craft cancel[FFFFFF] <id> | [F4C96D]/craft collect[FFFFFF] (goods waiting for you). XP comes from gathering, hunting and crafting." },
                { "HelpAdmin", "  Admin: [F4C96D]/craft admin[FFFFFF] status | watch <player> [off] | unmapped | items <word> | xp | level | reset | crown | cancel <id>" },
                { "MeHeader", "Your professions ({0} XP this week):" },
                { "MeLine", "  {0}: {1}, level {2} - {3}; today {4} of {5}" },
                { "MeNext", "{0} XP to level {1}" },
                { "MeMax", "mastered" },
                { "MeHouse", "  House {0} keeps {1} this week ({2} XP pooled): +{3}% XP for its members." },
                { "MeHouseNone", "  House {0} has no workshop yet this week ({1} of {2} XP pooled)." },
                { "MeOwed", "  Goods are waiting for you: [F4C96D]/craft collect[FFFFFF]" },
                { "MeHint", "  [F4C96D]/craft[FFFFFF] <profession> for more, [F4C96D]/craft perks[FFFFFF] for what each rank brings." },
                { "ProfHeader", "{0}: {1}, level {2} of {3} ({4} XP)." },
                { "ProfEarn", "  XP from {0}." },
                { "ProfNext", "  {0} XP to level {1}; {2} at level {3}." },
                { "ProfMaxed", "  You are a {0}. Nothing higher can be learned." },
                { "ProfToday", "  Today {0} of {1} XP; this week {2}." },
                { "ProfYield", "  Your bonus yield: +{0}% of every good you gather." },
                { "ProfExtra", "  Your chance of an extra item: {0}% per craft ({1} of {2} today)." },
                { "ProfDiscount", "  The market takes {0}% less fee when you sell goods of this craft." },
                { "ProfDiscountNext", "  At level {0} the market takes {1}% less fee on goods of this craft." },
                { "PerksHeader", "What the guilds teach (bonus yield for gatherers, extra items for crafters):" },
                { "PerksLine", "  {0} (level {1}): +{2}% yield or {3}% extra-item chance{4}" },
                { "PerksFee", "; {0}% off the market fee" },
                { "PerksHouse", "  A house workshop adds: {0}." },
                { "PerksHouseTier", "{0} +{1}% XP, +{2} yield, +{3} extra" },
                { "PerksDominion", "  Holdings (RealmDominion) add: {0}." },
                { "PerksDominionLine", "{0}: {1} +{2}" },
                { "TopHeader", "The week's finest crafters (named at the crowning {0}):" },
                { "TopLine", "  {0}. {1} - {2} XP{3}" },
                { "TopYou", "  You: {0} XP this week, place {1}." },
                { "TopNone", "  No one has earned XP this week yet." },
                { "TopProfHeader", "The realm's best at {0}:" },
                { "TopProfLine", "  {0}. {1} - {2}, level {3}" },
                { "TopHousesHeader", "House workshops this week:" },
                { "TopHousesLine", "  {0}. {1} - {2} XP, {3}" },
                { "TopLast", "  Last Master Crafter: {0} ({1} XP)." },
                { "HouseNone", "You are in no house. A house pools its members' XP into a workshop: [F4C96D]/house[FFFFFF]" },
                { "HouseHeader", "House {0}'s workshop: {1}." },
                { "HouseWeek", "  This week {0} XP pooled (last week {1}). Next tier at {2} XP." },
                { "HouseTop", "  {0} XP pooled at most per member each week; members count after {1} h in the house." },
                { "HousePerks", "  Members now get +{0}% XP, +{1} yield points and +{2} extra-item points." },
                { "HouseMember", "  {0}: {1} XP" },
                { "HouseYou", "  You count from {0}." },

                // XP and ranks
                { "LevelUp", "{0} rises to level {1}." },
                { "RankUp", "You are now {0} in {1}. [F4C96D]/craft perks[FFFFFF]" },
                { "RankHerald", "{0} is now {1} in {2}." },
                { "MasterHerald", "{0} is named {1}, a master of the guild of {2}." },
                { "MasterYou", "You are named {0}: a master of {1}. The guild will remember it." },
                { "MasterPopupTitle", "Master of {0}" },
                { "MasterPopupBody", "You are named {0}.\nNo one in Ostreval knows the craft better.\nThe market takes a smaller fee on your goods." },
                { "DailyCap", "You have learned all you can in {0} today. Come back tomorrow." },
                { "BonusGiven", "Your skill finds you {0} more." },
                { "ExtraGiven", "A master's touch: one more {0} ({1})." },
                { "WatchLine", "  watch {0}: {1}" },

                // Weekly
                { "CrownHerald", "{0} is named Master Crafter of the week with {1} XP. The guilds salute them." },
                { "CrownHouse", "  House {0} earns {1} season point(s) for its crafter." },
                { "CrownYou", "You are the Master Crafter of the week. {0} marks are yours." },
                { "CrownYouOwed", "You are the Master Crafter of the week. The treasury owes you {0} marks; they come when it can pay." },
                { "CrownNone", "No crafter earned enough this week to be named Master Crafter." },
                { "CrownPopupTitle", "Master Crafter" },
                { "CrownPopupBody", "The guilds name you Master Crafter of the week.\n{0} XP earned." },
                { "WorkshopHerald", "The workshops close their week: {0}." },
                { "WorkshopLine", "{0} ({1}, +{2})" },
                { "MarksPaid", "The treasury pays you {0} marks it owed you." },

                // Commissions
                { "OrdersHeader", "Open commissions ({0}):" },
                { "OrdersLine", "  #{0} {1} x{2} at {3} each - {4}{5} - {6} left" },
                { "OrdersMineHeader", "Your commissions:" },
                { "OrdersMineLine", "  #{0} {1}: {2} of {3} filled at {4} each - {5}" },
                { "OrdersNone", "No commission is open. Post one: [F4C96D]/craft order[FFFFFF] <item> <qty> <marks each>" },
                { "OrdersMineNone", "You have no commissions." },
                { "OrdersMore", "  ...and {0} more. [F4C96D]/craft orders[FFFFFF] <word> narrows the list." },
                { "MinLevelNote", ", level {0}+" },
                { "Status.open", "open, {0} left" },
                { "Status.done", "filled" },
                { "Status.cancelled", "cancelled" },
                { "Status.expired", "lapsed" },
                { "OrderUsage", "Usage: [F4C96D]/craft order[FFFFFF] <item> <qty> <marks each> [min level], e.g. [F4C96D]/craft order[FFFFFF] Iron Sword 2 150" },
                { "OrderUnknownItem", "There is no item '{0}'." },
                { "OrderBadQty", "The quantity must be 1 to {0}." },
                { "OrderBadPrice", "The price must be 1 to {0} marks each." },
                { "OrderBadTotal", "A commission may hold at most {0} marks in all." },
                { "OrderBadLevel", "The level asked must be 0 to {0}." },
                { "OrderTooMany", "You have {0} commissions open already; cancel or wait for one." },
                { "OrderBoardFull", "The commission board is full. Try again later." },
                { "OrderCooldown", "You posted a commission just now. Try again {0}." },
                { "OrderNoTreasury", "The treasury is closed, so no commission can be held. Try again later." },
                { "OrderShort", "A commission of {0} marks must be paid in now; your purse holds {1}. [F4C96D]/purse[FFFFFF]" },
                { "OrderPosted", "Commission #{0} posted: {1} x{2} at {3} marks each. {4} marks are held by the treasury until it is filled." },
                { "OrderPostedProf", "  It is work for {0}{1}. It lapses in {2} h if no one fills it." },
                { "OrderHerald", "{0} seeks {1} x{2} at {3} marks each: [F4C96D]/craft fill[FFFFFF] {4}" },
                { "FillUsage", "Usage: [F4C96D]/craft fill[FFFFFF] <id> [qty]" },
                { "FillUnknown", "There is no open commission #{0}." },
                { "FillOwn", "You cannot fill your own commission." },
                { "FillPosterFull", "{0} has too many goods waiting already; this commission cannot be filled until they collect." },
                { "FillLevel", "Commission #{0} asks for {1} level {2}; you are level {3}." },
                { "FillNone", "You carry no {0}." },
                { "FillTakeFailed", "The goods could not be taken from your packs. Nothing was paid." },
                { "FillHoldGone", "The marks for commission #{0} are no longer held (it lapsed). Your goods are returned." },
                { "Filled", "You fill commission #{0}: {1} x{2}. {3} marks are yours (fee {4})." },
                { "FilledXp", "  {0} +{1} XP." },
                { "FilledNoXp", "  No XP for this one: {0}." },
                { "NoXp.address", "you share a hearth with the poster" },
                { "NoXp.pair", "you filled for this poster lately" },
                { "NoXp.price", "the price is far below the market's" },
                { "NoXp.house", "the poster is of your own house" },
                { "NoXp.cap", "you have earned all the commission XP you can today" },
                { "NoXp.none", "the item belongs to no profession" },
                { "PosterNotice", "{0} filled your commission #{1}: {2} x{3}. {4}" },
                { "PosterGiven", "The goods are in your packs." },
                { "PosterOwed", "The goods wait for you: [F4C96D]/craft collect[FFFFFF]" },
                { "PosterDone", "  Commission #{0} is complete." },
                { "CancelUsage", "Usage: [F4C96D]/craft cancel[FFFFFF] <id>" },
                { "CancelNotYours", "Commission #{0} is not yours." },
                { "Cancelled", "Commission #{0} is cancelled. {1} marks return to your purse." },
                { "Lapsed", "Your commission #{0} for {1} lapsed. {2} marks return to your purse." },
                { "CollectNone", "Nothing is waiting for you." },
                { "Collected", "You collect: {0}." },
                { "CollectFull", "Your packs are full. Make room and try again." },
                { "OwedOnJoin", "Goods are waiting for you: [F4C96D]/craft collect[FFFFFF]" },
                { "Pending", "  More is still waiting: [F4C96D]/craft collect[FFFFFF]" },

                // Admin
                { "AdminUsage", "Usage: {0}" },
                { "StatusHeader", "Guilds: {0} crafter(s) on record, {1} open commission(s), {2} house workshop(s)." },
                { "StatusCounts", "  Gathered units counted {0}, crafts counted {1}, bonus units given {2}, extra items {3}, commissions filled {4} ({5} marks)." },
                { "StatusItems", "  Gathering items resolved: {0} of {1}. Unresolved: {2}." },
                { "StatusCrown", "  Next crowning {0}. Treasury: {1}. Renown: {2}. Houses: {3}. Seasons: {4}. Dominion: {5}." },
                { "StatusSubs", "  Container events subscribed: {0}." },
                { "WatchOn", "Watching {0}'s gathering and crafting here. [F4C96D]/craft admin[FFFFFF] watch {0} off ends it." },
                { "WatchOff", "No longer watching {0}." },
                { "UnmappedHeader", "Products and stations that matched no profession (latest {0}):" },
                { "UnmappedLine", "  {0}" },
                { "UnmappedNone", "Every product made since the last load matched a profession." },
                { "ItemsHeader", "Item names holding '{0}' ({1}):" },
                { "ItemsLine", "  {0}" },
                { "ItemsNone", "No item name holds '{0}'." },
                { "ItemsNotReady", "The game's item list is not loaded yet. Try again once the world is up." },
                { "AdminXp", "{0}'s {1} is now {2} XP (level {3})." },
                { "AdminResetAsk", "This wipes every profession of {0}. Type [F4C96D]/craft admin[FFFFFF] reset {0} confirm within 60 s." },
                { "AdminReset", "{0}'s professions are wiped." },
                { "AdminCrowned", "The week is closed now." },
                { "AdminCancelled", "Commission #{0} is cancelled; {1} marks went back to {2}." },
                { "Yes", "yes" },
                { "No", "no" },
                { "None", "none" },
                { "Loaded", "loaded" },
                { "Absent", "absent" },
                { "Now", "now" },
                { "In", "in {0}" },
                { "PopupOk", "Onward" }
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
            "LevelUp", "RankUp", "MasterYou", "BonusGiven", "ExtraGiven", "CrownYou", "MarksPaid", "OrderPosted", "Filled",
            "PosterNotice", "Cancelled", "Collected", "AdminXp", "AdminReset", "AdminCrowned", "AdminCancelled", "WatchOn"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "DailyCap", "CrownYouOwed", "Lapsed", "OwedOnJoin", "AdminResetAsk", "FilledNoXp", "WatchOff"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
        }

        private bool HeraldBudget()
        {
            DateTime now = Now();
            heralds.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
            if (heralds.Count >= config.Levels.HeraldsPerHour) return false;
            heralds.Add(now);
            return true;
        }

        private string ProfName(string prof, Player player)
        {
            return Msg("Prof." + prof, player);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintError("oxide/config/RealmCrafts.json could not be read (" + ex.Message + "); using the defaults for this run.");
                config = null;
            }
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (loadFailed || data == null) return;
            // Re-sent on hot load (docs/oxide-rok-api.md 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.General.TickSeconds, SafeTick);
            SubscribeGame();
            ResolveGatherItems();
            if (data.NextCrowning.Year < 2000 || string.IsNullOrEmpty(data.WeekKey))
            {
                data.NextCrowning = NextCrowning(Now());
                data.WeekKey = KeyOf(data.NextCrowning);
                dirty = true;
            }
            foreach (Player p in OnlinePlayers()) { Seen(p); NoteAddress(p); }
            SafeTick();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            popupsClosed = true;
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            UnsubscribeGame();
            trackers.Clear();
            early.Clear();
            craftClaims.Clear();
            SaveData();
        }

        private void SubscribeGame()
        {
            UnsubscribeGame();
            try
            {
                addEarlySub = new EventSubscriber<ContainerItemAddEvent>(OnAddEarly);
                addLateSub = new EventSubscriber<ContainerItemAddEvent>(OnAddLate);
                mergeEarlySub = new EventSubscriber<ContainerItemMergeEvent>(OnMergeEarly);
                mergeLateSub = new EventSubscriber<ContainerItemMergeEvent>(OnMergeLate);
                removeSub = new EventSubscriber<ContainerItemRemoveEvent>(OnRemoveLate);
                splitSub = new EventSubscriber<ContainerItemSplitEvent>(OnSplitLate);
                passSub = new EventSubscriber<ItemPassEvent>(OnPassLate);
                craftSub = new EventSubscriber<ItemCrafterCraftEvent>(OnCraftRequested);
                craftItemSub = new EventSubscriber<ItemCrafterItemEvent>(OnCraftProduct);
                // VeryEarly reads a merge's source before the game's own listener (Early) moves it; VeryLate counts only
                // what the game let through (not cancelled).
                EventManager.Subscribe<ContainerItemAddEvent>(addEarlySub, EventHandlerOrder.VeryEarly);
                EventManager.Subscribe<ContainerItemAddEvent>(addLateSub, EventHandlerOrder.VeryLate);
                EventManager.Subscribe<ContainerItemMergeEvent>(mergeEarlySub, EventHandlerOrder.VeryEarly);
                EventManager.Subscribe<ContainerItemMergeEvent>(mergeLateSub, EventHandlerOrder.VeryLate);
                EventManager.Subscribe<ContainerItemRemoveEvent>(removeSub, EventHandlerOrder.VeryLate);
                EventManager.Subscribe<ContainerItemSplitEvent>(splitSub, EventHandlerOrder.VeryLate);
                EventManager.Subscribe<ItemPassEvent>(passSub, EventHandlerOrder.VeryLate);
                EventManager.Subscribe<ItemCrafterCraftEvent>(craftSub, EventHandlerOrder.VeryLate);
                EventManager.Subscribe<ItemCrafterItemEvent>(craftItemSub, EventHandlerOrder.VeryLate);
                subscribed = true;
            }
            catch (Exception ex)
            {
                subscribed = false;
                PrintError("Could not subscribe to the game's container and crafting events (" + ex.Message + "): gathering and crafting XP are off.");
            }
        }

        private bool subscribed;

        private void UnsubscribeGame()
        {
            try
            {
                if (addEarlySub != null) EventManager.Unsubscribe<ContainerItemAddEvent>(addEarlySub);
                if (addLateSub != null) EventManager.Unsubscribe<ContainerItemAddEvent>(addLateSub);
                if (mergeEarlySub != null) EventManager.Unsubscribe<ContainerItemMergeEvent>(mergeEarlySub);
                if (mergeLateSub != null) EventManager.Unsubscribe<ContainerItemMergeEvent>(mergeLateSub);
                if (removeSub != null) EventManager.Unsubscribe<ContainerItemRemoveEvent>(removeSub);
                if (splitSub != null) EventManager.Unsubscribe<ContainerItemSplitEvent>(splitSub);
                if (passSub != null) EventManager.Unsubscribe<ItemPassEvent>(passSub);
                if (craftSub != null) EventManager.Unsubscribe<ItemCrafterCraftEvent>(craftSub);
                if (craftItemSub != null) EventManager.Unsubscribe<ItemCrafterItemEvent>(craftItemSub);
            }
            catch (Exception ex) { PrintWarning("Unsubscribe failed: " + ex.Message); }
            addEarlySub = addLateSub = null;
            mergeEarlySub = mergeLateSub = null;
            removeSub = null;
            splitSub = null;
            passSub = null;
            craftSub = null;
            craftItemSub = null;
            subscribed = false;
        }

        #endregion

        #region Players and houses

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            try
            {
                NoteAddress(player);
                CraftRec rec = Seen(player);
                TrackHouse(player.Id.ToString(), rec);
                if (rec.Owed.Count > 0) { ulong id = player.Id; timer.Once(15f, delegate { OwedOnJoin(id); }); }
                PayOwedMarks(player.Id.ToString());
            }
            catch (Exception ex) { PrintError("OnPlayerConnected: " + ex.Message); }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null) return;
            trackers.Remove(player.Id);
            if (loadFailed || data == null || player.IsServer) return;
            CraftRec rec;
            if (data.Players.TryGetValue(player.Id.ToString(), out rec)) { rec.LastSeen = Now(); dirty = true; }
        }

        private void NoteAddress(Player player)
        {
            try
            {
                string ip = player.Connection != null ? player.Connection.IpAddress : null;   // [ASM Connection.IpAddress]
                if (!string.IsNullOrEmpty(ip)) addressOf[player.Id] = Hash(salt + "|" + ip);
            }
            catch (Exception) { }
        }

        // Addresses are kept only as a salted hash, in memory, for the one-hearth rules.
        private readonly string salt = Guid.NewGuid().ToString("N");

        private CraftRec Seen(Player p)
        {
            CraftRec rec = Rec(p.Id.ToString(), p.Name, true);
            rec.LastSeen = Now();
            dirty = true;
            return rec;
        }

        private CraftRec Rec(string id, string name, bool create)
        {
            CraftRec r;
            if (data == null || id == null) return null;
            if (data.Players.TryGetValue(id, out r))
            {
                string clean = Clean(name, 40);
                if (clean.Length > 0 && r.Name != clean) { r.Name = clean; dirty = true; }
                return r;
            }
            if (!create || !IsSteamId(id)) return null;
            if (data.Players.Count >= config.General.MaxPlayersKept) Prune();
            r = new CraftRec { Name = Clean(name, 40), FirstSeen = Now(), LastSeen = Now() };
            if (r.Name.Length == 0) r.Name = id;
            data.Players[id] = r;
            dirty = true;
            return r;
        }

        private void Prune()
        {
            var list = new List<KeyValuePair<string, CraftRec>>(data.Players);
            list.Sort(delegate(KeyValuePair<string, CraftRec> a, KeyValuePair<string, CraftRec> b)
            {
                double xa = TotalXp(a.Value), xb = TotalXp(b.Value);
                bool ea = xa <= 0 && a.Value.Owed.Count == 0, eb = xb <= 0 && b.Value.Owed.Count == 0;
                if (ea != eb) return ea ? -1 : 1;
                return a.Value.LastSeen.CompareTo(b.Value.LastSeen);
            });
            int remove = data.Players.Count - config.General.MaxPlayersKept + 1;
            for (int i = 0; i < remove && i < list.Count; i++)
            {
                if (list[i].Value.Owed.Count > 0) continue;                 // never drop goods someone is owed
                data.Players.Remove(list[i].Key);
            }
        }

        private static double TotalXp(CraftRec r)
        {
            double t = 0;
            foreach (Prof p in r.Profs.Values) t += p.Xp;
            return t;
        }

        private Prof ProfOf(CraftRec r, string prof)
        {
            Prof p;
            if (!r.Profs.TryGetValue(prof, out p)) { p = new Prof(); r.Profs[prof] = p; }
            return p;
        }

        // A realm day and a week end at the crowning: counters roll over lazily the first time a record is touched.
        private void Roll(CraftRec r)
        {
            string day = DayKey(Now());
            if (r.DayKey != day)
            {
                r.DayKey = day;
                foreach (Prof p in r.Profs.Values) p.Today = 0;
                r.ProductsToday.Clear();
                r.GatheredToday.Clear();
                r.BonusToday.Clear();
                r.ExtrasToday = 0;
                r.CreaturesToday = 0;
                r.CommissionXpToday = 0;
                dirty = true;
            }
            if (r.WeekKey != data.WeekKey)
            {
                r.WeekKey = data.WeekKey;
                r.WeekXp = 0;
                foreach (Prof p in r.Profs.Values) p.Week = 0;
                dirty = true;
            }
        }

        // Which house a player is in (RealmHouses), and since when. Leaving and joining again starts the clock again.
        private void TrackHouse(string id, CraftRec rec)
        {
            if (rec == null || RealmHouses == null) return;
            string h = AskString(RealmHouses, "GetHouse", id);
            if (h != null && h.Trim().Length == 0) h = null;
            if (!SameName(h, rec.House))
            {
                rec.House = h;
                rec.HouseSince = Now();
                dirty = true;
            }
        }

        private HouseWs House(string house, bool create)
        {
            if (string.IsNullOrEmpty(house)) return null;
            HouseWs h;
            string key = house.Trim().ToLowerInvariant();
            if (!data.Houses.TryGetValue(key, out h))
            {
                if (!create) return null;
                h = new HouseWs { Name = house.Trim(), WeekKey = data.WeekKey };
                data.Houses[key] = h;
                dirty = true;
            }
            RollHouse(h);
            return h;
        }

        private void RollHouse(HouseWs h)
        {
            if (h.WeekKey == data.WeekKey) return;
            h.LastWeekXp = h.WeekKey == data.PrevWeekKey ? h.WeekXp : 0;
            h.WeekXp = 0;
            h.Members.Clear();
            h.WeekKey = data.WeekKey;
            dirty = true;
        }

        // The workshop's tier: the better of this week's and last week's pooled XP. 0 = none.
        private int Tier(HouseWs h)
        {
            if (h == null || !config.Workshops.Enabled) return 0;
            double xp = Math.Max(h.WeekXp, h.LastWeekXp);
            int t = 0;
            for (int i = 0; i < 3; i++) if (xp >= config.Workshops.TierXp[i]) t = i + 1;
            return t;
        }

        private int TierOf(CraftRec rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.House)) return 0;
            return Tier(House(rec.House, false));
        }

        // A member's XP into the house workshop, within the rules against alt farms (see the header).
        private void Pool(string id, CraftRec rec, double xp)
        {
            if (!config.Workshops.Enabled || xp <= 0 || string.IsNullOrEmpty(rec.House)) return;
            if ((Now() - rec.HouseSince).TotalHours < config.Workshops.MinMemberHours) return;
            HouseWs h = House(rec.House, true);
            if (h == null) return;
            double have;
            h.Members.TryGetValue(id, out have);
            double room = config.Workshops.MemberCapXpPerWeek - have;
            if (room <= 0) return;
            if (!AddressMayPool(h, id)) return;
            double add = Math.Min(xp, room);
            h.Members[id] = have + add;
            h.WeekXp += add;
            h.LastActive = Now();
            dirty = true;
        }

        private bool AddressMayPool(HouseWs h, string id)
        {
            if (config.Workshops.AccountsPerAddress <= 0) return true;
            ulong u;
            string addr;
            if (!ulong.TryParse(id, out u) || !addressOf.TryGetValue(u, out addr)) return true;
            if (weekAddressesKey != data.WeekKey) { weekAddresses.Clear(); weekAddressesKey = data.WeekKey; }
            Dictionary<string, List<string>> byAddr;
            string hk = h.Name.ToLowerInvariant();
            if (!weekAddresses.TryGetValue(hk, out byAddr)) { byAddr = new Dictionary<string, List<string>>(); weekAddresses[hk] = byAddr; }
            List<string> ids;
            if (!byAddr.TryGetValue(addr, out ids)) { ids = new List<string>(); byAddr[addr] = ids; }
            if (ids.Contains(id)) return true;
            if (ids.Count >= config.Workshops.AccountsPerAddress) return false;
            ids.Add(id);
            return true;
        }

        #endregion

        #region Tick

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            if (loadFailed || data == null) return;
            DateTime now = Now();
            early.Clear();                                   // a VeryEarly look whose VeryLate never came (another plugin threw)
            PruneTransit(now);
            if (config.General.Enabled)
            {
                if (now >= nextHouseCheck)
                {
                    nextHouseCheck = now.AddMinutes(2);
                    foreach (Player p in OnlinePlayers()) TrackHouse(p.Id.ToString(), Rec(p.Id.ToString(), p.Name, true));
                }
                if (config.Dominion.Enabled && now >= nextDominion) { nextDominion = now.AddSeconds(config.Dominion.PollSeconds); PollDominion(); }
                if (now >= nextBonus) { nextBonus = now.AddSeconds(config.Perks.BonusEverySeconds); GiveBonuses(); }
                if (config.Weekly.Enabled && now >= data.NextCrowning) Crown();
                else if (!config.Weekly.Enabled && now >= data.NextCrowning) CloseWeek();
                ExpireCommissions(now);
                foreach (string id in new List<string>(data.OwedMarks.Keys)) if (OnlineById(id) != null) PayOwedMarks(id);
            }
            if (dirty && now >= nextSave) SaveData();
        }

        private void PruneTransit(DateTime now)
        {
            foreach (Tracker t in trackers.Values)
            {
                var gone = new List<object>();
                foreach (KeyValuePair<object, Transit> kv in t.Stacks) if (kv.Value.Until <= now || kv.Value.Units <= 0) gone.Add(kv.Key);
                foreach (object k in gone) t.Stacks.Remove(k);
                foreach (List<Transit> l in t.Pool.Values) l.RemoveAll(delegate(Transit x) { return x.Until <= now || x.Units <= 0; });
            }
            if (craftClaims.Count > 0)
            {
                var old = new List<object>();
                foreach (KeyValuePair<object, CraftClaim> kv in craftClaims) if ((now - kv.Value.At).TotalMinutes > config.Crafting.ClaimMinutes) old.Add(kv.Key);
                foreach (object k in old) craftClaims.Remove(k);
            }
            if (corpseTaken.Count > 2000) corpseTaken.Clear();
            if (data.CommissionPairs.Count > 5000 || !Throttled("pairs", 3600))
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, DateTime> kv in data.CommissionPairs) if ((now - kv.Value).TotalHours >= Math.Max(1, config.Commissions.PairCooldownHours)) stale.Add(kv.Key);
                foreach (string k in stale) data.CommissionPairs.Remove(k);
            }
        }

        #endregion

        #region Gathering

        private bool Counting()
        {
            return !loadFailed && data != null && config.General.Enabled && !giving;
        }

        // The player whose own client sent this change, or null for anything the server did itself.
        private static Player ClientOf(NetworkEvent e)
        {
            Player s = e.Sender;
            if (s == null || s.IsServer || e.IsSender) return null;
            return s;
        }

        private static bool OwnContainer(EntityEvent e, Player p)
        {
            return e.Entity != null && p.Entity != null && ReferenceEquals(e.Entity, p.Entity);
        }

        private Tracker Track(ulong id)
        {
            Tracker t;
            if (!trackers.TryGetValue(id, out t)) { t = new Tracker(); trackers[id] = t; }
            return t;
        }

        private void OnAddEarly(ContainerItemAddEvent e)
        {
            if (!Counting() || e == null) return;
            try
            {
                Player p = ClientOf(e);
                if (p == null || !OwnContainer(e, p)) return;
                InvGameItemStack s = e.ItemStack;
                if (s == null || s.Blueprint == null) return;
                if (early.Count > 500) early.Clear();
                early[e] = new Early { Stack = s, Item = s.Blueprint.Name, Amount = s.StackAmount };
            }
            catch (Exception ex) { if (!Throttled("add", 60)) PrintWarning("Container watch failed: " + ex.Message); }
        }

        private void OnAddLate(ContainerItemAddEvent e)
        {
            if (e == null) return;
            Early x;
            if (!early.TryGetValue(e, out x)) return;
            early.Remove(e);
            if (!Counting() || e.Cancelled) return;
            try
            {
                Player p = ClientOf(e);
                if (p != null) Incoming(p, x.Item, x.Amount, x.Stack);
            }
            catch (Exception ex) { if (!Throttled("add", 60)) PrintWarning("Container watch failed: " + ex.Message); }
        }

        private void OnMergeEarly(ContainerItemMergeEvent e)
        {
            if (!Counting() || e == null) return;
            try
            {
                Player p = ClientOf(e);
                if (p == null || !OwnContainer(e, p)) return;
                InvGameItemStack s = e.ItemStack;
                if (s == null || s.Blueprint == null) return;
                var x = new Early { Stack = s, Item = s.Blueprint.Name, Amount = e.Quantity };
                if (s.CollectionInterface != null)
                {
                    x.SourceInCollection = true;
                    ItemCollection col = s.CollectionInterface as ItemCollection;
                    Container from = col != null ? col.Container : null;
                    x.SourceCorpse = from is LootableCreatureContainer;
                    x.SourceContainer = from;
                }
                InvGameItemStack target = e.TargetStack;
                if (target != null) x.TargetBefore = target.StackAmount;
                if (early.Count > 500) early.Clear();
                early[e] = x;
            }
            catch (Exception ex) { if (!Throttled("merge", 60)) PrintWarning("Container watch failed: " + ex.Message); }
        }

        private void OnMergeLate(ContainerItemMergeEvent e)
        {
            if (e == null) return;
            Early x;
            if (!early.TryGetValue(e, out x)) return;
            early.Remove(e);
            if (!Counting() || e.Cancelled) return;
            try
            {
                Player p = ClientOf(e);
                if (p == null) return;
                int amount = x.Amount;
                InvGameItemStack target = e.TargetStack;
                if (target != null && x.TargetBefore >= 0)
                {
                    int moved = target.StackAmount - x.TargetBefore;          // what really went in (a full stack takes less)
                    if (moved >= 0 && moved <= amount) amount = moved;
                }
                if (amount <= 0) return;
                if (x.SourceInCollection)
                {
                    if (x.SourceCorpse) { int c = CorpseAllowance(x.SourceContainer, amount); if (c > 0) Credit(p, x.Item, c, "corpse"); else Watch(p, amount + " " + x.Item + " from a corpse (its share is spent)"); }
                    else Watch(p, amount + " " + x.Item + " moved from a container");
                    return;
                }
                Incoming(p, x.Item, amount, x.Stack);
            }
            catch (Exception ex) { if (!Throttled("merge", 60)) PrintWarning("Container watch failed: " + ex.Message); }
        }

        // Goods a client took out of a container (its own included): they are on the move, not new.
        private void OnRemoveLate(ContainerItemRemoveEvent e)
        {
            if (!Counting() || e == null || e.Cancelled) return;
            try
            {
                Player p = ClientOf(e);
                if (p == null) return;
                InvGameItemStack s = e.ItemStack;
                if (s == null || s.Blueprint == null) return;
                Container c = e.Container;
                bool corpse = c is LootableCreatureContainer;
                AddTransit(p.Id, s, s.Blueprint.Name, s.StackAmount, corpse ? (object)c : null, config.Gathering.TransitSeconds);
            }
            catch (Exception ex) { if (!Throttled("remove", 60)) PrintWarning("Container watch failed: " + ex.Message); }
        }

        private void OnSplitLate(ContainerItemSplitEvent e)
        {
            if (!Counting() || e == null || e.Cancelled) return;
            try
            {
                Player p = ClientOf(e);
                if (p == null) return;
                InvGameItemStack s = e.ItemStack;
                if (s == null || s.Blueprint == null || e.Quantity <= 0) return;
                Container c = e.Container;
                bool corpse = c is LootableCreatureContainer;
                AddTransit(p.Id, e.ResultStack, s.Blueprint.Name, e.Quantity, corpse ? (object)c : null, config.Gathering.TransitSeconds);
            }
            catch (Exception ex) { if (!Throttled("split", 60)) PrintWarning("Container watch failed: " + ex.Message); }
        }

        // The server hands a stack to a player (salvage, a broken container, a block picked up, a passed item): when the
        // client puts it away, it is not gathered. A farm's crops are the one exception (LootCountsAsGather).
        private void OnPassLate(ItemPassEvent e)
        {
            if (!Counting() || e == null || e.Cancelled) return;
            try
            {
                Player r = e.Recipient;
                if (r == null || r.IsServer) return;
                InvGameItemStack s = e.ItemStack;
                if (s == null || s.Blueprint == null || s.StackAmount <= 0) return;
                string item = s.Blueprint.Name;
                if (e.Memo == "Loot" && LootCounts(item)) { Watch(r, s.StackAmount + " " + item + " harvested (counts when it lands)"); return; }
                AddTransit(r.Id, s, item, s.StackAmount, null, config.Gathering.PassSeconds);
                Watch(r, s.StackAmount + " " + item + " handed over by the game (" + Clean(e.Memo, 20) + ")");
            }
            catch (Exception ex) { if (!Throttled("pass", 60)) PrintWarning("Item pass watch failed: " + ex.Message); }
        }

        private bool LootCounts(string item)
        {
            foreach (string s in config.Gathering.LootCountsAsGather)
            {
                if (SameName(s, item)) return true;
                InvItemBlueprint bp = Blueprint(s);
                if (bp != null && SameName(bp.Name, item)) return true;
            }
            return false;
        }

        private void AddTransit(ulong id, object stack, string item, int units, object corpse, int seconds)
        {
            if (units <= 0 || string.IsNullOrEmpty(item)) return;
            Tracker t = Track(id);
            DateTime until = Now().AddSeconds(seconds);
            bool isCorpse = false;
            if (corpse != null)
            {
                int allowed = CorpseAllowance(corpse, units);
                isCorpse = allowed > 0;
                if (isCorpse && allowed < units)
                {
                    PoolAdd(t, item, units - allowed, until, false);
                    units = allowed;
                }
            }
            if (stack != null)
            {
                if (t.Stacks.Count > 300) t.Stacks.Clear();
                t.Stacks[stack] = new Transit { Units = units, Until = until, Corpse = isCorpse };
            }
            PoolAdd(t, item, units, until, isCorpse);
        }

        private static void PoolAdd(Tracker t, string item, int units, DateTime until, bool corpse)
        {
            List<Transit> l;
            if (!t.Pool.TryGetValue(item, out l)) { l = new List<Transit>(); t.Pool[item] = l; }
            if (l.Count > 200) l.RemoveAt(0);
            l.Add(new Transit { Units = units, Until = until, Corpse = corpse });
        }

        private int PoolDraw(Tracker t, string item, int want, bool corpse, DateTime now)
        {
            List<Transit> l;
            if (want <= 0 || !t.Pool.TryGetValue(item, out l)) return 0;
            int got = 0;
            foreach (Transit x in l)
            {
                if (got >= want) break;
                if (x.Corpse != corpse || x.Until <= now || x.Units <= 0) continue;
                int use = Math.Min(x.Units, want - got);
                x.Units -= use;
                got += use;
            }
            l.RemoveAll(delegate(Transit x) { return x.Units <= 0; });
            return got;
        }

        // How many of `units` taken from this corpse still count for hunting (CorpseCreditPerContainer per corpse).
        private int CorpseAllowance(object corpse, int units)
        {
            if (corpse == null || units <= 0) return 0;
            int taken;
            corpseTaken.TryGetValue(corpse, out taken);
            int allowed = Math.Max(0, Math.Min(units, config.Gathering.CorpseCreditPerContainer - taken));
            corpseTaken[corpse] = taken + allowed;
            return allowed;
        }

        // Goods a client put into its own packs. First what is known to be on the move (the same stack, then the same
        // item), then a corpse's share; whatever is left came from nowhere: gathered.
        private void Incoming(Player p, string item, int amount, object stack)
        {
            if (amount <= 0 || string.IsNullOrEmpty(item)) return;
            if (!config.Gathering.Enabled) return;
            Tracker t = Track(p.Id);
            DateTime now = Now();
            int units = amount, corpseUnits = 0, moved = 0;
            Transit tr;
            if (stack != null && t.Stacks.TryGetValue(stack, out tr) && tr.Until > now && tr.Units > 0)
            {
                int use = Math.Min(units, tr.Units);
                tr.Units -= use;
                if (tr.Units <= 0) t.Stacks.Remove(stack);
                units -= use;
                PoolDraw(t, item, use, tr.Corpse, now);                       // the same units, counted once
                if (tr.Corpse) corpseUnits += use; else moved += use;
            }
            int pooled = PoolDraw(t, item, units, false, now);
            units -= pooled;
            moved += pooled;
            int fromCorpse = PoolDraw(t, item, units, true, now);
            units -= fromCorpse;
            corpseUnits += fromCorpse;
            if (moved > 0) Watch(p, moved + " " + item + " moved (not gathered)");
            if (corpseUnits > 0) Credit(p, item, corpseUnits, "corpse");
            if (units > 0) Credit(p, item, units, "gathered");
        }

        private GatherDef GatherDefFor(string item)
        {
            GatherDef d;
            if (item != null && gatherByItem.TryGetValue(item, out d)) return d;
            if (item == null || Throttled("resolve|" + item, 60)) return null;
            ResolveGatherItems();
            return gatherByItem.TryGetValue(item, out d) ? d : null;
        }

        private int resolvedItems;
        private readonly List<string> unresolvedItems = new List<string>();

        // Config names (ResourceType names or item names) to the game's blueprint names.
        private void ResolveGatherItems()
        {
            gatherByItem.Clear();
            unresolvedItems.Clear();
            resolvedItems = 0;
            foreach (GatherDef d in config.Gathering.Items)
            {
                InvItemBlueprint bp = Blueprint(d.Item);
                if (bp == null || string.IsNullOrEmpty(bp.Name)) { unresolvedItems.Add(d.Item); gatherByItem[d.Item] = d; continue; }
                resolvedItems++;
                gatherByItem[bp.Name] = d;
                if (!gatherByItem.ContainsKey(d.Item)) gatherByItem[d.Item] = d;
            }
        }

        // Gathered (or taken from a corpse, for hunting goods only): XP and the bonus share.
        private void Credit(Player p, string item, int units, string how)
        {
            GatherDef def = GatherDefFor(item);
            if (def == null) { Watch(p, units + " " + item + " " + how + " (no gathering profession)"); return; }
            if (how == "corpse" && def.Profession != PHunt) { Watch(p, units + " " + item + " from a corpse (not a hunting good)"); return; }
            string id = p.Id.ToString();
            CraftRec rec = Rec(id, p.Name, true);
            if (rec == null) return;
            Roll(rec);
            int had;
            rec.GatheredToday.TryGetValue(def.Profession + "|" + item, out had);
            int counted = Math.Max(0, Math.Min(units, config.Gathering.UnitsPerItemPerDay - had));
            if (counted <= 0) { Watch(p, units + " " + item + " " + how + " (daily item cap reached)"); return; }
            rec.GatheredToday[def.Profession + "|" + item] = had + counted;
            data.GatheredUnits += counted;
            double got = AddXp(p, id, rec, def.Profession, counted * def.Xp);
            Watch(p, counted + " " + item + " " + how + ": " + ProfName(def.Profession, null) + " +" + Num(got) + " XP");
            double pct = YieldPercent(rec, def.Profession);
            if (pct > 0)
            {
                double owe;
                rec.BonusOwed.TryGetValue(item, out owe);
                rec.BonusOwed[item] = Math.Min(owe + counted * pct / 100.0, 100000);
            }
            dirty = true;
        }

        // Bonus yield, given in batches by the tick: whole units only, within the daily cap; fractions wait.
        private void GiveBonuses()
        {
            foreach (Player p in OnlinePlayers())
            {
                CraftRec rec = Rec(p.Id.ToString(), null, false);
                if (rec == null || rec.BonusOwed.Count == 0 || p.Entity == null) continue;
                Roll(rec);
                var parts = new List<string>();
                foreach (string item in new List<string>(rec.BonusOwed.Keys))
                {
                    double owe = rec.BonusOwed[item];
                    int whole = (int)Math.Floor(owe);
                    if (whole <= 0) continue;
                    int given;
                    rec.BonusToday.TryGetValue(item, out given);
                    int n = Math.Min(whole, config.Perks.BonusYieldMaxPerItemPerDay - given);
                    if (n <= 0) { rec.BonusOwed.Remove(item); continue; }   // capped today: the fraction is dropped
                    InvItemBlueprint bp = Blueprint(item);
                    if (bp == null) { rec.BonusOwed.Remove(item); continue; }
                    rec.BonusOwed[item] = owe - n;                    // taken off before the give: never twice
                    int got = GiveItems(p, bp, n);
                    rec.BonusToday[item] = given + got;
                    data.BonusUnitsGiven += got;
                    if (got > 0) parts.Add(got + " " + bp.Name);
                    if (rec.BonusOwed[item] < 0.0001) rec.BonusOwed.Remove(item);
                    dirty = true;
                }
                if (parts.Count > 0)
                {
                    Ask(RealmSentinel, "SentinelItemSource", p.Id, 30f);
                    Reply(p, "BonusGiven", string.Join(", ", parts.ToArray()));
                }
            }
        }

        // RB 1 [OPJ L188]: always null, so the game's own death handling continues. Creatures slain: hunting XP.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (!Counting() || evt == null || evt.Entity == null || !config.Gathering.Enabled) return null;
            try
            {
                Entity e = evt.Entity;
                if (e.IsPlayer || !IsCreature(e)) return null;
                Damage d = evt.KillingDamage;
                Player killer = d != null && d.DamageSource != null && d.DamageSource.IsPlayer ? d.DamageSource.Owner : null;
                if (killer == null || killer.IsServer) return null;
                string id = killer.Id.ToString();
                CraftRec rec = Rec(id, killer.Name, true);
                if (rec == null) return null;
                Roll(rec);
                if (rec.CreaturesToday >= config.Gathering.CreatureCreditsPerDay) { Watch(killer, "a creature slain (daily cap reached)"); return null; }
                rec.CreaturesToday++;
                string label = CreatureLabel(e);
                double xp = config.Gathering.CreatureDefaultXp;
                foreach (CreatureDef c in config.Gathering.Creatures) if (label.IndexOf(c.Match, StringComparison.Ordinal) >= 0) { xp = c.Xp; break; }
                double got = AddXp(killer, id, rec, PHunt, xp);
                Watch(killer, "slew " + label + ": " + ProfName(PHunt, null) + " +" + Num(got) + " XP");
            }
            catch (Exception ex) { if (!Throttled("death", 60)) PrintWarning("Death watch failed: " + ex.Message); }
            return null;
        }

        // A creature, as the game's own SalvageSupplier tells one [DEC]: a MonsterMotor or a MonsterEntity attribute.
        private static bool IsCreature(Entity e)
        {
            try { return e != null && !e.IsPlayer && (e.TryGet<MonsterMotor>() != null || e.TryGet<MonsterEntity>() != null); }
            catch (Exception) { return false; }
        }

        // "Wolf(Clone) (CodeHatch.Engine.Core.Cache.Entity)" -> "wolf". UNVERIFIED: the real names.
        private static string CreatureLabel(object e)
        {
            string s;
            try { s = e.ToString(); }
            catch (Exception) { s = null; }
            return Label(s, "creature");
        }

        private static string Label(string s, string fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            int paren = s.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) s = s.Substring(0, paren);
            s = s.Replace("(Clone)", "").Replace("[Entity]", "").Replace("_", " ").Trim();
            var sb = new StringBuilder();
            foreach (char c in s) if (char.IsLetterOrDigit(c) || c == ' ' || c == '-') sb.Append(char.ToLowerInvariant(c));
            string t = sb.ToString().Trim();
            if (t.Length > 40) t = t.Substring(0, 40);
            return t.Length == 0 ? fallback : t;
        }

        #endregion

        #region Crafting

        // The player who asks a crafter to start (ItemCrafterCraftEvent.Sender), so its products can be credited.
        private void OnCraftRequested(ItemCrafterCraftEvent evt)
        {
            if (!Counting() || evt == null || evt.Cancelled || evt.Crafter == null) return;
            try
            {
                Player p = evt.Sender;
                if (p == null || p.IsServer) return;
                if (craftClaims.Count > 500) craftClaims.Clear();
                craftClaims[evt.Crafter] = new CraftClaim { PlayerId = p.Id, Station = Label(SafeString(evt.Crafter), "station"), At = Now() };
            }
            catch (Exception ex) { if (!Throttled("craftreq", 60)) PrintWarning("Craft request watch failed: " + ex.Message); }
        }

        // RB 1 [OPJ L1088]. The run is over: forget whose craft the crafter was running. Always null.
        private object OnItemCrafted(ItemCrafterFinishEvent evt)
        {
            try { if (evt != null && evt.Crafter != null) craftClaims.Remove(evt.Crafter); }
            catch (Exception) { }
            return null;
        }

        // Each product stack the server makes [DEC ItemCrafter.Update -> CraftItem -> ItemCrafterItemEvent].
        private void OnCraftProduct(ItemCrafterItemEvent evt)
        {
            if (!Counting() || evt == null || evt.Cancelled || evt.Stack == null || evt.Stack.Blueprint == null || !config.Crafting.Enabled) return;
            try
            {
                ulong pid = 0;
                string station = null;
                CraftClaim c;
                if (evt.Crafter != null && craftClaims.TryGetValue(evt.Crafter, out c)) { pid = c.PlayerId; station = c.Station; }
                else if (evt.Entity != null && evt.Entity.IsPlayer && evt.Entity.Owner != null && !evt.Entity.Owner.IsServer) { pid = evt.Entity.Owner.Id; station = "hands"; }
                if (pid == 0) return;
                if (station == null && evt.Crafter != null) station = Label(SafeString(evt.Crafter), "station");
                Crafted(pid, evt.Stack.Blueprint.Name, evt.Stack.StackAmount > 0 ? evt.Stack.StackAmount : 1, station);
            }
            catch (Exception ex) { if (!Throttled("craft", 60)) PrintWarning("Craft watch failed: " + ex.Message); }
        }

        private void Crafted(ulong pid, string product, int units, string station)
        {
            if (string.IsNullOrEmpty(product)) return;
            foreach (string s in config.Crafting.Ignore) if (SameName(s, product)) return;
            string id = pid.ToString();
            Player online = OnlineById(id);
            string prof = ProfessionOfProduct(product, station);
            if (prof == null)
            {
                NoteUnmapped(product + " (at " + (station ?? "?") + ")");
                if (online != null) Watch(online, "made " + units + " " + product + " (no profession)");
                return;
            }
            CraftRec rec = Rec(id, online != null ? online.Name : null, true);
            if (rec == null) return;
            Roll(rec);
            string key = product.ToLowerInvariant();
            int n;
            rec.ProductsToday.TryGetValue(key, out n);
            rec.ProductsToday[key] = n + 1;
            double factor = n < config.Crafting.FullXpCraftsPerProductPerDay ? 1.0
                : n < config.Crafting.FullXpCraftsPerProductPerDay + config.Crafting.HalfXpCraftsPerProductPerDay ? 0.5 : 0.0;
            data.CraftsCounted++;
            double got = factor > 0 ? AddXp(online, id, rec, prof, config.Crafting.DefaultCraftXp * WeightOf(product) * factor) : 0;
            if (online != null) Watch(online, "made " + units + " " + product + ": " + ProfName(prof, null) + " +" + Num(got) + " XP" + (factor < 1 ? " (made often today)" : ""));
            dirty = true;
            if (online != null && factor > 0) MaybeExtra(online, rec, prof, product, units);
        }

        // A master's touch: a chance of one more of what was just made, into the crafter's packs.
        private void MaybeExtra(Player p, CraftRec rec, string prof, string product, int units)
        {
            if (rec.ExtrasToday >= config.Perks.ExtraItemsPerDay || p.Entity == null) return;
            foreach (string w in config.Perks.NoExtraItems) if (product.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return;
            double chance = ExtraChance(rec, prof);
            if (chance <= 0 || roll() * 100.0 >= chance) return;
            InvItemBlueprint bp = Blueprint(product);
            if (bp == null) return;
            int n = Math.Max(1, Math.Min(units, config.Perks.ExtraItemMaxUnits));
            rec.ExtrasToday++;                                              // counted before the give: never twice
            dirty = true;
            int got = GiveItems(p, bp, n);
            if (got <= 0) return;
            data.ExtrasGiven += got;
            Ask(RealmSentinel, "SentinelItemSource", p.Id, 30f);
            Reply(p, "ExtraGiven", got > 1 ? got + " " + bp.Name : bp.Name, ProfName(prof, p));
        }

        private string ProfessionOfProduct(string product, string station)
        {
            string prof;
            if (config.Crafting.Products.TryGetValue(product, out prof)) return prof;
            foreach (KeywordRule r in config.Crafting.ProductRules)
                foreach (string w in r.Words) if (product.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return r.Profession;
            if (!string.IsNullOrEmpty(station))
                foreach (KeywordRule r in config.Crafting.StationRules)
                    foreach (string w in r.Words) if (station.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return r.Profession;
            return null;
        }

        private double WeightOf(string product)
        {
            double best = -1;
            foreach (KeyValuePair<string, double> kv in config.Crafting.XpWeights)
                if (product.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value > best) best = kv.Value;
            return best < 0 ? 1.0 : best;
        }

        private void NoteUnmapped(string s)
        {
            s = Clean(s, 80);
            unmapped.Remove(s);
            unmapped.Add(s);
            if (unmapped.Count > 20) unmapped.RemoveAt(0);
        }

        // The profession an item belongs to: a gathered good, or a product by the crafting rules (no station known).
        private string ProfessionOfItem(string item)
        {
            if (string.IsNullOrEmpty(item)) return null;
            GatherDef g;
            if (gatherByItem.TryGetValue(item, out g)) return g.Profession;
            return ProfessionOfProduct(item, null);
        }

        #endregion

        #region XP, ranks and perks

        private int LevelFor(double xp)
        {
            if (xpTable == null) return 1;
            int max = xpTable.Length - 2;
            for (int lv = max; lv > 1; lv--) if (xp >= xpTable[lv]) return lv;
            return 1;
        }

        private long XpFor(int level)
        {
            if (level < 1) return 0;
            if (level > xpTable.Length - 2) return xpTable[xpTable.Length - 2];
            return xpTable[level];
        }

        private int RankOf(int level)
        {
            int r = 0;
            List<int> ranks = config.Levels.RankLevels;
            for (int i = 0; i < ranks.Count; i++) if (level >= ranks[i]) r = i;
            if (level >= config.Levels.MaxLevel) r = 5;                     // Master is always the last
            else if (r > 4) r = 4;
            return r;
        }

        private string RankName(int level, Player player)
        {
            return Msg("Rank." + RankOf(level), player);
        }

        // XP into a profession: the workshop bonus, then the daily cap. Returns what was added.
        private double AddXp(Player online, string id, CraftRec rec, string prof, double raw)
        {
            if (raw <= 0 || rec == null) return 0;
            Roll(rec);
            Prof pr = ProfOf(rec, prof);
            int tier = TierOf(rec);
            if (tier > 0) raw *= 1.0 + config.Workshops.XpBonusPercent[tier - 1] / 100.0;
            double room = config.Levels.DailyXpCap - pr.Today;
            if (room <= 0)
            {
                if (online != null && !Throttled("cap|" + id + "|" + prof, 600)) Reply(online, "DailyCap", ProfName(prof, online));
                return 0;
            }
            double gained = Math.Min(raw, room);
            int before = pr.Level;
            pr.Today += gained;
            pr.Week += gained;
            rec.WeekXp += gained;
            pr.Xp = Math.Min(pr.Xp + gained, 1e12);
            pr.Level = LevelFor(pr.Xp);
            Pool(id, rec, gained);
            dirty = true;
            if (pr.Level > before) LevelledUp(online, id, rec, prof, before, pr.Level);
            return gained;
        }

        private void LevelledUp(Player online, string id, CraftRec rec, string prof, int before, int after)
        {
            int oldRank = RankOf(before), newRank = RankOf(after);
            if (online != null && newRank == oldRank) Reply(online, "LevelUp", ProfName(prof, online), after);
            if (newRank <= oldRank) return;
            for (int r = oldRank + 1; r <= newRank; r++)
            {
                if (config.Rewards.RankDeed.Length > 0 && r < 5)
                    Ask(RealmRenown, "AddDeed", id, rec.Name, config.Rewards.RankDeed, ProfName(prof, null) + ": " + Msg("Rank." + r, null), "crafts:rank:" + id + ":" + prof + ":" + r);
            }
            string rankName = Msg("Rank." + newRank, online);
            if (newRank >= 5) { Mastered(online, id, rec, prof); return; }
            if (online != null) Reply(online, "RankUp", rankName, ProfName(prof, online));
            if (newRank >= config.Levels.HeraldFromRank && HeraldBudget()) Herald(Fmt("RankHerald", null, rec.Name, Msg("Rank." + newRank, null), ProfName(prof, null)));
        }

        private void Mastered(Player online, string id, CraftRec rec, string prof)
        {
            string title = Msg("Master." + prof, null);
            if (online != null)
            {
                Reply(online, "MasterYou", Msg("Master." + prof, online), ProfName(prof, online));
                Notice(online, Fmt("MasterPopupTitle", online, ProfName(prof, online)), Fmt("MasterPopupBody", online, Msg("Master." + prof, online)));
            }
            if (HeraldBudget()) Herald(Fmt("MasterHerald", null, rec.Name, title, ProfName(prof, null)));
            if (config.Rewards.MasterDeed.Length > 0)
                Ask(RealmRenown, "AddDeed", id, rec.Name, config.Rewards.MasterDeed, title, "crafts:master:" + id + ":" + prof);
            if (config.Rewards.ChronicleMasters)
                Ask(RealmChronicle, "Log", TitleChronicleType, rec.Name + " is named " + title, ProfName(prof, null) + ": the rank of Master, the highest the guild knows.", new string[] { rec.Name });
            if (config.Rewards.QuestReports) Ask(RealmQuests, "ReportQuestEvent", id, "custom", "mastery", 1);
        }

        private int LevelIn(CraftRec rec, string prof)
        {
            Prof p;
            return rec != null && rec.Profs.TryGetValue(prof, out p) ? p.Level : 1;
        }

        private double YieldPercent(CraftRec rec, string prof)
        {
            if (Array.IndexOf(GatherProfs, prof) < 0) return 0;
            double pct = LevelIn(rec, prof) * config.Perks.BonusYieldPercentPerLevel;
            int tier = TierOf(rec);
            if (tier > 0) pct += config.Workshops.YieldBonusPoints[tier - 1];
            pct += HoldingPoints(rec, prof, true);
            return Math.Max(0, Math.Min(50, pct));
        }

        private double ExtraChance(CraftRec rec, string prof)
        {
            if (Array.IndexOf(CraftProfs, prof) < 0) return 0;
            double pct = LevelIn(rec, prof) * config.Perks.ExtraItemChancePerLevel;
            int tier = TierOf(rec);
            if (tier > 0) pct += config.Workshops.ExtraChancePoints[tier - 1];
            pct += HoldingPoints(rec, prof, false);
            return Math.Max(0, Math.Min(50, pct));
        }

        private int DiscountAt(int level)
        {
            int pct = 0;
            foreach (TierLevel t in config.Perks.MarketDiscount) if (level >= t.Level) pct = t.Percent;
            return Math.Max(0, Math.Min(50, pct));
        }

        // RealmDominion: what the holdings of the player's house add to this profession.
        private int HoldingPoints(CraftRec rec, string prof, bool yield)
        {
            if (!config.Dominion.Enabled || rec == null || string.IsNullOrEmpty(rec.House)) return 0;
            List<string> kinds;
            if (!holdingKinds.TryGetValue(rec.House.Trim(), out kinds)) return 0;
            int pts = 0;
            foreach (HoldingPerk hp in config.Dominion.Perks)
                if (hp.Profession == prof && kinds.Contains(hp.Kind)) pts += yield ? hp.YieldPoints : hp.ExtraChancePoints;
            return Math.Min(pts, 25);
        }

        private void PollDominion()
        {
            holdingKinds.Clear();
            if (RealmDominion == null) return;
            var list = Ask(RealmDominion, "GetHoldings") as List<Dictionary<string, object>>;
            if (list == null) return;
            foreach (Dictionary<string, object> d in list)
            {
                object owner, kind, placed;
                if (!d.TryGetValue("owner", out owner) || !d.TryGetValue("kind", out kind)) continue;
                string o = owner as string, k = kind as string;
                if (string.IsNullOrEmpty(o) || string.IsNullOrEmpty(k)) continue;
                if (d.TryGetValue("placed", out placed) && placed is bool && !(bool)placed) continue;
                List<string> kinds;
                if (!holdingKinds.TryGetValue(o.Trim(), out kinds)) { kinds = new List<string>(); holdingKinds[o.Trim()] = kinds; }
                if (!kinds.Contains(k.ToLowerInvariant())) kinds.Add(k.ToLowerInvariant());
            }
        }

        #endregion

        #region Weekly crowning

        private DateTime NextCrowning(DateTime from)
        {
            DayOfWeek day;
            TryDay(config.Weekly.CrownDay, out day);
            DateTime d = new DateTime(from.Year, from.Month, from.Day, config.Weekly.CrownHourUtc, 0, 0, DateTimeKind.Utc);
            int add = ((int)day - (int)d.DayOfWeek + 7) % 7;
            d = d.AddDays(add);
            if (d <= from) d = d.AddDays(7);
            return d;
        }

        private static string KeyOf(DateTime crowning)
        {
            return crowning.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // Ranking of the week: every player whose records belong to this week, most XP first.
        private List<KeyValuePair<string, CraftRec>> WeekRanking()
        {
            var list = new List<KeyValuePair<string, CraftRec>>();
            foreach (KeyValuePair<string, CraftRec> kv in data.Players) if (kv.Value.WeekKey == data.WeekKey && kv.Value.WeekXp > 0) list.Add(kv);
            list.Sort(delegate(KeyValuePair<string, CraftRec> a, KeyValuePair<string, CraftRec> b)
            {
                int c = b.Value.WeekXp.CompareTo(a.Value.WeekXp);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            return list;
        }

        private void Crown()
        {
            string week = data.WeekKey;
            foreach (WeekResult done in data.History) if (done.WeekKey == week) { CloseWeek(); return; }   // never twice
            KeyValuePair<string, CraftRec> best = new KeyValuePair<string, CraftRec>(null, null);
            foreach (KeyValuePair<string, CraftRec> kv in WeekRanking())
            {
                if (kv.Value.WeekXp < config.Weekly.MinWeeklyXp) break;
                if (!config.General.AdminsCountForWeekly && permission.UserHasPermission(kv.Key, PermAdmin)) continue;
                best = kv;
                break;
            }
            var result = new WeekResult { WeekKey = week, At = Now() };
            if (best.Key != null)
            {
                CraftRec r = best.Value;
                result.WinnerId = best.Key;
                result.WinnerName = r.Name;
                result.Xp = (int)Math.Floor(r.WeekXp);
                result.House = r.House;
                r.Crowns++;
            }
            data.History.Add(result);
            if (data.History.Count > 52) data.History.RemoveAt(0);
            SaveData();                                                     // the week is settled before anything is paid
            if (best.Key != null) RewardWinner(best.Key, best.Value, result);
            else if (HeraldBudget()) Herald(Msg("CrownNone", null));
            SettleWorkshops();
            CloseWeek();
        }

        private void RewardWinner(string id, CraftRec r, WeekResult result)
        {
            Herald(Fmt("CrownHerald", null, r.Name, result.Xp));
            Player online = OnlineById(id);
            long paid = 0, want = config.Weekly.RewardMarks;
            if (want > 0)
            {
                object got = Ask(RealmTreasury, "RewardMarks", id, r.Name, want, "RealmCrafts weekly");
                paid = got is long ? (long)got : 0;
                if (paid < want) { long o; data.OwedMarks.TryGetValue(id, out o); data.OwedMarks[id] = o + (want - paid); }
            }
            if (online != null)
            {
                if (paid < want) Reply(online, "CrownYouOwed", want - paid); else Reply(online, "CrownYou", paid);
                Notice(online, Msg("CrownPopupTitle", online), Fmt("CrownPopupBody", online, result.Xp));
            }
            if (config.Rewards.WeeklyDeed.Length > 0)
                Ask(RealmRenown, "AddDeed", id, r.Name, config.Rewards.WeeklyDeed, "Master Crafter of the week", "crafts:week:" + result.WeekKey);
            Ask(RealmChronicle, "Log", TitleChronicleType, r.Name + " is named Master Crafter", "The guilds' finest of the week, with " + result.Xp + " XP.", new string[] { r.Name });
            if (!string.IsNullOrEmpty(r.House) && config.Weekly.HousePoints > 0
                && AskBool(RealmSeasons, "AwardHouse", r.House, config.Weekly.HousePoints, "Master Crafter " + r.Name))
                Server.BroadcastMessage(Fmt("CrownHouse", null, HouseTint(r.House), config.Weekly.HousePoints));
            dirty = true;
        }

        private void SettleWorkshops()
        {
            if (!config.Workshops.Enabled) return;
            var parts = new List<string>();
            foreach (HouseWs h in data.Houses.Values)
            {
                if (h.WeekKey != data.WeekKey || h.WeekXp <= 0) continue;
                int t = 0;
                for (int i = 0; i < 3; i++) if (h.WeekXp >= config.Workshops.TierXp[i]) t = i + 1;
                if (t == 0) continue;
                int pts = config.Workshops.SeasonPoints[t - 1];
                if (pts > 0 && AskBool(RealmSeasons, "AwardHouse", h.Name, pts, "workshop " + Msg("Tier." + t, null)))
                    parts.Add(Fmt("WorkshopLine", null, HouseTint(h.Name), Msg("Tier." + t, null), pts));
            }
            if (parts.Count > 0) Herald(Fmt("WorkshopHerald", null, string.Join(", ", parts.ToArray())));
        }

        private void CloseWeek()
        {
            data.PrevWeekKey = data.WeekKey;
            data.NextCrowning = NextCrowning(Now());
            data.WeekKey = KeyOf(data.NextCrowning);
            foreach (HouseWs h in data.Houses.Values) RollHouse(h);
            var stale = new List<string>();
            foreach (KeyValuePair<string, HouseWs> kv in data.Houses) if (kv.Value.WeekXp <= 0 && kv.Value.LastWeekXp <= 0 && (Now() - kv.Value.LastActive).TotalDays > 30) stale.Add(kv.Key);
            foreach (string k in stale) data.Houses.Remove(k);
            dirty = true;
            SaveData();
        }

        private void PayOwedMarks(string id)
        {
            long owe;
            if (!data.OwedMarks.TryGetValue(id, out owe) || owe <= 0) { data.OwedMarks.Remove(id); return; }
            CraftRec r = Rec(id, null, false);
            object got = Ask(RealmTreasury, "RewardMarks", id, r != null ? r.Name : id, owe, "RealmCrafts weekly");
            long paid = got is long ? (long)got : 0;
            if (paid <= 0) return;
            if (paid >= owe) data.OwedMarks.Remove(id); else data.OwedMarks[id] = owe - paid;
            dirty = true;
            Player p = OnlineById(id);
            if (p != null) Reply(p, "MarksPaid", paid);
        }

        #endregion

        #region Commissions

        private Commission FindCommission(string idText)
        {
            int id;
            if (idText == null || !int.TryParse(idText.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return null;
            foreach (Commission c in data.Commissions) if (c.Id == id) return c;
            return null;
        }

        private int OpenCount(string posterId)
        {
            int n = 0;
            foreach (Commission c in data.Commissions) if (c.Status == SOpen && (posterId == null || c.PosterId == posterId)) n++;
            return n;
        }

        private int FeePercent()
        {
            if (config.Commissions.FeePercent >= 0) return config.Commissions.FeePercent;
            string s = AskString(RealmTreasury, "GetTreasurySummary");         // "marks|minted|fee|tithe|openOrders"
            if (s == null) return 0;
            string[] parts = s.Split('|');
            int fee;
            return parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fee) ? Clamp(fee, 0, 50) : 0;
        }

        private void CmdOrder(Player player, string[] args)
        {
            if (!config.Commissions.Enabled) { ReplyError(player, "PartClosed"); return; }
            // /craft order <item words...> <qty> <price> [min level]
            var nums = new List<long>();
            int end = args.Length;
            while (end > 1 && nums.Count < 3)
            {
                long v;
                if (!long.TryParse(args[end - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) break;
                nums.Insert(0, v);
                end--;
            }
            if (nums.Count < 2 || end < 2) { ReplyError(player, "OrderUsage"); return; }
            string itemText = string.Join(" ", args, 1, end - 1).Trim('"', ' ');
            long qty = nums[0], price = nums[1], minLevel = nums.Count > 2 ? nums[2] : 0;
            InvItemBlueprint bp = Blueprint(itemText);
            if (bp == null) { ReplyError(player, "OrderUnknownItem", Clean(itemText, 40)); return; }
            if (qty < 1 || qty > config.Commissions.MaxQty) { ReplyError(player, "OrderBadQty", config.Commissions.MaxQty); return; }
            if (price < 1 || price > config.Commissions.MaxPrice) { ReplyError(player, "OrderBadPrice", config.Commissions.MaxPrice); return; }
            if (minLevel < 0 || minLevel > config.Levels.MaxLevel) { ReplyError(player, "OrderBadLevel", config.Levels.MaxLevel); return; }
            long total = qty * price;
            if (total > config.Commissions.MaxTotal) { ReplyError(player, "OrderBadTotal", config.Commissions.MaxTotal); return; }
            string id = player.Id.ToString();
            if (OpenCount(id) >= config.Commissions.MaxOpenPerPlayer) { ReplyError(player, "OrderTooMany", OpenCount(id)); return; }
            if (OpenCount(null) >= config.Commissions.MaxOpen) { ReplyError(player, "OrderBoardFull"); return; }
            DateTime last;
            if (data.LastPost.TryGetValue(id, out last) && (Now() - last).TotalSeconds < config.Commissions.PostCooldownSeconds)
            { ReplyError(player, "OrderCooldown", When(last.AddSeconds(config.Commissions.PostCooldownSeconds), player)); return; }
            if (RealmTreasury == null) { ReplyError(player, "OrderNoTreasury"); return; }
            var c = new Commission
            {
                Id = data.NextCommissionId, PosterId = id, PosterName = Clean(player.Name, 40), Item = bp.Name, Profession = ProfessionOfItem(bp.Name),
                Qty = (int)qty, Price = price, MinLevel = (int)minLevel, PostedAt = Now(), Expires = Now().AddHours(config.Commissions.DurationHours)
            };
            c.HoldId = "crafts:" + c.Id + ":" + c.PostedAt.Ticks.ToString(CultureInfo.InvariantCulture);
            object held = Ask(RealmTreasury, "HoldMarks", c.HoldId, id, c.PosterName, total, HoldSource, config.Commissions.DurationHours * 60 + 120);
            if (!(held is long) || (long)held != total)
            {
                if (held is long && (long)held > 0) Ask(RealmTreasury, "ReleaseHold", c.HoldId, HoldSource);
                object purse = Ask(RealmTreasury, "GetPurse", id);
                if (purse is long && (long)purse < total) ReplyError(player, "OrderShort", total, (long)purse);
                else ReplyError(player, "OrderNoTreasury");
                return;
            }
            data.NextCommissionId++;
            data.Commissions.Add(c);
            data.LastPost[id] = Now();
            dirty = true;
            SaveData();
            Reply(player, "OrderPosted", c.Id, c.Item, c.Qty, c.Price, total);
            if (c.Profession != null) Reply(player, "OrderPostedProf", ProfName(c.Profession, player), c.MinLevel > 0 ? Fmt("MinLevelNote", player, c.MinLevel) : "", config.Commissions.DurationHours);
            if (total >= 500 && HeraldBudget()) Herald(Fmt("OrderHerald", null, c.PosterName, c.Item, c.Qty, c.Price, c.Id));
            PruneCommissions();
        }

        private void CmdOrders(Player player, string[] args)
        {
            string id = player.Id.ToString();
            string word = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1).Trim() : "";
            if (word.ToLowerInvariant() == "mine")
            {
                var mine = new List<Commission>();
                foreach (Commission c in data.Commissions) if (c.PosterId == id) mine.Add(c);
                if (mine.Count == 0) { Reply(player, "OrdersMineNone"); return; }
                Reply(player, "OrdersMineHeader");
                int shown = 0;
                for (int i = mine.Count - 1; i >= 0 && shown < 10; i--, shown++)
                {
                    Commission c = mine[i];
                    string st = c.Status == SOpen ? Fmt("Status.open", player, When(c.Expires, player)) : Msg("Status." + c.Status, player);
                    Reply(player, "OrdersMineLine", c.Id, c.Item, c.Filled, c.Qty, c.Price, st);
                }
                return;
            }
            var open = new List<Commission>();
            string prof = word.Length > 0 ? NormalizeProfession(word) : null;
            foreach (Commission c in data.Commissions)
            {
                if (c.Status != SOpen || Now() >= c.Expires) continue;
                if (word.Length > 0 && (prof != null ? c.Profession != prof : c.Item.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                open.Add(c);
            }
            if (open.Count == 0) { Reply(player, "OrdersNone"); return; }
            open.Sort(delegate(Commission a, Commission b) { return b.Price.CompareTo(a.Price); });
            Reply(player, "OrdersHeader", open.Count);
            for (int i = 0; i < open.Count && i < 10; i++)
            {
                Commission c = open[i];
                Reply(player, "OrdersLine", c.Id, c.Item, c.Qty - c.Filled, c.Price, c.PosterName, c.MinLevel > 0 ? Fmt("MinLevelNote", player, c.MinLevel) : "", Span(c.Expires - Now()));
            }
            if (open.Count > 10) Reply(player, "OrdersMore", open.Count - 10);
        }

        private void CmdFill(Player player, string[] args)
        {
            if (!config.Commissions.Enabled) { ReplyError(player, "PartClosed"); return; }
            if (args.Length < 2) { ReplyError(player, "FillUsage"); return; }
            Commission c = FindCommission(args[1]);
            if (c == null || c.Status != SOpen || Now() >= c.Expires) { ReplyError(player, "FillUnknown", Clean(args[1], 10)); return; }
            string id = player.Id.ToString();
            if (c.PosterId == id) { ReplyError(player, "FillOwn"); return; }
            if (RealmTreasury == null) { ReplyError(player, "OrderNoTreasury"); return; }
            CraftRec posterRec = Rec(c.PosterId, c.PosterName, true);
            if (posterRec != null && posterRec.Owed.Count >= config.Commissions.MaxOwedLines && !OwesItem(posterRec, c.Item)) { ReplyError(player, "FillPosterFull", c.PosterName); return; }
            int left = c.Qty - c.Filled;
            int qty = left;
            if (args.Length > 2)
            {
                if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out qty) || qty < 1) { ReplyError(player, "FillUsage"); return; }
                qty = Math.Min(qty, left);
            }
            CraftRec rec = Rec(id, player.Name, true);
            if (c.MinLevel > 0 && c.Profession != null && LevelIn(rec, c.Profession) < c.MinLevel)
            { ReplyError(player, "FillLevel", c.Id, ProfName(c.Profession, player), c.MinLevel, LevelIn(rec, c.Profession)); return; }
            InvItemBlueprint bp = Blueprint(c.Item);
            int have = bp != null ? CountItems(player, bp) : 0;
            if (have <= 0) { ReplyError(player, "FillNone", c.Item); return; }
            qty = Math.Min(qty, have);
            if (GetHold(c.HoldId) < qty * c.Price) { CloseLapsed(c); ReplyError(player, "FillHoldGone", c.Id); return; }
            int taken = TakeItems(player, bp, qty);                         // measured: what really left the packs
            if (taken <= 0) { ReplyError(player, "FillTakeFailed"); return; }
            long due = taken * c.Price;
            object paidObj = Ask(RealmTreasury, "PayFromHold", c.HoldId, id, rec.Name, due, HoldSource);
            long paid = paidObj is long ? (long)paidObj : 0;
            if (paid < due)
            {
                // The hold could not pay in full (it lapsed in between): what was paid stands, the rest of the goods go back.
                int paidUnits = (int)(paid / c.Price);
                int back = taken - paidUnits;
                if (back > 0) { AddOwed(rec, c.Item, back, "commission #" + c.Id); PayOwed(player, rec); }
                taken = paidUnits;
                CloseLapsed(c);
                if (taken <= 0) { ReplyError(player, "FillHoldGone", c.Id); SaveData(); return; }
            }
            long fee = 0;
            int pct = FeePercent();
            if (pct > 0)
            {
                fee = paid * pct / 100;
                fee -= fee * GetMarketFeeDiscount(id, c.Item) / 100;
                if (fee > 0 && !AskBool(RealmTreasury, "ChargeMarks", id, rec.Name, fee, HoldSource, "commission #" + c.Id + " fee")) fee = 0;
            }
            c.Filled += taken;
            c.PaidOut += paid;
            if (c.Filled >= c.Qty) { c.Status = SDone; Ask(RealmTreasury, "ReleaseHold", c.HoldId, HoldSource); }
            data.CommissionsFilled++;
            data.CommissionMarks += paid;
            // The poster's goods: owed first (saved), then given if they are here; a crash never pays them twice.
            CraftRec poster = Rec(c.PosterId, c.PosterName, true);
            AddOwed(poster, c.Item, taken, "commission #" + c.Id);
            dirty = true;
            SaveData();
            Reply(player, "Filled", c.Id, c.Item, taken, paid - fee, fee);
            CommissionXp(player, id, rec, c, paid);
            if (config.Rewards.QuestReports) Ask(RealmQuests, "ReportQuestEvent", id, "custom", "commission", 1);
            Player po = OnlineById(c.PosterId);
            if (po != null)
            {
                string given = PayOwed(po, poster);
                Reply(po, "PosterNotice", rec.Name, c.Id, c.Item, taken, poster.Owed.Count == 0 && given.Length > 0 ? Msg("PosterGiven", po) : Msg("PosterOwed", po));
                if (c.Status == SDone) Reply(po, "PosterDone", c.Id);
            }
        }

        // XP for a filled commission, unless it looks like an alt feeding a main (see the header).
        private void CommissionXp(Player player, string id, CraftRec rec, Commission c, long paid)
        {
            string why = null;
            ulong pu;
            string a, b;
            string pair = string.CompareOrdinal(id, c.PosterId) < 0 ? id + "|" + c.PosterId : c.PosterId + "|" + id;
            DateTime last;
            long market = AskLong(RealmTreasury, "GetLastPrice", c.Item);
            if (c.Profession == null) why = "none";
            else if (addressOf.TryGetValue(player.Id, out a) && ulong.TryParse(c.PosterId, out pu) && addressOf.TryGetValue(pu, out b) && a == b) why = "address";
            else if (!config.Commissions.XpWithinHouse && rec.House != null && SameName(rec.House, AskString(RealmHouses, "GetHouse", c.PosterId))) why = "house";
            else if (config.Commissions.PairCooldownHours > 0 && data.CommissionPairs.TryGetValue(pair, out last) && (Now() - last).TotalHours < config.Commissions.PairCooldownHours) why = "pair";
            else if (market > 0 && c.Price < market * config.Commissions.MinPriceRatioForXp) why = "price";
            else
            {
                Roll(rec);
                if (rec.CommissionXpToday >= config.Commissions.XpPerDay) why = "cap";
            }
            if (why != null) { Reply(player, "FilledNoXp", Msg("NoXp." + why, player)); return; }
            double xp = Math.Min(config.Commissions.XpMaxPerFill, paid * config.Commissions.XpPerMark);
            xp = Math.Min(xp, config.Commissions.XpPerDay - rec.CommissionXpToday);
            data.CommissionPairs[pair] = Now();
            if (xp <= 0) return;
            double got = AddXp(player, id, rec, c.Profession, xp);
            rec.CommissionXpToday += got;
            if (got > 0) Reply(player, "FilledXp", ProfName(c.Profession, player), Num(got));
            if (config.Rewards.CommissionDeed.Length > 0)
                Ask(RealmRenown, "AddDeed", id, rec.Name, config.Rewards.CommissionDeed, "commission #" + c.Id, "crafts:commission:" + c.Id);
        }

        private void CmdCancel(Player player, string[] args)
        {
            if (args.Length < 2) { ReplyError(player, "CancelUsage"); return; }
            Commission c = FindCommission(args[1]);
            if (c == null || c.Status != SOpen) { ReplyError(player, "FillUnknown", Clean(args[1], 10)); return; }
            if (c.PosterId != player.Id.ToString()) { ReplyError(player, "CancelNotYours", c.Id); return; }
            if (RealmTreasury == null) { ReplyError(player, "OrderNoTreasury"); return; }
            c.Status = SCancelled;
            dirty = true;
            SaveData();                                                     // closed first: a second cancel finds nothing
            long back = AskLong(RealmTreasury, "ReleaseHold", c.HoldId, HoldSource);
            Reply(player, "Cancelled", c.Id, back);
        }

        // A commission whose hold is gone (it lapsed in the treasury) closes; the treasury already returned the marks.
        private void CloseLapsed(Commission c)
        {
            if (c.Status != SOpen) return;
            c.Status = SExpired;
            dirty = true;
        }

        private void ExpireCommissions(DateTime now)
        {
            foreach (Commission c in data.Commissions)
            {
                if (c.Status != SOpen) continue;
                bool lapsed = now >= c.Expires;
                bool holdGone = RealmTreasury != null && GetHold(c.HoldId) <= 0;
                if (!lapsed && !holdGone) continue;
                c.Status = SExpired;
                dirty = true;
                SaveData();
                long back = lapsed && !holdGone ? AskLong(RealmTreasury, "ReleaseHold", c.HoldId, HoldSource) : 0;
                Player p = OnlineById(c.PosterId);
                if (p != null) Reply(p, "Lapsed", c.Id, c.Item, back);
            }
        }

        private long GetHold(string holdId)
        {
            return AskLong(RealmTreasury, "GetHold", holdId);
        }

        private void PruneCommissions()
        {
            if (data.Commissions.Count <= config.Commissions.MaxOpen + 200) return;
            var closed = new List<Commission>();
            foreach (Commission c in data.Commissions) if (c.Status != SOpen) closed.Add(c);
            closed.Sort(delegate(Commission a, Commission b) { return a.PostedAt.CompareTo(b.PostedAt); });
            for (int i = 0; i < closed.Count && data.Commissions.Count > config.Commissions.MaxOpen + 100; i++) data.Commissions.Remove(closed[i]);
        }

        private static bool OwesItem(CraftRec rec, string item)
        {
            foreach (Owed o in rec.Owed) if (o.Item == item) return true;
            return false;
        }

        private void AddOwed(CraftRec rec, string item, int amount, string note)
        {
            if (rec == null || amount <= 0) return;
            foreach (Owed o in rec.Owed) if (o.Item == item) { o.Amount += amount; return; }
            rec.Owed.Add(new Owed { Item = item, Amount = amount, Note = note });
        }

        // Pays what the ledger owes, measured. Each line leaves the ledger and the file is saved BEFORE the give; what
        // did not fit goes back and is saved again. A crash in between loses items, it never pays them twice.
        private string PayOwed(Player player, CraftRec rec)
        {
            if (rec == null || rec.Owed.Count == 0 || player == null || player.Entity == null) return "";
            var paying = new List<Owed>(rec.Owed);
            rec.Owed.Clear();
            dirty = true;
            SaveData();
            Ask(RealmSentinel, "SentinelItemSource", player.Id, 30f);
            var parts = new List<string>();
            foreach (Owed o in paying)
            {
                InvItemBlueprint bp = Blueprint(o.Item);
                int given = bp != null ? GiveItems(player, bp, o.Amount) : 0;
                if (given > 0) parts.Add(given + " " + bp.Name);
                if (given < o.Amount) rec.Owed.Add(new Owed { Item = o.Item, Amount = o.Amount - given, Note = o.Note });
            }
            dirty = true;
            SaveData();
            return string.Join(", ", parts.ToArray());
        }

        private void OwedOnJoin(ulong id)
        {
            if (loadFailed || data == null) return;
            Player p = OnlineById(id.ToString());
            CraftRec rec = p != null ? Rec(p.Id.ToString(), p.Name, false) : null;
            if (rec == null || rec.Owed.Count == 0) return;
            string given = PayOwed(p, rec);
            if (given.Length > 0) Reply(p, "Collected", given);
            if (rec.Owed.Count > 0) Reply(p, "OwedOnJoin");
        }

        private void CmdCollect(Player player)
        {
            CraftRec rec = Rec(player.Id.ToString(), player.Name, true);
            if (rec.Owed.Count == 0) { ReplyError(player, "CollectNone"); return; }
            string given = PayOwed(player, rec);
            if (given.Length == 0) { ReplyError(player, "CollectFull"); return; }
            Reply(player, "Collected", given);
            if (rec.Owed.Count > 0) Reply(player, "Pending");
        }

        #endregion

        #region Commands

        [ChatCommand("craft")]
        private void CmdCraft(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "admin") { CmdAdmin(player, args); return; }
            if (!config.General.Enabled) { ReplyError(player, "Closed"); return; }
            switch (sub)
            {
                case "": ShowMe(player); return;
                case "help": ShowHelp(player); return;
                case "perks": ShowPerks(player); return;
                case "top": ShowTop(player, args); return;
                case "house": ShowHouse(player); return;
                case "orders": CmdOrders(player, args); return;
                case "order": CmdOrder(player, args); return;
                case "fill": CmdFill(player, args); return;
                case "cancel": CmdCancel(player, args); return;
                case "collect": CmdCollect(player); return;
            }
            string prof = NormalizeProfession(sub);
            if (prof != null) { ShowProfession(player, prof); return; }
            ReplyError(player, "UnknownProfession", Clean(sub, 20), ProfList(player));
        }

        private void ShowHelp(Player player)
        {
            Reply(player, "Help1");
            Reply(player, "Help2");
            Reply(player, "Help3");
            if (IsAdmin(player)) Reply(player, "HelpAdmin");
        }

        private void ShowMe(Player player)
        {
            string id = player.Id.ToString();
            CraftRec rec = Rec(id, player.Name, true);
            Roll(rec);
            Reply(player, "MeHeader", Num(rec.WeekXp));
            foreach (string prof in Professions)
            {
                Prof p = ProfOf(rec, prof);
                string next = p.Level >= config.Levels.MaxLevel ? Msg("MeMax", player) : Fmt("MeNext", player, Num(XpFor(p.Level + 1) - p.Xp), p.Level + 1);
                Reply(player, "MeLine", ProfName(prof, player), RankName(p.Level, player), p.Level, next, Num(p.Today), config.Levels.DailyXpCap);
            }
            if (!string.IsNullOrEmpty(rec.House) && config.Workshops.Enabled)
            {
                HouseWs h = House(rec.House, false);
                int tier = Tier(h);
                double week = h != null ? h.WeekXp : 0;
                if (tier > 0) Reply(player, "MeHouse", HouseTint(rec.House), Msg("Tier." + tier, player), Num(week), config.Workshops.XpBonusPercent[tier - 1]);
                else Reply(player, "MeHouseNone", HouseTint(rec.House), Num(week), config.Workshops.TierXp[0]);
            }
            if (rec.Owed.Count > 0) Reply(player, "MeOwed");
            Reply(player, "MeHint");
        }

        private void ShowProfession(Player player, string prof)
        {
            CraftRec rec = Rec(player.Id.ToString(), player.Name, true);
            Roll(rec);
            Prof p = ProfOf(rec, prof);
            Reply(player, "ProfHeader", ProfName(prof, player), RankName(p.Level, player), p.Level, config.Levels.MaxLevel, Num(p.Xp));
            Reply(player, "ProfEarn", Msg("Earn." + prof, player));
            if (p.Level >= config.Levels.MaxLevel) Reply(player, "ProfMaxed", Msg("Master." + prof, player));
            else
            {
                int nextRank = -1;
                foreach (int r in config.Levels.RankLevels) if (r > p.Level) { nextRank = r; break; }
                if (nextRank < 0) nextRank = config.Levels.MaxLevel;
                Reply(player, "ProfNext", Num(XpFor(p.Level + 1) - p.Xp), p.Level + 1, RankName(nextRank, player), nextRank);
            }
            Reply(player, "ProfToday", Num(p.Today), config.Levels.DailyXpCap, Num(p.Week));
            if (Array.IndexOf(GatherProfs, prof) >= 0) Reply(player, "ProfYield", Num(YieldPercent(rec, prof)));
            else Reply(player, "ProfExtra", Num(ExtraChance(rec, prof)), rec.ExtrasToday, config.Perks.ExtraItemsPerDay);
            int disc = DiscountAt(p.Level);
            if (disc > 0) Reply(player, "ProfDiscount", disc);
            else
                foreach (TierLevel t in config.Perks.MarketDiscount)
                    if (t.Level > p.Level && t.Percent > 0) { Reply(player, "ProfDiscountNext", t.Level, t.Percent); break; }
        }

        private void ShowPerks(Player player)
        {
            Reply(player, "PerksHeader");
            List<int> ranks = config.Levels.RankLevels;
            for (int i = 0; i < ranks.Count; i++)
            {
                int lv = ranks[i];
                int disc = DiscountAt(lv);
                Reply(player, "PerksLine", RankName(lv, player), lv, Num(lv * config.Perks.BonusYieldPercentPerLevel), Num(lv * config.Perks.ExtraItemChancePerLevel),
                    disc > 0 ? Fmt("PerksFee", player, disc) : "");
            }
            if (config.Workshops.Enabled)
            {
                var tiers = new List<string>();
                for (int t = 1; t <= 3; t++)
                    tiers.Add(Fmt("PerksHouseTier", player, Msg("Tier." + t, player), config.Workshops.XpBonusPercent[t - 1], config.Workshops.YieldBonusPoints[t - 1], config.Workshops.ExtraChancePoints[t - 1]));
                Reply(player, "PerksHouse", string.Join("; ", tiers.ToArray()));
            }
            if (config.Dominion.Enabled && config.Dominion.Perks.Count > 0 && RealmDominion != null)
            {
                var parts = new List<string>();
                foreach (HoldingPerk hp in config.Dominion.Perks)
                    parts.Add(Fmt("PerksDominionLine", player, hp.Kind, ProfName(hp.Profession, player), hp.YieldPoints > 0 ? hp.YieldPoints : hp.ExtraChancePoints));
                Reply(player, "PerksDominion", string.Join(", ", parts.ToArray()));
            }
        }

        private void ShowTop(Player player, string[] args)
        {
            string what = args.Length > 1 ? args[1].ToLowerInvariant() : "week";
            if (what == "houses" || what == "house" || what == "workshops")
            {
                var houses = new List<HouseWs>();
                foreach (HouseWs h in data.Houses.Values) { RollHouse(h); if (h.WeekXp > 0) houses.Add(h); }
                houses.Sort(delegate(HouseWs a, HouseWs b) { return b.WeekXp.CompareTo(a.WeekXp); });
                Reply(player, "TopHousesHeader");
                if (houses.Count == 0) { Reply(player, "TopNone"); return; }
                for (int i = 0; i < houses.Count && i < config.Weekly.TopCount; i++)
                    Reply(player, "TopHousesLine", i + 1, HouseTint(houses[i].Name), Num(houses[i].WeekXp), Msg("Tier." + Tier(houses[i]), player));
                return;
            }
            string prof = what == "week" ? null : NormalizeProfession(what);
            if (what != "week" && prof == null) { ReplyError(player, "UnknownProfession", Clean(what, 20), ProfList(player)); return; }
            if (prof != null)
            {
                var list = new List<KeyValuePair<string, CraftRec>>();
                foreach (KeyValuePair<string, CraftRec> kv in data.Players) { Prof p; if (kv.Value.Profs.TryGetValue(prof, out p) && p.Xp > 0) list.Add(kv); }
                list.Sort(delegate(KeyValuePair<string, CraftRec> a, KeyValuePair<string, CraftRec> b) { return b.Value.Profs[prof].Xp.CompareTo(a.Value.Profs[prof].Xp); });
                Reply(player, "TopProfHeader", ProfName(prof, player));
                if (list.Count == 0) { Reply(player, "TopNone"); return; }
                for (int i = 0; i < list.Count && i < config.Weekly.TopCount; i++)
                {
                    Prof p = list[i].Value.Profs[prof];
                    Reply(player, "TopProfLine", i + 1, list[i].Value.Name, RankName(p.Level, player), p.Level);
                }
                return;
            }
            List<KeyValuePair<string, CraftRec>> week = WeekRanking();
            Reply(player, "TopHeader", When(data.NextCrowning, player));
            if (week.Count == 0) Reply(player, "TopNone");
            for (int i = 0; i < week.Count && i < config.Weekly.TopCount; i++)
                Reply(player, "TopLine", i + 1, week[i].Value.Name, Num(week[i].Value.WeekXp), string.IsNullOrEmpty(week[i].Value.House) ? "" : " (" + HouseTint(week[i].Value.House) + ")");
            string id = player.Id.ToString();
            for (int i = 0; i < week.Count; i++) if (week[i].Key == id && i >= config.Weekly.TopCount) { Reply(player, "TopYou", Num(week[i].Value.WeekXp), i + 1); break; }
            for (int i = data.History.Count - 1; i >= 0; i--)
                if (data.History[i].WinnerName != null) { Reply(player, "TopLast", data.History[i].WinnerName, data.History[i].Xp); break; }
        }

        private void ShowHouse(Player player)
        {
            string id = player.Id.ToString();
            CraftRec rec = Rec(id, player.Name, true);
            TrackHouse(id, rec);
            if (string.IsNullOrEmpty(rec.House)) { ReplyError(player, "HouseNone"); return; }
            if (!config.Workshops.Enabled) { ReplyError(player, "PartClosed"); return; }
            HouseWs h = House(rec.House, true);
            int tier = Tier(h);
            Reply(player, "HouseHeader", HouseTint(rec.House), Msg("Tier." + tier, player));
            int next = tier < 3 ? config.Workshops.TierXp[tier] : config.Workshops.TierXp[2];
            Reply(player, "HouseWeek", Num(h.WeekXp), Num(h.LastWeekXp), next);
            if (tier > 0) Reply(player, "HousePerks", config.Workshops.XpBonusPercent[tier - 1], config.Workshops.YieldBonusPoints[tier - 1], config.Workshops.ExtraChancePoints[tier - 1]);
            Reply(player, "HouseTop", config.Workshops.MemberCapXpPerWeek, config.Workshops.MinMemberHours);
            var members = new List<KeyValuePair<string, double>>(h.Members);
            members.Sort(delegate(KeyValuePair<string, double> a, KeyValuePair<string, double> b) { return b.Value.CompareTo(a.Value); });
            for (int i = 0; i < members.Count && i < 5; i++)
            {
                CraftRec m = Rec(members[i].Key, null, false);
                Reply(player, "HouseMember", m != null ? m.Name : members[i].Key, Num(members[i].Value));
            }
            DateTime from = rec.HouseSince.AddHours(config.Workshops.MinMemberHours);
            if (from > Now()) Reply(player, "HouseYou", When(from, player));
        }

        private void CmdAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                {
                    Reply(player, "StatusHeader", data.Players.Count, OpenCount(null), data.Houses.Count);
                    Reply(player, "StatusCounts", data.GatheredUnits, data.CraftsCounted, data.BonusUnitsGiven, data.ExtrasGiven, data.CommissionsFilled, data.CommissionMarks);
                    ResolveGatherItems();
                    Reply(player, "StatusItems", resolvedItems, config.Gathering.Items.Count, unresolvedItems.Count > 0 ? string.Join(", ", unresolvedItems.ToArray()) : Msg("None", player));
                    Reply(player, "StatusCrown", When(data.NextCrowning, player), Here(RealmTreasury, player), Here(RealmRenown, player), Here(RealmHouses, player), Here(RealmSeasons, player), Here(RealmDominion, player));
                    Reply(player, "StatusSubs", Msg(subscribed ? "Yes" : "No", player));
                    return;
                }
                case "watch":
                {
                    if (args.Length < 3) { ReplyError(player, "AdminUsage", "/craft admin watch <player> [off]"); return; }
                    Player t = FindOnline(args[2]);
                    if (t == null) { ReplyError(player, "PlayerNotFound"); return; }
                    if (args.Length > 3 && args[3].ToLowerInvariant() == "off") { watchers.Remove(t.Id); Reply(player, "WatchOff", Clean(t.Name, 40)); return; }
                    watchers[t.Id] = player.Id;
                    Reply(player, "WatchOn", Clean(t.Name, 40));
                    return;
                }
                case "unmapped":
                {
                    if (unmapped.Count == 0) { Reply(player, "UnmappedNone"); return; }
                    Reply(player, "UnmappedHeader", unmapped.Count);
                    for (int i = unmapped.Count - 1; i >= 0; i--) Reply(player, "UnmappedLine", unmapped[i]);
                    return;
                }
                case "items":
                {
                    if (args.Length < 3) { ReplyError(player, "AdminUsage", "/craft admin items <word>"); return; }
                    string word = string.Join(" ", args, 2, args.Length - 2);
                    InvItemBlueprint[] found;
                    try { found = InvBlueprints.GetBlueprintsContaining(word); }
                    catch (Exception) { ReplyError(player, "ItemsNotReady"); return; }
                    if (found == null || found.Length == 0) { Reply(player, "ItemsNone", Clean(word, 30)); return; }
                    Reply(player, "ItemsHeader", Clean(word, 30), found.Length);
                    for (int i = 0; i < found.Length && i < 20; i++) if (found[i] != null) Reply(player, "ItemsLine", found[i].Name);
                    return;
                }
                case "xp":
                case "level":
                {
                    if (args.Length < 5) { ReplyError(player, "AdminUsage", "/craft admin " + sub + " <player> <profession> <" + (sub == "xp" ? "+/-amount" : "level") + ">"); return; }
                    string tid;
                    string tname;
                    if (!FindAnyPlayer(args[2], out tid, out tname)) { ReplyError(player, "PlayerNotFound"); return; }
                    string prof = NormalizeProfession(args[3]);
                    if (prof == null) { ReplyError(player, "UnknownProfession", Clean(args[3], 20), ProfList(player)); return; }
                    long n;
                    if (!long.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { ReplyError(player, "AdminUsage", "/craft admin " + sub + " <player> <profession> <number>"); return; }
                    CraftRec rec = Rec(tid, tname, true);
                    Prof p = ProfOf(rec, prof);
                    if (sub == "xp") p.Xp = Math.Max(0, p.Xp + ClampL(n, -config.General.AdminMaxAdjust, config.General.AdminMaxAdjust));
                    else p.Xp = XpFor(Clamp((int)ClampL(n, 1, config.Levels.MaxLevel), 1, config.Levels.MaxLevel));
                    p.Level = LevelFor(p.Xp);
                    dirty = true;
                    LogToFile("admin", player.Name + " set " + rec.Name + " " + prof + " to " + Num(p.Xp) + " XP", this, true, true);
                    Reply(player, "AdminXp", rec.Name, ProfName(prof, player), Num(p.Xp), p.Level);
                    return;
                }
                case "reset":
                {
                    if (args.Length < 3) { ReplyError(player, "AdminUsage", "/craft admin reset <player> confirm"); return; }
                    string tid, tname;
                    if (!FindAnyPlayer(args[2], out tid, out tname)) { ReplyError(player, "PlayerNotFound"); return; }
                    DateTime asked;
                    string key = player.Id + "|" + tid;
                    if (args.Length < 4 || args[3].ToLowerInvariant() != "confirm" || !resetConfirm.TryGetValue(key, out asked) || (Now() - asked).TotalSeconds > 60)
                    {
                        resetConfirm[key] = Now();
                        Reply(player, "AdminResetAsk", tname);
                        return;
                    }
                    resetConfirm.Remove(key);
                    CraftRec rec = Rec(tid, tname, true);
                    rec.Profs.Clear();
                    rec.WeekXp = 0;
                    rec.BonusOwed.Clear();
                    dirty = true;
                    LogToFile("admin", player.Name + " reset the professions of " + rec.Name, this, true, true);
                    Reply(player, "AdminReset", rec.Name);
                    return;
                }
                case "crown":
                {
                    if (config.Weekly.Enabled) Crown(); else CloseWeek();
                    LogToFile("admin", player.Name + " closed the week early", this, true, true);
                    Reply(player, "AdminCrowned");
                    return;
                }
                case "cancel":
                {
                    if (args.Length < 3) { ReplyError(player, "AdminUsage", "/craft admin cancel <id>"); return; }
                    Commission c = FindCommission(args[2]);
                    if (c == null || c.Status != SOpen) { ReplyError(player, "FillUnknown", Clean(args[2], 10)); return; }
                    c.Status = SCancelled;
                    dirty = true;
                    SaveData();
                    long back = AskLong(RealmTreasury, "ReleaseHold", c.HoldId, HoldSource);
                    LogToFile("admin", player.Name + " cancelled commission #" + c.Id + " (" + back + " marks back to " + c.PosterName + ")", this, true, true);
                    Reply(player, "AdminCancelled", c.Id, back, c.PosterName);
                    return;
                }
            }
            Reply(player, "HelpAdmin");
        }

        private string Here(Plugin p, Player player)
        {
            return Msg(p != null ? "Loaded" : "Absent", player);
        }

        private string ProfList(Player player)
        {
            var names = new List<string>();
            foreach (string p in Professions) names.Add(ProfName(p, player).ToLowerInvariant());
            return string.Join(", ", names.ToArray());
        }

        private string watchMinute;
        private int watchLines;

        private void Watch(Player p, string what)
        {
            if (p == null || watchers.Count == 0) return;
            ulong admin;
            if (!watchers.TryGetValue(p.Id, out admin)) return;
            Player a = OnlineById(admin.ToString());
            if (a == null) { watchers.Remove(p.Id); return; }
            string minute = Now().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            if (watchMinute != minute) { watchMinute = minute; watchLines = 0; }
            if (++watchLines > 40) return;                          // at most 40 lines a minute
            a.SendMessage(Fmt("WatchLine", a, Clean(p.Name, 40), Clean(what, 150)));
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): the game's own windows, opened with the Player extension
        // methods in CodeHatch.Common.PlayerExtensions. Signature read from the 2.0.3867 Assembly-CSharp.dll metadata:
        //   MessageDialogue ShowPopup(this Player, string title, string message, string buttonText = "Ok",
        //       Dialogue.OnSubmit handler = null, bool interupt = false, bool broadcast = true)
        // RealmCrafts opens notices only (no answers are awaited); every window has its chat line too.
        // UNVERIFIED in game: that the windows show on a client.
        private bool popupsClosed;                              // set on Unload

        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.General.UsePopups || popupsClosed) return false;
            if (RealmHerald == null) return true;
            object wanted = Ask(RealmHerald, "PopupsWanted", player.Id.ToString());
            return !(wanted is bool) || (bool)wanted;
        }

        private void Notice(Player player, string title, string message)
        {
            if (!PopupsFor(player)) return;
            try
            {
                player.ShowPopup(PopupText(title), PopupText(message), PopupText(Msg("PopupOk", player)), null, false, true);
            }
            catch (Exception ex)
            {
                PrintWarning("ShowPopup failed (" + ex.Message + "); the chat line stands.");
            }
        }

        // A window is plain text: chat colour tags are stripped.
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

        // RealmTreasury asks this when a player sells on the market: percent off the market fee (0 to 50) when the
        // goods are of a profession the player has risen far enough in. 0 for anyone else, anything else, or when closed.
        private int GetMarketFeeDiscount(string playerId, string item)
        {
            if (loadFailed || data == null || !config.General.Enabled || playerId == null || string.IsNullOrEmpty(item)) return 0;
            CraftRec rec;
            if (!data.Players.TryGetValue(playerId, out rec)) return 0;
            string prof = ProfessionOfItem(item);
            if (prof == null) return 0;
            return DiscountAt(LevelIn(rec, prof));
        }

        private int GetProfessionLevel(string playerId, string profession)
        {
            string prof = NormalizeProfession(profession);
            CraftRec rec;
            if (data == null || prof == null || playerId == null || !data.Players.TryGetValue(playerId, out rec)) return 0;
            return LevelIn(rec, prof);
        }

        // The last Master Crafter's name, or null.
        private string GetMasterCrafter()
        {
            if (data == null) return null;
            for (int i = data.History.Count - 1; i >= 0; i--) if (data.History[i].WinnerName != null) return data.History[i].WinnerName;
            return null;
        }

        private int GetWorkshopTier(string house)
        {
            if (data == null || string.IsNullOrEmpty(house)) return 0;
            return Tier(House(house, false));
        }

        // "smithing:23|mining:5|..." for every profession above level 1 (for pages and boards), or null.
        private string GetCraftSummary(string playerId)
        {
            CraftRec rec;
            if (data == null || playerId == null || !data.Players.TryGetValue(playerId, out rec)) return null;
            var parts = new List<string>();
            foreach (string p in Professions) { int lv = LevelIn(rec, p); if (lv > 1) parts.Add(p + ":" + lv); }
            return string.Join("|", parts.ToArray());
        }

        #endregion

        #region Helpers

        private DateTime Now()
        {
            return clock();
        }

        private static string DayKey(DateTime t)
        {
            return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private static bool IsSteamId(string id)
        {
            ulong u;
            return id != null && id.Length >= 17 && ulong.TryParse(id, out u);
        }

        private static string NormalizeProfession(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.Length < 3) return null;
            switch (t)
            {
                case "wood": case "woodcutter": case "lumberjack": case "logging": return PWood;
                case "mine": case "miner": return PMine;
                case "forage": case "forager": case "gathering": case "herbalism": return PForage;
                case "hunt": case "hunter": case "huntsman": return PHunt;
                case "smith": case "blacksmith": case "blacksmithing": return PSmith;
                case "carpenter": case "woodworking": return PCarpentry;
                case "tailor": case "leatherwork": return PTailor;
                case "cook": case "baking": return PCook;
            }
            foreach (string p in Professions) if (p.StartsWith(t, StringComparison.Ordinal)) return p;
            return null;
        }

        private static bool SameName(string a, string b)
        {
            if (a == null || b == null) return a == null && b == null;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Clean(string s, int max)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c) && c != '[' && c != ']' && c != '{' && c != '}') sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        private static string SafeString(object o)
        {
            try { return o != null ? o.ToString() : null; }
            catch (Exception) { return null; }
        }

        private static string Num(double v)
        {
            return Math.Floor(v + 0.0001).ToString("0", CultureInfo.InvariantCulture);
        }

        private static string Span(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "<1m";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h";
            return (int)t.TotalDays + "d " + t.Hours + "h";
        }

        private string When(DateTime t, Player player)
        {
            TimeSpan d = t - Now();
            return d.TotalSeconds <= 0 ? Msg("Now", player) : Fmt("In", player, Span(d));
        }

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
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer && p.Id == u) return p;
            return null;
        }

        private static Player FindOnline(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Player byId = OnlineById(name);
            if (byId != null) return byId;
            Player found = null;
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer || p.Name == null) continue;
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
                if (p.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { if (found != null) return null; found = p; }
            }
            return found;
        }

        // An online player by name, or anyone on record by Steam id or exact name.
        private bool FindAnyPlayer(string text, out string id, out string name)
        {
            id = null;
            name = null;
            Player p = FindOnline(text);
            if (p != null) { id = p.Id.ToString(); name = p.Name; return true; }
            if (IsSteamId(text)) { CraftRec r = Rec(text, null, false); id = text; name = r != null ? r.Name : text; return true; }
            foreach (KeyValuePair<string, CraftRec> kv in data.Players)
                if (string.Equals(kv.Value.Name, text, StringComparison.OrdinalIgnoreCase)) { id = kv.Key; name = kv.Value.Name; return true; }
            return false;
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
            if (blueprintCache.Count > 2000) blueprintCache.Clear();
            blueprintCache[name] = bp;
            return bp;
        }

        private static ItemCollection PacksOf(Player player)
        {
            if (player == null || player.Entity == null) return null;
            Container inv = player.GetInventory();
            return inv != null ? inv.Contents : null;
        }

        private int CountItems(Player player, InvItemBlueprint bp)
        {
            try
            {
                ItemCollection items = PacksOf(player);
                return items != null && bp != null ? ItemCollection.AutoCount(items, bp) : 0;
            }
            catch (Exception) { return 0; }
        }

        // Takes up to n of an item from the packs and returns what really left them [ASM AutoCount/AutoSplit].
        private int TakeItems(Player player, InvItemBlueprint bp, int n)
        {
            try
            {
                ItemCollection items = PacksOf(player);
                if (items == null || bp == null || n <= 0) return 0;
                int before = ItemCollection.AutoCount(items, bp);
                int want = Math.Min(n, before);
                if (want <= 0) return 0;
                giving = true;
                ItemCollection.AutoSplit(items, bp, want);
                return Math.Max(0, before - ItemCollection.AutoCount(items, bp));
            }
            catch (Exception ex)
            {
                PrintWarning("Taking " + n + " " + bp.Name + " from " + Clean(player.Name, 40) + " failed: " + ex.Message);
                return 0;
            }
            finally { giving = false; }
        }

        // Server-side grant, the same calls as the game's /give (ThronesCommandHandler.Give) [IL]: the Inventory
        // container (PlayerExtensions.GetInventory), stacks capped at ContainerManagement.StackLimit,
        // ItemCollection.AutoMergeAdd. What was added is measured with ItemCollection.AutoCount, never assumed.
        // UNVERIFIED in game: that the client's inventory shows it at once (the game's own /give relies on it).
        private int GiveItems(Player player, InvItemBlueprint bp, int amount)
        {
            try
            {
                ItemCollection items = PacksOf(player);
                if (items == null || bp == null || amount <= 0) return 0;
                ContainerManagement cm = bp.TryGet<ContainerManagement>();
                int limit = cm != null && cm.StackLimit > 0 ? cm.StackLimit : amount;
                int given = 0;
                giving = true;                                       // our own gift: never counted as gathered
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
                PrintWarning("Giving " + amount + " " + (bp != null ? bp.Name : "?") + " to " + Clean(player.Name, 40) + " failed: " + ex.Message);
                return 0;
            }
            finally { giving = false; }
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

        private long AskLong(Plugin plugin, string method, params object[] args)
        {
            object r = Ask(plugin, method, args);
            return r is long ? (long)r : (r is int ? (int)r : 0);
        }

        private string AskString(Plugin plugin, string method, params object[] args)
        {
            return Ask(plugin, method, args) as string;
        }

        private bool Throttled(string key, int seconds)
        {
            DateTime now = Now();
            DateTime until;
            if (throttle.TryGetValue(key, out until) && now < until) return true;
            if (throttle.Count > 5000) throttle.Clear();
            throttle[key] = now.AddSeconds(seconds);
            return false;
        }

        #endregion
    }
}
