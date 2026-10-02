// Design-system checks for both apps, and the screens that need a staged situation: the pre-start
// "Stop it cleanly / Adopt it" dialog, the world choice, the Chronicle loading/empty/error states.
// Runs Realm Steward's page in headless Chromium with the browser bridge (renderer/mock-preload.js),
// so it needs no Electron and no server. Saves docs/img/design-system.png, steward-prestart.png,
// steward-prestart-blocked.png, steward-world.png and steward-home-offline.png.
//
//   node scripts/design-screens.mjs
//
// Checks: the art copies in renderer/assets match art/; renderer/heraldry.js matches the event map,
// the overlay's event tones, the palette and the renown titles; the CSS brand tokens equal
// art/palette.json; text tokens meet WCAG contrast on every iron surface; every asset the pages
// reference exists and loads; keyboard (skip link, dialog focus trap, arrows, Escape, focus return);
// reduced motion. Needs the playwright package (local or global) and its Chromium.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { startPreview } from './preview-server.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const repo = path.resolve(launcher, '..');
const renderer = path.join(launcher, 'renderer');
const imgDir = path.join(repo, 'docs', 'img');
const require = createRequire(import.meta.url);

function loadPlaywright() {
  try {
    return require('playwright');
  } catch {
    return require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
  }
}

