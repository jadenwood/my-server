// node --test tools/realm-integration/  (no dependencies)
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { analyse, splitArgs, stripComments, commandsMarkdown } from './check.mjs';

const CHRONICLE = `namespace Oxide.Plugins {
  public class RealmChronicle : ReignOfKingsPlugin {
    private static readonly HashSet<string> KnownTypes = new HashSet<string> {
      "decree", "coronation", // a comment
      "hunt_kill"
    };
    private int Log(string type, string title, string detail, string[] actors) { return 1; }
    [ChatCommand("chronicle")] private void CmdChronicle(Player player, string command, string[] args) { }
  }
}`;

function fakeRepo(plugins, { server = ['decree', 'coronation', 'hunt_kill'], page = ['decree', 'coronation', 'hunt_kill'], events = false } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'realm-int-'));
  mkdirSync(join(dir, 'plugins', 'docs', 'RealmX'), { recursive: true });
  mkdirSync(join(dir, 'chronicle', 'public', 'assets'), { recursive: true });
  writeFileSync(join(dir, 'plugins', 'RealmChronicle.cs'), CHRONICLE);
  for (const [name, src] of Object.entries(plugins)) writeFileSync(join(dir, 'plugins', name + '.cs'), src);
  writeFileSync(join(dir, 'chronicle', 'server.js'), `export const EVENT_TYPES = new Set([\n  ${server.map((t) => `'${t}'`).join(', ')}, // x\n]);\n`);
  writeFileSync(join(dir, 'chronicle', 'public', 'assets', 'common.js'),
    `export const TYPE_META = {\n${page.map((t) => `  ${t}: { label: 'L', icon: 'scroll' },`).join('\n')}\n};\n`);
  if (events) writeFileSync(join(dir, 'plugins', 'docs', 'RealmX', 'EVENTS.json'), '[]');
  return dir;
}

const plugin = (name, body) => `namespace Oxide.Plugins {\n  public class ${name} : ReignOfKingsPlugin {\n${body}\n  }\n}\n`;

test('the real repository has no wiring problems', () => {
  const r = analyse();
  assert.deepEqual(r.problems, []);
  assert.ok(r.plugins.length >= 14);
  assert.ok(r.plugins.reduce((n, p) => n + p.calls.filter((c) => c.target).length, 0) > 50);
  assert.match(commandsMarkdown(r), /\/house/);
});

