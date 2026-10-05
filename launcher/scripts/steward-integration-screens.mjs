// Walk-through of the screens that bring the server plugins into Realm Steward: Realm features, the Sentinel,
// Publish > News and Publish > Player update, and the data files Update plugins deploys. Uses an imitation
// ROK.exe whose admin console speaks the game's socket protocol (test/fake-admin-console.js), real plugin
// files, a RealmSentinel feed and evidence log in the shape the plugin writes, and saves
// docs/img/steward-{features,features-live,sentinel,sentinel-kick,court-commands,publish-news,publish-update}.png.
//
//   xvfb-run -a node scripts/steward-integration-screens.mjs
//
// Needs the playwright package (local or global). The real game and real Oxide are NOT exercised.
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
const require = createRequire(import.meta.url);
const N = require(path.join(launcher, 'lib', 'news.js'));
const U = require(path.join(launcher, 'lib', 'updater.js'));
const FT = require(path.join(launcher, 'lib', 'features.js'));

function loadPlaywright() {
  try {
    return require('playwright');
  } catch {
    return require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
  }
}
const { _electron: electron } = loadPlaywright();

const FAKE_LIB = path.join(launcher, 'test', 'fake-admin-console.js').replace(/\\/g, '/');
const FAKE_ROK = `#!/usr/bin/env node
// Imitation ROK.exe: when started with -cport opens an imitation of the game's admin console. Records what it got.
const fs = require('fs');
const path = require('path');
const { FakeAdminConsole } = require(${JSON.stringify(FAKE_LIB)});
const root = process.cwd();
const rec = path.join(root, 'fake-game.jsonl');
const note = (o) => fs.appendFileSync(rec, JSON.stringify({ at: Date.now(), ...o }) + '\\n');
const argv = process.argv.slice(2);
note({ argv });
const say = (s) => process.stdout.write(s + '\\r\\n');
say('Loading level CrownLand...');
const i = argv.indexOf('-cport');
setTimeout(async () => {
  say('Server for 40 players started on port 7350.');
  say('Game has started.');
  if (i < 0) return;
  const game = new FakeAdminConsole({
    cportRuleMs: 10000,
    players: [
      { name: 'Aldric Thornvale', id: '76561190000000001' },
      { name: 'Wren', id: '76561190000000002' },
      { name: 'Ysolde Corvane', id: '76561190000000004' },
      { name: 'Grimsby the Cutpurse', id: '76561190000000009' }
    ]
  });
  game.on('received', (r) => note({ received: r.text }));
  game.on('shutdown', (why) => {
    note({ shutdown: why });
    setTimeout(() => process.exit(0), 200);
  });
  await game.listen(Number(argv[i + 1]));
  game.logLine('I', 'Admin console enabled.');
}, 1200);
setInterval(() => {}, 1000);
`;

const CFG = ["# -- Server --", "isPrivate = 'True'", "serverName = 'Realm - Hearthmoor Test'", "maxPlayers = '120'", "bindIP = '127.0.0.1'", "portNumber = '7350'", "pingPort = '7350'", "steamAuthPort = '27015'", "restartTime = '0'", "timeBetweenPlayerJoin = '10'", "enableCommands = 'True'", ""].join('\r\n');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[steward-integration]', ...a);
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

