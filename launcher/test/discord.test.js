// Discord herald (lib/discord.js): URL validation, embeds, the rate-limited queue with retries, the
// settings store and the chronicle watcher. No real network: every send goes to a stub, and time is
// a fake clock, so these tests never wait.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const D = require('../lib/discord');

const ID = '123456789012345678';
const TOKEN = 'AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-AbCdEfGhIjKlMnOpQrStUvWxYz01';
const GOOD = `https://discord.com/api/webhooks/${ID}/${TOKEN}`;

function fakeClock(start = 1_000_000) {
  const c = { t: start, slept: [] };
  c.now = () => c.t;
  c.sleep = async (ms) => {
    c.slept.push(ms);
    c.t += Math.max(0, ms);
  };
  return c;
}
const ev = (id, type = 'decree', extra = {}) => ({ id, ts: '2026-10-01T20:58:27Z', type, title: `Event ${id}`, detail: 'Something happened.', actors: ['Aldric Varrow'], ...extra });

// ---------------------------------------------------------------- URL validation

test('webhook URL: accepts discord.com and discordapp.com webhooks, normalises them', () => {
  assert.deepEqual(D.validateWebhookUrl(GOOD), { ok: true, url: GOOD, id: ID });
  assert.equal(D.validateWebhookUrl(`  https://discordapp.com/api/webhooks/${ID}/${TOKEN}/  `).url, `https://discordapp.com/api/webhooks/${ID}/${TOKEN}`);
  assert.equal(D.validateWebhookUrl(`https://DISCORD.com/api/v10/webhooks/${ID}/${TOKEN}`).url, GOOD);
});

test('webhook URL: rejects everything else', () => {
  const bad = [
    '',
    null,
    'discord.com/api/webhooks/1/2',
    `http://discord.com/api/webhooks/${ID}/${TOKEN}`,
    `https://discord.com.evil.test/api/webhooks/${ID}/${TOKEN}`,
    `https://evil.test/api/webhooks/${ID}/${TOKEN}`,
    `https://ptb.discord.com/api/webhooks/${ID}/${TOKEN}`,
    `https://user:pw@discord.com/api/webhooks/${ID}/${TOKEN}`,
    `https://discord.com:8443/api/webhooks/${ID}/${TOKEN}`,
    `https://discord.com/api/webhooks/${ID}/${TOKEN}?wait=true`,
    `https://discord.com/api/webhooks/${ID}/${TOKEN}#x`,
    `https://discord.com/api/webhooks/${ID}`,
    `https://discord.com/api/webhooks/notanid/${TOKEN}`,
    `https://discord.com/api/webhooks/${ID}/${TOKEN}/slack`,
    `https://discord.com/channels/${ID}/${ID}`,
    'https://discord.gg/invite',
    `javascript:fetch('https://discord.com/api/webhooks/${ID}/${TOKEN}')`,
    `https://discord.com/api/webhooks/${ID}/${'a'.repeat(400)}`
  ];
  for (const u of bad) {
    const r = D.validateWebhookUrl(u);
    assert.equal(r.ok, false, `rejected: ${u}`);
    assert.ok(r.error && !r.error.includes(TOKEN), 'error never echoes the token');
  }
});

test('redaction and scrubbing never reveal the token', () => {
  const hint = D.redactWebhookUrl(GOOD);
  assert.ok(hint.includes(ID));
  assert.ok(!hint.includes(TOKEN));
  assert.equal(D.redactWebhookUrl('nonsense'), '');
  const s = D.scrubSecrets(`fetch failed for ${GOOD} (ECONNRESET)`);
  assert.ok(!s.includes(TOKEN));
  assert.ok(s.includes('ECONNRESET'));
});

// ---------------------------------------------------------------- embeds

