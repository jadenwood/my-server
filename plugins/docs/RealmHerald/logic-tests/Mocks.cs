// Behavioural test mocks for RealmHerald: only the surface the plugin touches. Not the real game or Oxide.
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

namespace CodeHatch.Engine.Networking
{
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer;
        public List<string> Messages = new List<string>();
        public Player(ulong id, string name) { Id = id; Name = name; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static int PlayerLimit = 50;
        public static List<string> Broadcasts = new List<string>();
        public static void BroadcastMessage(string m) { Broadcasts.Add(m); }
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
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        public int Writes;
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
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
        public int Writes;
        public T ReadObject<T>() { return Json == null ? default(T) : JsonSerializer.Deserialize<T>(Json, DataFileSystem.Opts); }
        public void WriteObject(object o, bool sync) { Writes++; Json = JsonSerializer.Serialize(o, o.GetType(), DataFileSystem.Opts); }
    }
    public class LangLib
    {
        public Dictionary<string, string> Msgs = new Dictionary<string, string>();
        public void RegisterMessages(Dictionary<string, string> m, object p) { foreach (var kv in m) if (!Msgs.ContainsKey(kv.Key)) Msgs[kv.Key] = kv.Value; }
        public string GetMessage(string k, object p, string id) { string v; return Msgs.TryGetValue(k, out v) ? v : k; }
    }
    public class PermLib
    {
        public HashSet<string> Grants = new HashSet<string>();
        public void RegisterPermission(string p, object pl) { }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class PluginsLib
    {
        public HashSet<string> Loaded = new HashSet<string>();
        public bool Exists(string name) { return Loaded.Contains(name); }
    }
    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
    public class TimerLib
    {
        public List<KeyValuePair<float, Action>> Pending = new List<KeyValuePair<float, Action>>();
        public int EveryCalls;
        public Timer Every(float s, Action a) { EveryCalls++; return new Timer(); }
        public Timer Once(float s, Action a) { Pending.Add(new KeyValuePair<float, Action>(s, a)); return new Timer(); }
        public void RunPending() { var p = Pending.ToList(); Pending.Clear(); foreach (var kv in p.OrderBy(x => x.Key)) kv.Value(); }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public PluginsLib plugins = new PluginsLib();
        public TimerLib timer = new TimerLib();
        public List<string> Log = new List<string>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Log.Add(s); }
        public void PrintWarning(string s) { Log.Add("WARN " + s); }
        public void PrintError(string s) { Log.Add("ERROR " + s); }
    }
}
