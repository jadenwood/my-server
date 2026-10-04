// RealmHerald: the realm's voice for newcomers, and the help hub for everyone.
//
//   Welcome      the first time a Steam id joins, a short welcome with the MOTD folded in (WelcomeDelaySeconds after
//                the join, so it lands after the game's own join lines and RealmWarden's protection notice) and,
//                optionally, one Herald line to the realm that a newcomer has arrived (capped per hour).
//   First steps  three steps every newcomer is walked through. Each is checked against the realm itself where a
//                plugin can say so:
//                  1 swear to a house   done when RealmHouses.GetHouse(id) names a house
//                  2 see the crown      done when the player types /realm crown, or /crown is seen through
//                                       OnPlayerCommand (UNVERIFIED that Oxide chat commands reach that hook)
//                  3 take a contract    done when RealmContracts.HasContractHistory(id) is true, or /contract
//                                       accept|post is seen through OnPlayerCommand (same caveat)
//                The next step is shown about a minute after joining and then every PathReminderMinutes, at most
//                PathRemindersPerSession times a session, until the path is walked. /realm path off stops it;
//                /realm skip sets a step aside. A plugin that is not loaded never blocks the path: its step can be
//                skipped, and the reminder says so.
//   /realm       every Realm chat command by subject, with a one-line description each (lang keys "Cmd.<command>",
//                so a server can reword them). Commands of plugins that are not loaded are left out.
//                tools/realm-integration/check.mjs fails if the catalogue below and the [ChatCommand]s in
//                plugins/*.cs ever differ.
//   Tips         one tip every TipIntervalMinutes to the players online who have not turned tips off
//                (/realm tips off). A tip that names a command of a plugin that is not loaded is skipped.
//   MOTD         configurable lines shown on every later join (MotdDelaySeconds after it) and on /realm motd. Admins edit
//                them in game with /realm admin motd. Placeholders: {player} {online} {max} {monarch} {season}.
//   Popups       /realm opens the hub in the game's own popup window, and a newcomer's welcome opens one too. The chat
//                version is always sent as well (/realm list prints the hub in chat). UsePopups switches them off for the
//                server; /realm popups off for one player, and RealmHouses asks PopupsWanted before its own popups.
//                UNVERIFIED in game: see the "Popups" region.
//
// Chat: every line follows the Realm chat style (docs/realm-commands.md). Text an admin writes (MOTD, tips) gets its
// /commands coloured at send time and goes out through the single-string overloads, so braces are harmless.
//
// Data: oxide/data/RealmHerald.json (who has been welcomed, their steps, tips off). If it exists but cannot be parsed,
// the herald keeps answering /realm from memory and never writes the file, so a damaged record is not overwritten.
//
// Cross-plugin calls (all optional; a missing plugin only disables the part that needs it):
//   RealmHouses.GetHouse(string playerId) -> string          step 1
//   CrownAndConsequences.GetKingName() / GetKingHouse()       /realm crown, {monarch}
//   RealmContracts.HasContractHistory(string playerId) -> bool step 3
//   RealmSeasons.GetSeasonName() -> string                    {season}
// Offered to other plugins: PopupsWanted(string playerId) -> bool (false after /realm popups off).
// No Chronicle entries: a welcome is not realm history.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public: Oxide.CSharp
// (CSharpPlugin.cs @49500b8, ctor) registers only NonPublic|Instance methods as callable hooks.
// UNVERIFIED in game: everything at run time. See plugins/docs/RealmHerald.md.

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError [ASM]
using CodeHatch.Engine.Networking;               // Player, Server [ASM]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem [SRC]
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference]) [SRC]

namespace Oxide.Plugins
{
    [Info("RealmHerald", "Realm", "0.1.0")]
    [Description("A welcome and first steps for newcomers, the /realm help hub, rotating tips and the message of the day")]
    public class RealmHerald : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmContracts;
        [PluginReference] private Plugin RealmSeasons;

        private const string PermAdmin = "realmherald.admin";
        private const string DataName = "RealmHerald";
        private const string ChatCmdColour = "F4C96D";
        private const string ChatMuted = "A3A6AD";
        private const int Steps = 3;

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private Timer tickTimer;
        private DateTime nextTipAt = DateTime.MinValue;
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        private readonly List<DateTime> newcomerHeralds = new List<DateTime>();

        // Clock indirection so the behaviour tests can move time; always DateTime.UtcNow on a server.
        private Func<DateTime> clock = DefaultClock;

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        #region Catalogue

        // Every Realm chat command, its subject and the plugin that registers it. check.mjs keeps this in step with
        // the [ChatCommand]s in plugins/*.cs; the descriptions are lang keys "Cmd.<command>".
        private class Entry
        {
            public readonly string Command;
            public readonly string Subject;
            public readonly string Plugin;

            public Entry(string command, string subject, string plugin)
            {
                Command = command;
                Subject = subject;
                Plugin = plugin;
            }
        }

        private static readonly string[] Subjects = { "houses", "crown", "law", "blood", "coin", "events", "roads", "letters", "fair" };

        private static readonly Entry[] Catalogue =
        {
            new Entry("house", "houses", "RealmHouses"),
            new Entry("swear", "houses", "RealmHouses"),
            new Entry("renounce", "houses", "RealmHouses"),
            new Entry("treaty", "houses", "RealmHouses"),
            new Entry("dominion", "houses", "RealmDominion"),
            new Entry("crown", "crown", "CrownAndConsequences"),
            new Entry("decree", "crown", "CrownAndConsequences"),
            new Entry("council", "crown", "CrownAndConsequences"),
            new Entry("claim", "crown", "CrownAndConsequences"),
            new Entry("ransom", "crown", "CrownAndConsequences"),
            new Entry("laws", "law", "RealmLaws"),
            new Entry("law", "law", "RealmLaws"),
            new Entry("court", "law", "RealmLaws"),
            new Entry("dynasty", "blood", "RealmDynasties"),
            new Entry("renown", "blood", "RealmRenown"),
            new Entry("titles", "blood", "RealmRenown"),
            new Entry("purse", "coin", "RealmTreasury"),
            new Entry("market", "coin", "RealmTreasury"),
            new Entry("vault", "coin", "RealmTreasury"),
            new Entry("treasury", "coin", "RealmTreasury"),
            new Entry("economy", "coin", "RealmTreasury"),
            new Entry("dice", "coin", "RealmArena"),
            new Entry("cards", "coin", "RealmArena"),
            new Entry("contract", "coin", "RealmContracts"),
            new Entry("season", "events", "RealmSeasons"),
            new Entry("duel", "events", "RealmArena"),
            new Entry("arena", "events", "RealmArena"),
            new Entry("events", "events", "RealmEvents"),
            new Entry("event", "events", "RealmEvents"),
            new Entry("tourney", "events", "RealmEvents"),
            new Entry("hunt", "events", "RealmEvents"),
            new Entry("truce", "events", "RealmEvents"),
            new Entry("chronicle", "events", "RealmChronicle"),
            new Entry("quest", "events", "RealmQuests"),
            new Entry("achievements", "blood", "RealmQuests"),
            new Entry("raven", "letters", "RealmRavens"),
            new Entry("rumour", "letters", "RealmRavens"),
            new Entry("rumor", "letters", "RealmRavens"),
            new Entry("travel", "roads", "RealmTravel"),
            new Entry("home", "roads", "RealmTravel"),
            new Entry("road", "roads", "RealmTravel"),
            new Entry("kit", "roads", "RealmTravel"),
            new Entry("realm", "fair", "RealmHerald"),
            new Entry("warden", "fair", "RealmWarden"),
            new Entry("stats", "fair", "RealmStats")
        };

