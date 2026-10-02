'use strict';

// Realm Steward: the owner's control app (main process).
// - PLAY only hands steam:// URLs from a fixed allow-list to the OS. Game client files are never
//   read, patched or launched.
// - Everything that writes goes to the owner's test copies of the dedicated server (marker
//   .realm-test-copy), never to a Steam folder. Up to four copies ("instances") s1..s4.
// - The firewall is changed only from the Go Public screen, only after the owner clicks, confirms
//   and accepts the Windows UAC prompt, and only with rules scoped to ROK.exe and the instance ports.
//   The router is never changed (Realm only shows which ports to forward).
// - "Publish server list" signs servers.json with a key that never leaves this PC and writes it to a
//   folder. Nothing is uploaded.
// - The renderer is sandboxed and isolated; every IPC argument is validated here.
//
// The player edition ("Realm") has its own entry point (player/main.js) and contains none of this.

const { app, BrowserWindow, ipcMain, shell, clipboard, dialog, net, session, safeStorage } = require('electron');
const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const { execFile } = require('child_process');
const S = require('./lib/safety');
const F = require('./lib/fsops');
const R = require('./lib/realm');
const FL = require('./lib/fleet');
const SV = require('./lib/supervisor');
const FW = require('./lib/firewall');
const PB = require('./lib/publish');
const N = require('./lib/netcheck');
const M = require('./lib/shared/manifest');
const ST = require('./lib/shared/steam');
const A2S = require('./lib/shared/a2s');
const { ServerManager, findServerProcesses } = require('./lib/server-process');
const { ChronicleHost } = require('./lib/chronicle-host');
const { Settings } = require('./lib/settings');

const FETCH_TIMEOUT_MS = 4000;
const DEV = !app.isPackaged;

// Development-only overrides (ignored in the installed app): a separate profile folder and an extra
// Steam server candidate, used by the automated screen walk-through on non-Windows machines.
// Keep the profile folder the owner app has always used (%APPDATA%\Realm), even though the product
// is now called "Realm Steward". The player edition uses %APPDATA%\Realm Player.
app.setPath('userData', path.join(app.getPath('appData'), 'Realm'));
if (DEV && process.env.REALM_USER_DATA) app.setPath('userData', path.resolve(process.env.REALM_USER_DATA));
const DEV_STEAM_SERVER = DEV ? process.env.REALM_DEV_STEAM_SERVER || '' : '';

const RESOURCE_BASE = app.isPackaged ? process.resourcesPath : path.join(__dirname, '..');
const CHRONICLE_DIR = path.join(RESOURCE_BASE, 'chronicle');
const PLUGINS_DIR = path.join(RESOURCE_BASE, 'plugins');
const PLAYER_CONFIG_IN_REPO = path.join(__dirname, 'player', 'player-config.json');

// ---------- config.json / news.json (a copy next to Realm Steward.exe overrides the bundled one) ----------

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
      if (fs.existsSync(file)) return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
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

// The only gate to shell.openExternal: an allow-listed steam:// URL (lib/shared/steam.js) or a link URL.
function openExternalAllowed(url) {
  const ok = ST.isAllowedSteamUrl(url, config ? config.steamAppId : ST.GAME_APP_ID) || isAllowedLinkUrl(url);
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
const managers = new Map();
const supervision = new Map();
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
    `Realm Steward ${app.getVersion()} on ${process.platform} ${process.arch}`,
    `Action: ${channel}`,
    `Time: ${new Date().toISOString()}`,
    `Message: ${e && e.message ? e.message : e}`,
    e && e.code ? `Code: ${e.code}` : '',
    e && e.details ? `Details:\n${e.details}` : '',
    !isFriendly && e && e.stack ? `Stack:\n${e.stack}` : ''
  ].filter(Boolean).join('\n');
  return { message, details, cancelled: !!aborted };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let steamCache = null;
async function steamInfo(refresh = false) {
  if (!steamCache || refresh) {
    const extra = [settings.get('steamServer'), DEV_STEAM_SERVER].filter(Boolean);
    steamCache = await R.findSteamServer({ extra });
  }
  return steamCache;
}

function steamGuards(steam) {
  return steam ? { steamServer: steam.found, steamRoot: steam.steamPath, steamLibraries: steam.libraries } : {};
}

// ---------- the fleet (instances s1..s4) ----------

function fleet() {
  return FL.normalizeInstances(settings.get('instances'), settings.get('testRoot'));
}

function instOf(id) {
  if (typeof id !== 'string' || !FL.ID_RE.test(id)) throw new TypeError('server id is not one of s1, s2, s3, s4');
  const inst = fleet().find((i) => i.id === id);
  if (!inst) throw friendly(`Server ${id.slice(1)} does not exist. Add it on the Servers screen first.`);
  return inst;
}

async function saveFleet(list) {
  // s1's folder is the "Test copy folder" setting; it is not stored twice.
  await settings.update({ instances: list.map((i) => (i.id === 's1' ? { ...i, root: undefined } : i)) });
  pushFleet();
}

function rootCheckFor(inst, steam) {
  return S.checkTestRoot(inst.root, steamGuards(steam));
}

// Back-compat helpers for Server 1 (the Chronicle, Home and Settings follow it).
function rootCheck(steam) {
  return rootCheckFor(instOf('s1'), steam);
}

async function requireRootFor(inst) {
  return R.assertTestCopy(inst.root, await steamInfo());
}

function downloadsRoot() {
  const c = rootCheck(null);
  return c.ok ? c.root : settings.get('testRoot');
}

function mgr(id) {
  if (!managers.has(id)) {
    const m = new ServerManager();
    managers.set(id, m);
    wireServerEvents(id, m);
  }
  return managers.get(id);
}

function supOf(id) {
  if (!supervision.has(id)) {
    supervision.set(id, { crashes: [], streak: 0, usedSlot: null, timer: null, nextAt: null, halted: null, plannedAt: null, fallbackTimer: null, restartRequested: false, lastAction: null, lastBackup: null, lastLoadMs: null });
  }
  return supervision.get(id);
}

function anyRunning() {
  return [...managers.values()].some((m) => m.isRunning());
}

let extCache = new Map();
async function externalProcesses(id, root, fresh = false) {
  if (process.platform !== 'win32' || !root) return [];
  const c = extCache.get(id);
  if (!fresh && c && c.root === root && Date.now() - c.at < 10000) return c.list;
  const m = managers.get(id);
  const ownPid = m && m.child ? m.child.pid : -1;
  const list = (await findServerProcesses(root)).filter((p) => p.pid !== ownPid);
  extCache.set(id, { at: Date.now(), root, list });
  return list;
}

