// Shared helpers for the sculpture generators. Everything is deterministic: the same code gives the same blocks.
import { Grid, style } from '../../../tools/sculptor/voxel.mjs';
import { pal, house, HOUSE_KEYS_ORDER } from '../../../tools/sculptor/palette.mjs';
import { orient } from '../../../tools/sculptor/shapes.mjs';
import { turnPos, turnIndex } from '../../../tools/sculptor/rotations.mjs';

export { Grid, style, pal, house, orient, HOUSE_KEYS_ORDER as HOUSE_KEYS };

/** A stable pseudo-random number in [0, 1) for a cell and a salt. */
export function hash(x, y, z, salt = 0) {
  let h = 2166136261 ^ salt;
  for (const v of [x, y, z]) { h = Math.imul(h ^ (v & 0xffff), 16777619); h ^= h >>> 13; }
  h = Math.imul(h ^ (h >>> 16), 2246822507);
  return ((h ^ (h >>> 13)) >>> 0) / 4294967296;
}

/** Picks one of several styles by hash, weighted: pick([[s1, 3], [s2, 1]], x, y, z). */
export function pick(weighted, x, y, z, salt = 0) {
  const total = weighted.reduce((a, [, w]) => a + w, 0);
  let r = hash(x, y, z, salt) * total;
  for (const [s, w] of weighted) { if ((r -= w) < 0) return s; }
  return weighted[weighted.length - 1][0];
}

// Directions for orient(high, base): the side a ramp is tall on, and the side its flat base lies on.
export const UP = [0, 1, 0], DOWN = [0, -1, 0], EAST = [1, 0, 0], WEST = [-1, 0, 0], BACK = [0, 0, 1], FRONT = [0, 0, -1];
export const neg = (d) => d.map((v) => -v);

/** A ramp at a cell: tall on the `high` side, flat base on the `base` side. */
export function ramp(g, x, y, z, s, high, base = DOWN) {
  return g.set(x, y, z, s, 2, orient(high, base));
}

/** A square ring of ramps around a box top at height y, sloping outwards (a chamfer), from x0..x1, z0..z1. */
export function chamferRing(g, x0, x1, y, z0, z1, s, { corners = true } = {}) {
  for (let x = x0; x <= x1; x++) { ramp(g, x, y, z0 - 1, s, BACK); ramp(g, x, y, z1 + 1, s, FRONT); }
  for (let z = z0; z <= z1; z++) { ramp(g, x0 - 1, y, z, s, EAST); ramp(g, x1 + 1, y, z, s, WEST); }
  if (corners) for (const [x, z] of [[x0 - 1, z0 - 1], [x1 + 1, z0 - 1], [x0 - 1, z1 + 1], [x1 + 1, z1 + 1]]) g.set(x, y, z, s);
  return g;
}

/** A filled stepped plinth: steps = [[halfX, halfZ, style], ...] bottom to top, centred on (cx, cz), from y0. */
export function stepped(g, cx, cz, y0, steps, { chamfer = false } = {}) {
  steps.forEach(([hx, hz, s], i) => {
    g.box(cx - hx, y0 + i, cz - hz, cx + hx, y0 + i, cz + hz, typeof s === 'function' ? null : s);
    if (typeof s === 'function') for (let x = cx - hx; x <= cx + hx; x++) for (let z = cz - hz; z <= cz + hz; z++) g.set(x, y0 + i, z, s(x, y0 + i, z));
  });
  return g;
}

export const ST = {
  // stone and iron
  stoneLight: style('stone', pal('Iron 200')),
  stoneMid: style('stone', house('corvane', 'metalShadow')),
  stoneWarm: style('stone', pal('Haze')),
  stoneDark: style('cobblestone', pal('Iron 600')),
  stoneDeep: style('cobblestone', pal('Far ridge')),
  blackStone: style('stone', pal('Iron 900')),
  blackStone2: style('stone', pal('Iron 800')),
  iron: style('reinforced', pal('Iron 700')),
  ironHi: style('reinforced', pal('Iron 600')),
  ironWorn: style('reinforced', pal('Iron 200')),
  rust: style('reinforced', pal('Haze')),
  rustDeep: style('reinforced', pal('Ink soft')),
  rustHot: style('reinforced', pal('Afterglow')),
  // steel, darkest to brightest (house metal greys from the palette)
  steelDark: style('reinforced', house('varrow', 'metalShadow')),
  steel: style('reinforced', house('corvane', 'metalShadow')),
  steelLight: style('reinforced', house('varrow', 'metal')),
  steelEdge: style('reinforced', house('corvane', 'metal')),
  // gold, cloth, wood
  gold: style('thatch', pal('Ember')),
  goldHot: style('thatch', pal('Ember hot')),
  goldDeep: style('thatch', pal('Ember deep')),
  leather: style('log', pal('Ink')),
  leatherSoft: style('log', pal('Ink soft')),
  wood: style('wood', pal('Ink soft')),
  parchment: style('clay', pal('Parchment')),
  parchment2: style('clay', pal('Parchment 2')),
  blood: style('clay', pal('Blood')),
  moss: style('sod', pal('Moss')),
};

export const houseStyles = (key) => ({
  field: style('clay', house(key, 'field')),
  fieldLight: style('clay', house(key, 'fieldLight')),
  fieldDark: style('clay', house(key, 'fieldDark')),
  metal: style('stone', house(key, 'metal')),
  metalLight: style('stone', house(key, 'metalLight')),
  metalShadow: style('stone', house(key, 'metalShadow')),
});

/**
 * A grid drawn in a site's frame, turned into the piece's own frame for a placement `turn` (quarter-turns, as
 * /sculpt place and art/tools/sculptor/site.mjs use them): every cell and every block rotation is turned back by
 * `turn`, so placing the piece with that turn puts each block where it was drawn. Lets a generator draw in the frame
 * of the site plan (art/sculptures/src/lib/arrival-site.mjs) instead of mirroring coordinates by hand.
 */
export function fromSiteFrame(site, turn) {
  const k = (4 - (((turn % 4) + 4) % 4)) % 4;
  const g = new Grid();
  for (const c of site) {
    const [x, , z] = turnPos([c.x, 0, c.z], k);
    g.set(x, c.y, z, { mat: c.mat, color: c.color }, c.prefab, c.prefab === 0 ? 0 : turnIndex(c.rot, k));
  }
  return g;
}
