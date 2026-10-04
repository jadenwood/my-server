// Behaviour tests for plugins/RealmArrival.cs, part 2: pledges, the fire, handover, nudges and the route rules,
// timeouts, log-off and resume, the stage API, stones, the provider and respawns.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeHatch.Common;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Players;
using CodeHatch.StarForge.Sleeping;
using CodeHatch.UserInterface.Dialogues;
using Oxide.Core;
using Oxide.Plugins;
using static W;

static partial class Tests
{
    // Walk onto a pledge stone from the avenue at x=11 and stand still.
    static void OnStone(Player p, string house, double seconds)
    {
        var xz = BannerXZ[house];
        At(p, xz[0], xz[1]);
        Advance(seconds);
    }

    static void Pledges()
    {
        Site();
        Houses.AddRange(new[] { "Varrow", "Ashgrove" });
        HouseOf[M1] = "Varrow"; HouseOf[M2] = "Varrow";
        var m1 = Mk(M1, "Varrow Officer", 500, 500);
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Walk(n, 11, 46, 3f);
        Advance(14);
        Clear();
        OnStone(n, "varrow", 2.2);
        Ok(!n.All().Contains("Stand fast"), "fewer than 3 s on the stone: nothing yet");
        Advance(1.5);
        Ok(n.All().Contains("[E8913A]Hearth[FFFFFF]: Stand fast to look to House [C58FC0]Varrow[FFFFFF]. Step off to walk on."), "3 s on the stone: PledgeDwell (amber)", n.All());
        var pop = n.Popups.LastOrDefault();
        Ok(pop != null && pop.Kind == "confirm" && pop.Title == "Look to House Varrow?" && pop.Confirm == "Look to Varrow" && pop.Cancel == "Walk on" && pop.Broadcast,
            "the pledge window: 'Look to House Varrow?', 'Look to Varrow' / 'Walk on', broadcast true");
        Ok(pop != null && pop.Message.Contains("This is not an oath. Only they can invite you."), "the window says it is not an oath");
        Advance(3.2);
        Ok(n.All().Contains("[8FC97A]Hearth[FFFFFF]: Word goes to House [C58FC0]Varrow[FFFFFF]. If they want you, an officer will invite you; then say [F4C96D]/house join[FFFFFF] [C58FC0]Varrow[FFFFFF]."),
            "3 s more: the pledge by standing (PledgeSent, green)", n.All());
        Ok(m1.All().Contains("[D6A043]Hearth[FFFFFF]: Ada, one of the Unwritten, looks to your banner on the Gatehouse road. [F4C96D]/house invite[FFFFFF] Ada brings them in."),
            "the house's online members are told, with /house invite", m1.All());
        Ok(Quests.Contains(N1 + "|custom|arrival_pledge|1"), "ReportQuestEvent arrival_pledge");
        Ok((int)RecF(n, "Pledges") == 1 && (string)RecF(n, "PledgeHouse") == "varrow", "the pledge is recorded");
        Ok(HouseOf[N1 > 0 ? M1 : M1] == "Varrow" && !HouseOf.ContainsKey(N1), "nothing joins a house automatically");
        Advance(10);
        Ok(n.All().Split('\n').Count(l => l.Contains("Word goes to House")) == 1, "standing on: no second pledge to the same house");
        // A late window answer after the dwell pledge counts for nothing more.
        Answer(pop, Options.Yes);
        Ok((int)RecF(n, "Pledges") == 1, "a late window answer after the dwell pledge changes nothing");
        // A change of mind: the second pledge, by the window.
        At(n, 11, 46); Advance(1);
        Clear();
        OnStone(n, "ashgrove", 4.5);
        Ok(n.All().Contains("Stand fast to look to House"), "the second stone asks again", n.All() + "\n" + Pos(n) + " " + Stage(n) + " run=" + ((System.Collections.IDictionary)F(A, "runs")).Contains(N1) + " " + string.Join(";", A.Logged));
        var pop2 = n.Popups.LastOrDefault();
        Answer(pop2, Options.Yes);
        Ok(n.All().Contains("[E8913A]Hearth[FFFFFF]: No one of House [E08A5C]Ashgrove[FFFFFF] is awake in the realm. Leave a letter: [F4C96D]/raven[FFFFFF] [E08A5C]Ashgrove[FFFFFF] <letter>."),
            "the window's Yes pledges; nobody awake: PledgeNoneAwake with /raven", n.All());
        Ok((int)RecF(n, "Pledges") == 2, "two pledges: one first choice and one change");
        Ok(n.Popups.Count == 1, "the second pledge window: at most two popups per arrival (the card was skipped)", n.Popups.Count.ToString());
        Advance(5);
        Ok(n.All().Split('\n').Count(l => l.Contains("No one of House")) == 1, "the dwell does not pledge again after the window did");
        At(n, 11, 74); Advance(1);
        Clear();
        OnStone(n, "corvane", 4);
        Ok(n.All().Contains("You have looked to a banner already. You may change your mind once."), "a third pledge: PledgeLimit", n.All());
        Ok((int)RecF(n, "Pledges") == 2, "and nothing is sent");
        // Stepping off cancels the dwell.
        Site();
        var s = ThroughGate(N2, "Bea");
        Advance(9);
        Walk(s, 11, 46, 3f);
        Advance(14);
        Clear();
        OnStone(s, "varrow", 4);
        At(s, 11, 46); Advance(4);
        Ok(s.All().Contains("You step off the stone and walk on.") && (int)RecF(s, "Pledges") == 0, "stepping off cancels the dwell (no pledge)", s.All());
        Answer(s.Popups.Last(), Options.No);
        Ok((int)RecF(s, "Pledges") == 0, "'Walk on' pledges nothing");
        // Six per house per hour.
        Site();
        int busy = 0;
        for (int i = 0; i < 7; i++)
        {
            var w = ThroughGate(N3 + (ulong)i * 1000, "Walker" + i);
            Advance(9);
            w.Messages.Clear();
            OnStone(w, "varrow", 7);
            if (w.All().Contains("has heard from many of the Unwritten this hour")) busy++;
            Inv(A, "OnPlayerDisconnected", w); Server.ClientPlayers.Remove(w);
        }
        Ok(busy == 1 && Counter("pledges") == 6, "PledgesPerHousePerHour: the seventh pledge to one house in an hour is refused", busy + " / " + Counter("pledges"));
        Clock = Clock.AddHours(1);
        var late = ThroughGate(N5, "Late");
        Advance(9);
        OnStone(late, "varrow", 7);
        Ok((int)RecF(late, "Pledges") == 1, "an hour later the house hears again");
        // Popups off: the dwell is the only way.
        Site();
        PopupsOff.Add(N4.ToString());
        var o = ThroughGate(N4, "Dag");
        Advance(9);
        OnStone(o, "varrow", 7);
        Ok(o.Popups.Count == 0 && (int)RecF(o, "Pledges") == 1, "popups off (RealmHerald.PopupsWanted): no window, the dwell pledges");
    }

