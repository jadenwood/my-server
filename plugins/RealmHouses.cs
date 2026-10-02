// RealmHouses: player houses, sworn fealty, oathbreaker marks and timed treaties.
//
// Language level: C# 3 syntax only (no expression-bodied members, index initializers, $"", ?. or nameof),
// against the .NET 3.5 API surface, so the file builds with any Oxide compiler generation.
//
// The plugin.Call API (GetHouse, GetLiege, GetMembers, ...) MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.

using System;
using System.Collections.Generic;
using CodeHatch.Common;                       // PlayerExtensions: SendMessage, SendError, GetGuild  [ASM]
using CodeHatch.Engine.Modules.SocialSystem;  // SocialAPI, Members, Member                          [ASM]
using CodeHatch.Engine.Networking;            // Player, Server                                      [ASM]
using CodeHatch.Thrones.SocialSystem;         // GuildScheme, Guild                                  [ASM]
using Oxide.Core;                             // Interface.Oxide.DataFileSystem                      [SRC]
using Oxide.Core.Plugins;                     // Plugin (for [PluginReference])

namespace Oxide.Plugins
{
    [Info("RealmHouses", "Realm", "0.1.0")]
    [Description("Player houses with sworn fealty, oathbreaker marks and timed treaties, layered on the game's guilds")]
    public class RealmHouses : ReignOfKingsPlugin
    {
        private const string PermAdmin = "realmhouses.admin";
        private const string RankLeader = "leader";
        private const string RankOfficer = "officer";
        private const string RankMember = "member";

        // [SRC] Oxide.CSharp src/CSharpPlugin.cs:91 (PluginReferenceAttribute) and Oxide.Core
        // src/Plugins/Plugin.cs:311 (Plugin.Call). If RealmChronicle is absent, events are only printed.
        [PluginReference] private Plugin RealmChronicle;

        private PluginConfig config;
        private StoredData data;

        private readonly Dictionary<string, House> housesByKey = new Dictionary<string, House>();
        private readonly Dictionary<string, House> houseByPlayer = new Dictionary<string, House>();

        // Short-lived offers are kept in memory only; a reload simply drops them.
        private readonly Dictionary<string, Dictionary<string, DateTime>> invites = new Dictionary<string, Dictionary<string, DateTime>>();
        private readonly Dictionary<string, Offer> fealtyRequests = new Dictionary<string, Offer>();   // vassalKey -> liege
        private readonly Dictionary<string, Offer> treatyProposals = new Dictionary<string, Offer>();  // "from|to" -> days
        private readonly Dictionary<string, DateTime> renounceConfirm = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> guildMisses = new Dictionary<string, int>();

        private Timer syncTimer;
        private Timer upkeepTimer;
        private bool syncQueued;
        private readonly Queue<DateTime> foundingNotices = new Queue<DateTime>();   // in memory: losing it on reload is harmless

        #region Config and data

        private class PluginConfig
        {
            public int MaxHouses = 0;                       // 0 = unlimited
            public int MaxMembersPerHouse = 20;             // plugin-managed houses only; guild-bound houses follow the game
            public int MaxOfficersPerHouse = 3;
            public int NameMinLength = 3;
            public int NameMaxLength = 24;
            public int SigilMaxLength = 32;
            public int InviteExpireSeconds = 300;
            public bool RequireLiegeAcceptance = true;
            public int FealtyRequestExpireSeconds = 600;
            public int MaxVassalsPerLiege = 0;              // 0 = unlimited
            public double RenounceCooldownHours = 24;
            public int RenounceConfirmSeconds = 60;
            public int TreatyDefaultDays = 7;
            public int TreatyMaxDays = 30;
            public int MaxTreatiesPerHouse = 5;
            public int TreatyProposalExpireSeconds = 600;
            public int TreatyRenewCooldownHours = 24;       // after a break, the same pair may not sign again for this long
            public int FoundCooldownMinutes = 60;           // per player, between founding houses (anti-spam)
            public bool LinkGameGuilds = true;
            public float GuildSyncIntervalSeconds = 60f;
            public int GuildMissingSyncsBeforeUnlink = 5;
            public bool BroadcastEvents = true;
            public bool ChronicleEnabled = true;
            // Anti-flood: founding and disbanding are free to repeat across many players (and alts), so their realm-wide
            // broadcasts and chronicle lines are capped per hour. Over the cap they still go to the server log.
            // Oath and treaty lines are never capped: they are the record of a house breaking its word.
            public int FoundingNoticesPerHour = 6;
        }

        private class HouseMember
        {
            public string Id;
            public string Name;
            public string Rank;
            public DateTime Joined;
        }

        private class House
        {
            public string Name;
            public string Sigil;
            public DateTime Founded;
            public ulong GuildId;                           // 0 = not bound to a game guild
            public string Liege;                            // house name or null
            public DateTime SwornSince;
            public DateTime SwearBlockedUntil;
            public int OathsBroken;
            public int TreatiesBroken;
            public List<HouseMember> Members = new List<HouseMember>();
        }

        private class Treaty
        {
            public string A;
            public string B;
            public DateTime Signed;
            public DateTime Expires;
        }

        private class PlayerRecord
        {
            public string Name;
            public int OathsBroken;
            public int TreatiesBroken;
        }

        private class StoredData
        {
            public List<House> Houses = new List<House>();
            public List<Treaty> Treaties = new List<Treaty>();
            public Dictionary<string, PlayerRecord> Players = new Dictionary<string, PlayerRecord>();
            public Dictionary<string, DateTime> LastFounded = new Dictionary<string, DateTime>();        // player id -> UTC
            public Dictionary<string, DateTime> TreatyCooldowns = new Dictionary<string, DateTime>();    // pair key -> UTC until
        }

