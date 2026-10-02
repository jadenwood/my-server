// Renders the launcher UI in headless Chromium (mock bridge) and saves
// docs/img/launcher.png. Needs the playwright package (local or global).
import path from 'node:path';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { startPreview } from './preview-server.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.resolve(here, '..', '..', 'docs', 'img', 'launcher.png');
const executablePath = process.env.CHROMIUM_PATH || '/opt/pw-browsers/chromium';

function loadPlaywright() {
  const require = createRequire(import.meta.url);
  try {
    return require('playwright');
  } catch {
    const globalRoot = execSync('npm root -g').toString().trim();
    return require(path.join(globalRoot, 'playwright'));
  }
}

const { chromium } = loadPlaywright();
const port = 5179;
const server = await startPreview(port);
const browser = await chromium.launch({ executablePath });
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  await page.goto(`http://127.0.0.1:${port}/`);
  await page.waitForSelector('.event');
  await page.waitForTimeout(2500);
  await page.screenshot({ path: out });
  if (errors.length) {
    console.error('Page errors:\n' + errors.join('\n'));
    process.exitCode = 1;
  }
  console.log('Saved ' + out);
} finally {
  await browser.close();
  server.close();
}