    static void Fire()
    {
        Site();
        Protected.Add(N1);
        Story = "Act 1: The Empty Seat - Ash at the Hearth";
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Walk(n, 11, 46, 3f); Advance(16);
        Walk(n, 11, 74, 3f); Advance(16);
        Walk(n, 11, 103, 3f); Advance(16);
        Walk(n, 11, 116, 3f);
        Advance(20);
        Clear();
        // Into the Hearth stone's 12 m: the lines start QuietAfterHearthSeconds later.
        At(n, 11, 126);
        Advance(5.2);
        Ok(!n.All().Contains("You warm your hands"), "the fire lines wait 6 s after the 12 m ring (RealmTravel's discovery first)", n.All());
        Advance(2);
        Ok(n.All().Contains("[D6A043]Hearth[FFFFFF]: You warm your hands at the fire that has not gone out in a hundred winters."), "Warm (gold)", n.All());
        Ok(n.Heals.SequenceEqual(new[] { "heal", "nourish", "hydrate" }), "HealAtHearth: Heal, Nourish and Hydrate once");
        Advance(17);
        string all = n.All();
        int iw = Index(n, "You warm your hands"), ik = Index(n, "[F4C96D]/kit starter[FFFFFF]"), ic = Index(n, "[F4C96D]/crown[FFFFFF]"),
            ish = Index(n, "For your first hour no other player can wound or bind you."), inx = Index(n, "Your tale has begun at the Hearth.");
        Ok(iw >= 0 && iw < ik && ik < ic && ic < ish && ish < inx, "the fire lines in order: Warm, Kit, Crown, Shelter, Next", all);
        Ok(n.Messages[ish].StartsWith("[E8913A]") && n.Messages[inx].StartsWith("[E8913A]"), "Shelter and Next are amber");
        Ok(all.Contains("Your first deed waits in [F4C96D]/quest[FFFFFF].") && Stage(n) == "hearth", "Next with a running tale; stage hearth");
        Ok(Quests.Contains(N1 + "|custom|arrival_hearth|1"), "ReportQuestEvent arrival_hearth at the Kit line");
        Ok(Lines(n).Count(l => l.Contains("A banner is never taken")) == 0, "the honest paths are not repeated at the fire (heard after the third banner)");
        // Unprotected and no tale: Unsheltered and NextNoTale.
        Site();
        var u = ThroughGate(N2, "Bea");
        Advance(9);
        WalkToFire(u);
        Advance(30);
        Ok(u.All().Contains("Walk carefully. Out here the blades are real.") && u.All().Contains("Tasks wait on the quest-board."), "Unsheltered when Warden reports no protection; NextNoTale without a tale", u.All());
        // The quiet ring holds the queue for 6 s on entry.
        Site();
        var q = ThroughGate(N3, "Cid");
        Advance(9);
        Walk(q, 11, 85, 3f);
        Advance(20);
        Clear();
        Walk(q, 11, 99, 6f);           // into the 40 m ring (z 90) and to the last pair at once
        var start = Clock;
        int seen = q.Messages.Count;
        while (q.Messages.Count == seen && (Clock - start).TotalSeconds < 20) Advance(0.5);
        Ok((Clock - start).TotalSeconds >= 3.5, "entering the 40 m ring holds the queue (RealmQuests' Hearth lines land first)", (Clock - start).TotalSeconds.ToString());
        // HealAtHearth false.
        Site(false, false, c => Tweak(c, "HealAtHearth", false));
        var h = ThroughGate(N4, "Dag");
        Advance(9);
        WalkToFire(h);
        Advance(30);
        Ok(h.Heals.Count == 0 && h.All().Contains("You warm your hands"), "HealAtHearth false: the Warm line, no heal");
    }