test('embed: house colour, fields, footer, timestamp and portal link', () => {
  const e = D.buildEmbed(
    { id: 16, ts: '2026-09-30T19:42:10Z', type: 'coronation', title: 'Aldric Varrow of Varrow takes the throne', detail: 'The crown passes from Ysolde Corvane to Aldric Varrow.', actors: ['Aldric Varrow', 'Ysolde Corvane'] },
    { realmName: 'Ostreval', portalUrl: 'https://realm.test/', houseNames: ['Varrow', 'Corvane'] }
  );
  assert.equal(e.color, 0x4a2347, 'Varrow is plum, as on the overlay and in lore.md');
  assert.equal(e.author.name, 'Coronation');
  assert.equal(e.timestamp, '2026-09-30T19:42:10.000Z');
  assert.equal(e.url, 'https://realm.test/chronicle.html#e16');
  assert.equal(e.footer.text, 'Ostreval · Chronicle #16');
  assert.deepEqual(e.fields.map((f) => f.name), ['House', 'Named in the record']);
  assert.equal(e.fields[0].value, 'House Varrow');
});

test('embed: "House X" in the title wins; roster actors are the fallback; tone colour otherwise', () => {
  const names = ['Grey', 'Grey Water', 'Dunmere'];
  assert.equal(D.houseForEvent({ title: 'House Grey Water swears fealty', detail: '', actors: [] }, { houseNames: names }), 'Grey Water');
  assert.equal(D.houseForEvent({ title: 'A ransom is set', detail: '', actors: ['Bram'] }, { houseNames: names, rosterIndex: new Map([['bram', 'Dunmere']]) }), 'Dunmere');
  const plain = D.buildEmbed({ id: 1, type: 'oath_broken', title: 'Someone breaks faith', detail: '', actors: [] }, {});
  assert.equal(plain.color, 0x8b2b22);
  assert.equal(plain.fields, undefined);
  const unknown = D.buildEmbed({ id: 2, type: 'brand_new_thing', title: 'X', actors: [] }, {});
  assert.equal(unknown.author.name, 'brand new thing');
});

test('embed: limits are enforced', () => {
  const e = D.buildEmbed({ id: 1, type: 'decree', title: 'T'.repeat(1000), detail: 'D'.repeat(10000), actors: Array(20).fill('A'.repeat(300)) }, { realmName: 'R'.repeat(5000) });
  assert.ok(e.title.length <= D.LIMITS.title);
  assert.ok(e.description.length <= D.LIMITS.description);
  assert.ok(e.footer.text.length <= D.LIMITS.footer);
  for (const f of e.fields) assert.ok(f.value.length <= D.LIMITS.fieldValue);
  assert.ok(D.embedSize(e) <= D.LIMITS.total);
});

test('embed: player text cannot ping, link or format; Steam-id-like actors are hidden', () => {
  const e = D.buildEmbed({ id: 1, type: 'house_founded', title: '@everyone House [Free Gold](https://evil.test) is founded', detail: '<@123456789012345678> **bold** @here', actors: ['76561190000000003', 'Wren'] }, {});
  assert.ok(!/@everyone/.test(e.title.replace(/​/g, 'X')) || e.title.includes('@​everyone'));
  assert.ok(e.title.includes('\\[Free Gold\\]'));
  assert.ok(!e.title.includes('https://'));
  assert.ok(e.description.includes('<@​123456789012345678>'));
  assert.ok(e.description.includes('\\*\\*bold\\*\\*'));
  assert.equal(e.fields.find((f) => f.name === 'Named').value, 'Wren');
  const p = D.buildPayload([e], { realmName: 'Discord Realm' });
  assert.deepEqual(p.allowed_mentions, { parse: [] });
  assert.ok(!/discord/i.test(p.username), 'Discord rejects usernames containing "discord"');
});

// ---------------------------------------------------------------- queue

test('queue: batches up to 10 embeds per message and spaces requests out', async () => {
  const clock = fakeClock();
  const calls = [];
  const q = new D.RelayQueue({ clock, send: async (p) => (calls.push({ at: clock.now(), n: p.embeds.length }), { status: 204 }) });
  q.enqueue(Array.from({ length: 23 }, (_, i) => D.buildEmbed(ev(i + 1))));
  await q.drain();
  assert.deepEqual(calls.map((c) => c.n), [10, 10, 3]);
  assert.ok(calls[1].at - calls[0].at >= 2000 && calls[2].at - calls[1].at >= 2000, 'at least 2 s between requests');
  assert.equal(q.stats.sent, 23);
  assert.equal(q.length, 0);
});

