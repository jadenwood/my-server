// End-to-end walk-through of Realm (the player edition) under xvfb with Playwright's Electron
// support. It builds a signed servers.json with a throwaway key, serves it on 127.0.0.1 (http to this
// PC is allowed in development runs only), runs imitation servers (A2S responders and Chronicle
// /api/state endpoints), and saves screenshots to docs/img/player-<screen>.png.
//
//   xvfb-run -a node scripts/player-screens.mjs
//
// steam:// URLs are captured in the main process instead of being opened.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import dgram from 'node:dgram';
import { spawn, execSync } from 'node:child_process';
import { createRequire } from 'node:module';
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
const playerMain = path.join(launcher, 'player', 'main.js');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[player]', ...a);
const checks = [];
function check(name, ok, info = '') {
  checks.push({ name, ok: !!ok, info });
  log(`${ok ? 'PASS' : 'FAIL'} ${name}${info ? ' - ' + info : ''}`);
}

// ---------- the imitation realm ----------

const base = await fsp.mkdtemp(path.join(os.tmpdir(), 'realm-player-'));
const userData = path.join(base, 'profile');
await fsp.mkdir(imgDir, { recursive: true });
const kp = PB.generateKeyPair();
const now = Date.now();
const iso = (ms) => new Date(Math.floor(ms / 1000) * 1000).toISOString().replace('.000Z', 'Z');

const chronicles = [];
function startChronicle(state, events = []) {
  return new Promise((resolve) => {
    const srv = http.createServer((req, res) => {
      res.setHeader('Content-Type', 'application/json');
      if (req.url.startsWith('/api/state')) res.end(JSON.stringify({ ...state, updated: new Date().toISOString() }));
      else if (req.url.startsWith('/api/events')) res.end(JSON.stringify(events));
      else res.writeHead(404).end('{}');
    });
    srv.listen(0, '127.0.0.1', () => {
      chronicles.push(srv);
      resolve(`http://127.0.0.1:${srv.address().port}`);
    });
  });
}

const responders = [];
function startA2S(port, players) {
  return new Promise((resolve) => {
    const s = dgram.createSocket('udp4');
    s.on('message', (msg, r) => {
      if (msg.length < 29) s.send(A2S.buildChallengeReply(Buffer.from([5, 6, 7, 8])), r.port, r.address);
      else setTimeout(() => s.send(A2S.buildInfoReply({ players }), r.port, r.address), 8);
    });
    s.bind(port, '127.0.0.1', () => {
      responders.push(s);
      resolve();
    });
  });
}

const c1 = await startChronicle({ king: 'Aldric Varrow', house: 'Varrow', online: 87, maxPlayers: 120, houses: [], stale: false });
const c2 = await startChronicle({ king: 'Aldric Varrow', house: 'Varrow', online: 120, maxPlayers: 120, houses: [], stale: false });
const c3 = await startChronicle({ king: null, online: 0, maxPlayers: 120, houses: [], stale: true });
await startA2S(47015, 85);
await startA2S(47025, 120);
await startA2S(47045, 4);

const servers = [
  { id: 's1', name: 'Realm I - Ashveil', region: 'EU', address: '127.0.0.1', port: 47350, queryPort: 47015, maxPlayers: 120, chronicleUrl: c1 },
  { id: 's2', name: 'Realm II - Corvane', region: 'EU', address: '127.0.0.1', port: 47360, queryPort: 47025, maxPlayers: 120, chronicleUrl: c2 },
  { id: 's3', name: 'Realm III - Thornmere', region: 'NA', address: '127.0.0.1', port: 47370, queryPort: 47035, maxPlayers: 120, chronicleUrl: c3 },
  { id: 's4', name: 'Realm IV - Greywater', region: 'NA', address: '127.0.0.1', port: 47380, queryPort: 47045, maxPlayers: 120 }
];
const manifest = { schema: 1, realm: 'The Realm', seq: 5, issued: iso(now - 3600e3), expires: iso(now + 30 * 86400e3), servers, links: { rules: 'https://example.org/realm/rules' } };
const good = JSON.stringify(PB.signManifest(manifest, kp.privateKeyPem));
const bundled = PB.signManifest({ ...manifest, seq: 1, servers: [servers[0]] }, kp.privateKeyPem);
const tamperedEnv = JSON.parse(good);
const payload = JSON.parse(Buffer.from(tamperedEnv.payload, 'base64').toString());
payload.servers[0].address = '198.51.100.66';
tamperedEnv.payload = Buffer.from(JSON.stringify(payload, null, 2)).toString('base64');
const rollback = JSON.stringify(PB.signManifest({ ...manifest, seq: 2 }, kp.privateKeyPem));

