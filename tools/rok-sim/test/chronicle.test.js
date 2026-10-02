'use strict';

// Fake RealmChronicle output, checked against the real readers in this repo (read-only use):
// chronicle/server.js serves it, and the closed list of event types in the plugin, the server and
// the overlay must accept every type the simulator writes.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const http = require('http');
const { pathToFileURL } = require('url');
const H = require('./helpers');
const { ChronicleWriter, KNOWN_TYPES, LIMITS, demoStory } = require('../lib/chronicle');

const REPO = path.resolve(__dirname, '../../..');
const CHRONICLE_SERVER = path.join(REPO, 'chronicle', 'server.js');
const PLUGIN = path.join(REPO, 'plugins', 'RealmChronicle.cs');
const COMMON = path.join(REPO, 'chronicle', 'public', 'assets', 'common.js');

test('every simulator event type is in the closed list of all three consumers', () => {
  const sets = {};
  if (fs.existsSync(PLUGIN)) {
    const block = /KnownTypes\s*=\s*\{([\s\S]*?)\};/.exec(fs.readFileSync(PLUGIN, 'utf8'));
    sets.plugin = new Set([...block[1].matchAll(/"([a-z_]+)"/g)].map((m) => m[1]));
  }
  if (fs.existsSync(CHRONICLE_SERVER)) {
    const block = /EVENT_TYPES\s*=\s*new Set\(\[([\s\S]*?)\]\)/.exec(fs.readFileSync(CHRONICLE_SERVER, 'utf8'));
    sets.server = new Set([...block[1].matchAll(/'([a-z_]+)'/g)].map((m) => m[1]));
  }
  if (fs.existsSync(COMMON)) {
    sets.overlay = new Set([...fs.readFileSync(COMMON, 'utf8').matchAll(/^\s*([a-z_]+):\s*\{\s*label:/gm)].map((m) => m[1]));
  }
  assert.ok(Object.keys(sets).length > 0, 'at least one consumer present');
  for (const [who, set] of Object.entries(sets)) {
    assert.ok(set.size >= KNOWN_TYPES.length, `${who} list parsed`);
    for (const t of KNOWN_TYPES) assert.ok(set.has(t), `${who} does not know "${t}"`);
  }
  for (let i = 0; i < 40; i++) assert.ok(KNOWN_TYPES.includes(demoStory(i).ev[0]));
});

test('writer: ids continue across restarts, limits, unknown types, duplicate window, retention', () => {
  const dir = H.tmpDir();
  let t = Date.parse('2026-10-02T10:00:00Z');
  const now = () => new Date(t);
  const w = new ChronicleWriter({ dataDir: dir, now, maxEvents: 5 });
  assert.equal(w.log('house_founded', 'House Varrow is founded', 'Iron Stag', ['Aldric Varrow']).id, 1);
  assert.equal(w.log('not_a_type', 'x'), null);
  assert.equal(w.log('decree', '   '), null);
  assert.equal(w.log('house_founded', 'House Varrow is founded', 'Iron Stag'), null, 'duplicate within 5 minutes');
  t += 301000;
  assert.equal(w.log('house_founded', 'House Varrow is founded', 'Iron Stag').id, 2, 'allowed again after the window');
  const long = w.log('decree', 'T'.repeat(500), 'D'.repeat(900), Array.from({ length: 12 }, (_, i) => `Actor ${i} ${'n'.repeat(80)}`));
  assert.equal(long.title.length, LIMITS.title);
  assert.equal(long.detail.length, LIMITS.detail);
  assert.equal(long.actors.length, LIMITS.actors);
  assert.ok(long.actors.every((a) => a.length <= LIMITS.actor));
  assert.equal(long.ts, '2026-10-02T10:05:01Z');
  for (let i = 0; i < 6; i++) w.log('decree', `Decree ${i}`);
  const file = JSON.parse(fs.readFileSync(path.join(dir, 'RealmChronicle.json'), 'utf8'));
  assert.equal(file.length, 5, 'retention cap');
  assert.ok(fs.readFileSync(path.join(dir, 'RealmChronicle.json'), 'utf8').includes('\r\n'));
  const w2 = new ChronicleWriter({ dataDir: dir, now });
  assert.equal(w2.log('decree', 'After restart').id, file[file.length - 1].id + 1);
});

test('torn writes leave half a file for a moment, then the whole one', async () => {
  const dir = H.tmpDir();
  const w = new ChronicleWriter({ dataDir: dir, tornWrites: true });
  w.log('decree', 'A torn decree');
  assert.throws(() => JSON.parse(fs.readFileSync(path.join(dir, 'RealmChronicle.json'), 'utf8')));
  await H.wait(250);
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, 'RealmChronicle.json'), 'utf8'))[0].title, 'A torn decree');
});

