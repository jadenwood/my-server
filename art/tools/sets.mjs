// Realm art pack: the generated sets that are not heraldry proper.
//   keyart   the Old Throne above the Hearth at dusk (1920 x 1080 and 1200 x 630, with and without the logo)
//   badges   season medals I-IV and the renown title badges (art/src/titles.json)
//   textures parchment and dark iron as small tileable SVG patterns
// build.mjs calls these with a context of shared helpers (colours, doc(), the sigils, the lockup), so this file has no
// state of its own. Everything is deterministic: the scenery uses a seeded random generator, so a rebuild is identical.

import { brush, blob } from './pen.mjs';

const n = (v) => +(+v).toFixed(1);

// mulberry32: small, fast, seeded; the same seed always draws the same mountains and stars
export function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// A ridgeline by midpoint displacement between fixed anchor points; returns a closed path down to `floor`.
export function ridge(anchors, { rough = 0.5, depth = 5, seed = 1, floor = 1080 } = {}) {
  const r = rng(seed);
  let pts = anchors.map(([x, y]) => [x, y]);
  for (let d = 0; d < depth; d++) {
    const next = [pts[0]];
    for (let i = 1; i < pts.length; i++) {
      const [x0, y0] = pts[i - 1], [x1, y1] = pts[i];
      const span = x1 - x0;
      next.push([(x0 + x1) / 2 + (r() - 0.5) * span * 0.2, (y0 + y1) / 2 + (r() - 0.5) * span * rough], pts[i]);
    }
    pts = next;
  }
  const first = pts[0], last = pts[pts.length - 1];
  return `M${n(first[0])} ${floor}V${n(first[1])}${pts.slice(1).map(([x, y]) => `L${n(x)} ${n(y)}`).join('')}V${floor}Z`;
}

// ---------------------------------------------------------------- key art

