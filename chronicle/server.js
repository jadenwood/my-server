// Realm Chronicle web service: serves the RealmChronicle / RealmState data files written by the
// RealmChronicle Oxide plugin as a small JSON API plus two static pages (/overlay, /realm).
// Dependency-free: only node: built-ins. Read-only: it never writes to the data directory.

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { join, resolve, extname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = fileURLToPath(new URL('.', import.meta.url));
const PUBLIC_DIR = resolve(HERE, 'public');
// Default: the Realm test copy made by server/New-TestServer.ps1 (never the Steam install).
const DEFAULT_DATA_DIR = 'G:\\RealmTest\\server\\oxide\\data';

const EVENT_TYPES = new Set([
  'coronation', 'abdication', 'claim_declared', 'rebellion_started', 'rebellion_ended',
  'house_founded', 'oath_sworn', 'oath_broken', 'treaty_signed', 'treaty_broken',
  'decree', 'ransom_set', 'ransom_paid', 'released',
]);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.json': 'application/json; charset=utf-8',
};

const PAGES = { '/overlay': 'overlay.html', '/realm': 'realm.html' };

export function parseOptions(argv = process.argv.slice(2), env = process.env) {
  const opts = {
    dataDir: env.REALM_DATA_DIR || DEFAULT_DATA_DIR,
    host: env.HOST || '127.0.0.1',
    port: Number(env.PORT || 8787),
    staleAfterSec: Number(env.REALM_STALE_SECONDS || 180),
  };
  for (let i = 0; i < argv.length; i++) {
    const [flag, inline] = argv[i].split('=', 2);
    const value = () => (inline !== undefined ? inline : argv[++i]);
    if (flag === '--data' || flag === '--data-dir') opts.dataDir = value();
    else if (flag === '--host') opts.host = value();
    else if (flag === '--port') opts.port = Number(value());
    else if (flag === '--stale') opts.staleAfterSec = Number(value());
    else if (flag === '--help' || flag === '-h') opts.help = true;
  }
  opts.dataDir = resolve(opts.dataDir);
  return opts;
}

// ---------- data loading ----------

// Caches each file by mtime+size and keeps the last good parse, so a half-written file
// (the plugin rewrites it in place) never breaks the overlay.
function makeJsonSource(path) {
  let key = null;
  let value = null;
  let error = null;
  return async function load() {
    let st;
    try {
      st = await stat(path);
    } catch (e) {
      error = e.code === 'ENOENT' ? 'missing' : e.message;
      return { value, error };
    }
    const k = `${st.mtimeMs}:${st.size}`;
    if (k === key) return { value, error };
    try {
      const text = (await readFile(path, 'utf8')).replace(/^\uFEFF/, '');
      value = JSON.parse(text);
      key = k;
      error = null;
    } catch (e) {
      error = `unreadable: ${e.message}`; // keep the previous good value
    }
    return { value, error };
  };
}

const str = (v, max) => (typeof v === 'string' ? v.slice(0, max) : null);
const int = (v) => (Number.isInteger(v) ? v : 0);

function normalizeIso(v) {
  if (typeof v !== 'string' || !v) return null;
  // Timestamps without a zone are treated as UTC (the plugin always writes UTC).
  const withZone = /[zZ]|[+-]\d\d:?\d\d$/.test(v) ? v : `${v}Z`;
  const t = Date.parse(withZone);
  return Number.isNaN(t) ? null : new Date(t).toISOString().replace('.000Z', 'Z');
}

// Whitelist the contract fields only: anything else in the file is never served.
export function sanitizeEvent(e) {
  if (!e || typeof e !== 'object' || !Number.isInteger(e.id) || !EVENT_TYPES.has(e.type)) return null;
  return {
    id: e.id,
    ts: normalizeIso(e.ts),
    type: e.type,
    title: str(e.title, 200) || '',
    detail: str(e.detail, 600) || '',
    actors: Array.isArray(e.actors) ? e.actors.filter((a) => typeof a === 'string').slice(0, 8).map((a) => a.slice(0, 64)) : [],
  };
}

export function sanitizeState(s) {
  s = s && typeof s === 'object' ? s : {};
  const houses = Array.isArray(s.houses) ? s.houses : [];
  return {
    king: str(s.king, 64),
    house: str(s.house, 64),
    since: s.king ? normalizeIso(s.since) : null,
    houses: houses
      .filter((h) => h && typeof h.name === 'string' && h.name)
      .slice(0, 64)
      .map((h) => ({ name: h.name.slice(0, 64), sigil: str(h.sigil, 64), liege: str(h.liege, 64), members: int(h.members) })),
    online: int(s.online),
    maxPlayers: int(s.maxPlayers),
    updated: normalizeIso(s.updated),
  };
}

