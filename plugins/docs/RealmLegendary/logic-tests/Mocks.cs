// Behaviour test mocks for RealmLegendary (and RealmEvents, so the exploit suite can run both together): only the
// surface the plugins touch, with a small item world in which stacks are real objects that move between collections.
// Not the real game. Type and member names follow the 2.0.3867 Assembly-CSharp metadata; behaviour follows the
// decompiled code where the plugin depends on it (AutoMergeAdd keeps a non-stackable stack object; Collection is the
// collection a stack sits in; RemoveItem clears it).
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
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    using CodeHatch.Thrones.SocialSystem;
    public struct Vector3Int { public int x, y, z; }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.Entity.GetContainerOfType(CollectionTypes.Inventory); }
        public static Guild GetGuild(this Player p) { return p.Guild; }
        public static object ShowPopup(this Player p, string title, string message, string button, object handler, bool interupt, bool broadcast)
        {
            if (p.PopupsThrow) throw new InvalidOperationException("no window");
            p.Popups.Add(title + "|" + message + "|" + button + "|" + broadcast);
            return null;
        }
    }
    // A held weapon: GameObjectUtility.TryGetEntity(Damager) gives the holdable's entity.
    public class HoldableObject : UnityEngine.Object { public Entity Entity; }
    public static class GameObjectUtility
    {
        public static Entity TryGetEntity(this UnityEngine.Object o) { var h = o as HoldableObject; return h != null ? h.Entity : null; }
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class Entity
    {
        public UnityEngine.Vector3 Position;
        public bool IsPlayer = true;
        public Player Owner;
        public bool Blocking;                                         // CombatUtil.IsBlocking reads it
        public List<object> Components = new List<object>();
        public List<CodeHatch.ItemContainer.Container> Containers = new List<CodeHatch.ItemContainer.Container>();
        public T TryGet<T>() where T : class { return Components.OfType<T>().FirstOrDefault(); }
        public bool Has<T>() where T : class { return TryGet<T>() != null; }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.ItemContainer;
    using CodeHatch.Thrones.SocialSystem;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public string ChatFormat = "%name% : %message%";
        public List<string> Messages = new List<string>();
        public List<string> Popups = new List<string>();
        public bool PopupsThrow;
        public Guild Guild;
        public Player(ulong id, string name, int hotbarSlots = 8, int packSlots = 24)
        {
            Id = id; Name = name;
            Entity = new Entity { Owner = this };
            Entity.Containers.Add(new Container(Entity, CollectionTypes.Hotbar, hotbarSlots, name + "'s hotbar"));
            Entity.Containers.Add(new Container(Entity, CollectionTypes.Inventory, packSlots, name + "'s packs"));
        }
        public ItemCollection Hotbar { get { return Entity.GetContainerOfType(CollectionTypes.Hotbar).Contents; } }
        public ItemCollection Packs { get { return Entity.GetContainerOfType(CollectionTypes.Inventory).Contents; } }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static int PlayerLimit = 50;
        public static List<string> Broadcasts = new List<string>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
        public static Player GetPlayerByName(string n) { return ClientPlayers.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)); }
        public static List<Player> MatchPlayerByName(string n) { return ClientPlayers.Where(p => p.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0).ToList(); }
        public static bool PlayerIsOnline(ulong id) { return GetPlayerById(id) != null; }
        public static void BroadcastMessage(string m) { Broadcasts.Add(m); }
    }
}

namespace CodeHatch.Engine.Modules.SocialSystem
{
    public static class SocialAPI
    {
        public static Dictionary<Type, object> Registry = new Dictionary<Type, object>();
        public static T Get<T>() where T : class { object o; return Registry.TryGetValue(typeof(T), out o) ? (T)o : null; }
    }
    public class Members { public int Count; public int MemberCount() { return Count; } }
}

namespace CodeHatch.Thrones.SocialSystem
{
    using CodeHatch.Engine.Networking;
    public class Guild
    {
        public string Name; public ulong OwnerId; public ulong BaseID;
        public CodeHatch.Engine.Modules.SocialSystem.Members Members() { return new CodeHatch.Engine.Modules.SocialSystem.Members { Count = Server.ClientPlayers.Count(p => p.Guild == this) }; }
    }
    public class GuildScheme { public Guild TryGetGuildByMember(ulong id) { var p = Server.GetPlayerById(id); return p != null ? p.Guild : null; } }
    public class KingsScheme
    {
        public ulong King; public string KingName;
        public bool HasKing() { return King != 0; }
        public bool IsKing(Player p) { return p != null && p.Id == King; }
        public ulong GetKingID() { return King; }
        public string GetKingName() { return KingName; }
    }
}