let passed = 0;
let failed = 0;
const warnings = [];
const log = (...a) => console.log('[design]', ...a);
function check(name, ok, detail = '') {
  if (ok) passed++;
  else failed++;
  log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' - ' + detail : ''}`);
}
const read = (p) => fs.readFileSync(p, 'utf8');

// ---------------------------------------------------------------- static checks

// 1. Art copies are byte-identical to their sources.
const ASSET_SOURCES = [
  [/^logo\/(.+)$/, 'art/logo/$1'],
  [/^keyart\/(.+)$/, 'art/keyart/$1'],
  [/^icons\.svg$/, 'art/sprite/icons.svg'],
  [/^(sigils|shields|banners)\/(.+)$/, 'art/$1/$2'],
  [/^badges\/(.+)$/, 'art/badges/$1']
];
function walk(dir, base = '') {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const rel = base ? `${base}/${e.name}` : e.name;
    if (e.isDirectory()) out.push(...walk(path.join(dir, e.name), rel));
    else out.push(rel);
  }
  return out;
}
const assets = walk(path.join(renderer, 'assets')).filter((f) => f !== 'README.md');
let copies = 0;
for (const rel of assets) {
  const rule = ASSET_SOURCES.find(([re]) => re.test(rel));
  if (!rule) {
    check(`asset ${rel} has a source in art/`, false, 'add it to ASSET_SOURCES or remove it');
    continue;
  }
  const src = path.join(repo, rel.replace(rule[0], rule[1]));
  const same = fs.existsSync(src) && fs.readFileSync(src).equals(fs.readFileSync(path.join(renderer, 'assets', rel)));
  if (!same) check(`assets/${rel} equals ${path.relative(repo, src)}`, false, 'copy it again (renderer/assets/README.md)');
  else copies++;
}
check('every renderer/assets file is an exact copy of art/', copies === assets.length, `${copies}/${assets.length}`);
for (const h of ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin']) {
  for (const k of ['sigils', 'shields', 'banners']) if (!fs.existsSync(path.join(renderer, 'assets', k, h + '.svg'))) check(`assets/${k}/${h}.svg exists`, false);
}

// 2. Every assets/... path the pages, styles and scripts name exists.
const refs = new Set();
for (const f of fs.readdirSync(renderer).filter((x) => /\.(html|css|js)$/.test(x))) {
  for (const m of read(path.join(renderer, f)).matchAll(/assets\/[A-Za-z0-9_./-]+\.svg/g)) refs.add(m[0]);
}
const missingRefs = [...refs].filter((r) => !fs.existsSync(path.join(renderer, r)));
check('every assets/ path named in renderer/ exists', !missingRefs.length && refs.size > 3, missingRefs.join(', ') || `${refs.size} paths`);

// 3. Brand tokens in styles.css equal art/palette.json.
const palette = JSON.parse(read(path.join(repo, 'art', 'palette.json')));
const css = read(path.join(renderer, 'styles.css'));
const rootBlock = /:root\s*\{([\s\S]*?)\n\}/.exec(css)[1];
const tokenOf = (name) => (new RegExp(`${name}:\\s*(#[0-9a-fA-F]{6})`).exec(rootBlock) || [])[1];
const wrongTokens = palette.brand.filter((b) => b.token && (tokenOf(b.token) || '').toLowerCase() !== b.hex.toLowerCase()).map((b) => `${b.token} ${tokenOf(b.token)} != ${b.hex}`);
check('styles.css brand tokens equal art/palette.json', !wrongTokens.length, wrongTokens.join('; '));

// 4. heraldry.js tables against their sources (evaluated in a small stand-in window).
const sandbox = { window: { addEventListener() {} }, document: { addEventListener() {} } };
new Function('window', 'document', read(path.join(renderer, 'heraldry.js')))(sandbox.window, sandbox.document);
const ART = sandbox.window.RealmArt;
const eventMap = JSON.parse(read(path.join(repo, 'art', 'icons', 'event-map.json'))).icons;
const common = read(path.join(repo, 'chronicle', 'public', 'assets', 'common.js'));
const meta = {};
for (const m of common.matchAll(/^\s+(\w+):\s+\{ label: '((?:[^'\\]|\\.)*)',\s+icon: '\w+',\s+tone: '(\w+)'/gm)) meta[m[1]] = { label: m[2].replace(/\\'/g, "'"), tone: m[3] };
const evKeys = Object.keys(ART.EVENTS).sort();
check('heraldry.js has every Chronicle event type of art/icons/event-map.json', evKeys.join() === Object.keys(eventMap).sort().join(), `${evKeys.length} types`);
const evDiff = evKeys.filter((t) => ART.EVENTS[t][0] !== eventMap[t] || !meta[t] || ART.EVENTS[t][1] !== meta[t].label || ART.EVENTS[t][2] !== meta[t].tone);
check('event icons match the event map; labels and tones match the overlay', !evDiff.length, evDiff.join(', '));
const sprite = read(path.join(renderer, 'assets', 'icons.svg'));
const noSymbol = [...new Set([...Object.values(ART.EVENTS).map((e) => e[0]), 'house', 'oath', 'crown', 'rebellion', 'ransom', 'decree', 'claim', 'beacon-out'])].filter((n) => !sprite.includes(`id="realm-icon-${n}"`));
check('every icon the apps use is in the sprite', !noSymbol.length, noSymbol.join(', '));
const houseDiff = ART.HOUSES.filter((h) => {
  const p = palette.houses[h.id];
  return !p || p.name !== h.name || p.sigil !== h.sigil || p.words !== h.words || p.field !== h.field || p.fieldLight !== h.fieldLight || p.fieldDark !== h.fieldDark || p.metal !== h.metal;
});
check('heraldry.js houses equal art/palette.json', !houseDiff.length && ART.HOUSES.length === Object.keys(palette.houses).length, houseDiff.map((h) => h.id).join(', '));
const playerJs = read(path.join(renderer, 'player.js'));
const playerHouseDiff = Object.entries(palette.houses).filter(([id, p]) => !new RegExp(`id: '${id}', name: '${p.name}', sigil: '${p.sigil}', field: '${p.field}', metal: '${p.metal}', words: '${p.words.replace(/[.]/g, '\\.')}'`).test(playerJs));
check('player.js allegiance houses equal art/palette.json', !playerHouseDiff.length, playerHouseDiff.map(([id]) => id).join(', '));
const titles = JSON.parse(read(path.join(repo, 'art', 'src', 'titles.json'))).titles;
const titleDiff = titles.filter((t) => !ART.TITLES.some((x) => x.id === t.id && x.name === t.name && x.infamous === t.infamous && fs.existsSync(path.join(renderer, x.file))));
check('heraldry.js titles equal art/src/titles.json and each has its badge', !titleDiff.length && ART.TITLES.length === titles.length, titleDiff.map((t) => t.id).join(', '));
const dyes = (/const DYES = (\[[^\]]+\])/.exec(common) || [])[1];
check('house dyes equal the overlay (chronicle common.js DYES)', dyes && read(path.join(renderer, 'heraldry.js')).includes(`const DYES = ${dyes}`));
check('title badge matching: "X is named Kingslayer" -> kingslayer', (ART.titleFor('Wren is named Kingslayer') || {}).id === 'kingslayer');
check('season matching: "Season 2 begins" -> 2', ART.seasonOf({ title: 'Season 2 begins' }) === 2);
check('a player-founded house never gets a great house charge', ART.greatHouse('Thornvale') === null && ART.greatHouse('House Varrow').id === 'varrow');

