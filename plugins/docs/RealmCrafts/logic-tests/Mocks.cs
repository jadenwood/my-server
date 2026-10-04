// Behaviour test mocks for RealmCrafts, compiled together with the REAL plugins/RealmTreasury.cs so commission marks move
// through the treasury's own holds and its zero-sum audit: only the surface the two plugins touch. Not the real game. Type
// and member names follow the 2.0.3867 Assembly-CSharp metadata (the plugin compiles against the real DLLs in
// tools/plugin-compile-check/check.sh); behaviour follows the decompiled code where the plugin depends on it:
//   - containers hold real stacks; a stack knows its collection (InvGameItemStack.CollectionInterface), a collection its
//     container (ItemCollection.Container), as in the game;
//   - EventManager calls subscribers in their order (VeryEarly, Early, Normal, Late, VeryLate); the game's own listener
//     (ContainerListener, which applies a client's change) runs at Early, so a test raises an event with the change as
//     its "apply" step between Early and Normal;
//   - Plugin.Call routes to the target's NON-PUBLIC instance methods by name and argument count, as Oxide does; a Handler
//     stands in for a plugin that is not compiled in.
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
}

namespace CodeHatch
{
    // The real enum ([ASM] CodeHatch.ResourceType), names in order.
    public enum ResourceType
    {
        Wood, Stone, Metal, Granite, Marble, Oil, Plastic, Glass, Aluminum, Titanium, Sand, Dirt, IronOre, OilShale, Clay, Gravel,
        SandStone, TitaniumOre, ObsidianOre, Sulphur, VoltronicOre, SaltexOre, GarrisonOre, CrystalisOre, DarkMatterShale, IronIngot,
        SteelIngot, TitaniumIngot, GrapheneIngot, MonolithiumIngot, SaltexPowder, ObsidianPowder, DarkPowder, Steel, Graphene,
        Monolithium, DarkSulphur, Antimatter, Water, Fiber, RawMeat, Grass, Grain, Charcoal, Wool, Fat, Bone, Heart, Blood, BearHide,
        LeatherHide, RawBird, Flowers, Roses, Stick, Bread, CookedMeat, CookedBird, Apple, Berry, Cabbage, Carrot, PlayerHead,
        BrownBeans, BatWing, DeerSkin, DuckFeet, Fang, Feather, Flax, Fuse, Liver, RabbitPelt, Diamond, GodTears, WolfPelt, StoneBlock,
        BakedClay, FireWater, BurntMeat, BurntBird, Lumber, Fern, Worm, SeedsApple, SeedsBeet, SeedsCabbage, SeedsCarrot, SeedsHay,
        SeedsOnion, SeedsPine, SeedsPoplar, SeedsBean, SeedsBerry, Flour, Count
    }
}

