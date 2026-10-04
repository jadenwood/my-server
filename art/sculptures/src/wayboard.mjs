// The wayboard (docs/arrival-design.md 4.2 and 4.3): where the Hearth's three roads are shown. A dark stone base, a
// back of spruce boards between six posts, five bays for the staff-placed signs (W1-W5: Three Roads, proclamation,
// event, standings, Other Banners) and a gold rail along the top. Stands across the axis beyond the fire with its
// front (-z) toward the fire; placed with turn 0.
import { Grid, style, pal } from './lib/kit.mjs';

const BASE = style('stone', pal('Iron 600'));
const POST = style('spruce', pal('Ink'));
const BOARD = style('spruce', pal('Ink soft'));
const RAIL = style('clay', pal('Ember'));

export const WIDTH = 15;
export const POSTS = [0, 3, 6, 8, 11, 14];
// The five bays, left to right as seen from the fire: [first x, last x] in the piece's own frame.
export const BAYS = [[1, 2], [4, 5], [7, 7], [9, 10], [12, 13]];

export default {
  id: 'wayboard',
  name: 'The Hearth wayboard',
  description: 'A long notice board beyond the Hearth fire: a dark base, spruce posts and boards with five bays for signs, and a gold rail.',
  build() {
    const g = new Grid();
    for (let x = 0; x < WIDTH; x++) for (let z = 0; z <= 1; z++) g.set(x, 0, z, BASE);
    for (let x = 0; x < WIDTH; x++) for (let y = 1; y <= 3; y++) g.set(x, y, 1, POSTS.includes(x) ? POST : BOARD);
    for (const x of POSTS) for (let y = 1; y <= 3; y++) g.set(x, y, 0, POST);
    for (let x = 0; x < WIDTH; x++) for (let z = 0; z <= 1; z++) g.set(x, 4, z, RAIL);
    return g;
  },
};
