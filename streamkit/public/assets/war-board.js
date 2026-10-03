// War Board: the houses of Ostreval as a live graph. Liege lines (gold), treaties (green, red when
// broken in the last day) and declared claims (red arrows at the crown) with a side ledger.

import { setupScene, startFeed, loadSchedule, el, clear, dyeFor, monogram, houseLabel, icon, artIcon, healthChip } from './kit.js';
import {
  buildWarBoard, layoutForest, normalizeSchedule, parseWindowsParam, houseIn, splitDuration, formatUtc,
} from './model.js';
import { sigilSvg } from './sigils.js';

const kit = setupScene({ transparent: true, anchor: 'center' });
const root = document.getElementById('scene');
const W = 1340, H = 860, PAD_X = 110, PAD_TOP = 190, PAD_BOTTOM = 150;

let windows = parseWindowsParam(kit.params.get('windows'));
let offsetHours = Number(kit.params.get('offset')) || 0;
const showLedger = kit.params.get('ledger') !== '0';
const showTitle = kit.params.get('title') !== '0';

// ---------- static frame ----------

const header = el('header', { class: 'wb-header' },
  el('div', { class: 'eyebrow' }, 'The War Board of Ostreval'),
  el('div', { class: 'wb-crown' }, artIcon('crown'), el('span', { id: 'wb-king', class: 'gold-text' }, ' ')),
);
const svg = el('svg', { class: 'wb-graph', viewBox: `0 0 ${W} ${H}`, role: 'img', 'aria-label': 'Houses, liege lines, treaties and claims' },
  el('defs', {},
    marker('arrow-gold', '#d6a043'), marker('arrow-blood', '#c7402f')),
  el('g', { class: 'edges' }), el('g', { class: 'nodes' }));
const graphWrap = el('section', { class: 'wb-graph-wrap iron' }, svg,
  el('div', { class: 'wb-legend' },
    legend('liege', 'Sworn to'), legend('treaty', 'Treaty'), legend('broken', 'Broken'), legend('claim', 'Claim on the crown')));
const ledger = el('aside', { class: 'wb-ledger iron' });
root.append(...[showTitle ? header : null, graphWrap, showLedger ? ledger : null].filter(Boolean));
if (!showLedger) root.classList.add('no-ledger');
if (!showTitle) root.classList.add('no-title');
const setHealth = healthChip();

function marker(id, color) {
  return el('marker', { id, viewBox: '0 0 10 10', refX: '8', refY: '5', markerWidth: '7', markerHeight: '7', orient: 'auto-start-reverse' },
    el('path', { d: 'M0 0 L10 5 L0 10 z', fill: color }));
}
function legend(kind, label) {
  return el('span', { class: `lg lg-${kind}` }, el('i'), label);
}

// ---------- render ----------

const nodeEls = new Map();
const seenEdges = new Set();


