// RealmStats: privacy-friendly server analytics for Ostreval. It records aggregate, pseudonymous play statistics to
// rotating day files that the offline dashboard builder in analytics/ turns into an HTML report.
//
// What it records (and nothing else):
//   * a pseudonymous player key: the first 16 hex chars of HMAC-SHA256(Salt, "player|" + SteamID64). The salt lives in
//     oxide/config/RealmStats.json (generated on first load, never in the repo). No Steam ids, names, IPs, chat,
//     positions or inventories are written.
//   * per UTC day: the set of active keys, the keys first seen that day (for D1/D7 retention), anonymous sessions
//     (start time + length, no key), joins and leaves per UTC hour, concurrent players every SampleMinutes (sample and
//     in-interval max), deaths by cause type, and per-house activity (active keys, online seconds, sessions, deaths,
//     PvP kills).
//   * Players can opt out (/stats optout). An opted-out player's key is removed from today's data and scrubbed from the
//     retained day files in the background; afterwards they only appear in anonymous counters (concurrency, joins and
//     leaves per hour, death causes).
//
// Hooks used (tags as in docs/oxide-rok-api.md):
//   OnPlayerConnected / OnPlayerDisconnected (Player)  core-dispatched, server player filtered [SRC ReignOfKingsHooks.cs]
//   OnEntityDeath(EntityDeathEvent)                     [OPJ L188] RB 1; this plugin always returns null. Victim =
//                                                       evt.Entity.Owner, killer = evt.KillingDamage.DamageSource.Owner,
//                                                       cause = evt.KillingDamage.DamageTypes (CodeHatch.Damaging.DamageType
//                                                       flags; members read in [DEC] DamageType.cs) [ASM]
//   OnServerInitialized / OnServerSave / OnServerShutdown / Unload           lifecycle [SRC][OPJ]
//   House: RealmHouses.Call("GetHouse", steamIdString) -> string (non-public API in plugins/RealmHouses.cs), or the game
//   guild name via PlayerExtensions.GetGuild() [ASM] when RealmHouses is not loaded (HouseSource "auto").
//
// Files (Oxide DataFileSystem, oxide/data/):
//   RealmStats/state.json              index of day files, first/last-seen day per key, open sessions, opt-outs.
//   RealmStats/day-YYYY-MM-DD.json     one file per UTC day; files older than RetentionDays are deleted on rollover.
//   RealmStats/day-YYYY-MM-DD-rN.json  written instead when the day file exists but cannot be parsed (never overwritten).
// Corruption safety: if state.json exists but cannot be parsed, the plugin keeps collecting from memory, marks every day
// it writes "Degraded" (no New list, so retention skips it), and NEVER writes state.json or deletes any file until the
// admin fixes or removes it and reloads.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmStats.md.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CodeHatch.Common;                          // PlayerExtensions.SendMessage/SendError/GetGuild [ASM]
using CodeHatch.Damaging;                        // Damage, DamageType [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Networking.Events.Entities;      // EntityDeathEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // Guild [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmStats", "Realm", "0.1.0")]
    [Description("Privacy-friendly server analytics: hashed player keys, sessions, concurrency, joins/leaves, death causes and house activity in rotating day files")]
    public class RealmStats : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmHouses;

        private const string PermAdmin = "realmstats.admin";
        private const string Dir = "RealmStats";
        private const string StateName = "RealmStats/state";
        private const int FormatVersion = 1;
        private const float TickSeconds = 15f;
        private const int MaxHouseNameLength = 48;
        private const int MaxSamplesPerDay = 1440;
        private const int ScrubFilesPerTick = 2;
        private const string OtherHouse = "(other)";
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly string[] CauseKeys = new string[]
        {
            "pvp", "suicide", "fall", "hunger", "thirst", "drowning", "fire", "explosion", "siege", "plague",
            "out_of_bounds", "creature", "weapon", "other"
        };

        private PluginConfig config;
        private StateData state;
        private DayData day;
        private string dayName;                          // data file name of the day being written
        private bool stateFailed;                        // state.json could not be parsed: never write it
        private bool configFailed;
        private bool initialized;
        private bool shuttingDown;
        private long lastSave;
        private long nextSample;
        private int intervalMax;
        private long clockOffset;                        // seconds added to the clock; only the logic tests change it
        private byte[] saltBytes;
        private string saltId = "";
        private int scrubCursor;
        private string dayNote = "";                     // what happened when today's file was opened

        private readonly Dictionary<ulong, OnlineRec> online = new Dictionary<ulong, OnlineRec>();
        private readonly Dictionary<ulong, string> keyCache = new Dictionary<ulong, string>();
        private readonly Dictionary<string, bool> optOut = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> activeSet = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> newSet = new Dictionary<string, bool>();
        private readonly Dictionary<string, Dictionary<string, bool>> houseActiveSet = new Dictionary<string, Dictionary<string, bool>>();
        private readonly Dictionary<ulong, long> lastDeath = new Dictionary<ulong, long>();
        private readonly Dictionary<string, long> cooldowns = new Dictionary<string, long>();

        #region Config

        private class PluginConfig
        {
            public string Salt = "";                     // generated on first load; keep it secret; changing it resets retention
            public string ServerLabel = "realm-1";       // written into each day file so several servers can be told apart
            public int SampleMinutes = 5;                // concurrency sample interval (1..60)
            public int RetentionDays = 120;              // day files older than this are deleted (7..730)
            public int SaveIntervalSeconds = 300;        // 60..3600
            public int MinSessionSeconds = 5;            // shorter sessions are counted as joins only
            public string HouseSource = "auto";          // auto | realmhouses | guild | off
            public bool TrackDeaths = true;
            public bool TrackHouses = true;
            public bool AllowOptOut = true;
            public bool PrivacyNoticeOnFirstJoin = true; // one chat line the first time a key is seen
            public int MaxTrackedPlayers = 200000;       // first/last-seen table; least recently seen are pruned
            public int MaxActivePerDay = 50000;
            public int MaxSessionsPerDay = 50000;
            public int MaxHousesPerDay = 100;            // further houses are folded into "(other)"
            public int MaxOptOuts = 100000;
            public int DeathDedupeSeconds = 3;           // one death per victim within this window
            public int CommandCooldownSeconds = 3;
            public int AdminSaveCooldownSeconds = 30;
            public int OptToggleCooldownSeconds = 60;
        }

        protected override void LoadDefaultConfig()
        {
            PluginConfig c = new PluginConfig();
            c.Salt = NewSalt();
            Config.WriteObject(c, true);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void ClampConfig()
        {
            if (config.ServerLabel == null) config.ServerLabel = "";
            config.ServerLabel = CleanName(config.ServerLabel, 32);
            config.SampleMinutes = Clamp(config.SampleMinutes, 1, 60);
            config.RetentionDays = Clamp(config.RetentionDays, 7, 730);
            config.SaveIntervalSeconds = Clamp(config.SaveIntervalSeconds, 60, 3600);
            config.MinSessionSeconds = Clamp(config.MinSessionSeconds, 0, 600);
            if (config.HouseSource == null) config.HouseSource = "auto";
            config.HouseSource = config.HouseSource.Trim().ToLowerInvariant();
            if (config.HouseSource != "auto" && config.HouseSource != "realmhouses" && config.HouseSource != "guild" && config.HouseSource != "off")
                config.HouseSource = "auto";
            config.MaxTrackedPlayers = Clamp(config.MaxTrackedPlayers, 1000, 1000000);
            config.MaxActivePerDay = Clamp(config.MaxActivePerDay, 100, 500000);
            config.MaxSessionsPerDay = Clamp(config.MaxSessionsPerDay, 100, 500000);
            config.MaxHousesPerDay = Clamp(config.MaxHousesPerDay, 1, 1000);
            config.MaxOptOuts = Clamp(config.MaxOptOuts, 10, 1000000);
            config.DeathDedupeSeconds = Clamp(config.DeathDedupeSeconds, 0, 60);
            config.CommandCooldownSeconds = Clamp(config.CommandCooldownSeconds, 0, 60);
            config.AdminSaveCooldownSeconds = Clamp(config.AdminSaveCooldownSeconds, 0, 3600);
            config.OptToggleCooldownSeconds = Clamp(config.OptToggleCooldownSeconds, 0, 86400);
        }

        #endregion

        #region Data model

        private class PlayerSeen
        {
            public string F = "";                        // first UTC day seen (yyyy-MM-dd)
            public string L = "";                        // last UTC day seen
        }

        private class OpenSession
        {
            public long S;                               // unix start
            public string H = "";                        // house at start ("" = none)
        }

        private class StateData
        {
            public int Version = FormatVersion;
            public string SaltId = "";
            public long Heartbeat;                       // unix time of the last save; ends sessions left open by a crash
            public List<string> Days = new List<string>();                       // day file names, oldest first
            public Dictionary<string, PlayerSeen> Players = new Dictionary<string, PlayerSeen>();
            public Dictionary<string, OpenSession> Open = new Dictionary<string, OpenSession>();
            public List<string> OptOut = new List<string>();
            public List<string> ScrubQueue = new List<string>();                 // opted-out keys still to remove from old files
        }

        private class SessionRec
        {
            public long S;                               // unix start
            public int D;                                // seconds
            public string E = "";                        // leave | shutdown | recovered | reconcile | optout
        }

        private class Sample
        {
            public long T;                               // unix start of the sample bucket
            public int N;                                // players online at the sample
            public int Max;                              // most players online at once since the previous sample
        }

        private class HouseDay
        {
            public List<string> Active = new List<string>();
            public long Seconds;
            public int Sessions;
            public int Deaths;
            public int Kills;
        }

        private class DayData
        {
            public int Version = FormatVersion;
            public string Date = "";
            public string Server = "";
            public string SaltId = "";
            public bool Degraded;                        // true when state.json was unreadable (New list unknown)
            public int SampleMinutes = 5;
            public List<string> Active = new List<string>();
            public List<string> New = new List<string>();
            public List<SessionRec> Sessions = new List<SessionRec>();
            public int[] Joins = new int[24];
            public int[] Leaves = new int[24];
            public List<Sample> Concurrency = new List<Sample>();
            public Dictionary<string, int> Deaths = new Dictionary<string, int>();
            public Dictionary<string, HouseDay> Houses = new Dictionary<string, HouseDay>();
            public Dictionary<string, int> Dropped = new Dictionary<string, int>();   // records not kept because of a cap
        }

        private class OnlineRec
        {
            public string Key = "";
            public bool Opted;
            public long Start;
            public long LastAccrue;
            public string House = "";
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                configFailed = true;
                PrintError("Could not read oxide/config/RealmStats.json: " + ex.Message + ". Using defaults in memory; the file is NOT rewritten. Stats are not collected until the config is fixed (the salt is needed).");
            }
            if (config == null) { config = new PluginConfig(); if (!configFailed) configFailed = true; }
            ClampConfig();
            if (!configFailed && (config.Salt == null || config.Salt.Trim().Length < 16 || config.Salt.IndexOf("CHANGE", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                config.Salt = NewSalt();
                Config.WriteObject(config, true);
                Puts("Generated a new secret salt in oxide/config/RealmStats.json. Keep it private and keep a backup: changing it resets retention.");
            }
            SetSalt(configFailed ? "" : config.Salt);
            LoadState();
            OpenDay(Now());
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                     // re-sent on hot load; keep idempotent
            initialized = true;
            long now = Now();
            lastSave = now;
            nextSample = BucketStart(now) + config.SampleMinutes * 60L;
            // Players already online (hot load) resume their open session; open sessions of anyone else were left by
            // a crash or an unclean stop and are closed at the last heartbeat.
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                StartSession(p, now, true);
            }
            CloseOrphanSessions(now);
            intervalMax = online.Count;
            timer.Every(TickSeconds, Tick);
        }

        private void OnServerSave()
        {
            SaveAll();
        }

        private void OnServerShutdown()
        {
            shuttingDown = true;
            long now = Now();
            List<ulong> ids = new List<ulong>(online.Keys);
            foreach (ulong id in ids) EndSession(id, now, "shutdown");
            SaveAll();
        }

        private void Unload()
        {
            if (shuttingDown) return;
            AccrueAll(Now());                            // sessions stay open in state.json and resume on reload
            SaveAll();
        }

        #endregion

        #region Hooks

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer || !Collecting()) return;
            long now = Now();
            EnsureDay(now);
            day.Joins[HourOf(now)]++;
            StartSession(player, now, false);
            if (online.Count > intervalMax) intervalMax = online.Count;
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null || player.IsServer || !Collecting() || shuttingDown) return;
            long now = Now();
            EnsureDay(now);
            day.Leaves[HourOf(now)]++;
            EndSession(player.Id, now, "leave");
        }

        // RB 1 [OPJ L188]: always returns null so the game's own death handling continues.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            try { HandleDeath(evt); }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        private void HandleDeath(EntityDeathEvent evt)
        {
            if (!config.TrackDeaths || !Collecting()) return;
            if (evt == null || evt.Entity == null || !evt.Entity.IsPlayer) return;
            Player victim = evt.Entity.Owner;
            if (victim == null || victim.IsServer) return;
            long now = Now();
            long last;
            if (lastDeath.TryGetValue(victim.Id, out last) && now - last < config.DeathDedupeSeconds) return;
            lastDeath[victim.Id] = now;
            if (lastDeath.Count > 5000) lastDeath.Clear();
            EnsureDay(now);

            Damage dmg = evt.KillingDamage;
            Player killer = null;
            if (dmg != null && dmg.DamageSource != null) killer = dmg.DamageSource.Owner;
            bool pvp = killer != null && !killer.IsServer && killer.Id != victim.Id;
            bool sourceIsNonPlayer = dmg != null && dmg.DamageSource != null && !dmg.DamageSource.IsPlayer;
            string cause = ClassifyDeath(pvp, dmg != null ? dmg.DamageTypes : DamageType.Unknown, sourceIsNonPlayer);
            Bump(day.Deaths, cause, 1);

            if (!config.TrackHouses) return;
            string vh = HouseForEvent(victim);
            if (vh != null) { HouseDay h = House(vh); if (h != null) h.Deaths++; }
            if (pvp)
            {
                string kh = HouseForEvent(killer);
                if (kh != null) { HouseDay h = House(kh); if (h != null) h.Kills++; }
            }
        }

        // Priority order: a kill by another player is always "pvp"; then the most specific environmental flag.
        // UNVERIFIED in game: which flags the game sets for each kind of death, and whether creature attacks carry a
        // non-player DamageSource entity ("creature").
        private static string ClassifyDeath(bool pvp, DamageType t, bool sourceIsNonPlayer)
        {
            if (pvp) return "pvp";
            if ((t & DamageType.Suicide) != 0) return "suicide";
            if ((t & DamageType.Falling) != 0 || (t & DamageType.Impact) != 0) return "fall";
            if ((t & DamageType.Hunger) != 0) return "hunger";
            if ((t & DamageType.Thirst) != 0) return "thirst";
            if ((t & DamageType.Drowning) != 0) return "drowning";
            if ((t & DamageType.Fire) != 0) return "fire";
            if ((t & DamageType.Explosion) != 0) return "explosion";
            if ((t & DamageType.Siege) != 0) return "siege";
            if ((t & DamageType.Plague) != 0) return "plague";
            if ((t & DamageType.OutOfBounds) != 0) return "out_of_bounds";
            if (sourceIsNonPlayer) return "creature";
            DamageType weapon = DamageType.Melee | DamageType.Projectile | DamageType.Cut | DamageType.Pierce | DamageType.Bash | DamageType.Slash;
            if ((t & weapon) != 0) return "weapon";
            return "other";
        }

        #endregion

        #region Sessions

        private void StartSession(Player p, long now, bool resume)
        {
            if (online.ContainsKey(p.Id)) return;
            OnlineRec r = new OnlineRec();
            r.Key = KeyOf(p.Id);
            r.Opted = optOut.ContainsKey(r.Key);
            r.Start = now;
            r.LastAccrue = now;
            if (!r.Opted)
            {
                OpenSession open;
                if (resume && state.Open.TryGetValue(r.Key, out open) && open != null && open.S > 0 && open.S <= now)
                {
                    r.Start = open.S;                    // hot reload: keep the session that was already running
                    r.House = open.H ?? "";
                    if (state.Heartbeat > open.S && state.Heartbeat <= now) r.LastAccrue = state.Heartbeat;
                }
                bool firstEver = !state.Players.ContainsKey(r.Key);
                MarkActive(r.Key);
                if (r.House.Length == 0) r.House = LookupHouse(p) ?? "";
                if (r.House.Length > 0) MarkHouseActive(r.House, r.Key);
                if (!resume || !state.Open.ContainsKey(r.Key))
                {
                    if (r.House.Length > 0) { HouseDay h = House(r.House); if (h != null) h.Sessions++; }
                }
                OpenSession os = new OpenSession();
                os.S = r.Start;
                os.H = r.House;
                state.Open[r.Key] = os;
                if (firstEver && config.PrivacyNoticeOnFirstJoin && !resume) Reply(p, "PrivacyNotice");
            }
            online[p.Id] = r;
        }

        private void EndSession(ulong id, long now, string reason)
        {
            OnlineRec r;
            if (!online.TryGetValue(id, out r)) return;
            online.Remove(id);
            if (r.Opted) return;
            Accrue(r, now);
            state.Open.Remove(r.Key);
            RecordSession(r.Start, now, reason);
        }

        private void RecordSession(long start, long end, string reason)
        {
            long len = end - start;
            if (len < 0) return;
            if (len < config.MinSessionSeconds) { Bump(day.Dropped, "short_sessions", 1); return; }
            if (day.Sessions.Count >= config.MaxSessionsPerDay) { Bump(day.Dropped, "sessions", 1); return; }
            SessionRec s = new SessionRec();
            s.S = start;
            s.D = len > int.MaxValue ? int.MaxValue : (int)len;
            s.E = reason;
            day.Sessions.Add(s);
        }

        // Sessions left in state.json by a crash (or a stop without OnServerShutdown) end at the last heartbeat.
        private void CloseOrphanSessions(long now)
        {
            List<string> keys = new List<string>(state.Open.Keys);
            Dictionary<string, bool> live = new Dictionary<string, bool>();
            foreach (OnlineRec r in online.Values) live[r.Key] = true;
            foreach (string k in keys)
            {
                if (live.ContainsKey(k)) continue;
                OpenSession o = state.Open[k];
                state.Open.Remove(k);
                if (o == null || o.S <= 0) continue;
                long end = state.Heartbeat > o.S ? state.Heartbeat : o.S;
                if (end > now) end = now;
                if (end - o.S > 86400L * 2) end = o.S + 86400L * 2;      // never invent a session longer than two days
                RecordSession(o.S, end, "recovered");
            }
        }

        private void Accrue(OnlineRec r, long now)
        {
            if (r.Opted || !config.TrackHouses) { r.LastAccrue = now; return; }
            long delta = now - r.LastAccrue;
            long cap = config.SampleMinutes * 60L * 2 + (long)TickSeconds * 2;
            if (delta > cap) delta = cap;                // never credit more than a missed tick or two
            if (delta > 0 && r.House.Length > 0)
            {
                HouseDay h = House(r.House);
                if (h != null) h.Seconds += delta;
            }
            r.LastAccrue = now;
        }

        private void AccrueAll(long now)
        {
            foreach (OnlineRec r in online.Values) Accrue(r, now);
        }

        #endregion

        #region Tick, sampling, rotation

        private void Tick()
        {
            if (!Collecting()) return;
            long now = Now();
            EnsureDay(now);
            if (now >= nextSample)
            {
                Reconcile(now);
                AccrueAll(now);
                RefreshHouses();
                TakeSample(now);
            }
            ScrubStep();
            if (now - lastSave >= config.SaveIntervalSeconds) SaveAll();
        }

        private void TakeSample(long now)
        {
            int n = online.Count;
            if (n > intervalMax) intervalMax = n;
            if (day.Concurrency.Count < MaxSamplesPerDay)
            {
                Sample s = new Sample();
                s.T = BucketStart(now);
                s.N = n;
                s.Max = intervalMax;
                if (day.Concurrency.Count > 0 && day.Concurrency[day.Concurrency.Count - 1].T == s.T)
                    day.Concurrency[day.Concurrency.Count - 1] = s;
                else
                    day.Concurrency.Add(s);
            }
            else Bump(day.Dropped, "samples", 1);
            intervalMax = n;
            nextSample = BucketStart(now) + config.SampleMinutes * 60L;
        }

        // Keeps the online table in step with the game's own list, in case a disconnect hook was missed.
        private void Reconcile(long now)
        {
            Dictionary<ulong, Player> live = new Dictionary<ulong, Player>();
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                live[p.Id] = p;
            }
            List<ulong> gone = new List<ulong>();
            foreach (ulong id in online.Keys) if (!live.ContainsKey(id)) gone.Add(id);
            foreach (ulong id in gone) EndSession(id, now, "reconcile");
            foreach (KeyValuePair<ulong, Player> kv in live)
                if (!online.ContainsKey(kv.Key)) StartSession(kv.Value, now, false);
        }

        private void RefreshHouses()
        {
            if (!config.TrackHouses) return;
            foreach (KeyValuePair<ulong, OnlineRec> kv in online)
            {
                OnlineRec r = kv.Value;
                if (r.Opted) continue;
                Player p = Server.GetPlayerById(kv.Key);
                if (p == null) continue;
                string h = LookupHouse(p) ?? "";
                if (h != r.House)
                {
                    r.House = h;
                    OpenSession o;
                    if (state.Open.TryGetValue(r.Key, out o) && o != null) o.H = h;
                }
                if (h.Length > 0) MarkHouseActive(h, r.Key);
            }
        }

        private void EnsureDay(long now)
        {
            string date = DateOf(now);
            if (day != null && day.Date == date) return;
            Rollover(now, date);
        }

        private void Rollover(long now, string date)
        {
            if (day != null)
            {
                AccrueAll(now);
                WriteDay();
            }
            OpenDay(now);
            // Everyone still online is active on the new day too.
            foreach (OnlineRec r in online.Values)
            {
                if (r.Opted) continue;
                MarkActive(r.Key);
                if (r.House.Length > 0) MarkHouseActive(r.House, r.Key);
            }
            PruneOldDays(date);
            SaveAll();
        }

        private void OpenDay(long now)
        {
            string date = DateOf(now);
            dayNote = "";
            DayData loaded = null;
            string name = DayFile(date, 0);
            int suffix = 0;
            while (suffix < 50)
            {
                name = DayFile(date, suffix);
                if (!Exists(name)) break;
                try
                {
                    loaded = Interface.Oxide.DataFileSystem.ReadObject<DayData>(name);
                    if (loaded != null && (string.IsNullOrEmpty(loaded.Date) || loaded.Date == date)) break;
                    if (loaded == null) PrintError("oxide/data/" + name + ".json is empty; it is kept and today's data goes to a new file.");
                    else PrintError("oxide/data/" + name + ".json holds another date (" + loaded.Date + "); it is kept and today's data goes to a new file.");
                    loaded = null;
                }
                catch (Exception ex)
                {
                    PrintError("Could not parse oxide/data/" + name + ".json: " + ex.Message + ". It is kept unchanged; today's data goes to a new file.");
                    loaded = null;
                }
                dayNote = "recovery file " + DayFile(date, suffix + 1);
                suffix++;
            }
            if (suffix >= 50) { configFailed = true; PrintError("Too many unreadable day files for " + date + "; collection stopped."); }

            day = loaded ?? new DayData();
            dayName = name;
            day.Date = date;
            day.Server = config.ServerLabel;
            day.SaltId = saltId;
            day.SampleMinutes = config.SampleMinutes;
            if (stateFailed) day.Degraded = true;
            RepairDay(day);
            activeSet.Clear();
            newSet.Clear();
            houseActiveSet.Clear();
            foreach (string k in day.Active) activeSet[k] = true;
            foreach (string k in day.New) newSet[k] = true;
            foreach (KeyValuePair<string, HouseDay> kv in day.Houses)
            {
                Dictionary<string, bool> set = new Dictionary<string, bool>();
                foreach (string k in kv.Value.Active) set[k] = true;
                houseActiveSet[kv.Key] = set;
            }
            if (!stateFailed && !state.Days.Contains(dayName)) state.Days.Add(dayName);
        }

        private static void RepairDay(DayData d)
        {
            if (d.Active == null) d.Active = new List<string>();
            if (d.New == null) d.New = new List<string>();
            if (d.Sessions == null) d.Sessions = new List<SessionRec>();
            d.Sessions.RemoveAll(delegate(SessionRec s) { return s == null; });
            if (d.Joins == null || d.Joins.Length != 24) d.Joins = new int[24];
            if (d.Leaves == null || d.Leaves.Length != 24) d.Leaves = new int[24];
            if (d.Concurrency == null) d.Concurrency = new List<Sample>();
            d.Concurrency.RemoveAll(delegate(Sample s) { return s == null; });
            if (d.Deaths == null) d.Deaths = new Dictionary<string, int>();
            if (d.Houses == null) d.Houses = new Dictionary<string, HouseDay>();
            if (d.Dropped == null) d.Dropped = new Dictionary<string, int>();
            List<string> bad = new List<string>();
            foreach (KeyValuePair<string, HouseDay> kv in d.Houses)
            {
                if (kv.Value == null) { bad.Add(kv.Key); continue; }
                if (kv.Value.Active == null) kv.Value.Active = new List<string>();
            }
            foreach (string k in bad) d.Houses.Remove(k);
        }

        // Deletes day files older than RetentionDays, using the index in state.json (never a directory scan).
        private void PruneOldDays(string today)
        {
            if (stateFailed) return;
            DateTime t;
            if (!TryParseDate(today, out t)) return;
            string cutoff = t.AddDays(-config.RetentionDays).ToString("yyyy-MM-dd");
            List<string> keep = new List<string>();
            foreach (string name in state.Days)
            {
                string d = DateFromFile(name);
                if (d != null && string.CompareOrdinal(d, cutoff) < 0)
                {
                    try { Interface.Oxide.DataFileSystem.DeleteDataFile(name); }
                    catch (Exception ex) { PrintWarning("Could not delete " + name + ": " + ex.Message); keep.Add(name); }
                }
                else keep.Add(name);
            }
            state.Days = keep;
        }

        #endregion

        #region Activity helpers

        private void MarkActive(string key)
        {
            string today = day.Date;
            PlayerSeen seen;
            if (!state.Players.TryGetValue(key, out seen) || seen == null)
            {
                seen = new PlayerSeen();
                seen.F = today;
                state.Players[key] = seen;
                if (!stateFailed && !newSet.ContainsKey(key))
                {
                    if (day.New.Count < config.MaxActivePerDay) { newSet[key] = true; day.New.Add(key); }
                    else Bump(day.Dropped, "new", 1);
                }
                if (state.Players.Count > config.MaxTrackedPlayers) PrunePlayers();
            }
            seen.L = today;
            if (!activeSet.ContainsKey(key))
            {
                if (day.Active.Count < config.MaxActivePerDay) { activeSet[key] = true; day.Active.Add(key); }
                else Bump(day.Dropped, "active", 1);
            }
        }

        private void MarkHouseActive(string house, string key)
        {
            HouseDay h = House(house);
            if (h == null) return;
            string name = houseActiveSet.ContainsKey(house) ? house : OtherHouse;
            Dictionary<string, bool> set;
            if (!houseActiveSet.TryGetValue(name, out set)) { set = new Dictionary<string, bool>(); houseActiveSet[name] = set; }
            if (set.ContainsKey(key)) return;
            if (h.Active.Count >= config.MaxActivePerDay) { Bump(day.Dropped, "house_active", 1); return; }
            set[key] = true;
            h.Active.Add(key);
        }

        // Returns the day's record for a house, creating it under the cap; past the cap everything goes to "(other)".
        private HouseDay House(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            HouseDay h;
            if (day.Houses.TryGetValue(name, out h)) return h;
            if (day.Houses.Count >= config.MaxHousesPerDay)
            {
                Bump(day.Dropped, "houses", 1);
                name = OtherHouse;
                if (day.Houses.TryGetValue(name, out h)) return h;
            }
            h = new HouseDay();
            day.Houses[name] = h;
            houseActiveSet[name] = new Dictionary<string, bool>();
            return h;
        }

        private void PrunePlayers()
        {
            int target = config.MaxTrackedPlayers * 9 / 10;
            List<KeyValuePair<string, PlayerSeen>> all = new List<KeyValuePair<string, PlayerSeen>>(state.Players);
            all.Sort(delegate(KeyValuePair<string, PlayerSeen> a, KeyValuePair<string, PlayerSeen> b)
            {
                return string.CompareOrdinal(a.Value != null ? a.Value.L : "", b.Value != null ? b.Value.L : "");
            });
            int remove = all.Count - target;
            for (int i = 0; i < remove && i < all.Count; i++) state.Players.Remove(all[i].Key);
        }

        private string HouseForEvent(Player p)
        {
            if (p == null || p.IsServer) return null;
            OnlineRec r;
            if (online.TryGetValue(p.Id, out r))
            {
                if (r.Opted) return null;
                return r.House.Length > 0 ? r.House : null;
            }
            if (optOut.ContainsKey(KeyOf(p.Id))) return null;
            return LookupHouse(p);
        }

        private string LookupHouse(Player p)
        {
            if (!config.TrackHouses || config.HouseSource == "off" || p == null) return null;
            try
            {
                bool useRealm = config.HouseSource == "realmhouses" || (config.HouseSource == "auto" && RealmHouses != null);
                if (useRealm)
                {
                    if (RealmHouses == null) return null;
                    string name = RealmHouses.Call("GetHouse", p.Id.ToString()) as string;
                    return string.IsNullOrEmpty(name) ? null : CleanName(name, MaxHouseNameLength);
                }
                // UNVERIFIED in game: GetGuild() for a player without a guild (expected null).
                Guild g = p.GetGuild();
                if (g == null || string.IsNullOrEmpty(g.Name)) return null;
                string n = CleanName(g.Name, MaxHouseNameLength);
                return n.Length > 0 ? n : null;
            }
            catch (Exception ex)
            {
                if (!Cooldown("houseerr", 300)) PrintWarning("House lookup failed: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Opt-out and scrubbing

        private void OptOutKey(string key, ulong id, long now)
        {
            if (optOut.ContainsKey(key)) return;
            if (state.OptOut.Count >= config.MaxOptOuts) { optOut.Remove(state.OptOut[0]); state.OptOut.RemoveAt(0); }
            optOut[key] = true;
            state.OptOut.Add(key);
            state.Players.Remove(key);
            state.Open.Remove(key);
            OnlineRec r;
            if (online.TryGetValue(id, out r)) { r.Opted = true; r.House = ""; }
            RemoveKeyFromDay(day, key);
            activeSet.Remove(key);
            newSet.Remove(key);
            foreach (Dictionary<string, bool> set in houseActiveSet.Values) set.Remove(key);
            if (!state.ScrubQueue.Contains(key)) state.ScrubQueue.Add(key);
            scrubCursor = 0;
        }

        private void OptInKey(string key, Player p, long now)
        {
            if (!optOut.ContainsKey(key)) return;
            optOut.Remove(key);
            state.OptOut.Remove(key);
            state.ScrubQueue.Remove(key);
            OnlineRec r;
            if (online.TryGetValue(p.Id, out r))
            {
                r.Opted = false;
                r.Start = now;
                r.LastAccrue = now;
                MarkActive(key);
                r.House = LookupHouse(p) ?? "";
                if (r.House.Length > 0) MarkHouseActive(r.House, key);
                OpenSession os = new OpenSession();
                os.S = now;
                os.H = r.House;
                state.Open[key] = os;
            }
        }

        private static bool RemoveKeyFromDay(DayData d, string key)
        {
            bool changed = d.Active.Remove(key);
            changed |= d.New.Remove(key);
            foreach (HouseDay h in d.Houses.Values) changed |= h.Active.Remove(key);
            return changed;
        }

        // Removes queued opted-out keys from retained day files, a couple of files per tick so the server never stalls.
        // An unreadable file is skipped and left untouched.
        private void ScrubStep()
        {
            if (stateFailed || state.ScrubQueue.Count == 0) return;
            for (int n = 0; n < ScrubFilesPerTick; n++)
            {
                if (scrubCursor >= state.Days.Count)
                {
                    state.ScrubQueue.Clear();
                    scrubCursor = 0;
                    return;
                }
                string name = state.Days[scrubCursor++];
                if (name == dayName) continue;           // today's file is cleaned in memory
                if (!Exists(name)) continue;
                DayData d;
                try { d = Interface.Oxide.DataFileSystem.ReadObject<DayData>(name); }
                catch (Exception ex) { PrintWarning("Scrub skipped unreadable " + name + ": " + ex.Message); continue; }
                if (d == null) continue;
                RepairDay(d);
                bool changed = false;
                foreach (string key in state.ScrubQueue) changed |= RemoveKeyFromDay(d, key);
                if (changed) Interface.Oxide.DataFileSystem.WriteObject(name, d);
            }
        }

        #endregion

        #region Persistence

        private void LoadState()
        {
            StateData loaded = null;
            bool existed = false;
            try
            {
                existed = Exists(StateName);
                if (existed) loaded = Interface.Oxide.DataFileSystem.ReadObject<StateData>(StateName);
            }
            catch (Exception ex)
            {
                stateFailed = true;
                state = new StateData();
                PrintError("Could not read oxide/data/" + StateName + ".json: " + ex.Message
                    + ". Collecting in memory; days are marked Degraded and the file will NOT be overwritten. Fix or remove it, then reload.");
                return;
            }
            if (loaded == null && existed)
            {
                stateFailed = true;
                state = new StateData();
                PrintError("oxide/data/" + StateName + ".json is empty. Collecting in memory; the file will NOT be overwritten.");
                return;
            }
            state = loaded ?? new StateData();
            if (state.Days == null) state.Days = new List<string>();
            state.Days.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s) || DateFromFile(s) == null; });
            if (state.Players == null) state.Players = new Dictionary<string, PlayerSeen>();
            if (state.Open == null) state.Open = new Dictionary<string, OpenSession>();
            if (state.OptOut == null) state.OptOut = new List<string>();
            if (state.ScrubQueue == null) state.ScrubQueue = new List<string>();
            List<string> bad = new List<string>();
            foreach (KeyValuePair<string, PlayerSeen> kv in state.Players) if (kv.Value == null || kv.Value.F == null) bad.Add(kv.Key);
            foreach (string k in bad) state.Players.Remove(k);
            foreach (string k in state.OptOut) if (!string.IsNullOrEmpty(k)) optOut[k] = true;
            if (!configFailed && state.SaltId.Length > 0 && state.SaltId != saltId)
            {
                // The salt changed: old keys can never match again. Start a fresh first-seen table so new keys are not
                // mistaken for returning players; the dashboard keeps cohorts with different SaltIds apart.
                PrintWarning("The salt in the config changed since the last run. First-seen history restarts (retention across the change is not comparable).");
                state.Players.Clear();
                state.Open.Clear();
                state.OptOut.Clear();
                state.ScrubQueue.Clear();
                optOut.Clear();
            }
            state.SaltId = saltId;
        }

        private void WriteDay()
        {
            if (day == null || configFailed) return;
            Interface.Oxide.DataFileSystem.WriteObject(dayName, day);
        }

        private void SaveAll()
        {
            if (state == null || day == null || configFailed) return;
            long now = Now();
            WriteDay();
            if (!stateFailed)
            {
                state.Heartbeat = now;
                foreach (OnlineRec r in online.Values)
                {
                    if (r.Opted) continue;
                    OpenSession o;
                    if (state.Open.TryGetValue(r.Key, out o) && o != null) o.H = r.House;
                }
                Interface.Oxide.DataFileSystem.WriteObject(StateName, state);
            }
            lastSave = now;
        }

        private static bool Exists(string name)
        {
            return Interface.Oxide.DataFileSystem.ExistsDatafile(name);
        }

        #endregion

        #region Commands

        [ChatCommand("stats")]
        private void CmdStats(Player player, string command, string[] args)
        {
            if (player == null) return;
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            bool admin = IsAdmin(player);
            if (!admin && Cooldown("cmd|" + player.Id, config.CommandCooldownSeconds)) { ReplyError(player, "Slow"); return; }
            long now = Now();
            switch (sub)
            {
                case "privacy":
                    Reply(player, "Privacy1");
                    Reply(player, "Privacy2", config.RetentionDays);
                    if (config.AllowOptOut) Reply(player, "Privacy3");
                    return;
                case "me":
                {
                    string key = KeyOf(player.Id);
                    if (optOut.ContainsKey(key)) { Reply(player, "MeOptedOut"); return; }
                    PlayerSeen seen;
                    string first = state.Players.TryGetValue(key, out seen) && seen != null ? seen.F : "-";
                    Reply(player, "Me", key.Substring(0, 6), first, config.RetentionDays);
                    return;
                }
                case "optout":
                {
                    if (!config.AllowOptOut) { ReplyError(player, "OptDisabled"); return; }
                    if (!Collecting()) { ReplyError(player, "NotCollecting"); return; }
                    string key = KeyOf(player.Id);
                    if (optOut.ContainsKey(key)) { Reply(player, "AlreadyOut"); return; }
                    if (!admin && Cooldown("opt|" + player.Id, config.OptToggleCooldownSeconds)) { ReplyError(player, "Slow"); return; }
                    EnsureDay(now);
                    OptOutKey(key, player.Id, now);
                    SaveAll();
                    Reply(player, "OptedOut");
                    return;
                }
                case "optin":
                {
                    if (!Collecting()) { ReplyError(player, "NotCollecting"); return; }
                    string key = KeyOf(player.Id);
                    if (!optOut.ContainsKey(key)) { Reply(player, "AlreadyIn"); return; }
                    if (!admin && Cooldown("opt|" + player.Id, config.OptToggleCooldownSeconds)) { ReplyError(player, "Slow"); return; }
                    EnsureDay(now);
                    OptInKey(key, player, now);
                    SaveAll();
                    Reply(player, "OptedIn");
                    return;
                }
                case "status":
                    if (!admin) { ReplyError(player, "NoPermission"); return; }
                    ShowStatus(player, now);
                    return;
                case "save":
                    if (!admin) { ReplyError(player, "NoPermission"); return; }
                    if (Cooldown("save", config.AdminSaveCooldownSeconds)) { ReplyError(player, "Slow"); return; }
                    if (!Collecting()) { ReplyError(player, "NotCollecting"); return; }
                    EnsureDay(now);
                    AccrueAll(now);
                    SaveAll();
                    Reply(player, "Saved", dayName);
                    return;
                default:
                    Reply(player, "Help1");
                    Reply(player, "Help2");
                    if (config.AllowOptOut) Reply(player, "Help3");
                    if (admin) Reply(player, "HelpAdmin");
                    return;
            }
        }

        private void ShowStatus(Player player, long now)
        {
            if (!Collecting()) { ReplyError(player, "NotCollecting"); return; }
            EnsureDay(now);
            int deaths = 0;
            foreach (int v in day.Deaths.Values) deaths += v;
            int joins = 0, leaves = 0, peak = 0;
            for (int i = 0; i < 24; i++) { joins += day.Joins[i]; leaves += day.Leaves[i]; }
            foreach (Sample s in day.Concurrency) if (s.Max > peak) peak = s.Max;
            if (intervalMax > peak) peak = intervalMax;
            Reply(player, "Status1", day.Date, online.Count, peak, day.Active.Count, day.New.Count);
            Reply(player, "Status2", day.Sessions.Count, joins, leaves, deaths, day.Houses.Count);
            string health = stateFailed ? Msg("Degraded", player) : Msg("Healthy", player);
            Reply(player, "Status3", health, dayName, state.Days.Count, config.RetentionDays, saltId, state.Players.Count, optOut.Count);
            if (dayNote.Length > 0) Reply(player, "Status4", dayNote);
            if (state.ScrubQueue.Count > 0) Reply(player, "Scrubbing", state.ScrubQueue.Count, scrubCursor, state.Days.Count);
        }

        #endregion

        #region API (plugin.Call; must stay non-public)

        // Today's headline numbers for other plugins. Keys: date, online, peak, active, new, sessions, houses, degraded.
        private Dictionary<string, object> GetStatsSummary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            if (!Collecting() || day == null) return d;
            int peak = intervalMax;
            foreach (Sample s in day.Concurrency) if (s.Max > peak) peak = s.Max;
            d["date"] = day.Date;
            d["online"] = online.Count;
            d["peak"] = peak;
            d["active"] = day.Active.Count;
            d["new"] = day.New.Count;
            d["sessions"] = day.Sessions.Count;
            d["houses"] = day.Houses.Count;
            d["degraded"] = stateFailed;
            return d;
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

        #region Lang and helpers

        protected override void LoadDefaultMessages()
        {
            Dictionary<string, string> m = new Dictionary<string, string>();
            m["Speaker"] = "Stats";
            m["Help1"] = "This server keeps privacy-friendly play statistics (no names, no Steam ids, no positions).";
            m["Help2"] = "  [F4C96D]/stats privacy[FFFFFF] - what is recorded.  [F4C96D]/stats me[FFFFFF] - what is stored about you.";
            m["Help3"] = "  [F4C96D]/stats optout[FFFFFF] - stop being counted by key (and remove your key from kept days).  [F4C96D]/stats optin[FFFFFF] - undo.";
            m["HelpAdmin"] = "  Admin: [F4C96D]/stats status[FFFFFF] - today's numbers and data health.  [F4C96D]/stats save[FFFFFF] - write the files now.";
            m["Privacy1"] = "Recorded: a salted one-way key per player, session start and length (without the key), players online every few minutes, joins and leaves per hour, death causes, and house activity.";
            m["Privacy2"] = "  Never recorded: names, Steam ids, IPs, chat, positions or items. Day files are deleted after {0} days and stay on this server.";
            m["Privacy3"] = "  Type [F4C96D]/stats optout[FFFFFF] to remove your key; you then only count in anonymous totals.";
            m["PrivacyNotice"] = "This server keeps anonymous play statistics. Type [F4C96D]/stats privacy[FFFFFF] to learn more or [F4C96D]/stats optout[FFFFFF] to opt out.";
            m["Me"] = "Your key starts with {0} (the full key cannot be turned back into your Steam id without the server's secret). First seen: {1}. Kept for {2} days.";
            m["MeOptedOut"] = "You have opted out. Nothing is stored about you except that opt-out choice (as a key).";
            m["OptedOut"] = "Done. Your key was removed from today's data and will be removed from older kept days over the next minutes.";
            m["OptedIn"] = "Welcome back. You are counted again from now on (as a new player for retention).";
            m["AlreadyOut"] = "You have already opted out.";
            m["AlreadyIn"] = "You are not opted out.";
            m["OptDisabled"] = "Opting out is turned off on this server. Ask an admin.";
            m["NotCollecting"] = "Statistics are paused because the config could not be read. An admin must fix oxide/config/RealmStats.json.";
            m["NoPermission"] = "You may not do that.";
            m["Slow"] = "Please wait a moment before using that again.";
            m["Saved"] = "Saved ({0}).";
            m["Status1"] = "{0} UTC | online {1} | peak {2} | active players {3} | new {4}";
            m["Status2"] = "  sessions {0} | joins {1} | leaves {2} | deaths {3} | houses {4}";
            m["Status3"] = "  data {0} | file {1} | {2} day files kept (max {3} days) | salt id {4} | {5} keys known | {6} opted out";
            m["Status4"] = "  note: {0}";
            m["Scrubbing"] = "Removing {0} opted-out key(s) from old files: {1}/{2} done.";
            m["Healthy"] = "OK";
            m["Degraded"] = "DEGRADED (state.json unreadable; fix it and reload)";
            lang.RegisterMessages(m, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
        }

        private string Fmt(string key, Player player, object[] args)
        {
            string text = Msg(key, player);
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); }
            catch (FormatException) { return text; }
        }

        private void Reply(Player player, string key, params object[] args)
        {
            string tone = key == "OptedOut" || key == "OptedIn" || key == "Saved" ? ChatOk : ChatGold;   // chat style: done, or news
            player.SendMessage(Styled(Msg("Speaker", player), tone, Fmt(key, player, args)));   // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        private bool IsAdmin(Player player)
        {
            return permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        // True when the key is still cooling down; otherwise starts the cooldown and returns false.
        private bool Cooldown(string key, int seconds)
        {
            if (seconds <= 0) return false;
            long now = Now();
            long until;
            if (cooldowns.TryGetValue(key, out until) && now < until) return true;
            if (cooldowns.Count > 2000) cooldowns.Clear();
            cooldowns[key] = now + seconds;
            return false;
        }

        private bool Collecting()
        {
            return !configFailed && saltBytes != null && state != null && day != null;
        }

        private long Now()
        {
            return (long)(DateTime.UtcNow - Epoch).TotalSeconds + clockOffset;
        }

        private long BucketStart(long t)
        {
            long step = config.SampleMinutes * 60L;
            return t - (t % step);
        }

        private static int HourOf(long t)
        {
            return Epoch.AddSeconds(t).Hour;
        }

        private static string DateOf(long t)
        {
            return Epoch.AddSeconds(t).ToString("yyyy-MM-dd");
        }

        private static string DayFile(string date, int suffix)
        {
            return Dir + "/day-" + date + (suffix > 0 ? "-r" + suffix : "");
        }

        private static string DateFromFile(string name)
        {
            int i = name.LastIndexOf("day-", StringComparison.Ordinal);
            if (i < 0 || name.Length < i + 14) return null;
            string d = name.Substring(i + 4, 10);
            DateTime t;
            return TryParseDate(d, out t) ? d : null;
        }

        private static bool TryParseDate(string s, out DateTime t)
        {
            return DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out t);
        }

        private static void Bump(Dictionary<string, int> d, string key, int by)
        {
            int v;
            d.TryGetValue(key, out v);
            d[key] = v + by;
        }

        private static string CleanName(string s, int max)
        {
            if (s == null) return "";
            StringBuilder b = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsControl(c) || c == '[' || c == ']' || c == '<' || c == '>') continue;
                b.Append(c);
                if (b.Length >= max) break;
            }
            return b.ToString().Trim();
        }

        private void SetSalt(string salt)
        {
            if (string.IsNullOrEmpty(salt)) { saltBytes = null; saltId = ""; return; }
            saltBytes = Encoding.UTF8.GetBytes(salt);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] id = sha.ComputeHash(Encoding.UTF8.GetBytes("realmstats-salt-id|" + salt));
                saltId = Hex(id, 4);
            }
            keyCache.Clear();
        }

        // Pseudonymous player key: HMAC-SHA256(salt, "player|" + SteamID64), first 8 bytes as hex.
        private string KeyOf(ulong id)
        {
            string k;
            if (keyCache.TryGetValue(id, out k)) return k;
            if (saltBytes == null) return "0000000000000000";
            using (HMACSHA256 h = new HMACSHA256(saltBytes))
            {
                k = Hex(h.ComputeHash(Encoding.UTF8.GetBytes("player|" + id.ToString())), 8);
            }
            if (keyCache.Count > 5000) keyCache.Clear();
            keyCache[id] = k;
            return k;
        }

        private static string NewSalt()
        {
            byte[] b = new byte[32];
            RandomNumberGenerator rng = RandomNumberGenerator.Create();
            rng.GetBytes(b);
            return Hex(b, b.Length);
        }

        private static string Hex(byte[] b, int count)
        {
            StringBuilder s = new StringBuilder(count * 2);
            for (int i = 0; i < count && i < b.Length; i++) s.Append(b[i].ToString("x2"));
            return s.ToString();
        }

        #endregion
    }
}
