// RealmSentinel: Realm's own server-side cheat watch. It uses ONLY what the server already sees through Oxide hooks and
// the game's own server-side types, and works the same whether the game's anti-cheat runs or not. It never touches,
// reads, calls or works around the game's anti-cheat in any way. Tags as in docs/oxide-rok-api.md; [DEC] = read in the
// decompiled shipped patched Assembly-CSharp.dll (type.member names only, no code copied).
//
// Every detection adds points to a per-player SUSPICION SCORE that halves every Score.HalfLifeMinutes, and writes an
// evidence line (what, where, ping, numbers). Responses follow the score and are configurable: log, alert admins (chat,
// oxide/logs/RealmSentinel/, the Steward feed oxide/data/RealmSentinelFeed.json, RealmWarden's queue), freeze, kick.
// Responses.Mode "watch" (the default) only logs and alerts ("would freeze", "would kick") until the owner has run the
// smoke test. Bans happen only by an admin (/sentinel ban <player> confirm) unless the owner sets Responses.AutoBan.
//
//   Movement      timer samples of player.Entity.Position [ASM] once a second (the game's own check samples every
//                 0.75-1.25 s, [DEC] TeleportationDetection.CheckIntervalMin/Max). Limits are the game's own numbers:
//                 [DEC] TeleportationDetection.MaxVelocityWhileNotFalling = 10 m/s (planar + upward), and
//                 MaxVelocityWhileFalling = 20 m/s once falling faster than FallVelocityThreshold = 5 m/s. Sentinel
//                 adds a movement budget (bursts after lag are paid from saved budget), ping credit
//                 (Connection.AveragePing [ASM; DEC CoreCommandHandler /ping]), a stand-still lag allowance and grace
//                 after respawn, death and every server teleport: [DEC] CharacterTeleport.Teleport raises TeleportEvent
//                 through EventManager.CallEvent with Sender = the server player (NetworkEvent ctor: Sender =
//                 Player.Local). Sentinel subscribes to it ([DEC] EventManager.Subscribe<T>(EventSubscriber<T>,
//                 EventHandlerOrder)) and ignores any TeleportEvent a client sent. Players with the game permissions
//                 the game's own check exempts ([DEC] TeleportationDetection.RefreshDisabledByServerSettings: fly,
//                 videofly, teleport) are exempt from movement checks. "Fly" is sustained ascent only: the server has
//                 no ground trace a plugin can compile against, so slow hovering is NOT detected.
//   Combat        OnEntityHealthChange (EntityDamageEvent) [OPJ L162]. Limits from the game's own damage checks:
//                 [DEC] MeleeVoodooModule (7.5 m reach, 25 damage, 0.3 s per target), ProjectileVoodooModule (50),
//                 BallistaVoodooModule (125, 3 s). Who REPORTS a hit matters: [DEC] MeleeVoodooModule accepts a melee
//                 hit on a player only when evt.Sender is the VICTIM, and a projectile only when the Sender is the
//                 shooter. Sentinel scores only the client that sent the event, so a victim cannot frame an attacker.
//                 Kill rate: OnEntityDeath [OPJ L188], killer = KillingDamage.DamageSource.Owner [USE DeathMessages].
//   Items         timer samples of the Inventory (16) and Hotbar (32) containers ([DEC] CollectionTypes;
//                 PlayerExtensions.GetInventory = GetContainerOfType(entity, Inventory) [IL]), stacks enumerated
//                 ([DEC] ItemCollectionBase : IEnumerable<InvGameItemStack>, InvGameItemStack.Name/StackAmount).
//                 Gathering happens on the client and has no server hook (docs/oxide-rok-api.md 3.9), so a gain is
//                 "explained" only by: the player's own earlier losses (drop, equip), a container they opened
//                 (OnPlayerInteract [OPJ L1036]) giving out what it held when first opened or what a watcher's own
//                 loss put in (so items spawned and stashed in one sample cannot be withdrawn clean), their crafting
//                 (OnItemCrafted [OPJ L1088]), a trusted /give, or a Realm plugin that pays items. The rest is
//                 unexplained: a big one-sample jump, or a gather rate over the window.
//   Flood         OnPlayerChat [SRC] (scored only; RealmWarden mutes) and OnPlayerCommand [SRC] (excess commands are
//                 refused). Order checked in Oxide.ReignOfKings.dll: IOnServerCommand calls OnServerCommand first,
//                 then OnPlayerCommand only for a real online player, so a console command is one OnServerCommand
//                 with no OnPlayerCommand after it.
//   Connections   OnPlayerConnected / OnPlayerDisconnected [SRC]; optional refusal through CanUserLogin [SRC].
//   Names         staff-name impersonation on join: case, leet, look-alike letters and invisible characters folded,
//                 exact match or one edit away from a known staff name (Oxide permissions or groups, or listed).
//   Freeze        any freeze (automatic ones only in enforce mode; an admin's in either) cancels the player's hits,
//                 block damage and placement, interactions,
//                 crafting, throne and rope captures, and (optional) moves them back to where they were frozen with
//                 [DEC] CharacterTeleport.Teleport (the same call the game's /tp uses).
//
// Nothing here goes to the public Chronicle. Data: oxide/data/RealmSentinel.json; if it exists but cannot be read the
// plugin runs from memory and NEVER overwrites it.
// Language level: C# 3 syntax, .NET 3.5 APIs. Cross-plugin methods are non-public (Oxide calls NonPublic|Instance only).
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmSentinel.md.

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.Blocks.Networking.Events;                          // CubeDamageEvent, CubePlaceEvent [ASM]
using CodeHatch.Common;                                            // PlayerExtensions [ASM]
using CodeHatch.Damaging;                                          // Damage, DamageType [ASM]
using CodeHatch.Engine.Behaviours;                                 // CharacterTeleport [DEC]
using CodeHatch.Engine.Core.Cache;                                 // Entity [ASM]
using CodeHatch.Engine.Core.Interaction.Behaviours.Networking;     // InteractEvent [DEC]
using CodeHatch.Engine.Networking;                                 // Player, Server [ASM]
using CodeHatch.ItemContainer;                                     // Container, ItemCollection, CollectionTypes [DEC]
using CodeHatch.Networking.Events;                                 // EventManager, BaseEvent, PlayerCaptureEvent [DEC]
using CodeHatch.Networking.Events.Entities;                        // EntityDamageEvent, EntityDeathEvent, TeleportEvent [DEC]
using CodeHatch.Networking.Events.Players;                         // PlayerMessageEvent, PlayerRespawnEvent [ASM]
using CodeHatch.Thrones.AncientThrone;                             // AncientThroneCaptureEvent [ASM]
using Oxide.Core;                                                  // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                                          // Plugin [SRC]
using UnityEngine;                                                 // Vector3 [ASM]

namespace Oxide.Plugins
{
    [Info("RealmSentinel", "Realm", "0.1.0")]
    [Description("Server-side cheat watch: movement, combat, items, floods, reconnects and staff impersonation, with evidence and a decaying suspicion score")]
    public class RealmSentinel : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmWarden;

        private const string PermAdmin = "realmsentinel.admin";
        private const string DataName = "RealmSentinel";
        private const string FeedName = "RealmSentinelFeed";
        private const float HousekeepingSeconds = 5f;
        private const int PageSize = 6;
        private const int MaxPlayersStored = 20000;
        private const int FeedVersion = 1;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Detection kinds (also the keys of Score.Weights and of the evidence log).
        private const string KSpeed = "speed";
        private const string KTeleport = "teleport";
        private const string KFly = "fly";
        private const string KReach = "reach";
        private const string KDamage = "damage";
        private const string KFireRate = "fire_rate";
        private const string KKillRate = "kill_rate";
        private const string KItemJump = "item_jump";
        private const string KGather = "gather_rate";
        private const string KCraft = "craft_rate";
        private const string KChat = "chat_flood";
        private const string KCommand = "command_flood";
        private const string KReconnect = "reconnect_cycle";
        private const string KImpersonation = "impersonation";
        private const string KImpersonationNear = "impersonation_near";
        private const string KAdmin = "admin_action";
        private const string KResponse = "response";

        private static readonly string[] AllKinds = { KSpeed, KTeleport, KFly, KReach, KDamage, KFireRate, KKillRate, KItemJump,
            KGather, KCraft, KChat, KCommand, KReconnect, KImpersonation, KImpersonationNear };

        // The game permissions its own movement check exempts ([DEC] TeleportationDetection.RefreshDisabledByServerSettings).
        private static readonly string[] GameMovementPermissions = { "rok.command.admin.fly", "rok.command.admin.videofly",
            "rok.movement.fly", "rok.command.teleport", "rok.command.teleport.coord", "rok.command.teleport.origin",
            "rok.command.teleport.user", "rok.command.teleport.usertocoord", "rok.command.teleport.usertouser" };
        private const string GameGivePermission = "rok.command.items.give";

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool initialized;
        private bool subscribed;
        private bool shuttingDown;
        private bool dirty;
        private bool feedDirty;
        private double lastSave;
        private double clockSkew;                                // seconds added to the clock; only the logic tests change it
        private EventSubscriber<TeleportEvent> teleportSubscriber;

        // In-memory state (losing it on reload is harmless: sampling restarts, the score and freezes are persisted).
        private readonly Dictionary<ulong, MoveState> moves = new Dictionary<ulong, MoveState>();
        private readonly Dictionary<ulong, ItemState> items = new Dictionary<ulong, ItemState>();
        private readonly Dictionary<ulong, CombatState> fights = new Dictionary<ulong, CombatState>();
        private readonly Dictionary<ulong, FloodState> floods = new Dictionary<ulong, FloodState>();
        private readonly Dictionary<string, List<double>> connects = new Dictionary<string, List<double>>();
        private readonly Dictionary<ulong, double> movementExemptUntil = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, bool> movementExempt = new Dictionary<ulong, bool>();
        private readonly Dictionary<string, double> throttle = new Dictionary<string, double>();
        private readonly Dictionary<ulong, bool> kicking = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, double> spawnOpenUntil = new Dictionary<ulong, double>();
        private readonly List<double> adminChatTimes = new List<double>();
        private int suppressedAdminChat;
        private PendingCommand pendingConsole;

        #region Config

        private class PluginConfig
        {
            public GeneralSettings General = new GeneralSettings();
            public MovementSettings Movement = new MovementSettings();
            public CombatSettings Combat = new CombatSettings();
            public ItemSettings Items = new ItemSettings();
            public FloodSettings Flood = new FloodSettings();
            public ConnectionSettings Connections = new ConnectionSettings();
            public NameSettings Names = new NameSettings();
            public ScoreSettings Score = new ScoreSettings();
            public ResponseSettings Responses = new ResponseSettings();
            public AlertSettings Alerts = new AlertSettings();
        }

        private class GeneralSettings
        {
            public bool Enabled = true;
            public bool AdminsExempt = true;                     // realmsentinel.admin skips every check
            public List<string> ExemptIds = new List<string>();  // Steam IDs that skip every check
            public int SaveIntervalSeconds = 120;
        }

        private class MovementSettings
        {
            public bool Enabled = true;
            public float SampleSeconds = 1f;
            public float MaxSpeed = 10f;                         // m/s, planar + upward ([DEC] TeleportationDetection)
            public float MaxSpeedFalling = 20f;                  // m/s while falling ([DEC] TeleportationDetection)
            public float FallVelocity = 5f;                      // m/s downward that counts as falling
            public float SpeedTolerancePercent = 10f;            // budget refills this much faster than MaxSpeed
            public float BurstSeconds = 2f;                      // how much unused movement a player may save up
            public float PingFactor = 1f;                        // budget cap grows by MaxSpeed x 2 x ping x factor
            public int MaxPingCreditMs = 600;                    // a lag switch cannot buy more credit than this
            public float LagAllowanceSeconds = 6f;               // standing still (no updates) saves up to this much
            public float MinExcessMeters = 1f;                   // smaller overshoots are ignored
            public int ViolationsToFlag = 4;                     // overshoots within ViolationWindowSeconds
            public int ViolationWindowSeconds = 10;
            public float TeleportMeters = 25f;                   // one overshoot this large is a teleport
            public bool CheckAscent = true;
            public float AscentWindowSeconds = 10f;
            public float MaxRiseMeters = 30f;                    // net rise within the window without coming down
            public float GraceAfterRespawnSeconds = 8f;
            public float GraceAfterTeleportSeconds = 5f;
            public float MaxGapSeconds = 5f;                     // a longer gap between samples restarts sampling
            public bool ExemptGameFlyPermissions = true;
            public List<string> TeleportCommands = new List<string> { "tp", "warp", "teleport", "tpdelay" };
        }

        private class CombatSettings
        {
            public bool Enabled = true;
            public float MeleeRange = 7.5f;                      // [DEC] MeleeVoodooModule
            public float MeleeMaxDamage = 75f;                   // game raw cap 25; effects multiply after it
            public float MeleeMinIntervalPerTarget = 0.3f;       // [DEC] MeleeVoodooModule
            public float ProjectileRange = 300f;
            public float ProjectileMaxDamage = 200f;             // ballista raw cap 125 [DEC]; effects multiply after it
            public int ProjectileMaxHitsPerWindow = 20;
            public int RateWindowSeconds = 5;
            public float RangeSlackMeters = 1.5f;
            public float PingFactor = 1f;                        // reach credit = (pings) x MaxSpeed x factor
            public int KillWindowSeconds = 120;
            public int MaxKillsPerWindow = 8;
        }

        private class ItemSettings
        {
            public bool Enabled = true;
            public float SampleSeconds = 10f;
            public bool IncludeHotbar = true;
            public int JumpAmount = 500;                         // one item, one sample, unexplained
            public int GainWindowMinutes = 5;
            public int MaxGainPerWindow = 3000;                  // all items, unexplained, per window
            public int ContainerTrackSeconds = 300;              // an opened container is watched this long
            public int SourceGraceSeconds = 60;                  // after a trusted /give or a paying Realm command
            public int CreditMinutes = 30;                       // own losses can come back for this long
            public int CraftGraceSeconds = 60;
            public int MaxCraftsPerMinute = 40;
            public List<string> GiveCommands = new List<string> { "give", "givefirst", "first", "givesearch", "gs",
                "givecategory", "gc", "givecat", "giveall", "allitems" };
            // Realm commands that may hand items to the player who types them (escrow, market, prizes, vaults).
            public List<string> ItemSourceCommands = new List<string> { "contract", "market", "vault", "treasury", "event", "tourney", "hunt" };
        }

        private class FloodSettings
        {
            public bool Enabled = true;
            public int ChatWindowSeconds = 10;
            public int ChatMaxPerWindow = 12;                    // RealmWarden already mutes at 5 per 8 s; this is bot-like
            public int CommandWindowSeconds = 10;
            public int CommandMaxPerWindow = 15;
            public bool BlockExcessCommands = true;
            public int RefusedCommandsToFlag = 20;               // within a minute
        }

        private class ConnectionSettings
        {
            public bool Enabled = true;
            public int WindowMinutes = 10;
            public int MaxConnects = 5;
            public bool RefuseWhileCycling = false;              // opt-in; only in enforce mode
            public int RefuseSeconds = 120;
        }

