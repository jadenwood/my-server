// RealmLaws: the laws of Ostreval and the crown's court.
//
// The reigning monarch proclaims up to MaxActiveLaws laws from a catalogue kept in the config. A law is either
//   ENFORCED   its breach is detected by a verified hook or verified polling and written to the crime ledger;
//              some kinds can also block the act outright ("Block": true), or
//   DECLARED   nothing in the game can observe the breach (banned weapons, tolls), so it is only a crime when the
//              crown or its council accuses someone of it in court.
// Enforced kinds and the APIs they rest on (tags as in docs/oxide-rok-api.md):
//   peace        no player may wound another inside a zone. OnEntityHealthChange(EntityDamageEvent) [OPJ L162];
//                victim = evt.Entity.Owner, attacker = evt.Damage.DamageSource.Owner [ASM]; block with
//                evt.Cancel() + Damage.Amount = 0 + return true [USE NoFriendlyFire.cs:116-117]. [IL] EntityHealth.
//                InvokeDamage calls the hook before invoking its OnDamage handler and returns early on non-null.
//   curfew       no one may stand inside a zone during the curfew hours (UTC). Polling of Server.ClientPlayers and
//                Entity.Position every ZoneCheckSeconds [ASM]. Recorded after a warning and a grace period.
//   no_building  no cube may be placed inside a zone. OnCubePlacement(CubePlaceEvent) [OPJ L266], block with
//                evt.Cancel() [USE LevelSystem.cs:2072]. [IL] the game's own CubeListener.OnCubePlace identifies the
//                placer by NetworkEvent.SenderId, reads Cancelled after the hook, and maps the cube to world space with
//                evt.Grid.LocalToWorldCoordinate(evt.Position); this plugin uses the same calls.
//   no_capture   no one may be roped, chained or caged inside a zone. OnPlayerCapture(PlayerCaptureEvent) [OPJ L711],
//                captor = evt.Captor.Owner, target = evt.Target [ASM]. Records by default; "Block": true cancels.
// Zones are circles on the X/Z plane, set in config or by an admin standing in the place (/law zone set).
// UNVERIFIED: that Entity.Position is in the same world units an admin's own position reports (it is the same
// property for both, so a zone set with /law zone set is self-consistent).
//
// The court: /court accuse (monarch or council, with quotas), /court trial (a jury of online sworn lords: heads of
// houses not party to the case), /court verdict (jurors vote, secret ballot, majority, tie acquits), /court combat
// (trial by combat: a timed window; the outcome is read only from the verified death hook OnEntityDeath
// [OPJ L188; IL], victim = evt.Entity.Owner, killer = evt.KillingDamage.DamageSource.Owner [ASM; USE
// DeathMessages.cs:20]). Sentences: fine (real items taken with ItemCollection.AutoCount/AutoSplit, measured before
// and after, as in RealmContracts [IL StationListener.OnStationUpgradeRequest]), outlawry (held here; also offered
// to RealmContracts via Call("ProclaimOutlaw", ...) so court outlaws become bounty targets; null if it is not loaded),
// and exile (a timer; an exile found in a town zone past a warning is outlawed).
//
// Abuse limits: active-law cap, proclamation cooldown and daily change cap; crown and per-accuser daily accusation
// quotas, a per-target weekly cap, one open case per target, immunity after acquittal, and each acquittal costs the
// crown accusation quota; capped sentences; a jury the crown cannot pick (its own house is excluded by default);
// pardons capped per day; laws lapse and crown cases are dismissed when the crown changes hands. Jurors' houses need
// JurorHouseMinMembers members and may not be liege, vassal or treaty partner of the accused's house; an accused who
// stays offline may be tried in absence after TrialInAbsentiaAfterHours; the crime ledger's size cap never drops the
// evidence of an open case; an unpaid-fine clock only runs once the convict has been online since the sentence.
//
// Data: oxide/data/RealmLaws.json. If it exists but cannot be parsed the plugin refuses to run its court and never
// writes the file, so a damaged record is not overwritten (fix or remove it, then reload).
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using CodeHatch.Blocks.Networking.Events;        // CubePlaceEvent [ASM]
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError, GetInventory, GetGuild [ASM]
using CodeHatch.Damaging;                        // Damage [ASM]
using CodeHatch.Engine.Core.Cache;               // Entity [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;            // InvItemBlueprint [ASM]
using CodeHatch.Inventory.Blueprints.Components; // ContainerManagement [ASM; IL ThronesCommandHandler.Give]
using CodeHatch.ItemContainer;                   // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events;               // PlayerCaptureEvent [ASM]
using CodeHatch.Networking.Events.Entities;      // EntityDamageEvent, EntityDeathEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // Guild, KingsScheme [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]
using UnityEngine;                               // Vector3 (Entity.Position) [ASM]

namespace Oxide.Plugins
{
    [Info("RealmLaws", "Realm", "0.1.0")]
    [Description("Laws proclaimed by the crown, a crime ledger fed by verified hooks, and a court with jury, trial by combat and capped sentences")]
    public class RealmLaws : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmContracts;

        private const string PermAdmin = "realmlaws.admin";
        private const string DataName = "RealmLaws";
        private const float TickSeconds = 10f;

        private const string KPeace = "peace";
        private const string KCurfew = "curfew";
        private const string KNoBuilding = "no_building";
        private const string KNoCapture = "no_capture";
        private const string KDeclared = "declared";

        private const string ExileLawId = "exile_breach";      // pseudo-law: breaking a sentence of exile

        private const string CAccused = "accused";            // waiting for trial
        private const string CTrial = "trial";                // jury sitting
        private const string CCombat = "combat";              // trial by combat window open
        private const string CGuilty = "guilty";
        private const string CAcquitted = "acquitted";
        private const string CDismissed = "dismissed";
        private const string CExpired = "expired";

        private const string SFine = "fine";
        private const string SOutlaw = "outlaw";
        private const string SExile = "exile";

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private bool dirty;
        private float zoneAccumulator;
        private readonly System.Random rng = new System.Random();

        // In-memory only: throttles and warnings (losing them on reload is harmless).
        private readonly Dictionary<string, DateTime> lastCrimeAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> zoneWarnedAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> lastNotice = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, bool> chronicleTypeAccepted = new Dictionary<string, bool>();
        private readonly Queue<DateTime> chronicleTimes = new Queue<DateTime>();

        // Rebuilt whenever laws or zones change, so the damage hook stays cheap.
        private readonly List<LawDef> activeEnforced = new List<LawDef>();
        private bool anyPeace, anyCurfew, anyNoBuilding, anyNoCapture;

        #region Config

        private class ZoneDef
        {
            public string Name;
            public float X;
            public float Z;
            public float Radius;
            public bool Town;                  // "*" in a law means every town zone; exiles may not enter towns
        }

        private class LawDef
        {
            public string Id;
            public string Name;
            public string Text;
            public string Kind;                // peace | curfew | no_building | no_capture | declared
            public string Zone;                // zone name, or "*" for every town zone (unused for declared)
            public int CurfewStartHourUtc;
            public int CurfewEndHourUtc;
            public bool Block;                 // peace / no_building / no_capture: also prevent the act
            public string DefaultSentence;     // fine | outlaw | exile (used when the accuser names none)
            public int DefaultAmount;          // fine units or hours
            public string DefaultItem;         // fine item
        }

        private class PluginConfig
        {
            // laws
            public int MaxActiveLaws = 3;
            public int LawProclaimCooldownMinutes = 30;
            public int LawChangesPerDay = 4;
            public int LawGraceMinutes = 5;            // a proclaimed law binds only after the heralds have cried it
            public bool LawsLapseOnSuccession = true;
            public bool PeaceSuspendedDuringRebellion = true;
            public bool OutlawsAndExilesLosePeace = true;
            public bool CrownHouseMayBuildInZones = true;
            public int ZoneCheckSeconds = 10;
            public int CurfewGraceSeconds = 60;
            public int CrimeCooldownSeconds = 120;     // per offender and law: one brawl is one record
            public int CrimeRecordDays = 7;
            public int MaxCrimeRecords = 500;
            // accusations
            public bool CouncilMayAccuse = true;
            public bool MonarchImmune = true;
            public int MaxOpenCases = 20;
            public int CrownAccusationsPerDay = 6;
            public int AccusationsPerAccuserPerDay = 3;
            public int AccusationsPerTargetPerWeek = 2;
            public int AcquittalImmunityHours = 24;
            public int AcquittalPenalty = 1;           // each acquittal in the last 24 h counts as this many accusations
            public bool RequireEvidenceForEnforcedLaws = false;
            public int CaseExpiryHours = 48;
            public bool DismissCrownCasesOnSuccession = true;
            public int MaxClosedCases = 100;
            // jury
            public int JurySize = 5;
            public int JuryMin = 3;
            public int MinVotes = 2;
            public int TrialMinutes = 10;
            public int MaxTrialAttempts = 2;
            public bool JurorsMustLeadHouse = true;
            public bool ExcludeCrownHouseFromJury = true;
            public bool TrialRequiresAccusedOnline = true;
            public int TrialInAbsentiaAfterHours = 24;  // an accused who stays away this long after the charge may be tried
                                                        // without being online (0 = never); stops dodging every trial
            public int JurorHouseMinMembers = 2;        // a juror's house needs this many members (one-man alt houses
                                                        // cannot pack the jury for either side; 0 or 1 = any house)
            public bool ExcludeAlliedJurors = true;     // no juror from a house that is liege, vassal or treaty partner of
                                                        // the accused's house
            // trial by combat
            public bool TrialByCombat = true;
            public int CombatWindowMinutes = 15;
            public string CombatTimeoutResult = "jury"; // jury | acquit | guilty
            public bool CombatFleeLoses = true;         // a duellist who disconnects in the window loses
            // sentences
            public int MaxFineAmount = 200;
            public string FineDestination = "burn";     // burn | victim
            public int FinePayHours = 24;
            public int UnpaidFineOutlawHours = 12;      // 0 = an unpaid fine simply stays on the record
            public int MaxOutlawHours = 48;
            public int MaxExileHours = 48;
            public int ExileBreachGraceSeconds = 60;
            public int ExileBreachOutlawHours = 24;
            public bool OfferOutlawryToContracts = true;
            public int PardonsPerDay = 2;
            // output
            public int ChronicleMaxPerHour = 15;
            public int MaxListLines = 12;
            public List<string> AllowedFineItems;       // empty = any item
            public List<ZoneDef> Zones;
            public List<LawDef> Catalogue;
        }

        protected override void LoadDefaultConfig()
        {
            var c = new PluginConfig();
            c.AllowedFineItems = new List<string>();
            c.Zones = DefaultZones();
            c.Catalogue = DefaultCatalogue();
            Config.WriteObject(c, true);
        }

        // Coordinates are PLACEHOLDERS: stand in the place and run /law zone set <name> <radius> [town].
        private static List<ZoneDef> DefaultZones()
        {
            return new List<ZoneDef>
            {
                new ZoneDef { Name = "Crown Market", X = 0f, Z = 0f, Radius = 60f, Town = true },
                new ZoneDef { Name = "Hearth", X = 0f, Z = 150f, Radius = 40f, Town = true }
            };
        }

        private static List<LawDef> DefaultCatalogue()
        {
            return new List<LawDef>
            {
                new LawDef { Id = "kings_peace", Name = "The King's Peace", Kind = KPeace, Zone = "Crown Market", Block = true,
                    Text = "No blade is drawn in the Crown Market. Blows struck there are stayed and written down.",
                    DefaultSentence = SFine, DefaultAmount = 20, DefaultItem = "Wood" },
                new LawDef { Id = "market_curfew", Name = "Curfew of the Market", Kind = KCurfew, Zone = "Crown Market",
                    CurfewStartHourUtc = 22, CurfewEndHourUtc = 6,
                    Text = "None may linger in the Crown Market between the night bells.",
                    DefaultSentence = SFine, DefaultAmount = 10, DefaultItem = "Wood" },
                new LawDef { Id = "no_building_towns", Name = "The Builder's Reserve", Kind = KNoBuilding, Zone = "*", Block = true,
                    Text = "No stone is laid in the realm's towns save by the crown's house.",
                    DefaultSentence = SFine, DefaultAmount = 30, DefaultItem = "Stone" },
                new LawDef { Id = "no_binding_towns", Name = "Free Streets", Kind = KNoCapture, Zone = "*", Block = false,
                    Text = "No rope, chain or cage within the towns. Captures there are crimes.",
                    DefaultSentence = SExile, DefaultAmount = 12 },
                new LawDef { Id = "banned_weapons", Name = "Edict of Sheathed Steel", Kind = KDeclared,
                    Text = "Siege weapons and war-bows are not carried in the towns. (Declared: tried on accusation only.)",
                    DefaultSentence = SFine, DefaultAmount = 25, DefaultItem = "Wood" },
                new LawDef { Id = "bridge_toll", Name = "The Bridge Toll", Kind = KDeclared,
                    Text = "Whoever crosses the crown's bridges pays the toll keeper. (Declared: tried on accusation only.)",
                    DefaultSentence = SFine, DefaultAmount = 10, DefaultItem = "Wood" },
                new LawDef { Id = "harbouring", Name = "Harbouring Outlaws", Kind = KDeclared,
                    Text = "Whoever shelters an outlaw of the court shares the sentence. (Declared: tried on accusation only.)",
                    DefaultSentence = SOutlaw, DefaultAmount = 12 }
            };
        }

