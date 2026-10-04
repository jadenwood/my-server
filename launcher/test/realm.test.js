// Tests for lib/realm.js against temporary folders (posix paths; Windows path rules are covered
// in safety.test.js). Run: npm test
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const fsp = require('fs/promises');
const os = require('os');
const path = require('path');
const http = require('http');
const crypto = require('crypto');
const yazl = require('yazl');
const R = require('../lib/realm');
const S = require('../lib/safety');
const Z = require('../lib/zip');

async function tmpdir() {
  return fsp.mkdtemp(path.join(os.tmpdir(), 'realm-test-'));
}

async function write(file, data) {
  await fsp.mkdir(path.dirname(file), { recursive: true });
  await fsp.writeFile(file, data);
}

async function makeSteamServer(dir) {
  await write(path.join(dir, 'Server.exe'), 'MZ fake wrapper');
  await write(path.join(dir, 'ROK.exe'), 'MZ fake game');
  await write(path.join(dir, 'ROK_Data', 'Managed', 'Assembly-CSharp.dll'), 'vanilla assembly');
  await write(path.join(dir, 'ROK_Data', 'Managed', 'UnityEngine.dll'), crypto.randomBytes(300 * 1024));
}

function makeZip(file, entries) {
  return new Promise((resolve, reject) => {
    const z = new yazl.ZipFile();
    for (const [name, data] of entries) {
      if (name.endsWith('/')) z.addEmptyDirectory(name);
      else z.addBuffer(Buffer.from(data), name);
    }
    z.end();
    z.outputStream.pipe(fs.createWriteStream(file)).on('close', resolve).on('error', reject);
  });
}

async function sha(file) {
  return crypto.createHash('sha256').update(await fsp.readFile(file)).digest('hex');
}

async function makeTestCopy(base) {
  const steam = path.join(base, 'Library', 'common', 'Reign Of Kings Dedicated Server');
  await makeSteamServer(steam);
  const root = path.join(base, 'RealmTest', 'server');
  await R.createTestCopy(steam, root);
  return { steam, root };
}

test('createTestCopy copies with progress, writes the marker and leaves the source alone', async () => {
  const base = await tmpdir();
  const steam = path.join(base, 'Library', 'common', 'Reign Of Kings Dedicated Server');
  await makeSteamServer(steam);
  const before = await fsp.readdir(steam);
  const root = path.join(base, 'RealmTest', 'server');
  const seen = [];
  const res = await R.createTestCopy(steam, root, { onProgress: (p) => seen.push(p) });
  assert.equal(res.skipped, false);
  assert.ok(await R.isTestCopy(root));
  assert.equal(await fsp.readFile(path.join(root, 'ROK_Data', 'Managed', 'Assembly-CSharp.dll'), 'utf8'), 'vanilla assembly');
  const last = seen.filter((p) => p.phase === 'copy').pop();
  assert.equal(last.done, last.total);
  assert.ok(last.total > 300 * 1024);
  assert.deepEqual(await fsp.readdir(steam), before);
  assert.deepEqual((await fsp.readdir(path.join(base, 'RealmTest'))).filter((n) => n.startsWith('.realm-copy')), []);
  assert.equal((await R.createTestCopy(steam, root)).skipped, true, 'second run is skipped');
});

test('createTestCopy refuses unsafe destinations and folders it did not make', async () => {
  const base = await tmpdir();
  const steam = path.join(base, 'lib', 'Reign Of Kings Dedicated Server');
  await makeSteamServer(steam);
  await assert.rejects(R.createTestCopy(steam, path.join(steam, 'copy')), /overlaps/);
  await assert.rejects(R.createTestCopy(steam, path.join(base, 'steamapps', 'x')), /steamapps/);
  await assert.rejects(R.createTestCopy(steam, path.join(base, 'Steam', 'x')), /Steam folder/);
  const busy = path.join(base, 'busy');
  await write(path.join(busy, 'something.txt'), 'x');
  await assert.rejects(R.createTestCopy(steam, busy), /no \.realm-test-copy marker/);
  await assert.rejects(R.createTestCopy(path.join(base, 'nothing'), path.join(base, 'dest')), /not found/);
});

