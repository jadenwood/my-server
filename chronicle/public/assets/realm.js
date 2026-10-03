// /realm: crown, houses and the full chronicle, refreshed by polling the local API.
import { metaFor, icon, el, getJSON, banner, houseLabel, reignFor } from './common.js';
import { eventBadge, eventIcon, GREAT_HOUSES, greatHouse, houseArt } from './realm-art.js';

const FILTERS = [
  { id: 'all', label: 'All' },
  { id: 'crown', label: 'Crown & Decrees' },
  { id: 'war', label: 'Claims & Rebellion' },
  { id: 'houses', label: 'Houses & Oaths' },
  { id: 'ransom', label: 'Ransom' },
  { id: 'contracts', label: 'Contracts' },
  { id: 'seasons', label: 'Seasons & Events' },
  { id: 'law', label: 'Law & Dynasty' },
  { id: 'treasury', label: 'Treasury & Rumours' },
];
const POLL_MS = 8000;

const $ = (id) => document.getElementById(id);
let state = null;
let events = [];
let lastId = 0;
let filter = FILTERS.some((f) => f.id === location.hash.slice(1)) ? location.hash.slice(1) : 'all';

const dayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });

function setStatus(text, warn = false) {
  const s = $('status');
  s.textContent = text;
  s.classList.toggle('warn', warn);
}

function renderCrown() {
  const card = document.querySelector('.crown-card');
  const vacant = !state.king;
  card.classList.toggle('vacant', vacant);
  $('king').textContent = vacant ? 'The throne stands empty' : state.king;
  if (vacant) {
    $('reign').textContent = 'No one holds the crown. Who will claim it?';
  } else {
    const parts = [];
    if (state.house) parts.push(`of ${houseLabel(state.house)}`);
    const r = reignFor(state.since);
    if (r) parts.push(`reigning for ${r}`);
    $('reign').textContent = parts.join(', ');
  }
  // The seal shows the ruling great house's sigil, or the crown for any other house.
  const key = vacant ? '' : greatHouse(state.house) || 'crown';
  const seal = $('crown-seal');
  if (seal.dataset.key !== key) {
    seal.dataset.key = key;
    const sigil = key && key !== 'crown' ? houseArt(state.house, 'sigil', 'seal-sigil') : null;
    seal.classList.toggle('art', !!sigil);
    seal.replaceChildren(sigil || eventIcon('coronation'));
    $('crown-words').textContent = sigil ? `“${GREAT_HOUSES[key].words}”` : '';
  }
  $('online').textContent = state.maxPlayers ? `${state.online}/${state.maxPlayers}` : String(state.online);
  $('house-count').textContent = String(state.houses.length);
}

function renderHouses() {
  const wrap = $('houses');
  if (!state.houses.length) {
    wrap.replaceChildren(el('p', { class: 'empty' }, 'No house has yet raised its banner.'));
    return;
  }
  const vassals = new Map();
  for (const h of state.houses) {
    if (h.liege) vassals.set(h.liege, [...(vassals.get(h.liege) || []), h.name]);
  }
  const sorted = [...state.houses].sort((a, b) =>
    (b.name === state.house) - (a.name === state.house) || b.members - a.members || a.name.localeCompare(b.name));
  wrap.replaceChildren(...sorted.map((h) => {
    const royal = h.name === state.house;
    const sworn = vassals.get(h.name) || [];
    return el('article', { class: 'house' + (royal ? ' royal' : '') },
      el('div', { class: 'crest' }, royal ? icon('crown') : null),
      banner(h),
      el('h3', {}, houseLabel(h.name)),
      el('p', {}, `${h.members} sworn ${h.members === 1 ? 'sword' : 'swords'}`),
      h.liege ? el('p', {}, `Kneels to ${houseLabel(h.liege)}`) : null,
      sworn.length ? el('p', { class: 'vassals' }, `Bannermen: ${sworn.join(', ')}`) : null);
  }));
}

function renderFilters() {
  $('filters').replaceChildren(...FILTERS.map((f) => {
    const b = el('button', { type: 'button', 'aria-pressed': String(f.id === filter) }, f.label);
    b.addEventListener('click', () => {
      filter = f.id;
      history.replaceState(null, '', f.id === 'all' ? location.pathname : `#${f.id}`);
      renderFilters();
      renderTimeline();
    });
    return b;
  }));
}

function entry(e, isNew) {
  const m = metaFor(e.type);
  const badge = eventBadge(e, 'entry-badge');
  const d = new Date(e.ts);
  const valid = !Number.isNaN(d.getTime());
  return el('li', { class: 'entry' + (isNew ? ' new' : ''), 'data-tone': m.tone },
    badge ? el('div', { class: 'seal badge' }, badge) : el('div', { class: 'seal' }, eventIcon(e.type)),
    el('div', {},
      el('div', { class: 'label' }, m.label),
      el('h3', {}, e.title),
      e.detail ? el('p', {}, e.detail) : null,
      e.actors.length ? el('p', { class: 'actors' }, e.actors.join(' · ')) : null),
    valid ? el('time', { datetime: e.ts, title: d.toISOString() }, timeFmt.format(d)) : el('span'));
}

function renderTimeline(newIds = new Set()) {
  const list = events.filter((e) => filter === 'all' || metaFor(e.type).group === filter).reverse();
  const ol = $('timeline');
  if (!list.length) {
    ol.replaceChildren(el('li', { class: 'empty' }, events.length ? 'Nothing of that kind has been recorded.' : 'The chronicle is yet unwritten.'));
    return;
  }
  const nodes = [];
  let day = null;
  for (const e of list) {
    const d = new Date(e.ts);
    const label = Number.isNaN(d.getTime()) ? 'Undated' : dayFmt.format(d);
    if (label !== day) {
      day = label;
      nodes.push(el('li', { class: 'day', 'aria-hidden': 'true' }, label));
    }
    nodes.push(entry(e, newIds.has(e.id)));
  }
  ol.replaceChildren(...nodes);
  $('entry-count').textContent = String(events.length);
}

async function refresh(initial) {
  try {
    const [s, fresh] = await Promise.all([
      getJSON('/api/state'),
      getJSON(initial ? '/api/events?limit=500' : `/api/events?since=${lastId}&limit=500`),
    ]);
    state = s;
    renderCrown();
    renderHouses();
    if (initial || fresh.length) {
      events = initial ? fresh : events.concat(fresh);
      if (events.length) lastId = events[events.length - 1].id;
      renderTimeline(initial ? new Set() : new Set(fresh.map((e) => e.id)));
    }
    setStatus(state.stale
      ? 'The ravens are late: the realm has not reported in a while.'
      : `Last word from the realm ${state.updated ? timeFmt.format(new Date(state.updated)) : 'unknown'}.`, state.stale);
  } catch (err) {
    console.warn(err);
    setStatus('The chronicle cannot be reached right now. Retrying…', true);
  }
}

renderFilters();
refresh(true).then(() => setInterval(() => refresh(false), POLL_MS));
