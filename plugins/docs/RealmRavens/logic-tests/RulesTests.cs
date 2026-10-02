// Logic tests for plugins/RealmRavens.cs. Not a plugin: lives outside plugins/*.cs so it is never deployed.
// run.sh compiles this file TOGETHER with the real plugin source (C# 3, against the shipped Oxide 2.0.3867 DLLs)
// and runs it on a local .NET runtime. Only RealmRavens.Rules and the data classes are exercised; no game type is
// constructed, so no game server is needed. Newtonsoft is the build shipped inside Oxide.References.dll.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Plugins;

namespace RealmRavensTests
{
    internal static class Program
    {
        private static int failed;
        private static int passed;

        private static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; System.Console.WriteLine("FAIL: " + what); }
        }

        private static readonly DateTime T0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        private static RealmRavens.PlayerInfo P()
        {
            var p = new RealmRavens.PlayerInfo();
            p.Name = "Aldric";
            p.FirstSeen = T0.AddDays(-10);
            p.LastSeen = T0;
            return p;
        }

        private static RealmRavens.SendRecord S(double minutesAgo, string target, bool anon)
        {
            var s = new RealmRavens.SendRecord();
            s.At = T0.AddMinutes(-minutesAgo);
            s.ArriveAt = s.At.AddMinutes(2);
            s.TargetKey = target;
            s.Anonymous = anon;
            return s;
        }

        private static RealmRavens.InboxLetter IL(int id, string sender, bool read, double minutesAgo)
        {
            var i = new RealmRavens.InboxLetter();
            i.LetterId = id; i.SenderId = sender; i.Read = read; i.Text = "t" + id;
            i.ArrivedAt = T0.AddMinutes(-minutesAgo);
            return i;
        }

        private static Func<double> Seq(params double[] values)
        {
            int[] at = { 0 };
            return delegate { double v = values[Math.Min(at[0], values.Length - 1)]; at[0]++; return v; };
        }

        // Spymaster of Halloran (player "sm") watching Varrow.
        private static Dictionary<string, RealmRavens.HouseIntel> Spies()
        {
            var d = new Dictionary<string, RealmRavens.HouseIntel>();
            var h = new RealmRavens.HouseIntel();
            h.House = "Halloran"; h.SpymasterId = "sm"; h.Watch = "Varrow";
            d["halloran"] = h;
            return d;
        }

        private static RealmRavens.Letter L(string senderHouse, string targetHouse, params string[] recipients)
        {
            var l = new RealmRavens.Letter();
            l.Id = 7; l.SenderId = "s1"; l.SenderName = "Berin"; l.SenderHouse = senderHouse; l.TargetHouse = targetHouse;
            l.Recipients = new List<string>(recipients); l.Text = "hello"; l.SentAt = T0;
            return l;
        }

        private static int Main()
        {
            var cfg = new RealmRavens.PluginConfig();
            RealmRavens.Rules.ClampConfig(cfg);
            TimeSpan wait;

            // --- sanitizing ---
            Check(RealmRavens.Rules.Sanitize("  hello   there \n friend ") == "hello there friend", "whitespace collapsed and trimmed");
            Check(RealmRavens.Rules.Sanitize("[FF0000]red[-]") == "(FF0000)red(-)", "colour tags neutralised");
            Check(RealmRavens.Rules.Sanitize("a\u0007b\tc") == "a b c", "control chars removed");
            Check(RealmRavens.Rules.Sanitize(null) == "", "null is empty");
            Check(RealmRavens.Rules.Sanitize("{0} {1}") == "{0} {1}", "braces kept (only single-string chat overloads are used)");
            Check(RealmRavens.Rules.Preview("abcdefghij", 6) == "abc...", "preview truncates");
            Check(RealmRavens.Rules.Preview("abc", 6) == "abc", "short preview unchanged");

            // --- travel time ---
            Check(RealmRavens.Rules.TravelSeconds(cfg, false, 0.0) == 60, "min travel");
            Check(RealmRavens.Rules.TravelSeconds(cfg, false, 0.999999) == 180, "max travel");
            Check(RealmRavens.Rules.TravelSeconds(cfg, true, 0.0) == 120, "anonymous adds extra time");
            Check(RealmRavens.Rules.TravelSeconds(cfg, false, 1.0) == 180, "roll of 1 never exceeds max");

            // --- send limits ---
            var p = P();
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:2", false, 0, cfg, out wait) == null, "fresh player may send");
            p.Sent.Add(S(0.2, "player:2", false));
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:3", false, 0, cfg, out wait) == "LimitCooldown" && wait.TotalSeconds > 0 && wait.TotalSeconds <= 30, "cooldown between ravens");
            p = P();
            for (int i = 0; i < 6; i++) p.Sent.Add(S(5 + i, "player:" + i, false));
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:x", false, 0, cfg, out wait) == "LimitHour", "hourly cap");
            Check(wait > TimeSpan.Zero && wait <= TimeSpan.FromHours(1), "hourly wait computed from oldest in the hour");
            p = P();
            for (int i = 0; i < 20; i++) p.Sent.Add(S(70 + i * 60, "player:" + i, false));
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:x", false, 0, cfg, out wait) == "LimitDay", "daily cap");
            p = P();
            for (int i = 0; i < 3; i++) p.Sent.Add(S(70 + i * 60, "house:varrow", false));
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "house:varrow", false, 0, cfg, out wait) == "LimitSameTarget", "same-target daily cap");
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "house:merrin", false, 0, cfg, out wait) == null, "other targets still allowed");
            p = P();
            p.Sent.Add(S(100, "player:1", true)); p.Sent.Add(S(200, "player:2", true));
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:3", true, 0, cfg, out wait) == "LimitAnon", "anonymous daily cap");
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:3", false, 0, cfg, out wait) == null, "signed letters unaffected by anon cap");
            p = P();
            p.Sent.Add(S(60 * 25, "player:1", false));
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:1", false, 0, cfg, out wait) == null, "records older than a day do not count");
            Check(RealmRavens.Rules.CheckSendLimits(P(), T0, "player:1", false, 5, cfg, out wait) == "LimitInFlight", "in-flight cap");
            p = P(); p.MutedUntil = T0.AddHours(2);
            Check(RealmRavens.Rules.CheckSendLimits(p, T0, "player:1", false, 0, cfg, out wait) == "Muted" && wait == TimeSpan.FromHours(2), "muted players refused");
            p = P(); p.Sent.Add(S(60 * 25, "player:1", false)); p.Sent.Add(S(1, "player:1", false));
            RealmRavens.Rules.PruneSent(p, T0);
            Check(p.Sent.Count == 1, "prune drops day-old records");

            // --- blocks ---
            var r = P();
            Check(!RealmRavens.Rules.IsBlocked(r, "s1", "Varrow", false), "nothing blocked by default");
            r.BlockedPlayers.Add("s1");
            Check(RealmRavens.Rules.IsBlocked(r, "s1", null, false), "blocked player");
            Check(RealmRavens.Rules.IsBlocked(r, "s1", null, true), "blocked player even when unsigned");
            r = P(); r.BlockedHouses.Add("varrow");
            Check(RealmRavens.Rules.IsBlocked(r, "s2", "VARROW", false), "blocked house, case-insensitive");
            Check(!RealmRavens.Rules.IsBlocked(r, "s2", "Merrin", false), "other houses pass");
            r = P(); r.BlockAnonymous = true;
            Check(RealmRavens.Rules.IsBlocked(r, "s3", null, true) && !RealmRavens.Rules.IsBlocked(r, "s3", null, false), "anon block only stops unsigned");
            Check(!RealmRavens.Rules.IsBlocked(null, "s1", null, false), "unknown recipient blocks nothing");

            // --- inbox cap ---
            var box = new List<RealmRavens.InboxLetter>();
            for (int i = 1; i <= 5; i++) box.Add(IL(i, "a", i % 2 == 0, 100 - i));
            RealmRavens.Rules.AddToInbox(box, IL(6, "a", false, 0), 5);
            Check(box.Count == 5 && RealmRavens.Rules.FindInInbox(box, 2) == null && RealmRavens.Rules.FindInInbox(box, 1) != null, "oldest READ letter dropped first");
            box.Clear();
            for (int i = 1; i <= 5; i++) box.Add(IL(i, "a", false, 100 - i));
            RealmRavens.Rules.AddToInbox(box, IL(6, "a", false, 0), 5);
            Check(box.Count == 5 && RealmRavens.Rules.FindInInbox(box, 1) == null, "all unread: oldest dropped");
            box.Clear();
            box.Add(IL(1, "a", false, 1)); box.Add(IL(2, "a", true, 1)); box.Add(IL(3, "b", false, 1));
            var copy = IL(4, "a", false, 1); copy.Intercepted = true; box.Add(copy);
            Check(RealmRavens.Rules.CountUnreadFrom(box, "a") == 1, "unread-from counts only unread, non-intercepted");
            Check(RealmRavens.Rules.CountUnreadFrom(null, "a") == 0, "null inbox counts zero");
            var flying = new List<RealmRavens.Letter>();
            flying.Add(L(null, null, "r1")); flying.Add(L(null, null, "r2", "r1"));
            Check(RealmRavens.Rules.CountInFlight(flying, "s1", "r1") == 2 && RealmRavens.Rules.CountInFlight(flying, "s1", null) == 2
                && RealmRavens.Rules.CountInFlight(flying, "s1", "r3") == 0 && RealmRavens.Rules.CountInFlight(flying, "zz", null) == 0, "in-flight counting");

            // --- interception ---
            Func<string, string> housesNow = delegate(string id) { return id == "sm" ? "Halloran" : (id == "s1" ? "Varrow" : null); };
            var spies = Spies();
            string got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.1));
            Check(got == "halloran", "letter FROM the watched house intercepted on a low roll");
            got = RealmRavens.Rules.PickInterceptor(L("Merrin", "Varrow", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.1));
            Check(got == "halloran", "letter TO the watched house intercepted");
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.9));
            Check(got == null, "high roll: not intercepted");
            got = RealmRavens.Rules.PickInterceptor(L("Ashgrove", "Merrin", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.0));
            Check(got == null, "unwatched houses never intercepted");
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Halloran", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.0));
            Check(got == null, "letters to the spymaster's own house are not 'intercepted'");
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "sm"), spies, housesNow, T0, cfg, Seq(0.0, 0.0));
            Check(got == null, "spymaster who is a recipient does not intercept");
            Func<string, string> leftHouse = delegate(string id) { return null; };
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, leftHouse, T0, cfg, Seq(0.0, 0.0));
            Check(got == null, "a spymaster who left the house has no power");
            spies["halloran"].Intercepts.Add(T0.AddMinutes(-10));
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.0));
            Check(got == null, "intercept cooldown respected");
            spies = Spies();
            for (int i = 0; i < 6; i++) spies["halloran"].Intercepts.Add(T0.AddHours(-1 - i * 2));
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, housesNow, T0, cfg, Seq(0.0, 0.0));
            Check(got == null, "daily intercept cap respected");
            var off = new RealmRavens.PluginConfig(); off.InterceptionEnabled = false;
            Check(RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), Spies(), housesNow, T0, off, Seq(0.0, 0.0)) == null, "interception switch");
            spies = Spies();
            var two = new RealmRavens.HouseIntel(); two.House = "Dunmere"; two.SpymasterId = "sm2"; two.Watch = "varrow";
            spies["dunmere"] = two;
            Func<string, string> both = delegate(string id) { return id == "sm" ? "Halloran" : (id == "sm2" ? "Dunmere" : null); };
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, both, T0, cfg, Seq(0.0, 0.9, 0.1));
            Check(got == "halloran", "second candidate tried when the first fails its roll (sorted, rotated)");
            got = RealmRavens.Rules.PickInterceptor(L("Varrow", "Merrin", "r1"), spies, both, T0, cfg, Seq(0.0, 0.9, 0.9));
            Check(got == null, "both fail: not intercepted");

            // --- spies ---
            var spy = P();
            var own = new RealmRavens.HouseIntel(); own.House = "Halloran";
            Check(RealmRavens.Rules.CheckSpy(spy, own, "varrow", T0, cfg, out wait) == null, "spy may act");
            spy.LastSpyAt = T0.AddHours(-2);
            Check(RealmRavens.Rules.CheckSpy(spy, own, "varrow", T0, cfg, out wait) == "SpyCooldown" && wait == TimeSpan.FromHours(10), "spy cooldown");
            spy.LastSpyAt = T0.AddHours(-13);
            own.SpiedTargets["varrow"] = T0.AddHours(-5);
            Check(RealmRavens.Rules.CheckSpy(spy, own, "varrow", T0, cfg, out wait) == "SpyTargetCooldown", "house-wide same-target cooldown");
            Check(RealmRavens.Rules.CheckSpy(spy, own, "merrin", T0, cfg, out wait) == null, "other targets allowed");

            var traffic = new List<RealmRavens.TrafficRecord>();
            string[] tos = { "Merrin", "Merrin", null, "Ashgrove" };
            foreach (string to in tos) { var t = new RealmRavens.TrafficRecord(); t.At = T0.AddHours(-1); t.FromHouse = "Varrow"; t.ToHouse = to; traffic.Add(t); }
            var old = new RealmRavens.TrafficRecord(); old.At = T0.AddHours(-30); old.FromHouse = "Varrow"; old.ToHouse = "Merrin"; traffic.Add(old);
            var other = new RealmRavens.TrafficRecord(); other.At = T0.AddHours(-1); other.FromHouse = "Merrin"; other.ToHouse = "Varrow"; traffic.Add(other);
            int total;
            Dictionary<string, int> by = RealmRavens.Rules.TrafficFrom(traffic, "varrow", T0.AddHours(-24), out total);
            Check(total == 4 && by["Merrin"] == 2 && by[""] == 1 && by["Ashgrove"] == 1, "traffic counts in window per destination");

            // --- rumours ---
            var rumours = new List<RealmRavens.Rumour>();
            var rp = P();
            Check(RealmRavens.Rules.CheckRumour(rp, rumours, T0, cfg, out wait) == null, "rumour allowed");
            rp.FirstSeen = T0.AddMinutes(-5);
            Check(RealmRavens.Rules.CheckRumour(rp, rumours, T0, cfg, out wait) == "TooNew" && wait == TimeSpan.FromMinutes(25), "new players wait");
            rp = P(); rp.LastRumourAt = T0.AddMinutes(-30);
            Check(RealmRavens.Rules.CheckRumour(rp, rumours, T0, cfg, out wait) == "RumourCooldown", "rumour cooldown");
            rp = P();
            for (int i = 0; i < 25; i++) { var ru = new RealmRavens.Rumour(); ru.Id = i; ru.Text = "x"; ru.Status = RealmRavens.RPending; ru.SubmitterId = "o" + i; rumours.Add(ru); }
            Check(RealmRavens.Rules.CheckRumour(rp, rumours, T0, cfg, out wait) == "RumourQueueFull", "queue cap");
            Check(RealmRavens.Rules.PendingRumours(rumours, "o3") == 1 && RealmRavens.Rules.PendingRumours(rumours, null) == 25, "pending counts");
            var words = new List<string>(); words.Add(" Slur ");
            Check(RealmRavens.Rules.ContainsBlockedWord("what a SLUR this is", words), "blocked word, case-insensitive, trimmed");
            Check(!RealmRavens.Rules.ContainsBlockedWord("clean text", words), "clean text passes");

            // --- name matching ---
            var names = new Dictionary<string, string>();
            names["1"] = "Aldric"; names["2"] = "Aldo"; names["3"] = "Berin"; names["4"] = "aldric the bold";
            List<string> m = RealmRavens.Rules.MatchNames(names, "ALDRIC");
            Check(m.Count == 1 && m[0] == "1", "exact match wins over prefix");
            m = RealmRavens.Rules.MatchNames(names, "ald");
            Check(m.Count == 3, "ambiguous prefix returns all");
            m = RealmRavens.Rules.MatchNames(names, "ber");
            Check(m.Count == 1 && m[0] == "3", "unique prefix");
            Check(RealmRavens.Rules.MatchNames(names, "  ").Count == 0, "blank matches nothing");

            // --- config clamps ---
            var bad = new RealmRavens.PluginConfig();
            bad.AuditRetentionDays = 30; bad.InterceptChance = 5; bad.SpyCaughtChance = -1; bad.TravelSecondsMin = 500; bad.TravelSecondsMax = 10;
            bad.MaxLettersPerDay = 1; bad.MaxLettersPerHour = 6; bad.RumourBlockedWords = null; bad.MinLetterLength = 9999;
            RealmRavens.Rules.ClampConfig(bad);
            Check(bad.AuditRetentionDays == 7, "audit retention can never exceed 7 days");
            Check(bad.InterceptChance == 1 && bad.SpyCaughtChance == 0, "chances clamped to [0,1]");
            Check(bad.TravelSecondsMax == 500, "max travel raised to min");
            Check(bad.MaxLettersPerDay == 6, "daily cap at least the hourly cap");
            Check(bad.RumourBlockedWords != null, "null word list repaired");
            Check(bad.MinLetterLength == bad.MaxLetterLength, "min length at most max length");

            // --- retention ---
            var s = new RealmRavens.StoredData();
            var a1 = new RealmRavens.AuditEntry(); a1.Kind = "letter_sent"; a1.At = T0.AddDays(-8); s.Audit.Add(a1);
            var a2 = new RealmRavens.AuditEntry(); a2.Kind = "letter_sent"; a2.At = T0.AddDays(-6); s.Audit.Add(a2);
            var rep = new RealmRavens.Report(); rep.At = T0.AddDays(-8); s.Reports.Add(rep);
            var ib = new List<RealmRavens.InboxLetter>(); ib.Add(IL(1, "a", true, 60 * 24 * 8)); s.Inboxes["p1"] = ib;
            var ib2 = new List<RealmRavens.InboxLetter>(); ib2.Add(IL(2, "a", true, 60 * 24 * 8)); ib2.Add(IL(3, "a", false, 10)); s.Inboxes["p2"] = ib2;
            var pend = new RealmRavens.Rumour(); pend.Text = "x"; pend.Status = RealmRavens.RPending; pend.SubmittedAt = T0.AddHours(-49); pend.SubmitterId = "p1"; s.Rumours.Add(pend);
            var appr = new RealmRavens.Rumour(); appr.Text = "y"; appr.Status = RealmRavens.RApproved; appr.DecidedAt = T0.AddDays(-9); appr.SubmitterId = "p1"; appr.SubmitterName = "A"; s.Rumours.Add(appr);
            var rej = new RealmRavens.Rumour(); rej.Text = "z"; rej.Status = RealmRavens.RRejected; rej.DecidedAt = T0.AddDays(-8); s.Rumours.Add(rej);
            var idle = P(); idle.LastSeen = T0.AddDays(-61); s.Players["gone"] = idle;
            var blocker = P(); blocker.LastSeen = T0.AddDays(-61); blocker.BlockAnonymous = true; s.Players["keep"] = blocker;
            var intel = new RealmRavens.HouseIntel(); intel.House = "Halloran"; intel.Intercepts.Add(T0.AddDays(-3)); intel.Intercepts.Add(T0.AddHours(-1));
            intel.SpiedTargets["varrow"] = T0.AddDays(-2); intel.SpiedTargets["merrin"] = T0.AddHours(-1); s.Houses["halloran"] = intel;
            RealmRavens.Rules.Prune(s, T0, cfg, null);
            Check(s.Audit.Count == 1 && s.Audit[0] == a2, "audit older than 7 days removed");
            Check(s.Reports.Count == 0, "reports older than retention removed");
            Check(!s.Inboxes.ContainsKey("p1") && s.Inboxes["p2"].Count == 1, "old letters pruned, empty inbox removed");
            Check(pend.Status == RealmRavens.RExpired && pend.DecidedAt == T0, "stale pending rumour expires");
            Check(s.Rumours.Contains(appr) && appr.SubmitterId == null && appr.SubmitterName == null, "approved rumour kept but anonymised after retention");
            Check(!s.Rumours.Contains(rej), "old rejected rumour removed");
            Check(!s.Players.ContainsKey("gone") && s.Players.ContainsKey("keep"), "idle player pruned, player with a block kept");
            Check(intel.Intercepts.Count == 1 && intel.SpiedTargets.Count == 1 && intel.SpiedTargets.ContainsKey("merrin"), "intel timers pruned");
            s = new RealmRavens.StoredData();
            for (int i = 0; i < 60; i++) { var ar = new RealmRavens.Rumour(); ar.Id = i; ar.Text = "r" + i; ar.Status = RealmRavens.RApproved; ar.DecidedAt = T0; s.Rumours.Add(ar); }
            RealmRavens.Rules.Prune(s, T0, cfg, null);
            Check(s.Rumours.Count == 50 && s.Rumours[0].Id == 10 && s.Rumours[49].Id == 59, "approved rumours capped at 50, oldest dropped");
            Check(RealmRavens.Rules.Duration(TimeSpan.FromSeconds(0.2)) == "1s" && RealmRavens.Rules.Duration(TimeSpan.FromMinutes(90)) == "1h 30m"
                && RealmRavens.Rules.Duration(TimeSpan.FromHours(30)) == "1d 6h" && RealmRavens.Rules.Duration(TimeSpan.FromMinutes(-5)) == "1s", "durations");

            // --- normalize ---
            s = new RealmRavens.StoredData();
            s.InFlight = null; s.Inboxes = null; s.Players = null; s.Houses = null; s.Rumours = null; s.Audit = null; s.Reports = null; s.Traffic = null;
            RealmRavens.Rules.Normalize(s);
            Check(s.InFlight != null && s.Inboxes != null && s.Players != null && s.Houses != null && s.Rumours != null
                && s.Audit != null && s.Reports != null && s.Traffic != null, "null collections restored");
            s = new RealmRavens.StoredData();
            var fl = L("Varrow", null, "r1"); fl.Id = 41; fl.Recipients = null; s.InFlight.Add(fl); s.InFlight.Add(null);
            s.Inboxes["x"] = null; var ibx = new List<RealmRavens.InboxLetter>(); ibx.Add(IL(90, "a", false, 1)); ibx.Add(null); s.Inboxes["y"] = ibx;
            s.Players["n"] = null; var pp = P(); pp.Sent = null; pp.BlockedPlayers = null; pp.Notices = null; s.Players["ok"] = pp;
            s.Houses["bad"] = null; var hh = new RealmRavens.HouseIntel(); hh.House = "Merrin"; hh.Spies = null; hh.SpiedTargets = null; s.Houses["merrin"] = hh;
            var ru2 = new RealmRavens.Rumour(); ru2.Id = 12; ru2.Text = "q"; ru2.Status = RealmRavens.RPending; s.Rumours.Add(ru2); s.Rumours.Add(null);
            RealmRavens.Rules.Normalize(s);
            Check(s.InFlight.Count == 1 && fl.Recipients != null, "broken in-flight rows dropped, recipients repaired");
            Check(s.NextLetterId == 91, "letter id counter continues past every stored id");
            Check(!s.Inboxes.ContainsKey("x") && s.Inboxes["y"].Count == 1, "null inboxes/rows dropped");
            Check(!s.Players.ContainsKey("n") && pp.Sent != null && pp.BlockedPlayers != null && pp.Notices != null, "player rows repaired");
            Check(!s.Houses.ContainsKey("bad") && hh.Spies != null && hh.SpiedTargets != null, "house intel repaired");
            Check(s.Rumours.Count == 1 && s.NextRumourId == 13, "rumour ids continue");
            s = new RealmRavens.StoredData();
            var hp = P(); hp.BlockedPlayers.Add("anon1"); hp.HiddenBlocks.Add("anon1"); hp.HiddenBlocks.Add("stale"); s.Players["h"] = hp;
            var np = P(); np.HiddenBlocks = null; s.Players["n2"] = np;
            RealmRavens.Rules.Normalize(s);
            Check(hp.HiddenBlocks.Count == 1 && hp.HiddenBlocks[0] == "anon1" && np.HiddenBlocks != null, "hidden blocks repaired; entries without a block dropped");

            // --- JSON round trip and corrupt files, with the Newtonsoft that Oxide ships ---
            s = new RealmRavens.StoredData();
            var inflight = L("Varrow", "Merrin", "r1", "r2"); inflight.Anonymous = true; inflight.ArriveAt = T0.AddMinutes(2); s.InFlight.Add(inflight);
            s.NextLetterId = 8;
            var bx = new List<RealmRavens.InboxLetter>(); bx.Add(IL(3, "s1", false, 1)); s.Inboxes["r1"] = bx;
            var info = P(); info.BlockedHouses.Add("varrow"); info.Sent.Add(S(1, "player:r1", true)); s.Players["s1"] = info;
            s.Houses = Spies();
            string json = JsonConvert.SerializeObject(s, Formatting.Indented);
            var back = JsonConvert.DeserializeObject<RealmRavens.StoredData>(json);
            RealmRavens.Rules.Normalize(back);
            Check(back.InFlight.Count == 1 && back.InFlight[0].Recipients.Count == 2 && back.InFlight[0].Anonymous, "round trip keeps letters in flight");
            Check(back.InFlight[0].ArriveAt == T0.AddMinutes(2) && back.InFlight[0].ArriveAt.Kind == DateTimeKind.Utc, "round trip keeps UTC arrival times");
            Check(back.Inboxes["r1"].Count == 1 && back.Players["s1"].BlockedHouses[0] == "varrow", "round trip keeps inbox and blocks");
            Check(back.Houses["halloran"].Watch == "Varrow" && back.NextLetterId == 8, "round trip keeps intel and counters");
            Check(JsonConvert.DeserializeObject<RealmRavens.StoredData>("") == null,
                "an empty file deserializes to null (LoadData refuses it and never writes)");
            bool threw = false;
            try { JsonConvert.DeserializeObject<RealmRavens.StoredData>(json.Substring(0, json.Length / 2)); }
            catch (Exception) { threw = true; }
            Check(threw, "a truncated file throws (LoadData sets loadFailed and never writes)");
            var cfgBack = JsonConvert.DeserializeObject<RealmRavens.PluginConfig>(JsonConvert.SerializeObject(cfg));
            Check(cfgBack.MaxLettersPerHour == 6 && cfgBack.AuditRetentionDays == 7 && cfgBack.RumourBlockedWords.Count == 0, "config round trip (no list duplication)");

            System.Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
