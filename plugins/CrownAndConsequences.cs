// CrownAndConsequences: the contested crown of the Realm.
//
// Tracks the reigning king and their house, limits royal power (decrees cost Crown
// Authority and have cooldowns), keeps a small council, gates throne captures to
// declared rebellion windows, caps taxes, and bounds ransom holds in time and amount.
// Every public act is written to the Realm Chronicle via RealmChronicle.Log(...).
//
// API tags below refer to docs/oxide-rok-api.md ([SRC]/[OPJ]/[ASM]/[USE]).
// Anything not covered there is isolated and marked // UNVERIFIED:.
//
// Language level: C# 3 syntax only (no expression-bodied members, index initializers, $"", ?. or nameof),
// against the .NET 3.5 API surface, so the file builds with any Oxide compiler generation.
//
// Royal Stores (formerly "Harvest Tithe"): no gather hook can exist server-side. In the shipped patched
// Assembly-CSharp.dll every gather path (Harvester.Use, CollectResourceOnInteract, CollectableResource,
// SpriteObjectInstanceGiveResource, ItemListener.OnAddResource) resolves ResourceHandler from
// Entity.LocalPlayer / Player.Local, i.e. on the game CLIENT, and the OPJ injects no gather hook [IL][OPJ].
// So the decree now grants real items server-side to online members of crown-sworn houses, using the
// same calls the game's own server /give command makes (ThronesCommandHandler.Give: GetContainerOfType
// (entity, CollectionTypes.Inventory) + ItemCollection.AutoMergeAdd) [IL]. See docs/oxide-rok-api.md 3.9.
//
// Anti-abuse (tools/exploit-review/README.md): a claim needs a house of ClaimMinMembers and at most MaxOpenClaims
// claims run at once (sham one-man rebellions would suspend the King's Peace, open raid hours and farm renown);
// council changes are capped per day; council and ransom chronicle lines are capped per hour.
//
// The plugin.Call API (GetKingName, GetOpenClaims, IsSwornToCrown, ...) MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using CodeHatch;                              // ResourceType enum [ASM]
using CodeHatch.Common;                       // PlayerExtensions: SendMessage, SendError, GetGuild [ASM]
using CodeHatch.Engine.Modules.SocialSystem;  // SocialAPI [ASM]
using CodeHatch.Engine.Networking;            // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;         // InvItemBlueprint [ASM]
using CodeHatch.Inventory.Blueprints.Components; // ContainerManagement (StackLimit) [ASM; IL ThronesCommandHandler.Give]
using CodeHatch.ItemContainer;                // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events;            // PlayerCaptureEvent (as in the doc skeleton) [ASM]
using CodeHatch.Thrones.AncientThrone;        // AncientThroneCaptureEvent/ReleaseEvent/TaxEvent [ASM]
using CodeHatch.Thrones.Capture;              // PlayerCaptureManager [ASM; USE LockPickManager.cs:152]
using CodeHatch.Thrones.SocialSystem;         // Guild, GuildScheme, KingsScheme, KingsRealm [ASM]
using Oxide.Core;                             // Interface [SRC]
using Oxide.Core.Plugins;                     // Plugin [SRC Oxide.Core src/Plugins/Plugin.cs]

// Namespaces checked against the metadata of the patched Assembly-CSharp.dll in the 2.0.3867 zip:
// AncientThroneReleaseEvent and AncientThroneTaxEvent are in CodeHatch.Thrones.AncientThrone;
// PlayerCaptureEvent and PlayerEscapeEvent are in CodeHatch.Networking.Events; DamageType is
// CodeHatch.Damaging.DamageType; PlayerCaptureManager is CodeHatch.Thrones.Capture.PlayerCaptureManager.

namespace Oxide.Plugins
{
    [Info("CrownAndConsequences", "Realm", "0.1.0")]
    [Description("Contested crown: limited decrees, council, declared rebellions, capped tax and bounded ransom")]
    public class CrownAndConsequences : ReignOfKingsPlugin
    {
        // [SRC] Oxide.CSharp src/CSharpPlugin.cs:91 (filled by plugin name) and Oxide.Core Plugin.cs:311 (Call).
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;

        private const string PermAdmin = "crownandconsequences.admin";
        private const string DataName = "CrownAndConsequences";
        private const float TickSeconds = 15f;
        private const int MaxReleaseAttempts = 8;          // automatic game-release tries after a term ends (~2 min)

        private const string EffectProclamation = "proclamation";
        private const string EffectProvision = "crown_provision";
        private const string LegacyEffectGather = "gather_multiplier";  // old Harvest Tithe; migrated on load
        private const string EffectTax = "tax_rate";

        private PluginConfig config;
        private StoredData data;
        private readonly Dictionary<ulong, DateTime> lastWarned = new Dictionary<ulong, DateTime>();
        private bool initialized;
        private DateTime lastTaxLogged = DateTime.MinValue;          // in-memory throttle for tax chronicle lines
        private DateTime lastCouncilChange = DateTime.MinValue;      // in-memory throttle for council changes

        #region Config

        private class DecreeDef
        {
            public string Id;
            public string Name;
            public string Effect;              // proclamation | crown_provision | tax_rate
            public float Value;                // crown_provision: units per member per grant; tax_rate: tax value
            public string Item;                // crown_provision: ResourceType name (e.g. "Wood") or item name
            public int IntervalMinutes;        // crown_provision: minutes between grants (first grant at issue)
            public int DurationMinutes;
            public int CooldownMinutes;
            public float AuthorityCost;
            public string Proclamation;        // "{king}" is replaced with the king's name
        }

        private class RebellionWindow
        {
            public string Day;                 // DayOfWeek name, e.g. "Saturday"
            public string Start;               // "HH:mm" in realm time (UTC + UtcOffsetHours)
            public int DurationMinutes;
        }

        private class PluginConfig
        {
            public float StartingAuthority;
            public float AuthorityPerMinute;
            public float MaxAuthority;
            public double FreshAuthorityAfterHours;    // a new monarch gets StartingAuthority only this long after the last grant
            public int GlobalDecreeCooldownMinutes;
            public List<DecreeDef> Decrees;

            public List<string> CouncilSeats;
            public List<string> SeatsThatMayProclaim;
            public bool ClearCouncilOnSuccession;
            public int CouncilChangeCooldownSeconds;

            public double UtcOffsetHours;
            public List<RebellionWindow> RebellionWindows;
            public int ClaimMinNoticeMinutes;
            public int ClaimCooldownHours;
            public bool ClaimRequiresHouseLeader;
            public bool GateThroneCaptureToWindows;
            public bool AllowCaptureWhenThroneVacant;
            public bool OnlyClaimantsMayCapture;

            public float MaxTaxAbsolute;               // < 0 = use MaxTaxFractionOfGameMaximum
            public float MaxTaxFractionOfGameMaximum;

            public int RansomMaxMinutes;
            public int RansomMaxAmount;
            public string RansomCurrency;
            public int RansomPaidGraceMinutes;
            public int RecaptureImmunityMinutes;
            public bool AllowSelfReleaseAfterExpiry;
            public int MaxRansomChanges;
            public int TaxLogCooldownMinutes;

            // Anti-abuse (initialised here so configs written before these keys existed get the safe values).
            public int ClaimMinMembers = 3;            // a house needs this many members to declare a claim (0 = any)
            public int MaxOpenClaims = 3;              // pending + active claims at once, realm-wide (0 = no cap)
            public int CouncilChangesPerDay = 8;       // appointments + dismissals per rolling 24 h (admins exempt)
            public int MinorChronicleMaxPerHour = 12;  // council changes and ransom lines; coronations, claims and
                                                       // decrees are never capped. Over the cap: server log only.

            public static PluginConfig Defaults()
            {
                return new PluginConfig
                {
                    StartingAuthority = 30f,
                    AuthorityPerMinute = 0.5f,
                    MaxAuthority = 100f,
                    FreshAuthorityAfterHours = 6,
                    GlobalDecreeCooldownMinutes = 15,
                    Decrees = new List<DecreeDef>
                    {
                        StoresDecree(),
                        new DecreeDef { Id = "peace", Name = "King's Peace", Effect = EffectProclamation, Value = 0f,
                            DurationMinutes = 60, CooldownMinutes = 180, AuthorityCost = 20f,
                            Proclamation = "{king} proclaims the King's Peace. Let no blade be drawn on the roads of the realm." },
                        new DecreeDef { Id = "roads", Name = "Open Roads", Effect = EffectProclamation, Value = 0f,
                            DurationMinutes = 60, CooldownMinutes = 120, AuthorityCost = 10f,
                            Proclamation = "{king} declares the roads open to all travellers and traders." },
                        new DecreeDef { Id = "relief", Name = "Tax Relief", Effect = EffectTax, Value = 0f,
                            DurationMinutes = 60, CooldownMinutes = 360, AuthorityCost = 30f,
                            Proclamation = "{king} lifts the crown's tax for a time." }
                    },
                    CouncilSeats = new List<string> { "Voice of the Crown", "Keeper of Coin", "Marshal" },
                    SeatsThatMayProclaim = new List<string> { "Voice of the Crown" },
                    ClearCouncilOnSuccession = true,
                    CouncilChangeCooldownSeconds = 60,
                    UtcOffsetHours = 0,
                    RebellionWindows = new List<RebellionWindow>
                    {
                        new RebellionWindow { Day = "Wednesday", Start = "19:00", DurationMinutes = 60 },
                        new RebellionWindow { Day = "Saturday", Start = "19:00", DurationMinutes = 90 }
                    },
                    ClaimMinNoticeMinutes = 60,
                    ClaimCooldownHours = 72,
                    ClaimRequiresHouseLeader = true,
                    GateThroneCaptureToWindows = true,
                    AllowCaptureWhenThroneVacant = true,
                    OnlyClaimantsMayCapture = true,
                    MaxTaxAbsolute = -1f,
                    MaxTaxFractionOfGameMaximum = 0.5f,
                    RansomMaxMinutes = 10,
                    RansomMaxAmount = 500,
                    RansomCurrency = "gold",
                    RansomPaidGraceMinutes = 2,
                    RecaptureImmunityMinutes = 15,
                    AllowSelfReleaseAfterExpiry = true,
                    MaxRansomChanges = 3,
                    TaxLogCooldownMinutes = 10
                };
            }
        }

