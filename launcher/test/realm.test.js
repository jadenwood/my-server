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
  assert.deepEqual(sets.map((s) => s.plugin), ['RealmSculptor', 'RealmPainter', 'RealmQuests', 'RealmArrival', null]);
  assert.equal(sets.find((s) => s.id === 'moods').src, path.join(repo, 'mods', 'presets'));
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const plan = await R.planData(root, sets);
  for (const s of plan.sets) {
    assert.ok(s.files >= 1, `${s.id} ships files`);
    assert.equal(s.invalid, 0, `${s.id}: every shipped file parses`);
  }
  assert.ok(plan.sets.find((s) => s.id === 'sculptures').files >= 11);
  assert.match(plan.sets.find((s) => s.id === 'paintings').version, /^[0-9a-f]{16}$/);
  // RealmArrival reads the site plan art/sculptures/sites/arrival.json as oxide/data/RealmArrival/site.json.
  const arrival = plan.items.filter((i) => i.set === 'arrival');
  assert.deepEqual(arrival.map((i) => [i.name, i.rel]), [['arrival.json', 'RealmArrival/site.json']]);
  assert.equal(arrival[0].dest, path.join(root, 'oxide', 'data', 'RealmArrival', 'site.json'));
  assert.equal(JSON.parse(fs.readFileSync(arrival[0].src, 'utf8')).format, 'realm-site/1');
  // The sites folder is not a sculpture: the sculptures set never picks it up.
  assert.ok(!plan.items.some((i) => i.set === 'sculptures' && /arrival/.test(i.name)));
  // Every mood in mods/presets passes the deploy's sanity check and lands in <server>/realm-moods/<id>/<id>.cfg.
  const moods = plan.items.filter((i) => i.set === 'moods');
  assert.ok(moods.length >= 14, moods.map((i) => i.name).join(', '));
  assert.ok(moods.some((i) => i.name === 'rotation.json' && i.dest === path.join(root, 'realm-moods', 'rotation.json')));
  for (const i of moods.filter((x) => x.name !== 'rotation.json')) {
    const id = i.name.split('/')[0];
    assert.equal(i.name, `${id}/${id}.cfg`);
    assert.equal(i.rel, `realm-moods/${id}/${id}.cfg`);
    assert.equal(i.dest, path.join(root, 'realm-moods', id, `${id}.cfg`));
  }
  assert.ok(!moods.some((i) => /Apply-Preset|tests/.test(i.name)), 'scripts and tests are not shipped');
});

