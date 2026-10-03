// The player edition ("Realm") must contain no admin or server-control code and expose no such IPC.
// Checked three ways: the require graph of its entry points, the API its preload exposes (and the
// channels that API can reach), and the files its electron-builder config packs.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const Module = require('module');

const ROOT = path.join(__dirname, '..');
const rel = (p) => path.relative(ROOT, p).split(path.sep).join('/');

// Static walk of require('./...') calls from a file.
function requireGraph(entry) {
  const seen = new Set();
  const builtins = new Set();
  const stack = [path.join(ROOT, entry)];
  while (stack.length) {
    const file = stack.pop();
    if (seen.has(file)) continue;
    seen.add(file);
    if (!file.endsWith('.js')) continue;
    const src = fs.readFileSync(file, 'utf8');
    for (const m of src.matchAll(/require\(\s*['"]([^'"]+)['"]\s*\)/g)) {
      const spec = m[1];
      if (spec.startsWith('.')) {
        let target = path.resolve(path.dirname(file), spec);
        if (!fs.existsSync(target) && fs.existsSync(target + '.js')) target += '.js';
        stack.push(target);
      } else builtins.add(spec);
    }
  }
  return { files: [...seen].map(rel).sort(), modules: [...builtins].sort() };
}

const FORBIDDEN_FILES = /^(main\.js|preload\.js|lib\/(realm|server-process|chronicle-host|safety|fsops|zip|settings|fleet|supervisor|firewall|publish|netcheck)\.js)$/;
const FORBIDDEN_CHANNEL = /^(server|setup|fleet|public|publish|settings|overlay|realm):/;

