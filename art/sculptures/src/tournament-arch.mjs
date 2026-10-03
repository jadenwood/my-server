// The Tournament Arch: the gate of the Royal Tournament lists. Two crenellated stone towers joined by a beam, the
// banners of the six great houses hanging in a row under it in their own colours, and the tournament's gold-rimmed
// red shield over the middle. Riders pass under the banners.
import { Grid, ST, style, house, pick, ramp, UP, DOWN, EAST, WEST, BACK, FRONT, HOUSE_KEYS } from './lib/kit.mjs';

const TW = 4;                          // tower width and depth
const SPAN = 11;                       // the passage between the towers
const W = TW * 2 + SPAN;               // 19
const D = 4;                           // depth (z 0..3)
const BEAM0 = 8;                       // the beam's lowest layer

export default {
  id: 'tournament-arch',
  name: 'The Tournament Arch',
  description: 'Two crenellated towers and a beam hung with the banners of the six great houses: the gate of the Royal Tournament.',
  build() {
    const g = new Grid();
    const stone = (x, y, z) => pick([[ST.stoneLight, 4], [ST.stoneWarm, 3], [ST.stoneDark, 1]], x, y, z, 41);

    // a road of cobbles through the gate
    for (let x = TW; x < TW + SPAN; x++) for (let z = 0; z < D; z++) g.set(x, 0, z, pick([[ST.stoneDark, 3], [ST.stoneDeep, 2]], x, 0, z, 42));
    // the towers, with a dark arrow slit on each front and a corbelled, crenellated top
    for (const x0 of [0, TW + SPAN]) {
      for (let y = 0; y <= 12; y++)
        for (let x = x0; x < x0 + TW; x++)
          for (let z = 0; z < D; z++) {
            const slit = z === 0 && y >= 5 && y <= 7 && x === x0 + (x0 === 0 ? 2 : 1);
            g.set(x, y, z, y === 0 ? ST.stoneDeep : slit ? ST.blackStone : stone(x, y, z));
          }
      for (let x = x0 - 1; x <= x0 + TW; x++) for (let z = -1; z <= D; z++) {
        const ring = x === x0 - 1 || x === x0 + TW || z === -1 || z === D;
        if (!ring) continue;
        const outward = x === x0 - 1 ? WEST : x === x0 + TW ? EAST : z === -1 ? FRONT : BACK;
        const corner = (x === x0 - 1 || x === x0 + TW) && (z === -1 || z === D);
        if (corner) g.set(x, 13, z, ST.stoneDark);
        else ramp(g, x, 12, z, ST.stoneDark, UP, outward === WEST ? EAST : outward === EAST ? WEST : outward === FRONT ? BACK : FRONT);
        g.set(x, 13, z, ST.stoneDark);
        if ((x + z) % 2 === 0) g.set(x, 14, z, ST.stoneLight);   // merlons
      }
    }
    // the beam across the passage, two deep, with a crenellated walk on top
    for (let x = TW; x < TW + SPAN; x++)
      for (let y = BEAM0; y <= BEAM0 + 2; y++)
        for (let z = 1; z <= 2; z++) g.set(x, y, z, y === BEAM0 ? ST.stoneDark : stone(x, y, z));
    for (let x = TW; x < TW + SPAN; x++) if (x % 2 === 0) { g.set(x, BEAM0 + 3, 1, ST.stoneLight); g.set(x, BEAM0 + 3, 2, ST.stoneLight); }
    // corbels where the beam meets the towers
    ramp(g, TW, BEAM0 - 1, 1, ST.stoneDark, WEST, UP);
    ramp(g, TW, BEAM0 - 1, 2, ST.stoneDark, WEST, UP);
    ramp(g, TW + SPAN - 1, BEAM0 - 1, 1, ST.stoneDark, EAST, UP);
    ramp(g, TW + SPAN - 1, BEAM0 - 1, 2, ST.stoneDark, EAST, UP);

    // six house banners under the beam, in the passage plane, high enough to ride under
    HOUSE_KEYS.forEach((key, i) => {
      const x = TW + i * 2;
      const field = style('clay', house(key, 'field')), metal = style('stone', house(key, 'metal')), dark = style('clay', house(key, 'fieldDark'));
      g.set(x, BEAM0 - 1, 1, metal);
      g.set(x, BEAM0 - 2, 1, field);
      g.set(x, BEAM0 - 3, 1, i % 2 ? field : metal);
      g.set(x, BEAM0 - 4, 1, field);
      ramp(g, x, BEAM0 - 5, 1, dark, UP, FRONT);
    });

    // the tournament's shield over the middle of the beam: red, rimmed in gold
    const mx = TW + Math.floor(SPAN / 2);
    for (let x = mx - 1; x <= mx + 1; x++) for (let y = BEAM0 + 3; y <= BEAM0 + 5; y++) g.set(x, y, 1, x === mx && y <= BEAM0 + 4 ? ST.blood : ST.gold);
    g.set(mx, BEAM0 + 3, 2, ST.stoneDark);
    ramp(g, mx - 1, BEAM0 + 2, 0, ST.gold, BACK, UP);
    ramp(g, mx + 1, BEAM0 + 2, 0, ST.gold, BACK, UP);
    g.set(mx, BEAM0 + 2, 0, ST.goldDeep);
    g.set(mx, BEAM0 + 6, 1, ST.goldHot);
    return g;
  },
};
