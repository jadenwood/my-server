// Text helpers shared by every reply: event-type labels, house colours, Markdown/mention escaping and
// Discord's embed limits. Player-written text (house names, sigils, chronicle titles) always goes
// through esc(), and every message is sent with allowedMentions { parse: [] }.

import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// Built-in labels for the Chronicle's original event types (same as chronicle/public/assets/common.js
// TYPE_META). At start-up loadChronicleLabels() reads that file, so types registered later are labelled too.
// Emoji are the bot's stand-ins for the overlay's drawn icons.
export const TYPE_META = {
  coronation: { label: 'Coronation', emoji: '👑' },
  abdication: { label: 'The Crown Falls', emoji: '💀' },
  claim_declared: { label: 'A Claim', emoji: '🚩' },
  rebellion_started: { label: 'Rebellion', emoji: '⚔️' },
  rebellion_ended: { label: 'Rebellion Ends', emoji: '🗡️' },
  house_founded: { label: 'A House Rises', emoji: '🛡️' },
  oath_sworn: { label: 'Oath Sworn', emoji: '🤝' },
  oath_broken: { label: 'Oathbreaker', emoji: '⛓️' },
  treaty_signed: { label: 'Treaty', emoji: '📜' },
  treaty_broken: { label: 'Treaty Broken', emoji: '🔥' },
  decree: { label: 'Royal Decree', emoji: '📯' },
  ransom_set: { label: 'Ransom', emoji: '⛓️' },
  ransom_paid: { label: 'Ransom Paid', emoji: '🪙' },
  released: { label: 'Set Free', emoji: '🕊️' },
  contract_posted: { label: 'A Price Is Set', emoji: '📜' },
  contract_fulfilled: { label: 'Contract Paid', emoji: '🪙' },
  contract_ended: { label: 'Contract Lapses', emoji: '📜' },
  season_started: { label: 'A New Season', emoji: '⏳' },
  season_ended: { label: "Season's End", emoji: '🏆' },
  event_started: { label: 'Realm Event', emoji: '📯' },
  event_ended: { label: 'Event Ends', emoji: '📯' },
  tournament_champion: { label: 'Tournament Champion', emoji: '🏆' },
  hunt_kill: { label: "The King's Hunt", emoji: '⚔️' },
  truce_broken: { label: 'Truce Broken', emoji: '🔥' },
};

const ICON_EMOJI = {
  crown: '👑', crownX: '💀', flag: '🚩', swords: '⚔️', sheath: '🗡️', shield: '🛡️', oath: '🤝', chain: '⛓️', chainX: '⛓️',
  seal: '🔏', scroll: '📜', scrollX: '🔥', coins: '🪙', people: '👥', trophy: '🏆', hourglass: '⏳', horn: '📯',
};
const emojiForIcon = (icon) => ICON_EMOJI[icon] || '📜';

const registeredMeta = {}; // from the Chronicle pages: types the Chronicle actually serves
const pendingMeta = {}; // from plugins/docs/*/EVENTS.json: types waiting to be registered

// Reads the event labels the Chronicle pages use (TYPE_META in chronicle/public/assets/common.js) so a type
// registered after this bot was written still gets its proper label. common.js is browser code, so it is
// parsed as text, never run. Returns how many types were read (0 if the file is missing or changed shape).
export function loadChronicleLabels(commonJsPath) {
  let text;
  try {
    text = readFileSync(commonJsPath, 'utf8');
  } catch {
    return 0;
  }
  const start = text.indexOf('TYPE_META = {');
  if (start < 0) return 0;
  const block = text.slice(start, text.indexOf('\n};', start));
  let n = 0;
  for (const m of block.matchAll(/^\s*([a-z_]{1,40}):\s*\{\s*label:\s*'((?:[^'\\\n]|\\.){1,60})'(?:,\s*icon:\s*'(\w+)')?/gm)) {
    registeredMeta[m[1]] = { label: m[2].replace(/\\(.)/g, '$1'), emoji: TYPE_META[m[1]]?.emoji || emojiForIcon(m[3]) };
    n++;
  }
  return n;
}

// Pending event types that other plugins list in plugins/docs/*/EVENTS.json. They only appear in the
// Chronicle once the integration team registers them, but knowing their labels means the bot needs
// no change when that happens. Bad files are skipped.
export function loadExtraTypes(pluginDocsDir) {
  let names = [];
  try {
    names = readdirSync(pluginDocsDir, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name);
  } catch {
    return 0;
  }
  let n = 0;
  for (const name of names) {
    try {
      const list = JSON.parse(readFileSync(join(pluginDocsDir, name, 'EVENTS.json'), 'utf8'));
      for (const t of Array.isArray(list) ? list : []) {
        if (t && typeof t.type === 'string' && /^[a-z_]{1,40}$/.test(t.type) && typeof t.label === 'string') {
          pendingMeta[t.type] = { label: t.label.slice(0, 40), emoji: emojiForIcon(t.icon) };
          n++;
        }
      }
    } catch {
      // no EVENTS.json there, or unreadable: ignore
    }
  }
  return n;
}

