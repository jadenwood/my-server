// Read-only access to the Oxide files the plugins write, for details the Chronicle API does not carry:
//   <data>/RealmHouses.json    house founding date, leaders, oath and treaty record, active treaties
//   <data>/RealmEvents.json    running realm events (Crown Night, tournament, King's Hunt, truce)
//   <config>/RealmEvents.json  the weekly event schedule (UTC)
// Every file is optional. A file being rewritten by the plugin keeps the last good copy (same rule as
// the Chronicle service). Nothing here ever writes to those folders.

import { readFile, stat } from 'node:fs/promises';
import { join } from 'node:path';

export const EVENT_KINDS = {
  crown_night: { name: 'Crown Night', emoji: '👑', flag: 'EnableCrownNight' },
  tournament: { name: 'The Royal Tournament', emoji: '🏆', flag: 'EnableTournament' },
  kings_hunt: { name: "The King's Hunt", emoji: '🏹', flag: 'EnableKingsHunt' },
  truce: { name: 'The Truce of the Realm', emoji: '🕊️', flag: 'EnableTruce' },
};

// Oxide writes UTC DateTimes, sometimes without a zone suffix: read those as UTC.
export function parseUtc(v) {
  if (typeof v !== 'string' || !v) return null;
  const withZone = /[zZ]|[+-]\d\d:?\d\d$/.test(v) ? v : `${v}Z`;
  const t = Date.parse(withZone);
  // .NET writes DateTime.MinValue for "never"; treat anything before 2000 as unset.
  return Number.isNaN(t) || t < Date.UTC(2000, 0, 1) ? null : t;
}

function jsonFile(path) {
  let key = null;
  let value = null;
  let error = null;
  return async function load() {
    let st;
    try {
      st = await stat(path);
    } catch (e) {
      error = e.code === 'ENOENT' ? 'missing' : e.message;
      return { value: null, error };
    }
    const k = `${st.mtimeMs}:${st.size}`;
    if (k === key) return { value, error };
    try {
      value = JSON.parse((await readFile(path, 'utf8')).replace(/^﻿/, ''));
      key = k;
      error = null;
    } catch (e) {
      error = `unreadable: ${e.message}`; // keep the previous good copy
    }
    return { value, error };
  };
}

const s = (v, max = 64) => (typeof v === 'string' && v ? v.slice(0, max) : null);
const n = (v) => (Number.isInteger(v) ? v : 0);

export function cleanHouses(raw) {
  const data = raw && typeof raw === 'object' ? raw : {};
  const houses = (Array.isArray(data.Houses) ? data.Houses : [])
    .filter((h) => h && typeof h.Name === 'string' && h.Name)
    .slice(0, 200)
    .map((h) => {
      const members = (Array.isArray(h.Members) ? h.Members : []).filter((m) => m && typeof m.Name === 'string');
      const byRank = (r) => members.filter((m) => m.Rank === r).map((m) => m.Name.slice(0, 64));
      return {
        name: h.Name.slice(0, 64),
        sigil: s(h.Sigil),
        founded: parseUtc(h.Founded),
        liege: s(h.Liege),
        swornSince: parseUtc(h.SwornSince),
        oathsBroken: n(h.OathsBroken),
        treatiesBroken: n(h.TreatiesBroken),
        members: members.length,
        leaders: byRank('leader'),
        officers: byRank('officer'),
      };
    });
  const treaties = (Array.isArray(data.Treaties) ? data.Treaties : [])
    .filter((t) => t && typeof t.A === 'string' && typeof t.B === 'string')
    .map((t) => ({ a: t.A.slice(0, 64), b: t.B.slice(0, 64), signed: parseUtc(t.Signed), expires: parseUtc(t.Expires) }));
  return { houses, treaties };
}