test('queue: never more than 20 requests in any minute', async () => {
  const clock = fakeClock();
  const times = [];
  const q = new D.RelayQueue({ clock, minIntervalMs: 0, send: async () => (times.push(clock.now()), { status: 204 }) });
  for (let i = 0; i < 25; i++) await q.deliver({ embeds: [] });
  assert.equal(times.length, 25);
  for (let i = 20; i < times.length; i++) assert.ok(times[i] - times[i - 20] >= 60000, `request ${i} waited for the minute window`);
});

test('queue: a 6000-character message limit splits big batches', async () => {
  const clock = fakeClock();
  const sizes = [];
  const q = new D.RelayQueue({ clock, send: async (p) => (sizes.push(p.embeds.reduce((n, e) => n + D.embedSize(e), 0)), { status: 204 }) });
  q.enqueue(Array.from({ length: 6 }, (_, i) => D.buildEmbed(ev(i + 1, 'decree', { detail: 'x'.repeat(2500) }))));
  await q.drain();
  assert.ok(sizes.length >= 3);
  for (const s of sizes) assert.ok(s <= D.LIMITS.total);
});

test('queue: 429 waits for retry_after and retries the same message', async () => {
  const clock = fakeClock();
  const replies = [{ status: 429, retryAfterMs: 7500 }, { status: 204 }];
  const at = [];
  const q = new D.RelayQueue({ clock, send: async () => (at.push(clock.now()), replies.shift()) });
  q.enqueue([D.buildEmbed(ev(1))]);
  await q.drain();
  assert.equal(at.length, 2);
  assert.ok(at[1] - at[0] >= 7500);
  assert.equal(q.stats.sent, 1);
  assert.equal(q.stats.failed, 0);
});

test('queue: X-RateLimit-Remaining 0 pauses until the bucket resets', async () => {
  const clock = fakeClock();
  const at = [];
  const q = new D.RelayQueue({ clock, minIntervalMs: 0, send: async () => (at.push(clock.now()), { status: 204, remaining: 0, resetAfterMs: 4000 }) });
  await q.deliver({});
  await q.deliver({});
  assert.ok(at[1] - at[0] >= 4000);
});

test('queue: 5xx and network errors back off exponentially, then give up', async () => {
  const clock = fakeClock();
  let n = 0;
  const logs = [];
  const q = new D.RelayQueue({ clock, minIntervalMs: 0, maxAttempts: 4, baseBackoffMs: 1000, log: (l, m) => logs.push(m), send: async () => (n++, n % 2 ? { status: 502 } : { status: 0, error: `connect ECONNREFUSED ${GOOD}` }) });
  q.enqueue([D.buildEmbed(ev(1))]);
  await q.drain();
  assert.equal(n, 4);
  assert.deepEqual(clock.slept.filter((ms) => ms >= 1000), [1000, 2000, 4000]);
  assert.equal(q.stats.failed, 1);
  assert.match(q.stats.lastError, /Could not reach Discord/);
  assert.ok(!q.stats.lastError.includes(TOKEN) && !logs.join(' ').includes(TOKEN), 'token scrubbed from errors and logs');
});

test('queue: a transient failure recovers', async () => {
  const clock = fakeClock();
  const replies = [{ status: 500 }, { status: 0, error: 'ETIMEDOUT' }, { status: 200 }];
  const q = new D.RelayQueue({ clock, send: async () => replies.shift() });
  q.enqueue([D.buildEmbed(ev(1))]);
  await q.drain();
  assert.equal(q.stats.sent, 1);
  assert.equal(q.stats.lastError, null);
});

test('queue: 404/401 is fatal: queue cleared, onFatal called, no retries', async () => {
  const clock = fakeClock();
  let calls = 0;
  let fatal = null;
  const q = new D.RelayQueue({ clock, send: async () => (calls++, { status: 404 }), onFatal: (r) => (fatal = r) });
  q.enqueue(Array.from({ length: 15 }, (_, i) => D.buildEmbed(ev(i + 1))));
  await q.drain();
  assert.equal(calls, 1);
  assert.ok(fatal && fatal.fatal);
  assert.equal(q.length, 0);
  assert.match(q.stats.lastError, /does not exist/);
});

test('queue: other 4xx drops that message and carries on', async () => {
  const clock = fakeClock();
  const replies = [{ status: 400, error: 'Invalid Form Body' }, { status: 204 }];
  const q = new D.RelayQueue({ clock, send: async () => replies.shift() });
  q.enqueue(Array.from({ length: 12 }, (_, i) => D.buildEmbed(ev(i + 1))));
  await q.drain();
  assert.equal(q.stats.failed, 10);
  assert.equal(q.stats.sent, 2);
});

