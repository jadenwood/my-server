// Tolerant readers for the realm data files (oxide/data/*.json, written by the Realm plugins).
//
// Every reader whitelists the public fields it needs and drops the rest. In particular, Steam ids
// (RealmHouses member Id, CrownAndConsequences KingId/Council ids, captivity records) never leave
// this module. A missing or unreadable file is a warning, never a crash: the portal still builds.
//
// Files and how sure we are of their shape:
//   RealmChronicle.json        [SRC] plugins/RealmChronicle.cs (ChronicleEvent: id, ts, type, title, detail, actors)
//   RealmState.json            [SRC] plugins/RealmChronicle.cs (RealmStateData)
//   RealmHouses.json           [SRC] plugins/RealmHouses.cs (StoredData: Houses[], Treaties[])
//   CrownAndConsequences.json  [SRC] plugins/CrownAndConsequences.cs (StoredData: Claims[], CouncilNames, ActiveDecrees[])
//   RealmSeasons.json          [SRC] plugins/RealmSeasons.cs (SeasonData: Number, Name, Active, StartedAt, EndsAt,
//                              Houses{name: HouseStanding}). The reader also accepts a few other shapes (see sanitizeSeasons).
//   RealmLegends.json          [SRC] plugins/RealmSeasons.cs (Legends: Current, Hall[], Seasons[]) - the Hall of Kings
//                              that outlives wipes.
//   RealmEvents.json (data)    [SRC] plugins/RealmEvents.cs (StoredData.Active[]: Kind, Start, End) - events running now.
//   ../config/RealmEvents.json [SRC] plugins/RealmEvents.cs (PluginConfig.Schedule[], Enable* flags) - the weekly schedule.
//                              Read only when the config folder is given or sits next to the data folder.

import { readFile } from 'node:fs/promises';
import { join } from 'node:path';

const TYPE_RE = /^[a-z][a-z0-9_]{1,31}$/;
// A RealmHouses member Name falls back to the player id when the name is unknown (AddMember: Name = name ?? id).
const LOOKS_LIKE_ID = /^\d{15,20}$/;

export async function readJson(path) {
  try {
    const text = (await readFile(path, 'utf8')).replace(/^﻿/, '');
    return { value: JSON.parse(text), error: null };
  } catch (e) {
    if (e && e.code === 'ENOENT') return { value: null, error: 'missing' };
    return { value: null, error: `unreadable: ${e && e.message ? e.message : e}` };
  }
}

// Looks for `name` in each data dir in order; the first dir that has it wins.
async function readFirst(dirs, name, warnings) {
  let lastErr = 'missing';
  for (const dir of dirs) {
    const r = await readJson(join(dir, name));
    if (r.value !== null) return r.value;
    if (r.error !== 'missing') lastErr = r.error;
  }
  if (lastErr !== 'missing') warnings.push(`${name}: ${lastErr}`);
  return null;
}

const str = (v, max) => (typeof v === 'string' && v.trim() ? v.trim().slice(0, max) : null);
const int = (v) => (Number.isFinite(v) ? Math.max(0, Math.trunc(v)) : 0);
const publicName = (v, max = 64) => {
  const s = str(v, max);
  return s && !LOOKS_LIKE_ID.test(s) ? s : null;
};

// Case-insensitive property lookup over several candidate keys ("StartedAt", "startedAt", "started_at").
export function pick(obj, ...keys) {
  if (!obj || typeof obj !== 'object') return undefined;
  const map = new Map(Object.keys(obj).map((k) => [k.toLowerCase().replace(/_/g, ''), k]));
  for (const k of keys) {
    const real = map.get(k.toLowerCase().replace(/_/g, ''));
    if (real !== undefined && obj[real] !== undefined && obj[real] !== null) return obj[real];
  }
  return undefined;
}

export function normalizeIso(v) {
  if (typeof v !== 'string' || !v) return null;
  if (/^0001-01-01/.test(v)) return null; // .NET DateTime.MinValue
  const withZone = /[zZ]|[+-]\d\d:?\d\d$/.test(v) ? v : `${v}Z`; // the plugins write UTC
  const t = Date.parse(withZone);
  return Number.isNaN(t) ? null : new Date(t).toISOString().replace('.000Z', 'Z');
}

