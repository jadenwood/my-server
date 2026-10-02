// Screenshots and a smoke test of the Connection Doctor in both editions, under xvfb with
// Playwright's Electron support. Builds a throwaway sandbox: an imitation test copy whose ROK.exe
// is a small Node script (it writes the game's Logger-format log, opens the game/ping/query ports
// and answers A2S), and an imitation game install with a client log that shows the owner's real
// failure ("Unable to resolve host name. (127.0.0.1:7350)"). Saves docs/img/doctor-*.png.
//
//   xvfb-run -a node scripts/doctor-screens.mjs
//
// Real Windows behaviour (PowerShell socket listing, firewall rules, the registry) is NOT exercised.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import dgram from 'node:dgram';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const repo = path.resolve(launcher, '..');
const imgDir = path.join(repo, 'docs', 'img');
const require = createRequire(import.meta.url);
const PB = require(path.join(launcher, 'lib', 'publish.js'));
const A2S = require(path.join(launcher, 'lib', 'shared', 'a2s.js'));

function loadPlaywright() {
  try {
    return require('playwright');
  } catch {
    return require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
  }
}
const { _electron: electron } = loadPlaywright();
const electronBin = path.join(launcher, 'node_modules', 'electron', 'dist', 'electron');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[doctor-screens]', ...a);
const checks = [];
function check(name, ok, info = '') {
  checks.push({ name, ok: !!ok });
  log(`${ok ? 'PASS' : 'FAIL'} ${name}${info ? ' - ' + info : ''}`);
}
async function write(file, data, mode) {
  await fsp.mkdir(path.dirname(file), { recursive: true });
  await fsp.writeFile(file, data);
  if (mode) await fsp.chmod(file, mode);
}