let served = good;
const listServer = http.createServer((req, res) => {
  if (req.url !== '/servers.json') return res.writeHead(404).end();
  res.setHeader('Content-Type', 'application/json');
  res.end(served);
});
await new Promise((r) => listServer.listen(0, '127.0.0.1', r));
const manifestUrl = `http://127.0.0.1:${listServer.address().port}/servers.json`;
const playerConfig = path.join(base, 'player-config.json');
await fsp.writeFile(playerConfig, JSON.stringify(PB.playerConfig({ realmName: 'The Realm', tagline: 'Swear an oath. Claim a crown. Answer for it.', manifestUrl: 'https://example.invalid/servers.json', publicKey: kp.publicKey, envelope: bundled, links: { rules: 'https://example.org/realm/rules' } }), null, 2));
// playerConfig() accepts https only; the development run swaps in the local http address.
const pcData = JSON.parse(await fsp.readFile(playerConfig, 'utf8'));
pcData.manifestUrl = manifestUrl;
await fsp.writeFile(playerConfig, JSON.stringify(pcData, null, 2));
log('sandbox', base);

const env = { ...process.env, REALM_USER_DATA: userData, REALM_DEV_PLAYER_CONFIG: playerConfig };

async function launch(extraArgs = [], extraEnv = {}) {
  const app = await electron.launch({ executablePath: electronBin, args: ['--no-sandbox', playerMain, ...extraArgs], env: { ...env, ...extraEnv } });
  const page = await app.firstWindow();
  await page.setViewportSize({ width: 1240, height: 800 }).catch(() => {});
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => document.fonts.ready);
  await app.evaluate(({ shell }) => {
    globalThis.__opened = [];
    shell.openExternal = async (u) => {
      globalThis.__opened.push(u);
    };
  });
  return { app, page };
}

const opened = (app) => app.evaluate(() => globalThis.__opened.slice());
// The join panel checks the server first (up to the A2S timeout), then hands the URL to Steam.
async function openedAfter(app, n, ms = 8000) {
  for (let t = 0; t < ms; t += 200) {
    const o = await opened(app);
    if (o.length > n) return o;
    await sleep(200);
  }
  return opened(app);
}
async function closeJoinPanel(page) {
  if (await page.isVisible('#joinflow')) await page.click('#jf-close');
}

async function shot(page, name) {
  await sleep(600);
  await page.evaluate(() => document.getElementById('toasts') && document.getElementById('toasts').replaceChildren());
  const file = path.join(imgDir, `player-${name}.png`);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}

async function coachPage(app) {
  for (let i = 0; i < 40; i++) {
    for (const w of app.windows()) if (/coach\.html/.test(w.url())) return w;
    await sleep(150);
  }
  return null;
}

