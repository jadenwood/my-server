// The Realm art pack in the browser, for the Chronicle pages (/overlay, /realm) and the stream scenes.
// The art files are copies of art/ in assets/art (made by portal/scripts/sync-art.mjs, so the pages
// work offline). This module only knows which file belongs to what. chronicle/test checks its tables
// against art/icons/event-map.json, art/palette.json and art/src/titles.json.
//
// sync-art copies this file to streamkit/public/assets/realm-art.js. Edit this one, then run
// node portal/scripts/sync-art.mjs.
//
// No innerHTML: every node is built with createElement / createElementNS.

export const ART_BASE = '/assets/art/';
const NS = 'http://www.w3.org/2000/svg';

// Chronicle event type -> icon in assets/art/icons.svg (art/icons/event-map.json). Each type has its own.
export const EVENT_ICONS = {
  coronation: 'crown', abdication: 'abdication', claim_declared: 'claim', rebellion_started: 'rebellion',
  rebellion_ended: 'sheathed', house_founded: 'house', oath_sworn: 'oath', oath_broken: 'oath-broken',
  treaty_signed: 'treaty', treaty_broken: 'treaty-broken', decree: 'decree', ransom_set: 'ransom',
  ransom_paid: 'coins', released: 'released', contract_posted: 'contract', contract_fulfilled: 'contract-paid',
  contract_ended: 'contract-lapsed', season_started: 'dawn', season_ended: 'laurel', event_started: 'beacon',
  event_ended: 'beacon-out', tournament_champion: 'trophy', hunt_kill: 'hunt', truce_broken: 'dagger',
  law_proclaimed: 'law', law_repealed: 'law-repealed', accusation: 'accuse', trial_by_combat: 'helm',
  verdict: 'scales', pardon: 'pardon', dynasty_founded: 'lineage', heir_named: 'heir', succession: 'succession',
  blood_claim: 'blood-claim', blood_restored: 'sprout', title_bestowed: 'collar', title_earned: 'medal',
  treasury_mint: 'mint', treasury_grant: 'grant', tithe_levied: 'tithe', great_trade: 'purse', rumour: 'whisper',
  holding_taken: 'holding',
  census_taken: 'census',
  vote_held: 'ballot',
  blade_claimed: 'blade',
  blade_lost: 'blade-lost',
};

// The six great houses of Ostreval (art/palette.json). Only these have drawn arms. A house a player
// founds gets its overlay dye and its initial instead, so it never borrows a great house's charge.
export const GREAT_HOUSES = {
  varrow: { name: 'Varrow', sigil: 'Iron Stag', words: 'We Stand Our Ground.', field: '#4a2347', fieldLight: '#674664', fieldDark: '#2e162c', metal: '#9aa0a8' },
  ashgrove: { name: 'Ashgrove', sigil: 'White Oak', words: 'Deep Roots, Long Memory.', field: '#7a3a1a', fieldLight: '#8f5a3f', fieldDark: '#4c2410', metal: '#e8dfc8' },
  corvane: { name: 'Corvane', sigil: 'Black Raven', words: 'Every Secret Has a Price.', field: '#2c3b42', fieldLight: '#4e5a60', fieldDark: '#1b2529', metal: '#c9ced4' },
  dunmere: { name: 'Dunmere', sigil: 'Drowned Bell', words: 'The Tide Returns.', field: '#5a5a22', fieldLight: '#747445', fieldDark: '#383815', metal: '#e0b56a' },
  halloran: { name: 'Halloran', sigil: 'Ember Hound', words: 'Loyal Until the Last Coal.', field: '#3a2a1a', fieldLight: '#5a4c3f', fieldDark: '#241a10', metal: '#e27a2c' },
  merrin: { name: 'Merrin', sigil: 'Silver Eel', words: 'Slip the Net.', field: '#24472d', fieldLight: '#47644f', fieldDark: '#162c1c', metal: '#c9ced4' },
};

