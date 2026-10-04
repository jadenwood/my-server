// The Hearth ring (docs/arrival-design.md 4.2): the raised round dais the Hearth fire burns on, 15 x 15 and 2 high.
// Two stepped seating rings: a low outer step in Iron 700 and the dais rim in Iron 600. The pit in the middle is left
// open for the staff-built fire pit. The 24 cells of the ember band at radius 5 are left EMPTY: RealmArrival places
// them (clay, Ember deep at rest, Ember hot in a flare) on the dais top, where they ring the fire one block proud so
// they show from the Gatehouse 100 m away. The dais under them is part of this piece, so they always rest on stone.
// Symmetric; drawn centred on the fire.
import { Grid, style, pal } from './lib/kit.mjs';
import { HEARTH, RING, ringR } from './lib/arrival-site.mjs';

const STEP = style('stone', pal('Iron 700'));
const DAIS = style('stone', pal('Iron 600'));

export default {
  id: 'hearth-ring',
  name: 'The Hearth ring',
  description: 'The raised round dais of the Hearth fire: an outer step, a dark rim to sit on, a pit for the fire, and the kerb the ember band rests on.',
  build() {
    const g = new Grid();
    const h = HEARTH.half;
    for (let dx = -h; dx <= h; dx++) for (let dz = -h; dz <= h; dz++) {
      const r = ringR(dx, dz);
      if (r > RING.outer || r < RING.pit) continue;
      g.set(dx, 0, dz, STEP);
      if (r <= RING.dais) g.set(dx, 1, dz, DAIS);
    }
    return g;
  },
};
