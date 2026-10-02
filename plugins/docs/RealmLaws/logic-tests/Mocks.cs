// Behavioural test mocks for RealmLaws: only the surface the plugin touches. Not the real game.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    using CodeHatch.Thrones.SocialSystem;
    public struct Vector3Int { public int x, y, z; }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.Inventory; }
        public static Guild GetGuild(this Player p) { return p.Guild; }
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
        public T TryGet<T>() where T : class { return null; }
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
        public List<string> Messages = new List<string>();
        public Container Inventory = new Container();
        public Guild Guild;
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
}

namespace CodeHatch.Thrones.SocialSystem
{
    using CodeHatch.Engine.Networking;
    public class Guild { public string Name; public ulong OwnerId; }
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

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    public class Damage { public float Amount; public Entity DamageSource; }
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

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    public class Grid { public UnityEngine.Vector3 World; public UnityEngine.Vector3 LocalToWorldCoordinate(Vector3Int p) { return World; } }
    public class CubePlaceEvent : CodeHatch.Networking.Events.BaseEvent { public ulong SenderId; public Grid Grid; public Vector3Int Position; }
}

namespace CodeHatch.Inventory.Blueprints
{
    public class InvItemBlueprint { public string Name; public T TryGet<T>() where T : class { return null; } }
    public class InvGameItemStack { public InvItemBlueprint Bp; public int Count; public InvGameItemStack(InvItemBlueprint bp, int c, object o) { Bp = bp; Count = c; } }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static string[] Names = { "Wood", "Stone", "Iron" };
        public InvItemBlueprint GetBlueprintForName(string n, bool a, bool b)
        {
            string hit = Names.FirstOrDefault(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase));
            return hit == null ? null : new InvItemBlueprint { Name = hit };
        }
    }
}

namespace CodeHatch.Inventory.Blueprints.Components
{
    public class ContainerManagement { public int StackLimit; }
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Inventory.Blueprints;
    public class Container { public ItemCollection Contents = new ItemCollection(); }
    public class ItemCollection
    {
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int Capacity = 100000;
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp) { int n; return c.Counts.TryGetValue(bp.Name, out n) ? n : 0; }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int q) { int n = AutoCount(c, bp); if (n < q) return false; c.Counts[bp.Name] = n - q; return true; }
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            int total = c.Counts.Values.Sum(); int room = c.Capacity - total; if (room <= 0) return false;
            int add = Math.Min(room, s.Count); c.Counts[s.Bp.Name] = AutoCount(c, s.Bp) + add; return add == s.Count;
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
        public object Call(string hook, params object[] args) { Calls.Add(hook + "(" + string.Join(",", args.Select(a => a is string[] ? "[...]" : Convert.ToString(a))) + ")"); return Handler != null ? Handler(hook, args) : null; }
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
        public void RegisterPermission(string p, object pl) { }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class TimerLib
    {
        public List<Action> Pending = new List<Action>();
        public object Every(float s, Action a) { return null; }
        public object Once(float s, Action a) { Pending.Add(a); return null; }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<string> Log = new List<string>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Log.Add(s); }
        public void PrintWarning(string s) { Log.Add("WARN " + s); }
        public void PrintError(string s) { Log.Add("ERROR " + s); }
    }
}
