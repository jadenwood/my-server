#!/usr/bin/env node
// Renders every scene against the fixtures with Playwright + Chromium, checks it in a real browser,
// and writes docs/img/stream-*.png. Dev tool only: Playwright is not a dependency of streamkit.
// Set PLAYWRIGHT_MODULE / CHROMIUM if the defaults below do not exist on your machine.
//
// Usage: node tools/screenshots.mjs [--out ../docs/img] [--check-only]
//
// Checks per scene: no page errors, no failed same-origin requests, the scene reported ready, the
// stage is transparent unless transparent=0, and reduced motion (both motion=0 and the OS setting)
// leaves no running decorative animations.

import { createServer } from 'node:http';
import { createRequire } from 'node:module';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createApp, parseOptions } from '../server.js';

const HERE = fileURLToPath(new URL('.', import.meta.url));
const args = process.argv.slice(2);
const outIdx = args.indexOf('--out');
const OUT = resolve(HERE, outIdx >= 0 ? args[outIdx + 1] : '../../docs/img');
const CHECK_ONLY = args.includes('--check-only');
const NOW = '2026-10-01T21:05:00Z'; // the fixtures' "updated" time; freezes every clock on the page

function loadPlaywright() {
  const require = createRequire(import.meta.url);
  for (const t of [process.env.PLAYWRIGHT_MODULE, 'playwright', '/opt/node-tools/node_modules/playwright'].filter(Boolean)) {
    try { return require(t); } catch { /* next */ }
  }
  throw new Error('Playwright not found; set PLAYWRIGHT_MODULE to its folder');
}

// The element each scene must actually show (visible, non-zero size, opaque body).
const MAIN = { '/war-board': 'svg.wb-graph', '/throne-room': 'section.throne', '/breaking-news': 'section.alert', '/countdown': 'section.countdown', '/starting-soon': '.ss-head h1' };

async function visible(page, path) {
  return page.evaluate((sel) => {
    const body = getComputedStyle(document.body);
    if (body.opacity !== '1' || body.visibility === 'hidden' || body.display === 'none') return `body opacity ${body.opacity}`;
    const node = document.querySelector(sel);
    if (!node) return `${sel} missing`;
    const junk = /\b(null|undefined|NaN|\[object Object\])\b/.exec(document.body.innerText);
    if (junk) return `page shows "${junk[1]}"`;
    const r = node.getBoundingClientRect();
    if (r.width < 10 || r.height < 10) return `${sel} has no size`;
    for (let n = node; n && n.nodeType === 1; n = n.parentElement) {
      if (Number(getComputedStyle(n).opacity) < 0.5) return `${sel} hidden by opacity on ${n.tagName}.${n.className}`;
    }
    return '';
  }, MAIN[path]);
}

const SHOTS = [
  { file: 'stream-war-board.png', path: '/war-board', q: 'transparent=0' },
  { file: 'stream-war-board-dunmere.png', path: '/war-board', q: 'transparent=0&house=Dunmere&ledger=0' },
  { file: 'stream-throne-room.png', path: '/throne-room', q: 'transparent=0&replay=1', wait: 4200 },
  { file: 'stream-breaking-news.png', path: '/breaking-news', q: 'transparent=0&replay=3', wait: 1500 },
  { file: 'stream-countdown.png', path: '/countdown', q: 'transparent=0' },
  { file: 'stream-countdown-compact.png', path: '/countdown', q: 'transparent=0&layout=compact&for=event&scale=1.4' },
  { file: 'stream-starting-soon.png', path: '/starting-soon', q: 'in=10m', wait: 2500 },
];

const CHECKS = [
  // transparent by default (overlays)
  { path: '/war-board', q: '', expectTransparent: true },
  { path: '/throne-room', q: 'replay=1', expectTransparent: true },
  { path: '/breaking-news', q: 'replay=1', expectTransparent: true },
  { path: '/countdown', q: '', expectTransparent: true },
  { path: '/starting-soon', q: '', expectTransparent: false },
  { path: '/starting-soon', q: 'transparent=1', expectTransparent: true },
  // reduced motion, by param and by OS setting
  { path: '/throne-room', q: 'replay=1&motion=0', reduced: true },
  { path: '/throne-room', q: 'replay=1', reduced: true, emulate: true },
  { path: '/breaking-news', q: 'replay=1&motion=0', reduced: true },
  { path: '/starting-soon', q: 'motion=0', reduced: true },
  { path: '/war-board', q: 'motion=0', reduced: true },
  { path: '/countdown', q: '', reduced: true, emulate: true },
  // filters and scale
  { path: '/throne-room', q: 'replay=1&house=Halloran', expectEmpty: 'section.throne' },
  { path: '/breaking-news', q: 'replay=5&house=Merrin', expectEmpty: 'section.alert' },
  { path: '/war-board', q: 'house=Thornwick', expectCount: ['g.node', 1] },
  { path: '/war-board', q: 'house=Dunmere', expectCount: ['g.node', 3] },
  { path: '/throne-room', q: 'replay=1&house=Corvane', expectCount: ['section.throne', 1] },
  { path: '/countdown', q: 'scale=0.5', expectScale: '0.5' },
];