// Assert-RealmServerStopped for one instance: neither our child nor any Server.exe/ROK.exe from its folder.
async function assertStoppedInst(inst, root) {
  if (mgr(inst.id).isRunning()) throw friendly(`Server ${inst.id.slice(1)} is running. Stop it first on the Servers screen.`);
  const ext = await externalProcesses(inst.id, root, true);
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

async function startChronicle() {
  const check = rootCheck(null);
  const dataDir = check.ok ? await R.chronicleDataDir(check.root) : path.join(app.getPath('userData'), 'no-server-data');
  const st = await chronicle.start(dataDir);
  push({ type: 'overlay', status: st });
  return st;
}

async function instanceName(inst) {
  const c = rootCheckFor(inst, null);
  if (c.ok) {
    const cfg = await R.readServerSettings(c.root);
    if (cfg.exists && cfg.serverName) return cfg.serverName;
  }
  return FL.defaultName(FL.slotOf(inst.id));
}

function supSummary(id) {
  const s = supOf(id);
  const day = Date.now() - 24 * 3600 * 1000;
  return {
    halted: s.halted,
    nextRestartAt: s.nextAt,
    plannedAt: s.plannedAt,
    crashes24h: s.crashes.filter((t) => t > day).length,
    lastAction: s.lastAction,
    lastBackup: s.lastBackup
  };
}

async function fleetSummary() {
  const out = [];
  for (const inst of fleet()) {
    const m = mgr(inst.id);
    const c = rootCheckFor(inst, null);
    const copied = c.ok && (await R.isTestCopy(c.root));
    const cfg = copied ? await R.readServerSettings(c.root) : { exists: false };
    out.push({
      id: inst.id,
      slot: FL.slotOf(inst.id),
      name: (cfg.exists && cfg.serverName) || FL.defaultName(FL.slotOf(inst.id)),
      root: inst.root,
      rootOk: c.ok,
      copied,
      ports: inst.ports,
      maxPlayers: cfg.exists && /^\d+$/.test(cfg.maxPlayers || '') ? Number(cfg.maxPlayers) : null,
      network: inst.network,
      listed: inst.listed,
      autoRestart: inst.autoRestart,
      backupBeforeRestart: inst.backupBeforeRestart,
      dailyRestart: inst.dailyRestart,
      state: m.state,
      ready: m.ready,
      players: m.players.size,
      startedAt: m.startedAt,
      supervisor: supSummary(inst.id)
    });
  }
  return out;
}

function pushFleet() {
  fleetSummary().then((list) => push({ type: 'fleet', list })).catch(() => {});
}

// ---------- start / stop / supervise ----------

async function writeInstanceCfg(inst, root, restartTime) {
  const c = R.cfgPaths(root);
  const values = FL.cfgValuesFor(inst, { restartTime });
  const out = [];
  if (await F.isFile(c.server)) out.push(await R.applyCfg(c.server, values.server, c.backupDir));
  else out.push({ file: c.server, missingFile: true, changes: [], missing: [] });
  if (await F.isFile(c.console)) out.push(await R.applyCfg(c.console, values.console, c.backupDir));
  return out;
}

async function portsInUse(inst) {
  const busyPorts = [];
  for (const sock of FL.socketsOf(inst)) {
    if ((await N.probePort(sock.port, sock.proto)) === 'in-use') busyPorts.push(`${sock.proto.toUpperCase()} ${sock.port} (${sock.what})`);
  }
  return busyPorts;
}

async function startInstance(id, { reason = 'owner' } = {}) {
  const inst = instOf(id);
  const root = await requireRootFor(inst);
  await assertStoppedInst(inst, root);
  const problems = FL.fleetProblems(fleet());
  if (problems.length) throw friendly('Fix the server ports first: ' + problems.join(' '));
  const inUse = await portsInUse(inst);
  if (inUse.length) throw friendly(`Another program already uses ${inUse.join(', ')}. Stop it, or change Server ${id.slice(1)}'s ports.`);
  const m = mgr(id);
  const sup = supOf(id);
  clearTimeout(sup.fallbackTimer);
  sup.plannedAt = null;
  let restartTime = 0;
  if (inst.dailyRestart.enabled) {
    const loadSec = sup.lastLoadMs ? sup.lastLoadMs / 1000 : 0;
    const r = SV.restartTimeFor(inst.dailyRestart.time, new Date(), loadSec, { skip: sup.usedSlot });
    restartTime = r.seconds;
    sup.plannedAt = r.at;
  }
  const cfg = await writeInstanceCfg(inst, root, restartTime);
  for (const r of cfg) {
    if (r.missingFile) m.log('sys', 'ServerSettings.cfg does not exist yet (first run): the server creates it. Restart once afterwards so the Realm settings are applied.');
    for (const c of r.changes) m.log('sys', `${path.basename(r.file)}: ${c.key} '${c.from}' -> '${c.to}' (previous file backed up)`);
    for (const k of r.missing) m.log('sys', `${path.basename(r.file)}: key ${k} not found; left unchanged.`);
  }
  const restartKeyMissing = cfg.some((r) => r.missing && r.missing.includes('restartTime'));
  if (sup.plannedAt) {
    m.log('sys', `Daily restart planned for ${new Date(sup.plannedAt).toLocaleString()}${restartKeyMissing ? ' (restartTime is not in the file, so there is no in-game countdown; Realm restarts the server itself)' : '; the game announces it in chat from 60 minutes before'}.`);
    const wait = Date.parse(sup.plannedAt) - Date.now() + (restartKeyMissing ? 0 : SV.SCHEDULE_GRACE_MS);
    sup.fallbackTimer = setTimeout(() => scheduledFallback(id), Math.max(1000, wait));
  }
  if (reason === 'owner') {
    sup.halted = null;
    sup.crashes = [];
    sup.streak = 0;
  }
  clearTimeout(sup.timer);
  sup.timer = null;
  sup.nextAt = null;
  const st = await m.start(root, settings.get('serverExe'));
  if (id === 's1') startChronicle().catch(() => {});
  pushFleet();
  return st;
}

// The planned time has passed and the server is still up: restartTime did not stop it (or the key is
// missing). Send "quit"; the exit handler then backs up and starts it again.
function scheduledFallback(id) {
  const m = mgr(id);
  const sup = supOf(id);
  if (!m.isRunning() || quitting) return;
  m.log('sys', 'Daily restart: the server is still running after the planned time, so Realm is sending "quit" now.');
  sup.restartRequested = true;
  m.stop();
}

async function autoBackup(id, inst, root) {
  if (!inst.backupBeforeRestart) return;
  const m = mgr(id);
  const sup = supOf(id);
  if (busy) {
    m.log('sys', `Backup before restart skipped: "${busy.label}" is running.`);
    return;
  }
  try {
    const b = await R.createBackup(root, { label: 'auto' });
    sup.lastBackup = { name: b.name, at: new Date().toISOString(), size: b.size };
    m.log('sys', `Backed up the world before restarting: ${b.name}.`);
    const names = (await R.listBackups(root)).map((x) => x.name);
    for (const n of SV.backupsToPrune(names, 'auto', 24)) {
      await fsp.rm(path.join(R.backupsDir(root), n), { force: true });
      m.log('sys', `Removed old automatic backup ${n} (the newest 24 are kept).`);
    }
  } catch (e) {
    m.log('sys', `Backup before restart failed: ${e.message}. Restarting anyway.`);
  }
}

async function restartAfterExit(id, why) {
  const inst = instOf(id);
  const m = mgr(id);
  const sup = supOf(id);
  sup.lastAction = { what: why, at: new Date().toISOString() };
  try {
    const root = await requireRootFor(inst);
    await autoBackup(id, inst, root);
    if (quitting || m.isRunning()) return;
    await startInstance(id, { reason: 'supervisor' });
    m.log('sys', `${why}: server started again.`);
  } catch (e) {
    m.log('sys', `${why}: could not start the server again: ${e.message}`);
    sup.halted = `Restart failed: ${e.message}`;
  }
  pushFleet();
}

function onInstanceExit(id, ex) {
  const sup = supOf(id);
  const m = mgr(id);
  clearTimeout(sup.fallbackTimer);
  if (ex && ex.loadMs) sup.lastLoadMs = ex.loadMs;
  if (quitting || !ex || ex.error) return pushFleet();
  let inst;
  try {
    inst = instOf(id);
  } catch {
    return pushFleet();
  }
  if (sup.restartRequested || (!ex.requested && SV.isScheduledExit(sup.plannedAt, Date.now(), ex.uptimeMs))) {
    sup.restartRequested = false;
    // Each planned slot is used once: the restart below plans the next one, never this one again.
    sup.usedSlot = sup.plannedAt;
    sup.plannedAt = null;
    m.log('sys', 'Daily restart: backing up and starting the server again.');
    restartAfterExit(id, 'Daily restart');
    return;
  }
  if (ex.requested) return pushFleet();
  if (!inst.autoRestart) {
    m.log('sys', 'The server stopped without being asked to. Automatic restart is off for this server.');
    return pushFleet();
  }
  const d = SV.crashDecision(sup.crashes, Date.now(), ex.uptimeMs, sup.streak);
  sup.crashes = d.history;
  sup.streak = d.streak;
  if (d.action === 'halt') {
    sup.halted = d.reason;
    sup.nextAt = null;
    m.log('sys', `Crash-loop breaker: ${d.reason}`);
    return pushFleet();
  }
  sup.nextAt = new Date(Date.now() + d.delayMs).toISOString();
  m.log('sys', `The server stopped without being asked to (exit ${ex.code}). Restart ${d.attempt} in ${Math.round(d.delayMs / 1000)} s${inst.backupBeforeRestart ? ', after a backup' : ''}.`);
  sup.timer = setTimeout(() => {
    sup.timer = null;
    sup.nextAt = null;
    restartAfterExit(id, `Crash restart ${d.attempt}`);
  }, d.delayMs);
  pushFleet();
}

function cancelSupervision(id) {
  const sup = supOf(id);
  clearTimeout(sup.timer);
  clearTimeout(sup.fallbackTimer);
  sup.timer = null;
  sup.nextAt = null;
  sup.restartRequested = false;
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
  const inst = instOf('s1');
  const check = rootCheck(null);
  const out = { ...config.server, port: inst.ports.game };
  if (check.ok) {
    const cfg = await R.readServerSettings(check.root);
    if (cfg.exists) {
      if (cfg.serverName) out.name = cfg.serverName;
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

// ---------- setup wizard (per instance) ----------

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

function oxideZipPath() {
  return path.join(R.downloadsDir(downloadsRoot()), S.OXIDE.fileName);
}

async function setupStatus(id = 's1') {
  const inst = instOf(id);
  const steam = await steamInfo(true);
  const check = rootCheckFor(inst, steam);
  const root = check.ok ? check.root : null;
  const copied = !!root && (await R.isTestCopy(root));
  const cfgExists = copied && (await F.isFile(R.cfgPaths(root).server));
  const oxide = copied ? await R.oxideInstalled(root) : null;
  const zip = oxideZipPath();
  const zipOk = !!oxide || (await zipVerified(zip));
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
    instance: id,
    instanceName: await instanceName(inst),
    testRoot: inst.root,
    rootOk: check.ok,
    rootReason: check.ok ? null : check.reason,
    steamFound: steam.found,
    steamCandidates: steam.candidates.slice(0, 12),
    steps,
    allDone: STEP_IDS.every((sid) => steps[sid].done),
    setupComplete: id === 's1' ? settings.get('setupComplete') : copied && cfgExists
  };
}

async function runFirstStart(signal, report, inst) {
  const root = await requireRootFor(inst);
  const m = mgr(inst.id);
  const cfgFile = R.cfgPaths(root).server;
  if (await F.isFile(cfgFile)) return { skipped: true };
  await assertStoppedInst(inst, root);
  // Until its settings file exists, a new copy uses the game's default ports (7350/27015), which
  // Server 1 also uses. Keep the others stopped for this one short run.
  const others = fleet().filter((i) => i.id !== inst.id && mgr(i.id).isRunning());
  if (others.length) throw friendly(`Stop ${others.map((i) => `Server ${i.id.slice(1)}`).join(' and ')} for this first start: a new copy uses the default ports until it has written its settings.`);
  report({ text: 'Starting the server once so it creates its settings files. If Windows asks about the firewall, choose Cancel.', indeterminate: true });
  await m.start(root, settings.get('serverExe'));
  const t0 = Date.now();
  let cfgAt = null;
  let shownLine = 0;
  try {
    for (;;) {
      F.throwIfAborted(signal);
      // Mirror the newest console line so a question from the server is visible; it can be
      // answered from the Servers screen's command box.
      const last = m.lines.filter((l) => l.src !== 'sys').pop();
      if (last && last.id !== shownLine) {
        shownLine = last.id;
        report({ text: `Server says: ${last.text.slice(0, 160)}`, indeterminate: true });
      }
      if (!cfgAt && (await F.isFile(cfgFile))) {
        cfgAt = Date.now();
        report({ text: 'Settings file created. Letting the server finish loading...', indeterminate: true });
      }
      if (!m.isRunning()) {
        // [DEC] On its very first run the game writes ServerSettings.cfg and exits by itself
        // ("This is the first time you have run this server."). That is a success.
        if (cfgAt || (await F.isFile(cfgFile))) return { skipped: false, exitedByItself: true };
        const tail = m.lines.slice(-40).map((l) => l.text).join('\n');
        throw friendly('The server closed before it created its settings file. Open the Servers screen to read its console, then press Retry.', tail);
      }
      if (cfgAt && (m.ready || Date.now() - cfgAt > 30000)) break;
      if (Date.now() - t0 > 20 * 60 * 1000) throw friendly('The server ran for 20 minutes without creating Configuration\\ServerSettings.cfg. Check the Servers screen console, then press Retry.');
      await sleep(500);
    }
  } catch (e) {
    if (m.isRunning()) {
      m.stop();
      if (!(await m.waitForExit(30000))) await m.forceStop();
    }
    throw e;
  }
  report({ text: 'Stopping the server (sent "quit")...', indeterminate: true });
  m.stop();
  if (!(await m.waitForExit(90000))) {
    report({ text: 'The server did not close in time; force stopping it (fresh world, nothing to lose).', indeterminate: true });
    await m.forceStop();
    await m.waitForExit(15000);
  }
  return { skipped: false };
}

async function runSetupStep(step, signal, id = 's1') {
  const inst = instOf(id);
  const report = progressReporter(`setup:${step}`);
  if (step === 'steam') {
    const steam = await steamInfo(true);
    const check = rootCheckFor(inst, steam);
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
    const check = rootCheckFor(inst, steam);
    if (!check.ok) throw friendly(check.reason + (id === 's1' ? ' Change the test folder in Settings.' : ' Remove this server and add it again with another folder.'));
    const problems = FL.fleetProblems(fleet());
    if (problems.length) throw friendly(problems.join(' '));
    if (await R.isTestCopy(check.root)) return { skipped: true };
    if (!steam.found) throw friendly('Find the Steam server first (step 1).');
    return R.createTestCopy(steam.found, check.root, {
      signal,
      steam,
      onProgress: (p) =>
        report(p.phase === 'measure' ? { text: 'Measuring the server folder...', indeterminate: true } : { done: p.done, total: p.total, text: `Copying ${S.formatBytes(p.done)} of ${S.formatBytes(p.total)}` })
    });
  }
  if (step === 'firstRun') return runFirstStart(signal, report, inst);
  if (step === 'download') {
    const root = await requireRootFor(inst);
    if (await R.oxideInstalled(root)) return { skipped: true };
    const file = oxideZipPath();
    if (await zipVerified(file)) return { skipped: true };
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
    const root = await requireRootFor(inst);
    if (await R.oxideInstalled(root)) return { skipped: true };
    await assertStoppedInst(inst, root);
    const file = oxideZipPath();
    if (!(await F.isFile(file))) throw friendly('The Oxide download is missing. Run the download step again.');
    return R.installOxide(root, file, { signal, onProgress: (p) => report({ done: p.done, total: p.total, text: `Extracting ${S.formatBytes(p.done)} of ${S.formatBytes(p.total)}` }) });
  }
  if (step === 'plugins') {
    const root = await requireRootFor(inst);
    const res = await R.deployPlugins(root, PLUGINS_DIR);
    if (id === 's1') await startChronicle();
    return { copied: res.copied, target: res.target, items: res.items.map((i) => ({ name: i.name, state: i.state })), others: res.others };
  }
  throw new Error('unknown step');
}

// ---------- signing key (publish) ----------

function keyFile() {
  return path.join(app.getPath('userData'), 'realm-signing-key.json');
}

async function readSigningKey() {
  const raw = await F.readJsonFile(keyFile(), null);
  if (!raw || typeof raw !== 'object' || typeof raw.data !== 'string') return null;
  try {
    const pem = raw.protection === 'safeStorage' ? safeStorage.decryptString(Buffer.from(raw.data, 'base64')) : raw.data;
    const publicKey = PB.publicKeyOfPrivate(pem);
    return { pem, publicKey, protection: raw.protection, created: raw.created };
  } catch (e) {
    throw friendly(`The signing key could not be read (${e.message}). It may belong to another Windows user.`);
  }
}

async function createSigningKey() {
  const kp = PB.generateKeyPair();
  const protectedOk = safeStorage.isEncryptionAvailable();
  const record = {
    version: 1,
    created: new Date().toISOString(),
    publicKey: kp.publicKey,
    protection: protectedOk ? 'safeStorage' : 'none',
    data: protectedOk ? safeStorage.encryptString(kp.privateKeyPem).toString('base64') : kp.privateKeyPem
  };
  await F.writeFileAtomic(keyFile(), JSON.stringify(record, null, 2));
  return readSigningKey();
}

function publishPrefs() {
  const p = settings.get('publish');
  return p && typeof p === 'object' && !Array.isArray(p) ? p : {};
}

function defaultOutDir() {
  return path.join(S.realmHome(downloadsRoot()), 'publish');
}

function checkOutDir(dir) {
  if (typeof dir !== 'string' || !dir.trim() || dir.length > 240) throw friendly('Choose a folder to write the files to.');
  const d = dir.trim();
  const abs = process.platform === 'win32' ? /^[A-Za-z]:\\/.test(d) : path.isAbsolute(d);
  if (!abs) throw friendly('Use a full folder path for the output folder.');
  return path.resolve(d);
}

// ---------- IPC handlers ----------

const idArg = (id) => instOf(id === undefined ? 's1' : id);

function registerIpc() {
  handle('app:info', () => ({ version: app.getVersion(), platform: process.platform, dev: DEV, edition: 'steward' }));

  handle('realm:config', async () => ({
    realmName: config.realmName,
    tagline: config.tagline,
    pollSeconds: config.pollSeconds,
    steamAppId: config.steamAppId,
    server: await displayServer(),
    maxPlayersCap: FL.MAX_PLAYERS_CAP,
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

  // ----- settings (global; the Test copy folder is Server 1's folder) -----
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
      const check = S.checkTestRoot(values.testRoot, steamGuards(steam));
      if (!check.ok) throw friendly(check.reason);
      if (mgr('s1').isRunning()) throw friendly('Stop Server 1 before changing the test folder.');
      const clash = FL.fleetProblems(fleet().map((i) => (i.id === 's1' ? { ...i, root: check.root } : i)));
      if (clash.length) throw friendly(clash.join(' '));
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
    if (Object.keys(cfgWanted).length) {
      const inst = instOf('s1');
      const root = await requireRootFor(inst);
      const current = await R.readServerSettings(root);
      if (!current.exists) throw friendly('ServerSettings.cfg does not exist yet. Run the setup (it starts the server once to create it), then save again.');
      const differs = (cfgWanted.serverName != null && cfgWanted.serverName !== current.serverName) || (cfgWanted.maxPlayers != null && String(cfgWanted.maxPlayers) !== current.maxPlayers);
      if (differs) {
        await assertStoppedInst(inst, root);
        const r = await R.writeServerSettings(root, cfgWanted);
        result.cfgChanges = r.changes;
        result.cfgMissing = r.missing;
      }
    }
    pushFleet();
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
    S.asEnum(kind, ['testRoot', 'steamServer', 'instanceRoot', 'publishOut'], 'kind');
    const titles = {
      testRoot: 'Choose a folder for the test copy (not inside Steam, not on C:)',
      instanceRoot: 'Choose an empty folder for the new server copy (not inside Steam, not on C:)',
      steamServer: 'Choose the Reign Of Kings Dedicated Server folder',
      publishOut: 'Choose where to write servers.json'
    };
    const res = await dialog.showOpenDialog(win, {
      title: titles[kind],
      properties: ['openDirectory', 'createDirectory', 'dontAddToRecent'],
      defaultPath: kind === 'steamServer' ? settings.get('steamServer') || undefined : kind === 'publishOut' ? publishPrefs().outDir || defaultOutDir() : path.dirname(settings.get('testRoot'))
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

  handle('settings:openFolder', async (kind, id) => {
    S.asEnum(kind, ['server', 'backups', 'logs', 'publish'], 'kind');
    let dir;
    if (kind === 'publish') dir = publishPrefs().outDir || defaultOutDir();
    else {
      const root = await requireRootFor(idArg(id));
      dir = kind === 'server' ? root : kind === 'backups' ? R.backupsDir(root) : path.join(root, 'Logs');
    }
    if (!(await F.isDir(dir))) throw friendly(`${dir} does not exist yet.`);
    const err = await shell.openPath(dir);
    if (err) throw friendly(err);
    return true;
  });

  // ----- setup wizard -----
  handle('setup:status', (id) => setupStatus(idArg(id).id));
  handle('setup:run', (step, id) => {
    S.asEnum(step, STEP_IDS, 'step');
    const inst = idArg(id);
    const label = inst.id === 's1' ? `Setup: ${STEP_LABELS[step]}` : `Setup Server ${inst.id.slice(1)}: ${STEP_LABELS[step]}`;
    return exclusive(label, (signal) => runSetupStep(step, signal, inst.id));
  });
  handle('setup:cancel', () => {
    if (busy) busy.controller.abort();
    return !!busy;
  });
  handle('setup:finish', async (id) => {
    const inst = idArg(id);
    if (inst.id === 's1') {
      await settings.update({ setupComplete: true });
      await startChronicle();
    }
    pushFleet();
    return true;
  });

  // ----- fleet -----
  handle('fleet:list', () => fleetSummary());

  handle('fleet:add', async (input) => {
    const list = fleet();
    if (list.length >= FL.MAX_INSTANCES) throw friendly(`Realm runs at most ${FL.MAX_INSTANCES} servers on one PC.`);
    const slot = FL.nextFreeSlot(list);
    const s1 = list.find((i) => i.id === 's1');
    let root = FL.defaultRoot(slot, s1.root, process.platform);
    if (input && typeof input === 'object' && input.root != null) root = S.asString(input.root, 240, 'folder').trim();
    const steam = await steamInfo();
    const check = S.checkTestRoot(root, steamGuards(steam));
    if (!check.ok) throw friendly(check.reason);
    const inst = FL.makeInstance(slot, { root: check.root });
    const problems = FL.fleetProblems([...list, inst]);
    if (problems.length) throw friendly(problems.join(' '));
    await saveFleet([...list, inst]);
    return { id: inst.id, root: inst.root, ports: inst.ports };
  });

  handle('fleet:remove', async (id) => {
    const inst = idArg(id);
    if (inst.id === 's1') throw friendly('Server 1 is the main server and cannot be removed.');
    if (mgr(inst.id).isRunning()) throw friendly('Stop this server before removing it.');
    cancelSupervision(inst.id);
    await saveFleet(fleet().filter((i) => i.id !== inst.id));
    return { removed: inst.id, folderKept: inst.root };
  });

  handle('fleet:update', async (id, patch) => {
    const inst = idArg(id);
    if (!patch || typeof patch !== 'object' || Array.isArray(patch)) throw new TypeError('changes must be an object');
    const next = { ...inst, ports: { ...inst.ports }, dailyRestart: { ...inst.dailyRestart } };
    const result = { saved: [], cfgChanges: [], cfgMissing: [] };
    if (patch.ports && typeof patch.ports === 'object') {
      if ('game' in patch.ports) next.ports.game = S.asInt(patch.ports.game, 1024, 65535, 'Game port');
      if ('query' in patch.ports) {
        next.ports.query = S.asInt(patch.ports.query, 1024, 65534, 'Query port');
        next.ports.rcon = next.ports.query + 1;
      }
    }
    if ('autoRestart' in patch) next.autoRestart = patch.autoRestart === true;
    if ('backupBeforeRestart' in patch) next.backupBeforeRestart = patch.backupBeforeRestart === true;
    if (patch.dailyRestart && typeof patch.dailyRestart === 'object') {
      if ('enabled' in patch.dailyRestart) next.dailyRestart.enabled = patch.dailyRestart.enabled === true;
      if ('time' in patch.dailyRestart) {
        if (!FL.isTime(patch.dailyRestart.time)) throw friendly('Daily restart time must look like 06:00.');
        next.dailyRestart.time = patch.dailyRestart.time;
      }
    }
    const list = fleet().map((i) => (i.id === inst.id ? next : i));
    const problems = FL.fleetProblems(list);
    if (problems.length) throw friendly(problems.join(' '));
    const portsChanged = next.ports.game !== inst.ports.game || next.ports.query !== inst.ports.query;
    if (portsChanged && mgr(inst.id).isRunning()) throw friendly('Stop the server before changing its ports.');
    await saveFleet(list);
    result.saved.push('instance');

    // Values that live in the server's own ServerSettings.cfg (existing keys only).
    if (patch.cfg && typeof patch.cfg === 'object') {
      const values = {};
      if ('serverName' in patch.cfg) {
        const v = S.validateSettings({ serverName: patch.cfg.serverName });
        if (!v.ok) throw friendly(v.errors.join(' '));
        values.serverName = v.values.serverName;
      }
      if ('maxPlayers' in patch.cfg) values.maxPlayers = String(S.asInt(patch.cfg.maxPlayers, 1, FL.MAX_PLAYERS_CAP, 'Max players'));
      if ('joinPacing' in patch.cfg && patch.cfg.joinPacing !== '' && patch.cfg.joinPacing != null) {
        values.timeBetweenPlayerJoin = String(S.asInt(patch.cfg.joinPacing, 1, 30, 'Seconds between joins'));
      }
      if (Object.keys(values).length) {
        const root = await requireRootFor(next);
        const c = R.cfgPaths(root);
        if (!(await F.isFile(c.server))) throw friendly('ServerSettings.cfg does not exist yet. Run this server\'s setup first.');
        const { text } = S.decodeText(await fsp.readFile(c.server));
        const differs = Object.entries(values).some(([k, v]) => S.getCfgValue(text, k) !== v);
        if (differs) {
          await assertStoppedInst(next, root);
          const r = await R.applyCfg(c.server, values, c.backupDir);
          result.cfgChanges = r.changes;
          result.cfgMissing = r.missing;
        }
      }
    }
    pushFleet();
    return result;
  });

  handle('fleet:instance', async (id) => {
    const inst = idArg(id);
    const c = rootCheckFor(inst, null);
    let cfg = { exists: false };
    if (c.ok && (await F.isFile(R.cfgPaths(c.root).server))) {
      const { text } = S.decodeText(await fsp.readFile(R.cfgPaths(c.root).server));
      const g = (k) => S.getCfgValue(text, k);
      cfg = { exists: true, serverName: g('serverName'), maxPlayers: g('maxPlayers'), joinPacing: g('timeBetweenPlayerJoin'), restartTime: g('restartTime'), bindIP: g('bindIP'), isPrivate: g('isPrivate') };
    }
    return { ...inst, cfg, maxPlayersCap: FL.MAX_PLAYERS_CAP, sockets: FL.socketsOf(inst), supervisor: supSummary(inst.id) };
  });

  // ----- servers (per instance) -----
  handle('server:status', async (id) => {
    const inst = idArg(id);
    const m = mgr(inst.id);
    const check = rootCheckFor(inst, null);
    const root = check.ok ? check.root : null;
    const copied = !!root && (await R.isTestCopy(root));
    return {
      ...m.status(),
      id: inst.id,
      testRoot: root || inst.root,
      copied,
      external: copied ? await externalProcesses(inst.id, root) : [],
      oxide: copied ? await R.oxideInstalled(root) : null,
      oxideBackup: copied ? !!(await R.latestOxideBackup(root)) : false,
      cfgExists: copied ? await F.isFile(R.cfgPaths(root).server) : false,
      network: inst.network,
      ports: inst.ports,
      supervisor: supSummary(inst.id),
      busy: busy ? busy.label : null
    };
  });

  handle('server:log', (id, since) => mgr(idArg(id).id).getLines(Number.isInteger(since) && since >= 0 ? since : 0).slice(-1500));
  handle('server:start', async (id) => {
    if (busy) throw friendly(`Please wait: "${busy.label}" is still running.`);
    return startInstance(idArg(id).id, { reason: 'owner' });
  });
  handle('server:stop', (id) => {
    const inst = idArg(id);
    cancelSupervision(inst.id);
    const st = mgr(inst.id).stop();
    pushFleet();
    return st;
  });
  handle('server:force', (id) => {
    const inst = idArg(id);
    cancelSupervision(inst.id);
    return mgr(inst.id).forceStop();
  });
  handle('server:restart', async (id) => {
    const inst = idArg(id);
    const m = mgr(inst.id);
    cancelSupervision(inst.id);
    if (m.isRunning()) {
      m.stop();
      if (!(await m.waitForExit(90000))) return { needsForce: true, ...m.status() };
    }
    return startInstance(inst.id, { reason: 'owner' });
  });
  handle('server:command', (id, text) => mgr(idArg(id).id).sendCommand(S.asString(text, 200, 'command')));

  handle('server:deployPlugins', (target) =>
    exclusive('Update plugins', async () => {
      const list = target === 'all' ? fleet() : [idArg(target)];
      let copied = 0;
      const targets = [];
      let last = null;
      for (const inst of list) {
        const c = rootCheckFor(inst, null);
        if (!c.ok || !(await R.isTestCopy(c.root))) continue;
        const res = await R.deployPlugins(c.root, PLUGINS_DIR);
        copied += res.copied;
        targets.push(res.target);
        last = res;
        if (res.copied) mgr(inst.id).log('sys', `Deployed ${res.copied} plugin file(s) to ${res.target}. Oxide reloads changed plugins while the server runs.`);
      }
      if (!last) throw friendly('No server copy is set up yet.');
      return { copied, servers: targets.length, target: targets.join(', '), items: last.items.map((i) => ({ name: i.name, state: i.state })), others: last.others, backupDir: last.backupDir || null };
    })
  );

  handle('server:backup', (id, allowRunning) =>
    exclusive('Back up world', async (signal) => {
      const inst = idArg(id);
      const root = await requireRootFor(inst);
      if (allowRunning !== true) await assertStoppedInst(inst, root);
      return R.createBackup(root, { signal, onProgress: progressReporter('backup') });
    })
  );
  handle('server:listBackups', async (id) => {
    const root = await requireRootFor(idArg(id));
    return { dir: R.backupsDir(root), list: (await R.listBackups(root)).slice(0, 100) };
  });
  handle('server:restore', (id, name) =>
    exclusive('Restore backup', async (signal) => {
      S.asString(name, 120, 'backup name');
      const inst = idArg(id);
      const root = await requireRootFor(inst);
      await assertStoppedInst(inst, root);
      return R.restoreBackup(root, name, { signal, onProgress: progressReporter('restore') });
    })
  );
  handle('server:undoOxide', (id) =>
    exclusive('Undo Oxide', async () => {
      const inst = idArg(id);
      const root = await requireRootFor(inst);
      await assertStoppedInst(inst, root);
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

  // ----- go public -----
  const rokExe = (root) => path.win32.join(root, 'ROK.exe');

  async function firewallState(inst) {
    if (process.platform !== 'win32') return { supported: false, present: [] };
    const out = await new Promise((resolve) => {
      execFile('powershell.exe', FW.plainArgs(FW.listScript(inst.id)), { windowsHide: true, timeout: 20000 }, (err, stdout) => resolve(err ? '' : stdout));
    });
    return { supported: true, present: FW.parseList(out) };
  }

  handle('public:status', async (id) => {
    const inst = idArg(id);
    const m = mgr(inst.id);
    const c = rootCheckFor(inst, null);
    const program = process.platform === 'win32' ? rokExe(c.ok ? c.root : inst.root) : path.join(c.ok ? c.root : inst.root, 'ROK.exe');
    let programError = null;
    if (process.platform === 'win32') {
      try {
        FW.checkProgram(program);
      } catch (e) {
        programError = `Realm will not add rules for ${program}: ${e.message}.`;
      }
    }
    return {
      id: inst.id,
      name: await instanceName(inst),
      network: inst.network,
      listed: inst.listed,
      ports: inst.ports,
      sockets: FL.socketsOf(inst),
      rules: FW.ruleTable(inst),
      program,
      programError,
      firewall: await firewallState(inst),
      lan: N.lanAddresses(),
      running: m.isRunning(),
      ready: m.ready,
      steam: m.steam,
      platform: process.platform
    };
  });

  handle('public:setNetwork', async (id, input) => {
    const inst = idArg(id);
    if (!input || typeof input !== 'object') throw new TypeError('network settings must be an object');
    S.asEnum(input.network, ['local', 'public'], 'network');
    const next = { ...inst, network: input.network, listed: input.network === 'public' && input.listed === true };
    await saveFleet(fleet().map((i) => (i.id === inst.id ? next : i)));
    return { network: next.network, listed: next.listed, appliesAtNextStart: mgr(inst.id).isRunning() };
  });

  async function runElevated(script, what) {
    if (process.platform !== 'win32') throw friendly('Windows Firewall rules can only be changed on Windows.');
    return new Promise((resolve, reject) => {
      execFile('powershell.exe', FW.elevatedArgs(script), { windowsHide: true, timeout: 120000 }, (err, stdout) => {
        const code = err && typeof err.code === 'number' ? err.code : err ? -1 : 0;
        if (code === 0) return resolve(true);
        if (code === 1223) return reject(friendly(`${what} was cancelled: Windows did not get permission (the UAC prompt was declined). Nothing was changed.`));
        reject(friendly(`${what} failed (code ${code}).`, String(stdout || (err && err.message) || '')));
      });
    });
  }

  handle('public:firewallAdd', async (id) => {
    const inst = idArg(id);
    const root = await requireRootFor(inst);
    const exe = rokExe(root);
    const rules = FW.rulesFor(inst, exe);
    const choice = await dialog.showMessageBox(win, {
      type: 'question',
      buttons: ['Add these rules', 'Cancel'],
      defaultId: 1,
      cancelId: 1,
      title: 'Realm Steward: Windows Firewall',
      message: `Add ${rules.length} Windows Firewall rules for Server ${inst.id.slice(1)}?`,
      detail: rules.map((r) => `${r.action} inbound ${r.protocol} ${r.ports} for ${r.program}  (${r.name})`).join('\n') + '\n\nWindows will ask for administrator permission next. "Remove rules" undoes exactly these.'
    });
    if (choice.response !== 0) throw Object.assign(friendly('Cancelled. The firewall was not changed.'), { cancelled: true });
    await runElevated(FW.addScript(inst, exe), 'Adding the firewall rules');
    return firewallState(inst);
  });

  handle('public:firewallRemove', async (id) => {
    const inst = idArg(id);
    await runElevated(FW.removeScript(inst.id), 'Removing the firewall rules');
    return firewallState(inst);
  });

  handle('public:selfTest', async (id) => {
    const inst = idArg(id);
    const m = mgr(inst.id);
    const checks = [];
    const add = (label, status, detail) => checks.push({ label, status, detail });
    const c = rootCheckFor(inst, null);
    if (!m.isRunning()) add('Server running', 'bad', 'Start the server first; the other checks need it running.');
    else add('Server running', m.ready ? 'ok' : 'warn', m.ready ? 'Loaded.' : 'Still loading. Run the checks again in a minute.');
    if (c.ok && (await F.isFile(R.cfgPaths(c.root).server))) {
      const cfg = await R.readServerSettings(c.root);
      const want = FL.cfgValuesFor(inst).server;
      if (cfg.bindIP === want.bindIP) add('Network setting', 'ok', `bindIP = '${cfg.bindIP}' (${inst.network === 'public' ? 'all network cards' : 'this PC only'}).`);
      else add('Network setting', 'warn', `bindIP is '${cfg.bindIP}' but Realm wants '${want.bindIP}'. Restart the server to apply it.`);
    }
    if (m.isRunning()) {
      for (const sock of FL.socketsOf(inst)) {
        const st = await N.probePort(sock.port, sock.proto);
        add(`${sock.proto.toUpperCase()} ${sock.port} (${sock.what})`, st === 'in-use' ? 'ok' : 'warn', st === 'in-use' ? 'The server has this port open.' : 'Nothing is listening here yet (UNVERIFIED which sockets the game opens before it has fully loaded).');
      }
      const local = await A2S.queryInfo('127.0.0.1', inst.ports.query, { timeoutMs: 1500 });
      add('Steam query on this PC', local.ok ? 'ok' : 'warn', local.ok ? `Answered in ${local.rttMs} ms as "${local.info.name}" (the game always reports this name).` : `No A2S answer on UDP ${inst.ports.query} (${local.error}). UNVERIFIED that the game answers A2S at all; players' ping display needs it.`);
      const lan = N.lanAddresses()[0];
      if (lan && inst.network === 'public') {
        const r = await A2S.queryInfo(lan.address, inst.ports.query, { timeoutMs: 1500 });
        add(`Steam query on ${lan.address}`, r.ok ? 'ok' : 'warn', r.ok ? `Answered on the LAN address in ${r.rttMs} ms.` : `No answer on the LAN address (${r.error}). Check the firewall rules.`);
      }
    }
    if (m.steam) {
      const ip = m.steam.publicIp;
      add('Steam sign-in', m.steam.logged && m.steam.secure ? 'ok' : 'warn', `The game reports Logged: ${m.steam.logged}, Secure: ${m.steam.secure}.`);
      if (N.isCgnat(ip)) add('Public address', 'bad', `${ip} is a carrier-grade NAT address. Port forwarding cannot work from this connection; ask your provider for a public IPv4, or host on a VPS.`);
      else if (N.isPrivateIPv4(ip)) add('Public address', 'warn', `Steam sees ${ip}, a private address. Check the router.`);
      else {
        add('Public address', 'ok', `Steam sees this PC as ${ip}. Players connect to that address (or a DNS name pointing at it).`);
        const t = await N.tcpConnect(ip, inst.ports.game, 2500);
        add(`TCP ${inst.ports.game} via ${ip}`, t.ok ? 'ok' : 'warn', t.ok ? 'Reached the ping port through the public address.' : `Not reachable from inside your own network (${t.error}). Many routers do not loop back, so ask a friend outside your network to test with the Realm player app.`);
      }
    } else if (m.isRunning()) {
      add('Public address', 'warn', 'The game has not printed "Steam game server started" yet, so its public address is unknown.');
    }
    if (process.platform === 'win32') {
      const fw = await firewallState(inst);
      add('Firewall rules', fw.present.length >= 4 ? 'ok' : inst.network === 'public' ? 'warn' : 'skip', fw.present.length ? `${fw.present.length} Realm rule(s) found.` : 'No Realm rules yet.');
    } else add('Firewall rules', 'skip', 'Only checked on Windows.');
    return { at: new Date().toISOString(), checks };
  });

  // ----- publish server list -----
  handle('publish:status', async () => {
    const key = await readSigningKey().catch((e) => ({ error: e.message }));
    const prefs = publishPrefs();
    const saved = prefs.servers && typeof prefs.servers === 'object' ? prefs.servers : {};
    const servers = [];
    for (const inst of fleet()) {
      const sv = saved[inst.id] || {};
      const c = rootCheckFor(inst, null);
      const cfg = c.ok ? await R.readServerSettings(c.root) : { exists: false };
      const m = mgr(inst.id);
      servers.push({
        id: inst.id,
        include: sv.include !== false,
        name: sv.name || (cfg.exists && cfg.serverName) || FL.defaultName(FL.slotOf(inst.id)),
        region: sv.region || '',
        address: sv.address || (m.steam && !N.isPrivateIPv4(m.steam.publicIp) ? m.steam.publicIp : ''),
        port: inst.ports.game,
        queryPort: inst.ports.query,
        maxPlayers: cfg.exists && /^\d+$/.test(cfg.maxPlayers || '') ? Math.min(Number(cfg.maxPlayers), 1000) : FL.MAX_PLAYERS_CAP,
        chronicleUrl: sv.chronicleUrl || '',
        network: inst.network
      });
    }
    return {
      key: key && !key.error ? { publicKey: key.publicKey, keyId: M.keyIdOf(key.publicKey), protection: key.protection, created: key.created } : null,
      keyError: key && key.error ? key.error : null,
      canProtect: safeStorage.isEncryptionAvailable(),
      realm: prefs.realm || config.realmName,
      tagline: config.tagline,
      manifestUrl: prefs.manifestUrl || '',
      outDir: prefs.outDir || defaultOutDir(),
      validDays: Number.isInteger(prefs.validDays) ? prefs.validDays : 30,
      lastSeq: Number.isInteger(prefs.seq) ? prefs.seq : 0,
      lastWritten: prefs.lastWritten || null,
      discordUrl: settings.get('discordUrl') || '',
      rulesUrl: prefs.rulesUrl || '',
      servers,
      canEmbed: DEV && fs.existsSync(path.dirname(PLAYER_CONFIG_IN_REPO))
    };
  });

  handle('publish:createKey', async (replace) => {
    const existing = await readSigningKey().catch(() => null);
    if (existing && replace !== true) throw friendly('A signing key already exists.');
    if (existing) {
      const choice = await dialog.showMessageBox(win, {
        type: 'warning',
        buttons: ['Replace the key', 'Cancel'],
        defaultId: 1,
        cancelId: 1,
        title: 'Realm Steward',
        message: 'Replace the signing key?',
        detail: 'Player builds made with the old public key will refuse lists signed with the new key. You would have to give players a new player installer.'
      });
      if (choice.response !== 0) throw Object.assign(friendly('Cancelled. The key was not replaced.'), { cancelled: true });
    }
    const key = await createSigningKey();
    return { publicKey: key.publicKey, keyId: M.keyIdOf(key.publicKey), protection: key.protection };
  });

  handle('publish:write', async (input) => {
    if (!input || typeof input !== 'object' || Array.isArray(input)) throw new TypeError('publish input must be an object');
    const key = await readSigningKey();
    if (!key) throw friendly('Create the signing key first.');
    const outDir = checkOutDir(input.outDir);
    const manifestUrl = typeof input.manifestUrl === 'string' ? input.manifestUrl.trim() : '';
    if (manifestUrl && !M.isWebUrl(manifestUrl, { httpsOnly: true })) throw friendly('The address where you will host servers.json must start with https://');
    const realm = S.asString(typeof input.realm === 'string' ? input.realm.trim() : '', 60, 'Realm name') || config.realmName;
    const validDays = S.asInt(input.validDays, 1, 365, 'Valid for (days)');
    const rulesUrl = typeof input.rulesUrl === 'string' ? input.rulesUrl.trim() : '';
    if (rulesUrl && !M.isWebUrl(rulesUrl, { httpsOnly: true })) throw friendly('The rules link must start with https://');
    if (!Array.isArray(input.servers) || input.servers.length > FL.MAX_INSTANCES) throw new TypeError('servers must be a list');
    const insts = fleet();
    const servers = [];
    const savedServers = {};
    for (const row of input.servers) {
      if (!row || typeof row !== 'object') continue;
      const inst = insts.find((i) => i.id === row.id);
      if (!inst) continue;
      savedServers[inst.id] = {
        include: row.include === true,
        name: typeof row.name === 'string' ? row.name.slice(0, 64) : '',
        region: typeof row.region === 'string' ? row.region.slice(0, 32) : '',
        address: typeof row.address === 'string' ? row.address.trim().slice(0, 253) : '',
        chronicleUrl: typeof row.chronicleUrl === 'string' ? row.chronicleUrl.trim().slice(0, 200) : ''
      };
      if (row.include !== true) continue;
      const entry = {
        id: inst.id,
        name: savedServers[inst.id].name.trim(),
        region: savedServers[inst.id].region.trim(),
        address: savedServers[inst.id].address,
        port: inst.ports.game,
        queryPort: inst.ports.query,
        maxPlayers: S.asInt(row.maxPlayers, 1, 1000, `Server ${inst.id.slice(1)} max players`)
      };
      if (savedServers[inst.id].chronicleUrl) entry.chronicleUrl = savedServers[inst.id].chronicleUrl;
      if (/^(127\.|localhost$)/i.test(entry.address)) throw friendly(`Server ${inst.id.slice(1)}: ${entry.address} only works on this PC. Use your public address or a DNS name.`);
      servers.push(entry);
    }
    if (!servers.length) throw friendly('Tick at least one server to list.');
    const prefs = publishPrefs();
    const seq = (Number.isInteger(prefs.seq) ? prefs.seq : 0) + 1;
    const links = {};
    if (settings.get('discordUrl')) links.discord = settings.get('discordUrl');
    if (rulesUrl) links.rules = rulesUrl;
    const manifest = PB.buildManifest({ realm, seq, servers, links, validDays });
    const envelope = PB.signManifest(manifest, key.pem);
    // Self-check with the public key exactly as a player build will.
    const check = M.verifyEnvelope(envelope, key.publicKey);
    if (!check.ok) throw new Error(`self-check failed: ${check.reason}`);
    const pc = PB.playerConfig({ realmName: realm, tagline: config.tagline, manifestUrl, publicKey: key.publicKey, envelope, links: { discord: links.discord, rules: rulesUrl } });
    await fsp.mkdir(outDir, { recursive: true });
    const files = {
      servers: path.join(outDir, 'servers.json'),
      playerConfig: path.join(outDir, 'player-config.json'),
      readme: path.join(outDir, 'PUBLISH-README.txt')
    };
    await F.writeFileAtomic(files.servers, JSON.stringify(envelope, null, 2) + '\n');
    await F.writeFileAtomic(files.playerConfig, JSON.stringify(pc, null, 2) + '\n');
    await F.writeFileAtomic(
      files.readme,
      [
        `Realm server list, version ${seq}, signed ${manifest.issued}, valid until ${manifest.expires}.`,
        `Public key (embedded in the player build): ${key.publicKey}`,
        `Key id: ${M.keyIdOf(key.publicKey)}`,
        '',
        '1. Upload servers.json to the https address you entered' + (manifestUrl ? ` (${manifestUrl})` : '') + ', for example GitHub Pages or a public gist (use the "raw" link).',
        '2. Before building the player installer, copy player-config.json to launcher\\player\\player-config.json, then run: npm run dist:player',
        '3. Publish a new servers.json before the expiry date above. Players keep using the last good list for a while, but an expired list is shown as stale.',
        '',
        'Never share realm-signing-key.json from the Realm Steward profile folder. Anyone with it can point players at other servers.'
      ].join('\r\n') + '\r\n'
    );
    let embedded = null;
    if (input.embed === true && DEV && fs.existsSync(path.dirname(PLAYER_CONFIG_IN_REPO))) {
      await F.writeFileAtomic(PLAYER_CONFIG_IN_REPO, JSON.stringify(pc, null, 2) + '\n');
      embedded = PLAYER_CONFIG_IN_REPO;
    }
    await settings.update({ publish: { ...prefs, seq, realm, manifestUrl, outDir, validDays, rulesUrl, servers: savedServers, lastWritten: new Date().toISOString() } });
    return { files, embedded, seq, expires: manifest.expires, servers: servers.length, keyId: envelope.keyId };
  });

  handle('publish:copyKey', async () => {
    const key = await readSigningKey();
    if (!key) throw friendly('Create the signing key first.');
    clipboard.writeText(key.publicKey);
    return key.publicKey;
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
    title: 'Realm Steward',
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
    if (quitting || !anyRunning()) return;
    e.preventDefault();
    const running = [...managers.entries()].filter(([, m]) => m.isRunning()).map(([id]) => `Server ${id.slice(1)}`);
    const choice = dialog.showMessageBoxSync(win, {
      type: 'question',
      buttons: ['Stop the servers and close', 'Cancel'],
      defaultId: 0,
      cancelId: 1,
      title: 'Realm Steward',
      message: `${running.join(', ')} ${running.length === 1 ? 'is' : 'are'} still running.`,
      detail: 'Realm sends "quit" so each world is saved, then closes. A server that has not closed within a minute is force stopped. Automatic restarts only happen while Realm Steward is open.'
    });
    if (choice !== 0) return;
    quitting = true;
    push({ type: 'closing' });
    for (const id of managers.keys()) cancelSupervision(id);
    Promise.all(
      [...managers.values()].filter((m) => m.isRunning()).map(async (m) => {
        m.stop();
        if (!(await m.waitForExit(60000))) await m.forceStop();
      })
    ).then(() => {
      if (win) win.close();
    });
  });
  win.on('closed', () => {
    win = null;
  });
  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));
}

function wireServerEvents(id, m) {
  let pending = [];
  let timer = null;
  m.on('line', (line) => {
    pending.push(line);
    if (!timer) {
      timer = setTimeout(() => {
        push({ type: 'server-lines', id, lines: pending });
        pending = [];
        timer = null;
      }, 120);
    }
  });
  m.on('status', (st) => {
    push({ type: 'server-status', id, status: { ...st, supervisor: supSummary(id) } });
    pushFleet();
  });
  m.on('exit', (ex) => onInstanceExit(id, ex));
  // Remember the fallback so the next start (and Settings) uses ROK.exe directly.
  m.on('exe-fallback', (exe) => {
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
    for (const inst of fleet()) mgr(inst.id);
    allowOverlayPreview();
    registerIpc();
    createWindow();
    startChronicle().catch((e) => console.error('[chronicle]', e));
  });
  app.on('before-quit', () => {
    quitting = quitting || !anyRunning();
  });
  app.on('window-all-closed', async () => {
    quitting = true;
    await chronicle.stop().catch(() => {});
    for (const m of managers.values()) if (m.isRunning()) await m.forceStop();
    app.quit();
  });
}
