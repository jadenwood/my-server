// Realm Stream Scenes: pure data model. No DOM, no fetch, no clock: everything takes `now` (ms) as an
// argument so the same code runs in the browser scenes and in `node --test`.
//
// The chronicle API only serves {king, house, houses[{name, sigil, liege, members}]} plus the event feed.
// Treaties, claims and rebellion windows are therefore rebuilt from the event wording that the plugins
// write (plugins/RealmHouses.cs, plugins/CrownAndConsequences.cs, plugins/RealmEvents.cs). If a plugin
// changes that wording, the parsers below fall back to safe defaults instead of failing.

export const MINUTE = 60 * 1000;
export const HOUR = 60 * MINUTE;
export const DAY = 24 * HOUR;

const DAYS = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];

// Defaults copied from the plugin configs. They only matter when the scene is not given a schedule
// (URL params or /schedule.json) and must match the server's real config to be right.
export const DEFAULT_REBELLION_WINDOWS = [
  { day: 3, minuteOfDay: 19 * 60, minutes: 60 },   // CrownAndConsequences: Wednesday 19:00, 60 min
  { day: 6, minuteOfDay: 19 * 60, minutes: 90 },   // Saturday 19:00, 90 min
];
export const DEFAULT_REALM_EVENTS = [
  { id: 'crown-night', name: 'Crown Night', day: 6, minuteOfDay: 19 * 60, minutes: 90 },
  { id: 'royal-tournament', name: 'The Royal Tournament', day: 5, minuteOfDay: 19 * 60, minutes: 60 },
  { id: 'kings-hunt', name: "The King's Hunt", day: 3, minuteOfDay: 21 * 60, minutes: 60 },
  { id: 'truce', name: 'The Truce of the Realm', day: 0, minuteOfDay: 12 * 60, minutes: 240 },
];
export const DEFAULT_TREATY_DAYS = 7; // RealmHouses TreatyDefaultDays; used when the detail has no term

// Event types the Breaking News scene shows by default: betrayals, rebellions and the fall of a crown.
export const BREAKING_TYPES = [
  'oath_broken', 'treaty_broken', 'truce_broken', 'claim_declared', 'rebellion_started', 'rebellion_ended',
  'abdication', 'blood_claim', 'accusation', 'trial_by_combat',
];
const URGENT_TYPES = new Set(['rebellion_started', 'abdication', 'oath_broken', 'treaty_broken', 'truce_broken']);

// Lore lines for the six great houses (docs/community/lore.md). Other houses simply have none.
export const HOUSE_WORDS = {
  varrow: 'We Stand Our Ground.',
  ashgrove: 'Deep Roots, Long Memory.',
  corvane: 'Every Secret Has a Price.',
  dunmere: 'The Tide Returns.',
  halloran: 'Loyal Until the Last Coal.',
  merrin: 'Slip the Net.',
};

const key = (s) => String(s || '').trim().toLowerCase();
const pairKey = (a, b) => [key(a), key(b)].sort().join('|');
const tsOf = (e) => {
  const t = Date.parse(e && e.ts);
  return Number.isNaN(t) ? null : t;
};
const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// ---------- schedule ----------

export function parseDay(s) {
  const k = key(s);
  if (!k) return -1;
  return DAYS.findIndex((d) => d === k || (k.length >= 3 && d.startsWith(k)));
}

export function parseHHMM(s) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(s || '').trim());
  if (!m) return -1;
  const h = Number(m[1]), mi = Number(m[2]);
  return h < 24 && mi < 60 ? h * 60 + mi : -1;
}

// "Saturday@19:00+90" -> {day: 6, minuteOfDay: 1140, minutes: 90}. Returns null when malformed.
export function parseSlot(text) {
  const m = /^\s*([A-Za-z]+)\s*@\s*(\d{1,2}:\d{2})\s*(?:\+\s*(\d{1,4}))?\s*$/.exec(String(text || ''));
  if (!m) return null;
  const day = parseDay(m[1]);
  const minuteOfDay = parseHHMM(m[2]);
  const minutes = m[3] ? Math.max(1, Number(m[3])) : 60;
  if (day < 0 || minuteOfDay < 0) return null;
  return { day, minuteOfDay, minutes };
}

