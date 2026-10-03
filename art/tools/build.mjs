#!/usr/bin/env node
// Realm art pack build.
//
//   node art/tools/build.mjs all        generate + optimize + png + gallery + check (the normal run)
//   node art/tools/build.mjs generate   banners, shields, logo lockups, Discord art, social cards, icon sprite,
//                                       key art, season and title badges, textures (the last three in sets.mjs)
//   node art/tools/build.mjs optimize   svgo every SVG in place (pretty-printed, ids/titles/desc kept)
//   node art/tools/build.mjs png        PNG previews into art/png/ with headless Chromium (Playwright)
//   node art/tools/build.mjs gallery    writes art/index.html
//   node art/tools/build.mjs check      lint: well-formed, safe, on-palette, ids resolve, sizes, PNGs present
//   node art/tools/build.mjs card --house corvane --kicker "Treaty broken" --title "..." [--subtitle ".."]
//                                 [--footer ".."] --out card.svg [--png card.png]
//
// Options: --chromium <path to chrome>   (default /opt/pw-browsers/chromium when it exists, else Playwright's own)
//          --fonts <dir>                 local Cinzel / EB Garamond .ttf files for PNG text (else Google Fonts)
// Modules (svgo, playwright) come from art/tools/node_modules or $ART_NODE_MODULES; see art/README.md.
//
// Hand-written sources: art/sigils/*.svg, art/logo/realm-emblem.svg, art/icons/*.svg, art/icons/event-map.json,
// art/src/emblems/*.svg, art/src/titles.json, art/palette.json, art/src/wordmark.json (outlined once from Cinzel by
// wordmark.mjs). Everything else is generated from them.
import fs from 'node:fs';
import path from 'node:path';
import { ART, loadModule, walk, rel, innerGroup, attrsOf, esc } from './lib.mjs';
import { keyartFiles, badgeFiles, textureFiles, scene } from './sets.mjs';

const argv = process.argv.slice(2);
const cmd = argv[0] && !argv[0].startsWith('--') ? argv[0] : 'all';
const opt = {};
for (let i = 0; i < argv.length; i++) if (argv[i].startsWith('--')) opt[argv[i].slice(2)] = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : true;

const palette = JSON.parse(fs.readFileSync(path.join(ART, 'palette.json'), 'utf8'));
const HOUSES = Object.keys(palette.houses);
const slug = (s) => s.toLowerCase().replace(/\s+/g, '-');
const C = Object.fromEntries(palette.brand.map((b) => [slug(b.name), b.hex]));
const S = Object.fromEntries((palette.scene || []).map((b) => [slug(b.name), b.hex]));
const read = (f) => fs.readFileSync(path.join(ART, f), 'utf8');
const write = (f, s) => {
  fs.mkdirSync(path.dirname(path.join(ART, f)), { recursive: true });
  fs.writeFileSync(path.join(ART, f), s);
};
const n = (v) => +(+v).toFixed(2);

// ---------------------------------------------------------------- composition helpers

function doc({ w, h, title, desc, defs = '', body, attrs = '' }) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${n(w)} ${n(h)}" width="${n(w)}" height="${n(h)}"${attrs}>
<title>${esc(title)}</title>
<desc>${esc(desc)}</desc>
${defs ? `<defs>${defs}</defs>\n` : ''}${body}
</svg>
`;
}

// Renames every id in a fragment (and its references) so one source can be nested twice in a document.
export function suffixIds(markup, suffix) {
  if (!suffix) return markup;
  const ids = [...markup.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]);
  for (const id of ids) {
    const re = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    markup = markup
      .replace(new RegExp(`\\bid="${re(id)}"`, 'g'), `id="${id}-${suffix}"`)
      .replace(new RegExp(`url\\(#${re(id)}\\)`, 'g'), `url(#${id}-${suffix})`)
      .replace(new RegExp(`href="#${re(id)}"`, 'g'), `href="#${id}-${suffix}"`);
  }
  return markup;
}

// Places a whole SVG file's drawing inside another as a nested <svg> viewport.
function nest(svgText, x, y, w, h, { suffix = '', extra = '' } = {}) {
  const open = svgText.match(/<svg\b[^>]*>/)[0];
  const rootAttrs = attrsOf(open);
  const vb = rootAttrs.viewBox;
  // keep inherited presentation attributes (an icon's fill/stroke live on its root)
  const carry = Object.entries(rootAttrs).filter(([k]) => /^(fill|stroke|stroke-[a-z]+|color|opacity|fill-rule)$/.test(k)).map(([k, v]) => ` ${k}="${v}"`).join('');
  let inner = svgText.slice(svgText.indexOf(open) + open.length, svgText.lastIndexOf('</svg>'));
  inner = inner.replace(/<title>[\s\S]*?<\/title>/, '').replace(/<desc>[\s\S]*?<\/desc>/, '');
  return `<svg x="${n(x)}" y="${n(y)}" width="${n(w)}" height="${n(h)}" viewBox="${vb}"${carry}${extra}>${suffixIds(inner, suffix)}</svg>`;
}

function sigil(house) {
  const file = `sigils/${house}.svg`;
  const svg = read(file);
  const root = attrsOf(svg.match(/<svg\b[^>]*>/)[0]);
  const title = (svg.match(/<title>([\s\S]*?)<\/title>/) || [])[1] || house;
  const desc = (svg.match(/<desc>([\s\S]*?)<\/desc>/) || [])[1] || '';
  return { file, svg, house, title, desc, field: root['data-field'], metal: root['data-metal'], charge: innerGroup(svg, 'charge').inner, p: palette.houses[house] };
}

const fieldGradient = (id, p) => `<radialGradient id="${id}" cx="50%" cy="42%" r="62%"><stop offset="0" stop-color="${p.fieldLight}"/><stop offset=".6" stop-color="${p.field}"/><stop offset="1" stop-color="${p.fieldDark}"/></radialGradient>`;
const chargeAt = (s, x, y, size) => `<svg x="${n(x)}" y="${n(y)}" width="${n(size)}" height="${n(size)}" viewBox="0 0 200 200">${s.charge}</svg>`;

// ---------------------------------------------------------------- houses: banner + shield

// Shared heraldic pieces: the gold gradient, a faint lozenge diaper for fields, and the sheen of light across metal.
const goldStops = `<stop offset="0" stop-color="${C['ember-pale']}"/><stop offset=".35" stop-color="${C['ember-hot']}"/><stop offset=".6" stop-color="${C.ember}"/><stop offset="1" stop-color="${C['ember-deep']}"/>`;
const goldGradient = (id, vertical = true) => `<linearGradient id="${id}" x1="0" y1="0" x2="${vertical ? 0 : 1}" y2="${vertical ? 1 : 0}">${goldStops}</linearGradient>`;
const diaper = (id, color, size = 18) => `<pattern id="${id}" width="${size}" height="${size}" patternTransform="rotate(45)" patternUnits="userSpaceOnUse"><path d="M0 .5h${size}M.5 0v${size}" fill="none" stroke="${color}" stroke-width=".7"/><circle cx="${size / 2}" cy="${size / 2}" r="1.1" fill="${color}"/></pattern>`;

