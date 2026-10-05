// Behaviour test mocks for RealmArrival: only the surface the plugin touches. NOT the real game or Oxide. Type and member
// names follow the 2.0.3867 Assembly-CSharp metadata (tools/plugin-compile-check/check.sh proves the plugin compiles
// against the real ones); behaviour follows the decompiled code where the plugin depends on it:
//   CharacterTeleport.Teleport moves the entity on the server (with knobs for a teleport that throws and one the
//   server's position does not follow); the spawn events carry Position / AtFirstSpawn as fields the plugin must not
//   change (the tests check); SpawnpointManager.defaultSpawnpointProvider is a static ISpawnpointProvider and
//   EventManager.CurrentEvent names the event being handled; RootCubeGrid applies a placement at once (material 0 is
//   air) and loses a colour sent to an empty cell; PlayerSleeperObject.AllSleeperObjects maps sleeper ids to entities.
// Timers run on a virtual clock (World.Clock): Every and Once fire as the tests advance it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return x + "," + y + "," + z; }
    }
    public struct Quaternion { public int Index; }
    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public int Rgb { get { return (r << 16) | (g << 8) | b; } }
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.UserInterface.Dialogues;
    public struct Vector3Int
    {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return x + "," + y + "," + z; }
    }
    public class MessageDialogue { }
    public class Popup
    {
        public string Kind, Title, Message, Confirm, Cancel;
        public Dialogue.OnSubmit Handler;
        public bool Interrupt, Broadcast;
    }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static MessageDialogue ShowPopup(this Player p, string title, string message, string buttonText, Dialogue.OnSubmit handler, bool interupt, bool broadcast)
        {
            if (p.PopupsThrow) throw new InvalidOperationException("no window");
            p.Popups.Add(new Popup { Kind = "basic", Title = title, Message = message, Confirm = buttonText, Handler = handler, Interrupt = interupt, Broadcast = broadcast });
            return new MessageDialogue();
        }
        public static void ShowConfirmPopup(this Player p, string title, string message, string confirmText, string cancelText, Dialogue.OnSubmit handler, bool interupt, bool broadcast)
        {
            if (p.PopupsThrow) throw new InvalidOperationException("no window");
            p.Popups.Add(new Popup { Kind = "confirm", Title = title, Message = message, Confirm = confirmText, Cancel = cancelText, Handler = handler, Interrupt = interupt, Broadcast = broadcast });
        }
        public static void Heal(this Player p, float amount) { p.Heals.Add("heal"); }
        public static void Nourish(this Player p, float amount) { p.Heals.Add("nourish"); }
        public static void Hydrate(this Player p, float amount) { p.Heals.Add("hydrate"); }
    }
}