namespace CodeHatch.Thrones.AncientThrone
{
    using CodeHatch.Engine.Networking;
    public class AncientThroneCaptureEvent : CodeHatch.Networking.Events.BaseEvent
    {
        public enum States { Capturing, Cancelled, Completed }
        public Player Player; public States State;
    }
    public class AncientThroneReleaseEvent : CodeHatch.Networking.Events.BaseEvent { public Player Sender; public bool IsDeath; }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    [Flags]
    public enum DamageType
    {
        Unknown = 0, Suicide = 1, Impact = 2, Melee = 4, Projectile = 8, Fire = 0x10, Explosion = 0x20, Cut = 0x40,
        Breach = 0x80, Plasma = 0x100, Harvest = 0x200, Healing = 0x400, Falling = 0x800, Pierce = 0x4000,
        Bash = 0x8000, Slash = 0x10000, Siege = 0x20000
    }
    public class Damage
    {
        public float Amount; public Entity DamageSource; public UnityEngine.Object Damager;
        public DamageType DamageTypes = DamageType.Melee | DamageType.Slash;
        public object RightHand;                                      // what TryGetFromSource<T> finds (null = unknown)
        public T TryGetFromSource<T>() where T : class { return RightHand as T; }
    }
}

namespace CodeHatch.Melee
{
    using CodeHatch.Engine.Core.Cache;
    public static class CombatUtil { public static bool IsBlocking(this Entity e) { return e != null && e.Blocking; } }
}

namespace CodeHatch.Blocks.Collapsing
{
    public class PlaceableBlockAssociation { }
}

namespace CodeHatch.Engine.Modules.Inventory.Holdables
{
    using CodeHatch.Inventory.Blueprints;
    public class BipedHoldable { public InvGameItemStack Stack; }
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
    public class PlayerCaptureEvent : BaseEvent { public Entity Captor; public Player Target; }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDamageEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage Damage; }
    public class EntityDeathEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage KillingDamage; }
}

namespace CodeHatch.Networking.Events.Entities.Players
{
    using CodeHatch.Damaging;
    public class SleeperDeathEvent : CodeHatch.Networking.Events.BaseEvent { public ulong SleeperId; public Damage KillingDamage; }
}

namespace CodeHatch.Networking.Events.Players
{
    using CodeHatch.Damaging;
    public class PlayerDeathEvent : CodeHatch.Networking.Events.BaseEvent { public ulong PlayerId; public Damage KillingDamage; }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    using CodeHatch.Damaging;
    public class Grid { public UnityEngine.Vector3 World; public UnityEngine.Vector3 LocalToWorldCoordinate(Vector3Int p) { return World; } }
    public class CubePlaceEvent : CodeHatch.Networking.Events.BaseEvent { public ulong SenderId; public Grid Grid; public Vector3Int Position; }
    public class CubeDamageEvent : CodeHatch.Networking.Events.BaseEvent { public Damage Damage; public Vector3Int Position; }
}