test('createTestCopy removes its staging folder when cancelled', async () => {
  const base = await tmpdir();
  const steam = path.join(base, 'lib', 'Reign Of Kings Dedicated Server');
  await makeSteamServer(steam);
  for (let i = 0; i < 20; i++) await write(path.join(steam, 'extra', `f${i}.bin`), crypto.randomBytes(64 * 1024));
  const ac = new AbortController();
  const root = path.join(base, 'RealmTest', 'server');
  await assert.rejects(
    R.createTestCopy(steam, root, { signal: ac.signal, onProgress: (p) => p.phase === 'copy' && ac.abort() }),
    (e) => e.name === 'AbortError'
  );
  assert.equal(fs.existsSync(root), false);
  assert.equal(fs.existsSync(path.join(base, 'RealmTest')), false, 'parent folders it created are removed');
});

test('assertTestCopy needs the marker', async () => {
  const base = await tmpdir();
  const plain = path.join(base, 'plain');
  await fsp.mkdir(plain);
  await assert.rejects(R.assertTestCopy(plain), /marker/);
  await assert.rejects(R.assertTestCopy(path.join(base, 'missing')), /does not exist/);
  const { root } = await makeTestCopy(base);
  assert.equal(await R.assertTestCopy(root), root);
});

test('local-only config: existing keys rewritten, encoding kept, previous file backed up', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const c = R.cfgPaths(root);
  assert.equal((await R.applyLocalOnlyConfig(root))[0].missingFile, true);
  await write(c.server, S.encodeText("isPrivate = 'False'\r\nserverName = 'X'\r\nbindIP = '0.0.0.0'\r\nportNumber = '7350'\r\n", 'utf8bom'));
  await write(c.console, "enableRCon = 'True'\nrConPort = '27015'\nrConPassword = ''\n");
  const res = await R.applyLocalOnlyConfig(root);
  assert.deepEqual(res[0].changes.map((x) => x.key).sort(), ['bindIP', 'isPrivate']);
  assert.deepEqual(res[1].changes.map((x) => x.key).sort(), ['enableRCon', 'rConPort']);
  const buf = await fsp.readFile(c.server);
  assert.deepEqual([...buf.slice(0, 3)], [0xef, 0xbb, 0xbf], 'BOM kept');
  const text = S.decodeText(buf).text;
  assert.equal(S.getCfgValue(text, 'bindIP'), '127.0.0.1');
  assert.ok(text.includes('\r\n'));
  assert.equal((await fsp.readdir(c.backupDir)).length, 2);
  assert.equal((await R.applyLocalOnlyConfig(root))[0].changes.length, 0, 'second run changes nothing');

  const w = await R.writeServerSettings(root, { serverName: 'Realm Trial', maxPlayers: 40, port: 7351 });
  assert.deepEqual(w.changes.map((x) => x.key), ['serverName', 'portNumber']);
  assert.deepEqual(w.missing, ['maxPlayers'], 'missing keys are reported, never appended');
  const s = await R.readServerSettings(root);
  assert.equal(s.serverName, 'Realm Trial');
  assert.equal(s.maxPlayers, null);
});