function render(state, events) {
  const now = kit.now();
  const board = buildWarBoard(state, events, { now, windows: windows.length ? windows : undefined, offsetHours });

  document.documentElement.dataset.ready = '1';
  const focus = kit.houses;
  // House filter: the named houses plus every house they touch.
  let visible = board.nodes;
  if (focus.length) {
    // Neighbours of the named houses only (one step), not neighbours of neighbours.
    const seed = new Set(board.nodes.filter((n) => houseIn(n.name, focus)).map((n) => n.name));
    const near = new Set(seed);
    for (const l of board.lieges) if (seed.has(l.vassal) || seed.has(l.liege)) { near.add(l.vassal); near.add(l.liege); }
    for (const t of board.treaties) if (seed.has(t.a) || seed.has(t.b)) { near.add(t.a); near.add(t.b); }
    for (const c of board.claims) {
      if (seed.has(c.house) && board.crownHouse) near.add(board.crownHouse);
      if (board.crownHouse && seed.has(board.crownHouse)) near.add(c.house);
    }
    visible = board.nodes.filter((n) => near.has(n.name));
  }
  const names = new Set(visible.map((n) => n.name));

  const kingEl = document.getElementById('wb-king');
  if (kingEl) kingEl.textContent = board.king ? `${board.king} · ${houseLabel(board.crownHouse) || 'no house'}` : 'The Old Throne stands empty';

  const pos = layoutForest(visible);
  const count = visible.length;
  const r = count > 16 ? 34 : count > 10 ? 44 : 56;
  const at = (name) => {
    const p = pos[name];
    return { x: PAD_X + p.x * (W - 2 * PAD_X), y: PAD_TOP + p.y * (H - PAD_TOP - PAD_BOTTOM) };
  };

  // nodes (kept between renders so they glide to new places)
  const nodeLayer = svg.querySelector('.nodes');
  for (const [name, g] of nodeEls) if (!names.has(name)) { g.remove(); nodeEls.delete(name); }
  for (const n of visible) {
    let g = nodeEls.get(n.name);
    if (!g) {
      g = el('g', { class: 'node enter' });
      nodeEls.set(n.name, g);
      nodeLayer.appendChild(g);
      requestAnimationFrame(() => requestAnimationFrame(() => g.classList.remove('enter')));
    }
    const p = at(n.name);
    g.style.transform = `translate(${p.x}px, ${p.y}px)`;
    drawNode(g, n, r, focus.length && houseIn(n.name, focus));
  }

  // edges (rebuilt; a new edge draws itself in once)
  const edgeLayer = clear(svg.querySelector('.edges'));
  const add = (id, cls, d, extra = {}) => {
    const fresh = !seenEdges.has(id);
    seenEdges.add(id);
    edgeLayer.appendChild(el('path', { d, class: `edge ${cls}${fresh && lastRenderDone ? ' draw' : ''}`, pathLength: '1', ...extra }));
  };
  for (const l of board.lieges) {
    if (!names.has(l.vassal) || !names.has(l.liege)) continue;
    add(`l:${l.vassal}>${l.liege}`, 'liege', straight(at(l.vassal), at(l.liege), r), { 'marker-end': 'url(#arrow-gold)' });
  }
  board.treaties.forEach((t, i) => {
    if (!names.has(t.a) || !names.has(t.b)) return;
    const d = curve(at(t.a), at(t.b), r, bendFor(at(t.a), at(t.b), i % 2 ? 0.28 : -0.28));
    add(`t:${t.a}|${t.b}|${t.status}`, t.status === 'broken' ? 'broken' : 'treaty', d.path);
    edgeLayer.appendChild(el('g', { class: `edge-badge ${t.status}`, transform: `translate(${d.mid.x} ${d.mid.y})` },
      el('circle', { r: '15' }), el('text', { y: '6', 'text-anchor': 'middle' }, t.status === 'broken' ? '✕' : '✦')));
  });
  for (const c of board.claims) {
    const target = c.againstHouse && names.has(c.againstHouse) ? c.againstHouse : board.crownHouse;
    if (!names.has(c.house) || !target || !names.has(target) || target === c.house) continue;
    const d = curve(at(c.house), at(target), r, bendFor(at(c.house), at(target), 0.38));
    add(`c:${c.house}|${c.status}`, `claim ${c.status}`, d.path, { 'marker-end': 'url(#arrow-blood)' });
    const label = c.status === 'active' ? 'RISING' : c.windowStart ? formatUtc(c.windowStart).replace(' UTC', '') : 'CLAIM';
    edgeLayer.appendChild(el('g', { class: `claim-tag ${c.status}`, transform: `translate(${d.mid.x} ${d.mid.y})` },
      el('rect', { x: String(-label.length * 6.5 - 14), y: '-16', width: String(label.length * 13 + 28), height: '32', rx: '4' }),
      el('text', { y: '6', 'text-anchor': 'middle' }, label)));
  }
  if (!visible.length) {
    edgeLayer.appendChild(el('text', { x: String(W / 2), y: String(H / 2), 'text-anchor': 'middle', class: 'empty' }, 'No houses have risen yet'));
  }

  renderLedger(board, names, now);
  lastRenderDone = true;
}
let lastRenderDone = false;