        private class NameSettings
        {
            public bool Enabled = true;
            public List<string> StaffNames = new List<string>();
            public List<string> StaffPermissions = new List<string> { "realmsentinel.admin", "realmwarden.admin" };
            public List<string> StaffGroups = new List<string> { "admin" };
            public bool LearnStaff = true;                       // remember staff names when staff join
            public int NearMatchMinLength = 5;
            public bool KickExactMatch = false;                  // only in enforce mode
            public int KickDelaySeconds = 5;
        }

        private class ScoreSettings
        {
            public float HalfLifeMinutes = 20f;
            public int KindCooldownSeconds = 15;                 // one scored detection per kind per player in this time
            public Dictionary<string, float> Weights = DefaultWeights();
        }

        private static Dictionary<string, float> DefaultWeights()
        {
            return new Dictionary<string, float>
            {
                { KSpeed, 8f }, { KTeleport, 25f }, { KFly, 12f }, { KReach, 10f }, { KDamage, 15f }, { KFireRate, 10f },
                { KKillRate, 8f }, { KItemJump, 15f }, { KGather, 10f }, { KCraft, 8f }, { KChat, 4f }, { KCommand, 6f },
                { KReconnect, 6f }, { KImpersonation, 40f }, { KImpersonationNear, 10f }
            };
        }

        private class ResponseSettings
        {
            public string Mode = "watch";                        // "watch" (log + alert only) or "enforce"
            public bool Alert = true;
            public float AlertScore = 20f;
            public int AlertCooldownMinutes = 10;
            public bool Freeze = true;
            public float FreezeScore = 50f;
            public int FreezeMinutes = 15;
            public bool PinFrozenPosition = true;
            public bool Kick = true;
            public float KickScore = 80f;
            public int KickCooldownMinutes = 10;
            public bool AutoBan = false;                         // owner opt-in; otherwise bans need an admin
            public float BanScore = 150f;
        }

        private class AlertSettings
        {
            public bool ChatToOnlineAdmins = true;
            public int MaxChatAlertsPerMinute = 6;
            public bool ForwardToWarden = true;
            public bool LogToFile = true;
            public bool WriteFeed = true;
            public int MaxStored = 500;
            public int MaxEvidence = 3000;
            public int FeedAlerts = 100;
            public int FeedSuspects = 20;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static float ClampF(float v, float lo, float hi)
        {
            if (float.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private void ClampConfig()
        {
            if (config.General == null) config.General = new GeneralSettings();
            if (config.Movement == null) config.Movement = new MovementSettings();
            if (config.Combat == null) config.Combat = new CombatSettings();
            if (config.Items == null) config.Items = new ItemSettings();
            if (config.Flood == null) config.Flood = new FloodSettings();
            if (config.Connections == null) config.Connections = new ConnectionSettings();
            if (config.Names == null) config.Names = new NameSettings();
            if (config.Score == null) config.Score = new ScoreSettings();
            if (config.Responses == null) config.Responses = new ResponseSettings();
            if (config.Alerts == null) config.Alerts = new AlertSettings();
            if (config.General.ExemptIds == null) config.General.ExemptIds = new List<string>();
            if (config.Movement.TeleportCommands == null) config.Movement.TeleportCommands = new List<string>();
            if (config.Items.GiveCommands == null) config.Items.GiveCommands = new List<string>();
            if (config.Items.ItemSourceCommands == null) config.Items.ItemSourceCommands = new List<string>();
            if (config.Names.StaffNames == null) config.Names.StaffNames = new List<string>();
            if (config.Names.StaffPermissions == null) config.Names.StaffPermissions = new List<string>();
            if (config.Names.StaffGroups == null) config.Names.StaffGroups = new List<string>();
            if (config.Score.Weights == null) config.Score.Weights = new Dictionary<string, float>();
            Dictionary<string, float> defaults = DefaultWeights();
            foreach (KeyValuePair<string, float> kv in defaults)
            {
                float w;
                if (!config.Score.Weights.TryGetValue(kv.Key, out w)) config.Score.Weights[kv.Key] = kv.Value;
                else config.Score.Weights[kv.Key] = ClampF(w, 0f, 500f);
            }

            config.General.SaveIntervalSeconds = Clamp(config.General.SaveIntervalSeconds, 30, 3600);
            MovementSettings m = config.Movement;
            m.SampleSeconds = ClampF(m.SampleSeconds, 0.5f, 10f);
            m.MaxSpeed = ClampF(m.MaxSpeed, 3f, 100f);
            m.MaxSpeedFalling = ClampF(m.MaxSpeedFalling, m.MaxSpeed, 200f);
            m.FallVelocity = ClampF(m.FallVelocity, 1f, 50f);
            m.SpeedTolerancePercent = ClampF(m.SpeedTolerancePercent, 0f, 200f);
            m.BurstSeconds = ClampF(m.BurstSeconds, 1f, 30f);
            m.PingFactor = ClampF(m.PingFactor, 0f, 5f);
            m.MaxPingCreditMs = Clamp(m.MaxPingCreditMs, 0, 2000);
            m.LagAllowanceSeconds = ClampF(m.LagAllowanceSeconds, 0f, 30f);
            m.MinExcessMeters = ClampF(m.MinExcessMeters, 0.1f, 50f);
            m.ViolationsToFlag = Clamp(m.ViolationsToFlag, 1, 100);
            m.ViolationWindowSeconds = Clamp(m.ViolationWindowSeconds, 2, 600);
            m.TeleportMeters = ClampF(m.TeleportMeters, 5f, 5000f);
            m.AscentWindowSeconds = ClampF(m.AscentWindowSeconds, 2f, 120f);
            m.MaxRiseMeters = ClampF(m.MaxRiseMeters, 3f, 1000f);
            m.GraceAfterRespawnSeconds = ClampF(m.GraceAfterRespawnSeconds, 0f, 120f);
            m.GraceAfterTeleportSeconds = ClampF(m.GraceAfterTeleportSeconds, 0f, 120f);
            m.MaxGapSeconds = ClampF(m.MaxGapSeconds, m.SampleSeconds * 2f, 60f);
            CombatSettings c = config.Combat;
            c.MeleeRange = ClampF(c.MeleeRange, 1f, 100f);
            c.MeleeMaxDamage = ClampF(c.MeleeMaxDamage, 1f, 100000f);
            c.MeleeMinIntervalPerTarget = ClampF(c.MeleeMinIntervalPerTarget, 0.05f, 10f);
            c.ProjectileRange = ClampF(c.ProjectileRange, 5f, 5000f);
            c.ProjectileMaxDamage = ClampF(c.ProjectileMaxDamage, 1f, 100000f);
            c.ProjectileMaxHitsPerWindow = Clamp(c.ProjectileMaxHitsPerWindow, 1, 1000);
            c.RateWindowSeconds = Clamp(c.RateWindowSeconds, 1, 120);
            c.RangeSlackMeters = ClampF(c.RangeSlackMeters, 0f, 50f);
            c.PingFactor = ClampF(c.PingFactor, 0f, 5f);
            c.KillWindowSeconds = Clamp(c.KillWindowSeconds, 10, 3600);
            c.MaxKillsPerWindow = Clamp(c.MaxKillsPerWindow, 1, 1000);
            ItemSettings it = config.Items;
            it.SampleSeconds = ClampF(it.SampleSeconds, 2f, 120f);
            it.JumpAmount = Clamp(it.JumpAmount, 1, 1000000);
            it.GainWindowMinutes = Clamp(it.GainWindowMinutes, 1, 120);
            it.MaxGainPerWindow = Clamp(it.MaxGainPerWindow, 1, 10000000);
            it.ContainerTrackSeconds = Clamp(it.ContainerTrackSeconds, 10, 3600);
            it.SourceGraceSeconds = Clamp(it.SourceGraceSeconds, 0, 3600);
            it.CreditMinutes = Clamp(it.CreditMinutes, 1, 1440);
            it.CraftGraceSeconds = Clamp(it.CraftGraceSeconds, 0, 3600);
            it.MaxCraftsPerMinute = Clamp(it.MaxCraftsPerMinute, 1, 10000);
            FloodSettings f = config.Flood;
            f.ChatWindowSeconds = Clamp(f.ChatWindowSeconds, 1, 600);
            f.ChatMaxPerWindow = Clamp(f.ChatMaxPerWindow, 1, 1000);
            f.CommandWindowSeconds = Clamp(f.CommandWindowSeconds, 1, 600);
            f.CommandMaxPerWindow = Clamp(f.CommandMaxPerWindow, 1, 1000);
            f.RefusedCommandsToFlag = Clamp(f.RefusedCommandsToFlag, 1, 10000);
            ConnectionSettings cn = config.Connections;
            cn.WindowMinutes = Clamp(cn.WindowMinutes, 1, 1440);
            cn.MaxConnects = Clamp(cn.MaxConnects, 2, 1000);
            cn.RefuseSeconds = Clamp(cn.RefuseSeconds, 5, 86400);
            NameSettings n = config.Names;
            n.NearMatchMinLength = Clamp(n.NearMatchMinLength, 3, 64);
            n.KickDelaySeconds = Clamp(n.KickDelaySeconds, 1, 60);
            if (n.StaffNames.Count > 200) n.StaffNames.RemoveRange(200, n.StaffNames.Count - 200);
            ScoreSettings s = config.Score;
            s.HalfLifeMinutes = ClampF(s.HalfLifeMinutes, 1f, 10080f);
            s.KindCooldownSeconds = Clamp(s.KindCooldownSeconds, 0, 3600);
            ResponseSettings r = config.Responses;
            string mode = (r.Mode ?? "").Trim().ToLowerInvariant();
            if (mode != "watch" && mode != "enforce")
            {
                PrintWarning("Responses.Mode must be \"watch\" or \"enforce\"; using \"watch\".");
                mode = "watch";
            }
            r.Mode = mode;
            r.AlertScore = ClampF(r.AlertScore, 1f, 100000f);
            r.AlertCooldownMinutes = Clamp(r.AlertCooldownMinutes, 0, 1440);
            r.FreezeScore = ClampF(r.FreezeScore, r.AlertScore, 100000f);
            r.FreezeMinutes = Clamp(r.FreezeMinutes, 1, 1440);
            r.KickScore = ClampF(r.KickScore, r.FreezeScore, 100000f);
            r.KickCooldownMinutes = Clamp(r.KickCooldownMinutes, 1, 1440);
            r.BanScore = ClampF(r.BanScore, r.KickScore, 100000f);
            AlertSettings a = config.Alerts;
            a.MaxChatAlertsPerMinute = Clamp(a.MaxChatAlertsPerMinute, 1, 60);
            a.MaxStored = Clamp(a.MaxStored, 10, 5000);
            a.MaxEvidence = Clamp(a.MaxEvidence, 10, 50000);
            a.FeedAlerts = Clamp(a.FeedAlerts, 1, 1000);
            a.FeedSuspects = Clamp(a.FeedSuspects, 1, 500);
        }

        private bool Enforcing
        {
            get { return config.Responses.Mode == "enforce"; }
        }

        #endregion

        #region Data

        private class PlayerRec
        {
            public string Name = "";
            public long FirstSeen;
            public long LastSeen;
            public double Score;                                 // value at ScoreAt; decays from there
            public long ScoreAt;
            public double PeakScore;
            public Dictionary<string, int> Counts = new Dictionary<string, int>();      // scored detections
            public Dictionary<string, int> Suppressed = new Dictionary<string, int>();  // seen inside the cooldown
            public Dictionary<string, long> LastKindAt = new Dictionary<string, long>();
            public long FrozenUntil;
            public string FreezeReason = "";
            public string FreezeBy = "";
            public bool HasFreezePos;
            public float FreezeX, FreezeY, FreezeZ;
            public bool FreezeNotified;
            public long LastAlertAt;
            public string LastResponse = "";
            public long LastResponseAt;
            public long LastKickAt;
            public int Kicks;
            public int LastLevel;                                // highest response level alerted (1 alert .. 4 ban)
            public bool Banned;
        }

        private class Evidence
        {
            public int Id;
            public long Ts;
            public string Kind = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public double Points;
            public double ScoreAfter;
            public string Pos = "";
            public int PingMs;
            public string Detail = "";
        }

        private class Alert
        {
            public int Id;
            public long Ts;
            public string Kind = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public double Score;
            public string Response = "";
            public string Detail = "";
        }

        private class StaffRec
        {
            public string Name = "";
            public long LastSeen;
        }

        private class Peak
        {
            public double Value;
            public string PlayerName = "";
            public long At;
        }

        private class StoredData
        {
            public int Version = 1;
            public int NextEvidenceId = 1;
            public int NextAlertId = 1;
            public long PeaksSince;
            public Dictionary<string, PlayerRec> Players = new Dictionary<string, PlayerRec>();
            public Dictionary<string, StaffRec> Staff = new Dictionary<string, StaffRec>();
            public Dictionary<string, Peak> Peaks = new Dictionary<string, Peak>();
            public List<Evidence> Evidence = new List<Evidence>();
            public List<Alert> Alerts = new List<Alert>();
        }

        // What Realm Steward reads (plugins/docs/RealmSentinel.md, "The Steward feed"). Stable field names.
        private class Feed
        {
            public int Version = FeedVersion;
            public string Generated = "";
            public string Mode = "";
            public bool DataDamaged;
            public int Online;
            public int Frozen;
            public List<FeedAlert> Alerts = new List<FeedAlert>();
            public List<FeedSuspect> Suspects = new List<FeedSuspect>();
        }

        private class FeedAlert
        {
            public int Id;
            public string Time = "";
            public string Kind = "";
            public string PlayerId = "";
            public string PlayerName = "";
            public double Score;
            public string Response = "";
            public string Detail = "";
        }

        private class FeedSuspect
        {
            public string PlayerId = "";
            public string PlayerName = "";
            public double Score;
            public double PeakScore;
            public bool Online;
            public bool Frozen;
            public string LastSeen = "";
            public Dictionary<string, int> Counts = new Dictionary<string, int>();
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
                    + ". Running from memory; the file will NOT be overwritten. Fix or remove it, then reload.");
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
            if (data.Staff == null) data.Staff = new Dictionary<string, StaffRec>();
            if (data.Peaks == null) data.Peaks = new Dictionary<string, Peak>();
            if (data.Evidence == null) data.Evidence = new List<Evidence>();
            if (data.Alerts == null) data.Alerts = new List<Alert>();
            List<string> bad = new List<string>();
            foreach (KeyValuePair<string, PlayerRec> kv in data.Players)
            {
                PlayerRec r = kv.Value;
                if (r == null) { bad.Add(kv.Key); continue; }
                if (r.Name == null) r.Name = "";
                if (r.Counts == null) r.Counts = new Dictionary<string, int>();
                if (r.Suppressed == null) r.Suppressed = new Dictionary<string, int>();
                if (r.LastKindAt == null) r.LastKindAt = new Dictionary<string, long>();
                if (r.FreezeReason == null) r.FreezeReason = "";
                if (r.FreezeBy == null) r.FreezeBy = "";
                if (r.LastResponse == null) r.LastResponse = "";
                if (double.IsNaN(r.Score) || double.IsInfinity(r.Score) || r.Score < 0) r.Score = 0;
            }
            foreach (string k in bad) data.Players.Remove(k);
            List<string> badStaff = new List<string>();
            foreach (KeyValuePair<string, StaffRec> kv in data.Staff) if (kv.Value == null || kv.Value.Name == null) badStaff.Add(kv.Key);
            foreach (string k in badStaff) data.Staff.Remove(k);
            List<string> badPeaks = new List<string>();
            foreach (KeyValuePair<string, Peak> kv in data.Peaks) if (kv.Value == null) badPeaks.Add(kv.Key);
            foreach (string k in badPeaks) data.Peaks.Remove(k);
            foreach (Peak p in data.Peaks.Values) if (p.PlayerName == null) p.PlayerName = "";
            data.Evidence.RemoveAll(delegate(Evidence e) { return e == null || e.Kind == null; });
            data.Alerts.RemoveAll(delegate(Alert a) { return a == null || a.Kind == null; });
            foreach (Evidence e in data.Evidence)
            {
                if (e.PlayerId == null) e.PlayerId = "";
                if (e.PlayerName == null) e.PlayerName = "";
                if (e.Pos == null) e.Pos = "";
                if (e.Detail == null) e.Detail = "";
                if (e.Id >= data.NextEvidenceId) data.NextEvidenceId = e.Id + 1;
            }
            foreach (Alert a in data.Alerts)
            {
                if (a.PlayerId == null) a.PlayerId = "";
                if (a.PlayerName == null) a.PlayerName = "";
                if (a.Response == null) a.Response = "";
                if (a.Detail == null) a.Detail = "";
                if (a.Id >= data.NextAlertId) data.NextAlertId = a.Id + 1;
            }
            if (data.PeaksSince == 0) data.PeaksSince = (long)NowSec();
        }

        private void SaveData()
        {
            if (data == null || loadFailed) return;              // never overwrite the file after a failed load
            PrunePlayers();
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
            lastSave = NowSec();
        }

        // Keeps the player table bounded: drops the longest-unseen players with no detections first.
        private void PrunePlayers()
        {
            if (data.Players.Count <= MaxPlayersStored) return;
            List<KeyValuePair<string, PlayerRec>> all = new List<KeyValuePair<string, PlayerRec>>(data.Players);
            all.Sort(delegate(KeyValuePair<string, PlayerRec> a, KeyValuePair<string, PlayerRec> b)
            {
                int fa = a.Value.Counts.Count > 0 ? 1 : 0, fb = b.Value.Counts.Count > 0 ? 1 : 0;
                if (fa != fb) return fa.CompareTo(fb);
                return a.Value.LastSeen.CompareTo(b.Value.LastSeen);
            });
            int remove = data.Players.Count - MaxPlayersStored;
            for (int i = 0; i < remove; i++) data.Players.Remove(all[i].Key);
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
                { "Speaker", "Sentinel" },
                { "PlayerInfo", "The Sentinel watches the realm for cheating. Seen something? Tell the admins with [F4C96D]/warden report[FFFFFF] <player> <reason>." },
                { "HelpHeader", "Sentinel (staff): suspicion scores, evidence and responses. Mode: {0}." },
                { "Help1", "  [F4C96D]/sentinel status[FFFFFF] - mode, checks, top suspects. [F4C96D]/sentinel report[FFFFFF] <player> [page] - score, counts and evidence." },
                { "Help2", "  [F4C96D]/sentinel clear[FFFFFF] <player> - reset score and lift a freeze. [F4C96D]/sentinel reload[FFFFFF] - re-read the config." },
                { "Help3", "  [F4C96D]/sentinel freeze[FFFFFF] <player> [minutes] | [F4C96D]/sentinel unfreeze[FFFFFF] <player> | [F4C96D]/sentinel ban[FFFFFF] <player> confirm" },
                { "Help4", "  [F4C96D]/sentinel peaks[FFFFFF] [reset] - the highest honest values seen, for tuning the limits." },
                { "NoPermission", "You may not do that." },
                { "PlayerNotFound", "No one by that name is online or in the Sentinel's records." },
                { "Ambiguous", "More than one player matches '{0}'. Use more of the name or their Steam ID." },
                { "BadNumber", "'{0}' is not a whole number from {1} to {2}." },
                { "DataDamaged", "The Sentinel's records are damaged (oxide/data/RealmSentinel.json). Nothing is being saved until an admin repairs it." },
                { "StatusMode", "Mode: {0}. Checks on: {1}. Online watched: {2}. Frozen: {3}. Alerts stored: {4}." },
                { "StatusResponses", "Responses: alert at {0}, freeze at {1}, kick at {2}, ban at {3} ({4}). Score halves every {5} min." },
                { "StatusTop", "Top suspects: {0}" },
                { "StatusNone", "No one has a suspicion score." },
                { "StatusFeed", "Steward feed: oxide/data/RealmSentinelFeed.json ({0}). Warden forwarding: {1}." },
                { "WatchNote", "Watch mode: nobody is frozen or kicked automatically. Set Responses.Mode to \"enforce\" after the smoke test." },
                { "ReportHeader", "{0} ({1}): score {2} (peak {3}), first seen {4}, last seen {5}." },
                { "ReportCounts", "Detections: {0}" },
                { "ReportState", "Frozen: {0}. Last response: {1}. Kicks: {2}. Online: {3}{4}." },
                { "ReportEvidence", "Evidence page {0}/{1}:" },
                { "EvidenceLine", "  #{0} {1} {2} +{3} -> {4}: {5}{6}" },
                { "ReportNoEvidence", "No evidence recorded for this player." },
                { "ClearDone", "Cleared {0}: score 0, detection counts reset, freeze lifted. The evidence log is kept." },
                { "Reloaded", "Config re-read. Mode: {0}." },
                { "ReloadFailed", "The config could not be read ({0}). The old settings stay." },
                { "FreezeSet", "{0} is frozen for {1}." },
                { "UnfreezeSet", "{0} is no longer frozen." },
                { "NotFrozen", "{0} is not frozen." },
                { "BanConfirm", "This bans {0} ({1}) for good. Type [F4C96D]/sentinel ban[FFFFFF] <player> confirm to do it." },
                { "BanDone", "{0} is banned. The evidence stays in the Sentinel's records." },
                { "BanFailed", "The game did not accept the ban for {0}. Use Realm Steward's Court screen." },
                { "PeaksHeader", "Highest values seen from players under the alert score since {0}:" },
                { "PeakLine", "  {0}: {1} ({2}, {3})" },
                { "PeaksNone", "No values recorded yet." },
                { "PeaksReset", "Peaks reset." },
                { "AlertChat", "[E8913A]Sentinel #{0} {1}[FFFFFF] {2} (score {3}): {4}" },
                { "AlertOverflow", "+{0} more Sentinel alerts were queued. [F4C96D]/sentinel status[FFFFFF]" },
                { "AdminJoin", "{0} player(s) have a high suspicion score. [F4C96D]/sentinel status[FFFFFF]" },
                { "FrozenNotice", "You are held by the Sentinel for review: you cannot fight, build, break, craft or loot for up to {0}. An admin has been told." },
                { "FrozenBlocked", "You are held by the Sentinel for review ({0} left)." },
                { "Released", "The Sentinel has released you. Play on." },
                { "CommandSlow", "Slow down: at most {0} commands every {1} s." },
                { "KickReason", "Removed by the Sentinel for review. An admin will look at it." },
                { "ImpersonationKick", "Your name looks like a staff member's. Change it in Steam and rejoin." },
                { "ImpersonationWarn", "Your name '{0}' looks like a staff member's. Please change it in Steam. The admins have been told." },
                { "ReconnectRefused", "Too many reconnects. Wait {0} and try again." },
                { "Off", "off" },
                { "On", "on" },
                { "None", "none" },
                { "Yes", "yes" },
                { "No", "no" }
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
            player.SendError(Styled(Msg("Speaker", player), WarnKeys.Contains(key) ? ChatWarn : ChatError, Fmt(key, player, args)));
        }

        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "ClearDone", "Reloaded", "FreezeSet", "UnfreezeSet", "BanDone", "PeaksReset", "Released"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "FrozenNotice", "FrozenBlocked", "CommandSlow", "ImpersonationWarn", "AlertOverflow", "AdminJoin", "WatchNote",
            "BanConfirm", "DataDamaged"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
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
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            if (initialized) return;                             // re-sent on hot load; keep idempotent
            initialized = true;
            lastSave = NowSec();
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                Touch(p);
                LearnStaff(p);
            }
            SubscribeGameEvents();
            timer.Every(config.Movement.SampleSeconds, MovementTick);
            timer.Every(config.Items.SampleSeconds, ItemTick);
            timer.Every(HousekeepingSeconds, Housekeeping);
            feedDirty = true;
        }

