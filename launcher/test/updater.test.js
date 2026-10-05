// update.json and the installer download: signature, SHA-256, tampering, version order, hotfix,
// "What's new". Downloads go through a real HTTP server on 127.0.0.1. Run: npm test
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');
const http = require('http');
const UP = require('../lib/updater');
const N = require('../lib/news');
const PB = require('../lib/publish');

const NOW = Date.parse('2026-10-03T12:00:00Z');
const kp = PB.generateKeyPair();
const other = PB.generateKeyPair();
const INSTALLER = crypto.randomBytes(300 * 1024 + 17);
const SHA = crypto.createHash('sha256').update(INSTALLER).digest('hex');

function upd(over = {}) {
  return UP.buildUpdate({ seq: 7, version: '1.2.0', url: 'https://realm.example.org/Realm-Setup-1.2.0.exe', size: INSTALLER.length, sha256: SHA, notes: { title: 'The Ember Update', items: ['News and season updates on the home screen.', 'Updates install themselves.'] }, ...over }, { now: new Date(NOW - 3600e3) });
}
const signed = (over = {}, key = kp) => JSON.stringify(UP.signUpdate(upd(over), key.privateKeyPem));

// ---------- versions ----------

test('compareVersions follows semantic-version order', () => {
  const order = ['0.9.9', '1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-alpha.beta', '1.0.0-beta', '1.0.0-beta.2', '1.0.0-beta.11', '1.0.0-rc.1', '1.0.0', '1.0.1', '1.2.0', '1.10.0', '2.0.0'];
  for (let i = 0; i < order.length; i++) {
    assert.equal(UP.compareVersions(order[i], order[i]), 0, order[i]);
    for (let j = i + 1; j < order.length; j++) {
      assert.equal(UP.compareVersions(order[i], order[j]), -1, `${order[i]} < ${order[j]}`);
      assert.equal(UP.compareVersions(order[j], order[i]), 1, `${order[j]} > ${order[i]}`);
    }
  }
  assert.equal(UP.compareVersions('1.10.0', '1.9.0'), 1, 'numeric, not text');
  assert.equal(UP.compareVersions('garbage', '0.0.1'), -1);
  assert.equal(UP.compareVersions('1.0.0', 'v2'), 1);
  assert.equal(UP.parseVersion('01.0.0'), null);
  assert.equal(UP.parseVersion('1.0'), null);
  assert.deepEqual(UP.parseVersion('1.2.3-rc.1'), { major: 1, minor: 2, patch: 3, pre: ['rc', '1'] });
});

// ---------- schema and signature ----------

test('buildUpdate: validated manifest with kind, app and file', () => {
  const u = upd();
  assert.equal(u.kind, 'realm-update');
  assert.equal(u.app, 'realm-player');
  assert.equal(u.file.name, 'Realm-Setup-1.2.0.exe');
  assert.equal(u.file.sha256, SHA);
  assert.equal(u.hotfix, false);
  assert.equal(u.expires, '2026-12-02T11:00:00Z');
  assert.equal(upd({ sha256: SHA.toUpperCase() }).file.sha256, SHA, 'hex is stored lower-case');
});

test('validateUpdate rejects unsafe or malformed fields', () => {
  const bad = (over) => assert.throws(() => upd(over), Error, JSON.stringify(over));
  bad({ version: '1.2' });
  bad({ url: 'http://realm.example.org/R.exe' });
  bad({ url: 'file:///C:/Windows/System32/cmd.exe' });
  bad({ url: 'https://user:pw@realm.example.org/R.exe' });
  bad({ sha256: 'abc' });
  bad({ size: 0 });
  bad({ size: UP.MAX_SIZE + 1 });
  bad({ name: '..\\evil.exe' });
  bad({ name: 'Realm-Setup.bat' });
  bad({ minVersion: '9.0.0' });
  bad({ notes: { items: ['two\nlines'] } });
  bad({ notes: { items: Array.from({ length: 21 }, () => 'x') } });
  assert.match(UP.validateUpdate({ ...upd(), app: 'realm-steward' }).errors.join(), /app must be/);
  assert.match(UP.validateUpdate({ ...upd(), hotfix: 'yes' }).errors.join(), /hotfix/);
  // http to this PC only in development runs
  assert.equal(UP.validateUpdate({ ...upd(), file: { ...upd().file, url: 'http://127.0.0.1:9/R.exe' } }).ok, false);
  assert.equal(UP.validateUpdate({ ...upd(), file: { ...upd().file, url: 'http://127.0.0.1:9/R.exe' } }, { allowLocalHttp: true }).ok, true);
  assert.equal(UP.validateUpdate({ ...upd(), file: { ...upd().file, url: 'http://10.0.0.5/R.exe' } }, { allowLocalHttp: true }).ok, false);
});

