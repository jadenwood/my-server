'use strict';
// Builds the self-contained dashboard HTML (inline CSS, inline SVG, a few lines of inline JS for tooltips and the
// theme toggle). No external requests of any kind: the file can be opened offline or attached to a message.

const { esc, concurrencyChart, dailyActiveChart, hourBars, heatmapChart, hBars, fmtInt, fmt1 } = require('./charts');

const HALF = 500;                              // viewBox width of charts in half-width cards
const pct = (v) => (v == null ? '-' : Math.round(v * 100) + '%');

function tzLabel(min) {
  if (!min) return 'UTC';
  const s = min < 0 ? '-' : '+';
  const a = Math.abs(min);
  return `UTC${s}${String(Math.floor(a / 60)).padStart(2, '0')}:${String(a % 60).padStart(2, '0')}`;
}

function stamp(t, tz) {
  if (t == null) return '-';
  return new Date(t * 1000 + tz * 60000).toISOString().slice(0, 16).replace('T', ' ') + ' ' + tzLabel(tz);
}

function table(headers, rows, cls = '') {
  return `<table class="${cls}"><thead><tr>${headers.map((h) => `<th scope="col">${esc(h)}</th>`).join('')}</tr></thead><tbody>${rows
    .map((r) => `<tr>${r.map((c, i) => (i === 0 ? `<th scope="row">${esc(c)}</th>` : `<td>${esc(c)}</td>`)).join('')}</tr>`)
    .join('')}</tbody></table>`;
}

function details(summary, inner) {
  return `<details><summary>${esc(summary)}</summary><div class="tablewrap">${inner}</div></details>`;
}

function legend(items) {
  return `<div class="legend">${items.map(([cls, label]) => `<span><i class="sw ${cls}"></i>${esc(label)}</span>`).join('')}</div>`;
}

function tile(label, value, sub) {
  return `<div class="tile"><div class="tl">${esc(label)}</div><div class="tv">${esc(value)}</div>${sub ? `<div class="ts">${esc(sub)}</div>` : ''}</div>`;
}

function retentionCell(v, count, size) {
  if (v == null) return '<td class="rc rn">-</td>';
  const q = Math.min(8, Math.floor(v * 8.999));
  return `<td class="rc q${q}" title="${esc(count + ' of ' + size)}">${esc(pct(v))}</td>`;
}