export function sanitizeEvent(e) {
  if (!e || typeof e !== 'object' || !Number.isInteger(e.id) || typeof e.type !== 'string' || !TYPE_RE.test(e.type)) return null;
  return {
    id: e.id,
    ts: normalizeIso(e.ts),
    type: e.type,
    title: str(e.title, 200) || '',
    detail: str(e.detail, 600) || '',
    actors: Array.isArray(e.actors) ? e.actors.map((a) => publicName(a)).filter(Boolean).slice(0, 8) : [],
  };
}

export function sanitizeState(s) {
  s = s && typeof s === 'object' ? s : {};
  const houses = Array.isArray(s.houses) ? s.houses : [];
  return {
    king: publicName(s.king),
    house: str(s.house, 64),
    since: s.king ? normalizeIso(s.since) : null,
    houses: houses
      .filter((h) => h && typeof h.name === 'string' && h.name.trim())
      .slice(0, 64)
      .map((h) => ({ name: h.name.trim().slice(0, 64), sigil: str(h.sigil, 64), liege: str(h.liege, 64), members: int(h.members) })),
    online: int(s.online),
    maxPlayers: int(s.maxPlayers),
    updated: normalizeIso(s.updated),
  };
}

// RealmHouses.json -> public house records and treaties. Member ids are dropped here.
export function sanitizeHousesData(raw) {
  const out = { houses: [], treaties: [] };
  if (!raw || typeof raw !== 'object') return out;
  const houses = pick(raw, 'Houses');
  if (Array.isArray(houses)) {
    for (const h of houses.slice(0, 128)) {
      const name = str(pick(h, 'Name'), 64);
      if (!name) continue;
      const members = Array.isArray(pick(h, 'Members')) ? pick(h, 'Members') : [];
      const byRank = (rank) => members.filter((m) => String(pick(m, 'Rank') || '').toLowerCase() === rank).map((m) => publicName(pick(m, 'Name'))).filter(Boolean);
      out.houses.push({
        name,
        sigil: str(pick(h, 'Sigil'), 64),
        founded: normalizeIso(pick(h, 'Founded')),
        liege: str(pick(h, 'Liege'), 64),
        swornSince: normalizeIso(pick(h, 'SwornSince')),
        oathsBroken: int(pick(h, 'OathsBroken')),
        treatiesBroken: int(pick(h, 'TreatiesBroken')),
        members: members.length,
        leader: byRank('leader')[0] || null,
        officers: byRank('officer').slice(0, 3),
        // Public names only, used to match chronicle actors to a house; never rendered as a list.
        roster: members.map((m) => publicName(pick(m, 'Name'))).filter(Boolean).slice(0, 200),
      });
    }
  }
  const treaties = pick(raw, 'Treaties');
  if (Array.isArray(treaties)) {
    for (const t of treaties.slice(0, 256)) {
      const a = str(pick(t, 'A'), 64);
      const b = str(pick(t, 'B'), 64);
      if (!a || !b) continue;
      out.treaties.push({ a, b, signed: normalizeIso(pick(t, 'Signed')), expires: normalizeIso(pick(t, 'Expires')) });
    }
  }
  return out;
}

