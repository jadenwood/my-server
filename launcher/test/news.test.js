// news.json: schema, signing, verification, ordering and the offline cache. Run: npm test
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('crypto');
const N = require('../lib/news');
const PB = require('../lib/publish');
const UP = require('../lib/updater');

const NOW = Date.parse('2026-10-03T12:00:00Z');
const kp = PB.generateKeyPair();
const other = PB.generateKeyPair();

function items() {
  return [
    { id: 'charter', type: 'announcement', title: 'The Hearth Charter', body: 'Read it before you rebel.', date: '2026-09-20T10:00:00Z', pinned: true },
    { id: 'season-1', type: 'season', title: 'Season 1: The Hollow Crown', body: 'Week 3 standings are up.', date: '2026-10-02T18:00:00Z' },
    { id: 'crowned', type: 'chronicle', title: 'Aldric Varrow takes the Old Throne', body: '', date: '2026-10-03T08:00:00Z', server: 's1' },
    { id: 'heron', type: 'realm', tag: 'sculpture', title: 'The Grey Heron stands at the harbour gate', body: 'A new sculpture in the capital.', date: '2026-10-01T09:00:00Z' },
    { id: 'crown-night', type: 'realm', tag: 'event', title: 'Crown Night', body: 'Saturday 20:00 UTC.', date: '2026-10-02T09:00:00Z', at: '2026-10-04T20:00:00Z' }
  ];
}

const news = (over = {}) => N.buildNews({ realm: 'The Realm', seq: 4, items: items(), ...over }, { now: new Date(NOW - 3600e3) });
const signed = (over = {}) => JSON.stringify(N.signNews(news(over), kp.privateKeyPem));

test('buildNews: validated payload with kind, seq, issued and a 90-day expiry', () => {
  const n = news();
  assert.equal(n.kind, 'realm-news');
  assert.equal(n.schema, 1);
  assert.equal(n.issued, '2026-10-03T11:00:00Z');
  assert.equal(n.expires, '2027-01-01T11:00:00Z');
  assert.equal(n.items.length, 5);
  assert.equal(n.items[3].tag, 'sculpture');
  assert.equal(n.items[0].pinned, true);
  assert.equal(N.buildNews({ realm: 'R', seq: 1, items: [{ id: 'x', type: 'realm', title: 'T', date: '2026-10-01T00:00:00Z' }] }).items[0].tag, 'other');
});

test('validateNews rejects bad items and drops unknown fields', () => {
  const bad = (it) => N.validateNews({ ...news(), items: [{ id: 'a', type: 'announcement', title: 'T', date: '2026-10-01T00:00:00Z', ...it }] });
  assert.ok(bad({}).ok);
  assert.equal(bad({ evil: '<script>' }).value.items[0].evil, undefined);
  assert.match(bad({ id: 'Bad Id' }).errors.join(), /id must be/);
  assert.match(bad({ type: 'ad' }).errors.join(), /type must be/);
  assert.match(bad({ title: '' }).errors.join(), /title is required/);
  assert.match(bad({ title: 'x'.repeat(101) }).errors.join(), /longer than 100/);
  assert.match(bad({ title: 'line\nbreak' }).errors.join(), /control characters/);
  assert.ok(bad({ body: 'two\nlines' }).ok, 'bodies may have line breaks');
  assert.match(bad({ date: 'yesterday' }).errors.join(), /date must be/);
  assert.match(bad({ link: 'http://example.org' }).errors.join(), /https/);
  assert.match(bad({ link: 'javascript:alert(1)' }).errors.join(), /https/);
  assert.match(bad({ tag: 'sign' }).errors.join(), /only for realm items/);
  assert.match(bad({ pinned: 'yes' }).errors.join(), /pinned/);
  assert.match(bad({ server: '../x' }).errors.join(), /server id/);
  assert.match(N.validateNews({ ...news(), items: [items()[0], items()[0]] }).errors.join(), /duplicate id charter/);
  assert.match(N.validateNews({ ...news(), items: Array.from({ length: 61 }, (_, i) => ({ ...items()[0], id: 'n' + i })) }).errors.join(), /at most 60/);
  assert.match(N.validateNews({ ...news(), kind: 'realm-update' }).errors.join(), /kind must be realm-news/);
  assert.throws(() => N.buildNews({ realm: 'R', seq: 0, items: [] }), /seq/);
});

