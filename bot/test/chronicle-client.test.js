import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { ChronicleUnavailable, cleanEvents, cleanState, createChronicleClient } from '../src/chronicle-client.js';
import { SAMPLE_DIR } from './helpers.js';

const jsonResponse = (body, { status = 200, headers = {} } = {}) => ({
  ok: status >= 200 && status < 300,
  status,
  headers: { get: (k) => headers[k.toLowerCase()] ?? null },
  json: async () => (typeof body === 'string' ? JSON.parse(body) : body),
});

test('state(): reads /api/state, cleans fields, surfaces the data warning header', async () => {
  const urls = [];
  const c = createChronicleClient({
    baseUrl: 'http://127.0.0.1:8787/',
    fetchImpl: async (url) => {
      urls.push(url);
      return jsonResponse({ king: 'K', house: 'Varrow', houses: [{ name: 'Varrow', members: 3, evil: 1 }, { name: '' }, null], online: -4, stale: true }, { headers: { 'x-realm-data-warning': 'unreadable' } });
    },
  });
  const s = await c.state();
  assert.deepEqual(urls, ['http://127.0.0.1:8787/api/state']);
  assert.equal(s.king, 'K');
  assert.deepEqual(s.houses, [{ name: 'Varrow', sigil: null, liege: null, members: 3 }]);
  assert.equal(s.online, 0);
  assert.equal(s.stale, true);
  assert.equal(s.warning, 'unreadable');
});

test('results are cached for a few seconds', async () => {
  let t = 0;
  let calls = 0;
  const c = createChronicleClient({ baseUrl: 'http://x', now: () => t, cacheMs: 5000, fetchImpl: async () => { calls++; return jsonResponse([]); } });
  await c.events(5);
  await c.events(5);
  assert.equal(calls, 1);
  t = 6000;
  await c.events(5);
  assert.equal(calls, 2);
  await c.events(6);
  assert.equal(calls, 3, 'a different query is a different cache entry');
});

test('network failure, HTTP error and non-JSON all become ChronicleUnavailable', async () => {
  const fail = (impl) => createChronicleClient({ baseUrl: 'http://x', fetchImpl: impl });
  await assert.rejects(fail(async () => { throw new TypeError('fetch failed'); }).state(), ChronicleUnavailable);
  await assert.rejects(fail(async () => jsonResponse({}, { status: 500 })).state(), /HTTP 500/);
  await assert.rejects(fail(async () => jsonResponse('not json{')).state(), /not JSON/);
});

test('a hung service times out instead of hanging the command', async () => {
  const c = createChronicleClient({
    baseUrl: 'http://x',
    timeoutMs: 50,
    // The keep-alive timer stands in for a real socket: AbortSignal.timeout() alone does not hold the event loop open.
    fetchImpl: (url, { signal }) => new Promise((_, reject) => {
      const keepAlive = setTimeout(() => {}, 5000);
      signal.addEventListener('abort', () => { clearTimeout(keepAlive); reject(signal.reason); });
    }),
  });
  await assert.rejects(c.state(), /timed out/);
});

test('cleanEvents drops junk and sorts by id', () => {
  const out = cleanEvents([{ id: 3, type: 'decree', title: 't' }, { id: 'x', type: 'decree' }, null, { id: 1, type: 'coronation', actors: ['a', 5] }]);
  assert.deepEqual(out.map((e) => e.id), [1, 3]);
  assert.deepEqual(out[0].actors, ['a']);
  assert.deepEqual(cleanEvents({ not: 'a list' }), []);
  assert.equal(cleanState(null).king, null);
});

test('integration: talks to the real chronicle/server.js over HTTP with its sample data', async (t) => {
  const { createApp } = await import('../../chronicle/server.js');
  const server = createServer(createApp({ dataDir: SAMPLE_DIR, staleAfterSec: 0 }));
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  t.after(() => server.close());
  const c = createChronicleClient({ baseUrl: `http://127.0.0.1:${server.address().port}` });
  const s = await c.state();
  assert.equal(s.king, 'Aldric Varrow');
  assert.equal(s.houses.length, 6);
  assert.equal(s.stale, false);
  const ev = await c.events(3);
  assert.equal(ev.length, 3);
  assert.ok(ev[0].id < ev[2].id, 'oldest first');
  const h = await c.health();
  assert.equal(h.ok, true);
});
