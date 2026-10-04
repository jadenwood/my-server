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

// The Crossing of the Grey Water (sign G1 on the Gatehouse's back wall, docs/arrival-design.md 4.3): the ferrymen's
// raft on grey water, the pale Gatehouse of the Unwritten on the far shore with the blank page over its gate, and the
// Hearth's smoke rising beyond it. Original; flat shapes and vertical gradients only, so the PNG stays small.
export function crossingSvg(W = 320, H = 256) {
  const pal = palette.scene.reduce((o, c) => ({ ...o, [c.name]: c.hex }), {});
  const night = pal.Night, dusk = pal.Dusk, ridge = pal['Far ridge'], haze = pal.Haze, glow = pal.Afterglow;
  const hz = 150;                              // the far shore's waterline
  // The Gatehouse on the far shore, centred a little right of the middle.
  const gx = 176, gw = 74, wallTop = hz - 30, towerTop = hz - 44, gateW = 16, gateH = 20;
  const gate = `
    <rect x="${gx - gw / 2}" y="${wallTop}" width="${gw}" height="${hz - wallTop}" fill="${C.parchment2}"/>
    <rect x="${gx - gw / 2}" y="${wallTop + 12}" width="${gw}" height="2" fill="${C.ember}"/>
    <rect x="${gx - gw / 2 - 2}" y="${towerTop}" width="16" height="${hz - towerTop}" fill="${C.parchment2}"/>
    <rect x="${gx + gw / 2 - 14}" y="${towerTop}" width="16" height="${hz - towerTop}" fill="${C.parchment2}"/>
    <rect x="${gx - gw / 2 - 2}" y="${towerTop + 12}" width="16" height="2" fill="${C.ember}"/>
    <rect x="${gx + gw / 2 - 14}" y="${towerTop + 12}" width="16" height="2" fill="${C.ember}"/>
    <path d="M${gx - gw / 2 - 3} ${towerTop}h18v-4h-3v-3h-4v3h-4v-3h-4v3h-3z M${gx + gw / 2 - 15} ${towerTop}h18v-4h-3v-3h-4v3h-4v-3h-4v3h-3z" fill="${C.iron800}"/>
    <rect x="${gx - gw / 2 - 2}" y="${towerTop}" width="16" height="4" fill="${C.iron800}"/>
    <rect x="${gx + gw / 2 - 14}" y="${towerTop}" width="16" height="4" fill="${C.iron800}"/>
    <rect x="${gx - gw / 2 + 5}" y="${towerTop + 18}" width="2" height="6" fill="${C.iron900}"/>
    <rect x="${gx + gw / 2 - 7}" y="${towerTop + 18}" width="2" height="6" fill="${C.iron900}"/>
    <path d="M${gx - gateW / 2 - 3} ${hz} V${hz - gateH} L${gx} ${hz - gateH - 13} L${gx + gateW / 2 + 3} ${hz - gateH} V${hz}z" fill="${C.iron600}"/>
    <path d="M${gx - gateW / 2 + 2} ${hz - gateH - 1} L${gx} ${hz - gateH - 10} L${gx + gateW / 2 - 2} ${hz - gateH - 1}z" fill="${C.parchment}"/>
    <rect x="${gx - gateW / 2}" y="${hz - gateH + 1}" width="${gateW}" height="${gateH - 1}" fill="${C.iron900}"/>
    <path d="M${gx - gateW / 2 + 3} ${hz - gateH + 1}v${gateH - 1}M${gx} ${hz - gateH + 1}v${gateH - 1}M${gx + gateW / 2 - 3} ${hz - gateH + 1}v${gateH - 1}M${gx - gateW / 2} ${hz - 12}h${gateW}" stroke="${C.iron700}" stroke-width="1.2"/>
    <rect x="${gx - 1}" y="${hz - gateH - 16}" width="2" height="3" fill="${C.emberHot}"/>
    <path d="M${gx - gw / 2 + 14} ${wallTop}h${gw - 28}" stroke="${C.edge}" stroke-width="1.5"/>`;
  // Its reflection: the same pale masses, broken into lines on the water.
  const refl = [0, 4, 8, 12, 16, 20, 25, 30, 36].map((d, i) => {
    const w = (gw + 4) * (1 - i * 0.07), y = hz + 3 + d;
    return `<rect x="${(gx - w / 2 + (i % 2 ? 3 : -2)).toFixed(1)}" y="${y}" width="${w.toFixed(1)}" height="${i < 4 ? 2 : 1.5}" fill="${C.parchment2}" opacity="${(0.42 - i * 0.04).toFixed(2)}"/>`;
  }).join('');
  // The Hearth's smoke, rising from beyond the gate and leaning with the wind, lit from below.
  const smoke = `
    <path d="M188 ${wallTop + 2} C182 100 198 86 190 68 C182 52 200 38 224 28 C242 20 258 10 274 4 L300 4 C280 14 264 26 250 34 C226 48 214 60 218 76 C222 92 210 104 208 ${wallTop + 2}z" fill="${C.iron200}" opacity=".5"/>
    <path d="M194 ${wallTop + 2} C190 104 204 90 196 72 C190 58 206 46 226 36 C238 30 248 24 258 18" fill="none" stroke="${C.parchment2}" stroke-width="3" opacity=".3"/>
    <ellipse cx="198" cy="${wallTop - 3}" rx="30" ry="11" fill="${glow}" opacity=".35"/>
    <ellipse cx="198" cy="${wallTop}" rx="16" ry="5" fill="${C.ember}" opacity=".6"/>`;
  // Water: grey, darkening toward us, with long ripple lines.
  const ripples = [];
  for (let i = 0; i < 16; i++) {
    const y = hz + 8 + i * i * 0.36 + i * 2.2;
    if (y > H - 30) break;
    const x = ((i * 53) % 140) + 10, w = 40 + ((i * 37) % 70);
    ripples.push(`<path d="M${x} ${y.toFixed(1)}h${w}" stroke="${C.iron200}" stroke-width="${(0.8 + i * 0.08).toFixed(2)}" opacity="${(0.32 - i * 0.012).toFixed(2)}"/>`);
    ripples.push(`<path d="M${x + w + 60} ${(y + 3).toFixed(1)}h${w * 0.7}" stroke="${C.iron200}" stroke-width="${(0.8 + i * 0.08).toFixed(2)}" opacity="${(0.26 - i * 0.01).toFixed(2)}"/>`);
  }
  // The raft: lashed logs, a ferryman poling at the stern, the newcomer hooded at the bow, a lantern on a post.
  const rx = 40, ry = 200;
  const logs = [0, 1, 2, 3, 4].map((i) => `<rect x="${rx + i * 1.5}" y="${ry + i * 4}" width="${92 - i * 3}" height="5" rx="2.5" fill="${i % 2 ? C.inkSoft : C.ink}"/>`).join('');
  const raft = `
    <ellipse cx="${rx + 46}" cy="${ry + 24}" rx="58" ry="5" fill="${C.iron950}" opacity=".45"/>
    ${logs}
    <path d="M${rx + 10} ${ry - 1}v22M${rx + 46} ${ry - 1}v22M${rx + 80} ${ry - 1}v22" stroke="${C.edge}" stroke-width="1.2" opacity=".6"/>
    <path d="M${rx + 18} ${ry - 44} L${rx + 2} ${ry + 30}" stroke="${C.ink}" stroke-width="2.2" stroke-linecap="round"/>
    <path d="M${rx + 16} ${ry - 32} c-5 4 -7 14 -6 31 h13 c1 -14 0 -25 -3 -31z" fill="${C.iron950}"/>
    <circle cx="${rx + 18}" cy="${ry - 36}" r="4.2" fill="${C.iron950}"/>
    <path d="M${rx + 14} ${ry - 39} h9 l-2 -3 h-5z" fill="${C.iron950}"/>
    <path d="M${rx + 20} ${ry - 26} L${rx + 15} ${ry - 18}" stroke="${C.iron950}" stroke-width="3" stroke-linecap="round"/>
    <path d="M${rx + 66} ${ry - 1} c0 -8 2 -16 8 -19 c6 3 8 11 8 19z" fill="${C.iron900}"/>
    <path d="M${rx + 70} ${ry - 15} c2 -4 6 -4 8 0" fill="none" stroke="${C.iron700}" stroke-width="1.5"/>
    <path d="M${rx + 88} ${ry - 1} v-22" stroke="${C.ink}" stroke-width="1.6"/>
    <rect x="${rx + 85.5}" y="${ry - 26}" width="5" height="6" rx="1" fill="${C.emberHot}"/>
    <circle cx="${rx + 88}" cy="${ry - 23}" r="9" fill="${C.ember}" opacity=".18"/>
    <path d="M${rx + 88} ${ry + 26} v14" stroke="${C.emberHot}" stroke-width="2" opacity=".35"/>`;
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" style="display:block">
  <defs>
    <linearGradient id="sky" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${night}"/><stop offset=".45" stop-color="${dusk}"/><stop offset=".8" stop-color="${ridge}"/><stop offset="1" stop-color="${haze}"/></linearGradient>
    <linearGradient id="sea" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="${C.iron600}"/><stop offset="1" stop-color="${C.iron800}"/></linearGradient>
  </defs>
  <rect width="${W}" height="${hz}" fill="url(#sky)"/>
  <rect y="${hz - 4}" width="${W}" height="4" fill="${glow}" opacity=".55"/>
  <path d="M0 ${hz - 18} C40 ${hz - 30} 70 ${hz - 22} 104 ${hz - 28} C130 ${hz - 33} 150 ${hz - 24} 170 ${hz - 26} L320 ${hz - 34} V${hz} H0z" fill="${ridge}"/>
  <path d="M0 ${hz - 8} C30 ${hz - 14} 80 ${hz - 10} 120 ${hz - 13} C150 ${hz - 15} 230 ${hz - 9} 320 ${hz - 14} V${hz} H0z" fill="${dusk}"/>
  ${smoke}
  ${gate}
  <rect y="${hz}" width="${W}" height="${H - hz}" fill="url(#sea)"/>
  <rect y="${hz}" width="${W}" height="2" fill="${C.iron900}" opacity=".7"/>
  ${refl}
  ${ripples.join('')}
  ${raft}
</svg>`;
}

function crossingHtml() {
  const W = 320, H = 256;
  return page(W, H, `background:${C.iron900}`, `<div class="p">${crossingSvg(W, H)}
  <div style="position:absolute;left:0;right:0;bottom:0;height:34px;background:${C.iron950};opacity:.82"></div>
  <div class="k" style="position:absolute;bottom:12px;width:100%;text-align:center;color:${C.parchment2};font-size:10.5px;letter-spacing:.16em">The Crossing of the Grey Water</div>
  ${frame(W, H, false)}</div>`);
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
  list.push({ id: 'the-crossing', title: 'The Crossing of the Grey Water: the raft, the Gatehouse of the Unwritten, the Hearth\'s smoke', group: 'posters', width: 320, height: 256, html: crossingHtml });
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
  for (const h of HOUSES) list.push({ id: `sigil-32-${h}`, kind: 'rgba', width: 32, height: 32, html: () => single(`sigils/${h}.svg`, 32, 32) });
  const iconNames = fs.readdirSync(path.join(ART, 'icons')).filter((f) => f.endsWith('.svg')).map((f) => f.slice(0, -4)).sort();
  for (const n of iconNames) list.push({ id: `icon-24-${n}`, kind: 'mask', width: 24, height: 24, html: () => maskHtml(icon(n, 24, '#ffffff'), 24) });
  for (const n of BIG_ICONS) list.push({ id: `icon-64-${n}`, kind: 'mask', width: 64, height: 64, html: () => maskHtml(icon(n, 64, '#ffffff'), 64) });
  return list;
}

// Glyph atlases for live text. Sizes are pixels on the board canvas the plugin composes (see RealmPainter.md).
// "display" is the poster headline ("WANTED"): capitals, digits and punctuation only, to keep its atlas small.
export const FACES = [
  { id: 'display', family: 'Cinzel', weight: 700, style: 'normal', size: 44, charset: 'display' },
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

// The display face: ASCII without lower case, and the Latin-1 capitals. The plugin upper-cases display text.
export const DISPLAY_CHARSET = (() => {
  const cps = [];
  for (let c = 32; c <= 126; c++) if (c < 97 || c > 122) cps.push(c);
  for (let c = 0xc0; c <= 0xde; c++) if (c !== 0xd7) cps.push(c);
  cps.push(0x2019, 0x2014);
  return cps;
})();

export const charsetOf = (face) => (face.charset === 'display' ? DISPLAY_CHARSET : CHARSET);

// The colours the plugin draws live boards with, from art/palette.json: brand colours by camel-case key
// ("Ember deep" -> emberDeep) and each house's name, field and metal.
export function boardPalette() {
  const brand = {};
  for (const b of palette.brand) brand[b.name.toLowerCase().replace(/ (\w)/g, (_, c) => c.toUpperCase()).replace(/ /g, '')] = b.hex;
  const houses = {};
  for (const h of HOUSES) {
    const p = palette.houses[h];
    houses[h] = { Name: p.name, Sigil: p.sigil, Words: p.words, Field: p.field, FieldDark: p.fieldDark, Metal: p.metal };
  }
  return { Brand: brand, Houses: houses };
}

export const FONT_LICENSES = [
  { family: 'Cinzel', file: 'OFL-Cinzel.txt', copyright: 'Copyright 2020 The Cinzel Project Authors', license: 'SIL Open Font License 1.1' },
  { family: 'EB Garamond', file: 'OFL-EBGaramond.txt', copyright: 'Copyright 2017 The EB Garamond Project Authors', license: 'SIL Open Font License 1.1' },
];