namespace CodeHatch.Inventory.Blueprints
{
    using CodeHatch.ItemContainer;
    using CodeHatch.Inventory.Blueprints.Components;
    public class InvItemBlueprint
    {
        public string Name; public int StackLimit = 1000;
        public T TryGet<T>() where T : class { return typeof(T) == typeof(ContainerManagement) ? new ContainerManagement { StackLimit = StackLimit } as T : null; }
    }
    public class InvGameItemStack
    {
        static int next = 1;
        public int UniqueID = next++;
        public InvItemBlueprint Blueprint;
        public int StackAmount;
        public ItemCollection Collection;                             // set by the collection it sits in
        public string Name { get { return Blueprint != null ? Blueprint.Name : "(null)"; } }
        public InvGameItemStack(InvItemBlueprint bp, int count, object instance) { Blueprint = bp; StackAmount = count; }
        public override string ToString() { return "#" + UniqueID + " " + Name + " x" + StackAmount; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static List<InvItemBlueprint> All = new List<InvItemBlueprint>
        {
            new InvItemBlueprint { Name = "Wood" }, new InvItemBlueprint { Name = "Stone" }, new InvItemBlueprint { Name = "Iron" },
            new InvItemBlueprint { Name = "Iron Sword", StackLimit = 1 }, new InvItemBlueprint { Name = "Steel Greatsword", StackLimit = 1 },
            new InvItemBlueprint { Name = "Battle Axe", StackLimit = 1 },
        };
        public List<string> AllBlueprintNames { get { return All.Select(b => b.Name).ToList(); } }
        public InvItemBlueprint GetBlueprintForName(string n, bool resources, bool ignoreCase)
        {
            return All.FirstOrDefault(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase));
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
    [Flags] public enum CollectionTypes { Unassigned = 1, Input = 2, Output = 4, Agregated = 8, Inventory = 0x10, Hotbar = 0x20, Hidden = 0x40, Fuel = 0x80 }
    public class Container
    {
        public Entity Entity; public CollectionTypes Type; public string ContainerName; public ItemCollection Contents;
        public Container() : this(null, CollectionTypes.Inventory, 1000, "container") { }
        public Container(Entity e, CollectionTypes t, int slots, string name) { Entity = e; Type = t; ContainerName = name; Contents = new ItemCollection(slots) { Container = this }; ItemCollection.World.Add(Contents); }
    }
    public static class ItemContainerExtensions
    {
        public static Container GetContainerOfType(this Entity e, CollectionTypes t) { return e == null ? null : e.Containers.FirstOrDefault(c => c.Type == t); }
    }
    public class ItemCollection
    {
        public Container Container;
        public int Slots;
        public bool ThrowOnRemove;                                   // a collection the server cannot change
        readonly List<InvGameItemStack> items = new List<InvGameItemStack>();
        public ItemCollection(int slots) { Slots = slots; }
        public List<InvGameItemStack> GetItems() { return new List<InvGameItemStack>(items); }
        public bool HasItem(InvGameItemStack s) { return items.Contains(s); }
        public InvGameItemStack AddItem(InvGameItemStack s) { if (items.Count >= Slots) return s; items.Add(s); s.Collection = this; return null; }
        public InvGameItemStack RemoveItem(InvGameItemStack s, bool broadcast)
        {
            if (ThrowOnRemove) throw new InvalidOperationException("locked");
            if (items.Remove(s)) s.Collection = null; return s;
        }
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp)
        {
            return c == null || bp == null ? 0 : c.items.Where(s => s.Blueprint != null && s.Blueprint.Name == bp.Name).Sum(s => s.StackAmount);
        }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int q)
        {
            if (AutoCount(c, bp) < q) return false;
            foreach (var s in c.items.Where(x => x.Blueprint.Name == bp.Name).Reverse().ToList())
            {
                int take = Math.Min(q, s.StackAmount); s.StackAmount -= take; q -= take;
                if (s.StackAmount == 0) c.RemoveItem(s, true);
                if (q == 0) break;
            }
            return true;
        }
        // As the game: a stackable stack merges into others of its kind first; what is left is added as the object itself.
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            if (c == null || s == null || s.StackAmount <= 0 || c.HasItem(s)) return false;
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
        // The whole world, for the audit: every collection ever made.
        public static List<ItemCollection> World = new List<ItemCollection>();
    }
}

namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public string Name;
        public Func<string, object[], object> Handler;
        public List<string> Calls = new List<string>();
        public object Call(string hook, params object[] args) { Calls.Add(hook + "(" + string.Join(",", args.Select(a => a is string[] ? "[" + string.Join(";", (string[])a) + "]" : Convert.ToString(a))) + ")"); return Handler != null ? Handler(hook, args) : null; }
    }
}

namespace Oxide.Core
{
    public class DataFileSystem
    {
        public string Dir;
        public int Writes;
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
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
        public List<string> Registered = new List<string>();
        public void RegisterPermission(string p, object pl) { Registered.Add(p); }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
    public class TimerLib
    {
        public List<Action> Pending = new List<Action>();
        public float LastEvery;
        public Timer Every(float s, Action a) { LastEvery = s; return new Timer(); }
        public Timer Once(float s, Action a) { Pending.Add(a); return new Timer(); }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<string> Logged = new List<string>();   // not "Log": RealmChronicle has a Log method
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Logged.Add(s); }
        public void PrintWarning(string s) { Logged.Add("WARN " + s); }
        public void PrintError(string s) { Logged.Add("ERROR " + s); }
    }
}
