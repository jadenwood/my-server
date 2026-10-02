// Shared helpers for /overlay and /realm. All text goes into the DOM via textContent, never innerHTML.

export const TYPE_META = {
  coronation:        { label: 'Coronation',        icon: 'crown',   tone: 'gold',  group: 'crown' },
  abdication:        { label: 'The Crown Falls',   icon: 'crownX',  tone: 'blood', group: 'crown' },
  claim_declared:    { label: 'A Claim',           icon: 'flag',    tone: 'blood', group: 'war' },
  rebellion_started: { label: 'Rebellion',         icon: 'swords',  tone: 'blood', group: 'war' },
  rebellion_ended:   { label: 'Rebellion Ends',    icon: 'sheath',  tone: 'iron',  group: 'war' },
  house_founded:     { label: 'A House Rises',     icon: 'shield',  tone: 'gold',  group: 'houses' },
  oath_sworn:        { label: 'Oath Sworn',        icon: 'oath',    tone: 'gold',  group: 'houses' },
  oath_broken:       { label: 'Oathbreaker',       icon: 'chainX',  tone: 'blood', group: 'houses' },
  treaty_signed:     { label: 'Treaty',            icon: 'seal',    tone: 'moss',  group: 'houses' },
  treaty_broken:     { label: 'Treaty Broken',     icon: 'scrollX', tone: 'blood', group: 'houses' },
  decree:            { label: 'Royal Decree',      icon: 'scroll',  tone: 'gold',  group: 'crown' },
  ransom_set:        { label: 'Ransom',            icon: 'chain',   tone: 'blood', group: 'ransom' },
  ransom_paid:       { label: 'Ransom Paid',       icon: 'coins',   tone: 'gold',  group: 'ransom' },
  released:          { label: 'Set Free',          icon: 'chainX',  tone: 'moss',  group: 'ransom' },
  contract_posted:    { label: 'A Price Is Set',   icon: 'scroll',  tone: 'iron',  group: 'contracts' },
  contract_fulfilled: { label: 'Contract Paid',    icon: 'coins',   tone: 'gold',  group: 'contracts' },
  contract_ended:     { label: 'Contract Lapses',  icon: 'scrollX', tone: 'iron',  group: 'contracts' },
  // RealmSeasons and RealmEvents
  season_started:      { label: 'A New Season',     icon: 'hourglass', tone: 'gold',  group: 'seasons' },
  season_ended:        { label: 'Season\'s End',    icon: 'trophy',    tone: 'gold',  group: 'seasons' },
  event_started:       { label: 'Realm Event',      icon: 'horn',      tone: 'iron',  group: 'seasons' },
  event_ended:         { label: 'Event Ends',       icon: 'horn',      tone: 'iron',  group: 'seasons' },
  tournament_champion: { label: 'Tournament Champion', icon: 'trophy', tone: 'gold',  group: 'seasons' },
  hunt_kill:           { label: 'The King\'s Hunt',  icon: 'swords',    tone: 'blood', group: 'seasons' },
  truce_broken:        { label: 'Truce Broken',     icon: 'scrollX',   tone: 'blood', group: 'seasons' },
  // RealmLaws, RealmDynasties, RealmRenown, RealmTreasury, RealmRavens (plugins/docs/*/EVENTS.json)
  law_proclaimed:      { label: 'A Law Proclaimed',        icon: 'scroll',  tone: 'gold',  group: 'law' },
  law_repealed:        { label: 'Law Repealed',            icon: 'scrollX', tone: 'iron',  group: 'law' },
  accusation:          { label: 'Accused',                 icon: 'flag',    tone: 'blood', group: 'law' },
  trial_by_combat:     { label: 'Trial by Combat',         icon: 'swords',  tone: 'blood', group: 'law' },
  verdict:             { label: 'Verdict',                 icon: 'seal',    tone: 'iron',  group: 'law' },
  pardon:              { label: 'Pardoned',                icon: 'chainX',  tone: 'moss',  group: 'law' },
  dynasty_founded:     { label: 'A Line Is Founded',       icon: 'people',  tone: 'gold',  group: 'law' },
  heir_named:          { label: 'An Heir Is Named',        icon: 'oath',    tone: 'gold',  group: 'law' },
  succession:          { label: 'Succession',              icon: 'scroll',  tone: 'gold',  group: 'law' },
  blood_claim:         { label: 'Blood Claim',             icon: 'flag',    tone: 'blood', group: 'law' },
  blood_restored:      { label: 'The Line Restored',       icon: 'crown',   tone: 'gold',  group: 'law' },
  title_bestowed:      { label: 'Title Bestowed',          icon: 'seal',    tone: 'gold',  group: 'law' },
  title_earned:        { label: 'A Title Earned',          icon: 'seal',    tone: 'gold',  group: 'law' },
  treasury_mint:       { label: 'The Crown Strikes Coin',  icon: 'coins',   tone: 'gold',  group: 'treasury' },
  treasury_grant:      { label: 'Royal Largesse',          icon: 'coins',   tone: 'gold',  group: 'treasury' },
  tithe_levied:        { label: 'The Tithe Gathered',      icon: 'scroll',  tone: 'iron',  group: 'treasury' },
  great_trade:         { label: 'A Great Sale',            icon: 'seal',    tone: 'gold',  group: 'treasury' },
  rumour:              { label: 'A Rumour Spreads',        icon: 'scroll',  tone: 'iron',  group: 'treasury' },
};

