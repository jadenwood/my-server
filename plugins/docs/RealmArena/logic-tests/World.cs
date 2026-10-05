// Shared test world for RealmArena: players with bodies and health, the REAL RealmTreasury holding the stakes, stand-ins
// for RealmRenown, RealmChronicle, RealmHouses, RealmSeasons, RealmHerald, RealmWarden, RealmEvents, RealmLaws,
// RealmLegendary and RealmSentinel, a clock, and blows applied the way the game applies them when nothing turns them
// aside. Used by the logic tests (plugins/docs/RealmArena/logic-tests) and the exploit suite (tools/exploit-review/arena).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Behaviours;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;

static class W
{
    public static int pass, fail;
    public const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static DateTime Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);   // a Monday
    public static RealmArena A;
    public static RealmTreasury T;
    public static Plugin Renown, Chron, Houses, Seasons, Herald, Warden, Events, Laws, Legendary, Sentinel;
    public static Dictionary<string, string> HouseOf = new Dictionary<string, string>();
    public static Dictionary<string, string> Liege = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> Treaties = new HashSet<string>();
    public static HashSet<string> Protected = new HashSet<string>();
    public static HashSet<string> Frozen = new HashSet<string>();
    public static HashSet<string> PopupsOff = new HashSet<string>();
    public static string Bearer;
    public static bool Truce;
    public static List<string> Deeds = new List<string>();
    public static List<string> ChronLog = new List<string>();
    public static List<string> Awards = new List<string>();
    public static List<string> Alerts = new List<string>();
    public static List<string> TrialResults = new List<string>();
    public static List<string> RoyalScores = new List<string>();
    public static List<string> Graces = new List<string>();
    public static string[] RoyalActive;
    public static string[] RoyalEntrants = new string[0];
    public static long Seeded;                                     // marks given to players by the tests (minted in the treasury)
    public static string Dir;
    public static List<Player> Everyone = new List<Player>();

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
    public static object Data() { return F(A, "data"); }
    public static object D(string field) { return F(Data(), field); }
    public static IList L(string field) { return (IList)D(field); }
    public static object Cfg(string section) { return F(F(A, "config"), section); }
    public static void Set(string section, string field, object v) { SetF(Cfg(section), field, v); }
    public static string B() { return string.Join("\n", Server.Broadcasts); }
    public static void Clear()
    {
        Server.Broadcasts.Clear(); Deeds.Clear(); ChronLog.Clear(); Awards.Clear(); Alerts.Clear(); TrialResults.Clear(); RoyalScores.Clear(); Graces.Clear();
        CharacterTeleport.Moves.Clear();
        foreach (var p in Everyone) { p.Messages.Clear(); p.Popups.Clear(); p.Heals.Clear(); }
    }

    public static void Reset()
    {
        Server.ClientPlayers.Clear(); Server.Broadcasts.Clear(); Everyone.Clear();
        HouseOf.Clear(); Liege.Clear(); Treaties.Clear(); Protected.Clear(); Frozen.Clear(); PopupsOff.Clear();
        Bearer = null; Truce = false; RoyalActive = null; RoyalEntrants = new string[0]; Seeded = 0;
        PlayerExtensions.PopupsFail = false;
        Clear();
        Clock = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        if (Dir == null) { Dir = Path.Combine(Path.GetTempPath(), "realmarena-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Dir); }
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Renown = new Plugin { Name = "RealmRenown", Handler = (h, a) => { if (h == "AddDeed") { Deeds.Add(string.Join("|", a.Select(x => Convert.ToString(x)))); return true; } return null; } };
        Chron = new Plugin { Name = "RealmChronicle", Handler = (h, a) => { if (h != "Log") return null; ChronLog.Add(a[0] + "|" + a[1] + "|" + a[2]); return ChronLog.Count; } };
        Houses = new Plugin { Name = "RealmHouses", Handler = (h, a) =>
        {
            if (h == "GetHouse") { string v; return HouseOf.TryGetValue((string)a[0], out v) ? v : null; }
            if (h == "GetLiege") { string v; return Liege.TryGetValue((string)a[0], out v) ? v : null; }
            if (h == "HasTreaty") return Treaties.Contains(Pair((string)a[0], (string)a[1]));
            return null;
        } };
        Seasons = new Plugin { Name = "RealmSeasons", Handler = (h, a) => { if (h == "AwardHouse") { Awards.Add(a[0] + "|" + a[1] + "|" + a[2]); return true; } return null; } };
        Herald = new Plugin { Name = "RealmHerald", Handler = (h, a) => h == "PopupsWanted" ? (object)!PopupsOff.Contains((string)a[0]) : null };
        Warden = new Plugin { Name = "RealmWarden", Handler = (h, a) =>
        {
            if (h == "IsNewPlayerProtected") return Protected.Contains(Convert.ToString(a[0]));
            if (h == "RaiseWardenAlert") { Alerts.Add(a[0] + "|" + a[1] + "|" + a[2]); return Alerts.Count; }
            return null;
        } };
        Events = new Plugin { Name = "RealmEvents", Handler = (h, a) =>
        {
            if (h == "IsTruceActive") return Truce;
            if (h == "GetActiveEvents") return RoyalActive ?? new string[0];
            if (h == "GetTournamentEntrants") return RoyalEntrants;
            if (h == "ScoreTournamentDuel") { RoyalScores.Add(a[0] + ">" + a[1]); return true; }
            return null;
        } };
        Laws = new Plugin { Name = "RealmLaws", Handler = (h, a) => { if (h == "ArenaTrialResult") { TrialResults.Add(a[0] + "|" + a[1]); return true; } return null; } };
        Legendary = new Plugin { Name = "RealmLegendary", Handler = (h, a) => h == "IsBearer" ? (object)(Bearer != null && (string)a[0] == Bearer) : null };
        Sentinel = new Plugin { Name = "RealmSentinel", Handler = (h, a) =>
        {
            if (h == "IsSentinelFrozen") return Frozen.Contains(Convert.ToString(a[0]));
            if (h == "SentinelGrace") { Graces.Add(Convert.ToString(a[0])); return null; }
            return null;
        } };
        T = NewTreasury();
        A = NewArena();
    }

    public static string Pair(string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0 ? (a + "|" + b).ToLowerInvariant() : (b + "|" + a).ToLowerInvariant(); }

    public static RealmTreasury NewTreasury()
    {
        var t = new RealmTreasury();
        t.Name = "RealmTreasury";
        Inv(t, "LoadDefaultConfig");
        Inv(t, "LoadDefaultMessages");
        SetF(t, "RealmChronicle", Chron); SetF(t, "RealmHouses", Houses);
        Inv(t, "Init");
        Inv(t, "OnServerInitialized");
        return t;
    }

    public static RealmArena NewArena(Action<object> tweak = null, bool treasury = true)
    {
        var p = new RealmArena();
        p.Name = "RealmArena";
        Inv(p, "LoadDefaultConfig");
        if (tweak != null)
        {
            var cfg = typeof(RealmArena).GetNestedType("PluginConfig", BF);
            var obj = System.Text.Json.JsonSerializer.Deserialize(p.Config.Json, cfg, DataFileSystem.Opts);
            tweak(obj);
            p.Config.WriteObject(obj, true);
        }
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmTreasury", treasury ? (Plugin)T : null);
        SetF(p, "RealmRenown", Renown); SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "RealmSeasons", Seasons);
        SetF(p, "RealmHerald", Herald); SetF(p, "RealmWarden", Warden); SetF(p, "RealmEvents", Events); SetF(p, "RealmLaws", Laws);
        SetF(p, "RealmLegendary", Legendary); SetF(p, "RealmSentinel", Sentinel);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        A = p;
        return p;
    }

    // A section of the config, changed in place (the plugin keeps the same object).
    public static void Tune(string section, string field, object v) { Set(section, field, v); }

    // A player, online, standing at (x, z), with `marks` in their purse and `minutes` already played on the server.
    public static Player Mk(ulong id, string name, float x = 0, float z = 0, long marks = 1000, double minutes = 600, string house = null)
    {
        var p = new Player(id, name);
        p.Entity.Position = new Vector3(x, 0, z);
        Server.ClientPlayers.Add(p);
        Everyone.Add(p);
        if (house != null) HouseOf[id.ToString()] = house;
        if (marks > 0) Give(p, marks);
        var f = Inv(A, "GetFighter", id.ToString(), name, true);
        SetF(f, "PlayedMinutes", minutes);
        return p;
    }

    // New marks for a player, minted in the treasury's own books so its audit stays balanced.
    public static void Give(Player p, long marks)
    {
        var purses = (IDictionary)F(F(T, "data"), "Purses");
        string k = p.Id.ToString();
        purses[k] = (purses.Contains(k) ? (long)purses[k] : 0) + marks;
        var td = F(T, "data");
        SetF(td, "MarksMinted", (long)F(td, "MarksMinted") + marks);
        Seeded += marks;
    }
    public static long Purse(Player p) { return (long)Inv(T, "GetPurse", p.Id.ToString()); }
    public static long Held() { var h = (IDictionary)F(F(T, "data"), "Holds"); long n = 0; foreach (var v in h.Values) n += (long)F(v, "Marks"); return n; }
    public static int Holds() { return ((IDictionary)F(F(T, "data"), "Holds")).Count; }
    public static List<string> Audit() { return (List<string>)Inv(T, "Audit"); }
    // Every mark the tests gave out is in a purse or in a hold, and the treasury's own audit agrees.
    public static string ZeroSum()
    {
        var bad = new List<string>(Audit());
        long purses = 0;
        foreach (var v in ((IDictionary)F(F(T, "data"), "Purses")).Values) purses += (long)v;
        if (purses + Held() != Seeded) bad.Add("purses " + purses + " + held " + Held() + " != given " + Seeded);
        return string.Join("; ", bad);
    }

    public static void Admin(Player p) { A.permission.Grants.Add(p.Id + "|realmarena.admin"); }
    public static void Offline(Player p) { Server.ClientPlayers.Remove(p); Inv(A, "OnPlayerDisconnected", p); }
    public static void Online(Player p) { if (!Server.ClientPlayers.Contains(p)) Server.ClientPlayers.Add(p); Inv(A, "OnPlayerConnected", p); }
    public static void Move(Player p, float x, float z) { p.Entity.Position = new Vector3(x, 0, z); }

    public static void Cmd(Player p, string command, params string[] args)
    {
        string m = command == "duel" ? "CmdDuel" : command == "arena" ? "CmdArena" : command == "dice" ? "CmdDice" : "CmdCards";
        Inv(A, m, p, command, args);
    }

    // Time passes: the ring checks run every second and the slow tick every TickSeconds.
    public static void Tick(int seconds = 1)
    {
        for (int i = 0; i < seconds; i++)
        {
            Clock = Clock.AddSeconds(1);
            Inv(A, "SafeFastTick");
            if (Clock.Second % 10 == 0) { Inv(A, "SafeSlowTick"); Inv(T, "Tick"); }
        }
    }
    public static void Slow() { Inv(A, "SafeSlowTick"); }
    public static void Advance(TimeSpan by) { DateTime until = Clock + by; while (Clock < until) Tick(10); }

    public static object Duel(Player p) { return Inv(A, "DuelOf", p.Id.ToString()); }
    public static string State(Player p) { var d = Duel(p); return d == null ? null : (string)F(d, "State"); }
    public static object Fighter(Player p) { return Inv(A, "GetFighter", p.Id.ToString(), null, false); }
    public static int Rating(Player p) { return (int)F(Fighter(p), "Rating"); }
    public static int FInt(Player p, string field) { return (int)F(Fighter(p), field); }
    public static int Pending() { return L("Settlements").Count; }

    // A blow as the game deals it: the plugin's hook first; if nothing turned it aside, the hit region loses the amount
    // and a killing blow is a death (OnEntityDeath). Returns "applied", "turned" or "killed".
    public static string Hit(Player attacker, Player victim, float amount, HumanBodyBones bone = HumanBodyBones.Chest)
    {
        var d = new Damage { Amount = amount, DamageSource = attacker != null ? attacker.Entity : null, HitBoxBone = bone };
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = d };
        Inv(A, "OnEntityHealthChange", evt);
        if (evt.Cancelled || d.Amount <= 0f) return "turned";
        if (victim.Health.Apply(d.Amount, bone))
        {
            Inv(A, "OnEntityDeath", new EntityDeathEvent { Entity = victim.Entity, KillingDamage = d });
            return "killed";
        }
        return "applied";
    }
    public static string Fall(Player victim, float amount) { return Hit(null, victim, amount, HumanBodyBones.LastBone); }

    // A challenge accepted at once (by chat, with the stake when there is one), both standing together.
    public static void Challenge(Player a, Player b, long wager = 0)
    {
        if (wager > 0) Cmd(a, "duel", b.Name, wager.ToString()); else Cmd(a, "duel", b.Name);
    }
    public static void Accept(Player b, Player a, long wager = 0)
    {
        if (wager > 0) Cmd(b, "duel", "accept", a.Name, wager.ToString()); else Cmd(b, "duel", "accept", a.Name);
    }
    public static void Fight(Player a, Player b, long wager = 0)
    {
        Challenge(a, b, wager);
        Accept(b, a, wager);
        Tick(6);
    }
    public static void Heal(Player p) { p.Health.HealBy(1000); }
}
