// Walk-through of the live console and the Court in Realm Steward, with an imitation ROK.exe whose
// admin console speaks the game's socket protocol (test/fake-admin-console.js). Starts the server
// from the Servers screen, checks that Steward passed -cport and connected, runs commands, uses
// every Court control, stops the server through /shutdown, and saves docs/img/court-*.png.
//
//   xvfb-run -a node scripts/court-screens.mjs
//
// Needs the playwright package (local or global). The real game is NOT exercised.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const repo = path.resolve(launcher, '..');
const imgDir = path.join(repo, 'docs', 'img');

function loadPlaywright() {
  const require = createRequire(import.meta.url);
  try {
    return require('playwright');
  } catch {
    return require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
  }
}
const { _electron: electron } = loadPlaywright();

const FAKE_LIB = path.join(launcher, 'test', 'fake-admin-console.js').replace(/\\/g, '/');
const FAKE_ROK = `#!/usr/bin/env node
// Imitation ROK.exe for the Court walk-through: prints a few log lines, and when started with
// -cport opens an imitation of the game's admin console on that port. Records what it was given.
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
  say('Initialize engine version: 5.x (imitation for tests)');
  say('Server for 40 players started on port 7350.');
  say('Game has started.');
  if (i < 0) return;
  const port = Number(argv[i + 1]);
  const game = new FakeAdminConsole({
    cportRuleMs: 10000,
    players: [
      { name: 'Aldric Thornvale', id: '76561190000000001' },
      { name: 'Wren', id: '76561190000000002' },
      { name: 'Odo the Tall', id: '76561190000000003' },
      { name: 'Ysolde Corvane', id: '76561190000000004' },
      { name: 'Petra Halloran', id: '76561190000000005' }
    ],
    bans: [{ name: 'Grimsby the Cutpurse', id: '76561190000000066' }]
  });
  game.on('received', (r) => note({ received: r.text, type: r.type }));
  game.on('shutdown', (why) => {
    note({ shutdown: why });
    say('Server shut down: ' + why);
    setTimeout(() => process.exit(0), 200);
  });
  await game.listen(port);
  game.logLine('I', 'Admin console enabled.');
  note({ listening: port });
  const chat = [
    ['C', 'Wren: does anyone sell iron near the crossroads?'],
    ['I', 'Authentication verified for Ysolde Corvane (76561190000000004).'],
    ['C', 'Aldric Thornvale: By order of the crown, the market opens at dusk.'],
    ['C', 'Odo the Tall: long live the king'],
    ['W', 'Player Odo the Tall is sending too many packets.'],
    ['C', 'Ysolde Corvane: House Corvane remembers.'],
    ['C', 'Petra Halloran: coin for the council, as agreed']
  ];
  let k = 0;
  setInterval(() => {
    const [lv, text] = chat[k++ % chat.length];
    game.logLine(lv, text);
  }, 700);
}, 1500);
setInterval(() => {}, 1000);
`;

const CFG = ["# -- Server --", "isPrivate = 'True'", "serverName = 'Realm - Kingslanding Test'", "maxPlayers = '120'", "bindIP = '127.0.0.1'", "portNumber = '7350'", "pingPort = '7350'", "steamAuthPort = '27015'", "restartTime = '0'", "timeBetweenPlayerJoin = '10'", "enableCommands = 'True'", ""].join('\r\n');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...a) => console.log('[court]', ...a);
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

