// What the painter renders: the finished paintings (whole signs), the sprites the plugin composes live boards from,
// and the glyph atlases for live text. Every design is original Realm art built from the pack in art/ and the
// brand fonts (Cinzel, EB Garamond; SIL OFL 1.1). Colours come from art/palette.json only.
import fs from 'node:fs';
import path from 'node:path';
import { ART } from '../lib.mjs';

export const REPO = path.resolve(ART, '..');
export const OUT = path.join(ART, 'paintings');
export const FONT_DIR = path.join(REPO, 'launcher', 'renderer', 'fonts');

export const palette = JSON.parse(fs.readFileSync(path.join(ART, 'palette.json'), 'utf8'));
const hex = (name) => palette.brand.find((b) => b.name === name).hex;
export const C = {
  iron950: hex('Iron 950'), iron900: hex('Iron 900'), iron800: hex('Iron 800'), iron700: hex('Iron 700'), iron600: hex('Iron 600'),
  iron200: hex('Iron 200'), parchment: hex('Parchment'), parchment2: hex('Parchment 2'), edge: hex('Parchment edge'),
  ink: hex('Ink'), inkSoft: hex('Ink soft'), ember: hex('Ember'), emberHot: hex('Ember hot'), emberDeep: hex('Ember deep'),
  blood: hex('Blood'), moss: hex('Moss'),
};

export const HOUSES = ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin'];

// Size caps. The game stores a painting as a PNG of at most 1024 px a side; the plugin caps its own output lower
// (RealmPainter config MaxImageSide). Finished paintings stay well under both so a sign update is a small packet.
export const LIMITS = { maxSide: 512, maxPaintingBytes: 160 * 1024, maxSpriteBytes: 48 * 1024, maxAtlasBytes: 64 * 1024, maxBundleBytes: 3 * 1024 * 1024 };

const read = (rel) => fs.readFileSync(path.join(ART, rel), 'utf8');
const dataUrl = (mime, buf) => `data:${mime};base64,${Buffer.from(buf).toString('base64')}`;
const svgUrl = (rel) => dataUrl('image/svg+xml', read(rel));
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

export function fontCss() {
  const f = (file) => dataUrl('font/woff2', fs.readFileSync(path.join(FONT_DIR, file)));
  return `@font-face{font-family:"Cinzel";src:url(${f('cinzel.woff2')}) format("woff2");font-weight:400 900;font-display:block}
@font-face{font-family:"EB Garamond";src:url(${f('ebgaramond.woff2')}) format("woff2");font-weight:400 800;font-display:block}
@font-face{font-family:"EB Garamond";src:url(${f('ebgaramond-italic.woff2')}) format("woff2");font-style:italic;font-weight:400 800;font-display:block}`;
}

// An icon from art/icons inline, so its currentColor strokes take the colour we give it.
function icon(name, size, color) {
  const svg = read(`icons/${name}.svg`).replace(/<title>[\s\S]*?<\/title>/, '')
    .replace('<svg ', `<svg style="width:${size}px;height:${size}px;color:${color};display:block" `);
  return svg.replace(/ width="24" height="24"/, '');
}

// The Ironbreaker's mark: a war hammer with a split anvil-head, drawn for this pack (no source art exists yet).
export function hammerSvg(size, metal = C.iron200, shadow = C.iron600, gold = C.ember) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128" style="width:${size}px;height:${size}px;display:block">
  <g stroke="${C.iron950}" stroke-width="2.5" stroke-linejoin="round">
    <path d="M58 52h12l2 58H56z" fill="${C.emberDeep}"/>
    <path d="M57 62h14M57 72h14M57 82h14M57 92h14M57 102h14" stroke="${C.ink}" stroke-width="2"/>
    <path d="M54 110h20l-3 10h-14z" fill="${metal}"/>
    <path d="M14 20l8-6h30v42H22l-8-6z" fill="${metal}"/>
    <path d="M114 20l-8-6H76v42h30l8-6z" fill="${metal}"/>
    <path d="M14 50l8 6h30v-8H20zM114 50l-8 6H76v-8h30z" fill="${shadow}"/>
    <path d="M50 10h28v50H50z" fill="${shadow}"/>
    <path d="M54 14h20v42H54z" fill="${metal}"/>
    <path d="M24 26h24M80 26h24" stroke="${shadow}" stroke-width="2"/>
    <circle cx="64" cy="35" r="6" fill="${gold}"/>
    <path d="M96 22l-6 10 6 6-4 8" fill="none" stroke="${C.iron950}" stroke-width="2"/>
  </g>
