// RealmRenown: renown, infamy and earned titles for the players of Ostreval.
//
// Every deed a player does is scored as renown (glory) and/or infamy (shame). Deeds come only from sources that were
// read in code; nothing is guessed from chat. Tags as in docs/oxide-rok-api.md:
//   reign_day           the reigning monarch, per ReignDayHours held. KingsScheme.GetKingID()/GetKingName() polled
//                       every TickSeconds [ASM; USE RaidBoss.cs:496]. Time only counts while the server runs.
//   kingslayer          OnEntityDeath(EntityDeathEvent) [OPJ L188]; victim = evt.Entity.Owner, killer =
//                       evt.KillingDamage.DamageSource.Owner [ASM; USE DeathMessages.cs:20], victim = the monarch
//                       (current KingsScheme king, or the king seen within KingslayerGraceSeconds, because the order of
//                       the death hook and the throne release is UNVERIFIED).
//   crown_seized,       CrownAndConsequences.Call("GetOpenClaims") / ("GetKingHouse") polled [plugin source]; members
//   kingmaker,          (and sworn vassals, via RealmHouses GetHouse/GetLiege) of the claimant and crown houses who were
//   crown_defended,     ONLINE during the Lawful Hours are the participants. When an active claim ends: claimant house
//   rebellion_defended  wears the crown -> crown_seized (the new monarch) + kingmaker (the rest); the crown house still
//                       holds it -> crown_defended (the monarch) + rebellion_defended (the rest).
//   contract_*          oxide/data/RealmContracts.json read-only (contracts with Status "done": FulfillerId, Type)
//                       [plugin source RealmContracts.cs StoredData]. RealmContracts has no API for this.
//   oath_broken,        oxide/data/RealmHouses.json read-only: per-player PlayerRecord.OathsBroken / TreatiesBroken
//   treaty_broken       deltas [plugin source RealmHouses.cs:109, :776, :887]. RealmHouses has no per-player API.
//   outlawed            RealmLaws.Call("GetCourtOutlaws") and RealmContracts.Call("IsOutlaw", id) [plugin source].
//   tournament_win,     oxide/data/RealmChronicle.json read-only, by event id cursor: tournament_champion, hunt_kill,
//   quarry_taken,       truce_broken; the doer is actors[0] [plugin source RealmEvents.cs], mapped from name to id
//   truce_broken        through the players this plugin has seen (ambiguous names are skipped).
// Other files are only ever READ (ExistsDatafile first, so DataFileSystem.ReadObject never creates them) and a file
// that fails to parse is skipped for that poll.
//
// Titles are earned once (kept even if infamy later decays) from configurable requirements. A chosen title is shown
// in global chat by prefixing Player.ChatFormat. [IL] PlayerChatEvent.ChatMessage formats Player.ChatFormat
// ("%name% : %message%" by default, Permission.cs) with Replace; Player.ChatFormat is a public [Syncable] property and
// the game itself changes it at runtime (CoreServer.UpdatePlayerData after the admin "chatformat" command), so the
// change reaches clients through SyncManager. UNVERIFIED on a live server: that the synced format shows on clients.
// The prefix is re-applied every tick (the game may reset the format) and removed on unload.
//
// Abuse limits: per-deed cooldowns, a daily renown and infamy cap per player, contract and chronicle feeds capped per
// poll, mark deltas capped per poll, a title-change cooldown, a command cooldown, announcement and chronicle caps per
// hour, an admin adjustment cap, and bounded data (players, deed logs, credited contracts).
//
// Data: oxide/data/RealmRenown.json. If it exists but cannot be parsed the plugin keeps no score and NEVER writes the
// file, so a damaged record is not overwritten (fix or remove it, then reload).
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using System.Globalization;
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Networking.Events.Entities;      // EntityDeathEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // KingsScheme [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmRenown", "Realm", "0.1.0")]
    [Description("Renown and infamy from deeds, earned titles, a chosen title as a chat prefix, and the realm's roll of honour")]
    public class RealmRenown : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmContracts;
        [PluginReference] private Plugin RealmLaws;

        private const string PermAdmin = "realmrenown.admin";
        private const string DataName = "RealmRenown";
        private const string ChronicleFile = "RealmChronicle";
        private const string ContractsFile = "RealmContracts";
        private const string HousesFile = "RealmHouses";
        private const string TitleEventType = "title_earned";
        private const int NameMax = 48;

        // Deed kinds known to the code (the config may tune their points but not invent kinds without a source,
        // except through the AddDeed API).
        private static readonly string[] BuiltInDeeds =
        {
            "reign_day", "kingslayer", "crown_seized", "kingmaker", "crown_defended", "rebellion_defended",
            "contract_bounty", "contract_delivery", "contract_merc", "tournament_win", "quarry_taken",
            "oath_broken", "treaty_broken", "outlawed", "truce_broken"
        };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private bool dirty;
        private Timer tickTimer;
        private DateTime lastTick = DateTime.MinValue;
        private DateTime lastFeedPoll = DateTime.MinValue;
        private DateTime lastSave = DateTime.MinValue;
        private ulong cachedKingId;
        private string cachedKingName;
        private DateTime cachedKingSeen = DateTime.MinValue;
        private bool chronicleTypeRejected;
        private Func<DateTime> clock = DefaultClock;   // replaced only by the offline behaviour tests

        // In-memory only (losing them on reload is harmless).
        private readonly Dictionary<ulong, string> appliedPrefix = new Dictionary<ulong, string>();
        private readonly Dictionary<string, DateTime> lastCommand = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> resetConfirm = new Dictionary<string, DateTime>();
        private readonly Queue<DateTime> announceTimes = new Queue<DateTime>();
        private readonly Queue<DateTime> chronicleTimes = new Queue<DateTime>();
        private readonly Dictionary<string, string> feedStatus = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> courtOutlawIds = new Dictionary<string, bool>();

        #region Config

        private class DeedDef
        {
            public string Label;
            public int Renown;
            public int Infamy;
            public int CooldownMinutes;
        }

        private class TitleDef
        {
            public string Id;
            public string Name;
            public string Description;
            public bool Infamous;                       // shown in the infamy colour
            public Dictionary<string, int> Requires;    // deed kind (count), "renown" or "infamy" (current points)
        }

        private class PluginConfig
        {
            public float TickSeconds = 30f;
            public float FeedPollSeconds = 60f;
            public int MaxRenownPerDay = 400;
            public int MaxInfamyPerDay = 400;
            public int InfamyDecayPerDay = 5;
            public float ReignDayHours = 24f;
            public int KingslayerGraceSeconds = 60;
            public int RebellionResolveGraceMinutes = 30;
            public bool CountHistoryOnFirstRun = false;
            public bool ChatPrefixEnabled = true;
            public string ChatPrefixFormat = "[D6A043]{title}[-] ";
            public string InfamousPrefixFormat = "[E86A5C]{title}[-] ";
            public int TitleChangeCooldownSeconds = 60;
            public int CommandCooldownSeconds = 2;
            public int TopCount = 10;
            public bool AnnounceTitles = true;
            public int MaxAnnouncementsPerHour = 6;
            public bool ChronicleTitles = true;
            public int MaxChronicleTitlesPerHour = 4;
            public int MaxPlayers = 5000;
            public int DeedLogPerPlayer = 10;
            public int MaxAwardsPerPoll = 100;
            public int MaxMarkDeltaPerPoll = 3;
            public int AdminMaxAdjust = 1000;
            public bool FeedReign = true;
            public bool FeedKingslayer = true;
            public bool FeedRebellions = true;
            public bool FeedContracts = true;
            public bool FeedHouses = true;
            public bool FeedOutlaws = true;
            public bool FeedChronicle = true;
            // Alt-farming limits (a player can post a cheap delivery and fill it with a second account, or two friends can
            // trade fills all day): one contract deed per poster/fulfiller pair per window, and a daily count per player.
            public int ContractPairCooldownHours = 24;
            public int MaxContractDeedsPerDay = 3;
            // A rebellion defended only counts when at least this many claimants were seen online in the window: a lone
            // alt's "claim" is not a rebellion worth renown to the crown's side.
            public int RebellionMinClaimants = 2;
            public Dictionary<string, string> ChronicleDeeds;  // chronicle event type -> deed kind (doer = actors[0])
            public Dictionary<string, DeedDef> Deeds;
            public List<TitleDef> Titles;
        }

        private static DeedDef D(string label, int renown, int infamy, int cooldown)
        {
            return new DeedDef { Label = label, Renown = renown, Infamy = infamy, CooldownMinutes = cooldown };
        }

        private static Dictionary<string, DeedDef> DefaultDeeds()
        {
            var d = new Dictionary<string, DeedDef>();
            d.Add("reign_day", D("a day upon the Old Throne", 40, 0, 0));
            d.Add("kingslayer", D("slew the reigning monarch", 60, 40, 360));
            d.Add("crown_seized", D("took the crown in the Lawful Hours", 120, 0, 0));
            d.Add("kingmaker", D("won the crown for their house", 60, 0, 0));
            d.Add("crown_defended", D("held the crown against a rebellion", 100, 0, 0));
            d.Add("rebellion_defended", D("stood for the crown against a rebellion", 40, 0, 0));
            d.Add("contract_bounty", D("collected a bounty", 25, 0, 20));
            d.Add("contract_delivery", D("filled an order", 15, 0, 30));
            d.Add("contract_merc", D("fought as a hired sword", 30, 0, 0));
            d.Add("tournament_win", D("won the Royal Tournament", 80, 0, 0));
            d.Add("quarry_taken", D("took the King's quarry", 30, 0, 0));
            d.Add("oath_broken", D("broke a sworn oath", 0, 60, 0));
            d.Add("treaty_broken", D("tore up a treaty", 0, 40, 0));
            d.Add("outlawed", D("was declared outlaw", 0, 50, 720));
            d.Add("truce_broken", D("broke the Truce of the Realm", 0, 30, 0));
            d.Add("holding_taken", D("took a holding for their house", 30, 0, 60));   // RealmDominion (AddDeed)
            // Reported by RealmQuests through AddDeed (one dedupe key per task, step or tier).
            d.Add("quest_daily", D("finished a daily task", 5, 0, 0));
            d.Add("quest_weekly", D("finished a weekly task", 15, 0, 0));
            d.Add("quest_story", D("followed the season's tale", 20, 0, 0));
            d.Add("story_complete", D("saw the season's tale to its end", 100, 0, 0));
            d.Add("achievement", D("earned a deed of note", 10, 0, 0));
            d.Add("achievement_gold", D("earned a gold deed", 30, 0, 0));
            d.Add("house_goal", D("met a weekly goal with their house", 15, 0, 0));
            return d;
        }

        private static TitleDef T(string id, string name, string desc, bool infamous, string key, int min)
        {
            var req = new Dictionary<string, int>();
            req.Add(key, min);
            return new TitleDef { Id = id, Name = name, Description = desc, Infamous = infamous, Requires = req };
        }

        private static List<TitleDef> DefaultTitles()
        {
            var t = new List<TitleDef>();
            t.Add(T("kingslayer", "Kingslayer", "Slew a reigning monarch.", false, "kingslayer", 1));
            t.Add(T("usurper", "Usurper", "Took the crown in the Lawful Hours.", false, "crown_seized", 1));
            t.Add(T("kingmaker", "Kingmaker", "Won the crown for their house without wearing it.", false, "kingmaker", 1));
            t.Add(T("unbowed", "The Unbowed", "Held the crown against a declared rebellion.", false, "crown_defended", 1));
            t.Add(T("shield_of_crown", "Shield of the Crown", "Stood for the crown against three rebellions.", false, "rebellion_defended", 3));
            t.Add(T("long_reign", "The Long Reign", "Sat seven days upon the Old Throne.", false, "reign_day", 7));
            t.Add(T("warden_of_roads", "Warden of Roads", "Filled five orders for the realm's traders.", false, "contract_delivery", 5));
            t.Add(T("sellsword", "Sellsword", "Fought three times as a hired sword.", false, "contract_merc", 3));
            t.Add(T("headtaker", "Headtaker", "Collected three bounties.", false, "contract_bounty", 3));
            t.Add(T("champion", "Champion of the Lists", "Won the Royal Tournament.", false, "tournament_win", 1));
            t.Add(T("huntsman", "Crown's Huntsman", "Took the King's quarry three times.", false, "quarry_taken", 3));
            t.Add(T("renowned", "the Renowned", "Gathered 500 renown.", false, "renown", 500));
            t.Add(T("legend", "Legend of Ostreval", "Gathered 2000 renown.", false, "renown", 2000));
            t.Add(T("oathbreaker", "Oathbreaker", "Broke a sworn oath.", true, "oath_broken", 1));
            t.Add(T("faithless", "The Faithless", "Tore up two treaties.", true, "treaty_broken", 2));
            t.Add(T("trucebreaker", "Trucebreaker", "Broke the Truce of the Realm.", true, "truce_broken", 1));
            t.Add(T("hunted", "The Hunted", "Was declared outlaw.", true, "outlawed", 1));
            t.Add(T("black_name", "Black Name", "Carried 300 infamy at once.", true, "infamy", 300));
            return t;
        }

        private static Dictionary<string, string> DefaultChronicleDeeds()
        {
            var m = new Dictionary<string, string>();
            m.Add("tournament_champion", "tournament_win");
            m.Add("hunt_kill", "quarry_taken");
            m.Add("truce_broken", "truce_broken");
            return m;
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.ChronicleDeeds = DefaultChronicleDeeds();
            c.Deeds = DefaultDeeds();
            c.Titles = DefaultTitles();
            return c;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static float ClampF(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private void SanitizeConfig()
        {
            PluginConfig c = config;
            c.TickSeconds = ClampF(c.TickSeconds, 5f, 300f);
            c.FeedPollSeconds = ClampF(c.FeedPollSeconds, 10f, 3600f);
            c.MaxRenownPerDay = Clamp(c.MaxRenownPerDay, 0, 100000);
            c.MaxInfamyPerDay = Clamp(c.MaxInfamyPerDay, 0, 100000);
            c.InfamyDecayPerDay = Clamp(c.InfamyDecayPerDay, 0, 10000);
            c.ReignDayHours = ClampF(c.ReignDayHours, 1f, 720f);
            c.KingslayerGraceSeconds = Clamp(c.KingslayerGraceSeconds, 0, 600);
            c.RebellionResolveGraceMinutes = Clamp(c.RebellionResolveGraceMinutes, 1, 1440);
            c.TitleChangeCooldownSeconds = Clamp(c.TitleChangeCooldownSeconds, 0, 86400);
            c.CommandCooldownSeconds = Clamp(c.CommandCooldownSeconds, 0, 60);
            c.TopCount = Clamp(c.TopCount, 1, 25);
            c.MaxAnnouncementsPerHour = Clamp(c.MaxAnnouncementsPerHour, 0, 120);
            c.MaxChronicleTitlesPerHour = Clamp(c.MaxChronicleTitlesPerHour, 0, 60);
            c.MaxPlayers = Clamp(c.MaxPlayers, 50, 100000);
            c.DeedLogPerPlayer = Clamp(c.DeedLogPerPlayer, 0, 50);
            c.MaxAwardsPerPoll = Clamp(c.MaxAwardsPerPoll, 1, 1000);
            c.MaxMarkDeltaPerPoll = Clamp(c.MaxMarkDeltaPerPoll, 1, 50);
            c.ContractPairCooldownHours = Clamp(c.ContractPairCooldownHours, 0, 8760);
            c.MaxContractDeedsPerDay = Clamp(c.MaxContractDeedsPerDay, 0, 1000);
            c.RebellionMinClaimants = Clamp(c.RebellionMinClaimants, 0, 100);
            c.AdminMaxAdjust = Clamp(c.AdminMaxAdjust, 1, 100000);
            if (!ValidPrefixFormat(c.ChatPrefixFormat)) c.ChatPrefixFormat = "[D6A043]{title}[-] ";
            if (!ValidPrefixFormat(c.InfamousPrefixFormat)) c.InfamousPrefixFormat = "[E86A5C]{title}[-] ";

            // Deeds: built-in kinds always exist; values are clamped; unknown kinds are kept (usable through AddDeed).
            Dictionary<string, DeedDef> defaults = DefaultDeeds();
            var deeds = new Dictionary<string, DeedDef>();
            if (c.Deeds != null)
                foreach (KeyValuePair<string, DeedDef> kv in c.Deeds)
                {
                    string k = NormalizeId(kv.Key);
                    if (k == null || kv.Value == null || deeds.ContainsKey(k)) continue;
                    DeedDef d = kv.Value;
                    d.Renown = Clamp(d.Renown, 0, 10000);
                    d.Infamy = Clamp(d.Infamy, 0, 10000);
                    d.CooldownMinutes = Clamp(d.CooldownMinutes, 0, 525600);
                    d.Label = CleanText(d.Label, 60);
                    if (d.Label.Length == 0) d.Label = k;
                    deeds[k] = d;
                }
            foreach (KeyValuePair<string, DeedDef> kv in defaults) if (!deeds.ContainsKey(kv.Key)) deeds[kv.Key] = kv.Value;
            c.Deeds = deeds;

            var chron = new Dictionary<string, string>();
            Dictionary<string, string> chronSrc = c.ChronicleDeeds ?? DefaultChronicleDeeds();
            foreach (KeyValuePair<string, string> kv in chronSrc)
            {
                string type = NormalizeId(kv.Key);
                string kind = NormalizeId(kv.Value);
                if (type == null || kind == null || !deeds.ContainsKey(kind)) { PrintWarning("ChronicleDeeds entry '" + kv.Key + "' ignored"); continue; }
                chron[type] = kind;
            }
            c.ChronicleDeeds = chron;

            var titles = new List<TitleDef>();
            var seenIds = new Dictionary<string, bool>();
            var seenNames = new Dictionary<string, bool>();
            foreach (TitleDef t in c.Titles ?? DefaultTitles())
            {
                if (t == null) continue;
                string id = NormalizeId(t.Id);
                string why = null;
                if (id == null) why = "bad id";
                else if (!ValidTitleName(t.Name)) why = "bad name (3-24 letters, digits, spaces, ' or -)";
                else if (seenIds.ContainsKey(id) || seenNames.ContainsKey(t.Name.ToLowerInvariant())) why = "duplicate";
                else if (t.Requires == null || t.Requires.Count == 0) why = "no requirements";
                else
                {
                    var req = new Dictionary<string, int>();
                    foreach (KeyValuePair<string, int> r in t.Requires)
                    {
                        string key = NormalizeId(r.Key);
                        if (key == null || (key != "renown" && key != "infamy" && !deeds.ContainsKey(key))) { why = "unknown requirement '" + r.Key + "'"; break; }
                        req[key] = Clamp(r.Value, 1, 1000000);
                    }
                    t.Requires = req;
                }
                if (why != null) { PrintWarning("Title '" + (t.Id ?? "?") + "' ignored: " + why); continue; }
                t.Id = id;
                t.Description = CleanText(t.Description, 120);
                seenIds[id] = true;
                seenNames[t.Name.ToLowerInvariant()] = true;
                titles.Add(t);
                if (titles.Count >= 60) break;
            }
            c.Titles = titles;
        }

        private static bool ValidPrefixFormat(string f)
        {
            return f != null && f.Length <= 60 && f.IndexOf("{title}", StringComparison.Ordinal) >= 0 && f.IndexOf('%') < 0;
        }

        #endregion

        #region Data

        private class DeedEntry
        {
            public DateTime At;
            public string Kind;
            public int Renown;
            public int Infamy;
            public string Note;
        }

        private class PlayerData
        {
            public string Name;
            public DateTime LastSeen;
            public int Renown;
            public int Infamy;
            public double ReignSeconds;
            public string DayKey;
            public int RenownToday;
            public int InfamyToday;
            public int ContractDeedsToday;
            public string Chosen;
            public DateTime ChosenAt;
            public Dictionary<string, int> Stats;
            public Dictionary<string, DateTime> Titles;
            public Dictionary<string, DateTime> Cooldowns;
            public List<DeedEntry> Log;
        }

        private class ClaimWatch
        {
            public string Key;
            public string House;
            public DateTime Start;
            public DateTime End;
            public bool SawActive;
            public string CrownHouseAtStart;
            public List<string> Claimants;
            public List<string> Defenders;
        }

        private class Marks
        {
            public int Oaths;
            public int Treaties;
        }

        private class StoredData
        {
            public Dictionary<string, PlayerData> Players;
            public List<ClaimWatch> Claims;
            public int ChronicleCursor = -1;
            public bool ContractsBaselined;
            public int ContractsNextId;
            public List<int> CreditedContracts;
            public bool HousesBaselined;
            public Dictionary<string, Marks> HouseMarks;
            public bool OutlawsBaselined;
            public List<string> OutlawsNow;
            public List<string> OutlawChecked;    // players whose RealmContracts outlaw status was read at least once
            public string LastDecayDay;
        }

        // Read-only views of other plugins' files: only the fields used here.
        private class ChronEventLite
        {
            public int id;
            public string type;
            public string title;
            public string[] actors;
        }

        private class ContractLite
        {
            public int Id;
            public string Type;
            public string Status;
            public string FulfillerId;
            public string FulfillerName;
            public string PosterId;
        }

        private class ContractsFileLite
        {
            public int NextId;
            public List<ContractLite> Contracts;
        }

        private class HouseRecordLite
        {
            public string Name;
            public int OathsBroken;
            public int TreatiesBroken;
        }

        private class HousesFileLite
        {
            public Dictionary<string, HouseRecordLite> Players;
        }

        private void LoadData()
        {
            StoredData loaded = null;
            bool existed = false;
            try
            {
                existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
                if (existed) loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". Renown is paused and the file will NOT be overwritten. Fix or remove it, then reload.");
                return;
            }
            if (existed && loaded == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or null. Renown is paused and the file will NOT be overwritten.");
                return;
            }
            data = loaded ?? new StoredData();
            Normalize(data);
            if (!existed) { dirty = true; SaveData(); }
        }

        private static void Normalize(StoredData d)
        {
            if (d.Players == null) d.Players = new Dictionary<string, PlayerData>();
            if (d.Claims == null) d.Claims = new List<ClaimWatch>();
            if (d.CreditedContracts == null) d.CreditedContracts = new List<int>();
            if (d.HouseMarks == null) d.HouseMarks = new Dictionary<string, Marks>();
            if (d.OutlawsNow == null) d.OutlawsNow = new List<string>();
            if (d.OutlawChecked == null) d.OutlawChecked = new List<string>();
            var bad = new List<string>();
            foreach (KeyValuePair<string, PlayerData> kv in d.Players)
            {
                if (kv.Value == null || !IsSteamId(kv.Key)) { bad.Add(kv.Key); continue; }
                PlayerData p = kv.Value;
                if (p.Stats == null) p.Stats = new Dictionary<string, int>();
                if (p.Titles == null) p.Titles = new Dictionary<string, DateTime>();
                if (p.Cooldowns == null) p.Cooldowns = new Dictionary<string, DateTime>();
                if (p.Log == null) p.Log = new List<DeedEntry>();
                p.Log.RemoveAll(delegate(DeedEntry e) { return e == null || e.Kind == null; });
                if (p.Name == null) p.Name = kv.Key;
                if (p.Renown < 0) p.Renown = 0;
                if (p.Infamy < 0) p.Infamy = 0;
                if (p.Chosen != null && !p.Titles.ContainsKey(p.Chosen)) p.Chosen = null;
            }
            foreach (string k in bad) d.Players.Remove(k);
            d.Claims.RemoveAll(delegate(ClaimWatch w) { return w == null || w.Key == null || w.House == null; });
            foreach (ClaimWatch w in d.Claims)
            {
                if (w.Claimants == null) w.Claimants = new List<string>();
                if (w.Defenders == null) w.Defenders = new List<string>();
            }
            d.OutlawsNow.RemoveAll(delegate(string s) { return s == null; });
            var badMarks = new List<string>();
            foreach (KeyValuePair<string, Marks> kv in d.HouseMarks) if (kv.Value == null) badMarks.Add(kv.Key);
            foreach (string k in badMarks) d.HouseMarks.Remove(k);
        }

        private void SaveData()
        {
            if (data == null || loadFailed) return;   // never overwrite a file we could not read
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            lastSave = Now();
        }

        #endregion

        #region Lifecycle

        protected override void LoadDefaultMessages()
        {
            var m = new Dictionary<string, string>();
            m.Add("Speaker", "Renown");
            m.Add("Herald", "[D6A043]Herald[FFFFFF]: ");
            m.Add("HelpHeader", "Deeds win renown; broken faith earns infamy. Both are remembered.");
            m.Add("Help1", "  [F4C96D]/renown[FFFFFF] - your renown, infamy, rank and recent deeds | [F4C96D]/renown[FFFFFF] <player> - another's | [F4C96D]/renown top[FFFFFF] [infamy] - the roll of honour");
            m.Add("Help2", "  [F4C96D]/titles[FFFFFF] - your titles | [F4C96D]/titles all[FFFFFF] - every title and how it is earned | [F4C96D]/titles set[FFFFFF] <title> - wear it in chat | [F4C96D]/titles clear[FFFFFF]");
            m.Add("HelpRenown", "  Renown comes from reigning, winning or holding the crown in the Lawful Hours, contracts, the tournament and the hunt.");
            m.Add("HelpInfamy", "  Infamy comes from broken oaths and treaties, outlawry and broken truces, and fades by {0} a day.");
            m.Add("HelpAdmin", "  Admin: [F4C96D]/renown admin grant[FFFFFF] <player> renown|infamy <+/-n> [reason] | title give|take <player> <title> | reset <player> [confirm] | status | save");
            m.Add("Paused", "The roll of honour is damaged and paused. Tell an admin (see the server log).");
            m.Add("Cooldown", "Wait a moment before asking again.");
            m.Add("NoPermission", "You may not do that.");
            m.Add("NotFound", "No one known to the realm matches '{0}'.");
            m.Add("Ambiguous", "More than one player matches '{0}'; use the full name.");
            m.Add("Summary", "{0}: {1} renown, {2} infamy (standing {3}), rank #{4} of {5}.");
            m.Add("SummaryTitles", "  Titles ({0}): {1}");
            m.Add("SummaryWorn", "  Wears the title: {0}");
            m.Add("SummaryDeeds", "  Recent deeds: {0}");
            m.Add("None", "none");
            m.Add("TopHeader", "The roll of {0} (top {1}):");
            m.Add("TopLine", "  #{0} {1} - {2}{3}");
            m.Add("TopEmpty", "No deeds have been recorded yet.");
            m.Add("TitlesHeader", "Your titles ({0} of {1}). Wear one with [F4C96D]/titles set[FFFFFF] <title>:");
            m.Add("TitlesNone", "You hold no titles yet. See [F4C96D]/titles all[FFFFFF] for how they are earned.");
            m.Add("TitleLine", "  {0}{1} - {2}");
            m.Add("TitlesAllHeader", "The titles of Ostreval (* = yours):");
            m.Add("TitleUnknown", "There is no title called '{0}'. See [F4C96D]/titles all[FFFFFF].");
            m.Add("TitleNotEarned", "You have not earned the title {0}.");
            m.Add("TitleSet", "You now bear the title {0} in chat.");
            m.Add("TitleCleared", "You no longer wear a title in chat.");
            m.Add("TitleCooldown", "You may change your title again in {0} s.");
            m.Add("TitleEarned", "You have earned the title {0}! Wear it with [F4C96D]/titles set[FFFFFF] {0}");
            m.Add("TitleAnnounce", "{0} has earned the title {1}.");
            m.Add("DeedNotice", "{0} ({1})");
            m.Add("DeedCapped", " - today's cap reached, no points");
            m.Add("AdminUsage", "Usage: [F4C96D]/renown admin grant[FFFFFF] <player> renown|infamy <+/-n> [reason] | title give|take <player> <title> | reset <player> [confirm] | status | save");
            m.Add("AdminTooMuch", "At most {0} per adjustment.");
            m.Add("AdminGranted", "{0} now has {1} renown and {2} infamy.");
            m.Add("AdminTitleGiven", "{0} now holds the title {1}.");
            m.Add("AdminTitleTaken", "{0} no longer holds the title {1}.");
            m.Add("AdminTitleNone", "{0} does not hold the title {1}.");
            m.Add("AdminResetWarn", "This erases all renown, infamy and titles of {0}. Type [F4C96D]/renown admin reset[FFFFFF] {0} confirm within 60 s.");
            m.Add("AdminResetDone", "The record of {0} is erased.");
            m.Add("AdminSaved", "Renown saved.");
            m.Add("Usage", "Usage: [F4C96D]/renown[FFFFFF] [player|top [infamy]|help]  [F4C96D]/titles[FFFFFF] [all|set <title>|clear]");
            lang.RegisterMessages(m, this);
        }

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintError("Could not read oxide/config/RealmRenown.json (" + ex.Message + "); using defaults for this session (the file is not overwritten).");
                config = null;
            }
            if (config == null) config = DefaultConfig();
            SanitizeConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                    // re-sent on hot load (doc section 7); keep idempotent
            initialized = true;
            lastTick = Now();
            tickTimer = timer.Every(config.TickSeconds, Tick);
            Tick();
        }

        private void OnServerSave()
        {
            if (dirty) SaveData();
        }

        private void Unload()
        {
            // Give every player back the chat format the game gave them.
            foreach (Player p in OnlinePlayers()) RemovePrefix(p);
            if (dirty) SaveData();
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

        #region Hooks

        private void OnPlayerConnected(Player player)
        {
            if (data == null || player == null || player.IsServer) return;
            PlayerData p = GetPlayer(player.Id.ToString(), player.Name, true);
            p.LastSeen = Now();
            dirty = true;
            appliedPrefix.Remove(player.Id);           // a new Player object carries the game's own format
            ApplyPrefix(player);
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null) return;
            appliedPrefix.Remove(player.Id);
            if (data == null || player.IsServer) return;
            PlayerData p = FindPlayer(player.Id.ToString());
            if (p != null) { p.LastSeen = Now(); dirty = true; }
        }

        private void OnEntityDeath(EntityDeathEvent evt)
        {
            if (data == null || evt == null || !config.FeedKingslayer) return;
            try { HandleDeath(evt); }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
        }

        private void HandleDeath(EntityDeathEvent evt)
        {
            if (evt.Entity == null || !evt.Entity.IsPlayer || evt.KillingDamage == null) return;
            Player victim = evt.Entity.Owner;
            Player killer = evt.KillingDamage.DamageSource != null ? evt.KillingDamage.DamageSource.Owner : null;
            if (victim == null || killer == null || victim.IsServer || killer.IsServer || victim.Id == killer.Id) return;
            DateTime now = Now();
            ulong king = KingId();
            bool wasKing = (king != 0 && victim.Id == king)
                || (cachedKingId != 0 && victim.Id == cachedKingId && (now - cachedKingSeen).TotalSeconds <= config.KingslayerGraceSeconds);
            if (!wasKing) return;
            // The game leaves a sleeping body when a player logs out, and it can still be killed. Stabbing a logged-out
            // monarch's sleeper is no deed of renown (and the "Kingslayer" title would be free to anyone who finds it).
            if (OnlinePlayer(victim.Id.ToString()) == null)
            {
                Puts("Kingslayer: " + victim.Name + " was not online (a sleeping body); no deed for " + killer.Name + ".");
                return;
            }
            Award(killer.Id.ToString(), killer.Name, "kingslayer", "slew " + Clean(victim.Name), "kingslayer:" + victim.Id);
        }

        #endregion

        #region Tick

        private void Tick()
        {
            if (data == null) return;
            DateTime now = Now();
            double elapsed = (now - lastTick).TotalSeconds;
            if (elapsed < 0) elapsed = 0;
            if (elapsed > config.TickSeconds * 3) elapsed = config.TickSeconds * 3;   // never count downtime
            lastTick = now;

            try { TickCrown(now, elapsed); } catch (Exception ex) { PrintError("Crown tick failed: " + ex.Message); }
            try { TickRebellions(now); } catch (Exception ex) { PrintError("Rebellion tick failed: " + ex.Message); }
            foreach (Player pl in OnlinePlayers())
            {
                PlayerData p = GetPlayer(pl.Id.ToString(), pl.Name, true);
                p.LastSeen = now;
                ApplyPrefix(pl);
            }
            DecayInfamy(now);
            if ((now - lastFeedPoll).TotalSeconds >= config.FeedPollSeconds)
            {
                lastFeedPoll = now;
                PollFeeds(now);
            }
            Prune();
            if (dirty && (now - lastSave).TotalSeconds >= 60) SaveData();   // at most once a minute; also on save/unload
        }

        private void TickCrown(DateTime now, double elapsed)
        {
            ulong king = KingId();
            if (king == 0) return;
            string name = KingName(king);
            cachedKingId = king;
            cachedKingName = name;
            cachedKingSeen = now;
            if (!config.FeedReign) return;
            PlayerData p = GetPlayer(king.ToString(), name, true);
            p.ReignSeconds += elapsed;
            double day = config.ReignDayHours * 3600.0;
            int guard = 0;
            while (p.ReignSeconds >= day && guard++ < 3)
            {
                p.ReignSeconds -= day;
                Award(king.ToString(), name, "reign_day", null, null);
            }
            if (p.ReignSeconds >= day) p.ReignSeconds = 0;
        }

        private void DecayInfamy(DateTime now)
        {
            string today = DayKey(now);
            if (data.LastDecayDay == null) { data.LastDecayDay = today; return; }
            if (data.LastDecayDay == today) return;
            DateTime last;
            int days = 1;
            if (DateTime.TryParseExact(data.LastDecayDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last))
                days = (int)Math.Floor((now.Date - last.Date).TotalDays);
            data.LastDecayDay = today;
            dirty = true;
            if (days < 1) return;
            if (days > 7) days = 7;
            int amount = config.InfamyDecayPerDay * days;
            if (amount <= 0) return;
            foreach (PlayerData p in data.Players.Values) p.Infamy = Math.Max(0, p.Infamy - amount);
        }

        #endregion

        #region Rebellions (CrownAndConsequences + RealmHouses)

        private void TickRebellions(DateTime now)
        {
            if (!config.FeedRebellions) return;
            string[] raw = CrownAndConsequences != null ? CrownAndConsequences.Call("GetOpenClaims") as string[] : null;
            if (raw == null)
            {
                // Plugin absent: resolve nothing, but forget watches that are long over.
                data.Claims.RemoveAll(delegate(ClaimWatch w) { return now > w.End.AddMinutes(config.RebellionResolveGraceMinutes); });
                return;
            }
            var present = new Dictionary<string, bool>();
            var houseCache = new Dictionary<string, string>();
            var liegeCache = new Dictionary<string, string>();
            foreach (string line in raw)
            {
                if (line == null) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 4) continue;
                string house = parts[0];
                string status = parts[1];
                DateTime start, end;
                if (!ParseIso(parts[2], out start) || !ParseIso(parts[3], out end) || string.IsNullOrEmpty(house)) continue;
                string key = house + "|" + parts[2];
                present[key] = true;
                if (status != "active") continue;
                ClaimWatch w = FindWatch(key);
                if (w == null)
                {
                    if (data.Claims.Count >= 20) continue;
                    w = new ClaimWatch { Key = key, House = house, Start = start, End = end, Claimants = new List<string>(), Defenders = new List<string>() };
                    data.Claims.Add(w);
                }
                w.End = end;
                if (!w.SawActive)
                {
                    w.SawActive = true;
                    w.CrownHouseAtStart = CrownHouse();
                }
                foreach (Player pl in OnlinePlayers())
                {
                    string id = pl.Id.ToString();
                    string h = CachedHouse(id, houseCache);
                    if (h == null) continue;
                    string liege = CachedLiege(h, liegeCache);
                    bool claimant = SameName(h, w.House) || SameName(liege, w.House);
                    bool defender = !claimant && w.CrownHouseAtStart != null
                        && (SameName(h, w.CrownHouseAtStart) || SameName(liege, w.CrownHouseAtStart));
                    if (claimant) AddCapped(w.Claimants, id, 300);
                    else if (defender) AddCapped(w.Defenders, id, 300);
                }
            }
            foreach (ClaimWatch w in data.Claims.ToArray())
            {
                if (present.ContainsKey(w.Key)) continue;
                // Gone from the open list: it ended (at its window end) or was withdrawn early (no deeds).
                if (w.SawActive && now >= w.End.AddMinutes(-2) && now <= w.End.AddMinutes(config.RebellionResolveGraceMinutes))
                    ResolveRebellion(w);
                data.Claims.Remove(w);
            }
        }

        private void ResolveRebellion(ClaimWatch w)
        {
            string crownHouse = CrownHouse();
            ulong king = KingId();
            string kingId = king != 0 ? king.ToString() : null;
            if (crownHouse != null && SameName(crownHouse, w.House))
            {
                Puts("Rebellion of House " + w.House + " won; crediting " + w.Claimants.Count + " participant(s).");
                if (kingId != null) Award(kingId, KingName(king), "crown_seized", "the rebellion of House " + w.House, "crown_seized:" + w.Key);
                foreach (string id in w.Claimants)
                    if (id != kingId) Award(id, null, "kingmaker", "the rebellion of House " + w.House, "kingmaker:" + w.Key);
            }
            else if (crownHouse != null && w.CrownHouseAtStart != null && SameName(crownHouse, w.CrownHouseAtStart))
            {
                if (w.Claimants.Count < config.RebellionMinClaimants)
                {
                    Puts("Rebellion of House " + w.House + " failed with " + w.Claimants.Count + " claimant(s) seen online; below "
                        + "RebellionMinClaimants (" + config.RebellionMinClaimants + "), so no defence deeds.");
                    return;
                }
                Puts("Rebellion of House " + w.House + " failed; crediting the crown and " + w.Defenders.Count + " defender(s).");
                if (kingId != null) Award(kingId, KingName(king), "crown_defended", "against House " + w.House, "crown_defended:" + w.Key);
                foreach (string id in w.Defenders)
                    if (id != kingId) Award(id, null, "rebellion_defended", "against House " + w.House, "rebellion_defended:" + w.Key);
            }
        }

        private ClaimWatch FindWatch(string key)
        {
            foreach (ClaimWatch w in data.Claims) if (w.Key == key) return w;
            return null;
        }

        private string CachedHouse(string playerId, Dictionary<string, string> cache)
        {
            string h;
            if (cache.TryGetValue(playerId, out h)) return h;
            h = RealmHouses != null ? RealmHouses.Call("GetHouse", playerId) as string : null;
            cache[playerId] = h;
            return h;
        }

        private string CachedLiege(string house, Dictionary<string, string> cache)
        {
            string l;
            if (cache.TryGetValue(house, out l)) return l;
            l = RealmHouses != null ? RealmHouses.Call("GetLiege", house) as string : null;
            cache[house] = l;
            return l;
        }

        private string CrownHouse()
        {
            return CrownAndConsequences != null ? CrownAndConsequences.Call("GetKingHouse") as string : null;
        }

        #endregion

        #region Feeds (read-only files and plugin calls)

        private void PollFeeds(DateTime now)
        {
            int budget = config.MaxAwardsPerPoll;
            if (config.FeedContracts) { try { budget = PollContracts(budget); } catch (Exception ex) { Status("contracts", "error: " + ex.Message); } }
            if (config.FeedHouses) { try { budget = PollHouses(budget); } catch (Exception ex) { Status("houses", "error: " + ex.Message); } }
            if (config.FeedChronicle) { try { budget = PollChronicle(budget); } catch (Exception ex) { Status("chronicle", "error: " + ex.Message); } }
            if (config.FeedOutlaws) { try { PollOutlaws(); } catch (Exception ex) { Status("outlaws", "error: " + ex.Message); } }
        }

        private void Status(string feed, string text)
        {
            string old;
            if (text.StartsWith("error") && (!feedStatus.TryGetValue(feed, out old) || old != text)) PrintWarning("Feed " + feed + ": " + text);
            feedStatus[feed] = text + " (" + Now().ToString("HH:mm") + " UTC)";
        }

        // Reads another plugin's data file without ever creating or writing it. Null if absent or unreadable.
        private T ReadForeign<T>(string name, string feed) where T : class
        {
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(name)) { Status(feed, "no oxide/data/" + name + ".json"); return null; }
            T obj = null;
            try { obj = Interface.Oxide.DataFileSystem.ReadObject<T>(name); }
            catch (Exception ex) { Status(feed, "error: unreadable " + name + ".json (" + ex.Message + ")"); return null; }
            if (obj == null) Status(feed, "empty " + name + ".json");
            return obj;
        }

        private int PollContracts(int budget)
        {
            ContractsFileLite f = ReadForeign<ContractsFileLite>(ContractsFile, "contracts");
            if (f == null || f.Contracts == null) return budget;
            if (f.NextId < data.ContractsNextId)
            {
                // The contracts file was replaced or reset: ids start again, so re-baseline instead of re-crediting.
                data.CreditedContracts.Clear();
                data.ContractsBaselined = false;
            }
            data.ContractsNextId = f.NextId;
            if (!data.ContractsBaselined)
            {
                data.ContractsBaselined = true;
                if (!config.CountHistoryOnFirstRun)
                {
                    foreach (ContractLite c in f.Contracts)
                        if (c != null && c.Status == "done") data.CreditedContracts.Add(c.Id);
                    TrimCredited();
                    Status("contracts", "baselined " + data.CreditedContracts.Count + " finished contract(s)");
                    return budget;
                }
            }
            int n = 0;
            foreach (ContractLite c in f.Contracts)
            {
                if (budget <= 0) break;
                if (c == null || c.Status != "done" || string.IsNullOrEmpty(c.FulfillerId) || !IsSteamId(c.FulfillerId)) continue;
                if (data.CreditedContracts.Contains(c.Id)) continue;
                data.CreditedContracts.Add(c.Id);
                string kind = c.Type == "bounty" ? "contract_bounty" : c.Type == "delivery" ? "contract_delivery" : c.Type == "merc" ? "contract_merc" : null;
                if (kind == null) continue;
                if (!ContractDeedAllowed(c)) continue;
                if (Award(c.FulfillerId, c.FulfillerName, kind, "contract #" + c.Id, null)) NoteContractDeed(c);
                budget--;
                n++;
            }
            TrimCredited();
            Status("contracts", "ok, " + n + " new");
            return budget;
        }

        // Alt-farming guard for contract deeds: the same poster cannot feed the same fulfiller more than once per
        // ContractPairCooldownHours, and a player earns at most MaxContractDeedsPerDay contract deeds per UTC day.
        private bool ContractDeedAllowed(ContractLite c)
        {
            PlayerData p = FindPlayer(c.FulfillerId);
            if (p == null) return true;
            DateTime now = Now();
            RollDay(p, now);
            if (config.MaxContractDeedsPerDay > 0 && p.ContractDeedsToday >= config.MaxContractDeedsPerDay)
            {
                Puts("Contract #" + c.Id + ": " + p.Name + " already earned " + p.ContractDeedsToday + " contract deeds today; no deed.");
                return false;
            }
            DateTime until;
            if (config.ContractPairCooldownHours > 0 && !string.IsNullOrEmpty(c.PosterId)
                && p.Cooldowns.TryGetValue("contract_pair:" + c.PosterId, out until) && until > now)
            {
                Puts("Contract #" + c.Id + ": " + p.Name + " was paid by the same poster within " + config.ContractPairCooldownHours + " h; no deed.");
                return false;
            }
            return true;
        }

        private void NoteContractDeed(ContractLite c)
        {
            PlayerData p = FindPlayer(c.FulfillerId);
            if (p == null) return;
            p.ContractDeedsToday++;
            if (config.ContractPairCooldownHours > 0 && !string.IsNullOrEmpty(c.PosterId))
                p.Cooldowns["contract_pair:" + c.PosterId] = Now().AddHours(config.ContractPairCooldownHours);
            dirty = true;
        }

        private void TrimCredited()
        {
            if (data.CreditedContracts.Count <= 3000) return;
            data.CreditedContracts.Sort();
            data.CreditedContracts.RemoveRange(0, data.CreditedContracts.Count - 3000);
        }

        private int PollHouses(int budget)
        {
            HousesFileLite f = ReadForeign<HousesFileLite>(HousesFile, "houses");
            if (f == null || f.Players == null) return budget;
            bool baseline = !data.HousesBaselined && !config.CountHistoryOnFirstRun;
            int n = 0;
            foreach (KeyValuePair<string, HouseRecordLite> kv in f.Players)
            {
                if (kv.Value == null || !IsSteamId(kv.Key)) continue;
                Marks prev;
                if (!data.HouseMarks.TryGetValue(kv.Key, out prev))
                {
                    if (data.HouseMarks.Count >= config.MaxPlayers) continue;
                    prev = new Marks();
                    data.HouseMarks[kv.Key] = prev;
                }
                int oaths = Math.Max(0, kv.Value.OathsBroken), treaties = Math.Max(0, kv.Value.TreatiesBroken);
                if (!baseline)
                {
                    int dOath = Math.Min(oaths - prev.Oaths, config.MaxMarkDeltaPerPoll);
                    int dTreaty = Math.Min(treaties - prev.Treaties, config.MaxMarkDeltaPerPoll);
                    for (int i = 0; i < dOath && budget > 0; i++, budget--, n++) Award(kv.Key, kv.Value.Name, "oath_broken", null, null);
                    for (int i = 0; i < dTreaty && budget > 0; i++, budget--, n++) Award(kv.Key, kv.Value.Name, "treaty_broken", null, null);
                }
                prev.Oaths = oaths;
                prev.Treaties = treaties;
            }
            data.HousesBaselined = true;
            Status("houses", baseline ? "baselined" : "ok, " + n + " new");
            return budget;
        }

        private int PollChronicle(int budget)
        {
            List<ChronEventLite> events = ReadForeign<List<ChronEventLite>>(ChronicleFile, "chronicle");
            if (events == null) return budget;
            int maxId = 0;
            foreach (ChronEventLite e in events) if (e != null && e.id > maxId) maxId = e.id;
            if (data.ChronicleCursor < 0)
            {
                data.ChronicleCursor = config.CountHistoryOnFirstRun ? 0 : maxId;
                if (!config.CountHistoryOnFirstRun) { Status("chronicle", "baselined at #" + maxId); return budget; }
            }
            if (maxId < data.ChronicleCursor)
            {
                // The chronicle was reset or restored: do not replay it.
                data.ChronicleCursor = maxId;
                Status("chronicle", "reset detected, cursor #" + maxId);
                return budget;
            }
            events.Sort(delegate(ChronEventLite a, ChronEventLite b) { return (a == null ? 0 : a.id).CompareTo(b == null ? 0 : b.id); });
            int n = 0;
            foreach (ChronEventLite e in events)
            {
                if (e == null || e.id <= data.ChronicleCursor) continue;
                if (budget <= 0) break;
                data.ChronicleCursor = e.id;
                string kind;
                if (e.type == null || !config.ChronicleDeeds.TryGetValue(e.type, out kind)) continue;
                if (e.actors == null || e.actors.Length == 0) continue;
                string id = ResolveName(e.actors[0]);
                if (id == null) { Puts("Chronicle #" + e.id + ": doer '" + e.actors[0] + "' is unknown or ambiguous; no deed."); continue; }
                Award(id, null, kind, Clean(e.title), null);
                budget--;
                n++;
            }
            Status("chronicle", "ok at #" + data.ChronicleCursor + ", " + n + " new");
            return budget;
        }

        private void PollOutlaws()
        {
            var now = new Dictionary<string, string>();
            bool any = false;
            courtOutlawIds.Clear();
            if (RealmLaws != null)
            {
                string[] court = RealmLaws.Call("GetCourtOutlaws") as string[];
                if (court != null)
                {
                    any = true;
                    foreach (string line in court)
                    {
                        if (line == null) continue;
                        string[] parts = line.Split('|');
                        if (parts.Length >= 2 && IsSteamId(parts[0])) { now[parts[0]] = parts[1]; courtOutlawIds[parts[0]] = true; }
                    }
                }
            }
            var online = new Dictionary<string, bool>();
            var firstLook = new Dictionary<string, bool>();
            foreach (Player pl in OnlinePlayers())
            {
                string id = pl.Id.ToString();
                online[id] = true;
                if (RealmContracts == null) continue;
                object r = RealmContracts.Call("IsOutlaw", id);
                if (!(r is bool)) continue;
                any = true;
                if (!data.OutlawChecked.Contains(id))
                {
                    // RealmContracts can only be asked about online players, so the first look at a player is their
                    // baseline: an outlawry that predates it is not a new deed.
                    firstLook[id] = true;
                    data.OutlawChecked.Add(id);
                    if (data.OutlawChecked.Count > config.MaxPlayers) data.OutlawChecked.RemoveAt(0);
                }
                if ((bool)r) now[id] = pl.Name;
            }
            if (!any && RealmLaws == null && RealmContracts == null) { Status("outlaws", "no RealmLaws or RealmContracts loaded"); return; }
            bool baseline = !data.OutlawsBaselined && !config.CountHistoryOnFirstRun;
            int n = 0;
            if (!baseline)
                foreach (KeyValuePair<string, string> kv in now)
                    if (!data.OutlawsNow.Contains(kv.Key) && !(firstLook.ContainsKey(kv.Key) && !CourtListed(kv.Key))
                        && Award(kv.Key, kv.Value, "outlawed", null, null)) n++;
            // Keep previous outlaws who are offline: their RealmContracts status cannot be read until they return.
            var next = new List<string>(now.Keys);
            foreach (string id in data.OutlawsNow)
                if (!now.ContainsKey(id) && !online.ContainsKey(id) && next.Count < 500) next.Add(id);
            data.OutlawsNow = next;
            data.OutlawsBaselined = true;
            Status("outlaws", baseline ? "baselined " + next.Count : "ok, " + n + " new");
        }

        private bool CourtListed(string id)
        {
            return courtOutlawIds.ContainsKey(id);
        }

        #endregion

        #region Deeds and titles

        private bool Award(string playerId, string name, string kind, string note, string cooldownKey)
        {
            if (data == null || playerId == null || !IsSteamId(playerId)) return false;
            DeedDef d;
            if (kind == null || !config.Deeds.TryGetValue(kind, out d) || d == null) return false;
            PlayerData p = GetPlayer(playerId, name, true);
            if (p == null) return false;
            DateTime now = Now();
            string ck = cooldownKey ?? kind;
            if (p.Cooldowns.Count > 40) PruneCooldowns(p, now);
            if (d.CooldownMinutes > 0)
            {
                DateTime until;
                if (p.Cooldowns.TryGetValue(ck, out until) && until > now) return false;
                p.Cooldowns[ck] = now.AddMinutes(d.CooldownMinutes);
            }
            else if (cooldownKey != null)
            {
                // A keyed deed (one claim, one victim) is never counted twice even without a configured cooldown.
                DateTime until;
                if (p.Cooldowns.TryGetValue(ck, out until) && until > now) return false;
                p.Cooldowns[ck] = now.AddDays(2);
            }
            RollDay(p, now);
            int r = Math.Max(0, Math.Min(d.Renown, config.MaxRenownPerDay - p.RenownToday));
            int i = Math.Max(0, Math.Min(d.Infamy, config.MaxInfamyPerDay - p.InfamyToday));
            bool capped = r < d.Renown || i < d.Infamy;
            p.RenownToday += r;
            p.InfamyToday += i;
            p.Renown += r;
            p.Infamy += i;
            int count;
            p.Stats.TryGetValue(kind, out count);
            p.Stats[kind] = count + 1;
            if (config.DeedLogPerPlayer > 0)
            {
                p.Log.Add(new DeedEntry { At = now, Kind = kind, Renown = r, Infamy = i, Note = note != null ? CleanText(note, 80) : null });
                if (p.Log.Count > config.DeedLogPerPlayer) p.Log.RemoveRange(0, p.Log.Count - config.DeedLogPerPlayer);
            }
            dirty = true;
            Puts("Deed " + kind + " for " + p.Name + " (" + playerId + "): +" + r + " renown, +" + i + " infamy" + (capped ? " (capped)" : "") + (note != null ? " - " + note : ""));

            Player online = OnlinePlayer(playerId);
            if (online != null)
            {
                string pts = Points(r, i);
                Tell(online, Msg("DeedNotice", online, Cap(d.Label), pts) + (capped ? Msg("DeedCapped", online) : ""));
            }
            CheckTitles(playerId, p);
            return true;
        }

        private static void PruneCooldowns(PlayerData p, DateTime now)
        {
            var expired = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in p.Cooldowns) if (kv.Value <= now) expired.Add(kv.Key);
            foreach (string k in expired) p.Cooldowns.Remove(k);
            if (p.Cooldowns.Count > 200) p.Cooldowns.Clear();   // hard bound; worst case a cooldown is forgotten
        }

        private static string Points(int r, int i)
        {
            if (r > 0 && i > 0) return "+" + r + " renown, +" + i + " infamy";
            if (i > 0) return "+" + i + " infamy";
            return "+" + r + " renown";
        }

        private void RollDay(PlayerData p, DateTime now)
        {
            string key = DayKey(now);
            if (p.DayKey == key) return;
            p.DayKey = key;
            p.RenownToday = 0;
            p.InfamyToday = 0;
            p.ContractDeedsToday = 0;
        }

        private bool Meets(PlayerData p, TitleDef t)
        {
            foreach (KeyValuePair<string, int> r in t.Requires)
            {
                int have;
                if (r.Key == "renown") have = p.Renown;
                else if (r.Key == "infamy") have = p.Infamy;
                else p.Stats.TryGetValue(r.Key, out have);
                if (have < r.Value) return false;
            }
            return true;
        }

        private void CheckTitles(string playerId, PlayerData p)
        {
            foreach (TitleDef t in config.Titles)
            {
                if (p.Titles.ContainsKey(t.Id) || !Meets(p, t)) continue;
                GrantTitle(playerId, p, t, true);
            }
        }

        private void GrantTitle(string playerId, PlayerData p, TitleDef t, bool announce)
        {
            p.Titles[t.Id] = Now();
            dirty = true;
            Puts(p.Name + " (" + playerId + ") earns the title " + t.Name);
            Player online = OnlinePlayer(playerId);
            if (online != null) Tell(online, Msg("TitleEarned", online, t.Name), ChatOk);
            if (!announce) return;
            DateTime now = Now();
            if (config.AnnounceTitles && UnderHourlyCap(announceTimes, config.MaxAnnouncementsPerHour, now))
                Server.BroadcastMessage(Msg("Herald", null) + Msg("TitleAnnounce", null, p.Name, t.Name));
            if (config.ChronicleTitles && RealmChronicle != null && !chronicleTypeRejected
                && UnderHourlyCap(chronicleTimes, config.MaxChronicleTitlesPerHour, now))
            {
                object res = RealmChronicle.Call("Log", TitleEventType, p.Name + " is named " + t.Name,
                    p.Name + " has earned the title " + t.Name + ": " + t.Description, new[] { p.Name });
                if (res is int && (int)res == 0)
                {
                    // An older RealmChronicle.cs without title_earned in its closed type list.
                    chronicleTypeRejected = true;
                    PrintWarning("RealmChronicle rejected '" + TitleEventType + "'; titles will not be chronicled until it is registered.");
                }
            }
        }

        private static bool UnderHourlyCap(Queue<DateTime> q, int cap, DateTime now)
        {
            while (q.Count > 0 && (now - q.Peek()).TotalHours >= 1) q.Dequeue();
            if (q.Count >= cap) return false;
            q.Enqueue(now);
            return true;
        }

        private TitleDef FindTitle(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string q = query.Trim();
            foreach (TitleDef t in config.Titles)
                if (string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Id, q, StringComparison.OrdinalIgnoreCase)) return t;
            // "the unbowed" / "unbowed" both find "The Unbowed".
            string bare = StripThe(q);
            TitleDef hit = null;
            foreach (TitleDef t in config.Titles)
                if (string.Equals(StripThe(t.Name), bare, StringComparison.OrdinalIgnoreCase)) { if (hit != null) return null; hit = t; }
            return hit;
        }

        private static string StripThe(string s)
        {
            return s.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? s.Substring(4).Trim() : s;
        }

        private TitleDef TitleById(string id)
        {
            if (id == null) return null;
            foreach (TitleDef t in config.Titles) if (t.Id == id) return t;
            return null;
        }

        private string RequirementText(TitleDef t)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> r in t.Requires)
            {
                if (r.Key == "renown" || r.Key == "infamy") parts.Add(r.Value + " " + r.Key);
                else
                {
                    DeedDef d;
                    string label = config.Deeds.TryGetValue(r.Key, out d) ? d.Label : r.Key;
                    parts.Add(r.Value > 1 ? label + " x" + r.Value : label);
                }
            }
            return string.Join(", ", parts.ToArray());
        }

        #endregion

        #region Chat prefix

        private string PrefixFor(string playerId)
        {
            PlayerData p = FindPlayer(playerId);
            if (p == null || p.Chosen == null) return "";
            TitleDef t = TitleById(p.Chosen);
            if (t == null || !p.Titles.ContainsKey(t.Id)) return "";
            string fmt = t.Infamous ? config.InfamousPrefixFormat : config.ChatPrefixFormat;
            return fmt.Replace("{title}", t.Name);
        }

        // Prefixes the game's own global chat format ("%name% : %message%" by default) with the chosen title, and
        // recognises its own earlier prefix so a format the game reset is simply prefixed again.
        private void ApplyPrefix(Player pl)
        {
            if (pl == null || pl.IsServer) return;
            string want = config.ChatPrefixEnabled && data != null ? PrefixFor(pl.Id.ToString()) : "";
            string cur = pl.ChatFormat ?? "";
            string applied;
            appliedPrefix.TryGetValue(pl.Id, out applied);
            string baseFormat = cur;
            if (!string.IsNullOrEmpty(applied) && cur.StartsWith(applied, StringComparison.Ordinal)) baseFormat = cur.Substring(applied.Length);
            if (baseFormat.IndexOf("%message%", StringComparison.Ordinal) < 0)
            {
                appliedPrefix.Remove(pl.Id);           // never build on a format we do not understand
                return;
            }
            string next = want + baseFormat;
            if (next != cur) pl.ChatFormat = next;
            if (want.Length > 0) appliedPrefix[pl.Id] = want;
            else appliedPrefix.Remove(pl.Id);
        }

        private void RemovePrefix(Player pl)
        {
            if (pl == null) return;
            string applied;
            if (!appliedPrefix.TryGetValue(pl.Id, out applied) || string.IsNullOrEmpty(applied)) return;
            string cur = pl.ChatFormat ?? "";
            if (cur.StartsWith(applied, StringComparison.Ordinal)) pl.ChatFormat = cur.Substring(applied.Length);
            appliedPrefix.Remove(pl.Id);
        }

        #endregion

        #region Commands

        [ChatCommand("renown")]
        private void CmdRenown(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null) args = new string[0];
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "help" || sub == "?") { ShowHelp(player); return; }
            if (data == null) { TellError(player, Msg("Paused", player)); return; }
            if (sub == "admin") { CmdAdmin(player, args); return; }
            if (!PassCooldown(player)) return;
            if (sub == "top") { ShowTop(player, args.Length > 1 && args[1].ToLowerInvariant() == "infamy"); return; }
            if (args.Length == 0) { ShowSummary(player, player.Id.ToString()); return; }
            string query = string.Join(" ", args);
            string id = FindPlayerId(player, query);
            if (id != null) ShowSummary(player, id);
        }

        [ChatCommand("titles")]
        private void CmdTitles(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (args == null) args = new string[0];
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "help" || sub == "?") { ShowHelp(player); return; }
            if (data == null) { TellError(player, Msg("Paused", player)); return; }
            if (!PassCooldown(player)) return;
            string id = player.Id.ToString();
            PlayerData p = GetPlayer(id, player.Name, true);
            if (sub == "" || sub == "mine") { ShowOwnTitles(player, p); return; }
            if (sub == "all") { ShowAllTitles(player, p); return; }
            if (sub == "clear" || sub == "none" || sub == "set" || sub == "wear")
            {
                bool clear = sub == "clear" || sub == "none";
                TitleDef t = null;
                if (!clear)
                {
                    string q = JoinFrom(args, 1);
                    t = FindTitle(q);
                    if (t == null) { TellError(player, Msg("TitleUnknown", player, CleanText(q, 30))); return; }
                    if (!p.Titles.ContainsKey(t.Id)) { TellError(player, Msg("TitleNotEarned", player, t.Name)); return; }
                }
                DateTime now = Now();
                double wait = config.TitleChangeCooldownSeconds - (now - p.ChosenAt).TotalSeconds;
                if (wait > 0 && p.ChosenAt != DateTime.MinValue) { TellError(player, Msg("TitleCooldown", player, (int)Math.Ceiling(wait))); return; }
                p.Chosen = clear ? null : t.Id;
                p.ChosenAt = now;
                dirty = true;
                ApplyPrefix(player);
                Tell(player, clear ? Msg("TitleCleared", player) : Msg("TitleSet", player, t.Name), ChatOk);
                return;
            }
            // "/titles Kingslayer" = info on one title.
            TitleDef info = FindTitle(string.Join(" ", args));
            if (info == null) { TellError(player, Msg("TitleUnknown", player, CleanText(string.Join(" ", args), 30))); return; }
            Tell(player, Msg("TitleLine", player, p.Titles.ContainsKey(info.Id) ? "* " : "", info.Name, info.Description + " [" + RequirementText(info) + "]"));
        }

        private void ShowHelp(Player player)
        {
            Tell(player, Msg("HelpHeader", player));
            Tell(player, Msg("Help1", player));
            Tell(player, Msg("Help2", player));
            Tell(player, Msg("HelpRenown", player));
            Tell(player, Msg("HelpInfamy", player, config.InfamyDecayPerDay));
            if (IsAdmin(player)) Tell(player, Msg("HelpAdmin", player));
        }

        private void ShowSummary(Player viewer, string id)
        {
            PlayerData p = FindPlayer(id);
            if (p == null) p = new PlayerData { Name = viewer.Id.ToString() == id ? viewer.Name : id, Stats = new Dictionary<string, int>(), Titles = new Dictionary<string, DateTime>(), Log = new List<DeedEntry>() };
            int rank = 1, total = 0;
            foreach (KeyValuePair<string, PlayerData> kv in data.Players)
            {
                if (kv.Value.Renown <= 0 && kv.Value.Infamy <= 0) continue;
                total++;
                if (kv.Key != id && Outranks(kv.Value, kv.Key, p, id)) rank++;
            }
            if (p.Renown <= 0 && p.Infamy <= 0) { total++; rank = total; }
            Tell(viewer, Msg("Summary", viewer, p.Name, p.Renown, p.Infamy, p.Renown - p.Infamy, rank, total));
            var names = new List<string>();
            foreach (TitleDef t in config.Titles) if (p.Titles.ContainsKey(t.Id)) names.Add(t.Name);
            Tell(viewer, Msg("SummaryTitles", viewer, names.Count, names.Count > 0 ? string.Join(", ", names.ToArray()) : Msg("None", viewer)));
            TitleDef worn = TitleById(p.Chosen);
            if (worn != null) Tell(viewer, Msg("SummaryWorn", viewer, worn.Name));
            if (p.Log.Count > 0)
            {
                var deeds = new List<string>();
                for (int i = p.Log.Count - 1; i >= 0 && deeds.Count < 4; i--)
                {
                    DeedEntry e = p.Log[i];
                    DeedDef d;
                    string label = config.Deeds.TryGetValue(e.Kind, out d) ? d.Label : e.Kind;
                    deeds.Add(e.At.ToString("MM-dd") + " " + label + (e.Note != null ? " (" + e.Note + ")" : ""));
                }
                Tell(viewer, Msg("SummaryDeeds", viewer, string.Join("; ", deeds.ToArray())));
            }
        }

        private static bool Outranks(PlayerData a, string aId, PlayerData b, string bId)
        {
            if (a.Renown != b.Renown) return a.Renown > b.Renown;
            if (a.Infamy != b.Infamy) return a.Infamy < b.Infamy;
            return string.CompareOrdinal(aId, bId) < 0;
        }

        private void ShowTop(Player player, bool infamy)
        {
            var list = new List<KeyValuePair<string, PlayerData>>();
            foreach (KeyValuePair<string, PlayerData> kv in data.Players)
                if (infamy ? kv.Value.Infamy > 0 : kv.Value.Renown > 0) list.Add(kv);
            if (list.Count == 0) { Tell(player, Msg("TopEmpty", player)); return; }
            list.Sort(delegate(KeyValuePair<string, PlayerData> a, KeyValuePair<string, PlayerData> b)
            {
                if (infamy)
                {
                    if (a.Value.Infamy != b.Value.Infamy) return b.Value.Infamy.CompareTo(a.Value.Infamy);
                    return string.CompareOrdinal(a.Key, b.Key);
                }
                return Outranks(a.Value, a.Key, b.Value, b.Key) ? -1 : (Outranks(b.Value, b.Key, a.Value, a.Key) ? 1 : 0);
            });
            int n = Math.Min(config.TopCount, list.Count);
            Tell(player, Msg("TopHeader", player, infamy ? "infamy" : "honour", n));
            for (int i = 0; i < n; i++)
            {
                PlayerData p = list[i].Value;
                TitleDef worn = TitleById(p.Chosen);
                string pts = infamy ? p.Infamy + " infamy" : p.Renown + " renown";
                Tell(player, Msg("TopLine", player, i + 1, p.Name, pts, worn != null ? " (" + worn.Name + ")" : ""));
            }
        }

        private void ShowOwnTitles(Player player, PlayerData p)
        {
            if (p.Titles.Count == 0) { Tell(player, Msg("TitlesNone", player)); return; }
            Tell(player, Msg("TitlesHeader", player, p.Titles.Count, config.Titles.Count));
            foreach (TitleDef t in config.Titles)
                if (p.Titles.ContainsKey(t.Id))
                    Tell(player, Msg("TitleLine", player, p.Chosen == t.Id ? "> " : "* ", t.Name, t.Description));
        }

        private void ShowAllTitles(Player player, PlayerData p)
        {
            Tell(player, Msg("TitlesAllHeader", player));
            foreach (TitleDef t in config.Titles)
                Tell(player, Msg("TitleLine", player, p.Titles.ContainsKey(t.Id) ? "* " : "", t.Name, RequirementText(t)));
        }

        private void CmdAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { TellError(player, Msg("NoPermission", player)); return; }
            string what = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (what == "save") { dirty = true; SaveData(); Tell(player, Msg("AdminSaved", player), ChatOk); return; }
            if (what == "status") { AdminStatus(player); return; }
            if (what == "grant" && args.Length >= 5)
            {
                string id = FindPlayerId(player, args[2]);
                if (id == null) return;
                string which = args[3].ToLowerInvariant();
                int amount;
                if ((which != "renown" && which != "infamy") || !int.TryParse(args[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount))
                { TellError(player, Msg("AdminUsage", player)); return; }
                if (Math.Abs(amount) > config.AdminMaxAdjust) { TellError(player, Msg("AdminTooMuch", player, config.AdminMaxAdjust)); return; }
                PlayerData p = GetPlayer(id, null, true);
                if (which == "renown") p.Renown = Math.Max(0, p.Renown + amount);
                else p.Infamy = Math.Max(0, p.Infamy + amount);
                string reason = JoinFrom(args, 5);
                if (config.DeedLogPerPlayer > 0)
                {
                    p.Log.Add(new DeedEntry { At = Now(), Kind = "admin", Renown = which == "renown" ? amount : 0, Infamy = which == "infamy" ? amount : 0,
                        Note = CleanText("by " + player.Name + (reason.Length > 0 ? ": " + reason : ""), 80) });
                    if (p.Log.Count > config.DeedLogPerPlayer) p.Log.RemoveRange(0, p.Log.Count - config.DeedLogPerPlayer);
                }
                dirty = true;
                Puts("Admin " + player.Name + " adjusted " + which + " of " + p.Name + " by " + amount + (reason.Length > 0 ? " (" + reason + ")" : ""));
                CheckTitles(id, p);
                Tell(player, Msg("AdminGranted", player, p.Name, p.Renown, p.Infamy), ChatOk);
                return;
            }
            if (what == "title" && args.Length >= 5)
            {
                string op = args[2].ToLowerInvariant();
                string id = FindPlayerId(player, args[3]);
                if (id == null) return;
                TitleDef t = FindTitle(JoinFrom(args, 4));
                if (t == null) { TellError(player, Msg("TitleUnknown", player, CleanText(JoinFrom(args, 4), 30))); return; }
                PlayerData p = GetPlayer(id, null, true);
                if (op == "give")
                {
                    if (!p.Titles.ContainsKey(t.Id)) GrantTitle(id, p, t, false);
                    Puts("Admin " + player.Name + " gave the title " + t.Name + " to " + p.Name);
                    Tell(player, Msg("AdminTitleGiven", player, p.Name, t.Name), ChatOk);
                }
                else if (op == "take")
                {
                    if (!p.Titles.Remove(t.Id)) { TellError(player, Msg("AdminTitleNone", player, p.Name, t.Name)); return; }
                    if (p.Chosen == t.Id) p.Chosen = null;
                    dirty = true;
                    Player online = OnlinePlayer(id);
                    if (online != null) ApplyPrefix(online);
                    Puts("Admin " + player.Name + " took the title " + t.Name + " from " + p.Name);
                    Tell(player, Msg("AdminTitleTaken", player, p.Name, t.Name), ChatOk);
                }
                else TellError(player, Msg("AdminUsage", player));
                return;
            }
            if (what == "reset" && args.Length >= 3)
            {
                string id = FindPlayerId(player, args[2]);
                if (id == null) return;
                PlayerData p = FindPlayer(id);
                string name = p != null ? p.Name : id;
                string key = player.Id + "|" + id;
                DateTime until;
                bool confirmed = args.Length >= 4 && args[3].ToLowerInvariant() == "confirm"
                    && resetConfirm.TryGetValue(key, out until) && until >= Now();
                if (!confirmed)
                {
                    resetConfirm[key] = Now().AddSeconds(60);
                    Tell(player, Msg("AdminResetWarn", player, name), ChatWarn);
                    return;
                }
                resetConfirm.Remove(key);
                if (p != null)
                {
                    data.Players[id] = NewPlayer(p.Name);
                    data.Players[id].LastSeen = p.LastSeen;
                    dirty = true;
                }
                Player online = OnlinePlayer(id);
                if (online != null) ApplyPrefix(online);
                Puts("Admin " + player.Name + " reset the renown record of " + name);
                Tell(player, Msg("AdminResetDone", player, name), ChatOk);
                return;
            }
            TellError(player, Msg("AdminUsage", player));
        }

        private void AdminStatus(Player player)
        {
            Tell(player, "Players on the roll: " + data.Players.Count + ", watched rebellions: " + data.Claims.Count
                + ", chronicle cursor: #" + data.ChronicleCursor + ", credited contracts: " + data.CreditedContracts.Count);
            Tell(player, "Plugins: Chronicle " + Loaded(RealmChronicle) + ", Houses " + Loaded(RealmHouses) + ", Crown " + Loaded(CrownAndConsequences)
                + ", Contracts " + Loaded(RealmContracts) + ", Laws " + Loaded(RealmLaws) + (chronicleTypeRejected ? " (title_earned not registered)" : ""));
            foreach (KeyValuePair<string, string> kv in feedStatus) Tell(player, "Feed " + kv.Key + ": " + kv.Value);
        }

        private static string Loaded(Plugin p)
        {
            return p != null ? "yes" : "no";
        }

        private bool PassCooldown(Player player)
        {
            if (config.CommandCooldownSeconds <= 0 || IsAdmin(player)) return true;
            string id = player.Id.ToString();
            DateTime now = Now();
            DateTime last;
            if (lastCommand.TryGetValue(id, out last) && (now - last).TotalSeconds < config.CommandCooldownSeconds)
            {
                TellError(player, Msg("Cooldown", player));
                return false;
            }
            lastCommand[id] = now;
            if (lastCommand.Count > 500) lastCommand.Clear();
            return true;
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        private int GetRenown(string playerId)
        {
            PlayerData p = data != null ? FindPlayer(playerId) : null;
            return p != null ? p.Renown : 0;
        }

        private int GetInfamy(string playerId)
        {
            PlayerData p = data != null ? FindPlayer(playerId) : null;
            return p != null ? p.Infamy : 0;
        }

        // Display name of the title the player wears, or null.
        private string GetChosenTitle(string playerId)
        {
            PlayerData p = data != null ? FindPlayer(playerId) : null;
            TitleDef t = p != null ? TitleById(p.Chosen) : null;
            return t != null ? t.Name : null;
        }

        // Ids of every title the player holds.
        private string[] GetTitles(string playerId)
        {
            PlayerData p = data != null ? FindPlayer(playerId) : null;
            if (p == null) return new string[0];
            return new List<string>(p.Titles.Keys).ToArray();
        }

        private bool HasTitle(string playerId, string titleId)
        {
            PlayerData p = data != null ? FindPlayer(playerId) : null;
            return p != null && titleId != null && p.Titles.ContainsKey(titleId.ToLowerInvariant());
        }

        // Lets another plugin report a deed directly (kind must be a configured deed). Cooldowns and daily caps apply.
        // dedupeKey (optional) stops the same deed from counting twice, e.g. "myplugin:event:42".
        private bool AddDeed(string playerId, string playerName, string kind, string note, string dedupeKey)
        {
            if (data == null) return false;
            return Award(playerId, playerName, NormalizeId(kind), note, string.IsNullOrEmpty(dedupeKey) ? null : CleanText(dedupeKey, 80));
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

        #endregion

        #region Helpers

        private PlayerData NewPlayer(string name)
        {
            return new PlayerData
            {
                Name = name,
                LastSeen = Now(),
                Stats = new Dictionary<string, int>(),
                Titles = new Dictionary<string, DateTime>(),
                Cooldowns = new Dictionary<string, DateTime>(),
                Log = new List<DeedEntry>()
            };
        }

        private PlayerData FindPlayer(string id)
        {
            PlayerData p;
            return id != null && data != null && data.Players.TryGetValue(id, out p) ? p : null;
        }

        private PlayerData GetPlayer(string id, string name, bool create)
        {
            PlayerData p = FindPlayer(id);
            string clean = name != null ? Clean(name) : null;
            if (p == null)
            {
                if (!create || data == null) return null;
                if (clean == null || clean.Length == 0)
                {
                    Player online = OnlinePlayer(id);
                    clean = online != null ? Clean(online.Name) : id;
                }
                p = NewPlayer(clean);
                data.Players[id] = p;
                dirty = true;
            }
            else if (clean != null && clean.Length > 0 && p.Name != clean)
            {
                p.Name = clean;
                dirty = true;
            }
            return p;
        }

        // Keeps the roll bounded: drops the longest-unseen players without renown, infamy or titles first.
        private void Prune()
        {
            if (data.Players.Count <= config.MaxPlayers) return;
            var list = new List<KeyValuePair<string, PlayerData>>(data.Players);
            list.Sort(delegate(KeyValuePair<string, PlayerData> a, KeyValuePair<string, PlayerData> b)
            {
                int wa = Weight(a.Value), wb = Weight(b.Value);
                if (wa != wb) return wa.CompareTo(wb);
                return a.Value.LastSeen.CompareTo(b.Value.LastSeen);
            });
            int remove = data.Players.Count - config.MaxPlayers;
            for (int i = 0; i < remove; i++) data.Players.Remove(list[i].Key);
            dirty = true;
        }

        private static int Weight(PlayerData p)
        {
            return p.Renown + p.Infamy + p.Titles.Count * 1000;
        }

        // Name -> id among players this plugin has seen. Null if unknown or ambiguous.
        private string ResolveName(string name)
        {
            string clean = Clean(name);
            if (clean.Length == 0) return null;
            string hit = null;
            foreach (KeyValuePair<string, PlayerData> kv in data.Players)
            {
                if (!string.Equals(kv.Value.Name, clean, StringComparison.OrdinalIgnoreCase)) continue;
                if (hit != null) return null;
                hit = kv.Key;
            }
            if (hit != null) return hit;
            foreach (Player pl in OnlinePlayers())
                if (string.Equals(Clean(pl.Name), clean, StringComparison.OrdinalIgnoreCase)) return pl.Id.ToString();
            return null;
        }

        // For commands: exact known name, else a unique online partial match. Tells the player when it fails.
        private string FindPlayerId(Player asker, string query)
        {
            string q = Clean(query);
            if (q.Length == 0) { TellError(asker, Msg("NotFound", asker, "")); return null; }
            if (IsSteamId(q) && FindPlayer(q) != null) return q;
            int exact = 0;
            string hit = null;
            foreach (KeyValuePair<string, PlayerData> kv in data.Players)
                if (string.Equals(kv.Value.Name, q, StringComparison.OrdinalIgnoreCase)) { exact++; hit = kv.Key; }
            if (exact == 1) return hit;
            if (exact > 1) { TellError(asker, Msg("Ambiguous", asker, q)); return null; }
            var partial = new List<Player>();
            foreach (Player pl in OnlinePlayers())
                if (pl.Name != null && pl.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(pl);
            if (partial.Count == 1) return partial[0].Id.ToString();
            TellError(asker, Msg(partial.Count > 1 ? "Ambiguous" : "NotFound", asker, q));
            return null;
        }

        private List<Player> OnlinePlayers()
        {
            var list = new List<Player>();
            if (Server.ClientPlayers == null) return list;
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private Player OnlinePlayer(string id)
        {
            ulong u;
            if (id == null || !ulong.TryParse(id, out u)) return null;
            foreach (Player p in OnlinePlayers()) if (p.Id == u) return p;
            return null;
        }

        private ulong KingId()
        {
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            return crown != null && crown.HasKing() ? crown.GetKingID() : 0;
        }

        private string KingName(ulong id)
        {
            Player online = OnlinePlayer(id.ToString());
            if (online != null) return online.Name;
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            string n = crown != null ? crown.GetKingName() : null;
            return string.IsNullOrEmpty(n) ? (id == cachedKingId ? cachedKingName : null) : n;
        }

        private static bool ParseIso(string s, out DateTime value)
        {
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
            {
                value = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
                return true;
            }
            return false;
        }

        private static string DayKey(DateTime t)
        {
            return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static bool SameName(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddCapped(List<string> list, string id, int cap)
        {
            if (list.Count < cap && !list.Contains(id)) list.Add(id);
        }

        private static bool IsSteamId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 5 || s.Length > 20) return false;
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        private static string NormalizeId(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.Length == 0 || t.Length > 40) return null;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return null;
            }
            return t;
        }

        private static bool ValidTitleName(string s)
        {
            if (s == null || s.Length < 3 || s.Length > 24 || s.Trim() != s || !char.IsLetter(s[0])) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '\'' || c == '-')) return false;
                if (c == ' ' && i > 0 && s[i - 1] == ' ') return false;
            }
            return true;
        }

        // Strips raw colour tags and control characters, collapses whitespace, truncates (like RealmChronicle.Clean).
        private static string Clean(string s)
        {
            return CleanText(s, NameMax);
        }

        private static string CleanText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[')
                {
                    int close = s.IndexOf(']', i);
                    if (close > i && close - i <= 9) { i = close; continue; }   // [RRGGBB], [-], [b] ...
                }
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    continue;
                }
                if (c == '{' || c == '}' || c == '%') continue;            // never let names act as format markers
                sb.Append(c);
            }
            string r = sb.ToString().Trim();
            if (r.Length > max) r = r.Substring(0, max).TrimEnd();
            return r;
        }

        private static string Cap(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string JoinFrom(string[] args, int start)
        {
            if (args == null || args.Length <= start) return "";
            var parts = new string[args.Length - start];
            Array.Copy(args, start, parts, 0, parts.Length);
            return string.Join(" ", parts).Trim();
        }

        private string Msg(string key, Player player, params object[] args)
        {
            string text = lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); }
            catch (FormatException) { return text; }
        }

        // Single-string overloads only (brace safety, doc section 4.2).
        private void Tell(Player player, string text)
        {
            Tell(player, text, ChatGold);
        }

        // tone: ChatGold for news, ChatOk for done, ChatWarn for take care (chat style).
        private void Tell(Player player, string text, string tone)
        {
            if (player != null) player.SendMessage(Styled(Msg("Speaker", player), tone, text));
        }

        private void TellError(Player player, string text)
        {
            if (player != null) player.SendError(Styled(Msg("Speaker", player), ChatError, text));
        }

        #endregion
    }
}
