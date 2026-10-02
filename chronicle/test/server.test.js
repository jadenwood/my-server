// Smoke tests against sample-data. Run: npm test
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdtemp, writeFile, rm, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApp, parseOptions, sanitizeEvent, sanitizeState, sanitizeNext, EVENT_TYPES } from '../server.js';

const SAMPLE = fileURLToPath(new URL('../sample-data', import.meta.url));
let server;
let base;

function listen(app) {
  return new Promise((ok) => {
    const s = createServer(app).listen(0, '127.0.0.1', () => ok(s));
  });
}

before(async () => {
  server = await listen(createApp(parseOptions(['--data', SAMPLE, '--stale', '0'], {})));
  base = `http://127.0.0.1:${server.address().port}`;
});
after(() => server.close());

test('defaults bind to loopback on 8787', () => {
  const o = parseOptions([], {});
  assert.equal(o.host, '127.0.0.1');
  assert.equal(o.port, 8787);
});

test('/api/state returns the contract shape', async () => {
  const s = await (await fetch(`${base}/api/state`)).json();
  assert.equal(s.king, 'Aldric Varrow');
  assert.equal(s.house, 'Varrow');
  assert.equal(s.houses.length, 6);
  assert.deepEqual(Object.keys(s.houses[0]).sort(), ['liege', 'members', 'name', 'sigil']);
  assert.equal(typeof s.online, 'number');
  assert.equal(s.stale, false);
  assert.deepEqual(s.next, { title: 'Rebellion window', at: '2026-10-03T19:00:00Z' });
});

test('/api/events: latest, since and limit', async () => {
  const all = await (await fetch(`${base}/api/events?limit=500`)).json();
  assert.equal(all.length, 18);
  const since = await (await fetch(`${base}/api/events?since=15`)).json();
  assert.deepEqual(since.map((e) => e.id), [16, 17, 18]);
  const last2 = await (await fetch(`${base}/api/events?limit=2`)).json();
  assert.deepEqual(last2.map((e) => e.id), [17, 18]);
  const bad = await fetch(`${base}/api/events?since=abc`);
  assert.equal(bad.status, 400);
});

test('pages and assets are served, traversal is refused', async () => {
  for (const p of ['/overlay', '/realm', '/assets/common.js', '/assets/theme.css']) {
    assert.equal((await fetch(`${base}${p}`)).status, 200, p);
  }
  assert.equal((await fetch(`${base}/assets/..%2f..%2fserver.js`)).status, 404);
  assert.equal((await fetch(`${base}/api/state`, { method: 'POST' })).status, 405);
});

