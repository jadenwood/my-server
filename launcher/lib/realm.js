'use strict';

// Realm server operations, ported from server/*.ps1 to Node. Every function that writes to a
// server folder goes through assertTestCopy first; the Steam copy is only ever read.
// Nothing here touches the firewall, the router, netsh or the game client.

const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const { execFile } = require('child_process');
const { Readable } = require('stream');
const { pipeline } = require('stream/promises');
const S = require('./safety');
const F = require('./fsops');
const Z = require('./zip');

const SPACE_MARGIN = 2 * 1024 ** 3;

function friendly(message, details) {
  const e = new Error(message);
  e.friendly = true;
  if (details) e.details = details;
  return e;
}

// ---------- Steam discovery ----------

function runFile(file, args, execFileImpl = execFile) {
  return new Promise((resolve) => {
    execFileImpl(file, args, { windowsHide: true, timeout: 8000 }, (err, stdout) => resolve(err ? '' : String(stdout || '')));
  });
}

async function hasServerExe(dir) {
  return (await F.isFile(path.join(dir, 'Server.exe'))) || (await F.isFile(path.join(dir, 'ROK.exe')));
}

// Default G:\ location first, then HKCU\Software\Valve\Steam SteamPath and every library in
// steamapps\libraryfolders.vdf (Find-SteamServer in Realm.ps1).
async function findSteamServer({ platform = process.platform, execFileImpl, extra = [] } = {}) {
  let steamPath = null;
  let libraries = [];
  if (platform === 'win32') {
    steamPath = S.parseRegSteamPath(await runFile('reg', ['query', 'HKCU\\Software\\Valve\\Steam', '/v', 'SteamPath'], execFileImpl));
    if (steamPath) {
      for (const vdf of [path.join(steamPath, 'steamapps', 'libraryfolders.vdf'), path.join(steamPath, 'config', 'libraryfolders.vdf')]) {
        try {
          libraries = S.parseLibraryFolders(await fsp.readFile(vdf, 'utf8'));
          if (libraries.length) break;
        } catch {
          /* not there */
        }
      }
    }
  }
  const candidates = [...extra.filter(Boolean), ...S.steamServerCandidates(steamPath, libraries)];
  let found = null;
  for (const c of candidates) {
    if (await hasServerExe(c)) {
      found = c;
      break;
    }
  }
  return { found, candidates, steamPath, libraries };
}

// ---------- test copy ----------

async function assertTestCopy(root, steam = {}) {
  const check = S.checkTestRoot(root, { steamServer: steam.found, steamRoot: steam.steamPath, steamLibraries: steam.libraries });
  if (!check.ok) throw friendly(check.reason);
  if (!(await F.isDir(check.root))) throw friendly(`The test copy folder ${check.root} does not exist yet. Run the setup first.`);
  if (!(await F.isFile(path.join(check.root, S.MARKER_NAME)))) {
    throw friendly(`Refusing: ${check.root} has no ${S.MARKER_NAME} marker, so Realm did not create it. Realm only changes folders it copied itself.`);
  }
  return check.root;
}

async function isTestCopy(root) {
  try {
    return (await F.isFile(path.join(root, S.MARKER_NAME))) && (await F.isDir(root));
  } catch {
    return false;
  }
}

async function measureSource(src, signal) {
  const files = await F.listFiles(src, signal);
  return { files, bytes: files.reduce((s, f) => s + f.size, 0) };
}

