// RealmLegendary: the Ironbreaker, the one legendary blade of Ostreval.
//
// Exactly one Ironbreaker exists. It looks like the game's own largest two-handed sword (BaseItem; the plugin never
// adds or changes a game asset), and everything that makes it legendary lives here, on the server:
//
//   Bearer     one player bears it at a time (or no one: then it rests in the crown's armoury). The plugin keeps the
//              bearer in oxide/data/RealmLegendary.json and, while the server runs, the very item stack it put into the
//              bearer's packs. [CODE] InvGameItemStack has a per-session UniqueID and a Collection (the ItemCollection
//              it sits in, from any container: packs, hotbar, chest, corpse); ItemCache maps a stack to a server GUID,
//              so moves keep the same object. Neither is saved with the world (InvGameItemStack.Serialize writes only
//              Blueprint, StackAmount and components), so the stack is re-found after a restart (see Restarts).
//   Strikes    OnEntityHealthChange(EntityDamageEvent) [OPJ L162] and OnCubeTakeDamage(CubeDamageEvent) [OPJ L292].
//              The attacker is Damage.DamageSource.Owner [ASM]. The weapon is read, in order, from Damage.Damager
//              (GameObjectUtility.TryGetEntity -> BipedHoldable.Stack) and Damage.TryGetFromSource<BipedHoldable>()
//              (the right-hand holdable) [CODE HoldableMelee.UpdateCollisionDetection sets Damager = the melee
//              holdable, DamageSource = its holder; BipedHoldable.Stack is set by EntityEquipment.OnInstanceCreated].
//              UNVERIFIED that the dedicated server fills either for a client's swing; when neither resolves,
//              WhenWeaponUnknown = "melee" counts the bearer's melee-typed hits (DamageType Melee/Slash/Pierce/Bash/
//              Cut), "never" counts none. A resolved stack must be the blade's own (HeldMatch "stack"); if the server
//              turns out to copy stacks into holdables, HeldMatch "name" accepts any BaseItem in the bearer's hand.
//              DebugStrikes logs which path each bearer strike took (smoke step L3).
//              Effects: DamageMultiplier against players, BlockingMultiplier more against a player who is blocking
//              (CombatUtil.IsBlocking: a parrying sword or a raised shield [CODE]), StructureMultiplier against placed
//              objects (gates, doors: entities with PlaceableBlockAssociation [CODE]), BlockMultiplier against
//              building blocks. Knockback and stagger cannot be sent: Damage.Force is not in the Damage network
//              codec [CODE Damage.Serializer] and melee stagger is decided on the clients. Stamina cannot be drained:
//              [CODE] StaminaManager.Start disables itself on every entity but the local player ("stamina is not
//              networked"). BearerDamageTakenMultiplier is the configurable cost instead.
//   Soulbound  the blade cannot leave its bearer. Every TickSeconds the stack is looked up: moved into a chest or
//              another player's packs (a trade) it is taken back to the bearer; gone from every container (dropped,
//              destroyed) for DropGraceTicks ticks, it is forfeit: it passes to a foe who struck the bearer within
//              CombatWindowSeconds, otherwise it returns to the armoury and the bearer is barred for ForfeitBanDays.
//              Any other stack this plugin minted that is still in a container is a copy and is removed. Swords the
//              players crafted themselves are never touched.
//   Death      OnKingDeath(PlayerDeathEvent) [OPJ L1140] is woven into AncientThroneListener.OnPlayerDeath, which
//              the game subscribes Early for EVERY player death; CreateCorpseOnDeath drops the packs into the corpse
//              at Normal order [CODE]. So the blade is taken out of the packs there, before the corpse is filled, and
//              the hook always returns null (the throne's own handling continues). OnEntityDeath is the fallback (it
//              runs after the corpse is filled; the stack is then taken out of the corpse). The slayer (or the last
//              foe within CombatWindowSeconds) takes the blade if PassToSlayer and they are not of the bearer's house
//              or an allied house, not on PassPairCooldownHours with the bearer, not barred, and fewer than
//              MaxPassesPerDay passes happened today. Otherwise it returns to the armoury.
//   Leaving    OnPlayerDisconnected: the blade is taken into the plugin's keeping at once (nothing to loot from the
//              sleeping body). Back within LogoutGraceMinutes, it returns to the bearer's hand; later, it returns to
//              the armoury. Logging off within CombatWindowSeconds of a foe's blow passes it to that foe
//              (CombatLogPassesBlade).
//   Restarts   Unload takes the blade into keeping and notes how many BaseItem the bearer still holds; the next load
//              gives it back. If the world was saved with the blade still in the packs (a save before Unload), the
//              count is higher and that stack is bound instead of minting another one.
//   Winning    RealmEvents calls AwardEventPrize(kind, playerId, playerName) when a Royal Tournament champion is
//              crowned or a King's Hunt quarry is taken (PrizeEvents chooses which). It is given only while the blade
//              rests in the armoury, and never to a player barred or within PrizeCooldownDays of losing it.
//   Title      the bearer's global chat format gets ChatTitleFormat around %name% (Player.ChatFormat, the same synced
//              property RealmRenown prefixes; this plugin edits only the %name% part, so both coexist).
//              RealmRenown.AddDeed(id, name, RenownDeed, note, key) is called on each claim; it counts only if a deed
//              of that kind is configured in RealmRenown.
//   Heralds    every claim and loss is told to the realm ("Herald:" voice, docs/realm-commands.md) and written to the
//              Chronicle as title_earned (claimed) or event_ended (lost); the Chronicle feeds the overlay, the portal
//              and Discord. No new Chronicle type is needed.
//
// Zero-sum: the plugin mints one BaseItem when the blade comes into a bearer's hand and takes that same stack back when
// it leaves. Data is saved before a stack is minted and after one is taken, so a crash can lose the blade but never
// make a second one. /ironbreaker status shows the audit: Minted + Restored = Reclaimed + Unrecovered + (1 if in hand).
//
// Admin: /ironbreaker status | grant <player> [force] | revoke | reset confirm | items [word]  (realmlegendary.admin)
// API (non-public, Plugin.Call): GetBearerName() -> string or null, GetBearerId() -> string or null,
// IsBearer(string playerId) -> bool, AwardEventPrize(string kind, string playerId, string playerName) -> bool.
// Data: oxide/data/RealmLegendary.json. If it exists but cannot be parsed the plugin does nothing and never writes it.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmLegendary.md for the smoke steps.

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.Blocks.Collapsing;                   // PlaceableBlockAssociation [CODE]
using CodeHatch.Blocks.Networking.Events;            // CubeDamageEvent [ASM]
using CodeHatch.Common;                              // PlayerExtensions, GameObjectUtility [ASM]
using CodeHatch.Damaging;                            // Damage, DamageType [ASM]
using CodeHatch.Engine.Core.Cache;                   // Entity [ASM]
using CodeHatch.Engine.Modules.Inventory.Holdables;  // BipedHoldable [CODE]
using CodeHatch.Engine.Modules.SocialSystem;         // SocialAPI [ASM]
using CodeHatch.Engine.Networking;                   // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;                // InvItemBlueprint, InvBlueprints, InvGameItemStack [ASM]
using CodeHatch.ItemContainer;                       // Container, ItemCollection, CollectionTypes [ASM]
using CodeHatch.Melee;                               // CombatUtil.IsBlocking [CODE]
using CodeHatch.Networking.Events.Entities;          // EntityDamageEvent, EntityDeathEvent [ASM]
using CodeHatch.Networking.Events.Entities.Players;  // SleeperDeathEvent [ASM]
using CodeHatch.Networking.Events.Players;           // PlayerDeathEvent [ASM]
using CodeHatch.Thrones.SocialSystem;                // Guild, GuildScheme [ASM]
using Oxide.Core;                                    // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                            // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmLegendary", "Realm", "0.1.0")]
    [Description("The Ironbreaker: one legendary blade, won at the realm's events, borne by one player, taken by their slayer")]
    public class RealmLegendary : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmRenown;
        [PluginReference] private Plugin RealmHerald;

        private const string PermAdmin = "realmlegendary.admin";
        private const string DataName = "RealmLegendary";
        private const string ClaimedChronicleType = "title_earned";
        private const string LostChronicleType = "event_ended";

        private const string SKeeping = "keeping";     // in the crown's armoury: no bearer
        private const string SBorne = "borne";         // a bearer holds it (or will, as soon as they can)
        private const string SAway = "away";           // the bearer is offline; the blade waits in the plugin's keeping

        private static readonly string[] PrizeKinds = { "tournament", "kings_hunt" };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private Timer tickTimer;

        // Session state (never saved: item stacks are not stable across restarts).
        private InvGameItemStack bound;                                    // the blade's stack while it is in the world
        private readonly List<InvGameItemStack> issued = new List<InvGameItemStack>();   // every stack minted or bound
        private int missTicks;
        private string lastFoeId;
        private string lastFoeName;
        private DateTime lastFoeAt = DateTime.MinValue;
        private InvItemBlueprint baseBlueprint;
        private bool baseResolved;
        private readonly Dictionary<ulong, string> titled = new Dictionary<ulong, string>();   // player id -> format applied
        private readonly Dictionary<string, DateTime> lastNotice = new Dictionary<string, DateTime>();
        private DateTime lastStrikeLog = DateTime.MinValue;

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        #region Config

        private class PluginConfig
        {
            public string BaseItem = "";                    // exact item name; empty = first item whose name holds a BaseItemSearch word
            public List<string> BaseItemSearch;
            public List<string> PrizeEvents;                // "tournament", "kings_hunt"
            public float DamageMultiplier = 1.3f;           // the bearer's blade strikes against players
            public float BlockingMultiplier = 1.5f;         // in addition, against a player who is blocking
            public float StructureMultiplier = 2f;          // against placed objects (gates, doors)
            public float BlockMultiplier = 2f;              // against building blocks
            public float BearerDamageTakenMultiplier = 1f;  // damage the bearer takes from players (a cost; 1 = none)
            public float MaxDamagePerHit = 0f;              // cap on a boosted hit (0 = no cap)
            public string WhenWeaponUnknown = "melee";      // melee | never
            public string HeldMatch = "stack";              // stack: only the blade's own stack | name: any BaseItem the bearer holds
            public bool PassToSlayer = true;
            public bool SlayerMustBeUnallied = true;
            public int CombatWindowSeconds = 30;
            public bool CombatLogPassesBlade = true;
            public int LogoutGraceMinutes = 15;
            public int PrizeClaimHours = 24;                // an offline winner has this long to log in and take it up
            public int PassPairCooldownHours = 72;          // the same two players pass it at most once in this window
            public int MaxPassesPerDay = 6;                 // more passes in 24 h and it returns to the armoury
            public int ForfeitBanDays = 7;                  // casting it away bars the bearer this long
            public int PrizeCooldownDays = 7;               // a former bearer cannot win it as a prize this soon after losing it
            public float TickSeconds = 3f;
            public int DropGraceTicks = 2;
            public bool ChatTitleEnabled = true;
            public string ChatTitleFormat = "%name% [D6A043](Ironbreaker)[-]";
            public bool UsePopups = true;
            public string RenownDeed = "ironbreaker";       // RealmRenown deed kind added on each claim ("" = off)
            public bool DebugStrikes = false;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(DefaultConfig(), true);
        }

        private static PluginConfig DefaultConfig()
        {
            var c = new PluginConfig();
            c.BaseItemSearch = new List<string> { "Greatsword", "Great Sword", "Claymore", "Two-Handed Sword", "Bastard Sword", "Longsword", "Long Sword" };
            c.PrizeEvents = new List<string> { "tournament" };
            return c;
        }

        private void ClampConfig()
        {
            if (config == null) config = DefaultConfig();
            PluginConfig d = DefaultConfig();
            if (config.BaseItem == null) config.BaseItem = "";
            config.BaseItem = config.BaseItem.Trim();
            if (config.BaseItemSearch == null) config.BaseItemSearch = d.BaseItemSearch;
            config.BaseItemSearch.RemoveAll(delegate(string s) { return string.IsNullOrEmpty(s) || s.Trim().Length == 0; });
            if (config.PrizeEvents == null) config.PrizeEvents = d.PrizeEvents;
            var kinds = new List<string>();
            foreach (string k in config.PrizeEvents)
            {
                string n = (k ?? "").Trim().ToLowerInvariant();
                if (Array.IndexOf(PrizeKinds, n) >= 0 && !kinds.Contains(n)) kinds.Add(n);
                else if (n.Length > 0 && Array.IndexOf(PrizeKinds, n) < 0) PrintWarning("PrizeEvents: unknown event '" + k + "' (use tournament or kings_hunt); ignored.");
            }
            config.PrizeEvents = kinds;
            config.DamageMultiplier = ClampF(config.DamageMultiplier, 0.1f, 5f, 1.3f);
            config.BlockingMultiplier = ClampF(config.BlockingMultiplier, 0.1f, 5f, 1.5f);
            config.StructureMultiplier = ClampF(config.StructureMultiplier, 0.1f, 10f, 2f);
            config.BlockMultiplier = ClampF(config.BlockMultiplier, 0.1f, 10f, 2f);
            config.BearerDamageTakenMultiplier = ClampF(config.BearerDamageTakenMultiplier, 0.1f, 5f, 1f);
            if (float.IsNaN(config.MaxDamagePerHit) || config.MaxDamagePerHit < 0f) config.MaxDamagePerHit = 0f;
            string w = (config.WhenWeaponUnknown ?? "").Trim().ToLowerInvariant();
            config.WhenWeaponUnknown = w == "never" ? "never" : "melee";
            string hm = (config.HeldMatch ?? "").Trim().ToLowerInvariant();
            config.HeldMatch = hm == "name" ? "name" : "stack";
            if (config.CombatWindowSeconds < 0) config.CombatWindowSeconds = 0;
            if (config.LogoutGraceMinutes < 0) config.LogoutGraceMinutes = 0;
            if (config.PrizeClaimHours < 1) config.PrizeClaimHours = 1;
            if (config.PassPairCooldownHours < 0) config.PassPairCooldownHours = 0;
            if (config.MaxPassesPerDay < 0) config.MaxPassesPerDay = 0;
            if (config.ForfeitBanDays < 0) config.ForfeitBanDays = 0;
            if (config.PrizeCooldownDays < 0) config.PrizeCooldownDays = 0;
            if (float.IsNaN(config.TickSeconds) || config.TickSeconds < 1f) config.TickSeconds = 1f;
            if (config.TickSeconds > 30f) config.TickSeconds = 30f;
            if (config.DropGraceTicks < 1) config.DropGraceTicks = 1;
            if (string.IsNullOrEmpty(config.ChatTitleFormat) || config.ChatTitleFormat.IndexOf("%name%", StringComparison.Ordinal) < 0
                || config.ChatTitleFormat.IndexOf("%message%", StringComparison.Ordinal) >= 0)
                config.ChatTitleFormat = d.ChatTitleFormat;
            if (config.RenownDeed == null) config.RenownDeed = "";
        }

        private static float ClampF(float v, float min, float max, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return v < min ? min : v > max ? max : v;
        }

        #endregion

        #region Data

        private class PassRecord
        {
            public DateTime At;
            public string FromId;
            public string ToId;
        }

        private class StoredData
        {
            public string State = SKeeping;
            public string BearerId;
            public string BearerName;
            public string BearerSince;                  // ISO time, for /ironbreaker status and the herald
            public string Source;                       // how the bearer came by it ("tournament", "slain", ...)
            public DateTime AwayUntil;
            public bool Custody = true;                 // true = no blade stack is in the world
            public bool UnloadCustody;                  // taken into keeping by Unload: the world may hold it again
            public int OthersCount;                     // BaseItem the bearer held besides the blade when it was taken
            public string LastBearerName;
            public string LastLostReason;
            public DateTime LastLostAt;
            public int Minted;
            public int Reclaimed;
            public int Restored;
            public int Unrecovered;
            public List<PassRecord> Passes = new List<PassRecord>();
            public Dictionary<string, DateTime> Barred = new Dictionary<string, DateTime>();   // cast it away: may not bear it before
            public Dictionary<string, DateTime> PrizeCooldown = new Dictionary<string, DateTime>();   // lost it: may not win it as a prize before
            public List<string> History = new List<string>();
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;            // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
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
                { "Speaker", "Ironbreaker" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Claimed.tournament", "{0}, champion of the Royal Tournament, takes up the Ironbreaker! Every house will know the name." },
                { "Claimed.kings_hunt", "{0} has taken the King's quarry and takes up the Ironbreaker! Every house will know the name." },
                { "Claimed.grant", "By the crown's leave, {0} takes up the Ironbreaker." },
                { "Claimed.slain", "{0} has slain {1} and takes the Ironbreaker from their hand!" },
                { "Claimed.foe", "{1} fled the fight and dropped their guard; the Ironbreaker passes to {0}, who pressed them." },
                { "AwaitClaim", "{0} has won the Ironbreaker and has {1} hours to come and take it up." },
                { "NotEligible", "{0} won, but may not bear the Ironbreaker again so soon. It stays in the crown's armoury." },
                { "Lost.death", "{0} has fallen. The Ironbreaker returns to the crown's armoury until it is won again." },
                { "Lost.ally", "{0} fell to their own side. The Ironbreaker will not pass so, and returns to the crown's armoury." },
                { "Lost.cooldown", "{0} fell, but the Ironbreaker passed between the same hands too lately. It returns to the crown's armoury." },
                { "Lost.weary", "{0} fell, but the Ironbreaker has changed hands too often today. It returns to the crown's armoury." },
                { "Lost.barred", "{0} fell, but their slayer may not bear the Ironbreaker yet. It returns to the crown's armoury." },
                { "Lost.abandoned", "{0} has been gone too long. The Ironbreaker returns to the crown's armoury until it is won again." },
                { "Lost.castaway", "{0} cast the Ironbreaker away. It returns to the crown's armoury, and {0} may not bear it for {1} days." },
                { "Lost.revoked", "The crown's stewards take the Ironbreaker from {0}. It returns to the armoury." },
                { "Lost.missing", "The Ironbreaker is no longer in {0}'s hands. It returns to the crown's armoury until it is won again." },
                { "Lost.sleeping", "{0} was slain asleep with the Ironbreaker. It returns to the crown's armoury." },
                { "YouBear", "You bear the Ironbreaker. Your blows land harder, guards and gates give way before it, and the realm knows your name." },
                { "YouBear2", "It will not leave your hand. Fall and your slayer takes it; flee a fight and your foe does; stay away over {0} min and the crown does." },
                { "PopupTitle", "The Ironbreaker" },
                { "PopupButton", "I bear it" },
                { "Returned", "The Ironbreaker returns to your hand." },
                { "WontLeave", "The Ironbreaker will not leave your hand." },
                { "MakeRoom", "Your packs are full. Make room and the Ironbreaker will come to your hand." },
                { "Help", "  [F4C96D]/ironbreaker[FFFFFF] status | grant <player> [force] | revoke | reset confirm | items [word]" },
                { "NoPermission", "You may not do that." },
                { "Paused", "The Ironbreaker is paused: oxide/data/RealmLegendary.json could not be read." },
                { "StatusKeeping", "The Ironbreaker rests in the crown's armoury. Last borne by {0} ({1})." },
                { "StatusKeepingNever", "The Ironbreaker rests in the crown's armoury. No one has borne it yet." },
                { "StatusBorne", "Borne by {0} since {1} UTC ({2}). In hand: {3}." },
                { "StatusAway", "Borne by {0}, who is away; it waits in keeping until {1} UTC." },
                { "StatusItem", "  Base item: {0}. Prize of: {1}. Last foe: {2}." },
                { "StatusAudit", "  Minted {0}, restored {1}, reclaimed {2}, unrecovered {3}. In the world: {4}." },
                { "ItemUnknown", "The base item is not known to this server. Set BaseItem in oxide/config/RealmLegendary.json; [F4C96D]/ironbreaker[FFFFFF] items sword lists names." },
                { "ItemsHeader", "Items whose name holds '{0}' ({1}):" },
                { "ItemsLine", "  {0}" },
                { "ItemsNone", "No item name holds '{0}'." },
                { "PlayerNotFound", "No such person is online (or the name is ambiguous)." },
                { "AlreadyBorne", "{0} bears it now. Add force to take it from them." },
                { "Granted", "{0} now bears the Ironbreaker." },
                { "GrantFailed", "{0} bears the Ironbreaker, but their packs are full: it comes to hand when they make room." },
                { "NoBearer", "No one bears the Ironbreaker." },
                { "Revoked", "The Ironbreaker is back in the crown's armoury." },
                { "ResetAsk", "This forgets the bearer, the passes and the bars. Type [F4C96D]/ironbreaker[FFFFFF] reset confirm" },
                { "ResetDone", "The Ironbreaker's record is reset. It rests in the crown's armoury." },
                { "Yes", "yes" },
                { "No", "no" },
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

        private void ReplyError(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, Fmt(key, player, args)));
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string> { "Granted", "Revoked", "ResetDone", "Returned", "YouBear" };
        private static readonly HashSet<string> WarnKeys = new HashSet<string> { "MakeRoom", "WontLeave", "ResetAsk", "AlreadyBorne", "YouBear2", "GrantFailed" };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        private void Herald(string text)
        {
            Server.BroadcastMessage(Msg("Herald", null) + text);                     // single-string overload [ASM]
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
                    + ". RealmLegendary will not run or write anything until the file is fixed or moved away, then reload.");
                return;
            }
            if (data == null)
            {
                loadFailed = true;
                PrintError("oxide/data/" + DataName + ".json is empty or null. RealmLegendary will not run or write it until it is fixed or moved away.");
                return;
            }
            if (data.Passes == null) data.Passes = new List<PassRecord>();
            if (data.Barred == null) data.Barred = new Dictionary<string, DateTime>();
            if (data.PrizeCooldown == null) data.PrizeCooldown = new Dictionary<string, DateTime>();
            if (data.History == null) data.History = new List<string>();
            data.Passes.RemoveAll(delegate(PassRecord p) { return p == null || p.FromId == null || p.ToId == null; });
            if (data.State != SBorne && data.State != SAway) data.State = SKeeping;
            if (data.State != SKeeping && string.IsNullOrEmpty(data.BearerId)) data.State = SKeeping;
            if (data.State == SKeeping) { data.BearerId = null; data.Custody = true; }
        }

        private void OnServerInitialized()
        {
            if (loadFailed) return;
            // Re-sent on hot reload (doc 2.1), so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            if (BaseBlueprint() == null)
                PrintWarning("The Ironbreaker's base item is not known to this server (BaseItem '" + config.BaseItem
                    + "'). It cannot be given until BaseItem in oxide/config/RealmLegendary.json names a real item; /ironbreaker items <word> lists names.");
            else Puts("The Ironbreaker takes the shape of '" + baseBlueprint.Name + "'.");
            SafeTick();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            if (loadFailed || data == null) return;
            try
            {
                // Into keeping across the restart: the world must not hold a blade nobody can recognise afterwards.
                if (data.State == SBorne && !data.Custody)
                {
                    Player b = OnlineById(data.BearerId);
                    // OthersCount is only known with the bearer online; only then can a restored blade be recognised.
                    if (TakeToken(b, "unload") && b != null) data.UnloadCustody = true;
                }
                foreach (Player p in OnlinePlayers()) RemoveTitle(p);
            }
            catch (Exception ex) { PrintError("Unload: " + ex.Message); }
            SaveData();
        }

        private void OnPlayerConnected(Player player)
        {
            if (loadFailed || player == null || player.IsServer || !IsBearerId(player.Id.ToString())) return;
            DateTime now = Now();
            if (data.State == SAway)
            {
                if (now < data.AwayUntil)
                {
                    data.State = SBorne;                       // the tick puts it in hand once the body is in the world
                    SaveData();
                }
                else LoseBlade("abandoned", null);
            }
            string name = Clean(player.Name);
            if (IsBearerId(player.Id.ToString()) && name.Length > 0 && name != data.BearerName) { data.BearerName = name; SaveData(); }
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (loadFailed || player == null || player.IsServer) return;
            try
            {
                RemoveTitle(player);
                titled.Remove(player.Id);
                if (!IsBearerId(player.Id.ToString()) || data.State != SBorne) return;
                // Leaving in the middle of a fight hands the blade to the foe who pressed them.
                if (config.CombatLogPassesBlade && RecentFoe() != null)
                {
                    TakeToken(player, "fled");
                    PassOrLose(player.Id.ToString(), data.BearerName, OnlineById(lastFoeId), "foe");
                    return;
                }
                TakeToken(player, "logout");
                data.State = SAway;
                data.AwayUntil = Now().AddMinutes(config.LogoutGraceMinutes);
                SaveData();
            }
            catch (Exception ex) { PrintError("Disconnect handling failed: " + ex.Message); }
        }

        #endregion

        #region Tick

        private void SafeTick()
        {
            if (loadFailed) return;
            try { Tick(); }
            catch (Exception ex) { PrintError("Ironbreaker tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = Now();
            PruneRecords(now);
            SweepCopies();
            if (data.State == SKeeping) { UpdateTitles(null); return; }

            Player bearer = OnlineById(data.BearerId);
            if (data.State == SAway)
            {
                if (bearer != null && now < data.AwayUntil) { data.State = SBorne; SaveData(); }
                else if (now >= data.AwayUntil) { LoseBlade("abandoned", null); UpdateTitles(null); return; }
                else
                {
                    // Offline: if a stack is still out in the world (a missed disconnect), take it in now.
                    if (!data.Custody && bound != null && CollectionOf(bound) != null) { TakeToken(null, "away"); SaveData(); }
                    UpdateTitles(null);
                    return;
                }
            }

            // SBorne
            if (bearer == null)
            {
                // The bearer is gone but no disconnect was seen (a restart, a missed hook): the same grace applies.
                if (!data.Custody) TakeToken(null, "away");
                data.State = SAway;
                data.AwayUntil = now.AddMinutes(config.LogoutGraceMinutes);
                SaveData();
                UpdateTitles(null);
                return;
            }
            if (bearer.Entity == null) { UpdateTitles(bearer); return; }   // still loading in

            if (data.Custody)
            {
                if (!GiveToken(bearer) && BaseBlueprint() != null) NoticeThrottled(bearer, "MakeRoom", 60);
                UpdateTitles(bearer);
                return;
            }
            CheckSoulbound(bearer);
            UpdateTitles(data.State == SBorne ? OnlineById(data.BearerId) : null);
        }

        // The blade cannot leave its bearer: a stack in someone else's container comes back; a stack in no container
        // for DropGraceTicks ticks has been cast away.
        private void CheckSoulbound(Player bearer)
        {
            if (bound == null)
            {
                InvGameItemStack s = FindBaseStack(bearer);
                if (s != null) { Bind(s); missTicks = 0; return; }
                if (++missTicks >= config.DropGraceTicks) Forfeit(bearer, "missing");
                return;
            }
            ItemCollection where = CollectionOf(bound);
            if (where != null && IsOwnCollection(bearer, where)) { missTicks = 0; return; }
            if (where != null)
            {
                missTicks = 0;
                string holder = HolderName(where);
                if (!RemoveFrom(where, bound)) return;                       // try again next tick
                PrintWarning("The Ironbreaker was found outside " + data.BearerName + "'s packs (" + (holder ?? "a container") + "); it is taken back.");
                data.Reclaimed++;
                data.Custody = true;
                data.OthersCount = CountBase(bearer);
                SaveData();
                if (PutInHand(bearer, bound)) { data.Custody = false; data.Minted++; SaveData(); }   // the same stack, back in hand
                NoticeThrottled(bearer, "WontLeave", 10);
                return;
            }
            if (++missTicks < config.DropGraceTicks) return;
            Forfeit(bearer, "castaway");
        }

        // The stack left every container (dropped, destroyed, or lost): the blade is forfeit.
        private void Forfeit(Player bearer, string why)
        {
            missTicks = 0;
            if (!data.Custody) { data.Unrecovered++; data.Custody = true; }
            bound = null;
            SaveData();
            if (RecentFoe() != null) { PassOrLose(data.BearerId, data.BearerName, OnlineById(lastFoeId), "foe"); return; }
            if (why == "castaway" && config.ForfeitBanDays > 0) data.Barred[data.BearerId] = Now().AddDays(config.ForfeitBanDays);
            LoseBlade(why, null);
        }

        // Stacks this plugin minted that are not the blade any more, still sitting in some container, are copies.
        private void SweepCopies()
        {
            for (int i = issued.Count - 1; i >= 0; i--)
            {
                InvGameItemStack s = issued[i];
                if (s == null) { issued.RemoveAt(i); continue; }
                if (s == bound && !data.Custody) continue;
                ItemCollection where = CollectionOf(s);
                if (where == null) continue;
                string holder = HolderName(where);
                if (RemoveFrom(where, s))
                {
                    PrintWarning("A copy of the Ironbreaker was found (" + (holder ?? "a container") + ") and removed.");
                    data.Reclaimed++;
                    if (data.Unrecovered > 0) data.Unrecovered--;
                    else data.Restored++;                      // keeps the audit honest if the copy was never counted
                    SaveData();
                }
            }
            while (issued.Count > 32) issued.RemoveAt(0);
        }

        private void PruneRecords(DateTime now)
        {
            int keepHours = Math.Max(24, config.PassPairCooldownHours);
            int before = data.Passes.Count;
            data.Passes.RemoveAll(delegate(PassRecord p) { return (now - p.At).TotalHours > keepHours; });
            int expired = PruneUntil(data.Barred, now) + PruneUntil(data.PrizeCooldown, now);
            if (before != data.Passes.Count || expired > 0) SaveData();
        }

        private static int PruneUntil(Dictionary<string, DateTime> d, DateTime now)
        {
            var gone = new List<string>();
            foreach (KeyValuePair<string, DateTime> kv in d) if (kv.Value <= now) gone.Add(kv.Key);
            foreach (string id in gone) d.Remove(id);
            return gone.Count;
        }

        #endregion

        #region The blade: claim, pass, lose

        private void SetBearer(string id, string name, string source)
        {
            data.State = SBorne;
            data.BearerId = id;
            data.BearerName = Clean(name);
            data.BearerSince = Iso(Now());
            data.Source = source;
            data.Custody = true;
            data.UnloadCustody = false;
            bound = null;
            missTicks = 0;
            lastFoeId = null;
            lastFoeName = null;
            lastFoeAt = DateTime.MinValue;
        }

        // A new bearer takes the blade: state first, then the herald, the chronicle, the renown deed, the stack.
        private void Claim(string id, string name, string source, string heraldKey, string fromName)
        {
            SetBearer(id, name, source);
            Player p = OnlineById(id);
            bool prize = source == "tournament" || source == "kings_hunt";
            if (p == null)
            {
                // An offline winner has PrizeClaimHours to come and take it up; anyone else the usual grace.
                data.State = SAway;
                data.AwayUntil = prize ? Now().AddHours(config.PrizeClaimHours) : Now().AddMinutes(config.LogoutGraceMinutes);
            }
            SaveData();
            string bearer = data.BearerName;
            Herald(Fmt(heraldKey, null, bearer, fromName ?? ""));
            if (p == null && prize) Herald(Fmt("AwaitClaim", null, bearer, config.PrizeClaimHours));
            string detail = ClaimDetail(source, bearer, fromName);
            Chronicle(ClaimedChronicleType, bearer + " takes up the Ironbreaker", detail,
                fromName != null ? new[] { bearer, fromName } : new[] { bearer });
            AddHistory(bearer + " takes it (" + source + ")");
            if (RealmRenown != null && config.RenownDeed.Length > 0)
                RealmRenown.Call("AddDeed", id, bearer, config.RenownDeed, "Took up the Ironbreaker (" + source + ")",
                    "ironbreaker:" + id + ":" + Now().ToString("yyyy-MM-dd"));
            if (p != null && p.Entity != null)
            {
                if (!GiveToken(p)) NoticeThrottled(p, "MakeRoom", 0);
                TellBearer(p);
            }
        }

        private string ClaimDetail(string source, string bearer, string fromName)
        {
            string house = HouseOfId(data.BearerId);
            string who = house != null ? bearer + " of House " + house : bearer;
            switch (source)
            {
                case "tournament": return who + " won it as champion of the Royal Tournament.";
                case "kings_hunt": return who + " won it by taking the King's quarry.";
                case "slain": return who + " slew " + fromName + " and took the blade from their hand.";
                case "foe": return fromName + " fled the fight, and " + who + " took the blade.";
                default: return who + " was given the blade by the crown's stewards.";
            }
        }

        // The bearer is gone from the fight (death, flight). The blade passes to `to` if the rules allow, otherwise it
        // returns to the armoury. The stack must already be out of the world (TakeToken) before this is called.
        private void PassOrLose(string fromId, string fromName, Player to, string how)
        {
            string refusal = config.PassToSlayer ? HeirRefusal(fromId, to) : "death";
            if (refusal != null) { LoseBlade(refusal, null); return; }
            data.Passes.Add(new PassRecord { At = Now(), FromId = fromId, ToId = to.Id.ToString() });
            data.LastBearerName = fromName;
            Claim(to.Id.ToString(), to.Name, how == "foe" ? "foe" : "slain", how == "foe" ? "Claimed.foe" : "Claimed.slain", fromName);
        }

        // Null when `to` may take the blade from `fromId`; otherwise the herald key suffix of the reason it may not.
        private string HeirRefusal(string fromId, Player to)
        {
            if (to == null || to.IsServer || to.Id.ToString() == fromId) return "death";
            string toId = to.Id.ToString();
            DateTime now = Now();
            if (IsBarred(toId, now)) return "barred";
            if (config.SlayerMustBeUnallied && Allied(HouseOfId(fromId), HouseOfId(toId))) return "ally";
            int today = 0;
            foreach (PassRecord p in data.Passes)
            {
                if ((now - p.At).TotalHours < 24) today++;
                bool pair = (p.FromId == fromId && p.ToId == toId) || (p.FromId == toId && p.ToId == fromId);
                if (pair && config.PassPairCooldownHours > 0 && (now - p.At).TotalHours < config.PassPairCooldownHours) return "cooldown";
            }
            if (config.MaxPassesPerDay > 0 && today >= config.MaxPassesPerDay) return "weary";
            return null;
        }

        // Back to the crown's armoury. Takes the stack if it is still out, tells the realm, writes the Chronicle.
        private void LoseBlade(string reason, Player bearerHint)
        {
            if (data.State == SKeeping) return;
            if (!data.Custody) TakeToken(bearerHint ?? OnlineById(data.BearerId), reason);
            string name = data.BearerName ?? "?";
            string id = data.BearerId;
            Player online = OnlineById(id);
            if (online != null) RemoveTitle(online);
            data.State = SKeeping;
            data.LastBearerName = name;
            data.LastLostReason = reason;
            data.LastLostAt = Now();
            if (config.PrizeCooldownDays > 0 && id != null)
            {
                DateTime until = Now().AddDays(config.PrizeCooldownDays);
                DateTime cur;
                if (!data.PrizeCooldown.TryGetValue(id, out cur) || cur < until) data.PrizeCooldown[id] = until;
            }
            data.BearerId = null;
            data.Custody = true;
            data.UnloadCustody = false;
            bound = null;
            missTicks = 0;
            lastFoeId = null;
            SaveData();
            Herald(Fmt("Lost." + reason, null, name, config.ForfeitBanDays));
            Chronicle(LostChronicleType, "The Ironbreaker returns to the crown's armoury", LostDetail(reason, name), new[] { name });
            AddHistory(name + " loses it (" + reason + ")");
        }

        private static string LostDetail(string reason, string name)
        {
            switch (reason)
            {
                case "ally": return name + " fell to their own side, and the blade would not pass so.";
                case "cooldown": return name + " fell, but the blade had passed between the same hands too lately.";
                case "weary": return name + " fell, but the blade had changed hands too often that day.";
                case "barred": return name + " fell to one who may not yet bear it.";
                case "abandoned": return name + " was gone too long, and the blade went back to the crown.";
                case "castaway": return name + " cast the blade away.";
                case "revoked": return "The crown's stewards took the blade from " + name + ".";
                case "sleeping": return name + " was slain asleep with the blade.";
                case "missing": return "The blade was no longer in " + name + "'s hands.";
                default: return name + " fell, and no one could take the blade from their hand.";
            }
        }

        private void TellBearer(Player p)
        {
            Reply(p, "YouBear");
            Reply(p, "YouBear2", config.LogoutGraceMinutes);
            if (PopupsFor(p)) ShowInfoPopup(p, Msg("PopupTitle", p), Msg("YouBear", p) + "\n\n" + Fmt("YouBear2", p, config.LogoutGraceMinutes), Msg("PopupButton", p));
        }

        private bool IsBarred(string id, DateTime now)
        {
            return Until(data.Barred, id, now);
        }

        private static bool Until(Dictionary<string, DateTime> d, string id, DateTime now)
        {
            DateTime until;
            return id != null && d.TryGetValue(id, out until) && until > now;
        }

        private string Source()
        {
            return data.Source ?? "";
        }

        #endregion

        #region The blade's stack (mint, bind, take)

        // Mints the blade into the bearer's packs (or binds the stack the world kept). Data is saved BEFORE the stack
        // is made, so a crash in between loses the blade rather than making two.
        private bool GiveToken(Player p)
        {
            if (!data.Custody) return true;
            InvItemBlueprint bp = BaseBlueprint();
            if (bp == null || p == null || p.Entity == null) return false;
            if (data.UnloadCustody)
            {
                // Taken into keeping by Unload: if the world was saved with the blade in the packs, it is back.
                int now = CountBase(p);
                data.UnloadCustody = false;
                if (now > data.OthersCount)
                {
                    InvGameItemStack kept = FindBaseStack(p);
                    if (kept != null)
                    {
                        Bind(kept);
                        data.Custody = false;
                        data.Restored++;
                        SaveData();
                        return true;
                    }
                }
            }
            var stack = new InvGameItemStack(bp, 1, null);
            data.Custody = false;
            data.Minted++;
            SaveData();
            if (PutInHand(p, stack)) { Bind(stack); SaveData(); return true; }
            data.Custody = true;                               // nothing was made
            data.Minted--;
            SaveData();
            return false;
        }

        // Takes the blade's stack out of the world, wherever it is. Taken first, saved after: a crash in between can
        // only lose the blade. A stack that cannot be found counts as unrecovered (an ordinary sword now).
        private bool TakeToken(Player holder, string why)
        {
            if (data.Custody) return true;
            bool taken = false;
            if (bound != null)
            {
                ItemCollection where = CollectionOf(bound);
                if (where != null) taken = RemoveFrom(where, bound);
            }
            else if (holder != null)
            {
                // No stack known (after a restart, before it was found again): one BaseItem from the bearer.
                InvGameItemStack s = FindBaseStack(holder);
                ItemCollection where = s != null ? CollectionOf(s) : null;
                if (where != null) taken = RemoveFrom(where, s);
            }
            if (taken) data.Reclaimed++;
            else
            {
                data.Unrecovered++;
                PrintWarning("The Ironbreaker's stack could not be taken back (" + why + "); it is an ordinary sword now.");
            }
            data.Custody = true;
            if (holder != null && holder.Entity != null) data.OthersCount = CountBase(holder);
            bound = null;
            missTicks = 0;
            SaveData();
            return taken;
        }

        private void Bind(InvGameItemStack s)
        {
            bound = s;
            if (!issued.Contains(s)) issued.Add(s);
        }

        #endregion

        #region Hooks: strikes and deaths

        // RB 1 [OPJ L162]: always return null; this plugin only scales Damage.Amount.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (loadFailed || data == null || data.State != SBorne || evt == null || evt.Cancelled) return null;
            try { HandleDamage(evt); }
            catch (Exception ex) { PrintError("Damage handling failed: " + ex.Message); }
            return null;
        }

        private void HandleDamage(EntityDamageEvent evt)
        {
            Damage d = evt.Damage;
            if (d == null || d.Amount <= 0f || (d.DamageTypes & DamageType.Healing) != 0) return;
            Entity victim = evt.Entity;
            Player attacker = d.DamageSource != null ? d.DamageSource.Owner : null;
            if (attacker != null && attacker.IsServer) attacker = null;
            Player victimPlayer = victim != null && victim.IsPlayer ? victim.Owner : null;
            if (victimPlayer != null && victimPlayer.IsServer) victimPlayer = null;

            // Blows against the bearer: remember the foe (death and flight pass the blade to them).
            if (victimPlayer != null && IsBearerId(victimPlayer.Id.ToString()))
            {
                // A blow from the bearer's own side is not a foe's: it must not steer where the blade goes.
                bool ownSide = attacker != null && config.SlayerMustBeUnallied && Allied(HouseOfId(victimPlayer.Id.ToString()), HouseOfId(attacker.Id.ToString()));
                if (attacker != null && attacker.Id != victimPlayer.Id && !ownSide)
                {
                    lastFoeId = attacker.Id.ToString();
                    lastFoeName = attacker.Name;
                    lastFoeAt = Now();
                    if (config.BearerDamageTakenMultiplier != 1f) d.Amount = d.Amount * config.BearerDamageTakenMultiplier;
                }
                return;
            }
            if (attacker == null || !IsBearerId(attacker.Id.ToString())) return;
            if (!IsBladeStrike(attacker, d)) return;
            float m;
            if (victimPlayer != null)
            {
                m = config.DamageMultiplier;
                if (IsBlocking(victim)) m *= config.BlockingMultiplier;
            }
            else if (IsStructure(victim)) m = config.StructureMultiplier;
            else m = 1f;                                          // creatures and other things: an ordinary blade
            Boost(d, m);
        }

        // OnCubeTakeDamage [OPJ L292], RB 0: called before the game applies CubeDamageEvent.Damage to the block.
        private void OnCubeTakeDamage(CubeDamageEvent evt)
        {
            if (loadFailed || data == null || data.State != SBorne || evt == null || evt.Cancelled) return;
            try
            {
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f) return;
                Player attacker = d.DamageSource != null ? d.DamageSource.Owner : null;
                if (attacker == null || attacker.IsServer || !IsBearerId(attacker.Id.ToString())) return;
                if (!IsBladeStrike(attacker, d)) return;
                Boost(d, config.BlockMultiplier);
            }
            catch (Exception ex) { PrintError("Block damage handling failed: " + ex.Message); }
        }

        private void Boost(Damage d, float m)
        {
            if (m == 1f) return;
            float amount = d.Amount * m;
            if (config.MaxDamagePerHit > 0f && amount > config.MaxDamagePerHit) amount = Math.Max(d.Amount, config.MaxDamagePerHit);
            d.Amount = amount;
        }

        // True when this strike of the bearer is made with the blade.
        private bool IsBladeStrike(Player attacker, Damage d)
        {
            if (data.Custody) return false;                       // not in hand (keeping, full packs)
            string path;
            InvGameItemStack held = HeldStack(d, out path);
            bool result;
            if (held != null)
            {
                if (config.HeldMatch == "name") result = IsBase(held);
                else if (bound != null) result = held == bound;
                else
                {
                    // Not yet found again after a restart: the held BaseItem in the bearer's own packs is the blade.
                    result = IsBase(held) && IsOwnCollection(attacker, CollectionOf(held));
                    if (result) Bind(held);
                }
            }
            else
            {
                path = "unknown";
                const DamageType MeleeTypes = DamageType.Melee | DamageType.Slash | DamageType.Pierce | DamageType.Bash | DamageType.Cut;
                result = config.WhenWeaponUnknown == "melee" && (d.DamageTypes & MeleeTypes) != 0 && (d.DamageTypes & DamageType.Projectile) == 0;
            }
            if (config.DebugStrikes && (Now() - lastStrikeLog).TotalSeconds >= 1)
            {
                lastStrikeLog = Now();
                Puts("Strike by " + attacker.Name + ": weapon via " + path + (held != null ? " (" + held.Name + ")" : "")
                    + ", types " + d.DamageTypes + ", blade " + (result ? "yes" : "no") + ", amount " + d.Amount);
            }
            return result;
        }

        // OnKingDeath [OPJ L1140]: AncientThroneListener.OnPlayerDeath, subscribed Early, runs for every player death
        // before CreateCorpseOnDeath (Normal) fills the corpse [CODE]. RB 1: MUST return null, or the throne's own
        // king-death handling is skipped.
        private object OnKingDeath(PlayerDeathEvent evt)
        {
            if (loadFailed || data == null || evt == null || data.State != SBorne) return null;
            try
            {
                if (!IsBearerId(evt.PlayerId.ToString())) return null;
                Damage d = evt.KillingDamage;
                BearerDied(d != null && d.DamageSource != null ? d.DamageSource.Owner : null);
            }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        // Fallback for a death OnKingDeath did not see (a cancelled PlayerDeathEvent): the stack is then in the corpse.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (loadFailed || data == null || evt == null || data.State != SBorne) return null;
            try
            {
                if (evt.Entity == null || !evt.Entity.IsPlayer || evt.Entity.Owner == null) return null;
                if (!IsBearerId(evt.Entity.Owner.Id.ToString())) return null;
                Damage d = evt.KillingDamage;
                BearerDied(d != null && d.DamageSource != null ? d.DamageSource.Owner : null);
            }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        // OnKingSleeperDeath [OPJ L1166]: a logged-out body killed. Normally the blade is already in keeping (SAway);
        // if it was still on the body, it is taken out and returns to the armoury: a sleeping body is no fight.
        private object OnKingSleeperDeath(SleeperDeathEvent evt)
        {
            if (loadFailed || data == null || evt == null || data.State == SKeeping) return null;
            try
            {
                if (!IsBearerId(evt.SleeperId.ToString()) || data.Custody) return null;
                LoseBlade("sleeping", null);
            }
            catch (Exception ex) { PrintError("Sleeper death handling failed: " + ex.Message); }
            return null;
        }

        private void BearerDied(Player killer)
        {
            // Called once per death: after it the victim is no longer the bearer, so OnEntityDeath finds nothing to do.
            string fromId = data.BearerId, fromName = data.BearerName;
            Player victim = OnlineById(fromId);
            TakeToken(victim, "death");                         // out of the packs before the corpse is filled
            if (killer != null && (killer.IsServer || killer.Id.ToString() == fromId)) killer = null;
            // No killer (a fall, fire, a trap): the last foe within CombatWindowSeconds counts as the slayer.
            if (killer == null && RecentFoe() != null) killer = OnlineById(lastFoeId);
            if (killer != null && OnlineById(killer.Id.ToString()) == null) killer = null;
            if (killer == null) { LoseBlade("death", null); return; }
            PassOrLose(fromId, fromName, killer, "slain");
        }

        // The last foe who struck the bearer within CombatWindowSeconds, if still online.
        private Player RecentFoe()
        {
            if (lastFoeId == null || config.CombatWindowSeconds <= 0) return null;
            if ((Now() - lastFoeAt).TotalSeconds > config.CombatWindowSeconds) return null;
            return OnlineById(lastFoeId);
        }

        #endregion

        #region Game: containers, stacks and weapons

        private InvItemBlueprint BaseBlueprint()
        {
            if (baseResolved && baseBlueprint != null) return baseBlueprint;
            if (InvBlueprints.Instance == null) return null;
            baseResolved = true;
            try
            {
                if (config.BaseItem.Length > 0)
                    baseBlueprint = InvBlueprints.Instance.GetBlueprintForName(config.BaseItem, false, true);   // exact, any case [ASM]
                else
                {
                    List<string> names = InvBlueprints.Instance.AllBlueprintNames;
                    foreach (string word in config.BaseItemSearch)
                    {
                        if (names == null) break;
                        foreach (string n in names)
                            if (n != null && n.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                baseBlueprint = InvBlueprints.Instance.GetBlueprintForName(n, false, true);
                                if (baseBlueprint != null) break;
                            }
                        if (baseBlueprint != null) break;
                    }
                }
            }
            catch (Exception ex) { PrintWarning("Base item lookup failed: " + ex.Message); baseBlueprint = null; }
            return baseBlueprint;
        }

        private bool IsBase(InvGameItemStack s)
        {
            InvItemBlueprint bp = BaseBlueprint();
            return s != null && bp != null && s.Blueprint != null && SameName(s.Blueprint.Name, bp.Name);
        }

        // The bearer's own packs and hotbar [CODE ItemContainerExtensions.GetContainerOfType; CollectionTypes].
        private static List<ItemCollection> OwnCollections(Player p)
        {
            var list = new List<ItemCollection>();
            if (p == null || p.Entity == null) return list;
            Container hot = p.Entity.GetContainerOfType(CollectionTypes.Hotbar);
            Container inv = p.Entity.GetContainerOfType(CollectionTypes.Inventory);
            if (hot != null && hot.Contents != null) list.Add(hot.Contents);
            if (inv != null && inv.Contents != null) list.Add(inv.Contents);
            return list;
        }

        private static bool IsOwnCollection(Player p, ItemCollection c)
        {
            if (c == null) return false;
            foreach (ItemCollection own in OwnCollections(p)) if (own == c) return true;
            return false;
        }

        private int CountBase(Player p)
        {
            InvItemBlueprint bp = BaseBlueprint();
            int n = 0;
            if (bp == null) return 0;
            foreach (ItemCollection c in OwnCollections(p)) n += ItemCollection.AutoCount(c, bp);
            return n;
        }

        // A BaseItem stack in the player's hotbar or packs (the hotbar first: that is the one in hand).
        private InvGameItemStack FindBaseStack(Player p)
        {
            foreach (ItemCollection c in OwnCollections(p))
            {
                List<InvGameItemStack> items = c.GetItems();
                if (items == null) continue;
                foreach (InvGameItemStack s in items) if (IsBase(s)) return s;
            }
            return null;
        }

        private static bool PutInHand(Player p, InvGameItemStack s)
        {
            foreach (ItemCollection c in OwnCollections(p))
            {
                if (ItemCollection.AutoMergeAdd(c, s) && c.HasItem(s)) return true;   // non-stackable: AddItem keeps the object [CODE]
            }
            return false;
        }

        // InvGameItemStack.Collection: the collection the stack is in, or null when its container is gone [CODE].
        private static ItemCollection CollectionOf(InvGameItemStack s)
        {
            try { return s != null ? s.Collection : null; }
            catch (Exception) { return null; }
        }

        private static bool RemoveFrom(ItemCollection c, InvGameItemStack s)
        {
            try
            {
                c.RemoveItem(s, true);                         // broadcast so the holder's client sees it go
                return !c.HasItem(s);
            }
            catch (Exception) { return false; }
        }

        // Name of the player whose container this is, for the log.
        private static string HolderName(ItemCollection c)
        {
            try
            {
                Container box = c.Container;
                Entity e = box != null ? box.Entity : null;
                Player o = e != null && e.IsPlayer ? e.Owner : null;
                return o != null ? o.Name : null;
            }
            catch (Exception) { return null; }
        }

        // The stack in the striker's hand, from the damage itself; null when the server does not know.
        private static InvGameItemStack HeldStack(Damage d, out string path)
        {
            path = "none";
            try
            {
                if (d.Damager != null)
                {
                    Entity e = GameObjectUtility.TryGetEntity(d.Damager);
                    BipedHoldable h = e != null ? e.TryGet<BipedHoldable>() : null;
                    if (h != null && h.Stack != null) { path = "damager"; return h.Stack; }
                }
            }
            catch (Exception) { }
            try
            {
                BipedHoldable h = d.TryGetFromSource<BipedHoldable>();
                if (h != null && h.Stack != null) { path = "right-hand"; return h.Stack; }
            }
            catch (Exception) { }
            return null;
        }

        private static bool IsBlocking(Entity e)
        {
            try { return e != null && e.IsBlocking(); }
            catch (Exception) { return false; }
        }

        // Placed objects (gates, doors, other placeables tied to blocks) [CODE PlaceableBlockAssociation].
        private static bool IsStructure(Entity e)
        {
            try { return e != null && !e.IsPlayer && e.Has<PlaceableBlockAssociation>(); }
            catch (Exception) { return false; }
        }

        #endregion

        #region Chat title

        // Puts the bearer's title around %name% in their global chat format and takes it off everyone else.
        private void UpdateTitles(Player bearer)
        {
            foreach (Player p in OnlinePlayers())
            {
                if (bearer != null && p.Id == bearer.Id && config.ChatTitleEnabled && !data.Custody) ApplyTitle(p);
                else if (titled.ContainsKey(p.Id)) RemoveTitle(p);
            }
        }

        private void ApplyTitle(Player p)
        {
            string cur = p.ChatFormat ?? "";
            string fmt = config.ChatTitleFormat;
            if (cur.IndexOf(fmt, StringComparison.Ordinal) >= 0) { titled[p.Id] = fmt; return; }
            int at = cur.IndexOf("%name%", StringComparison.Ordinal);
            if (at < 0 || cur.IndexOf("%message%", StringComparison.Ordinal) < 0) return;   // never build on a format we do not understand
            p.ChatFormat = cur.Substring(0, at) + fmt + cur.Substring(at + "%name%".Length);
            titled[p.Id] = fmt;
        }

        private void RemoveTitle(Player p)
        {
            if (p == null) return;
            string fmt;
            if (!titled.TryGetValue(p.Id, out fmt)) return;
            string cur = p.ChatFormat ?? "";
            int at = cur.IndexOf(fmt, StringComparison.Ordinal);
            if (at >= 0) p.ChatFormat = cur.Substring(0, at) + "%name%" + cur.Substring(at + fmt.Length);
            titled.Remove(p.Id);
        }

        #endregion

        #region Commands

        [ChatCommand("ironbreaker")]
        private void CmdIronbreaker(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            if (loadFailed) { ReplyError(player, "Paused"); return; }
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status": ShowStatus(player); break;
                case "grant": AdminGrant(player, args); break;
                case "revoke": AdminRevoke(player); break;
                case "reset": AdminReset(player, args); break;
                case "items": AdminItems(player, args); break;
                default: Reply(player, "Help"); break;
            }
        }

        private void ShowStatus(Player player)
        {
            if (data.State == SKeeping)
            {
                if (data.LastBearerName != null) Reply(player, "StatusKeeping", data.LastBearerName, data.LastLostReason ?? "?");
                else Reply(player, "StatusKeepingNever");
            }
            else if (data.State == SAway) Reply(player, "StatusAway", data.BearerName, data.AwayUntil.ToString("MM-dd HH:mm"));
            else
            {
                string since = data.BearerSince != null && data.BearerSince.Length >= 16 ? data.BearerSince.Substring(5, 11).Replace('T', ' ') : "?";
                Reply(player, "StatusBorne", data.BearerName, since, Source(), Msg(data.Custody ? "No" : "Yes", player));
            }
            InvItemBlueprint bp = BaseBlueprint();
            string foe = RecentFoe() != null ? lastFoeName : Msg("None", player);
            Reply(player, "StatusItem", bp != null ? bp.Name : "?", config.PrizeEvents.Count > 0 ? string.Join(", ", config.PrizeEvents.ToArray()) : Msg("None", player), foe);
            Reply(player, "StatusAudit", data.Minted, data.Restored, data.Reclaimed, data.Unrecovered, data.Custody ? 0 : 1);
            if (bp == null) ReplyError(player, "ItemUnknown");
            Reply(player, "Help");
        }

        private void AdminGrant(Player player, string[] args)
        {
            bool force = args.Length > 2 && args[args.Length - 1].ToLowerInvariant() == "force";
            string name = JoinFrom(args, 1, force ? args.Length - 1 : args.Length);
            Player target = FindOnline(name);
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            if (BaseBlueprint() == null) { ReplyError(player, "ItemUnknown"); return; }
            if (data.State != SKeeping)
            {
                if (!force) { ReplyError(player, "AlreadyBorne", data.BearerName); return; }
                if (IsBearerId(target.Id.ToString())) { Reply(player, "Granted", target.Name); return; }
                LoseBlade("revoked", null);
            }
            Claim(target.Id.ToString(), target.Name, "grant", "Claimed.grant", null);
            if (data.Custody && target.Entity != null) { Reply(player, "GrantFailed", target.Name); return; }
            Reply(player, "Granted", target.Name);
        }

        private void AdminRevoke(Player player)
        {
            if (data.State == SKeeping) { ReplyError(player, "NoBearer"); return; }
            LoseBlade("revoked", null);
            Reply(player, "Revoked");
        }

        private void AdminReset(Player player, string[] args)
        {
            if (args.Length < 2 || args[1].ToLowerInvariant() != "confirm") { Reply(player, "ResetAsk"); return; }
            if (!data.Custody) TakeToken(OnlineById(data.BearerId), "reset");
            foreach (Player p in OnlinePlayers()) RemoveTitle(p);
            var fresh = new StoredData();
            fresh.Minted = data.Minted;                        // the audit survives a reset
            fresh.Reclaimed = data.Reclaimed;
            fresh.Restored = data.Restored;
            fresh.Unrecovered = data.Unrecovered;
            data = fresh;
            bound = null;
            missTicks = 0;
            lastFoeId = null;
            SaveData();
            Puts("Ironbreaker record reset by " + player.Name + ".");
            Reply(player, "ResetDone");
        }

        private void AdminItems(Player player, string[] args)
        {
            string word = args.Length > 1 ? JoinFrom(args, 1, args.Length) : "sword";
            var hits = new List<string>();
            List<string> names = InvBlueprints.Instance != null ? InvBlueprints.Instance.AllBlueprintNames : null;
            if (names != null)
                foreach (string n in names) if (n != null && n.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(n);
            if (hits.Count == 0) { Reply(player, "ItemsNone", word); return; }
            hits.Sort(StringComparer.OrdinalIgnoreCase);
            Reply(player, "ItemsHeader", word, hits.Count);
            for (int i = 0; i < hits.Count && i < 20; i++) Reply(player, "ItemsLine", hits[i]);
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // Name of the bearer, or null when the blade rests in the armoury (RealmPainter's Ironbreaker poster).
        private string GetBearerName()
        {
            return data != null && data.State != SKeeping ? data.BearerName : null;
        }

        private string GetBearerId()
        {
            return data != null && data.State != SKeeping ? data.BearerId : null;
        }

        private bool IsBearer(string playerId)
        {
            return IsBearerId(playerId);
        }

        // RealmEvents' prize hook: kind is "tournament" (the champion) or "kings_hunt" (a hunter who took a quarry for a
        // prize). True when the blade was given. Only while it rests in the armoury, and only for the kinds in PrizeEvents.
        private bool AwardEventPrize(string kind, string playerId, string playerName)
        {
            if (loadFailed || data == null || kind == null || string.IsNullOrEmpty(playerId)) return false;
            kind = kind.Trim().ToLowerInvariant();
            if (!config.PrizeEvents.Contains(kind) || data.State != SKeeping || BaseBlueprint() == null) return false;
            ulong u;
            if (!ulong.TryParse(playerId, out u)) return false;
            string name = Clean(string.IsNullOrEmpty(playerName) ? playerId : playerName);
            if (IsBarred(playerId, Now()) || Until(data.PrizeCooldown, playerId, Now()))
            {
                Herald(Fmt("NotEligible", null, name));
                return false;
            }
            Claim(playerId, name, kind, "Claimed." + kind, null);
            return true;
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

        #region Helpers

        private bool IsBearerId(string id)
        {
            return data != null && data.State != SKeeping && id != null && id == data.BearerId;
        }

        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            object r = RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
            // An older RealmChronicle without this type rejects it (0): a decree line instead. -1 = duplicate or folded.
            if (r is int && (int)r == 0 && type != "decree")
                RealmChronicle.Call("Log", "decree", title, detail, actors ?? new string[0]);
        }

        private void AddHistory(string line)
        {
            data.History.Add(Now().ToString("MM-dd HH:mm") + " " + line);
            while (data.History.Count > 20) data.History.RemoveAt(0);
            SaveData();
        }

        private string HouseOfId(string id)
        {
            ulong u;
            if (id == null || !ulong.TryParse(id, out u)) return null;
            if (RealmHouses != null)
            {
                string h = RealmHouses.Call("GetHouse", id) as string;
                return string.IsNullOrEmpty(h) ? null : h;
            }
            GuildScheme guilds = SocialAPI.Get<GuildScheme>();
            Guild g = guilds != null ? guilds.TryGetGuildByMember(u) : null;
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

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        private void NoticeThrottled(Player player, string key, int seconds)
        {
            string k = player.Id + "|" + key;
            DateTime last;
            DateTime now = Now();
            if (seconds > 0 && lastNotice.TryGetValue(k, out last) && (now - last).TotalSeconds < seconds) return;
            lastNotice[k] = now;
            if (lastNotice.Count > 200) lastNotice.Clear();
            Reply(player, key);
        }

        private static List<Player> OnlinePlayers()
        {
            var list = new List<Player>();
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) list.Add(p);
            return list;
        }

        private static Player OnlineById(string id)
        {
            ulong u;
            if (id == null || !ulong.TryParse(id, out u)) return null;
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

        private static string JoinFrom(string[] args, int start, int end)
        {
            if (args == null || start >= end) return "";
            var parts = new List<string>();
            for (int i = start; i < end && i < args.Length; i++) parts.Add(args[i]);
            return string.Join(" ", parts.ToArray()).Trim();
        }

        // Names go into chat and the Chronicle: no colour tags, no control characters, at most 32 characters.
        private static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) if (!char.IsControl(c) && c != '[' && c != ']') sb.Append(c);
            string t = sb.ToString().Trim();
            return t.Length > 32 ? t.Substring(0, 32) : t;
        }

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private DateTime Now()
        {
            return clock();
        }

        private static string Iso(DateTime t)
        {
            return t.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        #endregion
    }
}
