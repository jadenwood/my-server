'use strict';

// Realm (player edition): main process.
//
// This edition contains NO server control. It cannot start, stop or configure a game server, touch
// the firewall, sign lists or run setup steps. It only:
// - downloads servers.json and accepts it only when the Ed25519 signature matches the public key
//   embedded in this build (otherwise: the last good list, then the list bundled in the build);
// - shows live status (Steam A2S ping, the server's public Chronicle if one is listed);
// - hands allow-listed steam:// URLs to Windows: steam://run/344760//-ip <host> -port <port>/ (quick
//   join), steam://rungameid/344760 (classic) and steam://install/344760. The player's own Steam
//   starts the player's own copy; no game file is read, written, patched or launched directly;
// - accepts realm://join/<server id> links for ids in the signed list, always with a confirmation.
// The require graph of this file is checked by test/player-edition.test.js.

const { app, BrowserWindow, ipcMain, shell, clipboard, net, Tray, Menu, Notification, nativeImage, screen } = require('electron');
const LOG = require('../lib/shared/applog');
const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const M = require('../lib/shared/manifest');
const ST = require('../lib/shared/steam');
const A2S = require('../lib/shared/a2s');
const DL = require('../lib/shared/deeplink');
const PK = require('../lib/shared/pick');
const NT = require('../lib/shared/notify');

const DEV = !app.isPackaged;
const ROOT = path.join(__dirname, '..');

// A separate profile from Realm Steward, so both can live on the owner's PC.
app.setPath('userData', path.join(app.getPath('appData'), 'Realm Player'));
if (DEV && process.env.REALM_USER_DATA) app.setPath('userData', path.resolve(process.env.REALM_USER_DATA));

// ---------- build config (embedded; the public key is never read from anywhere else) ----------

const BUILTIN_SERVERS = [{ id: 's1', name: 'Realm I (this PC, test)', region: '', address: '127.0.0.1', port: 7350, queryPort: 27015, maxPlayers: 120 }];

function loadPlayerConfig() {
  let file = path.join(__dirname, 'player-config.json');
  if (DEV && process.env.REALM_DEV_PLAYER_CONFIG) file = path.resolve(process.env.REALM_DEV_PLAYER_CONFIG);
  let raw = {};
  try {
    raw = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  } catch (e) {
    console.error(`player-config.json: ${e.message}`);
  }
  let publicKey = '';
  if (typeof raw.publicKey === 'string' && raw.publicKey) {
    try {
      M.publicKeyFromBase64(raw.publicKey);
      publicKey = raw.publicKey;
    } catch (e) {
      console.error(`player-config.json publicKey: ${e.message}`);
    }
  }
  const links = raw.links && typeof raw.links === 'object' ? raw.links : {};
  return {
    realmName: String(raw.realmName || 'The Realm').slice(0, 60),
    tagline: String(raw.tagline || '').slice(0, 160),
    manifestUrl: manifestUrlAllowed(raw.manifestUrl) ? raw.manifestUrl : '',
    publicKey,
    bundledManifest: raw.bundledManifest && typeof raw.bundledManifest === 'object' ? raw.bundledManifest : null,
    links: {
      discord: typeof links.discord === 'string' && M.isWebUrl(links.discord, { httpsOnly: true }) ? links.discord : '',
      rules: typeof links.rules === 'string' && M.isWebUrl(links.rules, { httpsOnly: true }) ? links.rules : ''
    },
    joinMethod: raw.joinMethod === 'classic' ? 'classic' : 'quick',
    steamAppId: ST.GAME_APP_ID
  };
}

// https only. In development, http to this PC is allowed for the automated walk-through.
function manifestUrlAllowed(u) {
  if (typeof u !== 'string' || !u || u.length > 400) return false;
  if (M.isWebUrl(u, { httpsOnly: true })) return true;
  if (!DEV) return false;
  try {
    const x = new URL(u);
    return x.protocol === 'http:' && x.hostname === '127.0.0.1';
  } catch {
    return false;
  }
}

const cfg = loadPlayerConfig();

// ---------- per-user preferences ----------

const PREF_DEFAULTS = { onboarded: false, joinMethod: cfg.joinMethod, notifications: false, background: false, maxSeq: 0, cache: null, cacheAt: null };
let prefs = { ...PREF_DEFAULTS };

function prefsFile() {
  return path.join(app.getPath('userData'), 'realm-player.json');
}