test('signature: verifies with the embedded key only; tampering is refused', () => {
  const v = UP.verifyUpdate(signed(), kp.publicKey, { now: NOW });
  assert.ok(v.ok, v.reason);
  assert.equal(v.update.version, '1.2.0');
  assert.match(UP.verifyUpdate(signed({}, other), kp.publicKey, { now: NOW }).reason, /does not match/);

  // Pointing the signed manifest at another file or hash breaks the signature.
  const env = JSON.parse(signed());
  for (const change of [(p) => (p.file.url = 'https://evil.example.org/R.exe'), (p) => (p.file.sha256 = 'b'.repeat(64)), (p) => (p.version = '9.9.9'), (p) => (p.hotfix = true)]) {
    const p = JSON.parse(Buffer.from(env.payload, 'base64').toString());
    change(p);
    const t = { ...env, payload: Buffer.from(JSON.stringify(p, null, 2)).toString('base64') };
    assert.match(UP.verifyUpdate(t, kp.publicKey, { now: NOW }).reason, /does not match/);
  }
  // Unsigned or re-labelled files.
  assert.match(UP.verifyUpdate(JSON.stringify(upd()), kp.publicKey, { now: NOW }).reason, /unknown format/);
  const news = N.signNews(N.buildNews({ realm: 'R', seq: 1, items: [] }, { now: new Date(NOW) }), kp.privateKeyPem);
  assert.match(UP.verifyUpdate({ ...news, format: UP.FORMAT }, kp.publicKey, { now: NOW }).reason, /kind must be realm-update/);
});

test('expiry, future dates and rollback are refused', () => {
  assert.match(UP.verifyUpdate(signed(), kp.publicKey, { now: NOW + 90 * 86400e3 }).reason, /expired/);
  assert.match(UP.verifyUpdate(signed(), kp.publicKey, { now: NOW - 3 * 86400e3 }).reason, /future/);
  assert.match(UP.verifyUpdate(signed({ seq: 6 }), kp.publicKey, { now: NOW, minSeq: 7 }).reason, /rollback/);
});

// ---------- check ----------

test('checkForUpdate: newer version available, same or older is current', async () => {
  const fetchText = async () => signed();
  const a = await UP.checkForUpdate({ currentVersion: '1.1.3', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText });
  assert.equal(a.status, 'available');
  assert.equal(a.hotfix, false);
  assert.equal(a.maxSeq, 7);
  assert.ok(a.cacheText);
  assert.equal((await UP.checkForUpdate({ currentVersion: '1.2.0', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText })).status, 'current');
  assert.equal((await UP.checkForUpdate({ currentVersion: '1.3.0-beta.1', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText })).status, 'current');
  assert.equal((await UP.checkForUpdate({ currentVersion: '1.2.0-rc.1', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText })).status, 'available');
});

test('hotfix: the flag, or a minimum version above the running one, makes the update urgent', async () => {
  const hot = await UP.checkForUpdate({ currentVersion: '1.1.0', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText: async () => signed({ hotfix: true }) });
  assert.equal(hot.status, 'available');
  assert.equal(hot.hotfix, true);
  const min = signed({ minVersion: '1.1.5' });
  assert.equal((await UP.checkForUpdate({ currentVersion: '1.1.0', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText: async () => min })).hotfix, true);
  assert.equal((await UP.checkForUpdate({ currentVersion: '1.1.5', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText: async () => min })).hotfix, false);
  assert.equal(UP.isUrgent(null, '1.0.0'), false);
});