// New-TestServer.ps1: copy (never move) into a hidden staging folder beside the destination,
// then rename it into place. A failed or cancelled copy removes the staging folder.
async function createTestCopy(src, dest, { onProgress, signal, steam = {} } = {}) {
  const source = path.resolve(src);
  if (!(await F.isDir(source))) throw friendly(`The Steam server folder was not found: ${source}`);
  if (!(await hasServerExe(source))) throw friendly(`Neither Server.exe nor ROK.exe is in ${source}. Is this the Reign Of Kings Dedicated Server folder?`);
  const check = S.checkTestRoot(dest, { steamServer: source, steamRoot: steam.steamPath, steamLibraries: steam.libraries });
  if (!check.ok) throw friendly(check.reason);
  const target = check.root;

  if (await F.isDir(target)) {
    const items = await fsp.readdir(target);
    if (items.length) {
      if (items.includes(S.MARKER_NAME)) return { skipped: true, root: target };
      throw friendly(`${target} already has files but no ${S.MARKER_NAME} marker. Pick an empty folder for the test copy.`);
    }
  } else if (await F.exists(target)) {
    throw friendly(`${target} exists and is not a folder.`);
  }

  onProgress && onProgress({ phase: 'measure', done: 0, total: 0 });
  const { files, bytes } = await measureSource(source, signal);
  const free = await F.freeBytes(target);
  if (free < bytes + SPACE_MARGIN) {
    throw friendly(`Not enough free space: the copy needs ${S.formatBytes(bytes)} plus a ${S.formatBytes(SPACE_MARGIN)} margin, and the drive has ${S.formatBytes(free)} free.`);
  }

  const parent = path.dirname(target);
  const createdParents = [];
  for (let p = parent; !(await F.exists(p)); p = path.dirname(p)) {
    createdParents.unshift(p);
    if (path.dirname(p) === p) break;
  }
  await fsp.mkdir(parent, { recursive: true });
  const staging = path.join(parent, `.realm-copy-${S.timestamp()}`);
  try {
    await F.copyTree(source, staging, { files, signal, onProgress: (p) => onProgress && onProgress({ phase: 'copy', ...p }) });
    await fsp.writeFile(
      path.join(staging, S.MARKER_NAME),
      ['Realm test copy. Created by the Realm client; Realm only writes to folders with this file.', `source=${source}`, `copied=${new Date().toISOString()}`, ''].join('\r\n'),
      'ascii'
    );
    if (await F.isDir(target)) await fsp.rmdir(target);
    await fsp.rename(staging, target);
  } catch (e) {
    await F.removeTree(staging).catch(() => {});
    for (const p of createdParents.reverse()) await fsp.rmdir(p).catch(() => {});
    throw e;
  }
  return { skipped: false, root: target, files: files.length, bytes };
}

// ---------- cfg ----------

function cfgPaths(root) {
  const dir = path.join(root, 'Configuration');
  return {
    server: path.join(dir, 'ServerSettings.cfg'),
    console: path.join(dir, 'ConsoleSettings.cfg'),
    backupDir: path.join(root, '_realm-backups', 'config')
  };
}

// Set-RealmCfgValues on disk: existing keys only, previous file copied to backupDir first.
async function applyCfg(file, values, backupDir) {
  const buf = await fsp.readFile(file);
  const { text, encoding } = S.decodeText(buf);
  const result = S.rewriteCfg(text, values);
  if (result.changes.length) {
    await fsp.mkdir(backupDir, { recursive: true });
    await fsp.copyFile(file, path.join(backupDir, `${path.basename(file)}.${S.timestamp()}.bak`));
    await F.writeFileAtomic(file, S.encodeText(result.text, encoding));
  }
  return { file, changes: result.changes, missing: result.missing };
}

// Start-LocalServer.ps1 defaults, applied before every start when the files exist.
async function applyLocalOnlyConfig(root) {
  const c = cfgPaths(root);
  const out = [];
  if (await F.isFile(c.server)) out.push(await applyCfg(c.server, S.LOCAL_SERVER_SETTINGS, c.backupDir));
  else out.push({ file: c.server, missingFile: true, changes: [], missing: [] });
  if (await F.isFile(c.console)) out.push(await applyCfg(c.console, S.LOCAL_CONSOLE_SETTINGS, c.backupDir));
  return out;
}

async function readServerSettings(root) {
  const c = cfgPaths(root);
  try {
    const { text } = S.decodeText(await fsp.readFile(c.server));
    const get = (k) => S.getCfgValue(text, k);
    return { exists: true, serverName: get('serverName'), maxPlayers: get('maxPlayers'), portNumber: get('portNumber'), bindIP: get('bindIP'), isPrivate: get('isPrivate') };
  } catch {
    return { exists: false };
  }
}

async function writeServerSettings(root, { serverName, maxPlayers, port }) {
  const c = cfgPaths(root);
  if (!(await F.isFile(c.server))) throw friendly('ServerSettings.cfg does not exist yet. Start the server once (the setup does this) so the game creates it.');
  const values = {};
  if (serverName != null) values.serverName = String(serverName);
  if (maxPlayers != null) values.maxPlayers = String(maxPlayers);
  if (port != null) values.portNumber = String(port);
  return applyCfg(c.server, values, c.backupDir);
}

// ---------- Oxide ----------

async function managedDirs(root) {
  let entries = [];
  try {
    entries = await fsp.readdir(root, { withFileTypes: true });
  } catch {
    return [];
  }
  const out = [];
  for (const e of entries) {
    if (e.isDirectory() && /_Data$/.test(e.name) && (await F.isDir(path.join(root, e.name, 'Managed')))) out.push(e.name);
  }
  return out;
}

async function oxideInstalled(root) {
  for (const d of await managedDirs(root)) {
    if (await F.isFile(path.join(root, d, 'Managed', 'Oxide.Core.dll'))) return d;
  }
  return null;
}