function get(port, p) {
  return new Promise((resolve, reject) => {
    http.get({ host: '127.0.0.1', port, path: p }, (res) => {
      let b = '';
      res.on('data', (d) => (b += d));
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: JSON.parse(b) }));
    }).on('error', reject);
  });
}

test('demo mode end to end: the real chronicle server serves what the simulator writes', { skip: !fs.existsSync(CHRONICLE_SERVER) && 'chronicle/ not in this tree' }, async () => {
  const { dir } = await H.preparedFolder({ maxPlayers: 40 });
  const s = await H.startReady(dir, [], { chronicle: 'demo', chronicleEveryMs: 20, players: ['Aldric Varrow', 'Wren', 'Odo the Tall'] });
  const mod = await import(pathToFileURL(CHRONICLE_SERVER).href);
  const srv = http.createServer(mod.createApp({ dataDir: path.join(dir, 'oxide', 'data'), staleAfterSec: 180 }));
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const port = srv.address().port;
  try {
    await H.until(() => s.chronicle && s.chronicle.events.length >= 14, 4000, '14 demo events');
    await H.until(() => s.players.length === 3, 2000, 'players');
    await H.wait(350); // the queued state refresh after the joins
    await H.stopSim(s); // freeze the files (a kill writes nothing more), so file and API can be compared
    const ev = await get(port, '/api/events?limit=500');
    assert.equal(ev.status, 200);
    assert.equal(ev.headers['x-realm-data-warning'], undefined);
    const fileEvents = JSON.parse(fs.readFileSync(path.join(dir, 'oxide', 'data', 'RealmChronicle.json'), 'utf8'));
    assert.equal(ev.body.length, fileEvents.length, 'the server accepts every event the simulator wrote');
    assert.deepEqual(ev.body.slice(0, 6).map((e) => e.type), Array(6).fill('house_founded'));
    assert.ok(ev.body.some((e) => e.type === 'coronation'));
    const st = await get(port, '/api/state');
    assert.equal(st.status, 200);
    assert.equal(st.body.online, 3);
    assert.equal(st.body.maxPlayers, 40);
    assert.equal(st.body.stale, false, 'updated within the last 180 s');
    assert.equal(st.body.houses.length, 6);
    assert.equal(st.body.houses.reduce((n, h) => n + h.members, 0), 3);
    assert.ok(st.body.king, 'a king was crowned in the demo story');
    assert.match(st.body.since, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
  } finally {
    srv.close();
    await H.stopSim(s);
  }
});

test('console-driven events and crown; events refused when the chronicle is off', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)], { chronicle: 'on' });
  const c = await H.connectConsole(cport);
  try {
    const r = await c.command('/sim.event treaty_signed Varrow and Corvane sign the Marsh Accord | Sealed at the Hearth. | Aldric Varrow, Ysolde Corvane');
    assert.deepEqual(r.lines, ['[I] SIM event 1']);
    assert.deepEqual((await c.command('/sim.event bogus_type Nope')).lines, ['[I] SIM event refused']);
    await c.command('/sim.crown "Ysolde Corvane" Corvane');
    const state = JSON.parse(fs.readFileSync(path.join(dir, 'oxide', 'data', 'RealmState.json'), 'utf8'));
    assert.equal(state.king, 'Ysolde Corvane');
    assert.equal(state.house, 'Corvane');
    const events = JSON.parse(fs.readFileSync(path.join(dir, 'oxide', 'data', 'RealmChronicle.json'), 'utf8'));
    assert.deepEqual(events[0].actors, ['Aldric Varrow', 'Ysolde Corvane']);
    assert.equal(events[0].detail, 'Sealed at the Hearth.');
  } finally {
    c.close();
    await H.stopSim(s);
  }
  const off = await H.preparedFolder();
  const s2 = await H.startReady(off.dir, [], {});
  try {
    assert.equal(s2.chronicleEvent('decree', 'x'), null);
    assert.ok(!fs.existsSync(path.join(off.dir, 'oxide')));
  } finally {
    await H.stopSim(s2);
  }
});