test('player main and preload only reach lib/shared, the news and updater modules, and the player folder', () => {
  for (const entry of ['player/main.js', 'player/preload.js']) {
    const g = requireGraph(entry);
    for (const f of g.files) {
      assert.ok(!FORBIDDEN_FILES.test(f), `${entry} reaches ${f}`);
      assert.ok(/^(player\/|lib\/shared\/|lib\/(news|updater)\.js$|package\.json$)/.test(f), `${entry} reaches ${f}, outside player/, lib/shared/ and lib/news.js, lib/updater.js`);
    }
    for (const mod of g.modules) assert.ok(!['yauzl', 'yazl'].includes(mod), `${entry} uses ${mod}`);
  }
  // child_process is used only by lib/shared/steam.js, for the read-only "reg query" install check.
  const users = requireGraph('player/main.js').files.filter((f) => f.endsWith('.js') && /require\(['"]child_process['"]\)/.test(fs.readFileSync(path.join(ROOT, f), 'utf8')));
  assert.deepEqual(users, ['lib/shared/steam.js']);
  const steamSrc = fs.readFileSync(path.join(ROOT, 'lib/shared/steam.js'), 'utf8');
  // Every child process it starts is "reg" (install check and the Doctor's Steam-running check).
  const runs = [...steamSrc.matchAll(/runFile\('([^']+)'/g)].map((m) => m[1]);
  assert.ok(runs.length >= 1);
  assert.deepEqual([...new Set(runs)], ['reg']);
});

test('player main registers only app: and player: IPC channels', () => {
  const src = fs.readFileSync(path.join(ROOT, 'player/main.js'), 'utf8');
  const channels = [...src.matchAll(/handle\('([^']+)'/g)].map((m) => m[1]);
  assert.ok(channels.length >= 10);
  for (const c of channels) assert.match(c, /^(app|player):/, c);
  assert.doesNotMatch(src, /ipcMain\.on\(/);
  assert.doesNotMatch(src, /spawn\(|execFile\(|exec\(|netsh|New-NetFirewallRule|safeStorage|ServerSettings\.cfg/);
});

test('player preload exposes only player calls, and every call stays on app:/player: channels', () => {
  const invoked = [];
  let exposed = null;
  const fakeElectron = {
    contextBridge: { exposeInMainWorld: (name, api) => (exposed = { name, api }) },
    ipcRenderer: { invoke: async (ch) => (invoked.push(ch), { ok: true, data: null }), on: () => {} }
  };
  const orig = Module._load;
  Module._load = function (req, ...rest) {
    if (req === 'electron') return fakeElectron;
    return orig.call(this, req, ...rest);
  };
  try {
    delete require.cache[require.resolve('../player/preload.js')];
    require('../player/preload.js');
  } finally {
    Module._load = orig;
  }
  assert.equal(exposed.name, 'realm');
  const keys = Object.keys(exposed.api).sort();
  assert.deepEqual(keys, ['appInfo', 'cancelUpdate', 'checkUpdate', 'copyAddress', 'doctorClassify', 'doctorReport', 'doctorRun', 'downloadUpdate', 'getConfig', 'installGame', 'installState', 'installUpdate', 'join', 'joinBest', 'news', 'onPush', 'openLink', 'openNewsLink', 'servers', 'status', 'takeLink', 'update', 'whatsNew', 'windowAction']);
  for (const k of keys) assert.equal(typeof exposed.api[k], 'function', k);
  return Promise.all(keys.filter((k) => k !== 'onPush').map((k) => exposed.api[k]('x'))).then(() => {
    assert.equal(invoked.length, keys.length - 1);
    for (const ch of invoked) {
      assert.match(ch, /^(app|player):/, ch);
      assert.doesNotMatch(ch, FORBIDDEN_CHANNEL);
    }
  });
});

test('steward preload, by contrast, carries the server calls (sanity check of the test itself)', () => {
  const src = fs.readFileSync(path.join(ROOT, 'preload.js'), 'utf8');
  assert.match(src, /server:start/);
  assert.match(src, /public:firewallAdd/);
});

test('the player build config packs only player files and lib/shared', () => {
  const cfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'build/player.json'), 'utf8'));
  assert.equal(cfg.extraMetadata.main, 'player/main.js');
  assert.equal(cfg.productName, 'Realm');
  assert.equal(cfg.publish, null);
  assert.notEqual(cfg.appId, JSON.parse(fs.readFileSync(path.join(ROOT, 'build/steward.json'), 'utf8')).appId);
  assert.equal(cfg.extraResources, undefined, 'no Chronicle or plugins in the player build');
  assert.equal(cfg.extraFiles, undefined);
  assert.deepEqual(cfg.protocols, [{ name: 'Realm link', schemes: ['realm'] }]);
  for (const pattern of cfg.files) {
    if (pattern.startsWith('!')) continue;
    assert.match(pattern, /^(player\/|lib\/shared\/|lib\/(news|updater)\.js$|renderer\/(player|coach|styles)\.|renderer\/(player|coach)\.|renderer\/doctor\.(js|css)$|renderer\/heraldry\.js$|renderer\/fonts\/|renderer\/assets\/|build\/icon\.png$)/, pattern);
  }
  assert.ok(cfg.files.includes('!node_modules/**/*'));
  // Every file the player renderer page loads is packed.
  const html = fs.readFileSync(path.join(ROOT, 'renderer/player.html'), 'utf8');
  for (const m of html.matchAll(/(?:src|href)="([a-z.]+\.(?:js|css))"/g)) assert.ok(cfg.files.includes(`renderer/${m[1]}`), m[1]);
  assert.doesNotMatch(html, /app\.js|mock-preload|frame-src http/);
});

test('the steward build keeps the owner app (and its profile) and leaves the player page out', () => {
  const cfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'build/steward.json'), 'utf8'));
  assert.equal(cfg.extraMetadata.main, 'main.js');
  assert.equal(cfg.appId, 'community.realm.launcher');
  assert.ok(cfg.files.includes('!renderer/player.*'));
  assert.equal(cfg.publish, null);
  const pkg = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8'));
  assert.match(pkg.scripts['dist:win'], /dist:steward/);
  assert.match(pkg.scripts['dist:player'], /build\/player\.json/);
  assert.match(fs.readFileSync(path.join(ROOT, 'main.js'), 'utf8'), /app\.setPath\('userData', path\.join\(app\.getPath\('appData'\), 'Realm'\)\)/);
});

test('the shipped player-config.json never carries a private key', () => {
  const txt = fs.readFileSync(path.join(ROOT, 'player/player-config.json'), 'utf8');
  assert.doesNotMatch(txt, /PRIVATE KEY|privateKey/);
  const cfg = JSON.parse(txt);
  assert.ok(cfg.publicKey === '' || Buffer.from(cfg.publicKey, 'base64').length === 32);
});

test('player fallbacks (saved copy and built-in list) refuse lists older than the newest seen', () => {
  const src = fs.readFileSync(path.join(__dirname, '..', 'player', 'main.js'), 'utf8');
  const calls = src.match(/M\.verifyEnvelope\([^)]*\)/g) || [];
  assert.ok(calls.length >= 3, 'online, cache and bundled checks');
  for (const c of calls) assert.match(c, /minSeq: prefs\.maxSeq/, c);
});

test('the player app is one screen: no rail, settings, onboarding, tray or notification poller', () => {
  const html = fs.readFileSync(path.join(ROOT, 'renderer/player.html'), 'utf8');
  assert.doesNotMatch(html, /class="rail"|data-go=|id="onboard"|view-psettings|pref-background|pref-notify/);
  assert.match(html, /id="play"/);
  assert.match(html, /id="update-bar"/);
  assert.match(html, /id="news-list"/);
  assert.match(html, /id="realm-new"/);
  const main = fs.readFileSync(path.join(ROOT, 'player/main.js'), 'utf8');
  assert.doesNotMatch(main, /\bTray\b|\bNotification\b|setPrefs|onboarded/);
  // The only file Realm ever runs is the installer that matched the signed update, re-hashed first.
  const openPath = main.match(/shell\.openPath\(/g) || [];
  assert.equal(openPath.length, 1);
  const install = main.slice(main.indexOf('async function installUpdate'), main.indexOf('shell.openPath('));
  assert.match(install, /UP\.verifyInstaller\(update\.file, update\.update\)/);
});

test('the player build packs the news and updater modules and the release notes', () => {
  const cfg = JSON.parse(fs.readFileSync(path.join(ROOT, 'build/player.json'), 'utf8'));
  for (const f of ['lib/news.js', 'lib/updater.js', 'player/release-notes.json']) assert.ok(cfg.files.includes(f), f);
  assert.ok(!cfg.files.some((f) => /publish|realm\.js|server-process|renderer\/app\.js|renderer\/index\.html/.test(f)));
  const notes = JSON.parse(fs.readFileSync(path.join(ROOT, 'player/release-notes.json'), 'utf8'));
  const version = JSON.parse(fs.readFileSync(path.join(ROOT, 'package.json'), 'utf8')).version;
  assert.ok(notes[version] && notes[version].items.length > 0, `release notes for ${version}`);
});