        // Replaces the old Harvest Tithe. Grants are real items (see header); the item name is UNVERIFIED until
        // seen in-game: if the blueprint is not found the decree refuses to issue and costs nothing.
        private static DecreeDef StoresDecree()
        {
            return new DecreeDef { Id = "stores", Name = "Royal Stores", Effect = EffectProvision, Value = 25f,
                Item = "Wood", IntervalMinutes = 15, DurationMinutes = 60, CooldownMinutes = 240, AuthorityCost = 40f,
                Proclamation = "{king} opens the royal stores. Every house sworn to the crown draws timber from them this hour." };
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(PluginConfig.Defaults(), true);
        }

        // Lists are null in the class and filled here, so Newtonsoft never appends to pre-filled defaults.
        private void FillMissingConfig()
        {
            PluginConfig d = PluginConfig.Defaults();
            if (config.Decrees == null) config.Decrees = d.Decrees;
            if (config.CouncilSeats == null) config.CouncilSeats = d.CouncilSeats;
            if (config.SeatsThatMayProclaim == null) config.SeatsThatMayProclaim = d.SeatsThatMayProclaim;
            if (config.RebellionWindows == null) config.RebellionWindows = d.RebellionWindows;
            if (string.IsNullOrEmpty(config.RansomCurrency)) config.RansomCurrency = d.RansomCurrency;
            if (config.RansomMaxMinutes <= 0) config.RansomMaxMinutes = d.RansomMaxMinutes;
            if (config.RansomMaxAmount <= 0) config.RansomMaxAmount = d.RansomMaxAmount;
            if (config.RansomPaidGraceMinutes < 0) config.RansomPaidGraceMinutes = 0;
            if (config.RecaptureImmunityMinutes < 0) config.RecaptureImmunityMinutes = 0;
            if (config.MaxRansomChanges <= 0) config.MaxRansomChanges = d.MaxRansomChanges;
            if (config.AuthorityPerMinute < 0f) config.AuthorityPerMinute = 0f;
            if (config.MaxAuthority <= 0f) config.MaxAuthority = d.MaxAuthority;
            if (config.StartingAuthority > config.MaxAuthority) config.StartingAuthority = config.MaxAuthority;
            if (config.FreshAuthorityAfterHours < 0) config.FreshAuthorityAfterHours = 0;
            foreach (DecreeDef def in config.Decrees)
                if (def != null && def.AuthorityCost < 0f) def.AuthorityCost = 0f;
            config.Decrees.RemoveAll(IsBrokenDecree);

            // Migrate the old Harvest Tithe (an inert gather multiplier) to Royal Stores in existing configs.
            bool migrated = false;
            for (int i = 0; i < config.Decrees.Count; i++)
            {
                if (config.Decrees[i].Effect != LegacyEffectGather) continue;
                PrintWarning("Decree '" + config.Decrees[i].Id + "' used gather_multiplier, which cannot work (no server-side "
                    + "gather hook exists). Replaced with Royal Stores (crown_provision).");
                config.Decrees[i] = StoresDecree();
                migrated = true;
            }
            foreach (DecreeDef def in config.Decrees)
            {
                if (def.Effect != EffectProvision) continue;
                if (def.IntervalMinutes <= 0) def.IntervalMinutes = 15;
                if (def.Value < 1f) def.Value = 1f;
                if (def.Value > MaxProvisionPerGrant) def.Value = MaxProvisionPerGrant;
            }
            if (migrated) Config.WriteObject(config, true);
        }

        private const float MaxProvisionPerGrant = 500f;

        private static bool IsBrokenDecree(DecreeDef def)
        {
            return def == null || string.IsNullOrEmpty(def.Id);
        }

        #endregion

        #region Data

        private class ActiveDecree
        {
            public string Id;
            public DateTime ExpiresAt;
            public bool HasPreviousTax;
            public float PreviousTax;
            public DateTime? NextGrantAt;      // crown_provision only
            public int UnitsGranted;           // crown_provision: measured units actually placed in inventories
        }

        private class Claim
        {
            public string House;
            public string DeclaredBy;
            public string CrownHouseAtDeclaration;
            public DateTime DeclaredAt;
            public DateTime WindowStart;
            public DateTime WindowEnd;
            public string Status;              // pending | active | ended
            public string Outcome;
        }

        private class Captivity
        {
            public ulong CaptiveId;
            public string CaptiveName;
            public ulong CaptorId;
            public string CaptorName;
            public DateTime Since;
            public DateTime ExpiresAt;
            public int Amount;
            public bool Paid;
            public bool Expired;               // term over; "released" already chronicled
            public int Changes;                // times the amount was set (bounded by MaxRansomChanges)
            public int ReleaseAttempts;        // automatic release attempts after expiry
            public bool AdminsAlerted;
        }

        private class StoredData
        {
            public ulong KingId;
            public string KingName;
            public string KingHouse;
            public DateTime? Since;
            public float Authority;
            public DateTime? LastFreshAuthorityAt;
            public DateTime LastAuthorityTick = DateTime.UtcNow;
            public DateTime? LastDecreeAt;
            public Dictionary<string, DateTime> DecreeLastIssued = new Dictionary<string, DateTime>();
            public List<ActiveDecree> ActiveDecrees = new List<ActiveDecree>();
            public Dictionary<string, ulong> Council = new Dictionary<string, ulong>();
            public Dictionary<string, string> CouncilNames = new Dictionary<string, string>();
            public List<Claim> Claims = new List<Claim>();
            public Dictionary<string, DateTime> HouseLastClaim = new Dictionary<string, DateTime>();
            public Dictionary<string, Captivity> Captives = new Dictionary<string, Captivity>();
            public Dictionary<string, DateTime> CaptureImmunityUntil = new Dictionary<string, DateTime>();
            public List<DateTime> CouncilChanges = new List<DateTime>();    // rolling 24 h, for CouncilChangesPerDay
        }

