// Behaviour tests for plugins/RealmHouses.cs against Mocks.cs: founding, oaths and renouncing, with the popup windows
// (Yes/No and input) and their chat fallbacks. Run with run.sh.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeHatch.Engine.Networking;
using CodeHatch.UserInterface.Dialogues;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    static Plugin Chronicle, Herald;
    static HashSet<string> PopupsOff = new HashSet<string>();
    static string Dir;
    static RealmHouses H;

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
    static object Cfg() { return F(H, "config"); }

    static Player Mk(ulong id, string name)
    {
        var p = new Player(id, name);
        Server.ClientPlayers.Add(p);
        return p;
    }
    static void House(Player p, params string[] args) { Inv(H, "CmdHouse", p, "house", args); }
    static void Swear(Player p, params string[] args) { Inv(H, "CmdSwear", p, "swear", args); }
    static void Renounce(Player p, params string[] args) { Inv(H, "CmdRenounce", p, "renounce", args); }
    static string HouseOf(Player p) { return (string)Inv(H, "GetHouse", p.Id.ToString()); }
    static string LiegeOf(string house) { return (string)Inv(H, "GetLiege", house); }
    static void Clear(params Player[] ps) { foreach (var p in ps) { p.Messages.Clear(); p.Popups.Clear(); } Server.Broadcasts.Clear(); }

    static RealmHouses NewHouses(string configJson = null)
    {
        var p = new RealmHouses();
        p.Name = "RealmHouses";
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chronicle);
        SetF(p, "RealmHerald", Herald);
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static int Main(string[] argv)
    {
        Dir = Path.Combine(Path.GetTempPath(), "realmhouses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Chronicle = new Plugin { Name = "RealmChronicle" };
        Herald = new Plugin { Name = "RealmHerald", Handler = (m, a) => m == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null };
        try { Run(); }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL unexpected exception: " + ex); }
        finally { try { Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static void Run()
    {
        H = NewHouses();
        var lang = H.lang.Msgs;
        var tooLong = lang.Where(kv => Plain(kv.Value).Length > 200).Select(kv => kv.Key).ToList();
        Ok(tooLong.Count == 0, "no lang line is longer than 200 visible characters", string.Join(", ", tooLong));

        var ada = Mk(76561190000000001, "Ada");
        var cass = Mk(76561190000000002, "Cass");
        var dain = Mk(76561190000000003, "Dain");
        var edda = Mk(76561190000000004, "Edda");

        // ---------------- Founding in chat, as before ----------------
        House(cass, "found", "Varrow", "a", "black", "stag");
        Ok(HouseOf(cass) == "Varrow" && cass.Popups.Count == 0, "the full /house found line founds at once, no window", cass.All());
        Ok(cass.Messages[0].StartsWith("[8FC97A]Houses[FFFFFF]: House [C58FC0]Varrow[FFFFFF] is founded"), "founded, in the done tone with the house tint", cass.Messages[0]);

        // ---------------- Founding through input windows ----------------
        Clear(ada);
        House(ada, "found");
        var name1 = ada.LastPopup;
        Ok(ada.Popups.Count == 1 && name1.Kind == "input" && name1.Title == "Found a house" && name1.Broadcast && name1.Input == "",
            "/house found alone asks for the name in an input window, sent to the client, starting empty", name1 == null ? "none" : name1.Kind);
        Ok(name1.Message.Contains("3 to 24 letters") && name1.Buttons[0] == "Next" && name1.Buttons[1] == "Cancel", "the name window says the rules", name1.Message);
        Ok(ada.Messages.Count == 1 && ada.Messages[0].Contains("Name your house in the window, or type [F4C96D]/house found[FFFFFF]"), "with it, the chat fallback line", ada.All());
        name1.Answer(Options.Yes, "  \"House   Ashgrove\" ");
        var sigil1 = ada.LastPopup;
        Ok(ada.Popups.Count == 2 && sigil1.Kind == "input" && sigil1.Message.Contains("House Ashgrove will bear") && sigil1.Buttons[0] == "Found the house",
            "a good name opens the sigil window (quotes, extra spaces and a leading 'House' dropped)", sigil1 == null ? "none" : sigil1.Message);
        Ok(HouseOf(ada) == null, "nothing is founded before the sigil");
        sigil1.Answer(Options.Yes, "an oak in flame");
        Ok(HouseOf(ada) == "Ashgrove" && ada.All().Contains("is founded under the sigil of an oak in flame"), "the sigil answer founds the house", ada.All());
        Ok(B().Contains("founds House") && Chronicle.Calls.Any(c => c.StartsWith("Log(house_founded")), "the founding is heralded and chronicled as before", B());
        sigil1.Answer(Options.Yes, "a second oak");
        Ok(((IList)F(F(H, "data"), "Houses")).Count == 2, "an answered window cannot be answered again");

        // a bad name is refused in chat and asked again, with the reason in the window
        Clear(dain);
        House(dain, "found");
        dain.LastPopup.Answer(Options.Yes, "x");
        var again = dain.LastPopup;
        Ok(dain.Popups.Count == 2 && again.Message.StartsWith("House names must be 3-24 characters") && dain.All().Contains("ERR [E86A5C]Houses[FFFFFF]: House names must be"),
            "a bad name: chat error and a new window that starts with the reason", dain.All());
        dain.LastPopup.Answer(Options.Yes, "Varrow");
        Ok(dain.Popups.Count == 3 && dain.LastPopup.Message.StartsWith("That name is already taken"), "a taken name is asked again", dain.LastPopup.Message);
        dain.LastPopup.Answer(Options.Yes, "Accept");
        Ok(dain.LastPopup.Message.StartsWith("That name is already taken or reserved"), "a reserved word is refused like a taken name");
        Clear(dain);
        House(dain, "found");
        dain.LastPopup.Answer(Options.No, "Dunmere");
        Ok(HouseOf(dain) == null && dain.All().Contains("No house is founded."), "Cancel founds nothing", dain.All());

        // /house found "<name>" without a sigil asks for the sigil only
        Clear(dain);
        House(dain, "found", "Dunmere");
        Ok(dain.Popups.Count == 1 && dain.LastPopup.Message.Contains("House Dunmere will bear") && dain.All().Contains("Choose the sigil of House Dunmere in the window"),
            "/house found <name> asks for the sigil only, with its chat fallback", dain.All());
        dain.LastPopup.Answer(Options.Yes, "[FF0000]red");
        Ok(HouseOf(dain) == null && dain.LastPopup.Message.StartsWith("A sigil must be 1-32 characters"), "a sigil with a colour tag is refused and asked again", dain.LastPopup.Message);
        dain.LastPopup.Answer(Options.Yes, "a grey tower");
        Ok(HouseOf(dain) == "Dunmere", "then the house is founded");
        Clear(edda);
        House(edda, "found", "Varrow");
        Ok(edda.Popups.Count == 0 && edda.All().Contains("That name is already taken"), "/house found <taken name> is refused before any window", edda.All());

        // a newer window replaces an older one; the older one's answer is ignored
        Clear(edda);
        House(edda, "found");
        var older = edda.LastPopup;
        House(edda, "found");
        older.Answer(Options.Yes, "Halloran");
        Ok(edda.Popups.Count == 2, "an answer to a replaced window does nothing");
        edda.LastPopup.Answer(Options.Yes, "Halloran");
        Ok(edda.Popups.Count == 3 && edda.LastPopup.Message.Contains("Halloran"), "the newest window is the one that counts");

        // already in a house, or popups off: no window
        Clear(ada);
        House(ada, "found");
        Ok(ada.Popups.Count == 0 && ada.All().Contains("You already belong to House"), "a member of a house is refused before any window", ada.All());
        PopupsOff.Add(edda.Id.ToString());
        Clear(edda);
        House(edda, "found");
        Ok(edda.Popups.Count == 0 && edda.All().Contains("Found a house, gather sworn members"), "a player with /realm popups off gets the chat help instead", edda.All());
        PopupsOff.Remove(edda.Id.ToString());

        // ---------------- Swearing: Yes/No for the oath, Accept/Refuse for the liege ----------------
        Clear(ada, cass);
        Swear(ada, "Varrow");
        var oath = ada.LastPopup;
        Ok(ada.Popups.Count == 1 && oath.Kind == "confirm" && oath.Title == "An oath of fealty" && oath.Buttons[0] == "Swear" && oath.Buttons[1] == "Not now",
            "/swear <house> asks Yes/No in a window", oath == null ? "none" : oath.Title);
        Ok(oath.Message.Contains("Offer the oath of House Ashgrove to House Varrow?") && oath.Message.Contains("The leader of House Varrow must still accept it."),
            "the window names both houses and that the liege must accept", oath.Message);
        Ok(ada.All().Contains("Answer the window, or type [F4C96D]/swear confirm[FFFFFF] within 2m"), "with it, the chat fallback", ada.All());
        Ok(cass.Messages.Count == 0 && cass.Popups.Count == 0, "nothing is offered before the answer");
        oath.Answer(Options.Yes);
        Ok(ada.All().Contains("Your oath has been offered to House"), "Swear offers the oath", ada.All());
        var fealty = cass.LastPopup;
        Ok(cass.All().Contains("offers fealty to your house. Type [F4C96D]/swear accept[FFFFFF]") && fealty != null && fealty.Kind == "confirm"
            && fealty.Title == "An oath is offered" && fealty.Buttons[0] == "Accept" && fealty.Buttons[1] == "Refuse",
            "the liege's leader gets the chat line and an Accept/Refuse window", cass.All());
        fealty.Answer(Options.Yes);
        Ok(LiegeOf("Ashgrove") == "Varrow" && B().Contains("bends the knee") && Chronicle.Calls.Any(c => c.StartsWith("Log(oath_sworn")), "Accept swears the oath", B());
        oath.Answer(Options.Yes); fealty.Answer(Options.Yes);
        Ok(Chronicle.Calls.Count(c => c.StartsWith("Log(oath_sworn")) == 1, "answering the same windows again does nothing");

        // chat fallback: /swear confirm, then /swear deny; the windows' later answers are ignored
        Clear(dain, cass);
        Swear(dain, "Varrow");
        var oath2 = dain.LastPopup;
        Swear(dain, "confirm");
        Ok(dain.All().Contains("Your oath has been offered to House"), "/swear confirm answers in chat", dain.All());
        oath2.Answer(Options.Yes);
        Ok(cass.Popups.Count == 1 && cass.Messages.Count(m => m.Contains("offers fealty")) == 1, "the window answered after /swear confirm offers nothing twice", cass.All());
        var fealty2 = cass.LastPopup;
        Swear(cass, "deny", "Dunmere");
        Ok(cass.All().Contains("You refuse the oath of House") && dain.All().Contains("has refused your oath"), "/swear deny in chat", cass.All());
        fealty2.Answer(Options.Yes);
        Ok(LiegeOf("Dunmere") == null, "the window answered after /swear deny swears nothing");

        // Not now; /swear confirm with nothing waiting; an expired window
        Clear(dain, cass);
        Swear(dain, "Varrow");
        dain.LastPopup.Answer(Options.No);
        Ok(dain.All().Contains("Your house swears no oath.") && cass.Messages.Count == 0, "Not now offers nothing", dain.All());
        Clear(dain);
        Swear(dain, "confirm");
        Ok(dain.All().Contains("ERR") && dain.All().Contains("No oath is waiting to be confirmed"), "/swear confirm with nothing waiting is refused", dain.All());
        Swear(dain, "Varrow");
        var asks = (IDictionary)F(H, "popupAsks");
        SetF(asks[dain.Id.ToString()], "Expires", DateTime.UtcNow.AddSeconds(-1));
        dain.LastPopup.Answer(Options.Yes);
        Ok(cass.Messages.Count == 0, "a window answered after its deadline does nothing");

        // the leader is checked again when the answer comes: a demoted leader cannot confirm
        Clear(dain, cass);
        Swear(dain, "Varrow");
        var stale = dain.LastPopup;
        House(dain, "disband");
        stale.Answer(Options.Yes);
        Ok(cass.Messages.Count(m => m.Contains("offers fealty")) == 0, "an answer after the house is gone offers nothing", dain.All());

        // the liege's leader is offline: refused before any window
        var fynn = Mk(76561190000000005, "Fynn");
        House(fynn, "found", "Merrin", "a", "blue", "heron");
        Server.ClientPlayers.Remove(cass);
        Clear(fynn);
        Swear(fynn, "Varrow");
        Ok(fynn.Popups.Count == 0 && fynn.All().Contains("has no leader online"), "no window when the liege's leader is not online", fynn.All());
        Server.ClientPlayers.Add(cass);

        // a player who left before answering
        Clear(fynn, cass);
        Swear(fynn, "Varrow");
        Server.ClientPlayers.Remove(fynn);
        fynn.LastPopup.Answer(Options.Yes);
        Ok(cass.Messages.Count == 0, "an answer from a player who is no longer online does nothing");
        Server.ClientPlayers.Add(fynn);

        // ---------------- Renouncing ----------------
        Clear(ada, cass);
        Renounce(ada);
        var ren = ada.LastPopup;
        Ok(ren != null && ren.Kind == "confirm" && ren.Title == "Break your oath?" && ren.Buttons[0] == "Renounce" && ren.Buttons[1] == "Keep the oath",
            "/renounce asks Yes/No in a window", ren == null ? "none" : ren.Title);
        Ok(ren.Message.Contains("Renounce the oath of House Ashgrove to House Varrow?") && ren.Message.Contains("cannot swear again for 1d 0h"), "the window says what it costs", ren.Message);
        Ok(ada.Messages[0].StartsWith("[E8913A]Houses[FFFFFF]: Renouncing your oath") && ada.Messages[0].Contains("Answer the window, or type [F4C96D]/renounce confirm[FFFFFF] within 60 seconds"),
            "with it, the chat fallback in the take-care tone", ada.All());
        ren.Answer(Options.No);
        Ok(LiegeOf("Ashgrove") == "Varrow" && ada.All().Contains("Your house keeps its oath to House"), "Keep the oath keeps it", ada.All());
        Renounce(ada, "confirm");
        Ok(LiegeOf("Ashgrove") == "Varrow", "after Keep the oath, /renounce confirm needs a new /renounce");
        Clear(ada);
        Renounce(ada);
        ada.LastPopup.Answer(Options.Yes);
        Ok(LiegeOf("Ashgrove") == null && B().Contains("breaks its oath") && Chronicle.Calls.Any(c => c.StartsWith("Log(oath_broken")), "Renounce breaks the oath", B());
        var rep = (IDictionary)Inv(H, "GetReputation", "Ashgrove");
        Ok((int)rep["oathsBroken"] == 1, "the oathbreaker mark is set once", rep["oathsBroken"].ToString());

        // renouncing in chat while the window is open; the window's later Yes does nothing
        Inv(H, "Swear", FindHouse("Ashgrove"), FindHouse("Varrow"));
        SetF(FindHouse("Ashgrove"), "SwearBlockedUntil", DateTime.MinValue);
        Clear(ada);
        Renounce(ada);
        var ren2 = ada.LastPopup;
        Renounce(ada, "confirm");
        int marks = (int)((IDictionary)Inv(H, "GetReputation", "Ashgrove"))["oathsBroken"];
        Inv(H, "Swear", FindHouse("Ashgrove"), FindHouse("Varrow"));
        ren2.Answer(Options.Yes);
        Ok(marks == 2 && LiegeOf("Ashgrove") == "Varrow", "a window answered after /renounce confirm breaks no second oath", LiegeOf("Ashgrove") ?? "none");

        // ---------------- Switches and failures ----------------
        // UsePopups off: /swear offers at once and /renounce asks in chat, as before popups
        SetF(Cfg(), "UsePopups", false);
        Clear(fynn, cass);
        Swear(fynn, "Varrow");
        Ok(fynn.Popups.Count == 0 && fynn.All().Contains("Your oath has been offered") && cass.Popups.Count == 0 && cass.All().Contains("offers fealty"),
            "UsePopups off: /swear offers at once and the liege gets the chat line only", fynn.All());
        Clear(ada);
        Renounce(ada);
        Ok(ada.Popups.Count == 0 && ada.Messages[0].Contains("Type [F4C96D]/renounce confirm[FFFFFF] within 60 seconds.") && !ada.All().Contains("window"),
            "UsePopups off: /renounce asks in chat", ada.All());
        SetF(Cfg(), "UsePopups", true);
        Swear(cass, "deny", "Merrin");

        // the game throws when opening a window: the chat path stands
        CodeHatch.Common.PlayerExtensions.PopupsFail = true;
        Clear(fynn, cass);
        Swear(fynn, "Varrow");
        Ok(fynn.All().Contains("Your oath has been offered") && cass.All().Contains("offers fealty") && H.Log.Any(l => l.StartsWith("WARN ShowConfirmPopup failed")),
            "a window that cannot open: the oath is offered at once and the liege answers in chat", fynn.All());
        Clear(edda);
        House(edda, "found");
        Ok(edda.All().Contains("Found a house, gather sworn members") && H.Log.Any(l => l.StartsWith("WARN ShowInputPopup failed")), "/house found falls back to the chat help", edda.All());
        CodeHatch.Common.PlayerExtensions.PopupsFail = false;
        Swear(cass, "deny", "Merrin");

        // without RealmHerald the plugin still opens windows
        SetF(H, "RealmHerald", null);
        Clear(edda);
        House(edda, "found");
        Ok(edda.Popups.Count == 1, "RealmHerald not loaded: windows still open");
        SetF(H, "RealmHerald", Herald);

        // windows are plain text
        var all = new[] { ada, cass, dain, edda, fynn }.SelectMany(p => p.Popups).ToList();
        Clear(ada, cass, edda);
        Renounce(ada); Swear(edda, "x"); House(edda, "found");
        all.AddRange(new[] { ada, edda }.SelectMany(p => p.Popups));
        Ok(all.All(p => !Regex.IsMatch(p.Title + p.Message + string.Join("", p.Buttons), @"\[[0-9A-Fa-f]{6}\]")), "every window is plain text, without chat colour tags");

        // after Unload (a reload), answers to windows still open do nothing
        Clear(ada);
        Renounce(ada);
        var open = ada.LastPopup;
        Inv(H, "Unload");
        open.Answer(Options.Yes);
        Ok(LiegeOf("Ashgrove") == "Varrow" && ada.Messages.Count == 1, "a window answered after Unload does nothing");

        // ---------------- Data survives a reload ----------------
        H = NewHouses();
        Ok(HouseOf(ada) == "Ashgrove" && LiegeOf("Ashgrove") == "Varrow" && HouseOf(dain) == null, "houses and oaths survive a reload");
    }

    static object FindHouse(string name) { return Inv(H, "FindHouse", name); }
}
