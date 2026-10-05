// Behaviour tests for plugins/RealmArena.cs (with the real plugins/RealmTreasury.cs). Run with run.sh (see there for what
// this does and does not prove).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CodeHatch.Blocks.Networking.Events;
using CodeHatch.Common;
using CodeHatch.Damaging;
using CodeHatch.Engine.Behaviours;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;
using static W;

static class Tests
{
    static string Repo;
    static ulong NextId = 76561190000000001;
    static Player P(string name, float x = 0, float z = 0, long marks = 1000, double minutes = 600, string house = null)
    {
        return Mk(NextId++, name, x, z, marks, minutes, house);
    }

    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : "../../../..";
        try
        {
            Startup();
            LangAndStyle();
            ChallengeAndFight();
            Interference();
            RingAndFlight();
            Wagers();
            Ratings();
            Teams();
            Refusals();
            FellingGuard();
            Champion();
            Tournament();
            Royal();
            Trial();
            Dice();
            Cards();
            Teleport();
            ReloadAndData();
            Popups();
            AdminAndExtras();
            ConfigClamp();
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
        Ok(A.permission.Registered.Contains("realmarena.admin"), "registers realmarena.admin");
        Ok(A.timer.Repeating.Count == 2, "a slow tick and a one-second ring tick", A.timer.Repeating.Count.ToString());
        Ok(File.Exists(Path.Combine(Dir, "RealmArena.json")) && File.Exists(Path.Combine(Dir, "RealmArena_lastgood.json")), "the data file and a last-good copy are written");
        Inv(A, "OnServerInitialized");
        Ok(A.timer.Repeating.Count(t => !t.Destroyed) == 2, "OnServerInitialized again (hot load) keeps exactly two live timers");
        var next = (DateTime)D("NextCrowning");
        Ok(next.DayOfWeek == DayOfWeek.Sunday && next.Hour == 20 && next > Clock, "the next crowning is Sunday 20:00 UTC", next.ToString("o"));
        Ok((int)F(Cfg("Ranked"), "StartRating") == 1000 && (bool)F(F(A, "config"), "UsePopups"), "defaults: rating 1000, popups on");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void LangAndStyle()
    {
        Reset();
        var lang = A.lang.Msgs;
        string src = File.ReadAllText(Path.Combine(Repo, "plugins", "RealmArena.cs"));
        var used = new HashSet<string>();
        foreach (Match m in Regex.Matches(src, "\\b(?:Reply|Ok|Warn|Error|Line|Msg|Fmt|NoticeOnce|Refuse|RefuseTavern|TavernError|ErrorFor|TellChallenge|VoidDuel|DropChallenge|CancelTourney)\\((?:[^\"()]|\\([^()]*\\))*?\"([A-Z][A-Za-z0-9.]+)\""))
            used.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(src, "TavernSay\\([^,]+,[^,]+,\\s*\"([A-Z][A-Za-z0-9.]+)\"")) used.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(src, "\\? \"([A-Z][A-Za-z0-9]+)\" : \"([A-Z][A-Za-z0-9]+)\"")) { used.Add(m.Groups[1].Value); used.Add(m.Groups[2].Value); }
        foreach (Match m in Regex.Matches(src, "return (?:self \\? )?\"([A-Z][A-Za-z]+)\"(?: : \"([A-Z][A-Za-z]+)\")?;")) { used.Add(m.Groups[1].Value); if (m.Groups[2].Success) used.Add(m.Groups[2].Value); }
        used.ExceptWith(new[] { "RealmArena", "The", "DuelHelp", "ArenaHelp" });              // DuelHelp1..5, ArenaHelp1..4: checked below
        used.RemoveWhere(k => k.EndsWith("."));                                                // composed keys ("Out." + how): checked below
        for (int i = 1; i <= 5; i++) used.Add("DuelHelp" + i);
        for (int i = 1; i <= 4; i++) used.Add("ArenaHelp" + i);
        var missing = used.Where(k => !lang.ContainsKey(k)).ToList();
        Ok(used.Count > 150 && missing.Count == 0, "every lang key the code names exists (" + used.Count + " found)", string.Join(", ", missing));
        foreach (string how in new[] { "felled", "fell", "yielded", "fled", "died", "decision", "noshow", "time", "draw", "bye" })
            if (!lang.ContainsKey("How." + how)) { Ok(false, "How." + how + " exists"); }
        foreach (string how in new[] { "felled", "fell", "yielded", "fled", "died" })
            if (!lang.ContainsKey("Out." + how)) { Ok(false, "Out." + how + " exists"); }
        Ok(new[] { "State.gather", "State.countdown", "State.fight", "Game.duel", "Game.dice", "Game.cards", "Confirm.duel", "Confirm.dice", "Confirm.cards" }.All(lang.ContainsKey),
            "the composed keys (state, game, confirm) exist");
        var tooLong = lang.Where(kv => Regex.Replace(kv.Value, "\\[[0-9A-Fa-f]{6}\\]", "").Length > 200).Select(kv => kv.Key).ToList();
        Ok(tooLong.Count == 0, "no lang line is longer than 200 visible characters", string.Join(", ", tooLong));
        var badColour = lang.Where(kv => Regex.Matches(kv.Value, "\\[([0-9A-Fa-f]{6})\\]").Cast<Match>().Any(m => !new[] { "D6A043", "8FC97A", "E8913A", "E86A5C", "F4C96D", "A3A6AD", "FFFFFF" }.Contains(m.Groups[1].Value))).Select(kv => kv.Key).ToList();
        Ok(badColour.Count == 0, "every colour is in the chat palette", string.Join(", ", badColour));
        var types = Regex.Matches(src, "Chronicle\\(\"([a-z_]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        Ok(types.Count > 0 && types.All(t => t == "title_earned" || t == "event_started" || t == "event_ended"),
            "Chronicle lines use registered types only, and never tournament_champion (RealmRenown would count it as the Royal Tournament)", string.Join(",", types));
    }

    // ------------------------------------------------------------------------------------------------------------
    static void ChallengeAndFight()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0); var cy = P("Cy", 60, 0);
        Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("You challenge Bram to a duel") && ada.All().Contains("Ranked"), "the challenger is told, with the ranked note", ada.All());
        Ok(bram.All().Contains("Ada challenges you to a duel to the first fall!") && bram.All().Contains("/duel accept[FFFFFF] Ada"), "the challenged is told how to answer", bram.All());
        Ok(bram.Popups.Count == 1 && bram.Popups[0].Broadcast && bram.Popups[0].Yes == "Accept" && bram.Popups[0].Message.Contains("Ada challenges you"),
            "the challenged gets a confirm window (broadcast = true)");
        Ok(L("Challenges").Count == 1 && Duel(ada) == null, "the challenge waits; no duel yet");
        bram.Popups[0].Answer(true);
        Ok(Duel(ada) != null && Duel(ada) == Duel(bram) && L("Challenges").Count == 0, "Yes in the window starts the duel");
        Ok(State(ada) == "countdown", "standing together, the ring is drawn at once and the count begins", State(ada));
        Ok(ada.All().Contains("The ring is drawn here, 15 m wide"), "the ring is announced", ada.All());
        string before = Hit(ada, bram, 10, HumanBodyBones.Chest);
        Ok(before == "turned" && bram.Health.CurrentHealth == 150, "no blow lands before the herald's word");
        Clear();
        Tick(5);
        Ok(State(ada) == "fight" && ada.All().Contains("Fight!") && ada.All().Contains("3..."), "the count runs down to Fight!", ada.All());
        Ok(Hit(ada, bram, 10, HumanBodyBones.Chest) == "applied" && bram.Health.TorsoHealth.CurrentHealth == 60, "an ordinary blow lands");
        Ok(Hit(bram, ada, 5, HumanBodyBones.LeftUpperLeg) == "applied", "and an answer");
        Tick(20);
        Clear();
        string felling = Hit(ada, bram, 25, HumanBodyBones.Head);
        Ok(felling == "turned" && !bram.Health.Dead && bram.Health.HeadHealth.CurrentHealth == 30, "a blow that would kill is turned aside: no death", felling);
        Ok(Duel(ada) == null && Duel(bram) == null, "the duel is over");
        Ok(ada.All().Contains("Victory over Bram") && bram.All().Contains("Ada has the better of you"), "winner and loser are told", ada.All() + "\n" + bram.All());
        Ok(bram.All().Contains("Bram is felled by Ada!"), "the felling is called", bram.All());
        Ok(ada.Heals.Count == 1 && bram.Heals.Count == 1 && Math.Abs(bram.Heals[0] - 75f) < 0.01f, "both are tended afterwards (half their health)", string.Join(",", bram.Heals));
        Ok(Rating(ada) == 1020 && Rating(bram) == 980, "a ranked result: +20 / -20 for two provisional fighters", Rating(ada) + "/" + Rating(bram));
        Ok(ada.All().Contains("Rating +20, now 1020") && bram.All().Contains("Rating -20, now 980"), "each sees the rating change");
        Ok(FInt(ada, "Wins") == 1 && FInt(bram, "Losses") == 1 && FInt(ada, "Games") == 1 && FInt(ada, "WeekGames") == 1, "the record counts it");
        Ok(Deeds.Count == 1 && Deeds[0].Contains("|duel_won|") && Deeds[0].EndsWith("|"), "RealmRenown hears of the ranked win (its own cooldown, no dedupe key)", string.Join("\n", Deeds));
        Ok(L("History").Count == 1, "the duel is in the history");
        // The shield after the duel.
        Ok(Hit(cy, bram, 10) == "turned" && cy.All().Contains("shield"), "an onlooker cannot strike the loser while the shield holds", cy.All());
        Ok(Hit(bram, cy, 10) == "turned", "nor the loser an onlooker");
        Tick(16);
        Ok(Hit(cy, bram, 10) == "applied", "after the shield, the world is the world again");
        Ok(ZeroSum() == "", "the treasury balances", ZeroSum());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Interference()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0); var cy = P("Cy", 8, 0);
        Fight(ada, bram);
        Ok(State(ada) == "fight", "a fight is on");
        Clear();
        Ok(Hit(cy, ada, 10) == "turned" && cy.All().Contains("Ada is fighting a duel"), "an outsider cannot strike a duellist", cy.All());
        Ok(Hit(ada, cy, 10) == "turned" && ada.All().Contains("strike only your foe"), "a duellist cannot strike an outsider", ada.All());
        var cap = new PlayerCaptureEvent { Captor = cy.Entity, Target = ada };
        Inv(A, "OnPlayerCapture", cap);
        Ok(cap.Cancelled, "no ropes on a duellist");
        var cap2 = new PlayerCaptureEvent { Captor = ada.Entity, Target = cy };
        Inv(A, "OnPlayerCapture", cap2);
        Ok(cap2.Cancelled, "nor by one");
        var cube = new CubePlaceEvent { SenderId = cy.Id, Grid = new Grid { World = new Vector3(3, 0, 1) } };
        Inv(A, "OnCubePlacement", cube);
        Ok(cube.Cancelled, "no building inside a drawn ring");
        var cube2 = new CubePlaceEvent { SenderId = cy.Id, Grid = new Grid { World = new Vector3(80, 0, 80) } };
        Inv(A, "OnCubePlacement", cube2);
        Ok(!cube2.Cancelled, "building elsewhere is untouched");
        // A second duel still gathering (far apart): its fighters are in the world, but not against a drawn ring.
        var eve = P("Eve", 200, 0); var fay = P("Fay", 600, 0);
        Challenge(eve, fay); Accept(fay, eve);
        Ok(State(eve) == "gather", "a second duel is gathering", State(eve));
        Clear();
        Ok(Hit(ada, eve, 10) == "turned", "a duellist in the ring cannot strike someone gathering for another duel");
        Ok(Hit(eve, ada, 10) == "turned", "nor be struck by one");
        Ok(Hit(eve, cy, 5) == "applied", "between a gathering fighter and the world, the world is the world");
        Cmd(eve, "duel", "yield");
        Ok(Duel(eve) == null && Duel(ada) != null, "the gathering duel is set aside", State(eve));
        Ok((bool)Inv(A, "IsDuelBlow", ada.Id.ToString(), bram.Id.ToString()) && !(bool)Inv(A, "IsDuelBlow", cy.Id.ToString(), ada.Id.ToString()),
            "IsDuelBlow (for RealmLaws' peace): foes in a fight yes, an outsider no");
        Ok((bool)Inv(A, "IsInDuel", ada.Id.ToString()) && !(bool)Inv(A, "IsInDuel", cy.Id.ToString()), "IsInDuel");
        // Blows outside any duel tag both as fighting (no escape into a duel).
        Ok(Hit(cy, P("Dee", 100, 100), 5) == "applied", "a fight outside the ring is untouched");
        Ok(Fall(ada, 20) == "applied", "a fall that does not kill hurts a duellist as usual");
        Clear();
        Ok(Fall(ada, 500) == "turned" && !ada.Health.Dead && Duel(ada) == null, "a fall that would kill fells the duellist instead");
        Ok(bram.All().Contains("Ada falls and is out of the fight!") && bram.All().Contains("Victory over Ada"), "and the foe wins", bram.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void RingAndFlight()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0);
        Fight(ada, bram);
        Clear();
        Move(bram, 40, 0);
        Tick(3);
        Ok(bram.All().Contains("Back into the ring!") && Duel(bram) != null, "leaving the ring warns", bram.All());
        Move(bram, 3, 0);
        Tick(10);
        Ok(Duel(bram) != null, "coming back in time keeps the fight");
        Move(bram, 40, 0);
        Tick(9);
        Ok(Duel(bram) == null && ada.All().Contains("Bram fled the ring and forfeits!"), "outside the ring too long is fleeing", ada.All());
        Ok(((IList)F(Fighter(bram), "Flees")).Count == 1 && Rating(ada) > 1000, "fleeing counts as a flight and a loss");

        // Logging off mid-fight forfeits; before the fight it voids.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram, 50);
        Offline(bram);
        Ok(Duel(ada) == null && Purse(ada) == 1050 && Purse(bram) == 950, "logging off in the fight forfeits the stake");
        Ok(ZeroSum() == "", "balanced", ZeroSum());
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 400, 0);
        Challenge(ada, bram, 50); Accept(bram, ada, 50);
        Ok(State(ada) == "gather", "far apart, the duellists must meet first", State(ada));
        Offline(bram);
        Ok(Duel(ada) == null && Purse(ada) == 1000 && Purse(bram) == 1000 && ((IList)F(Fighter(bram), "Flees")).Count == 0,
            "leaving before the fight voids it: stakes back, no flight counted");
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Challenge(ada, bram); Accept(bram, ada);
        Ok(State(ada) == "countdown", "counting");
        Offline(bram);
        Ok(Duel(ada) == null && ((IList)F(Fighter(bram), "Flees")).Count == 1, "leaving in the count voids it but counts as a flight");
        // Three flights in a day bar the player.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        for (int i = 0; i < 3; i++) { Fight(ada, bram); Move(bram, 50, 0); Tick(9); Move(bram, 4, 0); Tick(20); }
        Ok((DateTime)F(Fighter(bram), "BarredUntil") > Clock, "fleeing three times in a day bars the player from the ring");
        Clear();
        Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("Bram is barred from the ring"), "a barred player cannot be challenged", ada.All());
        // Gathering that never meets; yield before the fight.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 400, 0);
        Challenge(ada, bram, 20); Accept(bram, ada, 20);
        Tick(91);
        Ok(Duel(ada) == null && ada.All().Contains("You did not meet in time") && Purse(ada) == 1000, "no meeting in time: void, stakes back");
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 400, 0);
        Challenge(ada, bram); Accept(bram, ada);
        Cmd(bram, "duel", "yield");
        Ok(Duel(ada) == null && ada.All().Contains("Bram withdraws before the fight"), "a yield while gathering withdraws without a loss");
        // Time runs out: a draw.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram, 30);
        Tick(301);
        Ok(Duel(ada) == null && ada.All().Contains("drawn") && Purse(ada) == 1000 && Rating(ada) == 1000 && FInt(ada, "Draws") == 1, "time runs out: a draw, stakes back, no rating change");
        Ok(ZeroSum() == "", "balanced", ZeroSum());
    }

    // ------------------------------------------------------------------------------------------------------------
    static long FLong(Player p, string field) { return (long)F(Fighter(p), field); }
    static IList Journal() { return (IList)F(F(T, "data"), "Journal"); }
    static void Rng(params int[] values)
    {
        var q = new Queue<int>(values);
        SetF(A, "rng", (Func<int, int>)(n => q.Count > 0 ? q.Dequeue() % n : 0));
    }
    // A ranked fight the winner wins: the loser strikes once (so it is a contest), time passes, the winner fells them.
    static void Win(Player w, Player l, long wager = 0)
    {
        Fight(w, l, wager);
        Hit(l, w, 2, HumanBodyBones.LeftUpperLeg);
        Tick(16);
        Hit(w, l, 500, HumanBodyBones.Chest);
        Heal(w); Heal(l);
        Tick(31);                                                            // past the shield and every cooldown
    }
    static string Iso(DateTime t) { return t.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'"); }

    // ------------------------------------------------------------------------------------------------------------
    static void Wagers()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0);
        Cmd(ada, "duel", "Bram", "100");
        Ok(Purse(ada) == 900 && Held() == 100 && Holds() == 1, "the challenger's stake is held by the treasury at once", Purse(ada) + " " + Held());
        Ok(bram.Popups.Count == 1 && bram.Popups[0].Message.Contains("Stake: 100 marks each"), "the window shows the stake");
        Clear();
        Cmd(bram, "duel", "accept", "Ada");
        Ok(bram.All().Contains("That challenge is for 100 marks each") && Duel(bram) == null, "accepting in chat needs the stake named", bram.All());
        Cmd(bram, "duel", "accept", "Ada", "10");
        Ok(Duel(bram) == null && Purse(bram) == 1000, "a wrong stake is refused and nothing is taken");
        Cmd(bram, "duel", "accept", "Ada", "100");
        Ok(Duel(bram) != null && Purse(bram) == 900 && Held() == 200, "the right stake accepts, and the second stake is held");
        Tick(6);
        Hit(bram, ada, 5);
        Tick(16);
        Clear();
        Hit(ada, bram, 500);
        Ok(Purse(ada) == 1100 && Purse(bram) == 900 && Held() == 0 && Holds() == 0, "the winner takes the whole pot: no fee", Purse(ada) + "/" + Purse(bram));
        Ok(ada.All().Contains("200 marks are yours") && bram.All().Contains("your stake of 100 marks goes to the winners"), "both are told", ada.All() + bram.All());
        Ok(ZeroSum() == "", "every mark is in a purse or a hold, and the treasury's audit agrees", ZeroSum());
        var kinds = Journal().Cast<object>().Select(e => (string)F(e, "Kind")).ToList();
        Ok(kinds.Count(k => k == "hold") == 2 && kinds.Count(k => k == "hold-pay") == 2, "the treasury journal shows both holds and both payouts", string.Join(",", kinds));
        Ok(FLong(ada, "MarksWon") == 100 && FLong(bram, "MarksLost") == 100, "the record keeps marks won and lost");

        // Decline, cancel, lapse: every stake back.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Cmd(ada, "duel", "Bram", "50");
        Clear();
        Cmd(bram, "duel", "decline");
        Ok(Purse(ada) == 1000 && Holds() == 0 && ada.All().Contains("Bram declines"), "a decline returns the stake", ada.All());
        Clear();
        Tick(16);
        Cmd(ada, "duel", "Bram", "50");
        Ok(ada.All().Contains("You challenged Bram a moment ago") && Holds() == 0, "the same pair cannot be challenged again at once", ada.All());
        Tick(61);
        Cmd(ada, "duel", "Bram", "50");
        Cmd(ada, "duel", "cancel");
        Ok(Purse(ada) == 1000 && Holds() == 0, "a cancel returns the stake");
        Tick(61);
        Cmd(ada, "duel", "Bram", "50");
        Clear();
        Tick(70);
        Ok(Purse(ada) == 1000 && Holds() == 0 && L("Challenges").Count == 0 && ada.All().Contains("lapsed"), "an unanswered challenge lapses and the stake returns", ada.All());
        Ok(ZeroSum() == "", "balanced", ZeroSum());

        // Bounds and limits.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0); var poor = P("Poor", 6, 0, 10); var newbie = P("Newbie", 8, 0, 1000, 10);
        Cmd(ada, "duel", "Bram", "2");
        Ok(ada.All().Contains("between 5 and 500 marks"), "a stake below the minimum is refused", ada.All());
        Clear(); Cmd(ada, "duel", "Bram", "501");
        Ok(ada.All().Contains("between 5 and 500 marks") && Holds() == 0, "above the maximum too");
        Cmd(poor, "duel", "Bram", "50");
        Ok(poor.All().Contains("You do not have that many marks"), "a short purse is refused", poor.All());
        Cmd(newbie, "duel", "Bram", "50");
        Ok(newbie.All().Contains("60 minutes"), "a newcomer cannot stake marks yet", newbie.All());
        Tune("Wagers", "DailyLimit", 150L);
        Win(ada, bram, 100);
        Clear();
        Cmd(ada, "duel", "Bram", "100");
        Ok(ada.All().Contains("today's limit of 150 marks") && Holds() == 0, "the daily stake limit holds", ada.All());
        Tune("Wagers", "DailyLimit", 2000L);
        Tune("Wagers", "PairDailyLimit", 120L);
        Clear();
        Cmd(ada, "duel", "Bram", "50");
        Ok(ada.All().Contains("staked 120 marks on each other today"), "the per-pair limit holds", ada.All());
        Ok(ZeroSum() == "", "balanced", ZeroSum());

        // No treasury: stakes are refused, honour duels go on.
        Reset();
        NewArena(null, false);
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Cmd(ada, "duel", "Bram", "50");
        Ok(ada.All().Contains("The treasury is closed") && L("Challenges").Count == 0, "without RealmTreasury no stake is taken", ada.All());
        Cmd(ada, "duel", "Bram");
        Ok(L("Challenges").Count == 1, "a duel without a stake still works");

        // The treasury goes away before the payout: written down, paid when it is back, never twice.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram, 100);
        Hit(bram, ada, 5);
        Tick(16);
        SetF(A, "RealmTreasury", null);
        Hit(ada, bram, 500);
        Ok(Pending() == 1 && Purse(ada) == 900 && Held() == 200, "with the treasury gone, the payout waits in the settlements");
        SetF(A, "RealmTreasury", T);
        Slow();
        Ok(Pending() == 0 && Purse(ada) == 1100 && Held() == 0, "back, it is paid on the next tick");
        Inv(A, "ProcessSettlements");
        Ok(Purse(ada) == 1100 && ZeroSum() == "", "and never paid twice", ZeroSum());

        // The treasury's own escrow rules.
        long h1 = (long)T.Call("HoldMarks", "test:1", bram.Id.ToString(), "Bram", 50L, "Other", 60);
        Ok(h1 == 50 && Purse(bram) == 850, "HoldMarks takes the stake");
        Ok((long)T.Call("HoldMarks", "test:1", bram.Id.ToString(), "Bram", 50L, "Other", 60) == 0, "the same hold id twice is refused");
        Ok((long)T.Call("HoldMarks", "test:2", bram.Id.ToString(), "Bram", 99999L, "Other", 60) == 0 && Purse(bram) == 850, "a short purse holds nothing");
        Ok((long)T.Call("PayFromHold", "test:1", ada.Id.ToString(), "Ada", 50L, "Thief") == 0, "only the source that made a hold may pay it out");
        Ok((long)T.Call("ReleaseHold", "test:1", "Thief") == 0 && Held() == 50, "or release it");
        var hold = ((IDictionary)F(F(T, "data"), "Holds"))["test:1"];
        SetF(hold, "Expires", DateTime.UtcNow.AddMinutes(-1));
        Inv(T, "Tick");
        Ok(Purse(bram) == 900 && Held() == 0, "a hold left open past its time goes back to its owner by itself");
        Ok(ZeroSum() == "", "balanced", ZeroSum());

        // Bait and switch: an old answer cannot accept a dearer challenge.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Cmd(ada, "duel", "Bram", "10");
        var cheap = bram.Popups.Last();
        Cmd(ada, "duel", "cancel");
        Tick(61);
        Cmd(ada, "duel", "Bram", "500");
        var dear = bram.Popups.Last();
        Ok(L("Challenges").Count == 1, "a new, dearer challenge");
        cheap.Answer(true);
        Ok(Duel(bram) == null && Purse(bram) == 1000, "answering the old window does not accept the new challenge");
        Clear();
        Cmd(bram, "duel", "accept", "Ada", "10");
        Ok(Duel(bram) == null && bram.All().Contains("That challenge is for 500 marks"), "nor does the old amount in chat", bram.All());
        dear.Answer(true);
        Ok(Duel(bram) != null && Purse(bram) == 500, "the window that showed 500 does accept it");
        dear.Answer(true);
        Ok(Purse(bram) == 500 && Holds() == 2, "an answer is used once");
        Inv(A, "Unload");
        Ok(Purse(bram) == 1000 && Purse(ada) == 1000 && Holds() == 0 && ZeroSum() == "", "a reload voids the duel and returns both stakes", ZeroSum());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Ratings()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0);
        Win(ada, bram);
        Ok(Rating(ada) == 1020 && Rating(bram) == 980, "first meeting: +20 / -20");
        Win(ada, bram);
        Ok(Rating(ada) == 1029 && Rating(bram) == 971, "a repeat in the week is worth half (and the favourite gains less)", Rating(ada) + "/" + Rating(bram));
        Clear();
        Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("you have met often enough today"), "the third meeting in a day is announced as friendly", ada.All());
        Accept(bram, ada); Tick(6); Hit(bram, ada, 2); Tick(16); Hit(ada, bram, 500); Tick(31);
        Ok(Rating(ada) == 1029 && FInt(ada, "Friendly") == 1 && FInt(ada, "Games") == 2, "and does not move the ladder");

        // No contest: a quick fall where the loser never struck.
        var cy = P("Cy", 0, 10); var dee = P("Dee", 4, 10);
        Fight(cy, dee);
        Clear();
        Hit(cy, dee, 500);
        Ok(Rating(cy) == 1000 && Rating(dee) == 1000 && cy.All().Contains("No contest") && FInt(cy, "Friendly") == 1, "a quick fall without a blow back is no contest", cy.All());

        // Farming newcomers: an established fighter beating a provisional one gains a quarter.
        Reset();
        var vet = P("Vet", 0, 0); var fresh = P("Fresh", 4, 0);
        SetF(Fighter(vet), "Games", 12); SetF(Fighter(vet), "Rating", 1200);
        Win(vet, fresh);
        Ok(Rating(vet) == 1201 && Rating(fresh) == 990, "beating a provisional fighter is worth a quarter (1, not 6); the newcomer loses 10", Rating(vet) + "/" + Rating(fresh));
        Tune("Ranked", "MaxGainPerDay", 25);
        var g1 = P("Gil", 0, 20); var g2 = P("Hal", 4, 20);
        Win(g1, g2);
        Ok(Rating(g1) == 1020, "an ordinary win");
        var g3 = P("Ivo", 0, 30);
        Move(g1, 2, 30);
        Win(g1, g3);
        Ok(Rating(g1) == 1025, "the daily gain cap stops a farmer at +25", Rating(g1).ToString());

        // Who is never ranked.
        Reset();
        var fen = P("Fen", 0, 0, 1000, 600, "Varrow"); var gil = P("Gil", 4, 0, 1000, 600, "Varrow");
        Cmd(fen, "duel", "Gil");
        Ok(fen.All().Contains("housemates"), "housemates fight friendly bouts", fen.All());
        var ash = P("Ash", 0, 10, 1000, 600, "Ashgrove"); var mer = P("Mer", 4, 10, 1000, 600, "Merrin");
        Liege["Ashgrove"] = "Merrin";
        Cmd(ash, "duel", "Mer");
        Ok(ash.All().Contains("allied houses"), "a liege and vassal fight friendly bouts", ash.All());
        var ivy = P("Ivy", 0, 20, 1000, 10); var jon = P("Jon", 4, 20);
        Cmd(jon, "duel", "Ivy");
        Ok(jon.All().Contains("too new to the realm"), "a newcomer's duels are not ranked", jon.All());
        var kai = P("Kai", 0, 30); var lia = P("Lia", 4, 30);
        Bearer = kai.Id.ToString();
        Cmd(lia, "duel", "Kai");
        Ok(lia.All().Contains("the Ironbreaker is in the ring"), "the Ironbreaker's bearer fights unranked", lia.All());
        Bearer = null;

        // Win-trading: a pair that meets too often is reported to the Warden.
        Reset();
        Tune("Ranked", "PairPerDay", 100); Tune("Ranked", "PairPerWeek", 100);
        var alt1 = P("Mona", 0, 0); var alt2 = P("Nils", 4, 0);
        for (int i = 0; i < 6; i++) { if (i % 2 == 0) Win(alt1, alt2); else Win(alt2, alt1); }
        Ok(Alerts.Count == 1 && Alerts[0].StartsWith("arena_pair|") && Alerts[0].Contains("6 ranked duels in 7 days"), "six ranked duels in a week alert RealmWarden", string.Join("\n", Alerts));
        int r0 = Rating(alt1);
        Win(alt1, alt2);
        Ok(Alerts.Count == 1, "once per pair per week");
        Ok(Rating(alt1) - r0 <= 1, "and the seventh meeting in a week is worth next to nothing", r0 + " -> " + Rating(alt1));

        // The floor.
        Reset();
        var low = P("Low", 0, 0); var top = P("Top", 4, 0);
        SetF(Fighter(low), "Rating", 110); SetF(Fighter(top), "Rating", 110);
        Win(top, low);
        Ok(Rating(low) == 100, "no rating falls below MinRating", Rating(low).ToString());

        // The ladder and the records.
        Reset();
        var a = P("Ada", 0, 0); var b = P("Bram", 4, 0); var c = P("Cy", 8, 0);
        Clear();
        Cmd(a, "arena", "top");
        Ok(a.All().Contains("after 10 ranked duels"), "an empty ladder says how to join it", a.All());
        foreach (var x in new[] { a, b, c }) { SetF(Fighter(x), "Games", 10); SetF(Fighter(x), "LastRanked", Clock); }
        SetF(Fighter(a), "Rating", 1300); SetF(Fighter(b), "Rating", 1250); SetF(Fighter(c), "Rating", 1100);
        SetF(Fighter(c), "LastRanked", Clock.AddDays(-40));
        Clear();
        Cmd(b, "arena", "top");
        Ok(b.All().Contains("1. Ada - 1300") && b.All().Contains("2. Bram - 1250") && !b.All().Contains("Cy"), "the ladder: best first, the long idle left off", b.All());
        Clear();
        Cmd(b, "arena");
        Ok(b.All().Contains("You are #2 on the ladder") && b.All().Contains("Next crowning"), "the overview", b.All());
        Clear();
        Cmd(b, "arena", "Ada");
        Ok(b.All().Contains("Ada in the ring") && b.All().Contains("Rating 1300, ladder #1"), "another fighter's record", b.All());
        Win(a, b);
        Clear();
        Cmd(a, "arena", "history");
        Ok(a.All().Contains("Ada beat Bram") && a.All().Contains("ranked"), "the history", a.All());
        var ladder = (string[])Inv(A, "GetArenaLadder", 5);
        Ok(ladder.Length == 2 && ladder[0].StartsWith("Ada|"), "GetArenaLadder for boards and pages", string.Join(";", ladder));
        Ok((int)Inv(A, "GetArenaRating", a.Id.ToString()) == Rating(a), "GetArenaRating");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Teams()
    {
        Reset();
        var ada = P("Ada", 0, 0); var cy = P("Cy", 3, 0); var bram = P("Bram", 6, 0); var dee = P("Dee", 9, 0);
        Clear();
        Cmd(ada, "duel", "2v2", "Cy", "Bram");
        Ok(ada.All().Contains("Usage") && L("Challenges").Count == 0, "a team challenge names every fighter", ada.All());
        Clear();
        Cmd(ada, "duel", "2v2", "Cy", "Bram", "Bram");
        Ok(ada.All().Contains("named twice"), "nobody twice", ada.All());
        Cmd(ada, "duel", "2v2", "Cy", "Bram", "Dee", "50");
        Ok(L("Challenges").Count == 1 && Held() == 50 && cy.Popups.Count == 1 && bram.Popups.Count == 1 && dee.Popups.Count == 1, "a 2v2 asks all three others");
        Ok(bram.All().Contains("Ada & Cy against Bram & Dee"), "the sides are named", bram.All());
        Clear();
        Cmd(cy, "duel", "accept", "Ada", "50");
        Ok(Duel(ada) == null && ada.All().Contains("Cy accepts. Still waiting for: Bram, Dee"), "each acceptance is told; the duel waits for all", ada.All());
        Cmd(bram, "duel", "accept", "Ada", "50");
        Cmd(dee, "duel", "accept", "Ada", "50");
        Ok(Duel(ada) != null && Held() == 200 && State(ada) == "countdown", "all four in: the ring is drawn (25 m for teams)");
        Tick(6);
        Ok(Hit(ada, cy, 10) == "turned", "no blows on your own side");
        Clear();
        Ok(Hit(ada, bram, 500) == "turned" && Duel(ada) != null && dee.All().Contains("Bram is felled by Ada"), "one foe felled; the fight goes on", dee.All());
        Ok(Hit(bram, ada, 10) == "turned", "a felled fighter is out of it");
        Ok(Fall(bram, 500) == "turned" && !bram.Health.Dead, "and a fall that would kill a felled fighter is still turned (no death, no loot)");
        Hit(dee, ada, 5);
        Tick(16);
        Hit(cy, dee, 500);
        Ok(Duel(ada) == null && Purse(ada) == 1050 && Purse(cy) == 1050 && Purse(bram) == 950 && Purse(dee) == 950, "the side left standing shares the pot", Purse(ada) + "," + Purse(cy) + "," + Purse(bram) + "," + Purse(dee));
        Ok(ZeroSum() == "", "balanced", ZeroSum());
        Ok(FInt(ada, "TeamRating") == 1020 && FInt(dee, "TeamRating") == 980 && Rating(ada) == 1000, "team duels move the team rating, not the duel rating");
        Tick(31);
        Cmd(ada, "duel", "3v3", "Cy", "Bram", "Dee", "Eve", "Fay", "20");
        Ok(ada.All().Contains("No one called 'Eve'"), "a 3v3 needs five others online", ada.All());
        Cmd(ada, "duel", "2v2", "Cy", "Bram", "Dee");
        bram.Popups.Last().Answer(false);
        Ok(L("Challenges").Count == 0, "one refusal calls the team challenge off");
        Tune("Teams", "Enabled", false);
        Clear();
        Tick(61);
        Cmd(ada, "duel", "2v2", "Cy", "Bram", "Dee");
        Ok(ada.All().Contains("Team duels are not allowed"), "team duels can be switched off", ada.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Refusals()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0); var eve = P("Eve", 40, 0);
        Protected.Add(bram.Id.ToString());
        Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("Bram is under the Warden's new-player protection"), "no duel with a protected newcomer (no blow could reach them)", ada.All());
        Protected.Clear(); Protected.Add(ada.Id.ToString());
        Clear(); Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("protection off confirm"), "a protected player is told how to give it up", ada.All());
        Protected.Clear();
        Frozen.Add(bram.Id.ToString());
        Clear(); Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("Bram cannot duel now"), "the Sentinel's frozen players cannot duel", ada.All());
        Frozen.Clear();
        Truce = true;
        Clear(); Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("Truce of the Realm holds"), "no duels while the truce holds", ada.All());
        Truce = false;
        Win(ada, bram, 0);
        Fight(ada, bram, 20);
        Truce = true;
        Clear();
        Slow();
        Ok(Duel(ada) == null && Purse(ada) == 1000 && ada.All().Contains("Truce of the Realm begins"), "a truce that begins mid-fight voids it, stakes back", ada.All());
        Truce = false;
        Tick(61);
        Cmd(bram, "duel", "off");
        Clear(); Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("Bram is not taking challenges"), "/duel off refuses challenges", ada.All());
        Cmd(bram, "duel", "on");
        Hit(ada, eve, 5);
        Clear(); Cmd(ada, "duel", "Bram");
        Ok(ada.All().Contains("You were in a fight a moment ago") && L("Challenges").Count == 0, "no escaping a fight into a duel", ada.All());
        Tick(31);
        Clear(); Cmd(ada, "duel", "Ada");
        Ok(ada.All().Contains("cannot challenge yourself"), "not yourself");
        Clear(); Cmd(ada, "duel", "Nobody");
        Ok(ada.All().Contains("No one called 'Nobody'"), "an unknown name");
        Tune("Duels", "ChallengeCooldownSeconds", 0);
        var c1 = P("C1", 8, 0); var c2 = P("C2", 12, 0); var c3 = P("C3", 16, 0);
        Cmd(ada, "duel", "Bram"); Cmd(ada, "duel", "C1"); Cmd(ada, "duel", "C2");
        Clear(); Cmd(ada, "duel", "C3");
        Ok(L("Challenges").Count == 3 && ada.All().Contains("already have 3 challenges"), "at most three open challenges", ada.All());
        Clear(); Cmd(bram, "duel", "Ada");
        Ok(bram.All().Contains("already waiting"), "one challenge between two players at a time", bram.All());
        Cmd(ada, "duel", "cancel");
        c1.Health.Dead = true;
        Clear(); Cmd(bram, "duel", "C1");
        Ok(bram.All().Contains("C1 is not on their feet"), "not against someone down", bram.All());
        c1.Health.Dead = false;
        Tune("Duels", "Enabled", false);
        Clear(); Cmd(bram, "duel", "C2");
        Ok(bram.All().Contains("Duels are not allowed"), "duels can be switched off", bram.All());
        Tune("Duels", "Enabled", true);
        SetF(F(A, "config"), "Enabled", false);
        Clear(); Cmd(bram, "arena");
        Ok(bram.All().Contains("The arena is closed"), "the whole arena can be switched off", bram.All());
        SetF(F(A, "config"), "Enabled", true);
        // An arena zone, required.
        Tune("Duels", "RequireArena", true);
        Clear(); Cmd(bram, "duel", "C2");
        Ok(bram.All().Contains("none is set up yet"), "arena required, none set: told so", bram.All());
        var steward = P("Steward", 100, 100, 0);
        Admin(steward);
        Cmd(steward, "arena", "admin", "zone", "set", "Proving", "Ring", "20");
        Ok(((IList)F(F(A, "config"), "Arenas")).Count == 1 && steward.All().Contains("Proving Ring is set"), "staff set an arena where they stand", steward.All());
        Clear(); Cmd(bram, "duel", "C2");
        Ok(bram.All().Contains("only in an arena") && L("Challenges").Count == 0, "outside it, no challenge", bram.All());
        Move(bram, 101, 100); Move(c2, 104, 100);
        Tick(61);
        Cmd(bram, "duel", "C2");
        Accept(c2, bram);
        Ok(State(bram) == "countdown" && (string)F(Duel(bram), "Arena") == "Proving Ring", "inside it, the arena is the ring", State(bram));
        Clear(); Cmd(steward, "arena", "zones");
        Ok(steward.All().Contains("Arena Proving Ring at (100, 100), 20 m"), "/arena zones lists it", steward.All());
        Cmd(steward, "arena", "admin", "zone", "remove", "Proving Ring");
        Ok(((IList)F(F(A, "config"), "Arenas")).Count == 0, "and staff remove it");
        Clear(); Cmd(bram, "arena", "admin", "status");
        Ok(bram.All().Contains("You may not do that"), "admin commands need realmarena.admin", bram.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void FellingGuard()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0);
        Fight(ada, bram);
        Ok(Hit(ada, bram, 20, HumanBodyBones.Head) == "applied" && bram.Health.HeadHealth.CurrentHealth == 10, "a head blow that leaves the head standing lands (20 of 30, judged as 25)");
        Ok(Hit(ada, bram, 8, HumanBodyBones.Head) == "turned" && Duel(ada) == null && !bram.Health.Dead, "8 more would be judged 10 against 10 left: felled, not killed");
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram);
        Ok(Hit(ada, bram, 50, HumanBodyBones.LeftLowerLeg) == "applied" && bram.Health.LegsHealth.CurrentHealth == 0 && bram.Health.TorsoHealth.CurrentHealth == 70,
            "a leg blow: the legs take it, the spill (judged 12.5) leaves the torso standing");
        Ok(Hit(ada, bram, 30, HumanBodyBones.Chest) == "applied" && Hit(ada, bram, 30, HumanBodyBones.Chest) == "applied" && bram.Health.TorsoHealth.CurrentHealth == 10, "torso blows land while it holds");
        Ok(Hit(ada, bram, 8, HumanBodyBones.Chest) == "turned" && Duel(ada) == null && !bram.Health.Dead, "until the next would end it");
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram);
        Ok(Hit(ada, bram, 100, HumanBodyBones.LastBone) == "applied" && !bram.Health.Dead, "a blow with no hit bone is spread over the regions, as the game does");
        Ok(Hit(ada, bram, 40, HumanBodyBones.LastBone) == "turned" && !bram.Health.Dead, "and judged the same way");
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Bearer = ada.Id.ToString();
        Fight(ada, bram);
        Ok(Hit(ada, bram, 13, HumanBodyBones.Head) == "turned" && Duel(ada) == null, "the Ironbreaker's bearer: a 13 head blow is judged twice as hard (RealmLegendary scales it)");
        Bearer = null;
        Reset();
        Tune("Duels", "YieldHealthPercent", 50f);
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram);
        Ok(Hit(ada, bram, 40, HumanBodyBones.LeftUpperLeg) == "applied", "a yield line at half health: 40 (judged 50) leaves 100");
        Ok(Hit(ada, bram, 30, HumanBodyBones.LeftUpperLeg) == "turned" && Duel(ada) == null, "30 more (judged 37.5) would leave under 75: yield");
        Reset();
        Tune("Duels", "PreventDeathFlag", true);
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Challenge(ada, bram); Accept(bram, ada);
        Ok(!bram.Health.PreventDeath, "PreventDeathFlag: not set before the fight");
        Tick(6);
        Ok(bram.Health.PreventDeath && ada.Health.PreventDeath, "set on both while they fight");
        Hit(bram, ada, 5); Tick(16);
        Hit(ada, bram, 500);
        Ok(!bram.Health.PreventDeath && !ada.Health.PreventDeath, "cleared when the duel ends");
        Fight(ada, bram); Tick(31);
        Fight(ada, bram);
        Inv(A, "Unload");
        Ok(!bram.Health.PreventDeath, "and on unload");
        // The fallback: a death in the fight still decides it, and is logged.
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        Fight(ada, bram);
        Inv(A, "OnEntityDeath", new CodeHatch.Networking.Events.Entities.EntityDeathEvent { Entity = bram.Entity, KillingDamage = new Damage { DamageSource = ada.Entity } });
        Ok(Duel(ada) == null && A.Logged.Any(l => l.Contains("died in the ring")) && ada.All().Contains("Victory over Bram"), "a death that slips past the guard decides the duel and is logged");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Champion()
    {
        Reset();
        var ada = P("Ada", 0, 0, 1000, 600, "Varrow"); var bram = P("Bram", 4, 0); var cy = P("Cy", 8, 0);
        DateTime next = (DateTime)D("NextCrowning");
        string key = next.ToString("yyyy'-'MM'-'dd");
        SetF(Fighter(ada), "Games", 12); SetF(Fighter(ada), "Rating", 1300); SetF(Fighter(ada), "WeekKey", key); SetF(Fighter(ada), "WeekGames", 3); SetF(Fighter(ada), "WeekWins", 3);
        SetF(Fighter(bram), "Games", 12); SetF(Fighter(bram), "Rating", 1350); SetF(Fighter(bram), "WeekKey", key); SetF(Fighter(bram), "WeekGames", 2);
        SetF(Fighter(cy), "Games", 5); SetF(Fighter(cy), "Rating", 1500); SetF(Fighter(cy), "WeekKey", key); SetF(Fighter(cy), "WeekGames", 5);
        Clock = next.AddMinutes(1);
        Clear();
        Slow();
        var champ = D("Champion");
        Ok(champ != null && (string)F(champ, "Name") == "Ada", "the best established fighter with three ranked duels is crowned (not the provisional Cy, not Bram with two)");
        Ok(B().Contains("Ada is crowned Champion of the Ring"), "the herald tells the realm", B());
        Ok(Deeds.Any(d => d.Contains("|arena_champion|") && d.EndsWith("arena:champion:" + key)), "RealmRenown's deed, once per week", string.Join("\n", Deeds));
        Ok(Awards.Count == 1 && Awards[0].StartsWith("Varrow|10|"), "RealmSeasons points for the champion's house", string.Join("\n", Awards));
        Ok(ChronLog.Count == 1 && ChronLog[0].StartsWith("title_earned|Ada is crowned Champion of the Ring"), "the Chronicle records it", string.Join("\n", ChronLog));
        Ok(ada.Popups.Any(p => p.Title == "Champion of the Ring"), "the champion gets a window");
        Ok(((DateTime)D("NextCrowning")) == next.AddDays(7) && FInt(ada, "Championships") == 1, "the next crowning is a week on");
        Clear();
        Slow();
        Ok(Deeds.Count == 0 && !B().Contains("crowned"), "no second crowning in the same week");
        Clear();
        Cmd(bram, "arena", "champion");
        Ok(bram.All().Contains("Champion of the Ring: Ada, crowned for the week of " + key), "/arena champion", bram.All());
        Clock = next.AddDays(7).AddMinutes(1);
        Clear();
        Slow();
        Ok(B().Contains("No Champion of the Ring this week"), "a week without enough ranked duels crowns no one", B());
        Admin(cy);
        SetF(Fighter(bram), "WeekKey", ((DateTime)D("NextCrowning")).ToString("yyyy'-'MM'-'dd")); SetF(Fighter(bram), "WeekGames", 4);
        SetF(Fighter(bram), "LastRanked", Clock); SetF(Fighter(ada), "LastRanked", Clock);
        Clear();
        Cmd(cy, "arena", "admin", "crown");
        Ok(B().Contains("Bram is crowned Champion of the Ring") && (string)F(D("Champion"), "Name") == "Bram", "staff may crown early", B());
        Ok((string)Inv(A, "GetArenaChampion") == "Bram", "GetArenaChampion");
        Clear();
        Cmd(cy, "arena", "top");
        Ok(cy.All().Contains("Bram, Champion of the Ring"), "the ladder marks the champion", cy.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Tournament()
    {
        Reset();
        var steward = P("Steward", 500, 500, 0); Admin(steward);
        var ana = P("Ana", 0, 0, 1000, 600, "Varrow"); var ben = P("Ben", 2, 0); var cal = P("Cal", 4, 0); var dov = P("Dov", 6, 0); var eli = P("Eli", 8, 0);
        Cmd(ben, "arena", "tourney", "open");
        Ok(ben.All().Contains("You may not do that"), "only staff open the Lists");
        Clear();
        Cmd(steward, "arena", "tourney", "open", "20");
        Ok(B().Contains("The Lists of the Ring are open, 20 marks to enter") && ChronLog.Any(c => c.StartsWith("event_started|")), "the Lists open with a fee, heralded and chronicled", B());
        foreach (var p in new[] { ana, ben, cal, dov, eli }) Cmd(p, "arena", "tourney", "join");
        Ok(Purse(ana) == 980 && Held() == 100, "each entrant's fee is held");
        Cmd(eli, "arena", "tourney", "leave");
        Ok(Purse(eli) == 1000 && Held() == 80, "leaving before the draw returns the fee");
        Cmd(eli, "arena", "tourney", "join");
        Clear();
        Advance(TimeSpan.FromMinutes(10));
        var t = D("Tourney");
        Ok(t != null && (string)F(t, "State") == "running" && B().Contains("The Lists of the Ring begin with 5 fighters"), "the bracket is drawn when sign-up ends", B());
        Ok(Duel(dov) != null && Duel(dov) == Duel(eli) && Duel(ana) == null, "seeds 4 and 5 meet first; the top three have byes");
        Tick(6);
        Ok(State(dov) == "fight", "the match is fought in the ring");
        Hit(eli, dov, 5); Tick(16); Hit(dov, eli, 500); Heal(dov); Heal(eli);
        Tick(2);
        Ok(Duel(ana) != null && Duel(ana) == Duel(dov) && Duel(ben) == Duel(cal), "the semi-finals are called: Ana against Dov, Ben against Cal");
        Tick(6);
        Hit(dov, ana, 5); Tick(16); Hit(ana, dov, 500);
        Hit(cal, ben, 5); Hit(ben, cal, 500);
        Heal(ana); Heal(ben);
        Tick(2);
        Ok(Duel(ana) != null && Duel(ana) == Duel(ben), "the final: Ana against Ben");
        Tick(6);
        Clear();
        Hit(ben, ana, 5); Tick(16); Hit(ana, ben, 500);
        Ok(D("Tourney") == null && B().Contains("Ana wins the Lists of the Ring, beating Ben in the final! 70 marks to the champion, 30 to the runner-up"), "the champion is heralded with the pot", B());
        Ok(Purse(ana) == 1050 && Purse(ben) == 1010 && Purse(cal) == 980 && Held() == 0, "the pot of 100 is shared 70 / 30", Purse(ana) + "/" + Purse(ben));
        Ok(ZeroSum() == "", "balanced", ZeroSum());
        Ok(Deeds.Any(d => d.Contains("|arena_tourney|")) && Awards.Any(a => a.StartsWith("Varrow|15|")), "RealmRenown's deed and RealmSeasons' points for the champion");
        Ok(ChronLog.Any(c => c.StartsWith("event_ended|Ana wins the Lists of the Ring")) && !ChronLog.Any(c => c.StartsWith("tournament_champion")), "chronicled as event_ended, never as the Royal Tournament", string.Join("\n", ChronLog));
        Ok(Rating(ana) == 1000 && FInt(ana, "TourneyWins") == 1, "bracket matches are unranked; the win is on the record");
        // A no-show, too few, a cancel.
        Reset();
        steward = P("Steward", 500, 500, 0); Admin(steward);
        var a = P("Ana", 0, 0); var b = P("Ben", 2, 0); var c = P("Cal", 4, 0); var d = P("Dov", 300, 300);
        Cmd(steward, "arena", "tourney", "open");
        foreach (var p in new[] { a, b, c, d }) Cmd(p, "arena", "tourney", "join");
        Cmd(steward, "arena", "tourney", "start");
        Ok(Duel(a) != null && Duel(a) == Duel(d), "staff may draw the bracket early: Ana meets Dov");
        Offline(d);
        Clear();
        Advance(TimeSpan.FromMinutes(7));
        Ok(a.All().Contains("Ana was there and goes through"), "Dov never came: Ana goes through", a.All());
        Reset();
        steward = P("Steward", 500, 500, 0); Admin(steward);
        a = P("Ana", 0, 0, 1000); b = P("Ben", 2, 0, 1000);
        Cmd(steward, "arena", "tourney", "open", "10");
        Cmd(a, "arena", "tourney", "join"); Cmd(b, "arena", "tourney", "join");
        Clear();
        Advance(TimeSpan.FromMinutes(10));
        Ok(D("Tourney") == null && B().Contains("Too few entered") && Purse(a) == 1000 && Held() == 0, "too few: called off, fees back", B());
        Cmd(steward, "arena", "tourney", "open", "10");
        Cmd(a, "arena", "tourney", "join");
        Cmd(steward, "arena", "tourney", "cancel");
        Ok(D("Tourney") == null && Purse(a) == 1000 && ZeroSum() == "", "staff may cancel: fees back");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Royal()
    {
        Reset();
        var a = P("Ana", 0, 0); var b = P("Ben", 2, 0); var c = P("Cal", 4, 0); var d = P("Dov", 6, 0);
        RoyalActive = new[] { "tournament|" + Iso(Clock) + "|" + Iso(Clock.AddHours(1)) };
        RoyalEntrants = new[] { a, b, c, d }.Select(p => p.Id + "|" + p.Name).ToArray();
        Clear();
        Slow();
        Ok(D("Tourney") != null && B().Contains("holds a bracket for the Royal Tournament"), "while the Royal Tournament runs, the ring opens a bracket", B());
        Clear();
        Cmd(a, "arena", "tourney", "join");
        Ok(a.All().Contains("/tourney[FFFFFF] join"), "entry is through the Royal Tournament itself", a.All());
        Advance(TimeSpan.FromMinutes(5));
        Ok(Events.Calls.Contains("GetTournamentEntrants()") && Duel(a) != null, "the entrants are drawn from RealmEvents");
        var foe = Duel(a) == Duel(d) ? d : (Duel(a) == Duel(c) ? c : b);
        Tick(6);
        Hit(foe, a, 5); Tick(16); Hit(a, foe, 500);
        Ok(RoyalScores.Contains(a.Id + ">" + foe.Id), "a bracket win scores in the Royal Tournament (ScoreTournamentDuel)", string.Join(",", RoyalScores));
        RoyalActive = null;
        Clear();
        Slow();
        Ok(D("Tourney") == null && B().Contains("bracket closes where it stood"), "when the Royal Tournament ends, its bracket closes", B());
        Ok(L("Duels").Count == 0, "no bracket duel is left");
        Slow();
        Ok(D("Tourney") == null, "the same Royal Tournament does not open a second bracket");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Trial()
    {
        Reset();
        var acc = P("Accused", 0, 0); var champ = P("Champion", 4, 0); var other = P("Other", 300, 0);
        Ok((bool)A.Call("StageTrial", "7", acc.Id.ToString(), "Accused", champ.Id.ToString(), "Champion", 15), "RealmLaws stages a trial by combat in the ring");
        var d = Duel(acc);
        Ok(d != null && (string)F(d, "Kind") == "trial" && acc.All().Contains("Case #7: your trial by combat against Champion"), "both are told", acc.All());
        Ok(!(bool)A.Call("StageTrial", "8", acc.Id.ToString(), "Accused", other.Id.ToString(), "Other", 15), "a fighter already in the ring cannot be tried twice");
        Tick(6);
        Ok(State(acc) == "fight" && ((DateTime)F(d, "EndsAt") - Clock).TotalMinutes > 10, "a trial fight lasts as long as the court's window");
        Hit(acc, champ, 5); Tick(16);
        Hit(champ, acc, 500);
        Ok(TrialResults.Count == 1 && TrialResults[0] == "7|" + champ.Id, "the winner goes back to RealmLaws (ArenaTrialResult)", string.Join(",", TrialResults));
        Ok(Rating(acc) == 1000 && !acc.Health.Dead, "unranked, and no one dies");
        Offline(other);
        Ok(!(bool)A.Call("StageTrial", "9", acc.Id.ToString(), "Accused", other.Id.ToString(), "Other", 15), "an offline fighter cannot be staged");
        Tune("Trials", "Enabled", false);
        Online(other);
        Ok(!(bool)A.Call("StageTrial", "9", acc.Id.ToString(), "Accused", other.Id.ToString(), "Other", 15), "the court's trials can be switched off");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Dice()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 2, 0); var cy = P("Cy", 10, 0); var dee = P("Dee", 300, 0);
        Cmd(ada, "dice", "Bram", "20");
        Ok(Purse(ada) == 980 && bram.Popups.Count == 1 && bram.All().Contains("Ada invites you to Hearth Dice for 20 marks each"), "a game of dice: the stake is held, the other is asked", bram.All());
        Clear();
        Cmd(bram, "dice", "accept", "Ada");
        Ok(bram.All().Contains("That game is for 20 marks each"), "the stake must be named to accept", bram.All());
        Rng(5, 4, 1, 1);
        Cmd(bram, "dice", "accept", "Ada", "20");
        Ok(ada.All().Contains("Ada throws 6 and 5 (11); Bram throws 2 and 2 (4)"), "both throws are shown", ada.All());
        Ok(Purse(ada) == 1020 && Purse(bram) == 980 && Held() == 0, "the higher total takes the pot of 40: no house share");
        Ok(cy.All().Contains("Ada beats Bram at Hearth Dice, 11 to 4") && !dee.All().Contains("Hearth Dice"), "players nearby hear of it, the far away do not");
        Ok(ZeroSum() == "", "balanced", ZeroSum());
        Tick(11);
        Cmd(ada, "dice", "Bram", "20");
        Rng(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Cmd(bram, "dice", "accept", "Ada", "20");
        Ok(Purse(ada) == 1020 && Purse(bram) == 980 && ada.All().Contains("Level after five throws"), "five level throws: both stakes back");
        Clear();
        Cmd(ada, "dice", "Bram", "101");
        Ok(ada.All().Contains("between 1 and 100 marks"), "the stake limit", ada.All());
        Clear();
        Cmd(ada, "dice", "Bram", "10");
        Ok(ada.All().Contains("Catch your breath"), "a cooldown between games", ada.All());
        Tick(11);
        Tune("Tavern", "DailyLossLimit", 30L);
        Clear();
        Cmd(bram, "dice", "Ada", "20");
        Ok(bram.All().Contains("could lose more than today's limit of 30"), "the daily loss limit: Bram lost 20 today and could lose 20 more", bram.All());
        Tune("Tavern", "DailyLossLimit", 250L);
        Tune("Tavern", "PairGamesPerDay", 2);
        Clear();
        Cmd(bram, "dice", "Ada", "20");
        Ok(bram.All().Contains("played 2 games today"), "games per pair per day", bram.All());
        Tune("Tavern", "PairGamesPerDay", 10);
        Tune("Tavern", "DailyStakeLimit", 50L);
        Clear();
        Cmd(bram, "dice", "Ada", "20");
        Ok(bram.All().Contains("today's limit of 50 marks staked"), "the daily stake limit", bram.All());
        var newbie = P("Newbie", 4, 0, 1000, 5);
        Clear();
        Cmd(newbie, "dice", "Cy", "5");
        Ok(newbie.All().Contains("60 minutes"), "newcomers play for marks only after an hour", newbie.All());
        Clear();
        Cmd(cy, "dice", "Newbie", "5");
        Ok(cy.All().Contains("Newbie cannot sit at the table now"), "and no one can take their marks either", cy.All());
        Clear();
        Rng(1, 2);
        Cmd(ada, "dice", "roll", "2d6");
        Ok(ada.All().Contains("Ada throws 2d6: 2 + 3 = 5") && cy.All().Contains("Ada throws 2d6") && !dee.All().Contains("throws"), "a throw for show reaches the players near", ada.All());
        Clear();
        Cmd(ada, "dice", "roll", "9d999");
        Ok(ada.All().Contains("Usage"), "and has bounds", ada.All());
        Tune("Tavern", "Dice", false);
        Clear();
        Cmd(ada, "dice", "Cy", "5");
        Ok(ada.All().Contains("tavern games are closed"), "dice can be switched off", ada.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    // Each deal below is fixed first with Rng(4, 5, 6, 7) (5 and 7 to the challenger, 6 and 8 to the other): a dealt 21
    // stands at once, which would leave the rigged hands to chance.
    static void SetHands(object g, int[] a, int[] b, params int[] deckTop)
    {
        var ha = (List<int>)F(g, "HandA"); ha.Clear(); ha.AddRange(a);
        var hb = (List<int>)F(g, "HandB"); hb.Clear(); hb.AddRange(b);
        var deck = (List<int>)F(g, "Deck"); foreach (int c in deckTop.Reverse()) { deck.Remove(c); }
        foreach (int c in deckTop) { deck.Remove(c); deck.Add(c); }
    }
    static void Cards()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 2, 0);
        Ok((int)typeof(RealmArena).GetMethod("HandValue", BF | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { new List<int> { 0, 12 } }) == 21, "Ace and King: 21");
        Ok((int)typeof(RealmArena).GetMethod("HandValue", BF | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { new List<int> { 0, 13, 12 } }) == 12, "two Aces and a King: 12");
        Cmd(ada, "cards", "Bram", "30");
        Rng(4, 5, 6, 7); Cmd(bram, "cards", "accept", "Ada", "30");
        Ok(L("Games").Count == 1 && ada.All().Contains("Your hand:") && bram.All().Contains("Your hand:") && Held() == 60, "both stakes held; each sees only their own hand");
        var g = L("Games")[0];
        SetHands(g, new[] { 12, 19 }, new[] { 4, 5 }, 9);                    // Ada King+7 = 17; Bram 5+6 = 11; next card a 10
        Clear();
        Cmd(bram, "cards", "hit");
        Ok(bram.All().Contains("(21)") && bram.All().Contains("You stand on 21") && ada.All().Contains("Bram draws a card"), "a hit to 21 stands at once; the other only hears a card was drawn", bram.All());
        Cmd(ada, "cards", "stand");
        Ok(L("Games").Count == 0 && Purse(bram) == 1030 && Purse(ada) == 970 && ada.All().Contains("King of Stags, 7 of Oaks (17)"), "both stand: hands shown, closest to 21 takes the pot", ada.All());
        Ok(ZeroSum() == "", "balanced", ZeroSum());
        Tick(11);
        Cmd(ada, "cards", "Bram", "30"); Rng(4, 5, 6, 7); Cmd(bram, "cards", "accept", "Ada", "30");
        g = L("Games")[0];
        SetHands(g, new[] { 12, 25 }, new[] { 4, 5 }, 22);                   // Ada King+King = 20, draws a 10: bust
        Cmd(ada, "cards", "hit");
        Ok(ada.All().Contains("over 21"), "over 21 is a bust", ada.All());
        Cmd(bram, "cards", "stand");
        Ok(Purse(bram) == 1060 && Purse(ada) == 940, "the bust loses to any standing hand");
        Tick(11);
        Cmd(ada, "cards", "Bram", "30"); Rng(4, 5, 6, 7); Cmd(bram, "cards", "accept", "Ada", "30");
        g = L("Games")[0];
        SetHands(g, new[] { 12, 6 }, new[] { 25, 19 });                       // 17 and 17
        Cmd(ada, "cards", "stand"); Cmd(bram, "cards", "stand");
        Ok(Purse(bram) == 1060 && Purse(ada) == 940 && Held() == 0 && ada.All().Contains("Level hands"), "level hands: both stakes back");
        Tick(11);
        Cmd(ada, "cards", "Bram", "30"); Rng(4, 5, 6, 7); Cmd(bram, "cards", "accept", "Ada", "30");
        g = L("Games")[0];
        SetHands(g, new[] { 12, 6 }, new[] { 4, 5 });
        Clear();
        Tick(46);
        Ok(L("Games").Count == 0 && Purse(ada) == 970, "a hand stands by itself when its time runs out", ada.All());
        Tick(11);
        Cmd(ada, "cards", "Bram", "30"); Rng(4, 5, 6, 7); Cmd(bram, "cards", "accept", "Ada", "30");
        g = L("Games")[0];
        SetHands(g, new[] { 12, 6 }, new[] { 4, 5 });
        Clear();
        Offline(ada);
        Ok(bram.All().Contains("Ada left the table; their hand stands"), "leaving the table stands the hand: no escape from a bad one", bram.All());
        Cmd(bram, "cards", "stand");
        Ok(L("Games").Count == 0 && Purse(ada) == 1000, "and it is settled as it stood");
        Ok(ZeroSum() == "", "balanced", ZeroSum());
        Clear();
        Cmd(bram, "cards", "hit");
        Ok(bram.All().Contains("not at a card table"), "no table, no cards", bram.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Teleport()
    {
        Reset();
        Tune("Duels", "Teleport", true);
        ((IList)F(F(A, "config"), "Arenas")).Add(Activator.CreateInstance(typeof(RealmArena).GetNestedType("Zone", BF)));
        var z = ((IList)F(F(A, "config"), "Arenas"))[0];
        SetF(z, "Name", "Proving Ring"); SetF(z, "X", 100f); SetF(z, "Z", 100f); SetF(z, "Radius", 20f);
        var ada = P("Ada", 0, 0); var bram = P("Bram", 300, 300);
        Challenge(ada, bram); Accept(bram, ada);
        Ok(CharacterTeleport.Moves.Count == 2 && Math.Abs(ada.Entity.Position.x - 90) < 0.1 && Math.Abs(bram.Entity.Position.x - 110) < 0.1, "Teleport: both are brought into the arena, one each side", string.Join(",", CharacterTeleport.Moves));
        Ok(Graces.Count == 2, "RealmSentinel is asked for movement grace first");
        Ok(State(ada) == "countdown", "the count starts at once");
        Tick(6);
        Hit(bram, ada, 5); Tick(16); Hit(ada, bram, 500);
        A.timer.RunPending();
        Ok(Math.Abs(bram.Entity.Position.x - 300) < 0.1 && Math.Abs(ada.Entity.Position.x) < 0.1, "and taken back where they stood afterwards");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void ReloadAndData()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0);
        Win(ada, bram);
        var steward = P("Steward", 500, 500, 0); Admin(steward);
        Cmd(steward, "arena", "tourney", "open");
        Cmd(ada, "arena", "tourney", "join");
        Inv(A, "Unload");
        NewArena();
        Ok(Rating(ada) == 1020 && D("Tourney") != null && ((IList)F(D("Tourney"), "Entrants")).Count == 1, "ratings and an open tournament survive a reload");
        Cmd(bram, "duel", "Ada", "40");
        var pop = ada.Popups.Last();
        Inv(A, "Unload");
        Ok(Purse(bram) == 1000 && Holds() == 0, "unload drops open challenges and returns their stakes");
        pop.Answer(true);
        Ok(Duel(ada) == null, "an answer to a window after unload is ignored");
        NewArena();
        // A damaged file is never overwritten.
        string path = Path.Combine(Dir, "RealmArena.json");
        File.WriteAllText(path, "{\"Version\": 1, \"Fighters\": {\"76561190000000001\": {\"Name\": \"Ada\", \"Rat");
        string before = File.ReadAllText(path);
        NewArena();
        Ok((bool)F(A, "loadFailed") && A.Logged.Any(l => l.StartsWith("ERROR") && l.Contains("RealmArena_lastgood.json")), "a cut-off data file stops the plugin with a clear message");
        Clear();
        Cmd(ada, "arena");
        Ok(ada.All().Contains("The arena is paused"), "commands say it is paused", ada.All());
        Inv(A, "OnEntityHealthChange", new CodeHatch.Networking.Events.Entities.EntityDamageEvent { Entity = bram.Entity, Damage = new Damage { Amount = 5, DamageSource = ada.Entity } });
        Inv(A, "Unload");
        Ok(File.ReadAllText(path) == before, "and the file is not overwritten");
        File.WriteAllText(path, "");
        NewArena();
        Ok((bool)F(A, "loadFailed"), "an empty file too");
        File.Delete(path);
        NewArena();
        Ok(!(bool)F(A, "loadFailed") && File.Exists(path), "a missing file starts fresh");
    }

    // ------------------------------------------------------------------------------------------------------------
    static void Popups()
    {
        Reset();
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0);
        PopupsOff.Add(bram.Id.ToString());
        Cmd(ada, "duel", "Bram");
        Ok(bram.Popups.Count == 0 && bram.All().Contains("/duel accept"), "a player who turned windows off gets the chat lines only");
        Cmd(ada, "duel", "cancel");
        PopupsOff.Clear();
        PlayerExtensions.PopupsFail = true;
        Tick(61);
        Cmd(ada, "duel", "Bram");
        Ok(bram.Popups.Count == 0 && A.Logged.Any(l => l.Contains("ShowConfirmPopup failed")) && bram.All().Contains("/duel accept"), "a window that fails falls back to chat");
        Cmd(bram, "duel", "accept", "Ada");
        Ok(Duel(ada) != null, "and the chat answer works");
        PlayerExtensions.PopupsFail = false;
        Reset();
        ada = P("Ada", 0, 0); bram = P("Bram", 4, 0);
        SetF(F(A, "config"), "UsePopups", false);
        Cmd(ada, "duel", "Bram");
        Ok(bram.Popups.Count == 0, "UsePopups false: no windows");
        SetF(F(A, "config"), "UsePopups", true);
        Cmd(ada, "duel", "cancel");
        Tick(61);
        Cmd(ada, "duel", "Bram");
        bram.Popups.Last().Answer(false);
        Ok(L("Challenges").Count == 0 && ada.All().Contains("Bram declines"), "Decline in the window declines");
        Tick(61);
        Cmd(ada, "duel", "Bram");
        var late = bram.Popups.Last();
        Tick(70);
        late.Answer(true);
        Ok(Duel(ada) == null, "a late answer is ignored");
        Clear();
        Cmd(ada, "duel", "status");
        Ok(ada.All().Contains("No duel, challenge or game"), "/duel status", ada.All());
        Clear();
        Cmd(ada, "duel");
        Ok(ada.All().Contains("Duels to the first fall") && ada.All().Contains("/duel 2v2"), "/duel alone is the help", ada.All());
        Clear();
        Cmd(ada, "arena", "rules");
        Ok(ada.All().Contains("first fall") && ada.All().Contains("held by the treasury"), "/arena rules", ada.All());
        Clear();
        Cmd(ada, "cards");
        Ok(ada.All().Contains("Twenty-One") && ada.All().Contains("house edge zero"), "/cards alone is the help, with the limits", ada.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void AdminAndExtras()
    {
        Reset();
        var steward = P("Steward", 500, 500, 0); Admin(steward);
        var ada = P("Ada", 0, 0); var bram = P("Bram", 4, 0); var cy = P("Cy", 8, 0);
        // Heralds for a high-stakes duel.
        Clear();
        Fight(ada, bram, 100);
        Ok(B().Contains("Ada and Bram meet in the ring, 100 marks a head on the outcome!"), "a duel for a high stake is heralded to the realm", B());
        Clear();
        Cmd(steward, "arena", "admin", "status");
        Ok(steward.All().Contains("Duels 1, challenges 0, games 0, settlements waiting 0") && steward.All().Contains("Treasury running: yes"), "/arena admin status", steward.All());
        int duelId = (int)F(Duel(ada), "Id");
        Clear();
        Cmd(steward, "arena", "admin", "void", duelId.ToString());
        Ok(Duel(ada) == null && Purse(ada) == 1000 && ada.All().Contains("The staff (Steward) call the duel off"), "staff void a duel: every stake back", ada.All());
        Cmd(steward, "arena", "admin", "void", "999");
        Ok(steward.All().Contains("No such duel"), "a duel that does not exist");
        Cmd(steward, "arena", "admin", "rating", "Ada", "1450");
        Ok(Rating(ada) == 1450 && steward.All().Contains("Ada's rating is now 1450"), "staff set a rating");
        Cmd(steward, "arena", "admin", "bar", "Bram", "2");
        Ok((DateTime)F(Fighter(bram), "BarredUntil") > Clock.AddHours(1.9), "staff bar a player from the ring");
        Clear(); Cmd(bram, "duel", "Cy");
        Ok(bram.All().Contains("You are barred from the ring until"), "the barred player is told until when", bram.All());
        Cmd(steward, "arena", "admin", "unbar", "Bram");
        Ok((DateTime)F(Fighter(bram), "BarredUntil") < Clock, "and lift it");
        Clear();
        Cmd(steward, "arena", "admin", "reset", "Ada");
        Ok(steward.All().Contains("Repeat with confirm") && Rating(ada) == 1450, "a reset asks for confirm first", steward.All());
        Cmd(steward, "arena", "admin", "reset", "Ada", "confirm");
        Ok(Rating(ada) == 1000 && FInt(ada, "Games") == 0 && (double)F(Fighter(ada), "PlayedMinutes") > 100, "and wipes the record (not the time in the realm)");
        Tick(31);
        Win(ada, bram);
        Clear();
        Cmd(steward, "arena", "admin", "pairs");
        Ok(steward.All().Contains("Ada and Bram: 1 ranked"), "/arena admin pairs lists the pairs that meet most", steward.All());
        Cmd(steward, "arena", "admin", "settle");
        Ok(steward.All().Contains("Settlements still waiting: 0"), "/arena admin settle");
        Clear();
        Cmd(steward, "arena", "admin", "tavern", "set", "The", "Hearth", "Inn", "12");
        Cmd(steward, "arena", "zones");
        Ok(steward.All().Contains("Tavern The Hearth Inn at (500, 500), 12 m"), "staff set a tavern", steward.All());
        Tune("Tavern", "RequireTavernZone", true);
        Clear();
        Cmd(ada, "dice", "Bram", "10");
        Ok(ada.All().Contains("only in a tavern"), "tavern games only in a tavern when required", ada.All());
        Move(ada, 501, 500); Move(bram, 502, 500);
        Tick(31);
        Cmd(ada, "dice", "Bram", "10");
        Ok(L("Challenges").Count == 1, "inside the tavern the game is on");
        Cmd(ada, "dice", "cancel");
        Tune("Tavern", "RequireTavernZone", false);
        Cmd(steward, "arena", "admin", "tavern", "remove", "The Hearth Inn");
        Ok(((IList)F(F(A, "config"), "Taverns")).Count == 0, "and remove it");
        // The team ladder.
        foreach (var x in new[] { ada, bram }) { SetF(Fighter(x), "TeamGames", 10); }
        SetF(Fighter(ada), "TeamRating", 1100);
        Clear();
        Cmd(cy, "arena", "top", "team");
        Ok(cy.All().Contains("The team ladder") && cy.All().Contains("1. Ada - 1100"), "/arena top team", cy.All());

        // A bracket fight that runs out of time is decided by the blows struck; a forfeit mid-bracket.
        Reset();
        steward = P("Steward", 500, 500, 0); Admin(steward);
        var a = P("Ana", 0, 0); var b = P("Ben", 2, 0); var c = P("Cal", 4, 0); var d = P("Dov", 6, 0);
        Cmd(steward, "arena", "tourney", "open");
        foreach (var p in new[] { a, b, c, d }) Cmd(p, "arena", "tourney", "join");
        Clear();
        Cmd(a, "arena", "tourney");
        Ok(a.All().Contains("Sign-up: 4 entered"), "/arena tourney shows the sign-up", a.All());
        Cmd(steward, "arena", "tourney", "start");
        Clear();
        Cmd(a, "arena", "tourney");
        Ok(a.All().Contains("semi-final") && a.All().Contains("Ana against Dov") && a.All().Contains("Ben against Cal"), "and the bracket once it runs", a.All());
        Tick(6);
        Hit(a, d, 10); Hit(d, a, 4);
        Cmd(c, "arena", "tourney", "leave");
        Ok(b.All().Contains("You beat Cal"), "Cal withdraws mid-match: Ben goes through", b.All());
        Tick(301);
        Ok(a.All().Contains("You beat Dov"), "time ran out: Ana struck harder and goes through", a.All());
        Tick(2);
        Ok(Duel(a) != null && Duel(a) == Duel(b), "the final is called");
        Clear();
        Cmd(b, "duel", "Cal");
        Ok(b.All().Contains("You are in the running bracket") || b.All().Contains("already in a duel"), "an entrant cannot slip into other fights", b.All());
    }

    // ------------------------------------------------------------------------------------------------------------
    static void ConfigClamp()
    {
        Reset();
        NewArena(c =>
        {
            var duels = F(c, "Duels");
            SetF(duels, "RingRadius", 0f); SetF(duels, "FatalMargin", 0.1f); SetF(duels, "MaxDuelMinutes", 0);
            var tour = F(c, "Tournament");
            SetF(tour, "PrizeSplit", new List<int> { 90, 90 });
            var tav = F(c, "Tavern");
            SetF(tav, "MaxStake", 0L);
            SetF(c, "Ranked", null);
        });
        Ok((float)F(Cfg("Duels"), "RingRadius") == 4f && (float)F(Cfg("Duels"), "FatalMargin") == 1f && (int)F(Cfg("Duels"), "MaxDuelMinutes") == 1, "duel settings are clamped");
        Ok(((List<int>)F(Cfg("Tournament"), "PrizeSplit")).SequenceEqual(new[] { 70, 30 }), "a prize split over 100% falls back to 70 / 30");
        Ok((long)F(Cfg("Tavern"), "MaxStake") >= 1, "the tavern's stake bounds are clamped");
        Ok(Cfg("Ranked") != null && (int)F(Cfg("Ranked"), "StartRating") == 1000, "a missing section takes its defaults");
    }
}
