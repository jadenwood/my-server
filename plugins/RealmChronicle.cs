// RealmChronicle: the public record of the realm.
//
// Writes two data files that the chronicle web service (chronicle/server.js) reads:
//   oxide/data/RealmChronicle.json  JSON array of events {id, ts, type, title, detail, actors[]}
//   oxide/data/RealmState.json      {king, house, since, houses[], online, maxPlayers, updated, next}
//
// `next` is {title, at} (at = UTC ISO 8601, "yyyy-MM-ddTHH:mm:ssZ") for the soonest scheduled realm event that has
// not started and is at most NextEventHorizonDays ahead, or null when there is none. Candidates, polled on every
// state refresh through non-public plugin.Call hooks:
//   CrownAndConsequences.Call("GetNextRebellionWindow") -> boxed UTC DateTime or null   (title "Rebellion window")
//   RealmEvents.Call("GetNextEvent") -> Dictionary<string, object> { "title": string, "at": DateTime (UTC) } or null
// RealmEvents implements the second (plugins/RealmEvents.cs GetNextEvent); neither has been seen on a live server.
//
// Other plugins log events with RealmChronicle.Call("Log", type, title, detail, actors).
// Only public names go into the chronicle: never positions, inventories or Steam ids.
//
// Flood budget (config "FloodBudget"). Every line reaches the overlay, the portal and the Discord herald, and the file
// keeps only the last MaxEvents lines, so a burst from any plugin or player-triggered path could push the realm's
// history out in hours. Each type may write PerTypePerWindow lines (PerType overrides it per type) and all foldable
// types together GlobalPerWindow lines per WindowMinutes (sliding window). A line over budget is FOLDED: counted, and
// later written as one summary line of the same type ("12 more contract posted entries") with no actors. A summary is
// written at most once per type per window, once the burst has been quiet for SummaryQuietSeconds (or a window after
// it began, or on unload). Never folded: coronation, abdication, claim_declared, rebellion_started and
// rebellion_ended (built in), plus NeverFold (by default the types other plugins count from this file, and the
// scheduled events, which their own plugins bound). Log returns -1 for a folded line and for a recent duplicate, and
// 0 only for a line it will never take (unknown type, empty title). Callers that fall back to "decree" on 0, or stop
// logging a type after a 0, therefore do not write a line twice or give up on a type because of a burst.
//
// A RealmChronicle.json that exists but cannot be read (a power cut can leave it empty or "null") is never overwritten:
// new lines are kept in memory until the owner fixes the file or moves it away.
//
// Language level: C# 3 syntax only (no expression-bodied members, index initializers, $"", ?. or nameof),
// against the .NET 3.5 API surface, so the file builds with any Oxide compiler generation.
//
// Cross-plugin API methods MUST stay non-public: Oxide.CSharp (CSharpPlugin.cs @49500b8, ctor) registers
// only NonPublic|Instance methods as callable hooks, so a public method is invisible to plugin.Call.

using System;
using System.Collections.Generic;
using CodeHatch.Common;                       // PlayerExtensions: SendMessage, GetGuild             [ASM]
using CodeHatch.Engine.Modules.SocialSystem;  // SocialAPI, Members                                  [ASM]
using CodeHatch.Engine.Networking;            // Player, Server                                      [ASM]
using CodeHatch.Networking.Events;            // BaseEvent / NetworkEvent                            [ASM]
using CodeHatch.Thrones.AncientThrone;        // AncientThroneCaptureEvent, AncientThroneReleaseEvent [ASM]
using CodeHatch.Thrones.SocialSystem;         // Guild, GuildScheme, KingsScheme                     [ASM]
using Oxide.Core;                             // Interface.Oxide.DataFileSystem                      [SRC]
using Oxide.Core.Plugins;                     // Plugin (for [PluginReference])

namespace Oxide.Plugins
{
    [Info("RealmChronicle", "Realm", "0.1.0")]
    [Description("Keeps the Realm Chronicle event log and the RealmState snapshot for overlays and the public page")]
    public class RealmChronicle : ReignOfKingsPlugin
    {
        private const string EventsFile = "RealmChronicle";
        private const string StateFile = "RealmState";
        private const int TitleMax = 140;
        private const int DetailMax = 400;
        private const int ActorMax = 48;
        private const int ActorsMax = 8;
        private const int NextTitleMax = 80;

