// Behavioural test mocks for RealmPainter: only the surface the plugin touches. Not the real game, Unity or Oxide.
// Member names follow the 2.0.3867 Assembly-CSharp.dll metadata and UnityEngine, so the plugin's reflection code
// (Bounds2.min/size, Transform.InverseTransformPoint, BoxCollider.center/size, Physics.RaycastAll) runs against them.
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
    public struct Vector2 { public float x, y; }
    public struct Bounds
    {
        public Vector3 min { get; set; }
        public Vector3 max { get; set; }
    }
    public class Object { public string name { get; set; } }
    public class Component : Object
    {
        public Component Parent;
        public GameObject gameObject { get; set; }
        public Transform transform { get; set; }
        public Component GetComponentInParent(Type t)
        {
            for (Component c = this; c != null; c = c.Parent) if (t.IsInstanceOfType(c)) return c;
            return null;
        }
    }
    public class MonoBehaviour : Component { }
    // Translation and per-axis scale only: enough to check the face probe's corner maths.
    public class Transform : Component
    {
        public Vector3 Offset;
        public Vector3 Scale = new Vector3(1, 1, 1);
        public Vector3 TransformPoint(Vector3 p) { return new Vector3(Offset.x + p.x * Scale.x, Offset.y + p.y * Scale.y, Offset.z + p.z * Scale.z); }
        public Vector3 InverseTransformPoint(Vector3 p) { return new Vector3((p.x - Offset.x) / Scale.x, (p.y - Offset.y) / Scale.y, (p.z - Offset.z) / Scale.z); }
    }
    public class GameObject : Object
    {
        public List<Component> Children = new List<Component>();
        public Component[] GetComponentsInChildren(Type t) { return Children.Where(c => t.IsInstanceOfType(c)).ToArray(); }
    }
    public class Collider : Component
    {
        public bool isTrigger { get; set; }
        public Bounds bounds { get; set; }
    }
    public class BoxCollider : Collider
    {
        public Vector3 center { get; set; }
        public Vector3 size { get; set; }
    }
    public struct RaycastHit
    {
        public Collider collider { get; set; }
        public float distance { get; set; }
    }
    public static class Physics
    {
        public static List<RaycastHit> Hits = new List<RaycastHit>();
        public static int Casts;
        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction, float maxDistance) { Casts++; return Hits.Where(h => h.distance <= maxDistance).ToArray(); }
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using UnityEngine;
    public struct Bounds2 { public Vector2 min; public Vector2 size; }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using UnityEngine;
    public sealed class Entity : MonoBehaviour
    {
        public static List<Entity> All = new List<Entity>();
        public static int Scans;
        public ulong NetViewID;
        public Vector3 Position;
        public Vector3 Forward = new Vector3(0, 0, 1);
        public List<object> Parts = new List<object>();
        public T TryGet<T>() where T : class { return Parts.OfType<T>().FirstOrDefault(); }
        public static List<Entity> TryGetAll() { Scans++; return new List<Entity>(All); }
        public static Entity TryGetFromViewID(ulong id) { return All.FirstOrDefault(e => e.NetViewID == id); }
    }
}

namespace CodeHatch
{
    using UnityEngine;
    public class LookBridge { public Vector3 Forward; }
}

namespace CodeHatch.Thrones.Painting
{
    using CodeHatch.Common;
    using UnityEngine;
    public class PaintArea
    {
        public static float TEXEL_WORLD_SIZE = 0.008f;
        public Bounds2 Bounds;
        public int TextureWidth, TextureHeight;
        public int Sets;
        public bool Refuse;                               // simulate a server where LoadImage throws
        byte[] data;
        public byte[] PaintData
        {
            get { return data; }
            set { if (Refuse) throw new InvalidOperationException("LoadImage failed"); data = value; Sets++; }
        }
        public PaintArea() { Bounds.size.x = TEXEL_WORLD_SIZE; Bounds.size.y = TEXEL_WORLD_SIZE; }
    }
    public class PaintableObject : MonoBehaviour
    {
        public PaintArea ColorArea = new PaintArea();
        public PaintArea DepthNormalArea = new PaintArea();
        public Transform PaintingSpaceTransform { get; set; }
        public int Applies;
        public void ApplyPainting(bool broadcast) { Applies++; }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer;
        public Entity Entity;
        public List<string> Messages = new List<string>();
        public Player(ulong id, string name) { Id = id; Name = name; }
        public string All() { return string.Join("\n", Messages); }
        public static Player Local = new Player(0, "Server") { IsServer = true };
    }
}

namespace CodeHatch.Networking.Events
{
    public abstract class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public virtual void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
    public delegate void EventSubscriber<T>(T theEvent) where T : BaseEvent;
    public enum EventHandlerOrder { VeryEarly, Early, Normal, Late, VeryLate }
    // On a server CallEvent handles an event locally (subscribers run), then sends it to the clients.
    public static class EventManager
    {
        public static List<BaseEvent> Called = new List<BaseEvent>();
        public static List<Delegate> Subs = new List<Delegate>();
        public static bool FailSubscribe;
        public static void CallEvent(BaseEvent e) { Called.Add(e); Deliver(e); }
        public static void Deliver(BaseEvent e)
        {
            foreach (Delegate d in Subs.ToList()) if (d.GetType().GetGenericArguments()[0].IsInstanceOfType(e)) d.DynamicInvoke(e);
        }
        public static void Subscribe<T>(EventSubscriber<T> s, EventHandlerOrder o) where T : BaseEvent { if (FailSubscribe) throw new InvalidOperationException("no events"); Subs.Add(s); }
        public static void Unsubscribe<T>(EventSubscriber<T> s) where T : BaseEvent { Subs.Remove(s); }
    }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Networking;
    using CodeHatch.Thrones.Painting;
    public class PaintObjectUpdateEvent : CodeHatch.Networking.Events.BaseEvent
    {
        public Entity Entity;
        public PaintableObject PaintableObject { get; set; }
        public Player Sender { get; set; }
        public byte[] SentPng;                            // what Write would serialise for the clients
        public PaintObjectUpdateEvent(Entity entity, PaintableObject po)
        {
            Entity = entity; PaintableObject = po; Sender = Player.Local;
            SentPng = po != null ? po.ColorArea.PaintData : null;
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
        public object Call(string hook, params object[] args) { Calls.Add(hook); return Handler != null ? Handler(hook, args) : null; }
    }
}

namespace Oxide.Core
{
    public class DataFileSystem
    {
        public string Dir;
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true, PropertyNameCaseInsensitive = true };
        public int Writes;
        public Dictionary<string, int> Reads = new Dictionary<string, int>();
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public T ReadObject<T>(string n) where T : new()
        {
            Reads[n] = Reads.TryGetValue(n, out int c) ? c + 1 : 1;
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
        public List<string> Registered = new List<string>();
        public void RegisterPermission(string p, object pl) { Registered.Add(p); }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
    public class TimerLib
    {
        public int EveryCalls;
        public Timer Every(float s, Action a) { EveryCalls++; return new Timer(); }
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