test('mood library deploy: rotation.json and <id>/<id>.cfg go to realm-moods; Mods\\ is never touched; damaged sources refused', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const res = path.join(base, 'res');
  const sets = R.dataSets(res, true).filter((s) => s.id === 'moods');
  assert.equal(sets.length, 1);
  const src = path.join(res, 'realm-data', 'moods');
  assert.equal(sets[0].src, src);
  const good = "# Realm world mood\nAtmosphere.FogDensity = '1.1'\nWeather.ClearWeight = '6'\n#@scale Clock.DaySpeed = '0.9'\n";
  await write(path.join(src, 'rotation.json'), '{"moods":{"dawn":{}},"seasonCycle":["dawn"],"events":{}}');
  await write(path.join(src, 'dawn', 'dawn.cfg'), good);
  await write(path.join(src, 'dawn', 'Apply-Preset.ps1'), 'Write-Host');
  await write(path.join(src, 'cut', 'cut.cfg'), "Atmosphere.FogDensity = '1.1'\nWeather.ClearWeight = '6");
  await write(path.join(src, 'odd', 'odd.cfg'), "Atmosphere.FogDensity = '1.1'\nPlayer.Speed = '9'\n");
  await write(path.join(src, 'Upper', 'Upper.cfg'), good);
  await write(path.join(src, 'loose', 'other-name.cfg'), good);
  await write(path.join(src, 'notes.txt'), 'not shipped');
  // The live Mods files on the server: a mood id that happens to match a handler name must not matter.
  const mods = path.join(root, 'Mods');
  await write(path.join(mods, 'Environment.cfg'), "Atmosphere.FogDensity = '1.4'\n");
  await write(path.join(mods, 'Environment.defaults.cfg'), "Atmosphere.FogDensity = '1'\n");
  await write(path.join(mods, 'dawn.cfg'), 'the owner\'s live file');
  const modsBefore = Object.fromEntries(fs.readdirSync(mods).map((n) => [n, fs.readFileSync(path.join(mods, n), 'utf8')]));

  const lib = path.join(root, 'realm-moods');
  const first = await R.deployData(root, sets);
  assert.deepEqual(first.items.map((i) => `${i.name}:${i.state}`), ['cut/cut.cfg:invalid', 'dawn/dawn.cfg:new', 'odd/odd.cfg:invalid', 'rotation.json:new']);
  assert.equal(first.copied, 2);
  assert.equal(first.invalid, 2);
  assert.deepEqual(first.reload, [], 'no plugin reloads for the mood library');
  assert.match(first.items[0].reason, /line 2 is not a complete key = 'value' line|no line break/);
  assert.match(first.items[2].reason, /'Player\.Speed' is not a mood key/);
  assert.equal(fs.readFileSync(path.join(lib, 'dawn', 'dawn.cfg'), 'utf8'), good);
  assert.ok(fs.existsSync(path.join(lib, 'rotation.json')));
  assert.ok(!fs.existsSync(path.join(lib, 'cut')), 'a damaged mood is never copied');
  assert.ok(!fs.existsSync(path.join(lib, 'dawn', 'Apply-Preset.ps1')));
  assert.ok(!fs.existsSync(path.join(lib, 'notes.txt')));
  assert.ok(!fs.existsSync(path.join(root, 'oxide', 'data', 'realm-moods')), 'the library is not plugin data');
  const summary = first.sets[0];
  assert.equal(summary.dir, lib);
  assert.equal(summary.plugin, null);
  assert.deepEqual(Object.fromEntries(fs.readdirSync(mods).map((n) => [n, fs.readFileSync(path.join(mods, n), 'utf8')])), modsBefore, 'Mods\\ is untouched');

  // The owner's own mood and a hand-edited shipped mood: the own one is listed and kept, the edited one is
  // backed up to _realm-backups\\data-<time>\\realm-moods\\ before the shipped version replaces it.
  await write(path.join(lib, 'mine', 'mine.cfg'), "Weather.ClearWeight = '9'\n");
  await write(path.join(lib, 'dawn', 'dawn.cfg'), "Atmosphere.FogDensity = '1.3'\n");
  // A damaged source never replaces the good copy on the server.
  await write(path.join(lib, 'cut', 'cut.cfg'), "Atmosphere.FogDensity = '1.2'\n");
  const second = await R.deployData(root, sets);
  assert.equal(second.copied, 1);
  assert.deepEqual(second.sets[0].others, ['mine/mine.cfg'], 'shipped names (even refused ones) are not owner files');
  assert.equal(await fsp.readFile(path.join(second.backupDir, 'realm-moods', 'dawn', 'dawn.cfg'), 'utf8'), "Atmosphere.FogDensity = '1.3'\n");
  assert.equal(await fsp.readFile(path.join(lib, 'dawn', 'dawn.cfg'), 'utf8'), good);
  assert.equal(await fsp.readFile(path.join(lib, 'mine', 'mine.cfg'), 'utf8'), "Weather.ClearWeight = '9'\n");
  assert.equal(await fsp.readFile(path.join(lib, 'cut', 'cut.cfg'), 'utf8'), "Atmosphere.FogDensity = '1.2'\n");
  assert.ok(!fs.readdirSync(path.join(lib, 'dawn')).some((n) => n.endsWith('.realm-part')));
  assert.deepEqual(Object.fromEntries(fs.readdirSync(mods).map((n) => [n, fs.readFileSync(path.join(mods, n), 'utf8')])), modsBefore);

  // A rotation.json without moods/seasonCycle is refused.
  await write(path.join(src, 'rotation.json'), '{"events":{}}');
  const third = await R.deployData(root, sets);
  assert.equal(third.copied, 0);
  assert.match(third.items.find((i) => i.name === 'rotation.json').reason, /no "moods" object/);
});

