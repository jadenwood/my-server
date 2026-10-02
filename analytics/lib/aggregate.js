'use strict';
// Pure aggregation over the merged day records from load.js. No I/O. Every number on the dashboard comes from here.
//
// Definitions (also printed on the dashboard):
//   Active player   a key seen online at least once that UTC day.
//   New player      a key seen for the first time that UTC day (per the plugin's first-seen table).
//   D1 / D7         classic day-N retention: of the players new on day d, the share active on day d+1 / d+7.
//                   A cohort is only scored when day d+N has a data file, both days share one salt id and neither day
//                   is Degraded; otherwise it shows "-" and is left out of the averages.
//   Concurrency     players online at each sample (every SampleMinutes); "peak" is the in-interval maximum.

const DAY_MS = 86400000;
const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const CAUSE_LABELS = {
  pvp: 'Killed by a player', suicide: 'Suicide', fall: 'Falling', hunger: 'Hunger', thirst: 'Thirst',
  drowning: 'Drowning', fire: 'Fire', explosion: 'Explosion', siege: 'Siege engine', plague: 'Plague',
  out_of_bounds: 'Out of bounds', creature: 'Creature or world', weapon: 'Weapon (unknown source)', other: 'Other',
};
const SESSION_BUCKETS = [
  { label: '< 5 min', max: 300 }, { label: '5-15 min', max: 900 }, { label: '15-30 min', max: 1800 },
  { label: '30-60 min', max: 3600 }, { label: '1-2 h', max: 7200 }, { label: '2-4 h', max: 14400 },
  { label: '4 h +', max: Infinity },
];

const addDays = (date, n) => new Date(Date.parse(date + 'T00:00:00Z') + n * DAY_MS).toISOString().slice(0, 10);
const round = (v, dp = 1) => (v == null || !Number.isFinite(v) ? null : Math.round(v * 10 ** dp) / 10 ** dp);