function banner(s) {
  const { p, house } = s;
  const cloth = 'M24 26h152v296l-76-40-76 40z';
  const defs = `<linearGradient id="${house}-cloth" x1="0" x2="1"><stop offset="0" stop-color="${p.fieldDark}"/><stop offset=".18" stop-color="${p.field}"/><stop offset=".32" stop-color="${p.fieldLight}"/><stop offset=".5" stop-color="${p.field}"/><stop offset=".68" stop-color="${p.fieldDark}"/><stop offset=".84" stop-color="${p.fieldLight}"/><stop offset="1" stop-color="${p.fieldDark}"/></linearGradient>
<linearGradient id="${house}-fold" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C['iron-950']}" stop-opacity=".55"/><stop offset=".12" stop-color="${C['iron-950']}" stop-opacity="0"/><stop offset=".8" stop-color="${C['iron-950']}" stop-opacity="0"/><stop offset="1" stop-color="${C['iron-950']}" stop-opacity=".45"/></linearGradient>
${goldGradient(`${house}-bgold`)}${goldGradient(`${house}-bgold-h`, false)}${diaper(`${house}-bdiaper`, p.fieldLight, 16)}
<clipPath id="${house}-bclip"><path d="${cloth}"/></clipPath>`;
  const finial = (x) => `<g transform="translate(${x} 19)"><circle r="8" fill="url(#${house}-bgold)" stroke="${C['iron-950']}" stroke-width="1.2"/><path d="M0-8l2.6-6L0-19l-2.6 5Z" fill="url(#${house}-bgold)" stroke="${C['iron-950']}" stroke-width="1"/><circle r="2.6" cx="-2.4" cy="-2.6" fill="${C['ember-pale']}" opacity=".7"/></g>`;
  const tassel = (x) => `<g transform="translate(${x} 0)"><path d="M0 24c-2 20 2 40 0 58" fill="none" stroke="${C['parchment-edge']}" stroke-width="1.6"/><circle cy="84" r="3.2" fill="url(#${house}-bgold)" stroke="${C['iron-950']}" stroke-width=".8"/><path d="M-3.4 86h6.8l2.6 14H-6Z" fill="url(#${house}-bgold)" stroke="${C['iron-950']}" stroke-width=".8"/><path d="M-3 90v9M0 90v10M3 90v9" stroke="${C['ember-deep']}" stroke-width=".8"/></g>`;
  const body = `<path d="M30 17 100 4l70 13" fill="none" stroke="${C['parchment-edge']}" stroke-width="2"/>
<g clip-path="url(#${house}-bclip)">
<path d="${cloth}" fill="url(#${house}-cloth)"/>
<path d="${cloth}" fill="url(#${house}-bdiaper)" opacity=".22"/>
<path d="M24 26h152v16H24z" fill="${p.fieldDark}"/>
<path d="M24 42h152" stroke="url(#${house}-bgold-h)" stroke-width="2.5"/>
<path d="M24 46h152" stroke="${C['ember-deep']}" stroke-width="1" stroke-dasharray="1 3"/>
<path d="${cloth}" fill="url(#${house}-fold)"/>
</g>
<path d="M34 50v258l66-34.5 66 34.5V50z" fill="none" stroke="url(#${house}-bgold)" stroke-width="3"/>
<path d="M39.5 55v244.5l60.5-31.5 60.5 31.5V55z" fill="none" stroke="${C.ember}" stroke-width="1" stroke-dasharray="3 2.5" opacity=".7"/>
<path d="M24 322l76-40 76 40" fill="none" stroke="url(#${house}-bgold)" stroke-width="5" stroke-dasharray="1.4 1.6"/>
<rect x="6" y="14" width="188" height="10" rx="5" fill="${C['iron-700']}" stroke="${C['iron-950']}" stroke-width="1.5"/>
<path d="M12 16.5h176" stroke="${C['iron-600']}" stroke-width="1.5" stroke-linecap="round"/>
${finial(8)}${finial(192)}
${chargeAt(s, 33, 92, 134)}
${tassel(18)}${tassel(182)}`;
  return doc({ w: 200, h: 340, title: `House ${p.name}: banner`, desc: `Hanging swallow-tailed banner of House ${p.name}, the ${p.sigil}. Generated by art/tools/build.mjs from ${s.file}.`, defs, body });
}

function shield(s) {
  const { p, house } = s;
  const outline = 'M20 14h200v114c0 68-44 112-100 138C64 240 20 196 20 128z';
  const inner = 'M33 27h174v101c0 59-37 97-87 121-50-24-87-62-87-121z';
  const defs = `${fieldGradient(`${house}-shield`, p)}${goldGradient(`${house}-sgold`)}${diaper(`${house}-sdiaper`, p.fieldLight)}
<linearGradient id="${house}-sheen" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${C.parchment}" stop-opacity=".22"/><stop offset=".45" stop-color="${C.parchment}" stop-opacity="0"/><stop offset="1" stop-color="${C['iron-950']}" stop-opacity=".35"/></linearGradient>`;
  const rivet = (x, y) => `<circle cx="${x}" cy="${y}" r="3.4" fill="url(#${house}-sgold)" stroke="${C['iron-950']}" stroke-width="1"/>`;
  const body = `<path d="${outline}" fill="${C['iron-800']}" stroke="${C['iron-950']}" stroke-width="3" stroke-linejoin="round"/>
<path d="${outline}" fill="none" stroke="url(#${house}-sgold)" stroke-width="7" stroke-linejoin="round" transform="translate(120 140) scale(.955) translate(-120 -140)"/>
<path d="${inner}" fill="url(#${house}-shield)"/>
<path d="${inner}" fill="url(#${house}-sdiaper)" opacity=".28"/>
<path d="${inner}" fill="none" stroke="${C['iron-950']}" stroke-width="2.5" opacity=".7"/>
<path d="M41 35h158v93c0 54-34 89-79 111-45-22-79-57-79-111z" fill="none" stroke="${C.ember}" stroke-width="1" opacity=".55"/>
${chargeAt(s, 37, 44, 166)}
<path d="${inner}" fill="url(#${house}-sheen)"/>
${rivet(30, 24)}${rivet(210, 24)}${rivet(120, 258)}`;
  return doc({ w: 240, h: 280, title: `House ${p.name}: shield`, desc: `Heater shield of House ${p.name}, the ${p.sigil}. Generated by art/tools/build.mjs from ${s.file}.`, defs, body });
}

// ---------------------------------------------------------------- logo

const WM = () => JSON.parse(read('src/wordmark.json'));

// The wordmark block: REALM over a ruled OSTREVAL line. Returns markup positioned at 0,0.
export function wordmark(mode, idp = 'wm') {
  const { realm, ostreval: o } = WM();
  const W = realm.ink.w, gap = 26, y = realm.ink.h + gap, h = y + o.ink.h;
  const ox = (W - o.ink.w) / 2;
  const mid = y + o.ink.h / 2;
  const dark = mode === 'dark';
  const gold = `url(#${idp}-gold)`;
  const defs = dark ? `<linearGradient id="${idp}-gold" x1="0" y1="0" x2="0" y2="${n(realm.ink.h)}" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${C['ember-pale']}"/><stop offset=".35" stop-color="${C['ember-hot']}"/><stop offset=".6" stop-color="${C.ember}"/><stop offset="1" stop-color="${C['ember-deep']}"/></linearGradient>` : '';
  const rule = dark ? C.ember : C['ember-deep'];
  const lo = ox - 22, ro = W - ox + 22;
  const body = `<path d="${realm.d}" transform="translate(${n(-realm.ink.x)} ${n(-realm.ink.y)})" fill="${dark ? gold : C.ink}"/>
<path d="${o.d}" transform="translate(${n(ox - o.ink.x)} ${n(y - o.ink.y)})" fill="${dark ? C.parchment : C['ink-soft']}"/>
<path d="M0 ${n(mid)}H${n(lo - 8)}M${n(ro + 8)} ${n(mid)}H${n(W)}" stroke="${rule}" stroke-width="2"/>
<path d="M${n(lo - 6)} ${n(mid)}l5-5 5 5-5 5zM${n(ro - 4)} ${n(mid)}l5-5 5 5-5 5z" fill="${rule}"/>`;
  return { w: W, h, defs, body };
}

function emblemInner(suffix = '') {
  return suffixIds(innerGroup(read('logo/realm-emblem.svg'), 'emblem').inner, suffix);
}

function lockup(kind, mode) {
  const wm = wordmark(mode);
  let w, h, body;
  if (kind === 'horizontal') {
    const E = 200, g = 44;
    w = E + g + wm.w; h = E;
    body = `<svg x="0" y="0" width="${E}" height="${E}" viewBox="0 0 256 256">${emblemInner()}</svg>
<g transform="translate(${E + g} ${n((E - wm.h) / 2)})">${wm.body}</g>`;
  } else {
    const E = 230, g = 34;
    w = wm.w; h = E + g + wm.h;
    body = `<svg x="${n((w - E) / 2)}" y="0" width="${E}" height="${E}" viewBox="0 0 256 256">${emblemInner()}</svg>
<g transform="translate(0 ${E + g})">${wm.body}</g>`;
  }
  return { w, h, defs: wm.defs, body };
}

function logoFiles() {
  const out = {};
  for (const mode of ['dark', 'light']) {
    const on = mode === 'dark' ? 'dark backgrounds' : 'light (parchment or white) backgrounds';
    const wm = wordmark(mode);
    out[`logo/realm-wordmark-${mode}.svg`] = doc({ w: wm.w, h: wm.h, title: `Realm wordmark (${mode})`, desc: `REALM / OSTREVAL wordmark for ${on}. Letters outlined from Cinzel (OFL 1.1). Generated by art/tools/build.mjs.`, defs: wm.defs, body: wm.body });
    for (const kind of ['horizontal', 'stacked']) {
      const l = lockup(kind, mode);
      out[`logo/realm-logo-${kind}-${mode}.svg`] = doc({ w: l.w, h: l.h, title: `Realm logo, ${kind} (${mode})`, desc: `Emblem and wordmark, ${kind} lockup for ${on}. Generated by art/tools/build.mjs from logo/realm-emblem.svg and src/wordmark.json.`, defs: l.defs, body: l.body });
    }
  }
  const throne = innerGroup(read('logo/realm-emblem.svg'), 'throne').inner;
  out['logo/realm-emblem-mono.svg'] = doc({
    w: 256, h: 256, title: 'Realm emblem, one colour',
    desc: 'Single-colour emblem that takes the CSS color (currentColor): for embossing, watermarks, stamps and one-colour print. Generated by art/tools/build.mjs.',
    body: `<g fill="none" stroke="currentColor"><circle cx="128" cy="128" r="118" stroke-width="8"/><circle cx="128" cy="128" r="104" stroke-width="2"/></g>
<g fill="currentColor">${throne}</g>`,
  });
  out['logo/realm-favicon.svg'] = doc({
    w: 32, h: 32, title: 'Realm favicon',
    desc: 'Small-size mark: the event crown on iron. Use below 48px, where the full emblem gets muddy. Generated by art/tools/build.mjs from icons/crown.svg.',
    body: `<rect width="32" height="32" rx="7" fill="${C['iron-900']}"/>\n${nest(read('icons/crown.svg'), 4, 4, 24, 24, { extra: ` color="${C.ember}"` })}`,
  });
  return out;
}

