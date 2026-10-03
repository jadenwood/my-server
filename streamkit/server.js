// Realm Stream Scenes server: serves the OBS scenes in public/ and relays two read-only chronicle
// endpoints (/api/state, /api/events) from the local chronicle service, so the browser sees one
// origin (the chronicle sends no CORS headers, and the scenes must not need it to).
//
// Loopback only: it refuses to bind anywhere but 127.0.0.1 / ::1 / localhost, only relays to a
// loopback upstream, and rejects requests whose Host header is not a loopback name (DNS rebinding).
// Dependency-free: node: built-ins only (Node 20+).

import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname, sep, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = fileURLToPath(new URL('.', import.meta.url));
const PUBLIC_DIR = resolve(HERE, 'public');
const LOOPBACK = new Set(['127.0.0.1', '::1', 'localhost', '[::1]']);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.json': 'application/json; charset=utf-8',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
  '.md': 'text/plain; charset=utf-8',
};

export const SCENES = ['war-board', 'throne-room', 'breaking-news', 'countdown', 'starting-soon'];

export const SECURITY_HEADERS = {
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'Content-Security-Policy':
    "default-src 'self'; style-src 'self'; font-src 'self'; " +
    "img-src 'self' data:; script-src 'self'; connect-src 'self'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'",
};

export function isLoopbackHost(host) {
  return LOOPBACK.has(String(host || '').toLowerCase());
}

export function parseOptions(argv = process.argv.slice(2), env = process.env) {
  const opts = {
    host: env.STREAMKIT_HOST || '127.0.0.1',
    port: Number(env.STREAMKIT_PORT || 8790),
    upstream: env.REALM_CHRONICLE_URL || 'http://127.0.0.1:8787',
    fixtures: env.STREAMKIT_FIXTURES || null,
    schedule: env.STREAMKIT_SCHEDULE || null,
    demo: 0,
    timeoutMs: 4000,
  };
  for (let i = 0; i < argv.length; i++) {
    const [flag, inline] = argv[i].split('=', 2);
    const value = () => (inline !== undefined ? inline : argv[++i]);
    if (flag === '--host') opts.host = value();
    else if (flag === '--port') opts.port = Number(value());
    else if (flag === '--upstream' || flag === '--chronicle') opts.upstream = value();
    else if (flag === '--fixtures') opts.fixtures = value();
    else if (flag === '--schedule') opts.schedule = value();
    else if (flag === '--demo') opts.demo = Number(value());
    else if (flag === '--help' || flag === '-h') opts.help = true;
    else throw new Error(`unknown option ${flag}`);
  }
  if (!isLoopbackHost(opts.host)) {
    throw new Error(`refusing to bind to ${opts.host}: the stream scenes server is loopback only (127.0.0.1, ::1 or localhost)`);
  }
  if (!Number.isInteger(opts.port) || opts.port < 0 || opts.port > 65535) throw new Error(`bad port ${opts.port}`);
  let up;
  try {
    up = new URL(opts.upstream);
  } catch {
    throw new Error(`bad upstream URL ${opts.upstream}`);
  }
  if (up.protocol !== 'http:' || !isLoopbackHost(up.hostname)) {
    throw new Error(`refusing upstream ${opts.upstream}: it must be http:// on 127.0.0.1, ::1 or localhost`);
  }
  opts.upstream = `${up.protocol}//${up.host}`;
  if (opts.fixtures) opts.fixtures = resolve(opts.fixtures);
  if (opts.schedule) opts.schedule = resolve(opts.schedule);
  if (!Number.isFinite(opts.demo) || opts.demo < 0) opts.demo = 0;
  return opts;
}

// ---------- event query (same semantics as chronicle/server.js) ----------

export function parseEventQuery(searchParams) {
  const limitRaw = parseInt(searchParams.get('limit') ?? '50', 10);
  const limit = Number.isInteger(limitRaw) ? Math.min(500, Math.max(1, limitRaw)) : 50;
  const sinceRaw = searchParams.get('since');
  if (sinceRaw === null || sinceRaw === '') return { limit, since: null };
  if (!/^-?\d+$/.test(sinceRaw)) return { error: 'since must be an integer event id' };
  return { limit, since: parseInt(sinceRaw, 10) };
}

