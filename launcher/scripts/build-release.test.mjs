// Release packaging: installer names and metadata, the release helper, and a static read of Build-Realm.bat
// (this machine cannot run Windows batch files). Run: node --test scripts/build-release.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import {
  LAUNCHER, REPO, EDITIONS, readVersion, loadEditionConfig, installerName, releaseDir, sha256File, checksumsText,
  collectRelease, playerConfigWarning, electronBuilderCacheDir, ensureWinCodeSign, explainBuildError, WIN_CODE_SIGN
} from './build-release.mjs';

const VERSION = readVersion();
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'realm-release-'));

test('the launcher version is 1.0.0 and both installers get the release names', () => {
  assert.equal(VERSION, '1.0.0');
  assert.equal(installerName(loadEditionConfig('steward'), VERSION), 'Realm-Steward-Setup-1.0.0.exe');
  assert.equal(installerName(loadEditionConfig('player'), VERSION), 'Realm-Setup-1.0.0.exe');
  assert.equal(releaseDir(VERSION), path.join(REPO, 'release', '1.0.0'));
});

test('installerName refuses macros it cannot resolve', () => {
  assert.throws(() => installerName({ nsis: { artifactName: '${productName}-${version}.${ext}' } }, '1.0.0'), /macro/);
  assert.throws(() => installerName({}, '1.0.0'), /artifactName/);
});

test('installer metadata: names, icon, shortcuts, per-user, uninstall keeps settings', () => {
  const st = loadEditionConfig('steward');
  const pl = loadEditionConfig('player');
  assert.equal(st.productName, 'Realm Steward');
  assert.equal(pl.productName, 'Realm');
  assert.equal(pl.extraMetadata.productName, 'Realm');
  // The Steward keeps the app id of the earlier owner app, so 1.0.0 installs over 0.x in place.
  assert.equal(st.appId, 'community.realm.launcher');
  assert.equal(pl.appId, 'community.realm.player');
  for (const [cfg, shortcut] of [[st, 'Realm Steward'], [pl, 'Realm']]) {
    const n = cfg.nsis;
    assert.equal(n.oneClick, false);
    assert.equal(n.perMachine, false, 'per-user install, no administrator rights');
    assert.equal(n.allowElevation, false);
    assert.equal(n.createDesktopShortcut, true);
    assert.equal(n.createStartMenuShortcut, true);
    assert.equal(n.shortcutName, shortcut);
    assert.equal(n.deleteAppDataOnUninstall, false, 'uninstall must keep %APPDATA% settings');
    assert.equal(cfg.publish, null, 'the build never publishes');
    assert.equal(cfg.win.icon, 'build/icon.png');
    assert.ok(cfg.files.includes('build/icon.png'));
    assert.deepEqual(cfg.win.target.map((t) => t.target).sort(), ['nsis', 'portable']);
  }
  // A 256x256 PNG is the smallest icon electron-builder accepts for Windows.
  const png = fs.readFileSync(path.join(LAUNCHER, 'build', 'icon.png'));
  assert.equal(png.subarray(1, 4).toString(), 'PNG');
  assert.ok(png.readUInt32BE(16) >= 256 && png.readUInt32BE(20) >= 256, 'icon.png must be at least 256x256');
});

test('the player uninstaller removes only its own realm:// handler', () => {
  const pl = loadEditionConfig('player');
  assert.equal(pl.nsis.include, 'build/player-uninstall.nsh');
  const nsh = fs.readFileSync(path.join(LAUNCHER, pl.nsis.include), 'utf8');
  assert.match(nsh, /!macro customUnInstall/);
  assert.match(nsh, /\$\{ifNot\} \$\{isUpdated\}/);
  assert.match(nsh, /\$\{if\} \$0 == '"\$INSTDIR\\\$\{APP_EXECUTABLE_FILENAME\}" "%1"'/);
  // electron-builder includes build/installer.nsh in every config by itself; the player's script must not be that.
  assert.ok(!fs.existsSync(path.join(LAUNCHER, 'build', 'installer.nsh')));
  assert.equal(loadEditionConfig('steward').nsis.include, undefined);
});