// CrownAndConsequences.json -> open claims, council names, active decrees. Ids are dropped.
export function sanitizeCrownData(raw) {
  const out = { claims: [], council: [], decrees: [], authority: null };
  if (!raw || typeof raw !== 'object') return out;
  const claims = pick(raw, 'Claims');
  if (Array.isArray(claims)) {
    for (const c of claims.slice(0, 32)) {
      const status = String(pick(c, 'Status') || '').toLowerCase();
      out.claims.push({
        house: str(pick(c, 'House'), 64),
        declaredBy: publicName(pick(c, 'DeclaredBy')),
        against: str(pick(c, 'CrownHouseAtDeclaration'), 64),
        declaredAt: normalizeIso(pick(c, 'DeclaredAt')),
        windowStart: normalizeIso(pick(c, 'WindowStart')),
        windowEnd: normalizeIso(pick(c, 'WindowEnd')),
        status: ['pending', 'active', 'ended'].includes(status) ? status : 'ended',
        outcome: str(pick(c, 'Outcome'), 64),
      });
    }
  }
  const names = pick(raw, 'CouncilNames');
  if (names && typeof names === 'object' && !Array.isArray(names)) {
    for (const [seat, name] of Object.entries(names).slice(0, 12)) {
      const n = publicName(name);
      if (n) out.council.push({ seat: String(seat).slice(0, 48), name: n });
    }
  }
  const decrees = pick(raw, 'ActiveDecrees');
  if (Array.isArray(decrees)) {
    for (const d of decrees.slice(0, 16)) {
      const id = str(pick(d, 'Id'), 48);
      if (id) out.decrees.push({ id, expiresAt: normalizeIso(pick(d, 'ExpiresAt')) });
    }
  }
  const a = pick(raw, 'Authority');
  if (Number.isFinite(a)) out.authority = Math.round(a);
  return out;
}

// RealmSeasons default score weights (plugins/RealmSeasons.cs ScoreWeights). The server's
// oxide/config/RealmSeasons.json may change them, so portal scores are labelled "by the default weights".
export const SEASON_WEIGHTS = { CrownDay: 10, RebellionWon: 25, RebellionDefended: 15, TreatyKept: 5, TreatyBroken: -10, OathBroken: -10, ContractFulfilled: 2, EventPointsFactor: 1 };

function standingOf(h, w = SEASON_WEIGHTS) {
  const n = (k) => (Number.isFinite(pick(h, k)) ? pick(h, k) : 0);
  const crownDays = n('CrownSeconds') / 86400;
  const score = Math.round(crownDays * w.CrownDay + n('RebellionsWon') * w.RebellionWon + n('RebellionsDefended') * w.RebellionDefended
    + n('TreatiesKept') * w.TreatyKept + n('TreatiesBroken') * w.TreatyBroken + n('OathsBroken') * w.OathBroken
    + n('ContractsFulfilled') * w.ContractFulfilled + n('EventPoints') * w.EventPointsFactor);
  const honours = pick(h, 'Honours');
  return {
    house: str(pick(h, 'House'), 64),
    score,
    crownDays: Math.round(crownDays * 100) / 100,
    rebellionsWon: int(n('RebellionsWon')),
    rebellionsDefended: int(n('RebellionsDefended')),
    treatiesKept: int(n('TreatiesKept')),
    treatiesBroken: int(n('TreatiesBroken')),
    oathsBroken: int(n('OathsBroken')),
    contractsFulfilled: int(n('ContractsFulfilled')),
    eventPoints: Math.trunc(n('EventPoints')),
    honours: Array.isArray(honours) ? honours.map((x) => str(x, 120)).filter(Boolean).slice(0, 12) : [],
  };
}