function render(agg, opts = {}) {
  const tz = agg.window.tzMinutes || 0;
  const tzl = tzLabel(tz);
  const title = opts.title || 'Realm analytics';
  const server = opts.server ? ` · ${opts.server}` : '';
  const t = agg.totals;
  const generated = opts.generatedAt || new Date().toISOString().slice(0, 16).replace('T', ' ') + ' UTC';

  const tiles = [
    tile('Unique players', fmtInt(t.uniquePlayers), `${agg.window.days}-day window`),
    tile('New players', fmtInt(t.newPlayers), 'first seen in the window'),
    tile('Average daily active', fmt1(t.avgDailyActive), `${agg.window.daysWithData} days with data`),
    tile('Peak online', fmtInt(t.peakConcurrent), stamp(t.peakAt, tz)),
    tile('Median session', t.medianSessionMin == null ? '-' : fmt1(t.medianSessionMin) + ' min', `${fmtInt(t.sessions)} sessions · ${fmtInt(t.playHours)} h played`),
    tile('Day-1 retention', pct(agg.retention.d1), `${fmtInt(agg.retention.d1Players)} new players scored`),
    tile('Day-7 retention', pct(agg.retention.d7), `${fmtInt(agg.retention.d7Players)} new players scored`),
  ].join('');

  const warn = agg.warnings.length
    ? `<section class="card warn" role="note"><h2><span aria-hidden="true">⚠</span> Data notes</h2><ul>${agg.warnings.map((w) => `<li>${esc(w)}</li>`).join('')}</ul></section>`
    : '';

  const conc = agg.concurrency;
  const concTable = table(['Time (' + tzl + ')', 'Average online', 'Peak online'],
    conc.points.slice(-400).map((p) => [stamp(p.t, tz), fmt1(p.avg), fmtInt(p.max)]));

  const dailyTable = table(['Date (UTC)', 'Active', 'New', 'Returning', 'Sessions', 'Hours played', 'Peak online', 'Joins', 'Deaths'],
    agg.daily.map((d) => (d.missing ? [d.date, 'no data', '', '', '', '', '', '', '']
      : [d.date + (d.degraded ? ' (degraded)' : ''), fmtInt(d.active), d.newPlayers == null ? '?' : fmtInt(d.newPlayers), d.returning == null ? '?' : fmtInt(d.returning),
        fmtInt(d.sessions), fmt1(d.hours), fmtInt(d.peak), fmtInt(d.joins), fmtInt(d.deaths)])));

  const hm = agg.heatmap;
  const hmTable = table(['Weekday', ...Array.from({ length: 24 }, (_, h) => String(h).padStart(2, '0'))],
    hm.grid.map((row, wd) => [hm.weekdays[wd], ...row.map((v) => (v == null ? '' : fmt1(v)))]), 'dense');
  const peakText = hm.peak ? `Busiest hour: ${hm.peak.weekday} ${String(hm.peak.hour).padStart(2, '0')}:00 ${tzl} (average ${fmt1(hm.peak.value)} online).` : 'No samples yet.';

  const ret = agg.retention;
  const retRows = ret.cohorts.slice().reverse().map((c) => `<tr><th scope="row">${esc(c.date)}</th><td>${fmtInt(c.size)}</td>${retentionCell(c.d1, c.d1Count, c.size)}${retentionCell(c.d7, c.d7Count, c.size)}<td class="note">${esc(c.note)}</td></tr>`).join('');
  const retTable = ret.cohorts.length
    ? `<div class="tablewrap"><table class="retention"><thead><tr><th scope="col">Cohort (first seen, UTC)</th><th scope="col">New players</th><th scope="col">Day 1</th><th scope="col">Day 7</th><th scope="col">Note</th></tr></thead><tbody>${retRows}</tbody></table></div>`
    : '<p class="muted">No cohorts yet: retention needs days with new players and the following days on file.</p>';

  const H = agg.houses;
  const houseRows = H.rows.map((r) => ({ ...r, label: r.folded ? 'Houses over the plugin cap' : r.name, tip: `${fmt1(r.hours)} player-hours · ${fmtInt(r.players)} players · ${fmtInt(r.sessions)} sessions · ${fmtInt(r.kills)} kills / ${fmtInt(r.deaths)} deaths` }));
  if (H.small) houseRows.push({ ...H.small, label: 'Smaller houses', tip: H.small.name + ': grouped so no small house reveals one person\'s play time.' });
  const houseChart = houseRows.length
    ? hBars(houseRows.slice(0, 20), { title: 'Player-hours by house', value: (r) => r.hours, fmt: fmt1, unit: ' h', width: HALF, labelW: 150 })
    : '<p class="muted">No house activity in this window.</p>';
  const houseTable = table(['House', 'Players', 'Player-hours', 'Sessions', 'PvP kills', 'Deaths', 'Days active'],
    houseRows.map((r) => [r.label, fmtInt(r.players), fmt1(r.hours), fmtInt(r.sessions), fmtInt(r.kills), fmtInt(r.deaths), r.activeDays == null ? '-' : fmtInt(r.activeDays)]));

  const deathRows = agg.deaths.map((d) => ({ label: d.label, value: d.count, tip: `${fmtInt(d.count)} deaths (${pct(d.share)})` }));
  const deathChart = deathRows.length
    ? hBars(deathRows, { title: 'Deaths by cause', value: (r) => r.value, fmt: fmtInt, width: HALF, labelW: 170 })
    : '<p class="muted">No deaths recorded in this window.</p>';

  const sessRows = agg.sessionHist.map((b) => ({ label: b.label, value: b.count }));
  const sessChart = hBars(sessRows, { title: 'Session length', value: (r) => r.value, fmt: fmtInt, labelW: 90, width: HALF });

  const joinsChart = hourBars(agg.joinsByHour, { title: `Joins by hour (${tzl})`, label: 'Joins', tzLabel: tzl, width: HALF });

  const range = agg.window.start ? `${agg.window.start} to ${agg.window.end} (UTC days)` : 'no data';

  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>Realm Analytics</title>
<style>
:root {
  color-scheme: light;
  --page: #f9f9f7; --surface: #fcfcfb; --ink: #0b0b0b; --ink2: #52514e; --muted: #6f6e69;
  --grid: #e1e0d9; --axis: #c3c2b7; --ring: rgba(11,11,11,0.10);
  --s1: #2a78d6; --s1a: rgba(42,120,214,0.16); --s2: #eb6834; --mutedfill: #b9b8b1;
  --q0: #cde2fb; --q1: #b7d3f6; --q2: #9ec5f4; --q3: #86b6ef; --q4: #5598e7; --q5: #2a78d6; --q6: #1c5cab; --q7: #104281; --q8: #0d366b; --qn: #efeeea;
  --qt-light: #0b0b0b; --qt-dark: #ffffff; --warn: #fab219; --warnbg: #fff7e3;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
    --page: #0d0d0d; --surface: #1a1a19; --ink: #ffffff; --ink2: #c3c2b7; --muted: #9a9991;
    --grid: #2c2c2a; --axis: #383835; --ring: rgba(255,255,255,0.10);
    --s1: #3987e5; --s1a: rgba(57,135,229,0.22); --s2: #d95926; --mutedfill: #4a4a46;
    --q0: #0d366b; --q1: #104281; --q2: #184f95; --q3: #1c5cab; --q4: #256abf; --q5: #3987e5; --q6: #6da7ec; --q7: #9ec5f4; --q8: #cde2fb; --qn: #242422;
    --qt-light: #ffffff; --qt-dark: #0b0b0b; --warnbg: #2a2414;
  }
}
:root[data-theme="dark"] {
  color-scheme: dark;
  --page: #0d0d0d; --surface: #1a1a19; --ink: #ffffff; --ink2: #c3c2b7; --muted: #9a9991;
  --grid: #2c2c2a; --axis: #383835; --ring: rgba(255,255,255,0.10);
  --s1: #3987e5; --s1a: rgba(57,135,229,0.22); --s2: #d95926; --mutedfill: #4a4a46;
  --q0: #0d366b; --q1: #104281; --q2: #184f95; --q3: #1c5cab; --q4: #256abf; --q5: #3987e5; --q6: #6da7ec; --q7: #9ec5f4; --q8: #cde2fb; --qn: #242422;
  --qt-light: #ffffff; --qt-dark: #0b0b0b; --warnbg: #2a2414;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--page); color: var(--ink); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
main { max-width: 1120px; margin: 0 auto; padding: 24px 16px 48px; }
header { display: flex; flex-wrap: wrap; align-items: flex-end; justify-content: space-between; gap: 8px 16px; margin-bottom: 16px; }
h1 { font-size: 24px; margin: 0; letter-spacing: -0.01em; }
h2 { font-size: 16px; margin: 0 0 4px; }
.sub, .muted { color: var(--ink2); }
.small { font-size: 13px; }
button.theme { font: inherit; font-size: 13px; color: var(--ink2); background: var(--surface); border: 1px solid var(--ring); border-radius: 8px; padding: 4px 10px; cursor: pointer; }
.tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(132px, 1fr)); gap: 12px; margin: 0 0 16px; }
.tile { background: var(--surface); border: 1px solid var(--ring); border-radius: 12px; padding: 12px 14px; }
.tl { color: var(--ink2); font-size: 13px; }
.tv { font-size: 26px; font-weight: 600; line-height: 1.25; }
.ts { color: var(--muted); font-size: 12px; }
.grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 480px), 1fr)); gap: 16px; }
.card { background: var(--surface); border: 1px solid var(--ring); border-radius: 12px; padding: 16px; margin-bottom: 16px; min-width: 0; }
.card p { margin: 0 0 8px; }
.warn { border-color: var(--warn); background: var(--warnbg); }
.warn ul { margin: 4px 0 0; padding-left: 20px; }
.legend { display: flex; flex-wrap: wrap; gap: 4px 16px; font-size: 13px; color: var(--ink2); margin: 2px 0 6px; }
.legend .sw { display: inline-block; width: 12px; height: 12px; border-radius: 3px; margin-right: 6px; vertical-align: -1px; }
.sw.s1 { background: var(--s1); } .sw.s2 { background: var(--s2); }
.scale { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--ink2); margin-top: 4px; }
.scale i { display: inline-block; width: 18px; height: 10px; border-radius: 2px; }
svg.chart { display: block; overflow: visible; }
svg .grid { stroke: var(--grid); stroke-width: 1; }
svg .axis { stroke: var(--axis); stroke-width: 1; }
svg .tick { fill: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
svg .label { fill: var(--ink2); font-size: 12px; }
svg .value { fill: var(--ink2); font-size: 12px; font-variant-numeric: tabular-nums; }
svg .s1-area { fill: var(--s1a); stroke: none; }
svg .s1-line { fill: none; stroke: var(--s1); stroke-width: 2; stroke-linejoin: round; }
svg .s2-line { fill: none; stroke: var(--s2); stroke-width: 1.5; stroke-linejoin: round; }
svg .s1-fill { fill: var(--s1); } svg .s2-fill { fill: var(--s2); } svg .muted-fill { fill: var(--mutedfill); }
svg .hit { fill: transparent; }
svg .hit:hover, svg .cell:hover { outline: none; }
svg .crosshair { stroke: var(--ink2); stroke-width: 1; pointer-events: none; }
svg .cell { stroke: none; }
svg .cell:hover { stroke: var(--ink); stroke-width: 1.5; }
${[0, 1, 2, 3, 4, 5, 6, 7, 8].map((i) => `.q${i} { fill: var(--q${i}); background: var(--q${i}); }`).join('\n')}
.qn { fill: var(--qn); background: var(--qn); }
table { border-collapse: collapse; width: 100%; font-size: 13px; font-variant-numeric: tabular-nums; }
th, td { padding: 4px 8px; text-align: right; border-bottom: 1px solid var(--grid); white-space: nowrap; }
th:first-child, td:first-child { text-align: left; }
thead th { color: var(--ink2); font-weight: 600; }
table.dense th, table.dense td { padding: 2px 4px; font-size: 11px; }
.tablewrap { overflow-x: auto; max-height: 420px; overflow-y: auto; }
td.rc { text-align: center; font-weight: 600; min-width: 64px; }
td.q0, td.q1, td.q2, td.q3, td.q4 { color: var(--qt-light); }
td.q5, td.q6, td.q7, td.q8 { color: var(--qt-dark); }
td.rn { color: var(--muted); }
td.note { color: var(--muted); text-align: left; }
details { margin-top: 8px; font-size: 13px; }
summary { cursor: pointer; color: var(--ink2); }
#tip { position: fixed; z-index: 10; pointer-events: none; background: var(--ink); color: var(--page); font-size: 12px; line-height: 1.4; padding: 6px 8px; border-radius: 6px; white-space: pre; display: none; max-width: 320px; box-shadow: 0 2px 8px rgba(0,0,0,0.2); }
footer { color: var(--ink2); font-size: 13px; }
footer dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; margin: 8px 0; }
footer dt { font-weight: 600; color: var(--ink); }
footer dd { margin: 0; }
@media (max-width: 560px) { .tv { font-size: 22px; } footer dl { grid-template-columns: 1fr; } }
</style>
</head>
<body>
<main>
<header>
  <div>
    <h1>${esc(title)}${esc(server)}</h1>
    <div class="sub">${esc(range)} · times in ${esc(tzl)} · generated ${esc(generated)}</div>
  </div>
  <button class="theme" type="button" id="themeBtn" aria-label="Switch light or dark theme">Theme</button>
