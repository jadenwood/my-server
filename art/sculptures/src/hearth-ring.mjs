// The Hearth ring (docs/arrival-design.md 4.2): the raised round dais the Hearth fire burns on, 15 x 15 and 2 high.
// Two stepped seating rings: a low outer step in Iron 700 and the dais in Iron 600, with a 3 x 3 hearthstone of
// Iron 800 in the middle. Nothing of this piece stands on the dais: the staff-built fire pit burns on the hearthstone
// one block up so its flames show over the band, and the 24 cells of the ember band at radius 5 sit ON TOP of the
// 2-high ring (the layer above the dais, y 2), outside this piece altogether; RealmArrival places them there (clay,
// Ember deep at rest, Ember hot in a flare): a low parapet round the fire that shows from the Gatehouse 100 m away. The
// dais under them is part of this piece, so they always rest on stone. Symmetric; drawn centred on the fire.
import { Grid, style, pal } from './lib/kit.mjs';
import { HEARTH, RING, ringR } from './lib/arrival-site.mjs';

const STEP = style('stone', pal('Iron 700'));
const DAIS = style('stone', pal('Iron 600'));
const HEARTHSTONE = style('stone', pal('Iron 800'));

export default {
  id: 'hearth-ring',
  name: 'The Hearth ring',
  description: 'The raised round dais of the Hearth fire: an outer step, the dais with a hearthstone for the fire pit, and the bed the ember band rests on.',
  build() {
    const g = new Grid();
    const h = HEARTH.half;
    for (let dx = -h; dx <= h; dx++) for (let dz = -h; dz <= h; dz++) {
      const r = ringR(dx, dz);
      if (r > RING.outer) continue;
      g.set(dx, 0, dz, STEP);
      if (r <= RING.dais) g.set(dx, 1, dz, Math.max(Math.abs(dx), Math.abs(dz)) <= 1 ? HEARTHSTONE : DAIS);
    }
    return g;
  },
};