        private static Entry FindEntry(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            string c = command.TrimStart('/').ToLowerInvariant();
            foreach (Entry e in Catalogue) if (e.Command == c) return e;
            return null;
        }

        #endregion

        #region Config

        private class PluginConfig
        {
            public bool WelcomeNewPlayers = true;
            public float WelcomeDelaySeconds = 20f;     // after the join; RealmWarden's protection notice comes first
            public bool HeraldNewcomers = true;         // one Herald line to the realm when a newcomer arrives
            public int NewcomerHeraldsPerHour = 6;      // 0 = never
            public bool FirstStepsPath = true;
            public int FirstReminderSeconds = 60;       // the first "next step" line after a join
            public int PathReminderMinutes = 15;
            public int PathRemindersPerSession = 3;
            public bool ShowMotdOnJoin = true;
            public float MotdDelaySeconds = 8f;
            public List<string> Motd = new List<string>
            {
                "Hail, {player}. {online} of the realm are here, and the Old Throne is held by {monarch}. /realm lists every command."
            };
            public bool TipsEnabled = true;
            public int TipIntervalMinutes = 20;
            public int TipMinPlayers = 1;
            public List<string> Tips = new List<string>
            {
                "Swear to a house before you build: /house list shows who is taking oaths.",
                "The Old Throne can only be contested in a declared rebellion window. /crown shows the next one.",
                "Bounties pay in real goods the realm holds in escrow. /contract list shows the board.",
                "A raven can be copied by a rival spymaster. Write as if the realm is reading. /raven",
                "Broken oaths are remembered: the mark follows a house and its leader. /house info",
                "Crown Night, the Royal Tournament and the King's Hunt come round every week. /events",
                "Deeds earn renown, and renown earns titles you can wear in chat. /titles all",
                "Marks are the coin of Ostreval. Keep them in your /purse or trade them on the /market.",
                "A law binds only once the crown proclaims it. /law list shows what is in force.",
                "New to the realm? Other players cannot harm you for a while. /warden status",
                "The Realm Chronicle remembers every crowning and every betrayal. /chronicle",
                "A named heir keeps a bloodline's claim alive when its monarch falls. /dynasty"
            };
            public bool UsePopups = true;               // the game's popup windows for the /realm hub and the welcome
            public bool WelcomePopup = true;            // a newcomer's welcome also opens as a popup
            public float TickSeconds = 30f;
            public int MaxPlayersKept = 20000;          // oldest records (by last seen) are dropped beyond this
            public int MaxMotdLines = 6;
            public int MaxLineLength = 200;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void ClampConfig()
        {
            if (config.Motd == null) config.Motd = new List<string>();
            if (config.Tips == null) config.Tips = new List<string>();
            config.Motd.RemoveAll(string.IsNullOrEmpty);
            config.Tips.RemoveAll(string.IsNullOrEmpty);
            if (config.MaxMotdLines < 1) config.MaxMotdLines = 1;
            if (config.MaxMotdLines > 12) config.MaxMotdLines = 12;
            if (config.Motd.Count > config.MaxMotdLines) config.Motd.RemoveRange(config.MaxMotdLines, config.Motd.Count - config.MaxMotdLines);
            if (config.MaxLineLength < 40) config.MaxLineLength = 40;
            if (config.WelcomeDelaySeconds < 0f) config.WelcomeDelaySeconds = 0f;
            if (config.MotdDelaySeconds < 0f) config.MotdDelaySeconds = 0f;
            if (config.NewcomerHeraldsPerHour < 0) config.NewcomerHeraldsPerHour = 0;
            if (config.FirstReminderSeconds < 10) config.FirstReminderSeconds = 10;
            if (config.PathReminderMinutes < 1) config.PathReminderMinutes = 1;
            if (config.PathRemindersPerSession < 0) config.PathRemindersPerSession = 0;
            if (config.TipIntervalMinutes < 1) config.TipIntervalMinutes = 1;
            if (config.TipMinPlayers < 1) config.TipMinPlayers = 1;
            if (config.TickSeconds < 5f) config.TickSeconds = 5f;
            if (config.MaxPlayersKept < 100) config.MaxPlayersKept = 100;
        }

        #endregion

        #region Data

        private class PlayerRec
        {
            public string Name;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public bool Sworn;             // step 1
            public bool SawCrown;          // step 2
            public bool TookContract;      // step 3
            public bool PathDone;          // all three, congratulated once
            public bool PathOff;           // no reminders (/realm path off)
            public bool TipsOff;           // no tips (/realm tips off)
            public bool PopupsOff;         // no Realm popups (/realm popups off); chat only
        }

        private class StoredData
        {
            public Dictionary<string, PlayerRec> Players = new Dictionary<string, PlayerRec>();
            public int TipIndex;
            public int NewcomersToday;
            public string NewcomersDay;
        }

        // Per session, not saved.
        private class Session
        {
            public DateTime NextReminder;
            public int Reminders;
        }