// The scene, drawn once on a 1920 x 1080 canvas. Returns { defs, body } for nesting.
export function scene(ctx) {
  const { C, S, palette, houses, chargeOf, throne } = ctx;
  const r = rng(344);
  const W = 1920, H = 1080, HX = 960;

  // stars in three sizes, kept to the upper sky and thinned towards the glow
  const stars = [[], [], []];
  for (let i = 0; i < 190; i++) {
    const x = r() * W, y = Math.pow(r(), 1.6) * 560;
    if (Math.hypot(x - 1650, y - 230) < 120) continue; // not on the moon
    stars[r() < 0.72 ? 0 : r() < 0.8 ? 1 : 2].push(`M${n(x)} ${n(y)}h.01`);
  }
  const starLayer = stars.map((s, i) => `<path d="${s.join('')}" stroke="${i === 2 ? C['ember-pale'] : C.parchment}" stroke-linecap="round" stroke-width="${[1.6, 2.6, 3.6][i]}" opacity="${[0.45, 0.7, 0.9][i]}"/>`).join('');

  // banners of the six houses on poles beside the road; each one carries its house's charge
  const pole = (x, y, house, i) => {
    const p = palette.houses[house];
    const sway = (i % 2 ? 1 : -1) * 3;
    return `<g transform="translate(${x} ${y})">
<path d="M0 -6V170" stroke="${C['iron-950']}" stroke-width="5" stroke-linecap="round"/>
<circle cy="-8" r="4.5" fill="url(#ka-gold)"/>
<path d="M-26 6h52" stroke="${C['iron-950']}" stroke-width="4" stroke-linecap="round"/>
<path d="M-24 8h48v76l${sway} 10-24-12-24 12 ${-sway} -10z" fill="${p.field}" stroke="${C['iron-950']}" stroke-width="2"/>
<path d="M-24 8h48v76l${sway} 10-24-12-24 12 ${-sway} -10z" fill="url(#ka-cloth)"/>
<path d="M-19 13h38v66l-19-9-19 9z" fill="none" stroke="${C.ember}" stroke-width="1.2" opacity=".85"/>
<svg x="-17" y="20" width="34" height="34" viewBox="0 0 200 200">${chargeOf(house)}</svg>
</g>`;
  };
  const left = ['varrow', 'ashgrove', 'corvane'], right = ['dunmere', 'halloran', 'merrin'];
  const banners = [...left.map((h, i) => pole(560 + i * 118, 742 + i * 26, h, i)), ...right.map((h, i) => pole(1360 - i * 118, 742 + i * 26, h, i + 1))].join('\n');

  // sparks rising from the Hearth
  const sparks = [];
  for (let i = 0; i < 46; i++) {
    const t = r();
    const x = HX + (r() - 0.5) * 140 * (1 + t * 1.6), y = 905 - t * 330;
    sparks.push(`<circle cx="${n(x)}" cy="${n(y)}" r="${n(0.9 + (1 - t) * 2.2 * r())}" opacity="${n(0.35 + (1 - t) * 0.65)}"/>`);
  }

  // ravens wheeling about the throne
  const raven = (x, y, s, a) => `<path transform="translate(${x} ${y}) rotate(${a}) scale(${s})" d="M-14 0c4-5 9-6 12-2 1-1 3-1 4 0 3-4 8-3 12 2-5-2-9-1-12 2l-2 3-2-3c-3-3-7-4-12-2Z"/>`;

  // road up the hill: switchbacks from the Hearth to the throne
  const road = 'M960 915C880 905 790 892 812 862S1080 840 1110 808 900 790 884 760 1040 742 1040 716 960 700 960 682';

  // the throne hill: a rugged outline with a flat top for the throne
  const hr = rng(77), side = (pts) => pts.map(([x, y], i) => (i === 0 || i === pts.length - 1 ? [x, y] : [x + (hr() - 0.5) * 22, y + (hr() - 0.5) * 10]));
  const L = side([[578, 1080], [640, 990], [700, 930], [760, 890], [800, 860], [830, 820], [850, 780], [866, 744], [880, 712], [888, 680]]);
  const R = side([[1032, 680], [1040, 712], [1056, 746], [1072, 784], [1092, 822], [1122, 860], [1164, 892], [1220, 930], [1282, 990], [1342, 1080]]);
  const hill = blob([[578, 1080, 'c'], ...L.slice(1, -1), [888, 680, 'c'], [1032, 680, 'c'], ...R.slice(1, -1), [1342, 1080, 'c']], 0.6);
  const rim = 'M' + [...L.slice(4, -1), [888, 680], [1032, 680], ...R.slice(1, 6)].map(([x, y]) => `${n(x)} ${n(y)}`).join('L');
  const towers = [[316, 668, 1], [1608, 640, 0.8], [1702, 652, 0.6]].map(([x, y, s]) => `<g transform="translate(${x} ${y}) scale(${s})"><path d="M-14 0v-46l-4-6h36l-4 6V0Zm2-52v-8h4v4h4v-4h4v4h4v-4h4v8Zm22 52V-30h14v30Z" fill="${S['far-ridge']}"/><circle cy="-36" r="1.8" fill="${C['ember-hot']}"/><circle cx="17" cy="-18" r="1.4" fill="${C['ember-hot']}" opacity=".8"/></g>`).join('');
  const tree = [brush([[150, 920, 16], [148, 860, 12], [150, 800, 9], [144, 740, 6], [140, 690, 1.5]]), brush([[148, 820, 6], [124, 796, 4], [104, 760, 2], [96, 732, 0.6]]), brush([[150, 850, 6.5], [174, 826, 4], [192, 790, 2.2], [198, 760, 0.6]]), brush([[146, 760, 4], [160, 740, 2.6], [164, 718, 0.5]]), brush([[124, 796, 2.6], [112, 800, 1.4], [100, 798, 0.4]]), brush([[180, 815, 2.6], [196, 816, 1.4], [208, 812, 0.4]])].map((d) => `<path d="${d}"/>`).join('');

  // the Hearth: tongues of flame in a ring of standing stones
  const fr = rng(912);
  const tongues = [];
  for (let i = 0; i < 9; i++) {
    const x = (i - 4) * 12 + (fr() - 0.5) * 6, h = 46 + (4 - Math.abs(i - 4)) * 20 + fr() * 26, lean = (fr() - 0.5) * 34 + (i - 4) * 3;
    tongues.push([x, h, lean, 15 + fr() * 9]);
  }
  const hearthFlames = (k) => tongues.map(([x, h, lean, w]) => `<path d="${blob([[x * k - w * k, 6, 'c'], [x * k - w * 1.1 * k, -h * 0.22 * k], [x * k - w * 0.2 * k + lean * 0.2 * k, -h * 0.5 * k], [x * k - lean * 0.15 * k, -h * 0.72 * k], [x * k + lean * k, -h * k, 'c'], [x * k + lean * 0.35 * k + w * 0.45 * k, -h * 0.62 * k], [x * k + w * 0.5 * k, -h * 0.4 * k], [x * k + w * 1.05 * k, -h * 0.18 * k], [x * k + w * k, 6, 'c']])}"/>`).join('');
  const stones = (backRow) => {
    const out = [];
    for (let i = 0; i < 14; i++) {
      const a = (i / 14) * Math.PI * 2 + 0.11, x = Math.cos(a) * 92, y = Math.sin(a) * 16 + 6;
      if ((Math.sin(a) < 0) !== backRow) continue;
      const w = 20 + (i % 3) * 4, h = 10 + (i % 4) * 3;
      out.push(`<path d="M${n(x - w / 2)} ${n(y)}c0-${h * 0.7} 3-${h} ${n(w / 2)}-${h}s${n(w / 2)} ${n(h * 0.3)} ${n(w / 2)} ${h}Z" fill="${C['iron-700']}" stroke="${C['iron-950']}" stroke-width="2"/><path d="M${n(x - w / 2 + 2)} ${n(y - h * 0.6)}c1-${n(h * 0.3)} 3-${n(h * 0.38)} ${n(w / 2 - 2)}-${n(h * 0.38)}" fill="none" stroke="${C['ember-hot']}" stroke-width="2" opacity=".6"/>`);
    }
    return out.join('');
  };

  const defs = `<linearGradient id="ka-sky" x1="0" y1="0" x2="0" y2="1">
<stop offset="0" stop-color="${S.night}"/><stop offset=".38" stop-color="${S.dusk}"/><stop offset=".56" stop-color="${S.haze}"/><stop offset=".66" stop-color="${S.afterglow}"/><stop offset=".72" stop-color="${C.ember}"/></linearGradient>
<radialGradient id="ka-sun" cx="960" cy="690" r="560" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${C['ember-pale']}" stop-opacity=".95"/><stop offset=".12" stop-color="${C['ember-hot']}" stop-opacity=".7"/><stop offset=".4" stop-color="${S.afterglow}" stop-opacity=".35"/><stop offset="1" stop-color="${S.afterglow}" stop-opacity="0"/></radialGradient>
<radialGradient id="ka-moon" cx="1650" cy="230" r="150" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${C.parchment}" stop-opacity=".22"/><stop offset=".45" stop-color="${C.parchment}" stop-opacity=".06"/><stop offset="1" stop-color="${C.parchment}" stop-opacity="0"/></radialGradient>
<linearGradient id="ka-moonface" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${C['ember-pale']}"/><stop offset="1" stop-color="${C['parchment-2']}"/></linearGradient>
<linearGradient id="ka-far" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${S['far-ridge']}"/><stop offset="1" stop-color="${S.haze}"/></linearGradient>
<linearGradient id="ka-mid" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${S.dusk}"/><stop offset="1" stop-color="${S['far-ridge']}"/></linearGradient>
<linearGradient id="ka-hill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C['iron-700']}"/><stop offset=".5" stop-color="${C['iron-800']}"/><stop offset="1" stop-color="${C['iron-950']}"/></linearGradient>
<linearGradient id="ka-mist" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C['parchment-2']}" stop-opacity="0"/><stop offset=".5" stop-color="${C['parchment-2']}" stop-opacity=".16"/><stop offset="1" stop-color="${C['parchment-2']}" stop-opacity="0"/></linearGradient>
<radialGradient id="ka-hearth" cx="960" cy="905" r="330" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${C['ember-hot']}" stop-opacity=".85"/><stop offset=".2" stop-color="${C.ember}" stop-opacity=".45"/><stop offset=".6" stop-color="${S.afterglow}" stop-opacity=".15"/><stop offset="1" stop-color="${S.afterglow}" stop-opacity="0"/></radialGradient>
<radialGradient id="ka-halo" cx="960" cy="600" r="260" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="${C['ember-pale']}" stop-opacity=".85"/><stop offset=".3" stop-color="${C['ember-hot']}" stop-opacity=".35"/><stop offset="1" stop-color="${C.ember}" stop-opacity="0"/></radialGradient>
<linearGradient id="ka-ray" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="${C['ember-hot']}" stop-opacity=".13"/><stop offset="1" stop-color="${C['ember-hot']}" stop-opacity="0"/></linearGradient>
<linearGradient id="ka-gold" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C['ember-pale']}"/><stop offset=".35" stop-color="${C['ember-hot']}"/><stop offset=".6" stop-color="${C.ember}"/><stop offset="1" stop-color="${C['ember-deep']}"/></linearGradient>
<linearGradient id="ka-cloth" x1="0" x2="1"><stop offset="0" stop-color="${C['iron-950']}" stop-opacity=".45"/><stop offset=".35" stop-color="${C.parchment}" stop-opacity=".12"/><stop offset=".7" stop-color="${C['iron-950']}" stop-opacity=".1"/><stop offset="1" stop-color="${C['iron-950']}" stop-opacity=".5"/></linearGradient>
<radialGradient id="ka-vignette" cx="50%" cy="48%" r="75%"><stop offset=".55" stop-color="${C['iron-950']}" stop-opacity="0"/><stop offset="1" stop-color="${C['iron-950']}" stop-opacity=".8"/></radialGradient>
<filter id="ka-soft" x="-20%" y="-50%" width="140%" height="200%"><feGaussianBlur stdDeviation="6"/></filter>
<filter id="ka-glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="3"/></filter>`;

  const rays = [[-38, 40], [-24, 70], [-11, 34], [-2, 90], [9, 46], [22, 76], [35, 30]].map(([a, w]) => `<path transform="rotate(${a} 960 600)" d="M960 600 ${n(960 - w / 2)} -60h${w}Z"/>`).join('');

  const body = `<rect width="${W}" height="${H}" fill="url(#ka-sky)"/>
<rect width="${W}" height="${H}" fill="url(#ka-sun)"/>
${starLayer}
<circle cx="1650" cy="230" r="150" fill="url(#ka-moon)"/>
<circle cx="1650" cy="230" r="54" fill="url(#ka-moonface)" opacity=".92"/>
<g fill="${C['parchment-edge']}" opacity=".22"><circle cx="1632" cy="214" r="11"/><circle cx="1670" cy="246" r="7"/><circle cx="1658" cy="206" r="5"/><circle cx="1628" cy="252" r="6"/></g>
<g fill="${S.dusk}" opacity=".7" filter="url(#ka-soft)"><path d="M1400 268c90-14 200-18 330-8 80 6 150 4 220-6-70 22-170 30-280 26-100-4-190-2-270-12Z"/><path d="M80 470c200-20 420-22 660-6-220 16-450 20-660 6Z"/><path d="M1180 520c180-14 380-14 600-2-200 12-410 14-600 2Z"/></g>
<g fill="url(#ka-ray)">${rays}</g>
<path d="${ridge([[0, 690], [300, 630], [620, 676], [960, 650], [1290, 672], [1620, 618], [1920, 680]], { seed: 7, rough: 0.42 })}" fill="url(#ka-far)"/>
<g fill="${S.dusk}" opacity=".6" filter="url(#ka-soft)"><path d="M260 604c120-12 260-10 420 4-150 10-290 8-420-4Z"/><path d="M1220 612c130-14 290-12 450 2-160 12-310 10-450-2Z"/></g>
${towers}
<rect y="640" width="${W}" height="110" fill="url(#ka-mist)"/>
<path d="${ridge([[0, 740], [240, 690], [520, 760], [760, 800], [1160, 800], [1400, 756], [1680, 694], [1920, 736]], { seed: 21, rough: 0.36 })}" fill="url(#ka-mid)"/>
<rect y="760" width="${W}" height="120" fill="url(#ka-mist)"/>
<circle cx="960" cy="600" r="260" fill="url(#ka-halo)"/>
<path d="${hill}" fill="url(#ka-hill)"/>
<path d="${rim}" fill="none" stroke="${C['ember-hot']}" stroke-width="2.5" stroke-linejoin="round" opacity=".55"/>
<g fill="none" stroke="${C['iron-950']}" stroke-linecap="round" stroke-width="2.5" opacity=".55"><path d="M846 790c40 6 90 2 120 8m40-4c30-2 54 0 76-6M820 840c50 4 110-2 150 6m50-2c40-2 70 0 100-8M868 740c30 4 60 0 84 4m30 0c24 0 40-2 56-6M760 900c60 6 120 0 170 8m70-2c60 0 120-4 170 4"/></g>
<path d="${road}" fill="none" stroke="${C['parchment-edge']}" stroke-width="5" stroke-linecap="round" opacity=".42"/>
<g fill="${C['ember-hot']}">${[[812, 862], [1110, 808], [884, 760], [1040, 716]].map(([x, y]) => `<circle cx="${x}" cy="${y - 6}" r="9" opacity=".25" filter="url(#ka-glow)"/><circle cx="${x}" cy="${y - 6}" r="2.6"/>`).join('')}</g>
<g transform="translate(844.8 491) scale(.9)"><g fill="${C['iron-950']}" stroke="url(#ka-gold)" stroke-width="2.6" stroke-linejoin="round">${throne}</g>
<g fill="url(#ka-gold)"><path d="m108 134-3-32 12 10 11-20 11 20 12-10-3 32Z"/><rect width="44" height="7" x="106" y="138" rx="1.5"/></g></g>
${banners}
<circle cx="960" cy="905" r="330" fill="url(#ka-hearth)"/>
<g transform="translate(960 908)">
${stones(true)}
<g fill="${C.ember}">${hearthFlames(1)}</g>
<g fill="${C['ember-hot']}">${hearthFlames(0.68)}</g>
<g fill="${C['ember-pale']}">${hearthFlames(0.36)}</g>
${stones(false)}
</g>
<g fill="${C['ember-hot']}">${sparks.join('')}</g>
<g fill="${C['iron-950']}">${raven(1080, 470, 1.4, -8)}${raven(1140, 430, 1, 6)}${raven(820, 500, 1.1, 10)}${raven(1200, 500, 0.8, -4)}</g>
<rect y="880" width="${W}" height="140" fill="url(#ka-mist)"/>
<path d="${ridge([[0, 880], [180, 900], [420, 960], [560, 1000], [620, 1080]], { seed: 3, rough: 0.3, depth: 4 })}" fill="${C['iron-950']}"/>
<path d="${ridge([[1300, 1080], [1380, 990], [1560, 950], [1760, 880], [1920, 860]], { seed: 5, rough: 0.3, depth: 4 })}" fill="${C['iron-950']}"/>
<g fill="${C['iron-950']}">${tree}</g>
<rect width="${W}" height="${H}" fill="url(#ka-vignette)"/>`;
  return { defs, body };
}

