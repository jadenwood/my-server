// RealmSeasons: numbered seasons, house standings, a season-end ceremony and the Hall of Kings.
//
//   season      a numbered stretch of play with a start and an end date (UTC). An admin starts and ends it
//               (/season start|end); with AutoEndSeason it also ends by itself when its end date passes.
//   standings   every house earns season points for:
//                 crown days    time its member sat the throne, measured by polling the crown every tick
//                 rebellions    won (a claimant that took the crown) or defended (the crown held)
//                 treaties      kept to term (+) or broken (-)
//                 oaths         broken (-)
//                 contracts     fulfilled by its members
//                 realm events  points awarded by RealmEvents (Royal Tournament, King's Hunt, Crown Night, Truce)
//   ceremony    at season end the standings are proclaimed, the champion house is named, and the season goes
//               into the chronicle and the legends file
//   Hall of Kings  every reign: monarch, house, start, end, length and how it ended. Kept across seasons and wipes.
//
// Two data files, on purpose:
//   oxide/data/RealmSeasons.json  the running season (standings, chronicle cursor, tracked treaties). Safe to delete
//                                 on a wipe; the season then starts fresh.
//   oxide/data/RealmLegends.json  the Hall of Kings and every past season. KEEP THIS FILE ACROSS WIPES: it is the
//                                 realm's memory. If it exists but cannot be parsed, the plugin refuses to run and
//                                 never writes it, so a damaged legend is not overwritten.
//
// Where the numbers come from (all optional; a missing plugin only disables the part that needs it):
//   CrownAndConsequences  GetKingName(), GetKingHouse(), GetOpenClaims() -> "house|status|startIso|endIso"
//                         (non-public API, plugins/CrownAndConsequences.cs). Without it: KingsScheme via
//                         SocialAPI.Get<KingsScheme>() HasKing/GetKingName/GetKingID [ASM][USE RaidBoss.cs:496] and
//                         the house from RealmHouses.GetHouse or GuildScheme.TryGetGuildByMember [ASM].
//   RealmChronicle        GetLastEventId() -> int, then oxide/data/RealmChronicle.json is READ (never written) with
//                         Interface.Oxide.DataFileSystem.ReadObject [SRC doc 4.6]. New events since the cursor feed the
//                         standings. The texts parsed are the ones this repo's plugins write (see ParseChronicleEvent):
//                           rebellion_ended   "House X prevailed ..." / "The crown held. House X failed."
//                                             (CrownAndConsequences.TickClaims)
//                           treaty_signed     "House A and House B sign a treaty"          (RealmHouses.TreatyAccept)
//                           treaty_broken     "House A breaks its treaty with House B"     (RealmHouses.TreatyBreak)
//                           oath_broken       "House A renounces its oath to House B"      (RealmHouses)
//                           contract_fulfilled "<name> collects the price on|fills an order for|is paid by House"
//                                             (RealmContracts.FulfilledTitle)
//                         If one of those texts changes, the matching standing silently stops counting; the
//                         behaviour tests (plugins/docs/RealmEvents/logic-tests) check the texts are still in those plugins.
//   RealmHouses           HasTreaty(a, b) -> bool (a treaty no longer present that was never broken counts as kept),
//                         GetHouseSummaries(), GetMemberNames(house) (to map a contract fulfiller's name to a house),
//                         GetHouse(playerId).
// Hooks: OnThroneReleased(AncientThroneReleaseEvent) [OPJ L633] only records HOW a reign ended (evt.IsDeath);
// the reign itself is tracked by polling, so a missed hook only loses the wording.
// UNVERIFIED (in-game): a treaty dissolved by a house disbanding also counts as "kept" (RealmHouses has no event for it).
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Thrones.AncientThrone;           // AncientThroneReleaseEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // Guild, GuildScheme, KingsScheme [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmSeasons", "Realm", "0.1.0")]
    [Description("Numbered seasons with house standings, a season-end ceremony and the Hall of Kings that outlives wipes")]
    public class RealmSeasons : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmseasons.admin";
        private const string SeasonFile = "RealmSeasons";
        private const string LegendsFile = "RealmLegends";
        private const string ChronicleFile = "RealmChronicle";
        private const int NameMax = 48;

        private PluginConfig config;
        private SeasonData season;
        private Legends legends;
        private bool loadFailed;
        private bool initialized;
        private Timer tickTimer;
        private DateTime lastTreatyCheck = DateTime.MinValue;

        // Set by OnThroneReleased; read by the next crown poll to word how the reign ended.
        private string pendingEnding;
        private DateTime pendingEndingAt = DateTime.MinValue;
        private readonly Dictionary<string, bool> chronicleTypeAccepted = new Dictionary<string, bool>();
        private bool firstCrownSync = true;

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        #region Config

        private class ScoreWeights
        {
            public double CrownDay = 10;               // per 24 h a house holds the crown (pro rata)
            public int RebellionWon = 25;
            public int RebellionDefended = 15;
            public int TreatyKept = 5;
            public int TreatyBroken = -10;
            public int OathBroken = -10;
            public int ContractFulfilled = 2;
            public double EventPointsFactor = 1.0;     // multiplier on points awarded by RealmEvents
        }

        private class PluginConfig
        {
            public int DefaultSeasonDays = 28;
            public bool AutoStartFirstSeason = true;   // start season 1 on first load if none was ever run
            public bool AutoEndSeason = true;          // end the season when its end date passes
            public bool AutoStartNextSeason = false;   // start the next season right after the ceremony
            public string SeasonNameFormat = "Season {0}";
            public float TickSeconds = 30f;
            public int MaxCreditSecondsPerTick = 300;  // crown time across a long stall/downtime is not credited
            public bool ReadChronicle = true;
            public int TreatyCheckSeconds = 300;
            public int StandingsShown = 5;
            public int CeremonyTopHouses = 3;
            public int HallPageSize = 6;
            public int MaxHallEntries = 1000;
            public int MaxEventAwardPerCall = 100;     // cap on a single AwardHouse call from another plugin
            public ScoreWeights Weights = new ScoreWeights();
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void ClampConfig()
        {
            if (config.Weights == null) config.Weights = new ScoreWeights();
            if (config.DefaultSeasonDays < 1) config.DefaultSeasonDays = 1;
            if (config.DefaultSeasonDays > 365) config.DefaultSeasonDays = 365;
            if (string.IsNullOrEmpty(config.SeasonNameFormat) || config.SeasonNameFormat.IndexOf("{0}") < 0) config.SeasonNameFormat = "Season {0}";
            if (config.TickSeconds < 5f) config.TickSeconds = 5f;
            if (config.MaxCreditSecondsPerTick < (int)config.TickSeconds) config.MaxCreditSecondsPerTick = (int)config.TickSeconds * 2;
            if (config.TreatyCheckSeconds < 30) config.TreatyCheckSeconds = 30;
            if (config.StandingsShown < 1) config.StandingsShown = 1;
            if (config.CeremonyTopHouses < 1) config.CeremonyTopHouses = 1;
            if (config.HallPageSize < 1) config.HallPageSize = 1;
            if (config.MaxHallEntries < 10) config.MaxHallEntries = 10;
            if (config.MaxEventAwardPerCall < 1) config.MaxEventAwardPerCall = 1;
        }

        #endregion

        #region Data

        private class HouseStanding
        {
            public string House;
            public double CrownSeconds;
            public int RebellionsWon;
            public int RebellionsDefended;
            public int TreatiesKept;
            public int TreatiesBroken;
            public int OathsBroken;
            public int ContractsFulfilled;
            public int EventPoints;
            public List<string> Honours = new List<string>();   // e.g. "Royal Tournament champion: Aldric"
        }

        private class TrackedTreaty
        {
            public string A;
            public string B;
            public DateTime Signed;
        }

        private class SeasonData
        {
            public int Number;                         // 0 = no season running
            public string Name;
            public bool Active;
            public DateTime StartedAt;
            public DateTime EndsAt;
            public int ChronicleCursor;                // last chronicle event id already counted
            public DateTime LastCrownTick;
            public Dictionary<string, HouseStanding> Houses = new Dictionary<string, HouseStanding>();
            public List<TrackedTreaty> Treaties = new List<TrackedTreaty>();
        }

        private class Reign
        {
            public int Season;                         // season number when the reign began (0 = between seasons)
            public string Monarch;
            public string House;
            public DateTime Start;
            public DateTime? End;
            public string Ending;                      // how it ended, in words
        }

        private class StandingLine
        {
            public int Rank;
            public string House;
            public int Score;
            public double CrownDays;
        }

        private class SeasonRecord
        {
            public int Number;
            public string Name;
            public DateTime Start;
            public DateTime End;
            public string Champion;
            public int ChampionScore;
            public string LongestReign;                // "Monarch of House (N days)"
            public List<StandingLine> Top = new List<StandingLine>();
        }

        private class Legends
        {
            public int LastSeasonNumber;
            public Reign Current;                      // the open reign, if any
            public List<Reign> Hall = new List<Reign>();
            public List<SeasonRecord> Seasons = new List<SeasonRecord>();
        }

        // Read-only view of oxide/data/RealmChronicle.json (field names are that file's JSON contract).
        private class ChronEntry
        {
            public int id;
            public string ts;
            public string type;
            public string title;
            public string detail;
            public string[] actors;
        }

        private void SaveSeason()
        {
            if (loadFailed || season == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(SeasonFile, season);
        }

        private void SaveLegends()
        {
            if (loadFailed || legends == null) return;
            Interface.Oxide.DataFileSystem.WriteObject(LegendsFile, legends);
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Prefix", "[C8A050]Seasons[FFFFFF]: " },
                { "Help", "/season | /season standings | /season house <name> | /season hall [page] | /season history. Admin: /season start [days] [name] | end | status" },
                { "NoSeason", "No season is running. The Hall of Kings still remembers: /season hall" },
                { "Status", "{0} - day {1} of {2}, ends {3} UTC." },
                { "StatusLeader", "Leading: House {0} with {1} points." },
                { "StatusYours", "Your house, {0}, stands #{1} with {2} points." },
                { "StandingsHeader", "{0} standings:" },
                { "StandingsLine", "  #{0} House {1}: {2} pts ({3} crown days, {4} rebellions won, {5} defended, treaties {6} kept / {7} broken, {8} contracts, {9} event pts)" },
                { "StandingsNone", "No house has earned anything yet this season." },
                { "HouseNotFound", "House {0} has no standing this season." },
                { "HouseHonours", "  Honours: {0}" },
                { "HallHeader", "The Hall of Kings ({0} reigns), page {1} of {2}:" },
                { "HallLine", "  {0} of House {1}: {2} -> {3} ({4}), {5}" },
                { "HallNone", "No monarch has reigned yet." },
                { "HistoryHeader", "Past seasons:" },
                { "HistoryLine", "  {0}: champion House {1} ({2} pts). Longest reign: {3}" },
                { "HistoryNone", "No season has ended yet." },
                { "NoPermission", "You may not do that." },
                { "AlreadyRunning", "{0} is already running. End it first with /season end." },
                { "NotRunning", "No season is running." },
                { "BadDays", "Days must be a whole number from 1 to 365." },
                { "Started", "{0} has begun. It ends {1} UTC." },
                { "AdminStatus", "Season #{0} active={1} cursor={2} houses={3} treaties tracked={4}; sources: crown={5} chronicle={6} houses={7}" },
                { "LoadFailed", "The legends file could not be read. Seasons are paused until an admin fixes oxide/data/RealmLegends.json." },
                { "BroadcastStart", "[C8A050]Herald[FFFFFF]: {0} begins! Houses, win the crown, keep your treaties and fill your contracts. It ends {1} UTC. See /season." },
                { "BroadcastEnding", "[C8A050]Herald[FFFFFF]: {0} has ended. Hear the standings of the realm:" },
                { "BroadcastPlace", "[C8A050]Herald[FFFFFF]:   #{0} House {1} - {2} points" },
                { "BroadcastChampion", "[C8A050]Herald[FFFFFF]: House {0} is champion of {1}! Their name goes into the legends." },
                { "BroadcastNoChampion", "[C8A050]Herald[FFFFFF]: No house earned glory this season. The legends record an empty page." },
                { "BroadcastLongest", "[C8A050]Herald[FFFFFF]: Longest reign of the season: {0}." }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        private void Reply(Player player, string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
            player.SendMessage(Msg("Prefix", player) + text);              // single-string overload: brace safe
        }

        private void ReplyRaw(Player player, string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
            player.SendMessage(text);
        }

        private void Herald(string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, null), args) : Msg(key, null);
            Server.BroadcastMessage(text);                                 // single-string overload [ASM]
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);

            // Legends first: they must never be overwritten by a failed parse.
            try
            {
                legends = Interface.Oxide.DataFileSystem.ReadObject<Legends>(LegendsFile);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                PrintError("Could not read oxide/data/" + LegendsFile + ".json: " + ex.Message
                    + ". RealmSeasons will not run or write anything until the file is fixed or moved away, then reload.");
                return;
            }
            if (legends == null) legends = new Legends();
            if (legends.Hall == null) legends.Hall = new List<Reign>();
            if (legends.Seasons == null) legends.Seasons = new List<SeasonRecord>();
            legends.Hall.RemoveAll(IsBrokenReign);
            legends.Seasons.RemoveAll(IsBrokenRecord);

            try
            {
                season = Interface.Oxide.DataFileSystem.ReadObject<SeasonData>(SeasonFile);
            }
            catch (Exception ex)
            {
                // The running season is replaceable (it is wiped with the world anyway); the legends are not.
                PrintWarning("Could not read oxide/data/" + SeasonFile + ".json (" + ex.Message + "); the running season is reset.");
                season = null;
            }
            if (season == null) season = new SeasonData();
            if (season.Houses == null) season.Houses = new Dictionary<string, HouseStanding>();
            if (season.Treaties == null) season.Treaties = new List<TrackedTreaty>();
            foreach (HouseStanding h in season.Houses.Values) if (h != null && h.Honours == null) h.Honours = new List<string>();
            if (season.Number > legends.LastSeasonNumber) legends.LastSeasonNumber = season.Number;
        }

        private void OnServerInitialized()
        {
            if (loadFailed) return;
            // Re-sent on hot reload (doc 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            if (!initialized)
            {
                initialized = true;
                firstCrownSync = true;
                if (config.AutoStartFirstSeason && legends.LastSeasonNumber == 0 && !season.Active)
                    StartSeason(config.DefaultSeasonDays, null, "the realm's first season begins on its own");
            }
            SafeTick();
        }

        private void OnServerSave()
        {
            SaveSeason();
        }

        private void Unload()
        {
            SaveSeason();
            SaveLegends();
        }

        private static bool IsBrokenReign(Reign r)
        {
            return r == null || string.IsNullOrEmpty(r.Monarch);
        }

        private static bool IsBrokenRecord(SeasonRecord r)
        {
            return r == null || r.Number <= 0;
        }

        #endregion

        #region Tick

        private void SafeTick()
        {
            if (loadFailed) return;
            try { Tick(); }
            catch (Exception ex) { PrintError("Season tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = Now();
            SyncCrown(now);
            if (season.Active)
            {
                CreditCrownTime(now);
                if (config.ReadChronicle) ReadNewChronicle();
                if ((now - lastTreatyCheck).TotalSeconds >= config.TreatyCheckSeconds)
                {
                    lastTreatyCheck = now;
                    CheckTreatiesKept();
                }
                if (config.AutoEndSeason && now >= season.EndsAt)
                {
                    EndSeason("the season's appointed end");
                    if (config.AutoStartNextSeason) StartSeason(config.DefaultSeasonDays, null, "the next season follows at once");
                    return;
                }
            }
            else if (config.ReadChronicle)
            {
                // Between seasons, keep the cursor current so a new season never counts old history.
                int last = ChronicleLastId();
                if (last >= 0) season.ChronicleCursor = last;
            }
            SaveSeason();
        }

        #endregion

        #region Crown and the Hall of Kings

        // RB 0 [OPJ L633]: evt.Sender is the old king, IsDeath tells a fall from a departure [USE RaidBoss.cs:626].
        private void OnThroneReleased(AncientThroneReleaseEvent evt)
        {
            if (loadFailed || evt == null) return;
            pendingEnding = evt.IsDeath ? "fell while holding the crown" : "left the throne";
            pendingEndingAt = Now();
            timer.Once(2f, SafeTick);   // let CrownAndConsequences record the change first
        }

        private void CurrentCrown(out string monarch, out string house)
        {
            monarch = null;
            house = null;
            if (CrownAndConsequences != null)
            {
                monarch = CleanName(CrownAndConsequences.Call("GetKingName") as string);
                house = monarch != null ? CleanName(CrownAndConsequences.Call("GetKingHouse") as string) : null;
                return;
            }
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            if (crown == null || !crown.HasKing()) return;
            monarch = CleanName(crown.GetKingName());
            house = monarch != null ? HouseOfPlayer(crown.GetKingID()) : null;
        }

        private void SyncCrown(DateTime now)
        {
            string monarch, house;
            CurrentCrown(out monarch, out house);
            Reign cur = legends.Current;
            bool first = firstCrownSync;
            firstCrownSync = false;

            if (cur != null && monarch != null && SameName(cur.Monarch, monarch))
            {
                // Same monarch. The house may become known later (house founded after the crowning).
                if (cur.House == null && house != null) { cur.House = house; SaveLegends(); }
                return;
            }
            if (cur == null && monarch == null) return;

            if (cur != null)
            {
                string ending;
                bool recent = pendingEnding != null && (now - pendingEndingAt).TotalMinutes <= 10;
                if (monarch != null)
                {
                    string rebel = ActiveClaimantHouse(house);
                    ending = rebel != null
                        ? "overthrown by the rebellion of House " + rebel + " (" + monarch + ")"
                        : (recent ? pendingEnding + "; " + monarch + " took the throne" : "the crown passed to " + monarch);
                }
                else if (recent) ending = pendingEnding;
                else if (first) ending = "no longer seated when the realm awoke";
                else ending = "the throne fell empty";
                CloseReign(cur, now, ending);
            }
            pendingEnding = null;

            if (monarch != null)
            {
                legends.Current = new Reign
                {
                    Season = season.Active ? season.Number : 0,
                    Monarch = monarch,
                    House = house,
                    Start = now
                };
            }
            season.LastCrownTick = now;
            SaveLegends();
        }

        private void CloseReign(Reign r, DateTime now, string ending)
        {
            r.End = now;
            r.Ending = ending;
            legends.Hall.Add(r);
            legends.Current = null;
            if (legends.Hall.Count > config.MaxHallEntries) legends.Hall.RemoveRange(0, legends.Hall.Count - config.MaxHallEntries);
        }

        private void CreditCrownTime(DateTime now)
        {
            Reign cur = legends.Current;
            DateTime last = season.LastCrownTick;
            season.LastCrownTick = now;
            if (cur == null || cur.House == null) return;
            if (last < season.StartedAt) last = season.StartedAt;
            if (last < cur.Start) last = cur.Start;
            double secs = (now - last).TotalSeconds;
            if (secs <= 0) return;
            if (secs > config.MaxCreditSecondsPerTick) secs = config.MaxCreditSecondsPerTick;
            Standing(cur.House).CrownSeconds += secs;
        }

        // The house of an ACTIVE claim that now holds the crown, else null.
        private string ActiveClaimantHouse(string newHouse)
        {
            if (newHouse == null || CrownAndConsequences == null) return null;
            string[] claims = CrownAndConsequences.Call("GetOpenClaims") as string[];
            if (claims == null) return null;
            foreach (string c in claims)
            {
                string[] parts = (c ?? "").Split('|');
                if (parts.Length >= 2 && parts[1] == "active" && SameName(parts[0], newHouse)) return parts[0];
            }
            return null;
        }

        #endregion

        #region Chronicle feed

        private int ChronicleLastId()
        {
            if (RealmChronicle == null) return -1;
            object r = RealmChronicle.Call("GetLastEventId");
            return r is int ? (int)r : -1;
        }

        private void ReadNewChronicle()
        {
            int last = ChronicleLastId();
            if (last < 0) return;
            if (last < season.ChronicleCursor)
            {
                // The chronicle was reset (wipe or a fresh file): its ids start again from 1.
                PrintWarning("The chronicle restarted (last id " + last + " < cursor " + season.ChronicleCursor + "); following it from the start.");
                season.ChronicleCursor = 0;
            }
            if (last == season.ChronicleCursor) return;
            if (!Interface.Oxide.DataFileSystem.ExistsDatafile(ChronicleFile)) return;

            List<ChronEntry> list;
            try { list = Interface.Oxide.DataFileSystem.ReadObject<List<ChronEntry>>(ChronicleFile); }
            catch (Exception ex)
            {
                PrintWarning("Could not read the chronicle (" + ex.Message + "); trying again next tick.");
                return;
            }
            if (list == null) return;
            string startIso = Iso(season.StartedAt);
            int max = season.ChronicleCursor;
            Dictionary<string, string> names = null;
            foreach (ChronEntry e in list)
            {
                if (e == null || e.id <= season.ChronicleCursor) continue;
                if (e.id > max) max = e.id;
                if (e.ts != null && string.CompareOrdinal(e.ts, startIso) < 0) continue;   // before the season
                if (e.type == "contract_fulfilled" && names == null) names = MemberHouseMap();
                ParseChronicleEvent(e.type, e.title ?? "", e.detail ?? "", names);
            }
            season.ChronicleCursor = Math.Max(max, last);
        }

        // Pure text rules, kept in one place so the coupling to other plugins' wording is visible.
        private void ParseChronicleEvent(string type, string title, string detail, Dictionary<string, string> memberHouse)
        {
            string a, b;
            switch (type)
            {
                case "rebellion_ended":
                    if (Between(detail, "House ", " prevailed", out a)) Standing(a).RebellionsWon++;
                    else if (detail.StartsWith("The crown held.", StringComparison.Ordinal))
                    {
                        string crownHouse = legends.Current != null ? legends.Current.House : null;
                        if (crownHouse != null) Standing(crownHouse).RebellionsDefended++;
                    }
                    break;
                case "treaty_signed":
                    if (TwoHouses(title, "House ", " and House ", " sign a treaty", out a, out b))
                    {
                        RemoveTracked(a, b);
                        season.Treaties.Add(new TrackedTreaty { A = a, B = b, Signed = Now() });
                    }
                    break;
                case "treaty_broken":
                    if (TwoHouses(title, "House ", " breaks its treaty with House ", null, out a, out b))
                    {
                        Standing(a).TreatiesBroken++;
                        RemoveTracked(a, b);
                    }
                    break;
                case "oath_broken":
                    if (Between(title, "House ", " renounces its oath", out a)) Standing(a).OathsBroken++;
                    break;
                case "contract_fulfilled":
                    string who = PrefixBefore(title, new[] { " collects the price on ", " fills an order for ", " is paid by House " });
                    string house;
                    if (who != null && memberHouse != null && memberHouse.TryGetValue(who.ToLowerInvariant(), out house))
                        Standing(house).ContractsFulfilled++;
                    break;
            }
        }

        private void CheckTreatiesKept()
        {
            if (RealmHouses == null || season.Treaties.Count == 0) return;
            foreach (TrackedTreaty t in season.Treaties.ToArray())
            {
                object r = RealmHouses.Call("HasTreaty", t.A, t.B);
                if (!(r is bool) || (bool)r) continue;
                // Gone, and no treaty_broken was seen for it: it ran its term.
                Standing(t.A).TreatiesKept++;
                Standing(t.B).TreatiesKept++;
                season.Treaties.Remove(t);
            }
        }

        private void RemoveTracked(string a, string b)
        {
            season.Treaties.RemoveAll(delegate(TrackedTreaty t)
            {
                return (SameName(t.A, a) && SameName(t.B, b)) || (SameName(t.A, b) && SameName(t.B, a));
            });
        }

        // lower-case member name -> house, from RealmHouses (names only, as written in the chronicle).
        private Dictionary<string, string> MemberHouseMap()
        {
            var map = new Dictionary<string, string>();
            if (RealmHouses == null) return map;
            var summaries = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
            if (summaries == null) return map;
            foreach (Dictionary<string, object> s in summaries)
            {
                object n;
                string house = s != null && s.TryGetValue("name", out n) ? n as string : null;
                if (string.IsNullOrEmpty(house)) continue;
                var names = RealmHouses.Call("GetMemberNames", house) as List<string>;
                if (names == null) continue;
                foreach (string m in names) if (!string.IsNullOrEmpty(m)) map[m.ToLowerInvariant()] = house;
            }
            return map;
        }

        #endregion

        #region Season start, end and ceremony

        private bool StartSeason(int days, string name, string why)
        {
            if (season.Active) return false;
            DateTime now = Now();
            int number = legends.LastSeasonNumber + 1;
            legends.LastSeasonNumber = number;
            season = new SeasonData
            {
                Number = number,
                Name = string.IsNullOrEmpty(name) ? string.Format(config.SeasonNameFormat, number) : name,
                Active = true,
                StartedAt = now,
                EndsAt = now.AddDays(days),
                LastCrownTick = now,
                ChronicleCursor = Math.Max(0, ChronicleLastId())
            };
            if (legends.Current != null && legends.Current.Season == 0) legends.Current.Season = number;
            SaveSeason();
            SaveLegends();

            string ends = season.EndsAt.ToString("yyyy-MM-dd HH:mm");
            Herald("BroadcastStart", season.Name, ends);
            Chronicle("season_started", season.Name + " begins",
                "A new season of the realm opens and runs until " + ends + " UTC. " + Capitalise(why) + ".",
                legends.Current != null ? new[] { legends.Current.Monarch } : new string[0]);
            Puts(season.Name + " started (" + why + "), ends " + ends + " UTC.");
            return true;
        }

        private void EndSeason(string why)
        {
            if (!season.Active) return;
            DateTime now = Now();
            CreditCrownTime(now);
            if (config.ReadChronicle) ReadNewChronicle();
            CheckTreatiesKept();

            List<StandingLine> ranked = Ranked();
            var record = new SeasonRecord
            {
                Number = season.Number,
                Name = season.Name,
                Start = season.StartedAt,
                End = now
            };
            for (int i = 0; i < ranked.Count && i < config.CeremonyTopHouses; i++) record.Top.Add(ranked[i]);
            bool hasChampion = ranked.Count > 0 && ranked[0].Score > 0;
            if (hasChampion)
            {
                record.Champion = ranked[0].House;
                record.ChampionScore = ranked[0].Score;
                HouseStanding champ = Standing(ranked[0].House);
                champ.Honours.Add("Champion of " + season.Name);
            }
            record.LongestReign = LongestReignIn(season.StartedAt, now);
            legends.Seasons.Add(record);

            Herald("BroadcastEnding", season.Name);
            foreach (StandingLine l in record.Top) Herald("BroadcastPlace", l.Rank, l.House, l.Score);
            if (hasChampion) Herald("BroadcastChampion", record.Champion, season.Name);
            else Herald("BroadcastNoChampion");
            if (record.LongestReign != null) Herald("BroadcastLongest", record.LongestReign);

            var detail = new System.Text.StringBuilder();
            foreach (StandingLine l in record.Top)
            {
                if (detail.Length > 0) detail.Append("; ");
                detail.Append("#" + l.Rank + " House " + l.House + " " + l.Score + " pts");
            }
            if (detail.Length == 0) detail.Append("No house earned glory");
            if (record.LongestReign != null) detail.Append(". Longest reign: " + record.LongestReign);
            detail.Append(". Ended by " + why + ".");
            Chronicle("season_ended",
                hasChampion ? season.Name + " ends: House " + record.Champion + " is champion" : season.Name + " ends without a champion",
                detail.ToString(), new string[0]);

            season.Active = false;
            season.EndsAt = now;
            SaveSeason();
            SaveLegends();
            Puts(season.Name + " ended (" + why + ").");
        }

        private string LongestReignIn(DateTime from, DateTime to)
        {
            Reign best = null;
            double bestDays = 0;
            var all = new List<Reign>(legends.Hall);
            if (legends.Current != null) all.Add(legends.Current);
            foreach (Reign r in all)
            {
                DateTime s = r.Start > from ? r.Start : from;
                DateTime e = r.End.HasValue && r.End.Value < to ? r.End.Value : to;
                double d = (e - s).TotalDays;
                if (d > bestDays) { bestDays = d; best = r; }
            }
            if (best == null) return null;
            return best.Monarch + " of House " + (best.House ?? "none") + " (" + FormatDays(bestDays) + ")";
        }

        #endregion

        #region Scoring

        private HouseStanding Standing(string house)
        {
            house = CleanName(house) ?? "?";
            foreach (KeyValuePair<string, HouseStanding> kv in season.Houses)
                if (SameName(kv.Key, house)) return kv.Value;
            var h = new HouseStanding { House = house };
            season.Houses[house] = h;
            return h;
        }

        private int Score(HouseStanding h)
        {
            ScoreWeights w = config.Weights;
            double s = h.CrownSeconds / 86400.0 * w.CrownDay
                + h.RebellionsWon * w.RebellionWon
                + h.RebellionsDefended * w.RebellionDefended
                + h.TreatiesKept * w.TreatyKept
                + h.TreatiesBroken * w.TreatyBroken
                + h.OathsBroken * w.OathBroken
                + h.ContractsFulfilled * w.ContractFulfilled
                + h.EventPoints * w.EventPointsFactor;
            return (int)Math.Round(s);
        }

        private List<StandingLine> Ranked()
        {
            var list = new List<StandingLine>();
            foreach (HouseStanding h in season.Houses.Values)
                if (h != null) list.Add(new StandingLine { House = h.House, Score = Score(h), CrownDays = Math.Round(h.CrownSeconds / 86400.0, 2) });
            list.Sort(delegate(StandingLine x, StandingLine y)
            {
                int c = y.Score.CompareTo(x.Score);
                if (c != 0) return c;
                c = y.CrownDays.CompareTo(x.CrownDays);
                return c != 0 ? c : string.Compare(x.House, y.House, StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < list.Count; i++) list[i].Rank = i + 1;
            return list;
        }

        #endregion

        #region Commands

        [ChatCommand("season")]
        private void CmdSeason(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed) { player.SendError(Msg("LoadFailed", player)); return; }
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "": ShowStatus(player); break;
                case "help": Reply(player, "Help"); break;
                case "standings": ShowStandings(player); break;
                case "house": ShowHouse(player, JoinFrom(args, 1)); break;
                case "hall": ShowHall(player, args.Length > 1 ? args[1] : null); break;
                case "history": ShowHistory(player); break;
                case "start": AdminStart(player, args); break;
                case "end": AdminEnd(player); break;
                case "status": AdminStatus(player); break;
                default: Reply(player, "Help"); break;
            }
        }

        private void ShowStatus(Player player)
        {
            if (!season.Active) { Reply(player, "NoSeason"); Reply(player, "Help"); return; }
            DateTime now = Now();
            int day = Math.Max(1, (int)Math.Ceiling((now - season.StartedAt).TotalDays));
            int total = Math.Max(1, (int)Math.Round((season.EndsAt - season.StartedAt).TotalDays));
            Reply(player, "Status", season.Name, day, total, season.EndsAt.ToString("yyyy-MM-dd HH:mm"));
            List<StandingLine> ranked = Ranked();
            if (ranked.Count > 0) Reply(player, "StatusLeader", ranked[0].House, ranked[0].Score);
            string mine = HouseOfPlayer(player.Id);
            if (mine != null)
                foreach (StandingLine l in ranked)
                    if (SameName(l.House, mine)) { Reply(player, "StatusYours", l.House, l.Rank, l.Score); break; }
        }

        private void ShowStandings(Player player)
        {
            if (!season.Active) { Reply(player, "NoSeason"); return; }
            List<StandingLine> ranked = Ranked();
            if (ranked.Count == 0) { Reply(player, "StandingsNone"); return; }
            Reply(player, "StandingsHeader", season.Name);
            for (int i = 0; i < ranked.Count && i < config.StandingsShown; i++) StandingLineTo(player, ranked[i]);
        }

        private void StandingLineTo(Player player, StandingLine l)
        {
            HouseStanding h = Standing(l.House);
            ReplyRaw(player, "StandingsLine", l.Rank, h.House, l.Score, l.CrownDays.ToString("0.0"), h.RebellionsWon,
                h.RebellionsDefended, h.TreatiesKept, h.TreatiesBroken, h.ContractsFulfilled, h.EventPoints);
        }

        private void ShowHouse(Player player, string name)
        {
            if (!season.Active) { Reply(player, "NoSeason"); return; }
            if (string.IsNullOrEmpty(name)) name = HouseOfPlayer(player.Id);
            if (string.IsNullOrEmpty(name)) { Reply(player, "Help"); return; }
            foreach (StandingLine l in Ranked())
            {
                if (!SameName(l.House, name)) continue;
                StandingLineTo(player, l);
                HouseStanding h = Standing(l.House);
                if (h.Honours.Count > 0) ReplyRaw(player, "HouseHonours", string.Join("; ", h.Honours.ToArray()));
                return;
            }
            Reply(player, "HouseNotFound", name);
        }

        private void ShowHall(Player player, string pageArg)
        {
            var all = new List<Reign>(legends.Hall);
            if (legends.Current != null) all.Add(legends.Current);
            if (all.Count == 0) { Reply(player, "HallNone"); return; }
            all.Reverse();                                         // newest first
            int pages = (all.Count + config.HallPageSize - 1) / config.HallPageSize;
            int page;
            if (pageArg == null || !int.TryParse(pageArg, out page)) page = 1;
            page = Math.Max(1, Math.Min(pages, page));
            Reply(player, "HallHeader", all.Count, page, pages);
            DateTime now = Now();
            for (int i = (page - 1) * config.HallPageSize; i < all.Count && i < page * config.HallPageSize; i++)
            {
                Reign r = all[i];
                DateTime end = r.End.HasValue ? r.End.Value : now;
                ReplyRaw(player, "HallLine", r.Monarch, r.House ?? "none", r.Start.ToString("yyyy-MM-dd"),
                    r.End.HasValue ? r.End.Value.ToString("yyyy-MM-dd") : "now", FormatDays((end - r.Start).TotalDays),
                    r.End.HasValue ? (r.Ending ?? "ended") : "reigns still");
            }
        }

        private void ShowHistory(Player player)
        {
            if (legends.Seasons.Count == 0) { Reply(player, "HistoryNone"); return; }
            Reply(player, "HistoryHeader");
            for (int i = legends.Seasons.Count - 1, shown = 0; i >= 0 && shown < config.StandingsShown; i--, shown++)
            {
                SeasonRecord s = legends.Seasons[i];
                ReplyRaw(player, "HistoryLine", s.Name, s.Champion ?? "(none)", s.ChampionScore, s.LongestReign ?? "(none)");
            }
        }

        private void AdminStart(Player player, string[] args)
        {
            if (!IsAdmin(player)) { player.SendError(Msg("NoPermission", player)); return; }
            if (season.Active) { player.SendError(string.Format(Msg("AlreadyRunning", player), season.Name)); return; }
            int days = config.DefaultSeasonDays;
            int nameFrom = 1;
            if (args.Length > 1)
            {
                int parsed;
                if (int.TryParse(args[1], out parsed))
                {
                    if (parsed < 1 || parsed > 365) { player.SendError(Msg("BadDays", player)); return; }
                    days = parsed;
                    nameFrom = 2;
                }
            }
            string name = CleanName(JoinFrom(args, nameFrom));
            StartSeason(days, name, "proclaimed by " + player.Name);
            Reply(player, "Started", season.Name, season.EndsAt.ToString("yyyy-MM-dd HH:mm"));
        }

        private void AdminEnd(Player player)
        {
            if (!IsAdmin(player)) { player.SendError(Msg("NoPermission", player)); return; }
            if (!season.Active) { player.SendError(Msg("NotRunning", player)); return; }
            EndSeason("proclamation of " + player.Name);
        }

        private void AdminStatus(Player player)
        {
            if (!IsAdmin(player)) { player.SendError(Msg("NoPermission", player)); return; }
            Reply(player, "AdminStatus", season.Number, season.Active, season.ChronicleCursor, season.Houses.Count, season.Treaties.Count,
                CrownAndConsequences != null ? "CrownAndConsequences" : "game", RealmChronicle != null, RealmHouses != null);
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // RealmEvents and others: award (or, negative, take) season points from a house. Returns false when no season
        // is running or the house is unknown. Each call is capped at MaxEventAwardPerCall in either direction.
        private bool AwardHouse(string house, int points, string honour)
        {
            if (loadFailed || !season.Active || string.IsNullOrEmpty(CleanName(house))) return false;
            int cap = config.MaxEventAwardPerCall;
            if (points > cap) points = cap;
            if (points < -cap) points = -cap;
            HouseStanding h = Standing(house);
            h.EventPoints += points;
            string clean = CleanName(honour);
            if (clean != null && h.Honours.Count < 50) h.Honours.Add(clean);
            SaveSeason();
            return true;
        }

        private bool IsSeasonActive()
        {
            return !loadFailed && season != null && season.Active;
        }

        private int GetSeasonNumber()
        {
            return season != null && season.Active ? season.Number : 0;
        }

        private string GetSeasonName()
        {
            return season != null && season.Active ? season.Name : null;
        }

        // [{rank, house, score, crownDays}] best first. Names only.
        private List<Dictionary<string, object>> GetSeasonStandings()
        {
            var list = new List<Dictionary<string, object>>();
            if (loadFailed || season == null || !season.Active) return list;
            foreach (StandingLine l in Ranked())
                list.Add(new Dictionary<string, object> { { "rank", l.Rank }, { "house", l.House }, { "score", l.Score }, { "crownDays", l.CrownDays } });
            return list;
        }

        // [{monarch, house, start, end, ending}] oldest first; the open reign last with end = null.
        private List<Dictionary<string, object>> GetHallOfKings()
        {
            var list = new List<Dictionary<string, object>>();
            if (legends == null) return list;
            var all = new List<Reign>(legends.Hall);
            if (legends.Current != null) all.Add(legends.Current);
            foreach (Reign r in all)
                list.Add(new Dictionary<string, object>
                {
                    { "monarch", r.Monarch }, { "house", r.House }, { "start", Iso(r.Start) },
                    { "end", r.End.HasValue ? Iso(r.End.Value) : null }, { "ending", r.Ending }
                });
            return list;
        }

        #endregion

        #region Helpers

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private DateTime Now()
        {
            return clock();
        }

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            if (RealmChronicle == null) return;
            object r = RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
            // An older RealmChronicle without the season types rejects them (returns 0); fall back to a decree line.
            // A rejection of a type that was accepted before is the chronicle's duplicate filter: leave it dropped.
            if (r is int && (int)r > 0) chronicleTypeAccepted[type] = true;
            else if (r is int && (int)r == 0 && type != "decree" && !chronicleTypeAccepted.ContainsKey(type))
                RealmChronicle.Call("Log", "decree", title, detail, actors ?? new string[0]);
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private string HouseOfPlayer(ulong playerId)
        {
            if (RealmHouses != null) return CleanName(RealmHouses.Call("GetHouse", playerId.ToString()) as string);
            GuildScheme guilds = SocialAPI.Get<GuildScheme>();
            Guild g = guilds != null ? guilds.TryGetGuildByMember(playerId) : null;
            return g != null ? CleanName(g.Name) : null;
        }

        private static bool SameName(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // "House X prevailed" -> X
        private static bool Between(string s, string before, string after, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(s)) return false;
            int i = s.IndexOf(before, StringComparison.Ordinal);
            if (i < 0) return false;
            i += before.Length;
            int j = s.IndexOf(after, i, StringComparison.Ordinal);
            if (j <= i) return false;
            value = CleanName(s.Substring(i, j - i));
            return value != null;
        }

        // "House A and House B sign a treaty" -> A, B (tail null = to the end of the string)
        private static bool TwoHouses(string s, string head, string mid, string tail, out string a, out string b)
        {
            a = null;
            b = null;
            if (string.IsNullOrEmpty(s) || !s.StartsWith(head, StringComparison.Ordinal)) return false;
            int m = s.IndexOf(mid, head.Length, StringComparison.Ordinal);
            if (m < 0) return false;
            a = CleanName(s.Substring(head.Length, m - head.Length));
            int bStart = m + mid.Length;
            int bEnd = tail != null ? s.IndexOf(tail, bStart, StringComparison.Ordinal) : s.Length;
            if (bEnd < bStart) return false;
            b = CleanName(s.Substring(bStart, bEnd - bStart));
            return a != null && b != null;
        }

        private static string PrefixBefore(string s, string[] markers)
        {
            if (string.IsNullOrEmpty(s)) return null;
            foreach (string m in markers)
            {
                int i = s.IndexOf(m, StringComparison.Ordinal);
                if (i > 0) return CleanName(s.Substring(0, i));
            }
            return null;
        }

        private static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsControl(c)) sb.Append(c);
            string r = sb.ToString().Trim();
            if (r.Length > NameMax) r = r.Substring(0, NameMax).TrimEnd();
            return r.Length == 0 ? null : r;
        }

        private static string Capitalise(string s)
        {
            return string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string JoinFrom(string[] args, int start)
        {
            return args == null || start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start);
        }

        private static string FormatDays(double days)
        {
            if (days < 1) return Math.Max(0, (int)Math.Round(days * 24)) + " h";
            return days.ToString("0.#") + " days";
        }

        private static string Iso(DateTime t)
        {
            return t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'");
        }

        #endregion
    }
}
