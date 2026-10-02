'use strict';

// Realm client main process.
// - PLAY only hands steam://rungameid/344760 to the OS. Game client files are never read, patched or launched.
// - Everything that writes goes to the owner's test copy of the dedicated server (marker .realm-test-copy),
//   never to a Steam folder. Nothing here touches the firewall, the router or netsh.
// - The renderer is sandboxed and isolated; every IPC argument is validated here.

const { app, BrowserWindow, ipcMain, shell, clipboard, dialog, net, session } = require('electron');
const fs = require('fs');
const path = require('path');
const S = require('./lib/safety');
const F = require('./lib/fsops');
const R = require('./lib/realm');
const { ServerManager, findServerProcesses } = require('./lib/server-process');
const { ChronicleHost } = require('./lib/chronicle-host');
const { Settings } = require('./lib/settings');

const FETCH_TIMEOUT_MS = 4000;
const DEV = !app.isPackaged;

// Development-only overrides (ignored in the installed app): a separate profile folder and an extra
// Steam server candidate, used by the automated screen walk-through on non-Windows machines.
if (DEV && process.env.REALM_USER_DATA) app.setPath('userData', path.resolve(process.env.REALM_USER_DATA));
const DEV_STEAM_SERVER = DEV ? process.env.REALM_DEV_STEAM_SERVER || '' : '';

const RESOURCE_BASE = app.isPackaged ? process.resourcesPath : path.join(__dirname, '..');
const CHRONICLE_DIR = path.join(RESOURCE_BASE, 'chronicle');
const PLUGINS_DIR = path.join(RESOURCE_BASE, 'plugins');

// ---------- config.json / news.json (a copy next to Realm.exe overrides the bundled one) ----------

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
      if (fs.existsSync(file)) return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
    } catch (err) {
      console.error(`Could not read ${file}: ${err.message}`);
    }
  }
  return fallback;
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

