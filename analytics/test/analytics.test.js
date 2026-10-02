'use strict';
// Tests for the RealmStats dashboard builder. Run: node --test analytics/test   (or: cd analytics && npm test)
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { loadDir, validateDay } = require('../lib/load');
const { aggregate, parseTzOffset, median, windowDates } = require('../lib/aggregate');
const { render } = require('../lib/render');
const { niceScale, esc } = require('../lib/charts');
const { run } = require('../bin/realm-analytics');
const { generate } = require('../tools/make-sample');

const SMALL = path.join(__dirname, 'fixtures', 'small');
const SIM = path.join(__dirname, 'fixtures', 'sim');      // written by the REAL plugin code (analytics/plugin-tests)
const ts = (s) => Date.parse(s.replace(' ', 'T') + ':00Z') / 1000;
const K = (c) => c.repeat(16);

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'realm-analytics-'));
}
function sink() {
  const s = { text: '', write(x) { s.text += x; } };
  return s;
}

test('loader merges recovery files, skips corrupt files and ignores state.json', () => {
  const l = loadDir(SMALL);
  assert.deepEqual([...l.days.keys()].sort(), ['2026-09-01', '2026-09-02', '2026-09-08']);
  assert.equal(l.filesRead, 4);
  assert.equal(l.filesSkipped, 1);
  assert.match(l.warnings.join('\n'), /Skipped day-2026-09-05\.json/);
  const d8 = l.days.get('2026-09-08');
  assert.deepEqual([...d8.active].sort(), [K('a'), K('c'), K('e'), K('f')]);
  assert.deepEqual([...d8.newKeys], [K('f')]);
  assert.equal(d8.concurrency.get(ts('2026-09-08 21:00')).max, 4, 'same sample time keeps the larger value');
  assert.equal(d8.deaths.pvp, 1);
  assert.equal(d8.files.length, 2);
});

test('validateDay rejects wrong shapes', () => {
  assert.deepEqual(validateDay({ Version: 1, Date: '2026-09-01', Active: [] }, '2026-09-01'), []);
  assert.match(validateDay([], 'x')[0], /not a JSON object/);
  assert.match(validateDay({ Date: '2026-09-02' }, '2026-09-01').join(), /does not match/);
  assert.match(validateDay({ Joins: [1, 2] }, 'x').join(), /24-hour/);
  assert.match(validateDay({ Active: 'abc' }, 'x').join(), /Active is not an array/);
  assert.match(validateDay({ Version: 2 }, 'x').join(), /unsupported Version/);
});

test('aggregate: totals, retention, peaks and sessions match hand-computed values', () => {
  const a = aggregate(loadDir(SMALL), { days: 8 });
  assert.equal(a.window.start, '2026-09-01');
  assert.equal(a.window.end, '2026-09-08');
  assert.equal(a.window.daysWithData, 3);
  assert.equal(a.totals.uniquePlayers, 6);
  assert.equal(a.totals.newPlayers, 6);
  assert.equal(a.totals.avgDailyActive, 11 / 3);
  assert.equal(a.totals.peakConcurrent, 5);
  assert.equal(a.totals.peakAt, ts('2026-09-01 20:00'));
  assert.equal(a.totals.sessions, 4);
  assert.equal(a.totals.medianSessionMin, 35);
  assert.equal(a.totals.playHours, 3.2);
  assert.equal(a.totals.recoveredSessions, 1);
  assert.equal(a.totals.deaths, 5);
  // D1: of a,b,c,d (new 09-01), a and b are active 09-02. D7: a and c are active 09-08 (merged with the -r1 file).
  assert.equal(a.retention.d1, 0.5);
  assert.equal(a.retention.d7, 0.5);
  assert.equal(a.retention.d1Players, 4);
  const c2 = a.retention.cohorts.find((c) => c.date === '2026-09-02');
  assert.equal(c2.d1, null, '09-03 has no file: not scored');
  assert.equal(c2.d7, null);
  assert.deepEqual(a.sessionHist.map((b) => b.count), [1, 1, 0, 0, 1, 1, 0]);
  assert.deepEqual(a.deaths.map((d) => [d.cause, d.count]), [['pvp', 4], ['fall', 1]]);
  assert.equal(a.daily.filter((d) => d.missing).length, 5);
  const w = a.warnings.join('\n');
  assert.match(w, /5 of 8 days in the window have no data file/);
  assert.match(w, /dropped 2 "sessions"/);
});

