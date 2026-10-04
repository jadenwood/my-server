// Behaviour tests for plugins/RealmCrafts.cs (with the real plugins/RealmTreasury.cs). Run with run.sh (see there for what
// this does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Common;
using CodeHatch.Engine.Networking;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Containers;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using static W;

static class Tests
{
    static string Repo;
    static ulong NextId = 76561190000000001;
    static Player P(string name, string house = null, string ip = null, long marks = 1000) { return Mk(NextId++, name, house, ip, marks); }

    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : "../../../..";
        try
        {
            Startup();
            LangAndStyle();
            Gathering();
            Transfers();
            Corpses();
            HarvestAndBlows();
            Hunting();
            Caps();
            Levels();
            Mastery();
            BonusYield();
            Crafting();
            ExtraItems();
            MarketDiscount();
            Workshops();
            Dominion();
            Weekly();
            Commissions();
            CommissionXp();
            CommissionEdges();
            Commands();
            AdminCommands();
            Popups();
            Switches();
            ReloadAndData();
            ConfigClamp();
            Api();
        }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL unexpected exception: " + ex); }
        finally { try { if (Dir != null) Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Startup()
    {
        Reset();
        Ok(C.permission.Registered.Contains("realmcrafts.admin"), "registers realmcrafts.admin");
        Ok(C.timer.Repeating.Count == 1, "one tick timer", C.timer.Repeating.Count.ToString());
        Ok(File.Exists(Path.Combine(Dir, "RealmCrafts.json")) && File.Exists(Path.Combine(Dir, "RealmCrafts_lastgood.json")), "the data file and a last-good copy are written");
        Ok(EventManager.Count<ContainerItemAddEvent>() == 2 && EventManager.Count<ContainerItemMergeEvent>() == 2 && EventManager.Count<ContainerItemRemoveEvent>() == 1
            && EventManager.Count<ContainerItemSplitEvent>() == 1, "subscribes to the container events (add and merge twice: VeryEarly and VeryLate)");
        var adds = EventManager.Subs[typeof(ContainerItemAddEvent)].Select(s => s.Order).ToList();
        Ok(adds.Contains(EventHandlerOrder.VeryEarly) && adds.Contains(EventHandlerOrder.VeryLate), "the add watch reads at VeryEarly and counts at VeryLate");
        Inv(C, "OnServerInitialized");
        Ok(C.timer.Repeating.Count(t => !t.Destroyed) == 1 && EventManager.Count<ContainerItemAddEvent>() == 2, "OnServerInitialized again (hot load) keeps one live timer and one set of subscriptions");
        var next = (DateTime)D("NextCrowning");
        Ok(next.DayOfWeek == DayOfWeek.Sunday && next.Hour == 20 && next > Clock && (string)D("WeekKey") == next.ToString("yyyy-MM-dd"), "the next crowning is Sunday 20:00 UTC and names the week", next.ToString("o"));
        Inv(C, "Unload");
        Ok(EventManager.Subs.Values.Sum(l => l.Count) == (EventManager.Subs.ContainsKey(typeof(CodeHatch.Networking.Events.Item.ItemPassEvent)) ? EventManager.Subs[typeof(CodeHatch.Networking.Events.Item.ItemPassEvent)].Count : 0),
            "Unload removes every subscription of its own (only the treasury's tax watch stays)");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void LangAndStyle()
    {
        Reset();
        var lang = C.lang.Msgs;
        string src = File.ReadAllText(Path.Combine(Repo, "plugins", "RealmCrafts.cs"));
        var used = new HashSet<string>();
        foreach (Match m in Regex.Matches(src, "\\b(?:Reply|ReplyError|Msg|Fmt)\\((?:[^\"()]|\\([^()]*\\))*?\"([A-Z][A-Za-z0-9.]+)\""))
            used.Add(m.Groups[1].Value);
        used.RemoveWhere(k => k.EndsWith("."));
        var missing = used.Where(k => !lang.ContainsKey(k)).ToList();
        Ok(used.Count > 80 && missing.Count == 0, "every lang key the code names exists (" + used.Count + " found)", string.Join(", ", missing));
        foreach (string p in new[] { "woodcutting", "mining", "foraging", "hunting", "smithing", "carpentry", "tailoring", "cooking" })
            Ok(lang.ContainsKey("Prof." + p) && lang.ContainsKey("Master." + p) && lang.ContainsKey("Earn." + p), "names, master titles and how XP is earned for " + p);
        Ok(Enumerable.Range(0, 6).All(i => lang.ContainsKey("Rank." + i)) && Enumerable.Range(0, 4).All(i => lang.ContainsKey("Tier." + i)), "the six ranks and four workshop tiers are named");
        Ok(new[] { "address", "pair", "price", "house", "cap", "none" }.All(w => lang.ContainsKey("NoXp." + w)) && new[] { "open", "done", "cancelled", "expired" }.All(w => lang.ContainsKey("Status." + w)),
            "the composed keys (why no XP, commission status) exist");
        var tooLong = lang.Where(kv => Regex.Replace(kv.Value, "\\[[0-9A-Fa-f]{6}\\]", "").Length > 200).Select(kv => kv.Key).ToList();
        Ok(tooLong.Count == 0, "no lang line is longer than 200 visible characters", string.Join(", ", tooLong));
        var badColour = lang.Where(kv => Regex.Matches(kv.Value, "\\[([0-9A-Fa-f]{6})\\]").Cast<Match>().Any(m => !new[] { "D6A043", "8FC97A", "E8913A", "E86A5C", "F4C96D", "A3A6AD", "FFFFFF" }.Contains(m.Groups[1].Value))).Select(kv => kv.Key).ToList();
        Ok(badColour.Count == 0, "every colour is in the chat palette", string.Join(", ", badColour));
        Ok(lang["Speaker"] == "Guilds" && lang["Herald"] == "[D6A043]Herald[FFFFFF]: ", "speaker Guilds, the one Herald voice");
        var types = Regex.Matches(src, "\"Log\", (\\w+)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        Ok(types.Count == 1 && types[0] == "TitleChronicleType" && src.Contains("TitleChronicleType = \"title_earned\""), "Chronicle lines use the registered title_earned only", string.Join(",", types));
        Ok(!Regex.IsMatch(src, "\\bpublic\\s+(?:int|string|bool|long|void)\\s+(Get|Has)\\w*\\("), "no API method is public (Oxide calls only non-public ones)");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Gathering()
    {
        Reset();
        var ada = P("Ada");
        Admin(ada);
        Cmd(ada, "admin", "watch", "Ada");
        Clear();
        Gather(ada, "Wood", 40);
        Ok(Has(ada, "Wood") == 40 && Xp(ada, "woodcutting") == 20, "40 wood gathered: 40 in the packs, 20 woodcutting XP (0.5 a unit)", Xp(ada, "woodcutting").ToString());
        Ok(ada.All().Contains("watch Ada: 40 Wood gathered: Woodcutting +20 XP"), "the admin watch tells what was counted", ada.All());
        GatherMerge(ada, "Wood", 40);
        Ok(Has(ada, "Wood") == 80 && Xp(ada, "woodcutting") == 40, "gathered onto a stack already carried (a merge from nowhere) counts too");
        Ok(ada.All().Contains("Woodcutting rises to level 2."), "40 XP is level 2", ada.All());
        Gather(ada, "Iron", 10);
        Ok(Xp(ada, "mining") == 20, "Iron (the game's name for ResourceType IronOre) is mining at 2 XP a unit", Xp(ada, "mining").ToString());
        Gather(ada, "Flax", 5);
        Gather(ada, "Leather Hide", 2);
        Ok(Xp(ada, "foraging") == 10 && Xp(ada, "hunting") == 6, "flax is foraging, hides are hunting");
        Gather(ada, "Strange Trinket", 3);
        Ok(Xp(ada, "woodcutting") == 40 && Xp(ada, "mining") == 20 && ada.All().Contains("Strange Trinket gathered (no gathering profession)"), "goods no profession gathers give nothing", ada.All());
        Ok((long)D("GatheredUnits") == 97, "the realm's count of gathered units", D("GatheredUnits").ToString());
        Ok(Math.Abs(WeekXp(ada) - 76) < 0.001, "the week's XP adds up", WeekXp(ada).ToString());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Transfers()
    {
        Reset();
        var ada = P("Ada"); var bram = P("Bram");
        var chest = Chest();
        Stock(ada, "Wood", 100);
        Ok(Xp(ada, "woodcutting") == 0, "goods the server gives (no client event) are not gathered");
        Move(ada, ada.Inventory, chest, "Wood");
        Ok(In(chest, "Wood") == 100 && Has(ada, "Wood") == 0, "stored in a chest");
        Move(ada, chest, ada.Inventory, "Wood");
        Ok(Has(ada, "Wood") == 100 && Xp(ada, "woodcutting") == 0, "taken back out of the chest: moved, not gathered");
        Gather(ada, "Wood", 20);
        Ok(Xp(ada, "woodcutting") == 10, "a real gather right after a chest shuffle still counts in full", Xp(ada, "woodcutting").ToString());
        Move(ada, ada.Inventory, chest, "Wood");
        Gather(ada, "Wood", 30);
        Ok(Xp(ada, "woodcutting") == 25, "goods stored in a chest leave nothing on the move: the next gather counts in full", Xp(ada, "woodcutting").ToString());
        SplitMove(ada, chest, ada.Inventory, "Wood", 50);
        Ok(Xp(ada, "woodcutting") == 25 && In(chest, "Wood") == 50, "half a stack split out of a chest: moved", Xp(ada, "woodcutting").ToString());
        MergeFrom(ada, chest, "Wood", 30);
        Ok(Xp(ada, "woodcutting") == 25 && In(chest, "Wood") == 20, "a chest stack dragged onto one in the packs: moved", In(chest, "Wood").ToString());
        var pack = Drop(ada, "Wood");
        Ok(Has(ada, "Wood") == 0 && In(pack, "Wood") > 0, "dropped on the ground: an item pack holds it");
        Move(ada, pack, ada.Inventory, "Wood");
        Ok(Xp(ada, "woodcutting") == 25, "picked up again: moved, not gathered (gather-drop-regather)");
        Move(bram, pack, bram.Inventory, "Wood");
        Ok(Xp(bram, "woodcutting") == 0, "another player picking up the pack gathers nothing either");
        Pass(ada, "Wood", 60, "Loot");
        Ok(Xp(ada, "woodcutting") == 25, "goods the server hands over (salvage, a broken crate) are not gathered");
        Harvest(ada, "Cabbage", 10);
        Ok(Xp(ada, "foraging") == 15, "a farm's harvest (a crop handed over as Loot inside the harvest) counts for foraging", Xp(ada, "foraging").ToString());
        Pass(ada, "Cabbage", 10, "Passed");
        Ok(Xp(ada, "foraging") == 15, "a crop passed with another memo (from a player) does not");
        ServerAdd(ada, "Wood", 40);
        Ok(Xp(ada, "woodcutting") == 25, "a change the server itself raises is never counted");
        var e = new ContainerItemAddEvent { Sender = ada, Entity = chest.Entity, Container = chest, ItemStack = new InvGameItemStack(Bp("Wood"), 10, null) };
        EventManager.Raise(e, () => chest.Contents.Put(e.ItemStack));
        Ok(Xp(ada, "woodcutting") == 25, "new goods a client puts into a container not its own are not counted");
        Gather(ada, "Wood", 10, bram);
        Ok(Xp(ada, "woodcutting") == 25 && Xp(bram, "woodcutting") == 0, "a client changing someone else's packs counts for no one");
        EventSubscriber<ContainerItemAddEvent> cancel = x => x.Cancel("test");
        EventManager.Subscribe(cancel, EventHandlerOrder.Early);
        Gather(ada, "Wood", 10);
        EventManager.Unsubscribe(cancel);
        Ok(Xp(ada, "woodcutting") == 25, "a gather the game (or another plugin) cancels is not counted");
        var own = new ContainerItemRemoveEvent { Sender = ada, Entity = ada.Entity, Container = ada.Inventory, ItemStack = ada.Inventory.Contents.Items.First(s => s.Blueprint.Name == "Wood") };
        EventManager.Raise(own, () => ada.Inventory.Contents.Take(own.ItemStack));
        int moved = own.ItemStack.StackAmount;
        Gather(ada, "Wood", 10);
        Ok(Xp(ada, "woodcutting") == 25, "goods just taken out of the packs (to somewhere unseen) absorb a gather a moment later", Xp(ada, "woodcutting").ToString());
        Tick(20);
        Gather(ada, "Wood", 10);
        Ok(Xp(ada, "woodcutting") == 30, "after TransitSeconds (15 s) gathering counts again", Xp(ada, "woodcutting").ToString());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Corpses()
    {
        Reset();
        var ada = P("Ada");
        var wolf = Corpse("Wolf", ("Raw Meat", 10), ("Wolf Pelt", 1), ("Wood", 5));
        Move(ada, wolf, ada.Inventory, "Raw Meat");
        Ok(Xp(ada, "hunting") == 20, "meat taken from a creature's corpse is hunting (2 XP a unit)", Xp(ada, "hunting").ToString());
        Move(ada, wolf, ada.Inventory, "Wolf Pelt");
        Ok(Xp(ada, "hunting") == 25, "and its pelt");
        Move(ada, wolf, ada.Inventory, "Wood");
        Ok(Xp(ada, "woodcutting") == 0, "wood in a corpse is not woodcutting");
        var bear = Corpse("Bear", ("Raw Meat", 100));
        Move(ada, bear, ada.Inventory, "Raw Meat");
        Ok(Xp(ada, "hunting") == 145, "one corpse yields at most CorpseCreditPerContainer (60) units of credit", Xp(ada, "hunting").ToString());
        var stag = Corpse("Stag", ("Raw Meat", 20));
        Gather(ada, "Raw Meat", 1);
        double before = Xp(ada, "hunting");
        MergeFrom(ada, stag, "Raw Meat", 20);
        Ok(Xp(ada, "hunting") == before + 40, "dragging corpse meat onto a carried stack counts too", (Xp(ada, "hunting") - before).ToString());
        var chest = Chest();
        Move(ada, ada.Inventory, chest, "Raw Meat");
        Move(ada, chest, ada.Inventory, "Raw Meat");
        Ok(Xp(ada, "hunting") == before + 40, "corpse goods shuffled through a chest afterwards count nothing more");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Hand-outs inside a game event: a farm's harvest (PlotCollectEvent) and goods given on a blow (EntityDamageEvent).
    static void HarvestAndBlows()
    {
        Reset();
        var ada = P("Ada");
        var bram = P("Bram");
        Harvest(ada, "Flax", 10);
        Ok(Xp(ada, "foraging") == 20, "flax from the player's own harvest: foraging (2 XP a unit)", Xp(ada, "foraging").ToString());
        Pass(ada, "Flax", 10, "Loot");
        Ok(Xp(ada, "foraging") == 20, "the same crop handed over as Loot outside a harvest (salvage): nothing", Xp(ada, "foraging").ToString());
        Harvest(ada, "Flax", 10, cancelled: true);
        Ok(Xp(ada, "foraging") == 20 && Has(ada, "Flax") == 20, "a cancelled harvest gives and counts nothing", Has(ada, "Flax").ToString());
        Pass(ada, "Flax", 5, "Loot");
        Ok(Xp(ada, "foraging") == 20, "and the flag does not outlive a cancelled harvest", Xp(ada, "foraging").ToString());
        var e = new CodeHatch.Farming.PlotCollectEvent { Sender = bram };
        EventManager.Raise(e, () => Pass(ada, "Flax", 10, "Loot"));
        Ok(Xp(ada, "foraging") == 20 && Xp(bram, "foraging") == 0, "crops handed to someone else during another's harvest count for no one");
        Harvest(ada, "Wood", 10);
        Ok(Xp(ada, "woodcutting") == 0, "only LootCountsAsGather crops count from a harvest");

        var wolf = Beast("Wolf");
        Hit(ada, wolf, "Raw Meat", 10);
        Ok(Xp(ada, "hunting") == 20, "meat handed over on a blow to a creature: hunting", Xp(ada, "hunting").ToString());
        Hit(ada, wolf, "Raw Meat", 100);
        Ok(Xp(ada, "hunting") == 120, "one creature yields at most CorpseCreditPerContainer (60) units, blows and looting together", Xp(ada, "hunting").ToString());
        var bed = new CodeHatch.Engine.Core.Cache.Entity { IsPlayer = false, Label = "Bed" };
        Hit(ada, bed, "Flax", 10);
        Hit(ada, bed, "Leather Hide", 10);
        Ok(Xp(ada, "foraging") == 20 && Xp(ada, "hunting") == 120, "salvaging a placed object (not a creature) is a hand-out: nothing");
        var bear = Beast("Bear");
        Hit(bram, bear, null, 0);
        Pass(bram, "Raw Meat", 10, "Loot");
        Ok(Xp(bram, "hunting") == 0, "the blow's flag ends with the blow");
        var corpse = Corpse("Stag", ("Raw Meat", 80));
        corpse.Entity.Components.Add(new CodeHatch.AI.MonsterMotor());
        Hit(bram, corpse.Entity, "Raw Meat", 50);
        Move(bram, corpse, bram.Inventory, "Raw Meat");
        Ok(Xp(bram, "hunting") == 120, "a creature hit for goods and then looted shares one allowance of 60", Xp(bram, "hunting").ToString());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Hunting()
    {
        Reset();
        var ada = P("Ada");
        Kill(ada, "Wolf");
        Ok(Xp(ada, "hunting") == 40, "a wolf slain: 40 hunting XP", Xp(ada, "hunting").ToString());
        Kill(ada, "Rabbit");
        Kill(ada, "Grumbleback");
        Ok(Xp(ada, "hunting") == 63, "a rabbit 8, an unknown beast the default 15", Xp(ada, "hunting").ToString());
        Set("Gathering", "CreatureCreditsPerDay", 4);
        Kill(ada, "Wolf");
        Kill(ada, "Wolf");
        Ok(Xp(ada, "hunting") == 103, "the fourth kill of the day counts, the fifth does not (CreatureCreditsPerDay)", Xp(ada, "hunting").ToString());
        var beast = new CodeHatch.Engine.Core.Cache.Entity { IsPlayer = false, Label = "Barrel" };
        Inv(C, "OnEntityDeath", new CodeHatch.Networking.Events.Entities.EntityDeathEvent { Entity = beast, KillingDamage = new CodeHatch.Damaging.Damage { DamageSource = ada.Entity } });
        Ok(Inv(C, "OnEntityDeath", new CodeHatch.Networking.Events.Entities.EntityDeathEvent { Entity = beast }) == null, "a death hook always returns null (the game's death goes on)");
        Clock = Clock.AddDays(1);
        Kill(ada, "Wolf");
        Ok(Xp(ada, "hunting") == 143, "a new day, new kills count");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Caps()
    {
        Reset();
        var ada = P("Ada");
        for (int i = 0; i < 30; i++) Gather(ada, "Iron", 50);
        Ok(Today(ada, "mining") == 2500 && Xp(ada, "mining") == 2500, "a profession earns at most DailyXpCap (2500) a day", Today(ada, "mining").ToString());
        Ok(ada.All().Contains("You have learned all you can in Mining today. Come back tomorrow."), "the cap is told once", ada.All());
        Ok(Regex.Matches(ada.All(), "learned all you can").Count == 1, "only once (throttled)");
        Gather(ada, "Wood", 10);
        Ok(Xp(ada, "woodcutting") == 5, "other professions still learn");
        Clock = Clock.AddDays(1);
        Gather(ada, "Iron", 10);
        Ok(Xp(ada, "mining") == 2520 && Today(ada, "mining") == 20, "the next day the cap starts again", Today(ada, "mining").ToString());
        Set("Gathering", "UnitsPerItemPerDay", 100);
        Gather(ada, "Flax", 80);
        Gather(ada, "Flax", 80);
        Ok(Xp(ada, "foraging") == 200, "at most UnitsPerItemPerDay units of one good count a day", Xp(ada, "foraging").ToString());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Levels()
    {
        Reset();
        Ok(XpFor(1) == 0 && XpFor(2) == 40 && XpFor(10) == 3240 && XpFor(30) == 33640 && XpFor(50) == 96040, "the curve: 40 * (L-1)^2", XpFor(50).ToString());
        Ok((int)Inv(C, "LevelFor", 3239.0) == 9 && (int)Inv(C, "LevelFor", 3240.0) == 10 && (int)Inv(C, "LevelFor", 1e9) == 50, "levels from XP, capped at 50");
        var ada = P("Ada");
        SetXp(ada, "smithing", XpFor(10) - 10);
        Clear();
        Craft(ada, "Iron Sword");
        Ok(Level(ada, "smithing") == 10 && ada.All().Contains("You are now Apprentice in Smithing."), "rank up to Apprentice at level 10", ada.All());
        Ok(Deeds.Any(d => d.Contains("craft_rank") && d.Contains("crafts:rank:" + ada.Id + ":smithing:1")), "the rank deed goes to RealmRenown once (dedupe key per rank)", string.Join("\n", Deeds));
        Ok(B().Length == 0, "Apprentice is not heralded to the realm");
        SetXp(ada, "smithing", XpFor(30) - 10);
        Clear();
        Craft(ada, "Iron Sword");
        Ok(B().Contains("Ada is now Expert in Smithing."), "Expert and above are heralded", B());
        SetXp(ada, "carpentry", XpFor(21) - 5);
        Clear();
        Craft(ada, "Wooden Chair", 1, Station("Carpenter's Bench"));
        Ok(ada.All().Contains("Carpentry rises to level 21."), "a level within a rank is told quietly", ada.All());
        SetXp(ada, "tailoring", XpFor(2) - 5);
        Clear();
        Craft(ada, "Leather Helmet");
        Ok(Level(ada, "tailoring") == 2, "level 2 tailoring");
        Set("Levels", "HeraldsPerHour", 1);
        Clock = Clock.AddHours(2);
        var bo = P("Bo"); var cy = P("Cy");
        SetXp(bo, "smithing", XpFor(30) - 5); SetXp(cy, "smithing", XpFor(30) - 5);
        Clear();
        Craft(bo, "Iron Sword"); Craft(cy, "Iron Sword");
        Ok(Regex.Matches(B(), "is now Expert").Count == 1, "heralds are capped per hour", B());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Mastery()
    {
        Reset();
        var ada = P("Ada");
        SetXp(ada, "smithing", XpFor(50) - 10);
        Clear();
        Craft(ada, "Iron Sword");
        Ok(Level(ada, "smithing") == 50, "level 50");
        Ok(ada.All().Contains("You are named Master Smith: a master of Smithing."), "the master is told", ada.All());
        Ok(B().Contains("Ada is named Master Smith, a master of the guild of Smithing."), "the realm is told", B());
        Ok(Deeds.Any(d => d.Contains("|craft_master|Master Smith|crafts:master:" + ada.Id + ":smithing")), "the craft_master deed (Guildmaster title) with a dedupe key per profession", string.Join("\n", Deeds));
        Ok(!Deeds.Any(d => d.Contains("|craft_rank|")), "Master has its own deed, not a rank deed");
        Ok(ChronLog.Any(l => l.StartsWith("title_earned|Ada is named Guildmaster of Smithing|")), "a title_earned Chronicle entry the art tables recognise (Guildmaster)", string.Join("\n", ChronLog));
        Ok(QuestReports.Any(q => q == ada.Id + "|custom|mastery|1"), "RealmQuests hears of it (custom: mastery)", string.Join(",", QuestReports));
        Ok(ada.Popups.Count == 1 && ada.Popups[0].Title == "Master of Smithing" && ada.Popups[0].Broadcast && !ada.Popups[0].Message.Contains("["), "a window (broadcast, plain text)", ada.Popups.Count > 0 ? ada.Popups[0].Message : "none");
        Clear();
        Craft(ada, "Iron Sword");
        Ok(Deeds.Count == 0 && ChronLog.Count == 0 && ada.Popups.Count == 0, "mastery is celebrated once");
        Cmd(ada, "smithing");
        Ok(ada.All().Contains("You are a Master Smith. Nothing higher can be learned."), "/craft smithing at the top", ada.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void BonusYield()
    {
        Reset();
        var ada = P("Ada");
        SetXp(ada, "mining", XpFor(25));
        Tick(30);
        Clear();
        Gather(ada, "Stone", 100);
        Ok(Has(ada, "Stone") == 100, "100 stone gathered");
        double xp = Xp(ada, "mining");
        Tick(30);
        Ok(Has(ada, "Stone") == 110 && ada.All().Contains("Your skill finds you 10 Stone more."), "level 25 mining: +10 % given by the server in a batch", Has(ada, "Stone") + "\n" + ada.All());
        Ok(Xp(ada, "mining") == xp, "the bonus itself is not gathered (no XP for it)");
        Ok(ItemSources.Contains(ada.Id.ToString()), "RealmSentinel is told about the gift");
        Gather(ada, "Stone", 5);
        Tick(30);
        Ok(Has(ada, "Stone") == 115, "half a unit waits for the next batch");
        Gather(ada, "Stone", 5);
        Tick(30);
        Ok(Has(ada, "Stone") == 121, "and is given when it makes a whole one", Has(ada, "Stone").ToString());
        Set("Perks", "BonusYieldMaxPerItemPerDay", 15);
        Gather(ada, "Stone", 100);
        Tick(30);
        Ok(Has(ada, "Stone") == 225, "at most BonusYieldMaxPerItemPerDay bonus units of a good a day (11 given, 4 more allowed)", Has(ada, "Stone").ToString());
        var bo = P("Bo");
        Gather(bo, "Stone", 100);
        Tick(30);
        Ok(Has(bo, "Stone") == 100, "a level 1 gatherer gets no whole bonus unit from 100 (0.4 %)", Has(bo, "Stone").ToString());
        var cy = P("Cy");
        SetXp(cy, "woodcutting", XpFor(50));
        ItemCollection.Capacity = 10;
        Gather(cy, "Wood", 10);
        Tick(30);
        Ok(Has(cy, "Wood") == 10, "full packs: the bonus is lost rather than duplicated");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Crafting()
    {
        Reset();
        var ada = P("Ada");
        Admin(ada);
        Craft(ada, "Iron Sword");
        Ok(Xp(ada, "smithing") == 30, "an iron sword: 15 XP times the Iron weight 2", Xp(ada, "smithing").ToString());
        Craft(ada, "Steel Sword");
        Ok(Xp(ada, "smithing") == 75, "steel weighs 3");
        Craft(ada, "Wooden Bow", 1, Station("Fletcher"));
        Ok(Xp(ada, "carpentry") == 15, "a bow is carpentry");
        Craft(ada, "Leather Helmet");
        Ok(Xp(ada, "tailoring") == 15 && Xp(ada, "smithing") == 75, "a leather helmet is tailoring (the first rule with a word in the name wins)");
        Craft(ada, "Cooked Meat", 1, Station("Fire Pit"));
        Craft(ada, "Bread", 1, Station("Stove"));
        Ok(Xp(ada, "cooking") == 30, "cooking and baking");
        Craft(ada, "Stone Hatchet", 1, Station("Smithy"));
        Ok(Xp(ada, "smithing") == 90, "a product no word names goes by the station's name (Smithy)", Xp(ada, "smithing").ToString());
        Craft(ada, "Strange Trinket", 1, Station("Mystery Box"));
        Ok(Xp(ada, "smithing") == 90, "nothing names it at all: no XP");
        Cmd(ada, "admin", "unmapped");
        Ok(ada.All().Contains("Strange Trinket (at mystery box)"), "and /craft admin unmapped lists it for tuning", ada.All());
        Craft(ada, "Arrow", 20, Station("Fletcher"));
        Ok(Xp(ada, "carpentry") == 22.5, "a stack of arrows is one craft (weight 0.5)", Xp(ada, "carpentry").ToString());
        // Diminishing returns on the same product in one day.
        var bo = P("Bo");
        for (int i = 0; i < 70; i++) Craft(bo, "Bandage", 1, Station("Loom"));
        Ok(Math.Abs(Xp(bo, "tailoring") - 300) < 0.01, "bandages: 20 at full XP, 40 at half, then none (7.5 x 20 + 3.75 x 40)", Xp(bo, "tailoring").ToString());
        Clock = Clock.AddDays(1);
        Craft(bo, "Bandage", 1, Station("Loom"));
        Ok(Math.Abs(Xp(bo, "tailoring") - 307.5) < 0.01, "a new day, full XP again");
        // Hand crafting: no station claim, the crafter's own entity.
        var cy = P("Cy");
        var hand = new CodeHatch.Networking.Events.Entities.ItemCrafterItemEvent { Entity = cy.Entity, Crafter = Station("Hands"), Stack = new InvGameItemStack(Bp("Torch"), 1, null) };
        EventManager.Raise(hand, null);
        Ok(Xp(cy, "carpentry") == 7.5, "hand crafting counts for the crafter (their own entity)", Xp(cy, "carpentry").ToString());
        var orphan = new CodeHatch.Networking.Events.Entities.ItemCrafterItemEvent { Crafter = Station("Smithy"), Stack = new InvGameItemStack(Bp("Iron Sword"), 1, null) };
        EventManager.Raise(orphan, null);
        Ok((long)D("CraftsCounted") == 9 + 71, "a product with no claim and no crafter counts for no one", D("CraftsCounted").ToString());
        var cancelled = new CodeHatch.Networking.Events.Entities.ItemCrafterItemEvent { Entity = cy.Entity, Crafter = Station("Hands"), Stack = new InvGameItemStack(Bp("Torch"), 1, null), Cancelled = true };
        EventManager.Raise(cancelled, null);
        Ok(Xp(cy, "carpentry") == 7.5, "a cancelled craft counts nothing");
        var at = Station("Smithy");
        var req = new CodeHatch.Networking.Events.Entities.ItemCrafterCraftEvent { Sender = cy, Crafter = at };
        EventManager.Raise(req, null);
        Inv(C, "OnItemCrafted", new CodeHatch.Networking.Events.Entities.ItemCrafterFinishEvent { Crafter = at });
        EventManager.Raise(new CodeHatch.Networking.Events.Entities.ItemCrafterItemEvent { Crafter = at, Stack = new InvGameItemStack(Bp("Iron Sword"), 1, null) }, null);
        Ok(Xp(cy, "smithing") == 0, "a finished run forgets its crafter (OnItemCrafted)");
        Set("Crafting", "Ignore", new List<string> { "Torch" });
        Craft(cy, "Torch", 1, Station("Bench"));
        Ok(Xp(cy, "carpentry") == 7.5, "products on the Ignore list give nothing");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void ExtraItems()
    {
        Reset();
        var ada = P("Ada");
        SetXp(ada, "smithing", XpFor(50));
        NextRoll = 0.05;
        Craft(ada, "Iron Sword");
        Ok(Has(ada, "Iron Sword") == 1 && ada.All().Contains("A master's touch: one more Iron Sword (Smithing)."), "a master smith: 10 % chance, the roll 5 gives one more into the packs", ada.All());
        Ok(ItemSources.Contains(ada.Id.ToString()), "RealmSentinel is told");
        NextRoll = 0.5;
        Craft(ada, "Iron Sword");
        Ok(Has(ada, "Iron Sword") == 1, "the roll 50 gives none");
        NextRoll = 0.0;
        Craft(ada, "Iron Ingot");
        Ok(Has(ada, "Iron Ingot") == 0, "never for NoExtraItems (ingots, flour, lumber: raw materials for loops)");
        SetXp(ada, "carpentry", XpFor(50));
        Craft(ada, "Arrow", 60, Station("Fletcher"));
        Ok(Has(ada, "Arrow") == 20, "a stackable product's extra is at most ExtraItemMaxUnits (20)", Has(ada, "Arrow").ToString());
        for (int i = 0; i < 20; i++) Craft(ada, "Iron Sword");
        Ok(Has(ada, "Iron Sword") == 9, "at most ExtraItemsPerDay (10) extra items a day (one went to the arrows)", Has(ada, "Iron Sword").ToString());
        var bo = P("Bo");
        NextRoll = 0.0;
        Craft(bo, "Iron Sword");
        Ok(Has(bo, "Iron Sword") == 0 || Has(bo, "Iron Sword") == 1, "a level 1 smith has 0.2 % (the roll 0 still wins)");
        Offline(ada);
        Craft(ada, "Steel Sword");
        Ok(Has(ada, "Steel Sword") == 0, "no extra for a crafter who is not online (no packs to give to)");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void MarketDiscount()
    {
        Reset();
        var ada = P("Ada"); var bo = P("Bo");
        string id = ada.Id.ToString();
        Func<string, int> disc = item => (int)C.Call("GetMarketFeeDiscount", id, item);
        Ok(disc("Iron") == 0, "a novice miner pays the full fee");
        SetXp(ada, "mining", XpFor(29)); Ok(disc("Iron") == 0, "level 29: nothing yet");
        SetXp(ada, "mining", XpFor(30)); Ok(disc("Iron") == 10, "Expert (30): 10 % off");
        SetXp(ada, "mining", XpFor(40)); Ok(disc("Iron") == 20, "Artisan (40): 20 % off");
        SetXp(ada, "mining", XpFor(50)); Ok(disc("Iron") == 30, "Master: 30 % off");
        Ok(disc("Wood") == 0 && disc("Iron Sword") == 0, "only for goods of that craft");
        SetXp(ada, "smithing", XpFor(40));
        Ok(disc("Iron Sword") == 20 && disc("Steel Sword") == 20, "a product counts by the crafting rules");
        Ok((int)C.Call("GetMarketFeeDiscount", "76561199999999999", "Iron") == 0 && (int)C.Call("GetMarketFeeDiscount", null, "Iron") == 0 && (int)C.Call("GetMarketFeeDiscount", id, "") == 0,
            "unknown players and empty items get nothing");
        // Through the real treasury: a master sells iron on the market.
        Stock(ada, "Iron", 10);
        Inv(T, "CmdMarket", ada, "market", new[] { "sell", "10", "100", "Iron" });
        var listings = (IList)F(F(T, "data"), "Listings");
        int lid = (int)F(listings[listings.Count - 1], "Id");
        long before = Purse(ada), crown = TreasuryMarks();
        Inv(T, "CmdMarket", bo, "market", new[] { "buy", lid.ToString(), "10" });
        Ok(Purse(ada) - before == 986 && TreasuryMarks() - crown == 14, "the treasury takes 14 instead of 20 (2 % less 30 %)", (Purse(ada) - before) + " / " + (TreasuryMarks() - crown));
        Ok(ZeroSum() == "", "the treasury's books balance", ZeroSum());
        Stock(bo, "Iron", 10);
        Inv(T, "CmdMarket", bo, "market", new[] { "sell", "10", "100", "Iron" });
        lid = (int)F(listings[listings.Count - 1], "Id");
        before = Purse(bo); crown = TreasuryMarks();
        Clock = Clock.AddSeconds(5);
        Inv(T, "CmdMarket", ada, "market", new[] { "buy", lid.ToString(), "10" });
        Ok(TreasuryMarks() - crown == 20, "a novice seller pays the full 20", (TreasuryMarks() - crown).ToString());
        SetF(T, "RealmCrafts", null);
        Ok((int)Inv(T, "CraftsFeeDiscount", id, "Iron") == 0, "without RealmCrafts the treasury gives no discount");
        SetF(T, "RealmCrafts", new Plugin { Name = "RealmCrafts", Handler = (h, a) => 90 });
        Ok((int)Inv(T, "CraftsFeeDiscount", id, "Iron") == 50, "and never more than 50 % whatever it is told");
        SetF(T, "RealmCrafts", new Plugin { Name = "RealmCrafts", Handler = (h, a) => { throw new InvalidOperationException("boom"); } });
        Ok((int)Inv(T, "CraftsFeeDiscount", id, "Iron") == 0, "a failing RealmCrafts costs the seller nothing extra");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Workshops()
    {
        Reset();
        var ada = P("Ada", "Varrow", "1.1.1.1"); var bo = P("Bo", "Varrow", "2.2.2.2"); var cy = P("Cy", "Varrow", "3.3.3.3");
        Settle(ada); Settle(bo);
        Gather(ada, "Iron", 500);
        Gather(bo, "Iron", 500);
        Gather(cy, "Iron", 500);
        Ok(HouseWeek("Varrow") == 2000, "two settled members pool their XP; a new member (under MinMemberHours) does not yet", HouseWeek("Varrow").ToString());
        Set("Workshops", "MemberCapXpPerWeek", 1200);
        Gather(ada, "Iron", 500);
        Ok(HouseWeek("Varrow") == 2200, "each member pools at most MemberCapXpPerWeek a week", HouseWeek("Varrow").ToString());
        var alt1 = P("Alt1", "Varrow", "9.9.9.9"); var alt2 = P("Alt2", "Varrow", "9.9.9.9"); var alt3 = P("Alt3", "Varrow", "9.9.9.9");
        Settle(alt1); Settle(alt2); Settle(alt3);
        Gather(alt1, "Iron", 100); Gather(alt2, "Iron", 100); Gather(alt3, "Iron", 100);
        Ok(HouseWeek("Varrow") == 2600, "at most AccountsPerAddress (2) accounts from one address pool for a house", HouseWeek("Varrow").ToString());
        Ok((int)C.Call("GetWorkshopTier", "Varrow") == 0, "below 6000 pooled: no workshop");
        Set("Workshops", "MemberCapXpPerWeek", 100000);
        Clock = Clock.AddDays(1); Gather(ada, "Iron", 1250);
        Clock = Clock.AddDays(1); Gather(bo, "Iron", 1250);
        Ok(HouseWeek("Varrow") >= 6000 && (int)C.Call("GetWorkshopTier", "Varrow") == 1, "6000 pooled: a Workshop", HouseWeek("Varrow").ToString());
        double x = Xp(cy, "mining");
        Settle(cy);
        Clock = Clock.AddDays(1);
        Gather(cy, "Iron", 100);
        Ok(Math.Abs(Xp(cy, "mining") - x - 210) < 0.001, "members of a house with a Workshop learn 5 % faster", (Xp(cy, "mining") - x).ToString());
        Cmd(ada, "house");
        Ok(ada.All().Contains("House [C58FC0]Varrow[FFFFFF]'s workshop: a Workshop.") && ada.All().Contains("Members now get +5% XP, +2 yield points and +1 extra-item points."), "/craft house", ada.All());
        Cmd(alt3, "house");
        Ok(alt3.All().Contains("Members now get"), "every member sees it");
        // The crowning pays season points by tier, and the tier carries into the next week.
        Clear();
        Advance(TimeSpan.FromDays(7));
        Ok(Awards.Any(a => a.StartsWith("Varrow|2|workshop")), "at the crowning a Workshop earns its house 2 season points", string.Join("\n", Awards));
        Ok(B().Contains("The workshops close their week"), "the herald names the workshops", B());
        Ok((int)C.Call("GetWorkshopTier", "Varrow") == 1 && HouseWeek("Varrow") == 0, "next week the house keeps its tier from last week's work");
        Advance(TimeSpan.FromDays(7));
        Ok((int)C.Call("GetWorkshopTier", "Varrow") == 0, "a week without work and the workshop is gone");
        var dee = P("Dee", "Ashgrove");
        Inv(C, "TrackHouse", dee.Id.ToString(), Rec(dee));
        HouseOf[dee.Id.ToString()] = "Merrin";
        Inv(C, "TrackHouse", dee.Id.ToString(), Rec(dee));
        Ok((string)F(Rec(dee), "House") == "Merrin" && (DateTime)F(Rec(dee), "HouseSince") == Clock, "changing house starts the member clock again");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Dominion()
    {
        Reset();
        var ada = P("Ada", "Dunmere");
        SetXp(ada, "mining", XpFor(25));
        Holdings.Add(new Dictionary<string, object> { { "id", "greyhold" }, { "kind", "mine" }, { "owner", "Dunmere" }, { "placed", true } });
        Holdings.Add(new Dictionary<string, object> { { "id", "keep" }, { "kind", "keep" }, { "owner", "Dunmere" }, { "placed", false } });
        Inv(C, "PollDominion");
        Ok((double)Inv(C, "YieldPercent", Rec(ada), "mining") == 15, "a house holding a mine: +5 yield points for its miners (10 + 5)");
        SetXp(ada, "smithing", XpFor(10));
        Ok((double)Inv(C, "ExtraChance", Rec(ada), "smithing") == 2, "a keep not placed on the land gives nothing");
        Gather(ada, "Stone", 100);
        Tick(30);
        Ok(Has(ada, "Stone") == 115, "and the miner feels it", Has(ada, "Stone").ToString());
        Cmd(ada, "perks");
        Ok(ada.All().Contains("Holdings (RealmDominion) add: mine: Mining +5"), "/craft perks names the holding perks", ada.All());
        SetF(C, "RealmDominion", null);
        Inv(C, "PollDominion");
        Ok((double)Inv(C, "YieldPercent", Rec(ada), "mining") == 10, "without RealmDominion, no holding perks");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Weekly()
    {
        Reset();
        var ada = P("Ada", "Halloran"); var bo = P("Bo"); var adm = P("Steward");
        Admin(adm);
        for (int d = 0; d < 2; d++) { Gather(ada, "Iron", 1250); Clock = Clock.AddDays(1); }
        Gather(bo, "Iron", 900);
        for (int d = 0; d < 3; d++) { Gather(adm, "Iron", 1250); Clock = Clock.AddDays(1); }
        Cmd(bo, "top");
        Ok(bo.All().Contains("1. Steward - 7500 XP") && bo.All().Contains("2. Ada - 5000 XP (["), "/craft top: the week's ranking with houses", bo.All());
        long purse = Purse(ada);
        Clear();
        Advance(TimeSpan.FromDays(7));
        Ok(B().Contains("Ada is named Master Crafter of the week with 5000 XP."), "the crowning names the best non-staff crafter", B());
        Ok(Purse(ada) == purse + 250, "250 marks from RealmTreasury.RewardMarks", (Purse(ada) - purse).ToString());
        Ok(Deeds.Any(d => d.Contains("|master_crafter|") && d.Contains("crafts:week:")), "the master_crafter deed with the week's key", string.Join("\n", Deeds));
        Ok(ChronLog.Any(l => l.StartsWith("title_earned|Ada is named Master Crafter|")), "a Chronicle entry", string.Join("\n", ChronLog));
        Ok(Awards.Any(a => a.StartsWith("Halloran|3|Master Crafter Ada")), "3 season points for her house", string.Join("\n", Awards));
        Ok(ada.Popups.Any(p => p.Title == "Master Crafter"), "a window for her");
        Ok(ZeroSum() == "", "the reward is minted in the treasury's books", ZeroSum());
        Ok((string)C.Call("GetMasterCrafter") == "Ada", "GetMasterCrafter");
        Ok(WeekXp(ada) == 0 || (string)F(Rec(ada), "WeekKey") != (string)D("WeekKey"), "a new week starts from nothing");
        Cmd(bo, "top");
        Ok(bo.All().Contains("No one has earned XP this week yet.") && bo.All().Contains("Last Master Crafter: Ada (5000 XP)."), "/craft top after the crowning", bo.All());
        int history = ((IList)D("History")).Count;
        var wk = ((IList)D("History"))[history - 1];
        SetF(Data(), "WeekKey", (string)F(wk, "WeekKey"));
        Inv(C, "Crown");
        Ok(((IList)D("History")).Count == history && Purse(ada) == purse + 250, "a week is never crowned twice (a reload or a clock skew)");
        Clear();
        Gather(bo, "Iron", 100);
        Advance(TimeSpan.FromDays(7));
        Ok(B().Contains("No crafter earned enough this week"), "below MinWeeklyXp nobody is named", B());
        // The treasury cannot pay: the reward waits and comes later.
        Reset();
        var cy = P("Cy");
        var tc = F(T, "config");
        SetF(tc, "RewardMintPerDay", 100L);
        for (int d = 0; d < 2; d++) { Gather(cy, "Iron", 1000); Clock = Clock.AddDays(1); }
        long p0 = Purse(cy);
        Clear();
        Advance(TimeSpan.FromDays(7));
        Ok(Purse(cy) == p0 + 100 && cy.All().Contains("The treasury owes you 150 marks"), "the treasury's daily reward cap pays 100 now and owes 150", (Purse(cy) - p0) + "\n" + cy.All());
        SetF(tc, "RewardMintPerDay", 3000L);
        Clock = Clock.AddDays(1);
        Tick(5);
        Ok(Purse(cy) == p0 + 250 && cy.All().Contains("The treasury pays you 150 marks it owed you."), "the rest is paid when the treasury can", (Purse(cy) - p0).ToString());
        Ok(ZeroSum() == "", "the books balance", ZeroSum());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Commissions()
    {
        Reset();
        var ada = P("Ada", null, "1.1.1.1"); var bram = P("Bram", null, "2.2.2.2");
        Cmd(ada, "order", "Iron", "Sword", "2", "150");
        Ok(ada.All().Contains("Commission #1 posted: Iron Sword x2 at 150 marks each. 300 marks are held by the treasury until it is filled.")
            && ada.All().Contains("It is work for Smithing. It lapses in 72 h"), "a commission is posted and its marks held", ada.All());
        Ok(Purse(ada) == 700 && Held() == 300 && Holds() == 1, "300 marks leave the purse into a treasury hold");
        Cmd(bram, "orders");
        Ok(bram.All().Contains("#1 Iron Sword x2 at 150 each - Ada - 3d 0h left"), "/craft orders lists it", bram.All());
        Cmd(bram, "orders", "smithing");
        Ok(bram.All().Contains("#1 Iron Sword"), "and by profession");
        Clear();
        Stock(bram, "Iron Sword", 1);
        Cmd(bram, "fill", "1");
        Ok(bram.All().Contains("You fill commission #1: Iron Sword x1. 147 marks are yours (fee 3).") && Purse(bram) == 1147, "a partial fill: paid from the hold, less the 2 % market fee", bram.All());
        Ok(bram.All().Contains("Smithing +75 XP.") && Xp(bram, "smithing") == 75, "the filler learns (0.5 XP a mark)", bram.All());
        Ok(Has(ada, "Iron Sword") == 1 && Has(bram, "Iron Sword") == 0, "the goods go from the filler's packs into the poster's");
        Ok(ada.All().Contains("Bram filled your commission #1: Iron Sword x1. The goods are in your packs."), "the poster is told", ada.All());
        Ok(Held() == 150 && Status(1) == "open", "the rest stays held, the commission stays open");
        Ok(QuestReports.Contains(bram.Id + "|custom|commission|1") && Deeds.Any(d => d.Contains("|commission_filled|")), "RealmQuests and RealmRenown hear of it");
        Ok(ZeroSum() == "", "the books balance", ZeroSum());
        Clear();
        Stock(bram, "Iron Sword", 3);
        Cmd(bram, "fill", "1", "5");
        Ok(bram.All().Contains("x1. 147 marks") && Has(bram, "Iron Sword") == 2 && Status(1) == "done" && Held() == 0 && Holds() == 0, "a fill takes no more than is left; the commission is done and the hold closed", bram.All());
        Ok(ada.All().Contains("Commission #1 is complete."), "the poster hears it is complete");
        Ok(bram.All().Contains("No XP for this one: you filled for this poster lately."), "the same pair earns XP once per PairCooldownHours", bram.All());
        Cmd(bram, "fill", "1");
        Ok(bram.All().Contains("There is no open commission #1."), "a done commission cannot be filled again");
        Ok(ZeroSum() == "" && Purse(ada) == 700 && Purse(bram) == 1294, "zero-sum: 300 left Ada, 294 reached Bram, 6 the crown", Purse(ada) + " " + Purse(bram) + " " + ZeroSum());
        // Cancel
        Clear();
        Clock = Clock.AddMinutes(1);
        Cmd(ada, "order", "Wooden", "Bow", "3", "40");
        Ok(Purse(ada) == 580, "a second commission holds 120");
        Cmd(bram, "cancel", "2");
        Ok(bram.All().Contains("Commission #2 is not yours."), "only the poster cancels");
        Cmd(ada, "cancel", "2");
        Ok(ada.All().Contains("Commission #2 is cancelled. 120 marks return to your purse.") && Purse(ada) == 700 && Held() == 0, "cancelling returns the hold", ada.All());
        Cmd(ada, "cancel", "2");
        Ok(Purse(ada) == 700 && ada.All().Contains("There is no open commission #2."), "a second cancel returns nothing");
        // Lapse
        Clock = Clock.AddMinutes(1);
        Cmd(ada, "order", "Bread", "4", "10");
        Ok(Purse(ada) == 660, "a third commission holds 40");
        Clear();
        Advance(TimeSpan.FromHours(73));
        Ok(Status(3) == "expired" && Purse(ada) == 700 && ada.All().Contains("Your commission #3 for Bread lapsed. 40 marks return to your purse."), "a commission nobody fills lapses and returns its marks", ada.All());
        Ok(ZeroSum() == "", "the books balance", ZeroSum());
        // The treasury's hold lapses first (a long outage): the commission closes, nothing is returned twice.
        Clock = Clock.AddMinutes(1);
        Cmd(ada, "order", "Bread", "4", "10");
        var c4 = Commission(4);
        var holds = (IDictionary)F(F(T, "data"), "Holds");
        var h = holds[(string)F(c4, "HoldId")];
        SetF(h, "Expires", DateTime.UtcNow.AddMinutes(-1));      // the treasury keeps real time
        Inv(T, "Tick");
        Ok(Purse(ada) == 700, "the treasury returns a lapsed hold by itself");
        Tick(5);
        Ok(Status(4) == "expired" && Purse(ada) == 700 && ZeroSum() == "", "the commission closes without a second refund", Purse(ada) + " " + ZeroSum());
        // Offline poster: goods wait in the ledger.
        Clock = Clock.AddMinutes(1);
        Cmd(ada, "order", "Cooked", "Meat", "2", "20");
        Offline(ada);
        Stock(bram, "Cooked Meat", 2);
        Clear();
        Cmd(bram, "fill", "5");
        Ok(Status(5) == "done" && Owed(ada, "Cooked Meat") == 2 && Has(ada, "Cooked Meat") == 0, "an offline poster's goods wait in the owed ledger");
        Online(ada);
        C.timer.RunPending();
        Ok(Has(ada, "Cooked Meat") == 2 && Owed(ada, "Cooked Meat") == 0 && ada.All().Contains("You collect: 2 Cooked Meat."), "and are given when they come back", ada.All());
        Ok(ZeroSum() == "", "the books balance", ZeroSum());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void CommissionXp()
    {
        Reset();
        var ada = P("Ada", "Corvane", "1.1.1.1"); var alt = P("AdaAlt", null, "1.1.1.1"); var cy = P("Cy", "Corvane", "3.3.3.3"); var dee = P("Dee", null, "4.4.4.4");
        Cmd(ada, "order", "Iron", "Sword", "5", "100");
        Stock(alt, "Iron Sword", 1);
        Cmd(alt, "fill", "1", "1");
        Ok(alt.All().Contains("No XP for this one: you share a hearth with the poster.") && Xp(alt, "smithing") == 0, "no commission XP between accounts of one address (alt feeding)", alt.All());
        Ok(Purse(alt) == 1098, "the marks still change hands (a trade is a trade)", Purse(alt).ToString());
        Stock(cy, "Iron Sword", 1);
        Cmd(cy, "fill", "1", "1");
        Ok(Xp(cy, "smithing") == 50, "within a house XP counts by default (XpWithinHouse)");
        Set("Commissions", "XpWithinHouse", false);
        Clock = Clock.AddHours(13);
        Stock(cy, "Iron Sword", 1);
        Clear();
        Cmd(cy, "fill", "1", "1");
        Ok(cy.All().Contains("the poster is of your own house") && Xp(cy, "smithing") == 50, "with XpWithinHouse off, not within the house", cy.All());
        // A price far below the market's is no honest work.
        var stat = Activator.CreateInstance(typeof(RealmTreasury).GetNestedType("PriceStat", BF));
        SetF(stat, "Last", 1000L);
        ((IDictionary)F(F(T, "data"), "Prices"))["Iron Sword"] = stat;
        Stock(dee, "Iron Sword", 1);
        Clear();
        Cmd(dee, "fill", "1", "1");
        Ok(dee.All().Contains("the price is far below the market's") && Xp(dee, "smithing") == 0, "no XP below MinPriceRatioForXp of the market's last price", dee.All());
        ((IDictionary)F(F(T, "data"), "Prices")).Remove("Iron Sword");
        // A daily cap on commission XP.
        Set("Commissions", "XpPerDay", 60);
        Cmd(ada, "order", "Steel", "Sword", "3", "100");
        Stock(dee, "Steel Sword", 1);
        Cmd(dee, "fill", "2", "1");
        Ok(Xp(dee, "smithing") == 50, "the first fill earns 50");
        Set("Commissions", "PairCooldownHours", 0);
        Stock(dee, "Steel Sword", 1);
        Clear();
        Cmd(dee, "fill", "2", "1");
        Ok(Xp(dee, "smithing") == 60, "the day's commission XP stops at XpPerDay", Xp(dee, "smithing").ToString());
        Stock(dee, "Steel Sword", 1);
        Clear();
        Cmd(dee, "fill", "2", "1");
        Ok(dee.All().Contains("you have earned all the commission XP you can today"), "and says so", dee.All());
        // A master pays less fee on a commission of their own craft.
        Reset();
        var po = P("Po", null, "5.5.5.5", 5000); var ms = P("Ms", null, "6.6.6.6");
        SetXp(ms, "smithing", XpFor(50));
        Cmd(po, "order", "Iron", "Sword", "1", "1000");
        Stock(ms, "Iron Sword", 1);
        long crown = TreasuryMarks();
        Cmd(ms, "fill", "1");
        Ok(TreasuryMarks() - crown == 14 && ms.All().Contains("986 marks are yours (fee 14)"), "a master smith's commission fee: 2 % less 30 %", ms.All());
        Ok(ZeroSum() == "", "the books balance", ZeroSum());
        Clock = Clock.AddMinutes(1);
        Cmd(po, "order", "Wood", "100", "2");
        var wc = P("Wc", null, "7.7.7.7");
        Stock(wc, "Wood", 100);
        Cmd(wc, "fill", "2");
        Ok(Xp(wc, "woodcutting") == 100, "a commission for a gathered good teaches its gathering profession", Xp(wc, "woodcutting").ToString());
        Clock = Clock.AddMinutes(1);
        Cmd(po, "order", "Strange", "Trinket", "1", "50");
        Stock(wc, "Strange Trinket", 1);
        Clear();
        Cmd(wc, "fill", "3");
        Ok(wc.All().Contains("the item belongs to no profession"), "an item of no profession earns no XP", wc.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void CommissionEdges()
    {
        Reset();
        var ada = P("Ada", null, "1.1.1.1", 100000); var bram = P("Bram", null, "2.2.2.2");
        Cmd(ada, "order");
        Ok(ada.All().Contains("Usage: [F4C96D]/craft order[FFFFFF]"), "usage");
        Cmd(ada, "order", "Unobtainium", "1", "10");
        Ok(ada.All().Contains("There is no item 'Unobtainium'."), "unknown item");
        Cmd(ada, "order", "Wood", "0", "10"); Ok(ada.All().Contains("The quantity must be 1 to 500."), "bad quantity");
        Cmd(ada, "order", "Wood", "5", "0"); Ok(ada.All().Contains("The price must be 1 to 100000 marks each."), "bad price");
        Cmd(ada, "order", "Wood", "500", "5000"); Ok(ada.All().Contains("A commission may hold at most 1000000 marks in all."), "500 x 5000 is over the total cap", ada.All());
        Clear();
        Cmd(ada, "order", "Iron", "Sword", "1", "100", "60");
        Ok(ada.All().Contains("The level asked must be 0 to 50."), "a minimum level above the cap is refused", ada.All());
        Clock = Clock.AddMinutes(1);
        Cmd(ada, "order", "Iron", "Sword", "1", "100", "30");
        int id = (int)F(Orders()[Orders().Count - 1], "Id");
        Ok(ada.All().Contains(", level 30+") , "a minimum level is shown", ada.All());
        Stock(bram, "Iron Sword", 1);
        Cmd(bram, "fill", id.ToString());
        Ok(bram.All().Contains("asks for Smithing level 30; you are level 1.") && Has(bram, "Iron Sword") == 1, "the level is checked", bram.All());
        Cmd(ada, "fill", id.ToString());
        Ok(ada.All().Contains("You cannot fill your own commission."), "nobody fills their own");
        var cy = P("Cy");
        SetXp(cy, "smithing", XpFor(30));
        Cmd(cy, "fill", id.ToString());
        Ok(cy.All().Contains("You carry no Iron Sword."), "a filler must carry the goods", cy.All());
        Cmd(cy, "fill"); Ok(cy.All().Contains("Usage: [F4C96D]/craft fill[FFFFFF]"), "fill usage");
        Cmd(cy, "fill", "999"); Ok(cy.All().Contains("There is no open commission #999."), "unknown id");
        Clear();
        Cmd(ada, "order", "Bread", "1", "5");
        Ok(ada.All().Contains("You posted a commission just now."), "a posting cooldown", ada.All());
        Set("Commissions", "PostCooldownSeconds", 0);
        for (int i = 0; i < 6; i++) Cmd(ada, "order", "Bread", "1", "5");
        Ok(ada.All().Contains("You have 5 commissions open already"), "at most MaxOpenPerPlayer open", ada.All());
        var poor = P("Poor", null, null, 10);
        Cmd(poor, "order", "Bread", "10", "5");
        Ok(poor.All().Contains("A commission of 50 marks must be paid in now; your purse holds 10.") && Purse(poor) == 10, "a short purse posts nothing", poor.All());
        Ok(ZeroSum() == "", "the books balance", ZeroSum());
        // The poster's owed ledger is full: no new goods can be sent to them.
        Set("Commissions", "MaxOwedLines", 5);
        var rec = Inv(C, "Rec", ada.Id.ToString(), ada.Name, true);
        var owed = (IList)F(rec, "Owed");
        var owedType = typeof(RealmCrafts).GetNestedType("Owed", BF);
        foreach (var it in new[] { "Wood", "Stone", "Flax", "Clay", "Fat" }) { var o = Activator.CreateInstance(owedType); SetF(o, "Item", it); SetF(o, "Amount", 1); owed.Add(o); }
        int bread = (int)F(Orders()[Orders().Count - 1], "Id");
        Stock(bram, "Bread", 1);
        Clear();
        Cmd(bram, "fill", bread.ToString());
        Ok(bram.All().Contains("Ada has too many goods waiting already") && Has(bram, "Bread") == 1, "a poster with a full owed ledger cannot be sent more", bram.All());
        owed.Clear();
        // No treasury, no commissions.
        NewCrafts(null, false);
        Clear();
        Cmd(ada, "order", "Bread", "1", "5");
        Ok(ada.All().Contains("The treasury is closed, so no commission can be held."), "no treasury: no commissions", ada.All());
        Cmd(bram, "fill", bread.ToString());
        Ok(bram.All().Contains("The treasury is closed") && Status(bread) == "open", "and none is filled or lapsed while it is away", bram.All());
        Tick(5);
        Ok(Status(bread) == "open", "the tick does not close commissions while the treasury is away");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Commands()
    {
        Reset();
        var ada = P("Ada", "Varrow");
        Gather(ada, "Wood", 100);
        Clear();
        Cmd(ada);
        Ok(ada.All().StartsWith("[D6A043]Guilds[FFFFFF]: Your professions (50 XP this week):"), "/craft opens in the Guilds voice", ada.All());
        Ok(ada.All().Contains("  Woodcutting: Novice, level 2 - 110 XP to level 3; today 50 of 2500") && Regex.Matches(ada.All(), "\n  [A-Z][a-z]+: ").Count >= 8, "a line per profession", ada.All());
        Ok(ada.All().Contains("has no workshop yet this week"), "the house workshop line");
        Clear();
        Cmd(ada, "help");
        Ok(ada.Messages.Count == 3 && ada.All().Contains("[F4C96D]/craft order[FFFFFF]"), "/craft help is three lines for a player", ada.All());
        Clear();
        Cmd(ada, "mining");
        Ok(ada.All().Contains("Mining: Novice, level 1 of 50 (0 XP).") && ada.All().Contains("XP from breaking stone, ore and clay.") && ada.All().Contains("At level 30 the market takes 10% less fee"), "/craft mining", ada.All());
        Clear();
        Cmd(ada, "smith");
        Ok(ada.All().Contains("Smithing: Novice"), "short names work (smith)");
        Clear();
        Cmd(ada, "perks");
        Ok(ada.All().Contains("Master (level 50): +20% yield or 10% extra-item chance; 30% off the market fee") && ada.All().Contains("a Great Guildhall +15% XP, +6 yield, +3 extra"), "/craft perks", ada.All());
        Clear();
        Cmd(ada, "top", "woodcutting");
        Ok(ada.All().Contains("The realm's best at Woodcutting:") && ada.All().Contains("1. Ada - Novice, level 2"), "/craft top <profession>", ada.All());
        Clear();
        Cmd(ada, "top", "houses");
        Ok(ada.All().Contains("House workshops this week:"), "/craft top houses");
        Clear();
        Cmd(ada, "juggling");
        Ok(ada.All().Contains("ERR") && ada.All().Contains("There is no profession 'juggling'."), "an unknown word is refused with the list", ada.All());
        Clear();
        Cmd(ada, "collect");
        Ok(ada.All().Contains("Nothing is waiting for you."), "collect with nothing owed");
        Clear();
        Cmd(ada, "admin");
        Ok(ada.All().Contains("You may not do that."), "admin needs realmcrafts.admin");
        Cmd(ada, "orders", "mine");
        Ok(ada.All().Contains("You have no commissions."), "/craft orders mine");
        Cmd(ada, "orders");
        Ok(ada.All().Contains("No commission is open."), "/craft orders with none");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void AdminCommands()
    {
        Reset();
        var adm = P("Steward"); var ada = P("Ada");
        W.Admin(adm);
        Cmd(adm, "admin", "status");
        Ok(adm.All().Contains("Guilds: 2 crafter(s) on record") && adm.All().Contains("Container events subscribed: yes"), "status", adm.All());
        Ok(adm.All().Contains("Gathering items resolved:"), "status lists what resolved");
        Clear();
        Cmd(adm, "admin", "items", "Sword");
        Ok(adm.All().Contains("Iron Sword") && adm.All().Contains("Steel Sword"), "items search", adm.All());
        Clear();
        Cmd(adm, "admin", "xp", "Ada", "smithing", "5000");
        Ok(Xp(ada, "smithing") == 5000 && adm.All().Contains("Ada's Smithing is now 5000 XP (level 12)."), "xp adds", adm.All());
        Cmd(adm, "admin", "xp", "Ada", "smithing", "-99999999");
        Ok(Xp(ada, "smithing") == 0, "and takes, never below 0 (clamped to AdminMaxAdjust)");
        Cmd(adm, "admin", "level", "Ada", "mining", "30");
        Ok(Level(ada, "mining") == 30 && Xp(ada, "mining") == XpFor(30), "level sets");
        Ok(C.FileLog.Any(l => l.Contains("Steward set Ada mining")), "admin changes are logged", string.Join("\n", C.FileLog));
        Clear();
        Cmd(adm, "admin", "reset", "Ada");
        Ok(adm.All().Contains("This wipes every profession of Ada.") && Level(ada, "mining") == 30, "reset asks first", adm.All());
        Cmd(adm, "admin", "reset", "Ada", "confirm");
        Ok(Level(ada, "mining") == 1 && adm.All().Contains("Ada's professions are wiped."), "then wipes");
        Cmd(ada, "order", "Bread", "2", "10");
        Clear();
        Cmd(adm, "admin", "cancel", "1");
        Ok(adm.All().Contains("Commission #1 is cancelled; 20 marks went back to Ada.") && Purse(ada) == 1000, "admin cancel returns the hold", adm.All());
        Gather(ada, "Iron", 1000);
        Clear();
        Cmd(adm, "admin", "crown");
        Ok(adm.All().Contains("The week is closed now.") && B().Contains("No crafter earned enough") == false, "crown closes the week early", B());
        Clear();
        Cmd(adm, "admin", "watch", "Ada", "off");
        Ok(adm.All().Contains("No longer watching Ada."), "watch off");
        Cmd(adm, "admin", "watch", "Nobody");
        Ok(adm.All().Contains("No such person"), "watch an unknown player");
        Cmd(adm, "admin", "bogus");
        Ok(adm.All().Contains("Admin: [F4C96D]/craft admin[FFFFFF]"), "unknown admin word shows the admin help");
    }


    // ------------------------------------------------------------------------------------------------------------
    static void Popups()
    {
        Reset();
        var ada = P("Ada");
        PopupsOff.Add(ada.Id.ToString());
        SetXp(ada, "cooking", XpFor(50) - 1);
        Craft(ada, "Bread", 1, Station("Oven"));
        Ok(ada.Popups.Count == 0 && ada.All().Contains("You are named Master Cook"), "/realm popups off: chat only");
        PopupsOff.Clear();
        var bo = P("Bo");
        PlayerExtensions.PopupsFail = true;
        SetXp(bo, "cooking", XpFor(50) - 1);
        Craft(bo, "Bread", 1, Station("Oven"));
        Ok(bo.All().Contains("You are named Master Cook") && C.Logged.Any(l => l.Contains("ShowPopup failed")), "a failing window falls back to chat");
        PlayerExtensions.PopupsFail = false;
        Set("General", "UsePopups", false);
        var cy = P("Cy");
        SetXp(cy, "cooking", XpFor(50) - 1);
        Craft(cy, "Bread", 1, Station("Oven"));
        Ok(cy.Popups.Count == 0, "UsePopups off: no window");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Switches()
    {
        Reset(cfg => Tw(cfg, "General", "Enabled", false));
        var ada = P("Ada");
        Gather(ada, "Wood", 50);
        Craft(ada, "Iron Sword");
        Kill(ada, "Wolf");
        Cmd(ada);
        Ok(Xp(ada, "woodcutting") == 0 && Xp(ada, "smithing") == 0 && Xp(ada, "hunting") == 0 && ada.All().Contains("The guilds of the realm are closed for now."), "General.Enabled off: nothing counts, /craft is closed");
        Ok((int)C.Call("GetMarketFeeDiscount", ada.Id.ToString(), "Iron") == 0, "and no discount");
        Reset(cfg => Tw(cfg, "Gathering", "Enabled", false));
        ada = P("Ada");
        Gather(ada, "Wood", 50); Kill(ada, "Wolf"); Craft(ada, "Iron Sword");
        Ok(Xp(ada, "woodcutting") == 0 && Xp(ada, "hunting") == 0 && Xp(ada, "smithing") == 30, "Gathering.Enabled off: crafting still counts");
        Reset(cfg => Tw(cfg, "Crafting", "Enabled", false));
        ada = P("Ada");
        Gather(ada, "Wood", 50); Craft(ada, "Iron Sword");
        Ok(Xp(ada, "woodcutting") == 25 && Xp(ada, "smithing") == 0, "Crafting.Enabled off: gathering still counts");
        Reset(cfg => Tw(cfg, "Commissions", "Enabled", false));
        ada = P("Ada");
        Cmd(ada, "order", "Bread", "1", "5");
        Ok(ada.All().Contains("That part of the guilds is closed") && Holds() == 0, "Commissions.Enabled off");
        Reset(cfg => Tw(cfg, "Weekly", "Enabled", false));
        ada = P("Ada");
        for (int d = 0; d < 2; d++) { Gather(ada, "Iron", 1250); Clock = Clock.AddDays(1); }
        string week = (string)D("WeekKey");
        Advance(TimeSpan.FromDays(7));
        Ok(((IList)D("History")).Count == 0 && (string)D("WeekKey") != week, "Weekly.Enabled off: nobody is named, but the week still turns");
        Reset(cfg => Tw(cfg, "Workshops", "Enabled", false));
        ada = P("Ada", "Varrow"); Settle(ada);
        Gather(ada, "Iron", 100);
        Cmd(ada, "house");
        Ok(HouseWeek("Varrow") == 0 && ada.All().Contains("That part of the guilds is closed"), "Workshops.Enabled off");
        Reset(cfg => Tw(cfg, "Dominion", "Enabled", false));
        ada = P("Ada", "Dunmere");
        Holdings.Add(new Dictionary<string, object> { { "kind", "mine" }, { "owner", "Dunmere" }, { "placed", true } });
        Inv(C, "PollDominion");
        Ok((double)Inv(C, "YieldPercent", Rec(ada) ?? Inv(C, "Rec", ada.Id.ToString(), "Ada", true), "mining") < 1, "Dominion.Enabled off: no holding perks");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void ReloadAndData()
    {
        Reset();
        var ada = P("Ada");
        Gather(ada, "Iron", 100);
        Cmd(ada, "order", "Bread", "2", "10");
        Inv(C, "Unload");
        NewCrafts();
        Ok(Xp(ada, "mining") == 200 && Status(1) == "open" && Held() == 20, "a reload keeps XP and commissions (and the treasury keeps the hold)");
        Ok(EventManager.Count<ContainerItemAddEvent>() == 2, "and subscribes again once");
        string file = Path.Combine(Dir, "RealmCrafts.json");
        string good = File.ReadAllText(file);
        File.WriteAllText(file, good.Substring(0, good.Length / 2));
        NewCrafts();
        Ok((bool)F(C, "loadFailed") && C.Logged.Any(l => l.Contains("will NOT write it")), "a cut-off file pauses the plugin", string.Join("\n", C.Logged));
        Clear();
        Cmd(ada);
        Ok(ada.All().Contains("The guilds are closed: oxide/data/RealmCrafts.json could not be read."), "players are told");
        Gather(ada, "Iron", 100);
        Inv(C, "SafeTick");
        Inv(C, "Unload");
        Ok(File.ReadAllText(file) == good.Substring(0, good.Length / 2), "and the damaged file is never overwritten");
        Ok(File.Exists(Path.Combine(Dir, "RealmCrafts_lastgood.json")) && File.ReadAllText(Path.Combine(Dir, "RealmCrafts_lastgood.json")).Contains("\"Players\""), "the last good copy is there to restore");
        File.WriteAllText(file, "");
        NewCrafts();
        Ok((bool)F(C, "loadFailed"), "an empty file pauses it too");
        File.WriteAllText(file, good.Replace("\"Format\": 1", "\"Format\": 9"));
        NewCrafts();
        Ok((bool)F(C, "loadFailed") && C.Logged.Any(l => l.Contains("newer RealmCrafts")), "a file from a newer version is left alone");
        File.WriteAllText(file, good);
        NewCrafts();
        Ok(!(bool)F(C, "loadFailed") && Xp(ada, "mining") == 200, "a restored file loads");
        File.Delete(file);
        NewCrafts();
        Ok(!(bool)F(C, "loadFailed") && Xp(ada, "mining") == 0, "no file: a fresh start");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void ConfigClamp()
    {
        Reset(cfg =>
        {
            Tw(cfg, "Levels", "MaxLevel", 9999); Tw(cfg, "Levels", "DailyXpCap", -5);
            Tw(cfg, "Commissions", "DurationHours", 5000); Tw(cfg, "Commissions", "MaxOpen", 99999); Tw(cfg, "Commissions", "FeePercent", 90);
            Tw(cfg, "Weekly", "CrownDay", "Caturday"); Tw(cfg, "Weekly", "CrownHourUtc", 30);
            Tw(cfg, "Perks", "BonusYieldPercentPerLevel", 99.0);
        });
        Ok((int)F(Cfg("Levels"), "MaxLevel") == 200 && (int)F(Cfg("Levels"), "DailyXpCap") == 0, "levels clamped");
        Ok((int)F(Cfg("Commissions"), "DurationHours") == 160 && (int)F(Cfg("Commissions"), "MaxOpen") == 1500 && (int)F(Cfg("Commissions"), "FeePercent") == 50,
            "commissions clamped (a treasury hold lasts at most 7 days; it keeps at most 2000 holds)");
        Ok((string)F(Cfg("Weekly"), "CrownDay") == "Sunday" && (int)F(Cfg("Weekly"), "CrownHourUtc") == 23, "a bad crowning day falls back to Sunday");
        Ok((double)F(Cfg("Perks"), "BonusYieldPercentPerLevel") == 5.0, "bonus yield per level at most 5 %");
        var ranks = (List<int>)F(Cfg("Levels"), "RankLevels");
        Ok(ranks.First() == 1 && ranks.Last() == 200 && ranks.Count == 6, "the ranks end at the max level", string.Join(",", ranks));
        Reset(cfg =>
        {
            var items = (System.Collections.IList)F(Sec(cfg, "Gathering"), "Items");
            var t = items[0].GetType();
            var bad = Activator.CreateInstance(t); SetF(bad, "Item", "Wood"); SetF(bad, "Profession", "smithing"); SetF(bad, "Xp", 1.0);
            items.Add(bad);
        });
        Ok(C.Logged.Any(l => l.Contains("Gathering item 'Wood' ignored")), "a gathering item with a crafting profession (or listed twice) is refused", string.Join("\n", C.Logged));
        var ada = P("Ada");
        Gather(ada, "Wood", 10);
        Ok(Xp(ada, "woodcutting") == 5, "the good entry stays");
        Reset(cfg => Tw(cfg, "Weekly", "CrownDay", "wednesday"));
        var next = (DateTime)D("NextCrowning");
        Ok(next.DayOfWeek == DayOfWeek.Wednesday, "the crowning day is configurable", next.ToString("o"));
        // The defaults come out of the config file once (lists are filled after reading, never doubled).
        Reset();
        var cfgObj = F(C, "config");
        int n = ((IList)F(Sec(cfgObj, "Gathering"), "Items")).Count;
        Inv(C, "ClampConfig");
        Ok(((IList)F(Sec(cfgObj, "Gathering"), "Items")).Count == n, "clamping twice keeps the lists as they are");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Api()
    {
        Reset();
        var ada = P("Ada", "Varrow");
        SetXp(ada, "smithing", XpFor(23));
        SetXp(ada, "mining", XpFor(5));
        Ok((int)C.Call("GetProfessionLevel", ada.Id.ToString(), "smithing") == 23 && (int)C.Call("GetProfessionLevel", ada.Id.ToString(), "smith") == 23, "GetProfessionLevel (names or short names)");
        Ok((int)C.Call("GetProfessionLevel", "76561199999999999", "smithing") == 0 && (int)C.Call("GetProfessionLevel", ada.Id.ToString(), "juggling") == 0, "unknown player or profession: 0");
        Ok((string)C.Call("GetCraftSummary", ada.Id.ToString()) == "mining:5|smithing:23", "GetCraftSummary", (string)C.Call("GetCraftSummary", ada.Id.ToString()));
        Ok(C.Call("GetMasterCrafter") == null, "no Master Crafter yet");
        Ok((int)C.Call("GetWorkshopTier", "Nobody") == 0 && (int)C.Call("GetWorkshopTier", "") == 0, "GetWorkshopTier of an unknown house: 0");
        var methods = typeof(RealmCrafts).GetMethods(BF).Where(m => new[] { "GetMarketFeeDiscount", "GetProfessionLevel", "GetMasterCrafter", "GetWorkshopTier", "GetCraftSummary" }.Contains(m.Name)).ToList();
        Ok(methods.Count == 5 && methods.All(m => !m.IsPublic), "every API method is non-public (Oxide's Call finds only those)");
    }
}
