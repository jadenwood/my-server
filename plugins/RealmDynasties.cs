// RealmDynasties: bloodlines, declared succession, blood claims to a fallen crown, and dynasty prestige.
//
//   /dynasty found <name>       a player founds a line and becomes its head (generation 1)
//   /dynasty heir name <player> the head names an heir; the heir must /dynasty accept (becomes the head's child)
//   succession                  headship and the line's titles pass to the first eligible heir in the declared order
//                               when the head abdicates (/dynasty abdicate) or has been inactive InactiveDaysForSuccession
//   blood claim                 when a monarch of the line loses the throne, the line's heirs may press a claim for
//                               BloodClaimWindowHours. The claim is raised through CrownAndConsequences' own /claim rules
//                               (house, leader, cooldown, rebellion window) and checked with its GetOpenClaims API
//   prestige                    reigns held and their length, oaths kept by the head's house, titles, generations,
//                               restorations; minus oaths and treaties broken (read from RealmHouses)
//   /dynasty tree               the family tree, from the founder down
//
// Cross-plugin calls (all optional; a missing plugin only disables the feature that needs it):
//   CrownAndConsequences  GetOpenClaims() -> string[] "house|status|startIso|endIso"; GetKingHouse() -> string;
//                         CmdClaim(Player, string, string[]): its own /claim chat command, a non-public instance method,
//                         which Oxide registers as a callable hook like every other non-public instance method
//                         [SRC Oxide.CSharp src/CSharpPlugin.cs:224-268, ctor scan of NonPublic|Instance members].
//                         We then confirm the claim exists with GetOpenClaims and never trust the call itself.
//   RealmHouses           GetHouse(string id), GetLiege(string house), GetVassals(string house),
//                         GetReputation(string house) -> {oathsBroken, treatiesBroken}, GetHouseLeader(string house).
//   RealmChronicle        Log(type, title, detail, actors) -> int id (0 = rejected, e.g. type not registered yet).
// RealmHouses exposes no call that changes a house's leader, so house leadership is only ADVISED on succession
// (the new head is told to ask for /house promote <name> leader); dynasty headship and titles pass for real.
//
// Game APIs (docs/oxide-rok-api.md): KingsScheme via SocialAPI.Get<KingsScheme>() with HasKing/GetKingID/GetKingName/
// IsKing [ASM][USE RaidBoss.cs:496]; Server.ClientPlayers, Player.Id/Name/IsServer [ASM]; OnPlayerConnected/
// OnPlayerDisconnected (core-dispatched) and OnThroneCaptured/OnThroneReleased [OPJ]. The crown is tracked by
// polling KingsScheme every tick; the two throne hooks only trigger an early poll.
//
// Data safety: oxide/data/RealmDynasties.json is read in Init. If it exists but cannot be parsed (or parses to
// nothing, e.g. a truncated file), the plugin refuses to load and never writes the file, so nothing is overwritten.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// The pure rules (succession choice, prestige, tree, downtime freeze, claim parsing) live in the nested static class
// Rules so they can be unit-tested without a game server (plugins/docs/RealmDynasties/logic-tests/).

