// RealmDominion: territorial war for the realm of Ostreval.
//
// Named holdings lie across the map: villages, keeps, mines, the harbour and the crossroads. Each is a circle on the
// ground (centre and radius set in game by a steward standing on the spot) that a house takes by holding the field
// with its own sworn members during the War Hours.
//
//   War Hours   capture windows in realm time (Windows, UtcOffsetMinutes or the crown's UtcOffsetHours). Outside them no
//               holding can change hands. Inside them, captures freeze ("paused") while RealmEvents' Truce of the
//               Realm holds (IsTruceActive), while CrownAndConsequences has a rebellion under way (IsRebellionActive;
//               DuringRebellion "pause", the default, keeps the crown's war undivided; "open" opens every holding
//               instead) and, with RequireRaidHours, while RealmWarden's raid hours are shut (IsRaidHourNow).
//               Stewards may open or close the field by hand (/dominion admin open|close).
//   Capture     every TickSeconds the online, living players inside each holding are counted by house. Only eligible
//               players count (see Anti-abuse). Only the owning house's own members defend; only one attacking house
//               leads; houses allied to the owner (treaty, liege, vassal, the same liege) are neutral and count for no
//               one, so allies cannot camp a holding shut or hand it to each other. Defenders and attackers in the
//               field together, or two hostile attackers, contest it: the banner does not move. A lone attacking
//               house raises its banner (Progress 0 to 100) at a rate that grows with its numbers (ExtraCapturerPercent
//               each, up to MaxCountedPerHouse) and shrinks with the holder's garrison. A rival banner must first be
//               torn down to 0. Defenders alone tear an attacker's banner down; an empty field lets it fall slowly.
//               At 100 the holding is taken: the herald tells the realm, the Chronicle records it (holding_taken), the
//               house earns season points (RealmSeasons.AwardHouse), each capturer present a renown deed
//               (RealmRenown.AddDeed, RenownDeed), and the holding is secure for SecureMinutes.
//   Garrison    a holding's garrison level rises by one for each daily payday its holder keeps it (to MaxGarrisonLevel).
//               Each level slows a capture by GarrisonPercentPerLevel and adds IncomeGarrisonPercentPerLevel to the
//               income. While the field is open, members of the holding house standing inside it take
//               GarrisonDamageReductionPercent less damage from hostile players (OnEntityHealthChange, Damage.Amount).
//   Income      once a day at IncomeTime (realm time) every holding pays its holder: IncomeByKind marks struck into the
//               house vault through RealmTreasury.GrantHouseIncome (counted in the treasury's mint and audit, within
//               its supply cap and its per-source daily budget), and PointsPerDayByKind season points.
//   Board       RealmPainter's live board "dominion" (/paint dominion) draws the holdings from GetDominionBoard();
//               this plugin asks RealmPainter.RefreshBoards("dominion") whenever an owner or a banner changes.
//   Map file    oxide/data/RealmDominionMap.json, written for the Chronicle service, the portal and the bot
//               (schema in plugins/docs/RealmDominion.md). Read only for them; this plugin never reads it back.
//
// Anti-abuse (each rule has a config switch and a regression test in tools/exploit-review/dominion):
//   alt houses      a house counts only with MinHouseMembers members, MinHouseAgeHours after its founding
//                   (RealmHouses.GetHouseFounded) and while it holds fewer than MaxHoldingsPerHouse holdings. A player
//                   counts only MinMembershipHours after this plugin first saw them in that house (house hopping), never
//                   while RealmWarden's new-player protection covers them, never as staff (AdminsCount false).
//   ally camping    allied houses are neutral in the field (above); a holder's ally cannot take it either.
//   flip farming    rewards (season points, renown) for a capture are withheld when the same two houses traded a
//                   holding within PairCooldownHours, when the capturer held that holding within PairCooldownHours,
//                   and after MaxRewardedCapturesPerDay rewarded captures by one house in 24 h.
//   truces          no progress during the Truce of the Realm, a paused rebellion or outside the raid hours.
//   log-off holding only online, living players count; a player who logs in inside a holding counts only after
//                   ReconnectGraceSeconds; a holding pays only if a member of its house was online within the last day
//                   (IncomeRequiresActivity), and returns to no one after AbandonAfterDays without one. A holding whose
//                   house is disbanded (or refounded under the same name) returns to no one.
//
// API (non-public on purpose, Plugin.Call): GetHoldings() -> List<Dictionary<string, object>>, GetHoldingOwner(string id)
// -> string, GetHouseHoldingCount(string house) -> int, GetDominionWindow() -> string, GetDominionBoard() -> Dictionary.
//
// Calls out (all optional; a missing plugin or method reads as "not available"):
//   RealmHouses.GetHouse(string id), GetMembers(string house), GetHouseFounded(string house), GetLiege(string house),
//     HasTreaty(string a, string b)
//   CrownAndConsequences.IsRebellionActive(), GetUtcOffsetHours()   RealmEvents.IsTruceActive()
//   RealmWarden.IsRaidHourNow(), IsNewPlayerProtected(ulong)        RealmChronicle.Log(type, title, detail, actors)
//   RealmSeasons.AwardHouse(house, points, honour)                   RealmRenown.AddDeed(id, name, kind, note, key)
//   RealmTreasury.GrantHouseIncome(house, marks, source, note)       RealmPainter.RefreshBoards(kind)
//   RealmHerald.PopupsWanted(string id)
//
// Game API used (names confirmed in the 2.0.3867 Assembly-CSharp.dll metadata; docs/oxide-rok-api.md):
//   Server.ClientPlayers, Server.BroadcastMessage(string), Server.GetPlayerById(ulong); Player.Id, Name, IsServer,
//   Entity.Position (UnityEngine.Vector3 x, y, z); PlayerExtensions.SendMessage(string), SendError(string), IsAlive(),
//   ShowPopup(...); EntityDamageEvent.Entity, Damage.Amount, Damage.DamageSource (Entity.Owner, Entity.IsPlayer).
//   The game knows nothing of holdings: no marker, flag or map icon can be shown on a client. Holdings live in chat,
//   in popups, on painted signs and on the portal.
//
// Data: oxide/data/RealmDominion.json. If it exists but cannot be read (damaged, truncated, empty) the plugin does
// nothing and never writes it until it is fixed or moved away.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface.
// UNVERIFIED in game: everything at run time. plugins/docs/RealmDominion.md has the in-game test for each part.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CodeHatch.Common;                              // PlayerExtensions: SendMessage, SendError, IsAlive, ShowPopup [ASM]
using CodeHatch.Engine.Core.Cache;                   // Entity [ASM]
using CodeHatch.Engine.Networking;                   // Player, Server [ASM]
using CodeHatch.Networking.Events.Entities;          // EntityDamageEvent [ASM]
using Oxide.Core;                                    // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                            // Plugin (for [PluginReference]) [SRC]
using UnityEngine;                                   // Vector3 [ASM]

namespace Oxide.Plugins
{
    [Info("RealmDominion", "Realm", "0.1.0")]
    [Description("Territorial war: houses take and hold the named holdings of Ostreval in the War Hours")]
    public class RealmDominion : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmPainter;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmEvents;
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmdominion.admin";
        private const string DataName = "RealmDominion";
        private const string ChronicleType = "holding_taken";
        private const string BoardKind = "dominion";

        private const string WOpen = "open";
        private const string WPaused = "paused";
        private const string WClosed = "closed";

        private static readonly string[] Kinds = { "village", "keep", "mine", "harbour", "crossroads" };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private bool mapDirty = true;
        private Timer tickTimer;

        // Session state (never saved).
        private DateTime lastTick = DateTime.MinValue;
        private DateTime lastSave = DateTime.MinValue;
        private DateTime lastMap = DateTime.MinValue;
        private DateTime lastSlowCheck = DateTime.MinValue;
        private DateTime lastBoardRefresh = DateTime.MinValue;
        private bool boardWanted;
        private string windowState;                  // last seen: open | paused | closed (null before the first tick)
        private string windowReason;
        private readonly Dictionary<ulong, DateTime> joinedAt = new Dictionary<ulong, DateTime>();
        private readonly Dictionary<string, DateTime> throttle = new Dictionary<string, DateTime>();
        private readonly Dictionary<ulong, string> insideOf = new Dictionary<ulong, string>();   // player -> holding id
        private readonly Dictionary<ulong, string> houseCache = new Dictionary<ulong, string>(); // refreshed each tick
        private readonly Dictionary<string, bool> allyCache = new Dictionary<string, bool>();
        private readonly Dictionary<string, string> eligibleCache = new Dictionary<string, string>();  // house -> null or reason
        private readonly Dictionary<string, int> memberCountCache = new Dictionary<string, int>();
        private readonly Dictionary<string, string> confirm = new Dictionary<string, string>();          // admin remove confirm

        // Indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        #region Config

        private class WindowDef
        {
            public List<string> Days;                       // day names ("Saturday", "Sat") or "daily"
            public string Start = "20:00";                  // HH:mm in realm time
            public int DurationMinutes = 120;
        }

        private class PluginConfig
        {
            public bool Enabled = true;                     // false: nothing is counted, captured or paid
            public float TickSeconds = 5f;
            public List<WindowDef> Windows;
            public bool UseCrownUtcOffset = true;           // realm time = CrownAndConsequences' UtcOffsetHours when loaded
            public int UtcOffsetMinutes = 0;                // otherwise UTC + this
            public string DuringRebellion = "pause";        // pause | open | normal
            public bool PauseDuringTruce = true;
            public bool RequireRaidHours = true;            // inside a window, count only while RealmWarden allows raiding
            public int CaptureSeconds = 300;                // one capturer, unclaimed holding, 0 to 100
            public float ExtraCapturerPercent = 25f;        // faster per extra capturer of the leading house
            public int MaxCountedPerHouse = 5;
            public int MinCapturers = 1;
            public float DecayPerMinute = 10f;              // banner points lost per minute with the field empty
            public int DecayDelaySeconds = 60;
            public int SecureMinutes = 30;                  // a fresh capture cannot be taken again this soon
            public int GarrisonPercentPerLevel = 25;        // slower capture per garrison level
            public int MaxGarrisonLevel = 4;
            public bool GarrisonDamageReduction = true;
            public float GarrisonDamageReductionPercent = 10f;
            public float VerticalRange = 40f;               // metres above or below the centre still inside
            public int MinHouseMembers = 2;
            public int MinHouseAgeHours = 24;
            public int MinMembershipHours = 12;
            public int MaxHoldingsPerHouse = 3;
            public bool AllowCaptureFromAllies = false;
            public bool CountNewPlayerProtected = false;
            public bool AdminsCount = false;
            public int ReconnectGraceSeconds = 60;
            public int PairCooldownHours = 24;
            public int MaxRewardedCapturesPerDay = 3;
            public int AbandonAfterDays = 3;
            public bool IncomeRequiresActivity = true;
            public string IncomeTime = "21:00";             // realm time, once a day
            public int MinHeldHoursForIncome = 6;
            public Dictionary<string, long> IncomeByKind;
            public int IncomeGarrisonPercentPerLevel = 10;
            public Dictionary<string, int> PointsPerDayByKind;
            public int CapturePoints = 5;
            public string RenownDeed = "holding_taken";     // "" = no renown deed
            public bool AnnounceCaptures = true;
            public bool AnnounceWindows = true;
            public bool ShowEnterNotices = true;
            public int AnnounceCooldownSeconds = 120;
            public bool UsePopups = true;
            public bool PaintBoards = true;
            public bool PublishMap = true;
            public int MaxHoldings = 30;
            public float MinRadius = 10f;
            public float MaxRadius = 150f;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            // Inside RealmWarden's default raid hours, clear of the rebellion windows, Crown Night and the Sunday truce.
            c.Windows = new List<WindowDef>
            {
                new WindowDef { Days = new List<string> { "Wednesday" }, Start = "20:30", DurationMinutes = 120 },
                new WindowDef { Days = new List<string> { "Saturday" }, Start = "21:00", DurationMinutes = 120 },
                new WindowDef { Days = new List<string> { "Sunday" }, Start = "17:00", DurationMinutes = 120 }
            };
            c.IncomeByKind = new Dictionary<string, long>();
            c.IncomeByKind.Add("village", 100);
            c.IncomeByKind.Add("keep", 150);
            c.IncomeByKind.Add("mine", 200);
            c.IncomeByKind.Add("harbour", 180);
            c.IncomeByKind.Add("crossroads", 120);
            c.PointsPerDayByKind = new Dictionary<string, int>();
            c.PointsPerDayByKind.Add("village", 2);
            c.PointsPerDayByKind.Add("keep", 3);
            c.PointsPerDayByKind.Add("mine", 3);
            c.PointsPerDayByKind.Add("harbour", 3);
            c.PointsPerDayByKind.Add("crossroads", 2);
            return c;
        }