// ---------------------------------------------------------------- Discord

const ironBg = (id, w, h) => ({
  defs: `<linearGradient id="${id}-bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C['iron-700']}"/><stop offset=".55" stop-color="${C['iron-900']}"/><stop offset="1" stop-color="${C['iron-950']}"/></linearGradient><radialGradient id="${id}-glow" cx="50%" cy="30%" r="55%"><stop offset="0" stop-color="${C.ember}" stop-opacity=".22"/><stop offset="1" stop-color="${C.ember}" stop-opacity="0"/></radialGradient>`,
  body: `<rect width="${w}" height="${h}" fill="url(#${id}-bg)"/><rect width="${w}" height="${h}" fill="url(#${id}-glow)"/>`,
});

// A sigil nested in a larger piece; its "charge" id is made unique so several sigils can share a document.
const sigilNest = (h, x, y, size) => nest(read(`sigils/${h}.svg`).replace(' id="charge"', ` id="charge-${h}"`), x, y, size, size);

function sigilRow(xs, y, size) {
  return HOUSES.map((h, i) => sigilNest(h, xs[i], typeof y === 'number' ? y : y[i], size)).join('\n');
}

function discordFiles() {
  const out = {};
  const bg512 = ironBg('icon', 512, 512);
  out['discord/server-icon.svg'] = doc({
    w: 512, h: 512, title: 'Realm Discord server icon', desc: 'Server icon, 512 x 512. Discord shows it in a circle; the emblem is round so nothing important is cropped. Generated by art/tools/build.mjs.',
    defs: bg512.defs, body: `${bg512.body}\n<svg x="12" y="12" width="488" height="488" viewBox="0 0 256 256">${emblemInner()}</svg>`,
  });

  const frame = (w, h, inset) => `<rect x="${inset}" y="${inset}" width="${w - inset * 2}" height="${h - inset * 2}" fill="none" stroke="${C.ember}" stroke-width="2" opacity=".55"/><rect x="${inset + 7}" y="${inset + 7}" width="${w - inset * 2 - 14}" height="${h - inset * 2 - 14}" fill="none" stroke="${C.ember}" stroke-width="1" opacity=".25"/>`;

  {
    // the key-art scene, darkened, behind the lockup and the six sigils (its ids get a suffix: the sigils reuse theirs)
    const W = 960, H = 540, l = lockup('horizontal', 'dark'), sc = sceneMarkup('bn');
    const lw = 600, lh = (lw * l.h) / l.w, size = 84, gap = 22;
    const x0 = (W - (size * 6 + gap * 5)) / 2;
    out['discord/server-banner.svg'] = doc({
      w: W, h: H, title: 'Realm Discord server banner', desc: 'Server banner, 960 x 540 (16:9): the Old Throne key art, darkened, under the logo and the six house sigils. Discord shows it small at the top of the channel list, so the lockup is large and centred. Generated by art/tools/build.mjs.',
      defs: sc.defs + l.defs,
      body: `<svg width="${W}" height="${H}" viewBox="0 0 1920 1080">${sc.body}</svg>\n<rect width="${W}" height="${H}" fill="${C['iron-950']}" opacity=".55"/>\n${frame(W, H, 18)}\n<svg x="${n((W - lw) / 2)}" y="120" width="${lw}" height="${n(lh)}" viewBox="0 0 ${n(l.w)} ${n(l.h)}">${l.body}</svg>\n<path d="M${n(x0)} ${H - 156}H${n(W - x0)}" stroke="${C.ember}" stroke-width="1.5" opacity=".5"/>\n${sigilRow(HOUSES.map((_, i) => x0 + i * (size + gap)), H - 136, size)}`,
    });
  }
  {
    // the invite splash is the 1920 key art with the logo
    const art = keyartFiles(setsContext())['keyart/old-throne-1920-title.svg'];
    out['discord/invite-splash.svg'] = art
      .replace(/<title>[\s\S]*?<\/title>/, '<title>Realm Discord invite splash</title>')
      .replace(/<desc>[\s\S]*?<\/desc>/, '<desc>Invite background, 1920 x 1080: the Old Throne key art with the logo. Discord blurs and darkens the edges, so the throne and the logo sit in the centre. Generated by art/tools/build.mjs from art/tools/sets.mjs.</desc>');
  }
  return out;
}

// ---------------------------------------------------------------- social card

// Greedy word wrap. `measure` gives a string's width in the same unit as `max` (default: characters).
export function wrap(text, max, lines, measure = (t) => t.length) {
  const words = String(text).split(/\s+/).filter(Boolean);
  const out = [''];
  for (const w of words) {
    const cur = out[out.length - 1];
    if (!cur) out[out.length - 1] = w;
    else if (measure(cur + ' ' + w) <= max) out[out.length - 1] = cur + ' ' + w;
    else out.push(w);
  }
  if (out.length > lines) {
    const kept = out.slice(0, lines);
    kept[lines - 1] = kept[lines - 1].replace(/\s*\S*$/, '') + '…';
    return kept;
  }
  return out;
}

// Width of a string set in Cinzel Bold at `size` px, from metrics outlined out of the font by wordmark.mjs.
let cinzelMetrics;
export function cinzelWidth(str, size) {
  cinzelMetrics ||= JSON.parse(read('src/cinzel-bold-widths.json'));
  let em = 0;
  for (const ch of str) em += cinzelMetrics.widths[ch] ?? cinzelMetrics.fallback;
  return em * size;
}

export function socialCard({ kicker, title, subtitle = '', footer = '', house = null, template = false }) {
  const W = 1200, H = 630, bg = ironBg('card', W, H);
  const small = wordmark('dark', 'card-wm');
  // The text column runs from x=72 to the rule's end at x=740; the sigil starts at x=790.
  const COL = 668;
  let titleSize = 66, tLines = [title];
  if (!template) {
    tLines = wrap(title, COL, 2, (t) => cinzelWidth(t, 66));
    if (tLines[tLines.length - 1].endsWith('…') || tLines.some((l) => cinzelWidth(l, 66) > COL)) {
      titleSize = 54;
      tLines = wrap(title, COL, 3, (t) => cinzelWidth(t, 54));
    }
  }
  const sLines = template ? [subtitle] : wrap(subtitle, 46, 2);
  const tY = 262;
  const titleSpans = tLines.map((t, i) => `<tspan x="72" y="${tY + i * Math.round(titleSize * 1.12)}">${esc(t)}</tspan>`).join('');
  const sY = tY + (tLines.length - 1) * Math.round(titleSize * 1.12) + 66;
  const subSpans = sLines.map((t, i) => `<tspan x="72" y="${sY + i * 40}">${esc(t)}</tspan>`).join('');
  const art = house
    ? sigilNest(house, 790, 135, 360)
    : `<svg x="790" y="135" width="360" height="360" viewBox="0 0 256 256">${emblemInner('card')}</svg>`;
  const label = house ? `House ${palette.houses[house].name}` : 'The Realm';
  const body = `${bg.body}
<rect x="24" y="24" width="${W - 48}" height="${H - 48}" fill="none" stroke="${C.ember}" stroke-width="2" opacity=".55"/>
<svg x="72" y="62" width="56" height="56" viewBox="0 0 256 256">${emblemInner('mark')}</svg>
<svg x="142" y="70" width="${n((small.w * 40) / small.h)}" height="40" viewBox="0 0 ${n(small.w)} ${n(small.h)}">${small.body}</svg>
<g id="slot-art">${art}</g>
<text id="slot-kicker" x="72" y="190" fill="${C.ember}" font-family="Cinzel, 'Trajan Pro', Georgia, serif" font-weight="700" font-size="24" letter-spacing="5">${esc(String(kicker).toUpperCase())}</text>
<text id="slot-title" fill="${C.parchment}" font-family="Cinzel, 'Trajan Pro', Georgia, serif" font-weight="700" font-size="${titleSize}">${titleSpans}</text>
<text id="slot-subtitle" fill="${C['parchment-2']}" font-family="'EB Garamond', Garamond, Georgia, serif" font-weight="500" font-size="30">${subSpans}</text>
<path d="M72 548H740" stroke="${C.ember}" stroke-width="1.5" opacity=".5"/>
<text id="slot-footer" x="72" y="584" fill="${C['iron-200']}" font-family="'EB Garamond', Garamond, Georgia, serif" font-weight="500" font-size="24">${esc(footer)}</text>
<text x="970" y="548" text-anchor="middle" fill="${C.ember}" font-family="Cinzel, 'Trajan Pro', Georgia, serif" font-weight="700" font-size="22" letter-spacing="4">${esc(label.toUpperCase())}</text>`;
  return doc({
    w: W, h: H,
    title: template ? 'Realm social card template' : `Realm social card: ${title}`,
    desc: template
      ? 'Open Graph / Discord embed card, 1200 x 630. Replace the {{...}} slots by hand, or run: node art/tools/build.mjs card --house <house> --kicker .. --title .. --subtitle .. --footer .. --out card.svg --png card.png. Text uses Cinzel and EB Garamond (OFL); export to PNG before posting.'
      : 'Social card generated by art/tools/build.mjs card.',
    defs: bg.defs + small.defs, body,
  });
}