        private static readonly string[] KnownTypes =
        {
            "coronation", "abdication", "claim_declared", "rebellion_started", "rebellion_ended",
            "house_founded", "oath_sworn", "oath_broken", "treaty_signed", "treaty_broken",
            "decree", "ransom_set", "ransom_paid", "released",
            "contract_posted", "contract_fulfilled", "contract_ended",
            // RealmSeasons and RealmEvents
            "season_started", "season_ended", "event_started", "event_ended",
            "tournament_champion", "hunt_kill", "truce_broken",
            // RealmLaws, RealmDynasties, RealmRenown
            "law_proclaimed", "law_repealed", "accusation", "trial_by_combat", "verdict", "pardon",
            "dynasty_founded", "heir_named", "succession", "blood_claim", "blood_restored", "title_bestowed",
            "title_earned",
            // RealmTreasury, RealmRavens
            "treasury_mint", "treasury_grant", "tithe_levied", "great_trade", "rumour"
        };

        // [SRC] Oxide.CSharp src/CSharpPlugin.cs:91 (PluginReferenceAttribute; the field is filled with the plugin
        // whose name matches the field name) and Oxide.Core src/Plugins/Plugin.cs:311 (Plugin.Call).
        // Both references are optional; null means "not loaded".
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmEvents;

        private PluginConfig config;
        private List<ChronicleEvent> events;
        private RealmStateData state;
        private int nextId = 1;
        private bool eventsLoadFailed;                 // RealmChronicle.json could not be read: never overwrite it
        private Timer refreshTimer;
        private bool refreshQueued;

        // Flood budget state, in memory only. Pending folds are written as summaries on unload.
        private readonly Dictionary<string, Queue<DateTime>> typeTimes = new Dictionary<string, Queue<DateTime>>();
        private readonly Queue<DateTime> globalTimes = new Queue<DateTime>();
        private readonly Dictionary<string, FoldedBurst> folded = new Dictionary<string, FoldedBurst>();
        private readonly Dictionary<string, DateTime> lastSummary = new Dictionary<string, DateTime>();

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        #region Data shapes (field names are the JSON contract, hence lower case)

        private class PluginConfig
        {
            public int MaxEvents = 500;                     // retention cap for RealmChronicle.json
            public float StateRefreshSeconds = 30f;
            public int ChatDefaultCount = 5;
            public int ChatMaxCount = 15;
            // Used only when CrownAndConsequences is not loaded, so crown changes are never logged twice.
            public bool LogThroneEventsWithoutCrownPlugin = true;
            public bool EchoToConsole = true;
            // Drop an event identical (type, title, detail) to one logged within this many seconds (anti-spam).
            public int DuplicateWindowSeconds = 300;
            // RealmState.next only lists events starting within this many days (the player app ignores later ones).
            public int NextEventHorizonDays = 45;
            // Per-type and global rate budget; lines over it are folded into one summary line (see header).
            public FloodBudgetSettings FloodBudget = new FloodBudgetSettings();
        }

        // The lists start null and are filled in Init, so a config file's own lists replace the defaults instead of
        // being merged into them.
        private class FloodBudgetSettings
        {
            public bool Enabled = true;
            public int WindowMinutes = 60;
            public int GlobalPerWindow = 30;           // all foldable types together
            public int PerTypePerWindow = 8;           // any one type, unless PerType says otherwise
            public Dictionary<string, int> PerType;    // type -> lines per window (0 = every line goes into the summary)
            public List<string> NeverFold;             // in addition to the built-in crown and rebellion types
            public int SummaryQuietSeconds = 120;
        }

        // Crown changes and rebellion milestones: never folded, whatever the config says.
        private static readonly string[] AlwaysWritten = { "coronation", "abdication", "claim_declared", "rebellion_started", "rebellion_ended" };

        private static Dictionary<string, int> DefaultPerType()
        {
            var d = new Dictionary<string, int>();
            d["decree"] = 12;
            d["contract_fulfilled"] = 12;
            d["rumour"] = 4;
            d["title_earned"] = 6;
            return d;
        }