</header>

<section class="tiles" aria-label="Headline numbers">${tiles}</section>
${warn}
<section class="card">
  <h2>Players online over time</h2>
  ${legend([['s1', 'Average online (per ' + (conc.bucketMinutes >= 60 ? conc.bucketMinutes / 60 + ' h' : conc.bucketMinutes + ' min') + ')'], ['s2', 'Peak online']])}
  ${concurrencyChart(conc, { tzMinutes: tz })}
  ${details('Show as a table (newest 400 rows)', concTable)}
</section>

<div class="grid2">
<section class="card">
  <h2>Daily active players</h2>
  ${legend([['s1', 'Returning'], ['s2', 'New']])}
  ${dailyActiveChart(agg.daily, { width: HALF })}
  ${details('Show as a table', dailyTable)}
</section>
<section class="card">
  <h2>Peak hours</h2>
  <p class="small muted">${esc(peakText)}</p>
  ${heatmapChart(hm, { tzLabel: tzl, width: HALF })}
  <div class="scale" aria-hidden="true">fewer ${[0, 2, 4, 6, 8].map((i) => `<i class="q${i}"></i>`).join('')} more · <i class="qn"></i> no samples</div>
  ${details('Show as a table', hmTable)}
</section>
</div>

<section class="card">
  <h2>Retention by cohort</h2>
  <p class="small muted">Of the players first seen on a day, the share who came back exactly 1 and 7 days later. Weighted averages: day 1 ${esc(pct(ret.d1))}, day 7 ${esc(pct(ret.d7))}.</p>
  ${retTable}
