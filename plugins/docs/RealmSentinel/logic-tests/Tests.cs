// Behaviour tests for plugins/RealmSentinel.cs. Run with run.sh (see there for what this does and does not prove).
// Each cheat is replayed as the server would see it, and honest players (high ping, lag spikes, cliffs, stairs, heavy
// gathering, looting, trading, chatting, flaky connections) are replayed for an hour each: they must never alert.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Damaging;
using CodeHatch.Engine.Behaviours;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Core.Interaction.Behaviours.Networking;
using CodeHatch.Engine.Networking;
using CodeHatch.Inventory.Blueprints;
using CodeHatch.ItemContainer;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Networking.Events.Players;
using CodeHatch.Thrones.AncientThrone;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static RealmSentinel P;
    static Plugin Warden;
    static List<object[]> WardenCalls = new List<object[]>();
    static string Dir;
    static Random Rng = new Random(20261003);
    const string Admin = "realmsentinel.admin";
    static ulong NextId = 76561190000001000;

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
    static object Data() { return F(P, "data"); }
    static IList L(string f) { return (IList)F(Data(), f); }
    static IDictionary Players() { return (IDictionary)F(Data(), "Players"); }
    static object Rec(Player p) { return Players()[p.Id.ToString()]; }
    static object Cfg(string section) { return F(F(P, "config"), section); }
    static void SetCfg(string section, string field, object v) { SetF(Cfg(section), field, v); }
    static int Count(Player p, string kind)
    {
        if (!Players().Contains(p.Id.ToString())) return 0;
        var d = (IDictionary)F(Rec(p), "Counts");
        return d.Contains(kind) ? (int)d[kind] : 0;
    }
    static int Total(Player p)
    {
        if (!Players().Contains(p.Id.ToString())) return 0;
        int n = 0;
        foreach (DictionaryEntry e in (IDictionary)F(Rec(p), "Counts")) n += (int)e.Value;
        return n;
    }
    static double Score(Player p) { return (double)Inv(P, "GetSentinelScore", p.Id); }
    static bool Frozen(Player p) { return (bool)Inv(P, "IsSentinelFrozen", p.Id); }
    static int Evidence(string kind) { return L("Evidence").Cast<object>().Count(a => (string)F(a, "Kind") == kind); }
    static int Alerts(string response) { return L("Alerts").Cast<object>().Count(a => (string)F(a, "Response") == response); }
    static int AlertsWith(Player p, string response)
    {
        return L("Alerts").Cast<object>().Count(a => (string)F(a, "PlayerId") == p.Id.ToString() && (string)F(a, "Response") == response);
    }
    static int AlertsFor(Player p) { return L("Alerts").Cast<object>().Count(a => (string)F(a, "PlayerId") == p.Id.ToString()); }
    static string Kinds(Player p)
    {
        if (!Players().Contains(p.Id.ToString())) return "(none)";
        var parts = new List<string>();
        foreach (DictionaryEntry e in (IDictionary)F(Rec(p), "Counts")) parts.Add(e.Key + "x" + e.Value);
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    static void SetTime(DateTime utc)
    {
        double skew = (utc - Epoch).TotalSeconds - (DateTime.UtcNow - Epoch).TotalSeconds;
        SetF(P, "clockSkew", skew);
    }
    static void Advance(double seconds) { SetF(P, "clockSkew", (double)F(P, "clockSkew") + seconds); }

    static Player Mk(string name, int ping = 60, bool connect = true)
    {
        var p = new Player(NextId++, name);
        p.Connection.AveragePing = ping;
        p.Entity.Position = new Vector3(1000 + Rng.Next(0, 500), 50, 1000 + Rng.Next(0, 500));
        Server.ClientPlayers.Add(p);
        if (connect) Inv(P, "OnPlayerConnected", p);
        return p;
    }
    static Player MkAdmin(string name)
    {
        var p = new Player(NextId++, name);
        P.permission.Grants.Add(p.Id + "|" + Admin);
        Server.ClientPlayers.Add(p);
        Inv(P, "OnPlayerConnected", p);
        return p;
    }
    static void Leave(Player p) { Inv(P, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    static void Rejoin(Player p) { Server.ClientPlayers.Add(p); Inv(P, "OnPlayerConnected", p); }
    static void Cmd(Player p, string line)
    {
        string[] args = line.Length == 0 ? new string[0] : line.Split(' ');
        Inv(P, "CmdSentinel", p, "sentinel", args);
    }
    static object GameCmd(Player p, string cmd, params string[] args)
    {
        Inv(P, "OnServerCommand", cmd, args);
        object r = p != null ? Inv(P, "OnPlayerCommand", p, cmd, args) : null;
        P.RunTicks();
        return r;
    }
    static void Clear() { foreach (var p in Server.ClientPlayers) p.Messages.Clear(); }
    static void Tick() { Inv(P, "MovementTick"); }
    static void ItemTick() { Inv(P, "ItemTick"); }
    static void House() { Inv(P, "Housekeeping"); }
    static void At(Player p, float x, float y, float z) { p.Entity.Position = new Vector3(x, y, z); }
    static void Shift(Player p, float dx, float dy, float dz)
    {
        var v = p.Entity.Position;
        p.Entity.Position = new Vector3(v.x + dx, v.y + dy, v.z + dz);
    }
    // One movement sample after dt seconds with the body moved by (dx, dy, dz).
    static void Step(Player p, double dt, float dx, float dy, float dz) { Advance(dt); Shift(p, dx, dy, dz); Tick(); }

    static EntityDamageEvent Hit(Player attacker, Entity target, float amount, DamageType type, Player sender)
    {
        var e = new EntityDamageEvent { Entity = target, Damage = new Damage { Amount = amount, DamageSource = attacker.Entity, DamageTypes = type }, Sender = sender };
        Inv(P, "OnEntityHealthChange", e);
        return e;
    }
    static Entity Creature(Player near, float dist)
    {
        var v = near.Entity.Position;
        return new Entity { IsPlayer = false, Position = new Vector3(v.x + dist, v.y, v.z) };
    }
    static void PlaceAt(Player victim, Player from, float dist)
    {
        var v = from.Entity.Position;
        victim.Entity.Position = new Vector3(v.x + dist, v.y, v.z);
    }
    static void Kill(Player killer, Player victim)
    {
        Inv(P, "OnEntityDeath", new EntityDeathEvent { Entity = victim.Entity, KillingDamage = new Damage { Amount = 50, DamageSource = killer.Entity } });
    }
    static object Say(Player p, string msg) { return Inv(P, "OnPlayerChat", new PlayerMessageEvent { Player = p, Message = msg, Sender = p }); }
    static Entity Chest(params object[] contents)
    {
        var e = new Entity { IsPlayer = false };
        var c = new Container();
        for (int i = 0; i < contents.Length; i += 2) c.Contents.Add((string)contents[i], (int)contents[i + 1]);
        e.Components[typeof(Container)] = c;
        return e;
    }
    static Container Box(Entity chest) { return (Container)chest.Components[typeof(Container)]; }
    static object Open(Player p, Entity chest)
    {
        return Inv(P, "OnPlayerInteract", new InteractEvent { Entity = chest, ControllerEntity = p.Entity, Sender = p });
    }

    static RealmSentinel NewPlugin(string configJson)
    {
        var p = new RealmSentinel();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmWarden", Warden);
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void Reset(string configJson = null, bool keepFiles = false)
    {
        if (P != null) { try { Inv(P, "Unload"); } catch { } }
        if (!keepFiles) foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Server.ClientPlayers.Clear();
        Server.Kicks.Clear();
        Server.Bans.Clear();
        Server.RefuseBans = false;
        EventManager.Subscribers.Clear();
        CharacterTeleport.Calls = 0;
        WardenCalls.Clear();
        P = NewPlugin(configJson);
        SetTime(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
    }
    static void Enforce() { SetCfg("Responses", "Mode", "enforce"); }

    static void Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "sentineltest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Warden = new Plugin { Name = "RealmWarden", Handler = (h, a) => { if (h == "RaiseWardenAlert") { WardenCalls.Add(a); return 1; } return null; } };
        try { Run(); }
        catch (Exception ex) { fail++; Console.WriteLine("CRASH " + ex); }
        finally { try { Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static void Run()
    {
        ConfigTests();
        MovementCheats();
        MovementGrace();
        CombatCheats();
        ItemCheats();
        FloodAndConnections();
        Impersonation();
        ScoreAndResponses();
        Commands();
        FeedLogsAndWarden();
        Persistence();
        Api();
        HonestPlayers();
    }

    // ---------------------------------------------------------------- Config
    static void ConfigTests()
    {
        Reset();
        Ok(P.Config.Json.Contains("\"Mode\": \"watch\"") && P.Config.Json.Contains("\"AutoBan\": false"), "default config: watch mode, no automatic bans");
        Ok(P.Config.Json.Contains("\"MaxSpeed\": 10") && P.Config.Json.Contains("\"MeleeRange\": 7.5"), "default limits are the game's own numbers (10 m/s, 7.5 m)");
        Ok(EventManager.Subscribers.Count == 1, "subscribes to the game's TeleportEvent on start");
        Inv(P, "OnServerInitialized");
        Ok(EventManager.Subscribers.Count == 1, "OnServerInitialized again (hot load) does not subscribe twice");
        P = NewPlugin("{\"Responses\":{\"Mode\":\"chaos\",\"AlertScore\":40,\"FreezeScore\":10,\"KickScore\":5},\"Score\":{\"Weights\":{\"speed\":-5}},\"Movement\":null}");
        Ok((string)F(Cfg("Responses"), "Mode") == "watch", "an unknown Mode falls back to watch");
        Ok((float)F(Cfg("Responses"), "FreezeScore") == 40f && (float)F(Cfg("Responses"), "KickScore") == 40f, "freeze and kick scores are never below the alert score");
        var w = (Dictionary<string, float>)F(Cfg("Score"), "Weights");
        Ok(w["speed"] == 0f && w.ContainsKey("teleport") && w["teleport"] == 25f, "weights are clamped and missing ones filled from the defaults");
        Ok(Cfg("Movement") != null && (float)F(Cfg("Movement"), "MaxSpeed") == 10f, "a null section gets its defaults");
        Inv(P, "Unload");
        Ok(EventManager.Subscribers.Count == 1, "Unload removes only its own subscription (the first instance's stays until it unloads)");
    }

    // ---------------------------------------------------------------- Movement cheats
    static void MovementCheats()
    {
        Reset();
        // Speed hack at 1.6x the game's limit, sustained.
        var sp = Mk("Swiftfoot", 80);
        Tick();
        for (int i = 0; i < 40; i++) Step(sp, 1.0, 16f, 0, 0);
        Ok(Count(sp, "speed") >= 1, "speed hack (16 m/s) is detected", Kinds(sp));
        Ok(Score(sp) >= 20, "a sustained speed hack reaches the alert score within 40 s", "score " + Score(sp));
        Ok(Evidence("speed") >= 1 && ((string)F(L("Evidence")[0], "Detail")).Contains("m/s"), "speed evidence says how fast and where");
        var sup = (IDictionary)F(Rec(sp), "Suppressed");
        Ok(sup.Contains("speed") && (int)sup["speed"] >= 1, "repeats inside the kind cooldown are counted but not scored again");

        // A milder hack (1.35x) still drains the budget and is caught.
        var mild = Mk("Lightstep", 80);
        Tick();
        for (int i = 0; i < 60; i++) Step(mild, 1.0, 13.5f, 0, 0);
        Ok(Count(mild, "speed") >= 1, "a 1.35x speed hack is caught once the saved budget is spent", Kinds(mild));

        // Teleport: one big jump.
        var tp = Mk("Blinker", 80);
        Tick();
        Step(tp, 1.0, 5f, 0, 0);
        Step(tp, 1.0, 250f, 0, 0);
        Ok(Count(tp, "teleport") == 1, "a 250 m jump in one second is a teleport", Kinds(tp));
        Ok(Score(tp) >= 50, "a teleport scores high enough to freeze (in enforce mode)", "score " + Score(tp));
        Ok(Alerts("would freeze") >= 1 && !Frozen(tp), "watch mode: 'would freeze', nobody frozen");

        // Fly: sustained ascent with no fall.
        var fly = Mk("Kestrel", 80);
        Tick();
        for (int i = 0; i < 14; i++) Step(fly, 1.0, 1f, 4f, 0);
        Ok(Count(fly, "fly") == 1, "rising 4 m/s for 14 s is flagged as fly", Kinds(fly));
        Ok(Count(fly, "speed") == 0, "the climb itself is within the speed limit");

        // Fast vertical rise counts against the speed budget too (planar + upward, as the game measures).
        var rocket = Mk("Updraft", 80);
        Tick();
        for (int i = 0; i < 12; i++) Step(rocket, 1.0, 0, 15f, 0);
        Ok(Count(rocket, "speed") + Count(rocket, "fly") >= 2, "rising 15 m/s trips both the speed budget and the ascent check", Kinds(rocket));

        // Falling fast is allowed up to the falling limit; horizontal teleport while falling is not.
        var faller = Mk("Plummet", 80);
        Tick();
        for (int i = 0; i < 4; i++) Step(faller, 1.0, 9f, -18f, 0);
        Ok(Total(faller) == 0, "falling 18 m/s while running 9 m/s is honest", Kinds(faller));
        Step(faller, 1.0, 120f, -18f, 0);
        Ok(Count(faller, "teleport") == 1, "a 120 m sideways jump while falling is still a teleport", Kinds(faller));
    }

    // ---------------------------------------------------------------- Grace and exemptions
    static void MovementGrace()
    {
        Reset();
        var a = Mk("Pilgrim", 80);
        Tick();
        Step(a, 1, 3, 0, 0);
        // Admin /tp: the game's CharacterTeleport.Teleport raises a server TeleportEvent.
        a.Entity.GetOrCreate<CharacterTeleport>().Teleport(new Vector3(5000, 80, 5000));
        Step(a, 1, 2, 0, 0);
        for (int i = 0; i < 5; i++) Step(a, 1, 6, 0, 0);
        Ok(Total(a) == 0, "a server teleport (TeleportEvent from the server) gives grace", Kinds(a));

        // A client-sent TeleportEvent never gives grace.
        var b = Mk("Forger", 80);
        Tick();
        Step(b, 1, 3, 0, 0);
        EventManager.CallEvent(new TeleportEvent { Entity = b.Entity, Position = new Vector3(0, 0, 0), Sender = b });
        Step(b, 1, 300, 0, 0);
        Ok(Count(b, "teleport") == 1, "a TeleportEvent sent by the client itself gives no grace", Kinds(b));

        // Respawn far away: the hook gives grace, and a new body restarts sampling anyway.
        var c = Mk("Phoenix", 80);
        Tick();
        Step(c, 1, 3, 0, 0);
        Kill(Mk("Reaper", 80), c);
        c.Respawn();
        Inv(P, "OnPlayerRespawn", new PlayerRespawnEvent { Player = c });
        At(c, 9000, 60, 9000);
        Step(c, 1, 1, 0, 0);
        for (int i = 0; i < 9; i++) Step(c, 1, 5, 0, 0);
        Ok(Total(c) == 0, "respawning far away is not a teleport", Kinds(c));

        // New body without any respawn hook (the core says the hook is not always called).
        var d = Mk("Wanderer", 80);
        Tick();
        Step(d, 1, 3, 0, 0);
        d.Respawn();
        At(d, 200, 60, 200);
        Step(d, 1, 0, 0, 0);
        Ok(Total(d) == 0, "a new Entity restarts sampling even without OnPlayerRespawn", Kinds(d));

        // /tp typed by an admin with the game permission: grace for the named target too.
        var mod = Mk("Steward Bran", 80);
        mod.GamePerms.Add("rok.command.teleport");
        var e = Mk("Summoned", 80);
        Tick();
        GameCmd(mod, "tp", "Summoned", "Steward", "Bran");
        Shift(e, 900, 0, 0);
        Step(e, 1, 1, 0, 0);
        Ok(Total(e) == 0, "/tp by a player with the game permission gives grace to the named player", Kinds(e));

        // /tp typed by anyone else gives nothing.
        var f = Mk("Trickster", 80);
        Tick();
        Step(f, 1, 2, 0, 0);
        GameCmd(f, "tp", "Trickster");
        Step(f, 1, 400, 0, 0);
        Ok(Count(f, "teleport") == 1, "/tp without the game permission gives no grace", Kinds(f));

        // Console /tp (no OnPlayerCommand after OnServerCommand): trusted on the next tick.
        var g = Mk("Ferried", 80);
        Tick();
        Step(g, 1, 2, 0, 0);
        GameCmd(null, "tp", "Ferried", "Pilgrim");
        Step(g, 1, 700, 0, 0);
        Ok(Total(g) == 0, "a console /tp gives grace to the named player", Kinds(g));

        // /tpdelay: grace lasts until the return trip.
        var h = Mk("Courier", 80);
        h.GamePerms.Add("rok.command.teleport");
        h.GamePerms.Add("rok.command.teleport.coord");
        SetCfg("Movement", "ExemptGameFlyPermissions", false);      // so the grace itself is what is tested
        Tick();
        Step(h, 1, 2, 0, 0);
        GameCmd(h, "tpdelay", "100", "100", "20");
        Step(h, 1, 900, 0, 0);                                     // out...
        for (int i = 0; i < 19; i++) Step(h, 1, 1, 0, 0);
        Step(h, 1, -900, 0, 0);                                    // ...and back 20 s later
        Ok(Total(h) == 0, "/tpdelay grace lasts until the return trip", Kinds(h));
        var h2 = Mk("Courier Two", 80);
        h2.GamePerms.Add("rok.command.teleport");
        Tick();
        Step(h2, 1, 2, 0, 0);
        GameCmd(h2, "tp", "Courier Two");
        Step(h2, 1, 900, 0, 0);
        for (int i = 0; i < 19; i++) Step(h2, 1, 1, 0, 0);
        Step(h2, 1, -900, 0, 0);
        Ok(Count(h2, "teleport") == 1, "a plain /tp grace does not cover a jump 20 s later", Kinds(h2));
        SetCfg("Movement", "ExemptGameFlyPermissions", true);

        // Game fly/teleport permissions exempt movement checks (as the game's own check does).
        var fl = Mk("Skywright", 80);
        fl.GamePerms.Add("rok.command.admin.fly");
        Tick();
        for (int i = 0; i < 15; i++) Step(fl, 1, 30, 5, 0);
        Ok(Total(fl) == 0, "a player with the game's fly permission is exempt from movement checks", Kinds(fl));

        // Sentinel admins are exempt from everything (AdminsExempt).
        var adm = MkAdmin("Warden Hale");
        Tick();
        for (int i = 0; i < 5; i++) Step(adm, 1, 300, 0, 0);
        Ok(!Players().Contains(adm.Id.ToString()) || Total(adm) == 0, "Sentinel admins are exempt (AdminsExempt)");

        // A stalled timer (no sample for 30 s) restarts sampling instead of flagging.
        var st = Mk("Patience", 80);
        Tick();
        Advance(30);
        Shift(st, 250, 0, 0);
        Tick();
        Ok(Total(st) == 0, "a 30 s gap between samples restarts sampling", Kinds(st));

        // Frozen players are pinned back to where they were frozen (enforce, PinFrozenPosition).
        Enforce();
        var fz = Mk("Fidget", 80);
        Tick();
        Cmd(MkAdmin("Marshal"), "freeze Fidget 10");
        var spot = fz.Entity.Position;
        Shift(fz, 20, 0, 0);
        Step(fz, 1, 0, 0, 0);
        Ok(CharacterTeleport.Calls >= 1 && Math.Abs(fz.Entity.Position.x - spot.x) < 0.01, "a frozen player who moves away is put back");
        Ok(Total(fz) == 0, "the pin-back teleport itself is not a detection", Kinds(fz));
    }

    // ---------------------------------------------------------------- Combat
    static void CombatCheats()
    {
        Reset();
        var archer = Mk("Longshot", 90);
        var target = Mk("Target", 90);
        PlaceAt(target, archer, 500);
        Hit(archer, target.Entity, 30, DamageType.Projectile, archer);
        Ok(Count(archer, "reach") == 1, "an arrow hit from 500 m is flagged (shooter-reported)", Kinds(archer));
        var archer2 = Mk("Fletcher", 90);
        PlaceAt(target, archer2, 120);
        Hit(archer2, target.Entity, 30, DamageType.Projectile, archer2);
        Ok(Total(archer2) == 0, "an arrow hit from 120 m is fine");

        var brute = Mk("Brute", 90);
        Hit(brute, Creature(brute, 20), 10, DamageType.Melee, brute);
        Ok(Count(brute, "reach") == 1, "a melee hit on a creature 20 m away is flagged (attacker-reported)", Kinds(brute));

        // Framing: the victim reports melee hits on itself ([DEC] MeleeVoodooModule). A far 'hit' must not score the attacker.
        var innocent = Mk("Innocent", 90);
        var framer = Mk("Framer", 90);
        PlaceAt(framer, innocent, 40);
        for (int i = 0; i < 30; i++) { Hit(innocent, framer.Entity, 999, DamageType.Melee, framer); Advance(0.05); }
        Ok(Total(innocent) == 0, "victim-reported melee hits never score the named attacker (framing-proof)", Kinds(innocent));
        Ok(Total(framer) == 0, "nor the victim (lag on their side is not cheating)");

        // Server-made damage is ignored.
        var trap = Mk("Trapper", 90);
        Hit(trap, Creature(trap, 50), 500, DamageType.Melee, Server.ServerPlayer);
        Ok(Total(trap) == 0, "damage the server itself sent is ignored");

        // Ping credit on reach: 400 + 400 ms ping allows a few metres more.
        var lag1 = Mk("Laggard", 400);
        var lag2 = Mk("Laggard Two", 400);
        PlaceAt(lag2, lag1, 12);
        Hit(lag1, Creature(lag1, 12), 10, DamageType.Melee, lag1);
        Ok(Total(lag1) == 0, "a melee hit at 12 m with 400 ms ping is within the ping credit");

        // Damage.
        var hard = Mk("Hardhitter", 90);
        PlaceAt(target, hard, 30);
        Hit(hard, target.Entity, 400, DamageType.Projectile, hard);
        Ok(Count(hard, "damage") == 1, "a 400-damage arrow is flagged", Kinds(hard));
        var bal = Mk("Siegewright", 90);
        PlaceAt(target, bal, 60);
        Hit(bal, target.Entity, 125, DamageType.Projectile, bal);
        Ok(Total(bal) == 0, "a 125-damage ballista bolt (the game's own cap) is fine");
        var club = Mk("Clubber", 90);
        Hit(club, Creature(club, 2), 120, DamageType.Bash, club);
        Ok(Count(club, "damage") == 1, "a 120-damage club hit on a creature is flagged (Bash counts as melee)", Kinds(club));

        // Fire rate.
        var rapid = Mk("Rapidfire", 90);
        PlaceAt(target, rapid, 30);
        for (int i = 0; i < 30; i++) { Hit(rapid, target.Entity, 20, DamageType.Projectile, rapid); Advance(0.15); }
        Ok(Count(rapid, "fire_rate") == 1, "30 arrows in 4.5 s is flagged", Kinds(rapid));
        var bow = Mk("Bowyer", 90);
        PlaceAt(target, bow, 30);
        for (int i = 0; i < 40; i++) { Hit(bow, target.Entity, 20, DamageType.Projectile, bow); Advance(1.1); }
        Ok(Total(bow) == 0, "an arrow every 1.1 s for 44 s is fine");
        var swing = Mk("Whirlwind", 90);
        var boar = Creature(swing, 2);
        for (int i = 0; i < 25; i++) { Hit(swing, boar, 10, DamageType.Slash, swing); Advance(0.15); }
        Ok(Count(swing, "fire_rate") == 1, "25 sword hits on one creature in under 4 s is flagged", Kinds(swing));
        var bunch = Mk("Bunched", 350);
        var boar2 = Creature(bunch, 2);
        for (int i = 0; i < 6; i++) { for (int k = 0; k < 3; k++) Hit(bunch, boar2, 10, DamageType.Slash, bunch); Advance(1.2); }
        Ok(Total(bunch) == 0, "hits bunched three at a time by lag (at an honest rate overall) are fine");

        // Kill rate.
        var killer = Mk("Butcher", 90);
        var victims = new List<Player>();
        for (int i = 0; i < 10; i++) victims.Add(Mk("Victim" + i, 90));
        foreach (var v in victims) { Kill(killer, v); Advance(8); }
        Ok(Count(killer, "kill_rate") == 1, "10 player kills in 80 s is flagged", Kinds(killer));
        var raider = Mk("Raider", 90);
        var sleepers = new List<Player>();
        for (int i = 0; i < 12; i++) { var s = Mk("Sleeper" + i, 90); sleepers.Add(s); Leave(s); }
        foreach (var s in sleepers) { Kill(raider, s); Advance(5); }
        Ok(Total(raider) == 0, "killing sleeping bodies (offline players) does not count toward the kill rate");

        // Frozen attacker cannot hurt anyone (enforce).
        Enforce();
        var frozen = Mk("Statue", 90);
        Cmd(MkAdmin("Judge"), "freeze Statue 5");
        PlaceAt(target, frozen, 2);
        var ev = Hit(frozen, target.Entity, 15, DamageType.Melee, target);
        Ok(ev.Cancelled && ev.Damage.Amount == 0f, "a frozen player's hits are cancelled (enforce)");
    }

    // ---------------------------------------------------------------- Items
    static void ItemCheats()
    {
        Reset();
        var dup = Mk("Duper", 90);
        dup.Inv.Add("Stone", 100);
        ItemTick();
        Advance(10);
        dup.Inv.Add("Stone", 5000);
        ItemTick();
        Ok(Count(dup, "item_jump") == 1, "+5000 Stone with no source is an item jump", Kinds(dup));
        Ok(L("Evidence").Cast<object>().Any(e => ((string)F(e, "Detail")).Contains("+5000 Stone")), "the evidence names the item and amount");

        // Looting a chest: the chest's loss explains the player's gain.
        var looter = Mk("Looter", 90);
        ItemTick();
        var chest = Chest("Iron", 2000, "Wood", 900);
        Open(looter, chest);
        Advance(10);
        Box(chest).Contents.Add("Iron", -2000);
        looter.Inv.Add("Iron", 2000);
        ItemTick();
        Advance(10);
        Box(chest).Contents.Add("Wood", -900);
        looter.Bar.Add("Wood", 900);
        ItemTick();
        Ok(Total(looter) == 0, "items taken from an opened chest are explained", Kinds(looter));

        // But opening a chest does not explain more than the chest lost.
        var sly = Mk("Sly", 90);
        ItemTick();
        var bait = Chest("Iron", 10);
        Open(sly, bait);
        Advance(10);
        Box(bait).Contents.Add("Iron", -10);
        sly.Inv.Add("Iron", 3010);
        ItemTick();
        Ok(Count(sly, "item_jump") == 1, "opening a chest of 10 does not hide a 3000 spawn", Kinds(sly));

        // Drop and pick back up; equip; hotbar shuffles.
        var tidy = Mk("Tidy", 90);
        tidy.Inv.Add("Wood", 1000);
        ItemTick();
        Advance(10); tidy.Inv.Add("Wood", -1000); ItemTick();
        Advance(10); tidy.Inv.Add("Wood", 1000); ItemTick();
        Advance(10); tidy.Inv.Add("Wood", -800); tidy.Bar.Add("Wood", 800); ItemTick();
        Ok(Total(tidy) == 0, "dropping and picking up your own items, and hotbar moves, are explained", Kinds(tidy));

        // A trusted /give (game permission) and a console /give explain the gain; an untrusted one does not.
        var gm = Mk("Quartermaster", 90);
        gm.GamePerms.Add("rok.command.items.give");
        var lucky = Mk("Lucky", 90);
        ItemTick();
        GameCmd(gm, "give", "Iron", "900", "Lucky");
        Advance(10); lucky.Inv.Add("Iron", 900); ItemTick();
        Ok(Total(lucky) == 0, "/give by a player with the game permission explains the target's gain", Kinds(lucky));
        var lucky2 = Mk("Lucky Two", 90);
        ItemTick();
        GameCmd(null, "give", "Iron", "900", "Lucky Two");
        Advance(10); lucky2.Inv.Add("Iron", 900); ItemTick();
        Ok(Total(lucky2) == 0, "a console /give explains the named player's gain");
        var faker = Mk("Faker", 90);
        ItemTick();
        GameCmd(faker, "give", "Iron", "900", "Faker");
        Advance(10); faker.Inv.Add("Iron", 900); ItemTick();
        Ok(Count(faker, "item_jump") == 1, "/give without the game permission explains nothing", Kinds(faker));

        // A Realm command that pays items (market, contract...) explains the next minute's gains.
        var buyer = Mk("Buyer", 90);
        ItemTick();
        GameCmd(buyer, "market", "buy", "12");
        Advance(10); buyer.Inv.Add("Iron", 1500); ItemTick();
        Ok(Total(buyer) == 0, "a market purchase explains the items it delivers", Kinds(buyer));
        // ...and a plugin can say so itself.
        var paid = Mk("Prizewinner", 90);
        ItemTick();
        Inv(P, "SentinelItemSource", paid.Id, 30f);
        Advance(10); paid.Inv.Add("Stone", 300); paid.Inv.Add("Gold", 600); ItemTick();
        Ok(Total(paid) == 0, "SentinelItemSource explains a plugin's payout");

        // Gather rate: unexplained gains add up over the window.
        var farm = Mk("Bot Farmer", 90);
        ItemTick();
        for (int i = 0; i < 30; i++) { Advance(10); farm.Inv.Add("Stone", 400); ItemTick(); }
        Ok(Count(farm, "gather_rate") >= 1, "+400 a sample for 5 min (12000) is a gather-rate spike", Kinds(farm));
        Ok(Count(farm, "item_jump") == 0, "each sample alone stays under the jump amount");

        // Crafting: the product is explained; the rate is watched.
        var smith = Mk("Smith", 90);
        ItemTick();
        Inv(P, "OnItemCrafted", new ItemCrafterFinishEvent { Entity = smith.Entity, Crafter = new ItemCrafter { Product = new InvItemBlueprint("Iron Arrow") }, Sender = smith });
        Advance(10); smith.Inv.Add("Iron Arrow", 600); ItemTick();
        Ok(Total(smith) == 0, "a crafted product's gain is explained", Kinds(smith));
        var macro = Mk("Macro", 90);
        for (int i = 0; i < 50; i++) { Inv(P, "OnItemCrafted", new ItemCrafterFinishEvent { Entity = macro.Entity, Crafter = new ItemCrafter { Product = new InvItemBlueprint("Plank") }, Sender = macro }); Advance(0.5); }
        Ok(Count(macro, "craft_rate") == 1, "50 crafts finished in 25 s is a craft-rate spike", Kinds(macro));
        var station = Mk("Station User", 90);
        var bench = new Entity { IsPlayer = false };
        for (int i = 0; i < 50; i++) { Inv(P, "OnItemCrafted", new ItemCrafterFinishEvent { Entity = bench, Crafter = new ItemCrafter { Product = new InvItemBlueprint("Plank") }, Sender = station }); Advance(0.5); }
        Ok(Count(station, "craft_rate") == 1, "a station's crafts count for the client that sent them");

        // A respawn kit is not a jump (new body = new baseline).
        var reborn = Mk("Reborn", 90);
        ItemTick();
        reborn.Respawn();
        reborn.Inv.Add("Stone", 2000);
        Advance(10); ItemTick();
        Ok(Total(reborn) == 0, "a new body starts a new item baseline", Kinds(reborn));

        // Frozen players cannot loot or craft (enforce).
        Enforce();
        var cold = Mk("Cold", 90);
        Cmd(MkAdmin("Provost"), "freeze Cold 5");
        var r1 = Open(cold, Chest("Iron", 5));
        var craft = new ItemCrafterStartEvent { Entity = cold.Entity, Sender = cold };
        var r2 = Inv(P, "OnItemCraft", craft);
        Ok(r1 != null && r2 != null && craft.Cancelled, "a frozen player cannot open containers or craft (enforce)");
    }

    // ---------------------------------------------------------------- Floods and connections
    static void FloodAndConnections()
    {
        Reset();
        var spam = Mk("Spammer", 90);
        for (int i = 0; i < 15; i++) { Say(spam, "buy gold " + i); Advance(0.5); }
        Ok(Count(spam, "chat_flood") == 1, "15 messages in 7.5 s is a chat flood", Kinds(spam));
        Ok(Say(spam, "hello") == null, "Sentinel never blocks chat itself (RealmWarden mutes)");
        var chatty = Mk("Chatty", 90);
        for (int i = 0; i < 300; i++) { Say(chatty, "news " + i); Advance(2); }
        Ok(Total(chatty) == 0, "one message every 2 s for 10 min is fine");

        var bot = Mk("Bot", 90);
        int blocked = 0;
        for (int i = 0; i < 60; i++) { if (GameCmd(bot, "house", "list") != null) blocked++; Advance(0.1); }
        Ok(blocked == 45, "commands over 15 per 10 s are refused", "blocked " + blocked);
        Ok(Count(bot, "command_flood") >= 1, "a command flood is detected", Kinds(bot));
        Ok(bot.All().Contains("Slow down"), "the flooder is told to slow down");
        var adm = MkAdmin("Castellan");
        int admBlocked = 0;
        for (int i = 0; i < 40; i++) { if (GameCmd(adm, "house", "list") != null) admBlocked++; Advance(0.1); }
        Ok(admBlocked == 0, "admins are not rate-limited");

        var cycler = Mk("Cycler", 90);
        for (int i = 0; i < 7; i++) { Advance(40); Leave(cycler); Advance(5); Rejoin(cycler); }
        Ok(Count(cycler, "reconnect_cycle") == 1, "8 connects in 6 min is reconnect cycling", Kinds(cycler));
        Ok(Inv(P, "CanUserLogin", "Cycler", cycler.Id.ToString(), "10.0.0.9") == null, "no login refusal unless opted in");
        SetCfg("Connections", "RefuseWhileCycling", true);
        Ok(Inv(P, "CanUserLogin", "Cycler", cycler.Id.ToString(), "10.0.0.9") == null, "refusal needs enforce mode too");
        Enforce();
        var refused = Inv(P, "CanUserLogin", "Cycler", cycler.Id.ToString(), "10.0.0.9") as string;
        Ok(refused != null && refused.Contains("Too many reconnects"), "opted in + enforce: a cycling player is refused for a while");
        Advance(200);
        Ok(Inv(P, "CanUserLogin", "Cycler", cycler.Id.ToString(), "10.0.0.9") == null, "the refusal ends after RefuseSeconds");
        var flaky = Mk("Flaky", 90);
        for (int i = 0; i < 3; i++) { Advance(120); Leave(flaky); Advance(10); Rejoin(flaky); }
        Ok(Total(flaky) == 0, "three reconnects in 8 min (a flaky line) is fine");
    }

    // ---------------------------------------------------------------- Names
    static void Impersonation()
    {
        Reset();
        var staff = MkAdmin("Aldric Varrow");
        var groupStaff = new Player(NextId++, "Maren Ashgrove");
        P.permission.Groups.Add(groupStaff.Id + "|admin");
        Server.ClientPlayers.Add(groupStaff);
        Inv(P, "OnPlayerConnected", groupStaff);
        Ok(((IDictionary)F(Data(), "Staff")).Count == 2, "staff are learned from the admin permission and the admin group");

        var leet = Mk("4ldr1c_V4rr0w");
        Ok(Count(leet, "impersonation") == 1, "leet spelling of a staff name is impersonation", Kinds(leet));
        Ok(leet.All().Contains("looks like a staff member"), "the impersonator is asked to rename");
        var cyr = Mk("Аldrіc Vаrrоw");
        Ok(Count(cyr, "impersonation") == 1, "Cyrillic look-alike letters are folded", Kinds(cyr));
        var hidden = Mk("[E86A5C]Ald​ric.Var‍row");
        Ok(Count(hidden, "impersonation") == 1, "colour tags and invisible characters are stripped", Kinds(hidden));
        var near = Mk("Aldrik Varrow");
        Ok(Count(near, "impersonation_near") == 1 && Count(near, "impersonation") == 0, "one letter off is a near match (low weight)", Kinds(near));
        Ok(AlertsFor(near) == 1, "a near match always alerts the admins, whatever the score");
        var grp = Mk("Maren  Ashgr0ve");
        Ok(Count(grp, "impersonation") == 1, "a group-staff name is protected too");
        var honest = Mk("Bramble of Dunmere");
        Ok(Total(honest) == 0, "an unrelated name is fine");
        var shortName = Mk("Al");
        Ok(Total(shortName) == 0, "very short names are not compared");
        Leave(staff);
        Rejoin(staff);
        Ok(!Players().Contains(staff.Id.ToString()) || Total(staff) == 0, "staff never match themselves");

        SetCfg("Names", "StaffNames", new List<string> { "Steward Elric" });
        var elric = Mk("steward_elric");
        Ok(Count(elric, "impersonation") == 1, "listed staff names are protected (Names.StaffNames)");

        Enforce();
        SetCfg("Names", "KickExactMatch", true);
        var kickme = Mk("ALDRIC VARROW");
        P.timer.RunPending();
        Ok(Server.Kicks.Any(k => k.StartsWith("ALDRIC VARROW|")), "KickExactMatch + enforce: an exact impersonator is kicked");
    }

    // ---------------------------------------------------------------- Score and responses
    static void ScoreAndResponses()
    {
        Reset();
        var p = Mk("Decay", 90);
        Tick();
        Step(p, 1, 2, 0, 0);
        Step(p, 1, 200, 0, 0);
        double s0 = Score(p);
        Advance(20 * 60);
        double s1 = Score(p);
        Ok(s0 > 0 && Math.Abs(s1 - s0 / 2) < 0.2, "the score halves every 20 min", s0 + " -> " + s1);
        Advance(4 * 3600);
        Ok(Score(p) == 0, "and fades away");
        Leave(p);
        Rejoin(p);
        Ok(Players().Contains(p.Id.ToString()), "a reconnect keeps the record (and the score)");

        // Watch mode never freezes or kicks.
        var w = Mk("Watched", 90);
        Tick();
        for (int i = 0; i < 6; i++) { Step(w, 1, 2, 0, 0); Step(w, 1, 300, 0, 0); Advance(20); }
        Ok(Score(w) >= 80 && !Frozen(w) && Server.Kicks.Count == 0, "watch mode: a high score freezes and kicks nobody");
        Ok(Alerts("would kick") >= 1, "watch mode alerts 'would kick'");
        var wAlerts = L("Alerts").Cast<object>().Where(a => (string)F(a, "PlayerId") == w.Id.ToString()).Select(a => (string)F(a, "Response")).ToList();
        var order = new List<string> { "alert", "would freeze", "would kick", "ban recommended" };
        bool neverDown = true;
        for (int i = 1; i < wAlerts.Count; i++) if (order.IndexOf(wAlerts[i]) <= order.IndexOf(wAlerts[i - 1])) neverDown = false;
        Ok(neverDown && wAlerts.Count <= 4, "within the cooldown, alerts only step up (no 'would freeze' after 'would kick')", string.Join(" > ", wAlerts));
        Ok(Alerts("ban recommended") == 0 || Score(w) >= 150, "no ban recommendation below the ban score");

        // Enforce: alert, then freeze, then kick.
        Reset();
        Enforce();
        var admin = MkAdmin("Lord Warden");
        var c = Mk("Cheater", 90);
        Tick();
        Step(c, 1, 2, 0, 0);
        Step(c, 1, 60, 0, 0);    // a short blink: 25-ish points
        Ok(Score(c) >= 20 && Score(c) < 50 && !Frozen(c), "alert score reached, not yet frozen", "score " + Score(c));
        Ok(Alerts("alert") == 1, "one alert");
        Ok(admin.All().Contains("Sentinel #"), "online admins see the alert in chat");
        Advance(20);
        Step(c, 1, 2, 0, 0);
        Step(c, 1, 52, 0, 0);    // another blink: ~30 points, total between the freeze and kick scores
        Ok(Frozen(c) && Server.Kicks.Count == 0, "freeze at the freeze score (enforce)", "score " + Score(c));
        Ok(c.All().Contains("held by the Sentinel"), "the frozen player is told why and for how long");
        var dmg = new CubeDamageEvent { Damage = new Damage { Amount = 50, DamageSource = c.Entity }, Sender = c };
        Inv(P, "OnCubeTakeDamage", dmg);
        var place = new CubePlaceEvent { Sender = c };
        Inv(P, "OnCubePlacement", place);
        var throne = new AncientThroneCaptureEvent { Player = c };
        var tr = Inv(P, "OnThroneCapture", throne);
        var rope = new PlayerCaptureEvent { Captor = c.Entity, Target = admin };
        var rr = Inv(P, "OnPlayerCapture", rope);
        Ok(dmg.Damage.Amount == 0f && dmg.Cancelled && place.Cancelled && throne.Cancelled && tr != null && rope.Cancelled && rr != null,
            "a frozen player cannot break or place blocks, take the throne or bind anyone");
        Ok(Say(c, "I was lagging") == null, "a frozen player can still talk");
        Leave(c);
        c.Messages.Clear();
        Rejoin(c);
        Ok(Frozen(c) && c.All().Contains("held by the Sentinel"), "a freeze survives a reconnect");
        Advance(20);
        Step(c, 1, 0, 0, 0);
        Inv(P, "Unfreeze", Rec(c));
        Tick();
        Step(c, 1, 2, 0, 0);
        Step(c, 1, 400, 0, 0);
        P.timer.RunPending();
        Ok(Server.Kicks.Any(k => k.StartsWith("Cheater|")), "kick at the kick score (enforce)", "score " + Score(c) + " kicks " + string.Join(";", Server.Kicks));
        Ok(Frozen(c) && AlertsWith(c, "kicked, frozen 15 min on return") == 1, "a kick also leaves a saved freeze, so rejoining is not a reset");
        Ok(Server.Bans.Count == 0, "no automatic ban by default");

        // Freeze expiry.
        var tf = Mk("Thaw", 90);
        Cmd(admin, "freeze Thaw 1");
        tf.Messages.Clear();
        Advance(61);
        House();
        Ok(!Frozen(tf) && tf.All().Contains("released"), "a freeze ends by itself and the player is told");

        // Ban recommended at the ban score; automatic only when opted in.
        var b = Mk("Recidivist", 90);
        Tick();
        for (int i = 0; i < 8; i++) { Step(b, 1, 2, 0, 0); Step(b, 1, 900, 0, 0); Advance(16); Inv(P, "Unfreeze", Rec(b)); }
        int recommended = L("Alerts").Cast<object>().Count(a => (string)F(a, "PlayerId") == b.Id.ToString() && ((string)F(a, "Response")).Contains("ban recommended"));
        Ok(Score(b) >= 150 && recommended == 1 && Server.Bans.Count == 0, "ban recommended once, never banned without an admin",
            "score " + Score(b) + " alerts " + string.Join("; ", L("Alerts").Cast<object>().Select(a => F(a, "PlayerName") + ":" + F(a, "Response"))) + " bans " + string.Join(";", Server.Bans));
        SetCfg("Responses", "AutoBan", true);
        var b2 = Mk("Recidivist Two", 90);
        Tick();
        for (int i = 0; i < 8; i++) { Step(b2, 1, 2, 0, 0); Step(b2, 1, 900, 0, 0); Advance(16); Inv(P, "Unfreeze", Rec(b2)); }
        P.timer.RunPending();
        Ok(Server.Kicks.Any(k => k.StartsWith("Recidivist Two|")), "the automatically banned player is removed");
        Ok(Server.Bans.Count == 1 && Server.Bans[0].StartsWith(b2.Id + "|"), "AutoBan + enforce: banned once at the ban score",
            Kinds(b2) + " score " + Score(b2) + " log " + string.Join("; ", P.Log.Where(l => l.Contains("ERROR") || l.Contains("Two"))));
    }

    // ---------------------------------------------------------------- Commands
    static void Commands()
    {
        Reset();
        var admin = MkAdmin("High Steward");
        var pl = Mk("Commoner", 90);
        var sus = Mk("Suspect", 90);
        Tick();
        Step(sus, 1, 2, 0, 0);
        Step(sus, 1, 120, 0, 0);
        Clear();
        Cmd(pl, "status");
        Ok(pl.All().Contains("/warden report") && !pl.All().Contains("Suspect"), "players get one pointer line and no data");
        Cmd(pl, "clear Suspect");
        Ok(Score(sus) > 0, "players cannot clear anyone");

        Clear();
        Cmd(admin, "");
        Ok(admin.All().Contains("/sentinel status") && admin.All().Contains("Mode: watch"), "help lists the commands and the mode");
        Clear();
        Cmd(admin, "status");
        Ok(admin.All().Contains("Top suspects: Suspect") && admin.All().Contains("Watch mode"), "status shows the top suspects and the watch-mode note", admin.All());
        Clear();
        Cmd(admin, "report Susp");
        Ok(admin.All().Contains("teleport x1") && admin.All().Contains("Evidence page 1/1") && admin.All().Contains("#1"), "report shows counts and evidence", admin.All());
        Clear();
        Cmd(admin, "report Nobody");
        Ok(admin.All().Contains("No one by that name"), "report of an unknown player says so");
        Clear();
        Cmd(admin, "report " + sus.Id);
        Ok(admin.All().Contains("Suspect"), "report by Steam ID");

        int evBefore = L("Evidence").Count;
        Clear();
        Cmd(admin, "clear Suspect");
        Ok(Score(sus) == 0 && Total(sus) == 0 && admin.All().Contains("Cleared Suspect"), "clear resets the score and counts");
        Ok(L("Evidence").Count == evBefore + 1 && Evidence("admin_action") == 1, "clear keeps the evidence and logs the admin action");

        Clear();
        Cmd(admin, "freeze Suspect 3");
        Ok(Frozen(sus) && admin.All().Contains("frozen for 3 min"), "admins can freeze in watch mode too");
        var held = Hit(sus, Creature(sus, 2), 10, DamageType.Melee, sus);
        Ok(held.Cancelled && held.Damage.Amount == 0f, "an admin freeze blocks the player's actions in watch mode too");
        Clear();
        Cmd(admin, "unfreeze Suspect");
        Ok(!Frozen(sus) && sus.All().Contains("released"), "unfreeze releases and tells the player");
        Clear();
        Cmd(admin, "unfreeze Suspect");
        Ok(admin.All().Contains("is not frozen"), "unfreeze of a free player says so");
        Clear();
        Cmd(admin, "freeze Suspect 99999");
        Ok(!Frozen(sus) && admin.All().Contains("not a whole number"), "freeze minutes are bounded");

        Clear();
        Cmd(admin, "ban Suspect");
        Ok(Server.Bans.Count == 0 && admin.All().Contains("confirm"), "ban needs confirm");
        Server.RefuseBans = true;
        Clear();
        Cmd(admin, "ban Suspect confirm");
        Ok(Server.Bans.Count == 0 && admin.All().Contains("did not accept"), "a refused ban is reported");
        Server.RefuseBans = false;
        Clear();
        Cmd(admin, "ban Suspect confirm");
        P.timer.RunPending();
        Ok(Server.Bans.Count == 1 && Server.Kicks.Any(k => k.StartsWith("Suspect|")) && admin.All().Contains("is banned"), "ban with confirm bans and removes the player");

        Clear();
        Cmd(admin, "peaks");
        Ok(admin.All().Contains("speed_mps"), "peaks shows the highest honest values", admin.All());
        Cmd(admin, "peaks reset");
        Clear();
        Cmd(admin, "peaks");
        Ok(admin.All().Contains("No values recorded"), "peaks reset clears them");

        // reload: re-reads the config file; a broken file keeps the old settings.
        P.Config.Json = "{\"Responses\":{\"Mode\":\"enforce\"}}";
        Clear();
        Cmd(admin, "reload");
        Ok((string)F(Cfg("Responses"), "Mode") == "enforce" && admin.All().Contains("Mode: enforce"), "reload re-reads the config");
        P.Config.Json = "{ broken";
        Clear();
        Cmd(admin, "reload");
        Ok((string)F(Cfg("Responses"), "Mode") == "enforce" && admin.All().Contains("could not be read"), "a broken config is refused and the old one kept");

        // A player frozen while offline is held where they are first seen.
        var away = Mk("Absent", 90);
        Leave(away);
        Clear();
        Cmd(admin, "freeze Absent 10");
        Ok(Frozen(away) && admin.All().Contains("Absent is frozen"), "an offline player can be frozen");
        CharacterTeleport.Calls = 0;
        At(away, 3000, 70, 3000);
        Rejoin(away);
        Ok(away.All().Contains("held by the Sentinel"), "they are told on join");
        Tick();
        Step(away, 1, 15, 0, 0);
        Ok(CharacterTeleport.Calls == 1 && Math.Abs(away.Entity.Position.x - 3000) < 0.01, "and held where they were first seen");

        // Ambiguous partial names.
        Mk("Rowan", 90); Mk("Rowena", 90);
        Clear();
        Cmd(admin, "report Row");
        Ok(admin.All().Contains("More than one player"), "an ambiguous partial name is refused");
    }

    // ---------------------------------------------------------------- Feed, logs, Warden
    static void FeedLogsAndWarden()
    {
        Reset();
        var admin = MkAdmin("Seneschal");
        var c = Mk("Blinky [FF0000]\nRed", 90);
        Tick();
        Step(c, 1, 2, 0, 0);
        Step(c, 1, 300, 0, 0);
        House();
        string feedPath = Path.Combine(Dir, "RealmSentinelFeed.json");
        Ok(File.Exists(feedPath), "the Steward feed is written (oxide/data/RealmSentinelFeed.json)");
        using (var doc = JsonDocument.Parse(File.ReadAllText(feedPath)))
        {
            var root = doc.RootElement;
            Ok(root.GetProperty("Version").GetInt32() == 1 && root.GetProperty("Mode").GetString() == "watch", "feed: version and mode");
            var alerts = root.GetProperty("Alerts");
            Ok(alerts.GetArrayLength() == 1 && alerts[0].GetProperty("Kind").GetString() == "teleport"
                && alerts[0].GetProperty("Time").GetString().EndsWith("Z"), "feed: the alert with kind and UTC time");
            var name = alerts[0].GetProperty("PlayerName").GetString();
            Ok(!name.Contains("[FF0000]") && !name.Contains("\n"), "feed: names are cleaned of colour tags and line breaks", name);
            var sus = root.GetProperty("Suspects");
            Ok(sus.GetArrayLength() == 1 && sus[0].GetProperty("Counts").GetProperty("teleport").GetInt32() == 1 && sus[0].GetProperty("Online").GetBoolean(),
                "feed: suspects with counts and online state");
        }
        Ok(P.FileLog.Any(l => l.StartsWith("alerts: ") && l.Contains("teleport")), "alerts go to oxide/logs/RealmSentinel (LogToFile)");
        Ok(P.FileLog.Any(l => l.StartsWith("evidence: ") && l.Contains("ping")), "evidence lines go to the log with the ping");
        Ok(WardenCalls.Count == 1 && WardenCalls[0].Length == 3 && (string)WardenCalls[0][0] == "sentinel teleport" && (ulong)WardenCalls[0][1] == c.Id,
            "alerts are forwarded to RealmWarden's queue (RaiseWardenAlert)");

        // Admin chat is rate-limited.
        SetCfg("Score", "KindCooldownSeconds", 0);
        SetCfg("Responses", "AlertCooldownMinutes", 0);
        admin.Messages.Clear();
        for (int i = 0; i < 10; i++) { var x = Mk("Burst" + i, 90); Tick(); Step(x, 1, 2, 0, 0); Step(x, 1, 300, 0, 0); }
        int shown = admin.Messages.Count(m => m.Contains("Sentinel #"));
        Ok(shown == 5, "at most 6 alerts a minute reach admin chat", "shown " + shown);
        Advance(61);
        var y = Mk("Afterwards", 90);
        Tick(); Step(y, 1, 2, 0, 0); Step(y, 1, 300, 0, 0);
        Ok(admin.All().Contains("more Sentinel alerts were queued"), "the overflow is summarised");

        // Unload writes the feed and data.
        Inv(P, "Unload");
        Ok(File.Exists(Path.Combine(Dir, "RealmSentinel.json")), "Unload saves the data");
        Ok(EventManager.Subscribers.Count == 0, "Unload unsubscribes from the game's events");
    }

    // ---------------------------------------------------------------- Persistence
    static void Persistence()
    {
        Reset();
        var staff = MkAdmin("Ser Odo");
        var c = Mk("Persistent", 90);
        Tick();
        Step(c, 1, 2, 0, 0);
        Step(c, 1, 300, 0, 0);
        Cmd(staff, "freeze Persistent 30");
        double s = Score(c);
        Inv(P, "SaveData");
        var ids = c.Id;
        P = NewPlugin(P.Config.Json);
        SetTime(new DateTime(2026, 10, 7, 12, 0, 1, DateTimeKind.Utc));
        Ok(Math.Abs(Score(c) - s) < 0.5 && Frozen(c), "score and freeze survive a reload");
        Ok(((IDictionary)F(Data(), "Staff")).Contains(staff.Id.ToString()), "learned staff survive a reload");
        Ok(L("Evidence").Count >= 2, "evidence survives a reload");

        File.WriteAllText(Path.Combine(Dir, "RealmSentinel.json"), "{ this is not json");
        Server.ClientPlayers.Clear();
        P = NewPlugin(P.Config.Json);
        Ok((bool)F(P, "loadFailed"), "a damaged data file is detected");
        Inv(P, "SaveData");
        Ok(File.ReadAllText(Path.Combine(Dir, "RealmSentinel.json")) == "{ this is not json", "and never overwritten");
        var adm = MkAdmin("Fixer");
        Ok(adm.All().Contains("records are damaged"), "admins are told on join");
        var x = Mk("StillWatched", 90);
        Tick(); Step(x, 1, 2, 0, 0); Step(x, 1, 300, 0, 0);
        Ok(Count(x, "teleport") == 1, "detection keeps working from memory");
        File.WriteAllText(Path.Combine(Dir, "RealmSentinel.json"), "null");
        P = NewPlugin(P.Config.Json);
        Ok((bool)F(P, "loadFailed"), "a 'null' data file is treated as damaged too");
    }

    // ---------------------------------------------------------------- API
    static void Api()
    {
        Reset();
        var p = Mk("Exile", 90);
        Tick();
        Inv(P, "SentinelGrace", p.Id, 5f);
        Shift(p, 2000, 0, 0);
        Step(p, 1, 0, 0, 0);
        Ok(Total(p) == 0, "SentinelGrace lets a plugin move a player");
        Inv(P, "SentinelGrace", p.Id, 99999f);
        double until = (double)F(((IDictionary)F(P, "moves"))[p.Id], "GraceUntil");
        double now = (double)Inv(P, "NowSec");
        Ok(until - now <= 60.01, "SentinelGrace is capped at 60 s");
        Ok((double)Inv(P, "GetSentinelScore", 12345UL) == 0 && !(bool)Inv(P, "IsSentinelFrozen", 12345UL), "unknown players: score 0, not frozen");
    }

    // ---------------------------------------------------------------- Honest players, an hour each
    // The server sees positions late and in bursts. Each sample interval is 0.75-1.25 s (as the game's own check).
    class Sim
    {
        public Player P; public double X, Y, Z; public double LagLeft;
        public Sim(Player p) { P = p; var v = p.Entity.Position; X = v.x; Y = v.y; Z = v.z; }
    }

    static void Drive(Sim s, double seconds, Func<double, double[]> vel, double lagEvery, double lagMin, double lagMax)
    {
        double t = 0, nextLag = lagEvery > 0 ? lagEvery * (0.5 + Rng.NextDouble()) : double.MaxValue;
        while (t < seconds)
        {
            double dt = 0.75 + Rng.NextDouble() * 0.5;
            for (int k = 0; k < 10; k++)
            {
                var v = vel(t + dt * k / 10.0);
                s.X += v[0] * dt / 10; s.Y += v[1] * dt / 10; s.Z += v[2] * dt / 10;
            }
            t += dt;
            if (t >= nextLag) { s.LagLeft = lagMin + Rng.NextDouble() * (lagMax - lagMin); nextLag = t + lagEvery * (0.5 + Rng.NextDouble()); }
            if (s.LagLeft > 0) s.LagLeft -= dt;                                  // the server hears nothing
            else s.P.Entity.Position = new Vector3((float)s.X, (float)s.Y, (float)s.Z);
            Advance(dt);
            Tick();
        }
    }

    static double[] V(double x, double y, double z) { return new[] { x, y, z }; }

    static void HonestPlayers()
    {
        Reset();
        var hp = Mk("Faraway Fen", 300);
        var worse = Mk("Satellite Sam", 600);
        var normal = Mk("Local Lia", 40);
        var sims = new[] { new Sim(hp), new Sim(worse), new Sim(normal) };
        Tick();
        // An hour of hard play: sprinting at 9.5 m/s with turns, jumps, a cliff fall every few minutes, a 24 m staircase,
        // and lag spikes (no updates) of 1.5-4 s (5 s for the 600 ms player) every ~40 s.
        for (int minute = 0; minute < 60; minute++)
        {
            for (int i = 0; i < sims.Length; i++)
            {
                var s = sims[i];
                double ang = Rng.NextDouble() * Math.PI * 2;
                double lagMax = i == 1 ? 5.0 : 4.0;
                Drive(s, 40, t => V(9.5 * Math.Cos(ang + t / 10), (t % 3) < 0.4 ? 3 : (t % 3) < 0.8 ? -3 : 0, 9.5 * Math.Sin(ang + t / 10)), 40, 1.5, lagMax);
                if (minute % 5 == 2) Drive(s, 2, t => V(8, -16, 0), 0, 0, 0);          // off a cliff
                if (minute % 7 == 3) Drive(s, 8, t => V(3, 3, 0), 0, 0, 0);            // up a tall staircase
                Drive(s, 18 - (minute % 5 == 2 ? 2 : 0), t => V(4, 0, 2), 40, 1.5, lagMax);
            }
        }
        var peaks = (IDictionary)F(Data(), "Peaks");
        double peakSpeed = (double)F(peaks["speed_mps"], "Value");
        Ok(peakSpeed > 25, "the replay really had lag catch-up bursts (one sample moved " + peakSpeed.ToString("0") + " m/s) and allowed them");
        foreach (var s in sims) Ok(Total(s.P) == 0 && Score(s.P) == 0, "honest mover " + s.P.Name + " (ping " + s.P.Connection.AveragePing + " ms, lag spikes) never flagged in an hour", Kinds(s.P));

        // Fights with high ping: melee at the edge of reach, bunched hits, bows at range.
        var duelA = Mk("Duelist", 450);
        var duelB = Mk("Opponent", 450);
        for (int i = 0; i < 600; i++)
        {
            PlaceAt(duelB, duelA, (float)(3 + Rng.NextDouble() * 10));
            Hit(duelA, duelB.Entity, 18, DamageType.Slash, duelB);                     // victim-reported, as in the game
            Hit(duelA, Creature(duelA, (float)(2 + Rng.NextDouble() * 9)), 22, DamageType.Melee, duelA);
            if (i % 3 == 0) { PlaceAt(duelB, duelA, (float)(20 + Rng.NextDouble() * 150)); Hit(duelA, duelB.Entity, 45, DamageType.Projectile, duelA); }
            Advance(i % 4 == 0 ? 0.05 : 0.6);
        }
        Ok(Total(duelA) == 0 && Total(duelB) == 0, "ten minutes of high-ping fighting are never flagged", Kinds(duelA) + " / " + Kinds(duelB));

        // A heavy gatherer, a looter, a trader and a crafter for an hour.
        var gather = Mk("Woodcutter", 250);
        ItemTick();
        var store = Chest("Stone", 30000);
        for (int i = 0; i < 360; i++)
        {
            Advance(10);
            gather.Inv.Add(i % 2 == 0 ? "Wood" : "Stone", 90);                        // ~540 a minute
            if (i % 60 == 30) { Open(gather, store); }
            if (i % 60 == 31) { Box(store).Contents.Add("Stone", -2500); gather.Inv.Add("Stone", 2500); }
            if (i % 60 == 40) { gather.Inv.Add("Stone", -3000); Box(store).Contents.Add("Stone", 3000); }
            if (i % 90 == 50) { GameCmd(gather, "contract", "collect"); }
            if (i % 90 == 51) { gather.Inv.Add("Iron", 700); }
            if (i % 30 == 10) { Inv(P, "OnItemCrafted", new ItemCrafterFinishEvent { Entity = gather.Entity, Crafter = new ItemCrafter { Product = new InvItemBlueprint("Plank") }, Sender = gather }); }
            if (i % 30 == 11) { gather.Inv.Add("Wood", -40); gather.Inv.Add("Plank", 20); }
            ItemTick();
        }
        Ok(Total(gather) == 0, "an hour of heavy gathering, looting, trading and crafting is never flagged", Kinds(gather));

        Ok(L("Alerts").Count == 0, "no alert was raised for any honest player");
    }
}