test('mood file sanity check: text, complete lines, proven keys only, no repeats, a final line break, 64 KB', async () => {
  const ok = (t) => R.checkMoodText(Buffer.from(t));
  assert.equal(ok("Atmosphere.FogDensity = '1.1'\n").ok, true);
  assert.equal(ok("\uFEFF# c\r\nAtmosphere.FogDensity = '1.1'\r\n\r\n").ok, true, 'BOM and CRLF');
  assert.equal(ok("  Weather.ClearWeight = '3'   # trailing note\n").ok, true);
  assert.equal(ok("#@scale Clock.DaySpeed = '0.9'\n").ok, true);
  assert.match(ok("Atmosphere.FogDensity='1.1'\n").reason, /not a complete key = 'value' line/);
  assert.match(ok("Atmosphere.FogDensity = '1.1\n").reason, /not a complete/);
  assert.match(ok("Atmosphere.FogDensity = '1.1'").reason, /no line break at the end/);
  assert.match(ok("Atmosphere.FogDensity = '1.1'\nAtmosphere.FogDensity = '1.2'\n").reason, /set twice/);
  assert.match(ok("Atmosphere.FogDensity = ''\n").reason, /no value/);
  assert.match(ok("Server.Name = 'x'\n").reason, /not a mood key/);
  assert.match(ok("#@scale Server.Name = '2'\n").reason, /not a mood key/);
  assert.match(ok('# only comments\n\n').reason, /no mood lines/);
  assert.match(ok("Atmosphere.FogDensity = '1.1'\n\u0000\n").reason, /control characters/);
  assert.match(R.checkMoodText(Buffer.from([0x41, 0xff, 0xfe, 0x0a])).reason, /not UTF-8/);
  const base = await tmpdir();
  const big = path.join(base, 'big.cfg');
  await write(big, '#'.repeat(70 * 1024) + "\nAtmosphere.FogDensity = '1.1'\n");
  assert.match((await R.checkMoodFile(big)).reason, /larger than/);
  const empty = path.join(base, 'empty.cfg');
  await write(empty, '');
  assert.equal((await R.checkMoodFile(empty)).reason, 'empty');
  assert.equal((await R.checkMoodFile(path.join(base, 'none.cfg'))).reason, 'missing');
});

