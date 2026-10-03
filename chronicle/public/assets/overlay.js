// OBS overlay: crown plate, house banners, chronicle ticker and animated proclamations. A coronation
// or a rebellion takes the centre of the screen for a few seconds (a "moment").
// Query options: banners=<n> (0 hides), ticker=0, crown=0, proclaim=0, moments=0, replay=1 (proclaim the
// latest event on load; replay=<type>, e.g. replay=coronation, proclaims the latest event of that type), hold=<seconds>, poll=<seconds>, motion=0 (no decorative motion), bg=1 (preview backdrop).
import { metaFor, icon, el, getJSON, banner, houseLabel, reignFor, relTime, params } from './common.js';
import { artIcon, eventBadge, eventIcon, GREAT_HOUSES, greatHouse, houseArt } from './realm-art.js';

const opt = {
  banners: clampInt(params.get('banners'), 0, 8, 6),
  ticker: params.get('ticker') !== '0',
  crown: params.get('crown') !== '0',
  proclaim: params.get('proclaim') !== '0',
  replay: params.get('replay') || '',
  moments: params.get('moments') !== '0',
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

  // The medallion carries the ruling great house's sigil; any other house (or none) gets the crown.
  const key = vacant ? '' : greatHouse(state.house) || 'crown';
  const medal = $('medallion');
  if (medal.dataset.key !== key) {
    medal.dataset.key = key;
    const sigil = key && key !== 'crown' ? houseArt(state.house, 'sigil', 'medal-sigil') : null;
    medal.classList.toggle('art', !!sigil);
    medal.replaceChildren(...(sigil ? [sigil, el('span', { class: 'medal-crown' }, eventIcon('coronation'))] : [eventIcon('coronation')]));
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
    el('span', { class: 't-icon' }, eventIcon(e.type)),
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
  if (opt.moments && MOMENTS[e.type]) { showMoment(e); return; }
  const m = metaFor(e.type);
  const badge = eventBadge(e, 'seal-badge');
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
        el('div', { class: 'seal' + (badge ? ' has-badge' : '') }, badge || eventIcon(e.type)),
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

// ---------- moments: coronation and rebellion ----------

// The houses an event names, longest name first, from the houses the realm knows ("House Varrow rises").
function housesIn(e) {
  const text = `${e.title} ${e.detail}`;
  const names = ((state && state.houses) || []).map((h) => h.name).sort((a, b) => b.length - a.length);
  const found = [];
  for (const n of names) {
    const bareName = n.replace(/^house\s+/i, '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    if (new RegExp(`\\b${bareName}\\b`, 'i').test(text) && !found.includes(n)) found.push(n);
  }
  return found;
}

function sparks(n, cls) {
  const box = el('div', { class: cls });
  for (let i = 0; i < n; i++) {
    const a = (i / n) * Math.PI * 2 + Math.random() * 0.4;
    const r = 220 + Math.random() * 320;
    const s = el('span');
    s.style.setProperty('--x', `${Math.round(Math.cos(a) * r)}px`);
    s.style.setProperty('--y', `${Math.round(Math.sin(a) * r * 0.62)}px`);
    s.style.animationDelay = `${(Math.random() * 0.5).toFixed(2)}s`;
    box.appendChild(s);
  }
  return box;
}

// A coronation: the new monarch's banner drops behind a descending crown, rays and sparks.
function coronation(e) {
  // CrownAndConsequences writes "<king> of <house> takes the throne"; RealmChronicle's own fallback writes
  // "<king> takes the throne" with "House <house> now holds the crown." as the detail.
  const full = /^(.+) of (.+?) takes the throne/i.exec(e.title);
  const short = /^(.+?) takes the throne/i.exec(e.title);
  const held = /House (.+?) now holds the crown/i.exec(e.detail || '');
  const king = full ? full[1] : short ? short[1] : (state && state.king) || e.title;
  let house = full ? full[2] : held ? held[1] : (state && state.house) || housesIn(e)[0] || null;
  if (house && /^no house$/i.test(house)) house = null;
  const g = greatHouse(house);
  const crest = houseArt(house, 'banner', 'm-banner') || (house ? banner({ name: house }, { cls: 'm-banner' }) : null);
  return el('article', { class: 'moment coronation', 'data-house': g || '' },
    el('div', { class: 'm-veil' }),
    el('div', { class: 'm-rays' }),
    sparks(36, 'm-sparks'),
    el('div', { class: 'm-crest' }, crest, el('div', { class: 'm-crown' }, artIcon('crown', 'icon'))),
    el('div', { class: 'm-text' },
      el('div', { class: 'm-kicker' }, 'Long live the crown'),
      el('div', { class: 'm-title gold-text' }, king),
      house ? el('div', { class: 'm-sub' }, `of ${houseLabel(house)}${g ? ` · the ${GREAT_HOUSES[g].sigil}` : ''}`) : null,
      g ? el('div', { class: 'm-words' }, `“${GREAT_HOUSES[g].words}”`) : (e.detail ? el('div', { class: 'm-words' }, e.detail) : null)));
}

// A rebellion: blood at the edges, the rebel house's shield and the rebellion mark slam in.
function rebellion(e) {
  const named = /^House (.+?) rises/i.exec(e.title);
  const house = named ? named[1] : housesIn(e)[0] || null;
  const shield = houseArt(house, 'shield', 'm-shield');
  return el('article', { class: 'moment rebellion' },
    el('div', { class: 'm-veil' }),
    el('div', { class: 'm-slash' }),
    sparks(24, 'm-sparks'),
    el('div', { class: 'm-crest' },
      shield,
      el('div', { class: 'm-mark' + (shield ? ' small' : '') }, eventIcon('rebellion_started'))),
    el('div', { class: 'm-text' },
      el('div', { class: 'm-kicker' }, 'Rebellion'),
      el('div', { class: 'm-title' }, e.title),
      e.detail ? el('div', { class: 'm-words' }, e.detail) : null));
}

const MOMENTS = { coronation, rebellion_started: rebellion };
const MOMENT_MS = { coronation: 9500, rebellion_started: 8000 };

function showMoment(e) {
  const card = MOMENTS[e.type](e);
  $('moment').replaceChildren(card);
  setTimeout(() => {
    card.classList.add('leaving');
    setTimeout(() => { card.remove(); nextProclamation(); }, 900);
  }, Math.max(MOMENT_MS[e.type], queue.length ? 0 : opt.hold - 2000));
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
      if (opt.replay === '1' && events.length) proclaim(events[events.length - 1]);
      else if (/^[a-z_]{2,32}$/.test(opt.replay)) {
        const all = await getJSON('/api/events?limit=500');
        const last = all.filter((e) => e.type === opt.replay).pop();
        if (last) proclaim(last);
      }
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
  if (params.get('motion') === '0') document.body.classList.add('reduced');
  if (!opt.crown) $('crown').classList.add('hidden');
  if (!opt.banners) $('banners').classList.add('hidden');
  if (!opt.ticker) $('ticker').classList.add('hidden');
  $('ticker-icon').replaceWith(artIcon('decree'));
  fitStage();
  addEventListener('resize', fitStage);

  pollState();
  pollEvents(true).then(() => setInterval(() => pollEvents(false), opt.poll));
  setInterval(pollState, Math.max(opt.poll, 5000));
  // Keep the "reigning" duration and relative times ticking without waiting for new data.
  setInterval(() => { if (state && opt.crown) renderCrown(state.king); }, 60000);
}

init();
