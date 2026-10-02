'use strict';
// Reads RealmStats day files (oxide/data/RealmStats/day-YYYY-MM-DD[-rN].json) and merges them into one record per
// UTC date. Read-only: this module never writes or deletes anything in the data folder.

const fs = require('node:fs');
const path = require('node:path');

const DAY_FILE = /^day-(\d{4}-\d{2}-\d{2})(?:-r(\d{1,3}))?\.json$/;
const MAX_FILE_BYTES = 64 * 1024 * 1024;       // a day file this large is refused rather than parsed
const MAX_FILES = 2000;

function emptyDay(date) {
  return {
    date,
    servers: new Set(),
    salts: new Set(),
    degraded: false,
    sampleMinutes: 5,
    active: new Set(),
    newKeys: new Set(),
    sessions: [],
    joins: new Array(24).fill(0),
    leaves: new Array(24).fill(0),
    concurrency: new Map(),                    // T -> {t, n, max}
    deaths: Object.create(null),               // null prototype: a cause named "constructor" or "__proto__" is just a key
    houses: new Map(),                         // name -> {active:Set, seconds, sessions, deaths, kills}
    dropped: Object.create(null),
    files: [],
  };
}

const isInt = (v) => Number.isInteger(v);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : 0);
const keyOk = (k) => typeof k === 'string' && /^[0-9a-f]{8,64}$/.test(k);

// Checks the shape of one parsed day file. Returns a list of problems ([] = fine). Unknown extra fields are allowed.
function validateDay(obj, date) {
  const problems = [];
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return ['not a JSON object'];
  if (obj.Version !== undefined && obj.Version !== 1) problems.push('unsupported Version ' + obj.Version);
  if (obj.Date && obj.Date !== date) problems.push('Date ' + obj.Date + ' does not match file name ' + date);
  for (const f of ['Active', 'New', 'Sessions', 'Concurrency']) {
    if (obj[f] !== undefined && !Array.isArray(obj[f])) problems.push(f + ' is not an array');
  }
  for (const f of ['Joins', 'Leaves']) {
    if (obj[f] !== undefined && (!Array.isArray(obj[f]) || obj[f].length !== 24)) problems.push(f + ' is not a 24-hour array');
  }
  for (const f of ['Deaths', 'Houses', 'Dropped']) {
    if (obj[f] !== undefined && (obj[f] === null || typeof obj[f] !== 'object' || Array.isArray(obj[f]))) problems.push(f + ' is not an object');
  }
  return problems;
}

function mergeInto(d, obj, fileName) {
  d.files.push(fileName);
  if (typeof obj.Server === 'string' && obj.Server) d.servers.add(obj.Server);
  if (typeof obj.SaltId === 'string' && obj.SaltId) d.salts.add(obj.SaltId);
  if (obj.Degraded === true) d.degraded = true;
  if (isInt(obj.SampleMinutes) && obj.SampleMinutes > 0) d.sampleMinutes = obj.SampleMinutes;
  for (const k of obj.Active || []) if (keyOk(k)) d.active.add(k);
  for (const k of obj.New || []) if (keyOk(k)) d.newKeys.add(k);
  for (const s of obj.Sessions || []) {
    if (s && isInt(s.S) && isInt(s.D) && s.D >= 0) d.sessions.push({ start: s.S, seconds: s.D, end: typeof s.E === 'string' ? s.E : '' });
  }
  (obj.Joins || []).forEach((v, i) => { d.joins[i] += num(v); });
  (obj.Leaves || []).forEach((v, i) => { d.leaves[i] += num(v); });
  for (const c of obj.Concurrency || []) {
    if (!c || !isInt(c.T)) continue;
    const prev = d.concurrency.get(c.T);
    const n = num(c.N), max = Math.max(num(c.Max), n);
    if (!prev) d.concurrency.set(c.T, { t: c.T, n, max });
    else { prev.n = Math.max(prev.n, n); prev.max = Math.max(prev.max, max); }
  }
  for (const [cause, v] of Object.entries(obj.Deaths || {})) d.deaths[cause] = (d.deaths[cause] || 0) + num(v);
  for (const [name, h] of Object.entries(obj.Houses || {})) {
    if (!h || typeof h !== 'object') continue;
    let rec = d.houses.get(name);
    if (!rec) { rec = { active: new Set(), seconds: 0, sessions: 0, deaths: 0, kills: 0 }; d.houses.set(name, rec); }
    for (const k of h.Active || []) if (keyOk(k)) rec.active.add(k);
    rec.seconds += num(h.Seconds);
    rec.sessions += num(h.Sessions);
    rec.deaths += num(h.Deaths);
    rec.kills += num(h.Kills);
  }
  for (const [k, v] of Object.entries(obj.Dropped || {})) d.dropped[k] = (d.dropped[k] || 0) + num(v);
}

// Loads every day file in `dir`. Returns {days: Map(date -> day), warnings: [string], filesRead, filesSkipped}.
// A file that cannot be read or parsed, or has the wrong shape, is skipped with a warning (never "repaired").
function loadDir(dir, opts = {}) {
  const warnings = [];
  let entries;
  try {
    entries = fs.readdirSync(dir);
  } catch (e) {
    throw new Error('Cannot read data folder ' + dir + ': ' + e.message);
  }
  const files = entries.filter((f) => DAY_FILE.test(f)).sort();
  if (files.length > MAX_FILES) {
    warnings.push(`${files.length} day files found; only the newest ${MAX_FILES} are read.`);
    files.splice(0, files.length - MAX_FILES);
  }
  const days = new Map();
  let filesRead = 0, filesSkipped = 0;
  for (const f of files) {
    const date = DAY_FILE.exec(f)[1];
    const full = path.join(dir, f);
    let obj;
    try {
      const st = fs.statSync(full);
      if (st.size > MAX_FILE_BYTES) throw new Error('file is larger than ' + MAX_FILE_BYTES + ' bytes');
      obj = JSON.parse(fs.readFileSync(full, 'utf8').replace(/^﻿/, ''));
    } catch (e) {
      warnings.push(`Skipped ${f}: ${e.message}`);
      filesSkipped++;
      continue;
    }
    const problems = validateDay(obj, date);
    if (problems.length) {
      warnings.push(`Skipped ${f}: ${problems.join('; ')}`);
      filesSkipped++;
      continue;
    }
    if (opts.server && obj.Server && obj.Server !== opts.server) continue;
    let d = days.get(date);
    if (!d) { d = emptyDay(date); days.set(date, d); }
    mergeInto(d, obj, f);
    filesRead++;
  }
  const servers = new Set();
  for (const d of days.values()) for (const s of d.servers) servers.add(s);
  if (servers.size > 1 && !opts.server) {
    warnings.push(`Day files from several servers are mixed (${[...servers].join(', ')}); use --server to pick one.`);
  }
  return { days, warnings, filesRead, filesSkipped };
}

module.exports = { loadDir, validateDay, DAY_FILE };