test('signature: a good feed verifies; another key, a changed byte or a changed signature does not', () => {
  const v = N.verifyNews(signed(), kp.publicKey, { now: NOW });
  assert.ok(v.ok, v.reason);
  assert.equal(v.news.items.length, 5);
  assert.match(N.verifyNews(signed(), other.publicKey, { now: NOW }).reason, /does not match/);

  const env = JSON.parse(signed());
  const payload = JSON.parse(Buffer.from(env.payload, 'base64').toString());
  payload.items[0].title = 'Free gold at 203.0.113.9';
  const tampered = { ...env, payload: Buffer.from(JSON.stringify(payload, null, 2)).toString('base64') };
  assert.match(N.verifyNews(tampered, kp.publicKey, { now: NOW }).reason, /does not match/);

  const sig = Buffer.from(env.signature, 'base64');
  sig[5] ^= 1;
  assert.match(N.verifyNews({ ...env, signature: sig.toString('base64') }, kp.publicKey, { now: NOW }).reason, /does not match/);
});

test('unsigned, malformed and cross-format files are refused', () => {
  const plain = JSON.stringify(news());
  assert.match(N.verifyNews(plain, kp.publicKey, { now: NOW }).reason, /unknown format/);
  assert.match(N.verifyNews('{nope', kp.publicKey, { now: NOW }).reason, /not valid JSON/);
  assert.match(N.verifyNews('[]', kp.publicKey, { now: NOW }).reason, /not an object/);
  assert.match(N.verifyNews('x'.repeat(200 * 1024), kp.publicKey, { now: NOW }).reason, /too large/);
  const env = JSON.parse(signed());
  assert.match(N.verifyNews({ ...env, alg: 'RS256' }, kp.publicKey, { now: NOW }).reason, /algorithm/);
  assert.match(N.verifyNews({ ...env, signature: undefined }, kp.publicKey, { now: NOW }).reason, /not signed/);
  assert.match(N.verifyNews(env, 'not-a-key', { now: NOW }).reason, /no valid public key/);
  // A signed server list re-labelled as news is refused: the payload says what it is.
  const list = PB.signManifest({ schema: 1, realm: 'R', seq: 9, issued: '2026-10-01T00:00:00Z', expires: '2026-11-01T00:00:00Z', servers: [{ id: 's1', name: 'A', address: '127.0.0.1', port: 7350, queryPort: 27015, maxPlayers: 10 }] }, kp.privateKeyPem);
  assert.match(N.verifyNews({ ...list, format: N.FORMAT }, kp.publicKey, { now: NOW }).reason, /kind must be realm-news/);
  // A signed update manifest re-labelled as news is refused too.
  const up = UP.signUpdate(UP.buildUpdate({ seq: 1, version: '1.1.0', url: 'https://example.org/R.exe', size: 10, sha256: 'a'.repeat(64) }, { now: new Date(NOW) }), kp.privateKeyPem);
  assert.match(N.verifyNews({ ...up, format: N.FORMAT }, kp.publicKey, { now: NOW }).reason, /kind must be realm-news/);
});

test('expiry, future dates and rollback', () => {
  const late = NOW + 200 * 86400e3;
  assert.match(N.verifyNews(signed(), kp.publicKey, { now: late }).reason, /expired/);
  assert.ok(N.verifyNews(signed(), kp.publicKey, { now: late, ignoreExpiry: true }).ok);
  assert.match(N.verifyNews(signed(), kp.publicKey, { now: NOW - 5 * 86400e3 }).reason, /future/);
  assert.match(N.verifyNews(signed({ seq: 3 }), kp.publicKey, { now: NOW, minSeq: 4 }).reason, /rollback/);
  assert.ok(N.verifyNews(signed({ seq: 4 }), kp.publicKey, { now: NOW, minSeq: 4 }).ok);
});

test('ordering: pinned first, then newest; ties by id; scheduled items stay hidden until their date', () => {
  const its = [...items(), { id: 'later', type: 'announcement', title: 'Scheduled', date: '2026-10-05T00:00:00Z' }, { id: 'b-tie', type: 'season', title: 'B', date: '2026-10-02T18:00:00Z' }];
  const order = N.orderNews(its, { now: NOW }).map((i) => i.id);
  assert.deepEqual(order, ['charter', 'crowned', 'b-tie', 'season-1', 'crown-night', 'heron']);
  assert.ok(N.orderNews(its, { now: Date.parse('2026-10-05T00:01:00Z') }).some((i) => i.id === 'later'));
  // Clock slack: an item dated 4 minutes ahead already shows.
  assert.equal(N.orderNews([{ ...its[0], pinned: false, date: new Date(NOW + 4 * 60e3).toISOString() }], { now: NOW }).length, 1);
  // Stable regardless of input order.
  assert.deepEqual(N.orderNews(its.slice().reverse(), { now: NOW }).map((i) => i.id), order);
});

