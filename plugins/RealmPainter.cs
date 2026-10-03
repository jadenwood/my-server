// RealmPainter: Realm's own art on the game's painted signs, and live boards that keep themselves up to date.
//
//   /paint <artwork>   an admin looks at a sign (or stands next to one) and binds it to one of the finished paintings
//                      in the art bundle (house sigils, banners, crests, event posters, the Realm emblem) or to a live
//                      board. The plugin writes the PNG into the sign and raises the game's paint update, so every
//                      client gets it. Bound signs are kept in a registry (oxide/data/RealmPainter.json) for redraws.
//   Live boards        chronicle     the latest Chronicle entries with their icons      (RealmChronicle)
//                      wanted [n]    the n-th outlaw: name, house sigil, bounties, until (RealmContracts, RealmLaws)
//                      standings     the season's house standings                       (RealmSeasons)
//                      proclamation  the monarch, their house, the latest decree        (CrownAndConsequences, Chronicle)
//                      event [kind]  the running or next event                          (RealmEvents)
//                      ironbreaker   the Ironbreaker and its current bearer              (RealmLegendary)
//                      notice        an admin's own title and text
//                      A live board is redrawn only when what it shows has changed, at most once per
//                      MinSecondsBetweenRedraws per sign and MaxRedrawsPerMinute / MaxBytesPerMinute for the server.
//   Text               composed here in C# from pre-rendered glyph atlases of the brand fonts (Cinzel, EB Garamond;
//                      SIL OFL 1.1), and encoded by a small PNG encoder in this file (zlib with fixed-Huffman deflate,
//                      or stored blocks). art/tools/painter/paint.mjs builds the atlases, sprites and paintings into
//                      one data file the plugin reads: copy art/paintings/RealmPainterArt.json to oxide/data/.
//
// What the game's code says ([CODE]: read from the type metadata of the 2.0.3867 Assembly-CSharp.dll; names only):
//   CodeHatch.Thrones.Painting.PaintableObject: an EntityBehaviour with ColorArea and DepthNormalArea (PaintArea),
//     DoubleSided, PaintingSpaceTransform, ApplyPainting(bool broadcast) (applies both areas, SetMaterialProperties on
//     the object and on a UI copy, then, if broadcast, EventManager.CallEvent(new PaintObjectUpdateEvent(Entity, this))),
//     Serialize/Deserialize (ColorArea then DepthNormalArea). Its InteractablePainting lets the sign's owner paint it
//     in the game's "Sign Painting" menu (SignPainting).
//   CodeHatch.Thrones.Painting.PaintArea: PaintData { get = StaticTexture.EncodeToPNG(); set = StaticTexture.LoadImage }
//     (PNG bytes), Bounds (CodeHatch.Common.Bounds2 min/size, in the paint space of PaintingSpaceTransform, metres),
//     TextureWidth/TextureHeight, MAX_TEXTURE_SIZE = 1024, TEXEL_WORLD_SIZE = 0.008, Serialize = Bounds then PaintData.
//     The image is stretched over Bounds; a fresh area is a 1x1 transparent texture with Bounds (0,0, 0.008 x 0.008).
//   CodeHatch.Networking.Events.Entities.PaintObjectUpdateEvent(Entity, PaintableObject): an EntityEvent whose Write
//     serialises the whole PaintableObject (both PNGs). PermissionString "rok.painting.update". On the server
//     EventManager.CallEvent handles it locally (HasPermission is checked against the Sender, which a server-made event
//     sets to Player.Local, the server) and then ServerSendEvent sends it to its Recipients (Server.AllPlayers).
//   Entity.TryGetAll(), Entity.TryGetFromViewID(ulong), Entity.NetViewID, Entity.Position; CodeHatch.LookBridge.Forward
//     (the synced look rotation of a player).
// Unity types the compile check has no stub for (Vector2, Bounds2's fields, Transform, Collider, Physics) are reached
// by reflection in the "Game" region only; on a server they are the real UnityEngine types.
//
// UNVERIFIED (nothing here has run on a real server; plugins/docs/RealmPainter.md has the first-test plan):
//   that PaintData can be set on the dedicated server (Texture2D.LoadImage in batch mode) and that the server-made
//   update reaches clients and survives a restart; which placeable items are paintable and how big their faces are
//   (the face probe below, or /paint face calibrate); which way up the image lands (FlipX/FlipY); the look-at
//   targeting (Physics.RaycastAll by reflection, LookBridge on the server); protecting bound signs from repainting.
//
// Cross-plugin calls (all optional; a missing plugin or method only empties its board):
//   RealmChronicle.GetLastEventId() -> int, then oxide/data/RealmChronicle.json is READ (never written)
//   RealmContracts.GetBountyCount(string id) -> int, and oxide/data/RealmContracts.json "Outlaws" is READ
//   RealmLaws.GetCourtOutlaws() -> string[] "id|name|untilIso|caseId"
//   RealmHouses.GetHouse(string id) -> string
//   RealmSeasons.GetSeasonNumber() -> int, GetSeasonName() -> string, GetSeasonStandings() -> [{rank,house,score,crownDays}]
//   CrownAndConsequences.GetKingName(), GetKingHouse(), GetKingSince() -> string; GetUtcOffsetHours() -> double
//   RealmEvents.GetActiveEvents() -> string[] "kind|startIso|endIso", GetNextEvent() -> {title, at}
//   RealmLegendary.GetBearerName() -> string or null (null, or the plugin missing, reads as "unclaimed")
// No Chronicle entries: painting a sign is not realm history.
//
// Language level: C# 3 syntax only, .NET 3.5 API surface. Cross-plugin API methods MUST stay non-public.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using CodeHatch;                                 // LookBridge [CODE]
using CodeHatch.Common;                          // PlayerExtensions: SendMessage, SendError [CODE]
using CodeHatch.Engine.Core.Cache;               // Entity [CODE]
using CodeHatch.Engine.Networking;               // Player, Server [CODE]
using CodeHatch.Networking.Events;               // EventManager, EventSubscriber<T>, EventHandlerOrder [CODE]
using CodeHatch.Networking.Events.Entities;      // PaintObjectUpdateEvent [CODE]
using CodeHatch.Thrones.Painting;                // PaintableObject, PaintArea [CODE]
using Oxide.Core;                                // Interface.Oxide.DataFileSystem
using Oxide.Core.Plugins;                        // Plugin (for [PluginReference])

namespace Oxide.Plugins
{
    [Info("RealmPainter", "Realm", "0.1.0")]
    [Description("Realm's own art and live boards on the game's painted signs")]
    public class RealmPainter : ReignOfKingsPlugin
    {
        [PluginReference] private Plugin RealmChronicle;
        [PluginReference] private Plugin RealmContracts;
        [PluginReference] private Plugin RealmLaws;
        [PluginReference] private Plugin RealmHouses;
        [PluginReference] private Plugin RealmSeasons;
        [PluginReference] private Plugin CrownAndConsequences;
        [PluginReference] private Plugin RealmEvents;
        [PluginReference] private Plugin RealmLegendary;

        private const string PermAdmin = "realmpainter.admin";
        private const string DataName = "RealmPainter";
        private const int HardMaxSide = 1024;            // PaintArea.MAX_TEXTURE_SIZE [CODE]
        private const float TexelWorldSize = 0.008f;     // PaintArea.TEXEL_WORLD_SIZE [CODE]

        // Live boards, in the order /paint list shows them.
        private static readonly string[] Boards = { "chronicle", "wanted", "standings", "proclamation", "event", "ironbreaker", "notice" };
        private static readonly string[] EventKinds = { "crown_night", "tournament", "kings_hunt", "truce" };
        private static readonly string[] EventIcons = { "crown", "trophy", "hunt", "sheathed" };
        private static readonly string[] HouseIds = { "varrow", "ashgrove", "corvane", "dunmere", "halloran", "merrin" };

        private PluginConfig config;
        private StoredData data;
        private bool loadFailed;
        private bool dirty;
        private Art art;
        private string artError;
        private Timer tickTimer;
        private EventSubscriber<PaintObjectUpdateEvent> paintSubscriber;
        private bool selfPainting;                       // our own update is being raised: the subscriber ignores it
        private DateTime nextRefresh = DateTime.MinValue;
        private readonly List<string> queue = new List<string>();          // sign ids waiting for a redraw
        private readonly HashSet<string> forced = new HashSet<string>();   // ... that skip the per-sign interval
        private readonly List<KeyValuePair<DateTime, int>> sent = new List<KeyValuePair<DateTime, int>>();  // when, bytes
        private int chronicleSeen = -1;
        private List<Wanted> outlawCache;
        private DateTime outlawAt;
        private List<ChronEntry> chronicleCache = new List<ChronEntry>();

        // Indirections so the behaviour tests can move time and stand in for the game's world.
        private Func<DateTime> clock = DefaultClock;
        private Func<Player, Entity> rayProbe;           // null: Physics.RaycastAll by reflection
        private Func<PaintableObject, float[]> faceProbe; // null: colliders by reflection

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        #region Config

        private class FaceRect
        {
            public float X;
            public float Y;
            public float W;
            public float H;
        }

        private class PluginConfig
        {
            public bool Enabled = true;                  // master switch: false = no drawing at all (commands still answer)
            public bool LiveBoards = true;               // false = live boards keep their last picture
            public List<string> DisabledBoards = new List<string>();
            public string ArtFile = "RealmPainterArt";   // oxide/data/<ArtFile>.json, from art/paintings
            public float TickSeconds = 10f;
            public int RefreshSeconds = 60;              // how often live boards look for changes
            public int MinSecondsBetweenRedraws = 120;   // per sign
            public int MaxRedrawsPerMinute = 6;          // whole server
            public int MaxBytesPerMinute = 1048576;      // whole server, PNG bytes sent
            public int MaxImageSide = 512;               // never above 1024 (PaintArea.MAX_TEXTURE_SIZE)
            public int MaxPngBytes = 262144;
            public string Compression = "deflate";       // deflate (this file's encoder) | system (DeflateStream) | stored
            public string Fit = "fit";                   // fit: keep the picture's shape inside the face | fill: stretch
            public bool BoardTexture = true;             // parchment and iron texture under live boards
            public bool ClearRelief = true;              // reset the depth/normal area so old brush relief goes
            public bool FlipX = false;                   // if the first test shows the picture mirrored
            public bool FlipY = false;                   // if the first test shows it upside down
            public string SendMode = "event";            // event: raise PaintObjectUpdateEvent | apply: ApplyPainting(true)
            public bool ProtectBoundSigns = true;        // repaint a bound sign that someone painted over
            public float MaxReach = 8f;                  // metres, for the sign you look at
            public float NearRadius = 3f;                // metres, for the sign you stand next to
            public float EyeHeight = 1.6f;
            public float AimDegrees = 12f;
            public FaceRect DefaultFace = new FaceRect { X = -0.5f, Y = 0f, W = 1f, H = 0.75f };
            public Dictionary<string, FaceRect> FacesByName = new Dictionary<string, FaceRect>();
            public int ChronicleRows = 5;
            public int MaxSigns = 64;
            public int MaxNoticeLength = 180;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        private void ClampConfig()
        {
            if (config.DisabledBoards == null) config.DisabledBoards = new List<string>();
            if (config.FacesByName == null) config.FacesByName = new Dictionary<string, FaceRect>();
            if (config.DefaultFace == null || !GoodFace(config.DefaultFace)) config.DefaultFace = new FaceRect { X = -0.5f, Y = 0f, W = 1f, H = 0.75f };
            if (string.IsNullOrEmpty(config.ArtFile)) config.ArtFile = "RealmPainterArt";
            if (config.TickSeconds < 2f) config.TickSeconds = 2f;
            if (config.RefreshSeconds < 10) config.RefreshSeconds = 10;
            if (config.MinSecondsBetweenRedraws < 5) config.MinSecondsBetweenRedraws = 5;
            if (config.MaxRedrawsPerMinute < 1) config.MaxRedrawsPerMinute = 1;
            if (config.MaxBytesPerMinute < 65536) config.MaxBytesPerMinute = 65536;
            if (config.MaxImageSide < 64) config.MaxImageSide = 64;
            if (config.MaxImageSide > HardMaxSide) config.MaxImageSide = HardMaxSide;
            if (config.MaxPngBytes < 16384) config.MaxPngBytes = 16384;
            if (config.Compression != "system" && config.Compression != "stored") config.Compression = "deflate";
            if (config.Fit != "fill") config.Fit = "fit";
            if (config.SendMode != "apply") config.SendMode = "event";
            if (config.MaxReach < 1f) config.MaxReach = 1f;
            if (config.NearRadius < 0.5f) config.NearRadius = 0.5f;
            if (config.AimDegrees < 1f) config.AimDegrees = 1f;
            if (config.AimDegrees > 45f) config.AimDegrees = 45f;
            if (config.ChronicleRows < 1) config.ChronicleRows = 1;
            if (config.ChronicleRows > 8) config.ChronicleRows = 8;
            if (config.MaxSigns < 1) config.MaxSigns = 1;
            if (config.MaxNoticeLength < 20) config.MaxNoticeLength = 20;
            var bad = new List<string>();
            foreach (KeyValuePair<string, FaceRect> kv in config.FacesByName) if (kv.Value == null || !GoodFace(kv.Value)) bad.Add(kv.Key);
            foreach (string k in bad) config.FacesByName.Remove(k);
        }

        private static bool GoodFace(FaceRect f)
        {
            return f != null && f.W > 0.05f && f.H > 0.05f && f.W < 20f && f.H < 20f && !float.IsNaN(f.X) && !float.IsNaN(f.Y)
                && Math.Abs(f.X) < 50f && Math.Abs(f.Y) < 50f;
        }

        #endregion

        #region Data

        private class SignRec
        {
            public string Id;
            public ulong ViewId;
            public float X;                              // world position when bound (to find it again)
            public float Y;
            public float Z;
            public string Name;                          // the sign's object name when bound
            public string Board;                         // a painting id, or a live board
            public string Arg;                           // wanted index, event kind, notice text
            public FaceRect Face;                        // paint-space rectangle the picture goes in, metres
            public string FaceFrom;                      // probe | calibrated | manual | name | default
            public string Fit;                           // null: config.Fit
            public string LastKey;                       // what a live board showed last time
            public uint LastCrc;
            public DateTime LastDrawn;
            public int Draws;
            public string LastError;
            public bool Missing;
            public string BoundBy;
            public DateTime BoundAt;
        }

        private class StoredData
        {
            public int NextId = 1;
            public List<SignRec> Signs = new List<SignRec>();
        }

        private void LoadData()
        {
            try
            {
                data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataName);
            }
            catch (Exception ex)
            {
                loadFailed = true;
                PrintError("Could not read oxide/data/" + DataName + ".json: " + ex.Message
                    + ". Bound signs keep their pictures, but nothing is redrawn and the file is NOT overwritten until it is fixed or moved away, then reloaded.");
                data = null;
            }
            if (data == null) data = new StoredData();
            if (data.Signs == null) data.Signs = new List<SignRec>();
            data.Signs.RemoveAll(delegate(SignRec s) { return s == null || string.IsNullOrEmpty(s.Id) || string.IsNullOrEmpty(s.Board); });
            foreach (SignRec s in data.Signs) if (s.Face != null && !GoodFace(s.Face)) s.Face = null;
            if (data.NextId < 1) data.NextId = 1;
        }

        private void SaveData()
        {
            if (loadFailed) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataName, data);
            dirty = false;
        }

