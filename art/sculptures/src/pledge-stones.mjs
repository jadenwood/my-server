// The pledge stones (docs/arrival-design.md 4.2 and 3.7): one 3 x 3 stone before each house monument. A plinth in the
// house's dark field colour with its raised centre in the house metal. A newcomer who stands still on the centre for
// 3 s may "look to" that house. One generator, six pieces; symmetric, so the placement turn does not matter.
import { Grid, style, house, HOUSE_KEYS } from './lib/kit.mjs';

const title = (key) => key[0].toUpperCase() + key.slice(1);

function stone(key) {
  const g = new Grid();
  const plinth = style('stone', house(key, 'fieldDark'));
  const centre = style('clay', house(key, 'metal'));
  for (let x = -1; x <= 1; x++) for (let z = -1; z <= 1; z++) g.set(x, 0, z, plinth);
  g.set(0, 1, 0, centre);
  return g;
}

export default HOUSE_KEYS.map((key) => ({
  id: `pledge-stone-${key}`,
  name: `Pledge stone of House ${title(key)}`,
  description: `A 3 x 3 plinth in House ${title(key)}'s dark field with a raised centre in its metal, before its monument on the Gatehouse road.`,
  build: () => stone(key),
}));
