// The Gatehouse of the Unwritten (docs/arrival-design.md 4.2): a walled court of pale stone below the Hearth, where
// every newcomer wakes. Parchment walls two thick and nine high with an ember string course, two corner towers at the
// front with dark caps and arrow slits, wooden eaves two cells in from the wall tops round an open court, six
// identical arrival stones inlaid in the dark floor, a gold line before the gate, and over the gate, inside and out,
// a blank page: the Chronicle's page that is not written yet. The Pilgrim's Stair in the left tower climbs to a sill
// in the outer wall, so nobody is ever shut in.
//
// The portcullis is its own piece (gatehouse-portcullis) because RealmArrival owns those cells: it builds them with
// /arrival admin gate build and takes them down row by row when the gate opens. Drawn in the site frame of
// lib/arrival-site.mjs (the gate toward +z) and turned into the piece's own frame (front -z) for a placement turn of 2.
import { Grid, style, pal, fromSiteFrame } from './lib/kit.mjs';
import { GH } from './lib/arrival-site.mjs';

const S = {
  floor: style('cobblestone', pal('Iron 700')),
  wall: style('stone', pal('Parchment 2')),
  edge: style('stone', pal('Parchment edge')),
  course: style('clay', pal('Ember')),
  trim: style('reinforced', pal('Iron 600')),
  cap: style('stone', pal('Iron 800')),
  eave: style('wood', pal('Ink soft')),
  stone: style('clay', pal('Parchment')),
  border: style('clay', pal('Iron 900')),
  gold: style('clay', pal('Ember hot')),
  page: style('clay', pal('Parchment')),
  slit: style('stone', pal('Iron 900')),
  portcullis: style('reinforced', pal('Iron 900')),
};

const inTower = (x, z) => z >= GH.towerZ0 && GH.towers.some((t) => x >= t.x0 && x <= t.x1);
const inWall = (x, z) => x <= GH.x0 + 1 || x >= GH.x1 - 1 || z <= GH.z0 + 1 || z >= GH.z1 - 1;
const inGate = (x, y, z) => z >= GH.gate.z && x >= GH.gate.x0 && x <= GH.gate.x1 && y >= GH.gate.y0 && y <= GH.gate.y1;
const isShaft = (x, z) => GH.pilgrim.shaft.some(([a, b]) => a === x && b === z);

