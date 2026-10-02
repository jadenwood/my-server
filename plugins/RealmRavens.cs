// RealmRavens: sealed letters, spymasters, sworn spies and rumours for the realm of Ostreval.
//
//   /raven <house|player> <message>   a sealed letter that flies for a while before it lands (offline players find it
//                                     on login); /raven anon ... sends it unsigned
//   spymasters                        a house leader appoints one; the spymaster watches one other house and has a chance
//                                     (with a cooldown and a daily cap) to read ravens to or from it
//   sworn spies                       a house leader swears up to N members; a spy (or the spymaster) can learn another
//                                     house's treaties, fealty and raven traffic once per cooldown, and may be caught
//   /rumour <text>                    an anonymous rumour; it waits in a moderation queue and an admin approves it into
//                                     the chronicle (event type "rumour", registered in RealmChronicle KnownTypes)
//
// Anti-harassment: per-player cooldown, hourly/daily/anonymous/same-target caps, in-flight cap, a cap on unread letters
// from one sender per recipient, block lists (/raven block <player|house|#letter|anon>; blocking by letter number works
// on anonymous letters without revealing the sender), silent drop of blocked letters, admin mute/purge, a player report
// queue, and an admin audit log kept at most 7 days (AuditRetentionDays is clamped to 1..7).
//
// Data: oxide/data/RealmRavens.json via DataFileSystem. If the file exists but cannot be parsed (or parses to null) the
// plugin refuses to run its commands and NEVER writes the file, so a damaged file is not replaced by an empty one.
//
// House facts come from RealmHouses through its non-public plugin.Call API (GetHouse, GetMembers, GetHouseLeader,
// GetHouseSummaries, GetLiege, GetVassals, HasTreaty). Without RealmHouses, player-to-player letters, blocks and rumours
// still work; house letters, spymasters and spies say so and refuse.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// Player-supplied text is only ever sent through the single-string chat overloads (docs/oxide-rok-api.md 4.2).

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.Common;                       // PlayerExtensions: SendMessage, SendError                 [ASM]
using CodeHatch.Engine.Networking;            // Player, Server                                           [ASM]
using Oxide.Core;                             // Interface.Oxide.DataFileSystem                           [SRC]
using Oxide.Core.Plugins;                     // Plugin (for [PluginReference])                           [SRC]

namespace Oxide.Plugins
{
    [Info("RealmRavens", "Realm", "0.1.0")]
    [Description("Sealed ravens with travel time, spymasters who intercept them, sworn spies and moderated rumours")]
    public class RealmRavens : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmChronicle;

        internal const string PermAdmin = "realmravens.admin";
        internal const string DataName = "RealmRavens";
        private const float TickSeconds = 5f;
        private const int MaintenanceEveryTicks = 60;      // 5 minutes

        internal const string KindPlayer = "player";
        internal const string KindHouse = "house";

        internal const string RPending = "pending";
        internal const string RApproved = "approved";
        internal const string RRejected = "rejected";
        internal const string RExpired = "expired";

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private bool initialized;
        private int tickCount;
        private readonly System.Random rng = new System.Random();
        private readonly Dictionary<string, SpyOffer> spyOffers = new Dictionary<string, SpyOffer>();   // player id -> offer

        #region Config and data shapes

        internal class PluginConfig
        {
            // Letters
            public int MaxLetterLength = 300;
            public int MinLetterLength = 2;
            public int TravelSecondsMin = 60;
            public int TravelSecondsMax = 180;
            public int AnonymousExtraSeconds = 60;
            public bool AllowAnonymous = true;
            public bool HouseLettersToAllMembers = true;      // false = only the house leader receives house letters
            public int MaxHouseRecipients = 30;
            // Rate limits and anti-harassment
            public int SendCooldownSeconds = 30;
            public int MaxLettersPerHour = 6;
            public int MaxLettersPerDay = 20;
            public int MaxLettersToSameTargetPerDay = 3;
            public int MaxAnonymousPerDay = 2;
            public int MaxInFlightPerSender = 5;
            public int MaxUnreadFromSameSender = 3;
            public int MaxInboxSize = 30;
            public int InboxRetentionDays = 7;
            public int MaxBlocks = 50;
            public int NewPlayerGraceMinutes = 30;            // first-seen players wait this long before anon letters/rumours
            public int InboxListLines = 8;
            // Interception
            public bool InterceptionEnabled = true;
            public double InterceptChance = 0.2;
            public int InterceptCooldownMinutes = 30;
            public int MaxInterceptsPerDay = 6;
            public bool InterceptedLettersStillDelivered = true;   // false = an intercepted raven never arrives
            public double TamperNoticeChance = 0.1;
            public bool RevealAnonHouseOnIntercept = true;
            public int SpymasterChangeCooldownHours = 24;
            public int WatchChangeCooldownMinutes = 60;
            // Spies
            public bool SpiesEnabled = true;
            public int MaxSpiesPerHouse = 2;
            public int SpyCooldownHours = 12;
            public int SpySameTargetCooldownHours = 24;
            public double SpyCaughtChance = 0.15;
            public bool RevealSpyNameWhenCaught = false;
            public int SpyOfferExpireSeconds = 300;
            public int TrafficWindowHours = 24;
            // Rumours
            public bool RumoursEnabled = true;
            public int MinRumourLength = 10;
            public int MaxRumourLength = 200;
            public int RumourCooldownMinutes = 120;
            public int MaxPendingRumoursPerPlayer = 1;
            public int MaxPendingRumours = 25;
            public int RumourQueueExpireHours = 48;
            public bool BroadcastApprovedRumours = true;
            public bool ChronicleRumours = true;
            public List<string> RumourBlockedWords = new List<string>();
            public int RumourListCount = 5;
            // Audit
            public int AuditRetentionDays = 7;
            public int MaxAuditEntries = 3000;
            public bool AuditStoreLetterText = true;
            public int AdminListLines = 15;
            public bool NotifyAdminsOfReports = true;
        }

        internal class SendRecord
        {
            public int LetterId;
            public DateTime At;
            public DateTime ArriveAt;
            public string TargetKey;        // "player:<id>" or "house:<key>"
            public string TargetLabel;
            public bool Anonymous;
        }

        internal class PlayerInfo
        {
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public List<string> BlockedPlayers = new List<string>();
            public List<string> BlockedHouses = new List<string>();   // house keys (lower case)
            public List<string> HiddenBlocks = new List<string>();    // ids blocked through an unsigned letter: never shown by name
            public bool BlockAnonymous;
            public DateTime MutedUntil;
            public List<SendRecord> Sent = new List<SendRecord>();
            public DateTime LastRumourAt;
            public DateTime LastSpyAt;
            public List<string> Notices = new List<string>();
        }

        internal class Letter
        {
            public int Id;
            public string SenderId;
            public string SenderName;
            public string SenderHouse;      // at send time, or null
            public bool Anonymous;
            public string TargetKind;
            public string TargetLabel;
            public string TargetHouse;      // the house written to, or the recipient's house at send time, or null
            public List<string> Recipients = new List<string>();
            public string Text;
            public DateTime SentAt;
            public DateTime ArriveAt;
        }

        internal class InboxLetter
        {
            public int LetterId;
            public string SenderId;         // kept server-side even for anonymous letters (block/report), never shown
            public string SenderHouse;
            public string From;             // what the reader sees
            public bool Anonymous;
            public string Text;
            public DateTime SentAt;
            public DateTime ArrivedAt;
            public bool Read;
            public bool Intercepted;        // this is a spymaster's copy
            public string InterceptedTo;
            public bool Tampered;
        }

        internal class HouseIntel
        {
            public string House;
            public string SpymasterId;
            public string SpymasterName;
            public DateTime SpymasterSetAt;
            public string Watch;            // house name being watched, or null
            public DateTime WatchSetAt;
            public List<string> Spies = new List<string>();
            public List<DateTime> Intercepts = new List<DateTime>();
            public Dictionary<string, DateTime> SpiedTargets = new Dictionary<string, DateTime>();
        }

        internal class Rumour
        {
            public int Id;
            public string SubmitterId;
            public string SubmitterName;
            public string Text;
            public DateTime SubmittedAt;
            public string Status;
            public DateTime DecidedAt;
            public string DecidedBy;
            public string Reason;
            public int ChronicleId;
        }

        internal class AuditEntry
        {
            public int Id;
            public DateTime At;
            public string Kind;
            public string ActorId;
            public string ActorName;
            public List<string> Targets = new List<string>();     // player ids involved
            public int LetterId;
            public string Detail;
        }

        internal class Report
        {
            public int Id;
            public DateTime At;
            public string ReporterId;
            public string ReporterName;
            public int LetterId;
            public string SenderId;
            public string SenderName;
            public bool Anonymous;
            public string Text;
            public string Reason;
            public bool Handled;
        }

        internal class TrafficRecord
        {
            public DateTime At;
            public string FromHouse;
            public string ToHouse;          // null = a player in no house
        }

        internal class StoredData
        {
            public int NextLetterId = 1;
            public int NextRumourId = 1;
            public int NextAuditId = 1;
            public int NextReportId = 1;
            public List<Letter> InFlight = new List<Letter>();
            public Dictionary<string, List<InboxLetter>> Inboxes = new Dictionary<string, List<InboxLetter>>();
            public Dictionary<string, PlayerInfo> Players = new Dictionary<string, PlayerInfo>();
            public Dictionary<string, HouseIntel> Houses = new Dictionary<string, HouseIntel>();
            public List<Rumour> Rumours = new List<Rumour>();
            public List<AuditEntry> Audit = new List<AuditEntry>();
            public List<Report> Reports = new List<Report>();
            public List<TrafficRecord> Traffic = new List<TrafficRecord>();
        }

        private class SpyOffer
        {
            public string House;
            public string ById;
            public DateTime Expires;
        }

        #endregion

        #region Rules (pure logic, no game types; exercised by plugins/docs/RealmRavens/logic-tests)

        internal static class Rules
        {
            internal static string Key(string name)
            {
                return (name ?? "").Trim().ToLowerInvariant();
            }

            internal static void ClampConfig(PluginConfig c)
            {
                c.MaxLetterLength = Clamp(c.MaxLetterLength, 20, 1000);
                c.MinLetterLength = Clamp(c.MinLetterLength, 1, c.MaxLetterLength);
                c.TravelSecondsMin = Clamp(c.TravelSecondsMin, 0, 3600);
                c.TravelSecondsMax = Clamp(c.TravelSecondsMax, c.TravelSecondsMin, 3600);
                c.AnonymousExtraSeconds = Clamp(c.AnonymousExtraSeconds, 0, 3600);
                c.MaxHouseRecipients = Clamp(c.MaxHouseRecipients, 1, 200);
                c.SendCooldownSeconds = Clamp(c.SendCooldownSeconds, 0, 86400);
                c.MaxLettersPerHour = Clamp(c.MaxLettersPerHour, 1, 1000);
                c.MaxLettersPerDay = Clamp(c.MaxLettersPerDay, c.MaxLettersPerHour, 10000);
                c.MaxLettersToSameTargetPerDay = Clamp(c.MaxLettersToSameTargetPerDay, 1, 1000);
                c.MaxAnonymousPerDay = Clamp(c.MaxAnonymousPerDay, 0, 1000);
                c.MaxInFlightPerSender = Clamp(c.MaxInFlightPerSender, 1, 100);
                c.MaxUnreadFromSameSender = Clamp(c.MaxUnreadFromSameSender, 1, 100);
                c.MaxInboxSize = Clamp(c.MaxInboxSize, 5, 200);
                c.InboxRetentionDays = Clamp(c.InboxRetentionDays, 1, 30);
                c.MaxBlocks = Clamp(c.MaxBlocks, 1, 500);
                c.NewPlayerGraceMinutes = Clamp(c.NewPlayerGraceMinutes, 0, 10080);
                c.InboxListLines = Clamp(c.InboxListLines, 1, 30);
                c.InterceptChance = Clamp01(c.InterceptChance);
                c.InterceptCooldownMinutes = Clamp(c.InterceptCooldownMinutes, 0, 10080);
                c.MaxInterceptsPerDay = Clamp(c.MaxInterceptsPerDay, 0, 1000);
                c.TamperNoticeChance = Clamp01(c.TamperNoticeChance);
                c.SpymasterChangeCooldownHours = Clamp(c.SpymasterChangeCooldownHours, 0, 720);
                c.WatchChangeCooldownMinutes = Clamp(c.WatchChangeCooldownMinutes, 0, 10080);
                c.MaxSpiesPerHouse = Clamp(c.MaxSpiesPerHouse, 0, 20);
                c.SpyCooldownHours = Clamp(c.SpyCooldownHours, 0, 720);
                c.SpySameTargetCooldownHours = Clamp(c.SpySameTargetCooldownHours, 0, 720);
                c.SpyCaughtChance = Clamp01(c.SpyCaughtChance);
                c.SpyOfferExpireSeconds = Clamp(c.SpyOfferExpireSeconds, 30, 3600);
                c.TrafficWindowHours = Clamp(c.TrafficWindowHours, 1, 168);
                c.MaxRumourLength = Clamp(c.MaxRumourLength, 20, 400);
                c.MinRumourLength = Clamp(c.MinRumourLength, 1, c.MaxRumourLength);
                c.RumourCooldownMinutes = Clamp(c.RumourCooldownMinutes, 0, 10080);
                c.MaxPendingRumoursPerPlayer = Clamp(c.MaxPendingRumoursPerPlayer, 1, 20);
                c.MaxPendingRumours = Clamp(c.MaxPendingRumours, 1, 500);
                c.RumourQueueExpireHours = Clamp(c.RumourQueueExpireHours, 1, 336);
                c.RumourListCount = Clamp(c.RumourListCount, 1, 20);
                if (c.RumourBlockedWords == null) c.RumourBlockedWords = new List<string>();
                c.RumourBlockedWords.RemoveAll(delegate(string w) { return string.IsNullOrEmpty(w) || w.Trim().Length == 0; });
                c.AuditRetentionDays = Clamp(c.AuditRetentionDays, 1, 7);          // hard ceiling: nothing older than 7 days
                c.MaxAuditEntries = Clamp(c.MaxAuditEntries, 100, 20000);
                c.AdminListLines = Clamp(c.AdminListLines, 1, 50);
            }

