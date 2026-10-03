// The Herald's Pillar: where proclamations are cried. A fluted stone column on a stepped base, a parchment notice
// with a red seal on its face, gold herald's pennants on its sides, a gold capital and a burning brazier on top.
// Small on purpose (about 140 blocks): it is the first sculpture to place on a real server.
import { Grid, ST, pick, ramp, UP, FRONT, BACK } from './lib/kit.mjs';

export default {
  id: 'heralds-pillar',
  name: "Herald's Pillar",
  description: 'A fluted stone column with a parchment notice, gold pennants and a brazier, where proclamations are cried.',
  build() {
    const g = new Grid();
    const cx = 2, cz = 2;
    // base: a sunk 5 x 5 slab and a 3 x 3 die
    for (let x = 0; x <= 4; x++) for (let z = 0; z <= 4; z++) g.set(x, 0, z, pick([[ST.stoneDark, 4], [ST.stoneDeep, 1]], x, 0, z, 1));
    for (let x = 1; x <= 3; x++) for (let z = 1; z <= 3; z++) g.set(x, 1, z, ST.stoneMid);
    // the column: 3 x 3, a darker flute down the middle of each face
    for (let y = 2; y <= 8; y++)
      for (let x = cx - 1; x <= cx + 1; x++)
        for (let z = cz - 1; z <= cz + 1; z++) g.set(x, y, z, x !== cx && z !== cz ? ST.stoneLight : ST.stoneMid);
    // the notice on the front face, one block proud: parchment over a red seal
    for (let y = 4; y <= 6; y++) g.set(cx, y, cz - 2, y === 4 ? ST.blood : y === 6 ? ST.parchment2 : ST.parchment);
    // the capital: a gold band and a dark abacus
    for (let x = cx - 1; x <= cx + 1; x++) for (let z = cz - 1; z <= cz + 1; z++) { g.set(x, 9, z, ST.gold); g.set(x, 10, z, ST.stoneDark); }
    // herald's pennants hung from the capital on both sides: gold, red-tipped
    for (const x of [cx - 2, cx + 2]) {
      g.set(x, 9, cz, ST.goldDeep);
      g.set(x, 8, cz, ST.gold);
      g.set(x, 7, cz, ST.gold);
      ramp(g, x, 6, cz, ST.blood, UP, BACK);
    }
    // the brazier: an iron bowl, embers and a flame
    g.set(cx, 11, cz, ST.iron);
    for (const [dx, dz] of [[-1, 0], [1, 0], [0, -1], [0, 1]]) g.set(cx + dx, 11, cz + dz, ST.ironHi);
    g.set(cx, 12, cz, ST.goldHot);
    return g;
  },
};
