// Behaviour tests for plugins/RealmSeasons.cs and plugins/RealmEvents.cs, wired together the way Oxide wires them
// (RealmEvents' [PluginReference] RealmSeasons calls into the real RealmSeasons object). Run with run.sh.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CodeHatch.Damaging;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Thrones.AncientThrone;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static DateTime Clock;
    static KingsScheme Kings;
    static Plugin Chron, Houses, Crown, SeasonsRef;
    static RealmSeasons S;
    static RealmEvents E;
    static Dictionary<ulong, string> HouseOf = new Dictionary<ulong, string>();
    static List<string> ChronLog = new List<string>();
    static List<Dictionary<string, object>> ChronFile = new List<Dictionary<string, object>>();
    static HashSet<string> Treaties = new HashSet<string>();
    static List<string> OpenClaims = new List<string>();
    static bool Rebellion;
    static int ChronId;
    static string Dir;

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
    static string B() { return string.Join("\n", Server.Broadcasts); }
    static void Clear() { Server.Broadcasts.Clear(); ChronLog.Clear(); foreach (var p in Server.ClientPlayers) p.Messages.Clear(); }

    static Player Mk(ulong id, string name, string house)
    {
        var p = new Player(id, name);
        if (house != null) { HouseOf[id] = house; p.Guild = new Guild { Name = house }; }
        Server.ClientPlayers.Add(p);
        return p;
    }

    static void Crowned(Player p) { Kings.King = p != null ? p.Id : 0; Kings.KingName = p != null ? p.Name : null; }

    static void ChronAppend(string type, string title, string detail)
    {
        ChronId++;
        ChronFile.Add(new Dictionary<string, object> { { "id", ChronId }, { "ts", Clock.ToString("yyyy-MM-ddTHH:mm:ssZ") }, { "type", type }, { "title", title }, { "detail", detail }, { "actors", new string[0] } });
        File.WriteAllText(Path.Combine(Dir, "RealmChronicle.json"), JsonSerializer.Serialize(ChronFile));
    }

    static RealmSeasons NewSeasons(string configJson = null)
    {
        var p = new RealmSeasons();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "CrownAndConsequences", Crown);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static RealmEvents NewEvents(string configJson = null)
    {
        var p = new RealmEvents();
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        SetF(p, "RealmChronicle", Chron); SetF(p, "RealmHouses", Houses); SetF(p, "CrownAndConsequences", Crown); SetF(p, "RealmSeasons", SeasonsRef);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        return p;
    }

    static void TickS() { Inv(S, "SafeTick"); }
    static void TickE() { Inv(E, "SafeTick"); }
    static void Advance(TimeSpan by, TimeSpan step)
    {
        DateTime until = Clock + by;
        while (Clock < until) { Clock += step; TickS(); if (E != null) TickE(); }
    }

    static object SData() { return F(S, "season"); }
    static object Legends() { return F(S, "legends"); }
    static object Standing(string house)
    {
        var d = (IDictionary)F(SData(), "Houses");
        foreach (DictionaryEntry kv in d) if (string.Equals((string)kv.Key, house, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
    static int SI(string house, string field) { var h = Standing(house); return h == null ? 0 : Convert.ToInt32(F(h, field)); }
    static int Score(string house) { var h = Standing(house); return h == null ? 0 : (int)Inv(S, "Score", h); }
    static IList Active() { return (IList)F(F(E, "data"), "Active"); }
    static object Running(string kind) { return Inv(E, "Running", kind); }
    static int Count(Player p, string item) { int n; return p.Inventory.Contents.Counts.TryGetValue(item, out n) ? n : 0; }

    static void Kill(Player killer, Player victim)
    {
        var evt = new EntityDeathEvent { Entity = victim.Entity, KillingDamage = new Damage { Amount = 100, DamageSource = killer.Entity } };
        Inv(E, "OnEntityDeath", evt);
    }
    static EntityDamageEvent Hit(Player attacker, Player victim)
    {
        var evt = new EntityDamageEvent { Entity = victim.Entity, Damage = new Damage { Amount = 20, DamageSource = attacker.Entity } };
        Inv(E, "OnEntityHealthChange", evt);
        return evt;
    }

    static void Main(string[] argv)
    {
        Dir = Path.Combine(Path.GetTempPath(), "realmevents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        Kings = new KingsScheme();
        SocialAPI.Registry[typeof(KingsScheme)] = Kings;
        SocialAPI.Registry[typeof(GuildScheme)] = new GuildScheme();

        Chron = new Plugin { Name = "RealmChronicle" };
        Chron.Handler = (hook, a) =>
        {
            if (hook == "GetLastEventId") return ChronId;
            if (hook == "Log") { ChronLog.Add(a[0] + "|" + a[1] + "|" + a[2]); return 1; }
            return null;
        };
        Houses = new Plugin { Name = "RealmHouses" };
        Houses.Handler = (hook, a) =>
        {
            if (hook == "GetHouse") { string h; return HouseOf.TryGetValue(ulong.Parse((string)a[0]), out h) ? h : null; }
            if (hook == "HasTreaty") { string k = ((string)a[0]).ToLower() + "|" + ((string)a[1]).ToLower(); string r = ((string)a[1]).ToLower() + "|" + ((string)a[0]).ToLower(); return Treaties.Contains(k) || Treaties.Contains(r); }
            if (hook == "GetHouseSummaries") return HouseOf.Values.Distinct().Select(h => new Dictionary<string, object> { { "name", h } }).ToList();
            if (hook == "GetMemberNames") return Server.ClientPlayers.Where(p => HouseOf.ContainsKey(p.Id) && HouseOf[p.Id] == (string)a[0]).Select(p => p.Name).ToList();
            return null;
        };
        Crown = new Plugin { Name = "CrownAndConsequences" };
        Crown.Handler = (hook, a) =>
        {
            if (hook == "GetKingName") return Kings.KingName;
            if (hook == "GetKingHouse") { string h; return Kings.King != 0 && HouseOf.TryGetValue(Kings.King, out h) ? h : null; }
            if (hook == "GetOpenClaims") return OpenClaims.ToArray();
            if (hook == "IsRebellionActive") return Rebellion;
            return null;
        };
        SeasonsRef = new Plugin { Name = "RealmSeasons" };
        SeasonsRef.Handler = (hook, a) => Inv(S, hook, a);   // the real plugin, reached like plugin.Call

        var admin = Mk(76561190000000001, "Steward", null);
        var aldric = Mk(76561190000000002, "Aldric", "Varn");
        var bryn = Mk(76561190000000003, "Bryn", "Varn");
        var cass = Mk(76561190000000004, "Cass", "Morrow");
        var dain = Mk(76561190000000005, "Dain", "Morrow");
        var edda = Mk(76561190000000006, "Edda", "Thorne");

        Clock = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);   // a Friday
        ChronAppend("decree", "old history", "before the first season");

        // ---------------- RealmSeasons ----------------
        S = NewSeasons();
        S.permission.Grants.Add(admin.Id + "|realmseasons.admin");
        Ok((bool)F(SData(), "Active") && (int)F(SData(), "Number") == 1, "first load auto-starts Season 1");
        Ok(ChronLog.Any(l => l.StartsWith("season_started|Season 1 begins")), "season_started chronicled", string.Join("\n", ChronLog));
        Ok((int)F(SData(), "ChronicleCursor") == 1, "season cursor skips history written before the season");
        Ok(B().Contains("Season 1 begins"), "season start heralded");

        Crowned(aldric);
        Clear();
        Advance(TimeSpan.FromHours(12), TimeSpan.FromMinutes(5));
        var cur = F(Legends(), "Current");
        Ok(cur != null && (string)F(cur, "Monarch") == "Aldric" && (string)F(cur, "House") == "Varn", "reign opened for the new monarch with house");
        double days = Convert.ToDouble(F(Standing("Varn"), "CrownSeconds")) / 86400.0;
        Ok(Math.Abs(days - 0.5) < 0.01, "12 h on the throne = 0.5 crown days", days.ToString());
        Ok(Score("Varn") == 5, "crown days score at 10 pts/day", Score("Varn").ToString());

        // Downtime is not credited: one tick after a 6 h stall credits at most MaxCreditSecondsPerTick.
        Clock += TimeSpan.FromHours(6); TickS();
        days = Convert.ToDouble(F(Standing("Varn"), "CrownSeconds")) / 86400.0;
        Ok(days < 0.51, "a long stall is not credited as crown time", days.ToString());

        // Rebellion: Morrow's claim is active and Cass takes the throne.
        OpenClaims.Add("Morrow|active|x|y");
        Inv(S, "OnThroneReleased", new AncientThroneReleaseEvent { Sender = aldric, IsDeath = true });
        Crowned(cass);
        TickS();
        var hall = (IList)F(Legends(), "Hall");
        Ok(hall.Count == 1 && ((string)F(hall[0], "Ending")).Contains("rebellion of House Morrow"), "reign ended by an active claimant is recorded as overthrown by rebellion",
            hall.Count > 0 ? (string)F(hall[0], "Ending") : "none");
        OpenClaims.Clear();

        // Chronicle feed. The anti-farming limits on treaties kept (minimum term and members) are switched off here;
        // tools/exploit-review/seasons tests them at their defaults.
        SetF(F(S, "config"), "TreatyKeptMinDays", 0);
        SetF(F(S, "config"), "TreatyKeptMinMembers", 0);
        ChronAppend("rebellion_ended", "The rebellion of House Morrow ends", "House Morrow prevailed and holds the crown.");
        ChronAppend("treaty_signed", "House Varn and House Thorne sign a treaty", "The treaty holds for 7 days.");
        Treaties.Add("varn|thorne");
        ChronAppend("treaty_signed", "House Morrow and House Thorne sign a treaty", "The treaty holds for 7 days.");
        Treaties.Add("morrow|thorne");
        ChronAppend("treaty_broken", "House Morrow breaks its treaty with House Thorne", "Cass tears up the treaty.");
        Treaties.Remove("morrow|thorne");
        ChronAppend("oath_broken", "House Varn renounces its oath to House Thorne", "x");
        ChronAppend("contract_fulfilled", "Bryn collects the price on Dain", "Bryn slew Dain.");
        ChronAppend("contract_fulfilled", "Nobody fills an order for Cass", "unknown fulfiller");
        TickS();
        Ok(SI("Morrow", "RebellionsWon") == 1, "rebellion won parsed from the chronicle");
        Ok(SI("Morrow", "TreatiesBroken") == 1 && SI("Varn", "OathsBroken") == 1, "treaty broken and oath broken parsed");
        Ok(SI("Varn", "ContractsFulfilled") == 1, "contract fulfiller mapped to their house by name");
        Ok(SI("Varn", "TreatiesKept") == 0, "treaty still in force is not yet 'kept'");
        Treaties.Remove("varn|thorne");
        Clock += TimeSpan.FromMinutes(6); TickS();
        Ok(SI("Varn", "TreatiesKept") == 1 && SI("Thorne", "TreatiesKept") == 1 && SI("Morrow", "TreatiesKept") == 0,
            "a treaty that lapsed without a break counts as kept for both houses, a broken one never");
        int cursor = (int)F(SData(), "ChronicleCursor");
        TickS();
        Ok(SI("Morrow", "RebellionsWon") == 1 && (int)F(SData(), "ChronicleCursor") == cursor, "events are counted once");

        // The crown held against a rebellion: the defending house is credited.
        ChronAppend("rebellion_ended", "The rebellion of House Thorne ends", "The crown held. House Thorne failed.");
        TickS();
        Ok(SI("Morrow", "RebellionsDefended") == 1, "a defended rebellion credits the crown's house");

        // AwardHouse caps and requires a running season.
        Ok((bool)Inv(S, "AwardHouse", "Thorne", 5000, "test"), "AwardHouse accepted");
        Ok(SI("Thorne", "EventPoints") == 100, "AwardHouse capped at MaxEventAwardPerCall");

        // Commands.
        Clear();
        Inv(S, "CmdSeason", bryn, "season", new string[0]);
        Ok(bryn.All().Contains("Season 1") && bryn.All().Contains("Your house, Varn"), "/season shows status and the player's house rank", bryn.All());
        Inv(S, "CmdSeason", bryn, "season", new[] { "hall" });
        Ok(bryn.All().Contains("Hall of Kings (2 reigns)") && bryn.All().Contains("Cass of House Morrow"), "/season hall lists past and current reigns", bryn.All());
        Inv(S, "CmdSeason", bryn, "season", new[] { "end" });
        Ok(bryn.All().Contains("ERR You may not do that."), "non-admin cannot end the season");

        // Ceremony.
        Clear();
        Inv(S, "CmdSeason", admin, "season", new[] { "end" });
        Ok(!(bool)F(SData(), "Active"), "admin ends the season");
        Ok(B().Contains("Season 1 has ended") && B().Contains("is champion of Season 1"), "ceremony heralded", B());
        Ok(ChronLog.Any(l => l.StartsWith("season_ended|Season 1 ends: House ")), "season_ended chronicled", string.Join("\n", ChronLog));
        var seasons = (IList)F(Legends(), "Seasons");
        Ok(seasons.Count == 1 && F(seasons[0], "Champion") != null, "season record with champion written to the legends");
        Ok(File.Exists(Path.Combine(Dir, "RealmLegends.json")), "legends file written");

        // Wipe: delete the running-season file (and the chronicle restarts); legends survive and numbering continues.
        Inv(S, "Unload");
        File.Delete(Path.Combine(Dir, "RealmSeasons.json"));
        ChronFile.Clear(); ChronId = 0; File.Delete(Path.Combine(Dir, "RealmChronicle.json"));
        Crowned(null);
        S = NewSeasons();
        S.permission.Grants.Add(admin.Id + "|realmseasons.admin");
        Ok(!(bool)F(SData(), "Active"), "after a wipe no season auto-starts (legends show one was already run)");
        Ok(((IList)F(Legends(), "Seasons")).Count == 1, "legends survive the wipe");
        hall = (IList)F(Legends(), "Hall");
        Ok(hall.Count == 2 && ((string)F(hall[1], "Ending")) == "no longer seated when the realm awoke", "the reign cut by the wipe is closed and remembered",
            hall.Count > 1 ? (string)F(hall[1], "Ending") : "");
        Clear();
        Inv(S, "CmdSeason", admin, "season", new[] { "start", "14", "The", "Long", "Winter" });
        Ok((int)F(SData(), "Number") == 2 && (string)F(SData(), "Name") == "The Long Winter", "season 2 starts with a custom name", (string)F(SData(), "Name"));
        Ok(((DateTime)F(SData(), "EndsAt") - Clock).TotalDays == 14, "custom length honoured");
        Clock += TimeSpan.FromDays(14).Add(TimeSpan.FromMinutes(1)); TickS();
        Ok(!(bool)F(SData(), "Active") && ((IList)F(Legends(), "Seasons")).Count == 2, "season ends by itself at its end date");
        Ok(B().Contains("No house earned glory"), "a season with no points ends without a champion");

        // Corrupt legends: refuse to run and never overwrite.
        Inv(S, "Unload");
        File.WriteAllText(Path.Combine(Dir, "RealmLegends.json"), "{ broken");
        var bad = NewSeasons();
        Inv(bad, "SafeTick"); Inv(bad, "Unload");
        Ok((bool)F(bad, "loadFailed") && File.ReadAllText(Path.Combine(Dir, "RealmLegends.json")) == "{ broken", "corrupt legends: refuses and does not overwrite");
        File.Delete(Path.Combine(Dir, "RealmLegends.json"));
        File.Delete(Path.Combine(Dir, "RealmSeasons.json"));
        S = NewSeasons();   // fresh realm: season 1 auto-starts again, used by the events below
        Ok((bool)F(SData(), "Active"), "fresh season running for the event tests");

        // ---------------- RealmEvents ----------------
        Crowned(aldric);
        Clock = new DateTime(2026, 10, 2, 17, 50, 0, DateTimeKind.Utc);   // Friday, 70 min before the tournament
        Clear();
        E = NewEvents();
        E.permission.Grants.Add(admin.Id + "|realmevents.admin");
        Ok(!B().Contains("Royal Tournament"), "no countdown herald 70 min out");
        Clock = new DateTime(2026, 10, 2, 18, 0, 0, DateTimeKind.Utc); TickE();
        Ok(B().Contains("The Royal Tournament begins in 60 min"), "60-minute countdown herald", B());
        Clear(); TickE();
        Ok(!B().Contains("begins in"), "each threshold heralds once");
        Clock = new DateTime(2026, 10, 2, 18, 45, 0, DateTimeKind.Utc); TickE();
        Ok(B().Contains("begins in 15 min") && Server.Broadcasts.Count(b => b.Contains("begins in")) == 1, "missed thresholds collapse into one herald", B());

        Inv(E, "CmdTourney", aldric, "tourney", new[] { "join" });
        Ok(aldric.All().Contains("You enter the Royal Tournament"), "can join during the countdown", aldric.All());
        Clock = new DateTime(2026, 10, 2, 19, 0, 0, DateTimeKind.Utc); Clear(); TickE();
        Ok(Running("tournament") != null, "tournament starts on schedule");
        Ok(ChronLog.Any(l => l.StartsWith("event_started|The Royal Tournament begins")), "event_started chronicled", string.Join("\n", ChronLog));
        Ok(((IDictionary)F(Running("tournament"), "Entrants")).Contains(aldric.Id.ToString()), "countdown entry carried into the tournament");
        foreach (var p in new[] { bryn, cass, dain }) Inv(E, "CmdTourney", p, "tourney", new[] { "join" });

        Kill(aldric, cass); Kill(aldric, cass); Kill(aldric, cass);   // third kill of the same victim does not count
        Kill(aldric, bryn);                                            // housemate: never counts
        Kill(cass, dain);                                              // housemate
        Kill(dain, bryn);
        Kill(aldric, edda);                                            // not entered
        var ent = (IDictionary)F(Running("tournament"), "Entrants");
        Ok(Convert.ToInt32(F(ent[aldric.Id.ToString()], "Kills")) == 2, "per-victim cap and housemate rule hold", F(ent[aldric.Id.ToString()], "Kills").ToString());
        Ok(Convert.ToInt32(F(ent[dain.Id.ToString()], "Kills")) == 1 && Convert.ToInt32(F(ent[cass.Id.ToString()], "Kills")) == 0, "other kills scored correctly");
        Ok(!ent.Contains(edda.Id.ToString()), "non-entrants are not added when joining is required");

        // A truce cannot start during the tournament.
        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "start", "truce", "30" });
        Ok(admin.All().Contains("cannot run alongside") && Running("truce") == null, "clashing event refused", admin.All());

        int varnBefore = SI("Varn", "EventPoints");
        dain.Inventory.Contents.Capacity = 50;                        // Dain's packs are almost full
        Clock = new DateTime(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc); Clear(); TickE();
        Ok(Running("tournament") == null, "tournament ends at its end time");
        Ok(B().Contains("#1 Aldric (2 kills)") && B().Contains("#2 Dain (1 kills)"), "standings heralded", B());
        Ok(ChronLog.Any(l => l.StartsWith("tournament_champion|Aldric wins the Royal Tournament")), "tournament_champion chronicled");
        Ok(Count(aldric, "Stone") == 300, "1st prize given and measured", Count(aldric, "Stone").ToString());
        Ok(Count(dain, "Stone") == 50, "2nd prize only partly fits", Count(dain, "Stone").ToString());
        var owed = (IList)F(F(E, "data"), "Owed");
        Ok(owed.Count == 1 && Convert.ToInt32(F(owed[0], "Amount")) == 150, "the rest is owed, not lost or duplicated");
        Ok(SI("Varn", "EventPoints") - varnBefore == 30 && SI("Morrow", "EventPoints") == 20, "season points awarded to the winners' houses",
            (SI("Varn", "EventPoints") - varnBefore) + " / " + SI("Morrow", "EventPoints"));
        dain.Inventory.Contents.Capacity = 100000;
        Inv(E, "CmdEvent", dain, "event", new[] { "collect" });
        Ok(Count(dain, "Stone") == 200 && ((IList)F(F(E, "data"), "Owed")).Count == 0, "/event collect pays the rest exactly once");
        Inv(E, "CmdEvent", dain, "event", new[] { "collect" });
        Ok(Count(dain, "Stone") == 200, "collect again pays nothing more");

        // Reload during a window: no second start of the same occurrence.
        Clock = new DateTime(2026, 10, 2, 19, 30, 0, DateTimeKind.Utc);
        Inv(E, "Unload");
        E = NewEvents();
        E.permission.Grants.Add(admin.Id + "|realmevents.admin");
        Ok(Running("tournament") == null, "a finished occurrence never starts again after a reload");

        // Truce (manual), enforced.
        Clock = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "start", "truce", "60" });
        Ok(Running("truce") != null && B().Contains("Truce of the Realm is proclaimed"), "admin starts a truce", admin.All() + B());
        var hit = Hit(cass, aldric);
        Ok(hit.Cancelled && hit.Damage.Amount == 0f, "player damage cancelled during the truce");
        Ok(cass.All().Contains("your blow does not land"), "attacker told why");
        Rebellion = true;
        hit = Hit(cass, aldric);
        Ok(!hit.Cancelled, "the truce yields to an open rebellion");
        Kill(cass, aldric);
        Ok(!ChronLog.Any(l => l.StartsWith("truce_broken")), "a kill during the suspended truce is lawful war");
        Rebellion = false;
        int morrowBefore = SI("Morrow", "EventPoints");
        Kill(cass, bryn); Kill(cass, aldric);
        Ok(ChronLog.Count(l => l.StartsWith("truce_broken|Cass breaks the Truce")) == 1, "a breach is chronicled once per killer", string.Join("\n", ChronLog));
        Ok(SI("Morrow", "EventPoints") - morrowBefore == -15, "breaker's house loses points");
        Inv(E, "CmdTruce", edda, "truce", new string[0]);
        Ok(edda.All().Contains("enforced"), "/truce shows the mode", edda.All());
        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "stop", "truce" });
        Ok(Running("truce") == null && B().Contains("Truce breakers: 1"), "admin stop ends with the result", B());

        // King's Hunt: called off without quarry, then a real hunt.
        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "start", "kings_hunt", "60" });
        Ok(Running("kings_hunt") != null, "hunt starts with a monarch on the throne");
        Clock += TimeSpan.FromMinutes(11); TickE();
        Ok(Running("kings_hunt") == null && B().Contains("called off"), "no quarry in time: called off", B());

        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "start", "kings_hunt", "60" });
        Inv(E, "CmdHunt", bryn, "hunt", new[] { "name", "Cass" });
        Ok(bryn.All().Contains("Only the reigning monarch"), "only the monarch names quarry");
        Inv(E, "CmdHunt", aldric, "hunt", new[] { "name", "Cass" });
        Inv(E, "CmdHunt", aldric, "hunt", new[] { "name", "Edda" });
        Inv(E, "CmdHunt", aldric, "hunt", new[] { "name", "Aldric" });
        var q = (IList)F(Running("kings_hunt"), "Quarry");
        Ok(q.Count == 2 && aldric.All().Contains("cannot hunt yourself"), "quarry named; monarch cannot name themselves", aldric.All());
        Kill(dain, cass);                                              // housemate of the quarry: does not claim it
        Ok(F(q[0], "ClaimedById") == null, "a housemate cannot claim the quarry");
        int bStone = Count(bryn, "Wood");
        varnBefore = SI("Varn", "EventPoints");
        Kill(bryn, cass);
        Ok((string)F(q[0], "ClaimedById") == bryn.Id.ToString() && Count(bryn, "Wood") - bStone == 200, "hunter claims the quarry and is paid");
        Ok(ChronLog.Any(l => l.StartsWith("hunt_kill|Bryn takes the King's quarry Cass")) && SI("Varn", "EventPoints") - varnBefore == 15, "hunt_kill chronicled and points awarded");
        Kill(aldric, cass);
        Ok(Count(aldric, "Wood") == 0, "a quarry is claimed only once");
        int thorneBefore = SI("Thorne", "EventPoints");
        Clock += TimeSpan.FromMinutes(61); Clear(); TickE();
        Ok(B().Contains("Survived the hunt: Edda") && SI("Thorne", "EventPoints") - thorneBefore == 10, "survivor's house rewarded at the end", B());

        Crowned(null);
        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "start", "kings_hunt", "60" });
        Ok(Running("kings_hunt") == null && B().Contains("no monarch"), "no hunt without a monarch", B());

        // Crown Night with claims.
        Crowned(aldric);
        OpenClaims.Add("Morrow|pending|a|b");
        Clock = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc); Clear(); TickE();
        Ok(B().Contains("Crown Night falls in 60 min") && B().Contains("House Morrow (pending)"), "Crown Night countdown names the claims", B());
        Clock = new DateTime(2026, 10, 3, 19, 0, 0, DateTimeKind.Utc); TickE();
        Ok(Running("crown_night") != null, "Crown Night starts with the rebellion window");
        int morrowEv = SI("Morrow", "EventPoints");
        var cap = new AncientThroneCaptureEvent { Player = cass, State = AncientThroneCaptureEvent.States.Completed };
        Inv(E, "OnThroneCaptured", cap); Inv(E, "OnThroneCaptured", cap);
        Inv(E, "OnThroneCaptured", new AncientThroneCaptureEvent { Player = dain, State = AncientThroneCaptureEvent.States.Capturing });
        Ok(SI("Morrow", "EventPoints") - morrowEv == 10, "capture points once per house, only for completed captures");
        Crowned(cass);
        Clock = new DateTime(2026, 10, 3, 20, 30, 0, DateTimeKind.Utc); Clear(); TickE();
        Ok(Running("crown_night") == null && B().Contains("The crown rests with House Morrow"), "Crown Night ends naming the holder", B());
        Ok(SI("Morrow", "EventPoints") - morrowEv == 40, "the holding house earns the hold points");
        Ok(ChronLog.Any(l => l.StartsWith("event_ended|Crown Night ends|") && l.Contains("changed hands from House Varn")), "Crown Night chronicle notes the change of hands", string.Join("\n", ChronLog));

        // /events listing.
        Clear();
        Inv(E, "CmdEvents", edda, "events", new string[0]);
        Ok(edda.All().Contains("Next: The Royal Tournament") && edda.All().Contains("Next: The Truce of the Realm"), "/events lists the upcoming schedule", edda.All());

        // Disabled kind is neither scheduled nor startable.
        var cfg = F(E, "config");
        SetF(cfg, "EnableTruce", false);
        Clear();
        Inv(E, "CmdEvent", admin, "event", new[] { "start", "truce" });
        Ok(admin.All().Contains("disabled"), "disabled event kind cannot be started");
        Clock = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc); TickE();
        Ok(Running("truce") == null, "disabled event kind is not scheduled");

        // Corrupt data file: refuses, never overwrites.
        Inv(E, "Unload");
        File.WriteAllText(Path.Combine(Dir, "RealmEvents.json"), "[ nope");
        var badE = NewEvents();
        Inv(badE, "SafeTick"); Inv(badE, "Unload");
        Ok((bool)F(badE, "loadFailed") && File.ReadAllText(Path.Combine(Dir, "RealmEvents.json")) == "[ nope", "corrupt events data: refuses and does not overwrite");

        // The chronicle texts RealmSeasons parses must still be what the other plugins write.
        if (argv.Length > 0)
        {
            string R(string f) { return File.ReadAllText(Path.Combine(argv[0], "plugins", f)); }
            string crown = R("CrownAndConsequences.cs"), houses = R("RealmHouses.cs"), contracts = R("RealmContracts.cs");
            Ok(crown.Contains("\" prevailed and holds the crown.\"") && crown.Contains("\"The crown held. House \""), "CrownAndConsequences still writes the rebellion outcome texts");
            Ok(houses.Contains("\" and House \" + house.Name + \" sign a treaty\"") && houses.Contains("\" breaks its treaty with House \"")
                && houses.Contains("\" renounces its oath to House \""), "RealmHouses still writes the treaty and oath texts");
            Ok(contracts.Contains("\" collects the price on \"") && contracts.Contains("\" fills an order for \"") && contracts.Contains("\" is paid by House \""),
                "RealmContracts still writes the fulfilled-contract texts");
        }

        Directory.Delete(Dir, true);
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