// Seasons. The RealmSeasons plugin's own shape is
//   RealmSeasons.json { Number, Name, Active, StartedAt, EndsAt, Houses: { name: HouseStanding } }
//   RealmLegends.json { Seasons: [ { Number, Name, Start, End, Champion, ChampionScore, LongestReign, Top[] } ] }
// For other tools the reader also accepts (case-insensitively)
//   { Current: {...}, History: [...], Schedule: [ {Title, At, End, Kind} ] } or a bare array of seasons.
export function sanitizeSeasons(raw, now = Date.now(), legends = null) {
  const out = { current: null, past: [], upcoming: [], standings: [] };
  const season = (s) => {
    if (!s || typeof s !== 'object') return null;
    const number = pick(s, 'Number', 'Index', 'Id', 'Season');
    const name = str(pick(s, 'Name', 'Title'), 80);
    const startedAt = normalizeIso(pick(s, 'StartedAt', 'Started', 'Start', 'StartsAt', 'From'));
    const endsAt = normalizeIso(pick(s, 'EndsAt', 'EndedAt', 'Ended', 'End', 'Until', 'To'));
    if (!name && !Number.isFinite(number)) return null;
    const top = pick(s, 'Top');
    return {
      number: Number.isFinite(number) ? Math.trunc(number) : null,
      name: name || `Season ${Math.trunc(number)}`,
      theme: str(pick(s, 'Theme', 'Motto', 'Description'), 200),
      startedAt,
      endsAt,
      champion: str(pick(s, 'Winner', 'King', 'Victor'), 64),
      championHouse: str(pick(s, 'Champion', 'ChampionHouse', 'WinnerHouse', 'House'), 64),
      championScore: Number.isFinite(pick(s, 'ChampionScore')) ? Math.round(pick(s, 'ChampionScore')) : null,
      longestReign: str(pick(s, 'LongestReign'), 120),
      top: Array.isArray(top) ? top.slice(0, 5).map((t) => ({ rank: int(pick(t, 'Rank')), house: str(pick(t, 'House'), 64), score: Math.round(Number(pick(t, 'Score')) || 0) })).filter((t) => t.house) : [],
    };
  };
  let list = [];
  if (Array.isArray(raw)) list = raw.map(season).filter(Boolean);
  else if (raw && typeof raw === 'object') {
    const houses = pick(raw, 'Houses');
    const isPlugin = Number.isFinite(pick(raw, 'Number')) && (pick(raw, 'Active') !== undefined || (houses && typeof houses === 'object'));
    if (isPlugin) {
      const active = pick(raw, 'Active') !== false && pick(raw, 'Number') > 0;
      if (active) out.current = season(raw);
      if (houses && typeof houses === 'object' && !Array.isArray(houses)) {
        out.standings = Object.values(houses).slice(0, 128).map((h) => standingOf(h)).filter((h) => h.house)
          .sort((a, b) => b.score - a.score || b.crownDays - a.crownDays || a.house.localeCompare(b.house))
          .map((h, i) => ({ ...h, rank: i + 1 }));
      }
    } else {
      const cur = season(pick(raw, 'Current', 'CurrentSeason', 'Season', 'Active'));
      if (cur) out.current = cur;
      const hist = pick(raw, 'History', 'Past', 'Seasons', 'Previous');
      if (Array.isArray(hist)) list = hist.map(season).filter(Boolean);
    }
    const sched = pick(raw, 'Schedule', 'Events', 'Upcoming', 'Calendar');
    if (Array.isArray(sched)) {
      for (const e of sched.slice(0, 32)) {
        const title = str(pick(e, 'Title', 'Name', 'Label'), 120);
        const at = normalizeIso(pick(e, 'At', 'Start', 'StartsAt', 'When', 'Time'));
        if (!title || !at) continue;
        out.upcoming.push({ title, at, end: normalizeIso(pick(e, 'End', 'EndsAt', 'Until')), kind: str(pick(e, 'Kind', 'Type'), 32) || 'season' });
      }
    }
  }
  const legendSeasons = legends && Array.isArray(pick(legends, 'Seasons')) ? pick(legends, 'Seasons').map(season).filter(Boolean) : [];
  list = list.concat(legendSeasons);
  if (!out.current && list.length) {
    const open = list.filter((s) => !s.endsAt || Date.parse(s.endsAt) > now);
    out.current = open.length ? open[open.length - 1] : null;
  }
  const seen = new Set();
  out.past = list.filter((s) => s !== out.current && s.endsAt && Date.parse(s.endsAt) <= now)
    .filter((s) => {
      const k = `${s.number}|${s.name}`;
      if (seen.has(k)) return false;
      seen.add(k);
      return true;
    })
    .sort((a, b) => Date.parse(b.endsAt) - Date.parse(a.endsAt)).slice(0, 24);
  return out;
}