function chronicleBase(value) {
  const u = parseUrl(value || DEFAULT_CHRONICLE_URL);
  if (!u || (u.protocol !== 'http:' && u.protocol !== 'https:') || u.username || u.password) return DEFAULT_CHRONICLE_URL;
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

function loadConfig() {
  const cfg = readJson('config.json', {});
  const server = cfg.server || {};
  return {
    realmName: String(cfg.realmName || 'The Realm').slice(0, 60),
    tagline: String(cfg.tagline || '').slice(0, 160),
    chronicleUrl: chronicleBase(cfg.chronicleUrl),
    pollSeconds: Math.max(5, Number(cfg.pollSeconds) || 15),
    server: {
      name: String(server.name || 'Realm').slice(0, 64),
      address: String(server.address || '127.0.0.1').slice(0, 64),
      port: Number(server.port) || 7350
    },
    steamAppId: Number.isInteger(Number(cfg.steamAppId)) && Number(cfg.steamAppId) > 0 ? Number(cfg.steamAppId) : S.STEAM_APP_ID,
    links: (Array.isArray(cfg.links) ? cfg.links : [])
      .filter((l) => l && l.id && l.label && isAllowedLinkUrl(l.url))
      .map((l) => ({ id: String(l.id), label: String(l.label), url: String(l.url) }))
  };
}

let config = loadConfig();
let settings = null;
let win = null;
let quitting = false;
const server = new ServerManager();
const chronicle = new ChronicleHost(CHRONICLE_DIR);

// ---------- helpers ----------

function push(msg) {
  if (win && !win.isDestroyed()) win.webContents.send('realm:push', msg);
}

function friendly(message, details) {
  return R.friendly(message, details);
}

function errorInfo(e, channel) {
  const isFriendly = e && e.friendly;
  const aborted = e && (e.name === 'AbortError' || e.code === 'ABORT_ERR');
  const message = aborted ? 'Cancelled. Nothing was left half-written.' : isFriendly ? e.message : `Something went wrong: ${e && e.message ? e.message : e}`;
  const details = [
    `Realm client ${app.getVersion()} on ${process.platform} ${process.arch}`,
    `Action: ${channel}`,
    `Time: ${new Date().toISOString()}`,
    `Message: ${e && e.message ? e.message : e}`,
    e && e.code ? `Code: ${e.code}` : '',
    e && e.details ? `Details:\n${e.details}` : '',
    !isFriendly && e && e.stack ? `Stack:\n${e.stack}` : ''
  ].filter(Boolean).join('\n');
  return { message, details, cancelled: !!aborted };
}

let steamCache = null;
async function steamInfo(refresh = false) {
  if (!steamCache || refresh) {
    const extra = [settings.get('steamServer'), DEV_STEAM_SERVER].filter(Boolean);
    steamCache = await R.findSteamServer({ extra });
  }
  return steamCache;
}

function rootCheck(steam) {
  return S.checkTestRoot(settings.get('testRoot'), steam ? { steamServer: steam.found, steamRoot: steam.steamPath, steamLibraries: steam.libraries } : {});
}

async function requireRoot() {
  return R.assertTestCopy(settings.get('testRoot'), await steamInfo());
}

let extCache = { at: 0, root: null, list: [] };
async function externalProcesses(root, fresh = false) {
  if (process.platform !== 'win32' || !root) return [];
  if (!fresh && extCache.root === root && Date.now() - extCache.at < 10000) return extCache.list;
  const ownPid = server.child ? server.child.pid : -1;
  const list = (await findServerProcesses(root)).filter((p) => p.pid !== ownPid);
  extCache = { at: Date.now(), root, list };
  return list;
}

// Assert-RealmServerStopped: neither our child nor any Server.exe/ROK.exe from this folder.
async function assertStopped(root) {
  if (server.isRunning()) throw friendly('The test server is running. Stop it first on the Server screen.');
  const ext = await externalProcesses(root, true);
  if (ext.length) {
    throw friendly(`A server from this folder is running outside Realm (${ext.map((p) => `${p.name}.exe pid ${p.pid}`).join(', ')}). Close it first (type quit in its window).`);
  }
}

let busy = null;
async function exclusive(label, fn) {
  if (busy) throw friendly(`Please wait: "${busy.label}" is still running.`);
  const controller = new AbortController();
  busy = { label, controller };
  push({ type: 'busy', label });
  try {
    return await fn(controller.signal);
  } finally {
    busy = null;
    push({ type: 'busy', label: null });
  }
}

function progressReporter(op) {
  let last = 0;
  return (p) => {
    const now = Date.now();
    const final = p.total && p.done >= p.total;
    if (!final && now - last < 100 && !p.text) return;
    last = now;
    push({ type: 'progress', op, ...p });
  };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function startChronicle() {
  const check = rootCheck(null);
  const dataDir = check.ok ? await R.chronicleDataDir(check.root) : path.join(app.getPath('userData'), 'no-server-data');
  const st = await chronicle.start(dataDir);
  push({ type: 'overlay', status: st });
  return st;
}

// ---------- IPC plumbing ----------

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
      return { ok: false, error: errorInfo(e, channel) };
    }
  });
}