test('checkForUpdate: errors and refusals never offer an update', async () => {
  const off = await UP.checkForUpdate({ currentVersion: '1.0.0', url: '', publicKey: kp.publicKey }, { fetchText: async () => signed() });
  assert.equal(off.status, 'off');
  const nokey = await UP.checkForUpdate({ currentVersion: '1.0.0', url: 'u', publicKey: '' }, { fetchText: async () => signed() });
  assert.equal(nokey.status, 'off');
  const net = await UP.checkForUpdate({ currentVersion: '1.0.0', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText: async () => { throw new Error('ETIMEDOUT'); } });
  assert.equal(net.status, 'error');
  assert.match(net.reason, /ETIMEDOUT/);
  const forged = await UP.checkForUpdate({ currentVersion: '1.0.0', url: 'u', publicKey: kp.publicKey, now: NOW }, { fetchText: async () => signed({}, other) });
  assert.equal(forged.status, 'error');
  assert.equal(forged.update, undefined);
  const rolled = await UP.checkForUpdate({ currentVersion: '1.0.0', url: 'u', publicKey: kp.publicKey, maxSeq: 9, now: NOW }, { fetchText: async () => signed() });
  assert.equal(rolled.status, 'error');
  assert.match(rolled.reason, /rollback/);
});

// ---------- download ----------

function serve(handler) {
  return new Promise((resolve) => {
    const srv = http.createServer(handler);
    srv.listen(0, '127.0.0.1', () => resolve({ srv, base: `http://127.0.0.1:${srv.address().port}` }));
  });
}
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'realm-updater-'));