function gatehouse() {
  const g = new Grid();
  const { x0, x1, z0, z1 } = GH;

  // The floor: dark cobbles, the six stones in 3 x 3 dark borders, the gold line.
  for (let x = x0; x <= x1; x++) for (let z = z0; z <= z1; z++) g.set(x, 0, z, S.floor);
  for (const [, sx, sz] of GH.stones) {
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) g.set(sx + dx, 0, sz + dz, S.border);
    g.set(sx, 0, sz, S.stone);
  }
  for (let x = GH.goldLine.x0; x <= GH.goldLine.x1; x++) g.set(x, 0, GH.goldLine.z, S.gold);

  // Walls, towers and the raised gate front.
  const midGate = (x) => x > GH.towers[0].x1 && x < GH.towers[1].x0;
  for (let x = x0; x <= x1; x++) for (let z = z0; z <= z1; z++) {
    const tower = inTower(x, z), wall = inWall(x, z);
    if (!tower && !wall) continue;
    const top = tower ? GH.towerTop - 1 : z >= z1 - 1 && midGate(x) ? GH.gateTop : GH.wallTop;
    for (let y = 1; y <= top; y++) {
      if (inGate(x, y, z)) continue;
      if (tower && isShaft(x, z) && y <= GH.towerTop - 3) continue;
      g.set(x, y, z, wallStyle(x, y, z, tower, top));
    }
  }

  // Merlons: tower tops, and the gate front's crenels either side of the page.
  for (const t of GH.towers) for (let x = t.x0; x <= t.x1; x++) for (let z = GH.towerZ0; z <= z1; z++) {
    const rim = x === t.x0 || x === t.x1 || z === GH.towerZ0 || z === z1;
    if (rim && (x + z) % 2 === 0) g.set(x, GH.towerTop, z, S.cap);
  }
  for (let x = GH.towers[0].x1 + 1; x < GH.towers[1].x0; x++) {
    if (Math.abs(x) <= 3) continue;
    if (x % 2 !== 0) for (const z of [z1 - 1, z1]) g.set(x, GH.gateTop + 1, z, S.wall);
  }

  // The gate: iron jambs and lintel round the gap, and over it, inside and out, a pointed tympanum of blank parchment
  // outlined in iron with a gold keystone: the Chronicle's page that is not written yet.
  const { gate } = GH;
  const head = gate.y1 + 1;
  for (const z of [gate.z, gate.z + 1]) {
    for (let y = gate.y0; y <= head; y++) { g.set(gate.x0 - 1, y, z, S.trim); g.set(gate.x1 + 1, y, z, S.trim); }
    for (let x = gate.x0 - 1; x <= gate.x1 + 1; x++) g.set(x, head, z, S.trim);
    for (let i = 0; i < 3; i++) {
      const y = head + 1 + i, w = 2 - i;
      for (let x = -w; x <= w; x++) g.set(x, y, z, S.page);
      g.set(-w - 1, y, z, S.trim); g.set(w + 1, y, z, S.trim);
    }
    g.set(0, head + 4, z, S.gold);
  }

  // Arrow slits on the towers' outer faces.
  for (const t of GH.towers) {
    const mid = t.x0 < 0 ? t.x0 + 1 : t.x1 - 1;
    for (const y of [8, 9]) g.set(mid, y, z1, S.slit);
    const side = t.x0 < 0 ? t.x0 : t.x1;
    for (const y of [8, 9]) g.set(side, y, z1 - 2, S.slit);
  }

  // Eaves: a wooden walk two cells in from the wall tops all round the court, under the towers' shoulders.
  for (let x = x0 + 2; x <= x1 - 2; x++) for (let z = z0 + 2; z <= z1 - 2; z++) {
    const ring = x <= x0 + 1 + GH.eave || x >= x1 - 1 - GH.eave || z <= z0 + 1 + GH.eave || z >= z1 - 1 - GH.eave;
    if (!ring || inTower(x, z)) continue;
    g.set(x, GH.wallTop, z, S.eave);
  }

  // The Pilgrim's Stair: a door from the court into the left tower, two steps, and the sill out through the outer wall.
  const p = GH.pilgrim;
  for (let y = 1; y <= 2; y++) g.del(p.door[0], y, p.door[1]);
  for (const [x, z, top] of p.steps) for (let y = 1; y <= top; y++) g.set(x, y, z, S.wall);
  for (let z = p.ledge.z0; z <= p.ledge.z1; z++) {
    g.set(p.ledge.x, p.ledge.y, z, S.edge);
    for (const y of p.ledge.open) g.del(p.ledge.x, y, z);
  }
  return g;
}

function wallStyle(x, y, z, tower, top) {
  const { x0, x1, z0, z1 } = GH;
  if (tower && y >= GH.towerTop - 1) return S.cap;
  if (y === GH.stringCourse) return S.course;
  // Quoins: the back corners and the towers' outer corners, alternating long and short.
  const corner = (x === x0 || x === x1) && (z === z0 || z === z1);
  const quoin = (x === x0 || x === x1) && z === z0 + 1 && y % 2 === 1 || (x === x0 + 1 || x === x1 - 1) && z === z0 && y % 2 === 0
    || (x === x0 || x === x1) && z === z1 - 1 && y % 2 === 1 || (x === x0 + 1 || x === x1 - 1) && z === z1 && y % 2 === 0;
  if (corner || quoin) return S.edge;
  // Buttresses: pale-edged piers on the outer faces, and a stepped edge at the wall top.
  const pierSide = (x === x0 || x === x1) && [6, 11, 16].includes(z);
  const pierBack = z === z0 && [-4, 4].includes(x);
  if (pierSide || pierBack) return S.edge;
  if (!tower && y === top) return S.edge;
  return S.wall;
}

function portcullis() {
  const g = new Grid();
  const { gate } = GH;
  for (let x = gate.x0; x <= gate.x1; x++) for (let y = gate.y0; y <= gate.y1; y++) g.set(x, y - gate.y0, gate.z, S.portcullis);
  return g;
}

export const GATEHOUSE_TURN = 2;

export default [
  {
    id: 'gatehouse-unwritten',
    name: 'The Gatehouse of the Unwritten',
    description: 'A walled court of pale stone where newcomers wake: six arrival stones, a gold line, corner towers and a blank page over the gate.',
    build: () => fromSiteFrame(gatehouse(), GATEHOUSE_TURN),
  },
  {
    id: 'gatehouse-portcullis',
    name: 'The Gatehouse portcullis',
    description: 'The 5 x 6 dark iron gate of the Gatehouse. RealmArrival places it and takes it down row by row, top first.',
    build: () => fromSiteFrame(portcullis(), GATEHOUSE_TURN),
  },
];