export function keyartFiles(ctx) {
  const { doc, lockup, n: nn } = ctx;
  const sc = scene(ctx);
  const desc = 'The Old Throne on its hill above the Hearth at dusk, with the banners of the six great houses along the road. Original key art for the Realm of Ostreval. Generated by art/tools/sets.mjs.';
  const out = {};
  for (const [w, h, name] of [[1920, 1080, 'old-throne-1920'], [1200, 630, 'old-throne-1200']]) {
    // the 1200 x 630 card crops the same scene a little, top and bottom
    const vb = w === 1920 ? '0 0 1920 1080' : '0 40 1920 1008';
    const nested = `<svg width="${w}" height="${h}" viewBox="${vb}" preserveAspectRatio="xMidYMid slice">${sc.body}</svg>`;
    out[`keyart/${name}.svg`] = doc({ w, h, title: `Realm key art: the Old Throne (${w} x ${h})`, desc, defs: sc.defs, body: nested });
    const l = lockup('horizontal', 'dark');
    const lw = w === 1920 ? 760 : 500, lh = (lw * l.h) / l.w, ly = w === 1920 ? 70 : 38;
    out[`keyart/${name}-title.svg`] = doc({
      w, h, title: `Realm key art with logo (${w} x ${h})`, desc: desc.replace('Original key art', 'With the Realm logo. Original key art'),
      defs: sc.defs + l.defs + `<linearGradient id="ka-scrim" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${ctx.C['iron-950']}" stop-opacity=".75"/><stop offset="1" stop-color="${ctx.C['iron-950']}" stop-opacity="0"/></linearGradient>`,
      body: `${nested}\n<rect width="${w}" height="${nn(ly * 2 + lh + 40)}" fill="url(#ka-scrim)"/>\n<svg x="${nn((w - lw) / 2)}" y="${ly}" width="${lw}" height="${nn(lh)}" viewBox="0 0 ${nn(l.w)} ${nn(l.h)}">${l.body}</svg>`,
    });
  }
  return out;
}