function drawNode(g, n, r, focused) {
  clear(g);
  g.setAttribute('class', `node${n.crown ? ' crown' : ''}${n.claiming ? ' claiming' : ''}${n.betrayedRecently ? ' betrayed' : ''}${focused ? ' focused' : ''}`);
  const dye = dyeFor(n.name);
  g.appendChild(el('circle', { class: 'halo', r: String(r + 14) }));
  g.appendChild(el('circle', { class: 'disc', r: String(r), style: { fill: dye['--dye'], stroke: dye['--dye-light'] } }));
  g.appendChild(el('circle', { class: 'rim', r: String(r - 6) }));
  const art = sigilSvg(n.sigil, n.name, 'sigil-art');
  // An engraved sigil covers the whole disc; line art sits inside the rim.
  const s = art.dataset.kind === 'art' ? r * 2 + 4 : r * 1.25;
  art.setAttribute('x', String(-s / 2));
  art.setAttribute('y', String(-s / 2));
  art.setAttribute('width', String(s));
  art.setAttribute('height', String(s));
  g.appendChild(art);
  g.appendChild(el('text', { class: 'name', y: String(r + 34), 'text-anchor': 'middle' }, n.name));
  g.appendChild(el('text', { class: 'meta', y: String(r + 58), 'text-anchor': 'middle' },
    [n.sigil, `${n.members} sworn`].filter(Boolean).join(' · ')));
  if (n.crown) {
    g.appendChild(el('path', { class: 'crown-mark', d: 'M-22 0 L-26 -26 L-11 -14 L0 -32 L11 -14 L26 -26 L22 0 Z', transform: `translate(0 ${-r - 10})` }));
  }
  if (n.oathsBroken || n.treatiesBroken) {
    const marks = n.oathsBroken + n.treatiesBroken;
    g.appendChild(el('g', { class: 'breaker', transform: `translate(${r * 0.78} ${-r * 0.78})` },
      el('circle', { r: '15' }), el('text', { y: '6', 'text-anchor': 'middle' }, `${marks}✕`),
      el('title', {}, `Oathbreaker marks: ${n.oathsBroken}, treaty-breaker marks: ${n.treatiesBroken}`)));
  }
}

function straight(a, b, r) {
  const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy) || 1;
  const ux = dx / len, uy = dy / len;
  return `M${a.x + ux * (r + 4)} ${a.y + uy * (r + 4)} L${b.x - ux * (r + 10)} ${b.y - uy * (r + 10)}`;
}

// Arcs between houses on the same row bow away from the graph (up for the top row, down for the
// bottom one) so they do not run through the houses and labels in between; other arcs keep `dflt`.
// The bow is capped so it stays inside the frame.
function bendFor(a, b, dflt) {
  const len = Math.hypot(b.x - a.x, b.y - a.y) || 1;
  if (Math.abs(a.y - b.y) > 1) return dflt;
  const cap = Math.min(Math.abs(dflt), 280 / len);
  const up = a.y < H / 2 + 1;
  // The normal (-dy, dx) points down when b is right of a; flip so the arc bows the chosen way.
  const sign = (b.x > a.x ? 1 : -1) * (up ? -1 : 1);
  return sign * cap;
}

function curve(a, b, r, bend) {
  const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy) || 1;
  const nx = -dy / len, ny = dx / len;
  const c = { x: (a.x + b.x) / 2 + nx * len * bend, y: (a.y + b.y) / 2 + ny * len * bend };
  const trim = (p, q, d) => {
    const vx = q.x - p.x, vy = q.y - p.y, l = Math.hypot(vx, vy) || 1;
    return { x: p.x + (vx / l) * d, y: p.y + (vy / l) * d };
  };
  const s = trim(a, c, r + 4), e = trim(b, c, r + 10);
  // Point on the quadratic at t=0.5.
  const mid = { x: 0.25 * s.x + 0.5 * c.x + 0.25 * e.x, y: 0.25 * s.y + 0.5 * c.y + 0.25 * e.y };
  return { path: `M${s.x} ${s.y} Q${c.x} ${c.y} ${e.x} ${e.y}`, mid };
}

