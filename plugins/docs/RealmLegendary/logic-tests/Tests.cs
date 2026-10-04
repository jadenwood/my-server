// Behaviour tests for plugins/RealmLegendary.cs (the Ironbreaker), compiled unchanged with Mocks.cs and World.cs.
// RealmEvents.cs is compiled in too, so the prize hook is tested end to end. Run with run.sh.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events.Entities.Players;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static class T
{
    static int Main(string[] argv)
    {
        Setup(); Claiming(); Strikes(); Revenge(); Blocks(); Title(); Death(); Leaving(); Soulbound(); Restart(); Commands(); Prizes(); Api();
        Chronicle(); Popups(); DataSafety(); Config(); EventsHook();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static void Setup()
    {
        Reset();
        NewLegendary();
        Ok(State == "keeping" && Bearer == null, "a new realm: the blade rests in the armoury, GetBearerName is null");
        Ok(L.permission.Registered.Contains("realmlegendary.admin"), "registers realmlegendary.admin");
        Ok(L.Logged.Any(l => l.Contains("'Steel Greatsword'")), "the base item is found by name search (Greatsword) and logged", string.Join("\n", L.Logged));
        Ok(AllBlades().Count == 0, "no blade exists before anyone wins it");
    }

    static void Claiming()
    {
        Reset();
        var a = Mk(1001, "Aldric", "Varrow");
        NewLegendary();
        Clear();
        Ok(Award("tournament", a), "AwardEventPrize(tournament) gives the blade to the champion");
        Ok(State == "borne" && Bearer == "Aldric", "Aldric bears it");
        Ok(Count(a) == 1 && Bound != null && a.Packs.HasItem(Bound) || a.Hotbar.HasItem(Bound), "exactly one blade is put in Aldric's packs, and it is the bound stack");
        Ok(B().Contains("[D6A043]Herald[FFFFFF]: Aldric, champion of the Royal Tournament, takes up the Ironbreaker!"), "the herald announces the claim in the Realm voice", B());
        Ok(a.All().Contains("You bear the Ironbreaker") && a.All().Contains("[8FC97A]Ironbreaker[FFFFFF]:"), "the bearer is told what it does, in the done tone", a.All());
        Ok(a.All().Contains("stay away over 15 min"), "the bearer is told the logout grace");
        Ok(ChronLog.Any(l => l.StartsWith("blade_claimed|Aldric takes up the Ironbreaker|Aldric of House Varrow won it as champion of the Royal Tournament.")), "chronicled as blade_claimed with the house", string.Join("\n", ChronLog));
        Ok(Deeds.Count == 1 && Deeds[0].StartsWith("1001|Aldric|ironbreaker|"), "RealmRenown.AddDeed is called with the configured deed", string.Join("\n", Deeds));
        Ok(AuditBalanced() && (int)D("Minted") == 1, "audit: one minted, one in the world", AuditText());
        Ok(!Award("tournament", Mk(1002, "Brannoc")), "a second award while it is borne is refused");
        Ok(AllBlades().Count == 1, "still exactly one blade in the world");
    }

    static void Strikes()
    {
        Reset();
        var a = Mk(1001, "Aldric", "Varrow"); var b = Mk(1002, "Brannoc", "Corvane");
        NewLegendary();
        Award("tournament", a);
        var blade = Bound;
        Ok(Math.Abs(Hit(a, b.Entity, 20f, blade) - 26f) < 0.01f, "blade strike on a player: x1.3 (Damager path)");
        Ok(Math.Abs(Hit(a, b.Entity, 20f, blade, "right") - 26f) < 0.01f, "blade strike on a player: x1.3 (right-hand path)");
        b.Entity.Blocking = true;
        Ok(Math.Abs(Hit(a, b.Entity, 20f, blade) - 39f) < 0.01f, "against a blocking player: x1.3 x1.5");
        b.Entity.Blocking = false;
        Ok(Math.Abs(Hit(a, Gate(), 20f, blade) - 40f) < 0.01f, "against a gate (placed object): x2");
        var sheep = new Entity { IsPlayer = false };
        Ok(Math.Abs(Hit(a, sheep, 20f, blade) - 20f) < 0.01f, "against a creature: unchanged");
        var other = Craft(a);
        Ok(Math.Abs(Hit(a, b.Entity, 20f, other) - 20f) < 0.01f, "the bearer's own crafted greatsword is an ordinary blade");
        var axe = new InvGameItemStack(InvBlueprints.Get("Battle Axe"), 1, null);
        Ok(Math.Abs(Hit(a, b.Entity, 20f, axe) - 20f) < 0.01f, "the bearer with another weapon: unchanged");
        Ok(Math.Abs(Hit(b, a.Entity, 20f, blade) - 20f) < 0.01f, "someone else holding a stack reference: unchanged (only the bearer)");
        Ok(Math.Abs(Hit(a, b.Entity, 20f, null, "none") - 26f) < 0.01f, "weapon unknown to the server, melee damage: counts (WhenWeaponUnknown melee)");
        Ok(Math.Abs(Hit(a, b.Entity, 20f, null, "none", DamageType.Projectile | DamageType.Pierce) - 20f) < 0.01f, "weapon unknown, an arrow: unchanged");
        Cfg("WhenWeaponUnknown", "never");
        Ok(Math.Abs(Hit(a, b.Entity, 20f, null, "none") - 20f) < 0.01f, "WhenWeaponUnknown never: unknown hits do not count");
        Cfg("WhenWeaponUnknown", "melee");
        Ok(Math.Abs(Hit(a, b.Entity, 20f, blade, "damager", DamageType.Healing) - 20f) < 0.01f, "healing is never scaled");
        var cancelled = new CodeHatch.Networking.Events.Entities.EntityDamageEvent { Entity = b.Entity, Damage = Strike(a, blade, "damager", 20f, DamageType.Melee) };
        cancelled.Cancel("truce");
        Inv(L, "OnEntityHealthChange", cancelled);
        Ok(cancelled.Damage.Amount == 20f, "a cancelled event (truce, raid hours) is left alone");
        Cfg("MaxDamagePerHit", 30f);
        Ok(Math.Abs(Hit(a, Gate(), 20f, blade) - 30f) < 0.01f, "MaxDamagePerHit caps a boosted hit");
        Cfg("MaxDamagePerHit", 0f);
        Cfg("BearerDamageTakenMultiplier", 1.2f);
        Ok(Math.Abs(Hit(b, a.Entity, 10f, null, "none") - 12f) < 0.01f, "BearerDamageTakenMultiplier is the bearer's cost");
        Cfg("BearerDamageTakenMultiplier", 1f);
        Ok((string)F(L, "lastFoeId") == "1002", "a blow on the bearer marks the foe");
        Cfg("DebugStrikes", true);
        Clock = Clock.AddSeconds(2);
        Hit(a, b.Entity, 20f, blade, "right");
        Ok(L.Logged.Any(l => l.Contains("Strike by Aldric: weapon via right-hand") && l.Contains("blade yes")), "DebugStrikes logs the path a strike was resolved by", string.Join("\n", L.Logged.TakeLast(3)));
        Cfg("DebugStrikes", false);

        Cfg("HeldMatch", "name");
        Ok(Math.Abs(Hit(a, b.Entity, 20f, other) - 26f) < 0.01f, "HeldMatch name: any greatsword in the bearer's hand counts (if the server copies stacks)");
        Cfg("HeldMatch", "stack");
        // After a restart the stack is found again from the first strike with a greatsword in the bearer's packs.
        SetF(L, "bound", null);
        Ok(Math.Abs(Hit(a, b.Entity, 20f, blade) - 26f) < 0.01f && Bound == blade, "with no bound stack, a held greatsword from the bearer's own packs is bound");
    }

    static void Revenge()
    {
        Reset();
        var a = Mk(1001, "Aldric", "Varrow"); var b = Mk(1002, "Brannoc", "Corvane"); var c = Mk(1003, "Cerys", "Varrow");
        NewLegendary();
        Award("tournament", a);
        Die(a, b);
        Clock = Clock.AddHours(1);
        Die(b, a);
        Ok(State == "keeping" && B().Contains("passed between the same hands too lately"), "the same two players cannot pass it back within PassPairCooldownHours", B());
        Award("tournament", b = Mk(1004, "Dain", "Merrin"));
        Clock = Clock.AddHours(1);
        Die(b, a);
        Ok(Bearer == "Aldric", "a former bearer may take it back from someone else by the sword (only prizes have a cooldown)");
        Ok(!(bool)Inv(L, "AwardEventPrize", "tournament", "1004", "Dain"), "while borne, no prize is given");

        // A blow from the bearer's own side is not a foe's blow.
        Hit(c, a.Entity, 5f, null, "none");
        Ok(F(L, "lastFoeId") == null, "a housemate's blow does not mark a foe");
        var e = Mk(1005, "Edda", "Merrin");
        Hit(e, a.Entity, 5f, null, "none");
        Hit(c, a.Entity, 5f, null, "none");
        Ok((string)F(L, "lastFoeId") == "1005", "a housemate's later blow does not hide the real foe");

        // The stack vanishes without a trace (no foe): the herald says it is no longer in hand.
        SetF(L, "lastFoeId", null);
        Clear();
        SetF(L, "bound", null);
        foreach (var s in a.Packs.GetItems().Concat(a.Hotbar.GetItems())) Drop(s);
        Tick(2);
        Ok(State == "keeping" && B().Contains("no longer in Aldric's hands"), "a blade gone missing returns to the armoury", B());
    }

    static void Blocks()
    {
        Reset();
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Ok(Math.Abs(HitBlock(a, 50f, Bound) - 100f) < 0.01f, "blade strike on a building block: x2 (OnCubeTakeDamage)");
        var b = Mk(1002, "Brannoc");
        Ok(Math.Abs(HitBlock(b, 50f, Bound) - 50f) < 0.01f, "anyone else's strike on a block: unchanged");
        var axe = new InvGameItemStack(InvBlueprints.Get("Battle Axe"), 1, null);
        Ok(Math.Abs(HitBlock(a, 50f, axe) - 50f) < 0.01f, "the bearer with another tool on a block: unchanged");
    }

    static void Title()
    {
        Reset();
        var a = Mk(1001, "Aldric"); var b = Mk(1002, "Brannoc");
        NewLegendary();
        Award("tournament", a);
        Tick();
        Ok(a.ChatFormat == "%name% [D6A043](Ironbreaker)[-] : %message%", "the bearer's chat format carries the title after the name", a.ChatFormat);
        Ok(b.ChatFormat == "%name% : %message%", "nobody else's format changes");
        a.ChatFormat = "[D6A043]Kingslayer[-] " + a.ChatFormat;          // RealmRenown prefixes in front
        Tick();
        Ok(a.ChatFormat == "[D6A043]Kingslayer[-] %name% [D6A043](Ironbreaker)[-] : %message%", "RealmRenown's prefix and the bearer title live together", a.ChatFormat);
        a.ChatFormat = "%name% : %message%";                                // the game reset it
        Tick();
        Ok(a.ChatFormat.Contains("(Ironbreaker)"), "a reset format gets the title again");
        Inv(L, "AdminRevoke", b);
        Ok(a.ChatFormat == "%name% : %message%", "losing the blade takes the title off", a.ChatFormat);
        Award("tournament", a); Tick();
        Inv(L, "Unload");
        Ok(a.ChatFormat == "%name% : %message%", "Unload takes the title off");
    }

    static void Death()
    {
        Reset();
        var a = Mk(1001, "Aldric", "Varrow"); var b = Mk(1002, "Brannoc", "Corvane"); var c = Mk(1003, "Cerys", "Varrow");
        NewLegendary();
        Award("tournament", a);
        Clear();
        var corpse = Die(a, b);
        Ok(Bearer == "Brannoc" && Count(b) == 1, "the slayer takes the blade into their own packs");
        Ok(corpse.Contents.GetItems().Count(s => s.Blueprint.Name == "Steel Greatsword") == 0, "the corpse holds no blade (taken before the corpse is filled)");
        Ok(AllBlades().Count == 1, "exactly one blade in the world after the pass");
        Ok(B().Contains("Brannoc has slain Aldric and takes the Ironbreaker from their hand!"), "the herald tells the pass", B());
        Ok(ChronLog.Any(l => l.StartsWith("blade_claimed|Brannoc takes up the Ironbreaker|Brannoc of House Corvane slew Aldric") && l.EndsWith("|Brannoc;Aldric")), "chronicled with both names", string.Join("\n", ChronLog));
        Ok(AuditBalanced(), "audit balanced after a pass", AuditText());

        // A housemate of the bearer cannot take it.
        Clear();
        HouseOf[1002] = "Corvane"; HouseOf[1003] = "Corvane";
        Die(b, c);
        Ok(State == "keeping" && AllBlades().Count == 0, "slain by a housemate: back to the armoury, no blade left anywhere");
        Ok(B().Contains("Brannoc fell to their own side"), "the herald says why", B());
        Ok(ChronLog.Any(l => l.StartsWith("blade_lost|The Ironbreaker returns to the crown's armoury|Brannoc fell to their own side")), "the loss is chronicled as blade_lost", string.Join("\n", ChronLog));

        // A death with no killer: the last foe within the window takes it; outside the window it is lost.
        Reset();
        a = Mk(1001, "Aldric", "Varrow"); b = Mk(1002, "Brannoc", "Corvane");
        NewLegendary();
        Award("tournament", a);
        Hit(b, a.Entity, 10f, null, "none");
        Clock = Clock.AddSeconds(10);
        Die(a, null);
        Ok(Bearer == "Brannoc", "a fall shortly after a foe's blow: the foe takes it");
        Clock = Clock.AddMinutes(5);
        Hit(a, b.Entity, 10f, null, "none");
        Clock = Clock.AddSeconds(31);
        Die(b, null);
        Ok(State == "keeping", "no killer and no foe within CombatWindowSeconds: back to the armoury");

        // Suicide.
        Reset();
        a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Die(a, a);
        Ok(State == "keeping" && AllBlades().Count == 0, "killing oneself: back to the armoury, nothing in the corpse");

        // Allied houses (liege, treaty) cannot pass it either.
        Reset();
        a = Mk(1001, "Aldric", "Varrow"); b = Mk(1002, "Brannoc", "Corvane");
        Treaties.Add(Pair("Varrow", "Corvane"));
        NewLegendary();
        Award("tournament", a);
        Die(a, b);
        Ok(State == "keeping", "slain by a treaty partner: back to the armoury");
        Treaties.Clear(); Liege["Corvane"] = "Varrow";
        Award("tournament", b = Mk(1003, "Cerys", "Corvane"));
        Die(b, a);                                                         // Aldric's Varrow is Corvane's liege
        Ok(State == "keeping", "slain by the liege house: back to the armoury");
        Cfg("SlayerMustBeUnallied", false);
        Award("tournament", Mk(1004, "Dain", "Corvane"));
        Die(Server.GetPlayerById(1004), a);
        Ok(Bearer == "Aldric", "SlayerMustBeUnallied false: allies may pass it");
        Cfg("PassToSlayer", false);
        Die(a, Mk(1005, "Edda", "Merrin"));
        Ok(State == "keeping", "PassToSlayer false: every death returns it to the armoury");

        // The death hook never blocks the game's own handling.
        Reset();
        a = Mk(1001, "Aldric"); b = Mk(1002, "Brannoc");
        NewLegendary();
        Award("tournament", a);
        object r = Inv(L, "OnKingDeath", new CodeHatch.Networking.Events.Players.PlayerDeathEvent { PlayerId = a.Id, KillingDamage = new Damage { DamageSource = b.Entity } });
        Ok(r == null, "OnKingDeath returns null (the throne's king-death handling continues)");
        Ok(Inv(L, "OnEntityDeath", new CodeHatch.Networking.Events.Entities.EntityDeathEvent { Entity = b.Entity }) == null, "OnEntityDeath returns null");
    }

    static void Leaving()
    {
        Reset();
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Inv(L, "OnPlayerDisconnected", a); Offline(a);
        Ok(State == "away" && Count(a) == 0 && AllBlades().Count == 0, "logging out: the blade goes into keeping at once (nothing on the sleeping body)");
        Ok(Bearer == "Aldric", "still the bearer during the grace");
        Advance(TimeSpan.FromMinutes(10));
        Online(a); Inv(L, "OnPlayerConnected", a); Tick();
        Ok(State == "borne" && Count(a) == 1 && AllBlades().Count == 1, "back within the grace: the blade returns to the hand, once");
        Tick(5);
        Ok(AllBlades().Count == 1, "more ticks give no second blade");
        Inv(L, "OnPlayerDisconnected", a); Offline(a);
        Clear();
        Advance(TimeSpan.FromMinutes(16));
        Ok(State == "keeping" && B().Contains("Aldric has been gone too long"), "gone longer than the grace: back to the armoury", B());
        Online(a); Inv(L, "OnPlayerConnected", a); Tick(3);
        Ok(Count(a) == 0, "returning later gives nothing");
        Ok(AuditBalanced(), "audit balanced", AuditText());

        // A winner who is offline when they win has PrizeClaimHours.
        Reset();
        a = Mk(1001, "Aldric");
        NewLegendary();
        Offline(a);
        Clear();
        Ok(Award("tournament", a) && State == "away", "an offline champion wins it into keeping");
        Ok(B().Contains("has 24 hours to come and take it up"), "the herald gives them 24 hours", B());
        Advance(TimeSpan.FromHours(3));
        Online(a); Inv(L, "OnPlayerConnected", a); Tick();
        Ok(State == "borne" && Count(a) == 1, "they log in hours later and take it up");
    }

    static void Soulbound()
    {
        Reset();
        var a = Mk(1001, "Aldric"); var b = Mk(1002, "Brannoc");
        NewLegendary();
        Award("tournament", a);
        var chest = Chest();
        Move(Bound, chest.Contents);
        Tick();
        Ok(Count(a) == 1 && chest.Contents.GetItems().Count == 0, "put in a chest: it comes back to the bearer's hand");
        Ok(a.All().Contains("will not leave your hand"), "the bearer is told", a.All());
        Move(Bound, b.Packs);
        Tick();
        Ok(Count(a) == 1 && Count(b) == 0, "traded to another player: it comes back");
        Ok(AuditBalanced(), "audit balanced after the returns", AuditText());
        // Packs full: it waits, then comes when there is room.
        Reset();
        a = Mk(1001, "Aldric", null, 0);
        for (int i = 0; i < 8; i++) a.Hotbar.AddItem(new InvGameItemStack(InvBlueprints.Get("Stone"), 1, null));
        NewLegendary();
        Award("tournament", a);
        Ok(State == "borne" && (bool)D("Custody") && Count(a) == 0, "full packs: borne, but waiting in keeping");
        Ok(a.All().Contains("Make room"), "the bearer is told to make room", a.All());
        Tick(3);
        Ok(Count(a) == 0, "no blade while still full");
        a.Hotbar.RemoveItem(a.Hotbar.GetItems()[0], true);
        Tick();
        Ok(Count(a) == 1 && AllBlades().Count == 1, "room made: the blade comes to hand, once");
    }

    static void Restart()
    {
        Reset();
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Reload();
        Tick();
        Ok(Count(a) == 1 && AllBlades().Count == 1 && Bearer == "Aldric", "Unload takes it into keeping, the next load gives it back: one blade");
        // The world kept the blade (saved before Unload): the stack is bound, not minted again.
        var kept = Bound;
        Inv(L, "Unload");
        a.Packs.AddItem(kept);                                             // what a world saved earlier brings back
        NewLegendary();
        Tick();
        Ok(Count(a) == 1 && AllBlades().Count == 1 && (int)D("Restored") == 1, "a blade restored with the world is bound, never doubled", AuditText());
        Ok(AuditBalanced(), "audit balanced after the restore", AuditText());
        // The bearer's own crafted greatsword is not mistaken for a restored blade.
        Craft(a);
        Inv(L, "Unload");
        NewLegendary();
        Tick();
        Ok(Count(a) == 2 && AllBlades().Count == 2, "own greatsword kept, blade given back: two (one theirs, one the blade)");
        Ok(AuditBalanced(), "audit still balanced", AuditText());
        // Offline at restart: away with the grace.
        Inv(L, "Unload");
        Offline(a);
        NewLegendary();
        Tick();
        Ok(State == "away", "bearer offline after a restart: away, with the grace");
    }

    static void Commands()
    {
        Reset();
        var a = Mk(1001, "Aldric"); var b = Mk(1002, "Brannoc"); var adm = Mk(1009, "Steward");
        NewLegendary();
        Inv(L, "CmdIronbreaker", a, "ironbreaker", new[] { "grant", "Aldric" });
        Ok(a.All().Contains("ERR") && a.All().Contains("You may not do that.") && State == "keeping", "players may not use /ironbreaker");
        Admin(adm);
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new string[0]);
        Ok(adm.All().Contains("rests in the crown's armoury") && adm.All().Contains("Base item: Steel Greatsword") && adm.All().Contains("Prize of: tournament"), "status in the armoury", adm.All());
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "grant", "Aldric" });
        Ok(Bearer == "Aldric" && adm.All().Contains("Aldric now bears the Ironbreaker.") && Count(a) == 1, "grant gives it", adm.All());
        Ok(B().Contains("By the crown's leave, Aldric takes up the Ironbreaker."), "grant is heralded");
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "grant", "Brannoc" });
        Ok(Bearer == "Aldric" && adm.All().Contains("Aldric bears it now"), "grant to another without force is refused", adm.All());
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "grant", "Brannoc", "force" });
        Ok(Bearer == "Brannoc" && Count(a) == 0 && Count(b) == 1 && AllBlades().Count == 1, "grant ... force moves it: one blade");
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "status" });
        Ok(adm.All().Contains("Borne by Brannoc") && adm.All().Contains("In hand: yes") && adm.All().Contains("Minted 2, restored 0, reclaimed 1, unrecovered 0. In the world: 1."), "status shows the bearer and the audit", adm.All());
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "revoke" });
        Ok(State == "keeping" && AllBlades().Count == 0, "revoke takes it back");
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "revoke" });
        Ok(adm.All().Contains("No one bears the Ironbreaker."), "revoke with no bearer says so");
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "grant", "Nobody" });
        Ok(adm.All().Contains("No such person"), "grant to an unknown name");
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "items", "sword" });
        Ok(adm.All().Contains("Items whose name holds 'sword' (2)") && adm.All().Contains("  Iron Sword") && adm.All().Contains("  Steel Greatsword"), "items lists names to set BaseItem", adm.All());
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "grant", "Aldric" });
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "reset" });
        Ok(adm.All().Contains("reset confirm") && Bearer == "Aldric", "reset asks for confirm");
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "reset", "confirm" });
        Ok(State == "keeping" && AllBlades().Count == 0 && AuditBalanced(), "reset confirm: armoury, no blade, audit kept", AuditText());
        adm.Messages.Clear();
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "nonsense" });
        Ok(adm.All().Contains("/ironbreaker") && adm.All().Contains("[F4C96D]"), "unknown subcommand shows help with the command colour", adm.All());
    }

    static void Prizes()
    {
        Reset();
        var a = Mk(1001, "Aldric"); var b = Mk(1002, "Brannoc");
        NewLegendary();
        Ok(!Award("kings_hunt", a), "kings_hunt is not a prize by default");
        Ok(!Award("crown_night", a) && !Award("", a), "unknown kinds are refused");
        Cfg("PrizeEvents", new List<string> { "kings_hunt" });
        Ok(Award("kings_hunt", a) && B().Contains("Aldric has taken the King's quarry and takes up the Ironbreaker!"), "kings_hunt when configured", B());
        Inv(L, "AdminRevoke", b);
        Ok(!Award("kings_hunt", a) && B().Contains("Aldric won, but may not bear the Ironbreaker again so soon"), "a former bearer cannot win it back within PrizeCooldownDays", B());
        Clock = Clock.AddDays(8);
        Tick();
        Ok(Award("kings_hunt", a), "after PrizeCooldownDays they may");
        Ok(!(bool)Inv(L, "AwardEventPrize", "kings_hunt", "not-an-id", "x"), "a bad id is refused");
    }

    static void Api()
    {
        Reset();
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Ok(Inv(L, "GetBearerId") == null && !(bool)Inv(L, "IsBearer", "1001"), "no bearer: GetBearerId null, IsBearer false");
        Award("tournament", a);
        Ok((string)Inv(L, "GetBearerId") == "1001" && (bool)Inv(L, "IsBearer", "1001") && !(bool)Inv(L, "IsBearer", "1002"), "GetBearerId and IsBearer");
        Inv(L, "OnPlayerDisconnected", a); Offline(a);
        Ok(Bearer == "Aldric", "GetBearerName still names an away bearer");
        foreach (var m in new[] { "GetBearerName", "GetBearerId", "IsBearer", "AwardEventPrize" })
            Ok(!typeof(RealmLegendary).GetMethods(BF).First(x => x.Name == m).IsPublic, m + " is non-public (Oxide calls only non-public methods)");
    }

    static void Chronicle()
    {
        Reset();
        ChronRejects = true;
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Ok(ChronLog.Any(l => l.StartsWith("decree|Aldric takes up the Ironbreaker")), "an older Chronicle that rejects the type gets a decree line", string.Join("\n", ChronLog));
        Reset();
        a = Mk(1001, "Aldric[FF0000]{0}");
        NewLegendary();
        Award("tournament", a);
        Ok(Bearer == "AldricFF0000{0}" && !B().Contains("[FF0000]"), "names are cleaned of colour tags; braces are harmless", B());
    }

    static void Popups()
    {
        Reset();
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Ok(a.Popups.Count == 1 && a.Popups[0].StartsWith("The Ironbreaker|You bear the Ironbreaker.") && a.Popups[0].EndsWith("|I bear it|True"), "the new bearer gets a popup, broadcast true", string.Join("\n", a.Popups));
        Reset();
        a = Mk(1001, "Aldric");
        PopupsOff.Add("1001");
        NewLegendary();
        Award("tournament", a);
        Ok(a.Popups.Count == 0 && a.All().Contains("You bear the Ironbreaker"), "/realm popups off is honoured; chat still sent");
        Reset();
        a = Mk(1001, "Aldric"); a.PopupsThrow = true;
        NewLegendary();
        Award("tournament", a);
        Ok(L.Logged.Any(l => l.Contains("ShowPopup failed")) && Bearer == "Aldric", "a failing window falls back to chat");
        Reset();
        a = Mk(1001, "Aldric");
        NewLegendary(c => c.GetType().GetField("UsePopups").SetValue(c, false));
        Award("tournament", a);
        Ok(a.Popups.Count == 0, "UsePopups false: no window");
    }

    static void DataSafety()
    {
        Reset();
        var a = Mk(1001, "Aldric");
        NewLegendary();
        Award("tournament", a);
        Inv(L, "Unload");
        string file = Path.Combine(Dir, "RealmLegendary.json");
        File.WriteAllText(file, "{ \"State\": \"borne\", \"Bear");
        string damaged = File.ReadAllText(file);
        NewLegendary();
        Ok((bool)F(L, "loadFailed") && L.Logged.Any(l => l.StartsWith("ERROR Could not read")), "a damaged data file is reported");
        Tick(3);
        Inv(L, "Unload");
        Ok(File.ReadAllText(file) == damaged, "a damaged data file is never overwritten");
        Ok(!(bool)Inv(L, "AwardEventPrize", "tournament", "1001", "Aldric"), "nothing is awarded while paused");
        File.WriteAllText(file, "null");
        NewLegendary();
        Ok((bool)F(L, "loadFailed"), "a null data file is refused too");
        File.Delete(file);
        NewLegendary();
        Ok(!(bool)F(L, "loadFailed") && State == "keeping", "with the file moved away it starts fresh");
    }

    static void Config()
    {
        Reset();
        NewLegendary(c =>
        {
            var t = c.GetType();
            t.GetField("DamageMultiplier").SetValue(c, 99f);
            t.GetField("TickSeconds").SetValue(c, 0f);
            t.GetField("ChatTitleFormat").SetValue(c, "no name token");
            t.GetField("PrizeEvents").SetValue(c, new List<string> { "Tournament", "crown_night", "kings_hunt", "tournament" });
            t.GetField("WhenWeaponUnknown").SetValue(c, "sometimes");
        });
        var cfg = F(L, "config");
        Ok((float)F(cfg, "DamageMultiplier") == 5f && (float)F(cfg, "TickSeconds") == 1f, "multipliers and the tick are clamped");
        Ok(((string)F(cfg, "ChatTitleFormat")).Contains("%name%"), "a title format without %name% falls back to the default");
        Ok(string.Join(",", (List<string>)F(cfg, "PrizeEvents")) == "tournament,kings_hunt", "PrizeEvents normalised, unknown kinds dropped");
        Ok(L.Logged.Any(l => l.Contains("unknown event 'crown_night'")), "an unknown prize event is warned about");
        Ok((string)F(cfg, "WhenWeaponUnknown") == "melee", "WhenWeaponUnknown falls back to melee");
        Ok(L.timer.LastEvery == 1f, "the tick timer uses the clamped TickSeconds");

        // A base item the server does not know: nothing is minted, the owner is told.
        Reset();
        var a = Mk(1001, "Aldric");
        var adm = Mk(1009, "Steward");
        NewLegendary(c => c.GetType().GetField("BaseItem").SetValue(c, "Sword of Nowhere"));
        Ok(L.Logged.Any(l => l.StartsWith("WARN The Ironbreaker's base item is not known")), "an unknown BaseItem is warned about at start");
        Ok(!Award("tournament", a) && AllBlades().Count == 0, "and the prize is not given");
        Admin(adm);
        Inv(L, "CmdIronbreaker", adm, "ironbreaker", new[] { "grant", "Aldric" });
        Ok(adm.All().Contains("not known to this server") && State == "keeping", "grant explains the unknown item", adm.All());
        Reset();
        NewLegendary(c => c.GetType().GetField("BaseItem").SetValue(c, "iron sword"));
        Ok(L.Logged.Any(l => l.Contains("'Iron Sword'")), "BaseItem matches the exact name in any case");
    }

    // RealmEvents -> RealmLegendary: the real RealmEvents source offers the prize through the non-public hook.
    static void EventsHook()
    {
        Reset();
        var a = Mk(1001, "Aldric", "Varrow"); var b = Mk(1002, "Brannoc", "Corvane"); var c = Mk(1003, "Cerys", "Dunmere");
        NewLegendary();
        var ev = new RealmEvents();
        Inv(ev, "LoadDefaultMessages");
        Inv(ev, "LoadDefaultConfig");
        SetF(ev, "RealmHouses", Houses);
        SetF(ev, "clock", (Func<DateTime>)(() => Clock));
        var legend = new Oxide.Core.Plugins.Plugin { Name = "RealmLegendary", Handler = (h, args) =>
        {
            var mi = typeof(RealmLegendary).GetMethods(BF).FirstOrDefault(x => x.Name == h && !x.IsPublic && x.GetParameters().Length == args.Length);
            return mi != null ? mi.Invoke(L, args) : null;
        } };
        SetF(ev, "RealmLegendary", legend);
        Inv(ev, "Init");
        var adm = c;
        ev.permission.Grants.Add(adm.Id + "|realmevents.admin");
        Inv(ev, "CmdEvent", adm, "event", new[] { "start", "tournament", "30" });
        Inv(ev, "CmdTourney", a, "tourney", new[] { "join" });
        Inv(ev, "CmdTourney", b, "tourney", new[] { "join" });
        Inv(ev, "CmdTourney", c, "tourney", new[] { "join" });
        Action<Player, Player> kill = (k, v) => Inv(ev, "OnEntityDeath", new CodeHatch.Networking.Events.Entities.EntityDeathEvent { Entity = v.Entity, KillingDamage = new Damage { Amount = 100, DamageSource = k.Entity } });
        Clock = Clock.AddMinutes(1); kill(a, b);
        Clock = Clock.AddMinutes(1); kill(a, c);
        Clock = Clock.AddMinutes(1); kill(b, c);
        Inv(ev, "CmdEvent", adm, "event", new[] { "stop", "tournament" });
        Ok(legend.Calls.Any(x => x == "AwardEventPrize(tournament,1001,Aldric)"), "RealmEvents offers the blade to the tournament champion", string.Join("\n", legend.Calls));
        Ok(Bearer == "Aldric" && Count(a) == 1, "and the champion bears it");
        Ok(B().Contains("Aldric, champion of the Royal Tournament, takes up the Ironbreaker!"), "heralded by RealmLegendary", B());
    }
}
