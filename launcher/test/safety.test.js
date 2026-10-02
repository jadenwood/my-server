// Unit tests for lib/safety.js (pure logic). Run: npm test
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const S = require('../lib/safety');

const W = { platform: 'win32' };
const STEAM_SERVER = 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings Dedicated Server';

test('test-copy folder: default is accepted and normalised', () => {
  assert.deepEqual(S.checkTestRoot('G:\\RealmTest\\server', W), { ok: true, root: 'G:\\RealmTest\\server' });
  assert.equal(S.checkTestRoot('G:/RealmTest/server/', W).root, 'G:\\RealmTest\\server');
  assert.equal(S.checkTestRoot('  D:\\Realm\\srv\\\\ ', W).root, 'D:\\Realm\\srv');
});

test('test-copy folder: refuses C:, Steam folders, steamapps and drive roots', () => {
  const refused = [
    'C:\\RealmTest\\server',
    'c:\\anything',
    'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings Dedicated Server',
    'G:\\Games\\steamapps\\copy',
    'D:\\Steam\\realm',
    'D:\\games\\steam\\realm',
    'G:\\SteamLibrary\\RealmTest',
    'G:\\',
    'RealmTest\\server',
    '\\\\nas\\share\\realm',
    '',
    '   ',
    'G:\\Realm<Test>'
  ];
  for (const p of refused) assert.equal(S.checkTestRoot(p, W).ok, false, `should refuse ${JSON.stringify(p)}`);
  assert.match(S.checkTestRoot('C:\\RealmTest\\server', W).reason, /C:/);
  assert.match(S.checkTestRoot('G:\\x\\steamapps\\y', W).reason, /steamapps/);
});

test('test-copy folder: refuses overlap with the Steam server or Steam install', () => {
  assert.equal(S.checkTestRoot('G:\\Copies\\server', { ...W, steamServer: 'G:\\Copies' }).ok, false);
  assert.equal(S.checkTestRoot('G:\\Copies', { ...W, steamServer: 'G:\\Copies\\server' }).ok, false);
  assert.equal(S.checkTestRoot('E:\\Valve\\Realm', { ...W, steamRoot: 'e:\\valve' }).ok, false);
  assert.equal(S.checkTestRoot('G:\\RealmTest\\server', { ...W, steamServer: STEAM_SERVER, steamLibraries: ['G:\\'] }).ok, true);
  assert.equal(S.checkTestRoot('G:\\RealmTest\\server', { ...W, steamServer: STEAM_SERVER, steamLibraries: ['G:\\Libraries\\Main'] }).ok, true);
  assert.equal(S.checkTestRoot('G:\\Libraries\\Main\\x', { ...W, steamLibraries: ['G:\\Libraries\\Main'] }).ok, false);
});

test('test-copy folder: posix rules', () => {
  const P = { platform: 'linux' };
  assert.equal(S.checkTestRoot('/srv/realm/server', P).ok, true);
  assert.equal(S.checkTestRoot('/home/u/.local/share/Steam/x', P).ok, false);
  assert.equal(S.checkTestRoot('/', P).ok, false);
  assert.equal(S.checkTestRoot('relative/dir', P).ok, false);
});

test('isInside: case-insensitive on Windows, no prefix tricks', () => {
  assert.equal(S.isInside('g:\\realmtest\\SERVER\\Saves', 'G:\\RealmTest\\server', 'win32'), true);
  assert.equal(S.isInside('G:\\RealmTest\\server', 'G:\\RealmTest\\server\\', 'win32'), true);
  assert.equal(S.isInside('G:\\RealmTest\\server2', 'G:\\RealmTest\\server', 'win32'), false);
  assert.equal(S.isInside('G:\\RealmTest\\server\\..\\other', 'G:\\RealmTest\\server', 'win32'), false);
  assert.equal(S.isInside('/a/B', '/a/b', 'linux'), false);
});

test('realmHome sits next to the server folder', () => {
  assert.equal(S.realmHome('G:\\RealmTest\\server', 'win32'), 'G:\\RealmTest');
});

const CFG = [
  '# -- Server --',
  "isPrivate = 'False'          # hide from lobby list",
  "serverName = 'My Server'",
  "maxPlayers = '30'",
  "bindIP = '0.0.0.0'",
  "portNumber = '7350'",
  "pingPort = '7350'",
  ''
].join('\r\n');

test('cfg: reads values', () => {
  assert.equal(S.getCfgValue(CFG, 'serverName'), 'My Server');
  assert.equal(S.getCfgValue(CFG, 'isPrivate'), 'False');
  assert.equal(S.getCfgValue(CFG, 'nope'), null);
  assert.equal(S.getCfgValue(CFG, 'Port'), null, 'no partial key match');
});

