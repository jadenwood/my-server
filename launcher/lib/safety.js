'use strict';

// Pure logic shared by the Realm client's main process: path refusal rules, cfg key rewrite,
// zip entry checks, Steam libraryfolders.vdf parsing, SHA-256 comparison and IPC argument
// validation. Mirrors server/RealmCommon.ps1 and the other server/*.ps1 scripts.
// No Electron imports here, so `node --test` can exercise every rule on any OS.

const path = require('path');

const MARKER_NAME = '.realm-test-copy';
const STEAM_SERVER_DIR = 'Reign Of Kings Dedicated Server';
const DEFAULT_STEAM_SERVER = 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings Dedicated Server';
const DEFAULT_TEST_SERVER = 'G:\\RealmTest\\server';
const STEAM_APP_ID = 344760;

const OXIDE = Object.freeze({
  version: '2.0.3867',
  url: 'https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip',
  sha256: '6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8',
  size: 11939101,
  fileName: 'Oxide.ReignOfKings-2.0.3867.zip'
});

// Start-LocalServer.ps1 defaults: local only, RCON off.
const LOCAL_SERVER_SETTINGS = Object.freeze({ bindIP: '127.0.0.1', isPrivate: 'True' });
const LOCAL_CONSOLE_SETTINGS = Object.freeze({ rConPort: '27016', enableRCon: 'False' });

function pathFor(platform) {
  return platform === 'win32' ? path.win32 : path.posix;
}

function isWin(platform) {
  return platform === 'win32';
}

// Resolves (without touching the disk) and drops trailing separators, except for a root.
function normalizePath(p, platform = process.platform) {
  const P = pathFor(platform);
  let full = P.resolve(String(p));
  const root = P.parse(full).root;
  while (full.length > root.length && /[\\/]$/.test(full)) full = full.slice(0, -1);
  return full;
}

function samePath(a, b, platform = process.platform) {
  const x = normalizePath(a, platform);
  const y = normalizePath(b, platform);
  return isWin(platform) ? x.toLowerCase() === y.toLowerCase() : x === y;
}

// True when child is parent or lies below it (Test-RealmPathInside: appends a separator to both).
function isInside(child, parent, platform = process.platform) {
  const P = pathFor(platform);
  let c = normalizePath(child, platform);
  let p = normalizePath(parent, platform);
  if (isWin(platform)) {
    c = c.toLowerCase();
    p = p.toLowerCase();
  }
  if (c === p) return true;
  const withSep = p.endsWith(P.sep) ? p : p + P.sep;
  return c.startsWith(withSep);
}

function segments(p, platform) {
  const P = pathFor(platform);
  const full = normalizePath(p, platform);
  return full.slice(P.parse(full).root.length).split(/[\\/]+/).filter(Boolean);
}

