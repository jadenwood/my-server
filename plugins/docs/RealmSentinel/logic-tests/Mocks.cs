// Behavioural test mocks for RealmSentinel: only the surface the plugin touches. NOT the real game. Shapes follow the
// shipped 2.0.3867 metadata (names and members), behaviour is the simplest that lets the scenarios run.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    public struct Vector3Int { public int x, y, z; }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static bool HasPermission(this Player p, string perm) { return p.GamePerms.Contains(perm); }
        public static Container GetInventory(this Player p) { return p.Entity.GetContainerOfType(CollectionTypes.Inventory); }
    }
}

namespace CodeHatch.Inventory.Blueprints
{
    public class InvItemBlueprint { public string Name; public InvItemBlueprint(string n) { Name = n; } }
}

public class InvGameItemStack
{
    public string Name;
    public int StackAmount;
    public InvGameItemStack(string name, int amount) { Name = name; StackAmount = amount; }
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Engine.Core.Cache;
    public enum CollectionTypes { Unassigned = 1, Input = 2, Output = 4, Agregated = 8, Inventory = 16, Hotbar = 32, Hidden = 64, Fuel = 128 }
    public class ItemCollection : IEnumerable<InvGameItemStack>
    {
        public List<InvGameItemStack> Stacks = new List<InvGameItemStack>();
        public IEnumerator<InvGameItemStack> GetEnumerator() { return Stacks.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return Stacks.GetEnumerator(); }
        public int Count(string name) { return Stacks.Where(s => s.Name == name).Sum(s => s.StackAmount); }
        public void Add(string name, int amount)
        {
            if (amount > 0) { Stacks.Add(new InvGameItemStack(name, amount)); return; }
            int take = -amount;
            foreach (var s in Stacks.Where(s => s.Name == name).ToList())
            {
                int t = Math.Min(take, s.StackAmount);
                s.StackAmount -= t; take -= t;
                if (s.StackAmount == 0) Stacks.Remove(s);
                if (take == 0) break;
            }
        }
    }
    public class Container
    {
        public ItemCollection Contents = new ItemCollection();
    }
    public static class ItemContainerExtensions
    {
        public static Container GetContainerOfType(this Entity e, CollectionTypes t)
        {
            Container c;
            return e.Containers.TryGetValue(t, out c) ? c : null;
        }
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    public class Entity
    {
        public UnityEngine.Vector3 Position;
        public bool IsPlayer = true;
        public Player Owner;
        public Dictionary<CollectionTypes, Container> Containers = new Dictionary<CollectionTypes, Container>();
        public Dictionary<Type, object> Components = new Dictionary<Type, object>();
        public T TryGet<T>() where T : class { object o; return Components.TryGetValue(typeof(T), out o) ? (T)o : null; }
        public bool Has<T>() where T : class { return Components.ContainsKey(typeof(T)); }
        public T GetOrCreate<T>() where T : class, new()
        {
            object o;
            if (!Components.TryGetValue(typeof(T), out o)) { o = new T(); Components[typeof(T)] = o; }
            var tp = o as CodeHatch.Engine.Behaviours.CharacterTeleport;
            if (tp != null) tp.Entity = this;
            return (T)o;
        }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.ItemContainer;
    public class Connection { public int AveragePing; }
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public Connection Connection = new Connection();
        public int AveragePing;
        public HashSet<string> GamePerms = new HashSet<string>();
        public List<string> Messages = new List<string>();
        public Player(ulong id, string name)
        {
            Id = id; Name = name;
            Respawn();
        }
        // A new body, as after a death and respawn: a new Entity with empty containers.
        public void Respawn()
        {
            var old = Entity;
            Entity = new Entity { Owner = this };
            Entity.Containers[CollectionTypes.Inventory] = new Container();
            Entity.Containers[CollectionTypes.Hotbar] = new Container();
            if (old != null) Entity.Position = old.Position;
        }
        public ItemCollection Inv { get { return Entity.Containers[CollectionTypes.Inventory].Contents; } }
        public ItemCollection Bar { get { return Entity.Containers[CollectionTypes.Hotbar].Contents; } }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Kicks = new List<string>();
        public static List<string> Bans = new List<string>();
        public static bool RefuseBans;
        public static Player ServerPlayer = new Player(9999999999, "Server") { IsServer = true };
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
        public static void Kick(Player p, string reason) { Kicks.Add(p.Name + "|" + reason); }
        public static bool Ban(ulong id, string name, string reason) { if (RefuseBans) return false; Bans.Add(id + "|" + name + "|" + reason); return true; }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    [Flags]
    public enum DamageType
    {
        Unknown = 0, Suicide = 1, Impact = 2, Melee = 4, Projectile = 8, Fire = 0x10, Explosion = 0x20, Cut = 0x40, Breach = 0x80,
        Plasma = 0x100, Harvest = 0x200, Healing = 0x400, Falling = 0x800, OutOfBounds = 0x1000, Plague = 0x2000, Pierce = 0x4000,
        Bash = 0x8000, Slash = 0x10000, Siege = 0x20000, Hunger = 0x40000, Drowning = 0x80000, God = 0x100000, Thirst = 0x200000,
        Salvage = 0x400000, Any = 0x40000000
    }
    public class Damage { public float Amount; public Entity DamageSource; public DamageType DamageTypes = DamageType.Melee; }
}

namespace CodeHatch.Networking.Events
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Networking;
    public class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
    public class NetworkEvent : BaseEvent { public Player Sender; }
    public delegate void EventSubscriber<T>(T theEvent) where T : BaseEvent;
    public enum EventHandlerOrder { VeryEarly, Early, Normal, Late, VeryLate }
    public static class EventManager
    {
        public static List<Delegate> Subscribers = new List<Delegate>();
        public static void Subscribe<T>(EventSubscriber<T> s, EventHandlerOrder order) where T : BaseEvent { Subscribers.Add(s); }
        public static void Unsubscribe<T>(EventSubscriber<T> s) where T : BaseEvent { Subscribers.Remove(s); }
        public static void CallEvent<T>(T e) where T : BaseEvent
        {
            foreach (var d in Subscribers.ToList()) { var s = d as EventSubscriber<T>; if (s != null) s(e); }
        }
    }
    public class PlayerCaptureEvent : NetworkEvent { public Entity Captor; public Player Target; }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Behaviours;
    using CodeHatch.Engine.Core.Cache;
    public class EntityEvent : CodeHatch.Networking.Events.NetworkEvent { public Entity Entity; }
    public class EntityDamageEvent : EntityEvent { public Damage Damage; }
    public class EntityDeathEvent : EntityEvent { public Damage KillingDamage; }
    public class TeleportEvent : EntityEvent { public UnityEngine.Vector3 Position; }
    public class ItemCrafterEvent : EntityEvent { public ItemCrafter Crafter; }
    public class ItemCrafterStartEvent : ItemCrafterEvent { }
    public class ItemCrafterFinishEvent : ItemCrafterEvent { }
}

namespace CodeHatch.Engine.Behaviours
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Networking;
    using CodeHatch.Inventory.Blueprints;
    using CodeHatch.Networking.Events;
    using CodeHatch.Networking.Events.Entities;
    public class ItemCrafter { public InvItemBlueprint Product; }
    // As in the game: the server's Teleport raises a TeleportEvent (Sender = the server player) and moves the body.
    public class CharacterTeleport
    {
        public Entity Entity;
        public static int Calls;
        public void Teleport(UnityEngine.Vector3 position)
        {
            Calls++;
            Entity.Position = position;
            EventManager.CallEvent(new TeleportEvent { Entity = Entity, Position = position, Sender = Server.ServerPlayer });
        }
    }
}