function median(values) {
  if (!values.length) return null;
  const s = [...values].sort((a, b) => a - b);
  const m = s.length >> 1;
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

// Parses "+02:00", "-5", "-330" (minutes when |v| > 14) or "UTC" into minutes east of UTC.
function parseTzOffset(text) {
  if (text == null || text === '' || /^utc$/i.test(String(text))) return 0;
  const s = String(text).trim();
  let m = /^([+-])?(\d{1,2}):(\d{2})$/.exec(s);
  if (m) {
    const v = Number(m[2]) * 60 + Number(m[3]);
    if (v > 14 * 60) throw new Error('time zone offset out of range: ' + text);
    return m[1] === '-' ? -v : v;
  }
  m = /^([+-]?\d{1,4})$/.exec(s);
  if (m) {
    const n = Number(m[1]);
    const v = Math.abs(n) <= 14 ? n * 60 : n;
    if (Math.abs(v) > 14 * 60) throw new Error('time zone offset out of range: ' + text);
    return v;
  }
  throw new Error('cannot read time zone offset "' + text + '" (use e.g. +02:00, -5 or -300)');
}

// Picks the dates in the window: the `days` dates ending at `until` (default: the newest date with data).
function windowDates(allDates, days, until) {
  if (!allDates.length) return [];
  const end = until || allDates[allDates.length - 1];
  const start = addDays(end, -(days - 1));
  const out = [];
  for (let d = start; d <= end; d = addDays(d, 1)) out.push(d);
  return out;
}

function retention(daysMap, dates) {
  const cohorts = [];
  let w1 = 0, n1 = 0, w7 = 0, n7 = 0;
  for (const date of dates) {
    const d = daysMap.get(date);
    if (!d || d.degraded || d.newKeys.size === 0) continue;
    const row = { date, size: d.newKeys.size, d1: null, d7: null, d1Count: null, d7Count: null, note: '' };
    for (const [n, field] of [[1, 'd1'], [7, 'd7']]) {
      const later = daysMap.get(addDays(date, n));
      if (!later) continue;                           // not observable yet, or the server kept no file that day
      if (later.degraded) continue;
      if (d.salts.size !== 1 || later.salts.size !== 1 || [...d.salts][0] !== [...later.salts][0]) { row.note = 'salt changed'; continue; }
      let back = 0;
      for (const k of d.newKeys) if (later.active.has(k)) back++;
      row[field + 'Count'] = back;
      row[field] = back / d.newKeys.size;
      if (n === 1) { w1 += back; n1 += d.newKeys.size; } else { w7 += back; n7 += d.newKeys.size; }
    }
    cohorts.push(row);
  }
  return { cohorts, d1: n1 ? w1 / n1 : null, d7: n7 ? w7 / n7 : null, d1Players: n1, d7Players: n7 };
}

// 7 x 24 grid (Mon..Sun x local hour) of the average players online, from the concurrency samples.
function heatmap(samples, tzMinutes) {
  const sum = Array.from({ length: 7 }, () => new Array(24).fill(0));
  const cnt = Array.from({ length: 7 }, () => new Array(24).fill(0));
  for (const s of samples) {
    const local = new Date(s.t * 1000 + tzMinutes * 60000);
    const wd = (local.getUTCDay() + 6) % 7;          // Monday = 0
    const h = local.getUTCHours();
    sum[wd][h] += s.n;
    cnt[wd][h]++;
  }
  const grid = sum.map((row, wd) => row.map((v, h) => (cnt[wd][h] ? v / cnt[wd][h] : null)));
  let peak = null;
  grid.forEach((row, wd) => row.forEach((v, h) => { if (v != null && (!peak || v > peak.value)) peak = { weekday: WEEKDAYS[wd], hour: h, value: v }; }));
  const byHour = new Array(24).fill(0).map((_, h) => {
    let s = 0, c = 0;
    for (let wd = 0; wd < 7; wd++) { s += sum[wd][h]; c += cnt[wd][h]; }
    return c ? s / c : null;
  });
  return { grid, peak, byHour, weekdays: WEEKDAYS };
}

// Hourly series (avg of samples and max of in-interval peaks), at most `maxPoints` points (coarser buckets if needed).
function concurrencySeries(samples, maxPoints = 1000) {
  if (!samples.length) return { bucketMinutes: 60, points: [] };
  const first = samples[0].t, last = samples[samples.length - 1].t;
  let bucket = 3600;
  while ((last - first) / bucket > maxPoints) bucket *= 2;
  const map = new Map();
  for (const s of samples) {
    const b = s.t - (s.t % bucket);
    let p = map.get(b);
    if (!p) { p = { t: b, sum: 0, count: 0, max: 0 }; map.set(b, p); }
    p.sum += s.n; p.count++; p.max = Math.max(p.max, s.max, s.n);
  }
  const points = [...map.values()].sort((a, b) => a.t - b.t).map((p) => ({ t: p.t, avg: p.sum / p.count, max: p.max }));
  return { bucketMinutes: bucket / 60, points };
}

function houseTable(daysMap, dates, minPlayers) {
  const acc = new Map();
  for (const date of dates) {
    const d = daysMap.get(date);
    if (!d) continue;
    for (const [name, h] of d.houses) {
      let r = acc.get(name);
      if (!r) { r = { name, keys: new Set(), seconds: 0, sessions: 0, deaths: 0, kills: 0, activeDays: 0 }; acc.set(name, r); }
      for (const k of h.active) r.keys.add(k);
      r.seconds += h.seconds; r.sessions += h.sessions; r.deaths += h.deaths; r.kills += h.kills;
      if (h.active.size > 0 || h.seconds > 0) r.activeDays++;
    }
  }
  const rows = [], small = { name: 'Smaller houses', houses: 0, players: 0, seconds: 0, sessions: 0, deaths: 0, kills: 0 };
  for (const r of acc.values()) {
    const row = { name: r.name, players: r.keys.size, hours: r.seconds / 3600, sessions: r.sessions, deaths: r.deaths, kills: r.kills, activeDays: r.activeDays, folded: r.name === '(other)' };
    if (r.keys.size < minPlayers) {
      small.houses++; small.players += r.keys.size; small.seconds += r.seconds; small.sessions += r.sessions; small.deaths += r.deaths; small.kills += r.kills;
    } else rows.push(row);
  }
  rows.sort((a, b) => b.hours - a.hours || b.players - a.players || a.name.localeCompare(b.name));
  const smallRow = small.houses ? { name: `Smaller houses (${small.houses}, under ${minPlayers} players each)`, players: small.players, hours: small.seconds / 3600, sessions: small.sessions, deaths: small.deaths, kills: small.kills, activeDays: null, suppressed: true } : null;
  return { rows, small: smallRow, minPlayers };
}

function aggregate(loaded, opts = {}) {
  const days = Math.max(1, Math.min(730, opts.days || 30));
  const tz = opts.tzMinutes || 0;
  const minHouse = Math.max(1, opts.minHousePlayers == null ? 3 : opts.minHousePlayers);
  const daysMap = loaded.days;
  const allDates = [...daysMap.keys()].sort();
  const dates = windowDates(allDates, days, opts.until);
  const present = dates.filter((d) => daysMap.has(d));

  const unique = new Set(), newKeys = new Set();
  const daily = [];
  let samples = [];
  const sessionsAll = [];
  const deaths = Object.create(null);
  const joinsByHour = new Array(24).fill(0), leavesByHour = new Array(24).fill(0);
  const dropped = Object.create(null);
  const salts = new Set();
  let degradedDays = 0, recovered = 0;

  for (const date of dates) {
    const d = daysMap.get(date);
    if (!d) { daily.push({ date, missing: true }); continue; }
    for (const k of d.active) unique.add(k);
    for (const k of d.newKeys) newKeys.add(k);
    for (const s of d.salts) salts.add(s);
    if (d.degraded) degradedDays++;
    const conc = [...d.concurrency.values()];
    samples = samples.concat(conc);
    for (const s of d.sessions) { sessionsAll.push(s); if (s.end === 'recovered') recovered++; }
    for (const [c, v] of Object.entries(d.deaths)) deaths[c] = (deaths[c] || 0) + v;
    for (const [c, v] of Object.entries(d.dropped)) dropped[c] = (dropped[c] || 0) + v;
    // Joins/leaves are stored per UTC hour; shift whole hours for the local view (sub-hour offsets round).
    const shift = Math.round(tz / 60);
    d.joins.forEach((v, h) => { joinsByHour[(((h + shift) % 24) + 24) % 24] += v; });
    d.leaves.forEach((v, h) => { leavesByHour[(((h + shift) % 24) + 24) % 24] += v; });
    const dayDeaths = Object.values(d.deaths).reduce((a, b) => a + b, 0);
    const peak = conc.reduce((m, s) => Math.max(m, s.max, s.n), 0);
    const playSeconds = d.sessions.reduce((a, s) => a + s.seconds, 0);
    daily.push({
      date, missing: false, degraded: d.degraded,
      active: d.active.size, newPlayers: d.degraded ? null : d.newKeys.size,
      returning: d.degraded ? null : Math.max(0, d.active.size - d.newKeys.size),
      sessions: d.sessions.length, hours: playSeconds / 3600, peak,
      joins: d.joins.reduce((a, b) => a + b, 0), deaths: dayDeaths,
      medianSessionMin: d.sessions.length ? median(d.sessions.map((s) => s.seconds)) / 60 : null,
    });
  }
  samples.sort((a, b) => a.t - b.t);

  const peakSample = samples.reduce((m, s) => (!m || Math.max(s.max, s.n) > m.value ? { value: Math.max(s.max, s.n), t: s.t } : m), null);
  const sessionSecs = sessionsAll.map((s) => s.seconds);
  const sessionHist = SESSION_BUCKETS.map((b) => ({ label: b.label, count: 0 }));
  for (const s of sessionSecs) sessionHist[SESSION_BUCKETS.findIndex((b) => s < b.max)].count++;
  const presentDaily = daily.filter((d) => !d.missing);
  const totalDeaths = Object.values(deaths).reduce((a, b) => a + b, 0);
  const deathRows = Object.entries(deaths).filter(([, v]) => v > 0)
    .map(([cause, count]) => ({ cause, label: (Object.prototype.hasOwnProperty.call(CAUSE_LABELS, cause) && CAUSE_LABELS[cause]) || cause, count, share: totalDeaths ? count / totalDeaths : 0 }))
    .sort((a, b) => b.count - a.count || a.cause.localeCompare(b.cause));

  const warnings = [...(loaded.warnings || [])];
  if (salts.size > 1) warnings.push(`The salt changed inside this window (${salts.size} salt ids); retention across the change is not scored and unique-player counts may double count.`);
  const missing = daily.filter((d) => d.missing).length;
  if (missing && present.length) warnings.push(`${missing} of ${dates.length} days in the window have no data file (server off, or files pruned).`);
  if (degradedDays) warnings.push(`${degradedDays} day(s) were written while state.json was unreadable (Degraded): their new players are unknown and they are left out of retention.`);
  for (const [k, v] of Object.entries(dropped)) {
    if (k !== 'short_sessions' && v > 0) warnings.push(`The plugin dropped ${v} "${k}" record(s) because a cap was reached; raise the cap in oxide/config/RealmStats.json if this keeps happening.`);
  }

  return {
    window: { start: dates[0] || null, end: dates[dates.length - 1] || null, days: dates.length, daysWithData: present.length, tzMinutes: tz },
    allDates: { first: allDates[0] || null, last: allDates[allDates.length - 1] || null, count: allDates.length },
    totals: {
      uniquePlayers: unique.size,
      newPlayers: newKeys.size,
      avgDailyActive: presentDaily.length ? presentDaily.reduce((a, d) => a + d.active, 0) / presentDaily.length : null,
      peakConcurrent: peakSample ? peakSample.value : null,
      peakAt: peakSample ? peakSample.t : null,
      sessions: sessionsAll.length,
      playHours: sessionSecs.reduce((a, b) => a + b, 0) / 3600,
      medianSessionMin: sessionSecs.length ? median(sessionSecs) / 60 : null,
      deaths: totalDeaths,
      recoveredSessions: recovered,
    },
    retention: retention(daysMap, dates),
    daily,
    heatmap: heatmap(samples, tz),
    concurrency: concurrencySeries(samples, opts.maxPoints || 400),
    sessionHist,
    joinsByHour, leavesByHour,
    deaths: deathRows,
    houses: houseTable(daysMap, dates, minHouse),
    dropped,
    warnings,
  };
}

module.exports = { aggregate, parseTzOffset, median, addDays, round, windowDates, retention, heatmap, concurrencySeries, houseTable, CAUSE_LABELS, WEEKDAYS };
