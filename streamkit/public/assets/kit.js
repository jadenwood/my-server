// Realm Stream Scenes: browser helpers shared by every scene (stage scaling, URL params, polling the
// chronicle API through the streamkit server, banner dye, icons). All text reaches the DOM through
// textContent, never innerHTML, because event titles come from player input.

import { parseKitParams } from './model.js';
import { eventIcon, houseArt } from './realm-art.js';

export { artIcon, eventIcon, eventBadge, houseArt, greatHouse, GREAT_HOUSES } from './realm-art.js';

// ---------- params, motion, stage ----------

export function setupScene({ transparent = true, hold = 10, anchor = 'center' } = {}) {
  const kit = parseKitParams(location.search, { transparent, hold });
  const root = document.documentElement;
  const mq = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  const applyMotion = () => {
    kit.reduced = kit.reducedMotion || !!(mq && mq.matches);
    root.classList.toggle('reduced', kit.reduced);
  };
  applyMotion();
  if (mq && mq.addEventListener) mq.addEventListener('change', applyMotion);
  root.classList.toggle('opaque', !kit.transparent);
  root.style.setProperty('--ui-scale', String(kit.scale));
  root.style.setProperty('--ui-origin', anchor);

  const stage = document.getElementById('stage');
  const fit = () => {
    const k = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
    stage.style.transform = `translate(-50%, -50%) scale(${k})`;
  };
  fit();
  window.addEventListener('resize', fit);

  kit.now = () => (kit.frozenNow != null ? kit.frozenNow : Date.now());
  // Waits that respect reduced motion and the frozen preview clock (screenshots set now=).
  kit.sleep = (ms) => new Promise((ok) => setTimeout(ok, ms));
  return kit;
}

// ---------- API ----------

export async function getJSON(path) {
  const res = await fetch(path, { cache: 'no-store' });
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`);
  return res.json();
}

// Polls /api/state and /api/events?since=<last id>. onSnapshot fires once with the full history,
// onEvents with each batch of new events afterwards, onState whenever the state is re-read.
export function startFeed(kit, { onSnapshot, onEvents, onState, onHealth, stateEvery = 15 }) {
  let lastId = null;
  let events = [];
  let failures = 0;
  let tick = 0;
  let state = null;

  const health = (ok) => {
    failures = ok ? 0 : failures + 1;
    if (onHealth) onHealth(failures < 3);
  };

  async function first() {
    try {
      const [s, e] = await Promise.all([getJSON('/api/state'), getJSON('/api/events?limit=500')]);
      state = s;
      events = Array.isArray(e) ? e : [];
      lastId = events.length ? events[events.length - 1].id : 0;
      health(true);
      onSnapshot && onSnapshot({ state, events });
      return true;
    } catch (err) {
      health(false);
      return false;
    }
  }

  async function poll() {
    tick++;
    try {
      const batch = await getJSON(`/api/events?since=${lastId}&limit=200`);
      let changedState = tick % stateEvery === 0;
      if (Array.isArray(batch) && batch.length) {
        // The chronicle was reset (ids restarted): reload everything.
        if (batch[0].id <= lastId) { await first(); return; }
        events = events.concat(batch).slice(-500);
        lastId = batch[batch.length - 1].id;
        changedState = true;
        onEvents && onEvents(batch, events);
      }
      if (changedState) {
        state = await getJSON('/api/state');
        onState && onState(state, events);
      }
      health(true);
    } catch (err) {
      health(false);
    }
  }

  (async () => {
    while (!(await first())) await kit.sleep(kit.poll * 1000);
    if (kit.frozenNow != null && kit.params.get('live') !== '1') return; // frozen previews do not poll
    const loop = async () => {
      await poll();
      setTimeout(loop, kit.poll * 1000);
    };
    setTimeout(loop, kit.poll * 1000);
  })();

  return { get events() { return events; }, get state() { return state; } };
}

export async function loadSchedule() {
  try {
    return await getJSON('/schedule.json');
  } catch {
    return null; // no schedule file configured: the scenes fall back to the plugin defaults
  }
}

// ---------- DOM ----------

export function el(tag, attrs = {}, ...children) {
  const svgTags = new Set(['svg', 'path', 'g', 'line', 'circle', 'text', 'defs', 'marker', 'rect', 'ellipse', 'title', 'linearGradient', 'radialGradient', 'stop', 'polygon', 'textPath', 'image', 'use', 'clipPath']);
  const node = svgTags.has(tag) ? document.createElementNS('http://www.w3.org/2000/svg', tag) : document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') node.setAttribute('class', v);
    else if (k === 'style') Object.assign(node.style, v);
    else if (k.startsWith('--')) node.style.setProperty(k, v);
    else node.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.appendChild(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
  }
  return node;
}

// Like node.append(), but skips null/false (DOM append would print them as text).
export function put(node, ...children) {
  for (const c of children.flat()) if (c != null && c !== false) node.append(c);
  return node;
}

export function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild);
  return node;
}

// ---------- house colours ----------
// Same palette and hash as chronicle/public/assets/common.js (dyeFor), so a house has one colour on
// every overlay. test/model.test.js fails if the two drift apart.

const DYES = ['#7a1f1c', '#1f2f5c', '#24472d', '#86601a', '#4a2347', '#2c3b42', '#7a3a1a', '#1c4a4a', '#3a2a1a', '#5a5a22'];

function hash(s) {
  let h = 2166136261;
  for (const ch of s.toLowerCase()) h = Math.imul(h ^ ch.codePointAt(0), 16777619);
  return h >>> 0;
}

function shade(hex, amt) {
  const n = parseInt(hex.slice(1), 16);
  const mix = (c) => Math.round(amt >= 0 ? c + (255 - c) * amt : c * (1 + amt));
  const r = mix(n >> 16), g = mix((n >> 8) & 255), b = mix(n & 255);
  return `#${((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1)}`;
}

