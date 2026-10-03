// The Old Throne dais, after the realm emblem (art/logo/realm-emblem.svg): a throne of black stone with a pointed-arch
// back picked out in gold, a gold finial, the crown resting on its red cushion inside the arch ("the crown belongs to
// the seat, not the blood"), stone arms, and the three steps of the throne hill with a red runner and two braziers.
import { Grid, ST, style, pal, pick, ramp, UP, DOWN, EAST, WEST, BACK, FRONT } from './lib/kit.mjs';

const CX = 7;                          // centre column (the dais is 15 wide, x 0..14)
const ACX = CX + 0.5;                  // arch centre in continuous coordinates
const SPRING = 7;                      // where the arch starts to curve
const BACK_Z = 8;                      // the throne back stands on z 8..9

// Equilateral pointed arch of half-width h above the spring line: inside both circles of radius 2h centred on the
// opposite springing points.
function inArch(u, v, h) {
  if (Math.abs(u) > h) return false;
  if (v <= 0) return true;
  return (u + h) ** 2 + v ** 2 <= (2 * h) ** 2 && (u - h) ** 2 + v ** 2 <= (2 * h) ** 2;
}

const BLACK = ST.blackStone, BLACK2 = ST.blackStone2;
const GOLD = ST.gold, GOLD_HOT = ST.goldHot, GOLD_DEEP = ST.goldDeep;
const CUSHION = ST.blood;

export default {
  id: 'old-throne',
  name: 'The Old Throne',
  description: 'The Old Throne of black stone on its three-step dais, the pointed arch picked out in gold and the crown resting on the seat.',
  build() {
    const g = new Grid();

    // The throne hill: three steps, a red runner up the middle.
    const dais = [[7, 6, ST.stoneDark], [6, 5, ST.stoneWarm], [5, 4, ST.stoneLight]];
    dais.forEach(([hx, hz, s], y) => {
      const cz = 5;
      for (let x = CX - hx; x <= CX + hx; x++)
        for (let z = cz - hz; z <= cz + hz; z++) {
          const runner = Math.abs(x - CX) <= 1 && z <= 6;
          g.set(x, y, z, runner ? (Math.abs(x - CX) === 1 ? style('clay', pal('Ember deep')) : CUSHION) : pick([[s, 5], [ST.stoneDark, 1]], x, y, z, 21 + y));
        }
    });

    // The back: a thick pointed-arch frame of black stone, two deep, its opening rimmed in gold on the front face.
    const outerH = 4.5, innerH = 2.5;
    g.sample((x, y, z) => {
      const u = x - ACX, v = y - SPRING;
      if (y < 3 || !inArch(u, v, outerH)) return null;
      if (y >= 4 && inArch(u, v, innerH)) return null;
      if (z >= BACK_Z + 1) return BLACK2;
      const d = 0.75;
      const rim = y >= 4 - d && [[d, 0], [-d, 0], [0, d], [0, -d], [d, d], [-d, d]].some(([du, dv]) => y + dv >= 4 && inArch(u + du, v + dv, innerH));
      return rim ? GOLD : BLACK;
    }, [CX - 5, 3, BACK_Z], [CX + 5, 16, BACK_Z + 1], { sub: 4 });

    // The finial: a gold diamond on the point.
    const top = Math.max(...[...g].filter((c) => c.x === CX).map((c) => c.y));
    g.set(CX, top + 1, BACK_Z, GOLD_HOT);

    // The seat and its red cushion, the arms with gold caps.
    for (let x = CX - 3; x <= CX + 3; x++) for (let z = 5; z <= 7; z++) g.set(x, 3, z, Math.abs(x - CX) === 3 ? BLACK : BLACK2);
    for (let x = CX - 2; x <= CX + 2; x++) for (let z = 5; z <= 7; z++) g.set(x, 4, z, CUSHION);
    for (const ax of [CX - 4, CX + 4]) {
      for (let y = 3; y <= 4; y++) for (let z = 5; z <= 7; z++) g.set(ax, y, z, BLACK);
      for (let z = 4; z <= 7; z++) g.set(ax, 5, z, z === 4 ? GOLD : GOLD_DEEP);
      g.set(ax, 4, 4, BLACK2);
    }

    // The crown, resting on the cushion: a gold band and three points, the middle one tallest.
    for (let x = CX - 1; x <= CX + 1; x++) g.set(x, 5, 6, GOLD);
    g.set(CX - 1, 6, 6, GOLD_HOT); g.set(CX + 1, 6, 6, GOLD_HOT);
    g.set(CX, 6, 6, GOLD); g.set(CX, 7, 6, GOLD_HOT);
    g.set(CX, 5, 5, ST.blood);                 // a ruby on the band's front

    // Braziers at the front corners of the top step.
    for (const bx of [CX - 5, CX + 5]) {
      g.set(bx, 3, 1, BLACK); g.set(bx, 4, 1, BLACK2);
      g.set(bx, 5, 1, ST.iron);
      g.set(bx, 6, 1, ST.gold);
      g.set(bx, 7, 1, ST.goldHot);
    }
    return g;
  },
};
