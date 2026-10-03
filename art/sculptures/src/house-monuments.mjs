// The six house monuments: a stone stele carrying the house's heater shield in its field colour, the charge from
// art/sigils/<house>.svg standing out one block in the house metal (art/sculptures/src/sigil-masks.json), and a crest
// on top that is each house's own: antlers, an oak crown, a perched raven, a hung bell, an ember brazier, a coiled eel.
import { readMasks } from '../../tools/sculptor/masks.mjs';
import { Grid, ST, style, pal, house, pick, ramp, UP, DOWN, EAST, WEST, BACK, FRONT } from './lib/kit.mjs';

const W = 16;                         // shield width = mask resolution
const PLINTH = 2;                     // stone layers under the stele
const SHIELD_TOP = PLINTH + 17;       // y of the shield's top row
const CRADLE = 12;                    // shield rows from here down sit in a stone cradle
const Z = 2;                          // the shield's front face; the charge stands out at Z - 1
const MASKS = readMasks();

// Heater shield: straight sides for the upper rows, then curving in to a point (row t from the top, 0..17).
function inShield(x, t) {
  const u = Math.abs(x + 0.5 - W / 2);
  if (t < 9) return u <= W / 2;
  const k = (t - 8) / 10;                         // 0.1 .. 0.9 over the lower rows
  return u <= (W / 2) * Math.sqrt(Math.max(0, 1 - k * k)) - 0.15;
}

function stele(key, crest) {
  const g = new Grid();
  const h = (part) => house(key, part);
  const S = (mat, hex) => style(mat, hex);
  const field = S('clay', h('field')), fieldLight = S('clay', h('fieldLight')), fieldDark = S('clay', h('fieldDark'));
  const metal = S('stone', h('metal')), metalLight = S('stone', h('metalLight')), metalShadow = S('stone', h('metalShadow'));
  const dark = key === 'corvane' ? S('stone', pal('Iron 900')) : metalShadow;
  const mask = MASKS[key][W];

  // Plinth: a sunk slab of dark stone and a step banded in the field colour.
  for (let x = -2; x < W + 2; x++) for (let z = 0; z <= 6; z++) g.set(x, 0, z, pick([[ST.stoneDeep, 3], [ST.stoneDark, 2]], x, 0, z, 31));
  for (let x = -1; x < W + 1; x++) for (let z = 1; z <= 5; z++) g.set(x, 1, z, z === 1 ? fieldDark : pick([[ST.stoneWarm, 3], [ST.stoneDark, 1]], x, 1, z, 32));

  // The shield: field two deep, a metal rim, the charge in relief one block proud. Below its widest rows a stone
  // cradle three deep holds the point, so the shield's own outline is the monument's silhouette.
  for (let t = 0; t <= 17; t++) {
    const y = SHIELD_TOP - t;
    for (let x = 0; x < W; x++) {
      const inside = inShield(x, t);
      const edge = inside && (!inShield(x - 1, t) || !inShield(x + 1, t) || t === 0 || !inShield(x, t + 1));
      const m = t >= 1 && t <= 16 ? mask[t - 1][x] : '.';
      if (!inside) {
        if (t < CRADLE) continue;
        for (let z = Z; z <= Z + 2; z++) g.set(x, y, z, t === CRADLE ? ST.blackStone2 : pick([[ST.stoneDark, 3], [ST.stoneDeep, 2]], x, y, z, 33));
        continue;
      }
      g.set(x, y, Z + 1, fieldDark);
      if (edge) g.set(x, y, Z, metal);
      else g.set(x, y, Z, m === '#' ? metal : m === '+' ? dark : ((x + t) % 6 === 0 || (x - t + 60) % 6 === 0) ? fieldLight : field);
      if (!edge && m !== '.') g.set(x, y, Z - 1, m === '#' ? (t < 8 ? metalLight : metal) : dark);
      if (t >= CRADLE) g.set(x, y, Z + 2, ST.stoneWarm);
    }
  }
  // A stone spine behind the shield, from the cradle up to its top, so it stands from every side.
  for (let y = PLINTH; y <= SHIELD_TOP - 1; y++) for (let x = W / 2 - 1; x <= W / 2; x++) g.set(x, y, Z + 2, ST.stoneDark);
  for (let x = W / 2 - 2; x <= W / 2 + 1; x++) for (let y = PLINTH; y <= PLINTH + 1; y++) g.set(x, y, Z + 3, ST.stoneDark);

  crest(g, { cx: W / 2 - 0.5, y: SHIELD_TOP + 1, z: Z, field, fieldLight, fieldDark, metal, metalLight, metalShadow, dark });
  return g;
}

// ------------------------------------------------------------------ crests (cx is the shield's centre line)

const L = (cx) => Math.floor(cx), R = (cx) => Math.ceil(cx);

function antlers(g, c) {
  const { y, z, metal, metalShadow } = c;
  // one antler as a face-connected path (beam out and up, three tines), mirrored to the other side
  const beam = [[0, 0], [0, 1], [-1, 1], [-1, 2], [-2, 2], [-2, 3], [-3, 3], [-3, 4], [-4, 4], [-4, 5]];
  const tines = [[-1, 3], [-1, 4], [-2, 4], [-2, 5], [-2, 6], [-3, 5], [-5, 5], [-4, 6]];
  for (const [side, bx] of [[1, L(c.cx)], [-1, R(c.cx)]]) {
    beam.forEach(([dx, dy], i) => g.set(bx + side * dx, y + dy, z, i < 3 ? metalShadow : metal));
    for (const [dx, dy] of tines) g.set(bx + side * dx, y + dy, z, metal);
  }
}