        private void ClampConfig()
        {
            if (config.AllowedFineItems == null) config.AllowedFineItems = new List<string>();
            if (config.Zones == null) config.Zones = DefaultZones();
            if (config.Catalogue == null) config.Catalogue = DefaultCatalogue();
            config.MaxActiveLaws = Clamp(config.MaxActiveLaws, 0, 20);
            config.LawProclaimCooldownMinutes = Clamp(config.LawProclaimCooldownMinutes, 0, 10080);
            config.LawChangesPerDay = Clamp(config.LawChangesPerDay, 1, 100);
            config.LawGraceMinutes = Clamp(config.LawGraceMinutes, 0, 1440);
            config.ZoneCheckSeconds = Clamp(config.ZoneCheckSeconds, 5, 300);
            config.CurfewGraceSeconds = Clamp(config.CurfewGraceSeconds, 0, 3600);
            config.CrimeCooldownSeconds = Clamp(config.CrimeCooldownSeconds, 10, 86400);
            config.CrimeRecordDays = Clamp(config.CrimeRecordDays, 1, 90);
            config.MaxCrimeRecords = Clamp(config.MaxCrimeRecords, 10, 5000);
            config.MaxOpenCases = Clamp(config.MaxOpenCases, 1, 200);
            config.CrownAccusationsPerDay = Clamp(config.CrownAccusationsPerDay, 0, 100);
            config.AccusationsPerAccuserPerDay = Clamp(config.AccusationsPerAccuserPerDay, 0, 100);
            config.AccusationsPerTargetPerWeek = Clamp(config.AccusationsPerTargetPerWeek, 1, 50);
            config.AcquittalImmunityHours = Clamp(config.AcquittalImmunityHours, 0, 720);
            config.AcquittalPenalty = Clamp(config.AcquittalPenalty, 0, 10);
            config.CaseExpiryHours = Clamp(config.CaseExpiryHours, 1, 720);
            config.MaxClosedCases = Clamp(config.MaxClosedCases, 10, 1000);
            config.JuryMin = Clamp(config.JuryMin, 1, 25);
            config.JurySize = Clamp(config.JurySize, config.JuryMin, 25);
            config.MinVotes = Clamp(config.MinVotes, 1, config.JurySize);
            config.TrialMinutes = Clamp(config.TrialMinutes, 1, 1440);
            config.MaxTrialAttempts = Clamp(config.MaxTrialAttempts, 1, 10);
            config.CombatWindowMinutes = Clamp(config.CombatWindowMinutes, 1, 1440);
            string ctr = (config.CombatTimeoutResult ?? "").ToLowerInvariant();
            config.CombatTimeoutResult = ctr == "acquit" || ctr == "guilty" ? ctr : "jury";
            config.MaxFineAmount = Clamp(config.MaxFineAmount, 1, 100000);
            config.FineDestination = string.Equals(config.FineDestination, "victim", StringComparison.OrdinalIgnoreCase) ? "victim" : "burn";
            config.FinePayHours = Clamp(config.FinePayHours, 1, 720);
            config.UnpaidFineOutlawHours = Clamp(config.UnpaidFineOutlawHours, 0, 720);
            config.MaxOutlawHours = Clamp(config.MaxOutlawHours, 1, 720);
            config.MaxExileHours = Clamp(config.MaxExileHours, 1, 720);
            config.ExileBreachGraceSeconds = Clamp(config.ExileBreachGraceSeconds, 0, 3600);
            config.ExileBreachOutlawHours = Clamp(config.ExileBreachOutlawHours, 0, 720);
            config.PardonsPerDay = Clamp(config.PardonsPerDay, 0, 100);
            config.ChronicleMaxPerHour = Clamp(config.ChronicleMaxPerHour, 0, 1000);
            config.MaxListLines = Clamp(config.MaxListLines, 1, 100);
            config.TrialInAbsentiaAfterHours = Clamp(config.TrialInAbsentiaAfterHours, 0, 720);
            config.JurorHouseMinMembers = Clamp(config.JurorHouseMinMembers, 0, 100);

            var zoneNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var zones = new List<ZoneDef>();
            foreach (ZoneDef z in config.Zones)
            {
                if (z == null || string.IsNullOrEmpty(z.Name) || zoneNames.ContainsKey(z.Name)) continue;
                if (z.Radius < 1f) z.Radius = 1f;
                if (z.Radius > 2000f) z.Radius = 2000f;
                zoneNames[z.Name] = true;
                zones.Add(z);
            }
            config.Zones = zones;

            var ids = new Dictionary<string, bool>();
            var laws = new List<LawDef>();
            foreach (LawDef l in config.Catalogue)
            {
                if (l == null || string.IsNullOrEmpty(l.Id)) continue;
                l.Id = l.Id.Trim().ToLowerInvariant();
                l.Kind = (l.Kind ?? KDeclared).Trim().ToLowerInvariant();
                if (ids.ContainsKey(l.Id) || l.Id == ExileLawId) { PrintWarning("Duplicate or reserved law id '" + l.Id + "' ignored."); continue; }
                if (l.Kind != KPeace && l.Kind != KCurfew && l.Kind != KNoBuilding && l.Kind != KNoCapture && l.Kind != KDeclared)
                {
                    PrintWarning("Law '" + l.Id + "' has unknown kind '" + l.Kind + "'; treated as declared.");
                    l.Kind = KDeclared;
                }
                if (l.Kind != KDeclared && l.Zone != "*" && (l.Zone == null || !zoneNames.ContainsKey(l.Zone)))
                    PrintWarning("Law '" + l.Id + "' names zone '" + l.Zone + "', which does not exist; it cannot be enforced until it does.");
                if (string.IsNullOrEmpty(l.Name)) l.Name = l.Id;
                if (l.Text == null) l.Text = "";
                l.CurfewStartHourUtc = Clamp(l.CurfewStartHourUtc, 0, 23);
                l.CurfewEndHourUtc = Clamp(l.CurfewEndHourUtc, 0, 23);
                string ds = (l.DefaultSentence ?? "").ToLowerInvariant();
                l.DefaultSentence = ds == SFine || ds == SOutlaw || ds == SExile ? ds : null;
                ids[l.Id] = true;
                laws.Add(l);
            }
            config.Catalogue = laws;
        }