namespace CodeHatch.UserInterface.Dialogues
{
    [Flags] public enum Options { Cancel = 1, OK = 2, Yes = 4, No = 8 }
    public abstract class Dialogue
    {
        public delegate void OnSubmit(Options selection, Dialogue dialogue, object contextData);
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class Entity : UnityEngine.Component
    {
        public UnityEngine.Vector3 Position;
        public UnityEngine.Vector3 Forward = new UnityEngine.Vector3(0, 0, 1);   // where the player looks (Entity.Forward)
        public bool IsPlayer = true;
        public Player Owner;
        public List<object> Components = new List<object>();
        public bool TeleportThrows;
        public bool TeleportIgnored;
        public List<UnityEngine.Vector3> Teleports = new List<UnityEngine.Vector3>();
        public List<string> Effects = new List<string>();
        public T GetOrCreate<T>() where T : class, new()
        {
            T t = Components.OfType<T>().FirstOrDefault();
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
    using CodeHatch.Common;
    using CodeHatch.Engine.Core.Cache;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public List<string> Messages = new List<string>();
        public List<Popup> Popups = new List<Popup>();
        public List<string> Heals = new List<string>();
        public bool PopupsThrow;
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

namespace CodeHatch.Engine.Core.Consoles
{
    using CodeHatch.Engine.Networking;
    public enum Severity { None, Low, Medium, Urgent }
    public class NewsFeed
    {
        public static List<string> Sent = new List<string>();
        public static void SendNews(string message, List<Player> players, Severity severity, bool isIgnored, string audioToPlay)
        { Sent.Add(message + "|" + string.Join(",", players.Select(p => p.Name)) + "|" + severity); }
    }
}

namespace CodeHatch.Engine.Modules.PeriodicEffects
{
    public class EffectDefinition
    {
        public static readonly HealthRegenerationMultiplier HealthRegenerationMultiplier = new HealthRegenerationMultiplier();
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    [Flags]
    public enum DamageType { Unknown = 0, Melee = 4, Projectile = 8, Healing = 0x400, Falling = 0x800, Slash = 0x10000 }
    public class Damage { public float Amount; public Entity DamageSource; public DamageType DamageTypes = DamageType.Melee | DamageType.Slash; }
}

namespace CodeHatch.Networking.Events
{
    public class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
    public class NetworkEvent : BaseEvent { }
    public static class EventManager
    {
        public static BaseEvent CurrentEvent { get; set; }
    }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDamageEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage Damage; }
}

namespace CodeHatch.Networking.Events.Players
{
    using CodeHatch.Engine.Networking;
    public class PlayerEvent : CodeHatch.Networking.Events.NetworkEvent
    {
        public Player Player;
        public ulong PlayerId { get { return Player != null ? Player.Id : 0; } }
    }
    public class PlayerSpawnEvent : PlayerEvent { public UnityEngine.Vector3 Position { get; set; } public int PositionSets; }
    public class PlayerFirstSpawnEvent : PlayerSpawnEvent { public bool AtFirstSpawn; }
    public class PlayerPreSpawnCompleteEvent : PlayerEvent { public UnityEngine.Vector3 PreSpawnPosition; public UnityEngine.Vector3 PostSpawnPosition; }
    public class PlayerRespawnEvent : PlayerSpawnEvent { }
    public class PlayerRespawnRandomlyEvent : PlayerRespawnEvent { }
    public class PlayerRespawnNormalEvent : PlayerRespawnEvent { }
    public class PlayerRespawnAtBedEvent : PlayerRespawnEvent { }
    public class PlayerRespawnAtBaseEvent : PlayerRespawnEvent { }
}

namespace CodeHatch.StarForge.Sleeping
{
    using CodeHatch.Engine.Core.Cache;
    public class PlayerSleeperObject
    {
        public static Dictionary<ulong, Entity> AllSleeperObjects = new Dictionary<ulong, Entity>();
    }
}

namespace CodeHatch.Thrones.AncientThrone
{
    public class AncientThrone { public static UnityEngine.Vector3 EntityPosition; }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Networking;
    public class CubeEvent : CodeHatch.Networking.Events.BaseEvent { public ushort GridID { get; set; } public Player Sender { get; set; } }
    public class CubeDamageEvent : CubeEvent { public Vector3Int Position { get; set; } public Damage Damage { get; set; } }
    public class CubePlaceEvent : CubeEvent { public Vector3Int Position { get; set; } public byte Material; }
}

namespace CodeHatch.Blocks
{
    using CodeHatch.Blocks.Networking.Events;
    using CodeHatch.Common;
    using CodeHatch.Engine.Networking;
    using UnityEngine;
    public struct CubeInfo
    {
        public static readonly Quaternion[] PossibleRotations = Enumerable.Range(0, 24).Select(i => new Quaternion { Index = i }).ToArray();
        public byte MaterialID { get; set; }
        public Color32 CubeColor { get; set; }
    }
    public class RootCubeGrid
    {
        public class Call { public string Kind; public Vector3Int Pos; public int Mat; public bool Collect, Owned; public int Rgb; public DateTime At; }
        public Dictionary<(int, int, int), CubeInfo> Cells = new Dictionary<(int, int, int), CubeInfo>();
        public List<Call> Calls = new List<Call>();
        public Action<CubePlaceEvent> PlaceHook;                    // Oxide's OnCubePlacement, called inside the place call
        public Player ServerPlayer = new Player(9999999999, "Server") { IsServer = true };
        public Func<DateTime> Now = () => DateTime.MinValue;
        public CubeInfo GetCubeInfoAtLocal(Vector3Int p) { CubeInfo c; return Cells.TryGetValue((p.x, p.y, p.z), out c) ? c : default(CubeInfo); }
        public void PlaceCubeAtLocal(Vector3Int p, byte materialID, byte prefabID, Quaternion rotation, bool collectPreviousCube, bool isOwnedByPlacer, float delayTime)
        {
            Calls.Add(new Call { Kind = "place", Pos = p, Mat = materialID, Collect = collectPreviousCube, Owned = isOwnedByPlacer, At = Now() });
            var evt = new CubePlaceEvent { Position = p, Material = materialID, Sender = ServerPlayer };
            if (PlaceHook != null) PlaceHook(evt);
            if (evt.Cancelled) return;
            if (materialID == 0) Cells.Remove((p.x, p.y, p.z));
            else Cells[(p.x, p.y, p.z)] = new CubeInfo { MaterialID = materialID, CubeColor = new Color32(128, 128, 128, 255) };
        }
        public void ColorCubeAtLocal(Vector3Int p, Color32 c)
        {
            Calls.Add(new Call { Kind = "paint", Pos = p, Rgb = c.Rgb, At = Now() });
            CubeInfo info;
            if (!Cells.TryGetValue((p.x, p.y, p.z), out info)) return;    // no block: the colour is lost
            info.CubeColor = c;
            Cells[(p.x, p.y, p.z)] = info;
        }
        // As the real grid at the origin with a 1.2 m scale: LocalToWorldCoordinate is the cell's centre (TransformPoint),
        // WorldToLocalCoordinate rounds each axis (new Vector3Int(Vector3) uses Mathf.RoundToInt, round half to even).
        public Vector3 LocalToWorldCoordinate(Vector3Int p) { return new Vector3(p.x * 1.2f, p.y * 1.2f, p.z * 1.2f); }
        public Vector3Int WorldToLocalCoordinate(Vector3 v)
        {
            return new Vector3Int((int)Math.Round(v.x / 1.2f), (int)Math.Round(v.y / 1.2f), (int)Math.Round(v.z / 1.2f));
        }
        public int Mat(int x, int y, int z) { return GetCubeInfoAtLocal(new Vector3Int(x, y, z)).MaterialID; }
        public int Rgb(int x, int y, int z) { return GetCubeInfoAtLocal(new Vector3Int(x, y, z)).CubeColor.Rgb; }
        public void Put(int x, int y, int z, int mat) { Cells[(x, y, z)] = new CubeInfo { MaterialID = (byte)mat, CubeColor = new Color32(200, 200, 200, 255) }; }
    }
    public static class BlockManager { public static RootCubeGrid DefaultCubeGrid; }
}

// Global namespace, as in the real Assembly-CSharp.dll.
public interface ISpawnpointProvider
{
    UnityEngine.Vector3 GetSpawnPoint(CodeHatch.Engine.Core.Cache.Entity player);
    UnityEngine.Vector3[] GetAllSpawnPoints(CodeHatch.Engine.Core.Cache.Entity player);
    UnityEngine.Vector3 GetRandomSpawnPoint();
}
public static class SpawnpointManager { public static ISpawnpointProvider defaultSpawnpointProvider; }
public class AudioController
{
    public static List<string> Played = new List<string>();
    public static void Play(string audioId, CodeHatch.Engine.Core.Cache.Entity transform, params CodeHatch.Engine.Networking.Player[] players)
    { Played.Add(audioId + "|" + (transform != null && transform.Owner != null ? transform.Owner.Name : "") + "|" + players.Length); }
}
public class GameClock
{
    public static GameClock Instance;
    public float TimeOfDay = 12f;
    public float HourOfSunriseStart = 6f, HourOfSunsetStart = 20f, HourOfSunsetEnd = 22f;
}
public class HealthRegenerationMultiplier
{
    public static int Applied;
    public void Apply(CodeHatch.Engine.Core.Cache.Entity e) { Applied++; e.Effects.Add("regen"); }
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
            Calls.Add(hook + "(" + string.Join(",", args.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture))) + ")");
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
        public List<string> Written = new List<string>();
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
        public DataFileSystem() { }
        public DataFileSystem(string directory) { Dir = directory; }
        public string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public T ReadObject<T>(string n) where T : new()
        {
            if (!File.Exists(P(n))) { var t = new T(); WriteObject(n, t); return t; }
            string text = File.ReadAllText(P(n));
            if (text.Trim().Length == 0) return default(T);                  // Newtonsoft returns null for an empty file
            return JsonSerializer.Deserialize<T>(text, Opts);
        }
        public void WriteObject<T>(string n, T o) { Writes++; Written.Add(n); File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts)); }
    }
    public class OxideMod { public DataFileSystem DataFileSystem = new DataFileSystem(); public string ConfigDirectory; }
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
    public class Timer
    {
        public bool Destroyed;
        public DateTime Due;
        public float Interval;
        public bool Repeat;
        public Action Callback;
        public void Destroy() { Destroyed = true; }
    }
    // Timers on the tests' virtual clock: Advance runs every timer that falls due, in order of due time.
    public class TimerLib
    {
        public static Func<DateTime> Now = () => DateTime.MinValue;
        public List<Timer> All = new List<Timer>();
        public int EveryCount;
        public Timer Every(float s, Action a) { EveryCount++; var t = new Timer { Due = Now().AddSeconds(s), Interval = s, Repeat = true, Callback = a }; All.Add(t); return t; }
        public Timer Once(float s, Action a) { var t = new Timer { Due = Now().AddSeconds(s), Interval = s, Repeat = false, Callback = a }; All.Add(t); return t; }
        public void RunDue()
        {
            for (int guard = 0; guard < 10000; guard++)
            {
                DateTime now = Now();
                Timer next = All.Where(t => !t.Destroyed && t.Due <= now).OrderBy(t => t.Due).FirstOrDefault();
                if (next == null) return;
                if (next.Repeat) next.Due = next.Due.AddSeconds(next.Interval); else { next.Destroyed = true; All.Remove(next); }
                next.Callback();
            }
        }
        public void DestroyAll() { foreach (var t in All) t.Destroyed = true; All.Clear(); }
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