async function loadPrefs() {
  try {
    const raw = JSON.parse(await fsp.readFile(prefsFile(), 'utf8'));
    for (const k of Object.keys(PREF_DEFAULTS)) if (k in raw) prefs[k] = raw[k];
  } catch {
    /* first run */
  }
  prefs.onboarded = prefs.onboarded === true;
  prefs.joinMethod = prefs.joinMethod === 'classic' ? 'classic' : 'quick';
  prefs.notifications = prefs.notifications === true;
  prefs.background = prefs.background === true;
  prefs.maxSeq = Number.isInteger(prefs.maxSeq) && prefs.maxSeq > 0 ? prefs.maxSeq : 0;
  if (typeof prefs.cache !== 'string') prefs.cache = null;
}

async function savePrefs(patch) {
  Object.assign(prefs, patch);
  await fsp.mkdir(path.dirname(prefsFile()), { recursive: true });
  const tmp = `${prefsFile()}.${process.pid}.tmp`;
  await fsp.writeFile(tmp, JSON.stringify(prefs, null, 2));
  await fsp.rename(tmp, prefsFile());
}

// ---------- the signed server list ----------

let list = { servers: BUILTIN_SERVERS, source: 'builtin', reason: 'No signed list yet.', realm: cfg.realmName, links: {} };
let statuses = {};
let statusAt = 0;

async function fetchText(url, maxBytes = 64 * 1024) {
  const res = await net.fetch(url, { signal: AbortSignal.timeout(8000), cache: 'no-store', redirect: 'follow' });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const buf = Buffer.from(await res.arrayBuffer());
  if (buf.length > maxBytes) throw new Error('response too large');
  return buf.toString('utf8');
}

async function loadServers() {
  const notes = [];
  if (!cfg.publicKey) notes.push('This build has no public key, so it cannot trust any downloaded list.');
  if (cfg.publicKey && cfg.manifestUrl) {
    try {
      const text = await fetchText(cfg.manifestUrl);
      const v = M.verifyEnvelope(text, cfg.publicKey, { minSeq: prefs.maxSeq });
      if (v.ok) {
        await savePrefs({ cache: text, cacheAt: new Date().toISOString(), maxSeq: Math.max(prefs.maxSeq, v.manifest.seq) });
        return setList(v.manifest, 'online', null);
      }
      notes.push(`Downloaded list refused: ${v.reason}.`);
    } catch (e) {
      notes.push(`Could not download the list (${e.message}).`);
    }
  }
  if (cfg.publicKey && prefs.cache) {
    // The saved copy and the built-in copy must not be older than the newest list this PC has
    // accepted (minSeq), so a fallback can never roll players back to retired addresses.
    const v = M.verifyEnvelope(prefs.cache, cfg.publicKey, { ignoreExpiry: true, minSeq: prefs.maxSeq });
    if (v.ok) {
      const stale = Date.parse(v.manifest.expires) <= Date.now();
      return setList(v.manifest, 'cache', notes.concat(stale ? ['The saved list has expired; the owner needs to publish a new one.'] : []).join(' '));
    }
  }
  if (cfg.publicKey && cfg.bundledManifest) {
    const v = M.verifyEnvelope(cfg.bundledManifest, cfg.publicKey, { ignoreExpiry: true, minSeq: prefs.maxSeq });
    if (v.ok) {
      const stale = Date.parse(v.manifest.expires) <= Date.now();
      return setList(v.manifest, 'bundled', notes.concat(stale ? ['The list inside this build has expired; the owner needs to publish a new one.'] : []).join(' '));
    }
    notes.push(`The list inside this build was not used: ${v.reason}.`);
  }
  list = { servers: BUILTIN_SERVERS, source: 'builtin', reason: notes.join(' ') || 'No signed list.', realm: cfg.realmName, links: {} };
  return list;
}

function setList(manifest, source, reason) {
  list = { servers: manifest.servers, source, reason: reason || null, realm: manifest.realm, seq: manifest.seq, issued: manifest.issued, expires: manifest.expires, links: manifest.links || {} };
  return list;
}

function serverById(id) {
  return list.servers.find((s) => s.id === id) || null;
}

// ---------- live status ----------

async function chronicleState(url) {
  try {
    const text = await fetchText(`${url}/api/state`, 256 * 1024);
    const s = JSON.parse(text);
    return s && typeof s === 'object' ? s : null;
  } catch {
    return null;
  }
}

