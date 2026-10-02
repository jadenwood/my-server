#!/usr/bin/env node
'use strict';
// realm-analytics: turns RealmStats day files into one self-contained HTML dashboard. No dependencies, no network.
// It only READS the data folder and refuses to write anything inside it.

const fs = require('node:fs');
const path = require('node:path');
const { loadDir } = require('../lib/load');
const { aggregate, parseTzOffset } = require('../lib/aggregate');
const { render } = require('../lib/render');

const HELP = `Usage: realm-analytics --data <folder> [options]

  --data <folder>      oxide/data/RealmStats on the server (or a copy of it). Required.
  --out <file>         HTML file to write (default: realm-analytics.html in the current folder).
  --json <file>        Also write the aggregated numbers as JSON.
  --days <n>           Window length in days, 1-730 (default 30).
  --until <date>       Last UTC day of the window, YYYY-MM-DD (default: newest day with data).
  --tz <offset>        Show hours in this offset from UTC, e.g. +02:00, -5 or -300 (default UTC).
  --min-house <n>      Group houses with fewer active players than this (default 3).
  --server <label>     Only read day files whose ServerLabel matches.
  --title <text>       Dashboard title (default "Realm analytics").
  --help               Show this help.

Example:
  node analytics/bin/realm-analytics.js --data "G:\\RealmTest\\server\\oxide\\data\\RealmStats" --out stats.html --tz +01:00
`;

function parseArgs(argv) {
  const o = { days: 30, minHouse: 3 };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const val = () => {
      if (i + 1 >= argv.length) throw new Error(a + ' needs a value');
      return argv[++i];
    };
    switch (a) {
      case '--data': o.data = val(); break;
      case '--out': o.out = val(); break;
      case '--json': o.json = val(); break;
      case '--days': o.days = Number(val()); break;
      case '--until': o.until = val(); break;
      case '--tz': o.tz = val(); break;
      case '--min-house': o.minHouse = Number(val()); break;
      case '--server': o.server = val(); break;
      case '--title': o.title = val(); break;
      case '-h': case '--help': o.help = true; break;
      default:
        if (!a.startsWith('-') && !o.data) o.data = a;
        else throw new Error('unknown option ' + a);
    }
  }
  if (o.help) return o;
  if (!o.data) throw new Error('--data <folder> is required');
  if (!Number.isInteger(o.days) || o.days < 1 || o.days > 730) throw new Error('--days must be a whole number from 1 to 730');
  if (!Number.isInteger(o.minHouse) || o.minHouse < 1 || o.minHouse > 1000) throw new Error('--min-house must be a whole number from 1 to 1000');
  if (o.until && !/^\d{4}-\d{2}-\d{2}$/.test(o.until)) throw new Error('--until must be YYYY-MM-DD');
  if (o.title && o.title.length > 120) throw new Error('--title is limited to 120 characters');
  o.tzMinutes = parseTzOffset(o.tz);
  return o;
}

function isInside(child, parent) {
  const rel = path.relative(path.resolve(parent), path.resolve(child));
  return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

function run(argv, out = process.stdout, err = process.stderr) {
  let o;
  try {
    o = parseArgs(argv);
  } catch (e) {
    err.write('realm-analytics: ' + e.message + '\n\n' + HELP);
    return 2;
  }
  if (o.help) { out.write(HELP); return 0; }
  const outFile = path.resolve(o.out || 'realm-analytics.html');
  for (const f of [outFile, o.json && path.resolve(o.json)].filter(Boolean)) {
    if (isInside(f, o.data)) { err.write(`realm-analytics: refusing to write ${f} inside the data folder; pick another --out/--json.\n`); return 2; }
    try { if (fs.statSync(f).isDirectory()) { err.write(`realm-analytics: ${f} is a folder.\n`); return 2; } } catch (_) { /* does not exist yet */ }
  }
  let loaded;
  try {
    loaded = loadDir(o.data, { server: o.server });
  } catch (e) {
    err.write('realm-analytics: ' + e.message + '\n');
    return 1;
  }
  if (loaded.days.size === 0) {
    err.write(`realm-analytics: no readable day-YYYY-MM-DD.json files in ${o.data}.\n`);
    for (const w of loaded.warnings) err.write('  ' + w + '\n');
    return 1;
  }
  const agg = aggregate(loaded, { days: o.days, until: o.until, tzMinutes: o.tzMinutes, minHousePlayers: o.minHouse });
  const html = render(agg, { title: o.title, server: o.server, filesRead: loaded.filesRead, filesSkipped: loaded.filesSkipped });
  fs.writeFileSync(outFile, html);
  if (o.json) fs.writeFileSync(path.resolve(o.json), JSON.stringify(agg, (k, v) => (v instanceof Set ? [...v] : v), 2));
  out.write(`Wrote ${outFile} (${agg.window.start} to ${agg.window.end}, ${agg.totals.uniquePlayers} players, ${loaded.filesRead} files read).\n`);
  for (const w of agg.warnings) out.write('  note: ' + w + '\n');
  return 0;
}

if (require.main === module) process.exitCode = run(process.argv.slice(2));

module.exports = { run, parseArgs };