</section>

<div class="grid2">
<section class="card">
  <h2>House activity</h2>
  <p class="small muted">Online time of each house's members. Houses with fewer than ${esc(H.minPlayers)} active players are grouped.</p>
  ${houseChart}
  ${details('Show as a table', houseTable)}
</section>
<section class="card">
  <h2>Deaths by cause</h2>
  <p class="small muted">${esc(fmtInt(t.deaths))} deaths in the window.</p>
  ${deathChart}
  ${details('Show as a table', table(['Cause', 'Deaths', 'Share'], agg.deaths.map((d) => [d.label, fmtInt(d.count), pct(d.share)])))}
</section>
</div>

<div class="grid2">
<section class="card">
  <h2>Session length</h2>
  <p class="small muted">Median ${esc(t.medianSessionMin == null ? '-' : fmt1(t.medianSessionMin) + ' min')}. ${esc(fmtInt(t.recoveredSessions))} sessions were closed after a crash (ended at the last save).</p>
  ${sessChart}
  ${details('Show as a table', table(['Length', 'Sessions'], agg.sessionHist.map((b) => [b.label, fmtInt(b.count)])))}
</section>
<section class="card">
  <h2>Joins by hour</h2>
  <p class="small muted">When players connect, all days in the window together.</p>
  ${joinsChart}
  ${details('Show as a table', table(['Hour (' + tzl + ')', 'Joins', 'Leaves'], agg.joinsByHour.map((v, h) => [String(h).padStart(2, '0') + ':00', fmtInt(v), fmtInt(agg.leavesByHour[h])])))}
