// OBS overlay: crown plate, house banners, chronicle ticker and animated proclamations.
// Query options: banners=<n> (0 hides), ticker=0, crown=0, proclaim=0, replay=1 (proclaim the latest
// event on load), hold=<seconds>, poll=<seconds>, bg=1 (preview backdrop).
import { metaFor, icon, el, getJSON, banner, houseLabel, reignFor, relTime, params } from './common.js';

const opt = {
  banners: clampInt(params.get('banners'), 0, 8, 6),
  ticker: params.get('ticker') !== '0',
  crown: params.get('crown') !== '0',
  proclaim: params.get('proclaim') !== '0',
  replay: params.get('replay') === '1',
  hold: clampInt(params.get('hold'), 4, 60, 12) * 1000,
  poll: clampInt(params.get('poll'), 2, 60, 3) * 1000,
  tickerCount: 12,
};

const $ = (id) => document.getElementById(id);
const stage = $('stage');
let state = null;
let events = [];
let lastId = 0;
let lastOkAt = Date.now();
const queue = [];
let showing = false;

function clampInt(v, lo, hi, def) {
  const n = parseInt(v ?? '', 10);
  return Number.isInteger(n) ? Math.min(hi, Math.max(lo, n)) : def;
}

function fitStage() {
  const s = Math.min(innerWidth / 1920, innerHeight / 1080);
  stage.style.transform = `scale(${s})`;
}

// ---------- crown ----------

function renderCrown(prevKing) {
  const plate = $('crown');
  const vacant = !state.king;
  plate.classList.toggle('vacant', vacant);
  $('king').textContent = vacant ? 'The Throne Stands Empty' : state.king;
  if (vacant) {
    $('reign').textContent = 'Who will dare to claim it?';
  } else {
    const parts = [];
    if (state.house) parts.push(`of ${houseLabel(state.house)}`);
    const r = reignFor(state.since);
    if (r) parts.push(`reigning ${r}`);
    $('reign').textContent = parts.join(' · ') || 'Long may the crown endure';
  }

  const souls = $('souls');
  souls.replaceChildren(icon('people'),
    el('span', {}, el('b', {}, String(state.online)), state.maxPlayers ? ` / ${state.maxPlayers}` : '', ' in the realm'));

  const late = state.stale || Date.now() - lastOkAt > 30000;
  $('ravens').hidden = !late;

  if (prevKing !== undefined && prevKing !== state.king) {
    plate.classList.remove('changed');
    void plate.offsetWidth; // restart the animation
    plate.classList.add('changed');
  }
}

// ---------- banners ----------

function renderBanners() {
  const wrap = $('banners');
  const houses = [...(state.houses || [])].sort((a, b) =>
    (b.name === state.house) - (a.name === state.house) || b.members - a.members || a.name.localeCompare(b.name));
  const shown = houses.slice(0, opt.banners);
  const key = JSON.stringify([shown, state.house]);
  if (wrap.dataset.key === key) return;
  wrap.dataset.key = key;

  wrap.replaceChildren(...shown.map((h, i) => {
    const royal = h.name === state.house;
    const slot = el('div', { class: 'banner-slot' + (royal ? ' royal' : '') },
      el('div', { class: 'crest' }, royal ? icon('crown') : null),
      banner(h),
      el('div', { class: 'banner-name' }, h.name),
      el('div', { class: 'banner-sub' },
        h.liege ? `sworn to ${h.liege}` : `${h.members} ${h.members === 1 ? 'sword' : 'swords'}`));
    slot.style.animationDelay = `${i * 0.12}s`;
    return slot;
  }));
}

// ---------- ticker ----------

function tickItem(e, fresh) {
  const m = metaFor(e.type);
  return el('span', { class: 'tick' + (fresh ? ' fresh' : ''), 'data-tone': m.tone },
    el('span', { class: 't-icon' }, icon(m.icon)),
    el('span', { class: 't-label' }, m.label),
    el('span', { class: 't-title' }, e.title),
    el('span', { class: 't-when' }, relTime(e.ts)));
}