namespace CodeHatch.Common
{
    using CodeHatch.Engine.Networking;
    using CodeHatch.ItemContainer;
    using CodeHatch.UserInterface.Dialogues;
    public static class PlayerExtensions
    {
        public static bool PopupsFail;
        public static void SendMessage(this Player p, string m) { p.Messages.Add(m); }
        public static void SendError(this Player p, string m) { p.Messages.Add("ERR " + m); }
        public static Container GetInventory(this Player p) { return p.Entity != null ? p.Inventory : null; }
        public static object ShowPopup(this Player p, string title, string message, string button, Dialogue.OnSubmit handler, bool interupt, bool broadcast)
        {
            if (PopupsFail) throw new InvalidOperationException("no window");
            p.Popups.Add(new Popup { Title = title, Message = message, Yes = button, Broadcast = broadcast });
            return null;
        }
        public static void ShowConfirmPopup(this Player p, string title, string message, string yes, string no, Dialogue.OnSubmit handler, bool interupt, bool broadcast)
        {
            if (PopupsFail) throw new InvalidOperationException("no window");
            p.Popups.Add(new Popup { Title = title, Message = message, Yes = yes, No = no, Broadcast = broadcast });
        }
    }
    public class Popup { public string Title, Message, Yes, No; public bool Broadcast; }
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

namespace CodeHatch.AI
{
    public class MonsterMotor { }
}

namespace CodeHatch.Engine.Core.Cache
{
    using CodeHatch.Engine.Networking;
    public class MonsterEntity { }
    public class Entity : UnityEngine.Object
    {
        public UnityEngine.Vector3 Position;
        public bool IsPlayer = true;
        public Player Owner;
        public string Label;
        public List<object> Components = new List<object>();
        public T TryGet<T>() where T : class { return Components.OfType<T>().FirstOrDefault(); }
        public override string ToString() { return (Label ?? "Entity") + " (CodeHatch.Engine.Core.Cache.Entity)"; }
    }
}

namespace CodeHatch.Engine.Behaviours
{
    public class ItemCrafter
    {
        public string Label = "Workbench";
        public override string ToString() { return Label + " (CodeHatch.Engine.Behaviours.ItemCrafter)"; }
    }
}

namespace CodeHatch.Engine.Networking
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.ItemContainer;
    public class Connection { public string IpAddress; }
    public class Player
    {
        public ulong Id; public string Name; public bool IsServer; public Entity Entity;
        public Connection Connection = new Connection { IpAddress = "10.0.0.1" };
        public List<string> Messages = new List<string>();
        public List<CodeHatch.Common.Popup> Popups = new List<CodeHatch.Common.Popup>();
        public Container Inventory;
        public Player(ulong id, string name)
        {
            Id = id; Name = name; Entity = new Entity { Owner = this, Label = name };
            Inventory = new Container { Entity = Entity, Name = "Inventory" };
        }
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
    public class Damage { public float Amount; public Entity DamageSource; }
}

namespace CodeHatch.Networking.Events
{
    using CodeHatch.Engine.Networking;
    public class BaseEvent
    {
        public bool Cancelled; public string CancelReason;
        public void Cancel() { Cancelled = true; }
        public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
    }
    public class NetworkEvent : BaseEvent
    {
        public Player Sender { get; set; }
        public bool IsSender;                       // the real one is Sender == Player.Local (true for the server's own events)
    }
    public delegate void EventSubscriber<T>(T theEvent) where T : BaseEvent;
    public enum EventHandlerOrder { VeryEarly, Early, Normal, Late, VeryLate }
    public static class EventManager
    {
        public class Sub { public Delegate D; public EventHandlerOrder Order; }
        public static Dictionary<Type, List<Sub>> Subs = new Dictionary<Type, List<Sub>>();
        public static void Subscribe<T>(EventSubscriber<T> s, EventHandlerOrder o) where T : BaseEvent
        {
            List<Sub> l; if (!Subs.TryGetValue(typeof(T), out l)) Subs[typeof(T)] = l = new List<Sub>(); l.Add(new Sub { D = s, Order = o });
        }
        public static void Unsubscribe<T>(EventSubscriber<T> s) where T : BaseEvent { List<Sub> l; if (Subs.TryGetValue(typeof(T), out l)) l.RemoveAll(x => x.D == (Delegate)s); }
        public static int Count<T>() { List<Sub> l; return Subs.TryGetValue(typeof(T), out l) ? l.Count : 0; }
        // Calls every subscriber in order; the game's own listener (`apply`) runs at Early, unless the event was cancelled.
        public static void Raise<T>(T e, Action apply) where T : BaseEvent
        {
            List<Sub> l;
            var subs = Subs.TryGetValue(typeof(T), out l) ? l.OrderBy(x => (int)x.Order).ToList() : new List<Sub>();
            bool applied = false;
            foreach (var s in subs)
            {
                if (!applied && s.Order > EventHandlerOrder.Early) { applied = true; if (!e.Cancelled && apply != null) apply(); }
                ((EventSubscriber<T>)s.D)(e);
            }
            if (!applied && !e.Cancelled && apply != null) apply();
        }
    }
}

namespace CodeHatch.Networking.Events.Entities
{
    using CodeHatch.Damaging;
    using CodeHatch.Engine.Behaviours;
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Inventory.Blueprints;
    public class EntityEvent : CodeHatch.Networking.Events.NetworkEvent { public Entity Entity; }
    public class EntityDeathEvent : EntityEvent { public Damage KillingDamage; }
    public class ItemCrafterEvent : EntityEvent { public ItemCrafter Crafter; }
    public class ItemCrafterCraftEvent : ItemCrafterEvent { public InvItemBlueprint Product; public int Quantity; }
    public class ItemCrafterItemEvent : ItemCrafterEvent { public InvGameItemStack Stack; public int Cycles; }
    public class ItemCrafterFinishEvent : ItemCrafterEvent { }
}

namespace CodeHatch.Networking.Events.Containers
{
    using CodeHatch.ItemContainer;
    public class ContainerEvent : CodeHatch.Networking.Events.Entities.EntityEvent { public Container Container; }
    public class ContainerItemEvent : ContainerEvent { public InvGameItemStack ItemStack; }
    public class ContainerItemAddEvent : ContainerItemEvent { public int Slot; }
    public class ContainerItemRemoveEvent : ContainerItemEvent { public int Slot; }
    public class ContainerItemSplitEvent : ContainerItemEvent { public int Quantity; public InvGameItemStack ResultStack; }
    public class ContainerItemMergeEvent : ContainerItemEvent { public int Quantity; public InvGameItemStack TargetStack; }
}

namespace CodeHatch.Networking.Events.Item
{
    using CodeHatch.Engine.Networking;
    public class ItemEvent : CodeHatch.Networking.Events.NetworkEvent { public InvGameItemStack ItemStack; }
    public class ItemPassEvent : ItemEvent { public Player Recipient { get; set; } public string Memo { get; set; } }
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
        public string Name; public int StackLimit = 1000;
        public T TryGet<T>() where T : class { return typeof(T) == typeof(ContainerManagement) ? new ContainerManagement { StackLimit = StackLimit } as T : null; }
    }
    public class InvBlueprints
    {
        public static InvBlueprints Instance = new InvBlueprints();
        public static Dictionary<string, InvItemBlueprint> All = new Dictionary<string, InvItemBlueprint>(StringComparer.OrdinalIgnoreCase);
        // ResourceType -> the item's display name, where they differ (as on a real server the names may).
        public static Dictionary<CodeHatch.ResourceType, string> ResourceNames = new Dictionary<CodeHatch.ResourceType, string>();
        public static void Add(string name, int stack = 1000) { All[name] = new InvItemBlueprint { Name = name, StackLimit = stack }; }
        public InvItemBlueprint GetBlueprintForName(string n, bool a, bool b) { InvItemBlueprint bp; return n != null && All.TryGetValue(n, out bp) ? bp : null; }
        public InvItemBlueprint GetBlueprintForResource(CodeHatch.ResourceType rt)
        {
            string n; if (!ResourceNames.TryGetValue(rt, out n)) n = rt.ToString();
            InvItemBlueprint bp; return All.TryGetValue(n, out bp) ? bp : null;
        }
        public static InvItemBlueprint[] GetBlueprintsContaining(string s) { return All.Values.Where(b => b.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0).ToArray(); }
    }
}

