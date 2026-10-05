// Publish news and Publish update (lib/publish-feeds.js): what Steward signs is exactly what the player
// app accepts, seq never goes down, damaged drafts are never overwritten, installers are hashed from disk.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const fsp = require('fs/promises');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const PF = require('../lib/publish-feeds');
const PB = require('../lib/publish');
const N = require('../lib/news');
const U = require('../lib/updater');

const NOW = new Date('2026-10-04T12:00:00Z');
const kp = PB.generateKeyPair();
const key = { pem: kp.privateKeyPem, publicKey: kp.publicKey };
const other = PB.generateKeyPair();

async function tmp() {
  return fsp.mkdtemp(path.join(os.tmpdir(), 'realm-feeds-'));
}

function drafts() {
  return [
    { id: 'charter', type: 'announcement', title: 'The Hearth Charter is law', body: 'Read it before you rebel.\nTwo lines.', date: '2026-09-24T18:00:00Z', pinned: true, link: 'https://example.org/rules' },
    { id: 'heron', type: 'realm', tag: 'sculpture', title: 'The Grey Heron stands at the harbour gate', body: '', date: '2026-10-01T09:00:00Z' },
    { id: 'crown-night', type: 'realm', tag: 'event', title: 'Crown Night', date: '2026-10-02T09:00:00Z', at: '2026-10-06T20:00:00Z', link: '' },
    { id: 'later', type: 'season', title: 'Season 2 is coming', date: '2026-10-20T09:00:00Z' }
  ];
}

test('writeNews: signed news.json the player app verifies, with scheduled items kept hidden', async () => {
  const dir = await tmp();
  const store = await new PF.FeedStore(path.join(dir, 'profile')).load();
  const out = path.join(dir, 'publish');
  const r = await PF.writeNews(store, { realm: 'The Realm', items: drafts(), validDays: 30 }, { key, outDir: out, now: NOW });
  assert.equal(r.seq, 1);
  assert.equal(r.items, 4);
  assert.equal(r.scheduled, 1, 'the season item dated later is scheduled');
  assert.equal(r.file, path.join(out, 'news.json'));
  const text = fs.readFileSync(r.file, 'utf8');
  const v = N.verifyNews(text, kp.publicKey, { now: NOW.getTime() });
  assert.ok(v.ok, v.reason);
  assert.equal(v.news.realm, 'The Realm');
  assert.equal(v.news.expires, '2026-11-03T12:00:00Z');
  assert.equal(v.news.items[0].body, 'Read it before you rebel.\nTwo lines.');
  assert.equal(v.news.items[2].link, undefined, 'an empty link is dropped');
  assert.equal(N.verifyNews(text, other.publicKey, { now: NOW.getTime() }).ok, false);
  // The drafts were saved with the new seq.
  const again = await new PF.FeedStore(path.join(dir, 'profile')).load();
  assert.equal(again.data.news.seq, 1);
  assert.equal(again.data.news.items.length, 4);
  assert.equal(again.data.news.validDays, 30);
});

test('writeNews: seq goes up from the saved counter or the published file, whichever is higher', async () => {
  const dir = await tmp();
  const out = path.join(dir, 'publish');
  const a = await new PF.FeedStore(path.join(dir, 'a')).load();
  assert.equal((await PF.writeNews(a, { realm: 'R', items: drafts() }, { key, outDir: out, now: NOW })).seq, 1);
  assert.equal((await PF.writeNews(a, { realm: 'R', items: drafts() }, { key, outDir: out, now: NOW })).seq, 2);
  // A fresh profile (another PC, a lost profile) still continues after the published file.
  const b = await new PF.FeedStore(path.join(dir, 'b')).load();
  assert.equal((await PF.writeNews(b, { realm: 'R', items: drafts() }, { key, outDir: out, now: NOW })).seq, 3);
  // A news.json signed with another key does not count (and is replaced).
  const c = await new PF.FeedStore(path.join(dir, 'c')).load();
  const foreign = N.signNews(N.buildNews({ realm: 'X', seq: 900, items: [] }, { now: NOW }), other.privateKeyPem);
  const out2 = path.join(dir, 'publish2');
  await fsp.mkdir(out2);
  fs.writeFileSync(path.join(out2, 'news.json'), JSON.stringify(foreign));
  assert.equal((await PF.writeNews(c, { realm: 'R', items: [] }, { key, outDir: out2, now: NOW })).seq, 1);
});

test('writeNews: invalid items are refused with the reason and nothing is written', async () => {
  const dir = await tmp();
  const store = await new PF.FeedStore(path.join(dir, 'p')).load();
  const out = path.join(dir, 'out');
  const bad = drafts();
  bad[1].id = 'charter';
  await assert.rejects(PF.writeNews(store, { realm: 'R', items: bad }, { key, outDir: out, now: NOW }), (e) => e.friendly && /duplicate id charter/.test(e.message));
  const bad2 = drafts();
  bad2[0].link = 'http://example.org';
  await assert.rejects(PF.writeNews(store, { realm: 'R', items: bad2 }, { key, outDir: out, now: NOW }), /https/);
  const tooMany = Array.from({ length: 61 }, (_, i) => ({ id: `n${i}`, type: 'announcement', title: 'T', date: '2026-10-01T00:00:00Z' }));
  await assert.rejects(PF.writeNews(store, { realm: 'R', items: tooMany }, { key, outDir: out, now: NOW }), /at most 60/);
  assert.ok(!fs.existsSync(path.join(out, 'news.json')));
  assert.equal(store.data.news.seq, 0);
});

