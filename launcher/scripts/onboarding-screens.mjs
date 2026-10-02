// Onboarding & polish walk-through: the player's first run (sigil reveal, sworn allegiance, crown
// tour), the Realm feed with a next-event countdown, the staged join panel, and the Steward Home
// dashboard strip. Saves docs/img/player-firstrun.png, player-allegiance.png, player-tour.png,
// player-home.png, player-join.png and steward-home.png, and checks what it can.
//
//   xvfb-run -a node scripts/onboarding-screens.mjs
//
// The player part runs the real player edition under Electron against imitation servers (A2S
// responders and Chronicle /api/state + /api/events endpoints, like scripts/player-screens.mjs).
// steam:// URLs are captured, never opened. The Steward part renders renderer/index.html in headless
// Chromium with the mock bridge (renderer/mock-preload.js) and a three-server fleet.
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import dgram from 'node:dgram';
import { execSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { startPreview } from './preview-server.mjs';

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
const { _electron: electron, chromium } = loadPlaywright();
const electronBin = path.join(launcher, 'node_modules', 'electron', 'dist', 'electron');
const playerMain = path.join(launcher, 'player', 'main.js');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[onboarding]', ...a);
const checks = [];
function check(name, ok, info = '') {
  checks.push({ name, ok: !!ok, info });
  log(`${ok ? 'PASS' : 'FAIL'} ${name}${info ? ' - ' + info : ''}`);
}

// ---------- the imitation realm ----------

const base = await fsp.mkdtemp(path.join(os.tmpdir(), 'realm-onboarding-'));
await fsp.mkdir(imgDir, { recursive: true });
const kp = PB.generateKeyPair();
const now = Date.now();
const iso = (ms) => new Date(Math.floor(ms / 1000) * 1000).toISOString().replace('.000Z', 'Z');
const minAgo = (m) => iso(now - m * 60000);

const servers = [];
const closers = [];
function startChronicle(state, events) {
  return new Promise((resolve) => {
    const srv = http.createServer((req, res) => {
      res.setHeader('Content-Type', 'application/json');
      if (req.url.startsWith('/api/state')) res.end(JSON.stringify({ ...state, updated: new Date().toISOString() }));
      else if (req.url.startsWith('/api/events')) res.end(JSON.stringify(events));
      else res.writeHead(404).end('{}');
    });
    srv.listen(0, '127.0.0.1', () => {
      closers.push(() => srv.close());
      resolve(`http://127.0.0.1:${srv.address().port}`);
    });
  });
}
function startA2S(port, players) {
  return new Promise((resolve) => {
    const s = dgram.createSocket('udp4');
    s.on('message', (msg, r) => {
      if (msg.length < 29) s.send(A2S.buildChallengeReply(Buffer.from([5, 6, 7, 8])), r.port, r.address);
      else setTimeout(() => s.send(A2S.buildInfoReply({ players }), r.port, r.address), 8);
    });
    s.bind(port, '127.0.0.1', () => {
      closers.push(() => s.close());
      resolve();
    });
  });
}

const houses1 = [
  { name: 'Varrow', sigil: 'Iron Stag', liege: null, members: 9 },
  { name: 'Ashgrove', sigil: 'White Oak', liege: 'Varrow', members: 6 },
  { name: 'Corvane', sigil: 'Black Raven', liege: null, members: 7 }
];
const houses2 = [
  { name: 'Dunmere', sigil: 'Drowned Bell', liege: null, members: 4 },
  { name: 'Corvane', sigil: 'Black Raven', liege: null, members: 3 }
];
const ev1 = [
  { id: 41, ts: minAgo(190), type: 'oath_sworn', title: 'House Ashgrove swears fealty to House Varrow', detail: '', actors: [] },
  { id: 42, ts: minAgo(95), type: 'coronation', title: 'Aldric Varrow takes the throne', detail: 'House Varrow now holds the crown.', actors: [] },
  { id: 43, ts: minAgo(12), type: 'claim_declared', title: 'House Corvane raises a claim at the Hearth', detail: '', actors: [] }
];
const ev2 = [
  { id: 7, ts: minAgo(48), type: 'oath_broken', title: 'House Dunmere renounces its oath to House Corvane', detail: '', actors: [] },
  { id: 8, ts: minAgo(3), type: 'decree', title: 'The Crown proclaims Open Roads', detail: '', actors: [] }
];
const nextAt = iso(now + ((2 * 24 + 5) * 3600 + 17 * 60 + 40) * 1000);
const c1 = await startChronicle({ king: 'Aldric Varrow', house: 'Varrow', online: 64, maxPlayers: 120, houses: houses1, stale: false, next: { title: 'Crown Night', at: nextAt } }, ev1);
const c2 = await startChronicle({ king: 'Ysolde Corvane', house: 'Corvane', online: 31, maxPlayers: 120, houses: houses2, stale: false }, ev2);
await startA2S(48015, 63);
await startA2S(48025, 31);
servers.push(
  { id: 's1', name: 'Realm I - Ashveil', region: 'EU', address: '127.0.0.1', port: 48350, queryPort: 48015, maxPlayers: 120, chronicleUrl: c1 },
  { id: 's2', name: 'Realm II - Corvane', region: 'NA', address: '127.0.0.1', port: 48360, queryPort: 48025, maxPlayers: 120, chronicleUrl: c2 }
);
const manifest = { schema: 1, realm: 'The Realm', seq: 3, issued: iso(now - 3600e3), expires: iso(now + 30 * 86400e3), servers, links: { rules: 'https://example.org/realm/rules' } };
const signed = JSON.stringify(PB.signManifest(manifest, kp.privateKeyPem));
const listServer = http.createServer((req, res) => {
  if (req.url !== '/servers.json') return res.writeHead(404).end();
  res.setHeader('Content-Type', 'application/json');
  res.end(signed);
});
await new Promise((r) => listServer.listen(0, '127.0.0.1', r));
closers.push(() => listServer.close());
const playerConfig = path.join(base, 'player-config.json');
const pc = PB.playerConfig({ realmName: 'The Realm', tagline: 'Swear an oath. Claim a crown. Answer for it.', manifestUrl: 'https://example.invalid/servers.json', publicKey: kp.publicKey, envelope: PB.signManifest(manifest, kp.privateKeyPem), links: { rules: 'https://example.org/realm/rules' } });
pc.manifestUrl = `http://127.0.0.1:${listServer.address().port}/servers.json`; // development runs only
await fsp.writeFile(playerConfig, JSON.stringify(pc, null, 2));

async function shot(page, name) {
  await page.evaluate(() => document.getElementById('toasts') && document.getElementById('toasts').replaceChildren());
  const file = path.join(imgDir, name);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}

// ---------- player ----------

let app;
const errors = [];
try {
  app = await electron.launch({ executablePath: electronBin, args: ['--no-sandbox', playerMain], env: { ...process.env, REALM_USER_DATA: path.join(base, 'profile'), REALM_DEV_PLAYER_CONFIG: playerConfig } });
  const page = await app.firstWindow();
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  await page.setViewportSize({ width: 1240, height: 800 }).catch(() => {});
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => document.fonts.ready);
  await app.evaluate(({ shell }) => {
    globalThis.__opened = [];
    shell.openExternal = async (u) => {
      globalThis.__opened.push(u);
    };
  });

  // first run: reveal
  await page.waitForSelector('#ob-reveal:not([hidden])', { timeout: 10000 });
  check('first run opens on the sigil reveal', true);
  await sleep(5200); // let the reveal finish (about 4.5 s)
  check('reveal title shows the realm name', /The Realm/i.test(await page.textContent('#ob-heading')));
  check('Enter the Realm has focus', await page.evaluate(() => document.activeElement && document.activeElement.id === 'ob-begin'));
  await shot(page, 'player-firstrun.png');

  // allegiance
  await page.click('#ob-begin');
  await page.waitForSelector('#ob-houses:not([hidden])');
  check('six great houses offered', (await page.$$('.house-opt')).length === 6);
  check('Swear is disabled until a house is chosen', await page.isDisabled('#ob-swear'));
  await page.keyboard.press('ArrowRight'); // radio group keyboard navigation
  await page.click('.house-opt:has(input[value="varrow"])');
  check('choosing Varrow enables Swear', /Varrow/.test(await page.textContent('#ob-swear')) && !(await page.isDisabled('#ob-swear')));
  await sleep(400);
  await shot(page, 'player-allegiance.png');
  await page.click('#ob-swear');

  // tour
  await page.waitForSelector('#ob-tour:not([hidden])');
  const titles = [];
  for (let i = 0; i < 4; i++) {
    titles.push(await page.textContent('#onboard-title'));
    if (i === 1) {
      await sleep(500);
      await shot(page, 'player-tour.png');
    }
    if (i < 3) await page.click('#onboard-next');
  }
  check('tour has four crown cards', titles.length === 4 && new Set(titles).size === 4, titles.join(' | '));
  check('last tour card offers the rules', await page.isVisible('#onboard-rules'));
  await page.click('#onboard-next');
  await page.waitForSelector('#onboard', { state: 'hidden' });
  check('allegiance stored on this PC', (await page.evaluate(() => localStorage.getItem('realm.allegiance'))) === 'varrow');

  // home: banner, sworn server, feed, countdown
  await page.waitForFunction(() => document.querySelectorAll('.server-card .pill.ok').length >= 2, null, { timeout: 20000 });
  await page.waitForFunction(() => document.querySelectorAll('#feed-list .feed-item').length >= 4, null, { timeout: 20000 }).catch(() => {});
  await sleep(900);
  check('banner shows the sworn house', /House Varrow/.test(await page.textContent('#banner-name')));
  const sug = await page.textContent('#banner-suggest');
  check('suggests the server where Varrow holds the crown', /holds the crown on Realm I/.test(sug), sug);
  check('server card marks where the house plays', (await page.$$('.server-card.sworn')).length === 1);
  const feedTitles = await page.$$eval('#feed-list .feed-title', (els) => els.map((e) => e.textContent));
  check('feed merges both servers, newest first', feedTitles[0] === 'The Crown proclaims Open Roads' && feedTitles.length === 5, feedTitles.join(' | '));
  check('next event countdown shown', !(await page.isHidden('#feed-next')) && /Crown Night/.test(await page.textContent('#feed-next-title')));
  const c0 = await page.textContent('#feed-count-s');
  await sleep(1300);
  check('countdown ticks', c0 !== (await page.textContent('#feed-count-s')));
  await shot(page, 'player-home.png');

  // join progress
  await page.click('.server-card[data-id="s1"] [data-join]');
  await page.waitForSelector('#joinflow:not([hidden])');
  check('join panel opens with Checking server', /now|done/.test(await page.getAttribute('[data-step="check"]', 'class')));
  await page.waitForFunction(() => document.querySelector('[data-step="game"]').className === 'now', null, { timeout: 10000 });
  const opened = await app.evaluate(() => globalThis.__opened.slice());
  check('Steam quick-join URL handed over', opened[0] === 'steam://run/344760//-ip%20127.0.0.1%20-port%2048350/', opened.join(' '));
  check('address and port shown in separate boxes', (await page.textContent('#jf-addr')) === '127.0.0.1' && (await page.textContent('#jf-port')) === '48350');
  await page.waitForFunction(() => document.querySelector('[data-step="join"]').className === 'now', null, { timeout: 15000 });
  check('steps advance to Joining', /Joining Realm I/.test(await page.textContent('#jf-join-label')));
  for (const w of app.windows()) if (/coach\.html/.test(w.url())) await w.close().catch(() => {});
  await page.bringToFront().catch(() => {});
  await shot(page, 'player-join.png');
  await page.click('#jf-copy-port');
  await sleep(300);
  const clip = await app.evaluate(({ clipboard }) => clipboard.readText());
  check('port copies on its own', clip === '48350', clip);
  await page.keyboard.press('Escape');
  check('Escape closes the join panel', await page.isHidden('#joinflow'));

  // reduced motion: the reveal is static
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.click('#banner-change');
  await page.waitForSelector('#ob-houses:not([hidden])');
  check('Change reopens the allegiance step', true);
  await page.click('#ob-unsworn');
  check('stay unsworn clears the banner', /Unsworn/.test(await page.textContent('#banner-name')));
  await page.evaluate(() => {
    document.getElementById('onboard').hidden = false;
    for (const id of ['ob-reveal', 'ob-houses', 'ob-tour']) document.getElementById(id).hidden = id !== 'ob-reveal';
  });
  const anim = await page.evaluate(() => getComputedStyle(document.querySelector('.ob-sigil')).animationName + '/' + getComputedStyle(document.querySelector('.ob-title')).opacity);
  check('reduced motion: no reveal animation, content visible', anim === 'none/1', anim);
  check('no renderer errors', errors.length === 0, errors.join(' | '));
} finally {
  if (app) await app.close().catch(() => {});
}