// RealmLegends.json -> the Hall of Kings kept across wipes (names only; the plugin stores no ids here).
export function sanitizeLegends(raw) {
  const out = { hall: [], current: null };
  if (!raw || typeof raw !== 'object') return out;
  const reign = (r) => {
    if (!r || typeof r !== 'object') return null;
    const monarch = publicName(pick(r, 'Monarch', 'King', 'Name'));
    const start = normalizeIso(pick(r, 'Start', 'Since'));
    if (!monarch || !start) return null;
    return { king: monarch, house: str(pick(r, 'House'), 64), start, end: normalizeIso(pick(r, 'End')), ending: str(pick(r, 'Ending'), 120), season: Number.isFinite(pick(r, 'Season')) ? Math.trunc(pick(r, 'Season')) : null };
  };
  const hall = pick(raw, 'Hall');
  if (Array.isArray(hall)) out.hall = hall.slice(-1000).map(reign).filter(Boolean);
  out.current = reign(pick(raw, 'Current'));
  if (out.current) out.current.end = null;
  return out;
}

// RealmEvents: what runs now (data file) and the weekly schedule (config file).
const EVENT_KINDS = {
  crown_night: { title: 'Crown Night', flag: 'EnableCrownNight', detail: 'Declared claims are fought for the Old Throne. The house holding the crown at the end earns season points.' },
  tournament: { title: 'Royal Tournament', flag: 'EnableTournament', detail: 'A timed PvP ranking. Join with /tourney join. Housemates never count.' },
  kings_hunt: { title: "The King's Hunt", flag: 'EnableKingsHunt', detail: 'The monarch names quarry. Hunters are paid; quarry who survive earn their house points.' },
  truce: { title: 'Truce of the Realm', flag: 'EnableTruce', detail: 'No blood may be spilled. A killer during the truce is chronicled and costs their house points.' },
};
export function eventKindInfo(kind) {
  return EVENT_KINDS[kind] || { title: String(kind || 'Realm event').replace(/_/g, ' '), detail: '' };
}

export function sanitizeEventsData(raw) {
  const out = { active: [] };
  const act = raw && typeof raw === 'object' ? pick(raw, 'Active') : null;
  if (Array.isArray(act)) {
    for (const a of act.slice(0, 8)) {
      const kind = str(pick(a, 'Kind'), 32);
      const start = normalizeIso(pick(a, 'Start'));
      const end = normalizeIso(pick(a, 'End'));
      if (kind && start) out.active.push({ kind, start, end });
    }
  }
  return out;
}

const DAY_NAMES = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];
function daySet(days) {
  const set = new Set();
  for (const d of Array.isArray(days) ? days : []) {
    const k = String(d || '').trim().toLowerCase();
    if (k === 'daily') DAY_NAMES.forEach((_, i) => set.add(i));
    else if (k === 'weekdays') [1, 2, 3, 4, 5].forEach((i) => set.add(i));
    else if (k === 'weekends') [0, 6].forEach((i) => set.add(i));
    else {
      const i = DAY_NAMES.findIndex((n) => n === k || n.slice(0, 3) === k);
      if (i >= 0) set.add(i);
    }
  }
  return set;
}

// The plugin's defaults when the config file is missing or has no Schedule (PluginConfig.Defaults()).
export const DEFAULT_EVENT_SCHEDULE = [
  { Id: 'crown-night', Event: 'crown_night', Days: ['Saturday'], StartUtc: '19:00', DurationMinutes: 90 },
  { Id: 'royal-tournament', Event: 'tournament', Days: ['Friday'], StartUtc: '19:00', DurationMinutes: 60 },
  { Id: 'kings-hunt', Event: 'kings_hunt', Days: ['Wednesday'], StartUtc: '21:00', DurationMinutes: 60 },
  { Id: 'truce', Event: 'truce', Days: ['Sunday'], StartUtc: '12:00', DurationMinutes: 240 },
];