export const metaFor = (type) => TYPE_META[type] || { label: 'Chronicle', icon: 'scroll', tone: 'iron', group: 'other' };

// 24x24 stroke icons drawn for this project.
const ICON_PATHS = {
  crown: 'M3 18h18M4 18 3 7l5 4 4-7 4 7 5-4-1 11M12 13.5v.01',
  crownX: 'M3 18h18M4 18 3 7l5 4 4-7 4 7 5-4-1 11M9 21l6-6M15 21l-6-6',
  flag: 'M5 21V3M5 4h11l-2 4 2 4H5',
  swords: 'M4 4l9 9M4 4h3l9 9M4 4v3l9 9M14 17l3 3M17 14l3 3M20 4l-7 7M20 4h-3l-4 4M20 4v3l-4 4M10 14l-3 3M7 20l-3-3',
  sheath: 'M12 2v4M9 6h6M10 6v14l2 2 2-2V6',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM12 8v8M8 12h8',
  oath: 'M7 11V6a2 2 0 0 1 4 0v5M11 10V4a2 2 0 0 1 4 0v7M15 9a2 2 0 0 1 4 0v4c0 4-3 8-7 8s-6-2-7-5l-2-4a2 2 0 0 1 3-2l2 2',
  chain: 'M9 15l6-6M8 11l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 13l2-2a3.5 3.5 0 0 0-5-5l-2 2',
  chainX: 'M8 11l-2 2a3.5 3.5 0 0 0 5 5l2-2M16 13l2-2a3.5 3.5 0 0 0-5-5l-2 2M4 4l3 3M20 20l-3-3',
  seal: 'M12 3a6 6 0 1 0 0 12 6 6 0 0 0 0-12zM12 6.5v5M9.5 9h5M8 14l-2 7 6-3 6 3-2-7',
  scroll: 'M6 4h11a2 2 0 0 1 2 2v12a2 2 0 0 0 2 2H8a2 2 0 0 1-2-2V4zM6 4a2 2 0 0 0-2 2v2h2M10 9h6M10 13h6',
  scrollX: 'M6 4h11a2 2 0 0 1 2 2v12a2 2 0 0 0 2 2H8a2 2 0 0 1-2-2V4zM6 4a2 2 0 0 0-2 2v2h2M10 9l6 5M16 9l-6 5',
  coins: 'M8 7a5 2 0 1 0 10 0 5 2 0 1 0-10 0M8 7v4c0 1.1 2.2 2 5 2s5-.9 5-2V7M6 12a5 2 0 1 0 10 0M6 12v4c0 1.1 2.2 2 5 2s5-.9 5-2v-3',
  people: 'M9 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM3 20c0-3 2.7-5 6-5s6 2 6 5M16 5a3 3 0 0 1 0 6M21 20c0-2.5-1.7-4.3-4-4.8',
  trophy: 'M8 4h8v5a4 4 0 0 1-8 0zM8 6H5a3 3 0 0 0 3 4M16 6h3a3 3 0 0 1-3 4M12 13v4M8 21h8M9 17h6',
  hourglass: 'M7 3h10M7 21h10M8 3c0 5 8 5 8 9s-8 4-8 9M16 3c0 5-8 5-8 9s8 4 8 9',
  horn: 'M4 10v4l3 1 11 5V4L7 9zM7 9v6M18 9a3 3 0 0 1 0 6',
};

export function icon(name, cls = 'icon') {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.7');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  const path = document.createElementNS(ns, 'path');
  path.setAttribute('d', ICON_PATHS[name] || ICON_PATHS.scroll);
  svg.appendChild(path);
  return svg;
}

// Tiny element builder: el('div', {class: 'x'}, 'text', child)
export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === 'class') node.className = v;
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

export async function getJSON(path) {
  const res = await fetch(path, { cache: 'no-store' });
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`);
  return res.json();
}

// Dyed-cloth palette for banners; a house keeps the same colour via a stable hash of its name.
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

export function banner(house, opts = {}) {
  const b = el('div', { class: 'banner' + (opts.cls ? ' ' + opts.cls : ''), ...dyeFor(house.name) },
    el('div', { class: 'pole' }),
    el('div', { class: 'cloth' },
      el('div', { class: 'monogram' }, el('span', { class: 'gold-text' }, monogram(house.name))),
      house.sigil ? el('div', { class: 'sigil' }, house.sigil) : null));
  b.title = `House ${house.name}` + (house.sigil ? ` (${house.sigil})` : '');
  return b;
}

export function houseLabel(name) {
  if (!name) return '';
  return /^house\s/i.test(name) ? name : `House ${name}`;
}

export function reignFor(sinceIso, now = Date.now()) {
  const t = Date.parse(sinceIso);
  if (Number.isNaN(t)) return '';
  let s = Math.max(0, Math.floor((now - t) / 1000));
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

export function relTime(iso, now = Date.now()) {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const s = Math.round((now - t) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return `${Math.floor(s / 86400)}d ago`;
}

export const params = new URLSearchParams(location.search);
