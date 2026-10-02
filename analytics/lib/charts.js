'use strict';
// Inline SVG chart builders. Each returns an SVG string with role="img", a <title>, and per-mark data-tip attributes
// that the dashboard's small inline script turns into hover/focus tooltips. Colours come from CSS classes whose custom
// properties are defined (light and dark) in render.js, so the SVG itself carries no hex values.

let W = 720;                                   // viewBox width; set per call from opts.width
const PAD = { l: 44, r: 16, t: 14, b: 30 };

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const fmtInt = (v) => (v == null ? '-' : Math.round(v).toLocaleString('en-US'));
const fmt1 = (v) => (v == null ? '-' : (Math.round(v * 10) / 10).toLocaleString('en-US', { maximumFractionDigits: 1 }));

// "Nice" axis: 0..top with 3-5 steps of 1/2/5 x 10^k.
function niceScale(max, target = 4) {
  if (!(max > 0)) return { top: 1, step: 1, ticks: [0, 1] };
  const raw = max / target;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].map((m) => m * mag).find((s) => s >= raw);
  const st = Math.max(step, max < 4 ? 1 : step);
  const top = Math.ceil(max / st) * st;
  const ticks = [];
  for (let v = 0; v <= top + 1e-9; v += st) ticks.push(Math.round(v * 1e6) / 1e6);
  return { top, step: st, ticks };
}

function svgOpen(h, title, desc) {
  return `<svg class="chart" viewBox="0 0 ${W} ${h}" width="100%" role="img" aria-label="${esc(title)}" preserveAspectRatio="xMidYMid meet"><title>${esc(title)}</title>${desc ? `<desc>${esc(desc)}</desc>` : ''}`;
}

function yAxis(scale, y, x0, x1, fmt = fmtInt) {
  let s = '';
  for (const v of scale.ticks) {
    const yy = y(v);
    s += `<line class="${v === 0 ? 'axis' : 'grid'}" x1="${x0}" x2="${x1}" y1="${yy}" y2="${yy}"/>`;
    s += `<text class="tick" x="${x0 - 6}" y="${yy + 4}" text-anchor="end">${esc(fmt(v))}</text>`;
  }
  return s;
}

function localDate(t, tz) {
  return new Date(t * 1000 + tz * 60000);
}
function dayLabel(t, tz) {
  const d = localDate(t, tz);
  return d.toISOString().slice(5, 10);
}
function stampLabel(t, tz) {
  const d = localDate(t, tz);
  return d.toISOString().slice(0, 16).replace('T', ' ');
}

// Time series of players online: an area for the hourly average and a thin line for the hourly peak.
function concurrencyChart(series, { tzMinutes = 0, height = 240, title = 'Players online over time' } = {}) {
  const pts = series.points;
  if (!pts.length) return emptyChart(title, height);
  const x0 = PAD.l, x1 = W - PAD.r, y0 = height - PAD.b, y1 = PAD.t;
  const tMin = pts[0].t, tMax = pts[pts.length - 1].t + series.bucketMinutes * 60;
  const scale = niceScale(Math.max(...pts.map((p) => p.max), 1));
  const x = (t) => x0 + ((t - tMin) / Math.max(1, tMax - tMin)) * (x1 - x0);
  const y = (v) => y0 - (v / scale.top) * (y0 - y1);
  const bw = series.bucketMinutes * 60;
  let s = svgOpen(height, title, `Average and peak players online per ${series.bucketMinutes >= 60 ? series.bucketMinutes / 60 + ' h' : series.bucketMinutes + ' min'} bucket.`);
  s += yAxis(scale, y, x0, x1);
  // x ticks at local midnights, at most ~8 labels
  const tz = tzMinutes * 60;
  const firstMidnight = Math.ceil((tMin + tz) / 86400) * 86400 - tz;
  const nDays = Math.max(1, Math.round((tMax - tMin) / 86400));
  const every = Math.max(1, Math.ceil(nDays / 8));
  for (let t = firstMidnight, i = 0; t <= tMax; t += 86400, i++) {
    if (i % every) continue;
    s += `<line class="grid" x1="${x(t)}" x2="${x(t)}" y1="${y0}" y2="${y0 + 4}"/><text class="tick" x="${x(t)}" y="${y0 + 18}" text-anchor="middle">${esc(dayLabel(t, tzMinutes))}</text>`;
  }
  // Break the line where a bucket is missing (server off): one path segment per contiguous run.
  const runs = [];
  let run = [];
  pts.forEach((p, i) => {
    if (i && p.t - pts[i - 1].t > bw) { runs.push(run); run = []; }
    run.push(p);
  });
  runs.push(run);
  for (const r of runs) {
    const mid = (p) => x(p.t + bw / 2);
    const area = `M${mid(r[0])},${y0} ` + r.map((p) => `L${mid(p)},${y(p.avg)}`).join(' ') + ` L${mid(r[r.length - 1])},${y0} Z`;
    s += `<path class="s1-area" d="${area}"/>`;
    s += `<path class="s1-line" d="${r.map((p, i) => (i ? 'L' : 'M') + mid(p) + ',' + y(p.avg)).join(' ')}"/>`;
    s += `<path class="s2-line" d="${r.map((p, i) => (i ? 'L' : 'M') + mid(p) + ',' + y(p.max)).join(' ')}"/>`;
  }
  // hover columns with a crosshair
  const colW = Math.max(1, (x1 - x0) / Math.max(1, (tMax - tMin) / bw));
  s += `<line class="crosshair" x1="0" x2="0" y1="${y1}" y2="${y0}" visibility="hidden"/>`;
  for (const p of pts) {
    const cx = x(p.t + bw / 2);
    const tip = `${stampLabel(p.t, tzMinutes)}\nAverage online: ${fmt1(p.avg)}\nPeak online: ${fmtInt(p.max)}`;
    s += `<rect class="hit" x="${cx - colW / 2}" y="${y1}" width="${colW}" height="${y0 - y1}" data-x="${cx}" data-tip="${esc(tip)}"/>`;
  }
  return s + '</svg>';
}