// "Wednesday@19:00+60,Saturday@19:00+90" -> windows. Bad entries are skipped.
export function parseWindowsParam(text) {
  return String(text || '').split(',').map(parseSlot).filter(Boolean);
}

// "Crown Night=Saturday@19:00+90;Royal Tournament=Friday@19:00+60" -> realm events.
export function parseEventsParam(text) {
  return String(text || '').split(';').map((part) => {
    const i = part.indexOf('=');
    if (i <= 0) return null;
    const name = part.slice(0, i).trim().slice(0, 60);
    const slot = parseSlot(part.slice(i + 1));
    return name && slot ? { id: key(name).replace(/[^a-z0-9]+/g, '-'), name, ...slot } : null;
  }).filter(Boolean);
}

// Accepts the shape of streamkit/schedule.example.json; anything malformed is dropped.
export function normalizeSchedule(json) {
  const out = { offsetHours: 0, windows: null, events: null };
  if (!json || typeof json !== 'object') return out;
  const off = Number(json.utcOffsetHours);
  if (Number.isFinite(off) && Math.abs(off) <= 14) out.offsetHours = off;
  if (Array.isArray(json.rebellionWindows)) {
    out.windows = json.rebellionWindows.map((w) => {
      const day = parseDay(w && w.day);
      const minuteOfDay = parseHHMM(w && w.start);
      const minutes = Math.max(1, Number(w && w.durationMinutes) || 60);
      return day >= 0 && minuteOfDay >= 0 ? { day, minuteOfDay, minutes } : null;
    }).filter(Boolean);
  }
  if (Array.isArray(json.events)) {
    out.events = [];
    for (const e of json.events) {
      if (!e || e.enabled === false || typeof e.name !== 'string') continue;
      const days = Array.isArray(e.days) ? e.days : [e.day];
      const minuteOfDay = parseHHMM(e.startUtc);
      const minutes = Math.max(1, Number(e.durationMinutes) || 60);
      for (const d of days) {
        const day = parseDay(d);
        if (day >= 0 && minuteOfDay >= 0) {
          out.events.push({ id: String(e.id || key(e.name)).slice(0, 40), name: e.name.slice(0, 60), day, minuteOfDay, minutes });
        }
      }
    }
  }
  return out;
}

// Same rule as CrownAndConsequences.NextWindow: the earliest start at or after `notBefore`, looking at
// most 8 realm days ahead. `offsetHours` is the realm's UTC offset (0 for RealmEvents, which is UTC).
export function nextOccurrence(slot, notBefore, offsetHours = 0) {
  const off = offsetHours * HOUR;
  const realm = new Date(notBefore + off);
  const realmDay = Date.UTC(realm.getUTCFullYear(), realm.getUTCMonth(), realm.getUTCDate());
  for (let i = 0; i <= 8; i++) {
    const local = realmDay + i * DAY;
    if (new Date(local).getUTCDay() !== slot.day) continue;
    const candidate = local + slot.minuteOfDay * MINUTE - off;
    if (candidate < notBefore) continue;
    return candidate;
  }
  return null;
}

// The slot that is open right now, or the next one to open. {start, end, open, slot}
export function nextSlot(slots, now, offsetHours = 0) {
  let best = null;
  for (const slot of slots || []) {
    // Starting the search one duration back finds a window that is already open.
    const start = nextOccurrence(slot, now - slot.minutes * MINUTE, offsetHours);
    if (start == null) continue;
    const end = start + slot.minutes * MINUTE;
    const open = start <= now && now < end;
    const cand = { start, end, open, slot };
    if (!best || (open && !best.open) || (open === best.open && start < best.start)) best = cand;
  }
  return best;
}

// ---------- event parsing ----------