test('a clean fake repo passes', () => {
  const dir = fakeRepo({
    RealmA: plugin('RealmA', `
    [PluginReference] private Plugin RealmChronicle;
    [PluginReference] private Plugin RealmB;
    private void Go() {
      RealmChronicle.Call("Log", "decree", "t", "d", new string[0]);
      Chronicle("hunt_kill", "title", "d", null);
      object o = RealmB.Call("GetThing", "x", 3);
      string s = CallString(RealmB, "GetName", "id");
    }
    private void Chronicle(string type, string title, string detail, string[] actors) { RealmChronicle.Call("Log", type, title, detail, actors); }
    private object CallSafe(Plugin plugin, string method, params object[] args) { return plugin.Call(method, args); }
    private string CallString(Plugin plugin, string method, params object[] args) { return CallSafe(plugin, method, args) as string; }
    [ChatCommand("alpha")] private void CmdA(Player p, string c, string[] a) { }`),
    RealmB: plugin('RealmB', `
    private object GetThing(string id, int n) { return null; }
    private string GetName(string id) { return id; }
    [ChatCommand("beta")] private void CmdB(Player p, string c, string[] a) { }`),
  });
  try {
    const r = analyse(dir);
    assert.deepEqual(r.problems, []);
    const a = r.plugins.find((p) => p.name === 'RealmA');
    assert.deepEqual([...new Set(a.calls.map((c) => `${c.target}.${c.method}/${c.arity}`))].sort(),
      ['RealmB.GetName/1', 'RealmB.GetThing/2', 'RealmChronicle.Log/4']);
    assert.deepEqual(a.types.map((t) => t.type).sort(), ['decree', 'hunt_kill']);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('every kind of drift is reported', () => {
  const dir = fakeRepo({
    RealmA: plugin('RealmA', `
    [PluginReference] private Plugin RealmB;
    [PluginReference] private Plugin RealmGone;
    [PluginReference] private Plugin RealmChronicle;
    private void Go() {
      RealmB.Call("Missing");
      RealmB.Call("Shown", "x");
      RealmB.Call("TwoArgs", "x");
      RealmChronicle.Call("Log", "not_registered", "t", "d", new string[0]);
      // RealmB.Call("CommentedOut");
    }
    [ChatCommand("house")] private void CmdA(Player p, string c, string[] a) { }
    [ChatCommand("kick")] private void CmdK(Player p, string c, string[] a) { }`),
    RealmB: plugin('RealmB', `
    public string Shown(string x) { return x; }
    private string TwoArgs(string a, string b) { return a; }
    [ChatCommand("House")] private void CmdB(Player p, string c, string[] a) { }`),
  }, { server: ['decree', 'coronation'], events: true });
  try {
    const text = analyse(dir).problems.join('\n');
    assert.match(text, /RealmGone names no plugin/);
    assert.match(text, /RealmB has no method Missing/);
    assert.match(text, /RealmB\.Shown is public/);
    assert.match(text, /RealmB\.TwoArgs takes 2 argument\(s\), called with 1/);
    assert.match(text, /"not_registered" is not registered/);
    assert.match(text, /hunt_kill is missing from: server/);
    assert.match(text, /EVENTS\.json still waiting/);
    assert.match(text, /\/house is also registered/);
    assert.match(text, /\/kick is a game command/);
    assert.doesNotMatch(text, /CommentedOut/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('parsing helpers keep strings and nesting intact', () => {
  assert.equal(stripComments('a // x\nb /* y\nz */ c "// not" @"/*q""*/"'), 'a \nb \n c "// not" @"/*q""*/"');
  const { args } = splitArgs('"Log", f(a, b), new[] { "x", "y" }, "a,b")', 0);
  assert.deepEqual(args, ['"Log"', 'f(a, b)', 'new[] { "x", "y" }', '"a,b"']);
});

const STYLE = `
    #region Chat style
    private const string ChatGold = "D6A043";
    private const string ChatOk = "8FC97A";
    private const string ChatWarn = "E8913A";
    private const string ChatError = "E86A5C";
    #endregion`;

test('chat style: a plugin in the house style passes', () => {
  const dir = fakeRepo({
    RealmA: plugin('RealmA', `${STYLE}
    protected override void LoadDefaultMessages() {
      lang.RegisterMessages(new Dictionary<string, string> {
        { "Speaker", "Alpha" },
        { "Herald", "[D6A043]Herald[FFFFFF]: " },
        { "Help", "  [F4C96D]/alpha list[FFFFFF] | see oxide/data/RealmA.json, 3/4 done, +/-n" }
      }, this);
    }
    [ChatCommand("alpha")] private void CmdA(Player p, string c, string[] a) { }`),
  });
  try {
    assert.deepEqual(analyse(dir).problems, []);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('chat style: every kind of drift is reported', () => {
  const dir = fakeRepo({
    RealmA: plugin('RealmA', `
    private const string ChatGold = "C8A050";
    protected override void LoadDefaultMessages() {
      var m = new Dictionary<string, string>();
      m["Herald"] = "[C8A050]Herald[FFFFFF]: ";
      m["Plain"] = "Type /alpha to start.";
      m["Ghost"] = "Try [F4C96D]/ghost[FFFFFF].";
      m["Wall"] = "${'x'.repeat(201)}";
      m["Pink"] = "[FF00FF]pink[FFFFFF]";
      lang.RegisterMessages(m, this);
    }
    [ChatCommand("alpha")] private void CmdA(Player p, string c, string[] a) { }`),
  });
  try {
    const text = analyse(dir).problems.join('\n');
    assert.match(text, /no "Chat style" block/);
    assert.match(text, /ChatGold is C8A050, the chat palette says D6A043/);
    assert.match(text, /ChatOk is missing/);
    assert.match(text, /no "Speaker" key/);
    assert.match(text, /"Herald" must be/);
    assert.match(text, /"Plain" mentions \/alpha without the command colour/);
    assert.match(text, /"Ghost" mentions \/ghost, which no plugin registers/);
    assert.match(text, /"Wall" is 201 characters/);
    assert.match(text, /colour \[FF00FF\] is not in the chat palette/);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the /realm catalogue must list every command once, under its owner', () => {
  const dir = fakeRepo({
    RealmA: plugin('RealmA', `
    [ChatCommand("alpha")] private void CmdA(Player p, string c, string[] a) { }
    [ChatCommand("beta")] private void CmdB(Player p, string c, string[] a) { }`),
    RealmHerald: plugin('RealmHerald', `${STYLE}
    private static readonly string[] Subjects = { "one" };
    private static readonly Entry[] Catalogue = {
      new Entry("alpha", "one", "RealmB"),
      new Entry("realm", "two", "RealmHerald"),
      new Entry("realm", "one", "RealmHerald"),
      new Entry("gamma", "one", "RealmA")
    };
    protected override void LoadDefaultMessages() {
      lang.RegisterMessages(new Dictionary<string, string> { { "Speaker", "Realm" }, { "Cmd.alpha", "a" }, { "Cmd.realm", "r" } }, this);
    }
    [ChatCommand("realm")] private void CmdR(Player p, string c, string[] a) { }`),
  });
  try {
    const r = analyse(dir);
    const text = r.problems.join('\n');
    assert.match(text, /says \/alpha belongs to RealmB; RealmA registers it/);
    assert.match(text, /lists \/realm twice/);
    assert.match(text, /\/realm under unknown subject "two"/);
    assert.match(text, /lists \/gamma, which no plugin registers/);
    assert.match(text, /"Cmd\.gamma" \(its one-line description\) is missing/);
    assert.match(text, /"Subject\.one" is missing/);
    assert.match(text, /\/beta \(RealmA\) is missing from the \/realm catalogue/);
    assert.equal(r.chat.catalogue, 4);
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

test('the real plugins keep the chat style and a complete /realm catalogue', () => {
  const r = analyse();
  assert.ok(r.chat.langStrings > 900);
  assert.equal(r.chat.catalogue, Object.keys(r.commands).length - 2);   // all but RealmCourt's two console commands
});