</section>
</div>

<footer class="card">
  <h2>How to read this</h2>
  <dl>
    <dt>Player</dt><dd>A salted one-way key made by the RealmStats plugin. This report never contains names, Steam ids, IPs, chat or positions, and keys are not shown.</dd>
    <dt>Active / new</dt><dd>Active: seen online that UTC day. New: seen for the first time that UTC day.</dd>
    <dt>Day-1 / Day-7</dt><dd>Of the new players of a day, the share active exactly 1 or 7 days later. A cohort is scored only when the later day has data, both days use the same salt, and neither day was written in degraded mode.</dd>
    <dt>Online</dt><dd>Players online at each sample (every few minutes); "peak" is the most at once between two samples.</dd>
    <dt>Houses</dt><dd>From RealmHouses (or the game guild if RealmHouses is not loaded). PvP kills count the killer's house.</dd>
  </dl>
  <p class="small">Made by <code>analytics/</code> from <code>oxide/data/RealmStats/day-*.json</code>. Files read: ${esc(opts.filesRead == null ? '-' : opts.filesRead)}${opts.filesSkipped ? `, skipped: ${esc(opts.filesSkipped)}` : ''}.</p>
</footer>
</main>
<div id="tip" role="tooltip"></div>
<script>
(function () {
  var tip = document.getElementById('tip');
  function show(el, x, y) {
    tip.textContent = el.getAttribute('data-tip');
    tip.style.display = 'block';
    var w = tip.offsetWidth, h = tip.offsetHeight;
    var left = Math.min(window.innerWidth - w - 8, x + 14), top = y + 14;
    if (top + h > window.innerHeight - 8) top = y - h - 10;
    tip.style.left = Math.max(8, left) + 'px';
    tip.style.top = Math.max(8, top) + 'px';
    var svg = el.ownerSVGElement, cross = svg && svg.querySelector('.crosshair');
    if (cross) {
      var x0 = el.getAttribute('data-x');
      if (x0) { cross.setAttribute('x1', x0); cross.setAttribute('x2', x0); cross.setAttribute('visibility', 'visible'); }
    }
  }
  function hide(el) {
    tip.style.display = 'none';
    var svg = el && el.ownerSVGElement, cross = svg && svg.querySelector('.crosshair');
    if (cross) cross.setAttribute('visibility', 'hidden');
  }
  document.querySelectorAll('[data-tip]').forEach(function (el) {
    el.addEventListener('pointermove', function (e) { show(el, e.clientX, e.clientY); });
    el.addEventListener('pointerleave', function () { hide(el); });
  });
  var btn = document.getElementById('themeBtn');
  var saved = null;
  try { saved = localStorage.getItem('realm-analytics-theme'); } catch (e) {}
  if (saved === 'light' || saved === 'dark') document.documentElement.setAttribute('data-theme', saved);
  btn.addEventListener('click', function () {
    var cur = document.documentElement.getAttribute('data-theme');
    if (!cur) cur = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    var next = cur === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    try { localStorage.setItem('realm-analytics-theme', next); } catch (e) {}
  });
})();
</script>
</body>
</html>
`;
}

module.exports = { render, tzLabel };