export function createApp(opts) {
  const loadEvents = makeJsonSource(join(opts.dataDir, 'RealmChronicle.json'));
  const loadState = makeJsonSource(join(opts.dataDir, 'RealmState.json'));

  async function getEvents() {
    const { value, error } = await loadEvents();
    const list = (Array.isArray(value) ? value : []).map(sanitizeEvent).filter(Boolean);
    list.sort((a, b) => a.id - b.id);
    return { list, error };
  }

  async function getState() {
    const { value, error } = await loadState();
    const state = sanitizeState(value);
    const age = state.updated ? (Date.now() - Date.parse(state.updated)) / 1000 : Infinity;
    // "stale" is an addition to the file contract: true when the plugin has stopped refreshing.
    const stale = opts.staleAfterSec > 0 && !(age <= opts.staleAfterSec);
    return { state: { ...state, stale }, error };
  }

  async function handle(req, res) {
    const url = new URL(req.url, 'http://localhost');
    const path = url.pathname.replace(/\/+$/, '') || '/';

    if (req.method !== 'GET' && req.method !== 'HEAD') {
      return send(res, 405, { error: 'method not allowed' }, { Allow: 'GET, HEAD' });
    }

    if (path === '/api/state') {
      const { state, error } = await getState();
      return send(res, 200, state, error ? { 'X-Realm-Data-Warning': error } : {});
    }

    if (path === '/api/events') {
      const { list, error } = await getEvents();
      const limit = clamp(parseInt(url.searchParams.get('limit') ?? '50', 10), 1, 500, 50);
      const sinceRaw = url.searchParams.get('since');
      let out;
      if (sinceRaw !== null && sinceRaw !== '') {
        const since = parseInt(sinceRaw, 10);
        if (!Number.isInteger(since)) return send(res, 400, { error: 'since must be an integer event id' });
        out = list.filter((e) => e.id > since).slice(0, limit); // oldest first so pollers can catch up in order
      } else {
        out = list.slice(-limit);
      }
      return send(res, 200, out, error ? { 'X-Realm-Data-Warning': error } : {});
    }

    if (path === '/healthz') {
      const [{ error: eErr }, { error: sErr }] = await Promise.all([loadEvents(), loadState()]);
      return send(res, 200, { ok: true, events: eErr || 'ok', state: sErr || 'ok' });
    }

    if (path === '/') return redirect(res, '/realm');
    if (PAGES[path]) return serveStatic(res, req, PAGES[path]);
    if (path.startsWith('/assets/')) return serveStatic(res, req, path.slice(1));
    return send(res, 404, { error: 'not found' });
  }

  return async (req, res) => {
    try {
      await handle(req, res);
    } catch (e) {
      console.error('[chronicle] request failed:', e);
      if (!res.headersSent) send(res, 500, { error: 'internal error' });
      else res.end();
    }
  };
}

function clamp(n, lo, hi, fallback) {
  return Number.isInteger(n) ? Math.min(hi, Math.max(lo, n)) : fallback;
}

const SECURITY_HEADERS = {
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'Content-Security-Policy':
    "default-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; " +
    "img-src 'self' data:; script-src 'self'; connect-src 'self'; frame-ancestors 'self'",
};

function send(res, status, body, extra = {}) {
  const data = JSON.stringify(body);
  res.writeHead(status, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    ...SECURITY_HEADERS,
    ...extra,
  });
  res.end(res.req?.method === 'HEAD' ? undefined : data);
}

function redirect(res, location) {
  res.writeHead(302, { Location: location, ...SECURITY_HEADERS });
  res.end();
}

async function serveStatic(res, req, rel) {
  const file = resolve(PUBLIC_DIR, rel);
  if (file !== PUBLIC_DIR && !file.startsWith(PUBLIC_DIR + sep)) return send(res, 404, { error: 'not found' });
  let body;
  try {
    body = await readFile(file);
  } catch {
    return send(res, 404, { error: 'not found' });
  }
  res.writeHead(200, {
    'Content-Type': MIME[extname(file)] || 'application/octet-stream',
    'Cache-Control': 'no-cache',
    ...SECURITY_HEADERS,
  });
  res.end(req.method === 'HEAD' ? undefined : body);
}

// ---------- main ----------

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const opts = parseOptions();
  if (opts.help) {
    console.log('Usage: node server.js [--data <dir>] [--host 127.0.0.1] [--port 8787] [--stale <seconds, 0 = off>]\n' +
      'Env: REALM_DATA_DIR, HOST, PORT, REALM_STALE_SECONDS');
    process.exit(0);
  }
  if (!['127.0.0.1', 'localhost', '::1'].includes(opts.host)) {
    console.warn(`[chronicle] WARNING: binding to ${opts.host} exposes the chronicle beyond this machine.`);
  }
  const server = createServer(createApp(opts));
  server.on('error', (e) => {
    console.error(`[chronicle] cannot listen on ${opts.host}:${opts.port}: ${e.message}`);
    process.exit(1);
  });
  server.listen(opts.port, opts.host, () => {
    const base = `http://${opts.host.includes(':') ? `[${opts.host}]` : opts.host}:${opts.port}`;
    console.log(`[chronicle] data dir: ${opts.dataDir}`);
    console.log(`[chronicle] overlay:  ${base}/overlay`);
    console.log(`[chronicle] realm:    ${base}/realm`);
  });
}
