// RealmEvents: scheduled realm events with countdown heralds, chronicle entries and season points.
//
//   crown_night   "Crown Night". Defaults to Saturday 19:00 UTC for 90 min, the same slot as CrownAndConsequences'
//                 default rebellion window, so declared claims are fought on the night. The countdown names the
//                 claims whose window falls in the night (CrownAndConsequences.GetOpenClaims) and tells houses how to
//                 declare. A house whose member completes a throne capture during the night earns points once
//                 (OnThroneCaptured, State == Completed [OPJ L607; state check as in CrownAndConsequences]); the house
//                 holding the crown when the night ends earns the hold points. This plugin never gates or opens the
//                 throne itself: CrownAndConsequences' own rebellion rules stay in charge.
//   tournament    "Royal Tournament". A timed PvP ranking. Entrants (/tourney join, or everyone with
//                 TournamentRequireJoin false) score a point per kill of another entrant, read only from the death
//                 hook OnEntityDeath(EntityDeathEvent) [OPJ L188; IL EntityHealth.InvokeDeath], victim =
//                 evt.Entity.Owner, killer = evt.KillingDamage.DamageSource.Owner [ASM; USE DeathMessages.cs:20].
//                 Housemates never count and each victim counts at most TournamentMaxKillsPerVictim times per killer.
//                 Against feeding (alts, housemates, allies giving up kills): kills of a liege, vassal or treaty
//                 partner's entrant do not count (TournamentAlliesCount false); houses are compared both as they were
//                 at entry and as they are now, so leaving a house for the evening does not help; a victim must be
//                 online (a sleeping body is no fight); one victim feeds at most TournamentMaxScoredDeathsPerVictim
//                 points in all; an entrant who leaves cannot enter again; and prizes and points need at least
//                 TournamentMinEntrantsForPrizes entrants who fought (killed or died).
//                 Prizes are real items given with the same calls RealmContracts uses (the game's own /give path:
//                 GetInventory -> Container.Contents, ItemCollection.AutoMergeAdd of InvGameItemStack, capped at
//                 ContainerManagement.StackLimit, every amount MEASURED with ItemCollection.AutoCount [IL
//                 ThronesCommandHandler.Give]). What does not fit goes into a persisted owed ledger (/event collect).
//   kings_hunt    "The King's Hunt". The reigning monarch (KingsScheme.IsKing [ASM][USE]) names up to HuntMaxTargets
//                 quarry with /hunt name <player>. A verified kill of a quarry (same death hook) by someone outside the
//                 quarry's house pays the hunter a prize and the hunter's house points; quarry still unclaimed at the
//                 end "survive the hunt" and their house earns points. No quarry named within HuntNamingMinutes =
//                 the hunt is called off. Against farming (HuntExcludeAllies): no quarry from the crown's own house,
//                 its liege, vassals or treaty partners; a quarry cannot be claimed by its house or allied houses
//                 (now, or when it was named: the members of those houses at naming are barred for the whole hunt);
//                 a quarry must be online when slain; the same hunter takes the same quarry for a prize at most once
//                 per HuntPairCooldownDays; a quarry survives only if online for HuntSurviveMinOnlinePercent of the
//                 hunt after being named (logging off is fleeing) and still of the house it was named with; and no
//                 player under RealmWarden's new-player protection is named (they cannot be harmed, so they would
//                 always survive, and naming them is harassment).
//   truce         "Truce of the Realm". A no-PvP proclamation. Enforced with OnEntityHealthChange(EntityDamageEvent)
//                 [OPJ L162], RB 1: evt.Cancel() + Damage.Amount = 0 + return true [USE NoFriendlyFire.cs:116-117];
//                 [IL] EntityHealth.InvokeDamage calls the hook first and returns early on a non-null result (the same
//                 basis as RealmLaws' King's Peace). UNVERIFIED in-game that this stops every kind of player damage
//                 (arrows, fire, siege): smoke test E5 checks it. Set "TruceEnforced": false for announce-only. Either
//                 way a player KILL during the truce is a breach: chronicled once per killer and the killer's house
//                 loses points. By default the truce yields to an active rebellion (CrownAndConsequences
//                 IsRebellionActive), because a declared rebellion window is the realm's highest law.
//
// Schedule: "Schedule" in oxide/config/RealmEvents.json, UTC. Days are weekday names, "Daily", "Weekdays" or
// "Weekends". Each event type has its own enable flag. Countdown heralds at CountdownMinutes before each start.
// A server started inside a window starts the event late (for what is left of it); each occurrence runs once.
// Events of conflicting kinds never overlap (truce vs tournament, kings_hunt or crown_night); a clash is skipped
// and logged.
//
// Season points go to RealmSeasons via its non-public AwardHouse(house, points, honour); without it events still run.
// Data: oxide/data/RealmEvents.json (running events, fired occurrences, owed prizes). If it exists but cannot be
// parsed, the plugin refuses to run and never writes it, so owed prizes are not lost.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError, GetInventory [ASM]
using CodeHatch.Damaging;                        // Damage [ASM]
using CodeHatch.Engine.Core.Cache;               // Entity [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;            // InvItemBlueprint, InvBlueprints, InvGameItemStack [ASM]
using CodeHatch.Inventory.Blueprints.Components; // ContainerManagement [ASM; IL ThronesCommandHandler.Give]
using CodeHatch.ItemContainer;                   // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events.Entities;      // EntityDamageEvent, EntityDeathEvent [ASM]
using CodeHatch.Thrones.AncientThrone;           // AncientThroneCaptureEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // Guild, GuildScheme, KingsScheme [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmEvents", "Realm", "0.1.0")]
    [Description("Scheduled realm events: Crown Night, the Royal Tournament, the King's Hunt and the Truce of the Realm")]
    public class RealmEvents : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmWarden;

        private const string PermAdmin = "realmevents.admin";
        private const string DataName = "RealmEvents";

        private const string KCrownNight = "crown_night";
        private const string KTournament = "tournament";
        private const string KHunt = "kings_hunt";
        private const string KTruce = "truce";
        private static readonly string[] Kinds = { KCrownNight, KTournament, KHunt, KTruce };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private Timer tickTimer;
        private readonly Dictionary<string, bool> chronicleTypeAccepted = new Dictionary<string, bool>();
        private readonly Dictionary<string, DateTime> lastNotice = new Dictionary<string, DateTime>();

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        #region Config

        private class ScheduleEntry
        {
            public string Id;                          // stable name of this slot; defaults to "<event>-<n>"
            public string Event;                       // crown_night | tournament | kings_hunt | truce
            public bool Enabled = true;
            public List<string> Days = new List<string>();
            public string StartUtc = "20:00";
            public int DurationMinutes = 60;
        }

        private class Prize
        {
            public int Place;                          // tournament place (1, 2, 3...); ignored for the hunt
            public string Item;                        // ResourceType name (e.g. "Stone") or exact item name
            public int Amount;
        }

        private class PluginConfig
        {
            public bool EnableCrownNight = true;
            public bool EnableTournament = true;
            public bool EnableKingsHunt = true;
            public bool EnableTruce = true;
            public List<ScheduleEntry> Schedule;
            public List<int> CountdownMinutes;
            public float TickSeconds = 10f;
            public int MaxManualMinutes = 360;

            public int CrownNightCapturePoints = 10;   // once per house per night
            public int CrownNightHoldPoints = 30;      // the house holding the crown at the end

            public bool TournamentRequireJoin = true;
            public bool TournamentHousematesCount = false;
            public bool TournamentAlliesCount = false;         // kills of a liege, vassal or treaty partner's entrants
            public int TournamentMaxKillsPerVictim = 2;
            public int TournamentMaxScoredDeathsPerVictim = 4; // one victim feeds at most this many points in all (0 = no cap)
            public int TournamentMinEntrantsForPrizes = 3;     // entrants who killed or died; fewer = no prizes or points (0 = off)
            public int TournamentMinKillsToPlace = 1;
            public List<int> TournamentPlacePoints;    // season points for 1st, 2nd, 3rd...
            public List<Prize> TournamentPrizes;

            public int HuntMaxTargets = 3;
            public int HuntNamingMinutes = 10;
            public int HuntKillPoints = 15;
            public int HuntSurvivePoints = 10;
            public List<Prize> HuntPrizes;             // paid to the hunter per quarry claimed
            public bool HuntExcludeAllies = true;      // no quarry from the crown's side; the quarry's own side cannot claim
            public bool HuntSkipProtectedPlayers = true;   // never name a player under RealmWarden's new-player protection
            public int HuntPairCooldownDays = 14;      // same hunter, same quarry: one prize per this many days (0 = off)
            public int HuntSurviveMinOnlinePercent = 75;   // share of the hunt (after naming) a quarry must be online to survive

            public bool TruceEnforced = true;
            public bool TruceYieldsToRebellion = true;
            public int TruceBreachPoints = -15;        // per killer, once per truce
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.Schedule = new List<ScheduleEntry>
            {
                new ScheduleEntry { Id = "crown-night", Event = KCrownNight, Days = new List<string> { "Saturday" }, StartUtc = "19:00", DurationMinutes = 90 },
                new ScheduleEntry { Id = "royal-tournament", Event = KTournament, Days = new List<string> { "Friday" }, StartUtc = "19:00", DurationMinutes = 60 },
                new ScheduleEntry { Id = "kings-hunt", Event = KHunt, Days = new List<string> { "Wednesday" }, StartUtc = "21:00", DurationMinutes = 60 },
                new ScheduleEntry { Id = "truce", Event = KTruce, Days = new List<string> { "Sunday" }, StartUtc = "12:00", DurationMinutes = 240 }
            };
            c.CountdownMinutes = new List<int> { 60, 30, 10, 5, 1 };
            c.TournamentPlacePoints = new List<int> { 30, 20, 10 };
            c.TournamentPrizes = new List<Prize>
            {
                new Prize { Place = 1, Item = "Stone", Amount = 300 },
                new Prize { Place = 2, Item = "Stone", Amount = 200 },
                new Prize { Place = 3, Item = "Stone", Amount = 100 }
            };
            c.HuntPrizes = new List<Prize> { new Prize { Place = 0, Item = "Wood", Amount = 200 } };
            return c;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private void ClampConfig()
        {
            PluginConfig d = DefaultConfig();
            if (config.Schedule == null) config.Schedule = d.Schedule;
            if (config.CountdownMinutes == null) config.CountdownMinutes = d.CountdownMinutes;
            if (config.TournamentPlacePoints == null) config.TournamentPlacePoints = d.TournamentPlacePoints;
            if (config.TournamentPrizes == null) config.TournamentPrizes = new List<Prize>();
            if (config.HuntPrizes == null) config.HuntPrizes = new List<Prize>();
            config.Schedule.RemoveAll(delegate(ScheduleEntry e) { return e == null || Array.IndexOf(Kinds, e.Event) < 0; });
            for (int i = 0; i < config.Schedule.Count; i++)
            {
                ScheduleEntry e = config.Schedule[i];
                if (string.IsNullOrEmpty(e.Id)) e.Id = e.Event + "-" + (i + 1);
                if (e.Days == null) e.Days = new List<string>();
                if (e.DurationMinutes < 1) e.DurationMinutes = 1;
                if (e.DurationMinutes > 1440) e.DurationMinutes = 1440;
            }
            config.CountdownMinutes.RemoveAll(delegate(int m) { return m < 1 || m > 1440; });
            config.CountdownMinutes.Sort();
            config.CountdownMinutes.Reverse();
            if (config.TickSeconds < 2f) config.TickSeconds = 2f;
            if (config.MaxManualMinutes < 1) config.MaxManualMinutes = 1;
            if (config.TournamentMaxKillsPerVictim < 1) config.TournamentMaxKillsPerVictim = 1;
            if (config.TournamentMinKillsToPlace < 1) config.TournamentMinKillsToPlace = 1;
            if (config.HuntMaxTargets < 1) config.HuntMaxTargets = 1;
            if (config.HuntNamingMinutes < 1) config.HuntNamingMinutes = 1;
            if (config.TournamentMaxScoredDeathsPerVictim < 0) config.TournamentMaxScoredDeathsPerVictim = 0;
            if (config.TournamentMinEntrantsForPrizes < 0) config.TournamentMinEntrantsForPrizes = 0;
            if (config.HuntPairCooldownDays < 0) config.HuntPairCooldownDays = 0;
            if (config.HuntSurviveMinOnlinePercent < 0) config.HuntSurviveMinOnlinePercent = 0;
            if (config.HuntSurviveMinOnlinePercent > 100) config.HuntSurviveMinOnlinePercent = 100;
            config.TournamentPrizes.RemoveAll(IsBadPrize);
            config.HuntPrizes.RemoveAll(IsBadPrize);
        }

        private static bool IsBadPrize(Prize p)
        {
            return p == null || string.IsNullOrEmpty(p.Item) || p.Amount <= 0 || p.Amount > 100000;
        }

        #endregion

        #region Data

        private class Entrant
        {
            public string Name;
            public string House;
            public int Kills;
            public int Deaths;
            public DateTime LastKill;
            public int Fed;                            // own deaths that scored for someone
            public Dictionary<string, int> Victims = new Dictionary<string, int>();   // victim id -> times counted
        }

        private class Quarry
        {
            public string Id;
            public string Name;
            public string House;
            public string ClaimedById;
            public string ClaimedByName;
            public bool NoPrize;                       // taken by a hunter still on HuntPairCooldownDays for this quarry
            public List<string> Barred = new List<string>();   // ids of the quarry's side when named: they never claim it
            public int Samples;                        // ticks while at large, and how many of them online
            public int OnlineSamples;
        }

        private class ActiveEvent
        {
            public string Kind;
            public string SlotId;                      // schedule entry id, or "manual"
            public DateTime Start;
            public DateTime End;
            // crown_night
            public string CrownHouseAtStart;
            public List<string> CaptureHouses = new List<string>();
            // tournament
            public Dictionary<string, Entrant> Entrants = new Dictionary<string, Entrant>();
            public List<string> Left = new List<string>();          // ids that left; they cannot enter again
            // kings_hunt
            public List<Quarry> Quarry = new List<Quarry>();
            public string NamedBy;
            // truce
            public List<string> Breakers = new List<string>();      // killer ids already chronicled this truce
            public int Blocked;                                     // damage events cancelled
        }

        private class Owed
        {
            public string PlayerId;
            public string PlayerName;
            public string Item;
            public int Amount;
            public string Reason;
        }

        private class StoredData
        {
            public List<ActiveEvent> Active = new List<ActiveEvent>();
            public Dictionary<string, DateTime> Fired = new Dictionary<string, DateTime>();       // occurrence key -> start
            public Dictionary<string, DateTime> Announced = new Dictionary<string, DateTime>();   // key|minutes -> start
            public List<Owed> Owed = new List<Owed>();
            public List<string> History = new List<string>();      // last results, newest last (for /events)
            public Dictionary<string, DateTime> HuntPairs = new Dictionary<string, DateTime>();   // "hunter|quarry" -> last prize
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Prefix", "[C8A050]Realm Events[FFFFFF]: " },
                { "Herald", "[C8A050]Herald[FFFFFF]: " },
                { "Help", "/events | /event collect | /tourney join|leave|standings | /hunt [name <player>] | /truce. Admin: /event start <crown_night|tournament|kings_hunt|truce> [minutes] | stop <kind> | cancel <kind>" },
                { "Name.crown_night", "Crown Night" },
                { "Name.tournament", "the Royal Tournament" },
                { "Name.kings_hunt", "the King's Hunt" },
                { "Name.truce", "the Truce of the Realm" },
                { "Countdown.crown_night", "Crown Night falls in {0} min. The throne is fought for tonight: houses with a declared claim may take it. {1}" },
                { "Countdown.tournament", "The Royal Tournament begins in {0} min. Enter with /tourney join; every kill of another entrant scores." },
                { "Countdown.kings_hunt", "The King's Hunt begins in {0} min. The monarch will name the quarry; hunters, ready your blades." },
                { "Countdown.truce", "The Truce of the Realm begins in {0} min. For {1} min no blood may be shed between players." },
                { "Begin.crown_night", "Crown Night has fallen! It lasts until {0} UTC. {1}" },
                { "Begin.tournament", "The Royal Tournament has begun! It ends {0} UTC. Enter with /tourney join." },
                { "Begin.kings_hunt", "The King's Hunt has begun! It ends {0} UTC. {1}" },
                { "Begin.truce", "The Truce of the Realm is proclaimed until {0} UTC. {1}" },
                { "ClaimsTonight", "Claims tonight: {0}." },
                { "NoClaimsTonight", "No claim stands for tonight yet: a house leader may still /claim declare." },
                { "HuntNameNow", "Monarch, name your quarry with /hunt name <player> within {0} min." },
                { "HuntNoMonarch", "There is no monarch to call the hunt; it is called off." },
                { "HuntCalledOff", "No quarry was named; the King's Hunt is called off." },
                { "TruceEnforcedLine", "Blows between players will not land." },
                { "TruceAnnouncedLine", "Blood shed now is a breach of the realm's peace and will be remembered." },
                { "End.crown_night", "Crown Night ends. {0}" },
                { "CrownHeldBy", "The crown rests with House {0}." },
                { "CrownHeldByNone", "The crown rests with no house." },
                { "CrownEmpty", "The throne stands empty." },
                { "End.tournament", "The Royal Tournament is over. {0}" },
                { "End.kings_hunt", "The King's Hunt is over. {0}" },
                { "End.truce", "The Truce of the Realm has ended. {0}" },
                { "Cancelled", "{0} is cancelled by the stewards." },
                { "Clash", "{0} cannot run alongside {1}." },
                { "Place", "#{0} {1} ({2} kills)" },
                { "NoChampion", "No one scored." },
                { "Survivors", "Survived the hunt: {0}." },
                { "NoSurvivors", "Every quarry was taken." },
                { "TruceKept", "The truce was kept." },
                { "TruceBroken", "Truce breakers: {0}." },
                { "EventsActive", "Now: {0} until {1} UTC ({2} min left)." },
                { "EventsNext", "Next: {0} at {1} UTC (in {2})." },
                { "EventsNone", "No realm events are scheduled." },
                { "EventsHistory", "Last: {0}" },
                { "NoPermission", "You may not do that." },
                { "UnknownKind", "Unknown event. Use crown_night, tournament, kings_hunt or truce." },
                { "KindDisabled", "{0} is disabled in the config." },
                { "BadMinutes", "Minutes must be a whole number from 1 to {0}." },
                { "AlreadyRunning", "{0} is already running." },
                { "NotRunning", "{0} is not running." },
                { "Started", "{0} started." },
                { "Joined", "You enter the Royal Tournament. Kills of other entrants score; housemates do not count." },
                { "AlreadyJoined", "You are already entered." },
                { "Left", "You leave the tournament. Your score is struck, and you cannot enter it again." },
                { "NotEntered", "You are not entered." },
                { "NoJoinNeeded", "Everyone fights in this tournament; there is nothing to join." },
                { "NoTournament", "No Royal Tournament is running or about to begin." },
                { "StandingsHeader", "Royal Tournament standings:" },
                { "StandingsLine", "  #{0} {1}{2}: {3} kills, {4} deaths" },
                { "StandingsNone", "No one has scored yet." },
                { "Scored", "Tournament: {0} kills." },
                { "NoHunt", "The King's Hunt is not running." },
                { "QuarryHeader", "The King's quarry:" },
                { "QuarryLine", "  {0}{1} - {2}" },
                { "QuarryOpen", "still at large" },
                { "QuarryTaken", "taken by {0}" },
                { "QuarryNone", "The monarch has named no quarry yet." },
                { "NotMonarch", "Only the reigning monarch may name the quarry." },
                { "PlayerNotFound", "No such person is online (or the name is ambiguous)." },
                { "QuarryFull", "You may name at most {0} quarry." },
                { "QuarrySelf", "You cannot hunt yourself." },
                { "QuarryAlready", "{0} is already named." },
                { "QuarryNamed", "{0}{1} is named quarry of the King's Hunt!" },
                { "QuarryTakenBroadcast", "{0} has taken the King's quarry {1}!" },
                { "QuarryTakenNoPrize", "{0} has taken the King's quarry {1}, but has already been paid for this quarry lately: no prize." },
                { "QuarryOwnSide", "{0} cannot claim the quarry: their house is on the quarry's side." },
                { "QuarryCrownSide", "{0} is of the crown's own house or its allies; name a quarry from outside them." },
                { "QuarryProtected", "{0} is under new-player protection and cannot be named quarry." },
                { "Fled", "Fled the hunt (offline for too long): {0}." },
                { "TooFew", "Too few took the field for prizes ({0} fought; {1} needed)." },
                { "NoScoreAlly", "Tournament: kills of your own or an allied house do not score." },
                { "NoRejoin", "You left this tournament; you cannot enter it again." },
                { "TruceActive", "The Truce of the Realm holds until {0} UTC ({1})." },
                { "TruceSuspended", "suspended while a rebellion is fought" },
                { "TruceEnforcedShort", "enforced" },
                { "TruceAnnouncedShort", "announced, not enforced" },
                { "NoTruce", "No truce is in force." },
                { "TruceBlocked", "The Truce of the Realm holds: your blow does not land." },
                { "TruceBreachBroadcast", "{0} has broken the Truce of the Realm!" },
                { "PrizeGiven", "You receive {0} {1} ({2})." },
                { "PrizeOwed", "Your packs are full. {0} {1} wait for you: /event collect" },
                { "NothingOwed", "Nothing is owed to you." }
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
            player.SendMessage(Msg("Prefix", player) + Fmt(key, player, args));      // single-string overload: brace safe
        }

        private void ReplyRaw(Player player, string key, params object[] args)
        {
            player.SendMessage(Fmt(key, player, args));
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Fmt(key, player, args));
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
        }

        private string KindName(string kind)
        {
            return Msg("Name." + kind, null);
        }

        private string KindTitle(string kind)
        {
            string n = KindName(kind);
            return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n.Substring(1) : n;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". RealmEvents will not run or write anything until the file is fixed or moved away, then reload.");
                return;
            }
            if (data == null) data = new StoredData();
            if (data.Active == null) data.Active = new List<ActiveEvent>();
            if (data.Fired == null) data.Fired = new Dictionary<string, DateTime>();
            if (data.Announced == null) data.Announced = new Dictionary<string, DateTime>();
            if (data.Owed == null) data.Owed = new List<Owed>();
            if (data.History == null) data.History = new List<string>();
            if (data.HuntPairs == null) data.HuntPairs = new Dictionary<string, DateTime>();
            data.Active.RemoveAll(delegate(ActiveEvent a) { return a == null || Array.IndexOf(Kinds, a.Kind) < 0; });
            foreach (ActiveEvent a in data.Active)
            {
                if (a.CaptureHouses == null) a.CaptureHouses = new List<string>();
                if (a.Entrants == null) a.Entrants = new Dictionary<string, Entrant>();
                if (a.Quarry == null) a.Quarry = new List<Quarry>();
                if (a.Breakers == null) a.Breakers = new List<string>();
                if (a.Left == null) a.Left = new List<string>();
                foreach (Entrant e in a.Entrants.Values) if (e != null && e.Victims == null) e.Victims = new Dictionary<string, int>();
                a.Quarry.RemoveAll(delegate(Quarry q) { return q == null || q.Id == null; });
                foreach (Quarry q in a.Quarry) if (q.Barred == null) q.Barred = new List<string>();
            }
            data.Owed.RemoveAll(delegate(Owed o) { return o == null || o.PlayerId == null || o.Item == null || o.Amount <= 0; });
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
                CheckPrizeItems();
                WarnRealmTimeOffset();
            }
            SafeTick();
        }

        // RealmEvents schedules in UTC; CrownAndConsequences' rebellion windows are in realm time
        // (UTC + UtcOffsetHours). With a non-zero offset the default Crown Night (Saturday 19:00 UTC)
        // no longer falls in the Saturday rebellion window, so say so once at start-up.
        private void WarnRealmTimeOffset()
        {
            if (CrownAndConsequences == null) return;
            object r = CrownAndConsequences.Call("GetUtcOffsetHours");
            if (!(r is double)) return;
            double off = (double)r;
            if (Math.Abs(off) < 0.01) return;
            Puts("CrownAndConsequences uses UtcOffsetHours = " + off + ", but RealmEvents' StartUtc times are UTC. " +
                 "Shift each StartUtc by " + (-off) + " h so Crown Night still falls inside the rebellion window " +
                 "(for example 19:00 realm time = " + DateTime.Today.AddHours(19 - off).ToString("HH:mm") + " UTC). " +
                 "Neither plugin follows daylight saving: both use fixed offsets.");
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
            if (loadFailed || player == null || player.IsServer) return;
            string id = player.Id.ToString();
            foreach (Owed o in data.Owed)
                if (o.PlayerId == id) { timer.Once(5f, delegate { PayOwed(player); }); break; }
        }

        // Warn once at start-up about prize items the server does not know (they would never be paid).
        private void CheckPrizeItems()
        {
            var all = new List<Prize>(config.TournamentPrizes);
            all.AddRange(config.HuntPrizes);
            foreach (Prize p in all)
                if (FindItem(p.Item) == null)
                    PrintWarning("Prize item '" + p.Item + "' is not known to this server; that prize will be skipped. Fix it in oxide/config/RealmEvents.json.");
        }

        #endregion

        #region Scheduler

        private void SafeTick()
        {
            if (loadFailed) return;
            try { Tick(); }
            catch (Exception ex) { PrintError("Event tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = Now();
            bool changed = false;

            foreach (ActiveEvent a in data.Active.ToArray())
            {
                if (now >= a.End) { FinishEvent(a, false); changed = true; continue; }
                if (a.Kind == KHunt) SampleQuarry(a);
                if (a.Kind == KHunt && a.Quarry.Count == 0 && (now - a.Start).TotalMinutes >= config.HuntNamingMinutes)
                {
                    data.Active.Remove(a);
                    Herald(Msg("HuntCalledOff", null));
                    Chronicle("event_ended", "The King's Hunt is called off", "No quarry was named in time.", new string[0]);
                    AddHistory(KindTitle(KHunt) + ": called off");
                    changed = true;
                }
            }

            int maxCountdown = config.CountdownMinutes.Count > 0 ? config.CountdownMinutes[0] : 0;
            foreach (ScheduleEntry e in config.Schedule)
            {
                if (!e.Enabled || !KindEnabled(e.Event)) continue;
                foreach (DateTime start in Occurrences(e, now.AddMinutes(-e.DurationMinutes), now.AddMinutes(maxCountdown + 1)))
                {
                    string key = e.Id + "@" + Iso(start);
                    DateTime end = start.AddMinutes(e.DurationMinutes);
                    if (now >= start && now < end)
                    {
                        if (data.Fired.ContainsKey(key)) continue;
                        data.Fired[key] = start;
                        changed = true;
                        if ((end - now).TotalMinutes < 1) continue;          // too little left to be worth starting
                        string clash;
                        if (!CanStart(e.Event, out clash))
                        {
                            PrintWarning("Skipped " + key + ": " + clash);
                            continue;
                        }
                        BeginEvent(e.Event, e.Id, now, end);
                    }
                    else if (now < start)
                    {
                        changed |= Countdown(e, key, start, now);
                    }
                }
            }

            // Forget occurrence keys older than nine days (the schedule is weekly at most).
            DateTime cutoff = now.AddDays(-9);
            changed |= Prune(data.Fired, cutoff) | Prune(data.Announced, cutoff);
            changed |= Prune(data.HuntPairs, now.AddDays(-Math.Max(1, config.HuntPairCooldownDays)));
            if (changed) SaveData();
        }

        private bool Countdown(ScheduleEntry e, string key, DateTime start, DateTime now)
        {
            int minutesLeft = (int)Math.Ceiling((start - now).TotalMinutes);
            bool due = false;
            foreach (int t in config.CountdownMinutes)
            {
                if (minutesLeft > t) continue;
                string akey = key + "|" + t;
                if (data.Announced.ContainsKey(akey)) continue;
                data.Announced[akey] = start;
                due = true;                                    // several thresholds passed at once: one herald
            }
            if (!due) return false;
            Herald(CountdownText(e.Event, minutesLeft, e.DurationMinutes));
            return true;
        }

        private string CountdownText(string kind, int minutes, int duration)
        {
            if (kind == KCrownNight) return Fmt("Countdown.crown_night", null, minutes, ClaimsLine());
            if (kind == KTruce) return Fmt("Countdown.truce", null, minutes, duration);
            return Fmt("Countdown." + kind, null, minutes);
        }

        // Occurrence starts of a schedule entry with from <= start <= to (UTC).
        private static List<DateTime> Occurrences(ScheduleEntry e, DateTime from, DateTime to)
        {
            var list = new List<DateTime>();
            TimeSpan at;
            if (!TryParseHourMinute(e.StartUtc, out at)) return list;
            for (DateTime day = from.Date.AddDays(-1); day <= to.Date; day = day.AddDays(1))
            {
                if (!DayMatches(e.Days, day.DayOfWeek)) continue;
                DateTime s = day + at;
                if (s >= from && s <= to) list.Add(s);
            }
            return list;
        }

        private static bool DayMatches(List<string> days, DayOfWeek dow)
        {
            foreach (string raw in days)
            {
                string d = (raw ?? "").Trim();
                if (d.Equals("Daily", StringComparison.OrdinalIgnoreCase)) return true;
                if (d.Equals("Weekdays", StringComparison.OrdinalIgnoreCase) && dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday) return true;
                if (d.Equals("Weekends", StringComparison.OrdinalIgnoreCase) && (dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday)) return true;
                if (d.Equals(dow.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private DateTime? NextStart(ScheduleEntry e, DateTime now)
        {
            List<DateTime> next = Occurrences(e, now, now.AddDays(8));
            if (next.Count == 0) return null;
            return next[0];
        }

        private bool KindEnabled(string kind)
        {
            if (kind == KCrownNight) return config.EnableCrownNight;
            if (kind == KTournament) return config.EnableTournament;
            if (kind == KHunt) return config.EnableKingsHunt;
            if (kind == KTruce) return config.EnableTruce;
            return false;
        }

        private static bool Clashes(string a, string b)
        {
            if (a == b) return true;
            if (a == KTruce || b == KTruce) return true;          // a truce cannot share the field with any fighting event
            return false;
        }

        private bool CanStart(string kind, out string why)
        {
            foreach (ActiveEvent a in data.Active)
                if (Clashes(kind, a.Kind)) { why = Fmt("Clash", null, KindTitle(kind), KindName(a.Kind)); return false; }
            why = null;
            return true;
        }

        private ActiveEvent Running(string kind)
        {
            foreach (ActiveEvent a in data.Active) if (a.Kind == kind) return a;
            return null;
        }

        private static bool Prune(Dictionary<string, DateTime> d, DateTime cutoff)
        {
            var old = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in d) if (kv.Value < cutoff) old.Add(kv.Key);
            foreach (string k in old) d.Remove(k);
            return old.Count > 0;
        }

        #endregion

        #region Begin and finish

        private ActiveEvent BeginEvent(string kind, string slot, DateTime now, DateTime end)
        {
            var a = new ActiveEvent { Kind = kind, SlotId = slot, Start = now, End = end };
            string endText = end.ToString("HH:mm");
            string line;
            if (kind == KCrownNight)
            {
                string monarch, house;
                CurrentCrown(out monarch, out house);
                a.CrownHouseAtStart = house;
                line = Fmt("Begin.crown_night", null, endText, ClaimsLine());
            }
            else if (kind == KHunt)
            {
                string monarch, house;
                CurrentCrown(out monarch, out house);
                if (monarch == null)
                {
                    Herald(Msg("HuntNoMonarch", null));
                    AddHistory(KindTitle(KHunt) + ": no monarch");
                    return null;
                }
                a.NamedBy = monarch;
                line = Fmt("Begin.kings_hunt", null, endText, Fmt("HuntNameNow", null, config.HuntNamingMinutes));
            }
            else if (kind == KTruce)
            {
                line = Fmt("Begin.truce", null, endText, Msg(config.TruceEnforced ? "TruceEnforcedLine" : "TruceAnnouncedLine", null));
            }
            else
            {
                line = Fmt("Begin.tournament", null, endText);
                // Entries made during the countdown carry over.
                if (pendingEntrants.Count > 0)
                {
                    foreach (KeyValuePair<string, Entrant> kv in pendingEntrants) a.Entrants[kv.Key] = kv.Value;
                    pendingEntrants.Clear();
                }
            }
            data.Active.Add(a);
            SaveData();
            Herald(line);
            Chronicle("event_started", KindTitle(kind) + " begins", StripTags(line), new string[0]);
            Puts(kind + " started (" + slot + "), ends " + Iso(end));
            return a;
        }

        // Ends an event. cancelled = no awards (admin /event cancel).
        private void FinishEvent(ActiveEvent a, bool cancelled)
        {
            data.Active.Remove(a);
            SaveData();                                        // state changes before anything is paid
            if (cancelled)
            {
                string text = Fmt("Cancelled", null, KindTitle(a.Kind));
                Herald(text);
                Chronicle("event_ended", KindTitle(a.Kind) + " is cancelled", text, new string[0]);
                AddHistory(KindTitle(a.Kind) + ": cancelled");
                return;
            }
            if (a.Kind == KCrownNight) FinishCrownNight(a);
            else if (a.Kind == KTournament) FinishTournament(a);
            else if (a.Kind == KHunt) FinishHunt(a);
            else FinishTruce(a);
            SaveData();
        }

        private void FinishCrownNight(ActiveEvent a)
        {
            string monarch, house;
            CurrentCrown(out monarch, out house);
            string result = monarch == null ? Msg("CrownEmpty", null)
                : (house != null ? Fmt("CrownHeldBy", null, house) : Msg("CrownHeldByNone", null));
            if (house != null)
                Award(house, config.CrownNightHoldPoints, "Held the crown at the close of Crown Night (" + monarch + ")");
            string text = Fmt("End.crown_night", null, result);
            Herald(text);
            string detail = result;
            if (a.CaptureHouses.Count > 0) detail += " Houses that took the throne tonight: " + string.Join(", ", a.CaptureHouses.ToArray()) + ".";
            if (a.CrownHouseAtStart != null && house != null && !SameName(a.CrownHouseAtStart, house))
                detail += " The crown changed hands from House " + a.CrownHouseAtStart + ".";
            Chronicle("event_ended", "Crown Night ends", detail, monarch != null ? new[] { monarch } : new string[0]);
            AddHistory("Crown Night: " + result);
        }

        private void FinishTournament(ActiveEvent a)
        {
            int fought = 0;
            foreach (Entrant e in a.Entrants.Values) if (e.Kills > 0 || e.Deaths > 0) fought++;
            if (config.TournamentMinEntrantsForPrizes > 0 && fought < config.TournamentMinEntrantsForPrizes)
            {
                string few = Fmt("TooFew", null, fought, config.TournamentMinEntrantsForPrizes);
                Herald(Fmt("End.tournament", null, few));
                Chronicle("event_ended", "The Royal Tournament ends", few + " " + a.Entrants.Count + " entered.", new string[0]);
                AddHistory("Royal Tournament: too few fought");
                return;
            }
            List<KeyValuePair<string, Entrant>> ranked = RankEntrants(a);
            var places = new List<string>();
            int place = 0;
            foreach (KeyValuePair<string, Entrant> kv in ranked)
            {
                if (kv.Value.Kills < config.TournamentMinKillsToPlace) break;
                place++;
                if (place > Math.Max(config.TournamentPlacePoints.Count, MaxPrizePlace())) break;
                Entrant en = kv.Value;
                places.Add(Fmt("Place", null, place, en.Name, en.Kills));
                if (place <= config.TournamentPlacePoints.Count && en.House != null)
                    Award(en.House, config.TournamentPlacePoints[place - 1], "Royal Tournament #" + place + ": " + en.Name);
                foreach (Prize p in config.TournamentPrizes)
                    if (p.Place == place) GrantPrize(kv.Key, en.Name, p, "Royal Tournament #" + place);
            }
            string result = places.Count > 0 ? string.Join(", ", places.ToArray()) : Msg("NoChampion", null);
            Herald(Fmt("End.tournament", null, result));
            if (places.Count > 0)
            {
                Entrant champ = ranked[0].Value;
                Chronicle("tournament_champion", champ.Name + " wins the Royal Tournament",
                    (champ.House != null ? champ.Name + " of House " + champ.House : champ.Name) + " is champion with " + champ.Kills
                    + " kills. Standings: " + result + ".", new[] { champ.Name });
            }
            else Chronicle("event_ended", "The Royal Tournament ends", "No one scored. " + a.Entrants.Count + " entered.", new string[0]);
            AddHistory("Royal Tournament: " + result);
        }

        private int MaxPrizePlace()
        {
            int m = 0;
            foreach (Prize p in config.TournamentPrizes) if (p.Place > m) m = p.Place;
            return m;
        }

        private void FinishHunt(ActiveEvent a)
        {
            SampleQuarry(a);
            var survivors = new List<string>();
            var fled = new List<string>();
            foreach (Quarry q in a.Quarry)
            {
                if (q.ClaimedById != null) continue;
                if (q.OnlineSamples * 100 < config.HuntSurviveMinOnlinePercent * q.Samples) { fled.Add(q.Name); continue; }
                survivors.Add(q.Name);
                // Points go to the house the quarry was named with, and only while they still belong to it.
                string houseNow = HouseOf(ParseId(q.Id));
                if (q.House != null && SameName(houseNow, q.House)) Award(q.House, config.HuntSurvivePoints, "Survived the King's Hunt: " + q.Name);
            }
            string result = a.Quarry.Count == 0 ? Msg("HuntCalledOff", null)
                : (survivors.Count > 0 ? Fmt("Survivors", null, string.Join(", ", survivors.ToArray())) : Msg("NoSurvivors", null));
            if (fled.Count > 0) result += " " + Fmt("Fled", null, string.Join(", ", fled.ToArray()));
            Herald(Fmt("End.kings_hunt", null, result));
            Chronicle("event_ended", "The King's Hunt ends", result, survivors.ToArray());
            AddHistory("King's Hunt: " + result);
        }

        private void FinishTruce(ActiveEvent a)
        {
            string result = a.Breakers.Count == 0 ? Msg("TruceKept", null) : Fmt("TruceBroken", null, a.Breakers.Count);
            Herald(Fmt("End.truce", null, result));
            Chronicle("event_ended", "The Truce of the Realm ends", result + (a.Blocked > 0 ? " " + a.Blocked + " blows were stayed." : ""), new string[0]);
            AddHistory("Truce: " + result);
        }

        private List<KeyValuePair<string, Entrant>> RankEntrants(ActiveEvent a)
        {
            var list = new List<KeyValuePair<string, Entrant>>(a.Entrants);
            list.Sort(delegate(KeyValuePair<string, Entrant> x, KeyValuePair<string, Entrant> y)
            {
                int c = y.Value.Kills.CompareTo(x.Value.Kills);
                if (c != 0) return c;
                c = x.Value.Deaths.CompareTo(y.Value.Deaths);
                if (c != 0) return c;
                return x.Value.LastKill.CompareTo(y.Value.LastKill);     // reached the score first
            });
            return list;
        }

        private void AddHistory(string line)
        {
            data.History.Add(Now().ToString("MM-dd HH:mm") + " " + line);
            while (data.History.Count > 10) data.History.RemoveAt(0);
        }

        private string ClaimsLine()
        {
            var houses = new List<string>();
            if (CrownAndConsequences != null)
            {
                string[] claims = CrownAndConsequences.Call("GetOpenClaims") as string[];
                if (claims != null)
                    foreach (string c in claims)
                    {
                        string[] parts = (c ?? "").Split('|');
                        if (parts.Length >= 2 && parts[0].Length > 0) houses.Add("House " + parts[0] + " (" + parts[1] + ")");
                    }
            }
            return houses.Count > 0 ? Fmt("ClaimsTonight", null, string.Join(", ", houses.ToArray())) : Msg("NoClaimsTonight", null);
        }

        #endregion

        #region Hooks: throne, death, damage

        // OnThroneCaptured [OPJ L607]; real plugins treat it as a crowning. UNVERIFIED firing states, hence the check.
        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (loadFailed || evt == null || evt.Cancelled || evt.Player == null || evt.Player.IsServer) return;
            if (evt.State != AncientThroneCaptureEvent.States.Completed) return;
            ActiveEvent a = Running(KCrownNight);
            if (a == null) return;
            string house = HouseOf(evt.Player.Id);
            if (house == null) return;
            foreach (string h in a.CaptureHouses) if (SameName(h, house)) return;   // once per house per night
            a.CaptureHouses.Add(house);
            Award(house, config.CrownNightCapturePoints, "Took the throne on Crown Night (" + evt.Player.Name + ")");
            SaveData();
        }

        // RB 1 [OPJ L188]: always return null so the game's own death handling continues.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (loadFailed || data == null || data.Active.Count == 0 || evt == null) return null;
            try { HandleDeath(evt); }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        private void HandleDeath(EntityDeathEvent evt)
        {
            if (evt.Entity == null || !evt.Entity.IsPlayer || evt.KillingDamage == null) return;
            Player victim = evt.Entity.Owner;
            Player killer = evt.KillingDamage.DamageSource != null ? evt.KillingDamage.DamageSource.Owner : null;
            if (victim == null || killer == null || victim.IsServer || killer.IsServer || victim.Id == killer.Id) return;
            string vid = victim.Id.ToString(), kid = killer.Id.ToString();
            string vHouse = HouseOf(victim.Id), kHouse = HouseOf(killer.Id);
            bool sameHouse = vHouse != null && kHouse != null && SameName(vHouse, kHouse);
            DateTime now = Now();
            bool changed = false;

            // A sleeping body left by a logged-out player is no fight (UNVERIFIED whether the game reports its death).
            bool victimOnline = OnlineById(vid) != null;

            ActiveEvent t = Running(KTournament);
            if (t != null && now < t.End && victimOnline)
            {
                Entrant ke = TournamentEntrant(t, killer, kHouse);
                Entrant ve = TournamentEntrant(t, victim, vHouse);
                if (ke != null && ve != null)
                {
                    ve.Deaths++;
                    changed = true;
                    int times;
                    ke.Victims.TryGetValue(vid, out times);
                    // Houses as they were at entry and as they are now: leaving a house for the evening does not help.
                    bool housemates = AnySame(kHouse, ke.House, vHouse, ve.House);
                    bool allies = !housemates && !config.TournamentAlliesCount && AnyAllied(kHouse, ke.House, vHouse, ve.House);
                    bool fedOut = config.TournamentMaxScoredDeathsPerVictim > 0 && ve.Fed >= config.TournamentMaxScoredDeathsPerVictim;
                    if ((housemates && !config.TournamentHousematesCount) || allies) NoticeThrottled(killer, "NoScoreAlly");
                    else if (times < config.TournamentMaxKillsPerVictim && !fedOut)
                    {
                        ke.Victims[vid] = times + 1;
                        ke.Kills++;
                        ke.LastKill = now;
                        ve.Fed++;
                        NoticeThrottled(killer, "Scored", ke.Kills);
                    }
                }
            }

            ActiveEvent h = Running(KHunt);
            if (h != null && now < h.End && victimOnline)
            {
                foreach (Quarry q in h.Quarry)
                {
                    if (q.Id != vid || q.ClaimedById != null || sameHouse) continue;
                    if (config.HuntExcludeAllies && (q.Barred.Contains(kid) || Allied(kHouse, q.House) || Allied(kHouse, vHouse)))
                    {
                        NoticeThrottled(killer, "QuarryOwnSide", killer.Name);
                        continue;
                    }
                    q.ClaimedById = kid;
                    q.ClaimedByName = killer.Name;
                    changed = true;
                    string pair = kid + "|" + vid;
                    DateTime lastPaid;
                    if (config.HuntPairCooldownDays > 0 && data.HuntPairs.TryGetValue(pair, out lastPaid)
                        && (now - lastPaid).TotalDays < config.HuntPairCooldownDays)
                    {
                        // Taken (the quarry does not survive), but the same pair is not paid twice in the cooldown.
                        q.NoPrize = true;
                        SaveData();
                        Herald(Fmt("QuarryTakenNoPrize", null, killer.Name, q.Name));
                        continue;
                    }
                    data.HuntPairs[pair] = now;
                    SaveData();                                  // claimed before anything is paid
                    Herald(Fmt("QuarryTakenBroadcast", null, killer.Name, q.Name));
                    Chronicle("hunt_kill", killer.Name + " takes the King's quarry " + q.Name,
                        killer.Name + (kHouse != null ? " of House " + kHouse : "") + " slew " + q.Name
                        + (q.House != null ? " of House " + q.House : "") + ", named quarry by " + (h.NamedBy ?? "the crown") + ".",
                        new[] { killer.Name, q.Name });
                    if (kHouse != null) Award(kHouse, config.HuntKillPoints, "Took the King's quarry " + q.Name + " (" + killer.Name + ")");
                    foreach (Prize p in config.HuntPrizes) GrantPrize(kid, killer.Name, p, "the King's Hunt");
                }
            }

            ActiveEvent tr = Running(KTruce);
            // A kill while the truce is suspended by an open rebellion is lawful war, not a breach.
            if (tr != null && now < tr.End && !tr.Breakers.Contains(kid) && !TruceSuspended())
            {
                tr.Breakers.Add(kid);
                changed = true;
                Herald(Fmt("TruceBreachBroadcast", null, killer.Name));
                Chronicle("truce_broken", killer.Name + " breaks the Truce of the Realm",
                    killer.Name + (kHouse != null ? " of House " + kHouse : "") + " slew " + victim.Name + " while the realm's truce held.",
                    new[] { killer.Name, victim.Name });
                if (kHouse != null) Award(kHouse, config.TruceBreachPoints, "Broke the Truce of the Realm (" + killer.Name + ")");
            }
            if (changed) SaveData();
        }

        // Entrant for a kill/death, or null if this player does not take part.
        private Entrant TournamentEntrant(ActiveEvent t, Player p, string house)
        {
            string id = p.Id.ToString();
            Entrant e;
            if (t.Entrants.TryGetValue(id, out e)) return e;
            if (config.TournamentRequireJoin) return null;
            e = new Entrant { Name = p.Name, House = house };
            t.Entrants[id] = e;
            return e;
        }

        // RB 1 [OPJ L162]. Returns early unless a truce is running; see the header for what is verified.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (loadFailed || data == null || data.Active.Count == 0 || !config.TruceEnforced || evt == null || evt.Cancelled) return null;
            ActiveEvent tr = Running(KTruce);
            if (tr == null || Now() >= tr.End) return null;
            try
            {
                Entity ve = evt.Entity;
                if (ve == null || !ve.IsPlayer) return null;
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f || d.DamageSource == null || !d.DamageSource.IsPlayer) return null;
                Player victim = ve.Owner, attacker = d.DamageSource.Owner;
                if (victim == null || attacker == null || victim.IsServer || attacker.IsServer || victim.Id == attacker.Id) return null;
                if (TruceSuspended()) return null;
                evt.Cancel("Truce of the Realm");
                d.Amount = 0f;
                tr.Blocked++;
                NoticeThrottled(attacker, "TruceBlocked");
                return true;
            }
            catch (Exception ex)
            {
                PrintError("Truce check failed: " + ex.Message);
                return null;
            }
        }

        private bool TruceSuspended()
        {
            if (!config.TruceYieldsToRebellion || CrownAndConsequences == null) return false;
            object r = CrownAndConsequences.Call("IsRebellionActive");
            return r is bool && (bool)r;
        }

        #endregion

        #region Commands

        private readonly Dictionary<string, Entrant> pendingEntrants = new Dictionary<string, Entrant>();

        [ChatCommand("events")]
        private void CmdEvents(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (loadFailed) { player.SendError("Realm events are paused: oxide/data/RealmEvents.json could not be read."); return; }
            DateTime now = Now();
            foreach (ActiveEvent a in data.Active)
                Reply(player, "EventsActive", KindTitle(a.Kind), a.End.ToString("HH:mm"), Math.Max(0, (int)Math.Ceiling((a.End - now).TotalMinutes)));
            var upcoming = new List<KeyValuePair<DateTime, string>>();
            foreach (ScheduleEntry e in config.Schedule)
            {
                if (!e.Enabled || !KindEnabled(e.Event)) continue;
                DateTime? next = NextStart(e, now);
                if (next.HasValue) upcoming.Add(new KeyValuePair<DateTime, string>(next.Value, e.Event));
            }
            upcoming.Sort(delegate(KeyValuePair<DateTime, string> x, KeyValuePair<DateTime, string> y) { return x.Key.CompareTo(y.Key); });
            if (upcoming.Count == 0 && data.Active.Count == 0) Reply(player, "EventsNone");
            foreach (KeyValuePair<DateTime, string> u in upcoming)
                Reply(player, "EventsNext", KindTitle(u.Value), u.Key.ToString("ddd HH:mm"), Until(u.Key - now));
            if (data.History.Count > 0) Reply(player, "EventsHistory", data.History[data.History.Count - 1]);
            Reply(player, "Help");
        }

        [ChatCommand("event")]
        private void CmdEvent(Player player, string command, string[] args)
        {
            if (player == null || loadFailed) return;
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "collect")
            {
                if (!HasOwed(player.Id.ToString())) { Reply(player, "NothingOwed"); return; }
                PayOwed(player);
                return;
            }
            if (sub != "start" && sub != "stop" && sub != "cancel") { CmdEvents(player, command, args); return; }
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string kind = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (Array.IndexOf(Kinds, kind) < 0) { ReplyError(player, "UnknownKind"); return; }

            if (sub == "start")
            {
                if (!KindEnabled(kind)) { ReplyError(player, "KindDisabled", KindTitle(kind)); return; }
                int minutes = 60;
                if (args.Length > 2 && (!int.TryParse(args[2], out minutes) || minutes < 1 || minutes > config.MaxManualMinutes))
                {
                    ReplyError(player, "BadMinutes", config.MaxManualMinutes);
                    return;
                }
                if (Running(kind) != null) { ReplyError(player, "AlreadyRunning", KindTitle(kind)); return; }
                string why;
                if (!CanStart(kind, out why)) { player.SendError(why); return; }
                DateTime now = Now();
                if (BeginEvent(kind, "manual", now, now.AddMinutes(minutes)) != null) Reply(player, "Started", KindTitle(kind));
                return;
            }
            ActiveEvent a = Running(kind);
            if (a == null) { ReplyError(player, "NotRunning", KindTitle(kind)); return; }
            FinishEvent(a, sub == "cancel");
        }

        [ChatCommand("tourney")]
        private void CmdTourney(Player player, string command, string[] args)
        {
            if (player == null || loadFailed) return;
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "standings";
            ActiveEvent t = Running(KTournament);
            string id = player.Id.ToString();
            if (sub == "join")
            {
                if (!config.TournamentRequireJoin) { Reply(player, "NoJoinNeeded"); return; }
                Dictionary<string, Entrant> book = t != null ? t.Entrants : (TournamentSoon() ? pendingEntrants : null);
                if (book == null) { Reply(player, "NoTournament"); return; }
                if (book.ContainsKey(id)) { Reply(player, "AlreadyJoined"); return; }
                if (t != null && t.Left.Contains(id)) { ReplyError(player, "NoRejoin"); return; }
                book[id] = new Entrant { Name = player.Name, House = HouseOf(player.Id) };
                if (t != null) SaveData();
                Reply(player, "Joined");
                return;
            }
            if (sub == "leave")
            {
                Dictionary<string, Entrant> book = t != null ? t.Entrants : pendingEntrants;
                if (!book.Remove(id)) { Reply(player, "NotEntered"); return; }
                if (t != null) { t.Left.Add(id); SaveData(); }
                Reply(player, "Left");
                return;
            }
            if (t == null) { Reply(player, "NoTournament"); return; }
            List<KeyValuePair<string, Entrant>> ranked = RankEntrants(t);
            if (ranked.Count == 0 || ranked[0].Value.Kills == 0) { Reply(player, "StandingsNone"); }
            else
            {
                Reply(player, "StandingsHeader");
                for (int i = 0; i < ranked.Count && i < 10; i++)
                {
                    Entrant e = ranked[i].Value;
                    ReplyRaw(player, "StandingsLine", i + 1, e.Name, e.House != null ? " (" + e.House + ")" : "", e.Kills, e.Deaths);
                }
            }
        }

        // A scheduled tournament starts within the longest countdown.
        private bool TournamentSoon()
        {
            DateTime now = Now();
            int lead = config.CountdownMinutes.Count > 0 ? config.CountdownMinutes[0] : 60;
            foreach (ScheduleEntry e in config.Schedule)
            {
                if (e.Event != KTournament || !e.Enabled || !config.EnableTournament) continue;
                if (Occurrences(e, now, now.AddMinutes(lead)).Count > 0) return true;
            }
            return false;
        }

        [ChatCommand("hunt")]
        private void CmdHunt(Player player, string command, string[] args)
        {
            if (player == null || loadFailed) return;
            ActiveEvent h = Running(KHunt);
            if (h == null) { Reply(player, "NoHunt"); return; }
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "name")
            {
                if (!IsMonarch(player) && !IsAdmin(player)) { ReplyError(player, "NotMonarch"); return; }
                if (h.Quarry.Count >= config.HuntMaxTargets) { ReplyError(player, "QuarryFull", config.HuntMaxTargets); return; }
                Player target = FindOnline(JoinFrom(args, 1));
                if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
                if (target.Id == player.Id || IsMonarch(target)) { ReplyError(player, "QuarrySelf"); return; }
                string tid = target.Id.ToString();
                foreach (Quarry q in h.Quarry) if (q.Id == tid) { ReplyError(player, "QuarryAlready", target.Name); return; }
                string house = HouseOf(target.Id);
                if (config.HuntSkipProtectedPlayers && IsProtected(target)) { ReplyError(player, "QuarryProtected", target.Name); return; }
                if (config.HuntExcludeAllies && house != null)
                {
                    string monarch, crownHouse;
                    CurrentCrown(out monarch, out crownHouse);
                    if (crownHouse != null && Allied(crownHouse, house)) { ReplyError(player, "QuarryCrownSide", target.Name); return; }
                }
                h.Quarry.Add(new Quarry { Id = tid, Name = target.Name, House = house, Barred = SideMembers(house), Samples = 1, OnlineSamples = 1 });
                if (IsMonarch(player)) h.NamedBy = player.Name;
                SaveData();
                string text = Fmt("QuarryNamed", null, target.Name, house != null ? " of House " + house : "");
                Herald(text);
                Chronicle("decree", "The crown names " + target.Name + " quarry of the King's Hunt", text, new[] { player.Name, target.Name });
                return;
            }
            if (h.Quarry.Count == 0) { Reply(player, "QuarryNone"); return; }
            Reply(player, "QuarryHeader");
            foreach (Quarry q in h.Quarry)
                ReplyRaw(player, "QuarryLine", q.Name, q.House != null ? " (" + q.House + ")" : "",
                    q.ClaimedById == null ? Msg("QuarryOpen", player) : Fmt("QuarryTaken", player, q.ClaimedByName));
        }

        [ChatCommand("truce")]
        private void CmdTruce(Player player, string command, string[] args)
        {
            if (player == null || loadFailed) return;
            ActiveEvent t = Running(KTruce);
            if (t == null) { Reply(player, "NoTruce"); return; }
            string mode = TruceSuspended() ? Msg("TruceSuspended", player)
                : Msg(config.TruceEnforced ? "TruceEnforcedShort" : "TruceAnnouncedShort", player);
            Reply(player, "TruceActive", t.End.ToString("HH:mm"), mode);
        }

        #endregion

        #region Prizes (items)

        private void GrantPrize(string playerId, string playerName, Prize p, string reason)
        {
            if (FindItem(p.Item) == null)
            {
                PrintWarning("Prize item '" + p.Item + "' is unknown; " + reason + " prize for " + playerName + " skipped.");
                return;
            }
            AddOwed(playerId, playerName, p.Item, p.Amount, reason);
            SaveData();                                        // the ledger is written before any item moves
            Player online = OnlineById(playerId);
            if (online != null) PayOwed(online);
        }

        private void AddOwed(string playerId, string playerName, string item, int amount, string reason)
        {
            foreach (Owed o in data.Owed)
                if (o.PlayerId == playerId && string.Equals(o.Item, item, StringComparison.OrdinalIgnoreCase))
                {
                    o.Amount += amount;
                    o.Reason = reason;
                    return;
                }
            data.Owed.Add(new Owed { PlayerId = playerId, PlayerName = playerName, Item = item, Amount = amount, Reason = reason });
        }

        private bool HasOwed(string playerId)
        {
            foreach (Owed o in data.Owed) if (o.PlayerId == playerId) return true;
            return false;
        }

        // Pays what is owed, reducing each entry only by the measured amount delivered.
        private void PayOwed(Player player)
        {
            if (player == null || player.IsServer || player.Entity == null) return;
            string id = player.Id.ToString();
            foreach (Owed o in data.Owed.ToArray())
            {
                if (o.PlayerId != id) continue;
                InvItemBlueprint bp = FindItem(o.Item);
                if (bp == null) continue;
                int given = GiveItems(player, bp, o.Amount);
                if (given > 0)
                {
                    o.Amount -= given;
                    SaveData();
                    Reply(player, "PrizeGiven", given, bp.Name, o.Reason ?? "");
                }
                if (o.Amount > 0) ReplyError(player, "PrizeOwed", o.Amount, bp.Name);
            }
            if (data.Owed.RemoveAll(delegate(Owed o) { return o.Amount <= 0; }) > 0) SaveData();
        }

        private InvItemBlueprint FindItem(string name)
        {
            if (string.IsNullOrEmpty(name) || InvBlueprints.Instance == null) return null;
            return InvBlueprints.Instance.GetBlueprintForName(name, true, true);      // exact, case-insensitive [ASM; IL]
        }

        private static ItemCollection InventoryOf(Player player)
        {
            if (player == null || player.Entity == null) return null;
            Container inv = player.GetInventory();
            return inv != null ? inv.Contents : null;
        }

        // Returns the number of units actually added (measured); same as RealmContracts.GiveItems.
        private static int GiveItems(Player player, InvItemBlueprint bp, int amount)
        {
            ItemCollection items = InventoryOf(player);
            if (items == null || bp == null || amount <= 0) return 0;
            ContainerManagement cm = bp.TryGet<ContainerManagement>();
            int limit = cm != null && cm.StackLimit > 0 ? cm.StackLimit : amount;
            int given = 0;
            while (given < amount)
            {
                int chunk = Math.Min(limit, amount - given);
                int before = ItemCollection.AutoCount(items, bp);
                ItemCollection.AutoMergeAdd(items, new InvGameItemStack(bp, chunk, null));
                int added = ItemCollection.AutoCount(items, bp) - before;
                if (added <= 0) break;
                given += Math.Min(added, chunk);
                if (added < chunk) break;
            }
            return given;
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        private bool IsTruceActive()
        {
            ActiveEvent t = data != null ? Running(KTruce) : null;
            return t != null && Now() < t.End;
        }

        // Kinds of the events running now, e.g. ["crown_night"].
        private string[] GetActiveEvents()
        {
            var list = new List<string>();
            if (data == null) return list.ToArray();
            foreach (ActiveEvent a in data.Active) list.Add(a.Kind + "|" + Iso(a.Start) + "|" + Iso(a.End));
            return list.ToArray();
        }

        // Next scheduled event for RealmChronicle's RealmState.next: { "title": string, "at": DateTime (UTC) }, or null.
        private Dictionary<string, object> GetNextEvent()
        {
            if (loadFailed || config == null || config.Schedule == null) return null;
            DateTime now = Now();
            DateTime? best = null;
            string kind = null;
            foreach (ScheduleEntry e in config.Schedule)
            {
                if (!e.Enabled || !KindEnabled(e.Event)) continue;
                DateTime? next = NextStart(e, now);
                if (next.HasValue && (!best.HasValue || next.Value < best.Value)) { best = next; kind = e.Event; }
            }
            if (!best.HasValue) return null;
            var result = new Dictionary<string, object>();
            result["title"] = KindTitle(kind);
            result["at"] = DateTime.SpecifyKind(best.Value, DateTimeKind.Utc);
            return result;
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

        private void Award(string house, int points, string honour)
        {
            if (RealmSeasons == null || house == null || points == 0) return;
            RealmSeasons.Call("AwardHouse", house, points, honour);
        }

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            object r = RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
            // An older RealmChronicle without these types rejects them (returns 0); fall back to a decree line.
            // -1 is a duplicate or a line folded by the flood budget: never retried. An older chronicle returned 0 for a
            // duplicate, so a 0 for a type that was accepted before is left dropped too.
            if (r is int && (int)r > 0) chronicleTypeAccepted[type] = true;
            else if (r is int && (int)r == 0 && type != "decree" && !chronicleTypeAccepted.ContainsKey(type))
                RealmChronicle.Call("Log", "decree", title, detail, actors ?? new string[0]);
        }

        private void CurrentCrown(out string monarch, out string house)
        {
            monarch = null;
            house = null;
            if (CrownAndConsequences != null)
            {
                monarch = CrownAndConsequences.Call("GetKingName") as string;
                if (string.IsNullOrEmpty(monarch)) { monarch = null; return; }
                house = CrownAndConsequences.Call("GetKingHouse") as string;
                if (string.IsNullOrEmpty(house)) house = null;
                return;
            }
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            if (crown == null || !crown.HasKing()) return;
            monarch = crown.GetKingName();
            house = HouseOf(crown.GetKingID());
        }

        private bool IsMonarch(Player player)
        {
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            return crown != null && player != null && crown.IsKing(player);
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private string HouseOf(ulong playerId)
        {
            if (RealmHouses != null)
            {
                string h = RealmHouses.Call("GetHouse", playerId.ToString()) as string;
                return string.IsNullOrEmpty(h) ? null : h;
            }
            GuildScheme guilds = SocialAPI.Get<GuildScheme>();
            Guild g = guilds != null ? guilds.TryGetGuildByMember(playerId) : null;
            return g != null && !string.IsNullOrEmpty(g.Name) ? g.Name : null;
        }

        // Same house, liege or vassal of each other, or bound by a treaty. Without RealmHouses only the same name counts.
        private bool Allied(string a, string b)
        {
            if (a == null || b == null) return false;
            if (SameName(a, b)) return true;
            if (RealmHouses == null) return false;
            if (SameName(RealmHouses.Call("GetLiege", a) as string, b) || SameName(RealmHouses.Call("GetLiege", b) as string, a)) return true;
            object r = RealmHouses.Call("HasTreaty", a, b);
            return r is bool && (bool)r;
        }

        private static bool AnySame(string a1, string a2, string b1, string b2)
        {
            return SameName(a1, b1) || SameName(a1, b2) || SameName(a2, b1) || SameName(a2, b2);
        }

        private bool AnyAllied(string a1, string a2, string b1, string b2)
        {
            return Allied(a1, b1) || Allied(a1, b2) || Allied(a2, b1) || Allied(a2, b2);
        }

        // Player ids of a house and of every house allied with it (liege, vassals, treaty partners), as they are now.
        private List<string> SideMembers(string house)
        {
            var ids = new List<string>();
            if (house == null || RealmHouses == null) return ids;
            var side = new List<string> { house };
            string liege = RealmHouses.Call("GetLiege", house) as string;
            if (!string.IsNullOrEmpty(liege)) side.Add(liege);
            var vassals = RealmHouses.Call("GetVassals", house) as List<string>;
            if (vassals != null) side.AddRange(vassals);
            var all = RealmHouses.Call("GetHouseSummaries") as List<Dictionary<string, object>>;
            if (all != null)
                foreach (Dictionary<string, object> s in all)
                {
                    object n;
                    string other = s != null && s.TryGetValue("name", out n) ? n as string : null;
                    if (string.IsNullOrEmpty(other) || SameName(other, house)) continue;
                    object r = RealmHouses.Call("HasTreaty", house, other);
                    if (r is bool && (bool)r) side.Add(other);
                }
            foreach (string h in side)
            {
                var members = RealmHouses.Call("GetMembers", h) as List<string>;
                if (members == null) continue;
                foreach (string id in members) if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        // Counts, for each quarry still at large, whether they are online now (see HuntSurviveMinOnlinePercent).
        private void SampleQuarry(ActiveEvent a)
        {
            foreach (Quarry q in a.Quarry)
            {
                if (q.ClaimedById != null) continue;
                q.Samples++;
                if (OnlineById(q.Id) != null) q.OnlineSamples++;
            }
        }

        private bool IsProtected(Player p)
        {
            if (RealmWarden == null || p == null) return false;
            object r = RealmWarden.Call("IsNewPlayerProtected", p.Id);
            return r is bool && (bool)r;
        }

        private static ulong ParseId(string id)
        {
            ulong u;
            return ulong.TryParse(id, out u) ? u : 0UL;
        }

        private void NoticeThrottled(Player player, string key, params object[] args)
        {
            string k = player.Id + "|" + key;
            DateTime last;
            DateTime now = Now();
            if (lastNotice.TryGetValue(k, out last) && (now - last).TotalSeconds < 5) return;
            lastNotice[k] = now;
            Reply(player, key, args);
        }

        private static Player OnlineById(string id)
        {
            ulong u;
            if (!ulong.TryParse(id, out u)) return null;
            Player p = Server.GetPlayerById(u);
            return p != null && !p.IsServer ? p : null;
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

        private static bool SameName(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string JoinFrom(string[] args, int start)
        {
            return args == null || start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start);
        }

        private static bool TryParseHourMinute(string s, out TimeSpan at)
        {
            at = TimeSpan.Zero;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split(':');
            int h, m = 0;
            if (!int.TryParse(parts[0], out h) || h < 0 || h > 23) return false;
            if (parts.Length > 1 && (!int.TryParse(parts[1], out m) || m < 0 || m > 59)) return false;
            at = new TimeSpan(h, m, 0);
            return true;
        }

        private static string Until(TimeSpan span)
        {
            if (span.TotalMinutes < 60) return Math.Max(1, (int)Math.Ceiling(span.TotalMinutes)) + " min";
            if (span.TotalHours < 48) return (int)span.TotalHours + " h " + span.Minutes + " min";
            return (int)span.TotalDays + " days";
        }

        // Drops [RRGGBB] colour tags so chronicle text stays plain.
        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '[' && i + 7 < s.Length && s[i + 7] == ']') { i += 7; continue; }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string Iso(DateTime t)
        {
            return t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'");
        }

        #endregion
    }
}