const RE = {
  founded: /^House (.+?) is founded$/,
  oathSworn: /^House (.+?) swears fealty to House (.+?)$/,
  oathBroken: /^House (.+?) renounces its oath to House (.+?)$/,
  treatySigned: /^House (.+?) and House (.+?) sign a treaty$/,
  treatyBroken: /^House (.+?) breaks its treaty with House (.+?)$/,
  claim: /^House (.+?) claims the crown$/,
  rises: /^House (.+?) rises$/,
  rebellionEnded: /^The rebellion of House (.+?) ends$/,
  coronation: /^(.+?) of (.+?) takes the throne$/,
  treatyTerm: /holds for (\d{1,3}) days?/i,
  opens: /opens (?:on )?([A-Za-z]+) (\d{1,2}:\d{2}) UTC/,
  until: /until (\d{1,2}:\d{2}) UTC/,
  against: /against (.+?) of (.+?)\.(?:\s|$)/,
  eventBegins: /^(.+?) begins$/,
  eventEnds: /^(.+?) (?:ends|is cancelled|is called off)$/,
};

export function parseCoronation(e) {
  const m = RE.coronation.exec((e && e.title) || '');
  if (m) return { king: m[1], house: m[2] === 'no house' ? null : m[2] };
  // Older wording ("<king> takes the throne", house in the detail) still gives the king.
  const k = /^(.+?) takes the throne$/.exec((e && e.title) || '');
  const h = /House (\S+?) now holds the crown/.exec((e && e.detail) || '');
  return { king: k ? k[1] : (e && e.actors && e.actors[0]) || null, house: h ? h[1] : null };
}

// The next time HH:MM (UTC) occurs strictly after `after`.
function nextClock(minuteOfDay, after) {
  const d = new Date(after);
  let t = Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()) + minuteOfDay * MINUTE;
  while (t <= after) t += DAY;
  return t;
}

// Claim window from a claim_declared event: "... The rebellion opens Saturday 19:00 UTC."
export function claimWindow(e, windows = DEFAULT_REBELLION_WINDOWS, offsetHours = 0) {
  const ts = tsOf(e);
  const m = RE.opens.exec((e && e.detail) || '');
  if (ts == null || !m) return { start: null, end: null };
  const slot = { day: parseDay(m[1]), minuteOfDay: parseHHMM(m[2]), minutes: 60 };
  if (slot.day < 0 || slot.minuteOfDay < 0) return { start: null, end: null };
  const start = nextOccurrence(slot, ts, 0); // the detail is written in UTC
  if (start == null) return { start: null, end: null };
  // The length comes from the configured window that starts at the same instant.
  let minutes = 60;
  for (const w of windows || []) {
    if (nextOccurrence(w, ts, offsetHours) === start) { minutes = w.minutes; break; }
  }
  return { start, end: start + minutes * MINUTE };
}

// ---------- house filter ----------

export function parseHouseFilter(text) {
  return String(text || '').split(',').map((s) => s.trim().replace(/^house\s+/i, '')).filter(Boolean).slice(0, 16);
}

export function eventMentions(e, houses) {
  if (!houses || !houses.length) return true;
  const hay = `${(e && e.title) || ''}\n${(e && e.detail) || ''}\n${((e && e.actors) || []).join('\n')}`;
  return houses.some((h) => new RegExp(`(^|[^\\p{L}\\p{N}])${escapeRe(h)}($|[^\\p{L}\\p{N}])`, 'iu').test(hay));
}

export const houseIn = (name, houses) => !houses || !houses.length || houses.some((h) => key(h) === key(name));

// ---------- war board ----------