const BACKUP_DIR_RE = /^oxide-\d{8}-\d{6}$/;

async function latestOxideBackup(root) {
  const base = path.join(root, '_realm-backups');
  let names = [];
  try {
    names = (await fsp.readdir(base, { withFileTypes: true })).filter((e) => e.isDirectory() && BACKUP_DIR_RE.test(e.name)).map((e) => e.name);
  } catch {
    return null;
  }
  names.sort();
  for (let i = names.length - 1; i >= 0; i--) {
    const dir = path.join(base, names[i]);
    if (await F.isFile(path.join(dir, 'install.json'))) return dir;
  }
  return null;
}

async function verifyOxideZip(zipPath, expected = S.OXIDE.sha256) {
  const hash = await F.sha256File(zipPath);
  return { ok: S.sha256Matches(hash, expected), hash };
}

// Downloads to <file>.part, checks SHA-256, then renames. A good existing file is reused.
async function downloadOxide(file, { fetchImpl, url = S.OXIDE.url, expectedSha256 = S.OXIDE.sha256, onProgress, signal } = {}) {
  if (await F.isFile(file)) {
    const v = await verifyOxideZip(file, expectedSha256);
    if (v.ok) return { skipped: true, file, hash: v.hash };
    await fsp.rm(file, { force: true });
  }
  await fsp.mkdir(path.dirname(file), { recursive: true });
  const part = file + '.part';
  try {
    const res = await fetchImpl(url, { signal, redirect: 'follow' });
    if (!res.ok || !res.body) throw friendly(`The download server answered HTTP ${res.status}. Check your internet connection and try again.`);
    const total = Number(res.headers.get('content-length')) || S.OXIDE.size;
    let done = 0;
    await pipeline(
      Readable.fromWeb(res.body),
      F.progressStream((n) => {
        done += n;
        onProgress && onProgress({ done, total });
      }, signal),
      fs.createWriteStream(part)
    );
    const v = await verifyOxideZip(part, expectedSha256);
    if (!v.ok) throw friendly(`The downloaded file failed its safety check (SHA-256 mismatch). Expected ${expectedSha256}, got ${v.hash}. Nothing was installed; try again later.`);
    await fsp.rename(part, file);
    return { skipped: false, file, hash: v.hash };
  } catch (e) {
    await fsp.rm(part, { force: true }).catch(() => {});
    throw e;
  }
}