test('download: streams, checks size and SHA-256, reports progress, then reuses the verified file', async () => {
  let hits = 0;
  const { srv, base } = await serve((req, res) => {
    hits++;
    res.setHeader('Content-Length', INSTALLER.length);
    // Several chunks, so progress is reported more than once.
    for (let i = 0; i < INSTALLER.length; i += 64 * 1024) res.write(INSTALLER.subarray(i, i + 64 * 1024));
    res.end();
  });
  const dir = tmp();
  try {
    const u = upd({ url: `${base}/Realm-Setup-1.2.0.exe`, allowLocalHttp: true });
    const progress = [];
    const r = await UP.downloadInstaller(u, { dir, allowLocalHttp: true, onProgress: (a, b) => progress.push([a, b]) }, { fetch });
    assert.equal(r.reused, false);
    assert.equal(path.basename(r.file), 'Realm-Setup-1.2.0.exe');
    assert.ok(fs.readFileSync(r.file).equals(INSTALLER));
    assert.ok(progress.length > 1);
    assert.deepEqual(progress[progress.length - 1], [INSTALLER.length, INSTALLER.length]);
    assert.equal(await UP.verifyInstaller(r.file, u), true);
    const again = await UP.downloadInstaller(u, { dir, allowLocalHttp: true }, { fetch });
    assert.equal(again.reused, true);
    assert.equal(hits, 1);
    assert.deepEqual(fs.readdirSync(dir), ['Realm-Setup-1.2.0.exe']);
  } finally {
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('download: a tampered file (same size, other bytes) is deleted and never kept', async () => {
  const evil = Buffer.from(INSTALLER);
  evil[1000] ^= 0xff;
  const { srv, base } = await serve((req, res) => res.end(evil));
  // (same length as the signed size, so only the hash can catch it)
  const dir = tmp();
  try {
    const u = upd({ url: `${base}/R.exe`, allowLocalHttp: true });
    await assert.rejects(UP.downloadInstaller(u, { dir, allowLocalHttp: true }, { fetch }), /SHA-256 differs/);
    assert.deepEqual(fs.readdirSync(dir), []);
  } finally {
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('download: too large, too short, wrong Content-Length and HTTP errors are refused', async () => {
  let mode = 'big';
  const { srv, base } = await serve((req, res) => {
    // write() + end() sends no Content-Length (chunked), so the byte count is checked while streaming.
    if (mode === 'big') return res.write(Buffer.concat([INSTALLER, Buffer.alloc(10)])), res.end();
    if (mode === 'short') return res.write(INSTALLER.subarray(0, 1000)), res.end();
    if (mode === 'header') {
      res.setHeader('Content-Length', 5);
      return res.end('12345');
    }
    res.writeHead(404).end();
  });
  const dir = tmp();
  try {
    const u = upd({ url: `${base}/R.exe`, allowLocalHttp: true });
    const go = () => UP.downloadInstaller(u, { dir, allowLocalHttp: true }, { fetch });
    await assert.rejects(go(), /larger than the signed update/);
    mode = 'short';
    await assert.rejects(go(), /ended early/);
    mode = 'header';
    await assert.rejects(go(), /not the size/);
    mode = '404';
    await assert.rejects(go(), /HTTP 404/);
    assert.deepEqual(fs.readdirSync(dir), [], 'no partial file is left behind');
  } finally {
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('download: refuses non-https addresses outside development, and a swapped file fails the re-check', async () => {
  const dir = tmp();
  try {
    let called = false;
    const u = upd({ url: 'http://127.0.0.1:1/R.exe', allowLocalHttp: true });
    await assert.rejects(UP.downloadInstaller(u, { dir }, { fetch: async () => ((called = true), null) }), /not an https/);
    assert.equal(called, false);
    // A file that was changed after the download no longer verifies (checked again before it runs).
    const file = path.join(dir, UP.installerName('1.2.0'));
    fs.writeFileSync(file, INSTALLER);
    assert.equal(await UP.verifyInstaller(file, upd()), true);
    fs.appendFileSync(file, 'x');
    assert.equal(await UP.verifyInstaller(file, upd()), false);
    assert.equal(await UP.verifyInstaller(path.join(dir, 'missing.exe'), upd()), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('download: cancelling stops and cleans up', async () => {
  const { srv, base } = await serve((req, res) => {
    res.write(INSTALLER.subarray(0, 1024));
    // never ends
  });
  const dir = tmp();
  try {
    const ac = new AbortController();
    const u = upd({ url: `${base}/R.exe`, allowLocalHttp: true });
    const p = UP.downloadInstaller(u, { dir, allowLocalHttp: true, signal: ac.signal, onProgress: () => ac.abort() }, { fetch });
    await assert.rejects(p, /cancelled|failed/);
    assert.deepEqual(fs.readdirSync(dir), []);
  } finally {
    srv.closeAllConnections();
    srv.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('pruneDownloads keeps only the current installer', async () => {
  const dir = tmp();
  try {
    for (const n of ['Realm-Setup-1.0.0.exe', 'Realm-Setup-1.2.0.exe', 'Realm-Setup-1.2.0.exe.partial', 'notes.txt']) fs.writeFileSync(path.join(dir, n), 'x');
    assert.equal(await UP.pruneDownloads(dir, '1.2.0'), 2);
    assert.deepEqual(fs.readdirSync(dir).sort(), ['Realm-Setup-1.2.0.exe', 'notes.txt']);
    assert.equal(await UP.pruneDownloads(path.join(dir, 'nope'), '1.0.0'), 0);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ---------- what's new ----------

test("whatsNew: shown once after an update, from the signed notes, else the build's notes", () => {
  const u = upd();
  assert.equal(UP.whatsNew({ currentVersion: '1.2.0', lastVersion: null, cachedUpdate: u }), null, 'first install');
  assert.equal(UP.whatsNew({ currentVersion: '1.2.0', lastVersion: '1.2.0', cachedUpdate: u }), null, 'same version');
  assert.equal(UP.whatsNew({ currentVersion: '1.1.0', lastVersion: '1.2.0', cachedUpdate: u }), null, 'downgrade');
  const w = UP.whatsNew({ currentVersion: '1.2.0', lastVersion: '1.1.3', cachedUpdate: u });
  assert.equal(w.title, 'The Ember Update');
  assert.equal(w.items.length, 2);
  assert.equal(w.from, '1.1.3');
  const b = UP.whatsNew({ currentVersion: '1.2.0', lastVersion: '1.1.3', cachedUpdate: upd({ version: '1.3.0', name: 'Realm-Setup-1.3.0.exe' }), bundledNotes: { '1.2.0': { title: 'From the build', items: ['One'] } } });
  assert.equal(b.title, 'From the build');
  const plain = UP.whatsNew({ currentVersion: '1.2.0', lastVersion: '1.1.3' });
  assert.deepEqual(plain, { version: '1.2.0', from: '1.1.3', title: 'Realm 1.2.0', items: [] });
});

test('installerName only takes valid versions (no path tricks)', () => {
  assert.equal(UP.installerName('1.2.0'), 'Realm-Setup-1.2.0.exe');
  assert.throws(() => UP.installerName('../../x'), /invalid version/);
});
