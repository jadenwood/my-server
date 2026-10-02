// Tests for the loopback static server + chronicle relay. Run: npm test
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { createServer, request } from 'node:http';
import { mkdtemp, writeFile, rm, cp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApp, parseOptions, parseEventQuery, isLoopbackHost, SCENES } from '../server.js';

const FIXTURES = fileURLToPath(new URL('../fixtures', import.meta.url));
const SCHEDULE = fileURLToPath(new URL('../schedule.example.json', import.meta.url));

const listen = (handler) => new Promise((ok) => {
  const s = createServer(handler).listen(0, '127.0.0.1', () => ok(s));
});
const baseOf = (s) => `http://127.0.0.1:${s.address().port}`;

// A stand-in for chronicle/server.js that records what it was asked.
const upstreamHits = [];
let upstreamMode = 'ok';
let fake;
let relay;
let fixtures;

before(async () => {
  fake = await listen((req, res) => {
    upstreamHits.push(req.url);
    if (upstreamMode === 'html') { res.writeHead(500, { 'Content-Type': 'text/html' }); return res.end('<h1>oops</h1>'); }
    if (upstreamMode === 'slow') return; // never answers
    if (req.url.startsWith('/api/state')) {
      res.writeHead(200, { 'Content-Type': 'application/json', 'X-Realm-Data-Warning': 'unreadable: test' });
      return res.end(JSON.stringify({ king: 'Aldric Varrow', houses: [], stale: false }));
    }
    if (req.url.startsWith('/api/events')) {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end(JSON.stringify([{ id: 5, ts: '2026-10-01T00:00:00Z', type: 'decree', title: 't', detail: '', actors: [] }]));
    }
    res.writeHead(404);
    res.end();
  });
  relay = await listen(createApp(parseOptions(['--upstream', baseOf(fake)], {})));
  fixtures = await listen(createApp(parseOptions(['--fixtures', FIXTURES, '--schedule', SCHEDULE], {})));
});
after(() => { fake.close(); relay.close(); fixtures.close(); });

test('options: loopback only, for the bind address and the upstream', () => {
  const d = parseOptions([], {});
  assert.equal(d.host, '127.0.0.1');
  assert.equal(d.port, 8790);
  assert.equal(d.upstream, 'http://127.0.0.1:8787');
  for (const host of ['0.0.0.0', '192.168.1.10', '::', 'example.com']) {
    assert.throws(() => parseOptions(['--host', host], {}), /loopback only/, host);
  }
  assert.throws(() => parseOptions([], { STREAMKIT_HOST: '0.0.0.0' }), /loopback only/);
  for (const up of ['http://10.0.0.2:8787', 'https://127.0.0.1:8787', 'file:///etc/passwd', 'http://127.0.0.1.nip.io:8787', 'nonsense']) {
    assert.throws(() => parseOptions(['--upstream', up], {}), /upstream/, up);
  }
  assert.equal(parseOptions(['--upstream=http://[::1]:9000/x?y'], {}).upstream, 'http://[::1]:9000');
  assert.equal(parseOptions(['--host', '::1'], {}).host, '::1');
  assert.throws(() => parseOptions(['--bogus'], {}), /unknown option/);
  assert.ok(isLoopbackHost('LOCALHOST'));
  assert.ok(!isLoopbackHost('127.0.0.2'));
});

test('event query validation mirrors the chronicle', () => {
  const q = (s) => parseEventQuery(new URLSearchParams(s));
  assert.deepEqual(q(''), { limit: 50, since: null });
  assert.deepEqual(q('limit=9999&since=3'), { limit: 500, since: 3 });
  assert.deepEqual(q('limit=0'), { limit: 1, since: null });
  assert.ok(q('since=abc').error);
  assert.ok(q('since=3;DROP').error);
});

test('relay: forwards only validated since/limit, passes the data warning', async () => {
  upstreamMode = 'ok';
  upstreamHits.length = 0;
  const r = await fetch(`${baseOf(relay)}/api/events?since=4&limit=20&evil=1&path=../../x`);
  assert.equal(r.status, 200);
  assert.deepEqual((await r.json()).map((e) => e.id), [5]);
  assert.deepEqual(upstreamHits, ['/api/events?limit=20&since=4']);
  const s = await fetch(`${baseOf(relay)}/api/state`);
  assert.equal(s.status, 200);
  assert.equal(s.headers.get('x-realm-data-warning'), 'unreadable: test');
  assert.equal((await s.json()).king, 'Aldric Varrow');
  assert.equal((await fetch(`${baseOf(relay)}/api/events?since=x`)).status, 400);
  assert.equal(upstreamHits.length, 2, 'a bad query never reaches the chronicle');
});

test('relay: upstream errors become 502 JSON', async () => {
  upstreamMode = 'html';
  const r = await fetch(`${baseOf(relay)}/api/state`);
  assert.equal(r.status, 502);
  assert.match((await r.json()).error, /not JSON|HTTP 500/);
  upstreamMode = 'ok';
  const dead = await listen(createApp(parseOptions(['--upstream', 'http://127.0.0.1:1'], {})));
  try {
    const d = await fetch(`${baseOf(dead)}/api/state`);
    assert.equal(d.status, 502);
    assert.match((await d.json()).error, /unreachable/);
    const h = await (await fetch(`${baseOf(dead)}/healthz`)).json();
    assert.equal(h.ok, true);
    assert.match(h.chronicle, /unreachable/);
  } finally {
    dead.close();
  }
});