// ---------- sandbox ----------
const base = await fsp.mkdtemp(path.join(os.tmpdir(), 'realm-integration-'));
const steamServer = path.join(base, 'SteamLibrary', 'steamapps', 'common', 'Reign Of Kings Dedicated Server');
const testRoot = path.join(base, 'RealmTest', 'server');
const ox = path.join(testRoot, 'oxide');
const userData = path.join(base, 'profile');
const publishDir = path.join(base, 'publish');
const releaseDir = path.join(base, 'release', '1.1.0');
await write(path.join(steamServer, 'ROK.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, 'ROK.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, 'Server.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, '.realm-test-copy'), 'Realm test copy\n');
await write(path.join(testRoot, 'Configuration', 'ServerSettings.cfg'), CFG);
await write(path.join(testRoot, 'Configuration', 'ConsoleSettings.cfg'), "enableRCon = 'False'\r\nrConPort = '27016'\r\n");
await write(path.join(testRoot, 'ROK_Data', 'Managed', 'Oxide.Core.dll'), 'stub');
await fsp.mkdir(path.join(testRoot, 'Logs'), { recursive: true });
// Every Realm plugin is deployed (as Update plugins would), so the screens see them installed.
for (const f of fs.readdirSync(path.join(repo, 'plugins')).filter((n) => n.endsWith('.cs'))) await write(path.join(ox, 'plugins', f), fs.readFileSync(path.join(repo, 'plugins', f)));

// Configs as the plugins write them: each catalogue switch at a plausible value, plus a few more fields.
function setPath(o, dotted, v) {
  const k = dotted.split('.');
  let p = o;
  for (const x of k.slice(0, -1)) p = p[x] = p[x] || {};
  p[k[k.length - 1]] = v;
}
const OFF = new Set(['RealmSentinel:Responses.AutoBan', 'RealmWarden:RaidHours.Enabled', 'RealmWarden:Chat.FilterWords', 'RealmSeasons:AutoStartNextSeason', 'RealmEvents:EnableTruce']);
for (const e of FT.CATALOGUE) {
  if (e.plugin === 'RealmStats') continue; // no config yet: shows the "start the server once" state
  const o = {};
  for (const s of e.switches) setPath(o, s.path, s.kind === 'enum' ? s.options[0] : !OFF.has(`${e.plugin}:${s.path}`));
  setPath(o, 'SaveIntervalSeconds', 120);
  if (e.plugin === 'RealmSentinel') {
    setPath(o, 'General.ExemptIds', [76561198000000123]);
    setPath(o, 'Responses.AlertScore', 20.0);
    setPath(o, 'Responses.FreezeScore', 50.0);
    setPath(o, 'Responses.KickScore', 80.0);
    setPath(o, 'Responses.BanScore', 150.0);
    setPath(o, 'Movement.CheckAscent', true);
    setPath(o, 'Names.LearnStaff', true);
  }
  await write(path.join(ox, 'config', `${e.plugin}.json`), JSON.stringify(o, null, 2));
}

// The Chronicle, including the Ironbreaker's own types.
const events = JSON.parse(fs.readFileSync(path.join(repo, 'chronicle', 'sample-data', 'RealmChronicle.json'), 'utf8'));
const now = Date.now();
events.push({ id: events.length + 1, ts: '', type: 'blade_claimed', title: 'Brannoc takes up the Ironbreaker', detail: 'Brannoc of House Corvane slew Aldric and took the blade.', actors: ['Brannoc', 'Aldric'] });
events.forEach((e, k) => (e.ts = new Date(now - (events.length - k) * 41 * 60000).toISOString().replace(/\.\d{3}Z$/, 'Z')));
await write(path.join(ox, 'data', 'RealmChronicle.json'), JSON.stringify(events, null, 2));
const state = JSON.parse(fs.readFileSync(path.join(repo, 'chronicle', 'sample-data', 'RealmState.json'), 'utf8'));
state.updated = new Date().toISOString();
await write(path.join(ox, 'data', 'RealmState.json'), JSON.stringify(state, null, 2));

// RealmSentinel's feed and evidence log (plugins/docs/RealmSentinel.md, "The Steward feed").
const iso = (minAgo) => new Date(now - minAgo * 60000).toISOString().replace(/\.\d{3}Z$/, 'Z');
const feed = {
  Version: 1, Generated: iso(0.2), Mode: 'watch', DataDamaged: false, Online: 4, Frozen: 0,
  Alerts: [
    { Id: 14, Time: iso(2), Kind: 'item_jump', PlayerId: '76561190000000009', PlayerName: 'Grimsby the Cutpurse', Score: 84.5, Response: 'would kick', Detail: '+2400 Iron Ingot in one 10 s sample with no container, craft or trade' },
    { Id: 13, Time: iso(6), Kind: 'speed', PlayerId: '76561190000000009', PlayerName: 'Grimsby the Cutpurse', Score: 54.5, Response: 'would freeze', Detail: '23.4 m/s for 3.0 s (limit 11.0 with ping credit)' },
    { Id: 12, Time: iso(19), Kind: 'reach', PlayerId: '76561190000000004', PlayerName: 'Ysolde Corvane', Score: 22.0, Response: 'alert', Detail: 'melee hit at 9.1 m (limit 7.5 + 1.5 slack)' },
    { Id: 11, Time: iso(47), Kind: 'chat_flood', PlayerId: '76561190000000002', PlayerName: 'Wren', Score: 20.4, Response: 'alert', Detail: '14 messages in 5 s' },
    { Id: 10, Time: iso(190), Kind: 'impersonation', PlayerId: '76561190000000044', PlayerName: 'Steward Aldric', Score: 40.0, Response: 'alert', Detail: 'name matches the staff name "Steward Aldrik"' }
  ],
  Suspects: [
    { PlayerId: '76561190000000009', PlayerName: 'Grimsby the Cutpurse', Score: 84.5, PeakScore: 84.5, Online: true, Frozen: false, LastSeen: iso(1), Counts: { item_jump: 1, speed: 4 } },
    { PlayerId: '76561190000000044', PlayerName: 'Steward Aldric', Score: 31.0, PeakScore: 40.0, Online: false, Frozen: false, LastSeen: iso(185), Counts: { impersonation: 1 } },
    { PlayerId: '76561190000000004', PlayerName: 'Ysolde Corvane', Score: 18.2, PeakScore: 22.0, Online: true, Frozen: false, LastSeen: iso(3), Counts: { reach: 2 } },
    { PlayerId: '76561190000000002', PlayerName: 'Wren', Score: 6.1, PeakScore: 20.4, Online: true, Frozen: false, LastSeen: iso(1), Counts: { chat_flood: 1 } }
  ]
};
const feedFile = path.join(ox, 'data', 'RealmSentinelFeed.json');
await write(feedFile, JSON.stringify(feed, null, 2));
const day = new Date().toISOString().slice(0, 10);
const evLines = [
  `${iso(31)} #201 speed Grimsby the Cutpurse (76561190000000009) +8.0 -> 8.0 at 1180.4,62.1,-311.9 ping 140 ms: 15.2 m/s for 1.0 s (limit 11.0)`,
  `${iso(12)} #214 speed Grimsby the Cutpurse (76561190000000009) +12.0 -> 30.5 at 1214.0,61.8,-290.2 ping 132 ms: 19.8 m/s for 2.0 s`,
  `${iso(19)} #219 reach Ysolde Corvane (76561190000000004) +10.0 -> 22.0 at 880.1,40.0,120.6 ping 60 ms: melee hit at 9.1 m`,
  `${iso(6)} #233 speed Grimsby the Cutpurse (76561190000000009) +24.0 -> 54.5 at 1302.7,63.0,-244.8 ping 128 ms: 23.4 m/s for 3.0 s (limit 11.0 with ping credit)`,
  `${iso(2)} #240 item_jump Grimsby the Cutpurse (76561190000000009) +30.0 -> 84.5 at 1310.2,63.1,-240.0 ping 131 ms: +2400 Iron Ingot in one 10 s sample with no container, craft or trade`
];
await write(path.join(ox, 'logs', 'RealmSentinel', `realmsentinel_evidence-${day}.txt`), evLines.join('\r\n') + '\r\n');

// A player installer as scripts/build-release.mjs leaves it.
const setupBytes = crypto.randomBytes(256 * 1024);
await write(path.join(releaseDir, 'Realm-Setup-1.1.0.exe'), setupBytes);
await write(path.join(releaseDir, 'SHA256SUMS.txt'), `${crypto.createHash('sha256').update(setupBytes).digest('hex')}  Realm-Setup-1.1.0.exe\n`);

await write(path.join(userData, 'realm-settings.json'), JSON.stringify({ version: 1, testRoot, serverExe: 'ROK', setupComplete: true, instances: [], publish: { outDir: publishDir, manifestUrl: 'https://realm.example.org/servers.json' } }, null, 2));
await fsp.mkdir(imgDir, { recursive: true });
log('sandbox', base);

const records = () => {
  try {
    return fs.readFileSync(path.join(testRoot, 'fake-game.jsonl'), 'utf8').trim().split('\n').filter(Boolean).map((l) => JSON.parse(l));
  } catch {
    return [];
  }
};

const app = await electron.launch({
  executablePath: path.join(launcher, 'node_modules', 'electron', 'dist', 'electron'),
  args: ['--no-sandbox', launcher],
  env: { ...process.env, REALM_USER_DATA: userData, REALM_DEV_STEAM_SERVER: steamServer }
});
const page = await app.firstWindow();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('console', (m) => m.type() === 'error' && !/ERR_CONNECTION_REFUSED|net::/.test(m.text()) && errors.push(m.text()));
await page.setViewportSize({ width: 1320, height: 840 }).catch(() => {});
await page.waitForLoadState('domcontentloaded');
await page.evaluate(() => document.fonts.ready);

async function shot(name) {
  await sleep(700);
  await page.evaluate(() => document.getElementById('toasts').replaceChildren());
  const file = path.join(imgDir, `steward-${name}.png`);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}
async function go(view) {
  await page.click(`.rail-btn[data-go="${view}"]`);
  await sleep(600);
}
async function toastText() {
  return page.evaluate(() => [...document.querySelectorAll('#toasts .toast')].map((t) => t.textContent).join(' | '));
}
const readCfg = (p) => JSON.parse(fs.readFileSync(path.join(ox, 'config', `${p}.json`), 'utf8'));

try {
  check('wizard stays closed (setup seeded)', await page.isHidden('#wizard').catch(() => true));

  // ---- Realm features, server stopped
  await go('features');
  await page.waitForSelector('#features-root .ft-card', { timeout: 15000 });
  const cards = await page.locator('#features-root .ft-card').count();
  check('Realm features shows a card for every plugin with a config', cards === FT.CATALOGUE.length, String(cards));
  check('the stopped server is explained', /applies when the server starts/.test(await page.textContent('#features-root .ft-note')));
  check('RealmStats without a config says to start once', /no config yet/.test(await page.textContent('#features-root .ft-card:has-text("Statistics")')));
  check('data chips offer the undeployed data', (await page.locator('#features-root .ft-datachip.stale').count()) === 3);
  const wagers = page.locator('#features-root .ft-card:has-text("The Arena") .ft-row:has-text("Stakes in marks") .ft-switch');
  await wagers.click();
  await page.waitForFunction(() => /Stakes in marks off/.test([...document.querySelectorAll('#toasts .toast')].map((t) => t.textContent).join(' ')), null, { timeout: 8000 });
  check('a switch writes the config', readCfg('RealmArena').Wagers.Enabled === false);
  const backups = fs.readdirSync(path.join(testRoot, '_realm-backups')).filter((n) => n.startsWith('config-'));
  check('the old config is backed up first', backups.length === 1 && fs.existsSync(path.join(testRoot, '_realm-backups', backups[0], 'RealmArena.json')));
  await page.waitForFunction(() => {
    const c = [...document.querySelectorAll('#features-root .ft-card')].find((x) => /The Arena/.test(x.textContent));
    const r = c && [...c.querySelectorAll('.ft-row')].find((x) => /Stakes in marks/.test(x.textContent));
    return r && r.querySelector('.ft-switch').getAttribute('aria-checked') === 'false';
  }, null, { timeout: 8000 });
  check('the switch shows off', true);
  // The master switch dims the rest.
  await page.click('#features-root .ft-card:has-text("Painted signs") .ft-row.master .ft-switch');
  await page.waitForFunction(() => document.querySelector('#features-root .ft-card[class*="ft-card"]') && [...document.querySelectorAll('#features-root .ft-card')].some((c) => /Painted signs/.test(c.textContent) && c.querySelector('.ft-row.dim')), null, { timeout: 8000 });
  check('turning a plugin off dims its other switches', true);
  await page.click('#features-root .ft-card:has-text("Painted signs") .ft-row.master .ft-switch');
  await sleep(600);
  // Danger asks first.
  await page.click('#features-root .ft-card:has-text("The Sentinel") .ft-row:has-text("Ban by itself") .ft-switch');
  await page.waitForSelector('.sn-modal', { timeout: 5000 });
  check('a dangerous switch asks first', /Off for 1.0/.test(await page.textContent('.sn-modal')));
  await page.click('.sn-modal button:has-text("Cancel")');
  await sleep(300);
  check('cancel leaves the config alone', readCfg('RealmSentinel').Responses.AutoBan === false);
  await page.evaluate(() => document.querySelector('#features-root .ft-body').scrollTo(0, 0));
  await shot('features');

  // ---- Update plugins deploys the data files (STW-1)
  await page.click('#features-root .ft-data button:has-text("Update plugins")');
  await page.waitForFunction(() => !document.querySelector('#features-root .ft-datachip.stale'), null, { timeout: 15000 });
  const sculpt = fs.readdirSync(path.join(ox, 'data', 'RealmSculptor')).filter((n) => n.endsWith('.json'));
  check('Update plugins copied the sculptures', sculpt.length === fs.readdirSync(path.join(repo, 'art', 'sculptures')).filter((n) => n.endsWith('.json')).length, String(sculpt.length));
  check('Update plugins copied the sign art bundle', fs.statSync(path.join(ox, 'data', 'RealmPainterArt.json')).size === fs.statSync(path.join(repo, 'art', 'paintings', 'RealmPainterArt.json')).size);
  check('Update plugins copied the quest content', fs.readdirSync(path.join(ox, 'data', 'RealmQuests')).length === fs.readdirSync(path.join(repo, 'plugins', 'docs', 'RealmQuests', 'content')).length);

  // ---- start the server; the live console connects
  await go('server');
  await page.click('#srv-start');
  await page.waitForFunction(() => /Admin console connected/.test(document.querySelector('#console').textContent), null, { timeout: 30000 });
  check('server started with the live console', true);

  // ---- Realm features, live: a change reloads the plugin over the console
  await go('features');
  await page.waitForFunction(() => /Live/.test(document.querySelector('#features-root .pill').textContent), null, { timeout: 10000 });
  await page.click('#features-root .ft-card:has-text("Realm events") .ft-row:has-text("Truce of the Realm") .ft-switch');
  await page.waitForFunction(() => /reloaded/.test([...document.querySelectorAll('#toasts .toast')].map((t) => t.textContent).join(' ')), null, { timeout: 10000 });
  check('a live change sends /oxide.reload for that plugin', records().some((r) => r.received === '/oxide.reload RealmEvents'));
  check('the change is in the Court rolls', /"action":"feature","target":"RealmEvents"/.test(fs.readFileSync(path.join(userData, 'court', 'court-log.jsonl'), 'utf8')));
  await page.click('#features-root .ft-card:has-text("The Sentinel") .ft-more');
  await sleep(300);
  await page.locator('#features-root .ft-card:has-text("The Sentinel")').scrollIntoViewIfNeeded();
  await shot('features-live');

  // ---- the Sentinel
  await page.waitForFunction(() => !document.getElementById('rail-sentinel-badge').hidden, null, { timeout: 12000 });
  check('the rail badge counts unseen alerts', (await page.textContent('#rail-sentinel-badge')) === '5');
  await go('sentinel');
  await page.waitForSelector('#sentinel-root .sn-alert', { timeout: 10000 });
  check('five alerts, newest first', (await page.locator('#sentinel-root .sn-alert').count()) === 5 && /Grimsby/.test(await page.textContent('#sentinel-root .sn-alert >> nth=0')));
  check('watch mode shown', /Watch mode/.test(await page.textContent('#sentinel-root .pill')));
  check('suspects ranked by score', /Grimsby/.test(await page.textContent('#sentinel-root .sn-suspect >> nth=0')));
  await page.click('#sentinel-root .sn-suspect >> nth=0');
  await page.waitForSelector('#sentinel-root .sn-ev', { timeout: 8000 });
  check('evidence lines from the daily log, newest first', (await page.locator('#sentinel-root .sn-ev').count()) === 4 && /2400 Iron/.test(await page.textContent('#sentinel-root .sn-ev >> nth=0')));
  await page.click('#sentinel-root .sn-evacts button:has-text("Freeze 15 min")');
  await sleep(300);
  const clip = await app.evaluate(({ clipboard }) => clipboard.readText());
  check('the in-game /sentinel command is copied with the name quoted', clip === '/sentinel freeze "Grimsby the Cutpurse" 15', clip);
  // A new alert arrives while the screen is open.
  feed.Alerts.unshift({ Id: 15, Time: iso(0), Kind: 'fly', PlayerId: '76561190000000004', PlayerName: 'Ysolde Corvane', Score: 41.0, Response: 'alert', Detail: 'rose 36 m in 10 s without a ladder or a fall' });
  feed.Generated = iso(0);
  await write(feedFile + '.tmp', JSON.stringify(feed, null, 2));
  await fsp.rename(feedFile + '.tmp', feedFile);
  await page.waitForFunction(() => /Ysolde Corvane - Flying/.test([...document.querySelectorAll('#toasts .toast')].map((t) => t.textContent).join(' ')), null, { timeout: 12000 });
  check('a new alert raises a toast', true);
  await page.waitForFunction(() => document.querySelectorAll('#sentinel-root .sn-alert').length === 6, null, { timeout: 8000 });
  check('the new alert is listed first', /Ysolde/.test(await page.textContent('#sentinel-root .sn-alert >> nth=0')));
  await shot('sentinel');
  await page.click('#sentinel-root .sn-evacts button:has-text("Kick")');
  await page.waitForSelector('.sn-modal', { timeout: 5000 });
  await shot('sentinel-kick');
  await page.click('.sn-modal button:has-text("Kick")');
  await page.waitForFunction(() => /was kicked/.test([...document.querySelectorAll('#toasts .toast')].map((t) => t.textContent).join(' ')), null, { timeout: 10000 });
  check('Kick goes over the console with the Sentinel reason', records().some((r) => r.received === '/kick "Grimsby the Cutpurse" "Sentinel: item_jump"'));
  await page.click('#sentinel-root button:has-text("Mark all seen")');
  await page.waitForFunction(() => document.getElementById('rail-sentinel-badge').hidden, null, { timeout: 8000 });
  check('Mark all seen clears the badge', true);
  await page.click('#sentinel-root button:has-text("Reload plugin")');
  await sleep(800);
  check('Reload plugin sends /oxide.reload RealmSentinel', records().some((r) => r.received === '/oxide.reload RealmSentinel'));

  // ---- Court: the in-game staff commands of every Realm plugin, one fold each
  await go('court');
  await page.waitForSelector('.court-plug-fold', { timeout: 10000 });
  const MOD = require(path.join(launcher, 'lib', 'moderation.js'));
  const pluginsWithCommands = new Set(MOD.PLUGIN_ADMIN.map((d) => d.plugin));
  check('one fold per plugin with staff commands', (await page.locator('.court-plug-fold').count()) === pluginsWithCommands.size, String(pluginsWithCommands.size));
  await page.evaluate(() => document.querySelectorAll('.court-plug-fold').forEach((f) => (f.open = false)));
  const arena = page.locator('.court-plug-fold:has(summary:has-text("RealmArena"))');
  await arena.locator('summary').click();
  await arena.locator('.court-plug:has-text("call a duel off") input').fill('#12');
  await arena.locator('.court-plug:has-text("call a duel off") button:has-text("Copy")').click();
  await sleep(300);
  check('a wave 3 staff command is filled and copied', (await app.evaluate(({ clipboard }) => clipboard.readText())) === '/arena admin void 12');
  await arena.scrollIntoViewIfNeeded();
  await shot('court-commands');

  // ---- Publish > News
  await go('publish');
  await page.click('#key-create');
  await page.waitForFunction(() => !document.getElementById('key-box').hidden, null, { timeout: 8000 });
  const publicKey = (await page.textContent('#key-public')).trim();
  await page.click('#pub-tab-news');
  await page.waitForSelector('#pub-news-root .pub-grid', { timeout: 8000 });
  check('the server-list button hides on the News tab', await page.isHidden('#pl-write'));
  const addItem = async (fill) => {
    await page.click('#pub-news-root button:has-text("New item")');
    await fill();
    await page.click('#pub-news-root .pub-form button:has-text("Add item")');
    await sleep(400);
  };
  await addItem(async () => {
    await page.fill('#pub-news-root .pub-form input[maxlength="100"]', 'The Hearth Charter is law');
    await page.fill('#pub-news-root .pub-form textarea', 'Read the Charter before you rebel. The council meets on Saturdays.');
    await page.check('#pub-news-root .pub-form input[type="checkbox"]');
  });
  await addItem(async () => {
    await page.selectOption('#pub-news-root .pub-form select >> nth=0', 'realm');
    await page.selectOption('#pub-news-root .pub-form select >> nth=1', 'sculpture');
    await page.fill('#pub-news-root .pub-form input[maxlength="100"]', 'The Grey Heron stands at the harbour gate');
    await page.fill('#pub-news-root .pub-form textarea', 'A new monument in the capital, raised from the realm\'s own stone.');
  });
  await page.click('#pub-news-root button:has-text("From the Chronicle")');
  await page.waitForSelector('#pub-news-root .pub-chron-row', { timeout: 10000 });
  await page.click('#pub-news-root .pub-chron-row:has-text("Ironbreaker")');
  await page.click('#pub-news-root .pub-form button:has-text("Add item")');
  await sleep(400);
  check('three news items in the draft', (await page.locator('#pub-news-root .pub-item').count()) === 3);
  await page.click('#pub-news-root button:has-text("Sign & write news.json")');
  await page.waitForSelector('#pub-news-root .pub-ok', { timeout: 10000 });
  const newsText = fs.readFileSync(path.join(publishDir, 'news.json'), 'utf8');
  const nv = N.verifyNews(newsText, publicKey);
  check('news.json verifies with the public key exactly as the player app checks it', nv.ok && nv.news.items.length === 3 && nv.news.seq === 1, nv.reason || '');
  check('the Chronicle highlight became a chronicle item', nv.ok && nv.news.items.some((i) => i.type === 'chronicle' && /Ironbreaker/.test(i.title)));
  await page.click('#pub-news-root .pub-item-main >> nth=1');
  await sleep(300);
  await shot('publish-news');
  await page.click('#pub-news-root .pub-form button:has-text("Cancel")');

  // ---- Publish > Player update
  await page.click('#pub-tab-update');
  await page.waitForSelector('#pub-update-root .pub-grid', { timeout: 8000 });
  await app.evaluate(({ dialog }, p) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [p] });
  }, path.join(releaseDir, 'Realm-Setup-1.1.0.exe'));
  await page.click('#pub-update-root button:has-text("Choose installer")');
  await page.waitForFunction(() => /Matches SHA256SUMS/.test(document.querySelector('#pub-update-root .pub-inst').textContent), null, { timeout: 10000 });
  check('the installer is hashed and checked against SHA256SUMS.txt', true);
  check('the version comes from the file name', (await page.inputValue('#pub-update-root input[placeholder="1.1.0"]')) === '1.1.0');
  await page.fill('#pub-update-root input[type="url"]', 'https://github.com/realm-ostreval/realm/releases/download/v1.1.0/Realm-Setup-1.1.0.exe');
  await page.fill('#pub-update-root input[maxlength="80"]', 'The Ember Update');
  await page.fill('#pub-update-root textarea', 'News from the realm on the Play screen\nSigned updates that check every byte\nThe Ironbreaker in the Chronicle with its own mark');
  await page.click('#pub-update-root button:has-text("Sign & write update.json")');
  await page.waitForSelector('#pub-update-root .pub-ok', { timeout: 10000 });
  const uv = U.verifyUpdate(fs.readFileSync(path.join(publishDir, 'update.json'), 'utf8'), publicKey);
  check('update.json verifies, with the installer\'s size and SHA-256', uv.ok && uv.update.file.size === setupBytes.length && uv.update.file.sha256 === crypto.createHash('sha256').update(setupBytes).digest('hex'), uv.reason || '');
  check('the player app would accept the installer bytes', uv.ok && (await U.verifyInstaller(path.join(releaseDir, 'Realm-Setup-1.1.0.exe'), uv.update)));
  await shot('publish-update');
  await page.click('#pub-tab-list');
  check('back on the Server list tab the sign button returns', await page.isVisible('#pl-write'));

  // ---- stop
  await go('server');
  await page.click('#srv-stop');
  await page.waitForFunction(() => document.querySelector('#srv-pill').textContent === 'Stopped', null, { timeout: 30000 });
  check('no renderer errors', errors.length === 0, errors.join(' | '));
} catch (e) {
  log('ERROR', e.stack || e.message);
  checks.push({ name: 'walk-through finished', ok: false });
  await page.screenshot({ path: path.join(base, 'integration-failure.png') }).catch(() => {});
  log('failure screenshot', path.join(base, 'integration-failure.png'), 'toasts:', await toastText().catch(() => ''));
} finally {
  await app.close().catch(() => {});
}

const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
process.exit(failed.length ? 1 : 0);