        // [DEC] EventManager.Subscribe<TeleportEvent>: the game's own movement check resets on the same event.
        private void SubscribeGameEvents()
        {
            if (subscribed) return;
            try
            {
                teleportSubscriber = new EventSubscriber<TeleportEvent>(HandleGameTeleport);
                EventManager.Subscribe<TeleportEvent>(teleportSubscriber, EventHandlerOrder.Late);
                subscribed = true;
            }
            catch (Exception ex)
            {
                PrintWarning("Could not watch server teleports (" + ex.Message + "); teleport commands still give grace.");
            }
        }

        private void UnsubscribeGameEvents()
        {
            if (!subscribed) return;
            try { EventManager.Unsubscribe<TeleportEvent>(teleportSubscriber); }
            catch (Exception ex) { PrintWarning("Could not stop watching server teleports: " + ex.Message); }
            subscribed = false;
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnServerShutdown()
        {
            shuttingDown = true;
            SaveData();
            WriteFeed();
        }

        private void Unload()
        {
            UnsubscribeGameEvents();
            SaveData();
            WriteFeed();
        }

        private void Housekeeping()
        {
            try
            {
                double now = NowSec();
                foreach (KeyValuePair<string, PlayerRec> kv in data.Players)
                {
                    PlayerRec r = kv.Value;
                    if (r.FrozenUntil <= 0 || r.FrozenUntil > now) continue;
                    r.FrozenUntil = 0;
                    r.HasFreezePos = false;
                    dirty = true;
                    feedDirty = true;
                    Player online = FindOnlineById(kv.Key);
                    if (online != null && r.FreezeNotified) Reply(online, "Released");
                    r.FreezeNotified = false;
                }
                if (dirty && now - lastSave >= config.General.SaveIntervalSeconds) SaveData();
                if (feedDirty) WriteFeed();
            }
            catch (Exception ex)
            {
                PrintError("Housekeeping failed: " + ex.Message);
            }
        }

        #endregion

        #region Players and exemptions

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

        private bool IsAdmin(Player player)
        {
            return player != null && !player.IsServer && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        // Skips every check: Sentinel admins (General.AdminsExempt) and listed Steam IDs.
        private bool IsExempt(Player p)
        {
            if (p == null || p.IsServer) return true;
            if (config.General.AdminsExempt && IsAdmin(p)) return true;
            return config.General.ExemptIds.Contains(p.Id.ToString());
        }

        // Movement only: the game's own fly/teleport permissions, cached for 15 s.
        private bool IsMovementExempt(Player p, double now)
        {
            if (IsExempt(p)) return true;
            if (!config.Movement.ExemptGameFlyPermissions) return false;
            double until;
            bool cached;
            if (movementExemptUntil.TryGetValue(p.Id, out until) && now < until && movementExempt.TryGetValue(p.Id, out cached)) return cached;
            bool exempt = false;
            try
            {
                foreach (string perm in GameMovementPermissions)
                {
                    if (p.HasPermission(perm)) { exempt = true; break; }
                }
            }
            catch (Exception)
            {
                exempt = false;
            }
            movementExempt[p.Id] = exempt;
            movementExemptUntil[p.Id] = now + 15;
            return exempt;
        }

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer || data == null) return;
            try
            {
                double now = NowSec();
                PlayerRec r = Touch(player);
                kicking.Remove(player.Id);
                moves.Remove(player.Id);
                items.Remove(player.Id);
                OpenSpawnWindow(player.Id);
                LearnStaff(player);
                if (IsAdmin(player))
                {
                    if (loadFailed) ReplyError(player, "DataDamaged");
                    int high = HighScoreCount(now);
                    if (high > 0) ReplyError(player, "AdminJoin", high);
                }
                if (r.FrozenUntil > now)
                {
                    r.FreezeNotified = true;
                    ReplyError(player, "FrozenNotice", Dur(r.FrozenUntil - now));
                }
                if (config.General.Enabled && !IsExempt(player))
                {
                    TrackConnect(player, now);
                    CheckImpersonation(player);
                }
                feedDirty = true;
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
                PlayerRec r = Rec(player.Id);
                if (r != null) { r.LastSeen = (long)NowSec(); dirty = true; }
                moves.Remove(player.Id);
                items.Remove(player.Id);
                fights.Remove(player.Id);
                floods.Remove(player.Id);
                movementExempt.Remove(player.Id);
                movementExemptUntil.Remove(player.Id);
                kicking.Remove(player.Id);
                spawnOpenUntil.Remove(player.Id);
                feedDirty = true;
            }
            catch (Exception ex)
            {
                PrintError("Disconnect check failed: " + ex.Message);
            }
        }

        #endregion

        #region Movement

        private class MoveState
        {
            public Entity Entity;
            public bool Has;
            public float X, Y, Z;
            public double T;
            public double Budget;
            public double StillSince;                            // last time the position changed (lag allowance)
            public double GraceUntil;
            public List<double> Violations = new List<double>();
            public List<double> RiseT = new List<double>();
            public List<float> RiseY = new List<float>();
        }

        private MoveState Move(ulong id)
        {
            MoveState st;
            if (!moves.TryGetValue(id, out st)) { st = new MoveState(); moves[id] = st; }
            return st;
        }

        private void Grace(ulong id, double seconds)
        {
            if (seconds <= 0) return;
            MoveState st = Move(id);
            double until = NowSec() + seconds;
            if (until > st.GraceUntil) st.GraceUntil = until;
            st.Has = false;
        }

        private void MovementTick()
        {
            if (data == null || !config.General.Enabled) return;
            try
            {
                double now = NowSec();
                foreach (Player p in Server.ClientPlayers)
                {
                    if (p == null || p.IsServer) continue;
                    SampleMovement(p, now);
                }
            }
            catch (Exception ex)
            {
                PrintError("Movement sample failed: " + ex.Message);
            }
        }

