// RealmChronicle: the public record of the realm.
//
// Writes two data files that the chronicle web service (chronicle/server.js) reads:
//   oxide/data/RealmChronicle.json  JSON array of events {id, ts, type, title, detail, actors[]}
//   oxide/data/RealmState.json      {king, house, since, houses[], online, maxPlayers, updated}
//
// Other plugins log events with RealmChronicle.Call("Log", type, title, detail, actors).
// Only public names go into the chronicle: never positions, inventories or Steam ids.
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

        private static readonly string[] KnownTypes =
        {
            "coronation", "abdication", "claim_declared", "rebellion_started", "rebellion_ended",
            "house_founded", "oath_sworn", "oath_broken", "treaty_signed", "treaty_broken",
            "decree", "ransom_set", "ransom_paid", "released"
        };

        // [SRC] Oxide.CSharp src/CSharpPlugin.cs:91 (PluginReferenceAttribute; the field is filled with the plugin
        // whose name matches the field name) and Oxide.Core src/Plugins/Plugin.cs:311 (Plugin.Call).
        // Both references are optional; null means "not loaded".
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;

        private PluginConfig config;
        private List<ChronicleEvent> events;
        private RealmStateData state;
        private int nextId = 1;
        private Timer refreshTimer;
        private bool refreshQueued;

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
                { "Header", "[C8A050]The Realm Chronicle[FFFFFF] (latest {0}):" },
                { "Empty", "[C8A050]The Realm Chronicle[FFFFFF]: nothing has been written yet." },
                { "Usage", "Usage: /chronicle [count]" }
            }, this);
        }

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            if (config.MaxEvents < 1) config.MaxEvents = 1;
            if (config.StateRefreshSeconds < 5f) config.StateRefreshSeconds = 5f;

            if (config.ChatMaxCount < 1) config.ChatMaxCount = 1;
            if (config.ChatDefaultCount < 1) config.ChatDefaultCount = 1;

            try
            {
                events = Interface.Oxide.DataFileSystem.ReadObject<List<ChronicleEvent>>(EventsFile);
            }
            catch (Exception ex)
            {
                // A damaged file must not stop the plugin; ids continue from 1 and the old file is overwritten
                // on the next event, so back it up by hand if the history matters.
                PrintError("Could not read oxide/data/" + EventsFile + ".json (" + ex.Message + "); starting a new chronicle.");
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
            refreshTimer = timer.Every(config.StateRefreshSeconds, RefreshState);
            RefreshState();
        }

        private void OnServerSave()
        {
            SaveEvents();
        }

        private void Unload()
        {
            SaveEvents();
            if (state != null) WriteState();
        }

        #endregion

        #region Public API (plugin.Call)

        // Contract: RealmChronicle.Call("Log", type, title, detail, actors). Returns the new event id, or 0 if rejected.
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
            if (IsRecentDuplicate(type, title, cleanDetail)) return 0;

            var ev = new ChronicleEvent
            {
                id = nextId++,
                ts = IsoNow(),
                type = type,
                title = title,
                detail = cleanDetail,
                actors = CleanActors(actors)
            };

            events.Add(ev);
            if (events.Count > config.MaxEvents) events.RemoveRange(0, events.Count - config.MaxEvents);
            SaveEvents();

            if (config.EchoToConsole) Puts("#" + ev.id + " [" + ev.type + "] " + ev.title);
            if (type == "coronation" || type == "abdication") QueueRefresh();
            return ev.id;
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
                    player.SendMessage(Msg("Usage", player));
                    return;
                }
                count = Math.Min(parsed, config.ChatMaxCount);
            }

            if (events.Count == 0)
            {
                player.SendMessage(Msg("Empty", player));
                return;
            }

            count = Math.Min(count, events.Count);
            player.SendMessage(string.Format(Msg("Header", player), count));
            // Event text is player-influenced, so it goes through the single-string overload (doc 4.2).
            for (int i = events.Count - count; i < events.Count; i++)
            {
                ChronicleEvent e = events[i];
                string when = e.ts != null && e.ts.Length >= 16 ? e.ts.Substring(5, 11).Replace('T', ' ') : "";
                player.SendMessage("[A08C64]" + when + "[FFFFFF] " + e.title);
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
            WriteState();
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

        // Explicit pattern so the output does not depend on the server's culture settings.
        private static string IsoNow()
        {
            return DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'");
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
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-config.DuplicateWindowSeconds);
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
