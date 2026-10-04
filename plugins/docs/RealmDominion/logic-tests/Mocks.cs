// Behavioural test mocks for RealmDominion: only the surface the plugin touches. NOT the real game. Shapes follow the
// shipped 2.0.3867 metadata (names and members); behaviour is the simplest that lets the scenarios run.
using System;
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
        public bool Alive = true;
        public List<string> Messages = new List<string>();
        public List<string> Popups = new List<string>();
        public Player(ulong id, string name)
        {
            Id = id; Name = name;
            Entity = new Entity { Owner = this };
        }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
        public static void BroadcastMessage(string m) { Broadcasts.Add(m); }
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    public static class PlayerExtensions
    {
        public static bool ThrowOnPopup;
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static bool IsAlive(this Player p) { return p.Alive; }
        public static object ShowPopup(this Player p, string title, string message, string buttonText, object handler, bool interupt, bool broadcast)
        {
            if (ThrowOnPopup) throw new InvalidOperationException("no UI on this server");
            p.Popups.Add(title + "|" + message + "|" + buttonText + "|" + broadcast);
            return null;
        }
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
        public bool Cancelled;
        public void Cancel(string reason) { Cancelled = true; }
    }
    public class NetworkEvent : BaseEvent { }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityEvent : CodeHatch.Networking.Events.NetworkEvent { public Entity Entity; }
    public class EntityDamageEvent : EntityEvent { public Damage Damage; }
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
        public void WriteObject<T>(string n, T o) { Writes++; File.WriteAllText(P(n), JsonSerializer.Serialize(o, o.GetType(), Opts)); }
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

    public class Timer
    {
        public bool Destroyed;
        public float Interval;
        public Action Callback;
        public void Destroy() { Destroyed = true; }
    }
    public class ConfigFile
    {
        public string Json;
        public T ReadObject<T>() { return Json == null ? default(T) : JsonSerializer.Deserialize<T>(Json, DataFileSystem.Opts); }
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
        public HashSet<string> Registered = new HashSet<string>();
        public void RegisterPermission(string p, object pl) { Registered.Add(p); }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class TimerLib
    {
        public List<Timer> Timers = new List<Timer>();
        public Timer Every(float s, Action a) { var t = new Timer { Interval = s, Callback = a }; Timers.Add(t); return t; }
        public Timer Once(float s, Action a) { var t = new Timer { Interval = s, Callback = a }; Timers.Add(t); return t; }
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