    static Player AtFireDone(ulong id, string name)
    {
        var n = ThroughGate(id, name);
        Advance(9);
        WalkToFire(n);
        Advance(30);
        return n;
    }

    static void Handover()
    {
        Site();
        var n = AtFireDone(N1, "Ada");
        Ok(Stage(n) == "hearth" && Owns(n), "at the fire: stage hearth, still owned");
        Clear();
        Command(n, "quest");
        Ok(n.Messages.Count == 1 && n.Messages[0] == "[8FC97A]Hearth[FFFFFF]: You are written. What the Chronicle says of you next is yours. [F4C96D]/realm path[FFFFFF] keeps your first steps.",
            "/quest: the green Written line", n.All());
        Ok(Stage(n) == "done" && !Owns(n) && ArrivalStage(n) == "done", "done: OwnsArrival false");
        Ok(Deeds.Count == 1 && Deeds[0] == N1 + "|Ada|written|Walked out of the Gatehouse|arrival:" + N1, "RealmRenown.AddDeed(id, name, written, ..., arrival:<id>)", string.Join("\n", Deeds));
        Ok(((List<int>)F(F(Data(), "Stats"), "Timings")).Count == 1, "the timing is kept for /arrival admin status");
        Command(n, "quest");
        Ok(n.Messages.Count == 1 && Deeds.Count == 1, "Written once");
        // 60 s after the Next line.
        Site();
        var m = AtFireDone(N2, "Bea");
        Clear();
        Advance(55);
        Ok(Stage(m) == "done" && m.All().Contains("You are written."), "60 s after the Next line: Written", m.All());
        // More than 70 m from the fire.
        Site();
        var k = AtFireDone(N3, "Cid");
        Clear();
        Walk(k, 90, 130, 8f);
        Ok(Stage(k) == "done" && k.All().Contains("You are written."), "more than 70 m from the fire: Written");
        // /quest before the fire does not hand over.
        Site();
        var j = ThroughGate(N4, "Dag");
        Command(j, "quest");
        Ok(Stage(j) != "done", "/quest before the fire changes nothing");
        // WrittenDeed "" : no deed.
        Site(false, false, c => Tweak(c, "WrittenDeed", ""));
        var d = AtFireDone(N5, "Eli");
        Command(d, "quest");
        Ok(Deeds.Count == 0 && Stage(d) == "done", "WrittenDeed empty: no deed");
    }