test('forPlayer splits news from realm changes and drops events that are over', () => {
  const p = N.forPlayer(news(), { now: NOW });
  assert.deepEqual(p.news.map((i) => i.id), ['charter', 'crowned', 'season-1']);
  assert.deepEqual(p.realm.map((i) => i.id), ['crown-night', 'heron']);
  const after = N.forPlayer(news(), { now: Date.parse('2026-10-05T03:00:00Z') });
  assert.deepEqual(after.realm.map((i) => i.id), ['heron'], 'Crown Night is over');
  assert.deepEqual(N.forPlayer(null), { news: [], realm: [] });
});

test('siblingUrl puts news.json and update.json next to servers.json', () => {
  assert.equal(N.siblingUrl('https://realm.example.org/pub/servers.json?x=1', 'news.json'), 'https://realm.example.org/pub/news.json');
  assert.equal(N.siblingUrl('https://user.github.io/realm/servers.json', 'update.json'), 'https://user.github.io/realm/update.json');
  assert.equal(N.siblingUrl('', 'news.json'), '');
  assert.equal(N.siblingUrl('not a url', 'news.json'), '');
});

test('loadNews: online first and returns the text to cache', async () => {
  const r = await N.loadNews({ url: 'https://x/news.json', publicKey: kp.publicKey, now: NOW }, { fetchText: async () => signed() });
  assert.equal(r.source, 'online');
  assert.equal(r.maxSeq, 4);
  assert.ok(r.cacheText);
  assert.equal(r.reason, null);
});

test('loadNews offline: the saved copy (even expired), then the copy in the build', async () => {
  const offline = { fetchText: async () => { throw new Error('getaddrinfo ENOTFOUND'); } };
  const cached = await N.loadNews({ url: 'https://x/news.json', publicKey: kp.publicKey, cacheText: signed(), maxSeq: 4, now: NOW + 200 * 86400e3 }, offline);
  assert.equal(cached.source, 'cache');
  assert.match(cached.reason, /ENOTFOUND/);
  assert.equal(cached.news.items.length, 5);
  const bundled = await N.loadNews({ url: 'https://x/news.json', publicKey: kp.publicKey, bundled: N.signNews(news({ seq: 1 }), kp.privateKeyPem), now: NOW }, offline);
  assert.equal(bundled.source, 'bundled');
  const none = await N.loadNews({ url: 'https://x/news.json', publicKey: kp.publicKey, now: NOW }, offline);
  assert.equal(none.source, 'none');
  assert.equal(none.news, null);
});

test('loadNews: a tampered or rolled-back download keeps the saved copy', async () => {
  const env = JSON.parse(signed({ seq: 6 }));
  const p = JSON.parse(Buffer.from(env.payload, 'base64').toString());
  p.items[0].title = 'Changed';
  const tampered = JSON.stringify({ ...env, payload: Buffer.from(JSON.stringify(p, null, 2)).toString('base64') });
  const r1 = await N.loadNews({ url: 'u', publicKey: kp.publicKey, cacheText: signed({ seq: 5 }), maxSeq: 5, now: NOW }, { fetchText: async () => tampered });
  assert.equal(r1.source, 'cache');
  assert.match(r1.reason, /does not match/);
  assert.equal(r1.news.items[0].title, 'The Hearth Charter');
  const r2 = await N.loadNews({ url: 'u', publicKey: kp.publicKey, cacheText: signed({ seq: 5 }), maxSeq: 5, now: NOW }, { fetchText: async () => signed({ seq: 2 }) });
  assert.equal(r2.source, 'cache');
  assert.match(r2.reason, /rollback/);
  // An old copy in the build never replaces a newer one this PC has seen.
  const r3 = await N.loadNews({ url: '', publicKey: kp.publicKey, bundled: JSON.parse(signed({ seq: 1 })), maxSeq: 5, now: NOW }, { fetchText: async () => '' });
  assert.equal(r3.source, 'none');
});

test('loadNews without a key trusts nothing', async () => {
  let fetched = false;
  const r = await N.loadNews({ url: 'u', publicKey: '', cacheText: signed() }, { fetchText: async () => ((fetched = true), signed()) });
  assert.equal(r.source, 'none');
  assert.equal(fetched, false);
  assert.match(r.reason, /no public key/);
});

test('signDocument refuses a key that is not Ed25519', () => {
  const rsa = crypto.generateKeyPairSync('rsa', { modulusLength: 1024 }).privateKey.export({ format: 'pem', type: 'pkcs8' });
  assert.throws(() => N.signDocument(N.FORMAT, news(), rsa), /not Ed25519/);
  assert.throws(() => N.signNews({ ...news(), seq: -1 }, kp.privateKeyPem), /invalid news/);
});