test('aggregate: house table groups small houses', () => {
  const a = aggregate(loadDir(SMALL), { days: 8, minHousePlayers: 3 });
  assert.equal(a.houses.rows.length, 1);
  const v = a.houses.rows[0];
  assert.deepEqual([v.name, v.players, v.hours, v.sessions, v.deaths, v.kills, v.activeDays], ['Varrow', 4, 4, 5, 2, 1, 2]);
  assert.equal(a.houses.small.players, 1);
  assert.match(a.houses.small.name, /1, under 3 players/);
  const b = aggregate(loadDir(SMALL), { days: 8, minHousePlayers: 1 });
  assert.equal(b.houses.rows.length, 2);
  assert.equal(b.houses.small, null);
});

test('aggregate: heatmap and hourly joins respect the time zone', () => {
  const utc = aggregate(loadDir(SMALL), { days: 8 });
  // 2026-09-01 is a Tuesday (row 1). 19:00 has samples 1 and 3 -> 2.
  assert.equal(utc.heatmap.grid[1][19], 2);
  assert.equal(utc.heatmap.grid[1][20], 4);
  assert.equal(utc.heatmap.grid[2][19], 2);
  assert.equal(utc.heatmap.grid[0][0], null);
  assert.deepEqual(utc.heatmap.peak, { weekday: 'Tue', hour: 20, value: 4 });
  assert.equal(utc.joinsByHour[19], 3);
  const plus2 = aggregate(loadDir(SMALL), { days: 8, tzMinutes: 120 });
  assert.equal(plus2.heatmap.grid[1][21], 2);
  assert.equal(plus2.joinsByHour[21], 3);
  assert.equal(plus2.leavesByHour[23], 4);
  const minus5 = aggregate(loadDir(SMALL), { days: 8, tzMinutes: -300 });
  assert.equal(minus5.heatmap.grid[1][14], 2, '19:00 UTC Tuesday is 14:00 at UTC-05:00');
  assert.equal(minus5.joinsByHour[14], 3);
});

test('aggregate: degraded days and salt changes are excluded from retention', () => {
  const dir = tmpDir();
  const base = (date, extra) => Object.assign({ Version: 1, Date: date, SaltId: 'aaaaaaaa', Active: [], New: [], Joins: new Array(24).fill(0), Leaves: new Array(24).fill(0) }, extra);
  fs.writeFileSync(path.join(dir, 'day-2026-01-01.json'), JSON.stringify(base('2026-01-01', { Active: [K('a')], New: [K('a')] })));
  fs.writeFileSync(path.join(dir, 'day-2026-01-02.json'), JSON.stringify(base('2026-01-02', { Active: [K('a')], SaltId: 'bbbbbbbb' })));
  fs.writeFileSync(path.join(dir, 'day-2026-01-03.json'), JSON.stringify(base('2026-01-03', { Active: [K('b')], New: [K('b')], SaltId: 'bbbbbbbb', Degraded: true })));
  fs.writeFileSync(path.join(dir, 'day-2026-01-04.json'), JSON.stringify(base('2026-01-04', { Active: [K('b')], SaltId: 'bbbbbbbb' })));
  const a = aggregate(loadDir(dir), { days: 7 });
  assert.equal(a.retention.d1, null);
  assert.equal(a.retention.cohorts[0].note, 'salt changed');
  assert.equal(a.retention.cohorts.length, 1, 'degraded day has no cohort');
  const w = a.warnings.join('\n');
  assert.match(w, /salt changed inside this window/);
  assert.match(w, /Degraded/);
  assert.equal(a.daily.find((d) => d.date === '2026-01-03').newPlayers, null);
});