</svg>`;
}

const parchmentBg = () => `background:${C.parchment} url(${svgUrl('textures/parchment.svg')}) 0 0/256px 256px`;
const ironBg = () => `background:${C.iron900} url(${svgUrl('textures/iron.svg')}) 0 0/256px 256px`;

function frame(w, h, light = true) {
  const outer = light ? C.inkSoft : C.ember;
  const inner = light ? C.edge : C.iron600;
  return `<div style="position:absolute;inset:6px;border:2px solid ${outer}"></div><div style="position:absolute;inset:11px;border:1px solid ${inner}"></div>`;
}

const page = (w, h, bg, body) => `<!doctype html><html><head><meta charset="utf-8"><style>${fontCss()}
html,body{margin:0;width:${w}px;height:${h}px;overflow:hidden;background:transparent}
.p{position:relative;width:${w}px;height:${h}px;${bg};display:flex;flex-direction:column;align-items:center;box-sizing:border-box}
.k{font:600 11px Cinzel;letter-spacing:.22em;text-transform:uppercase}
.t{font:700 26px/1.05 Cinzel;letter-spacing:.04em;text-align:center}
.w{font:italic 500 18px/1.2 "EB Garamond";text-align:center}
.b{font:500 15px/1.25 "EB Garamond";text-align:center}
.rule{width:60%;height:1px;margin:8px 0}
</style></head><body>${body}</body></html>`;

function crestHtml(h) {
  const p = palette.houses[h];
  const W = 256, H = 320;
  return page(W, H, parchmentBg(), `<div class="p">${frame(W, H)}
  <div class="k" style="color:${C.inkSoft};margin-top:24px">The Realm of Ostreval</div>
  <img src="${svgUrl(`sigils/${h}.svg`)}" style="width:150px;height:150px;margin-top:10px">
  <div class="t" style="color:${C.ink};margin-top:8px">House ${esc(p.name)}</div>
  <div class="k" style="color:${C.emberDeep};margin-top:4px">The ${esc(p.sigil)}</div>
  <div class="rule" style="background:${C.edge}"></div>
  <div class="w" style="color:${C.inkSoft};width:200px">&ldquo;${esc(p.words)}&rdquo;</div></div>`);
}

const EVENTS = [
  { id: 'crown-night', title: 'Crown Night', icon: 'crown', tone: C.blood, line: 'The night the Old Throne is fought for.', foot: 'A house with a claim may take the throne' },
  { id: 'royal-tournament', title: 'The Royal Tournament', icon: 'trophy', tone: C.emberDeep, line: 'Steel against steel for the glory of your house.', foot: 'Join with /tourney join' },
  { id: 'kings-hunt', title: "The King's Hunt", icon: 'hunt', tone: C.moss, line: 'The crown names its quarry. Whoever takes them is rewarded.', foot: 'See the quarry with /hunt' },
  { id: 'truce', title: 'The Truce of the Realm', icon: 'sheathed', tone: C.inkSoft, line: 'No blade is drawn. Breaking the truce shames your house.', foot: 'Ask whether it holds with /truce' },
];

function eventHtml(e) {
  const W = 256, H = 320;
  return page(W, H, parchmentBg(), `<div class="p">${frame(W, H)}
  <div class="k" style="color:${C.inkSoft};margin-top:26px">By order of the crown</div>
  <div style="margin-top:14px">${icon(e.icon, 92, e.tone)}</div>
  <div class="t" style="color:${C.ink};margin-top:12px;width:220px">${esc(e.title)}</div>
  <div class="rule" style="background:${C.edge}"></div>
  <div class="w" style="color:${C.inkSoft};width:206px">${esc(e.line)}</div>
  <div class="b" style="color:${C.ink};position:absolute;bottom:24px;width:210px;font-size:14px">${esc(e.foot)}</div></div>`);
}

function welcomeHtml() {
  const W = 320, H = 256;
  return page(W, H, ironBg(), `<div class="p">${frame(W, H, false)}
  <img src="${svgUrl('logo/realm-emblem.svg')}" style="width:88px;height:88px;margin-top:20px">
  <div class="k" style="color:${C.iron200};margin-top:8px">Welcome to the realm of</div>
  <div class="t" style="color:${C.parchment};margin-top:4px;font-size:32px">Ostreval</div>
  <div class="rule" style="background:${C.ember};width:40%"></div>
  <div class="b" style="color:${C.parchment2};width:260px">Six houses. One Old Throne. Type <span style="font-weight:800;color:${C.emberHot}">/realm</span> in chat to begin.</div></div>`);
}

function ironbreakerHtml() {
  const W = 256, H = 320;
  return page(W, H, ironBg(), `<div class="p">${frame(W, H, false)}
  <div class="k" style="color:${C.iron200};margin-top:26px">A legend of the realm</div>
  <div style="margin-top:12px">${hammerSvg(120)}</div>
  <div class="t" style="color:${C.emberHot};margin-top:10px;font-size:30px">Ironbreaker</div>
  <div class="rule" style="background:${C.ember};width:50%"></div>
  <div class="w" style="color:${C.parchment2};width:210px">Whoever bears it is known to every house.</div></div>`);
}

const single = (rel, w, h, bg = 'transparent') => `<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;background:${bg}}img{display:block;width:${w}px;height:${h}px}</style></head><body><img src="${svgUrl(rel)}"></body></html>`;
const tile = (bgCss, s) => `<!doctype html><html><head><style>html,body{margin:0}div{width:${s}px;height:${s}px;${bgCss}}</style></head><body><div></div></body></html>`;
const maskHtml = (inner, s) => `<!doctype html><html><head><style>html,body{margin:0;background:transparent}body>*{display:block}</style></head><body style="width:${s}px;height:${s}px">${inner}</body></html>`;

// Finished paintings: an admin binds a sign to one of these with /paint <id>.
export function paintings() {
  const list = [];
  for (const h of HOUSES) {
    const p = palette.houses[h];
    list.push({ id: `sigil-${h}`, title: `House ${p.name} sigil, the ${p.sigil}`, group: 'sigils', width: 256, height: 256, html: () => single(`sigils/${h}.svg`, 256, 256) });
  }
  for (const h of HOUSES) {
    const p = palette.houses[h];
    list.push({ id: `banner-${h}`, title: `House ${p.name} banner`, group: 'banners', width: 160, height: 272, html: () => single(`banners/${h}.svg`, 160, 272) });
  }
  for (const h of HOUSES) {
    const p = palette.houses[h];
    list.push({ id: `crest-${h}`, title: `House ${p.name} notice: sigil, name and words`, group: 'crests', width: 256, height: 320, html: () => crestHtml(h) });
  }
  for (const e of EVENTS) list.push({ id: `poster-${e.id}`, title: `${e.title} poster`, group: 'posters', width: 256, height: 320, html: () => eventHtml(e) });
  list.push({ id: 'poster-welcome', title: 'Welcome to Ostreval', group: 'posters', width: 320, height: 256, html: welcomeHtml });
  list.push({ id: 'poster-ironbreaker', title: 'Ironbreaker (static; the live board names its bearer)', group: 'posters', width: 256, height: 320, html: ironbreakerHtml });
  list.push({ id: 'realm-emblem', title: 'The Realm emblem: the crown in the Old Throne', group: 'emblems', width: 256, height: 256, html: () => single('logo/realm-emblem.svg', 256, 256) });
  return list;
}

// Icons the live boards draw large (event and proclamation boards).
export const BIG_ICONS = ['crown', 'decree', 'contract', 'trophy', 'hunt', 'sheathed', 'helm', 'beacon', 'dagger', 'scales', 'laurel', 'law'];

// Sprites: pieces the plugin composes live boards from. "rgba" sprites keep their colour; "mask" sprites are
// alpha only and are tinted by the plugin.
export function sprites() {
  const list = [
    { id: 'tile-parchment', kind: 'rgba', width: 128, height: 128, html: () => tile(`background:${C.parchment} url(${svgUrl('textures/parchment.svg')}) 0 0/128px 128px`, 128) },
    { id: 'tile-iron', kind: 'rgba', width: 128, height: 128, html: () => tile(`background:${C.iron900} url(${svgUrl('textures/iron.svg')}) 0 0/128px 128px`, 128) },
    { id: 'emblem-96', kind: 'rgba', width: 96, height: 96, html: () => single('logo/realm-emblem.svg', 96, 96) },
    { id: 'hammer-128', kind: 'rgba', width: 128, height: 128, html: () => maskHtml(hammerSvg(128), 128) },
  ];
  for (const h of HOUSES) list.push({ id: `sigil-96-${h}`, kind: 'rgba', width: 96, height: 96, html: () => single(`sigils/${h}.svg`, 96, 96) });
  const iconNames = fs.readdirSync(path.join(ART, 'icons')).filter((f) => f.endsWith('.svg')).map((f) => f.slice(0, -4)).sort();
  for (const n of iconNames) list.push({ id: `icon-24-${n}`, kind: 'mask', width: 24, height: 24, html: () => maskHtml(icon(n, 24, '#ffffff'), 24) });
  for (const n of BIG_ICONS) list.push({ id: `icon-64-${n}`, kind: 'mask', width: 64, height: 64, html: () => maskHtml(icon(n, 64, '#ffffff'), 64) });
  return list;
}

// Glyph atlases for live text. Sizes are pixels on the board canvas the plugin composes (see RealmPainter.md).
export const FACES = [
  { id: 'title', family: 'Cinzel', weight: 700, style: 'normal', size: 30 },
  { id: 'head', family: 'Cinzel', weight: 700, style: 'normal', size: 20 },
  { id: 'label', family: 'Cinzel', weight: 600, style: 'normal', size: 13 },
  { id: 'body', family: 'EB Garamond', weight: 500, style: 'normal', size: 17 },
  { id: 'small', family: 'EB Garamond', weight: 500, style: 'normal', size: 14 },
  { id: 'italic', family: 'EB Garamond', weight: 500, style: 'italic', size: 17 },
];

// ASCII, Latin-1 and the punctuation player names and Chronicle titles use. Anything else is folded by the plugin
// (accents dropped, then '?').
export const CHARSET = (() => {
  const cps = [];
  for (let c = 32; c <= 126; c++) cps.push(c);
  for (let c = 160; c <= 255; c++) cps.push(c);
  cps.push(0x2013, 0x2014, 0x2018, 0x2019, 0x201c, 0x201d, 0x2022, 0x2026);
  return cps;
})();

export const FONT_LICENSES = [
  { family: 'Cinzel', file: 'OFL-Cinzel.txt', copyright: 'Copyright 2020 The Cinzel Project Authors', license: 'SIL Open Font License 1.1' },
  { family: 'EB Garamond', file: 'OFL-EBGaramond.txt', copyright: 'Copyright 2017 The EB Garamond Project Authors', license: 'SIL Open Font License 1.1' },
];
