// The processional (docs/arrival-design.md 4.2): the 7-cell road from the Gatehouse to the Hearth ring, a cobbled path
// left in the stone's own colour between dark stone kerbs. Two pieces, because one would be longer than 64 cells:
// processional-a (39 long, from the gate through the forecourt and the first pair) and processional-b (38 long, on to
// the hearth ring). Optional: skip it where the ground is not flat to within one block over 45 m. Drawn along its own
// z, which is the site axis; placed with turn 0.
import { Grid, style, pal } from './lib/kit.mjs';
import { AVENUE } from './lib/arrival-site.mjs';

const PATH = style('cobblestone', null);
const KERB = style('stone', pal('Iron 600'));

function road(z0, z1) {
  const g = new Grid();
  for (let z = z0; z <= z1; z++) for (let x = -AVENUE.half; x <= AVENUE.half; x++) g.set(x, 0, z, Math.abs(x) === AVENUE.half ? KERB : PATH);
  return g;
}

export default [
  {
    id: 'processional-a',
    name: 'The Processional, gate half',
    description: 'A 7-wide cobbled road between dark kerbs, from the Gatehouse of the Unwritten through the forecourt and the first pair of banners.',
    build: () => road(AVENUE.a.z0, AVENUE.a.z1),
  },
  {
    id: 'processional-b',
    name: 'The Processional, Hearth half',
    description: 'A 7-wide cobbled road between dark kerbs, from the second pair of banners to the Hearth ring.',
    build: () => road(AVENUE.b.z0, AVENUE.b.z1),
  },
];
