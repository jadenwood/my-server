// RealmArena: honourable combat in Ostreval. Duels by challenge, a ring no one else may enter, wagers held by the
// treasury, an Elo ladder with a weekly champion, team duels, bracket tournaments, trial by combat for the court, and
// two tavern games played in chat. Tags as in docs/oxide-rok-api.md ([OPJ] hook manifest, [ASM] metadata, [IL] IL
// bodies, [DEC] read in the decompiled patched Assembly-CSharp.dll to learn the behaviour; no game code is copied).
//
// The first fall, never a death. A duel ends when one side is felled, never by a killing blow:
//   OnEntityHealthChange(EntityDamageEvent) [OPJ L162] runs inside EntityHealth.InvokeDamage, which [DEC]
//   PlayerHealth.OnEntityDamage calls BEFORE it takes Damage.Amount off the hit region and calls Kill when the head or
//   the torso reaches 0 (a leg hit spills into the torso; a blow with no hit bone is spread over the three regions by
//   their MaxHealth). Every blow between duellists is judged first: if it would kill (or leave less than
//   YieldHealthPercent of full health), it is turned aside (evt.Cancel + Damage.Amount = 0 + return true, the pattern
//   RealmLaws and RealmEvents use [USE NoFriendlyFire.cs:116-117]) and the one it was aimed at is felled. No death means
//   no PlayerDeathEvent, so [DEC] CreateCorpseOnDeath never moves the packs into a corpse: duels never drop loot.
//   The judgement reads PlayerExtensions.GetHealth(Player) -> PlayerHealth (CurrentHealth, MaxHealth, HeadHealth,
//   TorsoHealth, LegsHealth: HealthRegion.CurrentHealth, MaxHealth) [ASM] and, by reflection because the compile check
//   has no UnityEngine.HumanBodyBones, Damage.HitBoxBone (field) and HealthRegion.Bones (property) [ASM]. The blow is
//   judged FatalMargin times harder (another plugin may change Damage.Amount after this one: Oxide does not order
//   plugins), and BearerMargin times more when the Ironbreaker's bearer is in it (RealmLegendary scales its blows).
//   Falls, fire and beasts that would kill a duellist in the fight fell them the same way. Fallback: if a duellist dies
//   anyway (OnEntityDeath [OPJ L188]), the duel is decided and a warning is logged; that death drops loot as any death
//   does (UNVERIFIED how often, if ever). PreventDeathFlag (off by default, UNVERIFIED) also sets PlayerHealth.PreventDeath
//   [ASM; DEC PlayerHealth.Kill returns false while it is set] on fighting duellists.
//   Afterwards both sides are tended (PlayerExtensions.Heal(Player, float) [ASM]) and shielded for ShieldSeconds.
// The ring. Duellists meet: the ring forms around them where they stand together (RingRadius), or in a configured arena
//   zone (circles on the X/Z plane, set by staff standing in the place, as RealmLaws' zones). Entity.Position [ASM]
//   is polled every second while a duel runs; outside the ring for LeaveRingSeconds is fleeing. Nobody outside the
//   duel can strike a duellist, and duellists can strike only their foes, from the countdown until the shield lapses;
//   ropes (OnPlayerCapture [OPJ L711]) and building in a ring (OnCubePlacement [OPJ L266], position from
//   CubePlaceEvent.Grid.LocalToWorldCoordinate as RealmLaws does) are refused. Optional, UNVERIFIED: Teleport brings
//   duellists into an arena and back with CharacterTeleport.Teleport(Vector3) [DEC: the call the game's own /tp makes;
//   RealmSentinel uses it] after asking RealmSentinel for movement grace (SentinelGrace).
// Wagers. Marks only, held in escrow by RealmTreasury (HoldMarks / PayFromHold / ReleaseHold, non-public): the
//   challenger's stake when the challenge is made, the other's when it is accepted. Nothing can be spent between
//   challenge and result. A win pays the whole pot to the winners (no fee); a draw or a void returns every stake.
//   Payouts and refunds are written down (Settlements) before the treasury is asked and retried until it answers.
// Ranked duels move an Elo rating; /arena top is the ladder; every week the best established fighter with enough
//   ranked duels is crowned Champion of the Ring: RealmRenown deed (title), RealmSeasons points for the house,
//   a Chronicle line (title_earned) and the herald. Arena tournaments (staff open them) are single-elimination
//   brackets with an optional entry fee paid out to the winners; during RealmEvents' Royal Tournament the ring also
//   runs a bracket of its entrants (GetTournamentEntrants), and each bracket win scores in the Royal Tournament
//   (ScoreTournamentDuel), because ring duels end without the death RealmEvents counts.
// Trial by combat. RealmLaws calls StageTrial: the accused and the crown's champion fight in the ring, and the winner
//   is reported back with ArenaTrialResult (guilty or acquitted). RealmLaws also asks IsDuelBlow so a peace law does
//   not stay a sanctioned blow.
// Tavern games. Hearth Dice (two dice each, high total takes the pot) and Twenty-One (both play at once, closest to
//   21 without going over). Both are player against player with equal stakes and symmetric rules: the house takes
//   nothing (house edge zero). Strict limits: stake bounds, a daily stake cap, a daily loss cap, games per pair per
//   day, a cooldown and a minimum time on the server. Dice and cards come from System.Security.Cryptography.RandomNumberGenerator.
// Anti-abuse. Win-trading with alts: per-pair ranked limits a day and a week, a rating factor that halves for each
//   repeat in the week, housemates and allies unranked, minimum play time, no contest for a quick fall where the loser
//   never struck, and an alert to RealmWarden for pairs that meet too often. Elo farming of new players: a newcomer's
//   rating is provisional (hidden from the ladder), beating one is worth a quarter, and a daily gain cap. Wager scams:
//   escrow, the amount shown in the window and required in the chat answer, one question per challenge token, no
//   re-challenge of the same player for RechallengeSeconds, caps a day and per pair. Leaving to avoid a loss: logging
//   off or leaving the ring in the fight forfeits; repeated fleeing bars the player from the ring for a while.
//   No duels for players under RealmWarden's new-player protection (their blows could not be answered), during
//   RealmEvents' Truce of the Realm (it blocks every blow), for RealmSentinel's frozen players, or straight after a
//   fight outside the ring (CombatTagSeconds: no escaping a fight into a duel).
//
// Data: oxide/data/RealmArena.json. If it exists but cannot be read (a cut-off or damaged file) the plugin refuses to
// run and never writes it; fix it or restore RealmArena_lastgood.json, then reload.
// Admin: /arena admin ... (realmarena.admin). Every switch is in oxide/config/RealmArena.json.
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmArena.md for the in-game test steps.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodeHatch.Blocks.Networking.Events;            // CubePlaceEvent [ASM]
using CodeHatch.Common;                              // PlayerExtensions: SendMessage, SendError, GetHealth, Heal [ASM]
using CodeHatch.Damaging;                            // Damage [ASM]
using CodeHatch.Engine.Behaviours;                   // CharacterTeleport [DEC]
using CodeHatch.Engine.Core.Cache;                   // Entity [ASM]
using CodeHatch.Engine.Entities.Definitions;         // PlayerHealth, HealthRegion [ASM]
using CodeHatch.Engine.Networking;                   // Player, Server [ASM]
using CodeHatch.Networking.Events;                   // PlayerCaptureEvent [ASM]
using CodeHatch.Networking.Events.Entities;          // EntityDamageEvent, EntityDeathEvent [ASM]
using CodeHatch.UserInterface.Dialogues;             // Dialogue.OnSubmit, Options (popup answers) [ASM]
using Oxide.Core;                                    // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                            // Plugin (for [PluginReference]) [SRC]
using UnityEngine;                                   // Vector3 (Entity.Position) [ASM]

namespace Oxide.Plugins
{
    [Info("RealmArena", "Realm", "0.1.0")]
    [Description("Honourable combat: duels in a ring with wagers held by the treasury, an Elo ladder and weekly champion, team duels, bracket tournaments, trial by combat and tavern games")]
    public class RealmArena : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmTreasury;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin RealmHerald;
        [PluginReference] private Plugin RealmWarden;
        [PluginReference] private Plugin RealmEvents;
        [PluginReference] private Plugin RealmLaws;
        [PluginReference] private Plugin RealmLegendary;
        [PluginReference] private Plugin RealmSentinel;

        private const string PermAdmin = "realmarena.admin";
        private const string DataName = "RealmArena";
        private const string BackupName = "RealmArena_lastgood";
        private const string HoldSource = "RealmArena";        // the treasury only lets this source move its holds

        private const string KDuel = "duel";
        private const string KTeam = "team";
        private const string KTrial = "trial";
        private const string KBracket = "bracket";
        private const string KDice = "dice";
        private const string KCards = "cards";

        private const string SGather = "gather";
        private const string SCountdown = "countdown";
        private const string SFight = "fight";

        private const string TSignup = "signup";
        private const string TRunning = "running";
        private const string TArena = "arena";
        private const string TRoyal = "royal";

        private const string MWait = "wait";
        private const string MCalled = "called";
        private const string MDone = "done";

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private bool dirty;
        private bool shuttingDown;
        private Timer slowTimer;
        private Timer fastTimer;
        private Func<DateTime> clock = DefaultClock;           // replaced only by the offline behaviour tests
        private Func<int, int> rng = CryptoNext;               // uniform 0..n-1; replaced only by the offline tests

        // In memory only: losing them on a reload is harmless.
        private readonly Dictionary<string, DateTime> shieldUntil = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> lastPvp = new Dictionary<string, DateTime>();       // fights outside the ring
        private readonly Dictionary<string, DateTime> cooldowns = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> lastNotice = new Dictionary<string, DateTime>();
        private readonly Dictionary<ulong, bool> deathGuarded = new Dictionary<ulong, bool>();              // PreventDeath set by us
        private readonly Queue<DateTime> heraldTimes = new Queue<DateTime>();
        private bool truceSeen;

        #region Config

        private class Zone
        {
            public string Name;
            public float X;
            public float Y;
            public float Z;
            public float Radius = 20f;
        }

        private class DuelSettings
        {
            public bool Enabled = true;
            public int ChallengeSeconds = 60;
            public int GatherSeconds = 90;              // after acceptance: time to stand together (or in the arena)
            public int CountdownSeconds = 5;
            public int MaxDuelMinutes = 5;              // then a draw: stakes back, no rating change
            public float RingRadius = 15f;              // metres; the ring forms where the duellists meet
            public int LeaveRingSeconds = 8;            // outside the ring this long in the fight is fleeing
            public float YieldHealthPercent = 5f;       // a blow that would leave less than this share of full health fells
            public float FatalMargin = 1.25f;           // a blow is judged this much harder than it is
            public float BearerMargin = 2f;             // and this much again with the Ironbreaker's bearer in the blow
            public bool PreventDeathFlag = false;       // UNVERIFIED: also set PlayerHealth.PreventDeath while fighting
            public float HealAfterPercent = 50f;        // both sides are tended after a fight (share of full health)
            public int ShieldSeconds = 15;              // after a fight no one strikes, or is struck by, the duellists
            public int ChallengeCooldownSeconds = 15;
            public int RechallengeSeconds = 60;         // the same two players again after a decline, cancel or lapse
            public int MaxOpenChallenges = 3;
            public int CombatTagSeconds = 30;           // no challenge or acceptance this soon after a fight outside the ring
            public int FleeBanCount = 3;                // fleeing this many times in 24 h ...
            public int FleeBanHours = 12;               // ... bars the player from the ring this long
            public bool RequireArena = false;           // duels only inside a configured arena zone
            public bool Teleport = false;               // UNVERIFIED: into the arena on acceptance, back afterwards
            public long AnnounceMinWager = 100;         // heralded when the stake is this high (or the champion fights)
            public int HeraldsPerHour = 6;
            public bool BlockBuildingInRing = true;
        }

        private class RankedSettings
        {
            public bool Enabled = true;
            public int StartRating = 1000;
            public int KProvisional = 40;
            public int K = 24;
            public int KHigh = 16;
            public int HighRating = 1600;
            public int ProvisionalGames = 10;           // a rating is provisional (off the ladder) until this many ranked duels
            public int MinRating = 100;
            public int MinPlayMinutes = 120;            // time on the server before duels are ranked
            public int PairPerDay = 2;                  // ranked duels between the same two players per UTC day
            public int PairPerWeek = 6;                 // and per 7 days
            public float PairRepeatFactor = 0.5f;       // the rating change is multiplied by this for each repeat in 7 days
            public float ProvisionalOpponentFactor = 0.25f;   // an established fighter beating a provisional one
            public int MaxGainPerDay = 120;
            public int MinDuelSeconds = 15;             // a quicker fall where the loser never struck is no contest
            public bool SameHouseRanked = false;
            public bool AlliesRanked = false;           // liege, vassal or treaty partner (RealmHouses)
            public bool BearerRanked = false;           // duels with the Ironbreaker's bearer (RealmLegendary)
            public int WinTradeAlertGames = 6;          // ranked duels between one pair in 7 days that alert RealmWarden
            public int LadderSize = 10;
            public int LadderActiveDays = 28;
            public string RenownDeed = "duel_won";      // RealmRenown AddDeed for a ranked win that moved the ladder
        }

        private class WagerSettings
        {
            public bool Enabled = true;
            public long Min = 5;
            public long Max = 500;
            public long DailyLimit = 2000;              // marks a player may stake on duels per UTC day
            public long PairDailyLimit = 1000;          // marks staked between the same two players per UTC day
            public int MinPlayMinutes = 60;
        }

        private class TeamSettings
        {
            public bool Enabled = true;
            public int MaxSize = 3;                     // 2v2 and 3v3
            public bool Ranked = true;                  // the team rating
            public float RingRadius = 25f;
            public int ChallengeSeconds = 90;
        }

        private class ChampionSettings
        {
            public bool Enabled = true;
            public string Day = "Sunday";               // UTC
            public int HourUtc = 20;
            public int MinWeeklyGames = 3;              // ranked duels in the week to be crowned
            public string RenownDeed = "arena_champion";
            public int HousePoints = 10;                // RealmSeasons points for the champion's house
            public bool Chronicle = true;
            public bool Popup = true;
        }

        private class TournamentSettings
        {
            public bool Enabled = true;
            public long MaxEntryFee = 200;
            public List<int> PrizeSplit;                // percent of the pot for 1st, 2nd (the rest goes to 1st)
            public int MinEntrants = 4;
            public int MaxEntrants = 32;
            public int SignupMinutes = 10;
            public int MatchMinutes = 6;                // to meet in the ring and fight it out
            public bool Ranked = false;
            public bool RoyalBracket = true;            // a bracket of the Royal Tournament's entrants while it runs
            public int RoyalSeedMinutes = 5;            // after the Royal Tournament begins
            public int ChampionHousePoints = 15;
            public int RunnerUpHousePoints = 8;
            public string RenownDeed = "arena_tourney";
            public bool Chronicle = true;
        }

        private class TavernSettings
        {
            public bool Enabled = true;
            public bool Dice = true;
            public bool Cards = true;
            public long MinStake = 1;
            public long MaxStake = 100;
            public long DailyStakeLimit = 500;          // marks staked at tavern games per UTC day
            public long DailyLossLimit = 250;           // net marks lost per UTC day; no game may risk more
            public int PairGamesPerDay = 10;
            public int CooldownSeconds = 10;
            public int MinPlayMinutes = 60;
            public int ChallengeSeconds = 60;
            public int CardTurnSeconds = 45;            // then the hand stands
            public bool RequireTavernZone = false;      // games only inside a configured tavern zone
            public float RollRadius = 30f;              // /dice roll is told to players this close
        }

        private class TrialSettings
        {
            public bool Enabled = true;                 // RealmLaws may stage trials by combat in the ring
        }

        private class PluginConfig
        {
            public bool Enabled = true;
            public bool UsePopups = true;
            public float TickSeconds = 10f;             // the slow tick; ring checks run every second while a duel is on
            public int MaxFighters = 5000;
            public int HistoryKept = 100;
            public DuelSettings Duels;
            public RankedSettings Ranked;
            public WagerSettings Wagers;
            public TeamSettings Teams;
            public ChampionSettings Champion;
            public TournamentSettings Tournament;
            public TavernSettings Tavern;
            public TrialSettings Trials;
            public List<Zone> Arenas;
            public List<Zone> Taverns;
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.Duels = new DuelSettings();
            c.Ranked = new RankedSettings();
            c.Wagers = new WagerSettings();
            c.Teams = new TeamSettings();
            c.Champion = new ChampionSettings();
            c.Tournament = new TournamentSettings();
            c.Tournament.PrizeSplit = new List<int> { 70, 30 };
            c.Tavern = new TavernSettings();
            c.Trials = new TrialSettings();
            c.Arenas = new List<Zone>();
            c.Taverns = new List<Zone>();
            return c;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static long ClampL(long v, long lo, long hi) { return v < lo ? lo : (v > hi ? hi : v); }
        private static float ClampF(float v, float lo, float hi) { return float.IsNaN(v) ? lo : (v < lo ? lo : (v > hi ? hi : v)); }

        private void ClampConfig()
        {
            PluginConfig d = DefaultConfig();
            if (config.Duels == null) config.Duels = d.Duels;
            if (config.Ranked == null) config.Ranked = d.Ranked;
            if (config.Wagers == null) config.Wagers = d.Wagers;
            if (config.Teams == null) config.Teams = d.Teams;
            if (config.Champion == null) config.Champion = d.Champion;
            if (config.Tournament == null) config.Tournament = d.Tournament;
            if (config.Tavern == null) config.Tavern = d.Tavern;
            if (config.Trials == null) config.Trials = d.Trials;
            if (config.Arenas == null) config.Arenas = new List<Zone>();
            if (config.Taverns == null) config.Taverns = new List<Zone>();
            config.Arenas.RemoveAll(delegate(Zone z) { return z == null || string.IsNullOrEmpty(z.Name); });
            config.Taverns.RemoveAll(delegate(Zone z) { return z == null || string.IsNullOrEmpty(z.Name); });
            foreach (Zone z in config.Arenas) z.Radius = ClampF(z.Radius, 5f, 200f);
            foreach (Zone z in config.Taverns) z.Radius = ClampF(z.Radius, 2f, 200f);
            config.TickSeconds = ClampF(config.TickSeconds, 2f, 60f);
            config.MaxFighters = Clamp(config.MaxFighters, 50, 100000);
            config.HistoryKept = Clamp(config.HistoryKept, 10, 1000);

            DuelSettings u = config.Duels;
            u.ChallengeSeconds = Clamp(u.ChallengeSeconds, 15, 600);
            u.GatherSeconds = Clamp(u.GatherSeconds, 15, 900);
            u.CountdownSeconds = Clamp(u.CountdownSeconds, 0, 30);
            u.MaxDuelMinutes = Clamp(u.MaxDuelMinutes, 1, 60);
            u.RingRadius = ClampF(u.RingRadius, 4f, 100f);
            u.LeaveRingSeconds = Clamp(u.LeaveRingSeconds, 2, 120);
            u.YieldHealthPercent = ClampF(u.YieldHealthPercent, 0f, 50f);
            u.FatalMargin = ClampF(u.FatalMargin, 1f, 5f);
            u.BearerMargin = ClampF(u.BearerMargin, 1f, 10f);
            u.HealAfterPercent = ClampF(u.HealAfterPercent, 0f, 100f);
            u.ShieldSeconds = Clamp(u.ShieldSeconds, 0, 120);
            u.ChallengeCooldownSeconds = Clamp(u.ChallengeCooldownSeconds, 0, 600);
            u.RechallengeSeconds = Clamp(u.RechallengeSeconds, 0, 3600);
            u.MaxOpenChallenges = Clamp(u.MaxOpenChallenges, 1, 10);
            u.CombatTagSeconds = Clamp(u.CombatTagSeconds, 0, 600);
            u.FleeBanCount = Clamp(u.FleeBanCount, 1, 100);
            u.FleeBanHours = Clamp(u.FleeBanHours, 0, 720);
            u.AnnounceMinWager = ClampL(u.AnnounceMinWager, 0, 1000000);
            u.HeraldsPerHour = Clamp(u.HeraldsPerHour, 0, 120);

            RankedSettings r = config.Ranked;
            r.StartRating = Clamp(r.StartRating, 100, 3000);
            r.KProvisional = Clamp(r.KProvisional, 1, 100);
            r.K = Clamp(r.K, 1, 100);
            r.KHigh = Clamp(r.KHigh, 1, 100);
            r.HighRating = Clamp(r.HighRating, 100, 5000);
            r.ProvisionalGames = Clamp(r.ProvisionalGames, 0, 100);
            r.MinRating = Clamp(r.MinRating, 0, r.StartRating);
            r.MinPlayMinutes = Clamp(r.MinPlayMinutes, 0, 100000);
            r.PairPerDay = Clamp(r.PairPerDay, 0, 100);
            r.PairPerWeek = Clamp(r.PairPerWeek, 0, 700);
            r.PairRepeatFactor = ClampF(r.PairRepeatFactor, 0f, 1f);
            r.ProvisionalOpponentFactor = ClampF(r.ProvisionalOpponentFactor, 0f, 1f);
            r.MaxGainPerDay = Clamp(r.MaxGainPerDay, 0, 10000);
            r.MinDuelSeconds = Clamp(r.MinDuelSeconds, 0, 600);
            r.WinTradeAlertGames = Clamp(r.WinTradeAlertGames, 2, 1000);
            r.LadderSize = Clamp(r.LadderSize, 3, 25);
            r.LadderActiveDays = Clamp(r.LadderActiveDays, 1, 3650);

            WagerSettings w = config.Wagers;
            w.Min = ClampL(w.Min, 1, 1000000);
            w.Max = ClampL(w.Max, w.Min, 1000000);
            w.DailyLimit = ClampL(w.DailyLimit, 0, 100000000);
            w.PairDailyLimit = ClampL(w.PairDailyLimit, 0, 100000000);
            w.MinPlayMinutes = Clamp(w.MinPlayMinutes, 0, 100000);

            TeamSettings t = config.Teams;
            t.MaxSize = Clamp(t.MaxSize, 2, 3);
            t.RingRadius = ClampF(t.RingRadius, 6f, 150f);
            t.ChallengeSeconds = Clamp(t.ChallengeSeconds, 15, 900);

            ChampionSettings c = config.Champion;
            if (ParseDay(c.Day) < 0) c.Day = "Sunday";
            c.HourUtc = Clamp(c.HourUtc, 0, 23);
            c.MinWeeklyGames = Clamp(c.MinWeeklyGames, 1, 1000);
            c.HousePoints = Clamp(c.HousePoints, 0, 1000);

            TournamentSettings o = config.Tournament;
            o.MaxEntryFee = ClampL(o.MaxEntryFee, 0, 1000000);
            if (o.PrizeSplit == null || o.PrizeSplit.Count == 0) o.PrizeSplit = new List<int> { 70, 30 };
            int sum = 0;
            for (int i = 0; i < o.PrizeSplit.Count; i++) { o.PrizeSplit[i] = Clamp(o.PrizeSplit[i], 0, 100); sum += o.PrizeSplit[i]; }
            if (o.PrizeSplit.Count > 2 || sum > 100) o.PrizeSplit = new List<int> { 70, 30 };
            o.MinEntrants = Clamp(o.MinEntrants, 2, 64);
            o.MaxEntrants = Clamp(o.MaxEntrants, o.MinEntrants, 64);
            o.SignupMinutes = Clamp(o.SignupMinutes, 1, 240);
            o.MatchMinutes = Clamp(o.MatchMinutes, 2, 60);
            o.RoyalSeedMinutes = Clamp(o.RoyalSeedMinutes, 0, 120);
            o.ChampionHousePoints = Clamp(o.ChampionHousePoints, 0, 1000);
            o.RunnerUpHousePoints = Clamp(o.RunnerUpHousePoints, 0, 1000);

            TavernSettings v = config.Tavern;
            v.MinStake = ClampL(v.MinStake, 1, 1000000);
            v.MaxStake = ClampL(v.MaxStake, v.MinStake, 1000000);
            v.DailyStakeLimit = ClampL(v.DailyStakeLimit, 0, 100000000);
            v.DailyLossLimit = ClampL(v.DailyLossLimit, 0, 100000000);
            v.PairGamesPerDay = Clamp(v.PairGamesPerDay, 0, 1000);
            v.CooldownSeconds = Clamp(v.CooldownSeconds, 0, 3600);
            v.MinPlayMinutes = Clamp(v.MinPlayMinutes, 0, 100000);
            v.ChallengeSeconds = Clamp(v.ChallengeSeconds, 15, 600);
            v.CardTurnSeconds = Clamp(v.CardTurnSeconds, 10, 600);
            v.RollRadius = ClampF(v.RollRadius, 0f, 200f);
        }

        #endregion

        #region Data

        private class Fighter
        {
            public string Name;
            public int Rating;                          // one against one
            public int Games;                           // ranked one-against-one duels
            public int Wins;
            public int Losses;
            public int Draws;
            public int Friendly;                        // unranked duels fought
            public int TeamRating;
            public int TeamGames;
            public int TeamWins;
            public int TeamLosses;
            public int Streak;
            public int BestStreak;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public DateTime LastRanked;
            public double PlayedMinutes;                // counted by this plugin while the player is online
            public string WeekKey;                      // the crowning this week's duels count towards
            public int WeekGames;
            public int WeekWins;
            public string DayKey;                       // UTC day of the daily counters below
            public int GainToday;
            public long WagerToday;
            public long TavernStaked;
            public long TavernWon;
            public long TavernLost;
            public int Championships;
            public int TourneyWins;
            public long MarksWon;                       // lifetime, from duel wagers
            public long MarksLost;
            public List<DateTime> Flees = new List<DateTime>();
            public DateTime BarredUntil;
            public bool ChallengesOff;
        }

        private class PairRec
        {
            public List<DateTime> Ranked = new List<DateTime>();     // ranked duels between the two (7 days)
            public List<string> Winners = new List<string>();        // winner id of each, same order
            public string DayKey;
            public long Wagered;                        // marks staked between them on DayKey (duels)
            public int TavernGames;                     // tavern games between them on DayKey
            public DateTime LastAlert;
            public DateTime LastUsed;
        }

        private class Member
        {
            public string Id;
            public string Name;
            public int Side;                            // 0 or 1
            public bool Accepted;
            public bool Out;
            public string OutHow;                       // felled | fell | yielded | fled | died
            public float Dealt;                         // damage dealt to foes in the fight
            public string HoldId;                       // treasury hold of this member's stake
            public long Held;
            public bool HasReturn;
            public float RX;
            public float RY;
            public float RZ;
            public DateTime OutsideSince;
            public DateTime LastWarn;
        }

        private class Duel
        {
            public int Id;
            public string Kind;                         // duel | team | trial | bracket
            public string State;                        // gather | countdown | fight
            public List<Member> Members = new List<Member>();
            public long Wager;                          // per head
            public bool Ranked;
            public string Unranked;                     // lang key of the reason it is not ranked
            public string Arena;                        // arena zone name, or null for a ring where they meet
            public bool HasCentre;
            public float CX;
            public float CY;
            public float CZ;
            public float Radius;
            public DateTime Created;
            public DateTime GatherEnds;
            public DateTime FightAt;
            public DateTime EndsAt;
            public int LastCount;
            public string TrialCase;
            public int TourneyId;
            public int MatchId;
            public bool Teleported;
        }

        private class Challenge
        {
            public int Id;
            public string Kind;                         // duel | team | dice | cards
            public List<Member> Members = new List<Member>();          // [0] made the challenge
            public long Wager;                          // per head (duels) or stake (tavern)
            public DateTime Created;
            public DateTime Expires;
        }

