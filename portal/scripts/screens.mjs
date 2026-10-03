// Screenshots of the built portal for docs/img/portal-*.png, rendered in headless Chromium with
// Playwright (a dev tool only, not a dependency of the portal). Builds the site from the sample data
// plus sample-data/showcase (a few extra Chronicle entries so title badges and more event icons show),
// loads each page from file:// and checks it on the way:
//
//   node scripts/screens.mjs [--out <dir>] [--check-only]
//
// Checks per page: no page errors, no failed requests, no console errors, every <img> loaded, every
// icon <use> points at a symbol on the page, no horizontal scroll at phone width.
// Set PLAYWRIGHT_MODULE / CHROMIUM if the defaults below do not exist on your machine.

import { mkdtemp, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { buildSite } from '../lib/generate.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const portal = resolve(here, '..');
const repo = resolve(portal, '..');
const args = process.argv.slice(2);
const outIdx = args.indexOf('--out');
const imgDir = resolve(outIdx >= 0 ? args[outIdx + 1] : join(repo, 'docs', 'img'));
const CHECK_ONLY = args.includes('--check-only');

function loadPlaywright() {
  const require = createRequire(import.meta.url);
  for (const t of [process.env.PLAYWRIGHT_MODULE, 'playwright', '/opt/node-tools/node_modules/playwright'].filter(Boolean)) {
    try { return require(t); } catch { /* next */ }
  }
  throw new Error('Playwright not found; set PLAYWRIGHT_MODULE to its folder');
}

const SHOTS = [
  { page: 'index.html', file: 'portal-home.png', w: 1440, h: 1600 },
  { page: 'chronicle.html', file: 'portal-chronicle.png', w: 1440, h: 1200 },
  { page: 'houses.html', file: 'portal-houses.png', w: 1440, h: 1300 },
  { page: 'houses/varrow.html', file: 'portal-house.png', w: 1440, h: 1500 },
  { page: 'kings.html', file: 'portal-kings.png', w: 1440, h: 1700 },
  { page: 'play.html', file: 'portal-play.png', w: 1440, h: 1100 },
  { page: 'download.html', file: 'portal-download.png', w: 1440, h: 900 },
  { page: 'index.html', file: 'portal-mobile.png', w: 390, h: 2200, mobile: true },
  { page: 'kings.html', file: 'portal-kings-mobile.png', w: 390, h: 1800, mobile: true },
];
// Pages checked at phone width without a screenshot.
const PHONE_CHECKS = ['chronicle.html', 'houses.html', 'houses/corvane.html', 'download.html', 'rules.html', '404.html'];

const tmp = await mkdtemp(join(tmpdir(), 'realm-portal-'));
const out = join(tmp, 'site');
// Fixed clock so screenshots are reproducible: shortly after the sample data's last update.
const NOW = Date.parse('2026-10-01T21:08:00Z');
await buildSite({
  outDir: out,
  now: NOW,
  dataDirs: [join(portal, 'sample-data', 'showcase'), join(repo, 'chronicle', 'sample-data'), join(portal, 'sample-data')],
  config: {
    realmName: 'The Realm of Ostreval',
    siteUrl: 'https://realm.test/',
    download: { url: 'https://downloads.realm.test/Realm-Setup-1.0.0.exe', version: '1.0.0' },
    server: { address: 'play.realm.test', port: 7350 },
    discordInvite: 'https://discord.realm.test/invite',
    streamers: [
      { name: 'Wren of Ashgrove', url: 'https://twitch.realm.test/wren', platform: 'Twitch', house: 'Ashgrove', note: 'Crown Nights live, with the Chronicle overlay.' },
      { name: 'The Raven\'s Eye', url: 'https://video.realm.test/ravens-eye', platform: 'YouTube', house: 'Corvane', note: 'Weekly recap of the realm.' },
    ],
    events: [{ title: 'Crown Night: the first Lawful Hours', at: '2026-10-17T19:00:00Z', end: '2026-10-17T20:30:00Z', detail: 'Declared claims only. Swear your oaths before dusk.' }],
  },
});

const { chromium } = loadPlaywright();
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || '/opt/pw-browsers/chromium' });
const problems = [];

async function open(page, { w, h, mobile }) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: 1, isMobile: !!mobile, hasTouch: !!mobile, reducedMotion: 'reduce' });
  const p = await ctx.newPage();
  // Freeze the page's clock at the build time, so countdowns and "2h ago" match the sample data.
  if (p.clock) await p.clock.setFixedTime(new Date(NOW));
  const errs = [];
  p.on('pageerror', (e) => errs.push(`pageerror ${e.message}`));
  p.on('console', (m) => { if (m.type() === 'error' && !/manifest/i.test(m.text())) errs.push(`console ${m.text()}`); });
  p.on('requestfailed', (r) => { if (!/manifest\.webmanifest$/.test(r.url())) errs.push(`requestfailed ${r.url()}`); });
  await p.goto(pathToFileURL(join(out, page)).href, { waitUntil: 'load' });
  await p.evaluate(() => document.fonts && document.fonts.ready);
  // Lazy images below the fold: load them all so a broken path is caught.
  await p.evaluate(async () => {
    for (const i of document.images) i.loading = 'eager';
    await Promise.all([...document.images].map((i) => i.decode().catch(() => {})));
  });
  await p.waitForTimeout(400);
  const found = await p.evaluate(() => {
    const bad = [];
    for (const i of document.images) if (!i.complete || !i.naturalWidth) bad.push(`image did not load: ${i.getAttribute('src')}`);
    for (const u of document.querySelectorAll('use')) {
      const id = (u.getAttribute('href') || '').slice(1);
      if (!document.getElementById(id)) bad.push(`icon symbol missing: ${id}`);
    }
    if (document.documentElement.scrollWidth > window.innerWidth + 1) bad.push(`scrolls sideways: ${document.documentElement.scrollWidth}px > ${window.innerWidth}px`);
    return bad;
  });
  for (const x of errs.concat(found)) problems.push(`${page} @${w}px: ${x}`);
  return { ctx, p };
}

if (!CHECK_ONLY) await mkdir(imgDir, { recursive: true });
for (const s of SHOTS) {
  const { ctx, p } = await open(s.page, s);
  if (!CHECK_ONLY) {
    await p.screenshot({ path: join(imgDir, s.file) });
    console.log('[portal-screens] wrote', join(imgDir, s.file));
  }
  await ctx.close();
}
for (const page of PHONE_CHECKS) {
  const { ctx } = await open(page, { w: 390, h: 900, mobile: true });
  await ctx.close();
}
await browser.close();
await rm(tmp, { recursive: true, force: true });
console.log(`[portal-screens] ${SHOTS.length + PHONE_CHECKS.length} page loads, ${problems.length} problem(s)`);
for (const x of problems) console.log(`  PROBLEM ${x}`);
process.exit(problems.length ? 1 : 0);
