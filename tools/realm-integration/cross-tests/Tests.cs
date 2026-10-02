// Cross-plugin behaviour test: plugins/RealmLaws.cs and plugins/RealmContracts.cs compiled UNCHANGED together, with
// Plugin.Call routed the way Oxide 2.0.3867 routes it (by name, to NON-PUBLIC instance methods only). Proves that a
// court sentence of outlawry reaches RealmContracts, makes the outlaw a bounty target, and that a court pardon lifts
// it and refunds the escrowed bounty. Run with run.sh. Mocks, not the game: nothing here proves in-game behaviour.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CodeHatch.Engine.Modules.SocialSystem;
using CodeHatch.Engine.Networking;
using CodeHatch.Thrones.SocialSystem;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;

static class T
{
    static int pass, fail;
    const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }

    static object Inv(object o, string m, params object[] a)
    {
        var mi = o.GetType().GetMethods(Any).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        return mi.Invoke(o, a);
    }
    static object F(object o, string f) { return o.GetType().GetField(f, Any).GetValue(o); }
    static void SetF(object o, string f, object v) { o.GetType().GetField(f, Any).SetValue(o, v); }

    // Oxide's CSharpPlugin registers only NonPublic|Instance methods as hooks; Call finds one by name and arity.
    static void RouteLikeOxide(Plugin target)
    {
        target.Handler = (hook, args) =>
        {
            var mi = target.GetType().GetMethods(NonPublicInstance)
                .FirstOrDefault(x => x.Name == hook && x.GetParameters().Length == args.Length);
            return mi == null ? null : mi.Invoke(target, args);
        };
    }

    static Plugin Stub(string name) { return new Plugin { Name = name }; }

    static void Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "realm-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Interface.Oxide.DataFileSystem.Dir = dir;
        try { Run(); }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    static void Run()
    {
        var kings = new KingsScheme { King = 1, KingName = "Queen Ysolde" };
        SocialAPI.Registry[typeof(KingsScheme)] = kings;
        SocialAPI.Registry[typeof(GuildScheme)] = new GuildScheme();
        var queen = new Player(1, "Queen Ysolde");
        var poster = new Player(2, "Bram");
        var accused = new Player(3, "Corwen");
        var other = new Player(4, "Dalla");
        foreach (var p in new[] { queen, poster, accused, other }) Server.ClientPlayers.Add(p);
        poster.Inventory.Contents.Counts["Wood"] = 40;

        var chron = Stub("RealmChronicle");
        var chronLog = new List<string>();
        chron.Handler = (hook, args) => { if (hook == "Log") { chronLog.Add((string)args[0]); return 1; } return null; };

        var contracts = new RealmContracts();
        Inv(contracts, "LoadDefaultConfig");
        Inv(contracts, "LoadDefaultMessages");
        SetF(contracts, "RealmChronicle", chron);
        SetF(contracts, "RealmHouses", Stub("RealmHouses"));
        SetF(contracts, "CrownAndConsequences", Stub("CrownAndConsequences"));
        Inv(contracts, "Init");
        Inv(contracts, "OnServerInitialized");
        RouteLikeOxide(contracts);

        var laws = new RealmLaws();
        Inv(laws, "LoadDefaultConfig");
        Inv(laws, "LoadDefaultMessages");
        SetF(laws, "RealmChronicle", chron);
        SetF(laws, "RealmHouses", Stub("RealmHouses"));
        SetF(laws, "CrownAndConsequences", Stub("CrownAndConsequences"));
        SetF(laws, "RealmContracts", contracts);
        Inv(laws, "Init");
        Inv(laws, "OnServerInitialized");

        Func<string, bool> isOutlaw = id => (bool)contracts.Call("IsOutlaw", id);
        IDictionary cOutlaws = (IDictionary)F(F(contracts, "data"), "Outlaws");
        IList cContracts = (IList)F(F(contracts, "data"), "Contracts");
        Action<Player, string> contractCmd = (p, line) => Inv(contracts, "CmdContract", p, "contract", line.Split(' '));
        Action<Player, string> courtCmd = (p, line) => Inv(laws, "CmdCourt", p, "court", line.Split(' '));

        // 0. The Oxide-style router only sees non-public methods (public ones are never hooks).
        Ok(contracts.Call("ProclaimOutlaw", "x", "y", 1, "z") is bool, "ProclaimOutlaw is reachable through Call (non-public)");
        Ok(contracts.Call("PardonOutlaw", "x") is bool, "PardonOutlaw is reachable through Call (non-public)");
        Ok(contracts.Call("NoSuchMethod") == null, "an unknown method returns null, as in Oxide");

        // 1. Before any sentence Corwen is no public enemy: a bounty is refused and nothing is escrowed.
        poster.Messages.Clear();
        contractCmd(poster, "post bounty Corwen 10 Wood");
        Ok(cContracts.Count == 0 && poster.Inventory.Contents.Counts["Wood"] == 40, "no bounty on a player the court has not outlawed",
            poster.All());

        // 2. The court outlaws Corwen: RealmLaws calls RealmContracts.ProclaimOutlaw.
        Inv(laws, "SetOutlaw", "3", "Corwen", 24, 7);
        Ok(laws.Calls.Any(c => c.StartsWith("ProclaimOutlaw(3,Corwen,24,the court")) || contracts.Calls.Any(c => c.StartsWith("ProclaimOutlaw(3,Corwen,24")),
            "RealmLaws sends the sentence to RealmContracts", string.Join(" | ", contracts.Calls));
        Ok(isOutlaw("3"), "RealmContracts now lists Corwen as an outlaw");
        Ok((bool)F(cOutlaws["3"], "Court") && (string)F(cOutlaws["3"], "By") == "the court", "the entry is marked as placed by the court");
        DateTime until = (DateTime)F(cOutlaws["3"], "Until");
        Ok(Math.Abs((until - DateTime.UtcNow.AddHours(24)).TotalMinutes) < 2, "for the sentence's 24 hours");
        Ok(!chronLog.Contains("decree"), "no second chronicle line from RealmContracts (the verdict is RealmLaws' line)");

        // 3. Now the bounty can be posted, with real escrow.
        poster.Messages.Clear();
        contractCmd(poster, "post bounty Corwen 10 Wood");
        Ok(cContracts.Count == 1 && (string)F(cContracts[0], "Status") == "open" && (string)F(cContracts[0], "TargetId") == "3",
            "a bounty on the court's outlaw is accepted", poster.All());
        Ok(poster.Inventory.Contents.Counts["Wood"] == 30, "10 Wood held in escrow");

        // 4. A shorter later sentence never shortens the entry; bad ids are refused.
        Ok(!(bool)contracts.Call("ProclaimOutlaw", "3", "Corwen", 1, "the court"), "a shorter sentence does not shorten it");
        Ok((DateTime)F(cOutlaws["3"], "Until") == until, "Until is unchanged");
        Ok(!(bool)contracts.Call("ProclaimOutlaw", "not-a-steam-id", "X", 5, "the court"), "a non-numeric id is refused");
        Ok(!(bool)contracts.Call("ProclaimOutlaw", "5", "X", 0, "the court"), "zero hours is refused");

        // 5. The crown's own proclamation is not lifted by a court pardon.
        contractCmd(queen, "outlaw Dalla");
        Ok(isOutlaw("4") && !(bool)F(cOutlaws["4"], "Court"), "the crown outlaws Dalla through /contract outlaw", queen.All());
        Ok(!(bool)contracts.Call("PardonOutlaw", "4"), "PardonOutlaw refuses a crown proclamation");
        Ok(isOutlaw("4"), "Dalla stays an outlaw");

        // 6. The queen pardons Corwen in court: RealmLaws calls PardonOutlaw, the bounty is withdrawn and refunded.
        queen.Messages.Clear();
        courtCmd(queen, "pardon Corwen");
        Ok(contracts.Calls.Any(c => c.StartsWith("PardonOutlaw(3")), "RealmLaws sends the pardon to RealmContracts", queen.All());
        Ok(!isOutlaw("3"), "Corwen is no longer an outlaw in RealmContracts");
        Ok((string)F(cContracts[0], "Status") == "cancelled", "the open bounty is withdrawn", (string)F(cContracts[0], "Status"));
        Ok(poster.Inventory.Contents.Counts["Wood"] == 40, "the escrowed Wood is back with the poster");
        Ok(chronLog.Contains("contract_ended") && chronLog.Contains("pardon"), "chronicle: contract_ended and pardon",
            string.Join(",", chronLog));

        // 7. The pardon set the repeat cooldown (as a crown pardon does), and the state survives a save and reload.
        Ok(((IDictionary)F(F(contracts, "data"), "OutlawAgainAfter")).Contains("3"), "repeat cooldown recorded");
        Inv(contracts, "SaveData");
        var reloaded = new RealmContracts();
        Inv(reloaded, "LoadDefaultConfig");
        Inv(reloaded, "LoadDefaultMessages");
        Inv(reloaded, "Init");
        IDictionary rOutlaws = (IDictionary)F(F(reloaded, "data"), "Outlaws");
        Ok(rOutlaws.Contains("4") && !(bool)F(rOutlaws["4"], "Court") && !rOutlaws.Contains("3"), "outlaw list and Court flag round-trip the data file");
    }
}