    static void Nudges()
    {
        Site();
        var n = ThroughGate(N1, "Ada");
        At(n, 11, 30);
        Advance(20);
        Clear();
        Advance(25);
        Ok(n.All().Contains("[D6A043]Hearth[FFFFFF]: The fire lies 100 m ahead, at the end of the banners."), "30 s still with no progress: the nudge, in metres", n.All());
        Advance(31);
        Ok(Lines(n).Count(l => l.Contains("The fire lies")) == 2, "a second nudge 30 s later");
        Advance(120);
        Ok(Lines(n).Count(l => l.Contains("The fire lies")) == 2, "at most two per stage");
        Site(false, false, c => Tweak(c, "UseCompassWords", true));
        var c2 = ThroughGate(N2, "Bea");
        At(c2, 11, 30);
        Advance(60);
        Ok(c2.All().Contains("The fire lies 100 m to the north."), "UseCompassWords: a compass word (+z north)", c2.All());
        // In the hall, the nudge repeats the call.
        Site(false, false, c => Tweak(c, "AutoOpenSeconds", 300));
        var h = ToNarration(N3, "Cid");
        Advance(45);
        Ok(Lines(h).Count(l => l.Contains("The gate stands open.")) == 2, "in the hall: the nudge repeats the call to the gate", h.All());
    }

    static void Wander()
    {
        Site();
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Clear();
        Walk(n, 60, 60, 6f);
        Ok(Stage(n) != "done", "inside the corridor (avenue +/- 20 m, 70 m round the fire, the hall) nothing happens");
        Walk(n, 110, 60, 6f);
        Ok(Stage(n) == "done" && n.All().Contains("[E8913A]Hearth[FFFFFF]: The Hearth will keep. [F4C96D]/road the-hearth[FFFFFF] leads back.")
            && !n.All().Contains("You are written"), "more than 40 m outside the corridor: Wander and handed over (never pulled back)", n.All());
        Ok(Pos(n).x == 110 && n.Entity.Teleports.Count == 1, "the plugin never pulls anyone back");
    }

    static void Timeout()
    {
        // Outside the hall at 8 minutes: Written.
        Site();
        var n = ThroughGate(N1, "Ada");
        At(n, 11, 40);
        Advance(8 * 60);
        Ok(Stage(n) == "done" && n.All().Contains("You are written.") && Counter("timeout") == 1, "8 minutes from the Finish click, outside the hall: Written");
        // Still in the hall: the gate opens, the stage and its sanctuary stay; Written on leaving.
        Site(true, false, c => Tweak(c, "AutoOpenSeconds", 600));
        var h = ToNarration(N2, "Bea");
        At(h, 5, 15);
        for (int i = 0; i < 8 * 60; i += 20) { At(h, i % 40 == 0 ? 5 : 6, 15); Advance(20); }
        Ok(Stage(h) == "gatehouse" && (bool)RecF(h, "TimedOut"), "8 minutes, still in the hall: the stage stays gatehouse", Stage(h));
        Advance(6);
        Ok(GateSolid() == 0, "and the gate opens", GateSolid().ToString());
        Ok(Hit(null, h) == 0f, "the sanctuary still holds");
        Walk(h, 11, 35, 4f);
        Ok(Stage(h) == "done" && h.All().Contains("You are written."), "written as they leave the hall");
    }

