// RealmCourt: two server-console commands for Realm Steward's Court view.
//
//   /realm.save     saves the world now (Game.Save). The game itself has no save command.
//   /realm.players  prints the online players with their Steam IDs, one per line:
//                     REALMCOURT|<seq>|players|<count>
//                     REALMCOURT|<seq>|p|<steamId>|<name>
//                   <seq> grows with every call, because the game's logger drops a line it has
//                   already printed 50 times within 5 minutes ([DEC] Logger.Log, MaxDuplicateLogs).
//
// How they are reached: Steward sends them over the game's admin console socket, which runs them
// as the server player ([DEC] SocketAdminConsole -> Console.Submit -> PlayerCommandEvent ->
// CommandManager.ExecuteCommand). They are put straight into the game's own command table with
// the permission "realm.court", which the server player always has ([DEC] PlayerExtensions.
// HasPermission: IsServer -> true) and no player has unless an admin grants it. They also check
// that the caller is the server. Output goes to the server console only (SendMessage on the local
// server player is Console.AddMessage), never to a player.
//
// Oxide chat commands ([ChatCommand]) are not used because Oxide only runs those for a sender it
// knows as a Covalence player, and the server console is not one ([DEC] Oxide.ReignOfKings
// ReignOfKingsCore.IOnServerCommand). UNVERIFIED at run time on the real server.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface (see RealmChronicle.cs).

using System;
using System.Collections.Generic;
using System.Text;
using CodeHatch.Common;                      // PlayerExtensions.SendMessage              [ASM]
using CodeHatch.Engine.Core.Commands;        // CommandManager, CommandInfo               [ASM]
// CommandAttribute and Game are written out in full: Oxide.Plugins and Oxide.Game have their own.
using GameCommand = CodeHatch.Engine.Core.Commands.CommandAttribute;
using CodeHatch.Engine.Networking;           // Player, Server                            [ASM]

namespace Oxide.Plugins
{
    [Info("RealmCourt", "Realm", "0.1.0")]
    [Description("Server-console commands for Realm Steward: save the world and list players with Steam IDs")]
    public class RealmCourt : ReignOfKingsPlugin
    {
        private const string Permission = "realm.court";
        private const string SaveLabel = "realm.save";
        private const string PlayersLabel = "realm.players";
        private const int NameMax = 64;

        private int seq;
        private DateTime lastSave = DateTime.MinValue;
        private readonly List<KeyValuePair<string, GameCommand>> registered = new List<KeyValuePair<string, GameCommand>>();

        private void Loaded()
        {
            Register(SaveLabel, "Saves the world now (Realm Steward).", new Action<CommandInfo>(CmdSave));
            Register(PlayersLabel, "Lists online players with Steam IDs (Realm Steward).", new Action<CommandInfo>(CmdPlayers));
        }

        private void Unload()
        {
            foreach (KeyValuePair<string, GameCommand> kv in registered)
            {
                GameCommand current;
                if (CommandManager.RegisteredCommands.TryGetValue(kv.Key, out current) && current == kv.Value)
                    CommandManager.RegisteredCommands.Remove(kv.Key);
            }
            registered.Clear();
        }

        private void Register(string label, string description, Action<CommandInfo> method)
        {
            GameCommand attr = new GameCommand("/" + label, description, Permission, new string[0]);
            attr.Method = method;
            CommandManager.RegisteredCommands[label] = attr;
            registered.Add(new KeyValuePair<string, GameCommand>(label, attr));
        }

        private static bool FromServer(CommandInfo cmd)
        {
            Player p = cmd.Player;
            return p != null && p.IsServer;
        }

        private void CmdSave(CommandInfo cmd)
        {
            if (!FromServer(cmd)) return;
            if ((DateTime.UtcNow - lastSave).TotalSeconds < 10)
            {
                cmd.Player.SendMessage("REALMCOURT|save|wait|The world was saved less than 10 seconds ago.");
                return;
            }
            lastSave = DateTime.UtcNow;
            bool started = CodeHatch.Engine.Core.Gaming.Game.Save();
            cmd.Player.SendMessage(started ? "REALMCOURT|save|ok|World saved." : "REALMCOURT|save|busy|A save is already running.");
        }

        private void CmdPlayers(CommandInfo cmd)
        {
            if (!FromServer(cmd)) return;
            seq++;
            List<Player> list = Server.ClientPlayers;
            StringBuilder sb = new StringBuilder();
            sb.Append("REALMCOURT|").Append(seq).Append("|players|").Append(list.Count);
            foreach (Player p in list)
            {
                if (p == null) continue;
                sb.Append('\n').Append("REALMCOURT|").Append(seq).Append("|p|").Append(p.Id).Append('|').Append(Clean(p.Name));
            }
            // One SendMessage: Console.AddMessage splits on newlines and logs each line in order.
            cmd.Player.SendMessage(sb.ToString());
        }

        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                if (sb.Length >= NameMax) break;
                sb.Append(c == '|' || c == '\n' || c == '\r' ? ' ' : c);
            }
            return sb.ToString();
        }
    }
}