function selectEvents(list, q) {
  return q.since != null ? list.filter((e) => e.id > q.since).slice(0, q.limit) : list.slice(-q.limit);
}

// ---------- data sources ----------

function makeFixtureSource(dir, demoSeconds) {
  let demo = null;
  async function readJson(name, fallback) {
    try {
      return JSON.parse((await readFile(join(dir, name), 'utf8')).replace(/^﻿/, ''));
    } catch (e) {
      if (e.code === 'ENOENT') return fallback;
      throw e;
    }
  }
  // Demo mode: every N seconds one event from fixtures/demo.json is appended (new id, ts = now), so
  // the animations can be positioned in OBS without a game server. In memory only.
  async function demoState(baseEvents, baseState) {
    if (!demoSeconds) return { events: baseEvents, state: baseState };
    if (!demo) {
      demo = { script: await readJson('demo.json', []), started: Date.now(), emitted: [], state: { ...baseState } };
    }
    const due = Math.floor((Date.now() - demo.started) / (demoSeconds * 1000));
    const lastBase = baseEvents.length ? baseEvents[baseEvents.length - 1].id : 0;
    while (demo.emitted.length < due && demo.script.length) {
      const tpl = demo.script[demo.emitted.length % demo.script.length];
      const ev = { ...tpl.event, id: lastBase + demo.emitted.length + 1, ts: new Date().toISOString().replace(/\.\d+Z$/, 'Z') };
      demo.emitted.push(ev);
      if (tpl.state) demo.state = { ...demo.state, ...tpl.state, ...(tpl.state.king ? { since: ev.ts } : {}) };
      if (tpl.lieges && Array.isArray(demo.state.houses)) {
        demo.state.houses = demo.state.houses.map((h) => (h && Object.prototype.hasOwnProperty.call(tpl.lieges, h.name) ? { ...h, liege: tpl.lieges[h.name] } : h));
      }
    }
    return { events: baseEvents.concat(demo.emitted), state: { ...demo.state, updated: new Date().toISOString() } };
  }
  return {
    async state() {
      const base = await readJson('state.json', {});
      const events = await readJson('events.json', []);
      const { state } = await demoState(events, base);
      return { status: 200, body: { ...state, stale: false } };
    },
    async events(q) {
      const base = await readJson('events.json', []);
      const st = await readJson('state.json', {});
      const { events } = await demoState(base, st);
      return { status: 200, body: selectEvents(events.slice().sort((a, b) => a.id - b.id), q) };
    },
  };
}

const MAX_UPSTREAM_BYTES = 4 * 1024 * 1024;

function makeUpstreamSource(base, timeoutMs) {
  async function relay(path) {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
      const res = await fetch(base + path, { signal: ctrl.signal, redirect: 'error', headers: { Accept: 'application/json' } });
      const len = Number(res.headers.get('content-length') || 0);
      if (len > MAX_UPSTREAM_BYTES) return { status: 502, body: { error: 'chronicle response too large' } };
      const text = await res.text();
      if (text.length > MAX_UPSTREAM_BYTES) return { status: 502, body: { error: 'chronicle response too large' } };
      let body;
      try {
        body = JSON.parse(text);
      } catch {
        return { status: 502, body: { error: 'chronicle sent something that is not JSON' } };
      }
      if (res.status !== 200 && res.status !== 400) return { status: 502, body: { error: `chronicle answered HTTP ${res.status}` } };
      const warning = res.headers.get('x-realm-data-warning');
      return { status: res.status, body, headers: warning ? { 'X-Realm-Data-Warning': warning.slice(0, 200) } : {} };
    } catch (e) {
      return { status: 502, body: { error: `chronicle unreachable at ${base}` } };
    } finally {
      clearTimeout(timer);
    }
  }
  return {
    state: () => relay('/api/state'),
    // Only the two known parameters are forwarded, rebuilt from validated numbers.
    events: (q) => relay(`/api/events?limit=${q.limit}${q.since != null ? `&since=${q.since}` : ''}`),
  };
}

// ---------- app ----------

