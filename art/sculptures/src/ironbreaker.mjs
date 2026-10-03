// The Ironbreaker: a colossal crude slab greatsword of siege iron, blunt and chipped, its hilt wrapped in leather,
// driven point-first into a stepped stone plinth that cracked and heaved around it. 11 x 16 x 9 blocks.
import { Grid, ST, pick, hash, ramp, UP, DOWN, EAST, WEST, BACK, FRONT } from './lib/kit.mjs';

const CX = 5, CZ = 4;                 // the blade's centre column
const TOP = 2;                        // the plinth's top layer; the blade enters it

export default {
  id: 'ironbreaker',
  name: 'The Ironbreaker',
  description: 'A colossal slab greatsword of siege iron, blunt and chipped, driven point-first into a cracked stone plinth.',
  build() {
    const g = new Grid();

    // The plinth: a sunk base slab and two crisp steps of weathered stone.
    const steps = [[5, 4, (x, y, z) => pick([[ST.stoneDeep, 3], [ST.stoneDark, 2]], x, y, z, 3)],
      [4, 3, (x, y, z) => pick([[ST.stoneWarm, 4], [ST.stoneDark, 1]], x, y, z, 4)],
      [3, 2, (x, y, z) => pick([[ST.stoneLight, 4], [ST.stoneWarm, 2]], x, y, z, 5)]];
    steps.forEach(([hx, hz, s], y) => {
      for (let x = CX - hx; x <= CX + hx; x++) for (let z = CZ - hz; z <= CZ + hz; z++) g.set(x, y, z, s(x, y, z));
    });

    // The blade: five wide with a three-deep spine, from inside the top step to the guard. Its last layer above the
    // stone narrows on ramps, so the buried point reads.
    for (let y = TOP; y <= 10; y++) {
      for (let x = CX - 2; x <= CX + 2; x++) {
        const off = Math.abs(x - CX);
        const edge = off === 2;
        if (edge && y >= 5 && y <= 9 && hash(x, y, 0, 11) < 0.28) continue;      // chips out of both edges
        const depth = off === 0 ? 1 : 0;
        for (let z = CZ - depth; z <= CZ + depth; z++) {
          let s;
          if (edge) s = pick([[ST.steelEdge, 3], [ST.steelLight, 2]], x, y, z, 5);
          else if (off === 0) s = z === CZ ? ST.steelDark : pick([[ST.steelDark, 3], [ST.iron, 1]], x, y, z, 7);
          else s = pick([[ST.steel, 5], [ST.steelDark, 1]], x, y, z, 6);
          if (off <= 1 && y >= 7 && y <= 10 && hash(x, y, z, 9) < (y - 6) * 0.12) s = y >= 9 ? ST.rustDeep : ST.rust;   // rust bleeding from the guard
          if (edge && y === TOP + 1) { ramp(g, x, y, z, s, UP, x < CX ? EAST : WEST); continue; }
          g.set(x, y, z, s);
        }
      }
    }
    // a bite hacked clean through one edge, so the silhouette reads as battered from far away
    g.del(CX + 2, 7, CZ); g.del(CX + 2, 8, CZ);
    ramp(g, CX + 1, 8, CZ, ST.steelEdge, DOWN, WEST);

    // Heaved stone around the blade's foot: slabs tipped up against the iron, and dark cracks running out.
    ramp(g, CX - 3, TOP + 1, CZ, ST.stoneDark, EAST);
    ramp(g, CX + 3, TOP + 1, CZ, ST.stoneDark, WEST);
    ramp(g, CX, TOP + 1, CZ - 2, ST.stoneWarm, BACK);
    ramp(g, CX, TOP + 1, CZ + 2, ST.stoneWarm, FRONT);
    ramp(g, CX - 1, TOP + 1, CZ - 2, ST.stoneDark, BACK);
    ramp(g, CX + 1, TOP + 1, CZ + 2, ST.stoneDark, FRONT);
    for (const [x, y, z] of [[CX - 1, 2, CZ - 2], [CX - 2, 1, CZ - 3], [CX - 3, 0, CZ - 4], [CX + 2, 2, CZ + 2], [CX + 3, 1, CZ + 3], [CX + 4, 0, CZ + 4],
      [CX + 3, 2, CZ - 1], [CX + 4, 1, CZ - 2], [CX + 5, 0, CZ - 2]]) {
      const c = g.get(x, y, z);
      if (c && !c.prefab) g.set(x, y, z, ST.blackStone);
    }

    // The guard: a crude bar of black iron, three deep in the middle, its ends hammered down.
    for (let x = CX - 4; x <= CX + 4; x++) {
      const mid = Math.abs(x - CX) <= 2;
      for (let z = CZ - (mid ? 1 : 0); z <= CZ + (mid ? 1 : 0); z++) g.set(x, 11, z, pick([[ST.iron, 3], [ST.blackStone2, 1]], x, 11, z, 8));
    }
    ramp(g, CX - 5, 11, CZ, ST.iron, EAST, DOWN);
    ramp(g, CX + 5, 11, CZ, ST.iron, WEST, DOWN);
    ramp(g, CX - 3, 10, CZ, ST.iron, UP, EAST);
    ramp(g, CX + 3, 10, CZ, ST.iron, UP, WEST);

    // The grip: round-ish, wrapped in two leathers in a spiral.
    for (let y = 12; y <= 14; y++)
      for (const [dx, dz] of [[0, 0], [-1, 0], [1, 0], [0, -1], [0, 1]]) g.set(CX + dx, y, CZ + dz, (y + dx - dz + 30) % 3 === 0 ? ST.leatherSoft : ST.leather);

    // The pommel: a heavy iron block, worn bright on its edges, one dull rivet.
    for (let x = CX - 2; x <= CX + 2; x++)
      for (let z = CZ - 1; z <= CZ + 1; z++) {
        const corner = Math.abs(x - CX) === 2 && z !== CZ;
        if (corner) continue;
        g.set(x, 15, z, Math.abs(x - CX) === 2 ? ST.steel : ST.steelDark);
      }
    ramp(g, CX - 2, 15, CZ, ST.steel, EAST, DOWN);
    ramp(g, CX + 2, 15, CZ, ST.steel, WEST, DOWN);
    g.set(CX, 15, CZ - 1, ST.goldDeep);
    return g;
  },
};