        private void SampleMovement(Player p, double now)
        {
            MoveState st = Move(p.Id);
            Entity e = p.Entity;
            if (e == null) { st.Has = false; st.Entity = null; return; }
            Vector3 pos;
            try { pos = e.Position; }
            catch (Exception) { st.Has = false; return; }

            PlayerRec rec = Rec(p.Id);
            if (rec != null && rec.FrozenUntil > now)
            {
                PinFrozen(p, rec, pos);
                st.Has = false;
                return;
            }
            if (!config.Movement.Enabled || IsMovementExempt(p, now) || now < st.GraceUntil || st.Entity != e || !st.Has)
            {
                Restart(st, e, pos, now);
                return;
            }
            MovementSettings c = config.Movement;
            double dt = now - st.T;
            if (dt <= 0.05) return;
            if (dt > c.MaxGapSeconds) { Restart(st, e, pos, now); return; }

            double dx = pos.x - st.X, dy = pos.y - st.Y, dz = pos.z - st.Z;
            double up = dy > 0 ? dy : 0;
            double dist = Math.Sqrt(dx * dx + dz * dz + up * up);
            bool falling = dy / dt < -c.FallVelocity;
            double maxSpeed = falling ? c.MaxSpeedFalling : c.MaxSpeed;
            int ping = Math.Min(PingMs(p), c.MaxPingCreditMs);
            double cap = c.MaxSpeed * (c.BurstSeconds + 2.0 * ping / 1000.0 * c.PingFactor);
            double still = st.StillSince > 0 ? now - dt - st.StillSince : 0;   // standing still before this sample
            if (still > 0.9) cap += c.MaxSpeed * Math.Min(still, c.LagAllowanceSeconds);
            if (falling) cap += (c.MaxSpeedFalling - c.MaxSpeed) * dt;
            double budget = Math.Min(cap, st.Budget + maxSpeed * dt * (1.0 + c.SpeedTolerancePercent / 100.0));

            if (dist <= budget)
            {
                st.Budget = budget - dist;
                if (dist >= 0.5 && rec != null && CurrentScore(rec, now) < config.Responses.AlertScore)
                    NotePeak("speed_mps", dist / dt, p);
            }
            else
            {
                double excess = dist - budget;
                st.Budget = 0;
                string where = Pos3(st.X, st.Y, st.Z) + " -> " + Pos(pos);
                if (excess >= c.TeleportMeters)
                {
                    Detect(p, KTeleport, excess / c.TeleportMeters, "moved " + F1(dist) + " m in " + F1(dt) + " s (" + F1(excess)
                        + " m over the limit) " + where + ", ping " + ping + " ms");
                    st.Violations.Clear();
                }
                else if (excess >= c.MinExcessMeters)
                {
                    st.Violations.Add(now);
                    st.Violations.RemoveAll(delegate(double t) { return now - t > c.ViolationWindowSeconds; });
                    if (st.Violations.Count >= c.ViolationsToFlag)
                    {
                        Detect(p, KSpeed, (dist / dt) / c.MaxSpeed, st.Violations.Count + " overshoots in " + c.ViolationWindowSeconds
                            + " s; last " + F1(dist / dt) + " m/s (limit " + F1(maxSpeed) + ") " + where + ", ping " + ping + " ms");
                        st.Violations.Clear();
                    }
                }
            }

            if (c.CheckAscent) CheckAscent(p, st, pos, now);
            if (dist > 0.01 || Math.Abs(dy) > 0.01) st.StillSince = now;
            st.X = pos.x; st.Y = pos.y; st.Z = pos.z; st.T = now;
        }

        private void Restart(MoveState st, Entity e, Vector3 pos, double now)
        {
            st.Entity = e;
            st.Has = true;
            st.X = pos.x; st.Y = pos.y; st.Z = pos.z;
            st.T = now;
            st.Budget = config.Movement.MaxSpeed * config.Movement.BurstSeconds;
            st.StillSince = now;
            st.Violations.Clear();
            st.RiseT.Clear();
            st.RiseY.Clear();
        }

        // Sustained ascent: the net rise inside the window since the lowest point, with no fall in between.
        private void CheckAscent(Player p, MoveState st, Vector3 pos, double now)
        {
            MovementSettings c = config.Movement;
            if (st.RiseY.Count > 0 && pos.y < st.RiseY[st.RiseY.Count - 1] - 0.5f)
            {
                st.RiseT.Clear();                                // came down: the climb starts again from here
                st.RiseY.Clear();
            }
            st.RiseT.Add(now);
            st.RiseY.Add(pos.y);
            while (st.RiseT.Count > 0 && now - st.RiseT[0] > c.AscentWindowSeconds)
            {
                st.RiseT.RemoveAt(0);
                st.RiseY.RemoveAt(0);
            }
            if (st.RiseY.Count < 3) return;
            float low = st.RiseY[0];
            foreach (float y in st.RiseY) if (y < low) low = y;
            float rise = pos.y - low;
            PlayerRec rec = Rec(p.Id);
            if (rise <= c.MaxRiseMeters)
            {
                if (rec != null && CurrentScore(rec, now) < config.Responses.AlertScore) NotePeak("rise_m", rise, p);
                return;
            }
            Detect(p, KFly, rise / c.MaxRiseMeters, "rose " + F1(rise) + " m in " + F1(now - st.RiseT[0]) + " s without coming down, now at "
                + Pos(pos) + ", ping " + PingMs(p) + " ms");
            st.RiseT.Clear();
            st.RiseY.Clear();
        }

        private void PinFrozen(Player p, PlayerRec rec, Vector3 pos)
        {
            if (!config.Responses.PinFrozenPosition) return;
            if (!rec.HasFreezePos)
            {
                // Frozen while offline: hold them where they are first seen.
                rec.HasFreezePos = true;
                rec.FreezeX = pos.x; rec.FreezeY = pos.y; rec.FreezeZ = pos.z;
                dirty = true;
                return;
            }
            double dx = pos.x - rec.FreezeX, dy = pos.y - rec.FreezeY, dz = pos.z - rec.FreezeZ;
            if (dx * dx + dy * dy + dz * dz < 9.0) return;
            try
            {
                Vector3 back = new Vector3();
                back.x = rec.FreezeX; back.y = rec.FreezeY; back.z = rec.FreezeZ;
                CharacterTeleport tp = p.Entity.GetOrCreate<CharacterTeleport>();
                if (tp != null) tp.Teleport(back);              // [DEC] the call the game's own /tp makes
            }
            catch (Exception ex)
            {
                if (!Throttled("pin|" + p.Id, 60)) PrintWarning("Could not hold " + Clean(p.Name, 40) + " in place: " + ex.Message);
            }
        }

        // A teleport the SERVER made (admin /tp, respawn, plugins). A client-sent TeleportEvent never gives grace.
        private void HandleGameTeleport(TeleportEvent evt)
        {
            try
            {
                if (evt == null || evt.Entity == null) return;
                if (evt.Sender != null && !evt.Sender.IsServer) return;
                Player owner = evt.Entity.Owner;
                if (owner == null || owner.IsServer) return;
                Grace(owner.Id, config.Movement.GraceAfterTeleportSeconds);
            }
            catch (Exception ex)
            {
                PrintError("Teleport watch failed: " + ex.Message);
            }
        }

        // Spawn events may be sent by the client ([DEC] PlayerPreSpawnCompleteEvent has a client permission string), so they
        // give grace only inside a spawn window that a connect or a death opened; the first one closes it 20 s later.
        // A new body (a different Entity) restarts sampling in any case.
        private void OpenSpawnWindow(ulong id)
        {
            spawnOpenUntil[id] = NowSec() + 300;
        }

        private void SpawnGrace(Player p)
        {
            if (p == null || p.IsServer) return;
            double now = NowSec();
            double until;
            if (!spawnOpenUntil.TryGetValue(p.Id, out until) || now > until) return;
            if (until > now + 20) spawnOpenUntil[p.Id] = now + 20;
            Grace(p.Id, config.Movement.GraceAfterRespawnSeconds);
            items.Remove(p.Id);
        }

        private void OnPlayerRespawn(PlayerRespawnEvent evt)
        {
            if (evt != null) SpawnGrace(evt.Player);
        }

        private void OnPlayerSpawn(PlayerFirstSpawnEvent evt)
        {
            if (evt != null) SpawnGrace(evt.Player);
        }

        private void OnPlayerSpawned(PlayerPreSpawnCompleteEvent evt)
        {
            if (evt != null) SpawnGrace(evt.Player);
        }

        #endregion

        #region Combat

        private class CombatState
        {
            public Dictionary<int, List<double>> MeleeHits = new Dictionary<int, List<double>>();   // per target entity
            public List<double> ProjectileHits = new List<double>();
            public List<double> Kills = new List<double>();
        }

        private CombatState Fight(ulong id)
        {
            CombatState st;
            if (!fights.TryGetValue(id, out st)) { st = new CombatState(); fights[id] = st; }
            return st;
        }

        // RB 1 [OPJ L162]. Only hits whose source is a player; only the client that SENT the hit is scored.
        private object OnEntityHealthChange(EntityDamageEvent evt)
        {
            if (data == null || evt == null) return null;
            try
            {
                Damage d = evt.Damage;
                if (d == null || d.Amount <= 0f || d.DamageSource == null || !d.DamageSource.IsPlayer) return null;
                Player attacker = d.DamageSource.Owner;
                if (attacker == null || attacker.IsServer) return null;
                Entity target = evt.Entity;
                if (target == null) return null;
                Player victim = target.IsPlayer ? target.Owner : null;
                if (victim != null && victim.Id == attacker.Id) return null;
                double now = NowSec();

                if (!evt.Cancelled && IsFrozenId(attacker.Id, now))
                {
                    evt.Cancel("Held by the Sentinel");
                    d.Amount = 0f;
                    NotifyFrozen(attacker, now);
                    return true;
                }
                if (evt.Cancelled || !config.General.Enabled || !config.Combat.Enabled || IsExempt(attacker)) return null;

                bool projectile = (d.DamageTypes & DamageType.Projectile) != 0;
                bool melee = !projectile && (d.DamageTypes & (DamageType.Melee | DamageType.Slash | DamageType.Bash
                    | DamageType.Pierce | DamageType.Cut)) != 0;
                if (!projectile && !melee) return null;

                Player reporter = evt.Sender;
                if (reporter != null && reporter.IsServer) return null;                   // server-made damage
                bool attackerReported = reporter == null || reporter.Id == attacker.Id;
                double dist = Distance(d.DamageSource.Position, target.Position);
                if (!attackerReported) return null;              // a victim's report never scores the attacker (framing)

                CombatSettings c = config.Combat;
                int pingA = Math.Min(PingMs(attacker), config.Movement.MaxPingCreditMs);
                int pingV = victim != null ? Math.Min(PingMs(victim), config.Movement.MaxPingCreditMs) : 0;
                double credit = (pingA + pingV) / 1000.0 * config.Movement.MaxSpeed * c.PingFactor + c.RangeSlackMeters;
                double range = (projectile ? c.ProjectileRange : c.MeleeRange) + credit;
                double maxDamage = projectile ? c.ProjectileMaxDamage : c.MeleeMaxDamage;
                string what = projectile ? "projectile" : "melee";
                string on = victim != null ? Clean(victim.Name, 40) : TargetLabel(target);
                PlayerRec rec = Rec(attacker.Id);
                bool honest = rec == null || CurrentScore(rec, now) < config.Responses.AlertScore;
                bool flagged = false;

                if (dist > range)
                {
                    flagged = true;
                    Detect(attacker, KReach, dist / range, what + " hit on " + on + " from " + F1(dist) + " m (limit " + F1(range)
                        + " with ping credit), pings " + pingA + "/" + pingV + " ms");
                }
                if (d.Amount > maxDamage)
                {
                    flagged = true;
                    Detect(attacker, KDamage, d.Amount / maxDamage, what + " hit on " + on + " for " + F1(d.Amount) + " (limit " + F1(maxDamage) + ")");
                }

                CombatState st = Fight(attacker.Id);
                int window = c.RateWindowSeconds;
                if (projectile)
                {
                    st.ProjectileHits.Add(now);
                    st.ProjectileHits.RemoveAll(delegate(double t) { return now - t > window; });
                    if (st.ProjectileHits.Count > c.ProjectileMaxHitsPerWindow)
                    {
                        flagged = true;
                        Detect(attacker, KFireRate, (double)st.ProjectileHits.Count / c.ProjectileMaxHitsPerWindow, st.ProjectileHits.Count
                            + " projectile hits in " + window + " s (limit " + c.ProjectileMaxHitsPerWindow + ")");
                        st.ProjectileHits.Clear();
                    }
                    else if (honest) NotePeak("projectile_hits_per_window", st.ProjectileHits.Count, attacker);
                }
                else
                {
                    int key = TargetKey(target);
                    List<double> hits;
                    if (!st.MeleeHits.TryGetValue(key, out hits)) { hits = new List<double>(); st.MeleeHits[key] = hits; }
                    hits.Add(now);
                    hits.RemoveAll(delegate(double t) { return now - t > window; });
                    if (st.MeleeHits.Count > 64) PruneMelee(st, now, window);
                    int limit = (int)Math.Floor(window / c.MeleeMinIntervalPerTarget) + 2;   // jitter-proof: a window, not one gap
                    if (hits.Count > limit)
                    {
                        flagged = true;
                        Detect(attacker, KFireRate, (double)hits.Count / limit, hits.Count + " melee hits on " + on + " in " + window + " s (limit "
                            + limit + ")");
                        hits.Clear();
                    }
                    else if (honest) NotePeak("melee_hits_per_target_window", hits.Count, attacker);
                }
                if (!flagged && honest)
                {
                    NotePeak(projectile ? "projectile_range_m" : "melee_range_m", dist, attacker);
                    NotePeak(projectile ? "projectile_damage" : "melee_damage", d.Amount, attacker);
                }
                return null;
            }
            catch (Exception ex)
            {
                PrintError("Damage check failed: " + ex.Message);
                return null;
            }
        }

        private static void PruneMelee(CombatState st, double now, int window)
        {
            List<int> stale = new List<int>();
            foreach (KeyValuePair<int, List<double>> kv in st.MeleeHits)
                if (kv.Value.Count == 0 || now - kv.Value[kv.Value.Count - 1] > window) stale.Add(kv.Key);
            foreach (int k in stale) st.MeleeHits.Remove(k);
        }

