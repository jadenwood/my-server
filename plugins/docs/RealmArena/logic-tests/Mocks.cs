// Behaviour test mocks for RealmArena, compiled together with the REAL plugins/RealmTreasury.cs so stakes move through
// the treasury's own escrow code: only the surface the two plugins touch. Not the real game. Type and member names
// follow the 2.0.3867 Assembly-CSharp metadata; behaviour follows the decompiled code where the plugin depends on it
// (PlayerHealth: three regions, a blow takes Damage.Amount off the hit region, a leg blow spills into the torso, a blow
// with no hit bone is spread by MaxHealth, death when the head or the torso reaches 0). Plugin.Call routes to the
// target's NON-PUBLIC instance methods by name and argument count, as Oxide does; a Handler stands in for a plugin that
// is not compiled in.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace UnityEngine
{
    public class Object { public string name; }
    public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    // As Unity: Hips 0, LeftUpperLeg 1, RightUpperLeg 2, LeftLowerLeg 3, RightLowerLeg 4, LeftFoot 5, RightFoot 6,
    // Spine 7, Chest 8, Neck 9, Head 10, ... LastBone 54 (Damage's "no bone").
    public enum HumanBodyBones { Hips = 0, LeftUpperLeg = 1, RightUpperLeg = 2, LeftLowerLeg = 3, RightLowerLeg = 4, LeftFoot = 5, RightFoot = 6,
        Spine = 7, Chest = 8, Neck = 9, Head = 10, LeftShoulder = 11, RightShoulder = 12, LeftUpperArm = 13, RightUpperArm = 14, LastBone = 54 }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Entities.Definitions;
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    using CodeHatch.UserInterface.Dialogues;
    public struct Vector3Int { public int x, y, z; }
    public static class PlayerExtensions
    {
        public static bool PopupsFail;
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.Inventory; }
        public static PlayerHealth GetHealth(this Player p) { return p.Entity != null ? p.Health : null; }
        public static bool IsAlive(this Player p) { return p.Health != null && !p.Health.Dead; }
        public static void Heal(this Player p, float amount) { p.Heals.Add(amount); if (p.Health != null) p.Health.HealBy(amount); }
        public static object ShowPopup(this Player p, string title, string message, string button, Dialogue.OnSubmit handler, bool interupt, bool broadcast)
        {
            if (PopupsFail) throw new InvalidOperationException("no window");
            p.Popups.Add(new Popup { Title = title, Message = message, Yes = button, Handler = handler, Broadcast = broadcast });
            return null;
        }
        public static void ShowConfirmPopup(this Player p, string title, string message, string yes, string no, Dialogue.OnSubmit handler, bool interupt, bool broadcast)
        {
            if (PopupsFail) throw new InvalidOperationException("no window");
            p.Popups.Add(new Popup { Title = title, Message = message, Yes = yes, No = no, Handler = handler, Broadcast = broadcast });
        }
    }
    public class Popup
    {
        public string Title, Message, Yes, No; public Dialogue.OnSubmit Handler; public bool Broadcast;
        public void Answer(bool yes) { if (Handler != null) Handler(yes ? Options.Yes : Options.No, new Dialogue(), null); }
    }
}