export function cleanActiveEvents(raw, now) {
  const data = raw && typeof raw === 'object' ? raw : {};
  return (Array.isArray(data.Active) ? data.Active : [])
    .filter((e) => e && EVENT_KINDS[e.Kind])
    .map((e) => {
      const entrants = e.Entrants && typeof e.Entrants === 'object' ? Object.values(e.Entrants) : [];
      return {
        kind: e.Kind,
        start: parseUtc(e.Start),
        end: parseUtc(e.End),
        crownHouseAtStart: s(e.CrownHouseAtStart),
        captureHouses: (Array.isArray(e.CaptureHouses) ? e.CaptureHouses : []).filter((x) => typeof x === 'string').map((x) => x.slice(0, 64)),
        leaders: entrants
          .filter((x) => x && typeof x.Name === 'string')
          .map((x) => ({ name: x.Name.slice(0, 64), house: s(x.House), kills: n(x.Kills) }))
          .sort((a, b) => b.kills - a.kills)
          .slice(0, 3),
        quarry: (Array.isArray(e.Quarry) ? e.Quarry : [])
          .filter((q) => q && typeof q.Name === 'string')
          .map((q) => ({ name: q.Name.slice(0, 64), house: s(q.House), claimedBy: s(q.ClaimedByName) })),
      };
    })
    .filter((e) => e.end && e.end > now);
}

const WEEKDAYS = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];

// Same rules as RealmEvents.DayMatches: weekday names, "Daily", "Weekdays", "Weekends".
export function dayMatches(days, dow) {
  for (const raw of Array.isArray(days) ? days : []) {
    const d = String(raw ?? '').trim().toLowerCase();
    if (d === 'daily') return true;
    if (d === 'weekdays' && dow !== 0 && dow !== 6) return true;
    if (d === 'weekends' && (dow === 0 || dow === 6)) return true;
    if (d === WEEKDAYS[dow]) return true;
  }
  return false;
}

// Next starts of each enabled schedule slot within `days` days, soonest first.
export function upcomingFromSchedule(cfg, now, days = 8) {
  const c = cfg && typeof cfg === 'object' ? cfg : {};
  const out = [];
  for (const e of Array.isArray(c.Schedule) ? c.Schedule : []) {
    if (!e || !EVENT_KINDS[e.Event] || e.Enabled === false) continue;
    if (c[EVENT_KINDS[e.Event].flag] === false) continue;
    const m = /^(\d{1,2}):(\d{2})$/.exec(String(e.StartUtc ?? '').trim());
    if (!m || +m[1] > 23 || +m[2] > 59) continue;
    const dur = Math.min(1440, Math.max(1, Number.isInteger(e.DurationMinutes) ? e.DurationMinutes : 60));
    const today = new Date(now);
    const day0 = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate());
    for (let i = 0; i <= days; i++) {
      const day = day0 + i * 86400000;
      if (!dayMatches(e.Days, new Date(day).getUTCDay())) continue;
      const start = day + (+m[1]) * 3600000 + (+m[2]) * 60000;
      if (start <= now || start > now + days * 86400000) continue;
      out.push({ kind: e.Event, slot: s(e.Id, 40), start, end: start + dur * 60000 });
      break; // only the next one per slot
    }
  }
  return out.sort((a, b) => a.start - b.start);
}

export function createRealmData({ dataDir, configDir, now = Date.now }) {
  const housesFile = jsonFile(join(dataDir, 'RealmHouses.json'));
  const eventsData = jsonFile(join(dataDir, 'RealmEvents.json'));
  const eventsConfig = jsonFile(join(configDir, 'RealmEvents.json'));
  return {
    async houses() {
      const { value, error } = await housesFile();
      return { ...cleanHouses(value), error };
    },
    async realmEvents() {
      const [d, c] = await Promise.all([eventsData(), eventsConfig()]);
      const t = now();
      return {
        active: cleanActiveEvents(d.value, t),
        upcoming: upcomingFromSchedule(c.value, t),
        error: d.error && d.error !== 'missing' ? d.error : c.error && c.error !== 'missing' ? c.error : null,
        known: !!(d.value || c.value),
      };
    },
  };
}