test('loader: --server filter and mixed-server warning', () => {
  const dir = tmpDir();
  fs.writeFileSync(path.join(dir, 'day-2026-01-01.json'), JSON.stringify({ Version: 1, Date: '2026-01-01', Server: 'north', Active: [K('a')] }));
  fs.writeFileSync(path.join(dir, 'day-2026-01-02.json'), JSON.stringify({ Version: 1, Date: '2026-01-02', Server: 'south', Active: [K('b')] }));
  assert.match(loadDir(dir).warnings.join(), /several servers/);
  const only = loadDir(dir, { server: 'north' });
  assert.deepEqual([...only.days.keys()], ['2026-01-01']);
  // keys that are not hex are dropped (the plugin never writes anything else here)
  fs.writeFileSync(path.join(dir, 'day-2026-01-03.json'), JSON.stringify({ Version: 1, Active: ['Aldric', K('c')] }));
  assert.deepEqual([...loadDir(dir).days.get('2026-01-03').active], [K('c')]);
});

test('helpers: tz parsing, median, nice scale, window', () => {
  assert.equal(parseTzOffset('+02:00'), 120);
  assert.equal(parseTzOffset('-05:30'), -330);
  assert.equal(parseTzOffset('-5'), -300);
  assert.equal(parseTzOffset('90'), 90);
  assert.equal(parseTzOffset('UTC'), 0);
  assert.equal(parseTzOffset(undefined), 0);
  assert.throws(() => parseTzOffset('+15:00'), /out of range/);
  assert.throws(() => parseTzOffset('Europe/Paris'), /cannot read/);
  assert.equal(median([3, 1, 2]), 2);
  assert.equal(median([]), null);
  assert.deepEqual(niceScale(14).ticks, [0, 5, 10, 15]);
  assert.deepEqual(niceScale(2).ticks, [0, 1, 2]);
  assert.deepEqual(niceScale(0).ticks, [0, 1]);
  assert.deepEqual(windowDates(['2026-02-27', '2026-03-01'], 3), ['2026-02-27', '2026-02-28', '2026-03-01']);
});