const base = await fsp.mkdtemp(path.join(os.tmpdir(), 'realm-court-'));
const steamServer = path.join(base, 'SteamLibrary', 'steamapps', 'common', 'Reign Of Kings Dedicated Server');
const testRoot = path.join(base, 'RealmTest', 'server');
const userData = path.join(base, 'profile');
await write(path.join(steamServer, 'ROK.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, 'ROK.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, 'Server.exe'), FAKE_ROK, 0o755);
await write(path.join(testRoot, '.realm-test-copy'), 'Realm test copy\n');
await write(path.join(testRoot, 'Configuration', 'ServerSettings.cfg'), CFG);
await write(path.join(testRoot, 'Configuration', 'ConsoleSettings.cfg'), "enableRCon = 'False'\r\nrConPort = '27016'\r\n");
await write(path.join(testRoot, 'ROK_Data', 'Managed', 'Oxide.Core.dll'), 'stub');
await write(path.join(testRoot, 'oxide', 'plugins', 'RealmCourt.cs'), fs.readFileSync(path.join(repo, 'plugins', 'RealmCourt.cs')));
await fsp.mkdir(path.join(testRoot, 'Logs'), { recursive: true });
await write(path.join(userData, 'realm-settings.json'), JSON.stringify({ version: 1, testRoot, serverExe: 'ROK', setupComplete: true, instances: [] }, null, 2));
// Earlier rolls so the log is not empty on first view.
const earlier = [
  { at: new Date(Date.now() - 3 * 86400e3).toISOString(), server: 's1', action: 'ban', target: 'Grimsby the Cutpurse', reason: 'stole from the royal stores', command: '/ban "Grimsby the Cutpurse" stole from the royal stores', ok: true, result: 'Grimsby the Cutpurse has been banned.', via: 'console' },
  { at: new Date(Date.now() - 26 * 3600e3).toISOString(), server: 's1', action: 'notice', target: null, reason: 'The tourney begins at noon', command: '/notice The tourney begins at noon', ok: true, result: '', via: 'console' }
];
await write(path.join(userData, 'court', 'court-log.jsonl'), earlier.map((e) => JSON.stringify(e)).join('\n') + '\n');
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
page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
await page.setViewportSize({ width: 1320, height: 840 }).catch(() => {});
await page.waitForLoadState('domcontentloaded');
await page.evaluate(() => document.fonts.ready);

async function shot(name) {
  await sleep(800);
  await page.evaluate(() => document.getElementById('toasts').replaceChildren());
  const file = path.join(imgDir, `court-${name}.png`);
  await page.screenshot({ path: file });
  log('saved', path.relative(repo, file));
}
async function go(view) {
  await page.click(`.rail-btn[data-go="${view}"]`);
  await sleep(600);
}

