// Behaviour tests for plugins/RealmPainter.cs against Mocks.cs. Run with run.sh.
// Writes every PNG it makes (and the RGBA it was made from) to the output directory, where decode-check.mjs decodes
// them with the independent Node decoder in art/tools/painter/png.mjs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeHatch;
using CodeHatch.Engine.Core.Cache;
using CodeHatch.Engine.Networking;
using CodeHatch.Networking.Events;
using CodeHatch.Networking.Events.Entities;
using CodeHatch.Thrones.Painting;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins;
using UnityEngine;

static class T
{
    static int pass, fail;
    const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    static DateTime Clock = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
    static string Dir, Out, Repo;
    static RealmPainter P;
    static Player Admin, Peasant;
    static ulong NextView = 1000;
    static Dictionary<string, Plugin> Plugins = new Dictionary<string, Plugin>();

    // What the other plugins report.
    static List<Dictionary<string, object>> Chronicle = new List<Dictionary<string, object>>();
    static Dictionary<string, int> Bounties = new Dictionary<string, int>();
    static Dictionary<string, string> HouseOf = new Dictionary<string, string>();
    static List<string> CourtOutlaws = new List<string>();
    static string King, KingHouse, KingSince, Bearer, SeasonName = "The Hollow Crown";
    static int SeasonNumber = 1;
    static List<Dictionary<string, object>> Standings = new List<Dictionary<string, object>>();
    static List<string> ActiveEvents = new List<string>();
    static Dictionary<string, object> NextEvent;

    static void Ok(bool cond, string name, string extra = "")
    {
        if (cond) { pass++; Console.WriteLine("PASS " + name); }
        else { fail++; Console.WriteLine("FAIL " + name + (extra.Length > 0 ? "\n     " + extra.Replace("\n", "\n     ") : "")); }
    }

    static object Inv(object o, string m, params object[] a)
    {
        Type t = o as Type ?? o.GetType();
        var mi = t.GetMethods(BF).First(x => x.Name == m && x.GetParameters().Length == a.Length);
        try { return mi.Invoke(o is Type ? null : o, a); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static Type Nested(string n) { return typeof(RealmPainter).GetNestedType(n, BF); }
    static object F(object o, string f)
    {
        if (o == null) throw new NullReferenceException("F(null, \"" + f + "\")");
        var fi = o.GetType().GetField(f, BF);
        if (fi == null) { var pi = o.GetType().GetProperty(f, BF); if (pi != null) return pi.GetValue(o); throw new MissingFieldException(o.GetType().Name, f); }
        return fi.GetValue(o);
    }
    static void SetF(object o, string f, object v) { o.GetType().GetField(f, BF).SetValue(o, v); }
    static object Cfg() { return F(P, "config"); }
    static void SetCfg(string f, object v) { SetF(Cfg(), f, v); }
    static IList Signs() { return (IList)F(F(P, "data"), "Signs"); }
    static object Sign(string id) { foreach (object s in Signs()) if ((string)F(s, "Id") == id) return s; return null; }
    static string Plain(string s) { return Regex.Replace(s, @"\[[0-9A-Fa-f]{6}\]", ""); }
    static void Cmd(Player p, params string[] args) { p.Messages.Clear(); Inv(P, "CmdPaint", p, "paint", args); }
    static void Tick() { Inv(P, "SafeTick"); }
    static void Advance(int seconds) { DateTime until = Clock.AddSeconds(seconds); while (Clock < until) { Clock = Clock.AddSeconds(10); Tick(); } }
    static int PaintEvents() { return EventManager.Called.OfType<PaintObjectUpdateEvent>().Count(); }
    static string Iso(DateTime t) { return t.ToString("o"); }

    static Entity MkSign(string name, float x, float y, float z, float w = 1f, float h = 0.75f)
    {
        var po = new PaintableObject();
        var space = new UnityEngine.Transform { Offset = new UnityEngine.Vector3(x, y, z) };
        var go = new GameObject { name = name };
        var e = new Entity { NetViewID = NextView++, Position = new UnityEngine.Vector3(x, y, z), name = name + "(Clone)" };
        po.PaintingSpaceTransform = space;
        po.gameObject = go;
        // the board: a box collider whose face spans w x h in paint space, centred on x, starting at y
        var box = new BoxCollider { center = new UnityEngine.Vector3(0, h / 2f, 0), size = new UnityEngine.Vector3(w, h, 0.05f), transform = space, Parent = e };
        go.Children.Add(box);
        e.Parts.Add(po);
        Entity.All.Add(e);
        return e;
    }
    static PaintableObject PO(Entity e) { return e.TryGet<PaintableObject>(); }

    static Player MkPlayer(ulong id, string name, float x, float z, bool admin)
    {
        var p = new Player(id, name) { Entity = new Entity { Position = new UnityEngine.Vector3(x, 0, z) } };
        p.Entity.Parts.Add(new LookBridge { Forward = new UnityEngine.Vector3(0, 0, 1) });
        if (admin) P.permission.Grants.Add(id + "|realmpainter.admin");
        return p;
    }
    static void Look(Player p, float x, float y, float z) { p.Entity.TryGet<LookBridge>().Forward = new UnityEngine.Vector3(x, y, z); }
    static void Stand(Player p, float x, float z) { p.Entity.Position = new UnityEngine.Vector3(x, 0, z); }

    static RealmPainter NewPainter(string configJson = null, bool withPlugins = true)
    {
        var p = new RealmPainter();
        p.Name = "RealmPainter";
        if (configJson == null) Inv(p, "LoadDefaultConfig"); else p.Config.Json = configJson;
        Inv(p, "LoadDefaultMessages");
        if (withPlugins) foreach (var kv in Plugins) SetF(p, kv.Key, kv.Value);
        SetF(p, "clock", (Func<DateTime>)(() => Clock));
        Inv(p, "Init");
        Inv(p, "OnServerInitialized");
        if (Admin != null) p.permission.Grants.Add(Admin.Id + "|realmpainter.admin");
        return p;
    }

    static void WriteChronicle()
    {
        File.WriteAllText(Path.Combine(Dir, "RealmChronicle.json"), JsonSerializer.Serialize(Chronicle));
    }
    static void AddChronicle(string type, string title)
    {
        int id = Chronicle.Count + 1;
        Chronicle.Add(new Dictionary<string, object> { { "id", id }, { "ts", Iso(Clock) }, { "type", type }, { "title", title }, { "detail", "" }, { "actors", new string[0] } });
        WriteChronicle();
    }
    static void WriteContracts(Dictionary<string, object> outlaws)
    {
        File.WriteAllText(Path.Combine(Dir, "RealmContracts.json"), JsonSerializer.Serialize(new Dictionary<string, object> { { "NextId", 9 }, { "Contracts", new object[0] }, { "Outlaws", outlaws } }));
    }

    // ---- images ----
    static object NewImg(int w, int h) { return Activator.CreateInstance(Nested("Img"), w, h); }
    static byte[] Px(object img) { return (byte[])F(img, "P"); }
    static byte[] Encode(object img, string mode) { return (byte[])Inv(Nested("Png"), "Encode", img, mode); }
    static object Decode(byte[] png) { return Inv(Nested("Png"), "Decode", png); }
    static void Dump(string name, byte[] png, object img)
    {
        File.WriteAllBytes(Path.Combine(Out, name + ".png"), png);
        if (img != null) File.WriteAllBytes(Path.Combine(Out, name + ".rgba"), Px(img));
        File.WriteAllText(Path.Combine(Out, name + ".size"), F(img ?? Decode(png), "W") + "x" + F(img ?? Decode(png), "H"));
    }
    static byte[] Noise(int n, uint seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) { seed = seed * 1664525 + 1013904223; b[i] = (byte)(seed >> 24); }
        return b;
    }
    static object BoardImg(object board, bool landscape) { return Inv(P, "RenderBoard", board, landscape); }