// ---------------------------------------------------------------- badges

// Roman numerals drawn as outlined, Trajan-like capitals (no live text anywhere in the pack). Height 72, top at y=0.
const GLYPH = {
  I: { w: 30, d: (x) => `M${n(x - 15)} 0H${n(x + 15)}V3.5C${n(x + 10)} 4 ${n(x + 7)} 6 ${n(x + 6.5)} 11V61C${n(x + 7)} 66 ${n(x + 10)} 68 ${n(x + 15)} 68.5V72H${n(x - 15)}V68.5C${n(x - 10)} 68 ${n(x - 7)} 66 ${n(x - 6.5)} 61V11C${n(x - 7)} 6 ${n(x - 10)} 4 ${n(x - 15)} 3.5Z` },
  V: { w: 56, d: (x) => `M${n(x - 28)} 0H${n(x - 6)}V3.5C${n(x - 9.5)} 4 ${n(x - 10.5)} 5.5 ${n(x - 9.5)} 8.5L${n(x + 3.5)} 52 ${n(x + 15.5)} 9C${n(x + 16.5)} 5.5 ${n(x + 15)} 4 ${n(x + 11)} 3.5V0H${n(x + 28)}V3.5C${n(x + 24.5)} 4 ${n(x + 22.5)} 6 ${n(x + 21.5)} 9L${n(x + 3)} 72H${n(x - 1.5)}L${n(x - 21)} 9C${n(x - 22)} 6 ${n(x - 24)} 4 ${n(x - 28)} 3.5Z` },
};
export const roman = (k) => ['', 'I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII', 'IX', 'X'][k] || String(k);
// Path data for a numeral centred on x=0, plus its width.
export function numeral(str, gap = 4) {
  const chars = [...str].filter((c) => GLYPH[c]);
  const total = chars.reduce((a, c) => a + GLYPH[c].w, 0) + gap * (chars.length - 1);
  let x = -total / 2, d = '';
  for (const c of chars) { d += GLYPH[c].d(x + GLYPH[c].w / 2); x += GLYPH[c].w + gap; }
  return { d, w: total };
}