function socialFiles() {
  return {
    'social/card-template.svg': socialCard({ kicker: '{{KICKER}}', title: '{{TITLE}}', subtitle: '{{SUBTITLE}}', footer: '{{FOOTER}}', template: true }),
    'social/card-example-coronation.svg': socialCard({ house: 'varrow', kicker: 'Coronation', title: 'The Iron Stag takes the Old Throne', subtitle: 'House Varrow holds the crown. Every other house may now raise a claim at the Hearth.', footer: 'The Chronicle of Ostreval · example card, not a real event' }),
    'social/card-example-treaty-broken.svg': socialCard({ house: 'corvane', kicker: 'Treaty broken', title: 'Corvane breaks the river truce', subtitle: 'Every secret has a price. The Chronicle remembers who paid it.', footer: 'The Chronicle of Ostreval · example card, not a real event' }),
  };
}

// ---------------------------------------------------------------- key art, badges, textures (art/tools/sets.mjs)

// The key-art scene with every id suffixed, so it can share a document with the sigils.
function sceneMarkup(suffix) {
  const sc = scene(setsContext());
  const [defs, body] = suffixIds(`${sc.defs}\u0000${sc.body}`, suffix).split('\u0000');
  return { defs, body };
}

function setsContext() {
  return {
    C, S, palette, doc, n, lockup, read,
    chargeOf: (h) => sigil(h).charge,
    throne: innerGroup(read('logo/realm-emblem.svg'), 'throne').inner,
    // an emblem ("icons/trophy" or "emblems/axe") nested at x, y, size; emblems live in art/src/emblems/
    nestEmblem: (ref, x, y, size, extra = '') => nest(read(ref.startsWith('emblems/') ? `src/${ref}.svg` : `${ref}.svg`), x, y, size, size, { extra }),
  };
}

// ---------------------------------------------------------------- icon sprite

function spriteFile() {
  const icons = walk(path.join(ART, 'icons'), (p) => p.endsWith('.svg'));
  const symbols = icons.map((f) => {
    const svg = fs.readFileSync(f, 'utf8');
    const name = path.basename(f, '.svg');
    const title = (svg.match(/<title>([\s\S]*?)<\/title>/) || [])[1] || name;
    const inner = svg.slice(svg.indexOf('>', svg.indexOf('<svg')) + 1, svg.lastIndexOf('</svg>')).replace(/<title>[\s\S]*?<\/title>/, '').trim();
    return `<symbol id="realm-icon-${name}" viewBox="0 0 24 24"><title>${title}</title><g fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">${inner}</g></symbol>`;
  });
  return `<svg xmlns="http://www.w3.org/2000/svg">
<!-- Realm event icons as one sprite. Use: <svg class='icon'><use href='sprite/icons.svg#realm-icon-crown'/></svg>
     Generated by art/tools/build.mjs from art/icons/*.svg. -->
${symbols.join('\n')}
</svg>
`;
}

function generate() {
  const files = {};
  for (const h of HOUSES) {
    const s = sigil(h);
    files[`banners/${h}.svg`] = banner(s);
    files[`shields/${h}.svg`] = shield(s);
  }
  Object.assign(files, logoFiles(), discordFiles(), socialFiles(), keyartFiles(setsContext()), badgeFiles(setsContext()), textureFiles(setsContext()));
  files['sprite/icons.svg'] = spriteFile();
  for (const [f, s] of Object.entries(files)) write(f, s);
  console.log(`generate: wrote ${Object.keys(files).length} files`);
}

// ---------------------------------------------------------------- optimize

const svgFiles = () => walk(ART, (p) => p.endsWith('.svg') && !p.includes(`${path.sep}node_modules${path.sep}`) && !p.includes(`${path.sep}png${path.sep}`));
const NO_SVGO = new Set(['sprite/icons.svg']);

async function optimize() {
  const { optimize: svgo } = await loadModule('svgo');
  const config = {
    multipass: true,
    js2svg: { pretty: true, indent: 2 },
    plugins: [
      {
        name: 'preset-default',
        params: {
          overrides: {
            removeViewBox: false, // keep every file scalable
            cleanupIds: false, // ids are namespaced by hand and used by the build (charge, emblem, throne, slot-*)
            removeTitle: false, // titles are the accessible names
            removeDesc: false, // the desc carries the lore line and licence note
            removeComments: false, // section comments document the hand-drawn shapes
            collapseGroups: false, // keeps <g id="charge"> and its transform intact
            moveGroupAttrsToElems: false,
            convertPathData: { floatPrecision: 2 },
            convertColors: { names2hex: false }, // white/black stay named: they are mask luminance, not paint
            cleanupNumericValues: { floatPrecision: 2 },
          },
        },
      },
    ],
  };
  let before = 0, after = 0, count = 0;
  for (const f of svgFiles()) {
    if (NO_SVGO.has(rel(f))) continue;
    const src = fs.readFileSync(f, 'utf8');
    let res;
    try { res = svgo(src, { ...config, path: f }); } catch (e) { throw new Error(`svgo failed on ${rel(f)}: ${e.message}`); }
    before += Buffer.byteLength(src); after += Buffer.byteLength(res.data); count++;
    if (res.data !== src) fs.writeFileSync(f, res.data);
  }
  console.log(`optimize: ${count} files, ${before} -> ${after} bytes (${before ? Math.round((100 * (before - after)) / before) : 0}% smaller)`);
}

// ---------------------------------------------------------------- PNG export

function pngSpecs(r) {
  const parts = r.split('/');
  const dir = parts[0], file = parts[parts.length - 1], sub = parts.slice(0, -1).join('/');
  const base = file.replace(/\.svg$/, '');
  const svg = read(r);
  const vb = attrsOf(svg.match(/<svg\b[^>]*>/)[0]).viewBox.split(/\s+/).map(Number);
  const [w, h] = [vb[2], vb[3]];
  const at = (scale, suffix = `@${scale}x`, color) => ({ out: `png/${sub}/${base}${suffix}.png`, w: Math.round(w * scale), h: Math.round(h * scale), color });
  switch (dir) {
    case 'sigils': return [at(2, '-512'), at(0.5, '-128')];
    case 'banners': case 'shields': return [at(2)];
    case 'icons': return [at(1, '-24', C.ember), at(2, '-48', C.ember), at(4, '-96', C.ember), at(4, '-96-ink', C.ink)];
    case 'logo':
      if (base === 'realm-emblem') return [at(4, '-1024'), at(2, '-512'), at(0.75, '-192')];
      if (base === 'realm-emblem-mono') return [at(2, '-512-gold', C.ember), at(2, '-512-ink', C.ink)];
      if (base === 'realm-favicon') return [at(1, '-32'), at(6, '-192')];
      return [at(2)];
    case 'discord': case 'social': case 'keyart': case 'textures': return [at(1, '')];
    case 'badges': return [at(1, '-256'), at(0.25, '-64')];
    default: return [];
  }
}

const pngTargets = () => svgFiles().map(rel).filter((r) => !r.startsWith('sprite/')).flatMap((r) => pngSpecs(r).map((s) => ({ ...s, src: r })));

async function fontCss() {
  if (opt.fonts) {
    const faces = [];
    for (const f of fs.readdirSync(opt.fonts).filter((x) => /\.(ttf|otf|woff2?)$/i.test(x))) {
      const family = /garamond/i.test(f) ? 'EB Garamond' : /cinzel/i.test(f) ? 'Cinzel' : null;
      if (!family) continue;
      const weight = /bold/i.test(f) && !/semi/i.test(f) ? 700 : /semi/i.test(f) ? 600 : /medium/i.test(f) ? 500 : 400;
      const data = fs.readFileSync(path.join(opt.fonts, f)).toString('base64');
      faces.push(`@font-face{font-family:'${family}';font-weight:${weight};src:url(data:font/ttf;base64,${data})}`);
    }
    return `<style>${faces.join('')}</style>`;
  }
  return '<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Cinzel:wght@600;700&family=EB+Garamond:wght@500&display=block">';
}

async function launch() {
  const { chromium } = await loadModule('playwright');
  const exe = opt.chromium || process.env.PLAYWRIGHT_CHROMIUM || (fs.existsSync('/opt/pw-browsers/chromium') ? '/opt/pw-browsers/chromium' : undefined);
  return chromium.launch(exe ? { executablePath: exe } : {});
}

