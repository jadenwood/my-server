// RealmWarden: anti-grief and fair play for Ostreval. It FLAGS and applies configurable SOFT actions only.
// It never bans anyone. Every rule rests on a verified hook (tags as in docs/oxide-rok-api.md; [DEC] = read in the
// decompiled shipped patched Assembly-CSharp.dll, [IL] = read in its IL bodies):
//
//   New-player protection  For the first PlaytimeMinutes of online play (and at most MaxWallClockHours after first
//                          joining) other players cannot wound or bind a new player. OnEntityHealthChange
//                          (EntityDamageEvent) [OPJ L162], RB 1; victim = evt.Entity.Owner, attacker =
//                          evt.Damage.DamageSource.Owner [ASM]; blocked with evt.Cancel() + Damage.Amount = 0 + return
//                          true [USE NoFriendlyFire.cs:116-117]. Binding: OnPlayerCapture (PlayerCaptureEvent) [OPJ L711].
//                          A protected player who attacks, binds, breaks into a crest zone or takes the throne
//                          (OnThroneCaptured [OPJ L607]) loses the protection at once, so it cannot be used to grief.
//   Combat-log detection   Every unblocked player-on-player hit tags both sides as "in combat" for WindowSeconds.
//                          Leaving inside that window (OnPlayerDisconnected, core-dispatched [SRC]) is flagged,
//                          logged as evidence and queued as an admin alert. A death (OnEntityDeath [OPJ L188]) clears
//                          the tag. Mass disconnects during OnServerShutdown [OPJ L946] are ignored. The game leaves a
//                          sleeping body on logout that can still die ([DEC] CreateSleeperOnLogout.OnPlayerLeave ->
//                          ISleeper.Sleep; "Your character died while you were away"), which the optional note says.
//   Chat flood / spam      OnPlayerChat(PlayerMessageEvent) [SRC ReignOfKingsHooks.cs:62-83]: a non-null return makes
//                          the core cancel the message. [DEC] PlayerListener.OnPlayerMessage calls IOnPlayerChat in an
//                          if / else-if (guild chat OR other chat), so one message reaches this hook once. Rate, repeat,
//                          length and capitals limits give strikes; strikes give an escalating, timed Warden mute.
//   Name filter            OnPlayerConnected [SRC]: names are normalised (case, leet, punctuation) and matched against
//                          a configurable list. Soft actions: flag + alert + warn, or (opt-in) Server.Kick(Player,
//                          string) [ASM; DEC Server.Kick]. Never a ban.
//   Raid hours             OnCubeTakeDamage(CubeDamageEvent) [OPJ L292] is RB 0 and the game does NOT read Cancelled:
//                          [IL] CubeListener.OnCubeDamage calls the hook at IL_0000, then does
//                          BlockHealth.CurrentHealth -= evt.Damage.Amount (IL_005a-IL_0074). So a block is stopped by
//                          setting evt.Damage.Amount = 0 (no health lost, no CubeDestroyEvent) and also evt.Cancel(),
//                          which [DEC] EventManager.ServerSendEvent then sends back to the sender only, not to everyone.
//                          Salvage hits arrive already cancelled ([DEC] SalvageSupplier.OnCubeDamage, VeryEarly) and
//                          repairs have negative Amount ([DEC] RepairOnHit.GetDamageAmount); both are left alone. Damage
//                          to a crest zone by its own group is allowed, using the game's own siege test ([DEC]
//                          CrestSupplier.OnCubeDamage: CrestScheme.CurrentCrestGroup(world) vs SocialAPI.GetGroupId).
//   Admin queue, evidence  Alerts (capped, acknowledged by admins), an evidence log (capped, also written with
//                          LogToFile to oxide/logs/RealmWarden/), per-player flag counters, /warden report for players.
//
// Nothing here is posted to the public Chronicle: moderation records are private to admins.
//
// Data: oxide/data/RealmWarden.json. If it exists but cannot be parsed, the plugin keeps running from memory, turns
// new-player protection OFF (it would otherwise hand protection to everyone again) and NEVER writes the file, so a
// damaged record is not overwritten. Fix or remove it, then reload.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmWarden.md.

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.Blocks.Networking.Events;        // CubeDamageEvent [ASM]
using CodeHatch.Common;                          // PlayerExtensions.SendMessage/SendError, Vector3Int [ASM]
using CodeHatch.Damaging;                        // Damage, DamageType [ASM]
using CodeHatch.Engine.Core.Cache;               // Entity [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Networking.Events;               // PlayerCaptureEvent [ASM]
using CodeHatch.Networking.Events.Entities;      // EntityDamageEvent, EntityDeathEvent [ASM]
using CodeHatch.Networking.Events.Players;       // PlayerMessageEvent [ASM]
using CodeHatch.Thrones.AncientThrone;           // AncientThroneCaptureEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // CrestScheme [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]
using UnityEngine;                               // Vector3 [ASM]

namespace Oxide.Plugins
{
    [Info("RealmWarden", "Realm", "0.1.0")]
    [Description("Anti-grief and fair play: new-player protection, combat-log flags, chat flood limits, name filter, raid hours, admin alerts and evidence")]
    public class RealmWarden : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmwarden.admin";
        private const string DataName = "RealmWarden";
        private const float TickSeconds = 15f;
        private const int PageSize = 8;
        private const int MaxPlayersStored = 20000;
        private const int MaxExternalAlertsPerHour = 30;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private const string KCombat = "combat_log";
        private const string KChat = "chat_flood";
        private const string KName = "offensive_name";
        private const string KRaid = "raid_hours";
        private const string KReport = "report";
        private const string KProtection = "protection";
        private const string KAdmin = "admin_action";

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private bool shuttingDown;
        private bool dirty;
        private double lastSave;
        private double clockSkew;                         // seconds added to the clock; only the logic tests change it

        private readonly List<RaidWindow> raidWindows = new List<RaidWindow>();
        private readonly List<string> nameWordsAny = new List<string>();     // normalised, matched anywhere
        private readonly List<string> nameWordsWhole = new List<string>();   // normalised, whole words only

        // In-memory state (losing it on reload is harmless).
        private readonly Dictionary<ulong, CombatTag> combat = new Dictionary<ulong, CombatTag>();
        private readonly Dictionary<ulong, ChatState> chat = new Dictionary<ulong, ChatState>();
        private readonly Dictionary<ulong, double> lastAccrual = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, List<double>> raidHits = new Dictionary<ulong, List<double>>();
        private readonly Dictionary<string, double> throttle = new Dictionary<string, double>();
        private readonly Dictionary<ulong, bool> kicking = new Dictionary<ulong, bool>();
        private readonly List<double> adminChatTimes = new List<double>();
        private readonly List<double> externalAlertTimes = new List<double>();
        private int suppressedAdminChat;

        #region Config

        private class PluginConfig
        {
            public GeneralSettings General = new GeneralSettings();
            public ProtectionSettings NewPlayerProtection = new ProtectionSettings();
            public CombatSettings CombatLog = new CombatSettings();
            public ChatSettings Chat = new ChatSettings();
            public NameSettings Names = new NameSettings();
            public RaidSettings RaidHours = new RaidSettings();
            public AlertSettings Alerts = new AlertSettings();
            public ReportSettings Reports = new ReportSettings();
        }

        private class GeneralSettings
        {
            public bool AdminsExempt = true;                 // admins skip chat limits, name filter and raid hours
            public int SaveIntervalSeconds = 120;
        }

        private class ProtectionSettings
        {
            public bool Enabled = true;
            public int PlaytimeMinutes = 60;                 // online minutes of protection
            public int MaxWallClockHours = 48;               // protection ends this long after first join regardless
            public bool AttackingEndsProtection = true;      // hitting or binding a player forfeits protection
            public bool StructureDamageEndsProtection = true;// damaging another group's crest zone forfeits protection
            public bool ThroneEndsProtection = true;         // taking the throne forfeits protection
            public bool BlockCaptureOfProtected = true;      // ropes, chains and cages
            public bool SuspendDuringRebellion = false;      // CrownAndConsequences.IsRebellionActive
            public bool AllowOptOut = true;                  // /warden protection off confirm
        }

        private class CombatSettings
        {
            public bool Enabled = true;
            public int WindowSeconds = 30;
            public bool TagAttacker = true;                  // the one who hits is in combat too
            public bool WarnOnCombatStart = true;
            public bool SleeperNote = true;                  // tell the player (and admins) the body stays behind
            public bool TellOpponent = true;
            public int AlertCooldownMinutes = 10;            // per player
            public bool IgnoreSameGroup = true;              // hits between members of one game guild/group (sparring,
                                                             // stray friendly fire) never start a combat tag, so a
                                                             // housemate cannot get someone flagged as a combat logger
        }

        private class ChatSettings
        {
            public bool Enabled = true;
            public int MaxMessages = 5;                      // per WindowSeconds
            public int WindowSeconds = 8;
            public int MaxDuplicates = 3;                    // same text within DuplicateWindowSeconds
            public int DuplicateWindowSeconds = 30;
            public int MaxLength = 300;
            public int CapsMaxPercent = 0;                   // 0 = off; else block when caps share exceeds this
            public int CapsMinLetters = 12;
            public bool FilterWords = false;                 // also block chat containing a Names word
            public int StrikesToMute = 3;
            public int StrikeWindowSeconds = 120;
            public int MuteSeconds = 60;                     // first mute; doubles each repeat
            public int MaxMuteSeconds = 900;
            public int MuteLevelResetHours = 24;
            public int AlertAfterMutes = 2;                  // within 24 h
        }

        private class NameSettings
        {
            public bool Enabled = true;
            // Entries are matched after normalising (lower case, leet digits to letters, punctuation removed).
            // An entry starting with "=" matches whole words only. Add the terms your community will not accept.
            public List<string> Words = new List<string> { "=admin", "=administrator", "=moderator", "=server", "=owner", "=gm" };
            public bool NormalizeLeet = true;
            public string Action = "flag";                   // "flag" (flag + alert + warn) or "kick" (also kick; never ban)
            public int KickDelaySeconds = 5;
            public bool WarnPlayer = true;
            public List<string> ExemptIds = new List<string>();
        }