async function statusOf(server) {
  const [a2s, chron] = await Promise.all([
    A2S.queryInfo(server.address, server.queryPort, { timeoutMs: 1500 }),
    server.chronicleUrl ? chronicleState(server.chronicleUrl) : Promise.resolve(null)
  ]);
  const fresh = chron && !chron.stale;
  const st = { online: null, pingMs: null, players: null, maxPlayers: server.maxPlayers, approx: false, source: [], king: null, house: null };
  if (a2s.ok) {
    st.online = true;
    st.pingMs = a2s.rttMs;
    st.source.push('steam');
  }
  if (chron) {
    st.source.push('chronicle');
    if (fresh) st.online = true;
    else if (!a2s.ok) st.online = false;
    if (fresh && Number.isInteger(chron.online)) st.players = Math.max(0, chron.online);
    if (Number.isInteger(chron.maxPlayers) && chron.maxPlayers > 0) st.maxPlayers = chron.maxPlayers;
    if (typeof chron.king === 'string') st.king = chron.king.slice(0, 64);
    if (typeof chron.house === 'string') st.house = chron.house.slice(0, 64);
  }
  if (st.players == null && a2s.ok && a2s.info.players != null) {
    // UNVERIFIED that the game's A2S player count is right; shown as approximate.
    st.players = a2s.info.players;
    st.approx = true;
  }
  return st;
}

async function refreshStatus() {
  const entries = await Promise.all(list.servers.slice(0, 16).map(async (s) => [s.id, await statusOf(s)]));
  statuses = Object.fromEntries(entries);
  statusAt = Date.now();
  const snap = statusSnapshot();
  push({ type: 'status', ...snap });
  return snap;
}

function statusSnapshot() {
  return { statuses, best: PK.pickBest(list.servers, statuses), at: statusAt ? new Date(statusAt).toISOString() : null };
}

// ---------- joining ----------

function openSteam(url) {
  if (!ST.isAllowedSteamUrl(url, cfg.steamAppId)) throw new Error('blocked external URL');
  return shell.openExternal(url);
}

async function installState() {
  // Development-only: lets the automated walk-through show the "not installed" path on Linux.
  if (DEV && process.env.REALM_DEV_GAME_INSTALL === 'missing') return { steam: true, steamPath: null, installed: false, library: null };
  return ST.detectGameInstall({ appId: cfg.steamAppId });
}

async function join(id) {
  const server = serverById(id);
  if (!server) throw friendly('That server is not in the signed list.');
  const inst = await installState();
  if (inst.installed === false) return { needInstall: true, steam: inst.steam };
  const method = prefs.joinMethod;
  const url = method === 'quick' ? ST.quickJoinUrl(server.address, server.port, cfg.steamAppId) : `steam://rungameid/${cfg.steamAppId}`;
  clipboard.writeText(server.address);
  await openSteam(url);
  showCoach(server, method);
  return { method, id: server.id, name: server.name, address: server.address, port: server.port };
}

// The always-on-top helper card: where to paste if the game opens at its menu instead of joining.
let coach = null;
let coachTimer = null;
function showCoach(server, method) {
  if (coach && !coach.isDestroyed()) coach.close();
  const area = screen.getPrimaryDisplay().workArea;
  const w = 400;
  const h = method === 'quick' ? 250 : 300;
  coach = new BrowserWindow({
    width: w,
    height: h,
    x: area.x + area.width - w - 24,
    y: area.y + area.height - h - 24,
    frame: false,
    resizable: false,
    movable: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    show: false,
    backgroundColor: '#120d0a',
    title: 'Realm: how to join',
    webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false, spellcheck: false }
  });
  coach.setAlwaysOnTop(true, 'screen-saver');
  coach.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  coach.webContents.on('will-navigate', (e) => e.preventDefault());
  coach.loadFile(path.join(ROOT, 'renderer', 'coach.html'), { query: { mode: method, name: server.name, host: server.address, port: String(server.port) } });
  coach.once('ready-to-show', () => coach && coach.showInactive());
  clearTimeout(coachTimer);
  coachTimer = setTimeout(() => coach && !coach.isDestroyed() && coach.close(), 3 * 60 * 1000);
  coach.on('closed', () => {
    coach = null;
  });
}

// ---------- deep links ----------

let pendingLink = null;

function handleLinkArg(arg) {
  if (!arg) return;
  const parsed = DL.parseDeepLink(arg, list.servers.map((s) => s.id));
  if (!parsed) {
    push({ type: 'deeplink', error: 'That realm:// link is not valid, or names a server that is not in the signed list. Nothing was started.' });
    return;
  }
  pendingLink = parsed.id;
  push({ type: 'deeplink', id: parsed.id });
  showWindow();
}

// ---------- tray and notifications ----------

let tray = null;
let notifyTimer = null;
const lastEventIds = {};

function iconPath() {
  return path.join(ROOT, 'build', 'icon.png');
}

