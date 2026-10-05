// Shape test: not a monument. One row per single-block shape, one column per rotation, on a stone slab, so an admin
// can compare the game's real shapes with what art/tools/sculptor/shapes.json assumes (plugins/docs/RealmSculptor.md,
// "Shape test"). Place it with /sculpt place shape-test 0 while facing +z, so the sculpture's axes are the world's.
// Rows (z, front to back): Stair 1, Ramp 2, RampVar1-6 3-8, Peak 9, ThinWall 15. Columns (x, left to right):
// rotation index 0, 1, 2, 3, 4, 8. The gold post stands at the back right (+x, +z) corner.
import { Grid, ST, style, pal } from './lib/kit.mjs';

export const SHAPE_ROWS = [1, 2, 3, 4, 5, 6, 7, 8, 9, 15];
export const ROTATION_COLUMNS = [0, 1, 2, 3, 4, 8];
const ROW_COLOURS = ['Blood', 'Ember', 'Moss', 'Lapis', 'Verdigris', 'Afterglow', 'Parchment', 'Ink soft', 'Ember hot', 'Iron 200'];

export default {
  id: 'shape-test',
  name: 'Shape Test',
  description: 'Every single-block shape in six rotations on a slab, to check the shapes the tools assume against the game.',
  build() {
    const g = new Grid();
    const W = ROTATION_COLUMNS.length * 2 + 1, D = SHAPE_ROWS.length * 2 + 1;
    for (let x = 0; x < W; x++) for (let z = 0; z < D; z++) g.set(x, 0, z, (x + z) % 2 ? ST.stoneDark : ST.stoneMid);
    SHAPE_ROWS.forEach((prefab, r) => {
      const s = style('clay', pal(ROW_COLOURS[r]));
      ROTATION_COLUMNS.forEach((rot, c) => g.set(1 + c * 2, 1, 1 + r * 2, s, prefab, rot));
    });
    for (let y = 1; y <= 3; y++) g.set(W - 1, y, D - 1, ST.gold);
    return g;
  },
};
