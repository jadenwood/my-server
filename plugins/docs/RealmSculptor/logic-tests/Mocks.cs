// Behavioural test mocks for RealmSculptor: only the surface the plugin touches. Not the real game or Oxide.
// The block grid copies the timing the decompiled game code shows (plugins/docs/RealmSculptor.md): a placement lands at
// the end of the frame, a new block takes its material's default colour, and a colour sent before the block exists is
// lost. Quaternion and Color32 exist here so the plugin's reflection binding runs against the same member names.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityEngine
{
    public class Object { public string name { get; set; } = ""; }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return x + "," + y + "," + z; }
    }
    // Stand-in: carries the index of the game's rotation table it came from.
    public struct Quaternion { public int Index; }
    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public int Rgba { get { return (r << 24) | (g << 16) | (b << 8) | a; } }
    }
    public struct Color { public float r, g, b, a; public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    public struct Vector3Int
    {
        public int x, y, z;
        public Vector3Int(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public override string ToString() { return x + "," + y + "," + z; }
    }
    public static class PlayerExtensions
    {
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
    }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    using UnityEngine;
    public class Entity
    {
        public Vector3 Position;
        public Vector3 Forward = new Vector3(0, 0, 1);
        public Player Owner;
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
        public Player(ulong id, string name) { Id = id; Name = name; Entity = new Entity { Owner = this }; }
        public string All() { return string.Join("\n", Messages); }
        public string Last() { return Messages.Count > 0 ? Messages[Messages.Count - 1] : ""; }
    }
    public static class Server
    {
        public static List<Player> ClientPlayers = new List<Player>();
        public static List<string> Broadcasts = new List<string>();
        public static void BroadcastMessage(string m) { Broadcasts.Add(m); }
    }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    [Flags] public enum DamageType { None = 0, Blunt = 2, Siege = 64, Salvage = 0x400000 }
    public class Damage
    {
        public float Amount { get; set; }
        public float ImpactDamage { get; set; }
        public float MiscDamage { get; set; }
        public DamageType DamageTypes { get; set; }
        public Entity DamageSource { get; set; }
    }
}

namespace CodeHatch.Networking.Events
{
    public enum EventHandlerOrder { VeryEarly, Early, Normal, Late, VeryLate }
    public delegate void EventSubscriber<T>(T theEvent);
    public class BaseEvent
    {
        public bool Cancelled { get; private set; }
        public string CancelReason;
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
    public static class EventManager
    {
        public static Dictionary<Type, List<Delegate>> Subs = new Dictionary<Type, List<Delegate>>();
        public static void Subscribe<T>(EventSubscriber<T> s, EventHandlerOrder order)
        {
            if (!Subs.ContainsKey(typeof(T))) Subs[typeof(T)] = new List<Delegate>();
            Subs[typeof(T)].Add(s);
        }
        public static void Unsubscribe<T>(EventSubscriber<T> s) { if (Subs.ContainsKey(typeof(T))) Subs[typeof(T)].Remove(s); }
        public static void Raise<T>(T e) { if (Subs.ContainsKey(typeof(T))) foreach (var d in Subs[typeof(T)].ToList()) ((EventSubscriber<T>)d)(e); }
        public static int Count<T>() { return Subs.ContainsKey(typeof(T)) ? Subs[typeof(T)].Count : 0; }
    }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Networking;
    using CodeHatch.Networking.Events;
    public class CubeEvent : BaseEvent
    {
        public ushort GridID { get; set; }
        public Player Sender { get; set; }
    }
    public class CubeDamageEvent : CubeEvent { public Vector3Int Position { get; set; } public Damage Damage { get; set; } }
    public class CubePlaceEvent : CubeEvent { public Vector3Int Position { get; set; } public byte Material; public byte PrefabId; }
    public class CubeDestroyEvent : CubeEvent { public Vector3Int Position { get; set; } }
    public class MassCubeDestroyEvent : CubeEvent { public Vector3Int[] Position { get; set; } }
}

namespace CodeHatch.ModTools.Textures
{
    public class TilesetMaterialInfo { public bool Colorable { get; set; } }
}

namespace CodeHatch.Blocks.Geometry
{
    using UnityEngine;
    public class OctPrefab : MonoBehaviour
    {
        public int PrefabID;
        public int PermittedRotations;
        public Vector3[] BlockOffsets = new Vector3[1];
        public bool IsMultiBlock() { return BlockOffsets.Length > 1; }
    }
}

namespace CodeHatch.Blocks.Inventory
{
    public class TilesetBlueprint { public byte MaterialID { get; set; } public byte PrefabID { get; set; } }
}

namespace CodeHatch.Inventory.Blueprints
{
    public class InvItemBlueprint
    {
        public string Name { get; set; }
        public object Part;
        public bool Has<T>() { return Part is T; }
        public T Get<T>() { return (T)Part; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance;
        public InvItemBlueprint[] AllDefinedBlueprints { get; set; }
    }
}

namespace CodeHatch.Blocks
{
    using CodeHatch.Blocks.Geometry;
    using CodeHatch.Blocks.Networking.Events;
    using CodeHatch.Common;
    using CodeHatch.Engine.Networking;
    using CodeHatch.ModTools.Textures;
    using UnityEngine;

    public struct CubeInfo
    {
        public static readonly Quaternion[] PossibleRotations = Enumerable.Range(0, 24).Select(i => new Quaternion { Index = i }).ToArray();
        public static byte GetIndexOfRotation(Quaternion rotation) { return (byte)rotation.Index; }
        public byte MaterialID { get; set; }
        public byte PrefabID { get; set; }
        public Quaternion Rotation { get; set; }
        public Color32 CubeColor { get; set; }
        public static CubeInfo Air { get { return default(CubeInfo); } }
    }

    public class OctTileset : MonoBehaviour
    {
        public int TilesetID { get; set; }
        public Color DefaultColor { get; set; }
        public float BuildTime { get; set; }
        public float Quality { get; set; }
        public bool Solid { get; set; } = true;
        public TilesetMaterialInfo[] TilesetMaterialBinding { get; set; } = new TilesetMaterialInfo[0];
        public OctPrefab[] SpecialPeices = new OctPrefab[0];
        public OctPrefab GetPrefabWithID(int id) { return SpecialPeices.FirstOrDefault(p => p.PrefabID == id); }
    }

    public class TilesetLibrary
    {
        public static TilesetLibrary Singleton;
        public OctTileset[] Tilesets { get; set; }
        public static OctTileset GetTilesetWithID(byte id, bool log) { return id > 0 && id <= Singleton.Tilesets.Length ? Singleton.Tilesets[id - 1] : null; }
        public static bool HasCubePrefab(byte id) { return id > 0 && id <= Singleton.Tilesets.Length; }
    }

    public class RootCubeGrid
    {
        public class Call { public string Kind; public Vector3Int Pos; public int Mat, Prefab, Rot; public bool Collect, Owned; public float Delay; public int Rgba; }
        public Dictionary<(int, int, int), CubeInfo> Cells = new Dictionary<(int, int, int), CubeInfo>();
        public List<Call> Calls = new List<Call>();
        public List<Call> Pending = new List<Call>();
        public Func<Vector3Int, bool> Loaded = p => true;          // cells outside a loaded page are not placed
        public Action<CubePlaceEvent> PlaceHook;                    // Oxide's OnCubePlacement, called inside the place call
        public Player ServerPlayer = new Player(0, "Server") { IsServer = true };

        public CubeInfo GetCubeInfoAtLocal(Vector3Int p)
        {
            CubeInfo c;
            return Cells.TryGetValue((p.x, p.y, p.z), out c) ? c : CubeInfo.Air;
        }
        public void PlaceCubeAtLocal(Vector3Int p, byte materialID, byte prefabID, Quaternion rotation, bool collectPreviousCube, bool isOwnedByPlacer, float delayTime)
        {
            var call = new Call { Kind = "place", Pos = p, Mat = materialID, Prefab = prefabID, Rot = rotation.Index, Collect = collectPreviousCube, Owned = isOwnedByPlacer, Delay = delayTime };
            Calls.Add(call);
            if (!Loaded(p)) return;                                  // CanPlaceCubeAtLocal false: the game does nothing
            var evt = new CubePlaceEvent { Position = p, Material = materialID, PrefabId = prefabID, Sender = ServerPlayer };
            if (PlaceHook != null) PlaceHook(evt);
            if (evt.Cancelled) return;
            Pending.Add(call);
        }
        // CubeListener.DelayedFinalize: the server applies placements at the end of the frame.
        public void EndFrame()
        {
            foreach (var c in Pending)
            {
                if (c.Mat == 0) { Cells.Remove((c.Pos.x, c.Pos.y, c.Pos.z)); continue; }
                var ts = TilesetLibrary.GetTilesetWithID((byte)c.Mat, false);
                var d = ts.DefaultColor;
                Cells[(c.Pos.x, c.Pos.y, c.Pos.z)] = new CubeInfo
                {
                    MaterialID = (byte)c.Mat, PrefabID = (byte)c.Prefab, Rotation = new Quaternion { Index = c.Prefab == 0 ? 0 : c.Rot },
                    CubeColor = new Color32((byte)(d.r * 255), (byte)(d.g * 255), (byte)(d.b * 255), 255)
                };
            }
            Pending.Clear();
        }
        public void ColorCubeAtLocal(Vector3Int p, Color32 c)
        {
            Calls.Add(new Call { Kind = "paint", Pos = p, Rgba = c.Rgba });
            CubeInfo info;
            if (!Cells.TryGetValue((p.x, p.y, p.z), out info)) return;    // no block yet: the colour is lost
            info.CubeColor = c;
            Cells[(p.x, p.y, p.z)] = info;
        }
        public Vector3 LocalToWorldCoordinate(Vector3Int p) { return new Vector3(p.x * 1.2f, p.y * 1.2f, p.z * 1.2f); }
        public Vector3Int WorldToLocalCoordinate(Vector3 v)
        {
            return new Vector3Int((int)Math.Round(v.x / 1.2f, MidpointRounding.ToEven), (int)Math.Round(v.y / 1.2f, MidpointRounding.ToEven), (int)Math.Round(v.z / 1.2f, MidpointRounding.ToEven));
        }
        // Helpers for the tests.
        public void Put(int x, int y, int z, int mat, int prefab = 0, int rot = 0, int rgba = -1)
        {
            Cells[(x, y, z)] = new CubeInfo { MaterialID = (byte)mat, PrefabID = (byte)prefab, Rotation = new Quaternion { Index = rot },
                CubeColor = rgba < 0 ? new Color32(200, 200, 200, 255) : new Color32((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba) };
        }
    }

    public static class BlockManager { public static RootCubeGrid DefaultCubeGrid; }
}

namespace CodeHatch.Engine.Modules.SocialSystem
{
    public static class SocialAPI
    {
        public static Dictionary<Type, object> Schemes = new Dictionary<Type, object>();
        public static T Get<T>() where T : class { object o; return Schemes.TryGetValue(typeof(T), out o) ? (T)o : null; }
    }
}

namespace CodeHatch.Thrones.SocialSystem
{
    using UnityEngine;
    // Crest zones as a function of the world position: group 0 = unclaimed.
    public class CrestScheme
    {
        public Func<Vector3, ulong> GroupAt = v => 0;
        public bool IsEmpty(Vector3 position) { return GroupAt(position) == 0; }
        public ulong CurrentCrestGroup(Vector3 position) { return GroupAt(position); }
    }
}

namespace Oxide.Core.Plugins
{
    public class Plugin { public string Name; }
}

namespace Oxide.Core
{
    // System.Text.Json stand-in for Oxide's Newtonsoft reader: "object" slots get plain numbers and strings, as
    // Newtonsoft gives (long, double, string, null).
    public class PlainObjectConverter : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            switch (r.TokenType)
            {
                case JsonTokenType.Number: return r.TryGetInt64(out long l) ? (object)l : r.GetDouble();
                case JsonTokenType.String: return r.GetString();
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.Null: return null;
                default: using (var doc = JsonDocument.ParseValue(ref r)) return doc.RootElement.Clone();
            }
        }
        public override void Write(Utf8JsonWriter w, object v, JsonSerializerOptions o) { JsonSerializer.Serialize(w, v, v.GetType(), o); }
    }
    public class DataFileSystem
    {
        public string Dir;
        public static JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, WriteIndented = false, Converters = { new PlainObjectConverter() } };
        public int Writes;
        public List<string> Written = new List<string>();
        string P(string n) { return Path.Combine(Dir, n + ".json"); }
        public bool ExistsDatafile(string n) { return File.Exists(P(n)); }
        public string[] GetFiles(string path, string pattern)
        {
            string d = Path.Combine(Dir, path);
            return Directory.Exists(d) ? Directory.GetFiles(d, pattern) : new string[0];
        }
        public T ReadObject<T>(string n) where T : new()
        {
            if (!File.Exists(P(n))) { var t = new T(); WriteObject(n, t); return t; }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(P(n)), Opts);
        }
        public void WriteObject<T>(string n, T o) { Writes++; Written.Add(n); File.WriteAllText(P(n), JsonSerializer.Serialize(o, Opts)); }
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
        public List<KeyValuePair<float, Action>> Repeating = new List<KeyValuePair<float, Action>>();
        public Timer Every(float s, Action a) { Repeating.Add(new KeyValuePair<float, Action>(s, a)); return new Timer(); }
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