// Daily active players as stacked bars: returning (slot 1) under new (slot 2). Missing days leave a labelled gap.
function dailyActiveChart(daily, { height = 220, title = 'Daily active players' } = {}) {
  if (!daily.length) return emptyChart(title, height);
  const x0 = PAD.l, x1 = W - PAD.r, y0 = height - PAD.b, y1 = PAD.t;
  const scale = niceScale(Math.max(1, ...daily.map((d) => (d.missing ? 0 : d.active))));
  const y = (v) => y0 - (v / scale.top) * (y0 - y1);
  const slot = (x1 - x0) / daily.length;
  const bw = Math.max(2, Math.min(28, slot - 2));
  let s = svgOpen(height, title, 'Returning and new players per UTC day.');
  s += yAxis(scale, y, x0, x1);
  const every = Math.max(1, Math.ceil(daily.length / 10));
  daily.forEach((d, i) => {
    const cx = x0 + slot * i + slot / 2;
    if (i % every === 0) s += `<text class="tick" x="${cx}" y="${y0 + 18}" text-anchor="middle">${esc(d.date.slice(5))}</text>`;
    if (d.missing) {
      s += `<rect class="hit" x="${cx - slot / 2}" y="${y1}" width="${slot}" height="${y0 - y1}" data-tip="${esc(d.date + '\nNo data file')}"/>`;
      return;
    }
    const ret = d.returning == null ? d.active : d.returning;
    const nw = d.newPlayers || 0;
    const hRet = y0 - y(ret), hNew = y0 - y(nw);
    if (ret > 0) s += barPath(cx - bw / 2, y0 - hRet, bw, hRet, 's1-fill', nw === 0);
    if (nw > 0) s += barPath(cx - bw / 2, y0 - hRet - hNew - (ret > 0 ? 2 : 0), bw, hNew, 's2-fill', true);
    const tip = `${d.date}${d.degraded ? ' (degraded)' : ''}\nActive: ${fmtInt(d.active)}\nNew: ${d.newPlayers == null ? 'unknown' : fmtInt(d.newPlayers)}\nReturning: ${d.returning == null ? 'unknown' : fmtInt(d.returning)}`;
    s += `<rect class="hit" x="${cx - slot / 2}" y="${y1}" width="${slot}" height="${y0 - y1}" data-tip="${esc(tip)}"/>`;
  });
  return s + '</svg>';
}

// Bar with a 4px rounded data end (top) and a square base on the axis.
function barPath(x, yTop, w, h, cls, roundTop) {
  if (h <= 0) return '';
  const r = roundTop ? Math.min(4, w / 2, h) : 0;
  const d = r
    ? `M${x},${yTop + h} L${x},${yTop + r} Q${x},${yTop} ${x + r},${yTop} L${x + w - r},${yTop} Q${x + w},${yTop} ${x + w},${yTop + r} L${x + w},${yTop + h} Z`
    : `M${x},${yTop + h} L${x},${yTop} L${x + w},${yTop} L${x + w},${yTop + h} Z`;
  return `<path class="${cls}" d="${d}"/>`;
}

// One series of vertical bars over 24 hours (joins per local hour etc).
function hourBars(values, { height = 180, title, label = 'Joins', tzLabel = 'UTC' } = {}) {
  const x0 = PAD.l, x1 = W - PAD.r, y0 = height - PAD.b, y1 = PAD.t;
  const scale = niceScale(Math.max(1, ...values.map((v) => v || 0)));
  const y = (v) => y0 - (v / scale.top) * (y0 - y1);
  const slot = (x1 - x0) / 24, bw = slot - 2;
  let s = svgOpen(height, title, `${label} per hour of the day (${tzLabel}).`);
  s += yAxis(scale, y, x0, x1);
  values.forEach((v, h) => {
    const cx = x0 + slot * h + slot / 2;
    if (h % 3 === 0) s += `<text class="tick" x="${cx}" y="${y0 + 18}" text-anchor="middle">${String(h).padStart(2, '0')}</text>`;
    s += barPath(cx - bw / 2, y(v || 0), bw, y0 - y(v || 0), 's1-fill', true);
    s += `<rect class="hit" x="${cx - slot / 2}" y="${y1}" width="${slot}" height="${y0 - y1}" data-tip="${esc(`${String(h).padStart(2, '0')}:00-${String(h).padStart(2, '0')}:59 ${tzLabel}\n${label}: ${fmt1(v)}`)}"/>`;
  });
  return s + '</svg>';
}

