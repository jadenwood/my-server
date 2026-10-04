// Behaviour tests for plugins/RealmWorld.cs, compiled unchanged with Mocks.cs and World.cs. Run with run.sh.
// What this proves: the plugin's own rules (schedule and clashes with RealmEvents, treasure hunts and chests, the Blood
// Moon, the caravan and raider bounties, Wandering Legends, festivals and decorations, the census, rewards, data safety,
// the painted board). What it does NOT prove: that the real game behaves like the mocks (see plugins/docs/RealmWorld.md).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static class Tests
{
    const ulong A = 76561190000000001, Bb = 76561190000000002, C = 76561190000000003, Dd = 76561190000000004, E = 76561190000000005,
        G = 76561190000000006, H = 76561190000000007, J = 76561190000000008;
    static string Repo;

    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Setup(); DefaultsNeverMeetRealmEvents(); Schedule(); ClashWithRealmEvents(); TreasureLayout(); TreasureHunt(); TreasureChest(); TreasureHints();
        BloodMoon(); Caravan(); CaravanPlunder(); CaravanFailures(); CaravanWaystones(); LegendBound(); LegendRegion(); Festival(); FestivalClose();
        Census(); OwedRewards(); Board(); DataSafety(); Config(); ChatStyle();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static Player Staff()
    {
        var ada = Mk(A, "Ada", 0, 0);
        Admin(ada);
        return ada;
    }

    static void Setup()
    {
        Reset();
        NewWorld();
        Ok(T.permission.Registered.Contains("realmworld.admin"), "registers realmworld.admin");
        Ok(File.Exists(Path.Combine(Dir, "RealmWorld.json")), "a new realm writes its data file");
        Ok(T.timer.EveryCount == 1 && Math.Abs(T.timer.LastEvery - 5f) < 0.01f, "one tick timer, every 5 s");
        var slots = (IList)F(F(T, "config"), "Schedule");
        Ok(slots.Count == 8, "eight default slots", slots.Count.ToString());
        Ok(((IList)F(Cfg("Legends"), "Legends")).Count == 3 && ((IList)F(Cfg("Festivals"), "Festivals")).Count == 2, "three legends, two festivals");
        var countdown = (List<int>)F(Cfg("General"), "CountdownMinutes");
        Ok(countdown.SequenceEqual(new[] { 30, 10, 1 }), "countdown 30, 10, 1 min");
        var places = (List<int>)F(((IList)F(Cfg("Festivals"), "Festivals"))[0], "PlacePoints");
        Ok(places.SequenceEqual(new[] { 40, 25, 10 }), "festival place points 40/25/10");
        var lang = T.lang.Msgs;
        Ok(lang.Values.All(v => Regex.Replace(v, @"\[[0-9A-Fa-f]{6}\]", "").Length <= 200), "no lang line is longer than 200 visible characters",
            string.Join(", ", lang.Where(kv => Regex.Replace(kv.Value, @"\[[0-9A-Fa-f]{6}\]", "").Length > 200).Select(kv => kv.Key)));
        Ok(lang["Speaker"] == "World" && lang["Herald"] == "[D6A043]Herald[FFFFFF]: ", "speaker World; the one Herald voice");
        Ok(lang.Keys.Where(k => k.StartsWith("Kind.")).Count() == 5, "a name for each of the five kinds");
        // Every default item name resolves against the item list.
        object cfg = F(T, "config");
        var names = new List<string>();
        foreach (var it in (IList)F(Cfg("Treasure"), "Items")) names.Add((string)F(it, "Item"));
        foreach (var l in (IList)F(Cfg("Legends"), "Legends")) foreach (var it in (IList)F(l, "Trophy")) names.Add((string)F(it, "Item"));
        foreach (var f in (IList)F(Cfg("Festivals"), "Festivals")) foreach (var o in (IList)F(f, "Offerings")) names.Add((string)F(o, "Item"));
        Ok(names.Count > 15 && names.All(n => Inv(T, "Blueprint", n) != null), "every default reward and offering is a ResourceType name the game knows", string.Join(",", names));
    }

    // RealmEvents' default schedule (read from its source) and RealmWorld's: two weeks, minute by minute, with the
    // buffer around each RealmEvents event: never a shared minute; and no two world events at once.
    static void DefaultsNeverMeetRealmEvents()
    {
        string src = File.ReadAllText(Path.Combine(Repo, "plugins", "RealmEvents.cs"));
        var ev = new List<(string day, int start, int len)>();
        foreach (Match m in Regex.Matches(src, @"new ScheduleEntry \{ Id = ""[^""]+"", Event = \w+, Days = new List<string> \{ ""(\w+)"" \}, StartUtc = ""(\d\d):(\d\d)"", DurationMinutes = (\d+) \}"))
            ev.Add((m.Groups[1].Value, int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
        Ok(ev.Count == 4, "read RealmEvents' four default slots", ev.Count.ToString());
        Reset();
        NewWorld();
        int buffer = (int)F(Cfg("General"), "ClashBufferMinutes");
        var blocked = new bool[14 * 1440];
        var start = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);       // a Monday
        for (int d = 0; d < 14; d++)
            foreach (var e in ev)
                if (start.AddDays(d).DayOfWeek.ToString() == e.day)
                    for (int t = d * 1440 + e.start - buffer; t < d * 1440 + e.start + e.len + buffer; t++) if (t >= 0 && t < blocked.Length) blocked[t] = true;
        var occ = (IList)Inv(T, "NextOccurrences", start.AddMinutes(-1), 14, 1000);
        var own = new int[14 * 1440];
        string clash = "", overlap = "";
        int seen = 0;
        foreach (var u in occ)
        {
            var slot = F(u, "Slot");
            DateTime at = (DateTime)F(u, "At");
            int s0 = (int)(at - start).TotalMinutes, len = Math.Max(1, (int)F(slot, "DurationMinutes"));
            seen++;
            for (int t = s0; t < s0 + len && t < blocked.Length; t++)
            {
                if (blocked[t]) clash += F(slot, "Id") + "@" + at + " ";
                if (own[t]++ > 0) overlap += F(slot, "Id") + "@" + at + " ";
            }
        }
        Ok(seen >= 14, "two weeks of world events", seen.ToString());
        Ok(clash.Length == 0, "no default world event comes within ClashBufferMinutes of a default RealmEvents event", clash);
        Ok(overlap.Length == 0, "no two default world events share a minute", overlap);
    }

    static void Schedule()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "old-mill", "the Miller's Hoard", 100, 0, 200, 0);
        Clear();
        Clock = new DateTime(2026, 10, 6, 18, 30, 0, DateTimeKind.Utc);       // Tuesday; the hunt is at 19:00
        Tick();
        Ok(B().Contains("A treasure hunt begins in 30 min: the Miller's Hoard."), "the 30-minute countdown names the hunt", B());
        Clear();
        Tick();
        Ok(B().Length == 0, "each countdown once");
        Clock = new DateTime(2026, 10, 6, 18, 50, 2, DateTimeKind.Utc);
        Tick();
        Ok(B().Contains("begins in 10 min"), "the 10-minute countdown", B());
        Clock = new DateTime(2026, 10, 6, 18, 59, 58, DateTimeKind.Utc);
        Tick();
        Ok(ActiveKind() == "treasure", "the hunt starts on the hour");
        Ok(B().Contains("A hoard lies hidden in Ostreval: the Miller's Hoard, 2 clue(s) deep. The first clue: \"Riddle number 1 for the seeker\""), "the first riddle is heralded", B());
        Ok(Logs.Any(l => l.StartsWith("event_started|The hunt for the Miller's Hoard begins")), "Chronicle: event_started");
        Ok(Refreshes.Contains("world"), "the painted world boards are told");
        DateTime end = (DateTime)F(Active(), "End");
        Ok(end == new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc) || Math.Abs((end - new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc)).TotalSeconds) < 10, "it runs its 60 minutes", end.ToString());
        Clock = end.AddSeconds(1);
        Clear();
        Tick();
        Ok(Active() == null && B().Contains("The hoard of the Miller's Hoard stays hidden."), "time runs out: the hoard stays hidden", B());
        Ok(Logs.Any(l => l.StartsWith("event_ended|The hoard of the Miller's Hoard stays hidden")), "Chronicle: event_ended");
        Clear();
        Tick(60);
        Ok(Active() == null, "an occurrence runs once");
        // A treasure slot with no hunt laid out is skipped, with a word to staff.
        Reset();
        NewWorld();
        ada = Staff();
        Clock = new DateTime(2026, 10, 6, 18, 59, 58, DateTimeKind.Utc);
        Clear();
        Tick();
        Ok(Active() == null && ada.All().Contains("Treasure Hunt is skipped this time: no treasure hunt is laid out"), "no hunt: skipped, staff told", ada.All());
        string r = Cmd(ada, "world");
        Ok(r.Contains("The realm is quiet just now.") && r.Contains("Coming in the living world (UTC):"), "/world: quiet, and what comes", r);
        r = Cmd(ada, "world", "schedule");
        Ok(r.Contains("the Merchant Caravan") && r.Contains("a Wandering Legend") && r.Contains("the Census"), "/world schedule lists the kinds", r);
        // The census slot is an instant event (Monday 18:00).
        Clock = new DateTime(2026, 10, 12, 17, 59, 58, DateTimeKind.Utc);
        Clear();
        Tick();
        Ok(B().Contains("The census of Ostreval, 12 October:"), "the census is taken on Monday 18:00", B());
    }

    static void ClashWithRealmEvents()
    {
        // A RealmEvents event running at the hour: the hunt waits until it ends (plus the buffer), then starts.
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "old-mill", "the Miller's Hoard", 100, 0);
        var t19 = new DateTime(2026, 10, 6, 19, 0, 0, DateTimeKind.Utc);
        SetEventsActive("tournament", t19.AddMinutes(-30), t19.AddMinutes(20));
        Clock = t19.AddMinutes(-31);
        Clear();
        Tick();
        Ok(B().Length == 0, "no countdown herald while the hour belongs to RealmEvents", B());
        Clock = t19.AddSeconds(-2);
        Tick();
        Ok(Active() == null && ada.All().Contains("Treasure Hunt waits: the Royal Tournament holds the realm. It starts at Tue 19:50 UTC"), "it waits for the tournament and its buffer", ada.All());
        Ok(((IDictionary)D("Postponed")).Count == 1, "the occurrence is remembered as put off");
        Clock = t19.AddMinutes(25);
        EventsActive.Clear();
        Tick();
        Ok(Active() == null, "not before the buffer is over");
        Clock = t19.AddMinutes(50);
        Tick();
        Ok(ActiveKind() == "treasure", "then it starts");
        DateTime end = (DateTime)F(Active(), "End");
        Ok(Math.Abs((end - t19.AddMinutes(110)).TotalMinutes) < 1, "for its full length", end.ToString());
        // The next RealmEvents event starts too soon: skipped, no promise heralded.
        Reset();
        NewWorld();
        ada = Staff();
        LayHunt(ada, "old-mill", "the Miller's Hoard", 100, 0);
        EventsNext = t19.AddMinutes(45);
        Clock = t19.AddMinutes(-30);
        Clear();
        Tick();
        Ok(B().Length == 0 && T.Logged.Any(l => l.Contains("No countdown for treasure-tuesday")), "a hunt that would meet Crown Night gets no countdown", string.Join("\n", T.Logged));
        Clock = t19.AddSeconds(-2);
        Tick();
        Ok(Active() == null && ada.All().Contains("Treasure Hunt is skipped this time: Crown Night"), "it is skipped and staff are told", ada.All());
        // A wait longer than MaxPostponeMinutes: skipped.
        Reset();
        NewWorld();
        ada = Staff();
        LayHunt(ada, "old-mill", "the Miller's Hoard", 100, 0);
        SetEventsActive("truce", t19.AddMinutes(-60), t19.AddMinutes(240));
        Clock = t19.AddSeconds(-2);
        Clear();
        Tick();
        Ok(Active() == null && ada.All().Contains("is skipped this time: the Truce of the Realm"), "a wait past MaxPostponeMinutes skips it", ada.All());
        // Admin start: refused over a RealmEvents event unless forced.
        Clock = t19.AddMinutes(10);
        string r = AdminCmd(ada, "start", "treasure");
        Ok(r.Contains("That would meet the Truce of the Realm (RealmEvents). Add 'force'"), "admin start respects RealmEvents", r);
        r = AdminCmd(ada, "start", "treasure", "old-mill", "30", "force");
        Ok(ActiveKind() == "treasure" && r.Contains("Treasure Hunt starts now, for 30 min."), "force starts it anyway", r);
        r = AdminCmd(ada, "start", "legend", "force");
        Ok(r.Contains("It cannot start: Treasure Hunt is already under way."), "one timed world event at a time", r);
        r = AdminCmd(ada, "stop");
        Ok(Active() == null && r.Contains("Treasure Hunt is called off."), "admin stop", r);
        var bob = Mk(Bb, "Bob");
        Ok(AdminCmd(bob, "start", "treasure").Contains("You may not do that."), "players cannot run admin commands");
        // A census waits while something runs, then is taken.
        Reset();
        NewWorld();
        ada = Staff();
        var mon18 = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
        SetEventsActive("crown_night", mon18.AddMinutes(-10), mon18.AddMinutes(30));
        Clock = mon18.AddSeconds(-2);
        Clear();
        Tick();
        Ok(!B().Contains("census"), "the census waits while an event runs");
        EventsActive.Clear();
        Tick();
        Ok(B().Contains("The census of Ostreval"), "and is taken when the realm is quiet", B());
    }

    static void TreasureLayout()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        string r = AdminCmd(ada, "hunt", "new", "old-mill", "the", "Miller's", "Hoard");
        Ok(r.Contains("Hunt the Miller's Hoard (old-mill) begun."), "hunt new", r);
        Ok(AdminCmd(ada, "hunt", "new", "old-mill", "x").Contains("exists already"), "no two hunts with one id");
        Ok(AdminCmd(ada, "hunt", "step", "old-mill", "hi").Contains("A riddle needs a few words."), "a riddle needs words");
        At(ada, 50, 60);
        r = AdminCmd(ada, "hunt", "step", "old-mill", "Where", "the", "{0}", "[FF0000]wheel", "turns");
        Ok(r.Contains("Place 1 of the Miller's Hoard set here at 50,10,60."), "a place where the admin stands", r);
        var h = ((IDictionary)D("Hunts"))["old-mill"];
        Ok((string)F(((IList)F(h, "Steps"))[0], "Riddle") == "Where the 0 FF0000wheel turns", "braces and colour tags are stripped from riddles", (string)F(((IList)F(h, "Steps"))[0], "Riddle"));
        At(ada, 300, 60);
        AdminCmd(ada, "hunt", "step", "old-mill", "Under", "the", "old", "stones");
        Ok(AdminCmd(ada, "hunt", "radius", "old-mill", "2", "12").Contains("Done: old-mill place 2 radius 12"), "a place's radius");
        Ok(AdminCmd(ada, "hunt", "radius", "old-mill", "5", "12").Contains("Usage"), "a radius for a missing place is refused");
        Ok(AdminCmd(ada, "hunt", "reward", "old-mill", "marks", "500").Contains("500 marks"), "a hunt's own marks");
        Ok(AdminCmd(ada, "hunt", "reward", "old-mill", "item", "Iron", "Ingot", "3").Contains("3 Iron Ingot"), "a hunt's own items (checked against the item list)");
        Ok(AdminCmd(ada, "hunt", "reward", "old-mill", "item", "Unicorn", "3").Contains("is not an item"), "an unknown item is refused");
        r = AdminCmd(ada, "hunt", "show", "old-mill");
        Ok(r.Contains("2 place(s)") && r.Contains("1. 50,10,60 r8: Where the 0 FF0000wheel turns") && r.Contains("2. 300,10,60 r12"), "hunt show", r);
        Ok(AdminCmd(ada, "hunt", "chest", "old-mill").Contains("No chest within 5 m."), "no chest near: refused");
        var chest = Chest(302, 61);
        At(ada, 301, 60);
        r = AdminCmd(ada, "hunt", "chest", "old-mill");
        Ok(r.Contains("The chest at 302,10,61 is bound to the Miller's Hoard") && !r.Contains("not at the dig site"), "a chest at the dig site is bound", r);
        r = AdminCmd(ada, "hunt", "list");
        Ok(r.Contains("old-mill 'the Miller's Hoard' 2 place(s), chest"), "hunt list", r);
        r = AdminCmd(ada, "hunt", "remove", "old-mill", "confirm");
        Ok(r.Contains("This removes the Miller's Hoard for good") && ((IDictionary)D("Hunts")).Contains("old-mill"), "remove asks first", r);
        r = AdminCmd(ada, "hunt", "remove", "old-mill", "confirm");
        Ok(!((IDictionary)D("Hunts")).Contains("old-mill"), "then removes", r);
        Ok(Cmd(Mk(Bb, "Bob"), "world", "admin", "hunt", "list").Contains("You may not do that."), "players cannot lay hunts");
    }

    static void TreasureHunt()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "old-mill", "the Miller's Hoard", 100, 0, 200, 0, 300, 0);
        var bob = Mk(Bb, "Bob", -50, 0, "Varrow");
        var cat = Mk(C, "Cat", -50, 0, "Ashgrove");
        AdminCmd(ada, "start", "treasure", "old-mill");
        Clear();
        string r = Cmd(bob, "treasure");
        Ok(r.Contains("The hunt for the Miller's Hoard: you have found 0 of 3 place(s).") && r.Contains("Clue 1: \"Riddle number 1 for the seeker\""), "/treasure shows your clue", r);
        At(bob, 200, 0);                                                // the second place first: nothing
        int had = bob.Messages.Count;
        Tick();
        Ok(bob.Messages.Count == had, "places count only in order");
        At(bob, 104, 3);
        Tick();
        Ok(bob.All().Contains("You found place 1 of 3! The next clue: \"Riddle number 2 for the seeker\"") && bob.Popups.Count == 1, "the next riddle, in chat and a window", bob.All());
        Ok(QuestEvents.Contains(Bb + "|event|treasure_step"), "RealmQuests hears of the first place");
        At(bob, 200, 0);
        Tick();
        Ok(!bob.All().Contains("place 2 of 3"), "too soon after the last place (MinSecondsBetweenSteps): not counted");
        Ok(ada.All().Contains("Bob reached place 2 of the Miller's Hoard faster than anyone walks"), "staff are told", ada.All());
        Tick(20);
        Ok(bob.All().Contains("You found place 2 of 3!"), "counted once the time has passed");
        At(ada, 300, 0);                                                 // staff stand on the dig site: they win nothing
        At(cat, 300, 0);
        Tick(30);
        Ok(ActiveKind() == "treasure", "a player who skipped the clues wins nothing at the dig site");
        Ok(Cmd(ada, "treasure").Contains("Staff run the living world"), "staff are reminded they win nothing");
        At(bob, 301, 1);
        Clear();
        Tick();
        Ok(Active() == null, "the first to the dig site ends the hunt");
        Ok(B().Contains("Bob of House [C58FC0]Varrow[FFFFFF] has found the hoard of the Miller's Hoard!"), "the herald names the finder", B());
        Ok(Rewards.Contains(Bb + "|150|world:treasure") && Purse(bob) == 150, "150 marks through RealmTreasury.RewardMarks");
        Ok(Count(bob, "IronIngot") == 10 && Count(bob, "Bread") == 5, "the items go to the packs (no chest bound)");
        Ok(Deeds.Any(d => d.StartsWith(Bb + "|Bob|treasure_found|")), "RealmRenown deed treasure_found", string.Join("\n", Deeds));
        Ok(Awards.Contains("Varrow|15|Bob found the Miller's Hoard"), "15 season points for the finder's house");
        Ok(Logs.Any(l => l.StartsWith("event_ended|Bob finds the hoard of the Miller's Hoard|Bob of House Varrow followed every clue")), "Chronicle: the find", string.Join("\n", Logs));
        Ok(ItemSources.Contains(Bb + "|30"), "RealmSentinel is told of the items");
        Ok(bob.All().Contains("The hoard of the Miller's Hoard is yours: 150 marks; the goods are in your packs."), "the finder is told", bob.All());
        var hunt = ((IDictionary)D("Hunts"))["old-mill"];
        Ok((int)F(hunt, "Runs") == 1 && (int)F(hunt, "Found") == 1, "the hunt keeps its count");
        Ok(QuestEvents.Contains(Bb + "|event|treasure_hunt"), "RealmQuests hears of the find");
        Ok(((IList)D("History")).Cast<string>().Any(l => l.Contains("Bob found the hoard of the Miller's Hoard")), "/world history remembers");
    }

    static void TreasureChest()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        var chest = Chest(101, 1);
        At(ada, 100, 0);
        AdminCmd(ada, "hunt", "chest", "crypt");
        At(ada, 0, 0);
        var bob = Mk(Bb, "Bob", 100, 0, "Varrow");
        var cat = Mk(C, "Cat", 0, 0, "Ashgrove");
        AdminCmd(ada, "start", "treasure", "crypt");
        Clear();
        Tick();
        Ok(Active() == null && ChestCount(chest, "IronIngot") == 10 && ChestCount(chest, "Bread") == 5, "the prize goes into the bound chest");
        Ok(Count(bob, "IronIngot") == 0 && bob.All().Contains("the goods are in the chest at the dig site, opened to you alone for a while"), "not into the packs", bob.All());
        Ok(!Use(cat, chest) && cat.All().Contains("This chest belongs to the treasure's finder for 10 min more."), "nobody else may open it", cat.All());
        Ok(Use(bob, chest), "the finder may");
        Ok(Use(ada, chest), "staff may");
        var other = Chest(500, 500);
        Ok(Use(cat, other), "other chests are not touched");
        Ok(ItemSources.Contains(Bb + "|120"), "RealmSentinel is told (two minutes)");
        Clock = Clock.AddMinutes(11);
        Tick();
        Ok(Use(cat, chest) && D("Lock") == null, "after ChestLockMinutes it is anyone's");
        // A chest that cannot be found: the packs take the prize.
        Reset();
        NewWorld();
        ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        chest = Chest(101, 1);
        At(ada, 100, 0);
        AdminCmd(ada, "hunt", "chest", "crypt");
        Entity.World.Remove(chest);
        bob = Mk(Bb, "Bob", 100, 0);
        AdminCmd(ada, "start", "treasure", "crypt");
        Tick();
        Ok(Count(bob, "IronIngot") == 10 && T.Logged.Any(l => l.Contains("the bound chest was not found")), "a missing chest: the packs take the prize");
        // A full chest: what does not fit goes to the packs.
        Reset();
        NewWorld();
        ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        chest = Chest(101, 1);
        chest.TryGet<CodeHatch.ItemContainer.InteractableContainer>().Contents.Capacity = 12;
        At(ada, 100, 0);
        AdminCmd(ada, "hunt", "chest", "crypt");
        bob = Mk(Bb, "Bob", 100, 0);
        AdminCmd(ada, "start", "treasure", "crypt");
        Tick();
        Ok(ChestCount(chest, "IronIngot") == 10 && ChestCount(chest, "Bread") == 2 && Count(bob, "Bread") == 3, "a full chest: the rest goes to the packs");
    }

    static void TreasureHints()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "old-mill", "the Miller's Hoard", 0, 600, 100, 0);
        var bob = Mk(Bb, "Bob", 0, 0);
        Ok(Cmd(bob, "treasure", "hint").Contains("No treasure hunt is under way"), "no hunt, no hint");
        AdminCmd(ada, "start", "treasure", "old-mill");
        string r = Cmd(bob, "treasure", "hint");
        Ok(r.Contains("The clues must be tried first. A hint can be had in 15 min."), "hints only after HintAfterMinutes", r);
        Clock = Clock.AddMinutes(15);
        r = Cmd(bob, "treasure", "hint");
        Ok(r.Contains("A hint for clue 1: look north, some way off."), "a direction and a distance band", r);
        Ok(Cmd(bob, "treasure", "hint").Contains("You have had your hint for this clue."), "one hint per clue");
        Reload(c => Section(c, "General", "NorthIsPositiveZ", false));
        bob.Messages.Clear();
        Ok(ActiveKind() == "treasure", "a reload keeps the running hunt");
        At(bob, 0, 600);
        Tick();
        Ok(bob.All().Contains("You found place 1 of 2"), "progress continues after a reload", bob.All());
        At(bob, 0, 0);
        Tick(30);
        r = Cmd(bob, "treasure", "hint");
        Ok(r.Contains("look east, a short walk away"), "the next clue has its own hint (and the map's north is configurable)", r);
    }

    static void BloodMoon()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        var bob = Mk(Bb, "Bob", 0, 0, "Varrow");
        var cat = Mk(C, "Cat", 0, 0, "Ashgrove");
        var dan = Mk(Dd, "Dan", 0, 0, "Corvane");
        var eve = Mk(E, "Eve", 0, 0, "Varrow");
        var gus = Mk(G, "Gus", 0, 0, "Merrin");
        var hal = Mk(H, "Hal", 0, 0, "Dunmere");
        var thu20 = new DateTime(2026, 10, 8, 20, 0, 0, DateTimeKind.Utc);
        Clock = thu20.AddMinutes(-60);
        Clear();
        Tick(10);
        Ok(B().Contains("The moon rises red in 60 min. For 120 min every fair kill is remembered"), "the Blood Moon warns at 60 min", B());
        Clock = thu20.AddSeconds(-2);
        Clear();
        Tick();
        Ok(ActiveKind() == "bloodmoon" && B().Contains("The Blood Moon rises! For 120 min"), "it rises Thursday 20:00 (every other week)", B());
        Ok(T.Logged.Any(l => l.Contains("Set-Mood.ps1 -Mood blood-moon")), "the log reminds the owner of the mood preset");
        Ok((bool)Inv(T, "IsBloodMoon"), "IsBloodMoon for other plugins");
        Clear();
        Kill(bob, cat);
        Ok(Deeds.Count == 1 && Deeds[0].StartsWith(Bb + "|Bob|blood_moon_kill|slew Cat under the Blood Moon"), "a fair kill earns the deed", string.Join("\n", Deeds));
        Ok(bob.All().Contains("Cat falls under the Blood Moon. Your kills tonight: 1."), "the killer is told");
        Kill(bob, cat);
        Ok(Deeds.Count == 1, "the same victim twice: once");
        Kill(bob, eve);
        Ok(Deeds.Count == 1, "a housemate: no");
        Liege["Corvane"] = "Varrow";
        Kill(bob, dan);
        Ok(Deeds.Count == 1, "a vassal: no");
        Liege.Clear();
        Treaties.Add(Pair("Varrow", "Merrin"));
        Kill(bob, gus);
        Ok(Deeds.Count == 1, "a treaty partner: no");
        Treaties.Clear();
        Protected.Add(H);
        Kill(bob, hal);
        Ok(Deeds.Count == 1, "a protected newcomer: no");
        Protected.Clear();
        Truce = true;
        Kill(bob, hal);
        Ok(Deeds.Count == 1, "during a truce: no");
        Truce = false;
        Offline(hal);
        Kill(bob, hal);
        Ok(Deeds.Count == 1, "an offline body: no");
        Online(hal);
        Kill(ada, hal);
        Ok(Deeds.Count == 1, "staff: no");
        Kill(bob, hal);
        Kill(bob, dan);
        Kill(bob, gus);
        Ok(Deeds.Count == 4, "three more fair kills");
        // Fed cap: Cat dies to three different killers; the fourth does not count.
        Kill(dan, cat); Kill(gus, cat);
        int before = Deeds.Count;
        Kill(hal, cat);
        Ok(Deeds.Count == before, "one victim feeds at most MaxDeathsFedPerVictim (3) kills");
        // Deed cap per killer (5): more kills still count for the house, without a deed.
        Kill(bob, eve);
        var j = Mk(J, "Jon", 0, 0, "Halloran");
        Kill(bob, j);
        Mk(76561190000000011, "Kit", 0, 0, "Halloran");
        Kill(bob, Everyone.Last());
        Ok(Deeds.Count(d => d.StartsWith(Bb + "|")) == 5, "at most KillDeedsPerPlayer deeds", string.Join("\n", Deeds));
        // Beasts bite harder.
        var wolf = Creature("wolf_grey(Clone)", 5, 5);
        Ok(Math.Abs(Bite(wolf, cat, 10f) - 12.5f) < 0.01f, "beast blows are multiplied (1.25)");
        Ok(Math.Abs(Hit(bob, cat.Entity, 10f) - 10f) < 0.01f, "player blows are not");
        // NightOnly: day kills do not count.
        Set("BloodMoon", "NightOnly", true);
        GameClock.Instance.CurrentTimeBlock = GameClock.TimeBlock.Morning;
        before = Deeds.Count;
        var cat2 = Mk(76561190000000012, "Cid", 0, 0, "Ashgrove");
        Kill(dan, cat2);
        Ok(Deeds.Count == before, "NightOnly: the game's clock says morning, the kill does not count");
        GameClock.Instance = null;
        Kill(dan, cat2);
        Ok(Deeds.Count == before + 1, "a clock that cannot be read counts as night");
        Set("BloodMoon", "NightOnly", false);
        string r = Cmd(bob, "world", "bloodmoon");
        Ok(r.Contains("Your counted kills: 6 (renown for the first 5).") && r.Contains("House [C58FC0]Varrow[FFFFFF]: 6"), "/world bloodmoon", r);
        Clock = thu20.AddMinutes(121);
        Clear();
        Tick();
        Ok(Active() == null && B().Contains("The Blood Moon sets.") && B().Contains("The deadliest: Bob (6).") && B().Contains("House [C58FC0]Varrow[FFFFFF] took the night (6)."), "it sets with the night's deadliest", B());
        Ok(Awards.Contains("Varrow|20|Deadliest house of the Blood Moon") && Rewards.Contains(Bb + "|60|world:bloodmoon"), "season points and the slayer's marks");
        Ok(Logs.Any(l => l.StartsWith("event_ended|The Blood Moon sets|")), "Chronicle entry");
        Ok(Math.Abs(Bite(wolf, cat, 10f) - 10f) < 0.01f, "after it sets, beasts bite as before");
    }

    static Player CaravanWorld(out Player ada)
    {
        Reset();
        NewWorld();
        ada = Staff();
        Place(ada, "kingsreach", 0, 0, "Kingsreach");
        Place(ada, "greywater", 1000, 0, "Greywater");
        string r = AdminCmd(ada, "route", "add", "grain-road", "kingsreach", "greywater", "The", "Grain", "Road");
        Ok(r.Contains("Route grain-road: Kingsreach - Greywater."), "a route between two places", r);
        Clear();
        return ada;
    }

    static void Caravan()
    {
        Player ada;
        CaravanWorld(out ada);
        Ok(AdminCmd(ada, "route", "add", "x1", "kingsreach", "nowhere").Contains("'nowhere' is not a place"), "a route to an unknown place is refused");
        Ok(AdminCmd(ada, "route", "add", "x1", "kingsreach", "kingsreach").Contains("two different places"), "a route needs two places");
        var bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        var cat = Mk(C, "Cat", 8, 0, "Varrow");
        var dan = Mk(Dd, "Dan", 3, 3, "Corvane");
        var eve = Mk(E, "Eve", 500, 500, "Ashgrove");
        var gus = Mk(G, "Gus", 2, 2, "Merrin");
        Ok(Cmd(bob, "caravan", "carry").Contains("No caravan is on the road."), "no caravan, nothing to carry");
        AdminCmd(ada, "start", "caravan");
        Ok(B().Contains("A merchant caravan musters at Kingsreach, bound for Greywater (1000 m). It leaves in 10 min."), "the muster is heralded", B());
        Protected.Add(G);
        Ok(Cmd(gus, "caravan", "carry").Contains("Newcomers under protection cannot bear the caravan"), "a protected newcomer cannot bear it");
        Ok(Cmd(eve, "caravan", "carry").Contains("Come to Kingsreach (within 20 m)"), "the bearer must stand at the start");
        Ok(Cmd(ada, "caravan", "carry").Contains("Staff run the living world"), "staff cannot bear it");
        string r = Cmd(bob, "caravan", "carry");
        Ok(r.Contains("You bear the caravan to Greywater.") && B().Contains("Bob of House [C58FC0]Varrow[FFFFFF] takes up the merchant's goods"), "Bob bears it", r);
        Ok(Cmd(cat, "caravan", "carry").Contains("Bob already bears the goods."), "one bearer");
        Ok(Cmd(cat, "caravan", "escort").Contains("You guard the caravan to Greywater."), "Cat escorts");
        Ok(bob.All().Contains("Cat rides with you as an escort."), "the bearer hears of it");
        Ok(Cmd(dan, "caravan", "escort").Contains("You guard the caravan"), "Dan escorts");
        Ok(Cmd(eve, "caravan", "escort").Contains("Join the caravan at Kingsreach"), "an escort must be near");
        Ok(Cmd(bob, "caravan", "escort").Contains("You bear the caravan; you cannot guard it as well."), "the bearer is no escort");
        Ok(Cmd(cat, "caravan").Contains("A caravan musters at Kingsreach for Greywater. Bearer: Bob, escorts: 2."), "/caravan status");
        Ok(!(bool)Inv(T, "IsCaravanBearer", Bb.ToString()), "IsCaravanBearer is false during the muster");
        Clear();
        Minutes(10);
        Ok(B().Contains("The caravan sets out from Kingsreach, borne by Bob with 2 escort(s). It must reach Greywater within 35 min."), "it sets out", B());
        Ok((bool)Inv(T, "IsCaravanBearer", Bb.ToString()), "IsCaravanBearer while on the road");
        Ok(Cmd(gus, "caravan", "carry").Contains("The caravan has left"), "no new bearer on the road");
        Travelling.Add(Bb.ToString());
        Tick();
        Ok(Cancelled.Contains(Bb.ToString()) && bob.All().Contains("The waystones will not carry the merchant's goods. Walk."), "a waystone journey is called off (RealmTravel.CancelJourney)");
        // Walk 1000 m in 100 m steps every 5 s; Cat keeps close, Dan wanders off after the start.
        Clear();
        for (int x = 100; x <= 1000; x += 100)
        {
            At(bob, x, 0); At(cat, x + 10, 0);
            At(dan, x > 200 ? 5000 : x, 0);
            if (x == 500) Minutes(5); else Tick();
        }
        Ok(B().Contains("The caravan was seen"), "sightings are heralded on the road", B());
        Ok(Active() == null, "the caravan arrives");
        Ok(B().Contains("The caravan reaches Greywater! Bob of House [C58FC0]Varrow[FFFFFF] brought it home with 1 escort(s)."), "the arrival is heralded", B());
        Ok(Rewards.Contains(Bb + "|120|world:caravan") && Rewards.Contains(C + "|180|world:caravan"), "the bearer takes 40%, the close escort the rest", string.Join(",", Rewards));
        Ok(!Rewards.Any(x => x.StartsWith(Dd.ToString())) && dan.All().Contains("You did not keep close enough to the bearer"), "a straying escort is not paid", dan.All());
        Ok(Deeds.Count(d => d.Contains("|caravan_escort|")) == 2 && Awards.Contains("Varrow|15|Brought the merchant caravan to Greywater"), "deeds and the bearer's house points");
        Ok(Logs.Any(l => l.StartsWith("event_ended|The caravan reaches Greywater|Borne from Kingsreach by Bob, with 1 sword(s)")), "Chronicle entry", string.Join("\n", Logs));
        Ok(QuestEvents.Contains(Bb + "|event|caravan") && QuestEvents.Contains(C + "|event|caravan"), "RealmQuests hears of it");
        // A bearer who leaves during the muster frees the goods; a bearer who dies during the muster too.
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        cat = Mk(C, "Cat", 8, 0, "Ashgrove");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Clear();
        Ok(Cmd(bob, "caravan", "leave") == "" && B().Contains("The caravan at Kingsreach needs a new bearer"), "the bearer may step down before it leaves");
        Cmd(cat, "caravan", "carry");
        Clear();
        Kill(bob, cat);
        Ok(B().Contains("needs a new bearer") && ActiveKind() == "caravan" && Rewards.Count == 0, "a bearer slain at the muster: no plunder, a new bearer is wanted", B());
        Clear();
        Minutes(10);
        Ok(Active() == null && B().Contains("The caravan at Kingsreach finds no bearer and stays home."), "no bearer: it stays home", B());
    }

    static void CaravanPlunder()
    {
        Player ada;
        CaravanWorld(out ada);
        var bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        var cat = Mk(C, "Cat", 8, 0, "Varrow");
        var rex = Mk(Dd, "Rex", 300, 0, "Corvane");
        var eve = Mk(E, "Eve", 300, 0, "Ashgrove");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Cmd(cat, "caravan", "escort");
        Minutes(10);
        At(bob, 100, 0);
        Tick();
        Clear();
        Kill(rex, bob);
        Ok(Active() == null, "a fair kill of the bearer ends the caravan");
        Ok(B().Contains("Rex of House [8FB0BF]Corvane[FFFFFF] has plundered the merchant caravan, cutting down its bearer Bob! The merchants pay 100 marks to whoever brings the raider down within 2 h."), "the plunder is heralded with the bounty", B());
        Ok(Rewards.Contains(Dd + "|120|world:caravan-raid"), "the raider takes 40% of the purse");
        Ok(Deeds.Any(d => d.StartsWith(Dd + "|Rex|caravan_raid|")), "the raider's infamous deed");
        Ok(((IDictionary)D("Bounties")).Contains(Dd.ToString()), "a bounty is on the raider");
        Ok(Logs.Any(l => l.StartsWith("event_ended|Rex plunders the merchant caravan|")), "Chronicle entry");
        // The plundered bearer cannot collect; the raider's own allies cannot; a fair hunter can, once.
        Clear();
        Kill(bob, rex);
        Ok(bob.All().Contains("The merchants do not pay the bearer they lost") && ((IDictionary)D("Bounties")).Contains(Dd.ToString()), "the bearer cannot take the price");
        var ally = Mk(G, "Gil", 300, 0, "Corvane");
        Kill(ally, rex);
        Ok(((IDictionary)D("Bounties")).Contains(Dd.ToString()) && !Rewards.Any(r => r.StartsWith(G.ToString())), "a housemate of the raider cannot take it");
        Ok(Cmd(rex, "caravan", "carry").Contains("No caravan"), "(no caravan now)");
        Kill(eve, rex);
        Ok(!((IDictionary)D("Bounties")).Contains(Dd.ToString()) && Rewards.Contains(E + "|100|world:raider-bounty"), "a fair hunter takes the price");
        Ok(B().Contains("Eve has brought down Rex, the raider of the caravan"), "heralded", B());
        Kill(cat, rex);
        Ok(!Rewards.Any(r => r.StartsWith(C + "|")), "the price is paid once");
        // A raider with a price cannot bear or escort; the price lapses.
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        rex = Mk(Dd, "Rex", 5, 0, "Corvane");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Minutes(10);
        Kill(rex, bob);
        AdminCmd(ada, "start", "caravan");
        At(rex, 3, 0);
        Ok(Cmd(rex, "caravan", "carry").Contains("raider with a price on their head") && Cmd(rex, "caravan", "escort").Contains("raider"), "a wanted raider is trusted with nothing");
        AdminCmd(ada, "stop");
        Clear();
        Clock = Clock.AddHours(2).AddMinutes(1);
        Tick();
        Ok(((IDictionary)D("Bounties")).Count == 0 && B().Contains("The merchants' price on Rex lapses."), "the price lapses after RaiderBountyHours", B());
        // A housemate or ally of the bearer only loses the caravan.
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        cat = Mk(C, "Cat", 5, 0, "Varrow");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Minutes(10);
        Clear();
        Kill(cat, bob);
        Ok(Active() == null && Rewards.Count == 0 && ((IDictionary)D("Bounties")).Count == 0 && B().Contains("cut down by friends"), "a housemate's blow: nobody profits", B());
        // An escort who turns on the bearer is a raider (and is paid no escort share).
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        var tom = Mk(J, "Tom", 5, 0, "Ashgrove");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Cmd(tom, "caravan", "escort");
        Minutes(10);
        Kill(tom, bob);
        Ok(Rewards.Count == 1 && Rewards[0].StartsWith(J + "|120|world:caravan-raid"), "a treacherous escort is paid as a raider only");
    }

    static void CaravanFailures()
    {
        Player ada;
        CaravanWorld(out ada);
        var bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Minutes(10);
        At(bob, 50, 0); Tick();
        Clear();
        At(bob, 900, 0); Tick();                                       // 850 m in one tick
        Ok(Active() == null && B().Contains("vanished from the road with Bob"), "a jump of more than MaxJumpMetres loses the caravan", B());
        Ok(Rewards.Count == 0, "nothing is paid");
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Minutes(10);
        Offline(bob);
        Tick(30);
        Ok(ActiveKind() == "caravan", "a short disconnect is forgiven");
        Online(bob);
        Tick();
        Offline(bob);
        Clear();
        Tick(65);
        Ok(Active() == null && B().Contains("The bearer Bob has left the road"), "offline past OfflineGraceSeconds: deserted", B());
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Minutes(10);
        var wolf = Creature("wolf(Clone)", 6, 0);
        Clear();
        KillBy(wolf, bob);
        Ok(Active() == null && B().Contains("The bearer Bob fell on the road to Greywater"), "a beast's kill scatters the goods", B());
        CaravanWorld(out ada);
        bob = Mk(Bb, "Bob", 5, 0, "Varrow");
        AdminCmd(ada, "start", "caravan");
        Cmd(bob, "caravan", "carry");
        Clear();
        Minutes(50);
        Ok(Active() == null && B().Contains("the caravan never reaches Greywater"), "time runs out", B());
        CaravanWorld(out ada);
        Ok(AdminCmd(ada, "route", "remove", "grain-road").Contains("route grain-road removed"), "route remove");
        Ok(AdminCmd(ada, "start", "caravan").Contains("no caravan route is set"), "no route: it cannot start");
    }

    static void CaravanWaystones()
    {
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmTravel.json"), "{ \"Format\": 1, \"Waystones\": { \"kingsreach\": { \"Id\": \"kingsreach\", \"Name\": \"Kingsreach\", \"X\": 0, \"Y\": 10, \"Z\": 0, \"Radius\": 10, \"Enabled\": true }, "
            + "\"old-mill\": { \"Id\": \"old-mill\", \"Name\": \"Old Mill\", \"X\": 0, \"Y\": 10, \"Z\": 800, \"Radius\": 10, \"Enabled\": true } }, \"Players\": {} }");
        NewWorld();
        var ada = Staff();
        string r = AdminCmd(ada, "route", "add", "mill-road", "waystone:kingsreach", "waystone:old-mill");
        Ok(r.Contains("Route mill-road: Kingsreach - Old Mill."), "a route between RealmTravel waystones (read from its data file)", r);
        Ok(AdminCmd(ada, "places").Contains("Waystones (RealmTravel): waystone:kingsreach, waystone:old-mill"), "the waystones are listed for staff");
        string before = File.ReadAllText(Path.Combine(Dir, "RealmTravel.json"));
        var bob = Mk(Bb, "Bob", 3, 3);
        AdminCmd(ada, "start", "caravan");
        Ok(B().Contains("musters at Kingsreach, bound for Old Mill (800 m)"), "it musters at the waystone", B());
        Cmd(bob, "caravan", "carry");
        Minutes(10);
        for (int z = 100; z <= 800; z += 100) { At(bob, 0, z); Tick(); }
        Ok(Active() == null && B().Contains("The caravan reaches Old Mill!"), "and arrives at the other", B());
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmTravel.json")) == before, "RealmTravel's file is never written");
    }

    static void LegendBound()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        Place(ada, "fenmarch", 1000, 1000, "the Fenmarch");
        var wolf = Creature("wolf_grey(Clone)", 1100, 1000);
        Creature("rabbit(Clone)", 1000, 1050);
        var bob = Mk(Bb, "Bob", 1090, 1000, "Varrow");
        var cat = Mk(C, "Cat", 1090, 1000, "Ashgrove");
        var dan = Mk(Dd, "Dan", 1090, 1000, "Corvane");
        string r = AdminCmd(ada, "start", "legend", "grey-widow");
        Ok(ActiveKind() == "legend" && (string)F(Sub("Legend"), "Mode") == "bound", "the Grey Widow is bound to a living wolf", r);
        Ok(B().Contains("the Grey Widow roams Ostreval! A she-wolf grey as ash") && B().Contains("It was last seen 100 m east of the Fenmarch."), "heralded with where it was seen", B());
        Ok(Logs.Any(l => l.StartsWith("event_started|The Grey Widow roams Ostreval")), "Chronicle entry");
        Ok(Math.Abs(Hit(bob, wolf, 40f) - 10f) < 0.01f, "the legend takes Toughness (4) times less damage");
        Hit(cat, wolf, 8f);
        Hit(dan, wolf, 1f);
        var other = Creature("wolf_black(Clone)", 0, 0);
        Ok(Math.Abs(Hit(bob, other, 40f) - 40f) < 0.01f, "other wolves do not");
        wolf.Position = new UnityEngine.Vector3(1000, 10, 1300);
        Clear();
        Minutes(10);
        Ok(B().Contains("the Grey Widow: It was last seen 300 m north of the Fenmarch."), "the hints follow it", B());
        Clear();
        Slay(dan, other);
        Ok(ActiveKind() == "legend", "another wolf's death is not the legend's");
        Slay(bob, wolf);
        Ok(Active() == null && B().Contains("Bob of House [C58FC0]Varrow[FFFFFF] has slain the Grey Widow!"), "the slayer is heralded", B());
        // Damage: Bob 40, Cat 8, Dan 1 of 49: Cat (16%) helps, Dan (2%) does not. 30% of 200 = 60 to helpers.
        Ok(Rewards.Contains(Bb + "|140|world:legend") && Rewards.Contains(C + "|60|world:legend") && !Rewards.Any(x => x.StartsWith(Dd.ToString())), "the slayer and the helpers share the marks", string.Join(",", Rewards));
        Ok(Count(bob, "WolfPelt") == 3 && Count(bob, "Fang") == 2, "the trophy goes to the slayer's packs");
        Ok(Deeds.Any(d => d.StartsWith(Bb + "|Bob|legend_slain|slew the Grey Widow")) && Awards.Contains("Varrow|20|Bob slew the Grey Widow"), "deed and season points");
        Ok(bob.Popups.Count == 1 && bob.Popups[0].StartsWith("A Legend Falls|"), "a window for the slayer");
        Ok(Logs.Any(l => l.StartsWith("event_ended|Bob slays the Grey Widow|")), "Chronicle entry");
        // A legend killed by staff or by no one: nobody is paid.
        Reset();
        NewWorld();
        ada = Staff();
        wolf = Creature("wolf(Clone)", 10, 0);
        AdminCmd(ada, "start", "legend", "grey-widow");
        Clear();
        Slay(ada, wolf);
        Ok(Active() == null && Rewards.Count == 0 && B().Contains("fell to the staff's own hand"), "staff kill it: nobody is rewarded", B());
        // A bound legend that vanishes is bound again.
        Reset();
        NewWorld();
        ada = Staff();
        wolf = Creature("wolf(Clone)", 10, 0);
        AdminCmd(ada, "start", "legend", "grey-widow");
        Entity.World.Remove(wolf);
        SetF(T, "legendEntity", null);
        var wolf2 = Creature("wolf(Clone)", 50, 0);
        Tick();
        Ok((ulong)F(Sub("Legend"), "ViewId") == wolf2.NetViewID, "a vanished legend is bound to another of its kind");
        Clear();
        Clock = ((DateTime)F(Active(), "End")).AddSeconds(1);
        Tick();
        Ok(Active() == null && B().Contains("The Grey Widow slips away into the wild."), "unslain at the end: it escapes", B());
    }

    static void LegendRegion()
    {
        Reset();
        NewWorld(c => { var l = ((IList)F(F(c, "Legends"), "Legends"))[1]; SetF(l, "Region", "deepwood"); SetF(l, "RegionRadius", 300f); });
        var ada = Staff();
        Ok(AdminCmd(ada, "start", "legend", "old-ironhide").Contains("the region 'deepwood' is not a known place"), "a legend's region must be a place");
        Place(ada, "deepwood", 2000, 0, "the Deepwood");
        var farBear = Creature("bear_brown(Clone)", 0, 0);
        AdminCmd(ada, "start", "legend", "old-ironhide");
        Ok((string)F(Sub("Legend"), "Mode") == "region", "no bear in the region: the hunt is for the first one slain there");
        Ok(B().Contains("It haunts the land within 300 m of the Deepwood: the first bear slain there is the one."), "the herald says so", B());
        var bob = Mk(Bb, "Bob", 0, 0, "Varrow");
        Ok(Math.Abs(Hit(bob, farBear, 40f) - 40f) < 0.01f, "region mode: no toughness");
        Slay(bob, farBear);
        Ok(ActiveKind() == "legend", "a bear slain outside the region is not the one");
        var bear = Creature("bear_black(Clone)", 2100, 50);
        Slay(bob, bear);
        Ok(Active() == null && Rewards.Contains(Bb + "|250|world:legend") && Count(bob, "BearHide") == 3, "the first bear slain in the region is Old Ironhide");
        // No creature list at all (the game call fails): still a hunt, anywhere.
        Reset();
        NewWorld();
        ada = Staff();
        Entity.ListThrows = true;
        AdminCmd(ada, "start", "legend", "pale-hart");
        Ok(ActiveKind() == "legend" && B().Contains("It could be anywhere: any deer/stag/hart may be the one."), "a failing creature list: the hunt is for any of the kind", B());
        Entity.ListThrows = false;
        Ok(Cmd(ada, "world", "legend").Contains("The Pale Hart: A white stag"), "/world legend");
        Ok(AdminCmd(ada, "creatures").Contains("creature(s) in the world"), "admin creatures lists kinds");
    }

    static void Festival()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        At(ada, 10, 10);
        ada.Entity.Forward = new UnityEngine.Vector3(1, 0, 0);
        string r = AdminCmd(ada, "deco", "add", "harvest", "house-varrow");
        Ok(r.Contains("house-varrow will be raised here for The Harvest Fair (decoration 1)."), "a decoration is marked where staff stand", r);
        Ok((int)F(((IList)((IDictionary)D("Decorations"))["harvest"])[0], "Turn") == 1, "facing east: quarter-turn 1 (RealmSculptor's rule)");
        AdminCmd(ada, "deco", "add", "harvest", "tournament-arch");
        Ok(AdminCmd(ada, "deco", "add", "nofest", "x1").Contains("Usage"), "an unknown festival is refused");
        Ok(AdminCmd(ada, "deco", "add", "harvest", "../evil").Contains("Usage"), "a path is not a sculpture id");
        var bob = Mk(Bb, "Bob", 0, 0, "Varrow");
        var cat = Mk(C, "Cat", 0, 0, "Varrow");
        var dan = Mk(Dd, "Dan", 0, 0, "Ashgrove");
        var eve = Mk(E, "Eve", 0, 0, "Ashgrove");
        var gus = Mk(G, "Gus", 0, 0);
        Ok(Cmd(bob, "festival").Contains("No festival is on. Next: The Harvest Fair on 22 September") || Cmd(bob, "festival").Contains("Next: Midwinter on 20 December"), "between festivals, the next one is named");
        Clock = new DateTime(2026, 12, 20, 11, 59, 58, DateTimeKind.Utc);
        Clear();
        Tick();
        Ok(D("Festival") != null && B().Contains("Midwinter begins and runs for 7 days!"), "Midwinter opens on its date", B());
        Ok(B().Contains("Points: Lumber 1, Fat 2, CookedMeat 3, WolfPelt 8, BearHide 10."), "the offerings are heralded", B());
        Ok(Placed.Count == 0, "Midwinter has no decorations marked");
        Ok(T.Logged.Any(l => l.Contains("Set-Mood.ps1 -Mood midwinter")), "the owner is reminded of the mood");
        AdminCmd(ada, "festival", "cancel");
        Ok(D("Festival") == null, "admin cancel");
        Clear();
        r = AdminCmd(ada, "festival", "start", "harvest", "3");
        Ok(D("Festival") != null && Placed.Count == 2 && Placed[0].StartsWith("100|house-varrow|1"), "the Harvest Fair opens and RealmSculptor raises its decorations", string.Join(",", Placed));
        Ok(r.Contains("harvest opened for 3 day(s)"), "admin start", r);
        Ok(Cmd(gus, "festival", "give").Contains("Only sworn members of a house can give"), "no house, no offering");
        Ok(Cmd(bob, "festival", "give").Contains("You carry nothing the festival takes."), "nothing to give");
        Give(bob, "Bread", 10); Give(bob, "Grain", 30); Give(bob, "Torch", 3);
        r = Cmd(bob, "festival", "give");
        Ok(r.Contains("You give 10 Bread: 50 point(s).") && r.Contains("You give 30 Grain: 60 point(s).") && r.Contains("House [C58FC0]Varrow[FFFFFF] now has 110 point(s) (+110)."), "goods are taken and counted", r);
        Ok(Count(bob, "Bread") == 0 && Count(bob, "Grain") == 0 && Count(bob, "Torch") == 3, "only offerings leave the packs");
        Give(bob, "Flour", 100);
        r = Cmd(bob, "festival", "give", "Flour");
        Ok(r.Contains("You give 30 Flour: 90 point(s).") && Count(bob, "Flour") == 70, "the daily cap takes only what it can count (200 points)", r);
        Ok(Cmd(bob, "festival", "give", "Flour").Contains("You have given all you can for today (200 points)."), "capped for today");
        Ok(Cmd(bob, "festival", "give", "Gold").Contains("'Gold' is not an offering here."), "a non-offering is refused");
        // Measured: a container that gives up less than asked counts only what left.
        Give(dan, "Apple", 20);
        dan.Inventory.Contents.SplitShort = 5;
        r = Cmd(dan, "festival", "give", "Apple");
        Ok(r.Contains("You give 15 Apple: 15 point(s).") && Count(dan, "Apple") == 5, "only what really left the packs counts", r);
        dan.Inventory.Contents.SplitShort = 0;
        Ok(Cmd(ada, "festival", "give").Contains("Staff run the living world"), "staff give nothing");
        r = Cmd(cat, "festival");
        Ok(r.Contains("The Harvest Fair: 3 days left.") && r.Contains("No house has placed yet") && r.Contains("Your house [C58FC0]Varrow[FFFFFF]: 200. Yours: 0"), "standings: a house with one giver does not place yet", r);
        Clock = Clock.AddDays(1);
        Give(bob, "Bread", 4);
        Ok(Cmd(bob, "festival", "give", "Bread").Contains("You give 4 Bread: 20 point(s)."), "a new day, a new cap");
        var board = (Dictionary<string, object>)Inv(T, "GetWorldBoard", "festival");
        Ok((string)board["title"] == "The Harvest Fair" && ((IList)board["rows"]).Count == 0, "the festival board (no house has two givers yet)");
    }

    static void FestivalClose()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        At(ada, 10, 10);
        AdminCmd(ada, "deco", "add", "midwinter", "heralds-pillar");
        var bob = Mk(Bb, "Bob", 0, 0, "Varrow");
        var cat = Mk(C, "Cat", 0, 0, "Varrow");
        var dan = Mk(Dd, "Dan", 0, 0, "Ashgrove");
        var eve = Mk(E, "Eve", 0, 0, "Ashgrove");
        var gus = Mk(G, "Gus", 0, 0, "Merrin");
        AdminCmd(ada, "festival", "start", "midwinter", "2");
        Ok(Placed.Count == 1, "Midwinter's decoration is raised");
        Give(bob, "BearHide", 5); Give(cat, "Lumber", 10); Give(dan, "WolfPelt", 10); Give(eve, "Fat", 5); Give(gus, "BearHide", 20);
        foreach (var p in new[] { bob, cat, dan, eve, gus }) Cmd(p, "festival", "give");
        // The festival hunt: wolves and bears for points; other beasts not.
        var wolf = Creature("wolf(Clone)", 0, 0);
        Clear();
        Slay(cat, wolf);
        Ok(cat.All().Contains("+10 for House [C58FC0]Varrow[FFFFFF] at the festival hunt."), "a wolf slain counts for the house", cat.All());
        Slay(cat, Creature("rabbit(Clone)", 0, 0));
        Ok(!cat.All().Contains("+10 for House [C58FC0]Varrow[FFFFFF] at the festival hunt.\n"), "a rabbit does not");
        // Varrow: 50 + 10 + 10 = 70; Ashgrove: 80 + 10 = 90; Merrin: 200 but one giver.
        SetEventsActive("tournament", Clock, Clock.AddDays(3).AddMinutes(30));
        Clock = Clock.AddDays(2).AddMinutes(1);
        Clear();
        Tick();
        Ok(D("Festival") != null, "the close waits while a RealmEvents event runs");
        EventsActive.Clear();
        Tick();
        Ok(D("Festival") == null, "then the festival closes");
        Ok(B().Contains("Midwinter ends! House [E08A5C]Ashgrove[FFFFFF] wins. Best giver: Dan."), "the winner and its best giver", B());
        Ok(B().Contains("  1. House [E08A5C]Ashgrove[FFFFFF]: 90") && B().Contains("  2. House [C58FC0]Varrow[FFFFFF]: 70") && !B().Contains("Merrin"), "the places (a house with one giver does not place)", B());
        Ok(Awards.Contains("Ashgrove|40|Midwinter: place 1") && Awards.Contains("Varrow|25|Midwinter: place 2"), "season points by place", string.Join(",", Awards));
        Ok(Rewards.Contains(Dd + "|100|world:festival") && Deeds.Any(d => d.StartsWith(Dd + "|Dan|festival_champion|")), "the best giver's marks and deed");
        Ok(Removed.SequenceEqual(new[] { "100" }), "RealmSculptor takes the decoration down");
        Ok(Logs.Any(l => l.StartsWith("event_ended|House Ashgrove wins Midwinter|Best giver: Dan (80 points).")), "Chronicle entry", string.Join("\n", Logs));
        // A sculptor that refuses: staff are warned, the festival runs anyway.
        Reset();
        NewWorld();
        ada = Staff();
        AdminCmd(ada, "deco", "add", "harvest", "house-merrin");
        SculptorRefuses = true;
        AdminCmd(ada, "festival", "start", "harvest");
        Ok(D("Festival") != null && T.Logged.Any(l => l.Contains("RealmSculptor did not raise house-merrin")), "a refused decoration is logged; the festival opens", string.Join("\n", T.Logged));
        Ok(AdminCmd(ada, "deco", "remove", "harvest", "1").Contains("Done"), "a decoration not standing may be removed");
        AdminCmd(ada, "festival", "stop");
        Ok(B().Contains("The Harvest Fair ends. No house gave enough to place."), "a festival nobody gave to", B());
    }

    static void Census()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        var bob = Mk(Bb, "Bob", 0, 0, "Varrow");
        var cat = Mk(C, "Cat", 0, 0, "Varrow");
        var dan = Mk(Dd, "Dan", 0, 0, "Ashgrove");
        King = "Bob"; KingHouse = "Varrow";
        Offline(dan);
        Online(dan);
        Ok(Cmd(bob, "world", "census").Contains("No census has been taken yet. The next: in"), "/world census before the first");
        ChronicleId = 25;
        Clear();
        string r = AdminCmd(ada, "census");
        string b = B();
        Ok(b.Contains("The census of Ostreval, 6 October:"), "the header", b);
        Ok(b.Contains("  4 soul(s) walked the realm this week (4 new), 4 at most at once."), "souls counted once each, newcomers, the peak", b);
        Ok(b.Contains("  2 house(s); the largest is House [C58FC0]Varrow[FFFFFF] with 2."), "houses and the largest", b);
        Ok(b.Contains("  Bob of House [C58FC0]Varrow[FFFFFF] sits upon the Old Throne."), "the crown", b);
        Ok(b.Contains("  House [C58FC0]Varrow[FFFFFF] leads the season with 140."), "the season's leader", b);
        Ok(b.Contains("  5000 marks struck in all; the crown holds 1200."), "the treasury", b);
        Ok(b.Contains("  15 new entries in the Chronicle."), "Chronicle entries since the week began", b);
        Ok(b.Contains("Hoards found: 0. Caravans home: 0, plundered: 0, lost: 0. Legends slain: 0. Blood Moon kills: 0."), "the week's world deeds", b);
        Ok(Logs.Any(l => l.StartsWith("census_taken|The census of Ostreval, 6 October 2026|4 soul(s) walked the realm")), "Chronicle: census_taken", string.Join("\n", Logs));
        Ok(!Logs.Any(l => l.Contains("76561190")), "counts only: no Steam id in the Chronicle");
        Ok(r.Contains("Done: census."), "admin census");
        r = Cmd(bob, "world", "census");
        Ok(r.Contains("The last census, 6 October:") && r.Contains("This week so far: 4 soul(s), 0 new"), "/world census shows the last one and this week so far", r);
        Ok(((List<string>)F(D("Week"), "Players")).Count == 4, "the new week starts with the players online");
        var board = (Dictionary<string, object>)Inv(T, "GetWorldBoard", "census");
        Ok((string)board["title"] == "The Census" && ((string)board["body"]).Contains("4 soul(s) walked the realm") && !((string)board["body"]).Contains("["), "the census board, plain text", (string)board["body"]);
    }

    static void OwedRewards()
    {
        // The treasury pays less than asked: the rest is owed and paid when the player next joins.
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        var bob = Mk(Bb, "Bob", 100, 0);
        RewardCap = 100;
        bob.Inventory.Contents.Capacity = 6;
        AdminCmd(ada, "start", "treasure", "crypt");
        Tick();
        Ok(Purse(bob) == 100 && OwedMarks(bob) == 50, "a short treasury: 100 paid, 50 owed");
        Ok(bob.All().Contains("100 marks now, 50 more owed"), "the finder is told", bob.All());
        Ok(Count(bob, "IronIngot") == 6 && OwedUnits(bob) == 9, "full packs: the rest of the items is owed", OwedUnits(bob).ToString());
        Ok(bob.All().Contains("Some of it did not fit. Make room and use [F4C96D]/world collect[FFFFFF]."), "and told how to collect");
        bob.Inventory.Contents.Capacity = 1000;
        RewardCap = long.MaxValue;
        Offline(bob);
        Online(bob);
        RunTimers();
        Ok(Purse(bob) == 150 && OwedMarks(bob) == 0 && OwedUnits(bob) == 0 && Count(bob, "Bread") == 5, "paid in full on the next join");
        Ok(Cmd(bob, "world", "collect").Contains("Nothing is owed to you."), "nothing left to collect");
        // Owed items are saved before anything is given.
        bob.Inventory.Contents.Capacity = 0;
        var list = new List<object>();
        Inv(T, "OweItems", Bb.ToString(), MakeItems("Wood", 5), "test");
        string onDisk = File.ReadAllText(Path.Combine(Dir, "RealmWorld.json"));
        Ok(onDisk.Contains("\"Item\": \"Wood\""), "an owed entry is on disk before the give");
        bob.Inventory.Contents.Capacity = 1000;
        bob.Inventory.Contents.Throws = true;
        Cmd(bob, "world", "collect");
        Ok(OwedUnits(bob) == 5 && Count(bob, "Wood") == 0, "a give the game refuses stays owed");
        bob.Inventory.Contents.Throws = false;
        Cmd(bob, "world", "collect");
        Ok(OwedUnits(bob) == 0 && Count(bob, "Wood") == 5, "collected later");
        // Without RealmTreasury: marks are owed, never lost.
        Reset();
        Absent.Add("RealmTreasury");
        NewWorld();
        ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        bob = Mk(Bb, "Bob", 100, 0);
        AdminCmd(ada, "start", "treasure", "crypt");
        Tick();
        Ok(OwedMarks(bob) == 150, "no treasury loaded: the marks wait");
    }

    static object MakeItems(string item, int n)
    {
        var t = typeof(RealmWorld).GetNestedType("ItemReward", BF);
        var lt = typeof(List<>).MakeGenericType(t);
        var list = (IList)Activator.CreateInstance(lt);
        var r = Activator.CreateInstance(t);
        SetF(r, "Item", item); SetF(r, "Amount", n);
        list.Add(r);
        return list;
    }

    static void Board()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0, 200, 0);
        var b = (Dictionary<string, object>)Inv(T, "GetWorldBoard", "clue 1");
        Ok((string)b["title"] == "The Trail Is Cold" && ((string)b["body"]).StartsWith("The next hunt begins Tuesday 19:00 UTC"), "a clue board with no hunt: the trail is cold", (string)b["body"]);
        AdminCmd(ada, "start", "treasure", "crypt");
        b = (Dictionary<string, object>)Inv(T, "GetWorldBoard", "clue 2");
        Ok((string)b["title"] == "The Crypt Hoard" && (string)b["subtitle"] == "Clue 2 of 2" && (string)b["body"] == "Riddle number 2 for the seeker", "clue 2 shows the riddle that leads to place 2");
        b = (Dictionary<string, object>)Inv(T, "GetWorldBoard", "clue 7");
        Ok((string)b["title"] == "The Trail Is Cold", "a clue past the end shows a cold trail");
        b = (Dictionary<string, object>)Inv(T, "GetWorldBoard", "");
        Ok((string)b["title"] == "Abroad in Ostreval" && ((string)b["subtitle"]).StartsWith("Now: the hunt for the Crypt Hoard") && ((IList)b["rows"]).Count >= 4, "the overview board", string.Join(",", b.Keys));
        Ok(Inv(T, "GetActiveWorldEvent") is string s && s.StartsWith("treasure|"), "GetActiveWorldEvent");
        var next = (Dictionary<string, object>)Inv(T, "GetNextWorldEvent");
        Ok(next != null && next["at"] is DateTime, "GetNextWorldEvent");
        Ok((string)Inv(T, "GetFestivalName") == null, "GetFestivalName with no festival");
        Ok(Refreshes.Count(r => r == "world") >= 1, "starting an event refreshes the world boards");
    }

    static void DataSafety()
    {
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmWorld.json"), "{ \"Format\": 1, \"Hunts\": { \"a\"");
        NewWorld();
        Ok((bool)F(T, "loadFailed") && T.Logged.Any(l => l.Contains("RealmWorld is paused and will NOT write")), "a truncated data file pauses the plugin");
        var bob = Mk(Bb, "Bob");
        Ok(Cmd(bob, "world").Contains("The living world is paused"), "commands say so");
        Inv(T, "OnServerSave");
        Inv(T, "Unload");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmWorld.json")) == "{ \"Format\": 1, \"Hunts\": { \"a\"", "the damaged file is never written");
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmWorld.json"), "");
        NewWorld();
        Ok((bool)F(T, "loadFailed") && File.ReadAllText(Path.Combine(Dir, "RealmWorld.json")) == "", "an empty file pauses the plugin and stays empty");
        Reset();
        File.WriteAllText(Path.Combine(Dir, "RealmWorld.json"), "{ \"Format\": 9 }");
        NewWorld();
        Ok((bool)F(T, "loadFailed") && T.Logged.Any(l => l.Contains("newer RealmWorld")), "a file from a newer version pauses it");
        // A round trip keeps everything.
        Reset();
        NewWorld();
        var ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        Place(ada, "kingsreach", 0, 0, "Kingsreach");
        AdminCmd(ada, "deco", "add", "harvest", "house-varrow");
        AdminCmd(ada, "start", "bloodmoon");
        Reload();
        Ok(((IDictionary)D("Hunts")).Contains("crypt") && ((IDictionary)D("Places")).Contains("kingsreach") && ((IDictionary)D("Decorations")).Contains("harvest") && ActiveKind() == "bloodmoon",
            "hunts, places, decorations and the running event survive a reload");
        // A damaged running-event record is dropped, not trusted.
        Inv(T, "Unload");
        string json = File.ReadAllText(Path.Combine(Dir, "RealmWorld.json"));
        json = json.Replace("\"Kind\": \"bloodmoon\"", "\"Kind\": \"dragon\"");
        File.WriteAllText(Path.Combine(Dir, "RealmWorld.json"), json);
        NewWorld();
        Ok(Active() == null && T.Logged.Any(l => l.Contains("A damaged running event record was dropped")), "a damaged running event is dropped with a warning");
    }

    static void Config()
    {
        Reset();
        NewWorld(c => Section(c, "General", "Enabled", false));
        var ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        Clock = new DateTime(2026, 10, 6, 18, 59, 58, DateTimeKind.Utc);
        Tick();
        Ok(Active() == null && Cmd(ada, "world").Contains("switched off by the staff"), "General.Enabled false: nothing runs");
        Reset();
        NewWorld(c => Section(c, "Treasure", "Enabled", false));
        ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        Clock = new DateTime(2026, 10, 6, 18, 59, 58, DateTimeKind.Utc);
        Tick();
        Ok(Active() == null && !Cmd(ada, "world", "schedule").Contains("Treasure Hunt"), "a kind switched off is not scheduled");
        Reset();
        NewWorld(c =>
        {
            Section(c, "Caravan", "BearerSharePercent", 250);
            Section(c, "Legends", "HelperSharePercent", -5);
            Section(c, "General", "TickSeconds", 0.01f);
            Section(c, "BloodMoon", "BeastDamageMultiplier", 9f);
        });
        Ok((int)F(Cfg("Caravan"), "BearerSharePercent") == 100 && (int)F(Cfg("Legends"), "HelperSharePercent") == 0
            && Math.Abs((float)F(Cfg("General"), "TickSeconds") - 1f) < 0.01f && Math.Abs((float)F(Cfg("BloodMoon"), "BeastDamageMultiplier") - 3f) < 0.01f, "config values are clamped");
        Reset();
        T = new RealmWorld();
        T.Config.Json = "{ broken";
        Inv(T, "LoadDefaultMessages");
        SetF(T, "clock", (Func<DateTime>)(() => Clock));
        Inv(T, "Init");
        Ok(T.Logged.Any(l => l.Contains("RealmWorld.json could not be read")) && ((IList)F(F(T, "config"), "Schedule")).Count == 8, "an unreadable config falls back to the defaults");
        // Days and weeks: every other week for the Blood Moon.
        Reset();
        NewWorld();
        var occ = (IList)Inv(T, "NextOccurrences", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), 28, 1000);
        var blood = occ.Cast<object>().Where(u => (string)F(F(u, "Slot"), "Event") == "bloodmoon").Select(u => (DateTime)F(u, "At")).ToList();
        Ok(blood.Count == 2 && (blood[1] - blood[0]).TotalDays == 14, "the Blood Moon comes every other week", string.Join(",", blood));
        // Without RealmEvents nothing is checked, and nothing breaks.
        Reset();
        Absent.Add("RealmEvents");
        NewWorld();
        ada = Staff();
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        Clock = new DateTime(2026, 10, 6, 18, 59, 58, DateTimeKind.Utc);
        Tick();
        Ok(ActiveKind() == "treasure", "without RealmEvents the schedule runs as set");
    }

    static void ChatStyle()
    {
        Reset();
        NewWorld();
        var ada = Staff();
        var bob = Mk(Bb, "B{0}b [FF0000]", 0, 0);
        string r = Cmd(bob, "world");
        Ok(r.StartsWith("[D6A043]World[FFFFFF]: "), "replies open with the speaker in gold", r);
        r = Cmd(bob, "treasure", "hint");
        Ok(r.StartsWith("ERR [E86A5C]World[FFFFFF]: "), "refusals in red through SendError", r);
        r = Cmd(bob, "world", "nonsense");
        Ok(r.Contains("There is no 'nonsense' here.") && r.Contains("[F4C96D]/treasure[FFFFFF]"), "an unknown word shows the help", r);
        Ok(Cmd(ada, "world", "help").Contains("Staff: [F4C96D]/world admin[FFFFFF]"), "staff see the admin line");
        LayHunt(ada, "crypt", "the Crypt Hoard", 100, 0);
        AdminCmd(ada, "start", "treasure", "crypt");
        At(bob, 100, 0);
        Clear();
        Tick();
        Ok(B().Contains("B0b FF0000 has found the hoard") || B().Contains("B{0}b"), "a player name with braces and tags breaks nothing", B());
        Ok(Server.Broadcasts.All(x => x.StartsWith("[D6A043]Herald[FFFFFF]: ") || x.StartsWith("  ")), "realm-wide news is the Herald's");
        Ok(bob.Popups.All(p => !p.Contains("[D6A043]")), "windows are plain text");
        Ok(bob.Popups.All(p => p.EndsWith("|True")), "windows are sent with broadcast = true");
        PopupsOff.Add(Bb.ToString());
        Ok(!(bool)Inv(T, "PopupsFor", bob), "a player who turned windows off gets none (RealmHerald.PopupsWanted)");
    }
}
