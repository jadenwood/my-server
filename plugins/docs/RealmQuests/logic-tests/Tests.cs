// Behaviour tests for plugins/RealmQuests.cs, compiled unchanged with Mocks.cs and World.cs. Run with run.sh.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static class T
{
    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        Content(); ContentRules(); ContentFiles(); DataSafety(); Assignment(); Rerolls(); Abandoning();
        Creatures(); Crafting(); Building(); Places(); Presence(); Events(); Oaths(); Chronicle(); Contracts(); Delivery(); AnyOne();
        Rewards(); Goods(); StoryLine(); Achievements(); HouseGoals(); PvP(); Journal(); AchievementsCommand(); AdminCommands(); Api(); Config();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static int ContentCount(string field) { var o = F(Q, field); return o is IDictionary d ? d.Count : ((IList)o).Count; }
    static List<string> Problems() { return (List<string>)F(Q, "contentProblems"); }

    static void Content()
    {
        Reset();
        NewQuests();
        Ok(Problems().Count == 0, "the shipped content loads with no problem", string.Join("\n", Problems()));
        Ok(ContentCount("dailies") >= 30, "30 or more daily tasks", ContentCount("dailies").ToString());
        Ok(ContentCount("weeklies") >= 15, "15 or more weekly tasks", ContentCount("weeklies").ToString());
        Ok(ContentCount("achievements") >= 60, "60 or more achievements", ContentCount("achievements").ToString());
        Ok(ContentCount("houseGoals") >= 10, "10 or more house goals", ContentCount("houseGoals").ToString());
        Ok(ContentCount("places") == 12, "the twelve notable places of docs/saga/locations.md", ContentCount("places").ToString());
        var ach = ((IList)F(Q, "achievements")).Cast<object>().ToList();
        foreach (var c in new[] { "survival", "war", "politics", "economy", "exploration" })
            Ok(ach.Count(a => (string)F(a, "Category") == c) >= 10, "10 or more achievements in " + c);
        Ok(ach.Count(a => ((IList)F(a, "Tiers")).Count >= 3) >= 40, "most achievements have three or more tiers");
        var story = F(Q, "story");
        Ok(story != null && (int)F(story, "Season") == 1 && (string)F(story, "Title") == "The Hollow Crown", "the Season 1 story is The Hollow Crown");
        var ch = ((IList)F(story, "Chapters")).Cast<object>().ToList();
        Ok(ch.Select(c => (string)F(c, "Title")).SequenceEqual(new[] { "The Empty Seat", "The Charter Tested", "The Lawful Hours", "The Reckoning at the Hearth" }),
            "four chapters follow the saga's four acts in order");
        Ok(ch.Select(c => (int)F(c, "UnlockDay")).SequenceEqual(new[] { 0, 14, 28, 42 }), "acts open in weeks 1, 3, 5 and 7 (days 0, 14, 28, 42)");
        Ok(ch.Sum(c => ((IList)F(c, "Steps")).Count) == 17, "17 story steps (the Teller of the Tale gold tier)");
        Ok(Q.permission.Registered.Contains("realmquests.admin"), "registers realmquests.admin");
        Ok(EventManager.Count<ItemCrafterCraftEvent>() == 1 && EventManager.Count<ItemCrafterItemEvent>() == 1, "subscribes to the crafting events");
        Inv(Q, "OnServerInitialized");
        Ok(EventManager.Count<ItemCrafterItemEvent>() == 1 && Q.timer.EveryCount == 1, "OnServerInitialized is idempotent (hot reload)");
        // Every content text in the original voice: plain, bounded, no franchise-free check possible here, so at least no
        // colour tags, braces or over-long lines (the loader enforces it; re-read the raw files to be sure).
        foreach (var f in Directory.GetFiles(Path.Combine(Repo, "plugins", "docs", "RealmQuests", "content"), "*.json"))
        {
            string raw = File.ReadAllText(f);
            Ok(!Regex.IsMatch(raw, "\\[[0-9A-Fa-f]{6}\\]"), Path.GetFileName(f) + ": no chat colour tags in content");
            JsonDocument.Parse(raw);
        }
        Inv(Q, "Unload");
        Ok(EventManager.Count<ItemCrafterItemEvent>() == 0, "Unload unsubscribes from crafting");
    }

    static void WriteContent(string name, string json) { File.WriteAllText(Path.Combine(Dir, "RealmQuests", name + ".json"), json); }

    static void ContentRules()
    {
        Reset();
        WriteContent("Dailies", @"{""Version"":1,""Quests"":[
 {""Id"":""ok_one"",""Title"":""Fine"",""Text"":""A fine task."",""Objectives"":[{""Type"":""build"",""Count"":5,""Text"":""Lay blocks""}],""Reward"":{""Marks"":5,""Items"":[{""Item"":""Bread"",""Count"":1}]}},
 {""Id"":""Bad-Id"",""Title"":""Bad"",""Text"":""x"",""Objectives"":[{""Type"":""build"",""Count"":5,""Text"":""Lay""}]},
 {""Id"":""colour"",""Title"":""[FF0000]Red"",""Text"":""x"",""Objectives"":[{""Type"":""build"",""Count"":5,""Text"":""Lay""}]},
 {""Id"":""loop"",""Title"":""Loop"",""Text"":""Hand back the bread."",""Objectives"":[{""Type"":""deliver"",""Targets"":[""Bread""],""Count"":5,""Text"":""Bread""}]},
 {""Id"":""what"",""Title"":""What"",""Text"":""x"",""Objectives"":[{""Type"":""dance"",""Count"":5,""Text"":""Dance""}]},
 {""Id"":""nowhere"",""Title"":""Nowhere"",""Text"":""x"",""Objectives"":[{""Type"":""visit"",""Targets"":[""atlantis""],""Count"":1,""Text"":""Go""}]},
 {""Id"":""toomany"",""Title"":""Too many"",""Text"":""x"",""Objectives"":[{""Type"":""visit"",""Targets"":[""the_hearth""],""Count"":2,""Text"":""Go""}]},
 {""Id"":""meta"",""Title"":""Meta"",""Text"":""x"",""Objectives"":[{""Type"":""achievements"",""Count"":2,""Text"":""Deeds""}]},
 {""Id"":""ok_one"",""Title"":""Dup"",""Text"":""x"",""Objectives"":[{""Type"":""build"",""Count"":5,""Text"":""Lay""}]},
 {""Id"":""rich"",""Title"":""Rich"",""Text"":""x"",""Objectives"":[{""Type"":""build"",""Count"":5,""Text"":""Lay""}],""Reward"":{""Marks"":99999}},
 {""Id"":""braces"",""Title"":""Braces"",""Text"":""Format {0} me"",""Objectives"":[{""Type"":""build"",""Count"":5,""Text"":""Lay""}]}
]}");
        WriteContent("Achievements", @"{""Version"":1,""Achievements"":[
 {""Id"":""a_ok"",""Name"":""Fine"",""Category"":""war"",""Text"":""Fine."",""Objective"":{""Type"":""slay_player"",""Text"":""Foes""},""Tiers"":[{""Count"":1},{""Count"":5}]},
 {""Id"":""a_fall"",""Name"":""Falling"",""Category"":""war"",""Text"":""x"",""Objective"":{""Type"":""slay_player"",""Text"":""Foes""},""Tiers"":[{""Count"":5},{""Count"":1}]},
 {""Id"":""a_cat"",""Name"":""Cat"",""Category"":""cooking"",""Text"":""x"",""Objective"":{""Type"":""slay_player"",""Text"":""Foes""},""Tiers"":[{""Count"":1}]}
]}");
        NewQuests();
        var probs = string.Join("\n", Problems());
        Ok(ContentCount("dailies") == 1 && ((IDictionary)F(Q, "dailies")).Contains("ok_one"), "only the valid daily survives", probs);
        Ok(probs.Contains("Bad-Id: bad id"), "a bad id is refused");
        Ok(probs.Contains("colour: bad title"), "a colour tag in a title is refused");
        Ok(probs.Contains("loop:") && probs.Contains("also a reward item"), "a delivery of a reward item is refused (no reward loop)");
        Ok(probs.Contains("unknown objective type 'dance'"), "an unknown objective type is refused");
        Ok(probs.Contains("unknown place 'atlantis'"), "an unknown place is refused");
        Ok(probs.Contains("toomany:") && probs.Contains("more places than it names"), "a visit asking for more places than it names is refused");
        Ok(probs.Contains("meta:") && probs.Contains("cannot count achievements"), "a task counting achievements is refused");
        Ok(probs.Contains("ok_one: duplicate id"), "a duplicate id is refused");
        Ok(probs.Contains("rich: marks must be 0-10000"), "an absurd reward is refused");
        Ok(probs.Contains("braces: bad text"), "braces in text are refused");
        Ok(ContentCount("achievements") == 1 && probs.Contains("a_fall: tiers must rise") && probs.Contains("a_cat: category must be"), "achievement tiers must rise and categories are fixed");
    }

    static void ContentFiles()
    {
        Reset();
        File.Delete(Path.Combine(Dir, "RealmQuests", "Weeklies.json"));
        string truncated = File.ReadAllText(Path.Combine(Repo, "plugins", "docs", "RealmQuests", "content", "HouseGoals.json"));
        truncated = truncated.Substring(0, truncated.Length / 2);
        WriteContent("HouseGoals", truncated);
        NewQuests();
        var status = (Dictionary<string, string>)F(Q, "contentStatus");
        Ok(status["Weeklies"] == "missing" && ContentCount("weeklies") == 0, "a missing content file switches off only its part");
        Ok(status["HouseGoals"].StartsWith("unreadable") && ContentCount("houseGoals") == 0, "a truncated content file is refused");
        Ok(ContentCount("dailies") >= 30 && ContentCount("achievements") >= 60, "the other parts still load");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmQuests", "HouseGoals.json")) == truncated, "a truncated content file is never rewritten");
        Ok(!File.Exists(Path.Combine(Dir, "RealmQuests", "Weeklies.json")), "a missing content file is never created");
        Reset(false);
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        Clear();
        Cmd(a);
        Ok(a.All().Contains("The quest-board is not posted yet"), "with no content at all, /quest says the board is not posted", a.All());
    }

    static void DataSafety()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        Inv(Q, "OnServerSave");
        string path = Path.Combine(Dir, "RealmQuests.json");
        Ok(File.Exists(path), "a fresh start creates RealmQuests.json");
        Reload();
        Ok(File.Exists(Path.Combine(Dir, "RealmQuests_lastgood.json")), "a good load keeps a copy as RealmQuests_lastgood.json");
        string good = File.ReadAllText(path);
        string cut = good.Substring(0, good.Length / 2);
        File.WriteAllText(path, cut);
        NewQuests();
        Ok((bool)F(Q, "loadFailed"), "a truncated RealmQuests.json refuses to load");
        Clear();
        Cmd(a);
        Ok(a.All().Contains("ERR") && a.All().Contains("could not be read"), "/quest says the board is closed", a.All());
        Hunt(a, "wolf", 3);
        Inv(Q, "OnServerSave");
        Inv(Q, "Unload");
        Ok(File.ReadAllText(path) == cut, "the damaged file is never overwritten (save, hooks, unload)");
        File.WriteAllText(path, "null");
        NewQuests();
        Ok((bool)F(Q, "loadFailed"), "an empty (null) RealmQuests.json refuses to load too");
        Inv(Q, "Unload");
        Ok(File.ReadAllText(path) == "null", "and is not overwritten");
    }

    static void Assignment()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        var b = Mk(76561198000000002, "Brannoc");
        Ok(Daily(a).Count == 3 && Weekly(a).Count == 2, "a player gets three dailies and two weeklies", string.Join(",", DailyIds(a)));
        Ok(DailyIds(a).Distinct().Count() == 3, "the three dailies differ");
        var first = DailyIds(a);
        Reload();
        Ok(DailyIds(a).SequenceEqual(first), "the same board after a restart (seeded by player and day)");
        SetF(P(a), "DayKey", null); SetF(P(a), "Daily", Activator.CreateInstance(Daily(a).GetType()));
        Inv(Q, "Roll", a.Id.ToString(), P(a));
        Ok(DailyIds(a).SequenceEqual(first), "drawn again for the same day: the same tasks (relogging cannot reroll)");
        Ok(!DailyIds(a).SequenceEqual(DailyIds(b)) || !WeeklyIds(a).SequenceEqual(WeeklyIds(b)), "two players get different boards");
        Ok(!DailyIds(a).Any(id => id == "d_market_day" || id == "d_pilgrim" || id == "d_long_patrol" || id == "d_toll_and_ford"), "tasks needing unmarked places are not drawn", string.Join(",", DailyIds(a)));
        Ok(!WeeklyIds(a).Contains("w_courtier"), "tasks needing a house are not drawn for the houseless");
        Clock = Clock.AddDays(1);
        Tick();
        var second = DailyIds(a);
        Ok(second.Count == 3 && !second.Intersect(first).Any(), "the next day brings a new board, without yesterday's tasks", string.Join(",", first) + " / " + string.Join(",", second));
        Ok(WeeklyIds(a).Count == 2, "the weeklies stay for the week");
        Clock = Clock.AddDays(7);
        Tick();
        Ok(WeeklyIds(a).Count == 2 && (string)F(P(a), "WeekKey") == "2026-10-12", "the week turns on Monday at the reset hour");
        // The reset hour: 03:59 UTC is still yesterday's quest day.
        Clock = new DateTime(2026, 10, 20, 3, 59, 0, DateTimeKind.Utc);
        Ok((string)Inv(Q, "DayKey", Clock) == "2026-10-19", "before ResetHourUtc the quest day is still yesterday");
        Clock = new DateTime(2026, 10, 20, 4, 0, 0, DateTimeKind.Utc);
        Ok((string)Inv(Q, "DayKey", Clock) == "2026-10-20", "at ResetHourUtc the quest day turns");
    }

    static void Rerolls()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        var before = DailyIds(a);
        Clear();
        Cmd(a, "reroll", "d2");
        var after = DailyIds(a);
        Ok(after[1] != before[1] && after[0] == before[0] && after[2] == before[2], "a reroll redraws only that slot", a.All());
        Ok(!before.Contains(after[1]), "the reroll never redraws a task already on the board");
        Clear();
        Cmd(a, "reroll", "d1");
        Ok(a.All().Contains("ERR") && a.All().Contains("drawn all 1"), "one reroll a day", a.All());
        Ok(DailyIds(a)[0] == before[0], "the refused reroll changes nothing");
        Cmd(a, "reroll", "w1");
        Ok(a.All().Contains("no task in slot w1"), "weeklies cannot be rerolled");
        Clock = Clock.AddDays(1); Tick();
        SetDaily(a, 0, "d_raise_walls");
        SetF(Daily(a)[0], "Done", true);
        Clear();
        Cmd(a, "reroll", "d1");
        Ok(a.All().Contains("done or abandoned"), "a finished task cannot be rerolled for another reward", a.All());
    }

    static void Abandoning()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_raise_walls");
        Build(a, 10);
        Clear();
        Cmd(a, "abandon", "d1");
        Ok(a.All().Contains("Type [F4C96D]/quest abandon[FFFFFF] d1 confirm"), "abandoning asks first", a.All());
        Ok(!(bool)F(Daily(a)[0], "Abandoned"), "nothing is abandoned before the confirmation");
        Cmd(a, "abandon", "d1", "confirm");
        Ok((bool)F(Daily(a)[0], "Abandoned") && Prog(Daily(a), 0, 0) == 0, "confirmed: abandoned, progress gone");
        Reload();
        Ok(Daily(a).Count == 3 && (bool)F(Daily(a)[0], "Abandoned"), "the slot stays empty after a relog the same day (abandon is no reroll)");
        Build(a, 80);
        Ok(!Done(Daily(a), 0), "an abandoned task does not move");
        Clock = Clock.AddDays(1); Tick();
        Ok(!(bool)F(Daily(a)[0], "Abandoned"), "the next day fills the slot again");
        Clear();
        Cmd(a, "abandon", "x9");
        Ok(a.All().Contains("ERR") && a.All().Contains("no task in slot x9"), "a bad slot is refused", a.All());
    }

    static void Creatures()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_wolves");
        Clear();
        Kill(a, Creature("Wolf(Clone)"));
        Ok(Prog(Daily(a), 0, 0) == 1, "a wolf death credits the killer (Entity.ToString name, MonsterMotor)");
        Ok(a.All().Contains("Wolves at the Fold: Slay wolves 1/3"), "progress is told in chat", a.All());
        Kill(a, Creature("GreyWolf (Clone)", false));
        Ok(Prog(Daily(a), 0, 0) == 2, "a MonsterEntity creature counts too, matched by part of its name");
        Kill(a, new Entity { IsPlayer = false, name = "Wolf(Clone)" });
        Ok(Prog(Daily(a), 0, 0) == 2, "an entity that is no creature (no MonsterMotor or MonsterEntity) does not count");
        Kill(null, Creature("Wolf(Clone)"));
        Ok(Prog(Daily(a), 0, 0) == 2, "a creature that died without a player's blow credits no one");
        Kill(a, Creature("Deer(Clone)"));
        Ok(Prog(Daily(a), 0, 0) == 2, "a deer is not a wolf");
        Clear();
        Kill(a, Creature("Wolf(Clone)"));
        Ok(Done(Daily(a), 0) && a.All().Contains("Task done: Wolves at the Fold"), "the third wolf completes the task", a.All());
        var recent = (List<string>)F(Q, "recentCreatures");
        Ok(recent.Contains("wolf") && recent.Contains("greywolf") && recent.Contains("deer"), "the last creature names are kept for /quest admin creatures", string.Join(",", recent));
        Cfg("CreatureCreditsPerDay", 5);
        SetDaily(a, 1, "d_thin_the_wild");
        Hunt(a, "boar", 10);
        Ok(Prog(Daily(a), 1, 0) == 0 || Prog(Daily(a), 1, 0) <= 5, "creature kills are capped per day (CreatureCreditsPerDay)", Prog(Daily(a), 1, 0).ToString());
    }

    static void Crafting()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        var b = Mk(76561198000000002, "Brannoc");
        SetDaily(a, 0, "d_blades");
        SetDaily(a, 1, "d_fletcher");
        var station = Craft(a, "Iron Sword", 1);
        Ok(Prog(Daily(a), 0, 0) == 1, "a sword made at a crafter the player started counts (ItemCrafterItemEvent)");
        EventManager.Raise(new ItemCrafterItemEvent { Crafter = station, Stack = new CodeHatch.Inventory.Blueprints.InvGameItemStack(CodeHatch.Inventory.Blueprints.InvBlueprints.Get("Steel Battle Axe"), 1, null) });
        Ok(Done(Daily(a), 0), "a later product of the same run counts for the same player");
        Inv(Q, "OnItemCrafted", new ItemCrafterFinishEvent { Crafter = station });
        EventManager.Raise(new ItemCrafterItemEvent { Crafter = station, Stack = new CodeHatch.Inventory.Blueprints.InvGameItemStack(CodeHatch.Inventory.Blueprints.InvBlueprints.Get("Iron Arrow"), 25, null) });
        Ok(Prog(Daily(a), 1, 0) == 0, "after OnItemCrafted the station is forgotten: nobody is credited for a stranger's products");
        Craft(a, "Iron Arrow", 2, 25);
        Ok(Done(Daily(a), 1), "stackable products count by stack amount (2 x 25 arrows)");
        SetDaily(b, 0, "d_blades");
        var hand = new CodeHatch.Engine.Behaviours.ItemCrafter();
        EventManager.Raise(new ItemCrafterItemEvent { Crafter = hand, Entity = b.Entity, Stack = new CodeHatch.Inventory.Blueprints.InvGameItemStack(CodeHatch.Inventory.Blueprints.InvBlueprints.Get("Iron Sword"), 1, null) });
        Ok(Prog(Daily(b), 0, 0) == 1, "hand crafting (the crafter's entity is the player) is credited to that player");
        var cancelled = new ItemCrafterCraftEvent { Sender = a, Crafter = new CodeHatch.Engine.Behaviours.ItemCrafter(), Product = CodeHatch.Inventory.Blueprints.InvBlueprints.Get("Iron Sword") };
        cancelled.Cancel("not a viewer");
        EventManager.Raise(cancelled);
        EventManager.Raise(new ItemCrafterItemEvent { Crafter = cancelled.Crafter, Stack = new CodeHatch.Inventory.Blueprints.InvGameItemStack(CodeHatch.Inventory.Blueprints.InvBlueprints.Get("Iron Sword"), 1, null) });
        Ok(((IDictionary)F(Q, "craftClaims")).Count <= 2, "a cancelled craft request claims no crafter");
        long before = AchCount(a, "ec_master_crafter");
        Craft(a, "Torch", 5);
        Ok(AchCount(a, "ec_master_crafter") == before, "products on the ignore list (cheap loops) never count");
        Cfg("CraftCreditsPerDay", 3);
        SetF(P(a), "Credit", new Dictionary<string, int>());
        Craft(a, "Iron Arrow", 1, 50);
        Ok(AchCount(a, "wa_fletcher") == 50 + 3, "crafting is capped per day (CraftCreditsPerDay)", AchCount(a, "wa_fletcher").ToString());
    }

    static void Building()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_stone_not_thatch");
        SetDaily(a, 1, "d_raise_walls");
        Place(a, 1, 1, 1, 2);
        Ok(Prog(Daily(a), 0, 0) == 1 && Prog(Daily(a), 1, 0) == 1, "a stone block counts for stone and for any-block tasks");
        Place(a, 1, 1, 1, 2);
        Ok(Prog(Daily(a), 1, 0) == 1, "the same cell counts once a day (place, break, place)");
        Place(a, 2, 1, 1, 7);
        Ok(Prog(Daily(a), 0, 0) == 1 && Prog(Daily(a), 1, 0) == 2, "a wood block is not stone (material roles from RealmSculptor's table)");
        Place(a, 3, 1, 1, 2, true);
        Ok(Prog(Daily(a), 1, 0) == 2, "a placement another plugin cancelled is not counted (checked on the next tick)");
        var e = new CodeHatch.Blocks.Networking.Events.CubePlaceEvent { Sender = a, Position = new CodeHatch.Common.Vector3Int(9, 9, 9), Material = 2, CausedByDestruction = true };
        Inv(Q, "OnCubePlacement", e); Q.RunTicks();
        Ok(Prog(Daily(a), 1, 0) == 2, "blocks the game places when something collapses do not count");
        Cfg("BuildCreditsPerDay", 10);
        Build(a, 30);
        Ok(Prog(Daily(a), 1, 0) == 10, "building is capped per day (BuildCreditsPerDay)", Prog(Daily(a), 1, 0).ToString());
        Clock = Clock.AddDays(1); Tick();
        SetDaily(a, 1, "d_raise_walls");
        Place(a, 1, 1, 1, 2);
        Ok(Prog(Daily(a), 1, 0) == 1, "a new day: the cell may count again");
    }

    static void Places()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        MarkPlace("the_hearth", 500, 500, 40);
        MarkPlace("old_throne", 900, 900, 40);
        SetDaily(a, 0, "d_pilgrim");
        Goto(a, 100, 100); Tick(1, false);
        Ok(Prog(Daily(a), 0, 0) == 0, "far from the Hearth: nothing");
        Clear();
        Goto(a, 520, 510); Tick(1, false);
        Ok(Prog(Daily(a), 0, 0) == 1 && a.All().Contains("You reach the Hearth."), "within the radius of a marked place: visited", a.All());
        Goto(a, 905, 890); Tick(1, false);
        Ok(Done(Daily(a), 0), "both places: the pilgrimage is done");
        var visited = (List<string>)F(P(a), "VisitedToday");
        Ok(visited.Contains("the_hearth") && visited.Contains("old_throne"), "visits are remembered for the day (no repeats)");
        long w = AchCount(a, "ex_wayfarer");
        Goto(a, 520, 510); Tick(1, false);
        Ok(AchCount(a, "ex_wayfarer") == w && w == 2, "a place counts once for Wayfarer", w.ToString());
        Clock = Clock.AddDays(1); Tick(1, false);
        Goto(a, 500, 500); Tick(1, false);
        Ok(AchCount(a, "ex_wayfarer") == 2, "a place seen again on another day is still one place");
        Admin(a);
        Clear();
        Cmd(a, "admin", "place", "set", "crown_market", "25");
        Ok(((IDictionary)F(Data, "Places")).Contains("crown_market") && a.All().Contains("the Crown Market is marked here (radius 25 m)"), "/quest admin place set marks a place where the steward stands", a.All());
        Cmd(a, "admin", "place", "clear", "crown_market");
        Ok(!((IDictionary)F(Data, "Places")).Contains("crown_market"), "/quest admin place clear removes it");
        Clear();
        Cmd(a, "admin", "place", "set", "atlantis");
        Ok(a.All().Contains("ERR") && a.All().Contains("No place is called"), "an unknown place is refused", a.All());
    }

    static void Presence()
    {
        Reset();
        NewQuests(c => Set(c, "TickSeconds", 30f));
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_watchman");
        Minutes(30, false);
        Ok(Prog(Daily(a), 0, 0) == 0, "standing still (AFK) is not active play");
        Minutes(30, true);
        int p = Prog(Daily(a), 0, 0);
        Ok(p >= 29 && p <= 30, "moving counts active minutes", p.ToString());
        Minutes(31, true);
        Ok(Done(Daily(a), 0), "an hour of active play completes the watch");
        Ok(AchCount(a, "su_long_life") >= 60, "a long life is followed (survive)", AchCount(a, "su_long_life").ToString());
        Kill(Mk(76561198000000002, "Brannoc"), a.Entity);
        Ok((double)F(P(a), "LifeMinutes") == 0, "a death starts a new life");
        Ok(AchCount(a, "su_long_life") >= 60, "the best life is kept for the deed");
    }

    static void Events()
    {
        Reset();
        NewQuests(c => Set(c, "TickSeconds", 30f));
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_answer_herald");
        ActiveEvents.Add("crown_night");
        Minutes(5);
        Ok(Prog(Daily(a), 0, 0) == 0, "a few minutes at an event is not attendance");
        Minutes(6);
        Ok(Done(Daily(a), 0), "EventAttendMinutes of active presence while an event runs counts");
        Ok(AchCount(a, "wa_crown_nights") == 1, "Night of Crowns counts the Crown Night");
        Minutes(30);
        Ok(AchCount(a, "wa_crown_nights") == 1, "once per event kind per day");
        Reload();
        Minutes(30);
        Ok(AchCount(a, "wa_crown_nights") == 1, "and not again after a restart the same day");
        Minutes(30, false);
        ActiveEvents.Clear(); ActiveEvents.Add("tournament");
        Minutes(15, false);
        Ok(AchCount(a, "wa_into_the_lists") == 0, "standing idle at an event is not taking part");
    }

    static void Oaths()
    {
        Reset();
        NewQuests();
        HouseOf[76561198000000003] = "Ashgrove";
        var a = Mk(76561198000000001, "Aldric", "Varrow");
        var b = Mk(76561198000000002, "Brannoc", "Ashgrove");
        var c = Mk(76561198000000004, "Cyne", "Varrow");
        var lone = Mk(76561198000000005, "Lonely", "Solo");
        Poll();
        foreach (var p in new[] { a, b, c, lone }) SetF(P(p), "HouseSince", Clock.AddDays(-3));
        Ok((bool)F(Data, "LiegesBaselined"), "the first poll only baselines the oaths");
        Liege["Ashgrove"] = "Varrow";
        Liege["Solo"] = "Varrow";
        Poll();
        Ok(AchCount(b, "po_oathsworn") == 1, "Ashgrove swears: its members are credited 'sworn'");
        Ok(AchCount(a, "po_liege") == 1 && AchCount(c, "po_liege") == 1, "Varrow's members are credited 'accepted'");
        Ok(AchCount(lone, "po_oathsworn") == 0, "a one-member house swearing is no oath");
        Ok(AchCount(a, "po_liege") == 1, "the lone house's oath credits nothing to the liege either");
        Liege.Remove("Ashgrove"); Poll();
        Liege["Ashgrove"] = "Varrow"; Poll();
        Ok(AchCount(b, "po_oathsworn") == 1, "renounce and swear again the same day: credited once a day");
        var d = Mk(76561198000000006, "Dwyn", "Ashgrove");
        Poll();
        Liege["Ashgrove"] = "Corvane"; HouseOf[76561198000000007] = "Corvane"; HouseOf[76561198000000008] = "Corvane";
        Clock = Clock.AddDays(1);
        SetF(P(d), "HouseSince", Clock.AddHours(-2));
        Poll();
        Ok(AchCount(d, "po_oathsworn") == 0, "a member of less than HouseMemberMinHours is not credited");
        Ok(AchCount(b, "po_oathsworn") == 2, "the long-standing member is, on a new day");
    }

    static void Chronicle()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric", "Varrow");
        var b = Mk(76561198000000002, "Brannoc", "Corvane");
        HouseOf[76561198000000003] = "Varrow"; HouseOf[76561198000000004] = "Corvane";
        Poll();
        foreach (var p in new[] { a, b }) SetF(P(p), "HouseSince", Clock.AddDays(-3));
        WriteJson("RealmChronicle", "[{\"id\":1,\"type\":\"coronation\",\"title\":\"x\",\"actors\":[\"Aldric\"]}]");
        Poll();
        Ok(AchCount(a, "wa_crowned") == 0 && (int)F(Data, "ChronicleCursor") == 1, "the first look baselines the Chronicle (history is never replayed)");
        WriteJson("RealmChronicle", "[{\"id\":1,\"type\":\"coronation\",\"title\":\"x\",\"actors\":[\"Aldric\"]},"
            + "{\"id\":2,\"type\":\"coronation\",\"title\":\"Aldric is crowned\",\"actors\":[\"Aldric\",\"Brannoc\"]},"
            + "{\"id\":3,\"type\":\"law_proclaimed\",\"title\":\"x\",\"actors\":[\"Nobody Known\"]},"
            + "{\"id\":4,\"type\":\"treaty_signed\",\"title\":\"House Varrow and House Corvane sign a treaty\",\"actors\":[\"Aldric\",\"Brannoc\"]}]");
        Poll();
        Ok(AchCount(a, "wa_crowned") == 1, "a new coronation credits its doer (actors[0])");
        Ok(AchCount(b, "wa_crowned") == 0, "the other actor is not the doer");
        Ok(AchCount(a, "po_seal_and_wax") == 1 && AchCount(b, "po_seal_and_wax") == 1, "a treaty credits the members of both houses named in its title");
        Ok((int)F(Data, "ChronicleCursor") == 4, "the cursor moves on");
        Poll();
        Ok(AchCount(a, "wa_crowned") == 1, "an entry is counted once");
        Mk(76561198000000009, "aldric");
        WriteJson("RealmChronicle", "[{\"id\":5,\"type\":\"coronation\",\"title\":\"x\",\"actors\":[\"Aldric\"]}]");
        Poll();
        Ok(AchCount(a, "wa_crowned") == 1, "a name two players share is never guessed");
        WriteJson("RealmChronicle", "[{\"id\":1,\"type\":\"coronation\",\"title\":\"x\",\"actors\":[\"Brannoc\"]}]");
        Poll();
        Ok(AchCount(b, "wa_crowned") == 0 && (int)F(Data, "ChronicleCursor") == 1, "a reset Chronicle re-baselines instead of replaying");
        File.WriteAllText(Path.Combine(Dir, "RealmChronicle.json"), "[{\"id\":2,");
        Poll();
        var fs = (Dictionary<string, string>)F(Q, "feedStatus");
        Ok(fs["chronicle"].StartsWith("unreadable"), "an unreadable Chronicle is reported, not trusted");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmChronicle.json")) == "[{\"id\":2,", "another plugin's file is never written");
    }

    static void Contracts()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric", "Varrow");
        var b = Mk(76561198000000002, "Brannoc", "Corvane");
        var c = Mk(76561198000000003, "Cyne", "Varrow");
        string ids = "\"" + a.Id + "\"";
        WriteJson("RealmContracts", "{\"NextId\":2,\"Contracts\":[{\"Id\":1,\"Type\":\"delivery\",\"Status\":\"done\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + b.Id + "\"}]}");
        Poll();
        Ok(AchCount(a, "ec_contract_keeper") == 0, "finished contracts before the first look are baselined");
        WriteJson("RealmContracts", "{\"NextId\":7,\"Contracts\":["
            + "{\"Id\":1,\"Type\":\"delivery\",\"Status\":\"done\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + b.Id + "\"},"
            + "{\"Id\":2,\"Type\":\"bounty\",\"Status\":\"done\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + b.Id + "\"},"
            + "{\"Id\":3,\"Type\":\"delivery\",\"Status\":\"done\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + b.Id + "\"},"
            + "{\"Id\":4,\"Type\":\"delivery\",\"Status\":\"done\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + c.Id + "\"},"
            + "{\"Id\":5,\"Type\":\"delivery\",\"Status\":\"done\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + a.Id + "\"},"
            + "{\"Id\":6,\"Type\":\"merc\",\"Status\":\"open\",\"FulfillerId\":\"" + a.Id + "\",\"PosterId\":\"" + b.Id + "\"}]}");
        Poll();
        Ok(AchCount(a, "ec_contract_keeper") == 1 && AchCount(a, "ec_headhunter") == 1, "a new finished bounty counts for contract deeds");
        Ok(AchCount(a, "ec_carter") == 0, "the same poster and fulfiller count once a day (contract #3 is a pair repeat)");
        Ok(AchCount(a, "ec_contract_keeper") == 1, "a contract within one house (#4) and a player's own (#5) never count; an open one (#6) waits");
        Poll();
        Ok(AchCount(a, "ec_contract_keeper") == 1, "a contract is credited once");
        _ = ids;
    }

    static void Delivery()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_wood_fold");
        SetDaily(a, 1, "d_hides");
        SetDaily(a, 2, "d_wolves");
        foreach (var sl in Weekly(a)) SetF(sl, "Done", true);
        Give(a, "Wood", 100); Give(a, "Wolf Pelt", 3);
        Clear();
        Cmd(a, "give");
        Ok(a.All().Contains("Timber for the Fold (Wood): 150 wanted, you carry 100"), "/quest give lists what is wanted and what is carried", a.All());
        Ok(Has(a, "Wood") == 100, "listing takes nothing");
        Cmd(a, "give", "all");
        Ok(Has(a, "Wood") == 0 && Prog(Daily(a), 0, 0) == 100, "give all hands in the wood, measured", a.All());
        Ok(Has(a, "Wolf Pelt") == 0 && Prog(Daily(a), 1, 0) == 3, "a ResourceType name (WolfPelt) finds the item by its display name (Wolf Pelt)");
        Ok(AchCount(a, "su_woodsman") == 100 && AchCount(a, "su_tanner") == 3, "hand-ins count for the delivery deeds");
        Give(a, "Wood", 80);
        Cmd(a, "give", "all");
        Ok(Done(Daily(a), 0) && Has(a, "Wood") == 30, "only what the task still wants is taken (50 of 80)", Has(a, "Wood").ToString());
        Ok(AchCount(a, "su_woodsman") == 150, "the deed counts only what was handed in");
        a.Packs.Refuse = true;
        Give(a, "Deer Skin", 1);
        a.Packs.Refuse = false; Give(a, "Deer Skin", 5); a.Packs.Refuse = true;
        Clear();
        Cmd(a, "give", "all");
        Ok(Prog(Daily(a), 1, 0) == 3 && a.All().Contains("could not be taken"), "goods the game would not take are not counted", a.All());
        a.Packs.Refuse = false;
        a.NoInventory = true;
        Clear();
        Cmd(a, "give", "all");
        Ok(a.All().Contains("cannot be reached"), "packs that cannot be read: nothing happens", a.All());
        a.NoInventory = false;
        // Two tasks asking for the same goods: each takes its own.
        Clock = Clock.AddDays(7); Tick();
        SetDaily(a, 0, "d_wood_fold"); SetWeekly(a, 0, "w_timber_levy");
        SetDaily(a, 1, "d_wolves"); SetDaily(a, 2, "d_bear"); SetWeekly(a, 1, "w_bearbane");
        Give(a, "Wood", 200);
        Cmd(a, "give", "all");
        Ok(Prog(Daily(a), 0, 0) + Prog(Weekly(a), 0, 0) == 230 && Has(a, "Wood") == 0, "two tasks wanting wood share what is handed in (230 carried), never count it twice",
            Prog(Daily(a), 0, 0) + "+" + Prog(Weekly(a), 0, 0));
    }

    static void AnyOne()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_steel_tested");
        Hunt(a, "boar", 10);
        Ok(Done(Daily(a), 0), "an AnyOne task is done when any one objective is (ten creatures, no duel needed)");
    }

    static void Rewards()
    {
        Reset();
        NewQuests(c => Set(c, "MinActiveMinutesForMarks", 30));
        var a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_wolves");
        Clear();
        Hunt(a, "wolf", 3);
        Ok(Pending(a) == 15 && RewardCalls.Count == 0, "a new account's marks are held (MinActiveMinutesForMarks)");
        Ok(a.All().Contains("held for now: you have not yet played long enough"), "and told why", a.All());
        Ok(Deeds.Any(d => d.StartsWith(a.Id + "|Aldric|quest_daily|Wolves at the Fold|quests:daily:d_wolves:2026-10-05")), "renown: AddDeed(quest_daily) with a dedupe key per task and day", string.Join("\n", Deeds));
        Active(a, 31);
        Clear();
        Tick();
        Ok(Pending(a) == 0 && Purse[a.Id.ToString()] == 15 && a.All().Contains("The treasury pays you 15 marks"), "after enough play the treasury pays (RewardMarks)", a.All());
        SetDaily(a, 1, "d_bear");
        TreasuryLeft = 5;
        Clear();
        Hunt(a, "bear", 1);
        Ok(Pending(a) == 25 && Purse[a.Id.ToString()] == 20, "what the treasury cannot pay stays owed (bear 20 + Bearbane 10: 5 paid, 25 owed)", Pending(a) + " / " + Purse[a.Id.ToString()]);
        Ok(a.All().Contains("reward purse is spent"), "and the player is told", a.All());
        TreasuryLeft = 1000;
        Tick();
        Ok(Pending(a) == 0 && Purse[a.Id.ToString()] == 45, "owed marks are paid on a later tick");
        SetDaily(a, 2, "d_raise_walls");
        TreasuryThrows = true;
        Build(a, 80);
        Ok(Pending(a) == 14, "a treasury that throws pays nothing and the marks stay owed (never lost, never doubled)", Pending(a).ToString());
        TreasuryThrows = false;
        SetF(Q, "RealmTreasury", null);
        Tick();
        Ok(Pending(a) == 14, "without RealmTreasury the marks wait");
        SetF(Q, "RealmTreasury", Treasury);
        Tick();
        Ok(Pending(a) == 0, "and are paid when it returns");
        // One hearth, many accounts.
        Reset();
        NewQuests(c => Set(c, "MaxRewardedAccountsPerAddress", 2));
        var p1 = Mk(76561198000000011, "One", null, 24, "10.1.1.1");
        var p2 = Mk(76561198000000012, "Two", null, 24, "10.1.1.1");
        var p3 = Mk(76561198000000013, "Three", null, 24, "10.1.1.1");
        var p4 = Mk(76561198000000014, "Four", null, 24, "10.9.9.9");
        foreach (var p in new[] { p1, p2, p3, p4 }) { SetDaily(p, 0, "d_wolves"); Hunt(p, "wolf", 3); }
        Ok(Purse.ContainsKey(p1.Id.ToString()) && Purse.ContainsKey(p2.Id.ToString()), "two accounts from one address are paid");
        Ok(!Purse.ContainsKey(p3.Id.ToString()) && Pending(p3) == 15, "a third from the same address is held (MaxRewardedAccountsPerAddress)");
        Ok(Purse.ContainsKey(p4.Id.ToString()), "another address is paid");
        Ok(p3.All().Contains("too many accounts from one hearth"), "the held account is told why", p3.All());
        Clock = Clock.AddDays(1); Tick();
        Ok(Pending(p3) == 15 || Purse.ContainsKey(p3.Id.ToString()), "the next day the count starts again");
        var dataText = File.ReadAllText(Path.Combine(Dir, "RealmQuests.json"));
        Ok(!dataText.Contains("10.1.1.1"), "addresses are never written to the data file");
    }

    static void Goods()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric", null, 2);
        SetDaily(a, 0, "d_venison");
        Hunt(a, "deer", 3);
        Ok(Has(a, "Cooked Meat") == 2 && OwedOf(a, "CookedMeat") == 0, "reward goods go straight into the packs (ResourceType name CookedMeat)");
        Ok(SentinelCalls.Any(s => s.StartsWith(a.Id + "|")), "RealmSentinel is told the gain is explained (SentinelItemSource)");
        Give(a, "Wood", 1000); Give(a, "Stone", 1000);
        // Packs full: a reward that cannot fit stays owed.
        var full = Mk(76561198000000002, "Fullpack", null, 1);
        Give(full, "Stone", 1000);
        Inv(Q, "AddOwed", P(full), "Bread", 2, "test");
        Clear();
        Cmd(full, "collect");
        Ok(OwedOf(full, "Bread") == 2 && full.All().Contains("still wait"), "goods that do not fit stay owed, measured", full.All());
        full.Packs.GetItems().ForEach(s => CodeHatch.ItemContainer.ItemCollection.AutoSplit(full.Packs, s.Blueprint, s.StackAmount));
        Clear();
        Cmd(full, "collect");
        Ok(OwedOf(full, "Bread") == 0 && Has(full, "Bread") == 2 && full.All().Contains("You collect 2 Bread"), "/quest collect gives them once there is room", full.All());
        Cmd(full, "collect");
        Ok(Has(full, "Bread") == 2 && full.All().Contains("No reward goods wait"), "collecting again gives nothing more");
        Inv(Q, "AddOwed", P(full), "Unobtainium", 1, "test");
        Clear();
        Cmd(full, "collect");
        Ok(OwedOf(full, "Unobtainium") == 1 && full.All().Contains("not an item this realm knows"), "an unknown item stays owed and is reported", full.All());
        // Crash safety: the owed entry is saved as taken before the stack is given.
        Inv(Q, "AddOwed", P(full), "Bread", 3, "test");
        int writesBefore = Interface.Oxide.DataFileSystem.Writes;
        Cmd(full, "collect");
        Ok(Interface.Oxide.DataFileSystem.Writes >= writesBefore + 2, "the ledger is saved before and after each gift");
    }

    static void StoryLine()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        Season(0);
        Clear();
        Cmd(a, "story");
        Ok(a.All().Contains("The Hollow Crown, the tale of Season 1.") && a.All().Contains("Act 1: The Empty Seat") && a.All().Contains("Now: Ash at the Hearth."),
            "/quest story tells the prologue, the act and the step", a.All());
        Ok(a.Popups.Count == 1 && a.Popups[0].StartsWith("The Hollow Crown|") && a.Popups[0].EndsWith("|True"), "and opens it in a window (broadcast true)", string.Join("\n", a.Popups));
        Minutes(16);
        Ok(Finished(a).Contains("s1_hearth_ash"), "an unmarked Hearth is waived; walking the realm finishes the first step", string.Join(",", Finished(a)));
        Ok(Deeds.Any(d => d.Contains("|quest_story|Ash at the Hearth|quests:story:s1_hearth_ash")), "a story step adds the quest_story deed");
        HouseOf[a.Id] = "Varrow"; HouseOf[76561198000000099] = "Varrow";
        Poll();
        Ok(Finished(a).Contains("s1_banner"), "joining a house finishes Under a Banner (state)");
        Build(a, 60);
        Ok(Finished(a).Contains("s1_roof") && Has(a, "Bread") == 5, "sixty blocks: A Roof Before Winter, with bread");
        WriteJson("RealmChronicle", "[]");
        Poll();
        WriteJson("RealmChronicle", "[{\"id\":1,\"type\":\"house_founded\",\"title\":\"House Varrow is founded\",\"actors\":[\"Aldric\"]}]");
        Poll();
        Ok(Finished(a).Contains("s1_knee"), "founding a house answers Bend the Knee, or Stand (AnyOne)");
        Ok(Finished(a).Contains("s1_first_sitting") && Finished(a).Contains("chapter:act1_empty_seat"), "an unmarked throne is waived: Act I is done", string.Join(",", Finished(a)));
        Clear();
        Tick();
        Cmd(a);
        Ok(a.All().Contains("the next chapter opens on day 15"), "Act II waits for day 15 of the season", a.All());
        Season(14);
        Clear();
        Cmd(a);
        Ok(a.All().Contains("Act 2: The Charter Tested - Coin at the Crown Market"), "on day 15 Act II opens", a.All());
        SeasonNumber = 2;
        Clear(); Cmd(a);
        Ok(a.All().Contains("waits for the season to open"), "another season number pauses the Season 1 tale");
        SeasonNumber = 1;
        // Through to the end with the steward's testing aid.
        Admin(a);
        var order = new[] { "s2_market", "s2_contract", "s2_lists", "s2_forge", "s2_court", "s3_wold", "s3_lawful", "s3_hunt", "s3_ember", "s4_ledger", "s4_last_night", "s4_hearth_truce" };
        Season(50);
        Clear();
        foreach (var s in order) Cmd(a, "admin", "complete", "Aldric", s);
        Ok(Finished(a).Count(x => !x.StartsWith("chapter:")) == 17 && (bool)F(Story(a), "Complete"), "the tale can be followed to its end", string.Join(",", Finished(a)));
        Ok(Deeds.Any(d => d.Contains("|story_complete|")), "the end adds the story_complete deed (RealmRenown's Witness of the Crown title)");
        Ok(B().Contains("Aldric is the first in the realm to see the tale of The Hollow Crown to its end"), "the first to finish is heralded", B());
        Ok(ChronLog.Any(l => l.StartsWith("title_earned|Aldric is the first to see The Hollow Crown to its end")), "and chronicled as title_earned", string.Join("\n", ChronLog));
        Ok(Has(a, "Steel Ingot") >= 10, "the final reward: steel");
        Ok(AchTier(a, "ex_teller") == 3, "Teller of the Tale reaches its gold tier at 17 steps");
        var b = Mk(76561198000000002, "Brannoc");
        Clear();
        foreach (var s in new[] { "s1_hearth_ash", "s1_banner", "s1_roof", "s1_knee", "s1_first_sitting" }.Concat(order)) Cmd(a, "admin", "complete", "Brannoc", s);
        Ok(B().Contains("Brannoc has seen the tale of The Hollow Crown to its end") && !B().Contains("Brannoc is the first"), "the second is heralded without 'first'", B());
        Clear();
        Cmd(a, "story");
        Ok(a.All().Contains("you saw it to its end") && a.All().Contains("pages of the lost winter"), "after the end, /quest story tells the epilogue", a.All());
    }

    static void Achievements()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        Clear();
        Hunt(a, "wolf", 5);
        Ok(AchTier(a, "su_wolfsbane") == 1 && AchTier(a, "su_hunter") == 0, "five wolves: Wolfsbane bronze; Hunter needs ten");
        Ok(a.All().Contains("Deed recorded: Wolfsbane (Bronze)"), "the deed is told", a.All());
        Ok(Pending(a) == 0 && Purse[a.Id.ToString()] >= 10, "and pays its bronze marks");
        Ok(Deeds.Any(d => d.Contains("|achievement|Wolfsbane (Bronze)|quests:ach:su_wolfsbane:0")), "a bronze tier adds the achievement deed");
        Clear();
        Hunt(a, "wolf", 95);
        Ok(AchTier(a, "su_wolfsbane") == 3, "a hundred wolves: gold");
        Ok(Deeds.Any(d => d.Contains("|achievement_gold|Wolfsbane (Gold)")), "a gold tier adds the achievement_gold deed (Paragon of Ostreval)");
        Ok(B().Contains("Aldric has earned the deed Wolfsbane (Gold)."), "a gold tier is heralded", B());
        Ok(AchTier(a, "su_hunter") == 2, "Hunter counts every creature (100: silver)");
        Ok(Count(a, "achievements") >= 5 && AchCount(a, "su_deeds_upon_deeds") == Count(a, "achievements"), "Deeds upon Deeds counts every tier earned");
        RenownOf[a.Id.ToString()] = 600;
        TitlesOf[a.Id.ToString()] = new[] { "kingslayer", "renowned" };
        Purse[a.Id.ToString()] = 150;
        Poll();
        Ok(AchTier(a, "po_name_in_realm") == 2, "renown is followed as a level (600: silver)");
        Ok(AchTier(a, "po_many_names") == 1 && AchCount(a, "po_many_names") == 2, "titles held are counted");
        Ok(AchTier(a, "ec_full_purse") == 1, "a purse of 150 marks: A Full Purse bronze");
        RenownOf[a.Id.ToString()] = 10;
        Poll();
        Ok(AchCount(a, "po_name_in_realm") == 600, "a level never goes back down");
        Clear();
        Hunt(a, "wolf", 50);
        Ok(!a.All().Contains("Wolfsbane"), "a finished deed stays finished; no repeat rewards");
    }

    static void HouseGoals()
    {
        Reset();
        NewQuests(c => { Set(c, "HouseGoalMinContributors", 2); Set(c, "BuildCreditsPerDay", 100000); });
        var a = Mk(76561198000000001, "Aldric", "Varrow");
        var b = Mk(76561198000000002, "Brannoc", "Varrow");
        var c = Mk(76561198000000003, "Cyne", "Varrow");
        var lone = Mk(76561198000000009, "Lonely", "Solo");
        Poll();
        foreach (var p in new[] { a, b, c, lone }) SetF(P(p), "HouseSince", Clock.AddDays(-3));
        var houses = (IDictionary)F(Data, "Houses");
        Clear(); Cmd(a, "house");
        var h = houses["Varrow"];
        var goals = (IList)F(h, "Goals");
        Ok(goals.Count == 1, "a house gets one goal for the week", a.All());
        // Make the goal known: walls.
        SetF(goals[0], "GoalId", "h_walls");
        Build(a, 1000);
        Ok((int)F(goals[0], "Progress") == 750, "a member's share is capped at HouseGoalMemberCapPercent (50% of 1500)", F(goals[0], "Progress").ToString());
        Build(b, 750);
        Ok((bool)F(goals[0], "Done"), "two members of real share meet the goal");
        Ok(SeasonAwards.Any(s => s.StartsWith("Varrow|10|Weekly goal: Walls of the House")), "the house is awarded season points (RealmSeasons.AwardHouse)", string.Join("\n", SeasonAwards));
        Ok(B().Contains("House Varrow has met its goal for the week: Walls of the House."), "the realm is told", B());
        Ok(Deeds.Count(d => d.Contains("|house_goal|")) == 2, "each who helped gets the house_goal deed", string.Join("\n", Deeds));
        Ok(AchCount(a, "po_pillar") == 1 && AchCount(c, "po_pillar") == 0, "Pillar of the House counts for helpers only");
        Build(c, 100);
        Ok(Deeds.Count(d => d.Contains("|house_goal|")) == 2, "a met goal pays nothing more");
        Clear(); Cmd(lone, "house");
        Ok(lone.All().Contains("a house needs 2 members"), "a one-member house is set no goal (alts)", lone.All());
        // A new recruit.
        Clock = Clock.AddDays(7); Tick();
        var d = Mk(76561198000000004, "Dwyn", "Varrow");
        Poll();
        goals = (IList)F(houses["Varrow"], "Goals");
        SetF(goals[0], "GoalId", "h_walls");
        Build(d, 500);
        Ok((int)F(goals[0], "Progress") == 0, "a member of less than HouseMemberMinHours cannot help yet (house hopping)");
        // Not enough helpers.
        Build(a, 750);
        Ok(!(bool)F(goals[0], "Done") && (int)F(goals[0], "Progress") == 750, "one member alone cannot finish it (capped share)");
        Cfg("HouseGoalMemberCapPercent", 100);
        Clear();
        Build(a, 750);
        Ok(!(bool)F(goals[0], "Done") && (int)F(goals[0], "Progress") == 1500, "even with no share cap, one helper is not enough (HouseGoalMinContributors)");
        Ok(a.All().Contains("only when 2 members have helped"), "and is told so", a.All());
        Cfg("HouseGoalMemberCapPercent", 50);
        // Delivery goals use /quest give within the member's share.
        SetF(goals[0], "GoalId", "h_woodpile");
        SetF(goals[0], "Progress", 0); ((Dictionary<string, int>)F(goals[0], "Contrib")).Clear();
        foreach (var sl in Daily(a).Cast<object>().Concat(Weekly(a).Cast<object>())) SetF(sl, "Done", true);   // only the house wants wood
        Give(a, "Wood", 2000);
        Cmd(a, "give", "all");
        Ok((int)F(goals[0], "Progress") == 1500 && Has(a, "Wood") == 500, "a house delivery takes only the member's share (half of 3000)", F(goals[0], "Progress") + " / " + Has(a, "Wood"));
        string text = (string)Inv(Q, "GetHouseGoalText", "Varrow");
        Ok(text != null && text.StartsWith("The House Woodpile 1500/3000"), "GetHouseGoalText gives a board line", text ?? "null");
    }

    static void PvP()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric", "Varrow");
        var b = Mk(76561198000000002, "Brannoc", "Corvane");
        var c = Mk(76561198000000003, "Cyne", "Varrow");
        var d = Mk(76561198000000004, "Dwyn", "Ashgrove");
        foreach (var p in new[] { a, b, c, d }) Active(p, 120);
        Kill(a, b.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a fair kill of an established foe of another house counts");
        Kill(b, a.Entity);
        Ok(AchCount(b, "wa_first_blood") == 0, "the same pair the other way round within the cooldown does not (kill-trading)");
        Clock = Clock.AddMinutes(5);
        Kill(a, b.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "nor the same pair again");
        Kill(a, c.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a housemate never counts");
        Liege["Ashgrove"] = "Varrow";
        Kill(a, d.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a sworn vassal house never counts");
        Liege.Clear(); Treaties.Add(Pair("Varrow", "Ashgrove"));
        Kill(a, d.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a treaty partner never counts");
        Treaties.Clear();
        var e = Mk(76561198000000005, "Edda", "Merrin");
        Kill(a, e.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a victim with little active play (a fodder alt) does not count");
        Active(e, 120);
        Protected.Add(e.Id);
        Kill(a, e.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a victim under new-player protection does not count");
        Protected.Clear();
        Clock = Clock.AddSeconds(30);
        Kill(a, e.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a victim killed again within PvpMinVictimLifeSeconds of a death does not count");
        Clock = Clock.AddMinutes(5);
        Offline(e);
        Kill(a, e.Entity);
        Ok(AchCount(a, "wa_first_blood") == 1, "a sleeping body is no fight");
        Online(e);
        Clock = Clock.AddMinutes(5);
        Kill(a, e.Entity);
        Ok(AchCount(a, "wa_first_blood") == 2 && AchCount(a, "wa_many_foes") == 2, "a fresh, established, online foe counts");
        Clock = Clock.AddDays(1).AddHours(1);
        Kill(b, a.Entity);
        Ok(AchCount(b, "wa_first_blood") == 1, "after PvpPairCooldownHours the pair may count again");
        Cfg("PvpCreditsPerDay", 1);
        var f = Mk(76561198000000006, "Fen", "Dunmere"); Active(f, 120);
        Kill(b, f.Entity);
        Ok(AchCount(b, "wa_first_blood") == 1, "player kills are capped per day (PvpCreditsPerDay)");
        Kill(a, a.Entity);
        Ok(AchCount(a, "wa_first_blood") == 2, "a suicide counts for nothing");
    }

    static void Journal()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric", "Varrow");
        SetDaily(a, 0, "d_wolves");
        Clear();
        Cmd(a);
        string all = a.All();
        Ok(all.StartsWith("[D6A043]Quests[FFFFFF]: Your journal. The day turns in 16h 0m, the week in 6d 16h."), "the journal opens in the Quests voice with the resets", all);
        Ok(all.Contains("  Daily 1: Wolves at the Fold - Slay wolves 0/3 [A3A6AD](15 marks)[FFFFFF]"), "each daily reads title, objectives and reward", all);
        Ok(all.Contains("  Weekly 1: ") && all.Contains("  Story: ") && all.Contains("  House Varrow: "), "weeklies, the story and the house goal are shown");
        Ok(all.Contains("[F4C96D]/quest log[FFFFFF]"), "commands are drawn in the command colour");
        Ok(a.Popups.Count == 1 && a.Popups[0].StartsWith("Your Journal|") && a.Popups[0].EndsWith("|Onward|True"), "the journal opens in a window too", string.Join("\n", a.Popups));
        Ok(!a.Popups[0].Contains("[A3A6AD]") && !a.Popups[0].Contains("[FFFFFF]"), "the window holds plain text");
        PopupsOff.Add(a.Id.ToString());
        Clear(); Cmd(a);
        Ok(a.Popups.Count == 0 && a.All().Contains("Your journal"), "/realm popups off: chat only");
        PopupsOff.Clear();
        a.PopupsThrow = true;
        Clear(); Cmd(a);
        Ok(a.All().Contains("Your journal") && Q.Logged.Any(l => l.Contains("ShowPopup failed")), "a window that fails falls back to chat");
        a.PopupsThrow = false;
        Cfg("UsePopups", false);
        Clear(); Cmd(a);
        Ok(a.Popups.Count == 0, "UsePopups false: no windows");
        Cfg("UsePopups", true);
        Clear();
        Cmd(a, "log");
        Ok(a.All().Contains("d1: Wolves at the Fold") && a.All().Contains("Halloran's shepherds") && a.All().Contains("  - Slay wolves: 0/3") && a.All().Contains("Reward: 15 marks, renown"),
            "/quest log reads every open task in full", a.All());
        Clear();
        Cmd(a, "help");
        Ok(a.All().Contains("[F4C96D]/quest give[FFFFFF]") && !a.All().Contains("Stewards:"), "/quest help; the steward line only for stewards", a.All());
        Clear();
        Cmd(a, "house");
        Ok(a.All().Contains("House Varrow, this week:") || a.All().Contains("a house needs"), "/quest house", a.All());
    }

    static void AchievementsCommand()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        Hunt(a, "wolf", 7);
        Clear();
        Ach(a);
        Ok(a.All().Contains("Your deeds: 1 of ") && a.All().Contains("  Survival: 1/15 deeds, 1 tiers") && a.All().Contains("Closest:"), "/achievements: totals, kinds and the closest deeds", a.All());
        Ok(a.Popups.Count == 1 && a.Popups[0].StartsWith("Your Deeds|"), "in a window too");
        Clear();
        Ach(a, "survival");
        Ok(a.All().Contains("Deeds of Survival:") && a.All().Contains("  Wolfsbane (Bronze): Wolves slain."), "/achievements survival lists the kind", a.All());
        Clear();
        Ach(a, "wolfsbane");
        Ok(a.All().Contains("Wolfsbane (Survival)") && a.All().Contains("  Silver: 7/25"), "/achievements <name> details the tiers", a.All());
        Clear();
        Ach(a, "top");
        Ok(a.All().Contains("  1. Aldric - 1 tiers"), "/achievements top ranks the realm", a.All());
        Clear();
        Ach(a, "nonsense");
        Ok(a.All().Contains("ERR") && a.All().Contains("No deed or kind"), "an unknown name is refused", a.All());
    }

    static void AdminCommands()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        var b = Mk(76561198000000002, "Brannoc");
        Clear();
        Cmd(a, "admin", "status");
        Ok(a.All().Contains("ERR") && a.All().Contains("Only the realm's stewards"), "admin needs realmquests.admin");
        Admin(a);
        Clear();
        Cmd(a, "admin", "status");
        Ok(a.All().Contains("Content: 31 daily") && a.All().Contains("Files:") && a.All().Contains("players"), "/quest admin status", a.All());
        Clear();
        Cmd(a, "admin", "reload");
        Ok(a.All().Contains("Quest content read again."), "/quest admin reload");
        Hunt(b, "wolf", 1);
        Clear();
        Cmd(a, "admin", "creatures");
        Ok(a.All().Contains("Creature deaths seen lately: wolf"), "/quest admin creatures", a.All());
        Clear();
        Cmd(a, "admin", "items", "ingot");
        Ok(a.All().Contains("Steel Ingot") && a.All().Contains("Iron Ingot"), "/quest admin items <word>", a.All());
        Clear();
        Cmd(a, "admin", "places");
        Ok(a.All().Contains("the Old Throne (old_throne): not marked"), "/quest admin places", a.All());
        var before = DailyIds(b);
        SetF(Daily(b)[0], "Done", true);
        Clear();
        Cmd(a, "admin", "reset", "Brannoc", "daily");
        Ok(!(bool)F(Daily(b)[0], "Done") && a.All().Contains("Brannoc's daily quests are reset."), "/quest admin reset <player> daily", a.All());
        Clear();
        Cmd(a, "admin", "complete", "Brannoc", DailyIds(b)[1]);
        Ok(Done(Daily(b), 1), "/quest admin complete <player> <quest>", a.All());
        Clear();
        Cmd(a, "admin", "complete", "Brannoc", "nope");
        Ok(a.All().Contains("No open task called 'nope'"), "an unknown task is refused", a.All());
        Clear();
        Cmd(a, "admin", "reset", "Nobody");
        Ok(a.All().Contains("No player matches"), "an unknown player is refused", a.All());
    }

    static void Api()
    {
        Reset();
        NewQuests();
        var a = Mk(76561198000000001, "Aldric");
        Ok((bool)Inv(Q, "ReportQuestEvent", a.Id.ToString(), "event", "crown_night", 1), "ReportQuestEvent accepts event reports from other plugins");
        Ok(AchCount(a, "wa_crown_nights") == 1, "and counts them");
        Ok(!(bool)Inv(Q, "ReportQuestEvent", a.Id.ToString(), "slay_player", "", 5), "it refuses types the plugin sees for itself (no forged kills)");
        Ok(!(bool)Inv(Q, "ReportQuestEvent", "123", "event", "x", 1), "and a bad id");
        Ok((int)Inv(Q, "GetAchievementCount", a.Id.ToString()) == 1, "GetAchievementCount (Night of Crowns bronze)");
        Hunt(a, "wolf", 5);
        Ok((int)Inv(Q, "GetAchievementCount", a.Id.ToString()) == 2, "GetAchievementCount counts tiers");
        Season(0);
        Ok((string)Inv(Q, "GetStoryProgress", a.Id.ToString()) == "Act 1: The Empty Seat - Ash at the Hearth", "GetStoryProgress", (string)Inv(Q, "GetStoryProgress", a.Id.ToString()));
        HouseOf[a.Id] = "Varrow"; HouseOf[76561198000000002] = "Varrow";
        Ok(Inv(Q, "GetHouseGoalText", "Varrow") is string, "GetHouseGoalText draws a goal for any house it is asked about");
    }

    static void Config()
    {
        Reset();
        NewQuests(c => Set(c, "Enabled", false));
        var a = Mk(76561198000000001, "Aldric");
        Clear();
        Cmd(a);
        Ok(a.All().Contains("ERR") && a.All().Contains("closed by the realm's stewards"), "Enabled false: /quest is closed", a.All());
        Hunt(a, "wolf", 5);
        Ok(AchCount(a, "su_wolfsbane") == 0, "and nothing is tracked");
        Reset();
        NewQuests(c => { Set(c, "DailiesEnabled", false); Set(c, "MarksRewards", false); Set(c, "ItemRewards", false); Set(c, "RenownRewards", false); });
        a = Mk(76561198000000001, "Aldric");
        Ok(Daily(a).Count == 0 && Weekly(a).Count == 2, "DailiesEnabled false: no dailies");
        Clear();
        Hunt(a, "deer", 30);
        Ok(Pending(a) == 0 && !Purse.ContainsKey(a.Id.ToString()) && Deeds.Count == 0 && Has(a, "Cooked Meat") == 0, "MarksRewards, ItemRewards and RenownRewards off: no rewards of those kinds");
        Reset();
        NewQuests(c => { Set(c, "DailyCount", 99); Set(c, "TickSeconds", 1f); Set(c, "RewardScale", -3f); });
        Ok((int)F(F(Q, "config"), "DailyCount") == 6 && (float)F(F(Q, "config"), "TickSeconds") == 5f && (float)F(F(Q, "config"), "RewardScale") == 1f, "config values are clamped");
        Reset();
        NewQuests(c => Set(c, "RewardScale", 2f));
        a = Mk(76561198000000001, "Aldric");
        SetDaily(a, 0, "d_wolves");
        Hunt(a, "wolf", 3);
        Ok(Purse[a.Id.ToString()] >= 30, "RewardScale doubles marks rewards");
        Reset();
        NewQuests(c => Set(c, "AchievementsEnabled", false));
        a = Mk(76561198000000001, "Aldric");
        Clear();
        Ach(a);
        Ok(a.All().Contains("not recorded on this server"), "AchievementsEnabled false: /achievements says so");
    }
}