        private class RaidWindowDef
        {
            public List<string> Days = new List<string>();   // Mon..Sun, or "*"
            public string Start = "18:00";
            public string End = "23:00";                     // End <= Start crosses midnight
        }

        private class RaidSettings
        {
            public bool Enabled = false;                     // opt in: UNVERIFIED in game
            public bool Block = true;                        // false = only record and alert
            public int UtcOffsetMinutes = 0;                 // windows are in UTC + this offset
            public List<RaidWindowDef> Windows = new List<RaidWindowDef>
            {
                new RaidWindowDef { Days = new List<string> { "Wed", "Sat" }, Start = "18:00", End = "23:00" },
                new RaidWindowDef { Days = new List<string> { "Sun" }, Start = "14:00", End = "20:00" }
            };
            public bool AllowDuringRebellion = true;         // CrownAndConsequences rebellion windows open raiding
            public bool ProtectUnclaimed = false;            // also protect blocks outside every crest zone
            public bool OwnersMayDamageOwn = true;           // a crest's own group may break its own blocks
            public bool BlockUnknownAttacker = true;         // inside a crest zone, block hits with no known player
            public int NotifySeconds = 15;                   // per attacker
            public int EvidenceCooldownMinutes = 5;          // per attacker
            public int AlertAfterBlockedHits = 30;           // within 10 minutes
        }

        private class AlertSettings
        {
            public int MaxStored = 500;
            public int MaxEvidence = 2000;
            public bool ChatToOnlineAdmins = true;
            public int MaxChatAlertsPerMinute = 6;
            public bool RemindOnAdminJoin = true;
            public bool LogToFile = true;
        }

        private class ReportSettings
        {
            public bool Enabled = true;
            public int CooldownSeconds = 120;
            public int MaxPerDay = 10;
            public int SameTargetCooldownMinutes = 60;
            public int MinReasonLength = 3;
            public int MaxReasonLength = 200;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void ClampConfig()
        {
            if (config.General == null) config.General = new GeneralSettings();
            if (config.NewPlayerProtection == null) config.NewPlayerProtection = new ProtectionSettings();
            if (config.CombatLog == null) config.CombatLog = new CombatSettings();
            if (config.Chat == null) config.Chat = new ChatSettings();
            if (config.Names == null) config.Names = new NameSettings();
            if (config.RaidHours == null) config.RaidHours = new RaidSettings();
            if (config.Alerts == null) config.Alerts = new AlertSettings();
            if (config.Reports == null) config.Reports = new ReportSettings();
            if (config.Names.Words == null) config.Names.Words = new List<string>();
            if (config.Names.ExemptIds == null) config.Names.ExemptIds = new List<string>();
            if (config.Names.Action == null) config.Names.Action = "flag";
            if (config.RaidHours.Windows == null) config.RaidHours.Windows = new List<RaidWindowDef>();

            config.General.SaveIntervalSeconds = Clamp(config.General.SaveIntervalSeconds, 30, 3600);
            ProtectionSettings p = config.NewPlayerProtection;
            p.PlaytimeMinutes = Clamp(p.PlaytimeMinutes, 0, 1440);
            p.MaxWallClockHours = Clamp(p.MaxWallClockHours, 1, 720);
            CombatSettings c = config.CombatLog;
            c.WindowSeconds = Clamp(c.WindowSeconds, 5, 300);
            c.AlertCooldownMinutes = Clamp(c.AlertCooldownMinutes, 0, 1440);
            ChatSettings ch = config.Chat;
            ch.MaxMessages = Clamp(ch.MaxMessages, 1, 100);
            ch.WindowSeconds = Clamp(ch.WindowSeconds, 1, 300);
            ch.MaxDuplicates = Clamp(ch.MaxDuplicates, 1, 100);
            ch.DuplicateWindowSeconds = Clamp(ch.DuplicateWindowSeconds, 1, 3600);
            ch.MaxLength = Clamp(ch.MaxLength, 10, 2000);
            ch.CapsMaxPercent = Clamp(ch.CapsMaxPercent, 0, 100);
            ch.CapsMinLetters = Clamp(ch.CapsMinLetters, 1, 500);
            ch.StrikesToMute = Clamp(ch.StrikesToMute, 1, 50);
            ch.StrikeWindowSeconds = Clamp(ch.StrikeWindowSeconds, 5, 3600);
            ch.MuteSeconds = Clamp(ch.MuteSeconds, 5, 86400);
            ch.MaxMuteSeconds = Clamp(ch.MaxMuteSeconds, ch.MuteSeconds, 86400);
            ch.MuteLevelResetHours = Clamp(ch.MuteLevelResetHours, 1, 720);
            ch.AlertAfterMutes = Clamp(ch.AlertAfterMutes, 1, 100);
            NameSettings n = config.Names;
            n.KickDelaySeconds = Clamp(n.KickDelaySeconds, 1, 60);
            if (n.Action != "flag" && n.Action != "kick")
            {
                PrintWarning("Names.Action must be \"flag\" or \"kick\"; using \"flag\".");
                n.Action = "flag";
            }
            if (n.Words.Count > 500) n.Words.RemoveRange(500, n.Words.Count - 500);
            RaidSettings r = config.RaidHours;
            r.UtcOffsetMinutes = Clamp(r.UtcOffsetMinutes, -720, 840);
            r.NotifySeconds = Clamp(r.NotifySeconds, 1, 3600);
            r.EvidenceCooldownMinutes = Clamp(r.EvidenceCooldownMinutes, 0, 1440);
            r.AlertAfterBlockedHits = Clamp(r.AlertAfterBlockedHits, 1, 10000);
            if (r.Windows.Count > 50) r.Windows.RemoveRange(50, r.Windows.Count - 50);
            AlertSettings a = config.Alerts;
            a.MaxStored = Clamp(a.MaxStored, 10, 5000);
            a.MaxEvidence = Clamp(a.MaxEvidence, 10, 20000);
            a.MaxChatAlertsPerMinute = Clamp(a.MaxChatAlertsPerMinute, 1, 60);
            ReportSettings rp = config.Reports;
            rp.CooldownSeconds = Clamp(rp.CooldownSeconds, 0, 86400);
            rp.MaxPerDay = Clamp(rp.MaxPerDay, 1, 200);
            rp.SameTargetCooldownMinutes = Clamp(rp.SameTargetCooldownMinutes, 0, 10080);
            rp.MaxReasonLength = Clamp(rp.MaxReasonLength, 10, 500);
            rp.MinReasonLength = Clamp(rp.MinReasonLength, 0, rp.MaxReasonLength);
        }

        private class RaidWindow
        {
            public bool[] Days = new bool[7];                // indexed by (int)DayOfWeek
            public int StartMinute;
            public int DurationMinutes;
        }

        private void BuildCaches()
        {
            raidWindows.Clear();
            foreach (RaidWindowDef def in config.RaidHours.Windows)
            {
                if (def == null) continue;
                int s, e;
                if (!ParseClock(def.Start, out s) || !ParseClock(def.End, out e))
                {
                    PrintWarning("Raid window with bad Start/End (use HH:mm) ignored: " + def.Start + "-" + def.End);
                    continue;
                }
                RaidWindow w = new RaidWindow();
                w.StartMinute = s;
                w.DurationMinutes = e > s ? e - s : e + 1440 - s;   // End <= Start crosses midnight; equal = 24 h
                bool any = false;
                if (def.Days != null)
                {
                    foreach (string d in def.Days)
                    {
                        int idx = DayIndex(d);
                        if (idx == 7) { for (int i = 0; i < 7; i++) w.Days[i] = true; any = true; }
                        else if (idx >= 0) { w.Days[idx] = true; any = true; }
                        else PrintWarning("Unknown day in raid window ignored: " + d);
                    }
                }
                if (any) raidWindows.Add(w);
            }

            nameWordsAny.Clear();
            nameWordsWhole.Clear();
            foreach (string raw in config.Names.Words)
            {
                if (raw == null) continue;
                string t = raw.Trim();
                bool whole = t.StartsWith("=");
                if (whole) t = t.Substring(1);
                string norm = NormalizeCompact(t);
                if (norm.Length < 2) continue;                    // one letter would match almost every name
                if (whole) { if (!nameWordsWhole.Contains(norm)) nameWordsWhole.Add(norm); }
                else if (!nameWordsAny.Contains(norm)) nameWordsAny.Add(norm);
            }
        }

