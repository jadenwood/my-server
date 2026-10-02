// Behaviour tests for plugins/RealmWarden.cs. Run with run.sh (see there for what this does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Damaging;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Networking.Events.Players;
using CodeHatch.Thrones.AncientThrone;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static RealmWarden P;
    static Plugin Crown;
    static bool Rebellion;
    static CrestScheme Crests = new CrestScheme();
    static string Dir;
    const string Admin = "realmwarden.admin";

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
    static int Flag(Player p, string kind) { var d = (IDictionary)F(Rec(p), "Flags"); return d.Contains(kind) ? (int)d[kind] : 0; }
    static int Alerts(string kind) { return L("Alerts").Cast<object>().Count(a => (string)F(a, "Kind") == kind); }
    static int Evidence(string kind) { return L("Evidence").Cast<object>().Count(a => (string)F(a, "Kind") == kind); }

    static void SetTime(DateTime utc)
    {
        double skew = (utc - Epoch).TotalSeconds - (DateTime.UtcNow - Epoch).TotalSeconds;
        SetF(P, "clockSkew", skew);
    }
    static void Advance(double seconds) { SetF(P, "clockSkew", (double)F(P, "clockSkew") + seconds); }

    static Player Mk(ulong id, string name, bool connect = true)
    {
        var p = new Player(id, name);
        Server.ClientPlayers.Add(p);
        if (connect) Inv(P, "OnPlayerConnected", p);
        return p;
    }
    static void Leave(Player p) { Inv(P, "OnPlayerDisconnected", p); Server.ClientPlayers.Remove(p); }
    static void Cmd(Player p, string line)
    {
        string[] args = line.Length == 0 ? new string[0] : line.Split(' ');
        Inv(P, "CmdWarden", p, "warden", args);
    }
    static void Clear() { foreach (var p in Server.ClientPlayers) p.Messages.Clear(); }
    static void Tick(int n) { for (int i = 0; i < n; i++) { Advance(15); Inv(P, "Tick"); } }

    static EntityDamageEvent Hit(Player attacker, Player victim, float amount = 10f)
    {
        var e = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = amount, DamageSource = attacker.Entity } };
        Inv(P, "OnEntityHealthChange", e);
        return e;
    }
    static object Say(Player p, string msg)
    {
        return Inv(P, "OnPlayerChat", new PlayerMessageEvent { Player = p, Message = msg, Sender = p });
    }
    static CubeDamageEvent Cube(Player attacker, float amount = 50f, DamageType type = DamageType.Melee)
    {
        var e = new CubeDamageEvent { Damage = new Damage { Amount = amount, DamageSource = attacker != null ? attacker.Entity : null, DamageTypes = type }, Sender = attacker };
        Inv(P, "OnCubeTakeDamage", e);
        return e;
    }

    static RealmWarden NewPlugin(string configJson)
    {
        var p = new RealmWarden();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "CrownAndConsequences", Crown);
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void Reset(string configJson = null)
    {
        foreach (var f in Directory.GetFiles(Dir)) File.Delete(f);
        Server.ClientPlayers.Clear();
        Server.Kicks.Clear();
        SocialAPI.Groups.Clear();
        Crests.GroupAtAnyPosition = 0;
        Rebellion = false;
        P = NewPlugin(configJson);
        SetTime(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));   // a Wednesday, 12:00 UTC
    }

    static void Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "wardentest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        SocialAPI.Registry[typeof(CrestScheme)] = Crests;
        Crown = new Plugin { Name = "CrownAndConsequences", Handler = (h, a) => h == "IsRebellionActive" ? (object)Rebellion : null };
        try { Run(); }
        catch (Exception ex) { fail++; Console.WriteLine("CRASH " + ex); }
        finally { try { Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static void Run()
    {
        // ---------------- Config ----------------
        Reset();
        Ok(P.Config.Json.Contains("\"PlaytimeMinutes\": 60") && P.Config.Json.Contains("\"Enabled\": false"), "default config written (raid hours opt-in)");
        P = NewPlugin("{\"Chat\":{\"MaxMessages\":0,\"MuteSeconds\":1},\"Names\":{\"Action\":\"ban\"},\"CombatLog\":null}");
        Ok((int)F(Cfg("Chat"), "MaxMessages") == 1 && (int)F(Cfg("Chat"), "MuteSeconds") == 5, "config values clamped");
        Ok((string)F(Cfg("Names"), "Action") == "flag", "unknown name action falls back to flag (no ban action exists)");
        Ok(Cfg("CombatLog") != null && (int)F(Cfg("CombatLog"), "WindowSeconds") == 30, "null config section restored with defaults");
        P = NewPlugin("{\"RaidHours\":{\"Windows\":[{\"Days\":[\"Funday\"],\"Start\":\"25:00\",\"End\":\"1\"},{\"Days\":[\"*\"],\"Start\":\"20:00\",\"End\":\"02:00\"}]}}");
        Ok(((IList)F(P, "raidWindows")).Count == 1, "bad raid window ignored, good one kept");

        // ---------------- New-player protection ----------------
        Reset();
        var vet = Mk(76561190000000001, "Veteran");
        SetF(Rec(vet), "Playtime", 999999.0);
        var newb = Mk(76561190000000002, "Newcomer");
        Ok(newb.All().Contains("new-player protection"), "new player greeted with protection notice");
        Leave(vet); vet.Messages.Clear(); Server.ClientPlayers.Add(vet); Inv(P, "OnPlayerConnected", vet);
        Ok(!vet.All().Contains("new-player protection for"), "known player not greeted again");
        var e1 = Hit(vet, newb);
        Ok(e1.Cancelled && e1.Damage.Amount == 0f, "damage to protected player is cancelled and zeroed");
        Ok(vet.All().Contains("cannot be harmed"), "attacker told the target is protected");
        Ok(!((IDictionary)F(P, "combat")).Contains(newb.Id), "blocked hit does not start combat");
        var e2 = Hit(newb, vet);
        Ok(!e2.Cancelled, "protected player hitting a veteran: damage goes through");
        Ok((bool)F(Rec(newb), "ProtectionEnded") && newb.All().Contains("has ended"), "attacking ends protection");
        Ok(Evidence("protection") == 1, "forfeit recorded as evidence");
        var e3 = Hit(vet, newb);
        Ok(!e3.Cancelled, "after forfeit the player can be hit");

        Reset();
        var a = Mk(76561190000000011, "Alpha");
        var b = Mk(76561190000000012, "Bravo");
        var e4 = Hit(a, b);
        Ok(e4.Cancelled, "two new players: victim protected");
        Ok(!(bool)F(Rec(a), "ProtectionEnded"), "hitting another protected player does not forfeit (it was blocked)");
        Tick(4 * 60);   // 60 minutes of play
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", a.Id), "protection runs out after PlaytimeMinutes of play");
        Ok(a.All().Contains("run out"), "player told protection ran out");
        int outs = a.Messages.Count(m => m.Contains("run out"));
        Tick(10);
        Ok(a.Messages.Count(m => m.Contains("run out")) == outs, "run-out notice sent once");

        Reset();
        var c = Mk(76561190000000021, "Charlie");
        Leave(c);
        Advance(49 * 3600);
        Server.ClientPlayers.Add(c);
        Inv(P, "OnPlayerConnected", c);
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", c.Id), "protection ends after MaxWallClockHours even with little play");

        Reset();
        var d = Mk(76561190000000031, "Delta");
        Advance(30); Inv(P, "Tick");
        Advance(100000); Inv(P, "Tick");
        double pt = (double)F(Rec(d), "Playtime");
        Ok(pt < 100, "a stalled timer cannot grant hours of playtime at once", "playtime " + pt);

        Reset();
        var o = Mk(76561190000000041, "Opter");
        Clear();
        Cmd(o, "protection off");
        Ok(o.All().Contains("confirm") && (bool)Inv(P, "IsNewPlayerProtected", o.Id), "opt-out needs confirm");
        Cmd(o, "protection off confirm");
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", o.Id), "opt-out with confirm ends protection");
        Ok(Evidence("protection") == 0, "voluntary opt-out is not evidence");

        Reset();
        var adm = Mk(76561190000000051, "Warden");
        P.permission.Grants.Add(adm.Id + "|" + Admin);
        var x = Mk(76561190000000052, "Xray");
        SetF(Rec(x), "Playtime", 999999.0);
        Cmd(adm, "protect Xray 30");
        Ok((bool)Inv(P, "IsNewPlayerProtected", x.Id), "admin can grant protection");
        Cmd(adm, "protect Xray off");
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", x.Id), "admin can remove protection");
        Ok(Evidence("admin_action") == 2, "admin actions logged");
        Clear();
        Cmd(x, "protect Warden 30");
        Ok(x.All().Contains("may not"), "non-admin cannot use admin commands");

        Reset();
        var cap1 = Mk(76561190000000061, "Captor");
        SetF(Rec(cap1), "Playtime", 999999.0);
        var cap2 = Mk(76561190000000062, "Target");
        var ce = new PlayerCaptureEvent { Captor = cap1.Entity, Target = cap2 };
        object cr = Inv(P, "OnPlayerCapture", ce);
        Ok(ce.Cancelled && cr is bool && (bool)cr, "binding a protected player is cancelled");
        var ce2 = new PlayerCaptureEvent { Captor = cap2.Entity, Target = cap1 };
        Inv(P, "OnPlayerCapture", ce2);
        Ok(!ce2.Cancelled && (bool)F(Rec(cap2), "ProtectionEnded"), "protected player who binds someone loses protection");

        Reset();
        var k = Mk(76561190000000071, "Kingly");
        Inv(P, "OnThroneCaptured", new AncientThroneCaptureEvent { Player = k, State = AncientThroneCaptureEvent.States.Capturing });
        Ok((bool)Inv(P, "IsNewPlayerProtected", k.Id), "throne capture in progress keeps protection");
        Inv(P, "OnThroneCaptured", new AncientThroneCaptureEvent { Player = k, State = AncientThroneCaptureEvent.States.Completed });
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", k.Id), "taking the throne ends protection");

        Reset();
        SetCfg("NewPlayerProtection", "SuspendDuringRebellion", true);
        var r1 = Mk(76561190000000081, "Rebel");
        SetF(Rec(r1), "Playtime", 999999.0);
        var r2 = Mk(76561190000000082, "Fresh");
        Rebellion = true;
        Ok(!Hit(r1, r2).Cancelled, "SuspendDuringRebellion: protection off while a rebellion is open");
        Rebellion = false;
        Ok(Hit(r1, r2).Cancelled, "protection back after the rebellion window");

        // ---------------- Combat log ----------------
        Reset();
        var f1 = Mk(76561190000000101, "Fighter");
        var f2 = Mk(76561190000000102, "Runner");
        SetF(Rec(f1), "Playtime", 999999.0);
        SetF(Rec(f2), "Playtime", 999999.0);
        Clear();
        Hit(f1, f2);
        Ok(f2.All().Contains("in combat") && f2.All().Contains("body stays"), "victim warned on entering combat (with sleeper note)");
        Ok((bool)Inv(P, "IsInCombat", f1.Id), "attacker tagged in combat too");
        Advance(10);
        Leave(f2);
        Ok(Flag(f2, "combat_log") == 1, "leaving 10 s after a hit is flagged");
        Ok(Alerts("combat_log") == 1 && Evidence("combat_log") == 1, "combat log queued as alert and evidence");
        Ok(f1.All().Contains("left the game in the middle"), "opponent told");
        var ev = L("Evidence").Cast<object>().Last(q => (string)F(q, "Kind") == "combat_log");
        Ok(((string)F(ev, "Detail")).Contains("sleeper") && (string)F(ev, "OtherName") == "Fighter", "evidence names opponent and sleeper note");
        Ok(P.FileLog.Any(l => l.StartsWith("evidence:") && l.Contains("combat_log")), "evidence written to the log file");

        Server.ClientPlayers.Add(f2); Inv(P, "OnPlayerConnected", f2);
        Hit(f1, f2);
        Advance(5);
        Leave(f2);
        Ok(Flag(f2, "combat_log") == 2 && Alerts("combat_log") == 1, "second combat log flagged but alert held by cooldown");

        Server.ClientPlayers.Add(f2); Inv(P, "OnPlayerConnected", f2);
        Hit(f1, f2);
        Advance(31);
        Leave(f2);
        Ok(Flag(f2, "combat_log") == 2, "leaving after the window is not flagged");

        Server.ClientPlayers.Add(f2); Inv(P, "OnPlayerConnected", f2);
        Hit(f1, f2);
        Inv(P, "OnEntityDeath", new EntityDeathEvent { Entity = f2.Entity });
        Leave(f2);
        Ok(Flag(f2, "combat_log") == 2, "dying then leaving is not a combat log");

        Server.ClientPlayers.Add(f2); Inv(P, "OnPlayerConnected", f2);
        Hit(f1, f2);
        Inv(P, "OnServerShutdown");
        Leave(f2);
        Ok(Flag(f2, "combat_log") == 2, "disconnects during shutdown are not flagged");

        Reset();
        var s1 = Mk(76561190000000111, "Self");
        SetF(Rec(s1), "Playtime", 999999.0);
        var ev2 = new EntityDamageEvent { Entity = s1.Entity, Damage = new Damage { Amount = 5, DamageSource = s1.Entity } };
        Inv(P, "OnEntityHealthChange", ev2);
        Ok(!(bool)Inv(P, "IsInCombat", s1.Id), "self damage does not tag combat");
        var cancelled = new EntityDamageEvent { Entity = s1.Entity, Damage = new Damage { Amount = 5, DamageSource = new Player(9, "Other").Entity } };
        cancelled.Cancel("other plugin");
        Inv(P, "OnEntityHealthChange", cancelled);
        Ok(!(bool)Inv(P, "IsInCombat", s1.Id), "hit already cancelled by another plugin is ignored");

        // ---------------- Chat ----------------
        Reset();
        var ch = Mk(76561190000000201, "Chatty");
        object res = null;
        for (int i = 0; i < 5; i++) { res = Say(ch, "hello " + i); }
        Ok(res == null, "5 messages in the window pass");
        res = Say(ch, "hello 5");
        Ok(res is bool && (bool)res && ch.All().Contains("Slow down"), "6th message within 8 s is blocked");
        Advance(9);
        Ok(Say(ch, "after window") == null, "after the window chat flows again");

        Reset();
        var du = Mk(76561190000000202, "Dupe");
        Say(du, "BUY GOLD"); Advance(2); Say(du, "buy gold!"); Advance(2); Say(du, "Buy  Gold");
        Advance(2);
        res = Say(du, "buy gold");
        Ok(res is bool && du.All().Contains("repeat"), "4th repeat of the same text (normalised) is blocked", (res == null ? "null" : res.ToString()) + "\n" + du.All());
        Ok(Say(du, new string('x', 301)) is bool && du.All().Contains("too long"), "over-long message blocked");
        Ok(Flag(du, "chat_flood") == 0, "two strikes: no mute yet");
        Advance(2);
        Say(du, new string('y', 400));
        Ok(Flag(du, "chat_flood") == 1 && du.All().Contains("muted for 1 min"), "third strike mutes for MuteSeconds");
        Advance(5);
        res = Say(du, "let me talk");
        Ok(res is bool && du.All().Contains("You are muted"), "muted player's chat is blocked");
        Advance(60);
        Ok(Say(du, "I am back") == null, "mute expires");
        for (int i = 0; i < 3; i++) { Advance(1); Say(du, new string('z', 500)); }
        Ok((int)F(Rec(du), "MuteLevel") == 2 && du.All().Contains("muted for 2 min"), "repeat mute doubles");
        Ok(Alerts("chat_flood") == 1, "second mute in 24 h raises an admin alert");
        Ok(Evidence("chat_flood") == 2, "each mute is evidence");

        Reset();
        var ad = Mk(76561190000000203, "Admin Person");
        P.permission.Grants.Add(ad.Id + "|" + Admin);
        for (int i = 0; i < 10; i++) Say(ad, "announcement");
        Ok(Say(ad, "one more") == null, "admins exempt from chat limits");

        Reset();
        SetCfg("Chat", "CapsMaxPercent", 70);
        var cp = Mk(76561190000000204, "Caps");
        Ok(Say(cp, "THIS IS VERY LOUD TEXT") is bool, "caps limit blocks shouting when enabled");
        Ok(Say(cp, "OK") == null, "short caps allowed");
        SetCfg("Chat", "FilterWords", true);
        Ok(Say(cp, "i am the server admin") is bool, "FilterWords blocks configured words in chat");

        Reset();
        var mu = Mk(76561190000000205, "Mutee");
        var adm2 = Mk(76561190000000206, "Boss");
        P.permission.Grants.Add(adm2.Id + "|" + Admin);
        Cmd(adm2, "mute Mutee 10");
        Ok(Say(mu, "hi") is bool, "admin mute blocks chat");
        Leave(mu);
        Server.ClientPlayers.Add(mu); Inv(P, "OnPlayerConnected", mu);
        Ok(Say(mu, "hi again") is bool, "mute survives reconnect");
        Cmd(adm2, "unmute Mutee");
        Ok(Say(mu, "free") == null, "admin unmute");

        // ---------------- Names ----------------
        Reset();
        var n1 = Mk(76561190000000301, "Admin");
        Ok(Flag(n1, "offensive_name") == 1 && Alerts("offensive_name") == 1 && n1.All().Contains("not allowed"), "'Admin' flagged, alerted and warned");
        var n2 = Mk(76561190000000302, "Badminton Bob");
        Ok(Flag(n2, "offensive_name") == 0, "'Badminton Bob' not flagged (whole-word entry)");
        var n3 = Mk(76561190000000303, "4dm1n");
        Ok(Flag(n3, "offensive_name") == 1, "leet '4dm1n' flagged");
        var n4 = Mk(76561190000000304, "TheAdmin");
        Ok(Flag(n4, "offensive_name") == 1, "camel-case 'TheAdmin' flagged");
        var n5 = Mk(76561190000000305, "Ad.Min");
        Ok(Flag(n5, "offensive_name") == 1, "split 'Ad.Min' flagged");
        var n6 = Mk(76561190000000306, "[FF0000]Server");
        Ok(Flag(n6, "offensive_name") == 1, "colour-tagged 'Server' flagged");
        var al = L("Alerts").Cast<object>().Last();
        Ok(!((string)F(al, "PlayerName")).Contains("[FF0000]"), "colour tags stripped from names in alerts");
        Ok(Server.Kicks.Count == 0 && P.timer.Pending.Count == 0, "default action never kicks");
        Leave(n1);
        Server.ClientPlayers.Add(n1); Inv(P, "OnPlayerConnected", n1);
        Ok(Flag(n1, "offensive_name") == 1, "re-joining within an hour does not re-flag");

        Reset("{\"Names\":{\"Action\":\"kick\",\"Words\":[\"grief\"]}}");
        var g1 = Mk(76561190000000311, "xXGr13fLordXx");
        Ok(Flag(g1, "offensive_name") == 1 && P.timer.Pending.Count == 1, "substring entry matches inside a name; kick scheduled");
        P.timer.Pending[0]();
        Ok(Server.Kicks.Count == 1 && !Server.Kicks[0].Contains("ban"), "kick after delay (not a ban)");
        Hit(new Player(1, "Z"), g1);
        Leave(g1);
        Ok(Flag(g1, "combat_log") == 0, "kicked player's disconnect is not a combat log");
        var ex1 = new Player(76561190000000312, "Griefer");
        P.Config.Json = null;
        ((List<string>)F(Cfg("Names"), "ExemptIds")).Add(ex1.Id.ToString());
        Server.ClientPlayers.Add(ex1); Inv(P, "OnPlayerConnected", ex1);
        Ok(Flag(ex1, "offensive_name") == 0, "exempt id not flagged");

        // ---------------- Raid hours ----------------
        Reset();
        var rd = Mk(76561190000000401, "Raider");
        SetF(Rec(rd), "Playtime", 999999.0);
        Crests.GroupAtAnyPosition = 900;
        var c0 = Cube(rd);
        Ok(!c0.Cancelled && c0.Damage.Amount == 50f, "raid hours disabled by default: no effect");
        SetCfg("RaidHours", "Enabled", true);
        // Wednesday 12:00 UTC; default windows Wed/Sat 18-23, Sun 14-20.
        var c1 = Cube(rd);
        Ok(c1.Cancelled && c1.Damage.Amount == 0f, "outside raid hours: damage zeroed and cancelled");
        Ok(rd.All().Contains("Outside raid hours"), "attacker told");
        Ok(Evidence("raid_hours") == 1, "first blocked hit recorded as evidence");
        Cube(rd);
        Ok(Evidence("raid_hours") == 1, "evidence throttled per attacker");
        SetTime(new DateTime(2026, 10, 7, 19, 0, 0, DateTimeKind.Utc));
        var c2 = Cube(rd);
        Ok(!c2.Cancelled && c2.Damage.Amount == 50f, "inside a window: damage allowed");
        SetTime(new DateTime(2026, 10, 7, 23, 0, 0, DateTimeKind.Utc));
        Ok(Cube(rd).Cancelled, "window end is exclusive");
        SetTime(new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
        SocialAPI.Groups[rd.Id] = 900;
        Ok(!Cube(rd).Cancelled, "own crest group may break its own blocks");
        SocialAPI.Groups.Clear();
        Crests.GroupAtAnyPosition = 0;
        Ok(!Cube(rd).Cancelled, "unclaimed blocks not protected by default");
        Crests.GroupAtAnyPosition = 900;
        Ok(!Cube(rd, 50f, DamageType.Salvage).Cancelled, "salvage ignored");
        var rep = Cube(rd, -20f, DamageType.Healing);
        Ok(!rep.Cancelled && rep.Damage.Amount == -20f, "repair (negative) ignored");
        var pre = new CubeDamageEvent { Damage = new Damage { Amount = 10, DamageSource = rd.Entity } };
        pre.Cancel("salvage supplier");
        Inv(P, "OnCubeTakeDamage", pre);
        Ok(pre.Damage.Amount == 10f, "already-cancelled event left alone");
        Ok(Cube(null).Cancelled, "unknown attacker inside a crest blocked (BlockUnknownAttacker)");
        Rebellion = true;
        Ok(!Cube(rd).Cancelled, "rebellion window opens raiding");
        Ok((bool)Inv(P, "IsRaidHourNow"), "API: raid hour during rebellion");
        Rebellion = false;
        var radm = Mk(76561190000000402, "Siege Admin");
        P.permission.Grants.Add(radm.Id + "|" + Admin);
        Ok(!Cube(radm).Cancelled, "admins exempt");
        SetCfg("RaidHours", "Block", false);
        var c3 = Cube(rd);
        Ok(!c3.Cancelled && c3.Damage.Amount == 50f, "record-only mode does not block");
        SetCfg("RaidHours", "Block", true);
        for (int i = 0; i < 30; i++) Cube(rd);
        Ok(Alerts("raid_hours") == 1 && Flag(rd, "raid_hours") == 1, "alert + flag after AlertAfterBlockedHits in 10 min");
        for (int i = 0; i < 30; i++) Cube(rd);
        Ok(Alerts("raid_hours") == 1, "raid alert throttled to one per 30 min");

        Reset("{\"RaidHours\":{\"Enabled\":true,\"UtcOffsetMinutes\":-300,\"Windows\":[{\"Days\":[\"Tue\"],\"Start\":\"22:00\",\"End\":\"02:00\"}]}}");
        var rz = Mk(76561190000000403, "Zoner");
        SetF(Rec(rz), "Playtime", 999999.0);
        Crests.GroupAtAnyPosition = 5;
        SetTime(new DateTime(2026, 10, 7, 5, 30, 0, DateTimeKind.Utc));   // Wed 00:30 at UTC-5: Tue window still open
        Ok(!Cube(rz).Cancelled, "window crossing midnight is open after midnight (UTC offset applied)");
        SetTime(new DateTime(2026, 10, 7, 7, 30, 0, DateTimeKind.Utc));   // Wed 02:30 local
        Ok(Cube(rz).Cancelled, "closed after the window ends");
        Clear();
        Cmd(rz, "status");
        Ok(rz.All().Contains("Next window: Tue 22:00 UTC-05:00"), "status shows next window in local time", rz.All());

        Reset();
        SetCfg("RaidHours", "Enabled", false);
        var pr = Mk(76561190000000404, "ProtBuilder");
        Crests.GroupAtAnyPosition = 77;
        Cube(pr);
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", pr.Id), "protected player damaging another group's structure loses protection");

        // ---------------- Reports ----------------
        Reset();
        var rep1 = Mk(76561190000000501, "Reporter");
        var bad = Mk(76561190000000502, "Baddie");
        Clear();
        Cmd(rep1, "report Baddie");
        Ok(rep1.All().Contains("Usage"), "report without reason shows usage");
        Cmd(rep1, "report Reporter spamming");
        Ok(rep1.All().Contains("yourself"), "cannot report yourself");
        Cmd(rep1, "report Baddie ok");
        Ok(rep1.All().Contains("reason of"), "too-short reason refused");
        Cmd(rep1, "report Badd destroying my door outside raid hours");
        Ok(rep1.All().Contains("Report #") && Alerts("report") == 1 && Flag(bad, "report") == 1, "report by partial name creates alert and counter");
        Cmd(rep1, "report Baddie again and again");
        Ok(rep1.All().Contains("another report in"), "report cooldown");
        Advance(121);
        Cmd(rep1, "report Baddie again and again");
        Ok(rep1.All().Contains("already reported"), "same-target cooldown");
        Leave(bad);
        var third = Mk(76561190000000503, "Third");
        Advance(121);
        Cmd(rep1, "report Baddie offline griefing");
        Ok(Alerts("report") == 1, "same target still blocked while offline");
        Cmd(rep1, "report Third name calling");
        Ok(Alerts("report") == 2, "another target accepted");
        SetF(Rec(rep1), "ReportTimes", Enumerable.Range(0, 10).Select(i => (long)((double)F(P, "clockSkew") + (DateTime.UtcNow - Epoch).TotalSeconds) - 3600 + i).ToList());
        SetF(Rec(rep1), "ReportedAt", new Dictionary<string, long>());
        Advance(121);
        Clear();
        Cmd(rep1, "report Third more name calling");
        Ok(rep1.All().Contains("that is the limit"), "daily report cap");

        // ---------------- Alerts and admin views ----------------
        Reset();
        var boss = Mk(76561190000000601, "Boss");
        P.permission.Grants.Add(boss.Id + "|" + Admin);
        var sp = Mk(76561190000000602, "Spammer");
        Clear();
        for (int i = 0; i < 10; i++) Inv(P, "RaiseWardenAlert", "test", sp.Id, "external " + i);
        Ok(Alerts("ext:test") == 10, "other plugins can raise alerts");
        Ok(boss.Messages.Count(m => m.Contains("ext:test")) == 6, "admin chat alerts capped per minute");
        Advance(61);
        Inv(P, "RaiseWardenAlert", "test", sp.Id, "after a minute");
        Ok(boss.All().Contains("+4 more alerts"), "overflow summarised");
        Clear();
        Cmd(boss, "alerts");
        Ok(boss.All().Contains("11 unread") && boss.All().Contains("page 1/2"), "alerts listing paginated");
        Cmd(boss, "ack 3");
        Ok(boss.All().Contains("Acknowledged 1"), "ack one alert");
        Cmd(boss, "ack all");
        Ok(boss.All().Contains("Acknowledged 10"), "ack all");
        Clear();
        Cmd(boss, "alerts");
        Ok(boss.All().Contains("No alerts"), "no unread left");
        Cmd(boss, "alerts all");
        Ok(boss.All().Contains("[ack Boss]"), "alerts all shows acknowledged");
        for (int i = 0; i < 40; i++) Inv(P, "RaiseWardenAlert", "flood", sp.Id, "x");
        Ok(Alerts("ext:flood") == 19, "external alerts capped per hour", Alerts("ext:flood").ToString());
        SetCfg("Alerts", "MaxStored", 10);
        Advance(3601);
        Inv(P, "RaiseWardenAlert", "cap", sp.Id, "y");
        Ok(L("Alerts").Count == 10 && L("Alerts").Cast<object>().All(q => !(bool)F(q, "Ack")), "alert cap drops acknowledged first");
        Clear();
        Cmd(boss, "player Spammer");
        Ok(boss.All().Contains("Spammer (76561190000000602)") && boss.All().Contains("Protection:"), "player summary");
        Cmd(boss, "evidence");
        Cmd(boss, "clear Spammer");
        Ok(boss.All().Contains("Cleared"), "clear flags");
        Clear();
        Mk(76561190000000603, "Spammer Two");
        Cmd(boss, "player Spam");
        Ok(boss.All().Contains("More than one"), "ambiguous partial name refused");
        Clear();
        Leave(boss);
        Inv(P, "RaiseWardenAlert", "late", sp.Id, "z");
        Server.ClientPlayers.Add(boss); Inv(P, "OnPlayerConnected", boss);
        Ok(boss.All().Contains("unread Warden alerts"), "admin reminded on join");
        Clear();
        Cmd(boss, "help");
        Ok(boss.All().Contains("Admin:"), "admin help");
        Cmd(sp, "help");
        Ok(sp.All().Contains("/warden report") && !sp.All().Contains("Admin:"), "player help hides admin commands");
        Cmd(sp, "rules");
        Ok(sp.All().Contains("Raid hours: not enforced"), "rules show raid status");
        Ok((int)Inv(P, "GetWardenFlagCount", sp.Id) == 0 && (int)Inv(P, "GetWardenFlagCount", 1UL) == 0, "flag count API");

        // ---------------- Persistence and corruption safety ----------------
        Reset();
        var pp = Mk(76561190000000701, "Persisted");
        SetF(Rec(pp), "Playtime", 1234.0);
        Inv(P, "RaiseWardenAlert", "persist", pp.Id, "kept");
        Inv(P, "OnServerSave");
        P = NewPlugin(null);
        Ok(Players().Contains(pp.Id.ToString()) && (double)F(Rec(pp), "Playtime") == 1234.0 && Alerts("ext:persist") == 1, "data survives reload");
        Ok((int)F(Data(), "NextAlertId") == 2, "ids continue after reload");

        string file = Path.Combine(Dir, "RealmWarden.json");
        File.WriteAllText(file, "{ this is not json");
        P = NewPlugin(null);
        Ok((bool)F(P, "loadFailed"), "corrupt data detected");
        var cz = Mk(76561190000000702, "AfterCorrupt");
        Ok(!(bool)Inv(P, "IsNewPlayerProtected", cz.Id), "protection off while data is damaged");
        Inv(P, "OnServerSave"); Inv(P, "Unload");
        Ok(File.ReadAllText(file) == "{ this is not json", "damaged file never overwritten");
        Ok(P.Log.Any(l => l.StartsWith("ERROR") && l.Contains("NOT be overwritten")), "error logged for the owner");
        var cadm = Mk(76561190000000703, "Owner");
        P.permission.Grants.Add(cadm.Id + "|" + Admin);
        Clear();
        Cmd(cadm, "help");
        Ok(cadm.All().Contains("records are damaged"), "admins told about damaged data");
        Ok(Say(cz, "chat still works") == null, "chat limits keep running from memory");

        File.WriteAllText(file, "null");
        P = NewPlugin(null);
        Ok((bool)F(P, "loadFailed") && File.ReadAllText(file) == "null", "null data file treated as damaged");

        Reset();
        var hot = new Player(76561190000000801, "AlreadyOnline");
        Server.ClientPlayers.Add(hot);
        P = NewPlugin(null);
        Ok(Players().Contains(hot.Id.ToString()), "hot load registers players already online");
        int before = P.timer.Repeating.Count;
        Inv(P, "OnServerInitialized");
        Ok(P.timer.Repeating.Count == before, "OnServerInitialized idempotent");
    }
}