// ---------- steward home (mock bridge) ----------

const fleetMock = `(() => {
  let r;
  const iso = (m) => new Date(Date.now() + m * 60000).toISOString();
  const fleet = [
    { id: 's1', slot: 1, name: 'Realm I - Ashveil', copied: true, rootOk: true, root: 'G:\\\\RealmTest\\\\server', ports: { game: 7350 }, maxPlayers: 120, network: 'public', state: 'running', ready: true, players: 64, dailyRestart: { enabled: true, time: '06:00' }, supervisor: { plannedAt: iso(9 * 60 + 12), crashes24h: 1, lastCrashAt: iso(-185), lastBackup: { at: iso(-185) } } },
    { id: 's2', slot: 2, name: 'Realm II - Corvane', copied: true, rootOk: true, root: 'G:\\\\RealmTest\\\\s2\\\\server', ports: { game: 7360 }, maxPlayers: 120, network: 'public', state: 'running', ready: true, players: 31, dailyRestart: { enabled: true, time: '06:30' }, supervisor: { plannedAt: iso(9 * 60 + 42), crashes24h: 0 } },
    { id: 's3', slot: 3, name: 'Realm III - Proving Grounds', copied: true, rootOk: true, root: 'G:\\\\RealmTest\\\\s3\\\\server', ports: { game: 7370 }, maxPlayers: 40, network: 'local', state: 'stopped', ready: false, players: 0, dailyRestart: { enabled: false, time: '06:00' }, supervisor: { crashes24h: 0 } }
  ];
  Object.defineProperty(window, 'realm', { configurable: true, get: () => r, set: (v) => {
    r = v;
    v.fleet.list = () => Promise.resolve(fleet);
    v.server.listBackups = (id) => Promise.resolve({ dir: '', list: id === 's2' ? [{ name: 'realm-saves-auto.zip', size: 1, mtime: iso(-42) }] : [] });
  } });
})();`;