        private void SaveData()
        {
            if (loadFailed || data == null) return;             // never overwrite a file that could not be read
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

        #region Lang

        protected override void LoadDefaultMessages()
        {
            var m = new Dictionary<string, string>
            {
                { "Speaker", "Realm" },
                { "Herald", "[D6A043]Herald[FFFFFF]: " },
                { "Tip", "[A3A6AD]Tip[FFFFFF]: " },
                { "Usage", "Usage: [F4C96D]/realm[FFFFFF] [list|subject|command] | [F4C96D]/realm path[FFFFFF] [on|off] | [F4C96D]/realm skip[FFFFFF] | [F4C96D]/realm crown[FFFFFF] | [F4C96D]/realm motd[FFFFFF]" },
                { "Usage2", "  Settings: [F4C96D]/realm tips[FFFFFF] on|off | [F4C96D]/realm popups[FFFFFF] on|off" },
                { "HubPopup", "Every command is open in a window. [F4C96D]/realm list[FFFFFF] shows them here in chat." },
                { "PopupsOff", "No more popup windows from the realm; everything comes in chat. [F4C96D]/realm popups on[FFFFFF] brings them back." },
                { "PopupsOn", "Popup windows are on again." },
                { "PopupsServerOff", "This server does not use popup windows; everything comes in chat." },
                { "PopupHubTitle", "The Realm of Ostreval" },
                { "PopupHubIntro", "Every command, by subject. Type one alone in chat for its own help." },
                { "PopupHubLine", "{0}: {1}" },
                { "PopupHubFooter", "[F4C96D]/realm <subject>[FFFFFF] gives a line on each command. [F4C96D]/realm path[FFFFFF] shows your first steps." },
                { "PopupClose", "To the realm" },
                { "PopupWelcomeTitle", "Welcome to Ostreval" },
                { "PopupWelcomeSteps", "Your first steps:" },
                { "PopupWelcomeStep", "{0}. {1}" },
                { "PopupWelcomeFooter", "[F4C96D]/realm[FFFFFF] lists every command. [F4C96D]/realm path[FFFFFF] shows how far you have come." },
                { "HubHeader", "Every command, by subject. [F4C96D]/realm <subject>[FFFFFF] gives a line on each:" },
                { "HubLine", "  {0} ({1}): {2}" },
                { "SubjectHeader", "{0}. Each command shows its own help when typed alone:" },
                { "CmdLine", "  {0} - {1}" },
                { "CommandOne", "{0} - {1}. Type it alone for its own help." },
                { "NotLoaded", "{0} belongs to {1}, which is not running on this server." },
                { "UnknownSubject", "There is no subject or command '{0}'. Subjects: {1}" },
                { "SubjectEmpty", "Nothing for {0} is running on this server." },
                { "Subject.houses", "Houses and oaths" },
                { "Subject.crown", "The crown" },
                { "Subject.law", "Law and the court" },
                { "Subject.blood", "Bloodlines and renown" },
                { "Subject.coin", "Coin, trade and contracts" },
                { "Subject.events", "Seasons and realm events" },
                { "Subject.roads", "Roads, waystones and kits" },
                { "Subject.letters", "Letters and rumours" },
                { "Subject.fair", "Help and fair play" },
                { "Cmd.house", "found, join and run a house; see any house with info" },
                { "Cmd.swear", "offer your house's oath to a liege, or accept one" },
                { "Cmd.renounce", "break your house's oath (it earns an oathbreaker mark)" },
                { "Cmd.treaty", "propose, accept, list or break treaties between houses" },
                { "Cmd.dominion", "the holdings of the realm, who holds them, and the War Hours" },
                { "Cmd.crown", "who reigns, since when, and the next rebellion window" },
                { "Cmd.decree", "the crown's decrees and their cooldowns" },
                { "Cmd.council", "the King's Council and its seats" },
                { "Cmd.claim", "open claims to the throne; a house leader declares one" },
                { "Cmd.ransom", "captives held for ransom, and how a captive goes free" },
                { "Cmd.laws", "help for the law and the court" },
                { "Cmd.law", "the laws in force, the catalogue and the public crime ledger" },
                { "Cmd.court", "cases, accusations, juries, trial by combat, fines and outlaws" },
                { "Cmd.dynasty", "bloodlines, heirs, succession and blood claims" },
                { "Cmd.renown", "your renown and infamy, another's, and the roll of honour" },
                { "Cmd.titles", "your titles, how each is earned, and which you wear in chat" },
                { "Cmd.purse", "your marks, and paying another player" },
                { "Cmd.market", "the realm market: sell, bid, buy and collect" },
                { "Cmd.vault", "your house vault and its stewards" },
                { "Cmd.treasury", "the crown's treasury, its ledger and the tithe" },
                { "Cmd.economy", "all the economy help, fees and the game tax" },
                { "Cmd.dice", "Hearth Dice for marks at the tavern, or a throw for show" },
                { "Cmd.cards", "Twenty-One for marks at the tavern: fair odds, strict limits" },
                { "Cmd.contract", "bounties, deliveries and swords for hire" },
                { "Cmd.season", "the season, house standings and the Hall of Kings" },
                { "Cmd.duel", "challenge a player or a team to a duel to the first fall, with a stake" },
                { "Cmd.arena", "your rating, the ladder, the Champion of the Ring and the Lists" },
                { "Cmd.events", "what is running now and what comes next" },
                { "Cmd.event", "collect event prizes that did not fit in your packs" },
                { "Cmd.tourney", "join, leave or follow the Royal Tournament" },
                { "Cmd.hunt", "the King's Hunt and its quarry" },
                { "Cmd.truce", "whether the Truce of the Realm holds" },
                { "Cmd.chronicle", "the latest entries of the Realm Chronicle" },
                { "Cmd.quest", "your journal: daily and weekly tasks, the season's tale, your house's goal" },
                { "Cmd.achievements", "your deeds of survival, war, politics, economy and exploration" },
                { "Cmd.raven", "letters to players and houses, your inbox, and intrigue" },
                { "Cmd.rumour", "whisper an anonymous rumour, or hear the latest" },
                { "Cmd.rumor", "the same as [F4C96D]/rumour[FFFFFF]" },
                { "Cmd.travel", "the waystones you know, and fast travel between them for a toll" },
                { "Cmd.home", "set a home in your own crest zone, and travel back to it" },
                { "Cmd.road", "the way to a waystone or your home, called in chat as you walk" },
                { "Cmd.kit", "a newcomer's pack, daily house provisions and the season's bounty" },
                { "Cmd.realm", "this help, your first steps, tips, popups and the message of the day" },
                { "Cmd.warden", "your protection, the raid hours, the rules, and reports" },
                { "Cmd.stats", "what the server's statistics record about you, and opting out" },
                { "Welcome1", "Hail, {0}, and well met. Six great houses contend for the Old Throne, and the realm remembers what you do." },
                { "Welcome2", "  Your first steps: swear to a house, see who holds the crown, take a contract. [F4C96D]/realm path[FFFFFF] shows the way." },
                { "Welcome3", "  Every command, by subject: [F4C96D]/realm[FFFFFF]" },
                { "NewcomerHerald", "{0} arrives in Ostreval for the first time." },
                { "PathHeader", "Your first steps in Ostreval ({0} of 3 done):" },
                { "PathDoneMark", "  [8FC97A]done[FFFFFF]  {0}" },
                { "PathNextMark", "  [F4C96D]next[FFFFFF]  {0}" },
                { "PathLaterMark", "  [A3A6AD]later[FFFFFF] {0}" },
                { "PathFooter", "  Stop the reminders: [F4C96D]/realm path off[FFFFFF]. Set a step aside: [F4C96D]/realm skip[FFFFFF]" },
                { "Step1", "Swear to a house: [F4C96D]/house list[FFFFFF], then [F4C96D]/house join[FFFFFF] <house> once invited, or [F4C96D]/house found[FFFFFF]" },
                { "Step2", "See who holds the crown: [F4C96D]/crown[FFFFFF] or [F4C96D]/realm crown[FFFFFF]" },
                { "Step3", "Take a first contract: [F4C96D]/contract list[FFFFFF], then [F4C96D]/contract accept[FFFFFF] <id> or [F4C96D]/contract post[FFFFFF]" },
                { "StepMissing", " (not running on this server: [F4C96D]/realm skip[FFFFFF])" },
                { "NextStep", "Your next step: {0}" },
                { "StepDone", "Step {0} of 3 done. {1}" },
                { "StepDoneNext", "Next: {0}" },
                { "PathComplete", "Your first steps are walked: a house, the crown, a contract. The realm is yours to shape. [F4C96D]/realm[FFFFFF] for everything else." },
                { "PathAllDone", "You have walked your first steps. [F4C96D]/realm[FFFFFF] lists every command." },
                { "PathOff", "No more reminders of your first steps. [F4C96D]/realm path[FFFFFF] shows them any time." },
                { "PathOn", "Reminders of your first steps are on again." },
                { "Skipped", "Step {0} set aside. [F4C96D]/realm path[FFFFFF] shows it again." },
                { "CrownHeld", "{0} of House {1} sits the Old Throne. [F4C96D]/crown[FFFFFF] shows the decrees and the next rebellion window." },
                { "CrownHeldNoHouse", "{0} sits the Old Throne. [F4C96D]/crown[FFFFFF] shows the decrees and the next rebellion window." },
                { "CrownEmpty", "The Old Throne stands empty. A house with a declared claim may take it: [F4C96D]/claim[FFFFFF]" },
                { "CrownUnknown", "The crown's records are not kept on this server." },
                { "TipsOff", "No more tips. [F4C96D]/realm tips on[FFFFFF] brings them back." },
                { "TipsOn", "Tips are on." },
                { "MotdNone", "There is no message of the day." },
                { "Monarch", "{0} of House {1}" },
                { "NoMonarch", "no one" },
                { "NoSeason", "no season" },
                { "NoPermission", "You may not do that." },
                { "PlayerNotFound", "No one by that name is online or known to the herald." },
                { "Paused", "The herald's records are damaged; your first steps are not being saved. Tell an admin." },
                { "AdminUsage", "Usage: [F4C96D]/realm admin motd[FFFFFF] add <text> | clear | list  [F4C96D]/realm admin tip[FFFFFF]  [F4C96D]/realm admin reset[FFFFFF] <player>  [F4C96D]/realm admin status[FFFFFF]" },
                { "MotdAdded", "Line {0} added to the message of the day." },
                { "MotdFull", "The message of the day already has {0} lines. Clear it first." },
                { "MotdTooLong", "A line may be at most {0} characters." },
                { "MotdCleared", "The message of the day is cleared." },
                { "MotdListHeader", "Message of the day ({0} lines):" },
                { "MotdListLine", "  {0}. {1}" },
                { "TipSent", "Tip {0} of {1} sent." },
                { "NoTips", "No tip can be sent (none configured, or their plugins are not loaded)." },
                { "ResetDone", "{0}'s first steps start again." },
                { "AdminStatus", "{0} players known, {1} have walked the path, {2} newcomers today. Tips every {3} min ({4} lines). Sources: houses={5} crown={6} contracts={7} seasons={8}. Data {9}." }
            };
            lang.RegisterMessages(m, this);
        }

        private string Msg(string key, Player player, params object[] args)
        {
            string text = lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); }
            catch (FormatException) { return text; }                  // a server's reworded line with a stray brace
        }