async function getJson(url) {
  const res = await fetch(url, { signal: AbortSignal.timeout(FETCH_TIMEOUT_MS), headers: { Accept: 'application/json' } });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

async function displayServer() {
  const check = rootCheck(null);
  const out = { ...config.server };
  if (check.ok) {
    const cfg = await R.readServerSettings(check.root);
    if (cfg.exists) {
      if (cfg.serverName) out.name = cfg.serverName;
      if (/^\d+$/.test(cfg.portNumber || '')) out.port = Number(cfg.portNumber);
      if (/^\d+$/.test(cfg.maxPlayers || '')) out.maxPlayers = Number(cfg.maxPlayers);
    }
  }
  return out;
}

function linkUrl(id) {
  if (id === 'discord') {
    const fromSettings = settings.get('discordUrl');
    if (fromSettings) return fromSettings;
    const l = config.links.find((x) => x.id === 'discord');
    return l && !/your-invite/.test(l.url) ? l.url : null;
  }
  if (id === 'chronicle') return `${chronicle.url}/realm`;
  if (id === 'overlay') return `${chronicle.url}/overlay`;
  const l = config.links.find((x) => x.id === id);
  return l ? l.url : null;
}

function readNews() {
  const custom = settings.get('news');
  if (Array.isArray(custom)) return custom;
  const news = readJson('news.json', []);
  try {
    return S.validateNews(Array.isArray(news) ? news.slice(0, 20) : []);
  } catch {
    return [];
  }
}

// CrownAndConsequences.json (plugin data): council names, open claims and active decrees only.
// Steam ids and everything else in the file are dropped.
async function readCrownData() {
  const dir = chronicle.dataDir;
  if (!dir) return null;
  const raw = await F.readJsonFile(path.join(dir, 'CrownAndConsequences.json'), null);
  if (!raw || typeof raw !== 'object') return null;
  const s = (v, n = 64) => (typeof v === 'string' ? v.slice(0, n) : null);
  const council = raw.CouncilNames && typeof raw.CouncilNames === 'object'
    ? Object.entries(raw.CouncilNames).slice(0, 12).map(([seat, name]) => ({ seat: String(seat).slice(0, 48), name: s(name) })).filter((c) => c.name)
    : [];
  const claims = Array.isArray(raw.Claims)
    ? raw.Claims.filter((c) => c && (c.Status === 'pending' || c.Status === 'active')).slice(0, 10)
        .map((c) => ({ house: s(c.House), declaredBy: s(c.DeclaredBy), status: s(c.Status, 16), windowStart: s(c.WindowStart, 40), windowEnd: s(c.WindowEnd, 40) }))
    : [];
  const decrees = Array.isArray(raw.ActiveDecrees)
    ? raw.ActiveDecrees.slice(0, 10).map((d) => ({ id: s(d && d.Id, 48), expiresAt: s(d && d.ExpiresAt, 40) })).filter((d) => d.id)
    : [];
  return { council, claims, decrees, authority: Number.isFinite(raw.Authority) ? Math.round(raw.Authority) : null };
}

// ---------- setup wizard ----------

const STEP_IDS = ['steam', 'copy', 'firstRun', 'download', 'install', 'plugins'];
const STEP_LABELS = { steam: 'Finding the server', copy: 'Copying the server', firstRun: 'First start', download: 'Downloading Oxide', install: 'Installing Oxide', plugins: 'Deploying plugins' };
const shaCache = new Map();

async function zipVerified(file) {
  try {
    const st = await fs.promises.stat(file);
    const key = `${file}:${st.size}:${st.mtimeMs}`;
    if (!shaCache.has(key)) shaCache.set(key, (await R.verifyOxideZip(file)).ok);
    return shaCache.get(key);
  } catch {
    return false;
  }
}

async function setupStatus() {
  const steam = await steamInfo(true);
  const check = rootCheck(steam);
  const root = check.ok ? check.root : null;
  const copied = !!root && (await R.isTestCopy(root));
  const cfgExists = copied && (await F.isFile(R.cfgPaths(root).server));
  const oxide = copied ? await R.oxideInstalled(root) : null;
  const zip = root ? path.join(R.downloadsDir(root), S.OXIDE.fileName) : null;
  const zipOk = !!oxide || (!!zip && (await zipVerified(zip)));
  let pluginsDone = false;
  let pluginDetail = '';
  if (copied) {
    try {
      const plan = await R.planPlugins(root, PLUGINS_DIR);
      pluginsDone = plan.items.every((i) => i.state === 'unchanged');
      pluginDetail = plan.target;
    } catch (e) {
      pluginDetail = e.message;
    }
  }
  const steps = {
    steam: { done: !!steam.found || copied, detail: steam.found || (copied ? 'Not needed: the test copy already exists.' : 'Not found yet.') },
    copy: { done: copied, detail: root || check.reason },
    firstRun: { done: cfgExists, detail: root ? path.join(root, 'Configuration', 'ServerSettings.cfg') : '' },
    download: { done: zipOk, detail: oxide ? `Oxide is already installed (${oxide}).` : zip || '' },
    install: { done: !!oxide, detail: oxide ? `${oxide}\\Managed` : '' },
    plugins: { done: pluginsDone, detail: pluginDetail }
  };
  return {
    testRoot: settings.get('testRoot'),
    rootOk: check.ok,
    rootReason: check.ok ? null : check.reason,
    steamFound: steam.found,
    steamCandidates: steam.candidates.slice(0, 12),
    steps,
    allDone: STEP_IDS.every((id) => steps[id].done),
    setupComplete: settings.get('setupComplete')
  };
}

async function runFirstStart(signal, report) {
  const root = await requireRoot();
  const cfgFile = R.cfgPaths(root).server;
  if (await F.isFile(cfgFile)) return { skipped: true };
  await assertStopped(root);
  report({ text: 'Starting the server once so it creates its settings files. If Windows asks about the firewall, choose Cancel.', indeterminate: true });
  await server.start(root, settings.get('serverExe'));
  const t0 = Date.now();
  let cfgAt = null;
  let shownLine = 0;
  try {
    for (;;) {
      F.throwIfAborted(signal);
      // Mirror the newest console line so a question from the server is visible; it can be
      // answered from the Server screen's command box.
      const last = server.lines.filter((l) => l.src !== 'sys').pop();
      if (last && last.id !== shownLine) {
        shownLine = last.id;
        report({ text: `Server says: ${last.text.slice(0, 160)}`, indeterminate: true });
      }
      if (!server.isRunning()) {
        const tail = server.lines.slice(-40).map((l) => l.text).join('\n');
        throw friendly('The server closed before it created its settings file. Open the Server screen to read its console, then press Retry.', tail);
      }
      if (!cfgAt && (await F.isFile(cfgFile))) {
        cfgAt = Date.now();
        report({ text: 'Settings file created. Letting the server finish loading...', indeterminate: true });
      }
      if (cfgAt && (server.ready || Date.now() - cfgAt > 30000)) break;
      if (Date.now() - t0 > 20 * 60 * 1000) throw friendly('The server ran for 20 minutes without creating Configuration\\ServerSettings.cfg. Check the Server screen console, then press Retry.');
      await sleep(500);
    }
  } catch (e) {
    if (server.isRunning()) {
      server.stop();
      if (!(await server.waitForExit(30000))) await server.forceStop();
    }
    throw e;
  }
  report({ text: 'Stopping the server (sent "quit")...', indeterminate: true });
  server.stop();
  if (!(await server.waitForExit(90000))) {
    report({ text: 'The server did not close in time; force stopping it (fresh world, nothing to lose).', indeterminate: true });
    await server.forceStop();
    await server.waitForExit(15000);
  }
  return { skipped: false };
}

async function runSetupStep(step, signal) {
  const report = progressReporter(`setup:${step}`);
  if (step === 'steam') {
    const steam = await steamInfo(true);
    const check = rootCheck(steam);
    if (check.ok && (await R.isTestCopy(check.root))) return { skipped: true };
    if (!steam.found) {
      throw friendly(
        "Could not find the Reign Of Kings Dedicated Server. In Steam open Library > Tools, install 'Reign Of Kings Dedicated Server', then press Retry. Or press 'Choose folder' and pick its folder yourself.",
        `Looked in:\n${steam.candidates.join('\n')}\nSteam path from registry: ${steam.steamPath || '(none)'}`
      );
    }
    return { found: steam.found };
  }
  if (step === 'copy') {
    const steam = await steamInfo();
    const check = rootCheck(steam);
    if (!check.ok) throw friendly(check.reason + ' Change the test folder in Settings.');
    if (await R.isTestCopy(check.root)) return { skipped: true };
    if (!steam.found) throw friendly('Find the Steam server first (step 1).');
    return R.createTestCopy(steam.found, check.root, {
      signal,
      steam,
      onProgress: (p) =>
        report(p.phase === 'measure' ? { text: 'Measuring the server folder...', indeterminate: true } : { done: p.done, total: p.total, text: `Copying ${S.formatBytes(p.done)} of ${S.formatBytes(p.total)}` })
    });
  }
  if (step === 'firstRun') return runFirstStart(signal, report);
  if (step === 'download') {
    const root = await requireRoot();
    if (await R.oxideInstalled(root)) return { skipped: true };
    const file = path.join(R.downloadsDir(root), S.OXIDE.fileName);
    report({ text: 'Connecting to GitHub...', indeterminate: true });
    let res;
    try {
      res = await R.downloadOxide(file, {
        fetchImpl: (url, opts) => net.fetch(url, opts),
        signal,
        onProgress: (p) => report({ done: p.done, total: p.total, text: `Downloading ${S.formatBytes(p.done)} of ${S.formatBytes(p.total)}` })
      });
    } catch (e) {
      if (e.friendly || e.name === 'AbortError') throw e;
      throw friendly(
        `Oxide could not be downloaded (${e.message}). Check that this PC is online, then press Retry. You can also download Oxide.ReignOfKings.zip from the 2.0.3867 GitHub release yourself and save it as ${file}; Realm checks its SHA-256 before using it.`,
        `URL: ${S.OXIDE.url}\n${e.stack || e.message}`
      );
    }
    report({ text: 'SHA-256 verified.', done: 1, total: 1 });
    return res;
  }
  if (step === 'install') {
    const root = await requireRoot();
    if (await R.oxideInstalled(root)) return { skipped: true };
    await assertStopped(root);
    const file = path.join(R.downloadsDir(root), S.OXIDE.fileName);
    if (!(await F.isFile(file))) throw friendly('The Oxide download is missing. Run the download step again.');
    return R.installOxide(root, file, { signal, onProgress: (p) => report({ done: p.done, total: p.total, text: `Extracting ${S.formatBytes(p.done)} of ${S.formatBytes(p.total)}` }) });
  }
  if (step === 'plugins') {
    const root = await requireRoot();
    const res = await R.deployPlugins(root, PLUGINS_DIR);
    await startChronicle();
    return { copied: res.copied, target: res.target, items: res.items.map((i) => ({ name: i.name, state: i.state })), others: res.others };
  }
  throw new Error('unknown step');
}

// ---------- IPC handlers ----------

function registerIpc() {
  handle('app:info', () => ({ version: app.getVersion(), platform: process.platform, dev: DEV }));

  handle('realm:config', async () => ({
    realmName: config.realmName,
    tagline: config.tagline,
    pollSeconds: config.pollSeconds,
    steamAppId: config.steamAppId,
    server: await displayServer(),
    links: [
      ...(linkUrl('discord') ? [{ id: 'discord', label: 'Discord' }] : []),
      { id: 'chronicle', label: 'Chronicle' }
    ]
  }));

  handle('realm:news', () => readNews());

  handle('realm:state', async () => getJson(`${config.chronicleUrl}/api/state`));

  handle('realm:events', async (since) => {
    const sinceId = Number.isInteger(since) && since > 0 && since < 1e9 ? since : 0;
    const query = sinceId > 0 ? `since=${sinceId}&limit=50` : 'limit=20';
    const body = await getJson(`${config.chronicleUrl}/api/events?${query}`);
    return Array.isArray(body) ? body : [];
  });

  handle('realm:realm', async () => {
    let state = null;
    let stateError = null;
    try {
      state = await getJson(`${config.chronicleUrl}/api/state`);
    } catch (e) {
      stateError = e.message;
    }
    return { state, stateError, crown: await readCrownData() };
  });

  handle('realm:copyAddress', async () => {
    const s = await displayServer();
    const text = `${s.address}:${s.port}`;
    clipboard.writeText(text);
    return text;
  });

  handle('realm:play', async () => {
    // Generic Steam URL: launches the player's own copy, passes no connect info.
    await openExternalAllowed(`steam://rungameid/${config.steamAppId}`);
    return true;
  });

  handle('realm:openLink', async (id) => {
    S.asEnum(id, ['discord', 'chronicle', 'overlay'], 'link');
    const url = linkUrl(id);
    if (!url) return false;
    await openExternalAllowed(url);
    return true;
  });

  handle('realm:copyText', (text) => {
    clipboard.writeText(S.asString(text, 20000, 'text'));
    return true;
  });

  handle('realm:window', (action) => {
    S.asEnum(action, ['minimize', 'maximize', 'close'], 'action');
    if (!win) return null;
    if (action === 'minimize') win.minimize();
    else if (action === 'maximize') win.isMaximized() ? win.unmaximize() : win.maximize();
    else win.close();
    return win.isMaximized();
  });

  // ----- settings -----
  handle('settings:get', async () => {
    const check = rootCheck(null);
    const cfg = check.ok ? await R.readServerSettings(check.root) : { exists: false };
    return {
      testRoot: settings.get('testRoot'),
      testRootOk: check.ok,
      testRootReason: check.ok ? null : check.reason,
      serverExe: settings.get('serverExe'),
      discordUrl: settings.get('discordUrl'),
      steamServer: settings.get('steamServer'),
      news: readNews(),
      newsCustom: Array.isArray(settings.get('news')),
      cfg,
      backups: check.ok ? R.backupsDir(check.root) : null
    };
  });

  handle('settings:save', async (input) => {
    if (!input || typeof input !== 'object' || Array.isArray(input)) throw new TypeError('settings must be an object');
    const { ok, values, errors } = S.validateSettings(input);
    if (!ok) throw friendly(errors.join(' '));
    const result = { saved: [], cfgChanges: [], needsSetup: false };
    const patch = {};
    if ('testRoot' in values && values.testRoot !== settings.get('testRoot')) {
      const steam = await steamInfo();
      const check = S.checkTestRoot(values.testRoot, { steamServer: steam.found, steamRoot: steam.steamPath, steamLibraries: steam.libraries });
      if (!check.ok) throw friendly(check.reason);
      if (server.isRunning()) throw friendly('Stop the server before changing the test folder.');
      patch.testRoot = check.root;
      if (!(await R.isTestCopy(check.root))) {
        patch.setupComplete = false;
        result.needsSetup = true;
      }
    }
    if ('serverExe' in values) patch.serverExe = values.serverExe;
    if ('discordUrl' in values) patch.discordUrl = values.discordUrl;
    if (Object.keys(patch).length) {
      await settings.update(patch);
      result.saved.push(...Object.keys(patch));
    }
    if ('testRoot' in patch) await startChronicle();

    const cfgWanted = {};
    if ('serverName' in values) cfgWanted.serverName = values.serverName;
    if ('maxPlayers' in values) cfgWanted.maxPlayers = values.maxPlayers;
    if ('port' in values) cfgWanted.port = values.port;
    if (Object.keys(cfgWanted).length) {
      const root = await requireRoot();
      const current = await R.readServerSettings(root);
      const differs =
        current.exists &&
        ((cfgWanted.serverName != null && cfgWanted.serverName !== current.serverName) ||
          (cfgWanted.maxPlayers != null && String(cfgWanted.maxPlayers) !== current.maxPlayers) ||
          (cfgWanted.port != null && String(cfgWanted.port) !== current.portNumber));
      if (differs) {
        await assertStopped(root);
        const r = await R.writeServerSettings(root, cfgWanted);
        result.cfgChanges = r.changes;
        result.cfgMissing = r.missing;
      } else if (!current.exists) {
        throw friendly('ServerSettings.cfg does not exist yet. Run the setup (it starts the server once to create it), then save again.');
      }
    }
    return result;
  });

  handle('settings:saveNews', async (list) => {
    if (list === null) {
      await settings.update({ news: null });
      return readNews();
    }
    const clean = S.validateNews(list);
    await settings.update({ news: clean });
    return clean;
  });

  handle('settings:browse', async (kind) => {
    S.asEnum(kind, ['testRoot', 'steamServer'], 'kind');
    const res = await dialog.showOpenDialog(win, {
      title: kind === 'testRoot' ? 'Choose a folder for the test copy (not inside Steam, not on C:)' : 'Choose the Reign Of Kings Dedicated Server folder',
      properties: ['openDirectory', 'createDirectory', 'dontAddToRecent'],
      defaultPath: kind === 'testRoot' ? path.dirname(settings.get('testRoot')) : settings.get('steamServer') || undefined
    });
    if (res.canceled || !res.filePaths || !res.filePaths[0]) return null;
    const picked = res.filePaths[0];
    if (kind === 'steamServer') {
      if (!(await R.hasServerExe(picked))) throw friendly(`${picked} has neither Server.exe nor ROK.exe. Pick the "Reign Of Kings Dedicated Server" folder.`);
      await settings.update({ steamServer: picked });
      steamCache = null;
    }
    return picked;
  });

  handle('settings:openFolder', async (kind) => {
    S.asEnum(kind, ['server', 'backups', 'logs'], 'kind');
    const root = await requireRoot();
    const dir = kind === 'server' ? root : kind === 'backups' ? R.backupsDir(root) : path.join(root, 'Logs');
    if (!(await F.isDir(dir))) throw friendly(`${dir} does not exist yet.`);
    const err = await shell.openPath(dir);
    if (err) throw friendly(err);
    return true;
  });

  // ----- setup wizard -----
  handle('setup:status', () => setupStatus());
  handle('setup:run', (step) => {
    S.asEnum(step, STEP_IDS, 'step');
    return exclusive(`Setup: ${STEP_LABELS[step]}`, (signal) => runSetupStep(step, signal));
  });
  handle('setup:cancel', () => {
    if (busy) busy.controller.abort();
    return !!busy;
  });
  handle('setup:finish', async () => {
    await settings.update({ setupComplete: true });
    await startChronicle();
    return true;
  });

  // ----- server -----
  handle('server:status', async () => {
    const check = rootCheck(null);
    const root = check.ok ? check.root : null;
    const copied = !!root && (await R.isTestCopy(root));
    return {
      ...server.status(),
      testRoot: root,
      copied,
      external: copied ? await externalProcesses(root) : [],
      oxide: copied ? await R.oxideInstalled(root) : null,
      oxideBackup: copied ? !!(await R.latestOxideBackup(root)) : false,
      cfgExists: copied ? await F.isFile(R.cfgPaths(root).server) : false,
      busy: busy ? busy.label : null
    };
  });

  handle('server:log', (since) => server.getLines(Number.isInteger(since) && since >= 0 ? since : 0).slice(-1500));

  handle('server:start', async () => {
    const root = await requireRoot();
    await assertStopped(root);
    if (busy) throw friendly(`Please wait: "${busy.label}" is still running.`);
    const cfg = await R.applyLocalOnlyConfig(root);
    for (const r of cfg) {
      if (r.missingFile) server.log('sys', 'ServerSettings.cfg does not exist yet (first run): the server creates it. Restart once afterwards so the local-only settings are applied.');
      for (const c of r.changes) server.log('sys', `${path.basename(r.file)}: ${c.key} '${c.from}' -> '${c.to}' (previous file backed up)`);
      for (const k of r.missing) server.log('sys', `${path.basename(r.file)}: key ${k} not found; left unchanged.`);
    }
    const st = await server.start(root, settings.get('serverExe'));
    startChronicle().catch(() => {});
    return st;
  });

  handle('server:stop', () => server.stop());
  handle('server:force', () => server.forceStop());
  handle('server:restart', async () => {
    if (server.isRunning()) {
      server.stop();
      if (!(await server.waitForExit(90000))) return { needsForce: true, ...server.status() };
    }
    const root = await requireRoot();
    await assertStopped(root);
    await R.applyLocalOnlyConfig(root);
    return server.start(root, settings.get('serverExe'));
  });
  handle('server:command', (text) => server.sendCommand(S.asString(text, 200, 'command')));

  handle('server:deployPlugins', () =>
    exclusive('Update plugins', async () => {
      const root = await requireRoot();
      const res = await R.deployPlugins(root, PLUGINS_DIR);
      if (res.copied) server.log('sys', `Deployed ${res.copied} plugin file(s) to ${res.target}. Oxide reloads changed plugins while the server runs.`);
      return { copied: res.copied, target: res.target, items: res.items.map((i) => ({ name: i.name, state: i.state })), others: res.others, backupDir: res.backupDir || null };
    })
  );

  handle('server:backup', (allowRunning) =>
    exclusive('Back up world', async (signal) => {
      const root = await requireRoot();
      if (allowRunning !== true) await assertStopped(root);
      return R.createBackup(root, { signal, onProgress: progressReporter('backup') });
    })
  );

  handle('server:listBackups', async () => {
    const root = await requireRoot();
    return { dir: R.backupsDir(root), list: (await R.listBackups(root)).slice(0, 100) };
  });

  handle('server:restore', (name) =>
    exclusive('Restore backup', async (signal) => {
      S.asString(name, 120, 'backup name');
      const root = await requireRoot();
      await assertStopped(root);
      return R.restoreBackup(root, name, { signal, onProgress: progressReporter('restore') });
    })
  );

  handle('server:undoOxide', () =>
    exclusive('Undo Oxide', async () => {
      const root = await requireRoot();
      await assertStopped(root);
      return R.rollbackOxide(root);
    })
  );

  // ----- overlay -----
  handle('overlay:status', () => chronicle.status());
  handle('overlay:copyUrl', () => {
    const url = `${chronicle.url}/overlay`;
    clipboard.writeText(url);
    return url;
  });
}

// ---------- window ----------

function createWindow() {
  win = new BrowserWindow({
    width: 1320,
    height: 840,
    minWidth: 1100,
    minHeight: 720,
    frame: false,
    backgroundColor: '#0b0907',
    show: false,
    title: 'Realm',
    icon: path.join(__dirname, 'build', 'icon.png'),
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
    // The overlay preview frame may only show the local Chronicle.
    if (!e.isMainFrame && !/^http:\/\/127\.0\.0\.1:8787\//.test(e.url)) e.preventDefault();
  });
  win.on('maximize', () => push({ type: 'window', maximized: true }));
  win.on('unmaximize', () => push({ type: 'window', maximized: false }));
  win.once('ready-to-show', () => win.show());
  win.on('close', (e) => {
    if (quitting || !server.isRunning()) return;
    e.preventDefault();
    const choice = dialog.showMessageBoxSync(win, {
      type: 'question',
      buttons: ['Stop the server and close', 'Cancel'],
      defaultId: 0,
      cancelId: 1,
      title: 'Realm',
      message: 'The test server is still running.',
      detail: 'Realm will send "quit" so the world is saved, then close. If it does not close within a minute it is force stopped.'
    });
    if (choice !== 0) return;
    quitting = true;
    push({ type: 'closing' });
    server.stop();
    server.waitForExit(60000).then(async (ok) => {
      if (!ok) await server.forceStop();
      if (win) win.close();
    });
  });
  win.on('closed', () => {
    win = null;
  });
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

function wireServerEvents() {
  let pending = [];
  let timer = null;
  server.on('line', (line) => {
    pending.push(line);
    if (!timer) {
      timer = setTimeout(() => {
        push({ type: 'server-lines', lines: pending });
        pending = [];
        timer = null;
      }, 120);
    }
  });
  server.on('status', (st) => push({ type: 'server-status', status: st }));
  // Remember the fallback so the next start (and Settings) uses ROK.exe directly.
  server.on('exe-fallback', (exe) => {
    settings.update({ serverExe: exe }).catch(() => {});
  });
}

// The Chronicle sends frame-ancestors 'self'; only inside this app, and only for the overlay preview
// frame served from 127.0.0.1:8787, that directive is dropped so the preview can render.
function allowOverlayPreview() {
  session.defaultSession.webRequest.onHeadersReceived({ urls: ['http://127.0.0.1:8787/*'] }, (details, cb) => {
    if (details.resourceType !== 'subFrame') return cb({});
    const headers = { ...details.responseHeaders };
    for (const k of Object.keys(headers)) {
      if (k.toLowerCase() === 'content-security-policy') {
        headers[k] = headers[k].map((v) => v.split(';').filter((d) => !/^\s*frame-ancestors\b/i.test(d)).join(';'));
      }
    }
    cb({ responseHeaders: headers });
  });
  session.defaultSession.setPermissionRequestHandler((_wc, _perm, cb) => cb(false));
}

app.on('web-contents-created', (_e, contents) => {
  contents.on('will-attach-webview', (e) => e.preventDefault());
  contents.setWindowOpenHandler(() => ({ action: 'deny' }));
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
  app.whenReady().then(async () => {
    config = loadConfig();
    settings = new Settings(app.getPath('userData'));
    await settings.load();
    allowOverlayPreview();
    registerIpc();
    wireServerEvents();
    createWindow();
    startChronicle().catch((e) => console.error('[chronicle]', e));
  });
  app.on('before-quit', () => {
    quitting = quitting || !server.isRunning();
  });
  app.on('window-all-closed', async () => {
    await chronicle.stop().catch(() => {});
    if (server.isRunning()) await server.forceStop();
    app.quit();
  });
}