// Install-Oxide.ps1, step for step: hash, entry checks, backup of every overwritten file plus
// install.json, then extract. If extraction fails, the backup is put back automatically.
async function installOxide(root, zipPath, { onProgress, signal, expectedSha256 = S.OXIDE.sha256 } = {}) {
  if (await oxideInstalled(root)) return { skipped: true };
  const v = await verifyOxideZip(zipPath, expectedSha256);
  if (!v.ok) throw friendly(`The Oxide zip failed its SHA-256 check (got ${v.hash}). Delete it and download again.`);
  const dataFolder = S.pickDataFolder(await managedDirs(root));
  if (!dataFolder) throw friendly(`No *_Data\\Managed folder was found in ${root}. Is this a complete copy of the dedicated server?`);

  const entries = await Z.listZip(zipPath);
  const plan = S.planOxideEntries(entries.map((e) => e.name), dataFolder);
  for (const item of plan) {
    item.dest = S.safeJoin(root, item.rel);
    item.exists = await F.exists(item.dest);
  }
  const backupDir = path.join(root, '_realm-backups', `oxide-${S.timestamp()}`);
  const overwritten = plan.filter((p) => p.exists).map((p) => p.rel.replace(/\//g, '\\'));
  const created = plan.filter((p) => !p.exists).map((p) => p.rel.replace(/\//g, '\\'));

  for (const item of plan.filter((p) => p.exists)) {
    const b = S.safeJoin(path.join(backupDir, 'files'), item.rel);
    await fsp.mkdir(path.dirname(b), { recursive: true });
    await fsp.copyFile(item.dest, b);
  }
  await fsp.mkdir(backupDir, { recursive: true });
  const manifest = { tool: 'Realm client', zip: zipPath, sha256: v.hash, dataFolder, installed: new Date().toISOString(), overwritten, created };
  await fsp.writeFile(path.join(backupDir, 'install.json'), JSON.stringify(manifest, null, 2), 'utf8');

  try {
    await Z.extractTo(zipPath, new Map(plan.map((p) => [p.entry, p.dest])), { onProgress, signal });
  } catch (e) {
    await restoreFromOxideBackup(root, backupDir, manifest).catch(() => {});
    await fsp.rename(backupDir, backupDir + '-failed').catch(() => {});
    throw e;
  }
  return { skipped: false, dataFolder, backupDir, overwritten: overwritten.length, created: created.length };
}

async function restoreFromOxideBackup(root, dir, log) {
  for (const rel of log.overwritten || []) {
    if (!rel) continue;
    const r = String(rel).replace(/\\/g, '/');
    await fsp.copyFile(S.safeJoin(path.join(dir, 'files'), r), S.safeJoin(root, r));
  }
  for (const rel of log.created || []) {
    if (!rel) continue;
    await fsp.rm(S.safeJoin(root, String(rel).replace(/\\/g, '/')), { force: true });
  }
}

async function rollbackOxide(root) {
  const dir = await latestOxideBackup(root);
  if (!dir) throw friendly('There is no Oxide install backup to undo.');
  const log = JSON.parse((await fsp.readFile(path.join(dir, 'install.json'), 'utf8')).replace(/^\uFEFF/, ''));
  await restoreFromOxideBackup(root, dir, log);
  await fsp.rename(dir, dir + '-rolledback');
  return { restored: (log.overwritten || []).length, removed: (log.created || []).length, backup: dir };
}

// ---------- plugins ----------

async function getOxideDir(root) {
  for (const rel of ['oxide', path.join('Saves', 'oxide')]) {
    const p = path.join(root, rel);
    if (await F.isDir(path.join(p, 'plugins'))) return p;
  }
  return null;
}

// Where the chronicle reads RealmChronicle.json / RealmState.json.
async function chronicleDataDir(root) {
  const options = [path.join(root, 'oxide', 'data'), path.join(root, 'Saves', 'oxide', 'data')];
  for (const p of options) if (await F.isFile(path.join(p, 'RealmState.json'))) return p;
  for (const p of options) if (await F.isDir(p)) return p;
  return options[0];
}

async function planPlugins(root, srcDir) {
  let names = [];
  try {
    names = (await fsp.readdir(srcDir)).filter((n) => /\.cs$/i.test(n));
  } catch {
    names = [];
  }
  if (!names.length) throw friendly(`No plugin files (*.cs) were found in ${srcDir}.`);
  const oxideDir = await getOxideDir(root);
  const target = path.join(oxideDir || path.join(root, 'oxide'), 'plugins');
  const items = [];
  for (const n of names.sort()) {
    const src = path.join(srcDir, n);
    const dest = path.join(target, n);
    let state = 'new';
    if (await F.isFile(dest)) state = (await F.sha256File(src)) === (await F.sha256File(dest)) ? 'unchanged' : 'changed';
    items.push({ name: n, src, dest, state });
  }
  let others = [];
  try {
    others = (await fsp.readdir(target)).filter((n) => /\.cs$/i.test(n) && !names.includes(n));
  } catch {
    others = [];
  }
  return { target, items, others };
}

// Deploy-Plugins.ps1 with -CreateTarget: changed files are saved to _realm-backups\plugins-<time>.
async function deployPlugins(root, srcDir) {
  const plan = await planPlugins(root, srcDir);
  const todo = plan.items.filter((i) => i.state !== 'unchanged');
  if (!todo.length) return { ...plan, copied: 0 };
  await fsp.mkdir(plan.target, { recursive: true });
  const backupDir = path.join(root, '_realm-backups', `plugins-${S.timestamp()}`);
  let backedUp = false;
  for (const i of todo) {
    if (i.state === 'changed') {
      await fsp.mkdir(backupDir, { recursive: true });
      await fsp.copyFile(i.dest, path.join(backupDir, i.name));
      backedUp = true;
    }
    const tmp = i.dest + '.realm-part';
    await fsp.copyFile(i.src, tmp);
    await fsp.rename(tmp, i.dest);
  }
  return { ...plan, copied: todo.length, backupDir: backedUp ? backupDir : null };
}

// ---------- plugin data files (ROADMAP STW-1) ----------
//
// Some plugins read files Realm ships rather than files they write: RealmSculptor's sculptures,
// RealmPainter's art bundle, RealmQuests' content and RealmArrival's site plan. "Update plugins" copies
// them to oxide\data next to the plugins (Deploy-Plugins.ps1 does the same on the PowerShell path). Rules:
// - every source file must parse as a JSON object first; a damaged or truncated source is refused and
//   the copy on the server is left as it is (never replaced by a broken one);
// - a changed file on the server is saved to _realm-backups\data-<time>\ before it is replaced;
// - files on the server that Realm does not ship (an owner's own sculpture) are listed, never touched;
// - each copy goes to <name>.realm-part and is renamed into place, so a crash never leaves half a file.
//
// A set may write a file under another name on the server (`as`: { source name: destination name }):
// RealmArrival reads art/sculptures/sites/arrival.json as oxide\data\RealmArrival\site.json. Backups,
// the .realm-part file, the "others" listing and the plan's rel/dest all use the destination name.
//
// In the installed Steward the files are under resources\realm-data (build/steward.json extraResources);
// in a development checkout they are read from the repository itself.
//
// The world-mood library (mods/presets, kind 'moods') is the one set that is not plugin data: it goes to
// <server>\realm-moods\ (base 'server'), laid out the way server\Set-Mood.ps1 reads -PresetsDir:
// rotation.json and <id>\<id>.cfg per mood. It is a library only. The game reads Mods\<Name>.cfg, not
// this folder, and the deploy never writes Mods\: switching the live mood stays Set-Mood.ps1's job
// (Set-Mood.ps1 -PresetsDir <server>\realm-moods -Mood <id>). The .cfg files are not JSON, so each one
// gets checkMoodFile instead: UTF-8 text, at most 64 KB, every line a comment or a complete key = 'value'
// line for one of the proven mood keys, no key twice, at least one key, and a final line break (a cut-off
// copy is refused). rotation.json must be a JSON object with "moods" and "seasonCycle". No plugin reloads.

const DATA_NAME_RE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.json$/;
const DATA_MAX_BYTES = 16 * 1024 * 1024;
const MOOD_ID_RE = /^[a-z0-9][a-z0-9-]{0,63}$/;
const MOOD_MAX_BYTES = 64 * 1024;
// The keys a mood may set: server/Set-Mood.ps1 $MoodKeys (proven in docs/mods-keys-from-dll.md); a test keeps them equal.
const MOOD_KEYS = [
  'Atmosphere.FogDensity',
  'Atmosphere.FogColor',
  'Atmosphere.SunColor',
  'Atmosphere.MoonColor',
  'Atmosphere.IslandLatitude',
  'Atmosphere.IslandLongitude',
  'Weather.ClearWeight',
  'Weather.CloudyWeight',
  'Weather.PrecipitateLowWeight',
  'Weather.PrecipitateMediumWeight',
  'Weather.PrecipitateHeavyWeight',
  'Clock.DaySpeed'
];

function dataSets(resourceBase, packaged) {
  const p = (...a) => path.join(resourceBase, ...a);
  return [
    { id: 'sculptures', label: 'Monuments', plugin: 'RealmSculptor', src: packaged ? p('realm-data', 'RealmSculptor') : p('art', 'sculptures'), dest: 'RealmSculptor', only: null, versionKey: null },
    { id: 'paintings', label: 'Sign art bundle', plugin: 'RealmPainter', src: packaged ? p('realm-data') : p('art', 'paintings'), dest: '', only: ['RealmPainterArt.json'], versionKey: 'Version' },
    { id: 'quests', label: 'Quests and deeds', plugin: 'RealmQuests', src: packaged ? p('realm-data', 'RealmQuests') : p('plugins', 'docs', 'RealmQuests', 'content'), dest: 'RealmQuests', only: null, versionKey: null },
    { id: 'arrival', label: 'Arrival site plan', plugin: 'RealmArrival', src: packaged ? p('realm-data', 'RealmArrival') : p('art', 'sculptures', 'sites'), dest: 'RealmArrival', only: ['arrival.json'], as: { 'arrival.json': 'site.json' }, versionKey: null },
    { id: 'moods', label: 'World mood presets', plugin: null, kind: 'moods', base: 'server', src: packaged ? p('realm-data', 'moods') : p('mods', 'presets'), dest: 'realm-moods', only: null, versionKey: null }
  ];
}

// The name a shipped file has on the server (the set's `as` rename, or its own name).
function dataDestName(set, name) {
  const to = set.as && Object.prototype.hasOwnProperty.call(set.as, name) ? set.as[name] : null;
  return typeof to === 'string' && DATA_NAME_RE.test(to) ? to : name;
}

// Reads and checks one shipped data file. Returns { ok, version, reason }.
async function checkDataFile(file, versionKey) {
  let text;
  try {
    const st = await fsp.stat(file);
    if (st.size > DATA_MAX_BYTES) return { ok: false, reason: `larger than ${S.formatBytes(DATA_MAX_BYTES)}` };
    text = await fsp.readFile(file, 'utf8');
  } catch (e) {
    return { ok: false, reason: e.code === 'ENOENT' ? 'missing' : e.message };
  }
  try {
    const j = JSON.parse(text.replace(/^﻿/, ''));
    if (!j || typeof j !== 'object' || Array.isArray(j)) return { ok: false, reason: 'not a JSON object' };
    const v = versionKey && (typeof j[versionKey] === 'string' || typeof j[versionKey] === 'number') ? String(j[versionKey]).slice(0, 40) : null;
    return { ok: true, version: v };
  } catch (e) {
    return { ok: false, reason: `damaged JSON (${e.message.slice(0, 80)})` };
  }
}

// Sanity check for one world-mood file's bytes (not JSON). Returns { ok, reason }.
function checkMoodText(buf) {
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(buf);
  } catch {
    return { ok: false, reason: 'not UTF-8 text' };
  }
  if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(text)) return { ok: false, reason: 'not a text file (control characters)' };
  text = text.replace(/^\uFEFF/, '');
  if (!/\n$/.test(text)) return { ok: false, reason: 'no line break at the end (cut off?)' };
  const seen = new Set();
  const lines = text.split(/\r?\n/);
  lines.pop();
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const at = `line ${i + 1}`;
    let m = /^\s*#@scale\s+(\S+)\s+=\s*'([^'#]*)'\s*$/.exec(line);
    if (!m) {
      if (/^\s*(#|$)/.test(line)) continue;
      m = /^\s*([^#=\s][^=]*?)\s+=\s*'([^'#]*)'\s*(#.*)?$/.exec(line);
      if (!m) return { ok: false, reason: `${at} is not a complete key = 'value' line` };
    }
    const key = m[1].trim();
    if (!MOOD_KEYS.includes(key)) return { ok: false, reason: `${at}: '${key.slice(0, 60)}' is not a mood key` };
    if (seen.has(key)) return { ok: false, reason: `${at}: '${key}' is set twice` };
    if (!m[2].trim()) return { ok: false, reason: `${at}: '${key}' has no value` };
    seen.add(key);
  }
  if (!seen.size) return { ok: false, reason: 'no mood lines' };
  return { ok: true };
}

async function checkMoodFile(file) {
  let buf;
  try {
    const st = await fsp.stat(file);
    if (st.size > MOOD_MAX_BYTES) return { ok: false, reason: `larger than ${S.formatBytes(MOOD_MAX_BYTES)}` };
    if (!st.size) return { ok: false, reason: 'empty' };
    buf = await fsp.readFile(file);
  } catch (e) {
    return { ok: false, reason: e.code === 'ENOENT' ? 'missing' : e.message };
  }
  const r = checkMoodText(buf);
  return r.ok ? { ok: true, version: null } : r;
}

// rotation.json: a JSON object with a "moods" object and a "seasonCycle" list (Set-Mood.ps1 -Season/-Event/-List).
async function checkMoodRotation(file) {
  const r = await checkDataFile(file, null);
  if (!r.ok) return r;
  const j = JSON.parse((await fsp.readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
  if (!j.moods || typeof j.moods !== 'object' || Array.isArray(j.moods)) return { ok: false, reason: 'no "moods" object' };
  if (!Array.isArray(j.seasonCycle)) return { ok: false, reason: 'no "seasonCycle" list' };
  return { ok: true, version: null };
}

// The shipped files of a moods set, as '/'-separated paths: rotation.json and <id>/<id>.cfg.
async function moodNames(dir) {
  let entries = [];
  try {
    entries = await fsp.readdir(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  const out = [];
  for (const e of entries) {
    if (e.isFile() && e.name === 'rotation.json') out.push(e.name);
    else if (e.isDirectory() && MOOD_ID_RE.test(e.name) && (await F.isFile(path.join(dir, e.name, `${e.name}.cfg`)))) out.push(`${e.name}/${e.name}.cfg`);
  }
  return out.sort();
}

// Files in the server's moods folder, one level deep, as '/'-separated paths (for the "others" listing).
async function moodServerFiles(dir) {
  const out = [];
  let entries = [];
  try {
    entries = await fsp.readdir(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const e of entries) {
    if (e.isFile()) out.push(e.name);
    else if (e.isDirectory()) {
      try {
        for (const f of await fsp.readdir(path.join(dir, e.name), { withFileTypes: true })) if (f.isFile()) out.push(`${e.name}/${f.name}`);
      } catch {
        /* unreadable: not listed */
      }
    }
  }
  return out.filter((n) => !n.endsWith('.realm-part')).sort();
}

async function planData(root, sets) {
  const oxideDir = (await getOxideDir(root)) || path.join(root, 'oxide');
  const dataDir = path.join(oxideDir, 'data');
  const out = { target: dataDir, items: [], sets: [] };
  for (const set of sets) {
    const moods = set.kind === 'moods';
    let names = [];
    if (moods) names = await moodNames(set.src);
    else {
      try {
        names = (await fsp.readdir(set.src)).filter((n) => DATA_NAME_RE.test(n) && /\.json$/i.test(n));
      } catch {
        names = [];
      }
    }
    if (set.only) names = names.filter((n) => set.only.includes(n));
    names.sort();
    // A set with base 'server' lives in the server folder itself, not in oxide\data.
    const baseDir = set.base === 'server' ? root : dataDir;
    const destDir = set.dest ? path.join(baseDir, set.dest) : baseDir;
    const destNames = names.map((n) => dataDestName(set, n));
    const renamed = names.map((n, i) => ({ from: n, to: destNames[i] })).filter((r) => r.from !== r.to);
    const summary = { id: set.id, label: set.label, plugin: set.plugin, dir: destDir, files: 0, changed: 0, invalid: 0, version: null, others: [], renamed, missing: !names.length };
    for (const name of names) {
      const destName = moods ? name : dataDestName(set, name);
      const src = path.join(set.src, ...name.split('/'));
      const dest = path.join(destDir, ...destName.split('/'));
      const check = moods ? await (name.endsWith('.cfg') ? checkMoodFile(src) : checkMoodRotation(src)) : await checkDataFile(src, set.versionKey);
      let state;
      if (!check.ok) state = 'invalid';
      else if (await F.isFile(dest)) state = (await F.sha256File(src)) === (await F.sha256File(dest)) ? 'unchanged' : 'changed';
      else state = 'new';
      if (check.version) summary.version = check.version;
      summary.files++;
      if (state === 'new' || state === 'changed') summary.changed++;
      if (state === 'invalid') summary.invalid++;
      out.items.push({ set: set.id, plugin: set.plugin, name, destName, rel: set.dest ? `${set.dest}/${destName}` : destName, src, dest, state, reason: check.ok ? null : check.reason });
    }
    if (moods) {
      summary.others = (await moodServerFiles(destDir)).filter((n) => !destNames.includes(n));
    } else if (set.dest) {
      try {
        summary.others = (await fsp.readdir(destDir)).filter((n) => /\.json$/i.test(n) && !destNames.includes(n)).sort();
      } catch {
        summary.others = [];
      }
    }
    out.sets.push(summary);
  }
  return out;
}

async function deployData(root, sets) {
  const plan = await planData(root, sets);
  const todo = plan.items.filter((i) => i.state === 'new' || i.state === 'changed');
  const backupDir = path.join(root, '_realm-backups', `data-${S.timestamp()}`);
  let backedUp = false;
  for (const i of todo) {
    await fsp.mkdir(path.dirname(i.dest), { recursive: true });
    if (i.state === 'changed') {
      const b = path.join(backupDir, ...i.rel.split('/'));
      await fsp.mkdir(path.dirname(b), { recursive: true });
      await fsp.copyFile(i.dest, b);
      backedUp = true;
    }
    const tmp = i.dest + '.realm-part';
    await fsp.copyFile(i.src, tmp);
    await fsp.rename(tmp, i.dest);
  }
  // The plugins whose data changed (the mood library has no plugin: nothing reloads for it).
  const reload = [...new Set(todo.map((i) => i.plugin).filter(Boolean))].sort();
  return { ...plan, copied: todo.length, invalid: plan.items.filter((i) => i.state === 'invalid').length, reload, backupDir: backedUp ? backupDir : null };
}

// ---------- backups ----------

function backupsDir(root) {
  return path.join(S.realmHome(root), 'backups');
}

function downloadsDir(root) {
  return path.join(S.realmHome(root), 'downloads');
}

// Backup-Saves.ps1: Saves\ and oxide\data\ into <realm home>\backups\realm-saves-<time>[-label].zip.
async function createBackup(root, { label = '', onProgress, signal } = {}) {
  const dir = backupsDir(root);
  if (S.isInside(dir, root)) throw friendly('The backup folder must be outside the server folder.');
  const sources = [];
  for (const rel of ['Saves', 'oxide/data']) if (await F.isDir(path.join(root, ...rel.split('/')))) sources.push(rel);
  if (!sources.length) throw friendly('Nothing to back up yet: neither Saves nor oxide\\data exists. Has the server run?');
  const files = [];
  const skipped = [];
  for (const rel of sources) {
    for (const f of await F.listFiles(path.join(root, ...rel.split('/')), signal)) {
      // *.lock files are held open by a running server and mean nothing in a backup.
      if (/\.lock$/i.test(f.abs)) { skipped.push(`${rel}/${f.rel.split(path.sep).join('/')}`); continue; }
      // A file the running server has locked (EBUSY/EPERM on Windows) is skipped, not fatal.
      try {
        const fh = await fsp.open(f.abs, 'r');
        await fh.close();
      } catch {
        skipped.push(`${rel}/${f.rel.split(path.sep).join('/')}`);
        continue;
      }
      const st = await fsp.stat(f.abs);
      files.push({ abs: f.abs, size: f.size, mtime: st.mtime, name: `${rel}/${f.rel.split(path.sep).join('/')}` });
    }
  }
  const bytes = files.reduce((s, f) => s + f.size, 0);
  const suffix = label ? '-' + S.sanitizeLabel(label) : '';
  const out = path.join(dir, `realm-saves-${S.timestamp()}${suffix}.zip`);
  const manifest = { tool: 'Realm client', source: root, created: new Date().toISOString(), folders: sources, files: files.length, bytes, skipped };
  await Z.writeZip(out, files, [{ name: 'realm-backup.json', data: Buffer.from(JSON.stringify(manifest, null, 2)) }], { onProgress, signal });
  const size = (await fsp.stat(out)).size;
  return { file: out, name: path.basename(out), files: files.length, bytes, size, skipped };
}

const BACKUP_NAME_RE = /^realm-saves-[A-Za-z0-9_-]+\.zip$/;

async function listBackups(root) {
  const dir = backupsDir(root);
  let names = [];
  try {
    names = (await fsp.readdir(dir)).filter((n) => BACKUP_NAME_RE.test(n));
  } catch {
    return [];
  }
  const out = [];
  for (const n of names) {
    const st = await fsp.stat(path.join(dir, n));
    out.push({ name: n, size: st.size, mtime: st.mtime.toISOString() });
  }
  return out.sort((a, b) => (a.name < b.name ? 1 : -1));
}

// Restore-Saves.ps1: validate, safety backup, move current Saves / oxide\data aside, extract.
// If extraction fails, the partial files are removed and the moved folders put back.
async function restoreBackup(root, name, { onProgress, signal } = {}) {
  if (typeof name !== 'string' || !BACKUP_NAME_RE.test(name)) throw friendly('Pick a backup from the list.');
  const zipPath = path.join(backupsDir(root), name);
  if (!(await F.isFile(zipPath))) throw friendly(`Backup not found: ${zipPath}`);
  const entries = await Z.listZip(zipPath);
  const plan = S.planRestoreEntries(entries.map((e) => e.name));
  const targets = new Map(plan.files.map((f) => [f.entry, S.safeJoin(root, f.rel)]));

  const existing = [];
  for (const top of plan.tops) if (await F.exists(path.join(root, ...top.split('/')))) existing.push(top);
  let safety = null;
  if (existing.length) safety = await createBackup(root, { label: 'pre-restore', signal });
  const aside = path.join(root, '_realm-backups', `pre-restore-${S.timestamp()}`);
  const moved = [];
  try {
    for (const top of existing) {
      const from = path.join(root, ...top.split('/'));
      const to = path.join(aside, ...top.split('/'));
      await fsp.mkdir(path.dirname(to), { recursive: true });
      await fsp.rename(from, to);
      moved.push({ from, to });
    }
    await Z.extractTo(zipPath, targets, { onProgress, signal });
  } catch (e) {
    for (const top of plan.tops) {
      const p = path.join(root, ...top.split('/'));
      if (!moved.some((m) => m.from === p) && existing.includes(top)) continue; // never moved: untouched
      await F.removeTree(p).catch(() => {});
    }
    for (const m of moved.reverse()) await fsp.rename(m.to, m.from).catch(() => {});
    throw e;
  }
  return { restored: plan.files.length, aside: moved.length ? aside : null, safetyBackup: safety && safety.name };
}

module.exports = {
  friendly,
  findSteamServer,
  hasServerExe,
  assertTestCopy,
  isTestCopy,
  createTestCopy,
  cfgPaths,
  applyCfg,
  applyLocalOnlyConfig,
  readServerSettings,
  writeServerSettings,
  managedDirs,
  oxideInstalled,
  latestOxideBackup,
  verifyOxideZip,
  downloadOxide,
  installOxide,
  rollbackOxide,
  getOxideDir,
  chronicleDataDir,
  planPlugins,
  deployPlugins,
  dataSets,
  checkDataFile,
  checkMoodText,
  checkMoodFile,
  MOOD_KEYS,
  planData,
  deployData,
  backupsDir,
  downloadsDir,
  createBackup,
  listBackups,
  restoreBackup
};