test('Oxide install backs up every overwritten file, redirects ROK_Data, and rollback restores', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  // Simulate a server whose data folder is Server_Data.
  await fsp.rename(path.join(root, 'ROK_Data'), path.join(root, 'Server_Data'));
  const zip = path.join(base, 'oxide.zip');
  await makeZip(zip, [
    ['ROK_Data/', ''],
    ['ROK_Data/Managed/', ''],
    ['ROK_Data/Managed/Assembly-CSharp.dll', 'patched assembly'],
    ['ROK_Data/Managed/Oxide.Core.dll', 'oxide core'],
    ['ROK_Data/Managed/x86/sqlite3.dll', 'sqlite']
  ]);
  const expected = await sha(zip);
  await assert.rejects(R.installOxide(root, zip), /SHA-256/, 'the real Oxide hash is enforced by default');

  const res = await R.installOxide(root, zip, { expectedSha256: expected });
  assert.equal(res.dataFolder, 'Server_Data');
  assert.equal(res.overwritten, 1);
  assert.equal(res.created, 2);
  const managed = path.join(root, 'Server_Data', 'Managed');
  assert.equal(await fsp.readFile(path.join(managed, 'Assembly-CSharp.dll'), 'utf8'), 'patched assembly');
  assert.equal(await R.oxideInstalled(root), 'Server_Data');
  const manifest = JSON.parse(await fsp.readFile(path.join(res.backupDir, 'install.json'), 'utf8'));
  assert.deepEqual(manifest.overwritten, ['Server_Data\\Managed\\Assembly-CSharp.dll']);
  assert.equal(await fsp.readFile(path.join(res.backupDir, 'files', 'Server_Data', 'Managed', 'Assembly-CSharp.dll'), 'utf8'), 'vanilla assembly');
  assert.equal((await R.installOxide(root, zip, { expectedSha256: expected })).skipped, true);

  const rb = await R.rollbackOxide(root);
  assert.equal(rb.restored, 1);
  assert.equal(rb.removed, 2);
  assert.equal(await fsp.readFile(path.join(managed, 'Assembly-CSharp.dll'), 'utf8'), 'vanilla assembly');
  assert.equal(fs.existsSync(path.join(managed, 'Oxide.Core.dll')), false);
  assert.equal(await R.oxideInstalled(root), null);
  assert.ok(fs.existsSync(res.backupDir + '-rolledback'));
  await assert.rejects(R.rollbackOxide(root), /no Oxide install backup/);
});

test('Oxide install refuses a zip with entries outside ROK_Data before writing anything', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const zip = path.join(base, 'evil.zip');
  await makeZip(zip, [['ROK_Data/Managed/Oxide.Core.dll', 'x'], ['Server.exe', 'replaced!']]);
  await assert.rejects(R.installOxide(root, zip, { expectedSha256: await sha(zip) }), /outside ROK_Data/);
  assert.equal(await fsp.readFile(path.join(root, 'Server.exe'), 'utf8'), 'MZ fake wrapper');
  assert.equal(fs.existsSync(path.join(root, '_realm-backups')), false);
});

test('download: progress, SHA-256 check, .part removed on mismatch, good file reused', async () => {
  const base = await tmpdir();
  const body = crypto.randomBytes(200 * 1024);
  const good = crypto.createHash('sha256').update(body).digest('hex');
  const srv = http.createServer((req, res) => {
    res.writeHead(200, { 'Content-Length': body.length });
    res.end(body);
  });
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const url = `http://127.0.0.1:${srv.address().port}/oxide.zip`;
  try {
    const file = path.join(base, 'downloads', 'oxide.zip');
    await assert.rejects(R.downloadOxide(file, { fetchImpl: fetch, url, expectedSha256: '0'.repeat(64) }), /SHA-256 mismatch/);
    assert.deepEqual(await fsp.readdir(path.join(base, 'downloads')), []);
    let last = null;
    const r = await R.downloadOxide(file, { fetchImpl: fetch, url, expectedSha256: good, onProgress: (p) => (last = p) });
    assert.equal(r.skipped, false);
    assert.equal(last.done, body.length);
    assert.equal(last.total, body.length);
    assert.equal((await R.downloadOxide(file, { fetchImpl: fetch, url, expectedSha256: good })).skipped, true);
  } finally {
    srv.close();
  }
});

test('plugin deploy: new, changed (backed up) and unchanged; others left alone', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const src = path.join(base, 'plugins');
  await write(path.join(src, 'A.cs'), 'class A {}');
  await write(path.join(src, 'B.cs'), 'class B {}');
  await write(path.join(src, 'notes.txt'), 'ignored');
  const first = await R.deployPlugins(root, src);
  assert.equal(first.copied, 2);
  assert.equal(first.target, path.join(root, 'oxide', 'plugins'));
  await write(path.join(src, 'B.cs'), 'class B { int x; }');
  await write(path.join(first.target, 'Other.cs'), 'class Other {}');
  const second = await R.deployPlugins(root, src);
  assert.deepEqual(second.items.map((i) => `${i.name}:${i.state}`), ['A.cs:unchanged', 'B.cs:changed']);
  assert.deepEqual(second.others, ['Other.cs']);
  assert.equal(await fsp.readFile(path.join(second.backupDir, 'B.cs'), 'utf8'), 'class B {}');
  assert.ok(fs.existsSync(path.join(first.target, 'Other.cs')));
  assert.equal((await R.deployPlugins(root, src)).copied, 0);
});

