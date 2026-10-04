// Behaviour tests for plugins/RealmHerald.cs against Mocks.cs. Run with run.sh.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeHatch.Engine.Networking;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    static DateTime Clock = new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc);
    static Plugin Houses, Crown, Contracts, Seasons;
    static Dictionary<string, string> HouseOf = new Dictionary<string, string>();
    static HashSet<string> ContractOf = new HashSet<string>();
    static string King, KingHouse, SeasonName;
    static string Dir;
    static RealmHerald H;

    static readonly string[] AllPlugins = { "CrownAndConsequences", "RealmChronicle", "RealmContracts", "RealmDynasties", "RealmEvents",
        "RealmHouses", "RealmLaws", "RealmRavens", "RealmRenown", "RealmSeasons", "RealmStats", "RealmTreasury", "RealmWarden", "RealmHerald", "RealmDominion" };

    static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }

    static object Inv(object o, string m, params object[] a)
    {
        var mi = o.GetType().GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return mi.Invoke(o, a);
    }
    static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    static string B() { return string.Join("\n", Server.Broadcasts); }
    static string Plain(string s) { return Regex.Replace(s, @"\[[0-9A-Fa-f]{6}\]", ""); }

    static Player Mk(ulong id, string name)
    {
        var p = new Player(id, name);
        Server.ClientPlayers.Add(p);
        return p;
    }
    static void Leave(Player p) { Inv(H, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    static void Join(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(H, "OnPlayerConnected", p); }
    static void Cmd(Player p, params string[] args) { Inv(H, "CmdRealm", p, "realm", args); }
    static void Tick() { Inv(H, "SafeTick"); }
    static void Advance(TimeSpan by) { DateTime until = Clock + by; while (Clock < until) { Clock += TimeSpan.FromSeconds(30); Tick(); } }
    static object Rec(Player p) { return ((IDictionary)F(F(H, "data"), "Players"))[p.Id.ToString()]; }
    static bool RB(Player p, string field) { return (bool)F(Rec(p), field); }
    static object Cfg() { return F(H, "config"); }

    static RealmHerald NewHerald(string configJson = null, string[] loaded = null)
    {
        var p = new RealmHerald();
        p.Name = "RealmHerald";
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        foreach (string n in loaded ?? AllPlugins) p.plugins.Loaded.Add(n);
        SetF(p, "RealmHouses", Houses); SetF(p, "CrownAndConsequences", Crown); SetF(p, "RealmContracts", Contracts); SetF(p, "RealmSeasons", Seasons);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static int Main(string[] argv)
    {
        string repo = argv.Length > 0 ? argv[0] : "../../../..";
        Dir = Path.Combine(Path.GetTempPath(), "realmherald-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Houses = new Plugin { Name = "RealmHouses", Handler = (m, a) => { string h; return m == "GetHouse" && HouseOf.TryGetValue((string)a[0], out h) ? h : null; } };
        Crown = new Plugin { Name = "CrownAndConsequences", Handler = (m, a) => m == "GetKingName" ? King : m == "GetKingHouse" ? (object)KingHouse : null };
        Contracts = new Plugin { Name = "RealmContracts", Handler = (m, a) => m == "HasContractHistory" ? (object)ContractOf.Contains((string)a[0]) : null };
        Seasons = new Plugin { Name = "RealmSeasons", Handler = (m, a) => m == "GetSeasonName" ? SeasonName : null };
        try { Run(repo); }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL unexpected exception: " + ex); }
        finally { try { Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static void Run(string repo)
    {
        H = NewHerald();
        var lang = H.lang.Msgs;

        // ---------------- Lang and catalogue ----------------
        var catalogue = ((Array)typeof(RealmHerald).GetField("Catalogue", BF).GetValue(null)).Cast<object>().ToList();
        var subjects = (string[])typeof(RealmHerald).GetField("Subjects", BF).GetValue(null);
        Ok(catalogue.Count == 35, "the catalogue lists the 35 Realm chat commands", catalogue.Count.ToString());
        Ok(catalogue.All(e => lang.ContainsKey("Cmd." + (string)F(e, "Command"))), "every catalogue command has a description key");
        Ok(catalogue.All(e => subjects.Contains((string)F(e, "Subject"))), "every catalogue command has a known subject");
        Ok(subjects.All(s => lang.ContainsKey("Subject." + s)), "every subject has a name key");
        var tooLong = lang.Where(kv => Plain(kv.Value).Length > 200).Select(kv => kv.Key).ToList();
        Ok(tooLong.Count == 0, "no lang line is longer than 200 visible characters", string.Join(", ", tooLong));
        var registered = new HashSet<string>();
        foreach (string f in Directory.GetFiles(Path.Combine(repo, "plugins"), "*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\\[ChatCommand\\(\"([a-z]+)\"\\)\\]")) registered.Add(m.Groups[1].Value);
        // Staff-only commands stay out of the player hub; the list lives in tools/realm-integration/check.mjs.
        var staff = Regex.Match(File.ReadAllText(Path.Combine(repo, "tools", "realm-integration", "check.mjs")), "STAFF_COMMANDS = \\[([^\\]]*)\\]");
        foreach (Match m in Regex.Matches(staff.Groups[1].Value, "'([a-z]+)'")) registered.Remove(m.Groups[1].Value);
        var listed = new HashSet<string>(catalogue.Select(e => (string)F(e, "Command")));
        Ok(registered.SetEquals(listed), "the catalogue matches the [ChatCommand]s in plugins/*.cs",
            "missing: " + string.Join(",", registered.Except(listed)) + " extra: " + string.Join(",", listed.Except(registered)));

        // ---------------- First join ----------------
        var ada = Mk(76561190000000001, "Ada");
        Join(ada);
        Ok(B().Contains("Herald") && B().Contains("Ada arrives in Ostreval for the first time"), "a newcomer is heralded to the realm", B());
        Ok(ada.Messages.Count == 0 && H.timer.Pending.Count == 1, "the welcome waits for its delay (the MOTD is folded into it)");
        H.timer.RunPending();
        string all = ada.All();
        Ok(ada.Messages[0].StartsWith("[D6A043]Realm[FFFFFF]: Hail, Ada, and well met. Six great houses"), "the welcome, in the herald voice", all);
        Ok(ada.Messages[1] == "  Hail, Ada. 1 of the realm are here, and the Old Throne is held by no one. [F4C96D]/realm[FFFFFF] lists every command.",
            "the MOTD inside the welcome: placeholders filled, commands coloured", all);
        Ok(all.Contains("[F4C96D]/realm path[FFFFFF] shows the way"), "the welcome points to the first steps", all);
        Ok(ada.Messages.Count == 4, "welcome is short: four lines", ada.Messages.Count.ToString());
        Ok(ada.Messages.Skip(1).All(m => m.StartsWith("  ")), "welcome lines after the first are indented, no repeated speaker", all);
        var wp = ada.LastPopup;
        Ok(ada.Popups.Count == 1 && wp.Kind == "basic" && wp.Title == "Welcome to Ostreval" && wp.Buttons[0] == "To the realm" && wp.Broadcast,
            "the welcome also opens as a popup window, sent to the client (broadcast)", wp == null ? "none" : wp.Title);
        Ok(wp.Message.StartsWith("Hail, Ada, and well met.") && wp.Message.Contains("Hail, Ada. 1 of the realm are here")
            && wp.Message.Contains("1. Swear to a house: /house list") && wp.Message.Contains("3. Take a first contract"),
            "the welcome window holds the greeting, the MOTD and the three first steps", wp.Message);
        Ok(!Regex.IsMatch(wp.Message + wp.Title, @"\[[0-9A-Fa-f]{6}\]"), "the window is plain text: no chat colour tags", wp.Message);

        Leave(ada); Server.Broadcasts.Clear(); ada.Messages.Clear();
        Join(ada);
        H.timer.RunPending();
        Ok(Server.Broadcasts.Count == 0 && ada.Messages.Count == 1 && ada.Messages[0].StartsWith("[D6A043]Realm[FFFFFF]: Hail, Ada. 1 of the realm"), "a returning player gets the MOTD only, in the herald voice", ada.All());
        Ok(ada.Popups.Count == 1, "a returning player gets no welcome window");

        // ---------------- Newcomer heralds are capped ----------------
        Clock = Clock.AddHours(1.1);                                  // Ada's herald falls out of the hour
        Server.Broadcasts.Clear();
        var crowd = new List<Player>();
        for (int i = 0; i < 8; i++) { var p = Mk(76561190000000100 + (ulong)i, "Crowd" + i); crowd.Add(p); Join(p); }
        H.timer.RunPending();
        Ok(Server.Broadcasts.Count(b => b.Contains("arrives in Ostreval")) == 6, "newcomer heralds capped at 6 an hour", Server.Broadcasts.Count.ToString());
        foreach (var p in crowd) Leave(p);
        Clock = Clock.AddHours(1.1);
        Server.Broadcasts.Clear();
        var late = Mk(76561190000000200, "Late");
        Join(late);
        Ok(Server.Broadcasts.Count == 1, "the cap is per hour");
        Leave(late);

        // ---------------- First steps path ----------------
        var bryn = Mk(76561190000000002, "Bryn");
        Join(bryn); H.timer.RunPending(); bryn.Messages.Clear();
        Tick();
        Ok(!bryn.All().Contains("Your next step"), "no reminder in the first minute");
        Advance(TimeSpan.FromSeconds(90));
        Ok(bryn.Messages.Any(m => m.StartsWith("[E8913A]Realm[FFFFFF]: Your next step: Swear to a house")), "the first reminder names step 1 in the take-care tone", bryn.All());
        int reminders = bryn.Messages.Count(m => m.Contains("Your next step"));
        Advance(TimeSpan.FromMinutes(14));
        Ok(bryn.Messages.Count(m => m.Contains("Your next step")) == reminders, "reminders are spaced (15 min)");
        Advance(TimeSpan.FromMinutes(40));
        Ok(bryn.Messages.Count(m => m.Contains("Your next step")) == 3, "at most 3 reminders a session", bryn.Messages.Count(m => m.Contains("Your next step")).ToString());

        bryn.Messages.Clear();
        HouseOf[bryn.Id.ToString()] = "Varrow";
        Tick();
        Ok(bryn.Messages.Any(m => m.StartsWith("[8FC97A]Realm[FFFFFF]: Step 1 of 3 done. Next: See who holds the crown")), "step 1 is seen through RealmHouses.GetHouse and announced as done", bryn.All());
        Ok(Houses.Calls.Any(c => c == "GetHouse(" + bryn.Id + ")"), "GetHouse is called with the id as a string");
        Tick();
        Ok(bryn.Messages.Count(m => m.Contains("Step 1 of 3 done")) == 1, "a step is announced once");

        bryn.Messages.Clear();
        King = "Aldric"; KingHouse = "Varrow";
        Cmd(bryn, "crown");
        Ok(bryn.Messages[0].Contains("Aldric of House [C58FC0]Varrow[FFFFFF] sits the Old Throne"), "/realm crown names the monarch with the house in its colour", bryn.All());
        Ok(RB(bryn, "SawCrown") && bryn.All().Contains("Step 2 of 3 done"), "/realm crown completes step 2");

        bryn.Messages.Clear();
        Cmd(bryn, "path");
        string path = bryn.All();
        Ok(path.Contains("(2 of 3 done)") && path.Contains("[8FC97A]done[FFFFFF]  Swear") && path.Contains("[F4C96D]next[FFFFFF]  Take a first contract"), "/realm path marks done and next", path);
        Ok(bryn.Messages.Count == 5, "/realm path is five lines", bryn.Messages.Count.ToString());

        bryn.Messages.Clear();
        ContractOf.Add(bryn.Id.ToString());
        Tick();
        Ok(bryn.All().Contains("Your first steps are walked") && RB(bryn, "PathDone"), "step 3 through RealmContracts.HasContractHistory completes the path", bryn.All());
        Tick(); Advance(TimeSpan.FromMinutes(30));
        Ok(bryn.Messages.Count(m => m.Contains("first steps are walked")) == 1 && !bryn.All().Contains("Your next step"), "the path is congratulated once and goes quiet");
        bryn.Messages.Clear();
        Cmd(bryn, "path");
        Ok(bryn.All().Contains("You have walked your first steps"), "/realm path after the end says so");

        // OnPlayerCommand observes /crown and /contract accept, and never blocks
        var cass = Mk(76561190000000003, "Cass");
        Join(cass); H.timer.RunPending(); cass.Messages.Clear();
        object r1 = Inv(H, "OnPlayerCommand", cass, "crown", new string[0]);
        object r2 = Inv(H, "OnPlayerCommand", cass, "contract", new[] { "list" });
        Ok(r1 == null && r2 == null, "OnPlayerCommand never blocks a command");
        Ok(RB(cass, "SawCrown") && !RB(cass, "TookContract"), "/crown seen by the hook completes step 2; /contract list does not complete step 3");
        Inv(H, "OnPlayerCommand", cass, "contract", new[] { "accept", "4" });
        Ok(RB(cass, "TookContract"), "/contract accept seen by the hook completes step 3");

        // path off / on and skip
        var dain = Mk(76561190000000004, "Dain");
        Join(dain); H.timer.RunPending(); dain.Messages.Clear();
        Cmd(dain, "path", "off");
        Advance(TimeSpan.FromMinutes(5));
        Ok(RB(dain, "PathOff") && !dain.All().Contains("Your next step"), "/realm path off stops the reminders", dain.All());
        Cmd(dain, "path", "on");
        Ok(!RB(dain, "PathOff") && dain.All().Contains("Reminders of your first steps are on"), "/realm path on brings them back");
        dain.Messages.Clear();
        Cmd(dain, "skip");
        Ok(RB(dain, "Sworn") && dain.All().Contains("Step 1 set aside"), "/realm skip sets the current step aside", dain.All());
        Cmd(dain, "skip"); Cmd(dain, "skip");
        Ok(RB(dain, "PathDone") && dain.All().Contains("Your first steps are walked"), "skipping every step ends the path");
        dain.Messages.Clear();
        Cmd(dain, "skip");
        Ok(dain.All().Contains("You have walked your first steps"), "nothing left to skip");

        // ---------------- The hub ----------------
        var edda = Mk(76561190000000005, "Edda");
        Join(edda); H.timer.RunPending(); edda.Messages.Clear();
        edda.Popups.Clear();
        Cmd(edda);
        var hp = edda.LastPopup;
        Ok(edda.Popups.Count == 1 && hp.Kind == "basic" && hp.Title == "The Realm of Ostreval" && hp.Broadcast, "/realm opens the hub as a popup window", hp == null ? "none" : hp.Title);
        Ok(listed.All(c => Regex.IsMatch(hp.Message, @"(^|\s)/" + c + @"(\s|$)")) && hp.Message.Contains("Houses and oaths: /house  /swear  /renounce  /treaty"),
            "the window lists every command under its subject", hp.Message);
        Ok(hp.Message.Split('\n').Length == 12 && !Regex.IsMatch(hp.Message, @"\[[0-9A-Fa-f]{6}\]"), "the window: intro, eight subject lines, footer; plain text", hp.Message);
        Ok(edda.Messages.Count == 1 && edda.Messages[0] == "[D6A043]Realm[FFFFFF]: Every command is open in a window. [F4C96D]/realm list[FFFFFF] shows them here in chat.",
            "with the window comes one chat line: the chat fallback", edda.All());
        edda.Messages.Clear();
        Cmd(edda, "list");
        string hub = edda.All();
        Ok(edda.Messages.Count == 9 && edda.Messages[0].StartsWith("[D6A043]Realm[FFFFFF]: Every command, by subject"), "/realm list is a header and one line per subject (9 lines)", hub);
        Ok(listed.All(c => Regex.IsMatch(hub, @"\[F4C96D\]/" + c + @"\[FFFFFF\]")), "every command appears in the chat hub, coloured", hub);
        Ok(edda.Popups.Count == 1, "/realm list opens no window");

        // Popups switched off by the player, by the server, or failing in the game: the chat hub instead
        edda.Messages.Clear();
        Cmd(edda, "popups", "off");
        Ok(RB(edda, "PopupsOff") && edda.All().Contains("No more popup windows"), "/realm popups off", edda.All());
        Ok((bool)Inv(H, "PopupsWanted", edda.Id.ToString()) == false && (bool)Inv(H, "PopupsWanted", ada.Id.ToString()), "PopupsWanted tells other plugins the player's choice");
        Ok((bool)Inv(H, "PopupsWanted", "123") && (bool)Inv(H, "PopupsWanted", (string)null), "PopupsWanted is true for an unknown or missing id");
        edda.Messages.Clear();
        Cmd(edda);
        Ok(edda.Popups.Count == 1 && edda.Messages.Count == 9, "with popups off, /realm prints the hub in chat", edda.All());
        Cmd(edda, "popups", "on");
        Ok(!RB(edda, "PopupsOff") && edda.All().Contains("Popup windows are on again"), "/realm popups on");
        SetF(Cfg(), "UsePopups", false);
        edda.Messages.Clear();
        Cmd(edda);
        Ok(edda.Popups.Count == 1 && edda.Messages.Count == 9, "with UsePopups off for the server, /realm prints the hub in chat");
        edda.Messages.Clear();
        Cmd(edda, "popups", "on");
        Ok(edda.All().Contains("This server does not use popup windows"), "/realm popups on says when the server has them off", edda.All());
        Ok((bool)Inv(H, "PopupsWanted", edda.Id.ToString()), "PopupsWanted is the player's choice only; each plugin has its own switch");
        SetF(Cfg(), "UsePopups", true);
        CodeHatch.Common.PlayerExtensions.PopupsFail = true;
        edda.Messages.Clear();
        Cmd(edda);
        Ok(edda.Messages.Count == 9 && H.Log.Any(l => l.StartsWith("WARN ShowPopup failed")), "a window the game cannot open falls back to the chat hub", edda.All());
        CodeHatch.Common.PlayerExtensions.PopupsFail = false;
        edda.Messages.Clear();
        Cmd(edda, "popups");
        Ok(edda.All().Contains("ERR") && edda.All().Contains("/realm popups[FFFFFF] on|off"), "/realm popups alone shows the usage", edda.All());
        edda.Messages.Clear();
        Cmd(edda, "events");
        Ok(edda.Messages.Count == 8 && edda.All().Contains("  [F4C96D]/tourney[FFFFFF] - join, leave or follow the Royal Tournament"), "/realm events lists each command with its line", edda.All());
        edda.Messages.Clear();
        Cmd(edda, "contract");
        Ok(edda.All().Contains("[F4C96D]/contract[FFFFFF] - bounties, deliveries and swords for hire. Type it alone"), "/realm <command> describes one command", edda.All());
        edda.Messages.Clear();
        Cmd(edda, "dragons");
        Ok(edda.Messages.Count == 1 && edda.Messages[0].StartsWith("ERR [E86A5C]Realm[FFFFFF]: There is no subject or command 'dragons'"), "an unknown subject is refused in the error tone", edda.All());
        edda.Messages.Clear();
        Cmd(edda, "help");
        Ok(edda.Messages.Count == 9, "/realm help is the hub");

        // A server without some plugins: their commands are left out
        var lean = NewHerald(null, new[] { "RealmHouses", "CrownAndConsequences", "RealmHerald" });
        var finn = Mk(76561190000000006, "Finn");
        Inv(lean, "CmdRealm", finn, "realm", new[] { "list" });
        string leanHub = finn.All();
        Ok(!leanHub.Contains("/law") && !leanHub.Contains("/raven") && leanHub.Contains("/house") && leanHub.Contains("/realm"), "the hub leaves out plugins that are not loaded", leanHub);
        Ok(finn.Messages.Count == 4, "subjects with nothing loaded are left out (header + houses + crown + help)", finn.Messages.Count.ToString());
        Inv(lean, "CmdRealm", finn, "realm", new string[0]);
        Ok(finn.LastPopup != null && !finn.LastPopup.Message.Contains("/law") && finn.LastPopup.Message.Split('\n').Length == 7,
            "the hub window leaves them out too (intro, three subjects, footer)", finn.LastPopup == null ? "none" : finn.LastPopup.Message);
        finn.Messages.Clear();
        var quiet = NewHerald("{ \"WelcomePopup\": false }");
        var ivo = Mk(76561190000000010, "Ivo");
        Inv(quiet, "OnPlayerConnected", ivo);
        quiet.timer.RunPending();
        Ok(ivo.Popups.Count == 0 && ivo.All().Contains("Hail, Ivo, and well met"), "WelcomePopup off: the welcome comes in chat only", ivo.All());
        Server.ClientPlayers.Remove(ivo);
        finn.Messages.Clear();
        Inv(lean, "CmdRealm", finn, "realm", new[] { "court" });
        Ok(finn.All().Contains("ERR") && finn.All().Contains("RealmLaws, which is not running"), "/realm <command> of a missing plugin says so", finn.All());
        finn.Messages.Clear();
        Inv(lean, "CmdRealm", finn, "realm", new[] { "law" });
        Ok(finn.All().Contains("Nothing for Law and the court is running"), "/realm <subject> with nothing loaded says so", finn.All());
        Server.ClientPlayers.Remove(finn);

        // A server without RealmHouses: step 1 says so and can be skipped
        SetF(H, "RealmHouses", null);
        var gwen = Mk(76561190000000007, "Gwen");
        Join(gwen); H.timer.RunPending(); gwen.Messages.Clear();
        Cmd(gwen, "path");
        Ok(gwen.All().Contains("not running on this server") && gwen.All().Contains("/realm skip"), "a step whose plugin is missing says so and points to /realm skip", gwen.All());
        SetF(H, "RealmHouses", Houses);

        // /realm crown without a monarch, and without the crown plugin
        gwen.Messages.Clear();
        King = null;
        Cmd(gwen, "crown");
        Ok(gwen.All().Contains("The Old Throne stands empty"), "/realm crown with an empty throne");
        SetF(H, "CrownAndConsequences", null);
        gwen.Messages.Clear();
        Cmd(gwen, "crown");
        Ok(gwen.All().Contains("not kept on this server") && RB(gwen, "SawCrown"), "/realm crown without CrownAndConsequences still completes the step");
        SetF(H, "CrownAndConsequences", Crown);
        King = "Aldric"; KingHouse = "Varrow";

        // ---------------- Tips ----------------
        foreach (var p in Server.ClientPlayers) p.Messages.Clear();
        Cmd(edda, "tips", "off");
        edda.Messages.Clear();
        SetF(F(H, "data"), "TipIndex", 0);
        Advance(TimeSpan.FromMinutes(21));
        Ok(gwen.Messages.Count(m => m.StartsWith("[A3A6AD]Tip[FFFFFF]: ")) == 1, "one tip in the quiet tip voice after the interval", gwen.All());
        Ok(gwen.Messages.Any(m => m.Contains("[F4C96D]/house list[FFFFFF] shows who")), "tip commands are coloured, stopping at plain words", gwen.All());
        Ok(!edda.All().Contains("Tip"), "a player with tips off gets none");
        int idx = (int)F(F(H, "data"), "TipIndex");
        Advance(TimeSpan.FromMinutes(20));
        Ok((int)F(F(H, "data"), "TipIndex") == idx + 1 && gwen.Messages.Count(m => m.StartsWith("[A3A6AD]Tip")) == 2, "tips rotate in order");
        // a tip naming a command of a plugin that is not loaded is skipped
        var cfgTips = (List<string>)F(Cfg(), "Tips");
        cfgTips.Clear(); cfgTips.Add("Laws: /law list"); cfgTips.Add("Houses: /house list");
        H.plugins.Loaded.Remove("RealmLaws");
        SetF(F(H, "data"), "TipIndex", 0);
        gwen.Messages.Clear();
        H.permission.Grants.Add(gwen.Id + "|realmherald.admin");
        Cmd(gwen, "admin", "tip");
        Ok(gwen.All().Contains("Tip[FFFFFF]: Houses:") && !gwen.All().Contains("Laws:") && gwen.All().Contains("Tip 2 of 2 sent"), "a tip for a plugin that is not loaded is skipped", gwen.All());
        H.plugins.Loaded.Add("RealmLaws");
        Cmd(edda, "tips", "on");
        Ok(!RB(edda, "TipsOff"), "/realm tips on");

        // ---------------- MOTD ----------------
        gwen.Messages.Clear();
        Cmd(gwen, "admin", "motd", "clear");
        Cmd(gwen, "admin", "motd", "add", "Hail", "{player}!", "{monarch}", "rules,", "{season},", "{max}", "slots,", "{0}", "stays.");
        Ok(gwen.All().Contains("Line 1 added") && H.Config.Json.Contains("Hail {player}!"), "an admin adds a MOTD line and it is saved to the config", gwen.All());
        SeasonName = "Season 2";
        var bad = Mk(76561190000000008, "[FF0000]Mal");
        gwen.Messages.Clear();
        Inv(H, "ShowMotd", bad, true);
        string motd = bad.All();
        Ok(motd.Contains("Hail FF0000Mal!") && motd.Contains("Aldric of House [C58FC0]Varrow[FFFFFF] rules") && motd.Contains("Season 2, 50 slots, {0} stays."),
            "MOTD placeholders filled, names cleaned of colour tags, braces left alone", motd);
        Cmd(gwen, "admin", "motd", "add", new string(' ', 1) + new string('x', 250));
        Ok(gwen.All().Contains("at most 200 characters"), "an overlong MOTD line is refused");
        for (int i = 0; i < 6; i++) Cmd(gwen, "admin", "motd", "add", "line" + i);
        Ok(gwen.All().Contains("already has 6 lines"), "the MOTD is capped at MaxMotdLines");
        gwen.Messages.Clear();
        Cmd(gwen, "admin", "motd", "list");
        Ok(gwen.Messages.Count == 7 && gwen.Messages[1].StartsWith("  1. Hail"), "/realm admin motd list", gwen.All());
        Cmd(gwen, "admin", "motd", "clear");
        gwen.Messages.Clear();
        Cmd(gwen, "motd");
        Ok(gwen.All().Contains("There is no message of the day"), "/realm motd with none");
        Server.ClientPlayers.Remove(bad);

        // ---------------- Admin ----------------
        edda.Messages.Clear();
        Cmd(edda, "admin", "status");
        Ok(edda.All().StartsWith("ERR [E86A5C]Realm[FFFFFF]: You may not do that."), "admin commands need realmherald.admin", edda.All());
        gwen.Messages.Clear();
        Cmd(gwen, "admin", "reset", "Bryn");
        Ok(gwen.All().Contains("Bryn's first steps start again") && !RB(bryn, "PathDone") && !RB(bryn, "Sworn"), "an admin resets a player's path", gwen.All());
        gwen.Messages.Clear();
        Cmd(gwen, "admin", "reset", "Nobody");
        Ok(gwen.All().Contains("ERR") && gwen.All().Contains("No one by that name"), "reset of an unknown player is refused");
        gwen.Messages.Clear();
        Cmd(gwen, "admin", "status");
        Ok(gwen.All().Contains("players known") && gwen.All().Contains("Data ok"), "/realm admin status", gwen.All());
        gwen.Messages.Clear();
        Cmd(gwen, "admin");
        Ok(gwen.All().Contains("Usage:"), "/realm admin alone shows its usage");

        // ---------------- Chat style ----------------
        Func<string, string, string, string> styled = (s, t, x) => (string)typeof(RealmHerald).GetMethod("Styled", BF).Invoke(null, new object[] { s, t, x });
        Ok(styled("Realm", "D6A043", "hello") == "[D6A043]Realm[FFFFFF]: hello", "Styled adds the speaker");
        Ok(styled("Realm", "D6A043", "  a list line") == "  a list line", "a line that starts with a space carries no speaker");
        Ok(styled("Realm", "D6A043", "[C8A050]Herald[FFFFFF]: old") == "[C8A050]Herald[FFFFFF]: old", "a line from an older lang file keeps its own heading");
        Ok(styled("Realm", "D6A043", "Realm: already named") == "Realm: already named", "a line that names its speaker is not doubled");
        Func<string, string> colour = x => (string)typeof(RealmHerald).GetMethod("ColourCommands", BF).Invoke(null, new object[] { x });
        Ok(colour("Try /house found or /purse.") == "Try [F4C96D]/house found[FFFFFF] or [F4C96D]/purse[FFFFFF].", "command phrases stop at plain words and punctuation", colour("Try /house found or /purse."));
        Ok(colour("oxide/data and /notacommand") == "oxide/data and /notacommand", "paths and unknown commands are left alone");

        // ---------------- Saving, reloading, damage ----------------
        Inv(H, "Unload");
        var again = NewHerald();
        var recs = (IDictionary)F(F(again, "data"), "Players");
        Ok(recs.Contains(cass.Id.ToString()) && (bool)F(recs[cass.Id.ToString()], "SawCrown") && (bool)F(recs[cass.Id.ToString()], "TookContract"), "steps survive a reload");
        Ok(!(bool)F(recs[edda.Id.ToString()], "TipsOff"), "tips choice survives a reload");

        string file = Path.Combine(Dir, "RealmHerald.json");
        File.WriteAllText(file, "{ \"Players\": { \"1\": ");
        var damaged = NewHerald();
        Ok((bool)F(damaged, "loadFailed") && damaged.Log.Any(l => l.StartsWith("ERROR") && l.Contains("NOT overwrite")), "a damaged data file is reported");
        H = damaged;
        var hana = Mk(76561190000000009, "Hana");
        Join(hana); H.timer.RunPending(); Advance(TimeSpan.FromMinutes(2)); Inv(H, "OnServerSave"); Inv(H, "Unload");
        Ok(File.ReadAllText(file) == "{ \"Players\": { \"1\": ", "a damaged data file is never overwritten");
        hana.Messages.Clear();
        Cmd(hana); Cmd(hana, "list"); Cmd(hana, "path");
        Ok(hana.All().Contains("Every command, by subject") && hana.All().Contains("not being saved"), "the hub still answers and /realm path says the records are damaged", hana.All());
        File.Delete(file);

        // ---------------- Pruning ----------------
        var small = NewHerald("{ \"MaxPlayersKept\": 100 }");
        H = small;
        var players = (IDictionary)F(F(small, "data"), "Players");
        var recType = typeof(RealmHerald).GetNestedType("PlayerRec", BF);
        for (int i = 0; i < 110; i++)
        {
            var rec = Activator.CreateInstance(recType);
            recType.GetField("LastSeen").SetValue(rec, Clock.AddDays(-200 + i));
            players["9" + i.ToString("000")] = rec;
        }
        Tick();
        Ok(players.Count == 100 && !players.Contains("9000") && players.Contains("9109"), "records beyond MaxPlayersKept are pruned oldest first", players.Count.ToString());
        Ok(players.Contains(hana.Id.ToString()), "online players are never pruned");
    }
}