        private class Transfer
        {
            public string HoldId;
            public string ToId;
            public string ToName;
            public long Amount;
            public bool Done;
        }

        private class Settlement
        {
            public string Ref;                          // what it settles, for the log
            public List<Transfer> Transfers = new List<Transfer>();
            public List<string> Releases = new List<string>();
            public int Tries;
            public DateTime Created;
        }

        private class ResultRec
        {
            public DateTime At;
            public int DuelId;
            public string Kind;
            public string Winners;
            public string Losers;
            public string How;
            public long Wager;
            public int Change;
            public bool Ranked;
        }

        private class ChampionRec
        {
            public string Id;
            public string Name;
            public string Week;
            public int Rating;
            public int WeekWins;
            public DateTime At;
        }

        private class TavernGame
        {
            public int Id;
            public string AId;
            public string AName;
            public string BId;
            public string BName;
            public long Stake;
            public string HoldA;
            public string HoldB;
            public List<int> Deck = new List<int>();
            public List<int> HandA = new List<int>();
            public List<int> HandB = new List<int>();
            public bool DoneA;
            public bool DoneB;
            public DateTime TurnEndsA;
            public DateTime TurnEndsB;
            public DateTime Started;
        }

        private class TEntrant
        {
            public string Id;
            public string Name;
            public int Rating;
            public int Seed;
            public bool Out;
            public string HoldId;
            public long Paid;
        }

        private class Match
        {
            public int Id;
            public int Round;
            public string AId;
            public string AName;
            public string BId;                          // null: a bye
            public string BName;
            public string WinnerId;
            public string State;                        // wait | called | done
            public DateTime Deadline;
            public int DuelId;
            public string Note;
        }

        private class Tourney
        {
            public int Id;
            public string Kind;                         // arena | royal
            public string State;                        // signup | running
            public DateTime Opened;
            public DateTime SignupEnds;
            public long Fee;
            public string OpenedBy;
            public string RoyalKey;
            public DateTime RoyalEnds;
            public List<TEntrant> Entrants = new List<TEntrant>();
            public int Round;
            public List<Match> Matches = new List<Match>();
            public int NextMatchId = 1;
        }

        private class StoredData
        {
            public int Version = 1;
            public int NextId = 1;                      // duels, challenges, games and tournaments share it
            public Dictionary<string, Fighter> Fighters = new Dictionary<string, Fighter>();
            public Dictionary<string, PairRec> Pairs = new Dictionary<string, PairRec>();
            public List<Duel> Duels = new List<Duel>();
            public List<Challenge> Challenges = new List<Challenge>();
            public List<TavernGame> Games = new List<TavernGame>();
            public List<Settlement> Settlements = new List<Settlement>();
            public List<ResultRec> History = new List<ResultRec>();
            public ChampionRec Champion;
            public List<ChampionRec> Champions = new List<ChampionRec>();
            public DateTime NextCrowning;
            public Tourney Tourney;
            public List<string> TourneyHistory = new List<string>();
            public string LastRoyalKey;
            public long PotPaid;                        // lifetime marks paid out of stakes (for /arena admin status)
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
        }