let app;
let page;
const errors = [];
try {
  ({ app, page } = await launch(['realm://join/s1']));
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));

  // ---- the bridge is the player bridge only
  const sec = await page.evaluate(() => ({ require: typeof require, process: typeof process, keys: Object.keys(window.realm || {}).sort() }));
  check('renderer has no require/process', sec.require === 'undefined' && sec.process === 'undefined');
  check('player bridge has no server, setup, firewall or publish calls', sec.keys.join(',') === 'appInfo,copyAddress,doctorClassify,doctorReport,doctorRun,feed,getConfig,installGame,installState,join,joinBest,onPush,openLink,prefs,servers,setPrefs,status,takeLink,windowAction', sec.keys.join(','));
  const steward = await page.evaluate(() => ['server', 'setup', 'fleet', 'goPublic', 'publish', 'settings'].filter((k) => k in window.realm));
  check('steward APIs are absent', steward.length === 0, steward.join(','));
  const badJoin = await page.evaluate(() => window.realm.join('../../etc').then(() => 'ran', (e) => e.message));
  check('join validates the server id', /invalid/.test(badJoin), badJoin);
  const notListed = await page.evaluate(() => window.realm.join('s9').then(() => 'ran', (e) => e.message));
  check('join refuses ids that are not in the signed list', /not in the signed list/.test(notListed), notListed);

  // ---- first-run intro
  // First run (sigil reveal -> allegiance -> 4-card crown tour); scripts/onboarding-screens.mjs covers it in depth.
  await page.waitForSelector('#onboard:not([hidden])', { timeout: 10000 });
  check('60-second intro opens on first run', true);
  await page.click('#ob-begin');
  await page.click('#ob-unsworn');
  await page.click('#onboard-next');
  await shot(page, 'intro');
  await page.click('#onboard-next');
  await page.click('#onboard-next');
  check('last intro card offers the rules link', await page.isVisible('#onboard-rules'));
  await page.click('#onboard-next');
  await page.waitForSelector('#onboard', { state: 'hidden' });

  // ---- deep link from the command line: confirmation, never an automatic launch
  await page.waitForSelector('#modal:not([hidden])', { timeout: 10000 });
  check('realm://join/s1 asks before joining', /Join Realm I - Ashveil\?/.test(await page.textContent('#modal-title')));
  check('nothing launched before the player confirms', (await opened(app)).length === 0);
  await page.waitForFunction(() => /87 of 120 players/.test(document.querySelector('#modal-text').textContent), null, { timeout: 10000 }).catch(() => {});
  await shot(page, 'deeplink');
  await page.click('#modal-ok');
  const firstOpen = await openedAfter(app, 0);
  check('JOIN opens the quick-join Steam URL for that server', firstOpen[0] === 'steam://run/344760//-ip%20127.0.0.1%20-port%2047350/', firstOpen.join(' '));
  const coach = await coachPage(app);
  check('the always-on-top join card opens', !!coach);
  if (coach) {
    await coach.waitForLoadState('domcontentloaded');
    await sleep(500);
    await coach.screenshot({ path: path.join(imgDir, 'player-coach.png') });
    log('saved docs/img/player-coach.png');
    check('join card shows the address and port', /127\.0\.0\.1/.test(await coach.textContent('#host')) && (await coach.textContent('#port')) === '47350');
    const top = await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows().some((w) => w.isAlwaysOnTop()));
    check('join card is always on top', top);
    await coach.click('#close');
  }

  // ---- home: live status and best pick
  await page.waitForFunction(() => document.querySelectorAll('.server-card .pill.ok').length >= 2, null, { timeout: 20000 });
  await sleep(800);
  const cards = await page.$$eval('.server-card', (els) => els.map((e) => ({ id: e.dataset.id, pill: e.querySelector('.sc-pill').textContent, best: e.classList.contains('best'), count: e.querySelector('.count').textContent })));
  check('signed list loaded online (4 servers)', cards.length === 4 && /Signed/.test(await page.textContent('#list-pill')), JSON.stringify(cards));
  check('status: lively, full, offline and A2S-only servers', cards[0].pill === 'Online' && cards[1].pill === 'Full' && cards[2].pill === 'Offline' && cards[3].pill === 'Online', cards.map((c) => c.pill).join(','));
  check('A2S-only player count marked approximate', /^~4 \/ 120$/.test(cards[3].count), cards[3].count);
  check('best pick is the lively, not-full server', cards[0].best && !cards[1].best, cards.filter((c) => c.best).map((c) => c.id).join(','));
  check('offline server cannot be joined', await page.isDisabled('.server-card[data-id="s3"] [data-join]'));
  check('crown read from the Chronicle', /Aldric Varrow/.test(await page.textContent('#king')));
  await closeJoinPanel(page);
  await shot(page, 'home');
  const nBefore = (await opened(app)).length;
  await page.click('#join-best');
  const afterBest = await openedAfter(app, nBefore);
  check('Join best server picks Realm I', afterBest[afterBest.length - 1] === 'steam://run/344760//-ip%20127.0.0.1%20-port%2047350/', afterBest[afterBest.length - 1]);
  const c2w = await coachPage(app);
  if (c2w) await c2w.close().catch(() => {});
  await closeJoinPanel(page);

  // ---- a bad link from a second launch is refused with a message
  await new Promise((resolve) => {
    const child = spawn(electronBin, ['--no-sandbox', playerMain, 'realm://join/zz'], { env, stdio: 'ignore' });
    child.on('exit', resolve);
    setTimeout(resolve, 8000);
  });
  await page.waitForFunction(() => /not valid/.test(document.querySelector('#toasts').textContent), null, { timeout: 8000 }).then(
    () => check('unknown realm:// link refused, nothing started', true),
    () => check('unknown realm:// link refused, nothing started', false)
  );

  // ---- tampered and rolled-back lists are refused
  served = JSON.stringify(tamperedEnv);
  await page.click('#list-refresh');
  await page.waitForFunction(() => /refused/.test(document.querySelector('#list-note').textContent), null, { timeout: 15000 });
  const note1 = await page.textContent('#list-note');
  check('tampered list refused, last good list kept', /signature does not match/.test(note1) && /Saved copy/.test(await page.textContent('#list-pill')), note1);
  check('tampered address never shown', !(await page.textContent('#server-list')).includes('198.51.100.66'));
  served = rollback;
  await page.click('#list-refresh');
  await page.waitForFunction(() => /rollback/.test(document.querySelector('#list-note').textContent), null, { timeout: 15000 }).then(
    () => check('older (rolled-back) list refused', true),
    async () => check('older (rolled-back) list refused', false, await page.textContent('#list-note'))
  );
  served = good;

  // ---- settings: classic join + guide
  await page.click('.rail-btn[data-go="settings"]');
  await sleep(500);
  await page.check('#jm-classic');
  await sleep(500);
  await shot(page, 'settings');
  await page.click('.rail-btn[data-go="home"]');
  await sleep(400);
  await closeJoinPanel(page);
  const nClassic = (await opened(app)).length;
  await page.click('.server-card[data-id="s4"] [data-join]');
  const classic = await openedAfter(app, nClassic);
  check('classic join only starts the game through Steam', classic[classic.length - 1] === 'steam://rungameid/344760', classic[classic.length - 1]);
  const c3w = await coachPage(app);
  if (c3w) {
    await c3w.waitForLoadState('domcontentloaded');
    await sleep(400);
    await c3w.screenshot({ path: path.join(imgDir, 'player-coach-classic.png') });
    log('saved docs/img/player-coach-classic.png');
    check('classic card tells where to paste', /Direct Connect/.test(await c3w.textContent('#steps')) && (await c3w.textContent('#port')) === '47380');
    await c3w.close().catch(() => {});
  }
  const clip = await app.evaluate(({ clipboard }) => clipboard.readText());
  check('address copied for pasting', clip === '127.0.0.1', clip);
  await closeJoinPanel(page);
  await page.click('.rail-btn[data-go="guide"]');
  await sleep(500);
  await shot(page, 'guide');
  await page.click('.rail-btn[data-go="settings"]');
  await page.check('#jm-quick');
  await page.check('#pref-notify');
  await sleep(400);
  await page.uncheck('#pref-notify');
  check('no renderer errors', errors.length === 0, errors.join(' | '));
} finally {
  if (app) await app.close().catch(() => {});
}

// ---- second run: the game is not installed
try {
  ({ app, page } = await launch([], { REALM_DEV_GAME_INSTALL: 'missing' }));
  await page.waitForSelector('#install-banner:not([hidden])', { timeout: 15000 });
  check('missing game is detected and Install is offered', true);
  check('intro not shown again', await page.isHidden('#onboard'));
  await page.waitForFunction(() => document.querySelectorAll('.server-card .pill.ok').length >= 2, null, { timeout: 20000 }).catch(() => {});
  await shot(page, 'install');
  await page.click('.server-card[data-id="s1"] [data-join]');
  await page.waitForSelector('#modal:not([hidden])', { timeout: 5000 });
  check('JOIN without the game asks to install it first', /Install Reign of Kings first/.test(await page.textContent('#modal-title')));
  await page.click('#modal-ok');
  await sleep(600);
  const o = await opened(app);
  check('install goes to steam://install/344760 and nothing else', o.length === 1 && o[0] === 'steam://install/344760', o.join(' '));
} finally {
  if (app) await app.close().catch(() => {});
}

listServer.close();
for (const s of chronicles) s.close();
for (const s of responders) s.close();
const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) process.exitCode = 1;
