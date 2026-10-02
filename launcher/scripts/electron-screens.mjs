// End-to-end walk-through of Realm Steward (the owner edition) with an imitation dedicated server,
// used on non-Windows build machines. It builds a throwaway "Steam" folder whose Server.exe/ROK.exe
// is a small Node script that imitates the game, drives every screen with Playwright's Electron
// support, checks the results on disk and saves screenshots to docs/img/steward-<screen>.png.
// Writes its signed list to <sandbox>/publish; scripts/player-screens.mjs can reuse it.
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
import { createRequire as cr } from 'node:module';
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

const A2S_LIB = path.join(launcher, 'lib', 'shared', 'a2s.js').replace(/\\/g, '/');
const FAKE_SERVER = `#!/usr/bin/env node
// Imitation of the Reign of Kings dedicated server, for automated UI tests only. Like the real one
// ([DEC] CoreServer) it writes ServerSettings.cfg and exits on its very first run; afterwards it
// opens its configured ports (UDP game, TCP ping, UDP query answering A2S) and prints the lines
// Realm looks for.
const fs = require('fs');
const path = require('path');
const net = require('net');
const dgram = require('dgram');
const A2S = require(${JSON.stringify(A2S_LIB)});
const root = process.cwd();
const say = (s) => process.stdout.write(s + '\\r\\n');
const cfgDir = path.join(root, 'Configuration');
const cfg = path.join(cfgDir, 'ServerSettings.cfg');
say('Mono path[0] = \\'' + root + '/ROK_Data/Managed\\'');
if (!fs.existsSync(cfg)) {
  setTimeout(() => {
    fs.mkdirSync(cfgDir, { recursive: true });
    fs.writeFileSync(cfg, ["# -- Server --", "isPrivate = 'False'", "serverName = 'Reign of Kings Server'", "greeting = ''", "maxPlayers = '30'", "bindIP = '0.0.0.0'", "portNumber = '7350'", "pingPort = '7350'", "password = ''", "steamAuthPort = '27015'", "restartTime = '0'", "timeBetweenPlayerJoin = '10'", ""].join('\\r\\n'));
    fs.writeFileSync(path.join(cfgDir, 'ConsoleSettings.cfg'), ["enableRCon = 'False'", "rConPassword = ''", "rConPort = '27015'", ""].join('\\r\\n'));
    say('This is the first time you have run this server.');
    setTimeout(() => process.exit(0), 300);
  }, 1200);
  return;
}
const text = fs.readFileSync(cfg, 'utf8');
const get = (k) => ((new RegExp('^\\\\s*' + k + "\\\\s*=\\\\s*'([^']*)'", 'm').exec(text) || [])[1]);
const port = Number(get('portNumber') || 7350);
const query = Number(get('steamAuthPort') || 27015);
const max = Number(get('maxPlayers') || 30);
const sockets = [];
say('Initialize engine version: 5.x (imitation for tests)');
say('Loading level CrownLand...');
setTimeout(() => {
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
  const game = dgram.createSocket('udp4');
  game.bind(port, get('bindIP') || '0.0.0.0');
  const ping = net.createServer((c) => c.end()).listen(port, '0.0.0.0');
  const q = dgram.createSocket('udp4');
  q.on('message', (msg, r) => {
    try {
      const isReq = msg.length >= 25 && msg[4] === 0x54;
      if (!isReq) return;
      if (msg.length < 29) q.send(A2S.buildChallengeReply(Buffer.from([1, 2, 3, 4])), r.port, r.address);
      else q.send(A2S.buildInfoReply({ players: 2 }), r.port, r.address);
    } catch (e) { say('[E] ' + e.message); }
  });
  q.bind(query, '0.0.0.0');
  sockets.push(game, ping, q);
  say('Server for ' + max + ' players started on port ' + port + '.');
  setTimeout(() => say('Steam game server started. (IP: 203.0.113.50, Logged: True, Secure: True)'), 300);
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
    else if (line === 'crash') { say('[E] Imitation crash for the supervisor test.'); process.exit(3); }
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
const s2Root = path.join(base, 'RealmTest', 's2', 'server');
const publishDir = path.join(base, 'publish');
const userData = path.join(base, 'profile');
await write(path.join(steamServer, 'Server.exe'), FAKE_SERVER, 0o755);
await write(path.join(steamServer, 'ROK.exe'), FAKE_SERVER, 0o755);
await write(path.join(steamServer, 'ROK_Data', 'Managed', 'Assembly-CSharp.dll'), 'vanilla assembly (test)');
await write(path.join(steamServer, 'ROK_Data', 'level0'), crypto.randomBytes(24 * 1024 * 1024));
await fsp.mkdir(path.join(steamServer, 'Logs'), { recursive: true });
await fsp.mkdir(imgDir, { recursive: true });
log('sandbox', base);
const lib = cr(import.meta.url);
const M = lib(path.join(launcher, 'lib', 'shared', 'manifest.js'));

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
// Folder pickers: answer native dialogs from the main process (no human at the keyboard).
async function nextFolderPick(dir) {
  await app.evaluate(({ dialog }, p) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [p] });
  }, dir);
}

async function shot(name) {
  await sleep(700);
  await page.evaluate(() => document.getElementById('toasts').replaceChildren());
  const file = path.join(imgDir, `steward-${name}.png`);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}

async function go(view) {
  await page.click(`.rail-btn[data-go="${view}"]`);
  await sleep(500);
}

const consoleHas = (re, timeout = 30000) => page.waitForFunction((src) => new RegExp(src).test(document.querySelector('#console').textContent), re.source, { timeout });
const pillIs = (text, timeout = 30000) => page.waitForFunction((t) => document.querySelector('#srv-pill').textContent === t, text, { timeout });

async function runWizardToEnd() {
  await page.waitForFunction(
    () => !document.querySelector('#wiz-error').hidden || document.querySelector('.done-mark'),
    null,
    { timeout: 240000 }
  );
  if (!(await page.isHidden('#wiz-error'))) {
    const msg = await page.textContent('#wiz-error-msg');
    if (!/Oxide could not be downloaded/.test(msg) || !zipArg) throw new Error('Setup failed: ' + msg);
    return false;
  }
  return true;
}

try {
  // ---- security posture of the renderer
  const sec = await page.evaluate(() => ({ require: typeof require, process: typeof process, keys: Object.keys(window.realm || {}).sort() }));
  check('renderer has no require/process', sec.require === 'undefined' && sec.process === 'undefined');
  check('bridge exposes only the named API', sec.keys.join(',') === 'appInfo,copyAddress,copyText,fleet,getConfig,getEvents,getNews,getRealm,getState,goPublic,onPush,openLink,overlay,play,publish,server,settings,setup,windowAction', sec.keys.join(','));
  const blocked = await page.evaluate(() => window.realm.openLink('file:///etc/passwd').then(() => 'opened', (e) => e.message));
  check('openLink rejects unknown ids', /not one of/.test(blocked), blocked);
  const badStep = await page.evaluate(() => window.realm.setup.run('rm -rf', 's1').then(() => 'ran', (e) => e.message));
  check('setup.run validates its argument', /not one of/.test(badStep), badStep);
  const badCmd = await page.evaluate(() => window.realm.server.command('s1', { evil: 1 }).then(() => 'ran', (e) => e.message));
  check('server.command validates its argument', /must be text/.test(badCmd), badCmd);
  const badId = await page.evaluate(() => window.realm.server.start('s9; rm').then(() => 'ran', (e) => e.message));
  check('server ids are validated', /not one of s1, s2, s3, s4/.test(badId), badId);

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
  await page.waitForFunction(() => /Starting the server once|Downloading|Installing|Deploying/.test(document.querySelector('#wiz-title').textContent), null, { timeout: 60000 });
  await sleep(600);
  await shot('setup-progress');
  let online = await runWizardToEnd();
  if (!online) {
    await fsp.mkdir(path.join(base, 'RealmTest', 'downloads'), { recursive: true });
    await fsp.copyFile(zipArg, path.join(base, 'RealmTest', 'downloads', 'Oxide.ReignOfKings-2.0.3867.zip'));
    await page.click('#wiz-go');
    await runWizardToEnd();
  }
  check('Oxide obtained', true, online ? 'downloaded in the app' : 'used --oxide-zip fallback');
  await shot('setup-done');
  check('test copy has marker', fs.existsSync(path.join(testRoot, '.realm-test-copy')));
  check('first start ended by the game itself counts as success', fs.existsSync(path.join(testRoot, 'Configuration', 'ServerSettings.cfg')));
  check('Steam copy untouched (no marker, no Oxide)', !fs.existsSync(path.join(steamServer, '.realm-test-copy')) && !fs.existsSync(path.join(steamServer, 'ROK_Data', 'Managed', 'Oxide.Core.dll')));
  check('plugins deployed', ['CrownAndConsequences.cs', 'RealmChronicle.cs', 'RealmHouses.cs'].every((f) => fs.existsSync(path.join(testRoot, 'oxide', 'plugins', f))));
  await page.click('#wiz-go');
  await page.waitForSelector('#wizard', { state: 'hidden' });

  // ---- Server 1: 120 players, daily restart, start
  await page.waitForFunction(() => !document.querySelector('#inst-max').disabled, null, { timeout: 10000 });
  await page.fill('#inst-max', '121');
  await page.click('#inst-save');
  await sleep(600);
  check('max players above 120 is refused', /from 1 to 120/.test(await page.textContent('#toasts')));
  await page.fill('#inst-name', 'Realm I - Ashveil');
  await page.fill('#inst-max', '120');
  await page.fill('#inst-pacing', '3');
  await page.check('#inst-daily');
  await page.fill('#inst-time', '06:00');
  await page.click('#inst-save');
  await sleep(900);
  let cfgText = fs.readFileSync(path.join(testRoot, 'Configuration', 'ServerSettings.cfg'), 'utf8');
  check('server name, 120 slots and join pacing written to ServerSettings.cfg', /serverName = 'Realm I - Ashveil'/.test(cfgText) && /maxPlayers = '120'/.test(cfgText) && /timeBetweenPlayerJoin = '3'/.test(cfgText));
  await page.click('#srv-start');
  await consoleHas(/players started on port/);
  await sleep(1500);
  cfgText = fs.readFileSync(path.join(testRoot, 'Configuration', 'ServerSettings.cfg'), 'utf8');
  const rt = Number((/restartTime = '(\d+)'/.exec(cfgText) || [])[1]);
  check('daily restart written as restartTime seconds (game countdown)', rt >= 600 && rt <= 86400, `restartTime=${rt}`);
  check('local-only network and instance ports applied before start', /bindIP = '127\.0\.0\.1'/.test(cfgText) && /isPrivate = 'True'/.test(cfgText) && /portNumber = '7350'/.test(cfgText) && /steamAuthPort = '27015'/.test(cfgText));
  const consoleCfg = fs.readFileSync(path.join(testRoot, 'Configuration', 'ConsoleSettings.cfg'), 'utf8');
  check('RCON off, rConPort 27016', /enableRCon = 'False'/.test(consoleCfg) && /rConPort = '27016'/.test(consoleCfg));
  await page.fill('#console-cmd', 'oxide.version');
  await page.press('#console-cmd', 'Enter');
  await consoleHas(/Version: 2\.0\.3867/, 5000);
  check('console command reaches the server stdin', true);

  // ---- crash -> automatic restart with backoff and backup
  await page.fill('#console-cmd', 'crash');
  await page.press('#console-cmd', 'Enter');
  await consoleHas(/Restart 1 in 10 s, after a backup/, 15000);
  check('crash detected, restart 1 scheduled in 10 s', true);
  await sleep(1200);
  await shot('server-crash');
  await consoleHas(/Crash restart 1: server started again/, 40000);
  check('server restarted by itself after the crash', true);
  check('backup made before the crash restart', fs.readdirSync(path.join(base, 'RealmTest', 'backups')).some((n) => /-auto\.zip$/.test(n)));
  await consoleHas(/Steam game server started/, 20000);
  await sleep(800);
  await page.evaluate(() => document.querySelector('.server-side').scrollTo(0, 0));
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
  await go('overlay');
  await sleep(3500);
  await shot('overlay');
  check('overlay preview frame loaded from 127.0.0.1:8787', page.frames().some((f) => /127\.0\.0\.1:8787\/overlay/.test(f.url())));

  // ---- Server 2: add, set up, run alongside Server 1
  await go('server');
  await page.click('#srv-stop');
  await pillIs('Stopped');
  await nextFolderPick(s2Root);
  await page.click('#fleet-add');
  await page.waitForSelector('#modal:not([hidden])');
  await page.click('#modal-ok');
  await page.waitForSelector('#wizard:not([hidden])', { timeout: 15000 });
  check('second server added with its own ports', /Let.s raise Server 2/.test(await page.textContent('#wiz-title')));
  await page.click('#wiz-go');
  await runWizardToEnd();
  check('server 2 copied, first-started and given Oxide + plugins', fs.existsSync(path.join(s2Root, '.realm-test-copy')) && fs.existsSync(path.join(s2Root, 'ROK_Data', 'Managed', 'Oxide.Core.dll')) && fs.existsSync(path.join(s2Root, 'oxide', 'plugins', 'RealmHouses.cs')));
  await page.click('#wiz-go');
  await page.waitForSelector('#wizard', { state: 'hidden' });
  await page.waitForFunction(() => document.querySelector('.fleet-card.active') && /II/.test(document.querySelector('.fleet-card.active .num').textContent), null, { timeout: 10000 });
  await page.waitForFunction(() => !document.querySelector('#inst-name').disabled, null, { timeout: 10000 });
  await page.fill('#inst-name', 'Realm II - Corvane');
  await page.click('#inst-save');
  await sleep(800);
  await page.click('#srv-start');
  await consoleHas(/players started on port/);
  const s2cfg = fs.readFileSync(path.join(s2Root, 'Configuration', 'ServerSettings.cfg'), 'utf8');
  check('server 2 runs on 7360 / 27025 / RCON port 27026', /portNumber = '7360'/.test(s2cfg) && /steamAuthPort = '27025'/.test(s2cfg) && /rConPort = '27026'/.test(fs.readFileSync(path.join(s2Root, 'Configuration', 'ConsoleSettings.cfg'), 'utf8')));
  await page.click('.fleet-card[data-inst="s1"]');
  await sleep(600);
  await page.click('#srv-start');
  await page.waitForFunction(() => /2 of 2 servers running/.test(document.querySelector('#title-status-text').textContent), null, { timeout: 30000 });
  check('two servers run side by side (imitation)', true);
  await sleep(1500);
  await page.evaluate(() => document.querySelector('.server-side').scrollTo(0, 0));
  await shot('fleet');
  await page.fill('#inst-port', '7360');
  await page.click('#inst-save');
  await sleep(600);
  check('a port already used by another server is refused', /already used by s1/.test(await page.textContent('#toasts')));

  // ---- Go Public
  await go('public');
  await page.waitForSelector('#pub-rules tr', { timeout: 10000 });
  await page.check('#pub-net-public');
  await page.check('#pub-listed');
  await page.click('#pub-net-save');
  await sleep(700);
  await page.click('#pub-test');
  await page.waitForFunction(() => document.querySelectorAll('#pub-checks li:not(.empty)').length > 3, null, { timeout: 30000 });
  const checksText = await page.textContent('#pub-checks');
  check('self-test sees the A2S answer on this PC', /Steam query on this PC.*Answered/.test(checksText), checksText.slice(0, 300));
  check('self-test reads the public IP from the Steam line', /203\.0\.113\.50/.test(checksText));
  const rulesText = await page.textContent('#pub-rules');
  check('firewall rules listed, scoped to the ports and blocking 11000-11003', /Realm s1 game \(UDP\)/.test(rulesText) && /11000-11003/.test(rulesText) && /Block/.test(rulesText));
  check('firewall buttons disabled off Windows', await page.isDisabled('#pub-fw-add'));
  await page.evaluate(() => document.querySelector('.public-grid').scrollTo(0, 0));
  await shot('public');

  // ---- Publish server list
  await go('publish');
  await page.waitForSelector('#key-create:not([hidden])', { timeout: 10000 });
  await page.click('#key-create');
  await page.waitForSelector('#key-box:not([hidden])', { timeout: 10000 });
  const pubKey = (await page.textContent('#key-public')).trim();
  check('signing key created; only the public key is shown', /^[A-Za-z0-9+/]{43}=$/.test(pubKey), pubKey);
  const keyFile = JSON.parse(fs.readFileSync(path.join(userData, 'realm-signing-key.json'), 'utf8'));
  check('private key stored in the steward profile only', keyFile.publicKey === pubKey && !JSON.stringify(keyFile).includes(pubKey + 'x'));
  await page.fill('#pl-url', 'https://example.github.io/realm/servers.json');
  await page.fill('#pl-out', publishDir);
  const rows = await page.$$('#pl-rows .pl-row');
  check('one list row per server', rows.length === 2, String(rows.length));
  await page.fill('#pl-rows .pl-row[data-id="s1"] input[data-k="address"]', 'play.example.org');
  await page.fill('#pl-rows .pl-row[data-id="s1"] input[data-k="region"]', 'EU');
  await page.fill('#pl-rows .pl-row[data-id="s2"] input[data-k="address"]', 'play.example.org');
  await page.fill('#pl-rows .pl-row[data-id="s2"] input[data-k="region"]', 'EU');
  await page.fill('#pl-rows .pl-row[data-id="s2"] input[data-k="name"]', 'Realm II - Corvane');
  await page.click('#pl-write');
  await page.waitForSelector('#pl-result:not([hidden])', { timeout: 10000 });
  const envText = fs.readFileSync(path.join(publishDir, 'servers.json'), 'utf8');
  const v = M.verifyEnvelope(envText, pubKey);
  check('servers.json verifies with the public key', v.ok, v.reason || '');
  check('servers.json lists both servers with ports and 120 slots', v.ok && v.manifest.servers.map((x) => `${x.id}:${x.port}:${x.queryPort}:${x.maxPlayers}`).join(',') === 's1:7350:27015:120,s2:7360:27025:30', v.ok ? JSON.stringify(v.manifest.servers) : '');
  const pc = JSON.parse(fs.readFileSync(path.join(publishDir, 'player-config.json'), 'utf8'));
  check('player-config.json carries the public key and the signed list, no private key', pc.publicKey === pubKey && pc.bundledManifest && !/PRIVATE/.test(JSON.stringify(pc)));
  check('repo player config left alone (embed unticked)', !JSON.parse(fs.readFileSync(path.join(launcher, 'player', 'player-config.json'), 'utf8')).publicKey);
  await page.evaluate(() => document.querySelector('.publish-grid').scrollTo(0, 0));
  await shot('publish');

  // ---- settings, stop everything, restore, undo oxide
  await go('settings');
  await sleep(500);
  await shot('settings');
  await go('server');
  await page.click('#srv-stop');
  await pillIs('Stopped');
  await page.click('.fleet-card[data-inst="s2"]');
  await sleep(600);
  await page.click('#srv-stop');
  await pillIs('Stopped');
  await page.click('.fleet-card[data-inst="s1"]');
  await sleep(600);
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
  check('undo Oxide restored vanilla Assembly-CSharp.dll', fs.readFileSync(path.join(testRoot, 'ROK_Data', 'Managed', 'Assembly-CSharp.dll'), 'utf8') === 'vanilla assembly (test)');

  const realErrors = errors.filter((e) => !/fonts\.(googleapis|gstatic)\.com|ERR_NAME_NOT_RESOLVED|ERR_INTERNET_DISCONNECTED|ERR_TUNNEL|ERR_PROXY|ERR_CERT_AUTHORITY_INVALID/.test(e));
  check('no renderer errors', realErrors.length === 0, realErrors.join(' | '));
} finally {
  await app.close().catch(() => {});
}

const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
log('publish output (for scripts/player-screens.mjs):', publishDir);
if (failed.length) process.exitCode = 1;