test('relay: a hung chronicle times out', async () => {
  upstreamMode = 'slow';
  const opts = parseOptions(['--upstream', baseOf(fake)], {});
  opts.timeoutMs = 200;
  const s = await listen(createApp(opts));
  try {
    const t0 = Date.now();
    const r = await fetch(`${baseOf(s)}/api/state`);
    assert.equal(r.status, 502);
    assert.ok(Date.now() - t0 < 3000);
  } finally {
    s.close();
    upstreamMode = 'ok';
  }
});

test('fixtures: same API semantics as the chronicle', async () => {
  const base = baseOf(fixtures);
  const s = await (await fetch(`${base}/api/state`)).json();
  assert.equal(s.houses.length, 7);
  const all = await (await fetch(`${base}/api/events?limit=500`)).json();
  assert.equal(all.length, 24);
  assert.deepEqual((await (await fetch(`${base}/api/events?since=21`)).json()).map((e) => e.id), [22, 23, 24]);
  assert.deepEqual((await (await fetch(`${base}/api/events?limit=2`)).json()).map((e) => e.id), [23, 24]);
  const sched = await (await fetch(`${base}/schedule.json`)).json();
  assert.equal(sched.rebellionWindows.length, 2);
  assert.equal((await fetch(`${baseOf(relay)}/schedule.json`)).status, 404, 'no schedule configured');
});

test('fixtures: demo mode appends events over time', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'streamkit-'));
  try {
    await cp(FIXTURES, dir, { recursive: true });
    const opts = parseOptions(['--fixtures', dir, '--demo', '0.05'], {});
    const s = await listen(createApp(opts));
    try {
      const first = await (await fetch(`${baseOf(s)}/api/events?since=24`)).json();
      await new Promise((ok) => setTimeout(ok, 200));
      const later = await (await fetch(`${baseOf(s)}/api/events?since=24`)).json();
      assert.ok(later.length > first.length, 'new demo events appeared');
      assert.equal(later[0].id, 25);
      assert.equal(later[0].type, 'oath_broken');
      const st = await (await fetch(`${baseOf(s)}/api/state`)).json();
      assert.equal(st.houses.find((h) => h.name === 'Merrin').liege, null, 'demo liege patch applied');
    } finally {
      s.close();
    }
    // A broken fixtures file is a 500, not a crash.
    await writeFile(join(dir, 'events.json'), '{ nope');
    const b = await listen(createApp(parseOptions(['--fixtures', dir], {})));
    try {
      assert.equal((await fetch(`${baseOf(b)}/api/events`)).status, 500);
      assert.equal((await fetch(`${baseOf(b)}/war-board`)).status, 200);
    } finally {
      b.close();
    }
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
});

test('pages, assets and security headers', async () => {
  const base = baseOf(fixtures);
  for (const s of SCENES) {
    for (const p of [`/${s}`, `/${s}.html`, `/${s}/`]) {
      const r = await fetch(`${base}${p}`);
      assert.equal(r.status, 200, p);
      assert.match(r.headers.get('content-type'), /text\/html/);
      assert.match(r.headers.get('content-security-policy'), /script-src 'self'/);
    }
  }
  for (const a of ['/', '/assets/kit.js', '/assets/model.js', '/assets/kit.css', '/assets/sigils.js', '/assets/favicon.svg']) {
    assert.equal((await fetch(`${base}${a}`)).status, 200, a);
  }
  assert.match((await fetch(`${base}/assets/kit.js`)).headers.get('content-type'), /javascript/);
  assert.equal((await fetch(`${base}/nope`)).status, 404);
  assert.equal((await fetch(`${base}/api/state`, { method: 'POST' })).status, 405);
  const head = await fetch(`${base}/war-board`, { method: 'HEAD' });
  assert.equal(head.status, 200);
  assert.equal((await head.text()).length, 0);
});

// fetch() normalises "..", so send raw paths with http.request.
function raw(base, path, headers = {}) {
  const u = new URL(base);
  return new Promise((ok, fail) => {
    const req = request({ host: u.hostname, port: u.port, path, headers }, (res) => {
      res.resume();
      res.on('end', () => ok(res.statusCode));
    });
    req.on('error', fail);
    req.end();
  });
}

test('path traversal and DNS rebinding are refused', async () => {
  const base = baseOf(fixtures);
  for (const p of ['/assets/../server.js', '/assets/..%2f..%2fserver.js', '/assets/%2e%2e/%2e%2e/server.js', '/assets/..%5c..%5cserver.js', '/assets/%E0%A4%A']) {
    const code = await raw(base, p);
    assert.ok(code === 404 || code === 400, `${p} -> ${code}`);
  }
  assert.equal(await raw(base, '/war-board', { Host: 'attacker.example:8790' }), 403);
  assert.equal(await raw(base, '/api/state', { Host: '127.0.0.1.nip.io' }), 403);
  assert.equal(await raw(base, '/war-board', { Host: 'localhost:8790' }), 200);
  assert.equal(await raw(base, '/war-board', { Host: '[::1]:8790' }), 200);
});
