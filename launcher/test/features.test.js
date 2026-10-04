// Realm features (lib/features.js): each switch exists in its plugin's PluginConfig, edits change exactly one
// value (64-bit Steam IDs and number formats survive), damaged configs are never touched, backups are made,
// and a running server reloads the plugin over the admin console.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const fsp = require('fs/promises');
const os = require('os');
const path = require('path');
const FT = require('../lib/features');

const PLUGINS = path.join(__dirname, '..', '..', 'plugins');

async function tmp() {
  return fsp.mkdtemp(path.join(os.tmpdir(), 'realm-features-'));
}

// The fields of a plugin's PluginConfig (and the classes it nests), from the C# source: { path: type }.
function configShape(src) {
  const classes = {};
  const re = /class\s+(\w+)\s*\{/g;
  let m;
  while ((m = re.exec(src))) {
    let depth = 1;
    let i = m.index + m[0].length;
    const start = i;
    while (depth && i < src.length) {
      if (src[i] === '{') depth++;
      else if (src[i] === '}') depth--;
      i++;
    }
    classes[m[1]] = src.slice(start, i - 1);
  }
  const out = {};
  const walk = (cls, prefix, depth) => {
    if (depth > 4 || !classes[cls]) return;
    // Only this class's own fields: drop nested class bodies first.
    const body = classes[cls].replace(/class\s+\w+\s*\{[\s\S]*?\n\s{8,}\}/g, '');
    for (const f of body.matchAll(/public\s+([\w<>, [\]]+?)\s+(\w+)\s*(?:=|;)/g)) {
      const [, type, name] = f;
      out[prefix + name] = type;
      if (classes[type] && type !== cls) walk(type, prefix + name + '.', depth + 1);
    }
  };
  walk('PluginConfig', '', 0);
  return out;
}

test('every switch on the screen is a field of that plugin\'s PluginConfig, with the right type', () => {
  const seen = new Set();
  for (const e of FT.CATALOGUE) {
    assert.ok(!seen.has(e.plugin), `${e.plugin} listed once`);
    seen.add(e.plugin);
    assert.ok(FT.GROUPS.includes(e.group), `${e.plugin} group`);
    const src = fs.readFileSync(path.join(PLUGINS, `${e.plugin}.cs`), 'utf8');
    const shape = configShape(src);
    assert.ok(Object.keys(shape).length > 0, `${e.plugin}: PluginConfig found`);
    const paths = new Set();
    for (const s of e.switches) {
      assert.ok(!paths.has(s.path), `${e.plugin} ${s.path} listed once`);
      paths.add(s.path);
      const want = s.kind === 'enum' ? 'string' : 'bool';
      assert.equal(shape[s.path], want, `${e.plugin}.${s.path} is a ${want} field of PluginConfig`);
      assert.ok(s.label && s.label.length <= 40, `${e.plugin}.${s.path} label`);
    }
  }
  // Every Realm plugin with a config is on the screen (RealmCourt has none).
  const all = fs.readdirSync(PLUGINS).filter((f) => f.endsWith('.cs')).map((f) => f.slice(0, -3));
  for (const p of all) if (p !== 'RealmCourt' && /class\s+PluginConfig/.test(fs.readFileSync(path.join(PLUGINS, `${p}.cs`), 'utf8'))) assert.ok(seen.has(p), `${p} has switches on the Realm features screen`);
});

const CONFIG = `{
  "General": {
    "Enabled": true,
    "AdminsExempt": true,
    "ExemptIds": [
      76561198000000123,
      76561199999999999
    ],
    "SaveIntervalSeconds": 120
  },
  "Movement": {
    "Enabled": true,
    "MaxSpeed": 10.0,
    "Tolerance": 1.5e-1,
    "Note": "a \\"quoted\\" word, a } and a {",
    "Enabled2": false
  },
  "Responses": {
    "Mode": "watch",
    "AutoBan": false
  },
  "Empty": {},
  "List": [ { "Enabled": true } ]
}`;

test('locate finds the exact bytes of one value and nothing else', () => {
  const a = FT.locate(CONFIG, ['Movement', 'Enabled']);
  assert.equal(a.raw, 'true');
  assert.equal(CONFIG.slice(a.start, a.end), 'true');
  assert.ok(a.start > CONFIG.indexOf('"Movement"'));
  assert.equal(FT.locate(CONFIG, ['Responses', 'Mode']).value, 'watch');
  assert.equal(FT.locate(CONFIG, ['Movement', 'Nope']), null);
  assert.equal(FT.locate(CONFIG, ['List', 'Enabled']), null, 'never inside a list');
  assert.equal(FT.locate(CONFIG, ['General', 'ExemptIds']).type, 'object');
  assert.throws(() => FT.locate('{"a": tru}', ['a']), SyntaxError);
  assert.throws(() => FT.locate('{"a": true} x', ['a']), /extra text/);
});

test('editConfigText changes one value; Steam IDs and number formats survive byte for byte', () => {
  const r = FT.editConfigText(CONFIG, 'Movement.Enabled', false);
  assert.equal(r.old, true);
  assert.equal(r.changed, true);
  assert.equal(r.text, CONFIG.replace('"Enabled": true,\n    "MaxSpeed"', '"Enabled": false,\n    "MaxSpeed"'));
  assert.ok(r.text.includes('76561199999999999'), 'a 64-bit id is not rounded');
  assert.ok(r.text.includes('10.0') && r.text.includes('1.5e-1'));
  const m = FT.editConfigText(CONFIG, 'Responses.Mode', 'enforce');
  assert.ok(m.text.includes('"Mode": "enforce"'));
  assert.equal(FT.editConfigText(CONFIG, 'General.Enabled', true).changed, false);
  assert.throws(() => FT.editConfigText(CONFIG, 'Movement.MaxSpeed', false), /not a switch/);
  assert.throws(() => FT.editConfigText(CONFIG, 'Movement.Enabled', 'yes'), /on or off/);
  assert.throws(() => FT.editConfigText(CONFIG, 'Responses.Mode', true), /takes a word/);
  assert.throws(() => FT.editConfigText(CONFIG, 'Responses.Mode', 'x"; drop'), /takes a word/);
  assert.throws(() => FT.editConfigText(CONFIG, 'Nope.Enabled', true), /not in this config/);
  assert.throws(() => FT.editConfigText(CONFIG, '__proto__.x', true), /not in this config|not valid/);
  assert.throws(() => FT.editConfigText(CONFIG.slice(0, 120), 'General.Enabled', false), /damaged/);
  assert.throws(() => FT.editConfigText('[1,2]', 'a', true), /not a JSON object/);
  // A BOM (Windows editors) is kept.
  const bom = FT.editConfigText('﻿{"Enabled": true}', 'Enabled', false);
  assert.equal(bom.text, '﻿{"Enabled": false}');
});

async function server(dir) {
  const root = path.join(dir, 'server');
  const ox = path.join(root, 'oxide');
  await fsp.mkdir(path.join(ox, 'config'), { recursive: true });
  await fsp.mkdir(path.join(ox, 'plugins'), { recursive: true });
  fs.writeFileSync(path.join(ox, 'plugins', 'RealmSentinel.cs'), '// plugin');
  fs.writeFileSync(path.join(ox, 'config', 'RealmSentinel.json'), CONFIG);
  fs.writeFileSync(path.join(ox, 'config', 'RealmQuests.json'), '{"Enabled": true, "Dailies');
  return { root, ox };
}

test('listFeatures: what is installed, which configs exist, current values, damaged files flagged', async () => {
  const dir = await tmp();
  const { ox } = await server(dir);
  const list = await FT.listFeatures(ox);
  assert.equal(list.length, FT.CATALOGUE.length);
  const sen = list.find((p) => p.plugin === 'RealmSentinel');
  assert.equal(sen.installed, true);
  assert.equal(sen.config, 'ok');
  assert.deepEqual(sen.switches.find((s) => s.path === 'General.Enabled'), { path: 'General.Enabled', label: 'Sentinel', kind: 'bool', options: null, master: true, danger: false, help: null, present: true, value: true });
  assert.equal(sen.switches.find((s) => s.path === 'Responses.Mode').value, 'watch');
  assert.equal(sen.switches.find((s) => s.path === 'Combat.Enabled').present, false, 'not in this (test) file');
  assert.ok(sen.extra.some((x) => x.path === 'Movement.Enabled2' && x.value === false), 'other switches in the file are listed too');
  assert.ok(!sen.extra.some((x) => x.path === 'List.Enabled'));
  const q = list.find((p) => p.plugin === 'RealmQuests');
  assert.equal(q.config, 'damaged');
  assert.equal(list.find((p) => p.plugin === 'RealmArena').config, 'missing');
});

test('setFeature: backup, atomic write, reload; damaged and unknown refused; nothing else changes', async () => {
  const dir = await tmp();
  const { root, ox } = await server(dir);
  const logs = [];
  const reloads = [];
  const deps = { log: async (e) => logs.push(e), reload: async (p) => (reloads.push(p), { ok: true, result: 'Reloaded plugin' }) };
  const r = await FT.setFeature(root, ox, 'RealmSentinel', 'Movement.Enabled', false, deps);
  assert.equal(r.changed, true);
  assert.deepEqual(reloads, ['RealmSentinel']);
  assert.deepEqual(logs, [{ plugin: 'RealmSentinel', path: 'Movement.Enabled', from: true, to: false }]);
  assert.equal(fs.readFileSync(r.backup, 'utf8'), CONFIG);
  const now = fs.readFileSync(path.join(ox, 'config', 'RealmSentinel.json'), 'utf8');
  assert.equal(now, CONFIG.replace('"Enabled": true,\n    "MaxSpeed"', '"Enabled": false,\n    "MaxSpeed"'));
  assert.ok(!fs.readdirSync(path.join(ox, 'config')).some((n) => n.endsWith('.realm-part')));
  // The mode is an enum.
  await FT.setFeature(root, ox, 'RealmSentinel', 'Responses.Mode', 'enforce', deps);
  await assert.rejects(FT.setFeature(root, ox, 'RealmSentinel', 'Responses.Mode', 'chaos', deps), /one of: watch, enforce/);
  // A bool already in the file but not on the screen may be switched; nothing else.
  assert.equal((await FT.setFeature(root, ox, 'RealmSentinel', 'Movement.Enabled2', true, deps)).changed, true);
  await assert.rejects(FT.setFeature(root, ox, 'RealmSentinel', 'General.SaveIntervalSeconds', true, deps), /not a switch/);
  await assert.rejects(FT.setFeature(root, ox, 'RealmSentinel', 'General.ExemptIds', false, deps), /not a switch/);
  // Same value: no write, no reload.
  reloads.length = 0;
  assert.equal((await FT.setFeature(root, ox, 'RealmSentinel', 'Movement.Enabled', false, deps)).changed, false);
  assert.deepEqual(reloads, []);
  // Damaged, missing, unknown plugin.
  const damaged = fs.readFileSync(path.join(ox, 'config', 'RealmQuests.json'), 'utf8');
  await assert.rejects(FT.setFeature(root, ox, 'RealmQuests', 'Enabled', false, deps), /damaged/);
  assert.equal(fs.readFileSync(path.join(ox, 'config', 'RealmQuests.json'), 'utf8'), damaged, 'a damaged config is never touched');
  await assert.rejects(FT.setFeature(root, ox, 'RealmArena', 'Enabled', false, deps), /has not written its config/);
  await assert.rejects(FT.setFeature(root, ox, 'RealmCourt', 'Enabled', false, deps), /no switches/);
  await assert.rejects(FT.setFeature(root, ox, '../evil', 'Enabled', false, deps), /Unknown plugin/);
  // Without a console (server stopped) there is no reload; the change applies at the next start.
  const r2 = await FT.setFeature(root, ox, 'RealmSentinel', 'General.Enabled', false, { reload: async () => null });
  assert.equal(r2.reload, null);
});

test('registerSteward: list and set over IPC, reload only when the console is up, change written to the Court rolls', async () => {
  const dir = await tmp();
  const { root } = await server(dir);
  const handlers = {};
  const acts = [];
  const rolls = [];
  let consoleUp = false;
  let running = false;
  FT.registerSteward({
    handle: (ch, fn) => (handlers[ch] = fn),
    instOf: (id) => ({ id }),
    rootOf: () => root,
    oxideDir: async (r) => path.join(r, 'oxide'),
    court: { consoleReady: () => consoleUp, act: async (id, a, args) => (acts.push([id, a, args]), { ok: true, result: 'Reloaded' }), courtLog: { append: async (e) => rolls.push(e) } },
    mgr: () => ({ isRunning: () => running, log: () => {} })
  });
  const l = await handlers['features:list']('s1');
  assert.equal(l.running, false);
  assert.equal(l.console, false);
  assert.deepEqual(l.groups, FT.GROUPS);
  let r = await handlers['features:set']('s1', 'RealmSentinel', 'Names.Enabled', false).catch((e) => e);
  assert.match(r.message, /not in this config/);
  r = await handlers['features:set']('s1', 'RealmSentinel', 'Responses.AutoBan', true);
  assert.equal(r.reload, null);
  assert.equal(r.running, false);
  consoleUp = true;
  running = true;
  r = await handlers['features:set']('s1', 'RealmSentinel', 'Responses.AutoBan', false);
  assert.deepEqual(r.reload, { ok: true, result: 'Reloaded' });
  assert.deepEqual(acts, [['s1', 'reload', { plugin: 'RealmSentinel' }]]);
  assert.equal(rolls.length, 2);
  assert.deepEqual([rolls[1].action, rolls[1].target, rolls[1].reason, rolls[1].via], ['feature', 'RealmSentinel', 'Responses.AutoBan: true -> false', 'features']);
});