// Weekday x hour heatmap on the sequential ramp (classes q0..q8; empty cells have no samples).
function heatmapChart(hm, { title = 'Average players online by weekday and hour', tzLabel = 'UTC' } = {}) {
  const labelW = 44, top = 8, cell = (W - labelW - PAD.r) / 24, ch = 24, height = top + ch * 7 + 30;
  const vals = hm.grid.flat().filter((v) => v != null);
  const max = vals.length ? Math.max(...vals) : 0;
  let s = svgOpen(height, title, `Average players online for each weekday and hour (${tzLabel}). Darker means more players.`);
  hm.grid.forEach((row, wd) => {
    const yy = top + wd * ch;
    s += `<text class="tick" x="${labelW - 8}" y="${yy + ch / 2 + 4}" text-anchor="end">${hm.weekdays[wd]}</text>`;
    row.forEach((v, h) => {
      const xx = labelW + h * cell;
      const q = v == null ? 'qn' : 'q' + (max > 0 ? Math.min(8, Math.floor((v / max) * 8.999)) : 0);
      const tip = `${hm.weekdays[wd]} ${String(h).padStart(2, '0')}:00 ${tzLabel}\n${v == null ? 'No samples' : 'Average online: ' + fmt1(v)}`;
      s += `<rect class="cell ${q}" x="${xx + 1}" y="${yy + 1}" width="${cell - 2}" height="${ch - 2}" rx="3" data-tip="${esc(tip)}"/>`;
    });
  });
  for (let h = 0; h < 24; h += 3) s += `<text class="tick" x="${labelW + h * cell + cell / 2}" y="${top + ch * 7 + 18}" text-anchor="middle">${String(h).padStart(2, '0')}</text>`;
  return s + '</svg>';
}

// Horizontal bars, one series (slot 1), value label at the bar end.
function hBars(rows, { title, value, fmt = fmt1, unit = '', labelW = 210 } = {}) {
  if (!rows.length) return emptyChart(title, 80);
  const rowH = 26, top = 6, height = top + rows.length * rowH + 8;
  const x0 = Math.min(labelW, Math.round(W * 0.4)), x1 = W - 60;
  const max = Math.max(...rows.map(value), 0) || 1;
  let s = svgOpen(height, title, '');
  rows.forEach((r, i) => {
    const yy = top + i * rowH;
    const v = value(r);
    const w = Math.max(v > 0 ? 2 : 0, ((x1 - x0) * v) / max);
    s += `<text class="label" x="${x0 - 10}" y="${yy + rowH / 2 + 4}" text-anchor="end">${esc(truncate(r.label || r.name, Math.max(12, Math.floor(x0 / 7))))}</text>`;
    if (w > 0) {
      const r4 = Math.min(4, w / 2);
      const bh = rowH - 10, by = yy + 5;
      s += `<path class="${r.suppressed ? 'muted-fill' : 's1-fill'}" d="M${x0},${by} L${x0 + w - r4},${by} Q${x0 + w},${by} ${x0 + w},${by + r4} L${x0 + w},${by + bh - r4} Q${x0 + w},${by + bh} ${x0 + w - r4},${by + bh} L${x0},${by + bh} Z"/>`;
    }
    s += `<text class="value" x="${x0 + w + 6}" y="${yy + rowH / 2 + 4}">${esc(fmt(v) + unit)}</text>`;
    s += `<rect class="hit" x="0" y="${yy}" width="${W}" height="${rowH}" data-tip="${esc((r.label || r.name) + '\n' + (r.tip || fmt(v) + unit))}"/>`;
  });
  return s + '</svg>';
}

function truncate(s, n) {
  s = String(s);
  return s.length > n ? s.slice(0, n - 1) + '…' : s;
}

function emptyChart(title, height) {
  return svgOpen(height, title, 'No data') + `<text class="label" x="${W / 2}" y="${height / 2}" text-anchor="middle">No data in this window</text></svg>`;
}

// Charts in half-width cards use a narrower viewBox so their text stays at the same on-screen size.
function sized(fn, optIndex) {
  return function (...args) {
    const o = args[optIndex] || {};
    const prev = W;
    W = Math.max(320, Math.min(1200, o.width || 720));
    try { return fn(...args); } finally { W = prev; }
  };
}

module.exports = {
  esc, niceScale, fmtInt, fmt1,
  concurrencyChart: sized(concurrencyChart, 1), dailyActiveChart: sized(dailyActiveChart, 1), hourBars: sized(hourBars, 1),
  heatmapChart: sized(heatmapChart, 1), hBars: sized(hBars, 1),
};