test('unknown fields and bad events are dropped; half-written files keep the last good copy', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'realm-'));
  try {
    await writeFile(join(dir, 'RealmChronicle.json'), JSON.stringify([
      { id: 1, ts: '2026-10-02T10:00:00', type: 'decree', title: 'T', detail: 'D', actors: ['A', 5], pos: [1, 2, 3] },
      { id: 2, ts: 'x', type: 'teleport', title: 'nope', actors: [] },
    ]));
    await writeFile(join(dir, 'RealmState.json'), JSON.stringify({ king: null, houses: [{ name: 'X', members: 2, secret: 1 }] }));
    const s2 = await listen(createApp(parseOptions(['--data', dir], {})));
    const b2 = `http://127.0.0.1:${s2.address().port}`;
    const ev = await (await fetch(`${b2}/api/events`)).json();
    assert.deepEqual(ev, [{ id: 1, ts: '2026-10-02T10:00:00Z', type: 'decree', title: 'T', detail: 'D', actors: ['A'] }]);
    const st = await (await fetch(`${b2}/api/state`)).json();
    assert.equal(st.king, null);
    assert.equal(st.since, null);
    assert.equal(st.stale, true);
    assert.deepEqual(st.houses, [{ name: 'X', sigil: null, liege: null, members: 2 }]);

    await writeFile(join(dir, 'RealmChronicle.json'), '[{"id": 1, "ty');
    const again = await fetch(`${b2}/api/events`);
    assert.equal(again.status, 200);
    assert.ok(again.headers.get('x-realm-data-warning'));
    assert.equal((await again.json()).length, 1);
    s2.close();
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('missing data dir still serves empty but valid responses', async () => {
  const s3 = await listen(createApp(parseOptions(['--data', join(tmpdir(), 'realm-does-not-exist')], {})));
  const b3 = `http://127.0.0.1:${s3.address().port}`;
  assert.deepEqual(await (await fetch(`${b3}/api/events`)).json(), []);
  const st = await (await fetch(`${b3}/api/state`)).json();
  assert.equal(st.king, null);
  s3.close();
});

test('contract events are accepted, unknown types are still dropped', () => {
  for (const type of ['contract_posted', 'contract_fulfilled', 'contract_ended']) {
    const e = sanitizeEvent({ id: 7, ts: '2026-10-02T10:00:00Z', type, title: 'A price on Wren', detail: 'D', actors: ['Wren'] });
    assert.equal(e && e.type, type);
  }
  assert.equal(sanitizeEvent({ id: 8, ts: '2026-10-02T10:00:00Z', type: 'contract_secret', title: 'x', actors: [] }), null);
});

test('event types match the RealmChronicle plugin and the page labels', async () => {
  const cs = await readFile(new URL('../../plugins/RealmChronicle.cs', import.meta.url), 'utf8');
  const block = cs.match(/KnownTypes\s*=\s*\{([^}]*)\}/);
  assert.ok(block, 'KnownTypes array not found in RealmChronicle.cs');
  const pluginTypes = [...block[1].matchAll(/"([a-z_]+)"/g)].map((m) => m[1]).sort();
  assert.deepEqual(pluginTypes, [...EVENT_TYPES].sort());

  const js = await readFile(new URL('../public/assets/common.js', import.meta.url), 'utf8');
  const meta = js.match(/TYPE_META = \{([\s\S]*?)\n\};/);
  assert.ok(meta, 'TYPE_META not found in common.js');
  const labelled = [...meta[1].matchAll(/^\s*([a-z_]+):/gm)].map((m) => m[1]).sort();
  assert.deepEqual(labelled, [...EVENT_TYPES].sort());
});

test('next event: valid {title, at} passes through, normalized to UTC', () => {
  assert.deepEqual(sanitizeNext({ title: 'Crown Night', at: '2026-10-03T19:00:00Z' }), { title: 'Crown Night', at: '2026-10-03T19:00:00Z' });
  assert.deepEqual(sanitizeNext({ title: ' Truce ', at: '2026-10-03T21:00:00+02:00', extra: 1 }), { title: 'Truce', at: '2026-10-03T19:00:00Z' });
  // No zone: read as UTC, like every other plugin timestamp.
  assert.deepEqual(sanitizeNext({ title: 'Royal Tournament', at: '2026-10-03T19:00:00' }), { title: 'Royal Tournament', at: '2026-10-03T19:00:00Z' });
  assert.deepEqual(sanitizeNext({ title: 'x'.repeat(80), at: '2026-10-03T19:00:00Z' }).title.length, 80);
});

test('next event: bad titles and times become null', () => {
  const at = '2026-10-03T19:00:00Z';
  for (const n of [
    undefined, null, 'Crown Night', [], {},
    { title: 'x'.repeat(81), at },
    { title: '', at }, { title: '   ', at }, { title: 5, at }, { at },
    { title: 'Crown Night' }, { title: 'Crown Night', at: 'soon' }, { title: 'Crown Night', at: 1790000000000 },
    { title: 'Crown Night', at: '2026-13-40T99:00:00Z' }, { title: 'Crown Night', at: 'Sat, 03 Oct 2026 19:00:00 GMT' },
  ]) assert.equal(sanitizeNext(n), null, JSON.stringify(n));
  assert.equal(sanitizeState({}).next, null);
  assert.equal(sanitizeState({ next: { title: 'Crown Night', at: 'never' } }).next, null);
});

test('next event title limit and field names match the RealmChronicle plugin', async () => {
  const cs = await readFile(new URL('../../plugins/RealmChronicle.cs', import.meta.url), 'utf8');
  assert.match(cs, /NextTitleMax\s*=\s*80\s*;/);
  assert.match(cs, /public NextEvent next;/);
  assert.match(cs, /class NextEvent\s*\{\s*public string title;\s*public string at;\s*\}/);
});