function updateTray() {
  if (prefs.background && !tray) {
    tray = new Tray(nativeImage.createFromPath(iconPath()).resize({ width: 16, height: 16 }));
    tray.setToolTip('Realm');
    tray.setContextMenu(
      Menu.buildFromTemplate([
        { label: 'Open Realm', click: showWindow },
        { label: 'Join best server', click: () => joinBest().catch(() => {}) },
        { type: 'separator' },
        { label: 'Quit', click: () => { quitting = true; app.quit(); } }
      ])
    );
    tray.on('click', showWindow);
  } else if (!prefs.background && tray) {
    tray.destroy();
    tray = null;
  }
  clearInterval(notifyTimer);
  notifyTimer = null;
  if (prefs.notifications) {
    pollEvents();
    notifyTimer = setInterval(pollEvents, 60 * 1000);
  }
}

async function pollEvents() {
  for (const s of list.servers) {
    if (!s.chronicleUrl) continue;
    try {
      const since = lastEventIds[s.id];
      const q = Number.isInteger(since) ? `since=${since}&limit=20` : 'limit=20';
      const events = JSON.parse(await fetchText(`${s.chronicleUrl}/api/events?${q}`, 256 * 1024));
      const r = NT.freshEvents(events, Number.isInteger(since) ? since : null);
      lastEventIds[s.id] = r.lastId;
      for (const e of r.events) {
        const n = NT.eventToNotification(e, s.name);
        if (n && Notification.isSupported()) {
          const note = new Notification({ title: n.title, body: n.body, silent: false });
          note.on('click', showWindow);
          note.show();
        }
      }
    } catch {
      /* the Chronicle is optional and may be offline */
    }
  }
}

// ---------- window and IPC ----------

let win = null;
let quitting = false;

function push(msg) {
  if (win && !win.isDestroyed()) win.webContents.send('realm:push', msg);
}

function showWindow() {
  if (!win) return createWindow();
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}

function friendly(message) {
  return Object.assign(new Error(message), { friendly: true });
}

function trusted(event) {
  return win && event.sender === win.webContents && event.senderFrame === win.webContents.mainFrame;
}

function handle(channel, fn) {
  ipcMain.handle(channel, async (event, ...args) => {
    if (!trusted(event)) throw new Error('untrusted sender');
    try {
      return { ok: true, data: await fn(...args) };
    } catch (e) {
      if (!e || !e.friendly) console.error(`[${channel}]`, e);
      return { ok: false, error: { message: e && e.friendly ? e.message : `Something went wrong: ${e && e.message ? e.message : e}`, details: '' } };
    }
  });
}

function asEnum(v, allowed, name) {
  if (!allowed.includes(v)) throw new TypeError(`${name} is not one of ${allowed.join(', ')}`);
  return v;
}

function asId(id) {
  if (typeof id !== 'string' || !M.ID_RE.test(id)) throw new TypeError('server id is invalid');
  return id;
}

function publicPrefs() {
  return { onboarded: prefs.onboarded, joinMethod: prefs.joinMethod, notifications: prefs.notifications, background: prefs.background };
}

function publicList() {
  return {
    realm: list.realm,
    source: list.source,
    reason: list.reason,
    seq: list.seq || null,
    issued: list.issued || null,
    expires: list.expires || null,
    keyEmbedded: !!cfg.publicKey,
    keyId: cfg.publicKey ? M.keyIdOf(cfg.publicKey) : null,
    servers: list.servers.map((s) => ({ id: s.id, name: s.name, region: s.region, address: s.address, port: s.port, maxPlayers: s.maxPlayers, hasChronicle: !!s.chronicleUrl }))
  };
}

async function joinBest() {
  if (Date.now() - statusAt > 30000) await refreshStatus();
  const best = PK.pickBest(list.servers, statuses);
  if (!best) throw friendly('No server is reachable right now. Try again in a minute.');
  return { ...(await join(best.id)), why: best.why };
}

