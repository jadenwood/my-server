// Palette quantisation: every sculpture colour is one of the colours in art/palette.json, picked by the
// smallest CIELAB distance (CIE76). Generators name colours by palette name; models give free colours.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ART = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const PALETTE = JSON.parse(fs.readFileSync(path.join(ART, 'palette.json'), 'utf8'));

const HOUSE_KEYS = ['field', 'fieldLight', 'fieldDark', 'metal', 'metalLight', 'metalShadow'];

/** Every colour a sculpture may use, as { name, hex }. */
export function paletteColours(pal = PALETTE) {
  const out = [];
  for (const group of ['brand', 'scene', 'enamel']) for (const c of pal[group] || []) out.push({ name: c.name, hex: c.hex.toLowerCase() });
  for (const [key, house] of Object.entries(pal.houses || {})) {
    for (const k of HOUSE_KEYS) if (house[k]) out.push({ name: `${key}.${k}`, hex: house[k].toLowerCase() });
    (house.extra || []).forEach((hex, i) => out.push({ name: `${key}.extra${i}`, hex: hex.toLowerCase() }));
  }
  const seen = new Set();
  return out.filter((c) => (seen.has(c.hex) ? false : seen.add(c.hex)));
}

export const COLOURS = paletteColours();
export const PALETTE_HEXES = new Set(COLOURS.map((c) => c.hex));

export const hexToRgb = (hex) => {
  const v = parseInt(hex.replace('#', ''), 16);
  return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
};
export const rgbToHex = ([r, g, b]) => '#' + [r, g, b].map((c) => Math.max(0, Math.min(255, Math.round(c))).toString(16).padStart(2, '0')).join('');

export function rgbToLab([r, g, b]) {
  const lin = (c) => { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
  const R = lin(r), G = lin(g), B = lin(b);
  const X = (R * 0.4124 + G * 0.3576 + B * 0.1805) / 0.95047;
  const Y = R * 0.2126 + G * 0.7152 + B * 0.0722;
  const Z = (R * 0.0193 + G * 0.1192 + B * 0.9505) / 1.08883;
  const f = (t) => (t > 216 / 24389 ? Math.cbrt(t) : (24389 / 27 * t + 16) / 116);
  return [116 * f(Y) - 16, 500 * (f(X) - f(Y)), 200 * (f(Y) - f(Z))];
}

const LABS = COLOURS.map((c) => ({ ...c, lab: rgbToLab(hexToRgb(c.hex)) }));

/** The nearest palette colour to any #rrggbb (or [r,g,b]); a palette colour maps to itself. */
export function quantise(colour, allowed = LABS) {
  const rgb = Array.isArray(colour) ? colour : hexToRgb(colour);
  const lab = rgbToLab(rgb);
  let best = null, bd = Infinity;
  for (const c of allowed) {
    const d = (c.lab[0] - lab[0]) ** 2 + (c.lab[1] - lab[1]) ** 2 + (c.lab[2] - lab[2]) ** 2;
    if (d < bd) { bd = d; best = c; }
  }
  return best.hex;
}

/** A palette colour by name ("Iron 700", "Ember", "varrow.field"); throws on a typo. */
export function pal(name) {
  const c = COLOURS.find((x) => x.name.toLowerCase() === name.toLowerCase());
  if (!c) throw new Error(`"${name}" is not a colour in art/palette.json`);
  return c.hex;
}

/** A house colour: house('varrow', 'field'). */
export function house(key, part) {
  const h = PALETTE.houses[key];
  if (!h || !h[part]) throw new Error(`palette.json has no ${key}.${part}`);
  return h[part].toLowerCase();
}

export const HOUSE_KEYS_ORDER = ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin'];