namespace CodeHatch.UserInterface.Dialogues
{
    public enum Options { Yes, No, Ok, Cancel }
    public class Dialogue
    {
        public string ValueMessage;
        public delegate void OnSubmit(Options selection, Dialogue dialogue, object contextData);
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
        public List<object> Components = new List<object>();
        public T GetOrCreate<T>() where T : class, new()
        {
            T t = Components.OfType<T>().FirstOrDefault();
            if (t == null) { t = new T(); Components.Add(t); var ct = t as CodeHatch.Engine.Behaviours.CharacterTeleport; if (ct != null) ct.Entity = this; }
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
        public static List<string> Moves = new List<string>();
        public void Teleport(UnityEngine.Vector3 to) { Entity.Position = to; Moves.Add((Entity.Owner != null ? Entity.Owner.Name : "?") + "@" + (int)to.x + "," + (int)to.z); }
    }
}

namespace CodeHatch.Engine.Entities.Definitions
{
    using UnityEngine;
    public class HealthRegion
    {
        float cur, max;
        public List<HumanBodyBones> _bones = new List<HumanBodyBones>();
        public HealthRegion(float max, params HumanBodyBones[] bones) { this.max = max; cur = max; _bones.AddRange(bones); }
        public float MaxHealth { get { return max; } set { max = Math.Max(0, value); if (cur > max) cur = max; } }
        public float CurrentHealth { get { return cur; } set { cur = value < 0 ? 0 : (value > max ? max : value); } }
        public List<HumanBodyBones> Bones { get { return _bones; } }
    }
    public class PlayerHealth
    {
        public HealthRegion HeadHealth = new HealthRegion(30, HumanBodyBones.Head, HumanBodyBones.Neck);
        public HealthRegion TorsoHealth = new HealthRegion(70, HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
            HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm);
        public HealthRegion LegsHealth = new HealthRegion(50, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot);
        public bool Dead;
        public bool PreventDeath { get; set; }
        public float MaxHealth { get { return HeadHealth.MaxHealth + TorsoHealth.MaxHealth + LegsHealth.MaxHealth; } }
        public float CurrentHealth { get { return HeadHealth.CurrentHealth + TorsoHealth.CurrentHealth + LegsHealth.CurrentHealth; } }
        public void HealBy(float amount)
        {
            foreach (var r in new[] { HeadHealth, TorsoHealth, LegsHealth })
            {
                float room = r.MaxHealth - r.CurrentHealth, add = Math.Min(room, amount);
                r.CurrentHealth += add; amount -= add;
                if (amount <= 0) break;
            }
        }
        // The game's rule ([DEC] PlayerHealth.OnEntityDamage): returns true when the blow kills.
        public bool Apply(float amount, HumanBodyBones bone)
        {
            if (HeadHealth.Bones.Contains(bone)) { HeadHealth.CurrentHealth -= amount; return Kill(HeadHealth.CurrentHealth <= 0f); }
            if (TorsoHealth.Bones.Contains(bone)) { TorsoHealth.CurrentHealth -= amount; return Kill(TorsoHealth.CurrentHealth <= 0f); }
            if (LegsHealth.Bones.Contains(bone))
            {
                if (LegsHealth.CurrentHealth > amount) { LegsHealth.CurrentHealth -= amount; return false; }
                float spill = amount - LegsHealth.CurrentHealth;
                LegsHealth.CurrentHealth = 0f;
                TorsoHealth.CurrentHealth -= spill;
                return Kill(TorsoHealth.CurrentHealth <= 0f);
            }
            float total = MaxHealth;
            float l = amount * (LegsHealth.MaxHealth / total), t = amount * (TorsoHealth.MaxHealth / total), h = amount * (HeadHealth.MaxHealth / total);
            LegsHealth.CurrentHealth -= l; TorsoHealth.CurrentHealth -= t; HeadHealth.CurrentHealth -= h;
            return Kill(HeadHealth.CurrentHealth <= 0f || TorsoHealth.CurrentHealth <= 0f);
        }
        bool Kill(bool fatal) { if (!fatal || PreventDeath) return false; Dead = true; return true; }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Engine.Entities.Definitions;
    using CodeHatch.ItemContainer;
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public PlayerHealth Health = new PlayerHealth();
        public List<string> Messages = new List<string>();
        public List<CodeHatch.Common.Popup> Popups = new List<CodeHatch.Common.Popup>();
        public List<float> Heals = new List<float>();
        public Container Inventory = new Container();
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
        public static void BroadcastMessage(string format, params object[] args) { Broadcasts.Add(args.Length == 0 ? format : string.Format(format, args)); }
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
    public class KingsScheme
    {
        public ulong King; public float Tax = 0.1f;
        public bool HasKing() { return King != 0; }
        public bool IsKing(Player p) { return p != null && p.Id == King; }
        public ulong GetKingID() { return King; }
        public float GetTax() { return Tax; }
    }
    public class KingsRealm { public static float TaxMaximum { get { return 0.5f; } } }
}

namespace CodeHatch.Damaging
{
    using CodeHatch.Engine.Core.Cache;
    public class Damage
    {
        public float Amount; public Entity DamageSource;
        public UnityEngine.HumanBodyBones HitBoxBone = UnityEngine.HumanBodyBones.LastBone;
    }
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
    public delegate void EventSubscriber<T>(T theEvent) where T : BaseEvent;
    public enum EventHandlerOrder { VeryEarly, Early, Normal, Late, VeryLate }
    public static class EventManager
    {
        public static Dictionary<Type, List<Delegate>> Subs = new Dictionary<Type, List<Delegate>>();
        public static void Subscribe<T>(EventSubscriber<T> s, EventHandlerOrder o) where T : BaseEvent
        {
            List<Delegate> l; if (!Subs.TryGetValue(typeof(T), out l)) Subs[typeof(T)] = l = new List<Delegate>(); l.Add(s);
        }
        public static void Unsubscribe<T>(EventSubscriber<T> s) where T : BaseEvent { List<Delegate> l; if (Subs.TryGetValue(typeof(T), out l)) l.Remove(s); }
    }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Core.Cache;
    public class EntityDamageEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage Damage; }
    public class EntityDeathEvent : CodeHatch.Networking.Events.BaseEvent { public Entity Entity; public Damage KillingDamage; }
}

namespace CodeHatch.Networking.Events.Item
{
    public class ItemPassEvent : CodeHatch.Networking.Events.BaseEvent { public string Memo; public InvGameItemStack ItemStack; }
}

namespace CodeHatch.Blocks.Networking.Events
{
    using CodeHatch.Common;
    public class Grid { public UnityEngine.Vector3 World; public UnityEngine.Vector3 LocalToWorldCoordinate(Vector3Int p) { return World; } }
    public class CubePlaceEvent : CodeHatch.Networking.Events.BaseEvent { public ulong SenderId; public Grid Grid; public Vector3Int Position; }
}

namespace CodeHatch.Inventory.Blueprints.Components
{
    public class ContainerManagement { public int StackLimit; }
}

namespace CodeHatch.Inventory.Blueprints
{
    using CodeHatch.Inventory.Blueprints.Components;
    public class InvItemBlueprint
    {
        public string Name; public ContainerManagement CM;
        public T TryGet<T>() where T : class { return CM as T; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static Dictionary<string, InvItemBlueprint> All = new Dictionary<string, InvItemBlueprint>(StringComparer.OrdinalIgnoreCase)
        {
            { "Wood", new InvItemBlueprint { Name = "Wood", CM = new ContainerManagement { StackLimit = 50 } } },
        };
        public InvItemBlueprint GetBlueprintForName(string n, bool a, bool b) { InvItemBlueprint bp; return All.TryGetValue(n, out bp) ? bp : null; }
        public static InvItemBlueprint[] GetBlueprintsContaining(string s) { return All.Values.Where(b => b.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToArray(); }
    }
}

// Global namespace, as in the real Assembly-CSharp.dll.
public class InvGameItemStack
{
    public CodeHatch.Inventory.Blueprints.InvItemBlueprint Blueprint;
    public int StackAmount;
    public InvGameItemStack(CodeHatch.Inventory.Blueprints.InvItemBlueprint bp, int c, object o) { Blueprint = bp; StackAmount = c; }
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Inventory.Blueprints;
    public class Container { public ItemCollection Contents = new ItemCollection(); }
    public class ItemCollection
    {
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp) { int n; return c.Counts.TryGetValue(bp.Name, out n) ? n : 0; }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int q) { int n = AutoCount(c, bp); if (n < q) return false; c.Counts[bp.Name] = n - q; return true; }
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s) { c.Counts[s.Blueprint.Name] = AutoCount(c, s.Blueprint) + s.StackAmount; return true; }
    }
}

