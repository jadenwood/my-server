#!/usr/bin/env node
'use strict';
// Writes a deterministic, made-up RealmStats data set (day-YYYY-MM-DD.json files in the plugin's format) for demos,
// the dashboard screenshot and tests. It is SAMPLE DATA: no real player is behind any key.
//
// Usage: node analytics/tools/make-sample.js --out <folder> [--days 30] [--end 2026-09-30] [--seed 42]

const fs = require('node:fs');
const path = require('node:path');

function mulberry32(seed) {
  let a = seed >>> 0;
  return function () {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const HOUSES = [
  ['Varrow', 0.17], ['Ashgrove', 0.14], ['Corvane', 0.13], ['Dunmere', 0.1], ['Halloran', 0.1], ['Merrin', 0.08],
  ['Tallowmere', 0.012], ['Greywater', 0.012], [null, 0.236],
];
const CAUSES = [['pvp', 0.34], ['fall', 0.12], ['hunger', 0.1], ['creature', 0.14], ['fire', 0.05], ['siege', 0.06], ['weapon', 0.05], ['drowning', 0.03], ['thirst', 0.04], ['suicide', 0.04], ['other', 0.03]];

function pick(r, table) {
  let x = r();
  for (const [v, p] of table) { if ((x -= p) <= 0) return v; }
  return table[table.length - 1][0];
}

function generate({ days = 30, end = '2026-09-30', seed = 42, server = 'realm-1', saltId = '5a17c0de' } = {}) {
  const r = mulberry32(seed);
  const key = () => Array.from({ length: 16 }, () => Math.floor(r() * 16).toString(16)).join('');
  const endMs = Date.parse(end + 'T00:00:00Z');
  const startMs = endMs - (days - 1) * 86400000;
  const players = [];
  const files = {};
  const dayOf = (ms) => new Date(ms).toISOString().slice(0, 10);
  const blank = (date) => ({
    Version: 1, Date: date, Server: server, SaltId: saltId, Degraded: false, SampleMinutes: 5,
    Active: [], New: [], Sessions: [], Joins: new Array(24).fill(0), Leaves: new Array(24).fill(0),
    Concurrency: [], Deaths: {}, Houses: {}, Dropped: {},
  });
  for (let i = 0; i < days; i++) files[dayOf(startMs + i * 86400000)] = blank(dayOf(startMs + i * 86400000));
  const online = new Int32Array(days * 288 + 288);   // players online per 5-min slot
  const house = (d, name) => d.Houses[name] || (d.Houses[name] = { Active: [], Seconds: 0, Sessions: 0, Deaths: 0, Kills: 0 });

  for (let i = 0; i < days; i++) {
    const dayMs = startMs + i * 86400000;
    const date = dayOf(dayMs);
    const d = files[date];
    const wd = new Date(dayMs).getUTCDay();
    const weekend = wd === 0 || wd === 6;
    // A launch bump in the first days, then a steady trickle (more at weekends).
    const arrivals = Math.round((i < 3 ? 26 - i * 6 : 7) * (weekend ? 1.6 : 1) * (0.75 + r() * 0.5));
    for (let n = 0; n < arrivals; n++) {
      players.push({ key: key(), first: i, stick: Math.pow(r(), 1.6) * 0.95, house: pick(r, HOUSES), evening: 17 + Math.floor(r() * 6) });
    }
    for (const p of players) {
      if (p.first > i) continue;
      const age = i - p.first;
      const isNew = age === 0;
      const chance = isNew ? 1 : p.stick * Math.pow(0.93, age) * (weekend ? 1.25 : 1);
      if (!isNew && r() > chance) continue;
      d.Active.push(p.key);
      if (isNew) d.New.push(p.key);
      const nSess = 1 + (r() < 0.35 ? 1 : 0) + (r() < 0.1 ? 1 : 0);
      for (let s = 0; s < nSess; s++) {
        const hour = r() < 0.7 ? (p.evening + Math.floor(r() * 4)) % 24 : Math.floor(r() * 24);
        const start = Math.floor(dayMs / 1000) + hour * 3600 + Math.floor(r() * 3600);
        const len = Math.min(6 * 3600, Math.max(60, Math.round(Math.exp(Math.log(2400) + (r() + r() + r() - 1.5) * 1.3))));
        d.Sessions.push({ S: start, D: len, E: r() < 0.02 ? 'recovered' : 'leave' });
        const jd = files[dayOf(start * 1000)] || d;
        jd.Joins[new Date(start * 1000).getUTCHours()]++;
        const leaveDay = files[dayOf((start + len) * 1000)];
        if (leaveDay) leaveDay.Leaves[new Date((start + len) * 1000).getUTCHours()]++;
        for (let t = start; t < start + len; t += 300) {
          const slot = Math.floor((t - startMs / 1000) / 300);
          if (slot >= 0 && slot < online.length) online[slot]++;
        }
        if (p.house) {
          const h = house(d, p.house);
          h.Seconds += len; h.Sessions++;
          if (!h.Active.includes(p.key)) h.Active.push(p.key);
        }
        const deaths = Math.floor((len / 3600) * 0.9 * r() * 2);
        for (let k = 0; k < deaths; k++) {
          const cause = pick(r, CAUSES);
          d.Deaths[cause] = (d.Deaths[cause] || 0) + 1;
          if (p.house) house(d, p.house).Deaths++;
          if (cause === 'pvp') {
            const killer = pick(r, HOUSES);
            if (killer) house(d, killer).Kills++;
          }
        }
      }
    }
  }
  for (let slot = 0; slot < days * 288; slot++) {
    const t = Math.floor(startMs / 1000) + slot * 300;
    const n = online[slot];
    files[dayOf(t * 1000)].Concurrency.push({ T: t, N: n, Max: n + (n > 0 && r() < 0.3 ? 1 : 0) });
  }
  return files;
}

function main(argv) {
  const o = { days: 30, end: '2026-09-30', seed: 42 };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--out') o.out = argv[++i];
    else if (argv[i] === '--days') o.days = Number(argv[++i]);
    else if (argv[i] === '--end') o.end = argv[++i];
    else if (argv[i] === '--seed') o.seed = Number(argv[++i]);
    else throw new Error('unknown option ' + argv[i]);
  }
  if (!o.out) throw new Error('--out <folder> is required');
  if (!Number.isInteger(o.days) || o.days < 1 || o.days > 365) throw new Error('--days must be 1-365');
  fs.mkdirSync(o.out, { recursive: true });
  const files = generate(o);
  for (const [date, d] of Object.entries(files)) fs.writeFileSync(path.join(o.out, `day-${date}.json`), JSON.stringify(d, null, 2));
  process.stdout.write(`Wrote ${Object.keys(files).length} sample day files to ${o.out}\n`);
}

if (require.main === module) {
  try { main(process.argv.slice(2)); } catch (e) { process.stderr.write('make-sample: ' + e.message + '\n'); process.exitCode = 2; }
}

module.exports = { generate, mulberry32 };