test('data deploy: sculptures, painter bundle and quest content land in oxide\\data; damaged sources refused', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const res = path.join(base, 'res');
  const sets = R.dataSets(res, true);
  await write(path.join(res, 'realm-data', 'RealmSculptor', 'old-throne.json'), '{"format":"realm-sculpture/1","id":"old-throne"}');
  await write(path.join(res, 'realm-data', 'RealmSculptor', 'broken.json'), '{"format":"realm-sculp');
  await write(path.join(res, 'realm-data', 'RealmSculptor', 'notes.txt'), 'not data');
  await write(path.join(res, 'realm-data', 'RealmPainterArt.json'), '{"Format":1,"Version":"abc123"}');
  await write(path.join(res, 'realm-data', 'Other.json'), '{"not":"shipped"}');
  await write(path.join(res, 'realm-data', 'RealmQuests', 'Dailies.json'), '{"Version":1,"Quests":[]}');
  await write(path.join(res, 'realm-data', 'RealmQuests', 'Null.json'), 'null');

  const first = await R.deployData(root, sets);
  const data = path.join(root, 'oxide', 'data');
  assert.equal(first.target, data);
  assert.equal(first.copied, 3);
  assert.equal(first.invalid, 2);
  assert.deepEqual(first.reload, ['RealmPainter', 'RealmQuests', 'RealmSculptor']);
  assert.ok(fs.existsSync(path.join(data, 'RealmSculptor', 'old-throne.json')));
  assert.ok(!fs.existsSync(path.join(data, 'RealmSculptor', 'broken.json')), 'a damaged source is never copied');
  assert.ok(!fs.existsSync(path.join(data, 'Other.json')), 'only the painter bundle comes from the realm-data root');
  assert.ok(fs.existsSync(path.join(data, 'RealmPainterArt.json')));
  assert.ok(fs.existsSync(path.join(data, 'RealmQuests', 'Dailies.json')));
  assert.ok(!fs.existsSync(path.join(data, 'RealmQuests', 'Null.json')));
  const painter = first.sets.find((s) => s.id === 'paintings');
  assert.equal(painter.version, 'abc123');
  assert.equal(first.items.find((i) => i.name === 'broken.json').state, 'invalid');
  assert.match(first.items.find((i) => i.name === 'broken.json').reason, /damaged JSON/);
  assert.equal(first.backupDir, null);

  // A damaged source never replaces a good file already on the server.
  await write(path.join(data, 'RealmSculptor', 'broken.json'), '{"id":"broken","good":true}');
  // The owner's own sculpture is listed, never touched; a changed file is backed up first.
  await write(path.join(data, 'RealmSculptor', 'my-own.json'), '{"id":"my-own"}');
  await write(path.join(res, 'realm-data', 'RealmPainterArt.json'), '{"Format":1,"Version":"def456"}');
  const second = await R.deployData(root, sets);
  assert.equal(second.copied, 1);
  assert.deepEqual(second.reload, ['RealmPainter']);
  assert.equal(await fsp.readFile(path.join(data, 'RealmSculptor', 'broken.json'), 'utf8'), '{"id":"broken","good":true}');
  assert.deepEqual(second.sets.find((s) => s.id === 'sculptures').others, ['my-own.json']);
  assert.equal(await fsp.readFile(path.join(second.backupDir, 'RealmPainterArt.json'), 'utf8'), '{"Format":1,"Version":"abc123"}');
  assert.equal(await fsp.readFile(path.join(data, 'RealmPainterArt.json'), 'utf8'), '{"Format":1,"Version":"def456"}');
  assert.ok(!fs.readdirSync(data).some((n) => n.endsWith('.realm-part')));

  const third = await R.deployData(root, sets);
  assert.equal(third.copied, 0);
  assert.deepEqual(third.reload, []);
});

