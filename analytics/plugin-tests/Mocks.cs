// Behavioural test mocks for RealmStats: only the surface the plugin touches. Not the real game or Oxide.
// The real signatures are proven separately by tools/plugin-compile-check/check.sh against the shipped DLLs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodeHatch.Thrones.SocialSystem
{
    public class Guild { public string Name; }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class Entity { public bool IsPlayer = true; public Player Owner; }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public CodeHatch.Thrones.SocialSystem.Guild Guild;
        public List<string> Messages = new List<string>();
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this }; }
        public string All() { return string.Join("\n", Messages); }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static Player GetPlayerById(ulong id) { return ClientPlayers.FirstOrDefault(p => p.Id == id); }
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static CodeHatch.Thrones.SocialSystem.Guild GetGuild(this Player p) { return p.Guild; }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    // Values copied from the shipped DLL ([DEC] CodeHatch.Damaging/DamageType.cs).
    [Flags]
    public enum DamageType
    {
        Unknown = 0, Suicide = 1, Impact = 2, Melee = 4, Projectile = 8, Fire = 0x10, Explosion = 0x20, Cut = 0x40,
        Breach = 0x80, Plasma = 0x100, Harvest = 0x200, Healing = 0x400, Falling = 0x800, OutOfBounds = 0x1000,
        Plague = 0x2000, Pierce = 0x4000, Bash = 0x8000, Slash = 0x10000, Siege = 0x20000, Hunger = 0x40000,
        Drowning = 0x80000, God = 0x100000, Thirst = 0x200000, Salvage = 0x400000, Any = 0x40000000
    }
    public class Damage { public Entity DamageSource; public DamageType DamageTypes; }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDeathEvent { public Entity Entity; public Damage KillingDamage { get; set; } }
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
    // Mirrors Oxide.Core DataFileSystem: "<dir>/<name>.json", "/" makes sub-folders, ReadObject creates missing files.
    public class DataFileSystem
    {
        public string Dir;
        public int Writes;
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        public string P(string n) { return Path.Combine(Dir, n.Replace('/', Path.DirectorySeparatorChar) + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public T ReadObject<T>(string n) where T : new()
        {
            if (!File.Exists(P(n))) { var t = new T(); WriteObject(n, t); return t; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(P(n)), Opts);
        }
        public void WriteObject<T>(string n, T o)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(P(n)));
            File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts));
            Writes++;
        }
        public void DeleteDataFile(string n) { if (File.Exists(P(n))) File.Delete(P(n)); }
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
        public void WriteObject(object o, bool sync) { Json = JsonSerializer.Serialize(o, o.GetType(), DataFileSystem.Opts); Writes++; }
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
        public List<Action> Repeating = new List<Action>();
        public object Every(float s, Action a) { Repeating.Add(a); return null; }
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