function left(ms) {
  const { d, h, m } = splitDuration(ms);
  return d ? `${d}d ${h}h` : h ? `${h}h ${m}m` : `${m}m`;
}

function renderLedger(board, names, now) {
  if (!showLedger) return;
  clear(ledger);
  const claims = board.claims.filter((c) => names.has(c.house));
  const treaties = board.treaties.filter((t) => names.has(t.a) && names.has(t.b));
  const sworn = board.lieges.filter((l) => names.has(l.vassal) && names.has(l.liege));

  ledger.appendChild(el('h2', {}, artIcon('claim'), 'Claims'));
  if (!claims.length) ledger.appendChild(el('p', { class: 'none' }, 'No house has raised a claim.'));
  for (const c of claims) {
    const when = c.status === 'active'
      ? (c.windowEnd ? `Rising now · ends in ${left(c.windowEnd - now)}` : 'Rising now')
      : c.windowStart ? `Opens ${formatUtc(c.windowStart)} · in ${left(c.windowStart - now)}` : 'Window not yet named';
    ledger.appendChild(el('div', { class: `row claim ${c.status}` },
      el('div', { class: 'who' }, el('span', { class: 'dot', ...dyeFor(c.house) }), houseLabel(c.house),
        el('span', { class: 'arrow' }, '→'), c.against || houseLabel(board.crownHouse) || 'the throne'),
      el('div', { class: 'when' }, when)));
  }

  ledger.appendChild(el('h2', {}, artIcon('treaty'), 'Treaties'));
  if (!treaties.length) ledger.appendChild(el('p', { class: 'none' }, 'No treaty holds.'));
  for (const t of treaties.slice(0, 7)) {
    const when = t.status === 'broken'
      ? `Broken by ${t.breaker || 'one side'}${t.brokenAt ? ` · ${left(now - t.brokenAt)} ago` : ''}`
      : t.expiresAt ? `${left(t.expiresAt - now)} left` : 'In force';
    ledger.appendChild(el('div', { class: `row treaty ${t.status}` },
      el('div', { class: 'who' }, el('span', { class: 'dot', ...dyeFor(t.a) }), t.a, el('span', { class: 'arrow' }, '⟷'),
        el('span', { class: 'dot', ...dyeFor(t.b) }), t.b),
      el('div', { class: 'when' }, when)));
  }

  ledger.appendChild(el('h2', {}, artIcon('oath'), 'Fealty'));
  if (!sworn.length) ledger.appendChild(el('p', { class: 'none' }, 'Every house stands alone.'));
  for (const l of sworn.slice(0, 6)) {
    ledger.appendChild(el('div', { class: 'row liege' },
      el('div', { class: 'who' }, el('span', { class: 'dot', ...dyeFor(l.vassal) }), l.vassal,
        el('span', { class: 'arrow' }, 'kneels to'), el('span', { class: 'dot', ...dyeFor(l.liege) }), l.liege)));
  }
}

// ---------- data ----------

(async () => {
  if (!windows.length) {
    const sched = normalizeSchedule(await loadSchedule());
    if (sched.windows) windows = sched.windows;
    if (!kit.params.has('offset')) offsetHours = sched.offsetHours;
  }
  const feed = startFeed(kit, {
    onSnapshot: ({ state, events }) => render(state, events),
    onState: (state, events) => render(state, events),
    onEvents: (batch, events) => {
      render(feed.state, events);
      // Flash the houses the new events touch.
      for (const e of batch) {
        for (const [name, g] of nodeEls) {
          if (`${e.title} ${e.detail}`.includes(name)) {
            g.classList.remove('flash');
            void g.getBBox();
            g.classList.add('flash');
          }
        }
      }
    },
    onHealth: setHealth,
  });
  // Countdowns in the ledger tick once a minute even when nothing happens.
  if (kit.frozenNow == null) setInterval(() => feed.state && render(feed.state, feed.events), 60 * 1000);
})();