test('queue: bounded; the oldest are dropped first', async () => {
  const clock = fakeClock();
  const seen = [];
  let release;
  const gate = new Promise((r) => (release = r));
  const q = new D.RelayQueue({ clock, maxQueue: 5, send: async (p) => (await gate, seen.push(...p.embeds.map((e) => e.footer.text)), { status: 204 }) });
  q.enqueue([D.buildEmbed(ev(1))]); // in flight
  q.enqueue(Array.from({ length: 8 }, (_, i) => D.buildEmbed(ev(i + 2))));
  assert.equal(q.stats.dropped, 3);
  release();
  await q.drain();
  assert.deepEqual(seen.map((s) => s.replace(/.*#/, '')), ['1', '5', '6', '7', '8', '9']);
});

test('sender: maps fetch responses (stubbed fetch, no network)', async () => {
  const seen = [];
  const fake = async (url, init) => {
    seen.push({ url, init });
    const headers = new Map([['x-ratelimit-remaining', '0'], ['x-ratelimit-reset-after', '1.5']]);
    return { status: 429, headers: { get: (k) => headers.get(k) || null }, json: async () => ({ retry_after: 2.25 }) };
  };
  const send = D.makeSender(() => GOOD, fake);
  const r = await send({ embeds: [] });
  assert.deepEqual(r, { status: 429, remaining: 0, resetAfterMs: 1500, retryAfterMs: 2250 });
  assert.equal(seen[0].init.method, 'POST');
  assert.equal(seen[0].init.redirect, 'error');
  const thrown = await D.makeSender(() => GOOD, async () => { throw new Error(`getaddrinfo ENOTFOUND for ${GOOD}`); })({});
  assert.equal(thrown.status, 0);
  assert.ok(!thrown.error.includes(TOKEN));
  assert.deepEqual(await D.makeSender(() => null, fake)({}), { status: 0, error: 'no webhook URL saved' });
});

// ---------------------------------------------------------------- store and watcher

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'realm-discord-'));
}
const fakeCrypt = { available: () => true, encrypt: (t) => Buffer.from([...t].reverse().join('')).toString('base64'), decrypt: (b) => [...Buffer.from(b, 'base64').toString()].reverse().join('') };