        private static int TargetKey(Entity target)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target);
        }

        private static string TargetLabel(Entity target)
        {
            try { return "a creature or object at " + Pos(target.Position); }
            catch (Exception) { return "a creature or object"; }
        }

        // RB 1 [OPJ L188]. Kill rate of online victims; a death also restarts the victim's sampling.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            if (data == null || evt == null || evt.Entity == null) return null;
            try
            {
                if (!evt.Entity.IsPlayer) return null;
                Player victim = evt.Entity.Owner;
                if (victim == null || victim.IsServer) return null;
                Grace(victim.Id, config.Movement.GraceAfterRespawnSeconds);
                OpenSpawnWindow(victim.Id);
                items.Remove(victim.Id);
                if (!config.General.Enabled || !config.Combat.Enabled) return null;
                Damage d = evt.KillingDamage;
                if (d == null || d.DamageSource == null || !d.DamageSource.IsPlayer) return null;
                Player killer = d.DamageSource.Owner;
                if (killer == null || killer.IsServer || killer.Id == victim.Id || IsExempt(killer)) return null;
                if (Server.GetPlayerById(victim.Id) == null) return null;   // sleepers do not count
                double now = NowSec();
                CombatSettings c = config.Combat;
                CombatState st = Fight(killer.Id);
                st.Kills.Add(now);
                st.Kills.RemoveAll(delegate(double t) { return now - t > c.KillWindowSeconds; });
                if (st.Kills.Count > c.MaxKillsPerWindow)
                {
                    Detect(killer, KKillRate, (double)st.Kills.Count / c.MaxKillsPerWindow, st.Kills.Count + " player kills in " + Dur(c.KillWindowSeconds)
                        + " (limit " + c.MaxKillsPerWindow + "); last: " + Clean(victim.Name, 40));
                    st.Kills.Clear();
                }
                else
                {
                    PlayerRec rec = Rec(killer.Id);
                    if (rec == null || CurrentScore(rec, now) < config.Responses.AlertScore) NotePeak("kills_per_window", st.Kills.Count, killer);
                }
                return null;
            }
            catch (Exception ex)
            {
                PrintError("Death check failed: " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Items

        private class ItemState
        {
            public Entity Entity;
            public Dictionary<string, int> Counts;
            public Dictionary<string, List<KeyValuePair<double, int>>> Credit = new Dictionary<string, List<KeyValuePair<double, int>>>();
            public List<KeyValuePair<double, int>> Gains = new List<KeyValuePair<double, int>>();
            public double SourceUntil;                           // trusted /give, paying Realm command, plugin API
            public Dictionary<string, double> Crafted = new Dictionary<string, double>();
            public List<double> Crafts = new List<double>();
        }

        // A container someone opened. What it held when first opened is trusted; later deposits become trusted only
        // when a watcher's own loss in the same sample accounts for them. Withdrawals are explained only up to the
        // trusted amount, so items spawned and stashed inside one sample cannot be "withdrawn" clean later.
        private class BoxWatch
        {
            public Container Box;
            public Dictionary<string, int> Last = new Dictionary<string, int>();
            public Dictionary<string, int> Trusted = new Dictionary<string, int>();
            public Dictionary<ulong, double> Watchers = new Dictionary<ulong, double>();
            public Dictionary<string, int> Out = new Dictionary<string, int>();   // explained withdrawals this sample
            public Dictionary<string, int> In = new Dictionary<string, int>();    // deposits this sample, unmatched
            public bool Gone;
        }

        private readonly List<BoxWatch> boxes = new List<BoxWatch>();
        private const int MaxBoxes = 500;

        private ItemState ItemsOf(ulong id)
        {
            ItemState st;
            if (!items.TryGetValue(id, out st)) { st = new ItemState(); items[id] = st; }
            return st;
        }

        private void ItemSource(ulong id, double seconds)
        {
            if (seconds <= 0) return;
            ItemState st = ItemsOf(id);
            double until = NowSec() + seconds;
            if (until > st.SourceUntil) st.SourceUntil = until;
        }

        private void ItemTick()
        {
            if (data == null || !config.General.Enabled || !config.Items.Enabled) return;
            try
            {
                double now = NowSec();
                UpdateBoxes();
                foreach (Player p in Server.ClientPlayers)
                {
                    if (p == null || p.IsServer) continue;
                    SampleItems(p, now);
                }
                ExpireBoxes(now);
            }
            catch (Exception ex)
            {
                PrintError("Item sample failed: " + ex.Message);
            }
        }

        private Dictionary<string, int> ReadCounts(Entity e)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            bool any = false;
            Container inv = e.GetContainerOfType(CollectionTypes.Inventory);
            if (inv != null) { any = true; AddCounts(counts, inv); }
            if (config.Items.IncludeHotbar)
            {
                Container bar = e.GetContainerOfType(CollectionTypes.Hotbar);
                if (bar != null) { any = true; AddCounts(counts, bar); }
            }
            return any ? counts : null;
        }

        private static void AddCounts(Dictionary<string, int> counts, Container c)
        {
            ItemCollection col = c.Contents;
            if (col == null) return;
            foreach (InvGameItemStack s in col)
            {
                if (s == null || s.StackAmount <= 0) continue;
                string name = s.Name ?? "(null)";
                int n;
                counts.TryGetValue(name, out n);
                counts[name] = n + s.StackAmount;
            }
        }

        private void SampleItems(Player p, double now)
        {
            if (IsExempt(p)) { items.Remove(p.Id); return; }
            Entity e = p.Entity;
            if (e == null) { items.Remove(p.Id); return; }
            Dictionary<string, int> counts;
            try { counts = ReadCounts(e); }
            catch (Exception) { return; }
            if (counts == null) return;
            ItemState st = ItemsOf(p.Id);
            ItemSettings c = config.Items;
            if (st.Counts == null || st.Entity != e || Move(p.Id).GraceUntil > now)
            {
                st.Entity = e;
                st.Counts = counts;
                return;
            }

            // Losses first: into a watched container (that deposit becomes trusted there), else the player's own credit
            // (dropped, equipped, used), which may come back later.
            foreach (KeyValuePair<string, int> kv in st.Counts)
            {
                int left;
                counts.TryGetValue(kv.Key, out left);
                if (left >= kv.Value) continue;
                int loss = kv.Value - left;
                loss -= MatchDeposit(p.Id, kv.Key, loss, now);
                if (loss > 0) AddCredit(st, kv.Key, loss, now);
            }

            bool source = now < st.SourceUntil;
            int unexplained = 0;
            string jumpName = null;
            int jumpAmount = 0;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                int before;
                st.Counts.TryGetValue(kv.Key, out before);
                int gain = kv.Value - before;
                if (gain <= 0) continue;
                gain -= TakeWithdrawal(p.Id, kv.Key, gain, now);
                if (gain > 0) gain -= TakeCredit(st, kv.Key, gain, now);
                if (gain <= 0 || source) continue;
                double craftedAt;
                if (st.Crafted.TryGetValue(kv.Key, out craftedAt) && now - craftedAt <= c.CraftGraceSeconds) continue;
                unexplained += gain;
                if (gain > jumpAmount) { jumpAmount = gain; jumpName = kv.Key; }
            }
            st.Counts = counts;

            PlayerRec rec = Rec(p.Id);
            bool honest = rec == null || CurrentScore(rec, now) < config.Responses.AlertScore;
            if (jumpName != null && jumpAmount >= c.JumpAmount)
            {
                Detect(p, KItemJump, (double)jumpAmount / c.JumpAmount, "+" + jumpAmount + " " + Clean(jumpName, 40) + " in " + F1(c.SampleSeconds)
                    + " s with no container, craft, give or own drop to explain it");
            }
            else if (honest && jumpAmount > 0) NotePeak("item_gain_per_sample", jumpAmount, p);

            if (unexplained > 0) st.Gains.Add(new KeyValuePair<double, int>(now, unexplained));
            double win = c.GainWindowMinutes * 60.0;
            st.Gains.RemoveAll(delegate(KeyValuePair<double, int> g) { return now - g.Key > win; });
            int total = 0;
            foreach (KeyValuePair<double, int> g in st.Gains) total += g.Value;
            if (total > c.MaxGainPerWindow)
            {
                Detect(p, KGather, (double)total / c.MaxGainPerWindow, "+" + total + " items with no known source in " + c.GainWindowMinutes
                    + " min (limit " + c.MaxGainPerWindow + ")");
                st.Gains.Clear();
            }
            else if (honest && total > 0) NotePeak("item_gain_per_window", total, p);
        }

        private void AddCredit(ItemState st, string name, int amount, double now)
        {
            List<KeyValuePair<double, int>> list;
            if (!st.Credit.TryGetValue(name, out list)) { list = new List<KeyValuePair<double, int>>(); st.Credit[name] = list; }
            list.Add(new KeyValuePair<double, int>(now, amount));
            if (list.Count > 50) list.RemoveAt(0);
            if (st.Credit.Count > 300) PruneCredit(st, now);
        }

        private int TakeCredit(ItemState st, string name, int want, double now)
        {
            List<KeyValuePair<double, int>> list;
            if (!st.Credit.TryGetValue(name, out list)) return 0;
            double life = config.Items.CreditMinutes * 60.0;
            list.RemoveAll(delegate(KeyValuePair<double, int> c) { return now - c.Key > life; });
            int taken = 0;
            while (want > 0 && list.Count > 0)
            {
                KeyValuePair<double, int> first = list[0];
                int use = Math.Min(want, first.Value);
                want -= use;
                taken += use;
                if (use == first.Value) list.RemoveAt(0);
                else list[0] = new KeyValuePair<double, int>(first.Key, first.Value - use);
            }
            if (list.Count == 0) st.Credit.Remove(name);
            return taken;
        }

        private void PruneCredit(ItemState st, double now)
        {
            double life = config.Items.CreditMinutes * 60.0;
            List<string> empty = new List<string>();
            foreach (KeyValuePair<string, List<KeyValuePair<double, int>>> kv in st.Credit)
            {
                kv.Value.RemoveAll(delegate(KeyValuePair<double, int> c) { return now - c.Key > life; });
                if (kv.Value.Count == 0) empty.Add(kv.Key);
            }
            foreach (string k in empty) st.Credit.Remove(k);
        }

        private static Dictionary<string, int> CountsOf(Container c)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            if (c != null) AddCounts(counts, c);
            return counts;
        }

        // Each watched container's change since the last sample: withdrawals up to the trusted amount, and deposits
        // waiting for a watcher's matching loss. A container that is gone (an emptied loot bag) gave out what it held.
        private void UpdateBoxes()
        {
            foreach (BoxWatch b in boxes)
            {
                b.Out.Clear();
                b.In.Clear();
                Dictionary<string, int> nowCounts = null;
                if (!b.Gone && b.Box != null)
                {
                    try { nowCounts = CountsOf(b.Box); }
                    catch (Exception) { nowCounts = null; }
                }
                if (nowCounts == null) { b.Gone = true; nowCounts = new Dictionary<string, int>(); }
                foreach (KeyValuePair<string, int> kv in b.Last)
                {
                    int left;
                    nowCounts.TryGetValue(kv.Key, out left);
                    if (left >= kv.Value) continue;
                    int taken = kv.Value - left;
                    int trusted;
                    b.Trusted.TryGetValue(kv.Key, out trusted);
                    int ok = Math.Min(taken, trusted);
                    if (ok > 0) { b.Out[kv.Key] = ok; b.Trusted[kv.Key] = trusted - ok; }
                }
                foreach (KeyValuePair<string, int> kv in nowCounts)
                {
                    int before;
                    b.Last.TryGetValue(kv.Key, out before);
                    if (kv.Value > before) b.In[kv.Key] = kv.Value - before;
                }
                b.Last = nowCounts;
            }
        }

        // Called after every player is sampled, so a gone container's last withdrawals were offered first.
        private void ExpireBoxes(double now)
        {
            for (int i = boxes.Count - 1; i >= 0; i--)
            {
                BoxWatch b = boxes[i];
                List<ulong> done = new List<ulong>();
                foreach (KeyValuePair<ulong, double> w in b.Watchers) if (now > w.Value) done.Add(w.Key);
                foreach (ulong id in done) b.Watchers.Remove(id);
                if (b.Gone || b.Watchers.Count == 0) boxes.RemoveAt(i);
            }
        }

        private int MatchDeposit(ulong player, string name, int loss, double now)
        {
            int matched = 0;
            foreach (BoxWatch b in boxes)
            {
                if (loss <= 0) break;
                if (!Watching(b, player, now)) continue;
                int dep;
                if (!b.In.TryGetValue(name, out dep) || dep <= 0) continue;
                int m = Math.Min(dep, loss);
                b.In[name] = dep - m;
                int t;
                b.Trusted.TryGetValue(name, out t);
                b.Trusted[name] = t + m;
                loss -= m;
                matched += m;
            }
            return matched;
        }

        private int TakeWithdrawal(ulong player, string name, int gain, double now)
        {
            int taken = 0;
            foreach (BoxWatch b in boxes)
            {
                if (gain <= 0) break;
                if (!Watching(b, player, now)) continue;
                int avail;
                if (!b.Out.TryGetValue(name, out avail) || avail <= 0) continue;
                int m = Math.Min(avail, gain);
                b.Out[name] = avail - m;
                gain -= m;
                taken += m;
            }
            return taken;
        }

        private static bool Watching(BoxWatch b, ulong player, double now)
        {
            double until;
            return b.Watchers.TryGetValue(player, out until) && now <= until + 1;
        }

        // RB 1 [OPJ L1036]. Opening anything with a Container starts watching it; a frozen player cannot interact.
        private object OnPlayerInteract(InteractEvent evt)
        {
            if (data == null || evt == null) return null;
            try
            {
                Player p = null;
                if (evt.ControllerEntity != null && evt.ControllerEntity.IsPlayer) p = evt.ControllerEntity.Owner;
                if (p == null) p = evt.Sender;
                if (p == null || p.IsServer) return null;
                double now = NowSec();
                if (!evt.Cancelled && IsFrozenId(p.Id, now))
                {
                    evt.Cancel("Held by the Sentinel");
                    NotifyFrozen(p, now);
                    return true;
                }
                if (evt.Cancelled || evt.Entity == null || !config.Items.Enabled) return null;
                Container box = evt.Entity.TryGet<Container>();
                if (box == null) return null;
                BoxWatch watch = null;
                foreach (BoxWatch b in boxes) if (b.Box == box && !b.Gone) { watch = b; break; }
                if (watch == null)
                {
                    watch = new BoxWatch();
                    watch.Box = box;
                    watch.Last = CountsOf(box);
                    watch.Trusted = new Dictionary<string, int>(watch.Last);
                    boxes.Add(watch);
                    if (boxes.Count > MaxBoxes) boxes.RemoveAt(0);
                }
                watch.Watchers[p.Id] = now + config.Items.ContainerTrackSeconds;
                return null;
            }
            catch (Exception ex)
            {
                PrintError("Interact check failed: " + ex.Message);
                return null;
            }
        }

        // RB 1 [OPJ L1062]. A frozen player cannot start crafting.
        private object OnItemCraft(ItemCrafterStartEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled) return null;
            Player p = CrafterOwner(evt);
            if (p == null || !IsFrozenId(p.Id, NowSec())) return null;
            evt.Cancel("Held by the Sentinel");
            NotifyFrozen(p, NowSec());
            return true;
        }

        // RB 1 [OPJ L1088]. Crafting explains a gain of its product, and the rate is watched.
        private object OnItemCrafted(ItemCrafterFinishEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled) return null;
            try
            {
                Player p = CrafterOwner(evt);
                if (p == null || IsExempt(p) || !config.General.Enabled || !config.Items.Enabled) return null;
                double now = NowSec();
                ItemState st = ItemsOf(p.Id);
                string product = null;
                if (evt.Crafter != null && evt.Crafter.Product != null) product = evt.Crafter.Product.Name;
                if (!string.IsNullOrEmpty(product))
                {
                    st.Crafted[product] = now;
                    if (st.Crafted.Count > 100) st.Crafted.Clear();
                }
                st.Crafts.Add(now);
                st.Crafts.RemoveAll(delegate(double t) { return now - t > 60; });
                if (st.Crafts.Count > config.Items.MaxCraftsPerMinute)
                {
                    Detect(p, KCraft, (double)st.Crafts.Count / config.Items.MaxCraftsPerMinute, st.Crafts.Count + " crafts finished in 1 min (limit "
                        + config.Items.MaxCraftsPerMinute + ")" + (product != null ? "; last: " + Clean(product, 40) : ""));
                    st.Crafts.Clear();
                }
                else
                {
                    PlayerRec rec = Rec(p.Id);
                    if (rec == null || CurrentScore(rec, now) < config.Responses.AlertScore) NotePeak("crafts_per_minute", st.Crafts.Count, p);
                }
                return null;
            }
            catch (Exception ex)
            {
                PrintError("Craft check failed: " + ex.Message);
                return null;
            }
        }

        // The crafter on a player's own body belongs to them; a station's craft is credited to the client that sent it.
        private static Player CrafterOwner(ItemCrafterEvent evt)
        {
            if (evt.Entity != null && evt.Entity.IsPlayer && evt.Entity.Owner != null && !evt.Entity.Owner.IsServer) return evt.Entity.Owner;
            if (evt.Sender != null && !evt.Sender.IsServer) return evt.Sender;
            return null;
        }

        #endregion

        #region Chat and commands

        private class FloodState
        {
            public List<double> Chat = new List<double>();
            public List<double> Commands = new List<double>();
            public List<double> Refused = new List<double>();
        }

        private class PendingCommand
        {
            public string Command = "";
            public string[] Args = new string[0];
            public double At;
        }

        private FloodState FloodOf(ulong id)
        {
            FloodState st;
            if (!floods.TryGetValue(id, out st)) { st = new FloodState(); floods[id] = st; }
            return st;
        }

        // Core-dispatched [SRC]. Scored only: RealmWarden owns chat limits and mutes.
        private object OnPlayerChat(PlayerMessageEvent evt)
        {
            if (data == null || evt == null || !config.General.Enabled || !config.Flood.Enabled) return null;
            try
            {
                Player p = evt.Player;
                if (p == null || p.IsServer || IsExempt(p)) return null;
                double now = NowSec();
                FloodState st = FloodOf(p.Id);
                int window = config.Flood.ChatWindowSeconds;
                st.Chat.Add(now);
                st.Chat.RemoveAll(delegate(double t) { return now - t > window; });
                if (st.Chat.Count > config.Flood.ChatMaxPerWindow)
                {
                    Detect(p, KChat, (double)st.Chat.Count / config.Flood.ChatMaxPerWindow, st.Chat.Count + " chat messages in " + window + " s (limit "
                        + config.Flood.ChatMaxPerWindow + "); last: \"" + Clean(evt.Message, 60) + "\"");
                    st.Chat.Clear();
                }
                else
                {
                    PlayerRec rec = Rec(p.Id);
                    if (rec == null || CurrentScore(rec, now) < config.Responses.AlertScore) NotePeak("chat_per_window", st.Chat.Count, p);
                }
            }
            catch (Exception ex)
            {
                PrintError("Chat check failed: " + ex.Message);
            }
            return null;
        }

        // Core-dispatched [SRC], first for every command. A console command has no OnPlayerCommand after it, so it is
        // handled on the next tick unless OnPlayerCommand claims it first.
        private object OnServerCommand(string command, string[] args)
        {
            if (data == null || command == null) return null;
            string cmd = command.Trim().TrimStart('/').ToLowerInvariant();
            if (!IsTeleportCommand(cmd) && !IsGiveCommand(cmd)) return null;
            PendingCommand pc = new PendingCommand();
            pc.Command = cmd;
            pc.Args = args ?? new string[0];
            pc.At = NowSec();
            pendingConsole = pc;
            NextTick(delegate()
            {
                if (pendingConsole != pc) return;
                pendingConsole = null;
                TrustedCommand(null, pc.Command, pc.Args);
            });
            return null;
        }

        // Core-dispatched [SRC]. Grace for trusted teleports and gives, item sources, and the command rate limit.
        private object OnPlayerCommand(Player player, string command, string[] args)
        {
            if (data == null || player == null || command == null) return null;
            try
            {
                string cmd = command.Trim().TrimStart('/').ToLowerInvariant();
                if (args == null) args = new string[0];
                if (pendingConsole != null && pendingConsole.Command == cmd) pendingConsole = null;   // it was this player's
                if (player.IsServer) { TrustedCommand(null, cmd, args); return null; }
                if (IsTeleportCommand(cmd) && CanUseGameCommand(player, "rok.command.teleport")) TrustedCommand(player, cmd, args);
                if (IsGiveCommand(cmd) && CanUseGameCommand(player, GameGivePermission)) TrustedCommand(player, cmd, args);
                if (config.Items.ItemSourceCommands.Contains(cmd)) ItemSource(player.Id, config.Items.SourceGraceSeconds);

                if (!config.General.Enabled || !config.Flood.Enabled || IsExempt(player)) return null;
                double now = NowSec();
                FloodState st = FloodOf(player.Id);
                FloodSettings c = config.Flood;
                st.Commands.RemoveAll(delegate(double t) { return now - t > c.CommandWindowSeconds; });
                if (st.Commands.Count < c.CommandMaxPerWindow)
                {
                    st.Commands.Add(now);
                    PlayerRec rec = Rec(player.Id);
                    if (rec == null || CurrentScore(rec, now) < config.Responses.AlertScore) NotePeak("commands_per_window", st.Commands.Count, player);
                    return null;
                }
                st.Refused.Add(now);
                st.Refused.RemoveAll(delegate(double t) { return now - t > 60; });
                if (st.Refused.Count >= c.RefusedCommandsToFlag)
                {
                    Detect(player, KCommand, (double)st.Refused.Count / c.RefusedCommandsToFlag, st.Refused.Count + " commands over the limit in 1 min ("
                        + c.CommandMaxPerWindow + " per " + c.CommandWindowSeconds + " s); last: /" + Clean(cmd, 30));
                    st.Refused.Clear();
                }
                if (!c.BlockExcessCommands) return null;
                ReplyThrottled(player, 3, "CommandSlow", c.CommandMaxPerWindow, c.CommandWindowSeconds);
                return true;
            }
            catch (Exception ex)
            {
                PrintError("Command check failed: " + ex.Message);
                return null;
            }
        }

        private bool IsTeleportCommand(string cmd)
        {
            return config.Movement.TeleportCommands.Contains(cmd);
        }

        private bool IsGiveCommand(string cmd)
        {
            return config.Items.GiveCommands.Contains(cmd);
        }

        private bool CanUseGameCommand(Player p, string gamePermission)
        {
            if (IsAdmin(p)) return true;
            try { return p.HasPermission(gamePermission); }
            catch (Exception) { return false; }
        }

        // A trusted teleport or give: grace for the sender (if a player) and for every online player named in the
        // arguments. /tpdelay brings the player back after its time argument, so that grace lasts until then.
        private void TrustedCommand(Player sender, string cmd, string[] args)
        {
            bool tp = IsTeleportCommand(cmd);
            double seconds = tp ? config.Movement.GraceAfterTeleportSeconds : config.Items.SourceGraceSeconds;
            if (tp && cmd == "tpdelay" && args.Length >= 3)
            {
                double back;
                if (double.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out back))
                    seconds = Math.Min(600, Math.Max(0, back) + config.Movement.GraceAfterTeleportSeconds + 5);
            }
            List<ulong> ids = new List<ulong>();
            if (sender != null) ids.Add(sender.Id);
            foreach (string a in args)
            {
                if (string.IsNullOrEmpty(a)) continue;
                foreach (Player p in Server.ClientPlayers)
                {
                    if (p == null || p.IsServer || p.Name == null) continue;
                    if (string.Equals(p.Name, a, StringComparison.OrdinalIgnoreCase) || p.Id.ToString() == a)
                        if (!ids.Contains(p.Id)) ids.Add(p.Id);
                }
            }
            foreach (ulong id in ids)
            {
                if (tp) Grace(id, seconds);
                else ItemSource(id, seconds);
            }
        }

        #endregion

        #region Connections

        private void TrackConnect(Player p, double now)
        {
            if (!config.Connections.Enabled) return;
            string id = p.Id.ToString();
            List<double> list;
            if (!connects.TryGetValue(id, out list)) { list = new List<double>(); connects[id] = list; }
            double win = config.Connections.WindowMinutes * 60.0;
            list.Add(now);
            list.RemoveAll(delegate(double t) { return now - t > win; });
            if (connects.Count > 5000) PruneConnects(now);
            int over = list.Count - config.Connections.MaxConnects;
            if (over > 0 && (over - 1) % config.Connections.MaxConnects == 0)    // once per batch of extra connects
                Detect(p, KReconnect, (double)list.Count / config.Connections.MaxConnects, list.Count + " connects in " + config.Connections.WindowMinutes
                    + " min (limit " + config.Connections.MaxConnects + ")");
            else if (over <= 0)
            {
                PlayerRec rec = Rec(p.Id);
                if (rec == null || CurrentScore(rec, now) < config.Responses.AlertScore) NotePeak("connects_per_window", list.Count, p);
            }
        }

        private void PruneConnects(double now)
        {
            double win = config.Connections.WindowMinutes * 60.0;
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, List<double>> kv in connects)
                if (kv.Value.Count == 0 || now - kv.Value[kv.Value.Count - 1] > win) stale.Add(kv.Key);
            foreach (string k in stale) connects.Remove(k);
        }

        // Core-dispatched [SRC ReignOfKingsHooks.cs:25-55]: a string refuses the login. Opt-in, enforce mode only.
        private object CanUserLogin(string name, string id, string ip)
        {
            if (data == null || id == null || !config.General.Enabled || !config.Connections.Enabled) return null;
            if (!config.Connections.RefuseWhileCycling || !Enforcing) return null;
            if (config.General.ExemptIds.Contains(id) || permission.UserHasPermission(id, PermAdmin)) return null;
            List<double> list;
            if (!connects.TryGetValue(id, out list) || list.Count < config.Connections.MaxConnects) return null;
            double now = NowSec();
            double last = list[list.Count - 1];
            double wait = config.Connections.RefuseSeconds - (now - last);
            if (wait <= 0) return null;
            Puts("Refused a login from " + Clean(name, 40) + " (" + id + "): reconnect cycling, " + Dur(wait) + " left.");
            return string.Format(lang.GetMessage("ReconnectRefused", this, id), Dur(wait));
        }

        #endregion

        #region Names

        // Staff are remembered when they join (Oxide permission or group), and listed in Names.StaffNames.
        private void LearnStaff(Player p)
        {
            if (p == null || p.IsServer || !config.Names.LearnStaff || !IsStaff(p)) return;
            string id = p.Id.ToString();
            StaffRec s;
            if (!data.Staff.TryGetValue(id, out s)) { s = new StaffRec(); data.Staff[id] = s; }
            s.Name = Clean(p.Name, 64);
            s.LastSeen = (long)NowSec();
            dirty = true;
            if (data.Staff.Count > 200) PruneStaff();
        }

        private void PruneStaff()
        {
            List<KeyValuePair<string, StaffRec>> all = new List<KeyValuePair<string, StaffRec>>(data.Staff);
            all.Sort(delegate(KeyValuePair<string, StaffRec> a, KeyValuePair<string, StaffRec> b) { return a.Value.LastSeen.CompareTo(b.Value.LastSeen); });
            for (int i = 0; i < all.Count - 200; i++) data.Staff.Remove(all[i].Key);
        }

        private bool IsStaff(Player p)
        {
            string id = p.Id.ToString();
            foreach (string perm in config.Names.StaffPermissions)
                if (!string.IsNullOrEmpty(perm) && permission.UserHasPermission(id, perm)) return true;
            foreach (string g in config.Names.StaffGroups)
                if (!string.IsNullOrEmpty(g) && permission.UserHasGroup(id, g)) return true;
            return false;
        }

        private void CheckImpersonation(Player p)
        {
            if (!config.Names.Enabled || IsStaff(p)) return;
            string mine = FoldName(p.Name);
            if (mine.Length < 3) return;
            string id = p.Id.ToString();
            string exact = null, near = null;
            foreach (KeyValuePair<string, StaffRec> kv in data.Staff)
            {
                if (kv.Key == id) continue;
                Compare(mine, kv.Value.Name, ref exact, ref near);
            }
            foreach (string listed in config.Names.StaffNames) Compare(mine, listed, ref exact, ref near);
            if (exact == null && near == null) return;
            string shown = Clean(p.Name, 64);
            if (exact != null)
            {
                Detect(p, KImpersonation, 1, "name '" + shown + "' reads as staff name '" + exact + "'");
                Reply(p, "ImpersonationWarn", shown);
                if (config.Names.KickExactMatch && Enforcing) KickLater(p, Msg("ImpersonationKick", p), config.Names.KickDelaySeconds, "impersonation");
            }
            else Detect(p, KImpersonationNear, 1, "name '" + shown + "' is one letter away from staff name '" + near + "'");
            // A name is a standing problem, not a burst: always tell the admins, whatever the score.
            PlayerRec rec = Rec(p.Id) ?? Touch(p);
            AddAlert(exact != null ? KImpersonation : KImpersonationNear, p.Id.ToString(), shown, CurrentScore(rec, NowSec()), "alert",
                exact != null ? "name reads as staff name '" + exact + "'" : "name is one letter away from staff name '" + near + "'");
        }

        private void Compare(string mine, string staffName, ref string exact, ref string near)
        {
            if (string.IsNullOrEmpty(staffName)) return;
            string theirs = FoldName(staffName);
            if (theirs.Length < 3) return;
            if (mine == theirs) { if (exact == null) exact = Clean(staffName, 40); return; }
            if (near == null && theirs.Length >= config.Names.NearMatchMinLength && mine.Length >= config.Names.NearMatchMinLength
                && (EditDistanceAtMostOne(mine, theirs) || mine.Contains(theirs)))
                near = Clean(staffName, 40);
        }

        // Lower case; look-alike Cyrillic and Greek letters to Latin; leet digits to letters; invisible characters,
        // colour tags, punctuation and spaces removed. "[FF0000]Ѕіr_ Аld.r1c" -> "siraldric".
        private static string FoldName(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '[' && i + 7 < s.Length && s[i + 7] == ']' && IsChatHex(s.Substring(i + 1, 6))) { i += 8; continue; }
                char ch = char.ToLowerInvariant(s[i]);
                i++;
                ch = LookAlike(ch);
                ch = LeetChar(ch);
                if (ch >= 'a' && ch <= 'z') sb.Append(ch);
            }
            return sb.ToString();
        }

        private static char LookAlike(char ch)
        {
            switch (ch)
            {
                case 'а': return 'a';   // Cyrillic a
                case 'е': return 'e';
                case 'ё': return 'e';
                case 'о': return 'o';
                case 'р': return 'p';
                case 'с': return 'c';
                case 'у': return 'y';
                case 'х': return 'x';
                case 'і': return 'i';
                case 'ї': return 'i';
                case 'ј': return 'j';
                case 'ѕ': return 's';
                case 'к': return 'k';
                case 'м': return 'm';
                case 'н': return 'h';
                case 'т': return 't';
                case 'в': return 'b';
                case 'һ': return 'h';
                case 'ԁ': return 'd';
                case 'α': return 'a';   // Greek alpha
                case 'ε': return 'e';
                case 'ο': return 'o';
                case 'ρ': return 'p';
                case 'κ': return 'k';
                case 'ν': return 'v';
                case 'ι': return 'i';
                case 'τ': return 't';
                case 'υ': return 'u';
                case 'χ': return 'x';
                case 'ı': return 'i';   // dotless i
                case 'à': case 'á': case 'â': case 'ã': case 'ä': case 'å': return 'a';
                case 'è': case 'é': case 'ê': case 'ë': return 'e';
                case 'ì': case 'í': case 'î': case 'ï': return 'i';
                case 'ò': case 'ó': case 'ô': case 'õ': case 'ö': return 'o';
                case 'ù': case 'ú': case 'û': case 'ü': return 'u';
                case 'ý': case 'ÿ': return 'y';
                case 'ñ': return 'n';
                case 'ç': return 'c';
            }
            if (ch >= 'ａ' && ch <= 'ｚ') return (char)('a' + (ch - 'ａ'));   // full-width letters
            return ch;
        }

        private static char LeetChar(char ch)
        {
            switch (ch)
            {
                case '0': return 'o';
                case '1': return 'i';
                case '!': return 'i';
                case '|': return 'l';
                case '3': return 'e';
                case '4': return 'a';
                case '@': return 'a';
                case '5': return 's';
                case '$': return 's';
                case '7': return 't';
                case '8': return 'b';
                case '9': return 'g';
            }
            return ch;
        }

        private static bool EditDistanceAtMostOne(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 1) return false;
            int i = 0, j = 0, edits = 0;
            while (i < a.Length && j < b.Length)
            {
                if (a[i] == b[j]) { i++; j++; continue; }
                if (++edits > 1) return false;
                if (a.Length > b.Length) i++;
                else if (b.Length > a.Length) j++;
                else if (i + 1 < a.Length && a[i] == b[j + 1] && a[i + 1] == b[j]) { i += 2; j += 2; }   // swapped pair
                else { i++; j++; }
            }
            edits += (a.Length - i) + (b.Length - j);
            return edits <= 1;
        }

        #endregion

        #region Score and responses

        private double CurrentScore(PlayerRec r, double now)
        {
            if (r == null || r.Score <= 0) return 0;
            double age = now - r.ScoreAt;
            if (age <= 0) return r.Score;
            return r.Score * Math.Pow(0.5, age / (config.Score.HalfLifeMinutes * 60.0));
        }

        private float Weight(string kind)
        {
            float w;
            return config.Score.Weights.TryGetValue(kind, out w) ? w : 0f;
        }

        // One detection: counted always; scored and written as evidence at most once per kind per KindCooldownSeconds.
        private void Detect(Player p, string kind, double severity, string detail)
        {
            if (p == null || p.IsServer || data == null || IsExempt(p)) return;
            double now = NowSec();
            PlayerRec r = Rec(p.Id) ?? Touch(p);
            long last;
            if (r.LastKindAt.TryGetValue(kind, out last) && now - last < config.Score.KindCooldownSeconds)
            {
                int s;
                r.Suppressed.TryGetValue(kind, out s);
                r.Suppressed[kind] = s + 1;
                dirty = true;
                return;
            }
            r.LastKindAt[kind] = (long)now;
            if (double.IsNaN(severity) || severity < 1) severity = 1;
            if (severity > 3) severity = 3;
            double points = Math.Round(Weight(kind) * severity, 1);
            double score = CurrentScore(r, now) + points;
            r.Score = score;
            r.ScoreAt = (long)now;
            if (score > r.PeakScore) r.PeakScore = score;
            int n;
            r.Counts.TryGetValue(kind, out n);
            r.Counts[kind] = n + 1;
            dirty = true;
            feedDirty = true;
            AddEvidence(kind, p, points, score, detail);
            Respond(p, r, kind, detail, now);
        }

        // Response levels: 1 alert, 2 freeze, 3 kick, 4 ban. Watch mode says what enforce mode would do. An alert goes out
        // when an action is taken, when the level rises, or after Responses.AlertCooldownMinutes; never a step down.
        private void Respond(Player p, PlayerRec r, string kind, string detail, double now)
        {
            ResponseSettings c = config.Responses;
            if (r.Banned) return;
            double score = CurrentScore(r, now);
            int level = 0;
            if (c.Alert && score >= c.AlertScore) level = 1;
            if (c.Freeze && score >= c.FreezeScore) level = 2;
            if (c.Kick && score >= c.KickScore) level = 3;
            if (score >= c.BanScore) level = 4;
            if (level == 0) return;
            string name = Clean(p.Name, 64);
            bool cooled = r.LastAlertAt == 0 || now - r.LastAlertAt >= c.AlertCooldownMinutes * 60.0;
            bool rising = level > r.LastLevel;
            string response = null;

            if (Enforcing)
            {
                if (level == 4 && c.AutoBan)
                {
                    if (BanPlayer(p.Id, name, "automatic: score " + F1(score) + ", last " + kind, null)) KickLater(p, Msg("KickReason", p), 1, "banned");
                    return;
                }
                bool kickCooled = r.LastKickAt == 0 || now - r.LastKickAt >= c.KickCooldownMinutes * 60.0;
                if (level >= 3 && c.Kick && kickCooled)
                {
                    r.LastKickAt = (long)now;
                    r.Kicks++;
                    response = "kicked";
                    // Held on return, so a kick is not a free reset: the freeze is saved with the record.
                    if (c.Freeze && !IsFrozenId(p.Id, now))
                    {
                        FreezePlayer(p, r, c.FreezeMinutes * 60.0, "kicked at score " + F1(score) + " after " + kind, "Sentinel");
                        response = "kicked, frozen " + c.FreezeMinutes + " min on return";
                    }
                    KickLater(p, Msg("KickReason", p), 1, kind);
                }
                else if (level >= 2 && c.Freeze && !IsFrozenId(p.Id, now))
                {
                    FreezePlayer(p, r, c.FreezeMinutes * 60.0, "score " + F1(score) + " after " + kind, "Sentinel");
                    response = "frozen " + c.FreezeMinutes + " min";
                }
                if (level == 4 && (rising || cooled)) response = response == null ? "ban recommended" : response + "; ban recommended";
                if (response == null && (rising || cooled)) response = "alert";
            }
            else if (rising || cooled)
            {
                response = level == 4 ? "ban recommended" : level == 3 ? "would kick" : level == 2 ? "would freeze" : "alert";
            }
            if (response == null) return;
            r.LastAlertAt = (long)now;
            r.LastLevel = level;
            r.LastResponse = response;
            r.LastResponseAt = (long)now;
            dirty = true;
            AddAlert(kind, p.Id.ToString(), name, score, response, detail);
        }

        private int HighScoreCount(double now)
        {
            int n = 0;
            foreach (PlayerRec r in data.Players.Values) if (CurrentScore(r, now) >= config.Responses.AlertScore) n++;
            return n;
        }

        private void KickLater(Player p, string reason, int delaySeconds, string why)
        {
            ulong id = p.Id;
            if (shuttingDown || kicking.ContainsKey(id)) return;
            kicking[id] = true;
            timer.Once(delaySeconds, delegate()
            {
                Player again = Server.GetPlayerById(id);
                if (again != null && !again.IsServer)
                {
                    try { Server.Kick(again, reason); }
                    catch (Exception ex) { PrintError("Kick failed for " + id + ": " + ex.Message); }
                }
                kicking.Remove(id);
            });
            Puts("Kicking " + Clean(p.Name, 40) + " (" + id + "): " + why);
        }

        private bool BanPlayer(ulong id, string name, string reason, Player admin)
        {
            bool ok = false;
            try { ok = Server.Ban(id, name, "Realm Sentinel: " + reason); }
            catch (Exception ex) { PrintError("Ban failed for " + id + ": " + ex.Message); }
            PlayerRec r = Rec(id);
            if (r != null)
            {
                if (ok) r.Banned = true;
                r.LastResponse = ok ? "banned" : "ban failed";
                r.LastResponseAt = (long)NowSec();
                dirty = true;
            }
            AddAlert(KResponse, id.ToString(), name, r != null ? CurrentScore(r, NowSec()) : 0, ok ? "banned" : "ban failed",
                (admin != null ? "by " + Clean(admin.Name, 40) + ": " : "") + reason);
            return ok;
        }

        #endregion

        #region Freeze

        private bool IsFrozenId(ulong id, double now)
        {
            PlayerRec r = Rec(id);
            return r != null && r.FrozenUntil > now;
        }

        private void FreezePlayer(Player p, PlayerRec r, double seconds, string reason, string by)
        {
            double now = NowSec();
            r.FrozenUntil = (long)(now + seconds);
            r.FreezeReason = Clean(reason, 120);
            r.FreezeBy = Clean(by, 40);
            Vector3 pos;
            if (TryPosition(p, out pos))
            {
                r.HasFreezePos = true;
                r.FreezeX = pos.x; r.FreezeY = pos.y; r.FreezeZ = pos.z;
            }
            else r.HasFreezePos = false;
            r.FreezeNotified = true;
            dirty = true;
            feedDirty = true;
            ReplyError(p, "FrozenNotice", Dur(seconds));
        }

        private void Unfreeze(PlayerRec r)
        {
            r.FrozenUntil = 0;
            r.HasFreezePos = false;
            r.FreezeNotified = false;
            dirty = true;
            feedDirty = true;
        }

        private void NotifyFrozen(Player p, double now)
        {
            PlayerRec r = Rec(p.Id);
            if (r == null) return;
            ReplyThrottled(p, 10, "FrozenBlocked", Dur(r.FrozenUntil - now));
        }

        // A frozen player cannot break or place blocks. RB 0 hooks: Damage.Amount = 0 is what the game reads [IL].
        private void OnCubeTakeDamage(CubeDamageEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled || evt.Damage == null) return;
            Player p = null;
            if (evt.Damage.DamageSource != null && evt.Damage.DamageSource.Owner != null) p = evt.Damage.DamageSource.Owner;
            else if (evt.Sender != null) p = evt.Sender;
            if (p == null || p.IsServer || evt.Damage.Amount <= 0f || !IsFrozenId(p.Id, NowSec())) return;
            evt.Damage.Amount = 0f;
            evt.Cancel("Held by the Sentinel");
            NotifyFrozen(p, NowSec());
        }

        private void OnCubePlacement(CubePlaceEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled) return;
            Player p = evt.Sender;
            if (p == null || p.IsServer || !IsFrozenId(p.Id, NowSec())) return;
            evt.Cancel("Held by the Sentinel");
            NotifyFrozen(p, NowSec());
        }

        private object OnThroneCapture(AncientThroneCaptureEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled || evt.Player == null) return null;
            if (!IsFrozenId(evt.Player.Id, NowSec())) return null;
            evt.Cancel("Held by the Sentinel");
            NotifyFrozen(evt.Player, NowSec());
            return true;
        }

        private object OnPlayerCapture(PlayerCaptureEvent evt)
        {
            if (data == null || evt == null || evt.Cancelled || evt.Captor == null || !evt.Captor.IsPlayer) return null;
            Player captor = evt.Captor.Owner;
            if (captor == null || captor.IsServer || !IsFrozenId(captor.Id, NowSec())) return null;
            evt.Cancel("Held by the Sentinel");
            NotifyFrozen(captor, NowSec());
            return true;
        }

        #endregion

        #region Alerts, evidence, peaks and the Steward feed

        private void AddEvidence(string kind, Player p, double points, double scoreAfter, string detail)
        {
            Evidence e = new Evidence();
            e.Id = data.NextEvidenceId++;
            e.Ts = (long)NowSec();
            e.Kind = kind;
            e.PlayerId = p.Id.ToString();
            e.PlayerName = Clean(p.Name, 64);
            e.Points = points;
            e.ScoreAfter = Math.Round(scoreAfter, 1);
            Vector3 pos;
            if (TryPosition(p, out pos)) e.Pos = Pos(pos);
            e.PingMs = PingMs(p);
            e.Detail = Clean(detail, 300);
            data.Evidence.Add(e);
            if (data.Evidence.Count > config.Alerts.MaxEvidence) data.Evidence.RemoveRange(0, data.Evidence.Count - config.Alerts.MaxEvidence);
            dirty = true;
            WriteLog("evidence", "#" + e.Id + " " + kind + " " + e.PlayerName + " (" + e.PlayerId + ") +" + F1(points) + " -> " + F1(scoreAfter)
                + (e.Pos.Length > 0 ? " at " + e.Pos : "") + " ping " + e.PingMs + " ms: " + e.Detail);
        }

        private void AddAdminEvidence(Player admin, string targetId, string targetName, string what)
        {
            Evidence e = new Evidence();
            e.Id = data.NextEvidenceId++;
            e.Ts = (long)NowSec();
            e.Kind = KAdmin;
            e.PlayerId = targetId;
            e.PlayerName = Clean(targetName, 64);
            e.Detail = Clean("by " + (admin != null ? admin.Name : "console") + ": " + what, 300);
            data.Evidence.Add(e);
            if (data.Evidence.Count > config.Alerts.MaxEvidence) data.Evidence.RemoveRange(0, data.Evidence.Count - config.Alerts.MaxEvidence);
            dirty = true;
            WriteLog("evidence", "#" + e.Id + " " + KAdmin + " " + e.PlayerName + " (" + targetId + "): " + e.Detail);
        }

        private void AddAlert(string kind, string playerId, string playerName, double score, string response, string detail)
        {
            Alert a = new Alert();
            a.Id = data.NextAlertId++;
            a.Ts = (long)NowSec();
            a.Kind = kind;
            a.PlayerId = playerId ?? "";
            a.PlayerName = Clean(playerName, 64);
            a.Score = Math.Round(score, 1);
            a.Response = Clean(response, 40);
            a.Detail = Clean(detail, 300);
            data.Alerts.Add(a);
            if (data.Alerts.Count > config.Alerts.MaxStored) data.Alerts.RemoveRange(0, data.Alerts.Count - config.Alerts.MaxStored);
            dirty = true;
            feedDirty = true;
            string line = "#" + a.Id + " " + kind + " " + a.PlayerName + " (" + a.PlayerId + ") score " + F1(a.Score) + " [" + a.Response + "]: " + a.Detail;
            Puts("ALERT " + line);
            WriteLog("alerts", line);
            ChatAdmins(a);
            ForwardToWarden(a);
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
                string detail = a.Response.Length > 0 && a.Response != "alert" ? "[" + a.Response + "] " + a.Detail : a.Detail;
                if (detail.Length > 120) detail = detail.Substring(0, 117) + "...";
                Reply(p, "AlertChat", a.Id, a.Kind, a.PlayerName, F1(a.Score), detail);
                if (suppressedAdminChat > 0) ReplyError(p, "AlertOverflow", suppressedAdminChat);
            }
            suppressedAdminChat = 0;
        }

        private void ForwardToWarden(Alert a)
        {
            if (!config.Alerts.ForwardToWarden || RealmWarden == null) return;
            ulong id;
            if (!ulong.TryParse(a.PlayerId, out id)) id = 0;
            try
            {
                RealmWarden.Call("RaiseWardenAlert", "sentinel " + a.Kind, id, "score " + F1(a.Score) + " [" + a.Response + "] " + a.Detail);
            }
            catch (Exception ex)
            {
                if (!Throttled("wardenfail", 600)) PrintWarning("Could not forward to RealmWarden: " + ex.Message);
            }
        }

        private void WriteLog(string file, string line)
        {
            if (!config.Alerts.LogToFile) return;
            try { LogToFile(file, Epoch.AddSeconds(NowSec()).ToString("yyyy-MM-ddTHH:mm:ssZ") + " " + line, this, true, false); }
            catch (Exception ex) { if (!Throttled("logfail|" + file, 600)) PrintWarning("Could not write the " + file + " log: " + ex.Message); }
        }

        // Highest values seen from players under the alert score: what honest play looks like on this server.
        private void NotePeak(string metric, double value, Player p)
        {
            if (data == null || double.IsNaN(value) || double.IsInfinity(value)) return;
            Peak pk;
            if (data.Peaks.TryGetValue(metric, out pk) && pk.Value >= value) return;
            if (pk == null) { pk = new Peak(); data.Peaks[metric] = pk; }
            pk.Value = Math.Round(value, 2);
            pk.PlayerName = p != null ? Clean(p.Name, 40) : "";
            pk.At = (long)NowSec();
            dirty = true;
        }

        private void WriteFeed()
        {
            if (data == null || !config.Alerts.WriteFeed) return;
            feedDirty = false;
            try
            {
                double now = NowSec();
                Feed f = new Feed();
                f.Generated = Iso(now);
                f.Mode = config.Responses.Mode;
                f.DataDamaged = loadFailed;
                foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) f.Online++;
                for (int i = data.Alerts.Count - 1; i >= 0 && f.Alerts.Count < config.Alerts.FeedAlerts; i--)
                {
                    Alert a = data.Alerts[i];
                    FeedAlert fa = new FeedAlert();
                    fa.Id = a.Id; fa.Time = Iso(a.Ts); fa.Kind = a.Kind; fa.PlayerId = a.PlayerId; fa.PlayerName = a.PlayerName;
                    fa.Score = a.Score; fa.Response = a.Response; fa.Detail = a.Detail;
                    f.Alerts.Add(fa);
                }
                List<KeyValuePair<string, PlayerRec>> ranked = Ranked(now);
                foreach (KeyValuePair<string, PlayerRec> kv in data.Players) if (kv.Value.FrozenUntil > now) f.Frozen++;
                foreach (KeyValuePair<string, PlayerRec> kv in ranked)
                {
                    if (f.Suspects.Count >= config.Alerts.FeedSuspects) break;
                    FeedSuspect s = new FeedSuspect();
                    s.PlayerId = kv.Key; s.PlayerName = kv.Value.Name; s.Score = Math.Round(CurrentScore(kv.Value, now), 1);
                    s.PeakScore = Math.Round(kv.Value.PeakScore, 1); s.Online = FindOnlineById(kv.Key) != null;
                    s.Frozen = kv.Value.FrozenUntil > now; s.LastSeen = Iso(kv.Value.LastSeen);
                    s.Counts = new Dictionary<string, int>(kv.Value.Counts);
                    f.Suspects.Add(s);
                }
                Interface.Oxide.DataFileSystem.WriteObject(FeedName, f);
            }
            catch (Exception ex)
            {
                if (!Throttled("feedfail", 600)) PrintWarning("Could not write the Steward feed: " + ex.Message);
            }
        }

        // Players with a score above 0.5 or frozen, highest first.
        private List<KeyValuePair<string, PlayerRec>> Ranked(double now)
        {
            List<KeyValuePair<string, PlayerRec>> list = new List<KeyValuePair<string, PlayerRec>>();
            foreach (KeyValuePair<string, PlayerRec> kv in data.Players)
                if (CurrentScore(kv.Value, now) >= 0.5 || kv.Value.FrozenUntil > now) list.Add(kv);
            list.Sort(delegate(KeyValuePair<string, PlayerRec> a, KeyValuePair<string, PlayerRec> b)
            {
                return CurrentScore(b.Value, now).CompareTo(CurrentScore(a.Value, now));
            });
            return list;
        }

        #endregion

        #region Commands

        [ChatCommand("sentinel")]
        private void CmdSentinel(Player player, string command, string[] args)
        {
            if (player == null || data == null) return;
            try
            {
                if (!IsAdmin(player)) { Reply(player, "PlayerInfo"); return; }
                string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
                switch (sub)
                {
                    case "status": CmdStatus(player); return;
                    case "report": CmdReport(player, args); return;
                    case "clear": CmdClear(player, args); return;
                    case "reload": CmdReload(player); return;
                    case "freeze": CmdFreeze(player, args); return;
                    case "unfreeze": CmdUnfreeze(player, args); return;
                    case "ban": CmdBan(player, args); return;
                    case "peaks": CmdPeaks(player, args); return;
                }
                ShowHelp(player);
            }
            catch (Exception ex)
            {
                PrintError("/sentinel failed: " + ex.Message);
            }
        }

        private void ShowHelp(Player p)
        {
            Reply(p, "HelpHeader", config.Responses.Mode);
            Reply(p, "Help1");
            Reply(p, "Help2");
            Reply(p, "Help3");
            Reply(p, "Help4");
            if (loadFailed) ReplyError(p, "DataDamaged");
        }

        private void CmdStatus(Player p)
        {
            double now = NowSec();
            List<string> on = new List<string>();
            if (config.Movement.Enabled) on.Add("movement");
            if (config.Combat.Enabled) on.Add("combat");
            if (config.Items.Enabled) on.Add("items");
            if (config.Flood.Enabled) on.Add("floods");
            if (config.Connections.Enabled) on.Add("reconnects");
            if (config.Names.Enabled) on.Add("names");
            if (!config.General.Enabled) on.Clear();
            int online = 0, frozen = 0;
            foreach (Player q in Server.ClientPlayers) if (q != null && !q.IsServer && !IsExempt(q)) online++;
            foreach (PlayerRec r in data.Players.Values) if (r.FrozenUntil > now) frozen++;
            Reply(p, "StatusMode", config.Responses.Mode, on.Count == 0 ? Msg("None", p) : string.Join(", ", on.ToArray()), online, frozen, data.Alerts.Count);
            ResponseSettings c = config.Responses;
            Reply(p, "StatusResponses", F1(c.AlertScore), c.Freeze ? F1(c.FreezeScore) : Msg("Off", p), c.Kick ? F1(c.KickScore) : Msg("Off", p),
                F1(c.BanScore), c.AutoBan ? "automatic" : "admin only", F1(config.Score.HalfLifeMinutes));
            List<KeyValuePair<string, PlayerRec>> ranked = Ranked(now);
            if (ranked.Count == 0) Reply(p, "StatusNone");
            else
            {
                List<string> top = new List<string>();
                for (int i = 0; i < ranked.Count && i < 5; i++)
                    top.Add(Clean(ranked[i].Value.Name, 24) + " " + F1(CurrentScore(ranked[i].Value, now)) + (ranked[i].Value.FrozenUntil > now ? " (frozen)" : ""));
                Reply(p, "StatusTop", string.Join(", ", top.ToArray()));
            }
            Reply(p, "StatusFeed", config.Alerts.WriteFeed ? Msg("On", p) : Msg("Off", p),
                config.Alerts.ForwardToWarden ? (RealmWarden != null ? Msg("On", p) : "RealmWarden not loaded") : Msg("Off", p));
            if (!Enforcing) ReplyError(p, "WatchNote");
            if (loadFailed) ReplyError(p, "DataDamaged");
        }

        private void CmdReport(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            int page = 1;
            if (args.Length > 2 && !ParseInt(p, args[2], 1, 10000, out page)) return;
            double now = NowSec();
            Reply(p, "ReportHeader", r.Name, id, F1(CurrentScore(r, now)), F1(r.PeakScore), Stamp(r.FirstSeen), Stamp(r.LastSeen));
            List<string> counts = new List<string>();
            foreach (string k in AllKinds)
            {
                int n, s;
                r.Counts.TryGetValue(k, out n);
                r.Suppressed.TryGetValue(k, out s);
                if (n > 0 || s > 0) counts.Add(k + " x" + n + (s > 0 ? " (+" + s + " more)" : ""));
            }
            Reply(p, "ReportCounts", counts.Count == 0 ? Msg("None", p) : string.Join(", ", counts.ToArray()));
            Player online = FindOnlineById(id);
            string frozen = r.FrozenUntil > now ? Dur(r.FrozenUntil - now) + " left (" + r.FreezeBy + ": " + r.FreezeReason + ")" : Msg("No", p);
            string last = r.LastResponse.Length > 0 ? r.LastResponse + " " + Stamp(r.LastResponseAt) : Msg("None", p);
            Reply(p, "ReportState", frozen, last, r.Kicks, online != null ? Msg("Yes", p) : Msg("No", p),
                online != null ? ", ping " + PingMs(online) + " ms" : "");
            List<Evidence> list = new List<Evidence>();
            for (int i = data.Evidence.Count - 1; i >= 0; i--) if (data.Evidence[i].PlayerId == id) list.Add(data.Evidence[i]);
            if (list.Count == 0) { Reply(p, "ReportNoEvidence"); return; }
            int pages = (list.Count + PageSize - 1) / PageSize;
            if (page > pages) page = pages;
            Reply(p, "ReportEvidence", page, pages);
            for (int i = (page - 1) * PageSize; i < Math.Min(list.Count, page * PageSize); i++)
            {
                Evidence e = list[i];
                string detail = e.Detail.Length > 110 ? e.Detail.Substring(0, 107) + "..." : e.Detail;
                p.SendMessage(Fmt("EvidenceLine", p, new object[] { e.Id, Stamp(e.Ts), e.Kind, F1(e.Points), F1(e.ScoreAfter), detail,
                    e.Pos.Length > 0 ? " @" + e.Pos : "" }));
            }
        }

        private void CmdClear(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            r.Score = 0;
            r.ScoreAt = (long)NowSec();
            r.Counts.Clear();
            r.Suppressed.Clear();
            r.LastKindAt.Clear();
            r.LastAlertAt = 0;
            r.LastResponse = "";
            r.LastResponseAt = 0;
            r.LastLevel = 0;
            r.Banned = false;
            Unfreeze(r);
            ulong uid;
            if (ulong.TryParse(id, out uid))
            {
                moves.Remove(uid);
                items.Remove(uid);
                fights.Remove(uid);
                floods.Remove(uid);
                connects.Remove(id);
            }
            AddAdminEvidence(p, id, name, "cleared score, counts and freeze");
            Reply(p, "ClearDone", name);
        }

        private void CmdReload(Player p)
        {
            PluginConfig fresh;
            try
            {
                fresh = Config.ReadObject<PluginConfig>();
                if (fresh == null) throw new Exception("empty");
            }
            catch (Exception ex)
            {
                ReplyError(p, "ReloadFailed", Clean(ex.Message, 80));
                return;
            }
            config = fresh;
            ClampConfig();
            moves.Clear();
            items.Clear();
            feedDirty = true;
            AddAdminEvidence(p, "", "", "reloaded the config (mode " + config.Responses.Mode + ")");
            Reply(p, "Reloaded", config.Responses.Mode);
        }

        private void CmdFreeze(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            int minutes = config.Responses.FreezeMinutes;
            if (args.Length > 2 && !ParseInt(p, args[2], 1, 1440, out minutes)) return;
            Player target = FindOnlineById(id);
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r)) { ReplyError(p, "PlayerNotFound"); return; }
            if (target != null) FreezePlayer(target, r, minutes * 60.0, "admin freeze", Clean(p.Name, 40));
            else
            {
                r.FrozenUntil = (long)(NowSec() + minutes * 60.0);
                r.FreezeReason = "admin freeze";
                r.FreezeBy = Clean(p.Name, 40);
                r.HasFreezePos = false;
                dirty = true;
                feedDirty = true;
            }
            AddAdminEvidence(p, id, name, "froze for " + minutes + " min");
            Reply(p, "FreezeSet", name, Dur(minutes * 60));
        }

        private void CmdUnfreeze(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            PlayerRec r;
            if (!data.Players.TryGetValue(id, out r) || r.FrozenUntil <= NowSec()) { ReplyError(p, "NotFrozen", name); return; }
            Unfreeze(r);
            Player target = FindOnlineById(id);
            if (target != null) Reply(target, "Released");
            AddAdminEvidence(p, id, name, "lifted the freeze");
            Reply(p, "UnfreezeSet", name);
        }

        private void CmdBan(Player p, string[] args)
        {
            if (args.Length < 2) { ShowHelp(p); return; }
            string id, name;
            if (!ResolveTarget(p, args[1], out id, out name)) return;
            ulong uid;
            if (!ulong.TryParse(id, out uid)) { ReplyError(p, "PlayerNotFound"); return; }
            if (args.Length < 3 || args[args.Length - 1].ToLowerInvariant() != "confirm") { Reply(p, "BanConfirm", name, id); return; }
            PlayerRec r = Rec(uid);
            string reason = "by an admin after review" + (r != null ? "; score " + F1(CurrentScore(r, NowSec())) + ", peak " + F1(r.PeakScore) : "");
            AddAdminEvidence(p, id, name, "banned");
            if (BanPlayer(uid, name, reason, p))
            {
                Player online = FindOnlineById(id);
                if (online != null) KickLater(online, Msg("KickReason", online), 1, "banned");
                Reply(p, "BanDone", name);
            }
            else ReplyError(p, "BanFailed", name);
        }

        private void CmdPeaks(Player p, string[] args)
        {
            if (args.Length > 1 && args[1].ToLowerInvariant() == "reset")
            {
                data.Peaks.Clear();
                data.PeaksSince = (long)NowSec();
                dirty = true;
                AddAdminEvidence(p, "", "", "reset the peaks");
                Reply(p, "PeaksReset");
                return;
            }
            if (data.Peaks.Count == 0) { Reply(p, "PeaksNone"); return; }
            Reply(p, "PeaksHeader", Stamp(data.PeaksSince));
            List<string> keys = new List<string>(data.Peaks.Keys);
            keys.Sort(string.CompareOrdinal);
            foreach (string k in keys)
            {
                Peak pk = data.Peaks[k];
                p.SendMessage(Fmt("PeakLine", p, new object[] { k, F1(pk.Value), pk.PlayerName, Stamp(pk.At) }));
            }
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
                    if (!data.Players.ContainsKey(id)) Touch(p);
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

        private bool ParseInt(Player p, string s, int lo, int hi, out int value)
        {
            if (int.TryParse(s, out value) && value >= lo && value <= hi) return true;
            ReplyError(p, "BadNumber", Clean(s, 20), lo, hi);
            return false;
        }

        #endregion

        #region Cross-plugin API (non-public on purpose: Oxide's Call only finds NonPublic|Instance methods)

        // A Realm plugin that moves a player (exile, arena) asks for movement grace first. Capped at 60 s.
        private void SentinelGrace(ulong playerId, float seconds)
        {
            if (data == null || playerId == 0) return;
            Grace(playerId, Math.Min(60.0, Math.Max(0.0, seconds)));
        }

        // A Realm plugin that hands items to a player (escrow, prizes, market) marks the gain as explained. Capped at 120 s.
        private void SentinelItemSource(ulong playerId, float seconds)
        {
            if (data == null || playerId == 0) return;
            ItemSource(playerId, Math.Min(120.0, Math.Max(0.0, seconds)));
        }

        private double GetSentinelScore(ulong playerId)
        {
            PlayerRec r = Rec(playerId);
            return r == null ? 0 : Math.Round(CurrentScore(r, NowSec()), 1);
        }

        private bool IsSentinelFrozen(ulong playerId)
        {
            return data != null && IsFrozenId(playerId, NowSec());
        }

        #endregion

        #region Helpers

        private double NowSec()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds + clockSkew;
        }

        // Connection.AveragePing is what the game's own /ping prints [DEC CoreCommandHandler]; Player.AveragePing next.
        private static int PingMs(Player p)
        {
            int ping = 0;
            try
            {
                if (p.Connection != null) ping = p.Connection.AveragePing;
                else ping = p.AveragePing;
            }
            catch (Exception)
            {
                ping = 0;
            }
            return ping < 0 ? 0 : (ping > 5000 ? 5000 : ping);
        }

        private static Player FindOnlineById(string id)
        {
            ulong u;
            if (!ulong.TryParse(id, out u)) return null;
            Player p = Server.GetPlayerById(u);
            return p != null && !p.IsServer ? p : null;
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

        private static double Distance(Vector3 a, Vector3 b)
        {
            double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string Pos(Vector3 v)
        {
            return Pos3(v.x, v.y, v.z);
        }

        private static string Pos3(float x, float y, float z)
        {
            return ((int)Math.Round(x)) + "," + ((int)Math.Round(y)) + "," + ((int)Math.Round(z));
        }

        private static string F1(double v)
        {
            return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Iso(double ts)
        {
            if (ts <= 0) return "";
            return Epoch.AddSeconds(ts).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Display-safe text: no colour tags ([RRGGBB]), no line breaks, no invisible characters, capped length.
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
                else if (ch == '​' || ch == '‌' || ch == '‍' || ch == '⁠' || ch == '﻿' || ch == '‮' || ch == '‭') continue;
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
            return Epoch.AddSeconds(ts).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        }

        #endregion
    }
}