        private void SaveData()
        {
            if (data == null) return;                       // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Herald", "[C8A050]Herald[FFFFFF]: " },
                { "NoPermission", "You may not do that." },
                { "NotKing", "Only the reigning monarch may do that." },
                { "NoKing", "The throne is vacant." },
                { "PlayerNotFound", "No such person is online." },
                { "CrownStatus", "Crown: {0} of {1}, reigning since {2} UTC. Authority {3}/{4}." },
                { "DecreeList", "Decrees (/decree <id>):" },
                { "DecreeLine", "  {0} - {1}: costs {2} authority, cooldown {3} min{4}" },
                { "DecreeUnknown", "There is no such decree." },
                { "DecreeCooldown", "That decree may be issued again in {0} min." },
                { "DecreeGlobalCooldown", "The crown must wait {0} min before another decree." },
                { "DecreeAuthority", "Not enough authority ({0} needed, {1} held)." },
                { "DecreeActiveAlready", "That decree is already in force." },
                { "DecreeNotProclamation", "Your seat may only issue proclamations." },
                { "DecreeIssued", "Decree issued: {0}." },
                { "CouncilHeader", "The King's Council:" },
                { "CouncilLine", "  {0}: {1}" },
                { "CouncilVacant", "vacant" },
                { "CouncilUsage", "Usage: /council appoint <player> <seat> | /council remove <player|seat>" },
                { "SeatUnknown", "Unknown seat. Seats: {0}" },
                { "CouncilCooldown", "The council may be changed again in {0} s." },
                { "RansomTooManyChanges", "The ransom for {0} may not be changed again." },
                { "RansomReleasePending", "{0} is still bound. The realm will free them now." },
                { "Appointed", "{0} is appointed {1}." },
                { "Removed", "{0} is removed from the seat of {1}." },
                { "ClaimUsage", "Usage: /claim declare | /claim list" },
                { "ClaimNoHouse", "You must belong to a house to press a claim." },
                { "ClaimNotLeader", "Only the head of your house may declare its claim." },
                { "ClaimIsCrown", "Your house already holds the crown." },
                { "ClaimOpen", "Your house already has an open claim." },
                { "ClaimCooldown", "Your house may press a new claim in {0} h." },
                { "ClaimTooFewMembers", "A claim to the crown needs a house of at least {0} sworn members (yours has {1})." },
                { "ClaimTooMany", "The realm already has {0} claims pending or under way. Wait for one to end." },
                { "CouncilDaily", "The council has been changed {0} times today; the realm will not stand more churn until tomorrow." },
                { "ClaimNoWindow", "No rebellion window is configured." },
                { "ClaimLine", "  House {0}: {1}, window {2} to {3} UTC" },
                { "ClaimNone", "No claims are open." },
                { "CaptureGated", "The throne may only be contested during a declared rebellion window." },
                { "CaptureNotClaimant", "Only houses with a declared claim may contest the throne now." },
                { "CaptureImmune", "That person is under the realm's protection and cannot be taken again yet." },
                { "RansomUsage", "Usage: /ransom set <player> <amount> | /ransom paid <player> | /ransom release <player> | /ransom free | /ransom list" },
                { "RansomNotCaptor", "You are not holding that person." },
                { "RansomBadAmount", "Ransom must be a whole number from 1 to {0}." },
                { "RansomOnlyLower", "A ransom may only be lowered once set." },
                { "RansomSet", "Ransom for {0} set at {1} {2}. They go free by law in {3} min." },
                { "RansomYouAreHeld", "{0} demands {1} {2} for your release. You go free by law in {3} min." },
                { "RansomPaid", "Ransom recorded. Release {0} now; the law frees them in {1} min regardless." },
                { "RansomFreeNotYet", "Your term is not over. You go free by law in {0} min." },
                { "RansomNotHeld", "You are not held captive." },
                { "RansomExpiredCaptive", "Your term of captivity has ended. Your captor must release you; if they do not, type /ransom free." },
                { "RansomExpiredCaptor", "The term for {0} has ended. Release them now." },
                { "RansomSelfFreed", "You claim your freedom." },
                { "RansomSelfFreeFailed", "The realm could not free you automatically. An admin has been alerted." },
                { "RansomList", "  {0} held by {1}: {2} {3}{4}, free in {5} min" },
                { "RansomNone", "No one is held for ransom." },
                { "TaxCapped", "The realm's law caps the crown's tax at {0}." },
                { "DecreeUnavailable", "That decree cannot be issued: the item '{0}' is not known to this server." },
                { "ProvisionReceived", "The royal stores grant you {0} {1}." },
                { "ProvisionFull", "Your packs are full; the royal stores could not give you {0}." },
                { "AdminAlert", "[Crown] {0}" }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        private void Reply(Player player, string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
            player.SendMessage(text);                                   // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
            player.SendError(text);
        }

        private void Broadcast(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            FillMissingConfig();
            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                // Refuse to run on a damaged file rather than overwrite crown, claims and captives.
                data = null;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message + ". Fix or remove the file, then reload.");
                throw;
            }
            if (data == null && existed)
            {
                // A file truncated to nothing (power lost mid-write) parses to null: refuse rather than reset the crown.
                PrintError("oxide/data/" + DataName + ".json exists but holds no data. Nothing was written. Restore it or delete it, then reload.");
                throw new InvalidOperationException("CrownAndConsequences data file is empty");
            }
            if (data == null) data = new StoredData();
            if (data.CouncilChanges == null) data.CouncilChanges = new List<DateTime>();
            if (data.DecreeLastIssued == null) data.DecreeLastIssued = new Dictionary<string, DateTime>();
            if (data.ActiveDecrees == null) data.ActiveDecrees = new List<ActiveDecree>();
            if (data.Council == null) data.Council = new Dictionary<string, ulong>();
            if (data.CouncilNames == null) data.CouncilNames = new Dictionary<string, string>();
            if (data.Claims == null) data.Claims = new List<Claim>();
            if (data.HouseLastClaim == null) data.HouseLastClaim = new Dictionary<string, DateTime>();
            if (data.Captives == null) data.Captives = new Dictionary<string, Captivity>();
            if (data.CaptureImmunityUntil == null) data.CaptureImmunityUntil = new Dictionary<string, DateTime>();
            data.ActiveDecrees.RemoveAll(IsBrokenActive);
            data.Claims.RemoveAll(IsBrokenClaim);
            foreach (string k in new List<string>(data.Captives.Keys))
                if (data.Captives[k] == null) data.Captives.Remove(k);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                                   // re-sent on hot load; keep idempotent
            initialized = true;
            data.LastAuthorityTick = DateTime.UtcNow;
            SyncCrown();
            timer.Every(TickSeconds, Tick);
        }

        private static bool IsBrokenActive(ActiveDecree a)
        {
            return a == null || a.Id == null;
        }

        private static bool IsBrokenClaim(Claim c)
        {
            return c == null || c.House == null || c.Status == null;
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

        #region Crown tracking

        private KingsScheme Crown()
        {
            return SocialAPI.Get<KingsScheme>();
        }

        private void OnThroneCaptured(AncientThroneCaptureEvent evt)
        {
            if (evt == null || evt.Cancelled || evt.Player == null) return;
            if (evt.State != AncientThroneCaptureEvent.States.Completed) return;     // doc 2.6 [UNVERIFIED] states
            if (evt.Player.Id == data.KingId) return;
            Crowned(evt.Player.Id, evt.Player.Name);
        }

        private void OnThroneReleased(AncientThroneReleaseEvent evt)
        {
            if (evt == null || evt.Sender == null) return;
            if (data.KingId == 0 || evt.Sender.Id != data.KingId) return;
            Abdicated(evt.IsDeath ? "falls, and the throne stands empty." : "leaves the throne.");
        }

        // Catches successions the hooks missed (plugin reload, hook firing order).
        private void SyncCrown()
        {
            KingsScheme crown = Crown();
            if (crown == null) return;
            if (crown.HasKing())
            {
                ulong id = crown.GetKingID();
                if (id != data.KingId) Crowned(id, crown.GetKingName());
            }
            else if (data.KingId != 0)
            {
                Abdicated("is no longer seated; the throne stands empty.");
            }
        }

        private void Crowned(ulong kingId, string kingName)
        {
            string previous = data.KingName;
            EndAllDecrees();
            data.KingId = kingId;
            data.KingName = kingName;
            data.KingHouse = HouseOf(kingId);
            data.Since = DateTime.UtcNow;
            // Authority belongs to the crown, not the wearer. A fresh grant is given at most once per
            // FreshAuthorityAfterHours, so passing the throne back and forth (vacant-throne captures are
            // allowed) cannot refill authority. The global decree cooldown also survives succession.
            DateTime now = DateTime.UtcNow;
            bool freshAllowed = !data.LastFreshAuthorityAt.HasValue
                || (now - data.LastFreshAuthorityAt.Value).TotalHours >= config.FreshAuthorityAfterHours;
            if (freshAllowed)
            {
                data.Authority = Math.Max(data.Authority, config.StartingAuthority);
                data.LastFreshAuthorityAt = now;
            }
            data.Authority = Math.Min(Math.Max(0f, data.Authority), config.MaxAuthority);
            data.LastAuthorityTick = now;
            if (config.ClearCouncilOnSuccession)
            {
                data.Council.Clear();
                data.CouncilNames.Clear();
            }

            string house = data.KingHouse ?? "no house";
            string title = kingName + " of " + house + " takes the throne";
            string detail = previous != null && previous != kingName
                ? "The crown passes from " + previous + " to " + kingName + "."
                : kingName + " is crowned.";
            Chronicle("coronation", title, detail, previous != null ? new[] { kingName, previous } : new[] { kingName });
            NotifyChronicleCrown();
            Broadcast(title + ".");
            SaveData();
        }

        private void Abdicated(string how)
        {
            string name = data.KingName ?? "The monarch";
            EndAllDecrees();
            data.KingId = 0;
            data.KingName = null;
            data.KingHouse = null;
            data.Since = null;
            Chronicle("abdication", name + " no longer reigns", name + " " + how, new[] { name });
            NotifyChronicleCrown();
            SaveData();
        }

        private bool IsKing(Player player)
        {
            KingsScheme crown = Crown();
            return crown != null && crown.IsKing(player);
        }

        #endregion

        #region Tick

        private void Tick()
        {
            DateTime now = DateTime.UtcNow;

            // Captives first and isolated: a fault elsewhere must never stop the release clock.
            try { TickCaptives(now); }
            catch (Exception ex) { PrintError("Captive tick failed: " + ex.Message); }

            SyncCrown();

            if (data.KingId != 0)
            {
                double minutes = (now - data.LastAuthorityTick).TotalMinutes;
                if (minutes > 0)
                    data.Authority = Math.Min(config.MaxAuthority, data.Authority + (float)(minutes * config.AuthorityPerMinute));
            }
            data.LastAuthorityTick = now;

            for (int i = data.ActiveDecrees.Count - 1; i >= 0; i--)
                if (data.ActiveDecrees[i].ExpiresAt <= now) EndDecree(data.ActiveDecrees[i], true);

            EnforceTaxCap();
            try { TickProvisions(now); }
            catch (Exception ex) { PrintError("Royal Stores tick failed: " + ex.Message); }
            TickClaims(now);
        }

        // Keeps the realm's tax at or under the cap whatever set it (game UI, load, another plugin).
        private void EnforceTaxCap()
        {
            KingsScheme crown = Crown();
            if (crown == null) return;
            float cap = TaxCap();
            if (crown.GetTax() > cap) crown.SetTax(cap);
        }

        #endregion

        #region Decrees

        [ChatCommand("decree")]
        private void CmdDecree(Player player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                Reply(player, "DecreeList");
                foreach (DecreeDef d in config.Decrees)
                {
                    ActiveDecree active = FindActive(d.Id);
                    string state = active != null ? " (in force, " + MinutesUntil(active.ExpiresAt) + " min left)" : "";
                    Reply(player, "DecreeLine", d.Id, d.Name, d.AuthorityCost, d.CooldownMinutes, state);
                }
                return;
            }

            DecreeDef def = FindDecree(args[0]);
            if (def == null) { ReplyError(player, "DecreeUnknown"); return; }

            bool king = IsKing(player);
            if (!king)
            {
                string seat = SeatOf(player.Id);
                if (seat == null || !ContainsIgnoreCase(config.SeatsThatMayProclaim, seat))
                {
                    ReplyError(player, "NotKing");
                    return;
                }
                if (def.Effect != EffectProclamation) { ReplyError(player, "DecreeNotProclamation"); return; }
            }
            if (data.KingId == 0) { ReplyError(player, "NoKing"); return; }

            DateTime now = DateTime.UtcNow;
            if (FindActive(def.Id) != null) { ReplyError(player, "DecreeActiveAlready"); return; }
            if (data.LastDecreeAt.HasValue)
            {
                DateTime ready = data.LastDecreeAt.Value.AddMinutes(config.GlobalDecreeCooldownMinutes);
                if (ready > now) { ReplyError(player, "DecreeGlobalCooldown", MinutesUntil(ready)); return; }
            }
            DateTime last;
            if (data.DecreeLastIssued.TryGetValue(def.Id, out last))
            {
                DateTime ready = last.AddMinutes(def.CooldownMinutes);
                if (ready > now) { ReplyError(player, "DecreeCooldown", MinutesUntil(ready)); return; }
            }
            if (data.Authority < def.AuthorityCost)
            {
                ReplyError(player, "DecreeAuthority", def.AuthorityCost, Math.Floor(data.Authority));
                return;
            }

            var issued = new ActiveDecree { Id = def.Id, ExpiresAt = now.AddMinutes(Math.Max(1, def.DurationMinutes)) };
            if (def.Effect == EffectTax && !ApplyTaxDecree(def, issued)) return;
            if (def.Effect == EffectProvision)
            {
                if (ProvisionBlueprint(def) == null) { ReplyError(player, "DecreeUnavailable", def.Item ?? "?"); return; }
                issued.NextGrantAt = now;                       // first grant right after the proclamation below
            }

            data.Authority -= def.AuthorityCost;
            data.LastDecreeAt = now;
            data.DecreeLastIssued[def.Id] = now;
            data.ActiveDecrees.Add(issued);

            string text = (def.Proclamation ?? def.Name).Replace("{king}", data.KingName ?? "The crown");
            string by = king ? player.Name : player.Name + ", " + SeatOf(player.Id) + ", in the name of " + data.KingName;
            Chronicle("decree", def.Name, text, king ? new[] { player.Name } : new[] { player.Name, data.KingName });
            Broadcast(def.Name + ": " + text);
            Reply(player, "DecreeIssued", def.Name);
            if (!king) Puts("Proclamation by " + by);
            if (def.Effect == EffectProvision) TickProvisions(now);
            SaveData();
        }