test('the Steward installer still carries the plugins and the Chronicle', () => {
  const st = loadEditionConfig('steward');
  const from = st.extraResources.map((r) => r.from).sort();
  assert.deepEqual(from, ['../art/paintings', '../art/sculptures', '../art/sculptures/sites', '../chronicle', '../plugins', '../plugins/docs/RealmQuests/content']);
  assert.ok(fs.readdirSync(path.join(REPO, 'plugins')).filter((f) => f.endsWith('.cs')).length >= 14);
});

test('the Steward installer carries the plugin data files where lib/realm.js dataSets looks for them', async () => {
  const R = (await import('../lib/realm.js')).default;
  const st = loadEditionConfig('steward');
  const res = path.join(path.sep, 'res');
  const packaged = R.dataSets(res, true);
  const dev = R.dataSets(REPO, false);
  for (let i = 0; i < packaged.length; i++) {
    const rel = path.relative(res, packaged[i].src).split(path.sep).join('/');
    const fromRel = path.relative(LAUNCHER, dev[i].src).split(path.sep).join('/');
    const entry = st.extraResources.find((r) => r.to === rel && r.from === fromRel);
    assert.ok(entry, `${packaged[i].id}: extraResources maps ${fromRel} to ${rel}`);
    const wanted = dev[i].only ? dev[i].only : ['*.json'];
    assert.deepEqual(entry.filter, wanted, `${packaged[i].id} filter`);
    const files = fs.readdirSync(dev[i].src).filter((f) => f.endsWith('.json'));
    assert.ok(files.length >= 1, `${dev[i].id} has files in the repository`);
    for (const f of dev[i].only || []) assert.ok(files.includes(f), `${dev[i].id}: ${f} is in the repository`);
  }
  // RealmArrival's site plan is packed under its repository name and written to the server as site.json.
  const arrival = packaged.find((s) => s.id === 'arrival');
  assert.equal(path.relative(res, arrival.src).split(path.sep).join('/'), 'realm-data/RealmArrival');
  assert.deepEqual(arrival.only, ['arrival.json']);
  assert.deepEqual(arrival.as, { 'arrival.json': 'site.json' });
});

