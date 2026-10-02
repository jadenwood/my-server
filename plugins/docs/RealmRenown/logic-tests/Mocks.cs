// Behavioural test mocks for RealmRenown: only the surface the plugin touches. Not the real game.
// The real signatures are proven separately by tools/plugin-compile-check/check.sh against the shipped DLLs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
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
        // Game default from CodeHatch.Permissions.Permission (DefaultGroup.SetChatFormat).
        public string ChatFormat = "%name% : %message%";
        public List<string> Messages = new List<string>();
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this }; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
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
    public class KingsScheme
    {
        public ulong King; public string KingName;
        public bool HasKing() { return King != 0; }
        public ulong GetKingID() { return King; }
        public string GetKingName() { return KingName; }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    public class Damage { public Entity DamageSource; }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDeathEvent { public Entity Entity; public Damage KillingDamage; }
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
        public List<string> Writes = new List<string>();
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        // Like Oxide: a missing file is created (that is why the plugin must check ExistsDatafile first).
        public T ReadObject<T>(string n)
        {
            if (!File.Exists(P(n))) { var t = Activator.CreateInstance<T>(); WriteObject(n, t); return t; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(P(n)), Opts);
        }
        public void WriteObject<T>(string n, T o) { Writes.Add(n); File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts)); }
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

    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
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
    public class TimerLib
    {
        public int EveryCount;
        public Timer Every(float s, Action a) { EveryCount++; return new Timer(); }
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