        private void ClampConfig()
        {
            if (config == null) config = DefaultConfig();
            PluginConfig d = DefaultConfig();
            if (config.Windows == null) config.Windows = d.Windows;
            config.Windows.RemoveAll(delegate(WindowDef w) { return w == null; });
            if (config.Windows.Count > 50) config.Windows.RemoveRange(50, config.Windows.Count - 50);
            foreach (WindowDef w in config.Windows)
            {
                if (w.Days == null) w.Days = new List<string>();
                int m;
                if (!ParseClock(w.Start, out m)) { PrintWarning("Window Start '" + w.Start + "' is not HH:mm; using 20:00."); w.Start = "20:00"; }
                w.DurationMinutes = Clamp(w.DurationMinutes, 1, 1440);
            }
            config.TickSeconds = ClampF(config.TickSeconds, 1f, 30f, 5f);
            config.UtcOffsetMinutes = Clamp(config.UtcOffsetMinutes, -720, 840);
            string r = (config.DuringRebellion ?? "").Trim().ToLowerInvariant();
            config.DuringRebellion = r == "open" || r == "normal" ? r : "pause";
            config.CaptureSeconds = Clamp(config.CaptureSeconds, 10, 86400);
            config.ExtraCapturerPercent = ClampF(config.ExtraCapturerPercent, 0f, 200f, 25f);
            config.MaxCountedPerHouse = Clamp(config.MaxCountedPerHouse, 1, 50);
            config.MinCapturers = Clamp(config.MinCapturers, 1, 50);
            config.DecayPerMinute = ClampF(config.DecayPerMinute, 0f, 100f, 10f);
            config.DecayDelaySeconds = Clamp(config.DecayDelaySeconds, 0, 3600);
            config.SecureMinutes = Clamp(config.SecureMinutes, 0, 1440);
            config.GarrisonPercentPerLevel = Clamp(config.GarrisonPercentPerLevel, 0, 200);
            config.MaxGarrisonLevel = Clamp(config.MaxGarrisonLevel, 0, 10);
            config.GarrisonDamageReductionPercent = ClampF(config.GarrisonDamageReductionPercent, 0f, 50f, 10f);
            config.VerticalRange = ClampF(config.VerticalRange, 2f, 500f, 40f);
            config.MinHouseMembers = Clamp(config.MinHouseMembers, 1, 100);
            config.MinHouseAgeHours = Clamp(config.MinHouseAgeHours, 0, 2160);
            config.MinMembershipHours = Clamp(config.MinMembershipHours, 0, 2160);
            config.MaxHoldingsPerHouse = Clamp(config.MaxHoldingsPerHouse, 1, 100);
            config.ReconnectGraceSeconds = Clamp(config.ReconnectGraceSeconds, 0, 3600);
            config.PairCooldownHours = Clamp(config.PairCooldownHours, 0, 720);
            config.MaxRewardedCapturesPerDay = Clamp(config.MaxRewardedCapturesPerDay, 0, 100);
            config.AbandonAfterDays = Clamp(config.AbandonAfterDays, 0, 365);
            int im;
            if (!ParseClock(config.IncomeTime, out im)) { PrintWarning("IncomeTime '" + config.IncomeTime + "' is not HH:mm; using 21:00."); config.IncomeTime = "21:00"; }
            config.MinHeldHoursForIncome = Clamp(config.MinHeldHoursForIncome, 0, 168);
            config.IncomeByKind = CleanKinds(config.IncomeByKind, d.IncomeByKind, 100000L);
            config.PointsPerDayByKind = CleanKinds(config.PointsPerDayByKind, d.PointsPerDayByKind, 1000);
            config.IncomeGarrisonPercentPerLevel = Clamp(config.IncomeGarrisonPercentPerLevel, 0, 100);
            config.CapturePoints = Clamp(config.CapturePoints, 0, 1000);
            if (config.RenownDeed == null) config.RenownDeed = "";
            config.AnnounceCooldownSeconds = Clamp(config.AnnounceCooldownSeconds, 10, 3600);
            config.MaxHoldings = Clamp(config.MaxHoldings, 1, 100);
            config.MinRadius = ClampF(config.MinRadius, 2f, 100f, 10f);
            config.MaxRadius = ClampF(config.MaxRadius, config.MinRadius, 500f, 150f);
        }

        private static Dictionary<string, long> CleanKinds(Dictionary<string, long> src, Dictionary<string, long> defaults, long max)
        {
            var o = new Dictionary<string, long>();
            foreach (string k in Kinds)
            {
                long v;
                if (src == null || !src.TryGetValue(k, out v)) v = defaults[k];
                o[k] = v < 0 ? 0 : v > max ? max : v;
            }
            return o;
        }

        private static Dictionary<string, int> CleanKinds(Dictionary<string, int> src, Dictionary<string, int> defaults, int max)
        {
            var o = new Dictionary<string, int>();
            foreach (string k in Kinds)
            {
                int v;
                if (src == null || !src.TryGetValue(k, out v)) v = defaults[k];
                o[k] = v < 0 ? 0 : v > max ? max : v;
            }
            return o;
        }

        #endregion

        #region Data

        private class Holding
        {
            public string Id;
            public string Name;
            public string Kind;
            public string Place;                        // lore line: where it lies in Ostreval
            public float X;
            public float Y;
            public float Z;
            public float Radius = 40f;
            public bool Placed;                         // false until a steward stands there and sets it
            public bool Enabled = true;
            public string Owner;                        // house name or null
            public string OwnerFounded;                 // RealmHouses founding date of the owning house (lineage)
            public DateTime OwnerSince;
            public int Garrison;                        // paydays held in a row, capped at MaxGarrisonLevel
            public string Capturer;                     // house whose banner is rising, or null
            public double Progress;                     // 0..100
            public DateTime LastPresence;               // last time the capturer had men in the field
            public DateTime SecureUntil;
            public string State = "quiet";              // quiet | capturing | contested | defending | secured | capped
            public int Captures;
            public List<string> History = new List<string>();   // "iso|house|from", newest last, at most 10
        }

        private class Seen
        {
            public string House;
            public DateTime Since;
        }

        private class CaptureRecord
        {
            public DateTime At;
            public string Holding;
            public string House;
            public string From;
            public bool Rewarded;
        }

        private class StoredData
        {
            public int Version = 1;
            public DateTime InstalledAt;
            public List<Holding> Holdings;
            public Dictionary<string, Seen> Members = new Dictionary<string, Seen>();            // player id -> house seen
            public Dictionary<string, DateTime> HouseActive = new Dictionary<string, DateTime>(); // house -> member last online
            public List<CaptureRecord> CaptureLog = new List<CaptureRecord>();
            public Dictionary<string, long> IncomePaid = new Dictionary<string, long>();           // house -> marks, all time
            public string LastIncomeDay;                // realm date (yyyy-MM-dd) of the last payday
            public DateTime ForcedOpenUntil;
            public DateTime ForcedClosedUntil;
            public long Seq;
        }

        // The places of docs/saga/locations.md. Unplaced until a steward stands on the spot: /dominion admin move <id>.
        private static List<Holding> SeedHoldings()
        {
            var list = new List<Holding>();
            list.Add(Seed("tollbridge", "The Tollbridge", "crossroads", "The Builders' bridge over the great river, where every monarch claims the toll.", 35f));
            list.Add(Seed("greywatch", "Greywatch Keep", "keep", "A roofless watch-keep on the high road between Varrow's Stair and the Vale.", 40f));
            list.Add(Seed("ember-mines", "The Ember Mines", "mine", "Old ore-cuts in the walls of the Ember Pass, worked by Halloran's sellswords.", 45f));
            list.Add(Seed("drowned-harbour", "The Drowned Harbour", "harbour", "The quays of flooded Dunmere, where boats tie up to drowned rooftops.", 45f));
            list.Add(Seed("ferrymans-rest", "Ferryman's Rest", "village", "A ferrymen's village below Merrin's Ford that trades with every side.", 40f));
            list.Add(Seed("wold-lodge", "The Huntsman's Lodge", "village", "The old lodge of the Builders' huntsmen at the edge of the Crown Wold.", 35f));
            list.Add(Seed("scarred-orchards", "The Scarred Orchards", "village", "The burned orchard-holds of Ashgrove Vale, black at the root.", 45f));
            return list;
        }

        private static Holding Seed(string id, string name, string kind, string place, float radius)
        {
            return new Holding { Id = id, Name = name, Kind = kind, Place = place, Radius = radius, History = new List<string>() };
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            lastSave = Now();
        }

