// Behaviour test mocks for RealmHeraldry: only the surface the plugin touches. NOT the real game. Type and member names
// follow the 2.0.3867 Assembly-CSharp metadata (tools/plugin-compile-check/check.sh proves the plugin compiles against
// the real ones); behaviour follows the decompiled code where the plugin depends on it:
//   Guild(Guild copy) copies BaseID, Name and the Banner reference; GuildUpdateEvent raised on the server is handled by
//   ServerSupplier.OnGuildUpdate, which looks the guild up by BaseID and calls Guild.UpdateGuild (Name and Banner), or
//   cancels the event when no such guild exists. EventManager.CallEvent records every event here, with knobs for a game
//   that refuses (cancels) or throws.
//   BannerData.CurrentColor / CurrentPaternColor are UnityEngine.Color (floats r, g, b, a), read and set by reflection.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnityEngine
{
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }
}

namespace CodeHatch.Thrones.Banner
{
    public class BannerData
    {
        public int CurrentBanner { get; set; }
        public int CurrentPattern { get; set; }
        public UnityEngine.Color CurrentColor { get; set; }
        public UnityEngine.Color CurrentPaternColor { get; set; }
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static object ShowPopup(this Player p, string title, string message, string button, object handler, bool interupt, bool broadcast)
        {
            if (p.PopupsThrow) throw new InvalidOperationException("no window");
            p.Popups.Add(title + "|" + message + "|" + button + "|" + broadcast);
            return null;
        }
    }
}

namespace CodeHatch.Engine.Networking
{
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer;
        public List<string> Messages = new List<string>();
        public List<string> Popups = new List<string>();
        public bool PopupsThrow;
        public Player(ulong id, string name) { Id = id; Name = name; }
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
    public class Member { public ulong PlayerId; public Member(ulong id) { PlayerId = id; } }
    public class Members
    {
        public List<Member> List = new List<Member>();
        public bool Has(ulong id) { return List.Any(m => m.PlayerId == id); }
        public int MemberCount() { return List.Count; }
        public IEnumerable<Member> EachMember() { return List.ToList(); }
    }
}

namespace CodeHatch.Networking.Events
{
    using CodeHatch.Thrones.Banner;
    using CodeHatch.Thrones.SocialSystem;
    public class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
    public class GuildBannerUpdateEvent : BaseEvent
    {
        public ulong GuildID; public BannerData Banner;
        public GuildBannerUpdateEvent(BannerData banner, ulong guildID) { Banner = banner; GuildID = guildID; }
    }
    public static class EventManager
    {
        public static List<BaseEvent> Events = new List<BaseEvent>();
        public static bool Refuse;            // the game cancels the guild update
        public static bool Throws;            // the game throws
        public static void CallEvent(BaseEvent e)
        {
            if (Throws) throw new InvalidOperationException("event system down");
            Events.Add(e);
            var u = e as GuildUpdateEvent;
            if (u == null) return;
            if (Refuse) { u.Cancel("You do not have permission."); return; }
            var scheme = CodeHatch.Engine.Modules.SocialSystem.SocialAPI.Get<GuildScheme>();
            Guild g = scheme != null ? scheme.TryGetGuild(u.TheGuild.BaseID) : null;
            if (g == null) { u.Cancel("No guild found by id " + u.TheGuild.BaseID + "."); return; }
            g.UpdateGuild(u.TheGuild);         // ServerSupplier.OnGuildUpdate
        }
    }
}

namespace CodeHatch.Thrones.SocialSystem
{
    using CodeHatch.Engine.Modules.SocialSystem;
    using CodeHatch.Engine.Networking;
    using CodeHatch.Thrones.Banner;
    public class Guild
    {
        public ulong BaseID;
        public string Name;
        public BannerData Banner;
        private Members members = new Members();
        public Guild(ulong id) { BaseID = id; Name = "My Guild"; Banner = new BannerData(); }
        public Guild(Guild copy) { BaseID = copy.BaseID; Name = copy.Name; Banner = copy.Banner; }
        public Members Members() { return members; }
        public void UpdateGuild(Guild other) { Name = other.Name; Banner = other.Banner; }
    }
    public class GuildUpdateEvent : CodeHatch.Networking.Events.BaseEvent
    {
        public Guild TheGuild;
        public GuildUpdateEvent(Guild g) { TheGuild = g; }
    }
    public class GuildScheme
    {
        public List<Guild> Guilds = new List<Guild>();
        public Guild TryGetGuild(ulong id) { return Guilds.FirstOrDefault(g => g.BaseID == id); }
        public Guild TryGetGuildByMember(ulong player) { return Guilds.FirstOrDefault(g => g.Members().Has(player)); }
    }
    public class KingsScheme
    {
        public ulong KingId;
        public bool HasKing() { return KingId != 0; }
        public bool IsKing(Player p) { return p != null && p.Id == KingId; }
        public ulong GetKingID() { return KingId; }
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
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Logged.Add(s); }
        public void PrintWarning(string s) { Logged.Add("WARN " + s); }
        public void PrintError(string s) { Logged.Add("ERROR " + s); }
    }
}