export const metaFor = (type) => registeredMeta[type] || TYPE_META[type] || pendingMeta[type] || { label: 'Chronicle', emoji: '📜' };

// Banner palette and name hash from chronicle/public/assets/common.js, so a house has the same colour
// on Discord as on the overlay, the portal and the Steward's herald.
export const DYES = ['#7a1f1c', '#1f2f5c', '#24472d', '#86601a', '#4a2347', '#2c3b42', '#7a3a1a', '#1c4a4a', '#3a2a1a', '#5a5a22'];

function hash(s) {
  let h = 2166136261;
  for (const ch of s.toLowerCase()) h = Math.imul(h ^ ch.codePointAt(0), 16777619);
  return h >>> 0;
}

export const dyeHexFor = (name) => DYES[hash(name || '') % DYES.length];
export const dyeFor = (name) => parseInt(dyeHexFor(name).slice(1), 16);

export const COLORS = { gold: 0xc8a050, blood: 0x7a1f1c, iron: 0x5b6670, moss: 0x24472d };

// Escapes Discord Markdown and defuses mentions. Control characters are dropped.
export function esc(value, max = 200) {
  let s = String(value ?? '')
    .replace(/[\u0000-\u0008\u000b-\u001f\u007f​-‏‪-‮⁦-⁩]/g, '')
    .slice(0, max);
  s = s.replace(/[\\*_~`|>#\-[\]()<:]/g, (c) => `\\${c}`);
  s = s.replace(/@/g, '@​');
  return s;
}

// Plain-text cleanup for places Markdown does not render (role names, autocomplete choices).
export function plain(value, max = 100) {
  return String(value ?? '')
    .replace(/[\u0000-\u001f\u007f​-‏‪-‮⁦-⁩]/g, '')
    .replace(/@/g, '')
    .trim()
    .slice(0, max);
}

export function houseLabel(name) {
  if (!name) return '';
  return /^house\s/i.test(name) ? name : `House ${name}`;
}

export const sameHouse = (a, b) =>
  typeof a === 'string' && typeof b === 'string' &&
  a.replace(/^house\s+/i, '').trim().toLowerCase() === b.replace(/^house\s+/i, '').trim().toLowerCase();

// Discord timestamp markup: renders in each reader's own time zone and keeps "3 hours ago" live.
export function ts(iso, style = 'R') {
  const t = typeof iso === 'number' ? iso : Date.parse(iso);
  if (!Number.isFinite(t)) return 'unknown time';
  return `<t:${Math.floor(t / 1000)}:${style}>`;
}

export function duration(ms) {
  let s = Math.max(0, Math.floor(ms / 1000));
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

export function truncate(s, max) {
  s = String(s ?? '');
  if (s.length <= max) return s;
  // Do not cut a backslash escape in half.
  let cut = s.slice(0, Math.max(0, max - 1));
  if (/(^|[^\\])(\\\\)*\\$/.test(cut)) cut = cut.slice(0, -1);
  return `${cut}…`;
}

// Discord's documented embed limits.
export const LIMITS = { title: 256, description: 4096, fields: 25, fieldName: 256, fieldValue: 1024, footer: 2048, author: 256, total: 6000 };

// Clamps an embed to Discord's limits so a long chronicle never makes a reply fail.
export function clampEmbed(e) {
  const out = { ...e };
  if (out.title) out.title = truncate(out.title, LIMITS.title);
  if (out.description) out.description = truncate(out.description, LIMITS.description);
  if (out.footer?.text) out.footer = { ...out.footer, text: truncate(out.footer.text, LIMITS.footer) };
  if (out.author?.name) out.author = { ...out.author, name: truncate(out.author.name, LIMITS.author) };
  if (Array.isArray(out.fields)) {
    out.fields = out.fields.slice(0, LIMITS.fields).map((f) => ({
      name: truncate(f.name || '​', LIMITS.fieldName),
      value: truncate(f.value || '​', LIMITS.fieldValue),
      inline: !!f.inline,
    }));
  }
  // Total budget: drop trailing fields, then shorten the description.
  const size = (x) => (x.title?.length || 0) + (x.description?.length || 0) + (x.footer?.text?.length || 0) +
    (x.author?.name?.length || 0) + (x.fields || []).reduce((n, f) => n + f.name.length + f.value.length, 0);
  while (size(out) > LIMITS.total && out.fields?.length) out.fields = out.fields.slice(0, -1);
  if (size(out) > LIMITS.total && out.description) {
    out.description = truncate(out.description, Math.max(1, out.description.length - (size(out) - LIMITS.total)));
  }
  return out;
}

export const embedSize = (e) => (e.title?.length || 0) + (e.description?.length || 0) + (e.footer?.text?.length || 0) +
  (e.author?.name?.length || 0) + (e.fields || []).reduce((n, f) => n + f.name.length + f.value.length, 0);