export function buildWarBoard(state, events, opts = {}) {
  const now = opts.now ?? Date.now();
  const windows = opts.windows || DEFAULT_REBELLION_WINDOWS;
  const offsetHours = opts.offsetHours || 0;
  const brokenShowMs = opts.brokenShowMs ?? DAY;
  const s = state || {};
  const list = (Array.isArray(events) ? events : []).filter((e) => e && typeof e === 'object').sort((a, b) => a.id - b.id);

  const nodes = new Map();
  for (const h of Array.isArray(s.houses) ? s.houses : []) {
    if (!h || !h.name || nodes.has(key(h.name))) continue;
    nodes.set(key(h.name), {
      name: h.name, sigil: h.sigil || null, liege: h.liege || null, members: h.members | 0,
      crown: !!s.house && key(s.house) === key(h.name),
      oathsBroken: 0, treatiesBroken: 0, betrayedAt: null, vassals: 0,
    });
  }
  const canon = (n) => (nodes.get(key(n)) || {}).name || null;

  const treaties = new Map();
  const claims = new Map();
  const recent = [];

  for (const e of list) {
    const ts = tsOf(e);
    const title = e.title || '';
    let m;
    if (e.type === 'treaty_signed' && (m = RE.treatySigned.exec(title))) {
      const days = Number((RE.treatyTerm.exec(e.detail || '') || [])[1]) || DEFAULT_TREATY_DAYS;
      treaties.set(pairKey(m[1], m[2]), {
        a: m[1], b: m[2], signedAt: ts, expiresAt: ts == null ? null : ts + days * DAY, days, status: 'active', eventId: e.id,
      });
    } else if (e.type === 'treaty_broken' && (m = RE.treatyBroken.exec(title))) {
      const t = treaties.get(pairKey(m[1], m[2])) || { a: m[1], b: m[2], signedAt: null, expiresAt: null, days: null };
      treaties.set(pairKey(m[1], m[2]), { ...t, status: 'broken', breaker: m[1], brokenAt: ts, eventId: e.id });
      const n = nodes.get(key(m[1]));
      if (n) { n.treatiesBroken++; n.betrayedAt = ts; }
    } else if (e.type === 'oath_broken' && (m = RE.oathBroken.exec(title))) {
      const n = nodes.get(key(m[1]));
      if (n) { n.oathsBroken++; n.betrayedAt = ts; }
      recent.push({ kind: 'oath_broken', from: m[1], to: m[2], at: ts });
    } else if (e.type === 'claim_declared' && (m = RE.claim.exec(title))) {
      const w = claimWindow(e, windows, offsetHours);
      const ag = RE.against.exec(e.detail || '');
      claims.set(key(m[1]), {
        house: m[1], declaredAt: ts, status: 'pending', windowStart: w.start, windowEnd: w.end,
        against: ag ? ag[1] : (e.actors && e.actors[1]) || null,
        againstHouse: ag && ag[2] !== 'no house' ? ag[2] : null, eventId: e.id,
      });
    } else if (e.type === 'rebellion_started' && (m = RE.rises.exec(title))) {
      const c = claims.get(key(m[1])) || { house: m[1], declaredAt: ts, against: null, againstHouse: null, windowStart: ts };
      const u = RE.until.exec(e.detail || '');
      const end = u && ts != null ? nextClock(parseHHMM(u[1]), ts) : c.windowEnd || (ts == null ? null : ts + HOUR);
      claims.set(key(m[1]), { ...c, status: 'active', startedAt: ts, windowEnd: end, eventId: e.id });
    } else if (e.type === 'rebellion_ended' && (m = RE.rebellionEnded.exec(title))) {
      claims.delete(key(m[1]));
    }
  }

  const treatyList = [];
  for (const t of treaties.values()) {
    if (!canon(t.a) || !canon(t.b)) continue; // a disbanded house takes its treaties with it
    if (t.status === 'active' && t.expiresAt != null && t.expiresAt <= now) continue;
    if (t.status === 'broken' && !(t.brokenAt != null && now - t.brokenAt < brokenShowMs)) continue;
    treatyList.push({ ...t, a: canon(t.a), b: canon(t.b), breaker: t.breaker ? canon(t.breaker) || t.breaker : undefined });
  }
  treatyList.sort((x, y) => (x.status === y.status ? (y.signedAt || 0) - (x.signedAt || 0) : x.status === 'active' ? -1 : 1));

  const claimList = [];
  for (const c of claims.values()) {
    if (!canon(c.house)) continue;
    // A claim the plugin never closed (server down at the time) lapses ten minutes after its window.
    if (c.windowEnd != null && c.windowEnd + 10 * MINUTE < now) continue;
    if (c.windowEnd == null && c.declaredAt != null && now - c.declaredAt > 9 * DAY) continue;
    let status = c.status;
    if (status === 'pending' && c.windowStart != null && c.windowStart <= now) status = 'active';
    claimList.push({ ...c, status, house: canon(c.house), againstHouse: c.againstHouse ? canon(c.againstHouse) || c.againstHouse : null });
  }
  claimList.sort((x, y) => (x.windowStart ?? Infinity) - (y.windowStart ?? Infinity));

  const lieges = [];
  for (const n of nodes.values()) {
    const l = n.liege && nodes.get(key(n.liege));
    if (l && l !== n) { lieges.push({ vassal: n.name, liege: l.name }); l.vassals++; }
  }
  for (const n of nodes.values()) {
    n.claiming = claimList.some((c) => key(c.house) === key(n.name));
    n.betrayedRecently = n.betrayedAt != null && now - n.betrayedAt < brokenShowMs;
  }

  return {
    king: s.king || null, crownHouse: s.house ? canon(s.house) || s.house : null,
    nodes: [...nodes.values()], lieges, treaties: treatyList, claims: claimList, recentBetrayals: recent.slice(-5),
  };
}