        private class Offer
        {
            public string Target;
            public int Days;
            public DateTime Expires;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "Speaker", "Houses" },
                { "HelpHeader", "Found a house, gather sworn members, and bind it to others by oath and treaty." },
                { "Help1", "  [F4C96D]/house found[FFFFFF] \"<name>\" <sigil> | invite <player> | join <house> | leave | info [house] | list" },
                { "Help2", "  Leaders and officers: [F4C96D]/house kick[FFFFFF] <player> | promote <player> [leader] | demote <player> | link | unlink | disband" },
                { "Help3", "  Fealty: [F4C96D]/swear[FFFFFF] <house> | [F4C96D]/swear accept|deny[FFFFFF] <house> | [F4C96D]/renounce[FFFFFF]. Treaties: [F4C96D]/treaty propose[FFFFFF] <house> [days] | accept | break | list" },
                { "NoPermission", "You may not do that." },
                { "NotInHouse", "You are not sworn to any house." },
                { "AlreadyInHouse", "You already belong to House {0}." },
                { "NoSuchHouse", "No house named '{0}' exists." },
                { "NoSuchPlayer", "No online player matches '{0}'." },
                { "NoSuchMember", "No member of your house matches '{0}'." },
                { "NotLeader", "Only the leader of your house may do that." },
                { "NotOfficer", "Only the leader or an officer of your house may do that." },
                { "BadName", "House names must be {0}-{1} characters: letters, digits, spaces, ' or -, starting with a letter." },
                { "BadSigil", "A sigil must be 1-{0} characters: letters, digits, spaces, ' or -." },
                { "FoundCooldown", "You may found another house in {0}." },
                { "TreatyCooldown", "House {0} broke faith with your house too recently. A new treaty is possible in {1}." },
                { "NameTaken", "That name is already taken or reserved." },
                { "TooManyHouses", "The realm cannot hold more houses." },
                { "GuildBound", "Your guild is already bound to House {0}." },
                { "NotGuildOwner", "Only the owner of your guild may found a house for it." },
                { "Founded", "House {0} is founded under the sigil of {1}." },
                { "FoundedLinked", "Your house is bound to your guild '{0}'. Guild members join it automatically." },
                { "BroadcastFounded", "[D6A043]Herald[FFFFFF]: {0} founds House {1}, bearing {2}." },
                { "GuildManaged", "House {0} is bound to a game guild. Use the in-game guild menu for invites, leaving and kicks." },
                { "HouseFull", "House {0} is full." },
                { "TargetInHouse", "{0} already belongs to a house." },
                { "Invited", "{0} has been invited to House {1}." },
                { "InviteReceived", "You are invited to House {0}. Type [F4C96D]/house join[FFFFFF] {0} within {1}." },
                { "NoInvite", "You have no invitation from House {0}." },
                { "Joined", "You have joined House {0}." },
                { "MemberJoined", "{0} has joined House {1}." },
                { "Left", "You have left House {0}." },
                { "MemberLeft", "{0} has left House {1}." },
                { "Kicked", "{0} has been cast out of House {1}." },
                { "YouWereKicked", "You have been cast out of House {0}." },
                { "CannotTargetSelf", "You cannot do that to yourself." },
                { "CannotKickRank", "You cannot cast out someone of equal or higher rank." },
                { "Promoted", "{0} is now an officer of House {1}." },
                { "AlreadyOfficer", "{0} is already an officer." },
                { "TooManyOfficers", "Your house already has {0} officers." },
                { "NewLeader", "{0} now leads House {1}." },
                { "Demoted", "{0} is no longer an officer." },
                { "NotOfficerTarget", "{0} is not an officer." },
                { "Disbanded", "House {0} has been disbanded." },
                { "BroadcastDisbanded", "[D6A043]Herald[FFFFFF]: House {0} is no more." },
                { "Linked", "House {0} is now bound to the guild '{1}'. Members not in the guild will be removed at the next sync." },
                { "Unlinked", "House {0} is no longer bound to a game guild." },
                { "NoGuild", "You are not in a game guild." },
                { "NotLinked", "Your house is not bound to a game guild." },
                { "Synced", "House membership synced with game guilds." },
                { "Pardoned", "The marks against House {0} and its leader are cleared." },
                { "InfoHeader", "House {0} - sigil: {1}" },
                { "InfoLeader", "  Leader: {0} | Members: {1} | Officers: {2}" },
                { "InfoLiege", "  Sworn to: {0} | Vassals: {1}" },
                { "InfoTreaties", "  Treaties: {0}" },
                { "InfoMarks", "  Marks: oathbreaker x{0}, treaty-breaker x{1} (leader personally: x{2}, x{3})" },
                { "InfoGuild", "  Bound to guild: {0}" },
                { "None", "none" },
                { "ListHeader", "Houses of the realm ({0}):" },
                { "ListLine", "  {0} ({1}) - {2} members{3}" },
                { "ListSworn", " - sworn to {0}" },
                { "ListEmpty", "No houses have been founded." },
                { "AlreadySworn", "Your house is already sworn to House {0}. Renounce first." },
                { "SwearSelf", "A house cannot swear to itself." },
                { "SwearCycle", "House {0} already owes fealty to your house, directly or through its lieges." },
                { "SwearCooldown", "Your house cannot swear a new oath for another {0}." },
                { "TooManyVassals", "House {0} cannot take more vassals." },
                { "NoLeaderOnline", "House {0} has no leader online to hear you." },
                { "FealtyRequested", "Your oath has been offered to House {0}. Its leader must accept within {1}." },
                { "FealtyRequestReceived", "House {0} offers fealty to your house. Type [F4C96D]/swear accept[FFFFFF] {0} or [F4C96D]/swear deny[FFFFFF] {0}." },
                { "NoFealtyRequest", "House {0} has not offered fealty to your house." },
                { "FealtyDenied", "House {0} has refused your oath." },
                { "FealtyDeniedSelf", "You refuse the oath of House {0}." },
                { "Sworn", "House {0} is now sworn to House {1}." },
                { "BroadcastSworn", "[D6A043]Herald[FFFFFF]: House {0} bends the knee to House {1}." },
                { "NotSworn", "Your house is sworn to no one." },
                { "RenounceWarn", "Renouncing your oath to House {0} marks your house and you as oathbreakers. Type [F4C96D]/renounce confirm[FFFFFF] within {1} seconds." },
                { "Renounced", "House {0} has renounced its oath to House {1}." },
                { "BroadcastRenounced", "[D6A043]Herald[FFFFFF]: House {0} breaks its oath to House {1}. Let the realm remember." },
                { "VassalFreed", "Your liege, House {0}, is gone. Your house is sworn to no one." },
                { "TreatyExists", "Your house already has a treaty with House {0}." },
                { "TreatyNone", "Your house has no treaty with House {0}." },
                { "TreatySelf", "A house cannot make a treaty with itself." },
                { "TooManyTreaties", "House {0} already holds the maximum number of treaties." },
                { "TreatyProposed", "You propose a {0}-day treaty to House {1}. Its leader must accept within {2}." },
                { "TreatyProposalReceived", "House {0} proposes a {1}-day treaty. Type [F4C96D]/treaty accept[FFFFFF] {0}." },
                { "NoTreatyProposal", "House {0} has not proposed a treaty to your house." },
                { "TreatySigned", "House {0} and House {1} have signed a treaty for {2} days." },
                { "BroadcastTreatySigned", "[D6A043]Herald[FFFFFF]: House {0} and House {1} sign a treaty." },
                { "TreatyBroken", "House {0} has broken its treaty with House {1}." },
                { "BroadcastTreatyBroken", "[D6A043]Herald[FFFFFF]: House {0} breaks its treaty with House {1}." },
                { "TreatyLapsed", "The treaty between House {0} and House {1} has lapsed." },
                { "TreatyListLine", "  {0} - {1} left" },
                { "TreatyUsage", "Usage: [F4C96D]/treaty propose[FFFFFF] <house> [days] | accept <house> | break <house> | list" },
                { "SwearUsage", "Usage: [F4C96D]/swear[FFFFFF] <house> | [F4C96D]/swear accept[FFFFFF] <house> | [F4C96D]/swear deny[FFFFFF] <house>" }
            }, this);
        }

        private void Init()
        {
            config = Config.ReadObject<PluginConfig>();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            // Also re-sent on hot-load, so destroy existing timers before recreating them.
            if (syncTimer != null) syncTimer.Destroy();
            if (upkeepTimer != null) upkeepTimer.Destroy();
            if (config.LinkGameGuilds) syncTimer = timer.Every(Math.Max(10f, config.GuildSyncIntervalSeconds), SyncLinkedHouses);
            upkeepTimer = timer.Every(30f, Upkeep);
            QueueSync();
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void Unload()
        {
            SaveData();
        }

        private void LoadData()
        {
            bool existed = Interface.Oxide.DataFileSystem.ExistsDatafile(Name);
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch (Exception ex)
            {
                // Refuse to run on a damaged file rather than overwrite every house with an empty list.
                data = null;
                PrintError("Could not read oxide/data/" + Name + ".json: " + ex.Message + ". Fix or remove the file, then reload.");
                throw;
            }
            if (data == null && existed)
            {
                // A file truncated to nothing (e.g. power lost mid-write) parses to null: the same refusal, never a reset.
                PrintError("oxide/data/" + Name + ".json exists but holds no data. Nothing was written. Restore it from a backup or delete it to start empty, then reload.");
                throw new InvalidOperationException("RealmHouses data file is empty");
            }
            if (data == null) data = new StoredData();
            if (data.Houses == null) data.Houses = new List<House>();
            if (data.Treaties == null) data.Treaties = new List<Treaty>();
            if (data.Players == null) data.Players = new Dictionary<string, PlayerRecord>();
            if (data.LastFounded == null) data.LastFounded = new Dictionary<string, DateTime>();
            if (data.TreatyCooldowns == null) data.TreatyCooldowns = new Dictionary<string, DateTime>();
            data.Houses.RemoveAll(IsBrokenHouse);
            data.Treaties.RemoveAll(IsBrokenTreaty);
            housesByKey.Clear();
            houseByPlayer.Clear();
            foreach (House house in data.Houses)
            {
                if (house.Members == null) house.Members = new List<HouseMember>();
                house.Members.RemoveAll(IsBrokenMember);
                housesByKey[Key(house.Name)] = house;
                foreach (HouseMember m in house.Members) houseByPlayer[m.Id] = house;
            }
        }

        private static bool IsBrokenHouse(House h)
        {
            return h == null || string.IsNullOrEmpty(h.Name);
        }

        private static bool IsBrokenTreaty(Treaty t)
        {
            return t == null || t.A == null || t.B == null;
        }

        private static bool IsBrokenMember(HouseMember m)
        {
            return m == null || string.IsNullOrEmpty(m.Id);
        }

        private void SaveData()
        {
            if (data == null) return;                       // never overwrite the file after a failed load
            Interface.Oxide.DataFileSystem.WriteObject(Name, data);
        }

        #endregion

        #region Game hooks

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer) return;
            string id = player.Id.ToString();
            House house = HouseOf(id);
            if (house != null)
            {
                HouseMember m = FindMemberById(house, id);
                if (m != null && m.Name != player.Name) { m.Name = player.Name; SaveData(); }
            }
            PlayerRecord rec;
            if (data.Players.TryGetValue(id, out rec)) rec.Name = player.Name;
        }

        // Guild membership changed in the game. Parameters are omitted on purpose (Oxide drops extra
        // arguments), so no event namespaces are needed; we only schedule a resync after the game applies it.
        private void OnGuildInvitationAccept()
        {
            QueueSync();
        }
        private void OnGuildAbandon()
        {
            QueueSync();
        }
        private void OnGuildBanish()
        {
            QueueSync();
        }

        #endregion

        #region /house

        [ChatCommand("house")]
        private void CmdHouse(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (sub)
            {
                case "found": HouseFound(player, args); break;
                case "invite": HouseInvite(player, args); break;
                case "join": HouseJoin(player, args); break;
                case "leave": HouseLeave(player); break;
                case "kick": HouseKick(player, args); break;
                case "promote": HousePromote(player, args); break;
                case "demote": HouseDemote(player, args); break;
                case "info": HouseInfo(player, args); break;
                case "list": HouseList(player); break;
                case "link": HouseLink(player); break;
                case "unlink": HouseUnlink(player); break;
                case "disband": HouseDisband(player, args); break;
                case "pardon": HousePardon(player, args); break;
                case "sync":
                    if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
                    SyncLinkedHouses();
                    Reply(player, "Synced");
                    break;
                default: ShowHelp(player); break;
            }
        }

        private void HouseFound(Player player, string[] args)
        {
            string id = player.Id.ToString();
            House current = HouseOf(id);
            if (current != null) { Error(player, "AlreadyInHouse", current.Name); return; }
            if (args.Length < 3) { ShowHelp(player); return; }

            string name = CleanText(args[1]);
            string sigil = CleanText(JoinFrom(args, 2));
            if (!ValidName(name)) { Error(player, "BadName", config.NameMinLength, config.NameMaxLength); return; }
            if (!ValidSigil(sigil)) { Error(player, "BadSigil", config.SigilMaxLength); return; }
            if (FindHouse(name) != null || IsReserved(name)) { Error(player, "NameTaken"); return; }
            if (config.MaxHouses > 0 && data.Houses.Count >= config.MaxHouses) { Error(player, "TooManyHouses"); return; }
            DateTime lastFounded;
            if (config.FoundCooldownMinutes > 0 && data.LastFounded.TryGetValue(id, out lastFounded))
            {
                DateTime ready = lastFounded.AddMinutes(config.FoundCooldownMinutes);
                if (ready > DateTime.UtcNow) { Error(player, "FoundCooldown", Duration(ready - DateTime.UtcNow)); return; }
            }

            ulong guildId = 0;
            string guildName = null;
            if (config.LinkGameGuilds)
            {
                Guild guild = player.GetGuild();
                if (guild != null)
                {
                    House bound = HouseByGuild(guild.BaseID);
                    if (bound != null) { Error(player, "GuildBound", bound.Name); return; }
                    if (guild.Members().MemberCount() > 1 && guild.OwnerId != player.Id) { Error(player, "NotGuildOwner"); return; }
                    guildId = guild.BaseID;
                    guildName = guild.Name;
                }
            }

            var house = new House { Name = name, Sigil = sigil, Founded = DateTime.UtcNow, GuildId = guildId };
            data.Houses.Add(house);
            housesByKey[Key(name)] = house;
            AddMember(house, id, player.Name, RankLeader);
            data.LastFounded[id] = DateTime.UtcNow;
            SaveData();

            Reply(player, "Founded", name, sigil);
            if (guildId != 0) { Reply(player, "FoundedLinked", guildName); QueueSync(); }
            if (FoundingNoticeAllowed())
            {
                Broadcast("BroadcastFounded", player.Name, name, sigil);
                Chronicle("house_founded", "House " + name + " is founded",
                    player.Name + " raises the sigil of " + sigil + ".", player.Name);
            }
            else Puts("House " + name + " founded by " + player.Name + " (notice capped: FoundingNoticesPerHour)");
        }

        private void HouseInvite(Player player, string[] args)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            if (!IsOfficerOrLeader(house, player.Id.ToString())) { Error(player, "NotOfficer"); return; }
            if (house.GuildId != 0) { Error(player, "GuildManaged", house.Name); return; }
            if (args.Length < 2) { ShowHelp(player); return; }

            Player target = FindOnlinePlayer(JoinFrom(args, 1));
            if (target == null) { Error(player, "NoSuchPlayer", JoinFrom(args, 1)); return; }
            if (target.Id == player.Id) { Error(player, "CannotTargetSelf"); return; }
            if (HouseOf(target.Id.ToString()) != null) { Error(player, "TargetInHouse", target.Name); return; }
            if (house.Members.Count >= config.MaxMembersPerHouse) { Error(player, "HouseFull", house.Name); return; }

            string tid = target.Id.ToString();
            Dictionary<string, DateTime> mine;
            if (!invites.TryGetValue(tid, out mine)) invites[tid] = mine = new Dictionary<string, DateTime>();
            mine[Key(house.Name)] = DateTime.UtcNow.AddSeconds(config.InviteExpireSeconds);

            Reply(player, "Invited", target.Name, house.Name);
            Reply(target, "InviteReceived", house.Name, Duration(TimeSpan.FromSeconds(config.InviteExpireSeconds)));
        }

        private void HouseJoin(Player player, string[] args)
        {
            string id = player.Id.ToString();
            House current = HouseOf(id);
            if (current != null) { Error(player, "AlreadyInHouse", current.Name); return; }
            if (args.Length < 2) { ShowHelp(player); return; }

            string wanted = JoinFrom(args, 1);
            House house = FindHouse(wanted);
            if (house == null) { Error(player, "NoSuchHouse", wanted); return; }

            Dictionary<string, DateTime> mine;
            DateTime expires;
            if (!invites.TryGetValue(id, out mine) || !mine.TryGetValue(Key(house.Name), out expires) || expires < DateTime.UtcNow)
            {
                Error(player, "NoInvite", house.Name);
                return;
            }
            if (house.GuildId != 0) { Error(player, "GuildManaged", house.Name); return; }
            if (house.Members.Count >= config.MaxMembersPerHouse) { Error(player, "HouseFull", house.Name); return; }

            invites.Remove(id);
            AddMember(house, id, player.Name, RankMember);
            SaveData();
            Reply(player, "Joined", house.Name);
            NotifyHouse(house, id, "MemberJoined", player.Name, house.Name);
        }

        private void HouseLeave(Player player)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            if (house.GuildId != 0) { Error(player, "GuildManaged", house.Name); return; }

            string houseName = house.Name;
            RemoveMember(house, player.Id.ToString());
            SaveData();
            Reply(player, "Left", houseName);
            if (housesByKey.ContainsKey(Key(houseName))) NotifyHouse(house, null, "MemberLeft", player.Name, houseName);
        }

        private void HouseKick(Player player, string[] args)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            string id = player.Id.ToString();
            if (!IsOfficerOrLeader(house, id)) { Error(player, "NotOfficer"); return; }
            if (house.GuildId != 0) { Error(player, "GuildManaged", house.Name); return; }
            if (args.Length < 2) { ShowHelp(player); return; }

            HouseMember target = FindMemberByName(house, JoinFrom(args, 1));
            if (target == null) { Error(player, "NoSuchMember", JoinFrom(args, 1)); return; }
            if (target.Id == id) { Error(player, "CannotTargetSelf"); return; }
            if (RankValue(target.Rank) >= RankValue(FindMemberById(house, id).Rank)) { Error(player, "CannotKickRank"); return; }

            RemoveMember(house, target.Id);
            SaveData();
            NotifyHouse(house, null, "Kicked", target.Name, house.Name);
            NotifyPlayer(target.Id, "YouWereKicked", house.Name);
        }

        private void HousePromote(Player player, string[] args)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            string id = player.Id.ToString();
            if (!IsLeader(house, id)) { Error(player, "NotLeader"); return; }
            if (args.Length < 2) { ShowHelp(player); return; }

            bool toLeader = args.Length >= 3 && args[args.Length - 1].ToLowerInvariant() == "leader";
            string who = toLeader ? JoinRange(args, 1, args.Length - 2) : JoinFrom(args, 1);
            HouseMember target = FindMemberByName(house, who);
            if (target == null) { Error(player, "NoSuchMember", who); return; }
            if (target.Id == id) { Error(player, "CannotTargetSelf"); return; }

            if (toLeader)
            {
                FindMemberById(house, id).Rank = RankOfficer;
                target.Rank = RankLeader;
                SaveData();
                NotifyHouse(house, null, "NewLeader", target.Name, house.Name);
                return;
            }
            if (target.Rank != RankMember) { Error(player, "AlreadyOfficer", target.Name); return; }
            if (CountRank(house, RankOfficer) >= config.MaxOfficersPerHouse) { Error(player, "TooManyOfficers", config.MaxOfficersPerHouse); return; }
            target.Rank = RankOfficer;
            SaveData();
            NotifyHouse(house, null, "Promoted", target.Name, house.Name);
        }

        private void HouseDemote(Player player, string[] args)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            if (!IsLeader(house, player.Id.ToString())) { Error(player, "NotLeader"); return; }
            if (args.Length < 2) { ShowHelp(player); return; }

            HouseMember target = FindMemberByName(house, JoinFrom(args, 1));
            if (target == null) { Error(player, "NoSuchMember", JoinFrom(args, 1)); return; }
            if (target.Rank != RankOfficer) { Error(player, "NotOfficerTarget", target.Name); return; }
            target.Rank = RankMember;
            SaveData();
            NotifyHouse(house, null, "Demoted", target.Name);
        }

        private void HouseInfo(Player player, string[] args)
        {
            House house;
            if (args.Length >= 2)
            {
                house = FindHouse(JoinFrom(args, 1));
                if (house == null) { Error(player, "NoSuchHouse", JoinFrom(args, 1)); return; }
            }
            else
            {
                house = RequireHouse(player);
                if (house == null) return;
            }

            string none = Msg("None", player);
            HouseMember leader = LeaderOf(house);
            var officers = new List<string>();
            foreach (HouseMember m in house.Members) if (m.Rank == RankOfficer) officers.Add(m.Name);
            List<string> vassals = VassalsOf(house.Name);
            var treaties = new List<string>();
            foreach (Treaty t in TreatiesOf(house.Name)) treaties.Add(Other(t, house.Name) + " (" + Duration(t.Expires - DateTime.UtcNow) + ")");
            PlayerRecord rec = leader != null ? GetRecord(leader.Id, null) : null;

            Reply(player, "InfoHeader", house.Name, house.Sigil);
            Reply(player, "InfoLeader", leader != null ? leader.Name : none, house.Members.Count, officers.Count > 0 ? string.Join(", ", officers.ToArray()) : none);
            Reply(player, "InfoLiege", house.Liege ?? none, vassals.Count > 0 ? string.Join(", ", vassals.ToArray()) : none);
            Reply(player, "InfoTreaties", treaties.Count > 0 ? string.Join(", ", treaties.ToArray()) : none);
            Reply(player, "InfoMarks", house.OathsBroken, house.TreatiesBroken, rec != null ? rec.OathsBroken : 0, rec != null ? rec.TreatiesBroken : 0);
            if (house.GuildId != 0)
            {
                Guild guild = Guilds() != null ? Guilds().TryGetGuild(house.GuildId) : null;
                Reply(player, "InfoGuild", guild != null ? guild.Name : house.GuildId.ToString());
            }
        }

        private void HouseList(Player player)
        {
            if (data.Houses.Count == 0) { Reply(player, "ListEmpty"); return; }
            Reply(player, "ListHeader", data.Houses.Count);
            foreach (House h in data.Houses)
            {
                string sworn = h.Liege != null ? Msg("ListSworn", player, h.Liege) : "";
                Reply(player, "ListLine", HouseTint(h.Name), h.Sigil, h.Members.Count, sworn);
            }
        }

        private void HouseLink(Player player)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            if (!IsLeader(house, player.Id.ToString())) { Error(player, "NotLeader"); return; }
            if (!config.LinkGameGuilds) { Error(player, "NoPermission"); return; }

            Guild guild = player.GetGuild();
            if (guild == null) { Error(player, "NoGuild"); return; }
            House bound = HouseByGuild(guild.BaseID);
            if (bound != null && bound != house) { Error(player, "GuildBound", bound.Name); return; }
            if (guild.Members().MemberCount() > 1 && guild.OwnerId != player.Id) { Error(player, "NotGuildOwner"); return; }

            house.GuildId = guild.BaseID;
            guildMisses.Remove(Key(house.Name));
            SaveData();
            Reply(player, "Linked", house.Name, guild.Name);
            QueueSync();
        }

        private void HouseUnlink(Player player)
        {
            House house = RequireHouse(player);
            if (house == null) return;
            if (!IsLeader(house, player.Id.ToString()) && !IsAdmin(player)) { Error(player, "NotLeader"); return; }
            if (house.GuildId == 0) { Error(player, "NotLinked"); return; }
            house.GuildId = 0;
            SaveData();
            Reply(player, "Unlinked", house.Name);
        }

        private void HouseDisband(Player player, string[] args)
        {
            House house;
            if (args.Length >= 2)
            {
                if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
                house = FindHouse(JoinFrom(args, 1));
                if (house == null) { Error(player, "NoSuchHouse", JoinFrom(args, 1)); return; }
            }
            else
            {
                house = RequireHouse(player);
                if (house == null) return;
                if (!IsLeader(house, player.Id.ToString())) { Error(player, "NotLeader"); return; }
            }
            string name = house.Name;
            DisbandHouse(house);
            SaveData();
            Reply(player, "Disbanded", name);
        }

        private void HousePardon(Player player, string[] args)
        {
            if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
            if (args.Length < 2) { ShowHelp(player); return; }
            House house = FindHouse(JoinFrom(args, 1));
            if (house == null) { Error(player, "NoSuchHouse", JoinFrom(args, 1)); return; }
            house.OathsBroken = 0;
            house.TreatiesBroken = 0;
            house.SwearBlockedUntil = DateTime.MinValue;
            HouseMember leader = LeaderOf(house);
            if (leader != null) data.Players.Remove(leader.Id);
            SaveData();
            Reply(player, "Pardoned", house.Name);
        }

        #endregion

        #region /swear and /renounce

        [ChatCommand("swear")]
        private void CmdSwear(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (args.Length < 1) { Reply(player, "SwearUsage"); return; }
            House house = RequireHouse(player);
            if (house == null) return;
            if (!IsLeader(house, player.Id.ToString())) { Error(player, "NotLeader"); return; }

            string sub = args[0].ToLowerInvariant();
            if ((sub == "accept" || sub == "deny") && args.Length >= 2)
            {
                House vassal = FindHouse(JoinFrom(args, 1));
                if (vassal == null) { Error(player, "NoSuchHouse", JoinFrom(args, 1)); return; }
                Offer req;
                if (!fealtyRequests.TryGetValue(Key(vassal.Name), out req) || req.Target != Key(house.Name) || req.Expires < DateTime.UtcNow)
                {
                    Error(player, "NoFealtyRequest", vassal.Name);
                    return;
                }
                fealtyRequests.Remove(Key(vassal.Name));
                if (sub == "deny")
                {
                    Reply(player, "FealtyDeniedSelf", vassal.Name);
                    NotifyLeader(vassal, "FealtyDenied", house.Name);
                    return;
                }
                if (!CanSwear(player, vassal, house)) return;
                Swear(vassal, house);
                return;
            }

            string wanted = JoinFrom(args, 0);
            House liege = FindHouse(wanted);
            if (liege == null) { Error(player, "NoSuchHouse", wanted); return; }
            if (!CanSwear(player, house, liege)) return;

            if (!config.RequireLiegeAcceptance) { Swear(house, liege); return; }

            Player liegeLeader = OnlineLeader(liege);
            if (liegeLeader == null) { Error(player, "NoLeaderOnline", liege.Name); return; }
            fealtyRequests[Key(house.Name)] = new Offer
            {
                Target = Key(liege.Name),
                Expires = DateTime.UtcNow.AddSeconds(config.FealtyRequestExpireSeconds)
            };
            Reply(player, "FealtyRequested", liege.Name, Duration(TimeSpan.FromSeconds(config.FealtyRequestExpireSeconds)));
            Reply(liegeLeader, "FealtyRequestReceived", house.Name);
        }

        // Reports the reason to `player` and returns false when the oath is not allowed.
        private bool CanSwear(Player player, House vassal, House liege)
        {
            if (vassal == liege) { Error(player, "SwearSelf"); return false; }
            if (vassal.Liege != null) { Error(player, "AlreadySworn", vassal.Liege); return false; }
            if (vassal.SwearBlockedUntil > DateTime.UtcNow) { Error(player, "SwearCooldown", Duration(vassal.SwearBlockedUntil - DateTime.UtcNow)); return false; }
            if (OwesFealtyTo(liege, vassal)) { Error(player, "SwearCycle", liege.Name); return false; }
            if (config.MaxVassalsPerLiege > 0 && VassalsOf(liege.Name).Count >= config.MaxVassalsPerLiege) { Error(player, "TooManyVassals", liege.Name); return false; }
            return true;
        }

        private void Swear(House vassal, House liege)
        {
            vassal.Liege = liege.Name;
            vassal.SwornSince = DateTime.UtcNow;
            SaveData();

            NotifyHouse(vassal, null, "Sworn", vassal.Name, liege.Name);
            NotifyHouse(liege, null, "Sworn", vassal.Name, liege.Name);
            Broadcast("BroadcastSworn", vassal.Name, liege.Name);
            Chronicle("oath_sworn", "House " + vassal.Name + " swears fealty to House " + liege.Name,
                "House " + vassal.Name + " (" + vassal.Sigil + ") bends the knee to House " + liege.Name + " (" + liege.Sigil + ").",
                LeaderNames(vassal, liege));
        }

        [ChatCommand("renounce")]
        private void CmdRenounce(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            House house = RequireHouse(player);
            if (house == null) return;
            string id = player.Id.ToString();
            if (!IsLeader(house, id)) { Error(player, "NotLeader"); return; }
            if (house.Liege == null) { Error(player, "NotSworn"); return; }

            DateTime until;
            bool confirmed = args.Length > 0 && args[0].ToLowerInvariant() == "confirm"
                && renounceConfirm.TryGetValue(id, out until) && until >= DateTime.UtcNow;
            if (!confirmed)
            {
                renounceConfirm[id] = DateTime.UtcNow.AddSeconds(config.RenounceConfirmSeconds);
                Reply(player, "RenounceWarn", house.Liege, config.RenounceConfirmSeconds);
                return;
            }
            renounceConfirm.Remove(id);

            string liegeName = house.Liege;
            House liege = FindHouse(liegeName);
            house.Liege = null;
            house.OathsBroken++;
            house.SwearBlockedUntil = DateTime.UtcNow.AddHours(config.RenounceCooldownHours);
            GetRecord(id, player.Name).OathsBroken++;
            SaveData();

            NotifyHouse(house, null, "Renounced", house.Name, liegeName);
            if (liege != null) NotifyHouse(liege, null, "Renounced", house.Name, liegeName);
            Broadcast("BroadcastRenounced", house.Name, liegeName);
            Chronicle("oath_broken", "House " + house.Name + " renounces its oath to House " + liegeName,
                player.Name + " breaks the oath of House " + house.Name + ". The house now bears the oathbreaker mark (x" + house.OathsBroken + ").",
                liege != null ? LeaderNames(house, liege) : new[] { player.Name });
        }

        #endregion

        #region /treaty

        [ChatCommand("treaty")]
        private void CmdTreaty(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer) return;
            if (args.Length < 1) { Reply(player, "TreatyUsage"); return; }
            House house = RequireHouse(player);
            if (house == null) return;
            string sub = args[0].ToLowerInvariant();

            if (sub == "list")
            {
                List<Treaty> mine = TreatiesOf(house.Name);
                if (mine.Count == 0) { Reply(player, "InfoTreaties", Msg("None", player)); return; }
                foreach (Treaty t in mine) Reply(player, "TreatyListLine", Other(t, house.Name), Duration(t.Expires - DateTime.UtcNow));
                return;
            }

            if (!IsLeader(house, player.Id.ToString())) { Error(player, "NotLeader"); return; }
            if (args.Length < 2) { Reply(player, "TreatyUsage"); return; }

            int days = config.TreatyDefaultDays;
            string otherName = JoinFrom(args, 1);
            if (sub == "propose" && args.Length >= 3)
            {
                int parsed;
                if (int.TryParse(args[args.Length - 1], out parsed))
                {
                    days = Math.Max(1, Math.Min(config.TreatyMaxDays, parsed));
                    otherName = JoinRange(args, 1, args.Length - 2);
                }
            }
            House other = FindHouse(otherName);
            if (other == null) { Error(player, "NoSuchHouse", otherName); return; }
            if (other == house) { Error(player, "TreatySelf"); return; }

            switch (sub)
            {
                case "propose": TreatyPropose(player, house, other, days); break;
                case "accept": TreatyAccept(player, house, other); break;
                case "break": TreatyBreak(player, house, other); break;
                default: Reply(player, "TreatyUsage"); break;
            }
        }

        private void TreatyPropose(Player player, House house, House other, int days)
        {
            if (FindTreaty(house.Name, other.Name) != null) { Error(player, "TreatyExists", other.Name); return; }
            if (TreatyOnCooldown(player, house, other)) return;
            if (!TreatyRoom(player, house, other)) return;
            Player otherLeader = OnlineLeader(other);
            if (otherLeader == null) { Error(player, "NoLeaderOnline", other.Name); return; }

            treatyProposals[Key(house.Name) + "|" + Key(other.Name)] = new Offer
            {
                Target = Key(other.Name),
                Days = days,
                Expires = DateTime.UtcNow.AddSeconds(config.TreatyProposalExpireSeconds)
            };
            Reply(player, "TreatyProposed", days, other.Name, Duration(TimeSpan.FromSeconds(config.TreatyProposalExpireSeconds)));
            Reply(otherLeader, "TreatyProposalReceived", house.Name, days);
        }

        private void TreatyAccept(Player player, House house, House proposer)
        {
            string key = Key(proposer.Name) + "|" + Key(house.Name);
            Offer offer;
            if (!treatyProposals.TryGetValue(key, out offer) || offer.Expires < DateTime.UtcNow)
            {
                Error(player, "NoTreatyProposal", proposer.Name);
                return;
            }
            if (FindTreaty(house.Name, proposer.Name) != null) { Error(player, "TreatyExists", proposer.Name); return; }
            if (TreatyOnCooldown(player, house, proposer)) return;
            if (!TreatyRoom(player, house, proposer)) return;
            treatyProposals.Remove(key);

            DateTime now = DateTime.UtcNow;
            data.Treaties.Add(new Treaty { A = proposer.Name, B = house.Name, Signed = now, Expires = now.AddDays(offer.Days) });
            SaveData();

            NotifyHouse(proposer, null, "TreatySigned", proposer.Name, house.Name, offer.Days);
            NotifyHouse(house, null, "TreatySigned", proposer.Name, house.Name, offer.Days);
            Broadcast("BroadcastTreatySigned", proposer.Name, house.Name);
            Chronicle("treaty_signed", "House " + proposer.Name + " and House " + house.Name + " sign a treaty",
                "The treaty holds for " + offer.Days + " days, until " + now.AddDays(offer.Days).ToString("yyyy-MM-dd") + " (UTC).",
                LeaderNames(proposer, house));
        }

        private void TreatyBreak(Player player, House house, House other)
        {
            Treaty treaty = FindTreaty(house.Name, other.Name);
            if (treaty == null) { Error(player, "TreatyNone", other.Name); return; }
            data.Treaties.Remove(treaty);
            if (config.TreatyRenewCooldownHours > 0)
                data.TreatyCooldowns[PairKey(house.Name, other.Name)] = DateTime.UtcNow.AddHours(config.TreatyRenewCooldownHours);
            house.TreatiesBroken++;
            GetRecord(player.Id.ToString(), player.Name).TreatiesBroken++;
            SaveData();

            NotifyHouse(house, null, "TreatyBroken", house.Name, other.Name);
            NotifyHouse(other, null, "TreatyBroken", house.Name, other.Name);
            Broadcast("BroadcastTreatyBroken", house.Name, other.Name);
            Chronicle("treaty_broken", "House " + house.Name + " breaks its treaty with House " + other.Name,
                player.Name + " tears up the treaty before its term. House " + house.Name + " now bears the treaty-breaker mark (x" + house.TreatiesBroken + ").",
                LeaderNames(house, other));
        }

        private bool TreatyOnCooldown(Player player, House mine, House other)
        {
            DateTime until;
            if (!data.TreatyCooldowns.TryGetValue(PairKey(mine.Name, other.Name), out until) || until <= DateTime.UtcNow) return false;
            Error(player, "TreatyCooldown", other.Name, Duration(until - DateTime.UtcNow));
            return true;
        }

        private static string PairKey(string a, string b)
        {
            string ka = Key(a), kb = Key(b);
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        private bool TreatyRoom(Player player, House a, House b)
        {
            if (TreatiesOf(a.Name).Count >= config.MaxTreatiesPerHouse) { Error(player, "TooManyTreaties", a.Name); return false; }
            if (TreatiesOf(b.Name).Count >= config.MaxTreatiesPerHouse) { Error(player, "TooManyTreaties", b.Name); return false; }
            return true;
        }

        #endregion

        #region Guild sync and upkeep

        private void QueueSync()
        {
            if (!config.LinkGameGuilds || syncQueued) return;
            syncQueued = true;
            // Give the game a moment to apply the guild change before reading it back.
            timer.Once(3f, () => { syncQueued = false; SyncLinkedHouses(); });
        }

        // Game guild membership is authoritative for guild-bound houses: add guild members, drop non-members.
        private void SyncLinkedHouses()
        {
            if (!config.LinkGameGuilds) return;
            GuildScheme scheme = Guilds();
            if (scheme == null) return;
            bool changed = false;

            foreach (House house in new List<House>(data.Houses))
            {
                if (house.GuildId == 0 || !data.Houses.Contains(house)) continue;
                string key = Key(house.Name);

                var roster = new Dictionary<string, string>();
                Guild guild = scheme.TryGetGuild(house.GuildId);
                if (guild != null)
                    foreach (var m in guild.Members().GetAllMembers()) roster[m.PlayerId.ToString()] = m.Name;

                if (roster.Count == 0)
                {
                    // Tolerate transient misses (e.g. during world load) before letting go of the guild.
                    int misses;
                    guildMisses.TryGetValue(key, out misses);
                    guildMisses[key] = ++misses;
                    if (misses >= config.GuildMissingSyncsBeforeUnlink)
                    {
                        PrintWarning("Guild " + house.GuildId + " for House " + house.Name + " is gone; unbinding the house.");
                        house.GuildId = 0;
                        guildMisses.Remove(key);
                        changed = true;
                    }
                    continue;
                }
                guildMisses.Remove(key);

                foreach (KeyValuePair<string, string> entry in roster)
                {
                    HouseMember existing = FindMemberById(house, entry.Key);
                    if (existing != null)
                    {
                        if (!string.IsNullOrEmpty(entry.Value) && existing.Name != entry.Value) { existing.Name = entry.Value; changed = true; }
                        continue;
                    }
                    House previous = HouseOf(entry.Key);
                    if (previous != null) RemoveMember(previous, entry.Key);
                    AddMember(house, entry.Key, entry.Value, RankMember);
                    changed = true;
                }

                foreach (HouseMember m in new List<HouseMember>(house.Members))
                {
                    if (roster.ContainsKey(m.Id)) continue;
                    RemoveMember(house, m.Id);
                    changed = true;
                }

                if (data.Houses.Contains(house) && LeaderOf(house) == null && house.Members.Count > 0)
                {
                    PromoteSuccessor(house);
                    changed = true;
                }
            }

            if (changed) SaveData();
        }

        private void Upkeep()
        {
            DateTime now = DateTime.UtcNow;
            bool changed = false;
            foreach (Treaty t in new List<Treaty>(data.Treaties))
            {
                if (t.Expires > now) continue;
                data.Treaties.Remove(t);
                changed = true;
                House a = FindHouse(t.A), b = FindHouse(t.B);
                if (a != null) NotifyHouse(a, null, "TreatyLapsed", t.A, t.B);
                if (b != null) NotifyHouse(b, null, "TreatyLapsed", t.A, t.B);
            }
            foreach (string k in new List<string>(data.TreatyCooldowns.Keys))
                if (data.TreatyCooldowns[k] <= now) { data.TreatyCooldowns.Remove(k); changed = true; }
            foreach (string k in new List<string>(data.LastFounded.Keys))
                if (data.LastFounded[k].AddMinutes(Math.Max(0, config.FoundCooldownMinutes)) <= now) { data.LastFounded.Remove(k); changed = true; }
            if (changed) SaveData();

            PruneOffers(fealtyRequests, now);
            PruneOffers(treatyProposals, now);
            foreach (string id in new List<string>(invites.Keys))
            {
                Dictionary<string, DateTime> mine = invites[id];
                foreach (string k in new List<string>(mine.Keys)) if (mine[k] < now) mine.Remove(k);
                if (mine.Count == 0) invites.Remove(id);
            }
            foreach (string id in new List<string>(renounceConfirm.Keys))
                if (renounceConfirm[id] < now) renounceConfirm.Remove(id);
        }

        private static void PruneOffers(Dictionary<string, Offer> offers, DateTime now)
        {
            foreach (string k in new List<string>(offers.Keys)) if (offers[k].Expires < now) offers.Remove(k);
        }

        #endregion

        #region Membership core

        private void AddMember(House house, string id, string name, string rank)
        {
            house.Members.Add(new HouseMember { Id = id, Name = name ?? id, Rank = rank, Joined = DateTime.UtcNow });
            houseByPlayer[id] = house;
        }

        // Removes a member, hands leadership on if needed, and disbands the house when it empties.
        private void RemoveMember(House house, string id)
        {
            HouseMember m = FindMemberById(house, id);
            if (m == null) return;
            house.Members.Remove(m);
            House indexed;
            if (houseByPlayer.TryGetValue(id, out indexed) && indexed == house) houseByPlayer.Remove(id);

            if (house.Members.Count == 0) { DisbandHouse(house); return; }
            if (m.Rank == RankLeader) PromoteSuccessor(house);
        }

        private void PromoteSuccessor(House house)
        {
            HouseMember next = null;
            if (house.GuildId != 0 && Guilds() != null)
            {
                Guild guild = Guilds().TryGetGuild(house.GuildId);
                if (guild != null) next = FindMemberById(house, guild.OwnerId.ToString());
            }
            if (next == null) foreach (HouseMember m in house.Members) if (m.Rank == RankOfficer) { next = m; break; }
            if (next == null && house.Members.Count > 0) next = house.Members[0];
            if (next == null) return;

            foreach (HouseMember m in house.Members) if (m.Rank == RankLeader) m.Rank = RankOfficer;
            next.Rank = RankLeader;
            NotifyHouse(house, null, "NewLeader", next.Name, house.Name);
        }

        private void DisbandHouse(House house)
        {
            string key = Key(house.Name);
            data.Houses.Remove(house);
            housesByKey.Remove(key);
            foreach (HouseMember m in house.Members)
            {
                House indexed;
                if (houseByPlayer.TryGetValue(m.Id, out indexed) && indexed == house) houseByPlayer.Remove(m.Id);
            }
            data.Treaties.RemoveAll(t => Key(t.A) == key || Key(t.B) == key);
            foreach (House vassal in data.Houses)
            {
                if (vassal.Liege == null || Key(vassal.Liege) != key) continue;
                vassal.Liege = null;
                NotifyHouse(vassal, null, "VassalFreed", house.Name);
            }
            fealtyRequests.Remove(key);
            foreach (string k in new List<string>(fealtyRequests.Keys))
                if (fealtyRequests[k].Target == key) fealtyRequests.Remove(k);
            foreach (string k in new List<string>(treatyProposals.Keys))
                if (k.StartsWith(key + "|") || k.EndsWith("|" + key)) treatyProposals.Remove(k);
            foreach (Dictionary<string, DateTime> mine in invites.Values) mine.Remove(key);
            guildMisses.Remove(key);
            if (FoundingNoticeAllowed()) Broadcast("BroadcastDisbanded", house.Name);
            else Puts("House " + house.Name + " disbanded (notice capped: FoundingNoticesPerHour)");
        }

        #endregion

        #region Lookups

        private GuildScheme Guilds()
        {
            return SocialAPI.Get<GuildScheme>();
        }

        private static string Key(string name)
        {
            return (name ?? "").Trim().ToLowerInvariant();
        }

        private House FindHouse(string name)
        {
            House house;
            return housesByKey.TryGetValue(Key(name), out house) ? house : null;
        }

        private House HouseOf(string playerId)
        {
            House house;
            return playerId != null && houseByPlayer.TryGetValue(playerId, out house) ? house : null;
        }

        private House HouseByGuild(ulong guildId)
        {
            foreach (House h in data.Houses) if (h.GuildId == guildId) return h;
            return null;
        }

        private House RequireHouse(Player player)
        {
            House house = HouseOf(player.Id.ToString());
            if (house == null) Error(player, "NotInHouse");
            return house;
        }

        private static HouseMember FindMemberById(House house, string id)
        {
            foreach (HouseMember m in house.Members) if (m.Id == id) return m;
            return null;
        }

        // Exact name or id first, then a unique prefix match.
        private static HouseMember FindMemberByName(House house, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            HouseMember prefix = null;
            int prefixCount = 0;
            foreach (HouseMember m in house.Members)
            {
                if (m.Id == text || string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)) return m;
                if (m.Name != null && m.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) { prefix = m; prefixCount++; }
            }
            return prefixCount == 1 ? prefix : null;
        }

        private static Player FindOnlinePlayer(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            Player prefix = null;
            int prefixCount = 0;
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                if (p.Id.ToString() == text || string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase)) return p;
                if (p.Name != null && p.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) { prefix = p; prefixCount++; }
            }
            return prefixCount == 1 ? prefix : null;
        }

        private static Player FindOnlineById(string id)
        {
            foreach (Player p in Server.ClientPlayers)
                if (p != null && !p.IsServer && p.Id.ToString() == id) return p;
            return null;
        }

        private static HouseMember LeaderOf(House house)
        {
            foreach (HouseMember m in house.Members) if (m.Rank == RankLeader) return m;
            return null;
        }

        private static Player OnlineLeader(House house)
        {
            HouseMember leader = LeaderOf(house);
            return leader != null ? FindOnlineById(leader.Id) : null;
        }

        private bool IsLeader(House house, string id)
        {
            HouseMember m = FindMemberById(house, id);
            return m != null && m.Rank == RankLeader;
        }

        private bool IsOfficerOrLeader(House house, string id)
        {
            HouseMember m = FindMemberById(house, id);
            return m != null && (m.Rank == RankLeader || m.Rank == RankOfficer);
        }

        private static int RankValue(string rank)
        {
            return rank == RankLeader ? 2 : rank == RankOfficer ? 1 : 0;
        }

        private static int CountRank(House house, string rank)
        {
            int n = 0;
            foreach (HouseMember m in house.Members) if (m.Rank == rank) n++;
            return n;
        }

        private List<string> VassalsOf(string houseName)
        {
            var list = new List<string>();
            string key = Key(houseName);
            foreach (House h in data.Houses) if (h.Liege != null && Key(h.Liege) == key) list.Add(h.Name);
            return list;
        }

        // True if `house` is sworn to `target` directly or through a chain of lieges.
        private bool OwesFealtyTo(House house, House target)
        {
            var seen = new HashSet<string>();
            House cur = house;
            while (cur != null && cur.Liege != null && seen.Add(Key(cur.Name)))
            {
                if (Key(cur.Liege) == Key(target.Name)) return true;
                cur = FindHouse(cur.Liege);
            }
            return false;
        }

        private List<Treaty> TreatiesOf(string houseName)
        {
            var list = new List<Treaty>();
            string key = Key(houseName);
            DateTime now = DateTime.UtcNow;
            foreach (Treaty t in data.Treaties)
                if (t.Expires > now && (Key(t.A) == key || Key(t.B) == key)) list.Add(t);
            return list;
        }

        private Treaty FindTreaty(string a, string b)
        {
            string kb = Key(b);
            foreach (Treaty t in TreatiesOf(a)) if (Key(t.A) == kb || Key(t.B) == kb) return t;
            return null;
        }

        private static string Other(Treaty t, string houseName)
        {
            return Key(t.A) == Key(houseName) ? t.B : t.A;
        }

        private PlayerRecord GetRecord(string id, string name)
        {
            PlayerRecord rec;
            if (!data.Players.TryGetValue(id, out rec))
            {
                if (name == null) return null;
                data.Players[id] = rec = new PlayerRecord { Name = name };
            }
            if (name != null) rec.Name = name;
            return rec;
        }

        private static string[] LeaderNames(House a, House b)
        {
            var names = new List<string>();
            HouseMember la = LeaderOf(a), lb = LeaderOf(b);
            if (la != null) names.Add(la.Name);
            if (lb != null) names.Add(lb.Name);
            return names.ToArray();
        }

        #endregion

        #region Validation and text

        private bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < config.NameMinLength || name.Length > config.NameMaxLength) return false;
            return char.IsLetter(name[0]) && AllowedChars(name);
        }

        private bool ValidSigil(string sigil)
        {
            return !string.IsNullOrEmpty(sigil) && sigil.Length <= config.SigilMaxLength && AllowedChars(sigil);
        }

        // Letters, digits, space, apostrophe and hyphen only: keeps out [RRGGBB] colour tags and format braces.
        private static bool AllowedChars(string s)
        {
            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '\'' && c != '-') return false;
            return true;
        }

        private static bool IsReserved(string name)
        {
            string k = Key(name);
            return k == "accept" || k == "deny" || k == "confirm" || k == "list" || k == "none";
        }

        private static string CleanText(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        private static string JoinFrom(string[] args, int start)
        {
            return JoinRange(args, start, args.Length - 1);
        }

        private static string JoinRange(string[] args, int start, int end)
        {
            if (start > end || start >= args.Length) return "";
            var parts = new string[end - start + 1];
            Array.Copy(args, start, parts, 0, parts.Length);
            return string.Join(" ", parts).Trim();
        }

        private static string Duration(TimeSpan span)
        {
            if (span.TotalSeconds < 0) span = TimeSpan.Zero;
            if (span.TotalDays >= 1) return (int)span.TotalDays + "d " + span.Hours + "h";
            if (span.TotalHours >= 1) return (int)span.TotalHours + "h " + span.Minutes + "m";
            if (span.TotalMinutes >= 1) return (int)span.TotalMinutes + "m";
            return (int)span.TotalSeconds + "s";
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


        // A house name in its chat colour. The six great houses of Ostreval keep their own (art/palette.json
        // "discordRole", chosen for dark backgrounds); any other house gets one of the six by a stable hash of its
        // name, so it always shows in the same colour. Same table in every plugin that uses it (check.mjs).
        private static readonly string[] HouseTintNames = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };
        private static readonly string[] HouseTintColours = { "C58FC0", "E08A5C", "8FB0BF", "B8B85A", "EC8A3C", "6FBF85" };

        private static string HouseTint(string house)
        {
            if (string.IsNullOrEmpty(house)) return house;
            string key = house.Trim().ToLowerInvariant();
            int i = Array.IndexOf(HouseTintNames, key);
            if (i < 0)
            {
                uint h = 2166136261;
                foreach (char c in key) { h ^= c; h *= 16777619; }
                i = (int)(h % (uint)HouseTintColours.Length);
            }
            return "[" + HouseTintColours[i] + "]" + house + "[FFFFFF]";
        }

        #endregion

        #region Messaging

        private bool IsAdmin(Player player)
        {
            return permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        // Lang strings are ours; player text only ever goes in as an argument, never as the format string.
        private string Msg(string key, Player player, params object[] args)
        {
            string text = lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
            return args.Length > 0 ? string.Format(text, args) : text;
        }

        // Single-string overloads only (see oxide-rok-api.md 4.2 on brace safety).
        private void Reply(Player player, string key, params object[] args)
        {
            player.SendMessage(Styled(Msg("Speaker", player), ToneOf(key), TintHouses(Msg(key, player, args))));
        }

        private void Error(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, TintHouses(Msg(key, player, args))));
        }

        private void ShowHelp(Player player)
        {
            Reply(player, "HelpHeader");
            for (int i = 1; i <= 3; i++) Reply(player, "Help" + i);
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Founded", "FoundedLinked", "Invited", "Joined", "Left", "Linked", "Unlinked", "Synced", "Pardoned", "FealtyRequested",
            "TreatyProposed", "Sworn", "TreatySigned"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string>
        {
            "RenounceWarn", "YouWereKicked", "VassalFreed", "TreatyLapsed", "TreatyBroken", "Renounced", "FealtyDenied", "Disbanded"
        };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        // Colours every "House <name>" of a standing house in a chat line (chat style). Log lines stay plain.
        private string TintHouses(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("House ", StringComparison.Ordinal) < 0 || data == null) return text;
            var sb = new System.Text.StringBuilder(text.Length + 32);
            int i = 0;
            while (i < text.Length)
            {
                int at = text.IndexOf("House ", i, StringComparison.Ordinal);
                if (at < 0) { sb.Append(text, i, text.Length - i); break; }
                int start = at + 6;
                sb.Append(text, i, start - i);
                string best = null;
                foreach (House h in data.Houses)
                {
                    if (h == null || string.IsNullOrEmpty(h.Name) || (best != null && h.Name.Length <= best.Length)) continue;
                    if (string.Compare(text, start, h.Name, 0, h.Name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    int end = start + h.Name.Length;
                    if (end < text.Length && char.IsLetterOrDigit(text[end])) continue;
                    best = h.Name;
                }
                if (best == null) { i = start; continue; }
                sb.Append(HouseTint(text.Substring(start, best.Length)));
                i = start + best.Length;
            }
            return sb.ToString();
        }

        private void NotifyPlayer(string id, string key, params object[] args)
        {
            Player p = FindOnlineById(id);
            if (p != null) Reply(p, key, args);
        }

        private void NotifyLeader(House house, string key, params object[] args)
        {
            Player p = OnlineLeader(house);
            if (p != null) Reply(p, key, args);
        }

        private void NotifyHouse(House house, string exceptId, string key, params object[] args)
        {
            foreach (HouseMember m in house.Members)
                if (m.Id != exceptId) NotifyPlayer(m.Id, key, args);
        }

        private void Broadcast(string key, params object[] args)
        {
            string text = Msg(key, null, args);
            Puts(text);
            if (config.BroadcastEvents) Server.BroadcastMessage(TintHouses(text));
        }

        // True while fewer than FoundingNoticesPerHour founding/disbanding notices went out in the last hour (and records one).
        private bool FoundingNoticeAllowed()
        {
            DateTime now = DateTime.UtcNow;
            while (foundingNotices.Count > 0 && (now - foundingNotices.Peek()).TotalHours >= 1) foundingNotices.Dequeue();
            if (foundingNotices.Count >= Math.Max(0, config.FoundingNoticesPerHour)) return false;
            foundingNotices.Enqueue(now);
            return true;
        }

        private void Chronicle(string type, string title, string detail, params string[] actors)
        {
            if (!config.ChronicleEnabled) return;
            if (RealmChronicle == null) { Puts("[chronicle unavailable] " + type + ": " + title); return; }
            RealmChronicle.Call("Log", type, title, detail, actors);
        }

        #endregion

        #region API (plugin.Call)

        private string GetHouse(string playerId)
        {
            House house = HouseOf(playerId);
            return house != null ? house.Name : null;
        }

        private string GetLiege(string house)
        {
            House h = FindHouse(house);
            return h != null ? h.Liege : null;
        }

        private List<string> GetMembers(string house)
        {
            House h = FindHouse(house);
            if (h == null) return null;
            var ids = new List<string>();
            foreach (HouseMember m in h.Members) ids.Add(m.Id);
            return ids;
        }

        private List<string> GetMemberNames(string house)
        {
            House h = FindHouse(house);
            if (h == null) return null;
            var names = new List<string>();
            foreach (HouseMember m in h.Members) names.Add(m.Name);
            return names;
        }

        private string GetHouseLeader(string house)
        {
            House h = FindHouse(house);
            HouseMember leader = h != null ? LeaderOf(h) : null;
            return leader != null ? leader.Id : null;
        }

        // When the house now bearing this name was founded (ISO 8601 UTC, full precision), or null if no such house. A house that is
        // disbanded and founded again under the same name gets a new date, so other plugins (RealmTreasury's vaults)
        // can tell a new house from the fallen one whose name it took.
        private string GetHouseFounded(string house)
        {
            House h = FindHouse(house);
            return h != null ? h.Founded.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        private List<string> GetVassals(string house)
        {
            return FindHouse(house) != null ? VassalsOf(house) : null;
        }

        private bool HasTreaty(string houseA, string houseB)
        {
            return FindTreaty(houseA, houseB) != null;
        }

        // Shape matches the houses[] entries of oxide/data/RealmState.json.
        private List<Dictionary<string, object>> GetHouseSummaries()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (House h in data.Houses)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "name", h.Name },
                    { "sigil", h.Sigil },
                    { "liege", h.Liege },
                    { "members", h.Members.Count }
                });
            }
            return list;
        }

        private Dictionary<string, object> GetReputation(string house)
        {
            House h = FindHouse(house);
            if (h == null) return null;
            return new Dictionary<string, object>
            {
                { "oathsBroken", h.OathsBroken },
                { "treatiesBroken", h.TreatiesBroken }
            };
        }

        #endregion
    }
}