test('a damaged drafts file is never overwritten and blocks publishing until moved away', async () => {
  const dir = await tmp();
  const prof = path.join(dir, 'p');
  await fsp.mkdir(prof);
  fs.writeFileSync(path.join(prof, 'feeds.json'), '{"version":1,"news":{"seq":7,');
  const store = await new PF.FeedStore(prof).load();
  assert.match(store.error, /damaged/);
  await assert.rejects(PF.writeNews(store, { realm: 'R', items: drafts() }, { key, outDir: path.join(dir, 'out'), now: NOW }), /damaged/);
  await assert.rejects(store.save(), /damaged/);
  assert.equal(fs.readFileSync(path.join(prof, 'feeds.json'), 'utf8'), '{"version":1,"news":{"seq":7,');
  assert.ok(!fs.existsSync(path.join(dir, 'out', 'news.json')));
});

test('itemIdFrom and cleanDraftItem', () => {
  assert.equal(PF.itemIdFrom('The Grey Heron stands!'), 'the-grey-heron-stands');
  assert.equal(PF.itemIdFrom('Crown Night', ['crown-night']), 'crown-night-2');
  assert.equal(PF.itemIdFrom(''), 'news');
  assert.ok(/^[a-z0-9][a-z0-9-]{0,39}$/.test(PF.itemIdFrom('x'.repeat(200), ['x'.repeat(32)])));
  const c = PF.cleanDraftItem({ id: 'a', type: 'realm', title: 'T', date: 'd', evil: '<script>', pinned: 'yes', tag: ' sign ' });
  assert.deepEqual(c, { id: 'a', type: 'realm', title: 'T', body: '', date: 'd', tag: 'sign' });
});

async function installer(dir, name, bytes = 'MZ fake installer bytes') {
  const file = path.join(dir, name);
  fs.writeFileSync(file, bytes);
  return { file, sha: crypto.createHash('sha256').update(bytes).digest('hex'), size: Buffer.byteLength(bytes) };
}

test('inspectInstaller: size and SHA-256 from disk, version from the name, SHA256SUMS.txt must agree', async () => {
  const dir = await tmp();
  const a = await installer(dir, 'Realm-Setup-1.1.0.exe');
  const i = await PF.inspectInstaller(a.file);
  assert.deepEqual([i.name, i.size, i.sha256, i.version, i.sums], ['Realm-Setup-1.1.0.exe', a.size, a.sha, '1.1.0', 'none']);
  fs.writeFileSync(path.join(dir, 'SHA256SUMS.txt'), `${a.sha}  Realm-Setup-1.1.0.exe\n${'0'.repeat(64)}  Realm-Steward-Setup-1.1.0.exe\n`);
  assert.equal((await PF.inspectInstaller(a.file)).sums, 'match');
  fs.writeFileSync(path.join(dir, 'SHA256SUMS.txt'), `${'1'.repeat(64)}  Realm-Setup-1.1.0.exe\n`);
  await assert.rejects(PF.inspectInstaller(a.file), /does not match its line in SHA256SUMS/);
  const s = await installer(dir, 'Realm-Steward-Setup-1.1.0.exe');
  await assert.rejects(PF.inspectInstaller(s.file), /Steward installer/);
  await assert.rejects(PF.inspectInstaller(path.join(dir, 'nope.exe')), /not found/);
  await assert.rejects(PF.inspectInstaller(path.join(dir, 'bad name.exe')), /plain \.exe/);
});

test('writeUpdate: signed update.json the player app verifies; tampering with the installer is caught by the player', async () => {
  const dir = await tmp();
  const rel = path.join(dir, 'release');
  await fsp.mkdir(rel);
  const a = await installer(rel, 'Realm-Setup-1.1.0.exe');
  const store = await new PF.FeedStore(path.join(dir, 'p')).load();
  const out = path.join(dir, 'out');
  const r = await PF.writeUpdate(
    store,
    { installer: a.file, url: 'https://example.org/Realm-Setup-1.1.0.exe', notesTitle: 'The Ember Update', notesItems: '- Better news\n\n* Signed updates\n', validDays: 30 },
    { key, outDir: out, now: NOW }
  );
  assert.equal(r.seq, 1);
  assert.equal(r.version, '1.1.0');
  assert.equal(r.urgent, false);
  const v = U.verifyUpdate(fs.readFileSync(r.file, 'utf8'), kp.publicKey, { now: NOW.getTime() });
  assert.ok(v.ok, v.reason);
  assert.equal(v.update.file.sha256, a.sha);
  assert.equal(v.update.file.size, a.size);
  assert.equal(v.update.file.name, 'Realm-Setup-1.1.0.exe');
  assert.deepEqual(v.update.notes, { title: 'The Ember Update', items: ['Better news', 'Signed updates'] });
  assert.equal(v.update.expires, '2026-11-03T12:00:00Z');
  // The player checks the downloaded bytes against the signed hash.
  assert.equal(await U.verifyInstaller(a.file, v.update), true);
  fs.writeFileSync(a.file, 'MZ tampered installer byte');
  assert.equal(await U.verifyInstaller(a.file, v.update), false);
  // Draft remembered; next seq is 2.
  assert.equal(store.data.update.draft.version, '1.1.0');
  assert.equal(await PF.nextUpdateSeq(store, out, kp.publicKey), 2);
});