// Places the liege forest on a unit square. Roots go in the top row, vassals under their liege; the
// crown's realm sits in the middle. Returns {name: {x, y, depth}} with x, y in [0, 1].
export function layoutForest(nodes) {
  const byKey = new Map(nodes.map((n) => [key(n.name), n]));
  const parentOf = (n) => {
    // Walk up and break cycles (A sworn to B sworn to A cannot happen in the plugin, but be safe).
    const l = n.liege && byKey.get(key(n.liege));
    if (!l || l === n) return null;
    const seen = new Set([n]);
    for (let p = l; p; p = p.liege ? byKey.get(key(p.liege)) : null) {
      if (seen.has(p)) return null;
      seen.add(p);
    }
    return l;
  };
  const children = new Map(nodes.map((n) => [n, []]));
  const roots = [];
  for (const n of nodes) {
    const p = parentOf(n);
    if (p) children.get(p).push(n); else roots.push(n);
  }
  const weight = (n) => n.members + children.get(n).reduce((s, c) => s + weight(c), 0);
  const order = (a, b) => weight(b) - weight(a) || a.name.localeCompare(b.name);
  for (const list of children.values()) list.sort(order);
  roots.sort(order);
  // Crown realm in the middle, the rest alternating left and right by size.
  const crownIdx = roots.findIndex((r) => r.crown || children.get(r).some(function has(c) { return c.crown || children.get(c).some(has); }));
  let arranged = roots;
  if (crownIdx >= 0) {
    const crownRoot = roots[crownIdx];
    const rest = roots.filter((_, i) => i !== crownIdx);
    const left = [], right = [];
    rest.forEach((r, i) => (i % 2 ? left : right).push(r));
    arranged = [...left.reverse(), crownRoot, ...right];
  }
  const leaves = (n) => Math.max(1, children.get(n).reduce((s, c) => s + leaves(c), 0));
  const total = arranged.reduce((s, r) => s + leaves(r), 0) || 1;
  let maxDepth = 0;
  const pos = {};
  const place = (n, start, depth) => {
    maxDepth = Math.max(maxDepth, depth);
    const w = leaves(n);
    pos[n.name] = { x: (start + w / 2) / total, depth };
    let s = start;
    for (const c of children.get(n)) { place(c, s, depth + 1); s += leaves(c); }
  };
  let s = 0;
  for (const r of arranged) { place(r, s, 0); s += leaves(r); }
  for (const p of Object.values(pos)) p.y = maxDepth === 0 ? 0.5 : p.depth / maxDepth;
  return pos;
}

// ---------- countdown ----------

