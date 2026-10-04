// Behaviour test mocks for RealmQuests: only the surface the plugin touches. Not the real game. Type and member names
// follow the 2.0.3867 Assembly-CSharp metadata (tools/plugin-compile-check/check.sh proves the plugin compiles against
// the real ones); behaviour follows the decompiled code where the plugin depends on it: InvBlueprints.GetBlueprintForName
// matches a ResourceType name only case-sensitively with ignoreCase false; AutoMergeAdd keeps a stack object; an
// UnityEngine.Object prints as "name (Type)". Serialization uses System.Text.Json, not Newtonsoft.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public class Object { public string name; }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    public struct Vector3Int { public int x, y, z; public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; } }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.NoInventory ? null : p.Entity.GetContainerOfType(CollectionTypes.Inventory); }
        public static object ShowPopup(this Player p, string title, string message, string button, object handler, bool interupt, bool broadcast)
        {
            if (p.PopupsThrow) throw new InvalidOperationException("no window");
            p.Popups.Add(title + "|" + message + "|" + button + "|" + broadcast);
            return null;
        }
    }
}

namespace CodeHatch.AI
{
    public class MonsterMotor { }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class MonsterEntity { }
    public class Entity : UnityEngine.Object
    {
        public UnityEngine.Vector3 Position;
        public bool IsPlayer = true;
        public Player Owner;
        public List<object> Components = new List<object>();
        public List<CodeHatch.ItemContainer.Container> Containers = new List<CodeHatch.ItemContainer.Container>();
        public T TryGet<T>() where T : class { return Components.OfType<T>().FirstOrDefault(); }
        public bool Has<T>() where T : class { return TryGet<T>() != null; }
        public override string ToString() { return (name ?? "Entity") + " (CodeHatch.Engine.Core.Cache.Entity)"; }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.ItemContainer;
    public class Connection { public string IpAddress; }
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public Connection Connection = new Connection { IpAddress = "10.0.0.1" };
        public List<string> Messages = new List<string>();
        public List<string> Popups = new List<string>();
        public bool PopupsThrow;
        public bool NoInventory;
        public Player(ulong id, string name, int packSlots = 24)
        {
            Id = id; Name = name;
            Entity = new Entity { Owner = this, name = name };
            Entity.Containers.Add(new Container(Entity, CollectionTypes.Inventory, packSlots, name + "'s packs"));
        }
        public ItemCollection Packs { get { return Entity.GetContainerOfType(CollectionTypes.Inventory).Contents; } }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
        public static Player GetPlayerByName(string n) { return ClientPlayers.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)); }
        public static List<Player> MatchPlayerByName(string n) { return ClientPlayers.Where(p => p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
        public static void BroadcastMessage(string m) { Broadcasts.Add(m); }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    public class Damage { public float Amount; public Entity DamageSource; }
}

namespace CodeHatch.Engine.Behaviours
{
    public class ItemCrafter { public string Label; }
}

namespace CodeHatch.Networking.Events
{
    using CodeHatch.Engine.Networking;
    public class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
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

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Behaviours;
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Networking;
    using CodeHatch.Inventory.Blueprints;
    using CodeHatch.ItemContainer;
    public class EntityDeathEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage KillingDamage; }
    public class ItemCrafterEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public ItemCrafter Crafter; public Player Sender; }
    public class ItemCrafterCraftEvent : ItemCrafterEvent { public InvItemBlueprint Product; public int Quantity; public ItemCollection Input, Output; }
    public class ItemCrafterItemEvent : ItemCrafterEvent { public InvGameItemStack Stack; public int Cycles; }
    public class ItemCrafterFinishEvent : ItemCrafterEvent { }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    using CodeHatch.Engine.Networking;
    public class CubePlaceEvent : CodeHatch.Networking.Events.BaseEvent
    {
        public Player Sender; public ushort GridID; public Vector3Int Position; public byte Material; public bool CausedByDestruction;
    }
}

