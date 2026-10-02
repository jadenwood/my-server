// Screenshots of the built portal for docs/img/portal-*.png, using the Electron binary the
// launcher already installs (no browser download). Builds from sample data into a temp folder,
// then loads each page from file:// in an offscreen window. Run under a display or xvfb:
//
//   xvfb-run -a node scripts/screens.mjs
//
import { mkdtemp, writeFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { buildSite } from '../lib/generate.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const portal = resolve(here, '..');
const repo = resolve(portal, '..');
const imgDir = join(repo, 'docs', 'img');
const electron = join(repo, 'launcher', 'node_modules', 'electron', 'dist', process.platform === 'win32' ? 'electron.exe' : 'electron');

const SHOTS = [
  { page: 'index.html', file: 'portal-home.png', w: 1440, h: 1500 },
  { page: 'chronicle.html', file: 'portal-chronicle.png', w: 1440, h: 1200 },
  { page: 'houses.html', file: 'portal-houses.png', w: 1440, h: 1100 },
  { page: 'houses/varrow.html', file: 'portal-house.png', w: 1440, h: 1500 },
  { page: 'kings.html', file: 'portal-kings.png', w: 1440, h: 1050 },
  { page: 'play.html', file: 'portal-play.png', w: 1440, h: 1100 },
  { page: 'download.html', file: 'portal-download.png', w: 1440, h: 900 },
  { page: 'index.html', file: 'portal-mobile.png', w: 400, h: 1700 },
];

const tmp = await mkdtemp(join(tmpdir(), 'realm-portal-'));
const out = join(tmp, 'site');
// Fixed clock so screenshots are reproducible: shortly after the sample data's last update.
await buildSite({
  outDir: out,
  now: Date.parse('2026-10-01T21:08:00Z'),
  config: {
    realmName: 'The Realm of Ostreval',
    download: { url: 'https://downloads.realm.test/Realm-Setup-0.3.0.exe', version: '0.3.0' },
    server: { address: 'play.realm.test', port: 7350 },
    discordInvite: 'https://discord.realm.test/invite',
    streamers: [
      { name: 'Wren of Ashgrove', url: 'https://twitch.realm.test/wren', platform: 'Twitch', house: 'Ashgrove', note: 'Crown Nights live, with the Chronicle overlay.' },
      { name: 'The Raven\'s Eye', url: 'https://video.realm.test/ravens-eye', platform: 'YouTube', house: 'Corvane', note: 'Weekly recap of the realm.' },
    ],
    events: [{ title: 'Crown Night: the first Lawful Hours', at: '2026-10-17T19:00:00Z', end: '2026-10-17T20:30:00Z', detail: 'Declared claims only. Swear your oaths before dusk.' }],
  },
});

const runner = join(tmp, 'shoot.cjs');
await writeFile(runner, `
const { app, BrowserWindow } = require('electron');
const fs = require('fs');
const shots = ${JSON.stringify(SHOTS.map((s) => ({ ...s, url: pathToFileURL(join(out, s.page)).href, dest: join(imgDir, s.file) })))};
app.disableHardwareAcceleration();
app.whenReady().then(async () => {
  // One window, navigated page by page (destroying offscreen windows between loads made the next load fail).
  const win = new BrowserWindow({ width: 1440, height: 900, show: false, webPreferences: { offscreen: true } });
  for (const s of shots) {
    win.setContentSize(s.w, s.h);
    try {
      await win.loadURL(s.url);
    } catch (e) {
      console.log('[portal-screens] FAILED', s.page, e.message);
      continue;
    }
    await new Promise((r) => setTimeout(r, 800));
    const img = await win.webContents.capturePage({ x: 0, y: 0, width: s.w, height: s.h });
    fs.writeFileSync(s.dest, img.toPNG());
    console.log('[portal-screens] wrote', s.dest);
  }
  app.quit();
});
`);
await mkdir(imgDir, { recursive: true });
const code = await new Promise((ok) => {
  const p = spawn(electron, [runner, '--no-sandbox'], { stdio: 'inherit' });
  p.on('exit', ok);
});
await rm(tmp, { recursive: true, force: true });
process.exit(code || 0);