namespace Oxide.Core.Plugins
{
    public class Plugin
    {
        public string Name;
        public Func<string, object[], object> Handler;           // a stand-in for a plugin that is not compiled in
        public List<string> Calls = new List<string>();
        public static bool LogCalls = true;
        // As Oxide: a missing method returns null; only NON-PUBLIC instance methods are callable.
        public object Call(string hook, params object[] args)
        {
            if (LogCalls) Calls.Add(hook + "(" + string.Join(",", args.Select(a => a is string[] ? "[" + string.Join(";", (string[])a) + "]" : Convert.ToString(a))) + ")");
            if (Handler != null) return Handler(hook, args);
            var mi = GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == hook && m.GetParameters().Length == args.Length && !m.IsPublic);
            if (mi == null) return null;
            try { return mi.Invoke(this, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
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
            if (text.Trim().Length == 0) return default(T);
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
        public List<string> Registered = new List<string>();
        public void RegisterPermission(string p, object pl) { Registered.Add(p); }
        public bool UserHasPermission(string id, string p) { return Grants.Contains(id + "|" + p); }
    }
    public class Timer { public bool Destroyed; public void Destroy() { Destroyed = true; } }
    public class TimerLib
    {
        public List<Action> Pending = new List<Action>();
        public List<Timer> Repeating = new List<Timer>();
        public Timer Every(float s, Action a) { var t = new Timer(); Repeating.Add(t); return t; }
        public Timer Once(float s, Action a) { Pending.Add(a); return new Timer(); }
        public void RunPending() { var p = Pending.ToList(); Pending.Clear(); foreach (var a in p) a(); }
    }
    public abstract class ReignOfKingsPlugin : Oxide.Core.Plugins.Plugin
    {
        public ConfigFile Config = new ConfigFile();
        public LangLib lang = new LangLib();
        public PermLib permission = new PermLib();
        public TimerLib timer = new TimerLib();
        public List<string> Logged = new List<string>();
        public List<string> FileLog = new List<string>();
        protected virtual void LoadDefaultConfig() { }
        protected virtual void LoadDefaultMessages() { }
        public void Puts(string s) { Logged.Add(s); }
        public void PrintWarning(string s) { Logged.Add("WARN " + s); }
        public void PrintError(string s) { Logged.Add("ERROR " + s); }
        protected void LogToFile(string f, string text, Oxide.Core.Plugins.Plugin p, bool dated, bool ts) { FileLog.Add(f + ": " + text); }
        protected void PrintToChat(string format, params object[] args) { CodeHatch.Engine.Networking.Server.BroadcastMessage(format, args); }
    }
}