        private bool ApplyTaxDecree(DecreeDef def, ActiveDecree active)
        {
            KingsScheme crown = Crown();
            if (crown == null) return false;
            active.HasPreviousTax = true;
            active.PreviousTax = crown.GetTax();
            crown.SetTax(Math.Min(def.Value, TaxCap()));
            return true;
        }

        private void EndDecree(ActiveDecree active, bool announce)
        {
            data.ActiveDecrees.Remove(active);
            DecreeDef def = FindDecree(active.Id);
            if (def != null && def.Effect == EffectTax && active.HasPreviousTax)
            {
                KingsScheme crown = Crown();
                if (crown != null) crown.SetTax(Math.Min(active.PreviousTax, TaxCap()));
            }
            if (announce && def != null)
            {
                string detail = "The decree of " + def.Name + " has run its course.";
                if (def.Effect == EffectProvision)
                    detail += " The crown gave " + active.UnitsGranted + " " + (def.Item ?? "goods") + " to its sworn houses.";
                Chronicle("decree", def.Name + " ends", detail, new string[0]);
            }
        }

        // Royal Stores: every IntervalMinutes while in force, each online member of a crown-sworn house receives
        // Value units. Bounded: at most ceil(Duration / Interval) grants per member per decree.
        private void TickProvisions(DateTime now)
        {
            foreach (ActiveDecree a in data.ActiveDecrees.ToArray())
            {
                DecreeDef def = FindDecree(a.Id);
                if (def == null || def.Effect != EffectProvision || !a.NextGrantAt.HasValue) continue;
                if (a.NextGrantAt.Value > now || a.ExpiresAt <= now) continue;
                a.NextGrantAt = now.AddMinutes(def.IntervalMinutes);   // set first: a fault below never re-grants
                InvItemBlueprint bp = ProvisionBlueprint(def);
                if (bp == null) continue;
                int per = (int)def.Value;
                foreach (Player p in Server.ClientPlayers)
                {
                    // Players still loading (no entity yet) are skipped rather than told their packs are full.
                    if (p == null || p.IsServer || p.Entity == null || !IsCrownSworn(HouseOf(p.Id))) continue;
                    int given = GiveItems(p, bp, per);
                    a.UnitsGranted += given;
                    if (given > 0) Reply(p, "ProvisionReceived", given, bp.Name);
                    if (given < per) ReplyError(p, "ProvisionFull", per - given);
                }
                SaveData();
            }
        }

        // ResourceType name first: the lookup ResourceTax.TaxResource itself uses [IL]; then an exact item name.
        private InvItemBlueprint ProvisionBlueprint(DecreeDef def)
        {
            if (string.IsNullOrEmpty(def.Item) || InvBlueprints.Instance == null) return null;
            foreach (ResourceType rt in Enum.GetValues(typeof(ResourceType)))
                if (string.Equals(rt.ToString(), def.Item, StringComparison.OrdinalIgnoreCase) && rt != ResourceType.Count)
                {
                    InvItemBlueprint byResource = InvBlueprints.Instance.GetBlueprintForResource(rt);
                    if (byResource != null) return byResource;
                }
            return InvBlueprints.Instance.GetBlueprintForName(def.Item, true, true);
        }