    static void Afk()
    {
        Site(false, false, c => { Tweak(c, "AutoOpenSeconds", 600); Tweak(c, "TimeoutMinutes", 60); });
        var n = ToNarration(N1, "Ada");
        Advance(29 * 60);
        Ok(Stage(n) != "done", "29 minutes without moving in the hall: still there");
        Advance(62);
        Ok(Stage(n) == "done" && Dist(Pos(n), 11, 31) < 0.1 && n.All().Contains("You are written.") && Counter("afk") == 1,
            "AfkReleaseMinutes without a move in the hall: moved to the eject point, written", Pos(n).ToString());
        Ok(Graces.Any(g => g == N1 + "|20"), "with SentinelGrace before the move");
    }

    static void Resume()
    {
        // Logged off in the Gatehouse, woke in the hall.
        Site();
        var n = ToNarration(N1, "Ada");
        Offline(n);
        Ok(Stage(n) == "gatehouse" && (bool)RecF(n, "HasLogout"), "the stage and the logout point are saved");
        Advance(600);
        Online(n);
        FirstSpawn(n, false);
        Clear();
        Advance(9);
        Ok(n.Messages.Count == 0, "the resume waits 10 s after the spawn");
        Advance(2);
        Ok(n.All().Contains("[D6A043]Hearth[FFFFFF]: You wake again in the Gatehouse. The gate still waits for you."), "ResumeHall", n.All());
        Advance(21);
        Ok((bool)RecF(n, "GateDone") && n.All().Contains("The gate opens on its own."), "the gate opens 20 s later if they do not walk", n.All());
        // Logged off on the avenue: one line pointing to the next beat.
        Site();
        var m = ThroughGate(N2, "Bea");
        Advance(9);
        Walk(m, 11, 60, 3f);
        Offline(m);
        Online(m);
        FirstSpawn(m, false);
        Clear();
        Advance(11);
        Ok(m.All().Contains("You wake again on the road. The fire is 70 m on.") && Stage(m) == "banners", "on the route: ResumeRoad with the distance; the arrival goes on", m.All());
        Walk(m, 11, 126, 3f);
        Advance(40);
        Ok(m.All().Contains("You warm your hands"), "the rest fire on their triggers");
        // Far away: released with one summary line.
        Site();
        var f = ThroughGate(N3, "Cid");
        Advance(9);
        At(f, 300, 300);
        Offline(f);
        Online(f);
        FirstSpawn(f, false);
        Clear();
        Advance(11);
        Ok(Stage(f) == "done" && f.All().Contains("The Hearth will keep. [F4C96D]/road the-hearth[FFFFFF] leads back."), "far from the route: released with one line", f.All());
        // Not back within 48 h: done silently; a stale wake-up in the hall is let out, not evicted.
        Site();
        var s = ToNarration(N4, "Dag");
        Offline(s);
        Clock = Clock.AddHours(49);
        Online(s);
        Ok(Stage(s) == "done", "not back within StaleHours: marked done silently");
        FirstSpawn(s, false);
        Clear();
        Advance(3);
        Ok(Dist(Pos(s), 11, 31) < 0.1 && s.All().Contains("You wake by the Gatehouse. The road to the Hearth runs ahead.") && Counter("evictions") == 0,
            "a stale wake-up in the hall: out to the forecourt with ReleasedGate, never counted as an eviction", s.All());
        // Pending or crossing (quit before or during creation): the Crossing replays.
        Site();
        var c = Mk(N5, "Eli");
        FirstSpawn(c, true);
        Offline(c);
        Online(c);
        Ok(Stage(c) == "crossing", "quit during creation: still crossing");
        FirstSpawn(c, true);
        Finish(c);
        Ok(Stage(c) == "gatehouse", "and the arrival starts when OnPlayerSpawned finally fires");
    }