// Finds the live realm event (event_started with no matching end) from the chronicle.
export function liveRealmEvent(events, schedule, now) {
  const list = (Array.isArray(events) ? events : []).filter((e) => e && typeof e === 'object').sort((a, b) => a.id - b.id);
  let live = null;
  for (const e of list) {
    let m;
    if (e.type === 'event_started' && (m = RE.eventBegins.exec(e.title || ''))) live = { name: m[1], startedAt: tsOf(e) };
    else if (e.type === 'event_ended' && live && (m = RE.eventEnds.exec(e.title || '')) && key(m[1]) === key(live.name)) live = null;
  }
  if (!live || live.startedAt == null) return null;
  const slot = (schedule || []).find((s) => key(s.name) === key(live.name));
  const end = live.startedAt + (slot ? slot.minutes : 60) * MINUTE;
  return end > now ? { ...live, end } : null;
}

// Picks what the Countdown scene counts to. `mode`: auto | rebellion | event | custom.
export function chooseCountdown({ board, events, now, mode = 'auto', windows, realmEvents, offsetHours = 0, custom, houses }) {
  const claims = (board ? board.claims : []).filter((c) => houseIn(c.house, houses));
  const live = claims.find((c) => c.status === 'active' && c.windowEnd != null && c.windowEnd > now);
  const pending = claims.find((c) => c.status === 'pending' && c.windowStart != null && c.windowStart > now);
  const win = nextSlot(windows || DEFAULT_REBELLION_WINDOWS, now, offsetHours);
  const sched = realmEvents || DEFAULT_REALM_EVENTS;
  const liveEvt = liveRealmEvent(events, sched, now);
  let nextEvt = null;
  for (const ev of sched) {
    const start = nextOccurrence(ev, now, 0);
    if (start != null && (!nextEvt || start < nextEvt.at)) nextEvt = { at: start, ev };
  }

  const out = {
    customT: custom && custom.at > now ? { kind: 'custom', at: custom.at, title: custom.label || 'The realm stirs', sub: custom.sub || '' } : null,
    liveRebellion: live ? {
      kind: 'rebellion-live', at: live.windowEnd, house: live.house, live: true,
      title: `The throne is contested`, sub: `The rebellion of House ${live.house} ends in`,
    } : null,
    claim: pending ? {
      kind: 'claim', at: pending.windowStart, house: pending.house,
      title: 'The Lawful Hours open in',
      sub: `House ${pending.house} has claimed the crown${pending.against ? ` from ${pending.against}` : ''}`,
    } : null,
    window: win ? (win.open ? {
      kind: 'window-live', at: win.end, live: true, title: 'The Lawful Hours are open', sub: 'The rebellion window closes in',
    } : {
      kind: 'window', at: win.start, title: 'The next Lawful Hours open in', sub: 'Any declared claim may be fought for then',
    }) : null,
    liveEvent: liveEvt ? { kind: 'event-live', at: liveEvt.end, live: true, title: liveEvt.name, sub: `${liveEvt.name} ends in` } : null,
    event: nextEvt ? { kind: 'event', at: nextEvt.at, title: `${nextEvt.ev.name} begins in`, sub: 'A realm event on the herald\'s calendar' } : null,
  };
  const pick = (...names) => names.map((n) => out[n]).find(Boolean) || null;
  if (mode === 'custom') return pick('customT');
  if (mode === 'rebellion') return pick('customT', 'liveRebellion', 'claim', 'window');
  if (mode === 'event') return pick('customT', 'liveEvent', 'event');
  // auto: a custom target, then anything live, then a declared claim, then the soonest scheduled thing.
  const live1 = pick('customT', 'liveRebellion', 'liveEvent', 'claim');
  if (live1) return live1;
  if (out.window && out.window.live) return out.window;
  const soon = [out.window, out.event].filter(Boolean).sort((a, b) => a.at - b.at);
  return soon[0] || null;
}

export function splitDuration(ms) {
  let s = Math.max(0, Math.floor(ms / 1000));
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60); s -= m * 60;
  return { d, h, m, s };
}