        // Server-side grant, same calls as the game's /give (ThronesCommandHandler.Give) [IL]: player inventory via
        // GetContainerOfType(entity, Inventory) (= PlayerExtensions.GetInventory [IL]), stacks capped at the
        // blueprint's ContainerManagement.StackLimit, ItemCollection.AutoMergeAdd. The amount actually added is
        // measured with ItemCollection.AutoCount, so a full inventory never counts as given.
        // UNVERIFIED (in-game): that the client inventory view refreshes at once (the game's own /give relies on it).
        private int GiveItems(Player player, InvItemBlueprint bp, int amount)
        {
            if (player == null || player.Entity == null || bp == null || amount <= 0) return 0;
            Container inv = player.GetInventory();
            ItemCollection items = inv != null ? inv.Contents : null;
            if (items == null) return 0;
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

        private void EndAllDecrees()
        {
            for (int i = data.ActiveDecrees.Count - 1; i >= 0; i--) EndDecree(data.ActiveDecrees[i], false);
        }

        private DecreeDef FindDecree(string id)
        {
            foreach (DecreeDef d in config.Decrees)
                if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        private ActiveDecree FindActive(string id)
        {
            foreach (ActiveDecree a in data.ActiveDecrees)
                if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        #endregion

        #region Tax

        // AncientThroneTaxEvent is RB 0 [OPJ L685]: the return is ignored, so clamp after the game applies it.
        // UNVERIFIED: whether the event's Tax is in the same units as KingsScheme.SetTax, and whether our own
        // SetTax call raises this event again (if so it arrives at or below the cap and is only logged).
        private void OnThroneTax(AncientThroneTaxEvent evt)
        {
            if (evt == null || evt.Cancelled) return;
            float cap = TaxCap();
            float requested = evt.Tax;
            bool byPlayer = evt.Player != null && !evt.Player.IsServer;

            // A player's change during Tax Relief means the king chose a rate; do not restore over it.
            // Our own SetTax calls (if they raise this event at all) carry no real player and are ignored here.
            if (byPlayer)
                foreach (ActiveDecree a in data.ActiveDecrees) a.HasPreviousTax = false;

            // Throttle chronicle lines so repeated tax changes cannot flood the public record.
            DateTime now = DateTime.UtcNow;
            bool mayLog = byPlayer && (now - lastTaxLogged).TotalMinutes >= Math.Max(0, config.TaxLogCooldownMinutes);

            if (requested <= cap)
            {
                if (mayLog)
                {
                    lastTaxLogged = now;
                    Chronicle("decree", "The crown sets its tax", "The tax of the realm is now " + requested.ToString("0.##") + ".",
                        new[] { evt.Player.Name });
                }
                return;
            }

            NextTick(() =>
            {
                KingsScheme crown = Crown();
                if (crown != null && crown.GetTax() > cap) crown.SetTax(cap);
            });
            if (byPlayer) Reply(evt.Player, "TaxCapped", cap.ToString("0.##"));
            if (mayLog)
            {
                lastTaxLogged = now;
                Chronicle("decree", "The crown's tax is capped", "The king sought a tax of " + requested.ToString("0.##")
                    + "; the realm's law holds it at " + cap.ToString("0.##") + ".", new[] { evt.Player.Name });
            }
        }

        private float TaxCap()
        {
            if (config.MaxTaxAbsolute >= 0f) return config.MaxTaxAbsolute;
            return GameTaxMaximum() * Mathf01(config.MaxTaxFractionOfGameMaximum);
        }

        // KingsRealm.TaxMaximum is a static float property (get_TaxMaximum in the patched Assembly-CSharp.dll
        // metadata) [ASM]. The tax is a fraction of each gathered amount: ResourceTax.TaxResource computes the
        // crown's cut as Amount * TaxCollector.Tax, and TaxCollector.Tax reads KingsScheme.GetTax() [IL].
        // UNVERIFIED: the runtime value of TaxMaximum (a static field set at load); MaxTaxAbsolute overrides it.
        private float GameTaxMaximum()
        {
            float max = KingsRealm.TaxMaximum;
            return max > 0f ? max : 1f;
        }

        private static float Mathf01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        #endregion

        #region Council

        [ChatCommand("council")]
        private void CmdCouncil(Player player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                Reply(player, "CouncilHeader");
                foreach (string seat in config.CouncilSeats)
                {
                    string name;
                    if (!data.CouncilNames.TryGetValue(seat, out name)) name = Msg("CouncilVacant", player);
                    player.SendMessage(string.Format(Msg("CouncilLine", player), seat, name));
                }
                return;
            }

            string sub = args[0].ToLowerInvariant();
            if ((sub != "appoint" && sub != "remove") || args.Length < 2) { Reply(player, "CouncilUsage"); return; }
            bool admin = IsAdmin(player);
            if (!IsKing(player) && !admin) { ReplyError(player, "NotKing"); return; }
            // Each change is chronicled, so the king may not churn the council to flood the record.
            DateTime nowCouncil = DateTime.UtcNow;
            if (!admin && (nowCouncil - lastCouncilChange).TotalSeconds < config.CouncilChangeCooldownSeconds)
            {
                ReplyError(player, "CouncilCooldown",
                    (int)Math.Ceiling(config.CouncilChangeCooldownSeconds - (nowCouncil - lastCouncilChange).TotalSeconds));
                return;
            }
            // A short per-change cooldown alone still lets a king appoint and dismiss an alt all day, one chronicle line a
            // minute, until the realm's history is pushed out of the chronicle's retention window.
            data.CouncilChanges.RemoveAll(delegate(DateTime t) { return (nowCouncil - t).TotalHours >= 24; });
            if (!admin && config.CouncilChangesPerDay > 0 && data.CouncilChanges.Count >= config.CouncilChangesPerDay)
            {
                ReplyError(player, "CouncilDaily", data.CouncilChanges.Count);
                return;
            }

            if (sub == "appoint")
            {
                if (args.Length < 3) { Reply(player, "CouncilUsage"); return; }
                Player target = FindOnline(args[1]);
                if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
                string seat = MatchSeat(JoinFrom(args, 2));
                if (seat == null) { ReplyError(player, "SeatUnknown", SeatList()); return; }

                string oldSeat = SeatOf(target.Id);
                if (oldSeat != null) { data.Council.Remove(oldSeat); data.CouncilNames.Remove(oldSeat); }
                data.Council[seat] = target.Id;
                data.CouncilNames[seat] = target.Name;
                ChronicleMinor("decree", target.Name + " named " + seat,
                    (data.KingName ?? player.Name) + " appoints " + target.Name + " as " + seat + ".",
                    new[] { target.Name, data.KingName ?? player.Name });
                Broadcast(string.Format(Msg("Appointed", null), target.Name, seat));
            }
            else
            {
                string rest = JoinFrom(args, 1);
                string seat = MatchSeat(rest);
                if (seat == null || !data.Council.ContainsKey(seat))
                {
                    seat = null;
                    foreach (KeyValuePair<string, string> kv in data.CouncilNames)
                        if (kv.Value.StartsWith(rest, StringComparison.OrdinalIgnoreCase)) { seat = kv.Key; break; }
                }
                if (seat == null) { ReplyError(player, "PlayerNotFound"); return; }
                string name = data.CouncilNames[seat];
                data.Council.Remove(seat);
                data.CouncilNames.Remove(seat);
                ChronicleMinor("decree", name + " dismissed as " + seat,
                    name + " no longer serves as " + seat + ".", new[] { name, data.KingName ?? player.Name });
                Broadcast(string.Format(Msg("Removed", null), name, seat));
            }
            lastCouncilChange = nowCouncil;
            if (!admin) data.CouncilChanges.Add(nowCouncil);
            SaveData();
        }

        private string SeatOf(ulong playerId)
        {
            foreach (KeyValuePair<string, ulong> kv in data.Council)
                if (kv.Value == playerId) return kv.Key;
            return null;
        }

        private string MatchSeat(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            foreach (string s in config.CouncilSeats)
                if (string.Equals(s, input, StringComparison.OrdinalIgnoreCase)) return s;
            foreach (string s in config.CouncilSeats)
                if (s.StartsWith(input, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        private string SeatList()
        {
            return string.Join(", ", config.CouncilSeats.ToArray());
        }

        #endregion

        #region Claims and rebellion

        [ChatCommand("claim")]
        private void CmdClaim(Player player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            if (sub == "list")
            {
                bool any = false;
                foreach (Claim c in data.Claims)
                {
                    if (c.Status == "ended") continue;
                    any = true;
                    player.SendMessage(string.Format(Msg("ClaimLine", player), c.House, c.Status,
                        c.WindowStart.ToString("ddd HH:mm"), c.WindowEnd.ToString("HH:mm")));
                }
                if (!any) Reply(player, "ClaimNone");
                return;
            }
            if (sub == "cancel" && IsAdmin(player) && args.Length > 1)
            {
                string house = JoinFrom(args, 1);
                foreach (Claim c in data.Claims)
                    if (c.Status != "ended" && string.Equals(c.House, house, StringComparison.OrdinalIgnoreCase))
                        EndClaim(c, "The claim was set aside by the realm's stewards.");
                SaveData();
                return;
            }
            if (sub != "declare") { Reply(player, "ClaimUsage"); return; }

            string myHouse = HouseOf(player.Id);
            if (myHouse == null) { ReplyError(player, "ClaimNoHouse"); return; }
            if (config.ClaimRequiresHouseLeader && !IsHouseLeader(player)) { ReplyError(player, "ClaimNotLeader"); return; }
            if (data.KingId != 0 && string.Equals(myHouse, data.KingHouse, StringComparison.OrdinalIgnoreCase))
            {
                ReplyError(player, "ClaimIsCrown");
                return;
            }
            foreach (Claim c in data.Claims)
                if (c.Status != "ended" && string.Equals(c.House, myHouse, StringComparison.OrdinalIgnoreCase))
                {
                    ReplyError(player, "ClaimOpen");
                    return;
                }
            // Sham rebellions: a one-player (alt) house declaring claims would make its "rebels" bounty targets, suspend
            // the King's Peace (RealmLaws) and hand the crown's side free renown for "defending" (RealmRenown).
            int members = HouseMemberCount(player, myHouse);
            if (config.ClaimMinMembers > 0 && members >= 0 && members < config.ClaimMinMembers)
            {
                ReplyError(player, "ClaimTooFewMembers", config.ClaimMinMembers, members);
                return;
            }
            int openClaims = 0;
            foreach (Claim c in data.Claims) if (c.Status != "ended") openClaims++;
            if (config.MaxOpenClaims > 0 && openClaims >= config.MaxOpenClaims)
            {
                ReplyError(player, "ClaimTooMany", openClaims);
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime lastClaim;
            if (data.HouseLastClaim.TryGetValue(myHouse, out lastClaim))
            {
                DateTime ready = lastClaim.AddHours(config.ClaimCooldownHours);
                if (ready > now)
                {
                    ReplyError(player, "ClaimCooldown", Math.Ceiling((ready - now).TotalHours));
                    return;
                }
            }

            DateTime start, end;
            if (!NextWindow(now.AddMinutes(config.ClaimMinNoticeMinutes), out start, out end))
            {
                ReplyError(player, "ClaimNoWindow");
                return;
            }

            var claim = new Claim
            {
                House = myHouse,
                DeclaredBy = player.Name,
                CrownHouseAtDeclaration = data.KingHouse,
                DeclaredAt = now,
                WindowStart = start,
                WindowEnd = end,
                Status = "pending"
            };
            data.Claims.Add(claim);
            data.HouseLastClaim[myHouse] = now;

            string against = data.KingName != null ? data.KingName + " of " + (data.KingHouse ?? "no house") : "the empty throne";
            string when = start.ToString("dddd HH:mm") + " UTC";
            Chronicle("claim_declared", "House " + myHouse + " claims the crown",
                player.Name + " of House " + myHouse + " declares a claim against " + against + ". The rebellion opens " + when + ".",
                data.KingName != null ? new[] { player.Name, data.KingName } : new[] { player.Name });
            Broadcast("House " + myHouse + " declares a claim to the crown. The rebellion opens " + when + ".");
            SaveData();
        }

        private void TickClaims(DateTime now)
        {
            bool changed = false;
            foreach (Claim c in data.Claims.ToArray())
            {
                if (c.Status == "pending" && c.WindowStart <= now)
                {
                    c.Status = "active";
                    changed = true;
                    Chronicle("rebellion_started", "House " + c.House + " rises",
                        "The rebellion of House " + c.House + " has begun. The throne may be contested until "
                        + c.WindowEnd.ToString("HH:mm") + " UTC.", new[] { c.DeclaredBy });
                    Broadcast("The rebellion of House " + c.House + " has begun!");
                }
                if (c.Status == "active" && c.WindowEnd <= now)
                {
                    string outcome;
                    if (data.KingHouse != null && string.Equals(data.KingHouse, c.House, StringComparison.OrdinalIgnoreCase))
                        outcome = "House " + c.House + " prevailed and holds the crown.";
                    else if (data.KingId == 0)
                        outcome = "The rebellion ended with the throne empty.";
                    else if (c.CrownHouseAtDeclaration != null
                             && string.Equals(data.KingHouse, c.CrownHouseAtDeclaration, StringComparison.OrdinalIgnoreCase))
                        outcome = "The crown held. House " + c.House + " failed.";
                    else
                        outcome = "House " + c.House + " failed; the crown now rests with " + (data.KingHouse ?? "no house") + ".";
                    EndClaim(c, outcome);
                    changed = true;
                }
            }
            // Keep history short; the chronicle holds the record.
            while (data.Claims.Count > 50 && data.Claims[0].Status == "ended") { data.Claims.RemoveAt(0); changed = true; }
            foreach (string h in new List<string>(data.HouseLastClaim.Keys))     // one entry per house ever: keep it bounded
                if ((now - data.HouseLastClaim[h]).TotalHours >= Math.Max(1, config.ClaimCooldownHours)) { data.HouseLastClaim.Remove(h); changed = true; }
            if (changed) SaveData();
        }

        private void EndClaim(Claim c, string outcome)
        {
            c.Status = "ended";
            c.Outcome = outcome;
            Chronicle("rebellion_ended", "The rebellion of House " + c.House + " ends", outcome,
                data.KingName != null ? new[] { c.DeclaredBy, data.KingName } : new[] { c.DeclaredBy });
            Broadcast(outcome);
        }

        // Earliest configured window that starts at or after `notBefore` (UTC), within the next 8 days.
        private bool NextWindow(DateTime notBefore, out DateTime start, out DateTime end)
        {
            start = DateTime.MaxValue;
            end = DateTime.MaxValue;
            TimeSpan offset = TimeSpan.FromHours(config.UtcOffsetHours);
            DateTime realmDay = (notBefore + offset).Date;
            foreach (RebellionWindow w in config.RebellionWindows)
            {
                DayOfWeek day;
                TimeSpan at;
                if (!TryParseDay(w.Day, out day) || !TryParseHourMinute(w.Start, out at)) continue;
                for (int i = 0; i <= 8; i++)
                {
                    DateTime local = realmDay.AddDays(i);
                    if (local.DayOfWeek != day) continue;
                    DateTime candidate = local + at - offset;
                    if (candidate < notBefore) continue;
                    if (candidate < start)
                    {
                        start = candidate;
                        end = candidate.AddMinutes(Math.Max(1, w.DurationMinutes));
                    }
                    break;
                }
            }
            return start != DateTime.MaxValue;
        }

        private static bool TryParseDay(string s, out DayOfWeek day)
        {
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
                if (string.Equals(d.ToString(), s, StringComparison.OrdinalIgnoreCase)) { day = d; return true; }
            day = DayOfWeek.Sunday;
            return false;
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

        private bool RebellionActive()
        {
            foreach (Claim c in data.Claims) if (c.Status == "active") return true;
            return false;
        }

        // RB 1 [OPJ L581]; blocking via evt.Cancel() follows LevelSystem.cs:2006-2020 [USE].
        // UNVERIFIED: that Cancel() plus a non-null return fully stops the capture (doc section 10 item 5).
        private object OnThroneCapture(AncientThroneCaptureEvent evt)
        {
            if (evt == null || evt.Cancelled || evt.Player == null) return null;
            if (!config.GateThroneCaptureToWindows || IsAdmin(evt.Player)) return null;
            if (data.KingId == 0 && config.AllowCaptureWhenThroneVacant) return null;
            if (evt.Player.Id == data.KingId) return null;

            string key = null;
            if (!RebellionActive()) key = "CaptureGated";
            else if (config.OnlyClaimantsMayCapture)
            {
                string house = HouseOf(evt.Player.Id);
                bool claimant = false;
                if (house != null)
                    foreach (Claim c in data.Claims)
                        if (c.Status == "active" && string.Equals(c.House, house, StringComparison.OrdinalIgnoreCase)) claimant = true;
                if (!claimant) key = "CaptureNotClaimant";
            }
            if (key == null) return null;

            evt.Cancel(Msg(key, evt.Player));
            WarnThrottled(evt.Player, key);
            return true;
        }

        #endregion

        #region Ransom

        // RB 1 [OPJ L711]. Records who holds whom; the hold is bounded by RansomMaxMinutes from the first capture.
        private object OnPlayerCapture(PlayerCaptureEvent evt)
        {
            if (evt == null || evt.Cancelled || evt.Target == null) return null;
            if (evt.Captor == null || !evt.Captor.IsPlayer || evt.Captor.Owner == null) return null;
            Player captor = evt.Captor.Owner;
            Player target = evt.Target;
            if (target.IsServer || captor.IsServer) return null;
            string key = target.Id.ToString();
            DateTime now = DateTime.UtcNow;

            DateTime immuneUntil;
            if (data.CaptureImmunityUntil.TryGetValue(key, out immuneUntil) && immuneUntil > now)
            {
                evt.Cancel(Msg("CaptureImmune", captor));
                WarnThrottled(captor, "CaptureImmune");
                return true;
            }

            Captivity existing;
            if (data.Captives.TryGetValue(key, out existing))
            {
                if (existing.Expired)
                {
                    evt.Cancel(Msg("CaptureImmune", captor));
                    WarnThrottled(captor, "CaptureImmune");
                    return true;
                }
                return null;                                            // re-binding never extends the term
            }

            data.Captives[key] = new Captivity
            {
                CaptiveId = target.Id,
                CaptiveName = target.Name,
                CaptorId = captor.Id,
                CaptorName = captor.Name,
                Since = now,
                ExpiresAt = now.AddMinutes(config.RansomMaxMinutes)
            };
            Puts("Capture: " + captor.Name + " took " + target.Name + " (" + evt.Type + ")");
            SaveData();
            return null;
        }

        // RB 1 [OPJ L737/L763]. UNVERIFIED: OnPlayerEscape (idx 6) is taken to be the attempt and
        // OnPlayerRelease (idx 20) the completed release. Neither ends the record on its own word: the
        // release is confirmed against the game's capture state a moment later (see VerifyRelease), so a
        // failed escape never leaves a captive held with no clock running.
        private object OnPlayerEscape(PlayerEscapeEvent evt)
        {
            if (evt != null && evt.Escapee != null && evt.Escapee.IsPlayer && evt.Escapee.Owner != null)
                Puts("Escape attempt by " + evt.Escapee.Owner.Name);
            return null;
        }

        private object OnPlayerRelease(PlayerEscapeEvent evt) { HandleEscape(evt); return null; }

        private void HandleEscape(PlayerEscapeEvent evt)
        {
            if (evt == null || evt.Cancelled || evt.Escapee == null || !evt.Escapee.IsPlayer) return;
            Player escapee = evt.Escapee.Owner;
            if (escapee == null) return;
            if (!data.Captives.ContainsKey(escapee.Id.ToString())) return;
            ulong id = escapee.Id;
            timer.Once(2f, delegate { VerifyRelease(id); });
        }

        private void VerifyRelease(ulong captiveId)
        {
            Captivity c;
            if (!data.Captives.TryGetValue(captiveId.ToString(), out c)) return;
            PlayerCaptureManager manager;
            if (CaptureState(OnlinePlayer(captiveId), out manager) == 1) return;    // still held: the clock keeps running
            FinishCaptivity(c, c.Paid ? "is released after the ransom was paid." : "is free.", !c.Expired);
        }

        [ChatCommand("ransom")]
        private void CmdRansom(Player player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
            DateTime now = DateTime.UtcNow;

            if (sub == "list")
            {
                if (data.Captives.Count == 0) { Reply(player, "RansomNone"); return; }
                foreach (Captivity c in data.Captives.Values)
                    player.SendMessage(string.Format(Msg("RansomList", player), c.CaptiveName, c.CaptorName,
                        c.Amount, config.RansomCurrency, c.Paid ? " (paid)" : "", MinutesUntil(c.ExpiresAt)));
                return;
            }

            if (sub == "free")
            {
                Captivity mine;
                if (!data.Captives.TryGetValue(player.Id.ToString(), out mine)) { ReplyError(player, "RansomNotHeld"); return; }
                if (mine.ExpiresAt > now) { ReplyError(player, "RansomFreeNotYet", MinutesUntil(mine.ExpiresAt)); return; }
                if (!config.AllowSelfReleaseAfterExpiry || !TryForceRelease(player))
                {
                    ReplyError(player, "RansomSelfFreeFailed");
                    AlertAdmins(player.Name + " is past their ransom term and still held by " + mine.CaptorName + ". Please free them.");
                    return;
                }
                Reply(player, "RansomSelfFreed");
                FinishCaptivity(mine, "claims their freedom under the realm's law.", !mine.Expired);
                return;
            }

            if (args.Length < 2 || (sub != "set" && sub != "paid" && sub != "release")) { Reply(player, "RansomUsage"); return; }
            Captivity held = FindCaptiveByName(args[1]);
            if (held == null || held.CaptorId != player.Id) { ReplyError(player, "RansomNotCaptor"); return; }

            if (sub == "set")
            {
                int amount;
                if (args.Length < 3 || !int.TryParse(args[2], out amount) || amount < 1 || amount > config.RansomMaxAmount)
                {
                    ReplyError(player, "RansomBadAmount", config.RansomMaxAmount);
                    return;
                }
                if (held.Amount > 0 && amount >= held.Amount) { ReplyError(player, "RansomOnlyLower"); return; }
                if (held.Paid || held.Expired) { ReplyError(player, "RansomNotCaptor"); return; }
                if (held.Changes >= config.MaxRansomChanges) { ReplyError(player, "RansomTooManyChanges", held.CaptiveName); return; }
                held.Changes++;
                held.Amount = amount;
                int left = MinutesUntil(held.ExpiresAt);
                Reply(player, "RansomSet", held.CaptiveName, amount, config.RansomCurrency, left);
                Player captive = OnlinePlayer(held.CaptiveId);                 // never message a disconnected player object
                if (captive != null) Reply(captive, "RansomYouAreHeld", held.CaptorName, amount, config.RansomCurrency, left);
                ChronicleMinor("ransom_set", held.CaptorName + " names a ransom for " + held.CaptiveName,
                    "A ransom of " + amount + " " + config.RansomCurrency + " is demanded. By law the captive goes free within "
                    + left + " minutes.", new[] { held.CaptorName, held.CaptiveName });
            }
            else if (sub == "paid")
            {
                // Payment is handed over in person; the captor's word records it. No inventory API is verified.
                if (held.Paid) return;
                held.Paid = true;
                DateTime grace = now.AddMinutes(config.RansomPaidGraceMinutes);
                if (grace < held.ExpiresAt) held.ExpiresAt = grace;
                Reply(player, "RansomPaid", held.CaptiveName, MinutesUntil(held.ExpiresAt));
                ChronicleMinor("ransom_paid", "The ransom of " + held.CaptiveName + " is paid",
                    held.CaptorName + " accepts " + (held.Amount > 0 ? held.Amount + " " + config.RansomCurrency : "payment")
                    + " for " + held.CaptiveName + ".", new[] { held.CaptorName, held.CaptiveName });
            }
            else
            {
                // The captor's word is not enough: try the game release, and if the captive is still held,
                // end the term now so the automatic release below takes over.
                Player captiveNow = OnlinePlayer(held.CaptiveId);
                TryGameRelease(captiveNow);
                PlayerCaptureManager manager;
                if (CaptureState(captiveNow, out manager) == 1)
                {
                    if (held.ExpiresAt > now) held.ExpiresAt = now;
                    Reply(player, "RansomReleasePending", held.CaptiveName);
                    SaveData();
                    return;
                }
                FinishCaptivity(held, "is set free by " + held.CaptorName + ".", !held.Expired);
                return;
            }
            SaveData();
        }

        // Every captivity ends: when the term runs out the plugin chronicles the release, then keeps trying the
        // game release each tick while the captive is online and still held, and alerts admins if that fails.
        private void TickCaptives(DateTime now)
        {
            bool changed = AdoptUntrackedCaptives(now);
            foreach (Captivity c in new List<Captivity>(data.Captives.Values))
            {
                Player captive = OnlinePlayer(c.CaptiveId);
                if (!c.Expired && c.ExpiresAt <= now)
                {
                    c.Expired = true;
                    changed = true;
                    GrantImmunity(c.CaptiveId);
                    ChronicleMinor("released", c.CaptiveName + " goes free",
                        "The term of captivity has ended; by the realm's law " + c.CaptiveName + " is free.",
                        new[] { c.CaptiveName, c.CaptorName });
                    Player captor = OnlinePlayer(c.CaptorId);
                    if (captive != null) Reply(captive, "RansomExpiredCaptive");
                    if (captor != null) Reply(captor, "RansomExpiredCaptor", c.CaptiveName);
                }
                if (!c.Expired) continue;

                PlayerCaptureManager manager;
                int state = CaptureState(captive, out manager);
                if (state == 0)
                {
                    // The game says they are free: close the record (the release was already chronicled).
                    data.Captives.Remove(c.CaptiveId.ToString());
                    changed = true;
                    continue;
                }
                if (state == 1 && c.ReleaseAttempts < MaxReleaseAttempts)
                {
                    c.ReleaseAttempts++;
                    changed = true;
                    TryGameRelease(captive);
                    continue;
                }
                if (captive != null && !c.AdminsAlerted)
                {
                    c.AdminsAlerted = true;
                    changed = true;
                    AlertAdmins(c.CaptiveName + " is past their ransom term and could not be freed automatically (captor "
                        + c.CaptorName + "). Please free them; the captive may also use /ransom free.");
                }
                // Offline captives keep their record for a day; if they return still bound, the scan above
                // adopts them again with a fresh, bounded term.
                if (captive == null && c.ExpiresAt.AddHours(24) <= now)
                {
                    data.Captives.Remove(c.CaptiveId.ToString());
                    changed = true;
                }
            }
            foreach (string k in new List<string>(data.CaptureImmunityUntil.Keys))
                if (data.CaptureImmunityUntil[k] <= now) { data.CaptureImmunityUntil.Remove(k); changed = true; }
            if (changed) SaveData();
        }

        // Captures that began while this plugin was not loaded (or whose record was dropped) still get a term.
        private bool AdoptUntrackedCaptives(DateTime now)
        {
            bool changed = false;
            List<Player> players = Server.ClientPlayers;
            if (players == null) return false;
            foreach (Player p in players)
            {
                if (p == null || p.IsServer || data.Captives.ContainsKey(p.Id.ToString())) continue;
                PlayerCaptureManager manager;
                if (CaptureState(p, out manager) != 1) continue;
                ulong captorId = 0;
                try { captorId = manager.CaptorPlayerID; }
                catch (Exception) { captorId = 0; }
                if (captorId == 0 || captorId == p.Id) continue;                  // UNVERIFIED semantics: be strict
                Player captor = Server.GetPlayerById(captorId);
                data.Captives[p.Id.ToString()] = new Captivity
                {
                    CaptiveId = p.Id,
                    CaptiveName = p.Name,
                    CaptorId = captorId,
                    CaptorName = captor != null ? captor.Name : "an unknown captor",
                    Since = now,
                    ExpiresAt = now.AddMinutes(config.RansomMaxMinutes)
                };
                Puts("Adopted untracked capture of " + p.Name);
                changed = true;
            }
            return changed;
        }

        private static Player OnlinePlayer(ulong id)
        {
            if (id == 0 || !Server.PlayerIsOnline(id)) return null;
            Player p = Server.GetPlayerById(id);
            return p != null && !p.IsServer ? p : null;
        }

        // Game capture state of an online player: 1 = held, 0 = free, -1 = unknown (offline or no manager).
        // PlayerCaptureManager is read from the player's entity as in LockPickManager.cs:152 [USE]; its public
        // field `bool Captured`, `ulong CaptorPlayerID` and method `void Release()` are present in the patched
        // Assembly-CSharp.dll metadata [ASM].
        // UNVERIFIED: that `Captured` is true on the captive's own manager, and what Release() does when
        // called by the server (expected: drop the rope/chain bind). Smoke-test C (ransom) must confirm both.
        private static int CaptureState(Player p, out PlayerCaptureManager manager)
        {
            manager = null;
            if (p == null || p.Entity == null) return -1;
            try { manager = p.Entity.TryGet<PlayerCaptureManager>(); }
            catch (Exception) { manager = null; }
            if (manager == null) return -1;
            return manager.Captured ? 1 : 0;
        }

        // UNVERIFIED: see CaptureState. Returns true if a release was attempted.
        private bool TryGameRelease(Player captive)
        {
            PlayerCaptureManager manager;
            if (CaptureState(captive, out manager) != 1) return false;
            try
            {
                manager.Release();
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning("Game release failed for " + captive.Name + ": " + ex.Message);
                return false;
            }
        }

        private void FinishCaptivity(Captivity c, string how, bool logRelease)
        {
            data.Captives.Remove(c.CaptiveId.ToString());
            GrantImmunity(c.CaptiveId);
            if (logRelease)
                ChronicleMinor("released", c.CaptiveName + " goes free", c.CaptiveName + " " + how,
                    new[] { c.CaptiveName, c.CaptorName });
            SaveData();
        }

        private void GrantImmunity(ulong playerId)
        {
            data.CaptureImmunityUntil[playerId.ToString()] = DateTime.UtcNow.AddMinutes(config.RecaptureImmunityMinutes);
        }

        // Last resort, only when the captive asks with /ransom free after the term: first the game release,
        // then PlayerExtensions.Kill(Player, CodeHatch.Damaging.DamageType) [ASM], which sends them to respawn.
        // UNVERIFIED: that death ends the bind, and what the captive loses by dying. If both fail, admins are alerted.
        private bool TryForceRelease(Player captive)
        {
            PlayerCaptureManager manager;
            TryGameRelease(captive);
            if (CaptureState(captive, out manager) == 0) return true;
            try
            {
                return captive.Kill(CodeHatch.Damaging.DamageType.Unknown);
            }
            catch (Exception ex)
            {
                PrintWarning("Forced release failed for " + captive.Name + ": " + ex.Message);
                return false;
            }
        }

        private Captivity FindCaptiveByName(string name)
        {
            foreach (Captivity c in data.Captives.Values)
                if (string.Equals(c.CaptiveName, name, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (Captivity c in data.Captives.Values)
                if (c.CaptiveName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        #endregion

        #region Status command

        [ChatCommand("crown")]
        private void CmdCrown(Player player, string command, string[] args)
        {
            if (data.KingId == 0) Reply(player, "NoKing");
            else
                player.SendMessage(string.Format(Msg("CrownStatus", player), data.KingName, data.KingHouse ?? "no house",
                    data.Since.HasValue ? data.Since.Value.ToString("yyyy-MM-dd HH:mm") : "?",
                    Math.Floor(data.Authority), config.MaxAuthority));
            foreach (ActiveDecree a in data.ActiveDecrees)
            {
                DecreeDef d = FindDecree(a.Id);
                if (d != null) player.SendMessage("  In force: " + d.Name + " (" + MinutesUntil(a.ExpiresAt) + " min)");
            }
            DateTime s, e;
            if (NextWindow(DateTime.UtcNow, out s, out e))
                player.SendMessage("  Next rebellion window: " + s.ToString("dddd HH:mm") + " UTC");
        }

        #endregion

        #region Public API (plugin.Call)

        // Open claims as "house|status|windowStartIso|windowEndIso" (status: pending or active). Used by RealmContracts.
        private string[] GetOpenClaims()
        {
            var list = new List<string>();
            if (data == null) return list.ToArray();
            foreach (Claim c in data.Claims)
                if (c.Status == "pending" || c.Status == "active")
                    list.Add(c.House + "|" + c.Status + "|" + c.WindowStart.ToString("o") + "|" + c.WindowEnd.ToString("o"));
            return list.ToArray();
        }

        // True if the house is the crown's house or sworn (via RealmHouses liege) to it.
        private bool IsSwornToCrown(string house)
        {
            return data != null && IsCrownSworn(house);
        }

        private string GetKingName()
        {
            return data != null ? data.KingName : null;
        }

        private string GetKingHouse()
        {
            return data != null ? data.KingHouse : null;
        }

        private string GetKingSince()
        {
            return data != null && data.Since.HasValue ? data.Since.Value.ToString("o") : null;
        }

        private bool IsDecreeActive(string id)
        {
            return data != null && FindActive(id) != null;
        }

        private bool IsRebellionActive()
        {
            return data != null && RebellionActive();
        }

        // Realm time offset of the rebellion windows (config "UtcOffsetHours"). RealmEvents schedules in
        // UTC and uses this to warn when Crown Night no longer lines up with the Saturday window.
        private double GetUtcOffsetHours()
        {
            return config != null ? config.UtcOffsetHours : 0;
        }

        private string GetCouncilSeat(ulong playerId)
        {
            return data != null ? SeatOf(playerId) : null;
        }

        private bool IsHeldForRansom(ulong playerId)
        {
            return data != null && data.Captives.ContainsKey(playerId.ToString());
        }

        #endregion

        #region Helpers

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
        }

        // Council changes and ransom lines: capped per hour (MinorChronicleMaxPerHour) so captures of an alt or council
        // churn cannot flood the chronicle, which keeps only its last MaxEvents entries. Over the cap: server log only.
        private readonly Queue<DateTime> minorChronicleTimes = new Queue<DateTime>();
        private void ChronicleMinor(string type, string title, string detail, string[] actors)
        {
            DateTime now = DateTime.UtcNow;
            while (minorChronicleTimes.Count > 0 && (now - minorChronicleTimes.Peek()).TotalHours >= 1) minorChronicleTimes.Dequeue();
            if (config.MinorChronicleMaxPerHour >= 0 && minorChronicleTimes.Count >= config.MinorChronicleMaxPerHour)
            {
                Puts("[" + type + "] (not chronicled: MinorChronicleMaxPerHour) " + title + " - " + detail);
                return;
            }
            minorChronicleTimes.Enqueue(now);
            Chronicle(type, title, detail, actors);
        }

        // Optional: lets RealmChronicle refresh RealmState.king/house immediately. RealmChronicle may also
        // derive this from coronation/abdication events; calling a missing method returns null harmlessly.
        private void NotifyChronicleCrown()
        {
            if (RealmChronicle == null) return;
            RealmChronicle.Call("SetCrown", data.KingName, data.KingHouse, GetKingSince());
        }

        // RealmHouses API (plugins/RealmHouses.cs): GetHouse(string playerId), GetLiege(string house),
        // GetHouseLeader(string house) -> leader id string. While RealmHouses is loaded it is authoritative:
        // a player with no house there has no house here. Falling back to the game guild name in that case
        // would let anyone rename their guild to a claimant house's name and pass the rebellion gate.
        // Only when RealmHouses is not loaded is the game guild used [ASM GuildScheme.TryGetGuildByMember].
        private string HouseOf(ulong playerId)
        {
            if (RealmHouses != null)
            {
                string fromHouses = RealmHouses.Call("GetHouse", playerId.ToString()) as string;
                return string.IsNullOrEmpty(fromHouses) ? null : fromHouses;
            }
            GuildScheme guilds = SocialAPI.Get<GuildScheme>();
            Guild g = guilds != null ? guilds.TryGetGuildByMember(playerId) : null;
            return g != null ? g.Name : null;
        }

        // Members of the house (RealmHouses when loaded, else the player's game guild); -1 if it cannot be told.
        private int HouseMemberCount(Player player, string house)
        {
            if (RealmHouses != null)
            {
                List<string> ids = RealmHouses.Call("GetMembers", house) as List<string>;
                return ids != null ? ids.Count : -1;
            }
            Guild g = player.GetGuild();
            return g != null ? g.Members().MemberCount() : -1;
        }

        private bool IsCrownSworn(string house)
        {
            if (house == null || data.KingHouse == null) return false;
            if (string.Equals(house, data.KingHouse, StringComparison.OrdinalIgnoreCase)) return true;
            if (RealmHouses == null) return false;
            string liege = RealmHouses.Call("GetLiege", house) as string;
            return liege != null && string.Equals(liege, data.KingHouse, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsHouseLeader(Player player)
        {
            if (RealmHouses != null)
            {
                string house = HouseOf(player.Id);
                string leaderId = house != null ? RealmHouses.Call("GetHouseLeader", house) as string : null;
                return leaderId != null && leaderId == player.Id.ToString();
            }
            Guild g = player.GetGuild();
            return g != null && g.OwnerId == player.Id;
        }

        private bool IsAdmin(Player player)
        {
            return permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private void AlertAdmins(string text)
        {
            PrintWarning(text);
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && IsAdmin(p)) p.SendMessage(string.Format(Msg("AdminAlert", p), text));
        }

        private void WarnThrottled(Player player, string key)
        {
            DateTime last;
            DateTime now = DateTime.UtcNow;
            if (lastWarned.TryGetValue(player.Id, out last) && (now - last).TotalSeconds < 5) return;
            lastWarned[player.Id] = now;
            ReplyError(player, key);
        }

        private Player FindOnline(string name)
        {
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

        #endregion
    }
}
