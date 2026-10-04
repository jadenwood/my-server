// Behaviour test mocks for RealmWorld: only the surface the plugin touches. NOT the real game. Type and member names
// follow the 2.0.3867 Assembly-CSharp metadata (tools/plugin-compile-check/check.sh proves the plugin compiles against
// the real ones); behaviour follows the decompiled code where the plugin depends on it:
//   Entity.TryGetAll / TryGetAll<MonsterEntity> / TryGetFromViewID answer from a list of world entities the tests fill;
//   a creature's kind is its ToString() ("wolf_grey(Clone) (Entity)"), as a Unity object prints;
//   ItemCollection.AutoMergeAdd adds what fits, AutoSplit takes, AutoCount measures (a units-capacity model);
//   InteractableContainer is a Container on a placed object; InteractEvent carries the thing used and its user;
//   GameClock.Instance.CurrentTimeBlock is the game's time of day.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class MonoBehaviour : Component { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return x + "," + y + "," + z; }
    }
}

namespace CodeHatch
{
    public enum ResourceType { Wood, Stone, IronIngot, Bread, WolfPelt, Fang, BearHide, DeerSkin, Grain, Flour, Apple, Berry, Cabbage, Carrot, Lumber, Fat, CookedMeat, Count }
}

namespace CodeHatch.AI { public class MonsterMotor { } }

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.Entity == null ? null : p.Inventory; }
        public static object ShowPopup(this Player p, string title, string message, string button, object handler, bool interupt, bool broadcast)
        {
            if (p.PopupsThrow) throw new InvalidOperationException("no window");
            p.Popups.Add(title + "|" + message + "|" + button + "|" + broadcast);
            return null;
        }
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class MonsterEntity { }
    public class Entity : UnityEngine.Component
    {
        public static List<Entity> World = new List<Entity>();          // every entity the tests placed
        public static bool ListThrows;
        public UnityEngine.Vector3 Position;
        public UnityEngine.Vector3 Forward = new UnityEngine.Vector3(0, 0, 1);
        public bool IsPlayer = true;
        public Player Owner;
        public ulong NetViewID;
        public string Label = "Player";
        public List<object> Components = new List<object>();
        public T TryGet<T>() where T : class { return Components.OfType<T>().FirstOrDefault(); }
        public override string ToString() { return Label + " (Entity)"; }
        public static List<Entity> TryGetAll() { if (ListThrows) throw new InvalidOperationException("no list"); return World.ToList(); }
        public static List<Entity> TryGetAll<T>() { if (ListThrows) throw new InvalidOperationException("no list"); return World.Where(e => e.Components.OfType<T>().Any()).ToList(); }
        public static Entity TryGetFromViewID(ulong id) { return World.FirstOrDefault(e => e.NetViewID == id); }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.ItemContainer;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public List<string> Messages = new List<string>();
        public List<string> Popups = new List<string>();
        public bool PopupsThrow;
        public Container Inventory = new Container();
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this, IsPlayer = true }; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
        public static bool PlayerIsOnline(ulong id) { return GetPlayerById(id) != null; }
        public static void BroadcastMessage(string m) { Broadcasts.Add(m); }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    public class Damage { public float Amount; public Entity DamageSource; }
}

namespace CodeHatch.Networking.Events
{
    public class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDamageEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage Damage; }
    public class EntityDeathEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage KillingDamage; }
}

namespace CodeHatch.Engine.Core.Interaction.Behaviours.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Networking;
    public class InteractEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Entity ControllerEntity; public Player Sender; }
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
        public string Name; public int StackLimit = 1000; public CodeHatch.ResourceType? Resource;
        public T TryGet<T>() where T : class { return typeof(T) == typeof(ContainerManagement) ? new ContainerManagement { StackLimit = StackLimit } as T : null; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static List<InvItemBlueprint> All = Enum.GetValues(typeof(CodeHatch.ResourceType)).Cast<CodeHatch.ResourceType>()
            .Where(r => r != CodeHatch.ResourceType.Count)
            .Select(r => new InvItemBlueprint { Name = System.Text.RegularExpressions.Regex.Replace(r.ToString(), "(?<=[a-z])([A-Z])", " $1"), Resource = r, StackLimit = 100 })
            .Concat(new[] { new InvItemBlueprint { Name = "Torch", StackLimit = 10 } }).ToList();
        public InvItemBlueprint GetBlueprintForResource(CodeHatch.ResourceType rt) { return All.FirstOrDefault(b => b.Resource == rt); }
        public InvItemBlueprint GetBlueprintForName(string n, bool resources, bool ignoreCase)
        {
            var bp = All.FirstOrDefault(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase));
            if (bp == null && resources) bp = All.FirstOrDefault(b => b.Resource.HasValue && b.Resource.Value.ToString() == n);
            return bp;
        }
        public static InvItemBlueprint Get(string n) { return Instance.GetBlueprintForName(n, true, true); }
    }
}

