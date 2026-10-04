// Behaviour tests for plugins/RealmTravel.cs, compiled unchanged with Mocks.cs and World.cs. Run with run.sh.
// What this proves: the plugin's own rules (waystones, journeys, tolls, blockers, home, roads, kits, wayfarer, data
// safety, config). What it does NOT prove: that the real game behaves like the mocks (see plugins/docs/RealmTravel.md).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.Thrones.AncientThrone;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static class Tests
{
    const ulong A = 76561190000000001, B = 76561190000000002, C = 76561190000000003, Dd = 76561190000000004, E = 76561190000000005,
        G = 76561190000000006, H = 76561190000000007, J = 76561190000000008, K = 76561190000000009;

    static int Main(string[] argv)
    {
        Setup(); AdminWaystones(); Discovery(); Journeys(); Tolls(); Breaking(); Blockers(); SeatAccess(); RecheckOnArrival();
        Shield(); TeleportFaults(); Home(); Roads(); StarterKit(); HouseKit(); SeasonKit(); KitDelivery(); KitItems(); Wayfarer();
        DataSafety(); Config(); Api(); ChatStyle();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    // A realm with four waystones: the capital (500,500), Varrow Hall (1500,500, seat of Varrow), Greywater (500,1500,
    // a holding) and the Old Mill (-800,200, a landmark). The admin Ada stands at the origin.
    static Player World4(out Player admin)
    {
        Reset();
        NewTravel();
        admin = Mk(A, "Ada");
        Admin(admin);
        Raise(admin, "kingsreach", "capital", 500, 500, "Kingsreach");
        Raise(admin, "varrow-hall", "seat", 1500, 500, "Varrow Hall");
        Cmd(admin, "travel", "admin", "house", "varrow-hall", "Varrow");
        Raise(admin, "greywater", "holding", 500, 1500, "Greywater");
        Raise(admin, "old-mill", "landmark", -800, 200, "Old Mill");
        Clear();
        return admin;
    }

    static void Setup()
    {
        Reset();
        NewTravel();
        Ok(T.permission.Registered.Contains("realmtravel.admin"), "registers realmtravel.admin");
        Ok(File.Exists(Path.Combine(Dir, "RealmTravel.json")), "a new realm writes its data file");
        var kits = (IList)F(Cfg("Kits"), "List");
        Ok(kits.Count == 3 && kits.Cast<object>().Select(k => (string)F(k, "Kind")).SequenceEqual(new[] { "starter", "house", "season" }), "three default kits: starter, house, season");
        Ok(!T.Logged.Any(l => l.Contains("is not known")), "every default kit item resolves against the item list (ResourceType names)", string.Join("\n", T.Logged));
        Ok(T.timer.EveryCount == 1 && Math.Abs(T.timer.LastEvery - 1f) < 0.01f, "one tick timer, every second");
        var lang = T.lang.Msgs;
        Ok(lang.Values.All(v => Regex.Replace(v, @"\[[0-9A-Fa-f]{6}\]", "").Length <= 200), "no lang line is longer than 200 visible characters");
        Ok(lang["Speaker"] == "Roads" && lang["Herald"] == "[D6A043]Herald[FFFFFF]: ", "speaker Roads; the one Herald voice");
    }

    static void AdminWaystones()
    {
        Reset();
        NewTravel();
        var ada = Mk(A, "Ada"); var bob = Mk(B, "Bob");
        string r = Cmd(bob, "travel", "admin", "set", "kingsreach", "capital");
        Ok(r.StartsWith("ERR ") && r.Contains("You may not do that."), "a player cannot raise a waystone", r);
        Admin(ada);
        At(ada, 500, 500);
        r = Cmd(ada, "travel", "admin", "set", "kingsreach", "capital", "Kingsreach");
        Ok(r.Contains("[8FC97A]Roads[FFFFFF]: Waystone Kingsreach (kingsreach) raised here, radius 10 m."), "an admin raises a waystone where they stand", r);
        Ok(r.Contains("[F4C96D]/sculpt place[FFFFFF] heralds-pillar"), "the capital is offered the Herald's Pillar monument (RealmSculptor)", r);
        var ws = ((IDictionary)D("Waystones"))["kingsreach"];
        Ok(Math.Abs((float)F(ws, "X") - 500) < 0.01 && Math.Abs((float)F(ws, "Z") - 500) < 0.01 && (string)F(ws, "Kind") == "capital", "stored at the admin's position, kind capital");
        Ok(Cmd(ada, "travel", "admin", "set", "x", "capital").Contains("A waystone id is 2 to 24"), "a one-letter id is refused");
        Ok(Cmd(ada, "travel", "admin", "set", "bad id!", "capital").Contains("A waystone id is 2 to 24"), "an id with odd characters is refused");
        Ok(Cmd(ada, "travel", "admin", "set", "mill", "castle").Contains("The kind is capital, seat, holding or landmark."), "an unknown kind is refused");
        Ok(Cmd(ada, "travel", "admin", "set", "mill").Contains("Usage: [F4C96D]/travel admin set[FFFFFF]"), "set without a kind shows the usage");
        At(ada, 1500, 500);
        r = Cmd(ada, "travel", "admin", "set", "varrow-hall", "seat", "Varrow", "Hall");
        Ok(r.Contains("Waystone Varrow Hall (varrow-hall)") && r.Contains("[F4C96D]/travel admin house[FFFFFF] varrow-hall <house>"), "a seat asks for its house", r);
        r = Cmd(ada, "travel", "admin", "house", "varrow-hall", "Varrow");
        Ok(r.Contains("Done: varrow-hall house Varrow") && r.Contains("[F4C96D]/sculpt place[FFFFFF] house-varrow"), "naming a great house offers its monument", r);
        At(ada, 1510, 505);
        r = Cmd(ada, "travel", "admin", "set", "varrow-hall", "seat");
        Ok(r.Contains("Waystone Varrow Hall moved here.") && Math.Abs((float)F(((IDictionary)D("Waystones"))["varrow-hall"], "X") - 1510) < 0.01, "set on an existing id moves it and keeps its name", r);
        Ok(Cmd(ada, "travel", "admin", "name", "kingsreach", "Kingsreach", "the", "Crowned").Contains("Done: kingsreach = Kingsreach the Crowned"), "rename");
        Ok(Cmd(ada, "travel", "admin", "name", "kingsreach", "[FF0000]Evil{0}").Contains("= FF0000Evil0"), "colour tags and braces are stripped from names");
        Cmd(ada, "travel", "admin", "name", "kingsreach", "Kingsreach");
        Ok(Cmd(ada, "travel", "admin", "note", "kingsreach", "Where", "the", "six", "roads", "meet.").Contains("Done: kingsreach note"), "a lore note");
        Ok(Cmd(ada, "travel", "admin", "kind", "kingsreach", "palace").Contains("The kind is capital"), "a bad kind is refused");
        Ok(Cmd(ada, "travel", "admin", "toll", "kingsreach", "7").Contains("toll 7") && (long)F(((IDictionary)D("Waystones"))["kingsreach"], "Toll") == 7, "an explicit toll");
        Ok(Cmd(ada, "travel", "admin", "toll", "kingsreach", "-3").Contains("Usage"), "a negative toll is refused");
        Ok(Cmd(ada, "travel", "admin", "toll", "kingsreach", "auto").Contains("toll auto"), "the toll back to distance");
        Ok(Cmd(ada, "travel", "admin", "radius", "kingsreach", "500").Contains("Usage") && Cmd(ada, "travel", "admin", "radius", "kingsreach", "15").Contains("radius 15"), "radius within 2-100 m");
        Ok(Cmd(ada, "travel", "admin", "hidden", "kingsreach", "maybe").Contains("Usage") && Cmd(ada, "travel", "admin", "hidden", "kingsreach", "off").Contains("hidden off"), "hidden on|off");
        Ok(Cmd(ada, "travel", "admin", "mark", "kingsreach", "heralds-pillar.json").Contains("mark heralds-pillar"), "mark with a sculpture id (a .json name is accepted)");
        Ok(Cmd(ada, "travel", "admin", "mark", "kingsreach", "../evil").Contains("Usage"), "a path is not a sculpture id");
        Ok(Cmd(ada, "travel", "admin", "name", "nowhere", "X").Contains("No waystone with the id 'nowhere'"), "an unknown id is refused");
        r = Cmd(ada, "travel", "admin", "list");
        Ok(r.Contains("Waystones (2):") && r.Contains("  kingsreach 'Kingsreach' capital at 500,10,500 r15 toll auto - found by 0")
            && r.Contains("  varrow-hall 'Varrow Hall' seat (Varrow) at 1510,10,505"), "the admin list", r);
        // Remove asks first; the confirm only counts within two minutes of the ask.
        Unlocked(bob).Add("varrow-hall");
        r = Cmd(ada, "travel", "admin", "remove", "varrow-hall", "confirm");
        Ok(r.Contains("This removes Varrow Hall") && ((IDictionary)D("Waystones")).Contains("varrow-hall"), "remove asks first (a bare confirm is not enough)", r);
        Clock = Clock.AddMinutes(3);
        Ok(Cmd(ada, "travel", "admin", "remove", "varrow-hall", "confirm").Contains("This removes"), "a confirm after two minutes asks again");
        r = Cmd(ada, "travel", "admin", "remove", "varrow-hall", "confirm");
        Ok(r.Contains("Waystone Varrow Hall is removed.") && !((IDictionary)D("Waystones")).Contains("varrow-hall") && !Unlocked(bob).Contains("varrow-hall"),
            "confirmed: gone, and forgotten by everyone who found it", r);
        At(ada, 300, 300);
        r = Cmd(ada, "travel", "admin", "throne");
        Ok(r.Contains("The Old Throne's point is set here (radius 80 m in rebellions).") && r.Contains("300,10,300 (admin)"), "the throne point from where the admin stands", r);
        AncientThrone.EntityPosition = new UnityEngine.Vector3(10, 5, 20);
        r = Cmd(ada, "travel", "admin", "throne", "clear");
        Ok(r.Contains("10,5,20 (game)"), "cleared: the game's own throne position (AncientThrone.EntityPosition)", r);
        r = Cmd(ada, "travel", "admin", "tp", "kingsreach");
        Ok(r.Contains("You are at Kingsreach.") && Math.Abs(Pos(ada).x - 500) < 0.01 && Graces.Contains(A + "|10"), "admin tp moves the admin (Sentinel grace first)", r);
        r = Cmd(ada, "travel", "admin", "status");
        Ok(r.Contains("Roads: 1 waystone(s)") && r.Contains("Mode: teleport") && r.Contains("Treasury: loaded") && r.Contains("Kits: starter (starter), house (house), season (season)"), "status", r);
        Ok(Cmd(ada, "travel", "admin").Contains("Admin: [F4C96D]/travel admin[FFFFFF] set"), "admin help");
        Ok(Cmd(bob, "travel", "admin", "tp", "kingsreach").Contains("You may not do that.") && Math.Abs(Pos(bob).x) < 0.01, "a player cannot use admin tp");
        Ok(Cmd(bob, "travel", "admin", "unlock", "Bob", "all").Contains("You may not do that."), "a player cannot unlock waystones");
        r = Cmd(ada, "travel", "admin", "unlock", "Bob", "all");
        Ok(r.Contains("Bob now knows 1 waystone(s).") && Unlocked(bob).Contains("kingsreach"), "admin unlock", r);
        Ok(Cmd(ada, "travel", "admin", "lock", "Bob", "kingsreach").Contains("Bob now knows 0") && Unlocked(bob).Count == 0, "admin lock");
        Ok(Cmd(ada, "travel", "admin", "unlock", "Nobody", "all").Contains("No such person"), "unlock an unknown player");
    }

    static void Discovery()
    {
        Player ada;
        World4(out ada);
        var bob = Mk(B, "Bob");
        Tick(3);
        Ok(Unlocked(bob).Count == 0, "standing far away learns nothing");
        At(bob, 506, 504);
        Tick(2);
        Ok(Unlocked(bob).Contains("kingsreach"), "coming within the radius learns the waystone");
        Ok(bob.All().Contains("[8FC97A]Roads[FFFFFF]: You have found the waystone Kingsreach. Return here any time: [F4C96D]/travel[FFFFFF] kingsreach")
            && bob.All().Contains("  You know 1 of 4 waystones."), "told in the done tone, with the count", bob.All());
        Ok(bob.Popups.Count == 1 && bob.Popups[0].StartsWith("Waystone found|Kingsreach\nthe capital\nReturn here any time: /travel kingsreach|Onward|True"),
            "a discovery window, plain text, broadcast = true", string.Join("\n", bob.Popups));
        Ok((int)F(((IDictionary)D("Waystones"))["kingsreach"], "Visitors") == 1, "the waystone counts its visitors");
        Tick(5);
        Ok(Unlocked(bob).Count(x => x == "kingsreach") == 1 && bob.Popups.Count == 1, "staying there learns it only once");
        var cat = Mk(C, "Cat");
        PopupsOff.Add(C.ToString());
        At(cat, 500, 500); Tick(2);
        Ok(Unlocked(cat).Contains("kingsreach") && cat.Popups.Count == 0 && cat.All().Contains("You have found"), "/realm popups off: chat only (RealmHerald.PopupsWanted)");
        var dan = Mk(Dd, "Dan");
        dan.PopupsThrow = true;
        At(dan, 500, 500); Tick(2);
        Ok(dan.All().Contains("You have found") && T.Logged.Any(l => l.StartsWith("WARN ShowPopup failed")), "a window the game cannot open: the chat stands");
        Cmd(ada, "travel", "admin", "enabled", "old-mill", "off");
        At(dan, -800, 200); Tick(2);
        Ok(!Unlocked(dan).Contains("old-mill"), "a closed waystone is not learned");
        Cmd(ada, "travel", "admin", "enabled", "old-mill", "on");
        At(ada, 0, 900);
        Cmd(ada, "travel", "admin", "set", "hermits-cave", "landmark", "Hermit's", "Cave");
        Cmd(ada, "travel", "admin", "hidden", "hermits-cave", "on");
        At(ada, 0, 0);
        string all = Cmd(bob, "travel", "all");
        Ok(all.Contains("Waystones of the realm (4):") && !all.Contains("Hermit"), "a hidden waystone is not listed before it is found", all);
        Ok(Cmd(bob, "road", "hermits-cave").Contains("No waystone called 'hermits-cave'"), "nor can a road be followed to it");
        At(bob, 2, 902); Tick(2);
        Ok(Unlocked(bob).Contains("hermits-cave") && !bob.All().Contains("You know 2 of"), "found by walking there (no count line for a hidden one)", bob.All());
        Ok(Cmd(bob, "travel", "all").Contains("Hermit's Cave (a landmark)"), "found, it is listed for that player");
        var eve = Mk(E, "Eve");
        Set("Discovery", "PopupOnDiscovery", false);
        At(eve, 500, 500); Tick(2);
        Ok(eve.Popups.Count == 0 && Unlocked(eve).Contains("kingsreach"), "PopupOnDiscovery false: no window");
        Set("Discovery", "Enabled", false);
        At(eve, -800, 200); Tick(3);
        Ok(!Unlocked(eve).Contains("old-mill"), "Discovery.Enabled false: nothing is learned");
    }

    static void Journeys()
    {
        Player ada;
        World4(out ada);
        var carl = Mk(C, "Carl");
        Visit(carl, 500, 500); Visit(carl, -800, 200);
        Clear();
        string list = Cmd(carl, "travel");
        Ok(list.Contains("Waystones you know (2 of 4):") && list.Contains("  Kingsreach (the capital) - 710 m north-east - toll 17")
            && list.Contains("  Old Mill (a landmark) - 820 m west - toll 18") && list.Contains("  2 more to find. [F4C96D]/road[FFFFFF] <name> shows the way.")
            && list.Contains("  Your next journey: now.") && list.Contains("  Free journeys left: 3."), "/travel lists known waystones, nearest first, with tolls", list);
        Ok(list.IndexOf("Kingsreach") < list.IndexOf("Old Mill"), "nearest first");
        string r = Cmd(carl, "travel", "kingsreach");
        Ok(r.Contains("[E8913A]Roads[FFFFFF]: You set out for Kingsreach. Stand still for 10 s: moving, fighting or being hurt breaks the journey.")
            && r.Contains("  This journey is free: one of your first journeys."), "setting out: stand still, and the first journeys are free", r);
        Ok(Travelling(carl), "the journey is under way");
        Tick(9);
        Ok(Math.Abs(Pos(carl).x) < 0.01 && Travelling(carl), "nothing happens before the channel time");
        Tick(1);
        Ok(Math.Abs(Pos(carl).x - 500) < 0.01 && Math.Abs(Pos(carl).y - 10.5f) < 0.01 && Math.Abs(Pos(carl).z - 500) < 0.01, "after 10 s the road takes them to the waystone (+0.5 m)", Pos(carl).ToString());
        Ok(carl.All().Contains("[8FC97A]Roads[FFFFFF]: You arrive at Kingsreach.") && carl.All().Contains("  Free journeys left: 2.")
            && carl.All().Contains("  The road watches over you for 5 s, unless you strike first."), "arrival told, free journeys left, the arrival shield", carl.All());
        Ok(Graces.Contains(C + "|10") && Charges.Count == 0, "RealmSentinel is asked for movement grace; a free trip charges nothing");
        Ok(!Travelling(carl) && (int)D("Trips") == 1, "the journey is done and counted");
        r = Cmd(carl, "travel", "old-mill");
        Ok(r.Contains("ERR [E86A5C]Roads[FFFFFF]: Your next journey is in 10 min."), "the cooldown, in the refused tone", r);
        Clock = Clock.AddMinutes(10);
        Cmd(carl, "travel", "old"); Tick(10);
        Ok(Math.Abs(Pos(carl).x + 800) < 0.01, "a unique start of a name is enough (old -> Old Mill)");
        Clock = Clock.AddMinutes(10);
        Cmd(carl, "travel", "Kingsreach"); Tick(10);
        Ok(Math.Abs(Pos(carl).x - 500) < 0.01 && carl.All().Contains("Free journeys left: 0."), "the third free journey (exact name)");
        Clock = Clock.AddMinutes(10);
        Clear();
        r = Cmd(carl, "travel", "old-mill");
        Ok(r.Contains("The toll is 23 marks; your purse holds 0. [F4C96D]/purse[FFFFFF]") && !Travelling(carl), "after the free journeys the toll is due; a short purse is refused", r);
        Purses[C.ToString()] = 100;
        r = Cmd(carl, "travel", "old-mill");
        Ok(r.Contains("  Toll on arrival: 23 marks, paid to the crown's treasury."), "the toll is quoted on setting out", r);
        Tick(10);
        Ok(Charges.Count == 1 && Charges[0] == C + "|Carl|23|RealmTravel|road toll: old-mill" && Purses[C.ToString()] == 77 && TreasuryMarks == 23,
            "on arrival RealmTreasury.ChargeMarks moves the toll from the purse to the crown's treasury", string.Join("\n", Charges));
        Ok(carl.All().Contains("  23 marks paid to the crown's treasury.") && (long)D("TollsCollected") == 23, "told and counted");
        Ok(Cmd(carl, "travel", "kingsreach", "palace").Contains("No waystone called 'kingsreach palace'"), "extra words that name nothing are refused");
        Clock = Clock.AddMinutes(10);
        Ok(Cmd(carl, "travel", "greywater").Contains("You have not been to Greywater yet. [F4C96D]/road[FFFFFF] greywater shows the way."), "a waystone not yet found");
        Ok(Cmd(carl, "travel", "atlantis").Contains("No waystone called 'atlantis'. [F4C96D]/travel all[FFFFFF] names them."), "an unknown waystone");
        Raise(ada, "oak-cross", "landmark", 3000, 0, "Oak Cross");
        Raise(ada, "oak-ford", "landmark", 3000, 900, "Oak Ford");
        Ok(Cmd(carl, "travel", "oak").Contains("Which one: Oak Cross, Oak Ford?"), "an ambiguous start asks which one");
        string info = Cmd(carl, "travel", "info", "kingsreach");
        Ok(info.Contains("Kingsreach (the capital):") && info.Contains("Found by 1 traveller(s).") && info.Contains("  Closed to those exiled from the towns."), "/travel info", info);
        Ok(Cmd(carl, "travel", "help").Contains("Waystones: [F4C96D]/travel[FFFFFF] lists those you know"), "/travel help");
        var newbie = Mk(Dd, "Newt");
        Ok(Cmd(newbie, "travel").Contains("You know no waystone yet."), "a newcomer knows none, and is told how to learn them");
    }

    static void Tolls()
    {
        Player ada;
        World4(out ada);
        var t = Mk(C, "Tess", 0, 0, "Varrow");
        foreach (var id in new[] { "kingsreach", "varrow-hall", "greywater", "old-mill" }) Unlocked(t).Add(id);
        F(Rec(t), "FreeTripsUsed");
        SetF(Rec(t), "FreeTripsUsed", 3);
        Purses[C.ToString()] = 1000;
        Ok(Cmd(t, "travel", "varrow-hall").Contains("Toll on arrival: 25 marks"), "distance toll: 10 + 1 per 100 m (1581 m -> 25)");
        Cmd(t, "travel", "cancel");
        Raise(ada, "far-isle", "landmark", 9000, 9000, "Far Isle");
        Unlocked(t).Add("far-isle");
        Ok(Cmd(t, "travel", "far-isle").Contains("Toll on arrival: 50 marks"), "at most MaxToll (50)");
        Cmd(t, "travel", "cancel");
        Cmd(ada, "travel", "admin", "toll", "far-isle", "3");
        Ok(Cmd(t, "travel", "far-isle").Contains("Toll on arrival: 3 marks"), "an explicit toll replaces the distance toll");
        Cmd(t, "travel", "cancel");
        Cmd(ada, "travel", "admin", "toll", "far-isle", "0");
        Ok(Cmd(t, "travel", "far-isle").Contains("This journey is free: this road takes no toll."), "a toll of 0 is a free road");
        Cmd(t, "travel", "cancel");
        Sworn.Add("Varrow");
        Ok(Cmd(t, "travel", "varrow-hall").Contains("Toll on arrival: 12 marks"), "a house sworn to the crown pays half (SwornTollPercent 50)");
        Cmd(t, "travel", "cancel");
        Decrees.Add("roads");
        Ok(Cmd(t, "travel", "varrow-hall").Contains("This journey is free: the crown has opened the roads."), "the Open Roads decree waives the toll");
        Cmd(t, "travel", "cancel");
        Decrees.Clear(); Sworn.Clear();
        string list = Cmd(t, "travel");
        Ok(list.Contains("Varrow Hall (seat of [C58FC0]Varrow[FFFFFF]) - 1.6 km east - toll 25"), "the list shows the house in its tint and km for long roads", list);
        // Treasury not loaded.
        Absent.Add("RealmTreasury");
        Reload();
        Ok(Cmd(t, "travel", "varrow-hall").Contains("This journey is free: the toll-keepers are away."), "RealmTreasury not loaded: free by default (TollFreeWithoutTreasury)");
        Cmd(t, "travel", "cancel");
        Reload(c => Section(c, "Travel", "TollFreeWithoutTreasury", false));
        Ok(Cmd(t, "travel", "varrow-hall").Contains("The toll-keepers are away (the treasury is not open)."), "TollFreeWithoutTreasury false: refused");
        Absent.Clear();
        Reload();
        // The treasury refuses the charge at arrival (it was checked, but something went wrong): the journey stands.
        Clear();
        TreasuryRefuses = true;
        Cmd(t, "travel", "varrow-hall"); Tick(10);
        Ok(Math.Abs(Pos(t).x - 1500) < 0.01 && (int)D("UnpaidTolls") == 1 && T.Logged.Any(l => l.Contains("was not collected")) && !t.All().Contains("marks paid"),
            "a refused charge after the move is logged and counted, never charged twice", t.All());
        TreasuryRefuses = false;
    }

    static void Breaking()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell"); var foe = Mk(Dd, "Foe", 3, 3);
        Unlocked(p).Add("kingsreach");
        Cmd(p, "travel", "kingsreach");
        At(p, 1, 0.5f); Tick(1);
        Ok(Travelling(p), "a small drift (under MoveTolerance) does not break the journey");
        At(p, 3, 0); Tick(1);
        Ok(!Travelling(p) && p.All().Contains("ERR [E86A5C]Roads[FFFFFF]: You moved, and the journey is broken."), "moving breaks the journey", p.All());
        At(p, 0, 0);
        Cmd(p, "travel", "kingsreach");
        Fall(p);
        Ok(!Travelling(p) && p.All().Contains("You were hurt, and the journey is broken."), "being hurt breaks it (even a fall)");
        Cmd(p, "travel", "kingsreach");
        Ok(Travelling(p), "a fall is not a fight: they may set out again at once");
        Hit(p, foe);
        Ok(!Travelling(p) && p.All().Contains("You struck a blow, and the journey is broken."), "striking a blow breaks it");
        Clock = Clock.AddSeconds(40);
        Cmd(p, "travel", "kingsreach");
        Die(p);
        Ok(!Travelling(p), "death ends it");
        Cmd(p, "travel", "kingsreach");
        Offline(p);
        Ok(!Travelling(p), "logging out ends it");
        Online(p);
        Unlocked(p).Add("old-mill");
        Cmd(p, "travel", "kingsreach");
        Ok(Cmd(p, "travel", "old-mill").Contains("You are already on the road to Kingsreach. [F4C96D]/travel cancel[FFFFFF] stops."), "one journey at a time");
        Ok(Cmd(p, "travel", "cancel").Contains("You stay where you are.") && !Travelling(p), "/travel cancel");
        Ok(Cmd(p, "travel", "cancel").Contains("You are not on the road."), "nothing to cancel");
        Tick(15);
        Ok(Math.Abs(Pos(p).x) < 0.01, "a cancelled journey never moves anyone");
    }

    static void Blockers()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell"); var foe = Mk(Dd, "Foe", 2, 2);
        foreach (var id in new[] { "kingsreach", "varrow-hall", "greywater", "old-mill" }) Unlocked(p).Add(id);
        Hit(foe, p);
        Ok(Cmd(p, "travel", "kingsreach").Contains("You are in a fight. Wait 30 s after the last blow."), "no journey within 30 s of a player's blow");
        Ok(Cmd(foe, "travel", "kingsreach").Contains("You have not been to") , "(the attacker is tagged too, but knows no waystone)");
        Unlocked(foe).Add("kingsreach");
        Ok(Cmd(foe, "travel", "kingsreach").Contains("You are in a fight."), "the attacker is in the fight as well");
        Clock = Clock.AddSeconds(31);
        Ok(Cmd(p, "travel", "kingsreach").Contains("You set out"), "after the fight window, the road is open"); Cmd(p, "travel", "cancel");
        Beast(p);
        Ok(Cmd(p, "travel", "kingsreach").Contains("Wait 10 s"), "a creature's bite: 10 s");
        Clock = Clock.AddSeconds(11);
        WardenFight.Add(C);
        Ok(Cmd(p, "travel", "kingsreach").Contains("You are in a fight. Wait until it is over."), "RealmWarden.IsInCombat is asked too");
        Set("Travel", "UseWardenCombat", false);
        Ok(Cmd(p, "travel", "kingsreach").Contains("You set out"), "UseWardenCombat false: only the plugin's own watch"); Cmd(p, "travel", "cancel");
        Set("Travel", "UseWardenCombat", true); WardenFight.Clear();
        var m = Capture(p);
        m.Captured = true;
        Ok(Cmd(p, "travel", "kingsreach").Contains("A captive cannot travel."), "a captive cannot travel (PlayerCaptureManager.Captured)");
        m.Captured = false; m.HoldingCaptive = true;
        Ok(Cmd(p, "travel", "kingsreach").Contains("You cannot travel while you hold a captive."), "nor someone holding one (HoldingCaptive)");
        m.HoldingCaptive = false; m.CaptivePlayerID = 42;
        Ok(Cmd(p, "travel", "kingsreach").Contains("hold a captive"), "nor someone with a captive id");
        m.CaptivePlayerID = 0;
        Ransom.Add(C);
        Ok(Cmd(p, "travel", "kingsreach").Contains("A captive cannot travel."), "nor a player CrownAndConsequences holds for ransom");
        Set("Travel", "BlockCaptives", false);
        Ok(Cmd(p, "travel", "kingsreach").Contains("You set out"), "BlockCaptives false"); Cmd(p, "travel", "cancel");
        Set("Travel", "BlockCaptives", true); Ransom.Clear();
        Bearer = C.ToString();
        Ok(Cmd(p, "travel", "kingsreach").Contains("The Ironbreaker will not be carried along the roads. You must walk."), "the Ironbreaker's bearer walks (RealmLegendary.IsBearer)");
        Bearer = null;
        ContractOutlaws.Add(C.ToString());
        Ok(Cmd(p, "travel", "kingsreach").Contains("Outlaws may not use the realm's roads."), "a crown outlaw cannot travel (RealmContracts.IsOutlaw)");
        ContractOutlaws.Clear(); CourtOutlaws.Add(C.ToString());
        Ok(Cmd(p, "travel", "kingsreach").Contains("Outlaws may not use"), "nor a court outlaw (RealmLaws.IsCourtOutlaw)");
        Set("Travel", "BlockOutlaws", false);
        Ok(Cmd(p, "travel", "kingsreach").Contains("You set out"), "BlockOutlaws false"); Cmd(p, "travel", "cancel");
        Set("Travel", "BlockOutlaws", true); CourtOutlaws.Clear();
        // The Old Throne in a rebellion.
        AncientThrone.EntityPosition = new UnityEngine.Vector3(520, 10, 480);
        Rebellion = true;
        Ok(Cmd(p, "travel", "kingsreach").Contains("A rebellion is under way: no one travels to or from the Old Throne until it ends."), "no journey to the throne in a rebellion (the game's throne position)");
        Ok(Cmd(p, "travel", "old-mill").Contains("You set out"), "other roads stay open"); Cmd(p, "travel", "cancel");
        At(p, 540, 470);
        Ok(Cmd(p, "travel", "old-mill").Contains("A rebellion is under way"), "nor away from the throne room");
        Rebellion = false;
        Ok(Cmd(p, "travel", "old-mill").Contains("You set out"), "no rebellion: open"); Cmd(p, "travel", "cancel");
        Rebellion = true;
        At(ada, 2000, 2000); Cmd(ada, "travel", "admin", "throne");
        Ok(Cmd(p, "travel", "old-mill").Contains("You set out"), "an admin's throne point replaces the game's"); Cmd(p, "travel", "cancel");
        Cmd(ada, "travel", "admin", "throne", "clear");
        AncientThrone.EntityPosition = new UnityEngine.Vector3(0, 0, 0);
        Ok(Cmd(p, "travel", "old-mill").Contains("You set out"), "throne unknown: no rule can apply"); Cmd(p, "travel", "cancel");
        Inv(T, "OnThroneCaptured", new AncientThroneCaptureEvent { Player = foe, State = AncientThroneCaptureEvent.States.Capturing });
        Ok(!(bool)D("LearnedThrone"), "a capture still in progress teaches nothing");
        At(foe, 545, 475);
        Inv(T, "OnThroneCaptured", new AncientThroneCaptureEvent { Player = foe, State = AncientThroneCaptureEvent.States.Completed });
        Ok((bool)D("LearnedThrone") && Cmd(p, "travel", "old-mill").Contains("A rebellion is under way"), "a completed capture teaches where the throne is");
        Set("Travel", "BlockNearThroneInRebellion", false);
        Ok(Cmd(p, "travel", "old-mill").Contains("You set out"), "BlockNearThroneInRebellion false"); Cmd(p, "travel", "cancel");
        Set("Travel", "BlockNearThroneInRebellion", true);
        Rebellion = false;
        At(p, 0, 0);
        Exiles.Add(C.ToString());
        Ok(Cmd(p, "travel", "kingsreach").Contains("You are exiled from the towns and may not travel to the capital."), "an exile may not travel to the capital (RealmLaws.IsExiled)");
        Ok(Cmd(p, "travel", "old-mill").Contains("You set out"), "an exile may use other roads"); Cmd(p, "travel", "cancel");
        Exiles.Clear();
        RaidHours = true;
        Ok(Cmd(p, "travel", "greywater").Contains("Holdings are closed to travellers in the raid hours. March there."), "holdings close in the raid hours (RealmWarden.IsRaidHourNow)");
        Set("Travel", "HoldingsClosedInRaidHours", false);
        Ok(Cmd(p, "travel", "greywater").Contains("You set out"), "HoldingsClosedInRaidHours false"); Cmd(p, "travel", "cancel");
        RaidHours = false;
        At(p, 520, 520);
        Ok(Cmd(p, "travel", "kingsreach").Contains("Kingsreach is close enough to walk (30 m)."), "closer than MinDistance: walk");
        At(p, 0, 0);
        Cmd(ada, "travel", "admin", "enabled", "old-mill", "off");
        Ok(Cmd(p, "travel", "old-mill").Contains("No waystone called 'old-mill'"), "a closed waystone cannot be named");
        Cmd(ada, "travel", "admin", "enabled", "old-mill", "on");
    }

    static void SeatAccess()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Corin", 0, 0, "Corvane");
        foreach (var id in new[] { "kingsreach", "varrow-hall" }) Unlocked(p).Add(id);
        string r = Cmd(p, "travel", "varrow-hall");
        Ok(r.Contains("Varrow Hall is the seat of House [C58FC0]Varrow[FFFFFF], open only to its house and allies."), "a seat is closed to a stranger house", r);
        Ok(Cmd(p, "travel").Contains("Varrow Hall (seat of [C58FC0]Varrow[FFFFFF]) - 1.6 km east - [E8913A]closed to you[FFFFFF]"), "the list says so");
        HouseOf[C] = "Varrow";
        Ok(Cmd(p, "travel", "varrow-hall").Contains("You set out"), "open to its own house"); Cmd(p, "travel", "cancel");
        HouseOf[C] = "Corvane";
        Liege["Corvane"] = "Varrow";
        Ok(Cmd(p, "travel", "varrow-hall").Contains("You set out"), "open to a vassal of the house"); Cmd(p, "travel", "cancel");
        Liege.Clear(); Liege["Varrow"] = "Corvane";
        Ok(Cmd(p, "travel", "varrow-hall").Contains("You set out"), "open to the house's liege"); Cmd(p, "travel", "cancel");
        Liege.Clear(); Liege["Varrow"] = "Halloran"; Liege["Corvane"] = "Halloran";
        Ok(Cmd(p, "travel", "varrow-hall").Contains("You set out"), "open to a fellow vassal of the same liege"); Cmd(p, "travel", "cancel");
        Liege.Clear(); Treaties.Add(Pair("Corvane", "Varrow"));
        Ok(Cmd(p, "travel", "varrow-hall").Contains("You set out"), "open to a house with a treaty"); Cmd(p, "travel", "cancel");
        Set("Travel", "SeatAccess", "house");
        Ok(Cmd(p, "travel", "varrow-hall").Contains("open only to its own."), "SeatAccess house: allies are not enough");
        Set("Travel", "SeatAccess", "all");
        Treaties.Clear();
        Ok(Cmd(p, "travel", "varrow-hall").Contains("You set out"), "SeatAccess all: open to everyone"); Cmd(p, "travel", "cancel");
        Set("Travel", "SeatAccess", "allies");
        HouseOf.Remove(C);
        Ok(Cmd(p, "travel", "varrow-hall").Contains("open only to its house and allies"), "no house: closed");
        Cmd(ada, "travel", "admin", "house", "varrow-hall", "none");
        Ok(Cmd(p, "travel", "varrow-hall").Contains("That seat has no house named; it is closed."), "a seat with no house named is closed");
        Absent.Add("RealmHouses");
        Reload();
        Cmd(ada, "travel", "admin", "house", "varrow-hall", "Varrow");
        Ok(Cmd(p, "travel", "varrow-hall").Contains("open only to its house"), "RealmHouses not loaded: nobody has a house, seats are closed");
        Absent.Clear();
    }

    static void RecheckOnArrival()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell");
        foreach (var id in new[] { "kingsreach", "old-mill" }) Unlocked(p).Add(id);
        SetF(Rec(p), "FreeTripsUsed", 3);
        Purses[C.ToString()] = 30;
        Cmd(p, "travel", "kingsreach");
        ContractOutlaws.Add(C.ToString());
        Tick(10);
        Ok(Math.Abs(Pos(p).x) < 0.01 && Charges.Count == 0 && p.All().Contains("Outlaws may not use"), "outlawed while waiting: refused on arrival, not moved, not charged", p.All());
        ContractOutlaws.Clear();
        Clear();
        Cmd(p, "travel", "kingsreach");
        Purses[C.ToString()] = 5;                                    // paid it away to a friend while waiting
        Tick(10);
        Ok(Math.Abs(Pos(p).x) < 0.01 && Charges.Count == 0 && p.All().Contains("The toll is 17 marks; your purse holds 5."), "a purse emptied while waiting: refused on arrival", p.All());
        Purses[C.ToString()] = 30;
        Cmd(p, "travel", "kingsreach");
        AncientThrone.EntityPosition = new UnityEngine.Vector3(500, 10, 500);
        Rebellion = true;
        Tick(10);
        Ok(Math.Abs(Pos(p).x) < 0.01 && p.All().Contains("A rebellion is under way"), "a rebellion that starts while waiting closes the road");
        Rebellion = false;
        Cmd(p, "travel", "kingsreach");
        Cmd(ada, "travel", "admin", "enabled", "kingsreach", "off");
        Tick(10);
        Ok(Math.Abs(Pos(p).x) < 0.01 && p.All().Contains("That waystone is closed."), "a waystone closed while waiting");
        Cmd(ada, "travel", "admin", "enabled", "kingsreach", "on");
        At(ada, 600, 600);
        Cmd(ada, "travel", "admin", "set", "kingsreach", "capital");
        Clear();
        Cmd(p, "travel", "kingsreach"); Tick(10);
        Ok(Math.Abs(Pos(p).x - 600) < 0.01, "a waystone moved while waiting: the traveller goes to where it is now");
    }

    static void Shield()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell"); var foe = Mk(Dd, "Foe", 505, 505);
        Unlocked(p).Add("kingsreach");
        Cmd(p, "travel", "kingsreach"); Tick(10);
        Ok(Hit(foe, p, 20f) == 0f, "a traveller who just arrived takes no player damage (arrival shield)");
        Clock = Clock.AddSeconds(6);
        Ok(Hit(foe, p, 20f) == 20f, "the shield ends after ArrivalShieldSeconds");
        Clock = Clock.AddMinutes(11);
        At(p, 0, 0);
        Cmd(p, "travel", "kingsreach"); Tick(10);
        Hit(p, foe, 5f);
        Ok(Hit(foe, p, 20f) == 20f, "striking first ends your own shield");
        Clock = Clock.AddMinutes(11);
        At(p, 0, 0);
        Set("Travel", "ArrivalShieldSeconds", 0);
        Clear();
        Cmd(p, "travel", "kingsreach"); Tick(10);
        Ok(Hit(foe, p, 20f) == 20f && !p.All().Contains("watches over you"), "ArrivalShieldSeconds 0: no shield");
    }

    static void TeleportFaults()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell");
        Unlocked(p).Add("kingsreach");
        SetF(Rec(p), "FreeTripsUsed", 3);
        Purses[C.ToString()] = 100;
        p.Entity.TeleportThrows = true;
        Cmd(p, "travel", "kingsreach"); Tick(10);
        Ok(p.All().Contains("The road failed you: you have not moved and paid nothing.") && Charges.Count == 0 && Purses[C.ToString()] == 100
            && T.Logged.Any(l => l.Contains("Teleport failed")), "a teleport the game refuses: not charged, logged", p.All());
        Ok(Cmd(p, "travel", "kingsreach").Contains("You set out"), "and no cooldown was set");
        Cmd(p, "travel", "cancel");
        p.Entity.TeleportThrows = false;
        p.Entity.TeleportIgnored = true;
        Cmd(p, "travel", "kingsreach"); Tick(10);
        T.timer.RunPending();
        Ok((int)D("ArrivalsUnconfirmed") == 1 && T.Logged.Any(l => l.Contains("Arrival not confirmed")), "the server's position not following the teleport is counted for the admins (UNVERIFIED path)");
        p.Entity.TeleportIgnored = false;
    }

    static void Home()
    {
        Reset();
        NewTravel();
        var dana = Mk(Dd, "Dana", 300, 300, "Varrow");
        SocialAPI.GroupOf[Dd] = 77;
        var zone = Crest(100, 100, 30, 77, 999);
        Purses[Dd.ToString()] = 100;
        Ok(Cmd(dana, "home").Contains("You have no home."), "no home yet");
        Ok(Cmd(dana, "home", "set").Contains("A home must be inside your own crest zone."), "a home outside any crest zone is refused");
        At(dana, 105, 100);
        string r = Cmd(dana, "home", "set");
        Ok(r.Contains("[8FC97A]Roads[FFFFFF]: Your home is here now. [F4C96D]/home[FFFFFF] brings you back (12 s, toll 5)."), "a home inside the house's crest zone", r);
        Ok(Cmd(dana, "home", "set").Contains("You moved your home lately. Try again in 10 min."), "moving the home has a cooldown");
        Clock = Clock.AddMinutes(11);
        SocialAPI.GroupOf[Dd] = 78;
        Ok(Cmd(dana, "home", "set").Contains("A home must be inside your own crest zone."), "another group's crest zone is refused");
        SocialAPI.GroupOf.Remove(Dd);
        zone.Owner = Dd;
        Ok(Cmd(dana, "home", "set").Contains("Your home is here now."), "a crest the player raised themselves counts (CrestScheme.GetCrestPlayer)");
        zone.Owner = 999; SocialAPI.GroupOf[Dd] = 77;
        At(dana, 900, 900);
        Clear();
        r = Cmd(dana, "home");
        Ok(r.Contains("You set out for your home. Stand still for 12 s") && r.Contains("Toll on arrival: 5 marks"), "/home sets out (12 s, toll 5)", r);
        Tick(12);
        Ok(Math.Abs(Pos(dana).x - 105) < 0.01 && Math.Abs(Pos(dana).z - 100) < 0.01 && Charges.Count == 1 && Charges[0].EndsWith("|5|RealmTravel|road toll: home"), "home: moved and charged", string.Join(",", Charges));
        At(dana, 900, 900);
        Ok(Cmd(dana, "home").Contains("Your next journey is in 15 min."), "the home cooldown");
        Clock = Clock.AddMinutes(16);
        zone.Siege = true;
        Ok(Cmd(dana, "home").Contains("Your hold is under siege. You must fight your way home on foot."), "no journey home into a siege");
        zone.Siege = false;
        SocialAPI.GroupOf[Dd] = 78;
        Ok(Cmd(dana, "home").Contains("Your home is no longer inside your own crest zone."), "a home that is no longer yours (left the house)");
        SocialAPI.GroupOf[Dd] = 77;
        Crests.Throws = true;
        Ok(Cmd(dana, "home").Contains("The land cannot be read right now."), "the crest map cannot be read: refused, never guessed");
        Crests.Throws = false;
        string info = Cmd(dana, "home", "info");
        Ok(info.Contains("Your home is 1.1 km south-west from here. It is inside your crest zone.") && info.Contains("Your next journey home: now."), "/home info", info);
        Ok(Cmd(dana, "travel", "home").Contains("You set out for your home."), "/travel home is /home"); Cmd(dana, "travel", "cancel");
        Crests.Zones.Clear();
        Ok(Cmd(dana, "home", "info").Contains("It is no longer inside your crest zone."), "info tells when the crest is gone");
        Ok(Cmd(dana, "home", "clear").Contains("Your home is forgotten.") && Cmd(dana, "home").Contains("You have no home."), "/home clear");
        Reload(c => Section(c, "Home", "RequireOwnCrest", false));
        Clock = Clock.AddMinutes(11);
        Ok(Cmd(dana, "home", "set").Contains("Your home is here now."), "RequireOwnCrest false: a home anywhere");
        Ok(Cmd(dana, "home").Contains("Your home is close enough to walk"), "standing at home: walk");
        Reload(c => Section(c, "Home", "Enabled", false));
        Ok(Cmd(dana, "home").Contains("That part of the roads is closed on this server."), "Home.Enabled false");
    }

    static void Roads()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell");
        string r = Cmd(p, "road", "kingsreach");
        Ok(r.Contains("The road to Kingsreach: 710 m to the north-east. The way is called every 15 s."), "/road works for a waystone not yet found", r);
        Clear();
        Tick(14);
        Ok(!p.All().Contains("Kingsreach:"), "nothing before UpdateSeconds");
        Tick(1);
        Ok(p.All().Contains("[D6A043]Roads[FFFFFF]: Kingsreach: 710 m to the north-east."), "the way is called every 15 s", p.All());
        At(p, 250, 250);
        Ok(Cmd(p, "road").Contains("Kingsreach: 350 m to the north-east."), "/road alone shows where the road stands now");
        At(p, 495, 497); Tick(15);
        Ok(p.All().Contains("You have reached Kingsreach.") && Unlocked(p).Contains("kingsreach"), "arriving ends the road (and the waystone is learned)", p.All());
        Clear();
        Tick(15);
        Ok(!p.All().Contains("Kingsreach:"), "the road is over");
        At(p, 0, 0);
        Cmd(p, "road", "old-mill");
        Clock = Clock.AddMinutes(31); Tick(1);
        Ok(p.All().Contains("The road to Old Mill fades. [F4C96D]/road[FFFFFF] old-mill to follow it again."), "a road fades after MaxMinutes", p.All());
        Cmd(p, "road", "greywater");
        Ok(Cmd(p, "road", "stop").Contains("You leave the road.") && Cmd(p, "road", "stop").Contains("You are not following a road."), "/road stop");
        Ok(Cmd(p, "road", "home").Contains("You have no home."), "/road home needs a home");
        Ok(Cmd(p, "road", "atlantis").Contains("No waystone called 'atlantis'"), "an unknown road");
        Ok(Cmd(p, "road", "help").Contains("[F4C96D]/road[FFFFFF] <waystone> or [F4C96D]/road home[FFFFFF]"), "/road help");
        Raise(ada, "north-tower", "landmark", 0, 1000, "North Tower");
        Raise(ada, "east-tower", "landmark", 1000, 0, "East Tower");
        Raise(ada, "south-tower", "landmark", 0, -1000, "South Tower");
        Raise(ada, "west-tower", "landmark", -1000, 0, "West Tower");
        Ok(Cmd(p, "road", "north-tower").Contains("to the north.") && Cmd(p, "road", "east-tower").Contains("to the east.")
            && Cmd(p, "road", "south-tower").Contains("to the south.") && Cmd(p, "road", "west-tower").Contains("to the west."), "compass points: +z north, +x east");
        Set("Roads", "NorthIsPositiveZ", false);
        Ok(Cmd(p, "road", "north-tower").Contains("1.0 km to the south."), "NorthIsPositiveZ false flips north and south");
        Set("Roads", "NorthIsPositiveZ", true);
        At(p, 2, 1000);
        Ok(Cmd(p, "road", "north-tower").Contains("You are already at North Tower."), "already there");
        At(p, 0, 0);
        Unlocked(p).Add("old-mill");
        Set("Travel", "Mode", "road");
        r = Cmd(p, "travel", "old-mill");
        Ok(r.Contains("The waystones sleep for now; follow the road instead.") && r.Contains("The road to Old Mill") && !Travelling(p), "Mode road: /travel shows the way instead of moving", r);
        Set("Roads", "Enabled", false);
        Ok(Cmd(p, "road", "old-mill").Contains("That part of the roads is closed"), "Roads.Enabled false");
    }

    static void StarterKit()
    {
        Reset();
        NewTravel();
        Protected.Add(C);
        var newt = Mk(C, "Newt");
        string list = Cmd(newt, "kit");
        Ok(list.Contains("Your kits:") && list.Contains("  Traveller's Pack (starter) - ready: [F4C96D]/kit[FFFFFF] starter")
            && list.Contains("  House Provisions (house) - for sworn members of a house") && list.Contains("  Season's Bounty (season) - no season is running")
            && list.Contains("    150 Wood, 100 Stone, 4 Bread, 6 Apple"), "/kit lists each kit, its contents and why it is or is not ready", list);
        string r = Cmd(newt, "kit", "starter");
        Ok(r.Contains("[8FC97A]Roads[FFFFFF]: You take the Traveller's Pack: 150 Wood, 100 Stone, 4 Bread, 6 Apple."), "a newcomer takes the starter kit", r);
        Ok(Count(newt, "Wood") == 150 && Count(newt, "Stone") == 100 && Count(newt, "Bread") == 4 && Count(newt, "Apple") == 6 && OwedUnits(newt) == 0, "the items are in the packs, measured");
        Ok(ItemSources.Contains(C + "|30"), "RealmSentinel is told the items are explained (SentinelItemSource)");
        Ok(Cmd(newt, "kit", "starter").Contains("The Traveller's Pack is not for you now: taken.") && Count(newt, "Wood") == 150, "once per Steam id");
        var vet = Mk(Dd, "Vet");
        Ok(Cmd(vet, "kit", "starter").Contains("not for you now: for newcomers only."), "a player RealmWarden does not protect as new is refused");
        // Seen once as a newcomer is enough, for StarterClaimDays.
        Protected.Add(E);
        var late = Mk(E, "Late");
        Tick(61);
        Protected.Remove(E);
        Ok(Cmd(late, "kit").Contains("Traveller's Pack (starter) - ready"), "a newcomer whose protection ended may still take it");
        Clock = Clock.AddDays(8);
        Ok(Cmd(late, "kit", "starter").Contains("a newcomer's kit, and your first days are past"), "after StarterClaimDays it is gone");
        // The realm-wide daily cap.
        Reload(c => Section(c, "Kits", "StarterMaxPerDay", 2));
        foreach (ulong id in new[] { G, H, J }) Protected.Add(id);
        var g = Mk(G, "Gil"); var h = Mk(H, "Hal"); var j = Mk(J, "Jo");
        Cmd(g, "kit", "starter");
        Cmd(h, "kit", "starter");
        Ok(Cmd(j, "kit", "starter").Contains("many newcomers took one today; try again later"), "StarterMaxPerDay caps the realm's starter kits in 24 h");
        Clock = Clock.AddHours(25);
        Ok(Cmd(j, "kit", "starter").Contains("You take the Traveller's Pack"), "the cap rolls over");
        // Without RealmWarden.
        Absent.Add("RealmWarden");
        Reload();
        var k = Mk(K, "Kit");
        Ok(Cmd(k, "kit", "starter").Contains("for newcomers only"), "RealmWarden not loaded and StarterFallbackHours 0: no newcomer can be told apart");
        Reload(c => Section(c, "Kits", "StarterFallbackHours", 24));
        Ok(Cmd(k, "kit", "starter").Contains("You take the Traveller's Pack"), "StarterFallbackHours: first seen within the window counts as new");
        Absent.Clear();
        // The newcomer's hint, once, after RealmHerald's welcome.
        Reset();
        NewTravel();
        Protected.Add(C);
        var n = Mk(C, "Nell");
        Ok(T.timer.Pending.Any(kv => Math.Abs(kv.Key - 45f) < 0.01) && n.Messages.Count == 0, "the hint waits NewcomerHintDelaySeconds");
        T.timer.RunPending();
        Ok(n.All().Contains("[E8913A]Roads[FFFFFF]: A traveller's pack waits for every newcomer: [F4C96D]/kit starter[FFFFFF]."), "the newcomer is told about the pack", n.All());
        Offline(n); n.Messages.Clear(); Online(n); T.timer.RunPending();
        Ok(!n.All().Contains("traveller's pack waits"), "only once");
        var v = Mk(Dd, "Vera");
        T.timer.RunPending();
        Ok(!v.All().Contains("traveller's pack waits"), "a veteran is not told");
    }

    static void HouseKit()
    {
        Reset();
        NewTravel();
        var e = Mk(E, "Eric", 0, 0, "Varrow");
        HouseOf[G] = "Varrow";                                       // a second member, offline
        Ok(Cmd(e, "kit", "house").Contains("not for you now: after 12 h in your house (12 h left)."), "a new member waits HouseKitMinMemberHours");
        Clock = Clock.AddHours(12); Tick(61);                       // 2026-10-06 00:01 UTC
        string r = Cmd(e, "kit", "house");
        Ok(r.Contains("You take the House Provisions: 100 Wood, 100 Stone, 3 Bread."), "a member of 12 h takes the house kit", r);
        Ok(Cmd(e, "kit", "house").Contains("taken today; again in 24 h"), "once per realm day", Cmd(e, "kit"));
        Clock = new DateTime(2026, 10, 6, 23, 59, 0, DateTimeKind.Utc);
        Ok(Cmd(e, "kit", "house").Contains("taken today; again in 1 min"), "still the same day at 23:59");
        Clock = Clock.AddMinutes(2);
        Ok(Cmd(e, "kit", "house").Contains("You take the House Provisions"), "a new day at midnight (realm time)");
        // Realm time UTC+3: the realm's day turns at 21:00 UTC.
        UtcOffset = 3;
        Clock = new DateTime(2026, 10, 7, 22, 0, 0, DateTimeKind.Utc);  // 01:00 on 10-08 in the realm
        Ok(Cmd(e, "kit", "house").Contains("You take the House Provisions"), "with UtcOffsetHours 3, 22:00 UTC is already the next realm day");
        Clock = new DateTime(2026, 10, 8, 20, 30, 0, DateTimeKind.Utc);  // 23:30 on 10-08 in the realm
        Ok(Cmd(e, "kit", "house").Contains("taken today; again in 30 min"), "the day follows CrownAndConsequences' UtcOffsetHours");
        UtcOffset = 0;
        // House hopping: a new house means a new wait, and the kit stays taken today.
        HouseOf[E] = "Corvane"; HouseOf[H] = "Corvane";
        Clock = Clock.AddDays(1);
        Ok(Cmd(e, "kit", "house").Contains("after 12 h in your house"), "changing house restarts the wait (no hopping for provisions)");
        HouseOf.Remove(H);
        Clock = Clock.AddHours(13);
        Ok(Cmd(e, "kit", "house").Contains("for houses of 2 or more"), "a house of one gets no provisions (HouseKitMinMembers)");
        HouseOf.Remove(E);
        Ok(Cmd(e, "kit", "house").Contains("for sworn members of a house"), "no house, no provisions");
    }

    static void SeasonKit()
    {
        Reset();
        NewTravel();
        var s = Mk(G, "Sigrid");
        Ok(Cmd(s, "kit", "season").Contains("no season is running"), "no season, no bounty");
        Season = 3;
        Tick(1);
        Ok(Cmd(s, "kit", "season").Contains("after 30 min of play this season (0 so far)"), "a season needs play time first");
        Tick(60 * 30);
        string r = Cmd(s, "kit", "season");
        Ok(r.Contains("You take the Season's Bounty: 300 Wood, 300 Stone, 40 Iron Ore."), "after 30 min of play: the bounty (IronOre resolves to the item Iron Ore)", r);
        Ok(Count(s, "Iron Ore") == 40, "ResourceType names resolve through GetBlueprintForResource");
        Ok(Cmd(s, "kit", "season").Contains("taken this season"), "once per season");
        Season = 4;
        Ok(Cmd(s, "kit", "season").Contains("(0 so far)"), "a new season: play time starts again");
        Tick(60 * 30);
        Ok(Cmd(s, "kit", "season").Contains("You take the Season's Bounty"), "and the bounty comes again");
        Reload();
        Ok(Cmd(s, "kit", "season").Contains("taken this season"), "the claim survives a reload");
    }

    static void KitDelivery()
    {
        Reset();
        NewTravel();
        Protected.Add(C);
        var n = Mk(C, "Nora");
        n.Inventory.Contents.Capacity = 100;
        string r = Cmd(n, "kit", "starter");
        Ok(r.Contains("You take the Traveller's Pack: 100 Wood.") && r.Contains("  Your packs are full: the rest waits. Make room, then [F4C96D]/kit collect[FFFFFF]."), "full packs: what fits now, the rest waits", r);
        Ok(Count(n, "Wood") == 100 && OwedUnits(n) == 160, "measured: 100 given, 160 owed");
        Ok(Cmd(n, "kit", "collect").Contains("Your packs are still full."), "still full: nothing more");
        Ok(OwedUnits(n) == 160 && Count(n, "Wood") == 100, "a failed collect creates nothing and loses nothing");
        n.Inventory.Contents.Capacity = 100000;
        r = Cmd(n, "kit", "collect");
        Ok(r.Contains("You collect: 50 Wood, 100 Stone, 4 Bread, 6 Apple.") && OwedUnits(n) == 0 && Count(n, "Wood") == 150, "/kit collect pays the rest", r);
        Ok(Cmd(n, "kit", "collect").Contains("Nothing is waiting for you."), "nothing left");
        // Owed survives a reload and is paid on the next join.
        Protected.Add(Dd);
        var o = Mk(Dd, "Odo");
        o.Inventory.Contents.Capacity = 10;
        Cmd(o, "kit", "starter");
        Offline(o);
        Reload();
        o.Inventory.Contents.Capacity = 100000; o.Messages.Clear();
        Online(o);
        T.timer.RunPending();
        Ok(OwedUnits(o) == 0 && Count(o, "Wood") == 150 && o.All().Contains("You collect:"), "what waits is paid on the next join", o.All());
        Set("Kits", "MaxOwedLines", 5);
        Protected.Add(E);
        var q = Mk(E, "Quin", 0, 0, "Varrow");
        HouseOf[H] = "Varrow";
        q.Inventory.Contents.Capacity = 0;
        Cmd(q, "kit", "starter");                                    // 4 lines wait
        Season = 1;
        SetF(Rec(q), "PlaySeason", 1); SetF(Rec(q), "PlaySeasonMinutes", 100);
        Ok(Cmd(q, "kit", "season").Contains("You take the Season's Bounty") && OwedUnits(q) == 260 + 640, "under MaxOwedLines another kit may wait too");
        SetF(Rec(q), "HouseSince", Clock.AddHours(-13));
        Ok(Cmd(q, "kit", "house").Contains("Too much is already waiting for you."), "MaxOwedLines: collect first");
        Hit(n, q);
        q.Inventory.Contents.Capacity = 100000;
        Ok(Cmd(q, "kit", "collect").Contains("You collect:"), "collect works even in a fight (it only empties the ledger)");
        Ok(Cmd(q, "kit", "season").Contains("Not in the middle of a fight."), "no kit in a fight");
        q.Entity = null;
        Clock = Clock.AddMinutes(1);
        Ok(Cmd(q, "kit", "season").Contains("You cannot take a kit in your present state."), "no body (dead): no kit");
    }

    static void KitItems()
    {
        Reset();
        NewTravel(c =>
        {
            var kits = (IList)F(F(c, "Kits"), "List");
            var t = kits[0].GetType();
            var itemT = typeof(RealmTravel).GetNestedType("KitItem", BF);
            object Item(string name, int n) { var it = Activator.CreateInstance(itemT); SetF(it, "Item", name); SetF(it, "Amount", n); return it; }
            var odd = Activator.CreateInstance(t); SetF(odd, "Id", "odd"); SetF(odd, "Name", "Odd Pack"); SetF(odd, "Kind", "starter");
            var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemT)); items.Add(Item("Dragon Scale", 1)); items.Add(Item("Torch", 2)); items.Add(Item("Bandage", 50000));
            SetF(odd, "Items", items); kits.Add(odd);
            var ghost = Activator.CreateInstance(t); SetF(ghost, "Id", "ghost"); SetF(ghost, "Name", "Ghost"); SetF(ghost, "Kind", "season");
            var gi = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemT)); gi.Add(Item("Unobtainium", 1)); SetF(ghost, "Items", gi); kits.Add(ghost);
            var bad = Activator.CreateInstance(t); SetF(bad, "Id", "starter"); SetF(bad, "Kind", "house"); kits.Add(bad);      // duplicate id
            var bad2 = Activator.CreateInstance(t); SetF(bad2, "Id", "weird"); SetF(bad2, "Kind", "monthly"); kits.Add(bad2); // unknown kind
        });
        Ok(T.Logged.Any(l => l.Contains("Kit 'odd': the item 'Dragon Scale' is not known to this server and is skipped.")), "an unknown kit item is reported at start-up", string.Join("\n", T.Logged));
        Ok(T.Logged.Count(l => l.Contains("ignored: it needs a unique id")) == 2, "a duplicate id and an unknown kind are ignored with a warning");
        var kitIds = ((IList)F(Cfg("Kits"), "List")).Cast<object>().Select(k => (string)F(k, "Id")).ToList();
        Ok(kitIds.SequenceEqual(new[] { "starter", "house", "season", "odd", "ghost" }), "the valid kits remain", string.Join(",", kitIds));
        var odd2 = ((IList)F(Cfg("Kits"), "List"))[3];
        Ok((int)F(((IList)F(odd2, "Items"))[2], "Amount") == 10000, "an amount over 10000 is clamped");
        Protected.Add(C);
        var p = Mk(C, "Pip");
        string r = Cmd(p, "kit", "odd");
        Ok(r.Contains("You take the Odd Pack: 2 Torch, 10000 Bandage.") && Count(p, "Torch") == 2, "only the items the server knows are given", r);
        Season = 1; SetF(Rec(p), "PlaySeason", 1); SetF(Rec(p), "PlaySeasonMinutes", 100);
        Ok(Cmd(p, "kit").Contains("Ghost (ghost) - unavailable (no item this server knows)"), "a kit with no known item is unavailable");
        Ok(Cmd(p, "kit", "ghost").Contains("not for you now: unavailable"), "and cannot be claimed (nothing is recorded)");
        Ok(!((IDictionary)F(Rec(p), "KitClaims")).Contains("ghost"), "no claim is written for it");
        var ada = Mk(A, "Ada");
        Ok(Cmd(p, "kit", "admin", "items", "wood").Contains("You may not do that."), "/kit admin is staff only");
        Admin(ada);
        r = Cmd(ada, "kit", "admin", "items", "stone");
        Ok(r.Contains("Item names holding 'stone' (2):") && r.Contains("  Stone") && r.Contains("  Stone Hatchet"), "/kit admin items searches the game's item list", r);
        Ok(Cmd(ada, "kit", "admin", "items", "x").Contains("Usage"), "a one-letter search is refused");
        Ok(Cmd(ada, "kit", "admin", "items", "dragon").Contains("No item name holds 'dragon'."), "nothing found");
        r = Cmd(ada, "kit", "admin", "check");
        Ok(r.Contains("Kits checked against the game's item list:") && r.Contains("  Odd Pack (odd): 2 Torch, 10000 Bandage") && r.Contains("    unknown item 'Dragon Scale' (skipped)")
            && r.Contains("  Ghost (ghost): none"), "/kit admin check", r);
        r = Cmd(ada, "kit", "admin", "reset", "Pip", "odd");
        Ok(r.Contains("Pip's kit claims cleared: odd.") && Cmd(p, "kit", "odd").Contains("You take the Odd Pack"), "/kit admin reset lets a player take a kit again (a testing aid)", r);
        Ok(Cmd(ada, "kit", "admin", "reset", "Nobody", "all").Contains("No such person"), "reset an unknown player");
        Ok(Cmd(ada, "kit", "admin", "reset", "Pip", "nokit").Contains("No kit called 'nokit'"), "reset an unknown kit");
        // The item list is not loaded yet: nothing is cached as unknown.
        var saved = InvBlueprints.Instance;
        InvBlueprints.Instance = null;
        Reload();
        Ok(Cmd(ada, "kit", "admin", "items", "wood").Contains("The game's item list is not loaded yet."), "item list not ready: said so");
        Ok(Cmd(ada, "kit", "admin", "check").Contains("not loaded yet"), "check: not ready");
        InvBlueprints.Instance = saved;
        Protected.Add(Dd);
        var q = Mk(Dd, "Quill");
        Ok(Cmd(q, "kit", "starter").Contains("You take the Traveller's Pack"), "once the list is up, the items resolve (nothing was cached as unknown)");
        Ok(Cmd(q, "kit", "dragon").Contains("No kit called 'dragon'."), "an unknown kit");
        Reload(c => Section(c, "Kits", "Enabled", false));
        Ok(Cmd(q, "kit").Contains("That part of the roads is closed"), "Kits.Enabled false");
    }

    static void Wayfarer()
    {
        Player ada;
        World4(out ada);
        Raise(ada, "hidden-glen", "landmark", 4000, 4000, "Hidden Glen");
        Cmd(ada, "travel", "admin", "hidden", "hidden-glen", "on");
        Season = 2;
        var w = Mk(G, "Wyn", 0, 0, "Merrin");
        Visit(w, 500, 500); Visit(w, 1500, 500); Visit(w, 500, 1500);
        Ok(!w.All().Contains("every waystone"), "three of four: not yet");
        Visit(w, -800, 200);
        Ok(w.All().Contains("[8FC97A]Roads[FFFFFF]: You have reached every waystone of the realm."), "the fourth: every (unhidden) waystone reached", w.All());
        Ok(B().Contains("[D6A043]Herald[FFFFFF]: Wyn has reached every waystone of the realm."), "the realm hears of it", B());
        Ok(Deeds.Count == 1 && Deeds[0] == G + "|Wyn|wayfarer|reached every waystone|wayfarer", "RealmRenown.AddDeed(wayfarer) with a once-only dedupe key", string.Join("\n", Deeds));
        Ok(Awards.Count == 1 && Awards[0] == "Merrin|2|Pathfinder: Wyn" && w.All().Contains("  House [6FBF85]Merrin[FFFFFF] earns 2 season point(s) for it."), "the house earns season points (RealmSeasons.AwardHouse)", string.Join("\n", Awards));
        Clear();
        Raise(ada, "new-gate", "landmark", 6000, 0, "New Gate");
        Visit(w, 6000, 0);
        Ok(Deeds.Count == 0 && Awards.Count == 0 && Server.Broadcasts.Count == 0 && !w.All().Contains("You have reached every waystone"), "completing again in the same season: no second deed, herald or points", w.All());
        Season = 3;
        Raise(ada, "third-gate", "landmark", 7000, 0, "Third Gate");
        Visit(w, 7000, 0);
        Ok(Awards.Count == 1 && Deeds.Count == 0 && Server.Broadcasts.Count == 0, "a new season: the house may earn again; the deed and herald stay once");
        Clear();
        string[] all = { "kingsreach", "varrow-hall", "greywater", "old-mill", "new-gate", "third-gate" };
        var names = new[] { "Mae", "Nim", "Ola", "Pim" };
        ulong id = 76561190000000100;
        foreach (string n in names)
        {
            var q = Mk(id++, n, 0, 0, "Merrin");
            foreach (var x in all.Take(5)) Unlocked(q).Add(x);
            Visit(q, 7000, 0);
        }
        Ok(Awards.Count == 2, "at most PathfinderAwardsPerHousePerSeason (3) awards per house per season: alts cannot farm points", string.Join("\n", Awards));
        Ok(Server.Broadcasts.Count == 3, "the herald speaks at most WayfarerHeraldsPerHour (4) times an hour", B());
        Reset(); NewTravel();
        var admin2 = Mk(A, "Ada"); Admin(admin2);
        Raise(admin2, "one", "landmark", 100, 100); Raise(admin2, "two", "landmark", 300, 300);
        var x2 = Mk(B, "Xan");
        Visit(x2, 100, 100); Visit(x2, 300, 300);
        Ok(!x2.All().Contains("every waystone"), "fewer than WayfarerMinWaystones (4): no wayfarer");
    }

    static void DataSafety()
    {
        Reset();
        string file = Path.Combine(Dir, "RealmTravel.json");
        File.WriteAllText(file, "{ \"Format\": 1, \"Waystones\": { \"kingsreach\": { \"X\": 5");
        string before = File.ReadAllText(file);
        NewTravel();
        var p = Mk(C, "Pell");
        Ok(T.Logged.Any(l => l.StartsWith("ERROR Could not read oxide/data/RealmTravel.json")), "a truncated data file is reported", string.Join("\n", T.Logged));
        Ok(Cmd(p, "travel").Contains("The roads are closed: oxide/data/RealmTravel.json could not be read.") && Cmd(p, "kit").Contains("could not be read")
            && Cmd(p, "home").Contains("could not be read") && Cmd(p, "road").Contains("could not be read"), "every command says the roads are paused");
        Tick(5); Inv(T, "OnServerSave"); Inv(T, "Unload");
        Ok(File.ReadAllText(file) == before, "the damaged file is never overwritten");
        File.WriteAllText(file, "");
        NewTravel();
        Ok(T.Logged.Any(l => l.Contains("is empty or truncated")) && File.ReadAllText(file) == "", "an empty file: paused, not overwritten");
        Inv(T, "Unload");
        File.WriteAllText(file, "{ \"Format\": 99 }");
        NewTravel();
        Ok(T.Logged.Any(l => l.Contains("written by a newer RealmTravel")) && File.ReadAllText(file) == "{ \"Format\": 99 }", "a newer format: paused, not overwritten");
        // A round trip: what players earned survives a reload; a journey in progress does not (and costs nothing).
        Reset();
        Player ada;
        World4(out ada);
        Protected.Add(Dd);
        var d = Mk(Dd, "Dana", 105, 100, "Varrow");
        SocialAPI.GroupOf[Dd] = 77; Crest(100, 100, 30, 77, 999);
        Cmd(d, "home", "set");
        Visit(d, 500, 500);
        d.Inventory.Contents.Capacity = 10;
        Cmd(d, "kit", "starter");
        Cmd(d, "travel", "kingsreach");
        Reload();
        Ok(Unlocked(d).Contains("kingsreach") && (bool)F(Rec(d), "HasHome") && ((IDictionary)F(Rec(d), "KitClaims")).Contains("starter") && OwedUnits(d) == 250,
            "found waystones, the home, kit claims and owed items survive a reload");
        Tick(15);
        Ok(!Travelling(d) && Math.Abs(Pos(d).x - 500) > 1 && Charges.Count == 0, "a journey in progress is dropped by a reload: no move, no charge");
        var players = (IDictionary)D("Players");
        Ok(players.Count >= 2, "players on record");
    }

    static void Config()
    {
        Reset();
        NewTravel(c =>
        {
            Section(c, "Travel", "BaseToll", -5L); Section(c, "Travel", "Mode", "fly"); Section(c, "Travel", "SeatAccess", "everyone");
            Section(c, "Travel", "ChannelSeconds", 999f); Section(c, "Travel", "SwornTollPercent", 300); Section(c, "Discovery", "DefaultRadius", 0.1f);
            Section(c, "General", "TickSeconds", 0.01f); Section(c, "Kits", "HouseKitMinMembers", 0);
        });
        Ok((long)F(Cfg("Travel"), "BaseToll") == 0 && (string)F(Cfg("Travel"), "Mode") == "teleport" && (string)F(Cfg("Travel"), "SeatAccess") == "allies"
            && Math.Abs((float)F(Cfg("Travel"), "ChannelSeconds") - 120f) < 0.01 && (int)F(Cfg("Travel"), "SwornTollPercent") == 100
            && Math.Abs((float)F(Cfg("Discovery"), "DefaultRadius") - 2f) < 0.01 && Math.Abs((float)F(Cfg("General"), "TickSeconds") - 0.5f) < 0.01
            && (int)F(Cfg("Kits"), "HouseKitMinMembers") == 1, "config values are clamped to safe ranges");
        T.Config.Json = "{ not json";
        Inv(T, "Init");
        Ok(T.Logged.Any(l => l.Contains("oxide/config/RealmTravel.json could not be read")) && (string)F(Cfg("Travel"), "Mode") == "teleport", "a broken config file: defaults for the run");
        Reset();
        NewTravel(c => Section(c, "General", "Enabled", false));
        var ada = Mk(A, "Ada"); Admin(ada);
        var p = Mk(C, "Pell");
        Ok(Cmd(p, "travel").Contains("The roads of the realm are closed for now.") && Cmd(p, "kit").Contains("closed for now") && Cmd(p, "home").Contains("closed for now")
            && Cmd(p, "road").Contains("closed for now"), "General.Enabled false: every player command is closed");
        Raise(ada, "kingsreach", "capital", 500, 500, "Kingsreach");
        Ok(((IDictionary)D("Waystones")).Contains("kingsreach"), "admins can still set the roads up while closed");
        At(p, 500, 500); Tick(3);
        Ok(Unlocked(p).Count == 0, "and nothing ticks");
        Reset();
        NewTravel(c => Section(c, "Travel", "Enabled", false));
        ada = Mk(A, "Ada"); Admin(ada);
        Ok(Cmd(ada, "travel").Contains("That part of the roads is closed on this server.") && Cmd(ada, "travel", "admin", "status").Contains("Roads:"), "Travel.Enabled false: /travel closed, /travel admin still answers");
        Reset();
        NewTravel(c => Section(c, "General", "AdminsExempt", true));
        ada = Mk(A, "Ada"); Admin(ada);
        Raise(ada, "kingsreach", "capital", 500, 500, "Kingsreach");
        Raise(ada, "old-mill", "landmark", -800, 200, "Old Mill");
        Unlocked(ada).Add("kingsreach"); Unlocked(ada).Add("old-mill");
        string r = Cmd(ada, "travel", "kingsreach");
        Ok(Math.Abs(Pos(ada).x - 500) < 0.01 && r.Contains("This journey is free: staff."), "AdminsExempt: no channel time, no toll", r);
        Ok(Cmd(ada, "travel", "old-mill").Contains("You arrive at Old Mill."), "and no cooldown");
    }

    static void Api()
    {
        Player ada;
        World4(out ada);
        var p = Mk(C, "Pell");
        Visit(p, 500, 500);
        Ok((int)Inv(T, "GetDiscoveredCount", C.ToString()) == 1 && (int)Inv(T, "GetDiscoveredCount", "123") == 0, "GetDiscoveredCount");
        Ok((bool)Inv(T, "HasDiscovered", C.ToString(), "kingsreach") && !(bool)Inv(T, "HasDiscovered", C.ToString(), "old-mill") && !(bool)Inv(T, "HasDiscovered", C.ToString(), null), "HasDiscovered");
        Cmd(p, "travel", "kingsreach");
        Ok(Travelling(p) && !(bool)Inv(T, "IsTravelling", "x"), "IsTravelling");
        Ok((bool)Inv(T, "CancelJourney", C.ToString()) && !Travelling(p) && !(bool)Inv(T, "CancelJourney", C.ToString()), "CancelJourney (an arena or a court may call a journey off)");
    }

    static void ChatStyle()
    {
        // Every line a player received across a busy scenario follows the Realm chat style.
        Player ada;
        World4(out ada);
        Protected.Add(C);
        var p = Mk(C, "Pell", 0, 0, "Corvane");
        Visit(p, 500, 500); Visit(p, 1500, 500);
        Cmd(p, "travel"); Cmd(p, "travel", "all"); Cmd(p, "travel", "varrow-hall"); Cmd(p, "travel", "kingsreach"); Tick(10);
        Cmd(p, "kit"); Cmd(p, "kit", "starter"); Cmd(p, "road", "old-mill"); Tick(15); Cmd(p, "home"); Cmd(p, "travel", "help");
        var bad = p.Messages.Where(m => !(Regex.IsMatch(m, @"^\[(D6A043|8FC97A|E8913A)\]Roads\[FFFFFF\]: ") || m.StartsWith("ERR [E86A5C]Roads[FFFFFF]: ") || m.StartsWith("  "))).ToList();
        Ok(p.Messages.Count > 20 && bad.Count == 0, "every reply opens with the Roads speaker in its tone, or continues a list", string.Join("\n", bad));
        var tags = p.Messages.SelectMany(m => Regex.Matches(m, @"\[([0-9A-Fa-f]{6})\]").Cast<Match>().Select(x => x.Groups[1].Value.ToUpperInvariant())).Distinct().ToList();
        var palette = new[] { "D6A043", "8FC97A", "E8913A", "E86A5C", "F4C96D", "A3A6AD", "FFFFFF", "C58FC0", "E08A5C", "8FB0BF", "B8B85A", "EC8A3C", "6FBF85" };
        Ok(tags.All(t => palette.Contains(t)), "only chat palette colours", string.Join(",", tags));
    }
}