    static void StageApi()
    {
        Site();
        var a = ToNarration(N1, "Ada");
        var adm = Mk(1, "Steward", 400, 400);
        Admin(adm);
        Ok(Cmd(adm, "admin", "close").Contains("closed"), "admin close");
        Ok(ArrivalStage(new Player(N5, "New")) == "none" && !Owns(new Player(N5, "New")), "closed: ArrivalStage none for new ids (Herald behaves as today)");
        Ok(ArrivalStage(a) == "running", "closed: arrivals under way finish");
        Cmd(adm, "admin", "open");
        Ok(ArrivalStage(new Player(N5, "New")) == "pending", "open again: pending");
        Cmd(adm, "admin", "pause");
        Ok(ArrivalStage(a) == "none" && Stage(a) == "none" && !Owns(a), "pause: every active arrival becomes none (Herald's normal welcome)");
        Ok(ArrivalStage(new Player(N5, "New")) == "none", "pause: new ids none");
        var b = Mk(N2, "Bea");
        FirstSpawn(b, true);
        Finish(b);
        Ok(Pos(b).x == RandomSpawn.x && Stage(b) == "none", "paused: vanilla spawns");
        Cmd(adm, "admin", "resume");
        Ok(ArrivalStage(new Player(N5, "New")) == "pending", "resume");
        Ok((string)Inv(A, "ArrivalStage", (string)null) == "none" && (string)Inv(A, "ArrivalStage", "not-a-number") == "pending", "a null id is none");
        Set("Enabled", false);
        Ok(ArrivalStage(new Player(N5, "New")) == "none", "Enabled false: none");
        Set("Enabled", true);
        Set("RoutingMode", "off");
        Ok(ArrivalStage(new Player(N5, "New")) == "none", "mode off: none");
    }

    static void Stones()
    {
        Site();
        var used = new List<int>();
        var players = new List<Player>();
        for (int i = 0; i < 6; i++)
        {
            var p = Newcomer(N1 + (ulong)i, "N" + i);
            players.Add(p);
            int idx = Array.FindIndex(StoneXZ, s => Dist(Pos(p), s[0], s[1]) < 0.01f);
            used.Add(idx);
            Advance(1);
        }
        Ok(used.Distinct().Count() == 6 && !used.Contains(-1), "six newcomers: six stones, no jitter while one is free", string.Join(",", used));
        var seventh = Newcomer(N1 + 6, "N6");
        var near = StoneXZ.Select(s => Dist(Pos(seventh), s[0], s[1])).Min();
        Ok(near > 0.01f && near <= 1.21f, "all six taken: a stone takes a second player, with up to 1.2 m of jitter", near.ToString());
        for (int i = 7; i < 12; i++) { Newcomer(N1 + (ulong)i, "N" + i); Advance(0.2); }
        var thirteenth = Newcomer(N1 + 12, "N12");
        Ok(Dist(Pos(thirteenth), 11, 128) < 12 && (bool)RecF(thirteenth, "ToFire") && Counter("overflow") == 1, "beyond twelve: a mercy stone at the Hearth", Pos(thirteenth).ToString());
        Advance(20);
        Ok(thirteenth.All().Contains("the Chronicle has no page for you") && !thirteenth.All().Contains("Stone underfoot"), "the overflow newcomer hears the short lines");
        Advance(30);
        Ok(thirteenth.All().Contains("You warm your hands") && B().Contains("N12 walks out of the Gatehouse"), "then the fire beats, and the Herald line at the fire", thirteenth.All());
        // Sleepers occupy a stone.
        Site();
        var sleeper = new Entity { IsPlayer = false, Position = new UnityEngine.Vector3(5, 10.5f, 5) };
        PlayerSleeperObject.AllSleeperObjects[V1] = sleeper;
        var n = Newcomer(N1, "Ada");
        Ok(Dist(Pos(n), 5, 5) > 1.5f, "a stone with a sleeper on it is taken", Pos(n).ToString());
        // Least recently used first.
        Site();
        var first = Newcomer(N1, "Ada");
        var firstStone = Array.FindIndex(StoneXZ, s => Dist(Pos(first), s[0], s[1]) < 0.01f);
        At(first, 11, 40);
        Advance(10);
        var second = Newcomer(N2, "Bea");
        var secondStone = Array.FindIndex(StoneXZ, s => Dist(Pos(second), s[0], s[1]) < 0.01f);
        Ok(secondStone >= 0 && secondStone != firstStone, "least recently used: the next newcomer gets another stone", firstStone + " / " + secondStone);
    }