test('data deploy: the development sources are the repository files', async () => {
  const repo = path.join(__dirname, '..', '..');
  const sets = R.dataSets(repo, false);
  assert.deepEqual(sets.map((s) => s.plugin), ['RealmSculptor', 'RealmPainter', 'RealmQuests']);
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const plan = await R.planData(root, sets);
  for (const s of plan.sets) {
    assert.ok(s.files >= 1, `${s.id} ships files`);
    assert.equal(s.invalid, 0, `${s.id}: every shipped file parses`);
  }
  assert.ok(plan.sets.find((s) => s.id === 'sculptures').files >= 11);
  assert.match(plan.sets.find((s) => s.id === 'paintings').version, /^[0-9a-f]{16}$/);
});

test('plugin deploy prefers an existing Saves\\oxide folder', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  await fsp.mkdir(path.join(root, 'Saves', 'oxide', 'plugins'), { recursive: true });
  await fsp.mkdir(path.join(root, 'Saves', 'oxide', 'data'), { recursive: true });
  const src = path.join(base, 'plugins');
  await write(path.join(src, 'A.cs'), 'class A {}');
  assert.equal((await R.deployPlugins(root, src)).target, path.join(root, 'Saves', 'oxide', 'plugins'));
  assert.equal(await R.chronicleDataDir(root), path.join(root, 'Saves', 'oxide', 'data'));
});

test('backup and restore round-trip; the current world is moved aside, never deleted', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  await write(path.join(root, 'Saves', 'world', 'a.sav'), 'version 1');
  await write(path.join(root, 'oxide', 'data', 'RealmState.json'), '{"king":"A"}');
  const b = await R.createBackup(root);
  assert.equal(path.dirname(b.file), path.join(base, 'RealmTest', 'backups'));
  assert.equal(b.files, 2);
  const names = (await Z.listZip(b.file)).map((e) => e.name).sort();
  assert.deepEqual(names, ['Saves/world/a.sav', 'oxide/data/RealmState.json', 'realm-backup.json']);

  await write(path.join(root, 'Saves', 'world', 'a.sav'), 'version 2');
  await write(path.join(root, 'Saves', 'world', 'b.sav'), 'new file');
  await new Promise((r) => setTimeout(r, 1100)); // distinct timestamp for the safety backup
  const r = await R.restoreBackup(root, b.name);
  assert.equal(r.restored, 2);
  assert.equal(await fsp.readFile(path.join(root, 'Saves', 'world', 'a.sav'), 'utf8'), 'version 1');
  assert.equal(fs.existsSync(path.join(root, 'Saves', 'world', 'b.sav')), false);
  assert.equal(await fsp.readFile(path.join(r.aside, 'Saves', 'world', 'a.sav'), 'utf8'), 'version 2');
  assert.match(r.safetyBackup, /pre-restore\.zip$/);
  const list = await R.listBackups(root);
  assert.equal(list.length, 2);
});

test('restore refuses foreign zips and bad names', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const dir = R.backupsDir(root);
  await fsp.mkdir(dir, { recursive: true });
  await makeZip(path.join(dir, 'realm-saves-foreign.zip'), [['Saves/a.sav', 'x']]);
  await assert.rejects(R.restoreBackup(root, 'realm-saves-foreign.zip'), /realm-backup.json/);
  await makeZip(path.join(dir, 'realm-saves-evil.zip'), [['realm-backup.json', '{}'], ['ROK_Data/Managed/Assembly-CSharp.dll', 'x']]);
  await assert.rejects(R.restoreBackup(root, 'realm-saves-evil.zip'), /Unexpected entry/);
  await assert.rejects(R.restoreBackup(root, '../../etc/passwd'), /Pick a backup/);
  await assert.rejects(R.createBackup(root), /Nothing to back up/);
});

test('findSteamServer uses extra candidates off Windows', async () => {
  const base = await tmpdir();
  const steam = path.join(base, 'Reign Of Kings Dedicated Server');
  await makeSteamServer(steam);
  const r = await R.findSteamServer({ platform: 'linux', extra: [path.join(base, 'nope'), steam] });
  assert.equal(r.found, steam);
  const none = await R.findSteamServer({ platform: 'linux' });
  assert.equal(none.found, null);
});
