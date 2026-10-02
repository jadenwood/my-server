'use strict';

// Steam helpers shared by both editions. Pure parsing plus read-only lookups:
// - Valve KeyValues (VDF/ACF) parsing, libraryfolders.vdf, `reg query` output.
// - Whether Reign of Kings (app 344760) is installed, from steamapps\appmanifest_344760.acf.
// - The only steam:// URLs either edition may hand to Windows, and their exact allow-list.
// Nothing here launches a game file or writes anywhere.

const path = require('path');
const fsp = require('fs/promises');
const { execFile } = require('child_process');

const GAME_APP_ID = 344760;

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

// ---------- host names and steam:// URLs ----------

// A server address from the signed list: a DNS name or a dotted IPv4. No ports, no IPv6, no
// leading '-' (it would read as a command-line switch), nothing that needs quoting.
function isValidHost(h) {
  if (typeof h !== 'string' || h.length < 1 || h.length > 253) return false;
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(h)) return h.split('.').every((o) => Number(o) <= 255 && String(Number(o)) === o);
  if (/^[\d.]+$/.test(h)) return false; // digits and dots but not a valid IPv4
  const labels = h.split('.');
  return labels.every((l) => /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/.test(l));
}

function isPort(p) {
  return Number.isInteger(p) && p >= 1 && p <= 65535;
}

// Quick join through the player's own Steam (docs/join-and-scale.md §1.5): Steam starts the game
// through its Easy Anti-Cheat launcher with "-ip <host> -port <port>"; the game's QuickJoinBypass
// reads them and calls Game.Join [DEC]. UNVERIFIED: that the EAC bootstrapper forwards the arguments
// and whether Steam shows a confirmation dialog for them. Never -pass (visible in the process list)
// and never -rejoin (a developer soak-test switch).
function quickJoinUrl(host, port, appId = GAME_APP_ID) {
  if (!isValidHost(host)) throw new RangeError('invalid server host');
  if (!isPort(port)) throw new RangeError('invalid server port');
  if (!Number.isInteger(appId) || appId <= 0) throw new RangeError('invalid app id');
  return `steam://run/${appId}//-ip%20${host}%20-port%20${port}/`;
}

const QUICK_JOIN_RE = /^steam:\/\/run\/(\d{1,10})\/\/-ip%20([A-Za-z0-9.-]{1,253})%20-port%20(\d{1,5})\/$/;

// The complete allow-list for shell.openExternal of steam:// URLs.
function isAllowedSteamUrl(url, appId = GAME_APP_ID) {
  if (typeof url !== 'string' || url.length > 400) return false;
  if (url === `steam://rungameid/${appId}`) return true;
  if (url === `steam://install/${appId}`) return true;
  const m = QUICK_JOIN_RE.exec(url);
  return !!m && Number(m[1]) === appId && isValidHost(m[2]) && isPort(Number(m[3]));
}

// ---------- install detection (read-only) ----------

function runFile(file, args, execFileImpl = execFile) {
  return new Promise((resolve) => {
    execFileImpl(file, args, { windowsHide: true, timeout: 8000 }, (err, stdout) => resolve(err ? '' : String(stdout || '')));
  });
}

// { steam: true|false|null, steamPath, installed: true|false|null, library }
// null means "could not tell" (for example on a non-Windows machine): the UI then lets PLAY through.
async function detectGameInstall({ platform = process.platform, execFileImpl, readFile = (p) => fsp.readFile(p, 'utf8'), appId = GAME_APP_ID } = {}) {
  if (platform !== 'win32') return { steam: null, steamPath: null, installed: null, library: null };
  const steamPath = parseRegSteamPath(await runFile('reg', ['query', 'HKCU\\Software\\Valve\\Steam', '/v', 'SteamPath'], execFileImpl));
  if (!steamPath) return { steam: false, steamPath: null, installed: false, library: null };
  let libraries = [];
  for (const vdf of [path.win32.join(steamPath, 'steamapps', 'libraryfolders.vdf'), path.win32.join(steamPath, 'config', 'libraryfolders.vdf')]) {
    try {
      libraries = parseLibraryFolders(await readFile(vdf));
      if (libraries.length) break;
    } catch {
      /* not there */
    }
  }
  for (const lib of [steamPath, ...libraries]) {
    const acf = path.win32.join(lib.replace(/\//g, '\\'), 'steamapps', `appmanifest_${appId}.acf`);
    try {
      if (isInstalledManifest(await readFile(acf), appId)) return { steam: true, steamPath, installed: true, library: lib };
    } catch {
      /* not in this library */
    }
  }
  return { steam: true, steamPath, installed: false, library: null };
}

// appmanifest_<id>.acf: AppState.appid matches and StateFlags has bit 4 (fully installed).
function isInstalledManifest(text, appId = GAME_APP_ID) {
  const doc = parseVdf(text);
  const key = Object.keys(doc).find((k) => /^appstate$/i.test(k));
  const st = key ? doc[key] : null;
  if (!st || typeof st !== 'object') return false;
  if (String(st.appid) !== String(appId)) return false;
  const flags = Number(st.StateFlags);
  return Number.isFinite(flags) ? (flags & 4) === 4 : true;
}

module.exports = {
  GAME_APP_ID,
  parseVdf,
  parseLibraryFolders,
  parseRegSteamPath,
  isValidHost,
  isPort,
  quickJoinUrl,
  isAllowedSteamUrl,
  detectGameInstall,
  isInstalledManifest
};
