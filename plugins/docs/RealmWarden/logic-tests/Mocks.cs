// Behavioural test mocks for RealmWarden: only the surface the plugin touches. Not the real game.
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
    public struct Vector3Int { public int x, y, z; }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
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
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public List<string> Messages = new List<string>();
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this }; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Kicks = new List<string>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
        public static void Kick(Player p, string reason) { Kicks.Add(p.Name + "|" + reason); }
    }
}

namespace CodeHatch.Engine.Modules.SocialSystem
{
    public static class SocialAPI
    {
        public static Dictionary<Type, object> Registry = new Dictionary<Type, object>();
        public static Dictionary<ulong, ulong> Groups = new Dictionary<ulong, ulong>();
        public static T Get<T>() where T : class { object o; return Registry.TryGetValue(typeof(T), out o) ? (T)o : null; }
        public static ulong GetGroupId(ulong playerId) { ulong g; return Groups.TryGetValue(playerId, out g) ? g : 0UL; }
    }
}

namespace CodeHatch.Thrones.SocialSystem
{
    public class CrestScheme
    {
        public ulong GroupAtAnyPosition;   // the test sets which crest group owns the hit block
        public ulong CurrentCrestGroup(UnityEngine.Vector3 world) { return GroupAtAnyPosition; }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    [Flags] public enum DamageType { Unknown = 0, Melee = 4, Healing = 1024, Siege = 131072, Salvage = 4194304 }
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
    public class PlayerCaptureEvent : BaseEvent { public Entity Captor; public Player Target; }
}

namespace CodeHatch.Networking.Events.Players
{
    using CodeHatch.Engine.Networking;
    public class PlayerMessageEvent : CodeHatch.Networking.Events.NetworkEvent { public Player Player; public string Message; }
    public class PlayerFirstSpawnEvent : CodeHatch.Networking.Events.NetworkEvent { public Player Player; public bool AtFirstSpawn; }
    public class PlayerPreSpawnCompleteEvent : CodeHatch.Networking.Events.NetworkEvent { public Player Player; }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDamageEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage Damage; }
    public class EntityDeathEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage KillingDamage; }
}

namespace CodeHatch.Thrones.AncientThrone
{
    using CodeHatch.Engine.Networking;
    public class AncientThroneCaptureEvent : CodeHatch.Networking.Events.BaseEvent
    {
        public enum States { Capturing, Cancelled, Completed }
        public Player Player; public States State;
    }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    using CodeHatch.Damaging;
    public class Grid { public UnityEngine.Vector3 World; public UnityEngine.Vector3 LocalToWorldCoordinate(Vector3Int p) { return World; } }
    public class CubeDamageEvent : CodeHatch.Networking.Events.NetworkEvent { public Grid Grid = new Grid(); public Vector3Int Position; public Damage Damage; }
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
        public void RegisterPermission(string p, object pl) { }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class TimerLib
    {
        public List<Action> Pending = new List<Action>();
        public List<Action> Repeating = new List<Action>();
        public object Every(float s, Action a) { Repeating.Add(a); return null; }
        public object Once(float s, Action a) { Pending.Add(a); return null; }
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
        protected void LogToFile(string file, string text, Oxide.Core.Plugins.Plugin p, bool dated, bool stamp) { FileLog.Add(file + ": " + text); }
    }
}