        private static bool ParseClock(string s, out int minutes)
        {
            minutes = 0;
            if (s == null) return false;
            string[] parts = s.Trim().Split(':');
            if (parts.Length != 2) return false;
            int h, m;
            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m)) return false;
            if (h < 0 || h > 24 || m < 0 || m > 59 || (h == 24 && m != 0)) return false;
            minutes = h * 60 + m;
            if (minutes == 1440) minutes = 0;
            return true;
        }

        private static int DayIndex(string d)
        {
            if (d == null) return -1;
            string t = d.Trim().ToLowerInvariant();
            if (t == "*" || t == "daily" || t == "all") return 7;
            if (t.Length < 3) return -1;
            switch (t.Substring(0, 3))
            {
                case "sun": return 0;
                case "mon": return 1;
                case "tue": return 2;
                case "wed": return 3;
                case "thu": return 4;
                case "fri": return 5;
                case "sat": return 6;
            }
            return -1;
        }

        #endregion

        #region Data

        private class PlayerRec
        {
            public string Name = "";
            public long FirstSeen;
            public long LastSeen;
            public double Playtime;                          // seconds online
            public bool ProtectionEnded;
            public string ProtectionEndReason = "";
            public long ProtectedUntil;                      // admin grant, overrides the normal window
            public bool ProtectionExpiryNotified;
            public Dictionary<string, int> Flags = new Dictionary<string, int>();
            public long LastFlagAt;
            public long MutedUntil;
            public int MuteLevel;
            public long LastMuteAt;
            public List<long> MuteTimes = new List<long>();
            public List<long> ReportTimes = new List<long>();
            public Dictionary<string, long> ReportedAt = new Dictionary<string, long>();
            public long LastCombatAlert;
        }

        private class Alert
        {
            public int Id;
            public long Ts;
            public string Kind = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public string Detail = "";
            public bool Ack;
            public string AckBy = "";
        }

        private class Evidence
        {
            public int Id;
            public long Ts;
            public string Kind = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public string OtherId = "";
            public string OtherName = "";
            public string Detail = "";
            public string Pos = "";
        }

        private class StoredData
        {
            public int Version = 1;
            public int NextAlertId = 1;
            public int NextEvidenceId = 1;
            public Dictionary<string, PlayerRec> Players = new Dictionary<string, PlayerRec>();
            public List<Alert> Alerts = new List<Alert>();
            public List<Evidence> Evidence = new List<Evidence>();
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
                data = new StoredData();
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". Running from memory with new-player protection OFF; the file will NOT be overwritten. Fix or remove it, then reload.");
                return;
            }
            if (loaded == null && existed)
            {
                loadFailed = true;
                data = new StoredData();
                PrintError("oxide/data/" + DataName + ".json is empty or null. Running from memory; the file will NOT be overwritten.");
                return;
            }
            data = loaded ?? new StoredData();
            if (data.Players == null) data.Players = new Dictionary<string, PlayerRec>();
            if (data.Alerts == null) data.Alerts = new List<Alert>();
            if (data.Evidence == null) data.Evidence = new List<Evidence>();
            List<string> bad = new List<string>();
            foreach (KeyValuePair<string, PlayerRec> kv in data.Players)
            {
                if (kv.Value == null) { bad.Add(kv.Key); continue; }
                PlayerRec r = kv.Value;
                if (r.Name == null) r.Name = "";
                if (r.ProtectionEndReason == null) r.ProtectionEndReason = "";
                if (r.Flags == null) r.Flags = new Dictionary<string, int>();
                if (r.MuteTimes == null) r.MuteTimes = new List<long>();
                if (r.ReportTimes == null) r.ReportTimes = new List<long>();
                if (r.ReportedAt == null) r.ReportedAt = new Dictionary<string, long>();
            }
            foreach (string k in bad) data.Players.Remove(k);
            data.Alerts.RemoveAll(delegate(Alert a) { return a == null || a.Kind == null; });
            data.Evidence.RemoveAll(delegate(Evidence e) { return e == null || e.Kind == null; });
            foreach (Alert a in data.Alerts)
            {
                if (a.PlayerId == null) a.PlayerId = "";
                if (a.PlayerName == null) a.PlayerName = "";
                if (a.Detail == null) a.Detail = "";
                if (a.AckBy == null) a.AckBy = "";
                if (a.Id >= data.NextAlertId) data.NextAlertId = a.Id + 1;
            }
            foreach (Evidence e in data.Evidence)
            {
                if (e.PlayerId == null) e.PlayerId = "";
                if (e.PlayerName == null) e.PlayerName = "";
                if (e.OtherId == null) e.OtherId = "";
                if (e.OtherName == null) e.OtherName = "";
                if (e.Detail == null) e.Detail = "";
                if (e.Pos == null) e.Pos = "";
                if (e.Id >= data.NextEvidenceId) data.NextEvidenceId = e.Id + 1;
            }
        }

        private void SaveData()
        {
            if (data == null || loadFailed) return;              // never overwrite the file after a failed load
            PrunePlayers();
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            lastSave = NowSec();
        }

        // Keeps the player table bounded: drops the longest-unseen players that have no flags first.
        private void PrunePlayers()
        {
            if (data.Players.Count <= MaxPlayersStored) return;
            List<KeyValuePair<string, PlayerRec>> all = new List<KeyValuePair<string, PlayerRec>>(data.Players);
            all.Sort(delegate(KeyValuePair<string, PlayerRec> a, KeyValuePair<string, PlayerRec> b)
            {
                int fa = a.Value.Flags.Count > 0 ? 1 : 0, fb = b.Value.Flags.Count > 0 ? 1 : 0;
                if (fa != fb) return fa.CompareTo(fb);
                return a.Value.LastSeen.CompareTo(b.Value.LastSeen);
            });
            int remove = data.Players.Count - MaxPlayersStored;
            for (int i = 0; i < remove; i++) data.Players.Remove(all[i].Key);
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Prefix", "[7090B0]Warden[FFFFFF]: " },
                { "Help1", "/warden status - your protection, mute and combat state, and the raid hours." },
                { "Help2", "/warden rules - the fair-play rules. /warden report <player> <reason> - tell the admins (quote names with spaces)." },
                { "Help3", "/warden protection off confirm - give up your new-player protection early." },
                { "HelpAdmin1", "Admin: /warden alerts [all] [page] | /warden ack <id|all> | /warden player <player> | /warden evidence [player] [page]" },
                { "HelpAdmin2", "Admin: /warden protect <player> <minutes|off> | /warden mute <player> <minutes> | /warden unmute <player> | /warden clear <player> | /warden raid" },
                { "NoPermission", "You may not do that." },
                { "PlayerNotFound", "No one by that name is online or in the Warden's records." },
                { "Ambiguous", "More than one player matches '{0}'. Use more of the name or their Steam ID." },
                { "BadNumber", "'{0}' is not a whole number from {1} to {2}." },
                { "DataDamaged", "The Warden's records are damaged (oxide/data/RealmWarden.json). Nothing is being saved and new-player protection is off until an admin repairs it." },
                { "Welcome", "Welcome to Ostreval. You are under new-player protection for {0} of play: other players cannot wound or bind you. Attacking anyone ends it. /warden status" },
                { "ProtectionLeft", "New-player protection: {0} left." },
                { "ProtectionNone", "New-player protection: none." },
                { "ProtectionEndedMsg", "Your new-player protection has ended ({0}). Other players can now attack you." },
                { "ProtectionExpired", "Your new-player protection has run out. Other players can now attack you." },
                { "ProtectedTarget", "{0} is under new-player protection and cannot be harmed." },
                { "ProtectedCapture", "{0} is under new-player protection and cannot be bound." },
                { "OptOutConfirm", "This cannot be undone. Type /warden protection off confirm to give up your protection." },
                { "OptOutDisabled", "Giving up protection early is turned off on this server." },
                { "NotProtected", "You are not under new-player protection." },
                { "Muted", "You are muted by the Warden for {0}." },
                { "NotMuted", "You are not muted." },
                { "ChatRate", "Slow down: at most {0} messages every {1} s." },
                { "ChatDuplicate", "Do not repeat the same message." },
                { "ChatLength", "That message is too long (at most {0} characters)." },
                { "ChatCaps", "Too many capital letters." },
                { "ChatWord", "That message contains a word that is not allowed here." },
                { "MutedNow", "You are muted for {0} for flooding chat. Repeats make it longer." },
                { "CombatStart", "You are in combat. Leaving the game in the next {0} s will be flagged for the admins." },
                { "CombatState", "In combat: {0} s left." },
                { "CombatNone", "Not in combat." },
                { "SleeperNote", "Your body stays in the world while you are away and can still be killed." },
                { "OpponentLeft", "{0} left the game in the middle of the fight. The admins have been told." },
                { "NameWarn", "Your name '{0}' is not allowed here. Please change it in Steam and rejoin. The admins have been told." },
                { "NameKick", "Name not allowed on this server. Change it in Steam and rejoin." },
                { "RaidOpen", "Raid hours: OPEN now (until {0})." },
                { "RaidClosed", "Raid hours: closed. Next window: {0}." },
                { "RaidClosedNone", "Raid hours: closed. No window is configured." },
                { "RaidOff", "Raid hours: not enforced; structures can be attacked at any time." },
                { "RaidRebellion", "Raid hours: OPEN while a rebellion window is open." },
                { "RaidBlocked", "Outside raid hours: structures in a claimed zone cannot be damaged. /warden status" },
                { "Rules1", "Fair play: new players are protected for {0} of play; leaving within {1} s of a fight is flagged; chat flooding gets a timed mute." },
                { "Rules2", "Names that impersonate staff or that the community has banned are flagged. No one is banned automatically; the admins decide." },
                { "ReportDisabled", "Reports are turned off on this server." },
                { "ReportUsage", "Usage: /warden report <player> <reason>. Quote names with spaces." },
                { "ReportSelf", "You cannot report yourself." },
                { "ReportReason", "Give a reason of {0} to {1} characters." },
                { "ReportCooldown", "You can send another report in {0}." },
                { "ReportDaily", "You have sent {0} reports today; that is the limit." },
                { "ReportSameTarget", "You already reported that player recently." },
                { "ReportSent", "Report #{0} sent to the admins. Thank you." },
                { "AlertsNone", "No alerts." },
                { "AlertsHeader", "Alerts ({0} unread, {1} stored) page {2}/{3}:" },
                { "AlertLine", "#{0} {1} {2} {3}: {4}{5}" },
                { "AlertChat", "[FF8040]#{0} {1}[FFFFFF] {2}: {3}" },
                { "AlertOverflow", "+{0} more alerts were queued. /warden alerts" },
                { "AlertsUnread", "{0} unread Warden alerts. /warden alerts" },
                { "AckDone", "Acknowledged {0} alert(s)." },
                { "AckNone", "No such unread alert." },
                { "EvidenceNone", "No evidence recorded." },
                { "EvidenceHeader", "Evidence page {0}/{1}:" },
                { "EvidenceLine", "#{0} {1} {2} {3}: {4}{5}" },
                { "PlayerHeader", "{0} ({1}) first seen {2}, last seen {3}, played {4}." },
                { "PlayerFlags", "Flags: {0}" },
                { "PlayerState", "Protection: {0}. Mute: {1}. Combat: {2}." },
                { "None", "none" },
                { "ProtectSet", "{0} is protected for {1}." },
                { "ProtectOff", "{0}'s protection removed." },
                { "MuteSet", "{0} is muted for {1}." },
                { "UnmuteSet", "{0} is unmuted." },
                { "ClearDone", "Cleared the flags and mute of {0}. The evidence log is kept." },
                { "RaidAdmin", "Raid hours enforced: {0}, blocking: {1}, UTC offset {2} min, {3} window(s), rebellion opens raids: {4}." }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        private string Fmt(string key, Player player, object[] args)
        {
            string text = Msg(key, player);
            return args != null && args.Length > 0 ? string.Format(text, args) : text;
        }

        private void Reply(Player player, string key, params object[] args)
        {
            player.SendMessage(Msg("Prefix", player) + Fmt(key, player, args));      // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Fmt(key, player, args));
        }

        private bool Throttled(string key, double seconds)
        {
            double now = NowSec();
            double last;
            if (throttle.TryGetValue(key, out last) && now - last < seconds) return true;
            throttle[key] = now;
            if (throttle.Count > 5000) throttle.Clear();
            return false;
        }

        private void ReplyThrottled(Player player, double seconds, string key, params object[] args)
        {
            if (Throttled(player.Id + "|" + key, seconds)) return;
            ReplyError(player, key, args);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            if (config == null) config = new PluginConfig();
            ClampConfig();
            BuildCaches();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                         // re-sent on hot load; keep idempotent
            initialized = true;
            lastSave = NowSec();
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                Touch(p);
                lastAccrual[p.Id] = NowSec();
            }
            timer.Every(TickSeconds, Tick);
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnServerShutdown()
        {
            shuttingDown = true;
            SaveData();
        }

        private void Unload()
        {
            AccrueAll();
            SaveData();
        }

        private void Tick()
        {
            try
            {
                AccrueAll();
                if (dirty && NowSec() - lastSave >= config.General.SaveIntervalSeconds) SaveData();
            }
            catch (Exception ex)
            {
                PrintError("Tick failed: " + ex.Message);
            }
        }

        // Adds online time to every online player and tells anyone whose protection just ran out.
        private void AccrueAll()
        {
            if (data == null) return;
            double now = NowSec();
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                Accrue(p, now);
                PlayerRec r = Rec(p.Id);
                if (r == null || r.ProtectionExpiryNotified || r.ProtectionEnded) continue;
                if (!IsProtectedRec(r, now) && config.NewPlayerProtection.Enabled && !loadFailed)
                {
                    r.ProtectionExpiryNotified = true;
                    dirty = true;
                    Reply(p, "ProtectionExpired");
                }
            }
        }

        private void Accrue(Player p, double now)
        {
            PlayerRec r = Rec(p.Id);
            if (r == null) return;
            double last;
            if (lastAccrual.TryGetValue(p.Id, out last))
            {
                double add = now - last;
                if (add > 0) r.Playtime += Math.Min(add, TickSeconds * 4);   // a stalled timer never grants hours at once
            }
            lastAccrual[p.Id] = now;
            r.LastSeen = (long)now;
            dirty = true;
        }

        #endregion

        #region Players and connection

        private PlayerRec Rec(ulong id)
        {
            PlayerRec r;
            return data != null && data.Players.TryGetValue(id.ToString(), out r) ? r : null;
        }

        private PlayerRec Touch(Player p)
        {
            string id = p.Id.ToString();
            PlayerRec r;
            long now = (long)NowSec();
            if (!data.Players.TryGetValue(id, out r))
            {
                r = new PlayerRec();
                r.FirstSeen = now;
                data.Players[id] = r;
            }
            r.Name = Clean(p.Name, 64);
            r.LastSeen = now;
            dirty = true;
            return r;
        }

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer || data == null) return;
            try
            {
                bool isNew = Rec(player.Id) == null;
                PlayerRec r = Touch(player);
                lastAccrual[player.Id] = NowSec();
                kicking.Remove(player.Id);
                if (IsAdmin(player))
                {
                    if (loadFailed) ReplyError(player, "DataDamaged");
                    int unread = UnreadCount();
                    if (config.Alerts.RemindOnAdminJoin && unread > 0) Reply(player, "AlertsUnread", unread);
                }
                if (isNew && IsProtectedRec(r, NowSec()))
                    Reply(player, "Welcome", Dur(config.NewPlayerProtection.PlaytimeMinutes * 60));
                CheckName(player);
            }
            catch (Exception ex)
            {
                PrintError("Connect check failed: " + ex.Message);
            }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null || player.IsServer || data == null) return;
            try
            {
                double now = NowSec();
                Accrue(player, now);
                lastAccrual.Remove(player.Id);
                chat.Remove(player.Id);
                raidHits.Remove(player.Id);
                bool wasKicked = kicking.Remove(player.Id);
                CombatTag tag;
                if (combat.TryGetValue(player.Id, out tag))
                {
                    combat.Remove(player.Id);
                    if (!shuttingDown && !wasKicked && config.CombatLog.Enabled && now - tag.LastHit <= config.CombatLog.WindowSeconds)
                        CombatLogged(player, tag, now);
                }
            }
            catch (Exception ex)
            {
                PrintError("Disconnect check failed: " + ex.Message);
            }
        }

        #endregion

        #region New-player protection

        private bool IsProtectedRec(PlayerRec r, double now)
        {
            if (r == null || loadFailed || !config.NewPlayerProtection.Enabled) return false;
            if (r.ProtectedUntil > now) return true;
            if (r.ProtectionEnded) return false;
            if (r.Playtime >= config.NewPlayerProtection.PlaytimeMinutes * 60.0) return false;
            if (now - r.FirstSeen >= config.NewPlayerProtection.MaxWallClockHours * 3600.0) return false;
            return true;
        }

        private bool IsProtectedId(ulong id)
        {
            return IsProtectedRec(Rec(id), NowSec());
        }

        private double ProtectionLeft(PlayerRec r, double now)
        {
            if (!IsProtectedRec(r, now)) return 0;
            double left = 0;
            if (!r.ProtectionEnded)
            {
                double byPlay = config.NewPlayerProtection.PlaytimeMinutes * 60.0 - r.Playtime;
                double byClock = config.NewPlayerProtection.MaxWallClockHours * 3600.0 - (now - r.FirstSeen);
                left = Math.Max(0, Math.Min(byPlay, byClock));
            }
            if (r.ProtectedUntil > now) left = Math.Max(left, r.ProtectedUntil - now);
            return left;
        }

        private void EndProtection(Player p, string reason, bool asEvidence)
        {
            PlayerRec r = Rec(p.Id);
            if (r == null || !IsProtectedRec(r, NowSec())) return;
            r.ProtectionEnded = true;
            r.ProtectedUntil = 0;
            r.ProtectionEndReason = Clean(reason, 80);
            r.ProtectionExpiryNotified = true;
            dirty = true;
            Reply(p, "ProtectionEndedMsg", reason);
            if (asEvidence) AddEvidence(KProtection, p, null, "protection forfeited: " + reason);
        }

        private bool ProtectionSuspended()
        {
            return config.NewPlayerProtection.SuspendDuringRebellion && RebellionActive();
        }

        #endregion

        #region Combat hooks

        private class CombatTag
        {
            public double LastHit;
            public ulong OtherId;
            public string OtherName = "";
            public bool WasVictim;
        }

        // RB 1 [OPJ L162]. Runs for every damage event; returns early for anything that is not player-on-player.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled) return null;
            try
            {
                Entity ve = evt.Entity;
                if (ve == null || !ve.IsPlayer) return null;
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f || d.DamageSource == null || !d.DamageSource.IsPlayer) return null;
                Player victim = ve.Owner, attacker = d.DamageSource.Owner;
                if (victim == null || attacker == null || victim.IsServer || attacker.IsServer || victim.Id == attacker.Id) return null;
                double now = NowSec();

                if (!ProtectionSuspended())
                {
                    if (config.NewPlayerProtection.AttackingEndsProtection && IsProtectedRec(Rec(attacker.Id), now)
                        && !IsProtectedRec(Rec(victim.Id), now))
                        EndProtection(attacker, "attacked " + Clean(victim.Name, 40), true);
                    if (IsProtectedRec(Rec(victim.Id), now))
                    {
                        evt.Cancel("New-player protection");
                        d.Amount = 0f;
                        ReplyThrottled(attacker, 10, "ProtectedTarget", Clean(victim.Name, 40));
                        return true;
                    }
                }

                if (config.CombatLog.Enabled && !(config.CombatLog.IgnoreSameGroup && SameGroup(victim, attacker)))
                {
                    Tag(victim, attacker, now, true);
                    if (config.CombatLog.TagAttacker) Tag(attacker, victim, now, false);
                }
                return null;
            }
            catch (Exception ex)
            {
                PrintError("Damage check failed: " + ex.Message);
                return null;
            }
        }

        // Same non-zero game group ([DEC] SocialAPI.GetGroupId, the test the game's own crest siege check uses).
        private static bool SameGroup(Player a, Player b)
        {
            ulong ga = SocialAPI.GetGroupId(a.Id);
            return ga != 0UL && ga == SocialAPI.GetGroupId(b.Id);
        }

        private void Tag(Player p, Player other, double now, bool wasVictim)
        {
            CombatTag tag;
            bool fresh = !combat.TryGetValue(p.Id, out tag) || now - tag.LastHit > config.CombatLog.WindowSeconds;
            if (tag == null) { tag = new CombatTag(); combat[p.Id] = tag; }
            tag.LastHit = now;
            tag.OtherId = other.Id;
            tag.OtherName = Clean(other.Name, 40);
            tag.WasVictim = wasVictim;
            if (fresh && config.CombatLog.WarnOnCombatStart)
            {
                ReplyThrottled(p, 60, "CombatStart", config.CombatLog.WindowSeconds);
                if (config.CombatLog.SleeperNote) ReplyThrottled(p, 60, "SleeperNote");
            }
        }

        // RB 1 [OPJ L188]. Clears the combat tag of a player who died: dying and leaving is not a combat log.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (evt == null || evt.Entity == null || !evt.Entity.IsPlayer) return null;
            Player p = evt.Entity.Owner;
            if (p != null) combat.Remove(p.Id);
            return null;
        }

        private void CombatLogged(Player p, CombatTag tag, double now)
        {
            PlayerRec r = Rec(p.Id);
            if (r == null) return;
            int secs = (int)Math.Round(now - tag.LastHit);
            string detail = "left " + secs + " s after " + (tag.WasVictim ? "being hit by " : "hitting ") + tag.OtherName;
            if (config.CombatLog.SleeperNote) detail += "; their body stays in the world as a sleeper and can still be killed";
            AddFlag(r, KCombat);
            Player other = Server.GetPlayerById(tag.OtherId);
            AddEvidence(KCombat, p, other, detail);
            if (r.LastCombatAlert == 0 || now - r.LastCombatAlert >= config.CombatLog.AlertCooldownMinutes * 60.0)
            {
                r.LastCombatAlert = (long)now;
                AddAlert(KCombat, p.Id.ToString(), r.Name, detail + " (combat logs: " + FlagCount(r, KCombat) + ")");
            }
            if (config.CombatLog.TellOpponent && other != null && !other.IsServer) Reply(other, "OpponentLeft", r.Name);
        }

        // RB 1 [OPJ L711]. Protected players cannot be bound; a protected player who binds someone loses protection.
        private object OnPlayerCapture(PlayerCaptureEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled || evt.Target == null) return null;
            try
            {
                if (evt.Captor == null || !evt.Captor.IsPlayer || evt.Captor.Owner == null) return null;
                Player captor = evt.Captor.Owner, target = evt.Target;
                if (captor.IsServer || target.IsServer || captor.Id == target.Id || ProtectionSuspended()) return null;
                double now = NowSec();
                if (config.NewPlayerProtection.BlockCaptureOfProtected && IsProtectedRec(Rec(target.Id), now))
                {
                    evt.Cancel("New-player protection");
                    ReplyThrottled(captor, 10, "ProtectedCapture", Clean(target.Name, 40));
                    return true;
                }
                if (config.NewPlayerProtection.AttackingEndsProtection && IsProtectedRec(Rec(captor.Id), now))
                    EndProtection(captor, "bound " + Clean(target.Name, 40), true);
                return null;
            }
            catch (Exception ex)
            {
                PrintError("Capture check failed: " + ex.Message);
                return null;
            }
        }

        // RB 0 [OPJ L607]. A protected monarch would be untouchable, so taking the throne ends protection.
        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled || evt.Player == null) return;
            if (evt.State != AncientThroneCaptureEvent.States.Completed) return;
            if (config.NewPlayerProtection.ThroneEndsProtection) EndProtection(evt.Player, "took the throne", true);
        }

        #endregion

        #region Raid hours

        // RB 0 [OPJ L292]. See the header: blocking = Damage.Amount = 0 (the only thing the game's listener reads) + Cancel.
        private void OnCubeTakeDamage(CubeDamageEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled) return;
            bool raidRules = config.RaidHours.Enabled;
            bool protRules = config.NewPlayerProtection.Enabled && config.NewPlayerProtection.StructureDamageEndsProtection;
            if (!raidRules && !protRules) return;
            try
            {
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f) return;
                if ((d.DamageTypes & (DamageType.Salvage | DamageType.Healing)) != 0) return;
                Player attacker = null;
                if (d.DamageSource != null && d.DamageSource.Owner != null && !d.DamageSource.Owner.IsServer) attacker = d.DamageSource.Owner;
                else if (evt.Sender != null && !evt.Sender.IsServer) attacker = evt.Sender;   // UNVERIFIED which side sends it

                Vector3 world;
                if (evt.Grid == null) return;
                world = evt.Grid.LocalToWorldCoordinate(evt.Position);
                CrestScheme crests = SocialAPI.Get<CrestScheme>();
                ulong crestGroup = crests != null ? crests.CurrentCrestGroup(world) : 0UL;
                bool ownGroup = attacker != null && crestGroup != 0 && SocialAPI.GetGroupId(attacker.Id) == crestGroup;

                if (protRules && attacker != null && crestGroup != 0 && !ownGroup && IsProtectedId(attacker.Id))
                    EndProtection(attacker, "damaged another group's structure", true);

                if (!raidRules) return;
                if (crestGroup == 0 && !config.RaidHours.ProtectUnclaimed) return;
                if (ownGroup && config.RaidHours.OwnersMayDamageOwn) return;
                if (attacker == null && !config.RaidHours.BlockUnknownAttacker) return;
                if (attacker != null && config.General.AdminsExempt && IsAdmin(attacker)) return;
                if (RaidAllowedNow()) return;

                if (config.RaidHours.Block)
                {
                    d.Amount = 0f;
                    evt.Cancel("Outside raid hours");
                }
                if (attacker != null) RaidAttempt(attacker, world, crestGroup);
            }
            catch (Exception ex)
            {
                PrintError("Structure damage check failed: " + ex.Message);
            }
        }

        private void RaidAttempt(Player attacker, Vector3 world, ulong crestGroup)
        {
            double now = NowSec();
            if (config.RaidHours.Block) ReplyThrottled(attacker, config.RaidHours.NotifySeconds, "RaidBlocked");
            List<double> hits;
            if (!raidHits.TryGetValue(attacker.Id, out hits)) { hits = new List<double>(); raidHits[attacker.Id] = hits; }
            hits.Add(now);
            hits.RemoveAll(delegate(double t) { return now - t > 600; });
            if (hits.Count > 2000) hits.RemoveRange(0, hits.Count - 2000);
            string detail = (config.RaidHours.Block ? "blocked" : "allowed (record only)") + " structure damage outside raid hours in crest group "
                + crestGroup + " at " + Pos(world);
            if (!Throttled("raidev|" + attacker.Id, config.RaidHours.EvidenceCooldownMinutes * 60.0))
                AddEvidence(KRaid, attacker, null, detail);
            if (hits.Count >= config.RaidHours.AlertAfterBlockedHits && !Throttled("raidal|" + attacker.Id, 1800))
            {
                PlayerRec r = Rec(attacker.Id) ?? Touch(attacker);
                AddFlag(r, KRaid);
                AddAlert(KRaid, attacker.Id.ToString(), r.Name, hits.Count + " structure hits outside raid hours in 10 min; " + detail);
            }
        }

        private bool RaidAllowedNow()
        {
            if (!config.RaidHours.Enabled) return true;
            if (config.RaidHours.AllowDuringRebellion && RebellionActive()) return true;
            return InRaidWindow(LocalNow());
        }

        private DateTime LocalNow()
        {
            return Epoch.AddSeconds(NowSec()).AddMinutes(config.RaidHours.UtcOffsetMinutes);
        }

        private bool InRaidWindow(DateTime local)
        {
            DateTime start, end;
            return NextRaidWindow(local, out start, out end) && start <= local;
        }

        // The window that is open now, or else the next one to open (within 8 days). Times are in configured local time.
        private bool NextRaidWindow(DateTime local, out DateTime bestStart, out DateTime bestEnd)
        {
            bestStart = DateTime.MaxValue;
            bestEnd = DateTime.MaxValue;
            bool found = false;
            foreach (RaidWindow w in raidWindows)
            {
                for (int off = -1; off <= 8; off++)
                {
                    DateTime day = local.Date.AddDays(off);
                    if (!w.Days[(int)day.DayOfWeek]) continue;
                    DateTime s = day.AddMinutes(w.StartMinute);
                    DateTime e = s.AddMinutes(w.DurationMinutes);
                    if (e <= local) continue;
                    if (s < bestStart) { bestStart = s; bestEnd = e; found = true; }
                }
            }
            return found;
        }

        private bool RebellionActive()
        {
            if (CrownAndConsequences == null) return false;
            object r = CrownAndConsequences.Call("IsRebellionActive");
            return r is bool && (bool)r;
        }

        private string RaidStatus(Player p)
        {
            if (!config.RaidHours.Enabled) return Fmt("RaidOff", p, null);
            if (config.RaidHours.AllowDuringRebellion && RebellionActive()) return Fmt("RaidRebellion", p, null);
            DateTime local = LocalNow(), s, e;
            if (!NextRaidWindow(local, out s, out e)) return Fmt("RaidClosedNone", p, null);
            if (s <= local) return Fmt("RaidOpen", p, new object[] { LocalLabel(e) });
            return Fmt("RaidClosed", p, new object[] { LocalLabel(s) + " - " + LocalLabel(e) + " (in " + Dur((s - local).TotalSeconds) + ")" });
        }

        private string LocalLabel(DateTime local)
        {
            int off = config.RaidHours.UtcOffsetMinutes;
            string zone = off == 0 ? "UTC" : "UTC" + (off > 0 ? "+" : "-") + (Math.Abs(off) / 60).ToString("00") + ":" + (Math.Abs(off) % 60).ToString("00");
            return local.ToString("ddd HH:mm") + " " + zone;
        }

        #endregion

        #region Chat

        private class ChatState
        {
            public List<double> Times = new List<double>();
            public List<KeyValuePair<string, double>> Recent = new List<KeyValuePair<string, double>>();
            public List<double> Strikes = new List<double>();
        }

        // Core-dispatched [SRC ReignOfKingsHooks.cs:62-83]: non-null = the core cancels the message.
        private object OnPlayerChat(PlayerMessageEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled || !config.Chat.Enabled) return null;
            try
            {
                Player p = evt.Player;
                if (p == null || p.IsServer) return null;
                if (config.General.AdminsExempt && IsAdmin(p)) return null;
                double now = NowSec();
                PlayerRec r = Rec(p.Id) ?? Touch(p);
                if (r.MutedUntil > now)
                {
                    ReplyThrottled(p, 5, "Muted", Dur(r.MutedUntil - now));
                    return true;
                }
                string msg = evt.Message ?? "";
                ChatState st;
                if (!chat.TryGetValue(p.Id, out st)) { st = new ChatState(); chat[p.Id] = st; }
                ChatSettings c = config.Chat;

                string why = null;
                object[] whyArgs = null;
                if (msg.Length > c.MaxLength) { why = "ChatLength"; whyArgs = new object[] { c.MaxLength }; }
                else if (c.FilterWords && ContainsBlockedWord(msg)) why = "ChatWord";
                else if (c.CapsMaxPercent > 0 && TooManyCaps(msg, c.CapsMinLetters, c.CapsMaxPercent)) why = "ChatCaps";
                else
                {
                    st.Times.RemoveAll(delegate(double t) { return now - t > c.WindowSeconds; });
                    if (st.Times.Count >= c.MaxMessages) { why = "ChatRate"; whyArgs = new object[] { c.MaxMessages, c.WindowSeconds }; }
                    else
                    {
                        string norm = NormalizeChat(msg);
                        st.Recent.RemoveAll(delegate(KeyValuePair<string, double> kv) { return now - kv.Value > c.DuplicateWindowSeconds; });
                        int same = 0;
                        foreach (KeyValuePair<string, double> kv in st.Recent) if (kv.Key == norm) same++;
                        if (norm.Length > 0 && same >= c.MaxDuplicates) why = "ChatDuplicate";
                        else
                        {
                            st.Times.Add(now);
                            st.Recent.Add(new KeyValuePair<string, double>(norm, now));
                            if (st.Recent.Count > 50) st.Recent.RemoveAt(0);
                        }
                    }
                }
                if (why == null) return null;
                Strike(p, r, st, now, why, msg);
                if (r.MutedUntil <= now) ReplyThrottled(p, 3, why, whyArgs ?? new object[0]);
                return true;
            }
            catch (Exception ex)
            {
                PrintError("Chat check failed: " + ex.Message);
                return null;
            }
        }

        private void Strike(Player p, PlayerRec r, ChatState st, double now, string why, string msg)
        {
            ChatSettings c = config.Chat;
            st.Strikes.Add(now);
            st.Strikes.RemoveAll(delegate(double t) { return now - t > c.StrikeWindowSeconds; });
            if (st.Strikes.Count < c.StrikesToMute) return;
            st.Strikes.Clear();
            int level = (r.LastMuteAt > 0 && now - r.LastMuteAt < c.MuteLevelResetHours * 3600.0) ? r.MuteLevel + 1 : 1;
            if (level > 20) level = 20;
            double secs = c.MuteSeconds * Math.Pow(2, level - 1);
            if (secs > c.MaxMuteSeconds) secs = c.MaxMuteSeconds;
            ApplyMute(r, now, secs, level);
            AddFlag(r, KChat);
            string sample = Clean(msg, 80);
            AddEvidence(KChat, p, null, "muted " + Dur(secs) + " (level " + level + ", last strike: " + why + "): \"" + sample + "\"");
            Reply(p, "MutedNow", Dur(secs));
            int recent = 0;
            foreach (long t in r.MuteTimes) if (now - t <= 86400) recent++;
            if (recent >= c.AlertAfterMutes)
                AddAlert(KChat, p.Id.ToString(), r.Name, recent + " chat mutes in 24 h; last: \"" + sample + "\"");
        }

        private void ApplyMute(PlayerRec r, double now, double secs, int level)
        {
            r.MutedUntil = (long)(now + secs);
            r.MuteLevel = level;
            r.LastMuteAt = (long)now;
            r.MuteTimes.Add((long)now);
            if (r.MuteTimes.Count > 20) r.MuteTimes.RemoveRange(0, r.MuteTimes.Count - 20);
            dirty = true;
        }

        private static bool TooManyCaps(string msg, int minLetters, int maxPercent)
        {
            int letters = 0, upper = 0;
            foreach (char ch in msg)
            {
                if (!char.IsLetter(ch)) continue;
                letters++;
                if (char.IsUpper(ch)) upper++;
            }
            return letters >= minLetters && upper * 100 > letters * maxPercent;
        }

        #endregion

        #region Names

        private void CheckName(Player p)
        {
            if (!config.Names.Enabled) return;
            if (config.General.AdminsExempt && IsAdmin(p)) return;
            if (config.Names.ExemptIds.Contains(p.Id.ToString())) return;
            string hit = BlockedWordIn(p.Name ?? "");
            if (hit == null) return;
            PlayerRec r = Rec(p.Id) ?? Touch(p);
            string shown = Clean(p.Name, 64);
            bool kick = config.Names.Action == "kick";
            if (!Throttled("name|" + p.Id, 3600))
            {
                AddFlag(r, KName);
                AddEvidence(KName, p, null, "name '" + shown + "' matched '" + hit + "'" + (kick ? "; kicked" : ""));
                AddAlert(KName, p.Id.ToString(), shown, "name matched '" + hit + "'" + (kick ? "; kicked (not banned)" : "; not kicked (Action flag)"));
            }
            if (config.Names.WarnPlayer) Reply(p, "NameWarn", shown);
            if (!kick) return;
            ulong id = p.Id;
            kicking[id] = true;
            string reason = Msg("NameKick", p);
            timer.Once(config.Names.KickDelaySeconds, delegate()
            {
                Player again = Server.GetPlayerById(id);
                if (again != null && !again.IsServer) Server.Kick(again, reason);
                else kicking.Remove(id);
            });
        }

        // Returns the configured entry that matches, or null.
        private string BlockedWordIn(string text)
        {
            string compact = NormalizeCompact(text);
            foreach (string w in nameWordsAny) if (compact.Contains(w)) return w;
            if (nameWordsWhole.Count == 0) return null;
            foreach (string token in NormalizeTokens(text))
                foreach (string w in nameWordsWhole) if (token == w) return "=" + w;
            // Also catch the whole name written as one word ("Ad.Min" -> "admin").
            foreach (string w in nameWordsWhole) if (compact == w) return "=" + w;
            return null;
        }

        private bool ContainsBlockedWord(string text)
        {
            return BlockedWordIn(text) != null;
        }

        private char Leet(char ch)
        {
            if (!config.Names.NormalizeLeet) return ch;
            switch (ch)
            {
                case '0': return 'o';
                case '1': return 'i';
                case '!': return 'i';
                case '|': return 'i';
                case '3': return 'e';
                case '4': return 'a';
                case '@': return 'a';
                case '5': return 's';
                case '$': return 's';
                case '7': return 't';
                case '+': return 't';
                case '8': return 'b';
                case '9': return 'g';
            }
            return ch;
        }

        // Lower case, leet to letters, letters only. "4dm1n_X" -> "adminx".
        private string NormalizeCompact(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char raw in s.ToLowerInvariant())
            {
                char ch = Leet(raw);
                if (char.IsLetter(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        // For repeat detection: lower case, letters and digits only, no leet mapping ("buy gold!" == "BUY  GOLD").
        private static string NormalizeChat(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        // Words of the text, split at anything that is not a letter after leet mapping, and at case changes in names
        // like "TheAdmin". Separators and digits that are not leet split words.
        private List<string> NormalizeTokens(string s)
        {
            List<string> tokens = new List<string>();
            if (s == null) return tokens;
            StringBuilder sb = new StringBuilder();
            char prev = ' ';
            foreach (char raw in s)
            {
                char mapped = Leet(char.ToLowerInvariant(raw));
                bool letter = char.IsLetter(mapped);
                bool camelBreak = letter && char.IsUpper(raw) && char.IsLower(prev);
                if (!letter || camelBreak)
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Length = 0; }
                }
                if (letter) sb.Append(mapped);
                prev = raw;
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        #endregion

        #region Alerts, flags and evidence

        private void AddFlag(PlayerRec r, string kind)
        {
            int n;
            r.Flags.TryGetValue(kind, out n);
            r.Flags[kind] = n + 1;
            r.LastFlagAt = (long)NowSec();
            dirty = true;
        }

        private static int FlagCount(PlayerRec r, string kind)
        {
            int n;
            return r != null && r.Flags.TryGetValue(kind, out n) ? n : 0;
        }

        private int AddAlert(string kind, string playerId, string playerName, string detail)
        {
            Alert a = new Alert();
            a.Id = data.NextAlertId++;
            a.Ts = (long)NowSec();
            a.Kind = kind;
            a.PlayerId = playerId ?? "";
            a.PlayerName = Clean(playerName, 64);
            a.Detail = Clean(detail, 300);
            data.Alerts.Add(a);
            if (data.Alerts.Count > config.Alerts.MaxStored)
            {
                // Drop the oldest acknowledged alerts first, then the oldest of all.
                int excess = data.Alerts.Count - config.Alerts.MaxStored;
                for (int i = 0; i < data.Alerts.Count && excess > 0; )
                {
                    if (data.Alerts[i].Ack) { data.Alerts.RemoveAt(i); excess--; }
                    else i++;
                }
                if (excess > 0) data.Alerts.RemoveRange(0, excess);
            }
            dirty = true;
            Puts("ALERT #" + a.Id + " " + kind + " " + a.PlayerName + " (" + a.PlayerId + "): " + a.Detail);
            WriteLog("alerts", "#" + a.Id + " " + kind + " " + a.PlayerName + " (" + a.PlayerId + "): " + a.Detail);
            ChatAdmins(a);
            return a.Id;
        }

        private void ChatAdmins(Alert a)
        {
            if (!config.Alerts.ChatToOnlineAdmins) return;
            double now = NowSec();
            adminChatTimes.RemoveAll(delegate(double t) { return now - t > 60; });
            if (adminChatTimes.Count >= config.Alerts.MaxChatAlertsPerMinute) { suppressedAdminChat++; return; }
            adminChatTimes.Add(now);
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer || !IsAdmin(p)) continue;
                Reply(p, "AlertChat", a.Id, a.Kind, a.PlayerName, a.Detail);
                if (suppressedAdminChat > 0) Reply(p, "AlertOverflow", suppressedAdminChat);
            }
            suppressedAdminChat = 0;
        }

        private void AddEvidence(string kind, Player p, Player other, string detail)
        {
            Evidence e = new Evidence();
            e.Id = data.NextEvidenceId++;
            e.Ts = (long)NowSec();
            e.Kind = kind;
            if (p != null)
            {
                e.PlayerId = p.Id.ToString();
                e.PlayerName = Clean(p.Name, 64);
                Vector3 pos;
                if (TryPosition(p, out pos)) e.Pos = Pos(pos);
            }
            if (other != null)
            {
                e.OtherId = other.Id.ToString();
                e.OtherName = Clean(other.Name, 64);
            }
            e.Detail = Clean(detail, 300);
            data.Evidence.Add(e);
            if (data.Evidence.Count > config.Alerts.MaxEvidence) data.Evidence.RemoveRange(0, data.Evidence.Count - config.Alerts.MaxEvidence);
            dirty = true;
            WriteLog("evidence", "#" + e.Id + " " + kind + " " + e.PlayerName + " (" + e.PlayerId + ")"
                + (e.OtherId.Length > 0 ? " vs " + e.OtherName + " (" + e.OtherId + ")" : "")
                + (e.Pos.Length > 0 ? " at " + e.Pos : "") + ": " + e.Detail);
        }

        private void WriteLog(string file, string line)
        {
            if (!config.Alerts.LogToFile) return;
            try { LogToFile(file, Epoch.AddSeconds(NowSec()).ToString("yyyy-MM-ddTHH:mm:ssZ") + " " + line, this, true, false); }
            catch (Exception ex) { PrintWarning("Could not write the " + file + " log: " + ex.Message); }
        }

        private int UnreadCount()
        {
            int n = 0;
            foreach (Alert a in data.Alerts) if (!a.Ack) n++;
            return n;
        }

        #endregion

        #region Commands

        [ChatCommand("warden")]
        private void CmdWarden(Player player, string command, string[] args)
        {
            if (player == null || data == null) return;
            try
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
                switch (sub)
                {
                    case "help": ShowHelp(player); return;
                    case "status": ShowStatus(player); return;
                    case "rules": ShowRules(player); return;
                    case "protection": CmdProtection(player, args); return;
                    case "report": CmdReport(player, args); return;
                }
                if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
                switch (sub)
                {
                    case "alerts": CmdAlerts(player, args); return;
                    case "ack": CmdAck(player, args); return;
                    case "player": CmdPlayer(player, args); return;
                    case "evidence": CmdEvidence(player, args); return;
                    case "protect": CmdProtect(player, args); return;
                    case "mute": CmdMute(player, args); return;
                    case "unmute": CmdUnmute(player, args); return;
                    case "clear": CmdClear(player, args); return;
                    case "raid": CmdRaid(player); return;
                }
                ShowHelp(player);
            }
            catch (Exception ex)
            {
                PrintError("/warden failed: " + ex.Message);
            }
        }

        private void ShowHelp(Player p)
        {
            Reply(p, "Help1");
            Reply(p, "Help2");
            if (config.NewPlayerProtection.AllowOptOut) Reply(p, "Help3");
            if (!IsAdmin(p)) return;
            Reply(p, "HelpAdmin1");
            Reply(p, "HelpAdmin2");
            if (loadFailed) ReplyError(p, "DataDamaged");
        }

        private void ShowRules(Player p)
        {
            Reply(p, "Rules1", Dur(config.NewPlayerProtection.PlaytimeMinutes * 60), config.CombatLog.WindowSeconds);
            Reply(p, "Rules2");
            p.SendMessage(Msg("Prefix", p) + RaidStatus(p));
        }

        private void ShowStatus(Player p)
        {
            double now = NowSec();
            PlayerRec r = Rec(p.Id) ?? Touch(p);
            double left = ProtectionLeft(r, now);
            if (left > 0) Reply(p, "ProtectionLeft", Dur(left)); else Reply(p, "ProtectionNone");
            if (r.MutedUntil > now) Reply(p, "Muted", Dur(r.MutedUntil - now)); else Reply(p, "NotMuted");
            CombatTag tag;
            if (combat.TryGetValue(p.Id, out tag) && now - tag.LastHit <= config.CombatLog.WindowSeconds)
                Reply(p, "CombatState", (int)Math.Ceiling(config.CombatLog.WindowSeconds - (now - tag.LastHit)));
            else Reply(p, "CombatNone");
            p.SendMessage(Msg("Prefix", p) + RaidStatus(p));
        }

        private void CmdProtection(Player p, string[] args)
        {
            if (args.Length < 2 || args[1].ToLowerInvariant() != "off") { ShowStatus(p); return; }
            if (!config.NewPlayerProtection.AllowOptOut) { ReplyError(p, "OptOutDisabled"); return; }
            if (!IsProtectedId(p.Id)) { ReplyError(p, "NotProtected"); return; }
            if (args.Length < 3 || args[2].ToLowerInvariant() != "confirm") { Reply(p, "OptOutConfirm"); return; }
            EndProtection(p, "gave it up", false);
        }

        private void CmdReport(Player p, string[] args)
        {
            ReportSettings c = config.Reports;
            if (!c.Enabled) { ReplyError(p, "ReportDisabled"); return; }
            if (args.Length < 3) { ReplyError(p, "ReportUsage"); return; }
            string targetId, targetName;
            if (!ResolveTarget(p, args[1], out targetId, out targetName)) return;
            if (targetId == p.Id.ToString()) { ReplyError(p, "ReportSelf"); return; }
            string reason = Clean(string.Join(" ", args, 2, args.Length - 2), 1000).Trim();
            if (reason.Length < c.MinReasonLength || reason.Length > c.MaxReasonLength)
            {
                ReplyError(p, "ReportReason", c.MinReasonLength, c.MaxReasonLength);
                return;
            }
            PlayerRec r = Rec(p.Id) ?? Touch(p);
            long now = (long)NowSec();
            r.ReportTimes.RemoveAll(delegate(long t) { return now - t > 86400; });
            if (r.ReportTimes.Count > 0 && now - r.ReportTimes[r.ReportTimes.Count - 1] < c.CooldownSeconds)
            {
                ReplyError(p, "ReportCooldown", Dur(c.CooldownSeconds - (now - r.ReportTimes[r.ReportTimes.Count - 1])));
                return;
            }
            if (r.ReportTimes.Count >= c.MaxPerDay) { ReplyError(p, "ReportDaily", r.ReportTimes.Count); return; }
            long last;
            if (r.ReportedAt.TryGetValue(targetId, out last) && now - last < c.SameTargetCooldownMinutes * 60L)
            {
                ReplyError(p, "ReportSameTarget");
                return;
            }
            r.ReportTimes.Add(now);
            r.ReportedAt[targetId] = now;
            if (r.ReportedAt.Count > 50)
            {
                List<string> old = new List<string>();
                foreach (KeyValuePair<string, long> kv in r.ReportedAt) if (now - kv.Value > c.SameTargetCooldownMinutes * 60L) old.Add(kv.Key);
                foreach (string k in old) r.ReportedAt.Remove(k);
            }
            PlayerRec tr;
            if (data.Players.TryGetValue(targetId, out tr)) AddFlag(tr, KReport);
            Player online = FindOnlineById(targetId);
            AddEvidence(KReport, p, online, "reported " + targetName + " (" + targetId + "): " + reason);
            int id = AddAlert(KReport, targetId, targetName, "reported by " + Clean(p.Name, 40) + ": " + reason);
            Reply(p, "ReportSent", id);
        }

        private void CmdAlerts(Player p, string[] args)
        {
            bool all = false;
            int page = 1;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].ToLowerInvariant() == "all") all = true;
                else if (!ParseInt(p, args[i], 1, 10000, out page)) return;
            }
            List<Alert> list = new List<Alert>();
            for (int i = data.Alerts.Count - 1; i >= 0; i--) if (all || !data.Alerts[i].Ack) list.Add(data.Alerts[i]);
            if (list.Count == 0) { Reply(p, "AlertsNone"); return; }
            int pages = (list.Count + PageSize - 1) / PageSize;
            if (page > pages) page = pages;
            Reply(p, "AlertsHeader", UnreadCount(), data.Alerts.Count, page, pages);
            for (int i = (page - 1) * PageSize; i < Math.Min(list.Count, page * PageSize); i++)
            {
                Alert a = list[i];
                p.SendMessage(Fmt("AlertLine", p, new object[] { a.Id, Stamp(a.Ts), a.Kind, a.PlayerName, a.Detail,
                    a.Ack ? " [ack " + a.AckBy + "]" : "" }));
            }
        }

        private void CmdAck(Player p, string[] args)
        {
            if (args.Length < 2) { ReplyError(p, "AckNone"); return; }
            int n = 0;
            string by = Clean(p.Name, 40);
            if (args[1].ToLowerInvariant() == "all")
            {
                foreach (Alert a in data.Alerts) if (!a.Ack) { a.Ack = true; a.AckBy = by; n++; }
            }
            else
            {
                int id;
                if (!ParseInt(p, args[1].TrimStart('#'), 1, int.MaxValue, out id)) return;
                foreach (Alert a in data.Alerts) if (a.Id == id && !a.Ack) { a.Ack = true; a.AckBy = by; n++; }
            }
            if (n == 0) { ReplyError(p, "AckNone"); return; }
            dirty = true;
            Reply(p, "AckDone", n);
        }

        private void CmdPlayer(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            double now = NowSec();
            Reply(p, "PlayerHeader", r.Name, id, Stamp(r.FirstSeen), Stamp(r.LastSeen), Dur(r.Playtime));
            List<string> flags = new List<string>();
            foreach (KeyValuePair<string, int> kv in r.Flags) flags.Add(kv.Key + " x" + kv.Value);
            Reply(p, "PlayerFlags", flags.Count == 0 ? Msg("None", p) : string.Join(", ", flags.ToArray()));
            double left = ProtectionLeft(r, now);
            string prot = left > 0 ? Dur(left) + " left" : (r.ProtectionEnded && r.ProtectionEndReason.Length > 0 ? "ended (" + r.ProtectionEndReason + ")" : Msg("None", p));
            string mute = r.MutedUntil > now ? Dur(r.MutedUntil - now) + " left (level " + r.MuteLevel + ")" : Msg("None", p);
            ulong uid;
            CombatTag tag;
            string fight = ulong.TryParse(id, out uid) && combat.TryGetValue(uid, out tag) && now - tag.LastHit <= config.CombatLog.WindowSeconds
                ? "with " + tag.OtherName : Msg("None", p);
            Reply(p, "PlayerState", prot, mute, fight);
            int shown = 0;
            for (int i = data.Evidence.Count - 1; i >= 0 && shown < 3; i--)
            {
                Evidence e = data.Evidence[i];
                if (e.PlayerId != id && e.OtherId != id) continue;
                SendEvidence(p, e);
                shown++;
            }
        }

        private void CmdEvidence(Player p, string[] args)
        {
            string filter = null;
            int page = 1;
            for (int i = 1; i < args.Length; i++)
            {
                int n;
                if (i == args.Length - 1 && int.TryParse(args[i], out n) && args[i].Length < 6) { if (!ParseInt(p, args[i], 1, 10000, out page)) return; continue; }
                string id, name;
                if (!ResolveTarget(p, args[i], out id, out name)) return;
                filter = id;
            }
            List<Evidence> list = new List<Evidence>();
            for (int i = data.Evidence.Count - 1; i >= 0; i--)
            {
                Evidence e = data.Evidence[i];
                if (filter == null || e.PlayerId == filter || e.OtherId == filter) list.Add(e);
            }
            if (list.Count == 0) { Reply(p, "EvidenceNone"); return; }
            int pages = (list.Count + PageSize - 1) / PageSize;
            if (page > pages) page = pages;
            Reply(p, "EvidenceHeader", page, pages);
            for (int i = (page - 1) * PageSize; i < Math.Min(list.Count, page * PageSize); i++) SendEvidence(p, list[i]);
        }

        private void SendEvidence(Player p, Evidence e)
        {
            string who = e.PlayerName + (e.OtherName.Length > 0 ? " vs " + e.OtherName : "");
            p.SendMessage(Fmt("EvidenceLine", p, new object[] { e.Id, Stamp(e.Ts), e.Kind, who, e.Detail, e.Pos.Length > 0 ? " @" + e.Pos : "" }));
        }

        private void CmdProtect(Player p, string[] args)
        {
            if (args.Length < 3) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            double now = NowSec();
            if (args[2].ToLowerInvariant() == "off")
            {
                r.ProtectedUntil = 0;
                r.ProtectionEnded = true;
                r.ProtectionEndReason = "removed by an admin";
                r.ProtectionExpiryNotified = true;
                dirty = true;
                AdminLog(p, id, name, "removed protection");
                Reply(p, "ProtectOff", name);
                return;
            }
            int minutes;
            if (!ParseInt(p, args[2], 1, 10080, out minutes)) return;
            r.ProtectedUntil = (long)(now + minutes * 60.0);
            r.ProtectionExpiryNotified = false;
            dirty = true;
            AdminLog(p, id, name, "granted protection for " + minutes + " min");
            Reply(p, "ProtectSet", name, Dur(minutes * 60));
        }

        private void CmdMute(Player p, string[] args)
        {
            if (args.Length < 3) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            int minutes;
            if (!ParseInt(p, args[2], 1, 1440, out minutes)) return;
            ApplyMute(r, NowSec(), minutes * 60.0, r.MuteLevel);
            AdminLog(p, id, name, "muted for " + minutes + " min");
            Reply(p, "MuteSet", name, Dur(minutes * 60));
        }

        private void CmdUnmute(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            r.MutedUntil = 0;
            dirty = true;
            AdminLog(p, id, name, "unmuted");
            Reply(p, "UnmuteSet", name);
        }

        private void CmdClear(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            r.Flags.Clear();
            r.MutedUntil = 0;
            r.MuteLevel = 0;
            r.MuteTimes.Clear();
            dirty = true;
            AdminLog(p, id, name, "cleared flags and mute");
            Reply(p, "ClearDone", name);
        }

        private void CmdRaid(Player p)
        {
            RaidSettings c = config.RaidHours;
            Reply(p, "RaidAdmin", c.Enabled, c.Block, c.UtcOffsetMinutes, raidWindows.Count, c.AllowDuringRebellion);
            p.SendMessage(Msg("Prefix", p) + RaidStatus(p));
        }

        private void AdminLog(Player admin, string targetId, string targetName, string what)
        {
            Evidence e = new Evidence();
            e.Id = data.NextEvidenceId++;
            e.Ts = (long)NowSec();
            e.Kind = KAdmin;
            e.PlayerId = admin.Id.ToString();
            e.PlayerName = Clean(admin.Name, 64);
            e.OtherId = targetId;
            e.OtherName = Clean(targetName, 64);
            e.Detail = what;
            data.Evidence.Add(e);
            if (data.Evidence.Count > config.Alerts.MaxEvidence) data.Evidence.RemoveRange(0, data.Evidence.Count - config.Alerts.MaxEvidence);
            dirty = true;
            WriteLog("evidence", "#" + e.Id + " " + KAdmin + " " + e.PlayerName + " -> " + e.OtherName + " (" + targetId + "): " + what);
        }

        // Online exact name, online Steam ID, unique online partial name, then a recorded name or Steam ID.
        private bool ResolveTarget(Player asker, string query, out string id, out string name)
        {
            id = null;
            name = null;
            string q = (query ?? "").Trim();
            if (q.Length == 0) { ReplyError(asker, "PlayerNotFound"); return false; }
            List<Player> partial = new List<Player>();
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer || p.Name == null) continue;
                if (string.Equals(p.Name, q, StringComparison.OrdinalIgnoreCase) || p.Id.ToString() == q)
                {
                    id = p.Id.ToString();
                    name = Clean(p.Name, 64);
                    if (data.Players.ContainsKey(id) == false) Touch(p);
                    return true;
                }
                if (p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(p);
            }
            if (partial.Count == 1)
            {
                id = partial[0].Id.ToString();
                name = Clean(partial[0].Name, 64);
                if (!data.Players.ContainsKey(id)) Touch(partial[0]);
                return true;
            }
            if (partial.Count > 1) { ReplyError(asker, "Ambiguous", Clean(q, 40)); return false; }
            PlayerRec r;
            if (data.Players.TryGetValue(q, out r)) { id = q; name = r.Name; return true; }
            foreach (KeyValuePair<string, PlayerRec> kv in data.Players)
            {
                if (string.Equals(kv.Value.Name, q, StringComparison.OrdinalIgnoreCase)) { id = kv.Key; name = kv.Value.Name; return true; }
            }
            ReplyError(asker, "PlayerNotFound");
            return false;
        }

        private static Player FindOnlineById(string id)
        {
            ulong u;
            return ulong.TryParse(id, out u) ? Server.GetPlayerById(u) : null;
        }

        private bool ParseInt(Player p, string s, int lo, int hi, out int value)
        {
            if (int.TryParse(s, out value) && value >= lo && value <= hi) return true;
            ReplyError(p, "BadNumber", Clean(s, 20), lo, hi);
            return false;
        }

        #endregion

        #region Cross-plugin API (non-public on purpose: Oxide's Call only finds NonPublic|Instance methods)

        private bool IsNewPlayerProtected(ulong playerId)
        {
            return data != null && IsProtectedId(playerId);
        }

        private bool IsInCombat(ulong playerId)
        {
            CombatTag tag;
            return combat.TryGetValue(playerId, out tag) && NowSec() - tag.LastHit <= config.CombatLog.WindowSeconds;
        }

        private int GetWardenFlagCount(ulong playerId)
        {
            PlayerRec r = Rec(playerId);
            if (r == null) return 0;
            int n = 0;
            foreach (int v in r.Flags.Values) n += v;
            return n;
        }

        private bool IsRaidHourNow()
        {
            return RaidAllowedNow();
        }

        // Other plugins may queue an admin alert. Capped per hour; returns the alert id, or 0 when refused.
        private int RaiseWardenAlert(string kind, ulong playerId, string detail)
        {
            if (data == null) return 0;
            double now = NowSec();
            externalAlertTimes.RemoveAll(delegate(double t) { return now - t > 3600; });
            if (externalAlertTimes.Count >= MaxExternalAlertsPerHour) return 0;
            externalAlertTimes.Add(now);
            PlayerRec r = Rec(playerId);
            string k = "ext:" + Clean(kind ?? "other", 24).Replace(' ', '_');
            return AddAlert(k, playerId == 0 ? "" : playerId.ToString(), r != null ? r.Name : "", detail ?? "");
        }

        #endregion

        #region Helpers

        private double NowSec()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds + clockSkew;
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
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Pos(Vector3 v)
        {
            return ((int)Math.Round(v.x)) + "," + ((int)Math.Round(v.y)) + "," + ((int)Math.Round(v.z));
        }

        // Display-safe text: no colour tags ([RRGGBB]), no line breaks, capped length.
        private static string Clean(string s, int max)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(Math.Min(s.Length, max));
            foreach (char ch in s)
            {
                if (sb.Length >= max) break;
                if (ch == '[') sb.Append('(');
                else if (ch == ']') sb.Append(')');
                else if (ch == '\n' || ch == '\r' || ch == '\t') sb.Append(' ');
                else if (!char.IsControl(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string Dur(double seconds)
        {
            if (seconds < 0) seconds = 0;
            long s = (long)Math.Ceiling(seconds);
            if (s < 60) return s + " s";
            long m = s / 60;
            if (m < 60) return m + " min";
            long h = m / 60;
            if (h < 48) return h + " h " + (m % 60).ToString("00") + " min";
            return (h / 24) + " d " + (h % 24) + " h";
        }

        private static string Stamp(long ts)
        {
            if (ts <= 0) return "-";
            return Epoch.AddSeconds(ts).ToString("yyyy-MM-dd HH:mm") + "Z";
        }

        #endregion
    }
}