        private SignRec FindSign(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (SignRec s in data.Signs) if (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        private SignRec SignOf(Entity e)
        {
            if (e == null) return null;
            ulong id = e.NetViewID;
            foreach (SignRec s in data.Signs) if (s.ViewId == id) return s;      // a "missing" record found again too
            return null;
        }

        #endregion

        #region Art bundle

        // oxide/data/RealmPainterArt.json, written by art/tools/painter/paint.mjs (field names are its contract).
        private class ArtItem
        {
            public string Id;
            public string Kind;                          // painting | rgba | mask
            public string Title;
            public int Width;
            public int Height;
            public string Png;                           // base64
        }

        private class ArtFont
        {
            public string Id;
            public string Family;
            public int Size;
            public int Ascent;
            public int Descent;
            public int LineHeight;
            public int Width;
            public int Height;
            public string Png;
            public List<int[]> Glyphs;                   // [codepoint, x, y, w, h, left, top, advance * 64]
        }

        private class ArtHouse
        {
            public string Name;
            public string Sigil;
            public string Words;
            public string Field;
            public string FieldDark;
            public string Metal;
        }

        private class ArtPalette
        {
            public Dictionary<string, string> Brand;
            public Dictionary<string, ArtHouse> Houses;
        }

        private class ArtBundle
        {
            public int Format;
            public string Version;
            public List<ArtItem> Items;
            public List<ArtFont> Fonts;
            public Dictionary<string, string> TypeIcons;
            public ArtPalette Palette;
            public List<string> Licenses;
        }

        private sealed class Glyph
        {
            public int X, Y, W, H, Left, Top, Adv;
        }

        private sealed class Font
        {
            public string Id;
            public int Size, Ascent, Descent, LineHeight;
            public Mask Atlas;
            public readonly Dictionary<int, Glyph> Glyphs = new Dictionary<int, Glyph>();
        }

        private sealed class Art
        {
            public string Version;
            public readonly Dictionary<string, ArtItem> Items = new Dictionary<string, ArtItem>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Paintings = new List<string>();
            public readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
            public readonly Dictionary<string, Img> Rgba = new Dictionary<string, Img>();
            public readonly Dictionary<string, Mask> Masks = new Dictionary<string, Mask>();
            public Dictionary<string, string> TypeIcons = new Dictionary<string, string>();
            public Dictionary<string, string> Brand = new Dictionary<string, string>();
            public Dictionary<string, ArtHouse> Houses = new Dictionary<string, ArtHouse>();
            public readonly List<string> Rejected = new List<string>();
        }

        // Reads and checks the bundle. Paintings stay PNG (they go into a sign as they are); sprites and glyph atlases
        // are decoded when first used.
        private void LoadArt()
        {
            art = null;
            artError = null;
            ArtBundle b = null;
            try
            {
                if (!Interface.Oxide.DataFileSystem.ExistsDatafile(config.ArtFile)) { artError = "oxide/data/" + config.ArtFile + ".json is missing (copy it from art/paintings)"; return; }
                b = Interface.Oxide.DataFileSystem.ReadObject<ArtBundle>(config.ArtFile);
            }
            catch (Exception ex)
            {
                artError = "oxide/data/" + config.ArtFile + ".json could not be read: " + ex.Message;
                return;
            }
            if (b == null || b.Items == null || b.Fonts == null) { artError = "oxide/data/" + config.ArtFile + ".json is not an art bundle"; return; }
            if (b.Format != 1) { artError = "art bundle format " + b.Format + " is not supported (expected 1); rebuild it with art/tools/painter"; return; }
            var a = new Art { Version = b.Version };
            foreach (ArtItem i in b.Items)
            {
                if (i == null || string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.Png)) continue;
                if (i.Width < 1 || i.Height < 1 || i.Width > HardMaxSide || i.Height > HardMaxSide) { a.Rejected.Add(i.Id); continue; }
                if (i.Kind == "painting" && (Math.Max(i.Width, i.Height) > config.MaxImageSide || i.Png.Length / 4 * 3 > config.MaxPngBytes)) { a.Rejected.Add(i.Id); continue; }
                a.Items[i.Id] = i;
                if (i.Kind == "painting") a.Paintings.Add(i.Id);
            }
            foreach (ArtFont f in b.Fonts)
            {
                if (f == null || string.IsNullOrEmpty(f.Id) || f.Glyphs == null || string.IsNullOrEmpty(f.Png)) continue;
                var font = new Font { Id = f.Id, Size = f.Size, Ascent = f.Ascent, Descent = f.Descent, LineHeight = Math.Max(f.LineHeight, f.Size) };
                try
                {
                    font.Atlas = Png.DecodeMask(Convert.FromBase64String(f.Png));
                }
                catch (Exception ex)
                {
                    artError = "font " + f.Id + ": " + ex.Message;
                    return;
                }
                foreach (int[] g in f.Glyphs)
                {
                    if (g == null || g.Length < 8) continue;
                    if (g[1] < 0 || g[2] < 0 || g[1] + g[3] > font.Atlas.W || g[2] + g[4] > font.Atlas.H) continue;
                    font.Glyphs[g[0]] = new Glyph { X = g[1], Y = g[2], W = g[3], H = g[4], Left = g[5], Top = g[6], Adv = g[7] };
                }
                a.Fonts[f.Id] = font;
            }
            foreach (string need in new[] { "title", "head", "label", "body", "small", "italic" })
                if (!a.Fonts.ContainsKey(need)) { artError = "the art bundle has no '" + need + "' font"; return; }
            if (b.TypeIcons != null) a.TypeIcons = b.TypeIcons;
            if (b.Palette != null && b.Palette.Brand != null) a.Brand = b.Palette.Brand;
            if (b.Palette != null && b.Palette.Houses != null) a.Houses = b.Palette.Houses;
            art = a;
            if (a.Rejected.Count > 0)
                PrintWarning("Art bundle: left out " + a.Rejected.Count + " item(s) over the size caps (MaxImageSide " + config.MaxImageSide + ", MaxPngBytes " + config.MaxPngBytes + "): " + string.Join(", ", a.Rejected.ToArray()));
        }

        private Img Sprite(string id)
        {
            if (art == null || id == null) return null;
            Img img;
            if (art.Rgba.TryGetValue(id, out img)) return img;
            ArtItem item;
            if (!art.Items.TryGetValue(id, out item) || item.Kind == "mask") return null;
            try { img = Png.Decode(Convert.FromBase64String(item.Png)); }
            catch (Exception ex) { PrintWarning("Art " + id + ": " + ex.Message); img = null; }
            art.Rgba[id] = img;
            return img;
        }

        private Mask MaskSprite(string id)
        {
            if (art == null || id == null) return null;
            Mask m;
            if (art.Masks.TryGetValue(id, out m)) return m;
            ArtItem item;
            if (!art.Items.TryGetValue(id, out item) || item.Kind != "mask") return null;
            try { m = Png.DecodeMask(Convert.FromBase64String(item.Png)); }
            catch (Exception ex) { PrintWarning("Art " + id + ": " + ex.Message); m = null; }
            art.Masks[id] = m;
            return m;
        }

        private Rgb Colour(string name)
        {
            string hex;
            if (art != null && art.Brand != null && art.Brand.TryGetValue(name, out hex)) return Rgb.Parse(hex);
            return Rgb.Parse(name);
        }

        #endregion

        #region PNG

        // A small, self-contained PNG codec: 8-bit grey, grey+alpha, RGB and RGBA, no interlace. The encoder writes
        // zlib with one fixed-Huffman deflate block (LZ77 over a 32 KiB window), or stored blocks, or DeflateStream;
        // the decoder (for the bundle's sprites and atlases) inflates stored, fixed and dynamic blocks. Tested by
        // decoding its output in Node (plugins/docs/RealmPainter/logic-tests/decode-check.mjs).
        private static class Png
        {
            private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            private static readonly uint[] CrcTable = MakeCrcTable();
            private static readonly int[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
            private static readonly int[] LExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
            private static readonly int[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
            private static readonly int[] DExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
            private static readonly int[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
            private static readonly int[] FixedCode = new int[288];
            private static readonly int[] FixedLen = new int[288];
            private static readonly byte[] LengthIndex = new byte[259];

            static Png()
            {
                for (int s = 0; s < 288; s++)
                {
                    int code, len;
                    if (s < 144) { code = 0x30 + s; len = 8; }
                    else if (s < 256) { code = 0x190 + s - 144; len = 9; }
                    else if (s < 280) { code = s - 256; len = 7; }
                    else { code = 0xC0 + s - 280; len = 8; }
                    FixedCode[s] = Reverse(code, len);
                    FixedLen[s] = len;
                }
                for (int l = 3; l <= 258; l++)
                {
                    int i = 28;
                    while (LBase[i] > l) i--;
                    LengthIndex[l] = (byte)i;
                }
            }

            private static uint[] MakeCrcTable()
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                return t;
            }

            public static uint Crc(byte[] b, int start, int end)
            {
                uint c = 0xFFFFFFFFu;
                for (int i = start; i < end; i++) c = CrcTable[(c ^ b[i]) & 0xFF] ^ (c >> 8);
                return c ^ 0xFFFFFFFFu;
            }

            public static uint Adler(byte[] b, int start, int end)
            {
                uint a = 1, s = 0;
                int i = start;
                while (i < end)
                {
                    int n = Math.Min(5552, end - i);
                    for (int k = 0; k < n; k++) { a += b[i++]; s += a; }
                    a %= 65521; s %= 65521;
                }
                return (s << 16) | a;
            }

            private static int Reverse(int code, int len)
            {
                int r = 0;
                for (int i = 0; i < len; i++) { r = (r << 1) | (code & 1); code >>= 1; }
                return r;
            }

            // ---- encoder ----

            private sealed class BitWriter
            {
                public byte[] Buf;
                public int Len;
                private uint acc;
                private int n;

                public BitWriter(int capacity)
                {
                    Buf = new byte[Math.Max(64, capacity)];
                }

                public void Byte(byte b)
                {
                    if (Len == Buf.Length) Array.Resize(ref Buf, Buf.Length * 2);
                    Buf[Len++] = b;
                }

                public void Put(int bits, int count)
                {
                    acc |= (uint)bits << n;
                    n += count;
                    while (n >= 8) { Byte((byte)acc); acc >>= 8; n -= 8; }
                }

                public void Align()
                {
                    if (n > 0) { Byte((byte)acc); acc = 0; n = 0; }
                }
            }

            // Encodes RGBA pixels. Opaque images are stored as RGB. mode: deflate | stored | system.
            public static byte[] Encode(Img img, string mode)
            {
                bool opaque = true;
                for (int i = 3; i < img.P.Length && opaque; i += 4) if (img.P[i] != 255) opaque = false;
                int ch = opaque ? 3 : 4;
                int stride = img.W * ch;
                var pix = new byte[stride * img.H];
                for (int i = 0, j = 0; i < img.P.Length; i += 4)
                {
                    pix[j++] = img.P[i]; pix[j++] = img.P[i + 1]; pix[j++] = img.P[i + 2];
                    if (!opaque) pix[j++] = img.P[i + 3];
                }
                return EncodeRaw(img.W, img.H, pix, ch, mode);
            }

            // Encodes raw pixels with ch channels (1 grey, 2 grey+alpha, 3 RGB, 4 RGBA).
            public static byte[] EncodeRaw(int w, int h, byte[] pix, int ch, string mode)
            {
                int colorType = ch == 1 ? 0 : ch == 2 ? 4 : ch == 3 ? 2 : 6;
                int stride = w * ch;
                var raw = new byte[(stride + 1) * h];
                var cand = new byte[5][];
                for (int f = 0; f < 5; f++) cand[f] = new byte[stride];
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride, best = 0;
                    long bestSum = long.MaxValue;
                    for (int f = 0; f < 5; f++)
                    {
                        long sum = 0;
                        for (int x = 0; x < stride; x++)
                        {
                            int v = pix[row + x];
                            int a = x >= ch ? pix[row + x - ch] : 0;
                            int b = y > 0 ? pix[row - stride + x] : 0;
                            int c = x >= ch && y > 0 ? pix[row - stride + x - ch] : 0;
                            int p = f == 0 ? v : f == 1 ? v - a : f == 2 ? v - b : f == 3 ? v - ((a + b) >> 1) : v - Paeth(a, b, c);
                            byte bt = (byte)p;
                            cand[f][x] = bt;
                            sum += bt < 128 ? bt : 256 - bt;
                        }
                        if (sum < bestSum) { bestSum = sum; best = f; }
                    }
                    raw[y * (stride + 1)] = (byte)best;
                    Buffer.BlockCopy(cand[best], 0, raw, y * (stride + 1) + 1, stride);
                }
                byte[] z = Zlib(raw, mode);
                var ms = new MemoryStream(z.Length + 64);
                ms.Write(Signature, 0, 8);
                var ihdr = new byte[13];
                PutBE(ihdr, 0, (uint)w); PutBE(ihdr, 4, (uint)h);
                ihdr[8] = 8; ihdr[9] = (byte)colorType;
                Chunk(ms, "IHDR", ihdr);
                Chunk(ms, "IDAT", z);
                Chunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }

            private static int Paeth(int a, int b, int c)
            {
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            }

            private static void PutBE(byte[] b, int at, uint v)
            {
                b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
            }

            private static void Chunk(MemoryStream ms, string type, byte[] body)
            {
                var b = new byte[12 + body.Length];
                PutBE(b, 0, (uint)body.Length);
                for (int i = 0; i < 4; i++) b[4 + i] = (byte)type[i];
                Buffer.BlockCopy(body, 0, b, 8, body.Length);
                PutBE(b, 8 + body.Length, Crc(b, 4, 8 + body.Length));
                ms.Write(b, 0, b.Length);
            }

            public static byte[] Zlib(byte[] data, string mode)
            {
                byte[] body = null;
                if (mode == "system")
                {
                    try
                    {
                        var ms = new MemoryStream();
                        using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(data, 0, data.Length);
                        body = ms.ToArray();
                        if (body.Length == 0 && data.Length > 0) body = null;
                    }
                    catch (Exception)
                    {
                        body = null;                     // e.g. no zlib under the game's Mono: use this file's encoder
                    }
                }
                if (body == null) body = mode == "stored" ? Stored(data) : DeflateFixed(data, 48);
                var outp = new byte[body.Length + 6];
                outp[0] = 0x78; outp[1] = 0x9C;
                Buffer.BlockCopy(body, 0, outp, 2, body.Length);
                PutBE(outp, body.Length + 2, Adler(data, 0, data.Length));
                return outp;
            }

            public static byte[] Stored(byte[] data)
            {
                var ms = new MemoryStream(data.Length + data.Length / 65535 * 5 + 8);
                int pos = 0;
                do
                {
                    int n = Math.Min(65535, data.Length - pos);
                    ms.WriteByte((byte)(pos + n >= data.Length ? 1 : 0));
                    ms.WriteByte((byte)n); ms.WriteByte((byte)(n >> 8));
                    ms.WriteByte((byte)~n); ms.WriteByte((byte)(~n >> 8));
                    ms.Write(data, pos, n);
                    pos += n;
                }
                while (pos < data.Length);
                return ms.ToArray();
            }

            public static byte[] DeflateFixed(byte[] data, int maxChain)
            {
                int n = data.Length;
                var bw = new BitWriter(n / 3 + 64);
                bw.Put(1, 1);                            // BFINAL
                bw.Put(1, 2);                            // BTYPE 01: fixed Huffman
                var head = new int[1 << 15];
                var prev = new int[1 << 15];
                for (int k = 0; k < head.Length; k++) head[k] = -1;
                int i = 0;
                while (i < n)
                {
                    int bestLen = 0, bestDist = 0;
                    if (i + 2 < n)
                    {
                        int h = ((data[i] << 10) ^ (data[i + 1] << 5) ^ data[i + 2]) & 0x7FFF;
                        int cand = head[h], chain = maxChain, maxLen = Math.Min(258, n - i);
                        while (cand >= 0 && i - cand <= 32768 && chain-- > 0)
                        {
                            if (data[cand + bestLen] == data[i + bestLen])
                            {
                                int l = 0;
                                while (l < maxLen && data[cand + l] == data[i + l]) l++;
                                if (l > bestLen) { bestLen = l; bestDist = i - cand; if (l == maxLen) break; }
                            }
                            cand = prev[cand & 0x7FFF];
                        }
                        prev[i & 0x7FFF] = head[h];
                        head[h] = i;
                    }
                    if (bestLen >= 3)
                    {
                        int li = LengthIndex[bestLen];
                        bw.Put(FixedCode[257 + li], FixedLen[257 + li]);
                        if (LExtra[li] > 0) bw.Put(bestLen - LBase[li], LExtra[li]);
                        int di = 29;
                        while (DBase[di] > bestDist) di--;
                        bw.Put(Reverse(di, 5), 5);
                        if (DExtra[di] > 0) bw.Put(bestDist - DBase[di], DExtra[di]);
                        for (int k = 1; k < bestLen; k++)
                        {
                            int j = i + k;
                            if (j + 2 >= n) break;
                            int h2 = ((data[j] << 10) ^ (data[j + 1] << 5) ^ data[j + 2]) & 0x7FFF;
                            prev[j & 0x7FFF] = head[h2];
                            head[h2] = j;
                        }
                        i += bestLen;
                    }
                    else
                    {
                        bw.Put(FixedCode[data[i]], FixedLen[data[i]]);
                        i++;
                    }
                }
                bw.Put(FixedCode[256], FixedLen[256]);   // end of block
                bw.Align();
                var outp = new byte[bw.Len];
                Buffer.BlockCopy(bw.Buf, 0, outp, 0, bw.Len);
                return outp;
            }

            // ---- decoder ----

            private sealed class BitReader
            {
                private readonly byte[] d;
                private int pos;
                private readonly int end;
                private int buf;
                private int cnt;

                public BitReader(byte[] data, int start, int end)
                {
                    d = data; pos = start; this.end = end;
                }

                public int Bits(int need)
                {
                    int val = buf;
                    while (cnt < need)
                    {
                        if (pos >= end) throw new InvalidDataException("deflate data ends early");
                        val |= d[pos++] << cnt;
                        cnt += 8;
                    }
                    buf = val >> need;
                    cnt -= need;
                    return val & ((1 << need) - 1);
                }

                public void Align()
                {
                    buf = 0; cnt = 0;
                }

                public byte Next()
                {
                    if (pos >= end) throw new InvalidDataException("deflate data ends early");
                    return d[pos++];
                }
            }

            private sealed class Huffman
            {
                public readonly short[] Count = new short[16];
                public short[] Symbol;
            }

            private static Huffman Build(int[] lengths, int offset, int n)
            {
                var h = new Huffman { Symbol = new short[n] };
                for (int s = 0; s < n; s++) h.Count[lengths[offset + s]]++;
                var offs = new short[16];
                for (int len = 1; len < 15; len++) offs[len + 1] = (short)(offs[len] + h.Count[len]);
                for (int s = 0; s < n; s++) if (lengths[offset + s] != 0) h.Symbol[offs[lengths[offset + s]]++] = (short)s;
                return h;
            }

            private static int DecodeSym(BitReader br, Huffman h)
            {
                int code = 0, first = 0, index = 0;
                for (int len = 1; len < 16; len++)
                {
                    code |= br.Bits(1);
                    int count = h.Count[len];
                    if (code - count < first) return h.Symbol[index + (code - first)];
                    index += count;
                    first += count;
                    first <<= 1;
                    code <<= 1;
                }
                throw new InvalidDataException("bad Huffman code");
            }

            private static Huffman fixedLit, fixedDist;

            public static byte[] Inflate(byte[] z, int expected)
            {
                if (z.Length < 6 || (z[0] & 0x0F) != 8 || ((z[0] << 8) | z[1]) % 31 != 0) throw new InvalidDataException("not a zlib stream");
                var outp = new byte[expected];
                int op = 0;
                var br = new BitReader(z, 2, z.Length - 4);
                int last;
                do
                {
                    last = br.Bits(1);
                    int type = br.Bits(2);
                    if (type == 0)
                    {
                        br.Align();
                        int len = br.Next() | (br.Next() << 8);
                        int nlen = br.Next() | (br.Next() << 8);
                        if ((len ^ 0xFFFF) != nlen) throw new InvalidDataException("bad stored block");
                        if (op + len > expected) throw new InvalidDataException("image data too long");
                        for (int k = 0; k < len; k++) outp[op++] = br.Next();
                        continue;
                    }
                    Huffman lit, dist;
                    if (type == 1)
                    {
                        if (fixedLit == null)
                        {
                            var l = new int[320];
                            for (int s = 0; s < 288; s++) l[s] = s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8;
                            for (int s = 288; s < 318; s++) l[s] = 5;
                            fixedLit = Build(l, 0, 288);
                            fixedDist = Build(l, 288, 30);
                        }
                        lit = fixedLit; dist = fixedDist;
                    }
                    else if (type == 2)
                    {
                        int nlen = br.Bits(5) + 257, ndist = br.Bits(5) + 1, ncode = br.Bits(4) + 4;
                        var lengths = new int[320];
                        for (int k = 0; k < ncode; k++) lengths[CodeLengthOrder[k]] = br.Bits(3);
                        Huffman lencode = Build(lengths, 0, 19);
                        lengths = new int[320];
                        int idx = 0;
                        while (idx < nlen + ndist)
                        {
                            int sym = DecodeSym(br, lencode);
                            if (sym < 16) { lengths[idx++] = sym; continue; }
                            int rep = 0, times;
                            if (sym == 16) { if (idx == 0) throw new InvalidDataException("bad code lengths"); rep = lengths[idx - 1]; times = 3 + br.Bits(2); }
                            else if (sym == 17) times = 3 + br.Bits(3);
                            else times = 11 + br.Bits(7);
                            if (idx + times > nlen + ndist) throw new InvalidDataException("bad code lengths");
                            while (times-- > 0) lengths[idx++] = rep;
                        }
                        lit = Build(lengths, 0, nlen);
                        dist = Build(lengths, nlen, ndist);
                    }
                    else throw new InvalidDataException("bad deflate block type");
                    while (true)
                    {
                        int sym = DecodeSym(br, lit);
                        if (sym < 256)
                        {
                            if (op >= expected) throw new InvalidDataException("image data too long");
                            outp[op++] = (byte)sym;
                        }
                        else if (sym == 256) break;
                        else
                        {
                            sym -= 257;
                            if (sym >= 29) throw new InvalidDataException("bad length code");
                            int len = LBase[sym] + br.Bits(LExtra[sym]);
                            int ds = DecodeSym(br, dist);
                            if (ds >= 30) throw new InvalidDataException("bad distance code");
                            int d = DBase[ds] + br.Bits(DExtra[ds]);
                            if (d > op) throw new InvalidDataException("distance too far back");
                            if (op + len > expected) throw new InvalidDataException("image data too long");
                            for (int k = 0; k < len; k++) { outp[op] = outp[op - d]; op++; }
                        }
                    }
                }
                while (last == 0);
                if (op != expected) throw new InvalidDataException("image data is " + op + " bytes, expected " + expected);
                return outp;
            }

            // Returns the raw (unfiltered) pixels and their channel count.
            private static byte[] DecodeRaw(byte[] png, out int w, out int h, out int ch)
            {
                if (png == null || png.Length < 20) throw new InvalidDataException("not a PNG");
                for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) throw new InvalidDataException("not a PNG (bad signature)");
                int pos = 8;
                w = 0; h = 0; ch = 0;
                int colorType = -1;
                var idat = new MemoryStream();
                bool ended = false;
                while (pos + 12 <= png.Length)
                {
                    int len = (int)GetBE(png, pos);
                    if (len < 0 || pos + 12 + len > png.Length) throw new InvalidDataException("truncated chunk");
                    string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                    if (Crc(png, pos + 4, pos + 8 + len) != GetBE(png, pos + 8 + len)) throw new InvalidDataException("bad CRC in " + type);
                    if (type == "IHDR")
                    {
                        w = (int)GetBE(png, pos + 8); h = (int)GetBE(png, pos + 12);
                        if (png[pos + 16] != 8) throw new InvalidDataException("bit depth " + png[pos + 16] + " not supported");
                        colorType = png[pos + 17];
                        if (png[pos + 20] != 0) throw new InvalidDataException("interlaced PNGs not supported");
                    }
                    else if (type == "IDAT") idat.Write(png, pos + 8, len);
                    else if (type == "IEND") { ended = true; break; }
                    pos += 12 + len;
                }
                if (!ended || colorType < 0) throw new InvalidDataException("incomplete PNG");
                ch = colorType == 0 ? 1 : colorType == 4 ? 2 : colorType == 2 ? 3 : colorType == 6 ? 4 : 0;
                if (ch == 0) throw new InvalidDataException("colour type " + colorType + " not supported");
                if (w < 1 || h < 1 || w > 4096 || h > 4096) throw new InvalidDataException("bad image size");
                int stride = w * ch;
                byte[] raw = Inflate(idat.ToArray(), (stride + 1) * h);
                var pix = new byte[stride * h];
                for (int y = 0; y < h; y++)
                {
                    int f = raw[y * (stride + 1)];
                    if (f > 4) throw new InvalidDataException("bad filter " + f);
                    for (int x = 0; x < stride; x++)
                    {
                        int v = raw[y * (stride + 1) + 1 + x];
                        int a = x >= ch ? pix[y * stride + x - ch] : 0;
                        int b = y > 0 ? pix[(y - 1) * stride + x] : 0;
                        int c = x >= ch && y > 0 ? pix[(y - 1) * stride + x - ch] : 0;
                        int p = f == 0 ? 0 : f == 1 ? a : f == 2 ? b : f == 3 ? (a + b) >> 1 : Paeth(a, b, c);
                        pix[y * stride + x] = (byte)(v + p);
                    }
                }
                return pix;
            }

            private static uint GetBE(byte[] b, int at)
            {
                return ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];
            }

            public static Img Decode(byte[] png)
            {
                int w, h, ch;
                byte[] pix = DecodeRaw(png, out w, out h, out ch);
                var img = new Img(w, h);
                for (int i = 0; i < w * h; i++)
                {
                    int s = i * ch, d = i * 4;
                    if (ch <= 2) { img.P[d] = img.P[d + 1] = img.P[d + 2] = pix[s]; img.P[d + 3] = ch == 2 ? pix[s + 1] : (byte)255; }
                    else { img.P[d] = pix[s]; img.P[d + 1] = pix[s + 1]; img.P[d + 2] = pix[s + 2]; img.P[d + 3] = ch == 4 ? pix[s + 3] : (byte)255; }
                }
                return img;
            }

            // Grey (or the alpha of grey+alpha / RGBA) as a mask.
            public static Mask DecodeMask(byte[] png)
            {
                int w, h, ch;
                byte[] pix = DecodeRaw(png, out w, out h, out ch);
                var m = new Mask(w, h);
                for (int i = 0; i < w * h; i++) m.A[i] = ch == 1 ? pix[i] : ch == 2 ? pix[i * 2 + 1] : ch == 4 ? pix[i * 4 + 3] : pix[i * 3];
                return m;
            }

            // Width and height from the IHDR, without decoding.
            public static bool Size(byte[] png, out int w, out int h)
            {
                w = h = 0;
                if (png == null || png.Length < 24) return false;
                for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) return false;
                w = (int)GetBE(png, 16); h = (int)GetBE(png, 20);
                return w > 0 && h > 0;
            }
        }

        #endregion

        #region Canvas

        private struct Rgb
        {
            public byte R, G, B;

            public static Rgb Parse(string hex)
            {
                var c = new Rgb();
                if (string.IsNullOrEmpty(hex)) return c;
                string s = hex.TrimStart('#');
                int v;
                if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                {
                    c.R = (byte)(v >> 16); c.G = (byte)(v >> 8); c.B = (byte)v;
                }
                return c;
            }
        }

        private sealed class Img
        {
            public readonly int W, H;
            public readonly byte[] P;                    // RGBA, rows top to bottom

            public Img(int w, int h)
            {
                W = w; H = h; P = new byte[w * h * 4];
            }
        }

        private sealed class Mask
        {
            public readonly int W, H;
            public readonly byte[] A;

            public Mask(int w, int h)
            {
                W = w; H = h; A = new byte[w * h];
            }
        }

        private static class Paint
        {
            public static void Fill(Img d, Rgb c)
            {
                for (int i = 0; i < d.P.Length; i += 4) { d.P[i] = c.R; d.P[i + 1] = c.G; d.P[i + 2] = c.B; d.P[i + 3] = 255; }
            }

            public static void Blend(Img d, int x, int y, Rgb c, int a)
            {
                if (a <= 0 || x < 0 || y < 0 || x >= d.W || y >= d.H) return;
                int i = (y * d.W + x) * 4;
                if (a >= 255) { d.P[i] = c.R; d.P[i + 1] = c.G; d.P[i + 2] = c.B; d.P[i + 3] = 255; return; }
                int ia = 255 - a;
                d.P[i] = (byte)((c.R * a + d.P[i] * ia + 127) / 255);
                d.P[i + 1] = (byte)((c.G * a + d.P[i + 1] * ia + 127) / 255);
                d.P[i + 2] = (byte)((c.B * a + d.P[i + 2] * ia + 127) / 255);
                d.P[i + 3] = (byte)(a + (d.P[i + 3] * ia + 127) / 255);
            }

            public static void Rect(Img d, int x, int y, int w, int h, Rgb c, int a)
            {
                for (int yy = Math.Max(0, y); yy < Math.Min(d.H, y + h); yy++)
                    for (int xx = Math.Max(0, x); xx < Math.Min(d.W, x + w); xx++) Blend(d, xx, yy, c, a);
            }

            public static void Outline(Img d, int inset, int thick, Rgb c, int a)
            {
                Rect(d, inset, inset, d.W - 2 * inset, thick, c, a);
                Rect(d, inset, d.H - inset - thick, d.W - 2 * inset, thick, c, a);
                Rect(d, inset, inset + thick, thick, d.H - 2 * inset - 2 * thick, c, a);
                Rect(d, d.W - inset - thick, inset + thick, thick, d.H - 2 * inset - 2 * thick, c, a);
            }

            public static void Tile(Img d, Img t)
            {
                if (t == null) return;
                for (int y = 0; y < d.H; y++)
                    for (int x = 0; x < d.W; x++)
                    {
                        int s = ((y % t.H) * t.W + (x % t.W)) * 4, o = (y * d.W + x) * 4;
                        Blend(d, x, y, new Rgb { R = t.P[s], G = t.P[s + 1], B = t.P[s + 2] }, t.P[s + 3]);
                        d.P[o + 3] = 255;
                    }
            }

            // Draws src scaled to dw x dh (area average when shrinking, nearest when growing) at dx, dy.
            public static void Image(Img d, Img src, int dx, int dy, int dw, int dh)
            {
                if (src == null || dw < 1 || dh < 1) return;
                for (int y = 0; y < dh; y++)
                {
                    int y0 = y * src.H / dh, y1 = Math.Max(y0 + 1, (y + 1) * src.H / dh);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = x * src.W / dw, x1 = Math.Max(x0 + 1, (x + 1) * src.W / dw);
                        long r = 0, g = 0, b = 0, a = 0, n = 0;
                        for (int sy = y0; sy < y1; sy++)
                            for (int sx = x0; sx < x1; sx++)
                            {
                                int i = (sy * src.W + sx) * 4, al = src.P[i + 3];
                                r += src.P[i] * al; g += src.P[i + 1] * al; b += src.P[i + 2] * al; a += al; n++;
                            }
                        if (a == 0) continue;
                        var c = new Rgb { R = (byte)(r / a), G = (byte)(g / a), B = (byte)(b / a) };
                        Blend(d, dx + x, dy + y, c, (int)(a / n));
                    }
                }
            }

            // Draws a mask (or a part of one) tinted with c, scaled to dw x dh.
            public static void MaskTo(Img d, Mask m, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, Rgb c, int alpha)
            {
                if (m == null || dw < 1 || dh < 1) return;
                for (int y = 0; y < dh; y++)
                {
                    int y0 = sy + y * sh / dh, y1 = Math.Max(y0 + 1, sy + (y + 1) * sh / dh);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = sx + x * sw / dw, x1 = Math.Max(x0 + 1, sx + (x + 1) * sw / dw);
                        int sum = 0, n = 0;
                        for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) { sum += m.A[yy * m.W + xx]; n++; }
                        int a = sum / n * alpha / 255;
                        Blend(d, dx + x, dy + y, c, a);
                    }
                }
            }

            public static void FlipX(Img d)
            {
                for (int y = 0; y < d.H; y++)
                    for (int x = 0; x < d.W / 2; x++)
                        for (int k = 0; k < 4; k++)
                        {
                            int a = (y * d.W + x) * 4 + k, b = (y * d.W + d.W - 1 - x) * 4 + k;
                            byte t = d.P[a]; d.P[a] = d.P[b]; d.P[b] = t;
                        }
            }

            public static void FlipY(Img d)
            {
                int stride = d.W * 4;
                var tmp = new byte[stride];
                for (int y = 0; y < d.H / 2; y++)
                {
                    Buffer.BlockCopy(d.P, y * stride, tmp, 0, stride);
                    Buffer.BlockCopy(d.P, (d.H - 1 - y) * stride, d.P, y * stride, stride);
                    Buffer.BlockCopy(tmp, 0, d.P, (d.H - 1 - y) * stride, stride);
                }
            }
        }

        #endregion

        #region Text

        // Board text may carry the chat colour tags of a lang string: [F4C96D]/crown[FFFFFF]. Any colour other than
        // white draws in the board's accent colour; white or [-] goes back to the text colour.
        private sealed class Styled
        {
            public string Text;
            public bool[] Accent;
        }

        private static Styled Parse(string s)
        {
            var sb = new StringBuilder();
            var acc = new List<bool>();
            bool on = false;
            if (s == null) s = "";
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '[' && i + 7 < s.Length && s[i + 7] == ']' && IsChatHex(s.Substring(i + 1, 6)))
                {
                    on = !string.Equals(s.Substring(i + 1, 6), "FFFFFF", StringComparison.OrdinalIgnoreCase);
                    i += 7;
                    continue;
                }
                if (s[i] == '[' && i + 2 < s.Length && s[i + 1] == '-' && s[i + 2] == ']') { on = false; i += 2; continue; }
                char c = s[i];
                if (c == '\n' || c == '\r' || c == '\t') c = ' ';
                sb.Append(c);
                acc.Add(on);
            }
            return new Styled { Text = sb.ToString(), Accent = acc.ToArray() };
        }

        private static Styled Slice(Styled s, int start, int len)
        {
            var a = new bool[len];
            Array.Copy(s.Accent, start, a, 0, len);
            return new Styled { Text = s.Text.Substring(start, len), Accent = a };
        }

        // Latin Extended-A (U+0100..U+017F) folded to its base letter, for names the atlases do not cover.
        private const string FoldLatinA = "AaAaAaCcCcCcCcDdDdEeEeEeEeEeGgGgGgGgHhHhIiIiIiIiIiIiJjKkkLlLlLlLlLlNnNnNnnNnOoOoOoOoRrRrRrSsSsSsSsTtTtTtUuUuUuUuUuUuWwYyYZzZzZzs";

        // The glyph for a character: itself, its letter without accents, its capital (display face), or '?'.
        private static Glyph GlyphFor(Font f, char ch)
        {
            Glyph g;
            if (f.Glyphs.TryGetValue(ch, out g)) return g;
            char basic = ch;
            if (ch >= 'Ā' && ch <= 'ſ') basic = FoldLatinA[ch - 0x100];
            else
            {
                try
                {
                    string d = ch.ToString().Normalize(NormalizationForm.FormD);
                    if (d.Length > 0) basic = d[0];
                }
                catch (Exception)
                {
                    basic = ch;
                }
            }
            if (basic != ch && f.Glyphs.TryGetValue(basic, out g)) return g;
            char up = char.ToUpperInvariant(basic);
            if (up != ch && f.Glyphs.TryGetValue(up, out g)) return g;
            f.Glyphs.TryGetValue('?', out g);
            return g;
        }

        private static int Measure(Font f, string s, int tracking)
        {
            int adv = 0;
            foreach (char c in s)
            {
                Glyph g = GlyphFor(f, c);
                if (g != null) adv += g.Adv + tracking * 64;
            }
            if (s.Length > 0) adv -= tracking * 64;
            return (adv + 63) / 64;
        }

        private static void DrawText(Img d, Font f, Styled s, int x, int baseline, Rgb colour, Rgb accent, int tracking)
        {
            int pen = x * 64;
            for (int i = 0; i < s.Text.Length; i++)
            {
                Glyph g = GlyphFor(f, s.Text[i]);
                if (g == null) continue;
                if (g.W > 0 && g.H > 0)
                    Paint.MaskTo(d, f.Atlas, g.X, g.Y, g.W, g.H, (pen + 32) / 64 + g.Left, baseline + g.Top, g.W, g.H, s.Accent[i] ? accent : colour, 255);
                pen += g.Adv + tracking * 64;
            }
        }

        // Greedy word wrap into at most maxLines lines; the last line ends in an ellipsis when text is left over.
        private static List<Styled> Wrap(Font f, Styled s, int maxW, int maxLines, int tracking)
        {
            var lines = new List<Styled>();
            string t = s.Text;
            int pos = 0;
            while (pos < t.Length && t[pos] == ' ') pos++;
            while (pos < t.Length && lines.Count < maxLines)
            {
                int end = pos, lastFit = -1;
                while (end <= t.Length)
                {
                    bool atBreak = end == t.Length || t[end] == ' ';
                    if (atBreak)
                    {
                        if (Measure(f, t.Substring(pos, end - pos), tracking) <= maxW) lastFit = end;
                        else break;
                        if (end == t.Length) break;
                    }
                    end++;
                }
                if (lastFit < 0)
                {
                    // one word longer than the line: cut it where it stops fitting
                    int cut = pos + 1;
                    while (cut < t.Length && t[cut] != ' ' && Measure(f, t.Substring(pos, cut + 1 - pos), tracking) <= maxW) cut++;
                    lastFit = cut;
                }
                lines.Add(Slice(s, pos, lastFit - pos));
                pos = lastFit;
                while (pos < t.Length && t[pos] == ' ') pos++;
            }
            if (pos < t.Length && lines.Count > 0) lines[lines.Count - 1] = Ellipsize(f, lines[lines.Count - 1], maxW, tracking, true);
            return lines;
        }

        // Two lines of about equal width for a title that does not fit on one (reads better centred than a greedy
        // break); falls back to Wrap when no single break makes both lines fit.
        private static List<Styled> Balanced(Font f, Styled s, int maxW, int tracking)
        {
            string t = s.Text.Trim();
            if (Measure(f, t, tracking) <= maxW) return Wrap(f, s, maxW, 1, tracking);
            int start = s.Text.IndexOf(t, StringComparison.Ordinal), best = -1, bestW = int.MaxValue;
            for (int i = 1; i < t.Length - 1; i++)
            {
                if (t[i] != ' ') continue;
                int w = Math.Max(Measure(f, t.Substring(0, i), tracking), Measure(f, t.Substring(i + 1), tracking));
                if (w <= maxW && w < bestW) { bestW = w; best = i; }
            }
            if (best < 0) return Wrap(f, s, maxW, 2, tracking);
            return new List<Styled> { Slice(s, start, best), Slice(s, start + best + 1, t.Length - best - 1) };
        }

        private static Styled Ellipsize(Font f, Styled s, int maxW, int tracking, bool force)
        {
            if (!force && Measure(f, s.Text, tracking) <= maxW) return s;
            string t = s.Text.TrimEnd();
            int n = t.Length;
            while (n > 0 && Measure(f, t.Substring(0, n).TrimEnd() + "…", tracking) > maxW) n--;
            string kept = t.Substring(0, n).TrimEnd();
            var a = new bool[kept.Length + 1];
            Array.Copy(s.Accent, a, kept.Length);
            if (kept.Length > 0) a[kept.Length] = a[kept.Length - 1];
            return new Styled { Text = kept + "…", Accent = a };
        }

        private static bool IsChatHex(string s)
        {
            foreach (char c in s) if ("0123456789ABCDEFabcdef".IndexOf(c) < 0) return false;
            return true;
        }

        #endregion

        #region Board layout

        // What a live board shows. Key is everything drawn, so "nothing changed" is a string comparison.
        private sealed class Board
        {
            public string Kind;
            public bool Iron;                            // iron theme (dark) instead of parchment
            public string Kicker;                        // small capitals at the top
            public string Hero;                          // sprite id: rgba sprites draw as they are, masks in HeroTint
            public string HeroTint;
            public int HeroSize;
            public bool Display;                         // Title in the display face, upper case (posters)
            public string Title;
            public string TitleTint;
            public string Headline;
            public string Subtitle;
            public readonly List<Row> Rows = new List<Row>();
            public string Body;
            public string Footer;

            public string Key
            {
                get
                {
                    var sb = new StringBuilder();
                    sb.Append(Kind).Append('|').Append(Iron).Append('|').Append(Kicker).Append('|').Append(Hero).Append('|').Append(HeroTint).Append('|')
                      .Append(HeroSize).Append('|').Append(Display).Append('|').Append(Title).Append('|').Append(TitleTint).Append('|').Append(Headline)
                      .Append('|').Append(Subtitle).Append('|').Append(Body).Append('|').Append(Footer);
                    foreach (Row r in Rows) sb.Append("|r:").Append(r.Icon).Append(',').Append(r.IconTint).Append(',').Append(r.Text).Append(',').Append(r.Right).Append(',').Append(r.Sub);
                    return sb.ToString();
                }
            }
        }

        private sealed class Row
        {
            public string Icon;
            public string IconTint;
            public string Text;
            public string Right;
            public string Sub;
        }

        private sealed class Block
        {
            public int Height;
            public int Gap;                              // space after
            public int Priority;                         // dropped first when the board overflows: higher goes first
            public Action<int> Draw;                     // y of the block's top
        }

        // Board canvas sizes: portrait matches the finished posters (256 x 320).
        private static void BoardSize(bool landscape, out int w, out int h)
        {
            if (landscape) { w = 320; h = 256; } else { w = 256; h = 320; }
        }

        private Img RenderBoard(Board b, bool landscape)
        {
            int W, H;
            BoardSize(landscape, out W, out H);
            var img = new Img(W, H);
            Rgb bg = Colour(b.Iron ? "iron900" : "parchment");
            Rgb text = Colour(b.Iron ? "parchment2" : "ink");
            Rgb soft = Colour(b.Iron ? "iron200" : "inkSoft");
            Rgb accent = Colour(b.Iron ? "emberHot" : "emberDeep");
            Rgb rule = Colour(b.Iron ? "ember" : "parchmentEdge");
            Paint.Fill(img, bg);
            if (config.BoardTexture) Paint.Tile(img, Sprite(b.Iron ? "tile-iron" : "tile-parchment"));
            Paint.Outline(img, 6, 2, Colour(b.Iron ? "ember" : "inkSoft"), 255);
            Paint.Outline(img, 11, 1, Colour(b.Iron ? "iron600" : "parchmentEdge"), 255);

            Font fTitle = art.Fonts["title"], fHead = art.Fonts["head"], fLabel = art.Fonts["label"];
            Font fBody = art.Fonts["body"], fSmall = art.Fonts["small"], fItalic = art.Fonts["italic"];
            Font fDisplay;
            if (!art.Fonts.TryGetValue("display", out fDisplay)) fDisplay = fTitle;
            int left = 22, right = W - 22, maxW = right - left, top = 20;
            var blocks = new List<Block>();

            if (!string.IsNullOrEmpty(b.Kicker))
            {
                Styled k = Parse(b.Kicker.ToUpperInvariant());
                int kt = 2;                              // letter-spaced like the finished posters, tighter if it must
                while (kt > 0 && Measure(fLabel, k.Text, kt) > maxW) kt--;
                k = Ellipsize(fLabel, k, maxW, kt, false);
                blocks.Add(new Block { Height = fLabel.Ascent + 2, Gap = 8, Priority = 1, Draw = delegate(int y) { DrawCentred(img, fLabel, k, W, y + fLabel.Ascent, soft, accent, kt); } });
            }
            Block hero = null;
            Img heroImg = null;
            Mask heroMask = null;
            if (!string.IsNullOrEmpty(b.Hero))
            {
                heroImg = Sprite(b.Hero);
                if (heroImg == null) heroMask = MaskSprite(b.Hero);
                if (heroImg != null || heroMask != null)
                {
                    int size = b.HeroSize > 0 ? b.HeroSize : heroImg != null ? heroImg.H : heroMask.H;
                    Rgb tint = string.IsNullOrEmpty(b.HeroTint) ? accent : Colour(b.HeroTint);
                    hero = new Block { Height = size, Gap = 8, Priority = 0 };
                    Block hb = hero;
                    hero.Draw = delegate(int y)
                    {
                        int s = hb.Height;
                        if (heroImg != null) Paint.Image(img, heroImg, (W - s) / 2, y, s, s);
                        else Paint.MaskTo(img, heroMask, 0, 0, heroMask.W, heroMask.H, (W - s) / 2, y, s, s, tint, 255);
                    };
                    blocks.Add(hero);
                }
            }
            if (!string.IsNullOrEmpty(b.Title))
            {
                Rgb tc = string.IsNullOrEmpty(b.TitleTint) ? text : Colour(b.TitleTint);
                Font tf = b.Display ? fDisplay : fTitle;
                Styled ts = Parse(b.Display ? b.Title.ToUpperInvariant() : b.Title);
                int track = b.Display ? 2 : 0;
                if (Measure(tf, ts.Text, track) > maxW && b.Display) { tf = fTitle; ts = Parse(b.Title.ToUpperInvariant()); track = 0; }
                if (Measure(tf, ts.Text, track) > maxW) { tf = fHead; track = 0; }
                List<Styled> lines = Balanced(tf, ts, maxW, track);
                Font lf = tf;
                int lt = track;
                foreach (Styled line in lines)
                {
                    Styled l = line;
                    blocks.Add(new Block { Height = lf.Ascent + lf.Descent, Gap = 2, Priority = 0, Draw = delegate(int y) { DrawCentred(img, lf, l, W, y + lf.Ascent, tc, accent, lt); } });
                }
                if (blocks.Count > 0) blocks[blocks.Count - 1].Gap = 6;
            }
            if (!string.IsNullOrEmpty(b.Headline))
            {
                foreach (Styled line in Balanced(fHead, Parse(b.Headline), maxW, 0))
                {
                    Styled l = line;
                    blocks.Add(new Block { Height = fHead.Ascent + fHead.Descent, Gap = 2, Priority = 2, Draw = delegate(int y) { DrawCentred(img, fHead, l, W, y + fHead.Ascent, text, accent, 0); } });
                }
                blocks[blocks.Count - 1].Gap = 6;
            }
            if (blocks.Count > 0)
            {
                int rw = maxW * 3 / 5;
                blocks.Add(new Block { Height = 1, Gap = 8, Priority = 3, Draw = delegate(int y) { Paint.Rect(img, (W - rw) / 2, y, rw, 1, rule, 255); } });
            }
            if (!string.IsNullOrEmpty(b.Subtitle))
            {
                foreach (Styled line in Wrap(fItalic, Parse(b.Subtitle), maxW - 8, 2, 0))
                {
                    Styled l = line;
                    blocks.Add(new Block { Height = fItalic.LineHeight, Gap = 0, Priority = 4, Draw = delegate(int y) { DrawCentred(img, fItalic, l, W, y + fItalic.Ascent, soft, accent, 0); } });
                }
                blocks[blocks.Count - 1].Gap = 8;
            }
            foreach (Row row in b.Rows)
            {
                Row r = row;
                int iconSize = r.Icon != null && r.Icon.StartsWith("sigil-") ? 28 : 22;
                int textX = left + (string.IsNullOrEmpty(r.Icon) ? 0 : iconSize + 7);
                int rightW = string.IsNullOrEmpty(r.Right) ? 0 : Measure(fLabel, Parse(r.Right).Text, 0) + 6;
                // a row without a value on the right may take two lines
                List<Styled> tl = string.IsNullOrEmpty(r.Right) ? Balanced(fBody, Parse(r.Text), right - textX, 0) : Wrap(fBody, Parse(r.Text), right - rightW - textX, 1, 0);
                int n = Math.Max(1, tl.Count);
                int textH = Math.Max(iconSize, n * RowLine(fBody)) + (string.IsNullOrEmpty(r.Sub) ? 0 : fSmall.LineHeight);
                blocks.Add(new Block { Height = textH, Gap = 6, Priority = 5, Draw = delegate(int y) { DrawRow(img, r, tl, y, left, right, textX, iconSize, fBody, fSmall, fLabel, text, soft, accent); } });
            }
            if (!string.IsNullOrEmpty(b.Body))
            {
                foreach (Styled line in Wrap(fBody, Parse(b.Body), maxW, 6, 0))
                {
                    Styled l = line;
                    blocks.Add(new Block { Height = fBody.LineHeight, Gap = 0, Priority = 6, Draw = delegate(int y) { DrawCentred(img, fBody, l, W, y + fBody.Ascent, text, accent, 0); } });
                }
            }

            int bottom = H - 18;
            if (!string.IsNullOrEmpty(b.Footer))
            {
                List<Styled> fl = Wrap(fSmall, Parse(b.Footer), maxW, 2, 0);
                int fh = fl.Count * fSmall.LineHeight;
                bottom = H - 16 - fh - 10;
                for (int i = 0; i < fl.Count; i++) DrawCentred(img, fSmall, fl[i], W, H - 16 - fh + i * fSmall.LineHeight + fSmall.Ascent, soft, accent, 0);
            }

            // Fit: shrink the hero first, then drop the least important blocks from the end.
            int avail = bottom - top;
            while (Total(blocks) > avail && hero != null && hero.Height > 40) hero.Height = hero.Height * 3 / 4;
            while (Total(blocks) > avail && blocks.Count > 0)
            {
                int worst = -1;
                for (int i = blocks.Count - 1; i >= 0; i--) if (blocks[i].Priority > 2 && (worst < 0 || blocks[i].Priority > blocks[worst].Priority)) worst = i;
                if (worst < 0) worst = blocks.Count - 1;
                blocks.RemoveAt(worst);
            }
            int total = Total(blocks);
            int yy = b.Rows.Count > 0 ? top : top + Math.Max(0, (avail - total) / 2);
            foreach (Block blk in blocks)
            {
                blk.Draw(yy);
                yy += blk.Height + blk.Gap;
            }
            return img;
        }

        private static int Total(List<Block> blocks)
        {
            int t = 0;
            for (int i = 0; i < blocks.Count; i++) t += blocks[i].Height + (i < blocks.Count - 1 ? blocks[i].Gap : 0);
            return t;
        }

        private static void DrawCentred(Img img, Font f, Styled s, int W, int baseline, Rgb c, Rgb accent, int tracking)
        {
            int w = Measure(f, s.Text, tracking);
            DrawText(img, f, s, (W - w) / 2, baseline, c, accent, tracking);
        }

        private static int RowLine(Font f)
        {
            return f.LineHeight - 2;
        }

        private void DrawRow(Img img, Row r, List<Styled> lines, int y, int left, int right, int textX, int iconSize, Font fBody, Font fSmall, Font fLabel, Rgb text, Rgb soft, Rgb accent)
        {
            int lh = RowLine(fBody);
            int textTop = y + (lines.Count <= 1 ? Math.Max(0, (iconSize - lh) / 2) : 0);
            if (!string.IsNullOrEmpty(r.Icon))
            {
                Img s = Sprite(r.Icon);
                int iy = y + Math.Max(0, (lh - iconSize) / 2);
                if (s != null) Paint.Image(img, s, left, iy, iconSize, iconSize);
                else
                {
                    Mask m = MaskSprite(r.Icon);
                    if (m != null) Paint.MaskTo(img, m, 0, 0, m.W, m.H, left, iy, iconSize, iconSize, string.IsNullOrEmpty(r.IconTint) ? soft : Colour(r.IconTint), 255);
                }
            }
            if (!string.IsNullOrEmpty(r.Right))
            {
                Styled rs = Parse(r.Right);
                int rw = Measure(fLabel, rs.Text, 0);
                DrawText(img, fLabel, rs, right - rw, textTop + (lh + fLabel.Ascent) / 2, soft, accent, 0);
            }
            for (int i = 0; i < lines.Count; i++) DrawText(img, fBody, lines[i], textX, textTop + fBody.Ascent + i * lh, text, accent, 0);
            if (!string.IsNullOrEmpty(r.Sub))
            {
                Styled ss = Ellipsize(fSmall, Parse(r.Sub), right - textX, 0, false);
                int below = Math.Max(y + iconSize, textTop + Math.Max(1, lines.Count) * lh);
                DrawText(img, fSmall, ss, textX, below + fSmall.Ascent - 2, soft, accent, 0);
            }
        }

        #endregion

        #region Live boards

        // Read-only views of other plugins' data files (field names are those files' JSON contracts).
        private class ChronEntry
        {
            public int id;
            public string ts;
            public string type;
            public string title;
            public string detail;
        }

        private class ContractsOutlaw
        {
            public string Name;
            public DateTime Until;
            public bool Court;
        }

        private class ContractsView
        {
            public Dictionary<string, ContractsOutlaw> Outlaws;
        }

        private sealed class Wanted
        {
            public string Id, Name, House;
            public DateTime Until;
            public bool Court;
            public int Bounties;
        }

        private bool BoardEnabled(string kind)
        {
            return config.LiveBoards && !config.DisabledBoards.Contains(kind);
        }

        private static bool IsBoard(string name)
        {
            return Array.IndexOf(Boards, name) >= 0;
        }

        // Builds what a live board shows now. Never throws: a broken source gives a board that says so.
        private Board Gather(string kind, string arg)
        {
            try
            {
                switch (kind)
                {
                    case "chronicle": return ChronicleBoard();
                    case "wanted": return WantedBoard(arg);
                    case "standings": return StandingsBoard();
                    case "proclamation": return ProclamationBoard();
                    case "event": return EventBoard(arg);
                    case "ironbreaker": return IronbreakerBoard();
                    case "notice": return NoticeBoard(arg);
                }
            }
            catch (Exception ex)
            {
                PrintWarning("Board " + kind + ": " + ex.Message);
            }
            return new Board { Kind = kind, Kicker = B("KickerRealm"), Title = B("Unavailable.Title"), Body = B("Unavailable.Body") };
        }

        private string B(string key, params object[] args)
        {
            return Msg("Board." + key, null, args);
        }

        private List<ChronEntry> ChronicleEntries()
        {
            object last = RealmChronicle != null ? RealmChronicle.Call("GetLastEventId") : null;
            int id = last is int ? (int)last : -1;
            if (id >= 0 && id == chronicleSeen) return chronicleCache;
            if (RealmChronicle == null) { chronicleCache = new List<ChronEntry>(); chronicleSeen = -1; return chronicleCache; }
            List<ChronEntry> list = null;
            try
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile("RealmChronicle"))
                    list = Interface.Oxide.DataFileSystem.ReadObject<List<ChronEntry>>("RealmChronicle");
            }
            catch (Exception ex)
            {
                PrintWarning("Could not read oxide/data/RealmChronicle.json: " + ex.Message);
            }
            chronicleCache = list ?? new List<ChronEntry>();
            chronicleCache.RemoveAll(delegate(ChronEntry e) { return e == null || string.IsNullOrEmpty(e.title); });
            chronicleCache.Sort(delegate(ChronEntry a, ChronEntry b) { return b.id.CompareTo(a.id); });
            if (chronicleCache.Count > 50) chronicleCache.RemoveRange(50, chronicleCache.Count - 50);
            chronicleSeen = id;
            return chronicleCache;
        }

        private Board ChronicleBoard()
        {
            var b = new Board { Kind = "chronicle", Kicker = B("KickerRealm"), Title = B("Chronicle.Title"), Footer = B("Chronicle.Footer") };
            if (RealmChronicle == null) { b.Body = B("Chronicle.Missing"); return b; }
            List<ChronEntry> list = ChronicleEntries();
            if (list.Count == 0) { b.Body = B("Chronicle.Empty"); return b; }
            for (int i = 0; i < list.Count && b.Rows.Count < config.ChronicleRows; i++)
            {
                ChronEntry e = list[i];
                string icon = null;
                if (e.type != null && art.TypeIcons != null) art.TypeIcons.TryGetValue(e.type, out icon);
                b.Rows.Add(new Row { Icon = icon != null ? "icon-24-" + icon : "icon-24-decree", IconTint = ToneOfType(e.type), Text = Clean(e.title, 80), Sub = ShortDate(e.ts) });
            }
            return b;
        }

        private static string ToneOfType(string type)
        {
            if (type == null) return "inkSoft";
            if (type.Contains("broken") || type.Contains("rebellion") || type.Contains("outlaw") || type.Contains("crime") || type.Contains("exile") || type.Contains("kill")) return "blood";
            if (type == "coronation" || type == "decree" || type == "succession" || type.Contains("champion") || type.Contains("season")) return "emberDeep";
            if (type.Contains("treaty_signed") || type.Contains("oath_sworn") || type.Contains("pardon") || type.Contains("released")) return "moss";
            return "inkSoft";
        }

        // The outlaw roll, read once per refresh pass however many wanted posters there are.
        private List<Wanted> Outlaws()
        {
            DateTime now = clock();
            if (outlawCache != null && Math.Abs((now - outlawAt).TotalSeconds) < 5) return outlawCache;
            outlawAt = now;
            outlawCache = ReadOutlaws(now);
            return outlawCache;
        }

        private List<Wanted> ReadOutlaws(DateTime now)
        {
            var map = new Dictionary<string, Wanted>();
            if (RealmContracts != null)
            {
                try
                {
                    if (Interface.Oxide.DataFileSystem.ExistsDatafile("RealmContracts"))
                    {
                        ContractsView v = Interface.Oxide.DataFileSystem.ReadObject<ContractsView>("RealmContracts");
                        if (v != null && v.Outlaws != null)
                            foreach (KeyValuePair<string, ContractsOutlaw> kv in v.Outlaws)
                                if (kv.Value != null && !string.IsNullOrEmpty(kv.Key) && kv.Value.Until > now)
                                    map[kv.Key] = new Wanted { Id = kv.Key, Name = kv.Value.Name, Until = kv.Value.Until, Court = kv.Value.Court };
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning("Could not read oxide/data/RealmContracts.json: " + ex.Message);
                }
            }
            if (RealmLaws != null)
            {
                var court = RealmLaws.Call("GetCourtOutlaws") as string[];
                if (court != null)
                    foreach (string line in court)
                    {
                        string[] p = (line ?? "").Split('|');
                        if (p.Length < 3 || string.IsNullOrEmpty(p[0])) continue;
                        DateTime until;
                        if (!DateTime.TryParse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out until)) continue;
                        if (until <= now) continue;
                        Wanted w;
                        if (!map.TryGetValue(p[0], out w)) map[p[0]] = w = new Wanted { Id = p[0], Name = p[1] };
                        w.Court = true;
                        if (until > w.Until) w.Until = until;
                        if (string.IsNullOrEmpty(w.Name)) w.Name = p[1];
                    }
            }
            var list = new List<Wanted>(map.Values);
            foreach (Wanted w in list)
            {
                object n = RealmContracts != null ? RealmContracts.Call("GetBountyCount", w.Id) : null;
                w.Bounties = n is int ? (int)n : 0;
                w.House = RealmHouses != null ? RealmHouses.Call("GetHouse", w.Id) as string : null;
                if (string.IsNullOrEmpty(w.Name)) w.Name = w.Id;
            }
            list.Sort(delegate(Wanted a, Wanted b)
            {
                int c = b.Bounties.CompareTo(a.Bounties);
                if (c == 0) c = b.Until.CompareTo(a.Until);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            return list;
        }

        private Board WantedBoard(string arg)
        {
            int n;
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1) n = 1;
            var b = new Board { Kind = "wanted", Kicker = B("KickerCrown"), Display = true, TitleTint = "blood", Footer = B("Wanted.Footer") };
            if (RealmContracts == null && RealmLaws == null) { b.Title = B("Wanted.Title"); b.Body = B("Wanted.Missing"); return b; }
            List<Wanted> list = Outlaws();
            if (list.Count < n)
            {
                b.Display = false; b.TitleTint = null;
                b.Hero = "icon-64-sheathed"; b.HeroTint = "moss";
                b.Title = B("Wanted.NoneTitle");
                b.Body = list.Count == 0 ? B("Wanted.None") : B("Wanted.NoneN", n, list.Count);
                return b;
            }
            Wanted w = list[n - 1];
            if (w.Court) b.Kicker = B("KickerCourt");
            b.Title = B("Wanted.Title");
            string sigil = SigilSprite(w.House, 96);
            b.Hero = sigil ?? "icon-64-dagger";
            b.HeroTint = "blood";
            b.HeroSize = sigil != null ? 96 : 64;
            b.Headline = Clean(w.Name, 40);
            b.Subtitle = string.IsNullOrEmpty(w.House) ? B("Wanted.NoHouse") : B("Wanted.OfHouse", Clean(w.House, 30));
            b.Body = (w.Bounties == 1 ? B("Wanted.Bounty1") : w.Bounties > 1 ? B("Wanted.BountyN", w.Bounties) : B("Wanted.Bounty0"))
                + " " + B("Wanted.Until", RealmTime(w.Until));
            return b;
        }

        private Board StandingsBoard()
        {
            var b = new Board { Kind = "standings", Footer = B("Standings.Footer") };
            if (RealmSeasons == null) { b.Kicker = B("KickerRealm"); b.Title = B("Standings.Title"); b.Body = B("Standings.Missing"); return b; }
            object num = RealmSeasons.Call("GetSeasonNumber");
            string name = RealmSeasons.Call("GetSeasonName") as string;
            int season = num is int ? (int)num : 0;
            if (season <= 0)
            {
                b.Kicker = B("KickerRealm"); b.Title = B("Standings.Title"); b.Hero = "icon-64-laurel"; b.HeroTint = "emberDeep";
                b.Body = B("Standings.None");
                return b;
            }
            b.Kicker = B("Standings.Kicker", season);
            b.Title = string.IsNullOrEmpty(name) ? B("Standings.Title") : Clean(name, 40);
            var rows = RealmSeasons.Call("GetSeasonStandings") as List<Dictionary<string, object>>;
            if (rows == null || rows.Count == 0) { b.Body = B("Standings.Empty"); return b; }
            foreach (Dictionary<string, object> r in rows)
            {
                if (b.Rows.Count >= 6) break;
                string house = Get(r, "house") as string;
                if (string.IsNullOrEmpty(house)) continue;
                int rank = ToInt(Get(r, "rank")), score = ToInt(Get(r, "score")), crownDays = ToInt(Get(r, "crownDays"));
                b.Rows.Add(new Row
                {
                    Icon = SigilSprite(house, 32) ?? "icon-24-house",
                    IconTint = "inkSoft",
                    Text = B("Standings.Row", rank, Clean(house, 24)),
                    Right = score.ToString(CultureInfo.InvariantCulture),
                    Sub = crownDays > 0 ? (crownDays == 1 ? B("Standings.Crown1") : B("Standings.CrownN", crownDays)) : null
                });
            }
            return b;
        }

        private Board ProclamationBoard()
        {
            var b = new Board { Kind = "proclamation", Kicker = B("Proclamation.Kicker"), Hero = "icon-64-crown", HeroTint = "emberDeep", Footer = B("Proclamation.Footer") };
            if (CrownAndConsequences == null) { b.Title = B("Proclamation.EmptyTitle"); b.Body = B("Proclamation.Missing"); return b; }
            string king = CrownAndConsequences.Call("GetKingName") as string;
            string house = CrownAndConsequences.Call("GetKingHouse") as string;
            string since = CrownAndConsequences.Call("GetKingSince") as string;
            if (string.IsNullOrEmpty(king))
            {
                b.Title = B("Proclamation.EmptyTitle");
                b.Body = B("Proclamation.Empty");
                return b;
            }
            b.Title = Clean(king, 32);
            if (!string.IsNullOrEmpty(house)) b.Headline = B("Proclamation.OfHouse", Clean(house, 30));
            DateTime t;
            if (ParseIso(since, out t)) b.Subtitle = B("Proclamation.Since", RealmDate(t));
            if (RealmChronicle != null)
            {
                foreach (ChronEntry e in ChronicleEntries())
                {
                    if (e.type != "decree") continue;
                    DateTime at;
                    if (ParseIso(since, out t) && ParseIso(e.ts, out at) && at < t) break;     // a decree of an earlier reign
                    b.Body = B("Proclamation.Decree", Clean(e.title, 90));
                    break;
                }
            }
            if (b.Body == null) b.Body = B("Proclamation.NoDecree");
            return b;
        }

        private Board EventBoard(string arg)
        {
            var b = new Board { Kind = "event", Kicker = B("KickerCrown"), Hero = "icon-64-beacon", HeroTint = "emberDeep" };
            string kind = string.IsNullOrEmpty(arg) ? null : arg.ToLowerInvariant();
            if (kind != null && Array.IndexOf(EventKinds, kind) < 0) kind = null;
            if (RealmEvents == null) { b.Title = B("Event.NoneTitle"); b.Body = B("Event.Missing"); return b; }
            DateTime now = clock();
            var active = RealmEvents.Call("GetActiveEvents") as string[];
            if (active != null)
            {
                foreach (string line in active)
                {
                    string[] p = (line ?? "").Split('|');
                    if (p.Length < 3 || (kind != null && p[0] != kind)) continue;
                    DateTime end;
                    if (!ParseIso(p[2], out end) || end <= now) continue;
                    FillEvent(b, p[0]);
                    b.Subtitle = B("Event.Running", RealmClock(end));
                    return b;
                }
            }
            if (kind != null)
            {
                FillEvent(b, kind);
                b.Subtitle = B("Event.Watch");
                return b;
            }
            var next = RealmEvents.Call("GetNextEvent") as Dictionary<string, object>;
            if (next == null || !(Get(next, "at") is DateTime))
            {
                b.Title = B("Event.NoneTitle");
                b.Body = B("Event.None");
                return b;
            }
            string title = Get(next, "title") as string;
            FillEvent(b, KindOfTitle(title));
            if (!string.IsNullOrEmpty(title)) b.Title = Clean(title, 40);
            b.Subtitle = B("Event.Next", RealmTime((DateTime)Get(next, "at")));
            return b;
        }

        private void FillEvent(Board b, string kind)
        {
            int i = Array.IndexOf(EventKinds, kind);
            if (i < 0) { b.Title = B("Event.NoneTitle"); return; }
            b.Hero = "icon-64-" + EventIcons[i];
            b.HeroTint = kind == "crown_night" ? "blood" : kind == "kings_hunt" ? "moss" : kind == "truce" ? "inkSoft" : "emberDeep";
            b.Title = B("Event.Title." + kind);
            b.Body = B("Event.Line." + kind);
            b.Footer = B("Event.Footer." + kind);
        }

        private static string KindOfTitle(string title)
        {
            string t = (title ?? "").ToLowerInvariant();
            if (t.Contains("crown")) return "crown_night";
            if (t.Contains("tourn")) return "tournament";
            if (t.Contains("hunt")) return "kings_hunt";
            if (t.Contains("truce")) return "truce";
            return null;
        }

        private Board IronbreakerBoard()
        {
            var b = new Board { Kind = "ironbreaker", Iron = true, Kicker = B("Ironbreaker.Kicker"), Hero = "hammer-128", HeroSize = 112, Title = B("Ironbreaker.Title"), TitleTint = "emberHot" };
            string bearer = null;
            try { bearer = RealmLegendary != null ? RealmLegendary.Call("GetBearerName") as string : null; }
            catch (Exception) { bearer = null; }
            if (string.IsNullOrEmpty(bearer) || bearer.Trim().Length == 0)
            {
                b.Headline = B("Ironbreaker.Unclaimed");
                b.Subtitle = B("Ironbreaker.UnclaimedLine");
            }
            else
            {
                b.Headline = B("Ironbreaker.Bearer", Clean(bearer, 32));
                b.Subtitle = B("Ironbreaker.BearerLine");
            }
            return b;
        }

        private Board NoticeBoard(string arg)
        {
            string title = arg ?? "", body = "";
            int bar = title.IndexOf('|');
            if (bar >= 0) { body = title.Substring(bar + 1).Trim(); title = title.Substring(0, bar).Trim(); }
            return new Board { Kind = "notice", Kicker = B("KickerRealm"), Hero = "emblem-96", HeroSize = 72, Title = Clean(title, 40), Body = Clean(body, config.MaxNoticeLength) };
        }

        private string SigilSprite(string house, int size)
        {
            if (string.IsNullOrEmpty(house)) return null;
            string key = house.Trim().ToLowerInvariant();
            if (key.StartsWith("house ")) key = key.Substring(6);
            if (Array.IndexOf(HouseIds, key) < 0) return null;
            string id = "sigil-" + size + "-" + key;
            return art != null && art.Items.ContainsKey(id) ? id : null;
        }

        private static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v : null;
        }

        private static int ToInt(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        private static bool ParseIso(string s, out DateTime t)
        {
            t = DateTime.MinValue;
            if (string.IsNullOrEmpty(s)) return false;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t);
        }

        private double RealmOffset()
        {
            object o = CrownAndConsequences != null ? CrownAndConsequences.Call("GetUtcOffsetHours") : null;
            return o is double ? (double)o : 0;
        }

        // Times on boards are in realm time (CrownAndConsequences' UtcOffsetHours), like the rebellion windows.
        private string RealmTime(DateTime utc)
        {
            return utc.AddHours(RealmOffset()).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);
        }

        private string RealmDate(DateTime utc)
        {
            return utc.AddHours(RealmOffset()).ToString("d MMMM", CultureInfo.InvariantCulture);
        }

        private string RealmClock(DateTime utc)
        {
            return utc.AddHours(RealmOffset()).ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private string ShortDate(string iso)
        {
            DateTime t;
            return ParseIso(iso, out t) ? t.AddHours(RealmOffset()).ToString("d MMM", CultureInfo.InvariantCulture) : "";
        }

        // Text from players and other plugins: one line, no colour tags, at most max characters.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsControl(c)) sb.Append(' ');
                else if (c == '[' || c == ']') sb.Append(c == '[' ? '(' : ')');
                else sb.Append(c);
            }
            string t = sb.ToString().Trim();
            while (t.Contains("  ")) t = t.Replace("  ", " ");
            return t.Length > max ? t.Substring(0, max - 1).TrimEnd() + "…" : t;
        }

        #endregion

        #region Drawing a sign

        private sealed class Picture
        {
            public byte[] Png;
            public int W, H;
            public string Key;
        }

        // The picture for a sign now: a painting from the bundle as it is, or a live board composed here.
        private Picture Compose(SignRec s, out string error)
        {
            error = null;
            if (art == null) { error = artError ?? "no art bundle"; return null; }
            if (IsBoard(s.Board))
            {
                Board b = Gather(s.Board, s.Arg);
                string key = FullKey(s, b.Key);
                Img img = RenderBoard(b, Landscape(s));
                if (config.FlipX) Paint.FlipX(img);
                if (config.FlipY) Paint.FlipY(img);
                return Finish(Png.Encode(img, config.Compression), img.W, img.H, key, out error);
            }
            ArtItem item;
            if (!art.Items.TryGetValue(s.Board, out item) || item.Kind != "painting") { error = "no painting '" + s.Board + "' in the art bundle"; return null; }
            byte[] png = Convert.FromBase64String(item.Png);
            string pkey = "painting|" + item.Id + "|" + art.Version + "|" + config.FlipX + config.FlipY;
            if (config.FlipX || config.FlipY)
            {
                Img img = Png.Decode(png);
                if (config.FlipX) Paint.FlipX(img);
                if (config.FlipY) Paint.FlipY(img);
                png = Png.Encode(img, config.Compression);
            }
            return Finish(png, item.Width, item.Height, pkey, out error);
        }

        private static bool Landscape(SignRec s)
        {
            return s.Face != null && s.Face.W > s.Face.H * 1.15f;
        }

        // Everything that decides a live board's picture: what it shows, its layout, the art and the drawing options.
        private string FullKey(SignRec s, string boardKey)
        {
            return boardKey + "|" + (Landscape(s) ? "L" : "P") + "|" + (art != null ? art.Version : "") + "|" + config.BoardTexture + config.FlipX + config.FlipY;
        }

        private Picture Finish(byte[] png, int w, int h, string key, out string error)
        {
            error = null;
            if (Math.Max(w, h) > config.MaxImageSide) { error = "picture is " + w + "x" + h + ", over MaxImageSide " + config.MaxImageSide; return null; }
            if (png.Length > config.MaxPngBytes) { error = "picture is " + png.Length + " bytes, over MaxPngBytes " + config.MaxPngBytes; return null; }
            return new Picture { Png = png, W = w, H = h, Key = key };
        }

        // Where in the sign's paint space the picture goes: fitted inside the face, or stretched over it.
        private static float[] PlaceOnFace(FaceRect f, int w, int h, string fit)
        {
            if (fit == "fill") return new[] { f.X, f.Y, f.W, f.H };
            float scale = Math.Min(f.W / w, f.H / h);
            float pw = w * scale, ph = h * scale;
            return new[] { f.X + (f.W - pw) / 2f, f.Y + (f.H - ph) / 2f, pw, ph };
        }

        private bool Draw(SignRec s, Entity e, bool force, out string error)
        {
            error = null;
            if (!config.Enabled) { error = "painting is switched off (config Enabled)"; return false; }
            Picture pic = Compose(s, out error);
            if (pic == null) { s.LastError = error; return false; }
            uint crc = Png.Crc(pic.Png, 0, pic.Png.Length);
            if (!force && crc == s.LastCrc && s.LastDrawn != DateTime.MinValue) { s.LastKey = pic.Key; return true; }   // same picture: nothing to send
            if (s.Face == null) s.Face = FaceFor(e, s.Name, out s.FaceFrom);
            float[] r = PlaceOnFace(s.Face, pic.W, pic.H, s.Fit ?? config.Fit);
            if (!WriteToSign(e, pic.Png, pic.W, pic.H, r, out error)) { s.LastError = error; return false; }
            s.LastCrc = crc;
            s.LastKey = pic.Key;
            s.LastDrawn = clock();
            s.Draws++;
            s.LastError = null;
            sent.Add(new KeyValuePair<DateTime, int>(s.LastDrawn, pic.Png.Length));
            dirty = true;
            return true;
        }

        #endregion

        #region Redraws and rate limits

        private void Prune(DateTime now)
        {
            sent.RemoveAll(delegate(KeyValuePair<DateTime, int> kv) { return (now - kv.Key).TotalSeconds >= 60; });
        }

        private bool BudgetLeft(DateTime now, out string why)
        {
            Prune(now);
            int bytes = 0;
            foreach (KeyValuePair<DateTime, int> kv in sent) bytes += kv.Value;
            why = null;
            if (sent.Count >= config.MaxRedrawsPerMinute) why = sent.Count + "/" + config.MaxRedrawsPerMinute + " redraws this minute";
            else if (bytes >= config.MaxBytesPerMinute) why = bytes + "/" + config.MaxBytesPerMinute + " bytes this minute";
            return why == null;
        }

        private void Enqueue(SignRec s, bool force)
        {
            if (force) forced.Add(s.Id);
            if (!queue.Contains(s.Id)) { if (force) queue.Insert(0, s.Id); else queue.Add(s.Id); }
        }

        // Live boards whose content changed go into the queue.
        private void Refresh(DateTime now)
        {
            if (art == null || !config.LiveBoards) return;
            var keys = new Dictionary<string, string>();
            foreach (SignRec s in data.Signs)
            {
                if (s.Missing || !IsBoard(s.Board) || !BoardEnabled(s.Board)) continue;
                string k = s.Board + "\n" + s.Arg;
                string key;
                if (!keys.TryGetValue(k, out key)) keys[k] = key = Gather(s.Board, s.Arg).Key;
                if (FullKey(s, key) != s.LastKey) Enqueue(s, false);
            }
        }

        // Draws queued signs while the server's budget lasts; a sign drawn too recently waits its turn.
        private int Drain(DateTime now)
        {
            int drawn = 0;
            for (int i = 0; i < queue.Count; )
            {
                string why;
                if (!BudgetLeft(now, out why)) break;
                SignRec s = FindSign(queue[i]);
                if (s == null || s.Missing) { forced.Remove(queue[i]); queue.RemoveAt(i); continue; }
                bool force = forced.Contains(s.Id);
                if (!force && s.LastDrawn != DateTime.MinValue && (now - s.LastDrawn).TotalSeconds < config.MinSecondsBetweenRedraws) { i++; continue; }
                queue.RemoveAt(i);
                forced.Remove(s.Id);
                Entity e = Locate(s);
                if (e == null) { s.Missing = true; s.LastError = "sign not found"; dirty = true; continue; }
                string err;
                int before = s.Draws;
                if (!Draw(s, e, force, out err)) PrintWarning("Sign " + s.Id + " (" + s.Board + "): " + err);
                else if (s.Draws > before) drawn++;              // an unchanged picture is not sent again
            }
            return drawn;
        }

        private void Tick()
        {
            if (data == null) return;
            DateTime now = clock();
            if (config.Enabled && !loadFailed)
            {
                if (now >= nextRefresh)
                {
                    nextRefresh = now.AddSeconds(config.RefreshSeconds);
                    Refresh(now);
                }
                Drain(now);
            }
            if (dirty) SaveData();
        }

        private void SafeTick()
        {
            try { Tick(); }
            catch (Exception ex) { PrintError("Tick failed: " + ex); }
        }

        #endregion

        #region Game

        // Everything that touches the game's world is here. [CODE] marks names read from the game's DLL metadata.

        private static float Dist2(UnityEngine.Vector3 a, UnityEngine.Vector3 b)
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static List<Entity> Paintables()
        {
            var list = new List<Entity>();
            List<Entity> all = Entity.TryGetAll();                                   // [CODE]
            if (all == null) return list;
            foreach (Entity e in all)
                if (!ReferenceEquals(e, null) && e.TryGet<PaintableObject>() != null) list.Add(e);   // [CODE]
            return list;
        }

        // The sign a player means: the one their look ray hits, else the one nearest their aim, else the nearest
        // within NearRadius.
        private Entity TargetSign(Player player, out string how)
        {
            how = null;
            if (player == null || ReferenceEquals(player.Entity, null)) return null;
            Entity hit = null;
            try { hit = rayProbe != null ? rayProbe(player) : RaycastSign(player); }
            catch (Exception) { hit = null; }
            if (!ReferenceEquals(hit, null) && hit.TryGet<PaintableObject>() != null) { how = "look"; return hit; }

            UnityEngine.Vector3 eye = player.Entity.Position;                           // [CODE]
            eye.y += config.EyeHeight;
            UnityEngine.Vector3 fwd = LookOf(player);
            float fl = (float)Math.Sqrt(fwd.x * fwd.x + fwd.y * fwd.y + fwd.z * fwd.z);
            Entity best = null, nearest = null;
            double bestAngle = config.AimDegrees, nearestD = config.NearRadius * config.NearRadius;
            foreach (Entity e in Paintables())
            {
                UnityEngine.Vector3 p = e.Position;
                float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
                float d = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > config.MaxReach) continue;
                UnityEngine.Vector3 feet = player.Entity.Position;
                float nd = Dist2(feet, p);
                if (nd <= nearestD) { nearestD = nd; nearest = e; }
                if (fl < 0.001f || d < 0.001f) continue;
                double cos = (dx * fwd.x + dy * fwd.y + dz * fwd.z) / (d * fl);
                double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
                // a sign's position is its pivot, often below or behind its face: allow more slack up close
                double slack = Math.Atan2(0.75, Math.Max(d, 0.5)) * 180.0 / Math.PI;
                if (angle - slack < bestAngle) { bestAngle = angle - slack; best = e; }
            }
            if (best != null) { how = "aim"; return best; }
            if (nearest != null) { how = "near"; return nearest; }
            return null;
        }

        private static UnityEngine.Vector3 LookOf(Player player)
        {
            LookBridge look = player.Entity.TryGet<LookBridge>();                      // [CODE] synced look rotation
            if (look != null) return look.Forward;
            return player.Entity.Forward;                                            // [CODE] body facing
        }

        // The sign recorded in the registry: by its network view id, else a paintable object within half a metre
        // of where it was bound (in case ids change across a restart; UNVERIFIED whether they do).
        private Entity Locate(SignRec s)
        {
            Entity e = Entity.TryGetFromViewID(s.ViewId);                            // [CODE]
            if (!ReferenceEquals(e, null) && e.TryGet<PaintableObject>() != null) return e;
            var at = new UnityEngine.Vector3();
            at.x = s.X; at.y = s.Y; at.z = s.Z;
            Entity best = null;
            float bestD = 0.25f;
            foreach (Entity p in Paintables())
            {
                float d = Dist2(p.Position, at);
                if (d <= bestD) { bestD = d; best = p; }
            }
            if (best != null && best.NetViewID != s.ViewId) { s.ViewId = best.NetViewID; dirty = true; }
            return best;
        }

        private static string NameOf(Entity e)
        {
            try
            {
                PropertyInfo p = e.GetType().GetProperty("name");                     // UnityEngine.Object.name
                string n = p != null ? p.GetValue(e, null) as string : null;
                if (string.IsNullOrEmpty(n)) return "sign";
                return n.Replace("(Clone)", "").Trim();
            }
            catch (Exception)
            {
                return "sign";
            }
        }

        // Face of a sign: calibrated or set for its kind (FacesByName), else what the colliders say, else DefaultFace.
        private FaceRect FaceFor(Entity e, string name, out string from)
        {
            FaceRect f;
            if (name != null && config.FacesByName.TryGetValue(name, out f) && GoodFace(f)) { from = "name"; return Copy(f); }
            float[] r = null;
            try
            {
                PaintableObject po = e.TryGet<PaintableObject>();
                r = po == null ? null : faceProbe != null ? faceProbe(po) : ProbeFace(po);
            }
            catch (Exception)
            {
                r = null;
            }
            if (r != null && r.Length == 4)
            {
                var p = new FaceRect { X = r[0], Y = r[1], W = r[2], H = r[3] };
                if (GoodFace(p)) { from = "probe"; return p; }
            }
            from = "default";
            return Copy(config.DefaultFace);
        }

        private static FaceRect Copy(FaceRect f)
        {
            return new FaceRect { X = f.X, Y = f.Y, W = f.W, H = f.H };
        }

        // Writes the PNG into the sign's colour area over rect (paint space) and sends it to every client.
        private bool WriteToSign(Entity e, byte[] png, int w, int h, float[] rect, out string error)
        {
            error = null;
            PaintableObject po = e.TryGet<PaintableObject>();                        // [CODE]
            if (po == null) { error = "that is not a paintable object"; return false; }
            selfPainting = true;
            try
            {
                PaintArea color = po.ColorArea;                                      // [CODE]
                SetAreaBounds(color, rect[0], rect[1], rect[2], rect[3]);
                color.TextureWidth = w;                                              // [CODE] used when a brush paints next
                color.TextureHeight = h;
                color.PaintData = png;                                               // [CODE] StaticTexture.LoadImage
                if (config.ClearRelief)
                {
                    PaintArea depth = po.DepthNormalArea;                            // [CODE]
                    SetAreaBounds(depth, 0f, 0f, TexelWorldSize, TexelWorldSize);    // a fresh area's Bounds [CODE]
                    depth.TextureWidth = 1;
                    depth.TextureHeight = 1;
                    depth.PaintData = BlankPng;
                }
                if (config.SendMode == "apply") po.ApplyPainting(true);              // [CODE] also SetMaterialProperties
                else EventManager.CallEvent(new PaintObjectUpdateEvent(e, po));      // [CODE] what ApplyPainting raises
                return true;
            }
            catch (Exception ex)
            {
                error = "the game refused the picture: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                selfPainting = false;
            }
        }

        private static byte[] blankPng;

        private static byte[] BlankPng
        {
            get
            {
                if (blankPng == null) blankPng = Png.Encode(new Img(1, 1), "stored");   // 1x1 transparent, like a fresh area
                return blankPng;
            }
        }

        // Bounds2 { Vector2 min; Vector2 size } [CODE], set by reflection (no Unity stub for Vector2 in the compile check).
        private static void SetAreaBounds(PaintArea area, float x, float y, float w, float h)
        {
            FieldInfo bf = area.GetType().GetField("Bounds", BindingFlags.Public | BindingFlags.Instance);
            object b = bf.GetValue(area);
            FieldInfo minF = b.GetType().GetField("min"), sizeF = b.GetType().GetField("size");
            object min = minF.GetValue(b), size = sizeF.GetValue(b);
            SetXY(min, x, y);
            SetXY(size, w, h);
            minF.SetValue(b, min);
            sizeF.SetValue(b, size);
            bf.SetValue(area, b);
        }

        private static float[] GetAreaBounds(PaintArea area)
        {
            object b = area.GetType().GetField("Bounds", BindingFlags.Public | BindingFlags.Instance).GetValue(area);
            object min = b.GetType().GetField("min").GetValue(b), size = b.GetType().GetField("size").GetValue(b);
            return new[] { GetF(min, "x"), GetF(min, "y"), GetF(size, "x"), GetF(size, "y") };
        }

        private static void SetXY(object v, float x, float y)
        {
            v.GetType().GetField("x").SetValue(v, x);
            v.GetType().GetField("y").SetValue(v, y);
        }

        private static float GetF(object o, string field)
        {
            FieldInfo f = o.GetType().GetField(field);
            return f != null ? Convert.ToSingle(f.GetValue(o), CultureInfo.InvariantCulture) : 0f;
        }

        private static object Prop(object o, string name)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p != null ? p.GetValue(o, null) : null;
        }

        private static object Call(object o, string name, Type[] sig, object[] args)
        {
            if (o == null) return null;
            MethodInfo m = o.GetType().GetMethod(name, sig);
            return m != null ? m.Invoke(o, args) : null;
        }

        private static Type UnityType(string name)
        {
            Type t = typeof(UnityEngine.Vector3).Assembly.GetType(name);
            if (t == null) t = Type.GetType(name + ", UnityEngine.PhysicsModule");
            return t;
        }

        private static UnityEngine.Vector3 V(float x, float y, float z)
        {
            var v = new UnityEngine.Vector3();
            v.x = x; v.y = y; v.z = z;
            return v;
        }

        // The paint-space rectangle the sign's colliders cover (UNVERIFIED: the colliders may include a post or
        // frame; /paint face calibrate measures the real face). Box colliders are exact; others use their world
        // bounds.
        private static float[] ProbeFace(PaintableObject po)
        {
            object space = Prop(po, "PaintingSpaceTransform");                       // [CODE] Transform
            object go = Prop(po, "gameObject");
            Type colliderType = UnityType("UnityEngine.Collider"), boxType = UnityType("UnityEngine.BoxCollider");
            if (space == null || go == null || colliderType == null) return null;
            var cols = Call(go, "GetComponentsInChildren", new[] { typeof(Type) }, new object[] { colliderType }) as Array;
            if (cols == null) return null;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            Type v3 = typeof(UnityEngine.Vector3);
            foreach (object col in cols)
            {
                if (col == null || true.Equals(Prop(col, "isTrigger"))) continue;
                var corners = new List<UnityEngine.Vector3>();
                if (boxType != null && boxType.IsInstanceOfType(col))
                {
                    var c = (UnityEngine.Vector3)Prop(col, "center");
                    var s = (UnityEngine.Vector3)Prop(col, "size");
                    object tr = Prop(col, "transform");
                    for (int k = 0; k < 8; k++)
                    {
                        UnityEngine.Vector3 local = V(c.x + ((k & 1) == 0 ? -s.x : s.x) / 2f, c.y + ((k & 2) == 0 ? -s.y : s.y) / 2f, c.z + ((k & 4) == 0 ? -s.z : s.z) / 2f);
                        corners.Add((UnityEngine.Vector3)Call(tr, "TransformPoint", new[] { v3 }, new object[] { local }));
                    }
                }
                else
                {
                    object bounds = Prop(col, "bounds");
                    var mn = (UnityEngine.Vector3)Prop(bounds, "min");
                    var mx = (UnityEngine.Vector3)Prop(bounds, "max");
                    for (int k = 0; k < 8; k++) corners.Add(V((k & 1) == 0 ? mn.x : mx.x, (k & 2) == 0 ? mn.y : mx.y, (k & 4) == 0 ? mn.z : mx.z));
                }
                foreach (UnityEngine.Vector3 w in corners)
                {
                    var p = (UnityEngine.Vector3)Call(space, "InverseTransformPoint", new[] { v3 }, new object[] { w });
                    minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                    minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
                }
            }
            if (minX > maxX) return null;
            return new[] { minX, minY, maxX - minX, maxY - minY };
        }

        // Physics.RaycastAll from the player's eye along their look; the nearest hit whose entity is paintable.
        private Entity RaycastSign(Player player)
        {
            Type physics = UnityType("UnityEngine.Physics");
            if (physics == null) return null;
            Type v3 = typeof(UnityEngine.Vector3);
            MethodInfo m = physics.GetMethod("RaycastAll", new[] { v3, v3, typeof(float) });
            if (m == null) return null;
            UnityEngine.Vector3 eye = player.Entity.Position;
            eye.y += config.EyeHeight;
            var hits = m.Invoke(null, new object[] { eye, LookOf(player), config.MaxReach }) as Array;
            if (hits == null) return null;
            Entity best = null;
            float bestD = float.MaxValue;
            foreach (object h in hits)
            {
                object col = Prop(h, "collider");
                object dist = Prop(h, "distance");
                if (col == null || !(dist is float) || (float)dist >= bestD) continue;
                var e = Call(col, "GetComponentInParent", new[] { typeof(Type) }, new object[] { typeof(Entity) }) as Entity;
                if (ReferenceEquals(e, null) || e.TryGet<PaintableObject>() == null) continue;
                best = e;
                bestD = (float)dist;
            }
            return best;
        }

        // A player painted a sign. If it is bound, put the realm's picture back (the server already took the
        // player's picture in when it read the event, so cancelling alone would not undo it).
        private void OnGamePaint(PaintObjectUpdateEvent e)
        {
            try
            {
                if (selfPainting || e == null || !config.ProtectBoundSigns) return;
                if (e.Sender == null || e.Sender.IsServer) return;
                SignRec s = SignOf(e.Entity);
                if (s == null) return;
                if (IsAdmin(e.Sender)) return;                  // admins paint corners to calibrate a face
                s.LastCrc = 0;
                Enqueue(s, true);
            }
            catch (Exception ex)
            {
                PrintWarning("Paint watch: " + ex.Message);
            }
        }

        #endregion

        #region Chat style

        // Realm chat style, the same block in every Realm plugin (docs/realm-commands.md, "Chat style";
        // tools/realm-integration/check.mjs checks it). A reply opens with its speaker in the colour of its tone:
        // gold for news and answers, green for done, amber for take care, red for refused. A line that starts with
        // a space continues a list and carries no speaker. A text that already opens with a colour tag or with
        // "<speaker>:" (a server's older lang file, or a line with a voice of its own) is sent as it is.
        private const string ChatGold = "D6A043";
        private const string ChatOk = "8FC97A";
        private const string ChatWarn = "E8913A";
        private const string ChatError = "E86A5C";

        private static string StyledLine(string speaker, string tone, string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(speaker) || text[0] == ' ') return text;
            if (text.StartsWith(speaker + ":", StringComparison.OrdinalIgnoreCase)) return text;
            if (text.Length >= 8 && text[0] == '[' && text[7] == ']' && IsChatHex(text.Substring(1, 6))) return text;
            return "[" + tone + "]" + speaker + "[FFFFFF]: " + text;
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            var m = new Dictionary<string, string>
            {
                { "Speaker", "Painter" },
                { "Usage", "[F4C96D]/paint[FFFFFF] <artwork> paints the sign you look at. [F4C96D]/paint list[FFFFFF] shows the artworks and live boards." },
                { "Usage2", "  [F4C96D]/paint info[FFFFFF] | [F4C96D]/paint nearby[FFFFFF] | [F4C96D]/paint signs[FFFFFF] | [F4C96D]/paint redraw[FFFFFF] [id|all] | [F4C96D]/paint unbind[FFFFFF] [id] | [F4C96D]/paint clear[FFFFFF] [id]" },
                { "Usage3", "  [F4C96D]/paint face[FFFFFF] [calibrate|auto|save|w h] | [F4C96D]/paint fit[FFFFFF] fit|fill | [F4C96D]/paint notice[FFFFFF] title | text | [F4C96D]/paint status[FFFFFF] | [F4C96D]/paint reload[FFFFFF]" },
                { "NoPermission", "Only the realm's painters may do that." },
                { "LoadFailed", "oxide/data/RealmPainter.json could not be read, so nothing is changed until it is fixed. See the server log." },
                { "NoArt", "No art bundle: {0}." },
                { "Off", "Painting is switched off in the config (Enabled)." },
                { "NoSign", "No paintable sign in front of you. Look at one within {0} m, or stand within {1} m of it." },
                { "NotBound", "That {0} is not bound to anything." },
                { "UnknownSign", "No sign {0}. [F4C96D]/paint signs[FFFFFF] lists them." },
                { "Unknown", "No artwork or board called '{0}'. [F4C96D]/paint list[FFFFFF] shows them." },
                { "UnknownMaybe", "No artwork or board called '{0}'. Did you mean {1}?" },
                { "TooMany", "The registry is full ({0} signs, MaxSigns). Unbind one first." },
                { "Bound", "Sign {0} ({1}, {2}) now shows {3}." },
                { "BoundFace", "  Face {0} x {1} m ({2}). If the picture sits wrong, [F4C96D]/paint face[FFFFFF] explains how to measure it." },
                { "DrawFailed", "Sign {0} is bound but could not be drawn: {1}" },
                { "Unbound", "Sign {0} is free again. Its picture stays until someone paints over it." },
                { "Cleared", "Sign {0} is blank and free again." },
                { "Forgotten", "Sign {0} is gone from the registry." },
                { "Redrawn", "Redrew {0} sign(s); {1} waiting in the queue." },
                { "Queued", "Sign {0} is queued: the server's redraw budget is used up ({1}). It is drawn within a minute." },
                { "ListPaintings", "Artworks ({0}): {1}" },
                { "ListBoards", "Live boards: {0}. Wanted takes a number, event a kind (crown_night, tournament, kings_hunt, truce)." },
                { "ListNone", "No artworks: the art bundle is not loaded ({0})." },
                { "Info", "Sign {0}: {1} at {2} m, shows {3}{4}. Face {5} x {6} m ({7}), {8}. Drawn {9} time(s){10}." },
                { "InfoFree", "This {0} is {1} m away and not bound. Its paint area is {2} x {3} m. [F4C96D]/paint[FFFFFF] <artwork> binds it." },
                { "SignsHeader", "{0} bound sign(s):" },
                { "NearbyHeader", "{0} paintable object(s) within 20 m, nearest first:" },
                { "NearbyLine", "  {0}  {1} m  face {2} x {3} m (the game's brush: {4} x {5} px)  {6}" },
                { "NearbyNone", "No paintable object within 20 m." },
                { "SignsLine", "  {0}  {1}{2}  {3}  {4}" },
                { "SignsNone", "No signs are bound yet. Look at a sign and type [F4C96D]/paint[FFFFFF] <artwork>." },
                { "FaceShow", "Sign {0}: face {1} x {2} m at ({3}, {4}), from {5}." },
                { "FaceHelp", "  Measure it: paint a dot in each corner with the game's brush, then [F4C96D]/paint face calibrate[FFFFFF]. Or [F4C96D]/paint face[FFFFFF] <width> <height> in metres." },
                { "FaceSet", "Sign {0}: face is now {1} x {2} m ({3}); redrawn." },
                { "FaceSaved", "Every {0} now uses a face of {1} x {2} m (config FacesByName)." },
                { "FaceBad", "That face is not usable ({0} x {1} m). Paint the four corners first, or give a width and height between 0.05 and 20 m." },
                { "FitSet", "Sign {0} now uses '{1}'; redrawn." },
                { "NoticeUsage", "Usage: [F4C96D]/paint notice[FFFFFF] <title> | <text>" },
                { "Status", "Painter {0}: {1} signs ({2} live, {3} missing), queue {4}, {5} redraws and {6} bytes in the last minute (caps {7} and {8})." },
                { "Status2", "  Art {0}: {1} artworks, {2} fonts. Compression {3}, max {4} px / {5} bytes. Sources: {6}." },
                { "Reloaded", "Art bundle reloaded: {0}." },
                // Text on the live boards. Colour tags draw in the board's accent colour; keep lines short.
                { "Board.KickerRealm", "The Realm of Ostreval" },
                { "Board.KickerCrown", "By order of the crown" },
                { "Board.KickerCourt", "By judgement of the court" },
                { "Board.Unavailable.Title", "Notice" },
                { "Board.Unavailable.Body", "This board could not be read just now. The painter will try again." },
                { "Board.Chronicle.Title", "The Chronicle" },
                { "Board.Chronicle.Footer", "Read it all with [F4C96D]/chronicle[FFFFFF]" },
                { "Board.Chronicle.Missing", "The chronicler is away. Nothing is written here today." },
                { "Board.Chronicle.Empty", "Nothing has happened yet. The first deed of the realm will be written here." },
                { "Board.Wanted.Title", "Wanted" },
                { "Board.Wanted.Footer", "Bounties: [F4C96D]/contract[FFFFFF]" },
                { "Board.Wanted.Missing", "No outlaw roll is kept in this realm." },
                { "Board.Wanted.NoneTitle", "The Roads Are Quiet" },
                { "Board.Wanted.None", "No outlaw is proclaimed. Should one be, their name goes up here." },
                { "Board.Wanted.NoneN", "Only {1} outlaw(s) are proclaimed, so poster number {0} stays bare." },
                { "Board.Wanted.NoHouse", "sworn to no house" },
                { "Board.Wanted.OfHouse", "of House {0}" },
                { "Board.Wanted.Bounty0", "No bounty yet." },
                { "Board.Wanted.Bounty1", "One bounty on this head." },
                { "Board.Wanted.BountyN", "{0} bounties on this head." },
                { "Board.Wanted.Until", "Outlaw until {0}." },
                { "Board.Standings.Title", "The Season" },
                { "Board.Standings.Kicker", "Season {0} standings" },
                { "Board.Standings.Footer", "See your house with [F4C96D]/season[FFFFFF]" },
                { "Board.Standings.Missing", "No season is kept in this realm." },
                { "Board.Standings.None", "No season is under way. When one begins, the houses are ranked here." },
                { "Board.Standings.Empty", "No house has scored yet." },
                { "Board.Standings.Row", "{0}. {1}" },
                { "Board.Standings.Crown1", "one day on the Old Throne" },
                { "Board.Standings.CrownN", "{0} days on the Old Throne" },
                { "Board.Proclamation.Kicker", "Hear the crown" },
                { "Board.Proclamation.Footer", "The crown speaks: [F4C96D]/crown[FFFFFF]" },
                { "Board.Proclamation.Missing", "No crown is kept in this realm." },
                { "Board.Proclamation.EmptyTitle", "The Old Throne Stands Empty" },
                { "Board.Proclamation.Empty", "Any house may take it. A house with a claim knows how: [F4C96D]/claim[FFFFFF]" },
                { "Board.Proclamation.OfHouse", "of House {0}" },
                { "Board.Proclamation.Since", "Reigning since {0}" },
                { "Board.Proclamation.Decree", "Latest decree: {0}" },
                { "Board.Proclamation.NoDecree", "No decree has been proclaimed in this reign." },
                { "Board.Event.NoneTitle", "No Event Is Called" },
                { "Board.Event.None", "The heralds will post the next one here." },
                { "Board.Event.Missing", "No events are kept in this realm." },
                { "Board.Event.Running", "Under way now, until {0}" },
                { "Board.Event.Next", "Next: {0}" },
                { "Board.Event.Watch", "The heralds will post its date." },
                { "Board.Event.Title.crown_night", "Crown Night" },
                { "Board.Event.Title.tournament", "The Royal Tournament" },
                { "Board.Event.Title.kings_hunt", "The King's Hunt" },
                { "Board.Event.Title.truce", "The Truce of the Realm" },
                { "Board.Event.Line.crown_night", "The night the Old Throne is fought for. A house with a claim may take it." },
                { "Board.Event.Line.tournament", "Steel against steel for the glory of your house." },
                { "Board.Event.Line.kings_hunt", "The crown names its quarry. Whoever takes them is rewarded." },
                { "Board.Event.Line.truce", "No blade is drawn. Breaking the truce shames your house." },
                { "Board.Event.Footer.crown_night", "Ask the crown: [F4C96D]/crown[FFFFFF]" },
                { "Board.Event.Footer.tournament", "Enter with [F4C96D]/tourney[FFFFFF]" },
                { "Board.Event.Footer.kings_hunt", "See the quarry with [F4C96D]/hunt[FFFFFF]" },
                { "Board.Event.Footer.truce", "Does it hold? [F4C96D]/truce[FFFFFF]" },
                { "Board.Ironbreaker.Kicker", "A legend of the realm" },
                { "Board.Ironbreaker.Title", "Ironbreaker" },
                { "Board.Ironbreaker.Unclaimed", "Unclaimed" },
                { "Board.Ironbreaker.UnclaimedLine", "Whoever takes it up will be known to every house." },
                { "Board.Ironbreaker.Bearer", "Borne by {0}" },
                { "Board.Ironbreaker.BearerLine", "Whoever bears it is known to every house." }
            };
            lang.RegisterMessages(m, this);
        }

        private string Msg(string key, Player player, params object[] args)
        {
            string text = lang.GetMessage(key, this, player != null ? player.Id.ToString() : null);
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); }
            catch (FormatException) { return text; }                  // a server's reworded line with a stray brace
        }

        // Tone of a reply (chat style): done, or take care; everything else is news.
        private static readonly HashSet<string> OkKeys = new HashSet<string>
        {
            "Bound", "Unbound", "Cleared", "Forgotten", "Redrawn", "FaceSet", "FaceSaved", "FitSet", "Reloaded"
        };
        private static readonly HashSet<string> WarnKeys = new HashSet<string> { "Queued", "DrawFailed" };

        private void Reply(Player player, string key, params object[] args)
        {
            string tone = OkKeys.Contains(key) ? ChatOk : WarnKeys.Contains(key) ? ChatWarn : ChatGold;
            player.SendMessage(StyledLine(Msg("Speaker", player), tone, Msg(key, player, args)));   // single-string overload: brace safe
        }

        private void Error(Player player, string key, params object[] args)
        {
            player.SendError(StyledLine(Msg("Speaker", player), ChatError, Msg(key, player, args)));
        }

        private void Line(Player player, string key, params object[] args)
        {
            player.SendMessage(Msg(key, player, args));
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            try { config = Config.ReadObject<PluginConfig>(); }
            catch (Exception ex)
            {
                PrintWarning("Could not read the config (" + ex.Message + "); using the defaults for this run.");
                config = null;
            }
            if (config == null) config = new PluginConfig();
            ClampConfig();
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
            LoadArt();
            if (art == null) PrintWarning("No art: " + artError + ". Bound signs keep their pictures; nothing new is painted.");
        }

        private void OnServerInitialized()
        {
            // Re-sent on hot reload, so keep this idempotent.
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            tickTimer = timer.Every(config.TickSeconds, SafeTick);
            nextRefresh = clock().AddSeconds(Math.Min(30, config.RefreshSeconds));
            if (paintSubscriber == null)
            {
                try
                {
                    paintSubscriber = new EventSubscriber<PaintObjectUpdateEvent>(OnGamePaint);
                    EventManager.Subscribe<PaintObjectUpdateEvent>(paintSubscriber, EventHandlerOrder.Late);   // [CODE]
                }
                catch (Exception ex)
                {
                    PrintWarning("Could not watch sign painting (" + ex.Message + "); bound signs are not protected.");
                    paintSubscriber = null;
                }
            }
        }

        private void OnServerSave()
        {
            if (dirty) SaveData();
        }

        private void Unload()
        {
            if (tickTimer != null && !tickTimer.Destroyed) tickTimer.Destroy();
            if (paintSubscriber != null)
            {
                try { EventManager.Unsubscribe<PaintObjectUpdateEvent>(paintSubscriber); }
                catch (Exception ex) { PrintWarning("Unsubscribe failed: " + ex.Message); }
                paintSubscriber = null;
            }
            if (dirty) SaveData();
        }

        #endregion

        #region Commands

        private bool IsAdmin(Player player)
        {
            return player != null && permission.UserHasPermission(player.Id.ToString(), PermAdmin);
        }

        [ChatCommand("paint")]
        private void CmdPaint(Player player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) { Error(player, "NoPermission"); return; }
            if (args == null) args = new string[0];
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "":
                case "help":
                    Reply(player, "Usage"); Line(player, "Usage2"); Line(player, "Usage3");
                    return;
                case "list": CmdList(player); return;
                case "status": CmdStatus(player); return;
                case "signs": CmdSigns(player); return;
                case "info": CmdInfo(player); return;
                case "nearby": CmdNearby(player); return;
                case "reload":
                    LoadArt();
                    if (art == null) { Error(player, "NoArt", artError); return; }
                    foreach (SignRec s in data.Signs) s.LastKey = null;
                    nextRefresh = clock();
                    Reply(player, "Reloaded", art.Version);
                    return;
            }
            if (loadFailed) { Error(player, "LoadFailed"); return; }
            switch (sub)
            {
                case "unbind": CmdUnbind(player, args.Length > 1 ? args[1] : null, false); return;
                case "clear": CmdUnbind(player, args.Length > 1 ? args[1] : null, true); return;
                case "forget": CmdForget(player, args.Length > 1 ? args[1] : null); return;
                case "redraw": CmdRedraw(player, args.Length > 1 ? args[1].ToLowerInvariant() : null); return;
                case "face": CmdFace(player, args); return;
                case "fit": CmdFit(player, args.Length > 1 ? args[1].ToLowerInvariant() : null); return;
            }
            if (!config.Enabled) { Error(player, "Off"); return; }
            if (art == null) { Error(player, "NoArt", artError); return; }
            string name = sub, arg = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : null;
            if (name == "notice")
            {
                if (string.IsNullOrEmpty(arg) || arg.Trim().Length == 0) { Error(player, "NoticeUsage"); return; }
                arg = Clean(arg, config.MaxNoticeLength + 42);
            }
            else if (IsBoard(name))
            {
                if (name == "event" && arg != null) arg = arg.ToLowerInvariant();
            }
            else
            {
                string id = ResolveArtwork(name);
                if (id == null)
                {
                    string maybe = Suggest(name);
                    if (maybe != null) Error(player, "UnknownMaybe", name, maybe); else Error(player, "Unknown", name);
                    return;
                }
                name = id;
                arg = null;
            }
            Bind(player, name, arg);
        }

        private string ResolveArtwork(string name)
        {
            ArtItem item;
            if (art.Items.TryGetValue(name, out item) && item.Kind == "painting") return item.Id;
            return null;
        }

        private string Suggest(string name)
        {
            var hits = new List<string>();
            foreach (string id in art.Paintings) if (id.Contains(name)) hits.Add(id);
            foreach (string b in Boards) if (b.StartsWith(name)) hits.Add(b);
            if (hits.Count == 0) return null;
            if (hits.Count > 4) hits.RemoveRange(4, hits.Count - 4);
            return string.Join(", ", hits.ToArray());
        }

        private void Bind(Player player, string board, string arg)
        {
            string how;
            Entity e = TargetSign(player, out how);
            if (e == null) { Error(player, "NoSign", config.MaxReach, config.NearRadius); return; }
            SignRec s = SignOf(e);
            if (s == null)
            {
                if (data.Signs.Count >= config.MaxSigns) { Error(player, "TooMany", config.MaxSigns); return; }
                s = new SignRec { Id = "s" + data.NextId++, BoundAt = clock() };
                data.Signs.Add(s);
            }
            UnityEngine.Vector3 p = e.Position;
            s.ViewId = e.NetViewID;
            s.X = p.x; s.Y = p.y; s.Z = p.z;
            s.Name = NameOf(e);
            s.Board = board;
            s.Arg = arg;
            s.BoundBy = player.Name;
            s.Missing = false;
            s.LastKey = null;
            if (s.Face == null) s.Face = FaceFor(e, s.Name, out s.FaceFrom);
            dirty = true;
            queue.Remove(s.Id);
            string err;
            if (!Draw(s, e, true, out err)) { Error(player, "DrawFailed", s.Id, err); SaveData(); return; }
            float d = (float)Math.Sqrt(Dist2(e.Position, player.Entity.Position));
            Reply(player, "Bound", s.Id, s.Name, Metres(d) + " m, " + how, Describe(s));
            Line(player, "BoundFace", Metres(s.Face.W), Metres(s.Face.H), s.FaceFrom);
            SaveData();
        }

        private string Describe(SignRec s)
        {
            if (!IsBoard(s.Board))
            {
                ArtItem item;
                return art != null && art.Items.TryGetValue(s.Board, out item) && !string.IsNullOrEmpty(item.Title) ? item.Title + " (" + s.Board + ")" : s.Board;
            }
            return "the " + s.Board + " board" + (string.IsNullOrEmpty(s.Arg) ? "" : " (" + (s.Board == "notice" ? Clean(s.Arg, 30) : s.Arg) + ")");
        }

        private static string Metres(float m)
        {
            return m.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // The sign named by id, or the one the player looks at.
        private SignRec PickSign(Player player, string id, out Entity e)
        {
            e = null;
            if (!string.IsNullOrEmpty(id))
            {
                SignRec s = FindSign(id);
                if (s == null) { Error(player, "UnknownSign", id); return null; }
                e = Locate(s);
                return s;
            }
            string how;
            e = TargetSign(player, out how);
            if (e == null) { Error(player, "NoSign", config.MaxReach, config.NearRadius); return null; }
            SignRec r = SignOf(e);
            if (r == null) Error(player, "NotBound", NameOf(e));
            return r;
        }

        private void CmdUnbind(Player player, string id, bool clear)
        {
            Entity e;
            SignRec s = PickSign(player, id, out e);
            if (s == null) return;
            if (clear && e != null)
            {
                string err;
                FaceRect face = s.Face ?? config.DefaultFace;
                if (!WriteToSign(e, BlankPng, 1, 1, new[] { face.X, face.Y, TexelWorldSize, TexelWorldSize }, out err)) { Error(player, "DrawFailed", s.Id, err); return; }
            }
            data.Signs.Remove(s);
            queue.Remove(s.Id);
            forced.Remove(s.Id);
            dirty = true;
            SaveData();
            Reply(player, clear ? "Cleared" : "Unbound", s.Id);
        }

        private void CmdForget(Player player, string id)
        {
            SignRec s = FindSign(id);
            if (s == null) { Error(player, "UnknownSign", id ?? ""); return; }
            data.Signs.Remove(s);
            queue.Remove(s.Id);
            forced.Remove(s.Id);
            dirty = true;
            SaveData();
            Reply(player, "Forgotten", s.Id);
        }

        private void CmdRedraw(Player player, string id)
        {
            var list = new List<SignRec>();
            if (id == "all") list.AddRange(data.Signs);
            else
            {
                Entity e;
                SignRec s = PickSign(player, id, out e);
                if (s == null) return;
                list.Add(s);
            }
            foreach (SignRec s in list) { s.Missing = false; s.LastCrc = 0; Enqueue(s, true); }
            int n = Drain(clock());
            string why;
            if (n == 0 && list.Count == 1 && queue.Contains(list[0].Id) && !BudgetLeft(clock(), out why)) { Reply(player, "Queued", list[0].Id, why); return; }
            if (n == 0 && list.Count == 1 && !string.IsNullOrEmpty(list[0].LastError)) { Error(player, "DrawFailed", list[0].Id, list[0].LastError); return; }
            Reply(player, "Redrawn", n, queue.Count);
            if (dirty) SaveData();
        }

        private void CmdFace(Player player, string[] args)
        {
            string how;
            Entity e = TargetSign(player, out how);
            if (e == null) { Error(player, "NoSign", config.MaxReach, config.NearRadius); return; }
            SignRec s = SignOf(e);
            string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            PaintableObject po = e.TryGet<PaintableObject>();
            if (mode == "")
            {
                FaceRect f = s != null ? s.Face : null;
                string from = s != null ? s.FaceFrom : null;
                if (f == null) f = FaceFor(e, NameOf(e), out from);
                Reply(player, "FaceShow", s != null ? s.Id : NameOf(e), Metres(f.W), Metres(f.H), Metres(f.X), Metres(f.Y), from);
                Line(player, "FaceHelp");
                return;
            }
            if (s == null) { Error(player, "NotBound", NameOf(e)); return; }
            if (mode == "save")
            {
                if (s.Face == null || string.IsNullOrEmpty(s.Name)) return;
                config.FacesByName[s.Name] = Copy(s.Face);
                Config.WriteObject(config, true);
                Reply(player, "FaceSaved", s.Name, Metres(s.Face.W), Metres(s.Face.H));
                return;
            }
            FaceRect nf;
            string nfrom;
            if (mode == "calibrate")
            {
                // The game's own Bounds after someone painted the corners: the area grows to cover every stroke [CODE].
                float[] r = GetAreaBounds(po.ColorArea);
                nf = new FaceRect { X = r[0], Y = r[1], W = r[2], H = r[3] };
                nfrom = "calibrated";
            }
            else if (mode == "auto")
            {
                nf = FaceFor(e, null, out nfrom);
            }
            else
            {
                float w, h;
                if (args.Length < 3 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                    || !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out h)) { Line(player, "FaceHelp"); return; }
                FaceRect old = s.Face ?? config.DefaultFace;
                nf = new FaceRect { W = w, H = h, X = old.X + (old.W - w) / 2f, Y = old.Y + (old.H - h) / 2f };
                float x, y;
                if (args.Length >= 5 && float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) { nf.X = x; nf.Y = y; }
                nfrom = "manual";
            }
            if (!GoodFace(nf)) { Error(player, "FaceBad", Metres(nf.W), Metres(nf.H)); return; }
            s.Face = nf;
            s.FaceFrom = nfrom;
            dirty = true;
            string err;
            if (!Draw(s, e, true, out err)) { Error(player, "DrawFailed", s.Id, err); SaveData(); return; }
            SaveData();
            Reply(player, "FaceSet", s.Id, Metres(nf.W), Metres(nf.H), nfrom);
        }

        private void CmdFit(Player player, string mode)
        {
            if (mode != "fit" && mode != "fill") { Reply(player, "Usage"); return; }
            Entity e;
            SignRec s = PickSign(player, null, out e);
            if (s == null || e == null) return;
            s.Fit = mode;
            dirty = true;
            string err;
            if (!Draw(s, e, true, out err)) { Error(player, "DrawFailed", s.Id, err); SaveData(); return; }
            SaveData();
            Reply(player, "FitSet", s.Id, mode);
        }

        private void CmdList(Player player)
        {
            if (art == null) { Reply(player, "ListNone", artError); return; }
            Reply(player, "ListPaintings", art.Paintings.Count, string.Join(", ", art.Paintings.ToArray()));
            var live = new List<string>();
            foreach (string b in Boards) if (BoardEnabled(b)) live.Add(b);
            Line(player, "ListBoards", string.Join(", ", live.ToArray()));
        }

        private void CmdInfo(Player player)
        {
            string how;
            Entity e = TargetSign(player, out how);
            if (e == null) { Error(player, "NoSign", config.MaxReach, config.NearRadius); return; }
            float d = (float)Math.Sqrt(Dist2(e.Position, player.Entity.Position));
            SignRec s = SignOf(e);
            if (s == null)
            {
                float[] r = GetAreaBounds(e.TryGet<PaintableObject>().ColorArea);
                Reply(player, "InfoFree", NameOf(e), Metres(d), Metres(r[2]), Metres(r[3]));
                return;
            }
            string when = s.LastDrawn == DateTime.MinValue ? "never" : ((int)(clock() - s.LastDrawn).TotalMinutes) + " min ago";
            Reply(player, "Info", s.Id, s.Name, Metres(d), Describe(s), s.Missing ? " (missing)" : "",
                Metres(s.Face != null ? s.Face.W : 0), Metres(s.Face != null ? s.Face.H : 0), s.FaceFrom, s.Fit ?? config.Fit, s.Draws,
                ", last " + when + (string.IsNullOrEmpty(s.LastError) ? "" : ", last error: " + s.LastError));
        }

        // Every paintable object within 20 m: which placeables are paintable is prefab data, not code, so this is how
        // the first test finds out (plugins/docs/RealmPainter.md).
        private void CmdNearby(Player player)
        {
            UnityEngine.Vector3 me = player.Entity.Position;
            var found = new List<KeyValuePair<float, Entity>>();
            foreach (Entity e in Paintables())
            {
                float d = (float)Math.Sqrt(Dist2(e.Position, me));
                if (d <= 20f) found.Add(new KeyValuePair<float, Entity>(d, e));
            }
            if (found.Count == 0) { Reply(player, "NearbyNone"); return; }
            found.Sort(delegate(KeyValuePair<float, Entity> a, KeyValuePair<float, Entity> b) { return a.Key.CompareTo(b.Key); });
            Reply(player, "NearbyHeader", found.Count);
            for (int i = 0; i < found.Count && i < 8; i++)
            {
                Entity e = found[i].Value;
                SignRec s = SignOf(e);
                string from;
                FaceRect f = s != null && s.Face != null ? s.Face : FaceFor(e, NameOf(e), out from);
                Line(player, "NearbyLine", NameOf(e), Metres(found[i].Key), Metres(f.W), Metres(f.H),
                    (int)Math.Round(f.W / TexelWorldSize), (int)Math.Round(f.H / TexelWorldSize), s != null ? s.Id + " " + s.Board : "free");
            }
        }

        private void CmdSigns(Player player)
        {
            if (data.Signs.Count == 0) { Reply(player, "SignsNone"); return; }
            Reply(player, "SignsHeader", data.Signs.Count);
            UnityEngine.Vector3 me = player.Entity.Position;
            var at = new UnityEngine.Vector3();
            foreach (SignRec s in data.Signs)
            {
                at.x = s.X; at.y = s.Y; at.z = s.Z;
                Line(player, "SignsLine", s.Id, s.Board, string.IsNullOrEmpty(s.Arg) ? "" : " " + Clean(s.Arg, 20),
                    Metres((float)Math.Sqrt(Dist2(me, at))) + " m", s.Missing ? "missing" : s.LastError ?? "ok");
            }
        }

        private void CmdStatus(Player player)
        {
            DateTime now = clock();
            Prune(now);
            int bytes = 0, live = 0, missing = 0;
            foreach (KeyValuePair<DateTime, int> kv in sent) bytes += kv.Value;
            foreach (SignRec s in data.Signs) { if (IsBoard(s.Board)) live++; if (s.Missing) missing++; }
            var src = new List<string>();
            AddSource(src, "chronicle", RealmChronicle); AddSource(src, "contracts", RealmContracts); AddSource(src, "laws", RealmLaws);
            AddSource(src, "seasons", RealmSeasons); AddSource(src, "crown", CrownAndConsequences); AddSource(src, "events", RealmEvents);
            AddSource(src, "houses", RealmHouses); AddSource(src, "legendary", RealmLegendary);
            Reply(player, "Status", config.Enabled ? "on" : "off", data.Signs.Count, live, missing, queue.Count, sent.Count, bytes, config.MaxRedrawsPerMinute, config.MaxBytesPerMinute);
            Line(player, "Status2", art != null ? art.Version : "missing (" + artError + ")", art != null ? art.Paintings.Count : 0, art != null ? art.Fonts.Count : 0,
                config.Compression, config.MaxImageSide, config.MaxPngBytes, string.Join(" ", src.ToArray()));
        }

        private static void AddSource(List<string> list, string name, Plugin p)
        {
            list.Add(name + "=" + (p != null ? "yes" : "no"));
        }

        #endregion

        #region API (plugin.Call) - non-public on purpose (see header)

        // Asks for the live boards of one kind (or all, kind null) to be looked at again on the next tick, for example
        // right after an outlaw is proclaimed. Redraws still keep to the rate limits.
        private void RefreshBoards(string kind)
        {
            if (data == null) return;
            foreach (SignRec s in data.Signs) if (kind == null || s.Board == kind) s.LastKey = null;
            nextRefresh = clock();
        }

        private int GetBoundSignCount()
        {
            return data != null ? data.Signs.Count : 0;
        }

        #endregion
    }
}