test('cfg: rewrites existing keys only, keeps CRLF and trailing comments', () => {
  const r = S.rewriteCfg(CFG, { bindIP: '127.0.0.1', isPrivate: 'True', portNumber: '7350', notThere: 'x' });
  assert.deepEqual(r.changes, [
    { key: 'bindIP', from: '0.0.0.0', to: '127.0.0.1' },
    { key: 'isPrivate', from: 'False', to: 'True' }
  ]);
  assert.deepEqual(r.missing, ['notThere']);
  assert.ok(r.text.includes("isPrivate = 'True'          # hide from lobby list\r\n"));
  assert.ok(r.text.includes("bindIP = '127.0.0.1'\r\n"));
  assert.ok(!r.text.includes('notThere'));
  assert.equal(r.text.split('\r\n').length, CFG.split('\r\n').length);
  assert.equal(S.getCfgValue(r.text, 'pingPort'), '7350', 'other keys untouched');
});

test('cfg: no change returns the original text; LF files stay LF', () => {
  const r = S.rewriteCfg(CFG, { portNumber: '7350' });
  assert.equal(r.text, CFG);
  assert.equal(r.changes.length, 0);
  const lf = "a = '1'\nb = '2'\n";
  assert.equal(S.rewriteCfg(lf, { b: '3' }).text, "a = '1'\nb = '3'\n");
});

test('cfg: values that could break the file are refused', () => {
  assert.throws(() => S.rewriteCfg(CFG, { serverName: "it's" }));
  assert.throws(() => S.rewriteCfg(CFG, { serverName: 'a\r\nbindIP = 1' }));
  assert.throws(() => S.rewriteCfg(CFG, { 'bad key': 'x' }));
});

test('cfg: encoding round-trips (UTF-8, UTF-8 BOM, UTF-16 LE)', () => {
  for (const enc of ['utf8', 'utf8bom', 'utf16le']) {
    const buf = S.encodeText("serverName = 'Ærendel'", enc);
    const back = S.decodeText(buf);
    assert.equal(back.encoding, enc);
    assert.equal(back.text, "serverName = 'Ærendel'");
  }
});

test('zip-slip: unsafe names', () => {
  for (const n of ['../evil.dll', 'ROK_Data/../../evil', '/etc/passwd', 'C:/Windows/x', 'c:\\x', 'a/../../b', '..', 'ROK_Data\\..\\..\\x', 'a\0b']) {
    assert.equal(S.isUnsafeEntryName(n), true, n);
  }
  for (const n of ['ROK_Data/Managed/Oxide.Core.dll', 'Saves/world..bak', 'a/..b/c']) assert.equal(S.isUnsafeEntryName(n), false, n);
});

test('Oxide plan: ROK_Data redirected to the detected data folder, directories skipped', () => {
  const plan = S.planOxideEntries(['ROK_Data/', 'ROK_Data/Managed/', 'ROK_Data/Managed/Oxide.Core.dll', 'ROK_Data/Managed/x86/sqlite3.dll'], 'Server_Data');
  assert.deepEqual(plan.map((p) => p.rel), ['Server_Data/Managed/Oxide.Core.dll', 'Server_Data/Managed/x86/sqlite3.dll']);
  assert.equal(plan[0].entry, 'ROK_Data/Managed/Oxide.Core.dll');
});

test('Oxide plan: refuses unexpected or unsafe entries and bad data folders', () => {
  assert.throws(() => S.planOxideEntries(['ROK_Data/Managed/a.dll', 'readme.txt'], 'ROK_Data'), /outside ROK_Data/);
  assert.throws(() => S.planOxideEntries(['ROK_Data/../../evil.dll'], 'ROK_Data'), /Unsafe/);
  assert.throws(() => S.planOxideEntries(['ROK_Data/a'], '..\\x_Data'), /Data folder/);
  assert.throws(() => S.planOxideEntries(['ROK_Data/'], 'ROK_Data'), /no files/);
});

test('restore plan: needs the manifest and only Saves/ or oxide/data/', () => {
  const p = S.planRestoreEntries(['realm-backup.json', 'Saves/', 'Saves/w/1.sav', 'oxide/data/RealmState.json']);
  assert.deepEqual(p.tops.sort(), ['Saves', 'oxide/data']);
  assert.equal(p.files.length, 2);
  assert.throws(() => S.planRestoreEntries(['Saves/a']), /realm-backup.json/);
  assert.throws(() => S.planRestoreEntries(['realm-backup.json', 'ROK_Data/Managed/x.dll']), /Unexpected/);
  assert.throws(() => S.planRestoreEntries(['realm-backup.json', 'Saves/../../x']), /Unsafe/);
});

test('safeJoin keeps paths inside the root', () => {
  assert.equal(S.safeJoin('G:\\R\\server', 'Saves/a.sav', 'win32'), 'G:\\R\\server\\Saves\\a.sav');
  assert.throws(() => S.safeJoin('G:\\R\\server', '../x', 'win32'));
  assert.throws(() => S.safeJoin('/r/server', '/etc/x', 'linux'));
});

test('pickDataFolder prefers ROK_Data', () => {
  assert.equal(S.pickDataFolder(['Server_Data', 'ROK_Data']), 'ROK_Data');
  assert.equal(S.pickDataFolder(['Server_Data']), 'Server_Data');
  assert.equal(S.pickDataFolder(['bad']), null);
});