    static void Provider()
    {
        Site();
        var adm = Mk(1, "Steward", 400, 400);
        Admin(adm);
        var vanilla = SpawnpointManager.defaultSpawnpointProvider;
        Cmd(adm, "admin", "mode", "provider");
        var wrapped = SpawnpointManager.defaultSpawnpointProvider;
        Ok(wrapped != vanilla && wrapped.GetType().Name == "ArrivalProvider", "mode provider: the spawn provider is wrapped");
        var n = Mk(N1, "Ada");
        FirstSpawn(n, true);
        Clear();
        Finish(n);
        Ok(StoneXZ.Any(s => Dist(Pos(n), s[0], s[1]) < 0.01f) && n.Entity.Teleports.Count == 0, "the game's own teleport went to the stone (no second move)", Pos(n).ToString());
        Ok(Stage(n) == "gatehouse", "the arrival starts");
        var e = new PlayerPreSpawnCompleteEvent { Player = n };
        EventManager.CurrentEvent = e;
        Ok(wrapped.GetRandomSpawnPoint().x == RandomSpawn.x, "the mark is one-shot: a stale CurrentEvent later gets a vanilla point");
        EventManager.CurrentEvent = null;
        Ok(wrapped.GetRandomSpawnPoint().x == RandomSpawn.x, "no current event (MovementStatisticCollector): vanilla");
        var v = Mk(V1, "Old Hand");
        FirstSpawn(v, false);
        EventManager.CurrentEvent = new PlayerPreSpawnCompleteEvent { Player = v };
        Ok(wrapped.GetRandomSpawnPoint().x == RandomSpawn.x, "an unmarked player gets vanilla points");
        EventManager.CurrentEvent = new PlayerRespawnRandomlyEvent { Player = n };
        Ok(wrapped.GetRandomSpawnPoint().x == RandomSpawn.x, "a respawn event gets vanilla points");
        EventManager.CurrentEvent = null;
        Ok(wrapped.GetSpawnPoint(n.Entity).x == RandomSpawn.x && wrapped.GetAllSpawnPoints(n.Entity).Length == 1, "GetSpawnPoint and GetAllSpawnPoints delegate");
        Cmd(adm, "admin", "pause");
        Ok(SpawnpointManager.defaultSpawnpointProvider == vanilla, "pause restores the original provider");
        Cmd(adm, "admin", "resume");
        Ok(SpawnpointManager.defaultSpawnpointProvider.GetType().Name == "ArrivalProvider", "resume wraps it again");
        Cmd(adm, "admin", "mode", "teleport");
        Ok(SpawnpointManager.defaultSpawnpointProvider == vanilla, "mode teleport restores it");
        Cmd(adm, "admin", "mode", "provider");
        Inv(A, "Unload");
        Ok(SpawnpointManager.defaultSpawnpointProvider == vanilla, "Unload restores it");
        // Closed: never installed.
        Site(false, false, c => Tweak(c, "RoutingMode", "provider"));
        var adm2 = Mk(2, "Steward2", 400, 400);
        Admin(adm2);
        Cmd(adm2, "admin", "close");
        Ok(SpawnpointManager.defaultSpawnpointProvider is VanillaProvider, "closed: the original provider stands");
    }