const preview = await startPreview(5181);
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium' });
try {
  const page = await browser.newPage({ viewport: { width: 1320, height: 840 } });
  const perr = [];
  page.on('pageerror', (e) => perr.push(e.message));
  await page.addInitScript(fleetMock);
  await page.goto('http://127.0.0.1:5181/');
  await page.waitForSelector('#dash-strip:not([hidden]) .dash-inst', { timeout: 10000 });
  await page.evaluate(() => {
    const w = document.getElementById('wizard');
    if (w) w.hidden = true;
  });
  await sleep(1500);
  const tiles = await page.$$eval('.dash-tile', (els) => els.map((e) => e.querySelector('.dash-label').textContent + '=' + e.querySelector('.dash-value').textContent));
  check('dashboard: five tiles', tiles.length === 5, tiles.join(', '));
  check('dashboard: 2 of 3 up, 95 players', tiles[0] === 'Servers up=2 / 3' && tiles[1] === 'Players online=95', tiles.join(', '));
  check('dashboard: last backup from the newest zip', /42 min ago/.test(tiles[3]), tiles[3]);
  check('dashboard: next restart counts down', /in 9 h/.test(tiles[4]), tiles[4]);
  check('dashboard: one row per instance', (await page.$$('.dash-inst')).length === 3);
  await page.screenshot({ path: path.join(imgDir, 'steward-home.png') });
  log('saved docs/img/steward-home.png');
  await page.click('.dash-inst[data-dash-inst="s2"]');
  await sleep(500);
  check('clicking an instance opens the Server screen', (await page.evaluate(() => document.body.dataset.view)) === 'server');
  check('no steward page errors', perr.length === 0, perr.join(' | '));
} finally {
  await browser.close();
  preview.close();
}

for (const c of closers) c();
const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) process.exitCode = 1;