// Renown titles with a badge (art/src/titles.json): [id, name, infamous]. A server can add its own
// titles in the RealmRenown config; those have no badge and get the event icon instead.
export const TITLES = [
  ['kingslayer', 'Kingslayer', false], ['usurper', 'Usurper', false], ['kingmaker', 'Kingmaker', false],
  ['unbowed', 'The Unbowed', false], ['shield_of_crown', 'Shield of the Crown', false], ['long_reign', 'The Long Reign', false],
  ['warden_of_roads', 'Warden of Roads', false], ['sellsword', 'Sellsword', false], ['headtaker', 'Headtaker', false],
  ['champion', 'Champion of the Lists', false], ['duelist', 'the Duelist', false], ['ring_champion', 'Champion of the Ring', false],
  ['ring_master', 'Master of the Ring', false], ['ring_victor', 'Victor of the Ring', false], ['guildmaster', 'Guildmaster', false],
  ['master_crafter', 'Master Crafter', false], ['huntsman', "Crown's Huntsman", false], ['hoardfinder', 'Hoardfinder', false],
  ['caravan_warden', 'Caravan Warden', false], ['bane_of_legends', 'Bane of Legends', false], ['renowned', 'the Renowned', false],
  ['legend', 'Legend of Ostreval', false], ['oathbreaker', 'Oathbreaker', true], ['faithless', 'The Faithless', true],
  ['trucebreaker', 'Trucebreaker', true], ['hunted', 'The Hunted', true], ['black_name', 'Black Name', true],
].map(([id, name, infamous]) => ({ id, name, infamous, file: `badges/titles/${id.replace(/_/g, '-')}.svg` }));

const bare = (name) => String(name || '').trim().replace(/^house\s+/i, '').toLowerCase();

// The great-house key ('varrow') for a house name ('Varrow', 'House Varrow'), or null.
export function greatHouse(name) {
  const k = bare(name);
  return Object.prototype.hasOwnProperty.call(GREAT_HOUSES, k) ? k : null;
}

function img(src, cls, alt) {
  const i = document.createElement('img');
  i.src = src;
  i.alt = alt || '';
  i.decoding = 'async';
  i.draggable = false;
  if (cls) i.className = cls;
  return i;
}

// One icon from the sprite. It takes the colour of the text around it (currentColor).
export function artIcon(name, cls = 'icon') {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', cls);
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(NS, 'use');
  use.setAttribute('href', `${ART_BASE}icons.svg#realm-icon-${name}`);
  svg.appendChild(use);
  return svg;
}

export const eventIconName = (type) => EVENT_ICONS[type] || 'decree';
export const eventIcon = (type, cls = 'icon') => artIcon(eventIconName(type), cls);

// A great house's drawn arms: kind 'sigil' (roundel), 'banner' or 'shield'. null for any other house.
export function houseArt(name, kind = 'sigil', cls = '') {
  const k = greatHouse(name);
  if (!k) return null;
  const dir = kind === 'banner' ? 'banners' : kind === 'shield' ? 'shields' : 'sigils';
  const h = GREAT_HOUSES[k];
  const i = img(`${ART_BASE}${dir}/${k}.svg`, cls, `Arms of House ${h.name}, the ${h.sigil}`);
  i.dataset.house = k;
  return i;
}

// "Season 3 begins" -> 3. RealmSeasons names seasons "Season {0}" unless the owner changes the format.
export function seasonNumber(e) {
  const m = /\bseason\s+(\d{1,3})\b/i.exec(String((e && e.title) || ''));
  return m ? Number(m[1]) : null;
}

// Season medals I to IV exist; later seasons reuse them in turn.
export function seasonBadge(n, cls = '') {
  const k = ((Math.max(1, n) - 1) % 4) + 1;
  return img(`${ART_BASE}badges/season-${k}.svg`, cls, `Season ${n}`);
}

// The renown title an event names ("Wren is named Kingslayer"), longest match first. null if none.
export function titleOf(text) {
  const t = String(text || '').toLowerCase();
  let best = null;
  for (const x of TITLES) {
    const n = x.name.toLowerCase();
    if ((t.endsWith(n) || t.includes(` ${n}`)) && (!best || n.length > best.name.length)) best = x;
  }
  return best;
}

export const titleBadge = (title, cls = '') => img(`${ART_BASE}${title.file}`, cls, title.name);

// A badge for an event when there is one: the season medal for season events, the title badge for a
// known renown title. null otherwise (use eventIcon).
export function eventBadge(e, cls = '') {
  if (!e) return null;
  if (e.type === 'season_started' || e.type === 'season_ended') {
    const n = seasonNumber(e);
    return n ? seasonBadge(n, cls) : null;
  }
  if (e.type === 'title_earned' || e.type === 'title_bestowed') {
    const t = titleOf(e.title);
    return t ? titleBadge(t, cls) : null;
  }
  return null;
}