    static void Respawns()
    {
        Site();
        Protected.Add(N1);
        var n = ThroughGate(N1, "Ada");
        Advance(9);
        Walk(n, 11, 60, 3f);
        Clear();
        var bed = Respawn(n, new PlayerRespawnAtBedEvent());
        var bas = Respawn(n, new PlayerRespawnAtBaseEvent());
        Ok(bed.Position.x == RandomSpawn.x && bas.Position.x == RandomSpawn.x && Graces.Count == 0, "bed and base respawns are never touched");
        var e = Respawn(n, new PlayerRespawnNormalEvent());
        Ok(Dist(e.Position, 8, 125) < 0.1 || Dist(e.Position, 14, 122) < 0.1 || Dist(e.Position, 11, 140) < 0.1, "a death during the arrival (Normal): a mercy stone at the Hearth", e.Position.ToString());
        Ok(Math.Abs(e.Position.y - 10.5f) < 0.01f && Graces.Contains(N1 + "|20"), "+0.5 m, with SentinelGrace(id, 20) first");
        Ok(!e.Cancelled && n.Heals.Count == 0, "never Cancel(), no items or healing in the hook");
        Ok((bool)RecF(n, "ToFire") && Stage(n) == "banners" && (int)RecF(n, "Deaths") == 1, "the stage jumps to the fire beats; the death is counted");
        Advance(0.2);
        Ok(n.All().Contains("[E8913A]Hearth[FFFFFF]: The Hearth's smoke led you back. The fire is before you."), "MidDeath a tick later", n.All());
        n.Entity.Position = e.Position;
        Advance(45);
        Ok(n.All().Contains("You warm your hands"), "the fire beats follow");
        Command(n, "quest");
        Ok(Stage(n) == "done", "and the handover");
        // After Written, protected: Hearth's Mercy once (MercyRespawns 1).
        Clear();
        var m = Respawn(n, new PlayerRespawnRandomlyEvent());
        Ok(Dist(m.Position, 11, 128) < 12, "after Written, protected, Randomly: Hearth's Mercy", m.Position.ToString());
        Advance(0.2);
        Ok(n.All().Contains("The Hearth takes you back, once. Raise a bed before you fall again."), "the Mercy line a tick later");
        var m2 = Respawn(n, new PlayerRespawnNormalEvent());
        Ok(m2.Position.x == RandomSpawn.x, "a second death: vanilla (MercyUsed reached MercyRespawns)");
        // Not protected: vanilla.
        Site();
        var u = AtFireDone(N2, "Bea");
        Command(u, "quest");
        var r = Respawn(u, new PlayerRespawnNormalEvent());
        Ok(r.Position.x == RandomSpawn.x, "after Written without Warden protection: vanilla");
        // A death in the Gatehouse goes to the Hearth too, never back into the hall.
        Site();
        var g = ToNarration(N3, "Cid");
        var gr = Respawn(g, new PlayerRespawnRandomlyEvent());
        Ok(Dist(gr.Position, 11, 128) < 12 && (bool)RecF(g, "GateDone"), "a death in the Gatehouse: a mercy stone, never back into the Gatehouse");
        // Veterans and strangers: vanilla.
        var v = Mk(V1, "Old Hand");
        FirstSpawn(v, false);
        Ok(Respawn(v, new PlayerRespawnNormalEvent()).Position.x == RandomSpawn.x, "a veteran's respawn is vanilla");
        // MercyRespawns 3 is the most; 0 switches the mercy off.
        Site(false, false, c => Tweak(c, "MercyRespawns", 9));
        Ok((int)F(Cfg(), "MercyRespawns") == 3, "MercyRespawns is clamped to 3");
    }
}
