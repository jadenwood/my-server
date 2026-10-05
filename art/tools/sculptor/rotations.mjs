// The game's 24 block rotations as integer 3 x 3 matrices, and the maths to turn a whole sculpture.
//
// The Euler angles are the game's own table of block rotations (the type and member are cited in
// plugins/docs/RealmSculptor.md; only the numbers are used). A block's rotation travels as an index into that table.
// Unity's Quaternion.Euler(x, y, z) turns about z, then x, then y, so the matrix is Ry * Rx * Rz. The matrices use the
// usual formulas; Unity's left-handed axes do not change the numbers.
// plugins/RealmSculptor.cs carries the same table; both are checked against
// plugins/docs/RealmSculptor/logic-tests/rotations.json (written by: node art/tools/sculptor/cli.mjs fixtures).

export const EULER = [
  [0, 0, 0], [0, 90, 0], [0, 180, 0], [0, 270, 0],
  [180, 0, 0], [180, 90, 0], [180, 180, 0], [180, 270, 0],
  [0, 90, 90], [90, 90, 90], [180, 90, 90], [270, 90, 90],
  [0, -90, 90], [-90, -90, 90], [0, 90, -90], [90, 180, 0],
  [0, 180, 90], [-90, 270, 0], [0, 0, -90], [90, 0, -90],
  [0, 0, 90], [-90, 0, 90], [0, 180, -90], [90, 180, -90],
];

const cs = (deg) => {
  const q = ((Math.round(deg) % 360) + 360) % 360;
  return [[1, 0], [0, 1], [-1, 0], [0, -1]][q / 90];                // [cos, sin] for multiples of 90
};
const rx = (d) => { const [c, s] = cs(d); return [[1, 0, 0], [0, c, -s], [0, s, c]]; };
const ry = (d) => { const [c, s] = cs(d); return [[c, 0, s], [0, 1, 0], [-s, 0, c]]; };
const rz = (d) => { const [c, s] = cs(d); return [[c, -s, 0], [s, c, 0], [0, 0, 1]]; };

export function mul(a, b) {
  const out = [[0, 0, 0], [0, 0, 0], [0, 0, 0]];
  for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) for (let k = 0; k < 3; k++) out[i][j] += a[i][k] * b[k][j];
  return out;
}

export const eulerMatrix = ([x, y, z]) => mul(ry(y), mul(rx(x), rz(z)));
export const MATRICES = EULER.map(eulerMatrix);

const key = (m) => m.flat().join(',');
const INDEX = new Map(MATRICES.map((m, i) => [key(m), i]));

/** Index of a matrix in the game's table, or -1 when the table has no such rotation. */
export const indexOfMatrix = (m) => (INDEX.has(key(m)) ? INDEX.get(key(m)) : -1);

export const apply = (m, [x, y, z]) => [
  m[0][0] * x + m[0][1] * y + m[0][2] * z,
  m[1][0] * x + m[1][1] * y + m[1][2] * z,
  m[2][0] * x + m[2][1] * y + m[2][2] * z,
];

/** A quarter-turn about the vertical axis, k in 0..3 (k = 1 is Quaternion.Euler(0, 90, 0)). */
export const yaw = (k) => ry(90 * (((k % 4) + 4) % 4));

/** The rotation index a block gets when the whole sculpture turns k quarter-turns. */
export const turnIndex = (index, k) => indexOfMatrix(mul(yaw(k), MATRICES[index]));

/** Turns a block position k quarter-turns about the vertical axis through the origin. */
export const turnPos = (pos, k) => apply(yaw(k), pos);