// Global namespace, as in the real Assembly-CSharp.dll.
public class InvGameItemStack
{
    public CodeHatch.Inventory.Blueprints.InvItemBlueprint Blueprint;
    public int StackAmount;
    public CodeHatch.ItemContainer.IItemCollection CollectionInterface;
    public InvGameItemStack(CodeHatch.Inventory.Blueprints.InvItemBlueprint bp, int c, object o) { Blueprint = bp; StackAmount = c; }
    public override string ToString() { return StackAmount + " " + (Blueprint != null ? Blueprint.Name : "?"); }
}

namespace CodeHatch.ItemContainer
{
    using CodeHatch.Engine.Core.Cache;
    using CodeHatch.Inventory.Blueprints;
    public interface IItemCollection { }
    public class Container
    {
        public Entity Entity; public string Name = "Container";
        public ItemCollection Contents;
        public Container() { Contents = new ItemCollection { Container = this }; }
    }
    public class LootableCreatureContainer : Container { }
    public class ItemCollection : IItemCollection
    {
        public Container Container { get; set; }
        public List<InvGameItemStack> Items = new List<InvGameItemStack>();
        public static int AutoCount(ItemCollection c, InvItemBlueprint bp) { return c.Items.Where(s => s.Blueprint.Name == bp.Name).Sum(s => s.StackAmount); }
        public static bool AutoSplit(ItemCollection c, InvItemBlueprint bp, int q)
        {
            if (AutoCount(c, bp) < q) return false;
            if (StealOnSplit != null) q = StealOnSplit(q);
            for (int i = c.Items.Count - 1; i >= 0 && q > 0; i--)
            {
                var s = c.Items[i];
                if (s.Blueprint.Name != bp.Name) continue;
                int t = Math.Min(q, s.StackAmount); s.StackAmount -= t; q -= t;
                if (s.StackAmount <= 0) { c.Items.RemoveAt(i); s.CollectionInterface = null; }
            }
            return true;
        }
        public static int Capacity = int.MaxValue;     // units a collection may hold in all (tests fill packs up)
        public static Action OnMergeAdd;              // a test's look at the world just before a server give lands
        public static Func<int, int> StealOnSplit;     // a test's hand in the packs: units AutoSplit really takes
        public static bool AutoMergeAdd(ItemCollection c, InvGameItemStack s)
        {
            if (OnMergeAdd != null) OnMergeAdd();
            int room = Capacity - c.Items.Sum(x => x.StackAmount);
            if (room <= 0) return false;
            int n = Math.Min(room, s.StackAmount);
            var into = c.Items.FirstOrDefault(x => x.Blueprint.Name == s.Blueprint.Name);
            if (into != null) into.StackAmount += n;
            else { var add = new InvGameItemStack(s.Blueprint, n, null) { CollectionInterface = c }; c.Items.Add(add); }
            s.StackAmount -= n;
            return s.StackAmount <= 0;
        }
        // The game applying a client's change (ContainerListener calls these with broadcast: false).
        public void Put(InvGameItemStack s) { s.CollectionInterface = this; Items.Add(s); }
        public void Take(InvGameItemStack s) { Items.Remove(s); s.CollectionInterface = null; }
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