// The test-copy rules from Assert-RealmTestCopy / New-TestServer.ps1, without disk access.
// Returns { ok: true, root } or { ok: false, reason } with a plain-English reason.
function checkTestRoot(input, opts = {}) {
  const platform = opts.platform || process.platform;
  const P = pathFor(platform);
  if (typeof input !== 'string' || !input.trim()) return { ok: false, reason: 'Choose a folder for the test copy.' };
  const raw = input.trim();
  if (raw.length > 240) return { ok: false, reason: 'That folder path is too long. Pick a shorter one, like G:\\RealmTest\\server.' };
  if (/[\0<>"|?*]/.test(raw.replace(/^[A-Za-z]:/, ''))) return { ok: false, reason: 'That folder path contains characters Windows does not allow.' };
  if (isWin(platform)) {
    if (!/^[A-Za-z]:\\/.test(raw.replace(/\//g, '\\'))) {
      return { ok: false, reason: 'Use a folder on a local drive, like G:\\RealmTest\\server.' };
    }
  } else if (!P.isAbsolute(raw)) {
    return { ok: false, reason: 'Use a full folder path.' };
  }
  const root = normalizePath(raw, platform);
  if (isWin(platform) && /^c:/i.test(root)) {
    return { ok: false, reason: `Refusing ${root}: the test copy must not be on C:. Use another drive, like G:\\RealmTest\\server.` };
  }
  const segs = segments(root, platform);
  if (segs.length === 0) return { ok: false, reason: `Refusing ${root}: pick a folder, not the top of a drive.` };
  for (const s of segs) {
    if (/^steamapps$/i.test(s)) return { ok: false, reason: `Refusing ${root}: it is inside a Steam library (steamapps). Realm only changes its own test copy.` };
    if (/^(steam|steamlibrary)$/i.test(s)) return { ok: false, reason: `Refusing ${root}: it is inside a Steam folder. Realm only changes its own test copy.` };
  }
  for (const guarded of [opts.steamServer, opts.steamRoot, ...(opts.steamLibraries || [])].filter(Boolean)) {
    // A library at the top of a drive (G:\) would refuse the whole drive; the segment rules cover it.
    if (segments(guarded, platform).length === 0) continue;
    if (isInside(root, guarded, platform) || isInside(guarded, root, platform)) {
      return { ok: false, reason: `Refusing ${root}: it overlaps the Steam folder ${guarded}.` };
    }
  }
  return { ok: true, root };
}

// Where Realm keeps downloads and backups: next to the test server folder (G:\RealmTest\{downloads,backups}).
function realmHome(testRoot, platform = process.platform) {
  return pathFor(platform).dirname(normalizePath(testRoot, platform));
}

function timestamp(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function formatBytes(n) {
  const b = Number(n) || 0;
  if (b >= 1024 ** 3) return (b / 1024 ** 3).toFixed(1) + ' GB';
  if (b >= 1024 ** 2) return (b / 1024 ** 2).toFixed(1) + ' MB';
  return Math.round(b / 1024) + ' KB';
}

function sanitizeLabel(label) {
  return String(label || '').replace(/[^A-Za-z0-9_-]/g, '_').slice(0, 40);
}

// ---------- cfg files (key = 'value') ----------

// Read-RealmTextFile: keep the original encoding (UTF-8 with/without BOM, UTF-16 LE) on rewrite.
function decodeText(buf) {
  if (buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) return { text: buf.slice(3).toString('utf8'), encoding: 'utf8bom' };
  if (buf.length >= 2 && buf[0] === 0xff && buf[1] === 0xfe) return { text: buf.slice(2).toString('utf16le'), encoding: 'utf16le' };
  return { text: buf.toString('utf8'), encoding: 'utf8' };
}

function encodeText(text, encoding) {
  if (encoding === 'utf8bom') return Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(text, 'utf8')]);
  if (encoding === 'utf16le') return Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(text, 'utf16le')]);
  return Buffer.from(text, 'utf8');
}

function escapeRegex(s) {
  return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function getCfgValue(text, key) {
  const re = new RegExp('^\\s*' + escapeRegex(key) + "\\s*=\\s*'(.*?)'");
  for (const line of String(text).split(/\r?\n/)) {
    const m = re.exec(line);
    if (m) return m[1];
  }
  return null;
}

// A value written into a cfg line must not be able to break the line format.
function isSafeCfgValue(v) {
  return typeof v === 'string' && v.length <= 128 && !/['\r\n\0]/.test(v) && !/[\u0000-\u001f]/.test(v);
}

// Set-RealmCfgValues: rewrites existing `key = '...'` lines only, never appends keys.
// Returns { text, changes: [{key, from, to}], missing: [key] }. text === input when nothing changed.
function rewriteCfg(text, values) {
  const src = String(text);
  const newline = src.includes('\r\n') ? '\r\n' : '\n';
  const lines = src.split(/\r?\n/);
  const changes = [];
  const missing = [];
  for (const [key, value] of Object.entries(values)) {
    if (!/^[A-Za-z0-9_.]+$/.test(key)) throw new Error(`Invalid cfg key ${JSON.stringify(key)}`);
    if (!isSafeCfgValue(value)) throw new Error(`Invalid value for ${key}: quotes and line breaks are not allowed.`);
    const re = new RegExp('^(\\s*' + escapeRegex(key) + "\\s*=\\s*')(.*?)('.*)$");
    let found = false;
    for (let i = 0; i < lines.length; i++) {
      const m = re.exec(lines[i]);
      if (!m) continue;
      found = true;
      if (m[2] !== value) {
        changes.push({ key, from: m[2], to: value });
        lines[i] = m[1] + value + m[3];
      }
    }
    if (!found) missing.push(key);
  }
  return { text: changes.length ? lines.join(newline) : src, changes, missing };
}

// ---------- zip entries ----------

function isUnsafeEntryName(name) {
  const n = String(name).replace(/\\/g, '/');
  return n.includes('\0') || /^\//.test(n) || /^[A-Za-z]:/.test(n) || /(^|\/)\.\.(\/|$)/.test(n);
}

function isValidDataFolder(name) {
  return typeof name === 'string' && /^[A-Za-z0-9_]+_Data$/.test(name);
}

// Install-Oxide.ps1: every entry must be under ROK_Data/; ROK_Data is redirected to the detected
// *_Data folder. Returns [{ entry, rel }] (rel uses '/'), directories skipped. Throws on anything odd.
function planOxideEntries(entryNames, dataFolder) {
  if (!isValidDataFolder(dataFolder)) throw new Error(`Data folder must look like ROK_Data or Server_Data, got ${JSON.stringify(dataFolder)}.`);
  const plan = [];
  for (const raw of entryNames) {
    const name = String(raw).replace(/\\/g, '/');
    if (isUnsafeEntryName(name)) throw new Error(`Unsafe entry in zip: ${name}`);
    if (!name.startsWith('ROK_Data/')) throw new Error(`Unexpected entry outside ROK_Data/: ${name}. This is not the expected Oxide ${OXIDE.version} zip.`);
    if (name.endsWith('/')) continue;
    plan.push({ entry: raw, rel: dataFolder + name.slice('ROK_Data'.length) });
  }
  if (!plan.length) throw new Error('The Oxide zip contains no files.');
  return plan;
}

// Restore-Saves.ps1: realm-backup.json must exist; all other entries under Saves/ or oxide/data/.
function planRestoreEntries(entryNames) {
  let hasManifest = false;
  const files = [];
  const tops = new Set();
  for (const raw of entryNames) {
    const name = String(raw).replace(/\\/g, '/');
    if (name === 'realm-backup.json') {
      hasManifest = true;
      continue;
    }
    if (isUnsafeEntryName(name)) throw new Error(`Unsafe entry in zip: ${name}`);
    if (!/^(Saves|oxide\/data)\//.test(name)) throw new Error(`Unexpected entry in backup (not under Saves/ or oxide/data/): ${name}`);
    if (name.endsWith('/')) continue;
    tops.add(name.startsWith('Saves/') ? 'Saves' : 'oxide/data');
    files.push({ entry: raw, rel: name });
  }
  if (!hasManifest) throw new Error('This zip has no realm-backup.json, so it was not made by Realm.');
  if (!files.length) throw new Error('The backup contains no files.');
  return { files, tops: [...tops] };
}

// Joins a '/'-separated relative path under root and refuses anything that escapes it.
function safeJoin(root, rel, platform = process.platform) {
  const P = pathFor(platform);
  if (isUnsafeEntryName(rel)) throw new Error(`Unsafe path: ${rel}`);
  const dest = P.resolve(root, ...String(rel).split('/').filter(Boolean));
  if (!isInside(dest, root, platform) || samePath(dest, root, platform)) throw new Error(`Unsafe path: ${dest}`);
  return dest;
}

// Picks the *_Data folder that has Managed\ (Get-RealmManagedDirs); ROK_Data wins if present.
function pickDataFolder(names) {
  const valid = (names || []).filter(isValidDataFolder);
  if (valid.includes('ROK_Data')) return 'ROK_Data';
  return valid.length ? valid.slice().sort()[0] : null;
}

// ---------- Steam discovery ----------

// Minimal Valve KeyValues (VDF) parser: quoted strings, braces, // comments.
function parseVdf(text) {
  const src = String(text);
  let i = 0;
  function skip() {
    for (;;) {
      while (i < src.length && /\s/.test(src[i])) i++;
      if (src.startsWith('//', i)) {
        while (i < src.length && src[i] !== '\n') i++;
      } else return;
    }
  }
  function str() {
    if (src[i] !== '"') {
      // Unquoted tokens are legal in KeyValues; read up to whitespace or a brace.
      const start = i;
      while (i < src.length && !/[\s{}"]/.test(src[i])) i++;
      return src.slice(start, i);
    }
    i++;
    let out = '';
    while (i < src.length && src[i] !== '"') {
      if (src[i] === '\\' && i + 1 < src.length) {
        const n = src[i + 1];
        out += n === 'n' ? '\n' : n === 't' ? '\t' : n;
        i += 2;
      } else out += src[i++];
    }
    i++;
    return out;
  }
  function obj(depth) {
    const o = {};
    for (;;) {
      skip();
      if (i >= src.length) return o;
      if (src[i] === '}') {
        i++;
        return o;
      }
      const key = str();
      if (!key && src[i] !== '"') {
        i++;
        continue;
      }
      skip();
      if (src[i] === '{') {
        i++;
        o[key] = depth > 20 ? {} : obj(depth + 1);
      } else {
        o[key] = str();
      }
    }
  }
  return obj(0);
}

// libraryfolders.vdf: new format {"0": {"path": "..."}}, old format {"1": "D:\\SteamLibrary"}.
function parseLibraryFolders(text) {
  const doc = parseVdf(text);
  const rootKey = Object.keys(doc).find((k) => /^libraryfolders$/i.test(k));
  const lf = rootKey ? doc[rootKey] : doc;
  const out = [];
  if (!lf || typeof lf !== 'object') return out;
  for (const [k, v] of Object.entries(lf)) {
    if (v && typeof v === 'object' && typeof v.path === 'string') out.push(v.path);
    else if (typeof v === 'string' && /^\d+$/.test(k)) out.push(v);
  }
  return out.filter((p) => p.trim());
}

// `reg query HKCU\Software\Valve\Steam /v SteamPath` output -> 'c:\program files (x86)\steam'.
function parseRegSteamPath(stdout) {
  const m = /^\s*SteamPath\s+REG_(?:EXPAND_)?SZ\s+(.+?)\s*$/im.exec(String(stdout));
  return m ? m[1].replace(/\//g, '\\') : null;
}

function steamServerCandidates(steamPath, libraries, defaultPath = DEFAULT_STEAM_SERVER) {
  const P = path.win32;
  const list = [defaultPath];
  for (const lib of [steamPath, ...(libraries || [])]) {
    if (lib) list.push(P.join(lib.replace(/\//g, '\\'), 'steamapps', 'common', STEAM_SERVER_DIR));
  }
  const seen = new Set();
  return list.filter((p) => {
    const k = p.toLowerCase();
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });
}

// ---------- hashes ----------

function isSha256Hex(s) {
  return typeof s === 'string' && /^[0-9a-fA-F]{64}$/.test(s);
}

function sha256Matches(actual, expected) {
  return isSha256Hex(actual) && isSha256Hex(expected) && actual.toLowerCase() === expected.toLowerCase();
}

// ---------- server console ----------

const READY_LINE = /^\s*Initialize engine version:/;
const JOIN_LINE = /Authentication verified for (.+?) \(\d+\)\./;
const LEAVE_LINE = /^\s*(.+?) has disconnected\./;

// ---------- IPC / settings validation ----------

function asString(v, max, name = 'value') {
  if (typeof v !== 'string') throw new TypeError(`${name} must be text`);
  if (v.length > max) throw new RangeError(`${name} is too long`);
  return v;
}

function asEnum(v, allowed, name = 'value') {
  if (!allowed.includes(v)) throw new TypeError(`${name} is not one of ${allowed.join(', ')}`);
  return v;
}

function asInt(v, min, max, name = 'value') {
  const n = typeof v === 'string' && /^\s*\d+\s*$/.test(v) ? Number(v) : v;
  if (!Number.isInteger(n) || n < min || n > max) throw new RangeError(`${name} must be a whole number from ${min} to ${max}`);
  return n;
}

function isHttpsUrl(v) {
  try {
    const u = new URL(String(v));
    return u.protocol === 'https:' && !u.username && !u.password && !!u.hostname;
  } catch {
    return false;
  }
}

// Settings screen input -> clean values or an error list. Unknown keys are dropped.
function validateSettings(input) {
  const errors = [];
  const out = {};
  const v = input && typeof input === 'object' ? input : {};
  if ('serverName' in v) {
    const s = typeof v.serverName === 'string' ? v.serverName.trim() : '';
    if (!s || s.length > 64 || !isSafeCfgValue(s)) errors.push('Server name: 1 to 64 characters, no quotes or line breaks.');
    else out.serverName = s;
  }
  if ('maxPlayers' in v) {
    try {
      out.maxPlayers = asInt(v.maxPlayers, 1, 200, 'Max players');
    } catch (e) {
      errors.push(e.message + '.');
    }
  }
  if ('port' in v) {
    try {
      out.port = asInt(v.port, 1024, 65535, 'Port');
    } catch (e) {
      errors.push(e.message + '.');
    }
  }
  if ('discordUrl' in v) {
    const s = typeof v.discordUrl === 'string' ? v.discordUrl.trim() : '';
    if (s && (!isHttpsUrl(s) || s.length > 300)) errors.push('Discord link must be an https:// address.');
    else out.discordUrl = s;
  }
  if ('serverExe' in v) {
    if (v.serverExe === 'Server' || v.serverExe === 'ROK') out.serverExe = v.serverExe;
    else errors.push('Server program must be Server.exe or ROK.exe.');
  }
  if ('testRoot' in v) {
    if (typeof v.testRoot === 'string' && v.testRoot.length <= 240) out.testRoot = v.testRoot.trim();
    else errors.push('Test folder must be a folder path.');
  }
  return { ok: errors.length === 0, values: out, errors };
}

function validateNews(list) {
  if (!Array.isArray(list)) throw new TypeError('news must be a list');
  if (list.length > 20) throw new RangeError('at most 20 news items');
  return list.map((n, i) => {
    if (!n || typeof n !== 'object') throw new TypeError(`news item ${i + 1} is invalid`);
    const date = typeof n.date === 'string' ? n.date.trim() : '';
    if (date && !/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new RangeError(`news item ${i + 1}: date must look like 2026-10-01`);
    const title = asString(typeof n.title === 'string' ? n.title.trim() : '', 120, `news item ${i + 1} title`);
    const body = asString(typeof n.body === 'string' ? n.body.trim() : '', 800, `news item ${i + 1} text`);
    if (!title) throw new RangeError(`news item ${i + 1} needs a title`);
    return { date, title, body };
  });
}

module.exports = {
  MARKER_NAME,
  STEAM_SERVER_DIR,
  DEFAULT_STEAM_SERVER,
  DEFAULT_TEST_SERVER,
  STEAM_APP_ID,
  OXIDE,
  LOCAL_SERVER_SETTINGS,
  LOCAL_CONSOLE_SETTINGS,
  READY_LINE,
  JOIN_LINE,
  LEAVE_LINE,
  pathFor,
  normalizePath,
  samePath,
  isInside,
  checkTestRoot,
  realmHome,
  timestamp,
  formatBytes,
  sanitizeLabel,
  decodeText,
  encodeText,
  getCfgValue,
  isSafeCfgValue,
  rewriteCfg,
  isUnsafeEntryName,
  isValidDataFolder,
  planOxideEntries,
  planRestoreEntries,
  safeJoin,
  pickDataFolder,
  parseVdf,
  parseLibraryFolders,
  parseRegSteamPath,
  steamServerCandidates,
  isSha256Hex,
  sha256Matches,
  asString,
  asEnum,
  asInt,
  isHttpsUrl,
  validateSettings,
  validateNews
};