const crownPath = 'M-22 14h44l3-24-12 9-13-20-13 20-12-9Z';

// gold leaves along an arc (a laurel spray); `mirror` flips it to the other side
function spray(cx, cy, r, a0, a1, count, size, mirror = false) {
  const out = [];
  for (let i = 0; i < count; i++) {
    const a = a0 + ((a1 - a0) * (i + 0.5)) / count;
    for (const side of [-1, 1]) {
      const rr = r + side * size * 0.75;
      let x = cx + Math.cos(a) * rr;
      const y = cy + Math.sin(a) * rr;
      let rot = (a * 180) / Math.PI + 90 + side * 28;
      if (mirror) { x = 2 * cx - x; rot = -rot; }
      out.push(`<ellipse cx="${n(x)}" cy="${n(y)}" rx="${n(size * 0.42)}" ry="${n(size)}" transform="rotate(${n(rot)} ${n(x)} ${n(y)})"/>`);
    }
  }
  return out.join('');
}

const goldDefs = (C, id) => `<linearGradient id="${id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C['ember-pale']}"/><stop offset=".35" stop-color="${C['ember-hot']}"/><stop offset=".6" stop-color="${C.ember}"/><stop offset="1" stop-color="${C['ember-deep']}"/></linearGradient>`;

export function seasonBadge(ctx, k) {
  const { C, palette, doc } = ctx;
  const enamel = palette.seasonEnamels[(k - 1) % palette.seasonEnamels.length];
  const id = `sb${k}`;
  const rays = [];
  for (let i = 0; i < 32; i++) {
    const a = (i / 32) * Math.PI * 2, R = i % 2 ? 114 : 125, w = i % 2 ? 0.07 : 0.09;
    rays.push(`${n(128 + Math.cos(a - w) * 96)} ${n(128 + Math.sin(a - w) * 96)}L${n(128 + Math.cos(a) * R)} ${n(128 + Math.sin(a) * R)}L${n(128 + Math.cos(a + w) * 96)} ${n(128 + Math.sin(a + w) * 96)}`);
  }
  const num = numeral(roman(k));
  const sc = Math.min(0.86, 100 / num.w);
  const rings = [30, 40, 50, 60, 70].map((r) => `<circle cx="128" cy="128" r="${r}"/>`).join('');
  const ribbon = 'M54 200h148l-10 13 10 13H54l10-13Z';
  const stem = `M${n(128 + Math.cos(Math.PI * 0.6) * 68)} ${n(128 + Math.sin(Math.PI * 0.6) * 68)}A68 68 0 0 1 ${n(128 + Math.cos(Math.PI * 1.1) * 68)} ${n(128 + Math.sin(Math.PI * 1.1) * 68)}`;
  const defs = `${goldDefs(C, `${id}-gold`)}
<radialGradient id="${id}-glass" cx="38%" cy="30%" r="80%"><stop offset="0" stop-color="${C.parchment}" stop-opacity=".28"/><stop offset=".45" stop-color="${C.parchment}" stop-opacity="0"/><stop offset="1" stop-color="${C['iron-950']}" stop-opacity=".55"/></radialGradient>`;
  const body = `<path d="M${rays.join('L')}Z" fill="url(#${id}-gold)" stroke="${C['ember-deep']}" stroke-width="1"/>
<circle cx="128" cy="128" r="104" fill="${C['iron-950']}"/>
<circle cx="128" cy="128" r="98" fill="none" stroke="url(#${id}-gold)" stroke-width="8"/>
<circle cx="128" cy="128" r="89.5" fill="none" stroke="${C['ember-hot']}" stroke-width="3.4" stroke-dasharray="0 7.03" stroke-linecap="round"/>
<circle cx="128" cy="128" r="84" fill="${enamel}"/>
<g fill="none" stroke="${C.parchment}" stroke-width=".8" opacity=".08">${rings}</g>
<circle cx="128" cy="128" r="84" fill="url(#${id}-glass)"/>
<circle cx="128" cy="128" r="84" fill="none" stroke="${C['iron-950']}" stroke-width="2"/>
<path d="${stem}M${n(256 - 128 - Math.cos(Math.PI * 0.6) * 68)} ${n(128 + Math.sin(Math.PI * 0.6) * 68)}A68 68 0 0 0 ${n(256 - 128 - Math.cos(Math.PI * 1.1) * 68)} ${n(128 + Math.sin(Math.PI * 1.1) * 68)}" fill="none" stroke="url(#${id}-gold)" stroke-width="2.2" stroke-linecap="round"/>
<g fill="url(#${id}-gold)" stroke="${C['iron-950']}" stroke-width=".6">${spray(128, 128, 68, Math.PI * 0.6, Math.PI * 1.1, 5, 8.5)}${spray(128, 128, 68, Math.PI * 0.6, Math.PI * 1.1, 5, 8.5, true)}</g>
<path transform="translate(128 76) scale(.8)" d="${crownPath}" fill="url(#${id}-gold)" stroke="${C['iron-950']}" stroke-width="2"/>
<path transform="translate(130 103) scale(${n(sc * 100) / 100})" d="${num.d}" fill="${C['iron-950']}" opacity=".6"/>
<path transform="translate(128 100) scale(${n(sc * 100) / 100})" d="${num.d}" fill="url(#${id}-gold)" stroke="${C['ember-deep']}" stroke-width="1"/>
<path d="${ribbon}" fill="${enamel}" stroke="url(#${id}-gold)" stroke-width="3" stroke-linejoin="round"/>
<path d="${ribbon}" fill="url(#${id}-glass)"/>
<g fill="url(#${id}-gold)">${[96, 112, 128, 144, 160].map((x) => `<path d="M${x} 207.5l4 5.5-4 5.5-4-5.5Z"/>`).join('')}</g>`;
  return doc({ w: 256, h: 256, title: `Season ${k} badge`, desc: `Medal for Season ${roman(k)} of the Realm: the numeral and crown in gold on enamel. Generated by art/tools/sets.mjs.`, defs, body });
}