namespace CodeHatch.Inventory.Blueprints
{
    using CodeHatch.ItemContainer;
    using CodeHatch.Inventory.Blueprints.Components;
    public class InvItemBlueprint
    {
        public string Name; public int StackLimit = 1000; public string ResourceKey;
        public T TryGet<T>() where T : class { return typeof(T) == typeof(ContainerManagement) ? new ContainerManagement { StackLimit = StackLimit } as T : null; }
    }
    public class InvGameItemStack
    {
        public InvItemBlueprint Blueprint;
        public int StackAmount;
        public ItemCollection Collection;
        public InvGameItemStack(InvItemBlueprint bp, int count, object instance) { Blueprint = bp; StackAmount = count; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static List<InvItemBlueprint> All = new List<InvItemBlueprint>
        {
            new InvItemBlueprint { Name = "Wood", ResourceKey = "Wood" }, new InvItemBlueprint { Name = "Stone", ResourceKey = "Stone" },
            new InvItemBlueprint { Name = "Iron", ResourceKey = "IronOre" }, new InvItemBlueprint { Name = "Clay", ResourceKey = "Clay" },
            new InvItemBlueprint { Name = "Flax", ResourceKey = "Flax" }, new InvItemBlueprint { Name = "Grain", ResourceKey = "Grain" },
            new InvItemBlueprint { Name = "Leather Hide", ResourceKey = "LeatherHide" }, new InvItemBlueprint { Name = "Deer Skin", ResourceKey = "DeerSkin" },
            new InvItemBlueprint { Name = "Wolf Pelt", ResourceKey = "WolfPelt" }, new InvItemBlueprint { Name = "Rabbit Pelt", ResourceKey = "RabbitPelt" },
            new InvItemBlueprint { Name = "Bear Hide", ResourceKey = "BearHide" }, new InvItemBlueprint { Name = "Feather", ResourceKey = "Feather" },
            new InvItemBlueprint { Name = "Fat", ResourceKey = "Fat" }, new InvItemBlueprint { Name = "Bone", ResourceKey = "Bone" },
            new InvItemBlueprint { Name = "Apple", ResourceKey = "Apple" }, new InvItemBlueprint { Name = "Berries", ResourceKey = "Berry" },
            new InvItemBlueprint { Name = "Cabbage", ResourceKey = "Cabbage" }, new InvItemBlueprint { Name = "Carrot", ResourceKey = "Carrot" },
            new InvItemBlueprint { Name = "Bread", ResourceKey = "Bread", StackLimit = 50 }, new InvItemBlueprint { Name = "Cooked Meat", ResourceKey = "CookedMeat", StackLimit = 50 },
            new InvItemBlueprint { Name = "Steel Ingot", ResourceKey = "SteelIngot", StackLimit = 100 }, new InvItemBlueprint { Name = "Charcoal", ResourceKey = "Charcoal" },
            new InvItemBlueprint { Name = "Iron Ingot", ResourceKey = "IronIngot" }, new InvItemBlueprint { Name = "Lumber", ResourceKey = "Lumber" },
            new InvItemBlueprint { Name = "Iron Sword", StackLimit = 1 }, new InvItemBlueprint { Name = "Steel Battle Axe", StackLimit = 1 },
            new InvItemBlueprint { Name = "Wood Shield", StackLimit = 1 }, new InvItemBlueprint { Name = "Iron Arrow", StackLimit = 100 },
            new InvItemBlueprint { Name = "Torch", StackLimit = 10 },
        };
        public List<string> AllBlueprintNames { get { return All.Select(b => b.Name).ToList(); } }
        // As the game [CODE InvBlueprints.GetBlueprintForName]: display names first (lower-cased when ignoreCase), then
        // ResourceType keys compared to the (possibly lower-cased) name, case-sensitively.
        public InvItemBlueprint GetBlueprintForName(string n, bool resources, bool ignoreCase)
        {
            string key = ignoreCase ? n.ToLower() : n;
            foreach (var b in All) if ((ignoreCase ? b.Name.ToLower() : b.Name) == key) return b;
            if (resources) foreach (var b in All) if (b.ResourceKey != null && b.ResourceKey == key) return b;
            return null;
        }
        public static InvItemBlueprint Get(string n) { return Instance.GetBlueprintForName(n, false, true); }
    }
}

namespace CodeHatch.Inventory.Blueprints.Components
{
    public class ContainerManagement { public int StackLimit; }
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Inventory.Blueprints;
    [Flags] public enum CollectionTypes { Inventory = 0x10, Hotbar = 0x20 }
    public class Container
    {
        public Entity Entity; public CollectionTypes Type; public string ContainerName; public ItemCollection Contents;
        public Container(Entity e, CollectionTypes t, int slots, string name) { Entity = e; Type = t; ContainerName = name; Contents = new ItemCollection(slots) { Container = this }; }
    }
    public static class ItemContainerExtensions
    {
        public static Container GetContainerOfType(this Entity e, CollectionTypes t) { return e == null ? null : e.Containers.FirstOrDefault(c => c.Type == t); }
    }
    public class ItemCollection
    {
        public Container Container;
        public int Slots;
        public bool Refuse;                                           // AutoSplit / AutoMergeAdd do nothing (a locked container)
        readonly List<InvGameItemStack> items = new List<InvGameItemStack>();
        public ItemCollection(int slots) { Slots = slots; }
        public List<InvGameItemStack> GetItems() { return new List<InvGameItemStack>(items); }
        public bool HasItem(InvGameItemStack s) { return items.Contains(s); }
        public InvGameItemStack AddItem(InvGameItemStack s) { if (items.Count >= Slots) return s; items.Add(s); s.Collection = this; return null; }
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp)
        {
            return c == null || bp == null ? 0 : c.items.Where(s => s.Blueprint != null && s.Blueprint.Name == bp.Name).Sum(s => s.StackAmount);
        }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int q)
        {
            if (c.Refuse || AutoCount(c, bp) < q) return false;
            foreach (var s in c.items.Where(x => x.Blueprint.Name == bp.Name).Reverse().ToList())
            {
                int take = Math.Min(q, s.StackAmount); s.StackAmount -= take; q -= take;
                if (s.StackAmount == 0) { c.items.Remove(s); s.Collection = null; }
                if (q == 0) break;
            }
            return true;
        }
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            if (c == null || s == null || c.Refuse || s.StackAmount <= 0 || c.HasItem(s)) return false;
            int limit = s.Blueprint.StackLimit;
            if (limit > 1)
                foreach (var o in c.items.Where(x => x.Blueprint.Name == s.Blueprint.Name))
                {
                    int room = limit - o.StackAmount; int move = Math.Min(room, s.StackAmount);
                    o.StackAmount += move; s.StackAmount -= move;
                    if (s.StackAmount == 0) return true;
                }
            return c.AddItem(s) == null;
        }
        public static void Give(ItemCollection c, string name, int n) { var bp = InvBlueprints.Get(name); while (n > 0) { int k = Math.Min(n, bp.StackLimit); AutoMergeAdd(c, new InvGameItemStack(bp, k, null)); n -= k; } }
    }
}

namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public string Name;
        public Func<string, object[], object> Handler;
        public List<string> Calls = new List<string>();
        public object Call(string hook, params object[] args) { Calls.Add(hook + "(" + string.Join(",", args.Select(a => Convert.ToString(a))) + ")"); return Handler != null ? Handler(hook, args) : null; }
    }
}

namespace Oxide.Core
{
    public class DataFileSystem
    {
        public string Dir;
        public int Writes;
        public List<string> Written = new List<string>();
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public T ReadObject<T>(string n) where T : new()
        {
            if (!File.Exists(P(n))) { var t = new T(); WriteObject(n, t); return t; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(P(n)), Opts);
        }
        public void WriteObject<T>(string n, T o)
        {
            Writes++; Written.Add(n);
            Directory.CreateDirectory(Path.GetDirectoryName(P(n)));
            File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts));
        }
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
        public List<string> Registered = new List<string>();
        public void RegisterPermission(string p, object pl) { Registered.Add(p); }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
    public class TimerLib
    {
        public int EveryCount;
        public float LastEvery;
        public Timer Every(float s, Action a) { EveryCount++; LastEvery = s; return new Timer(); }
        public Timer Once(float s, Action a) { return new Timer(); }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<string> Logged = new List<string>();
        public List<Action> Ticks = new List<Action>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Logged.Add(s); }
        public void PrintWarning(string s) { Logged.Add("WARN " + s); }
        public void PrintError(string s) { Logged.Add("ERROR " + s); }
        public void NextTick(Action a) { Ticks.Add(a); }
        public void RunTicks() { var t = Ticks.ToArray(); Ticks.Clear(); foreach (var a in t) a(); }
    }
}