test('writeUpdate: hotfix and minVersion, and the refusals', async () => {
  const dir = await tmp();
  const a = await installer(dir, 'Realm-Setup-1.2.0.exe');
  const store = await new PF.FeedStore(path.join(dir, 'p')).load();
  const out = path.join(dir, 'out');
  const opts = { key, outDir: out, now: NOW };
  const base = { installer: a.file, url: 'https://example.org/r.exe' };
  await assert.rejects(PF.writeUpdate(store, { ...base, url: 'http://example.org/r.exe' }, opts), /https/);
  await assert.rejects(PF.writeUpdate(store, { ...base, version: '1.3.0' }, opts), /named for version 1\.2\.0/);
  await assert.rejects(PF.writeUpdate(store, { ...base, minVersion: '2.0.0' }, opts), /minVersion/);
  const b = await installer(dir, 'RealmPlayer.exe');
  await assert.rejects(PF.writeUpdate(store, { installer: b.file, url: base.url }, opts), /Enter the version/);
  assert.ok(!fs.existsSync(path.join(out, 'update.json')), 'nothing written by a refused update');
  const r = await PF.writeUpdate(store, { ...base, hotfix: true, minVersion: '1.1.0' }, opts);
  assert.equal(r.urgent, true);
  const v = U.verifyUpdate(fs.readFileSync(r.file, 'utf8'), kp.publicKey, { now: NOW.getTime() });
  assert.equal(v.update.hotfix, true);
  assert.equal(v.update.minVersion, '1.1.0');
  assert.equal(U.isUrgent(v.update, '1.0.0'), true);
  // An update manifest is never accepted as news, or news as an update.
  assert.equal(N.verifyNews(fs.readFileSync(r.file, 'utf8'), kp.publicKey, { now: NOW.getTime() }).ok, false);
});

test('registerSteward: the IPC calls work end to end with a stored key', async () => {
  const dir = await tmp();
  const handlers = {};
  const logs = [];
  const out = path.join(dir, 'out');
  const a = await installer(dir, 'Realm-Setup-1.1.0.exe');
  let keyNow = null;
  PF.registerSteward({
    handle: (ch, fn) => (handlers[ch] = fn),
    userData: path.join(dir, 'profile'),
    readSigningKey: async () => keyNow,
    outDir: () => out,
    manifestUrl: () => 'https://example.org/realm/servers.json',
    realmName: () => 'The Realm',
    dialog: { showOpenDialog: async () => ({ canceled: false, filePaths: [a.file] }) },
    win: () => null,
    log: (lvl, msg) => logs.push(msg)
  });
  let st = await handlers['feeds:status']();
  assert.equal(st.key, null);
  assert.equal(st.newsUrl, 'https://example.org/realm/news.json');
  assert.equal(st.updateUrl, 'https://example.org/realm/update.json');
  assert.deepEqual(st.types, N.TYPES);
  await assert.rejects(handlers['feeds:writeNews']({ items: drafts(), validDays: 30 }), /signing key first/);
  keyNow = key;
  assert.deepEqual(await handlers['feeds:saveNewsDraft'](drafts(), 45), { saved: 4 });
  st = await handlers['feeds:status']();
  assert.equal(st.news.items.length, 4);
  assert.equal(st.news.validDays, 45);
  assert.equal(st.news.published.exists, false);
  const w = await handlers['feeds:writeNews']({ items: drafts(), validDays: 30 });
  assert.equal(w.seq, 1);
  st = await handlers['feeds:status']();
  assert.equal(st.news.published.ok, true);
  assert.equal(st.news.published.seq, 1);
  assert.equal(st.news.nextSeq, 2);
  const pick = await handlers['feeds:pickInstaller']();
  assert.equal(pick.version, '1.1.0');
  const u = await handlers['feeds:writeUpdate']({ installer: pick.path, url: 'https://example.org/realm/Realm-Setup-1.1.0.exe', notesItems: ['One'], validDays: 60 });
  assert.equal(u.seq, 1);
  st = await handlers['feeds:status']();
  assert.equal(st.update.published.seq, 1);
  assert.ok(logs.some((l) => /news\.json version 1/.test(l)));
});