test('the mood keys the deploy accepts are exactly Set-Mood.ps1 $MoodKeys', () => {
  const ps = fs.readFileSync(path.join(__dirname, '..', '..', 'server', 'Set-Mood.ps1'), 'utf8');
  const block = ps.slice(ps.indexOf('$MoodKeys = @{'), ps.indexOf('$WeightOrder'));
  const keys = [...block.matchAll(/^\s*'([A-Za-z.]+)'\s*=\s*@\{/gm)].map((m) => m[1]);
  assert.equal(keys.length, 12);
  assert.deepEqual([...R.MOOD_KEYS].sort(), keys.sort());
  const deploy = fs.readFileSync(path.join(__dirname, '..', '..', 'server', 'Deploy-Plugins.ps1'), 'utf8');
  const dkeys = [...deploy.slice(deploy.indexOf('$moodKeys = @('), deploy.indexOf('$moodKeys = @(') + 2000).matchAll(/'([A-Za-z]+\.[A-Za-z]+)'/g)].map((m) => m[1]);
  assert.deepEqual(dkeys.sort(), keys.sort(), 'Deploy-Plugins.ps1 checks the same keys');
});

test('data deploy: the arrival site plan is written as RealmArrival/site.json with backups and the usual rules', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const res = path.join(base, 'res');
  const sets = R.dataSets(res, true).filter((s) => s.id === 'arrival');
  assert.equal(sets.length, 1);
  assert.equal(sets[0].src, path.join(res, 'realm-data', 'RealmArrival'));
  const srcDir = sets[0].src;
  const dir = path.join(root, 'oxide', 'data', 'RealmArrival');
  await write(path.join(srcDir, 'arrival.json'), '{"format":"realm-site/1","id":"arrival","v":1}');
  await write(path.join(srcDir, 'other-site.json'), '{"format":"realm-site/1","id":"other"}');
  await write(path.join(srcDir, 'site.json'), '{"not":"shipped"}');

  const first = await R.deployData(root, sets);
  assert.equal(first.copied, 1);
  assert.deepEqual(first.reload, ['RealmArrival']);
  assert.deepEqual(fs.readdirSync(dir).sort(), ['site.json'], 'only arrival.json is shipped, and only under its destination name');
  assert.equal(await fsp.readFile(path.join(dir, 'site.json'), 'utf8'), '{"format":"realm-site/1","id":"arrival","v":1}');
  const summary = first.sets[0];
  assert.deepEqual(summary.renamed, [{ from: 'arrival.json', to: 'site.json' }]);
  assert.deepEqual(summary.others, [], 'the deployed site.json is not listed as an owner file');
  assert.equal(first.items[0].name, 'arrival.json');
  assert.equal(first.items[0].destName, 'site.json');
  assert.equal(first.items[0].rel, 'RealmArrival/site.json');

  // Unchanged: nothing to do. The owner's own file in the folder is listed, never touched.
  await write(path.join(dir, 'my-notes.json'), '{"mine":true}');
  const second = await R.deployData(root, sets);
  assert.equal(second.copied, 0);
  assert.equal(second.items[0].state, 'unchanged');
  assert.deepEqual(second.sets[0].others, ['my-notes.json']);

  // Changed: the server's site.json is backed up under its own name before it is replaced.
  await write(path.join(srcDir, 'arrival.json'), '{"format":"realm-site/1","id":"arrival","v":2}');
  const third = await R.deployData(root, sets);
  assert.equal(third.copied, 1);
  assert.equal(await fsp.readFile(path.join(third.backupDir, 'RealmArrival', 'site.json'), 'utf8'), '{"format":"realm-site/1","id":"arrival","v":1}');
  assert.equal(await fsp.readFile(path.join(dir, 'site.json'), 'utf8'), '{"format":"realm-site/1","id":"arrival","v":2}');
  assert.ok(!fs.readdirSync(dir).some((n) => n.endsWith('.realm-part')));
  assert.equal(await fsp.readFile(path.join(dir, 'my-notes.json'), 'utf8'), '{"mine":true}');

  // A damaged or non-object source is refused; the good site.json on the server stays.
  for (const bad of ['{"format":"realm-si', '[1,2]']) {
    await write(path.join(srcDir, 'arrival.json'), bad);
    const r = await R.deployData(root, sets);
    assert.equal(r.copied, 0);
    assert.equal(r.invalid, 1);
    assert.equal(r.items[0].state, 'invalid');
    assert.equal(r.items[0].rel, 'RealmArrival/site.json');
    assert.equal(await fsp.readFile(path.join(dir, 'site.json'), 'utf8'), '{"format":"realm-site/1","id":"arrival","v":2}');
  }
});

test('data deploy: an `as` rename to an unsafe name is ignored (the file keeps its own name)', async () => {
  const base = await tmpdir();
  const { root } = await makeTestCopy(base);
  const src = path.join(base, 'src');
  await write(path.join(src, 'a.json'), '{"a":1}');
  const sets = [{ id: 't', label: 'T', plugin: 'RealmTest', src, dest: 'RealmTest', only: null, as: { 'a.json': '../escape.json' }, versionKey: null }];
  const plan = await R.planData(root, sets);
  assert.equal(plan.items[0].rel, 'RealmTest/a.json');
  assert.deepEqual(plan.sets[0].renamed, []);
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
