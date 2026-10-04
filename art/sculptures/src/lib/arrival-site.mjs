// The arrival site's geometry (docs/arrival-design.md section 4), in one place so the pieces' generators and the site
// layout (art/sculptures/sites/arrival.json, made by src/sites/arrival.mjs) can never disagree.
//
// Site frame, in block cells (1 cell = 1.2 m): y is up; z runs along the axis from the Gatehouse's back wall (z = 0)
// through the gate, down the avenue to the Hearth fire and on toward the Old Throne; x is across, +x on the right of
// someone walking toward the fire. Cell (0, 0, 0) is the middle of the back wall's outer face, in the floor layer.
// Every piece's bottom layer is y = 0 (RealmSculptor builds a piece's bottom layer in the cell an admin's feet are in),
// so a player standing on a piece's floor has their feet in y = 1, and on bare ground in y = 0.
//
// The design's distances in metres (L, from the back wall) are z * 1.2: gate at L 27.6, pairs at L 34-58, 62-86 and
// 91-115, the hearth-ring edge at L 120, the fire at L 129, the pillar about L 140 and the wayboard at L 146.

export const CELL_M = 1.2;

// The Gatehouse of the Unwritten: 19 x 23 cells, walls 2 thick and 9 high, two corner towers 13 high at the front.
export const GH = {
  x0: -9, x1: 9, z0: 0, z1: 22,
  wallTop: 8,                 // walls are y 0..8 (9 high, the floor layer included)
  towerTop: 12,               // towers are y 0..12 (13 high)
  gateTop: 10,                // the raised gate front between the towers
  stringCourse: 6,            // the Ember band round every wall
  towers: [{ x0: -9, x1: -6 }, { x0: 6, x1: 9 }],
  towerZ0: 19,                // towers stand on z 19..22
  gate: { x0: -2, x1: 2, y0: 1, y1: 6, z: 21 },        // the 5 x 6 gap; the portcullis stands in its inner layer
  goldLine: { x0: -2, x1: 2, z: 19 },                  // 5 x 1 inlay, 2 cells inside the gate
  // Six arrival stones, all alike: [name, x, z]. A1-A3 are the row nearer the gate.
  stones: [['A1', -4, 9], ['A2', 0, 9], ['A3', 4, 9], ['A4', -4, 5], ['A5', 0, 5], ['A6', 4, 5]],
  court: { x0: -5, x1: 5, z0: 4, z1: 18 },             // the open court under the sky (11 x 15)
  signBoards: [-4, 0, 4],                              // the Chronicle Wall's three boards (G2, G1, G3), centre x
  eave: 2,                                             // eaves run 2 cells in from the walls at the wall top
  // The Pilgrim's Stair in the left tower: a door from the court, two 1-cell steps, and a 2-cell-wide ledge (a sill
  // in the outer wall) 2 cells above the ground outside.
  pilgrim: {
    shaft: [[-8, 20], [-7, 20], [-8, 21], [-7, 21]],   // the hollow inside the tower, y 1..10
    door: [-7, 19],                                      // y 1..2, opening onto the court
    steps: [[-7, 21, 1], [-8, 21, 2], [-8, 20, 2]],      // [x, z, top y] of each step column
    ledge: { x: -9, z0: 20, z1: 21, y: 2, open: [3, 4] },// sill blocks up to y 2; the opening above is y 3..4
  },
};

// The avenue: a 7-cell path (kerbs at x = -3 and 3), monuments with their fronts at x = -7 and 7.
export const AVENUE = {
  half: 3,
  a: { z0: 23, z1: 61 },      // processional-a, 7 x 39 (the first 5 cells are the forecourt)
  b: { z0: 62, z1: 99 },      // processional-b, 7 x 38
  monumentFront: 7,           // monuments stand on x 7..13 and -13..-7 (7 deep once turned)
  pairs: [28, 52, 76],        // each pair stands on z .. z + 19 (20 cells)
  pledgeX: 5,                 // pledge stones are 3 x 3, centred 2 cells out from the monument front
  pledgeDz: 9,                // ... and 9 cells along the monument from its gate-side end
};

// The Hearth: a 15 x 15 ring centred on the fire.
export const HEARTH = { x: 0, z: 107, half: 7, bandY: 2 };

// The ember band: 24 cells round the fire at radius 5 (an octagon: |dx| or |dz| = 5 with the other at most 1, and the
// diagonals |dx| + |dz| = 6). RealmArrival places and recolours them; the hearth ring leaves them empty and holds
// them up on its dais, so they stand one block proud of it at the height of the Hearth centre's feet cell (y 2).
export function bandOffsets() {
  const out = [];
  for (let dz = -5; dz <= 5; dz++) for (let dx = -5; dx <= 5; dx++) {
    const m = Math.max(Math.abs(dx), Math.abs(dz)), s = Math.abs(dx) + Math.abs(dz);
    const outer = m <= 5 && s <= 6, inner = m <= 4 && s <= 5;
    if (outer && !inner) out.push([dx, dz]);
  }
  return out;
}
// In order from the avenue side (-z) round through +x (anticlockwise seen from above), so a flare can run round it.
export const BAND = bandOffsets().sort((a, b) => ang(a) - ang(b));
function ang([dx, dz]) { const a = Math.atan2(dx, -dz); return a < 0 ? a + 2 * Math.PI : a; }

// The hearth ring's cells by distance from the fire's cell centre.
export const ringR = (dx, dz) => Math.hypot(dx, dz);
export const RING = { outer: 7.5, dais: 6.5 };

// Herald's Pillar (5 x 5) beside the ring on its left, on the throne side, and the wayboard (2 x 15 once turned) on its
// right, its boards facing the axis. Both stand clear of the axis: in the view from the gate they flank the Old Throne
// instead of covering it (a wayboard across the axis at L 146 hid the throne's steps in the previews).
export const PILLAR = { x0: -11, z0: 112 };
export const WAYBOARD = { x0: 9, z0: 108, turn: 1 };
export const THRONE = { x0: -7, z0: 178, lift: 10 };     // context only: the Old Throne on its hill, for previews

export const HOUSE_SLOTS = ['p1-left', 'p1-right', 'p2-left', 'p2-right', 'p3-left', 'p3-right'];
// The draw the previews show. The real order is drawn by lot in public at every build (docs/arrival-design.md 3.7).
export const PREVIEW_DRAW = { 'p1-left': 'varrow', 'p1-right': 'dunmere', 'p2-left': 'ashgrove', 'p2-right': 'corvane', 'p3-left': 'halloran', 'p3-right': 'merrin' };
