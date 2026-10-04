// Behaviour tests for plugins/RealmHeraldry.cs, compiled unchanged with Mocks.cs and World.cs. Run with run.sh.
// What this proves: the plugin's own rules (house arms and the guild sync, colour choices, the voter rules, council
// elections, referendums, data safety, config). What it does NOT prove: that the real game behaves like the mocks (see
// plugins/docs/RealmHeraldry.md for the in-game steps).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static class Tests
{
    const ulong A = 76561190000000001, B = 76561190000000002, C = 76561190000000003, Dd = 76561190000000004, E = 76561190000000005,
        G2 = 76561190000000006, J = 76561190000000007, K = 76561190000000008, L = 76561190000000009, M = 76561190000000010,
        N = 76561190000000011, Q = 76561190000000012;
    static string Repo;

    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : ".";
        Setup(); Sync(); EnforceOff(); SyncOptions(); Binding(); ArmsCommands(); ColourChoices(); ArmsAdmin(); Voters(); VoterRules();
        Election(); ElectionOutcomes(); ElectionAdmin(); Referendums(); LawReferendums(); DataSafety(); Api(); ConfigClamps(); ChatStyle();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    // Four houses: Varrow (guild 101: Ada the head, Bram, Cato), Wolves (guild 102: Wren the head, Esk, Gale), Ashgrove
    // (guild 103: Jory the head, Kell, Lisle) and Lone (no guild: Mott). Ada is staff. Everyone has 1000 marks.
    static Player Realm(out Player ada, Action<object> tweak = null)
    {
        Reset();
        House("Varrow", 101); House("Wolves", 102); House("Ashgrove", 103); House("Lone", 0);
        NewHeraldry(tweak);
        ada = Mk(A, "Ada", "Varrow", 1000); Admin(ada);
        Mk(B, "Bram", "Varrow", 1000); Mk(C, "Cato", "Varrow", 1000);
        Mk(Dd, "Wren", "Wolves", 1000); Mk(E, "Esk", "Wolves", 1000); Mk(G2, "Gale", "Wolves", 1000);
        Mk(J, "Jory", "Ashgrove", 1000); Mk(K, "Kell", "Ashgrove", 1000); Mk(L, "Lisle", "Ashgrove", 1000);
        Mk(M, "Mott", "Lone", 1000);
        Clear();
        return ada;
    }
    static Player P(ulong id) { return Everyone.First(p => p.Id == id); }
    static string PairId(string house) { return (string)F(Inv(H, "PairOf", house, false), "Id"); }
    static object Arms(string house) { var d = (IDictionary)D("Arms"); return d.Contains(house.ToLowerInvariant()) ? d[house.ToLowerInvariant()] : null; }
    static int Count<T>() { return EventManager.Events.Count(e => e is T); }

    static void Setup()
    {
        Player ada;
        Realm(out ada);
        Ok(H.permission.Registered.Contains("realmheraldry.admin"), "registers realmheraldry.admin");
        Ok(File.Exists(Path.Combine(Dir, "RealmHeraldry.json")), "a new realm writes its data file");
        Ok(H.timer.EveryCount == 1 && Math.Abs(H.timer.LastEvery - 60f) < 0.01f, "one tick timer, every minute");
        var pairs = ((IList)F(Cfg("Heraldry"), "Pairs")).Cast<object>().ToList();
        Ok(pairs.Count == 14 && pairs.Count(p => ((string)F(p, "ReservedFor")).Length > 0) == 6, "14 colour pairs, six reserved for the great houses");
        // Every colour is in art/palette.json, the source of truth for Realm's colours.
        string palette = File.ReadAllText(Path.Combine(Repo, "art", "palette.json")).ToLowerInvariant();
        var missing = pairs.SelectMany(p => new[] { (string)F(p, "Field"), (string)F(p, "Charge") }).Where(h => !palette.Contains("\"" + h.ToLowerInvariant() + "\"")).ToList();
        Ok(missing.Count == 0, "every pair's colours are listed in art/palette.json", string.Join(", ", missing));
        var great = new[] { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };
        foreach (var g in great)
        {
            var p = pairs.First(x => (string)F(x, "Id") == g);
            Ok(palette.Contains("\"field\": \"" + ((string)F(p, "Field")).ToLowerInvariant() + "\"") && ((string)F(p, "ReservedFor")).ToLowerInvariant() == g,
                "House " + g + " bears its own field from art/palette.json, reserved for it");
        }
        // The charge must read on the field (the emblem on the banner, the name-tag icon on its ground).
        var weak = pairs.Where(p => Contrast((string)F(p, "Field"), (string)F(p, "Charge")) < 3.0).Select(p => (string)F(p, "Id") + " " + Contrast((string)F(p, "Field"), (string)F(p, "Charge")).ToString("0.0")).ToList();
        Ok(weak.Count == 0, "every pair's charge stands out from its field (contrast 3:1 or more)", string.Join(", ", weak));
        var lang = H.lang.Msgs;
        Ok(lang.Values.All(v => Regex.Replace(v, @"\[[0-9A-Fa-f]{6}\]", "").Length <= 200), "no lang line is longer than 200 visible characters");
        Ok(lang["Speaker"] == "Heraldry" && lang["SpeakerCouncil"] == "Council", "two speakers: Heraldry and Council");
    }

    static double Lum(string hex)
    {
        Func<int, double> ch = i => { double c = Convert.ToInt32(hex.Substring(1 + 2 * i, 2), 16) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); };
        return 0.2126 * ch(0) + 0.7152 * ch(1) + 0.0722 * ch(2);
    }
    static double Contrast(string a, string b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    static void Sync()
    {
        Player ada;
        Realm(out ada);
        Tick(1);
        Ok(G(101).Name == "Varrow" && G(102).Name == "Wolves" && G(103).Name == "Ashgrove", "the first sync names each bound guild after its house");
        Ok(Field(101) == "#4a2347" && Charge(101) == "#9aa0a8", "Varrow's guild flies plum and iron grey (art/palette.json)", Field(101) + " " + Charge(101));
        Ok(Field(103) == "#7a3a1a" && Charge(103) == "#e8dfc8", "Ashgrove's guild flies rust and bone white");
        string wolves = PairId("Wolves");
        var wp = ((IList)F(Cfg("Heraldry"), "Pairs")).Cast<object>().First(p => (string)F(p, "Id") == wolves);
        Ok(((string)F(wp, "ReservedFor")).Length == 0 && Field(102) == (string)F(wp, "Field") && Charge(102) == (string)F(wp, "Charge"),
            "a house of the players' own flies an open pair, chosen by a stable hash of its name (" + wolves + ")");
        Ok(PairId("Wolves") == PairId("wolves") && PairId("Wolves") == wolves, "the hash ignores case and does not change");
        Ok(G(101).Banner.CurrentBanner == 1 && G(101).Banner.CurrentPattern == 2, "the guild's own banner and pattern are kept (indices are the client's)");
        Ok(Count<GuildUpdateEvent>() == 3 && Count<GuildBannerUpdateEvent>() == 3, "three guilds updated through GuildUpdateEvent, and each banner sent to everyone",
            Count<GuildUpdateEvent>() + " " + Count<GuildBannerUpdateEvent>());
        Ok(EventManager.Events.OfType<GuildBannerUpdateEvent>().All(e => e.Banner != null && G(e.GuildID).Banner == e.Banner), "the banner sent is the one the guild now flies");
        Ok(Guilds.Guilds.Count == 3, "the house with no guild (Lone) gets none made for it");
        Clear();
        Tick(5);
        Ok(EventManager.Events.Count == 0, "a realm in step raises no events");
        // A player renames the guild in the game's menu: the heralds put it back.
        G(101).Name = "Ada's Guild";
        G(102).Banner.CurrentColor = new UnityEngine.Color(1, 0, 0, 1);
        Tick(3);
        Ok(G(101).Name == "Varrow" && Field(102) == (string)F(wp, "Field"), "a guild renamed or recoloured in the game is put back at the next sync");
        Ok((int)F(Arms("Varrow"), "Drifts") == 1 && (int)F(Arms("Wolves"), "Drifts") == 1, "each drift is counted");
        Ok((ulong)F(Arms("Varrow"), "GuildId") == 101, "the bound guild is recorded");
        // A guild whose banner is shared by reference is never changed in place: each update brings a new BannerData.
        var before = G(101).Banner;
        G(101).Banner.CurrentPaternColor = new UnityEngine.Color(0, 0, 0, 1);
        Tick(3);
        Ok(!ReferenceEquals(before, G(101).Banner) && Charge(101) == "#9aa0a8", "each update sends a fresh BannerData");
    }

    static void EnforceOff()
    {
        Player ada;
        Realm(out ada, c => Section(c, "Heraldry", "Enforce", false));
        Tick(1);
        Ok(G(101).Name == "Varrow", "Enforce off: the heralds still raise the house's arms the first time");
        G(101).Name = "Ada's Guild";
        Tick(5);
        Ok(G(101).Name == "Ada's Guild", "Enforce off: a player's rename in the game stands");
        Purses[A.ToString()] = 1000;
        Cmd(ada, "heraldry", "colours", "moss-gold");
        Ok(Field(101) == "#4d6b3a" && G(101).Name == "Varrow", "Enforce off: a new choice of colours is still raised (with the house name)");
    }

    static void SyncOptions()
    {
        Player ada;
        Realm(out ada, c => Section(c, "Heraldry", "NameFormat", "House {0}"));
        Tick(1);
        Ok(G(101).Name == "House Varrow", "NameFormat House {0}");
        House("The Most Ancient And Honourable", 104);
        Mk(N, "Nell", "The Most Ancient And Honourable");
        Tick(3);
        Ok(G(104).Name.Length <= 28 && G(104).Name == "The Most Ancient And Honoura", "names are cut to the game's 28 characters", G(104).Name);

        Realm(out ada, c => Section(c, "Heraldry", "SyncGuildNames", false));
        Tick(1);
        Ok(G(101).Name == "Guild 101" && Field(101) == "#4a2347", "SyncGuildNames off: colours only");
        Realm(out ada, c => Section(c, "Heraldry", "SyncBannerColours", false));
        Tick(1);
        Ok(G(101).Name == "Varrow" && Field(101) == "#000000", "SyncBannerColours off: names only");

        Realm(out ada, c => Section(c, "Heraldry", "MaxAppliesPerSync", 1));
        Tick(1);
        Ok(Count<GuildUpdateEvent>() == 1, "MaxAppliesPerSync 1: one guild per sync");
        Tick(2);
        Ok(Count<GuildUpdateEvent>() == 2, "the next sync takes the next guild");
        Tick(2);
        Ok(G(101).Name == "Varrow" && G(102).Name == "Wolves" && G(103).Name == "Ashgrove", "until all are in step");

        Realm(out ada, c => Section(c, "Heraldry", "GuildCooldownSeconds", 600));
        Tick(1);
        G(101).Name = "Mine";
        Tick(3);
        Ok(G(101).Name == "Mine", "GuildCooldownSeconds: a guild updated a moment ago waits");
        Tick(8);
        Ok(G(101).Name == "Varrow", "and is put back once the cooldown has passed");

        Realm(out ada, c => Section(c, "Heraldry", "Enabled", false));
        Tick(5);
        Ok(EventManager.Events.Count == 0 && G(101).Name == "Guild 101", "Heraldry.Enabled off: no guild is touched");
        Ok(Cmd(ada, "heraldry", "sync").Contains("switched off"), "and /heraldry sync says so");

        Realm(out ada, c => Section(c, "Heraldry", "BroadcastBanner", false));
        Tick(1);
        Ok(Count<GuildUpdateEvent>() == 3 && Count<GuildBannerUpdateEvent>() == 0, "BroadcastBanner off: only the game's own update to members");
    }

    static void Binding()
    {
        Player ada;
        Realm(out ada);
        HouseNamed("Wolves").GuildId = 101;                       // a damaged file: two houses on one guild
        WriteHousesFile();
        Tick(1);
        Ok(G(101).Name == "Varrow" && G(102).Name == "Guild 102", "two houses bound to one guild: the first keeps it, the other is left unbound");
        Ok(Cmd(ada, "heraldry", "preview").Contains("also bound to House"), "and preview says why");
        HouseNamed("Wolves").GuildId = 999;
        WriteHousesFile();
        Ok(Cmd(ada, "heraldry", "preview").Contains("is gone"), "a guild that no longer exists is reported");

        // No RealmHouses.json: the leader's guild, when every member of it is in the house.
        Realm(out ada);
        File.Delete(Path.Combine(Dir, "RealmHouses.json"));
        WriteFile = false;
        try
        {
            Tick(1);
            Ok(G(102).Name == "Wolves", "without RealmHouses.json the leader's guild is used when all its members are the house's");
            G(103).Members().List.Add(new Member(M));
            G(103).Name = "x";
            Tick(3);
            Ok(G(103).Name == "x", "but not a guild that holds a player from outside the house");
            Ok(!File.Exists(Path.Combine(Dir, "RealmHouses.json")), "RealmHouses.json is never created by the heralds");
            Reload(c => Section(c, "Heraldry", "GuildFallbackByLeader", false));
            G(102).Name = "y";
            Tick(3);
            Ok(G(102).Name == "y", "GuildFallbackByLeader off: no file, no binding");
        }
        finally { WriteFile = true; }

        Realm(out ada);
        EventManager.Refuse = true;
        Tick(1);
        Ok(G(101).Name == "Guild 101" && H.Logged.Any(l => l.Contains("refused the guild update")), "a guild update the game refuses changes nothing and is logged");
        EventManager.Refuse = false;
        Tick(3);
        Ok(G(101).Name == "Varrow", "and is tried again at the next sync");
        Realm(out ada);
        EventManager.Throws = true;
        bool threw = false;
        try { Tick(1); } catch (Exception) { threw = true; }
        Ok(!threw && G(101).Name == "Guild 101" && H.Logged.Any(l => l.Contains("Could not update the guild")), "a game that throws is caught and logged");
        EventManager.Throws = false;
        Tick(3);
        Ok(G(101).Name == "Varrow", "and the next sync succeeds");
    }

    static void ArmsCommands()
    {
        Player ada;
        Realm(out ada);
        Tick(1);
        string r = Cmd(ada, "heraldry");
        Ok(r.Contains("bears Iron Stag: iron grey on a field of plum.") && r.Contains("the guild 'Varrow': in step"), "/heraldry shows your house's arms and its guild", r);
        r = Cmd(P(M), "heraldry");
        Ok(r.Contains("bound to no game guild yet") && r.Contains("/house link"), "a house with no guild is told how to bind one", r);
        var stray = Mk(N, "Nell");
        Ok(Cmd(stray, "heraldry").Contains("ERR") && stray.All().Contains("belong to no house"), "no house: told to found or join one");
        r = Cmd(stray, "heraldry", "house", "Wolves");
        Ok(r.Contains("House [") && r.Contains("(given by the heralds until its head chooses)"), "/heraldry house <house> shows another house's arms", r);
        Ok(Cmd(stray, "heraldry", "Ashgrove").Contains("White Oak"), "/heraldry <house> works too");
        Ok(Cmd(stray, "heraldry", "house", "Nobody").Contains("No house of that name"), "an unknown house is refused");
        G(102).Name = "Drift";
        r = Cmd(P(Dd), "heraldry");
        Ok(r.Contains("will bring it in step within"), "a guild out of step says when it will be put right", r);
        r = Cmd(ada, "heraldry", "colours");
        Ok(r.Contains("Colours a house may bear (14)") && Regex.Matches(r, @"\n  ").Count >= 14, "/heraldry colours lists the pairs", r);
        Ok(r.Contains("varrow - Iron Stag") && r.Contains("(yours)") && r.Contains("(the colours of House"), "your own pair and the reserved ones are marked");
        Ok(r.Contains("for 250 marks to the crown's treasury, once every 72 hours (the first choice is free)"), "the fee and cooldown are explained");
        Ok(Cmd(stray, "heraldry", "help").Contains("/heraldry colours") && !stray.All().Contains("Staff:"), "help, without the staff line for players");
        Ok(Cmd(ada, "heraldry", "help").Contains("Staff:"), "and with it for staff");
    }

    static void ColourChoices()
    {
        Player ada;
        Realm(out ada);
        Tick(1);
        var wren = P(Dd);
        long total = Total();
        string r = Cmd(wren, "heraldry", "colours", "blood-gold");
        Ok(r.Contains("now bears Blood and Gold") && Field(102) == "#8b2b22" && Charge(102) == "#d6a043", "the head chooses an open pair and the guild flies it at once", r);
        Ok(Charges.Count == 0 && Purses[Dd.ToString()] == 1000, "the first choice is free");
        Ok(B().Contains("raises new colours: Blood and Gold"), "the Herald tells the realm");
        Ok(Cmd(P(E), "heraldry", "colours", "lapis-gold").Contains("Only the head of House"), "only the head of the house may choose");
        Ok(Cmd(wren, "heraldry", "colours", "varrow").Contains("are the colours of House"), "a great house's colours are reserved for it");
        Ok(Cmd(P(J), "heraldry", "colours", "Blood and Gold").Contains("already bears those colours"), "a pair another house chose is taken (by name too)");
        Ok(Cmd(wren, "heraldry", "colours", "blood-gold").Contains("already bears Blood and Gold"), "choosing what you bear is refused");
        Ok(Cmd(wren, "heraldry", "colours", "lapis-gold").Contains("may change them again in"), "a new choice waits ChangeCooldownHours");
        Skip(TimeSpan.FromHours(73));
        Purses[Dd.ToString()] = 100;
        r = Cmd(wren, "heraldry", "colours", "lapis-gold");
        Ok(r.Contains("cost 250 marks") && PairId("Wolves") == "blood-gold" && Purses[Dd.ToString()] == 100, "a purse that cannot pay changes nothing", r);
        Purses[Dd.ToString()] = 1000;
        r = Cmd(wren, "heraldry", "colours", "lapis-gold");
        Ok(r.Contains("now bears Lapis and Gold") && Purses[Dd.ToString()] == 750 && TreasuryMarks == 250, "a later change costs 250 marks, paid to the crown's treasury", r);
        Ok(Charges.Last().Contains("|250|heraldry|new colours for House Wolves"), "through RealmTreasury.ChargeMarks with its source and note", Charges.LastOrDefault());
        Ok(Total() == total, "no mark is made or lost");
        Ok(Cmd(wren, "heraldry", "colours", "plaid").Contains("No pair of colours called plaid"), "an unknown pair is refused");
        r = Cmd(ada, "heraldry", "colours", "moss-gold");
        Ok(r.Contains("now bears Moss and Gold") && Field(101) == "#4d6b3a", "a great house may bear an open pair", r);
        Ok(Cmd(wren, "heraldry", "colours", "varrow").Contains("are the colours of House"), "and its own colours stay reserved for it");
        Ok(Cmd(P(J), "heraldry", "colours", "blood-gold").Contains("now bears Blood and Gold"), "a pair given up is free again");
        // A fallen house holds no colours.
        Houses.Remove(HouseNamed("Ashgrove"));
        Skip(TimeSpan.FromHours(80));
        Ok(Cmd(wren, "heraldry", "colours", "blood-gold").Contains("now bears Blood and Gold"), "the colours of a house that fell are free");
        Absent.Add("RealmTreasury");
        Reload();
        Skip(TimeSpan.FromHours(80));
        Ok(Cmd(wren, "heraldry", "colours", "iron-ember").Contains("treasury (RealmTreasury) is not open"), "a fee with no treasury loaded is refused");
        Absent.Clear();

        Realm(out ada, c => { Section(c, "Heraldry", "ReserveGreatHousePairs", false); Section(c, "Heraldry", "UniqueChoices", false); });
        Ok(Cmd(P(Dd), "heraldry", "colours", "corvane").Contains("now bears Black Raven"), "ReserveGreatHousePairs off: any house may bear a great house's colours");
        Ok(Cmd(P(J), "heraldry", "colours", "corvane").Contains("now bears Black Raven"), "UniqueChoices off: two houses may bear one pair");
        Realm(out ada, c => Section(c, "Heraldry", "FirstChoiceFree", false));
        Cmd(P(Dd), "heraldry", "colours", "blood-gold");
        Ok(Purses[Dd.ToString()] == 750, "FirstChoiceFree off: the first choice costs the fee too");
    }

    static void ArmsAdmin()
    {
        Player ada;
        Realm(out ada);
        Ok(Cmd(P(B), "heraldry", "sync").Contains("Only the realm's staff"), "/heraldry sync is for staff");
        string r = Cmd(ada, "heraldry", "preview");
        Ok(r.Contains("name 'Guild 101' to 'Varrow'") && r.Contains("colours #000000/#000000 to #4a2347/#9aa0a8") && r.Contains("no game guild"), "preview shows each change", r);
        Ok(G(101).Name == "Guild 101" && Count<GuildUpdateEvent>() == 0, "and changes nothing");
        Ok(Cmd(ada, "heraldry", "preview", "Wolves").Split('\n').Length == 2, "preview <house> shows one house");
        r = Cmd(ada, "heraldry", "sync");
        Ok(r.Contains("3 brought in step, 0 already in step, 1 with no game guild, 0 waiting"), "sync reports what it did", r);
        r = Cmd(ada, "heraldry", "set", "Wolves", "lapis-silver");
        Ok(r.Contains("now bears Lapis and Silver") && Field(102) == "#2c4a7a", "set <house> <pair> raises it at once", r);
        r = Cmd(ada, "heraldry", "reset", "Wolves");
        Ok(r.Contains("bears the heralds' choice again") && PairId("Wolves") != "lapis-silver", "reset gives the heralds' choice back");
        Ok(Cmd(ada, "heraldry", "banner", "Wolves", "1", "1").Contains("indices are off"), "banner indices are off until the counts are set");
        Realm(out ada, c => { Section(c, "Heraldry", "BannerCount", 4); Section(c, "Heraldry", "PatternCount", 10); });
        Ok(Cmd(ada, "heraldry", "banner", "Wolves", "3", "9").Contains("banner 3, pattern 9") && G(102).Banner.CurrentBanner == 3 && G(102).Banner.CurrentPattern == 9,
            "banner <house> <banner> <pattern> within the counts");
        Ok(Cmd(ada, "heraldry", "banner", "Wolves", "4", "1").Contains("Banner must be 0 to 3"), "an index past the client's count is refused");
        Cmd(ada, "heraldry", "banner", "Wolves", "keep", "keep");
        G(102).Banner.CurrentPattern = 5;
        Tick(3);
        Ok(G(102).Banner.CurrentPattern == 5, "keep: the guild's own index stands");
        r = Cmd(ada, "heraldry", "status");
        Ok(r.Contains("RealmHeraldry status") && r.Contains("RealmHouses.json: 4 houses") && r.Contains("Colour binding: ok"), "status", r);
        Ok(Cmd(ada, "heraldry", "set", "Nobody", "x").Contains("No house of that name"), "set on an unknown house is refused");
    }

    static void Voters()
    {
        Player ada;
        Realm(out ada);
        var nell = Mk(N, "Nell", null);
        var r = Voter(nell);
        Ok(r != null && (DateTime)F(r, "FirstSeen") == Clock, "a player is known from their first connection");
        Tick(1);
        Ok((long)F(r, "PlaySeconds") == 0, "the first minute after joining is not counted (no half minutes)");
        Tick(10);
        Ok((long)F(r, "PlaySeconds") == 600, "each minute online counts", F(r, "PlaySeconds").ToString());
        Offline(nell);
        Tick(10);
        Ok((long)F(r, "PlaySeconds") == 600, "time offline does not");
        Online(nell);
        Tick(2);
        Ok((long)F(r, "PlaySeconds") == 660, "back online: counted again after the first minute");
        // A long server stall counts at most two ticks.
        Clock = Clock.AddHours(5);
        Tick(1);
        Ok((long)F(r, "PlaySeconds") <= 660 + 120, "a stalled timer cannot credit hours at once");
        // House membership seeded from RealmHouses.json's Joined date.
        var ra = Voter(ada);
        Ok((DateTime)F(ra, "HouseSince") == Clock.AddHours(-5).AddMinutes(-23).AddDays(-30) || ((DateTime)F(ra, "HouseSince") < Clock.AddDays(-29)),
            "a member's time in the house comes from RealmHouses.json", F(ra, "HouseSince").ToString());
        Ok((DateTime)F(ra, "FirstSeen") <= (DateTime)F(ra, "HouseSince"), "and they count as seen since then");
        Join(nell, "Wolves", 0);
        Tick(1);
        Ok((string)F(r, "House") == "Wolves" && ((DateTime)F(r, "HouseSince") - Clock).Duration() < TimeSpan.FromMinutes(2), "a new member's time starts now");
        Leave(nell);
        Tick(1);
        Ok(F(r, "House") == null, "leaving the house is seen");
    }

    static void VoterRules()
    {
        Player ada;
        Realm(out ada);
        var nell = Mk(N, "Nell", "Wolves");
        Join(nell, "Wolves", 0);
        string id = N.ToString();
        Func<string> why = () => (string)Inv(H, "WhyNot", id, null, null);
        Inv(H, "Observe", nell, 0L);
        Ok(why() != null && why().Contains("seen in the realm for 72 hours"), "a new account waits MinAccountAgeHours", why());
        var r = Voter(nell);
        SetF(r, "FirstSeen", Clock.AddDays(-10));
        Ok(why().Contains("180 minutes of play"), "then MinPlayMinutes", why());
        SetF(r, "PlaySeconds", 200L * 60);
        Ok(why().Contains("in your house for 48 hours"), "then MinHouseMembershipHours", why());
        SetF(r, "HouseSince", Clock.AddDays(-3));
        Ok(why() == null, "then the player may vote");
        Protected.Add(N);
        Ok(why().Contains("new-player protection"), "not under new-player protection (RealmWarden)");
        Protected.Clear();
        Outlaws.Add(id);
        Ok(why().Contains("outlaws"), "not an outlaw of the crown (RealmContracts)");
        Outlaws.Clear(); CourtOutlaws.Add(id);
        Ok(why().Contains("outlaws"), "nor of the court (RealmLaws)");
        CourtOutlaws.Clear(); Exiles.Add(id);
        Ok(why().Contains("exiles"), "not exiled (RealmLaws)");
        Exiles.Clear();
        Leave(nell);
        Ok(why().Contains("only members of a house vote"), "a player with no house has no vote");
        Tick(1);
        Join(nell, "Wolves", 10);
        Ok(why().Contains("in your house for 48 hours"), "a house change not yet seen counts as new");
        Clock = Clock.AddSeconds(11);                             // the cached RealmHouses.json is read again at most every 10 s
        Inv(H, "Observe", nell, 0L);
        Ok(why() == null, "seen with the file's Joined date (10 days): may vote");
        Reload(c => { Section(c, "Voters", "MinWaystones", 2); });
        Waystones[id] = 1;
        Ok(why().Contains("find 2 waystones"), "MinWaystones asks RealmTravel", why());
        Waystones[id] = 2;
        Ok(why() == null, "and is met");
        Absent.Add("RealmTravel");
        Reload(c => { Section(c, "Voters", "MinWaystones", 2); });
        Waystones[id] = 0;
        Ok(why() == null, "without RealmTravel the waystone rule is not asked");
        Absent.Clear();
        Reload(c => { Section(c, "Voters", "OutlawsMayVote", true); Section(c, "Voters", "ExilesMayVote", true); Section(c, "Voters", "NewPlayersMayVote", true); Section(c, "Voters", "RequireHouse", false); });
        Outlaws.Add(id); Exiles.Add(id); Protected.Add(N); Leave(nell);
        Ok(why() == null, "every rule can be switched off");
        Outlaws.Clear(); Exiles.Clear(); Protected.Clear();
        Reload();
        string me = Cmd(nell, "ballot", "me");
        Ok(me.Contains("You may not vote yet: only members of a house vote") && me.Contains("Play counted here: 200 of 180 minutes"), "/ballot me explains", me);
        Join(nell, "Wolves", 10);
        Ok(Cmd(nell, "ballot", "me").Contains("You may vote"), "/ballot me says yes when it is yes");
        Clock = Clock.AddSeconds(11);
        Ok(Cmd(Mk(Q, "Quill", "Wolves"), "ballot", "me").Contains("seen in the realm for 72 hours") == false, "a member the file shows for 30 days counts as seen for 30 days");
    }

    // An election with Ada (Varrow), Wren (Wolves) and Jory (Ashgrove) standing.
    static Player ElectionRealm(out Player ada, Action<object> tweak = null)
    {
        Realm(out ada, tweak);
        Season = 1;
        Tick(1);
        Season = 2;
        Tick(1);
        Seasoned(Everyone.ToArray());
        Clear();
        return ada;
    }

    static void Election()
    {
        Player ada;
        Realm(out ada);
        Season = 1;
        Tick(1);
        Ok(Ballots().Count == 0, "the season under way when the heralds arrive is not elected (baseline)");
        Season = 2;
        Tick(1);
        Ok(Ballots().Count == 1 && Status(1) == "nominating", "a new season calls a council election");
        var seats = (List<string>)F(Ballot(1), "Seats");
        Ok(seats.SequenceEqual(new[] { "Keeper of Coin", "Marshal" }), "for the elected seats the crown's council has (Voice of the Crown stays the monarch's)");
        Ok(B().Contains("The council election of Season 2: The Hollow Crown is called") && B().Contains("/ballot stand"), "the Herald calls it", B());
        Tick(5);
        Ok(Ballots().Count == 1, "once per season");
        Seasoned(Everyone.ToArray());
        Ok(Cmd(P(B), "ballot", "stand", "Marshal").Contains("Only the head of a house"), "only heads of houses stand");
        Ok(Cmd(P(M), "ballot", "stand", "Marshal").Contains("has 1 members; a candidate's house needs at least 3"), "a house of one cannot field a candidate");
        House("Upstarts", 105, 1);
        var u1 = Mk(N, "Uly", "Upstarts", 1000); Mk(Q, "Uma", "Upstarts"); Mk(76561190000000013, "Urs", "Upstarts");
        Seasoned(u1);
        Ok(Cmd(u1, "ballot", "stand", "Marshal").Contains("must stand 3 days"), "a house younger than CandidateMinHouseAgeDays cannot");
        Kings.KingId = J;
        Ok(Cmd(P(J), "ballot", "stand", "Marshal").Contains("The monarch does not stand"), "the monarch does not stand");
        Kings.KingId = 0;
        Ok(Cmd(ada, "ballot", "stand", "Voice").Contains("not a seat the realm elects"), "a seat that is not elected is refused");
        Ok(Cmd(ada, "ballot", "stand").Contains("Seats elected now: Keeper of Coin, Marshal"), "stand with no seat lists them");
        long total = Total();
        string r = Cmd(ada, "ballot", "stand", "keep");
        Ok(r.Contains("You stand for Keeper of Coin (100 marks are held") && Purses[A.ToString()] == 900 && Holds.Count == 1, "Ada stands for Keeper of Coin; her deposit is held", r);
        Ok(B().Contains("Ada, head of House") && B().Contains("stands for Keeper of Coin"), "the Herald names the candidate");
        Ok(Cmd(ada, "ballot", "stand", "Marshal").Contains("You already stand for Keeper of Coin"), "one seat per candidate");
        Purses[Dd.ToString()] = 50;
        Ok(Cmd(P(Dd), "ballot", "stand", "Keeper of Coin").Contains("deposit of 100 marks") && Holds.Count == 1, "a deposit the purse cannot pay: no candidacy");
        Purses[Dd.ToString()] = 1000;
        Cmd(P(Dd), "ballot", "stand", "Keeper of Coin");
        Cmd(P(J), "ballot", "stand", "Marshal");
        Ok(Total() == total, "deposits move marks, they do not make or lose them");
        Ok(Cmd(P(B), "vote", "Ada").Contains("still taking candidates"), "no votes while candidates stand");
        r = Cmd(P(B), "ballot", "1");
        Ok(r.Contains("Keeper of Coin: Ada of House") && r.Contains("Marshal: Jory of House"), "/ballot <n> shows the candidates", r);
        Ok(Cmd(P(B), "ballot").Contains("#1 Council election, Season 2: The Hollow Crown: heads of houses stand"), "/ballot lists it");
        PopupsOff.Add(C.ToString());
        Skip(TimeSpan.FromHours(48));
        Ok(Status(1) == "voting" && B().Contains("The realm votes for its council"), "after NominationHours the realm votes", B());
        Ok(P(B).Popups.Count == 1 && P(B).Popups[0].Contains("The realm votes|") && P(B).Popups[0].EndsWith("|Ok|True"), "a notice window for each voter (broadcast)", string.Join(";", P(B).Popups));
        Ok(P(C).Popups.Count == 0, "but not for one who turned popups off (RealmHerald)");
        Ok(!P(B).Popups[0].Contains("[F4C96D]") && !P(B).Popups[0].Contains("[C58FC0]"), "the window has no chat colour tags");
        var nobody = Mk(76561190000000020, "Newt", "Varrow");
        Join(nobody, "Varrow", 0);
        Ok(Cmd(nobody, "vote", "Ada").Contains("You may not vote yet"), "an account the heralds do not know cannot vote");
        Ok(Cmd(P(B), "vote", "Ada").Contains("Your vote for Ada as Keeper of Coin is cast"), "/vote <candidate>");
        Ok(Cmd(P(C), "vote", "Varrow").Contains("for Ada"), "/vote <house>");
        Ok(Cmd(P(E), "vote", "House", "Wolves").Contains("for Wren"), "/vote House <house>");
        Ok(Cmd(P(G2), "vote", "wr").Contains("for Wren"), "/vote by a unique start");
        Ok(Cmd(P(K), "vote", "a").Contains("More than one candidate matches a: Ada, Ashgrove") || P(K).All().Contains("More than one candidate"), "an ambiguous start is refused", P(K).All());
        Ok(Cmd(P(K), "vote", "Zed").Contains("No candidate called Zed"), "an unknown candidate is refused");
        Cmd(P(K), "vote", "Wren");
        Ok(Cmd(P(K), "vote", "Wren").Contains("already your vote"), "the same vote twice is told");
        Ok(Cmd(P(K), "vote", "Ada").Contains("for Ada") && Cmd(P(K), "vote", "Wren").Contains("for Wren"), "a vote may be changed until the close");
        Cmd(P(L), "vote", "Jory");
        Ok(QuestReports.Count(q => q.EndsWith("|custom|vote_cast|1")) == 6 && QuestReports.Count(q => q.StartsWith(K + "|")) == 1, "each voter's first vote is reported to RealmQuests once");
        r = Cmd(P(B), "ballot", "1");
        Ok(r.Contains("6 have voted. Counts are kept sealed") && !Regex.IsMatch(r, @"\d+ of \d+ votes"), "only the turnout shows while voting", r);
        Ok(r.Contains("Your vote: Keeper of Coin: Ada"), "a voter sees their own vote");
        string bv = P(B).All();
        Skip(TimeSpan.FromHours(72));
        Ok(Status(1) == "closed", "after VotingHours the ballot closes");
        Ok(Seated.Count == 2 && Seated[0].StartsWith("Keeper of Coin|" + Dd + "|Wren|") && Seated[1].StartsWith("Marshal|" + J + "|Jory|"), "the winners are seated by the crown", string.Join("\n", Seated));
        DateTime until = DateTime.Parse(Seated[0].Split('|')[3], null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        Ok(Math.Abs((until - Clock.AddDays(28)).TotalMinutes) < 2, "for TermDays (28)", until.ToString("o"));
        Ok(B().Contains("Wren of House") && B().Contains("is elected Keeper of Coin (3 of 5 votes)") && B().Contains("Jory of House") && B().Contains("elected Marshal unopposed"),
            "the Herald announces the results", B());
        Ok(Awards.Count == 2 && Awards[0] == "Wolves|5|elected to the council: Keeper of Coin", "the winners' houses earn season points", string.Join(";", Awards));
        Ok(QuestReports.Count(q => q.EndsWith("|custom|council_elected|1")) == 2, "and RealmQuests hears of each winner");
        Ok(Logs.Count == 1 && Logs[0].StartsWith("vote_held|The realm elects its council|") && Logs[0].EndsWith("|Wren,Jory") && !Logs[0].Contains("[C58FC0]"),
            "one Chronicle entry (vote_held), plain text, with the winners", string.Join("\n", Logs));
        Ok(Holds.Count == 0 && Purses[A.ToString()] == 1000 && Purses[Dd.ToString()] == 1000 && Purses[J.ToString()] == 1000, "every deposit is back: the winner, the unopposed, and Ada with 40%");
        Ok(Total() == total, "and the realm's marks are whole");
        r = Cmd(P(B), "ballot", "results");
        Ok(r.Contains("is elected Keeper of Coin (3 of 5 votes)"), "/ballot results shows the last result", r);
        Ok(Cmd(P(B), "ballot", "history").Contains("#1 Council election"), "/ballot history");
        Ok(Cmd(P(B), "vote", "Ada").Contains("Nothing is open for voting"), "no votes after the close");
    }

    static void ElectionOutcomes()
    {
        Player ada;
        // A candidate under DepositReturnPercent loses the deposit to the crown's treasury.
        ElectionRealm(out ada);
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        long total = Total();
        Skip(TimeSpan.FromHours(48));
        foreach (var id in new[] { B, C, E, G2, K, L }) Cmd(P(id), "vote", "Wren");
        Skip(TimeSpan.FromHours(72));
        Ok(Purses[A.ToString()] == 900 && TreasuryMarks == 100 && P(A).All().Contains("goes to the crown's treasury"), "0 of 6 votes: Ada's deposit is forfeit to the crown's treasury");
        Ok(Charges.Any(c => c.StartsWith(A + "|100|heraldry|forfeit deposit")), "through ChargeMarks (released, then charged, in one tick)");
        Ok(Total() == total && Holds.Count == 0, "zero-sum");

        // Too few voices: no one is seated, deposits back.
        ElectionRealm(out ada);
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        Cmd(P(B), "vote", "Ada"); Cmd(P(E), "vote", "Wren");
        Skip(TimeSpan.FromHours(72));
        Ok(Seated.Count == 0 && B().Contains("Too few voices for Keeper of Coin (2 of 3 needed)"), "under MinTurnout the seat stays as it is", B());
        Ok(Purses[A.ToString()] == 1000 && Purses[Dd.ToString()] == 1000, "and the deposits come back");

        // A tie goes to the greater renown, then to whoever stood first.
        ElectionRealm(out ada);
        Cmd(P(Dd), "ballot", "stand", "Keeper"); Cmd(ada, "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        Cmd(P(B), "vote", "Ada"); Cmd(P(C), "vote", "Ada"); Cmd(P(E), "vote", "Wren"); Cmd(P(G2), "vote", "Wren");
        Renown[A.ToString()] = 50; Renown[Dd.ToString()] = 10;
        Skip(TimeSpan.FromHours(72));
        Ok(Seated.Count == 1 && Seated[0].Contains("|Ada|"), "a tie goes to the greater renown (RealmRenown)", string.Join(";", Seated));
        ElectionRealm(out ada);
        Cmd(P(Dd), "ballot", "stand", "Keeper"); Cmd(ada, "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        Cmd(P(B), "vote", "Ada"); Cmd(P(C), "vote", "Ada"); Cmd(P(E), "vote", "Wren"); Cmd(P(G2), "vote", "Wren");
        Skip(TimeSpan.FromHours(72));
        Ok(Seated.Count == 1 && Seated[0].Contains("|Wren|"), "then to whoever stood first");

        // The crown cannot seat the winner: said aloud, and the winner keeps the deposit.
        ElectionRealm(out ada);
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        foreach (var id in new[] { B, C, K, L }) Cmd(P(id), "vote", "Ada");
        SeatRefuses = true;
        Skip(TimeSpan.FromHours(72));
        Ok(B().Contains("Ada won Keeper of Coin, but the crown's council could not seat them") && Purses[A.ToString()] == 1000, "a seat the crown refuses is reported; the winner's deposit is returned");

        // Nobody stands.
        ElectionRealm(out ada);
        Skip(TimeSpan.FromHours(48));
        Ok(Status(1) == "closed" && B().Contains("No head of a house stood"), "an election nobody stands in closes at once");

        // Withdrawing: free before voting, forfeit after; votes for the candidate are dropped.
        ElectionRealm(out ada);
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        string r = Cmd(ada, "ballot", "withdraw");
        Ok(r.Contains("no longer stand for Keeper of Coin. Your deposit of 100 marks is returned.") && Purses[A.ToString()] == 1000, "withdrawing before the vote returns the deposit", r);
        Ok(Cmd(ada, "ballot", "withdraw").Contains("do not stand"), "withdrawing twice is refused");
        Cmd(ada, "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        Cmd(P(B), "vote", "Ada");
        r = Cmd(ada, "ballot", "withdraw");
        Ok(r.Contains("your deposit is forfeit") && Purses[A.ToString()] == 900 && TreasuryMarks == 100, "withdrawing during the vote forfeits it", r);
        Ok(!((IDictionary)F(Ballot(1), "Votes")).Values.Cast<Dictionary<string, string>>().Any(v => v.ContainsValue(A.ToString())), "and the votes for the candidate are dropped");

        // No vote changes when the server says so.
        ElectionRealm(out ada, c => Section(c, "Council", "AllowVoteChange", false));
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        Ok(Cmd(P(B), "vote", "Ada").Contains("Your vote for Ada as Keeper of Coin is cast.") && Cmd(P(B), "vote", "Wren").Contains("may not be changed"), "AllowVoteChange off");

        // A reload in the middle of the vote keeps everything.
        ElectionRealm(out ada);
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        foreach (var id in new[] { B, C, K }) Cmd(P(id), "vote", "Ada");
        Reload();
        Ok(Status(1) == "voting" && ((IDictionary)F(Ballot(1), "Votes")).Count == 3, "votes survive a reload");
        Skip(TimeSpan.FromHours(72));
        Ok(Seated.Count == 1 && Seated[0].Contains("|Ada|"), "and the ballot closes as it would have");

        // Moving house after the election opened costs the vote in it.
        ElectionRealm(out ada);
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        Skip(TimeSpan.FromHours(48));
        Join(P(M), "Wolves", 0);
        Skip(TimeSpan.FromHours(50));
        Ok(Cmd(P(M), "vote", "Wren").Contains("joined your house after this ballot opened"), "a player who changed house after the ballot opened cannot vote in it");
    }

    static void ElectionAdmin()
    {
        Player ada;
        Realm(out ada);
        Seasoned(Everyone.ToArray());
        Ok(Cmd(P(B), "ballot", "admin", "open").Contains("Only the realm's staff"), "/ballot admin is for staff");
        string r = Cmd(ada, "ballot", "admin", "open");
        Ok(r.Contains("Council election #1 is open") && Status(1) == "nominating", "admin open calls an election", r);
        Ok(Cmd(ada, "ballot", "admin", "open").Contains("already open (#1)"), "one council election at a time");
        Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Marshal");
        r = Cmd(ada, "ballot", "admin", "advance", "1");
        Ok(Status(1) == "voting" && r.Contains("moves on: voting"), "advance: to the vote", r);
        Cmd(P(B), "vote", "Ada"); Cmd(P(C), "vote", "Ada"); Cmd(P(E), "vote", "Wren");
        r = Cmd(ada, "ballot", "admin", "audit", "1");
        Ok(r.Contains("Ballot #1: 3 votes (0 struck)") && r.Contains("2 voters") && r.Contains("Least seen voters"), "audit shows votes by house and the least played", r);
        r = Cmd(ada, "ballot", "admin", "strike", "1", "Cato");
        Ok(r.Contains("Cato's vote in ballot #1 is struck") && ((IDictionary)F(Ballot(1), "Votes")).Count == 2, "strike removes a vote", r);
        Ok(Cmd(P(C), "vote", "Ada").Contains("struck by the realm's staff"), "and the struck voter cannot vote again in it");
        Ok(H.Logged.Any(l => l.Contains("struck by Ada")), "a strike is logged");
        Ok(Cmd(ada, "ballot", "admin", "strike", "1", "Nobody").Contains("No player called Nobody"), "strike an unknown player is refused");
        r = Cmd(ada, "ballot", "admin", "voter", "Bram");
        Ok(r.Contains("Bram: first seen") && r.Contains("May vote now"), "voter <player> shows the voter's record", r);
        r = Cmd(ada, "ballot", "admin", "cancel", "1");
        Ok(r.Contains("Ballot #1 is cancelled; 2 deposit(s) returned") && Status(1) == "cancelled" && Holds.Count == 0 && Purses[A.ToString()] == 1000, "cancel returns every deposit", r);
        Ok(B().Contains("Ballot #1 is called off"), "and the Herald says so");
        Ok(Cmd(ada, "ballot", "admin", "advance", "1").Contains("is not open"), "a closed ballot cannot be moved");
        Ok(Cmd(ada, "ballot", "admin", "advance", "9").Contains("No ballot #9"), "an unknown ballot is refused");
        r = Cmd(ada, "ballot", "admin", "open");
        Cmd(ada, "ballot", "admin", "advance", "2");
        Ok(Status(2) == "closed", "advance with no candidates closes it");

        Realm(out ada, c => Section(c, "Council", "ElectedSeats", new List<string> { "High Steward" }));
        Ok(Cmd(ada, "ballot", "admin", "open").Contains("No elected seat matches the crown's council"), "an elected seat the crown does not have is refused");
        Realm(out ada, c => Section(c, "Council", "Enabled", false));
        Ok(Cmd(ada, "ballot", "admin", "open").Contains("switched off"), "Council.Enabled off");
        Season = 1; Tick(1); Season = 2; Tick(1);
        Ok(Ballots().Count == 0, "and no season election either");
        Realm(out ada);
        Absent.Add("CrownAndConsequences");
        Reload();
        Ok(Cmd(ada, "ballot", "admin", "open").Contains("CrownAndConsequences) is not loaded"), "no crown, no election");
        Absent.Clear();
        Realm(out ada, c => Section(c, "Council", "ElectEachSeason", false));
        Season = 1; Tick(1); Season = 2; Tick(1);
        Ok(Ballots().Count == 0, "ElectEachSeason off: only staff open elections");
    }

    static void Referendums()
    {
        Player ada;
        Realm(out ada);
        Seasoned(Everyone.ToArray());
        Kings.KingId = J;
        var jory = P(J);
        Ok(Cmd(P(B), "ballot", "propose", "decree", "open_roads").Contains("Only the monarch"), "only the monarch puts a question");
        Ok(Cmd(jory, "ballot", "propose", "decree", "golden_age").Contains("No decree called golden_age"), "an unknown decree is refused");
        Ok(Cmd(jory, "ballot", "propose").Contains("/ballot propose"), "propose alone shows how");
        string r = Cmd(jory, "ballot", "propose", "decree", "open_roads");
        Ok(r.Contains("Referendum #1 is put to the realm: Shall the crown issue the decree Open Roads?") && Status(1) == "voting", "the monarch asks the realm about a decree", r);
        Ok(B().Contains("The crown asks the realm: Shall the crown issue the decree Open Roads?"), "the Herald asks it");
        Ok(P(B).Popups.Count == 1 && P(B).Popups[0].Contains("Shall the crown issue the decree Open Roads?"), "a window for each voter");
        Ok(Cmd(jory, "ballot", "propose", "decree", "royal_stores").Contains("already voting on a question (#1)"), "one question at a time");
        Ok(Cmd(P(B), "vote", "yes").Contains("Your vote, yes, on referendum #1 is cast"), "/vote yes");
        Ok(Cmd(P(B), "vote", "no").Contains("Your vote, no,"), "changed to no");
        Ok(Cmd(P(B), "vote", "aye").Contains("Your vote, yes,") && Cmd(P(B), "vote", "1", "yes").Contains("already your vote"), "aye and /vote <n> yes");
        foreach (var id in new[] { C, Dd, E }) Cmd(P(id), "vote", "yes");
        Cmd(P(K), "vote", "nay");
        Ok(Cmd(P(B), "ballot").Contains("#1 Shall the crown issue the decree Open Roads?: voting ends in"), "/ballot lists the question");
        Skip(TimeSpan.FromHours(24));
        Ok(Status(1) == "closed" && B().Contains("The realm answers yes (4 to 1): Shall the crown issue the decree Open Roads?"), "carried", B());
        Ok(Mandates.Count == 1 && Mandates[0] == "open_roads|True|72", "the crown is told: the decree costs no authority (SetDecreeMandate)", string.Join(";", Mandates));
        Ok(B().Contains("may now be issued without cost"), "and the realm hears so");
        Ok(Logs.Count == 1 && Logs[0].StartsWith("vote_held|The realm answers the crown|"), "Chronicle: vote_held");
        Ok(Cmd(jory, "ballot", "propose", "decree", "royal_stores").Contains("may be asked again in"), "the next question waits CooldownHours from the answer");
        Skip(TimeSpan.FromHours(25));
        Cmd(jory, "ballot", "propose", "decree", "royal_stores");
        foreach (var id in new[] { B, C, Dd, E }) Cmd(P(id), "vote", "no");
        Cmd(P(K), "vote", "yes");
        Skip(TimeSpan.FromHours(24));
        Ok(Mandates.Count == 2 && Mandates[1] == "royal_stores|False|72" && B().Contains("may not be issued for 72 hours"), "refused: the decree is barred for MandateHours", string.Join(";", Mandates));
        Skip(TimeSpan.FromHours(25));
        Cmd(jory, "ballot", "propose", "decree", "tax_relief");
        Cmd(P(B), "vote", "yes"); Cmd(P(C), "vote", "yes"); Cmd(P(Dd), "vote", "no");
        Skip(TimeSpan.FromHours(24));
        Ok(Mandates.Count == 2 && B().Contains("Too few voices to answer (3 of 5 needed)"), "under MinTurnout the realm gives no answer");
        Ok(Cmd(ada, "ballot", "propose", "decree", "tax_relief").Contains("Referendum #4"), "staff may put a question (no cooldown)");
        Ok(Cmd(P(B), "vote", "4", "maybe").Contains("/vote"), "a choice that is not yes or no is refused");
        Cmd(ada, "ballot", "admin", "cancel", "4");

        // Exactly half is not a majority.
        Realm(out ada, c => Section(c, "Referendum", "MinTurnout", 4));
        Seasoned(Everyone.ToArray());
        Kings.KingId = J;
        Cmd(P(J), "ballot", "propose", "decree", "open_roads");
        Cmd(P(B), "vote", "yes"); Cmd(P(C), "vote", "yes"); Cmd(P(E), "vote", "no"); Cmd(P(G2), "vote", "no");
        Skip(TimeSpan.FromHours(24));
        Ok(Mandates.Count == 1 && Mandates[0] == "open_roads|False|72", "a tie is not carried (PassPercent 50 means more than half)");
        Ok(Cmd(P(B), "vote", "yes").Contains("Nothing is open for voting"), "/vote yes with nothing open");

        Realm(out ada, c => Section(c, "Referendum", "Decrees", false));
        Kings.KingId = J;
        Ok(Cmd(P(J), "ballot", "propose", "decree", "open_roads").Contains("Referendums on decrees are switched off"), "Referendum.Decrees off");
        Realm(out ada, c => Section(c, "Referendum", "Enabled", false));
        Kings.KingId = J;
        Ok(Cmd(P(J), "ballot", "propose", "decree", "open_roads").Contains("Referendums are switched off"), "Referendum.Enabled off");
    }

    static void LawReferendums()
    {
        Player ada;
        Realm(out ada);
        Seasoned(Everyone.ToArray());
        Kings.KingId = J;
        var jory = P(J);
        Ok(Cmd(jory, "ballot", "propose", "law", "Kings Peace!").Contains("named by its id"), "a law is named by its id");
        string r = Cmd(jory, "ballot", "propose", "law", "kings_peace");
        Ok(r.Contains("Shall the law kings_peace be proclaimed?"), "a law not in force: shall it be proclaimed", r);
        foreach (var id in new[] { B, C, Dd, E, K }) Cmd(P(id), "vote", "no");
        Skip(TimeSpan.FromHours(24));
        Ok(B().Contains("The realm answers no (0 to 5)") && B().Contains("the crown acts with"), "the realm's word is given", B());
        Ok(((IList)D("LawWatches")).Count == 1, "and watched for MandateHours");
        Clear();
        ActiveLaws.Add("kings_peace|The King's Peace|peace");
        Tick(1);
        Ok(B().Contains("The crown defies the realm's vote: kings_peace is proclaimed"), "proclaiming it anyway is called out by the Herald", B());
        Ok(Logs.Count == 1 && Logs[0].StartsWith("vote_held|The crown defies the realm's vote|"), "and chronicled");
        Clear();
        Tick(3);
        Ok(Logs.Count == 0 && Server.Broadcasts.Count == 0, "once");

        Realm(out ada);
        Seasoned(Everyone.ToArray());
        Kings.KingId = J;
        ActiveLaws.Add("market_curfew|Curfew of the Market|curfew");
        r = Cmd(P(J), "ballot", "propose", "law", "market_curfew");
        Ok(r.Contains("Shall the law Curfew of the Market be repealed?"), "a law in force: shall it be repealed (named from RealmLaws)", r);
        foreach (var id in new[] { B, C, Dd, E, K }) Cmd(P(id), "vote", "no");
        Skip(TimeSpan.FromHours(24));
        Clear();
        Tick(5);
        Ok(Logs.Count == 0, "keeping the law the realm kept is no defiance");
        ActiveLaws.Clear();
        Tick(1);
        Ok(B().Contains("Curfew of the Market is repealed though the realm said otherwise"), "repealing it is", B());

        Realm(out ada);
        Seasoned(Everyone.ToArray());
        Kings.KingId = J;
        Cmd(P(J), "ballot", "propose", "law", "kings_peace");
        foreach (var id in new[] { B, C, Dd, E, K }) Cmd(P(id), "vote", "no");
        Skip(TimeSpan.FromHours(24));
        Skip(TimeSpan.FromHours(73));
        ActiveLaws.Add("kings_peace|The King's Peace|peace");
        Clear();
        Tick(2);
        Ok(Logs.Count == 0 && ((IList)D("LawWatches")).Count == 0, "after MandateHours the crown is free again");
        Realm(out ada);
        Absent.Add("RealmLaws");
        Reload();
        Kings.KingId = J;
        Ok(Cmd(P(J), "ballot", "propose", "law", "kings_peace").Contains("RealmLaws) are not loaded"), "no laws plugin, no law questions");
        Absent.Clear();
    }

    static void DataSafety()
    {
        Player ada;
        Realm(out ada);
        string file = Path.Combine(Dir, "RealmHeraldry.json");
        Inv(H, "Unload");
        File.WriteAllText(file, "");
        int writes = Interface.Oxide.DataFileSystem.Writes;
        NewHeraldry();
        Ok((bool)F(H, "loadFailed") && H.Logged.Any(l => l.Contains("empty or truncated")), "a truncated data file pauses the plugin");
        Ok(Cmd(ada, "heraldry").Contains("paused") && Cmd(ada, "vote", "yes").Contains("paused"), "commands say so");
        Tick(3);
        Inv(H, "Unload");
        Ok(File.ReadAllText(file) == "" && Interface.Oxide.DataFileSystem.Writes == writes, "and the file is never overwritten");
        File.WriteAllText(file, "{ \"Arms\": [ broken");
        NewHeraldry();
        Ok((bool)F(H, "loadFailed") && H.Logged.Any(l => l.Contains("Could not read")), "unreadable JSON pauses it too");
        File.WriteAllText(file, "{ \"Format\": 99 }");
        NewHeraldry();
        Ok((bool)F(H, "loadFailed") && H.Logged.Any(l => l.Contains("newer RealmHeraldry")), "a file from a newer version is left alone");
        File.WriteAllText(file, "{ \"Format\": 1, \"Voters\": { \"bad\": { \"Name\": \"x\" }, \"" + B + "\": { \"Name\": \"Bram\", \"PlaySeconds\": -5 } },"
            + " \"Ballots\": [ { \"Id\": 7, \"Kind\": \"nonsense\" }, { \"Id\": 3, \"Kind\": \"council\", \"Status\": \"weird\", \"Candidates\": [ { \"Seat\": \"Marshal\" } ] } ],"
            + " \"Arms\": { \"wolves\": { \"Pair\": \"blood-gold\" } } }");
        NewHeraldry();
        var voters = (IDictionary)D("Voters");
        Ok(!(bool)F(H, "loadFailed") && !voters.Contains("bad") && (long)F(voters[B.ToString()], "PlaySeconds") == 0, "bad voter entries are dropped and repaired");
        Ok(Ballots().Count == 1 && Status(3) == "cancelled" && ((IList)F(Ballot(3), "Candidates")).Count == 0, "a ballot of unknown kind is dropped; an unknown status counts as cancelled");
        Ok((int)D("NextBallotId") == 4, "new ballots never reuse an id");
        Ok(PairId("Wolves") == "blood-gold" && (string)F(Arms("wolves"), "House") == "wolves", "arms survive with their house");
        // Closed ballots are trimmed.
        Realm(out ada, c => Section(c, "General", "ClosedBallotsKept", 2));
        for (int i = 0; i < 4; i++) { Cmd(ada, "ballot", "admin", "open"); Cmd(ada, "ballot", "admin", "cancel", (i + 1).ToString()); }
        Ok(Ballots().Count == 2 && (int)F(Ballots()[0], "Id") == 3, "only ClosedBallotsKept closed ballots are kept");
        // Voters are capped, but never one with a vote in an open ballot.
        Realm(out ada, c => Section(c, "General", "MaxVotersKept", 100));
        Seasoned(Everyone.ToArray());
        Cmd(ada, "ballot", "admin", "open"); Cmd(ada, "ballot", "stand", "Keeper"); Cmd(P(Dd), "ballot", "stand", "Keeper");
        Cmd(ada, "ballot", "admin", "advance", "1");
        Cmd(P(B), "vote", "Ada");
        SetF(Voter(P(B)), "LastSeen", Clock.AddYears(-3));
        for (int i = 0; i < 150; i++) ((IDictionary)D("Voters"))[(76561190100000000 + (ulong)i).ToString()] = Activator.CreateInstance(Voter(ada).GetType());
        Inv(H, "PruneVoters");
        Ok(((IDictionary)D("Voters")).Count == 100 && Voter(P(B)) != null, "the voter ledger is capped (MaxVotersKept) but keeps open ballots' voters");
    }

    static void Api()
    {
        Player ada;
        Realm(out ada);
        Ok((string)Inv(H, "GetHouseArms", "varrow") == "varrow|#4a2347|#9aa0a8|Iron Stag" && Inv(H, "GetHouseArms", "Nobody") == null, "GetHouseArms");
        Seasoned(Everyone.ToArray());
        Ok((bool)Inv(H, "IsEligibleVoter", B.ToString()) && !(bool)Inv(H, "IsEligibleVoter", "1"), "IsEligibleVoter");
        Ok(Inv(H, "GetBallotSummary") == null, "GetBallotSummary: nothing open");
        Cmd(ada, "ballot", "admin", "open");
        Ok(((string)Inv(H, "GetBallotSummary")).StartsWith("Council election: heads of houses stand for Keeper of Coin, Marshal"), "GetBallotSummary: an election", (string)Inv(H, "GetBallotSummary"));
        Cmd(ada, "ballot", "stand", "Keeper");
        Cmd(ada, "ballot", "admin", "advance", "1");
        string s = (string)Inv(H, "GetBallotSummary");
        Ok(s.Contains("Keeper of Coin: Ada (Varrow)") && !s.Contains("["), "and its candidates in plain text", s);
    }

    static void ConfigClamps()
    {
        Player ada;
        Realm(out ada, c =>
        {
            Section(c, "Heraldry", "SyncSeconds", 1);
            Section(c, "Heraldry", "NameFormat", "Banner of the house");
            Section(c, "Council", "ElectedSeats", new List<string> { "Marshal", "marshal", "", "Keeper of Coin" });
            Section(c, "Referendum", "PassPercent", 100);
        });
        Ok((int)F(Cfg("Heraldry"), "SyncSeconds") == 30 && (string)F(Cfg("Heraldry"), "NameFormat") == "{0}", "SyncSeconds is clamped; a NameFormat without {0} falls back");
        Ok(((List<string>)F(Cfg("Council"), "ElectedSeats")).SequenceEqual(new[] { "Marshal", "Keeper of Coin" }), "elected seats are cleaned of duplicates and blanks");
        Ok((int)F(Cfg("Referendum"), "PassPercent") == 99, "PassPercent stays below 100");
        var t = typeof(RealmHeraldry).GetNestedType("ArmsPair", W.BF);
        Realm(out ada, c =>
        {
            var list = (IList)F(F(c, "Heraldry"), "Pairs");
            var bad = Activator.CreateInstance(t); SetF(bad, "Id", "bad pair!"); SetF(bad, "Field", "#123456"); SetF(bad, "Charge", "#654321");
            var bad2 = Activator.CreateInstance(t); SetF(bad2, "Id", "nohex"); SetF(bad2, "Field", "red"); SetF(bad2, "Charge", "#654321");
            list.Add(bad); list.Add(bad2);
        });
        Ok(((IList)F(Cfg("Heraldry"), "Pairs")).Count == 14 && H.Logged.Count(l => l.Contains("is left out")) == 2, "a pair with a bad id or colour is left out");
        Realm(out ada, c =>
        {
            var list = (IList)F(F(c, "Heraldry"), "Pairs");
            for (int i = list.Count - 1; i >= 0; i--) if (((string)F(list[i], "ReservedFor")).Length == 0) list.RemoveAt(i);
        });
        Ok(((IList)F(Cfg("Heraldry"), "Pairs")).Count == 14, "with no open pair left the defaults are used");
    }

    static void ChatStyle()
    {
        // Every line a player received across a busy scenario follows the Realm chat style.
        Player ada;
        Realm(out ada);
        Seasoned(Everyone.ToArray());
        var bram = P(B);
        Tick(1);
        Cmd(bram, "heraldry"); Cmd(bram, "heraldry", "colours"); Cmd(bram, "heraldry", "colours", "blood-gold"); Cmd(bram, "heraldry", "house", "Wolves");
        Cmd(bram, "ballot"); Cmd(bram, "ballot", "me"); Cmd(bram, "vote"); Cmd(bram, "vote", "yes"); Cmd(bram, "ballot", "help"); Cmd(bram, "ballot", "stand", "Marshal");
        Cmd(ada, "ballot", "admin", "open"); Cmd(ada, "ballot", "stand", "Keeper"); Cmd(ada, "ballot", "admin", "advance", "1");
        Cmd(bram, "vote", "Ada"); Cmd(bram, "ballot", "1"); Cmd(bram, "ballot", "results"); Cmd(bram, "ballot", "history");
        var bad = bram.Messages.Where(m => !(Regex.IsMatch(m, @"^\[(D6A043|8FC97A|E8913A)\](Heraldry|Council)\[FFFFFF\]: ") || Regex.IsMatch(m, @"^ERR \[E86A5C\](Heraldry|Council)\[FFFFFF\]: ") || m.StartsWith("  "))).ToList();
        Ok(bram.Messages.Count > 25 && bad.Count == 0, "every reply opens with the Heraldry or Council speaker in its tone, or continues a list", string.Join("\n", bad));
        var palette = new[] { "D6A043", "8FC97A", "E8913A", "E86A5C", "F4C96D", "A3A6AD", "FFFFFF", "C58FC0", "E08A5C", "8FB0BF", "B8B85A", "EC8A3C", "6FBF85" };
        var tags = bram.Messages.Concat(Server.Broadcasts).SelectMany(m => Regex.Matches(m, @"\[([0-9A-Fa-f]{6})\]").Cast<Match>().Select(x => x.Groups[1].Value.ToUpperInvariant())).Distinct().ToList();
        Ok(tags.All(t => palette.Contains(t)), "only chat palette colours", string.Join(",", tags));
        Ok(Server.Broadcasts.All(b => b.StartsWith("[D6A043]Herald[FFFFFF]: ")), "realm-wide news is spoken by the Herald");
        // A player name with colour tags cannot recolour a line.
        var trick = Mk(N, "[FF0000]Evil", "Wolves");
        Seasoned(trick);
        Cmd(trick, "ballot", "me");
        Ok(!trick.All().Contains("[FF0000]"), "colour tags in a name are stripped");
    }
}
