// End-to-end walk-through of the Electron client with a mocked dedicated server, used on
// non-Windows build machines. It builds a throwaway "Steam" folder whose Server.exe is a small
// Node script that imitates the console, drives every screen with Playwright's Electron support,
// checks the results on disk and saves screenshots to docs/img/client-<screen>.png.
//
//   xvfb-run -a node scripts/electron-screens.mjs [--oxide-zip <path>]
//
// Needs the playwright package (local or global). Real Windows / Server.exe behaviour is NOT
// exercised by this script.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const repo = path.resolve(launcher, '..');
const imgDir = path.join(repo, 'docs', 'img');
const args = process.argv.slice(2);
const zipArg = args.includes('--oxide-zip') ? args[args.indexOf('--oxide-zip') + 1] : null;

function loadPlaywright() {
  const require = createRequire(import.meta.url);
  try {
    return require('playwright');
  } catch {
    return require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
  }
}
const { _electron: electron } = loadPlaywright();

const FAKE_SERVER = `#!/usr/bin/env node
// Imitation of the Reign of Kings dedicated server console, for automated UI tests only.
const fs = require('fs');
const path = require('path');
const root = process.cwd();
const say = (s) => process.stdout.write(s + '\\r\\n');
const cfgDir = path.join(root, 'Configuration');
const cfg = path.join(cfgDir, 'ServerSettings.cfg');
say('Mono path[0] = \\'' + root + '/ROK_Data/Managed\\'');
say('Loading level CrownLand...');
setTimeout(() => {
  if (!fs.existsSync(cfg)) {
    fs.mkdirSync(cfgDir, { recursive: true });
    fs.writeFileSync(cfg, ["# -- Server --", "isPrivate = 'False'", "serverName = 'Reign of Kings Server'", "greeting = ''", "maxPlayers = '30'", "bindIP = '0.0.0.0'", "portNumber = '7350'", "password = ''", "steamAuthPort = '27015'", ""].join('\\r\\n'));
    fs.writeFileSync(path.join(cfgDir, 'ConsoleSettings.cfg'), ["enableRCon = 'False'", "rConPassword = ''", "rConPort = '27015'", ""].join('\\r\\n'));
    say('Created default configuration files.');
  }
  const managed = fs.readdirSync(root).filter((d) => /_Data$/.test(d)).map((d) => path.join(root, d, 'Managed'));
  const oxide = managed.some((m) => fs.existsSync(path.join(m, 'Oxide.Core.dll')));
  if (oxide) {
    say('[Oxide] 12:00 [Info] Loading Oxide Core v2.0.3867...');
    const data = path.join(root, 'oxide', 'data');
    fs.mkdirSync(path.join(root, 'oxide', 'plugins'), { recursive: true });
    fs.mkdirSync(data, { recursive: true });
    for (const f of fs.readdirSync(path.join(root, 'oxide', 'plugins'))) say('[Oxide] 12:00 [Info] Loaded plugin ' + f.replace(/\\.cs$/, '') + ' v0.1.0 by Realm');
    const sample = process.env.FAKE_SAMPLE_DATA;
    if (sample) {
      const st = JSON.parse(fs.readFileSync(path.join(sample, 'RealmState.json'), 'utf8'));
      st.updated = new Date().toISOString();
      fs.writeFileSync(path.join(data, 'RealmState.json'), JSON.stringify(st, null, 2));
      const ev = JSON.parse(fs.readFileSync(path.join(sample, 'RealmChronicle.json'), 'utf8'));
      const now = Date.now();
      ev.forEach((e, i) => { e.ts = new Date(now - (ev.length - i) * 37 * 60000).toISOString(); });
      fs.writeFileSync(path.join(data, 'RealmChronicle.json'), JSON.stringify(ev, null, 2));
      fs.writeFileSync(path.join(data, 'CrownAndConsequences.json'), JSON.stringify({
        KingId: 76561190000000001, KingName: st.king, CouncilNames: { 'Voice of the Crown': 'Wren', 'Keeper of Coin': 'Petra Halloran', 'Marshal': 'Odo the Tall' },
        Council: { 'Voice of the Crown': 76561190000000002 },
        Claims: [{ House: 'Corvane', DeclaredBy: 'Ysolde Corvane', Status: 'pending', WindowStart: new Date(now + 26 * 3600e3).toISOString(), WindowEnd: new Date(now + 28 * 3600e3).toISOString() }],
        ActiveDecrees: [{ Id: 'kings_peace', ExpiresAt: new Date(now + 5 * 3600e3).toISOString() }]
      }, null, 2));
    }
  }
  say('Initialize engine version: 5.x (imitation for tests)');
  setTimeout(() => say('Authentication verified for Wren (76561190000000002).'), 600);
  setTimeout(() => say('Authentication verified for Odo the Tall (76561190000000003).'), 900);
}, 1800);
let buf = '';
process.stdin.on('data', (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (line === 'quit') { say('Saving world...'); setTimeout(() => { say('Server shut down.'); process.exit(0); }, 900); }
    else if (line === 'oxide.version') say('Oxide.ReignOfKings Version: 2.0.3867');
    else if (line) say('Unknown command: ' + line);
  }
});
setInterval(() => {}, 1000);
`;