// Logger.FileFormat line: "[yyMMdd-HHmmss] [Level]  message" ([DEC] Logger).
const stamp = (d = new Date()) => {
  const p = (n) => String(n).padStart(2, '0');
  return `${p(d.getFullYear() % 100)}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
};

const A2S_LIB = path.join(launcher, 'lib', 'shared', 'a2s.js').replace(/\\/g, '/');
const FAKE_ROK = `#!/usr/bin/env node
// Imitation ROK.exe for UI tests: like the real -batchmode server it prints nothing to stdout,
// writes Unity lines to the -logFile path and the game's own lines to Logs/Log[...].txt.
const fs = require('fs');
const path = require('path');
const net = require('net');
const dgram = require('dgram');
const A2S = require(${JSON.stringify(A2S_LIB)});
const root = process.cwd();
const argv = process.argv.slice(2);
const unity = argv.includes('-logFile') ? argv[argv.indexOf('-logFile') + 1] : path.join(root, 'Logs', 'output.log');
const p = (n) => String(n).padStart(2, '0');
const d = new Date();
const name = 'Log[' + p(d.getFullYear() % 100) + p(d.getMonth() + 1) + p(d.getDate()) + '-' + p(d.getHours() % 12 || 12) + p(d.getMinutes()) + p(d.getSeconds()) + '].txt';
fs.mkdirSync(path.join(root, 'Logs'), { recursive: true });
const gameLog = path.join(root, 'Logs', name);
const st = () => { const n = new Date(); return p(n.getFullYear() % 100) + p(n.getMonth() + 1) + p(n.getDate()) + '-' + p(n.getHours()) + p(n.getMinutes()) + p(n.getSeconds()); };
const L = (lv, msg) => fs.appendFileSync(gameLog, '[' + st() + '] [' + lv + ']' + ' '.repeat(Math.max(1, 8 - lv.length - 2)) + msg + '\\n');
fs.appendFileSync(unity, 'Initialize engine version: 5.1.2f1 (imitation for tests)\\nGfxDevice: creating device client; threaded=0\\n');
const cfg = fs.readFileSync(path.join(root, 'Configuration', 'ServerSettings.cfg'), 'utf8');
const get = (k) => ((new RegExp('^\\\\s*' + k + "\\\\s*=\\\\s*'([^']*)'", 'm').exec(cfg) || [])[1]);
const port = Number(get('portNumber') || 7350);
const query = Number(get('steamAuthPort') || 27015);
const max = Number(get('maxPlayers') || 30);
L('Info', 'Starting EAC Integration with network ID 2.');
L('Info', 'EAC server initialized.');
setTimeout(() => {
  const game = dgram.createSocket('udp4'); game.bind(port, '127.0.0.1');
  const ping = net.createServer(() => {}); ping.listen(port, '0.0.0.0');
  const q = dgram.createSocket('udp4');
  q.on('message', (msg, r) => { if (msg[4] === 0x54) q.send(A2S.buildInfoReply({ players: 1 }), r.port, r.address); });
  q.bind(query, '0.0.0.0');
  L('Info', 'Server for ' + max + ' players started on port ' + port + '.');
  L('Info', 'Steam game server started. (IP: 203.0.113.50, Logged: True, Secure: True)');
}, 900);
setTimeout(() => L('Error', 'Player Wren denied connection because the password provided was invalid.'), 1600);
setTimeout(() => L('Info', 'Authentication verified for Odo the Tall (76561190000000003).'), 2000);
setTimeout(() => L('Info', 'Wren (76561190000000002) was kicked from the server because You have been disconnected for failing to initialize with the ping system..'), 2400);
process.stdin.on('data', (b) => { if (/quit/.test(String(b))) { L('Info', 'Saving game...'); setTimeout(() => process.exit(0), 300); } });
setInterval(() => {}, 1000);
`;

const base = await fsp.mkdtemp(path.join(os.tmpdir(), 'realm-doctor-'));
const testRoot = path.join(base, 'RealmTest', 'server');
const gameDir = path.join(base, 'Games', 'Reign Of Kings');
const userData = path.join(base, 'profile');
const playerData = path.join(base, 'player-profile');
await write(path.join(testRoot, 'ROK.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, 'Server.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, '.realm-test-copy'), 'test copy (doctor screenshots)\n');
await write(
  path.join(testRoot, 'Configuration', 'ServerSettings.cfg'),
  ["isPrivate = 'True'", "serverName = 'Realm I'", "maxPlayers = '120'", "bindIP = '127.0.0.1'", "portNumber = '7350'", "pingPort = '7350'", "password = 'oathkeeper'", "steamAuthPort = '27015'", "restartTime = '0'", "enablePingLimit = 'True'", ''].join('\r\n')
);
await write(path.join(testRoot, 'Configuration', 'ConsoleSettings.cfg'), ["enableRCon = 'False'", "rConPassword = ''", "rConPort = '27016'", ''].join('\r\n'));
await write(path.join(userData, 'realm-settings.json'), JSON.stringify({ version: 1, testRoot, serverExe: 'ROK', setupComplete: true, instances: [] }, null, 2));

// The imitation game install: the owner's real failure, then a later good attempt is NOT there yet.
const t0 = new Date(Date.now() - 6 * 60000);
const at = (s) => stamp(new Date(t0.getTime() + s * 1000));
await write(
  path.join(gameDir, 'Logs', `Log[${stamp(t0)}].txt`),
  [
    `[${at(0)}] [Info]   SteamManager created.`,
    `[${at(1)}] [Info]   Starting EAC Integration with network ID 1.`,
    `[${at(3)}] [Info]   EAC client initialized.`,
    `[${at(40)}] [Info]   Joining 127.0.0.1:7350 : 7350`,
    `[${at(40)}] [Error]  Unable to resolve host name. (127.0.0.1:7350)`,
    'Context: Game (8812)',
    '  at CodeHatch.Engine.Core.Gaming.Game.End (System.String reason)',
    `[${at(40)}] [Info]   Ending game...`,
    ''
  ].join('\n')
);
await write(path.join(gameDir, 'ROK_Data', 'output_log.txt'), 'Initialize engine version: 5.1.2f1\nDirect3D:\n    Version:  Direct3D 11.0 [level 11.0]\n');

const errors = [];
async function launch(args, env) {
  const app = await electron.launch({ executablePath: electronBin, args: ['--no-sandbox', ...args], env: { ...process.env, ...env } });
  const page = await app.firstWindow();
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => document.fonts.ready);
  return { app, page };
}
async function shot(page, name) {
  await sleep(600);
  await page.evaluate(() => {
    const t = document.getElementById('toasts');
    if (t) t.replaceChildren();
  });
  const file = path.join(imgDir, `doctor-${name}.png`);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}

await fsp.mkdir(imgDir, { recursive: true });

// ------------------------------------------------------------- Realm Steward
{
  const { app, page } = await launch([launcher], { REALM_USER_DATA: userData, REALM_DEV_STEAM_SERVER: path.join(base, 'NoSteamHere'), REALM_DEV_GAME_DIR: gameDir });
  await page.setViewportSize({ width: 1320, height: 840 }).catch(() => {});
  await page.waitForSelector('.rail-btn[data-go="doctor"]');
  check('Doctor entry in the sidebar', await page.isVisible('.rail-btn[data-go="doctor"]'));
  const bridge = await page.evaluate(() => Object.keys(window.realm.doctor || {}).sort().join(','));
  check('steward bridge exposes doctor calls', bridge === 'classify,copyReport,log,run', bridge);
  await page.evaluate(() => window.realm.server.start('s1'));
  await page.waitForFunction(() => window.realm.server.status('s1').then((s) => s.state === 'running'), null, { timeout: 15000 });
  await sleep(3500);
  await page.click('.rail-btn[data-go="doctor"]');
  await page.waitForSelector('.doc-step', { timeout: 30000 });
  await page.waitForFunction(() => !document.getElementById('doc-run').disabled, null, { timeout: 30000 });
  // Run once more so the server has finished "loading" in the imitation.
  await page.click('#doc-run');
  await page.waitForFunction(() => !document.getElementById('doc-run').disabled && document.querySelectorAll('.doc-step').length > 5, null, { timeout: 30000 });
  const verdict = await page.textContent('.doc-verdict');
  check('verdict names the address:port mistake', /address box contains a port/i.test(verdict), verdict.slice(0, 120));
  const stepText = await page.textContent('.doc-steps');
  check('server process step is good', /runs as ROK\.exe/.test(stepText));
  check('server log step shows the ready line', /Server for 120 players started on port 7350/.test(stepText));
  check('A2S step answered on loopback', /Answered on 127\.0\.0\.1/.test(stepText));
  check('address advice keeps address and port apart', /address 127\.0\.0\.1, port 7350/.test(stepText));
  await page.waitForSelector('.doc-console .ln.hit.bad', { timeout: 10000 });
  const badLines = await page.$$eval('.doc-console .ln.hit.bad', (n) => n.map((x) => x.textContent).join('\n'));
  check('client log lines highlighted as problems', /Unable to resolve host name\. \(127\.0\.0\.1:7350\)/.test(badLines) && /Joining 127\.0\.0\.1:7350 : 7350/.test(badLines));
  // Live tail: a new line in the client log appears without re-running the checks.
  fs.appendFileSync(path.join(gameDir, 'Logs', `Log[${stamp(t0)}].txt`), `[${stamp()}] [Info]   Joining 127.0.0.1 : 7350\n[${stamp()}] [Info]   Connecting to '127.0.0.1:7350'.\n`);
  await page.waitForFunction(() => /Connecting to '127\.0\.0\.1:7350'/.test(document.querySelector('.doc-console').textContent), null, { timeout: 10000 }).then(
    () => check('live tail shows new game log lines', true),
    () => check('live tail shows new game log lines', false)
  );
  await page.fill('.doc-paste-input', 'Incorrect password.');
  await page.click('.doc-paste button');
  await page.waitForSelector('.doc-paste .doc-finding', { timeout: 5000 });
  check('pasted popup text is explained', /Wrong server password/.test(await page.textContent('.doc-paste .doc-findings')));
  await page.evaluate(() => document.querySelector('.doc-col').scrollTo(0, 0));
  await shot(page, 'steward');

  // Server log with "only known messages".
  const opts = await page.$$eval('.doc-log-pick option', (o) => o.map((x) => ({ v: x.value, t: x.textContent })));
  const srv = opts.find((o) => /^Server: Server game log/.test(o.t));
  check('server game log (Logs\\Log[...].txt) found', !!srv, opts.map((o) => o.t).join(' | '));
  if (srv) {
    await page.selectOption('.doc-log-pick', srv.v);
    await page.check('.doc-log-tools input[type=checkbox]');
    await page.waitForFunction(() => /denied connection because the password/.test(document.querySelector('.doc-console').textContent), null, { timeout: 8000 }).catch(() => {});
    const hits = await page.textContent('.doc-console');
    check('server log shows password refusal and ping kick', /password provided was invalid/.test(hits) && /ping system/.test(hits));
    await page.evaluate(() => document.querySelector('.doc-col').scrollTo(0, 400));
    await shot(page, 'steward-serverlog');
  }

  const report = await page.evaluate(() => window.realm.doctor.copyReport({ pasted: 'Incorrect password.', logIndex: 0 }));
  check('report is redacted (no public IP, no Steam ID)', !/203\.0\.113\.50/.test(report.text) && !/7656119\d{10}/.test(report.text), `${report.chars} chars`);
  check('report carries the verdict and a fix', /Verdict: .*address box/i.test(report.text) && /Fix:/.test(report.text));
  await page.evaluate(() => window.realm.server.stop('s1'));
  await sleep(1200);
  await app.close();
}

// ------------------------------------------------------------- Realm (player)
{
  const kp = PB.generateKeyPair();
  const now = Date.now();
  const iso = (t) => new Date(t).toISOString();
  const q1 = dgram.createSocket('udp4');
  q1.on('message', (msg, r) => msg[4] === 0x54 && q1.send(A2S.buildInfoReply({ players: 7 }), r.port, r.address));
  await new Promise((r) => q1.bind(0, '127.0.0.1', r));
  const servers = [
    { id: 's1', name: 'Realm I - Kingsfall', region: 'EU', address: '127.0.0.1', port: 47350, queryPort: q1.address().port, maxPlayers: 120 },
    { id: 's2', name: 'Realm II - Corvane', region: 'EU', address: '127.0.0.1', port: 47360, queryPort: 9, maxPlayers: 120 }
  ];
  const manifest = { schema: 1, realm: 'The Realm', seq: 3, issued: iso(now - 3600e3), expires: iso(now + 30 * 86400e3), servers, links: {} };
  const signed = JSON.stringify(PB.signManifest(manifest, kp.privateKeyPem));
  const listServer = http.createServer((req, res) => (req.url === '/servers.json' ? res.end(signed) : res.writeHead(404).end()));
  await new Promise((r) => listServer.listen(0, '127.0.0.1', r));
  const pc = JSON.parse(JSON.stringify(PB.playerConfig({ realmName: 'The Realm', tagline: 'Swear an oath. Claim a crown.', manifestUrl: 'https://example.invalid/servers.json', publicKey: kp.publicKey, envelope: PB.signManifest(manifest, kp.privateKeyPem), links: {} })));
  pc.manifestUrl = `http://127.0.0.1:${listServer.address().port}/servers.json`;
  const pcFile = path.join(base, 'player-config.json');
  await fsp.writeFile(pcFile, JSON.stringify(pc, null, 2));
  await write(path.join(playerData, 'realm-player.json'), JSON.stringify({ onboarded: true }));

  // The player tries again and pastes "address:port" once more: the newest attempt fails again.
  fs.appendFileSync(path.join(gameDir, 'Logs', `Log[${stamp(t0)}].txt`), `[${stamp()}] [Info]   Joining 127.0.0.1:47350 : 47350\n[${stamp()}] [Error]  Unable to resolve host name. (127.0.0.1:47350)\n`);
  const { app, page } = await launch([path.join(launcher, 'player', 'main.js')], { REALM_USER_DATA: playerData, REALM_DEV_PLAYER_CONFIG: pcFile, REALM_DEV_GAME_DIR: gameDir });
  await page.setViewportSize({ width: 1240, height: 800 }).catch(() => {});
  await sleep(1500);
  await page.evaluate(() => {
    const ob = document.getElementById('onboard');
    if (ob) ob.hidden = true;
  });
  check("Can't join? button present", await page.isVisible('[data-doctor-open]'));
  await page.click('[data-doctor-open]');
  await page.waitForSelector('.doc-modal .doc-step', { timeout: 20000 });
  await page.waitForFunction(() => document.querySelectorAll('.doc-modal .doc-step').length >= 4, null, { timeout: 20000 });
  const text = await page.textContent('.doc-modal');
  check('player verdict names the address:port mistake', /address box contains a port/i.test(text));
  check('player sees which server answers', /Realm I - Kingsfall/.test(text) && /Online/.test(text));
  check('player sees an offline server with a fix', /No status answer/.test(text));
  await shot(page, 'player');
  const rep = await page.evaluate(() => window.realm.doctorReport({ pasted: 'Unable to resolve host name. (127.0.0.1:7350)' }));
  check('player report copied and redacted', rep.chars > 100 && !/7656119\d{10}/.test(rep.text));
  await app.close();
  listServer.close();
  q1.close();
}

check('no renderer errors', errors.length === 0, errors.slice(0, 3).join(' | '));
fs.rmSync(base, { recursive: true, force: true });
const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
process.exit(failed.length ? 1 : 0);
