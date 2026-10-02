// Screenshot and smoke test of the Discord herald card (Settings) in Realm Steward, under xvfb with
// Playwright's Electron support. Uses a throwaway user-data folder and a made-up webhook URL.
// It never presses "Send test" and no Chronicle events exist in the sandbox, so nothing is sent
// to Discord. Saves docs/img/steward-discord.png.
//
//   xvfb-run -a node scripts/discord-screens.mjs
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const launcher = path.resolve(here, '..');
const repo = path.resolve(launcher, '..');
const require = createRequire(import.meta.url);
function loadPlaywright() {
  try {
    return require('playwright');
  } catch {
    return require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
  }
}
const { _electron: electron } = loadPlaywright();
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const checks = [];
const check = (name, ok, info = '') => {
  checks.push(!!ok);
  console.log(`[discord-screens] ${ok ? 'PASS' : 'FAIL'} ${name}${info ? ' - ' + info : ''}`);
};

const base = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-discord-ui-'));
const userData = path.join(base, 'userdata');
fs.mkdirSync(userData, { recursive: true });
// Skip the first-run wizard: this script only looks at Settings.
fs.writeFileSync(path.join(userData, 'realm-settings.json'), JSON.stringify({ setupComplete: true, testRoot: path.join(base, 'server') }));
const FAKE = 'https://discord.com/api/webhooks/100000000000000001/Sample_Token_For_Screenshots_Only_0123456789abcdefghij';

const app = await electron.launch({
  executablePath: path.join(launcher, 'node_modules', 'electron', 'dist', 'electron'),
  args: ['--no-sandbox', launcher],
  env: { ...process.env, REALM_USER_DATA: userData }
});
const errors = [];
try {
  const page = await app.firstWindow();
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewportSize({ width: 1320, height: 900 }).catch(() => {});
  await page.waitForLoadState('domcontentloaded');
  await page.evaluate(() => document.fonts.ready);
  await page.click('.rail-btn[data-go="settings"]');
  await page.waitForSelector('#discord-card .discord-type input', { timeout: 15000 });
  check('Discord card mounted in Settings', true);

  // Bad URL is refused with a plain message.
  await page.fill('#discord-card input[type=password]', 'https://evil.test/api/webhooks/1/2');
  await page.click('#discord-card button:has-text("Save Discord settings")');
  await page.waitForSelector('.toast.bad', { timeout: 5000 });
  check('non-Discord URL refused', /discord\.com/.test(await page.textContent('.toast.bad')));
  await page.evaluate(() => document.getElementById('toasts').replaceChildren());

  await page.fill('#discord-card input[type=password]', FAKE);
  await page.check('#discord-card .discord-toggle input');
  await page.fill('#discord-card .discord-twin input >> nth=0', 'The Realm of Ostreval');
  await page.fill('#discord-card .discord-twin input >> nth=1', 'https://realm.test');
  await page.click('#discord-card button:has-text("Save Discord settings")');
  await page.waitForFunction(() => /Saved: https:\/\/discord\.com\/api\/webhooks\/100000000000000001/.test(document.querySelector('#discord-card small').textContent), null, { timeout: 5000 });
  const hint = await page.textContent('#discord-card small');
  check('saved hint shows the webhook id but not the token', !hint.includes('Sample_Token'), hint);
  check('pill says On', (await page.textContent('#discord-card .pill')) === 'On');
  const stored = fs.readFileSync(path.join(userData, 'realm-discord.json'), 'utf8');
  check('settings file kept in user data', stored.includes('"enabled": true'));
  check('token stored encrypted or (no keyring under xvfb) plain in user data only', !stored.includes('Sample_Token') || stored.includes('"plain"'));
  const bridge = await page.evaluate(() => Object.keys(window.realm.discord).sort().join(','));
  check('bridge exposes only get/save/test/forget', bridge === 'forget,get,save,test', bridge);

  await page.evaluate(() => document.getElementById('discord-card').scrollIntoView({ block: 'start' }));
  await sleep(600);
  await page.evaluate(() => document.getElementById('toasts').replaceChildren());
  await page.screenshot({ path: path.join(repo, 'docs', 'img', 'steward-discord.png') });
  console.log('[discord-screens] saved docs/img/steward-discord.png');
  check('no page errors', errors.length === 0, errors.join(' | '));
} finally {
  await app.close().catch(() => {});
  fs.rmSync(base, { recursive: true, force: true });
}
const failed = checks.filter((c) => !c).length;
console.log(`[discord-screens] ${checks.length - failed}/${checks.length} checks passed`);
process.exit(failed ? 1 : 0);