        // Types other plugins count from RealmChronicle.json (RealmSeasons: treaties and oaths; RealmRenown: event
        // deeds), succession, and the scheduled events, which RealmSeasons and RealmEvents already bound.
        private static List<string> DefaultNeverFold()
        {
            return new List<string>
            {
                "treaty_signed", "treaty_broken", "oath_broken", "succession", "blood_claim",
                "season_started", "season_ended", "event_started", "event_ended", "tournament_champion", "hunt_kill", "truce_broken"
            };
        }

        private class FoldedBurst
        {
            public int Count;
            public DateTime First;
            public DateTime Last;
            public string LastTitle;
        }

        private class ChronicleEvent
        {
            public int id;
            public string ts;
            public string type;
            public string title;
            public string detail;
            public string[] actors;
        }

        private class HouseEntry
        {
            public string name;
            public string sigil;
            public string liege;
            public int members;
        }

        private class RealmStateData
        {
            public string king;
            public string house;
            public string since;
            public List<HouseEntry> houses = new List<HouseEntry>();
            public int online;
            public int maxPlayers;
            public string updated;
            public NextEvent next;
        }

        private class NextEvent
        {
            public string title;
            public string at;
        }

        #endregion

        #region Lifecycle

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Chronicle" },
                { "Header", "The latest {0} entries of the Realm Chronicle:" },
                { "Line", "  [A3A6AD]{0}[FFFFFF] {1}" },
                { "Empty", "Nothing has been written in the Realm Chronicle yet." },
                { "Usage", "Usage: [F4C96D]/chronicle[FFFFFF] [count]" }
            }, this);
        }

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            if (config.MaxEvents < 1) config.MaxEvents = 1;
            if (config.StateRefreshSeconds < 5f) config.StateRefreshSeconds = 5f;

            if (config.ChatMaxCount < 1) config.ChatMaxCount = 1;
            if (config.ChatDefaultCount < 1) config.ChatDefaultCount = 1;
            if (config.NextEventHorizonDays < 1) config.NextEventHorizonDays = 1;
            ClampFloodBudget();
            Config.WriteObject(config, true);             // writes newly added keys and clamped values

            // A damaged file (a power cut can leave it empty or "null") must not be overwritten: that would wipe the
            // realm's history. The plugin keeps logging in memory and writes nothing until the file is fixed or moved
            // away; once it is gone, the next save writes the new lines (see SaveEvents).
            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(EventsFile);
            try
            {
                events = Interface.Oxide.DataFileSystem.ReadObject<List<ChronicleEvent>>(EventsFile);
                if (events == null && existed) throw new Exception("the file is empty or null");
            }
            catch (Exception ex)
            {
                eventsLoadFailed = true;
                PrintError("Could not read oxide/data/" + EventsFile + ".json (" + ex.Message + "). It will NOT be overwritten: "
                    + "fix it, or move it away to start a new chronicle, then reload. New lines are kept in memory until then.");
                events = null;
            }
            if (events == null) events = new List<ChronicleEvent>();
            events.RemoveAll(IsNullEvent);
            foreach (ChronicleEvent existing in events)
                if (existing.id >= nextId) nextId = existing.id + 1;

            // The previous snapshot is read back only to keep "since" across reloads.
            try
            {
                state = Interface.Oxide.DataFileSystem.ReadObject<RealmStateData>(StateFile);
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read oxide/data/" + StateFile + ".json (" + ex.Message + "); rebuilding it.");
                state = null;
            }
            if (state == null) state = new RealmStateData();
            if (state.houses == null) state.houses = new List<HouseEntry>();
        }

        private void OnServerInitialized()
        {
            // Re-sent on hot reload (doc section 7), so keep this idempotent.
            if (refreshTimer != null && !refreshTimer.Destroyed) refreshTimer.Destroy();
            refreshTimer = timer.Every(config.StateRefreshSeconds, RefreshAndFlush);
            RefreshState();
        }

        private void OnServerSave()
        {
            SaveEvents();
        }

        private void Unload()
        {
            if (events != null) FlushSummaries(Now(), true);
            SaveEvents();
            if (state != null) WriteState();
        }

        private void ClampFloodBudget()
        {
            if (config.FloodBudget == null) config.FloodBudget = new FloodBudgetSettings();
            FloodBudgetSettings fb = config.FloodBudget;
            if (fb.WindowMinutes < 1) fb.WindowMinutes = 1;
            if (fb.WindowMinutes > 1440) fb.WindowMinutes = 1440;
            if (fb.GlobalPerWindow < 0) fb.GlobalPerWindow = 0;
            if (fb.PerTypePerWindow < 0) fb.PerTypePerWindow = 0;
            if (fb.SummaryQuietSeconds < 0) fb.SummaryQuietSeconds = 0;
            if (fb.PerType == null) fb.PerType = DefaultPerType();
            if (fb.NeverFold == null) fb.NeverFold = DefaultNeverFold();
            var perType = new Dictionary<string, int>();
            foreach (KeyValuePair<string, int> kv in fb.PerType)
                if (!string.IsNullOrEmpty(kv.Key)) perType[kv.Key.Trim().ToLowerInvariant()] = Math.Max(0, kv.Value);
            fb.PerType = perType;
            var never = new List<string>();
            foreach (string t in fb.NeverFold)
            {
                string k = (t ?? "").Trim().ToLowerInvariant();
                if (k.Length > 0 && !never.Contains(k)) never.Add(k);
            }
            fb.NeverFold = never;
        }

        #endregion

        #region Public API (plugin.Call)

        // Contract: RealmChronicle.Call("Log", type, title, detail, actors). Returns the new event id; 0 if rejected (unknown
        // type or empty title: the caller may fall back to another type); -1 if taken but not written as its own line (a
        // recent duplicate, or folded by the flood budget into a later summary line: the caller must not retry).
        // Private on purpose: only non-public methods are reachable through plugin.Call (see header).
        private int Log(string type, string title, string detail, string[] actors)
        {
            type = (type ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(KnownTypes, type) < 0)
            {
                PrintWarning("Rejected chronicle event with unknown type '" + type + "'");
                return 0;
            }

            title = Clean(title, TitleMax);
            if (title.Length == 0)
            {
                PrintWarning("Rejected chronicle event of type '" + type + "' with an empty title");
                return 0;
            }
            string cleanDetail = Clean(detail, DetailMax);
            if (IsRecentDuplicate(type, title, cleanDetail)) return -1;

            DateTime now = Now();
            FlushSummaries(now, false);
            if (OverBudget(type, now))
            {
                Fold(type, title, now);
                return -1;
            }

            ChronicleEvent ev = Append(type, title, cleanDetail, CleanActors(actors));
            if (type == "coronation" || type == "abdication") QueueRefresh();
            return ev.id;
        }

        private ChronicleEvent Append(string type, string title, string cleanDetail, string[] cleanActors)
        {
            var ev = new ChronicleEvent
            {
                id = nextId++,
                ts = IsoNow(),
                type = type,
                title = title,
                detail = cleanDetail,
                actors = cleanActors
            };

            events.Add(ev);
            if (events.Count > config.MaxEvents) events.RemoveRange(0, events.Count - config.MaxEvents);
            SaveEvents();

            if (config.EchoToConsole) Puts("#" + ev.id + " [" + ev.type + "] " + ev.title);
            return ev;
        }

        // Optional fast path used by CrownAndConsequences after coronation/abdication.
        private void SetCrown(string king, string house, string since)
        {
            state.king = NullIfEmpty(Clean(king, ActorMax));
            state.house = NullIfEmpty(Clean(house, ActorMax));
            state.since = state.king != null ? (NullIfEmpty(since) ?? state.since ?? IsoNow()) : null;
            WriteState();
        }

        private int GetLastEventId()
        {
            return nextId - 1;
        }

        #endregion

        #region Flood budget

        private bool NeverFolded(string type)
        {
            return Array.IndexOf(AlwaysWritten, type) >= 0 || config.FloodBudget.NeverFold.Contains(type);
        }

        private int TypeBudget(string type)
        {
            int n;
            return config.FloodBudget.PerType.TryGetValue(type, out n) ? n : config.FloodBudget.PerTypePerWindow;
        }

        // True when this line must be folded; otherwise it is counted against its type's budget and the global one.
        private bool OverBudget(string type, DateTime now)
        {
            FloodBudgetSettings fb = config.FloodBudget;
            if (!fb.Enabled || NeverFolded(type)) return false;
            DateTime cutoff = now.AddMinutes(-fb.WindowMinutes);
            Queue<DateTime> q;
            if (!typeTimes.TryGetValue(type, out q)) { q = new Queue<DateTime>(); typeTimes[type] = q; }
            Prune(q, cutoff);
            Prune(globalTimes, cutoff);
            if (q.Count >= TypeBudget(type) || globalTimes.Count >= fb.GlobalPerWindow) return true;
            q.Enqueue(now);
            globalTimes.Enqueue(now);
            return false;
        }

        private static void Prune(Queue<DateTime> q, DateTime cutoff)
        {
            while (q.Count > 0 && q.Peek() <= cutoff) q.Dequeue();
        }

        private void Fold(string type, string title, DateTime now)
        {
            FoldedBurst f;
            if (!folded.TryGetValue(type, out f))
            {
                f = new FoldedBurst { First = now };
                folded[type] = f;
                Puts("Flood budget: folding '" + type + "' lines into one summary (" + TypeBudget(type) + " of this type and "
                    + config.FloodBudget.GlobalPerWindow + " in all per " + config.FloodBudget.WindowMinutes + " min).");
            }
            f.Count++;
            f.Last = now;
            f.LastTitle = title;
        }

        // Writes one summary line per folded type once its burst is quiet (or a window old), at most once per type per
        // window. force (unload) writes every pending summary now.
        private void FlushSummaries(DateTime now, bool force)
        {
            if (folded.Count == 0) return;
            FloodBudgetSettings fb = config.FloodBudget;
            TimeSpan window = TimeSpan.FromMinutes(fb.WindowMinutes);
            foreach (string type in new List<string>(folded.Keys))
            {
                FoldedBurst f = folded[type];
                DateTime last;
                bool spaced = !lastSummary.TryGetValue(type, out last) || now - last >= window;
                bool settled = (now - f.Last).TotalSeconds >= fb.SummaryQuietSeconds || now - f.First >= window;
                if (!force && !(spaced && settled)) continue;
                folded.Remove(type);
                lastSummary[type] = now;
                string label = type.Replace('_', ' ');
                Append(type, Clean(f.Count + " more " + label + " entries", TitleMax),
                    Clean("Folded to keep the chronicle readable: " + f.Count + " " + label + " entries between "
                        + f.First.ToString("HH:mm") + " and " + f.Last.ToString("HH:mm") + " UTC. The latest: " + f.LastTitle, DetailMax),
                    new string[0]);
            }
        }

        #endregion

        #region Hooks

        private void OnPlayerConnected(Player player)
        {
            QueueRefresh();
        }

        private void OnPlayerDisconnected(Player player)
        {
            QueueRefresh();
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            QueueRefresh();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            QueueRefresh();
        }

        // Fallback crown tracking for when CrownAndConsequences is not loaded.
        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (evt == null || evt.Cancelled || evt.Player == null || evt.Player.IsServer) return;
            if (evt.State != AncientThroneCaptureEvent.States.Completed) return;   // doc 2.6 [UNVERIFIED] firing states
            if (CrownAndConsequences != null) { QueueRefresh(); return; }

            Guild house = evt.Player.GetGuild();
            string name = Clean(evt.Player.Name, ActorMax);
            string houseName = house != null ? Clean(house.Name, ActorMax) : null;
            state.since = IsoNow();
            if (config.LogThroneEventsWithoutCrownPlugin)
            {
                Log("coronation", name + " takes the throne",
                    houseName != null ? "House " + houseName + " now holds the crown." : "A sworn sword of no house now holds the crown.",
                    new[] { name });
            }
            QueueRefresh();
        }

        private void OnThroneReleased(AncientThroneReleaseEvent evt)
        {
            if (evt == null || evt.Sender == null || evt.Sender.IsServer) return;
            if (CrownAndConsequences != null) { QueueRefresh(); return; }

            string name = Clean(evt.Sender.Name, ActorMax);
            if (config.LogThroneEventsWithoutCrownPlugin)
            {
                Log("abdication", name + " no longer reigns",
                    evt.IsDeath ? name + " fell while holding the crown." : name + " has left the throne.",
                    new[] { name });
            }
            QueueRefresh();
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

        #region Commands

        [ChatCommand("chronicle")]
        private void CmdChronicle(Player player, string command, string[] args)
        {
            int count = config.ChatDefaultCount;
            if (args != null && args.Length > 0)
            {
                int parsed;
                if (!int.TryParse(args[0], out parsed) || parsed < 1)
                {
                    player.SendError(Styled(Msg("Speaker", player), ChatError, Msg("Usage", player)));
                    return;
                }
                count = Math.Min(parsed, config.ChatMaxCount);
            }

            if (events.Count == 0)
            {
                player.SendMessage(Styled(Msg("Speaker", player), ChatGold, Msg("Empty", player)));
                return;
            }

            count = Math.Min(count, events.Count);
            player.SendMessage(Styled(Msg("Speaker", player), ChatGold, string.Format(Msg("Header", player), count)));
            // Event text is player-influenced, so it goes through the single-string overload (doc 4.2).
            for (int i = events.Count - count; i < events.Count; i++)
            {
                ChronicleEvent e = events[i];
                string when = e.ts != null && e.ts.Length >= 16 ? e.ts.Substring(5, 11).Replace('T', ' ') : "";
                player.SendMessage(string.Format(Msg("Line", player), when, e.title));
            }
        }

        #endregion

        #region State refresh

        private void QueueRefresh()
        {
            // Coalesce bursts (joins, plugin reloads) and let the game finish removing a leaving player.
            if (refreshQueued) return;
            refreshQueued = true;
            timer.Once(2f, () => { refreshQueued = false; RefreshState(); });
        }

        private void RefreshAndFlush()
        {
            FlushSummaries(Now(), false);
            RefreshState();
        }

        private void RefreshState()
        {
            RefreshCrown();
            state.houses = CollectHouses();

            int online = 0;
            List<Player> players = Server.ClientPlayers;
            if (players != null)
                foreach (Player p in players)
                    if (p != null && !p.IsServer) online++;   // doc 3.4: filter the server pseudo-player

            state.online = online;
            state.maxPlayers = Server.PlayerLimit;
            state.next = FindNextEvent();
            WriteState();
        }

        // Soonest upcoming scheduled event from the loaded schedule plugins (see header); null when none qualifies.
        private NextEvent FindNextEvent()
        {
            DateTime now = Now();
            DateTime horizon = now.AddDays(config.NextEventHorizonDays);
            string bestTitle = null;
            DateTime bestAt = DateTime.MaxValue;

            if (CrownAndConsequences != null)
            {
                object at = CrownAndConsequences.Call("GetNextRebellionWindow");
                if (at is DateTime) ConsiderNext("Rebellion window", (DateTime)at, now, horizon, ref bestTitle, ref bestAt);
            }

            if (RealmEvents != null)
            {
                var ev = RealmEvents.Call("GetNextEvent") as Dictionary<string, object>;
                object at = Get(ev, "at");
                if (at is DateTime) ConsiderNext(Get(ev, "title") as string, (DateTime)at, now, horizon, ref bestTitle, ref bestAt);
            }

            if (bestTitle == null) return null;
            return new NextEvent { title = bestTitle, at = bestAt.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'") };
        }

        private static void ConsiderNext(string title, DateTime at, DateTime now, DateTime horizon, ref string bestTitle, ref DateTime bestAt)
        {
            title = NullIfEmpty(Clean(title, NextTitleMax));
            if (title == null) return;
            if (at.Kind == DateTimeKind.Local) at = at.ToUniversalTime();   // Unspecified is taken as UTC (contract)
            if (at <= now || at > horizon || at >= bestAt) return;
            bestTitle = title;
            bestAt = at;
        }

        private void RefreshCrown()
        {
            if (CrownAndConsequences != null)
            {
                string king = CrownAndConsequences.Call("GetKingName") as string;
                state.king = NullIfEmpty(Clean(king, ActorMax));
                state.house = state.king != null ? NullIfEmpty(Clean(CrownAndConsequences.Call("GetKingHouse") as string, ActorMax)) : null;
                string since = CrownAndConsequences.Call("GetKingSince") as string;
                state.since = state.king != null ? (NullIfEmpty(since) ?? state.since) : null;
                return;
            }

            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            if (crown == null || !crown.HasKing())
            {
                state.king = null;
                state.house = null;
                state.since = null;
                return;
            }

            string name = NullIfEmpty(Clean(crown.GetKingName(), ActorMax));
            if (name != state.king) state.since = IsoNow();   // first sighting of a new king
            state.king = name;

            GuildScheme guilds = SocialAPI.Get<GuildScheme>();
            Guild g = guilds != null ? guilds.TryGetGuildByMember(crown.GetKingID()) : null;
            state.house = g != null ? NullIfEmpty(Clean(g.Name, ActorMax)) : null;
            if (state.since == null) state.since = IsoNow();
        }

        private List<HouseEntry> CollectHouses()
        {
            var list = new List<HouseEntry>();

            if (RealmHouses != null)
            {
                var summaries = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
                if (summaries != null)
                {
                    foreach (Dictionary<string, object> s in summaries)
                    {
                        string name = NullIfEmpty(Clean(Get(s, "name") as string, ActorMax));
                        if (name == null) continue;
                        object members = Get(s, "members");
                        list.Add(new HouseEntry
                        {
                            name = name,
                            sigil = NullIfEmpty(Clean(Get(s, "sigil") as string, ActorMax)),
                            liege = NullIfEmpty(Clean(Get(s, "liege") as string, ActorMax)),
                            members = members is int ? (int)members : 0
                        });
                    }
                    return list;
                }
            }

            // Fallback: game guilds of online players only. Listing every guild needs GuildScheme.Storage,
            // whose return type is not verified (doc 3.6), so offline-only guilds are omitted here.
            var seen = new HashSet<ulong>();
            List<Player> players = Server.ClientPlayers;
            if (players == null) return list;
            foreach (Player p in players)
            {
                if (p == null || p.IsServer) continue;
                Guild g = p.GetGuild();
                if (g == null || !seen.Add(g.BaseID)) continue;
                string name = NullIfEmpty(Clean(g.Name, ActorMax));
                if (name == null) continue;
                Members m = g.Members();
                list.Add(new HouseEntry { name = name, sigil = null, liege = null, members = m != null ? m.MemberCount() : 0 });
            }
            return list;
        }

        #endregion

        #region Helpers

        private void SaveEvents()
        {
            if (events == null) return;
            if (eventsLoadFailed)
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(EventsFile)) return;   // still the damaged file
                eventsLoadFailed = false;                                                 // moved away: start afresh
                PrintWarning("oxide/data/" + EventsFile + ".json was moved away; a new chronicle is written from now on.");
            }
            Interface.Oxide.DataFileSystem.WriteObject(EventsFile, events);
        }

        private void WriteState()
        {
            state.updated = IsoNow();
            Interface.Oxide.DataFileSystem.WriteObject(StateFile, state);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player.Id.ToString());
        }

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private DateTime Now()
        {
            return clock();
        }

        // Explicit pattern so the output does not depend on the server's culture settings.
        private string IsoNow()
        {
            return Now().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'");
        }

        private static bool IsNullEvent(ChronicleEvent ev)
        {
            return ev == null;
        }

        // Anti-spam: the same event text inside DuplicateWindowSeconds is dropped (cycling found/disband, etc.).
        private bool IsRecentDuplicate(string type, string title, string detail)
        {
            if (config.DuplicateWindowSeconds <= 0) return false;
            // Crown changes and rebellion milestones are gated by the game and must never be dropped.
            if (type == "coronation" || type == "abdication" || type == "rebellion_started" || type == "rebellion_ended") return false;
            DateTime cutoff = Now().AddSeconds(-config.DuplicateWindowSeconds);
            string cutoffIso = cutoff.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'");
            for (int i = events.Count - 1; i >= 0; i--)
            {
                ChronicleEvent e = events[i];
                if (e.ts != null && string.CompareOrdinal(e.ts, cutoffIso) < 0) break;   // same fixed ISO format sorts by time
                if (e.type == type && e.title == title && e.detail == detail) return true;
            }
            return false;
        }

        private static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static string[] CleanActors(string[] actors)
        {
            var list = new List<string>();
            if (actors == null) return list.ToArray();
            foreach (string a in actors)
            {
                string c = Clean(a, ActorMax);
                if (c.Length > 0 && !list.Contains(c)) list.Add(c);
                if (list.Count >= ActorsMax) break;
            }
            return list.ToArray();
        }

        // Strips RoK colour tags like [FF0000] and control characters, collapses whitespace, caps length.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[' && i + 7 < s.Length && s[i + 7] == ']' && IsHex(s, i + 1, 6)) { i += 7; continue; }
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }
            string r = sb.ToString().Trim();
            return r.Length > max ? r.Substring(0, max - 1).TrimEnd() + "…" : r;
        }

        private static bool IsHex(string s, int start, int len)
        {
            for (int i = start; i < start + len; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        #endregion
    }
}