test('vdf: new libraryfolders format', () => {
  const vdf = `"libraryfolders"
{
\t"0"
\t{
\t\t"path"\t\t"C:\\\\Program Files (x86)\\\\Steam"
\t\t"label"\t\t""
\t\t"apps"
\t\t{
\t\t\t"228980"\t\t"123"
\t\t}
\t}
\t"1"
\t{
\t\t"path"\t\t"G:\\\\SteamLibrary"   // comment
\t\t"apps" { "381690" "456" }
\t}
}`;
  assert.deepEqual(S.parseLibraryFolders(vdf), ['C:\\Program Files (x86)\\Steam', 'G:\\SteamLibrary']);
});

test('vdf: old LibraryFolders format and junk', () => {
  const old = '"LibraryFolders"\n{\n\t"TimeNextStatsReport"\t\t"1"\n\t"ContentStatsID"\t\t"-1"\n\t"1"\t\t"D:\\\\Games\\\\SteamLibrary"\n}\n';
  assert.deepEqual(S.parseLibraryFolders(old), ['D:\\Games\\SteamLibrary']);
  assert.deepEqual(S.parseLibraryFolders('not a vdf {{{'), []);
  assert.deepEqual(S.parseLibraryFolders(''), []);
});

test('registry SteamPath parsing and candidate list', () => {
  const out = '\r\nHKEY_CURRENT_USER\\Software\\Valve\\Steam\r\n    SteamPath    REG_SZ    c:/program files (x86)/steam\r\n\r\n';
  assert.equal(S.parseRegSteamPath(out), 'c:\\program files (x86)\\steam');
  assert.equal(S.parseRegSteamPath('ERROR: The system was unable to find the specified registry key or value.'), null);
  const c = S.steamServerCandidates('c:\\program files (x86)\\steam', ['C:\\Program Files (x86)\\Steam', 'G:\\SteamLibrary']);
  assert.deepEqual(c, [S.DEFAULT_STEAM_SERVER, 'c:\\program files (x86)\\steam\\steamapps\\common\\Reign Of Kings Dedicated Server']);
});

test('sha256 comparison', () => {
  assert.equal(S.sha256Matches(S.OXIDE.sha256.toUpperCase(), S.OXIDE.sha256), true);
  assert.equal(S.sha256Matches('00', S.OXIDE.sha256), false);
  assert.equal(S.sha256Matches(undefined, S.OXIDE.sha256), false);
  assert.equal(S.OXIDE.sha256, '6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8');
});

test('local-only defaults mirror Start-LocalServer.ps1', () => {
  assert.deepEqual({ ...S.LOCAL_SERVER_SETTINGS }, { bindIP: '127.0.0.1', isPrivate: 'True' });
  assert.deepEqual({ ...S.LOCAL_CONSOLE_SETTINGS }, { rConPort: '27016', enableRCon: 'False' });
});

test('settings validation', () => {
  const ok = S.validateSettings({ serverName: ' Realm ', maxPlayers: '40', port: 7350, discordUrl: 'https://discord.gg/abc', serverExe: 'ROK', junk: 1 });
  assert.equal(ok.ok, true);
  assert.deepEqual(ok.values, { serverName: 'Realm', maxPlayers: 40, port: 7350, discordUrl: 'https://discord.gg/abc', serverExe: 'ROK' });
  const bad = S.validateSettings({ serverName: "a'b", maxPlayers: 0, port: 80, discordUrl: 'http://evil', serverExe: 'cmd' });
  assert.equal(bad.ok, false);
  assert.equal(bad.errors.length, 5);
  assert.equal(S.validateSettings({ discordUrl: '' }).values.discordUrl, '');
});

test('news validation', () => {
  assert.deepEqual(S.validateNews([{ date: '2026-10-01', title: ' Hi ', body: 'x', extra: 1 }]), [{ date: '2026-10-01', title: 'Hi', body: 'x' }]);
  assert.throws(() => S.validateNews([{ date: 'yesterday', title: 'x' }]));
  assert.throws(() => S.validateNews([{ title: '' }]));
  assert.throws(() => S.validateNews('nope'));
  assert.throws(() => S.validateNews(new Array(21).fill({ title: 'x' })));
});

test('IPC helpers', () => {
  assert.equal(S.asEnum('a', ['a', 'b']), 'a');
  assert.throws(() => S.asEnum('c', ['a', 'b']));
  assert.throws(() => S.asString(5, 10));
  assert.throws(() => S.asString('x'.repeat(11), 10));
  assert.equal(S.asInt('42', 1, 50), 42);
  assert.throws(() => S.asInt('4.2', 1, 50));
});

test('console line patterns', () => {
  assert.ok(S.READY_LINE.test('Server for 120 players started on port 7350.'));
  assert.ok(!S.READY_LINE.test('Initialize engine version: 5.x'), 'the Unity banner is not the ready line');
  assert.ok(S.ENGINE_LINE.test('Initialize engine version: 5.x'));
  assert.equal(S.JOIN_LINE.exec('Authentication verified for Wren (76561190000000000).')[1], 'Wren');
  assert.equal(S.LEAVE_LINE.exec('Wren has disconnected.')[1], 'Wren');
});
