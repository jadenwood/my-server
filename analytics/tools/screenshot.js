#!/usr/bin/env node
'use strict';
// Renders a dashboard HTML file to a PNG with Playwright + Chromium. Dev tool only: Playwright is NOT a dependency of
// analytics/ (the dashboard builder itself needs nothing but Node). Point PLAYWRIGHT_MODULE at an installed copy and
// CHROMIUM at a Chromium binary if the defaults below do not exist on your machine.
//
// Usage: node analytics/tools/screenshot.js <dashboard.html> <out.png> [--width 1200] [--dark] [--full]

const path = require('node:path');

function loadPlaywright() {
  const tries = [process.env.PLAYWRIGHT_MODULE, 'playwright', '/opt/node-tools/node_modules/playwright'].filter(Boolean);
  for (const t of tries) {
    try { return require(t); } catch (_) { /* try the next one */ }
  }
  throw new Error('Playwright not found; set PLAYWRIGHT_MODULE to its folder');
}

async function main(argv) {
  const [input, output] = argv.filter((a) => !a.startsWith('--') && !/^\d+$/.test(a));
  if (!input || !output) throw new Error('usage: screenshot.js <dashboard.html> <out.png> [--width 1200] [--dark] [--full]');
  const wi = argv.indexOf('--width');
  const width = wi >= 0 ? Number(argv[wi + 1]) : 1200;
  const { chromium } = loadPlaywright();
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM || '/opt/pw-browsers/chromium' });
  try {
    const page = await browser.newPage({ viewport: { width, height: 900 }, deviceScaleFactor: 1, colorScheme: argv.includes('--dark') ? 'dark' : 'light' });
    const errors = [];
    page.on('pageerror', (e) => errors.push(e.message));
    page.on('requestfailed', (r) => errors.push('request ' + r.url()));
    page.on('request', (r) => { if (!r.url().startsWith('file:')) errors.push('external request ' + r.url()); });
    await page.goto('file://' + path.resolve(input));
    // Hover one point of the concurrency chart so the screenshot shows a tooltip and the crosshair.
    const hits = await page.$$('svg.chart .hit[data-x]');
    if (hits.length) {
      const target = hits[Math.floor(hits.length * 0.62)];
      const box = await target.boundingBox();
      if (box) await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.45);
    }
    await page.screenshot({ path: output, fullPage: argv.includes('--full') });
    if (errors.length) throw new Error('page problems: ' + errors.join('; '));
    process.stdout.write(`Wrote ${output}\n`);
  } finally {
    await browser.close();
  }
}

main(process.argv.slice(2)).catch((e) => { process.stderr.write('screenshot: ' + e.message + '\n'); process.exitCode = 1; });