test('render: self-contained, escaped, accessible', () => {
  const dir = tmpDir();
  const evil = '<img src=x onerror=alert(1)>"&';
  fs.writeFileSync(path.join(dir, 'day-2026-01-01.json'), JSON.stringify({
    Version: 1, Date: '2026-01-01', Active: [K('a'), K('b'), K('c')], New: [K('a'), K('b'), K('c')],
    Houses: { [evil]: { Active: [K('a'), K('b'), K('c')], Seconds: 7200, Sessions: 3, Deaths: 0, Kills: 0 } },
    Concurrency: [{ T: ts('2026-01-01 10:00'), N: 3, Max: 3 }],
  }));
  const html = render(aggregate(loadDir(dir), { days: 7 }), { title: 'T <b>', server: 'x"y' });
  assert.ok(!html.includes(evil), 'house name is escaped');
  assert.ok(html.includes('&lt;img src=x onerror=alert(1)&gt;'));
  assert.ok(html.includes('T &lt;b&gt;'));
  assert.ok(!/(src|href)=["']?https?:/i.test(html), 'no external resources');
  assert.ok(!/<link\b/i.test(html));
  assert.ok(!html.includes(K('a')), 'player keys never appear in the dashboard');
  assert.match(html, /<title>Realm Analytics<\/title>/);
  assert.match(html, /role="img"/);
  assert.match(html, /prefers-color-scheme: dark/);
  assert.match(html, /data-theme="dark"/);
  assert.ok((html.match(/<details>/g) || []).length >= 6, 'every chart has a table view');
  assert.equal(esc(`<'">&`), '&lt;&#39;&quot;&gt;&amp;');
});

test('render: empty window still renders', () => {
  const html = render(aggregate({ days: new Map(), warnings: [] }, { days: 7 }), {});
  assert.match(html, /No data in this window|no data/i);
});

test('CLI: writes the dashboard and JSON, validates options, never writes into the data folder', () => {
  const out = tmpDir();
  const o = sink(), e = sink();
  const html = path.join(out, 'dash.html'), json = path.join(out, 'agg.json');
  assert.equal(run(['--data', SMALL, '--out', html, '--json', json, '--days', '8', '--tz', '+01:00'], o, e), 0, e.text);
  assert.match(fs.readFileSync(html, 'utf8'), /Realm analytics/);
  const j = JSON.parse(fs.readFileSync(json, 'utf8'));
  assert.equal(j.totals.uniquePlayers, 6);
  assert.match(o.text, /note: Skipped day-2026-09-05/);
  assert.equal(run(['--data', SMALL, '--out', path.join(SMALL, 'x.html')], sink(), e), 2);
  assert.match(e.text, /refusing to write/);
  assert.ok(!fs.existsSync(path.join(SMALL, 'x.html')));
  assert.equal(run(['--days', '3'], sink(), sink()), 2, 'missing --data');
  assert.equal(run(['--data', SMALL, '--days', '0'], sink(), sink()), 2);
  assert.equal(run(['--data', SMALL, '--tz', 'nope'], sink(), sink()), 2);
  assert.equal(run(['--data', SMALL, '--until', '01/02/2026'], sink(), sink()), 2);
  assert.equal(run(['--data', SMALL, '--bogus'], sink(), sink()), 2);
  assert.equal(run(['--data', tmpDir(), '--out', path.join(out, 'e.html')], sink(), sink()), 1, 'no day files');
  assert.equal(run(['--data', path.join(out, 'missing'), '--out', path.join(out, 'm.html')], sink(), sink()), 1);
  const h = sink();
  assert.equal(run(['--help'], h, sink()), 0);
  assert.match(h.text, /Usage/);
});

test('integration: data written by the real plugin code loads cleanly', () => {
  // analytics/test/fixtures/sim is produced by plugins/RealmStats.cs itself (RS_EXPORT=... analytics/plugin-tests/run.sh).
  const l = loadDir(SIM);
  assert.equal(l.filesSkipped, 0, l.warnings.join('\n'));
  assert.ok(l.filesRead >= 14);
  const a = aggregate(l, { days: 30 });
  assert.equal(a.totals.uniquePlayers, 60);
  assert.ok(a.totals.sessions > 100);
  assert.ok(a.totals.peakConcurrent > 0);
  assert.ok(a.retention.d1 != null && a.retention.d1 >= 0 && a.retention.d1 <= 1);
  assert.equal(a.houses.rows.length, 6);
  assert.ok(a.deaths.some((d) => d.cause === 'pvp'));
  for (const d of l.days.values()) {
    assert.equal(d.salts.size, 1);
    assert.equal(d.servers.has('realm-1'), true);
    assert.equal(d.degraded, false);
    for (const k of d.active) assert.match(k, /^[0-9a-f]{16}$/);
  }
  const html = render(a, {});
  for (const k of [...l.days.values()][0].active) assert.ok(!html.includes(k));
});

test('sample generator is deterministic and loads as valid day files', () => {
  const a = generate({ days: 10, seed: 3 }), b = generate({ days: 10, seed: 3 });
  assert.equal(JSON.stringify(a), JSON.stringify(b));
  const dir = tmpDir();
  for (const [date, d] of Object.entries(a)) fs.writeFileSync(path.join(dir, `day-${date}.json`), JSON.stringify(d));
  const l = loadDir(dir);
  assert.equal(l.filesSkipped, 0);
  assert.equal(l.days.size, 10);
  const agg = aggregate(l, { days: 10 });
  assert.ok(agg.retention.d1 > 0 && agg.retention.d1 < 1);
  assert.ok(agg.totals.peakConcurrent > 0);
});

test('death causes and dropped keys named like Object.prototype members are plain keys', () => {
  const dir = tmpDir();
  fs.writeFileSync(path.join(dir, 'day-2026-01-01.json'), JSON.stringify({
    Version: 1, Date: '2026-01-01', Active: [K('a')], New: [K('a')],
    Deaths: JSON.parse('{"constructor": 2, "toString": 3, "__proto__": 4, "pvp": 1}'),
    Dropped: JSON.parse('{"hasOwnProperty": 1}'),
  }));
  const agg = aggregate(loadDir(dir), { days: 7 });
  const byCause = Object.fromEntries(agg.deaths.map((d) => [d.cause, d]));
  assert.equal(agg.totals.deaths, 10);
  assert.equal(byCause.constructor.count, 2);
  assert.equal(byCause.constructor.label, 'constructor');
  assert.equal(byCause.toString.label, 'toString');
  assert.equal(byCause.__proto__.count, 4);
  const html = render(agg, {});
  assert.ok(!html.includes('NaN'));
  assert.ok(!html.includes('[native code]'));
  assert.ok(agg.warnings.some((w) => w.includes('"hasOwnProperty"')));
});