namespace CodeHatch.Networking.Events.Players
{
    using CodeHatch.Engine.Networking;
    public class PlayerEvent : CodeHatch.Networking.Events.NetworkEvent { public Player Player; }
    public class PlayerMessageEvent : PlayerEvent { public string Message; }
    public class PlayerSpawnEvent : PlayerEvent { public UnityEngine.Vector3 Position; }
    public class PlayerRespawnEvent : PlayerSpawnEvent { }
    public class PlayerFirstSpawnEvent : PlayerSpawnEvent { public bool AtFirstSpawn; }
    public class PlayerPreSpawnCompleteEvent : PlayerEvent { }
}

namespace CodeHatch.Engine.Core.Interaction.Behaviours.Networking
{
    using CodeHatch.Engine.Core.Cache;
    public class InteractEvent : CodeHatch.Networking.Events.Entities.EntityEvent { public Entity ControllerEntity; }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Damaging;
    public class CubeDamageEvent : CodeHatch.Networking.Events.NetworkEvent { public Damage Damage; }
    public class CubePlaceEvent : CodeHatch.Networking.Events.NetworkEvent { }
}

namespace CodeHatch.Thrones.AncientThrone
{
    using CodeHatch.Engine.Networking;
    public class AncientThroneCaptureEvent : CodeHatch.Networking.Events.BaseEvent { public Player Player; }
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
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public T ReadObject<T>(string n) where T : new()
        {
            if (!File.Exists(P(n))) { var t = new T(); WriteObject(n, t); return t; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(P(n)), Opts);
        }
        public void WriteObject<T>(string n, T o) { File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts)); }
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
        public HashSet<string> Groups = new HashSet<string>();
        public void RegisterPermission(string p, object pl) { }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
        public bool UserHasGroup(string id, string g) { return Groups.Contains(id + "|" + g); }
    }
    public class TimerLib
    {
        public List<Action> Pending = new List<Action>();
        public List<KeyValuePair<float, Action>> Repeating = new List<KeyValuePair<float, Action>>();
        public object Every(float s, Action a) { Repeating.Add(new KeyValuePair<float, Action>(s, a)); return null; }
        public object Once(float s, Action a) { Pending.Add(a); return null; }
        public void RunPending() { var list = Pending.ToList(); Pending.Clear(); foreach (var a in list) a(); }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<Action> Ticks = new List<Action>();
        public List<string> Log = new List<string>();
        public List<string> FileLog = new List<string>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Log.Add(s); }
        public void PrintWarning(string s) { Log.Add("WARN " + s); }
        public void PrintError(string s) { Log.Add("ERROR " + s); }
        public void NextTick(Action a) { Ticks.Add(a); }
        public void RunTicks() { var list = Ticks.ToList(); Ticks.Clear(); foreach (var a in list) a(); }
        protected void LogToFile(string file, string text, Oxide.Core.Plugins.Plugin p, bool dated, bool stamp) { FileLog.Add(file + ": " + text); }
    }
}
