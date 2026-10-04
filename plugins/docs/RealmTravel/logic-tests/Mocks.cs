// Behaviour test mocks for RealmTravel: only the surface the plugin touches. NOT the real game. Type and member names
// follow the 2.0.3867 Assembly-CSharp metadata (tools/plugin-compile-check/check.sh proves the plugin compiles against
// the real ones); behaviour follows the decompiled code where the plugin depends on it:
//   CharacterTeleport.Teleport moves the entity on the server (the game's own /tp relies on it) - with knobs for a
//   teleport that throws and one the server's position does not follow;
//   ItemCollection.AutoMergeAdd adds what fits and AutoCount measures it (a units-capacity inventory);
//   InvBlueprints.GetBlueprintForName(name, true, true) matches an exact name case-insensitively, then a ResourceType key;
//   CrestScheme answers from a list of round crest zones (group, owner, siege).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return x + "," + y + "," + z; }
    }
}

namespace CodeHatch
{
    public enum ResourceType { Wood, Stone, Metal, IronOre, Clay, Grain, Bread, CookedMeat, Apple, Berry, Lumber, Count }
}

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
    public class Entity : UnityEngine.Component
    {
        public UnityEngine.Vector3 Position;
        public bool IsPlayer = true;
        public Player Owner;
        public List<object> Components = new List<object>();
        public bool TeleportThrows;                                   // the game call fails
        public bool TeleportIgnored;                                  // the server's position does not follow
        public List<UnityEngine.Vector3> Teleports = new List<UnityEngine.Vector3>();
        public T TryGet<T>() where T : class { return Components.OfType<T>().FirstOrDefault(); }
        public T GetOrCreate<T>() where T : class, new()
        {
            T t = TryGet<T>();
            if (t != null) return t;
            t = new T();
            var tp = t as CodeHatch.Engine.Behaviours.CharacterTeleport;
            if (tp != null) tp.Entity = this;
            Components.Add(t);
            return t;
        }
    }
}

namespace CodeHatch.Engine.Behaviours
{
    using CodeHatch.Engine.Core.Cache;
    public class CharacterTeleport
    {
        public Entity Entity;
        public void Teleport(UnityEngine.Vector3 position)
        {
            if (Entity.TeleportThrows) throw new InvalidOperationException("teleport failed");
            Entity.Teleports.Add(position);
            if (!Entity.TeleportIgnored) Entity.Position = position;
        }
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
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this }; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
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
        public static Dictionary<ulong, ulong> GroupOf = new Dictionary<ulong, ulong>();
        public static T Get<T>() where T : class { object o; return Registry.TryGetValue(typeof(T), out o) ? (T)o : null; }
        public static ulong GetGroupId(ulong playerId) { ulong g; return GroupOf.TryGetValue(playerId, out g) ? g : 0UL; }
    }
}

namespace CodeHatch.Thrones.SocialSystem
{
    public class CrestScheme
    {
        public class Zone { public float X, Z, R; public ulong Group; public ulong Owner; public bool Siege; }
        public List<Zone> Zones = new List<Zone>();
        public bool Throws;
        Zone At(UnityEngine.Vector3 p)
        {
            if (Throws) throw new InvalidOperationException("crest map unavailable");
            return Zones.FirstOrDefault(z => (p.x - z.X) * (p.x - z.X) + (p.z - z.Z) * (p.z - z.Z) <= z.R * z.R);
        }
        public ulong CurrentCrestGroup(UnityEngine.Vector3 p) { var z = At(p); return z != null ? z.Group : 0UL; }
        public ulong GetCrestPlayer(UnityEngine.Vector3 p) { var z = At(p); return z != null ? z.Owner : 0UL; }
        public bool IsUnderSiege(UnityEngine.Vector3 p) { var z = At(p); return z != null && z.Siege; }
    }
}

namespace CodeHatch.Thrones.Capture
{
    public class PlayerCaptureManager { public bool Captured; public bool HoldingCaptive; public ulong CaptivePlayerID; }
}

namespace CodeHatch.Thrones.AncientThrone
{
    using CodeHatch.Engine.Networking;
    public class AncientThrone { public static UnityEngine.Vector3 EntityPosition; }
    public class AncientThroneCaptureEvent : CodeHatch.Networking.Events.BaseEvent
    {
        public enum States { Capturing, Cancelled, Completed }
        public Player Player; public States State;
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    [Flags]
    public enum DamageType { Unknown = 0, Suicide = 1, Impact = 2, Melee = 4, Projectile = 8, Fire = 0x10, Healing = 0x400, Falling = 0x800, Slash = 0x10000 }
    public class Damage { public float Amount; public Entity DamageSource; public DamageType DamageTypes = DamageType.Melee | DamageType.Slash; }
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
        public static List<InvItemBlueprint> All = new List<InvItemBlueprint>
        {
            new InvItemBlueprint { Name = "Wood", Resource = CodeHatch.ResourceType.Wood, StackLimit = 1000 },
            new InvItemBlueprint { Name = "Stone", Resource = CodeHatch.ResourceType.Stone, StackLimit = 1000 },
            new InvItemBlueprint { Name = "Bread", Resource = CodeHatch.ResourceType.Bread, StackLimit = 20 },
            new InvItemBlueprint { Name = "Apple", Resource = CodeHatch.ResourceType.Apple, StackLimit = 20 },
            new InvItemBlueprint { Name = "Iron Ore", Resource = CodeHatch.ResourceType.IronOre, StackLimit = 100 },
            new InvItemBlueprint { Name = "Torch", StackLimit = 10 },
            new InvItemBlueprint { Name = "Bandage", StackLimit = 10 },
            new InvItemBlueprint { Name = "Stone Hatchet", StackLimit = 1 },
        };
        public InvItemBlueprint GetBlueprintForResource(CodeHatch.ResourceType rt) { return All.FirstOrDefault(b => b.Resource == rt); }
        public InvItemBlueprint GetBlueprintForName(string n, bool resources, bool ignoreCase)
        {
            var bp = All.FirstOrDefault(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase));
            if (bp == null && resources) bp = All.FirstOrDefault(b => b.Resource.HasValue && b.Resource.Value.ToString() == n);
            return bp;
        }
        public static InvItemBlueprint[] GetBlueprintsContaining(string s) { return All.Where(b => b.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToArray(); }
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

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Inventory.Blueprints;
    public class Container { public ItemCollection Contents = new ItemCollection(); }
    // Units-capacity model of an inventory: AutoMergeAdd adds what fits (Capacity units in all), AutoCount measures.
    public class ItemCollection
    {
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Capacity = 100000;
        public int MergeCalls;
        public Action OnMerge;                                        // runs inside AutoMergeAdd, before the stack lands
        public bool Throws;                                           // the game refuses the change
        public int Total { get { return Counts.Values.Sum(); } }
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp) { int n; return c.Counts.TryGetValue(bp.Name, out n) ? n : 0; }
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            c.MergeCalls++;
            if (c.OnMerge != null) c.OnMerge();
            if (c.Throws) throw new InvalidOperationException("container locked");
            int room = c.Capacity - c.Total;
            if (room <= 0) return false;
            int add = Math.Min(room, s.StackAmount);
            c.Counts[s.Blueprint.Name] = AutoCount(c, s.Blueprint) + add;
            return add == s.StackAmount;
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
            Calls.Add(hook + "(" + string.Join(",", args.Select(a => Convert.ToString(a))) + ")");
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