    static int Main(string[] argv)
    {
        Repo = argv.Length > 0 ? argv[0] : "../../../..";
        Out = argv.Length > 1 ? argv[1] : Path.Combine(Path.GetTempPath(), "realmpainter-out");
        Directory.CreateDirectory(Out);
        Dir = Path.Combine(Path.GetTempPath(), "realmpainter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Interface.Oxide.DataFileSystem.Dir = Dir;
        File.Copy(Path.Combine(Repo, "art", "paintings", "RealmPainterArt.json"), Path.Combine(Dir, "RealmPainterArt.json"));
        try { Run(); }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL unexpected exception: " + ex); }
        finally { try { Directory.Delete(Dir, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static void Run()
    {
        Plugins["RealmChronicle"] = new Plugin { Name = "RealmChronicle", Handler = (m, a) => m == "GetLastEventId" ? (object)Chronicle.Count : null };
        Plugins["RealmContracts"] = new Plugin { Name = "RealmContracts", Handler = (m, a) => { int n; return m == "GetBountyCount" ? (object)(Bounties.TryGetValue((string)a[0], out n) ? n : 0) : null; } };
        Plugins["RealmLaws"] = new Plugin { Name = "RealmLaws", Handler = (m, a) => m == "GetCourtOutlaws" ? CourtOutlaws.ToArray() : null };
        Plugins["RealmHouses"] = new Plugin { Name = "RealmHouses", Handler = (m, a) => { string h; return m == "GetHouse" && HouseOf.TryGetValue((string)a[0], out h) ? h : null; } };
        Plugins["RealmSeasons"] = new Plugin { Name = "RealmSeasons", Handler = (m, a) => m == "GetSeasonNumber" ? (object)SeasonNumber : m == "GetSeasonName" ? SeasonName : m == "GetSeasonStandings" ? Standings : null };
        Plugins["CrownAndConsequences"] = new Plugin { Name = "CrownAndConsequences", Handler = (m, a) => m == "GetKingName" ? King : m == "GetKingHouse" ? KingHouse : m == "GetKingSince" ? KingSince : m == "GetUtcOffsetHours" ? (object)0.0 : null };
        Plugins["RealmEvents"] = new Plugin { Name = "RealmEvents", Handler = (m, a) => m == "GetActiveEvents" ? ActiveEvents.ToArray() : m == "GetNextEvent" ? NextEvent : null };
        Plugins["RealmLegendary"] = new Plugin { Name = "RealmLegendary", Handler = (m, a) => m == "GetBearerName" ? Bearer : null };

        Codec();
        P = NewPainter();
        Admin = MkPlayer(76561190000000001, "Edda", 0, 0, true);
        Peasant = MkPlayer(76561190000000002, "Tam", 0, 0, false);
        ArtAndText();
        Binding();
        LiveBoards();
        RateLimits();
        Protection();
        FacesAndTargeting();
        Persistence();
        Previews();
    }

    // ---------------- PNG codec ----------------
    static void Codec()
    {
        var png = Nested("Png");
        Ok((uint)Inv(png, "Crc", System.Text.Encoding.ASCII.GetBytes("IEND"), 0, 4) == 0xAE426082u, "CRC-32 matches the PNG specification value for IEND");
        Ok((uint)Inv(png, "Adler", System.Text.Encoding.ASCII.GetBytes("Wikipedia"), 0, 9) == 0x11E60398u, "Adler-32 matches the reference value for 'Wikipedia'");

        int n = 0;
        foreach (string mode in new[] { "deflate", "stored", "system" })
            foreach (var wh in new[] { new[] { 1, 1 }, new[] { 3, 2 }, new[] { 17, 9 }, new[] { 300, 7 }, new[] { 64, 64 } })
                foreach (bool alpha in new[] { false, true })
                {
                    var img = NewImg(wh[0], wh[1]);
                    byte[] p = Px(img);
                    byte[] noise = Noise(p.Length, (uint)(wh[0] * 31 + wh[1] + (alpha ? 7 : 0)));
                    for (int i = 0; i < p.Length; i++) p[i] = i % 4 == 3 && !alpha ? (byte)255 : (byte)((i / 4 % 5 == 0) ? noise[i] : (i * 7 / 3) & 255);
                    byte[] enc = Encode(img, mode);
                    byte[] back = Px(Decode(enc));
                    if (back.SequenceEqual(p)) n++;
                    else Ok(false, "round trip " + mode + " " + wh[0] + "x" + wh[1] + (alpha ? " RGBA" : " RGB"));
                    Dump("codec-" + mode + "-" + wh[0] + "x" + wh[1] + (alpha ? "-rgba" : "-rgb"), enc, img);
                }
        Ok(n == 30, "30 images round-trip exactly through the C# encoder and decoder (deflate, stored, system; RGB and RGBA)", n.ToString());

        var flat = NewImg(256, 320);
        var fp = Px(flat);
        for (int i = 0; i < fp.Length; i += 4) { fp[i] = 236; fp[i + 1] = 223; fp[i + 2] = 191; fp[i + 3] = 255; }
        byte[] d = Encode(flat, "deflate"), s = Encode(flat, "stored");
        Ok(d.Length < 4096 && s.Length > 245000, "a flat board deflates to under 4 KB; stored blocks keep all " + s.Length + " bytes", d.Length + " / " + s.Length);
        Ok(d[25] == 2, "an opaque image is written as RGB (colour type 2)");
        object[] sizeArgs = { d, 0, 0 };
        bool okSize = (bool)png.GetMethod("Size", BF).Invoke(null, sizeArgs);
        Ok(okSize && (int)sizeArgs[1] == 256 && (int)sizeArgs[2] == 320, "Size reads width and height from the IHDR");
        var bad = (byte[])d.Clone();
        bad[40] ^= 0x55;
        bool threw = false;
        try { Decode(bad); } catch (Exception) { threw = true; }
        Ok(threw, "the decoder refuses a PNG with a damaged chunk (CRC)");
        var big = NewImg(700, 700);
        Array.Copy(Noise(Px(big).Length, 99), Px(big), Px(big).Length);
        byte[] bigEnc = Encode(big, "deflate");
        Ok(Px(Decode(bigEnc)).SequenceEqual(Px(big)), "a 700x700 noise image (window over 32 KiB, matches at every distance) round-trips");
        Dump("codec-deflate-700x700-noise", bigEnc, big);
    }

    // ---------------- art bundle and text ----------------
    static void ArtAndText()
    {
        object art = F(P, "art");
        Ok(art != null, "the art bundle loads", (string)F(P, "artError") ?? "");
        var paintings = (List<string>)F(art, "Paintings");
        var fonts = (IDictionary)F(art, "Fonts");
        Ok(paintings.Count == 25 && paintings.Contains("crest-varrow") && paintings.Contains("poster-ironbreaker"), "25 finished paintings are offered", string.Join(",", paintings));
        Ok(fonts.Count == 7 && fonts.Contains("display"), "7 glyph atlases decode (dynamic-Huffman PNGs from Node's zlib)");
        var items = (IDictionary)F(art, "Items");
        int decoded = 0, total = 0;
        foreach (DictionaryEntry kv in items)
        {
            string kind = (string)F(kv.Value, "Kind");
            if (kind == "painting") continue;
            total++;
            object img = kind == "mask" ? Inv(P, "MaskSprite", kv.Key) : Inv(P, "Sprite", kv.Key);
            if (img != null && (int)F(img, "W") == (int)F(kv.Value, "Width") && (int)F(img, "H") == (int)F(kv.Value, "Height")) decoded++;
            if (kind != "mask" && img != null && ((string)kv.Key == "sigil-96-varrow" || (string)kv.Key == "tile-parchment")) File.WriteAllBytes(Path.Combine(Out, "bundle-" + kv.Key + ".rgba"), Px(img));
        }
        Ok(decoded == total && total == 72, "every sprite in the bundle decodes at its stated size (" + decoded + "/" + total + ")");

        var small = NewPainter("{ \"MaxImageSide\": 256, \"MaxPngBytes\": 90000 }");
        var sp = (List<string>)F(F(small, "art"), "Paintings");
        Ok(!sp.Contains("crest-varrow") && !sp.Contains("poster-welcome") && !sp.Contains("banner-varrow") && !sp.Contains("sigil-merrin") && sp.Contains("sigil-varrow"), "paintings over MaxImageSide or MaxPngBytes are left out of the bundle", string.Join(",", sp));
        Ok(small.Log.Any(l => l.StartsWith("WARN Art bundle: left out")), "and the server log says which");
        Inv(small, "Unload");

        File.Move(Path.Combine(Dir, "RealmPainterArt.json"), Path.Combine(Dir, "x.json"));
        var none = NewPainter();
        Ok(F(none, "art") == null && ((string)F(none, "artError")).Contains("missing") && !File.Exists(Path.Combine(Dir, "RealmPainterArt.json")), "a missing bundle is reported and not created empty");
        Inv(none, "Unload");
        File.Move(Path.Combine(Dir, "x.json"), Path.Combine(Dir, "RealmPainterArt.json"));

        object body = ((IDictionary)F(F(P, "art"), "Fonts"))["body"];
        Func<string, object> parse = s => Inv(typeof(RealmPainter), "Parse", s);
        object st = parse("Read it with [F4C96D]/chronicle[FFFFFF] now");
        var acc = (bool[])F(st, "Accent");
        Ok((string)F(st, "Text") == "Read it with /chronicle now" && acc[13] && acc[22] && !acc[12] && !acc[24], "board text takes colour tags as accent spans");
        int wA = (int)Inv(typeof(RealmPainter), "Measure", body, "Ostreval", 0), wB = (int)Inv(typeof(RealmPainter), "Measure", body, "Ostreval Ostreval", 0);
        Ok(wA > 40 && wB > 2 * wA, "text is measured from the atlas advances", wA + " " + wB);
        Ok((int)Inv(typeof(RealmPainter), "Measure", body, "Ālfrēd", 0) == (int)Inv(typeof(RealmPainter), "Measure", body, "Alfred", 0), "letters outside the atlas fall back to their unaccented form");
        var lines = (IList)Inv(typeof(RealmPainter), "Wrap", body, parse("The night the Old Throne is fought for. A house with a claim may take it, and the realm will remember."), 200, 2, 0);
        string last = (string)F(lines[lines.Count - 1], "Text");
        Ok(lines.Count == 2 && last.EndsWith("…") && (int)Inv(typeof(RealmPainter), "Measure", body, last, 0) <= 200, "wrapping stops at the line limit with an ellipsis that fits", string.Join(" / ", lines.Cast<object>().Select(l => (string)F(l, "Text"))));
        var longWord = (IList)Inv(typeof(RealmPainter), "Wrap", body, parse("Aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 120, 3, 0);
        Ok(longWord.Count >= 2 && longWord.Cast<object>().All(l => (int)Inv(typeof(RealmPainter), "Measure", body, (string)F(l, "Text"), 0) <= 120), "a word longer than the line is cut to fit");
        Ok((string)Inv(typeof(RealmPainter), "Clean", "  [FF0000]Bad\nname  ", 40) == "(FF0000)Bad name", "names lose colour tags and line breaks before they reach a board");
    }

    // ---------------- binding a sign ----------------
    static void Binding()
    {
        var sign = MkSign("WoodenSign", 0, 0, 3);
        SetF(P, "faceProbe", null);
        Cmd(Peasant, "crest-varrow");
        Ok(Peasant.All().Contains("Only the realm's painters"), "a player without realmpainter.admin is refused");
        Ok(P.permission.Registered.Contains("realmpainter.admin"), "the realmpainter.admin permission is registered");

        Cmd(Admin);
        Ok(Plain(Admin.All()).Contains("/paint <artwork> paints the sign you look at"), "/paint alone shows the usage");
        Cmd(Admin, "list");
        Ok(Admin.All().Contains("crest-varrow") && Admin.All().Contains("chronicle, wanted, standings"), "/paint list names the artworks and the live boards");

        Cmd(Admin, "varrow");
        Ok(Admin.All().Contains("Did you mean") && Admin.All().Contains("sigil-varrow"), "an unknown name suggests close artworks", Admin.All());

        int before = PaintEvents();
        Cmd(Admin, "crest-varrow");
        var po = PO(sign);
        string crest = null;
        foreach (DictionaryEntry kv in (IDictionary)F(F(P, "art"), "Items")) if ((string)kv.Key == "crest-varrow") crest = (string)F(kv.Value, "Png");
        Ok(po.ColorArea.PaintData != null && po.ColorArea.PaintData.SequenceEqual(Convert.FromBase64String(crest)), "the sign gets the painting's PNG exactly as built");
        Ok(PaintEvents() == before + 1 && EventManager.Called.OfType<PaintObjectUpdateEvent>().Last().Entity == sign, "one PaintObjectUpdateEvent is raised for that sign");
        Ok(EventManager.Called.OfType<PaintObjectUpdateEvent>().Last().SentPng == po.ColorArea.PaintData, "the event carries the new picture");
        // face from the box collider: 1.0 x 0.75 m from (-0.5, 0); a 256x320 crest fits as 0.6 x 0.75 centred
        var b = po.ColorArea.Bounds;
        Ok(Math.Abs(b.min.x - -0.3f) < 1e-4 && Math.Abs(b.min.y) < 1e-4 && Math.Abs(b.size.x - 0.6f) < 1e-4 && Math.Abs(b.size.y - 0.75f) < 1e-4, "the picture keeps its shape, centred in the face the colliders give", b.min.x + "," + b.min.y + " " + b.size.x + "x" + b.size.y);
        Ok(po.ColorArea.TextureWidth == 256 && po.ColorArea.TextureHeight == 320, "the area's texture size follows the picture");
        Ok(po.DepthNormalArea.Sets == 1 && Math.Abs(po.DepthNormalArea.Bounds.size.x - 0.008f) < 1e-6, "old brush relief is cleared to a fresh 1x1 area");
        Ok(Admin.All().Contains("Sign s1") && Admin.All().Contains("House Varrow notice") && Admin.All().Contains("probe"), "the admin is told which sign, what it shows and where the face came from", Admin.All());
        Ok(Signs().Count == 1 && (string)F(Signs()[0], "Board") == "crest-varrow" && (ulong)F(Signs()[0], "ViewId") == sign.NetViewID, "the sign is in the registry");
        Ok(File.Exists(Path.Combine(Dir, "RealmPainter.json")) && File.ReadAllText(Path.Combine(Dir, "RealmPainter.json")).Contains("crest-varrow"), "the registry is saved");

        Cmd(Admin, "sigil-varrow");
        Ok(Signs().Count == 1 && (string)F(Signs()[0], "Board") == "sigil-varrow" && PO(sign).ColorArea.Bounds.size.x > 0.74f, "binding again changes the same record, not a new one");

        SetCfg("SendMode", "apply");
        int applies = po.Applies;
        Cmd(Admin, "redraw");
        Ok(po.Applies == applies + 1, "SendMode apply uses PaintableObject.ApplyPainting(true)");
        SetCfg("SendMode", "event");

        po.ColorArea.Refuse = true;
        Cmd(Admin, "banner-merrin");
        Ok(Admin.All().Contains("could not be drawn") && Admin.All().Contains("LoadImage failed"), "a picture the game refuses is reported, not swallowed", Admin.All());
        po.ColorArea.Refuse = false;
        Cmd(Admin, "redraw");
        Ok(Admin.All().Contains("Redrew 1"), "and a redraw puts it right", Admin.All());

        Cmd(Admin, "info");
        Ok(Admin.All().Contains("Sign s1") && Admin.All().Contains("banner-merrin"), "/paint info describes the sign you look at", Admin.All());

        Cmd(Admin, "notice");
        Ok(Admin.All().Contains("Usage: /paint notice") || Plain(Admin.All()).Contains("/paint notice"), "/paint notice without text shows its usage");

        SetCfg("Enabled", false);
        Cmd(Admin, "sigil-merrin");
        Ok(Admin.All().Contains("switched off"), "Enabled false stops new paintings");
        SetCfg("Enabled", true);

        Cmd(Admin, "clear");
        Ok(Signs().Count == 0 && PO(sign).ColorArea.TextureWidth == 1 && Admin.All().Contains("blank"), "/paint clear blanks the sign and frees it");

        Entity.All.Remove(sign);
        Stand(Admin, 50, 50);
        Cmd(Admin, "sigil-varrow");
        Ok(Admin.All().Contains("No paintable sign"), "with no sign in reach the admin is told so");
        Stand(Admin, 0, 0);
    }

    // ---------------- live boards ----------------
    static void LiveBoards()
    {
        var board = MkSign("NoticeBoard", 0, 0, 4, 1.6f, 1.0f);
        SetCfg("MinSecondsBetweenRedraws", 5);
        AddChronicle("coronation", "Edda of House Varrow takes the Old Throne");
        AddChronicle("treaty_signed", "Varrow and Merrin sign a treaty");
        Cmd(Admin, "chronicle");
        object s = Signs().Cast<object>().Last();
        var po = PO(board);
        Ok(po.ColorArea.PaintData != null && Admin.All().Contains("chronicle board"), "/paint chronicle draws a live board", Admin.All());
        object img = Decode(po.ColorArea.PaintData);
        Ok((int)F(img, "W") == 320 && (int)F(img, "H") == 256, "a wide face gets the landscape layout (320x256)");
        Dump("board-chronicle-sign", po.ColorArea.PaintData, img);
        int events = PaintEvents();
        Advance(120);
        Ok(PaintEvents() == events, "an unchanged board is not redrawn");
        AddChronicle("decree", "The crown forbids raids on market day");
        Advance(70);
        Ok(PaintEvents() == events + 1, "a new Chronicle entry redraws the board once", (PaintEvents() - events).ToString());
        int reads = Interface.Oxide.DataFileSystem.Reads["RealmChronicle"];
        Advance(300);
        Ok(Interface.Oxide.DataFileSystem.Reads["RealmChronicle"] == reads, "RealmChronicle.json is read again only when GetLastEventId moves");

        // wanted posters
        WriteContracts(new Dictionary<string, object>
        {
            { "76561190000000010", new Dictionary<string, object> { { "Name", "Black Rhosyn" }, { "Until", Clock.AddDays(2) }, { "By", "Edda" }, { "Court", false } } },
            { "76561190000000011", new Dictionary<string, object> { { "Name", "Wat the Lame" }, { "Until", Clock.AddDays(1) }, { "By", "court" }, { "Court", true } } },
            { "76561190000000012", new Dictionary<string, object> { { "Name", "Old News" }, { "Until", Clock.AddDays(-1) }, { "By", "Edda" }, { "Court", false } } }
        });
        CourtOutlaws.Add("76561190000000013|Gil Thatch|" + Iso(Clock.AddHours(12)) + "|c4");
        Bounties["76561190000000011"] = 3; Bounties["76561190000000010"] = 1;
        HouseOf["76561190000000011"] = "Corvane";
        var gather = (Func<string, string, object>)((k, a) => Inv(P, "Gather", k, a));
        object w1 = gather("wanted", null), w2 = gather("wanted", "2"), w3 = gather("wanted", "3"), w4 = gather("wanted", "4");
        Ok((string)F(w1, "Headline") == "Wat the Lame" && (string)F(w1, "Hero") == "sigil-96-corvane" && ((string)F(w1, "Kicker")).Contains("court"), "wanted 1 is the outlaw with most bounties, with their house sigil and the court's kicker");
        Ok(((string)F(w1, "Body")).StartsWith("3 bounties") && (string)F(w2, "Headline") == "Black Rhosyn" && (string)F(w3, "Headline") == "Gil Thatch", "then by bounties, and court outlaws from RealmLaws join the roll");
        Ok((string)F(w4, "Title") == "The Roads Are Quiet" && ((string)F(w4, "Body")).Contains("Only 3"), "a poster number past the roll stays bare and says why");
        Ok(!((string)F(w1, "Key")).Contains("Old News") && !((string)F(w4, "Key")).Contains("Old News"), "lapsed outlaws are not shown");
        string k1 = (string)F(w1, "Key");
        Bounties["76561190000000011"] = 4;
        int contractReads = Interface.Oxide.DataFileSystem.Reads["RealmContracts"];
        gather("wanted", "2"); gather("wanted", "3");
        Ok(Interface.Oxide.DataFileSystem.Reads["RealmContracts"] == contractReads, "several wanted posters share one read of RealmContracts.json");
        Clock = Clock.AddSeconds(10);
        Ok((string)F(gather("wanted", null), "Key") != k1, "a new bounty changes what the poster shows");

        // ironbreaker
        Bearer = null;
        Ok((string)F(gather("ironbreaker", null), "Headline") == "Unclaimed", "the Ironbreaker reads unclaimed when GetBearerName returns null");
        Bearer = "Aldric Vane";
        Ok((string)F(gather("ironbreaker", null), "Headline") == "Borne by Aldric Vane", "and names its bearer when there is one");
        SetF(P, "RealmLegendary", null);
        Ok((string)F(gather("ironbreaker", null), "Headline") == "Unclaimed", "and reads unclaimed when RealmLegendary is not loaded");
        SetF(P, "RealmLegendary", Plugins["RealmLegendary"]);
        Plugins["RealmLegendary"].Handler = (m, a) => { throw new InvalidOperationException("boom"); };
        Ok((string)F(gather("ironbreaker", null), "Headline") == "Unclaimed", "and when the call throws");
        Plugins["RealmLegendary"].Handler = (m, a) => m == "GetBearerName" ? Bearer : null;

        // standings, proclamation, events
        Standings.Add(new Dictionary<string, object> { { "rank", 1 }, { "house", "Varrow" }, { "score", 120 }, { "crownDays", 3 } });
        Standings.Add(new Dictionary<string, object> { { "rank", 2 }, { "house", "Merrin" }, { "score", 95 }, { "crownDays", 0 } });
        Standings.Add(new Dictionary<string, object> { { "rank", 3 }, { "house", "The Grey Company" }, { "score", 40 }, { "crownDays", 0 } });
        object st = gather("standings", null);
        var rows = (IList)F(st, "Rows");
        Ok(rows.Count == 3 && (string)F(rows[0], "Icon") == "sigil-32-varrow" && (string)F(rows[2], "Icon") == "icon-24-house" && (string)F(rows[0], "Sub") == "3 days on the Old Throne", "standings list each house with its sigil (a house sigil icon for others) and days crowned");
        Ok((string)F(st, "Kicker") == "Season 1 standings" && (string)F(st, "Title") == "The Hollow Crown", "with the season's number and name");
        SeasonNumber = 0;
        Ok(((string)F(gather("standings", null), "Body")).Contains("No season"), "between seasons the board says so");
        SeasonNumber = 1;

        King = null;
        Ok((string)F(gather("proclamation", null), "Title") == "The Old Throne Stands Empty", "an empty throne is proclaimed as such");
        King = "Edda"; KingHouse = "Varrow"; KingSince = Iso(Clock.AddDays(-2));
        object pr = gather("proclamation", null);
        Ok((string)F(pr, "Title") == "Edda" && (string)F(pr, "Headline") == "of House Varrow" && ((string)F(pr, "Body")).Contains("forbids raids"), "the proclamation names the monarch, their house and the latest decree of the reign", (string)F(pr, "Body"));

        NextEvent = null; ActiveEvents.Clear();
        Ok((string)F(gather("event", null), "Title") == "No Event Is Called", "no event scheduled: the board says so");
        NextEvent = new Dictionary<string, object> { { "title", "Crown night" }, { "at", Clock.AddDays(1) } };
        object ev = gather("event", null);
        Ok((string)F(ev, "Hero") == "icon-64-crown" && ((string)F(ev, "Subtitle")).StartsWith("Next: "), "the next event shows its icon and date", (string)F(ev, "Subtitle"));
        ActiveEvents.Add("tournament|" + Iso(Clock.AddHours(-1)) + "|" + Iso(Clock.AddHours(1)));
        object run = gather("event", null);
        Ok((string)F(run, "Title") == "The Royal Tournament" && ((string)F(run, "Subtitle")).StartsWith("Under way now"), "a running event wins over the next one");
        Ok((string)F(gather("event", "truce"), "Title") == "The Truce of the Realm", "event <kind> shows that event");

        object notice = gather("notice", "To the Capital | Follow the river road north. Mind the toll.");
        Ok((string)F(notice, "Title") == "To the Capital" && (string)F(notice, "Body") == "Follow the river road north. Mind the toll.", "a notice splits title and text at the bar");

        foreach (string k in new[] { "chronicle", "wanted", "standings", "proclamation", "event", "ironbreaker" }) SetF(P, k == "chronicle" ? "RealmChronicle" : k == "wanted" ? "RealmContracts" : k == "standings" ? "RealmSeasons" : k == "proclamation" ? "CrownAndConsequences" : k == "event" ? "RealmEvents" : "RealmLegendary", null);
        SetF(P, "RealmLaws", null);
        bool allFine = true;
        foreach (string k in new[] { "chronicle", "wanted", "standings", "proclamation", "event", "ironbreaker" })
        {
            object g = gather(k, null);
            if (string.IsNullOrEmpty((string)F(g, "Title"))) allFine = false;
            Inv(P, "RenderBoard", g, false);
        }
        Ok(allFine, "every board still draws with none of its plugins loaded");
        foreach (var kv in Plugins) SetF(P, kv.Key, kv.Value);

        // determinism and caps
        byte[] a1 = Encode(BoardImg(gather("standings", null), false), "deflate");
        byte[] a2 = Encode(BoardImg(gather("standings", null), false), "deflate");
        Ok(a1.SequenceEqual(a2), "the same content renders to the same bytes");
        SetCfg("MaxPngBytes", 16384);
        Cmd(Admin, "redraw");
        Ok(Admin.All().Contains("over MaxPngBytes"), "a board over MaxPngBytes is refused", Admin.All());
        SetCfg("MaxPngBytes", 262144);
        SetCfg("FlipX", true);
        object flat = Decode((byte[])F(Inv(P, "Compose", s, null), "Png"));
        SetCfg("FlipX", false);
        Ok(flat != null, "FlipX composes");
    }

    // ---------------- rate limits ----------------
    static void RateLimits()
    {
        Entity.All.Clear();
        Signs().Clear();
        EventManager.Called.Clear();
        SetCfg("MinSecondsBetweenRedraws", 120);
        SetCfg("MaxRedrawsPerMinute", 6);
        var ids = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            var e = MkSign("Board" + i, i * 10, 0, 3);
            Stand(Admin, i * 10, 0);
            Look(Admin, 0, 0, 1);
            Cmd(Admin, "notice", "Notice " + i + " | First text");
            ids.Add((string)F(Signs()[Signs().Count - 1], "Id"));
        }
        Ok(PaintEvents() == 10, "an admin's own binds are drawn at once", PaintEvents().ToString());
        Clock = Clock.AddMinutes(5);
        foreach (object s in Signs()) SetF(s, "Arg", "Notice | Second text");
        Inv(P, "RefreshBoards", (string)null);
        EventManager.Called.Clear();
        Tick();
        Ok(PaintEvents() == 6, "ten changed boards: six are drawn in the first minute (MaxRedrawsPerMinute)", PaintEvents().ToString());
        Ok(((IList)F(P, "queue")).Count == 4, "the other four wait in the queue");
        Advance(30);
        Ok(PaintEvents() == 6, "still six half a minute later");
        Advance(40);
        Ok(PaintEvents() == 10, "the rest are drawn when the minute has passed", PaintEvents().ToString());

        // per-sign interval
        object first = Signs()[0];
        SetF(first, "Arg", "Notice | Third text");
        Inv(P, "RefreshBoards", "notice");
        EventManager.Called.Clear();
        Advance(10);
        Ok(PaintEvents() == 0 && ((IList)F(P, "queue")).Contains(ids[0]), "a sign drawn less than MinSecondsBetweenRedraws ago waits");
        Advance(120);
        Ok(PaintEvents() == 1, "and is drawn once its interval has passed", PaintEvents().ToString());

        // admin redraw skips the interval but keeps the server budget
        EventManager.Called.Clear();
        Stand(Admin, 0, 0);
        Cmd(Admin, "redraw");
        Ok(PaintEvents() == 1 && Admin.All().Contains("Redrew 1"), "an admin's /paint redraw skips the per-sign interval", Admin.All());
        Cmd(Admin, "redraw", "all");
        Ok(PaintEvents() == 6 && Admin.All().Contains("Redrew 5") && Admin.All().Contains("5 waiting"), "/paint redraw all still keeps to the server's budget", Admin.All());
        Cmd(Admin, "redraw", ids[9]);
        Ok(Admin.All().Contains("queued") || Admin.All().Contains("waiting"), "a redraw past the budget says it is queued", Admin.All());
        Advance(70);
        Ok(((IList)F(P, "queue")).Count == 0, "and the queue empties within the next minute");

        // byte budget
        EventManager.Called.Clear();
        SetCfg("MaxBytesPerMinute", 65536);
        SetCfg("MaxRedrawsPerMinute", 100);
        Clock = Clock.AddMinutes(5);
        foreach (object s in Signs()) SetF(s, "Arg", "Notice | Fourth text, with more words to draw");
        Inv(P, "RefreshBoards", (string)null);
        Tick();
        int drawn = PaintEvents();
        Ok(drawn >= 1 && drawn < 10, "MaxBytesPerMinute stops redraws once the minute's bytes are spent (" + drawn + " drawn)");
        SetCfg("MaxBytesPerMinute", 1048576);
        SetCfg("MaxRedrawsPerMinute", 6);
        Advance(300);

        SetCfg("LiveBoards", false);
        foreach (object s in Signs()) SetF(s, "Arg", "Notice | Fifth");
        EventManager.Called.Clear();
        Advance(200);
        Ok(PaintEvents() == 0, "LiveBoards false: boards keep their last picture");
        SetCfg("LiveBoards", true);
        Advance(200);
        Ok(PaintEvents() == 10, "and switching it back on catches up", PaintEvents().ToString());
    }

    // ---------------- protection ----------------
    static void Protection()
    {
        EventManager.Called.Clear();
        object s0 = Signs()[0];
        Entity e = Entity.TryGetFromViewID((ulong)F(s0, "ViewId"));
        var po = PO(e);
        byte[] ours = po.ColorArea.PaintData;
        Ok(EventManager.Subs.Count == 1, "the plugin watches PaintObjectUpdateEvent");
        // a player paints over it: the server has already read the player's picture in
        po.ColorArea.PaintData = new byte[] { 1, 2, 3 };
        var ev = new PaintObjectUpdateEvent(e, po) { Sender = Peasant };
        EventManager.Deliver(ev);
        Ok(((IList)F(P, "queue")).Contains((string)F(s0, "Id")), "a player's paint on a bound sign queues a repaint");
        Tick();
        Ok(po.ColorArea.PaintData.SequenceEqual(ours), "and the realm's picture is back on the next tick");
        int before = PaintEvents();
        po.ColorArea.PaintData = new byte[] { 4, 5, 6 };
        EventManager.Deliver(new PaintObjectUpdateEvent(e, po) { Sender = Admin });
        Tick();
        Ok(PaintEvents() == before && po.ColorArea.PaintData[0] == 4, "an admin's own brush strokes are left alone (they calibrate faces this way)");
        Cmd(Admin, "redraw", (string)F(s0, "Id"));
        SetCfg("ProtectBoundSigns", false);
        EventManager.Deliver(new PaintObjectUpdateEvent(e, po) { Sender = Peasant });
        Ok(!((IList)F(P, "queue")).Contains((string)F(s0, "Id")), "ProtectBoundSigns false lets players paint over boards");
        SetCfg("ProtectBoundSigns", true);
        Ok(EventManager.Called.OfType<PaintObjectUpdateEvent>().All(x => x.Sender.IsServer), "every update the plugin raises is the server's own");
    }

    // ---------------- faces and targeting ----------------
    static void FacesAndTargeting()
    {
        Entity.All.Clear();
        Signs().Clear();
        var near = MkSign("SmallSign", 0, 0, 2);
        var far = MkSign("SmallSign", 3, 0, 6);
        Stand(Admin, 0, 0);
        Look(Admin, 3, 0, 6);
        string how = null;
        object[] args = { Admin, how };
        var got = (Entity)typeof(RealmPainter).GetMethod("TargetSign", BF).Invoke(P, args);
        Ok(got == far && (string)args[1] == "aim", "the sign in the direction you look wins over a nearer one beside you");
        Look(Admin, -1, 0, 0);
        args = new object[] { Admin, null };
        got = (Entity)typeof(RealmPainter).GetMethod("TargetSign", BF).Invoke(P, args);
        Ok(got == near && (string)args[1] == "near", "looking away, the sign you stand next to is used");
        Physics.Hits.Add(new RaycastHit { collider = (Collider)((GameObject)PO(far).gameObject).Children[0], distance = 6.5f });
        Look(Admin, -1, 0, 0);
        args = new object[] { Admin, null };
        got = (Entity)typeof(RealmPainter).GetMethod("TargetSign", BF).Invoke(P, args);
        Ok(got == far && (string)args[1] == "look" && Physics.Casts > 0, "a look ray that hits a sign's collider (Physics.RaycastAll by reflection) is used first");
        Physics.Hits.Clear();

        // the face probe by reflection: a box collider 1.2 x 0.6, scaled 2x in paint space
        var probe = MkSign("ScaledSign", 20, 0, 0, 1.2f, 0.6f);
        ((UnityEngine.Transform)PO(probe).PaintingSpaceTransform).Scale = new UnityEngine.Vector3(0.5f, 0.5f, 1);
        var box = (BoxCollider)((GameObject)PO(probe).gameObject).Children[0];
        box.transform = new UnityEngine.Transform { Offset = new UnityEngine.Vector3(20, 0, 0) };
        var face = (float[])Inv(typeof(RealmPainter), "ProbeFace", PO(probe));
        Ok(face != null && Math.Abs(face[2] - 2.4f) < 1e-4 && Math.Abs(face[3] - 1.2f) < 1e-4 && Math.Abs(face[0] + 1.2f) < 1e-4, "ProbeFace maps box collider corners into paint space", face == null ? "null" : string.Join(",", face));
        var trigger = new BoxCollider { center = new UnityEngine.Vector3(0, 0, 0), size = new UnityEngine.Vector3(9, 9, 9), transform = box.transform, isTrigger = true };
        ((GameObject)PO(probe).gameObject).Children.Add(trigger);
        face = (float[])Inv(typeof(RealmPainter), "ProbeFace", PO(probe));
        Ok(Math.Abs(face[2] - 2.4f) < 1e-4, "trigger colliders are ignored");

        // calibrate from the game's own bounds after an admin painted the corners
        Stand(Admin, 0, 0);
        Look(Admin, 0, 0, 1);
        Cmd(Admin, "sigil-ashgrove");
        var po = PO(near);
        po.ColorArea.Bounds.min.x = -0.45f; po.ColorArea.Bounds.min.y = 0.1f; po.ColorArea.Bounds.size.x = 0.9f; po.ColorArea.Bounds.size.y = 0.5f;
        Cmd(Admin, "face", "calibrate");
        object s = Signs()[0];
        Ok(Admin.All().Contains("0.90 x 0.50") && (string)F(s, "FaceFrom") == "calibrated", "/paint face calibrate takes the painted area as the face", Admin.All());
        Ok(Math.Abs(po.ColorArea.Bounds.size.x - 0.5f) < 1e-4 && Math.Abs(po.ColorArea.Bounds.min.x + 0.25f) < 1e-4, "and the sigil is redrawn square inside it");
        Cmd(Admin, "face", "save");
        Ok(Admin.All().Contains("Every SmallSign") && P.Config.Json.Contains("SmallSign"), "/paint face save remembers it for every sign of that kind");
        Look(Admin, 3, 0, 6);
        Cmd(Admin, "sigil-merrin");
        Ok((string)F(Signs()[1], "FaceFrom") == "name", "a new sign of that kind uses the saved face");
        Look(Admin, 0, 0, 1);
        Cmd(Admin, "face", "1.5", "1");
        Ok(Admin.All().Contains("1.50 x 1.00") && (string)F(s, "FaceFrom") == "manual", "/paint face <w> <h> sets it by hand");
        Cmd(Admin, "face", "0", "1");
        Ok(Admin.All().Contains("not usable"), "a face of zero width is refused");
        Cmd(Admin, "fit", "fill");
        Ok(Math.Abs(po.ColorArea.Bounds.size.x - 1.5f) < 1e-4 && Math.Abs(po.ColorArea.Bounds.size.y - 1f) < 1e-4, "/paint fit fill stretches the picture over the face");
        Cmd(Admin, "face");
        Ok(Admin.All().Contains("from manual") && Plain(Admin.All()).Contains("/paint face calibrate"), "/paint face shows the face and how to measure it");
    }

    // ---------------- registry and persistence ----------------
    static void Persistence()
    {
        Stand(Admin, 0, 0);
        Cmd(Admin, "nearby");
        Ok(Admin.All().Contains("paintable object(s) within 20 m") && Admin.All().Contains("SmallSign") && Admin.All().Contains("px"), "/paint nearby lists paintable objects with their face in metres and the game's texels", Admin.All());
        Cmd(Admin, "signs");
        Ok(Admin.All().Contains("2 bound sign(s)") && Admin.All().Contains("sigil-ashgrove"), "/paint signs lists the registry", Admin.All());
        Cmd(Admin, "status");
        Ok(Admin.All().Contains("2 signs") && Admin.All().Contains("legendary=yes"), "/paint status reports signs, budget and sources", Admin.All());
        Inv(P, "Unload");
        Ok(EventManager.Subs.Count == 0, "Unload stops watching paint events");

        // a restart where view ids change: the sign is found again by position
        Entity first = Entity.All[0];
        first.NetViewID = 777777;
        var again = NewPainter();
        P = again;
        object s = Signs()[0];
        var e = (Entity)Inv(P, "Locate", s);
        Ok(e == first && (ulong)F(s, "ViewId") == 777777, "after a restart the sign is found by where it stands if its id changed");
        Ok((string)F(s, "FaceFrom") == "manual" && (string)F(s, "Fit") == "fill", "faces and fit survive a reload");

        Entity.All.Remove(first);
        SetF(s, "LastKey", null);
        Inv(P, "Enqueue", s, true);
        Tick();
        Ok((bool)F(s, "Missing") && (string)F(s, "LastError") == "sign not found", "a sign that is gone is marked missing, not retried forever");
        Cmd(Admin, "forget", (string)F(s, "Id"));
        Ok(Signs().Count == 1, "/paint forget drops it");
        object keep = Signs()[0];
        SetF(keep, "Missing", true);
        Entity back = Entity.TryGetFromViewID((ulong)F(keep, "ViewId"));
        Stand(Admin, back.Position.x, back.Position.z - 2);
        Look(Admin, 0, 0, 1);
        Cmd(Admin, "realm-emblem");
        Ok(Signs().Count == 1 && !(bool)F(keep, "Missing") && (string)F(keep, "Board") == "realm-emblem", "binding a sign whose record was marked missing reuses that record", Admin.All());
        Cmd(Admin, "unbind", "s999");
        Ok(Admin.All().Contains("No sign s999"), "an unknown sign id is answered");

        Inv(P, "Unload");
        string file = Path.Combine(Dir, "RealmPainter.json");
        File.WriteAllText(file, "{ \"Signs\": [ { \"Id\": ");
        var damaged = NewPainter();
        P = damaged;
        Ok((bool)F(damaged, "loadFailed") && damaged.Log.Any(l => l.StartsWith("ERROR") && l.Contains("NOT overwritten")), "a damaged registry is reported");
        Stand(Admin, 3, 6);
        Cmd(Admin, "sigil-varrow");
        Ok(Admin.All().Contains("could not be read"), "and nothing is bound while it is damaged");
        Tick(); Inv(P, "OnServerSave"); Inv(P, "Unload");
        Ok(File.ReadAllText(file) == "{ \"Signs\": [ { \"Id\": ", "a damaged registry is never overwritten");
        File.Delete(file);

        EventManager.FailSubscribe = true;
        var noEvents = NewPainter();
        Ok(noEvents.Log.Any(l => l.Contains("not protected")), "if the game's event bus refuses the subscription the plugin still loads and says so");
        EventManager.FailSubscribe = false;
        P = NewPainter();
    }

    // ---------------- previews for the guide ----------------
    static void Previews()
    {
        King = "Edda"; KingHouse = "Varrow"; KingSince = Iso(Clock.AddDays(-2)); Bearer = "Aldric Vane";
        foreach (var kv in Plugins) SetF(P, kv.Key, kv.Value);
        Chronicle.Clear();
        AddChronicle("coronation", "Edda of House Varrow takes the Old Throne");
        AddChronicle("treaty_signed", "Varrow and Merrin sign a treaty of ten days");
        AddChronicle("contract_posted", "A bounty of 40 iron is posted on Wat the Lame");
        AddChronicle("tournament_champion", "Brannoc of Halloran wins the Royal Tournament");
        AddChronicle("oath_broken", "Gil Thatch breaks his oath to House Dunmere");
        AddChronicle("decree", "The crown forbids raids on market day");
        var gather = (Func<string, string, object>)((k, a) => Inv(P, "Gather", k, a));
        var shots = new[]
        {
            new { Name = "chronicle", Kind = "chronicle", Arg = (string)null, Land = false },
            new { Name = "chronicle-wide", Kind = "chronicle", Arg = (string)null, Land = true },
            new { Name = "wanted", Kind = "wanted", Arg = "1", Land = false },
            new { Name = "wanted-none", Kind = "wanted", Arg = "9", Land = false },
            new { Name = "standings", Kind = "standings", Arg = (string)null, Land = false },
            new { Name = "proclamation", Kind = "proclamation", Arg = (string)null, Land = false },
            new { Name = "event", Kind = "event", Arg = (string)null, Land = false },
            new { Name = "ironbreaker", Kind = "ironbreaker", Arg = (string)null, Land = false },
            new { Name = "notice-wide", Kind = "notice", Arg = "To the Capital | Follow the river road north to the Old Throne. Mind the toll at the bridge.", Land = true },
        };
        int sizes = 0;
        foreach (var s in shots)
        {
            object img = BoardImg(gather(s.Kind, s.Arg), s.Land);
            byte[] png = Encode(img, "deflate");
            Dump("board-" + s.Name, png, img);
            if (png.Length < 160 * 1024) sizes++;
            Console.WriteLine("     board-" + s.Name + ": " + png.Length + " bytes");
        }
        Ok(sizes == shots.Length, "every preview board encodes under 160 KB");
        SetCfg("BoardTexture", false);
        object plain = BoardImg(gather("chronicle", null), false);
        byte[] plainPng = Encode(plain, "deflate");
        Dump("board-chronicle-plain", plainPng, plain);
        Console.WriteLine("     board-chronicle-plain: " + plainPng.Length + " bytes; stored: " + Encode(plain, "stored").Length + " bytes");
        Ok(plainPng.Length < 30 * 1024, "without the texture a board is under 30 KB", plainPng.Length.ToString());
        SetCfg("BoardTexture", true);
    }
}