// Global namespace, as in the real Assembly-CSharp.dll.
public class InvGameItemStack
{
    public CodeHatch.Inventory.Blueprints.InvItemBlueprint Blueprint;
    public int StackAmount;
    public InvGameItemStack(CodeHatch.Inventory.Blueprints.InvItemBlueprint bp, int c, object o) { Blueprint = bp; StackAmount = c; }
}

public class GameClock : UnityEngine.MonoBehaviour
{
    public enum TimeBlock { Dawn, Morning, Afternoon, Dusk, Night }
    public static GameClock Instance;
    public TimeBlock CurrentTimeBlock = TimeBlock.Night;
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Inventory.Blueprints;
    public class Container { public ItemCollection Contents = new ItemCollection(); }
    public class InteractableContainer : Container { }
    // Units-capacity model of an inventory: AutoMergeAdd adds what fits (Capacity units in all), AutoSplit takes, AutoCount measures.
    public class ItemCollection
    {
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Capacity = 100000;
        public bool Throws;                                           // the game refuses the change
        public int SplitShort;                                        // AutoSplit takes this many fewer than asked
        public Action OnMerge;                                        // runs inside AutoMergeAdd, before the stack lands
        public int Total { get { return Counts.Values.Sum(); } }
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp) { int n; return c.Counts.TryGetValue(bp.Name, out n) ? n : 0; }
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            if (c.OnMerge != null) c.OnMerge();
            if (c.Throws) throw new InvalidOperationException("container locked");
            int room = c.Capacity - c.Total;
            if (room <= 0) return false;
            int add = Math.Min(room, s.StackAmount);
            c.Counts[s.Blueprint.Name] = AutoCount(c, s.Blueprint) + add;
            return add == s.StackAmount;
        }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int n)
        {
            if (c.Throws) throw new InvalidOperationException("container locked");
            int have = AutoCount(c, bp);
            int take = Math.Max(0, Math.Min(have, n - c.SplitShort));
            c.Counts[bp.Name] = have - take;
            return take == n;
        }
    }
}

namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public string Name;
        public Func<string, object[], object> Handler;
        public List<string> Calls = new List<string>();
        public object Call(string hook, params object[] args)
        {
            Calls.Add(hook + "(" + string.Join(",", args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture))) + ")");
            return Handler != null ? Handler(hook, args) : null;
        }
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
            string text = File.ReadAllText(P(n));
            if (text.Trim().Length == 0) return default(T);                  // Newtonsoft returns null for an empty file
            return JsonSerializer.Deserialize<T>(text, Opts);
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
        public int Writes;
        public T ReadObject<T>() { return JsonSerializer.Deserialize<T>(Json, DataFileSystem.Opts); }
        public void WriteObject(object o, bool sync) { Writes++; Json = JsonSerializer.Serialize(o, o.GetType(), DataFileSystem.Opts); }
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
        public List<string> Registered = new List<string>();
        public void RegisterPermission(string p, object pl) { Registered.Add(p); }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
    public class TimerLib
    {
        public List<KeyValuePair<float, Action>> Pending = new List<KeyValuePair<float, Action>>();
        public int EveryCount;
        public float LastEvery;
        public Timer Every(float s, Action a) { EveryCount++; LastEvery = s; return new Timer(); }
        public Timer Once(float s, Action a) { Pending.Add(new KeyValuePair<float, Action>(s, a)); return new Timer(); }
        public void RunPending() { var p = Pending.ToList(); Pending.Clear(); foreach (var kv in p) kv.Value(); }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<string> Logged = new List<string>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Logged.Add(s); }
        public void PrintWarning(string s) { Logged.Add("WARN " + s); }
        public void PrintError(string s) { Logged.Add("ERROR " + s); }
    }
}