        private void Reply(Player player, string key, params object[] args)
        {
            Say(player, ToneOf(key), Msg(key, player, args));
        }

        private void Error(Player player, string key, params object[] args)
        {
            player.SendError(Styled(Msg("Speaker", player), ChatError, Msg(key, player, args)));
        }

        private void Say(Player player, string tone, string text)
        {
            player.SendMessage(Styled(Msg("Speaker", player), tone, text));     // single-string overload: brace safe
        }

        private void Line(Player player, string key, params object[] args)
        {
            player.SendMessage(Msg(key, player, args));
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "StepDone", "PathComplete", "PathOn", "TipsOn", "PopupsOn", "MotdAdded", "MotdCleared", "TipSent", "ResetDone"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string> { "NextStep", "Paused" };

        private static string ToneOf(string key)
        {
            return OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintWarning("Could not read the config (" + ex.Message + "); using the defaults for this run.");
                config = null;
            }
            if (config == null) config = new PluginConfig();
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
                    + ". The herald keeps answering /realm from memory and will NOT overwrite the file until it is fixed or moved away, then reloaded.");
                data = null;
            }
            if (data == null) data = new StoredData();
            if (data.Players == null) data.Players = new Dictionary<string, PlayerRec>();
            var broken = new List<string>();
            foreach (KeyValuePair<string, PlayerRec> kv in data.Players) if (kv.Value == null || string.IsNullOrEmpty(kv.Key)) broken.Add(kv.Key);
            foreach (string k in broken) data.Players.Remove(k);
            if (data.TipIndex < 0) data.TipIndex = 0;
        }

        private void OnServerInitialized()
        {
            // Re-sent on hot reload, so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            DateTime now = clock();
            if (nextTipAt == DateTime.MinValue) nextTipAt = now.AddMinutes(config.TipIntervalMinutes);
            foreach (Player p in Server.ClientPlayers)
            {
                if (p == null || p.IsServer) continue;
                string id = p.Id.ToString();
                Rec(id, p.Name, now);
                if (!sessions.ContainsKey(id)) sessions[id] = new Session { NextReminder = now.AddSeconds(config.FirstReminderSeconds) };
            }
        }

        private void OnServerSave()
        {
            if (dirty) SaveData();
        }

        private void Unload()
        {
            if (dirty) SaveData();
        }

        #endregion

        #region Joining

        private void OnPlayerConnected(Player player)
        {
            if (player == null || player.IsServer) return;
            DateTime now = clock();
            string id = player.Id.ToString();
            bool isNew = !data.Players.ContainsKey(id);
            PlayerRec rec = Rec(id, player.Name, now);
            sessions[id] = new Session { NextReminder = now.AddSeconds(config.FirstReminderSeconds + (isNew ? config.WelcomeDelaySeconds : 0f)) };

            bool welcome = isNew && config.WelcomeNewPlayers;
            // A newcomer hears the MOTD inside the welcome, so the realm greets them once, not twice.
            if (config.ShowMotdOnJoin && config.Motd.Count > 0 && !welcome)
                timer.Once(Math.Max(0.1f, config.MotdDelaySeconds), delegate { ShowMotd(Online(id), false); });

            if (welcome)
            {
                CountNewcomer(now);
                timer.Once(Math.Max(0.1f, config.WelcomeDelaySeconds), delegate { Welcome(Online(id)); });
                if (config.HeraldNewcomers && NewcomerHeraldAllowed(now))
                    Server.BroadcastMessage(Msg("Herald", null) + Msg("NewcomerHerald", null, Clean(player.Name)));
            }
            if (rec.PathOff || rec.PathDone) sessions[id].Reminders = int.MaxValue;
        }

        private void OnPlayerDisconnected(Player player)
        {
            if (player == null || player.IsServer) return;
            string id = player.Id.ToString();
            sessions.Remove(id);
            PlayerRec rec;
            if (data.Players.TryGetValue(id, out rec)) { rec.LastSeen = clock(); dirty = true; }
        }

        private void Welcome(Player player)
        {
            if (player == null) return;
            if (config.WelcomePopup) ShowWelcomePopup(player);
            Reply(player, "Welcome1", Clean(player.Name));
            if (config.ShowMotdOnJoin) foreach (string raw in config.Motd) player.SendMessage("  " + ColourCommands(Fill(raw, player)));
            Line(player, "Welcome2");
            Line(player, "Welcome3");
        }

        private void ShowMotd(Player player, bool asked)
        {
            if (player == null) return;
            if (config.Motd.Count == 0) { if (asked) Reply(player, "MotdNone"); return; }
            bool first = true;
            foreach (string raw in config.Motd)
            {
                string text = ColourCommands(Fill(raw, player));
                if (first) { Say(player, ChatGold, text); first = false; }
                else player.SendMessage("  " + text);
            }
        }

        // Placeholders by plain replacement (never string.Format on admin text). Player names are cleaned first.
        private string Fill(string text, Player player)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string s = text;
            if (s.IndexOf("{player}", StringComparison.Ordinal) >= 0) s = s.Replace("{player}", player != null ? Clean(player.Name) : "");
            if (s.IndexOf("{online}", StringComparison.Ordinal) >= 0) s = s.Replace("{online}", OnlineCount().ToString());
            if (s.IndexOf("{max}", StringComparison.Ordinal) >= 0) s = s.Replace("{max}", Server.PlayerLimit.ToString());
            if (s.IndexOf("{monarch}", StringComparison.Ordinal) >= 0) s = s.Replace("{monarch}", MonarchText(player));
            if (s.IndexOf("{season}", StringComparison.Ordinal) >= 0)
            {
                string season = RealmSeasons != null ? RealmSeasons.Call("GetSeasonName") as string : null;
                s = s.Replace("{season}", string.IsNullOrEmpty(season) ? Msg("NoSeason", player) : season);
            }
            return s;
        }

        private string MonarchText(Player player)
        {
            string king = CrownAndConsequences != null ? CrownAndConsequences.Call("GetKingName") as string : null;
            if (string.IsNullOrEmpty(king)) return Msg("NoMonarch", player);
            string house = CrownAndConsequences.Call("GetKingHouse") as string;
            return string.IsNullOrEmpty(house) ? Clean(king) : Msg("Monarch", player, Clean(king), HouseTint(house));
        }

        private void CountNewcomer(DateTime now)
        {
            string day = now.ToString("yyyy-MM-dd");
            if (data.NewcomersDay != day) { data.NewcomersDay = day; data.NewcomersToday = 0; }
            data.NewcomersToday++;
            dirty = true;
        }

        private bool NewcomerHeraldAllowed(DateTime now)
        {
            newcomerHeralds.RemoveAll(delegate(DateTime t) { return (now - t).TotalHours >= 1; });
            if (newcomerHeralds.Count >= config.NewcomerHeraldsPerHour) return false;
            newcomerHeralds.Add(now);
            return true;
        }

        #endregion

        #region First steps

        private bool StepDone(PlayerRec rec, int step)
        {
            return step == 1 ? rec.Sworn : step == 2 ? rec.SawCrown : rec.TookContract;
        }

        private int DoneCount(PlayerRec rec)
        {
            int n = 0;
            for (int s = 1; s <= Steps; s++) if (StepDone(rec, s)) n++;
            return n;
        }

        private int NextStepOf(PlayerRec rec)
        {
            for (int s = 1; s <= Steps; s++) if (!StepDone(rec, s)) return s;
            return 0;
        }

        private bool StepAvailable(int step)
        {
            if (step == 1) return RealmHouses != null;
            if (step == 2) return true;                                 // /realm crown always answers
            return RealmContracts != null;
        }

        private string StepText(Player player, int step)
        {
            string text = Msg("Step" + step, player);
            if (!StepAvailable(step)) text += Msg("StepMissing", player);
            return text;
        }

        // Asks the realm whether steps 1 and 3 are done; returns the steps that just became done.
        private List<int> Refresh(string id, PlayerRec rec)
        {
            var done = new List<int>();
            if (!rec.Sworn && RealmHouses != null)
            {
                string house = RealmHouses.Call("GetHouse", id) as string;
                if (!string.IsNullOrEmpty(house)) { rec.Sworn = true; done.Add(1); }
            }
            if (!rec.TookContract && RealmContracts != null)
            {
                object r = RealmContracts.Call("HasContractHistory", id);
                if (r is bool && (bool)r) { rec.TookContract = true; done.Add(3); }
            }
            if (done.Count > 0) dirty = true;
            return done;
        }

        private void MarkStep(Player player, PlayerRec rec, int step)
        {
            if (StepDone(rec, step)) return;
            if (step == 1) rec.Sworn = true;
            else if (step == 2) rec.SawCrown = true;
            else rec.TookContract = true;
            dirty = true;
            Announce(player, rec, new List<int> { step });
        }

        // Tells the player which steps were just done and what comes next, or congratulates once at the end.
        private void Announce(Player player, PlayerRec rec, List<int> done)
        {
            if (player == null || done.Count == 0 || rec.PathDone || !config.FirstStepsPath) return;
            int next = NextStepOf(rec);
            if (next == 0)
            {
                rec.PathDone = true;
                dirty = true;
                Reply(player, "PathComplete");
                return;
            }
            if (rec.PathOff) return;
            Reply(player, "StepDone", DoneCount(rec), Msg("StepDoneNext", player, StepText(player, next)));
        }

        private void ShowPath(Player player, PlayerRec rec)
        {
            if (rec.PathDone) { Reply(player, "PathAllDone"); return; }
            int next = NextStepOf(rec);
            Reply(player, "PathHeader", DoneCount(rec));
            for (int s = 1; s <= Steps; s++)
            {
                string key = StepDone(rec, s) ? "PathDoneMark" : s == next ? "PathNextMark" : "PathLaterMark";
                Line(player, key, StepText(player, s));
            }
            if (!rec.PathOff) Line(player, "PathFooter");
        }

        // /crown or /contract seen by the game's command hook. Observes only: always returns null, never blocks.
        // UNVERIFIED: whether Oxide's own chat commands pass through IOnPlayerCommand on this game.
        private object OnPlayerCommand(Player player, string command, string[] args)
        {
            if (player == null || player.IsServer || string.IsNullOrEmpty(command)) return null;
            try
            {
                string c = command.TrimStart('/').ToLowerInvariant();
                PlayerRec rec;
                if (!data.Players.TryGetValue(player.Id.ToString(), out rec)) return null;
                if (c == "crown") MarkStep(player, rec, 2);
                else if (c == "contract" && args != null && args.Length > 0)
                {
                    string sub = args[0].ToLowerInvariant();
                    if (sub == "accept" || sub == "post") MarkStep(player, rec, 3);
                }
            }
            catch (Exception ex) { PrintWarning("OnPlayerCommand: " + ex.Message); }
            return null;
        }

        #endregion

        #region Tick: reminders and tips

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex.Message); }
        }

        private void Tick()
        {
            DateTime now = clock();
            var online = new List<Player>();
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) online.Add(p);

            foreach (Player p in online)
            {
                string id = p.Id.ToString();
                PlayerRec rec = Rec(id, p.Name, now);
                if (rec.PathDone || !config.FirstStepsPath) continue;
                Announce(p, rec, Refresh(id, rec));
                if (rec.PathDone || rec.PathOff) continue;
                Session s;
                if (!sessions.TryGetValue(id, out s)) { s = new Session { NextReminder = now.AddSeconds(config.FirstReminderSeconds) }; sessions[id] = s; }
                if (now < s.NextReminder || s.Reminders >= config.PathRemindersPerSession) continue;
                int next = NextStepOf(rec);
                if (next == 0) continue;
                Reply(p, "NextStep", StepText(p, next));
                s.Reminders++;
                s.NextReminder = now.AddMinutes(config.PathReminderMinutes);
            }

            if (config.TipsEnabled && now >= nextTipAt)
            {
                nextTipAt = now.AddMinutes(config.TipIntervalMinutes);
                if (online.Count >= config.TipMinPlayers) SendTip(online);
            }

            PruneRecords();
            if (dirty) SaveData();
        }

        // Sends the next tip whose commands are all loaded; returns its 1-based number, or 0 if none could go.
        private int SendTip(List<Player> online)
        {
            int count = config.Tips.Count;
            for (int tries = 0; tries < count; tries++)
            {
                int i = data.TipIndex % count;
                data.TipIndex = (i + 1) % count;
                dirty = true;
                string tip = config.Tips[i];
                if (!CommandsLoaded(tip)) continue;
                string text = ColourCommands(tip);
                foreach (Player p in online)
                {
                    PlayerRec rec;
                    if (data.Players.TryGetValue(p.Id.ToString(), out rec) && rec.TipsOff) continue;
                    p.SendMessage(Msg("Tip", p) + text);
                }
                return i + 1;
            }
            return 0;
        }

        private void PruneRecords()
        {
            int over = data.Players.Count - config.MaxPlayersKept;
            if (over <= 0) return;
            var list = new List<KeyValuePair<string, PlayerRec>>(data.Players);
            list.Sort(delegate(KeyValuePair<string, PlayerRec> a, KeyValuePair<string, PlayerRec> b) { return a.Value.LastSeen.CompareTo(b.Value.LastSeen); });
            for (int i = 0; i < over; i++) if (!sessions.ContainsKey(list[i].Key)) data.Players.Remove(list[i].Key);
            dirty = true;
        }

        #endregion

        #region /realm

        [ChatCommand("realm")]
        private void CmdRealm(Player player, string command, string[] args)
        {
            if (player == null) return;
            string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string id = player.Id.ToString();
            PlayerRec rec = Rec(id, player.Name, clock());
            switch (sub)
            {
                case "":
                    if (ShowHubPopup(player)) Reply(player, "HubPopup");
                    else ShowHub(player);
                    return;
                case "help":
                case "list":
                    ShowHub(player);
                    return;
                case "popups":
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "off") { rec.PopupsOff = true; dirty = true; Reply(player, "PopupsOff"); return; }
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "on")
                    {
                        rec.PopupsOff = false; dirty = true;
                        Reply(player, config.UsePopups ? "PopupsOn" : "PopupsServerOff");
                        return;
                    }
                    ShowUsage(player);
                    return;
                case "path":
                case "steps":
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "off")
                    {
                        rec.PathOff = true; dirty = true;
                        Session s;
                        if (sessions.TryGetValue(id, out s)) s.Reminders = int.MaxValue;
                        Reply(player, "PathOff");
                        return;
                    }
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "on")
                    {
                        rec.PathOff = false; dirty = true;
                        sessions[id] = new Session { NextReminder = clock().AddMinutes(config.PathReminderMinutes) };
                        Reply(player, "PathOn");
                        return;
                    }
                    Announce(player, rec, Refresh(id, rec));
                    ShowPath(player, rec);
                    if (loadFailed) Reply(player, "Paused");
                    return;
                case "skip":
                {
                    int next = NextStepOf(rec);
                    if (next == 0 || rec.PathDone) { Reply(player, "PathAllDone"); return; }
                    Reply(player, "Skipped", next);
                    MarkStep(player, rec, next);
                    return;
                }
                case "crown":
                    ShowCrown(player);
                    MarkStep(player, rec, 2);
                    return;
                case "tips":
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "off") { rec.TipsOff = true; dirty = true; Reply(player, "TipsOff"); return; }
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "on") { rec.TipsOff = false; dirty = true; Reply(player, "TipsOn"); return; }
                    ShowUsage(player);
                    return;
                case "motd":
                    ShowMotd(player, true);
                    return;
                case "admin":
                    CmdAdmin(player, args);
                    return;
            }
            ShowTopic(player, sub);
        }

        private void ShowUsage(Player player)
        {
            Error(player, "Usage");
            Line(player, "Usage2");
        }

        // The hub as one popup window: a line per subject with its commands. False when popups are off for this player
        // or the window could not be opened; the caller then prints the hub in chat.
        private bool ShowHubPopup(Player player)
        {
            if (!PopupsFor(player)) return false;
            var sb = new StringBuilder();
            sb.Append(Msg("PopupHubIntro", player)).Append('\n');
            foreach (string subject in Subjects)
            {
                var names = new List<string>();
                foreach (Entry e in Catalogue) if (e.Subject == subject && Loaded(e.Plugin)) names.Add("/" + e.Command);
                if (names.Count == 0) continue;
                sb.Append('\n').Append(Msg("PopupHubLine", player, Msg("Subject." + subject, player), string.Join("  ", names.ToArray())));
            }
            sb.Append("\n\n").Append(Msg("PopupHubFooter", player));
            return ShowInfoPopup(player, Msg("PopupHubTitle", player), sb.ToString(), Msg("PopupClose", player));
        }

        // A newcomer's welcome as a popup window: the greeting, the MOTD and the three first steps.
        private bool ShowWelcomePopup(Player player)
        {
            if (!PopupsFor(player)) return false;
            var sb = new StringBuilder();
            sb.Append(Msg("Welcome1", player, Clean(player.Name)));
            if (config.ShowMotdOnJoin) foreach (string raw in config.Motd) sb.Append("\n\n").Append(Fill(raw, player));
            if (config.FirstStepsPath)
            {
                sb.Append("\n\n").Append(Msg("PopupWelcomeSteps", player));
                for (int step = 1; step <= Steps; step++) sb.Append('\n').Append(Msg("PopupWelcomeStep", player, step, StepText(player, step)));
            }
            sb.Append("\n\n").Append(Msg("PopupWelcomeFooter", player));
            return ShowInfoPopup(player, Msg("PopupWelcomeTitle", player), sb.ToString(), Msg("PopupClose", player));
        }

        private void ShowHub(Player player)
        {
            Reply(player, "HubHeader");
            foreach (string subject in Subjects)
            {
                var names = new List<string>();
                foreach (Entry e in Catalogue) if (e.Subject == subject && Loaded(e.Plugin)) names.Add(Cmd("/" + e.Command));
                if (names.Count == 0) continue;
                Line(player, "HubLine", Msg("Subject." + subject, player), subject, string.Join(" ", names.ToArray()));
            }
        }

        private void ShowTopic(Player player, string word)
        {
            foreach (string subject in Subjects)
            {
                if (subject != word) continue;
                var lines = new List<Entry>();
                foreach (Entry e in Catalogue) if (e.Subject == subject && Loaded(e.Plugin)) lines.Add(e);
                if (lines.Count == 0) { Error(player, "SubjectEmpty", Msg("Subject." + subject, player)); return; }
                Reply(player, "SubjectHeader", Msg("Subject." + subject, player));
                foreach (Entry e in lines) Line(player, "CmdLine", Cmd("/" + e.Command), Msg("Cmd." + e.Command, player));
                return;
            }
            Entry one = FindEntry(word);
            if (one != null)
            {
                if (!Loaded(one.Plugin)) { Error(player, "NotLoaded", "/" + one.Command, one.Plugin); return; }
                Reply(player, "CommandOne", Cmd("/" + one.Command), Msg("Cmd." + one.Command, player));
                return;
            }
            Error(player, "UnknownSubject", Clean(word), string.Join(", ", Subjects));
        }

        private void ShowCrown(Player player)
        {
            if (CrownAndConsequences == null) { Reply(player, "CrownUnknown"); return; }
            string king = CrownAndConsequences.Call("GetKingName") as string;
            if (string.IsNullOrEmpty(king)) { Reply(player, "CrownEmpty"); return; }
            string house = CrownAndConsequences.Call("GetKingHouse") as string;
            if (string.IsNullOrEmpty(house)) Reply(player, "CrownHeldNoHouse", Clean(king));
            else Reply(player, "CrownHeld", Clean(king), HouseTint(house));
        }

        private void CmdAdmin(Player player, string[] args)
        {
            if (!permission.UserHasPermission(player.Id.ToString(), PermAdmin)) { Error(player, "NoPermission"); return; }
            string what = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (what == "motd")
            {
                string op = args.Length > 2 ? args[2].ToLowerInvariant() : "list";
                if (op == "add" && args.Length > 3)
                {
                    string line = string.Join(" ", args, 3, args.Length - 3).Trim();
                    if (line.Length > config.MaxLineLength) { Error(player, "MotdTooLong", config.MaxLineLength); return; }
                    if (config.Motd.Count >= config.MaxMotdLines) { Error(player, "MotdFull", config.MaxMotdLines); return; }
                    config.Motd.Add(line);
                    Config.WriteObject(config, true);
                    Reply(player, "MotdAdded", config.Motd.Count);
                    return;
                }
                if (op == "clear")
                {
                    config.Motd.Clear();
                    Config.WriteObject(config, true);
                    Reply(player, "MotdCleared");
                    return;
                }
                Reply(player, "MotdListHeader", config.Motd.Count);
                for (int i = 0; i < config.Motd.Count; i++) player.SendMessage(Msg("MotdListLine", player, i + 1, config.Motd[i]));
                return;
            }
            if (what == "tip")
            {
                var online = new List<Player>();
                foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) online.Add(p);
                int n = config.Tips.Count > 0 ? SendTip(online) : 0;
                if (n == 0) Error(player, "NoTips");
                else Reply(player, "TipSent", n, config.Tips.Count);
                return;
            }
            if (what == "reset" && args.Length > 2)
            {
                string q = string.Join(" ", args, 2, args.Length - 2);
                string targetId = null;
                PlayerRec target = null;
                foreach (KeyValuePair<string, PlayerRec> kv in data.Players)
                {
                    if (kv.Key == q || string.Equals(kv.Value.Name, q, StringComparison.OrdinalIgnoreCase)) { targetId = kv.Key; target = kv.Value; break; }
                }
                if (target == null) { Error(player, "PlayerNotFound"); return; }
                target.Sworn = target.SawCrown = target.TookContract = target.PathDone = target.PathOff = false;
                dirty = true;
                Session s;
                if (sessions.TryGetValue(targetId, out s)) { s.Reminders = 0; s.NextReminder = clock(); }
                Reply(player, "ResetDone", Clean(target.Name ?? targetId));
                return;
            }
            if (what == "status")
            {
                int walked = 0;
                foreach (PlayerRec r in data.Players.Values) if (r.PathDone) walked++;
                Reply(player, "AdminStatus", data.Players.Count, walked, data.NewcomersDay == clock().ToString("yyyy-MM-dd") ? data.NewcomersToday : 0,
                    config.TipIntervalMinutes, config.Tips.Count, RealmHouses != null, CrownAndConsequences != null, RealmContracts != null,
                    RealmSeasons != null, loadFailed ? "DAMAGED (not saved)" : "ok");
                return;
            }
            Error(player, "AdminUsage");
        }

        #endregion

        #region Popups

        // Realm popups (docs/realm-commands.md, "Popups"): the game's own windows, opened with the Player extension
        // methods in CodeHatch.Common.PlayerExtensions. Signatures read from the 2.0.3867 Assembly-CSharp.dll metadata:
        //   MessageDialogue ShowPopup(this Player, string title, string message, string buttonText = "Ok",
        //                             Dialogue.OnSubmit handler = null, bool interupt = false, bool broadcast = true)
        // On a dedicated server the window reaches the client only with broadcast = true (the server sends the
        // "codehatch.ui.popup.basic.show" event). The herald's windows ask nothing, so no answer is awaited.
        // A popup is plain text: chat colour tags are stripped (whether the window would draw them is UNVERIFIED).
        // UNVERIFIED in game: that the window shows, its size, and that "\n" breaks lines in it.
        private bool PopupsFor(Player player)
        {
            if (player == null || player.IsServer || !config.UsePopups) return false;
            PlayerRec rec;
            return !(data.Players.TryGetValue(player.Id.ToString(), out rec) && rec.PopupsOff);
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

        // API for other Realm plugins (non-public: Oxide only calls non-public methods): false when this player turned
        // Realm popups off with /realm popups off. Each plugin keeps its own UsePopups switch for the server.
        // RealmHouses asks before each of its own popups.
        private bool PopupsWanted(string playerId)
        {
            PlayerRec rec;
            return !(playerId != null && data.Players.TryGetValue(playerId, out rec) && rec.PopupsOff);
        }

        #endregion

        #region Helpers

        private PlayerRec Rec(string id, string name, DateTime now)
        {
            PlayerRec rec;
            if (!data.Players.TryGetValue(id, out rec))
            {
                rec = new PlayerRec { FirstSeen = now };
                data.Players[id] = rec;
            }
            else if (!string.IsNullOrEmpty(name) && rec.Name == name)
            {
                rec.LastSeen = now;                                   // saved with the next real change
                return rec;
            }
            if (!string.IsNullOrEmpty(name)) rec.Name = name;
            rec.LastSeen = now;
            dirty = true;
            return rec;
        }

        private Player Online(string id)
        {
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer && p.Id.ToString() == id) return p;
            return null;
        }

        private int OnlineCount()
        {
            int n = 0;
            foreach (Player p in Server.ClientPlayers) if (p != null && !p.IsServer) n++;
            return n;
        }

        private bool Loaded(string plugin)
        {
            if (plugin == Name) return true;
            return plugins.Exists(plugin);
        }

        // True unless the text names a Realm command whose plugin is not loaded.
        private bool CommandsLoaded(string text)
        {
            foreach (string c in CommandsIn(text))
            {
                Entry e = FindEntry(c);
                if (e != null && !Loaded(e.Plugin)) return false;
            }
            return true;
        }

        private static string Cmd(string command)
        {
            return "[" + ChatCmdColour + "]" + command + "[FFFFFF]";
        }

        // Every "/command" in a text that names a catalogue command (the word only).
        private static List<string> CommandsIn(string text)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(text)) return found;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '/' || (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == ']' || text[i - 1] == '/'))) continue;
                int j = i + 1;
                while (j < text.Length && text[j] >= 'a' && text[j] <= 'z') j++;
                if (j > i + 1 && FindEntry(text.Substring(i + 1, j - i - 1)) != null) found.Add(text.Substring(i + 1, j - i - 1));
            }
            return found;
        }

        private static readonly string[] StopWords =
        {
            "or", "and", "to", "within", "for", "with", "the", "then", "in", "on", "at", "works", "is", "a", "if", "it", "you",
            "your", "before", "after", "from", "by", "now", "here", "when", "while", "again", "instead", "too", "also", "first",
            "later", "as", "of", "so", "but", "not", "shows", "lists", "gives"
        };

        // Colours "/command sub words" in admin-written text the way the lang strings are coloured (chat style).
        private static string ColourCommands(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("[" + ChatCmdColour + "]", StringComparison.OrdinalIgnoreCase) >= 0) return text;
            var sb = new StringBuilder(text.Length + 32);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                bool start = c == '/' && (i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == ']' || text[i - 1] == '/'));
                int j = i + 1;
                while (start && j < text.Length && text[j] >= 'a' && text[j] <= 'z') j++;
                if (!start || j == i + 1 || FindEntry(text.Substring(i + 1, j - i - 1)) == null) { sb.Append(c); i++; continue; }
                int end = j;
                while (end < text.Length && text[end] == ' ')
                {
                    int k = end + 1;
                    while (k < text.Length && ((text[k] >= 'a' && text[k] <= 'z') || text[k] == '_' || text[k] == '|')) k++;
                    if (k == end + 1 || text[end + 1] < 'a' || text[end + 1] > 'z') break;
                    string word = text.Substring(end + 1, k - end - 1);
                    if (Array.IndexOf(StopWords, word) >= 0) break;
                    end = k;
                }
                sb.Append(Cmd(text.Substring(i, end - i)));
                i = end;
            }
            return sb.ToString();
        }

        // A player or monarch name as it goes into chat: no square brackets (so it cannot open a colour tag of its
        // own), no control characters, at most 40 characters.
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) if (c != '[' && c != ']' && !char.IsControl(c)) sb.Append(c);
            string s = sb.ToString().Trim();
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

        #endregion
    }
}
