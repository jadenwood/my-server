// Client for the local Realm Chronicle service (chronicle/server.js): GET /api/state, /api/events, /healthz.
// Results are cached for a few seconds so a burst of commands makes one request. Every value is checked
// again here; the bot never trusts the shape of a response.

export class ChronicleUnavailable extends Error {
  constructor(message, cause) {
    super(message);
    this.name = 'ChronicleUnavailable';
    this.cause = cause;
  }
}

const str = (v, max) => (typeof v === 'string' && v ? v.slice(0, max) : null);
const int = (v) => (Number.isInteger(v) && v >= 0 ? v : 0);

export function cleanState(s) {
  s = s && typeof s === 'object' && !Array.isArray(s) ? s : {};
  return {
    king: str(s.king, 64),
    house: str(s.house, 64),
    since: str(s.since, 40),
    houses: (Array.isArray(s.houses) ? s.houses : [])
      .filter((h) => h && typeof h.name === 'string' && h.name)
      .slice(0, 64)
      .map((h) => ({ name: h.name.slice(0, 64), sigil: str(h.sigil, 64), liege: str(h.liege, 64), members: int(h.members) })),
    online: int(s.online),
    maxPlayers: int(s.maxPlayers),
    updated: str(s.updated, 40),
    stale: s.stale === true,
  };
}

export function cleanEvents(list) {
  return (Array.isArray(list) ? list : [])
    .filter((e) => e && Number.isInteger(e.id) && typeof e.type === 'string')
    .map((e) => ({
      id: e.id,
      ts: str(e.ts, 40),
      type: e.type.slice(0, 40),
      title: str(e.title, 200) || '',
      detail: str(e.detail, 600) || '',
      actors: Array.isArray(e.actors) ? e.actors.filter((a) => typeof a === 'string').slice(0, 8).map((a) => a.slice(0, 64)) : [],
    }))
    .sort((a, b) => a.id - b.id);
}

export function createChronicleClient({ baseUrl, fetchImpl = globalThis.fetch, timeoutMs = 4000, cacheMs = 5000, now = Date.now } = {}) {
  const base = baseUrl.replace(/\/+$/, '');
  const cache = new Map();

  async function getJson(path) {
    const hit = cache.get(path);
    if (hit && now() - hit.at < cacheMs) return hit.value;
    let res;
    try {
      res = await fetchImpl(base + path, { signal: AbortSignal.timeout(timeoutMs), headers: { Accept: 'application/json' } });
    } catch (e) {
      throw new ChronicleUnavailable(`Chronicle service did not answer at ${base} (${e.name === 'TimeoutError' ? 'timed out' : e.message})`, e);
    }
    if (!res.ok) throw new ChronicleUnavailable(`Chronicle service answered HTTP ${res.status} for ${path}`);
    let value;
    try {
      value = await res.json();
    } catch (e) {
      throw new ChronicleUnavailable(`Chronicle service sent something that is not JSON for ${path}`, e);
    }
    const warning = res.headers?.get?.('x-realm-data-warning') || null;
    const out = { value, warning };
    cache.set(path, { at: now(), value: out });
    return out;
  }

  return {
    baseUrl: base,
    async state() {
      const { value, warning } = await getJson('/api/state');
      return { ...cleanState(value), warning };
    },
    async events(limit = 50) {
      const n = Math.min(500, Math.max(1, Math.floor(limit)));
      const { value } = await getJson(`/api/events?limit=${n}`);
      return cleanEvents(value);
    },
    async health() {
      const { value } = await getJson('/healthz');
      return value && typeof value === 'object' ? value : { ok: false };
    },
    clearCache() {
      cache.clear();
    },
  };
}
