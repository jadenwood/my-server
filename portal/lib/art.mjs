// The Realm art pack in the portal: event icons, great-house arms, season medals and renown title
// badges. The files are copies of art/ in assets/art (portal/scripts/sync-art.mjs), so the site works
// offline and from file://. This module reads the icon sprite, the event map, the palette and the title
// list from there at build time and writes markup that passes the strict CSP (no style attributes, no
// inline script). Icons are inlined as <symbol>s once per page and drawn with <use href="#...">, because
// browsers refuse <use> of an external file opened from file://.

import { readFileSync } from 'node:fs';
import { esc } from './util.mjs';

const ART_DIR = new URL('../assets/art/', import.meta.url);

function readArt(file) {
  try {
    return readFileSync(new URL(file, ART_DIR), 'utf8');
  } catch {
    return null;
  }
}
function readArtJson(file, fallback) {
  try {
    return JSON.parse(readArt(file));
  } catch {
    return fallback;
  }
}

// Chronicle event type -> icon name (art/icons/event-map.json).
export const EVENT_ICONS = readArtJson('event-map.json', {}).icons || {};

// Icon name -> its <symbol> markup from the sprite (titles dropped: the page labels every icon in text).
export const SYMBOLS = new Map();
for (const m of (readArt('icons.svg') || '').matchAll(/<symbol id="realm-icon-([a-z0-9-]+)"([^>]*)>([\s\S]*?)<\/symbol>/g)) {
  SYMBOLS.set(m[1], `<symbol id="realm-icon-${m[1]}"${m[2]}>${m[3].replace(/<title>[\s\S]*?<\/title>/, '')}</symbol>`);
}

// The six great houses of Ostreval (art/palette.json). Only they have drawn arms; a house a player
// founds keeps its dyed banner with its initial, so it never borrows a great house's charge.
export const GREAT_HOUSES = readArtJson('palette.json', {}).houses || {};

// Renown titles with a badge (art/src/titles.json). Titles an owner adds in the plugin config have none.
export const TITLES = (readArtJson('titles.json', {}).titles || [])
  .map((t) => ({ id: t.id, name: t.name, infamous: !!t.infamous, file: `badges/titles/${t.id.replace(/_/g, '-')}.svg` }));

const bare = (name) => String(name || '').trim().replace(/^house\s+/i, '').toLowerCase();

export function greatHouse(name) {
  const k = bare(name);
  return Object.prototype.hasOwnProperty.call(GREAT_HOUSES, k) ? k : null;
}

export const eventIconName = (type) => (SYMBOLS.has(EVENT_ICONS[type]) ? EVENT_ICONS[type] : 'decree');

// One icon from the sprite; it takes the colour of the text around it.
export function artIcon(name, cls = 'icon') {
  return `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><use href="#realm-icon-${esc(name)}"/></svg>`;
}

export const eventIcon = (type, cls = 'icon') => artIcon(eventIconName(type), cls);

// The <symbol>s a page uses, as one hidden inline sprite. Call with the finished page body.
export function spriteFor(html) {
  const used = new Set([...String(html).matchAll(/href="#realm-icon-([a-z0-9-]+)"/g)].map((m) => m[1]));
  const symbols = [...used].filter((n) => SYMBOLS.has(n)).sort().map((n) => SYMBOLS.get(n));
  return symbols.length ? `<svg class="sprite" aria-hidden="true" focusable="false"><defs>${symbols.join('')}</defs></svg>` : '';
}

const img = (src, cls, alt, w, h) => `<img class="${cls}" src="${src}" alt="${esc(alt)}" width="${w}" height="${h}" loading="lazy" decoding="async">`;

// A great house's arms: kind 'sigil' (256 roundel), 'banner' (200 x 340) or 'shield' (240 x 280).
// Returns '' for any other house.
export function houseArt(name, kind = 'sigil', up = '', cls = '') {
  const k = greatHouse(name);
  if (!k) return '';
  const h = GREAT_HOUSES[k];
  const [dir, w, ht] = kind === 'banner' ? ['banners', 200, 340] : kind === 'shield' ? ['shields', 240, 280] : ['sigils', 256, 256];
  return img(`${up}assets/art/${dir}/${k}.svg`, `art-${kind}${cls ? ` ${cls}` : ''}`, `Arms of House ${h.name}, the ${h.sigil}`, w, ht);
}

// Season medals I to IV exist; later seasons reuse them in turn.
export function seasonBadge(n, up = '', cls = 'season-medal') {
  if (!Number.isInteger(n) || n < 1) return '';
  return img(`${up}assets/art/badges/season-${((n - 1) % 4) + 1}.svg`, cls, `Season ${n}`, 256, 256);
}

// The renown title an event names ("Wren is named Kingslayer"), the longest match. null if none.
export function titleOf(text) {
  const t = String(text || '').toLowerCase();
  let best = null;
  for (const x of TITLES) {
    const n = x.name.toLowerCase();
    if ((t.endsWith(n) || t.includes(` ${n}`)) && (!best || n.length > best.name.length)) best = x;
  }
  return best;
}

export const titleBadge = (t, up = '', cls = 'title-badge') => img(`${up}assets/art/${t.file}`, cls, t.name, 256, 256);
