// End-to-end walk-through of Realm (the player edition) under xvfb with Playwright's Electron
// support. It signs servers.json, news.json and update.json with a throwaway key, serves them and a
// fake installer on 127.0.0.1 (http to this PC is allowed in development runs only), runs imitation
// servers (A2S responders and Chronicle /api/state endpoints), and saves screenshots to
// docs/img/player-<screen>.png.
//
//   xvfb-run -a node scripts/player-screens.mjs
//
// steam:// URLs and the installer hand-off (shell.openPath) are captured in the main process
// instead of being opened. REALM_DEV_VERSION makes the app pretend to be an older build.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import dgram from 'node:dgram';
import crypto from 'node:crypto';
import { spawn, execSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const repo = path.resolve(launcher, '..');
const imgDir = path.join(repo, 'docs', 'img');
const require = createRequire(import.meta.url);
const PB = require(path.join(launcher, 'lib', 'publish.js'));
const NW = require(path.join(launcher, 'lib', 'news.js'));
const UP = require(path.join(launcher, 'lib', 'updater.js'));
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
const other = PB.generateKeyPair();
const now = Date.now();
const iso = (ms) => new Date(Math.floor(ms / 1000) * 1000).toISOString().replace('.000Z', 'Z');
const H = 3600e3;

const closers = [];
function startChronicle(state) {
  return new Promise((resolve) => {
    const srv = http.createServer((req, res) => {
      res.setHeader('Content-Type', 'application/json');
      if (req.url.startsWith('/api/state')) res.end(JSON.stringify({ ...state, updated: new Date().toISOString() }));
      else if (req.url.startsWith('/api/events')) res.end('[]');
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

const c1 = await startChronicle({ king: 'Aldric Varrow', house: 'Varrow', online: 87, maxPlayers: 120, houses: [], stale: false });
const c2 = await startChronicle({ king: 'Aldric Varrow', house: 'Varrow', online: 120, maxPlayers: 120, houses: [], stale: false });
await startA2S(47015, 85);
await startA2S(47025, 120);

const servers = [
  { id: 's1', name: 'Realm I - Ashveil', region: 'EU', address: '127.0.0.1', port: 47350, queryPort: 47015, maxPlayers: 120, chronicleUrl: c1 },
  { id: 's2', name: 'Realm II - Corvane', region: 'EU', address: '127.0.0.1', port: 47360, queryPort: 47025, maxPlayers: 120, chronicleUrl: c2 },
  { id: 's3', name: 'Realm III - Thornmere', region: 'NA', address: '127.0.0.1', port: 47370, queryPort: 47035, maxPlayers: 120 }
];
const manifest = { schema: 1, realm: 'The Realm', seq: 5, issued: iso(now - H), expires: iso(now + 30 * 86400e3), servers, links: { rules: 'https://example.org/realm/rules', discord: 'https://discord.gg/example' } };
const goodList = JSON.stringify(PB.signManifest(manifest, kp.privateKeyPem));
const tamperedList = (() => {
  const env = JSON.parse(goodList);
  const p = JSON.parse(Buffer.from(env.payload, 'base64').toString());
  p.servers[0].address = '198.51.100.66';
  return JSON.stringify({ ...env, payload: Buffer.from(JSON.stringify(p, null, 2)).toString('base64') });
})();

// The news feed: every type, one pinned item, one scheduled item that must stay hidden.
const newsItems = [
  { id: 'charter', type: 'announcement', pinned: true, title: 'The Hearth Charter is law', body: 'The crown belongs to the seat, not the blood. Read the Charter before you rebel: claims must be declared an hour before a rebellion window.', date: iso(now - 9 * 86400e3), link: 'https://example.org/realm/rules' },
  { id: 'crowned', type: 'chronicle', title: 'Aldric Varrow takes the Old Throne', body: 'House Varrow held the Hearth through the Lawful Hours. Corvane withdrew its claim at dawn.', date: iso(now - 3 * H), server: 's1' },
  { id: 'week-3', type: 'season', title: 'Season 1, week 3: the Hollow Crown', body: 'Varrow leads the standings, Corvane two oaths behind. The Hall of Kings gains its first name on Sunday.', date: iso(now - 20 * H) },
  { id: 'ransom-rule', type: 'announcement', title: 'Ransoms now settle in Marks', body: 'Captives are still held ten minutes at most. Use /ransom list in game.', date: iso(now - 2 * 86400e3) },
  { id: 'oath-broken', type: 'chronicle', title: 'House Dunmere breaks its oath to Corvane', body: 'The Chronicle marks Dunmere as oathbreakers until the season ends.', date: iso(now - 3 * 86400e3), server: 's2' },
  { id: 'future', type: 'announcement', title: 'SCHEDULED: must not show yet', date: iso(now + 5 * 86400e3) },
  { id: 'heron', type: 'realm', tag: 'sculpture', title: 'The Grey Heron stands at the harbour gate', body: 'A new sculpture in the capital.', date: iso(now - 26 * H), server: 's1' },
  { id: 'sigil-signs', type: 'realm', tag: 'sign', title: 'House sigils on the great hall signs', body: 'Painted signs in the capital now carry each house sigil.', date: iso(now - 50 * H) },
  { id: 'crown-night', type: 'realm', tag: 'event', title: 'Crown Night', body: 'The Lawful Hours open at the Hearth.', date: iso(now - 30 * H), at: iso(now + 2 * 86400e3 + 5 * H) }
];
const newsPayload = (seq, items = newsItems) => NW.buildNews({ realm: 'The Realm', seq, items }, { now: new Date(now - 2 * H) });
const goodNews = JSON.stringify(NW.signNews(newsPayload(3), kp.privateKeyPem));
const tamperedNews = (() => {
  const env = JSON.parse(JSON.stringify(NW.signNews(newsPayload(4), kp.privateKeyPem)));
  const p = JSON.parse(Buffer.from(env.payload, 'base64').toString());
  p.items[0].title = 'Free gold: join 198.51.100.66';
  return JSON.stringify({ ...env, payload: Buffer.from(JSON.stringify(p, null, 2)).toString('base64') });
})();

// The fake installer and its signed update manifests.
const installer = crypto.randomBytes(2 * 1024 * 1024 + 333);
const sha = crypto.createHash('sha256').update(installer).digest('hex');
const tamperedInstaller = Buffer.from(installer);
tamperedInstaller[4242] ^= 0x55;
const notes110 = { title: 'The Ember Update', items: ['News, season news and Chronicle highlights on the home screen.', "What's new in the realm: sculptures, sign art and events you see when you join.", 'Realm checks for updates and installs them for you, verified against the realm signature.', 'Faster start, and the server status right above Play.'] };

let filesServer;
let paused = null; // a promise the installer route waits on half-way, so the screenshot shows progress
let release = () => {};
const routes = { '/servers.json': () => goodList, '/news.json': () => goodNews, '/update.json': () => null, installer: () => installer };
filesServer = http.createServer(async (req, res) => {
  const u = req.url.split('?')[0];
  if (u.startsWith('/Realm-Setup-')) {
    const body = routes.installer();
    res.setHeader('Content-Length', body.length);
    const half = Math.floor(body.length * 0.46);
    for (let i = 0; i < body.length; i += 64 * 1024) {
      if (i >= half && paused) {
        await paused;
        paused = null;
      }
      res.write(body.subarray(i, Math.min(body.length, i + 64 * 1024)));
      await sleep(15);
    }
    return res.end();
  }
  const f = routes[u];
  const body = f ? f() : null;
  if (body == null) return res.writeHead(404).end();
  res.setHeader('Content-Type', 'application/json');
  res.end(body);
});
await new Promise((r) => filesServer.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${filesServer.address().port}`;
const update = (over = {}) => JSON.stringify(UP.signUpdate(UP.buildUpdate({ seq: 2, version: '1.1.0', url: `${origin}/Realm-Setup-1.1.0.exe`, size: installer.length, sha256: sha, notes: notes110, allowLocalHttp: true, ...over }, { now: new Date(now - H) }), kp.privateKeyPem, { allowLocalHttp: true }));

const playerConfig = path.join(base, 'player-config.json');
const pc = PB.playerConfig({ realmName: 'The Realm', tagline: 'Swear an oath. Claim a crown. Answer for it.', manifestUrl: 'https://example.invalid/servers.json', publicKey: kp.publicKey, envelope: PB.signManifest({ ...manifest, seq: 1, servers: [servers[0]] }, kp.privateKeyPem), links: { rules: 'https://example.org/realm/rules', discord: 'https://discord.gg/example' } });
// playerConfig() accepts https only; the development run swaps in the local http address.
pc.manifestUrl = `${origin}/servers.json`;
await fsp.writeFile(playerConfig, JSON.stringify(pc, null, 2));
log('sandbox', base);

const env = { ...process.env, REALM_USER_DATA: userData, REALM_DEV_PLAYER_CONFIG: playerConfig, REALM_DEV_NO_QUIT: '1' };

async function launch(extraArgs = [], extraEnv = {}) {
  const app = await electron.launch({ executablePath: electronBin, args: ['--no-sandbox', playerMain, ...extraArgs], env: { ...env, ...extraEnv } });
  const page = await app.firstWindow();
  await page.setViewportSize({ width: 1240, height: 780 }).catch(() => {});
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => document.fonts.ready);
  await app.evaluate(({ shell }) => {
    globalThis.__opened = [];
    globalThis.__ran = [];
    shell.openExternal = async (u) => {
      globalThis.__opened.push(u);
    };
    shell.openPath = async (p) => {
      globalThis.__ran.push(p);
      return '';
    };
  });
  return { app, page };
}

const opened = (app) => app.evaluate(() => globalThis.__opened.slice());
const ran = (app) => app.evaluate(() => globalThis.__ran.slice());
async function openedAfter(app, n, ms = 8000) {
  for (let t = 0; t < ms; t += 200) {
    const o = await opened(app);
    if (o.length > n) return o;
    await sleep(200);
  }
  return opened(app);
}

async function shot(page, name) {
  await sleep(700);
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

const text = (page, sel) => page.textContent(sel).then((t) => (t || '').replace(/\s+/g, ' ').trim());

let app;
let page;
const errors = [];
const watch = (p) => {
  p.on('pageerror', (e) => errors.push(e.message));
  p.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
};

// ======================================================= run 1: an older build (1.0.0)
try {
  ({ app, page } = await launch([], { REALM_DEV_VERSION: '1.0.0' }));
  watch(page);

  // ---- the bridge is the player bridge only
  const sec = await page.evaluate(() => ({ require: typeof require, process: typeof process, keys: Object.keys(window.realm || {}).sort() }));
  check('renderer has no require/process', sec.require === 'undefined' && sec.process === 'undefined');
  const owner = sec.keys.filter((k) => /^(server|setup|fleet|goPublic|publish|settings|firewall|prefs|setPrefs|feed)$|^(server|setup|fleet|publish|firewall)[A-Z]/.test(k));
  check('player bridge has no server, setup, firewall, publish or settings calls', owner.length === 0 && sec.keys.includes('news') && sec.keys.includes('installUpdate'), owner.join(',') || sec.keys.join(','));
  const badJoin = await page.evaluate(() => window.realm.join('../../etc').then(() => 'ran', (e) => e.message));
  check('join validates the server id', /invalid/.test(badJoin), badJoin);
  const notListed = await page.evaluate(() => window.realm.join('s9').then(() => 'ran', (e) => e.message));
  check('join refuses ids that are not in the signed list', /not in the signed list/.test(notListed), notListed);
  const badLink = await page.evaluate(() => window.realm.openNewsLink('nope').then((r) => r, (e) => e.message));
  check('news links open only for items in the signed feed', badLink === false, String(badLink));

  // ---- one screen
  check('no first-run intro, rail or settings', (await page.$$('.rail, #onboard, [data-go]')).length === 0);
  await page.waitForFunction(() => /Online/.test(document.querySelector('#sl-state').textContent), null, { timeout: 20000 });
  await page.waitForFunction(() => document.querySelectorAll('#news-list .pn-item').length >= 5, null, { timeout: 10000 });
  await page.waitForSelector('#rn-list .rn-card', { timeout: 10000 });
  const status = await text(page, '#status-line');
  check('status line: online, players, ping and the crown', /Online/.test(status) && /87 \/ 120 players/.test(status) && /Aldric Varrow/.test(status), status);
  check('Play joins the best server', /Join Realm I - Ashveil/.test(await text(page, '#play-sub')), await text(page, '#play-sub'));
  const opts = await page.$$eval('#server-select option', (os) => os.map((o) => o.textContent));
  check('server picker: best + every server with its status', opts.length === 4 && /^Best now: Realm I/.test(opts[0]) && /120\/120/.test(opts[2]), opts.join(' | '));
  const order = await page.$$eval('#news-list .pn-item .ni-title', (els) => els.map((e) => e.textContent));
  check('news: pinned first, then newest; scheduled item hidden', order[0] === 'The Hearth Charter is law' && order[1] === 'Aldric Varrow takes the Old Throne' && !order.some((t) => /SCHEDULED/.test(t)) && order.length === 5, order.join(' | '));
  const rn = await page.$$eval('#rn-list .rn-card .rn-title', (els) => els.map((e) => e.textContent));
  check("What's new in the realm lists the content changes", rn.length === 3 && rn[0] === 'The Grey Heron stands at the harbour gate', rn.join(' | '));
  check('news is signed', /Signed/.test(await text(page, '#news-meta')), await text(page, '#news-meta'));
  check('no update banner while there is no update', await page.isHidden('#update-bar'));
  check('footer: signed list', /Signed server list/.test(await text(page, '#trust')), await text(page, '#trust'));
  await shot(page, 'home');

  // ---- keyboard: the news tabs
  await page.focus('#tab-all');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  const onSeason = await page.evaluate(() => ({ sel: document.querySelector('[role="tab"][aria-selected="true"]').dataset.filter, focus: document.activeElement.id, n: document.querySelectorAll('#news-list .pn-item').length }));
  check('arrow keys move between news tabs and filter the list', onSeason.sel === 'season' && onSeason.focus === 'tab-season' && onSeason.n === 1, JSON.stringify(onSeason));
  await page.keyboard.press('ArrowRight');
  await shot(page, 'news-chronicle');
  await page.keyboard.press('Home');
  await page.keyboard.press('Tab');
  check('Tab from the tabs goes into the news list', await page.evaluate(() => document.activeElement.id === 'news-list'));
  const tabOrder = [];
  await page.focus('#play');
  for (let i = 0; i < 3; i++) {
    await page.keyboard.press('Tab');
    tabOrder.push(await page.evaluate(() => document.activeElement.id || document.activeElement.className));
  }
  check('Tab from Play reaches the server picker and the copy button', tabOrder[0] === 'server-select' && tabOrder[1] === 'copy-address', tabOrder.join(','));

  // ---- the news link opens only the signed https address
  await page.click('#news-list [data-news="charter"]');
  const afterLink = await openedAfter(app, 0, 3000);
  check('Read more opens the https link from the signed feed', afterLink[0] === 'https://example.org/realm/rules', afterLink.join(' '));

  // ---- tampered and rolled-back feeds are refused, the saved copy stays
  routes['/news.json'] = () => tamperedNews;
  const n1 = await page.evaluate(() => window.realm.news(true));
  check('tampered news refused, saved copy shown', n1.source === 'cache' && /does not match/.test(n1.reason) && n1.items[0].title === 'The Hearth Charter is law', `${n1.source}: ${n1.reason}`);
  routes['/news.json'] = () => JSON.stringify(NW.signNews(newsPayload(1), kp.privateKeyPem));
  const n2 = await page.evaluate(() => window.realm.news(true));
  check('older (rolled-back) news refused', n2.source === 'cache' && /rollback/.test(n2.reason), n2.reason);
  routes['/news.json'] = () => JSON.stringify(NW.signNews(newsPayload(5), other.privateKeyPem));
  const n3 = await page.evaluate(() => window.realm.news(true));
  check('news signed with another key refused', n3.source === 'cache' && /does not match/.test(n3.reason), n3.reason);
  routes['/news.json'] = () => goodNews;

  // ---- tampered and rolled-back server lists are refused
  routes['/servers.json'] = () => tamperedList;
  const l1 = await page.evaluate(() => window.realm.servers(true));
  check('tampered server list refused, last good list kept', l1.source === 'cache' && /signature does not match/.test(l1.reason) && !JSON.stringify(l1.servers).includes('198.51.100.66'), l1.reason);
  routes['/servers.json'] = () => JSON.stringify(PB.signManifest({ ...manifest, seq: 2 }, kp.privateKeyPem));
  const l2 = await page.evaluate(() => window.realm.servers(true));
  check('older (rolled-back) server list refused', /rollback/.test(l2.reason || ''), l2.reason);
  routes['/servers.json'] = () => goodList;
  await page.evaluate(() => window.realm.servers(true));

  // ---- deep link from a second launch: confirmation, never an automatic launch
  const nBefore = (await opened(app)).length;
  await new Promise((resolve) => {
    const child = spawn(electronBin, ['--no-sandbox', playerMain, 'realm://join/s1'], { env, stdio: 'ignore' });
    child.on('exit', resolve);
    setTimeout(resolve, 8000);
  });
  await page.waitForSelector('#modal:not([hidden])', { timeout: 10000 });
  check('realm://join/s1 asks before joining', /Join Realm I - Ashveil\?/.test(await page.textContent('#modal-title')));
  check('nothing launched before the player confirms', (await opened(app)).length === nBefore);
  await shot(page, 'deeplink');
  await page.click('#modal-ok');
  const o1 = await openedAfter(app, nBefore);
  check('JOIN opens the quick-join Steam URL for that server', o1[o1.length - 1] === 'steam://run/344760//-ip%20127.0.0.1%20-port%2047350/', o1[o1.length - 1]);
  await page.waitForFunction(() => /connecting|Steam is starting/.test(document.querySelector('#jf-status').textContent), null, { timeout: 8000 }).catch(() => {});
  await shot(page, 'join');
  const coach = await coachPage(app);
  check('the always-on-top join card opens', !!coach);
  if (coach) {
    await coach.waitForLoadState('domcontentloaded');
    await sleep(500);
    await coach.screenshot({ path: path.join(imgDir, 'player-coach.png') });
    log('saved docs/img/player-coach.png');
    check('join card shows the address and port', /127\.0\.0\.1/.test(await coach.textContent('#host')) && (await coach.textContent('#port')) === '47350');
    await coach.click('#close');
  }
  await page.click('#jf-close');

  // ---- Play with a picked server, from the keyboard
  await page.selectOption('#server-select', 's2');
  check('picking a server changes Play', /Join Realm II - Corvane · you queue/.test(await text(page, '#play-sub')), await text(page, '#play-sub'));
  const nPlay = (await opened(app)).length;
  await page.focus('#play');
  await page.keyboard.press('Enter');
  const o2 = await openedAfter(app, nPlay);
  check('Enter on Play joins the picked server', o2[o2.length - 1] === 'steam://run/344760//-ip%20127.0.0.1%20-port%2047360/', o2[o2.length - 1]);
  const c2w = await coachPage(app);
  if (c2w) await c2w.close().catch(() => {});
  await page.keyboard.press('Escape');
  check('Escape closes the join panel', await page.isHidden('#joinflow'));
  await page.selectOption('#server-select', 'auto');

  // ---- updates: a forged manifest is ignored
  routes['/update.json'] = () => JSON.stringify(UP.signUpdate(UP.buildUpdate({ seq: 9, version: '9.0.0', url: `${origin}/Realm-Setup-9.0.0.exe`, size: 10, sha256: 'a'.repeat(64), allowLocalHttp: true }, { now: new Date(now - H) }), other.privateKeyPem, { allowLocalHttp: true }));
  const forged = await page.evaluate(() => window.realm.checkUpdate());
  check('update manifest signed with another key: nothing offered', forged.status === 'error' && !forged.version && /does not match/.test(forged.reason), forged.reason);
  check('no banner for a forged update', await page.isHidden('#update-bar'));

  // ---- updates: a real one
  routes['/update.json'] = () => update();
  await page.evaluate(() => window.realm.checkUpdate());
  await page.waitForSelector('#update-bar:not([hidden])', { timeout: 5000 });
  check('update banner: version and notes title', /Update available: Realm 1\.1\.0/.test(await text(page, '#update-title')) && /The Ember Update/.test(await text(page, '#update-sub')), await text(page, '#update-bar'));
  check('a normal update can be put off', await page.isVisible('#update-later'));
  await shot(page, 'update');
  await page.click('#update-notes');
  await page.waitForSelector('#notes:not([hidden])');
  check("What's in it shows the signed notes", (await page.$$('#notes-list li')).length === 4 && /Coming in Realm 1\.1\.0/.test(await text(page, '#notes-kicker')));
  await page.keyboard.press('Escape');
  check('Escape closes the notes and returns focus', (await page.isHidden('#notes')) && (await page.evaluate(() => document.activeElement.id)) === 'update-notes');

  // A tampered download (same size, other bytes) is refused and deleted.
  routes.installer = () => tamperedInstaller;
  await page.click('#update-go');
  await page.waitForFunction(() => document.querySelector('#update-bar').classList.contains('s-error'), null, { timeout: 15000 });
  check('tampered installer refused (SHA-256 differs) and never run', /SHA-256 differs/.test(await text(page, '#update-sub')) && (await ran(app)).length === 0, await text(page, '#update-sub'));
  const leftovers = fs.existsSync(path.join(userData, 'updates')) ? fs.readdirSync(path.join(userData, 'updates')) : [];
  check('nothing left on disk after a refused download', leftovers.length === 0, leftovers.join(','));
  await shot(page, 'update-refused');

  // The real installer: progress, then verified and ready.
  routes.installer = () => installer;
  paused = new Promise((r) => (release = r));
  await page.click('#update-go');
  await page.waitForFunction(() => document.querySelector('#update-bar').classList.contains('s-downloading') && parseInt(document.querySelector('#update-pct').textContent, 10) >= 40, null, { timeout: 15000 });
  check('download shows progress and can be cancelled', await page.isVisible('#update-cancel'));
  await shot(page, 'update-downloading');
  release();
  await page.waitForFunction(() => document.querySelector('#update-bar').classList.contains('s-ready'), null, { timeout: 20000 });
  check('verified installer is ready', /Restart and update/.test(await text(page, '#update-go')), await text(page, '#update-bar'));
  await shot(page, 'update-ready');
  const file = path.join(userData, 'updates', 'Realm-Setup-1.1.0.exe');
  check('installer saved under the profile with the signed hash', fs.existsSync(file) && crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex') === sha);
  await page.click('#update-go');
  await page.waitForFunction(() => document.querySelector('#update-bar').classList.contains('s-installing'), null, { timeout: 5000 });
  const r1 = await ran(app);
  check('Restart and update runs exactly the verified installer', r1.length === 1 && r1[0] === file, r1.join(' '));

  check('no renderer errors (run 1)', errors.length === 0, errors.join(' | '));
} finally {
  if (app) await app.close().catch(() => {});
}

// ======================================================= run 2: after the update (1.1.0), then a hotfix
try {
  errors.length = 0;
  routes['/update.json'] = () => null;
  ({ app, page } = await launch([], { REALM_DEV_VERSION: '1.1.0' }));
  watch(page);
  await page.waitForSelector('#notes:not([hidden])', { timeout: 10000 });
  check("What's new opens once after the update, with the signed notes", /Updated to Realm 1\.1\.0/.test(await text(page, '#notes-kicker')) && /The Ember Update/.test(await text(page, '#notes-title')) && (await page.$$('#notes-list li')).length === 4, await text(page, '#notes'));
  await page.waitForFunction(() => /Online/.test(document.querySelector('#sl-state').textContent), null, { timeout: 20000 }).catch(() => {});
  await shot(page, 'whats-new');
  await page.click('#notes-close');

  routes['/update.json'] = () => update({ seq: 3, version: '1.1.1', url: `${origin}/Realm-Setup-1.1.1.exe`, hotfix: true, notes: { title: 'Hotfix', items: ['Fixes joining when Steam is in offline mode.'] } });
  paused = new Promise((r) => (release = r));
  await page.evaluate(() => window.realm.checkUpdate());
  await page.waitForFunction(() => document.querySelector('#update-bar').classList.contains('urgent') && document.querySelector('#update-bar').classList.contains('s-downloading'), null, { timeout: 15000 });
  check('hotfix: urgent banner, downloads by itself, cannot be put off', /Urgent update: Realm 1\.1\.1/.test(await text(page, '#update-title')) && (await page.isHidden('#update-later')));
  await page.waitForFunction(() => parseInt(document.querySelector('#update-pct').textContent, 10) >= 40, null, { timeout: 15000 });
  await shot(page, 'hotfix');
  release();
  await page.waitForFunction(() => document.querySelector('#update-bar').classList.contains('s-ready'), null, { timeout: 20000 });
  check('hotfix never runs without a click', (await ran(app)).length === 0);
  check('older installers are cleaned up', !fs.existsSync(path.join(userData, 'updates', 'Realm-Setup-1.1.0.exe')) && fs.existsSync(path.join(userData, 'updates', 'Realm-Setup-1.1.1.exe')));
  check('no renderer errors (run 2)', errors.length === 0, errors.join(' | '));
} finally {
  if (app) await app.close().catch(() => {});
}

// ======================================================= run 3: offline (saved copies), then the game is not installed
try {
  errors.length = 0;
  ({ app, page } = await launch([], { REALM_DEV_VERSION: '1.1.0' }));
  watch(page);
  // Same version as last time: no What's new.
  await page.waitForFunction(() => document.querySelectorAll('#news-list .pn-item').length >= 5, null, { timeout: 10000 });
  check("What's new does not open again", await page.isHidden('#notes'));
} finally {
  if (app) await app.close().catch(() => {});
}
filesServer.close();
filesServer.closeAllConnections();
try {
  errors.length = 0;
  ({ app, page } = await launch([], { REALM_DEV_VERSION: '1.1.0', REALM_DEV_GAME_INSTALL: 'missing' }));
  watch(page);
  await page.waitForFunction(() => document.querySelectorAll('#news-list .pn-item').length >= 5, null, { timeout: 15000 });
  check('offline: news from the saved copy', /Saved copy/.test(await text(page, '#news-meta')), await text(page, '#news-meta'));
  check('offline: servers from the saved list', /Saved server list/.test(await text(page, '#trust')), await text(page, '#trust'));
  await page.waitForFunction(() => document.querySelector('#play').classList.contains('install'), null, { timeout: 10000 });
  check('missing game: Play becomes Install', (await text(page, '#play-label')) === 'Install');
  await page.waitForFunction(() => /Online/.test(document.querySelector('#sl-state').textContent), null, { timeout: 20000 }).catch(() => {});
  await shot(page, 'install');
  await page.click('#play');
  await sleep(600);
  const o = await opened(app);
  check('Install goes to steam://install/344760 and nothing else', o.length === 1 && o[0] === 'steam://install/344760', o.join(' '));
  check('no renderer errors (run 3)', errors.length === 0, errors.join(' | '));
} finally {
  if (app) await app.close().catch(() => {});
}

// ======================================================= run 4: a build with nothing published yet
try {
  errors.length = 0;
  const bare = path.join(base, 'bare-config.json');
  await fsp.writeFile(bare, JSON.stringify({ realmName: 'The Realm', tagline: 'Swear an oath. Claim a crown. Answer for it.', manifestUrl: '', publicKey: '', bundledManifest: null, links: {} }));
  ({ app, page } = await launch([], { REALM_USER_DATA: path.join(base, 'bare-profile'), REALM_DEV_PLAYER_CONFIG: bare }));
  watch(page);
  await page.waitForFunction(() => document.querySelector('#news-list .empty-state'), null, { timeout: 10000 });
  check('nothing published: an empty news panel that says why', /No news yet/.test(await text(page, '#news-list')), await text(page, '#news-list'));
  check('nothing published: the footer says the list is not signed', /No signed server list/.test(await text(page, '#trust')), await text(page, '#trust'));
  await page.waitForFunction(() => /Offline|answering/.test(document.querySelector('#status-line').textContent), null, { timeout: 15000 }).catch(() => {});
  check('nothing published: no update banner, no realm changes', (await page.isHidden('#update-bar')) && (await page.isHidden('#realm-new')));
  await shot(page, 'empty');
  check('no renderer errors (run 4)', errors.length === 0, errors.join(' | '));
} finally {
  if (app) await app.close().catch(() => {});
}

for (const c of closers) c();
const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
if (failed.length) process.exitCode = 1;