function registerIpc() {
  handle('app:info', () => ({ version: app.isPackaged ? app.getVersion() : require('../package.json').version, platform: process.platform, dev: DEV, edition: 'player' }));
  handle('player:config', () => ({
    realmName: list.realm || cfg.realmName,
    tagline: cfg.tagline,
    links: [...(cfg.links.discord || list.links.discord ? [{ id: 'discord', label: 'Discord' }] : []), ...(cfg.links.rules || list.links.rules ? [{ id: 'rules', label: 'Rules' }] : [])]
  }));
  handle('player:servers', async (refresh) => {
    if (refresh === true) await loadServers();
    return publicList();
  });
  handle('player:status', async (refresh) => (refresh === true || Date.now() - statusAt > 15000 ? refreshStatus() : statusSnapshot()));
  handle('player:installState', () => installState());
  handle('player:join', (id) => join(asId(id)));
  handle('player:joinBest', () => joinBest());
  handle('player:installGame', async () => {
    await openSteam(`steam://install/${cfg.steamAppId}`);
    return true;
  });
  handle('player:copyAddress', (id) => {
    const s = serverById(asId(id));
    if (!s) throw friendly('That server is not in the signed list.');
    // Address only: the game's Direct Connect takes the port in its own box.
    clipboard.writeText(String(s.address));
    return `${s.address} (port ${s.port})`;
  });
  handle('player:openLink', async (id) => {
    asEnum(id, ['discord', 'rules', 'steam'], 'link');
    const url = id === 'steam' ? 'https://store.steampowered.com/about/' : cfg.links[id] || list.links[id] || '';
    if (!url || !M.isWebUrl(url, { httpsOnly: true })) return false;
    await shell.openExternal(url);
    return true;
  });
  handle('player:prefs', () => publicPrefs());
  handle('player:setPrefs', async (patch) => {
    if (!patch || typeof patch !== 'object' || Array.isArray(patch)) throw new TypeError('prefs must be an object');
    const next = {};
    if ('joinMethod' in patch) next.joinMethod = asEnum(patch.joinMethod, ['quick', 'classic'], 'join method');
    for (const k of ['onboarded', 'notifications', 'background']) if (k in patch) next[k] = patch[k] === true;
    await savePrefs(next);
    updateTray();
    return publicPrefs();
  });
  handle('player:takeLink', () => {
    const id = pendingLink;
    pendingLink = null;
    return id;
  });
  handle('player:window', (action) => {
    asEnum(action, ['minimize', 'maximize', 'close'], 'action');
    if (!win) return null;
    if (action === 'minimize') win.minimize();
    else if (action === 'maximize') win.isMaximized() ? win.unmaximize() : win.maximize();
    else win.close();
    return win.isMaximized();
  });
}

function createWindow() {
  win = new BrowserWindow({
    width: 1240,
    height: 800,
    minWidth: 1040,
    minHeight: 700,
    frame: false,
    backgroundColor: '#0b0907',
    show: false,
    title: 'Realm',
    icon: iconPath(),
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
  win.webContents.on('will-frame-navigate', (e) => {
    if (!e.isMainFrame) e.preventDefault();
  });
  win.on('maximize', () => push({ type: 'window', maximized: true }));
  win.on('unmaximize', () => push({ type: 'window', maximized: false }));
  win.once('ready-to-show', () => win.show());
  win.on('close', (e) => {
    if (!quitting && prefs.background && tray) {
      e.preventDefault();
      win.hide();
    }
  });
  win.on('closed', () => {
    win = null;
  });
  win.loadFile(path.join(ROOT, 'renderer', 'player.html'));
}

app.on('web-contents-created', (_e, contents) => {
  contents.on('will-attach-webview', (e) => e.preventDefault());
  contents.setWindowOpenHandler(() => ({ action: 'deny' }));
});

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', (_e, argv) => {
    showWindow();
    handleLinkArg(DL.findDeepLinkArg(argv));
  });
  app.on('open-url', (e, url) => {
    e.preventDefault();
    handleLinkArg(url);
  });
  app.whenReady().then(async () => {
    LOG.init(path.join(app.getPath('userData'), 'logs'));
    LOG.write('info', `Realm ${app.getVersion()} starting`);
    LOG.installCrashGuards();
    // The installer registers realm:// for this user (electron-builder "protocols"); this keeps the
    // registration pointing at the current exe. Never done from a development run.
    if (app.isPackaged) app.setAsDefaultProtocolClient('realm');
    await loadPrefs();
    await loadServers();
    registerIpc();
    createWindow();
    updateTray();
    const first = DL.findDeepLinkArg(process.argv);
    if (first) {
      const parsed = DL.parseDeepLink(first, list.servers.map((s) => s.id));
      if (parsed) pendingLink = parsed.id;
    }
    refreshStatus().catch(() => {});
    setInterval(() => refreshStatus().catch(() => {}), 30 * 1000);
    setInterval(() => loadServers().then(() => push({ type: 'servers' })).catch(() => {}), 30 * 60 * 1000);
  });
  app.on('before-quit', () => {
    quitting = true;
  });
  app.on('window-all-closed', () => {
    if (!prefs.background) app.quit();
  });
}