using System;
using System.Collections.Generic;
using System.Globalization;
using CodeHatch.Common;                       // PlayerExtensions: SendMessage, SendError [ASM]
using CodeHatch.Engine.Modules.SocialSystem;  // SocialAPI [ASM]
using CodeHatch.Engine.Networking;            // Player, Server [ASM]
using CodeHatch.Thrones.AncientThrone;        // AncientThroneCaptureEvent, AncientThroneReleaseEvent [ASM]
using CodeHatch.Thrones.SocialSystem;         // KingsScheme [ASM]
using Oxide.Core;                             // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                     // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmDynasties", "Realm", "0.1.0")]
    [Description("Bloodlines and succession: heirs, declared succession, blood claims to a fallen crown, family trees and dynasty prestige")]
    public class RealmDynasties : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmdynasties.admin";
        private const string DataName = "RealmDynasties";

        internal const string BloodOpen = "open";          // the crown fell from the line; no claim pressed yet
        internal const string BloodAwaiting = "awaiting";  // pressed; waiting for the house's claim to appear in CrownAndConsequences
        internal const string BloodLinked = "linked";      // tied to an open CrownAndConsequences claim and its window

        private PluginConfig config;
        private StoredData data;
        private bool initialized;
        private Timer tickTimer;

        private readonly Dictionary<string, Dynasty> dynByKey = new Dictionary<string, Dynasty>();
        private readonly Dictionary<string, Dynasty> dynByMember = new Dictionary<string, Dynasty>();

        // Short-lived state, memory only: a reload drops it.
        private readonly Dictionary<string, Offer> offers = new Dictionary<string, Offer>();             // target id -> offer
        private readonly Dictionary<string, DateTime> lastOfferBy = new Dictionary<string, DateTime>();  // head id -> UTC
        private readonly Dictionary<string, DateTime> confirmUntil = new Dictionary<string, DateTime>(); // "id|action" -> UTC
        private readonly Dictionary<string, DateTime> lastClaimCmd = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> lastCommand = new Dictionary<string, DateTime>();
        private readonly List<DateTime> chronicleTimes = new List<DateTime>();
        private readonly Dictionary<string, bool> warnedTypes = new Dictionary<string, bool>();

        #region Config

        internal class PluginConfig
        {
            public float TickSeconds = 60f;
            public int NameMinLength = 3;
            public int NameMaxLength = 24;
            public int TitleMaxLength = 40;
            public int MaxDynasties = 200;
            public int MaxMembersPerDynasty = 24;
            public int MaxHeirsInLine = 8;
            public int MaxTitlesPerDynasty = 6;
            public int MaxPendingOffersPerDynasty = 3;
            public int OfferExpireSeconds = 300;
            public double FoundCooldownHours = 24;          // per player, between founding lines
            public double RejoinCooldownHours = 12;         // after leaving or being disowned, before joining or founding again
            public int HeirOfferCooldownSeconds = 30;       // per head, between two heir offers
            public double AbdicateCooldownHours = 24;       // after any succession, before the line may abdicate again
            public int DisownCooldownMinutes = 30;          // per head
            public double InactiveDaysForSuccession = 14;   // a head unseen this long passes the line on; heirs unseen this long are skipped
            public bool FreezeInactivityDuringDowntime = true;
            public int DowntimeGapMinutes = 10;             // a gap between ticks longer than this counts as server downtime
            public double MinHeadshipHoursForGeneration = 24;
            public int MaxGenerationsCounted = 10;
            public int MinReignMinutesToCount = 30;         // a reign shorter than this is not a "reign held"
            public double ReignRecountCooldownHours = 24;   // a line's next reign counts as a new one only after this long
            public double BloodClaimWindowHours = 72;
            public double RestorationCooldownHours = 72;    // per line, between two restorations that earn prestige
            public bool DelegateToCrownClaim = true;        // raise the house claim through CrownAndConsequences' /claim declare
            public bool AllowFallenMonarchToClaim = false;
            public int ClaimCommandCooldownSeconds = 60;
            public int BestowCooldownMinutes = 60;          // crown-wide, between two titles bestowed
            public int MaxBestowsPerReign = 5;
            public bool BestowTransfersTitles = true;       // bestowing a title another line holds takes it from them
            public int PrestigePerReign = 100;
            public double PrestigePerReignHour = 2;
            public double ReignHoursCap = 500;
            public double PrestigePerOathDay = 5;
            public double OathDaysCap = 365;
            public int OathPartnerMinMembers = 3;           // an oath earns prestige only with a liege or vassal house of at
                                                            // least this many members (a sworn one-man alt house is no oath)
            public int PenaltyPerOathBroken = 75;
            public int PenaltyPerTreatyBroken = 25;
            public int PrestigePerTitle = 25;
            public int PrestigePerGeneration = 30;
            public int PrestigePerRestoration = 150;
            public int AdminAdjustLimit = 1000;
            public int MaxListLines = 15;
            public int MaxTreeLines = 30;
            public int MaxHistoryEntries = 25;
            public bool BroadcastEvents = true;
            public bool ChronicleEnabled = true;
            public bool ChronicleHeirNamed = true;
            public int ChronicleMaxPerHour = 12;
            public float CommandThrottleSeconds = 1f;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void ClampConfig()
        {
            PluginConfig c = config;
            if (c.TickSeconds < 15f) c.TickSeconds = 15f;
            if (c.NameMinLength < 2) c.NameMinLength = 2;
            if (c.NameMaxLength < c.NameMinLength) c.NameMaxLength = c.NameMinLength;
            if (c.NameMaxLength > 40) c.NameMaxLength = 40;
            if (c.TitleMaxLength < 3) c.TitleMaxLength = 3;
            if (c.TitleMaxLength > 60) c.TitleMaxLength = 60;
            if (c.MaxDynasties < 1) c.MaxDynasties = 1;
            if (c.MaxMembersPerDynasty < 1) c.MaxMembersPerDynasty = 1;
            if (c.MaxMembersPerDynasty > 100) c.MaxMembersPerDynasty = 100;
            if (c.MaxHeirsInLine < 1) c.MaxHeirsInLine = 1;
            if (c.MaxHeirsInLine > 20) c.MaxHeirsInLine = 20;
            if (c.MaxTitlesPerDynasty < 0) c.MaxTitlesPerDynasty = 0;
            if (c.MaxTitlesPerDynasty > 20) c.MaxTitlesPerDynasty = 20;
            if (c.MaxPendingOffersPerDynasty < 1) c.MaxPendingOffersPerDynasty = 1;
            if (c.OfferExpireSeconds < 30) c.OfferExpireSeconds = 30;
            if (c.FoundCooldownHours < 0) c.FoundCooldownHours = 0;
            if (c.RejoinCooldownHours < 0) c.RejoinCooldownHours = 0;
            if (c.HeirOfferCooldownSeconds < 0) c.HeirOfferCooldownSeconds = 0;
            if (c.AbdicateCooldownHours < 0) c.AbdicateCooldownHours = 0;
            if (c.DisownCooldownMinutes < 0) c.DisownCooldownMinutes = 0;
            if (c.InactiveDaysForSuccession < 1) c.InactiveDaysForSuccession = 1;
            if (c.DowntimeGapMinutes < 2) c.DowntimeGapMinutes = 2;
            if (c.MinHeadshipHoursForGeneration < 0) c.MinHeadshipHoursForGeneration = 0;
            if (c.MaxGenerationsCounted < 1) c.MaxGenerationsCounted = 1;
            if (c.MinReignMinutesToCount < 0) c.MinReignMinutesToCount = 0;
            if (c.ReignRecountCooldownHours < 0) c.ReignRecountCooldownHours = 0;
            if (c.BloodClaimWindowHours < 1) c.BloodClaimWindowHours = 1;
            if (c.RestorationCooldownHours < 0) c.RestorationCooldownHours = 0;
            if (c.ClaimCommandCooldownSeconds < 0) c.ClaimCommandCooldownSeconds = 0;
            if (c.BestowCooldownMinutes < 0) c.BestowCooldownMinutes = 0;
            if (c.MaxBestowsPerReign < 0) c.MaxBestowsPerReign = 0;
            if (c.ReignHoursCap < 0) c.ReignHoursCap = 0;
            if (c.OathDaysCap < 0) c.OathDaysCap = 0;
            if (c.OathPartnerMinMembers < 0) c.OathPartnerMinMembers = 0;
            if (c.AdminAdjustLimit < 0) c.AdminAdjustLimit = 0;
            if (c.MaxListLines < 1) c.MaxListLines = 1;
            if (c.MaxTreeLines < 3) c.MaxTreeLines = 3;
            if (c.MaxHistoryEntries < 0) c.MaxHistoryEntries = 0;
            if (c.ChronicleMaxPerHour < 0) c.ChronicleMaxPerHour = 0;
            if (c.CommandThrottleSeconds < 0f) c.CommandThrottleSeconds = 0f;
        }

        #endregion

        #region Data

        internal class Member
        {
            public string Id;
            public string Name;
            public string ParentId;            // who named them heir (the head at the time); null for the founder
            public int Generation = 1;
            public DateTime Joined;
            public DateTime LastSeen;          // UTC; shifted forward over server downtime when FreezeInactivityDuringDowntime
            public bool WasHead;               // an elder: has headed the line before
        }

        internal class BloodRight
        {
            public string FallenKingId;
            public string FallenKingName;
            public DateTime FallenAt;
            public DateTime ExpiresAt;         // last moment a claim may be pressed (open / awaiting)
            public string Status;              // open | awaiting | linked
            public string ClaimantId;
            public string ClaimantName;
            public string House;
            public DateTime? WindowStart;
            public DateTime? WindowEnd;
            public bool Usurped;               // someone outside the line has reigned since the fall
        }

        internal class Dynasty
        {
            public string Name;
            public string FounderId;
            public string FounderName;
            public DateTime Founded;
            public string HeadId;
            public DateTime HeadSince;
            public List<Member> Members = new List<Member>();
            public List<string> Line = new List<string>();        // ordered heir ids, first = next head
            public List<string> Titles = new List<string>();
            public int ReignsHeld;
            public double ReignHours;
            public double OathKeptHours;
            public int OathsBroken;
            public int TreatiesBroken;
            public int Restorations;
            public int MaxGenerationCounted = 1;
            public int Successions;
            public int PrestigeAdjust;
            public string SeatHouse;                              // the head's house when last read from RealmHouses
            public int SeatOathsSnapshot;
            public int SeatTreatiesSnapshot;
            public DateTime? LastSuccessionAt;
            public DateTime? LastDisownAt;
            public DateTime? LastReignCountedAt;
            public DateTime? LastRestorationAt;
            public BloodRight Blood;
            public bool DormantNotified;
            public List<string> History = new List<string>();
        }

        internal class ReignState
        {
            public string KingId;              // null = vacant
            public string KingName;
            public string Dynasty;             // the king's line while it reigns, or null
            public DateTime Since;
            public DateTime DynastySince;
            public bool Counted;
            public DateTime LastAccrual;
            public int Bestows;
        }

        internal class StoredData
        {
            public int Version = 1;
            public List<Dynasty> Dynasties = new List<Dynasty>();
            public Dictionary<string, DateTime> LastFounded = new Dictionary<string, DateTime>();       // player id -> UTC
            public Dictionary<string, DateTime> RejoinBlockedUntil = new Dictionary<string, DateTime>(); // player id -> UTC
            public ReignState Reign = new ReignState();
            public DateTime? LastBestowAt;
            public DateTime LastTickAt;
        }

        private class Offer
        {
            public string DynastyKey;
            public string FromId;
            public DateTime Expires;
        }

        private void LoadData()
        {
            bool exists = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            StoredData loaded;
            try
            {
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                // Refuse to run on a damaged file rather than overwrite every line with an empty list.
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message + ". Fix or remove the file, then reload. The file was not changed.");
                throw;
            }
            if (loaded == null)
            {
                if (exists)
                {
                    // An empty or truncated file deserializes to null. Treat it like a parse failure.
                    PrintError("oxide/data/" + DataName + ".json exists but holds no data. Fix or remove the file, then reload. The file was not changed.");
                    throw new Exception(DataName + ": refusing to start on an empty data file");
                }
                loaded = new StoredData();
            }
            Rules.Normalize(loaded, DateTime.UtcNow);
            data = loaded;
            RebuildIndex();
        }

        private void RebuildIndex()
        {
            dynByKey.Clear();
            dynByMember.Clear();
            foreach (Dynasty d in data.Dynasties)
            {
                dynByKey[Key(d.Name)] = d;
                foreach (Member m in d.Members) dynByMember[m.Id] = d;
            }
        }

        private void SaveData()
        {
            if (data == null) return;                       // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
        }

        #endregion

        #region Rules (pure, unit-tested)

        internal static class Rules
        {
            // Repairs a freshly loaded data object in place: null lists, broken entries, dangling references.
            internal static void Normalize(StoredData d, DateTime now)
            {
                if (d.Dynasties == null) d.Dynasties = new List<Dynasty>();
                if (d.LastFounded == null) d.LastFounded = new Dictionary<string, DateTime>();
                if (d.RejoinBlockedUntil == null) d.RejoinBlockedUntil = new Dictionary<string, DateTime>();
                if (d.Reign == null) d.Reign = new ReignState();
                d.Dynasties.RemoveAll(delegate(Dynasty x) { return x == null || string.IsNullOrEmpty(x.Name); });
                var seenNames = new Dictionary<string, bool>();
                var seenMembers = new Dictionary<string, bool>();
                foreach (Dynasty dyn in new List<Dynasty>(d.Dynasties))
                {
                    string key = dyn.Name.ToLowerInvariant();
                    if (seenNames.ContainsKey(key)) { d.Dynasties.Remove(dyn); continue; }
                    seenNames[key] = true;
                    if (dyn.Members == null) dyn.Members = new List<Member>();
                    if (dyn.Line == null) dyn.Line = new List<string>();
                    if (dyn.Titles == null) dyn.Titles = new List<string>();
                    if (dyn.History == null) dyn.History = new List<string>();
                    // A player may belong to one line only; the first line in the file keeps them.
                    dyn.Members.RemoveAll(delegate(Member m)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Id) || seenMembers.ContainsKey(m.Id)) return true;
                        seenMembers[m.Id] = true;
                        return false;
                    });
                    foreach (Member m in dyn.Members)
                    {
                        if (m.Generation < 1) m.Generation = 1;
                        if (m.Name == null) m.Name = m.Id;
                        if (m.Joined == default(DateTime)) m.Joined = now;
                        if (m.LastSeen == default(DateTime)) m.LastSeen = now;   // unknown = not yet judged, not "gone"
                    }
                    if (dyn.Members.Count == 0) { d.Dynasties.Remove(dyn); continue; }
                    if (Find(dyn, dyn.HeadId) == null) dyn.HeadId = dyn.Members[0].Id;
                    var lineSeen = new Dictionary<string, bool>();
                    dyn.Line.RemoveAll(delegate(string id)
                    {
                        if (id == null || id == dyn.HeadId || Find(dyn, id) == null || lineSeen.ContainsKey(id)) return true;
                        lineSeen[id] = true;
                        return false;
                    });
                    dyn.Titles.RemoveAll(delegate(string t) { return string.IsNullOrEmpty(t); });
                    if (dyn.MaxGenerationCounted < 1) dyn.MaxGenerationCounted = 1;
                    if (dyn.Blood != null && (dyn.Blood.Status == null || dyn.Blood.FallenKingName == null)) dyn.Blood = null;
                    foreach (Member m in dyn.Members)
                        if (m.ParentId != null && Find(dyn, m.ParentId) == null) m.ParentId = null;
                }
            }

            internal static Member Find(Dynasty d, string id)
            {
                if (d == null || id == null) return null;
                foreach (Member m in d.Members) if (m.Id == id) return m;
                return null;
            }

            // Unique case-insensitive match on a member name: exact first, then prefix.
            internal static Member FindByName(Dynasty d, string text)
            {
                if (d == null || string.IsNullOrEmpty(text)) return null;
                foreach (Member m in d.Members)
                    if (string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)) return m;
                Member found = null;
                foreach (Member m in d.Members)
                {
                    if (m.Name == null || !m.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (found != null) return null;                 // ambiguous
                    found = m;
                }
                return found;
            }

            internal static bool ValidName(string s, int min, int max)
            {
                if (s == null || s.Length < min || s.Length > max) return false;
                if (!char.IsLetter(s[0]) || s[s.Length - 1] == ' ') return false;
                for (int i = 0; i < s.Length; i++)
                {
                    char ch = s[i];
                    if (char.IsLetterOrDigit(ch) || ch == '\'' || ch == '-') continue;
                    if (ch == ' ' && s[i - 1] != ' ') continue;
                    return false;
                }
                return true;
            }

            internal static bool IsInactive(Member m, DateTime now, double days, bool online)
            {
                if (m == null) return true;
                if (online) return false;
                return (now - m.LastSeen).TotalDays > days;
            }

            // First heir in the declared line who still belongs to the line and (if required) has been seen recently.
            internal static Member PickSuccessor(Dynasty d, DateTime now, double inactiveDays, Predicate<string> isOnline, bool requireActive)
            {
                foreach (string id in d.Line)
                {
                    if (id == d.HeadId) continue;
                    Member m = Find(d, id);
                    if (m == null) continue;
                    if (requireActive && IsInactive(m, now, inactiveDays, isOnline != null && isOnline(id))) continue;
                    return m;
                }
                return null;
            }

            // Makes newHead the head. Returns the previous head (may be null). The generation the line reaches is only
            // credited when the previous head held the line for minHeadshipHours (no prestige for quick abdication chains).
            internal static Member ApplySuccession(Dynasty d, Member newHead, DateTime now, double minHeadshipHours, int maxGenerations)
            {
                Member old = Find(d, d.HeadId);
                bool credit = old == null || (now - d.HeadSince).TotalHours >= minHeadshipHours;
                if (old != null) old.WasHead = true;
                d.Line.Remove(newHead.Id);
                d.HeadId = newHead.Id;
                d.HeadSince = now;
                if (credit) d.MaxGenerationCounted = Math.Max(d.MaxGenerationCounted, Math.Min(newHead.Generation, maxGenerations));
                d.Successions++;
                d.LastSuccessionAt = now;
                d.DormantNotified = false;
                return old;
            }

            // Removes a non-head member; their children are re-parented to the member's own parent.
            internal static bool RemoveMember(Dynasty d, string id)
            {
                Member m = Find(d, id);
                if (m == null || id == d.HeadId) return false;
                d.Members.Remove(m);
                d.Line.Remove(id);
                foreach (Member c in d.Members)
                    if (c.ParentId == id) c.ParentId = m.ParentId;
                return true;
            }

            // Moves an heir to a 1-based position in the line (clamped). False if they are not in the line.
            internal static bool MoveInLine(Dynasty d, string id, int position)
            {
                int at = d.Line.IndexOf(id);
                if (at < 0) return false;
                d.Line.RemoveAt(at);
                int to = Math.Max(0, Math.Min(d.Line.Count, position - 1));
                d.Line.Insert(to, id);
                return true;
            }

            internal static int Prestige(Dynasty d, PluginConfig c)
            {
                double score = 0;
                score += d.ReignsHeld * (double)c.PrestigePerReign;
                score += Math.Min(d.ReignHours, c.ReignHoursCap) * c.PrestigePerReignHour;
                score += Math.Min(d.OathKeptHours / 24.0, c.OathDaysCap) * c.PrestigePerOathDay;
                score -= d.OathsBroken * (double)c.PenaltyPerOathBroken;
                score -= d.TreatiesBroken * (double)c.PenaltyPerTreatyBroken;
                score += d.Titles.Count * (double)c.PrestigePerTitle;
                score += (Math.Min(d.MaxGenerationCounted, c.MaxGenerationsCounted) - 1) * (double)c.PrestigePerGeneration;
                score += d.Restorations * (double)c.PrestigePerRestoration;
                score += d.PrestigeAdjust;
                return (int)Math.Max(0, Math.Floor(score));
            }

            internal static string RoleOf(Dynasty d, Member m)
            {
                if (m.Id == d.HeadId) return "head";
                int at = d.Line.IndexOf(m.Id);
                if (at >= 0) return "heir " + (at + 1);
                return m.WasHead ? "elder" : "kin";
            }

            // Indented family tree, founder(s) first, children by join date. Cycle-safe; capped at maxLines.
            internal static List<string> Tree(Dynasty d, int maxLines)
            {
                var lines = new List<string>();
                var visited = new Dictionary<string, bool>();
                var ordered = new List<Member>(d.Members);
                ordered.Sort(delegate(Member a, Member b) { return a.Joined.CompareTo(b.Joined); });
                int hidden = 0;
                foreach (Member root in ordered)
                    if (root.ParentId == null || Find(d, root.ParentId) == null)
                        Walk(d, root, 0, ordered, visited, lines, maxLines, ref hidden);
                // Anything not reached (only possible with a corrupted parent cycle) is listed flat.
                foreach (Member m in ordered)
                    if (!visited.ContainsKey(m.Id)) Walk(d, m, 0, ordered, visited, lines, maxLines, ref hidden);
                if (hidden > 0) lines.Add("... and " + hidden + " more");
                return lines;
            }

            private static void Walk(Dynasty d, Member m, int depth, List<Member> ordered, Dictionary<string, bool> visited,
                List<string> lines, int maxLines, ref int hidden)
            {
                if (visited.ContainsKey(m.Id)) return;
                visited[m.Id] = true;
                if (lines.Count < maxLines - 1)
                    lines.Add(new string(' ', depth * 2) + (depth > 0 ? "- " : "") + m.Name + " [" + RoleOf(d, m) + ", gen " + m.Generation + "]");
                else hidden++;
                foreach (Member c in ordered)
                    if (c.ParentId == m.Id) Walk(d, c, depth + 1, ordered, visited, lines, maxLines, ref hidden);
            }

            // Server downtime should not count as players being away: shift last-seen times (and open claim
            // deadlines) forward by the gap, never past now.
            internal static void ShiftForDowntime(StoredData s, TimeSpan gap, DateTime now)
            {
                if (gap <= TimeSpan.Zero) return;
                foreach (Dynasty d in s.Dynasties)
                {
                    foreach (Member m in d.Members)
                    {
                        DateTime moved = m.LastSeen + gap;
                        m.LastSeen = moved > now ? now : moved;
                    }
                    if (d.Blood != null && d.Blood.Status != BloodLinked) d.Blood.ExpiresAt = d.Blood.ExpiresAt + gap;
                }
            }

            // Finds the open CrownAndConsequences claim of a house in GetOpenClaims() output ("house|status|startIso|endIso").
            internal static bool ParseClaim(string[] raw, string house, out string status, out DateTime start, out DateTime end)
            {
                status = null;
                start = DateTime.MinValue;
                end = DateTime.MinValue;
                if (raw == null || house == null) return false;
                foreach (string line in raw)
                {
                    if (line == null) continue;
                    string[] p = line.Split('|');
                    if (p.Length < 4 || !string.Equals(p[0], house, StringComparison.OrdinalIgnoreCase)) continue;
                    DateTime s, e;
                    if (!TryIso(p[2], out s) || !TryIso(p[3], out e)) continue;
                    status = p[1];
                    start = s;
                    end = e;
                    return true;
                }
                return false;
            }

            internal static bool TryIso(string text, out DateTime value)
            {
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
                {
                    if (value.Kind == DateTimeKind.Local) value = value.ToUniversalTime();
                    else if (value.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
                    return true;
                }
                return false;
            }

            // Adds elapsed time to an accumulator, but never more than maxStep (protects against clock jumps and downtime).
            internal static double Accrue(double total, DateTime last, DateTime now, double maxStepHours)
            {
                double hours = (now - last).TotalHours;
                if (hours <= 0) return total;
                return total + Math.Min(hours, maxStepHours);
            }

            internal static bool CountsAsNewReign(Dynasty d, DateTime now, double recountHours)
            {
                return !d.LastReignCountedAt.HasValue || (now - d.LastReignCountedAt.Value).TotalHours >= recountHours;
            }

            // A king from outside the line (newLine) now reigns: every other line's standing blood right is usurped.
            internal static void MarkUsurped(StoredData s, Dynasty newLine)
            {
                foreach (Dynasty d in s.Dynasties)
                    if (d != newLine && d.Blood != null) d.Blood.Usurped = true;
            }

            // A restoration needs a pressed claim and an outsider's reign between the fall and the return,
            // so a line cannot farm it by stepping off an empty throne and sitting back down.
            internal static bool EarnsRestoration(BloodRight b, Dynasty d, DateTime now, double cooldownHours)
            {
                if (b == null || b.Status == BloodOpen || !b.Usurped) return false;
                return !d.LastRestorationAt.HasValue || (now - d.LastRestorationAt.Value).TotalHours >= cooldownHours;
            }

            internal static void AddHistory(Dynasty d, string line, DateTime now, int max)
            {
                if (max <= 0) return;
                d.History.Add(now.ToString("yyyy-MM-dd HH:mm") + " " + line);
                while (d.History.Count > max) d.History.RemoveAt(0);
            }
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Herald", "[C8A050]Herald[FFFFFF]: " },
                { "Help1", "Dynasties: /dynasty found <name> | info [line|player] | list | tree [line] | accept | decline | leave" },
                { "Help2", "Head of a line: /dynasty heir name <player> | heir remove <player> | heir order <player> <position> | disown <player> | abdicate | dissolve" },
                { "Help3", "Blood and crown: /dynasty claim (heirs of a fallen monarch) | /dynasty bestow \"<line>\" <title> (the reigning monarch)" },
                { "Help4", "Succession: when the head abdicates or is unseen for {0} days, the first active heir in the line becomes head and takes the line's titles." },
                { "HelpAdmin", "Admin: /dynasty admin pass \"<line>\" [heir] | dissolve \"<line>\" | title add|remove \"<line>\" <title> | prestige \"<line>\" <+/-n> | check | save" },
                { "NoPermission", "You may not do that." },
                { "Throttled", "Slow down." },
                { "NotInDynasty", "You belong to no line. Found one with /dynasty found <name>." },
                { "AlreadyInDynasty", "You already belong to the line of {0}." },
                { "NoSuchDynasty", "No line named '{0}' exists." },
                { "NoSuchPlayer", "No online player matches '{0}'." },
                { "NoSuchMember", "No member of your line matches '{0}'." },
                { "NotHead", "Only the head of your line may do that." },
                { "BadName", "A line's name must be {0}-{1} characters: letters, digits, single spaces, ' or -, starting with a letter." },
                { "BadTitle", "A title must be 3-{0} characters: letters, digits, single spaces, ' or -, starting with a letter." },
                { "NameTaken", "A line named {0} already exists." },
                { "TooManyDynasties", "The realm cannot record more lines." },
                { "FoundCooldown", "You may found another line in {0}." },
                { "RejoinCooldown", "You left a line too recently. You may join or found one in {0}." },
                { "Founded", "The line of {0} is founded. You are its head." },
                { "BroadcastFounded", "{0} founds the line of {1}." },
                { "HeirSelf", "You cannot name yourself." },
                { "HeirInOther", "{0} belongs to the line of {1} and must leave it first." },
                { "HeirAlready", "{0} is already in your line of succession." },
                { "LineFull", "Your line of succession is full ({0} heirs)." },
                { "DynastyFull", "Your line already has {0} members." },
                { "TooManyOffers", "Your line already has {0} offers waiting for an answer." },
                { "OfferCooldown", "Wait {0} s before naming another heir." },
                { "OfferSent", "You name {0} heir of the line of {1}. They must type /dynasty accept within {2} s." },
                { "OfferReceived", "{0} names you heir of the line of {1}. Type /dynasty accept or /dynasty decline within {2} s." },
                { "NoOffer", "No one has named you heir." },
                { "OfferDeclined", "{0} declines to be named heir." },
                { "YouDeclined", "You decline." },
                { "HeirAccepted", "{0} is now heir {1} of the line of {2}." },
                { "BroadcastHeir", "{0} is named heir of the line of {1}." },
                { "KinRestored", "{0} is placed in the line of succession at position {1}." },
                { "HeirRemoved", "{0} is removed from the line of succession but remains kin." },
                { "NotHeir", "{0} is not in your line of succession." },
                { "HeirMoved", "{0} is now heir {1}." },
                { "HeirOrderUsage", "Usage: /dynasty heir order <player> <position>" },
                { "HeirUsage", "Usage: /dynasty heir name <player> | remove <player> | order <player> <position> | list" },
                { "LineHeader", "Line of succession of {0}:" },
                { "LineEntry", "  {0}. {1}{2}" },
                { "LineEmpty", "  (no heirs named)" },
                { "Inactive", " (unseen {0} days)" },
                { "DisownSelf", "You cannot disown yourself. Abdicate or dissolve instead." },
                { "DisownCooldown", "You may disown again in {0} min." },
                { "Disowned", "{0} is cast out of the line of {1}." },
                { "YouWereDisowned", "You have been cast out of the line of {0}." },
                { "HeadCannotLeave", "The head cannot leave. Abdicate first, or dissolve the line if you are its only member." },
                { "LeaveConfirm", "Leaving the line of {0} cannot be undone, and you may not join or found a line for {1}. Type /dynasty leave confirm within 60 s." },
                { "Left", "You leave the line of {0}." },
                { "MemberLeft", "{0} has left the line of {1}." },
                { "AbdicateConfirm", "Abdicating passes the line of {0} and its titles to {1}. Type /dynasty abdicate confirm within 60 s." },
                { "AbdicateNoHeir", "No active heir can take the line. Name an heir first." },
                { "AbdicateCooldown", "The line changed hands too recently. You may abdicate in {0}." },
                { "DissolveNotAlone", "Only a line with a single member may be dissolved. Others must leave or be disowned first." },
                { "DissolveConfirm", "Dissolving the line of {0} erases it and returns its titles to the crown. Type /dynasty dissolve confirm within 60 s." },
                { "Dissolved", "The line of {0} is ended." },
                { "Succession", "{0} succeeds {1} as head of the line of {2} ({3})." },
                { "HouseAdvice", "You lead House {0} in the realm's records. To pass the house to {1} as well, use /house promote {1} leader." },
                { "Dormant", "The head of the line of {0} is unseen and no active heir can take the line." },
                { "InfoHeader", "The line of {0} - founded {1} by {2}" },
                { "InfoHead", "Head: {0} (gen {1}) since {2} UTC | Members: {3}/{4}" },
                { "InfoTitles", "Titles: {0}" },
                { "InfoPrestige", "Prestige {0}: reigns {1}, {2} h on the throne, {3} days of oaths kept, {4} oaths / {5} treaties broken, {6} restorations, {7} generations" },
                { "InfoSeat", "Seat: House {0}" },
                { "InfoBlood", "Blood right: the crown fell from {0}; {1}" },
                { "BloodOpenText", "a claim may be pressed for another {0}" },
                { "BloodAwaitingText", "pressed by {0}; waiting for House {1} to raise its claim (before {2} UTC)" },
                { "BloodLinkedText", "pressed by {0} through House {1}; rebellion {2} to {3} UTC" },
                { "InfoHistory", "Recent: {0}" },
                { "None", "none" },
                { "ListHeader", "Lines of the realm by prestige ({0}):" },
                { "ListLine", "  {0}. {1} - {2} prestige, head {3}, {4} members{5}" },
                { "ListEmpty", "No lines have been founded." },
                { "TreeHeader", "Family tree of the line of {0}:" },
                { "ClaimNoRight", "The crown has not fallen from your line, or the time to press the claim has passed." },
                { "ClaimNotHeir", "Only the head or a named heir of the line may press its blood claim." },
                { "ClaimFallen", "The fallen monarch may not press the claim. An heir must." },
                { "ClaimAlready", "The blood claim is already pressed by {0}." },
                { "ClaimHolding", "Your line already holds the crown." },
                { "ClaimNoHouse", "A blood claim is raised through a house. Join or found a house first." },
                { "ClaimCooldown", "Wait {0} s before pressing the claim again." },
                { "ClaimLinked", "Your blood claim rides on House {0}'s claim. The rebellion runs {1} to {2} UTC." },
                { "ClaimAwaiting", "Your blood claim is recorded. House {0} has no open claim yet: its head must /claim declare before {1} UTC, or the right lapses." },
                { "ClaimNoCrownPlugin", "Your blood claim is recorded, but the crown's claim rules are not running here. It will be honoured only if the line retakes the throne before {0} UTC." },
                { "BroadcastClaim", "{0} presses the blood claim of the line of {1} to the crown of {2}." },
                { "BloodFallen", "The crown has fallen from {0} of your line. An heir may press the blood claim with /dynasty claim within {1} h." },
                { "BloodLapsed", "The blood claim of the line of {0} has lapsed." },
                { "BloodRestored", "The line of {0} regains the crown." },
                { "ReignCounted", "{0} of the line of {1} has held the throne long enough: the reign is entered in the line's record." },
                { "BestowNotKing", "Only the reigning monarch may bestow titles." },
                { "BestowOwn", "The crown may not bestow titles on its own line." },
                { "BestowCooldown", "The crown may bestow another title in {0} min." },
                { "BestowLimit", "The crown has bestowed {0} titles this reign, the most allowed." },
                { "BestowFull", "The line of {0} already holds {1} titles." },
                { "BestowHeld", "The line of {0} already holds that title." },
                { "BestowTaken", "That title belongs to the line of {0}." },
                { "BestowUsage", "Usage: /dynasty bestow \"<line>\" <title>" },
                { "Bestowed", "The crown bestows the title {0} on the line of {1}{2}." },
                { "BestowedFrom", ", taking it from the line of {0}" },
                { "TitleAdded", "Title {0} given to the line of {1}." },
                { "TitleRemoved", "Title {0} taken from the line of {1}." },
                { "TitleMissing", "The line of {0} holds no title {1}." },
                { "AdminPassed", "The line of {0} is passed to {1}." },
                { "AdminNoTarget", "No member of that line matches, or no heir is available." },
                { "PrestigeAdjusted", "Prestige adjustment for the line of {0} is now {1} (score {2})." },
                { "Checked", "Succession, crown and claim checks ran." },
                { "Saved", "Dynasty data saved." },
                { "AdminUsage", "Usage: /dynasty admin pass \"<line>\" [heir] | dissolve \"<line>\" | title add|remove \"<line>\" <title> | prestige \"<line>\" <+/-n> | check | save" },
                { "Unknown", "Unknown subcommand. Type /dynasty help." }
            }, this);
        }

        private string Msg(string key, Player player, params object[] args)
        {
            string text = lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
            return args.Length > 0 ? string.Format(text, args) : text;   // player text only ever goes in as an argument
        }

        // Single-string overloads only (docs/oxide-rok-api.md 4.2, brace safety).
        private void Reply(Player player, string key, params object[] args)
        {
            player.SendMessage(Msg(key, player, args));
        }

        private void Error(Player player, string key, params object[] args)
        {
            player.SendError(Msg(key, player, args));
        }

        private void NotifyId(string id, string key, params object[] args)
        {
            Player p = OnlineById(id);
            if (p != null) Reply(p, key, args);
        }

        private void NotifyDynasty(Dynasty d, string exceptId, string key, params object[] args)
        {
            foreach (Member m in d.Members)
                if (m.Id != exceptId) NotifyId(m.Id, key, args);
        }

        private void Broadcast(string key, params object[] args)
        {
            string text = Msg(key, null, args);
            Puts(text);
            if (config.BroadcastEvents) Server.BroadcastMessage(Msg("Herald", null) + text);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            if (config == null) config = new PluginConfig();
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            // Re-sent on hot load: keep idempotent.
            if (tickTimer != null) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, Tick);
            if (!initialized)
            {
                initialized = true;
                DateTime now = DateTime.UtcNow;
                foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) Seen(p, now);
                NextTick(Tick);
            }
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            SaveData();
        }

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer || data == null) return;
            Seen(player, DateTime.UtcNow);
            Offer o;
            if (offers.TryGetValue(player.Id.ToString(), out o) && o.Expires > DateTime.UtcNow)
            {
                Dynasty d;
                if (dynByKey.TryGetValue(o.DynastyKey, out d))
                    Reply(player, "OfferReceived", NameOf(d, o.FromId), d.Name, (int)Math.Ceiling((o.Expires - DateTime.UtcNow).TotalSeconds));
            }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null || player.IsServer || data == null) return;
            Seen(player, DateTime.UtcNow);
        }

        // Early poll only; the tick reconciles with KingsScheme either way.
        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (data != null) NextTick(SyncCrownSafe);
        }

        private void OnThroneReleased(AncientThroneReleaseEvent evt)
        {
            if (data != null) NextTick(SyncCrownSafe);
        }

        private void Seen(Player player, DateTime now)
        {
            Dynasty d;
            if (!dynByMember.TryGetValue(player.Id.ToString(), out d)) return;
            Member m = Rules.Find(d, player.Id.ToString());
            if (m == null) return;
            m.LastSeen = now;
            if (!string.IsNullOrEmpty(player.Name) && m.Name != player.Name) m.Name = player.Name;
        }

        #endregion

        #region Tick

        private void Tick()
        {
            if (data == null) return;
            DateTime now = DateTime.UtcNow;
            DateTime previous = data.LastTickAt != default(DateTime) && data.LastTickAt < now ? data.LastTickAt : now;

            if (config.FreezeInactivityDuringDowntime && (now - previous).TotalMinutes > config.DowntimeGapMinutes)
            {
                TimeSpan gap = now - previous;
                Rules.ShiftForDowntime(data, gap, now);
                Puts("Server was down for " + Math.Round(gap.TotalHours, 1) + " h; inactivity clocks and claim deadlines paused for that time.");
            }
            data.LastTickAt = now;

            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) Seen(p, now);

            try { SyncCrown(now); }
            catch (Exception ex) { PrintError("Crown sync failed: " + ex.Message); }
            try { TickSuccession(now); }
            catch (Exception ex) { PrintError("Succession tick failed: " + ex.Message); }
            try { TickBlood(now); }
            catch (Exception ex) { PrintError("Blood claim tick failed: " + ex.Message); }
            try { TickOaths(previous, now); }
            catch (Exception ex) { PrintError("Oath tick failed: " + ex.Message); }

            PruneMemory(now);
            PruneCooldowns(now);
            // Last-seen times and the tick clock move every tick, so the file is written every tick (it is small).
            SaveData();
        }

        private void SyncCrownSafe()
        {
            try { if (SyncCrown(DateTime.UtcNow)) SaveData(); }
            catch (Exception ex) { PrintError("Crown sync failed: " + ex.Message); }
        }

        private bool TickSuccession(DateTime now)
        {
            bool changed = false;
            foreach (Dynasty d in new List<Dynasty>(data.Dynasties))
            {
                Member head = Rules.Find(d, d.HeadId);
                if (head == null) continue;
                if (!Rules.IsInactive(head, now, config.InactiveDaysForSuccession, IsOnline(head.Id))) continue;
                Member next = Rules.PickSuccessor(d, now, config.InactiveDaysForSuccession, IsOnline, true);
                if (next == null)
                {
                    if (!d.DormantNotified)
                    {
                        d.DormantNotified = true;
                        Puts(Msg("Dormant", null, d.Name));
                        changed = true;
                    }
                    continue;
                }
                PassLine(d, next, "the head was unseen for " + Math.Floor((now - head.LastSeen).TotalDays) + " days", now);
                changed = true;
            }
            return changed;
        }

        private bool PruneCooldowns(DateTime now)
        {
            bool changed = false;
            foreach (string k in new List<string>(data.LastFounded.Keys))
                if (data.LastFounded[k].AddHours(config.FoundCooldownHours) <= now) { data.LastFounded.Remove(k); changed = true; }
            foreach (string k in new List<string>(data.RejoinBlockedUntil.Keys))
                if (data.RejoinBlockedUntil[k] <= now) { data.RejoinBlockedUntil.Remove(k); changed = true; }
            return changed;
        }

        private void PruneMemory(DateTime now)
        {
            foreach (string k in new List<string>(offers.Keys)) if (offers[k].Expires < now) offers.Remove(k);
            foreach (string k in new List<string>(confirmUntil.Keys)) if (confirmUntil[k] < now) confirmUntil.Remove(k);
            foreach (string k in new List<string>(lastOfferBy.Keys))
                if ((now - lastOfferBy[k]).TotalSeconds > config.HeirOfferCooldownSeconds) lastOfferBy.Remove(k);
            foreach (string k in new List<string>(lastClaimCmd.Keys))
                if ((now - lastClaimCmd[k]).TotalSeconds > config.ClaimCommandCooldownSeconds) lastClaimCmd.Remove(k);
            foreach (string k in new List<string>(lastCommand.Keys))
                if ((now - lastCommand[k]).TotalSeconds > 60) lastCommand.Remove(k);
            chronicleTimes.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
        }

        #endregion

        #region Succession

        // Hands the line to `next`, with titles. Advises on house leadership, which RealmHouses alone can change.
        private void PassLine(Dynasty d, Member next, string why, DateTime now)
        {
            Member old = Rules.ApplySuccession(d, next, now, config.MinHeadshipHoursForGeneration, config.MaxGenerationsCounted);
            string oldName = old != null ? old.Name : "nobody";
            Rules.AddHistory(d, next.Name + " succeeded " + oldName + " (" + why + ")", now, config.MaxHistoryEntries);
            NotifyDynasty(d, null, "Succession", next.Name, oldName, d.Name, why);
            if (config.BroadcastEvents) Server.BroadcastMessage(Msg("Herald", null) + Msg("Succession", null, next.Name, oldName, d.Name, why));
            string titles = d.Titles.Count > 0 ? " The titles " + string.Join(", ", d.Titles.ToArray()) + " pass with the line." : "";
            Chronicle("succession", next.Name + " succeeds to the line of " + d.Name,
                next.Name + " succeeds " + oldName + " as head of the line of " + d.Name + " (" + why + ")." + titles,
                old != null ? new[] { next.Name, old.Name } : new[] { next.Name });

            // House leadership: advisory only (RealmHouses has no transfer call).
            if (old != null && RealmHouses != null)
            {
                string house = CallString(RealmHouses, "GetHouse", old.Id);
                string leader = house != null ? CallString(RealmHouses, "GetHouseLeader", house) : null;
                string nextHouse = CallString(RealmHouses, "GetHouse", next.Id);
                if (house != null && leader == old.Id && nextHouse != null && string.Equals(house, nextHouse, StringComparison.OrdinalIgnoreCase))
                {
                    NotifyId(old.Id, "HouseAdvice", house, next.Name);
                    NotifyId(next.Id, "HouseAdvice", house, next.Name);
                }
            }
        }

        #endregion

        #region Crown, reigns and blood claims

        private KingsScheme Crown()
        {
            return SocialAPI.Get<KingsScheme>();
        }

        // Reconciles the reign record with the game's KingsScheme. Returns true if data changed.
        private bool SyncCrown(DateTime now)
        {
            KingsScheme crown = Crown();
            if (crown == null) return false;
            string kingId = null, kingName = null;
            if (crown.HasKing())
            {
                ulong id = crown.GetKingID();
                if (id != 0) { kingId = id.ToString(); kingName = crown.GetKingName(); }
            }
            ReignState r = data.Reign;
            bool changed = false;

            if (kingId != r.KingId)
            {
                Dynasty newLine = kingId != null ? DynastyOf(kingId) : null;
                EndReign(r, newLine, now);
                data.Reign = r = new ReignState();
                if (kingId != null)
                {
                    r.KingId = kingId;
                    r.KingName = kingName ?? kingId;
                    r.Since = now;
                    r.LastAccrual = now;
                    if (newLine != null) { r.Dynasty = newLine.Name; r.DynastySince = now; }
                    Rules.MarkUsurped(data, newLine);
                    if (newLine != null) CheckRestoration(newLine, now, true);
                }
                changed = true;
            }
            else if (kingId != null)
            {
                // The king may join or leave a line mid-reign; only time inside the line counts for it.
                Dynasty line = DynastyOf(kingId);
                string lineName = line != null ? line.Name : null;
                if (!string.Equals(lineName, r.Dynasty, StringComparison.OrdinalIgnoreCase))
                {
                    r.Dynasty = lineName;
                    r.DynastySince = now;
                    r.LastAccrual = now;
                    r.Counted = false;
                    Rules.MarkUsurped(data, line);
                    // A sitting monarch who joins the line settles its blood right, but earns no restoration:
                    // otherwise a line could simply recruit whoever holds the throne.
                    if (line != null) CheckRestoration(line, now, false);
                    changed = true;
                }
                if (line != null)
                {
                    double maxStep = Math.Max(2.0 * config.TickSeconds / 3600.0, 0.05);
                    line.ReignHours = Rules.Accrue(line.ReignHours, r.LastAccrual, now, maxStep);
                    if (!r.Counted && (now - r.DynastySince).TotalMinutes >= config.MinReignMinutesToCount)
                    {
                        r.Counted = true;
                        // Passing an empty throne around inside one line is one reign, not many.
                        if (Rules.CountsAsNewReign(line, now, config.ReignRecountCooldownHours))
                        {
                            line.ReignsHeld++;
                            Rules.AddHistory(line, r.KingName + " reigned", now, config.MaxHistoryEntries);
                            NotifyDynasty(line, null, "ReignCounted", r.KingName, line.Name);
                        }
                        line.LastReignCountedAt = now;
                    }
                    changed = true;
                }
                r.LastAccrual = now;
            }
            return changed;
        }

        // The reign in `r` is over. If it was a counted reign of a line and the crown left that line, the line's heirs
        // get a blood right for BloodClaimWindowHours.
        private void EndReign(ReignState r, Dynasty newLine, DateTime now)
        {
            if (r == null || r.KingId == null || r.Dynasty == null || !r.Counted) return;
            Dynasty line = FindDynasty(r.Dynasty);
            if (line == null) return;
            if (newLine == line) return;                                    // the crown stays in the line
            line.Blood = new BloodRight
            {
                FallenKingId = r.KingId,
                FallenKingName = r.KingName,
                FallenAt = now,
                ExpiresAt = now.AddHours(config.BloodClaimWindowHours),
                Status = BloodOpen
            };
            Rules.AddHistory(line, "the crown fell from " + r.KingName, now, config.MaxHistoryEntries);
            NotifyDynasty(line, null, "BloodFallen", r.KingName, config.BloodClaimWindowHours);
        }

        // The line has the crown again while its blood right stands: a restoration if the claim was pressed, someone
        // outside the line reigned in between, and the line's restoration cooldown has passed.
        private void CheckRestoration(Dynasty line, DateTime now, bool allowBonus)
        {
            BloodRight b = line.Blood;
            if (b == null) return;
            line.Blood = null;
            if (!allowBonus || !Rules.EarnsRestoration(b, line, now, config.RestorationCooldownHours))
            {
                Rules.AddHistory(line, "holds the crown again", now, config.MaxHistoryEntries);
                return;
            }
            line.Restorations++;
            line.LastRestorationAt = now;
            Rules.AddHistory(line, "regained the crown by blood claim", now, config.MaxHistoryEntries);
            Broadcast("BloodRestored", line.Name);
            Chronicle("blood_restored", "The line of " + line.Name + " regains the crown",
                "The blood claim pressed by " + (b.ClaimantName ?? "an heir") + " after the fall of " + b.FallenKingName
                + " is made good: the line of " + line.Name + " holds the throne again.",
                b.ClaimantName != null ? new[] { b.ClaimantName, b.FallenKingName } : new[] { b.FallenKingName });
        }

        private bool TickBlood(DateTime now)
        {
            bool changed = false;
            string[] claims = null;
            bool claimsRead = false;
            foreach (Dynasty d in data.Dynasties)
            {
                BloodRight b = d.Blood;
                if (b == null) continue;

                if (b.Status == BloodOpen || b.Status == BloodAwaiting)
                {
                    if (now > b.ExpiresAt) { LapseBlood(d, now); changed = true; continue; }
                    if (b.Status == BloodAwaiting && CrownAndConsequences != null)
                    {
                        if (!claimsRead) { claims = OpenClaims(); claimsRead = true; }
                        string status; DateTime s, e;
                        if (Rules.ParseClaim(claims, b.House, out status, out s, out e))
                        {
                            LinkBlood(d, b, s, e);
                            changed = true;
                        }
                    }
                    continue;
                }

                if (b.Status == BloodLinked)
                {
                    // Restoration by house: the claimant house holds the crown inside or after its window.
                    if (CrownAndConsequences != null && b.House != null && b.WindowStart.HasValue && now >= b.WindowStart.Value)
                    {
                        string kingHouse = CallString(CrownAndConsequences, "GetKingHouse");
                        if (kingHouse != null && string.Equals(kingHouse, b.House, StringComparison.OrdinalIgnoreCase))
                        {
                            CheckRestoration(d, now, true);
                            changed = true;
                            continue;
                        }
                    }
                    if (b.WindowEnd.HasValue && now > b.WindowEnd.Value.AddMinutes(5))
                    {
                        LapseBlood(d, now);
                        changed = true;
                        continue;
                    }
                    // The claim was set aside (e.g. an admin cancelled it): the blood claim falls with it.
                    if (CrownAndConsequences != null && b.WindowEnd.HasValue && now < b.WindowEnd.Value)
                    {
                        if (!claimsRead) { claims = OpenClaims(); claimsRead = true; }
                        string status; DateTime s, e;
                        if (claims != null && !Rules.ParseClaim(claims, b.House, out status, out s, out e))
                        {
                            LapseBlood(d, now);
                            changed = true;
                        }
                    }
                }
            }
            return changed;
        }

        private void LinkBlood(Dynasty d, BloodRight b, DateTime start, DateTime end)
        {
            b.Status = BloodLinked;
            b.WindowStart = start;
            b.WindowEnd = end;
            NotifyDynasty(d, null, "ClaimLinked", b.House, start.ToString("ddd HH:mm"), end.ToString("ddd HH:mm"));
        }

        private void LapseBlood(Dynasty d, DateTime now)
        {
            d.Blood = null;
            Rules.AddHistory(d, "the blood claim lapsed", now, config.MaxHistoryEntries);
            NotifyDynasty(d, null, "BloodLapsed", d.Name);
        }

        private string[] OpenClaims()
        {
            if (CrownAndConsequences == null) return null;
            return CallSafe(CrownAndConsequences, "GetOpenClaims") as string[];
        }

        #endregion

        #region Oaths kept (RealmHouses)

        // The head's house is the line's seat. Time it spends sworn to a liege or holding vassals counts as oaths kept;
        // oaths and treaties it breaks while it is the seat count against the line.
        private bool TickOaths(DateTime previous, DateTime now)
        {
            if (RealmHouses == null) return false;
            bool changed = false;
            double maxStep = Math.Max(2.0 * config.TickSeconds / 3600.0, 0.05);
            foreach (Dynasty d in data.Dynasties)
            {
                string house = CallString(RealmHouses, "GetHouse", d.HeadId);
                int oaths = 0, treaties = 0;
                if (house != null)
                {
                    var rep = CallSafe(RealmHouses, "GetReputation", house) as Dictionary<string, object>;
                    if (rep != null)
                    {
                        oaths = ToInt(rep, "oathsBroken");
                        treaties = ToInt(rep, "treatiesBroken");
                    }
                }
                if (!string.Equals(house, d.SeatHouse, StringComparison.OrdinalIgnoreCase))
                {
                    d.SeatHouse = house;                               // new seat: start from its current record
                    d.SeatOathsSnapshot = oaths;
                    d.SeatTreatiesSnapshot = treaties;
                    changed = true;
                    continue;
                }
                if (house == null) continue;
                if (oaths > d.SeatOathsSnapshot)
                {
                    d.OathsBroken += oaths - d.SeatOathsSnapshot;
                    Rules.AddHistory(d, "House " + house + " broke an oath", now, config.MaxHistoryEntries);
                    changed = true;
                }
                if (treaties > d.SeatTreatiesSnapshot)
                {
                    d.TreatiesBroken += treaties - d.SeatTreatiesSnapshot;
                    changed = true;
                }
                d.SeatOathsSnapshot = oaths;                           // a pardon lowers the house count; follow it down
                d.SeatTreatiesSnapshot = treaties;

                string liege = CallString(RealmHouses, "GetLiege", house);
                var vassals = CallSafe(RealmHouses, "GetVassals", house) as List<string>;
                bool sworn = liege != null && RealOathPartner(liege);
                if (!sworn && vassals != null)
                    foreach (string v in vassals) if (RealOathPartner(v)) { sworn = true; break; }
                if (sworn)
                {
                    d.OathKeptHours = Rules.Accrue(d.OathKeptHours, previous, now, maxStep);
                    changed = true;
                }
            }
            return changed;
        }

        // A liege or vassal house counts for oath prestige only with OathPartnerMinMembers members; a house of unknown
        // size (no answer from RealmHouses) counts, so an older RealmHouses keeps the old behaviour.
        private bool RealOathPartner(string house)
        {
            if (house == null) return false;
            if (config.OathPartnerMinMembers <= 1) return true;
            var members = CallSafe(RealmHouses, "GetMembers", house) as List<string>;
            return members == null || members.Count >= config.OathPartnerMinMembers;
        }

        private static int ToInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        #endregion

        #region /dynasty

        [ChatCommand("dynasty")]
        private void CmdDynasty(Player player, string command, string[] args)
        {
            if (player == null || data == null) return;
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            bool readOnly = sub == "help" || sub == "info" || sub == "list" || sub == "top" || sub == "tree";
            if (!readOnly && Throttled(player)) return;
            switch (sub)
            {
                case "help": CmdHelp(player); break;
                case "found": CmdFound(player, args); break;
                case "info": CmdInfo(player, args); break;
                case "list":
                case "top": CmdList(player); break;
                case "tree": CmdTree(player, args); break;
                case "heir": CmdHeir(player, args); break;
                case "accept": CmdAccept(player); break;
                case "decline": CmdDecline(player); break;
                case "disown": CmdDisown(player, args); break;
                case "leave": CmdLeave(player, args); break;
                case "abdicate": CmdAbdicate(player, args); break;
                case "dissolve": CmdDissolve(player, args); break;
                case "claim": CmdClaimBlood(player); break;
                case "bestow": CmdBestow(player, args); break;
                case "admin": CmdAdmin(player, args); break;
                default: Error(player, "Unknown"); break;
            }
        }

        private void CmdHelp(Player player)
        {
            Reply(player, "Help1");
            Reply(player, "Help2");
            Reply(player, "Help3");
            Reply(player, "Help4", config.InactiveDaysForSuccession);
            if (IsAdmin(player)) Reply(player, "HelpAdmin");
        }

        private void CmdFound(Player player, string[] args)
        {
            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            Dynasty mine = DynastyOf(id);
            if (mine != null) { Error(player, "AlreadyInDynasty", mine.Name); return; }
            string name = JoinFrom(args, 1).Trim();
            if (!Rules.ValidName(name, config.NameMinLength, config.NameMaxLength))
            {
                Error(player, "BadName", config.NameMinLength, config.NameMaxLength);
                return;
            }
            if (FindDynasty(name) != null) { Error(player, "NameTaken", name); return; }
            if (data.Dynasties.Count >= config.MaxDynasties) { Error(player, "TooManyDynasties"); return; }
            if (!IsAdmin(player))
            {
                DateTime until;
                if (data.RejoinBlockedUntil.TryGetValue(id, out until) && until > now) { Error(player, "RejoinCooldown", Duration(until - now)); return; }
                DateTime last;
                if (data.LastFounded.TryGetValue(id, out last) && last.AddHours(config.FoundCooldownHours) > now)
                {
                    Error(player, "FoundCooldown", Duration(last.AddHours(config.FoundCooldownHours) - now));
                    return;
                }
            }

            var d = new Dynasty
            {
                Name = name,
                FounderId = id,
                FounderName = player.Name,
                Founded = now,
                HeadId = id,
                HeadSince = now
            };
            d.Members.Add(new Member { Id = id, Name = player.Name, Generation = 1, Joined = now, LastSeen = now });
            data.Dynasties.Add(d);
            dynByKey[Key(name)] = d;
            dynByMember[id] = d;
            data.LastFounded[id] = now;
            Rules.AddHistory(d, "founded by " + player.Name, now, config.MaxHistoryEntries);
            SaveData();
            Reply(player, "Founded", name);
            Broadcast("BroadcastFounded", player.Name, name);
            string house = RealmHouses != null ? CallString(RealmHouses, "GetHouse", id) : null;
            Chronicle("dynasty_founded", "The line of " + name + " is founded",
                player.Name + " founds the line of " + name + (house != null ? " within House " + house : "") + ".",
                new[] { player.Name });
        }

        private void CmdInfo(Player player, string[] args)
        {
            Dynasty d;
            if (args.Length > 1)
            {
                string text = JoinFrom(args, 1);
                d = FindDynasty(text);
                if (d == null)
                {
                    Player p = FindOnline(text);
                    if (p != null) d = DynastyOf(p.Id.ToString());
                }
                if (d == null) { Error(player, "NoSuchDynasty", text); return; }
            }
            else
            {
                d = DynastyOf(player.Id.ToString());
                if (d == null) { Error(player, "NotInDynasty"); return; }
            }
            DateTime now = DateTime.UtcNow;
            Member head = Rules.Find(d, d.HeadId);
            Reply(player, "InfoHeader", d.Name, d.Founded.ToString("yyyy-MM-dd"), d.FounderName ?? "?");
            Reply(player, "InfoHead", head != null ? head.Name : "?", head != null ? head.Generation : 0,
                d.HeadSince.ToString("yyyy-MM-dd HH:mm"), d.Members.Count, config.MaxMembersPerDynasty);
            ShowLine(player, d, now);
            Reply(player, "InfoTitles", d.Titles.Count > 0 ? string.Join(", ", d.Titles.ToArray()) : Msg("None", player));
            Reply(player, "InfoPrestige", Rules.Prestige(d, config), d.ReignsHeld, Math.Floor(d.ReignHours),
                Math.Floor(d.OathKeptHours / 24.0), d.OathsBroken, d.TreatiesBroken, d.Restorations, d.MaxGenerationCounted);
            if (d.SeatHouse != null) Reply(player, "InfoSeat", d.SeatHouse);
            if (d.Blood != null) Reply(player, "InfoBlood", d.Blood.FallenKingName, BloodText(player, d.Blood, now));
            if (d.History.Count > 0) Reply(player, "InfoHistory", d.History[d.History.Count - 1]);
        }

        private string BloodText(Player player, BloodRight b, DateTime now)
        {
            if (b.Status == BloodOpen) return Msg("BloodOpenText", player, Duration(b.ExpiresAt - now));
            if (b.Status == BloodAwaiting) return Msg("BloodAwaitingText", player, b.ClaimantName, b.House, b.ExpiresAt.ToString("ddd HH:mm"));
            return Msg("BloodLinkedText", player, b.ClaimantName, b.House,
                b.WindowStart.HasValue ? b.WindowStart.Value.ToString("ddd HH:mm") : "?",
                b.WindowEnd.HasValue ? b.WindowEnd.Value.ToString("ddd HH:mm") : "?");
        }

        private void ShowLine(Player player, Dynasty d, DateTime now)
        {
            Reply(player, "LineHeader", d.Name);
            if (d.Line.Count == 0) { Reply(player, "LineEmpty"); return; }
            for (int i = 0; i < d.Line.Count; i++)
            {
                Member m = Rules.Find(d, d.Line[i]);
                if (m == null) continue;
                bool online = IsOnline(m.Id);
                string note = Rules.IsInactive(m, now, config.InactiveDaysForSuccession, online)
                    ? Msg("Inactive", player, Math.Floor((now - m.LastSeen).TotalDays)) : "";
                Reply(player, "LineEntry", i + 1, m.Name, note);
            }
        }

        private void CmdList(Player player)
        {
            if (data.Dynasties.Count == 0) { Reply(player, "ListEmpty"); return; }
            var sorted = new List<Dynasty>(data.Dynasties);
            var scores = new Dictionary<Dynasty, int>();
            foreach (Dynasty d in sorted) scores[d] = Rules.Prestige(d, config);
            sorted.Sort(delegate(Dynasty a, Dynasty b)
            {
                int c = scores[b].CompareTo(scores[a]);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            Reply(player, "ListHeader", sorted.Count);
            string reigning = data.Reign.Dynasty;
            for (int i = 0; i < sorted.Count && i < config.MaxListLines; i++)
            {
                Dynasty d = sorted[i];
                Member head = Rules.Find(d, d.HeadId);
                string mark = reigning != null && string.Equals(reigning, d.Name, StringComparison.OrdinalIgnoreCase) ? " - holds the crown" : "";
                Reply(player, "ListLine", i + 1, d.Name, scores[d], head != null ? head.Name : "?", d.Members.Count, mark);
            }
        }

        private void CmdTree(Player player, string[] args)
        {
            Dynasty d = args.Length > 1 ? FindDynasty(JoinFrom(args, 1)) : DynastyOf(player.Id.ToString());
            if (d == null)
            {
                if (args.Length > 1) Error(player, "NoSuchDynasty", JoinFrom(args, 1));
                else Error(player, "NotInDynasty");
                return;
            }
            Reply(player, "TreeHeader", d.Name);
            foreach (string line in Rules.Tree(d, config.MaxTreeLines)) player.SendMessage("  " + line);
        }

        private void CmdHeir(Player player, string[] args)
        {
            string op = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            Dynasty d = DynastyOf(player.Id.ToString());
            if (d == null) { Error(player, "NotInDynasty"); return; }
            if (op == "list") { ShowLine(player, d, DateTime.UtcNow); return; }
            if (d.HeadId != player.Id.ToString()) { Error(player, "NotHead"); return; }

            if (op == "name" || op == "add") { HeirName(player, d, JoinFrom(args, 2)); return; }
            if (op == "remove")
            {
                Member m = Rules.FindByName(d, JoinFrom(args, 2));
                if (m == null) { Error(player, "NoSuchMember", JoinFrom(args, 2)); return; }
                if (!d.Line.Remove(m.Id)) { Error(player, "NotHeir", m.Name); return; }
                Rules.AddHistory(d, m.Name + " removed from the succession", DateTime.UtcNow, config.MaxHistoryEntries);
                SaveData();
                NotifyDynasty(d, null, "HeirRemoved", m.Name);
                return;
            }
            if (op == "order")
            {
                int pos;
                if (args.Length < 4 || !int.TryParse(args[args.Length - 1], out pos) || pos < 1) { Error(player, "HeirOrderUsage"); return; }
                Member m = Rules.FindByName(d, JoinRange(args, 2, args.Length - 1));
                if (m == null) { Error(player, "NoSuchMember", JoinRange(args, 2, args.Length - 1)); return; }
                if (!Rules.MoveInLine(d, m.Id, pos)) { Error(player, "NotHeir", m.Name); return; }
                SaveData();
                NotifyDynasty(d, null, "HeirMoved", m.Name, d.Line.IndexOf(m.Id) + 1);
                return;
            }
            Error(player, "HeirUsage");
        }

        private void HeirName(Player player, Dynasty d, string text)
        {
            if (string.IsNullOrEmpty(text)) { Error(player, "HeirUsage"); return; }
            DateTime now = DateTime.UtcNow;

            // Kin already in the line's family go straight into the succession (they accepted the family before).
            Member kin = Rules.FindByName(d, text);
            if (kin != null)
            {
                if (kin.Id == d.HeadId) { Error(player, "HeirSelf"); return; }
                if (d.Line.Contains(kin.Id)) { Error(player, "HeirAlready", kin.Name); return; }
                if (d.Line.Count >= config.MaxHeirsInLine) { Error(player, "LineFull", config.MaxHeirsInLine); return; }
                d.Line.Add(kin.Id);
                Rules.AddHistory(d, kin.Name + " restored to the succession", now, config.MaxHistoryEntries);
                SaveData();
                NotifyDynasty(d, null, "KinRestored", kin.Name, d.Line.Count);
                return;
            }

            Player target = FindOnline(text);
            if (target == null) { Error(player, "NoSuchPlayer", text); return; }
            string tid = target.Id.ToString();
            if (tid == player.Id.ToString()) { Error(player, "HeirSelf"); return; }
            Dynasty other = DynastyOf(tid);
            if (other != null) { Error(player, "HeirInOther", target.Name, other.Name); return; }
            if (d.Line.Count >= config.MaxHeirsInLine) { Error(player, "LineFull", config.MaxHeirsInLine); return; }
            if (d.Members.Count >= config.MaxMembersPerDynasty) { Error(player, "DynastyFull", config.MaxMembersPerDynasty); return; }

            string key = Key(d.Name);
            int pending = 0;
            foreach (Offer o in offers.Values) if (o.DynastyKey == key && o.Expires > now) pending++;
            if (pending >= config.MaxPendingOffersPerDynasty) { Error(player, "TooManyOffers", config.MaxPendingOffersPerDynasty); return; }
            DateTime lastOffer;
            if (lastOfferBy.TryGetValue(player.Id.ToString(), out lastOffer) && (now - lastOffer).TotalSeconds < config.HeirOfferCooldownSeconds)
            {
                Error(player, "OfferCooldown", (int)Math.Ceiling(config.HeirOfferCooldownSeconds - (now - lastOffer).TotalSeconds));
                return;
            }
            lastOfferBy[player.Id.ToString()] = now;
            offers[tid] = new Offer { DynastyKey = key, FromId = player.Id.ToString(), Expires = now.AddSeconds(config.OfferExpireSeconds) };
            Reply(player, "OfferSent", target.Name, d.Name, config.OfferExpireSeconds);
            Reply(target, "OfferReceived", player.Name, d.Name, config.OfferExpireSeconds);
        }

        private void CmdAccept(Player player)
        {
            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            Offer o;
            if (!offers.TryGetValue(id, out o) || o.Expires < now) { offers.Remove(id); Error(player, "NoOffer"); return; }
            Dynasty d;
            if (!dynByKey.TryGetValue(o.DynastyKey, out d)) { offers.Remove(id); Error(player, "NoOffer"); return; }
            // Every rule is checked again: things may have changed since the offer.
            Dynasty mine = DynastyOf(id);
            if (mine != null) { offers.Remove(id); Error(player, "AlreadyInDynasty", mine.Name); return; }
            if (d.HeadId != o.FromId) { offers.Remove(id); Error(player, "NoOffer"); return; }
            DateTime until;
            if (data.RejoinBlockedUntil.TryGetValue(id, out until) && until > now) { Error(player, "RejoinCooldown", Duration(until - now)); return; }
            if (d.Line.Count >= config.MaxHeirsInLine) { offers.Remove(id); Error(player, "LineFull", config.MaxHeirsInLine); return; }
            if (d.Members.Count >= config.MaxMembersPerDynasty) { offers.Remove(id); Error(player, "DynastyFull", config.MaxMembersPerDynasty); return; }
            offers.Remove(id);

            Member head = Rules.Find(d, d.HeadId);
            var m = new Member
            {
                Id = id,
                Name = player.Name,
                ParentId = d.HeadId,
                Generation = head != null ? head.Generation + 1 : 1,
                Joined = now,
                LastSeen = now
            };
            d.Members.Add(m);
            d.Line.Add(id);
            dynByMember[id] = d;
            Rules.AddHistory(d, player.Name + " named heir by " + (head != null ? head.Name : "the head"), now, config.MaxHistoryEntries);
            SaveData();
            NotifyDynasty(d, null, "HeirAccepted", player.Name, d.Line.Count, d.Name);
            if (config.ChronicleHeirNamed)
            {
                Broadcast("BroadcastHeir", player.Name, d.Name);
                Chronicle("heir_named", player.Name + " is named heir of the line of " + d.Name,
                    (head != null ? head.Name : "The head") + " names " + player.Name + " heir " + d.Line.Count + " of the line of " + d.Name + ".",
                    head != null ? new[] { player.Name, head.Name } : new[] { player.Name });
            }
        }

        private void CmdDecline(Player player)
        {
            string id = player.Id.ToString();
            Offer o;
            if (!offers.TryGetValue(id, out o)) { Error(player, "NoOffer"); return; }
            offers.Remove(id);
            Reply(player, "YouDeclined");
            NotifyId(o.FromId, "OfferDeclined", player.Name);
        }

        private void CmdDisown(Player player, string[] args)
        {
            Dynasty d = DynastyOf(player.Id.ToString());
            if (d == null) { Error(player, "NotInDynasty"); return; }
            if (d.HeadId != player.Id.ToString()) { Error(player, "NotHead"); return; }
            DateTime now = DateTime.UtcNow;
            if (!IsAdmin(player) && d.LastDisownAt.HasValue && d.LastDisownAt.Value.AddMinutes(config.DisownCooldownMinutes) > now)
            {
                Error(player, "DisownCooldown", (int)Math.Ceiling((d.LastDisownAt.Value.AddMinutes(config.DisownCooldownMinutes) - now).TotalMinutes));
                return;
            }
            Member m = Rules.FindByName(d, JoinFrom(args, 1));
            if (m == null) { Error(player, "NoSuchMember", JoinFrom(args, 1)); return; }
            if (m.Id == d.HeadId) { Error(player, "DisownSelf"); return; }
            Rules.RemoveMember(d, m.Id);
            dynByMember.Remove(m.Id);
            data.RejoinBlockedUntil[m.Id] = now.AddHours(config.RejoinCooldownHours);
            d.LastDisownAt = now;
            Rules.AddHistory(d, m.Name + " disowned", now, config.MaxHistoryEntries);
            SaveData();
            NotifyDynasty(d, null, "Disowned", m.Name, d.Name);
            NotifyId(m.Id, "YouWereDisowned", d.Name);
        }

        private void CmdLeave(Player player, string[] args)
        {
            string id = player.Id.ToString();
            Dynasty d = DynastyOf(id);
            if (d == null) { Error(player, "NotInDynasty"); return; }
            if (d.HeadId == id) { Error(player, "HeadCannotLeave"); return; }
            if (!Confirmed(player, "leave", args)) { Reply(player, "LeaveConfirm", d.Name, Duration(TimeSpan.FromHours(config.RejoinCooldownHours))); return; }
            DateTime now = DateTime.UtcNow;
            Rules.RemoveMember(d, id);
            dynByMember.Remove(id);
            data.RejoinBlockedUntil[id] = now.AddHours(config.RejoinCooldownHours);
            Rules.AddHistory(d, player.Name + " left", now, config.MaxHistoryEntries);
            SaveData();
            Reply(player, "Left", d.Name);
            NotifyDynasty(d, null, "MemberLeft", player.Name, d.Name);
        }

        private void CmdAbdicate(Player player, string[] args)
        {
            Dynasty d = DynastyOf(player.Id.ToString());
            if (d == null) { Error(player, "NotInDynasty"); return; }
            if (d.HeadId != player.Id.ToString()) { Error(player, "NotHead"); return; }
            DateTime now = DateTime.UtcNow;
            if (!IsAdmin(player) && d.LastSuccessionAt.HasValue && d.LastSuccessionAt.Value.AddHours(config.AbdicateCooldownHours) > now)
            {
                Error(player, "AbdicateCooldown", Duration(d.LastSuccessionAt.Value.AddHours(config.AbdicateCooldownHours) - now));
                return;
            }
            Member next = Rules.PickSuccessor(d, now, config.InactiveDaysForSuccession, IsOnline, true);
            if (next == null) { Error(player, "AbdicateNoHeir"); return; }
            if (!Confirmed(player, "abdicate", args)) { Reply(player, "AbdicateConfirm", d.Name, next.Name); return; }
            PassLine(d, next, "abdication", now);
            SaveData();
        }

        private void CmdDissolve(Player player, string[] args)
        {
            Dynasty d = DynastyOf(player.Id.ToString());
            if (d == null) { Error(player, "NotInDynasty"); return; }
            if (d.HeadId != player.Id.ToString()) { Error(player, "NotHead"); return; }
            if (d.Members.Count > 1) { Error(player, "DissolveNotAlone"); return; }
            if (!Confirmed(player, "dissolve", args)) { Reply(player, "DissolveConfirm", d.Name); return; }
            DissolveDynasty(d);
            data.RejoinBlockedUntil[player.Id.ToString()] = DateTime.UtcNow.AddHours(config.RejoinCooldownHours);
            SaveData();
        }

        private void DissolveDynasty(Dynasty d)
        {
            data.Dynasties.Remove(d);
            dynByKey.Remove(Key(d.Name));
            foreach (Member m in d.Members)
            {
                Dynasty indexed;
                if (dynByMember.TryGetValue(m.Id, out indexed) && indexed == d) dynByMember.Remove(m.Id);
            }
            string key = Key(d.Name);
            foreach (string k in new List<string>(offers.Keys)) if (offers[k].DynastyKey == key) offers.Remove(k);
            if (data.Reign.Dynasty != null && Key(data.Reign.Dynasty) == key) data.Reign.Dynasty = null;
            NotifyDynasty(d, null, "Dissolved", d.Name);
            Puts(Msg("Dissolved", null, d.Name));
        }

        // Blood claim: an heir of a line whose monarch lost the throne presses the line's claim to the crown.
        private void CmdClaimBlood(Player player)
        {
            string id = player.Id.ToString();
            Dynasty d = DynastyOf(id);
            if (d == null) { Error(player, "NotInDynasty"); return; }
            DateTime now = DateTime.UtcNow;
            BloodRight b = d.Blood;
            if (b == null || (b.Status == BloodOpen && now > b.ExpiresAt)) { Error(player, "ClaimNoRight"); return; }
            if (b.Status != BloodOpen) { Error(player, "ClaimAlready", b.ClaimantName); return; }
            if (id != d.HeadId && !d.Line.Contains(id)) { Error(player, "ClaimNotHeir"); return; }
            if (id == b.FallenKingId && !config.AllowFallenMonarchToClaim) { Error(player, "ClaimFallen"); return; }
            if (data.Reign.Dynasty != null && string.Equals(data.Reign.Dynasty, d.Name, StringComparison.OrdinalIgnoreCase))
            {
                Error(player, "ClaimHolding");
                return;
            }
            DateTime last;
            if (lastClaimCmd.TryGetValue(id, out last) && (now - last).TotalSeconds < config.ClaimCommandCooldownSeconds)
            {
                Error(player, "ClaimCooldown", (int)Math.Ceiling(config.ClaimCommandCooldownSeconds - (now - last).TotalSeconds));
                return;
            }
            lastClaimCmd[id] = now;

            string against = data.Reign.KingName ?? "the empty throne";
            if (CrownAndConsequences == null)
            {
                // Honour mode: no rebellion rules to ride on. Restoration still counts if the line retakes the throne in time.
                b.Status = BloodAwaiting;
                b.ClaimantId = id;
                b.ClaimantName = player.Name;
                Reply(player, "ClaimNoCrownPlugin", b.ExpiresAt.ToString("ddd HH:mm"));
                AnnounceClaim(d, player, against, now);
                SaveData();
                return;
            }

            string house = RealmHouses != null ? CallString(RealmHouses, "GetHouse", id) : null;
            if (house == null) { Error(player, "ClaimNoHouse"); return; }

            string status; DateTime s, e;
            bool found = Rules.ParseClaim(OpenClaims(), house, out status, out s, out e);
            if (!found && config.DelegateToCrownClaim)
            {
                // CrownAndConsequences applies its own rules (house head only, cooldown, notice, next window) and
                // answers the player directly. Success is judged only by its GetOpenClaims afterwards.
                CallSafe(CrownAndConsequences, "CmdClaim", player, "claim", new string[] { "declare" });
                found = Rules.ParseClaim(OpenClaims(), house, out status, out s, out e);
            }
            b.ClaimantId = id;
            b.ClaimantName = player.Name;
            b.House = house;
            if (found)
            {
                LinkBlood(d, b, s, e);
            }
            else
            {
                b.Status = BloodAwaiting;
                Reply(player, "ClaimAwaiting", house, b.ExpiresAt.ToString("ddd HH:mm"));
            }
            AnnounceClaim(d, player, against, now);
            SaveData();
        }

        private void AnnounceClaim(Dynasty d, Player player, string against, DateTime now)
        {
            BloodRight b = d.Blood;
            Rules.AddHistory(d, player.Name + " pressed the blood claim", now, config.MaxHistoryEntries);
            Broadcast("BroadcastClaim", player.Name, d.Name, against);
            Chronicle("blood_claim", player.Name + " presses the blood claim of the line of " + d.Name,
                player.Name + ", heir of the line of " + d.Name + ", claims the crown by blood after the fall of " + b.FallenKingName
                + (b.House != null ? ", through House " + b.House : "") + ".",
                new[] { player.Name, b.FallenKingName });
        }

        // The reigning monarch bestows a title on another line. Titles pass with the line's headship.
        private void CmdBestow(Player player, string[] args)
        {
            KingsScheme crown = Crown();
            if (crown == null || !crown.IsKing(player)) { Error(player, "BestowNotKing"); return; }
            if (args.Length < 3) { Error(player, "BestowUsage"); return; }
            SyncCrownSafe();                                            // so the per-reign count belongs to this king
            Dynasty d = FindDynasty(args[1]);
            if (d == null) { Error(player, "NoSuchDynasty", args[1]); return; }
            Dynasty own = DynastyOf(player.Id.ToString());
            if (own == d) { Error(player, "BestowOwn"); return; }
            string title = JoinFrom(args, 2).Trim();
            if (!Rules.ValidName(title, 3, config.TitleMaxLength)) { Error(player, "BadTitle", config.TitleMaxLength); return; }
            DateTime now = DateTime.UtcNow;
            if (data.LastBestowAt.HasValue && data.LastBestowAt.Value.AddMinutes(config.BestowCooldownMinutes) > now)
            {
                Error(player, "BestowCooldown", (int)Math.Ceiling((data.LastBestowAt.Value.AddMinutes(config.BestowCooldownMinutes) - now).TotalMinutes));
                return;
            }
            if (data.Reign.KingId == player.Id.ToString() && data.Reign.Bestows >= config.MaxBestowsPerReign)
            {
                Error(player, "BestowLimit", config.MaxBestowsPerReign);
                return;
            }
            if (ContainsTitle(d, title)) { Error(player, "BestowHeld", d.Name); return; }
            Dynasty holder = TitleHolder(title);
            if (holder != null && !config.BestowTransfersTitles) { Error(player, "BestowTaken", holder.Name); return; }
            if (d.Titles.Count >= config.MaxTitlesPerDynasty) { Error(player, "BestowFull", d.Name, config.MaxTitlesPerDynasty); return; }

            if (holder != null)
            {
                RemoveTitle(holder, title);
                Rules.AddHistory(holder, "lost the title " + title + " to the line of " + d.Name, now, config.MaxHistoryEntries);
            }
            d.Titles.Add(title);
            data.LastBestowAt = now;
            if (data.Reign.KingId == player.Id.ToString()) data.Reign.Bestows++;
            Rules.AddHistory(d, "granted the title " + title + " by " + player.Name, now, config.MaxHistoryEntries);
            SaveData();
            string from = holder != null ? Msg("BestowedFrom", null, holder.Name) : "";
            Broadcast("Bestowed", title, d.Name, from);
            Member head = Rules.Find(d, d.HeadId);
            Chronicle("title_bestowed", "The crown names the line of " + d.Name + " " + title,
                player.Name + " bestows the title " + title + " on the line of " + d.Name
                + (holder != null ? ", taking it from the line of " + holder.Name : "") + ".",
                head != null ? new[] { player.Name, head.Name } : new[] { player.Name });
        }

        private void CmdAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
            string op = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            DateTime now = DateTime.UtcNow;
            switch (op)
            {
                case "pass":
                {
                    Dynasty d = args.Length > 2 ? FindDynasty(args[2]) : null;
                    if (d == null) { Error(player, "NoSuchDynasty", args.Length > 2 ? args[2] : ""); return; }
                    Member next = args.Length > 3 ? Rules.FindByName(d, JoinFrom(args, 3))
                        : Rules.PickSuccessor(d, now, config.InactiveDaysForSuccession, IsOnline, false);
                    if (next == null || next.Id == d.HeadId) { Error(player, "AdminNoTarget"); return; }
                    PassLine(d, next, "by the realm's stewards", now);
                    SaveData();
                    Reply(player, "AdminPassed", d.Name, next.Name);
                    return;
                }
                case "dissolve":
                {
                    Dynasty d = args.Length > 2 ? FindDynasty(args[2]) : null;
                    if (d == null) { Error(player, "NoSuchDynasty", args.Length > 2 ? args[2] : ""); return; }
                    DissolveDynasty(d);
                    SaveData();
                    Reply(player, "Dissolved", d.Name);
                    return;
                }
                case "title":
                {
                    string verb = args.Length > 2 ? args[2].ToLowerInvariant() : "";
                    Dynasty d = args.Length > 3 ? FindDynasty(args[3]) : null;
                    string title = JoinFrom(args, 4).Trim();
                    if (d == null || (verb != "add" && verb != "remove") || title.Length == 0) { Error(player, "AdminUsage"); return; }
                    if (verb == "add")
                    {
                        if (!Rules.ValidName(title, 3, config.TitleMaxLength)) { Error(player, "BadTitle", config.TitleMaxLength); return; }
                        if (ContainsTitle(d, title)) { Error(player, "BestowHeld", d.Name); return; }
                        Dynasty holder = TitleHolder(title);
                        if (holder != null) { Error(player, "BestowTaken", holder.Name); return; }
                        if (d.Titles.Count >= config.MaxTitlesPerDynasty) { Error(player, "BestowFull", d.Name, config.MaxTitlesPerDynasty); return; }
                        d.Titles.Add(title);
                        Rules.AddHistory(d, "given the title " + title, now, config.MaxHistoryEntries);
                        Reply(player, "TitleAdded", title, d.Name);
                    }
                    else
                    {
                        if (!RemoveTitle(d, title)) { Error(player, "TitleMissing", d.Name, title); return; }
                        Reply(player, "TitleRemoved", title, d.Name);
                    }
                    SaveData();
                    return;
                }
                case "prestige":
                {
                    Dynasty d = args.Length > 2 ? FindDynasty(args[2]) : null;
                    int delta;
                    if (d == null || args.Length < 4 || !int.TryParse(args[3], out delta)) { Error(player, "AdminUsage"); return; }
                    long next = (long)d.PrestigeAdjust + delta;
                    d.PrestigeAdjust = (int)Math.Max(-config.AdminAdjustLimit, Math.Min(config.AdminAdjustLimit, next));
                    SaveData();
                    Reply(player, "PrestigeAdjusted", d.Name, d.PrestigeAdjust, Rules.Prestige(d, config));
                    return;
                }
                case "check":
                    Tick();
                    Reply(player, "Checked");
                    return;
                case "save":
                    SaveData();
                    Reply(player, "Saved");
                    return;
                default:
                    Reply(player, "AdminUsage");
                    return;
            }
        }

        #endregion

        #region Helpers

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private bool Throttled(Player player)
        {
            if (config.CommandThrottleSeconds <= 0f) return false;
            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (lastCommand.TryGetValue(id, out last) && (now - last).TotalSeconds < config.CommandThrottleSeconds)
            {
                Error(player, "Throttled");
                return true;
            }
            lastCommand[id] = now;
            return false;
        }

        // Two-step confirmation: "<cmd>" arms it for 60 s, "<cmd> confirm" executes.
        private bool Confirmed(Player player, string action, string[] args)
        {
            string k = player.Id + "|" + action;
            DateTime until;
            bool armed = confirmUntil.TryGetValue(k, out until) && until > DateTime.UtcNow;
            if (args.Length > 1 && args[1].ToLowerInvariant() == "confirm" && armed)
            {
                confirmUntil.Remove(k);
                return true;
            }
            confirmUntil[k] = DateTime.UtcNow.AddSeconds(60);
            return false;
        }

        private static string Key(string name)
        {
            return name == null ? "" : name.Trim().ToLowerInvariant();
        }

        private Dynasty FindDynasty(string name)
        {
            Dynasty d;
            return name != null && dynByKey.TryGetValue(Key(name), out d) ? d : null;
        }

        private Dynasty DynastyOf(string playerId)
        {
            Dynasty d;
            return playerId != null && dynByMember.TryGetValue(playerId, out d) ? d : null;
        }

        private static string NameOf(Dynasty d, string id)
        {
            Member m = Rules.Find(d, id);
            return m != null ? m.Name : id;
        }

        private static bool ContainsTitle(Dynasty d, string title)
        {
            foreach (string t in d.Titles) if (string.Equals(t, title, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private Dynasty TitleHolder(string title)
        {
            foreach (Dynasty d in data.Dynasties) if (ContainsTitle(d, title)) return d;
            return null;
        }

        private static bool RemoveTitle(Dynasty d, string title)
        {
            return d.Titles.RemoveAll(delegate(string t) { return string.Equals(t, title, StringComparison.OrdinalIgnoreCase); }) > 0;
        }

        private bool IsOnline(string id)
        {
            return OnlineById(id) != null;
        }

        private static Player OnlineById(string id)
        {
            ulong uid;
            if (id == null || !ulong.TryParse(id, out uid)) return null;
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && p.Id == uid) return p;
            return null;
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

        private object CallSafe(Plugin plugin, string method, params object[] args)
        {
            if (plugin == null) return null;
            try { return plugin.Call(method, args); }
            catch (Exception ex)
            {
                PrintWarning("Call " + plugin.Name + "." + method + " failed: " + ex.Message);
                return null;
            }
        }

        private string CallString(Plugin plugin, string method, params object[] args)
        {
            string s = CallSafe(plugin, method, args) as string;
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // Rate-limited; new event types are dropped by RealmChronicle (Log returns 0) until registered there.
        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            LogToFile("events", DateTime.UtcNow.ToString("o") + " [" + type + "] " + title + " - " + detail, this);
            if (!config.ChronicleEnabled || RealmChronicle == null) return;
            DateTime now = DateTime.UtcNow;
            chronicleTimes.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
            if (chronicleTimes.Count >= config.ChronicleMaxPerHour) return;
            chronicleTimes.Add(now);
            object id = CallSafe(RealmChronicle, "Log", type, title, detail, actors ?? new string[0]);
            if ((id == null || (id is int && (int)id == 0)) && !warnedTypes.ContainsKey(type))
            {
                warnedTypes[type] = true;
                PrintWarning("RealmChronicle did not accept event type '" + type + "'. Update RealmChronicle.cs (its KnownTypes list); until then it is only in oxide/logs.");
            }
        }

        private static string JoinFrom(string[] args, int start)
        {
            return start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start);
        }

        private static string JoinRange(string[] args, int start, int end)
        {
            return start >= end ? "" : string.Join(" ", args, start, end - start);
        }

        private static string Duration(TimeSpan span)
        {
            if (span.TotalHours >= 48) return Math.Ceiling(span.TotalDays) + " days";
            if (span.TotalMinutes >= 120) return Math.Ceiling(span.TotalHours) + " h";
            return Math.Max(1, Math.Ceiling(span.TotalMinutes)) + " min";
        }

        #endregion

        #region API (plugin.Call)

        // All non-public on purpose (see header). Ids are SteamID64 strings.
        private string GetDynasty(string playerId)
        {
            Dynasty d = data != null ? DynastyOf(playerId) : null;
            return d != null ? d.Name : null;
        }

        private string GetDynastyHead(string dynasty)
        {
            Dynasty d = data != null ? FindDynasty(dynasty) : null;
            return d != null ? d.HeadId : null;
        }

        private List<string> GetDynastyHeirs(string dynasty)
        {
            Dynasty d = data != null ? FindDynasty(dynasty) : null;
            return d != null ? new List<string>(d.Line) : null;
        }

        private int GetDynastyPrestige(string dynasty)
        {
            Dynasty d = data != null ? FindDynasty(dynasty) : null;
            return d != null ? Rules.Prestige(d, config) : 0;
        }

        private List<string> GetDynastyTitles(string dynasty)
        {
            Dynasty d = data != null ? FindDynasty(dynasty) : null;
            return d != null ? new List<string>(d.Titles) : null;
        }

        // [{name, head, members, heirs, prestige, titles, reigns}] for overlays or other plugins. Names only, no ids.
        private List<Dictionary<string, object>> GetDynastySummaries()
        {
            var list = new List<Dictionary<string, object>>();
            if (data == null) return list;
            foreach (Dynasty d in data.Dynasties)
            {
                Member head = Rules.Find(d, d.HeadId);
                list.Add(new Dictionary<string, object>
                {
                    { "name", d.Name },
                    { "head", head != null ? head.Name : null },
                    { "members", d.Members.Count },
                    { "heirs", d.Line.Count },
                    { "prestige", Rules.Prestige(d, config) },
                    { "titles", d.Titles.ToArray() },
                    { "reigns", d.ReignsHeld }
                });
            }
            return list;
        }

        #endregion
    }
}