        private void Normalize()
        {
            if (data.Fighters == null) data.Fighters = new Dictionary<string, Fighter>();
            if (data.Pairs == null) data.Pairs = new Dictionary<string, PairRec>();
            if (data.Duels == null) data.Duels = new List<Duel>();
            if (data.Challenges == null) data.Challenges = new List<Challenge>();
            if (data.Games == null) data.Games = new List<TavernGame>();
            if (data.Settlements == null) data.Settlements = new List<Settlement>();
            if (data.History == null) data.History = new List<ResultRec>();
            if (data.Champions == null) data.Champions = new List<ChampionRec>();
            if (data.TourneyHistory == null) data.TourneyHistory = new List<string>();
            var clean = new Dictionary<string, Fighter>();
            foreach (KeyValuePair<string, Fighter> kv in data.Fighters)
            {
                if (kv.Value == null || !IsSteamId(kv.Key)) continue;
                Fighter f = kv.Value;
                if (f.Flees == null) f.Flees = new List<DateTime>();
                if (f.Rating <= 0) f.Rating = config.Ranked.StartRating;
                if (f.TeamRating <= 0) f.TeamRating = config.Ranked.StartRating;
                if (f.Name == null) f.Name = kv.Key;
                clean[kv.Key] = f;
            }
            data.Fighters = clean;
            var pairs = new Dictionary<string, PairRec>();
            foreach (KeyValuePair<string, PairRec> kv in data.Pairs)
            {
                if (kv.Value == null || kv.Key == null) continue;
                if (kv.Value.Ranked == null) kv.Value.Ranked = new List<DateTime>();
                if (kv.Value.Winners == null) kv.Value.Winners = new List<string>();
                while (kv.Value.Winners.Count < kv.Value.Ranked.Count) kv.Value.Winners.Add("");
                pairs[kv.Key] = kv.Value;
            }
            data.Pairs = pairs;
            data.Duels.RemoveAll(delegate(Duel d) { return d == null || d.Members == null; });
            data.Challenges.RemoveAll(delegate(Challenge c) { return c == null || c.Members == null || c.Members.Count < 2; });
            data.Games.RemoveAll(delegate(TavernGame g) { return g == null || g.AId == null || g.BId == null; });
            data.Settlements.RemoveAll(delegate(Settlement s) { return s == null; });
            foreach (Settlement s in data.Settlements)
            {
                if (s.Transfers == null) s.Transfers = new List<Transfer>();
                if (s.Releases == null) s.Releases = new List<string>();
                s.Transfers.RemoveAll(delegate(Transfer t) { return t == null || t.HoldId == null; });
            }
            data.History.RemoveAll(delegate(ResultRec r) { return r == null; });
            data.Champions.RemoveAll(delegate(ChampionRec r) { return r == null || r.Id == null; });
            if (data.Tourney != null)
            {
                if (data.Tourney.Entrants == null) data.Tourney.Entrants = new List<TEntrant>();
                if (data.Tourney.Matches == null) data.Tourney.Matches = new List<Match>();
                data.Tourney.Entrants.RemoveAll(delegate(TEntrant e) { return e == null || e.Id == null; });
                data.Tourney.Matches.RemoveAll(delegate(Match m) { return m == null; });
            }
            int max = data.NextId;
            foreach (Duel d in data.Duels) if (d.Id >= max) max = d.Id + 1;
            foreach (Challenge c in data.Challenges) if (c.Id >= max) max = c.Id + 1;
            foreach (TavernGame g in data.Games) if (g.Id >= max) max = g.Id + 1;
            if (data.Tourney != null && data.Tourney.Id >= max) max = data.Tourney.Id + 1;
            data.NextId = Math.Max(1, max);
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

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Arena" },
                { "TavernSpeaker", "Tavern" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Raw", "{0}" },
                { "Paused", "The arena is paused: oxide/data/RealmArena.json could not be read. Staff have been told in the server log." },
                { "ArenaOff", "The arena is closed on this server." },
                { "Failed", "Something went wrong; nothing was changed. Tell the staff if it happens again." },
                { "NoPermission", "You may not do that." },
                { "PlayerNotFound", "No one called '{0}' is in the realm (or the name fits several)." },
                { "FighterNotFound", "No fighter called '{0}' is on record." },
                { "NotYourself", "You cannot challenge yourself." },
                { "Yes", "yes" },
                { "No", "no" },
                { "None", "none" },
                { "TheRing", "the ring" },

                // /duel help
                { "DuelHelp1", "Duels to the first fall: no one dies and nothing is looted. Only the stake changes hands." },
                { "DuelHelp2", "  [F4C96D]/duel[FFFFFF] <player> [marks] challenges | [F4C96D]/duel accept[FFFFFF] [player] [marks] | [F4C96D]/duel decline[FFFFFF] [player]" },
                { "DuelHelp3", "  [F4C96D]/duel 2v2[FFFFFF] <ally> <foe> <foe> [marks] | [F4C96D]/duel 3v3[FFFFFF] <ally> <ally> <foe> <foe> <foe> [marks]" },
                { "DuelHelp4", "  [F4C96D]/duel yield[FFFFFF] | [F4C96D]/duel cancel[FFFFFF] | [F4C96D]/duel status[FFFFFF] | [F4C96D]/duel off[FFFFFF] refuses challenges, [F4C96D]/duel on[FFFFFF] takes them again" },
                { "DuelHelp5", "  [F4C96D]/arena[FFFFFF] your rating, the ladder, the champion and the Lists | [F4C96D]/dice[FFFFFF] and [F4C96D]/cards[FFFFFF] at the tavern" },

                // refusals (duels): {0} name, {1} barred until, {2} min stake, {3} max stake, {4} minutes, {5} daily, {6} pair daily
                { "DuelsOff", "Duels are not allowed on this server." },
                { "TeamsOff", "Team duels are not allowed on this server." },
                { "TruceHolds", "The Truce of the Realm holds: the ring is closed until it ends." },
                { "ChallengeCooldown", "Wait {0} s before your next challenge." },
                { "TooManyChallenges", "You already have {0} challenges waiting for an answer. [F4C96D]/duel cancel[FFFFFF] withdraws them." },
                { "NoArenas", "Duels are fought only in an arena here, and none is set up yet." },
                { "NeedArena", "Duels are fought only in an arena here. [F4C96D]/arena zones[FFFFFF] lists them." },
                { "TheyRefuse", "{0} is not taking challenges." },
                { "RechallengeWait", "You challenged {0} a moment ago. Wait {1} s." },
                { "AlreadyChallenged", "A challenge between you and {0} is already waiting." },
                { "YouBarred", "You are barred from the ring until {1}." },
                { "TheyBarred", "{0} is barred from the ring for now." },
                { "YouInDuel", "You are already in a duel." },
                { "TheyInDuel", "{0} is already in a duel." },
                { "YouAtTable", "Finish your game at the tavern table first." },
                { "TheyAtTable", "{0} is at a tavern table." },
                { "YouDown", "You must be on your feet to fight." },
                { "TheyDown", "{0} is not on their feet." },
                { "YouProtected", "You are under the Warden's new-player protection, so no one could strike back. [F4C96D]/warden[FFFFFF] protection off confirm gives it up." },
                { "TheyProtected", "{0} is under the Warden's new-player protection and cannot duel yet." },
                { "YouFrozen", "The Sentinel holds you; you cannot duel now." },
                { "TheyFrozen", "{0} cannot duel now." },
                { "YouInCombat", "You were in a fight a moment ago. Wait a little before you step into the ring." },
                { "TheyInCombat", "{0} was in a fight a moment ago. Try again shortly." },
                { "YouInTourney", "You are in the running bracket: your next match will be called." },
                { "TheyInTourney", "{0} is in the running bracket." },
                { "WagersOff", "Stakes are not allowed on this server; challenge without one." },
                { "WagerBounds", "A stake must be between {2} and {3} marks." },
                { "WagerNew", "You may stake marks once you have spent {4} minutes in the realm." },
                { "WagerDaily", "That would take you over today's limit of {5} marks staked on duels." },
                { "WagerPair", "You two have staked {6} marks on each other today; that is the limit." },
                { "WagerPurse", "You do not have that many marks. [F4C96D]/purse[FFFFFF] shows your purse." },
                { "TheyCannotStake", "{0} cannot stake that much now." },
                { "TreasuryClosed", "The treasury is closed (RealmTreasury is not running), so no stake can be held." },
                { "HoldFailed", "The treasury could not hold the stake. Nothing was taken." },
                { "Team2Usage", "Usage: [F4C96D]/duel 2v2[FFFFFF] <ally> <foe> <foe> [marks]" },
                { "Team3Usage", "Usage: [F4C96D]/duel 3v3[FFFFFF] <ally> <ally> <foe> <foe> <foe> [marks]" },
                { "TeamTwice", "{0} is named twice." },

                // challenges
                { "ChallengeSent", "You challenge {0} to a duel. They have {2} s to answer." },
                { "ChallengeSentWager", "You challenge {0} to a duel for {1} marks each; your stake is held by the treasury. They have {2} s to answer." },
                { "TeamSent", "You challenge {1}: {0} against them. Every one of them must accept within {3} s." },
                { "ChallengeIn", "{0} challenges you to a duel to the first fall!" },
                { "ChallengeInWager", "{0} challenges you to a duel to the first fall, for {1} marks each!" },
                { "TeamIn", "{0} calls a team duel: {2} against {3}!" },
                { "TeamInWager", "{0} calls a team duel for {1} marks a head: {2} against {3}!" },
                { "RankLine", "  {0}" },
                { "RankedYes", "Ranked: the result moves the ladder." },
                { "RankedNo", "A friendly bout, off the ladder: {0}" },
                { "RankOff", "ranked duels are off." },
                { "RankTrial", "a trial by combat." },
                { "RankBracket", "a tournament match." },
                { "RankNew", "a fighter is too new to the realm." },
                { "RankBearer", "the Ironbreaker is in the ring." },
                { "RankHouse", "housemates." },
                { "RankAllies", "allied houses." },
                { "RankPairDay", "you have met often enough today." },
                { "RankPairWeek", "you have met often enough this week." },
                { "Answer", "  Answer with [F4C96D]/duel accept[FFFFFF] {0} or [F4C96D]/duel decline[FFFFFF] {0} within {2} s." },
                { "AnswerWager", "  Answer with [F4C96D]/duel accept[FFFFFF] {0} {1} (the stake confirms it) or [F4C96D]/duel decline[FFFFFF] {0} within {2} s." },
                { "NoChallenge", "No challenge is waiting for you." },
                { "NoChallengeFrom", "No challenge from '{0}' is waiting for you." },
                { "WhichChallenge", "Several challenges wait for you: name the one you answer, e.g. [F4C96D]/duel accept[FFFFFF] <player>." },
                { "Confirm.duel", "That challenge is for {0} marks each. To take it, type [F4C96D]/duel accept[FFFFFF] {1} {0}" },
                { "Confirm.dice", "That game is for {0} marks each. To take it, type [F4C96D]/dice accept[FFFFFF] {1} {0}" },
                { "Confirm.cards", "That game is for {0} marks each. To take it, type [F4C96D]/cards accept[FFFFFF] {1} {0}" },
                { "TeamAccepted", "{0} accepts. Still waiting for: {1}." },
                { "ChallengeDeclined", "{0} declines the challenge. Any stake goes back to its purse." },
                { "ChallengeCancelled", "{0} withdraws the challenge. Any stake goes back to its purse." },
                { "ChallengeLapsed", "The challenge lapsed unanswered. Any stake goes back to its purse." },
                { "ChallengeGone", "{0} is no longer here; the challenge is off and any stake goes back to its purse." },
                { "ChallengeVoidBusy", "{0} is no longer free to play; the challenge is off and any stake goes back to its purse." },
                { "TableBusy", "You already have a game waiting for an answer. [F4C96D]/dice cancel[FFFFFF] or [F4C96D]/cards cancel[FFFFFF] withdraws it." },
                { "NothingToCancel", "You have no challenge waiting." },
                { "ChallengesOn", "You take challenges again." },
                { "ChallengesOff", "You refuse all challenges until you type [F4C96D]/duel on[FFFFFF]." },

                // the ring
                { "MeetHere", "Accepted! Stand together with {0}: the ring forms where you meet ({1} m). {2} s." },
                { "MeetArena", "Accepted! Meet {0} inside an arena ([F4C96D]/arena zones[FFFFFF]). {2} s." },
                { "StillGathering", "Find {0} and stand together: {1} s left." },
                { "RingDrawn", "The ring is drawn here, {0} m wide. Out of it for {1} s is fleeing." },
                { "RingArena", "The ring is {0}, {1} m wide. Out of it for {2} s is fleeing." },
                { "Count", "{0}..." },
                { "Fight", "Fight! To the first fall. {0} min." },
                { "BackInRing", "Back into the ring! {0} s." },
                { "NotYet", "The ring is not drawn yet: meet first." },
                { "WaitWord", "Wait for the herald's word." },
                { "OutOfFight", "That fighter is out of this fight." },
                { "NoFriendlyBlows", "That is your own side." },
                { "StrikeYourFoe", "You are in a duel: strike only your foe." },
                { "RingClosed", "{0} is fighting a duel. The ring is closed to you." },
                { "ShieldedBlow", "The herald's shield holds over a duel just fought. Stay your hand." },
                { "NoBinding", "No ropes in the ring." },
                { "NoBuilding", "No building inside a drawn ring." },
                { "NotInDuel", "You are not in a duel." },
                { "AlreadyOut", "You are already out of this fight." },
                { "OutBy", "{0} is felled by {1}!" },
                { "Out.felled", "{0} is felled!" },
                { "Out.fell", "{0} falls and is out of the fight!" },
                { "Out.yielded", "{0} yields!" },
                { "Out.fled", "{0} fled the ring and forfeits!" },
                { "Out.died", "{0} has fallen!" },
                { "How.felled", "by a felling blow" },
                { "How.fell", "by a fall" },
                { "How.yielded", "by yielding" },
                { "How.fled", "by flight" },
                { "How.died", "by a death" },
                { "How.decision", "on the judges' count" },
                { "How.noshow", "by a no-show" },
                { "How.time", "as time ran out" },
                { "How.draw", "in a draw" },
                { "How.bye", "with a bye" },
                { "YouWon", "Victory over {0}! The herald's surgeons tend you." },
                { "YouWonWager", "Victory over {0}! {1} marks are yours. The herald's surgeons tend you." },
                { "YouLost", "{0} has the better of you this time. The herald's surgeons tend you." },
                { "YouLostWager", "{0} has the better of you; your stake of {1} marks goes to the winners. The surgeons tend you." },
                { "Draw", "Time! The duel is drawn." },
                { "DrawWager", "Time! The duel is drawn and every stake ({1} marks) goes back." },
                { "RatingUp", "  Rating +{0}, now {1}." },
                { "RatingDown", "  Rating -{0}, now {1}." },
                { "NoContest", "  No contest for the ladder: the fall came too quickly to count." },
                { "FriendlyLine", "  A friendly bout: the ladder does not move." },
                { "VoidReload", "The ring is closed for a reload. The duel is void; every stake goes back." },
                { "VoidShutdown", "The realm is closing. The duel is void; every stake goes back." },
                { "VoidLeft", "{0} left before the fight. The duel is void; every stake goes back." },
                { "VoidNoMeet", "You did not meet in time. The duel is void; every stake goes back." },
                { "VoidTruce", "The Truce of the Realm begins. The duel is void; every stake goes back." },
                { "VoidWithdrawn", "{0} withdraws before the fight. The duel is void; every stake goes back." },
                { "VoidDied", "{0} fell before the fight. The duel is void; every stake goes back." },
                { "VoidStaff", "The staff ({0}) call the duel off. Every stake goes back." },
                { "VoidTourney", "The bracket is closed; this match will not be fought." },
                { "BarredForFleeing", "You fled the ring too often. You are barred from it for {0} h." },
                { "BarredNotice", "You are barred from the ring until {0}." },
                { "HeraldFight", "{0} and {1} meet in {3}!" },
                { "HeraldFightWager", "{0} and {1} meet in {3}, {2} marks a head on the outcome!" },
                { "HeraldWin", "{0} defeats {1} {3}." },
                { "HeraldWinWager", "{0} defeats {1} {3} and takes the pot of {2} marks." },

                // status
                { "StatusDuel", "Duel #{0} against {1}: {2}. Stake {3} marks. {4}" },
                { "StatusRing", "  Ring: {0}, {1} m wide." },
                { "StatusOut", "  You challenged {1} ({0}), {2} marks, {3} s left." },
                { "StatusIn", "  {1} challenges you ({0}), {2} marks, {3} s left." },
                { "StatusBarred", "  You are barred from the ring until {0}." },
                { "StatusRefusing", "  You refuse challenges ([F4C96D]/duel on[FFFFFF] takes them again)." },
                { "StatusNone", "No duel, challenge or game is waiting for you." },
                { "State.gather", "meeting" },
                { "State.countdown", "the count" },
                { "State.fight", "fighting" },
                { "Game.duel", "a duel" },
                { "Game.dice", "Hearth Dice" },
                { "Game.cards", "Twenty-One" },

                // /arena
                { "ArenaHelp1", "The Proving Ring: honour, a ladder and a champion." },
                { "ArenaHelp2", "  [F4C96D]/arena[FFFFFF] overview | top [team] | me | <player> | history | champion | rules | zones" },
                { "ArenaHelp3", "  [F4C96D]/arena tourney[FFFFFF] the bracket | join | leave" },
                { "ArenaHelp4", "  [F4C96D]/duel[FFFFFF] challenges | [F4C96D]/dice[FFFFFF] and [F4C96D]/cards[FFFFFF] at the tavern" },
                { "ArenaHelpAdmin1", "  Admin: [F4C96D]/arena admin[FFFFFF] status | zone set <name> [radius] | zone remove <name> | tavern set|remove <name> | void <duel>" },
                { "ArenaHelpAdmin2", "  Admin: rating <player> <n> | reset <player> confirm | bar <player> <hours> | unbar <player> | crown | pairs | settle; [F4C96D]/arena tourney[FFFFFF] open [fee] | start | cancel" },
                { "Overview", "The Proving Ring." },
                { "YouProvisional", "Your rating {0} is provisional: {1} of {2} ranked duels." },
                { "YouRanked", "You are #{1} on the ladder: rating {0}, {2} wins, {3} losses." },
                { "YouUnplaced", "Rating {0}, {2} wins, {3} losses (off the ladder: no ranked duel lately)." },
                { "ChampionLine", "Champion of the Ring: {0} (week of {1})." },
                { "NoChampionYet", "No Champion of the Ring has been crowned yet." },
                { "NextCrowning", "Next crowning: {1} {0} UTC, for the best rating with {2} ranked duels in the week." },
                { "RingNow", "In the ring now: {0} fights, {1} challenges waiting." },
                { "TourneySignupLine", "{0}: sign-up open until {1} UTC ([F4C96D]/arena tourney[FFFFFF] join)." },
                { "TourneyRunningLine", "{0} is being fought ([F4C96D]/arena tourney[FFFFFF])." },
                { "OverviewMore", "[F4C96D]/arena top[FFFFFF] the ladder | [F4C96D]/arena rules[FFFFFF] | [F4C96D]/arena help[FFFFFF]" },
                { "LadderEmpty", "The ladder is empty: a fighter joins it after {0} ranked duels." },
                { "LadderHeader", "The ladder of the Proving Ring (top {0}):" },
                { "LadderTeamHeader", "The team ladder (top {0}):" },
                { "LadderLine", "  {0}. {1} - {2} ({3} wins, {4} losses)" },
                { "LadderLineChampion", "  {0}. {1}, Champion of the Ring - {2} ({3} wins, {4} losses)" },
                { "LadderYou", "  You: #{0}, rating {1}." },
                { "NoRecord", "No duels on record yet." },
                { "StatsHeader", "{0} in the ring:" },
                { "StatsRating", "  Rating {0}, ladder {1}, {2} ranked duels." },
                { "StatsProvisional", "  Rating {0} (provisional: {2} of {3} ranked duels)." },
                { "StatsRecord", "  {0} wins, {1} losses, {2} draws, {3} friendly bouts. Best streak {4}." },
                { "StatsTeam", "  Teams: rating {0}, {1} wins, {2} losses." },
                { "StatsHonours", "  Crowned champion {0} times, {1} brackets won. Stakes: +{2} won, -{3} lost." },
                { "HistoryEmpty", "No duel has been fought yet." },
                { "HistoryHeader", "The latest duels:" },
                { "HistoryLine", "  [A3A6AD]{0}[FFFFFF] {1} beat {2} {3}" },
                { "HistoryRanked", "  [A3A6AD]{0}[FFFFFF] {1} beat {2} {3} (ranked, +{4})" },
                { "HistoryDraw", "  [A3A6AD]{0}[FFFFFF] duel #{1} was drawn" },
                { "ChampionIs", "Champion of the Ring: {0}, crowned for the week of {1} (rating {2}, {3} wins that week)." },
                { "PastChampion", "  [A3A6AD]{0}[FFFFFF] {1}" },
                { "NoChampion", "No Champion of the Ring this week: no established fighter fought {0} ranked duels." },
                { "ChampionCrowned", "{0} is crowned Champion of the Ring! Rating {1}, {2} wins in {3} ranked duels this week." },
                { "ChampionAgain", "{0} holds the Proving Ring for another week! Rating {1}, {2} wins in {3} ranked duels." },
                { "YouAreChampion", "You are crowned Champion of the Ring for the week: {0} wins in {1} ranked duels." },
                { "Rules1", "Duels end at the first fall: a blow that would kill is turned aside. Out of the ring for {0} s, or logging off, forfeits." },
                { "Rules2", "  No one outside a duel may strike a duellist, and duellists strike only their foes. A shield holds {0} s after." },
                { "Rules3", "  Ranked: provisional for {0} duels; {1} ranked duels a day with the same foe; at most +{2} rating a day." },
                { "Rules4", "  Stakes: {0} to {1} marks, held by the treasury; at most {2} marks a day. The winners take the whole pot." },
                { "Rules5", "  Fleeing {0} times in a day bars you from the ring for {1} h." },
                { "NoZones", "No arena or tavern is set up. Duels are fought where the duellists meet (a ring of {0} m)." },
                { "ZonesHeader", "Arenas and taverns:" },
                { "ZoneLine", "  {0} {1} at ({2}, {3}), {4} m" },
                { "ZoneArena", "Arena" },
                { "ZoneTavern", "Tavern" },

                // tournaments
                { "TourneyName", "the Lists of the Ring" },
                { "RoyalBracketName", "the Royal Tournament bracket" },
                { "TourneyOff", "Tournaments are not allowed on this server." },
                { "TourneyBusy", "A tournament is already open or running." },
                { "TourneyFeeBounds", "The entry fee must be between 0 and {0} marks." },
                { "TourneyOpen", "The Lists of the Ring are open! Enter with [F4C96D]/arena tourney[FFFFFF] join before {2} UTC; the bracket is drawn in {0} min." },
                { "TourneyOpenFee", "The Lists of the Ring are open, {1} marks to enter, the pot to the winners! [F4C96D]/arena tourney[FFFFFF] join before {2} UTC." },
                { "NoSignup", "No tournament is taking entries now." },
                { "RoyalJoin", "This bracket is drawn from the Royal Tournament's entrants: enter with [F4C96D]/tourney[FFFFFF] join." },
                { "AlreadyEntered", "You are already entered." },
                { "TourneyFull", "The bracket is full ({0})." },
                { "TourneyPurse", "The entry fee is {0} marks, and your purse is short." },
                { "TourneyJoined", "You are entered. The bracket is drawn at {0} UTC ({1} entered so far)." },
                { "NotEntered", "You are not in this tournament." },
                { "TourneyLeft", "You leave the Lists; your fee goes back to your purse." },
                { "TourneyForfeit", "You withdraw from the bracket; your match goes to your foe." },
                { "TourneyTooFew", "Too few entered the Lists ({0}, at least {1} are needed). Every fee goes back." },
                { "TourneyEmpty", "The bracket is empty and is closed." },
                { "TourneyCancelled", "The staff ({0}) call the tournament off. Every fee goes back." },
                { "TourneyBegins", "The Lists of the Ring begin with {0} fighters! Top seeds: {1}." },
                { "RoyalBracketOpen", "The Proving Ring holds a bracket for the Royal Tournament: its entrants are drawn in {0} min. Enter with [F4C96D]/tourney[FFFFFF] join." },
                { "RoyalBracketBegins", "The Royal Tournament bracket begins with {0} fighters! Each win counts as a tournament kill. Top seeds: {1}." },
                { "RoyalBracketChampion", "{0} wins the Royal Tournament bracket, beating {1} in the final!" },
                { "RoyalBracketClosed", "The Royal Tournament is over; its bracket closes where it stood." },
                { "RoyalProtected", "You are under the Warden's new-player protection, so you are left out of the bracket (no one could strike you)." },
                { "MatchCalled", "Your match against {0} ({1}) is called! Meet in the ring within {2} min." },
                { "MatchWon", "You beat {0} in the {1}. Wait for your next match." },
                { "MatchLost", "{0} beat you in the {1}. Your tournament is over." },
                { "NoShowDecided", "The match time ran out; {0} was there and goes through." },
                { "CoinDecided", "The match time ran out with no fight; a fair coin sends {0} through." },
                { "RoundDone", "{0} of the Lists is decided. Through: {1}." },
                { "RoundFinal", "final" },
                { "RoundSemi", "semi-final" },
                { "RoundN", "round {0}" },
                { "TourneyChampion", "{0} wins the Lists of the Ring, beating {1} in the final!" },
                { "TourneyChampionPot", "{0} wins the Lists of the Ring, beating {1} in the final! {2} marks to the champion, {3} to the runner-up." },
                { "NoTourney", "No tournament is open. Past brackets:" },
                { "TourneySignupStatus", "Sign-up: {0} entered, the bracket is drawn at {1} UTC. Fee: {2} marks." },
                { "RoyalSignupStatus", "The Royal Tournament bracket: {0} entered so far, drawn at {1} UTC." },
                { "TourneyHowToJoin", "  Enter with [F4C96D]/arena tourney[FFFFFF] join, leave with [F4C96D]/arena tourney[FFFFFF] leave." },
                { "RoyalHowToJoin", "  Enter the Royal Tournament with [F4C96D]/tourney[FFFFFF] join to be drawn." },
                { "BracketHeader", "{0}, {1} ({2} entered):" },
                { "BracketBye", "  {0} has a bye" },
                { "BracketDone", "  {0} against {1}: {2} goes through" },
                { "BracketOpen", "  {0} against {1}: to be fought (by {2} UTC)" },

                // trial by combat
                { "TrialStaged", "Case #{0}: your trial by combat against {1} is fought in the ring, to the first fall. Meet within {2} min." },

                // admin
                { "AdminStatus", "Duels {0}, challenges {1}, games {2}, settlements waiting {3}, fighters {4}." },
                { "AdminStatus2", "  Treasury running: {0}. Paid out of stakes: {1}. Next crowning {3} {2} UTC. Tournament: {4}." },
                { "NoSuchDuel", "No such duel. [F4C96D]/arena admin[FFFFFF] status counts them." },
                { "AdminVoided", "Duel #{0} is void." },
                { "AdminRatingUsage", "Usage: [F4C96D]/arena admin[FFFFFF] rating <player> <rating>" },
                { "AdminRatingSet", "{0}'s rating is now {1}." },
                { "AdminResetAsk", "This wipes {0}'s arena record. Repeat with confirm at the end." },
                { "AdminReset", "{0}'s arena record is wiped." },
                { "AdminBarUsage", "Usage: [F4C96D]/arena admin[FFFFFF] bar <player> <hours>" },
                { "AdminBarred", "{0} is barred from the ring until {1}." },
                { "AdminUnbarred", "{0} may enter the ring again." },
                { "AdminCrowned", "Crowned now. The next crowning is {1} {0} UTC." },
                { "PairsHeader", "Pairs that met most in 7 days (ranked, stakes today, tavern today):" },
                { "PairsLine", "  {0} and {1}: {2} ranked, {3} marks, {4} games" },
                { "AdminSettled", "Settlements still waiting: {0}." },
                { "ZoneUsage", "Usage: [F4C96D]/arena admin[FFFFFF] zone set <name> [radius] (stand in the middle) or zone remove <name>; the same for tavern." },
                { "ZoneSet", "{0} is set at ({1}, {2}), {3} m." },
                { "ZoneRemoved", "{0} is removed." },
                { "ZoneNotFound", "No zone called '{0}'." },

                // tavern: {0} name, {1} min, {2} max, {3} daily stake, {4} daily loss, {5} pair games, {6} minutes, {7} cooldown
                { "GameDice", "Hearth Dice" },
                { "GameCards", "Twenty-One" },
                { "DiceHelp1", "Hearth Dice: you each throw two dice; the higher total takes the pot. No house takes a share." },
                { "DiceHelp2", "  [F4C96D]/dice[FFFFFF] <player> <marks> | [F4C96D]/dice accept[FFFFFF] [player] <marks> | [F4C96D]/dice decline[FFFFFF] | [F4C96D]/dice cancel[FFFFFF]" },
                { "DiceHelp3", "  [F4C96D]/dice roll[FFFFFF] [2d6] throws for show, for the players near you." },
                { "CardsHelp1", "Twenty-One: you both draw at once; closest to 21 without going over takes the pot. No house." },
                { "CardsHelp2", "  [F4C96D]/cards[FFFFFF] <player> <marks> | [F4C96D]/cards accept[FFFFFF] [player] <marks> | [F4C96D]/cards decline[FFFFFF] | [F4C96D]/cards cancel[FFFFFF]" },
                { "CardsHelp3", "  [F4C96D]/cards hit[FFFFFF] | [F4C96D]/cards stand[FFFFFF] | [F4C96D]/cards hand[FFFFFF]. Level hands, or both over 21, take back their stakes." },
                { "TavernLimits", "  Stakes {0} to {1} marks. Each day: at most {2} marks staked and {3} lost. Fair odds: house edge zero." },
                { "TavernOff", "The tavern games are closed on this server." },
                { "StakeBounds", "A stake at the table must be between {1} and {2} marks." },
                { "NoTaverns", "Games are played only in a tavern here, and none is set up yet." },
                { "NeedTavern", "Games are played only in a tavern here. [F4C96D]/arena zones[FFFFFF] lists them." },
                { "TavernNew", "You may play for marks once you have spent {6} minutes in the realm." },
                { "TavernCooldown", "Catch your breath: {7} s before the next game." },
                { "StakeDaily", "That would take you over today's limit of {3} marks staked at the tavern." },
                { "LossDaily", "You could lose more than today's limit of {4} marks. The tavern opens to you again tomorrow." },
                { "PairGames", "You two have played {5} games today; that is the limit." },
                { "StakePurse", "You do not have that many marks. [F4C96D]/purse[FFFFFF] shows your purse." },
                { "TheyCannotPlay", "{0} cannot sit at the table now." },
                { "TableSent", "You invite {0} to {1} for {2} marks each; your stake is held. They have {3} s." },
                { "TableIn", "{0} invites you to {1} for {2} marks each!" },
                { "AnswerDice", "  Answer with [F4C96D]/dice accept[FFFFFF] {0} {1} or [F4C96D]/dice decline[FFFFFF] within {2} s." },
                { "AnswerCards", "  Answer with [F4C96D]/cards accept[FFFFFF] {0} {1} or [F4C96D]/cards decline[FFFFFF] within {2} s." },
                { "DiceThrows", "  {0} throws {1} and {2} ({3}); {4} throws {5} and {6} ({7})." },
                { "DiceTie", "Level after five throws: both stakes ({0} marks) go back." },
                { "DiceNearby", "{0} beats {1} at Hearth Dice, {2} to {3}." },
                { "TableWon", "You win against {0}: the pot of {1} marks is yours." },
                { "TableLost", "{0} wins this one; your {1} marks go to them." },
                { "YourHand", "Your hand: {0} ({1}). {2} holds {3} cards." },
                { "HitOrStand", "  [F4C96D]/cards hit[FFFFFF] or [F4C96D]/cards stand[FFFFFF] ({0} s, then the hand stands)." },
                { "OpponentDraws", "{0} draws a card ({1} in hand)." },
                { "OpponentStands", "{0} stands." },
                { "OpponentLeftTable", "{0} left the table; their hand stands." },
                { "YouStand", "You stand on {0}." },
                { "YouBust", "{0}: over 21." },
                { "CardsReveal", "  {0}: {1} ({2}); {3}: {4} ({5})." },
                { "CardsLevel", "Level hands: both stakes ({0} marks) go back." },
                { "NoGame", "You are not at a card table. [F4C96D]/cards help[FFFFFF] explains the game." },
                { "AlreadyStood", "Your hand already stands; wait for the other." },
                { "GameVoid", "The table is closed for a reload; both stakes ({0} marks) go back." },
                { "RollUsage", "Usage: [F4C96D]/dice roll[FFFFFF] [NdM], up to 6 dice of up to 100 sides." },
                { "RollWait", "Wait a moment before you throw again." },
                { "RollLine", "{0} throws {1}d{2}: {3} = {4}" },

                // popups (plain text in the window)
                { "PopupChallengeTitle", "A challenge" },
                { "PopupChallengeBody", "{0} challenges you to a duel to the first fall.\n{1}\n{2}\nAnswer within {5} s." },
                { "PopupTeamBody", "{0} calls a team duel to the first fall:\n{3}\nagainst\n{4}\n{1}\n{2}\nAnswer within {5} s." },
                { "PopupStake", "Stake: {0} marks each, held by the treasury. The winners take it all." },
                { "PopupNoStake", "No stake: for honour alone." },
                { "PopupAccept", "Accept" },
                { "PopupDecline", "Decline" },
                { "PopupOk", "Ok" },
                { "PopupTableTitle", "The tavern" },
                { "PopupDiceBody", "{0} invites you to Hearth Dice for {1} marks each.\nTwo dice each, the higher total takes the pot.\nAnswer within {2} s." },
                { "PopupCardsBody", "{0} invites you to Twenty-One for {1} marks each.\nClosest to 21 without going over takes the pot.\nAnswer within {2} s." },
                { "PopupChampionTitle", "Champion of the Ring" },
                { "PopupChampionBody", "You are crowned Champion of the Ring for the week.\n{0} wins in {1} ranked duels, rating {2}.\nThe realm will know your name." },
                { "PopupMatchTitle", "Your match is called" },
                { "PopupMatchBody", "Your match against {0} ({1}) is called.\nMeet in the ring within {2} min, or the match may go against you." }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        // Lang strings are ours; player text only ever goes in as an argument, never as the format string.
        private string Fmt(string key, Player player, params object[] args)
        {
            string m = Msg(key, player);
            if (args == null || args.Length == 0) return m;
            try { return string.Format(m, args); }
            catch (FormatException) { return m; }
        }

        private void Say(Player p, string tone, string key, params object[] args)
        {
            if (p == null) return;
            p.SendMessage(Styled(Msg("Speaker", p), tone, Fmt(key, p, args)));          // single-string overload: brace safe
        }

        private void Reply(Player p, string key, params object[] args) { Say(p, ChatGold, key, args); }
        private void Ok(Player p, string key, params object[] args) { Say(p, ChatOk, key, args); }
        private void Warn(Player p, string key, params object[] args) { Say(p, ChatWarn, key, args); }

        private void Error(Player p, string key, params object[] args)
        {
            if (p == null) return;
            p.SendError(Styled(Msg("Speaker", p), ChatError, Fmt(key, p, args)));
        }

        // A line that continues the reply above it: no speaker, two spaces in front.
        private void Line(Player p, string key, params object[] args)
        {
            if (p == null) return;
            string t = Fmt(key, p, args);
            if (t.Length > 0 && t[0] != ' ') t = "  " + t;
            p.SendMessage(t);
        }

        private void TavernSay(Player p, string tone, string key, params object[] args)
        {
            if (p == null) return;
            p.SendMessage(Styled(Msg("TavernSpeaker", p), tone, Fmt(key, p, args)));
        }

        private void TavernError(Player p, string key, params object[] args)
        {
            if (p == null) return;
            p.SendError(Styled(Msg("TavernSpeaker", p), ChatError, Fmt(key, p, args)));
        }

        private void Refuse(Player p, string key, string name)
        {
            WagerSettings w = config.Wagers;
            Error(p, key, name ?? "", Until(BarredOf(p.Id.ToString())), w.Min, w.Max, w.MinPlayMinutes, w.DailyLimit, w.PairDailyLimit);
        }

        private void RefuseTavern(Player p, string key, string name)
        {
            TavernSettings v = config.Tavern;
            DateTime cd;
            cooldowns.TryGetValue("tv:" + p.Id, out cd);
            DateTime now = Now();
            TavernError(p, key, name ?? "", v.MinStake, v.MaxStake, v.DailyStakeLimit, v.DailyLossLimit, v.PairGamesPerDay, v.MinPlayMinutes, cd > now ? Secs(cd - now) : 0);
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
        }

        private void HeraldThrottled(string text)
        {
            DateTime now = Now();
            while (heraldTimes.Count > 0 && (now - heraldTimes.Peek()).TotalHours >= 1) heraldTimes.Dequeue();
            if (heraldTimes.Count >= config.Duels.HeraldsPerHour) return;
            heraldTimes.Enqueue(now);
            Herald(text);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex) { PrintWarning("oxide/config/RealmArena.json could not be read (" + ex.Message + "); using the defaults for this run."); config = null; }
            if (config == null) config = DefaultConfig();
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);

            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            StoredData loaded;
            try
            {
                loaded = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                // A cut-off or damaged file: refuse to run rather than overwrite every rating and every held stake.
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message + ". RealmArena will not run or write it. Fix the file"
                    + " or restore oxide/data/" + BackupName + ".json, then reload. Stakes it held stay in the treasury and lapse back to their owners.");
                return;
            }
            if (loaded == null && existed)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json exists but holds no data. RealmArena will not run or write it. Restore "
                    + BackupName + ".json or delete the file to start empty, then reload.");
                return;
            }
            data = loaded ?? new StoredData();
            Normalize();
            Interface.Oxide.DataFileSystem.WriteObject(BackupName, data);     // last known good copy
        }

        private void OnServerInitialized()
        {
            if (loadFailed || data == null) return;
            // Re-sent on hot load (doc 2.1): keep it idempotent.
            if (slowTimer != null && !slowTimer.Destroyed) slowTimer.Destroy();
            if (fastTimer != null && !fastTimer.Destroyed) fastTimer.Destroy();
            slowTimer = timer.Every(config.TickSeconds, SafeSlowTick);
            fastTimer = timer.Every(1f, SafeFastTick);
            if (!initialized)
            {
                initialized = true;
                RecoverAfterLoad();
            }
            if (data.NextCrowning == DateTime.MinValue || data.NextCrowning.Year < 2000)
            {
                data.NextCrowning = NextCrowning(Now());
                dirty = true;
            }
            SafeSlowTick();
        }

        // Anything left running by a crash or a reload is void: every stake goes back, no one wins or loses.
        private void RecoverAfterLoad()
        {
            foreach (Duel d in data.Duels.ToArray()) VoidDuel(d, "VoidReload", false);
            foreach (Challenge c in data.Challenges.ToArray()) DropChallenge(c, null, null);
            foreach (TavernGame g in data.Games.ToArray()) VoidGame(g, "GameVoid");
            if (data.Tourney != null)
                foreach (Match m in data.Tourney.Matches)
                    if (m.State == MCalled) { m.State = MWait; m.DuelId = 0; }
            ProcessSettlements();
            SaveData();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnServerShutdown()
        {
            shuttingDown = true;
        }

        private void Unload()
        {
            popupsClosed = true;
            if (loadFailed || data == null) return;
            try
            {
                foreach (Duel d in data.Duels.ToArray()) VoidDuel(d, "VoidReload", true);
                foreach (Challenge c in data.Challenges.ToArray()) DropChallenge(c, null, null);
                foreach (TavernGame g in data.Games.ToArray()) VoidGame(g, "GameVoid");
                ClearDeathGuards();
            }
            catch (Exception ex) { PrintError("Unload: " + ex.Message); }
            SaveData();
        }

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            try
            {
                Fighter f = GetFighter(player.Id.ToString(), player.Name, true);
                if (f == null) return;
                f.LastSeen = Now();
                DateTime now = Now();
                if (f.BarredUntil > now) Warn(player, "BarredNotice", Until(f.BarredUntil));
                dirty = true;
            }
            catch (Exception ex) { PrintError("Connect handling failed: " + ex.Message); }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (loadFailed || data == null || player == null || player.IsServer) return;
            try
            {
                string id = player.Id.ToString();
                foreach (Challenge c in data.Challenges.ToArray())
                    foreach (Member m in c.Members)
                        if (m.Id == id) { DropChallenge(c, "ChallengeGone", CleanName(player.Name)); break; }
                Duel d = DuelOf(id);
                if (d != null)
                {
                    Member m = MemberOf(d, id);
                    if (shuttingDown) VoidDuel(d, "VoidShutdown", true);
                    else if (d.State == SFight) { if (!m.Out) MemberOut(d, m, "fled", null); }
                    else if (d.Kind == KBracket) { if (d.State == SCountdown) MemberOut(d, m, "fled", null); }   // gathering: absent until the deadline
                    else
                    {
                        if (d.State == SCountdown) NoteFlee(id, CleanName(player.Name));
                        VoidDuel(d, "VoidLeft", true, CleanName(player.Name));
                    }
                }
                TavernGame g = GameOf(id);
                if (g != null) StandHand(g, id, true);
                lastPvp.Remove(id);
                shieldUntil.Remove(id);
                ClearDeathGuard(player);
                Fighter f = GetFighter(id, null, false);
                if (f != null) f.LastSeen = Now();
                dirty = true;
            }
            catch (Exception ex) { PrintError("Disconnect handling failed: " + ex.Message); }
        }

        #endregion

        #region Ticks

        private void SafeSlowTick()
        {
            if (loadFailed || data == null) return;
            try { SlowTick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex.Message); }
        }

        private void SafeFastTick()
        {
            if (loadFailed || data == null) return;
            if (data.Duels.Count == 0 && data.Games.Count == 0) return;
            try { FastTick(); }
            catch (Exception ex) { PrintError("Ring tick failed: " + ex.Message); }
        }

        private DateTime lastPlayClock = DateTime.MinValue;

        private void SlowTick()
        {
            DateTime now = Now();
            // Time on the server, for the minimum play time of ranked duels, wagers and tavern games.
            if (lastPlayClock != DateTime.MinValue)
            {
                double minutes = Math.Min(10.0, Math.Max(0.0, (now - lastPlayClock).TotalMinutes));
                if (minutes > 0)
                    foreach (Player p in Online())
                    {
                        Fighter f = GetFighter(p.Id.ToString(), p.Name, true);
                        if (f != null) { f.PlayedMinutes += minutes; f.LastSeen = now; }
                    }
                dirty = true;
            }
            lastPlayClock = now;

            foreach (Challenge c in data.Challenges.ToArray())
                if (now >= c.Expires) DropChallenge(c, "ChallengeLapsed", null);

            bool truce = TruceActive();
            if (truce && !truceSeen)
                foreach (Duel d in data.Duels.ToArray()) VoidDuel(d, "VoidTruce", true);
            truceSeen = truce;

            ProcessSettlements();
            TourneyTick(now);
            RoyalTick(now);
            ChampionTick(now);
            Prune(now);
            if (dirty) SaveData();
        }

        private void FastTick()
        {
            DateTime now = Now();
            foreach (Duel d in data.Duels.ToArray())
            {
                if (!data.Duels.Contains(d)) continue;
                if (d.State == SGather) GatherTick(d, now);
                else if (d.State == SCountdown) CountdownTick(d, now);
                else if (d.State == SFight) FightTick(d, now);
            }
            foreach (TavernGame g in data.Games.ToArray())
            {
                if (!g.DoneA && now >= g.TurnEndsA) StandHand(g, g.AId, false);
                if (data.Games.Contains(g) && !g.DoneB && now >= g.TurnEndsB) StandHand(g, g.BId, false);
            }
        }

        private void Prune(DateTime now)
        {
            foreach (string k in new List<string>(shieldUntil.Keys)) if (shieldUntil[k] <= now) shieldUntil.Remove(k);
            foreach (string k in new List<string>(lastPvp.Keys)) if ((now - lastPvp[k]).TotalSeconds > 600) lastPvp.Remove(k);
            foreach (string k in new List<string>(cooldowns.Keys)) if (cooldowns[k] <= now) cooldowns.Remove(k);
            if (lastNotice.Count > 500) lastNotice.Clear();
            foreach (string k in new List<string>(data.Pairs.Keys))
            {
                PairRec p = data.Pairs[k];
                while (p.Ranked.Count > 0 && (now - p.Ranked[0]).TotalDays > 7) { p.Ranked.RemoveAt(0); if (p.Winners.Count > 0) p.Winners.RemoveAt(0); }
                if (p.Ranked.Count == 0 && (now - p.LastUsed).TotalDays > 8) { data.Pairs.Remove(k); dirty = true; }
            }
            while (data.History.Count > config.HistoryKept) { data.History.RemoveAt(0); dirty = true; }
            while (data.Champions.Count > 52) { data.Champions.RemoveAt(0); dirty = true; }
            while (data.TourneyHistory.Count > 30) { data.TourneyHistory.RemoveAt(0); dirty = true; }
            if (data.Fighters.Count > config.MaxFighters)
            {
                // Forget the longest-unseen fighters with no ranked duels first; the ladder is never pruned.
                var victims = new List<KeyValuePair<string, Fighter>>(data.Fighters);
                victims.Sort(delegate(KeyValuePair<string, Fighter> a, KeyValuePair<string, Fighter> b)
                {
                    int ga = a.Value.Games + a.Value.TeamGames > 0 ? 1 : 0, gb = b.Value.Games + b.Value.TeamGames > 0 ? 1 : 0;
                    if (ga != gb) return ga.CompareTo(gb);
                    return a.Value.LastSeen.CompareTo(b.Value.LastSeen);
                });
                for (int i = 0; i < victims.Count && data.Fighters.Count > config.MaxFighters; i++)
                {
                    if (DuelOf(victims[i].Key) != null || GameOf(victims[i].Key) != null) continue;
                    data.Fighters.Remove(victims[i].Key);
                }
                dirty = true;
            }
        }

        #endregion

        #region Challenges

        // Whether a player may step into the ring at all. Returns the lang key of the refusal, or null.
        private string RingRefusal(Player p, bool self)
        {
            string id = p.Id.ToString();
            Fighter f = GetFighter(id, p.Name, true);
            DateTime now = Now();
            if (f != null && f.BarredUntil > now) return self ? "YouBarred" : "TheyBarred";
            if (DuelOf(id) != null) return self ? "YouInDuel" : "TheyInDuel";
            if (GameOf(id) != null) return self ? "YouAtTable" : "TheyAtTable";
            if (!IsAlive(p)) return self ? "YouDown" : "TheyDown";
            if (IsProtected(p)) return self ? "YouProtected" : "TheyProtected";
            if (IsFrozen(p)) return self ? "YouFrozen" : "TheyFrozen";
            if (InCombat(id, now)) return self ? "YouInCombat" : "TheyInCombat";
            if (InTourney(id)) return self ? "YouInTourney" : "TheyInTourney";
            return null;
        }

        // Whether a stake may be wagered by this player against these others. Returns a lang key, or null.
        private string WagerRefusal(Player p, long wager, List<string> others)
        {
            if (wager <= 0) return null;
            WagerSettings w = config.Wagers;
            if (!w.Enabled) return "WagersOff";
            if (wager < w.Min || wager > w.Max) return "WagerBounds";
            Fighter f = GetFighter(p.Id.ToString(), p.Name, true);
            if (f == null) return "WagerBounds";
            if (f.PlayedMinutes < w.MinPlayMinutes) return "WagerNew";
            DateTime now = Now();
            RollDay(f, now);
            if (f.WagerToday + wager > w.DailyLimit) return "WagerDaily";
            foreach (string o in others)
            {
                PairRec pr = GetPair(p.Id.ToString(), o, false);
                if (pr != null) { RollPairDay(pr, now); if (pr.Wagered + wager > w.PairDailyLimit) return "WagerPair"; }
            }
            long purse;
            if (!TryPurse(p.Id.ToString(), out purse)) return "TreasuryClosed";
            if (purse < wager) return "WagerPurse";
            return null;
        }

        private bool CommonChecks(Player player, List<Player> others, long wager)
        {
            string id = player.Id.ToString();
            DateTime now = Now();
            if (TruceActive()) { Error(player, "TruceHolds"); return false; }
            DateTime cd;
            if (cooldowns.TryGetValue("ch:" + id, out cd) && cd > now) { Error(player, "ChallengeCooldown", Secs(cd - now)); return false; }
            int open = 0;
            foreach (Challenge c in data.Challenges) if (c.Members[0].Id == id) open++;
            if (open >= config.Duels.MaxOpenChallenges) { Error(player, "TooManyChallenges", config.Duels.MaxOpenChallenges); return false; }
            string why = RingRefusal(player, true);
            if (why != null) { Refuse(player, why, null); return false; }
            if (config.Duels.RequireArena && !(config.Duels.Teleport && config.Arenas.Count > 0) && ArenaAt(player) == null)
            {
                Error(player, config.Arenas.Count == 0 ? "NoArenas" : "NeedArena");
                return false;
            }
            var ids = new List<string>();
            foreach (Player o in others)
            {
                string oid = o.Id.ToString();
                Fighter of = GetFighter(oid, o.Name, true);
                if (of != null && of.ChallengesOff) { Error(player, "TheyRefuse", CleanName(o.Name)); return false; }
                why = RingRefusal(o, false);
                if (why != null) { Refuse(player, why, CleanName(o.Name)); return false; }
                if (cooldowns.TryGetValue("re:" + PairKey(id, oid), out cd) && cd > now) { Error(player, "RechallengeWait", CleanName(o.Name), Secs(cd - now)); return false; }
                foreach (Challenge c in data.Challenges)
                    if (Involves(c, id) && Involves(c, oid)) { Error(player, "AlreadyChallenged", CleanName(o.Name)); return false; }
                ids.Add(oid);
            }
            why = WagerRefusal(player, wager, ids);
            if (why != null) { Refuse(player, why, null); return false; }
            return true;
        }

        private void ChallengeDuel(Player player, string[] args, int start)
        {
            if (!config.Duels.Enabled) { Error(player, "DuelsOff"); return; }
            if (args.Length <= start) { DuelHelp(player); return; }
            long wager = 0;
            int last = args.Length - 1;
            long parsed;
            if (last > start && long.TryParse(args[last], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { wager = parsed; last--; }
            if (wager < 0) { Refuse(player, "WagerBounds", null); return; }
            string name = JoinRange(args, start, last);
            Player target = FindOnline(name);
            if (target == null) { Error(player, "PlayerNotFound", CleanName(name)); return; }
            if (target.Id == player.Id) { Error(player, "NotYourself"); return; }
            if (!CommonChecks(player, new List<Player> { target }, wager)) return;

            Challenge c = NewChallenge(KDuel, player, wager, config.Duels.ChallengeSeconds);
            c.Members.Add(NewMember(target, 1));
            if (!HoldFirstStake(player, c)) return;
            data.Challenges.Add(c);
            cooldowns["ch:" + player.Id] = Now().AddSeconds(config.Duels.ChallengeCooldownSeconds);
            SaveData();
            string rank = RankNote(target, RankedReason(c.Members, KDuel));
            Ok(player, wager > 0 ? "ChallengeSentWager" : "ChallengeSent", CleanName(target.Name), wager, config.Duels.ChallengeSeconds);
            Line(player, "RankLine", rank);
            InviteToDuel(target, c, player, rank);
        }

        private void ChallengeTeam(Player player, string[] args, int size)
        {
            if (!config.Duels.Enabled) { Error(player, "DuelsOff"); return; }
            if (!config.Teams.Enabled || size > config.Teams.MaxSize) { Error(player, "TeamsOff"); return; }
            int names = size * 2 - 1;
            int last = args.Length - 1;
            long wager = 0, parsed;
            if (args.Length - 1 > names && long.TryParse(args[last], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { wager = parsed; last--; }
            if (last != names) { Error(player, size == 2 ? "Team2Usage" : "Team3Usage"); return; }
            if (wager < 0) { Refuse(player, "WagerBounds", null); return; }
            var players = new List<Player>();
            var seen = new HashSet<ulong> { player.Id };
            for (int i = 1; i <= names; i++)
            {
                Player p = FindOnline(args[i]);
                if (p == null) { Error(player, "PlayerNotFound", CleanName(args[i])); return; }
                if (!seen.Add(p.Id)) { Error(player, "TeamTwice", CleanName(p.Name)); return; }
                players.Add(p);
            }
            if (!CommonChecks(player, players, wager)) return;
            foreach (Player p in players)
            {
                string why = WagerRefusal(p, wager, new List<string>());
                if (why != null) { Error(player, "TheyCannotStake", CleanName(p.Name)); return; }
            }
            Challenge c = NewChallenge(KTeam, player, wager, config.Teams.ChallengeSeconds);
            for (int i = 0; i < players.Count; i++) c.Members.Add(NewMember(players[i], i < size - 1 ? 0 : 1));
            if (!HoldFirstStake(player, c)) return;
            data.Challenges.Add(c);
            cooldowns["ch:" + player.Id] = Now().AddSeconds(config.Duels.ChallengeCooldownSeconds);
            SaveData();
            string rank = RankNote(null, RankedReason(c.Members, KTeam));
            Ok(player, "TeamSent", SideNames(c.Members, 0), SideNames(c.Members, 1), wager, config.Teams.ChallengeSeconds);
            Line(player, "RankLine", rank);
            foreach (Player p in players) InviteToDuel(p, c, player, rank);
        }

        private Challenge NewChallenge(string kind, Player by, long wager, int seconds)
        {
            DateTime now = Now();
            var c = new Challenge { Id = data.NextId++, Kind = kind, Wager = wager, Created = now, Expires = now.AddSeconds(seconds) };
            Member m = NewMember(by, 0);
            m.Accepted = true;
            c.Members.Add(m);
            return c;
        }

        private static Member NewMember(Player p, int side)
        {
            return new Member { Id = p.Id.ToString(), Name = CleanName(p.Name), Side = side };
        }

        // The challenger's stake is held before anyone is asked: a challenge can never promise marks that are gone.
        private bool HoldFirstStake(Player player, Challenge c)
        {
            if (c.Wager <= 0) return true;
            if (HoldStake(c.Id, c.Members[0], c.Wager, HoldMinutesFor(c.Kind))) return true;
            Error(player, "HoldFailed");
            return false;
        }

        private void InviteToDuel(Player target, Challenge c, Player by, string rank)
        {
            string key = c.Kind == KTeam ? (c.Wager > 0 ? "TeamInWager" : "TeamIn") : (c.Wager > 0 ? "ChallengeInWager" : "ChallengeIn");
            Warn(target, key, CleanName(by.Name), c.Wager, SideNames(c.Members, 0), SideNames(c.Members, 1));
            Line(target, "RankLine", rank);
            Line(target, c.Wager > 0 ? "AnswerWager" : "Answer", CleanName(by.Name), c.Wager, SecondsLeft(c.Expires));
            string title = Msg("PopupChallengeTitle", target);
            string body = Fmt(c.Kind == KTeam ? "PopupTeamBody" : "PopupChallengeBody", target, CleanName(by.Name), c.Wager > 0 ? Fmt("PopupStake", target, c.Wager) : Msg("PopupNoStake", target),
                rank, SideNames(c.Members, 0), SideNames(c.Members, 1), SecondsLeft(c.Expires));
            AskYesNo(target, "challenge", c.Id.ToString(), SecondsLeft(c.Expires), title, body, Msg("PopupAccept", target), Msg("PopupDecline", target),
                delegate(Player p, PopupAsk ask, bool yes, string text)
                {
                    Challenge open = ChallengeById(ask.Data);
                    if (open == null) { Error(p, "NoChallenge"); return; }
                    if (yes) DoAccept(p, open); else DoDecline(p, open);
                });
        }

        // /duel accept [player] [amount], /dice accept ..., /cards accept ... (family: duel, dice or cards)
        private void AcceptCommand(Player player, string[] args, string family)
        {
            string id = player.Id.ToString();
            long amount = -1, parsed;
            int last = args.Length - 1;
            if (last >= 1 && long.TryParse(args[last], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { amount = parsed; last--; }
            string from = last >= 1 ? JoinRange(args, 1, last) : null;
            int count;
            Challenge c = FindIncoming(id, from, family, out count);
            if (c == null)
            {
                if (count > 1) ErrorFor(player, family, "WhichChallenge", CommandWord(family));
                else ErrorFor(player, family, from == null ? "NoChallenge" : "NoChallengeFrom", CleanName(from ?? ""));
                return;
            }
            if (c.Wager > 0 && amount != c.Wager)
            {
                // The amount is part of the answer: a challenge cannot be swapped for a dearer one under the reader's eyes.
                ErrorFor(player, family, "Confirm." + family, c.Wager, c.Members[0].Name);
                return;
            }
            DropAsk(id, "challenge");
            DoAccept(player, c);
        }

        private void DoAccept(Player player, Challenge c)
        {
            string id = player.Id.ToString();
            Member me = null;
            foreach (Member m in c.Members) if (m.Id == id && !m.Accepted) me = m;
            if (me == null || !data.Challenges.Contains(c)) { ErrorFor(player, Family(c), "NoChallenge"); return; }
            if (Now() >= c.Expires) { DropChallenge(c, "ChallengeLapsed", null); return; }
            bool tavern = c.Kind == KDice || c.Kind == KCards;
            if (TruceActive() && !tavern) { Error(player, "TruceHolds"); return; }
            if (!tavern && config.Duels.RequireArena && !(config.Duels.Teleport && config.Arenas.Count > 0) && ArenaAt(player) == null)
            {
                Error(player, config.Arenas.Count == 0 ? "NoArenas" : "NeedArena");
                return;
            }
            var others = new List<string>();
            foreach (Member m in c.Members) if (m.Id != id && m.Side != me.Side) others.Add(m.Id);
            string why = tavern ? TavernRefusal(player, c.Kind, c.Wager, c.Members[0].Id) : RingRefusal(player, true);
            if (why == null && !tavern) why = WagerRefusal(player, c.Wager, others);
            if (why != null) { if (tavern) RefuseTavern(player, why, null); else Refuse(player, why, null); return; }
            Player by = OnlineById(c.Members[0].Id);
            if (by == null) { DropChallenge(c, "ChallengeGone", c.Members[0].Name); return; }
            // Everyone who already said yes must still be free to play: one of them may have taken another fight since.
            foreach (Member m in c.Members)
            {
                if (m.Id == id || !m.Accepted) continue;
                Player mp = OnlineById(m.Id);
                string busy = mp == null ? "gone" : (tavern ? TavernStillOk(mp, c.Wager) : RingRefusal(mp, false));
                if (busy != null) { DropChallenge(c, "ChallengeVoidBusy", m.Name); return; }
            }
            if (c.Wager > 0 && !HoldStake(c.Id, me, c.Wager, HoldMinutesFor(c.Kind))) { ErrorFor(player, Family(c), "HoldFailed"); return; }
            me.Accepted = true;
            foreach (Member m in c.Members) if (!m.Accepted) { dirty = true; TellChallenge(c, "TeamAccepted", me.Name, PendingNames(c)); SaveData(); return; }
            data.Challenges.Remove(c);
            if (tavern) StartGame(c);
            else StartDuel(c);
        }

        private void DeclineCommand(Player player, string[] args, string family)
        {
            string from = args.Length > 1 ? JoinFrom(args, 1) : null;
            int count;
            Challenge c = FindIncoming(player.Id.ToString(), from, family, out count);
            if (c == null) { ErrorFor(player, family, count > 1 ? "WhichChallenge" : "NoChallenge", CommandWord(family)); return; }
            DropAsk(player.Id.ToString(), "challenge");
            DoDecline(player, c);
        }

        private void DoDecline(Player player, Challenge c)
        {
            if (!data.Challenges.Contains(c)) return;
            DropChallenge(c, "ChallengeDeclined", CleanName(player.Name));
        }

        private void CancelOutgoing(Player player, string family)
        {
            int n = 0;
            foreach (Challenge c in data.Challenges.ToArray())
                if (c.Members[0].Id == player.Id.ToString() && Family(c) == family) { DropChallenge(c, "ChallengeCancelled", c.Members[0].Name); n++; }
            if (n == 0) ErrorFor(player, family, "NothingToCancel");
        }

        // Ends a challenge that will not be fought: every stake back, the same pair may not meet again at once.
        private void DropChallenge(Challenge c, string key, string arg)
        {
            if (!data.Challenges.Remove(c)) return;
            var s = new Settlement { Ref = "challenge #" + c.Id, Created = Now() };
            foreach (Member m in c.Members) if (m.Held > 0 && m.HoldId != null) s.Releases.Add(m.HoldId);
            QueueSettlement(s);
            DateTime until = Now().AddSeconds(config.Duels.RechallengeSeconds);
            for (int i = 1; i < c.Members.Count; i++) cooldowns["re:" + PairKey(c.Members[0].Id, c.Members[i].Id)] = until;
            foreach (Member m in c.Members) DropAsk(m.Id, "challenge");
            if (key != null) TellChallenge(c, key, arg ?? "", c.Wager);
            dirty = true;
        }

        private void TellChallenge(Challenge c, string key, params object[] args)
        {
            bool tavern = c.Kind == KDice || c.Kind == KCards;
            foreach (Member m in c.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null) continue;
                if (tavern) TavernSay(p, ChatWarn, key, args); else Warn(p, key, args);
            }
        }

        // The one open challenge to this player of this family (from the named challenger, if given).
        private Challenge FindIncoming(string id, string from, string family, out int count)
        {
            Challenge hit = null;
            count = 0;
            DateTime now = Now();
            foreach (Challenge c in data.Challenges)
            {
                if (Family(c) != family || now >= c.Expires) continue;     // lapsed: dropped on the next tick
                bool invited = false;
                foreach (Member m in c.Members) if (m.Id == id && !m.Accepted) invited = true;
                if (!invited) continue;
                if (from != null && !NameMatches(c.Members[0].Name, from)) continue;
                hit = c;
                count++;
            }
            return count == 1 ? hit : null;
        }

        private static string Family(Challenge c)
        {
            return c.Kind == KTeam ? KDuel : c.Kind;
        }

        private static string CommandWord(string family)
        {
            return family == KDice ? "dice" : (family == KCards ? "cards" : "duel");
        }

        private void ErrorFor(Player p, string family, string key, params object[] args)
        {
            if (family == KDice || family == KCards) TavernError(p, key, args); else Error(p, key, args);
        }

        private Challenge ChallengeById(string id)
        {
            int n;
            if (!int.TryParse(id, out n)) return null;
            foreach (Challenge c in data.Challenges) if (c.Id == n) return c;
            return null;
        }

        private static bool Involves(Challenge c, string id)
        {
            foreach (Member m in c.Members) if (m.Id == id) return true;
            return false;
        }

        private string PendingNames(Challenge c)
        {
            var n = new List<string>();
            foreach (Member m in c.Members) if (!m.Accepted) n.Add(m.Name);
            return string.Join(", ", n.ToArray());
        }

        #endregion

        #region Duels: gather, countdown, fight, end

        private void StartDuel(Challenge c)
        {
            DateTime now = Now();
            var d = new Duel { Id = c.Id, Kind = c.Kind, Members = c.Members, Wager = c.Wager, Created = now, State = SGather, GatherEnds = now.AddSeconds(config.Duels.GatherSeconds) };
            d.Unranked = RankedReason(d.Members, d.Kind);
            d.Ranked = d.Unranked == null;
            foreach (Member m in d.Members)
            {
                Fighter f = GetFighter(m.Id, m.Name, true);
                if (f == null) continue;
                RollDay(f, now);
                f.WagerToday += m.Held;
            }
            if (d.Wager > 0)
                foreach (Member a in d.Members)
                    foreach (Member b in d.Members)
                        if (a.Side == 0 && b.Side == 1)
                        {
                            PairRec pr = GetPair(a.Id, b.Id, true);
                            RollPairDay(pr, now);
                            pr.Wagered += d.Wager;
                            pr.LastUsed = now;
                        }
            data.Duels.Add(d);
            BeginGathering(d);
            SaveData();
        }

        // A new duel looks for its ring: brought into an arena (Teleport), or waiting for the duellists to meet.
        private void BeginGathering(Duel d)
        {
            DateTime now = Now();
            Zone z = config.Duels.Teleport && config.Arenas.Count > 0 ? config.Arenas[0] : null;
            if (z != null && TeleportInto(d, z))
            {
                SetRing(d, z.Name, z.X, z.Y, z.Z, z.Radius);
                d.Teleported = true;
                StartCountdown(d, now);
                return;
            }
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null) continue;
                Warn(p, config.Duels.RequireArena ? "MeetArena" : "MeetHere", Others(d, m), RingRadiusFor(d), SecondsLeft(d.GatherEnds));
            }
            GatherTick(d, now);
        }

        private void GatherTick(Duel d, DateTime now)
        {
            var players = new List<Player>();
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null || p.Entity == null || !IsAlive(p)) { players = null; break; }
                players.Add(p);
            }
            if (players != null)
            {
                Zone z = null;
                foreach (Zone a in config.Arenas)
                {
                    bool all = true;
                    foreach (Player p in players) if (!Inside(p.Entity.Position, a.X, a.Z, a.Radius)) { all = false; break; }
                    if (all) { z = a; break; }
                }
                if (z != null) { SetRing(d, z.Name, z.X, z.Y, z.Z, z.Radius); StartCountdown(d, now); return; }
                if (!config.Duels.RequireArena)
                {
                    float cx = 0, cy = 0, cz = 0;
                    foreach (Player p in players) { Vector3 v = p.Entity.Position; cx += v.x; cy += v.y; cz += v.z; }
                    cx /= players.Count; cy /= players.Count; cz /= players.Count;
                    float r = RingRadiusFor(d);
                    bool together = true;
                    foreach (Player p in players) if (!Inside(p.Entity.Position, cx, cz, r)) { together = false; break; }
                    if (together) { SetRing(d, null, cx, cy, cz, r); StartCountdown(d, now); return; }
                }
            }
            if (now >= d.GatherEnds) { GatherTimeout(d); return; }
            int left = SecondsLeft(d.GatherEnds);
            if (left > 0 && left % 30 == 0)
                foreach (Member m in d.Members)
                {
                    Player p = OnlineById(m.Id);
                    if (p != null && Throttle("gather:" + d.Id + ":" + m.Id, 20)) Warn(p, "StillGathering", Others(d, m), left);
                }
        }

        private void SetRing(Duel d, string arena, float x, float y, float z, float r)
        {
            d.Arena = arena;
            d.HasCentre = true;
            d.CX = x; d.CY = y; d.CZ = z;
            d.Radius = r;
        }

        private void StartCountdown(Duel d, DateTime now)
        {
            d.State = SCountdown;
            d.FightAt = now.AddSeconds(config.Duels.CountdownSeconds);
            d.LastCount = config.Duels.CountdownSeconds + 1;
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null) continue;
                if (d.Arena != null) Warn(p, "RingArena", d.Arena, (int)d.Radius, config.Duels.LeaveRingSeconds);
                else Warn(p, "RingDrawn", (int)d.Radius, config.Duels.LeaveRingSeconds);
            }
            dirty = true;
            if (config.Duels.CountdownSeconds == 0) StartFight(d, now);
        }

        private void GatherTimeout(Duel d)
        {
            if (d.Kind == KBracket) { BracketNoShow(d); return; }
            VoidDuel(d, "VoidNoMeet", true);
        }

        private void CountdownTick(Duel d, DateTime now)
        {
            int secs = (int)Math.Ceiling((d.FightAt - now).TotalSeconds);
            if (secs <= 0) { StartFight(d, now); return; }
            if (secs >= d.LastCount) return;
            d.LastCount = secs;
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p != null) Warn(p, "Count", secs);
            }
        }

        private void StartFight(Duel d, DateTime now)
        {
            d.State = SFight;
            d.FightAt = now;
            d.EndsAt = d.Kind == KTrial && d.GatherEnds > now.AddMinutes(1) ? d.GatherEnds : now.AddMinutes(config.Duels.MaxDuelMinutes);
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null) continue;
                Ok(p, "Fight", config.Duels.MaxDuelMinutes);
                SetDeathGuard(p);
            }
            if (Notable(d))
                HeraldThrottled(Fmt(d.Wager > 0 ? "HeraldFightWager" : "HeraldFight", null, SideNames(d.Members, 0), SideNames(d.Members, 1), d.Wager,
                    d.Arena ?? Msg("TheRing", null)));
            dirty = true;
        }

        private void FightTick(Duel d, DateTime now)
        {
            if (now >= d.EndsAt) { EndDuel(d, -1, "time"); return; }
            foreach (Member m in d.Members.ToArray())
            {
                if (m.Out || !data.Duels.Contains(d)) continue;
                Player p = OnlineById(m.Id);
                if (p == null || p.Entity == null) continue;
                PlayerHealth h = HealthOf(p);
                if (h != null && h.CurrentHealth <= 0.01f) { MemberOut(d, m, "felled", null); continue; }   // past the guard (PreventDeathFlag)
                if (!d.HasCentre) continue;
                if (Inside(p.Entity.Position, d.CX, d.CZ, d.Radius)) { m.OutsideSince = DateTime.MinValue; continue; }
                if (m.OutsideSince == DateTime.MinValue) { m.OutsideSince = now; m.LastWarn = DateTime.MinValue; }
                double outside = (now - m.OutsideSince).TotalSeconds;
                if (outside >= config.Duels.LeaveRingSeconds) { MemberOut(d, m, "fled", null); continue; }
                if ((now - m.LastWarn).TotalSeconds >= 2)
                {
                    m.LastWarn = now;
                    Warn(p, "BackInRing", (int)Math.Ceiling(config.Duels.LeaveRingSeconds - outside));
                }
            }
        }

        private void YieldCommand(Player player)
        {
            string id = player.Id.ToString();
            Duel d = DuelOf(id);
            if (d == null) { Error(player, "NotInDuel"); return; }
            Member m = MemberOf(d, id);
            if (m.Out) { Error(player, "AlreadyOut"); return; }
            if (d.State == SGather && (d.Kind == KDuel || d.Kind == KTeam)) { VoidDuel(d, "VoidWithdrawn", true, m.Name); return; }
            MemberOut(d, m, "yielded", null);
        }

        // One duellist is out of the fight: felled by a foe, fallen, yielded, fled or died.
        private void MemberOut(Duel d, Member m, string how, Member by)
        {
            if (m.Out || !data.Duels.Contains(d)) return;
            m.Out = true;
            m.OutHow = how;
            if (how == "fled" && (d.State == SFight || d.State == SCountdown) && d.Kind != KBracket) NoteFlee(m.Id, m.Name);
            ClearDeathGuard(OnlineById(m.Id));
            foreach (Member o in d.Members)
            {
                Player p = OnlineById(o.Id);
                if (p == null) continue;
                if (by != null) Warn(p, "OutBy", m.Name, by.Name);
                else Warn(p, "Out." + how, m.Name);
            }
            int alive0 = 0, alive1 = 0;
            foreach (Member o in d.Members) if (!o.Out) { if (o.Side == 0) alive0++; else alive1++; }
            if (alive0 == 0 && alive1 == 0) EndDuel(d, -1, how);
            else if (alive0 == 0) EndDuel(d, 1, how);
            else if (alive1 == 0) EndDuel(d, 0, how);
            else dirty = true;
        }

        private void NoteFlee(string id, string name)
        {
            Fighter f = GetFighter(id, name, true);
            if (f == null) return;
            DateTime now = Now();
            f.Flees.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 24; });
            f.Flees.Add(now);
            if (f.Flees.Count >= config.Duels.FleeBanCount && config.Duels.FleeBanHours > 0)
            {
                f.BarredUntil = now.AddHours(config.Duels.FleeBanHours);
                f.Flees.Clear();
                Player p = OnlineById(id);
                if (p != null) Error(p, "BarredForFleeing", config.Duels.FleeBanHours);
                Puts(name + " (" + id + ") is barred from the ring for " + config.Duels.FleeBanHours + " h for fleeing.");
            }
            dirty = true;
        }

        // The end of a duel that was fought (or forfeited): winSide 0 or 1, or -1 for a draw.
        private void EndDuel(Duel d, int winSide, string how)
        {
            if (!data.Duels.Remove(d)) return;
            DateTime now = Now();
            bool fought = d.State == SFight || d.State == SCountdown;
            int seconds = d.State == SFight ? Math.Max(0, (int)(now - d.FightAt).TotalSeconds) : 0;
            foreach (Member m in d.Members)
            {
                ClearDeathGuard(OnlineById(m.Id));
                if (!fought) continue;
                Tend(m);
                if (config.Duels.ShieldSeconds > 0) shieldUntil[m.Id] = now.AddSeconds(config.Duels.ShieldSeconds);
            }
            if (d.Teleported) ReturnLater(d);
            var winners = new List<Member>();
            var losers = new List<Member>();
            foreach (Member m in d.Members) { if (m.Side == winSide) winners.Add(m); else losers.Add(m); }

            if (winSide < 0)
            {
                var s = new Settlement { Ref = "duel #" + d.Id + " drawn", Created = now };
                foreach (Member m in d.Members) if (m.Held > 0) s.Releases.Add(m.HoldId);
                QueueSettlement(s);
                foreach (Member m in d.Members)
                {
                    Fighter f = GetFighter(m.Id, m.Name, true);
                    if (f != null) { f.Draws++; f.Friendly += d.Ranked ? 0 : 1; }
                    Player p = OnlineById(m.Id);
                    if (p != null) Warn(p, d.Wager > 0 ? "DrawWager" : "Draw", d.Wager);
                }
                AddHistory(d, "", "", "draw", 0);
                if (d.Kind == KBracket) BracketDraw(d);
                SaveData();
                return;
            }

            string note = null;
            Dictionary<string, int> changes = d.Ranked ? RateDuel(d, winners, losers, seconds, out note) : null;
            CountResult(d, winners, losers, changes != null, now);
            if (d.Wager > 0)
            {
                QueueSettlement(Payout("duel #" + d.Id, d.Members, winners));
                long won = d.Wager * losers.Count / Math.Max(1, winners.Count);
                foreach (Member w in winners) { Fighter f = GetFighter(w.Id, w.Name, true); if (f != null) f.MarksWon += won; }
                foreach (Member l in losers) { Fighter f = GetFighter(l.Id, l.Name, true); if (f != null) f.MarksLost += d.Wager; }
            }
            string wn = SideNames(d.Members, winSide), ln = SideNames(d.Members, 1 - winSide);
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null) continue;
                bool won = m.Side == winSide;
                int ch = 0;
                if (changes != null) changes.TryGetValue(m.Id, out ch);
                if (won) Ok(p, d.Wager > 0 ? "YouWonWager" : "YouWon", ln, d.Wager * losers.Count / Math.Max(1, winners.Count) + d.Wager);
                else Warn(p, d.Wager > 0 ? "YouLostWager" : "YouLost", wn, d.Wager);
                if (changes != null) Line(p, ch >= 0 ? "RatingUp" : "RatingDown", Math.Abs(ch), RatingOf(m.Id, d.Kind == KTeam));
                else if (note != null) Line(p, note);
                else if (d.Unranked != null && d.Kind != KTrial && d.Kind != KBracket) Line(p, "FriendlyLine");
            }
            if (changes != null && d.Kind == KDuel && !string.IsNullOrEmpty(config.Ranked.RenownDeed))
            {
                int gain;
                if (changes.TryGetValue(winners[0].Id, out gain) && gain > 0)
                    AddDeed(winners[0].Id, winners[0].Name, config.Ranked.RenownDeed, "beat " + losers[0].Name + " in the ring", null);   // RealmRenown's own cooldown applies
            }
            if (Notable(d)) HeraldThrottled(Fmt(d.Wager > 0 ? "HeraldWinWager" : "HeraldWin", null, wn, ln, d.Wager * d.Members.Count, Msg("How." + how, null)));
            int shown = 0;
            if (changes != null) changes.TryGetValue(winners[0].Id, out shown);
            AddHistory(d, wn, ln, how, shown);
            if (d.Kind == KTrial) ReportTrial(d, winners[0], how);
            if (d.Kind == KBracket) BracketResult(d, winners[0].Id, losers[0].Id, how);
            SaveData();
        }

        // A duel that will not be decided: every stake back, no rating, no record.
        private void VoidDuel(Duel d, string key, bool tell, params object[] args)
        {
            if (!data.Duels.Remove(d)) return;
            DateTime now = Now();
            bool fought = d.State == SFight || d.State == SCountdown;
            var s = new Settlement { Ref = "duel #" + d.Id + " void", Created = now };
            foreach (Member m in d.Members)
            {
                if (m.Held > 0 && m.HoldId != null) s.Releases.Add(m.HoldId);
                ClearDeathGuard(OnlineById(m.Id));
                if (fought && tell)
                {
                    Tend(m);
                    if (config.Duels.ShieldSeconds > 0) shieldUntil[m.Id] = now.AddSeconds(config.Duels.ShieldSeconds);
                }
                if (!tell) continue;
                Player p = OnlineById(m.Id);
                if (p != null) Warn(p, key, args.Length > 0 ? args[0] : (object)"", d.Wager);
            }
            QueueSettlement(s);
            if (d.Teleported && tell) ReturnLater(d);
            if (d.Kind == KBracket) BracketVoid(d);
            dirty = true;
        }

        private void ReportTrial(Duel d, Member winner, string how)
        {
            if (RealmLaws == null || d.TrialCase == null) return;
            try { RealmLaws.Call("ArenaTrialResult", d.TrialCase, winner.Id, winner.Name + " " + Msg("How." + how, null)); }
            catch (Exception ex) { PrintWarning("RealmLaws trial report failed: " + ex.Message); }
        }

        private bool Notable(Duel d)
        {
            if (d.Kind == KTrial || d.Kind == KBracket) return false;
            if (d.Wager > 0 && d.Wager >= config.Duels.AnnounceMinWager) return true;
            if (data.Champion != null) foreach (Member m in d.Members) if (m.Id == data.Champion.Id) return true;
            return false;
        }

        private void AddHistory(Duel d, string winners, string losers, string how, int change)
        {
            data.History.Add(new ResultRec { At = Now(), DuelId = d.Id, Kind = d.Kind, Winners = winners, Losers = losers, How = how, Wager = d.Wager, Change = change, Ranked = d.Ranked });
            while (data.History.Count > config.HistoryKept) data.History.RemoveAt(0);
        }

        private void Tend(Member m)
        {
            if (config.Duels.HealAfterPercent <= 0f) return;
            Player p = OnlineById(m.Id);
            if (p == null) return;
            try
            {
                PlayerHealth h = HealthOf(p);
                if (h == null || !IsAlive(p)) return;
                float amount = h.MaxHealth * config.Duels.HealAfterPercent / 100f;
                if (amount > 0f) p.Heal(amount);                    // PlayerExtensions.Heal(Player, float) [ASM]
            }
            catch (Exception ex) { PrintWarning("Could not tend " + m.Name + ": " + ex.Message); }
        }

        private float RingRadiusFor(Duel d)
        {
            return d.Kind == KTeam ? config.Teams.RingRadius : config.Duels.RingRadius;
        }

        private string Others(Duel d, Member me)
        {
            var n = new List<string>();
            foreach (Member m in d.Members) if (m.Side != me.Side) n.Add(m.Name);
            return string.Join(", ", n.ToArray());
        }

        private static string SideNames(List<Member> members, int side)
        {
            var n = new List<string>();
            foreach (Member m in members) if (m.Side == side) n.Add(m.Name);
            return string.Join(" & ", n.ToArray());
        }

        #endregion

        #region Teleport (optional, UNVERIFIED)

        // Into the arena: side 0 at the west edge, side 1 at the east edge, half a radius from the centre.
        private bool TeleportInto(Duel d, Zone z)
        {
            var moves = new List<KeyValuePair<Player, Vector3>>();
            int[] n = new int[2];
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p == null || p.Entity == null) return false;
                Vector3 pos = p.Entity.Position;
                m.HasReturn = true;
                m.RX = pos.x; m.RY = pos.y; m.RZ = pos.z;
                Vector3 to = new Vector3();
                to.x = z.X + (m.Side == 0 ? -0.5f : 0.5f) * z.Radius;
                to.y = z.Y + 0.5f;
                to.z = z.Z + 2f * n[m.Side] - 2f;
                n[m.Side]++;
                moves.Add(new KeyValuePair<Player, Vector3>(p, to));
            }
            foreach (KeyValuePair<Player, Vector3> mv in moves) Teleport(mv.Key, mv.Value);
            return true;
        }

        private void ReturnLater(Duel d)
        {
            var back = new List<Member>();
            foreach (Member m in d.Members) if (m.HasReturn) back.Add(m);
            if (back.Count == 0) return;
            timer.Once(3f, delegate
            {
                foreach (Member m in back)
                {
                    Player p = OnlineById(m.Id);
                    if (p == null || p.Entity == null || DuelOf(m.Id) != null) continue;
                    Vector3 to = new Vector3();
                    to.x = m.RX; to.y = m.RY; to.z = m.RZ;
                    Teleport(p, to);
                }
            });
        }

        private void Teleport(Player p, Vector3 to)
        {
            try
            {
                if (RealmSentinel != null) RealmSentinel.Call("SentinelGrace", p.Id, 10f);
                CharacterTeleport tp = p.Entity.GetOrCreate<CharacterTeleport>();
                if (tp != null) tp.Teleport(to);                     // [DEC] the call the game's own /tp makes
            }
            catch (Exception ex) { PrintWarning("Could not move " + CleanName(p.Name) + ": " + ex.Message); }
        }

        #endregion

        #region Hooks: the ring's rules

        // RB 1 [OPJ L162]. Runs for every damage event. Only blows that touch a duel (or a shield) are changed; any other
        // player blow just marks both players as in a fight (CombatTagSeconds).
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Cancelled) return null;
            try { return JudgeBlow(evt); }
            catch (Exception ex) { PrintError("Blow judgement failed: " + ex.Message); return null; }
        }

        private object JudgeBlow(EntityDamageEvent evt)
        {
            Entity ve = evt.Entity;
            if (ve == null || !ve.IsPlayer) return null;
            Damage d = evt.Damage;
            if (d == null || d.Amount <= 0f) return null;                 // healing passes
            Player victim = ve.Owner;
            if (victim == null || victim.IsServer) return null;
            Player attacker = d.DamageSource != null && d.DamageSource.IsPlayer ? d.DamageSource.Owner : null;
            if (attacker != null && (attacker.IsServer || attacker.Id == victim.Id)) attacker = null;
            string vid = victim.Id.ToString();
            DateTime now = Now();
            Duel vd = data.Duels.Count > 0 ? DuelOf(vid) : null;

            if (attacker == null)
            {
                // The world (a fall, fire, a beast) may hurt a duellist, but it does not kill one in the fight.
                if (vd == null || vd.State != SFight) return null;
                Member vm = MemberOf(vd, vid);
                if (vm.Out || !WouldFell(victim, d, false)) return null;
                Turn(evt, d);
                MemberOut(vd, vm, "fell", null);
                return true;
            }

            string aid = attacker.Id.ToString();
            Duel ad = data.Duels.Count > 0 ? DuelOf(aid) : null;
            if (ad == null && vd == null)
            {
                if (shieldUntil.Count > 0 && (Shielded(vid, now) || Shielded(aid, now)))
                {
                    Turn(evt, d);
                    NoticeOnce(attacker, "ShieldedBlow");
                    return true;
                }
                lastPvp[aid] = now;                                        // a fight outside the ring
                lastPvp[vid] = now;
                return null;
            }
            if (ad != vd)
            {
                // One of them is in a duel the other is not part of.
                Duel inDuel = vd ?? ad;
                if (inDuel.State == SGather) { lastPvp[aid] = now; lastPvp[vid] = now; return null; }   // no ring is drawn yet
                Turn(evt, d);
                if (ad != null) NoticeOnce(attacker, "StrikeYourFoe");
                else NoticeOnce(attacker, "RingClosed", CleanName(victim.Name));
                return true;
            }
            Member am = MemberOf(ad, aid), tm = MemberOf(ad, vid);
            if (ad.State != SFight) { Turn(evt, d); NoticeOnce(attacker, ad.State == SGather ? "NotYet" : "WaitWord"); return true; }
            if (am.Out || tm.Out) { Turn(evt, d); NoticeOnce(attacker, "OutOfFight"); return true; }
            if (am.Side == tm.Side) { Turn(evt, d); NoticeOnce(attacker, "NoFriendlyBlows"); return true; }
            float amount = d.Amount;
            if (WouldFell(victim, d, IsBearer(aid) || IsBearer(vid)))
            {
                am.Dealt += amount;
                Turn(evt, d);
                MemberOut(ad, tm, "felled", am);
                return true;
            }
            am.Dealt += amount;
            return null;
        }

        private static void Turn(EntityDamageEvent evt, Damage d)
        {
            evt.Cancel("The ring");
            d.Amount = 0f;
        }

        // Would this blow kill the player, or leave less than YieldHealthPercent of full health? The game's own rule
        // ([DEC] PlayerHealth.OnEntityDamage): the hit region loses Damage.Amount and the player dies when the head or the
        // torso reaches 0; a leg hit beyond the legs' health spills into the torso; a blow with no hit bone is spread over
        // the three regions by their MaxHealth. Judged FatalMargin (and BearerMargin) times harder than it is.
        private bool WouldFell(Player victim, Damage d, bool bearer)
        {
            PlayerHealth h = HealthOf(victim);
            if (h == null) return false;
            float amount = d.Amount * config.Duels.FatalMargin * (bearer ? config.Duels.BearerMargin : 1f);
            float max = h.MaxHealth;
            if (max > 0f && h.CurrentHealth - amount < max * config.Duels.YieldHealthPercent / 100f) return true;
            HealthRegion head = h.HeadHealth, torso = h.TorsoHealth, legs = h.LegsHealth;
            if (head == null || torso == null || legs == null) return h.CurrentHealth - amount <= 0f;
            int bone;
            if (TryBone(d, out bone))
            {
                if (InRegion(head, bone)) return head.CurrentHealth - amount <= 0f;
                if (InRegion(torso, bone)) return torso.CurrentHealth - amount <= 0f;
                if (InRegion(legs, bone)) return legs.CurrentHealth <= amount && torso.CurrentHealth - (amount - legs.CurrentHealth) <= 0f;
                float total = head.MaxHealth + torso.MaxHealth + legs.MaxHealth;
                if (total <= 0f) return true;
                return head.CurrentHealth - amount * head.MaxHealth / total <= 0f || torso.CurrentHealth - amount * torso.MaxHealth / total <= 0f;
            }
            // The hit bone could not be read: judge the blow as if it struck the weaker vital region.
            return Math.Min(head.CurrentHealth, torso.CurrentHealth) - amount <= 0f;
        }

        // Damage.HitBoxBone is a public field of type UnityEngine.HumanBodyBones and HealthRegion.Bones a public
        // List<HumanBodyBones> [ASM]. The compile check's UnityEngine stub has no HumanBodyBones, so both are read by
        // reflection and compared as integers.
        private static FieldInfo hitBoneField;
        private static PropertyInfo bonesProperty;
        private static bool boneBindTried;

        private static bool BindBones()
        {
            if (!boneBindTried)
            {
                boneBindTried = true;
                try
                {
                    hitBoneField = typeof(Damage).GetField("HitBoxBone", BindingFlags.Public | BindingFlags.Instance);
                    bonesProperty = typeof(HealthRegion).GetProperty("Bones", BindingFlags.Public | BindingFlags.Instance);
                }
                catch (Exception) { hitBoneField = null; bonesProperty = null; }
            }
            return hitBoneField != null && bonesProperty != null;
        }

        private static bool TryBone(Damage d, out int bone)
        {
            bone = -1;
            if (!BindBones()) return false;
            try
            {
                object v = hitBoneField.GetValue(d);
                if (v == null) return false;
                bone = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool InRegion(HealthRegion r, int bone)
        {
            try
            {
                System.Collections.IList list = bonesProperty.GetValue(r, null) as System.Collections.IList;
                if (list == null) return false;
                foreach (object o in list) if (o != null && Convert.ToInt32(o, CultureInfo.InvariantCulture) == bone) return true;
            }
            catch (Exception) { }
            return false;
        }

        // RB 1 [OPJ L188]. Never changes the death. A duellist should not die in the fight (see the header); if one does,
        // the duel is decided and the miss is logged so the margins can be tuned.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Entity == null || !evt.Entity.IsPlayer || data.Duels.Count == 0) return null;
            try
            {
                Player victim = evt.Entity.Owner;
                if (victim == null || victim.IsServer) return null;
                string vid = victim.Id.ToString();
                Duel d = DuelOf(vid);
                if (d == null) return null;
                Member m = MemberOf(d, vid);
                if (d.State != SFight) { VoidDuel(d, "VoidDied", true, m.Name); return null; }
                if (m.Out) return null;
                PrintWarning("A duellist died in the ring (" + m.Name + ", duel #" + d.Id + "): the felling guard missed. Raise Duels.FatalMargin or test PreventDeathFlag.");
                Player killer = evt.KillingDamage != null && evt.KillingDamage.DamageSource != null ? evt.KillingDamage.DamageSource.Owner : null;
                Member by = killer != null ? MemberOf(d, killer.Id.ToString()) : null;
                if (by != null && by.Side == m.Side) by = null;
                MemberOut(d, m, "died", by);
            }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        // RB 1 [OPJ L711]. No ropes, chains or cages on duellists, or by them, while a ring is drawn or a shield holds.
        private object OnPlayerCapture(PlayerCaptureEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Cancelled || evt.Target == null) return null;
            if (data.Duels.Count == 0 && shieldUntil.Count == 0) return null;
            try
            {
                Player captor = evt.Captor != null && evt.Captor.IsPlayer ? evt.Captor.Owner : null;
                string tid = evt.Target.Id.ToString();
                DateTime now = Now();
                bool guarded = InRing(tid) || Shielded(tid, now)
                    || (captor != null && (InRing(captor.Id.ToString()) || Shielded(captor.Id.ToString(), now)));
                if (!guarded) return null;
                evt.Cancel("The ring");
                if (captor != null) NoticeOnce(captor, "NoBinding");
                return true;
            }
            catch (Exception ex) { PrintError("Capture check failed: " + ex.Message); return null; }
        }

        // RB 0 [OPJ L266]; blocked by Cancel() [USE LevelSystem.cs:2072]. No walls in a drawn ring.
        private void OnCubePlacement(CubePlaceEvent evt)
        {
            if (loadFailed || data == null || evt == null || evt.Cancelled || !config.Duels.BlockBuildingInRing || data.Duels.Count == 0) return;
            try
            {
                Player builder = Server.GetPlayerById(evt.SenderId);
                Vector3 pos;
                if (evt.Grid != null) pos = evt.Grid.LocalToWorldCoordinate(evt.Position);
                else if (builder != null && builder.Entity != null) pos = builder.Entity.Position;
                else return;
                foreach (Duel d in data.Duels)
                {
                    if (!d.HasCentre || (d.State != SFight && d.State != SCountdown)) continue;
                    if (!Inside(pos, d.CX, d.CZ, d.Radius + 2f)) continue;
                    evt.Cancel("The ring");
                    if (builder != null) NoticeOnce(builder, "NoBuilding");
                    return;
                }
            }
            catch (Exception ex) { PrintError("Building check failed: " + ex.Message); }
        }

        private void SetDeathGuard(Player p)
        {
            if (!config.Duels.PreventDeathFlag || p == null) return;
            try
            {
                PlayerHealth h = HealthOf(p);
                if (h == null) return;
                h.PreventDeath = true;                                   // [ASM] PlayerHealth.PreventDeath { get; set; }
                deathGuarded[p.Id] = true;
            }
            catch (Exception ex) { PrintWarning("PreventDeath could not be set: " + ex.Message); }
        }

        private void ClearDeathGuard(Player p)
        {
            if (p == null || !deathGuarded.ContainsKey(p.Id)) return;
            deathGuarded.Remove(p.Id);
            try
            {
                PlayerHealth h = HealthOf(p);
                if (h != null) h.PreventDeath = false;
            }
            catch (Exception ex) { PrintWarning("PreventDeath could not be cleared: " + ex.Message); }
        }

        private void ClearDeathGuards()
        {
            foreach (ulong id in new List<ulong>(deathGuarded.Keys))
            {
                Player p = Server.GetPlayerById(id);
                if (p != null) ClearDeathGuard(p);
            }
            deathGuarded.Clear();
        }

        #endregion

        #region Ratings and the ladder

        // Why a duel between these members is not ranked (a lang key), or null when it is.
        private string RankedReason(List<Member> members, string kind)
        {
            if (kind == KTrial) return "RankTrial";
            if (kind == KBracket && !config.Tournament.Ranked) return "RankBracket";
            if (!config.Ranked.Enabled) return "RankOff";
            if (kind == KTeam && !config.Teams.Ranked) return "RankOff";
            DateTime now = Now();
            foreach (Member m in members)
            {
                Fighter f = GetFighter(m.Id, m.Name, true);
                if (f == null || f.PlayedMinutes < config.Ranked.MinPlayMinutes) return "RankNew";
                if (!config.Ranked.BearerRanked && IsBearer(m.Id)) return "RankBearer";
            }
            string day = DayKey(now);
            foreach (Member a in members)
                foreach (Member b in members)
                {
                    if (a.Side != 0 || b.Side != 1) continue;
                    string ha = HouseOfId(a.Id), hb = HouseOfId(b.Id);
                    if (!config.Ranked.SameHouseRanked && ha != null && hb != null && SameText(ha, hb)) return "RankHouse";
                    if (!config.Ranked.AlliesRanked && Allied(ha, hb)) return "RankAllies";
                    PairRec pr = GetPair(a.Id, b.Id, false);
                    if (pr == null) continue;
                    int today = 0, week = 0;
                    foreach (DateTime t in pr.Ranked)
                    {
                        if (DayKey(t) == day) today++;
                        if ((now - t).TotalDays < 7) week++;
                    }
                    if (today >= config.Ranked.PairPerDay) return "RankPairDay";
                    if (week >= config.Ranked.PairPerWeek) return "RankPairWeek";
                }
            return null;
        }

        private string RankNote(Player to, string reason)
        {
            return reason == null ? Msg("RankedYes", to) : Fmt("RankedNo", to, Msg(reason, to));
        }

        private static double Expected(double ra, double rb)
        {
            return 1.0 / (1.0 + Math.Pow(10.0, (rb - ra) / 400.0));
        }

        private static int GamesOf(Fighter f, bool team) { return team ? f.TeamGames : f.Games; }
        private static int RatingOf(Fighter f, bool team) { return team ? f.TeamRating : f.Rating; }

        private int RatingOf(string id, bool team)
        {
            Fighter f = GetFighter(id, null, false);
            return f == null ? config.Ranked.StartRating : RatingOf(f, team);
        }

        private bool Provisional(Fighter f, bool team)
        {
            return GamesOf(f, team) < config.Ranked.ProvisionalGames;
        }

        private int KFor(Fighter f, bool team)
        {
            if (Provisional(f, team)) return config.Ranked.KProvisional;
            return RatingOf(f, team) >= config.Ranked.HighRating ? config.Ranked.KHigh : config.Ranked.K;
        }

        // Elo with the abuse limits: repeats between the same players halve it (PairRepeatFactor), beating only
        // provisional fighters is worth ProvisionalOpponentFactor, and no one gains more than MaxGainPerDay a day.
        // A quick fall where the losers never struck is no contest. Returns each member's change, or null.
        private Dictionary<string, int> RateDuel(Duel d, List<Member> winners, List<Member> losers, int seconds, out string note)
        {
            note = null;
            bool team = d.Kind == KTeam;
            float struck = 0f;
            foreach (Member m in losers) struck += m.Dealt;
            if (seconds < config.Ranked.MinDuelSeconds && struck <= 0f) { note = "NoContest"; return null; }
            DateTime now = Now();
            double ra = 0, rb = 0;
            foreach (Member m in winners) ra += RatingOf(GetFighter(m.Id, m.Name, true), team);
            foreach (Member m in losers) rb += RatingOf(GetFighter(m.Id, m.Name, true), team);
            ra /= winners.Count;
            rb /= losers.Count;
            double surprise = 1.0 - Expected(ra, rb);
            double rep = Math.Pow(config.Ranked.PairRepeatFactor, PriorMeetings(winners, losers, now));
            bool losersProvisional = true;
            foreach (Member m in losers) if (!Provisional(GetFighter(m.Id, m.Name, true), team)) losersProvisional = false;
            var changes = new Dictionary<string, int>();
            foreach (Member m in winners)
            {
                Fighter f = GetFighter(m.Id, m.Name, true);
                double k = KFor(f, team) * rep;
                if (losersProvisional && !Provisional(f, team)) k *= config.Ranked.ProvisionalOpponentFactor;
                int gain = (int)Math.Round(k * surprise, MidpointRounding.AwayFromZero);
                RollDay(f, now);
                gain = Math.Max(0, Math.Min(gain, config.Ranked.MaxGainPerDay - f.GainToday));
                f.GainToday += gain;
                if (team) f.TeamRating += gain; else f.Rating += gain;
                changes[m.Id] = gain;
            }
            foreach (Member m in losers)
            {
                Fighter f = GetFighter(m.Id, m.Name, true);
                int loss = (int)Math.Round(KFor(f, team) * rep * surprise, MidpointRounding.AwayFromZero);
                int before = RatingOf(f, team);
                int after = Math.Max(config.Ranked.MinRating, before - loss);
                if (team) f.TeamRating = after; else f.Rating = after;
                changes[m.Id] = after - before;
            }
            RecordMeetings(winners, losers, now);
            return changes;
        }

        // Ranked meetings in the last 7 days between any winner and any loser (the most of any pair).
        private int PriorMeetings(List<Member> winners, List<Member> losers, DateTime now)
        {
            int most = 0;
            foreach (Member w in winners)
                foreach (Member l in losers)
                {
                    PairRec pr = GetPair(w.Id, l.Id, false);
                    if (pr == null) continue;
                    int n = 0;
                    foreach (DateTime t in pr.Ranked) if ((now - t).TotalDays < 7) n++;
                    if (n > most) most = n;
                }
            return most;
        }

        private void RecordMeetings(List<Member> winners, List<Member> losers, DateTime now)
        {
            foreach (Member w in winners)
                foreach (Member l in losers)
                {
                    PairRec pr = GetPair(w.Id, l.Id, true);
                    pr.Ranked.Add(now);
                    pr.Winners.Add(w.Id);
                    pr.LastUsed = now;
                    if (pr.Ranked.Count < config.Ranked.WinTradeAlertGames || (now - pr.LastAlert).TotalDays < 7) continue;
                    pr.LastAlert = now;
                    int wWins = 0;
                    foreach (string id in pr.Winners) if (id == w.Id) wWins++;
                    string detail = w.Name + " and " + l.Name + ": " + pr.Ranked.Count + " ranked duels in 7 days (" + w.Name + " won " + wWins
                        + ", " + l.Name + " won " + (pr.Winners.Count - wWins) + "). Possible win-trading.";
                    PrintWarning("Arena: " + detail);
                    RaiseAlert("arena_pair", w.Id, detail);
                }
        }

        private void CountResult(Duel d, List<Member> winners, List<Member> losers, bool ranked, DateTime now)
        {
            bool team = d.Kind == KTeam;
            string week = CrownKey(data.NextCrowning);
            foreach (Member m in d.Members)
            {
                Fighter f = GetFighter(m.Id, m.Name, true);
                if (f == null) continue;
                bool won = winners.Contains(m);
                if (!ranked) { f.Friendly++; continue; }
                if (team)
                {
                    f.TeamGames++;
                    if (won) f.TeamWins++; else f.TeamLosses++;
                    continue;
                }
                f.Games++;
                f.LastRanked = now;
                if (f.WeekKey != week) { f.WeekKey = week; f.WeekGames = 0; f.WeekWins = 0; }
                f.WeekGames++;
                if (won)
                {
                    f.Wins++;
                    f.WeekWins++;
                    f.Streak++;
                    if (f.Streak > f.BestStreak) f.BestStreak = f.Streak;
                }
                else
                {
                    f.Losses++;
                    f.Streak = 0;
                }
            }
        }

        // The ladder: established (not provisional) fighters, ranked in the last LadderActiveDays (one against one).
        private List<KeyValuePair<string, Fighter>> Ladder(bool team)
        {
            DateTime now = Now();
            var list = new List<KeyValuePair<string, Fighter>>();
            foreach (KeyValuePair<string, Fighter> kv in data.Fighters)
            {
                if (Provisional(kv.Value, team)) continue;
                if (!team && (now - kv.Value.LastRanked).TotalDays > config.Ranked.LadderActiveDays) continue;
                list.Add(kv);
            }
            list.Sort(delegate(KeyValuePair<string, Fighter> a, KeyValuePair<string, Fighter> b)
            {
                int c = RatingOf(b.Value, team).CompareTo(RatingOf(a.Value, team));
                if (c != 0) return c;
                c = (team ? b.Value.TeamWins : b.Value.Wins).CompareTo(team ? a.Value.TeamWins : a.Value.Wins);
                if (c != 0) return c;
                return string.Compare(a.Value.Name, b.Value.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private int LadderRank(string id, bool team)
        {
            List<KeyValuePair<string, Fighter>> l = Ladder(team);
            for (int i = 0; i < l.Count; i++) if (l[i].Key == id) return i + 1;
            return 0;
        }

        #endregion

        #region The weekly champion

        private DateTime NextCrowning(DateTime now)
        {
            int day = ParseDay(config.Champion.Day);
            if (day < 0) day = 0;
            var t = new DateTime(now.Year, now.Month, now.Day, config.Champion.HourUtc, 0, 0, DateTimeKind.Utc);
            t = t.AddDays(((day - (int)t.DayOfWeek) + 7) % 7);
            if (t <= now) t = t.AddDays(7);
            return t;
        }

        private static string CrownKey(DateTime t)
        {
            return t.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
        }

        private void ChampionTick(DateTime now)
        {
            if (data.NextCrowning == DateTime.MinValue || data.NextCrowning.Year < 2000) { data.NextCrowning = NextCrowning(now); dirty = true; return; }
            if (now < data.NextCrowning) return;
            string key = CrownKey(data.NextCrowning);
            data.NextCrowning = NextCrowning(now);
            dirty = true;
            if (config.Champion.Enabled) CrownChampion(key, now);
        }

        // The best established fighter with at least MinWeeklyGames ranked duels in the week that ends now.
        private void CrownChampion(string key, DateTime now)
        {
            string bestId = null;
            Fighter best = null;
            foreach (KeyValuePair<string, Fighter> kv in data.Fighters)
            {
                Fighter f = kv.Value;
                if (Provisional(f, false) || f.WeekKey != key || f.WeekGames < config.Champion.MinWeeklyGames) continue;
                if (best == null || f.Rating > best.Rating || (f.Rating == best.Rating && (f.WeekWins > best.WeekWins
                    || (f.WeekWins == best.WeekWins && f.LastRanked < best.LastRanked)))) { best = f; bestId = kv.Key; }
            }
            if (best == null)
            {
                Herald(Fmt("NoChampion", null, config.Champion.MinWeeklyGames));
                Puts("No Champion of the Ring for the week of " + key + ".");
                return;
            }
            var rec = new ChampionRec { Id = bestId, Name = best.Name, Week = key, Rating = best.Rating, WeekWins = best.WeekWins, At = now };
            bool again = data.Champion != null && data.Champion.Id == bestId;
            data.Champion = rec;
            data.Champions.Add(rec);
            best.Championships++;
            Herald(Fmt(again ? "ChampionAgain" : "ChampionCrowned", null, best.Name, best.Rating, best.WeekWins, best.WeekGames));
            if (!string.IsNullOrEmpty(config.Champion.RenownDeed))
                AddDeed(bestId, best.Name, config.Champion.RenownDeed, "Champion of the Ring, week of " + key, "arena:champion:" + key);
            string house = HouseOfId(bestId);
            if (house != null && config.Champion.HousePoints > 0) AwardHouse(house, config.Champion.HousePoints, "Champion of the Ring: " + best.Name);
            if (config.Champion.Chronicle)
                Chronicle("title_earned", best.Name + " is crowned Champion of the Ring", best.Name + (house != null ? " of House " + house : "")
                    + " holds the Proving Ring for the week: rating " + best.Rating + ", " + best.WeekWins + " wins in " + best.WeekGames + " ranked duels.", new[] { best.Name });
            Player p = OnlineById(bestId);
            if (p != null)
            {
                Ok(p, "YouAreChampion", best.WeekWins, best.WeekGames);
                if (config.Champion.Popup) Notice(p, Msg("PopupChampionTitle", p), Fmt("PopupChampionBody", p, best.WeekWins, best.WeekGames, best.Rating));
            }
            dirty = true;
        }

        private static int ParseDay(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            for (int i = 0; i < 7; i++) if (string.Equals(((DayOfWeek)i).ToString(), s.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        #endregion

        #region Tournaments (single-elimination brackets)

        private void OpenTourney(Player by, long fee)
        {
            if (!config.Tournament.Enabled) { Error(by, "TourneyOff"); return; }
            if (data.Tourney != null) { Error(by, "TourneyBusy"); return; }
            if (fee < 0 || fee > config.Tournament.MaxEntryFee) { Error(by, "TourneyFeeBounds", config.Tournament.MaxEntryFee); return; }
            if (fee > 0 && RealmTreasury == null) { Error(by, "TreasuryClosed"); return; }
            DateTime now = Now();
            var t = new Tourney { Id = data.NextId++, Kind = TArena, State = TSignup, Opened = now, Fee = fee, OpenedBy = CleanName(by.Name) };
            t.SignupEnds = now.AddMinutes(config.Tournament.SignupMinutes);
            data.Tourney = t;
            Herald(Fmt(fee > 0 ? "TourneyOpenFee" : "TourneyOpen", null, config.Tournament.SignupMinutes, fee, ClockText(t.SignupEnds)));
            if (config.Tournament.Chronicle)
                Chronicle("event_started", "The Lists of the Ring are open", "Fighters may enter the Proving Ring's bracket until " + ClockText(t.SignupEnds)
                    + " UTC" + (fee > 0 ? " for " + fee + " marks; the pot goes to the winners." : "."), new string[0]);
            SaveData();
        }

        private void JoinTourney(Player p)
        {
            Tourney t = data.Tourney;
            if (t == null || t.State != TSignup) { Error(p, "NoSignup"); return; }
            if (t.Kind == TRoyal) { Warn(p, "RoyalJoin"); return; }
            string id = p.Id.ToString();
            if (EntrantOf(t, id) != null) { Error(p, "AlreadyEntered"); return; }
            if (t.Entrants.Count >= config.Tournament.MaxEntrants) { Error(p, "TourneyFull", config.Tournament.MaxEntrants); return; }
            Fighter f = GetFighter(id, p.Name, true);
            if (f != null && f.BarredUntil > Now()) { Refuse(p, "YouBarred", null); return; }
            if (IsProtected(p)) { Refuse(p, "YouProtected", null); return; }
            var e = new TEntrant { Id = id, Name = CleanName(p.Name), Rating = f != null ? f.Rating : config.Ranked.StartRating };
            if (t.Fee > 0)
            {
                long purse;
                if (!TryPurse(id, out purse)) { Error(p, "TreasuryClosed"); return; }
                if (purse < t.Fee) { Error(p, "TourneyPurse", t.Fee); return; }
                var m = new Member { Id = id, Name = e.Name };
                if (!HoldStake(t.Id, m, t.Fee, HoldMinutesFor("tourney"))) { Error(p, "HoldFailed"); return; }
                e.HoldId = m.HoldId;
                e.Paid = m.Held;
            }
            t.Entrants.Add(e);
            Ok(p, "TourneyJoined", ClockText(t.SignupEnds), t.Entrants.Count);
            SaveData();
        }

        private void LeaveTourney(Player p)
        {
            Tourney t = data.Tourney;
            TEntrant e = t != null ? EntrantOf(t, p.Id.ToString()) : null;
            if (e == null || e.Out) { Error(p, "NotEntered"); return; }
            if (t.State == TSignup)
            {
                t.Entrants.Remove(e);
                if (e.Paid > 0) { var s = new Settlement { Ref = "tourney #" + t.Id + " leave", Created = Now() }; s.Releases.Add(e.HoldId); QueueSettlement(s); }
                Ok(p, "TourneyLeft");
                SaveData();
                return;
            }
            Ok(p, "TourneyForfeit");
            Forfeit(t, e);
            SaveData();
        }

        // An entrant gives up the rest of a running bracket: the current match goes to the other side.
        private void Forfeit(Tourney t, TEntrant e)
        {
            e.Out = true;
            foreach (Match m in t.Matches)
            {
                if (m.Round != t.Round || m.State == MDone || (m.AId != e.Id && m.BId != e.Id)) continue;
                Duel d = DuelById(m.DuelId);
                if (d != null) { Member dm = MemberOf(d, e.Id); if (dm != null) { MemberOut(d, dm, "yielded", null); return; } }
                string other = m.AId == e.Id ? m.BId : m.AId;
                DecideMatch(t, m, other, e.Id, "yielded");
                return;
            }
        }

        private void TourneyTick(DateTime now)
        {
            Tourney t = data.Tourney;
            if (t == null) return;
            if (t.State == TSignup) { if (now >= t.SignupEnds) SeedTourney(t, now); return; }
            foreach (Match m in t.Matches.ToArray())
            {
                if (data.Tourney != t) return;
                if (m.Round != t.Round) continue;
                if (m.State == MCalled && DuelById(m.DuelId) == null) { m.State = MWait; m.DuelId = 0; }
                if (m.State == MWait) CallMatch(t, m, now);
            }
            if (data.Tourney == t) AdvanceIfDone(t);
        }

        private void SeedTourney(Tourney t, DateTime now)
        {
            if (t.Kind == TRoyal) LoadRoyalEntrants(t);
            if (t.Entrants.Count < config.Tournament.MinEntrants) { CancelTourney(t, "TourneyTooFew", t.Entrants.Count); return; }
            foreach (TEntrant e in t.Entrants) e.Rating = RatingOf(e.Id, false);
            t.Entrants.Sort(delegate(TEntrant a, TEntrant b)
            {
                int c = b.Rating.CompareTo(a.Rating);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < t.Entrants.Count; i++) t.Entrants[i].Seed = i + 1;
            int size = 1;
            while (size < t.Entrants.Count) size *= 2;
            List<int> order = SeedOrder(size);
            t.State = TRunning;
            t.Round = 1;
            for (int i = 0; i + 1 < order.Count; i += 2)
            {
                TEntrant a = order[i] <= t.Entrants.Count ? t.Entrants[order[i] - 1] : null;
                TEntrant b = order[i + 1] <= t.Entrants.Count ? t.Entrants[order[i + 1] - 1] : null;
                if (a == null) { a = b; b = null; }
                if (a == null) continue;
                t.Matches.Add(NewMatch(t, 1, a, b));
            }
            Herald(Fmt(t.Kind == TRoyal ? "RoyalBracketBegins" : "TourneyBegins", null, t.Entrants.Count, TopSeeds(t)));
            foreach (Match m in t.Matches) if (m.Round == 1) CallMatch(t, m, now);
            SaveData();
        }

        // Standard seeding: 1 meets the last seed, and the top seeds can only meet late. Byes go to the top seeds.
        private static List<int> SeedOrder(int size)
        {
            var order = new List<int> { 1, 2 };
            while (order.Count < size)
            {
                var next = new List<int>();
                int n = order.Count * 2 + 1;
                foreach (int s in order) { next.Add(s); next.Add(n - s); }
                order = next;
            }
            if (size == 1) order = new List<int> { 1 };
            return order;
        }

        private Match NewMatch(Tourney t, int round, TEntrant a, TEntrant b)
        {
            var m = new Match { Id = t.NextMatchId++, Round = round, AId = a.Id, AName = a.Name, State = MWait };
            if (b == null) { m.WinnerId = a.Id; m.State = MDone; m.Note = "bye"; }
            else { m.BId = b.Id; m.BName = b.Name; }
            return m;
        }

        private void CallMatch(Tourney t, Match m, DateTime now)
        {
            if (m.State != MWait || m.BId == null) return;
            if (DuelOf(m.AId) != null || DuelOf(m.BId) != null || GameOf(m.AId) != null || GameOf(m.BId) != null) return;   // busy: next tick
            var d = new Duel { Id = data.NextId++, Kind = KBracket, State = SGather, Created = now, GatherEnds = now.AddMinutes(config.Tournament.MatchMinutes), TourneyId = t.Id, MatchId = m.Id };
            d.Members.Add(new Member { Id = m.AId, Name = m.AName, Side = 0, Accepted = true });
            d.Members.Add(new Member { Id = m.BId, Name = m.BName, Side = 1, Accepted = true });
            d.Unranked = RankedReason(d.Members, KBracket);
            d.Ranked = d.Unranked == null;
            m.State = MCalled;
            m.Deadline = d.GatherEnds;
            m.DuelId = d.Id;
            data.Duels.Add(d);
            foreach (Member dm in d.Members)
            {
                Player p = OnlineById(dm.Id);
                if (p == null) continue;
                Warn(p, "MatchCalled", Others(d, dm), RoundName(t, m.Round), config.Tournament.MatchMinutes);
                Notice(p, Msg("PopupMatchTitle", p), Fmt("PopupMatchBody", p, Others(d, dm), RoundName(t, m.Round), config.Tournament.MatchMinutes));
            }
            BeginGathering(d);
            dirty = true;
        }

        // A bracket duel was decided in the ring.
        private void BracketResult(Duel d, string winnerId, string loserId, string how)
        {
            Tourney t = data.Tourney;
            if (t == null || t.Id != d.TourneyId) return;
            Match m = MatchById(t, d.MatchId);
            if (m == null || m.State == MDone) return;
            DecideMatch(t, m, winnerId, loserId, how);
        }

        private void DecideMatch(Tourney t, Match m, string winnerId, string loserId, string how)
        {
            m.WinnerId = winnerId;
            m.State = MDone;
            m.Note = how;
            TEntrant le = EntrantOf(t, loserId);
            if (le != null) le.Out = true;
            TEntrant we = EntrantOf(t, winnerId);
            foreach (string id in new[] { winnerId, loserId })
            {
                Player p = OnlineById(id);
                if (p == null) continue;
                if (id == winnerId) Ok(p, "MatchWon", le != null ? le.Name : "?", RoundName(t, m.Round));
                else Warn(p, "MatchLost", we != null ? we.Name : "?", RoundName(t, m.Round));
            }
            if (t.Kind == TRoyal && how != "noshow" && how != "bye") ScoreRoyal(winnerId, loserId);
            dirty = true;
            AdvanceIfDone(t);
        }

        // Time ran out in a bracket fight: whoever dealt more damage goes through; level, the higher seed.
        private void BracketDraw(Duel d)
        {
            if (d.Members.Count < 2) return;
            Member a = d.Members[0], b = d.Members[1];
            string w;
            if (a.Dealt > b.Dealt) w = a.Id;
            else if (b.Dealt > a.Dealt) w = b.Id;
            else w = SeedOf(a.Id) <= SeedOf(b.Id) ? a.Id : b.Id;
            BracketResult(d, w, w == a.Id ? b.Id : a.Id, "decision");
        }

        // The match time ran out before the two met. Whoever was there goes through (in an arena zone, if there is one);
        // both there or both missing: a fair coin decides.
        private void BracketNoShow(Duel d)
        {
            data.Duels.Remove(d);
            if (d.Members.Count < 2) return;
            Member a = d.Members[0], b = d.Members[1];
            bool ha = Present(a.Id), hb = Present(b.Id);
            string w = ha && !hb ? a.Id : (hb && !ha ? b.Id : (rng(2) == 0 ? a.Id : b.Id));
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p != null) Warn(p, ha != hb ? "NoShowDecided" : "CoinDecided", w == a.Id ? a.Name : b.Name);
            }
            BracketResult(d, w, w == a.Id ? b.Id : a.Id, "noshow");
        }

        private bool Present(string id)
        {
            Player p = OnlineById(id);
            if (p == null || p.Entity == null || !IsAlive(p)) return false;
            if (config.Arenas.Count == 0) return true;
            return ArenaAt(p) != null;
        }

        // A void bracket duel (a reload, a truce) is called again on the next tick.
        private void BracketVoid(Duel d)
        {
            Tourney t = data.Tourney;
            if (t == null || t.Id != d.TourneyId) return;
            Match m = MatchById(t, d.MatchId);
            if (m != null && m.State == MCalled) { m.State = MWait; m.DuelId = 0; }
        }

        private void AdvanceIfDone(Tourney t)
        {
            if (data.Tourney != t || t.State != TRunning) return;
            var round = new List<Match>();
            foreach (Match m in t.Matches) if (m.Round == t.Round) round.Add(m);
            if (round.Count == 0) { CancelTourney(t, "TourneyEmpty"); return; }
            foreach (Match m in round) if (m.State != MDone) return;
            if (round.Count == 1) { FinishTourney(t, round[0]); return; }
            var names = new List<string>();
            foreach (Match m in round) { TEntrant e = EntrantOf(t, m.WinnerId); if (e != null) names.Add(e.Name); }
            Herald(Fmt("RoundDone", null, RoundName(t, t.Round), string.Join(", ", names.ToArray())));
            t.Round++;
            for (int i = 0; i < round.Count; i += 2)
            {
                TEntrant a = EntrantOf(t, round[i].WinnerId);
                TEntrant b = i + 1 < round.Count ? EntrantOf(t, round[i + 1].WinnerId) : null;
                if (a == null) { a = b; b = null; }
                if (a == null) continue;
                t.Matches.Add(NewMatch(t, t.Round, a, b));
            }
            DateTime now = Now();
            foreach (Match m in t.Matches.ToArray()) if (m.Round == t.Round && data.Tourney == t) CallMatch(t, m, now);
            if (data.Tourney == t) AdvanceIfDone(t);
            dirty = true;
        }

        private void FinishTourney(Tourney t, Match final)
        {
            data.Tourney = null;
            string champId = final.WinnerId;
            string runnerId = final.BId == null ? null : (final.AId == champId ? final.BId : final.AId);
            TEntrant champ = EntrantOf(t, champId), runner = runnerId != null ? EntrantOf(t, runnerId) : null;
            string cn = champ != null ? champ.Name : "?", rn = runner != null ? runner.Name : "-";
            Fighter fc = GetFighter(champId, cn, true);
            if (fc != null) fc.TourneyWins++;
            data.TourneyHistory.Add(DayKey(Now()) + " " + (t.Kind == TRoyal ? "Royal bracket" : "Lists of the Ring") + ": " + cn + " beat " + rn + " (" + t.Entrants.Count + " entered)");
            if (t.Kind == TRoyal)
            {
                Herald(Fmt("RoyalBracketChampion", null, cn, rn));
                SaveData();
                return;
            }
            long pot = 0;
            var payers = new List<Member>();
            foreach (TEntrant e in t.Entrants) if (e.Paid > 0) { pot += e.Paid; payers.Add(new Member { Id = e.Id, Name = e.Name, HoldId = e.HoldId, Held = e.Paid }); }
            long first = 0, second = 0;
            if (pot > 0)
            {
                List<int> split = config.Tournament.PrizeSplit;
                second = runner != null && split.Count > 1 ? pot * split[1] / 100 : 0;
                first = pot - second;
                var shares = new List<KeyValuePair<Member, long>>();
                shares.Add(new KeyValuePair<Member, long>(new Member { Id = champId, Name = cn }, first));
                if (second > 0) shares.Add(new KeyValuePair<Member, long>(new Member { Id = runnerId, Name = rn }, second));
                QueueSettlement(PayoutShares("tourney #" + t.Id, payers, shares));
            }
            Herald(Fmt(pot > 0 ? "TourneyChampionPot" : "TourneyChampion", null, cn, rn, first, second));
            if (!string.IsNullOrEmpty(config.Tournament.RenownDeed))
                AddDeed(champId, cn, config.Tournament.RenownDeed, "won the Lists of the Ring", "arena:tourney:" + t.Id);
            string ch = HouseOfId(champId), rh = runnerId != null ? HouseOfId(runnerId) : null;
            if (ch != null && config.Tournament.ChampionHousePoints > 0) AwardHouse(ch, config.Tournament.ChampionHousePoints, "Lists of the Ring champion: " + cn);
            if (rh != null && config.Tournament.RunnerUpHousePoints > 0) AwardHouse(rh, config.Tournament.RunnerUpHousePoints, "Lists of the Ring runner-up: " + rn);
            if (config.Tournament.Chronicle)
                Chronicle("event_ended", cn + " wins the Lists of the Ring", cn + (ch != null ? " of House " + ch : "") + " won the Proving Ring's bracket of "
                    + t.Entrants.Count + ", beating " + rn + " in the final" + (pot > 0 ? "; the pot of " + pot + " marks is shared " + first + " and " + second + "." : "."), new[] { cn });
            SaveData();
        }

        private void CancelTourney(Tourney t, string key, params object[] args)
        {
            if (data.Tourney != t) return;
            data.Tourney = null;
            foreach (Duel d in data.Duels.ToArray()) if (d.Kind == KBracket && d.TourneyId == t.Id) VoidDuel(d, "VoidTourney", true);
            var s = new Settlement { Ref = "tourney #" + t.Id + " cancelled", Created = Now() };
            foreach (TEntrant e in t.Entrants) if (e.Paid > 0 && e.HoldId != null) s.Releases.Add(e.HoldId);
            QueueSettlement(s);
            Herald(Fmt(key, null, args.Length > 0 ? args[0] : (object)"", config.Tournament.MinEntrants));
            SaveData();
        }

        private string RoundName(Tourney t, int round)
        {
            int size = 1;
            while (size < t.Entrants.Count) size *= 2;
            int left = size >> (round - 1);
            if (left <= 2) return Msg("RoundFinal", null);
            if (left <= 4) return Msg("RoundSemi", null);
            return Fmt("RoundN", null, round);
        }

        private string TopSeeds(Tourney t)
        {
            var n = new List<string>();
            for (int i = 0; i < t.Entrants.Count && i < 4; i++) n.Add(t.Entrants[i].Name);
            return string.Join(", ", n.ToArray());
        }

        private static TEntrant EntrantOf(Tourney t, string id)
        {
            if (t == null || id == null) return null;
            foreach (TEntrant e in t.Entrants) if (e.Id == id) return e;
            return null;
        }

        private static Match MatchById(Tourney t, int id)
        {
            foreach (Match m in t.Matches) if (m.Id == id) return m;
            return null;
        }

        private int SeedOf(string id)
        {
            TEntrant e = EntrantOf(data.Tourney, id);
            return e != null && e.Seed > 0 ? e.Seed : int.MaxValue;
        }

        private bool InTourney(string id)
        {
            Tourney t = data.Tourney;
            if (t == null || t.State != TRunning) return false;
            TEntrant e = EntrantOf(t, id);
            return e != null && !e.Out;
        }

        // ---- The Royal Tournament (RealmEvents) ----

        private void RoyalTick(DateTime now)
        {
            if (!config.Tournament.Enabled || !config.Tournament.RoyalBracket || RealmEvents == null) return;
            string start, end;
            bool running = RoyalRunning(out start, out end);
            Tourney t = data.Tourney;
            if (t != null && t.Kind == TRoyal)
            {
                if (!running || t.RoyalKey != start || now >= t.RoyalEnds) CloseRoyal(t);
                return;
            }
            if (!running || t != null || data.LastRoyalKey == start) return;
            DateTime s = ParseIso(start, now), e = ParseIso(end, now.AddHours(1));
            DateTime seed = s.AddMinutes(config.Tournament.RoyalSeedMinutes);
            var r = new Tourney { Id = data.NextId++, Kind = TRoyal, State = TSignup, Opened = now, SignupEnds = seed > now ? seed : now, RoyalKey = start, RoyalEnds = e };
            data.Tourney = r;
            data.LastRoyalKey = start;
            Herald(Fmt("RoyalBracketOpen", null, Math.Max(0, (int)Math.Ceiling((r.SignupEnds - now).TotalMinutes))));
            dirty = true;
        }

        private bool RoyalRunning(out string start, out string end)
        {
            start = null;
            end = null;
            string[] active = null;
            try { active = RealmEvents.Call("GetActiveEvents") as string[]; }
            catch (Exception ex) { PrintWarning("RealmEvents GetActiveEvents failed: " + ex.Message); }
            if (active == null) return false;
            foreach (string a in active)
            {
                if (a == null) continue;
                string[] parts = a.Split('|');
                if (parts.Length < 3 || parts[0] != "tournament") continue;
                start = parts[1];
                end = parts[2];
                return true;
            }
            return false;
        }

        private void LoadRoyalEntrants(Tourney t)
        {
            string[] list = null;
            try { list = RealmEvents != null ? RealmEvents.Call("GetTournamentEntrants") as string[] : null; }
            catch (Exception ex) { PrintWarning("RealmEvents GetTournamentEntrants failed: " + ex.Message); }
            if (list == null) return;
            foreach (string s in list)
            {
                if (s == null) continue;
                int bar = s.IndexOf('|');
                string id = bar > 0 ? s.Substring(0, bar) : s;
                if (!IsSteamId(id) || EntrantOf(t, id) != null) continue;
                Player p = OnlineById(id);
                if (p != null && IsProtected(p)) { Warn(p, "RoyalProtected"); continue; }
                string name = CleanName(bar > 0 ? s.Substring(bar + 1) : id);
                if (t.Entrants.Count >= config.Tournament.MaxEntrants) break;
                t.Entrants.Add(new TEntrant { Id = id, Name = name, Rating = RatingOf(id, false) });
            }
        }

        private void CloseRoyal(Tourney t)
        {
            if (data.Tourney != t) return;
            data.Tourney = null;
            foreach (Duel d in data.Duels.ToArray()) if (d.Kind == KBracket && d.TourneyId == t.Id) VoidDuel(d, "VoidTourney", true);
            if (t.State == TRunning) Herald(Msg("RoyalBracketClosed", null));
            dirty = true;
        }

        private void ScoreRoyal(string winnerId, string loserId)
        {
            if (RealmEvents == null) return;
            try { RealmEvents.Call("ScoreTournamentDuel", winnerId, loserId); }
            catch (Exception ex) { PrintWarning("RealmEvents ScoreTournamentDuel failed: " + ex.Message); }
        }

        private static DateTime ParseIso(string s, DateTime fallback)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return DateTime.SpecifyKind(t, DateTimeKind.Utc);
            return fallback;
        }

        #endregion

        #region Tavern games: Hearth Dice and Twenty-One

        // Player against player, equal stakes, the same rules for both: the house takes nothing (house edge zero).

        // A player who already staked at this table: still free to play, and still within today's limits?
        private string TavernStillOk(Player p, long stake)
        {
            string id = p.Id.ToString();
            if (DuelOf(id) != null || GameOf(id) != null || InTourney(id)) return "busy";
            Fighter f = GetFighter(id, p.Name, true);
            if (f == null) return "busy";
            RollDay(f, Now());
            if (f.TavernStaked + stake > config.Tavern.DailyStakeLimit || f.TavernLost - f.TavernWon + stake > config.Tavern.DailyLossLimit) return "limits";
            return null;
        }

        private string TavernRefusal(Player p, string kind, long stake, string otherId)
        {
            TavernSettings v = config.Tavern;
            if (!v.Enabled || (kind == KDice && !v.Dice) || (kind == KCards && !v.Cards)) return "TavernOff";
            string id = p.Id.ToString();
            DateTime now = Now();
            Fighter f = GetFighter(id, p.Name, true);
            if (f == null) return "TavernOff";
            if (DuelOf(id) != null) return "YouInDuel";
            if (GameOf(id) != null) return "YouAtTable";
            if (InTourney(id)) return "YouInTourney";
            if (stake < v.MinStake || stake > v.MaxStake) return "StakeBounds";
            if (v.RequireTavernZone && TavernAt(p) == null) return config.Taverns.Count == 0 ? "NoTaverns" : "NeedTavern";
            if (f.PlayedMinutes < v.MinPlayMinutes) return "TavernNew";
            DateTime cd;
            if (cooldowns.TryGetValue("tv:" + id, out cd) && cd > now) return "TavernCooldown";
            RollDay(f, now);
            if (f.TavernStaked + stake > v.DailyStakeLimit) return "StakeDaily";
            if (f.TavernLost - f.TavernWon + stake > v.DailyLossLimit) return "LossDaily";
            if (otherId != null)
            {
                PairRec pr = GetPair(id, otherId, false);
                if (pr != null) { RollPairDay(pr, now); if (pr.TavernGames >= v.PairGamesPerDay) return "PairGames"; }
            }
            long purse;
            if (!TryPurse(id, out purse)) return "TreasuryClosed";
            if (purse < stake) return "StakePurse";
            return null;
        }

        private void ChallengeTavern(Player player, string kind, string[] args, int start)
        {
            if (args.Length - start < 2) { TavernHelp(player, kind); return; }
            long stake;
            if (!long.TryParse(args[args.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out stake)) { TavernHelp(player, kind); return; }
            string name = JoinRange(args, start, args.Length - 2);
            Player target = FindOnline(name);
            if (target == null) { TavernError(player, "PlayerNotFound", CleanName(name)); return; }
            if (target.Id == player.Id) { TavernError(player, "NotYourself"); return; }
            string id = player.Id.ToString(), tid = target.Id.ToString();
            string why = TavernRefusal(player, kind, stake, tid);
            if (why != null) { RefuseTavern(player, why, null); return; }
            why = TavernRefusal(target, kind, stake, id);
            if (why != null) { RefuseTavern(player, "TheyCannotPlay", CleanName(target.Name)); return; }
            DateTime cd;
            if (cooldowns.TryGetValue("re:" + PairKey(id, tid), out cd) && cd > Now()) { TavernError(player, "RechallengeWait", CleanName(target.Name), Secs(cd - Now())); return; }
            foreach (Challenge open in data.Challenges)
            {
                if (Involves(open, id) && Involves(open, tid)) { TavernError(player, "AlreadyChallenged", CleanName(target.Name)); return; }
                if ((open.Kind == KDice || open.Kind == KCards) && open.Members[0].Id == id) { TavernError(player, "TableBusy"); return; }   // one table at a time
            }
            Challenge c = NewChallenge(kind, player, stake, config.Tavern.ChallengeSeconds);
            c.Members.Add(NewMember(target, 1));
            if (!HoldStake(c.Id, c.Members[0], stake, HoldMinutesFor(kind))) { TavernError(player, "HoldFailed"); return; }
            data.Challenges.Add(c);
            SaveData();
            string game = Msg(kind == KDice ? "GameDice" : "GameCards", player);
            TavernSay(player, ChatOk, "TableSent", CleanName(target.Name), game, stake, config.Tavern.ChallengeSeconds);
            TavernSay(target, ChatWarn, "TableIn", CleanName(player.Name), Msg(kind == KDice ? "GameDice" : "GameCards", target), stake);
            Line(target, kind == KDice ? "AnswerDice" : "AnswerCards", CleanName(player.Name), stake, config.Tavern.ChallengeSeconds);
            AskYesNo(target, "challenge", c.Id.ToString(), config.Tavern.ChallengeSeconds, Msg("PopupTableTitle", target),
                Fmt(kind == KDice ? "PopupDiceBody" : "PopupCardsBody", target, CleanName(player.Name), stake, config.Tavern.ChallengeSeconds),
                Msg("PopupAccept", target), Msg("PopupDecline", target),
                delegate(Player p, PopupAsk ask, bool yes, string text)
                {
                    Challenge ch = ChallengeById(ask.Data);
                    if (ch == null) { TavernError(p, "NoChallenge"); return; }
                    if (yes) DoAccept(p, ch); else DoDecline(p, ch);
                });
        }

        // Both stakes are held: play.
        private void StartGame(Challenge c)
        {
            Member a = c.Members[0], b = c.Members[1];
            DateTime now = Now();
            foreach (Member m in c.Members)
            {
                Fighter f = GetFighter(m.Id, m.Name, true);
                if (f != null) { RollDay(f, now); f.TavernStaked += c.Wager; }
                cooldowns["tv:" + m.Id] = now.AddSeconds(config.Tavern.CooldownSeconds);
            }
            PairRec pr = GetPair(a.Id, b.Id, true);
            RollPairDay(pr, now);
            pr.TavernGames++;
            pr.LastUsed = now;
            if (c.Kind == KDice) { PlayDice(c, a, b); return; }
            var g = new TavernGame { Id = c.Id, AId = a.Id, AName = a.Name, BId = b.Id, BName = b.Name, Stake = c.Wager, HoldA = a.HoldId, HoldB = b.HoldId, Started = now };
            for (int i = 0; i < 52; i++) g.Deck.Add(i);
            for (int i = g.Deck.Count - 1; i > 0; i--) { int j = rng(i + 1); int t = g.Deck[i]; g.Deck[i] = g.Deck[j]; g.Deck[j] = t; }   // Fisher-Yates
            g.HandA.Add(Draw(g)); g.HandB.Add(Draw(g)); g.HandA.Add(Draw(g)); g.HandB.Add(Draw(g));
            g.TurnEndsA = now.AddSeconds(config.Tavern.CardTurnSeconds);
            g.TurnEndsB = g.TurnEndsA;
            data.Games.Add(g);
            ShowHand(g, g.AId);
            ShowHand(g, g.BId);
            if (HandValue(g.HandA) >= 21) StandHand(g, g.AId, false);
            if (data.Games.Contains(g) && HandValue(g.HandB) >= 21) StandHand(g, g.BId, false);
            SaveData();
        }

        // Hearth Dice: each throws two dice, the higher total takes the pot. A tie is thrown again (up to five times,
        // then the stakes go back).
        private void PlayDice(Challenge c, Member a, Member b)
        {
            int a1 = 0, a2 = 0, b1 = 0, b2 = 0, tries = 0;
            do
            {
                a1 = rng(6) + 1; a2 = rng(6) + 1; b1 = rng(6) + 1; b2 = rng(6) + 1;
                tries++;
            } while (a1 + a2 == b1 + b2 && tries < 5);
            int sa = a1 + a2, sb = b1 + b2;
            Player pa = OnlineById(a.Id), pb = OnlineById(b.Id);
            string line = Fmt("DiceThrows", null, a.Name, a1, a2, sa, b.Name, b1, b2, sb);
            if (pa != null) Line(pa, "Raw", line);
            if (pb != null) Line(pb, "Raw", line);
            if (sa == sb)
            {
                var s = new Settlement { Ref = "dice #" + c.Id + " tied", Created = Now() };
                s.Releases.Add(a.HoldId); s.Releases.Add(b.HoldId);
                QueueSettlement(s);
                if (pa != null) TavernSay(pa, ChatWarn, "DiceTie", c.Wager);
                if (pb != null) TavernSay(pb, ChatWarn, "DiceTie", c.Wager);
                SaveData();
                return;
            }
            Member w = sa > sb ? a : b, l = sa > sb ? b : a;
            SettleTable("dice #" + c.Id, w, l, c.Wager, a, b);
            TellNearby(OnlineById(w.Id), Fmt("DiceNearby", null, w.Name, l.Name, Math.Max(sa, sb), Math.Min(sa, sb)), w.Id, l.Id);
            SaveData();
        }

        private void SettleTable(string reference, Member winner, Member loser, long stake, Member a, Member b)
        {
            var shares = new List<KeyValuePair<Member, long>>();
            shares.Add(new KeyValuePair<Member, long>(winner, stake * 2));
            var payers = new List<Member> { a, b };
            QueueSettlement(PayoutShares(reference, payers, shares));
            Fighter fw = GetFighter(winner.Id, winner.Name, true), fl = GetFighter(loser.Id, loser.Name, true);
            DateTime now = Now();
            if (fw != null) { RollDay(fw, now); fw.TavernWon += stake; }
            if (fl != null) { RollDay(fl, now); fl.TavernLost += stake; }
            Player pw = OnlineById(winner.Id), pl = OnlineById(loser.Id);
            if (pw != null) TavernSay(pw, ChatOk, "TableWon", loser.Name, stake * 2);
            if (pl != null) TavernSay(pl, ChatWarn, "TableLost", winner.Name, stake);
        }

        private void CardsCommand(Player player, string verb)
        {
            string id = player.Id.ToString();
            TavernGame g = GameOf(id);
            if (g == null) { TavernError(player, "NoGame"); return; }
            bool isA = g.AId == id;
            if (isA ? g.DoneA : g.DoneB) { TavernError(player, "AlreadyStood"); return; }
            if (verb == "hand") { ShowHand(g, id); return; }
            if (verb == "stand") { StandHand(g, id, false); return; }
            List<int> hand = isA ? g.HandA : g.HandB;
            if (g.Deck.Count == 0) { StandHand(g, id, false); return; }
            hand.Add(Draw(g));
            if (isA) g.TurnEndsA = Now().AddSeconds(config.Tavern.CardTurnSeconds); else g.TurnEndsB = Now().AddSeconds(config.Tavern.CardTurnSeconds);
            int v = HandValue(hand);
            ShowHand(g, id);
            Player other = OnlineById(isA ? g.BId : g.AId);
            if (other != null) TavernSay(other, ChatGold, "OpponentDraws", isA ? g.AName : g.BName, hand.Count);
            if (v >= 21) StandHand(g, id, false);
            dirty = true;
        }

        // A hand stands (by choice, at 21 or over, when the turn time runs out, or when its player leaves the table).
        private void StandHand(TavernGame g, string id, bool left)
        {
            if (!data.Games.Contains(g)) return;
            bool isA = g.AId == id;
            if (isA) { if (g.DoneA) return; g.DoneA = true; } else { if (g.DoneB) return; g.DoneB = true; }
            Player p = OnlineById(id);
            int v = HandValue(isA ? g.HandA : g.HandB);
            if (p != null && !left) TavernSay(p, v > 21 ? ChatWarn : ChatGold, v > 21 ? "YouBust" : "YouStand", v);
            Player other = OnlineById(isA ? g.BId : g.AId);
            if (other != null && !(isA ? g.DoneB : g.DoneA)) TavernSay(other, ChatGold, left ? "OpponentLeftTable" : "OpponentStands", isA ? g.AName : g.BName);
            dirty = true;
            if (g.DoneA && g.DoneB) FinishCards(g);
        }

        private void FinishCards(TavernGame g)
        {
            data.Games.Remove(g);
            int va = HandValue(g.HandA), vb = HandValue(g.HandB);
            string line = Fmt("CardsReveal", null, g.AName, HandText(g.HandA), va, g.BName, HandText(g.HandB), vb);
            Player pa = OnlineById(g.AId), pb = OnlineById(g.BId);
            if (pa != null) Line(pa, "Raw", line);
            if (pb != null) Line(pb, "Raw", line);
            bool bustA = va > 21, bustB = vb > 21;
            int winner = 0;                                          // 1 = A, 2 = B, 0 = level
            if (bustA && !bustB) winner = 2;
            else if (bustB && !bustA) winner = 1;
            else if (!bustA && !bustB && va != vb) winner = va > vb ? 1 : 2;
            var a = new Member { Id = g.AId, Name = g.AName, HoldId = g.HoldA, Held = g.Stake };
            var b = new Member { Id = g.BId, Name = g.BName, HoldId = g.HoldB, Held = g.Stake };
            if (winner == 0)
            {
                var s = new Settlement { Ref = "cards #" + g.Id + " level", Created = Now() };
                s.Releases.Add(g.HoldA); s.Releases.Add(g.HoldB);
                QueueSettlement(s);
                if (pa != null) TavernSay(pa, ChatWarn, "CardsLevel", g.Stake);
                if (pb != null) TavernSay(pb, ChatWarn, "CardsLevel", g.Stake);
            }
            else SettleTable("cards #" + g.Id, winner == 1 ? a : b, winner == 1 ? b : a, g.Stake, a, b);
            SaveData();
        }

        // A game that cannot finish (a reload): both stakes go back.
        private void VoidGame(TavernGame g, string key)
        {
            if (!data.Games.Remove(g)) return;
            var s = new Settlement { Ref = "cards #" + g.Id + " void", Created = Now() };
            s.Releases.Add(g.HoldA); s.Releases.Add(g.HoldB);
            QueueSettlement(s);
            foreach (string id in new[] { g.AId, g.BId })
            {
                Player p = OnlineById(id);
                if (p != null) TavernSay(p, ChatWarn, key, g.Stake);
            }
            dirty = true;
        }

        private static int Draw(TavernGame g)
        {
            int c = g.Deck[g.Deck.Count - 1];
            g.Deck.RemoveAt(g.Deck.Count - 1);
            return c;
        }

        // Ace 1 or 11, Knight, Queen and King 10.
        private static int HandValue(List<int> hand)
        {
            int total = 0, aces = 0;
            foreach (int c in hand)
            {
                int r = c % 13;
                if (r == 0) { aces++; total += 11; }
                else total += r >= 9 ? 10 : r + 1;
            }
            while (total > 21 && aces > 0) { total -= 10; aces--; }
            return total;
        }

        private static readonly string[] Ranks = { "Ace", "2", "3", "4", "5", "6", "7", "8", "9", "10", "Knight", "Queen", "King" };
        private static readonly string[] Suits = { "Stags", "Oaks", "Ravens", "Bells" };

        private static string CardName(int c)
        {
            return Ranks[c % 13] + " of " + Suits[(c / 13) % 4];
        }

        private static string HandText(List<int> hand)
        {
            var n = new List<string>();
            foreach (int c in hand) n.Add(CardName(c));
            return string.Join(", ", n.ToArray());
        }

        private void ShowHand(TavernGame g, string id)
        {
            Player p = OnlineById(id);
            if (p == null) return;
            bool isA = g.AId == id;
            List<int> hand = isA ? g.HandA : g.HandB;
            List<int> theirs = isA ? g.HandB : g.HandA;
            TavernSay(p, ChatGold, "YourHand", HandText(hand), HandValue(hand), isA ? g.BName : g.AName, theirs.Count);
            if (!(isA ? g.DoneA : g.DoneB) && HandValue(hand) < 21) Line(p, "HitOrStand", config.Tavern.CardTurnSeconds);
        }

        private void RollCommand(Player player, string[] args)
        {
            int n = 1, sides = 6;
            if (args.Length > 1)
            {
                string spec = args[1].ToLowerInvariant();
                int d = spec.IndexOf('d');
                int pn, ps;
                if (d < 0 || !int.TryParse(d == 0 ? "1" : spec.Substring(0, d), out pn) || !int.TryParse(spec.Substring(d + 1), out ps)) { TavernError(player, "RollUsage"); return; }
                n = pn; sides = ps;
            }
            if (n < 1 || n > 6 || sides < 2 || sides > 100) { TavernError(player, "RollUsage"); return; }
            if (!Throttle("roll:" + player.Id, 3)) { TavernError(player, "RollWait"); return; }
            var rolls = new List<string>();
            int total = 0;
            for (int i = 0; i < n; i++) { int r = rng(sides) + 1; total += r; rolls.Add(r.ToString(CultureInfo.InvariantCulture)); }
            string text = Fmt("RollLine", null, CleanName(player.Name), n, sides, string.Join(" + ", rolls.ToArray()), total);
            TavernSay(player, ChatGold, "Raw", text);
            TellNearby(player, text, player.Id.ToString(), null);
        }

        // A line for the players standing near (RollRadius), not the whole realm.
        private void TellNearby(Player near, string text, string skipA, string skipB)
        {
            if (near == null || near.Entity == null || config.Tavern.RollRadius <= 0f) return;
            Vector3 at = near.Entity.Position;
            foreach (Player p in Online())
            {
                string id = p.Id.ToString();
                if (id == skipA || id == skipB || p.Entity == null) continue;
                if (Inside(p.Entity.Position, at.x, at.z, config.Tavern.RollRadius)) TavernSay(p, ChatGold, "Raw", text);
            }
        }

        private TavernGame GameOf(string id)
        {
            foreach (TavernGame g in data.Games) if (g.AId == id || g.BId == id) return g;
            return null;
        }

        #endregion

        #region Commands

        private bool Ready(Player player)
        {
            if (player == null || player.IsServer) return false;
            if (loadFailed || data == null) { player.SendError(Styled(Msg("Speaker", player), ChatError, Msg("Paused", player))); return false; }
            if (!config.Enabled && !IsAdmin(player)) { Error(player, "ArenaOff"); return false; }
            return true;
        }

        [ChatCommand("duel")]
        private void CmdDuel(Player player, string command, string[] args)
        {
            if (!Ready(player)) return;
            try
            {
                if (args.Length == 0) { DuelHelp(player); return; }
                switch (args[0].ToLowerInvariant())
                {
                    case "help": DuelHelp(player); return;
                    case "accept": AcceptCommand(player, args, KDuel); return;
                    case "decline": DeclineCommand(player, args, KDuel); return;
                    case "cancel": CancelOutgoing(player, KDuel); return;
                    case "yield": YieldCommand(player); return;
                    case "status": DuelStatus(player); return;
                    case "on": SetChallenges(player, true); return;
                    case "off": SetChallenges(player, false); return;
                    case "2v2": ChallengeTeam(player, args, 2); return;
                    case "3v3": ChallengeTeam(player, args, 3); return;
                    case "challenge": ChallengeDuel(player, args, 1); return;
                    default: ChallengeDuel(player, args, 0); return;
                }
            }
            catch (Exception ex) { PrintError("/duel failed: " + ex); Error(player, "Failed"); }
        }

        private void DuelHelp(Player player)
        {
            Reply(player, "DuelHelp1");
            for (int i = 2; i <= 5; i++) Line(player, "DuelHelp" + i);
        }

        private void SetChallenges(Player player, bool on)
        {
            Fighter f = GetFighter(player.Id.ToString(), player.Name, true);
            if (f == null) return;
            f.ChallengesOff = !on;
            dirty = true;
            Ok(player, on ? "ChallengesOn" : "ChallengesOff");
        }

        private void DuelStatus(Player player)
        {
            string id = player.Id.ToString();
            Duel d = DuelOf(id);
            bool any = false;
            if (d != null)
            {
                any = true;
                Member me = MemberOf(d, id);
                Reply(player, "StatusDuel", d.Id, Others(d, me), Msg("State." + d.State, player), d.Wager, RankNote(player, d.Unranked));
                if (d.HasCentre) Line(player, "StatusRing", d.Arena ?? Msg("TheRing", player), (int)d.Radius);
            }
            foreach (Challenge c in data.Challenges)
            {
                if (!Involves(c, id) || Now() >= c.Expires) continue;
                any = true;
                bool mine = c.Members[0].Id == id;
                Line(player, mine ? "StatusOut" : "StatusIn", Msg("Game." + Family(c), player), mine ? PendingNames(c) : c.Members[0].Name, c.Wager, SecondsLeft(c.Expires));
            }
            TavernGame g = GameOf(id);
            if (g != null) { any = true; ShowHand(g, id); }
            Fighter f = GetFighter(id, player.Name, true);
            if (f != null && f.BarredUntil > Now()) { any = true; Line(player, "StatusBarred", Until(f.BarredUntil)); }
            if (f != null && f.ChallengesOff) { any = true; Line(player, "StatusRefusing"); }
            if (!any) Reply(player, "StatusNone");
        }

        [ChatCommand("arena")]
        private void CmdArena(Player player, string command, string[] args)
        {
            if (!Ready(player)) return;
            try
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                switch (sub)
                {
                    case "": ArenaOverview(player); return;
                    case "help": ArenaHelp(player); return;
                    case "top": case "ladder": ShowLadder(player, args.Length > 1 && args[1].ToLowerInvariant() == "team"); return;
                    case "me": case "stats": ShowStats(player, args.Length > 1 ? JoinFrom(args, 1) : null); return;
                    case "history": ShowHistory(player); return;
                    case "champion": ShowChampion(player); return;
                    case "rules": ShowRules(player); return;
                    case "zones": ShowZones(player); return;
                    case "tourney": case "lists": TourneyCommand(player, args); return;
                    case "admin": AdminCommand(player, args); return;
                    default: ShowStats(player, JoinFrom(args, 0)); return;
                }
            }
            catch (Exception ex) { PrintError("/arena failed: " + ex); Error(player, "Failed"); }
        }

        private void ArenaHelp(Player player)
        {
            Reply(player, "ArenaHelp1");
            for (int i = 2; i <= 4; i++) Line(player, "ArenaHelp" + i);
            if (IsAdmin(player)) { Line(player, "ArenaHelpAdmin1"); Line(player, "ArenaHelpAdmin2"); }
        }

        private void ArenaOverview(Player player)
        {
            string id = player.Id.ToString();
            Fighter f = GetFighter(id, player.Name, true);
            Reply(player, "Overview");
            if (f != null)
            {
                int rank = LadderRank(id, false);
                if (Provisional(f, false)) Line(player, "YouProvisional", f.Rating, f.Games, config.Ranked.ProvisionalGames);
                else Line(player, rank > 0 ? "YouRanked" : "YouUnplaced", f.Rating, rank, f.Wins, f.Losses);
            }
            if (data.Champion != null) Line(player, "ChampionLine", data.Champion.Name, data.Champion.Week);
            else Line(player, "NoChampionYet");
            Line(player, "NextCrowning", ClockText(data.NextCrowning), DayText(data.NextCrowning), config.Champion.MinWeeklyGames);
            int fights = 0;
            foreach (Duel d in data.Duels) if (d.State == SFight) fights++;
            Line(player, "RingNow", fights, data.Challenges.Count);
            if (data.Tourney != null) Line(player, data.Tourney.State == TSignup ? "TourneySignupLine" : "TourneyRunningLine", TourneyName(data.Tourney, player), ClockText(data.Tourney.SignupEnds));
            Line(player, "OverviewMore");
        }

        private void ShowLadder(Player player, bool team)
        {
            List<KeyValuePair<string, Fighter>> l = Ladder(team);
            if (l.Count == 0) { Reply(player, "LadderEmpty", config.Ranked.ProvisionalGames); return; }
            Reply(player, team ? "LadderTeamHeader" : "LadderHeader", Math.Min(l.Count, config.Ranked.LadderSize));
            for (int i = 0; i < l.Count && i < config.Ranked.LadderSize; i++)
            {
                Fighter f = l[i].Value;
                bool champ = data.Champion != null && data.Champion.Id == l[i].Key && !team;
                Line(player, champ ? "LadderLineChampion" : "LadderLine", i + 1, f.Name, RatingOf(f, team), team ? f.TeamWins : f.Wins, team ? f.TeamLosses : f.Losses);
            }
            int me = LadderRank(player.Id.ToString(), team);
            if (me > config.Ranked.LadderSize) Line(player, "LadderYou", me, RatingOf(player.Id.ToString(), team));
        }

        private void ShowStats(Player player, string who)
        {
            string id = player.Id.ToString();
            if (!string.IsNullOrEmpty(who))
            {
                id = FindFighterId(who);
                if (id == null) { Error(player, "FighterNotFound", CleanName(who)); return; }
            }
            Fighter f = GetFighter(id, null, false);
            if (f == null) { Reply(player, "NoRecord"); return; }
            int rank = LadderRank(id, false);
            Reply(player, "StatsHeader", f.Name);
            Line(player, Provisional(f, false) ? "StatsProvisional" : "StatsRating", f.Rating, rank > 0 ? "#" + rank : "-", f.Games, config.Ranked.ProvisionalGames);
            Line(player, "StatsRecord", f.Wins, f.Losses, f.Draws, f.Friendly, f.BestStreak);
            Line(player, "StatsTeam", f.TeamRating, f.TeamWins, f.TeamLosses);
            Line(player, "StatsHonours", f.Championships, f.TourneyWins, f.MarksWon, f.MarksLost);
        }

        private void ShowHistory(Player player)
        {
            if (data.History.Count == 0) { Reply(player, "HistoryEmpty"); return; }
            Reply(player, "HistoryHeader");
            int shown = 0;
            for (int i = data.History.Count - 1; i >= 0 && shown < 8; i--, shown++)
            {
                ResultRec r = data.History[i];
                if (r.How == "draw" || r.How == "time") Line(player, "HistoryDraw", r.At.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), r.DuelId);
                else Line(player, r.Ranked ? "HistoryRanked" : "HistoryLine", r.At.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), r.Winners, r.Losers, Msg("How." + r.How, player), r.Change, r.Wager);
            }
        }

        private void ShowChampion(Player player)
        {
            if (data.Champion == null) Reply(player, "NoChampionYet");
            else Reply(player, "ChampionIs", data.Champion.Name, data.Champion.Week, data.Champion.Rating, data.Champion.WeekWins);
            Line(player, "NextCrowning", ClockText(data.NextCrowning), DayText(data.NextCrowning), config.Champion.MinWeeklyGames);
            int shown = 0;
            for (int i = data.Champions.Count - 2; i >= 0 && shown < 4; i--, shown++) Line(player, "PastChampion", data.Champions[i].Week, data.Champions[i].Name);
        }

        private void ShowRules(Player player)
        {
            Reply(player, "Rules1", config.Duels.LeaveRingSeconds);
            Line(player, "Rules2", config.Duels.ShieldSeconds);
            Line(player, "Rules3", config.Ranked.ProvisionalGames, config.Ranked.PairPerDay, config.Ranked.MaxGainPerDay);
            Line(player, "Rules4", config.Wagers.Min, config.Wagers.Max, config.Wagers.DailyLimit);
            Line(player, "Rules5", config.Duels.FleeBanCount, config.Duels.FleeBanHours);
        }

        private void ShowZones(Player player)
        {
            if (config.Arenas.Count == 0 && config.Taverns.Count == 0) { Reply(player, "NoZones", (int)config.Duels.RingRadius); return; }
            Reply(player, "ZonesHeader");
            foreach (Zone z in config.Arenas) Line(player, "ZoneLine", Msg("ZoneArena", player), z.Name, (int)z.X, (int)z.Z, (int)z.Radius);
            foreach (Zone z in config.Taverns) Line(player, "ZoneLine", Msg("ZoneTavern", player), z.Name, (int)z.X, (int)z.Z, (int)z.Radius);
        }

        private void TourneyCommand(Player player, string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "join": JoinTourney(player); return;
                case "leave": LeaveTourney(player); return;
                case "open":
                    if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
                    long fee = 0;
                    if (args.Length > 2 && !long.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out fee)) { Error(player, "TourneyFeeBounds", config.Tournament.MaxEntryFee); return; }
                    OpenTourney(player, fee);
                    return;
                case "start":
                    if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
                    if (data.Tourney == null || data.Tourney.State != TSignup) { Error(player, "NoSignup"); return; }
                    SeedTourney(data.Tourney, Now());
                    return;
                case "cancel":
                    if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
                    if (data.Tourney == null) { Error(player, "NoTourney"); return; }
                    CancelTourney(data.Tourney, "TourneyCancelled", CleanName(player.Name));
                    return;
                default: ShowBracket(player); return;
            }
        }

        private void ShowBracket(Player player)
        {
            Tourney t = data.Tourney;
            if (t == null)
            {
                Reply(player, "NoTourney");
                for (int i = data.TourneyHistory.Count - 1, n = 0; i >= 0 && n < 3; i--, n++) Line(player, "Raw", data.TourneyHistory[i]);
                return;
            }
            if (t.State == TSignup)
            {
                Reply(player, t.Kind == TRoyal ? "RoyalSignupStatus" : "TourneySignupStatus", t.Entrants.Count, ClockText(t.SignupEnds), t.Fee);
                Line(player, t.Kind == TRoyal ? "RoyalHowToJoin" : "TourneyHowToJoin");
                return;
            }
            Reply(player, "BracketHeader", TourneyName(t, player), RoundName(t, t.Round), t.Entrants.Count);
            foreach (Match m in t.Matches)
            {
                if (m.Round != t.Round) continue;
                if (m.BId == null) Line(player, "BracketBye", m.AName);
                else if (m.State == MDone) Line(player, "BracketDone", m.AName, m.BName, m.WinnerId == m.AId ? m.AName : m.BName);
                else Line(player, "BracketOpen", m.AName, m.BName, m.State == MCalled ? ClockText(m.Deadline) : "-");
            }
        }

        private void AdminCommand(Player player, string[] args)
        {
            if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                    Reply(player, "AdminStatus", data.Duels.Count, data.Challenges.Count, data.Games.Count, data.Settlements.Count, data.Fighters.Count);
                    Line(player, "AdminStatus2", RealmTreasury != null ? Msg("Yes", player) : Msg("No", player), data.PotPaid, ClockText(data.NextCrowning), DayText(data.NextCrowning),
                        data.Tourney != null ? TourneyName(data.Tourney, player) + " (" + data.Tourney.State + ")" : Msg("None", player));
                    return;
                case "zone":
                case "tavern":
                    ZoneCommand(player, args, sub == "zone" ? config.Arenas : config.Taverns);
                    return;
                case "void":
                {
                    int id;
                    Duel d = args.Length > 2 && int.TryParse(args[2], out id) ? DuelById(id) : null;
                    if (d == null) { Error(player, "NoSuchDuel"); return; }
                    VoidDuel(d, "VoidStaff", true, CleanName(player.Name));
                    Ok(player, "AdminVoided", d.Id);
                    SaveData();
                    return;
                }
                case "rating":
                {
                    int value;
                    string fid = args.Length > 3 ? FindFighterId(JoinRange(args, 2, args.Length - 2)) : null;
                    if (fid == null || !int.TryParse(args[args.Length - 1], out value)) { Error(player, "AdminRatingUsage"); return; }
                    Fighter f = GetFighter(fid, null, false);
                    f.Rating = Clamp(value, config.Ranked.MinRating, 5000);
                    Puts(player.Name + " set " + f.Name + "'s rating to " + f.Rating);
                    Ok(player, "AdminRatingSet", f.Name, f.Rating);
                    SaveData();
                    return;
                }
                case "reset":
                {
                    bool confirm = args.Length > 3 && args[args.Length - 1].ToLowerInvariant() == "confirm";
                    string fid = args.Length > 2 ? FindFighterId(JoinRange(args, 2, args.Length - (confirm ? 2 : 1))) : null;
                    if (fid == null) { Error(player, "FighterNotFound", args.Length > 2 ? CleanName(args[2]) : ""); return; }
                    Fighter f = GetFighter(fid, null, false);
                    if (!confirm) { Warn(player, "AdminResetAsk", f.Name); return; }
                    string name = f.Name;
                    double played = f.PlayedMinutes;
                    DateTime first = f.FirstSeen;
                    data.Fighters.Remove(fid);
                    Fighter fresh = GetFighter(fid, name, true);
                    fresh.PlayedMinutes = played;                    // time in the realm is not part of the arena record
                    fresh.FirstSeen = first;
                    Puts(player.Name + " reset the arena record of " + name);
                    Ok(player, "AdminReset", name);
                    SaveData();
                    return;
                }
                case "bar":
                {
                    int hours;
                    string fid = args.Length > 3 ? FindFighterId(JoinRange(args, 2, args.Length - 2)) : null;
                    if (fid == null || !int.TryParse(args[args.Length - 1], out hours) || hours < 1) { Error(player, "AdminBarUsage"); return; }
                    Fighter f = GetFighter(fid, null, false);
                    f.BarredUntil = Now().AddHours(Math.Min(hours, 24 * 365));
                    Duel d = DuelOf(fid);
                    if (d != null) VoidDuel(d, "VoidStaff", true, CleanName(player.Name));
                    Ok(player, "AdminBarred", f.Name, Until(f.BarredUntil));
                    SaveData();
                    return;
                }
                case "unbar":
                {
                    string fid = args.Length > 2 ? FindFighterId(JoinFrom(args, 2)) : null;
                    if (fid == null) { Error(player, "FighterNotFound", args.Length > 2 ? CleanName(args[2]) : ""); return; }
                    Fighter f = GetFighter(fid, null, false);
                    f.BarredUntil = DateTime.MinValue;
                    f.Flees.Clear();
                    Ok(player, "AdminUnbarred", f.Name);
                    SaveData();
                    return;
                }
                case "crown":
                {
                    DateTime now = Now();
                    string key = CrownKey(data.NextCrowning);
                    data.NextCrowning = NextCrowning(data.NextCrowning.AddMinutes(1));
                    CrownChampion(key, now);
                    Ok(player, "AdminCrowned", ClockText(data.NextCrowning), DayText(data.NextCrowning));
                    SaveData();
                    return;
                }
                case "pairs":
                {
                    var list = new List<KeyValuePair<string, PairRec>>(data.Pairs);
                    list.Sort(delegate(KeyValuePair<string, PairRec> a, KeyValuePair<string, PairRec> b) { return b.Value.Ranked.Count.CompareTo(a.Value.Ranked.Count); });
                    Reply(player, "PairsHeader");
                    for (int i = 0; i < list.Count && i < 8; i++)
                    {
                        if (list[i].Value.Ranked.Count == 0) break;
                        string[] ids = list[i].Key.Split('|');
                        Line(player, "PairsLine", NameOf(ids[0]), NameOf(ids.Length > 1 ? ids[1] : ""), list[i].Value.Ranked.Count, list[i].Value.Wagered, list[i].Value.TavernGames);
                    }
                    return;
                }
                case "settle":
                    ProcessSettlements();
                    Ok(player, "AdminSettled", data.Settlements.Count);
                    SaveData();
                    return;
                default:
                    Line(player, "ArenaHelpAdmin1");
                    Line(player, "ArenaHelpAdmin2");
                    return;
            }
        }

        // /arena admin zone|tavern set <name> [radius] (stand in the middle) | remove <name>
        private void ZoneCommand(Player player, string[] args, List<Zone> zones)
        {
            string verb = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            if (verb == "set" && args.Length > 3)
            {
                float radius = zones == config.Arenas ? 20f : 10f;
                int last = args.Length - 1;
                float parsed;
                if (last > 3 && float.TryParse(args[last], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) { radius = parsed; last--; }
                string name = CleanName(JoinRange(args, 3, last));
                if (name.Length == 0 || player.Entity == null) { Error(player, "ZoneUsage"); return; }
                Vector3 pos = player.Entity.Position;
                zones.RemoveAll(delegate(Zone z) { return string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase); });
                zones.Add(new Zone { Name = name, X = pos.x, Y = pos.y, Z = pos.z, Radius = ClampF(radius, zones == config.Arenas ? 5f : 2f, 200f) });
                Config.WriteObject(config, true);
                Ok(player, "ZoneSet", name, (int)pos.x, (int)pos.z, (int)ClampF(radius, 2f, 200f));
                return;
            }
            if (verb == "remove" && args.Length > 3)
            {
                string name = JoinFrom(args, 3);
                int n = zones.RemoveAll(delegate(Zone z) { return string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase); });
                if (n == 0) { Error(player, "ZoneNotFound", CleanName(name)); return; }
                Config.WriteObject(config, true);
                Ok(player, "ZoneRemoved", CleanName(name));
                return;
            }
            Error(player, "ZoneUsage");
        }

        [ChatCommand("dice")]
        private void CmdDice(Player player, string command, string[] args)
        {
            if (!Ready(player)) return;
            try
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
                switch (sub)
                {
                    case "help": TavernHelp(player, KDice); return;
                    case "roll": RollCommand(player, args); return;
                    case "accept": AcceptCommand(player, args, KDice); return;
                    case "decline": DeclineCommand(player, args, KDice); return;
                    case "cancel": CancelOutgoing(player, KDice); return;
                    default: ChallengeTavern(player, KDice, args, 0); return;
                }
            }
            catch (Exception ex) { PrintError("/dice failed: " + ex); TavernError(player, "Failed"); }
        }

        [ChatCommand("cards")]
        private void CmdCards(Player player, string command, string[] args)
        {
            if (!Ready(player)) return;
            try
            {
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
                switch (sub)
                {
                    case "help": TavernHelp(player, KCards); return;
                    case "hit": case "stand": case "hand": CardsCommand(player, sub); return;
                    case "accept": AcceptCommand(player, args, KCards); return;
                    case "decline": DeclineCommand(player, args, KCards); return;
                    case "cancel": CancelOutgoing(player, KCards); return;
                    default: ChallengeTavern(player, KCards, args, 0); return;
                }
            }
            catch (Exception ex) { PrintError("/cards failed: " + ex); TavernError(player, "Failed"); }
        }

        private void TavernHelp(Player player, string kind)
        {
            if (kind == KDice)
            {
                TavernSay(player, ChatGold, "DiceHelp1");
                Line(player, "DiceHelp2");
                Line(player, "DiceHelp3");
            }
            else
            {
                TavernSay(player, ChatGold, "CardsHelp1");
                Line(player, "CardsHelp2");
                Line(player, "CardsHelp3");
            }
            Line(player, "TavernLimits", config.Tavern.MinStake, config.Tavern.MaxStake, config.Tavern.DailyStakeLimit, config.Tavern.DailyLossLimit);
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // Whether the player is in a duel (gathering, counting down or fighting).
        private bool IsInDuel(string playerId)
        {
            return data != null && playerId != null && DuelOf(playerId) != null;
        }

        // Whether a blow from attacker to victim is a sanctioned duel blow: two foes in one fight. RealmLaws asks this so a
        // peace law does not stop a duel fought in its zone.
        private bool IsDuelBlow(string attackerId, string victimId)
        {
            if (data == null || attackerId == null || victimId == null) return false;
            Duel d = DuelOf(attackerId);
            if (d == null || d.State != SFight || DuelOf(victimId) != d) return false;
            Member a = MemberOf(d, attackerId), v = MemberOf(d, victimId);
            return !a.Out && !v.Out && a.Side != v.Side;
        }

        // RealmLaws: a trial by combat fought in the ring (to the first fall, no death, no loot). The winner is reported
        // back with RealmLaws' ArenaTrialResult. Returns false when the ring cannot take it (a fighter offline or busy);
        // the court then keeps its own rules.
        private bool StageTrial(string caseId, string accusedId, string accusedName, string championId, string championName, int minutes)
        {
            if (loadFailed || data == null || !config.Trials.Enabled || string.IsNullOrEmpty(caseId) || accusedId == championId) return false;
            Player a = OnlineById(accusedId), c = OnlineById(championId);
            if (a == null || c == null) return false;
            if (DuelOf(accusedId) != null || DuelOf(championId) != null || GameOf(accusedId) != null || GameOf(championId) != null) return false;
            foreach (Challenge ch in data.Challenges.ToArray())
                if (Involves(ch, accusedId) || Involves(ch, championId)) DropChallenge(ch, "ChallengeGone", "");
            DateTime now = Now();
            minutes = Clamp(minutes, 1, 1440);
            var d = new Duel { Id = data.NextId++, Kind = KTrial, State = SGather, Created = now, GatherEnds = now.AddMinutes(minutes), TrialCase = caseId, Unranked = "RankTrial" };
            d.Members.Add(new Member { Id = accusedId, Name = CleanName(accusedName ?? a.Name), Side = 0, Accepted = true });
            d.Members.Add(new Member { Id = championId, Name = CleanName(championName ?? c.Name), Side = 1, Accepted = true });
            data.Duels.Add(d);
            foreach (Member m in d.Members)
            {
                Player p = OnlineById(m.Id);
                if (p != null) Warn(p, "TrialStaged", caseId, Others(d, m), minutes);
            }
            BeginGathering(d);
            SaveData();
            return true;
        }

        private int GetArenaRating(string playerId)
        {
            Fighter f = data != null ? GetFighter(playerId, null, false) : null;
            return f != null ? f.Rating : 0;
        }

        private string GetArenaChampion()
        {
            return data != null && data.Champion != null ? data.Champion.Name : null;
        }

        // The top of the one-against-one ladder as "name|rating|wins|losses" (for boards and pages).
        private string[] GetArenaLadder(int count)
        {
            var list = new List<string>();
            if (data == null) return list.ToArray();
            List<KeyValuePair<string, Fighter>> l = Ladder(false);
            for (int i = 0; i < l.Count && i < Clamp(count, 1, 25); i++)
                list.Add(l[i].Value.Name + "|" + l[i].Value.Rating + "|" + l[i].Value.Wins + "|" + l[i].Value.Losses);
            return list.ToArray();
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): the game's own windows, opened with the Player extension
        // methods in CodeHatch.Common.PlayerExtensions. Signatures read from the 2.0.3867 Assembly-CSharp.dll metadata:
        //   MessageDialogue ShowPopup(this Player, string title, string message, string buttonText = "Ok",
        //       Dialogue.OnSubmit handler = null, bool interupt = false, bool broadcast = true)
        //   void ShowConfirmPopup(this Player, string title, string message, string confirmText = "Confirm",
        //       string cancelText = "Cancel", Dialogue.OnSubmit handler = null, bool interupt = false, bool broadcast = true)
        //   delegate void Dialogue.OnSubmit(Options selection, Dialogue dialogue, object contextData)
        // An answer is only a request: it must match the one question open for that player (token, kind, deadline), it
        // is used once, and the challenge it names (by id: a re-issued challenge has a new id) then runs the same checks
        // as the chat answer. Every window has its chat fallback. UNVERIFIED in game: that the windows show and answer.
        private class PopupAsk
        {
            public int Token;
            public string Kind;
            public string Data;
            public DateTime Expires;
        }

        private readonly Dictionary<string, PopupAsk> popupAsks = new Dictionary<string, PopupAsk>();
        private int popupToken;
        private bool popupsClosed;                              // set on Unload: answers to windows still open are ignored

        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.UsePopups || popupsClosed) return false;
            if (RealmHerald == null) return true;
            object wanted = RealmHerald.Call("PopupsWanted", player.Id.ToString());
            return !(wanted is bool) || (bool)wanted;
        }

        private bool AskYesNo(Player player, string kind, string data, int seconds, string title, string message, string yes, string no,
            Action<Player, PopupAsk, bool, string> answer)
        {
            if (!PopupsFor(player)) return false;
            string id = player.Id.ToString();
            var ask = new PopupAsk { Token = ++popupToken, Kind = kind, Data = data, Expires = Now().AddSeconds(Math.Max(10, seconds)) };
            popupAsks[id] = ask;
            try
            {
                player.ShowConfirmPopup(PopupText(title), PopupText(message), PopupText(yes), PopupText(no), Answered(id, ask.Token, kind, answer), false, true);
                return true;
            }
            catch (Exception ex)
            {
                popupAsks.Remove(id);
                PrintWarning("ShowConfirmPopup failed (" + ex.Message + "); the chat answer stands.");
                return false;
            }
        }

        private void Notice(Player player, string title, string message)
        {
            if (!PopupsFor(player)) return;
            try
            {
                player.ShowPopup(PopupText(title), PopupText(message), PopupText(Msg("PopupOk", player)), null, false, true);
            }
            catch (Exception ex)
            {
                PrintWarning("ShowPopup failed (" + ex.Message + "); the chat line stands.");
            }
        }

        private PopupAsk TakeAsk(string id, int token, string kind)
        {
            PopupAsk ask;
            if (popupsClosed || !popupAsks.TryGetValue(id, out ask) || ask.Token != token || ask.Kind != kind) return null;
            popupAsks.Remove(id);
            return ask.Expires >= Now() ? ask : null;
        }

        private void DropAsk(string id, string kind)
        {
            PopupAsk ask;
            if (popupAsks.TryGetValue(id, out ask) && ask.Kind == kind) popupAsks.Remove(id);
        }

        // The game calls this when the window is answered. Never throws back into the game.
        private Dialogue.OnSubmit Answered(string id, int token, string kind, Action<Player, PopupAsk, bool, string> answer)
        {
            return delegate(Options selection, Dialogue dialogue, object context)
            {
                try
                {
                    if (loadFailed || data == null) return;
                    PopupAsk ask = TakeAsk(id, token, kind);
                    if (ask == null) return;
                    Player player = OnlineById(id);
                    if (player == null) return;
                    answer(player, ask, selection == Options.Yes, dialogue != null ? dialogue.ValueMessage : null);
                }
                catch (Exception ex)
                {
                    PrintError("Popup answer (" + kind + ") failed: " + ex.Message);
                }
            };
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

        #region Treasury: stakes in escrow

        private bool TryPurse(string id, out long purse)
        {
            purse = 0;
            if (RealmTreasury == null) return false;
            object r;
            try { r = RealmTreasury.Call("GetPurse", id); }
            catch (Exception ex) { PrintWarning("RealmTreasury GetPurse failed: " + ex.Message); return false; }
            if (!(r is long)) return false;
            purse = (long)r;
            return true;
        }

        // All or nothing: the stake is held whole, or not at all.
        private bool HoldStake(int refId, Member m, long amount, int minutes)
        {
            if (amount <= 0) return true;
            if (RealmTreasury == null) return false;
            // Unique even if the arena's data were ever reset: the ids restart, the clock does not.
            string holdId = "arena:" + refId + ":" + m.Id + ":" + Now().ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
            object r;
            try { r = RealmTreasury.Call("HoldMarks", holdId, m.Id, m.Name, amount, HoldSource, minutes); }
            catch (Exception ex) { PrintWarning("RealmTreasury HoldMarks failed: " + ex.Message); return false; }
            long held = r is long ? (long)r : 0;
            if (held != amount)
            {
                if (held > 0) { var s = new Settlement { Ref = "partial hold " + holdId, Created = Now() }; s.Releases.Add(holdId); QueueSettlement(s); }
                return false;
            }
            m.HoldId = holdId;
            m.Held = amount;
            return true;
        }

        private int HoldMinutesFor(string kind)
        {
            DuelSettings u = config.Duels;
            if (kind == KDuel) return (u.ChallengeSeconds + u.GatherSeconds + u.CountdownSeconds) / 60 + u.MaxDuelMinutes + 30;
            if (kind == KTeam) return (config.Teams.ChallengeSeconds + u.GatherSeconds + u.CountdownSeconds) / 60 + u.MaxDuelMinutes + 30;
            if (kind == KDice) return config.Tavern.ChallengeSeconds / 60 + 30;
            if (kind == KCards) return config.Tavern.ChallengeSeconds / 60 + config.Tavern.CardTurnSeconds * 12 / 60 + 30;
            return config.Tournament.SignupMinutes + config.Tournament.MatchMinutes * 8 + 240;
        }

        // Equal shares of every stake for the winners (the winner's own stake comes back from its own hold first).
        private Settlement Payout(string reference, List<Member> payers, List<Member> winners)
        {
            long total = 0;
            var seen = new HashSet<string>();
            foreach (Member m in payers) if (m.Held > 0 && m.HoldId != null && seen.Add(m.HoldId)) total += m.Held;
            var shares = new List<KeyValuePair<Member, long>>();
            if (winners.Count > 0)
            {
                long share = total / winners.Count, rest = total % winners.Count;
                for (int i = 0; i < winners.Count; i++) shares.Add(new KeyValuePair<Member, long>(winners[i], share + (i < rest ? 1 : 0)));
            }
            return PayoutShares(reference, payers, shares);
        }

        private Settlement PayoutShares(string reference, List<Member> payers, List<KeyValuePair<Member, long>> shares)
        {
            var s = new Settlement { Ref = reference, Created = Now() };
            var left = new Dictionary<string, long>();
            var holds = new List<Member>();
            foreach (Member m in payers)
                if (m.Held > 0 && m.HoldId != null && !left.ContainsKey(m.HoldId)) { left[m.HoldId] = m.Held; holds.Add(m); }
            var owed = new long[shares.Count];
            for (int i = 0; i < shares.Count; i++) owed[i] = Math.Max(0, shares[i].Value);
            for (int i = 0; i < shares.Count; i++)
                foreach (Member h in holds)
                    if (h.Id == shares[i].Key.Id) MoveShare(s, h.HoldId, shares[i].Key, left, ref owed[i]);
            for (int i = 0; i < shares.Count; i++)
                foreach (Member h in holds) MoveShare(s, h.HoldId, shares[i].Key, left, ref owed[i]);
            foreach (Member h in holds) if (left[h.HoldId] > 0) s.Releases.Add(h.HoldId);   // unclaimed: back to whoever staked it
            return s;
        }

        private static void MoveShare(Settlement s, string holdId, Member to, Dictionary<string, long> left, ref long owed)
        {
            long take = Math.Min(left[holdId], owed);
            if (take <= 0) return;
            left[holdId] -= take;
            owed -= take;
            s.Transfers.Add(new Transfer { HoldId = holdId, ToId = to.Id, ToName = to.Name, Amount = take });
        }

        // Written down first, then paid: a crash between the two leaves the settlement to be retried, never paid twice
        // (a hold can never pay out more than it holds).
        private void QueueSettlement(Settlement s)
        {
            if (s == null || (s.Transfers.Count == 0 && s.Releases.Count == 0)) return;
            data.Settlements.Add(s);
            SaveData();
            ProcessSettlements();
        }

        private void ProcessSettlements()
        {
            if (data == null || data.Settlements.Count == 0 || RealmTreasury == null) return;
            foreach (Settlement s in data.Settlements.ToArray())
            {
                bool pending = false;
                foreach (Transfer t in s.Transfers)
                {
                    if (t.Done) continue;
                    object r;
                    try { r = RealmTreasury.Call("PayFromHold", t.HoldId, t.ToId, t.ToName, t.Amount, HoldSource); }
                    catch (Exception ex) { PrintWarning("RealmTreasury PayFromHold failed: " + ex.Message); r = null; }
                    if (!(r is long)) { pending = true; break; }
                    long paid = (long)r;
                    if (paid < t.Amount) PrintWarning("Settlement " + s.Ref + ": " + t.HoldId + " paid " + paid + " of " + t.Amount + " (the rest had lapsed back to its owner).");
                    data.PotPaid += paid;
                    t.Done = true;
                    dirty = true;
                }
                if (!pending)
                    foreach (string h in s.Releases.ToArray())
                    {
                        object r;
                        try { r = RealmTreasury.Call("ReleaseHold", h, HoldSource); }
                        catch (Exception ex) { PrintWarning("RealmTreasury ReleaseHold failed: " + ex.Message); r = null; }
                        if (!(r is long)) { pending = true; break; }
                        s.Releases.Remove(h);
                        dirty = true;
                    }
                if (!pending) { data.Settlements.Remove(s); dirty = true; }
                else s.Tries++;
            }
        }

        #endregion

        #region Other Realm plugins

        private bool IsProtected(Player p)
        {
            if (RealmWarden == null || p == null) return false;
            object r = RealmWarden.Call("IsNewPlayerProtected", p.Id);
            return r is bool && (bool)r;
        }

        private bool IsFrozen(Player p)
        {
            if (RealmSentinel == null || p == null) return false;
            object r = RealmSentinel.Call("IsSentinelFrozen", p.Id);
            return r is bool && (bool)r;
        }

        private bool TruceActive()
        {
            if (RealmEvents == null) return false;
            object r = RealmEvents.Call("IsTruceActive");
            return r is bool && (bool)r;
        }

        private bool IsBearer(string id)
        {
            if (RealmLegendary == null || id == null) return false;
            object r = RealmLegendary.Call("IsBearer", id);
            return r is bool && (bool)r;
        }

        private string HouseOfId(string id)
        {
            if (RealmHouses == null || id == null) return null;
            string h = RealmHouses.Call("GetHouse", id) as string;
            return string.IsNullOrEmpty(h) ? null : h;
        }

        // Liege and vassal either way, or a treaty (RealmHouses), as RealmEvents counts allies.
        private bool Allied(string a, string b)
        {
            if (a == null || b == null || RealmHouses == null || SameText(a, b)) return false;
            if (SameText(RealmHouses.Call("GetLiege", a) as string, b) || SameText(RealmHouses.Call("GetLiege", b) as string, a)) return true;
            object r = RealmHouses.Call("HasTreaty", a, b);
            return r is bool && (bool)r;
        }

        private void AwardHouse(string house, int points, string honour)
        {
            if (RealmSeasons == null || house == null || points == 0) return;
            try { RealmSeasons.Call("AwardHouse", house, points, honour); }
            catch (Exception ex) { PrintWarning("RealmSeasons AwardHouse failed: " + ex.Message); }
        }

        private void AddDeed(string id, string name, string kind, string note, string key)
        {
            if (RealmRenown == null || string.IsNullOrEmpty(kind)) return;
            try { RealmRenown.Call("AddDeed", id, name, kind, note, key); }
            catch (Exception ex) { PrintWarning("RealmRenown AddDeed failed: " + ex.Message); }
        }

        private void RaiseAlert(string kind, string id, string detail)
        {
            if (RealmWarden == null) return;
            ulong u;
            if (!ulong.TryParse(id, out u)) return;
            try { RealmWarden.Call("RaiseWardenAlert", kind, u, detail); }
            catch (Exception ex) { PrintWarning("RealmWarden alert failed: " + ex.Message); }
        }

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title);
            if (RealmChronicle == null) return;
            try { RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]); }
            catch (Exception ex) { PrintWarning("RealmChronicle Log failed: " + ex.Message); }
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

        private static readonly RandomNumberGenerator Crypto = RandomNumberGenerator.Create();      // mscorlib, System.Security.Cryptography

        // Uniform 0..n-1 from the crypto generator, without modulo bias.
        private static int CryptoNext(int n)
        {
            if (n <= 1) return 0;
            var b = new byte[4];
            uint limit = uint.MaxValue - uint.MaxValue % (uint)n;
            while (true)
            {
                lock (Crypto) Crypto.GetBytes(b);
                uint v = BitConverter.ToUInt32(b, 0);
                if (v < limit) return (int)(v % (uint)n);
            }
        }

        private Fighter GetFighter(string id, string name, bool create)
        {
            if (data == null || !IsSteamId(id)) return null;
            Fighter f;
            if (!data.Fighters.TryGetValue(id, out f))
            {
                if (!create) return null;
                DateTime now = Now();
                f = new Fighter { Name = CleanName(name ?? id), Rating = config.Ranked.StartRating, TeamRating = config.Ranked.StartRating, FirstSeen = now, LastSeen = now };
                data.Fighters[id] = f;
                dirty = true;
            }
            else if (name != null)
            {
                string clean = CleanName(name);
                if (clean != f.Name && clean != "?") { f.Name = clean; dirty = true; }
            }
            return f;
        }

        private PairRec GetPair(string a, string b, bool create)
        {
            string key = PairKey(a, b);
            PairRec p;
            if (data.Pairs.TryGetValue(key, out p)) return p;
            if (!create) return null;
            p = new PairRec { LastUsed = Now() };
            data.Pairs[key] = p;
            return p;
        }

        private static string PairKey(string a, string b)
        {
            return string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
        }

        private static string DayKey(DateTime t)
        {
            return t.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);
        }

        private static void RollDay(Fighter f, DateTime now)
        {
            string key = DayKey(now);
            if (f.DayKey == key) return;
            f.DayKey = key;
            f.GainToday = 0;
            f.WagerToday = 0;
            f.TavernStaked = 0;
            f.TavernWon = 0;
            f.TavernLost = 0;
        }

        private static void RollPairDay(PairRec p, DateTime now)
        {
            string key = DayKey(now);
            if (p.DayKey == key) return;
            p.DayKey = key;
            p.Wagered = 0;
            p.TavernGames = 0;
        }

        private Duel DuelOf(string id)
        {
            foreach (Duel d in data.Duels) foreach (Member m in d.Members) if (m.Id == id) return d;
            return null;
        }

        private static Member MemberOf(Duel d, string id)
        {
            if (d == null) return null;
            foreach (Member m in d.Members) if (m.Id == id) return m;
            return null;
        }

        private Duel DuelById(int id)
        {
            foreach (Duel d in data.Duels) if (d.Id == id) return d;
            return null;
        }

        private bool InRing(string id)
        {
            Duel d = DuelOf(id);
            return d != null && (d.State == SFight || d.State == SCountdown);
        }

        private bool Shielded(string id, DateTime now)
        {
            DateTime until;
            return shieldUntil.TryGetValue(id, out until) && until > now;
        }

        private bool InCombat(string id, DateTime now)
        {
            DateTime last;
            return config.Duels.CombatTagSeconds > 0 && lastPvp.TryGetValue(id, out last) && (now - last).TotalSeconds < config.Duels.CombatTagSeconds;
        }

        private DateTime BarredOf(string id)
        {
            Fighter f = GetFighter(id, null, false);
            return f != null ? f.BarredUntil : DateTime.MinValue;
        }

        private static List<Player> Online()
        {
            var list = new List<Player>();
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private static Player OnlineById(string id)
        {
            ulong u;
            if (id == null || !ulong.TryParse(id, out u)) return null;
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer && p.Id == u) return p;
            return null;
        }

        private static Player FindOnline(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string q = name.Trim();
            Player hit = null;
            int partial = 0;
            foreach (Player p in Online())
            {
                if (string.Equals(p.Name, q, StringComparison.OrdinalIgnoreCase) || p.Id.ToString() == q) return p;
                if (p.Name != null && p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { hit = p; partial++; }
            }
            return partial == 1 ? hit : null;
        }

        private string FindFighterId(string who)
        {
            if (string.IsNullOrEmpty(who)) return null;
            Player p = FindOnline(who);
            if (p != null) return p.Id.ToString();
            string q = who.Trim();
            if (data.Fighters.ContainsKey(q)) return q;
            foreach (KeyValuePair<string, Fighter> kv in data.Fighters) if (string.Equals(kv.Value.Name, q, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        private string NameOf(string id)
        {
            Fighter f = GetFighter(id, null, false);
            return f != null ? f.Name : id;
        }

        private static bool NameMatches(string name, string query)
        {
            if (name == null || query == null) return false;
            string q = query.Trim();
            return string.Equals(name, q, StringComparison.OrdinalIgnoreCase) || name.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameText(string a, string b)
        {
            return a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSteamId(string s)
        {
            if (s == null || s.Length != 17) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        // Names go into chat lines as arguments only; colour brackets and control characters are taken out first.
        private static string CleanName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            var sb = new StringBuilder();
            foreach (char c in s) if (c != '[' && c != ']' && c != '{' && c != '}' && !char.IsControl(c)) sb.Append(c);
            string r = sb.ToString().Trim();
            if (r.Length > 32) r = r.Substring(0, 32);
            return r.Length == 0 ? "?" : r;
        }

        private bool IsAlive(Player p)
        {
            try { return p.IsAlive(); }                               // PlayerExtensions.IsAlive [ASM]
            catch (Exception) { return true; }
        }

        private PlayerHealth HealthOf(Player p)
        {
            try { return p.GetHealth(); }                             // PlayerExtensions.GetHealth [ASM]
            catch (Exception) { return null; }
        }

        private static bool Inside(Vector3 pos, float x, float z, float r)
        {
            float dx = pos.x - x, dz = pos.z - z;
            return dx * dx + dz * dz <= r * r;
        }

        private Zone ArenaAt(Player p)
        {
            if (p == null || p.Entity == null) return null;
            foreach (Zone z in config.Arenas) if (Inside(p.Entity.Position, z.X, z.Z, z.Radius)) return z;
            return null;
        }

        private Zone TavernAt(Player p)
        {
            if (p == null || p.Entity == null) return null;
            foreach (Zone z in config.Taverns) if (Inside(p.Entity.Position, z.X, z.Z, z.Radius)) return z;
            return null;
        }

        private string Until(DateTime t)
        {
            if (t == DateTime.MinValue) return "-";
            return (DayKey(t) == DayKey(Now()) ? t.ToString("HH':'mm", CultureInfo.InvariantCulture) : t.ToString("MM'-'dd HH':'mm", CultureInfo.InvariantCulture)) + " UTC";
        }

        private static string ClockText(DateTime t)
        {
            return t.ToString("HH':'mm", CultureInfo.InvariantCulture);
        }

        private static string DayText(DateTime t)
        {
            return t.DayOfWeek.ToString();
        }

        private static int Secs(TimeSpan span)
        {
            return Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
        }

        private int SecondsLeft(DateTime t)
        {
            return Math.Max(0, (int)Math.Ceiling((t - Now()).TotalSeconds));
        }

        // True at most once per `seconds` for this key.
        private bool Throttle(string key, int seconds)
        {
            DateTime now = Now(), last;
            if (lastNotice.TryGetValue(key, out last) && (now - last).TotalSeconds < seconds) return false;
            lastNotice[key] = now;
            return true;
        }

        private void NoticeOnce(Player p, string key, params object[] args)
        {
            if (p != null && Throttle("n:" + p.Id + ":" + key, 4)) Error(p, key, args);
        }

        private static string JoinFrom(string[] args, int start)
        {
            return JoinRange(args, start, args.Length - 1);
        }

        private static string JoinRange(string[] args, int first, int last)
        {
            if (first > last || first >= args.Length) return "";
            var parts = new List<string>();
            for (int i = first; i <= last && i < args.Length; i++) parts.Add(args[i]);
            return string.Join(" ", parts.ToArray()).Trim();
        }

        private string TourneyName(Tourney t, Player p)
        {
            return Msg(t.Kind == TRoyal ? "RoyalBracketName" : "TourneyName", p);
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        #endregion
    }
}