function oak(g, c) {
  const { y, z } = c;
  const leaf = style('clay', house('ashgrove', 'metal')), leafShade = style('clay', house('ashgrove', 'metalShadow'));
  const trunk = style('log', pal('Ink soft'));
  g.set(L(c.cx), y, z, trunk); g.set(R(c.cx), y, z, trunk); g.set(L(c.cx), y + 1, z, trunk); g.set(R(c.cx), y + 1, z, trunk);
  g.sample((x, yy, zz) => {
    const d = Math.hypot((x - (c.cx + 0.5)) / 4.2, (yy - (y + 3.6)) / 2.2, (zz - (z + 0.5)) / 1.6);
    return d <= 1 ? (yy < y + 3 ? leafShade : leaf) : null;
  }, [L(c.cx) - 5, y + 1, z - 2], [R(c.cx) + 5, y + 6, z + 3], { sub: 4, onlyEmpty: true });
}

function raven(g, c) {
  const { y, z } = c;
  const black = style('stone', pal('Iron 900')), sheen = style('stone', pal('Far ridge'));
  const beak = style('stone', house('corvane', 'metal')), eye = style('stone', pal('Ember hot'));
  const x = L(c.cx);
  // feet on the capstone, body, head facing front, beak with the silver key
  g.box(x - 1, y, z, x + 2, y + 1, z + 1, black);
  g.box(x, y + 2, z, x + 1, y + 2, z + 1, sheen);
  g.set(x, y + 3, z, black); g.set(x + 1, y + 3, z, black);
  g.set(x, y + 3, z - 1, black);
  ramp(g, x, y + 3, z - 2, beak, BACK, DOWN);
  g.set(x + 1, y + 3, z - 1, eye);
  ramp(g, x + 3, y + 1, z, black, WEST, DOWN);          // tail
  ramp(g, x - 2, y + 1, z, sheen, EAST, DOWN);          // folded wing tip
}

function bell(g, c) {
  const { y, z } = c;
  const bronze = style('stone', house('dunmere', 'metal')), bronzeDark = style('stone', house('dunmere', 'metalShadow'));
  const wood = style('wood', pal('Ink soft'));
  const x = L(c.cx);
  // a gallows frame, and a bell hung from its beam: flared mouth, waist, crown
  for (let k = 0; k <= 6; k++) { g.set(x - 3, y + k, z, wood); g.set(x + 4, y + k, z, wood); }
  for (let xx = x - 3; xx <= x + 4; xx++) g.set(xx, y + 7, z, wood);
  const half = [2.05, 1.55, 1.45, 1.3, 0.9];               // half-width per row, mouth to crown
  g.sample((px, py, pz) => {
    const row = Math.floor(py - (y + 1));
    if (row < 0 || row > 4 || Math.abs(pz - (z + 0.5)) > 1.5) return null;
    return Math.abs(px - (x + 1)) <= half[row] ? (row === 0 ? bronzeDark : bronze) : null;
  }, [x - 2, y + 1, z - 1], [x + 3, y + 5, z + 1], { sub: 4, fit: false, solidAt: 0.45, onlyEmpty: true });
  g.set(x, y + 6, z, bronzeDark); g.set(x + 1, y + 6, z, bronzeDark);
}

function brazier(g, c) {
  const { y, z } = c;
  const iron = ST.iron, ember = style('stone', house('halloran', 'metal')), hot = style('stone', house('halloran', 'metalLight')), deep = style('stone', house('halloran', 'metalShadow'));
  const x = L(c.cx);
  g.box(x, y, z, x + 1, y, z + 1, iron);
  g.box(x - 1, y + 1, z - 1, x + 2, y + 1, z + 2, iron);
  g.box(x, y + 1, z, x + 1, y + 1, z + 1, deep);
  g.box(x, y + 2, z, x + 1, y + 2, z + 1, ember);
  g.set(x, y + 3, z, hot); g.set(x + 1, y + 3, z + 1, hot); g.set(x + 1, y + 3, z, ember);
  g.set(x, y + 4, z, hot);
}

function eel(g, c) {
  const { y, z } = c;
  const silver = style('stone', house('merrin', 'metal')), shade = style('stone', house('merrin', 'metalShadow')), net = style('wood', house('merrin', 'fieldLight'));
  const x = L(c.cx);
  // an eel leaping in an arch out of a broken net: tail down on the left, head diving on the right
  const path = [[-4, 0], [-4, 1], [-3, 1], [-3, 2], [-2, 2], [-2, 3], [-1, 3], [0, 3], [1, 3], [2, 3], [2, 2], [3, 2], [3, 1], [4, 1], [4, 0]];
  path.forEach(([dx, dy], i) => g.set(x + dx, y + dy, z, i % 4 === 2 ? shade : silver));
  ramp(g, x - 5, y, z, silver, EAST, DOWN);               // tail fin
  g.set(x + 5, y, z, shade);                              // the head
  for (const [dx, dy] of [[-2, 0], [-1, 0], [1, 0], [2, 0], [0, 1], [-1, 1]]) g.set(x + dx, y + dy, z + 1, net);
}

const HOUSES = [
  ['varrow', 'Varrow', 'the Iron Stag', antlers],
  ['ashgrove', 'Ashgrove', 'the White Oak', oak],
  ['corvane', 'Corvane', 'the Black Raven', raven],
  ['dunmere', 'Dunmere', 'the Drowned Bell', bell],
  ['halloran', 'Halloran', 'the Ember Hound', brazier],
  ['merrin', 'Merrin', 'the Silver Eel', eel],
];

export default HOUSES.map(([key, name, sigil, crest]) => ({
  id: `house-${key}`,
  name: `House ${name} Monument`,
  description: `A stone stele with the shield of House ${name}, ${sigil}, in its own colours, and a crest on top.`,
  build: () => stele(key, crest),
}));
