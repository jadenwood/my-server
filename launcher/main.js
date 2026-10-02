'use strict';

// Realm launcher main process. It never writes to, patches or launches game
// files directly: PLAY hands steam://rungameid/<appId> to the OS, and
// "Verify install" only checks whether configured folders exist.

const { app, BrowserWindow, ipcMain, shell, clipboard } = require('electron');
const fs = require('fs');
const path = require('path');

const FETCH_TIMEOUT_MS = 4000;

// A config.json / news.json placed next to the .exe overrides the bundled one,
// so the owner can edit settings without rebuilding.
function candidateDirs() {
  const dirs = [];
  if (process.env.PORTABLE_EXECUTABLE_DIR) dirs.push(process.env.PORTABLE_EXECUTABLE_DIR);
  if (app.isPackaged) dirs.push(path.dirname(process.execPath));
  dirs.push(__dirname);
  return dirs;
}

function readJson(name, fallback) {
  for (const dir of candidateDirs()) {
    const file = path.join(dir, name);
    try {
      if (fs.existsSync(file)) return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch (err) {
      console.error(`Could not read ${file}: ${err.message}`);
    }
  }
  return fallback;
}

function loadConfig() {
  const cfg = readJson('config.json', {});
  const server = cfg.server || {};
  return {
    realmName: String(cfg.realmName || 'The Realm'),
    tagline: String(cfg.tagline || ''),
    chronicleUrl: chronicleBase(cfg.chronicleUrl),
    pollSeconds: Math.max(5, Number(cfg.pollSeconds) || 15),
    server: {
      name: String(server.name || 'Realm'),
      address: String(server.address || '127.0.0.1'),
      port: Number(server.port) || 7350
    },
    steamAppId: Number.isInteger(Number(cfg.steamAppId)) && Number(cfg.steamAppId) > 0 ? Number(cfg.steamAppId) : 344760,
    installPaths: Array.isArray(cfg.installPaths) ? cfg.installPaths.map(String) : [],
    links: (Array.isArray(cfg.links) ? cfg.links : [])
      .filter((l) => l && l.id && l.label && isAllowedLinkUrl(l.url))
      .map((l) => ({ id: String(l.id), label: String(l.label), url: String(l.url) }))
  };
}

const DEFAULT_CHRONICLE_URL = 'http://127.0.0.1:8787';
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]']);

function parseUrl(value) {
  try {
    return new URL(String(value));
  } catch {
    return null;
  }
}

// Chronicle base URL: http(s) only, no embedded credentials, no query or fragment.
// Anything else falls back to the local default.
function chronicleBase(value) {
  const u = parseUrl(value || DEFAULT_CHRONICLE_URL);
  if (!u || (u.protocol !== 'http:' && u.protocol !== 'https:') || u.username || u.password) {
    if (value) console.error(`Ignoring invalid chronicleUrl ${JSON.stringify(value)}; using ${DEFAULT_CHRONICLE_URL}`);
    return DEFAULT_CHRONICLE_URL;
  }
  return `${u.protocol}//${u.host}${u.pathname}`.replace(/\/+$/, '');
}

// Link buttons: any https:// URL, or plain http:// only to this machine (the local chronicle).
function isAllowedLinkUrl(value) {
  const u = parseUrl(value);
  if (!u || u.username || u.password) return false;
  if (u.protocol === 'https:') return true;
  return u.protocol === 'http:' && LOOPBACK_HOSTS.has(u.hostname);
}

// The only gate to shell.openExternal: steam://rungameid/<digits> or an allowed link URL.
function openExternalAllowed(url) {
  const ok = /^steam:\/\/rungameid\/\d+$/.test(url) || isAllowedLinkUrl(url);
  if (!ok) throw new Error('blocked external URL');
  return shell.openExternal(url);
}

let config = loadConfig();
let win = null;

async function getJson(url) {
  const res = await fetch(url, {
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
    headers: { Accept: 'application/json' }
  });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

// Only accept IPC from our own window's top frame.
function trusted(event) {
  return win && event.sender === win.webContents && event.senderFrame === win.webContents.mainFrame;
}

function handle(channel, fn) {
  ipcMain.handle(channel, async (event, ...args) => {
    if (!trusted(event)) throw new Error('untrusted sender');
    return fn(...args);
  });
}

function publicConfig() {
  // The renderer gets display data only; link URLs stay in the main process.
  const { installPaths, links, ...rest } = config;
  return { ...rest, links: links.map(({ id, label }) => ({ id, label })) };
}

function registerIpc() {
  handle('realm:config', () => publicConfig());

  handle('realm:news', () => {
    const news = readJson('news.json', []);
    return Array.isArray(news) ? news.slice(0, 20) : [];
  });

  handle('realm:state', async () => {
    try {
      return { ok: true, state: await getJson(`${config.chronicleUrl}/api/state`) };
    } catch (err) {
      return { ok: false, error: err.message };
    }
  });

  handle('realm:events', async (since) => {
    const sinceId = Number.isInteger(since) && since > 0 ? since : 0;
    // First load asks for the latest events; later polls ask only for newer ones.
    const query = sinceId > 0 ? `since=${sinceId}&limit=50` : 'limit=20';
    try {
      const body = await getJson(`${config.chronicleUrl}/api/events?${query}`);
      const events = Array.isArray(body) ? body : Array.isArray(body && body.events) ? body.events : [];
      return { ok: true, events };
    } catch (err) {
      return { ok: false, error: err.message };
    }
  });

  handle('realm:copyAddress', () => {
    const text = `${config.server.address}:${config.server.port}`;
    clipboard.writeText(text);
    return text;
  });

  handle('realm:play', async () => {
    // Generic Steam URL: launches the player's own copy, passes no connect info.
    await openExternalAllowed(`steam://rungameid/${config.steamAppId}`);
    return true;
  });

  handle('realm:openLink', async (id) => {
    const link = config.links.find((l) => l.id === id);
    if (!link) return false;
    await openExternalAllowed(link.url);
    return true;
  });

  handle('realm:verifyInstall', () => {
    // Read-only: existence checks, nothing else.
    const results = config.installPaths.map((p) => {
      let exists = false;
      try {
        exists = fs.statSync(p).isDirectory();
      } catch {
        exists = false;
      }
      return { path: p, exists };
    });
    return { found: results.some((r) => r.exists), results };
  });

  handle('realm:window', (action) => {
    if (!win) return;
    if (action === 'minimize') win.minimize();
    else if (action === 'close') win.close();
  });
}

function createWindow() {
  win = new BrowserWindow({
    width: 1280,
    height: 800,
    minWidth: 1024,
    minHeight: 680,
    frame: false,
    backgroundColor: '#0b0907',
    show: false,
    title: config.realmName,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      webviewTag: false,
      spellcheck: false
    }
  });

  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  win.webContents.on('will-navigate', (e) => e.preventDefault());
  win.once('ready-to-show', () => win.show());
  win.on('closed', () => {
    win = null;
  });
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

app.on('web-contents-created', (_e, contents) => {
  contents.on('will-attach-webview', (e) => e.preventDefault());
});

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (win) {
      if (win.isMinimized()) win.restore();
      win.focus();
    }
  });
  app.whenReady().then(() => {
    config = loadConfig();
    registerIpc();
    createWindow();
  });
  app.on('window-all-closed', () => app.quit());
}
