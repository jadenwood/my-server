#!/usr/bin/env node
// Renders the Chronicle service's own pages (/overlay and /realm, chronicle/public) against
// chronicle/sample-data in headless Chromium and writes docs/img/overlay-*.png and realm-*.png.
// Dev tool only: Playwright is not a dependency. It lives here next to the stream scenes' screenshot
// tool because both are broadcast pages. Set PLAYWRIGHT_MODULE / CHROMIUM if the defaults do not exist.
//
// Usage: node tools/chronicle-shots.mjs [--out ../docs/img] [--check-only]
//
// Checks: no page errors or failed requests, every icon and image loads, the coronation and rebellion
// moments appear for replay=coronation / replay=rebellion_started, and the stage is transparent.

import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApp, parseOptions } from '../../chronicle/server.js';

const HERE = fileURLToPath(new URL('.', import.meta.url));
const args = process.argv.slice(2);
const outIdx = args.indexOf('--out');
const OUT = resolve(HERE, outIdx >= 0 ? args[outIdx + 1] : '../../docs/img');
const CHECK_ONLY = args.includes('--check-only');
const NOW = new Date('2026-10-01T21:05:00Z'); // the sample data's last update

function loadPlaywright() {
  const require = createRequire(import.meta.url);
  for (const t of [process.env.PLAYWRIGHT_MODULE, 'playwright', '/opt/node-tools/node_modules/playwright'].filter(Boolean)) {
    try { return require(t); } catch { /* next */ }
  }
  throw new Error('Playwright not found; set PLAYWRIGHT_MODULE to its folder');
}

const SHOTS = [
  { file: 'overlay-preview.png', path: '/overlay?bg=1&replay=1', w: 1920, h: 1080, wait: 3600, expect: '.proclaim' },
  { file: 'overlay-coronation.png', path: '/overlay?bg=1&replay=coronation', w: 1920, h: 1080, wait: 4200, expect: '.moment.coronation' },
  { file: 'overlay-rebellion.png', path: '/overlay?bg=1&replay=rebellion_started', w: 1920, h: 1080, wait: 3400, expect: '.moment.rebellion' },
  { file: 'overlay-transparent.png', path: '/overlay?replay=coronation', w: 1920, h: 1080, wait: 4200, expect: '.moment.coronation', transparent: true },
  { file: 'realm-desktop.png', path: '/realm', w: 1280, h: 2400, wait: 1500, expect: '.entry' },
  { file: 'realm-mobile.png', path: '/realm', w: 390, h: 1600, wait: 1500, expect: '.entry', mobile: true },
];

const app = createApp(parseOptions(['--data', resolve(HERE, '../../chronicle/sample-data'), '--stale', '0', '--port', '0'], {}));
const server = createServer(app);
await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
const base = `http://127.0.0.1:${server.address().port}`;
const { chromium } = loadPlaywright();
const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || '/opt/pw-browsers/chromium' });
const problems = [];
if (!CHECK_ONLY) await mkdir(OUT, { recursive: true });

for (const s of SHOTS) {
  const ctx = await browser.newContext({ viewport: { width: s.w, height: s.h }, isMobile: !!s.mobile });
  const page = await ctx.newPage();
  // Freeze Date only (timers and animations run normally), so "reigning 1d 1h" matches the sample data.
  if (page.clock) await page.clock.setFixedTime(NOW);
  const errs = [];
  page.on('pageerror', (e) => errs.push(`pageerror ${e.message}`));
  page.on('requestfailed', (r) => errs.push(`requestfailed ${r.url()}`));
  page.on('response', (r) => { if (r.status() >= 400) errs.push(`HTTP ${r.status()} ${r.url()}`); });
  await page.goto(base + s.path, { waitUntil: 'load' });
  await page.waitForTimeout(s.wait);
  const found = await page.evaluate((sel) => {
    const bad = [];
    if (!document.querySelector(sel)) bad.push(`${sel} not shown`);
    for (const i of document.images) if (i.complete && !i.naturalWidth) bad.push(`image did not load: ${i.getAttribute('src')}`);
    if (document.documentElement.scrollWidth > window.innerWidth + 1 && !document.getElementById('stage')) bad.push('scrolls sideways');
    return bad;
  }, s.expect);
  if (s.transparent) {
    const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor + '|' + getComputedStyle(document.body).backgroundImage);
    if (bg !== 'rgba(0, 0, 0, 0)|none') found.push(`overlay is not transparent (${bg})`);
  }
  for (const x of errs.concat(found)) problems.push(`${s.path}: ${x}`);
  if (!CHECK_ONLY) await page.screenshot({ path: resolve(OUT, s.file), omitBackground: !!s.transparent });
  console.log(`${CHECK_ONLY ? 'checked' : 'wrote  '} ${s.file}  ${s.path}`);
  await ctx.close();
}
await browser.close();
server.close();
console.log(`${SHOTS.length} page loads, ${problems.length} problem(s)`);
for (const p of problems) console.log(`  PROBLEM ${p}`);
process.exit(problems.length ? 1 : 0);