// Next occurrences of every enabled schedule slot within `days` days (UTC).
export function scheduleOccurrences(config, now = Date.now(), days = 14) {
  if (!config || typeof config !== 'object') return [];
  const sched = Array.isArray(pick(config, 'Schedule')) ? pick(config, 'Schedule') : DEFAULT_EVENT_SCHEDULE;
  const out = [];
  for (const e of sched.slice(0, 32)) {
    if (!e || pick(e, 'Enabled') === false) continue;
    const kind = str(pick(e, 'Event'), 32);
    const info = EVENT_KINDS[kind];
    if (!info || pick(config, info.flag) === false) continue;
    const m = String(pick(e, 'StartUtc') || '').match(/^(\d{1,2}):(\d{2})$/);
    if (!m || Number(m[1]) > 23 || Number(m[2]) > 59) continue;
    const dur = Math.min(Math.max(int(pick(e, 'DurationMinutes')) || 60, 1), 24 * 60);
    const set = daySet(pick(e, 'Days'));
    const d0 = new Date(now);
    for (let i = -1; i <= days; i++) {
      const day = new Date(Date.UTC(d0.getUTCFullYear(), d0.getUTCMonth(), d0.getUTCDate() + i, Number(m[1]), Number(m[2])));
      if (!set.has(day.getUTCDay())) continue;
      const end = day.getTime() + dur * 60000;
      if (end <= now) continue;
      out.push({ kind, title: info.title, detail: info.detail, at: day.toISOString().replace('.000Z', 'Z'), end: new Date(end).toISOString().replace('.000Z', 'Z') });
    }
  }
  return out.sort((a, b) => Date.parse(a.at) - Date.parse(b.at));
}

// Labels for event types that other plugins register in plugins/docs/<Plugin>/EVENTS.json.
export function sanitizeTypeDocs(list) {
  const out = {};
  if (!Array.isArray(list)) return out;
  for (const t of list) {
    if (!t || typeof t.type !== 'string' || !TYPE_RE.test(t.type)) continue;
    out[t.type] = { label: str(t.label, 40) || t.type, icon: typeof t.icon === 'string' && /^[a-zA-Z]{1,16}$/.test(t.icon) ? t.icon : 'scroll' };
  }
  return out;
}

export async function readRealmData({ dataDirs, configDirs = [], typeDocs = [], now = Date.now() }) {
  const warnings = [];
  const dirs = (Array.isArray(dataDirs) ? dataDirs : [dataDirs]).filter(Boolean);
  const rawEvents = await readFirst(dirs, 'RealmChronicle.json', warnings);
  const rawState = await readFirst(dirs, 'RealmState.json', warnings);
  const rawHouses = await readFirst(dirs, 'RealmHouses.json', warnings);
  const rawCrown = await readFirst(dirs, 'CrownAndConsequences.json', warnings);
  const rawSeasons = await readFirst(dirs, 'RealmSeasons.json', warnings);
  const rawLegends = await readFirst(dirs, 'RealmLegends.json', warnings);
  const rawEventsData = await readFirst(dirs, 'RealmEvents.json', warnings);
  const cfgDirs = (Array.isArray(configDirs) ? configDirs : [configDirs]).filter(Boolean);
  const rawEventsConfig = cfgDirs.length ? await readFirst(cfgDirs, 'RealmEvents.json', warnings) : null;

  if (rawEvents === null) warnings.push('RealmChronicle.json not found: the Chronicle will be empty.');
  if (rawState === null) warnings.push('RealmState.json not found: no king or online count.');

  const seen = new Set();
  const events = (Array.isArray(rawEvents) ? rawEvents : [])
    .map(sanitizeEvent)
    .filter((e) => e && !seen.has(e.id) && seen.add(e.id))
    .sort((a, b) => a.id - b.id);

  const typeMeta = {};
  for (const file of typeDocs) {
    const r = await readJson(file);
    Object.assign(typeMeta, sanitizeTypeDocs(r.value));
  }

  return {
    events,
    state: sanitizeState(rawState),
    housesData: sanitizeHousesData(rawHouses),
    crown: sanitizeCrownData(rawCrown),
    seasons: sanitizeSeasons(rawSeasons, now, rawLegends),
    legends: sanitizeLegends(rawLegends),
    // No config file found but RealmEvents is running (its data file exists): assume the plugin's default schedule.
    realmEvents: { ...sanitizeEventsData(rawEventsData), schedule: rawEventsConfig || rawEventsData ? scheduleOccurrences(rawEventsConfig || {}, now) : [], scheduleFromConfig: !!rawEventsConfig },
    typeMeta,
    warnings,
  };
}