        private void Normalize()
        {
            if (data.Holdings == null) data.Holdings = SeedHoldings();
            if (data.Members == null) data.Members = new Dictionary<string, Seen>();
            if (data.HouseActive == null) data.HouseActive = new Dictionary<string, DateTime>();
            if (data.CaptureLog == null) data.CaptureLog = new List<CaptureRecord>();
            if (data.IncomePaid == null) data.IncomePaid = new Dictionary<string, long>();
            if (data.InstalledAt == DateTime.MinValue) data.InstalledAt = Now();
            data.CaptureLog.RemoveAll(delegate(CaptureRecord c) { return c == null || c.House == null || c.Holding == null; });
            var seenIds = new HashSet<string>();
            data.Holdings.RemoveAll(delegate(Holding h)
            {
                if (h == null || string.IsNullOrEmpty(h.Id) || seenIds.Contains(h.Id.ToLowerInvariant())) return true;
                seenIds.Add(h.Id.ToLowerInvariant());
                return false;
            });
            foreach (Holding h in data.Holdings)
            {
                h.Id = Slug(h.Id);
                if (string.IsNullOrEmpty(h.Name)) h.Name = h.Id;
                if (Array.IndexOf(Kinds, h.Kind) < 0) h.Kind = "village";
                if (h.History == null) h.History = new List<string>();
                if (float.IsNaN(h.Radius) || h.Radius < config.MinRadius) h.Radius = config.MinRadius;
                if (h.Radius > config.MaxRadius) h.Radius = config.MaxRadius;
                if (double.IsNaN(h.Progress) || h.Progress < 0) h.Progress = 0;
                if (h.Progress > 100) h.Progress = 100;
                if (h.Progress == 0) h.Capturer = null;
                if (h.Garrison < 0) h.Garrison = 0;
                if (h.Garrison > config.MaxGarrisonLevel) h.Garrison = config.MaxGarrisonLevel;
                if (string.IsNullOrEmpty(h.Owner)) { h.Owner = null; h.OwnerFounded = null; }
                if (h.State == null) h.State = "quiet";
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

        // House names in their house colour (docs/realm-commands.md); other houses by a stable hash of the name.
        private static readonly string[] HouseTintNames = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };
        private static readonly string[] HouseTintColours = { "C58FC0", "E08A5C", "8FB0BF", "B8B85A", "EC8A3C", "6FBF85" };

        private static string HouseTint(string house)
        {
            if (string.IsNullOrEmpty(house)) return house;
            string name = Clean(house, 40);
            string key = name.Trim().ToLowerInvariant();
            int i = Array.IndexOf(HouseTintNames, key);
            if (i < 0)
            {
                uint h = 2166136261;
                foreach (char c in key) { h ^= c; h *= 16777619; }
                i = (int)(h % (uint)HouseTintColours.Length);
            }
            return "[" + HouseTintColours[i] + "]" + name + "[FFFFFF]";
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Dominion" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Paused", "Dominion is paused: oxide/data/RealmDominion.json could not be read. Staff have been told in the server log." },
                { "Off", "Dominion is switched off on this server." },
                { "NoPermission", "You may not do that." },
                { "MapHeader", "The holdings of Ostreval ({0}):" },
                { "MapLine", "  {0} ({1}) - {2}" },
                { "MapFooter", "  [F4C96D]/dominion[FFFFFF] <holding> for one holding, [F4C96D]/dominion here[FFFFFF] for where you stand, [F4C96D]/dominion rules[FFFFFF] for how war is made." },
                { "MapEmpty", "No holdings have been marked yet." },
                { "OwnerLine", "House {0}, garrison {1}, {2} {3} a day" },
                { "Unclaimed", "unclaimed, {0} {1} a day" },
                { "Unplaced", "not yet marked on the land" },
                { "Disabled", "closed by the stewards" },
                { "StateCapturing", " - [E8913A]banner of {0} rising, {1}%[FFFFFF]" },
                { "StateContested", " - [E8913A]contested[FFFFFF]" },
                { "StateSecured", " - secure for {0} min" },
                { "WindowOpen", "The War Hours are on: holdings may be taken until {0} (realm time)." },
                { "WindowOpenRebellion", "A rebellion is under way: every holding may be taken while it lasts." },
                { "WindowForced", "The stewards have opened the field until {0} (realm time)." },
                { "WindowPausedTruce", "The War Hours are on, but the Truce of the Realm holds: no holding changes hands." },
                { "WindowPausedRebellion", "The War Hours are on, but the realm's eyes are on the rebellion: holdings wait." },
                { "WindowPausedRaid", "The War Hours are on, but the raid hours are shut: holdings wait." },
                { "WindowClosed", "No holding can be taken now. The next War Hours: {0} (realm time)." },
                { "WindowClosedNone", "No holding can be taken now, and no War Hours are set." },
                { "WindowClosedStewards", "The stewards have closed the field until {0} (realm time)." },
                { "WindowOff", "Dominion is switched off: no holding can be taken." },
                { "InfoTitle", "{0} ({1})" },
                { "InfoPlace", "  {0}" },
                { "InfoOwner", "  Held by House {0} since {1} UTC. Garrison {2} of {3}: captures take {4}% longer." },
                { "InfoUnclaimed", "  Held by no house. The first house to raise its banner here takes it." },
                { "InfoIncome", "  Pays {0} {1} and {2} season points a day at {3} (realm time)." },
                { "InfoWhere", "  Centre {0}, {1}; {2} m across. You are {3} m from it." },
                { "InfoWhereNoYou", "  Centre {0}, {1}; {2} m across." },
                { "InfoUnplaced", "  Not yet marked on the land: the stewards will set it before the War Hours." },
                { "InfoBanner", "  The banner of House {0} is rising here: {1}%." },
                { "InfoSecure", "  Secure for {0} more minutes after its capture." },
                { "InfoHistory", "  Last taken: {0}" },
                { "NotFound", "No holding is called '{0}'. [F4C96D]/dominion[FFFFFF] lists them." },
                { "HereNone", "You stand in no holding." },
                { "HereIn", "You stand in {0}. {1}" },
                { "HereCount", "  In the field now: {0}." },
                { "HereNobody", "nobody who counts" },
                { "HereYou", "  You: {0}" },
                { "YouCount", "you count for House {0}" },
                { "YouNoHouse", "you belong to no house, so you count for no one" },
                { "YouNew", "your house is too young or too small to make war yet ({0})" },
                { "YouHop", "you joined House {0} too lately; you count in {1} h" },
                { "YouProtected", "you are under new-player protection, so you count for no one" },
                { "YouStaff", "staff count for no one" },
                { "YouGrace", "you just arrived; you count in {0} s" },
                { "Rules1", "Houses take holdings in the War Hours by standing in them: only the holder's own members defend, and one attacking house leads." },
                { "Rules2", "  Allies of the holder count for no one. Defenders and attackers together, or two hostile attackers, freeze the banner." },
                { "Rules3", "  A house needs {0} members and {1} h since its founding; a member counts {2} h after joining. A house holds at most {3}." },
                { "Rules4", "  Each payday held raises the garrison: captures take longer, income rises, and defenders inside take {0}% less harm from foes." },
                { "Rules5", "  No banner moves in a truce, a rebellion or outside the raid hours. Sleepers never count. An idle house loses its holdings in {0} days." },
                { "Enter", "You enter {0}, {1}." },
                { "EnterHeld", "a holding of House {0}" },
                { "EnterFree", "a holding of no house" },
                { "FieldCapturing", "{0}: the banner of House {1} is at {2}%. Hold the ground." },
                { "FieldContested", "{0} is contested: no banner moves while foes stand here together." },
                { "FieldCapped", "{0}: House {1} already holds {2} holdings and can take no more." },
                { "FieldSecured", "{0} was taken lately and is secure for {1} more minutes." },
                { "FieldDefending", "{0}: the defenders tear down the banner of House {1} ({2}%)." },
                { "UnderAttack", "Your holding {0} is under attack by House {1}: their banner is at {2}%." },
                { "HeraldBanner", "House {0} raises its banner at {1}, held by {2}." },
                { "HeraldTaken", "House {0} takes {1}{2}!" },
                { "HeraldTakenFrom", " from House {0}" },
                { "HeraldAbandoned", "{0} lies abandoned: House {1} has not been seen there in {2} days." },
                { "HeraldFallen", "{0} is held by no one: House {1} is no more." },
                { "HeraldOpen", "The War Hours begin: the holdings of Ostreval may be taken until {0} (realm time). [F4C96D]/dominion[FFFFFF]" },
                { "HeraldClose", "The War Hours end. Unfinished banners fall; the holdings stay with those who hold them." },
                { "HeraldPaused", "The War Hours pause: {0}." },
                { "HeraldResumed", "The War Hours resume." },
                { "ReasonTruce", "the Truce of the Realm holds" },
                { "ReasonRebellion", "the realm's eyes are on the rebellion" },
                { "ReasonRaid", "the raid hours are shut" },
                { "HeraldPayday", "The holdings pay their dues: {0}." },
                { "PaydayPart", "House {0} {1} {2}" },
                { "YourPayday", "{0} paid House {1} {2} {3} into the vault and {4} season points." },
                { "YourPaydayNoVault", "{0} earned House {1} {2} season points. The vault was not paid ({3})." },
                { "YourPaydayIdle", "{0} paid nothing today: no one of House {1} was seen in the realm." },
                { "NoTreasury", "the treasury is not running" },
                { "TreasuryRefused", "the treasury refused: its daily budget or supply cap is spent" },
                { "PopupTitle", "The Holdings of Ostreval" },
                { "PopupButton", "Close" },
                { "Admin1", "  [F4C96D]/dominion admin[FFFFFF] status | create <id> <kind> [radius] <name> | move <id> [radius] | radius <id> <m> | rename <id> <name>" },
                { "Admin2", "  [F4C96D]/dominion admin[FFFFFF] remove <id> confirm | enable <id> | disable <id> | owner <id> <house or none> | reset <id>" },
                { "Admin3", "  [F4C96D]/dominion admin[FFFFFF] open [minutes] | close [minutes] | auto | payday | kinds: village, keep, mine, harbour, crossroads" },
                { "AdminStatus", "Field: {0} ({1}). Holdings: {2}, marked {3}, held {4}. Last payday: {5}. Treasury: {6}. Houses: {7}." },
                { "AdminStatus2", "  Realm time now {0}. Next War Hours: {1}. Payday at {2}." },
                { "Loaded", "loaded" },
                { "Missing", "missing" },
                { "Created", "{0} is marked here, {1} m across." },
                { "Moved", "{0} now lies here, {1} m across." },
                { "Radius", "{0} is now {1} m across." },
                { "Renamed", "{0} is now called {1}." },
                { "RemoveAsk", "This forgets {0} and its owner. Type [F4C96D]/dominion admin[FFFFFF] remove {1} confirm" },
                { "Removed", "{0} is gone from the map." },
                { "Enabled", "{0} is open to war again." },
                { "DisabledDone", "{0} is closed to war; it pays no one until it is opened." },
                { "OwnerSet", "{0} now belongs to {1}." },
                { "NoOne", "no one" },
                { "ResetDone", "{0}: banners cleared." },
                { "ForcedOpen", "The field is open for {0} minutes." },
                { "ForcedClosed", "The field is closed for {0} minutes." },
                { "AutoDone", "The field follows the War Hours again." },
                { "PaydayDone", "Payday made for {0} holdings." },
                { "PaydayAlready", "Today's payday was made already ({0})." },
                { "BadId", "An id is 2 to 24 letters, digits or dashes; '{0}' is not." },
                { "IdTaken", "A holding called '{0}' exists already." },
                { "BadKind", "'{0}' is not a kind: village, keep, mine, harbour or crossroads." },
                { "BadRadius", "A holding is {0} to {1} m across." },
                { "BadNumber", "'{0}' is not a number from {1} to {2}." },
                { "TooMany", "The map holds at most {0} holdings." },
                { "NoPosition", "Your position cannot be read just now." },
                { "NoSuchHouse", "No house is called '{0}'." },
                { "NeedName", "Give the holding a name." },
                { "BoardTitle", "Dominion" },
                { "BoardKicker", "The holdings of Ostreval" },
                { "BoardEmpty", "No holding has been marked yet." },
                { "BoardFooter", "Make war with [F4C96D]/dominion[FFFFFF]" },
                { "BoardFree", "Unclaimed" },
                { "BoardRising", "{0} {1}%" },
                { "BoardContested", "Contested" },
                { "Kind.village", "village" },
                { "Kind.keep", "keep" },
                { "Kind.mine", "mine" },
                { "Kind.harbour", "harbour" },
                { "Kind.crossroads", "crossroads" },
                { "Never", "never" },
                { "None", "none" }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        private string Fmt(string key, Player player, params object[] args)
        {
            string m = Msg(key, player);
            return args.Length > 0 ? string.Format(m, args) : m;
        }

        private void Reply(Player player, string key, params object[] args)
        {
            player.SendMessage(Styled(Msg("Speaker", player), ToneOf(key), Fmt(key, player, args)));   // single-string overload: brace safe
        }

        private void ReplyText(Player player, string tone, string text)
        {
            player.SendMessage(Styled(Msg("Speaker", player), tone, text));
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Created", "Moved", "Radius", "Renamed", "Removed", "Enabled", "OwnerSet", "ResetDone", "ForcedOpen", "AutoDone", "PaydayDone", "YourPayday"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "RemoveAsk", "DisabledDone", "ForcedClosed", "UnderAttack", "FieldCapturing", "FieldContested", "FieldCapped", "FieldSecured",
            "FieldDefending", "YourPaydayNoVault", "YourPaydayIdle", "PaydayAlready"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
        }

        private string KindName(string kind, Player p)
        {
            return Msg("Kind." + kind, p);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex) { PrintWarning("Config could not be read (" + ex.Message + "); using defaults."); config = null; }
            ClampConfig();
            Config.WriteObject(config, true);                // writes newly added keys and clamped values
            permission.RegisterPermission(PermAdmin, this);
            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                data = null;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". RealmDominion will not run or write anything until the file is fixed or moved away, then reload.");
                return;
            }
            if (data == null || (existed && data.Holdings == null && data.InstalledAt == DateTime.MinValue))
            {
                loadFailed = true;
                data = null;
                PrintError("oxide/data/" + DataName + ".json is empty or holds no holdings. RealmDominion will not run or write it until it is fixed or moved away.");
                return;
            }
            Normalize();
            SaveData();
        }

        private void OnServerInitialized()
        {
            if (loadFailed) return;
            // Re-sent on hot reload (doc 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            int unplaced = 0;
            foreach (Holding h in data.Holdings) if (!h.Placed) unplaced++;
            if (unplaced > 0) Puts(unplaced + " holdings are not marked on the land yet: stand on each and type /dominion admin move <id>.");
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            if (loadFailed || data == null) return;
            SaveData();
            WriteMap();
        }

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer) return;
            joinedAt[player.Id] = Now();
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null) return;
            joinedAt.Remove(player.Id);
            insideOf.Remove(player.Id);
        }

        #endregion

        #region Window

        // Realm time = UTC + offset (the crown's when UseCrownUtcOffset and CrownAndConsequences answers).
        private int OffsetMinutes()
        {
            if (config.UseCrownUtcOffset && CrownAndConsequences != null)
            {
                object o = CrownAndConsequences.Call("GetUtcOffsetHours");
                if (o is double) return Clamp((int)Math.Round((double)o * 60.0), -720, 840);
            }
            return config.UtcOffsetMinutes;
        }

        private DateTime RealmTime(DateTime utc)
        {
            return utc.AddMinutes(OffsetMinutes());
        }

        private static bool DayMatches(WindowDef w, DayOfWeek day)
        {
            foreach (string s in w.Days)
            {
                string d = (s ?? "").Trim().ToLowerInvariant();
                if (d == "daily" || d == "every day" || d == "*") return true;
                string name = day.ToString().ToLowerInvariant();
                if (d.Length >= 3 && name.StartsWith(d)) return true;
            }
            return false;
        }

        // In a scheduled window now? closes: when it ends (UTC).
        private bool InScheduledWindow(DateTime utc, out DateTime closes)
        {
            closes = DateTime.MinValue;
            DateTime local = RealmTime(utc);
            int off = OffsetMinutes();
            foreach (WindowDef w in config.Windows)
            {
                int startMin;
                if (!ParseClock(w.Start, out startMin)) continue;
                for (int back = 0; back <= 1; back++)
                {
                    DateTime day = local.Date.AddDays(-back);
                    if (!DayMatches(w, day.DayOfWeek)) continue;
                    DateTime s = day.AddMinutes(startMin), e = s.AddMinutes(w.DurationMinutes);
                    if (local >= s && local < e)
                    {
                        DateTime endUtc = e.AddMinutes(-off);
                        if (endUtc > closes) closes = endUtc;
                    }
                }
            }
            return closes != DateTime.MinValue;
        }

        // Start (UTC) of the next scheduled window that has not started yet, or MinValue.
        private DateTime NextWindowStart(DateTime utc)
        {
            DateTime local = RealmTime(utc);
            int off = OffsetMinutes();
            DateTime best = DateTime.MinValue;
            foreach (WindowDef w in config.Windows)
            {
                int startMin;
                if (!ParseClock(w.Start, out startMin)) continue;
                for (int ahead = 0; ahead <= 8; ahead++)
                {
                    DateTime day = local.Date.AddDays(ahead);
                    if (!DayMatches(w, day.DayOfWeek)) continue;
                    DateTime s = day.AddMinutes(startMin);
                    if (s <= local) continue;
                    DateTime sUtc = s.AddMinutes(-off);
                    if (best == DateTime.MinValue || sUtc < best) best = sUtc;
                    break;
                }
            }
            return best;
        }

        private bool CallBool(Plugin p, string method, params object[] args)
        {
            if (p == null) return false;
            object r = p.Call(method, args);
            return r is bool && (bool)r;
        }

        // open | paused | closed, with the reason: off, stewards-closed, truce, rebellion, forced, window, raid, none.
        private string WindowStateNow(DateTime now, out string reason, out DateTime until)
        {
            until = DateTime.MinValue;
            if (!config.Enabled) { reason = "off"; return WClosed; }
            if (data.ForcedClosedUntil > now) { reason = "stewards"; until = data.ForcedClosedUntil; return WClosed; }
            bool forced = data.ForcedOpenUntil > now;
            DateTime closes;
            bool scheduled = InScheduledWindow(now, out closes);
            bool rebellion = CallBool(CrownAndConsequences, "IsRebellionActive");
            bool inField = forced || scheduled || (rebellion && config.DuringRebellion == "open");
            if (!inField) { reason = "none"; until = NextWindowStart(now); return WClosed; }
            until = forced ? data.ForcedOpenUntil : closes;
            if (config.PauseDuringTruce && CallBool(RealmEvents, "IsTruceActive")) { reason = "truce"; return WPaused; }
            if (rebellion && config.DuringRebellion == "pause") { reason = "rebellion"; return WPaused; }
            if (rebellion && config.DuringRebellion == "open") { reason = "rebellion"; return WOpen; }
            if (forced) { reason = "forced"; return WOpen; }
            if (config.RequireRaidHours && RealmWarden != null)
            {
                object r = RealmWarden.Call("IsRaidHourNow");
                if (r is bool && !(bool)r) { reason = "raid"; return WPaused; }
            }
            reason = "window";
            return WOpen;
        }

        private string WindowLine(Player p)
        {
            string reason;
            DateTime until;
            string st = WindowStateNow(Now(), out reason, out until);
            if (st == WOpen)
            {
                if (reason == "rebellion") return Fmt("WindowOpenRebellion", p);
                if (reason == "forced") return Fmt("WindowForced", p, RealmClock(until));
                return Fmt("WindowOpen", p, RealmClock(until));
            }
            if (st == WPaused)
            {
                if (reason == "truce") return Fmt("WindowPausedTruce", p);
                if (reason == "rebellion") return Fmt("WindowPausedRebellion", p);
                return Fmt("WindowPausedRaid", p);
            }
            if (reason == "off") return Fmt("WindowOff", p);
            if (reason == "stewards") return Fmt("WindowClosedStewards", p, RealmDay(until));
            return until == DateTime.MinValue ? Fmt("WindowClosedNone", p) : Fmt("WindowClosed", p, RealmDay(until));
        }

        private string RealmClock(DateTime utc)
        {
            return RealmTime(utc).ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private string RealmDay(DateTime utc)
        {
            return RealmTime(utc).ToString("dddd HH:mm", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Tick

        private void SafeTick()
        {
            if (loadFailed || data == null) return;
            try { Tick(); }
            catch (Exception ex) { PrintError("Dominion tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = Now();
            double dt = lastTick == DateTime.MinValue ? config.TickSeconds : (now - lastTick).TotalSeconds;
            if (dt < 0) dt = 0;
            if (dt > config.TickSeconds * 3) dt = config.TickSeconds * 3;   // a hitch never jumps a banner
            lastTick = now;

            houseCache.Clear();
            allyCache.Clear();
            eligibleCache.Clear();
            memberCountCache.Clear();

            string reason;
            DateTime until;
            string state = WindowStateNow(now, out reason, out until);
            WindowTransition(state, reason, until);

            // Who is where.
            var inside = new Dictionary<string, List<Player>>();
            foreach (Holding h in data.Holdings) inside[h.Id] = new List<Player>();
            foreach (Player p in OnlinePlayers())
            {
                string house = HouseOfPlayer(p);
                TrackMembership(p, house, now);
                if (house != null) data.HouseActive[house] = now;
                Vector3 pos;
                if (!TryPosition(p, out pos) || !Alive(p)) { insideOf.Remove(p.Id); continue; }
                Holding at = HoldingAt(pos);
                if (at != null) inside[at.Id].Add(p);
                EnterNotice(p, at);
            }

            if (config.Enabled)
                foreach (Holding h in data.Holdings)
                {
                    if (!h.Placed || !h.Enabled) continue;
                    if (state == WOpen) Step(h, inside[h.Id], now, dt);
                    else if (h.State != "quiet") { h.State = "quiet"; mapDirty = true; }
                }

            if ((now - lastSlowCheck).TotalSeconds >= 60)
            {
                lastSlowCheck = now;
                CheckLineage(now);
                CheckAbandoned(now);
                PruneRecords(now);
                PruneThrottle(now);
                if (config.Enabled) TryPayday(now, false);
            }
            if (boardWanted && config.PaintBoards && (now - lastBoardRefresh).TotalSeconds >= 30)
            {
                boardWanted = false;
                lastBoardRefresh = now;
                if (RealmPainter != null) RealmPainter.Call("RefreshBoards", BoardKind);
            }
            if (mapDirty && (now - lastMap).TotalSeconds >= 15) WriteMap();
            if (dirty && (now - lastSave).TotalSeconds >= 60) SaveData();
        }

        private void WindowTransition(string state, string reason, DateTime until)
        {
            string before = windowState;
            string beforeReason = windowReason;
            windowState = state;
            windowReason = reason;
            if (before == state && beforeReason == reason) return;
            mapDirty = true;
            boardWanted = true;
            // Closing: every unfinished banner falls.
            if (state == WClosed)
                foreach (Holding h in data.Holdings)
                    if (h.Capturer != null || h.Progress > 0) { h.Capturer = null; h.Progress = 0; h.State = "quiet"; dirty = true; }
            if (before == null || !config.AnnounceWindows) return;   // first tick after a load: say nothing
            if (state == WOpen && before == WClosed) Herald(Fmt("HeraldOpen", null, until == DateTime.MinValue ? "?" : RealmClock(until)));
            else if (state == WOpen && before == WPaused) Herald(Fmt("HeraldResumed", null));
            else if (state == WPaused) Herald(Fmt("HeraldPaused", null, ReasonText(reason)));
            else if (state == WClosed && before != WClosed) Herald(Fmt("HeraldClose", null));
        }

        private string ReasonText(string reason)
        {
            if (reason == "truce") return Msg("ReasonTruce", null);
            if (reason == "rebellion") return Msg("ReasonRebellion", null);
            return Msg("ReasonRaid", null);
        }

        private Holding HoldingAt(Vector3 pos)
        {
            Holding best = null;
            double bestD = double.MaxValue;
            foreach (Holding h in data.Holdings)
            {
                if (!h.Placed || !h.Enabled) continue;
                double dx = pos.x - h.X, dz = pos.z - h.Z;
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > h.Radius || Math.Abs(pos.y - h.Y) > config.VerticalRange) continue;
                if (d < bestD) { bestD = d; best = h; }
            }
            return best;
        }

        private void EnterNotice(Player p, Holding at)
        {
            string was;
            insideOf.TryGetValue(p.Id, out was);
            string now = at != null ? at.Id : null;
            if (was == now) return;
            if (now == null) { insideOf.Remove(p.Id); return; }
            insideOf[p.Id] = now;
            if (!config.ShowEnterNotices || !Throttle("enter|" + p.Id + "|" + now, 300)) return;
            Reply(p, "Enter", at.Name, at.Owner != null ? Fmt("EnterHeld", p, HouseTint(at.Owner)) : Fmt("EnterFree", p));
        }

        private void TrackMembership(Player p, string house, DateTime now)
        {
            string id = p.Id.ToString();
            Seen s;
            data.Members.TryGetValue(id, out s);
            if (house == null)
            {
                if (s != null) { data.Members.Remove(id); dirty = true; }
                return;
            }
            if (s != null && string.Equals(s.House, house, StringComparison.OrdinalIgnoreCase)) return;
            DateTime since = now;
            // In the first MinMembershipHours after installing, everyone already in a house counts at once.
            if ((now - data.InstalledAt).TotalHours < config.MinMembershipHours && s == null)
                since = data.InstalledAt.AddHours(-config.MinMembershipHours);
            data.Members[id] = new Seen { House = house, Since = since };
            dirty = true;
        }

        #endregion

        #region Capture

        // Why a player does not count in the field, or null when they do (with their house in `house`).
        private string Ineligible(Player p, DateTime now, out string house, out string detail)
        {
            detail = null;
            house = HouseOfPlayer(p);
            if (!config.AdminsCount && IsAdmin(p)) return "staff";
            if (house == null) return "nohouse";
            DateTime joined;
            if (config.ReconnectGraceSeconds > 0 && joinedAt.TryGetValue(p.Id, out joined))
            {
                double left = config.ReconnectGraceSeconds - (now - joined).TotalSeconds;
                if (left > 0) { detail = ((int)Math.Ceiling(left)).ToString(CultureInfo.InvariantCulture); return "grace"; }
            }
            if (!config.CountNewPlayerProtected && CallBool(RealmWarden, "IsNewPlayerProtected", p.Id)) return "protected";
            string why = HouseIneligible(house, now);
            if (why != null) { detail = why; return "house"; }
            Seen s;
            if (config.MinMembershipHours > 0)
            {
                if (!data.Members.TryGetValue(p.Id.ToString(), out s) || !string.Equals(s.House, house, StringComparison.OrdinalIgnoreCase))
                {
                    detail = config.MinMembershipHours.ToString(CultureInfo.InvariantCulture);
                    return "hop";
                }
                double left = config.MinMembershipHours - (now - s.Since).TotalHours;
                if (left > 0) { detail = ((int)Math.Ceiling(left)).ToString(CultureInfo.InvariantCulture); return "hop"; }
            }
            return null;
        }

        // A house too small or too young to make war, or null.
        private string HouseIneligible(string house, DateTime now)
        {
            string cached;
            string key = house.ToLowerInvariant();
            if (eligibleCache.TryGetValue(key, out cached)) return cached;
            string why = null;
            int members = MemberCount(house);
            if (members < config.MinHouseMembers) why = members + "/" + config.MinHouseMembers + " members";
            else if (config.MinHouseAgeHours > 0)
            {
                DateTime founded;
                if (!HouseFounded(house, out founded)) why = "founding unknown";
                else
                {
                    double left = config.MinHouseAgeHours - (now - founded).TotalHours;
                    if (left > 0) why = "founded " + (int)Math.Floor((now - founded).TotalHours) + "/" + config.MinHouseAgeHours + " h ago";
                }
            }
            eligibleCache[key] = why;
            return why;
        }

        private double Rate(Holding h, int n)
        {
            double garrison = h.Owner != null ? 1.0 + h.Garrison * config.GarrisonPercentPerLevel / 100.0 : 1.0;
            double crowd = 1.0 + (Math.Min(n, config.MaxCountedPerHouse) - 1) * config.ExtraCapturerPercent / 100.0;
            return 100.0 / (config.CaptureSeconds * garrison) * crowd;    // banner points per second
        }

        private void Step(Holding h, List<Player> present, DateTime now, double dt)
        {
            // Count the eligible by house (capped), keep the players of each house for rewards and notices.
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byHouse = new Dictionary<string, List<Player>>(StringComparer.OrdinalIgnoreCase);
            foreach (Player p in present)
            {
                string house, detail;
                if (Ineligible(p, now, out house, out detail) != null) continue;
                int c;
                counts.TryGetValue(house, out c);
                counts[house] = Math.Min(c + 1, config.MaxCountedPerHouse);
                List<Player> l;
                if (!byHouse.TryGetValue(house, out l)) byHouse[house] = l = new List<Player>();
                l.Add(p);
            }

            string owner = h.Owner;
            int defenders = 0;
            if (owner != null) counts.TryGetValue(owner, out defenders);
            var attackers = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (owner != null && string.Equals(kv.Key, owner, StringComparison.OrdinalIgnoreCase)) continue;
                if (owner != null && !config.AllowCaptureFromAllies && Allied(kv.Key, owner)) continue;   // allies count for no one
                attackers.Add(kv.Key);
            }

            string old = h.State;
            if (now < h.SecureUntil)
            {
                SetState(h, "secured");
                if (attackers.Count > 0) FieldNotice(present, h, "FieldSecured", h.Name, MinutesUntil(h.SecureUntil, now));
                return;
            }

            // Two attacking houses that are not allies of each other contest the field.
            bool hostileAttackers = false;
            for (int i = 0; i < attackers.Count && !hostileAttackers; i++)
                for (int j = i + 1; j < attackers.Count; j++)
                    if (!Allied(attackers[i], attackers[j])) { hostileAttackers = true; break; }

            if (attackers.Count > 0 && (defenders > 0 || hostileAttackers))
            {
                SetState(h, "contested");
                if (old != "contested") FieldNotice(present, h, "FieldContested", h.Name);
                return;
            }

            if (attackers.Count == 0)
            {
                if (h.Progress <= 0) { SetState(h, "quiet"); return; }
                if (defenders > 0)
                {
                    h.Progress = Math.Max(0, h.Progress - Rate(h, defenders) * dt);
                    SetState(h, "defending");
                    if (old != "defending" && h.Capturer != null) FieldNotice(present, h, "FieldDefending", h.Name, HouseTint(h.Capturer), (int)h.Progress);
                }
                else if ((now - h.LastPresence).TotalSeconds >= config.DecayDelaySeconds)
                {
                    h.Progress = Math.Max(0, h.Progress - config.DecayPerMinute / 60.0 * dt);
                    SetState(h, "quiet");
                }
                if (h.Progress <= 0) { h.Progress = 0; h.Capturer = null; }
                dirty = true;
                mapDirty = true;
                return;
            }

            // One attacking side: the house with the most men leads (ties: the banner already rising, else the first name).
            string lead = null;
            int leadN = 0;
            foreach (string a in attackers)
            {
                int n = counts[a];
                bool better = n > leadN || (n == leadN && h.Capturer != null && string.Equals(a, h.Capturer, StringComparison.OrdinalIgnoreCase))
                    || (n == leadN && lead != null && !string.Equals(lead, h.Capturer, StringComparison.OrdinalIgnoreCase) && string.Compare(a, lead, StringComparison.OrdinalIgnoreCase) < 0);
                if (lead == null || better) { lead = a; leadN = n; }
            }
            if (leadN < config.MinCapturers) { SetState(h, "quiet"); return; }
            if (HoldingsOf(lead) >= config.MaxHoldingsPerHouse)
            {
                SetState(h, "capped");
                if (old != "capped") FieldNotice(present, h, "FieldCapped", h.Name, HouseTint(lead), config.MaxHoldingsPerHouse);
                return;
            }

            h.LastPresence = now;
            double rate = Rate(h, leadN) * dt;
            if (h.Capturer != null && !string.Equals(h.Capturer, lead, StringComparison.OrdinalIgnoreCase))
            {
                h.Progress -= rate;                          // a rival banner must come down first
                if (h.Progress <= 0) { h.Progress = 0; h.Capturer = null; }
                SetState(h, "capturing");
                dirty = true;
                mapDirty = true;
                return;
            }
            bool fresh = h.Capturer == null;
            h.Capturer = lead;
            h.Progress = Math.Min(100, h.Progress + rate);
            SetState(h, "capturing");
            dirty = true;
            mapDirty = true;
            if (fresh)
            {
                boardWanted = true;
                if (config.AnnounceCaptures && Throttle("banner|" + h.Id + "|" + lead.ToLowerInvariant(), config.AnnounceCooldownSeconds))
                    Herald(Fmt("HeraldBanner", null, HouseTint(lead), h.Name, owner != null ? "House " + HouseTint(owner) : Msg("NoOne", null)));
            }
            if (h.Progress >= 100) { Capture(h, lead, byHouse[lead], now); return; }
            int pct = (int)Math.Floor(h.Progress);
            if (Throttle("field|" + h.Id, Math.Max(15, config.AnnounceCooldownSeconds / 4)))
                FieldNotice(present, h, "FieldCapturing", h.Name, HouseTint(lead), pct);
            if (owner != null && Throttle("attack|" + h.Id, config.AnnounceCooldownSeconds))
                foreach (Player m in OnlineMembers(owner)) Reply(m, "UnderAttack", h.Name, HouseTint(lead), pct);
        }

        private void SetState(Holding h, string state)
        {
            if (h.State == state) return;
            h.State = state;
            mapDirty = true;
            boardWanted = true;
        }

        private void FieldNotice(List<Player> present, Holding h, string key, params object[] args)
        {
            foreach (Player p in present) Reply(p, key, args);
        }

        private void Capture(Holding h, string house, List<Player> capturers, DateTime now)
        {
            string from = h.Owner;
            h.Owner = house;
            h.OwnerSince = now;
            h.OwnerFounded = HouseFoundedText(house);
            h.Garrison = 0;
            h.Capturer = null;
            h.Progress = 0;
            h.SecureUntil = now.AddMinutes(config.SecureMinutes);
            h.Captures++;
            h.State = "secured";
            h.History.Add(Iso(now) + "|" + house + "|" + (from ?? ""));
            if (h.History.Count > 10) h.History.RemoveRange(0, h.History.Count - 10);
            data.Seq++;
            bool rewarded = Rewardable(h, house, from, now);
            data.CaptureLog.Add(new CaptureRecord { At = now, Holding = h.Id, House = house, From = from, Rewarded = rewarded });
            SaveData();                                     // the capture stands before anything is paid or told
            mapDirty = true;
            boardWanted = true;

            var names = new List<string>();
            foreach (Player p in capturers) names.Add(Clean(p.Name, 32));
            if (rewarded)
            {
                if (RealmSeasons != null && config.CapturePoints > 0) RealmSeasons.Call("AwardHouse", house, config.CapturePoints, "Took " + h.Name);
                if (RealmRenown != null && config.RenownDeed.Length > 0)
                    foreach (Player p in capturers)
                        RealmRenown.Call("AddDeed", p.Id.ToString(), p.Name, config.RenownDeed, "took " + h.Name,
                            "dominion:" + h.Id + ":" + data.Seq + ":" + p.Id);
            }
            else Puts("Capture of " + h.Name + " by " + house + " earns no rewards (flip or daily limit).");

            string fromText = from != null ? Fmt("HeraldTakenFrom", null, HouseTint(from)) : "";
            if (config.AnnounceCaptures) Herald(Fmt("HeraldTaken", null, HouseTint(house), h.Name, fromText));
            string title = "House " + Clean(house, 40) + " takes " + h.Name;
            string detail = (from != null ? "Taken from House " + Clean(from, 40) + ". " : "Claimed while no house held it. ")
                + (names.Count > 0 ? "In the field: " + string.Join(", ", names.ToArray()) + "." : "");
            Chronicle(title, detail, names.ToArray());
            WriteMap();
        }

        // No rewards for flips: the same two houses trading a holding, a house retaking what it lately held, or past the daily cap.
        private bool Rewardable(Holding h, string house, string from, DateTime now)
        {
            int today = 0;
            foreach (CaptureRecord c in data.CaptureLog)
            {
                double hours = (now - c.At).TotalHours;
                if (c.Rewarded && hours < 24 && string.Equals(c.House, house, StringComparison.OrdinalIgnoreCase)) today++;
                if (hours >= config.PairCooldownHours) continue;
                if (from != null && string.Equals(c.House, from, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.From, house, StringComparison.OrdinalIgnoreCase)) return false;      // they took one from us lately
                if (c.Holding == h.Id && string.Equals(c.From, house, StringComparison.OrdinalIgnoreCase)) return false;  // we lost this one lately
            }
            return today < config.MaxRewardedCapturesPerDay;
        }

        private void LoseHolding(Holding h, string heraldKey, params object[] args)
        {
            h.Owner = null;
            h.OwnerFounded = null;
            h.Garrison = 0;
            h.Capturer = null;
            h.Progress = 0;
            h.State = "quiet";
            dirty = true;
            mapDirty = true;
            boardWanted = true;
            if (heraldKey != null && config.AnnounceCaptures) Herald(Fmt(heraldKey, null, args));
        }

        // A holding whose house is gone, or refounded under the same name, returns to no one.
        private void CheckLineage(DateTime now)
        {
            if (RealmHouses == null) return;
            foreach (Holding h in data.Holdings)
            {
                if (h.Owner == null) continue;
                string founded = HouseFoundedText(h.Owner);
                if (founded == null || (h.OwnerFounded != null && founded != h.OwnerFounded))
                {
                    string was = h.Owner;
                    LoseHolding(h, "HeraldFallen", h.Name, HouseTint(was));
                }
                else if (h.OwnerFounded == null) { h.OwnerFounded = founded; dirty = true; }
            }
        }

        private void CheckAbandoned(DateTime now)
        {
            if (config.AbandonAfterDays <= 0) return;
            foreach (Holding h in data.Holdings)
            {
                if (h.Owner == null) continue;
                DateTime seen;
                if (!data.HouseActive.TryGetValue(h.Owner, out seen)) { data.HouseActive[h.Owner] = now; dirty = true; continue; }
                if ((now - seen).TotalDays < config.AbandonAfterDays) continue;
                string was = h.Owner;
                LoseHolding(h, "HeraldAbandoned", h.Name, HouseTint(was), config.AbandonAfterDays);
            }
        }

        private void PruneRecords(DateTime now)
        {
            int keepHours = Math.Max(48, config.PairCooldownHours + 24);
            int before = data.CaptureLog.Count;
            data.CaptureLog.RemoveAll(delegate(CaptureRecord c) { return (now - c.At).TotalHours > keepHours; });
            if (data.CaptureLog.Count > 500) data.CaptureLog.RemoveRange(0, data.CaptureLog.Count - 500);
            var gone = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in data.HouseActive)
                if ((now - kv.Value).TotalDays > 60) gone.Add(kv.Key);
            foreach (string k in gone) data.HouseActive.Remove(k);
            if (data.CaptureLog.Count != before || gone.Count > 0) dirty = true;
        }

        private void PruneThrottle(DateTime now)
        {
            var gone = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in throttle) if (kv.Value < now) gone.Add(kv.Key);
            foreach (string k in gone) throttle.Remove(k);
        }

        #endregion

        #region Income

        private long IncomeOf(Holding h)
        {
            long baseIncome = config.IncomeByKind[h.Kind];
            return baseIncome + baseIncome * h.Garrison * config.IncomeGarrisonPercentPerLevel / 100;
        }

        private int PointsOf(Holding h)
        {
            return config.PointsPerDayByKind[h.Kind];
        }

        // Once a realm day at IncomeTime. A missed day is not paid twice; a restart after the hour pays the same day.
        private int TryPayday(DateTime now, bool force)
        {
            DateTime local = RealmTime(now);
            string today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (data.LastIncomeDay == today) return -1;
            int at;
            ParseClock(config.IncomeTime, out at);
            if (!force && local.TimeOfDay.TotalMinutes < at) return -1;
            data.LastIncomeDay = today;
            int paid = 0;
            var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (Holding h in data.Holdings)
            {
                if (h.Owner == null || !h.Placed || !h.Enabled) continue;
                if ((now - h.OwnerSince).TotalHours < config.MinHeldHoursForIncome) continue;
                string house = h.Owner;
                var members = OnlineMembers(house);
                DateTime seen;
                bool active = data.HouseActive.TryGetValue(house, out seen) && (now - seen).TotalHours <= 24;
                if (config.IncomeRequiresActivity && !active)
                {
                    foreach (Player m in members) Reply(m, "YourPaydayIdle", h.Name, HouseTint(house));
                    continue;
                }
                long marks = IncomeOf(h);
                int points = PointsOf(h);
                long credited = 0;
                string refused = null;
                if (marks > 0)
                {
                    if (RealmTreasury == null) refused = Msg("NoTreasury", null);
                    else
                    {
                        object r = RealmTreasury.Call("GrantHouseIncome", house, marks, "dominion", "holding " + h.Name);
                        credited = r is long ? (long)r : 0;
                        if (credited <= 0) refused = Msg("TreasuryRefused", null);
                    }
                }
                if (points > 0 && RealmSeasons != null) RealmSeasons.Call("AwardHouse", house, points, null);
                if (h.Garrison < config.MaxGarrisonLevel) h.Garrison++;
                if (credited > 0)
                {
                    long had;
                    data.IncomePaid.TryGetValue(house, out had);
                    data.IncomePaid[house] = had + credited;
                    long t;
                    totals.TryGetValue(house, out t);
                    totals[house] = t + credited;
                }
                paid++;
                foreach (Player m in members)
                {
                    if (refused == null) Reply(m, "YourPayday", h.Name, HouseTint(house), credited, Currency(), points);
                    else Reply(m, "YourPaydayNoVault", h.Name, HouseTint(house), points, refused);
                }
            }
            SaveData();
            mapDirty = true;
            boardWanted = true;
            if (totals.Count > 0 && config.AnnounceWindows)
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, long> kv in totals) parts.Add(Fmt("PaydayPart", null, HouseTint(kv.Key), kv.Value, Currency()));
                Herald(Fmt("HeraldPayday", null, string.Join(", ", parts.ToArray())));
            }
            return paid;
        }

        private static string Currency()
        {
            return "marks";
        }

        #endregion

        #region Garrison (damage)

        // While the field is open, members of the holding house standing in their own holding take less harm from hostile
        // players. Damage.Amount is scaled, as RealmLegendary does. A blow another plugin zeroed or cancelled stays so.
        // UNVERIFIED in game: that a scaled Amount is what the victim loses (doc smoke step D7).
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            try
            {
                if (loadFailed || data == null || !config.Enabled || !config.GarrisonDamageReduction || config.GarrisonDamageReductionPercent <= 0f) return null;
                if (evt == null || evt.Cancelled || evt.Damage == null || evt.Entity == null || !evt.Entity.IsPlayer) return null;
                if (windowState != WOpen || evt.Damage.Amount <= 0f) return null;
                Player victim = evt.Entity.Owner;
                Entity src = evt.Damage.DamageSource;
                Player attacker = src != null && src.IsPlayer ? src.Owner : null;
                if (victim == null || attacker == null || victim.IsServer || attacker.IsServer || victim.Id == attacker.Id) return null;
                string vh = HouseOfPlayer(victim);
                if (vh == null) return null;
                string ah = HouseOfPlayer(attacker);
                if (ah != null && Allied(ah, vh)) return null;
                Holding at = HoldingAt(evt.Entity.Position);
                if (at == null || at.Owner == null || !string.Equals(at.Owner, vh, StringComparison.OrdinalIgnoreCase)) return null;
                evt.Damage.Amount = evt.Damage.Amount * (1f - config.GarrisonDamageReductionPercent / 100f);
            }
            catch (Exception ex) { PrintWarning("Garrison damage: " + ex.Message); }
            return null;
        }

        #endregion

        #region Commands

        [ChatCommand("dominion")]
        private void CmdDominion(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed || data == null) { ReplyError(player, "Paused"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                if (sub == "admin") { CmdAdmin(player, args); return; }
                if (!config.Enabled && !IsAdmin(player)) { Reply(player, "Off"); return; }
                if (sub == "") { ShowMap(player); return; }
                if (sub == "here") { ShowHere(player); return; }
                if (sub == "rules" || sub == "help")
                {
                    Reply(player, "Rules1");
                    Reply(player, "Rules2");
                    Reply(player, "Rules3", config.MinHouseMembers, config.MinHouseAgeHours, config.MinMembershipHours, config.MaxHoldingsPerHouse);
                    Reply(player, "Rules4", Num(config.GarrisonDamageReduction ? config.GarrisonDamageReductionPercent : 0f));
                    Reply(player, "Rules5", config.AbandonAfterDays);
                    return;
                }
                Holding h = FindHolding(string.Join(" ", args));
                if (h == null) { ReplyError(player, "NotFound", Clean(string.Join(" ", args), 40)); return; }
                ShowHolding(player, h);
            }
            catch (Exception ex)
            {
                PrintError("/dominion failed: " + ex.Message);
            }
        }

        private string SummaryOf(Holding h, Player p)
        {
            if (!h.Placed) return Msg("Unplaced", p);
            if (!h.Enabled) return Msg("Disabled", p);
            string s = h.Owner != null
                ? Fmt("OwnerLine", p, HouseTint(h.Owner), Roman(h.Garrison), IncomeOf(h), Currency())
                : Fmt("Unclaimed", p, IncomeOf(h), Currency());
            DateTime now = Now();
            if (now < h.SecureUntil) s += Fmt("StateSecured", p, MinutesUntil(h.SecureUntil, now));
            else if (h.State == "contested") s += Fmt("StateContested", p);
            else if (h.Capturer != null && h.Progress > 0) s += Fmt("StateCapturing", p, HouseTint(h.Capturer), (int)Math.Floor(h.Progress));
            return s;
        }

        private void ShowMap(Player player)
        {
            ReplyText(player, ChatGold, WindowLine(player));
            if (data.Holdings.Count == 0) { Reply(player, "MapEmpty"); return; }
            Reply(player, "MapHeader", data.Holdings.Count);
            var popup = new StringBuilder();
            popup.Append(WindowLine(player));
            foreach (Holding h in data.Holdings)
            {
                string line = Fmt("MapLine", player, h.Name, KindName(h.Kind, player), SummaryOf(h, player));
                player.SendMessage(line);
                popup.Append('\n').Append(line.Trim());
            }
            player.SendMessage(Msg("MapFooter", player));
            if (PopupsFor(player)) ShowInfoPopup(player, Msg("PopupTitle", player), popup.ToString(), Msg("PopupButton", player));
        }

        private void ShowHolding(Player player, Holding h)
        {
            DateTime now = Now();
            Reply(player, "InfoTitle", h.Name, KindName(h.Kind, player));
            if (!string.IsNullOrEmpty(h.Place)) player.SendMessage(Fmt("InfoPlace", player, Clean(h.Place, 160)));
            if (h.Owner != null)
            {
                int slower = h.Garrison * config.GarrisonPercentPerLevel;
                player.SendMessage(Fmt("InfoOwner", player, HouseTint(h.Owner), h.OwnerSince.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Roman(h.Garrison), Roman(config.MaxGarrisonLevel), slower));
            }
            else player.SendMessage(Fmt("InfoUnclaimed", player));
            player.SendMessage(Fmt("InfoIncome", player, IncomeOf(h), Currency(), PointsOf(h), config.IncomeTime));
            if (!h.Placed) player.SendMessage(Fmt("InfoUnplaced", player));
            else
            {
                Vector3 pos;
                if (TryPosition(player, out pos))
                {
                    double dx = pos.x - h.X, dz = pos.z - h.Z;
                    player.SendMessage(Fmt("InfoWhere", player, (int)Math.Round(h.X), (int)Math.Round(h.Z), (int)Math.Round(h.Radius * 2), (int)Math.Round(Math.Sqrt(dx * dx + dz * dz))));
                }
                else player.SendMessage(Fmt("InfoWhereNoYou", player, (int)Math.Round(h.X), (int)Math.Round(h.Z), (int)Math.Round(h.Radius * 2)));
            }
            if (now < h.SecureUntil) player.SendMessage(Fmt("InfoSecure", player, MinutesUntil(h.SecureUntil, now)));
            else if (h.Capturer != null && h.Progress > 0) player.SendMessage(Fmt("InfoBanner", player, HouseTint(h.Capturer), (int)Math.Floor(h.Progress)));
            if (h.History.Count > 0)
            {
                string[] last = h.History[h.History.Count - 1].Split('|');
                if (last.Length >= 2) player.SendMessage(Fmt("InfoHistory", player, (last[0].Length >= 10 ? last[0].Substring(0, 10) : last[0]) + ", House " + HouseTint(last[1])));
            }
            ReplyText(player, ChatGold, WindowLine(player));
        }

        private void ShowHere(Player player)
        {
            Vector3 pos;
            if (!TryPosition(player, out pos)) { ReplyError(player, "NoPosition"); return; }
            Holding h = HoldingAt(pos);
            if (h == null) { Reply(player, "HereNone"); return; }
            Reply(player, "HereIn", h.Name, SummaryOf(h, player));
            DateTime now = Now();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Player p in OnlinePlayers())
            {
                Vector3 pp;
                if (!TryPosition(p, out pp) || !Alive(p) || HoldingAt(pp) != h) continue;
                string house, detail;
                if (Ineligible(p, now, out house, out detail) != null) continue;
                int c;
                counts.TryGetValue(house, out c);
                counts[house] = c + 1;
            }
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in counts) parts.Add(HouseTint(kv.Key) + " " + kv.Value);
            player.SendMessage(Fmt("HereCount", player, parts.Count > 0 ? string.Join(", ", parts.ToArray()) : Msg("HereNobody", player)));
            string myHouse, why;
            string reason = Ineligible(player, now, out myHouse, out why);
            string you;
            if (reason == null) you = Fmt("YouCount", player, HouseTint(myHouse));
            else if (reason == "nohouse") you = Fmt("YouNoHouse", player);
            else if (reason == "house") you = Fmt("YouNew", player, why);
            else if (reason == "hop") you = Fmt("YouHop", player, HouseTint(myHouse), why);
            else if (reason == "protected") you = Fmt("YouProtected", player);
            else if (reason == "grace") you = Fmt("YouGrace", player, why);
            else you = Fmt("YouStaff", player);
            player.SendMessage(Fmt("HereYou", player, you));
        }

        private void CmdAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string op = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            DateTime now = Now();
            Holding h = args.Length > 2 ? FindHolding(args[2]) : null;
            switch (op)
            {
                case "status":
                {
                    string reason;
                    DateTime until;
                    string st = WindowStateNow(now, out reason, out until);
                    int placed = 0, held = 0;
                    foreach (Holding x in data.Holdings) { if (x.Placed) placed++; if (x.Owner != null) held++; }
                    Reply(player, "AdminStatus", st, reason, data.Holdings.Count, placed, held, data.LastIncomeDay ?? Msg("Never", player),
                        Msg(RealmTreasury != null ? "Loaded" : "Missing", player), Msg(RealmHouses != null ? "Loaded" : "Missing", player));
                    DateTime next = NextWindowStart(now);
                    player.SendMessage(Fmt("AdminStatus2", player, RealmTime(now).ToString("ddd HH:mm", CultureInfo.InvariantCulture),
                        next == DateTime.MinValue ? Msg("None", player) : RealmDay(next), config.IncomeTime));
                    return;
                }
                case "create":
                {
                    // create <id> <kind> [radius] <name...>
                    if (args.Length < 5) { ShowAdminHelp(player); return; }
                    string id = Slug(args[2]);
                    if (id == null) { ReplyError(player, "BadId", Clean(args[2], 30)); return; }
                    if (FindById(id) != null) { ReplyError(player, "IdTaken", id); return; }
                    string kind = args[3].ToLowerInvariant();
                    if (Array.IndexOf(Kinds, kind) < 0) { ReplyError(player, "BadKind", Clean(args[3], 20)); return; }
                    if (data.Holdings.Count >= config.MaxHoldings) { ReplyError(player, "TooMany", config.MaxHoldings); return; }
                    int nameAt = 4;
                    float radius = 40f;
                    float r;
                    if (float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out r)) { radius = r; nameAt = 5; }
                    if (radius < config.MinRadius || radius > config.MaxRadius || float.IsNaN(radius)) { ReplyError(player, "BadRadius", (int)(config.MinRadius * 2), (int)(config.MaxRadius * 2)); return; }
                    string name = Clean(JoinFrom(args, nameAt), 40);
                    if (name.Length == 0) { ReplyError(player, "NeedName"); return; }
                    Vector3 pos;
                    if (!TryPosition(player, out pos)) { ReplyError(player, "NoPosition"); return; }
                    var nh = new Holding { Id = id, Name = name, Kind = kind, Radius = radius, X = pos.x, Y = pos.y, Z = pos.z, Placed = true, Place = "", History = new List<string>() };
                    data.Holdings.Add(nh);
                    Changed();
                    Reply(player, "Created", nh.Name, (int)(radius * 2));
                    return;
                }
                case "move":
                {
                    if (h == null) { NotFoundOrHelp(player, args); return; }
                    float radius = h.Radius;
                    if (args.Length > 3)
                    {
                        float r;
                        if (!float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r < config.MinRadius || r > config.MaxRadius)
                        { ReplyError(player, "BadRadius", (int)(config.MinRadius * 2), (int)(config.MaxRadius * 2)); return; }
                        radius = r;
                    }
                    Vector3 pos;
                    if (!TryPosition(player, out pos)) { ReplyError(player, "NoPosition"); return; }
                    h.X = pos.x; h.Y = pos.y; h.Z = pos.z; h.Radius = radius; h.Placed = true;
                    h.Capturer = null; h.Progress = 0;
                    Changed();
                    Reply(player, "Moved", h.Name, (int)(radius * 2));
                    return;
                }
                case "radius":
                {
                    if (h == null || args.Length < 4) { NotFoundOrHelp(player, args); return; }
                    float r;
                    if (!float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r < config.MinRadius || r > config.MaxRadius)
                    { ReplyError(player, "BadRadius", (int)(config.MinRadius * 2), (int)(config.MaxRadius * 2)); return; }
                    h.Radius = r;
                    Changed();
                    Reply(player, "Radius", h.Name, (int)(r * 2));
                    return;
                }
                case "rename":
                {
                    if (h == null || args.Length < 4) { NotFoundOrHelp(player, args); return; }
                    string name = Clean(JoinFrom(args, 3), 40);
                    if (name.Length == 0) { ReplyError(player, "NeedName"); return; }
                    string was = h.Name;
                    h.Name = name;
                    Changed();
                    Reply(player, "Renamed", was, name);
                    return;
                }
                case "remove":
                {
                    if (h == null) { NotFoundOrHelp(player, args); return; }
                    string key = player.Id.ToString();
                    string pending;
                    if (args.Length > 3 && args[3].ToLowerInvariant() == "confirm" && confirm.TryGetValue(key, out pending) && pending == h.Id)
                    {
                        confirm.Remove(key);
                        data.Holdings.Remove(h);
                        Changed();
                        Reply(player, "Removed", h.Name);
                        return;
                    }
                    confirm[key] = h.Id;
                    Reply(player, "RemoveAsk", h.Name, h.Id);
                    return;
                }
                case "enable":
                case "disable":
                {
                    if (h == null) { NotFoundOrHelp(player, args); return; }
                    h.Enabled = op == "enable";
                    h.Capturer = null; h.Progress = 0;
                    Changed();
                    Reply(player, h.Enabled ? "Enabled" : "DisabledDone", h.Name);
                    return;
                }
                case "owner":
                {
                    if (h == null || args.Length < 4) { NotFoundOrHelp(player, args); return; }
                    string house = JoinFrom(args, 3).Trim();
                    if (house.ToLowerInvariant() == "none")
                    {
                        LoseHolding(h, null);
                        Changed();
                        Reply(player, "OwnerSet", h.Name, Msg("NoOne", player));
                        return;
                    }
                    string founded = HouseFoundedText(house);
                    if (RealmHouses != null && founded == null) { ReplyError(player, "NoSuchHouse", Clean(house, 40)); return; }
                    h.Owner = CanonicalHouse(house);
                    h.OwnerFounded = founded;
                    h.OwnerSince = now;
                    h.Garrison = 0;
                    h.Capturer = null; h.Progress = 0;
                    data.HouseActive[h.Owner] = now;
                    Changed();
                    Reply(player, "OwnerSet", h.Name, "House " + HouseTint(h.Owner));
                    return;
                }
                case "reset":
                {
                    if (h == null) { NotFoundOrHelp(player, args); return; }
                    h.Capturer = null; h.Progress = 0; h.SecureUntil = DateTime.MinValue; h.State = "quiet";
                    Changed();
                    Reply(player, "ResetDone", h.Name);
                    return;
                }
                case "open":
                case "close":
                {
                    int minutes = op == "open" ? 60 : 120;
                    if (args.Length > 2)
                    {
                        int m;
                        if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) || m < 1 || m > 1440)
                        { ReplyError(player, "BadNumber", Clean(args[2], 20), 1, 1440); return; }
                        minutes = m;
                    }
                    if (op == "open") { data.ForcedOpenUntil = now.AddMinutes(minutes); data.ForcedClosedUntil = DateTime.MinValue; }
                    else { data.ForcedClosedUntil = now.AddMinutes(minutes); data.ForcedOpenUntil = DateTime.MinValue; }
                    Changed();
                    Reply(player, op == "open" ? "ForcedOpen" : "ForcedClosed", minutes);
                    SafeTick();
                    return;
                }
                case "auto":
                    data.ForcedOpenUntil = DateTime.MinValue;
                    data.ForcedClosedUntil = DateTime.MinValue;
                    Changed();
                    Reply(player, "AutoDone");
                    SafeTick();
                    return;
                case "payday":
                {
                    int n = TryPayday(now, true);
                    if (n < 0) Reply(player, "PaydayAlready", data.LastIncomeDay);
                    else Reply(player, "PaydayDone", n);
                    return;
                }
            }
            ShowAdminHelp(player);
        }

        private void NotFoundOrHelp(Player player, string[] args)
        {
            if (args.Length > 2) ReplyError(player, "NotFound", Clean(args[2], 40));
            else ShowAdminHelp(player);
        }

        private void ShowAdminHelp(Player player)
        {
            player.SendMessage(Msg("Admin1", player));
            player.SendMessage(Msg("Admin2", player));
            player.SendMessage(Msg("Admin3", player));
        }

        private void Changed()
        {
            mapDirty = true;
            boardWanted = true;
            SaveData();
            WriteMap();
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

        #region Map file (oxide/data/RealmDominionMap.json; schema in plugins/docs/RealmDominion.md)

        private class MapWindow
        {
            public string state;
            public string reason;
            public string until;
            public string nextOpen;
        }

        private class MapHolding
        {
            public string id;
            public string name;
            public string kind;
            public string place;
            public bool placed;
            public bool enabled;
            public float x;
            public float z;
            public float radius;
            public string owner;
            public string ownerSince;
            public int garrison;
            public long incomePerDay;
            public int pointsPerDay;
            public string state;
            public string capturer;
            public int progress;
            public string secureUntil;
            public int captures;
            public string lastTaken;
        }

        private class MapHouse
        {
            public string name;
            public int holdings;
            public long incomePerDay;
            public long incomeTotal;
        }

        private class MapFile
        {
            public int schema = 1;
            public string generated;
            public MapWindow window;
            public List<MapHolding> holdings;
            public List<MapHouse> houses;
        }

        private void WriteMap()
        {
            if (loadFailed || data == null || !config.PublishMap) return;
            DateTime now = Now();
            lastMap = now;
            mapDirty = false;
            try
            {
                string reason;
                DateTime until;
                string st = WindowStateNow(now, out reason, out until);
                DateTime next = NextWindowStart(now);
                var f = new MapFile
                {
                    generated = Iso(now),
                    window = new MapWindow { state = st, reason = reason, until = until == DateTime.MinValue ? null : Iso(until), nextOpen = next == DateTime.MinValue ? null : Iso(next) },
                    holdings = new List<MapHolding>(),
                    houses = new List<MapHouse>()
                };
                var houses = new Dictionary<string, MapHouse>(StringComparer.OrdinalIgnoreCase);
                foreach (Holding h in data.Holdings)
                {
                    string last = null;
                    if (h.History.Count > 0) { string[] parts = h.History[h.History.Count - 1].Split('|'); last = parts[0]; }
                    f.holdings.Add(new MapHolding
                    {
                        id = h.Id, name = h.Name, kind = h.Kind, place = h.Place ?? "", placed = h.Placed, enabled = h.Enabled,
                        x = (float)Math.Round(h.X, 1), z = (float)Math.Round(h.Z, 1), radius = h.Radius,
                        owner = h.Owner, ownerSince = h.Owner != null ? Iso(h.OwnerSince) : null, garrison = h.Garrison,
                        incomePerDay = IncomeOf(h), pointsPerDay = PointsOf(h), state = h.State, capturer = h.Capturer,
                        progress = (int)Math.Floor(h.Progress), secureUntil = now < h.SecureUntil ? Iso(h.SecureUntil) : null,
                        captures = h.Captures, lastTaken = last
                    });
                    if (h.Owner == null) continue;
                    MapHouse mh;
                    if (!houses.TryGetValue(h.Owner, out mh)) houses[h.Owner] = mh = new MapHouse { name = h.Owner };
                    mh.holdings++;
                    if (h.Placed && h.Enabled) mh.incomePerDay += IncomeOf(h);
                }
                foreach (MapHouse mh in houses.Values)
                {
                    long t;
                    data.IncomePaid.TryGetValue(mh.name, out t);
                    mh.incomeTotal = t;
                    f.houses.Add(mh);
                }
                f.houses.Sort(delegate(MapHouse a, MapHouse b) { return b.holdings != a.holdings ? b.holdings.CompareTo(a.holdings) : string.CompareOrdinal(a.name, b.name); });
                Interface.Oxide.DataFileSystem.WriteObject(DataName + "Map", f);
            }
            catch (Exception ex) { PrintWarning("Could not write oxide/data/" + DataName + "Map.json: " + ex.Message); }
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // Every holding: {id, name, kind, owner, state, progress, garrison, income, placed}.
        private List<Dictionary<string, object>> GetHoldings()
        {
            var list = new List<Dictionary<string, object>>();
            if (data == null) return list;
            foreach (Holding h in data.Holdings)
            {
                var d = new Dictionary<string, object>();
                d["id"] = h.Id;
                d["name"] = h.Name;
                d["kind"] = h.Kind;
                d["owner"] = h.Owner;
                d["state"] = h.State;
                d["capturer"] = h.Capturer;
                d["progress"] = (int)Math.Floor(h.Progress);
                d["garrison"] = h.Garrison;
                d["income"] = IncomeOf(h);
                d["placed"] = h.Placed && h.Enabled;
                list.Add(d);
            }
            return list;
        }

        private string GetHoldingOwner(string id)
        {
            Holding h = data != null ? FindHolding(id) : null;
            return h != null ? h.Owner : null;
        }

        private int GetHouseHoldingCount(string house)
        {
            return data != null && house != null ? HoldingsOf(house) : 0;
        }

        // One line on the War Hours, plain text (no colour tags), for boards and other plugins.
        private string GetDominionWindow()
        {
            if (data == null) return null;
            return PopupText(WindowLine(null));
        }

        // What RealmPainter's "dominion" board draws: {title, kicker, footer, window, rows: [{name, kind, owner, right}]}.
        private Dictionary<string, object> GetDominionBoard()
        {
            if (data == null) return null;
            var b = new Dictionary<string, object>();
            b["title"] = Msg("BoardTitle", null);
            b["kicker"] = Msg("BoardKicker", null);
            b["footer"] = Msg("BoardFooter", null);
            b["window"] = PopupText(WindowLine(null));
            b["empty"] = Msg("BoardEmpty", null);
            var rows = new List<Dictionary<string, object>>();
            foreach (Holding h in data.Holdings)
            {
                if (!h.Placed || !h.Enabled) continue;
                var r = new Dictionary<string, object>();
                r["name"] = h.Name;
                r["kind"] = h.Kind;
                r["owner"] = h.Owner;
                string right;
                if (h.State == "contested") right = Msg("BoardContested", null);
                else if (h.Capturer != null && h.Progress > 0) right = Fmt("BoardRising", null, Clean(h.Capturer, 20), (int)Math.Floor(h.Progress));
                else right = h.Owner != null ? Roman(h.Garrison) : Msg("BoardFree", null);
                r["right"] = right;
                rows.Add(r);
            }
            b["rows"] = rows;
            return b;
        }

        #endregion

        #region Helpers

        private DateTime Now()
        {
            return clock();
        }

        private static string Iso(DateTime t)
        {
            return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        private bool Throttle(string key, int seconds)
        {
            DateTime now = Now();
            DateTime until;
            if (throttle.TryGetValue(key, out until) && until > now) return false;
            throttle[key] = now.AddSeconds(seconds);
            return true;
        }

        private static List<Player> OnlinePlayers()
        {
            var list = new List<Player>();
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private List<Player> OnlineMembers(string house)
        {
            var list = new List<Player>();
            foreach (Player p in OnlinePlayers())
            {
                string h = HouseOfPlayer(p);
                if (h != null && string.Equals(h, house, StringComparison.OrdinalIgnoreCase)) list.Add(p);
            }
            return list;
        }

        private string HouseOfPlayer(Player p)
        {
            if (p == null || RealmHouses == null) return null;
            string h;
            if (houseCache.TryGetValue(p.Id, out h)) return h;
            h = RealmHouses.Call("GetHouse", p.Id.ToString()) as string;
            if (string.IsNullOrEmpty(h)) h = null;
            houseCache[p.Id] = h;
            return h;
        }

        private int MemberCount(string house)
        {
            int n;
            string key = house.ToLowerInvariant();
            if (memberCountCache.TryGetValue(key, out n)) return n;
            var members = RealmHouses != null ? RealmHouses.Call("GetMembers", house) as List<string> : null;
            n = members != null ? members.Count : 0;
            memberCountCache[key] = n;
            return n;
        }

        private string HouseFoundedText(string house)
        {
            if (RealmHouses == null || string.IsNullOrEmpty(house)) return null;
            string f = RealmHouses.Call("GetHouseFounded", house) as string;
            return string.IsNullOrEmpty(f) ? null : f;
        }

        private bool HouseFounded(string house, out DateTime founded)
        {
            founded = DateTime.MinValue;
            string f = HouseFoundedText(house);
            return f != null && DateTime.TryParse(f, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out founded);
        }

        private string Liege(string house)
        {
            if (RealmHouses == null) return null;
            string l = RealmHouses.Call("GetLiege", house) as string;
            return string.IsNullOrEmpty(l) ? null : l;
        }

        // Same house, a treaty, liege and vassal, or sworn to the same liege.
        private bool Allied(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            string key = string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()) < 0
                ? a.ToLowerInvariant() + "|" + b.ToLowerInvariant() : b.ToLowerInvariant() + "|" + a.ToLowerInvariant();
            bool cached;
            if (allyCache.TryGetValue(key, out cached)) return cached;
            bool allied = CallBool(RealmHouses, "HasTreaty", a, b);
            if (!allied)
            {
                string la = Liege(a), lb = Liege(b);
                allied = (la != null && string.Equals(la, b, StringComparison.OrdinalIgnoreCase))
                    || (lb != null && string.Equals(lb, a, StringComparison.OrdinalIgnoreCase))
                    || (la != null && lb != null && string.Equals(la, lb, StringComparison.OrdinalIgnoreCase));
            }
            allyCache[key] = allied;
            return allied;
        }

        private int HoldingsOf(string house)
        {
            int n = 0;
            foreach (Holding h in data.Holdings)
                if (h.Owner != null && string.Equals(h.Owner, house, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        private string CanonicalHouse(string house)
        {
            if (RealmHouses != null)
            {
                var list = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
                if (list != null)
                    foreach (Dictionary<string, object> d in list)
                    {
                        object n;
                        if (d.TryGetValue("name", out n) && n is string && string.Equals((string)n, house, StringComparison.OrdinalIgnoreCase)) return (string)n;
                    }
            }
            return Clean(house, 40);
        }

        private void Chronicle(string title, string detail, string[] actors)
        {
            Puts("[" + ChronicleType + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            RealmChronicle.Call("Log", ChronicleType, title, detail, actors ?? new string[0]);
        }

        private Holding FindById(string id)
        {
            foreach (Holding h in data.Holdings) if (string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase)) return h;
            return null;
        }

        // By id, by full name (with or without "The"), then by a unique prefix of either.
        private Holding FindHolding(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.Trim().ToLowerInvariant();
            if (t.Length == 0) return null;
            Holding h = FindById(t);
            if (h != null) return h;
            foreach (Holding x in data.Holdings)
            {
                string n = x.Name.ToLowerInvariant();
                if (n == t || (n.StartsWith("the ") && n.Substring(4) == t)) return x;
            }
            Holding found = null;
            foreach (Holding x in data.Holdings)
            {
                string n = x.Name.ToLowerInvariant();
                string bare = n.StartsWith("the ") ? n.Substring(4) : n;
                if (x.Id.StartsWith(t) || n.StartsWith(t) || bare.StartsWith(t))
                {
                    if (found != null && found != x) return null;    // ambiguous
                    found = x;
                }
            }
            return found;
        }

        private bool IsAdmin(Player player)
        {
            return player != null && !player.IsServer && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private static bool TryPosition(Player p, out Vector3 pos)
        {
            pos = default(Vector3);
            try
            {
                if (p == null || p.Entity == null) return false;
                pos = p.Entity.Position;
                return !(float.IsNaN(pos.x) || float.IsNaN(pos.z));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Alive(Player p)
        {
            try { return p.IsAlive(); }
            catch (Exception) { return true; }               // unknown: the position check still applies
        }

        private static bool ParseClock(string s, out int minutes)
        {
            minutes = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Trim().Split(':');
            int h, m;
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            minutes = h * 60 + m;
            return true;
        }

        private static string Slug(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim().ToLowerInvariant();
            if (t.Length < 2 || t.Length > 24) return null;
            foreach (char c in t) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return null;
            return t;
        }

        // Display-safe text: no colour tags, no line breaks, capped length.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '[' || c == ']' || c == '{' || c == '}' || char.IsControl(c)) continue;
                sb.Append(c);
            }
            string t = sb.ToString().Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        private static string JoinFrom(string[] args, int start)
        {
            return start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start);
        }

        private static int MinutesUntil(DateTime when, DateTime now)
        {
            return Math.Max(0, (int)Math.Ceiling((when - now).TotalMinutes));
        }

        private static string Roman(int n)
        {
            string[] r = { "0", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
            return n >= 0 && n < r.Length ? r[n] : n.ToString(CultureInfo.InvariantCulture);
        }

        private static string Num(float f)
        {
            return f.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private static float ClampF(float v, float lo, float hi, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return v < lo ? lo : v > hi ? hi : v;
        }

        #endregion
    }
}