async function main() {
  const server = createServer(createApp(parseOptions(['--fixtures', resolve(HERE, '../fixtures'), '--schedule', resolve(HERE, '../schedule.example.json'), '--port', '0'], {})));
  await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
  const base = `http://127.0.0.1:${server.address().port}`;
  const { chromium } = loadPlaywright();
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || '/opt/pw-browsers/chromium' });
  const problems = [];
  let pages = 0;

  async function open(path, q, { reduced = false, width = 1280, height = 720 } = {}) {
    const ctx = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1, reducedMotion: reduced ? 'reduce' : 'no-preference', ignoreHTTPSErrors: false });
    const page = await ctx.newPage();
    const errs = [];
    page.on('pageerror', (e) => errs.push(`pageerror: ${e.message}`));
    page.on('console', (m) => { if (m.type() === 'error') errs.push(`console: ${m.text()}`); });
    page.on('requestfailed', (r) => { if (r.url().startsWith(base)) errs.push(`requestfailed: ${r.url()}`); });
    page.on('response', (r) => { if (r.url().startsWith(base) && r.status() >= 400 && !r.url().endsWith('/schedule.json')) errs.push(`HTTP ${r.status()} ${r.url()}`); });
    const url = `${base}${path}?now=${NOW}${q ? `&${q}` : ''}`;
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 15000 });
    pages++;
    return { ctx, page, errs, url };
  }

  async function ready(page, url) {
    try {
      await page.waitForFunction(() => document.documentElement.dataset.ready === '1', null, { timeout: 8000 });
    } catch {
      return false;
    }
    return true;
  }

  if (!CHECK_ONLY) await mkdir(OUT, { recursive: true });
  for (const s of SHOTS) {
    const { ctx, page, errs, url } = await open(s.path, s.q);
    if (!(await ready(page, url))) problems.push(`${url}: never ready`);
    await page.evaluate(() => Promise.race([document.fonts ? document.fonts.ready : null, new Promise((ok) => setTimeout(ok, 3000))]));
    await page.waitForTimeout(s.wait ?? 2600);
    const vis = await visible(page, s.path);
    if (vis) problems.push(`${url}: not visible: ${vis}`);
    if (!CHECK_ONLY) await page.screenshot({ path: resolve(OUT, s.file) });
    for (const e of errs) problems.push(`${url}: ${e}`);
    console.log(`${CHECK_ONLY ? 'checked' : 'wrote  '} ${s.file}  ${url}`);
    await ctx.close();
  }

  for (const c of CHECKS) {
    const { ctx, page, errs, url } = await open(c.path, c.q, { reduced: !!c.emulate });
    const isReady = await ready(page, url);
    await page.waitForTimeout(600);
    if (c.expectEmpty) {
      const n = await page.locator(c.expectEmpty).count();
      if (n !== 0) problems.push(`${url}: expected no ${c.expectEmpty}, found ${n}`);
    } else if (!isReady) problems.push(`${url}: never ready`);
    else {
      const vis = await visible(page, c.path);
      if (vis) problems.push(`${url}: not visible: ${vis}`);
    }
    if (c.expectTransparent !== undefined) {
      const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundImage + '|' + getComputedStyle(document.body).backgroundColor);
      const transparent = bg === 'none|rgba(0, 0, 0, 0)';
      if (transparent !== c.expectTransparent) problems.push(`${url}: transparent=${transparent}, expected ${c.expectTransparent} (${bg.slice(0, 60)})`);
    }
    if (c.reduced) {
      // Every running animation must be (near) instant; transitions are limited to opacity.
      const longest = await page.evaluate(() => {
        let worst = { ms: 0, what: '' };
        for (const a of document.getAnimations()) {
          const t = a.effect && a.effect.getTiming ? a.effect.getTiming() : {};
          const ms = (Number(t.duration) || 0) * (t.iterations === Infinity ? 1e6 : Number(t.iterations) || 1);
          const what = `${a.animationName || a.transitionProperty || 'anim'} on ${a.effect && a.effect.target ? a.effect.target.className.baseVal ?? a.effect.target.className : '?'}`;
          // The breaking-news timer bar is information (time left), so it is allowed to run.
          if (ms > worst.ms && !/drain/.test(what)) worst = { ms, what };
        }
        return worst;
      });
      if (longest.ms > 300) problems.push(`${url}: reduced motion still runs ${longest.what} for ${longest.ms}ms`);
    }
    if (c.expectCount) {
      const n = await page.locator(c.expectCount[0]).count();
      if (n !== c.expectCount[1]) problems.push(`${url}: expected ${c.expectCount[1]} ${c.expectCount[0]}, found ${n}`);
    }
    if (c.expectScale) {
      const v = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--ui-scale').trim());
      if (v !== c.expectScale) problems.push(`${url}: --ui-scale ${v}`);
    }
    for (const e of errs) problems.push(`${url}: ${e}`);
    await ctx.close();
  }

  // Live update: with live=1 the War Board keeps polling; a new event must redraw it.
  {
    const demo = createServer(createApp(parseOptions(['--fixtures', resolve(HERE, '../fixtures'), '--demo', '1', '--port', '0'], {})));
    await new Promise((ok) => demo.listen(0, '127.0.0.1', ok));
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 720 } });
    const page = await ctx.newPage();
    const errs = [];
    page.on('pageerror', (e) => errs.push(e.message));
    await page.goto(`http://127.0.0.1:${demo.address().port}/breaking-news?poll=1&hold=3`);
    try {
      await page.waitForSelector('.alert.enter', { timeout: 9000 });
      const headline = await page.locator('.alert .headline').first().textContent();
      if (!/Merrin renounces/.test(headline)) problems.push(`live breaking news showed "${headline}"`);
      console.log(`live    breaking-news picked up a demo event: "${headline}"`);
    } catch {
      problems.push('live breaking news never showed the demo event');
    }
    for (const e of errs) problems.push(`live: ${e}`);
    await ctx.close();
    demo.close();
  }

  await browser.close();
  server.close();
  console.log(`${pages} page loads, ${problems.length} problem(s)`);
  for (const p of problems) console.log(`  PROBLEM ${p}`);
  process.exit(problems.length ? 1 : 0);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
