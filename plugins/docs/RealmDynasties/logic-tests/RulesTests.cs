// Logic tests for plugins/RealmDynasties.cs. Not a plugin: lives outside plugins/*.cs so it is never deployed.
// run.sh compiles this file TOGETHER with the real plugin source (C# 3, against the shipped Oxide 2.0.3867 DLLs)
// and runs it on a local .NET runtime. Only RealmDynasties.Rules and the data classes are exercised; no game
// type is constructed, so no game server is needed. Newtonsoft is the build shipped inside Oxide.References.dll.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Plugins;

namespace RealmDynastiesTests
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

        private static RealmDynasties.Member M(string id, string name, string parent, int gen, double seenDaysAgo)
        {
            var m = new RealmDynasties.Member();
            m.Id = id; m.Name = name; m.ParentId = parent; m.Generation = gen;
            m.Joined = T0.AddDays(-30).AddMinutes(int.Parse(id));
            m.LastSeen = T0.AddDays(-seenDaysAgo);
            return m;
        }

        // Founder 1 (head) with heirs 2, 3, 4 (children of 1); 5 is a child of 2 (kin, not in line).
        private static RealmDynasties.Dynasty Sample()
        {
            var d = new RealmDynasties.Dynasty();
            d.Name = "Varrow";
            d.HeadId = "1";
            d.HeadSince = T0.AddDays(-30);
            d.Members.Add(M("1", "Aldric", null, 1, 20));
            d.Members.Add(M("2", "Berin", "1", 2, 16));
            d.Members.Add(M("3", "Cyra", "1", 2, 1));
            d.Members.Add(M("4", "Doran", "1", 2, 0));
            d.Members.Add(M("5", "Edda", "2", 3, 0));
            d.Line.Add("2"); d.Line.Add("3"); d.Line.Add("4");
            return d;
        }

        private static int Main()
        {
            var cfg = new RealmDynasties.PluginConfig();

            // --- names ---
            Check(RealmDynasties.Rules.ValidName("Varrow", 3, 24), "plain name valid");
            Check(RealmDynasties.Rules.ValidName("Iron Stag of the Hill", 3, 24), "spaced name valid");
            Check(RealmDynasties.Rules.ValidName("Ash-grove's", 3, 24), "hyphen and apostrophe valid");
            Check(!RealmDynasties.Rules.ValidName("Va", 3, 24), "too short rejected");
            Check(!RealmDynasties.Rules.ValidName("1Varrow", 3, 24), "leading digit rejected");
            Check(!RealmDynasties.Rules.ValidName("Var  row", 3, 24), "double space rejected");
            Check(!RealmDynasties.Rules.ValidName("Varrow ", 3, 24), "trailing space rejected");
            Check(!RealmDynasties.Rules.ValidName("Var{0}row", 3, 24), "braces rejected");
            Check(!RealmDynasties.Rules.ValidName("[FF0000]Red", 3, 24), "colour tags rejected");
            Check(!RealmDynasties.Rules.ValidName(null, 3, 24), "null rejected");
            Check(!RealmDynasties.Rules.ValidName(new string('a', 25), 3, 24), "too long rejected");

            // --- inactivity and successor choice ---
            var d = Sample();
            Predicate<string> nobodyOnline = delegate(string id) { return false; };
            Predicate<string> berinOnline = delegate(string id) { return id == "2"; };
            Check(RealmDynasties.Rules.IsInactive(d.Members[0], T0, 14, false), "head unseen 20 days is inactive");
            Check(!RealmDynasties.Rules.IsInactive(d.Members[0], T0, 14, true), "online is never inactive");
            Check(!RealmDynasties.Rules.IsInactive(d.Members[2], T0, 14, false), "seen yesterday is active");
            RealmDynasties.Member next = RealmDynasties.Rules.PickSuccessor(d, T0, 14, nobodyOnline, true);
            Check(next != null && next.Id == "3", "inactive heir 1 skipped, heir 2 chosen");
            next = RealmDynasties.Rules.PickSuccessor(d, T0, 14, berinOnline, true);
            Check(next != null && next.Id == "2", "heir 1 chosen when online");
            next = RealmDynasties.Rules.PickSuccessor(d, T0, 14, nobodyOnline, false);
            Check(next != null && next.Id == "2", "admin pass ignores activity");
            var lonely = Sample();
            lonely.Line.Clear(); lonely.Line.Add("2");
            Check(RealmDynasties.Rules.PickSuccessor(lonely, T0, 14, nobodyOnline, true) == null, "no active heir -> null (dormant)");
            var dangling = Sample();
            dangling.Line.Insert(0, "99");
            next = RealmDynasties.Rules.PickSuccessor(dangling, T0, 14, nobodyOnline, true);
            Check(next != null && next.Id == "3", "dangling id in line skipped");

            // --- succession ---
            d = Sample();
            RealmDynasties.Member cyra = RealmDynasties.Rules.Find(d, "3");
            RealmDynasties.Member old = RealmDynasties.Rules.ApplySuccession(d, cyra, T0, 24, 10);
            Check(old != null && old.Id == "1" && old.WasHead, "old head returned and marked elder");
            Check(d.HeadId == "3" && d.HeadSince == T0, "new head set");
            Check(!d.Line.Contains("3") && d.Line.Count == 2 && d.Line[0] == "2" && d.Line[1] == "4", "new head leaves the line, order kept");
            Check(d.MaxGenerationCounted == 2, "generation 2 credited after a long headship");
            Check(d.Successions == 1 && d.LastSuccessionAt == T0, "succession counted");
            Check(RealmDynasties.Rules.RoleOf(d, old) == "elder", "role elder");
            Check(RealmDynasties.Rules.RoleOf(d, cyra) == "head", "role head");
            Check(RealmDynasties.Rules.RoleOf(d, RealmDynasties.Rules.Find(d, "4")) == "heir 2", "role heir 2");
            Check(RealmDynasties.Rules.RoleOf(d, RealmDynasties.Rules.Find(d, "5")) == "kin", "role kin");

            // quick abdication chain earns no generation
            d = Sample();
            d.HeadSince = T0.AddHours(-2);
            RealmDynasties.Rules.ApplySuccession(d, RealmDynasties.Rules.Find(d, "2"), T0, 24, 10);
            Check(d.MaxGenerationCounted == 1, "no generation credit after a 2 h headship");
            // generation credit is capped
            d = Sample();
            RealmDynasties.Rules.Find(d, "2").Generation = 40;
            RealmDynasties.Rules.ApplySuccession(d, RealmDynasties.Rules.Find(d, "2"), T0, 24, 10);
            Check(d.MaxGenerationCounted == 10, "generation credit capped");

            // --- membership edits ---
            d = Sample();
            Check(!RealmDynasties.Rules.RemoveMember(d, "1"), "head cannot be removed");
            Check(RealmDynasties.Rules.RemoveMember(d, "2"), "member removed");
            Check(RealmDynasties.Rules.Find(d, "5").ParentId == "1", "child re-parented to grandparent");
            Check(!d.Line.Contains("2"), "removed member leaves the line");
            Check(!RealmDynasties.Rules.RemoveMember(d, "2"), "second removal is a no-op");
            d = Sample();
            Check(RealmDynasties.Rules.MoveInLine(d, "4", 1) && d.Line[0] == "4" && d.Line[1] == "2", "move to front");
            Check(RealmDynasties.Rules.MoveInLine(d, "4", 99) && d.Line[2] == "4", "position clamped to the end");
            Check(!RealmDynasties.Rules.MoveInLine(d, "5", 1), "kin cannot be moved in the line");
            Check(RealmDynasties.Rules.FindByName(d, "cy").Id == "3", "prefix match");
            Check(RealmDynasties.Rules.FindByName(d, "ALDRIC").Id == "1", "case-insensitive exact match");
            d.Members.Add(M("6", "Cyrus", "1", 2, 0));
            Check(RealmDynasties.Rules.FindByName(d, "cy") == null, "ambiguous prefix -> null");

            // --- prestige ---
            d = Sample();
            Check(RealmDynasties.Rules.Prestige(d, cfg) == 0, "fresh line has 0 prestige");
            d.ReignsHeld = 2; d.ReignHours = 10; d.OathKeptHours = 48; d.Titles.Add("Warden of the Hill Road");
            d.MaxGenerationCounted = 3; d.Restorations = 1;
            // 2*100 + 10*2 + 2*5 + 25 + 2*30 + 150 = 465
            Check(RealmDynasties.Rules.Prestige(d, cfg) == 465, "prestige formula (" + RealmDynasties.Rules.Prestige(d, cfg) + ")");
            d.OathsBroken = 1; d.TreatiesBroken = 2;
            Check(RealmDynasties.Rules.Prestige(d, cfg) == 465 - 75 - 50, "broken oaths and treaties subtract");
            d.ReignHours = 100000; d.OathKeptHours = 1000000;
            Check(RealmDynasties.Rules.Prestige(d, cfg) == 200 + 1000 + 1825 + 25 + 60 + 150 - 125, "reign hours and oath days capped");
            d = Sample(); d.OathsBroken = 50;
            Check(RealmDynasties.Rules.Prestige(d, cfg) == 0, "prestige never negative");
            d.PrestigeAdjust = 10000;
            Check(RealmDynasties.Rules.Prestige(d, cfg) == 10000 - 3750, "admin adjustment applies");

            // --- family tree ---
            d = Sample();
            List<string> tree = RealmDynasties.Rules.Tree(d, 30);
            Check(tree.Count == 5, "tree lists everyone (" + tree.Count + ")");
            Check(tree[0] == "Aldric [head, gen 1]", "root line: " + tree[0]);
            Check(tree[1] == "  - Berin [heir 1, gen 2]", "child line: " + tree[1]);
            Check(tree[2] == "    - Edda [kin, gen 3]", "grandchild under its parent: " + tree[2]);
            Check(tree[4] == "  - Doran [heir 3, gen 2]", "last child: " + tree[4]);
            List<string> small = RealmDynasties.Rules.Tree(d, 3);
            Check(small.Count == 3 && small[2] == "... and 3 more", "tree capped with a remainder line");
            var cyc = Sample();
            RealmDynasties.Rules.Find(cyc, "1").ParentId = "5";       // corrupt: a parent cycle with no root
            List<string> cycTree = RealmDynasties.Rules.Tree(cyc, 30);
            Check(cycTree.Count == 5, "cycle does not hang and lists everyone once (" + cycTree.Count + ")");

            // --- downtime freeze ---
            var s = new RealmDynasties.StoredData();
            d = Sample();
            d.Blood = new RealmDynasties.BloodRight();
            d.Blood.Status = RealmDynasties.BloodOpen; d.Blood.FallenKingName = "Aldric"; d.Blood.ExpiresAt = T0.AddHours(10);
            s.Dynasties.Add(d);
            RealmDynasties.Rules.ShiftForDowntime(s, TimeSpan.FromDays(3), T0);
            Check(RealmDynasties.Rules.Find(d, "1").LastSeen == T0.AddDays(-17), "last seen shifted by the downtime");
            Check(RealmDynasties.Rules.Find(d, "4").LastSeen == T0, "never shifted past now");
            Check(d.Blood.ExpiresAt == T0.AddHours(82), "open claim deadline extended");
            d.Blood.Status = RealmDynasties.BloodLinked;
            RealmDynasties.Rules.ShiftForDowntime(s, TimeSpan.FromDays(1), T0);
            Check(d.Blood.ExpiresAt == T0.AddHours(82), "linked claim follows the rebellion window, not shifted");

            // --- CrownAndConsequences GetOpenClaims parsing (format: house|status|start.ToString("o")|end.ToString("o")) ---
            DateTime ws = new DateTime(2026, 10, 7, 19, 0, 0, DateTimeKind.Utc), we = ws.AddHours(2);
            string[] raw = { "Corvane|pending|" + ws.AddDays(1).ToString("o") + "|" + we.AddDays(1).ToString("o"),
                             "broken line", null, "Varrow|active|" + ws.ToString("o") + "|" + we.ToString("o") };
            string st; DateTime a, b;
            Check(RealmDynasties.Rules.ParseClaim(raw, "varrow", out st, out a, out b), "claim found case-insensitively");
            Check(st == "active" && a == ws && b == we && a.Kind == DateTimeKind.Utc, "claim window parsed as UTC");
            Check(!RealmDynasties.Rules.ParseClaim(raw, "Merrin", out st, out a, out b), "other house not matched");
            Check(!RealmDynasties.Rules.ParseClaim(null, "Varrow", out st, out a, out b), "null list tolerated");
            Check(!RealmDynasties.Rules.ParseClaim(new string[] { "Varrow|active|nonsense|x" }, "Varrow", out st, out a, out b), "bad dates skipped");

            // --- accrual, reign recount, usurpation, restoration ---
            Check(RealmDynasties.Rules.Accrue(1, T0.AddMinutes(-1), T0, 0.05) > 1.016 && RealmDynasties.Rules.Accrue(1, T0.AddMinutes(-1), T0, 0.05) < 1.017, "accrue one minute");
            Check(RealmDynasties.Rules.Accrue(1, T0.AddDays(-5), T0, 0.05) == 1.05, "accrual capped per step");
            Check(RealmDynasties.Rules.Accrue(1, T0.AddDays(1), T0, 0.05) == 1, "clock going back adds nothing");
            d = Sample();
            Check(RealmDynasties.Rules.CountsAsNewReign(d, T0, 24), "first reign counts");
            d.LastReignCountedAt = T0.AddHours(-3);
            Check(!RealmDynasties.Rules.CountsAsNewReign(d, T0, 24), "re-sitting within 24 h is the same reign");
            d.LastReignCountedAt = T0.AddHours(-25);
            Check(RealmDynasties.Rules.CountsAsNewReign(d, T0, 24), "after the cooldown it is a new reign");
            s = new RealmDynasties.StoredData();
            var x = Sample(); var y = Sample(); y.Name = "Dunmere";
            x.Blood = new RealmDynasties.BloodRight(); x.Blood.Status = RealmDynasties.BloodAwaiting; x.Blood.FallenKingName = "Aldric";
            s.Dynasties.Add(x); s.Dynasties.Add(y);
            RealmDynasties.Rules.MarkUsurped(s, x);
            Check(!x.Blood.Usurped, "own line regaining the throne is not usurpation");
            Check(!RealmDynasties.Rules.EarnsRestoration(x.Blood, x, T0, 72), "no restoration without an outsider's reign");
            RealmDynasties.Rules.MarkUsurped(s, null);
            Check(x.Blood.Usurped, "an outsider (or no line) reigning marks usurpation");
            Check(RealmDynasties.Rules.EarnsRestoration(x.Blood, x, T0, 72), "pressed claim + usurper earns restoration");
            x.LastRestorationAt = T0.AddHours(-10);
            Check(!RealmDynasties.Rules.EarnsRestoration(x.Blood, x, T0, 72), "restoration cooldown blocks a quick repeat");
            x.LastRestorationAt = T0.AddHours(-73);
            Check(RealmDynasties.Rules.EarnsRestoration(x.Blood, x, T0, 72), "restoration allowed after the cooldown");
            x.Blood.Status = RealmDynasties.BloodOpen;
            Check(!RealmDynasties.Rules.EarnsRestoration(x.Blood, x, T0, 72), "unpressed claim earns nothing");
            var hist = Sample();
            for (int i = 0; i < 40; i++) RealmDynasties.Rules.AddHistory(hist, "e" + i, T0, 25);
            Check(hist.History.Count == 25 && hist.History[24].EndsWith("e39"), "history capped, newest kept");

            // --- normalize (repairs a hand-edited or partial file) ---
            s = new RealmDynasties.StoredData();
            s.Dynasties = null; s.LastFounded = null; s.Reign = null;
            RealmDynasties.Rules.Normalize(s, T0);
            Check(s.Dynasties != null && s.LastFounded != null && s.RejoinBlockedUntil != null && s.Reign != null, "null collections restored");
            s = new RealmDynasties.StoredData();
            var n1 = Sample();
            var n2 = Sample(); n2.Name = "VARROW";                     // duplicate name
            var n3 = Sample(); n3.Name = "Merrin"; n3.Members.RemoveRange(1, 4); n3.Members[0].Id = "2"; n3.HeadId = "2"; n3.Line.Clear();
            var n4 = new RealmDynasties.Dynasty(); n4.Name = "Empty";
            var n5 = Sample(); n5.Name = "Halloran";
            foreach (RealmDynasties.Member m in n5.Members) m.Id = "h" + m.Id;
            foreach (RealmDynasties.Member m in n5.Members) if (m.ParentId != null) m.ParentId = "h" + m.ParentId;
            n5.HeadId = "nobody"; n5.Line.Clear(); n5.Line.Add("h2"); n5.Line.Add("h2"); n5.Line.Add("ghost"); n5.Line.Add("h1");
            n5.Members[1].LastSeen = default(DateTime);
            n5.Members[2].ParentId = "ghost";
            n5.Titles.Add(null); n5.Titles.Add("");
            s.Dynasties.Add(n1); s.Dynasties.Add(n2); s.Dynasties.Add(n3); s.Dynasties.Add(n4); s.Dynasties.Add(null); s.Dynasties.Add(n5);
            RealmDynasties.Rules.Normalize(s, T0);
            Check(s.Dynasties.Count == 2 && s.Dynasties[0] == n1 && s.Dynasties[1] == n5,
                "duplicate name, member-less, null and fully-claimed lines dropped (" + s.Dynasties.Count + ")");
            Check(n5.HeadId == "h1", "missing head repaired to first member");
            Check(n5.Line.Count == 1 && n5.Line[0] == "h2", "line de-duplicated, ghosts and head removed");
            Check(n5.Members[1].LastSeen == T0, "missing last-seen set to now, not treated as long gone");
            Check(n5.Members[2].ParentId == null, "dangling parent cleared");
            Check(n5.Titles.Count == 0, "empty titles removed");

            // --- JSON round trip and corrupt files, with the Newtonsoft that Oxide ships ---
            s = new RealmDynasties.StoredData();
            d = Sample();
            d.Titles.Add("Warden of the Hill Road");
            d.Blood = new RealmDynasties.BloodRight();
            d.Blood.Status = RealmDynasties.BloodLinked; d.Blood.FallenKingName = "Aldric"; d.Blood.WindowStart = ws; d.Blood.WindowEnd = we;
            s.Dynasties.Add(d);
            s.LastTickAt = T0;
            s.Reign.KingId = "76561190000000001"; s.Reign.Dynasty = "Varrow"; s.Reign.Since = T0;
            string json = JsonConvert.SerializeObject(s, Formatting.Indented);
            var back = JsonConvert.DeserializeObject<RealmDynasties.StoredData>(json);
            RealmDynasties.Rules.Normalize(back, T0);
            Check(back.Dynasties.Count == 1 && back.Dynasties[0].Members.Count == 5 && back.Dynasties[0].Line.Count == 3, "round trip keeps members and line");
            Check(back.Dynasties[0].Blood != null && back.Dynasties[0].Blood.WindowStart == ws, "round trip keeps the blood claim");
            Check(back.LastTickAt == T0 && back.LastTickAt.Kind == DateTimeKind.Utc, "round trip keeps UTC times");
            Check(back.Reign.KingId == "76561190000000001", "round trip keeps the reign");
            Check(RealmDynasties.Rules.Prestige(back.Dynasties[0], cfg) == RealmDynasties.Rules.Prestige(d, cfg), "prestige identical after round trip");
            Check(JsonConvert.DeserializeObject<RealmDynasties.StoredData>("") == null,
                "an empty file deserializes to null (the plugin must refuse it, which LoadData does)");
            bool threw = false;
            try { JsonConvert.DeserializeObject<RealmDynasties.StoredData>(json.Substring(0, json.Length / 2)); }
            catch (Exception) { threw = true; }
            Check(threw, "a truncated file throws (LoadData rethrows and never writes)");
            var cfgBack = JsonConvert.DeserializeObject<RealmDynasties.PluginConfig>(JsonConvert.SerializeObject(cfg));
            Check(cfgBack.InactiveDaysForSuccession == 14 && cfgBack.MaxHeirsInLine == 8, "config round trip");

            System.Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
