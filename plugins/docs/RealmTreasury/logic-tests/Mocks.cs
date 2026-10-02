// Behavioural test mocks for RealmTreasury: only the surface the plugin touches. NOT the real game.
// The real signatures are proven by tools/plugin-compile-check/check.sh against the shipped DLL metadata.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.Inventory; }
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class Entity { public Player Owner; }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.ItemContainer;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public List<string> Messages = new List<string>();
        public Container Inventory = new Container();
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this }; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
        public static Player GetPlayerByName(string n) { return ClientPlayers.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)); }
        public static List<Player> MatchPlayerByName(string n) { return ClientPlayers.Where(p => p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
        public static bool PlayerIsOnline(ulong id) { return GetPlayerById(id) != null; }
        public static void BroadcastMessage(string format, params object[] args) { Broadcasts.Add(string.Format(format, args)); }
    }
}

namespace CodeHatch.Engine.Modules.SocialSystem
{
    public static class SocialAPI
    {
        public static Dictionary<Type, object> Registry = new Dictionary<Type, object>();
        public static T Get<T>() where T : class { object o; return Registry.TryGetValue(typeof(T), out o) ? (T)o : null; }
    }
}

namespace CodeHatch.Thrones.SocialSystem
{
    using CodeHatch.Engine.Networking;
    public class KingsScheme
    {
        public ulong King; public float Tax = 0.1f;
        public bool HasKing() { return King != 0; }
        public bool IsKing(Player p) { return p != null && p.Id == King; }
        public ulong GetKingID() { return King; }
        public float GetTax() { return Tax; }
    }
    public class KingsRealm { public static float TaxMaximum { get { return 0.5f; } } }
}

namespace CodeHatch.Inventory.Blueprints.Components
{
    public class ContainerManagement { public int StackLimit; }
}

namespace CodeHatch.Inventory.Blueprints
{
    using CodeHatch.Inventory.Blueprints.Components;
    public class InvItemBlueprint
    {
        public string Name; public ContainerManagement CM;
        public T TryGet<T>() where T : class { return CM as T; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static Dictionary<string, InvItemBlueprint> All = new Dictionary<string, InvItemBlueprint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Wood", new InvItemBlueprint { Name = "Wood", CM = new ContainerManagement { StackLimit = 50 } } },
            { "Stone", new InvItemBlueprint { Name = "Stone", CM = new ContainerManagement { StackLimit = 50 } } },
            { "Iron Ingot", new InvItemBlueprint { Name = "Iron Ingot", CM = new ContainerManagement { StackLimit = 25 } } },
            { "Gold Ore", new InvItemBlueprint { Name = "Gold Ore", CM = null } }
        };
        public InvItemBlueprint GetBlueprintForName(string n, bool a, bool b) { InvItemBlueprint bp; return All.TryGetValue(n, out bp) ? bp : null; }
        public static InvItemBlueprint[] GetBlueprintsContaining(string s)
        {
            return All.Values.Where(b => b.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        }
    }
}

// Global namespace, as in the real Assembly-CSharp.dll.
public class InvGameItemStack
{
    public CodeHatch.Inventory.Blueprints.InvItemBlueprint Blueprint;
    public int StackAmount;
    public InvGameItemStack(CodeHatch.Inventory.Blueprints.InvItemBlueprint bp, int c, object o) { Blueprint = bp; StackAmount = c; }
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Inventory.Blueprints;
    public class Container { public ItemCollection Contents = new ItemCollection(); }
    // Units-capacity model of an inventory. Fault knobs: SplitShortBy (AutoSplit removes fewer), MergeCap (AutoMergeAdd
    // adds at most N per call, like a nearly full inventory).
    public class ItemCollection
    {
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Capacity = 100000;
        public int SplitShortBy;
        public int MergeCalls;
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp) { int n; return c.Counts.TryGetValue(bp.Name, out n) ? n : 0; }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int q)
        {
            int n = AutoCount(c, bp); if (n < q) return false;
            int take = Math.Max(0, q - c.SplitShortBy);
            c.Counts[bp.Name] = n - take; return take == q;
        }
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            c.MergeCalls++;
            int total = c.Counts.Values.Sum(); int room = c.Capacity - total; if (room <= 0) return false;
            int add = Math.Min(room, s.StackAmount); c.Counts[s.Blueprint.Name] = AutoCount(c, s.Blueprint) + add; return add == s.StackAmount;
        }
    }
}