test('store: the webhook is saved encrypted when possible, never in plain text', async () => {
  const dir = tmpDir();
  const s = new D.DiscordStore(dir, fakeCrypt);
  await s.load();
  s.setUrl(GOOD);
  await s.save();
  const raw = fs.readFileSync(path.join(dir, 'realm-discord.json'), 'utf8');
  assert.ok(!raw.includes(TOKEN), 'token not in the file as plain text');
  const again = new D.DiscordStore(dir, fakeCrypt);
  await again.load();
  assert.equal(again.url(), GOOD);
  assert.equal(again.encrypted(), true);
  // Copied to another account (cannot decrypt): treated as no webhook, relay stays off.
  const other = new D.DiscordStore(dir, { available: () => true, encrypt: () => '', decrypt: () => { throw new Error('DPAPI'); } });
  await other.load();
  assert.equal(other.url(), null);
  assert.throws(() => s.setUrl('https://evil.test/api/webhooks/1/2'), /discord\.com/);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('store: falls back to plain storage when encryption is unavailable', async () => {
  const dir = tmpDir();
  const s = new D.DiscordStore(dir, null);
  await s.load();
  s.setUrl(GOOD);
  assert.equal(s.encrypted(), false);
  assert.equal(s.url(), GOOD);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('watcher: primes on first run (no history), then relays only new, selected events', async () => {
  const dir = tmpDir();
  const data = path.join(dir, 'data');
  fs.mkdirSync(data);
  const write = (list) => fs.writeFileSync(path.join(data, 'RealmChronicle.json'), JSON.stringify(list));
  fs.writeFileSync(path.join(data, 'RealmState.json'), JSON.stringify({ houses: [{ name: 'Varrow' }] }));
  write([ev(1), ev(2)]);
  const store = new D.DiscordStore(dir, fakeCrypt);
  await store.load();
  store.setUrl(GOOD);
  store.data.enabled = true;
  store.data.types = ['decree', 'coronation'];
  const queued = [];
  const relay = new D.ChronicleRelay({ store, queue: { enqueue: (e) => queued.push(...e) }, dataDir: () => data });
  assert.equal(await relay.tick(), 0, 'first run posts nothing');
  assert.equal(store.data.lastId, 2);
  write([ev(1), ev(2), ev(3, 'decree', { title: 'House Varrow decrees' }), ev(4, 'oath_sworn'), ev(5, 'coronation')]);
  assert.equal(await relay.tick(), 2);
  assert.deepEqual(queued.map((e) => e.footer.text.replace(/.*#/, '')), ['3', '5']);
  assert.equal(queued[0].color, 0x4a2347);
  assert.equal(store.data.lastId, 5);
  assert.equal(await relay.tick(), 0, 'nothing twice');
  // Chronicle wiped (ids restart): re-prime, do not replay.
  write([ev(1)]);
  assert.equal(await relay.tick(), 0);
  assert.equal(store.data.lastId, 1);
  // Disabled: position advances but nothing is posted.
  store.data.enabled = false;
  write([ev(1), ev(2)]);
  assert.equal(await relay.tick(), 0);
  assert.equal(store.data.lastId, 2);
  assert.equal(queued.length, 2);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('watcher: missing or half-written chronicle is ignored', async () => {
  const dir = tmpDir();
  const store = new D.DiscordStore(dir, null);
  await store.load();
  const relay = new D.ChronicleRelay({ store, queue: { enqueue: () => assert.fail('nothing to queue') }, dataDir: () => path.join(dir, 'none') });
  assert.equal(await relay.tick(), 0);
  fs.mkdirSync(path.join(dir, 'none'));
  fs.writeFileSync(path.join(dir, 'none', 'RealmChronicle.json'), '[{"id": 1, "ty');
  assert.equal(await relay.tick(), 0);
  const relay2 = new D.ChronicleRelay({ store, queue: { enqueue: () => assert.fail() }, dataDir: () => null });
  assert.equal(await relay2.tick(), 0);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('registerSteward: IPC handlers never expose the token; test send uses the stubbed fetch', async () => {
  const dir = tmpDir();
  const handlers = new Map();
  const posts = [];
  const fetchImpl = async (url, init) => (posts.push({ url, body: JSON.parse(init.body) }), { status: 204, headers: { get: () => null } });
  const reg = D.registerSteward({ handle: (ch, fn) => handlers.set(ch, fn), userData: dir, dataDir: () => null, safeStorage: null, fetchImpl, pollMs: 3600000 });
  await reg.ready;
  assert.deepEqual([...handlers.keys()].sort(), ['discord:forget', 'discord:get', 'discord:save', 'discord:test']);
  await assert.rejects(handlers.get('discord:save')({ enabled: true }), /Paste a webhook URL/);
  await assert.rejects(handlers.get('discord:save')({ url: 'https://evil.test/x' }), /discord\.com/);
  await assert.rejects(handlers.get('discord:save')({ portalUrl: 'javascript:alert(1)' }), /https/);
  const st = await handlers.get('discord:save')({ url: GOOD, enabled: true, types: ['decree', 'not a type', 'coronation'], realmName: 'Ostreval', portalUrl: 'https://realm.test' });
  assert.equal(st.enabled, true);
  assert.deepEqual(st.types, ['decree', 'coronation']);
  assert.ok(!JSON.stringify(st).includes(TOKEN), 'status never carries the token');
  assert.ok(st.urlHint.includes(ID));
  assert.deepEqual(await handlers.get('discord:test')(), { delivered: true, status: 204 });
  assert.equal(posts.length, 1);
  assert.equal(posts[0].url, GOOD);
  assert.equal(posts[0].body.embeds[0].title, 'A raven from the Steward');
  assert.deepEqual(posts[0].body.allowed_mentions, { parse: [] });
  const off = await handlers.get('discord:forget')();
  assert.equal(off.hasUrl, false);
  assert.equal(off.enabled, false);
  await assert.rejects(handlers.get('discord:test')(), /webhook URL first/);
  reg.stop();
  fs.rmSync(dir, { recursive: true, force: true });
});