export function dyeFor(name) {
  const dye = DYES[hash(name || '') % DYES.length];
  return { '--dye': dye, '--dye-light': shade(dye, 0.16), '--dye-dark': shade(dye, -0.38) };
}

export function monogram(name) {
  const m = (name || '?').trim().match(/\p{L}|\p{N}/u);
  return m ? m[0].toUpperCase() : '?';
}

export function houseLabel(name) {
  if (!name) return '';
  return /^house\s/i.test(name) ? name : `House ${name}`;
}

// A great house hangs its drawn banner; any other house the dyed cloth with its initial.
export function banner(house, cls = '') {
  const art = houseArt(house.name, 'banner');
  if (art) return el('div', { class: `banner art ${cls}`.trim(), title: houseLabel(house.name), 'data-house': art.dataset.house }, art);
  return el('div', { class: `banner ${cls}`.trim(), ...dyeFor(house.name), title: houseLabel(house.name) },
    el('div', { class: 'pole' }),
    el('div', { class: 'cloth' },
      el('div', { class: 'monogram' }, monogram(house.name)),
      house.sigil ? el('div', { class: 'sigil-name' }, house.sigil) : null));
}

// ---------- icons (24x24 strokes, drawn for this project) ----------

const ICONS = {
  crown: 'M3 18h18M4 18 3 7l5 4 4-7 4 7 5-4-1 11M12 13.5v.01',
  crownX: 'M3 18h18M4 18 3 7l5 4 4-7 4 7 5-4-1 11M9 21l6-6M15 21l-6-6',
  flag: 'M5 21V3M5 4h11l-2 4 2 4H5',
  swords: 'M4 4l9 9M4 4h3l9 9M4 4v3l9 9M14 17l3 3M17 14l3 3M20 4l-7 7M20 4h-3l-4 4M20 4v3l-4 4M10 14l-3 3M7 20l-3-3',
  sheath: 'M12 2v4M9 6h6M10 6v14l2 2 2-2V6',
  chainX: 'M8 11l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 13l2-2a3.5 3.5 0 0 0-5-5l-2 2M4 4l3 3M20 20l-3-3',
  scrollX: 'M6 4h11a2 2 0 0 1 2 2v12a2 2 0 0 0 2 2H8a2 2 0 0 1-2-2V4zM6 4a2 2 0 0 0-2 2v2h2M10 9l6 5M16 9l-6 5',
  scroll: 'M6 4h11a2 2 0 0 1 2 2v12a2 2 0 0 0 2 2H8a2 2 0 0 1-2-2V4zM6 4a2 2 0 0 0-2 2v2h2M10 9h6M10 13h6',
  seal: 'M12 3a6 6 0 1 0 0 12 6 6 0 0 0 0-12zM12 6.5v5M9.5 9h5M8 14l-2 7 6-3 6 3-2-7',
  hourglass: 'M7 3h10M7 21h10M8 3c0 5 8 5 8 9s-8 4-8 9M16 3c0 5-8 5-8 9s8 4 8 9',
  horn: 'M4 10v4l3 1 11 5V4L7 9zM7 9v6M18 9a3 3 0 0 1 0 6',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM12 8v8M8 12h8',
  raven: 'M3 14c3-1 5-4 9-4 2-2 5-3 8-2l1 1-3 1c-1 3-3 6-7 7l-5 2 1-3z',
};

export function icon(name, cls = 'icon') {
  return el('svg', { viewBox: '0 0 24 24', class: cls, 'aria-hidden': 'true', fill: 'none', stroke: 'currentColor',
    'stroke-width': '1.7', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' },
  el('path', { d: ICONS[name] || ICONS.scroll }));
}

// Event icons come from the art pack (eventIcon, one per Chronicle type).
export const typeIcon = (type, cls = 'icon') => eventIcon(type, cls);

export const TYPE_LABEL = {
  oath_broken: 'Oathbreaker', treaty_broken: 'Treaty Broken', truce_broken: 'Truce Broken',
  claim_declared: 'A Claim on the Crown', rebellion_started: 'Rebellion', rebellion_ended: 'Rebellion Ends',
  abdication: 'The Crown Falls', blood_claim: 'Blood Claim', accusation: 'Accused', trial_by_combat: 'Trial by Combat',
};

// A little "ravens are late" chip, shown only when the API stops answering.
export function healthChip() {
  const chip = el('div', { class: 'health-chip', hidden: true, role: 'status' }, icon('raven'), 'The ravens are late');
  document.body.appendChild(chip);
  return (ok) => { chip.hidden = ok; };
}