        private static int Clamp(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        #endregion

        #region Data

        private class ActiveLaw
        {
            public string Id;
            public string ProclaimedBy;
            public DateTime ProclaimedAt;
            public DateTime ActiveFrom;
        }

        private class Crime
        {
            public int Id;
            public string PlayerId;
            public string PlayerName;
            public string LawId;
            public string LawName;
            public DateTime At;
            public string Source;              // hook | accusation
            public string Detail;
            public string VictimId;
            public string VictimName;
            public int CaseId;                 // 0 = not yet charged
        }

        private class Sentence
        {
            public string Kind;                // fine | outlaw | exile
            public string Item;
            public int Amount;                 // fine units, or hours
        }

        private class Case
        {
            public int Id;
            public string Status;
            public string LawId;
            public string LawName;
            public string AccusedId;
            public string AccusedName;
            public string AccuserId;
            public string AccuserName;
            public string AccuserRole;
            public string CrownId;             // monarch reigning when the case was brought
            public string VictimId;
            public string VictimName;
            public List<int> CrimeIds = new List<int>();
            public Sentence Sentence;
            public DateTime OpenedAt;
            public DateTime ExpiresAt;
            public int TrialAttempts;
            public DateTime TrialEnds;
            public List<string> Jurors = new List<string>();
            public Dictionary<string, bool> Votes = new Dictionary<string, bool>();    // juror id -> guilty?
            public bool CombatUsed;
            public bool ChampionNamed;
            public string ChampionId;
            public string ChampionName;
            public DateTime CombatEnds;
            public string Outcome;
            public DateTime ClosedAt;
            public int FineRemaining;          // guilty with an unpaid fine
            public DateTime FineDue;
            public bool FineNoticed;           // the convict has been online since the sentence (the pay clock runs)
        }

        private class Punishment
        {
            public string Name;
            public DateTime Until;
            public int CaseId;
        }

        private class Owed
        {
            public string PlayerId;
            public string PlayerName;
            public string Item;
            public int Amount;
        }

        private class Stamp
        {
            public string Kind;                // accuse | acquit | law | pardon
            public string ActorId;
            public string TargetId;
            public DateTime At;
        }

        private class StoredData
        {
            public int NextCrimeId = 1;
            public int NextCaseId = 1;
            public string CrownId;
            public string CrownName;
            public DateTime LastProclamation = DateTime.MinValue;
            public List<ActiveLaw> Laws = new List<ActiveLaw>();
            public List<Crime> Crimes = new List<Crime>();
            public List<Case> Cases = new List<Case>();
            public Dictionary<string, Punishment> Outlaws = new Dictionary<string, Punishment>();
            public Dictionary<string, Punishment> Exiles = new Dictionary<string, Punishment>();
            public List<Owed> Owed = new List<Owed>();
            public List<Stamp> History = new List<Stamp>();
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
                    + ". The court is closed and the file will NOT be overwritten. Fix or remove it, then reload.");
                return;
            }
            if (loaded == null && existed)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or null. The court is closed and the file will NOT be overwritten.");
                return;
            }
            data = loaded ?? new StoredData();
            if (data.Laws == null) data.Laws = new List<ActiveLaw>();
            if (data.Crimes == null) data.Crimes = new List<Crime>();
            if (data.Cases == null) data.Cases = new List<Case>();
            if (data.Outlaws == null) data.Outlaws = new Dictionary<string, Punishment>();
            if (data.Exiles == null) data.Exiles = new Dictionary<string, Punishment>();
            if (data.Owed == null) data.Owed = new List<Owed>();
            if (data.History == null) data.History = new List<Stamp>();
            data.Laws.RemoveAll(delegate(ActiveLaw a) { return a == null || a.Id == null; });
            data.Crimes.RemoveAll(delegate(Crime c) { return c == null || c.PlayerId == null || c.LawId == null; });
            data.Cases.RemoveAll(delegate(Case c) { return c == null || c.AccusedId == null || c.Status == null || c.Sentence == null; });
            data.Owed.RemoveAll(delegate(Owed o) { return o == null || o.PlayerId == null || o.Item == null || o.Amount <= 0; });
            data.History.RemoveAll(delegate(Stamp s) { return s == null || s.Kind == null; });
            foreach (Case c in data.Cases)
            {
                if (c.CrimeIds == null) c.CrimeIds = new List<int>();
                if (c.Jurors == null) c.Jurors = new List<string>();
                if (c.Votes == null) c.Votes = new Dictionary<string, bool>();
                if (c.Id >= data.NextCaseId) data.NextCaseId = c.Id + 1;
            }
            foreach (Crime c in data.Crimes) if (c.Id >= data.NextCrimeId) data.NextCrimeId = c.Id + 1;
        }

        private void SaveData()
        {
            if (data == null || loadFailed) return;              // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
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
                { "Speaker", "Court" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "HelpHeader", "The laws of the realm and the crown's court." },
                { "Help1", "  [F4C96D]/laws[FFFFFF] - this help. [F4C96D]/law list[FFFFFF] | [F4C96D]/law catalogue[FFFFFF] | [F4C96D]/law info[FFFFFF] <id> | [F4C96D]/law crimes[FFFFFF] [player]" },
                { "Help2", "  Monarch: [F4C96D]/law proclaim[FFFFFF] <id> | [F4C96D]/law repeal[FFFFFF] <id>. Admin: [F4C96D]/law zone list|set[FFFFFF] <name> <radius> [town]|remove <name>" },
                { "Help3", "  [F4C96D]/court cases[FFFFFF] | [F4C96D]/court case[FFFFFF] <id> | [F4C96D]/court outlaws[FFFFFF] | [F4C96D]/court pay[FFFFFF] <case> | [F4C96D]/court collect[FFFFFF]" },
                { "Help4", "  Crown & council: [F4C96D]/court accuse[FFFFFF] <player> <law> [fine <n> \"<item>\" | outlaw <hours> | exile <hours>]" },
                { "Help5", "  [F4C96D]/court trial[FFFFFF] <case> (crown, council or accused) | [F4C96D]/court verdict[FFFFFF] <case> guilty|innocent (jurors)" },
                { "Help6", "  [F4C96D]/court combat[FFFFFF] <case> (accused demands trial by combat) | [F4C96D]/court champion[FFFFFF] <case> <player> (accuser)" },
                { "Help7", "  Monarch: [F4C96D]/court pardon[FFFFFF] <player>. Admin: [F4C96D]/court admin dismiss[FFFFFF] <case> | verdict <case> guilty|innocent | clear <player>" },
                { "Closed", "The court's records are damaged. An admin must repair oxide/data/RealmLaws.json and reload." },
                { "NoPermission", "You may not do that." },
                { "NotKing", "Only the reigning monarch may do that." },
                { "PlayerNotFound", "No one by that name is online or on the court's records." },
                { "BadNumber", "'{0}' is not a whole number from {1} to {2}." },
                { "UnknownLaw", "There is no law '{0}' in the catalogue. See [F4C96D]/law catalogue[FFFFFF]." },
                { "LawLine", "  {0} - {1} [{2}{3}] {4}" },
                { "LawInfo", "{0} ({1}): {2}" },
                { "LawInfo2", "  Kind: {0}. Zone: {1}. Blocks the act: {2}. Default sentence: {3}." },
                { "LawsNone", "No laws are in force. The realm lives by the Charter alone." },
                { "LawsHeader", "Laws in force ({0}/{1}):" },
                { "CatalogueHeader", "Laws the crown may proclaim:" },
                { "AlreadyActive", "That law is already in force." },
                { "NotActive", "That law is not in force." },
                { "TooManyLaws", "At most {0} laws may be in force at once. Repeal one first." },
                { "LawCooldown", "The heralds are hoarse. The crown may proclaim again in {0} min." },
                { "LawDaily", "The crown has changed the law {0} times today; that is the Charter's limit." },
                { "ZoneMissing", "That law's zone '{0}' is not defined. An admin must set it first." },
                { "Proclaimed", "{0} proclaims {1}: {2} It binds in {3} min." },
                { "Repealed", "{0} repeals {1}." },
                { "Lapsed", "The crown has changed hands. The old laws lapse." },
                { "CrimeRecorded", "You have broken {0}. It is written in the court's ledger (record #{1})." },
                { "PeaceBlocked", "The King's Peace holds here. Your blow is stayed." },
                { "BuildBlocked", "No one may build here under {0}." },
                { "CaptureBlocked", "No one may be bound here under {0}." },
                { "CurfewWarn", "Curfew: {0}. Leave this place within {1} s or it is written as a crime." },
                { "ExileWarn", "You are exiled from the towns. Leave within {0} s or you will be outlawed." },
                { "CrimesHeader", "Crime records for {0} (last {1} days):" },
                { "CrimeLine", "  #{0} {1} - {2} UTC, {3}{4}" },
                { "CrimesNone", "No crimes are recorded against {0}." },
                { "ZoneLine", "  {0}: x {1}, z {2}, radius {3}{4}" },
                { "ZoneSet", "Zone '{0}' set at x {1}, z {2}, radius {3}{4}." },
                { "ZoneRemoved", "Zone '{0}' removed." },
                { "ZoneUnknown", "No zone is named '{0}'." },
                { "ZoneNoPos", "Your position cannot be read." },
                { "NotAccuser", "Only the monarch or a member of the council may bring an accusation." },
                { "SelfAccuse", "You cannot accuse yourself." },
                { "MonarchImmune", "The reigning monarch cannot be tried by the crown's own court." },
                { "OpenCaseExists", "{0} already faces case #{1}." },
                { "TooManyCases", "The court's docket is full ({0} open cases)." },
                { "CrownQuota", "The crown has used its {0} accusations for today (acquittals count against it)." },
                { "AccuserQuota", "You have made {0} accusations today; that is the limit." },
                { "TargetQuota", "{0} has been accused {1} times this week; the Charter forbids more." },
                { "AcquittedImmune", "{0} was acquitted recently and cannot be accused again for {1} min." },
                { "NeedEvidence", "The ledger shows no breach of {0} by {1}. The Charter requires evidence for this law." },
                { "LawNotInForce", "{0} is not in force and {1} has no recorded breach of it." },
                { "BadSentence", "Sentence: fine <1-{0}> \"<item>\" | outlaw <1-{1}> | exile <1-{2}> (hours)." },
                { "NoDefaultSentence", "That law has no default sentence; name one." },
                { "UnknownItem", "No item is named '{0}'." },
                { "ItemNotAllowed", "'{0}' may not be used for fines on this server." },
                { "Accused", "{0} accuses {1} of breaking {2}. Case #{3}; sentence sought: {4}. Evidence: {5} record(s)." },
                { "AccusedYou", "You are accused in case #{0}. You may demand trial by combat: [F4C96D]/court combat[FFFFFF] {0}" },
                { "CaseNotFound", "There is no case #{0}." },
                { "CaseNotOpen", "Case #{0} is not waiting for that." },
                { "CaseHeader", "Case #{0} [{1}]: {2} accused by {3} ({4}) of {5}." },
                { "CaseLine2", "  Sentence sought: {0}. Evidence: {1} record(s). Opened {2} UTC, expires {3} UTC." },
                { "CaseLine3", "  Jury: {0} sworn, {1} votes cast; closes {2} UTC." },
                { "CaseLine4", "  Trial by combat: {0} vs champion {1}; window closes {2} UTC." },
                { "CaseLine5", "  Outcome: {0}." },
                { "CaseLine6", "  Unpaid fine: {0} {1}, due {2} UTC ([F4C96D]/court pay[FFFFFF] {3})." },
                { "CasesHeader", "Open cases:" },
                { "CaseShort", "  #{0} [{1}] {2} - {3}" },
                { "CasesNone", "The court has no open cases." },
                { "ListMore", "  ... and {0} more." },
                { "NotParty", "Only the crown, its council or the accused may call the court to sit." },
                { "AccusedOffline", "{0} must be online to stand trial (or be tried in absence {1} h after the charge)." },
                { "TooFewJurors", "Too few sworn lords are online to seat a jury ({0} of {1} needed)." },
                { "TrialOpened", "The court sits on case #{0}: {1} stands accused of {2}. {3} sworn lords are called to judge." },
                { "JurorCalled", "You are sworn to the jury of case #{0} ({1} accused of {2}, sentence sought: {3}). Vote within {4} min: [F4C96D]/court verdict[FFFFFF] {0} guilty|innocent" },
                { "NotJuror", "You are not sworn to that jury." },
                { "Voted", "Your vote is recorded. It is secret." },
                { "Mistrial", "Case #{0}: too few jurors voted. Mistrial." },
                { "MistrialFinal", "Case #{0}: mistrial for the last time. The case is dismissed." },
                { "Guilty", "Case #{0}: {1} is found GUILTY of {2} ({3}). Sentence: {4}." },
                { "Acquitted", "Case #{0}: {1} is found INNOCENT of {2} ({3})." },
                { "CombatOff", "Trial by combat is not allowed on this server." },
                { "CombatUsed", "Trial by combat was already demanded in this case." },
                { "CombatVotesCast", "The jury has begun to vote; it is too late to demand combat." },
                { "NotAccused", "Only the accused may demand that." },
                { "ChampionOffline", "The crown's champion {0} must be online. The accuser may name another: [F4C96D]/court champion[FFFFFF] {1} <player>" },
                { "CombatOpened", "Case #{0}: {1} demands TRIAL BY COMBAT against {2}. They have {3} min. The gods will judge." },
                { "CombatTimeout", "Case #{0}: no blood was shed in the window." },
                { "CombatFled", "Case #{0}: {1} fled the field." },
                { "NotCaseAccuser", "Only the accuser may name a champion." },
                { "ChampionNamedAlready", "A champion was already named for this case." },
                { "BadChampion", "That champion cannot fight in this case." },
                { "ChampionNamed", "{0} is named the crown's champion in case #{1}." },
                { "NoFine", "You owe no fine in case #{0}." },
                { "FinePaid", "Fine of case #{0} paid in full." },
                { "FinePartial", "{0} {1} taken. {2} still owed by {3} UTC." },
                { "FineNone", "You carry no {0}. {1} owed by {2} UTC ([F4C96D]/court pay[FFFFFF] {3})." },
                { "FineUnpaidOutlaw", "{0} did not pay the fine of case #{1} and is outlawed." },
                { "OwedPaid", "You receive {0} {1} in fines paid to you." },
                { "OwedFull", "Your packs are full. {0} {1} wait for you: [F4C96D]/court collect[FFFFFF]" },
                { "NothingOwed", "Nothing is owed to you." },
                { "OutlawsHeader", "Outlaws and exiles of the court:" },
                { "OutlawLine", "  {0} - {1} until {2} UTC (case #{3})" },
                { "OutlawsNone", "The court has outlawed or exiled no one." },
                { "Outlawed", "{0} is declared OUTLAW for {1} h. The King's Peace no longer shelters them." },
                { "Exiled", "{0} is EXILED from the towns for {1} h." },
                { "ExileBreach", "{0} broke exile and is outlawed." },
                { "PardonQuota", "The crown has used its {0} pardons for today." },
                { "NothingToPardon", "{0} has no sentence to pardon." },
                { "Pardoned", "{0} pardons {1}." },
                { "Dismissed", "Case #{0} is dismissed ({1})." },
                { "AdminDone", "Done: {0}." },
                { "Cleared", "Cleared sentences and open cases of {0}." }
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
            player.SendMessage(Styled(Msg("Speaker", player), ToneOf(key), Fmt(key, player, args)));   // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Voted", "FinePaid", "OwedPaid", "ZoneSet", "ZoneRemoved", "AdminDone", "Cleared"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "AccusedYou", "JurorCalled", "FinePartial", "FineNone", "OwedFull"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        private void Broadcast(string key, params object[] args)
        {
            Server.BroadcastMessage(Msg("Herald", null) + Fmt(key, null, args));
        }

        private void Herald(string key, params object[] args)
        {
            Server.BroadcastMessage(Msg("Herald", null) + Fmt(key, null, args));
        }

        private void NotifyId(string playerId, string key, params object[] args)
        {
            Player p = OnlineById(playerId);
            if (p != null) Reply(p, key, args);
        }

        // At most one message per key and player every 10 s (hooks can fire many times a second).
        private void ReplyThrottled(Player player, string key, params object[] args)
        {
            string k = player.Id + "|" + key;
            DateTime last;
            DateTime now = DateTime.UtcNow;
            if (lastNotice.TryGetValue(k, out last) && (now - last).TotalSeconds < 10) return;
            lastNotice[k] = now;
            ReplyError(player, key, args);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            if (config == null) { config = new PluginConfig(); }
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            if (!loadFailed) RebuildLawCache();
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

        #endregion

        #region Law catalogue and zones

        private LawDef FindLawDef(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string key = id.Trim().ToLowerInvariant();
            foreach (LawDef l in config.Catalogue) if (l.Id == key) return l;
            return null;
        }

        private ActiveLaw FindActive(string id)
        {
            foreach (ActiveLaw a in data.Laws) if (a.Id == id) return a;
            return null;
        }

        private bool IsInForce(LawDef law, DateTime now)
        {
            ActiveLaw a = FindActive(law.Id);
            return a != null && a.ActiveFrom <= now;
        }

        private ZoneDef FindZone(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (ZoneDef z in config.Zones) if (string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase)) return z;
            return null;
        }

        private bool ZoneDefined(LawDef law)
        {
            if (law.Kind == KDeclared) return true;
            if (law.Zone == "*") { foreach (ZoneDef z in config.Zones) if (z.Town) return true; return false; }
            return FindZone(law.Zone) != null;
        }

        private static bool Inside(ZoneDef z, Vector3 pos)
        {
            float dx = pos.x - z.X, dz = pos.z - z.Z;
            return dx * dx + dz * dz <= z.Radius * z.Radius;
        }

        // Returns the zone of the law that contains pos, or null.
        private ZoneDef LawZoneAt(LawDef law, Vector3 pos)
        {
            if (law.Zone == "*")
            {
                foreach (ZoneDef z in config.Zones) if (z.Town && Inside(z, pos)) return z;
                return null;
            }
            ZoneDef zone = FindZone(law.Zone);
            return zone != null && Inside(zone, pos) ? zone : null;
        }

        private ZoneDef TownAt(Vector3 pos)
        {
            foreach (ZoneDef z in config.Zones) if (z.Town && Inside(z, pos)) return z;
            return null;
        }

        private static bool TryPosition(Player p, out Vector3 pos)
        {
            pos = new Vector3();
            if (p == null || p.Entity == null) return false;
            pos = p.Entity.Position;
            return true;
        }

        private void RebuildLawCache()
        {
            activeEnforced.Clear();
            anyPeace = anyCurfew = anyNoBuilding = anyNoCapture = false;
            if (data == null) return;
            foreach (ActiveLaw a in data.Laws)
            {
                LawDef l = FindLawDef(a.Id);
                if (l == null || l.Kind == KDeclared) continue;
                activeEnforced.Add(l);
                if (l.Kind == KPeace) anyPeace = true;
                else if (l.Kind == KCurfew) anyCurfew = true;
                else if (l.Kind == KNoBuilding) anyNoBuilding = true;
                else if (l.Kind == KNoCapture) anyNoCapture = true;
            }
        }

        // The first law of this kind in force whose zone contains pos.
        private LawDef EnforcedLawAt(string kind, Vector3 pos, DateTime now)
        {
            foreach (LawDef l in activeEnforced)
                if (l.Kind == kind && IsInForce(l, now) && LawZoneAt(l, pos) != null) return l;
            return null;
        }

        private static bool CurfewNow(LawDef l, DateTime now)
        {
            int h = now.Hour, s = l.CurfewStartHourUtc, e = l.CurfewEndHourUtc;
            if (s == e) return false;
            return s < e ? (h >= s && h < e) : (h >= s || h < e);
        }

        #endregion

        #region Commands: /laws and /law

        [ChatCommand("laws")]
        private void CmdLaws(Player player, string command, string[] args)
        {
            ShowHelp(player);
        }

        private void ShowHelp(Player player)
        {
            Reply(player, "HelpHeader");
            for (int i = 1; i <= 7; i++) player.SendMessage(Msg("Help" + i, player));
        }

        [ChatCommand("law")]
        private void CmdLaw(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (data == null) { ReplyError(player, "Closed"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            switch (sub)
            {
                case "help": ShowHelp(player); return;
                case "list": ListLaws(player); return;
                case "catalogue":
                case "catalog": ListCatalogue(player); return;
                case "info": LawInfo(player, args); return;
                case "proclaim": Proclaim(player, args); return;
                case "repeal": Repeal(player, args); return;
                case "crimes": ListCrimes(player, args); return;
                case "zone": ZoneCmd(player, args); return;
                default: ShowHelp(player); return;
            }
        }

        private void ListLaws(Player player)
        {
            DateTime now = DateTime.UtcNow;
            if (data.Laws.Count == 0) { Reply(player, "LawsNone"); return; }
            Reply(player, "LawsHeader", data.Laws.Count, config.MaxActiveLaws);
            foreach (ActiveLaw a in data.Laws)
            {
                LawDef l = FindLawDef(a.Id);
                if (l == null) continue;
                string when = a.ActiveFrom > now ? "binds in " + MinutesUntil(a.ActiveFrom) + " min" : "by " + a.ProclaimedBy;
                player.SendMessage(Fmt("LawLine", player, new object[] { l.Id, l.Name, KindLabel(l), ZoneLabel(l), when }));
            }
        }

        private void ListCatalogue(Player player)
        {
            Reply(player, "CatalogueHeader");
            DateTime now = DateTime.UtcNow;
            foreach (LawDef l in config.Catalogue)
                player.SendMessage(Fmt("LawLine", player, new object[] { l.Id, l.Name, KindLabel(l), ZoneLabel(l),
                    FindActive(l.Id) != null ? (IsInForce(l, now) ? "(in force)" : "(proclaimed)") : "" }));
        }

        private void LawInfo(Player player, string[] args)
        {
            LawDef l = args.Length > 1 ? FindLawDef(args[1]) : null;
            if (l == null) { ReplyError(player, "UnknownLaw", args.Length > 1 ? args[1] : ""); return; }
            Reply(player, "LawInfo", l.Name, l.Id, l.Text);
            player.SendMessage(Fmt("LawInfo2", player, new object[] { l.Kind, l.Kind == KDeclared ? "-" : (l.Zone == "*" ? "every town" : l.Zone),
                l.Kind == KDeclared || l.Kind == KCurfew ? "no" : (l.Block ? "yes" : "no"), DefaultSentenceText(l) }));
        }

        private void Proclaim(Player player, string[] args)
        {
            bool admin = IsAdmin(player);
            if (!IsMonarch(player) && !admin) { ReplyError(player, "NotKing"); return; }
            LawDef l = args.Length > 1 ? FindLawDef(args[1]) : null;
            if (l == null) { ReplyError(player, "UnknownLaw", args.Length > 1 ? args[1] : ""); return; }
            if (FindActive(l.Id) != null) { ReplyError(player, "AlreadyActive"); return; }
            if (!ZoneDefined(l)) { ReplyError(player, "ZoneMissing", l.Zone); return; }
            if (data.Laws.Count >= config.MaxActiveLaws) { ReplyError(player, "TooManyLaws", config.MaxActiveLaws); return; }
            DateTime now = DateTime.UtcNow;
            if (!admin)
            {
                DateTime next = data.LastProclamation.AddMinutes(config.LawProclaimCooldownMinutes);
                if (data.LastProclamation != DateTime.MinValue && next > now) { ReplyError(player, "LawCooldown", MinutesUntil(next)); return; }
                int changes = CountStamps("law", null, null, now.AddHours(-24));
                if (changes >= config.LawChangesPerDay) { ReplyError(player, "LawDaily", changes); return; }
            }
            data.LastProclamation = now;
            data.Laws.Add(new ActiveLaw { Id = l.Id, ProclaimedBy = player.Name, ProclaimedAt = now, ActiveFrom = now.AddMinutes(config.LawGraceMinutes) });
            AddStamp("law", player.Id.ToString(), l.Id, now);
            RebuildLawCache();
            SaveData();
            Herald("Proclaimed", player.Name, l.Name, l.Text, config.LawGraceMinutes);
            Chronicle("law_proclaimed", player.Name + " proclaims " + l.Name, l.Text, new[] { player.Name });
        }

        private void Repeal(Player player, string[] args)
        {
            bool admin = IsAdmin(player);
            if (!IsMonarch(player) && !admin) { ReplyError(player, "NotKing"); return; }
            LawDef l = args.Length > 1 ? FindLawDef(args[1]) : null;
            string id = l != null ? l.Id : (args.Length > 1 ? args[1].ToLowerInvariant() : "");
            ActiveLaw a = FindActive(id);
            if (a == null) { ReplyError(player, "NotActive"); return; }
            data.Laws.Remove(a);
            AddStamp("law", player.Id.ToString(), id, DateTime.UtcNow);
            RebuildLawCache();
            SaveData();
            string name = l != null ? l.Name : id;
            Herald("Repealed", player.Name, name);
            Chronicle("law_repealed", player.Name + " repeals " + name, name + " no longer binds the realm.", new[] { player.Name });
        }

        private void ListCrimes(Player player, string[] args)
        {
            string id = player.Id.ToString(), name = player.Name;
            if (args.Length > 1)
            {
                string who = JoinFrom(args, 1);
                if (!ResolvePlayerRef(who, out id, out name)) { ReplyError(player, "PlayerNotFound"); return; }
            }
            var list = new List<Crime>();
            foreach (Crime c in data.Crimes) if (c.PlayerId == id) list.Add(c);
            if (list.Count == 0) { Reply(player, "CrimesNone", name); return; }
            Reply(player, "CrimesHeader", name, config.CrimeRecordDays);
            int shown = 0;
            for (int i = list.Count - 1; i >= 0 && shown < config.MaxListLines; i--, shown++)
            {
                Crime c = list[i];
                string charged = c.CaseId > 0 ? " (case #" + c.CaseId + ")" : "";
                player.SendMessage(Fmt("CrimeLine", player, new object[] { c.Id, c.LawName, c.At.ToString("MM-dd HH:mm"), c.Detail, charged }));
            }
            if (list.Count > shown) player.SendMessage(Fmt("ListMore", player, new object[] { list.Count - shown }));
        }

        private void ZoneCmd(Player player, string[] args)
        {
            string op = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            if (op == "list")
            {
                foreach (ZoneDef z in config.Zones)
                    player.SendMessage(Fmt("ZoneLine", player, new object[] { z.Name, z.X.ToString("0"), z.Z.ToString("0"), z.Radius.ToString("0"), z.Town ? " (town)" : "" }));
                return;
            }
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            if (op == "set" && args.Length >= 4)
            {
                // /law zone set <name...> <radius> [town]
                bool town = string.Equals(args[args.Length - 1], "town", StringComparison.OrdinalIgnoreCase);
                int radiusIdx = town ? args.Length - 2 : args.Length - 1;
                int radius;
                if (radiusIdx < 3 || !int.TryParse(args[radiusIdx], out radius) || radius < 1 || radius > 2000)
                {
                    ReplyError(player, "BadNumber", radiusIdx >= 0 && radiusIdx < args.Length ? args[radiusIdx] : "", 1, 2000);
                    return;
                }
                string name = string.Join(" ", args, 2, radiusIdx - 2);
                Vector3 pos;
                if (!TryPosition(player, out pos)) { ReplyError(player, "ZoneNoPos"); return; }
                ZoneDef z = FindZone(name);
                if (z == null) { z = new ZoneDef { Name = name }; config.Zones.Add(z); }
                z.X = pos.x; z.Z = pos.z; z.Radius = radius; z.Town = town;
                Config.WriteObject(config, true);
                RebuildLawCache();
                Reply(player, "ZoneSet", z.Name, z.X.ToString("0"), z.Z.ToString("0"), radius, town ? " (town)" : "");
                return;
            }
            if (op == "remove" && args.Length >= 3)
            {
                ZoneDef z = FindZone(JoinFrom(args, 2));
                if (z == null) { ReplyError(player, "ZoneUnknown", JoinFrom(args, 2)); return; }
                config.Zones.Remove(z);
                Config.WriteObject(config, true);
                RebuildLawCache();
                Reply(player, "ZoneRemoved", z.Name);
                return;
            }
            ShowHelp(player);
        }

        #endregion

        #region Crime ledger

        // Records a breach, at most once per offender and law every CrimeCooldownSeconds. Returns the record or null.
        private Crime RecordCrime(Player offender, string lawId, string lawName, string detail, Player victim)
        {
            if (offender == null || offender.IsServer || data == null) return null;
            DateTime now = DateTime.UtcNow;
            string key = offender.Id + "|" + lawId;
            DateTime last;
            if (lastCrimeAt.TryGetValue(key, out last) && (now - last).TotalSeconds < config.CrimeCooldownSeconds) return null;
            lastCrimeAt[key] = now;
            var c = new Crime
            {
                Id = data.NextCrimeId++,
                PlayerId = offender.Id.ToString(),
                PlayerName = offender.Name,
                LawId = lawId,
                LawName = lawName,
                At = now,
                Source = "hook",
                Detail = detail ?? "",
                VictimId = victim != null ? victim.Id.ToString() : null,
                VictimName = victim != null ? victim.Name : null
            };
            data.Crimes.Add(c);
            PruneCrimes(now);
            dirty = true;
            Puts("Crime #" + c.Id + ": " + c.PlayerName + " broke " + lawName + " (" + c.Detail + ")");
            ReplyError(offender, "CrimeRecorded", lawName, c.Id);
            return c;
        }

        private bool PruneCrimes(DateTime now)
        {
            DateTime cutoff = now.AddDays(-config.CrimeRecordDays);
            int removed = data.Crimes.RemoveAll(delegate(Crime c) { return c.At < cutoff && !IsCrimeOnOpenCase(c); });
            if (data.Crimes.Count > config.MaxCrimeRecords)
            {
                // Oldest first, but never the evidence of an open case: otherwise anyone could flush a pending charge by
                // loitering through curfews or brawling in a peace zone until the ledger overflows.
                int extra = data.Crimes.Count - config.MaxCrimeRecords;
                removed += data.Crimes.RemoveAll(delegate(Crime c)
                {
                    if (extra <= 0 || IsCrimeOnOpenCase(c)) return false;
                    extra--;
                    return true;
                });
            }
            return removed > 0;
        }

        private bool IsCrimeOnOpenCase(Crime c)
        {
            if (c.CaseId <= 0) return false;
            Case k = FindCase(c.CaseId);
            return k != null && IsOpen(k);
        }

        #endregion

        #region Enforcement hooks

        // RB 1 [OPJ L162]. Fires for every damage event; returns early unless a peace law is in force.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (!anyPeace || data == null || evt == null || evt.Cancelled) return null;
            try
            {
                Entity ve = evt.Entity;
                if (ve == null || !ve.IsPlayer) return null;
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f || d.DamageSource == null || !d.DamageSource.IsPlayer) return null;
                Player victim = ve.Owner, attacker = d.DamageSource.Owner;
                if (victim == null || attacker == null || victim.IsServer || attacker.IsServer || victim.Id == attacker.Id) return null;
                if (InDuel(attacker.Id.ToString(), victim.Id.ToString())) return null;
                if (config.OutlawsAndExilesLosePeace && (IsPunished(data.Outlaws, victim.Id.ToString()) || IsPunished(data.Exiles, victim.Id.ToString()))) return null;
                if (config.PeaceSuspendedDuringRebellion && RebellionActive()) return null;
                Vector3 pos = ve.Position;
                DateTime now = DateTime.UtcNow;
                LawDef law = EnforcedLawAt(KPeace, pos, now);
                if (law == null)
                {
                    Vector3 apos;
                    if (TryPosition(attacker, out apos)) law = EnforcedLawAt(KPeace, apos, now);
                }
                if (law == null) return null;
                RecordCrime(attacker, law.Id, law.Name, "struck " + victim.Name, victim);
                if (!law.Block) return null;
                evt.Cancel("King's Peace");
                d.Amount = 0f;
                ReplyThrottled(attacker, "PeaceBlocked");
                return true;
            }
            catch (Exception ex)
            {
                PrintError("Peace check failed: " + ex.Message);
                return null;
            }
        }

        // RB 0 [OPJ L266]; blocking is by Cancel() [USE LevelSystem.cs:2072]. [IL] CubeListener.OnCubePlace calls this
        // hook first, then uses NetworkEvent.SenderId for the game's own CrestScheme.OwnsLocation check, cancels with
        // NetworkEvent.Cancel and reads BaseEvent.Cancelled afterwards; it converts the cube's position to world space
        // with evt.Grid.LocalToWorldCoordinate(evt.Position). The same calls are used here.
        private void OnCubePlacement(CubePlaceEvent evt)
        {
            if (!anyNoBuilding || data == null || evt == null || evt.Cancelled) return;
            try
            {
                Player builder = Server.GetPlayerById(evt.SenderId);
                if (builder == null || builder.IsServer) return;
                if (IsAdmin(builder)) return;
                if (config.CrownHouseMayBuildInZones && IsCrownHouseMember(builder)) return;
                Vector3 pos;
                if (evt.Grid != null) pos = evt.Grid.LocalToWorldCoordinate(evt.Position);
                else if (!TryPosition(builder, out pos)) return;
                LawDef law = EnforcedLawAt(KNoBuilding, pos, DateTime.UtcNow);
                if (law == null) return;
                RecordCrime(builder, law.Id, law.Name, "built in " + ZoneName(law, pos), null);
                if (!law.Block) return;
                evt.Cancel("Building forbidden by law");
                ReplyThrottled(builder, "BuildBlocked", law.Name);
            }
            catch (Exception ex)
            {
                PrintError("Building check failed: " + ex.Message);
            }
        }

        // RB 1 [OPJ L711]. CrownAndConsequences also handles this hook; it skips cancelled events, but if it runs first
        // it records the capture before this cancels it. That is why the default catalogue ships no_capture with Block false.
        private object OnPlayerCapture(PlayerCaptureEvent evt)
        {
            if (!anyNoCapture || data == null || evt == null || evt.Cancelled || evt.Target == null) return null;
            try
            {
                if (evt.Captor == null || !evt.Captor.IsPlayer || evt.Captor.Owner == null) return null;
                Player captor = evt.Captor.Owner, target = evt.Target;
                if (captor.IsServer || target.IsServer || captor.Id == target.Id) return null;
                if (IsPunished(data.Outlaws, target.Id.ToString())) return null;   // taking an outlaw is lawful
                Vector3 pos;
                if (!TryPosition(target, out pos)) return null;
                LawDef law = EnforcedLawAt(KNoCapture, pos, DateTime.UtcNow);
                if (law == null) return null;
                RecordCrime(captor, law.Id, law.Name, "bound " + target.Name, target);
                if (!law.Block) return null;
                evt.Cancel("Binding forbidden by law");
                ReplyThrottled(captor, "CaptureBlocked", law.Name);
                return true;
            }
            catch (Exception ex)
            {
                PrintError("Capture check failed: " + ex.Message);
                return null;
            }
        }

        // RB 1 [OPJ L188]. Only reads; never changes the death.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (data == null || evt == null) return null;
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
            string v = victim.Id.ToString(), k = killer.Id.ToString();
            foreach (Case c in data.Cases.ToArray())
            {
                if (c.Status != CCombat) continue;
                if (k == c.ChampionId && v == c.AccusedId) { CloseGuilty(c, "by combat: " + killer.Name + " slew " + victim.Name); return; }
                if (k == c.AccusedId && v == c.ChampionId) { CloseAcquitted(c, "by combat: " + killer.Name + " slew " + victim.Name); return; }
            }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (data == null || player == null || player.IsServer || !config.CombatFleeLoses) return;
            string id = player.Id.ToString();
            foreach (Case c in data.Cases.ToArray())
            {
                if (c.Status != CCombat) continue;
                if (id == c.AccusedId) { Broadcast("CombatFled", c.Id, c.AccusedName); CloseGuilty(c, c.AccusedName + " fled the trial by combat"); }
                else if (id == c.ChampionId) { Broadcast("CombatFled", c.Id, c.ChampionName); CloseAcquitted(c, c.ChampionName + " fled the trial by combat"); }
            }
        }

        private void OnPlayerConnected(Player player)
        {
            if (data == null || player == null || player.IsServer) return;
            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            foreach (Case c in data.Cases)
            {
                if (c.AccusedId == id && c.Status == CAccused) Reply(player, "AccusedYou", c.Id);
                if (c.AccusedId == id && c.Status == CGuilty && c.FineRemaining > 0)
                {
                    if (!c.FineNoticed)
                    {
                        c.FineNoticed = true;
                        DateTime due = now.AddHours(config.FinePayHours);
                        if (c.FineDue < due) c.FineDue = due;
                        dirty = true;
                    }
                    Reply(player, "CaseLine6", c.FineRemaining, c.Sentence.Item, c.FineDue.ToString("MM-dd HH:mm"), c.Id);
                }
            }
            if (HasOwed(id)) timer.Once(5f, delegate { PayOwed(player); });
        }

        private bool InDuel(string a, string b)
        {
            foreach (Case c in data.Cases)
                if (c.Status == CCombat && ((c.AccusedId == a && c.ChampionId == b) || (c.AccusedId == b && c.ChampionId == a))) return true;
            return false;
        }

        #endregion

        #region Tick: zones, timers, succession

        private void Tick()
        {
            if (data == null) return;
            DateTime now = DateTime.UtcNow;
            try
            {
                CheckSuccession(now);
                zoneAccumulator += TickSeconds;
                if (zoneAccumulator >= config.ZoneCheckSeconds) { zoneAccumulator = 0f; CheckZones(now); }
                CheckCases(now);
                if (PrunePunishments(data.Outlaws, now)) dirty = true;
                if (PrunePunishments(data.Exiles, now)) dirty = true;
                if (PruneCrimes(now)) dirty = true;
                PruneHistory(now);
                PruneClosedCases();
                PruneThrottles(now);
            }
            catch (Exception ex)
            {
                PrintError("Tick failed: " + ex.Message);
            }
            if (dirty) SaveData();
        }

        private void CheckSuccession(DateTime now)
        {
            KingsScheme ks = SocialAPI.Get<KingsScheme>();
            if (ks == null || !ks.HasKing()) return;           // a vacant throne (or an unloaded one at boot) changes nothing
            string kid = ks.GetKingID().ToString();
            if (kid == "0" || kid == data.CrownId) return;
            bool succession = !string.IsNullOrEmpty(data.CrownId);
            data.CrownId = kid;
            data.CrownName = ks.GetKingName();
            dirty = true;
            if (!succession) return;
            if (config.LawsLapseOnSuccession && data.Laws.Count > 0)
            {
                data.Laws.Clear();
                RebuildLawCache();
                Herald("Lapsed");
            }
            if (config.DismissCrownCasesOnSuccession)
                foreach (Case c in data.Cases.ToArray())
                    if ((c.Status == CAccused || c.Status == CTrial) && c.AccuserRole != "admin") Dismiss(c, "the crown that brought it has fallen");
        }

        private void CheckZones(DateTime now)
        {
            bool exiles = data.Exiles.Count > 0;
            if (!anyCurfew && !exiles) return;
            var seen = new Dictionary<string, bool>();
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                Vector3 pos;
                if (!TryPosition(p, out pos)) continue;
                string id = p.Id.ToString();
                if (anyCurfew)
                    foreach (LawDef l in activeEnforced)
                    {
                        if (l.Kind != KCurfew || !IsInForce(l, now) || !CurfewNow(l, now)) continue;
                        ZoneDef z = LawZoneAt(l, pos);
                        if (z == null) continue;
                        string key = id + "|" + l.Id;
                        seen[key] = true;
                        DateTime warned;
                        if (!zoneWarnedAt.TryGetValue(key, out warned))
                        {
                            zoneWarnedAt[key] = now;
                            ReplyError(p, "CurfewWarn", l.Name, config.CurfewGraceSeconds);
                        }
                        else if ((now - warned).TotalSeconds >= config.CurfewGraceSeconds)
                            RecordCrime(p, l.Id, l.Name, "in " + z.Name + " after curfew", null);
                    }
                if (exiles && IsPunished(data.Exiles, id) && TownAt(pos) != null)
                {
                    string key = id + "|" + ExileLawId;
                    seen[key] = true;
                    DateTime warned;
                    if (!zoneWarnedAt.TryGetValue(key, out warned))
                    {
                        zoneWarnedAt[key] = now;
                        ReplyError(p, "ExileWarn", config.ExileBreachGraceSeconds);
                    }
                    else if ((now - warned).TotalSeconds >= config.ExileBreachGraceSeconds)
                        BreakExile(p, TownAt(pos).Name, now);
                }
            }
            foreach (string key in new List<string>(zoneWarnedAt.Keys)) if (!seen.ContainsKey(key)) zoneWarnedAt.Remove(key);
        }

        private void BreakExile(Player p, string town, DateTime now)
        {
            string id = p.Id.ToString();
            Punishment ex = data.Exiles[id];
            Crime c = RecordCrime(p, ExileLawId, "Breach of Exile", "entered " + town + " while exiled", null);
            if (c == null) return;
            data.Exiles.Remove(id);
            zoneWarnedAt.Remove(id + "|" + ExileLawId);
            if (config.ExileBreachOutlawHours > 0)
            {
                Broadcast("ExileBreach", p.Name);
                SetOutlaw(id, p.Name, config.ExileBreachOutlawHours, ex.CaseId);
                Chronicle("verdict", p.Name + " breaks exile", p.Name + " returned to " + town + " before the sentence ran out and is outlawed for "
                    + config.ExileBreachOutlawHours + " hours.", new[] { p.Name });
            }
            SaveData();
        }

        private void CheckCases(DateTime now)
        {
            foreach (Case c in data.Cases.ToArray())
            {
                if (c.Status == CAccused && c.ExpiresAt <= now) { c.Status = CExpired; c.Outcome = "not brought to trial in time"; c.ClosedAt = now; ReleaseCrimes(c); dirty = true; }
                else if (c.Status == CTrial && (c.TrialEnds <= now || AllVoted(c))) ResolveTrial(c, now);
                else if (c.Status == CCombat && c.CombatEnds <= now)
                {
                    Broadcast("CombatTimeout", c.Id);
                    if (config.CombatTimeoutResult == "guilty") CloseGuilty(c, "no blood in the combat window");
                    else if (config.CombatTimeoutResult == "acquit") CloseAcquitted(c, "no blood in the combat window");
                    else { c.Status = CAccused; c.ExpiresAt = now.AddHours(config.CaseExpiryHours); dirty = true; }
                }
                else if (c.Status == CGuilty && c.FineRemaining > 0 && c.FineDue <= now && c.FineNoticed)
                {
                    int left = c.FineRemaining;
                    c.FineRemaining = 0;
                    c.Outcome += "; fine unpaid (" + left + " " + c.Sentence.Item + ")";
                    dirty = true;
                    if (config.UnpaidFineOutlawHours > 0)
                    {
                        Broadcast("FineUnpaidOutlaw", c.AccusedName, c.Id);
                        SetOutlaw(c.AccusedId, c.AccusedName, config.UnpaidFineOutlawHours, c.Id);
                    }
                }
            }
        }

        private bool PrunePunishments(Dictionary<string, Punishment> map, DateTime now)
        {
            bool changed = false;
            foreach (string k in new List<string>(map.Keys))
                if (map[k] == null || map[k].Until <= now) { map.Remove(k); changed = true; }
            return changed;
        }

        // In-memory throttles hold one entry per offender and law (or notice key); drop the stale ones so a long uptime
        // with many players does not grow them without bound.
        private void PruneThrottles(DateTime now)
        {
            if (lastCrimeAt.Count > 256)
                foreach (string k in new List<string>(lastCrimeAt.Keys))
                    if ((now - lastCrimeAt[k]).TotalSeconds >= config.CrimeCooldownSeconds) lastCrimeAt.Remove(k);
            if (lastNotice.Count > 256)
                foreach (string k in new List<string>(lastNotice.Keys))
                    if ((now - lastNotice[k]).TotalMinutes >= 10) lastNotice.Remove(k);
        }

        private void PruneHistory(DateTime now)
        {
            DateTime cutoff = now.AddDays(-8);
            if (data.History.RemoveAll(delegate(Stamp s) { return s.At < cutoff; }) > 0) dirty = true;
        }

        private void PruneClosedCases()
        {
            int closed = 0;
            foreach (Case c in data.Cases) if (!IsOpen(c) && !(c.Status == CGuilty && c.FineRemaining > 0)) closed++;
            if (closed <= config.MaxClosedCases) return;
            int drop = closed - config.MaxClosedCases;
            data.Cases.RemoveAll(delegate(Case c)
            {
                if (drop <= 0 || IsOpen(c) || (c.Status == CGuilty && c.FineRemaining > 0)) return false;
                drop--;
                return true;
            });
            dirty = true;
        }

        #endregion

        #region Commands: /court

        [ChatCommand("court")]
        private void CmdCourt(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (data == null) { ReplyError(player, "Closed"); return; }
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "cases": ListCases(player); return;
                case "case": ShowCase(player, args); return;
                case "accuse": Accuse(player, args); return;
                case "trial": OpenTrial(player, args); return;
                case "verdict":
                case "vote": Vote(player, args); return;
                case "combat": DemandCombat(player, args); return;
                case "champion": NameChampion(player, args); return;
                case "pay": PayFine(player, args); return;
                case "collect":
                    if (!HasOwed(player.Id.ToString())) { Reply(player, "NothingOwed"); return; }
                    PayOwed(player);
                    return;
                case "outlaws":
                case "exiles": ListOutlaws(player); return;
                case "pardon": Pardon(player, args); return;
                case "admin": AdminCmd(player, args); return;
                default: ShowHelp(player); return;
            }
        }

        private void ListCases(Player player)
        {
            int n = 0, total = 0;
            foreach (Case c in data.Cases)
            {
                if (!IsOpen(c)) continue;
                total++;
                if (n >= config.MaxListLines) continue;
                if (n == 0) Reply(player, "CasesHeader");
                player.SendMessage(Fmt("CaseShort", player, new object[] { c.Id, c.Status, c.AccusedName, c.LawName }));
                n++;
            }
            if (total == 0) Reply(player, "CasesNone");
            else if (total > n) player.SendMessage(Fmt("ListMore", player, new object[] { total - n }));
        }

        private void ShowCase(Player player, string[] args)
        {
            Case c = args.Length > 1 ? FindCase(args[1]) : null;
            if (c == null) { ReplyError(player, "CaseNotFound", args.Length > 1 ? args[1] : ""); return; }
            Reply(player, "CaseHeader", c.Id, c.Status, c.AccusedName, c.AccuserName, c.AccuserRole, c.LawName);
            player.SendMessage(Fmt("CaseLine2", player, new object[] { SentenceText(c.Sentence), c.CrimeIds.Count,
                c.OpenedAt.ToString("MM-dd HH:mm"), c.ExpiresAt.ToString("MM-dd HH:mm") }));
            if (c.Status == CTrial)
                player.SendMessage(Fmt("CaseLine3", player, new object[] { c.Jurors.Count, c.Votes.Count, c.TrialEnds.ToString("HH:mm") }));
            if (c.Status == CCombat)
                player.SendMessage(Fmt("CaseLine4", player, new object[] { c.AccusedName, c.ChampionName, c.CombatEnds.ToString("HH:mm") }));
            if (!string.IsNullOrEmpty(c.Outcome)) player.SendMessage(Fmt("CaseLine5", player, new object[] { c.Outcome }));
            if (c.Status == CGuilty && c.FineRemaining > 0)
                player.SendMessage(Fmt("CaseLine6", player, new object[] { c.FineRemaining, c.Sentence.Item, c.FineDue.ToString("MM-dd HH:mm"), c.Id }));
        }

        // /court accuse <player> <law> [fine <n> "<item>" | outlaw <hours> | exile <hours>]
        private void Accuse(Player player, string[] args)
        {
            bool admin = IsAdmin(player);
            string role = AccuserRole(player);
            if (role == null && !admin) { ReplyError(player, "NotAccuser"); return; }
            if (role == null) role = "admin";
            if (args.Length < 3) { ShowHelp(player); return; }

            // The law is the first argument after the name that names a catalogue law, so unquoted multi-word names work.
            int lawIdx = -1;
            for (int i = 2; i < args.Length; i++) if (FindLawDef(args[i]) != null) { lawIdx = i; break; }
            if (lawIdx < 0) { ReplyError(player, "UnknownLaw", args[args.Length - 1]); return; }
            LawDef law = FindLawDef(args[lawIdx]);
            string targetId, targetName;
            if (!ResolvePlayerRef(string.Join(" ", args, 1, lawIdx - 1), out targetId, out targetName)) { ReplyError(player, "PlayerNotFound"); return; }
            if (targetId == player.Id.ToString()) { ReplyError(player, "SelfAccuse"); return; }
            if (config.MonarchImmune && IsMonarchId(targetId)) { ReplyError(player, "MonarchImmune"); return; }

            Sentence s;
            if (!ParseSentence(player, args, lawIdx + 1, law, out s)) return;

            DateTime now = DateTime.UtcNow;
            foreach (Case open in data.Cases)
                if (IsOpen(open) && open.AccusedId == targetId) { ReplyError(player, "OpenCaseExists", targetName, open.Id); return; }
            int openCount = 0;
            foreach (Case open in data.Cases) if (IsOpen(open)) openCount++;
            if (openCount >= config.MaxOpenCases) { ReplyError(player, "TooManyCases", openCount); return; }

            if (!admin)
            {
                int crownUsed = CountStamps("accuse", null, null, now.AddHours(-24)) + config.AcquittalPenalty * CountStamps("acquit", null, null, now.AddHours(-24));
                if (crownUsed >= config.CrownAccusationsPerDay) { ReplyError(player, "CrownQuota", config.CrownAccusationsPerDay); return; }
                if (CountStamps("accuse", player.Id.ToString(), null, now.AddHours(-24)) >= config.AccusationsPerAccuserPerDay)
                {
                    ReplyError(player, "AccuserQuota", config.AccusationsPerAccuserPerDay);
                    return;
                }
                int week = CountStamps("accuse", null, targetId, now.AddDays(-7));
                if (week >= config.AccusationsPerTargetPerWeek) { ReplyError(player, "TargetQuota", targetName, week); return; }
                DateTime lastAcquit = LastStamp("acquit", targetId);
                DateTime immuneUntil = lastAcquit.AddHours(config.AcquittalImmunityHours);
                if (lastAcquit != DateTime.MinValue && immuneUntil > now) { ReplyError(player, "AcquittedImmune", targetName, MinutesUntil(immuneUntil)); return; }
            }

            // Evidence: uncharged records of this law against the accused.
            var evidence = new List<Crime>();
            foreach (Crime cr in data.Crimes) if (cr.PlayerId == targetId && cr.LawId == law.Id && cr.CaseId == 0) evidence.Add(cr);
            if (evidence.Count == 0)
            {
                if (FindActive(law.Id) == null) { ReplyError(player, "LawNotInForce", law.Name, targetName); return; }
                if (law.Kind != KDeclared && config.RequireEvidenceForEnforcedLaws) { ReplyError(player, "NeedEvidence", law.Name, targetName); return; }
            }

            var c = new Case
            {
                Id = data.NextCaseId++,
                Status = CAccused,
                LawId = law.Id,
                LawName = law.Name,
                AccusedId = targetId,
                AccusedName = targetName,
                AccuserId = player.Id.ToString(),
                AccuserName = player.Name,
                AccuserRole = role,
                CrownId = data.CrownId,
                Sentence = s,
                OpenedAt = now,
                ExpiresAt = now.AddHours(config.CaseExpiryHours),
                ChampionId = player.Id.ToString(),
                ChampionName = player.Name
            };
            foreach (Crime cr in evidence)
            {
                cr.CaseId = c.Id;
                c.CrimeIds.Add(cr.Id);
                if (cr.VictimId != null) { c.VictimId = cr.VictimId; c.VictimName = cr.VictimName; }
            }
            data.Cases.Add(c);
            if (role != "admin") AddStamp("accuse", player.Id.ToString(), targetId, now);
            SaveData();
            Broadcast("Accused", player.Name, targetName, law.Name, c.Id, SentenceText(s), evidence.Count);
            NotifyId(targetId, "AccusedYou", c.Id);
            Chronicle("accusation", player.Name + " accuses " + targetName, player.Name + " (" + role + ") accuses " + targetName + " of breaking "
                + law.Name + " and seeks " + SentenceText(s) + ". Case #" + c.Id + ".", new[] { player.Name, targetName });
        }

        private bool ParseSentence(Player player, string[] args, int start, LawDef law, out Sentence s)
        {
            s = null;
            if (start >= args.Length)
            {
                if (law.DefaultSentence == null) { ReplyError(player, "NoDefaultSentence"); return false; }
                s = new Sentence { Kind = law.DefaultSentence, Amount = law.DefaultAmount, Item = law.DefaultItem };
            }
            else
            {
                string kind = args[start].ToLowerInvariant();
                int n;
                if (kind == "outlawry") kind = SOutlaw;
                if (kind == "exiled") kind = SExile;
                if ((kind != SFine && kind != SOutlaw && kind != SExile) || start + 1 >= args.Length || !int.TryParse(args[start + 1], out n))
                {
                    ReplyError(player, "BadSentence", config.MaxFineAmount, config.MaxOutlawHours, config.MaxExileHours);
                    return false;
                }
                s = new Sentence { Kind = kind, Amount = n };
                if (kind == SFine) s.Item = start + 2 < args.Length ? JoinFrom(args, start + 2) : law.DefaultItem;
            }
            int max = s.Kind == SFine ? config.MaxFineAmount : (s.Kind == SOutlaw ? config.MaxOutlawHours : config.MaxExileHours);
            if (s.Amount < 1 || s.Amount > max) { ReplyError(player, "BadNumber", s.Amount, 1, max); return false; }
            if (s.Kind == SFine)
            {
                InvItemBlueprint bp = FindItem(s.Item);
                if (bp == null) { ReplyError(player, "UnknownItem", s.Item ?? ""); return false; }
                if (config.AllowedFineItems.Count > 0 && !ContainsIgnoreCase(config.AllowedFineItems, bp.Name)) { ReplyError(player, "ItemNotAllowed", bp.Name); return false; }
                s.Item = bp.Name;
            }
            else s.Item = null;
            return true;
        }

        private void OpenTrial(Player player, string[] args)
        {
            Case c = args.Length > 1 ? FindCase(args[1]) : null;
            if (c == null) { ReplyError(player, "CaseNotFound", args.Length > 1 ? args[1] : ""); return; }
            if (c.Status != CAccused) { ReplyError(player, "CaseNotOpen", c.Id); return; }
            string pid = player.Id.ToString();
            if (pid != c.AccusedId && AccuserRole(player) == null && !IsAdmin(player)) { ReplyError(player, "NotParty"); return; }
            if (config.TrialRequiresAccusedOnline && OnlineById(c.AccusedId) == null)
            {
                // Logging off whenever a trial is called would let an accused run out every case's clock.
                bool absentia = config.TrialInAbsentiaAfterHours > 0 && (DateTime.UtcNow - c.OpenedAt).TotalHours >= config.TrialInAbsentiaAfterHours;
                if (!absentia)
                {
                    ReplyError(player, "AccusedOffline", c.AccusedName, config.TrialInAbsentiaAfterHours > 0 ? config.TrialInAbsentiaAfterHours.ToString() : "-");
                    return;
                }
            }
            List<Player> jurors = PickJurors(c);
            if (jurors.Count < config.JuryMin) { ReplyError(player, "TooFewJurors", jurors.Count, config.JuryMin); return; }
            DateTime now = DateTime.UtcNow;
            c.Status = CTrial;
            c.TrialAttempts++;
            c.TrialEnds = now.AddMinutes(config.TrialMinutes);
            c.Jurors = new List<string>();
            c.Votes = new Dictionary<string, bool>();
            foreach (Player j in jurors) c.Jurors.Add(j.Id.ToString());
            SaveData();
            Broadcast("TrialOpened", c.Id, c.AccusedName, c.LawName, jurors.Count);
            foreach (Player j in jurors) Reply(j, "JurorCalled", c.Id, c.AccusedName, c.LawName, SentenceText(c.Sentence), config.TrialMinutes);
        }

        // Sworn lords: online heads of houses, not party to the case, not of the accused's house, not the monarch or a
        // council member, and (by default) not of the crown's house; outlaws and exiles may not sit. Chosen at random.
        private List<Player> PickJurors(Case c)
        {
            var pool = new List<Player>();
            string accusedHouse = HouseOfId(c.AccusedId);
            string crownHouse = config.ExcludeCrownHouseFromJury ? CrownHouse() : null;
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                string id = p.Id.ToString();
                if (id == c.AccusedId || id == c.AccuserId || id == c.ChampionId || id == c.VictimId) continue;
                if (AccuserRole(p) != null) continue;               // the monarch and the council serve the prosecution
                if (IsPunished(data.Outlaws, id) || IsPunished(data.Exiles, id)) continue;
                string house = HouseOfId(id);
                if (house == null) continue;
                if (accusedHouse != null && string.Equals(house, accusedHouse, StringComparison.OrdinalIgnoreCase)) continue;
                if (crownHouse != null && string.Equals(house, crownHouse, StringComparison.OrdinalIgnoreCase)) continue;
                if (config.JurorsMustLeadHouse && !IsHouseLeader(p, house)) continue;
                if (config.JurorHouseMinMembers > 1)
                {
                    int members = HouseMemberCount(house);
                    if (members >= 0 && members < config.JurorHouseMinMembers) continue;
                }
                if (config.ExcludeAlliedJurors && accusedHouse != null && HousesAllied(house, accusedHouse)) continue;
                pool.Add(p);
            }
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                Player t = pool[i]; pool[i] = pool[j]; pool[j] = t;
            }
            if (pool.Count > config.JurySize) pool.RemoveRange(config.JurySize, pool.Count - config.JurySize);
            return pool;
        }

        private void Vote(Player player, string[] args)
        {
            Case c = args.Length > 1 ? FindCase(args[1]) : null;
            if (c == null) { ReplyError(player, "CaseNotFound", args.Length > 1 ? args[1] : ""); return; }
            if (c.Status != CTrial) { ReplyError(player, "CaseNotOpen", c.Id); return; }
            string id = player.Id.ToString();
            if (!c.Jurors.Contains(id)) { ReplyError(player, "NotJuror"); return; }
            string v = args.Length > 2 ? args[2].ToLowerInvariant() : "";
            bool guilty;
            if (v == "guilty" || v == "g" || v == "yes") guilty = true;
            else if (v == "innocent" || v == "i" || v == "no" || v == "notguilty") guilty = false;
            else { ShowHelp(player); return; }
            c.Votes[id] = guilty;
            dirty = true;
            Reply(player, "Voted");
            if (AllVoted(c)) ResolveTrial(c, DateTime.UtcNow);
        }

        private static bool AllVoted(Case c)
        {
            return c.Jurors.Count > 0 && c.Votes.Count >= c.Jurors.Count;
        }

        private void ResolveTrial(Case c, DateTime now)
        {
            int guilty = 0, innocent = 0;
            foreach (KeyValuePair<string, bool> kv in c.Votes) if (kv.Value) guilty++; else innocent++;
            if (guilty + innocent < config.MinVotes)
            {
                c.Jurors.Clear();
                c.Votes.Clear();
                dirty = true;
                if (c.TrialAttempts >= config.MaxTrialAttempts) { Broadcast("MistrialFinal", c.Id); Dismiss(c, "mistrial"); return; }
                Broadcast("Mistrial", c.Id);
                c.Status = CAccused;
                c.ExpiresAt = now.AddHours(config.CaseExpiryHours);
                return;
            }
            string tally = guilty + "-" + innocent;
            if (guilty > innocent) CloseGuilty(c, "jury " + tally);
            else CloseAcquitted(c, "jury " + tally + (guilty == innocent ? ", a tie acquits" : ""));
        }

        private void DemandCombat(Player player, string[] args)
        {
            Case c = args.Length > 1 ? FindCase(args[1]) : null;
            if (c == null) { ReplyError(player, "CaseNotFound", args.Length > 1 ? args[1] : ""); return; }
            if (!config.TrialByCombat) { ReplyError(player, "CombatOff"); return; }
            if (player.Id.ToString() != c.AccusedId) { ReplyError(player, "NotAccused"); return; }
            if (c.Status != CAccused && c.Status != CTrial) { ReplyError(player, "CaseNotOpen", c.Id); return; }
            if (c.CombatUsed) { ReplyError(player, "CombatUsed"); return; }
            if (c.Status == CTrial && c.Votes.Count > 0) { ReplyError(player, "CombatVotesCast"); return; }
            Player champion = OnlineById(c.ChampionId);
            if (champion == null) { ReplyError(player, "ChampionOffline", c.ChampionName, c.Id); return; }
            DateTime now = DateTime.UtcNow;
            c.Status = CCombat;
            c.CombatUsed = true;
            c.CombatEnds = now.AddMinutes(config.CombatWindowMinutes);
            c.Jurors.Clear();
            c.Votes.Clear();
            SaveData();
            Broadcast("CombatOpened", c.Id, c.AccusedName, c.ChampionName, config.CombatWindowMinutes);
            Chronicle("trial_by_combat", c.AccusedName + " demands trial by combat", c.AccusedName + " answers the charge of breaking " + c.LawName
                + " with steel. " + c.ChampionName + " stands for the crown.", new[] { c.AccusedName, c.ChampionName });
        }

        private void NameChampion(Player player, string[] args)
        {
            Case c = args.Length > 1 ? FindCase(args[1]) : null;
            if (c == null) { ReplyError(player, "CaseNotFound", args.Length > 1 ? args[1] : ""); return; }
            if (player.Id.ToString() != c.AccuserId && !IsAdmin(player)) { ReplyError(player, "NotCaseAccuser"); return; }
            if (c.Status != CAccused && c.Status != CTrial) { ReplyError(player, "CaseNotOpen", c.Id); return; }
            if (c.ChampionNamed && !IsAdmin(player)) { ReplyError(player, "ChampionNamedAlready"); return; }
            Player champ = args.Length > 2 ? FindOnline(JoinFrom(args, 2)) : null;
            if (champ == null) { ReplyError(player, "PlayerNotFound"); return; }
            string cid = champ.Id.ToString();
            string accusedHouse = HouseOfId(c.AccusedId);
            if (cid == c.AccusedId || c.Jurors.Contains(cid) || IsPunished(data.Outlaws, cid)
                || (accusedHouse != null && string.Equals(HouseOfId(cid), accusedHouse, StringComparison.OrdinalIgnoreCase)))
            {
                ReplyError(player, "BadChampion");
                return;
            }
            c.ChampionId = cid;
            c.ChampionName = champ.Name;
            c.ChampionNamed = true;
            SaveData();
            Broadcast("ChampionNamed", champ.Name, c.Id);
        }

        private void PayFine(Player player, string[] args)
        {
            Case c = args.Length > 1 ? FindCase(args[1]) : null;
            if (c == null || c.AccusedId != player.Id.ToString() || c.Status != CGuilty || c.FineRemaining <= 0)
            {
                ReplyError(player, "NoFine", args.Length > 1 ? args[1] : "");
                return;
            }
            CollectFine(c, player);
        }

        private void ListOutlaws(Player player)
        {
            if (data.Outlaws.Count == 0 && data.Exiles.Count == 0) { Reply(player, "OutlawsNone"); return; }
            Reply(player, "OutlawsHeader");
            int n = 0;
            foreach (KeyValuePair<string, Punishment> kv in data.Outlaws)
                if (n++ < config.MaxListLines) player.SendMessage(Fmt("OutlawLine", player, new object[] { kv.Value.Name, "outlaw", kv.Value.Until.ToString("MM-dd HH:mm"), kv.Value.CaseId }));
            foreach (KeyValuePair<string, Punishment> kv in data.Exiles)
                if (n++ < config.MaxListLines) player.SendMessage(Fmt("OutlawLine", player, new object[] { kv.Value.Name, "exile", kv.Value.Until.ToString("MM-dd HH:mm"), kv.Value.CaseId }));
            if (n > config.MaxListLines) player.SendMessage(Fmt("ListMore", player, new object[] { n - config.MaxListLines }));
        }

        private void Pardon(Player player, string[] args)
        {
            bool admin = IsAdmin(player);
            if (!IsMonarch(player) && !admin) { ReplyError(player, "NotKing"); return; }
            string id, name;
            if (args.Length < 2 || !ResolvePlayerRef(JoinFrom(args, 1), out id, out name)) { ReplyError(player, "PlayerNotFound"); return; }
            DateTime now = DateTime.UtcNow;
            if (!admin && CountStamps("pardon", null, null, now.AddHours(-24)) >= config.PardonsPerDay) { ReplyError(player, "PardonQuota", config.PardonsPerDay); return; }
            bool any = data.Outlaws.Remove(id) | data.Exiles.Remove(id);
            foreach (Case c in data.Cases)
                if (c.AccusedId == id && c.Status == CGuilty && c.FineRemaining > 0) { c.FineRemaining = 0; c.Outcome += "; fine pardoned"; any = true; }
            if (!any) { ReplyError(player, "NothingToPardon", name); return; }
            if (!admin) AddStamp("pardon", player.Id.ToString(), id, now);
            if (RealmContracts != null && config.OfferOutlawryToContracts) RealmContracts.Call("PardonOutlaw", id);   // returns null if absent
            SaveData();
            Broadcast("Pardoned", player.Name, name);
            Chronicle("pardon", player.Name + " pardons " + name, player.Name + " lifts the court's sentences on " + name + ".", new[] { player.Name, name });
        }

        private void AdminCmd(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string op = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (op == "dismiss" && args.Length > 2)
            {
                Case c = FindCase(args[2]);
                if (c == null || !IsOpen(c)) { ReplyError(player, "CaseNotFound", args[2]); return; }
                Dismiss(c, "by order of " + player.Name);
                Reply(player, "AdminDone", "dismiss #" + c.Id);
                return;
            }
            if (op == "verdict" && args.Length > 3)
            {
                Case c = FindCase(args[2]);
                if (c == null || !IsOpen(c)) { ReplyError(player, "CaseNotFound", args[2]); return; }
                if (args[3].ToLowerInvariant() == "guilty") CloseGuilty(c, "ruled by " + player.Name);
                else CloseAcquitted(c, "ruled by " + player.Name);
                Reply(player, "AdminDone", "verdict #" + c.Id);
                return;
            }
            if (op == "clear" && args.Length > 2)
            {
                string id, name;
                if (!ResolvePlayerRef(JoinFrom(args, 2), out id, out name)) { ReplyError(player, "PlayerNotFound"); return; }
                data.Outlaws.Remove(id);
                data.Exiles.Remove(id);
                foreach (Case c in data.Cases.ToArray())
                {
                    if (c.AccusedId != id) continue;
                    if (IsOpen(c)) Dismiss(c, "cleared by " + player.Name);
                    else if (c.FineRemaining > 0) c.FineRemaining = 0;
                }
                SaveData();
                Reply(player, "Cleared", name);
                return;
            }
            ShowHelp(player);
        }

        #endregion

        #region Verdicts and sentences

        private void CloseGuilty(Case c, string how)
        {
            DateTime now = DateTime.UtcNow;
            c.Status = CGuilty;
            c.Outcome = "guilty, " + how;
            c.ClosedAt = now;
            SaveData();
            Broadcast("Guilty", c.Id, c.AccusedName, c.LawName, how, SentenceText(c.Sentence));
            Chronicle("verdict", c.AccusedName + " is found guilty", c.AccusedName + " is guilty of breaking " + c.LawName + " (" + how + "). Sentence: "
                + SentenceText(c.Sentence) + ".", new[] { c.AccusedName, c.AccuserName });
            ApplySentence(c, now);
        }

        private void CloseAcquitted(Case c, string how)
        {
            DateTime now = DateTime.UtcNow;
            c.Status = CAcquitted;
            c.Outcome = "innocent, " + how;
            c.ClosedAt = now;
            ReleaseCrimes(c);
            if (c.AccuserRole != "admin") AddStamp("acquit", c.AccuserId, c.AccusedId, now);
            SaveData();
            Broadcast("Acquitted", c.Id, c.AccusedName, c.LawName, how);
            Chronicle("verdict", c.AccusedName + " is acquitted", c.AccusedName + " is found innocent of breaking " + c.LawName + " (" + how
                + "). The charge brought by " + c.AccuserName + " fails.", new[] { c.AccusedName, c.AccuserName });
        }

        private void Dismiss(Case c, string why)
        {
            c.Status = CDismissed;
            c.Outcome = why;
            c.ClosedAt = DateTime.UtcNow;
            ReleaseCrimes(c);
            dirty = true;
            Broadcast("Dismissed", c.Id, why);
        }

        // Records of a case that ends without conviction stay on the ledger (marked with the negative case id) but can
        // never be cited again: no one is tried twice on the same evidence.
        private void ReleaseCrimes(Case c)
        {
            foreach (Crime cr in data.Crimes) if (cr.CaseId == c.Id) cr.CaseId = -c.Id;   // negative: tried, not proven
        }

        private void ApplySentence(Case c, DateTime now)
        {
            Sentence s = c.Sentence;
            if (s.Kind == SOutlaw) { SetOutlaw(c.AccusedId, c.AccusedName, s.Amount, c.Id); return; }
            if (s.Kind == SExile)
            {
                data.Exiles[c.AccusedId] = new Punishment { Name = c.AccusedName, Until = now.AddHours(s.Amount), CaseId = c.Id };
                SaveData();
                Broadcast("Exiled", c.AccusedName, s.Amount);
                return;
            }
            c.FineRemaining = s.Amount;
            c.FineDue = now.AddHours(config.FinePayHours);
            Player convict = OnlineById(c.AccusedId);
            // A convict sentenced while offline cannot pay: the pay window starts when they are next seen (see
            // OnPlayerConnected), so staying away merely delays the fine and coming back never finds them outlawed.
            c.FineNoticed = convict != null;
            SaveData();
            if (convict != null) CollectFine(c, convict);
        }

        // Takes what the convict carries, up to what is owed, measured with AutoCount before and after.
        private void CollectFine(Case c, Player convict)
        {
            InvItemBlueprint bp = FindItem(c.Sentence.Item);
            if (bp == null) return;
            int taken = TakeUpTo(convict, bp, c.FineRemaining);
            if (taken > 0)
            {
                c.FineRemaining -= taken;
                if (config.FineDestination == "victim" && c.VictimId != null) AddOwed(c.VictimId, c.VictimName, bp.Name, taken);
                SaveData();                                          // record the take at once
                Player victim = c.VictimId != null ? OnlineById(c.VictimId) : null;
                if (victim != null && config.FineDestination == "victim") PayOwed(victim);
            }
            if (c.FineRemaining <= 0) { c.FineRemaining = 0; Reply(convict, "FinePaid", c.Id); SaveData(); return; }
            if (taken > 0) Reply(convict, "FinePartial", taken, bp.Name, c.FineRemaining, c.FineDue.ToString("MM-dd HH:mm"));
            else Reply(convict, "FineNone", bp.Name, c.FineRemaining, c.FineDue.ToString("MM-dd HH:mm"), c.Id);
        }

        private void SetOutlaw(string playerId, string name, int hours, int caseId)
        {
            DateTime until = DateTime.UtcNow.AddHours(hours);
            Punishment existing;
            if (data.Outlaws.TryGetValue(playerId, out existing) && existing.Until > until) until = existing.Until;   // never shortens
            data.Outlaws[playerId] = new Punishment { Name = name, Until = until, CaseId = caseId };
            data.Exiles.Remove(playerId);                            // outlawry supersedes exile
            SaveData();
            Broadcast("Outlawed", name, hours);
            // Optional integration: RealmContracts' non-public bool ProclaimOutlaw(string playerId, string name, int hours,
            // string by) mirrors the sentence so bounties can be posted. Returns null if RealmContracts is not loaded.
            if (RealmContracts != null && config.OfferOutlawryToContracts)
            {
                object r = RealmContracts.Call("ProclaimOutlaw", playerId, name, hours, "the court");
                if (r is bool && (bool)r) Puts("RealmContracts accepted the outlawry of " + name);
            }
        }

        #endregion

        #region Items (fines)

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

        // Removes up to `amount` (as many as the player carries); returns the measured number removed.
        private static int TakeUpTo(Player player, InvItemBlueprint bp, int amount)
        {
            ItemCollection items = InventoryOf(player);
            if (items == null || bp == null || amount <= 0) return 0;
            int before = ItemCollection.AutoCount(items, bp);
            int want = Math.Min(before, amount);
            if (want <= 0) return 0;
            ItemCollection.AutoSplit(items, bp, want);
            int taken = before - ItemCollection.AutoCount(items, bp);
            return taken < 0 ? 0 : Math.Min(taken, want);
        }

        // Returns the measured number added (same pattern as RealmContracts.GiveItems).
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

        private void AddOwed(string playerId, string playerName, string item, int amount)
        {
            if (amount <= 0 || playerId == null) return;
            foreach (Owed o in data.Owed)
                if (o.PlayerId == playerId && string.Equals(o.Item, item, StringComparison.OrdinalIgnoreCase))
                {
                    o.Amount += amount;
                    if (playerName != null) o.PlayerName = playerName;
                    return;
                }
            data.Owed.Add(new Owed { PlayerId = playerId, PlayerName = playerName, Item = item, Amount = amount });
        }

        private bool HasOwed(string playerId)
        {
            foreach (Owed o in data.Owed) if (o.PlayerId == playerId) return true;
            return false;
        }

        private void PayOwed(Player player)
        {
            if (player == null || player.IsServer || player.Entity == null || data == null) return;
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
                    SaveData();                                      // record each delivery as it happens
                    Reply(player, "OwedPaid", given, bp.Name);
                }
                if (o.Amount > 0) Reply(player, "OwedFull", o.Amount, o.Item);
            }
            if (data.Owed.RemoveAll(delegate(Owed o) { return o.Amount <= 0; }) > 0) SaveData();
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        private bool IsCourtOutlaw(string playerId)
        {
            return data != null && playerId != null && IsPunished(data.Outlaws, playerId);
        }

        private bool IsExiled(string playerId)
        {
            return data != null && playerId != null && IsPunished(data.Exiles, playerId);
        }

        // "id|name|untilIso|caseId" for every current court outlaw.
        private string[] GetCourtOutlaws()
        {
            var list = new List<string>();
            if (data == null) return list.ToArray();
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<string, Punishment> kv in data.Outlaws)
                if (kv.Value != null && kv.Value.Until > now) list.Add(kv.Key + "|" + kv.Value.Name + "|" + kv.Value.Until.ToString("o") + "|" + kv.Value.CaseId);
            return list.ToArray();
        }

        // "id|name|kind" for every law in force (or proclaimed and within its grace period).
        private string[] GetActiveLaws()
        {
            var list = new List<string>();
            if (data == null) return list.ToArray();
            foreach (ActiveLaw a in data.Laws)
            {
                LawDef l = FindLawDef(a.Id);
                if (l != null) list.Add(l.Id + "|" + l.Name + "|" + l.Kind);
            }
            return list.ToArray();
        }

        private int GetCrimeCount(string playerId)
        {
            int n = 0;
            if (data == null) return n;
            foreach (Crime c in data.Crimes) if (c.PlayerId == playerId) n++;
            return n;
        }

        #endregion

        #region Helpers

        private bool IsOpen(Case c)
        {
            return c.Status == CAccused || c.Status == CTrial || c.Status == CCombat;
        }

        private Case FindCase(int id)
        {
            foreach (Case c in data.Cases) if (c.Id == id) return c;
            return null;
        }

        private Case FindCase(string text)
        {
            int id;
            return int.TryParse((text ?? "").TrimStart('#'), out id) ? FindCase(id) : null;
        }

        private static bool IsPunished(Dictionary<string, Punishment> map, string id)
        {
            Punishment p;
            return map.TryGetValue(id, out p) && p != null && p.Until > DateTime.UtcNow;
        }

        private void AddStamp(string kind, string actor, string target, DateTime at)
        {
            data.History.Add(new Stamp { Kind = kind, ActorId = actor, TargetId = target, At = at });
        }

        private int CountStamps(string kind, string actor, string target, DateTime since)
        {
            int n = 0;
            foreach (Stamp s in data.History)
                if (s.Kind == kind && s.At >= since && (actor == null || s.ActorId == actor) && (target == null || s.TargetId == target)) n++;
            return n;
        }

        private DateTime LastStamp(string kind, string target)
        {
            DateTime last = DateTime.MinValue;
            foreach (Stamp s in data.History) if (s.Kind == kind && s.TargetId == target && s.At > last) last = s.At;
            return last;
        }

        private string KindLabel(LawDef l)
        {
            if (l.Kind == KDeclared) return "declared";
            return l.Kind + (l.Block && l.Kind != KCurfew ? ", blocks" : "") + (l.Kind == KCurfew ? " " + l.CurfewStartHourUtc + "-" + l.CurfewEndHourUtc + " UTC" : "");
        }

        private string ZoneLabel(LawDef l)
        {
            if (l.Kind == KDeclared) return "";
            return l.Zone == "*" ? ", all towns" : ", " + l.Zone;
        }

        private string ZoneName(LawDef l, Vector3 pos)
        {
            ZoneDef z = LawZoneAt(l, pos);
            return z != null ? z.Name : "a forbidden place";
        }

        private string DefaultSentenceText(LawDef l)
        {
            if (l.DefaultSentence == null) return "none";
            return SentenceText(new Sentence { Kind = l.DefaultSentence, Amount = l.DefaultAmount, Item = l.DefaultItem });
        }

        private static string SentenceText(Sentence s)
        {
            if (s == null) return "none";
            if (s.Kind == SFine) return "a fine of " + s.Amount + " " + s.Item;
            if (s.Kind == SOutlaw) return "outlawry for " + s.Amount + " h";
            return "exile from the towns for " + s.Amount + " h";
        }

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private static bool IsMonarch(Player player)
        {
            KingsScheme ks = SocialAPI.Get<KingsScheme>();
            return ks != null && ks.HasKing() && ks.IsKing(player);
        }

        private static bool IsMonarchId(string id)
        {
            KingsScheme ks = SocialAPI.Get<KingsScheme>();
            return ks != null && ks.HasKing() && ks.GetKingID().ToString() == id;
        }

        // "monarch", the council seat name, or null.
        private string AccuserRole(Player player)
        {
            if (IsMonarch(player)) return "monarch";
            if (!config.CouncilMayAccuse || CrownAndConsequences == null) return null;
            string seat = CrownAndConsequences.Call("GetCouncilSeat", player.Id) as string;
            return string.IsNullOrEmpty(seat) ? null : seat;
        }

        private bool RebellionActive()
        {
            if (CrownAndConsequences == null) return false;
            object r = CrownAndConsequences.Call("IsRebellionActive");
            return r is bool && (bool)r;
        }

        // RealmHouses is authoritative when loaded (same rule as CrownAndConsequences.HouseOf); else the game guild.
        private string HouseOfId(string playerId)
        {
            if (playerId == null) return null;
            if (RealmHouses != null)
            {
                string h = RealmHouses.Call("GetHouse", playerId) as string;
                return string.IsNullOrEmpty(h) ? null : h;
            }
            ulong id;
            if (!ulong.TryParse(playerId, out id)) return null;
            GuildScheme guilds = SocialAPI.Get<GuildScheme>();
            Guild g = guilds != null ? guilds.TryGetGuildByMember(id) : null;
            return g != null ? g.Name : null;
        }

        // Members of a house per RealmHouses, or -1 when that cannot be told (plugin absent or no answer).
        private int HouseMemberCount(string house)
        {
            if (RealmHouses == null || house == null) return -1;
            List<string> ids = RealmHouses.Call("GetMembers", house) as List<string>;
            return ids != null ? ids.Count : -1;
        }

        // Liege, vassal or treaty partner (RealmHouses); false when RealmHouses is absent.
        private bool HousesAllied(string a, string b)
        {
            if (RealmHouses == null || a == null || b == null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
            string la = RealmHouses.Call("GetLiege", a) as string, lb = RealmHouses.Call("GetLiege", b) as string;
            if (la != null && string.Equals(la, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (lb != null && string.Equals(lb, a, StringComparison.OrdinalIgnoreCase)) return true;
            object t = RealmHouses.Call("HasTreaty", a, b);
            return t is bool && (bool)t;
        }

        private bool IsHouseLeader(Player player, string house)
        {
            if (RealmHouses != null)
            {
                string leader = RealmHouses.Call("GetHouseLeader", house) as string;
                return leader != null && leader == player.Id.ToString();
            }
            Guild g = player.GetGuild();
            return g != null && g.OwnerId == player.Id;
        }

        private string CrownHouse()
        {
            if (CrownAndConsequences != null)
            {
                string h = CrownAndConsequences.Call("GetKingHouse") as string;
                if (!string.IsNullOrEmpty(h)) return h;
            }
            KingsScheme ks = SocialAPI.Get<KingsScheme>();
            return ks != null && ks.HasKing() ? HouseOfId(ks.GetKingID().ToString()) : null;
        }

        private bool IsCrownHouseMember(Player player)
        {
            string crown = CrownHouse();
            string mine = HouseOfId(player.Id.ToString());
            return crown != null && mine != null && string.Equals(crown, mine, StringComparison.OrdinalIgnoreCase);
        }

        // An online player by name, or else the latest crime record or case with that exact name (offline players).
        private bool ResolvePlayerRef(string name, out string id, out string resolvedName)
        {
            id = null;
            resolvedName = null;
            if (string.IsNullOrEmpty(name)) return false;
            Player p = FindOnline(name);
            if (p != null) { id = p.Id.ToString(); resolvedName = p.Name; return true; }
            for (int i = data.Crimes.Count - 1; i >= 0; i--)
                if (string.Equals(data.Crimes[i].PlayerName, name, StringComparison.OrdinalIgnoreCase))
                {
                    id = data.Crimes[i].PlayerId;
                    resolvedName = data.Crimes[i].PlayerName;
                    return true;
                }
            for (int i = data.Cases.Count - 1; i >= 0; i--)
                if (string.Equals(data.Cases[i].AccusedName, name, StringComparison.OrdinalIgnoreCase))
                {
                    id = data.Cases[i].AccusedId;
                    resolvedName = data.Cases[i].AccusedName;
                    return true;
                }
            foreach (KeyValuePair<string, Punishment> kv in data.Outlaws)
                if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) { id = kv.Key; resolvedName = kv.Value.Name; return true; }
            foreach (KeyValuePair<string, Punishment> kv in data.Exiles)
                if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)) { id = kv.Key; resolvedName = kv.Value.Name; return true; }
            return false;
        }

        private static Player OnlineById(string id)
        {
            ulong n;
            if (!ulong.TryParse(id, out n)) return null;
            Player p = Server.GetPlayerById(n);
            return p != null && !p.IsServer && Server.PlayerIsOnline(n) ? p : null;
        }

        private static Player FindOnline(string name)
        {
            Player exact = Server.GetPlayerByName(name);
            if (exact != null && !exact.IsServer) return exact;
            List<Player> matches = Server.MatchPlayerByName(name);
            if (matches == null) return null;
            Player found = null;
            foreach (Player p in matches)
            {
                if (p == null || p.IsServer) continue;
                if (found != null) return null;                  // ambiguous
                found = p;
            }
            return found;
        }

        private static string JoinFrom(string[] args, int start)
        {
            return start >= args.Length ? "" : string.Join(" ", args, start, args.Length - start);
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list) if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int MinutesUntil(DateTime when)
        {
            return Math.Max(0, (int)Math.Ceiling((when - DateTime.UtcNow).TotalMinutes));
        }

        // These event types are registered in RealmChronicle's KnownTypes. With an older RealmChronicle that rejects them,
        // until a type has been accepted once a rejection falls back to "decree" so nothing is lost.
        // Capped per hour so court churn cannot push coronations and rebellions out of the chronicle's retention window.
        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            DateTime now = DateTime.UtcNow;
            while (chronicleTimes.Count > 0 && (now - chronicleTimes.Peek()).TotalHours >= 1) chronicleTimes.Dequeue();
            if (chronicleTimes.Count >= config.ChronicleMaxPerHour) return;
            chronicleTimes.Enqueue(now);
            string[] a = actors ?? new string[0];
            object r = RealmChronicle.Call("Log", type, title, detail, a);
            bool accepted = r is int && (int)r > 0;
            if (accepted) { chronicleTypeAccepted[type] = true; return; }
            if (type != "decree" && !chronicleTypeAccepted.ContainsKey(type))
                RealmChronicle.Call("Log", "decree", title, detail, a);
        }

        #endregion
    }
}