// 5. Packaging: the player build lists its renderer files one by one (launcher/build/player.json).
const playerBuild = JSON.parse(read(path.join(launcher, 'build', 'player.json')));
const packs = (p) => playerBuild.files.some((f) => f === p || (f.endsWith('/**/*') && p.startsWith(f.slice(0, -4))));
const unpacked = ['renderer/heraldry.js', 'renderer/assets/icons.svg'].filter((p) => !packs(p));
if (unpacked.length) warnings.push(`launcher/build/player.json does not package ${unpacked.join(' or ')}: add "renderer/heraldry.js" and "renderer/assets/**/*" to its "files", or the installed player app shows no art (it still works).`);

// ---------------------------------------------------------------- browser checks

// WCAG 2 contrast from two #rrggbb colours.
function contrast(a, b) {
  const lum = (h) => {
    const c = [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16) / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  };
  const [x, y] = [lum(a), lum(b)];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

const { chromium } = loadPlaywright();
const port = 5181;
const server = await startPreview(port);
const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
const errors = [];
try {
  const page = await browser.newPage({ viewport: { width: 1320, height: 840 }, deviceScaleFactor: 1 });
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  page.on('requestfailed', (r) => errors.push('request failed: ' + r.url()));
  await page.goto(`http://127.0.0.1:${port}/index.html`);
  await page.waitForSelector('.event .ev-mark');
  await page.waitForTimeout(800);

  // tokens and contrast (computed values, so a bad var() chain shows up here)
  const tok = await page.evaluate(() => {
    const cs = getComputedStyle(document.documentElement);
    const v = (n) => cs.getPropertyValue(n).trim().toLowerCase();
    return Object.fromEntries(['--iron-900', '--iron-800', '--iron-700', '--text', '--text-2', '--text-muted', '--accent', '--accent-hi', '--ok', '--warn', '--bad', '--tone-blood', '--tone-moss', '--tone-iron'].map((n) => [n, v(n)]));
  });
  const resolve = (v) => (v.startsWith('var(') ? tok[/var\((--[\w-]+)\)/.exec(v)[1]] : v);
  for (const k of Object.keys(tok)) tok[k] = resolve(tok[k]);
  const low = [];
  for (const bg of ['--iron-900', '--iron-800', '--iron-700']) {
    for (const [fg, min] of [['--text', 7], ['--text-2', 7], ['--text-muted', 4.5], ['--accent', 4.5], ['--accent-hi', 4.5], ['--ok', 4.5], ['--warn', 4.5], ['--bad', 4.5], ['--tone-blood', 4.5], ['--tone-moss', 4.5], ['--tone-iron', 4.5]]) {
      const r = contrast(tok[fg], tok[bg]);
      if (r < min) low.push(`${fg} on ${bg} ${r.toFixed(2)} < ${min}`);
    }
  }
  check('text tokens meet WCAG AA (body text AAA) on iron 900, 800 and 700', !low.length, low.join('; '));

  // every image on the page loaded
  const broken = await page.evaluate(() => Array.from(document.images).filter((i) => !i.complete || !i.naturalWidth).map((i) => i.getAttribute('src')));
  check('every image on Home loads', !broken.length, broken.join(', '));
  const marks = await page.evaluate(() => Array.from(document.querySelectorAll('#events .event')).map((li) => li.querySelector('.ev-mark use, .ev-mark img') ? 1 : 0));
  check('every Chronicle event on Home shows its own icon', marks.length >= 5 && marks.every(Boolean), `${marks.length} events`);
  const shotHome = await page.evaluate(() => document.querySelector('.crest-sigil').naturalWidth > 0 && document.querySelector('.brand-mark').naturalWidth > 0 && document.querySelector('.keyart').naturalWidth > 0);
  check('emblem, title mark and key art render', shotHome);

  // keyboard: the skip link is the first stop
  await page.keyboard.press('Tab');
  check('Tab first lands on "Skip to content"', await page.evaluate(() => document.activeElement && document.activeElement.classList.contains('skip-link')));
  const skipVisible = await page.evaluate(() => getComputedStyle(document.activeElement).opacity === '1');
  check('the focused skip link is visible', skipVisible);

  // ---- pre-start: a leftover server that can be adopted or stopped
  const LEFTOVER = {
    running: false,
    survey: {
      clear: false,
      verdict: 'leftover',
      message: 'UDP 7350 (game), TCP 7350 (ping) are already in use. Found a server from this folder that Steward did not start (or lost track of): ROK.exe (pid 8124, G:\\RealmTest\\server\\ROK.exe) on UDP 7350, TCP 7350.',
      holders: [{ pid: 8124, name: 'ROK', path: 'G:\\RealmTest\\server\\ROK.exe', ports: ['UDP 7350', 'TCP 7350'], kind: 'leftover' }],
      busy: [{ proto: 'udp', port: 7350, what: 'game' }, { proto: 'tcp', port: 7350, what: 'ping' }],
      actions: {
        stop: { ok: true, port: 11000, why: 'Sends /shutdown to its admin console on 127.0.0.1:11000: the game saves the world and exits.' },
        adopt: { ok: true, pid: 8124, port: 11000, why: 'Steward connects to its admin console on 127.0.0.1:11000, follows its log and shows it as running. Nothing is restarted.' }
      }
    },
    world: { action: 'load', slot: 3, choices: [] },
    saveLocation: 'Saves/'
  };
  await page.evaluate((pre) => {
    window.__calls = { prestart: 0, adopt: 0, stopHolder: 0, start: 0 };
    window.__pre = pre;
    const s = window.realm.server;
    s.prestart = () => (window.__calls.prestart++, Promise.resolve(window.__pre));
    s.adopt = () => (window.__calls.adopt++, s.status());
    s.start = () => (window.__calls.start++, s.status());
    s.stopHolder = () => (window.__calls.stopHolder++, Promise.resolve({ stopped: true, relaunched: true, message: 'The server shut down, but UDP 7350 was taken again within 2 s: the game\'s Server.exe watchdog started it again. Close Server.exe first, then try again.' }));
  }, LEFTOVER);
  await page.click('.rail-btn[data-go="server"]');
  await page.waitForSelector('#srv-start:not([disabled])');
  await page.focus('#srv-start');
  await page.keyboard.press('Enter');
  await page.waitForSelector('#modal:not([hidden]) .choice');
  await page.waitForTimeout(450);
  const dlg = await page.evaluate(() => ({
    title: document.querySelector('#modal-title').textContent,
    choices: Array.from(document.querySelectorAll('#modal .choice')).map((c) => c.querySelector('.choice-label').textContent),
    checked: document.querySelector('#modal .choice input:checked').value,
    focus: document.activeElement && document.activeElement.value,
    ok: document.querySelector('#modal-ok').textContent,
    facts: document.querySelector('#modal .modal-facts') ? document.querySelector('#modal .modal-facts').textContent : ''
  }));
  check('pre-start dialog offers Adopt (recommended) and Stop cleanly', dlg.choices.length === 2 && /Adopt it\s*Recommended/.test(dlg.choices[0]) && /Stop it cleanly/.test(dlg.choices[1]), dlg.choices.join(' | '));
  check('Adopt is chosen and focused first; OK says what it does', dlg.checked === 'adopt' && dlg.focus === 'adopt' && dlg.ok === 'Adopt it', `${dlg.checked}/${dlg.focus}/${dlg.ok}`);
  check('the dialog names the process that holds the ports', /ROK\.exe · pid 8124/.test(dlg.facts), dlg.facts);
  await page.screenshot({ path: path.join(imgDir, 'steward-prestart.png') });
  log('saved docs/img/steward-prestart.png');
  await page.keyboard.press('ArrowDown');
  check('arrow keys move between choices and relabel OK', await page.evaluate(() => document.querySelector('#modal .choice input:checked').value === 'stop' && document.querySelector('#modal-ok').textContent === 'Stop it cleanly'));
  for (let i = 0; i < 7; i++) await page.keyboard.press('Tab');
  check('Tab stays inside the dialog', await page.evaluate(() => !!document.activeElement.closest('.modal-card')));
  await page.keyboard.press('Escape');
  await page.waitForSelector('#modal', { state: 'hidden' });
  check('Escape cancels and focus returns to Start', await page.evaluate(() => document.activeElement.id === 'srv-start' && window.__calls.adopt === 0 && window.__calls.stopHolder === 0));
  await page.click('#srv-start');
  await page.waitForSelector('#modal:not([hidden]) .choice');
  await page.keyboard.press('Enter');
  await page.waitForSelector('#modal', { state: 'hidden' });
  check('Enter on the chosen tile adopts the server', await page.evaluate(() => window.__calls.adopt === 1 && window.__calls.start === 0));

  // stop cleanly, but the watchdog brings it back: no start, the toast says why
  await page.click('#srv-start');
  await page.waitForSelector('#modal:not([hidden]) .choice');
  await page.click('#modal .choice:nth-child(2)');
  await page.click('#modal-ok');
  await page.waitForFunction(() => /watchdog started it again/.test(document.querySelector('#toasts').textContent));
  check('a watchdog relaunch after Stop cleanly is reported and nothing starts', await page.evaluate(() => window.__calls.stopHolder === 1 && window.__calls.start === 0));

  // ---- pre-start: nothing Steward can safely do (the Server.exe watchdog)
  await page.evaluate(() => {
    window.__pre = JSON.parse(JSON.stringify(window.__pre));
    const s = window.__pre.survey;
    s.verdict = 'watchdog';
    s.message = 'UDP 7350 (game) is already in use. Found the game\'s Server.exe watchdog and the server it keeps restarting: Server.exe (pid 7720, G:\\RealmTest\\server\\Server.exe); ROK.exe (pid 8124) on UDP 7350.';
    s.holders = [{ pid: 7720, name: 'Server', path: 'G:\\RealmTest\\server\\Server.exe', ports: [], kind: 'watchdog' }, { pid: 8124, name: 'ROK', path: '', ports: ['UDP 7350'], kind: 'hidden' }];
    const why = 'The game\'s Server.exe is running. It runs as administrator and starts ROK.exe again about 2 seconds after it ends, so stopping ROK.exe alone does not help. Close the Server.exe window first (or, in Task Manager opened as administrator, end Server.exe first, then ROK.exe). Steward does not end administrator programs.';
    s.actions = { stop: { ok: false, why }, adopt: { ok: false, why } };
    document.querySelector('#toasts').replaceChildren();
  });
  await page.click('#srv-start');
  await page.waitForSelector('#modal:not([hidden]) .modal-fix');
  await page.waitForTimeout(450);
  const blocked = await page.evaluate(() => ({ title: document.querySelector('#modal-title').textContent, ok: document.querySelector('#modal-ok').textContent, fix: document.querySelector('#modal .modal-fix').textContent, choices: document.querySelectorAll('#modal .choice').length }));
  check('watchdog: one clear fix and Check again, no unsafe choices', /watchdog is running/.test(blocked.title) && blocked.ok === 'Check again' && /Close the Server\.exe window first/.test(blocked.fix) && blocked.choices === 0, blocked.title);
  await page.screenshot({ path: path.join(imgDir, 'steward-prestart-blocked.png') });
  log('saved docs/img/steward-prestart-blocked.png');
  const before = await page.evaluate(() => window.__calls.prestart);
  await page.evaluate(() => (window.__pre = { running: false, survey: { clear: true, actions: { stop: {}, adopt: {} } }, world: { action: 'load', slot: 3, choices: [] }, saveLocation: 'Saves/' }));
  await page.click('#modal-ok');
  await page.waitForFunction((n) => window.__calls.prestart > n && window.__calls.start === 1, before);
  check('Check again surveys once more and starts when the ports are free', true);

  // ---- world choice
  await page.evaluate(() => {
    const now = Date.now();
    window.__pre = { running: false, survey: { clear: true, actions: { stop: {}, adopt: {} } }, saveLocation: 'G:\\RealmTest\\server\\Saves', world: { action: 'choose', suggest: 3, message: 'The settings point at world 6, a new world made by a failed start. World 3 is the one that last reached "Game has started." under Realm.', choices: [{ slot: 6, newestMs: now - 3600e3 * 26 }, { slot: 3, remembered: true, newestMs: now - 60e3 * 42 }] } };
  });
  await page.click('#srv-start');
  await page.waitForSelector('#modal:not([hidden]) .choice');
  await page.waitForTimeout(450);
  const world = await page.evaluate(() => ({ labels: Array.from(document.querySelectorAll('#modal .choice-label')).map((x) => x.textContent), ok: document.querySelector('#modal-ok').textContent }));
  check('world choice: the remembered world first, a new world last', /World 3\s*Suggested/.test(world.labels[0]) && /A new world/.test(world.labels[world.labels.length - 1]) && world.ok === 'Start world 3', world.labels.join(' | '));
  await page.screenshot({ path: path.join(imgDir, 'steward-world.png') });
  log('saved docs/img/steward-world.png');
  await page.keyboard.press('Escape');

  // ---- reduced motion
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const motion = await page.evaluate(() => ({ dur: getComputedStyle(document.documentElement).getPropertyValue('--dur-3').trim(), view: getComputedStyle(document.querySelector('.view:not([hidden])')).animationDuration, embers: getComputedStyle(document.querySelector('.embers')).display }));
  check('reduced motion: durations drop to zero and embers stop', motion.dur === '0ms' && parseFloat(motion.view) <= 0.002 && motion.embers === 'none', JSON.stringify(motion));
  await page.emulateMedia({ reducedMotion: 'no-preference' });

  // ---- Chronicle states on Home: loading skeleton, then unreachable with one fix
  const off = await browser.newPage({ viewport: { width: 1320, height: 840 }, deviceScaleFactor: 1 });
  off.on('pageerror', (e) => errors.push(e.message));
  await off.addInitScript(() => {
    // hold the first Chronicle answer back so the loading state can be seen
    Object.defineProperty(window, 'realm', {
      configurable: true,
      set(v) {
        const getEvents = v.getEvents;
        v.getEvents = (since) => new Promise((r) => setTimeout(r, 1500)).then(() => getEvents(since));
        Object.defineProperty(window, 'realm', { value: v, writable: true, configurable: true });
      }
    });
  });
  await off.goto(`http://127.0.0.1:${port}/index.html?offline`);
  await off.waitForSelector('#events .skel-row');
  check('Home shows skeleton rows while the Chronicle answers', (await off.$$('#events .skel-row')).length >= 3 && (await off.getAttribute('#events', 'aria-busy')) === 'true');
  await off.waitForSelector('#events .error-inline');
  const err = await off.textContent('#events .error-inline');
  check('an unreachable Chronicle says so, with one fix and a button', /not answering/.test(err) && /Open Overlay/.test(err), err.trim().replace(/\s+/g, ' '));
  await off.waitForTimeout(400);
  await off.screenshot({ path: path.join(imgDir, 'steward-home-offline.png') });
  log('saved docs/img/steward-home-offline.png');
  await off.close();

  // ---- the design system sheet
  await page.goto(`http://127.0.0.1:${port}/index.html?view=settings`);
  await page.waitForSelector('#set-save');
  await page.evaluate(({ events, houses, titles }) => {
    const ART = window.RealmArt;
    const el = (tag, cls, text) => {
      const n = document.createElement(tag);
      if (cls) n.className = cls;
      if (text != null) n.textContent = text;
      return n;
    };
    const sheet = el('div', 'ds-sheet');
    const add = (title, node) => {
      const sec = el('section', 'ds-sec');
      sec.append(el('p', 'eyebrow', title), node);
      sheet.appendChild(sec);
      return sec;
    };
    const sw = el('div', 'ds-row');
    for (const t of ['--iron-950', '--iron-900', '--iron-800', '--iron-700', '--iron-600', '--iron-200', '--parchment', '--parchment-2', '--parchment-edge', '--ember-deep', '--ember', '--ember-hot', '--blood', '--moss', '--ok', '--warn', '--bad']) {
      const s = el('div', 'ds-swatch');
      const chip = el('span', 'ds-chip');
      chip.style.background = `var(${t})`;
      s.append(chip, el('code', null, t));
      sw.appendChild(s);
    }
    add('Colour: brand palette (art/palette.json) and status tones', sw);
    const type = el('div', 'ds-type');
    const h = el('p', 'view-title', 'The Realm');
    const k = el('p', 'card-title', 'Card title · Cinzel 13.5');
    const p = el('p', 'ds-prose', 'Swear an oath. Claim a crown. Answer for it. EB Garamond for prose, 15.5 and up.');
    const u = el('p', 'fine', 'Segoe UI for data and controls · 12 / 13.5');
    type.append(h, k, p, u);
    add('Type: Cinzel display, EB Garamond prose, system UI for data', type);
    const ctl = el('div', 'ds-row');
    for (const [cls, label] of [['btn primary', 'Start'], ['btn', 'Restart'], ['btn ghost', 'Copy'], ['btn danger', 'Force stop'], ['btn small', 'Send']]) ctl.appendChild(el('button', cls, label));
    for (const [cls, label] of [['pill ok', 'Running'], ['pill warn', 'Loading'], ['pill bad', 'Offline'], ['pill', 'Stopped']]) ctl.appendChild(el('span', cls, label));
    const focus = el('button', 'btn ds-focus', 'Focus ring');
    ctl.appendChild(focus);
    add('Controls, status pills and the focus ring', ctl);
    const icons = el('div', 'ds-icons');
    for (const t of events) {
      const cell = el('div', 'ds-icon');
      cell.append(ART.eventMark({ type: t, title: '' }), el('code', null, t.replace(/_/g, ' ')));
      icons.appendChild(cell);
    }
    add('One icon per Chronicle event type, toned like the overlay', icons);
    const her = el('div', 'ds-row ds-heraldry');
    for (const id of houses) her.appendChild(ART.houseMark(id, 'shield', 'ds-shield'));
    her.appendChild(ART.houseMark('Thornvale', 'shield', 'ds-shield'));
    for (let n = 1; n <= 4; n++) her.appendChild(ART.seasonBadge(n, 'ds-badge'));
    for (const t of titles) her.appendChild(ART.titleBadge(ART.TITLES.find((x) => x.id === t), 'ds-badge'));
    add('Heraldry: the six great houses, a player-founded house, season and title badges', her);
    const states = el('div', 'ds-states');
    states.append(
      el('div', 'ds-state'),
      el('div', 'ds-state'),
      el('div', 'ds-state')
    );
    states.children[0].append(...ART.skeletonRows(3, 'div'));
    states.children[1].append(ART.emptyState({ art: 'decree', title: 'The Chronicle is silent', body: 'Coronations, oaths and claims appear here once the plugins record them.' }));
    states.children[2].append(ART.errorBox({ title: 'The Chronicle is not answering', body: 'Realm runs it on this PC at 127.0.0.1:8787.', fix: 'Open the Overlay screen to see why.', action: { label: 'Open Overlay', onClick() {} } }));
    add('States: loading, empty, error with one fix', states);
    const wrap = el('div', 'ds-overlay');
    wrap.appendChild(sheet);
    document.body.appendChild(wrap);
  }, { events: Object.keys(ART.EVENTS), houses: ART.HOUSES.map((x) => x.id), titles: ['kingslayer', 'kingmaker', 'legend', 'oathbreaker', 'black_name'] });
  // The page's CSP allows only its own stylesheets, so the sheet's layout is set through the CSSOM.
  await page.evaluate(() => {
    const set = (sel, css) => document.querySelectorAll(sel).forEach((n) => Object.assign(n.style, css));
    set('.ds-overlay', { position: 'fixed', inset: '40px 0 0 92px', zIndex: '45', overflow: 'auto', padding: '22px 30px', background: 'var(--iron-900)' });
    set('.ds-sheet', { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '14px 26px' });
    set('.ds-sec', { display: 'flex', flexDirection: 'column', gap: '8px' });
    set('.ds-sec:nth-child(4), .ds-sec:nth-child(5), .ds-sec:nth-child(6)', { gridColumn: '1 / -1' });
    set('.ds-row', { display: 'flex', flexWrap: 'wrap', gap: '8px', alignItems: 'center' });
    set('.ds-swatch', { display: 'flex', flexDirection: 'column', gap: '3px', width: '74px' });
    set('.ds-chip', { display: 'block', height: '30px', borderRadius: '4px', border: '1px solid rgba(214,160,67,.25)' });
    set('.ds-swatch code', { fontSize: '9px', color: 'var(--text-muted)', whiteSpace: 'nowrap' });
    set('.ds-icon code', { fontSize: '9px', lineHeight: '1.15', color: 'var(--text-muted)', textAlign: 'center', whiteSpace: 'normal' });
    set('.ds-type', { display: 'flex', flexDirection: 'column', gap: '4px' });
    set('.ds-prose', { fontFamily: 'var(--font-body)', fontSize: '17px', color: 'var(--text-2)' });
    set('.ds-icons', { display: 'grid', gridTemplateColumns: 'repeat(14, minmax(0, 1fr))', gap: '8px 6px' });
    set('.ds-icon', { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px', minWidth: '0' });
    set('.ds-shield', { width: '50px', height: '58px', objectFit: 'contain' });
    set('.ds-badge', { width: '48px', height: '48px' });
    set('.ds-states', { display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: '14px', alignItems: 'start' });
    set('.ds-state', { padding: '10px 14px', border: '1px solid var(--line)', borderRadius: '4px', background: 'var(--surface)' });
    const f = document.querySelector('.ds-focus');
    f.style.boxShadow = 'var(--focus-ring)';
  });
  await page.waitForFunction(() => Array.from(document.querySelectorAll('.ds-overlay img')).every((i) => i.complete));
  await page.waitForTimeout(300);
  const dsBroken = await page.evaluate(() => Array.from(document.querySelectorAll('.ds-overlay img')).filter((i) => !i.naturalWidth).map((i) => i.getAttribute('src')));
  check('every sigil, shield and badge in the sheet loads', !dsBroken.length, dsBroken.join(', '));
  await page.screenshot({ path: path.join(imgDir, 'design-system.png') });
  log('saved docs/img/design-system.png');

  const real = errors.filter((e) => !/fonts\.(googleapis|gstatic)\.com/.test(e));
  check('no page errors or failed requests', !real.length, real.join(' | '));
} catch (e) {
  failed++;
  log('ERROR', e.stack || e.message);
} finally {
  await browser.close();
  server.close();
}

for (const w of warnings) log('WARN ' + w);
log(`${passed}/${passed + failed} checks passed${warnings.length ? `, ${warnings.length} warning(s)` : ''}`);
process.exitCode = failed ? 1 : 0;
