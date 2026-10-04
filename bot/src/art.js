// Pictures from the Realm art pack on the bot's embeds: the realm emblem as the author icon and a
// thumbnail that fits the reply (the ruling house's sigil, the newest event's icon, a renown title's
// badge). The PNGs come from art/png in this repository and are attached to the message, then shown
// with attachment://<name>, so nothing has to be hosted anywhere. A missing file simply means no picture.
//
// REALM_EMBED_ART=0 turns pictures off; REALM_ART_DIR points at another copy of art/png.

import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { clampEmbed } from './text.js';

// Chronicle event type -> icon (art/icons/event-map.json; test/art.test.js keeps the two in step).
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
  blade_claimed: 'blade', blade_lost: 'blade-lost',
};

// The six great houses have drawn sigils. A house a player founds has none (it keeps its banner colour).
export const GREAT_HOUSES = ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin'];

// Renown titles with a badge (art/src/titles.json): [id, name].
export const TITLES = [
  ['kingslayer', 'Kingslayer'], ['usurper', 'Usurper'], ['kingmaker', 'Kingmaker'], ['unbowed', 'The Unbowed'],
  ['shield_of_crown', 'Shield of the Crown'], ['long_reign', 'The Long Reign'], ['warden_of_roads', 'Warden of Roads'], ['sellsword', 'Sellsword'],
  ['headtaker', 'Headtaker'], ['champion', 'Champion of the Lists'], ['duelist', 'the Duelist'], ['ring_champion', 'Champion of the Ring'],
  ['ring_master', 'Master of the Ring'], ['ring_victor', 'Victor of the Ring'], ['guildmaster', 'Guildmaster'], ['master_crafter', 'Master Crafter'],
  ['huntsman', "Crown's Huntsman"], ['hoardfinder', 'Hoardfinder'], ['caravan_warden', 'Caravan Warden'], ['bane_of_legends', 'Bane of Legends'],
  ['renowned', 'the Renowned'], ['legend', 'Legend of Ostreval'], ['oathbreaker', 'Oathbreaker'], ['faithless', 'The Faithless'],
  ['trucebreaker', 'Trucebreaker'], ['hunted', 'The Hunted'], ['black_name', 'Black Name'],
];

export function greatHouse(name) {
  const k = String(name || '').trim().replace(/^house\s+/i, '').toLowerCase();
  return GREAT_HOUSES.includes(k) ? k : null;
}

// The renown title an event names ("Wren is named Kingslayer"), the longest match, or null.
export function titleIn(text) {
  const t = String(text || '').toLowerCase();
  let best = null;
  for (const [id, name] of TITLES) {
    const n = name.toLowerCase();
    if ((t.endsWith(n) || t.includes(` ${n}`)) && (!best || n.length > best[1].length)) best = [id, name];
  }
  return best ? best[0] : null;
}

// Finds the art files. Each method returns { name, path } or null (art off, or the file is missing).
export function createArt({ dir, enabled = true } = {}) {
  const file = (rel, name) => {
    if (!enabled || !dir) return null;
    const path = join(dir, rel);
    return existsSync(path) ? { name, path } : null;
  };
  const art = {
    enabled: !!(enabled && dir),
    emblem: () => file('logo/realm-emblem-192.png', 'realm-emblem.png'),
    icon: (name) => file(`icons/${name}-96.png`, `icon-${name}.png`),
    eventIcon: (type) => art.icon(EVENT_ICONS[type] || 'decree'),
    house: (name) => {
      const k = greatHouse(name);
      return k ? file(`sigils/${k}-128.png`, `sigil-${k}.png`) : null;
    },
    season: (n) => (Number.isInteger(n) && n > 0 ? file(`badges/seasons/season-${((n - 1) % 4) + 1}-256.png`, `season-${((n - 1) % 4) + 1}.png`) : null),
    title: (id) => (id ? file(`badges/titles/${id.replace(/_/g, '-')}-256.png`, `title-${id.replace(/_/g, '-')}.png`) : null),
    // The best picture for one Chronicle event: a season medal, a title badge, else the event's icon.
    event: (e) => {
      if (!e) return null;
      if (e.type === 'season_started' || e.type === 'season_ended') {
        const m = /\bseason\s+(\d{1,3})\b/i.exec(e.title || '');
        const s = m ? art.season(Number(m[1])) : null;
        if (s) return s;
      }
      if (e.type === 'title_earned' || e.type === 'title_bestowed') {
        const t = art.title(titleIn(e.title));
        if (t) return t;
      }
      return art.eventIcon(e.type);
    },
  };
  return art;
}

// Puts art on a reply's first embed: `emblem` as the author icon (next to `authorName`) and `thumb` as
// the thumbnail. Adds the files to attach. A payload with no pictures to add is returned unchanged.
export function decorate(payload, { emblem = null, thumb = null, authorName = '' } = {}) {
  if (!payload || !Array.isArray(payload.embeds) || !payload.embeds.length) return payload;
  const files = [];
  const add = (f) => {
    if (!files.some((x) => x.name === f.name)) files.push({ attachment: f.path, name: f.name });
    return `attachment://${f.name}`;
  };
  const [first, ...rest] = payload.embeds;
  const e = { ...first };
  if (emblem && authorName && !e.author) e.author = { name: authorName, icon_url: add(emblem) };
  if (thumb && !e.thumbnail) e.thumbnail = { url: add(thumb) };
  if (!files.length) return payload;
  return { ...payload, embeds: [clampEmbed(e), ...rest], files };
}

// A copy of a payload without its pictures (used when Discord refuses the files).
export function stripArt(payload) {
  if (!payload || !payload.files) return payload;
  const { files, ...rest } = payload;
  return {
    ...rest,
    embeds: (rest.embeds || []).map((e) => {
      const out = { ...e };
      if (out.thumbnail && /^attachment:/.test(out.thumbnail.url)) delete out.thumbnail;
      if (out.author && /^attachment:/.test(out.author.icon_url || '')) out.author = { name: out.author.name };
      return out;
    }),
  };
}