export function formatUtc(ms) {
  if (ms == null) return '';
  const d = new Date(ms);
  const day = DAYS[d.getUTCDay()];
  const pad = (n) => String(n).padStart(2, '0');
  return `${day[0].toUpperCase()}${day.slice(1)} ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())} UTC`;
}

// "15m", "1h30m", "90s", "45" (minutes) -> ms; null when not parseable.
export function parseDurationParam(text) {
  const t = String(text || '').trim().toLowerCase();
  if (!t) return null;
  if (/^\d+$/.test(t)) return Number(t) * MINUTE;
  const re = /(\d+)\s*(d|h|m|s)/g;
  let total = 0, any = false, m, consumed = '';
  while ((m = re.exec(t))) {
    any = true; consumed += m[0];
    total += Number(m[1]) * { d: DAY, h: HOUR, m: MINUTE, s: 1000 }[m[2]];
  }
  return any && consumed.replace(/\s/g, '') === t.replace(/\s/g, '') ? total : null;
}

// ---------- breaking news queue ----------

export function breakingSeverity(type) {
  return URGENT_TYPES.has(type) ? 'urgent' : 'alert';
}

// A small FIFO with de-duplication by event id and a cap, so a burst of 40 events does not keep the
// alert on screen for ten minutes: the oldest are dropped and counted.
export class AlertQueue {
  constructor({ max = 8, types = BREAKING_TYPES, houses = [] } = {}) {
    this.max = max;
    this.types = new Set(types);
    this.houses = houses;
    this.items = [];
    this.seen = new Set();
    this.dropped = 0;
  }
  accepts(e) {
    return !!e && this.types.has(e.type) && eventMentions(e, this.houses);
  }
  push(events) {
    let added = 0;
    for (const e of events || []) {
      if (!this.accepts(e) || this.seen.has(e.id)) continue;
      this.seen.add(e.id);
      this.items.push(e);
      added++;
    }
    // Urgent items jump ahead of plain alerts, keeping arrival order within each tier.
    this.items.sort((a, b) => (breakingSeverity(a.type) === breakingSeverity(b.type) ? a.id - b.id
      : breakingSeverity(a.type) === 'urgent' ? -1 : 1));
    while (this.items.length > this.max) {
      // Drop the oldest plain alert first, then the oldest of anything.
      let idx = -1;
      for (let i = this.items.length - 1; i >= 0; i--) if (breakingSeverity(this.items[i].type) !== 'urgent') idx = i;
      if (idx < 0) idx = 0;
      this.items.splice(idx, 1);
      this.dropped++;
    }
    if (this.seen.size > 2000) this.seen = new Set([...this.seen].slice(-1000));
    return added;
  }
  next() {
    return this.items.shift() || null;
  }
  get length() {
    return this.items.length;
  }
}

// ---------- shared URL params ----------

export function parseKitParams(search, defaults = {}) {
  const p = new URLSearchParams(search || '');
  const num = (name, lo, hi, dflt) => {
    const v = Number(p.get(name));
    return p.has(name) && Number.isFinite(v) ? Math.min(hi, Math.max(lo, v)) : dflt;
  };
  const flag = (name, dflt) => {
    if (!p.has(name)) return dflt;
    const v = key(p.get(name));
    return !(v === '0' || v === 'false' || v === 'no' || v === 'off');
  };
  const nowRaw = p.get('now');
  const now = nowRaw ? Date.parse(nowRaw) : NaN;
  const motion = key(p.get('motion'));
  return {
    scale: num('scale', 0.25, 4, 1),
    transparent: flag('transparent', defaults.transparent ?? true),
    houses: parseHouseFilter(p.get('house') || p.get('houses')),
    reducedMotion: motion === '0' || motion === 'off' || motion === 'reduced' || flag('reduced', false),
    poll: num('poll', 1, 60, 3),
    hold: num('hold', 3, 120, defaults.hold ?? 10),
    replay: num('replay', 0, 20, 0),
    frozenNow: Number.isNaN(now) ? null : now,
    params: p,
  };
}
