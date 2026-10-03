// Block shape geometry: an inside test and a polygon mesh for each kind, in block-centred coordinates
// (-0.5..0.5 on every axis) at rotation index 0. A rotated block uses MATRICES[rotation] from rotations.mjs.
//
// UNVERIFIED: the game's ramp, stair and peak meshes are scene data. The kinds below are the tools' assumption
// for rotation 0 (the ramp rises towards +z with its flat base down). /sculpt shapes places every prefab in
// rotations 0-3 so the owner can compare it with this file; fix the kind (or its geometry) here if they differ.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { MATRICES, apply } from './rotations.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const SHAPES = JSON.parse(fs.readFileSync(path.join(HERE, 'shapes.json'), 'utf8')).shapes;
export const shapeById = (id) => SHAPES.find((s) => s.prefabId === id) || null;

const h = 0.5;
const quad = (a, b, c, d) => [a, b, c, d];

// Each kind: inside(x, y, z) and faces (counter-clockwise seen from outside), all at rotation 0.
export const KINDS = {
  cube: {
    inside: () => true,
    faces: [
      quad([-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h]),       // bottom (y-)
      quad([-h, h, -h], [-h, h, h], [h, h, h], [h, h, -h]),           // top (y+)
      quad([-h, -h, -h], [-h, h, -h], [h, h, -h], [h, -h, -h]),       // z-
      quad([-h, -h, h], [h, -h, h], [h, h, h], [-h, h, h]),           // z+
      quad([-h, -h, -h], [-h, -h, h], [-h, h, h], [-h, h, -h]),       // x-
      quad([h, -h, -h], [h, h, -h], [h, h, h], [h, -h, h]),           // x+
    ],
  },
  // Solid where y <= z: the base (y-) and the back (z+) are full faces; the slope faces up and towards z-.
  wedge: {
    inside: (x, y, z) => y <= z + 1e-9,
    faces: [
      quad([-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h]),
      quad([-h, -h, h], [h, -h, h], [h, h, h], [-h, h, h]),
      quad([-h, -h, -h], [-h, h, h], [h, h, h], [h, -h, -h]),
      [[-h, -h, -h], [-h, -h, h], [-h, h, h]],
      [[h, -h, -h], [h, h, h], [h, -h, h]],
    ],
  },
  // Two steps rising towards +z: solid where y <= 0 or z >= 0.
  stair: {
    inside: (x, y, z) => y <= 1e-9 || z >= -1e-9,
    faces: [
      quad([-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h]),
      quad([-h, -h, h], [h, -h, h], [h, h, h], [-h, h, h]),
      quad([-h, -h, -h], [-h, 0, -h], [h, 0, -h], [h, -h, -h]),
      quad([-h, 0, -h], [-h, 0, 0], [h, 0, 0], [h, 0, -h]),
      quad([-h, 0, 0], [-h, h, 0], [h, h, 0], [h, 0, 0]),
      quad([-h, h, 0], [-h, h, h], [h, h, h], [h, h, 0]),
      [[-h, -h, -h], [-h, -h, h], [-h, h, h], [-h, h, 0], [-h, 0, 0], [-h, 0, -h]],
      [[h, -h, -h], [h, 0, -h], [h, 0, 0], [h, h, 0], [h, h, h], [h, -h, h]],
    ],
  },
  // A roof ridge along z: solid where y + 0.5 <= 1 - 2|x|.
  peak: {
    inside: (x, y) => y + h <= 1 - 2 * Math.abs(x) + 1e-9,
    faces: [
      quad([-h, -h, -h], [h, -h, -h], [h, -h, h], [-h, -h, h]),
      quad([-h, -h, -h], [-h, -h, h], [0, h, h], [0, h, -h]),
      quad([h, -h, -h], [0, h, -h], [0, h, h], [h, -h, h]),
      [[-h, -h, -h], [0, h, -h], [h, -h, -h]],
      [[-h, -h, h], [h, -h, h], [0, h, h]],
    ],
  },
};

export const kindOf = (prefabId) => {
  const s = shapeById(prefabId);
  return s && s.kind && KINDS[s.kind] ? s.kind : null;
};

/** Is a block-centred point inside a block of this prefab and rotation? (An unknown kind counts as a cube.) */
export function insideBlock(prefabId, rotation, p) {
  const kind = KINDS[kindOf(prefabId) || 'cube'];
  const m = MATRICES[rotation] || MATRICES[0];
  // M is orthogonal, so its transpose undoes it.
  const t = [[m[0][0], m[1][0], m[2][0]], [m[0][1], m[1][1], m[2][1]], [m[0][2], m[1][2], m[2][2]]];
  const [x, y, z] = apply(t, p);
  return kind.inside(x, y, z);
}

/** The block's faces in block-centred coordinates after rotation. */
export function blockFaces(prefabId, rotation) {
  const kind = KINDS[kindOf(prefabId) || 'cube'];
  const m = MATRICES[rotation] || MATRICES[0];
  return kind.faces.map((f) => f.map((v) => apply(m, v)));
}

/** Rotation index that turns the canonical high side (+z) to `high` and the base (y-) to `base`. */
export function orient(high, base) {
  for (let i = 0; i < MATRICES.length; i++) {
    const a = apply(MATRICES[i], [0, 0, 1]), b = apply(MATRICES[i], [0, -1, 0]);
    if (a.every((v, k) => v === high[k]) && b.every((v, k) => v === base[k])) return i;
  }
  throw new Error(`no rotation puts the high side at ${high} and the base at ${base}`);
}