try {
  check('wizard stays closed (setup seeded)', await page.isHidden('#wizard').catch(() => true));
  await go('court');
  await page.waitForSelector('#court-root .court-grid', { timeout: 15000 });
  const banner = await page.textContent('#court-root .court-banner');
  check('Court explains what to do while the server is stopped', /Start this server/.test(banner), banner.trim());
  await shot('offline');

  // ---- start from the Servers screen
  await go('server');
  await page.click('#srv-start');
  await page.waitForFunction(() => /Admin console connected/.test(document.querySelector('#console').textContent), null, { timeout: 30000 });
  const argv = (records().find((r) => r.argv) || {}).argv || [];
  check('ROK.exe started with -cport 11000', argv.includes('-cport') && argv[argv.indexOf('-cport') + 1] === '11000', argv.join(' '));
  check('-logFile kept', argv.includes('-logFile'));
  await page.fill('#console-cmd', 'list');
  await page.press('#console-cmd', 'Enter');
  await page.waitForFunction(() => /Aldric Thornvale, Wren/.test(document.querySelector('#console').textContent), null, { timeout: 10000 });
  check('a typed command goes over the console and its answer is shown', records().some((r) => r.received === '/list'));
  await page.fill('#console-cmd', 'banlist');
  await page.press('#console-cmd', 'Enter');
  await sleep(500);
  await page.evaluate(() => {
    const c = document.querySelector('#console');
    c.scrollTop = c.scrollHeight;
  });
  await shot('console');

  // ---- the Court in session
  await go('court');
  await page.waitForFunction(() => document.querySelectorAll('#court-root .court-player').length === 5, null, { timeout: 15000 });
  check('roster from RealmCourt shows names with Steam IDs', /76561190000000002/.test(await page.textContent('#court-root .court-players')));
  check('session pill says in session', /Court in session/.test(await page.textContent('#court-root .pill')));

  // proclamations
  await page.fill('#court-root .court-message', 'Hear ye: the market opens at dusk, by order of the crown.');
  await page.click('#court-root button:has-text("Notice")');
  await sleep(600);
  check('notice sent', records().some((r) => r.received === '/notice Hear ye: the market opens at dusk, by order of the crown.'));
  await page.fill('#court-root .court-message', 'Well met, travellers.');
  await page.click('#court-root button:has-text("Say in chat")');
  await sleep(600);
  check('chat sent without a slash', records().some((r) => r.received === 'Well met, travellers.'));

  // save
  await page.click('#court-root button:has-text("Save world")');
  await sleep(700);
  check('save sent as /realm.save', records().some((r) => r.received === '/realm.save'));

  // whitelist
  await page.click('#court-root button:has-text("Whitelist on")');
  await sleep(500);
  check('whitelist enable sent', records().some((r) => r.received === '/whitelist enable'));
  await page.click('#court-root button:has-text("Whitelist off")');
  await sleep(500);

  // kick Odo with a reason
  const odo = page.locator('#court-root .court-player', { hasText: 'Odo the Tall' });
  await odo.locator('button:has-text("Kick")').click();
  await page.fill('#court-root .court-form input', 'flooding the market square');
  await page.click('#court-root .court-form button:has-text("Kick Odo the Tall")');
  await page.waitForFunction(() => document.querySelectorAll('#court-root .court-player').length === 4, null, { timeout: 10000 });
  check('kick sent with the full name and the reason as one argument', records().some((r) => r.received === '/kick "Odo the Tall" "flooding the market square"'));

  // ban list
  await page.click('#court-root button:has-text("Read list")');
  await page.waitForFunction(() => /Grimsby the Cutpurse/.test(document.querySelector('#court-root .court-bans').textContent), null, { timeout: 10000 });
  check('ban list parsed', true);

  // plugin command copy
  await page.fill('#court-root .court-plug-arg >> nth=0', 'Corvane');
  await page.click('#court-root .court-plug button:has-text("Copy") >> nth=1');
  await sleep(300);
  const clip = await app.evaluate(({ clipboard }) => clipboard.readText());
  check('plugin admin command copied with its value', clip === '/house disband Corvane', clip);

  await page.evaluate(() => {
    const f = document.querySelector('#court-root .court-feed');
    f.scrollTop = f.scrollHeight;
  });
  await page.fill('#court-root .court-message', 'The tourney begins at noon tomorrow.');
  await shot('session');

  // ban form (not submitted for the picture), then submit
  const wren = page.locator('#court-root .court-player', { hasText: 'Wren' });
  await wren.locator('button:has-text("Ban")').click();
  await page.fill('#court-root .court-form input >> nth=0', 'selling stolen iron at the crossroads');
  await page.fill('#court-root .court-form input >> nth=1', '7');
  await shot('ban');
  await page.click('#court-root .court-form button:has-text("Banish Wren")');
  await page.waitForFunction(() => document.querySelectorAll('#court-root .court-player').length === 3, null, { timeout: 10000 });
  check('ban sent with days and reason', records().some((r) => r.received === '/ban Wren 7 selling stolen iron at the crossroads'));

  // the moderation log is on disk
  const logText = fs.readFileSync(path.join(userData, 'court', 'court-log.jsonl'), 'utf8');
  check('moderation log written to disk', /"action":"kick"/.test(logText) && /"action":"ban","target":"Wren"/.test(logText));
  check('court rolls show the kick', /kick/i.test(await page.textContent('#court-root .court-rolls')));

  // ---- stop: /shutdown over the console
  await go('server');
  await page.click('#srv-stop');
  await page.waitForFunction(() => document.querySelector('#srv-pill').textContent === 'Stopped', null, { timeout: 30000 });
  check('Stop went through /shutdown', records().some((r) => r.received === '/shutdown') && records().some((r) => r.shutdown === '/shutdown'));
  check('no renderer errors', errors.length === 0, errors.join(' | '));
} catch (e) {
  log('ERROR', e.stack || e.message);
  checks.push({ name: 'walk-through finished', ok: false });
  await page.screenshot({ path: path.join(base, 'court-failure.png') }).catch(() => {});
  log('failure screenshot', path.join(base, 'court-failure.png'));
} finally {
  await app.close().catch(() => {});
}

const failed = checks.filter((c) => !c.ok);
log(`${checks.length - failed.length}/${checks.length} checks passed`);
process.exit(failed.length ? 1 : 0);