            internal static int Clamp(int v, int min, int max)
            {
                return v < min ? min : (v > max ? max : v);
            }

            internal static double Clamp01(double v)
            {
                if (double.IsNaN(v) || v < 0) return 0;
                return v > 1 ? 1 : v;
            }

            // Repairs a hand-edited or partial file: restores null collections, drops broken rows, fixes id counters.
            internal static void Normalize(StoredData d)
            {
                if (d.InFlight == null) d.InFlight = new List<Letter>();
                if (d.Inboxes == null) d.Inboxes = new Dictionary<string, List<InboxLetter>>();
                if (d.Players == null) d.Players = new Dictionary<string, PlayerInfo>();
                if (d.Houses == null) d.Houses = new Dictionary<string, HouseIntel>();
                if (d.Rumours == null) d.Rumours = new List<Rumour>();
                if (d.Audit == null) d.Audit = new List<AuditEntry>();
                if (d.Reports == null) d.Reports = new List<Report>();
                if (d.Traffic == null) d.Traffic = new List<TrafficRecord>();

                d.InFlight.RemoveAll(delegate(Letter l) { return l == null || l.SenderId == null || l.Text == null; });
                foreach (Letter l in d.InFlight)
                {
                    if (l.Recipients == null) l.Recipients = new List<string>();
                    if (l.Id >= d.NextLetterId) d.NextLetterId = l.Id + 1;
                }
                var emptyBoxes = new List<string>();
                foreach (KeyValuePair<string, List<InboxLetter>> kv in d.Inboxes)
                {
                    if (kv.Value == null) { emptyBoxes.Add(kv.Key); continue; }
                    kv.Value.RemoveAll(delegate(InboxLetter i) { return i == null || i.Text == null; });
                    foreach (InboxLetter i in kv.Value) if (i.LetterId >= d.NextLetterId) d.NextLetterId = i.LetterId + 1;
                }
                foreach (string k in emptyBoxes) d.Inboxes.Remove(k);
                var badPlayers = new List<string>();
                foreach (KeyValuePair<string, PlayerInfo> kv in d.Players)
                {
                    PlayerInfo p = kv.Value;
                    if (p == null) { badPlayers.Add(kv.Key); continue; }
                    if (p.BlockedPlayers == null) p.BlockedPlayers = new List<string>();
                    if (p.BlockedHouses == null) p.BlockedHouses = new List<string>();
                    if (p.Sent == null) p.Sent = new List<SendRecord>();
                    if (p.Notices == null) p.Notices = new List<string>();
                    if (p.HiddenBlocks == null) p.HiddenBlocks = new List<string>();
                    p.HiddenBlocks.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s) || !p.BlockedPlayers.Contains(s); });
                    p.Sent.RemoveAll(delegate(SendRecord s) { return s == null; });
                    p.BlockedPlayers.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s); });
                    p.BlockedHouses.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s); });
                    p.Notices.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s); });
                }
                foreach (string k in badPlayers) d.Players.Remove(k);
                var badHouses = new List<string>();
                foreach (KeyValuePair<string, HouseIntel> kv in d.Houses)
                {
                    HouseIntel h = kv.Value;
                    if (h == null || string.IsNullOrEmpty(h.House)) { badHouses.Add(kv.Key); continue; }
                    if (h.Spies == null) h.Spies = new List<string>();
                    if (h.Intercepts == null) h.Intercepts = new List<DateTime>();
                    if (h.SpiedTargets == null) h.SpiedTargets = new Dictionary<string, DateTime>();
                    h.Spies.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s); });
                }
                foreach (string k in badHouses) d.Houses.Remove(k);
                d.Rumours.RemoveAll(delegate(Rumour r) { return r == null || r.Text == null || r.Status == null; });
                foreach (Rumour r in d.Rumours) if (r.Id >= d.NextRumourId) d.NextRumourId = r.Id + 1;
                d.Audit.RemoveAll(delegate(AuditEntry a) { return a == null || a.Kind == null; });
                foreach (AuditEntry a in d.Audit)
                {
                    if (a.Targets == null) a.Targets = new List<string>();
                    if (a.Id >= d.NextAuditId) d.NextAuditId = a.Id + 1;
                }
                d.Reports.RemoveAll(delegate(Report r) { return r == null; });
                foreach (Report r in d.Reports) if (r.Id >= d.NextReportId) d.NextReportId = r.Id + 1;
                d.Traffic.RemoveAll(delegate(TrafficRecord t) { return t == null || t.FromHouse == null; });
            }

            // Strips control characters and chat colour tags, collapses whitespace. Does not truncate: callers
            // reject over-long text so a player never has a letter silently cut.
            internal static string Sanitize(string s)
            {
                if (s == null) return "";
                var sb = new StringBuilder(s.Length);
                bool space = false;
                foreach (char ch in s)
                {
                    char c = ch;
                    if (char.IsControl(c) || char.IsWhiteSpace(c)) c = ' ';
                    if (c == '[') c = '(';
                    else if (c == ']') c = ')';
                    if (c == ' ')
                    {
                        if (space || sb.Length == 0) continue;
                        space = true;
                    }
                    else space = false;
                    sb.Append(c);
                }
                return sb.ToString().Trim();
            }

            internal static string Preview(string s, int max)
            {
                if (s == null) return "";
                return s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 3)) + "...";
            }

            internal static int TravelSeconds(PluginConfig c, bool anonymous, double roll)
            {
                int span = c.TravelSecondsMax - c.TravelSecondsMin;
                int secs = c.TravelSecondsMin + (int)Math.Floor(Clamp01(roll) * (span + 1));
                if (secs > c.TravelSecondsMax) secs = c.TravelSecondsMax;
                if (anonymous) secs += c.AnonymousExtraSeconds;
                return secs;
            }

            internal static void PruneSent(PlayerInfo p, DateTime now)
            {
                p.Sent.RemoveAll(delegate(SendRecord s) { return s.At < now.AddDays(-1) && s.ArriveAt < now; });
            }

            // Returns null when the letter may be sent; otherwise a lang key. wait is how long until it frees up.
            internal static string CheckSendLimits(PlayerInfo p, DateTime now, string targetKey, bool anonymous,
                int inFlightFromSender, PluginConfig c, out TimeSpan wait)
            {
                wait = TimeSpan.Zero;
                if (p.MutedUntil > now) { wait = p.MutedUntil - now; return "Muted"; }
                if (inFlightFromSender >= c.MaxInFlightPerSender) return "LimitInFlight";
                int hour = 0, day = 0, same = 0, anon = 0;
                DateTime last = DateTime.MinValue;
                DateTime oldestHour = DateTime.MaxValue, oldestDay = DateTime.MaxValue, oldestSame = DateTime.MaxValue, oldestAnon = DateTime.MaxValue;
                foreach (SendRecord s in p.Sent)
                {
                    if (s.At > last) last = s.At;
                    if (s.At <= now.AddDays(-1)) continue;
                    day++;
                    if (s.At < oldestDay) oldestDay = s.At;
                    if (s.At > now.AddHours(-1)) { hour++; if (s.At < oldestHour) oldestHour = s.At; }
                    if (s.TargetKey == targetKey) { same++; if (s.At < oldestSame) oldestSame = s.At; }
                    if (s.Anonymous) { anon++; if (s.At < oldestAnon) oldestAnon = s.At; }
                }
                if (c.SendCooldownSeconds > 0 && last > now.AddSeconds(-c.SendCooldownSeconds))
                {
                    wait = last.AddSeconds(c.SendCooldownSeconds) - now;
                    return "LimitCooldown";
                }
                if (hour >= c.MaxLettersPerHour) { wait = oldestHour.AddHours(1) - now; return "LimitHour"; }
                if (day >= c.MaxLettersPerDay) { wait = oldestDay.AddDays(1) - now; return "LimitDay"; }
                if (same >= c.MaxLettersToSameTargetPerDay) { wait = oldestSame.AddDays(1) - now; return "LimitSameTarget"; }
                if (anonymous && anon >= c.MaxAnonymousPerDay)
                {
                    if (anon > 0) wait = oldestAnon.AddDays(1) - now;
                    return "LimitAnon";
                }
                return null;
            }

            internal static int CountUnreadFrom(List<InboxLetter> box, string senderId)
            {
                if (box == null) return 0;
                int n = 0;
                foreach (InboxLetter i in box) if (!i.Read && !i.Intercepted && i.SenderId == senderId) n++;
                return n;
            }

            internal static int CountInFlight(List<Letter> inFlight, string senderId, string recipientId)
            {
                int n = 0;
                foreach (Letter l in inFlight)
                {
                    if (l.SenderId != senderId) continue;
                    if (recipientId == null || l.Recipients.Contains(recipientId)) n++;
                }
                return n;
            }

            // True when the recipient does not want this letter. Checked again at arrival, so a block made while a
            // raven is in the air still stops it.
            internal static bool IsBlocked(PlayerInfo recipient, string senderId, string senderHouse, bool anonymous)
            {
                if (recipient == null) return false;
                if (anonymous && recipient.BlockAnonymous) return true;
                if (recipient.BlockedPlayers.Contains(senderId)) return true;
                if (!string.IsNullOrEmpty(senderHouse) && recipient.BlockedHouses.Contains(Key(senderHouse))) return true;
                return false;
            }

            // Adds to an inbox capped at max: drops the oldest read letters first, then the oldest unread ones.
            // A full inbox drops the oldest READ letter first. If every letter is unread, it drops the oldest unread letter
            // of the sender who has the most letters in the box, so a flood from many alts (each within its own unread cap)
            // evicts the flooders' letters instead of pushing out the one real letter the reader has not seen yet.
            internal static void AddToInbox(List<InboxLetter> box, InboxLetter letter, int max)
            {
                box.Add(letter);
                while (box.Count > max)
                {
                    int drop = -1;
                    for (int i = 0; i < box.Count; i++)
                        if (box[i].Read && (drop < 0 || box[i].ArrivedAt < box[drop].ArrivedAt)) drop = i;
                    if (drop < 0)
                    {
                        var perSender = new Dictionary<string, int>();
                        foreach (InboxLetter l in box)
                        {
                            string k = SenderKey(l);
                            int n;
                            perSender.TryGetValue(k, out n);
                            perSender[k] = n + 1;
                        }
                        int most = 0;
                        foreach (int n in perSender.Values) if (n > most) most = n;
                        for (int i = 0; i < box.Count; i++)
                            if (perSender[SenderKey(box[i])] == most && (drop < 0 || box[i].ArrivedAt < box[drop].ArrivedAt)) drop = i;
                    }
                    box.RemoveAt(drop);
                }
            }

            // Anonymous letters are grouped as one sender: the reader cannot tell them apart, and alts sending
            // anonymously are exactly the flood this guards against.
            private static string SenderKey(InboxLetter l)
            {
                if (l.Anonymous) return "anon";
                return l.SenderId ?? "";
            }

            internal static InboxLetter FindInInbox(List<InboxLetter> box, int letterId)
            {
                if (box == null) return null;
                foreach (InboxLetter i in box) if (i.LetterId == letterId) return i;
                return null;
            }

            internal static int CountRecent(List<DateTime> times, DateTime since)
            {
                int n = 0;
                foreach (DateTime t in times) if (t > since) n++;
                return n;
            }

            internal static DateTime Latest(List<DateTime> times)
            {
                DateTime last = DateTime.MinValue;
                foreach (DateTime t in times) if (t > last) last = t;
                return last;
            }

            // Picks the house whose spymaster intercepts this letter, or null. houseOf gives a player's CURRENT house
            // (a spymaster who left the house has no power). roll returns [0,1).
            internal static string PickInterceptor(Letter l, Dictionary<string, HouseIntel> houses, Func<string, string> houseOf,
                DateTime now, PluginConfig c, Func<double> roll)
            {
                if (!c.InterceptionEnabled || c.InterceptChance <= 0 || houses == null) return null;
                string from = Key(l.SenderHouse), to = Key(l.TargetHouse);
                var candidates = new List<string>();
                foreach (KeyValuePair<string, HouseIntel> kv in houses)
                {
                    HouseIntel h = kv.Value;
                    if (string.IsNullOrEmpty(h.SpymasterId) || string.IsNullOrEmpty(h.Watch)) continue;
                    string own = Key(h.House);
                    string watch = Key(h.Watch);
                    if (watch != from && watch != to) continue;
                    if (own == from || own == to) continue;                     // never your own house's ravens
                    if (h.SpymasterId == l.SenderId || l.Recipients.Contains(h.SpymasterId)) continue;
                    if (Key(houseOf(h.SpymasterId)) != own) continue;
                    if (CountRecent(h.Intercepts, now.AddDays(-1)) >= c.MaxInterceptsPerDay) continue;
                    if (Latest(h.Intercepts) > now.AddMinutes(-c.InterceptCooldownMinutes)) continue;
                    candidates.Add(kv.Key);
                }
                if (candidates.Count == 0) return null;
                candidates.Sort(StringComparer.Ordinal);
                int start = (int)Math.Floor(Clamp01(roll()) * candidates.Count) % candidates.Count;
                for (int i = 0; i < candidates.Count; i++)
                {
                    string k = candidates[(start + i) % candidates.Count];
                    if (roll() < c.InterceptChance) return k;
                }
                return null;
            }

            // Returns null when a spy may act now; otherwise a lang key and how long to wait.
            internal static string CheckSpy(PlayerInfo spy, HouseIntel own, string targetKey, DateTime now, PluginConfig c, out TimeSpan wait)
            {
                wait = TimeSpan.Zero;
                if (spy.LastSpyAt > now.AddHours(-c.SpyCooldownHours))
                {
                    wait = spy.LastSpyAt.AddHours(c.SpyCooldownHours) - now;
                    return "SpyCooldown";
                }
                DateTime last;
                if (own.SpiedTargets.TryGetValue(targetKey, out last) && last > now.AddHours(-c.SpySameTargetCooldownHours))
                {
                    wait = last.AddHours(c.SpySameTargetCooldownHours) - now;
                    return "SpyTargetCooldown";
                }
                return null;
            }

            // Raven traffic sent by one house in the window: total and per destination (null destination = "unhoused").
            internal static Dictionary<string, int> TrafficFrom(List<TrafficRecord> traffic, string house, DateTime since, out int total)
            {
                total = 0;
                var by = new Dictionary<string, int>();
                string k = Key(house);
                foreach (TrafficRecord t in traffic)
                {
                    if (t.At <= since || Key(t.FromHouse) != k) continue;
                    total++;
                    string dest = t.ToHouse ?? "";
                    int n;
                    by.TryGetValue(dest, out n);
                    by[dest] = n + 1;
                }
                return by;
            }

            internal static bool ContainsBlockedWord(string text, List<string> words)
            {
                if (words == null || text == null) return false;
                string lower = text.ToLowerInvariant();
                foreach (string w in words)
                    if (!string.IsNullOrEmpty(w) && lower.IndexOf(w.Trim().ToLowerInvariant(), StringComparison.Ordinal) >= 0) return true;
                return false;
            }

            internal static int PendingRumours(List<Rumour> rumours, string submitterId)
            {
                int n = 0;
                foreach (Rumour r in rumours)
                    if (r.Status == RPending && (submitterId == null || r.SubmitterId == submitterId)) n++;
                return n;
            }

            // Returns null when the rumour may enter the queue; otherwise a lang key and how long to wait.
            internal static string CheckRumour(PlayerInfo p, List<Rumour> rumours, DateTime now, PluginConfig c, out TimeSpan wait)
            {
                wait = TimeSpan.Zero;
                if (p.MutedUntil > now) { wait = p.MutedUntil - now; return "Muted"; }
                if (p.FirstSeen > now.AddMinutes(-c.NewPlayerGraceMinutes)) { wait = p.FirstSeen.AddMinutes(c.NewPlayerGraceMinutes) - now; return "TooNew"; }
                if (p.LastRumourAt > now.AddMinutes(-c.RumourCooldownMinutes)) { wait = p.LastRumourAt.AddMinutes(c.RumourCooldownMinutes) - now; return "RumourCooldown"; }
                if (PendingRumours(rumours, null) >= c.MaxPendingRumours) return "RumourQueueFull";
                return null;
            }

            // Ids whose name matches: all exact (case-insensitive) matches if any, else all prefix matches.
            internal static List<string> MatchNames(Dictionary<string, string> idToName, string text)
            {
                var exact = new List<string>();
                var prefix = new List<string>();
                string t = (text ?? "").Trim().ToLowerInvariant();
                if (t.Length == 0) return exact;
                foreach (KeyValuePair<string, string> kv in idToName)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    string n = kv.Value.ToLowerInvariant();
                    if (n == t) exact.Add(kv.Key);
                    else if (n.StartsWith(t, StringComparison.Ordinal)) prefix.Add(kv.Key);
                }
                return exact.Count > 0 ? exact : prefix;
            }

            // Retention: everything that holds private text or ids is bounded in time and size.
            internal static void Prune(StoredData d, DateTime now, PluginConfig c, Predicate<string> keepPlayer)
            {
                DateTime auditCut = now.AddDays(-c.AuditRetentionDays);
                d.Audit.RemoveAll(delegate(AuditEntry a) { return a.At < auditCut; });
                if (d.Audit.Count > c.MaxAuditEntries) d.Audit.RemoveRange(0, d.Audit.Count - c.MaxAuditEntries);
                d.Reports.RemoveAll(delegate(Report r) { return r.At < auditCut; });
                d.Traffic.RemoveAll(delegate(TrafficRecord t) { return t.At < now.AddHours(-Math.Max(c.TrafficWindowHours, 24)); });
                if (d.Traffic.Count > 5000) d.Traffic.RemoveRange(0, d.Traffic.Count - 5000);

                DateTime inboxCut = now.AddDays(-c.InboxRetentionDays);
                var emptyBoxes = new List<string>();
                foreach (KeyValuePair<string, List<InboxLetter>> kv in d.Inboxes)
                {
                    kv.Value.RemoveAll(delegate(InboxLetter i) { return i.ArrivedAt < inboxCut; });
                    if (kv.Value.Count == 0) emptyBoxes.Add(kv.Key);
                }
                foreach (string k in emptyBoxes) d.Inboxes.Remove(k);

                // Pending rumours expire; decided ones keep their submitter id only for the audit window. Approved
                // rumour text is public already, but only the newest 50 are kept.
                foreach (Rumour r in d.Rumours)
                    if (r.Status == RPending && r.SubmittedAt < now.AddHours(-c.RumourQueueExpireHours))
                    {
                        r.Status = RExpired;
                        r.DecidedAt = now;
                        r.DecidedBy = "queue";
                    }
                d.Rumours.RemoveAll(delegate(Rumour r) { return r.Status != RPending && r.Status != RApproved && r.DecidedAt < auditCut; });
                foreach (Rumour r in d.Rumours)
                    if (r.Status == RApproved && r.DecidedAt < auditCut) { r.SubmitterId = null; r.SubmitterName = null; }
                int approved = 0;
                for (int i = d.Rumours.Count - 1; i >= 0; i--)
                    if (d.Rumours[i].Status == RApproved && ++approved > 50) d.Rumours.RemoveAt(i);

                foreach (HouseIntel h in d.Houses.Values)
                {
                    h.Intercepts.RemoveAll(delegate(DateTime t) { return t < now.AddDays(-2); });
                    var stale = new List<string>();
                    foreach (KeyValuePair<string, DateTime> kv in h.SpiedTargets)
                        if (kv.Value < now.AddHours(-Math.Max(c.SpySameTargetCooldownHours, 1))) stale.Add(kv.Key);
                    foreach (string k in stale) h.SpiedTargets.Remove(k);
                }

                var gone = new List<string>();
                foreach (KeyValuePair<string, PlayerInfo> kv in d.Players)
                {
                    PlayerInfo p = kv.Value;
                    PruneSent(p, now);
                    if (p.Notices.Count > 10) p.Notices.RemoveRange(0, p.Notices.Count - 10);
                    bool idle = p.Sent.Count == 0 && p.BlockedPlayers.Count == 0 && p.BlockedHouses.Count == 0 && !p.BlockAnonymous
                        && p.MutedUntil < now && p.Notices.Count == 0 && !d.Inboxes.ContainsKey(kv.Key)
                        && p.LastSeen < now.AddDays(-60);
                    if (idle && (keepPlayer == null || !keepPlayer(kv.Key))) gone.Add(kv.Key);
                }
                foreach (string k in gone) d.Players.Remove(k);
            }

            internal static string Duration(TimeSpan span)
            {
                if (span < TimeSpan.Zero) span = TimeSpan.Zero;
                if (span.TotalDays >= 1) return (int)span.TotalDays + "d " + span.Hours + "h";
                if (span.TotalHours >= 1) return (int)span.TotalHours + "h " + span.Minutes + "m";
                if (span.TotalMinutes >= 1) return (int)span.TotalMinutes + "m";
                return Math.Max(1, (int)Math.Ceiling(span.TotalSeconds)) + "s";
            }
        }

        #endregion

        #region Lifecycle

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void Init()
        {
            try
            {
                config = Config.ReadObject<PluginConfig>();
            }
            catch (Exception ex)
            {
                PrintError("Could not read oxide/config/" + Name + ".json (" + ex.Message + "); using defaults for this session.");
                config = null;
            }
            if (config == null) config = new PluginConfig();
            Rules.ClampConfig(config);
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                         // re-sent on hot load; keep idempotent
            initialized = true;
            timer.Every(TickSeconds, Tick);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            SaveData();
        }

        private void LoadData()
        {
            StoredData loaded = null;
            bool existed = false;
            try
            {
                existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". The rookery is closed and the file will NOT be overwritten. Fix or remove it, then reload.");
                return;
            }
            if (loaded == null && existed)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or null. The rookery is closed and the file will NOT be overwritten.");
                return;
            }
            data = loaded ?? new StoredData();
            Rules.Normalize(data);
        }

        private void SaveData()
        {
            if (data == null || loadFailed) return;          // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
        }

        private void Tick()
        {
            if (loadFailed || data == null) return;
            DateTime now = DateTime.UtcNow;
            if (data.InFlight.Count > 0)
            {
                var landed = new List<Letter>();
                foreach (Letter l in data.InFlight) if (l.ArriveAt <= now) landed.Add(l);
                foreach (Letter l in landed)
                {
                    data.InFlight.Remove(l);
                    Deliver(l, now);
                    dirty = true;
                }
            }
            if (++tickCount % MaintenanceEveryTicks == 0) Maintenance(now);
            if (dirty) SaveData();
        }

        private void Maintenance(DateTime now)
        {
            int expiredBefore = CountStatus(RExpired);
            Rules.Prune(data, now, config, null);
            if (CountStatus(RExpired) != expiredBefore)
            {
                foreach (Rumour r in data.Rumours)
                    if (r.Status == RExpired && r.DecidedAt == now && r.SubmitterId != null)
                        Notify(r.SubmitterId, Msg("RumourExpired", r.SubmitterId, r.Id));
            }
            var offersGone = new List<string>();
            foreach (KeyValuePair<string, SpyOffer> kv in spyOffers) if (kv.Value.Expires < now) offersGone.Add(kv.Key);
            foreach (string k in offersGone) spyOffers.Remove(k);
            if (HousesLoaded()) ValidateIntel();
            dirty = true;
        }

        private int CountStatus(string status)
        {
            int n = 0;
            foreach (Rumour r in data.Rumours) if (r.Status == status) n++;
            return n;
        }

        // Drops spymasters and spies who no longer belong to the house, and intel of houses that no longer exist.
        private void ValidateIntel()
        {
            var gone = new List<string>();
            foreach (KeyValuePair<string, HouseIntel> kv in data.Houses)
            {
                HouseIntel h = kv.Value;
                if (HouseMembers(h.House) == null) { gone.Add(kv.Key); continue; }
                if (h.SpymasterId != null && Rules.Key(HouseOf(h.SpymasterId)) != kv.Key)
                {
                    Audit("spymaster_lapsed", h.SpymasterId, h.SpymasterName, null, 0, "left House " + h.House);
                    h.SpymasterId = null; h.SpymasterName = null; h.Watch = null;
                }
                h.Spies.RemoveAll(delegate(string id) { return Rules.Key(HouseOf(id)) != kv.Key; });
                if (h.Watch != null && HouseMembers(h.Watch) == null) h.Watch = null;
            }
            foreach (string k in gone) data.Houses.Remove(k);
        }

        #endregion

        #region Player hooks

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer || loadFailed || data == null) return;
            string id = player.Id.ToString();
            PlayerInfo p = Info(id, player.Name);
            p.LastSeen = DateTime.UtcNow;
            dirty = true;
            timer.Once(8f, delegate { GreetOnLogin(id); });
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null || player.IsServer || loadFailed || data == null) return;
            PlayerInfo p;
            if (data.Players.TryGetValue(player.Id.ToString(), out p)) { p.LastSeen = DateTime.UtcNow; dirty = true; }
        }

        private void GreetOnLogin(string id)
        {
            Player player = Online(id);
            if (player == null || data == null) return;
            PlayerInfo p = Info(id, player.Name);
            if (p.Notices.Count > 0)
            {
                foreach (string n in p.Notices) player.SendMessage(n);
                p.Notices.Clear();
                dirty = true;
            }
            int unread = 0;
            List<InboxLetter> box;
            if (data.Inboxes.TryGetValue(id, out box)) foreach (InboxLetter i in box) if (!i.Read) unread++;
            if (unread > 0) player.SendMessage(Msg("LoginUnread", id, unread));
            if (IsAdmin(id))
            {
                int pending = Rules.PendingRumours(data.Rumours, null);
                int reports = 0;
                foreach (Report r in data.Reports) if (!r.Handled) reports++;
                if (pending > 0 || reports > 0) player.SendMessage(Msg("AdminLoginQueue", id, pending, reports));
            }
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Help1", "[8AA0B4]Ravens[FFFFFF]: /raven <house|player> <message> - a sealed letter (it flies {0}-{1}s). /raven anon <target> <message> - unsigned. Names with spaces go in \"quotes\"; prefix h: or p: to force a house or a player." },
                { "Help2", "/raven inbox | read <#> | delete <#|read|all> | sent | status | block <player|house|#letter|anon> | unblock <player|house|anon|hidden> | blocks | report <#> [reason]" },
                { "Help3", "Intrigue: /raven spymaster <player|none> (leader) | watch <house|none> (spymaster) | spy swear|dismiss <player> (leader) | spy accept|decline | spy list | spy report <house>. Rumours: /rumour <text> (moderated), /rumour list." },
                { "Help4", "Ravens can be intercepted by a rival spymaster. Never put anything in a letter you could not bear the realm to read." },
                { "HelpAdmin", "Admin: /raven admin queue | approve <id> | reject <id> [reason] | reports | resolve <report id> | audit <player> [hours] | letter <#> | mute <player> <hours> | unmute <player> | purge <player> | save" },
                { "Closed", "The rookery is closed: the ravens' data file could not be read. An admin must fix oxide/data/RealmRavens.json." },
                { "NoPermission", "You may not do that." },
                { "HousesMissing", "That needs the RealmHouses plugin, which is not loaded." },
                { "NoHouse", "You are not sworn to any house." },
                { "NotLeader", "Only the leader of your house may do that." },
                { "NoSuchHouse", "No house named '{0}' exists." },
                { "NoSuchPlayer", "No player known to the rookery matches '{0}'." },
                { "Ambiguous", "'{0}' could mean: {1}. Be more exact, or use \"quotes\"." },
                { "NoSelf", "You cannot send a raven to yourself." },
                { "NoRecipients", "No one in House {0} could receive your raven." },
                { "SendUsage", "Usage: /raven <house|player> <message>  or  /raven anon <house|player> <message>" },
                { "TooShort", "Your letter is too short." },
                { "TooLong", "Your letter is too long ({0} characters; at most {1})." },
                { "AnonDisabled", "Unsigned letters are not allowed in this realm." },
                { "TooNew", "You are new to the realm. Wait {0} before sending unsigned letters or rumours." },
                { "Muted", "The rookery will not serve you for another {0}." },
                { "LimitCooldown", "Your last raven has barely left. Wait {0}." },
                { "LimitHour", "You have sent all the ravens you may this hour. Next in {0}." },
                { "LimitDay", "You have sent all the ravens you may today. Next in {0}." },
                { "LimitSameTarget", "You have written to them enough today. Next in {0}." },
                { "LimitAnon", "You have sent all the unsigned letters you may today." },
                { "LimitInFlight", "Too many of your ravens are still in the air. Wait for one to land." },
                { "RoostFull", "{0} already has too many unread letters from you." },
                { "Sent", "Your raven takes wing toward {0}. It should land in about {1}. (letter #{2})" },
                { "SentAnon", "Your unsigned raven takes wing toward {0}. It should land in about {1}. (letter #{2})" },
                { "Arrived", "[8AA0B4]A raven lands[FFFFFF] bearing a letter from {0}. Read it with /raven read {1}" },
                { "ArrivedIntercepted", "[8AA0B4]Your agents[FFFFFF] copied a raven from {0} to {1}. Read it with /raven read {2}" },
                { "LoginUnread", "[8AA0B4]Ravens[FFFFFF]: {0} unread letter(s) wait for you. /raven inbox" },
                { "AnonFrom", "an unknown hand" },
                { "AnonFromHouse", "an unsigned letter sealed with the mark of House {0}" },
                { "SignedFrom", "{0}" },
                { "SignedFromHouse", "{0} of House {1}" },
                { "ToHouse", "House {0}" },
                { "InboxEmpty", "Your inbox is empty." },
                { "InboxHeader", "Your letters ({0}, {1} unread), newest first:" },
                { "InboxLine", "#{0} {1}from {2}, {3} ago: {4}" },
                { "InboxUnreadTag", "[unread] " },
                { "InboxInterceptTag", "[intercepted] " },
                { "InboxMore", "...and {0} older. /raven read <#> works on any of them." },
                { "ReadHeader", "Letter #{0} from {1}, sent {2} ago:" },
                { "ReadIntercepted", "Letter #{0}, copied in secret: from {1} to {2}, sent {3} ago:" },
                { "ReadTampered", "(The seal looks as if it has been lifted and pressed again.)" },
                { "ReadBody", "\"{0}\"" },
                { "NoSuchLetter", "You have no letter #{0}." },
                { "ReadUsage", "Usage: /raven read <#>" },
                { "Deleted", "Deleted {0} letter(s)." },
                { "DeleteUsage", "Usage: /raven delete <#|read|all>" },
                { "SentEmpty", "You have sent no ravens in the last day." },
                { "SentHeader", "Your ravens of the last day:" },
                { "SentLine", "#{0} to {1}{2} - {3}" },
                { "SentAnonTag", " (unsigned)" },
                { "SentInFlight", "in the air, lands in about {0}" },
                { "SentFlown", "flown" },
                { "Status", "Ravens left: {0} this hour, {1} today, {2} unsigned today. In the air: {3}/{4}." },
                { "StatusRole", "Your secret office: {0}" },
                { "RoleSpymaster", "spymaster of House {0}, watching {1}" },
                { "RoleSpy", "sworn spy of House {0}" },
                { "Nothing", "nothing" },
                { "BlockUsage", "Usage: /raven block <player|house|#letter|anon>" },
                { "Blocked", "You will receive no ravens from {0}." },
                { "BlockedAnon", "You will receive no unsigned ravens." },
                { "BlockedLetterSender", "You will receive no more ravens from the sender of letter #{0}." },
                { "AlreadyBlocked", "{0} is already blocked." },
                { "TooManyBlocks", "Your block list is full ({0})." },
                { "Unblocked", "{0} may send you ravens again." },
                { "UnblockedAnon", "You will receive unsigned ravens again." },
                { "UnblockedHidden", "Lifted {0} block(s) you placed through unsigned letters." },
                { "HiddenBlocks", "{0} unknown hand(s) (/raven unblock hidden)" },
                { "NotBlocked", "{0} is not blocked." },
                { "BlocksEmpty", "You block no one." },
                { "BlocksLine", "Blocked: players {0}; houses {1}; unsigned letters: {2}." },
                { "Yes", "yes" },
                { "No", "no" },
                { "ReportUsage", "Usage: /raven report <#> [reason]" },
                { "ReportCannot", "Copies made by your own agents cannot be reported." },
                { "Reported", "Letter #{0} has been reported to the realm's stewards. You can also /raven block #{0}." },
                { "AdminReportNotice", "[C8A050]Ravens[FFFFFF]: a letter was reported (report {0}). /raven admin reports" },
                { "SpymasterUsage", "Usage: /raven spymaster <player|none>" },
                { "SpymasterNotMember", "{0} is not a member of your house." },
                { "SpymasterCooldown", "Your house changed its spymaster too recently. Next change in {0}." },
                { "SpymasterSet", "{0} is now the spymaster of House {1}. Only your house knows." },
                { "SpymasterYou", "[8AA0B4]You are now the spymaster of House {0}.[FFFFFF] Choose whom to watch with /raven watch <house>." },
                { "SpymasterCleared", "House {0} has no spymaster now." },
                { "NotSpymaster", "Only your house's spymaster may do that." },
                { "WatchUsage", "Usage: /raven watch <house|none>" },
                { "WatchOwn", "Your agents cannot watch your own house." },
                { "WatchCooldown", "Your agents are still settling in. You may change whom you watch in {0}." },
                { "WatchSet", "Your agents now watch the ravens of House {0}." },
                { "WatchCleared", "Your agents watch no one." },
                { "InterceptOff", "(Interception is turned off on this server.)" },
                { "SpyUsage", "Usage: /raven spy swear <player> | dismiss <player> | accept | decline | list | report <house>" },
                { "SpiesDisabled", "Sworn spies are not allowed in this realm." },
                { "SpyTooMany", "Your house already has {0} sworn spies." },
                { "SpyAlready", "{0} is already sworn to your house's secret service." },
                { "SpyNotOnline", "{0} must be online to swear the oath." },
                { "SpyOffered", "You offer {0} a place among your spies. They must accept within {1}." },
                { "SpyOfferReceived", "[8AA0B4]Your leader offers you a secret oath[FFFFFF] as a spy of House {0}. /raven spy accept or /raven spy decline" },
                { "SpyNoOffer", "No one has offered you a secret oath." },
                { "SpySworn", "You are sworn as a spy of House {0}. /raven spy report <house> to learn another house's secrets." },
                { "SpySwornLeader", "{0} has sworn the secret oath." },
                { "SpyDeclined", "You decline the oath." },
                { "SpyDismissed", "{0} is released from the secret oath." },
                { "SpyNotSpy", "{0} is not one of your spies." },
                { "SpyListHeader", "The secret service of House {0}: spymaster {1}; spies {2}; watching {3}." },
                { "NotSpy", "Only a sworn spy or the spymaster of a house may do that." },
                { "SpyOwn", "You need no spy to learn your own house's secrets." },
                { "SpyCooldown", "Your cover needs time. You may spy again in {0}." },
                { "SpyTargetCooldown", "Your house spied on them too recently. Again in {0}." },
                { "SpyReportHeader", "[8AA0B4]Your spy's report on House {0}:[FFFFFF]" },
                { "SpyReportTreaties", "Treaties: {0}" },
                { "SpyReportFealty", "Sworn to: {0} | Vassals: {1}" },
                { "SpyReportTraffic", "Ravens sent in the last {0}h: {1}{2}" },
                { "SpyReportTrafficTo", " (to {0})" },
                { "SpyReportService", "Their secret service: {0}" },
                { "SpyServiceNone", "no spymaster" },
                { "SpyServiceWatch", "a spymaster watching House {0}, and {1} sworn spies" },
                { "SpyServiceIdle", "a spymaster watching no one, and {0} sworn spies" },
                { "Unhoused", "the unhoused" },
                { "SpyCaughtSelf", "[FF6050]You were seen.[FFFFFF] House {0} knows a spy of your house was prying." },
                { "SpyCaughtTarget", "[FF6050]A spy was caught[FFFFFF] prying into the affairs of House {0}: {1}." },
                { "SpyCaughtWho", "a spy of House {0}" },
                { "SpyCaughtWhoNamed", "{0}, a spy of House {1}" },
                { "RumourUsage", "Usage: /rumour <text> (it is checked by the realm's stewards before it spreads) | /rumour list" },
                { "RumoursDisabled", "Rumours are not collected in this realm." },
                { "RumourTooShort", "That is too short to be a rumour." },
                { "RumourTooLong", "A rumour must be at most {0} characters ({1} given)." },
                { "RumourCooldown", "You whispered too recently. Again in {0}." },
                { "RumourQueueFull", "The taverns are already full of whispers. Try again later." },
                { "RumourMine", "You already have a rumour waiting for the stewards." },
                { "RumourRefused", "That rumour cannot be spread here." },
                { "RumourQueued", "Your rumour (#{0}) is whispered to the stewards. If they allow it, it spreads without your name." },
                { "RumourApproved", "[8AA0B4]Your rumour #{0} spreads through the realm.[FFFFFF]" },
                { "RumourRejected", "Your rumour #{0} was not allowed to spread{1}." },
                { "RumourExpired", "Your rumour #{0} was never heard by the stewards and has faded." },
                { "RumourBroadcast", "[9A8A70]Whispers in the taverns[FFFFFF]: {0}" },
                { "RumourListEmpty", "No rumours have spread yet." },
                { "RumourListHeader", "The latest rumours:" },
                { "RumourListLine", "{0} ago: {1}" },
                { "AdminNewRumour", "[C8A050]Ravens[FFFFFF]: a rumour awaits moderation (#{0}). /raven admin queue" },
                { "AdminQueueEmpty", "No rumours wait for moderation." },
                { "AdminQueueLine", "#{0} by {1} ({2} ago): {3}" },
                { "AdminNoRumour", "No pending rumour #{0}." },
                { "AdminApproved", "Rumour #{0} approved{1}." },
                { "AdminApprovedNoChronicle", " (the chronicle did not take it: is RealmChronicle loaded and the 'rumour' type registered?)" },
                { "AdminRejected", "Rumour #{0} rejected." },
                { "AdminReportsEmpty", "No open reports." },
                { "AdminReportLine", "Report {0}: {1} reported letter #{2} from {3}{4} ({5} ago): \"{6}\" Reason: {7}" },
                { "AdminNoReport", "No report {0}." },
                { "AdminResolved", "Report {0} marked handled." },
                { "AdminAuditEmpty", "No audit entries for {0} in the last {1}h." },
                { "AdminAuditLine", "{0} ago [{1}] {2}{3}{4}" },
                { "AdminLetterNone", "No record of letter #{0} (records are kept {1} days{2})." },
                { "AdminLetterTextOff", ", and letter text is not stored" },
                { "AdminLetter", "Letter #{0} from {1} ({2}){3} to {4}, {5} ago: \"{6}\"" },
                { "AdminMuted", "{0} may not send ravens or rumours for {1}." },
                { "AdminUnmuted", "{0} may use the rookery again." },
                { "AdminPurged", "Removed {0} raven(s) in the air from {1}." },
                { "AdminSaved", "RealmRavens data saved." },
                { "AdminUsage", "Usage: /raven admin queue | approve <id> | reject <id> [reason] | reports | resolve <id> | audit <player> [hours] | letter <#> | mute <player> <hours> | unmute <player> | purge <player> | save" },
                { "AdminLoginQueue", "[C8A050]Ravens[FFFFFF]: {0} rumour(s) wait for moderation, {1} open report(s)." }
            }, this);
        }

        #endregion

        #region /raven

        [ChatCommand("raven")]
        private void CmdRaven(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            string id = player.Id.ToString();
            if (loadFailed || data == null) { player.SendError(Msg("Closed", id)); return; }
            PlayerInfo me = Info(id, player.Name);
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "help": ShowHelp(player); return;
                case "send":
                    if (args.Length < 3) { Error(player, "SendUsage"); return; }
                    Send(player, me, args[1], JoinFrom(args, 2), false); return;
                case "anon":
                    if (args.Length < 3) { Error(player, "SendUsage"); return; }
                    Send(player, me, args[1], JoinFrom(args, 2), true); return;
                case "inbox": ShowInbox(player); return;
                case "read": ReadLetter(player, args); return;
                case "delete": DeleteLetters(player, args); return;
                case "sent": ShowSent(player, me); return;
                case "status": ShowStatus(player, me); return;
                case "block": Block(player, me, args); return;
                case "unblock": Unblock(player, me, args); return;
                case "blocks": ShowBlocks(player, me); return;
                case "report": ReportLetter(player, args); return;
                case "spymaster": SetSpymaster(player, args); return;
                case "watch": SetWatch(player, args); return;
                case "spy": CmdSpy(player, me, args); return;
                case "admin": CmdAdmin(player, args); return;
            }
            if (args.Length < 2) { Error(player, "SendUsage"); return; }
            Send(player, me, args[0], JoinFrom(args, 1), false);
        }

        private void ShowHelp(Player player)
        {
            string id = player.Id.ToString();
            player.SendMessage(Msg("Help1", id, config.TravelSecondsMin, config.TravelSecondsMax));
            player.SendMessage(Msg("Help2", id));
            player.SendMessage(Msg("Help3", id));
            if (config.InterceptionEnabled) player.SendMessage(Msg("Help4", id));
            if (IsAdmin(id)) player.SendMessage(Msg("HelpAdmin", id));
        }

        private void Send(Player player, PlayerInfo me, string targetText, string rawText, bool anonymous)
        {
            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            if (anonymous && !config.AllowAnonymous) { Error(player, "AnonDisabled"); return; }
            if (me.MutedUntil > now) { Error(player, "Muted", Rules.Duration(me.MutedUntil - now)); return; }
            if (anonymous && me.FirstSeen > now.AddMinutes(-config.NewPlayerGraceMinutes))
            {
                Error(player, "TooNew", Rules.Duration(me.FirstSeen.AddMinutes(config.NewPlayerGraceMinutes) - now));
                return;
            }
            string text = Rules.Sanitize(rawText);
            if (text.Length < config.MinLetterLength) { Error(player, "TooShort"); return; }
            if (text.Length > config.MaxLetterLength) { Error(player, "TooLong", text.Length, config.MaxLetterLength); return; }

            string kind, label, targetKey, targetHouse;
            List<string> recipients;
            if (!ResolveTarget(player, targetText, out kind, out label, out targetKey, out targetHouse, out recipients)) return;

            TimeSpan wait;
            string why = Rules.CheckSendLimits(me, now, targetKey, anonymous, Rules.CountInFlight(data.InFlight, id, null), config, out wait);
            if (why != null) { Error(player, why, Rules.Duration(wait)); return; }

            if (kind == KindPlayer)
            {
                List<InboxLetter> box;
                data.Inboxes.TryGetValue(recipients[0], out box);
                if (Rules.CountUnreadFrom(box, id) + Rules.CountInFlight(data.InFlight, id, recipients[0]) >= config.MaxUnreadFromSameSender)
                {
                    Error(player, "RoostFull", label);
                    return;
                }
            }

            string myHouse = HouseOf(id);
            var letter = new Letter
            {
                Id = data.NextLetterId++,
                SenderId = id,
                SenderName = player.Name,
                SenderHouse = myHouse,
                Anonymous = anonymous,
                TargetKind = kind,
                TargetLabel = label,
                TargetHouse = targetHouse,
                Recipients = recipients,
                Text = text,
                SentAt = now,
                ArriveAt = now.AddSeconds(Rules.TravelSeconds(config, anonymous, rng.NextDouble()))
            };
            data.InFlight.Add(letter);
            me.Sent.Add(new SendRecord
            {
                LetterId = letter.Id, At = now, ArriveAt = letter.ArriveAt,
                TargetKey = targetKey, TargetLabel = label, Anonymous = anonymous
            });
            if (myHouse != null) data.Traffic.Add(new TrafficRecord { At = now, FromHouse = myHouse, ToHouse = targetHouse });
            Audit(anonymous ? "letter_sent_anon" : "letter_sent", id, player.Name, recipients, letter.Id,
                "to " + label + (config.AuditStoreLetterText ? ": " + text : ""));
            dirty = true;
            Reply(player, anonymous ? "SentAnon" : "Sent", label, Rules.Duration(letter.ArriveAt - now), letter.Id);
        }

        // Resolves "h:Name", "p:Name" or a bare name (house first, then player). Reports its own errors.
        private bool ResolveTarget(Player player, string text, out string kind, out string label, out string targetKey,
            out string targetHouse, out List<string> recipients)
        {
            string id = player.Id.ToString();
            kind = null; label = null; targetKey = null; targetHouse = null; recipients = null;
            string t = (text ?? "").Trim();
            bool forceHouse = false, forcePlayer = false;
            if (t.StartsWith("h:", StringComparison.OrdinalIgnoreCase)) { forceHouse = true; t = t.Substring(2).Trim(); }
            else if (t.StartsWith("p:", StringComparison.OrdinalIgnoreCase)) { forcePlayer = true; t = t.Substring(2).Trim(); }
            if (t.Length == 0) { Error(player, "SendUsage"); return false; }

            if (!forcePlayer)
            {
                string house = FindHouseName(t);
                if (house != null)
                {
                    List<string> members = HouseRecipients(house);
                    members.Remove(id);
                    if (members.Count == 0) { Error(player, "NoRecipients", house); return false; }
                    kind = KindHouse;
                    label = Msg("ToHouse", id, house);
                    targetKey = "house:" + Rules.Key(house);
                    targetHouse = house;
                    recipients = members;
                    return true;
                }
                if (forceHouse)
                {
                    if (!HousesLoaded()) Error(player, "HousesMissing");
                    else Error(player, "NoSuchHouse", t);
                    return false;
                }
            }

            string target = FindPlayerId(player, t);
            if (target == null) return false;
            if (target == id) { Error(player, "NoSelf"); return false; }
            kind = KindPlayer;
            label = NameOf(target);
            targetKey = "player:" + target;
            targetHouse = HouseOf(target);
            recipients = new List<string> { target };
            return true;
        }

        private List<string> HouseRecipients(string house)
        {
            var list = new List<string>();
            if (config.HouseLettersToAllMembers)
            {
                List<string> members = HouseMembers(house);
                if (members != null) foreach (string m in members) if (!list.Contains(m)) list.Add(m);
            }
            else
            {
                string leader = HouseLeader(house);
                if (leader != null) list.Add(leader);
            }
            if (list.Count > config.MaxHouseRecipients) list.RemoveRange(config.MaxHouseRecipients, list.Count - config.MaxHouseRecipients);
            return list;
        }

        // Online players first (exact name, then unique partial), then every player the rookery has seen.
        private string FindPlayerId(Player asker, string text)
        {
            string askerId = asker.Id.ToString();
            var online = new Dictionary<string, string>();
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer) online[p.Id.ToString()] = p.Name;
            List<string> hits = Rules.MatchNames(online, text);
            if (hits.Count == 0)
            {
                var known = new Dictionary<string, string>();
                foreach (KeyValuePair<string, PlayerInfo> kv in data.Players) known[kv.Key] = kv.Value.Name;
                hits = Rules.MatchNames(known, text);
            }
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) { Error(asker, "NoSuchPlayer", text); return null; }
            var names = new List<string>();
            for (int i = 0; i < hits.Count && i < 5; i++) names.Add(NameOf(hits[i]));
            Error(asker, "Ambiguous", text, string.Join(", ", names.ToArray()));
            return null;
        }

        private void Deliver(Letter l, DateTime now)
        {
            string interceptor = null;
            if (config.InterceptionEnabled && HousesLoaded())
                interceptor = Rules.PickInterceptor(l, data.Houses, HouseOf, now, config, rng.NextDouble);

            bool tampered = false;
            if (interceptor != null)
            {
                HouseIntel h = data.Houses[interceptor];
                h.Intercepts.Add(now);
                string from = l.Anonymous
                    ? (config.RevealAnonHouseOnIntercept && l.SenderHouse != null ? Msg("AnonFromHouse", h.SpymasterId, l.SenderHouse) : Msg("AnonFrom", h.SpymasterId))
                    : SignedFrom(h.SpymasterId, l);
                var copy = new InboxLetter
                {
                    LetterId = l.Id, SenderId = l.SenderId, SenderHouse = l.SenderHouse, From = from, Anonymous = l.Anonymous,
                    Text = l.Text, SentAt = l.SentAt, ArrivedAt = now, Intercepted = true, InterceptedTo = l.TargetLabel
                };
                Rules.AddToInbox(Inbox(h.SpymasterId), copy, config.MaxInboxSize);
                var involved = new List<string>(l.Recipients);
                involved.Add(l.SenderId);
                Audit("letter_intercepted", h.SpymasterId, h.SpymasterName, involved, l.Id,
                    "by the spymaster of House " + h.House + " (watching " + h.Watch + ")" + (config.InterceptedLettersStillDelivered ? "" : "; the letter is lost"));
                Player sm = Online(h.SpymasterId);
                if (sm != null) sm.SendMessage(Msg("ArrivedIntercepted", h.SpymasterId, from, l.TargetLabel, l.Id));
                tampered = rng.NextDouble() < config.TamperNoticeChance;
                if (!config.InterceptedLettersStillDelivered) return;
            }

            foreach (string rid in l.Recipients)
            {
                PlayerInfo r = Info(rid, null);
                if (Rules.IsBlocked(r, l.SenderId, l.SenderHouse, l.Anonymous))
                {
                    Audit("letter_blocked", l.SenderId, l.SenderName, new List<string> { rid }, l.Id, "dropped: the recipient blocks the sender");
                    continue;
                }
                List<InboxLetter> box = Inbox(rid);
                if (l.TargetKind == KindHouse && Rules.CountUnreadFrom(box, l.SenderId) >= config.MaxUnreadFromSameSender)
                {
                    Audit("letter_dropped", l.SenderId, l.SenderName, new List<string> { rid }, l.Id, "dropped: too many unread letters from the sender");
                    continue;
                }
                string from = l.Anonymous ? Msg("AnonFrom", rid) : SignedFrom(rid, l);
                Rules.AddToInbox(box, new InboxLetter
                {
                    LetterId = l.Id, SenderId = l.SenderId, SenderHouse = l.SenderHouse, From = from, Anonymous = l.Anonymous,
                    Text = l.Text, SentAt = l.SentAt, ArrivedAt = now, Tampered = tampered
                }, config.MaxInboxSize);
                Player p = Online(rid);
                if (p != null) p.SendMessage(Msg("Arrived", rid, from, l.Id));
            }
        }

        private string SignedFrom(string readerId, Letter l)
        {
            return l.SenderHouse != null ? Msg("SignedFromHouse", readerId, l.SenderName, l.SenderHouse) : Msg("SignedFrom", readerId, l.SenderName);
        }

        private void ShowInbox(Player player)
        {
            string id = player.Id.ToString();
            List<InboxLetter> box;
            if (!data.Inboxes.TryGetValue(id, out box) || box.Count == 0) { Reply(player, "InboxEmpty"); return; }
            int unread = 0;
            foreach (InboxLetter i in box) if (!i.Read) unread++;
            Reply(player, "InboxHeader", box.Count, unread);
            var sorted = new List<InboxLetter>(box);
            sorted.Sort(delegate(InboxLetter a, InboxLetter b) { return b.ArrivedAt.CompareTo(a.ArrivedAt); });
            DateTime now = DateTime.UtcNow;
            int shown = 0;
            foreach (InboxLetter i in sorted)
            {
                if (shown++ >= config.InboxListLines) break;
                string tags = (i.Read ? "" : Msg("InboxUnreadTag", id)) + (i.Intercepted ? Msg("InboxInterceptTag", id) : "");
                Reply(player, "InboxLine", i.LetterId, tags, i.From, Rules.Duration(now - i.ArrivedAt), Rules.Preview(i.Text, 40));
            }
            if (sorted.Count > config.InboxListLines) Reply(player, "InboxMore", sorted.Count - config.InboxListLines);
        }

        private void ReadLetter(Player player, string[] args)
        {
            string id = player.Id.ToString();
            int num;
            if (args.Length < 2 || !int.TryParse(args[1].TrimStart('#'), out num)) { Error(player, "ReadUsage"); return; }
            List<InboxLetter> box;
            data.Inboxes.TryGetValue(id, out box);
            InboxLetter l = Rules.FindInInbox(box, num);
            if (l == null) { Error(player, "NoSuchLetter", num); return; }
            TimeSpan ago = DateTime.UtcNow - l.SentAt;
            if (l.Intercepted) Reply(player, "ReadIntercepted", l.LetterId, l.From, l.InterceptedTo, Rules.Duration(ago));
            else Reply(player, "ReadHeader", l.LetterId, l.From, Rules.Duration(ago));
            Reply(player, "ReadBody", l.Text);
            if (l.Tampered) Reply(player, "ReadTampered");
            if (!l.Read) { l.Read = true; dirty = true; }
        }

        private void DeleteLetters(Player player, string[] args)
        {
            string id = player.Id.ToString();
            if (args.Length < 2) { Error(player, "DeleteUsage"); return; }
            List<InboxLetter> box;
            if (!data.Inboxes.TryGetValue(id, out box)) { Reply(player, "Deleted", 0); return; }
            string what = args[1].ToLowerInvariant();
            int removed;
            if (what == "all") { removed = box.Count; box.Clear(); }
            else if (what == "read") removed = box.RemoveAll(delegate(InboxLetter i) { return i.Read; });
            else
            {
                int num;
                if (!int.TryParse(what.TrimStart('#'), out num)) { Error(player, "DeleteUsage"); return; }
                removed = box.RemoveAll(delegate(InboxLetter i) { return i.LetterId == num; });
                if (removed == 0) { Error(player, "NoSuchLetter", num); return; }
            }
            if (box.Count == 0) data.Inboxes.Remove(id);
            dirty = true;
            Reply(player, "Deleted", removed);
        }

        private void ShowSent(Player player, PlayerInfo me)
        {
            DateTime now = DateTime.UtcNow;
            Rules.PruneSent(me, now);
            if (me.Sent.Count == 0) { Reply(player, "SentEmpty"); return; }
            string id = player.Id.ToString();
            Reply(player, "SentHeader");
            int start = Math.Max(0, me.Sent.Count - config.InboxListLines);
            for (int i = start; i < me.Sent.Count; i++)
            {
                SendRecord s = me.Sent[i];
                string state = s.ArriveAt > now ? Msg("SentInFlight", id, Rules.Duration(s.ArriveAt - now)) : Msg("SentFlown", id);
                Reply(player, "SentLine", s.LetterId, s.TargetLabel, s.Anonymous ? Msg("SentAnonTag", id) : "", state);
            }
        }

        private void ShowStatus(Player player, PlayerInfo me)
        {
            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            Rules.PruneSent(me, now);
            int hour = 0, day = 0, anon = 0;
            foreach (SendRecord s in me.Sent)
            {
                if (s.At <= now.AddDays(-1)) continue;
                day++;
                if (s.At > now.AddHours(-1)) hour++;
                if (s.Anonymous) anon++;
            }
            Reply(player, "Status", Math.Max(0, config.MaxLettersPerHour - hour), Math.Max(0, config.MaxLettersPerDay - day),
                config.AllowAnonymous ? Math.Max(0, config.MaxAnonymousPerDay - anon) : 0,
                Rules.CountInFlight(data.InFlight, id, null), config.MaxInFlightPerSender);
            if (me.MutedUntil > now) Reply(player, "Muted", Rules.Duration(me.MutedUntil - now));
            string house = HouseOf(id);
            HouseIntel h = house != null ? Intel(house, false) : null;
            if (h == null) return;
            if (h.SpymasterId == id) Reply(player, "StatusRole", Msg("RoleSpymaster", id, h.House, h.Watch ?? Msg("Nothing", id)));
            else if (h.Spies.Contains(id)) Reply(player, "StatusRole", Msg("RoleSpy", id, h.House));
        }

        #endregion

        #region Blocks and reports

        private void Block(Player player, PlayerInfo me, string[] args)
        {
            string id = player.Id.ToString();
            if (args.Length < 2) { Error(player, "BlockUsage"); return; }
            string t = JoinFrom(args, 1).Trim();
            if (t.ToLowerInvariant() == "anon")
            {
                me.BlockAnonymous = true;
                dirty = true;
                Audit("block", id, player.Name, null, 0, "all unsigned letters");
                Reply(player, "BlockedAnon");
                return;
            }
            if (me.BlockedPlayers.Count + me.BlockedHouses.Count >= config.MaxBlocks) { Error(player, "TooManyBlocks", config.MaxBlocks); return; }

            int num;
            if (t.StartsWith("#") && int.TryParse(t.Substring(1), out num))
            {
                List<InboxLetter> box;
                data.Inboxes.TryGetValue(id, out box);
                InboxLetter l = Rules.FindInInbox(box, num);
                if (l == null) { Error(player, "NoSuchLetter", num); return; }
                if (l.Intercepted) { Error(player, "ReportCannot"); return; }
                if (!me.BlockedPlayers.Contains(l.SenderId))
                {
                    me.BlockedPlayers.Add(l.SenderId);
                    if (l.Anonymous) me.HiddenBlocks.Add(l.SenderId);   // the blocker never learns who it was
                }
                RemoveInFlightTo(id, l.SenderId);
                dirty = true;
                Audit("block", id, player.Name, new List<string> { l.SenderId }, num, "sender of letter #" + num);
                Reply(player, "BlockedLetterSender", num);
                return;
            }

            bool forcePlayer = t.StartsWith("p:", StringComparison.OrdinalIgnoreCase);
            bool forceHouse = t.StartsWith("h:", StringComparison.OrdinalIgnoreCase);
            if (forcePlayer || forceHouse) t = t.Substring(2).Trim();
            string house = forcePlayer ? null : FindHouseName(t);
            if (house != null)
            {
                string key = Rules.Key(house);
                if (me.BlockedHouses.Contains(key)) { Error(player, "AlreadyBlocked", house); return; }
                me.BlockedHouses.Add(key);
                dirty = true;
                Audit("block", id, player.Name, null, 0, "House " + house);
                Reply(player, "Blocked", Msg("ToHouse", id, house));
                return;
            }
            if (forceHouse) { Error(player, HousesLoaded() ? "NoSuchHouse" : "HousesMissing", t); return; }
            string target = FindPlayerId(player, t);
            if (target == null) return;
            if (target == id) { Error(player, "NoSelf"); return; }
            if (me.BlockedPlayers.Contains(target))
            {
                // Saying "already blocked" for someone blocked through an unsigned letter would unmask them.
                if (!me.HiddenBlocks.Remove(target)) { Error(player, "AlreadyBlocked", NameOf(target)); return; }
                dirty = true;
                Reply(player, "Blocked", NameOf(target));
                return;
            }
            me.BlockedPlayers.Add(target);
            RemoveInFlightTo(id, target);
            dirty = true;
            Audit("block", id, player.Name, new List<string> { target }, 0, "player " + NameOf(target));
            Reply(player, "Blocked", NameOf(target));
        }

        // A block also stops ravens already in the air from that sender to the blocker (they are dropped on arrival
        // anyway; removing them early frees the sender's in-flight slots only if no one else is a recipient).
        private void RemoveInFlightTo(string recipientId, string senderId)
        {
            foreach (Letter l in data.InFlight)
                if (l.SenderId == senderId) l.Recipients.Remove(recipientId);
            data.InFlight.RemoveAll(delegate(Letter l) { return l.SenderId == senderId && l.Recipients.Count == 0; });
        }

        private void Unblock(Player player, PlayerInfo me, string[] args)
        {
            string id = player.Id.ToString();
            if (args.Length < 2) { Error(player, "BlockUsage"); return; }
            string t = JoinFrom(args, 1).Trim();
            if (t.ToLowerInvariant() == "anon")
            {
                me.BlockAnonymous = false;
                dirty = true;
                Reply(player, "UnblockedAnon");
                return;
            }
            if (t.ToLowerInvariant() == "hidden")
            {
                foreach (string h in me.HiddenBlocks) me.BlockedPlayers.Remove(h);
                int n = me.HiddenBlocks.Count;
                me.HiddenBlocks.Clear();
                dirty = true;
                Reply(player, "UnblockedHidden", n);
                return;
            }
            bool forcePlayer = t.StartsWith("p:", StringComparison.OrdinalIgnoreCase);
            bool forceHouse = t.StartsWith("h:", StringComparison.OrdinalIgnoreCase);
            if (forcePlayer || forceHouse) t = t.Substring(2).Trim();
            if (!forcePlayer && me.BlockedHouses.Remove(Rules.Key(t)))
            {
                dirty = true;
                Reply(player, "Unblocked", Msg("ToHouse", id, t));
                return;
            }
            if (forceHouse) { Error(player, "NotBlocked", t); return; }
            var blocked = new Dictionary<string, string>();
            foreach (string b in me.BlockedPlayers) if (!me.HiddenBlocks.Contains(b)) blocked[b] = NameOf(b);
            List<string> hits = Rules.MatchNames(blocked, t);
            if (hits.Count != 1) { Error(player, "NotBlocked", t); return; }
            me.BlockedPlayers.Remove(hits[0]);
            dirty = true;
            Reply(player, "Unblocked", NameOf(hits[0]));
        }

        private void ShowBlocks(Player player, PlayerInfo me)
        {
            string id = player.Id.ToString();
            if (me.BlockedPlayers.Count == 0 && me.BlockedHouses.Count == 0 && !me.BlockAnonymous) { Reply(player, "BlocksEmpty"); return; }
            var names = new List<string>();
            foreach (string b in me.BlockedPlayers) if (!me.HiddenBlocks.Contains(b)) names.Add(NameOf(b));
            if (me.HiddenBlocks.Count > 0) names.Add(Msg("HiddenBlocks", id, me.HiddenBlocks.Count));
            string none = Msg("Nothing", id);
            Reply(player, "BlocksLine", names.Count > 0 ? string.Join(", ", names.ToArray()) : none,
                me.BlockedHouses.Count > 0 ? string.Join(", ", me.BlockedHouses.ToArray()) : none,
                Msg(me.BlockAnonymous ? "Yes" : "No", id));
        }

        private void ReportLetter(Player player, string[] args)
        {
            string id = player.Id.ToString();
            int num;
            if (args.Length < 2 || !int.TryParse(args[1].TrimStart('#'), out num)) { Error(player, "ReportUsage"); return; }
            List<InboxLetter> box;
            data.Inboxes.TryGetValue(id, out box);
            InboxLetter l = Rules.FindInInbox(box, num);
            if (l == null) { Error(player, "NoSuchLetter", num); return; }
            if (l.Intercepted) { Error(player, "ReportCannot"); return; }
            foreach (Report existing in data.Reports)
                if (existing.ReporterId == id && existing.LetterId == num) { Reply(player, "Reported", num); return; }
            string reason = args.Length > 2 ? Rules.Preview(Rules.Sanitize(JoinFrom(args, 2)), 120) : "";
            var rep = new Report
            {
                Id = data.NextReportId++, At = DateTime.UtcNow, ReporterId = id, ReporterName = player.Name,
                LetterId = num, SenderId = l.SenderId, SenderName = NameOf(l.SenderId), Anonymous = l.Anonymous,
                Text = l.Text, Reason = reason
            };
            data.Reports.Add(rep);
            if (data.Reports.Count > 500) data.Reports.RemoveAt(0);
            Audit("letter_reported", id, player.Name, new List<string> { l.SenderId }, num, reason);
            dirty = true;
            Reply(player, "Reported", num);
            if (config.NotifyAdminsOfReports) NotifyAdmins("AdminReportNotice", rep.Id);
        }

        #endregion

        #region Spymasters and spies

        private bool RequireOwnHouse(Player player, out string house, out HouseIntel intel)
        {
            house = null; intel = null;
            if (!HousesLoaded()) { Error(player, "HousesMissing"); return false; }
            house = HouseOf(player.Id.ToString());
            if (house == null) { Error(player, "NoHouse"); return false; }
            intel = Intel(house, true);
            return true;
        }

        private void SetSpymaster(Player player, string[] args)
        {
            string id = player.Id.ToString();
            string house; HouseIntel h;
            if (!RequireOwnHouse(player, out house, out h)) return;
            if (HouseLeader(house) != id) { Error(player, "NotLeader"); return; }
            if (args.Length < 2) { Error(player, "SpymasterUsage"); return; }
            DateTime now = DateTime.UtcNow;
            if (h.SpymasterSetAt > now.AddHours(-config.SpymasterChangeCooldownHours) && !IsAdmin(id))
            {
                Error(player, "SpymasterCooldown", Rules.Duration(h.SpymasterSetAt.AddHours(config.SpymasterChangeCooldownHours) - now));
                return;
            }
            string t = JoinFrom(args, 1).Trim();
            if (t.ToLowerInvariant() == "none")
            {
                h.SpymasterId = null; h.SpymasterName = null; h.Watch = null; h.SpymasterSetAt = now;
                dirty = true;
                Audit("spymaster_set", id, player.Name, null, 0, "House " + house + ": none");
                Reply(player, "SpymasterCleared", house);
                return;
            }
            string target = FindMemberId(player, house, t);
            if (target == null) return;
            h.SpymasterId = target;
            h.SpymasterName = NameOf(target);
            h.SpymasterSetAt = now;
            h.Watch = null;
            h.WatchSetAt = DateTime.MinValue;
            h.Spies.Remove(target);
            dirty = true;
            Audit("spymaster_set", id, player.Name, new List<string> { target }, 0, "House " + house + ": " + h.SpymasterName);
            Reply(player, "SpymasterSet", h.SpymasterName, house);
            if (target != id) Notify(target, Msg("SpymasterYou", target, house));
        }

        private string FindMemberId(Player player, string house, string text)
        {
            List<string> members = HouseMembers(house);
            var names = new Dictionary<string, string>();
            if (members != null) foreach (string m in members) names[m] = NameOf(m);
            List<string> hits = Rules.MatchNames(names, text);
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) { Error(player, "SpymasterNotMember", text); return null; }
            var list = new List<string>();
            for (int i = 0; i < hits.Count && i < 5; i++) list.Add(NameOf(hits[i]));
            Error(player, "Ambiguous", text, string.Join(", ", list.ToArray()));
            return null;
        }

        private void SetWatch(Player player, string[] args)
        {
            string id = player.Id.ToString();
            string house; HouseIntel h;
            if (!RequireOwnHouse(player, out house, out h)) return;
            if (h.SpymasterId != id) { Error(player, "NotSpymaster"); return; }
            if (args.Length < 2) { Error(player, "WatchUsage"); return; }
            DateTime now = DateTime.UtcNow;
            if (h.WatchSetAt > now.AddMinutes(-config.WatchChangeCooldownMinutes))
            {
                Error(player, "WatchCooldown", Rules.Duration(h.WatchSetAt.AddMinutes(config.WatchChangeCooldownMinutes) - now));
                return;
            }
            string t = JoinFrom(args, 1).Trim();
            if (t.ToLowerInvariant() == "none")
            {
                h.Watch = null; h.WatchSetAt = now;
                dirty = true;
                Reply(player, "WatchCleared");
                return;
            }
            string target = FindHouseName(t);
            if (target == null) { Error(player, "NoSuchHouse", t); return; }
            if (Rules.Key(target) == Rules.Key(house)) { Error(player, "WatchOwn"); return; }
            h.Watch = target;
            h.WatchSetAt = now;
            dirty = true;
            Audit("watch_set", id, player.Name, null, 0, "House " + house + " watches House " + target);
            Reply(player, "WatchSet", target);
            if (!config.InterceptionEnabled) Reply(player, "InterceptOff");
        }

        private void CmdSpy(Player player, PlayerInfo me, string[] args)
        {
            string id = player.Id.ToString();
            if (!config.SpiesEnabled) { Error(player, "SpiesDisabled"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "accept" || sub == "decline") { AnswerSpyOffer(player, sub == "accept"); return; }
            string house; HouseIntel h;
            if (!RequireOwnHouse(player, out house, out h)) return;
            bool leader = HouseLeader(house) == id;
            switch (sub)
            {
                case "swear":
                {
                    if (!leader) { Error(player, "NotLeader"); return; }
                    if (args.Length < 3) { Error(player, "SpyUsage"); return; }
                    string target = FindMemberId(player, house, JoinFrom(args, 2));
                    if (target == null) return;
                    if (h.Spies.Contains(target) || h.SpymasterId == target) { Error(player, "SpyAlready", NameOf(target)); return; }
                    if (h.Spies.Count >= config.MaxSpiesPerHouse) { Error(player, "SpyTooMany", config.MaxSpiesPerHouse); return; }
                    if (target == id) { AcceptSpy(player, house); return; }
                    if (Online(target) == null) { Error(player, "SpyNotOnline", NameOf(target)); return; }
                    spyOffers[target] = new SpyOffer { House = house, ById = id, Expires = DateTime.UtcNow.AddSeconds(config.SpyOfferExpireSeconds) };
                    Reply(player, "SpyOffered", NameOf(target), Rules.Duration(TimeSpan.FromSeconds(config.SpyOfferExpireSeconds)));
                    Notify(target, Msg("SpyOfferReceived", target, house));
                    return;
                }
                case "dismiss":
                {
                    if (!leader) { Error(player, "NotLeader"); return; }
                    if (args.Length < 3) { Error(player, "SpyUsage"); return; }
                    var spies = new Dictionary<string, string>();
                    foreach (string s in h.Spies) spies[s] = NameOf(s);
                    List<string> hits = Rules.MatchNames(spies, JoinFrom(args, 2));
                    if (hits.Count != 1) { Error(player, "SpyNotSpy", JoinFrom(args, 2)); return; }
                    h.Spies.Remove(hits[0]);
                    dirty = true;
                    Audit("spy_dismissed", id, player.Name, hits, 0, "House " + house);
                    Reply(player, "SpyDismissed", NameOf(hits[0]));
                    return;
                }
                case "list":
                {
                    if (!leader && h.SpymasterId != id && !h.Spies.Contains(id)) { Error(player, "NotSpy"); return; }
                    var names = new List<string>();
                    foreach (string s in h.Spies) names.Add(NameOf(s));
                    string none = Msg("Nothing", id);
                    Reply(player, "SpyListHeader", house, h.SpymasterId != null ? NameOf(h.SpymasterId) : none,
                        names.Count > 0 ? string.Join(", ", names.ToArray()) : none, h.Watch ?? none);
                    return;
                }
                case "report":
                    if (args.Length < 3) { Error(player, "SpyUsage"); return; }
                    SpyReport(player, me, house, h, JoinFrom(args, 2));
                    return;
            }
            Error(player, "SpyUsage");
        }

        private void AnswerSpyOffer(Player player, bool accept)
        {
            string id = player.Id.ToString();
            SpyOffer offer;
            if (!spyOffers.TryGetValue(id, out offer) || offer.Expires < DateTime.UtcNow) { spyOffers.Remove(id); Error(player, "SpyNoOffer"); return; }
            spyOffers.Remove(id);
            if (!accept) { Reply(player, "SpyDeclined"); return; }
            if (Rules.Key(HouseOf(id)) != Rules.Key(offer.House)) { Error(player, "SpyNoOffer"); return; }
            AcceptSpy(player, offer.House);
            Notify(offer.ById, Msg("SpySwornLeader", offer.ById, player.Name));
        }

        private void AcceptSpy(Player player, string house)
        {
            string id = player.Id.ToString();
            HouseIntel h = Intel(house, true);
            if (h.Spies.Count >= config.MaxSpiesPerHouse) { Error(player, "SpyTooMany", config.MaxSpiesPerHouse); return; }
            if (!h.Spies.Contains(id)) h.Spies.Add(id);
            dirty = true;
            Audit("spy_sworn", id, player.Name, null, 0, "House " + house);
            Reply(player, "SpySworn", house);
        }

        private void SpyReport(Player player, PlayerInfo me, string house, HouseIntel h, string targetText)
        {
            string id = player.Id.ToString();
            if (h.SpymasterId != id && !h.Spies.Contains(id)) { Error(player, "NotSpy"); return; }
            string target = FindHouseName(targetText);
            if (target == null) { Error(player, "NoSuchHouse", targetText); return; }
            string tkey = Rules.Key(target);
            if (tkey == Rules.Key(house)) { Error(player, "SpyOwn"); return; }
            DateTime now = DateTime.UtcNow;
            TimeSpan wait;
            string why = Rules.CheckSpy(me, h, tkey, now, config, out wait);
            if (why != null) { Error(player, why, Rules.Duration(wait)); return; }

            me.LastSpyAt = now;
            h.SpiedTargets[tkey] = now;
            dirty = true;
            string none = Msg("Nothing", id);

            var treaties = new List<string>();
            foreach (string other in AllHouseNames())
                if (Rules.Key(other) != tkey && HasTreaty(target, other)) treaties.Add(other);
            string liege = HousesCallString("GetLiege", target);
            List<string> vassals = HousesCallList("GetVassals", target);

            int total;
            Dictionary<string, int> by = Rules.TrafficFrom(data.Traffic, target, now.AddHours(-config.TrafficWindowHours), out total);
            var dests = new List<KeyValuePair<string, int>>(by);
            dests.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
            var destText = new List<string>();
            for (int i = 0; i < dests.Count && i < 5; i++)
                destText.Add((dests[i].Key.Length > 0 ? dests[i].Key : Msg("Unhoused", id)) + " x" + dests[i].Value);

            HouseIntel theirs = Intel(target, false);
            string service;
            if (theirs == null || theirs.SpymasterId == null) service = Msg("SpyServiceNone", id) + (theirs != null && theirs.Spies.Count > 0 ? ", " + theirs.Spies.Count + " sworn spies" : "");
            else if (theirs.Watch != null) service = Msg("SpyServiceWatch", id, theirs.Watch, theirs.Spies.Count);
            else service = Msg("SpyServiceIdle", id, theirs.Spies.Count);

            Reply(player, "SpyReportHeader", target);
            Reply(player, "SpyReportTreaties", treaties.Count > 0 ? string.Join(", ", treaties.ToArray()) : none);
            Reply(player, "SpyReportFealty", liege ?? none, vassals != null && vassals.Count > 0 ? string.Join(", ", vassals.ToArray()) : none);
            Reply(player, "SpyReportTraffic", config.TrafficWindowHours, total, destText.Count > 0 ? Msg("SpyReportTrafficTo", id, string.Join(", ", destText.ToArray())) : "");
            Reply(player, "SpyReportService", service);

            bool caught = rng.NextDouble() < config.SpyCaughtChance;
            Audit(caught ? "spy_caught" : "spy_report", id, player.Name, null, 0, "House " + house + " spied on House " + target);
            if (!caught) return;
            Reply(player, "SpyCaughtSelf", target);
            var tell = new List<string>();
            string leaderId = HouseLeader(target);
            if (leaderId != null) tell.Add(leaderId);
            if (theirs != null && theirs.SpymasterId != null && !tell.Contains(theirs.SpymasterId)) tell.Add(theirs.SpymasterId);
            foreach (string t in tell)
            {
                string who = config.RevealSpyNameWhenCaught ? Msg("SpyCaughtWhoNamed", t, player.Name, house) : Msg("SpyCaughtWho", t, house);
                Notify(t, Msg("SpyCaughtTarget", t, target, who));
            }
        }

        #endregion

        #region /rumour

        [ChatCommand("rumour")]
        private void CmdRumour(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            string id = player.Id.ToString();
            if (loadFailed || data == null) { player.SendError(Msg("Closed", id)); return; }
            if (!config.RumoursEnabled) { Error(player, "RumoursDisabled"); return; }
            if (args.Length == 0 || args[0].ToLowerInvariant() == "help") { Reply(player, "RumourUsage"); return; }
            if (args.Length == 1 && args[0].ToLowerInvariant() == "list") { ListRumours(player); return; }

            PlayerInfo me = Info(id, player.Name);
            DateTime now = DateTime.UtcNow;
            string text = Rules.Sanitize(JoinFrom(args, 0));
            if (text.Length < config.MinRumourLength) { Error(player, "RumourTooShort"); return; }
            if (text.Length > config.MaxRumourLength) { Error(player, "RumourTooLong", config.MaxRumourLength, text.Length); return; }
            if (Rules.PendingRumours(data.Rumours, id) >= config.MaxPendingRumoursPerPlayer) { Error(player, "RumourMine"); return; }
            TimeSpan wait;
            string why = Rules.CheckRumour(me, data.Rumours, now, config, out wait);
            if (why != null) { Error(player, why, Rules.Duration(wait)); return; }
            me.LastRumourAt = now;
            dirty = true;
            if (Rules.ContainsBlockedWord(text, config.RumourBlockedWords))
            {
                Audit("rumour_refused", id, player.Name, null, 0, "blocked word: " + text);
                Error(player, "RumourRefused");
                return;
            }
            var r = new Rumour { Id = data.NextRumourId++, SubmitterId = id, SubmitterName = player.Name, Text = text, SubmittedAt = now, Status = RPending };
            data.Rumours.Add(r);
            Audit("rumour_submitted", id, player.Name, null, 0, "#" + r.Id + ": " + text);
            Reply(player, "RumourQueued", r.Id);
            NotifyAdmins("AdminNewRumour", r.Id);
        }

        [ChatCommand("rumor")]
        private void CmdRumor(Player player, string command, string[] args)
        {
            CmdRumour(player, command, args);
        }

        private void ListRumours(Player player)
        {
            var approved = new List<Rumour>();
            foreach (Rumour r in data.Rumours) if (r.Status == RApproved) approved.Add(r);
            if (approved.Count == 0) { Reply(player, "RumourListEmpty"); return; }
            Reply(player, "RumourListHeader");
            DateTime now = DateTime.UtcNow;
            for (int i = approved.Count - 1, n = 0; i >= 0 && n < config.RumourListCount; i--, n++)
                Reply(player, "RumourListLine", Rules.Duration(now - approved[i].DecidedAt), approved[i].Text);
        }

        #endregion

        #region Admin

        private void CmdAdmin(Player player, string[] args)
        {
            string id = player.Id.ToString();
            if (!IsAdmin(id)) { Error(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            DateTime now = DateTime.UtcNow;
            int n;
            switch (sub)
            {
                case "queue":
                {
                    int shown = 0;
                    foreach (Rumour r in data.Rumours)
                    {
                        if (r.Status != RPending) continue;
                        if (shown++ >= config.AdminListLines) break;
                        Reply(player, "AdminQueueLine", r.Id, r.SubmitterName, Rules.Duration(now - r.SubmittedAt), r.Text);
                    }
                    if (shown == 0) Reply(player, "AdminQueueEmpty");
                    return;
                }
                case "approve":
                case "reject":
                {
                    if (args.Length < 3 || !int.TryParse(args[2].TrimStart('#'), out n)) { Error(player, "AdminUsage"); return; }
                    Rumour r = null;
                    foreach (Rumour x in data.Rumours) if (x.Id == n && x.Status == RPending) r = x;
                    if (r == null) { Error(player, "AdminNoRumour", n); return; }
                    r.DecidedAt = now;
                    r.DecidedBy = player.Name;
                    dirty = true;
                    if (sub == "reject")
                    {
                        r.Status = RRejected;
                        r.Reason = args.Length > 3 ? Rules.Preview(Rules.Sanitize(JoinFrom(args, 3)), 120) : null;
                        Audit("rumour_rejected", id, player.Name, r.SubmitterId != null ? new List<string> { r.SubmitterId } : null, 0, "#" + r.Id + (r.Reason != null ? ": " + r.Reason : ""));
                        if (r.SubmitterId != null) Notify(r.SubmitterId, Msg("RumourRejected", r.SubmitterId, r.Id, r.Reason != null ? " (" + r.Reason + ")" : ""));
                        Reply(player, "AdminRejected", r.Id);
                        return;
                    }
                    r.Status = RApproved;
                    r.ChronicleId = ChronicleRumour(r.Text);
                    Audit("rumour_approved", id, player.Name, r.SubmitterId != null ? new List<string> { r.SubmitterId } : null, r.ChronicleId, "#" + r.Id);
                    if (config.BroadcastApprovedRumours) Server.BroadcastMessage(Msg("RumourBroadcast", null, r.Text));
                    if (r.SubmitterId != null) Notify(r.SubmitterId, Msg("RumourApproved", r.SubmitterId, r.Id));
                    Reply(player, "AdminApproved", r.Id, config.ChronicleRumours && r.ChronicleId <= 0 ? Msg("AdminApprovedNoChronicle", id) : "");
                    return;
                }
                case "reports":
                {
                    int shown = 0;
                    foreach (Report r in data.Reports)
                    {
                        if (r.Handled) continue;
                        if (shown++ >= config.AdminListLines) break;
                        Reply(player, "AdminReportLine", r.Id, r.ReporterName, r.LetterId, r.SenderName, r.Anonymous ? Msg("SentAnonTag", id) : "",
                            Rules.Duration(now - r.At), r.Text, string.IsNullOrEmpty(r.Reason) ? "-" : r.Reason);
                    }
                    if (shown == 0) Reply(player, "AdminReportsEmpty");
                    Audit("admin_viewed_reports", id, player.Name, null, 0, null);
                    return;
                }
                case "resolve":
                {
                    if (args.Length < 3 || !int.TryParse(args[2], out n)) { Error(player, "AdminUsage"); return; }
                    Report rep = null;
                    foreach (Report r in data.Reports) if (r.Id == n) rep = r;
                    if (rep == null) { Error(player, "AdminNoReport", n); return; }
                    rep.Handled = true;
                    dirty = true;
                    Audit("report_resolved", id, player.Name, null, rep.LetterId, "report " + n);
                    Reply(player, "AdminResolved", n);
                    return;
                }
                case "audit":
                {
                    if (args.Length < 3) { Error(player, "AdminUsage"); return; }
                    string target = FindPlayerId(player, args[2]);
                    if (target == null) return;
                    int hours = 24;
                    if (args.Length > 3 && int.TryParse(args[3], out n)) hours = Rules.Clamp(n, 1, config.AuditRetentionDays * 24);
                    var hits = new List<AuditEntry>();
                    foreach (AuditEntry a in data.Audit)
                        if (a.At > now.AddHours(-hours) && (a.ActorId == target || a.Targets.Contains(target))) hits.Add(a);
                    if (hits.Count == 0) { Reply(player, "AdminAuditEmpty", NameOf(target), hours); return; }
                    for (int i = Math.Max(0, hits.Count - config.AdminListLines); i < hits.Count; i++)
                    {
                        AuditEntry a = hits[i];
                        Reply(player, "AdminAuditLine", Rules.Duration(now - a.At), a.Kind, a.ActorName ?? "?",
                            a.LetterId > 0 ? " #" + a.LetterId : "", string.IsNullOrEmpty(a.Detail) ? "" : " " + a.Detail);
                    }
                    Audit("admin_viewed_audit", id, player.Name, new List<string> { target }, 0, hours + "h");
                    return;
                }
                case "letter":
                {
                    if (args.Length < 3 || !int.TryParse(args[2].TrimStart('#'), out n)) { Error(player, "AdminUsage"); return; }
                    AuditEntry sent = null;
                    foreach (AuditEntry a in data.Audit)
                        if (a.LetterId == n && (a.Kind == "letter_sent" || a.Kind == "letter_sent_anon")) sent = a;
                    if (sent == null) { Reply(player, "AdminLetterNone", n, config.AuditRetentionDays, config.AuditStoreLetterText ? "" : Msg("AdminLetterTextOff", id)); return; }
                    Reply(player, "AdminLetter", n, sent.ActorName, sent.ActorId, sent.Kind == "letter_sent_anon" ? Msg("SentAnonTag", id) : "",
                        Names(sent.Targets), Rules.Duration(now - sent.At), sent.Detail ?? "");
                    Audit("admin_read_letter", id, player.Name, null, n, null);
                    return;
                }
                case "mute":
                {
                    if (args.Length < 4 || !int.TryParse(args[3], out n)) { Error(player, "AdminUsage"); return; }
                    string target = FindPlayerId(player, args[2]);
                    if (target == null) return;
                    n = Rules.Clamp(n, 1, 24 * 365);
                    Info(target, null).MutedUntil = now.AddHours(n);
                    dirty = true;
                    Audit("admin_mute", id, player.Name, new List<string> { target }, 0, n + "h");
                    Reply(player, "AdminMuted", NameOf(target), Rules.Duration(TimeSpan.FromHours(n)));
                    return;
                }
                case "unmute":
                {
                    if (args.Length < 3) { Error(player, "AdminUsage"); return; }
                    string target = FindPlayerId(player, args[2]);
                    if (target == null) return;
                    Info(target, null).MutedUntil = DateTime.MinValue;
                    dirty = true;
                    Audit("admin_unmute", id, player.Name, new List<string> { target }, 0, null);
                    Reply(player, "AdminUnmuted", NameOf(target));
                    return;
                }
                case "purge":
                {
                    if (args.Length < 3) { Error(player, "AdminUsage"); return; }
                    string target = FindPlayerId(player, args[2]);
                    if (target == null) return;
                    int removed = data.InFlight.RemoveAll(delegate(Letter l) { return l.SenderId == target; });
                    dirty = true;
                    Audit("admin_purge", id, player.Name, new List<string> { target }, 0, removed + " in flight");
                    Reply(player, "AdminPurged", removed, NameOf(target));
                    return;
                }
                case "save":
                    SaveData();
                    Reply(player, "AdminSaved");
                    return;
            }
            Error(player, "AdminUsage");
        }

        private int ChronicleRumour(string text)
        {
            if (!config.ChronicleRumours || RealmChronicle == null) return 0;
            string[] titles = { "Whispers in the taverns", "A rumour spreads", "Overheard at the Hearth", "Talk on the hill road" };
            string title = titles[rng.Next(titles.Length)];
            object res = RealmChronicle.Call("Log", "rumour", title, text, new string[0]);
            return res is int ? (int)res : 0;
        }

        #endregion

        #region RealmHouses bridge

        private bool HousesLoaded()
        {
            return RealmHouses != null;
        }

        private string HouseOf(string playerId)
        {
            if (RealmHouses == null || playerId == null) return null;
            return RealmHouses.Call("GetHouse", playerId) as string;
        }

        private List<string> HouseMembers(string house)
        {
            return HousesCallList("GetMembers", house);
        }

        private string HouseLeader(string house)
        {
            return HousesCallString("GetHouseLeader", house);
        }

        private bool HasTreaty(string a, string b)
        {
            if (RealmHouses == null) return false;
            object res = RealmHouses.Call("HasTreaty", a, b);
            return res is bool && (bool)res;
        }

        private string HousesCallString(string method, string house)
        {
            if (RealmHouses == null || house == null) return null;
            return RealmHouses.Call(method, house) as string;
        }

        private List<string> HousesCallList(string method, string house)
        {
            if (RealmHouses == null || house == null) return null;
            return RealmHouses.Call(method, house) as List<string>;
        }

        private List<string> AllHouseNames()
        {
            var names = new List<string>();
            if (RealmHouses == null) return names;
            var list = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
            if (list == null) return names;
            foreach (Dictionary<string, object> h in list)
            {
                object n;
                if (h != null && h.TryGetValue("name", out n) && n is string) names.Add((string)n);
            }
            return names;
        }

        // Case-insensitive exact match against the houses RealmHouses knows. Returns the canonical name or null.
        private string FindHouseName(string text)
        {
            string k = Rules.Key(text);
            if (k.Length == 0) return null;
            foreach (string n in AllHouseNames()) if (Rules.Key(n) == k) return n;
            return null;
        }

        #endregion

        #region Helpers

        private HouseIntel Intel(string house, bool create)
        {
            string k = Rules.Key(house);
            HouseIntel h;
            if (data.Houses.TryGetValue(k, out h)) { h.House = house; return h; }
            if (!create) return null;
            h = new HouseIntel { House = house };
            data.Houses[k] = h;
            return h;
        }

        private PlayerInfo Info(string id, string name)
        {
            PlayerInfo p;
            if (!data.Players.TryGetValue(id, out p))
            {
                p = new PlayerInfo { Name = name ?? id, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
                data.Players[id] = p;
                dirty = true;
            }
            else if (name != null && p.Name != name) { p.Name = name; dirty = true; }
            return p;
        }

        private List<InboxLetter> Inbox(string id)
        {
            List<InboxLetter> box;
            if (!data.Inboxes.TryGetValue(id, out box)) { box = new List<InboxLetter>(); data.Inboxes[id] = box; }
            return box;
        }

        private string NameOf(string id)
        {
            if (id == null) return "?";
            PlayerInfo p;
            if (data.Players.TryGetValue(id, out p) && !string.IsNullOrEmpty(p.Name)) return p.Name;
            Player online = Online(id);
            return online != null ? online.Name : id;
        }

        private string Names(List<string> ids)
        {
            var names = new List<string>();
            if (ids != null) foreach (string i in ids) { if (names.Count >= 8) { names.Add("..."); break; } names.Add(NameOf(i)); }
            return string.Join(", ", names.ToArray());
        }

        private static Player Online(string id)
        {
            ulong uid;
            if (id == null || !ulong.TryParse(id, out uid)) return null;
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && p.Id == uid) return p;
            return null;
        }

        private bool IsAdmin(string id)
        {
            return permission.UserHasPermission(id, PermAdmin);
        }

        // Sends now if online, otherwise keeps it for login (at most 10 per player).
        private void Notify(string id, string text)
        {
            Player p = Online(id);
            if (p != null) { p.SendMessage(text); return; }
            PlayerInfo info = Info(id, null);
            info.Notices.Add(text);
            if (info.Notices.Count > 10) info.Notices.RemoveAt(0);
            dirty = true;
        }

        private void NotifyAdmins(string key, params object[] args)
        {
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                string id = p.Id.ToString();
                if (IsAdmin(id)) p.SendMessage(Msg(key, id, args));
            }
        }

        private void Audit(string kind, string actorId, string actorName, List<string> targets, int letterId, string detail)
        {
            if (data == null) return;
            data.Audit.Add(new AuditEntry
            {
                Id = data.NextAuditId++, At = DateTime.UtcNow, Kind = kind, ActorId = actorId, ActorName = actorName,
                Targets = targets != null ? new List<string>(targets) : new List<string>(), LetterId = letterId, Detail = detail
            });
            if (data.Audit.Count > config.MaxAuditEntries) data.Audit.RemoveRange(0, data.Audit.Count - config.MaxAuditEntries);
            dirty = true;
        }

        private static string JoinFrom(string[] args, int start)
        {
            if (start >= args.Length) return "";
            var parts = new string[args.Length - start];
            Array.Copy(args, start, parts, 0, parts.Length);
            return string.Join(" ", parts);
        }

        private string Msg(string key, string playerId, params object[] args)
        {
            string text = lang.GetMessage(key, this, playerId);
            return args != null && args.Length > 0 ? string.Format(text, args) : text;
        }

        private void Reply(Player player, string key, params object[] args)
        {
            player.SendMessage(Msg(key, player.Id.ToString(), args));
        }

        private void Error(Player player, string key, params object[] args)
        {
            player.SendError(Msg(key, player.Id.ToString(), args));
        }

        #endregion
    }
}