namespace CodeHatch.Networking.Events
{
    public class BaseEvent { }
    public delegate void EventSubscriber<T>(T theEvent) where T : BaseEvent;
    public enum EventHandlerOrder { VeryEarly, Early, Normal, Late, VeryLate }
    public static class EventManager
    {
        public static Dictionary<Type, List<Delegate>> Subs = new Dictionary<Type, List<Delegate>>();
        public static void Subscribe<T>(EventSubscriber<T> s, EventHandlerOrder o) where T : BaseEvent
        {
            List<Delegate> l; if (!Subs.TryGetValue(typeof(T), out l)) Subs[typeof(T)] = l = new List<Delegate>(); l.Add(s);
        }
        public static void Unsubscribe<T>(EventSubscriber<T> s) where T : BaseEvent { List<Delegate> l; if (Subs.TryGetValue(typeof(T), out l)) l.Remove(s); }
        public static void Raise<T>(T e) where T : BaseEvent
        {
            List<Delegate> l; if (!Subs.TryGetValue(typeof(T), out l)) return;
            foreach (var d in l.ToArray()) ((EventSubscriber<T>)d)(e);
        }
        public static int Count<T>() { List<Delegate> l; return Subs.TryGetValue(typeof(T), out l) ? l.Count : 0; }
    }
}

namespace CodeHatch.Networking.Events.Item
{
    public class ItemPassEvent : CodeHatch.Networking.Events.BaseEvent { public string Memo; public InvGameItemStack ItemStack; }
}

namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public string Name;
        public Func<string, object[], object> Handler;
        public object Call(string hook, params object[] args) { return Handler != null ? Handler(hook, args) : null; }
    }
}

namespace Oxide.Core
{
    public class DataFileSystem
    {
        public string Dir;
        public int Writes;
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        public string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public T ReadObject<T>(string n) where T : new()
        {
            if (!File.Exists(P(n))) { var t = new T(); WriteObject(n, t); return t; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(P(n)), Opts);
        }
        public void WriteObject<T>(string n, T o) { Writes++; File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts)); }
    }
    public class OxideMod { public DataFileSystem DataFileSystem = new DataFileSystem(); }
    public static class Interface { public static OxideMod Oxide = new OxideMod(); }
}

namespace Oxide.Plugins
{
    using Oxide.Core;
    [AttributeUsage(AttributeTargets.Class)] public class InfoAttribute : Attribute { public InfoAttribute(string a, string b, string c) { } }
    [AttributeUsage(AttributeTargets.Class)] public class DescriptionAttribute : Attribute { public DescriptionAttribute(string a) { } }
    [AttributeUsage(AttributeTargets.Method)] public class ChatCommandAttribute : Attribute { public ChatCommandAttribute(string a) { } }
    [AttributeUsage(AttributeTargets.Field)] public class PluginReferenceAttribute : Attribute { }

    public class ConfigFile
    {
        public string Json;
        public T ReadObject<T>() { return JsonSerializer.Deserialize<T>(Json, DataFileSystem.Opts); }
        public void WriteObject(object o, bool sync) { Json = JsonSerializer.Serialize(o, o.GetType(), DataFileSystem.Opts); }
    }
    public class LangLib
    {
        public Dictionary<string, string> Msgs = new Dictionary<string, string>();
        public void RegisterMessages(Dictionary<string, string> m, object p) { foreach (var kv in m) Msgs[kv.Key] = kv.Value; }
        public string GetMessage(string k, object p, string id) { string v; return Msgs.TryGetValue(k, out v) ? v : k; }
    }
    public class PermLib
    {
        public HashSet<string> Grants = new HashSet<string>();
        public void RegisterPermission(string p, object pl) { }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class TimerLib
    {
        public List<Action> Every_ = new List<Action>();
        public object Every(float s, Action a) { Every_.Add(a); return null; }
        public object Once(float s, Action a) { return null; }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<string> Log = new List<string>();
        public List<string> FileLog = new List<string>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Log.Add(s); }
        public void PrintWarning(string s) { Log.Add("WARN " + s); }
        public void PrintError(string s) { Log.Add("ERROR " + s); }
        protected void LogToFile(string f, string text, Oxide.Core.Plugins.Plugin p, bool dated, bool ts) { FileLog.Add(f + ": " + text); }
        protected void PrintToChat(string format, params object[] args) { CodeHatch.Engine.Networking.Server.BroadcastMessage(format, args); }
    }
}