async function write(file, data, mode) {
  await fsp.mkdir(path.dirname(file), { recursive: true });
  await fsp.writeFile(file, data);
  if (mode) await fsp.chmod(file, mode);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[screens]', ...a);
const checks = [];
function check(name, ok, info = '') {
  checks.push({ name, ok: !!ok, info });
  log(`${ok ? 'PASS' : 'FAIL'} ${name}${info ? ' - ' + info : ''}`);
}

const base = await fsp.mkdtemp(path.join(os.tmpdir(), 'realm-e2e-'));
const steamServer = path.join(base, 'SteamLibrary', 'steamapps', 'common', 'Reign Of Kings Dedicated Server');
const testRoot = path.join(base, 'RealmTest', 'server');
const userData = path.join(base, 'profile');
await write(path.join(steamServer, 'Server.exe'), FAKE_SERVER, 0o755);
await write(path.join(steamServer, 'ROK.exe'), FAKE_SERVER, 0o755);
await write(path.join(steamServer, 'ROK_Data', 'Managed', 'Assembly-CSharp.dll'), 'vanilla assembly (test)');
await write(path.join(steamServer, 'ROK_Data', 'level0'), crypto.randomBytes(48 * 1024 * 1024));
await fsp.mkdir(path.join(steamServer, 'Logs'), { recursive: true });
await fsp.mkdir(imgDir, { recursive: true });
log('sandbox', base);

const app = await electron.launch({
  executablePath: path.join(launcher, 'node_modules', 'electron', 'dist', 'electron'),
  args: ['--no-sandbox', launcher],
  env: {
    ...process.env,
    REALM_USER_DATA: userData,
    REALM_DEV_STEAM_SERVER: steamServer,
    FAKE_SAMPLE_DATA: path.join(repo, 'chronicle', 'sample-data')
  }
});
const page = await app.firstWindow();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
await page.setViewportSize({ width: 1320, height: 840 }).catch(() => {});
await page.waitForLoadState('domcontentloaded');
await page.evaluate(() => document.fonts.ready);

async function shot(name) {
  await sleep(700);
  await page.evaluate(() => document.getElementById('toasts').replaceChildren());
  const file = path.join(imgDir, `client-${name}.png`);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}

async function go(view) {
  await page.click(`.rail-btn[data-go="${view}"]`);
  await sleep(500);
}

try {
  // ---- security posture of the renderer
  const sec = await page.evaluate(() => ({ require: typeof require, process: typeof process, keys: Object.keys(window.realm || {}).sort() }));
  check('renderer has no require/process', sec.require === 'undefined' && sec.process === 'undefined');
  check('bridge exposes only the named API', sec.keys.join(',') === 'appInfo,copyAddress,copyText,getConfig,getEvents,getNews,getRealm,getState,onPush,openLink,overlay,play,server,settings,setup,windowAction', sec.keys.join(','));
  const blocked = await page.evaluate(() => window.realm.openLink('file:///etc/passwd').then(() => 'opened', (e) => e.message));
  check('openLink rejects unknown ids', /not one of/.test(blocked), blocked);
  const badStep = await page.evaluate(() => window.realm.setup.run('rm -rf').then(() => 'ran', (e) => e.message));
  check('setup.run validates its argument', /not one of/.test(badStep), badStep);
  const badCmd = await page.evaluate(() => window.realm.server.command({ evil: 1 }).then(() => 'ran', (e) => e.message));
  check('server.command validates its argument', /must be text/.test(badCmd), badCmd);

  // ---- wizard appears on first run
  await page.waitForSelector('#wizard:not([hidden])', { timeout: 15000 });
  check('setup wizard opens on first run', true);
  await page.fill('#wiz-root', 'relative-folder');
  await page.click('#wiz-go');
  await sleep(400);
  const refusal = await page.textContent('#wiz-root-msg');
  check('wizard refuses a bad test folder', /full folder path/i.test(refusal), refusal);
  await page.fill('#wiz-root', path.join(base, 'SteamLibrary', 'RealmTest'));
  await page.click('#wiz-go');
  await sleep(400);
  check('wizard refuses a folder inside a Steam library', /Steam/.test(await page.textContent('#wiz-root-msg')));
  await page.fill('#wiz-root', testRoot);
  await shot('setup');

  await page.click('#wiz-go');
  // copy -> first start (fake server writes its cfg)
  await page.waitForFunction(() => /Starting the server once/.test(document.querySelector('#wiz-title').textContent), null, { timeout: 60000 });
  await sleep(900);
  await shot('setup-progress');
  // download: try the real network first; fall back to a supplied zip
  await page.waitForFunction(
    () => !document.querySelector('#wiz-error').hidden || document.querySelector('.done-mark') || /Installing|Deploying/.test(document.querySelector('#wiz-title').textContent),
    null,
    { timeout: 180000 }
  );
  let downloadedOnline = true;
  if (!(await page.isHidden('#wiz-error'))) {
    downloadedOnline = false;
    const msg = await page.textContent('#wiz-error-msg');
    log('download step failed here:', msg);
    await shot('setup-error');
    if (!zipArg) throw new Error('Download failed and no --oxide-zip was given: ' + msg);
    await fsp.mkdir(path.join(base, 'RealmTest', 'downloads'), { recursive: true });
    await fsp.copyFile(zipArg, path.join(base, 'RealmTest', 'downloads', 'Oxide.ReignOfKings-2.0.3867.zip'));
    await page.click('#wiz-go'); // Retry
  }
  check('Oxide downloaded inside the app', downloadedOnline, downloadedOnline ? 'net.fetch via system proxy' : 'used --oxide-zip fallback');
  await page.waitForSelector('.done-mark', { timeout: 180000 });
  await shot('setup-done');

  check('test copy has marker', fs.existsSync(path.join(testRoot, '.realm-test-copy')));
  check('Steam copy untouched (no marker, no Oxide)', !fs.existsSync(path.join(steamServer, '.realm-test-copy')) && !fs.existsSync(path.join(steamServer, 'ROK_Data', 'Managed', 'Oxide.Core.dll')));
  check('Oxide installed into test copy', fs.existsSync(path.join(testRoot, 'ROK_Data', 'Managed', 'Oxide.Core.dll')));
  const backups = fs.readdirSync(path.join(testRoot, '_realm-backups')).filter((n) => n.startsWith('oxide-'));
  const manifest = JSON.parse(fs.readFileSync(path.join(testRoot, '_realm-backups', backups[0], 'install.json'), 'utf8'));
  check('install.json lists overwritten Assembly-CSharp.dll', manifest.overwritten.includes('ROK_Data\\Managed\\Assembly-CSharp.dll'), JSON.stringify(manifest.overwritten));
  check('plugins deployed', ['CrownAndConsequences.cs', 'RealmChronicle.cs', 'RealmHouses.cs'].every((f) => fs.existsSync(path.join(testRoot, 'oxide', 'plugins', f))));
  check('no staging or .part leftovers', !fs.readdirSync(path.join(base, 'RealmTest')).some((n) => n.startsWith('.realm-copy')) && !fs.readdirSync(path.join(base, 'RealmTest', 'downloads')).some((n) => n.endsWith('.part')));

  await page.click('#wiz-go'); // Enter the Realm -> server screen
  await page.waitForSelector('#wizard', { state: 'hidden' });

  // ---- server
  await page.click('#srv-start');
  await page.waitForFunction(() => /Initialize engine version/.test(document.querySelector('#console').textContent), null, { timeout: 30000 });
  await sleep(1500);
  const cfgText = fs.readFileSync(path.join(testRoot, 'Configuration', 'ServerSettings.cfg'), 'utf8');
  check('local-only cfg applied before start', /bindIP = '127\.0\.0\.1'/.test(cfgText) && /isPrivate = 'True'/.test(cfgText));
  const consoleCfg = fs.readFileSync(path.join(testRoot, 'Configuration', 'ConsoleSettings.cfg'), 'utf8');
  check('RCON off, rConPort 27016', /enableRCon = 'False'/.test(consoleCfg) && /rConPort = '27016'/.test(consoleCfg));
  await page.fill('#console-cmd', 'oxide.version');
  await page.press('#console-cmd', 'Enter');
  await page.waitForFunction(() => /Version: 2\.0\.3867/.test(document.querySelector('#console').textContent), null, { timeout: 5000 });
  check('console command reaches the server stdin', true);
  await page.click('#act-plugins');
  await sleep(800);
  await shot('server');

  // ---- home / realm / overlay with chronicle data written by the fake plugins
  await go('home');
  await page.waitForFunction(() => document.querySelectorAll('#events .event').length > 3, null, { timeout: 20000 });
  await sleep(800);
  await shot('home');
  check('home shows the king from the Chronicle', /Aldric Varrow/.test(await page.textContent('#king')));

  await go('realm');
  await page.waitForSelector('#realm-tree .house', { timeout: 10000 });
  await shot('realm');
  check('realm shows council from plugin data', /Keeper of Coin/.test(await page.textContent('#realm-council')));

  await go('overlay');
  await sleep(3500);
  await shot('overlay');
  const frameOk = await page.frames().some((f) => /127\.0\.0\.1:8787\/overlay/.test(f.url()));
  check('overlay preview frame loaded from 127.0.0.1:8787', frameOk);

  // ---- backup while running (confirm), stop, restore, settings, undo oxide
  await go('server');
  await page.click('#act-backup');
  await page.waitForSelector('#modal:not([hidden])');
  await page.click('#modal-ok');
  await page.waitForFunction(() => /Backup written/.test(document.querySelector('#toasts').textContent), null, { timeout: 30000 });
  check('backup written', fs.readdirSync(path.join(base, 'RealmTest', 'backups')).some((n) => /^realm-saves-\d{8}-\d{6}\.zip$/.test(n)));
  await page.click('#srv-stop');
  await page.waitForFunction(() => document.querySelector('#srv-pill').textContent === 'Stopped', null, { timeout: 30000 });
  check('stop sends quit and the server exits', true);

  await go('settings');
  await page.waitForFunction(() => !document.querySelector('#set-name').disabled, null, { timeout: 5000 });
  await page.fill('#set-name', 'Realm - Evening Trials');
  await page.fill('#set-max', '24');
  await page.fill('#set-discord', 'https://discord.gg/realm-example');
  await page.click('#set-save');
  await sleep(1200);
  const cfgAfter = fs.readFileSync(path.join(testRoot, 'Configuration', 'ServerSettings.cfg'), 'utf8');
  check('settings rewrote serverName and maxPlayers in ServerSettings.cfg', /serverName = 'Realm - Evening Trials'/.test(cfgAfter) && /maxPlayers = '24'/.test(cfgAfter));
  check('cfg backups kept', fs.readdirSync(path.join(testRoot, '_realm-backups', 'config')).length >= 2);
  await page.fill('#set-root', 'C:\\Games\\server');
  await page.click('#set-save');
  await sleep(600);
  check('settings refuse a test folder on C:/relative', /full folder path|C:/.test(await page.textContent('#toasts')));
  await page.fill('#set-root', testRoot);
  await sleep(300);
  await page.evaluate(() => document.querySelector('.settings-grid').scrollTo(0, 0));
  await shot('settings');

  await go('server');
  await page.click('#act-restore');
  await page.waitForSelector('#modal:not([hidden])');
  await sleep(300);
  await shot('server-restore');
  await page.click('#modal-ok');
  await page.waitForFunction(() => /Restored \d+ files/.test(document.querySelector('#toasts').textContent), null, { timeout: 30000 });
  check('restore moved the current world aside', fs.readdirSync(path.join(testRoot, '_realm-backups')).some((n) => n.startsWith('pre-restore-')));

  await page.click('#act-undo-oxide');
  await page.waitForSelector('#modal:not([hidden])');
  await page.click('#modal-ok');
  await page.waitForFunction(() => /Oxide removed/.test(document.querySelector('#toasts').textContent), null, { timeout: 30000 });
  check('undo Oxide restored vanilla Assembly-CSharp.dll', fs.readFileSync(path.join(testRoot, 'ROK_Data', 'Managed', 'Assembly-CSharp.dll'), 'utf8') === 'vanilla assembly (test)' && !fs.existsSync(path.join(testRoot, 'ROK_Data', 'Managed', 'Oxide.Core.dll')));

  const realErrors = errors.filter((e) => !/fonts\.(googleapis|gstatic)\.com|ERR_NAME_NOT_RESOLVED|ERR_INTERNET_DISCONNECTED|ERR_TUNNEL|ERR_PROXY|ERR_CERT_AUTHORITY_INVALID/.test(e));
  check('no renderer errors', realErrors.length === 0, realErrors.join(' | '));
} finally {
  await app.close().catch(() => {});
}

const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) process.exitCode = 1;