export function titleBadge(ctx, t) {
  const { C, doc, nestEmblem } = ctx;
  const id = `tb-${t.id.replace(/_/g, '-')}`;
  const bad = t.infamous;
  const outline = 'M40 30h176v96c0 58-38 96-88 120-50-24-88-62-88-120Z';
  const inner = 'M54 44h148v82c0 49-32 81-74 102-42-21-74-53-74-102Z';
  const defs = `${goldDefs(C, `${id}-gold`)}
<linearGradient id="${id}-rim" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${bad ? C['iron-600'] : C['ember-pale']}"/><stop offset=".5" stop-color="${bad ? C['iron-700'] : C.ember}"/><stop offset="1" stop-color="${bad ? C['iron-950'] : C['ember-deep']}"/></linearGradient>
<radialGradient id="${id}-field" cx="45%" cy="35%" r="75%"><stop offset="0" stop-color="${bad ? C.blood : C['iron-700']}"/><stop offset="1" stop-color="${C['iron-950']}"/></radialGradient>
${bad ? `<linearGradient id="${id}-bone" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C.parchment}"/><stop offset="1" stop-color="${C['parchment-edge']}"/></linearGradient>` : ''}
<mask id="${id}-mask">${nestEmblem(t.emblem, 68, 64, 120, ' color="white"')}</mask>`;
  const ink = bad ? `url(#${id}-bone)` : `url(#${id}-gold)`;
  const studs = bad
    ? [[56, 44], [200, 44], [128, 228]].map(([x, y]) => `<path d="M${x} ${y - 6}l4 6-4 6-4-6Z" fill="${C['iron-600']}" stroke="${C['iron-950']}" stroke-width="1.2"/>`).join('')
    : [[56, 44], [200, 44], [128, 228]].map(([x, y]) => `<circle cx="${x}" cy="${y}" r="5" fill="url(#${id}-gold)" stroke="${C['iron-950']}" stroke-width="1.2"/>`).join('');
  const body = `<path d="${outline}" fill="${C['iron-950']}" stroke="${C['iron-950']}" stroke-width="12" stroke-linejoin="round"/>
<path d="${outline}" fill="url(#${id}-field)" stroke="url(#${id}-rim)" stroke-width="7" stroke-linejoin="round"/>
<path d="${inner}" fill="none" stroke="${bad ? C['iron-950'] : C.ember}" stroke-width="1.5" opacity=".6"/>
${bad ? `<path d="M64 60l14 10 6 18m112 6-12 8-4 14" fill="none" stroke="${C['iron-950']}" stroke-width="1.6" opacity=".6"/>` : ''}
<rect width="256" height="256" fill="${C['iron-950']}" opacity=".75" mask="url(#${id}-mask)" transform="translate(3 4)"/>
<rect width="256" height="256" fill="${ink}" mask="url(#${id}-mask)"/>
${studs}`;
  return doc({
    w: 256, h: 256, title: `Renown title badge: ${t.name}`,
    desc: `${bad ? 'Infamous' : 'Honourable'} renown title "${t.name}" (RealmRenown id ${t.id}). Generated by art/tools/sets.mjs from ${t.emblem}.svg and src/titles.json.`,
    defs, body,
  });
}

