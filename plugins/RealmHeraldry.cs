// RealmHeraldry: Realm's houses in the game's own guild system, and the realm's votes.
//
//   Heraldry   Each Realm house bears a pair of colours from art/palette.json: a field (the banner's ground) and a charge
//              (the emblem on it). The six great houses of Ostreval bear their own sigil colours, reserved for them; any
//              other house bears one of the open pairs, chosen by a stable hash of its name until its head picks one with
//              /heraldry colours <pair> (first choice free, then a fee in marks to the crown's treasury through
//              RealmTreasury.ChargeMarks and a cooldown). Every SyncSeconds the plugin finds the game guild each house
//              is bound to and keeps the guild's name and banner colours in step with the house, so the colours show
//              wherever the game draws a guild: its banners, crests, armour tints and name-tag icons. Admins: /heraldry
//              sync | preview | set | reset | banner | status.
//   Council    Each RealmSeasons season the realm elects the council seats named in Council.ElectedSeats (the crown's
//              other seats stay the monarch's to fill). Heads of houses stand (/ballot stand <seat>, with a deposit
//              held by RealmTreasury), the realm votes (/vote <candidate>), and the winners are seated through
//              CrownAndConsequences.SeatElectedCouncillor for TermDays: the monarch cannot dismiss them before then.
//   Referendum The monarch may put a decree or a law to the whole realm (/ballot propose decree|law <id>; /vote yes|no).
//              A decree the realm carries costs no authority when next issued; one it refuses may not be issued for
//              MandateHours (CrownAndConsequences.SetDecreeMandate). RealmLaws has no API to enact or repeal a law, so a
//              law referendum is the realm's word: if the crown then acts against it within MandateHours, the Herald
//              says so and the Chronicle records it.
//   Voters     One vote per Steam account (the game's player id is the Steam id). Against alts: an account must have been
//              seen for MinAccountAgeHours and played MinPlayMinutes (counted here: RealmStats keeps no per-player play
//              time, by design), belong to a Realm house since before the ballot opened and for MinHouseMembershipHours
//              (first sight here, or the Joined date in RealmHouses.json), and must not be under new-player protection
//              (RealmWarden), an outlaw (RealmContracts, RealmLaws) or exiled (RealmLaws). Optional: a number of
//              RealmTravel waystones found. Counts stay hidden until a ballot closes; admins can audit and strike votes.
//   Results    Announced by the Herald and written to the Chronicle (type vote_held). The winner's house earns
//              RealmSeasons points; voters and winners are reported to RealmQuests (custom deeds vote_cast and
//              council_elected) for quest content to use.
//
// Game API ([DEC] = read in the decompiled shipped patched Assembly-CSharp.dll, type.member names only; [ASM] = in its
// metadata, proven by tools/plugin-compile-check/check.sh). No game code is copied here.
//   Guilds   [ASM] SocialAPI.Get<GuildScheme>(): GuildScheme.TryGetGuild(ulong), TryGetGuildByMember(ulong); Guild.BaseID,
//            Guild.Name, Guild.Banner (a public BannerData field), Guild.Members() -> Members.Has(ulong), MemberCount(),
//            EachMember() -> Member.PlayerId.
//   Banner   [ASM] BannerData: CurrentBanner and CurrentPattern (int indices into the client's BannerOptions
//            bannerMaterials and patterns arrays), CurrentColor (the field) and CurrentPaternColor (the charge; the
//            game's own spelling), both UnityEngine.Color, set here by reflection because the compile check has no
//            UnityEngine.Color (as RealmSculptor does for Color32).
//   Update   [DEC] The game's own rename, GuildScheme.SetGuildName, builds new Guild(guild) with the new Name and raises
//            GuildUpdateEvent through EventManager.CallEvent. On the server ServerSupplier.OnGuildUpdate then calls
//            Guild.UpdateGuild (Name and Banner), sends GuildBannerUpdateEvent to the guild's members, BannerUpdateEvent,
//            and GuildNameUpdateEvent when the name changed; the server's own ServerSupplier.OnBannerSend raises the same
//            event for a banner. RealmHeraldry does exactly this with a new BannerData in the copy. On the clients,
//            GuildBannerUpdateEvent drives the banner (GuildBannerUpdate), the crest colours (CrestSupplier ->
//            CrestColorsUpdate), name-tag icons and armour tints (BannerIconSupplier -> BannerSetByPlayerIDEvent ->
//            GuildColoredArmor: CurrentPaternColor is the guild colour, CurrentColor the background). With
//            Heraldry.BroadcastBanner the plugin also sends GuildBannerUpdateEvent(banner, guildId) to every player, so
//            other players' name tags and armour follow at once.
//   Limits   [DEC] The game's rename window allows 28 characters (SocialMenu.ChangeName, ValueMaximum); names are cut to
//            that. The number of banners and patterns lives in the client (BannerOptions), and an index outside them
//            breaks the client's crest and name-tag drawing, so the plugin keeps each guild's own indices unless an
//            admin sets Heraldry.BannerCount / PatternCount (found in game) and an index per house.
//   Crown    [ASM] SocialAPI.Get<KingsScheme>(): HasKing(), IsKing(Player), GetKingID().
//   Hooks    OnPlayerConnected / OnPlayerDisconnected [SRC]. The game has no hook for a guild rename or banner change
//            (docs/oxide-rok-api.md 2.8), so drift is found by polling every SyncSeconds.
//
// Data: oxide/data/RealmHeraldry.json (arms, voters, ballots). If the file exists but cannot be read the plugin pauses
// and NEVER writes it. RealmHouses.json is only ever read (ExistsDatafile first, so it is never created). Marks: a
// deposit is held by RealmTreasury (HoldMarks) and either released or, when forfeit, released and charged to the
// crown's treasury in the same tick; a colour fee is one ChargeMarks. Nothing is minted here.
// Language level: C# 3 syntax, .NET 3.5 API surface. Cross-plugin methods are non-public (Oxide calls NonPublic|Instance).
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmHeraldry.md for the in-game test steps.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using CodeHatch.Common;                            // PlayerExtensions: SendMessage, SendError, ShowPopup [ASM]
using CodeHatch.Engine.Modules.SocialSystem;       // SocialAPI, Member [ASM]
using CodeHatch.Engine.Networking;                 // Player, Server [ASM]
using CodeHatch.Networking.Events;                 // EventManager, GuildBannerUpdateEvent [ASM]
using CodeHatch.Thrones.Banner;                    // BannerData [ASM]
using CodeHatch.Thrones.SocialSystem;              // Guild, GuildScheme, GuildUpdateEvent, KingsScheme [ASM]
using Oxide.Core;                                  // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                          // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmHeraldry", "Realm", "0.1.0")]
    [Description("House colours on the game's own guild banners, council elections and the realm's referendums")]
    public class RealmHeraldry : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmLaws;
        [PluginReference] private Plugin RealmContracts;
        [PluginReference] private Plugin RealmQuests;
        [PluginReference] private Plugin RealmTravel;

        private const string PermAdmin = "realmheraldry.admin";
        private const string DataName = "RealmHeraldry";
        private const string HousesFile = "RealmHouses";
        private const string ChronicleType = "vote_held";
        private const string Source = "heraldry";
        private const int DataFormat = 1;
        private const int GameNameMax = 28;
        private const int MaxOpenBallots = 4;

        private const string KCouncil = "council";
        private const string KReferendum = "referendum";
        private const string SNominating = "nominating";
        private const string SVoting = "voting";
        private const string SClosed = "closed";
        private const string SCancelled = "cancelled";
        private const string QDecree = "decree";
        private const string QProclaim = "proclaim";
        private const string QRepeal = "repeal";
        private const string QKey = "q";

        // The six great houses of Ostreval, in art/palette.json order.
        private static readonly string[] GreatHouses = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private bool popupsClosed;
        private Timer tickTimer;
        private Func<DateTime> clock = delegate { return DateTime.UtcNow; };
        private DateTime lastTick = DateTime.MinValue;
        private DateTime nextSync = DateTime.MinValue;
        private DateTime nextSave = DateTime.MinValue;
        private DateTime lastSyncAt = DateTime.MinValue;
        private string lastSyncSummary = "";
        private readonly HashSet<ulong> onlineLastTick = new HashSet<ulong>();
        private readonly Dictionary<ulong, DateTime> guildApplied = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<string, DateTime> throttles = new Dictionary<string, DateTime>();
        private HousesFileLite housesFile;
        private DateTime housesFileAt = DateTime.MinValue;
        private string housesFileStatus = "not read yet";

        // BannerData colours, reached by reflection (UnityEngine.Color is not in the compile check's stubs).
        private static PropertyInfo fieldColourProp, chargeColourProp;
        private static ConstructorInfo colourCtor;
        private static FieldInfo colourR, colourG, colourB;
        private static string colourBindError;

        #region Config

        private class ArmsPair
        {
            public string Id;
            public string Name;
            public string Field;                // #rrggbb from art/palette.json: the banner's ground (BannerData.CurrentColor)
            public string Charge;               // #rrggbb from art/palette.json: the emblem (BannerData.CurrentPaternColor)
            public string FieldName;
            public string ChargeName;
            public string ReservedFor = "";     // a great house's own colours ("" = open to any house)
        }

        private class GeneralSection
        {
            public float TickSeconds = 60f;
            public int SaveEverySeconds = 300;
            public bool UsePopups = true;
            public int MaxVotersKept = 20000;
            public int ClosedBallotsKept = 30;
        }

        private class HeraldrySection
        {
            public bool Enabled = true;
            public bool SyncGuildNames = true;
            public bool SyncBannerColours = true;
            public bool Enforce = true;                     // put back what a player changes in the game's guild menu
            public string NameFormat = "{0}";               // {0} = the house name; cut to the game's 28 characters
            public int SyncSeconds = 120;
            public int MaxAppliesPerSync = 6;
            public int GuildCooldownSeconds = 60;
            public bool BroadcastBanner = true;
            public bool GuildFallbackByLeader = true;       // without RealmHouses.json: the leader's guild, if all its members are the house's
            public int ChangeCooldownHours = 72;
            public long ChangeFeeMarks = 250;
            public bool FirstChoiceFree = true;
            public bool UniqueChoices = true;
            public bool ReserveGreatHousePairs = true;
            public bool AnnounceNewColours = true;
            public int BannerCount = 0;                     // 0 = never set the banner index (the client's count is UNVERIFIED)
            public int PatternCount = 0;                    // 0 = never set the pattern index
            public List<ArmsPair> Pairs;
        }

        private class CouncilSection
        {
            public bool Enabled = true;
            public List<string> ElectedSeats;
            public bool ElectEachSeason = true;
            public int NominationHours = 48;
            public int VotingHours = 72;
            public int TermDays = 28;
            public int MinTurnout = 3;
            public long CandidateDeposit = 100;
            public int DepositReturnPercent = 10;
            public int CandidateMinHouseMembers = 3;
            public int CandidateMinHouseAgeDays = 3;
            public bool MonarchMayStand = false;
            public int ElectedHousePoints = 5;
            public bool AllowVoteChange = true;
        }

        private class ReferendumSection
        {
            public bool Enabled = true;
            public bool Decrees = true;
            public bool Laws = true;
            public int VotingHours = 24;
            public int CooldownHours = 24;
            public int MinTurnout = 5;
            public int PassPercent = 50;                    // carried when more than this share of the votes say yes
            public int MandateHours = 72;
        }

        private class VoterSection
        {
            public int MinAccountAgeHours = 72;
            public int MinPlayMinutes = 180;
            public bool RequireHouse = true;
            public int MinHouseMembershipHours = 48;
            public bool JoinedBeforeBallot = true;
            public bool NewPlayersMayVote = false;
            public bool OutlawsMayVote = false;
            public bool ExilesMayVote = false;
            public int MinWaystones = 0;                    // RealmTravel waystones found (0 = not asked)
            public bool SeedFromHousesFile = true;
        }

        private class PluginConfig
        {
            public GeneralSection General = new GeneralSection();
            public HeraldrySection Heraldry = new HeraldrySection();
            public CouncilSection Council = new CouncilSection();
            public ReferendumSection Referendum = new ReferendumSection();
            public VoterSection Voters = new VoterSection();
        }

        // The approved pairs: every hex is in art/palette.json (the logic tests check it). The great houses' own field
        // and metal, then open pairs from the brand and enamel colours.
        private static List<ArmsPair> DefaultPairs()
        {
            return new List<ArmsPair>
            {
                P("varrow", "Iron Stag", "#4a2347", "#9aa0a8", "plum", "iron grey", "Varrow"),
                P("ashgrove", "White Oak", "#7a3a1a", "#e8dfc8", "rust", "bone white", "Ashgrove"),
                P("corvane", "Black Raven", "#2c3b42", "#c9ced4", "slate", "silver", "Corvane"),
                P("dunmere", "Drowned Bell", "#5a5a22", "#e0b56a", "marsh olive", "tarnished bronze", "Dunmere"),
                P("halloran", "Ember Hound", "#3a2a1a", "#e27a2c", "smoke brown", "ember orange", "Halloran"),
                P("merrin", "Silver Eel", "#24472d", "#c9ced4", "deep green", "silver", "Merrin"),
                P("blood-gold", "Blood and Gold", "#8b2b22", "#d6a043", "blood red", "gold", ""),
                P("moss-parchment", "Moss and Parchment", "#4d6b3a", "#ecdfbf", "moss green", "parchment", ""),
                P("iron-ember", "Iron and Ember", "#131417", "#f4c96d", "iron black", "bright gold", ""),
                P("verdigris-bone", "Verdigris and Bone", "#245a52", "#e8dfc8", "verdigris", "bone white", ""),
                P("lapis-silver", "Lapis and Silver", "#2c4a7a", "#c9ced4", "lapis blue", "silver", ""),
                P("ink-parchment", "Ink and Parchment", "#2a1c0f", "#e0cfa4", "ink brown", "old parchment", ""),
                P("lapis-gold", "Lapis and Gold", "#2c4a7a", "#d6a043", "lapis blue", "gold", ""),
                P("moss-gold", "Moss and Gold", "#4d6b3a", "#f4c96d", "moss green", "bright gold", ""),
            };
        }

        private static ArmsPair P(string id, string name, string field, string charge, string fieldName, string chargeName, string reserved)
        {
            return new ArmsPair { Id = id, Name = name, Field = field, Charge = charge, FieldName = fieldName, ChargeName = chargeName, ReservedFor = reserved };
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.Heraldry.Pairs = DefaultPairs();
            c.Council.ElectedSeats = new List<string> { "Keeper of Coin", "Marshal" };
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
            if (config.General == null) config.General = new GeneralSection();
            if (config.Heraldry == null) config.Heraldry = new HeraldrySection();
            if (config.Council == null) config.Council = new CouncilSection();
            if (config.Referendum == null) config.Referendum = new ReferendumSection();
            if (config.Voters == null) config.Voters = new VoterSection();

            GeneralSection g = config.General;
            g.TickSeconds = ClampF(g.TickSeconds, 10f, 300f);
            g.SaveEverySeconds = Clamp(g.SaveEverySeconds, 30, 3600);
            g.MaxVotersKept = Clamp(g.MaxVotersKept, 100, 1000000);
            g.ClosedBallotsKept = Clamp(g.ClosedBallotsKept, 1, 500);

            HeraldrySection h = config.Heraldry;
            if (string.IsNullOrEmpty(h.NameFormat) || h.NameFormat.IndexOf("{0}", StringComparison.Ordinal) < 0) h.NameFormat = "{0}";
            h.SyncSeconds = Clamp(h.SyncSeconds, 30, 3600);
            h.MaxAppliesPerSync = Clamp(h.MaxAppliesPerSync, 1, 50);
            h.GuildCooldownSeconds = Clamp(h.GuildCooldownSeconds, 10, 3600);
            h.ChangeCooldownHours = Clamp(h.ChangeCooldownHours, 0, 24 * 90);
            h.ChangeFeeMarks = ClampL(h.ChangeFeeMarks, 0, 1000000);
            h.BannerCount = Clamp(h.BannerCount, 0, 256);
            h.PatternCount = Clamp(h.PatternCount, 0, 256);
            var pairs = new List<ArmsPair>();
            var seen = new HashSet<string>();
            foreach (ArmsPair p in h.Pairs ?? DefaultPairs())
            {
                if (p == null) continue;
                string id = NormalizeId(p.Id);
                int fr, fg, fb, cr, cg, cb;
                if (id == null || seen.Contains(id) || !ParseHex(p.Field, out fr, out fg, out fb) || !ParseHex(p.Charge, out cr, out cg, out cb))
                {
                    PrintWarning("Heraldry.Pairs: the pair '" + p.Id + "' has a bad id or colour and is left out.");
                    continue;
                }
                seen.Add(id);
                p.Id = id;
                p.Name = Clean(p.Name, 40);
                if (p.Name.Length == 0) p.Name = id;
                p.FieldName = Clean(p.FieldName, 30);
                p.ChargeName = Clean(p.ChargeName, 30);
                p.ReservedFor = Clean(p.ReservedFor, 40);
                pairs.Add(p);
            }
            if (OpenPairs(pairs).Count == 0) { PrintWarning("Heraldry.Pairs has no open pair; the defaults are used."); pairs = DefaultPairs(); }
            h.Pairs = pairs;

            CouncilSection c = config.Council;
            if (c.ElectedSeats == null) c.ElectedSeats = new List<string>();
            var seats = new List<string>();
            foreach (string s in c.ElectedSeats) { string t = Clean(s, 40); if (t.Length > 0 && !ContainsIgnoreCase(seats, t)) seats.Add(t); }
            c.ElectedSeats = seats;
            c.NominationHours = Clamp(c.NominationHours, 1, 24 * 14);
            c.VotingHours = Clamp(c.VotingHours, 1, 24 * 14);
            c.TermDays = Clamp(c.TermDays, 1, 180);
            c.MinTurnout = Clamp(c.MinTurnout, 1, 10000);
            c.CandidateDeposit = ClampL(c.CandidateDeposit, 0, 100000);
            c.DepositReturnPercent = Clamp(c.DepositReturnPercent, 0, 100);
            c.CandidateMinHouseMembers = Clamp(c.CandidateMinHouseMembers, 1, 1000);
            c.CandidateMinHouseAgeDays = Clamp(c.CandidateMinHouseAgeDays, 0, 365);
            c.ElectedHousePoints = Clamp(c.ElectedHousePoints, 0, 1000);

            ReferendumSection r = config.Referendum;
            r.VotingHours = Clamp(r.VotingHours, 1, 24 * 14);
            r.CooldownHours = Clamp(r.CooldownHours, 0, 24 * 30);
            r.MinTurnout = Clamp(r.MinTurnout, 1, 10000);
            r.PassPercent = Clamp(r.PassPercent, 1, 99);
            r.MandateHours = Clamp(r.MandateHours, 1, 720);

            VoterSection v = config.Voters;
            v.MinAccountAgeHours = Clamp(v.MinAccountAgeHours, 0, 24 * 90);
            v.MinPlayMinutes = Clamp(v.MinPlayMinutes, 0, 60 * 500);
            v.MinHouseMembershipHours = Clamp(v.MinHouseMembershipHours, 0, 24 * 90);
            v.MinWaystones = Clamp(v.MinWaystones, 0, 200);
        }

        #endregion

        #region Data

        private class HouseArms
        {
            public string House;
            public string Pair = "";                 // "" = the default pair
            public string ChosenBy = "";
            public DateTime ChosenAt;
            public int BannerIndex = -1;             // -1 = keep the guild's own
            public int PatternIndex = -1;
            public ulong GuildId;                    // the guild last seen bound (information)
            public string AppliedSig = "";           // what was last applied (name|field|charge|banner|pattern)
            public DateTime LastApplied;
            public int Applies;
            public int Drifts;
        }

        private class VoterRec
        {
            public string Name = "";
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public long PlaySeconds;
            public string House;
            public DateTime HouseSince;
        }

        private class Candidate
        {
            public string Seat;
            public string Id;
            public string Name;
            public string House;
            public DateTime StoodAt;
            public long Deposit;
            public string HoldId = "";
        }

        private class Ballot
        {
            public int Id;
            public string Kind;
            public string Status;
            public int Season;
            public DateTime OpenedAt;
            public DateTime VotingAt;
            public DateTime ClosesAt;
            public DateTime ClosedAt;
            public string OpenedBy = "";
            public List<string> Seats = new List<string>();
            public List<Candidate> Candidates = new List<Candidate>();
            public Dictionary<string, Dictionary<string, string>> Votes = new Dictionary<string, Dictionary<string, string>>();
            public List<string> Struck = new List<string>();
            public string Question = "";             // decree | proclaim | repeal
            public string Target = "";               // decree or law id
            public string TargetName = "";
            public string ProposedById = "";
            public List<string> Results = new List<string>();
        }

        private class LawWatch
        {
            public int BallotId;
            public string LawId;
            public string LawName;
            public bool MustBeInForce;
            public DateTime Until;
            public bool Reported;
        }

        private class StoredData
        {
            public int Format = DataFormat;
            public Dictionary<string, HouseArms> Arms = new Dictionary<string, HouseArms>();
            public Dictionary<string, VoterRec> Voters = new Dictionary<string, VoterRec>();
            public List<Ballot> Ballots = new List<Ballot>();
            public List<LawWatch> LawWatches = new List<LawWatch>();
            public int NextBallotId = 1;
            public int LastElectionSeason;
            public bool SeasonBaselined;
            public DateTime LastReferendumAt;
        }

        // The parts of RealmHouses.json this plugin reads (never writes).
        private class HouseMemberLite
        {
            public string Id;
            public DateTime Joined;
        }

        private class HouseLite
        {
            public string Name;
            public ulong GuildId;
            public List<HouseMemberLite> Members;
        }

        private class HousesFileLite
        {
            public List<HouseLite> Houses;
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
                PrintError("Could not read oxide/data/" + DataName + ".json (" + ex.Message + "). RealmHeraldry is paused and will NOT "
                    + "write the file. Fix it or move it away, then reload.");
                return;
            }
            if (data == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or truncated. RealmHeraldry is paused and will NOT write it. "
                    + "Fix it or move it away, then reload.");
                return;
            }
            if (data.Format > DataFormat)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json was written by a newer RealmHeraldry (format " + data.Format + "). Paused; nothing is written.");
                data = null;
                return;
            }
            NormalizeData();
        }

        private void NormalizeData()
        {
            if (data.Arms == null) data.Arms = new Dictionary<string, HouseArms>();
            if (data.Voters == null) data.Voters = new Dictionary<string, VoterRec>();
            if (data.Ballots == null) data.Ballots = new List<Ballot>();
            if (data.LawWatches == null) data.LawWatches = new List<LawWatch>();
            var arms = new Dictionary<string, HouseArms>();
            foreach (KeyValuePair<string, HouseArms> kv in data.Arms)
            {
                HouseArms a = kv.Value;
                if (a == null || string.IsNullOrEmpty(kv.Key)) continue;
                if (string.IsNullOrEmpty(a.House)) a.House = kv.Key;
                if (a.Pair == null) a.Pair = "";
                if (a.ChosenBy == null) a.ChosenBy = "";
                if (a.AppliedSig == null) a.AppliedSig = "";
                arms[HouseKey(a.House)] = a;
            }
            data.Arms = arms;
            var voters = new Dictionary<string, VoterRec>();
            foreach (KeyValuePair<string, VoterRec> kv in data.Voters)
            {
                ulong u;
                if (kv.Value == null || !ulong.TryParse(kv.Key, out u)) continue;
                if (kv.Value.Name == null) kv.Value.Name = kv.Key;
                if (kv.Value.PlaySeconds < 0) kv.Value.PlaySeconds = 0;
                voters[kv.Key] = kv.Value;
            }
            data.Voters = voters;
            data.Ballots.RemoveAll(delegate(Ballot b) { return b == null || (b.Kind != KCouncil && b.Kind != KReferendum); });
            foreach (Ballot b in data.Ballots)
            {
                if (b.Seats == null) b.Seats = new List<string>();
                if (b.Candidates == null) b.Candidates = new List<Candidate>();
                b.Candidates.RemoveAll(delegate(Candidate c) { return c == null || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Seat); });
                foreach (Candidate c in b.Candidates) if (c.HoldId == null) c.HoldId = "";
                if (b.Votes == null) b.Votes = new Dictionary<string, Dictionary<string, string>>();
                var bad = new List<string>();
                foreach (KeyValuePair<string, Dictionary<string, string>> kv in b.Votes) if (kv.Value == null) bad.Add(kv.Key);
                foreach (string k in bad) b.Votes.Remove(k);
                if (b.Struck == null) b.Struck = new List<string>();
                if (b.Results == null) b.Results = new List<string>();
                if (b.Question == null) b.Question = "";
                if (b.Target == null) b.Target = "";
                if (b.TargetName == null) b.TargetName = "";
                if (b.ProposedById == null) b.ProposedById = "";
                if (b.OpenedBy == null) b.OpenedBy = "";
                if (b.Status != SNominating && b.Status != SVoting && b.Status != SClosed && b.Status != SCancelled) b.Status = SCancelled;
                if (b.Id >= data.NextBallotId) data.NextBallotId = b.Id + 1;
            }
            data.LawWatches.RemoveAll(delegate(LawWatch w) { return w == null || string.IsNullOrEmpty(w.LawId); });
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

        private static string Cmd(string text)
        {
            return "[F4C96D]" + text + "[FFFFFF]";
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Heraldry" },
                { "SpeakerCouncil", "Council" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Paused", "RealmHeraldry is paused: its data file could not be read. An admin must fix oxide/data/RealmHeraldry.json." },
                { "NoPermission", "Only the realm's staff may do that." },
                { "Off", "The heralds are not at work on this server." },

                // Heraldry
                { "HelpArms1", "Arms: [F4C96D]/heraldry[FFFFFF] your house's colours | [F4C96D]/heraldry house[FFFFFF] <house> | [F4C96D]/heraldry colours[FFFFFF] the pairs a house may bear" },
                { "HelpArms2", "  The head of a house chooses with [F4C96D]/heraldry colours[FFFFFF] <pair>. The colours show on your guild's banner, crest, armour and name tags." },
                { "HelpArmsAdmin", "  Staff: [F4C96D]/heraldry sync[FFFFFF] | preview [house] | set <house> <pair> | reset <house> | banner <house> <banner|keep> <pattern|keep> | status" },
                { "NoHouse", "You belong to no house. Found or join one with [F4C96D]/house[FFFFFF]." },
                { "HouseUnknown", "No house of that name: {0}." },
                { "HousesAway", "The roll of houses (RealmHouses) is not loaded." },
                { "ArmsLine", "House {0} bears {1}: {2} on a field of {3}{4}." },
                { "ArmsDefault", " (given by the heralds until its head chooses)" },
                { "ArmsGuild", "  In the game it flies as the guild '{0}': {1}." },
                { "ArmsGuildNone", "  It is bound to no game guild yet, so its colours show only here. A house leader binds one with [F4C96D]/house link[FFFFFF]." },
                { "ArmsInStep", "in step" },
                { "ArmsPending", "the heralds will bring it in step within {0}" },
                { "PairsHeader", "Colours a house may bear ({0}):" },
                { "PairLine", "  {0} - {1}: {2} on {3}{4}" },
                { "PairYours", " (yours)" },
                { "PairReserved", " (the colours of House {0})" },
                { "PairTaken", " (borne by House {0})" },
                { "PairsHint", "  The head of a house chooses with [F4C96D]/heraldry colours[FFFFFF] <pair>{0}." },
                { "PairsFee", " for {0} marks to the crown's treasury, once every {1} hours" },
                { "PairsFirstFree", " (the first choice is free)" },
                { "PairUnknown", "No pair of colours called {0}. See [F4C96D]/heraldry colours[FFFFFF]." },
                { "NotLeader", "Only the head of House {0} may choose its colours." },
                { "PairIsReserved", "Those are the colours of House {0}; no other house may bear them." },
                { "PairIsTaken", "House {0} already bears those colours." },
                { "PairAlready", "House {0} already bears {1}." },
                { "PairCooldown", "House {0} chose its colours lately; it may change them again in {1}." },
                { "TreasuryAway", "The crown's treasury (RealmTreasury) is not open, so the fee cannot be paid." },
                { "CannotPay", "The new colours cost {0} marks and your purse cannot pay them." },
                { "PairChosen", "House {0} now bears {1}: {2} on a field of {3}. The heralds will raise them on your guild's banner." },
                { "HeraldNewColours", "House {0} raises new colours: {1}." },

                // Heraldry admin
                { "SyncDone", "Heraldry sync: {0} brought in step, {1} already in step, {2} with no game guild, {3} waiting their turn." },
                { "SyncOff", "Heraldry is switched off in the config (Heraldry.Enabled)." },
                { "PreviewHeader", "What the heralds would change ({0} houses):" },
                { "PreviewLine", "  {0} -> guild '{1}': {2}" },
                { "PreviewInStep", "in step" },
                { "PreviewName", "name '{0}' to '{1}'" },
                { "PreviewColours", "colours {0}/{1} to {2}/{3}" },
                { "PreviewIndex", "banner {0} pattern {1} to banner {2} pattern {3}" },
                { "PreviewUnbound", "  {0}: no game guild ({1})" },
                { "PreviewError", "The game's banner colours cannot be reached: {0}" },
                { "AdminSet", "House {0} now bears {1}." },
                { "AdminReset", "House {0} bears the heralds' choice again ({1})." },
                { "AdminBannerOff", "Banner and pattern indices are off: set Heraldry.BannerCount and PatternCount to the numbers found in game first." },
                { "AdminBannerBad", "Banner must be 0 to {0} or keep; pattern 0 to {1} or keep." },
                { "AdminBannerSet", "House {0}: banner {1}, pattern {2}." },
                { "StatusHeader", "RealmHeraldry status:" },
                { "StatusSync", "  Heraldry {0}: names {1}, colours {2}, enforce {3}; last sync {4}: {5}" },
                { "StatusFile", "  RealmHouses.json: {0}. Colour binding: {1}." },
                { "StatusVotes", "  Council elections {0}, referendums {1}; {2} voters known, {3} ballot(s) open." },
                { "On", "on" },
                { "OffWord", "off" },
                { "Never", "never" },
                { "Ok", "ok" },

                // Ballots
                { "HelpBallot1", "Votes: [F4C96D]/ballot[FFFFFF] what the realm votes on | [F4C96D]/ballot[FFFFFF] <number> | results | history | me (can I vote?)" },
                { "HelpBallot2", "  Heads of houses: [F4C96D]/ballot stand[FFFFFF] <seat> | withdraw. The monarch: [F4C96D]/ballot propose[FFFFFF] decree <id> | law <id>" },
                { "HelpBallotAdmin", "  Staff: [F4C96D]/ballot admin[FFFFFF] open | advance <n> | cancel <n> | strike <n> <player> | audit <n> | voter <player>" },
                { "HelpVote", "Vote with [F4C96D]/vote[FFFFFF] <candidate or house> in a council election, or [F4C96D]/vote[FFFFFF] yes | no on the open question ([F4C96D]/vote[FFFFFF] <number> yes | no if there are more)." },
                { "NoBallots", "Nothing is put to the realm's vote now." },
                { "NextElection", "  The council is elected at the start of each season ({0})." },
                { "BallotsHeader", "Put to the realm's vote:" },
                { "BallotCouncilLine", "  #{0} Council election, {1}: {2}" },
                { "BallotReferendumLine", "  #{0} {1}: voting ends in {2}" },
                { "PhaseNominating", "heads of houses stand for {0} until {1} from now" },
                { "PhaseVoting", "voting ends in {0}" },
                { "BallotUnknown", "No ballot #{0}." },
                { "BallotHeaderCouncil", "Council election #{0} ({1}): {2}" },
                { "BallotSeat", "  {0}: {1}" },
                { "BallotSeatNone", "no one stands yet" },
                { "BallotCandidate", "{0} of House {1}" },
                { "BallotTurnout", "  {0} have voted. Counts are kept sealed until the close." },
                { "BallotHeaderReferendum", "Referendum #{0}: {1} ({2})" },
                { "BallotYourVote", "  Your vote: {0}" },
                { "BallotResultLine", "  {0}" },
                { "StatusNominating", "standing" },
                { "StatusVoting", "voting" },
                { "StatusClosed", "closed" },
                { "StatusCancelled", "cancelled" },
                { "QuestionDecree", "Shall the crown issue the decree {0}?" },
                { "QuestionProclaim", "Shall the law {0} be proclaimed?" },
                { "QuestionRepeal", "Shall the law {0} be repealed?" },
                { "ResultsNone", "No ballot has closed yet." },
                { "HistoryHeader", "The realm's last votes:" },
                { "HistoryLine", "  #{0} {1}, {2}: {3}" },

                // Standing
                { "StandUsage", "Stand with [F4C96D]/ballot stand[FFFFFF] <seat>. Seats elected now: {0}" },
                { "NoElection", "No council election is taking candidates now." },
                { "SeatNotElected", "{0} is not a seat the realm elects. Seats: {1}" },
                { "StandNotLeader", "Only the head of a house may stand for the council." },
                { "StandSmallHouse", "House {0} has {1} members; a candidate's house needs at least {2}." },
                { "StandYoungHouse", "House {0} must stand {1} days before its head may seek a seat." },
                { "StandMonarch", "The monarch does not stand for the council." },
                { "StandAlready", "You already stand for {0}." },
                { "StandHouseAlready", "House {0} already has a candidate: {1}." },
                { "StandNotVoter", "A candidate must be able to vote: {0}" },
                { "StandDepositFailed", "Standing needs a deposit of {0} marks, held until the count; your purse cannot pay it." },
                { "Stood", "You stand for {0}{1}. Voting opens in {2}." },
                { "StoodDeposit", " ({0} marks are held, returned if you win or take {1}% of the votes)" },
                { "HeraldStood", "{0}, head of House {1}, stands for {2}." },
                { "WithdrawNone", "You do not stand in any election." },
                { "Withdrawn", "You no longer stand for {0}.{1}" },
                { "WithdrawRefund", " Your deposit of {0} marks is returned." },
                { "WithdrawKept", " Voting had begun, so your deposit is forfeit." },
                { "HeraldWithdrawn", "{0} withdraws from the election for {1}." },

                // Proposing
                { "ProposeUsage", "Ask the realm with [F4C96D]/ballot propose[FFFFFF] decree <id> (see [F4C96D]/decree[FFFFFF]) or law <id> (see [F4C96D]/law[FFFFFF] catalogue)" },
                { "ProposeNotMonarch", "Only the monarch may put a question to the realm." },
                { "ProposeOff", "Referendums on {0} are switched off on this server." },
                { "ProposeOpen", "The realm is already voting on a question (#{0}). One at a time." },
                { "ProposeCooldown", "The realm may be asked again in {0}." },
                { "CrownAway", "The crown (CrownAndConsequences) is not loaded." },
                { "LawsAway", "The laws (RealmLaws) are not loaded." },
                { "DecreeUnknown", "No decree called {0}. See [F4C96D]/decree[FFFFFF]." },
                { "LawBadId", "A law is named by its id, such as kings_peace (see [F4C96D]/law[FFFFFF] catalogue)." },
                { "Proposed", "Referendum #{0} is put to the realm: {1} Voting ends in {2}." },
                { "HeraldProposed", "The crown asks the realm: {0} Vote with [F4C96D]/vote[FFFFFF] yes or [F4C96D]/vote[FFFFFF] no before {1} from now." },

                // Voting
                { "VoteNothing", "Nothing is open for voting now. See [F4C96D]/ballot[FFFFFF]." },
                { "VoteWhich", "More than one question is open: [F4C96D]/vote[FFFFFF] <number> yes | no ({0})." },
                { "VoteCandidateUnknown", "No candidate called {0} in the open election. See [F4C96D]/ballot[FFFFFF]." },
                { "VoteCandidateAmbiguous", "More than one candidate matches {0}: {1}." },
                { "VoteNotYet", "Ballot #{0} is still taking candidates; voting opens in {1}." },
                { "VoteNoChange", "You have already voted on {0}, and votes may not be changed." },
                { "Voted", "Your vote for {0} as {1} is cast. You may change it until the close." },
                { "VotedFinal", "Your vote for {0} as {1} is cast." },
                { "VotedQuestion", "Your vote, {0}, on referendum #{1} is cast." },
                { "VoteSame", "That is already your vote." },
                { "Yes", "yes" },
                { "No", "no" },

                // Eligibility
                { "MeHeader", "Your voice in the realm:" },
                { "MeOk", "  You may vote. One vote per account, per seat or question." },
                { "MeNo", "  You may not vote yet: {0}" },
                { "MePlay", "  Play counted here: {0} of {1} minutes; first seen {2} ago." },
                { "MeHouse", "  House: {0}, a member for {1}." },
                { "WhyUnknown", "the heralds have not seen you play yet" },
                { "WhyNewAccount", "your account must be seen in the realm for {0} hours first" },
                { "WhyPlay", "you need {0} minutes of play here ({1} so far)" },
                { "WhyNoHouse", "only members of a house vote" },
                { "WhyHouseYoung", "you must be in your house for {0} hours first" },
                { "WhyJoinedLate", "you joined your house after this ballot opened" },
                { "WhyProtected", "you are still under new-player protection" },
                { "WhyOutlaw", "outlaws have no voice in the realm" },
                { "WhyExiled", "exiles have no voice in the realm" },
                { "WhyWaystones", "find {0} waystones of the realm first ([F4C96D]/travel[FFFFFF])" },
                { "WhyStruck", "your vote in this ballot was struck by the realm's staff" },
                { "WhyNotHere", "you must be in the game" },

                // Results
                { "HeraldElectionCalled", "The council election of {0} is called: heads of houses may stand for {1} with [F4C96D]/ballot stand[FFFFFF] <seat> for the next {2}." },
                { "HeraldVotingOpen", "The realm votes for its council: {0}. Cast your vote with [F4C96D]/vote[FFFFFF] <candidate> within {1}." },
                { "HeraldNoCandidates", "No head of a house stood for the council; the seats stay in the crown's gift." },
                { "HeraldElected", "{0} of House {1} is elected {2} ({3} of {4} votes)." },
                { "HeraldUnopposed", "{0} of House {1} is elected {2} unopposed." },
                { "HeraldSeatEmpty", "No one stood for {0}; the seat stays in the crown's gift." },
                { "HeraldSeatQuorum", "Too few voices for {0} ({1} of {2} needed); the seat stays as it is." },
                { "HeraldSeatFailed", "{0} won {1}, but the crown's council could not seat them (an admin must look)." },
                { "HeraldCarried", "The realm answers yes ({0} to {1}): {2}" },
                { "HeraldRefused", "The realm answers no ({0} to {1}): {2}" },
                { "HeraldNoQuorum", "Too few voices to answer ({0} of {1} needed): {2}" },
                { "HeraldMandatePassed", "The decree {0} may now be issued without cost to the crown's authority." },
                { "HeraldMandateRefused", "The decree {0} may not be issued for {1} hours." },
                { "HeraldLawAdvice", "The realm's word on {0} stands for {1} hours: the crown acts with [F4C96D]/law[FFFFFF] proclaim or repeal." },
                { "HeraldDefied", "The crown defies the realm's vote: {0} {1} though the realm said otherwise." },
                { "DefiedNowInForce", "is proclaimed" },
                { "DefiedNoLongerInForce", "is repealed" },
                { "HeraldCancelled", "Ballot #{0} is called off by the realm's staff." },
                { "PopupVoteTitle", "The realm votes" },
                { "PopupVoteCouncil", "The council election is open: {0}.\nVote with [F4C96D]/vote[FFFFFF] <candidate> within {1}." },
                { "PopupVoteReferendum", "{0}\nVote with [F4C96D]/vote[FFFFFF] yes or [F4C96D]/vote[FFFFFF] no within {1}." },
                { "PopupOk", "Ok" },
                { "DepositBack", "Your deposit of {0} marks for {1} is returned." },
                { "DepositLost", "You took too few votes for {0}; your deposit of {1} marks goes to the crown's treasury." },

                // Ballot admin
                { "AdminOpened", "Council election #{0} is open: standing until {1} from now, then {2} hours of voting." },
                { "AdminNoSeats", "No elected seat matches the crown's council (Council.ElectedSeats: {0}; the crown's seats: {1})." },
                { "AdminElectionOpen", "A council election is already open (#{0})." },
                { "AdminCannotOpen", "The election cannot open: {0}" },
                { "ReferendumsOff", "Referendums are switched off on this server." },
                { "AdminCouncilOff", "Council elections are switched off (Council.Enabled)." },
                { "AdminTooMany", "Too many ballots are open." },
                { "AdminAdvanced", "Ballot #{0} moves on: {1}." },
                { "AdminCancelled", "Ballot #{0} is cancelled; {1} deposit(s) returned." },
                { "AdminNotOpen", "Ballot #{0} is not open." },
                { "AdminStruck", "{0}'s vote in ballot #{1} is struck and they may not vote in it again." },
                { "AdminStrikeNone", "{0} has not voted in ballot #{1}; they may not vote in it now." },
                { "PlayerUnknown", "No player called {0} is known to the heralds." },
                { "AuditHeader", "Ballot #{0}: {1} votes ({2} struck)." },
                { "AuditHouse", "  House {0}: {1} voters" },
                { "AuditNoHouse", "  No house: {0} voters" },
                { "AuditYoung", "  Least seen voters: {0}" },
                { "AuditVoter", "{0} ({1} min, {2} h in house)" },
                { "VoterHeader", "{0}: first seen {1} ago, {2} minutes of play, house {3} for {4}." },
                { "VoterOk", "  May vote now." },
                { "VoterNo", "  May not vote now: {0}" },
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

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "PairChosen", "SyncDone", "AdminSet", "AdminReset", "AdminBannerSet", "Stood", "Withdrawn", "Proposed", "Voted", "VotedFinal",
            "VotedQuestion", "AdminOpened", "AdminAdvanced", "AdminCancelled", "AdminStruck", "DepositBack", "MeOk", "VoterOk"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "ArmsPending", "VoteNotYet", "VoteSame", "DepositLost", "AdminStrikeNone", "MeNo", "VoterNo"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
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

        private void Say(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendMessage(Styled(Msg("SpeakerCouncil", player), ToneOf(key), Fmt(key, player, args)));
        }

        private void SayError(Player player, string key, params object[] args)
        {
            if (player == null) return;
            player.SendError(Styled(Msg("SpeakerCouncil", player), ChatError, Fmt(key, player, args)));
        }

        private void Herald(string key, params object[] args)
        {
            Server.BroadcastMessage(Msg("Herald", null) + Fmt(key, null, args));        // single-string overload [ASM]
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintError("oxide/config/RealmHeraldry.json could not be read (" + ex.Message + "); using the defaults for this run.");
                config = null;
            }
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            if (!loadFailed && !Interface.Oxide.DataFileSystem.ExistsDatafile(DataName)) SaveData();
        }

        private void OnServerInitialized()
        {
            if (loadFailed) return;
            popupsClosed = false;
            // Re-sent on hot reload (docs/oxide-rok-api.md 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.General.TickSeconds, SafeTick);
            BindColours();
            if (colourBindError != null) PrintWarning("Banner colours unavailable: " + colourBindError + ". Guild names are still kept in step.");
            lastTick = Now();
            onlineLastTick.Clear();
            foreach (Player p in OnlinePlayers()) { Observe(p, 0); onlineLastTick.Add(p.Id); }
            nextSync = Now().AddSeconds(15);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            popupsClosed = true;
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            SaveData();
        }

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            Observe(player, 0);
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            VoterRec r = Voter(player.Id.ToString(), player.Name, false);
            if (r != null) { r.LastSeen = Now(); dirty = true; }
            onlineLastTick.Remove(player.Id);
        }

        #endregion

        #region Tick

        private void SafeTick()
        {
            if (loadFailed || data == null) return;
            try { Tick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = Now();
            long step = (long)Math.Max(0, Math.Min((now - lastTick).TotalSeconds, config.General.TickSeconds * 2.0));
            lastTick = now;
            var online = new HashSet<ulong>();
            foreach (Player p in OnlinePlayers())
            {
                online.Add(p.Id);
                Observe(p, onlineLastTick.Contains(p.Id) ? step : 0);
            }
            onlineLastTick.Clear();
            foreach (ulong id in online) onlineLastTick.Add(id);

            ScheduleSeasonElection(now);
            AdvanceBallots(now);
            CheckLawWatches(now);
            if (config.Heraldry.Enabled && now >= nextSync)
            {
                nextSync = now.AddSeconds(config.Heraldry.SyncSeconds);
                int a, s, u, w;
                SyncAll(false, null, null, out a, out s, out u, out w);
            }
            if (dirty && now >= nextSave) { PruneVoters(); SaveData(); }
        }

        // Counts play time and follows each player's house (what the voter rules read).
        private void Observe(Player p, long seconds)
        {
            string id = p.Id.ToString();
            VoterRec r = Voter(id, p.Name, true);
            DateTime now = Now();
            r.LastSeen = now;
            if (seconds > 0) r.PlaySeconds += seconds;
            string house = HouseOf(id);
            if (!SameHouse(house, r.House))
            {
                r.House = house;
                DateTime since = now;
                if (house != null)
                {
                    DateTime joined;
                    if (JoinedFromFile(house, id, out joined) && joined < now) since = joined;
                }
                r.HouseSince = house != null ? since : DateTime.MinValue;
                if (house != null && since < r.FirstSeen) r.FirstSeen = since;
            }
            dirty = true;
        }

        private VoterRec Voter(string id, string name, bool create)
        {
            VoterRec r;
            if (data.Voters.TryGetValue(id, out r) && r != null)
            {
                if (name != null) { string n = Clean(name, 40); if (n.Length > 0) r.Name = n; }
                return r;
            }
            if (!create) return null;
            r = new VoterRec { Name = Clean(name ?? id, 40), FirstSeen = Now(), LastSeen = Now() };
            data.Voters[id] = r;
            dirty = true;
            return r;
        }

        private void PruneVoters()
        {
            int over = data.Voters.Count - config.General.MaxVotersKept;
            if (over <= 0) return;
            var keep = new HashSet<string>();
            foreach (Ballot b in OpenBallots()) foreach (string v in b.Votes.Keys) keep.Add(v);
            var list = new List<KeyValuePair<string, VoterRec>>(data.Voters);
            list.Sort(delegate(KeyValuePair<string, VoterRec> x, KeyValuePair<string, VoterRec> y) { return x.Value.LastSeen.CompareTo(y.Value.LastSeen); });
            foreach (KeyValuePair<string, VoterRec> kv in list)
            {
                if (over <= 0) break;
                if (keep.Contains(kv.Key)) continue;
                data.Voters.Remove(kv.Key);
                over--;
            }
        }

        #endregion

        #region Heraldry

        private static List<ArmsPair> OpenPairs(List<ArmsPair> pairs)
        {
            var list = new List<ArmsPair>();
            foreach (ArmsPair p in pairs) if (string.IsNullOrEmpty(p.ReservedFor)) list.Add(p);
            return list;
        }

        private ArmsPair FindPair(string id)
        {
            string n = NormalizeId(id);
            if (n == null) return null;
            foreach (ArmsPair p in config.Heraldry.Pairs) if (p.Id == n) return p;
            foreach (ArmsPair p in config.Heraldry.Pairs) if (string.Equals(p.Name, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private static bool IsGreatHouse(string house)
        {
            return house != null && Array.IndexOf(GreatHouses, house.Trim().ToLowerInvariant()) >= 0;
        }

        // The pair a house bears: its explicit choice if still valid, else the great house's own, else a stable hash of
        // its name into the open pairs.
        private ArmsPair PairOf(string house, out bool chosen)
        {
            chosen = false;
            HouseArms a;
            if (data.Arms.TryGetValue(HouseKey(house), out a) && a.Pair.Length > 0)
            {
                ArmsPair p = FindPair(a.Pair);
                if (p != null && MayBear(house, p)) { chosen = true; return p; }
            }
            foreach (ArmsPair p in config.Heraldry.Pairs)
                if (!string.IsNullOrEmpty(p.ReservedFor) && string.Equals(p.ReservedFor, house, StringComparison.OrdinalIgnoreCase)) return p;
            List<ArmsPair> open = OpenPairs(config.Heraldry.Pairs);
            uint h = 2166136261;
            foreach (char c in HouseKey(house)) { h ^= c; h *= 16777619; }
            return open[(int)(h % (uint)open.Count)];
        }

        private bool MayBear(string house, ArmsPair p)
        {
            if (string.IsNullOrEmpty(p.ReservedFor) || !config.Heraldry.ReserveGreatHousePairs) return true;
            return string.Equals(p.ReservedFor, house, StringComparison.OrdinalIgnoreCase);
        }

        // Another existing house that has explicitly chosen this pair, or null.
        private string HolderOf(ArmsPair p, string exceptHouse)
        {
            List<string> houses = HouseNames();
            foreach (HouseArms a in data.Arms.Values)
            {
                if (a.Pair != p.Id || string.Equals(a.House, exceptHouse, StringComparison.OrdinalIgnoreCase)) continue;
                if (houses != null && !ContainsIgnoreCase(houses, a.House)) continue;      // a fallen house holds nothing
                return a.House;
            }
            return null;
        }

        private HouseArms ArmsRec(string house, bool create)
        {
            HouseArms a;
            string key = HouseKey(house);
            if (data.Arms.TryGetValue(key, out a)) { a.House = house; return a; }
            if (!create) return null;
            a = new HouseArms { House = house };
            data.Arms[key] = a;
            return a;
        }

        private string GameName(string house)
        {
            string n = house;
            try { n = string.Format(config.Heraldry.NameFormat, house); }
            catch (FormatException) { n = house; }
            if (n.Length > GameNameMax) n = house;
            if (n.Length > GameNameMax) n = n.Substring(0, GameNameMax).TrimEnd();
            return n;
        }

        private class Want
        {
            public string House;
            public Guild Guild;
            public string Unbound;
            public string Name;
            public string Field;
            public string Charge;
            public int Banner;
            public int Pattern;
            public string CurName;
            public string CurField;
            public string CurCharge;
            public int CurBanner;
            public int CurPattern;
            public bool NameDiffers, ColoursDiffer, IndexDiffers;
            public string Sig;
        }

        private Want Wanted(string house, Dictionary<ulong, string> claimed)
        {
            var w = new Want { House = house };
            string why;
            Guild g = GuildFor(house, out why);
            if (g == null) { w.Unbound = why; return w; }
            string other;
            if (claimed.TryGetValue(g.BaseID, out other)) { w.Unbound = "its guild is also bound to House " + other; return w; }
            claimed[g.BaseID] = house;
            w.Guild = g;
            bool chosen;
            ArmsPair p = PairOf(house, out chosen);
            HouseArms a = ArmsRec(house, false);
            w.Name = GameName(house);
            w.Field = p.Field.ToLowerInvariant();
            w.Charge = p.Charge.ToLowerInvariant();
            BannerData cur = g.Banner;
            w.CurName = g.Name ?? "";
            w.CurBanner = cur != null ? cur.CurrentBanner : 0;
            w.CurPattern = cur != null ? cur.CurrentPattern : 0;
            w.CurField = ReadColour(cur, true);
            w.CurCharge = ReadColour(cur, false);
            w.Banner = a != null && a.BannerIndex >= 0 && a.BannerIndex < config.Heraldry.BannerCount ? a.BannerIndex : w.CurBanner;
            w.Pattern = a != null && a.PatternIndex >= 0 && a.PatternIndex < config.Heraldry.PatternCount ? a.PatternIndex : w.CurPattern;
            w.NameDiffers = config.Heraldry.SyncGuildNames && w.CurName != w.Name;
            w.ColoursDiffer = config.Heraldry.SyncBannerColours && colourBindError == null && (w.CurField != w.Field || w.CurCharge != w.Charge);
            w.IndexDiffers = w.Banner != w.CurBanner || w.Pattern != w.CurPattern;
            w.Sig = w.Name + "|" + w.Field + "|" + w.Charge + "|" + w.Banner + "|" + w.Pattern;
            return w;
        }

        // One pass over every house. dryRun: report only. Returns counts (applied, in step, unbound, waiting).
        private void SyncAll(bool dryRun, string onlyHouse, List<string> report, out int applied, out int inStep, out int unbound, out int waiting)
        {
            applied = inStep = unbound = waiting = 0;
            List<string> houses = HouseNames();
            if (houses == null) { lastSyncSummary = "RealmHouses not loaded"; return; }
            DateTime now = Now();
            ReadHousesFile(true);
            var claimed = new Dictionary<ulong, string>();
            houses.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string house in houses)
            {
                Want w = Wanted(house, claimed);
                if (onlyHouse != null && !string.Equals(house, onlyHouse, StringComparison.OrdinalIgnoreCase)) continue;
                if (w.Guild == null)
                {
                    unbound++;
                    if (report != null) report.Add(Fmt("PreviewUnbound", null, HouseTint(house), w.Unbound));
                    continue;
                }
                HouseArms a = ArmsRec(house, true);
                if (a.GuildId != w.Guild.BaseID) { a.GuildId = w.Guild.BaseID; dirty = true; }
                bool differs = w.NameDiffers || w.ColoursDiffer || w.IndexDiffers;
                if (report != null)
                {
                    var parts = new List<string>();
                    if (w.NameDiffers) parts.Add(Fmt("PreviewName", null, Clean(w.CurName, 40), w.Name));
                    if (w.ColoursDiffer) parts.Add(Fmt("PreviewColours", null, w.CurField, w.CurCharge, w.Field, w.Charge));
                    if (w.IndexDiffers) parts.Add(Fmt("PreviewIndex", null, w.CurBanner, w.CurPattern, w.Banner, w.Pattern));
                    report.Add(Fmt("PreviewLine", null, HouseTint(house), Clean(w.CurName, 40), parts.Count == 0 ? Msg("PreviewInStep", null) : string.Join("; ", parts.ToArray())));
                }
                if (!differs)
                {
                    inStep++;
                    if (a.AppliedSig != w.Sig) { a.AppliedSig = w.Sig; dirty = true; }
                    continue;
                }
                if (dryRun) continue;
                // Enforce off: apply only when the realm changed the arms (a new choice, a renamed house); a player's edit in
                // the game's guild menu stands until then.
                if (!config.Heraldry.Enforce && a.AppliedSig == w.Sig) { inStep++; continue; }
                DateTime last;
                // The per-guild cooldown paces the routine sync; a house's own new colours (onlyHouse) go up at once.
                if (applied >= config.Heraldry.MaxAppliesPerSync || (onlyHouse == null
                    && guildApplied.TryGetValue(w.Guild.BaseID, out last) && (now - last).TotalSeconds < config.Heraldry.GuildCooldownSeconds))
                {
                    waiting++;
                    continue;
                }
                bool drift = a.AppliedSig == w.Sig;            // it was in step with these very arms: someone changed it in game
                if (!Apply(w)) { waiting++; continue; }
                guildApplied[w.Guild.BaseID] = now;
                applied++;
                if (drift) a.Drifts++;
                a.Applies++;
                a.AppliedSig = w.Sig;
                a.LastApplied = now;
                dirty = true;
            }
            if (!dryRun)
            {
                lastSyncAt = now;
                lastSyncSummary = applied + " applied, " + inStep + " in step, " + unbound + " unbound, " + waiting + " waiting";
            }
        }

        // The game's own path (see header): a copy of the guild with the new name and a new BannerData, raised as
        // GuildUpdateEvent; the server updates the guild and tells its members. Then, optionally, everyone hears the
        // banner. Returns false if the game refused or threw; the next sync tries again.
        private bool Apply(Want w)
        {
            try
            {
                BannerData banner = new BannerData();
                banner.CurrentBanner = w.Banner;
                banner.CurrentPattern = w.Pattern;
                BannerData cur = w.Guild.Banner;
                if (!SetColour(banner, true, config.Heraldry.SyncBannerColours ? w.Field : w.CurField)
                    || !SetColour(banner, false, config.Heraldry.SyncBannerColours ? w.Charge : w.CurCharge))
                {
                    if (cur == null || colourBindError == null) return false;
                    banner = cur;                                          // colours unreachable: keep the guild's own
                }
                Guild copy = new Guild(w.Guild);
                copy.Name = config.Heraldry.SyncGuildNames ? w.Name : w.Guild.Name;
                copy.Banner = banner;
                GuildUpdateEvent evt = new GuildUpdateEvent(copy);
                EventManager.CallEvent(evt);
                if (evt.Cancelled) { PrintWarning("The game refused the guild update for House " + w.House + ": " + evt.CancelReason); return false; }
                if (config.Heraldry.BroadcastBanner) EventManager.CallEvent(new GuildBannerUpdateEvent(banner, w.Guild.BaseID));
                return true;
            }
            catch (Exception ex)
            {
                if (Throttle("apply:" + HouseKey(w.House), 600)) PrintWarning("Could not update the guild of House " + w.House + ": " + ex.Message);
                return false;
            }
        }

        // The game guild a house flies as. RealmHouses.json's GuildId when the file can be read (its own binding,
        // /house link); otherwise, with GuildFallbackByLeader, the leader's guild when every member of it is in the house.
        private Guild GuildFor(string house, out string why)
        {
            why = null;
            GuildScheme scheme = SocialAPI.Get<GuildScheme>();
            if (scheme == null) { why = "the game's guilds are not ready"; return null; }
            HousesFileLite f = ReadHousesFile(false);
            if (f != null && f.Houses != null)
            {
                foreach (HouseLite h in f.Houses)
                {
                    if (h == null || !string.Equals(h.Name, house, StringComparison.OrdinalIgnoreCase)) continue;
                    if (h.GuildId == 0) { why = "not bound (/house link)"; return null; }
                    Guild g = scheme.TryGetGuild(h.GuildId);
                    if (g == null) why = "its guild " + h.GuildId + " is gone";
                    return g;
                }
                // A house founded since the file was last written: fall through to the leader's guild.
            }
            if (!config.Heraldry.GuildFallbackByLeader) { why = "RealmHouses.json unreadable"; return null; }
            string leader = RealmHouses != null ? RealmHouses.Call("GetHouseLeader", house) as string : null;
            ulong lid;
            if (leader == null || !ulong.TryParse(leader, out lid)) { why = "no leader known"; return null; }
            Guild lg = scheme.TryGetGuildByMember(lid);
            if (lg == null) { why = "the leader has no guild"; return null; }
            List<string> members = RealmHouses.Call("GetMembers", house) as List<string>;
            if (members == null) { why = "no members known"; return null; }
            foreach (Member m in lg.Members().EachMember())
                if (m != null && !members.Contains(m.PlayerId.ToString())) { why = "the leader's guild holds players outside the house"; return null; }
            return lg;
        }

        private HousesFileLite ReadHousesFile(bool refresh)
        {
            if (!refresh && housesFileAt != DateTime.MinValue) return housesFile;
            housesFileAt = Now();
            housesFile = null;
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(HousesFile)) { housesFileStatus = "absent"; return null; }
            try { housesFile = Interface.Oxide.DataFileSystem.ReadObject<HousesFileLite>(HousesFile); }
            catch (Exception ex) { housesFileStatus = "unreadable (" + ex.Message + ")"; housesFile = null; return null; }
            housesFileStatus = housesFile == null || housesFile.Houses == null ? "empty" : housesFile.Houses.Count + " houses";
            return housesFile;
        }

        private bool JoinedFromFile(string house, string id, out DateTime joined)
        {
            joined = DateTime.MinValue;
            if (!config.Voters.SeedFromHousesFile) return false;
            HousesFileLite f = ReadHousesFile(housesFileAt == DateTime.MinValue || (Now() - housesFileAt).TotalSeconds > 300);
            if (FindJoined(f, house, id, out joined)) return true;
            // A member the cached copy does not know yet (a new join): read the file again, at most every 10 seconds.
            if ((Now() - housesFileAt).TotalSeconds < 10) return false;
            return FindJoined(ReadHousesFile(true), house, id, out joined);
        }

        private static bool FindJoined(HousesFileLite f, string house, string id, out DateTime joined)
        {
            joined = DateTime.MinValue;
            if (f == null || f.Houses == null) return false;
            foreach (HouseLite h in f.Houses)
            {
                if (h == null || h.Members == null || !string.Equals(h.Name, house, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (HouseMemberLite m in h.Members)
                    if (m != null && m.Id == id && m.Joined > DateTime.MinValue) { joined = DateTime.SpecifyKind(m.Joined, DateTimeKind.Utc); return true; }
            }
            return false;
        }

        // Binds BannerData's two colour properties and UnityEngine.Color (ctor r, g, b, a and fields r, g, b) once.
        private static void BindColours()
        {
            if (fieldColourProp != null && chargeColourProp != null && colourCtor != null) { colourBindError = null; return; }
            try
            {
                Type bd = typeof(BannerData);
                fieldColourProp = bd.GetProperty("CurrentColor", BindingFlags.Public | BindingFlags.Instance);
                chargeColourProp = bd.GetProperty("CurrentPaternColor", BindingFlags.Public | BindingFlags.Instance);
                if (fieldColourProp == null || chargeColourProp == null) { colourBindError = "BannerData.CurrentColor / CurrentPaternColor not found"; return; }
                Type ct = fieldColourProp.PropertyType;
                colourCtor = ct.GetConstructor(new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                colourR = ct.GetField("r");
                colourG = ct.GetField("g");
                colourB = ct.GetField("b");
                if (colourCtor == null || colourR == null || colourG == null || colourB == null) { colourBindError = ct.FullName + " (r, g, b, a) not found"; return; }
                colourBindError = null;
            }
            catch (Exception ex) { colourBindError = ex.Message; }
        }

        private static bool SetColour(BannerData b, bool field, string hex)
        {
            int r, g, bl;
            if (colourBindError != null || b == null || !ParseHex(hex, out r, out g, out bl)) return false;
            try
            {
                object c = colourCtor.Invoke(new object[] { r / 255f, g / 255f, bl / 255f, 1f });
                (field ? fieldColourProp : chargeColourProp).SetValue(b, c, null);
                return true;
            }
            catch (Exception) { return false; }
        }

        // "#rrggbb" of a banner colour, or "" when unreadable.
        private static string ReadColour(BannerData b, bool field)
        {
            if (b == null || colourBindError != null) return "";
            try
            {
                object c = (field ? fieldColourProp : chargeColourProp).GetValue(b, null);
                return "#" + Byte(colourR.GetValue(c)) + Byte(colourG.GetValue(c)) + Byte(colourB.GetValue(c));
            }
            catch (Exception) { return ""; }
        }

        private static string Byte(object f)
        {
            float v = f is float ? (float)f : 0f;
            int n = (int)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 255f);
            return n.ToString("x2", CultureInfo.InvariantCulture);
        }

        [ChatCommand("heraldry")]
        private void CmdHeraldry(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            bool admin = IsAdmin(player);
            switch (sub)
            {
                case "": ShowArms(player, HouseOf(player.Id.ToString()), true); return;
                case "help": HelpArms(player); return;
                case "house": ShowArms(player, args.Length > 1 ? CanonicalHouse(JoinFrom(args, 1)) : null, false); return;
                case "colours": case "colors": case "colour": case "color":
                    if (args.Length > 1) ChoosePair(player, JoinFrom(args, 1)); else ListPairs(player);
                    return;
                case "sync": case "preview": case "set": case "reset": case "banner": case "status":
                    if (!admin) { ReplyError(player, "NoPermission"); return; }
                    AdminArms(player, sub, args);
                    return;
            }
            string named = CanonicalHouse(JoinFrom(args, 0));
            if (named != null && HouseNames() != null && ContainsIgnoreCase(HouseNames(), named)) { ShowArms(player, named, false); return; }
            HelpArms(player);
        }

        private void HelpArms(Player player)
        {
            Reply(player, "HelpArms1");
            Reply(player, "HelpArms2");
            if (IsAdmin(player)) Reply(player, "HelpArmsAdmin");
        }

        private void ShowArms(Player player, string house, bool own)
        {
            if (RealmHouses == null) { ReplyError(player, "HousesAway"); return; }
            if (house == null) { if (own) { ReplyError(player, "NoHouse"); HelpArms(player); } else HelpArms(player); return; }
            List<string> houses = HouseNames();
            if (houses == null || !ContainsIgnoreCase(houses, house)) { ReplyError(player, "HouseUnknown", Clean(house, 40)); return; }
            house = CanonicalHouse(house);
            bool chosen;
            ArmsPair p = PairOf(house, out chosen);
            Reply(player, "ArmsLine", HouseTint(house), p.Name, p.ChargeName, p.FieldName,
                chosen || IsGreatHouse(house) ? "" : Msg("ArmsDefault", player));
            ReadHousesFile(housesFileAt == DateTime.MinValue || (Now() - housesFileAt).TotalSeconds > 60);
            Want w = Wanted(house, new Dictionary<ulong, string>());
            if (w.Guild == null) { Reply(player, "ArmsGuildNone"); return; }
            bool step = !(w.NameDiffers || w.ColoursDiffer || w.IndexDiffers);
            Reply(player, "ArmsGuild", Clean(w.CurName, 40), step ? Msg("ArmsInStep", player)
                : Fmt("ArmsPending", player, Span(nextSync > Now() ? nextSync - Now() : TimeSpan.FromSeconds(config.Heraldry.SyncSeconds))));
        }

        private void ListPairs(Player player)
        {
            string mine = HouseOf(player.Id.ToString());
            List<ArmsPair> pairs = config.Heraldry.Pairs;
            Reply(player, "PairsHeader", pairs.Count);
            foreach (ArmsPair p in pairs)
            {
                string note = "";
                bool chosen;
                if (mine != null && PairOf(mine, out chosen).Id == p.Id) note = Msg("PairYours", player);
                else if (!string.IsNullOrEmpty(p.ReservedFor) && config.Heraldry.ReserveGreatHousePairs) note = Fmt("PairReserved", player, HouseTint(p.ReservedFor));
                else
                {
                    string holder = config.Heraldry.UniqueChoices ? HolderOf(p, mine) : null;
                    if (holder != null) note = Fmt("PairTaken", player, HouseTint(holder));
                }
                Reply(player, "PairLine", p.Id, p.Name, p.ChargeName, p.FieldName, note);
            }
            string fee = config.Heraldry.ChangeFeeMarks > 0
                ? Fmt("PairsFee", player, config.Heraldry.ChangeFeeMarks, config.Heraldry.ChangeCooldownHours) + (config.Heraldry.FirstChoiceFree ? Msg("PairsFirstFree", player) : "")
                : "";
            Reply(player, "PairsHint", fee);
        }

        private void ChoosePair(Player player, string text)
        {
            if (!config.Heraldry.Enabled) { ReplyError(player, "SyncOff"); return; }
            if (RealmHouses == null) { ReplyError(player, "HousesAway"); return; }
            string id = player.Id.ToString();
            string house = HouseOf(id);
            if (house == null) { ReplyError(player, "NoHouse"); return; }
            ArmsPair p = FindPair(text);
            if (p == null) { ReplyError(player, "PairUnknown", Clean(text, 40)); return; }
            string leader = RealmHouses.Call("GetHouseLeader", house) as string;
            if (leader != id) { ReplyError(player, "NotLeader", HouseTint(house)); return; }
            if (!MayBear(house, p)) { ReplyError(player, "PairIsReserved", HouseTint(p.ReservedFor)); return; }
            string holder = config.Heraldry.UniqueChoices ? HolderOf(p, house) : null;
            if (holder != null) { ReplyError(player, "PairIsTaken", HouseTint(holder)); return; }
            bool chosen;
            if (PairOf(house, out chosen).Id == p.Id && chosen) { ReplyError(player, "PairAlready", HouseTint(house), p.Name); return; }
            HouseArms a = ArmsRec(house, true);
            bool first = a.ChosenAt == DateTime.MinValue;
            DateTime now = Now();
            if (!first && config.Heraldry.ChangeCooldownHours > 0 && now < a.ChosenAt.AddHours(config.Heraldry.ChangeCooldownHours))
            {
                ReplyError(player, "PairCooldown", HouseTint(house), Span(a.ChosenAt.AddHours(config.Heraldry.ChangeCooldownHours) - now));
                return;
            }
            long fee = first && config.Heraldry.FirstChoiceFree ? 0 : config.Heraldry.ChangeFeeMarks;
            if (fee > 0)
            {
                if (RealmTreasury == null) { ReplyError(player, "TreasuryAway"); return; }
                object paid = RealmTreasury.Call("ChargeMarks", id, player.Name, fee, Source, "new colours for House " + house);
                if (!(paid is bool) || !(bool)paid) { ReplyError(player, "CannotPay", fee); return; }
            }
            a.Pair = p.Id;
            a.ChosenAt = now;
            a.ChosenBy = Clean(player.Name, 40);
            SaveData();
            Reply(player, "PairChosen", HouseTint(house), p.Name, p.ChargeName, p.FieldName);
            if (config.Heraldry.AnnounceNewColours && Throttle("colours:" + HouseKey(house), 3600)) Herald("HeraldNewColours", HouseTint(house), p.Name);
            int ap, st, ub, wt;
            SyncAll(false, house, null, out ap, out st, out ub, out wt);
        }

        private void AdminArms(Player player, string sub, string[] args)
        {
            if (sub == "status")
            {
                Reply(player, "StatusHeader");
                Reply(player, "StatusSync", OnOff(config.Heraldry.Enabled, player), OnOff(config.Heraldry.SyncGuildNames, player),
                    OnOff(config.Heraldry.SyncBannerColours, player), OnOff(config.Heraldry.Enforce, player),
                    lastSyncAt == DateTime.MinValue ? Msg("Never", player) : Span(Now() - lastSyncAt), lastSyncSummary);
                Reply(player, "StatusFile", housesFileStatus, colourBindError ?? Msg("Ok", player));
                Reply(player, "StatusVotes", OnOff(config.Council.Enabled, player), OnOff(config.Referendum.Enabled, player), data.Voters.Count, OpenBallots().Count);
                return;
            }
            if (sub == "sync" || sub == "preview")
            {
                if (!config.Heraldry.Enabled && sub == "sync") { ReplyError(player, "SyncOff"); return; }
                if (RealmHouses == null) { ReplyError(player, "HousesAway"); return; }
                BindColours();
                if (colourBindError != null) Reply(player, "PreviewError", colourBindError);
                var report = new List<string>();
                string only = args.Length > 1 ? CanonicalHouse(JoinFrom(args, 1)) : null;
                int ap, st, ub, wt;
                SyncAll(sub == "preview", only, report, out ap, out st, out ub, out wt);
                if (sub == "preview")
                {
                    Reply(player, "PreviewHeader", report.Count);
                    for (int i = 0; i < report.Count && i < 40; i++) player.SendMessage(report[i]);
                }
                else Reply(player, "SyncDone", ap, st, ub, wt);
                if (dirty) SaveData();
                return;
            }
            if (args.Length < 2) { HelpArms(player); return; }
            string house = CanonicalHouse(args[1]);
            List<string> houses = HouseNames();
            if (houses == null) { ReplyError(player, "HousesAway"); return; }
            if (!ContainsIgnoreCase(houses, house)) { ReplyError(player, "HouseUnknown", Clean(args[1], 40)); return; }
            HouseArms a = ArmsRec(house, true);
            if (sub == "set")
            {
                ArmsPair p = args.Length > 2 ? FindPair(JoinFrom(args, 2)) : null;
                if (p == null) { ReplyError(player, "PairUnknown", Clean(args.Length > 2 ? JoinFrom(args, 2) : "", 40)); return; }
                a.Pair = p.Id;
                a.ChosenBy = "staff";
                SaveData();
                Reply(player, "AdminSet", HouseTint(house), p.Name);
            }
            else if (sub == "reset")
            {
                a.Pair = "";
                a.ChosenAt = DateTime.MinValue;
                bool chosen;
                SaveData();
                Reply(player, "AdminReset", HouseTint(house), PairOf(house, out chosen).Name);
            }
            else if (sub == "banner")
            {
                if (config.Heraldry.BannerCount <= 0 || config.Heraldry.PatternCount <= 0) { ReplyError(player, "AdminBannerOff"); return; }
                int bi, pi;
                if (args.Length < 4 || !ParseIndex(args[2], config.Heraldry.BannerCount, out bi) || !ParseIndex(args[3], config.Heraldry.PatternCount, out pi))
                {
                    ReplyError(player, "AdminBannerBad", config.Heraldry.BannerCount - 1, config.Heraldry.PatternCount - 1);
                    return;
                }
                a.BannerIndex = bi;
                a.PatternIndex = pi;
                SaveData();
                Reply(player, "AdminBannerSet", HouseTint(house), bi < 0 ? "keep" : bi.ToString(), pi < 0 ? "keep" : pi.ToString());
            }
            int ap2, st2, ub2, wt2;
            SyncAll(false, house, null, out ap2, out st2, out ub2, out wt2);
        }

        private static bool ParseIndex(string s, int count, out int value)
        {
            value = -1;
            if (string.Equals(s, "keep", StringComparison.OrdinalIgnoreCase)) return true;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0 && value < count;
        }

        private string OnOff(bool on, Player player)
        {
            return Msg(on ? "On" : "OffWord", player);
        }

        #endregion

        #region Ballots

        private List<Ballot> OpenBallots()
        {
            var list = new List<Ballot>();
            foreach (Ballot b in data.Ballots) if (b.Status == SNominating || b.Status == SVoting) list.Add(b);
            return list;
        }

        private Ballot OpenCouncil()
        {
            foreach (Ballot b in data.Ballots) if (b.Kind == KCouncil && (b.Status == SNominating || b.Status == SVoting)) return b;
            return null;
        }

        private Ballot OpenReferendum()
        {
            foreach (Ballot b in data.Ballots) if (b.Kind == KReferendum && b.Status == SVoting) return b;
            return null;
        }

        private Ballot FindBallot(string text)
        {
            int id;
            string t = (text ?? "").TrimStart('#');
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return null;
            foreach (Ballot b in data.Ballots) if (b.Id == id) return b;
            return null;
        }

        private void ScheduleSeasonElection(DateTime now)
        {
            if (!config.Council.Enabled || !config.Council.ElectEachSeason || RealmSeasons == null) return;
            object o = RealmSeasons.Call("GetSeasonNumber");
            if (!(o is int)) return;
            int season = (int)o;
            if (season <= 0) return;
            if (!data.SeasonBaselined)
            {
                // First sight of a season already under way: its election comes with the next season (or /ballot admin open).
                data.SeasonBaselined = true;
                data.LastElectionSeason = season;
                dirty = true;
                return;
            }
            if (season == data.LastElectionSeason || OpenCouncil() != null) return;
            string refusal;
            if (OpenElection(now, "season " + season, season, out refusal) != null) data.LastElectionSeason = season;
            else if (Throttle("season-election", 3600)) PrintWarning("The council election of season " + season + " could not open: " + refusal);
        }

        // Opens a council election for the elected seats that the crown's council really has. Null with a reason if not.
        private Ballot OpenElection(DateTime now, string by, int season, out string refusal)
        {
            refusal = null;
            if (CrownAndConsequences == null) { refusal = Msg("CrownAway", null); return null; }
            if (OpenCouncil() != null) { refusal = Fmt("AdminElectionOpen", null, OpenCouncil().Id); return null; }
            if (OpenBallots().Count >= MaxOpenBallots) { refusal = Msg("AdminTooMany", null); return null; }
            List<string> crownSeats = CrownAndConsequences.Call("GetCouncilSeats") as List<string>;
            var seats = new List<string>();
            if (crownSeats != null)
                foreach (string s in config.Council.ElectedSeats)
                    foreach (string c in crownSeats)
                        if (string.Equals(s, c, StringComparison.OrdinalIgnoreCase) && !seats.Contains(c)) seats.Add(c);
            if (seats.Count == 0)
            {
                refusal = Fmt("AdminNoSeats", null, string.Join(", ", config.Council.ElectedSeats.ToArray()),
                    crownSeats != null ? string.Join(", ", crownSeats.ToArray()) : "?");
                return null;
            }
            var b = new Ballot
            {
                Id = data.NextBallotId++, Kind = KCouncil, Status = SNominating, Season = season, OpenedAt = now, OpenedBy = Clean(by, 40),
                VotingAt = now.AddHours(config.Council.NominationHours)
            };
            b.ClosesAt = b.VotingAt.AddHours(config.Council.VotingHours);
            b.Seats = seats;
            data.Ballots.Add(b);
            SaveData();
            Herald("HeraldElectionCalled", season > 0 ? SeasonLabel(season) : "the realm", string.Join(", ", seats.ToArray()), Span(b.VotingAt - now));
            return b;
        }

        private string SeasonLabel(int season)
        {
            string name = RealmSeasons != null ? RealmSeasons.Call("GetSeasonName") as string : null;
            return string.IsNullOrEmpty(name) ? "season " + season : name;
        }

        private void AdvanceBallots(DateTime now)
        {
            foreach (Ballot b in OpenBallots())
            {
                if (b.Status == SNominating && now >= b.VotingAt) StartVoting(b, now);
                else if (b.Status == SVoting && now >= b.ClosesAt) CloseBallot(b, now);
            }
        }

        private void StartVoting(Ballot b, DateTime now)
        {
            if (b.Candidates.Count == 0)
            {
                b.Status = SClosed;
                b.ClosedAt = now;
                b.Results.Add(Msg("HeraldNoCandidates", null));
                Herald("HeraldNoCandidates");
                Trim();
                SaveData();
                return;
            }
            b.Status = SVoting;
            if (b.ClosesAt <= now) b.ClosesAt = now.AddHours(config.Council.VotingHours);
            SaveData();
            string list = CandidateList(b);
            Herald("HeraldVotingOpen", list, Span(b.ClosesAt - now));
            NotifyVoters(b, Fmt("PopupVoteCouncil", null, PopupText(list), Span(b.ClosesAt - now)));
        }

        private string CandidateList(Ballot b)
        {
            var parts = new List<string>();
            foreach (string seat in b.Seats)
            {
                var names = new List<string>();
                foreach (Candidate c in b.Candidates) if (c.Seat == seat) names.Add(c.Name + " (" + HouseTint(c.House) + ")");
                if (names.Count > 0) parts.Add(seat + ": " + string.Join(", ", names.ToArray()));
            }
            return string.Join("; ", parts.ToArray());
        }

        private void CloseBallot(Ballot b, DateTime now)
        {
            b.Status = SClosed;
            b.ClosedAt = now;
            if (b.Kind == KCouncil) CountCouncil(b, now); else CountReferendum(b, now);
            Trim();
            SaveData();
        }

        private Dictionary<string, int> Tally(Ballot b, string key)
        {
            var t = new Dictionary<string, int>();
            foreach (KeyValuePair<string, Dictionary<string, string>> kv in b.Votes)
            {
                if (b.Struck.Contains(kv.Key)) continue;
                string choice;
                if (!kv.Value.TryGetValue(key, out choice) || choice == null) continue;
                int n;
                t.TryGetValue(choice, out n);
                t[choice] = n + 1;
            }
            return t;
        }

        private void CountCouncil(Ballot b, DateTime now)
        {
            var actors = new List<string>();
            var lines = new List<string>();
            string until = now.AddDays(config.Council.TermDays).ToString("o", CultureInfo.InvariantCulture);
            foreach (string seat in b.Seats)
            {
                var cands = new List<Candidate>();
                foreach (Candidate c in b.Candidates) if (c.Seat == seat) cands.Add(c);
                if (cands.Count == 0) { Announce(b, lines, "HeraldSeatEmpty", seat); continue; }
                Dictionary<string, int> t = Tally(b, seat);
                int total = 0;
                foreach (Candidate c in cands) { int n; if (t.TryGetValue(c.Id, out n)) total += n; }
                Candidate winner = null;
                bool unopposed = cands.Count == 1;
                if (unopposed) winner = cands[0];
                else if (total < config.Council.MinTurnout) Announce(b, lines, "HeraldSeatQuorum", seat, total, config.Council.MinTurnout);
                else
                {
                    foreach (Candidate c in cands)
                        if (winner == null || Better(c, winner, t)) winner = c;
                }
                Candidate top = winner;                         // keeps the deposit even if the crown cannot seat them
                if (winner != null)
                {
                    object ok = CrownAndConsequences != null ? CrownAndConsequences.Call("SeatElectedCouncillor", seat, winner.Id, winner.Name, until) : null;
                    if (!(ok is bool) || !(bool)ok) { Announce(b, lines, "HeraldSeatFailed", winner.Name, seat); winner = null; }
                    else
                    {
                        int votes;
                        t.TryGetValue(winner.Id, out votes);
                        if (unopposed) Announce(b, lines, "HeraldUnopposed", winner.Name, HouseTint(winner.House), seat);
                        else Announce(b, lines, "HeraldElected", winner.Name, HouseTint(winner.House), seat, votes, total);
                        actors.Add(winner.Name);
                        if (RealmSeasons != null && config.Council.ElectedHousePoints > 0)
                            RealmSeasons.Call("AwardHouse", winner.House, config.Council.ElectedHousePoints, "elected to the council: " + seat);
                        if (RealmQuests != null) RealmQuests.Call("ReportQuestEvent", winner.Id, "custom", "council_elected", 1);
                    }
                }
                // Deposits: back to the winner, an unopposed or quorum-less seat, and anyone with DepositReturnPercent of
                // the votes; the rest go to the crown's treasury.
                foreach (Candidate c in cands)
                {
                    int votes;
                    t.TryGetValue(c.Id, out votes);
                    bool back = c == top || unopposed || total < config.Council.MinTurnout
                        || (long)votes * 100 >= (long)config.Council.DepositReturnPercent * total;
                    SettleDeposit(c, back, true);
                }
            }
            if (actors.Count > 0 || lines.Count > 0)
                Chronicle(ChronicleType, "The realm elects its council", PlainText(string.Join(" ", lines.ToArray())), actors.ToArray());
        }

        // Most votes; on a tie the greater renown (RealmRenown), then whoever stood first.
        private bool Better(Candidate c, Candidate best, Dictionary<string, int> t)
        {
            int a, b;
            t.TryGetValue(c.Id, out a);
            t.TryGetValue(best.Id, out b);
            if (a != b) return a > b;
            int ra = RenownOf(c.Id), rb = RenownOf(best.Id);
            if (ra != rb) return ra > rb;
            return c.StoodAt < best.StoodAt;
        }

        private int RenownOf(string id)
        {
            if (RealmRenown == null) return 0;
            object o = RealmRenown.Call("GetRenown", id);
            return o is int ? (int)o : 0;
        }

        private void SettleDeposit(Candidate c, bool back, bool tell)
        {
            if (c.Deposit <= 0 || string.IsNullOrEmpty(c.HoldId) || RealmTreasury == null) return;
            object o = RealmTreasury.Call("ReleaseHold", c.HoldId, Source);
            long returned = o is long ? (long)o : 0;
            c.HoldId = "";
            if (returned <= 0) return;
            Player p = Online(c.Id);
            if (back) { if (p != null && tell) Say(p, "DepositBack", returned, c.Seat); return; }
            // Forfeit: straight back out of the purse it just returned to, in the same tick, into the crown's treasury.
            object paid = RealmTreasury.Call("ChargeMarks", c.Id, c.Name, returned, Source, "forfeit deposit, election for " + c.Seat);
            if (paid is bool && (bool)paid) { if (p != null && tell) Say(p, "DepositLost", c.Seat, returned); }
        }

        private void CountReferendum(Ballot b, DateTime now)
        {
            data.LastReferendumAt = now;                       // the cooldown runs from the answer
            Dictionary<string, int> t = Tally(b, QKey);
            int yes, no;
            t.TryGetValue("yes", out yes);
            t.TryGetValue("no", out no);
            int total = yes + no;
            string q = QuestionText(b, null);
            var lines = new List<string>();
            if (total < config.Referendum.MinTurnout) { Announce(b, lines, "HeraldNoQuorum", total, config.Referendum.MinTurnout, q); }
            else
            {
                bool carried = (long)yes * 100 > (long)config.Referendum.PassPercent * total;
                Announce(b, lines, carried ? "HeraldCarried" : "HeraldRefused", yes, no, q);
                int hours = config.Referendum.MandateHours;
                if (b.Question == QDecree)
                {
                    object ok = CrownAndConsequences != null ? CrownAndConsequences.Call("SetDecreeMandate", b.Target, carried, hours) : null;
                    if (ok is bool && (bool)ok) Announce(b, lines, carried ? "HeraldMandatePassed" : "HeraldMandateRefused", b.TargetName, hours);
                }
                else
                {
                    bool proclaim = b.Question == QProclaim;
                    Announce(b, lines, "HeraldLawAdvice", b.TargetName, hours);
                    // What the crown must not do now: proclaim a law the realm refused, or repeal one it chose to keep.
                    if (!carried)
                        data.LawWatches.Add(new LawWatch { BallotId = b.Id, LawId = b.Target, LawName = b.TargetName, MustBeInForce = !proclaim, Until = now.AddHours(hours) });
                }
            }
            Chronicle(ChronicleType, "The realm answers the crown", PlainText(string.Join(" ", lines.ToArray())), new string[0]);
        }

        private void CheckLawWatches(DateTime now)
        {
            data.LawWatches.RemoveAll(delegate(LawWatch w) { return w.Reported || w.Until <= now; });
            if (data.LawWatches.Count == 0 || RealmLaws == null) return;
            string[] active = RealmLaws.Call("GetActiveLaws") as string[];
            if (active == null) return;
            foreach (LawWatch w in data.LawWatches)
            {
                bool inForce = false;
                foreach (string row in active) if (row != null && row.Split('|')[0] == w.LawId) inForce = true;
                if (inForce == w.MustBeInForce) continue;
                w.Reported = true;
                dirty = true;
                string what = Msg(inForce ? "DefiedNowInForce" : "DefiedNoLongerInForce", null);
                Herald("HeraldDefied", w.LawName, what);
                Chronicle(ChronicleType, "The crown defies the realm's vote", PlainText(Fmt("HeraldDefied", null, w.LawName, what)), new string[0]);
            }
        }

        private void Announce(Ballot b, List<string> lines, string key, params object[] args)
        {
            string text = Fmt(key, null, args);
            lines.Add(text);
            b.Results.Add(text);
            Server.BroadcastMessage(Msg("Herald", null) + text);
        }

        private void Trim()
        {
            var closed = new List<Ballot>();
            foreach (Ballot b in data.Ballots) if (b.Status == SClosed || b.Status == SCancelled) closed.Add(b);
            int over = closed.Count - config.General.ClosedBallotsKept;
            for (int i = 0; i < over; i++) data.Ballots.Remove(closed[i]);
        }

        private string QuestionText(Ballot b, Player player)
        {
            if (b.Question == QDecree) return Fmt("QuestionDecree", player, b.TargetName);
            if (b.Question == QProclaim) return Fmt("QuestionProclaim", player, b.TargetName);
            return Fmt("QuestionRepeal", player, b.TargetName);
        }

        // Why a player may not vote in ballot b (or in general when b is null); null = may vote.
        private string WhyNot(string id, Ballot b, Player asker)
        {
            VoterRec r = Voter(id, null, false);
            if (r == null) return Msg("WhyUnknown", asker);
            if (b != null && b.Struck.Contains(id)) return Msg("WhyStruck", asker);
            DateTime now = Now();
            VoterSection v = config.Voters;
            if ((now - r.FirstSeen).TotalHours < v.MinAccountAgeHours) return Fmt("WhyNewAccount", asker, v.MinAccountAgeHours);
            if (r.PlaySeconds / 60 < v.MinPlayMinutes) return Fmt("WhyPlay", asker, v.MinPlayMinutes, r.PlaySeconds / 60);
            if (v.RequireHouse)
            {
                string house = HouseOf(id);
                if (house == null) return Msg("WhyNoHouse", asker);
                if (!SameHouse(house, r.House)) return Fmt("WhyHouseYoung", asker, v.MinHouseMembershipHours);   // not yet observed
                if (b != null && v.JoinedBeforeBallot && r.HouseSince > b.OpenedAt) return Msg("WhyJoinedLate", asker);
                if ((now - r.HouseSince).TotalHours < v.MinHouseMembershipHours) return Fmt("WhyHouseYoung", asker, v.MinHouseMembershipHours);
            }
            ulong u;
            if (!v.NewPlayersMayVote && RealmWarden != null && ulong.TryParse(id, out u) && IsTrue(RealmWarden.Call("IsNewPlayerProtected", u)))
                return Msg("WhyProtected", asker);
            if (!v.OutlawsMayVote && ((RealmContracts != null && IsTrue(RealmContracts.Call("IsOutlaw", id)))
                || (RealmLaws != null && IsTrue(RealmLaws.Call("IsCourtOutlaw", id))))) return Msg("WhyOutlaw", asker);
            if (!v.ExilesMayVote && RealmLaws != null && IsTrue(RealmLaws.Call("IsExiled", id))) return Msg("WhyExiled", asker);
            if (v.MinWaystones > 0 && RealmTravel != null)
            {
                object o = RealmTravel.Call("GetDiscoveredCount", id);
                if (!(o is int) || (int)o < v.MinWaystones) return Fmt("WhyWaystones", asker, v.MinWaystones);
            }
            return null;
        }

        private static bool IsTrue(object o)
        {
            return o is bool && (bool)o;
        }

        private bool IsMonarch(Player player)
        {
            KingsScheme ks = SocialAPI.Get<KingsScheme>();
            return ks != null && ks.HasKing() && ks.IsKing(player);
        }

        private bool IsMonarchId(string id)
        {
            KingsScheme ks = SocialAPI.Get<KingsScheme>();
            return ks != null && ks.HasKing() && ks.GetKingID().ToString() == id;
        }

        [ChatCommand("ballot")]
        private void CmdBallot(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed || data == null) { SayError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "": ListBallots(player); return;
                case "help": HelpBallot(player); return;
                case "stand": Stand(player, JoinFrom(args, 1)); return;
                case "withdraw": Withdraw(player); return;
                case "propose": Propose(player, args); return;
                case "results": Results(player, args.Length > 1 ? args[1] : null); return;
                case "history": History(player); return;
                case "me": Me(player); return;
                case "admin":
                    if (!IsAdmin(player)) { SayError(player, "NoPermission"); return; }
                    AdminBallot(player, args);
                    return;
            }
            Ballot b = FindBallot(sub);
            if (b != null) { ShowBallot(player, b); return; }
            HelpBallot(player);
        }

        private void HelpBallot(Player player)
        {
            Say(player, "HelpBallot1");
            Say(player, "HelpBallot2");
            if (IsAdmin(player)) Say(player, "HelpBallotAdmin");
        }

        private void ListBallots(Player player)
        {
            List<Ballot> open = OpenBallots();
            DateTime now = Now();
            if (open.Count == 0)
            {
                Say(player, "NoBallots");
                if (config.Council.Enabled && config.Council.ElectEachSeason) Say(player, "NextElection", string.Join(", ", config.Council.ElectedSeats.ToArray()));
                HelpBallot(player);
                return;
            }
            Say(player, "BallotsHeader");
            foreach (Ballot b in open)
            {
                if (b.Kind == KCouncil)
                    Say(player, "BallotCouncilLine", b.Id, b.Season > 0 ? SeasonLabel(b.Season) : b.OpenedBy,
                        b.Status == SNominating ? Fmt("PhaseNominating", player, string.Join(", ", b.Seats.ToArray()), Span(b.VotingAt - now))
                        : Fmt("PhaseVoting", player, Span(b.ClosesAt - now)));
                else Say(player, "BallotReferendumLine", b.Id, QuestionText(b, player), Span(b.ClosesAt - now));
            }
            Say(player, "HelpVote");
        }

        private void ShowBallot(Player player, Ballot b)
        {
            DateTime now = Now();
            string status = Msg(b.Status == SNominating ? "StatusNominating" : b.Status == SVoting ? "StatusVoting" : b.Status == SClosed ? "StatusClosed" : "StatusCancelled", player);
            Dictionary<string, string> mine;
            b.Votes.TryGetValue(player.Id.ToString(), out mine);
            if (b.Kind == KCouncil)
            {
                string phase = b.Status == SNominating ? Fmt("PhaseNominating", player, string.Join(", ", b.Seats.ToArray()), Span(b.VotingAt - now))
                    : b.Status == SVoting ? Fmt("PhaseVoting", player, Span(b.ClosesAt - now)) : status;
                Say(player, "BallotHeaderCouncil", b.Id, b.Season > 0 ? SeasonLabel(b.Season) : b.OpenedBy, phase);
                foreach (string seat in b.Seats)
                {
                    var names = new List<string>();
                    foreach (Candidate c in b.Candidates) if (c.Seat == seat) names.Add(Fmt("BallotCandidate", player, c.Name, HouseTint(c.House)));
                    Say(player, "BallotSeat", seat, names.Count > 0 ? string.Join(", ", names.ToArray()) : Msg("BallotSeatNone", player));
                }
                if (mine != null && mine.Count > 0)
                {
                    var picks = new List<string>();
                    foreach (KeyValuePair<string, string> kv in mine) { Candidate c = CandidateById(b, kv.Value); if (c != null) picks.Add(kv.Key + ": " + c.Name); }
                    if (picks.Count > 0) Say(player, "BallotYourVote", string.Join(", ", picks.ToArray()));
                }
            }
            else
            {
                Say(player, "BallotHeaderReferendum", b.Id, QuestionText(b, player), b.Status == SVoting ? Fmt("PhaseVoting", player, Span(b.ClosesAt - now)) : status);
                string q;
                if (mine != null && mine.TryGetValue(QKey, out q)) Say(player, "BallotYourVote", Msg(q == "yes" ? "Yes" : "No", player));
            }
            if (b.Status == SVoting) Say(player, "BallotTurnout", b.Votes.Count);
            foreach (string line in b.Results) Say(player, "BallotResultLine", line);
        }

        private Candidate CandidateById(Ballot b, string id)
        {
            foreach (Candidate c in b.Candidates) if (c.Id == id) return c;
            return null;
        }

        private void Results(Player player, string which)
        {
            Ballot b = which != null ? FindBallot(which) : null;
            if (which != null && b == null) { SayError(player, "BallotUnknown", Clean(which, 10)); return; }
            if (b == null)
                for (int i = data.Ballots.Count - 1; i >= 0 && b == null; i--) if (data.Ballots[i].Status == SClosed) b = data.Ballots[i];
            if (b == null) { Say(player, "ResultsNone"); return; }
            ShowBallot(player, b);
        }

        private void History(Player player)
        {
            var closed = new List<Ballot>();
            for (int i = data.Ballots.Count - 1; i >= 0 && closed.Count < 8; i--)
                if (data.Ballots[i].Status == SClosed || data.Ballots[i].Status == SCancelled) closed.Add(data.Ballots[i]);
            if (closed.Count == 0) { Say(player, "ResultsNone"); return; }
            Say(player, "HistoryHeader");
            foreach (Ballot b in closed)
            {
                string what = b.Kind == KCouncil ? "Council election" : QuestionText(b, player);
                string first = b.Results.Count > 0 ? b.Results[0] : Msg(b.Status == SCancelled ? "StatusCancelled" : "StatusClosed", player);
                Say(player, "HistoryLine", b.Id, what, b.ClosedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), first);
            }
        }

        private void Me(Player player)
        {
            string id = player.Id.ToString();
            Observe(player, 0);
            VoterRec r = Voter(id, player.Name, true);
            Say(player, "MeHeader");
            Ballot b = OpenCouncil() ?? OpenReferendum();
            string why = WhyNot(id, b, player);
            if (why == null) Say(player, "MeOk"); else Say(player, "MeNo", why);
            Say(player, "MePlay", r.PlaySeconds / 60, config.Voters.MinPlayMinutes, Span(Now() - r.FirstSeen));
            if (r.House != null) Say(player, "MeHouse", HouseTint(r.House), Span(Now() - r.HouseSince));
        }

        private void Stand(Player player, string seatText)
        {
            Ballot b = OpenCouncil();
            if (b == null || b.Status != SNominating) { SayError(player, "NoElection"); return; }
            if (seatText.Length == 0) { Say(player, "StandUsage", string.Join(", ", b.Seats.ToArray())); return; }
            string seat = MatchSeat(b.Seats, seatText);
            if (seat == null) { SayError(player, "SeatNotElected", Clean(seatText, 40), string.Join(", ", b.Seats.ToArray())); return; }
            string id = player.Id.ToString();
            foreach (Candidate c in b.Candidates) if (c.Id == id) { SayError(player, "StandAlready", c.Seat); return; }
            string house = HouseOf(id);
            string leader = house != null && RealmHouses != null ? RealmHouses.Call("GetHouseLeader", house) as string : null;
            if (house == null || leader != id) { SayError(player, "StandNotLeader"); return; }
            foreach (Candidate c in b.Candidates)
                if (SameHouse(c.House, house)) { SayError(player, "StandHouseAlready", HouseTint(house), c.Name); return; }
            if (!config.Council.MonarchMayStand && IsMonarch(player)) { SayError(player, "StandMonarch"); return; }
            List<string> members = RealmHouses.Call("GetMembers", house) as List<string>;
            int count = members != null ? members.Count : 0;
            if (count < config.Council.CandidateMinHouseMembers) { SayError(player, "StandSmallHouse", HouseTint(house), count, config.Council.CandidateMinHouseMembers); return; }
            if (config.Council.CandidateMinHouseAgeDays > 0)
            {
                string f = RealmHouses.Call("GetHouseFounded", house) as string;
                DateTime founded;
                if (f == null || !DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out founded)
                    || (Now() - founded).TotalDays < config.Council.CandidateMinHouseAgeDays)
                {
                    SayError(player, "StandYoungHouse", HouseTint(house), config.Council.CandidateMinHouseAgeDays);
                    return;
                }
            }
            Observe(player, 0);
            string why = WhyNot(id, b, player);
            if (why != null) { SayError(player, "StandNotVoter", why); return; }
            var cand = new Candidate { Seat = seat, Id = id, Name = Clean(player.Name, 40), House = house, StoodAt = Now() };
            long deposit = config.Council.CandidateDeposit;
            if (deposit > 0)
            {
                if (RealmTreasury == null) { SayError(player, "TreasuryAway"); return; }
                string hold = "heraldry:" + b.Id + ":" + id;
                int minutes = (int)Math.Min(10080, Math.Max(60, (b.ClosesAt - Now()).TotalMinutes + 1440));
                object o = RealmTreasury.Call("HoldMarks", hold, id, player.Name, deposit, Source, minutes);
                long held = o is long ? (long)o : 0;
                if (held != deposit) { SayError(player, "StandDepositFailed", deposit); return; }
                cand.Deposit = held;
                cand.HoldId = hold;
            }
            b.Candidates.Add(cand);
            SaveData();
            Say(player, "Stood", seat, deposit > 0 ? Fmt("StoodDeposit", player, deposit, config.Council.DepositReturnPercent) : "", Span(b.VotingAt - Now()));
            Herald("HeraldStood", cand.Name, HouseTint(house), seat);
        }

        private void Withdraw(Player player)
        {
            string id = player.Id.ToString();
            Ballot b = OpenCouncil();
            Candidate cand = b != null ? CandidateById(b, id) : null;
            if (cand == null) { SayError(player, "WithdrawNone"); return; }
            bool voting = b.Status == SVoting;
            b.Candidates.Remove(cand);
            foreach (Dictionary<string, string> v in b.Votes.Values)
            {
                string pick;
                if (v.TryGetValue(cand.Seat, out pick) && pick == id) v.Remove(cand.Seat);
            }
            SettleDeposit(cand, !voting, false);
            SaveData();
            Say(player, "Withdrawn", cand.Seat, cand.Deposit > 0 ? Fmt(voting ? "WithdrawKept" : "WithdrawRefund", player, cand.Deposit) : "");
            Herald("HeraldWithdrawn", cand.Name, cand.Seat);
        }

        private void Propose(Player player, string[] args)
        {
            if (!config.Referendum.Enabled) { SayError(player, "ReferendumsOff"); return; }
            bool admin = IsAdmin(player);
            if (!IsMonarch(player) && !admin) { SayError(player, "ProposeNotMonarch"); return; }
            if (args.Length < 3) { Say(player, "ProposeUsage"); return; }
            Ballot open = OpenReferendum();
            if (open != null) { SayError(player, "ProposeOpen", open.Id); return; }
            if (OpenBallots().Count >= MaxOpenBallots) { SayError(player, "AdminTooMany"); return; }
            DateTime now = Now();
            if (!admin && data.LastReferendumAt != DateTime.MinValue && now < data.LastReferendumAt.AddHours(config.Referendum.CooldownHours))
            {
                SayError(player, "ProposeCooldown", Span(data.LastReferendumAt.AddHours(config.Referendum.CooldownHours) - now));
                return;
            }
            string kind = args[1].ToLowerInvariant();
            string target = args[2];
            string question, id, name;
            if (kind == "decree")
            {
                if (!config.Referendum.Decrees) { SayError(player, "ProposeOff", "decrees"); return; }
                if (CrownAndConsequences == null) { SayError(player, "CrownAway"); return; }
                string[] decrees = CrownAndConsequences.Call("GetDecreeList") as string[];
                id = null; name = null;
                if (decrees != null)
                    foreach (string row in decrees)
                    {
                        string[] parts = row.Split('|');
                        if (parts.Length >= 2 && string.Equals(parts[0], target, StringComparison.OrdinalIgnoreCase)) { id = parts[0]; name = parts[1]; }
                    }
                if (id == null) { SayError(player, "DecreeUnknown", Clean(target, 40)); return; }
                question = QDecree;
            }
            else if (kind == "law")
            {
                if (!config.Referendum.Laws) { SayError(player, "ProposeOff", "laws"); return; }
                if (RealmLaws == null) { SayError(player, "LawsAway"); return; }
                id = NormalizeLawId(target);
                if (id == null) { SayError(player, "LawBadId"); return; }
                string[] active = RealmLaws.Call("GetActiveLaws") as string[];
                name = null;
                if (active != null)
                    foreach (string row in active)
                    {
                        string[] parts = row.Split('|');
                        if (parts.Length >= 2 && parts[0] == id) name = parts[1];
                    }
                question = name != null ? QRepeal : QProclaim;
                if (name == null) name = id;
            }
            else { Say(player, "ProposeUsage"); return; }
            var b = new Ballot
            {
                Id = data.NextBallotId++, Kind = KReferendum, Status = SVoting, OpenedAt = now, VotingAt = now,
                ClosesAt = now.AddHours(config.Referendum.VotingHours), OpenedBy = Clean(player.Name, 40),
                Question = question, Target = id, TargetName = Clean(name, 60), ProposedById = player.Id.ToString()
            };
            data.Ballots.Add(b);
            if (!admin) data.LastReferendumAt = now;
            SaveData();
            string q = QuestionText(b, null);
            Say(player, "Proposed", b.Id, q, Span(b.ClosesAt - now));
            Herald("HeraldProposed", q, Span(b.ClosesAt - now));
            NotifyVoters(b, Fmt("PopupVoteReferendum", null, q, Span(b.ClosesAt - now)));
        }

        private static string NormalizeLawId(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            if (t.Length < 2 || t.Length > 40) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return null;
            return t;
        }

        [ChatCommand("vote")]
        private void CmdVote(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed || data == null) { SayError(player, "Paused"); return; }
            if (args.Length == 0 || args[0].ToLowerInvariant() == "help") { ListBallots(player); return; }
            string first = args[0].ToLowerInvariant();
            Ballot b = null;
            string choice = null;
            if (first == "yes" || first == "no" || first == "aye" || first == "nay")
            {
                var refs = new List<Ballot>();
                foreach (Ballot x in OpenBallots()) if (x.Kind == KReferendum) refs.Add(x);
                if (refs.Count == 0) { SayError(player, "VoteNothing"); return; }
                if (refs.Count > 1)
                {
                    var ids = new List<string>();
                    foreach (Ballot x in refs) ids.Add("#" + x.Id);
                    SayError(player, "VoteWhich", string.Join(", ", ids.ToArray()));
                    return;
                }
                b = refs[0];
                choice = first == "yes" || first == "aye" ? "yes" : "no";
            }
            else if (args.Length >= 2 && FindBallot(args[0]) != null && FindBallot(args[0]).Kind == KReferendum)
            {
                b = FindBallot(args[0]);
                string c = args[1].ToLowerInvariant();
                if (c != "yes" && c != "no" && c != "aye" && c != "nay") { Say(player, "HelpVote"); return; }
                choice = c == "yes" || c == "aye" ? "yes" : "no";
            }
            if (b != null) { CastQuestion(player, b, choice); return; }
            Ballot council = OpenCouncil();
            if (council == null) { SayError(player, "VoteNothing"); return; }
            if (council.Status == SNominating) { SayError(player, "VoteNotYet", council.Id, Span(council.VotingAt - Now())); return; }
            string text = JoinFrom(args, 0);
            Ballot named = FindBallot(args[0]);
            if (named != null && named == council && args.Length > 1) text = JoinFrom(args, 1);
            string ambiguous;
            Candidate cand = FindCandidate(council, text, out ambiguous);
            if (ambiguous != null) { SayError(player, "VoteCandidateAmbiguous", Clean(text, 40), ambiguous); return; }
            if (cand == null) { SayError(player, "VoteCandidateUnknown", Clean(text, 40)); return; }
            CastCouncil(player, council, cand);
        }

        // A candidate by exact name, exact house, then a unique start of either.
        private Candidate FindCandidate(Ballot b, string text, out string ambiguous)
        {
            ambiguous = null;
            string t = (text ?? "").Trim();
            if (t.StartsWith("House ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(6).Trim();
            if (t.Length == 0) return null;
            foreach (Candidate c in b.Candidates) if (string.Equals(c.Name, t, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (Candidate c in b.Candidates) if (string.Equals(c.House, t, StringComparison.OrdinalIgnoreCase)) return c;
            var hits = new List<Candidate>();
            foreach (Candidate c in b.Candidates)
                if (c.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase) || c.House.StartsWith(t, StringComparison.OrdinalIgnoreCase)) hits.Add(c);
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                var names = new List<string>();
                foreach (Candidate c in hits) names.Add(c.Name);
                ambiguous = string.Join(", ", names.ToArray());
            }
            return null;
        }

        private bool MayCast(Player player, Ballot b)
        {
            string id = player.Id.ToString();
            Observe(player, 0);
            string why = WhyNot(id, b, player);
            if (why != null) { SayError(player, "MeNo", why); return false; }
            return true;
        }

        private void CastCouncil(Player player, Ballot b, Candidate cand)
        {
            if (!MayCast(player, b)) return;
            string id = player.Id.ToString();
            Dictionary<string, string> mine;
            bool firstVote = !b.Votes.TryGetValue(id, out mine);
            if (firstVote) { mine = new Dictionary<string, string>(); b.Votes[id] = mine; }
            string old;
            if (mine.TryGetValue(cand.Seat, out old))
            {
                if (old == cand.Id) { Say(player, "VoteSame"); return; }
                if (!config.Council.AllowVoteChange) { SayError(player, "VoteNoChange", cand.Seat); return; }
            }
            mine[cand.Seat] = cand.Id;
            SaveData();
            Say(player, config.Council.AllowVoteChange ? "Voted" : "VotedFinal", cand.Name, cand.Seat);
            if (firstVote && RealmQuests != null) RealmQuests.Call("ReportQuestEvent", id, "custom", "vote_cast", 1);
        }

        private void CastQuestion(Player player, Ballot b, string choice)
        {
            if (b.Status != SVoting) { SayError(player, "AdminNotOpen", b.Id); return; }
            if (!MayCast(player, b)) return;
            string id = player.Id.ToString();
            Dictionary<string, string> mine;
            bool firstVote = !b.Votes.TryGetValue(id, out mine);
            if (firstVote) { mine = new Dictionary<string, string>(); b.Votes[id] = mine; }
            string old;
            if (mine.TryGetValue(QKey, out old))
            {
                if (old == choice) { Say(player, "VoteSame"); return; }
                if (!config.Council.AllowVoteChange) { SayError(player, "VoteNoChange", "#" + b.Id); return; }
            }
            mine[QKey] = choice;
            SaveData();
            Say(player, "VotedQuestion", Msg(choice == "yes" ? "Yes" : "No", player), b.Id);
            if (firstVote && RealmQuests != null) RealmQuests.Call("ReportQuestEvent", id, "custom", "vote_cast", 1);
        }

        private void AdminBallot(Player player, string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            DateTime now = Now();
            if (sub == "open")
            {
                if (!config.Council.Enabled) { SayError(player, "AdminCouncilOff"); return; }
                string refusal;
                int season = 0;
                if (RealmSeasons != null) { object o = RealmSeasons.Call("GetSeasonNumber"); if (o is int) season = (int)o; }
                Ballot b = OpenElection(now, player.Name, season, out refusal);
                if (b == null) { SayError(player, "AdminCannotOpen", refusal); return; }
                if (season > 0) { data.LastElectionSeason = season; data.SeasonBaselined = true; SaveData(); }
                Say(player, "AdminOpened", b.Id, Span(b.VotingAt - now), config.Council.VotingHours);
                return;
            }
            if (sub == "voter")
            {
                string vid = FindVoterId(JoinFrom(args, 2));
                if (vid == null) { SayError(player, "PlayerUnknown", Clean(JoinFrom(args, 2), 40)); return; }
                VoterRec r = Voter(vid, null, false);
                Say(player, "VoterHeader", r.Name, Span(now - r.FirstSeen), r.PlaySeconds / 60, r.House != null ? HouseTint(r.House) : "-",
                    r.House != null ? Span(now - r.HouseSince) : "-");
                string why = WhyNot(vid, OpenCouncil() ?? OpenReferendum(), player);
                if (why == null) Say(player, "VoterOk"); else Say(player, "VoterNo", why);
                return;
            }
            Ballot t = args.Length > 2 ? FindBallot(args[2]) : null;
            if (sub != "advance" && sub != "cancel" && sub != "strike" && sub != "audit") { HelpBallot(player); return; }
            if (t == null) { SayError(player, "BallotUnknown", args.Length > 2 ? Clean(args[2], 10) : "?"); return; }
            if (sub == "audit") { Audit(player, t); return; }
            if (t.Status != SNominating && t.Status != SVoting) { SayError(player, "AdminNotOpen", t.Id); return; }
            if (sub == "advance")
            {
                if (t.Status == SNominating) { t.VotingAt = now; StartVoting(t, now); }
                else CloseBallot(t, now);
                Say(player, "AdminAdvanced", t.Id, Msg(t.Status == SVoting ? "StatusVoting" : "StatusClosed", player));
            }
            else if (sub == "cancel")
            {
                int refunds = 0;
                foreach (Candidate c in t.Candidates) if (c.Deposit > 0 && !string.IsNullOrEmpty(c.HoldId)) { SettleDeposit(c, true, true); refunds++; }
                t.Status = SCancelled;
                t.ClosedAt = now;
                t.Results.Add(Fmt("HeraldCancelled", null, t.Id));
                Trim();
                SaveData();
                Herald("HeraldCancelled", t.Id);
                Say(player, "AdminCancelled", t.Id, refunds);
            }
            else if (sub == "strike")
            {
                string vid = FindVoterId(JoinFrom(args, 3));
                if (vid == null) { SayError(player, "PlayerUnknown", Clean(JoinFrom(args, 3), 40)); return; }
                string name = Voter(vid, null, false).Name;
                bool had = t.Votes.Remove(vid);
                if (!t.Struck.Contains(vid)) t.Struck.Add(vid);
                SaveData();
                Puts("Vote of " + name + " (" + vid + ") in ballot #" + t.Id + " struck by " + player.Name);
                Say(player, had ? "AdminStruck" : "AdminStrikeNone", name, t.Id);
            }
        }

        // Votes per house and the least-played voters, for staff looking for a bloc of alts.
        private void Audit(Player player, Ballot b)
        {
            DateTime now = Now();
            Say(player, "AuditHeader", b.Id, b.Votes.Count, b.Struck.Count);
            var byHouse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var voters = new List<KeyValuePair<string, VoterRec>>();
            int none = 0;
            foreach (string id in b.Votes.Keys)
            {
                VoterRec r = Voter(id, null, false);
                if (r == null) continue;
                voters.Add(new KeyValuePair<string, VoterRec>(id, r));
                if (r.House == null) { none++; continue; }
                int n;
                byHouse.TryGetValue(r.House, out n);
                byHouse[r.House] = n + 1;
            }
            foreach (KeyValuePair<string, int> kv in byHouse) Say(player, "AuditHouse", HouseTint(kv.Key), kv.Value);
            if (none > 0) Say(player, "AuditNoHouse", none);
            voters.Sort(delegate(KeyValuePair<string, VoterRec> x, KeyValuePair<string, VoterRec> y) { return x.Value.PlaySeconds.CompareTo(y.Value.PlaySeconds); });
            var young = new List<string>();
            for (int i = 0; i < voters.Count && i < 6; i++)
            {
                VoterRec r = voters[i].Value;
                young.Add(Fmt("AuditVoter", player, r.Name, r.PlaySeconds / 60, r.House != null ? (int)(now - r.HouseSince).TotalHours : 0));
            }
            if (young.Count > 0) Say(player, "AuditYoung", string.Join(", ", young.ToArray()));
        }

        private string FindVoterId(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            ulong u;
            if (ulong.TryParse(t, out u) && data.Voters.ContainsKey(t)) return t;
            foreach (Player p in OnlinePlayers()) if (string.Equals(p.Name, t, StringComparison.OrdinalIgnoreCase)) return p.Id.ToString();
            string hit = null;
            foreach (KeyValuePair<string, VoterRec> kv in data.Voters)
                if (string.Equals(kv.Value.Name, t, StringComparison.OrdinalIgnoreCase)) { if (hit != null) return null; hit = kv.Key; }
            if (hit != null) return hit;
            foreach (KeyValuePair<string, VoterRec> kv in data.Voters)
                if (kv.Value.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) { if (hit != null) return null; hit = kv.Key; }
            return hit;
        }

        private static string MatchSeat(List<string> seats, string input)
        {
            string t = (input ?? "").Trim();
            if (t.Length == 0) return null;
            foreach (string s in seats) if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) return s;
            string hit = null;
            foreach (string s in seats)
                if (s.StartsWith(t, StringComparison.OrdinalIgnoreCase)) { if (hit != null) return null; hit = s; }
            return hit;
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): a notice with an Ok button when the realm starts voting, to
        // each online player who may vote. The game's window:
        //   ShowPopup(this Player, string title, string message, string buttonText, Dialogue.OnSubmit handler,
        //             bool interupt, bool broadcast)   [ASM CodeHatch.Common.PlayerExtensions]
        // broadcast = true or a dedicated server never sends it. The Herald's chat line is always sent as well.
        // UNVERIFIED in game: that the window shows, how much text fits, and that "\n" breaks lines in it.
        private void NotifyVoters(Ballot b, string message)
        {
            if (popupsClosed) return;
            foreach (Player p in OnlinePlayers())
            {
                if (!PopupsFor(p) || WhyNot(p.Id.ToString(), b, p) != null) continue;
                ShowNotice(p, Msg("PopupVoteTitle", p), message);
            }
        }

        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.General.UsePopups) return false;
            if (RealmHerald == null) return true;
            object wanted = RealmHerald.Call("PopupsWanted", player.Id.ToString());
            return !(wanted is bool) || (bool)wanted;
        }

        private bool ShowNotice(Player player, string title, string message)
        {
            try
            {
                player.ShowPopup(PopupText(title), PopupText(message), PopupText(Msg("PopupOk", player)), null, false, true);
                return true;
            }
            catch (Exception ex)
            {
                if (Throttle("popup", 300)) PrintWarning("ShowPopup failed (" + ex.Message + "); the chat version stands.");
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

        // "pairId|#field|#charge|pair name" of the colours a house bears, or null for an unknown house.
        private string GetHouseArms(string house)
        {
            if (data == null || string.IsNullOrEmpty(house)) return null;
            List<string> houses = HouseNames();
            if (houses != null && !ContainsIgnoreCase(houses, house)) return null;
            bool chosen;
            ArmsPair p = PairOf(CanonicalHouse(house), out chosen);
            return p.Id + "|" + p.Field + "|" + p.Charge + "|" + p.Name;
        }

        // Whether a player may vote now (the general rules; a ballot may add "joined before it opened").
        private bool IsEligibleVoter(string playerId)
        {
            return data != null && playerId != null && WhyNot(playerId, null, null) == null;
        }

        // One plain line on what the realm votes on now, for a board or a page; null when nothing is open.
        private string GetBallotSummary()
        {
            if (data == null) return null;
            List<Ballot> open = OpenBallots();
            if (open.Count == 0) return null;
            Ballot b = open[0];
            DateTime now = Now();
            if (b.Kind == KReferendum) return PlainText(QuestionText(b, null)) + " Voting ends in " + Span(b.ClosesAt - now) + ".";
            return b.Status == SNominating
                ? "Council election: heads of houses stand for " + string.Join(", ", b.Seats.ToArray()) + " for " + Span(b.VotingAt - now) + "."
                : "Council election: " + PlainText(CandidateList(b)) + ". Voting ends in " + Span(b.ClosesAt - now) + ".";
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

        private bool Throttle(string key, int seconds)
        {
            DateTime last;
            DateTime now = Now();
            if (throttles.TryGetValue(key, out last) && (now - last).TotalSeconds < seconds) return false;
            throttles[key] = now;
            return true;
        }

        private static List<Player> OnlinePlayers()
        {
            var list = new List<Player>();
            if (Server.ClientPlayers == null) return list;
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private static Player Online(string id)
        {
            foreach (Player p in OnlinePlayers()) if (p.Id.ToString() == id) return p;
            return null;
        }

        private string HouseOf(string playerId)
        {
            if (RealmHouses == null || playerId == null) return null;
            string h = RealmHouses.Call("GetHouse", playerId) as string;
            return string.IsNullOrEmpty(h) ? null : h;
        }

        private List<string> HouseNames()
        {
            if (RealmHouses == null) return null;
            List<Dictionary<string, object>> rows = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
            if (rows == null) return null;
            var names = new List<string>();
            foreach (Dictionary<string, object> r in rows)
            {
                object n;
                if (r != null && r.TryGetValue("name", out n) && n is string && ((string)n).Length > 0) names.Add((string)n);
            }
            return names;
        }

        private string CanonicalHouse(string house)
        {
            string t = (house ?? "").Trim();
            if (t.StartsWith("House ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(6).Trim();
            if (t.Length == 0) return null;
            List<string> names = HouseNames();
            if (names != null) foreach (string n in names) if (string.Equals(n, t, StringComparison.OrdinalIgnoreCase)) return n;
            return t;
        }

        private static bool SameHouse(string a, string b)
        {
            if (a == null || b == null) return a == b;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string HouseKey(string house)
        {
            return (house ?? "").Trim().ToLowerInvariant();
        }

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
        }

        // Plain text for the Chronicle: chat colour tags out.
        private static string PlainText(string text)
        {
            return PopupText(text);
        }

        private static string NormalizeId(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant().Replace(' ', '-');
            if (t.Length == 0 || t.Length > 40) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return null;
            return t;
        }

        private static bool ParseHex(string hex, out int r, out int g, out int b)
        {
            r = g = b = 0;
            string t = (hex ?? "").Trim().TrimStart('#');
            if (t.Length != 6 || !IsChatHex(t)) return false;
            r = int.Parse(t.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            g = int.Parse(t.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            b = int.Parse(t.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        // Player text for chat and files: no control characters, no brackets (no colour tags), at most max characters.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsControl(c) || c == '[' || c == ']') continue;
                sb.Append(c);
            }
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max).TrimEnd() : t;
        }

        private static string JoinFrom(string[] args, int start)
        {
            return start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start).Trim();
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            if (list == null || value == null) return false;
            foreach (string s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Span(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            if (t.TotalDays >= 1) return (int)t.TotalDays + " d " + t.Hours + " h";
            if (t.TotalHours >= 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
            return Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) + " min";
        }

        #endregion
    }
}