function renderTicker(freshIds = new Set()) {
  const run = $('ticker-run');
  const latest = events.slice(-opt.tickerCount).reverse();
  if (!latest.length) {
    run.replaceChildren(el('span', { class: 'ticker-empty' }, 'The chronicle awaits its first entry…'));
    run.style.animation = 'none';
    return;
  }
  // Fill one copy at least as wide as the track, then duplicate it so translateX(-50%) loops seamlessly.
  const trackW = run.parentElement.clientWidth || 1500;
  const copy = document.createDocumentFragment();
  run.replaceChildren();
  let guard = 0;
  do {
    for (const e of latest) run.appendChild(tickItem(e, freshIds.has(e.id)));
  } while (run.scrollWidth < trackW && ++guard < 8);
  for (const n of [...run.children]) copy.appendChild(n.cloneNode(true));
  run.appendChild(copy);
  const pxPerSec = 90;
  run.style.setProperty('--dur', `${Math.max(20, run.scrollWidth / 2 / pxPerSec)}s`);
  run.style.animation = 'none';
  void run.offsetWidth;
  run.style.animation = '';
}

// ---------- proclamations ----------

function proclaim(e) {
  if (!opt.proclaim) return;
  queue.push(e);
  if (!showing) nextProclamation();
}

function nextProclamation() {
  const e = queue.shift();
  if (!e) { showing = false; return; }
  showing = true;
  const m = metaFor(e.type);
  const embers = el('div', { class: 'embers' });
  for (let i = 0; i < 18; i++) {
    const s = el('span', { class: 'ember' });
    s.style.left = `${5 + Math.random() * 90}%`;
    s.style.animationDelay = `${0.6 + Math.random() * 1.6}s`;
    s.style.setProperty('--dx', `${Math.round((Math.random() - 0.5) * 80)}px`);
    embers.appendChild(s);
  }
  const card = el('article', { class: 'proclaim', 'data-tone': m.tone },
    el('div', { class: 'rod' }),
    el('div', { class: 'sheet parchment' },
      el('div', { class: 'sheet-inner' },
        el('div', { class: 'seal' }, icon(m.icon)),
        el('div', { class: 'p-body' },
          el('div', { class: 'p-label' }, m.label),
          el('div', { class: 'p-title' }, e.title),
          e.detail ? el('div', { class: 'p-detail' }, e.detail) : null,
          e.actors.length ? el('div', { class: 'p-actors' }, '— ' + e.actors.join(' · ')) : null))),
    el('div', { class: 'rod' }),
    embers);
  $('proclamations').replaceChildren(card);

  setTimeout(() => {
    card.classList.add('leaving');
    setTimeout(() => { card.remove(); nextProclamation(); }, 850);
  }, queue.length ? Math.min(opt.hold, 7000) : opt.hold);
}

// ---------- polling ----------

async function pollState() {
  try {
    const prevKing = state ? state.king : undefined;
    state = await getJSON('/api/state');
    lastOkAt = Date.now();
    if (opt.crown) renderCrown(prevKing);
    if (opt.banners) renderBanners();
  } catch (err) {
    console.warn(err);
    if (state && opt.crown) renderCrown(state.king);
  }
}

async function pollEvents(initial) {
  try {
    const url = initial ? `/api/events?limit=${opt.tickerCount}` : `/api/events?since=${lastId}&limit=50`;
    const fresh = await getJSON(url);
    lastOkAt = Date.now();
    if (initial) {
      events = fresh;
      lastId = events.length ? events[events.length - 1].id : 0;
      if (opt.ticker) renderTicker();
      if (opt.replay && events.length) proclaim(events[events.length - 1]);
      return;
    }
    if (!fresh.length) return;
    events = events.concat(fresh).slice(-100);
    lastId = fresh[fresh.length - 1].id;
    if (opt.ticker) renderTicker(new Set(fresh.map((e) => e.id)));
    fresh.forEach(proclaim);
  } catch (err) {
    console.warn(err);
  }
}

function init() {
  if (params.get('bg') === '1') document.body.classList.add('preview-bg');
  if (!opt.crown) $('crown').classList.add('hidden');
  if (!opt.banners) $('banners').classList.add('hidden');
  if (!opt.ticker) $('ticker').classList.add('hidden');
  $('crown-icon').replaceWith(icon('crown'));
  $('ticker-icon').replaceWith(icon('scroll'));
  fitStage();
  addEventListener('resize', fitStage);

  pollState();
  pollEvents(true).then(() => setInterval(() => pollEvents(false), opt.poll));
  setInterval(pollState, Math.max(opt.poll, 5000));
  // Keep the "reigning" duration and relative times ticking without waiting for new data.
  setInterval(() => { if (state && opt.crown) renderCrown(state.king); }, 60000);
}

init();