async function renderOne(page, svgText, { w, h, color = '#000' }, css, needsFonts) {
  await page.setViewportSize({ width: w, height: h });
  await page.setContent(`<!doctype html><html><head><meta charset="utf-8">${css}<style>html,body{margin:0;background:transparent}body>svg{display:block;width:${w}px;height:${h}px}</style></head><body style="color:${color}">${svgText}</body></html>`, { waitUntil: 'load' });
  if (needsFonts) {
    const ok = await page.evaluate(async () => {
      await document.fonts.ready;
      const c = document.createElement('canvas').getContext('2d');
      const width = (f) => { c.font = f; return c.measureText('REALM OSTREVAL Hearth').width; };
      return width("700 40px Cinzel, monospace") !== width('700 40px monospace') && width("500 40px 'EB Garamond', monospace") !== width('500 40px monospace');
    });
    if (!ok) throw new Error('Cinzel / EB Garamond did not load: pass --fonts <dir> with the .ttf files, or allow fonts.googleapis.com');
    // social cards: the text column must not run into the sigil (which starts at x=790 of 1200)
    const over = await page.evaluate(() => [...document.querySelectorAll('text[id^="slot-"]')]
      .map((t) => ({ id: t.id, right: t.getBBox().x + t.getBBox().width }))
      .filter((t) => t.right > 770));
    if (over.length) throw new Error(`text runs into the sigil column: ${over.map((o) => `${o.id} ends at x=${Math.round(o.right)}`).join(', ')}; shorten it`);
  }
  return page.screenshot({ type: 'png', omitBackground: true, clip: { x: 0, y: 0, width: w, height: h } });
}

async function png() {
  const browser = await launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  const css = await fontCss();
  fs.rmSync(path.join(ART, 'png'), { recursive: true, force: true });
  let count = 0;
  for (const t of pngTargets()) {
    const svgText = read(t.src);
    // strict XML parse in the browser: catches anything the regex lint can miss
    const parseErr = await page.evaluate((s) => { const d = new DOMParser().parseFromString(s, 'image/svg+xml'); const e = d.querySelector('parsererror'); return e ? e.textContent : null; }, svgText);
    if (parseErr) throw new Error(`${t.src}: XML parse error: ${parseErr}`);
    const buf = await renderOne(page, svgText, t, css, /<text\b/.test(svgText));
    write(t.out, buf);
    count++;
  }
  // one contact sheet of every icon, for quick review
  await page.setViewportSize({ width: 900, height: 200 });
  const icons = walk(path.join(ART, 'icons'), (p) => p.endsWith('.svg'));
  const cell = (f, bg, fg) => `<figure style="margin:0;display:grid;justify-items:center;gap:6px;width:84px"><div style="width:72px;height:72px;display:grid;place-items:center;background:${bg};color:${fg};border-radius:8px">${fs.readFileSync(f, 'utf8').replace('<svg ', '<svg style="width:48px;height:48px" ')}</div><figcaption style="font:12px sans-serif;color:${C.parchment}">${path.basename(f, '.svg')}</figcaption></figure>`;
  const sheet = (bg, fg) => `<div style="display:grid;grid-template-columns:repeat(14,auto);gap:10px">${icons.map((f) => cell(f, bg, fg)).join('')}</div>`;
  await page.setContent(`<body style="margin:0;padding:16px;background:${C['iron-900']};display:grid;gap:22px;width:max-content">${sheet(C['iron-800'], C.ember)}${sheet(C.parchment, C.ink)}</body>`);
  const box = await page.evaluate(() => ({ w: document.body.scrollWidth, h: document.body.scrollHeight }));
  await page.setViewportSize({ width: box.w, height: box.h });
  write('png/icons/contact-sheet.png', await page.screenshot({ type: 'png', fullPage: true }));
  await browser.close();
  console.log(`png: rendered ${count + 1} PNGs into art/png/`);
}

// ---------------------------------------------------------------- check