export function createApp(opts) {
  const source = opts.fixtures ? makeFixtureSource(opts.fixtures, opts.demo) : makeUpstreamSource(opts.upstream, opts.timeoutMs);

  async function handle(req, res) {
    // DNS rebinding guard: a page on some other site cannot reach us through a name that resolves to 127.0.0.1.
    const hostHeader = String(req.headers.host || '');
    const hostName = hostHeader.replace(/:\d+$/, '');
    if (!isLoopbackHost(hostName)) return send(res, 403, { error: 'loopback hosts only' });

    if (req.method !== 'GET' && req.method !== 'HEAD') {
      return send(res, 405, { error: 'method not allowed' }, { Allow: 'GET, HEAD' });
    }
    const url = new URL(req.url, 'http://localhost');
    const path = url.pathname.replace(/\/+$/, '') || '/';

    if (path === '/api/state') {
      const r = await source.state();
      return send(res, r.status, r.body, r.headers);
    }
    if (path === '/api/events') {
      const q = parseEventQuery(url.searchParams);
      if (q.error) return send(res, 400, { error: q.error });
      const r = await source.events(q);
      return send(res, r.status, r.body, r.headers);
    }
    if (path === '/schedule.json') {
      if (!opts.schedule) return send(res, 404, { error: 'no schedule configured' });
      try {
        const json = JSON.parse((await readFile(opts.schedule, 'utf8')).replace(/^﻿/, ''));
        return send(res, 200, json);
      } catch (e) {
        return send(res, 500, { error: `schedule file unreadable: ${e.code || e.message}` });
      }
    }
    if (path === '/healthz') {
      const r = await source.state();
      return send(res, 200, { ok: true, chronicle: r.status === 200 ? 'ok' : (r.body && r.body.error) || 'error', mode: opts.fixtures ? 'fixtures' : 'relay' });
    }
    if (path === '/') return serveStatic(res, req, 'index.html');
    const scene = path.slice(1).replace(/\.html$/, '');
    if (SCENES.includes(scene)) return serveStatic(res, req, `${scene}.html`);
    if (path.startsWith('/assets/')) return serveStatic(res, req, path.slice(1));
    return send(res, 404, { error: 'not found' });
  }

  return async (req, res) => {
    try {
      await handle(req, res);
    } catch (e) {
      console.error('[streamkit] request failed:', e);
      if (!res.headersSent) send(res, 500, { error: 'internal error' });
      else res.end();
    }
  };
}

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

async function serveStatic(res, req, rel) {
  let decoded;
  try {
    decoded = decodeURIComponent(rel);
  } catch {
    return send(res, 400, { error: 'bad path' });
  }
  const file = resolve(PUBLIC_DIR, decoded);
  if (!file.startsWith(PUBLIC_DIR + sep)) return send(res, 404, { error: 'not found' });
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
  let opts;
  try {
    opts = parseOptions();
  } catch (e) {
    console.error(`[streamkit] ${e.message}`);
    process.exit(2);
  }
  if (opts.help) {
    console.log('Usage: node server.js [--port 8790] [--host 127.0.0.1] [--upstream http://127.0.0.1:8787]\n' +
      '                      [--fixtures <dir>] [--demo <seconds>] [--schedule <schedule.json>]\n' +
      'Env: STREAMKIT_PORT, STREAMKIT_HOST, REALM_CHRONICLE_URL, STREAMKIT_FIXTURES, STREAMKIT_SCHEDULE');
    process.exit(0);
  }
  const server = createServer(createApp(opts));
  server.on('error', (e) => {
    console.error(`[streamkit] cannot listen on ${opts.host}:${opts.port}: ${e.message}`);
    process.exit(1);
  });
  server.listen(opts.port, opts.host, () => {
    const base = `http://${opts.host.includes(':') ? `[${opts.host}]` : opts.host}:${server.address().port}`;
    console.log(`[streamkit] data: ${opts.fixtures ? `fixtures in ${opts.fixtures}${opts.demo ? ` (demo event every ${opts.demo}s)` : ''}` : `chronicle at ${opts.upstream}`}`);
    if (opts.schedule) console.log(`[streamkit] schedule: ${opts.schedule}`);
    for (const s of SCENES) console.log(`[streamkit] ${s.padEnd(14)} ${base}/${s}`);
  });
}
