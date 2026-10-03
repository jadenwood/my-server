// Shared test world for RealmLegendary: players with real stack objects, a fake Chronicle, RealmHouses, RealmRenown and
// RealmHerald, a death that fills a corpse the way the game does, and the token audit. Used by the logic tests
// (plugins/docs/RealmLegendary/logic-tests) and the exploit suite (tools/exploit-review/legendary).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Blocks.Collapsing;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Modules.Inventory.Holdables;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Networking.Events.Entities.Players;
using CodeHatch.Networking.Events.Players;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    public static Plugin Chron, Houses, Renown, Herald;
    public static RealmLegendary L;
    public static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    public static Dictionary<string, string> Liege = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Treaties = new HashSet<string>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static List<string> ChronLog = new List<string>();      // "type|title|detail|actors"
    public static List<string> Deeds = new List<string>();
    public static bool ChronRejects;                               // an older RealmChronicle without the type
    public static int ChronId;
    public static string Dir;
    public static InvItemBlueprint Blade { get { return InvBlueprints.Get("Steel Greatsword"); } }

    public static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }
    public static object Inv(object o, string m, params object[] a)
    {
        var mi = o.GetType().GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        try { return mi.Invoke(o, a); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    public static object F(object o, string f) { return o.GetType().GetField(f, BF).GetValue(o); }
    public static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    public static object D(string field) { return F(F(L, "data"), field); }
    public static string State { get { return (string)D("State"); } }
    public static string Bearer { get { return (string)Inv(L, "GetBearerName"); } }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static void Clear() { Server.Broadcasts.Clear(); ChronLog.Clear(); Deeds.Clear(); foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); } }
    public static List<Player> Everyone = new List<Player>();

    public static void Reset()
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        HouseOf.Clear(); Liege.Clear(); Treaties.Clear(); PopupsOff.Clear(); ChronLog.Clear(); Deeds.Clear();
        ItemCollection.World.Clear();
        ChronRejects = false;
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmlegendary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) =>
        {
            if (h != "Log") return null;
            if (ChronRejects && (string)a[0] != "decree") return 0;
            ChronLog.Add(a[0] + "|" + a[1] + "|" + a[2] + "|" + string.Join(";", (string[])a[3]));
            return ++ChronId;
        } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (h == "GetHouse") { string v; ulong u = ulong.Parse((string)a[0]); return HouseOf.TryGetValue(u, out v) ? v : null; }
            if (h == "GetLiege") { string v; return Liege.TryGetValue((string)a[0], out v) ? v : null; }
            if (h == "HasTreaty") return Treaties.Contains(Pair((string)a[0], (string)a[1]));
            return null;
        } };
        Renown = new Plugin { Name = "RealmRenown", Handler = (h, a) => { if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; } };
        Herald = new Plugin { Name = "RealmHerald", Handler = (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null };
    }

    public static string Pair(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a + "|" + b).ToLowerInvariant() : (b + "|" + a).ToLowerInvariant(); }

    public static Player Mk(ulong id, string name, string house = null, int packSlots = 24)
    {
        var p = new Player(id, name, 8, packSlots);
        if (house != null) HouseOf[id] = house;
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        return p;
    }
    public static void Offline(Player p) { Server.ClientPlayers.Remove(p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); }
    public static void Admin(Player p) { L.permission.Grants.Add(p.Id + "|realmlegendary.admin"); }

    public static RealmLegendary NewLegendary(Action<object> tweak = null)
    {
        var p = new RealmLegendary();
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmLegendary).GetNestedType("PluginConfig", BF);
            var obj = System.Text.Json.JsonSerializer.Deserialize(p.Config.Json, cfg, DataFileSystem.Opts);
            tweak(obj);
            p.Config.WriteObject(obj, true);
        }
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "RealmRenown", Renown); SetF(p, "RealmHerald", Herald);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        L = p;
        return p;
    }
    public static void Cfg(string field, object v) { SetF(F(L, "config"), field, v); }

    public static void Tick(int n = 1) { for (int i = 0; i < n; i++) { Clock = Clock.AddSeconds(3); Inv(L, "SafeTick"); } }
    public static void Advance(TimeSpan by) { DateTime until = Clock + by; while (Clock < until) Tick(); }

    // The blade's stack as the plugin knows it this session.
    public static InvGameItemStack Bound { get { return (InvGameItemStack)F(L, "bound"); } }

    public static bool Award(string kind, Player p) { return (bool)Inv(L, "AwardEventPrize", kind, p.Id.ToString(), p.Name); }

    // Every stack of the base item anywhere in the world (packs, hotbars, chests, corpses).
    public static List<InvGameItemStack> AllBlades() { return ItemCollection.World.SelectMany(c => c.GetItems()).Where(s => s.Blueprint.Name == "Steel Greatsword").ToList(); }
    public static int Count(Player p) { return ItemCollection.AutoCount(p.Hotbar, Blade) + ItemCollection.AutoCount(p.Packs, Blade); }
    public static InvGameItemStack Craft(Player p) { var s = new InvGameItemStack(Blade, 1, null); ItemCollection.AutoMergeAdd(p.Packs, s); return s; }
    public static Container Chest(string name = "chest") { return new Container(new Entity { IsPlayer = false }, CollectionTypes.Inventory, 20, name); }
    public static void Move(InvGameItemStack s, ItemCollection to) { s.Collection.RemoveItem(s, true); to.AddItem(s); }
    public static void Drop(InvGameItemStack s) { s.Collection.RemoveItem(s, true); }   // on the ground: in no collection

    // A strike. via: "damager" (Damage.Damager is the held weapon), "right" (TryGetFromSource), "none" (unknown).
    public static float Hit(Player attacker, Entity victim, float amount, InvGameItemStack held, string via = "damager", DamageType types = DamageType.Melee | DamageType.Slash)
    {
        var d = Strike(attacker, held, via, amount, types);
        var evt = new EntityDamageEvent { Entity = victim, Damage = d };
        Inv(L, "OnEntityHealthChange", evt);
        return d.Amount;
    }
    public static Damage Strike(Player attacker, InvGameItemStack held, string via, float amount, DamageType types)
    {
        var d = new Damage { Amount = amount, DamageSource = attacker.Entity, DamageTypes = types };
        var h = new BipedHoldable { Stack = held };
        if (via == "damager") { var e = new Entity { IsPlayer = false }; e.Components.Add(h); d.Damager = new HoldableObject { Entity = e }; }
        else if (via == "right") d.RightHand = h;
        return d;
    }
    public static float HitBlock(Player attacker, float amount, InvGameItemStack held, string via = "damager")
    {
        var evt = new CubeDamageEvent { Damage = Strike(attacker, held, via, amount, DamageType.Melee | DamageType.Slash) };
        Inv(L, "OnCubeTakeDamage", evt);
        return evt.Damage.Amount;
    }
    public static Entity Gate() { var e = new Entity { IsPlayer = false }; e.Components.Add(new PlaceableBlockAssociation()); return e; }

    // A death as the game does it [CODE]: PlayerDeathEvent -> OnKingDeath (Early) -> the corpse is filled from every
    // container (Normal) -> EntityHealth.InvokeDeath -> OnEntityDeath. kingHook false = a cancelled PlayerDeathEvent.
    public static Container Die(Player victim, Player killer, bool kingHook = true)
    {
        var dmg = new Damage { Amount = 500, DamageSource = killer != null ? killer.Entity : null };
        if (kingHook) Inv(L, "OnKingDeath", new PlayerDeathEvent { PlayerId = victim.Id, KillingDamage = dmg });
        var corpse = new Container(new Entity { IsPlayer = false }, CollectionTypes.Inventory, 64, victim.Name + "'s corpse");
        foreach (var c in victim.Entity.Containers) foreach (var s in c.Contents.GetItems()) { c.Contents.RemoveItem(s, true); corpse.Contents.AddItem(s); }
        Inv(L, "OnEntityDeath", new EntityDeathEvent { Entity = victim.Entity, KillingDamage = dmg });
        return corpse;
    }

    // Unload and load again, as a restart or hot reload does. Session state (the bound stack) is lost.
    public static void Reload(Action<object> tweak = null) { Inv(L, "Unload"); NewLegendary(tweak); }

    // Audit (from /ironbreaker status): Minted + Restored = Reclaimed + Unrecovered + (1 if in hand).
    public static bool AuditBalanced()
    {
        int minted = (int)D("Minted"), restored = (int)D("Restored"), reclaimed = (int)D("Reclaimed"), unrec = (int)D("Unrecovered");
        int inWorld = (bool)D("Custody") ? 0 : 1;
        return minted + restored == reclaimed + unrec + inWorld;
    }
    public static string AuditText()
    {
        return "minted " + D("Minted") + " restored " + D("Restored") + " reclaimed " + D("Reclaimed") + " unrecovered " + D("Unrecovered") + " custody " + D("Custody");
    }
}
