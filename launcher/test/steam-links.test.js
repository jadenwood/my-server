// steam:// allow-list, quick-join URL, host names, realm:// deep links and install detection.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const ST = require('../lib/shared/steam');
const DL = require('../lib/shared/deeplink');

test('host names: DNS names and dotted IPv4 only', () => {
  for (const ok of ['play.example.org', 'realm-1.example.co.uk', '127.0.0.1', '203.0.113.7', 'localhost']) assert.equal(ST.isValidHost(ok), true, ok);
  for (const bad of ['', '-ip', 'a b', 'host:7350', '256.1.1.1', '1.2.3', '01.2.3.4', 'x_y.org', 'a..b', '.a', 'a.', '[::1]', 'é.org', 'x'.repeat(254), 'a%20-pass']) assert.equal(ST.isValidHost(bad), false, bad);
});

test('quick-join URL is exactly the documented steam://run form', () => {
  assert.equal(ST.quickJoinUrl('play.example.org', 7350), 'steam://run/344760//-ip%20play.example.org%20-port%207350/');
  assert.throws(() => ST.quickJoinUrl('evil -pass x', 7350), /host/);
  assert.throws(() => ST.quickJoinUrl('ok.org', 0), /port/);
  assert.throws(() => ST.quickJoinUrl('ok.org', '7350'), /port/);
});

test('steam:// allow-list', () => {
  for (const ok of ['steam://rungameid/344760', 'steam://install/344760', 'steam://run/344760//-ip%20play.example.org%20-port%207350/', 'steam://run/344760//-ip%201.2.3.4%20-port%2027015/']) {
    assert.equal(ST.isAllowedSteamUrl(ok), true, ok);
  }
  for (const bad of [
    'steam://rungameid/440',
    'steam://install/440',
    'steam://run/440//-ip%20a.org%20-port%207350/',
    'steam://run/344760//-ip%20a.org%20-port%207350%20-pass%20x/',
    'steam://run/344760//-ip%20a.org%20-port%207350%20-rejoin%201/',
    'steam://run/344760//-ip a.org -port 7350/',
    'steam://run/344760//-ip%20a.org%20-port%2099999/',
    'steam://run/344760//-ip%20-batchmode%20-port%207350/',
    'steam://run/344760//+connect%20a.org:7350/',
    'steam://connect/1.2.3.4:7350',
    'steam://rungameid/344760/../../x',
    'https://store.steampowered.com',
    'file:///C:/Windows/System32/cmd.exe',
    'STEAM://rungameid/344760'
  ]) {
    assert.equal(ST.isAllowedSteamUrl(bad), false, bad);
  }
});

test('realm:// deep links: only join/<known id>', () => {
  const known = ['s1', 's2', 'north-1'];
  assert.deepEqual(DL.parseDeepLink('realm://join/s2', known), { action: 'join', id: 's2' });
  assert.deepEqual(DL.parseDeepLink('realm://join/s1/', known), { action: 'join', id: 's1' });
  assert.deepEqual(DL.parseDeepLink('realm://join/north-1', new Set(known)), { action: 'join', id: 'north-1' });
  for (const bad of [
    'realm://join/s9',
    'realm://join/S1',
    'realm://join/s1?ip=1.2.3.4',
    'realm://join/s1#x',
    'realm://join/s1/extra',
    'realm://join/',
    'realm://join/../s1',
    'realm://join/s%31',
    'realm://probe/s1',
    'realm://JOIN/s1',
    'realms://join/s1',
    'realm:join/s1',
    'realm://join/' + 'a'.repeat(60),
    'https://example.org/join/s1',
    null,
    42
  ]) {
    assert.equal(DL.parseDeepLink(bad, known), null, String(bad));
  }
  assert.deepEqual(DL.parseDeepLink('realm://join/zz'), { action: 'join', id: 'zz' }); // shape only, no list given
  assert.equal(DL.findDeepLinkArg(['Realm.exe', '--flag', 'realm://join/s1']), 'realm://join/s1');
  assert.equal(DL.findDeepLinkArg(['Realm.exe']), null);
  assert.equal(DL.findDeepLinkArg('nope'), null);
});

test('install detection reads the registry and appmanifest_344760.acf (read-only)', async () => {
  const acfInstalled = '"AppState"\n{\n "appid" "344760"\n "name" "Reign Of Kings"\n "StateFlags" "4"\n}\n';
  const acfUpdating = acfInstalled.replace('"4"', '"1026"');
  assert.equal(ST.isInstalledManifest(acfInstalled), true);
  assert.equal(ST.isInstalledManifest(acfUpdating), false);
  assert.equal(ST.isInstalledManifest(acfInstalled.replace('344760', '381690')), false);
  assert.equal(ST.isInstalledManifest('garbage'), false);

  const reg = (path) => (file, args, opts, cb) => cb(null, path ? `HKEY_CURRENT_USER\\Software\\Valve\\Steam\n    SteamPath    REG_SZ    ${path}\n` : '');
  const files = {
    'c:\\steam\\steamapps\\libraryfolders.vdf': '"libraryfolders" { "0" { "path" "c:\\\\steam" } "1" { "path" "G:\\\\SteamLibrary" } }',
    'G:\\SteamLibrary\\steamapps\\appmanifest_344760.acf': acfInstalled
  };
  const readFile = async (p) => {
    if (p in files) return files[p];
    throw Object.assign(new Error('ENOENT'), { code: 'ENOENT' });
  };
  const yes = await ST.detectGameInstall({ platform: 'win32', execFileImpl: reg('c:/steam'), readFile });
  assert.deepEqual(yes, { steam: true, steamPath: 'c:\\steam', installed: true, library: 'G:\\SteamLibrary' });
  const no = await ST.detectGameInstall({ platform: 'win32', execFileImpl: reg('c:/steam'), readFile: async (p) => (p.endsWith('.vdf') ? files['c:\\steam\\steamapps\\libraryfolders.vdf'] : Promise.reject(new Error('x'))) });
  assert.equal(no.installed, false);
  const noSteam = await ST.detectGameInstall({ platform: 'win32', execFileImpl: reg(null), readFile });
  assert.deepEqual(noSteam, { steam: false, steamPath: null, installed: false, library: null });
  const elsewhere = await ST.detectGameInstall({ platform: 'linux' });
  assert.equal(elsewhere.installed, null);
});