test('collectRelease copies the installers and writes sha256sum-format checksums', () => {
  const dir = tmp();
  try {
    const src = path.join(dir, 'dist');
    fs.mkdirSync(src);
    fs.writeFileSync(path.join(src, 'Realm-Steward-Setup-1.0.0.exe'), 'steward');
    fs.writeFileSync(path.join(src, 'Realm-Setup-1.0.0.exe'), 'player');
    const out = path.join(dir, 'release', '1.0.0');
    fs.mkdirSync(out, { recursive: true });
    fs.writeFileSync(path.join(out, 'Realm-Setup-0.9.0.exe'), 'stale');
    const entries = collectRelease(out, [
      { src: path.join(src, 'Realm-Steward-Setup-1.0.0.exe'), name: 'Realm-Steward-Setup-1.0.0.exe' },
      { src: path.join(src, 'Realm-Setup-1.0.0.exe'), name: 'Realm-Setup-1.0.0.exe' }
    ]);
    assert.deepEqual(fs.readdirSync(out).sort(), ['Realm-Setup-1.0.0.exe', 'Realm-Steward-Setup-1.0.0.exe', 'SHA256SUMS.txt']);
    const sha = (s) => crypto.createHash('sha256').update(s).digest('hex');
    assert.equal(fs.readFileSync(path.join(out, 'SHA256SUMS.txt'), 'utf8'),
      `${sha('steward')}  Realm-Steward-Setup-1.0.0.exe\n${sha('player')}  Realm-Setup-1.0.0.exe\n`);
    assert.equal(entries[0].bytes, 7);
    try {
      execFileSync('sha256sum', ['-c', 'SHA256SUMS.txt'], { cwd: out, stdio: 'pipe' });
    } catch (e) {
      if (e.code !== 'ENOENT') throw e; // sha256sum is not on every machine
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('building one edition keeps the other installer and re-hashes both', () => {
  const dir = tmp();
  try {
    const out = path.join(dir, 'out');
    fs.mkdirSync(out);
    fs.writeFileSync(path.join(out, 'Realm-Steward-Setup-1.0.0.exe'), 'old steward');
    fs.writeFileSync(path.join(out, 'notes.txt'), 'x');
    const src = path.join(dir, 'Realm-Setup-1.0.0.exe');
    fs.writeFileSync(src, 'new player');
    const order = ['Realm-Steward-Setup-1.0.0.exe', 'Realm-Setup-1.0.0.exe'];
    const entries = collectRelease(out, [{ src, name: 'Realm-Setup-1.0.0.exe' }], { keep: ['Realm-Steward-Setup-1.0.0.exe'], order });
    assert.deepEqual(entries.map((e) => [e.name, e.kept]), [['Realm-Steward-Setup-1.0.0.exe', true], ['Realm-Setup-1.0.0.exe', false]]);
    assert.deepEqual(fs.readdirSync(out).sort(), ['Realm-Setup-1.0.0.exe', 'Realm-Steward-Setup-1.0.0.exe', 'SHA256SUMS.txt']);
    assert.equal(fs.readFileSync(path.join(out, 'SHA256SUMS.txt'), 'utf8').trim().split('\n').length, 2);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('collectRelease fails before touching anything when an installer is missing', () => {
  const dir = tmp();
  try {
    const out = path.join(dir, 'out');
    fs.mkdirSync(out);
    fs.writeFileSync(path.join(out, 'Realm-Setup-1.0.0.exe'), 'keep me');
    assert.throws(() => collectRelease(out, [{ src: path.join(dir, 'nope.exe'), name: 'Realm-Setup-1.0.0.exe' }]), /missing/);
    assert.equal(fs.readFileSync(path.join(out, 'Realm-Setup-1.0.0.exe'), 'utf8'), 'keep me');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('sha256File matches a one-shot hash on a file larger than the read buffer', () => {
  const dir = tmp();
  try {
    const f = path.join(dir, 'big.bin');
    const data = crypto.randomBytes(3 * 1024 * 1024 + 17);
    fs.writeFileSync(f, data);
    assert.equal(sha256File(f), crypto.createHash('sha256').update(data).digest('hex'));
    assert.equal(checksumsText([{ sha256: 'ab', name: 'x.exe' }]), 'ab  x.exe\n');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('the player-config warning appears only while no list is published', () => {
  const dir = tmp();
  try {
    fs.mkdirSync(path.join(dir, 'player'));
    const write = (o) => fs.writeFileSync(path.join(dir, 'player', 'player-config.json'), JSON.stringify(o));
    write({ manifestUrl: '', publicKey: '' });
    assert.match(playerConfigWarning(dir), /no published server list/);
    write({ manifestUrl: 'https://example.org/servers.json', publicKey: 'MCowBQYDK2VwAyEA' });
    assert.equal(playerConfigWarning(dir), null);
    fs.writeFileSync(path.join(dir, 'player', 'player-config.json'), '{');
    assert.match(playerConfigWarning(dir), /could not be read/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('electron-builder cache folder per platform', () => {
  assert.equal(electronBuilderCacheDir({ ELECTRON_BUILDER_CACHE: '/x' }, 'win32'), '/x');
  assert.equal(electronBuilderCacheDir({ LOCALAPPDATA: 'C:\\Users\\a\\AppData\\Local' }, 'win32', 'C:\\Users\\a'),
    path.join('C:\\Users\\a\\AppData\\Local', 'electron-builder', 'Cache'));
  assert.equal(electronBuilderCacheDir({}, 'linux', '/home/a'), path.join('/home/a', '.cache', 'electron-builder'));
  assert.equal(electronBuilderCacheDir({}, 'darwin', '/Users/a'), path.join('/Users/a', 'Library', 'Caches', 'electron-builder'));
});

test('ensureWinCodeSign: present folder is left alone; a bad download is refused and not fatal', async () => {
  const dir = tmp();
  try {
    const final = path.join(dir, 'winCodeSign', WIN_CODE_SIGN.dir);
    fs.mkdirSync(final, { recursive: true });
    fs.writeFileSync(path.join(final, 'rcedit-x64.exe'), '');
    assert.equal(await ensureWinCodeSign({ cacheDir: dir, fetchImpl: () => assert.fail('no download expected') }), 'present');
    fs.rmSync(final, { recursive: true });
    const fake = async () => ({ ok: true, status: 200, arrayBuffer: async () => new TextEncoder().encode('not the archive').buffer });
    const r = await ensureWinCodeSign({ cacheDir: dir, fetchImpl: fake, sevenZip: '/nonexistent/7za' });
    assert.match(r, /^skipped: .*SHA-512/);
    assert.ok(!fs.existsSync(final));
    assert.deepEqual(fs.readdirSync(path.join(dir, 'winCodeSign')), [], 'no temporary folder is left behind');
    const offline = async () => { throw new Error('getaddrinfo ENOTFOUND github.com'); };
    assert.match(await ensureWinCodeSign({ cacheDir: dir, fetchImpl: offline, sevenZip: '/x' }), /^skipped: .*ENOTFOUND/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ensureWinCodeSign reuses the archive a failed electron-builder unpack left behind, without downloading', { skip: process.platform === 'win32' }, async () => {
  const dir = tmp();
  const saved = WIN_CODE_SIGN.sha512;
  try {
    const base = path.join(dir, 'winCodeSign');
    fs.mkdirSync(base, { recursive: true });
    const archive = Buffer.from('pretend 7z archive');
    fs.writeFileSync(path.join(base, '123456.7z'), archive);
    fs.writeFileSync(path.join(base, '999.7z'), 'some other file');
    WIN_CODE_SIGN.sha512 = crypto.createHash('sha512').update(archive).digest('hex');
    // A stand-in for 7za that records its arguments and "unpacks" rcedit-x64.exe into the -o folder.
    const fake7z = path.join(dir, 'fake7za.sh');
    fs.writeFileSync(fake7z, '#!/bin/sh\nfor a in "$@"; do case "$a" in -o*) out="${a#-o}";; esac; done\nmkdir -p "$out" && : > "$out/rcedit-x64.exe" && echo "$@" > "' + path.join(dir, 'args') + '"\n');
    fs.chmodSync(fake7z, 0o755);
    const r = await ensureWinCodeSign({ cacheDir: dir, sevenZip: fake7z, fetchImpl: () => assert.fail('must not download') });
    assert.equal(r, 'prepared');
    assert.ok(fs.existsSync(path.join(base, WIN_CODE_SIGN.dir, 'rcedit-x64.exe')));
    assert.match(fs.readFileSync(path.join(dir, 'args'), 'utf8').trim(), /^x -bd -y -o\S+ \S+winCodeSign\.7z -xr!darwin$/);
    assert.deepEqual(fs.readdirSync(base).filter((n) => n.startsWith('.realm-')), []);
  } finally {
    WIN_CODE_SIGN.sha512 = saved;
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('explainBuildError names the fix for the failures people hit', () => {
  assert.match(explainBuildError(new Error('ERROR: Cannot create symbolic link : A required privilege is not held by the client')), /winCodeSign/);
  assert.match(explainBuildError(new Error('EBUSY: resource busy or locked, unlink')), /in use/);
  assert.match(explainBuildError(new Error('getaddrinfo ENOTFOUND github.com')), /download failed/i);
  assert.match(explainBuildError(new Error('ENOSPC: no space left on device')), /2 GB/);
  assert.match(explainBuildError(new Error('something new')), /lines above/);
});

test('.gitignore keeps release output out of git; .gitattributes keeps .bat files CRLF', () => {
  const ignore = fs.readFileSync(path.join(REPO, '.gitignore'), 'utf8').split(/\r?\n/);
  assert.ok(ignore.includes('release/'));
  const attrs = fs.readFileSync(path.join(REPO, '.gitattributes'), 'utf8');
  assert.match(attrs, /^\*\.bat\s+-text/m);
});

// ---------------------------------------------------------------- Build-Realm.bat, read statically

const BAT = fs.readFileSync(path.join(REPO, 'Build-Realm.bat'), 'latin1');
const batLines = BAT.split('\r\n');

test('Build-Realm.bat: CRLF line endings only, plain ASCII, no tabs', () => {
  assert.ok(BAT.endsWith('\r\n'));
  assert.equal(BAT.replace(/\r\n/g, '').includes('\n'), false, 'a bare LF');
  assert.equal(BAT.replace(/\r\n/g, '').includes('\r'), false, 'a bare CR');
  assert.ok(/^[\x20-\x7E\r\n]*$/.test(BAT), 'only printable ASCII');
});

test('Build-Realm.bat: runs from its own folder and never asks for administrator rights', () => {
  assert.equal(batLines[0], '@echo off');
  assert.ok(batLines.includes('cd /d "%~dp0"'));
  assert.ok(batLines.includes('setlocal EnableExtensions DisableDelayedExpansion'));
  assert.doesNotMatch(BAT, /\brunas\b|-Verb\s+RunAs|\bUAC\b/i);
});

test('Build-Realm.bat: checks Node 22 and git, then npm ci, check, test and the release build, each checked', () => {
  const order = ['where node', 'LSS 22', 'where npm', 'where git', 'call npm ci', 'call npm run check', 'call npm test', 'node scripts\\build-release.mjs'];
  let at = -1;
  for (const s of order) {
    const i = BAT.indexOf(s);
    assert.ok(i > at, `${s} comes in order`);
    at = i;
  }
  // npm is npm.cmd: without "call" the script would end after the first npm command.
  for (const l of batLines) if (/^\s*npm\s/.test(l)) assert.fail(`npm without call: ${l}`);
  // Every command that can fail is followed by an errorlevel check on the next line.
  const mustCheck = batLines.map((l, i) => [l.trim(), i]).filter(([l]) => /^(call npm (ci|run check)|node scripts\\build-release\.mjs$|where )/.test(l));
  assert.ok(mustCheck.length >= 6);
  for (const [l, i] of mustCheck) assert.match(batLines[i + 1].trim(), /^if errorlevel 1 \($/, `no errorlevel check after: ${l}`);
  // npm test keeps its result while TEMP is put back, then checks it.
  const t = batLines.findIndex((l) => l.trim() === 'call npm test');
  assert.equal(batLines[t + 1], 'set "TEST_RESULT=%ERRORLEVEL%"');
  assert.ok(batLines.slice(t + 2, t + 7).includes('if not "%TEST_RESULT%"=="0" ('));
});

test('Build-Realm.bat: refuses a C: folder and gives the tests a temporary folder off C:', () => {
  // Realm refuses server folders on C:, and the launcher tests make server folders under TEMP (on C: on almost
  // every PC; seen failing with Windows Node under wine). So the tests get TEMP next to the .bat.
  assert.match(BAT, /if \/i "%~d0"=="C:" \(\r\n  set "PROBLEM=[^"]*drive C:/);
  const t = batLines.findIndex((l) => l.trim() === 'call npm test');
  const before = batLines.slice(t - 8, t);
  assert.ok(before.includes('set "TEST_TMP=%~dp0launcher\\.test-tmp"'));
  assert.ok(before.includes('set "TEMP=%TEST_TMP%"') && before.includes('set "TMP=%TEST_TMP%"'));
  const after = batLines.slice(t, t + 6);
  assert.ok(after.includes('set "TEMP=%SAVED_TEMP%"') && after.includes('set "TMP=%SAVED_TMP%"'));
  assert.ok(fs.readFileSync(path.join(REPO, '.gitignore'), 'utf8').split(/\r?\n/).includes('launcher/.test-tmp/'));
});

test('Build-Realm.bat: every failure explains itself, pauses, and exits 1; success pauses and exits 0', () => {
  const gotoFail = batLines.filter((l) => l.trim() === 'goto :fail').length;
  assert.ok(gotoFail >= 8);
  // Each failing block sets PROBLEM and FIX before goto :fail.
  batLines.forEach((l, i) => {
    if (l.trim() !== 'goto :fail') return;
    const block = batLines.slice(Math.max(0, i - 3), i).join('\n');
    assert.match(block, /set "PROBLEM=/, `PROBLEM before line ${i + 1}`);
    assert.match(block, /set "FIX=/, `FIX before line ${i + 1}`);
  });
  const fail = BAT.slice(BAT.indexOf('\r\n:fail\r\n'));
  assert.match(fail, /echo   PROBLEM: %PROBLEM%/);
  assert.match(fail, /echo   FIX:     %FIX%/);
  assert.match(fail, /call :maybe_pause\r\nexit \/b 1/);
  assert.match(BAT, /call :maybe_pause\r\nexit \/b 0\r\n\r\n:fail/);
  assert.match(BAT, /:maybe_pause\r\nif defined NOPAUSE goto :eof\r\n[\s\S]*pause >nul/);
});

test('Build-Realm.bat: parentheses inside blocks are only in quoted set values', () => {
  // An unquoted ")" inside an if-block ends the block early: the classic batch bug.
  let depth = 0;
  for (const raw of batLines) {
    const l = raw.trim();
    if (/^rem\b/i.test(l) || l === '') continue;
    const unquoted = l.replace(/"[^"]*"/g, '""');
    if (depth > 0 && /^echo\b/i.test(l)) assert.doesNotMatch(unquoted, /[()]/, `paren in echo inside a block: ${l}`);
    for (const c of unquoted) {
      if (c === '(') depth++;
      if (c === ')') depth--;
    }
    assert.ok(depth >= 0, `unbalanced ) at: ${l}`);
  }
  assert.equal(depth, 0);
  // No redirection characters or percent signs in rem lines (cmd expands % even there).
  for (const l of batLines) if (/^\s*rem\b/i.test(l)) assert.doesNotMatch(l, /[<>|%]/, l);
});

test('Build-Realm.bat: every label it jumps to exists', () => {
  const labels = new Set(batLines.filter((l) => /^:[a-z_]+$/i.test(l)).map((l) => l.slice(1).toLowerCase()));
  for (const m of BAT.matchAll(/(?:goto|call) :([a-z_]+)/gi)) {
    if (m[1].toLowerCase() === 'eof') continue;
    assert.ok(labels.has(m[1].toLowerCase()), `missing label :${m[1]}`);
  }
});

// ---------------------------------------------------------------- first-contact docs

test('START-HERE.md is linked from the top of README.md and gives the right ports', () => {
  const readme = fs.readFileSync(path.join(REPO, 'README.md'), 'utf8');
  assert.match(readme.split('\n').slice(0, 6).join('\n'), /\(START-HERE\.md\)/);
  const start = fs.readFileSync(path.join(REPO, 'START-HERE.md'), 'utf8');
  for (const s of ['Build-Realm.bat', 'Realm-Steward-Setup-1.0.0.exe', 'Realm-Setup-1.0.0.exe', 'UDP 7350', 'TCP 7350', 'UDP 27015', '11000', 'docs/troubleshooting.md']) {
    assert.ok(start.includes(s), `START-HERE.md mentions ${s}`);
  }
  assert.match(start, /never forward[^.]*11000/i);
  for (const name of Object.keys(EDITIONS)) assert.ok(start.includes(installerName(loadEditionConfig(name), VERSION)));
});

test('docs/troubleshooting.md covers the problems hit on the real server and the Doctor\'s messages', () => {
  const t = fs.readFileSync(path.join(REPO, 'docs', 'troubleshooting.md'), 'utf8');
  for (const s of ['EACCES', 'Unable to resolve host name', 'Session.lock', 'k_EBeginAuthSessionResultGameMismatch',
    'already being used', 'Could not load world', '3 of 4 Realm rules', 'eac_usermode', 'Lobby query failed',
    'Connection timed out', 'Please run the game from Reign Of Kings.exe', 'EAC has not intiailized yet',
    'not running the same version', 'Server is full', 'ping system', 'This is the first time you have run this server',
    'The Steam game server could not initialize', 'Incorrect password', 'carrier-grade NAT', 'Cannot create symbolic link']) {
    assert.ok(t.includes(s), `troubleshooting.md covers ${s}`);
  }
});