export function badgeFiles(ctx) {
  const out = {};
  for (let k = 1; k <= 4; k++) out[`badges/seasons/season-${k}.svg`] = seasonBadge(ctx, k);
  for (const t of JSON.parse(ctx.read('src/titles.json')).titles) out[`badges/titles/${t.id.replace(/_/g, '-')}.svg`] = titleBadge(ctx, t);
  return out;
}

// ---------------------------------------------------------------- textures

// One noise layer: turbulence, one channel turned into alpha, filled with a palette colour. stitchTiles makes it repeat.
function layer(id, o) {
  return `<filter id="${id}" x="0" y="0" width="512" height="512" filterUnits="userSpaceOnUse" color-interpolation-filters="sRGB">
<feTurbulence type="${o.type || 'fractalNoise'}" baseFrequency="${o.freq}" numOctaves="${o.octaves || 3}" seed="${o.seed || 1}" stitchTiles="stitch" result="noise"/>
<feColorMatrix in="noise" values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 ${o.gain} 0 0 0 ${o.bias}" result="mask"/>
<feFlood flood-color="${o.color}" flood-opacity="${o.opacity ?? 1}"/>
<feComposite in2="mask" operator="in"/>
</filter>`;
}

export function textureFiles(ctx) {
  const { C, doc } = ctx;
  const tile = (id, base, layers, title, desc) => doc({
    w: 512, h: 512, title, desc,
    defs: layers.map((l, i) => layer(`${id}-${i}`, l)).join('\n'),
    body: `<rect width="512" height="512" fill="${base}"/>\n${layers.map((_, i) => `<rect width="512" height="512" filter="url(#${id}-${i})"/>`).join('\n')}`,
  });
  return {
    'textures/parchment.svg': tile('tx-parchment', C.parchment, [
      { freq: '.006', octaves: 4, seed: 11, gain: 2.4, bias: -1.05, color: C['parchment-2'], opacity: 0.9 },
      { freq: '.014', octaves: 4, seed: 4, gain: 2.2, bias: -1.25, color: C['parchment-edge'], opacity: 0.35 },
      { freq: '.004 .09', octaves: 2, seed: 9, gain: 2.6, bias: -1.5, color: C['parchment-edge'], opacity: 0.25 },
      { freq: '.8', octaves: 1, seed: 3, gain: 3, bias: -1.9, color: C['ink-soft'], opacity: 0.18, type: 'turbulence' },
    ], 'Parchment texture (tileable)', 'A 512 x 512 tile of aged parchment: mottling, stains, fibres and grain, all in palette colours. Repeats seamlessly; use as a CSS background over #ecdfbf. Generated by art/tools/sets.mjs.'),
    'textures/iron.svg': tile('tx-iron', C['iron-900'], [
      { freq: '.005', octaves: 4, seed: 21, gain: 2.4, bias: -1.1, color: C['iron-800'], opacity: 0.9 },
      { freq: '.002 .35', octaves: 2, seed: 5, gain: 2.4, bias: -1.2, color: C['iron-700'], opacity: 0.5 },
      { freq: '.012', octaves: 3, seed: 8, gain: 2.6, bias: -1.55, color: C['iron-950'], opacity: 0.7 },
      { freq: '.9', octaves: 1, seed: 2, gain: 3, bias: -2.1, color: C['iron-600'], opacity: 0.35, type: 'turbulence' },
    ], 'Dark iron texture (tileable)', 'A 512 x 512 tile of dark worked iron: mottled plates, brushed streaks, pitting and grain, all in palette colours. Repeats seamlessly; use as a CSS background over #131417. Generated by art/tools/sets.mjs.'),
  };
}