function luminance(hex) {
  const v = parseInt(hex.slice(1), 16);
  return [v >> 16, (v >> 8) & 255, v & 255].map((c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; }).reduce((a, c, i) => a + c * [0.2126, 0.7152, 0.0722][i], 0);
}
export const contrast = (a, b) => { const x = luminance(a), y = luminance(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };

function pngSize(file) {
  const b = fs.readFileSync(file);
  if (b.readUInt32BE(0) !== 0x89504e47) return null;
  return { w: b.readUInt32BE(16), h: b.readUInt32BE(20) };
}

// Minimal well-formedness check (tags balanced, attributes quoted) that runs without a browser.
export function xmlBalanced(s) {
  const body = s.replace(/<!--[\s\S]*?-->/g, '').replace(/<!\[CDATA\[[\s\S]*?\]\]>/g, '').replace(/<\?[\s\S]*?\?>/g, '');
  const stack = [];
  for (const m of body.matchAll(/<(\/?)([A-Za-z][\w:-]*)((?:\s+[\w:-]+="[^"]*")*)\s*(\/?)>|<[^>]*>/g)) {
    if (!m[2]) return `malformed tag: ${m[0].slice(0, 60)}`;
    if (m[1]) { if (stack.pop() !== m[2]) return `unexpected </${m[2]}>`; }
    else if (!m[4]) stack.push(m[2]);
  }
  if (stack.length) return `unclosed <${stack.pop()}>`;
  if (/<[^>]*$/.test(body)) return 'unterminated tag';
  return null;
}

// The closed list of Chronicle event types, read from chronicle/server.js (null when the service is not in this checkout).
export function chronicleTypes() {
  const server = path.join(ART, '..', 'chronicle', 'server.js');
  if (!fs.existsSync(server)) return null;
  const block = (fs.readFileSync(server, 'utf8').match(/EVENT_TYPES = new Set\(\[([\s\S]*?)\]\)/) || [, ''])[1].replace(/\/\/.*$/gm, '');
  return [...block.matchAll(/'([a-z_]+)'/g)].map((m) => m[1]);
}

// Every hex colour the pack may use: brand, scene (key art), enamel (badges) and each house's colours.
export function paletteHexes() {
  const out = [...palette.brand, ...(palette.scene || []), ...(palette.enamel || [])].map((b) => b.hex.toLowerCase());
  for (const p of Object.values(palette.houses)) for (const k of HOUSE_KEYS) if (p[k]) out.push(p[k].toLowerCase());
  for (const p of Object.values(palette.houses)) for (const x of p.extra) out.push(x.toLowerCase());
  return out;
}
const HOUSE_KEYS = ['field', 'fieldLight', 'fieldDark', 'metal', 'metalLight', 'metalShadow', 'discordRole'];

// Colour keywords. Only white and black are allowed, and only inside <mask> (where they mean "show" and "hide").
export function namedColourErrors(svg) {
  const out = [];
  const outside = svg.replace(/<mask\b[\s\S]*?<\/mask>/g, '');
  const ok = new Set(['none', 'currentcolor', 'transparent', 'inherit']);
  for (const m of outside.matchAll(/\b(fill|stroke|stop-color|flood-color|lighting-color|color)="([a-zA-Z]+)"/g)) if (!ok.has(m[2].toLowerCase())) out.push(`${m[1]}="${m[2]}" outside a <mask> (use a palette hex)`);
  for (const mask of svg.match(/<mask\b[\s\S]*?<\/mask>/g) || []) for (const m of mask.matchAll(/\b(fill|stroke|stop-color|flood-color|color)="([a-zA-Z]+)"/g)) if (!ok.has(m[2].toLowerCase()) && !/^(white|black)$/i.test(m[2])) out.push(`${m[1]}="${m[2]}" in a <mask>; only white and black are allowed there`);
  return out;
}

const FORBIDDEN = [/reign of kings/i, /game of thrones/i, /song of ice/i, /westeros/i, /\bstark\b/i, /lannister/i, /targaryen/i, /baratheon/i, /\bHBO\b/, /code ?hatch/i, /iron throne/i];

function check() {
  const errors = [], warns = [];
  const err = (f, m) => errors.push(`${f}: ${m}`);
  const allowed = new Set(paletteHexes());

  const files = svgFiles();
  // byte budgets: icons stay tiny, sigils stay light enough to nest six times, scenes carry the key art
  const budget = (r) => (r.startsWith('icons/') || r.startsWith('src/emblems/') ? 1500 : r.startsWith('sigils/') ? 16000 : r.startsWith('textures/') ? 8000
    : r.startsWith('badges/') ? 24000 : r.startsWith('sprite/') ? 60000 : r.startsWith('keyart/') || r.startsWith('discord/') ? 220000 : 80000);
  for (const f of files) {
    const r = rel(f), s = fs.readFileSync(f, 'utf8');
    const bad = xmlBalanced(s); if (bad) err(r, bad);
    const root = (s.match(/<svg\b[^>]*>/) || [''])[0];
    if (!/xmlns="http:\/\/www\.w3\.org\/2000\/svg"/.test(root)) err(r, 'root <svg> lacks the SVG namespace');
    if (!r.startsWith('sprite/') && !/viewBox="[^"]+"/.test(root)) err(r, 'root <svg> has no viewBox');
    if (!r.startsWith('sprite/') && !/<title>[^<]+<\/title>/.test(s)) err(r, 'no <title> (needed for accessibility)');
    if (/<script\b|\son[a-z]+="|javascript:/i.test(s)) err(r, 'script or event handler');
    if (/<(image|foreignObject|iframe)\b/i.test(s)) err(r, 'embedded raster, foreignObject or iframe');
    for (const m of s.matchAll(/(?:xlink:)?href="([^"]*)"/g)) if (!m[1].startsWith('#')) err(r, `external reference ${m[1]}`);
    if (/@import|url\((?!#)/.test(s)) err(r, 'external url() reference');
    if (/<text\b/.test(s) && !r.startsWith('social/')) err(r, '<text> outside social/ (logos and art must be outlined so they render without fonts)');
    const ids = [...s.matchAll(/\bid="([^"]+)"/g)].map((m) => m[1]);
    const dup = ids.filter((id, i) => ids.indexOf(id) !== i);
    if (dup.length) err(r, `duplicate ids: ${[...new Set(dup)].join(', ')}`);
    for (const m of s.matchAll(/url\(#([^)]+)\)|href="#([^"]+)"/g)) if (!ids.includes(m[1] || m[2])) err(r, `reference to missing #${m[1] || m[2]}`);
    for (const m of s.matchAll(/#[0-9a-fA-F]{6}\b|#[0-9a-fA-F]{3}\b(?![0-9a-fA-F])/g)) {
      const hex = m[0].length === 4 ? '#' + [...m[0].slice(1)].map((c) => c + c).join('') : m[0];
      if (!allowed.has(hex.toLowerCase())) err(r, `colour ${m[0]} is not in art/palette.json`);
    }
    for (const e of namedColourErrors(s)) err(r, e);
    if (r.startsWith('icons/') || r.startsWith('src/emblems/')) {
      if (!/viewBox="0 0 24 24"/.test(root)) err(r, 'icons must use the 24 x 24 grid');
      if (/#[0-9a-f]{3,6}\b/i.test(s)) err(r, 'icons must use currentColor only');
      if (!/stroke-width="1\.75"/.test(root)) err(r, 'icons must set stroke-width="1.75" on the root');
      for (const m of s.matchAll(/\bstroke-width="([\d.]+)"/g)) if (m[1] !== '1.75') err(r, `icon stroke-width ${m[1]} breaks the 1.75 grid`);
    }
    if (r.startsWith('sigils/')) {
      const h = path.basename(r, '.svg');
      if (!palette.houses[h]) err(r, 'sigil for an unknown house (add it to palette.json)');
      else {
        if (!s.includes('id="charge"')) err(r, 'sigil needs <g id="charge"> (the build reuses it)');
        if (!new RegExp(`data-field="${palette.houses[h].field}"`, 'i').test(root)) err(r, `data-field must equal the overlay dye ${palette.houses[h].field}`);
        for (const id of ids) if (!id.startsWith(`${h}-`) && id !== 'charge') err(r, `id "${id}" must be prefixed "${h}-" so sigils can be combined`);
      }
    }
    if (Buffer.byteLength(s) > budget(r)) err(r, `${Buffer.byteLength(s)} bytes is over the ${budget(r)} byte budget`);
  }
  for (const h of HOUSES) for (const d of ['sigils', 'banners', 'shields']) if (!fs.existsSync(path.join(ART, d, `${h}.svg`))) err(`${d}/${h}.svg`, 'missing');

  // the overlay dye each house gets is fixed by its name: recompute it the same way the overlay does
  const common = path.join(ART, '..', 'chronicle', 'public', 'assets', 'common.js');
  if (fs.existsSync(common)) {
    const src = fs.readFileSync(common, 'utf8');
    const dyes = JSON.parse((src.match(/const DYES = (\[[^\]]+\])/) || [, '[]'])[1].replace(/'/g, '"'));
    const hash = (str) => { let x = 2166136261; for (const ch of str.toLowerCase()) x = Math.imul(x ^ ch.codePointAt(0), 16777619); return x >>> 0; };
    const shade = (hex, amt) => { const v = parseInt(hex.slice(1), 16); const mix = (c) => Math.round(amt >= 0 ? c + (255 - c) * amt : c * (1 + amt)); return `#${((1 << 24) | (mix(v >> 16) << 16) | (mix((v >> 8) & 255) << 8) | mix(v & 255)).toString(16).slice(1)}`; };
    for (const [k, p] of Object.entries(palette.houses)) {
      if (!dyes.length) { warns.push('could not read DYES from chronicle common.js'); break; }
      const dye = dyes[hash(p.name) % dyes.length];
      if (dye !== p.field) err('palette.json', `${k}.field ${p.field} but the overlay dyes "${p.name}" ${dye}`);
      if (shade(dye, 0.16) !== p.fieldLight) err('palette.json', `${k}.fieldLight should be ${shade(dye, 0.16)} (overlay --dye-light)`);
      if (shade(dye, -0.38) !== p.fieldDark) err('palette.json', `${k}.fieldDark should be ${shade(dye, -0.38)} (overlay --dye-dark)`);
    }
  } else warns.push('chronicle/public/assets/common.js not found; skipped the overlay dye check');

  // icon map: every icon it names exists; event types the Chronicle knows but the map lacks are reported
  const mapFile = path.join(ART, 'icons', 'event-map.json');
  if (fs.existsSync(mapFile)) {
    const map = JSON.parse(fs.readFileSync(mapFile, 'utf8')).icons;
    for (const [t, icon] of Object.entries(map)) if (!fs.existsSync(path.join(ART, 'icons', `${icon}.svg`))) err('icons/event-map.json', `${t} -> ${icon}.svg does not exist`);
    // every event type gets its own icon, so a feed can tell any two events apart at a glance
    const byIcon = {};
    for (const [t, icon] of Object.entries(map)) (byIcon[icon] ||= []).push(t);
    for (const [icon, ts] of Object.entries(byIcon)) if (ts.length > 1) err('icons/event-map.json', `${ts.join(', ')} share the icon ${icon}; each event type needs its own`);
    const types = chronicleTypes();
    if (types) {
      const missing = types.filter((t) => !map[t]);
      if (missing.length) warns.push(`icons/event-map.json has no icon for: ${missing.join(', ')} (new Chronicle types; draw an icon and add a mapping)`);
      for (const t of Object.keys(map)) if (!types.includes(t)) err('icons/event-map.json', `${t} is not a Chronicle event type in chronicle/server.js`);
    }
  } else err('icons/event-map.json', 'missing');

  for (const c of palette.contrast) { const v = contrast(c.fg, c.bg); if (v < c.min) err('palette.json', `${c.fg} on ${c.bg} is ${v.toFixed(2)}:1, needs ${c.min} (${c.why})`); }
  for (const e of palette.seasonEnamels || []) {
    if (!allowed.has(e.toLowerCase())) err('palette.json', `season enamel ${e} is not a palette colour`);
    const v = contrast(C.ember, e); if (v < 3) err('palette.json', `gold ${C.ember} on season enamel ${e} is ${v.toFixed(2)}:1, needs 3 for the numeral to read`);
  }
  const titlesFile = path.join(ART, 'src', 'titles.json');
  if (fs.existsSync(titlesFile)) {
    const titles = JSON.parse(fs.readFileSync(titlesFile, 'utf8')).titles;
    const ids = titles.map((t) => t.id);
    if (new Set(ids).size !== ids.length) err('src/titles.json', 'duplicate title ids');
    for (const t of titles) {
      const f = t.emblem.startsWith('emblems/') ? path.join(ART, 'src', `${t.emblem}.svg`) : path.join(ART, `${t.emblem}.svg`);
      if (!fs.existsSync(f)) err('src/titles.json', `${t.id}: emblem ${t.emblem}.svg does not exist`);
      if (!fs.existsSync(path.join(ART, 'badges', 'titles', `${t.id.replace(/_/g, '-')}.svg`))) err(`badges/titles/${t.id.replace(/_/g, '-')}.svg`, 'missing (run: node art/tools/build.mjs generate)');
    }
  }
  for (let k = 1; k <= 4; k++) if (!fs.existsSync(path.join(ART, 'badges', 'seasons', `season-${k}.svg`))) err(`badges/seasons/season-${k}.svg`, 'missing');
  for (const [k, p] of Object.entries(palette.houses)) {
    const v = contrast(p.discordRole, palette.discordBackgrounds.dark);
    if (v < palette.discordBackgrounds.minDark) err('palette.json', `${k}.discordRole ${p.discordRole} is ${v.toFixed(2)}:1 on Discord dark, needs ${palette.discordBackgrounds.minDark}`);
    const m = contrast(p.metal, p.field);
    if (m < 3) err('palette.json', `${k}: metal on field is ${m.toFixed(2)}:1, needs 3 for the charge to read`);
  }

  // brand.md must quote the palette exactly
  const brandDoc = path.join(ART, '..', 'docs', 'brand.md');
  if (fs.existsSync(brandDoc)) {
    const md = fs.readFileSync(brandDoc, 'utf8').toLowerCase();
    for (const b of [...palette.brand, ...(palette.scene || []), ...(palette.enamel || [])]) if (!md.includes(b.hex.toLowerCase())) err('docs/brand.md', `colour ${b.name} ${b.hex} is not documented`);
    for (const p of Object.values(palette.houses)) for (const hx of [p.field, p.metal, p.discordRole]) if (!md.includes(hx.toLowerCase())) err('docs/brand.md', `${p.name} colour ${hx} is not documented`);
    const docAllowed = new Set([...allowed, palette.discordBackgrounds.dark, palette.discordBackgrounds.light]);
    for (const m of md.matchAll(/#[0-9a-f]{6}\b/g)) if (!docAllowed.has(m[0])) err('docs/brand.md', `mentions ${m[0]}, which is not in art/palette.json`);
  } else err('docs/brand.md', 'missing');

  // no third-party franchise names anywhere in the pack or the brand guide
  const texts = walk(ART, (p) => /\.(svg|json|md|html|mjs)$/.test(p) && !p.includes('node_modules'));
  if (fs.existsSync(brandDoc)) texts.push(brandDoc);
  for (const f of texts) {
    const s = fs.readFileSync(f, 'utf8').replace(/<!-- trademark-guard-ok -->[\s\S]*?<!-- \/trademark-guard-ok -->/g, '');
    for (const re of FORBIDDEN) if (re.test(s) && !f.endsWith('build.mjs')) err(path.relative(path.join(ART, '..'), f), `mentions a third-party franchise (${re})`);
  }

  // PNG previews exist at the right pixel size
  if (!opt['skip-png']) {
    for (const t of pngTargets()) {
      const p = path.join(ART, t.out);
      if (!fs.existsSync(p)) { err(t.out, 'missing PNG (run: node art/tools/build.mjs png)'); continue; }
      const d = pngSize(p);
      if (!d || d.w !== t.w || d.h !== t.h) err(t.out, `PNG is ${d ? `${d.w}x${d.h}` : 'unreadable'}, expected ${t.w}x${t.h}`);
    }
  }
  if (!fs.existsSync(path.join(ART, 'index.html'))) err('index.html', 'missing (run: node art/tools/build.mjs gallery)');

  for (const w of warns) console.warn(`warn: ${w}`);
  if (errors.length) { for (const e of errors) console.error(`error: ${e}`); console.error(`check: ${errors.length} error(s)`); process.exitCode = 1; }
  else console.log(`check: ${files.length} SVGs, ${opt['skip-png'] ? 'PNGs skipped' : `${pngTargets().length} PNGs`}, palette, contrast, overlay dyes, brand.md and trademark guard: OK`);
}

// ---------------------------------------------------------------- gallery

function gallery() {
  const exists = (p) => fs.existsSync(path.join(ART, p));
  const pngsFor = (r) => pngSpecs(r).map((s) => s.out).filter(exists);
  const pngLabel = (r, p) => p.split('/').pop().replace(/\.png$/, '').slice(path.basename(r, '.svg').length).replace(/^[-@]/, '') || 'PNG';
  const links = (r) => `<a href="${r}">SVG</a>${pngsFor(r).map((p) => ` · <a href="${p}">${esc(pngLabel(r, p))}</a>`).join('')}`;
  const tile = (r, label, cls = '') => `<figure class="tile ${cls}"><div class="frame"><img src="${r}" alt="${esc(label)}"></div><figcaption><b>${esc(label)}</b><span>${links(r)}</span></figcaption></figure>`;
  const sw = (hex, name, use = '') => `<li class="sw"><span style="background:${hex}"></span><b>${esc(name)}</b><code>${hex}</code>${use ? `<small>${esc(use)}</small>` : ''}</li>`;

  const houses = HOUSES.map((h) => {
    const p = palette.houses[h];
    return `<article class="house" style="--dye:${p.field};--dye-light:${p.fieldLight};--dye-dark:${p.fieldDark};--metal:${p.metal}">
  <header><h3>House ${esc(p.name)}</h3><p class="words">“${esc(p.words)}”</p><p class="sig">The ${esc(p.sigil)}</p></header>
  <div class="trio">${tile(`sigils/${h}.svg`, 'Sigil')}${tile(`banners/${h}.svg`, 'Banner')}${tile(`shields/${h}.svg`, 'Shield')}</div>
  <ul class="swatches">${sw(p.field, 'Field (overlay dye)')}${sw(p.metal, `Metal: ${p.metalName}`)}${sw(p.discordRole, 'Discord role')}</ul>
</article>`;
  }).join('\n');

  const eventMap = JSON.parse(read('icons/event-map.json')).icons;
  const typesFor = (name) => Object.entries(eventMap).filter(([, i]) => i === name).map(([t]) => t);
  const icons = walk(path.join(ART, 'icons'), (p) => p.endsWith('.svg')).map((f) => {
    const r = rel(f), name = path.basename(f, '.svg');
    const svg = fs.readFileSync(f, 'utf8');
    const title = (svg.match(/<title>([\s\S]*?)<\/title>/) || [])[1] || name;
    const types = typesFor(name);
    return `<figure class="icon-tile"><div class="ic">${svg.replace(/<\?xml[^>]*>/, '').replace('<svg ', '<svg aria-hidden="true" ')}</div><figcaption><b>${esc(name)}</b><small>${esc(title)}</small>${types.length ? `<code>${types.map(esc).join(', ')}</code>` : ''}<span>${links(r)}</span></figcaption></figure>`;
  }).join('\n');
  const titleList = JSON.parse(read('src/titles.json')).titles;
  const seasonTiles = [1, 2, 3, 4].map((k) => tile(`badges/seasons/season-${k}.svg`, `Season ${k}`)).join('\n');
  const titleTiles = titleList.map((t) => tile(`badges/titles/${t.id.replace(/_/g, '-')}.svg`, `${t.name}${t.infamous ? ' (infamy)' : ''}`)).join('\n');

  const logos = ['realm-logo-horizontal-dark', 'realm-logo-horizontal-light', 'realm-logo-stacked-dark', 'realm-logo-stacked-light', 'realm-wordmark-dark', 'realm-wordmark-light', 'realm-emblem', 'realm-emblem-mono', 'realm-favicon']
    .map((b) => tile(`logo/${b}.svg`, b.replace(/^realm-/, '').replace(/-/g, ' '), /light|mono/.test(b) ? 'on-light' : '')).join('\n');

  const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Realm Heraldry</title>
<meta name="description" content="The Realm of Ostreval art pack: house sigils, banners, shields, logo, event icons, key art, badges, textures, Discord and social art.">
<link rel="icon" href="logo/realm-favicon.svg" type="image/svg+xml">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Cinzel:wght@600;700&family=EB+Garamond:ital,wght@0,500;1,500&display=swap">
<style>
:root{--bg:${C['iron-900']};--panel:${C['iron-800']};--raised:${C['iron-700']};--line:${C['iron-600']};--text:${C.parchment};--muted:${C['iron-200']};--gold:${C.ember};--tile:${C['iron-950']};
--display:Cinzel,'Trajan Pro',Georgia,serif;--body:'EB Garamond',Garamond,Georgia,serif}
:root[data-theme="light"]{--bg:${C.parchment};--panel:${C['parchment-2']};--raised:${C['parchment-2']};--line:${C['parchment-edge']};--text:${C.ink};--muted:${C['ink-soft']};--gold:${C['ember-deep']};--tile:${C['iron-900']}}
@media (prefers-color-scheme: light){:root:not([data-theme="dark"]){--bg:${C.parchment};--panel:${C['parchment-2']};--raised:${C['parchment-2']};--line:${C['parchment-edge']};--text:${C.ink};--muted:${C['ink-soft']};--gold:${C['ember-deep']};--tile:${C['iron-900']}}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:19px/1.5 var(--body)}
a{color:var(--gold)}
code{overflow-wrap:anywhere}
main{max-width:1180px;margin:0 auto;padding:24px 16px 80px}
.top{display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap;border-bottom:1px solid var(--line);padding-bottom:18px}
.top img{height:84px;max-width:100%}
.top .light-only{display:none}
:root[data-theme="light"] .top .dark-only{display:none}:root[data-theme="light"] .top .light-only{display:block}
@media (prefers-color-scheme: light){:root:not([data-theme="dark"]) .top .dark-only{display:none}:root:not([data-theme="dark"]) .top .light-only{display:block}}
button{font:600 14px var(--display);letter-spacing:.08em;background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:6px;padding:8px 14px;cursor:pointer}
h1,h2,h3{font-family:var(--display);font-weight:700;letter-spacing:.04em;margin:0}
h2{font-size:24px;margin:48px 0 6px;color:var(--gold)}
h2+p{margin:0 0 18px;color:var(--muted)}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:16px}
.tile{margin:0;background:var(--panel);border:1px solid var(--line);border-radius:10px;overflow:hidden;display:flex;flex-direction:column}
.tile .frame{background:var(--tile);height:240px;display:grid;place-items:center;padding:16px;min-width:0}
.tile.on-light .frame{background:${C.parchment}}
.tile img{max-width:100%;max-height:208px;width:auto;height:auto;min-width:0;object-fit:contain;display:block}
.wide img{max-height:none}
.tile figcaption{padding:10px 12px;display:grid;gap:2px;font-size:15px}
.tile figcaption b{font:600 14px var(--display);letter-spacing:.06em;text-transform:capitalize}
.tile figcaption span{color:var(--muted)}
.houses{display:grid;gap:18px}
.house{background:linear-gradient(135deg,var(--dye-dark),var(--dye) 60%,var(--dye-light));border-radius:12px;padding:18px;color:${C.parchment};border:1px solid ${C['iron-950']}}
.house header{display:flex;gap:6px 18px;align-items:baseline;flex-wrap:wrap;margin-bottom:12px}
.house h3{font-size:22px}
.house .words{margin:0;font-style:italic}
.house .sig{margin:0;color:var(--metal);font-family:var(--display);font-size:14px;letter-spacing:.08em}
.trio{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}
.house .tile{background:rgba(12,13,15,.55);border-color:rgba(0,0,0,.4);color:${C.parchment}}
.house .tile .frame{background:rgba(12,13,15,.35)}
.house .tile figcaption span{color:${C['parchment-2']}}
.house a{color:${C['ember-hot']}}
.swatches{list-style:none;padding:0;margin:12px 0 0;display:flex;flex-wrap:wrap;gap:8px 18px}
.sw{display:flex;align-items:center;gap:8px;font-size:15px;flex-wrap:wrap}
.sw span{width:26px;height:26px;border-radius:6px;border:1px solid rgba(0,0,0,.5);box-shadow:inset 0 0 0 1px rgba(255,255,255,.12)}
.sw code{font-size:13px;opacity:.85}
.sw small{flex-basis:100%;color:var(--muted);font-size:14px;padding-left:34px}
.palette{list-style:none;padding:0;display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:12px 18px}
.icons{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px}
.icon-tile{margin:0;display:grid;grid-template-columns:auto 1fr;gap:10px;align-items:center;background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:10px}
.ic{display:flex;gap:8px;align-items:center;color:var(--gold)}
.ic svg{width:48px;height:48px}
.icon-tile figcaption{display:grid;font-size:14px;line-height:1.3}
.icon-tile b{font:600 14px var(--display)}
.icon-tile small,.icon-tile span{color:var(--muted)}
.icon-tile code{font-size:12px;color:var(--gold)}
.badges{grid-template-columns:repeat(auto-fill,minmax(170px,1fr))}
.badges .tile .frame{height:180px}.badges .tile img{max-height:150px}
.wide{grid-column:1/-1}
.wide .frame{height:auto;max-height:none}
pre{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:12px;overflow:auto;font-size:14px}
footer{margin-top:56px;color:var(--muted);font-size:15px;border-top:1px solid var(--line);padding-top:16px}
@media (max-width:640px){.trio{grid-template-columns:1fr 1fr}.top img{height:56px}.tile .frame{height:180px}.tile img{max-height:148px}.wide img{max-height:none}}
</style>
</head>
<body>
<main>
<div class="top">
  <img class="dark-only" src="logo/realm-logo-horizontal-dark.svg" alt="Realm">
  <img class="light-only" src="logo/realm-logo-horizontal-light.svg" alt="Realm">
  <button type="button" id="theme">Toggle parchment / iron</button>
</div>
<p>The heraldry of the Realm of Ostreval: original art for the six great houses, the Realm logo, Chronicle event icons, key art, season and title badges, textures, and Discord and social images. Every file is an SVG; PNG exports are in <code>png/</code>. Usage rules are in <a href="../docs/brand.md">docs/brand.md</a>.</p>

<h2>The six great houses</h2>
<p>Field colours are the exact dyes the Chronicle overlay gives each house name, so these match what viewers see on stream.</p>
<section class="houses">
${houses}
</section>

<h2>Logo</h2>
<p>Use the dark versions on iron and dark photos, and the light versions on parchment or white. Keep clear space of half the height of the REALM capitals on every side. The one-colour emblem takes the text colour; in an image tag it falls back to black, as shown here.</p>
<section class="grid">
${logos}
</section>

<h2>Event icons</h2>
<p>One icon for each of the Chronicle's event types (listed under each icon). 24px grid, 1.75 stroke, <code>currentColor</code>. They take the text colour of wherever they are placed. A sprite of all icons is at <a href="sprite/icons.svg">sprite/icons.svg</a>; a contact sheet is at <a href="png/icons/contact-sheet.png">png/icons/contact-sheet.png</a>.</p>
<section class="icons">
${icons}
</section>

<h2>Key art</h2>
<p>The Old Throne on its hill above the Hearth at dusk, with the banners of the six great houses along the road. For the launcher, the portal and Discord: 1920 &times; 1080 for splash screens and headers, 1200 &times; 630 for link previews. The <code>-title</code> versions carry the logo on a dark scrim.</p>
<section class="grid">
${tile('keyart/old-throne-1920.svg', 'Key art 1920 × 1080', 'wide')}
${tile('keyart/old-throne-1920-title.svg', 'Key art with logo 1920 × 1080', 'wide')}
${tile('keyart/old-throne-1200.svg', 'Key art 1200 × 630')}
${tile('keyart/old-throne-1200-title.svg', 'Key art with logo 1200 × 630')}
</section>

<h2>Season badges</h2>
<p>One medal per season: the numeral in gold on an enamel that cycles every four seasons. 256px, with 64px PNGs.</p>
<section class="grid badges">
${seasonTiles}
</section>

<h2>Renown title badges</h2>
<p>One badge for each default title in RealmRenown. Honours are gold on iron; infamous titles are bone on blood in blackened iron. Titles a server adds in the plugin config have no badge until one is added to <code>src/titles.json</code>.</p>
<section class="grid badges">
${titleTiles}
</section>

<h2>Textures</h2>
<p>Tileable 512px patterns for backgrounds: <code>background: #ecdfbf url(textures/parchment.svg)</code> and <code>background: #131417 url(textures/iron.svg)</code>.</p>
<section class="grid">
${tile('textures/parchment.svg', 'Parchment')}
${tile('textures/iron.svg', 'Dark iron')}
</section>

<h2>Discord</h2>
<p>Upload the PNGs, not the SVGs. The server icon is shown as a circle. Sigil PNGs at 128px (<code>png/sigils/*-128.png</code>) work as server emoji.</p>
<section class="grid">
${tile('discord/server-icon.svg', 'Server icon 512')}
${tile('discord/server-banner.svg', 'Server banner 960 × 540', 'wide')}
${tile('discord/invite-splash.svg', 'Invite splash 1920 × 1080', 'wide')}
</section>

<h2>Social card</h2>
<p>1200 × 630, for link previews and announcements. The template's text needs Cinzel and EB Garamond; post the PNG. Make a filled card with:</p>
<pre>node art/tools/build.mjs card --house dunmere --kicker "Claim declared" \\
  --title "Dunmere raises a claim at the Hearth" --subtitle "The Lawful Hours open on Saturday." \\
  --footer "The Chronicle of Ostreval" --out card.svg --png card.png</pre>
<section class="grid">
${tile('social/card-template.svg', 'Template', 'wide')}
${tile('social/card-example-coronation.svg', 'Example: coronation', 'wide')}
${tile('social/card-example-treaty-broken.svg', 'Example: treaty broken', 'wide')}
</section>

<h2>Palette</h2>
<p>Brand colours match the Chronicle overlay tokens in <code>chronicle/public/assets/theme.css</code>. The full list is in <a href="palette.json">palette.json</a>.</p>
<ul class="palette">
${palette.brand.map((b) => sw(b.hex, b.name, b.use)).join('\n')}
</ul>

<footer>Original artwork for the Realm community server. The wordmark letters are outlined from Cinzel (SIL Open Font License 1.1). Generated by <code>art/tools/build.mjs gallery</code>; do not edit by hand.</footer>
</main>
<script>
(function () {
  var root = document.documentElement, key = 'realm-art-theme';
  try { var saved = localStorage.getItem(key); if (saved) root.setAttribute('data-theme', saved); } catch (e) {}
  document.getElementById('theme').addEventListener('click', function () {
    var cur = root.getAttribute('data-theme') || (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark');
    var next = cur === 'light' ? 'dark' : 'light';
    root.setAttribute('data-theme', next);
    try { localStorage.setItem(key, next); } catch (e) {}
  });
})();
</script>
</body>
</html>
`;
  write('index.html', html);
  console.log('gallery: wrote art/index.html');
}

// ---------------------------------------------------------------- card (CLI)

async function card() {
  if (!opt.title || !opt.out) { console.error('usage: build.mjs card --title ".." --out card.svg [--house <house>] [--kicker ..] [--subtitle ..] [--footer ..] [--png card.png]'); process.exit(2); }
  if (opt.house && !palette.houses[opt.house]) { console.error(`unknown house "${opt.house}"; one of: ${HOUSES.join(', ')}`); process.exit(2); }
  const svg = socialCard({ house: opt.house || null, kicker: opt.kicker || 'The Chronicle', title: opt.title, subtitle: opt.subtitle || '', footer: opt.footer || '' });
  fs.writeFileSync(opt.out, svg);
  console.log(`card: wrote ${opt.out}`);
  if (opt.png) {
    const browser = await launch();
    const page = await browser.newPage();
    fs.writeFileSync(opt.png, await renderOne(page, svg, { w: 1200, h: 630 }, await fontCss(), true));
    await browser.close();
    console.log(`card: wrote ${opt.png}`);
  }
}

const steps = { generate, optimize, png, gallery, check, card };
const isMain = process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname);
if (!isMain) { /* imported as a module: run nothing */ }
else if (cmd === 'all') { generate(); await optimize(); await png(); gallery(); check(); }
else if (steps[cmd]) await steps[cmd]();
else { console.error(`unknown command "${cmd}"; one of: all, ${Object.keys(steps).join(', ')}`); process.exit(2); }
