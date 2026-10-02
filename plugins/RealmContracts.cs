// RealmContracts: player contracts that let small groups matter.
//
//   bounty    a reward on a public enemy of the crown, paid when a death hook proves the kill
//   delivery  a supply order: "bring me N of X, I pay M of Y"; filled from the deliverer's inventory
//   merc      a house in a rebellion hires a sword; paid on verified kills of the other side, or on the
//             hiring poster's confirmation, otherwise refunded after the window
//
// Escrow is real items. In the shipped patched Assembly-CSharp.dll the game's own server code moves items with:
//   take  ItemCollection.AutoCount + ItemCollection.AutoSplit   (StationListener.OnStationUpgradeRequest) [IL]
//   give  GetContainerOfType(entity, Inventory) + ItemCollection.AutoMergeAdd, stacks capped at
//         ContainerManagement.StackLimit                       (ThronesCommandHandler.Give, the /give command) [IL]
// PlayerExtensions.GetInventory(player) is exactly GetContainerOfType(player.Entity, CollectionTypes.Inventory) [IL].
//
// Dup protection: every escrow change is by an amount MEASURED with AutoCount before/after the game call, never by
// the amount requested; a contract changes state before anything is paid; payouts go through a persisted "owed"
// ledger that is only reduced by measured deliveries; the data file is written after every escrow change.
// The failure mode of a crash between a game call and the save is a LOSS of escrow, never a duplicate.
// UNVERIFIED (in-game): that server-side AutoSplit/AutoMergeAdd refresh the client's inventory at once (the game's
// own /give and station upgrade rely on it), and that items on the hotbar count (only the Inventory container is read).
// Set "ItemEscrow": false to run honour-based instead (nothing is moved; posters confirm; the chronicle records it).
//
// Abuse guards: bounties pay only while the target is still a public enemy, and are refunded once they are not
// (checked only while a monarch reigns and the plugins that decide it are loaded); mercenary kills count once per
// victim and never on the killer's own housemates; a bounty is not paid to a killer whose house is the target house's
// liege, vassal or treaty partner (BountyExcludeAllies); a merc contract cannot be taken by the other side of the
// rebellion and a hired sword may be dismissed before the window opens; the owed ledger holds one entry per (player, item); the crown's
// outlaw proclamations have a global cooldown and a per-player repeat cooldown; contract lines in the chronicle are
// capped per hour so they cannot push political history out of its retention window.
//
// Death hook: OnEntityDeath(EntityDeathEvent) is injected at the start of EntityHealth.InvokeDeath [OPJ L188; IL];
// victim = evt.Entity.Owner, killer = evt.KillingDamage.DamageSource.Owner [ASM; USE DeathMessages.cs:20].
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using System.Globalization;
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError, GetInventory [ASM]
using CodeHatch.Engine.Modules.SocialSystem;     // SocialAPI [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using CodeHatch.Inventory.Blueprints;            // InvItemBlueprint [ASM]
using CodeHatch.Inventory.Blueprints.Components; // ContainerManagement [ASM; IL ThronesCommandHandler.Give]
using CodeHatch.ItemContainer;                   // Container, ItemCollection [ASM]
using CodeHatch.Networking.Events.Entities;      // EntityDeathEvent [ASM]
using CodeHatch.Thrones.SocialSystem;            // KingsScheme [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmContracts", "Realm", "0.1.0")]
    [Description("Bounties on enemies of the crown, delivery orders and mercenary hire, with real item escrow")]
    public class RealmContracts : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;

        private const string PermAdmin = "realmcontracts.admin";
        private const string DataName = "RealmContracts";
        private const float TickSeconds = 30f;

        private const string TBounty = "bounty";
        private const string TDelivery = "delivery";
        private const string TMerc = "merc";

        private const string SOpen = "open";          // posted, waiting
        private const string SAccepted = "accepted";  // merc hired / honour delivery awaiting confirmation
        private const string SDone = "done";
        private const string SCancelled = "cancelled";
        private const string SExpired = "expired";

        private PluginConfig config;
        private StoredData data;
        private bool initialized;
        private readonly Dictionary<string, DateTime> lastFullWarning = new Dictionary<string, DateTime>();

        #region Config

        private class PluginConfig
        {
            public bool ItemEscrow = true;             // false = honour-based (no items move)
            public int MaxOpenPerPlayer = 3;
            public int MaxOpenTotal = 60;
            public int MaxBountiesPerTarget = 3;
            public int PostCooldownSeconds = 120;
            public int MinReward = 1;
            public int MaxReward = 1000;               // units of the reward item per contract
            public int MaxWantAmount = 1000;           // delivery: units asked for
            public int DefaultExpiryHours = 24;
            public int MaxExpiryHours = 72;
            public bool RebelsArePublicEnemies = true; // members of houses with an open claim
            public int MaxOutlaws = 5;                 // proclaimed by the reigning monarch
            public int OutlawHours = 24;
            public int TargetCooldownMinutes = 60;     // after a bounty is collected on someone
            public int MercRequiredKills = 1;          // verified kills of the other side in the window to auto-pay
            public int MercConfirmGraceMinutes = 30;   // after the window: poster may still confirm, then refund
            public bool ChronicleDeliveries = false;   // delivery orders are routine; off keeps the chronicle political
            public int ChronicleMaxPerHour = 20;       // all contract lines together; the chronicle keeps only its last N events
            public int OutlawProclaimCooldownMinutes = 10; // between two proclamations of outlawry by the crown
            public int OutlawRepeatCooldownHours = 24; // after outlawry ends (expiry or pardon) before the same player can be named again
            public int MaxListLines = 15;              // /contract list output cap
            public bool BountyExcludeAllies = true;    // no bounty for a killer whose house is liege, vassal or treaty partner
                                                       // of the target's house (the target's friends "collecting" on them)
            public List<string> AllowedItems;          // empty = any item; else exact item names only
        }

        protected override void LoadDefaultConfig()
        {
            var c = new PluginConfig();
            c.AllowedItems = new List<string>();
            Config.WriteObject(c, true);
        }

        private void ClampConfig()
        {
            if (config.AllowedItems == null) config.AllowedItems = new List<string>();
            if (config.MaxOpenPerPlayer < 1) config.MaxOpenPerPlayer = 1;
            if (config.MaxOpenTotal < 1) config.MaxOpenTotal = 1;
            if (config.MaxBountiesPerTarget < 1) config.MaxBountiesPerTarget = 1;
            if (config.PostCooldownSeconds < 0) config.PostCooldownSeconds = 0;
            if (config.MinReward < 1) config.MinReward = 1;
            if (config.MaxReward < config.MinReward) config.MaxReward = config.MinReward;
            if (config.MaxWantAmount < 1) config.MaxWantAmount = 1;
            if (config.MaxExpiryHours < 1) config.MaxExpiryHours = 1;
            if (config.DefaultExpiryHours < 1 || config.DefaultExpiryHours > config.MaxExpiryHours)
                config.DefaultExpiryHours = config.MaxExpiryHours;
            if (config.MaxOutlaws < 0) config.MaxOutlaws = 0;
            if (config.OutlawHours < 1) config.OutlawHours = 1;
            if (config.TargetCooldownMinutes < 0) config.TargetCooldownMinutes = 0;
            if (config.MercRequiredKills < 0) config.MercRequiredKills = 0;
            if (config.MercConfirmGraceMinutes < 0) config.MercConfirmGraceMinutes = 0;
            if (config.ChronicleMaxPerHour < 0) config.ChronicleMaxPerHour = 0;
            if (config.OutlawProclaimCooldownMinutes < 0) config.OutlawProclaimCooldownMinutes = 0;
            if (config.OutlawRepeatCooldownHours < 0) config.OutlawRepeatCooldownHours = 0;
            if (config.MaxListLines < 1) config.MaxListLines = 1;
        }

        #endregion

        #region Data

        private class Contract
        {
            public int Id;
            public string Type;
            public string Status;
            public string PosterId;
            public string PosterName;
            public DateTime PostedAt;
            public DateTime ExpiresAt;
            public string RewardItem;          // blueprint name
            public int RewardAmount;           // measured units held in escrow (or promised, honour mode)
            public bool Escrowed;              // true if RewardAmount is really held by the realm
            // bounty
            public string TargetId;
            public string TargetName;
            // delivery
            public string WantItem;
            public int WantAmount;
            // merc
            public string House;
            public string Side;                // claimant | crown
            public DateTime WindowStart;
            public DateTime WindowEnd;
            public int Kills;
            public List<string> Victims;       // distinct victim ids already counted (one kill per victim)
            // fulfiller (bounty killer, deliverer, mercenary)
            public string FulfillerId;
            public string FulfillerName;
            public string Outcome;
        }

        private class Owed
        {
            public string PlayerId;
            public string PlayerName;
            public string Item;
            public int Amount;
            public int ContractId;
        }

        private class Outlaw
        {
            public string Name;
            public DateTime Until;
            public string By;
            public bool Court;                         // placed by RealmLaws' court (ProclaimOutlaw), not the crown
        }

        private class StoredData
        {
            public int NextId = 1;
            public List<Contract> Contracts = new List<Contract>();
            public List<Owed> Owed = new List<Owed>();
            public Dictionary<string, DateTime> LastPost = new Dictionary<string, DateTime>();
            public Dictionary<string, Outlaw> Outlaws = new Dictionary<string, Outlaw>();
            public Dictionary<string, DateTime> TargetCooldown = new Dictionary<string, DateTime>();
            public Dictionary<string, DateTime> OutlawAgainAfter = new Dictionary<string, DateTime>();  // player id -> earliest re-proclamation
            public DateTime LastOutlawAt = DateTime.MinValue;
        }

        private void SaveData()
        {
            if (data == null) return;                         // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Prefix", "[A08850]Contracts[FFFFFF]: " },
                { "Help1", "/contract list [bounty|delivery|merc] | /contract info <id> | /contract collect" },
                { "Help2", "/contract post bounty <player> <amount> \"<item>\" [hours]" },
                { "Help3", "/contract post delivery <qty> \"<item wanted>\" <amount> \"<reward item>\" [hours]" },
                { "Help4", "/contract post merc <amount> \"<item>\" (house leader, during a pending or open rebellion)" },
                { "Help5", "/contract accept <id> | deliver <id> | confirm <id> | cancel <id> | enemies | items <search>" },
                { "Help6", "Monarch: /contract outlaw <player> | /contract pardon <player>. Admin: /contract admin cancel|pay|refund <id>" },
                { "Mode", "Escrow: {0}." },
                { "NoPermission", "You may not do that." },
                { "NotFound", "There is no open contract #{0}." },
                { "PlayerNotFound", "No such person is online." },
                { "BadNumber", "'{0}' is not a whole number from {1} to {2}." },
                { "UnknownItem", "No item is named '{0}'. Try /contract items <search>." },
                { "ItemNotAllowed", "'{0}' may not be used in contracts on this server." },
                { "ItemsFound", "Items matching '{0}': {1}" },
                { "ItemsNone", "No item matches '{0}'." },
                { "PostCooldown", "You may post another contract in {0} s." },
                { "TooManyMine", "You already have {0} open contracts." },
                { "TooManyTotal", "The realm's board is full. Try again later." },
                { "NotEnoughItems", "You need {0} {1} in your inventory (you have {2})." },
                { "EscrowFailed", "The realm could not take the items. Nothing was posted." },
                { "Posted", "Contract #{0} posted." },
                { "NotEnemy", "{0} is not a public enemy of the crown. See /contract enemies." },
                { "NoSelf", "You cannot do that to yourself." },
                { "TargetCooldown", "A bounty was just collected on {0}. Wait {1} min." },
                { "TargetFull", "{0} already carries {1} bounties." },
                { "Enemies", "Public enemies of the crown: {0}" },
                { "EnemiesNone", "The crown has no public enemies right now." },
                { "NoKing", "The throne is vacant; there is no crown to name enemies." },
                { "NotKing", "Only the reigning monarch may do that." },
                { "OutlawFull", "The crown may name at most {0} outlaws at once." },
                { "Outlawed", "{0} is proclaimed an outlaw for {1} h." },
                { "Pardoned", "{0} is pardoned." },
                { "NotOutlaw", "{0} is not an outlaw." },
                { "AlreadyOutlaw", "{0} is already an outlaw ({1} min left)." },
                { "OutlawCooldown", "The crown may proclaim another outlaw in {0} min." },
                { "OutlawRepeat", "{0} was an outlaw too recently. The crown may name them again in {1} min." },
                { "ListMore", "  ... and {0} more. Filter with /contract list bounty|delivery|merc." },
                { "NeedHouse", "You must belong to a house." },
                { "NotLeader", "Only the head of your house may hire swords." },
                { "NoRebellion", "Your house has no part in a pending or open rebellion." },
                { "MercOwnHouse", "You cannot be hired by your own house." },
                { "MercEnemySide", "Your house stands on the other side of this rebellion; House {0} will not hire you." },
                { "MercDismissed", "House {0} has dismissed you before the fighting began; contract #{1} is withdrawn." },
                { "NotMerc", "That is not a mercenary contract." },
                { "NotDelivery", "That is not a delivery contract." },
                { "AlreadyTaken", "That contract is already taken." },
                { "Accepted", "You are hired by House {0} until {1} UTC. Kills of the other side in the window count." },
                { "Delivered", "Delivered. Your reward is {0} {1}." },
                { "DeliveredHonour", "Marked as delivered. The poster must confirm with /contract confirm {0}." },
                { "NotPoster", "Only the poster may do that." },
                { "CannotCancel", "That contract can no longer be cancelled." },
                { "Cancelled", "Contract #{0} cancelled." },
                { "NothingToConfirm", "There is nothing to confirm on that contract." },
                { "Confirmed", "Contract #{0} confirmed." },
                { "Paid", "You receive {0} {1} (contract #{2})." },
                { "StillOwed", "Your packs are full. {0} {1} wait for you: /contract collect" },
                { "NothingOwed", "Nothing is owed to you." },
                { "Line", "  #{0} {1}: {2} - reward {3} {4}, {5}" },
                { "ListNone", "No open contracts." },
                { "AdminDone", "Done: {0}." }
            }, this);
        }

        private string Msg(string key, Player player)
        {
            return lang.GetMessage(key, this, player == null ? null : player.Id.ToString());
        }

        private void Reply(Player player, string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
            player.SendMessage(Msg("Prefix", player) + text);                // single-string overload: brace safe
        }

        private void ReplyError(Player player, string key, params object[] args)
        {
            string text = args.Length > 0 ? string.Format(Msg(key, player), args) : Msg(key, player);
            player.SendError(text);
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            ClampConfig();
            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(DataName);
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                // Refuse to run on a damaged file rather than overwrite escrow records.
                data = null;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message + ". Fix or remove the file, then reload.");
                throw;
            }
            if (data == null && existed)
            {
                // A file truncated to nothing parses to null. Starting empty would forget every escrowed reward and owed
                // item (players' real goods), so refuse instead.
                PrintError("oxide/data/" + DataName + ".json exists but holds no data. Nothing was written. Restore it or delete it, then reload.");
                throw new InvalidOperationException("RealmContracts data file is empty");
            }
            if (data == null) data = new StoredData();
            if (data.Contracts == null) data.Contracts = new List<Contract>();
            if (data.Owed == null) data.Owed = new List<Owed>();
            if (data.LastPost == null) data.LastPost = new Dictionary<string, DateTime>();
            if (data.Outlaws == null) data.Outlaws = new Dictionary<string, Outlaw>();
            if (data.TargetCooldown == null) data.TargetCooldown = new Dictionary<string, DateTime>();
            if (data.OutlawAgainAfter == null) data.OutlawAgainAfter = new Dictionary<string, DateTime>();
            data.Contracts.RemoveAll(IsBrokenContract);
            data.Owed.RemoveAll(IsBrokenOwed);
            CompactOwed();
            foreach (Contract c in data.Contracts) if (c.Id >= data.NextId) data.NextId = c.Id + 1;
            permission.RegisterPermission(PermAdmin, this);
        }

        private static bool IsBrokenContract(Contract c)
        {
            return c == null || c.Type == null || c.Status == null || c.PosterId == null;
        }

        private static bool IsBrokenOwed(Owed o)
        {
            return o == null || o.PlayerId == null || o.Item == null || o.Amount <= 0;
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

        #region Commands

        [ChatCommand("contract")]
        private void CmdContract(Player player, string command, string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "list": CmdList(player, args.Length > 1 ? args[1].ToLowerInvariant() : null); return;
                case "info": CmdInfo(player, args); return;
                case "post": CmdPost(player, args); return;
                case "accept": CmdAccept(player, args); return;
                case "deliver": CmdDeliver(player, args); return;
                case "confirm": CmdConfirm(player, args); return;
                case "cancel": CmdCancel(player, args); return;
                case "collect": CmdCollect(player); return;
                case "enemies": CmdEnemies(player); return;
                case "items": CmdItems(player, args); return;
                case "outlaw": CmdOutlaw(player, args, true); return;
                case "pardon": CmdOutlaw(player, args, false); return;
                case "admin": CmdAdmin(player, args); return;
            }
            for (int i = 1; i <= 6; i++) player.SendMessage(Msg("Help" + i, player));
            Reply(player, "Mode", config.ItemEscrow ? "items held by the realm" : "honour (no items are moved)");
        }

        private void CmdList(Player player, string type)
        {
            int shown = 0, more = 0;
            foreach (Contract c in data.Contracts)
            {
                if (c.Status != SOpen && c.Status != SAccepted) continue;
                if (type != null && c.Type != type) continue;
                if (shown >= config.MaxListLines) { more++; continue; }   // bounded chat output
                shown++;
                Reply(player, "Line", c.Id, c.Type, Describe(c), c.RewardAmount, c.RewardItem, StatusText(c));
            }
            if (shown == 0) Reply(player, "ListNone");
            else if (more > 0) Reply(player, "ListMore", more);
        }

        private void CmdInfo(Player player, string[] args)
        {
            Contract c = args.Length > 1 ? FindContract(args[1]) : null;
            if (c == null) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            Reply(player, "Line", c.Id, c.Type, Describe(c), c.RewardAmount, c.RewardItem, StatusText(c));
            player.SendMessage("  Posted by " + c.PosterName + (c.Escrowed ? " (reward held in escrow)" : " (honour)")
                + (c.FulfillerName != null ? "; taken by " + c.FulfillerName : "") + (c.Outcome != null ? ". " + c.Outcome : ""));
        }

        private void CmdPost(Player player, string[] args)
        {
            string type = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (type != TBounty && type != TDelivery && type != TMerc) { for (int i = 2; i <= 4; i++) player.SendMessage(Msg("Help" + i, player)); return; }

            string id = player.Id.ToString();
            DateTime now = DateTime.UtcNow;
            DateTime last;
            if (!IsAdmin(player) && data.LastPost.TryGetValue(id, out last) && (now - last).TotalSeconds < config.PostCooldownSeconds)
            {
                ReplyError(player, "PostCooldown", (int)Math.Ceiling(config.PostCooldownSeconds - (now - last).TotalSeconds));
                return;
            }
            int mine = 0, total = 0;
            foreach (Contract c in data.Contracts)
            {
                if (c.Status != SOpen && c.Status != SAccepted) continue;
                total++;
                if (c.PosterId == id) mine++;
            }
            if (mine >= config.MaxOpenPerPlayer) { ReplyError(player, "TooManyMine", mine); return; }
            if (total >= config.MaxOpenTotal) { ReplyError(player, "TooManyTotal"); return; }

            var c2 = new Contract { Type = type, Status = SOpen, PosterId = id, PosterName = player.Name, PostedAt = now };
            int rewardIdx;
            if (type == TBounty)
            {
                if (args.Length < 5) { player.SendMessage(Msg("Help2", player)); return; }
                Player target = FindOnline(args[2]);
                if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
                if (target.Id == player.Id) { ReplyError(player, "NoSelf"); return; }
                string why = EnemyReason(target.Id, target.Name);
                if (why == null) { ReplyError(player, "NotEnemy", target.Name); return; }
                string tid = target.Id.ToString();
                DateTime cool;
                if (data.TargetCooldown.TryGetValue(tid, out cool) && cool > now)
                {
                    ReplyError(player, "TargetCooldown", target.Name, MinutesUntil(cool));
                    return;
                }
                int onTarget = 0;
                foreach (Contract c in data.Contracts) if (c.Type == TBounty && c.Status == SOpen && c.TargetId == tid) onTarget++;
                if (onTarget >= config.MaxBountiesPerTarget) { ReplyError(player, "TargetFull", target.Name, onTarget); return; }
                c2.TargetId = tid;
                c2.TargetName = target.Name;
                rewardIdx = 3;
            }
            else if (type == TDelivery)
            {
                if (args.Length < 6) { player.SendMessage(Msg("Help3", player)); return; }
                int want;
                if (!ParseAmount(player, args[2], config.MaxWantAmount, out want)) return;
                InvItemBlueprint wantBp = ResolveItem(player, args[3]);
                if (wantBp == null) return;
                c2.WantItem = wantBp.Name;
                c2.WantAmount = want;
                rewardIdx = 4;
            }
            else
            {
                if (args.Length < 4) { player.SendMessage(Msg("Help4", player)); return; }
                if (!SetupMerc(player, c2)) return;
                rewardIdx = 2;
            }

            int amount;
            if (!ParseAmount(player, args[rewardIdx], config.MaxReward, out amount)) return;
            if (amount < config.MinReward) { ReplyError(player, "BadNumber", args[rewardIdx], config.MinReward, config.MaxReward); return; }
            InvItemBlueprint bp = ResolveItem(player, args[rewardIdx + 1]);
            if (bp == null) return;
            if (type != TMerc)
            {
                int hours = config.DefaultExpiryHours;
                if (args.Length > rewardIdx + 2 && !ParseAmount(player, args[rewardIdx + 2], config.MaxExpiryHours, out hours)) return;
                c2.ExpiresAt = now.AddHours(hours);
            }
            else c2.ExpiresAt = c2.WindowEnd.AddMinutes(config.MercConfirmGraceMinutes);

            c2.RewardItem = bp.Name;
            if (config.ItemEscrow)
            {
                int have = CountItems(player, bp);
                if (have < amount) { ReplyError(player, "NotEnoughItems", amount, bp.Name, have); return; }
                int taken = TakeItems(player, bp, amount);
                if (taken <= 0) { ReplyError(player, "EscrowFailed"); return; }
                if (taken < amount)
                {
                    // Partial take (should not happen after the count check): return what was taken, post nothing.
                    AddOwed(id, player.Name, bp.Name, taken, 0);
                    SaveData();
                    PayOwed(player);
                    ReplyError(player, "EscrowFailed");
                    return;
                }
                c2.Escrowed = true;
            }
            c2.RewardAmount = amount;
            c2.Id = data.NextId++;
            data.Contracts.Add(c2);
            data.LastPost[id] = now;
            SaveData();                                          // escrow is recorded before anything else happens

            Reply(player, "Posted", c2.Id);
            if (type != TDelivery || config.ChronicleDeliveries)
                Chronicle("contract_posted", PostedTitle(c2), PostedDetail(c2), ActorsOf(c2));
        }

        private bool SetupMerc(Player player, Contract c)
        {
            string house = HouseOf(player.Id);
            if (house == null) { ReplyError(player, "NeedHouse"); return false; }
            if (!IsHouseLeader(player, house)) { ReplyError(player, "NotLeader"); return false; }
            DateTime now = DateTime.UtcNow;
            DateTime start, end;
            string side;
            if (!RebellionWindowFor(house, now, out side, out start, out end)) { ReplyError(player, "NoRebellion"); return false; }
            c.House = house;
            c.Side = side;
            c.WindowStart = start;
            c.WindowEnd = end;
            return true;
        }

        private void CmdAccept(Player player, string[] args)
        {
            Contract c = args.Length > 1 ? FindContract(args[1]) : null;
            if (c == null || c.Status != SOpen) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            if (c.Type != TMerc) { ReplyError(player, "NotMerc"); return; }
            if (DateTime.UtcNow >= c.WindowEnd) { ReplyError(player, "NotFound", c.Id); return; }
            if (c.PosterId == player.Id.ToString()) { ReplyError(player, "NoSelf"); return; }
            string myHouse = HouseOf(player.Id);
            if (myHouse != null && string.Equals(myHouse, c.House, StringComparison.OrdinalIgnoreCase)) { ReplyError(player, "MercOwnHouse"); return; }
            // A sword from the other side could take the contract only to lock the house's coin until the window ends.
            if (myHouse != null && IsOtherSide(c, myHouse)) { ReplyError(player, "MercEnemySide", c.House); return; }
            c.Status = SAccepted;
            c.FulfillerId = player.Id.ToString();
            c.FulfillerName = player.Name;
            SaveData();
            Reply(player, "Accepted", c.House, c.WindowEnd.ToString("ddd HH:mm"));
            Chronicle("contract_posted", player.Name + " takes House " + c.House + "'s coin",
                player.Name + " is hired to fight for House " + c.House + " in the coming rebellion.", new[] { player.Name, c.PosterName });
        }

        private void CmdDeliver(Player player, string[] args)
        {
            Contract c = args.Length > 1 ? FindContract(args[1]) : null;
            if (c == null || c.Status != SOpen) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            if (c.Type != TDelivery) { ReplyError(player, "NotDelivery"); return; }
            if (c.PosterId == player.Id.ToString()) { ReplyError(player, "NoSelf"); return; }

            if (!config.ItemEscrow || !c.Escrowed)
            {
                c.Status = SAccepted;
                c.FulfillerId = player.Id.ToString();
                c.FulfillerName = player.Name;
                SaveData();
                Reply(player, "DeliveredHonour", c.Id);
                return;
            }

            InvItemBlueprint want = FindItem(c.WantItem);
            if (want == null) { ReplyError(player, "UnknownItem", c.WantItem); return; }
            int have = CountItems(player, want);
            if (have < c.WantAmount) { ReplyError(player, "NotEnoughItems", c.WantAmount, want.Name, have); return; }
            int taken = TakeItems(player, want, c.WantAmount);
            if (taken < c.WantAmount)
            {
                if (taken > 0) { AddOwed(player.Id.ToString(), player.Name, want.Name, taken, c.Id); SaveData(); PayOwed(player); }
                ReplyError(player, "EscrowFailed");
                return;
            }
            c.Status = SDone;
            c.FulfillerId = player.Id.ToString();
            c.FulfillerName = player.Name;
            c.Outcome = "Delivered by " + player.Name + ".";
            AddOwed(c.PosterId, c.PosterName, want.Name, taken, c.Id);          // the goods, for the poster
            AddOwed(c.FulfillerId, player.Name, c.RewardItem, c.RewardAmount, c.Id); // the reward, for the deliverer
            SaveData();
            Reply(player, "Delivered", c.RewardAmount, c.RewardItem);
            PayOwed(player);
            Player poster = OnlineById(c.PosterId);
            if (poster != null) PayOwed(poster);
            if (config.ChronicleDeliveries)
                Chronicle("contract_fulfilled", player.Name + " fills an order for " + c.PosterName,
                    player.Name + " delivered " + c.WantAmount + " " + c.WantItem + " to " + c.PosterName + ".", new[] { player.Name, c.PosterName });
        }

        private void CmdConfirm(Player player, string[] args)
        {
            Contract c = args.Length > 1 ? FindContract(args[1]) : null;
            if (c == null) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            if (c.PosterId != player.Id.ToString()) { ReplyError(player, "NotPoster"); return; }
            if (c.Status != SAccepted || c.FulfillerId == null) { ReplyError(player, "NothingToConfirm"); return; }
            if (c.Type == TMerc && DateTime.UtcNow < c.WindowStart) { ReplyError(player, "NothingToConfirm"); return; }
            Settle(c, true, c.Type == TMerc ? "The house paid its sword." : "Delivery confirmed by " + c.PosterName + ".");
            Reply(player, "Confirmed", c.Id);
        }

        private void CmdCancel(Player player, string[] args)
        {
            Contract c = args.Length > 1 ? FindContract(args[1]) : null;
            if (c == null) { ReplyError(player, "NotFound", args.Length > 1 ? args[1] : "?"); return; }
            if (c.PosterId != player.Id.ToString()) { ReplyError(player, "NotPoster"); return; }
            // A hired sword cannot be dropped. An honour-mode delivery that someone has only *marked* as delivered
            // (nothing moved) may be refused by the poster; otherwise anyone could lock an order until it expires.
            bool honourClaim = c.Status == SAccepted && c.Type == TDelivery;
            // A hired sword may be dismissed only before the fighting begins (nothing has been earned yet); otherwise
            // anyone could accept a house's contract just to lock its coin until the window ends.
            bool mercUnstarted = c.Status == SAccepted && c.Type == TMerc && DateTime.UtcNow < c.WindowStart && c.Kills == 0;
            if (c.Status != SOpen && !honourClaim && !mercUnstarted) { ReplyError(player, "CannotCancel"); return; }
            string dismissed = mercUnstarted ? c.FulfillerId : null;
            Settle(c, false, honourClaim ? "The poster refused the delivery claimed by " + c.FulfillerName + "."
                : mercUnstarted ? "House " + c.House + " dismissed " + c.FulfillerName + " before the fighting began."
                : "Withdrawn by " + c.PosterName + ".");
            Reply(player, "Cancelled", c.Id);
            Player merc = dismissed != null ? OnlineById(dismissed) : null;
            if (merc != null) Reply(merc, "MercDismissed", c.House, c.Id);
        }

        private void CmdCollect(Player player)
        {
            bool any = false;
            foreach (Owed o in data.Owed) if (o.PlayerId == player.Id.ToString()) any = true;
            if (!any) { Reply(player, "NothingOwed"); return; }
            PayOwed(player);
        }

        private void CmdEnemies(Player player)
        {
            if (CrownName() == null) { Reply(player, "NoKing"); return; }
            var names = new List<string>();
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && EnemyReason(p.Id, p.Name) != null) names.Add(p.Name);
            if (names.Count == 0) Reply(player, "EnemiesNone");
            else Reply(player, "Enemies", string.Join(", ", names.ToArray()));
        }

        private void CmdItems(Player player, string[] args)
        {
            if (args.Length < 2 || InvBlueprints.Instance == null) { player.SendMessage(Msg("Help5", player)); return; }
            string search = JoinFrom(args, 1);
            InvItemBlueprint[] found = InvBlueprints.GetBlueprintsContaining(search);
            if (found == null || found.Length == 0) { Reply(player, "ItemsNone", search); return; }
            var names = new List<string>();
            foreach (InvItemBlueprint bp in found) { if (bp != null) names.Add(bp.Name); if (names.Count >= 10) break; }
            Reply(player, "ItemsFound", search, string.Join(", ", names.ToArray()));
        }

        private void CmdOutlaw(Player player, string[] args, bool proclaim)
        {
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            if (crown == null || !crown.IsKing(player)) { ReplyError(player, "NotKing"); return; }
            if (args.Length < 2) { player.SendMessage(Msg("Help6", player)); return; }
            string name = JoinFrom(args, 1);
            DateTime now = DateTime.UtcNow;
            PruneOutlaws(now);
            if (!proclaim)
            {
                foreach (KeyValuePair<string, Outlaw> kv in new List<KeyValuePair<string, Outlaw>>(data.Outlaws))
                    if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        data.Outlaws.Remove(kv.Key);
                        SetOutlawAgainAfter(kv.Key, now);
                        // A pardon withdraws the price on the pardoned (rebels stay enemies until their claim ends).
                        if (!IsEnemyId(kv.Key))
                            foreach (Contract c in data.Contracts.ToArray())
                                if (c.Type == TBounty && c.Status == SOpen && c.TargetId == kv.Key)
                                    Settle(c, false, "The crown pardoned " + kv.Value.Name + ".");
                        SaveData();
                        Reply(player, "Pardoned", kv.Value.Name);
                        Chronicle("decree", kv.Value.Name + " is pardoned", player.Name + " lifts the sentence of outlawry on "
                            + kv.Value.Name + ".", new[] { player.Name, kv.Value.Name });
                        return;
                    }
                ReplyError(player, "NotOutlaw", name);
                return;
            }
            Player target = FindOnline(name);
            if (target == null) { ReplyError(player, "PlayerNotFound"); return; }
            if (target.Id == player.Id) { ReplyError(player, "NoSelf"); return; }
            string tid = target.Id.ToString();
            // Anti-grief: a proclamation cannot be renewed or cycled (pardon + proclaim) to keep one player hunted,
            // and the crown cannot flood the chronicle with proclamations.
            if (data.Outlaws.ContainsKey(tid)) { ReplyError(player, "AlreadyOutlaw", target.Name, MinutesUntil(data.Outlaws[tid].Until)); return; }
            DateTime again;
            if (data.OutlawAgainAfter.TryGetValue(tid, out again) && again > now)
            {
                ReplyError(player, "OutlawRepeat", target.Name, MinutesUntil(again));
                return;
            }
            DateTime nextProclaim = data.LastOutlawAt.AddMinutes(config.OutlawProclaimCooldownMinutes);
            if (data.LastOutlawAt != DateTime.MinValue && nextProclaim > now)
            {
                ReplyError(player, "OutlawCooldown", MinutesUntil(nextProclaim));
                return;
            }
            if (data.Outlaws.Count >= config.MaxOutlaws) { ReplyError(player, "OutlawFull", config.MaxOutlaws); return; }
            data.LastOutlawAt = now;
            data.Outlaws[tid] = new Outlaw { Name = target.Name, Until = now.AddHours(config.OutlawHours), By = player.Name };
            SaveData();
            Reply(player, "Outlawed", target.Name, config.OutlawHours);
            Chronicle("decree", target.Name + " is declared outlaw", player.Name + " names " + target.Name
                + " an enemy of the crown for " + config.OutlawHours + " hours.", new[] { player.Name, target.Name });
        }

        private void CmdAdmin(Player player, string[] args)
        {
            if (!IsAdmin(player)) { ReplyError(player, "NoPermission"); return; }
            string what = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            Contract c = args.Length > 2 ? FindContract(args[2]) : null;
            if (c == null || (c.Status != SOpen && c.Status != SAccepted))
            {
                ReplyError(player, "NotFound", args.Length > 2 ? args[2] : "?");
                return;
            }
            if (what == "cancel" || what == "refund") Settle(c, false, "Set aside by the realm's stewards.");
            else if (what == "pay" && c.FulfillerId != null) Settle(c, true, "Settled by the realm's stewards.");
            else { player.SendMessage(Msg("Help6", player)); return; }
            Reply(player, "AdminDone", "#" + c.Id + " " + c.Status);
        }

        #endregion

        #region Settlement and ticks

        // Closes a contract. pay=true: reward to the fulfiller; false: back to the poster. State changes first.
        private void Settle(Contract c, bool pay, string outcome)
        {
            if (c.Status == SDone || c.Status == SCancelled || c.Status == SExpired) return;
            bool expired = !pay && DateTime.UtcNow >= c.ExpiresAt;
            c.Status = pay ? SDone : (expired ? SExpired : SCancelled);
            c.Outcome = outcome;
            string toId = pay ? c.FulfillerId : c.PosterId;
            string toName = pay ? c.FulfillerName : c.PosterName;
            if (c.Escrowed && toId != null) AddOwed(toId, toName, c.RewardItem, c.RewardAmount, c.Id);
            SaveData();
            Player to = toId != null ? OnlineById(toId) : null;
            if (to != null) PayOwed(to);

            if (c.Type == TDelivery && !config.ChronicleDeliveries) return;
            string honour = c.Escrowed ? "" : " (honour: " + c.PosterName + " owes " + c.RewardAmount + " " + c.RewardItem + ")";
            if (pay)
                Chronicle("contract_fulfilled", FulfilledTitle(c), outcome + honour, ActorsOf(c));
            else
                Chronicle("contract_ended", "Contract of " + c.PosterName + " " + (expired ? "lapses" : "is withdrawn"),
                    Describe(c) + ". " + outcome, new[] { c.PosterName });
        }

        private void Tick()
        {
            DateTime now = DateTime.UtcNow;
            bool changed = false;
            foreach (Contract c in data.Contracts.ToArray())
            {
                if (c.Status != SOpen && c.Status != SAccepted) continue;
                if (c.Type == TMerc && c.Status == SAccepted && now >= c.WindowEnd
                    && config.MercRequiredKills > 0 && c.Kills >= config.MercRequiredKills)
                {
                    Settle(c, true, c.FulfillerName + " earned the coin of House " + c.House + " with " + c.Kills + " verified kills.");
                    changed = true;
                }
                else if (c.Type == TBounty && c.Status == SOpen && EnemyStatusKnown() && !IsEnemyId(c.TargetId))
                {
                    // Outlawry lapsed or the claim ended: the price on them is withdrawn and refunded.
                    Settle(c, false, c.TargetName + " is no longer an enemy of the crown.");
                    changed = true;
                }
                else if (now >= c.ExpiresAt)
                {
                    Settle(c, false, c.Type == TMerc && c.FulfillerName != null
                        ? c.FulfillerName + " did not earn the coin; it returns to House " + c.House + "."
                        : "No one took it up in time.");
                    changed = true;
                }
            }
            if (PruneOutlaws(now)) changed = true;
            foreach (string k in new List<string>(data.TargetCooldown.Keys))
                if (data.TargetCooldown[k] <= now) { data.TargetCooldown.Remove(k); changed = true; }
            foreach (string k in new List<string>(data.LastPost.Keys))      // one entry per poster ever: keep it bounded
                if ((now - data.LastPost[k]).TotalSeconds >= config.PostCooldownSeconds) { data.LastPost.Remove(k); changed = true; }
            // Keep history short; the chronicle holds the record.
            int closed = 0;
            foreach (Contract c in data.Contracts) if (c.Status != SOpen && c.Status != SAccepted) closed++;
            while (closed > 100)
            {
                int i = data.Contracts.FindIndex(IsClosed);
                if (i < 0) break;
                data.Contracts.RemoveAt(i);
                closed--;
                changed = true;
            }
            if (changed) SaveData();

            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && HasOwed(p.Id.ToString())) PayOwed(p);
        }

        private static bool IsClosed(Contract c)
        {
            return c.Status != SOpen && c.Status != SAccepted;
        }

        private bool PruneOutlaws(DateTime now)
        {
            bool changed = false;
            foreach (string k in new List<string>(data.Outlaws.Keys))
                if (data.Outlaws[k] == null || data.Outlaws[k].Until <= now)
                {
                    data.Outlaws.Remove(k);
                    SetOutlawAgainAfter(k, now);
                    changed = true;
                }
            foreach (string k in new List<string>(data.OutlawAgainAfter.Keys))
                if (data.OutlawAgainAfter[k] <= now) { data.OutlawAgainAfter.Remove(k); changed = true; }
            return changed;
        }

        private void SetOutlawAgainAfter(string playerId, DateTime now)
        {
            if (config.OutlawRepeatCooldownHours > 0) data.OutlawAgainAfter[playerId] = now.AddHours(config.OutlawRepeatCooldownHours);
        }

        // RB 1 [OPJ L188]: always return null so the game's own death handling continues.
        private object OnEntityDeath(EntityDeathEvent evt)
        {
            try { HandleDeath(evt); }
            catch (Exception ex) { PrintError("Death handling failed: " + ex.Message); }
            return null;
        }

        private void HandleDeath(EntityDeathEvent evt)
        {
            if (evt == null || evt.Entity == null || !evt.Entity.IsPlayer || evt.KillingDamage == null) return;
            Player victim = evt.Entity.Owner;
            Player killer = evt.KillingDamage.DamageSource != null ? evt.KillingDamage.DamageSource.Owner : null;
            if (victim == null || killer == null || victim.IsServer || killer.IsServer || killer.Id == victim.Id) return;
            string vid = victim.Id.ToString(), kid = killer.Id.ToString();
            string victimHouse = HouseOf(victim.Id), killerHouse = HouseOf(killer.Id);
            bool sameHouse = victimHouse != null && killerHouse != null
                && string.Equals(victimHouse, killerHouse, StringComparison.OrdinalIgnoreCase);
            DateTime now = DateTime.UtcNow;
            bool changed = false;

            foreach (Contract c in data.Contracts.ToArray())
            {
                if (c.Type == TBounty && c.Status == SOpen && c.TargetId == vid)
                {
                    // A bounty is not paid to its own poster or to the target's housemates (collusion guard),
                    // and only while the target is still a public enemy (outlawry or claim may have ended since posting).
                    if (kid == c.PosterId || sameHouse) continue;
                    if (config.BountyExcludeAllies && Allied(victimHouse, killerHouse)) continue;
                    if (EnemyReason(victim.Id, victim.Name) == null) continue;
                    c.FulfillerId = kid;
                    c.FulfillerName = killer.Name;
                    if (config.TargetCooldownMinutes > 0) data.TargetCooldown[vid] = now.AddMinutes(config.TargetCooldownMinutes);
                    Settle(c, true, killer.Name + " slew " + victim.Name + " and claims the bounty of " + c.PosterName + ".");
                    changed = true;
                }
                else if (c.Type == TMerc && c.Status == SAccepted && c.FulfillerId == kid && now >= c.WindowStart && now < c.WindowEnd)
                {
                    // Housemates never count (no farming a friend in your own house), and each victim counts once.
                    if (sameHouse || !IsOtherSide(c, victimHouse)) continue;
                    if (c.Victims == null) c.Victims = new List<string>();
                    if (c.Victims.Contains(vid)) continue;
                    c.Victims.Add(vid);
                    c.Kills++;
                    changed = true;
                }
            }
            if (changed) SaveData();
        }

        // claimant side fights the crown and its sworn houses; the crown side fights houses with an active claim.
        private bool IsOtherSide(Contract c, string victimHouse)
        {
            if (victimHouse == null || string.Equals(victimHouse, c.House, StringComparison.OrdinalIgnoreCase)) return false;
            if (c.Side == "claimant") return IsSwornToCrown(victimHouse);
            foreach (ClaimInfo ci in OpenClaims())
                if (ci.Status == "active" && string.Equals(ci.House, victimHouse, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        #endregion

        #region Enemies of the crown and rebellion windows

        private class ClaimInfo
        {
            public string House;
            public string Status;
            public DateTime Start;
            public DateTime End;
        }

        // Returns why the player is a public enemy, or null. Requires a reigning monarch.
        private string EnemyReason(ulong playerId, string name)
        {
            if (CrownName() == null) return null;
            Outlaw o;
            if (data.Outlaws.TryGetValue(playerId.ToString(), out o) && o != null && o.Until > DateTime.UtcNow) return "outlaw";
            if (!config.RebelsArePublicEnemies) return null;
            string house = HouseOf(playerId);
            if (house == null) return null;
            foreach (ClaimInfo ci in OpenClaims())
                if (string.Equals(ci.House, house, StringComparison.OrdinalIgnoreCase)) return "rebel";
            return null;
        }

        // True when the enemy test can be trusted to say "no": a monarch reigns and the plugins it reads are loaded.
        // (A reload of CrownAndConsequences or RealmHouses must not refund every bounty on the board.)
        private bool EnemyStatusKnown()
        {
            return CrownName() != null && (!config.RebelsArePublicEnemies || (CrownAndConsequences != null && RealmHouses != null));
        }

        private bool IsEnemyId(string playerId)
        {
            ulong id;
            return ulong.TryParse(playerId, out id) && EnemyReason(id, null) != null;
        }

        private bool RebellionWindowFor(string house, DateTime now, out string side, out DateTime start, out DateTime end)
        {
            side = null;
            start = DateTime.MaxValue;
            end = DateTime.MaxValue;
            List<ClaimInfo> claims = OpenClaims();
            foreach (ClaimInfo ci in claims)
                if (string.Equals(ci.House, house, StringComparison.OrdinalIgnoreCase) && ci.End > now)
                {
                    side = "claimant";
                    start = ci.Start;
                    end = ci.End;
                    return true;
                }
            if (!IsSwornToCrown(house)) return false;
            foreach (ClaimInfo ci in claims)
                if (ci.End > now && ci.Start < start) { start = ci.Start; end = ci.End; side = "crown"; }
            return side != null;
        }

        private List<ClaimInfo> OpenClaims()
        {
            var list = new List<ClaimInfo>();
            if (CrownAndConsequences == null) return list;
            string[] raw = CrownAndConsequences.Call("GetOpenClaims") as string[];
            if (raw == null) return list;
            foreach (string line in raw)
            {
                string[] p = (line ?? "").Split('|');
                DateTime s, e;
                if (p.Length < 4 || !ParseUtc(p[2], out s) || !ParseUtc(p[3], out e)) continue;
                list.Add(new ClaimInfo { House = p[0], Status = p[1], Start = s, End = e });
            }
            return list;
        }

        private bool IsSwornToCrown(string house)
        {
            if (house == null || CrownAndConsequences == null) return false;
            object r = CrownAndConsequences.Call("IsSwornToCrown", house);
            return r is bool && (bool)r;
        }

        private string CrownName()
        {
            KingsScheme crown = SocialAPI.Get<KingsScheme>();
            return crown != null && crown.HasKing() ? crown.GetKingName() : null;
        }

        #endregion

        #region Items (escrow)

        private InvItemBlueprint FindItem(string name)
        {
            if (string.IsNullOrEmpty(name) || InvBlueprints.Instance == null) return null;
            return InvBlueprints.Instance.GetBlueprintForName(name, true, true);      // exact, case-insensitive [ASM; IL]
        }

        private InvItemBlueprint ResolveItem(Player player, string name)
        {
            InvItemBlueprint bp = FindItem(name);
            if (bp == null) { ReplyError(player, "UnknownItem", name); return null; }
            if (config.AllowedItems.Count > 0 && !ContainsIgnoreCase(config.AllowedItems, bp.Name))
            {
                ReplyError(player, "ItemNotAllowed", bp.Name);
                return null;
            }
            return bp;
        }

        private static ItemCollection InventoryOf(Player player)
        {
            if (player == null || player.Entity == null) return null;
            Container inv = player.GetInventory();
            return inv != null ? inv.Contents : null;
        }

        private static int CountItems(Player player, InvItemBlueprint bp)
        {
            ItemCollection items = InventoryOf(player);
            return items != null && bp != null ? ItemCollection.AutoCount(items, bp) : 0;
        }

        // Returns the number of units actually removed (measured).
        private static int TakeItems(Player player, InvItemBlueprint bp, int amount)
        {
            ItemCollection items = InventoryOf(player);
            if (items == null || bp == null || amount <= 0) return 0;
            int before = ItemCollection.AutoCount(items, bp);
            if (before < amount) return 0;
            ItemCollection.AutoSplit(items, bp, amount);
            int taken = before - ItemCollection.AutoCount(items, bp);
            return taken < 0 ? 0 : Math.Min(taken, amount);
        }

        // Returns the number of units actually added (measured); see CrownAndConsequences.GiveItems.
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

        private void AddOwed(string playerId, string playerName, string item, int amount, int contractId)
        {
            if (amount <= 0 || playerId == null) return;
            // One entry per player and item, so the ledger stays bounded even for players who never return.
            foreach (Owed o in data.Owed)
                if (o.PlayerId == playerId && string.Equals(o.Item, item, StringComparison.OrdinalIgnoreCase))
                {
                    o.Amount += amount;
                    o.ContractId = contractId;
                    if (playerName != null) o.PlayerName = playerName;
                    return;
                }
            data.Owed.Add(new Owed { PlayerId = playerId, PlayerName = playerName, Item = item, Amount = amount, ContractId = contractId });
        }

        // Merges duplicate (player, item) entries left by older versions of the data file.
        private void CompactOwed()
        {
            var merged = new List<Owed>();
            foreach (Owed o in data.Owed)
            {
                Owed same = null;
                foreach (Owed m in merged)
                    if (m.PlayerId == o.PlayerId && string.Equals(m.Item, o.Item, StringComparison.OrdinalIgnoreCase)) { same = m; break; }
                if (same == null) merged.Add(o);
                else same.Amount += o.Amount;
            }
            data.Owed = merged;
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
            bool changed = false, full = false;
            foreach (Owed o in data.Owed.ToArray())
            {
                if (o.PlayerId != id) continue;
                InvItemBlueprint bp = FindItem(o.Item);
                if (bp == null) continue;
                int given = GiveItems(player, bp, o.Amount);
                if (given > 0)
                {
                    o.Amount -= given;
                    changed = true;
                    Reply(player, "Paid", given, bp.Name, o.ContractId);
                    SaveData();                              // record each delivery as it happens
                }
                if (o.Amount > 0) full = true;
            }
            int removed = data.Owed.RemoveAll(IsBrokenOwed);
            if (changed || removed > 0) SaveData();
            if (!full) return;
            DateTime last;
            DateTime now = DateTime.UtcNow;
            if (lastFullWarning.TryGetValue(id, out last) && (now - last).TotalMinutes < 5) return;
            lastFullWarning[id] = now;
            foreach (Owed o in data.Owed)
                if (o.PlayerId == id) { ReplyError(player, "StillOwed", o.Amount, o.Item); break; }
        }

        #endregion

        #region API (plugin.Call)

        // Number of open bounties on a player (0 if none). Non-public on purpose (see header).
        private int GetBountyCount(string playerId)
        {
            int n = 0;
            if (data == null) return n;
            foreach (Contract c in data.Contracts) if (c.Type == TBounty && c.Status == SOpen && c.TargetId == playerId) n++;
            return n;
        }

        private bool IsOutlaw(string playerId)
        {
            Outlaw o;
            return data != null && data.Outlaws.TryGetValue(playerId, out o) && o != null && o.Until > DateTime.UtcNow;
        }

        // Called by RealmLaws when its court sentences a player to outlawry, so the outlaw becomes a bounty target.
        // A court verdict follows a trial and has its own quotas in RealmLaws, so the crown's proclamation cooldown,
        // repeat cooldown and MaxOutlaws cap do not apply. Never shortens an existing sentence. No chronicle line:
        // RealmLaws already writes the verdict. Returns true when the entry was added or extended.
        private bool ProclaimOutlaw(string playerId, string name, int hours, string by)
        {
            if (data == null || string.IsNullOrEmpty(playerId) || hours < 1) return false;
            ulong check;
            if (!ulong.TryParse(playerId, NumberStyles.None, CultureInfo.InvariantCulture, out check)) return false;
            if (hours > 24 * 90) hours = 24 * 90;
            DateTime now = DateTime.UtcNow;
            PruneOutlaws(now);
            DateTime until = now.AddHours(hours);
            Outlaw o;
            if (data.Outlaws.TryGetValue(playerId, out o) && o != null)
            {
                if (o.Until >= until) return false;
                o.Until = until;
                o.Court = true;                            // the longer court sentence now governs; a court pardon lifts it
            }
            else
            {
                data.Outlaws[playerId] = new Outlaw
                {
                    Name = string.IsNullOrEmpty(name) ? playerId : name,
                    Until = until,
                    By = string.IsNullOrEmpty(by) ? "the court" : by,
                    Court = true
                };
            }
            SaveData();
            return true;
        }

        // Called by RealmLaws when its court pardons a player. Lifts only outlawry the court placed here (the crown's
        // own proclamations stay with /contract pardon), and withdraws open bounties as a crown pardon does.
        private bool PardonOutlaw(string playerId)
        {
            Outlaw o;
            if (data == null || string.IsNullOrEmpty(playerId) || !data.Outlaws.TryGetValue(playerId, out o)) return false;
            if (o != null && !o.Court) return false;
            DateTime now = DateTime.UtcNow;
            data.Outlaws.Remove(playerId);
            SetOutlawAgainAfter(playerId, now);
            if (!IsEnemyId(playerId))
                foreach (Contract c in data.Contracts.ToArray())
                    if (c.Type == TBounty && c.Status == SOpen && c.TargetId == playerId)
                        Settle(c, false, "The court pardoned " + (o != null ? o.Name : playerId) + ".");
            SaveData();
            return true;
        }

        #endregion

        #region Helpers

        private string Describe(Contract c)
        {
            if (c.Type == TBounty) return "bounty on " + c.TargetName;
            if (c.Type == TDelivery) return "wants " + c.WantAmount + " " + c.WantItem;
            return "House " + c.House + " hires a sword (" + c.Side + " side)";
        }

        private string StatusText(Contract c)
        {
            if (c.Type == TMerc)
            {
                if (c.Status == SOpen) return "open until " + c.WindowEnd.ToString("ddd HH:mm") + " UTC";
                return "hired: " + c.FulfillerName + ", " + c.Kills + " kills";
            }
            if (c.Status == SAccepted) return "awaiting confirmation";
            return MinutesUntil(c.ExpiresAt) + " min left";
        }

        private string PostedTitle(Contract c)
        {
            if (c.Type == TBounty) return "A price on " + c.TargetName;
            if (c.Type == TDelivery) return c.PosterName + " posts an order";
            return "House " + c.House + " hires swords";
        }

        private string PostedDetail(Contract c)
        {
            string reward = c.RewardAmount + " " + c.RewardItem + (c.Escrowed ? " held by the realm" : " on " + c.PosterName + "'s honour");
            if (c.Type == TBounty) return c.PosterName + " offers " + reward + " for the death of " + c.TargetName + ", enemy of the crown.";
            if (c.Type == TDelivery) return c.PosterName + " offers " + reward + " for " + c.WantAmount + " " + c.WantItem + ".";
            return c.PosterName + " of House " + c.House + " offers " + reward + " to a sword who fights for the house before "
                + c.WindowEnd.ToString("ddd HH:mm") + " UTC.";
        }

        private string FulfilledTitle(Contract c)
        {
            if (c.Type == TBounty) return c.FulfillerName + " collects the price on " + c.TargetName;
            if (c.Type == TDelivery) return c.FulfillerName + " fills an order for " + c.PosterName;
            return c.FulfillerName + " is paid by House " + c.House;
        }

        private static string[] ActorsOf(Contract c)
        {
            var list = new List<string> { c.PosterName };
            if (c.TargetName != null) list.Add(c.TargetName);
            if (c.FulfillerName != null) list.Add(c.FulfillerName);
            return list.ToArray();
        }

        private readonly Queue<DateTime> chronicleTimes = new Queue<DateTime>();

        // Capped per hour: the chronicle keeps only its last MaxEvents lines, so contract churn must never push
        // coronations and rebellions out of the public record. Over the cap the line still goes to the server log.
        private void Chronicle(string type, string title, string detail, string[] actors)
        {
            Puts("[" + type + "] " + title + " - " + detail);
            if (RealmChronicle == null) return;
            DateTime now = DateTime.UtcNow;
            while (chronicleTimes.Count > 0 && (now - chronicleTimes.Peek()).TotalHours >= 1) chronicleTimes.Dequeue();
            if (chronicleTimes.Count >= config.ChronicleMaxPerHour) return;
            chronicleTimes.Enqueue(now);
            RealmChronicle.Call("Log", type, title, detail, actors ?? new string[0]);
        }

        // RealmHouses is authoritative when loaded (same rule as CrownAndConsequences.HouseOf).
        private string HouseOf(ulong playerId)
        {
            if (RealmHouses == null) return null;
            string h = RealmHouses.Call("GetHouse", playerId.ToString()) as string;
            return string.IsNullOrEmpty(h) ? null : h;
        }

        // True when two different houses are bound to each other: one is the other's liege, or they hold a treaty.
        private bool Allied(string a, string b)
        {
            if (a == null || b == null || RealmHouses == null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
            string la = RealmHouses.Call("GetLiege", a) as string, lb = RealmHouses.Call("GetLiege", b) as string;
            if (la != null && string.Equals(la, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (lb != null && string.Equals(lb, a, StringComparison.OrdinalIgnoreCase)) return true;
            object t = RealmHouses.Call("HasTreaty", a, b);
            return t is bool && (bool)t;
        }

        private bool IsHouseLeader(Player player, string house)
        {
            if (RealmHouses == null) return false;
            string leader = RealmHouses.Call("GetHouseLeader", house) as string;
            return leader != null && leader == player.Id.ToString();
        }

        private Contract FindContract(string idText)
        {
            int id;
            if (!int.TryParse((idText ?? "").TrimStart('#'), out id)) return null;
            foreach (Contract c in data.Contracts) if (c.Id == id) return c;
            return null;
        }

        private bool ParseAmount(Player player, string text, int max, out int value)
        {
            if (!int.TryParse(text, out value) || value < 1 || value > max)
            {
                ReplyError(player, "BadNumber", text, 1, max);
                return false;
            }
            return true;
        }

        private static bool ParseUtc(string text, out DateTime value)
        {
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)) return false;
            if (value.Kind == DateTimeKind.Local) value = value.ToUniversalTime();
            else if (value.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return true;
        }

        private bool IsAdmin(Player player)
        {
            return permission.UserHasPermission(player.Id.ToString(), PermAdmin);
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

        #endregion
    }
}
