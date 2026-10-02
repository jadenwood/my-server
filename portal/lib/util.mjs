// Small helpers shared by the portal generator. No dependencies.

// HTML-escape any value for text or attribute context.
export function esc(v) {
  return String(v == null ? '' : v)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// URL-safe, file-safe slug. Never empty; never contains "." or "/".
export function slug(s) {
  const out = String(s || '')
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 48);
  return out || 'house';
}

// GitHub-style heading anchor ("5. Streams, sniping and ghosting" -> "5-streams-sniping-and-ghosting").
export function anchor(text) {
  return String(text || '')
    .toLowerCase()
    .replace(/<[^>]*>/g, '')
    .replace(/[^\p{L}\p{N}\s_-]/gu, '')
    .trim()
    .replace(/\s/g, '-');
}

// Dyed-cloth palette, identical to chronicle/public/assets/common.js (dyeFor) and
// launcher/lib/discord.js (houseColour), so a house has one colour everywhere: overlay, portal, Discord.
export const DYES = ['#7a1f1c', '#1f2f5c', '#24472d', '#86601a', '#4a2347', '#2c3b42', '#7a3a1a', '#1c4a4a', '#3a2a1a', '#5a5a22'];

export function fnv(s) {
  let h = 2166136261;
  for (const ch of String(s || '').toLowerCase()) h = Math.imul(h ^ ch.codePointAt(0), 16777619);
  return h >>> 0;
}

export function shade(hex, amt) {
  const n = parseInt(hex.slice(1), 16);
  const mix = (c) => Math.round(amt >= 0 ? c + (255 - c) * amt : c * (1 + amt));
  const r = mix(n >> 16), g = mix((n >> 8) & 255), b = mix(n & 255);
  return `#${((1 << 24) | (r << 16) | (g << 8) | b).toString(16).slice(1)}`;
}

export function dyeFor(name) {
  return DYES[fnv(name || '') % DYES.length];
}

export function dyeStyle(name) {
  const d = dyeFor(name);
  return `--dye:${d};--dye-light:${shade(d, 0.16)};--dye-dark:${shade(d, -0.38)}`;
}

export function monogram(name) {
  const m = String(name || '?').trim().match(/\p{L}|\p{N}/u);
  return m ? m[0].toUpperCase() : '?';
}

export function houseLabel(name) {
  if (!name) return '';
  return /^house\s/i.test(name) ? name : `House ${name}`;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// "30 Sep 2026, 19:42 UTC". Always UTC so a static page means the same thing everywhere.
export function fmtDate(iso, withTime = true) {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const d = new Date(t);
  const p = (n) => String(n).padStart(2, '0');
  const day = `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
  return withTime ? `${day}, ${p(d.getUTCHours())}:${p(d.getUTCMinutes())} UTC` : day;
}

export function duration(ms) {
  if (!(ms >= 0)) return '';
  let s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

// Only http(s) URLs (or a same-site relative path) ever reach an href; everything else becomes null.
export function safeUrl(u, { allowRelative = false } = {}) {
  if (typeof u !== 'string' || !u.trim()) return null;
  const s = u.trim();
  if (allowRelative && /^(\.\/|\.\.\/|[a-z0-9_-]+(\.html)?([#?].*)?$|#)/i.test(s) && !/^[a-z][a-z0-9+.-]*:/i.test(s)) return s;
  try {
    const url = new URL(s);
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
    if (url.username || url.password) return null;
    return url.href;
  } catch {
    return null;
  }
}
