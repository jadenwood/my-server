// A voxel grid of game blocks, a sampler that turns any solid (a density function) into blocks with shape
// fitting, and the sculpture JSON that plugins/RealmSculptor.cs places in game.
//
// Sculpture JSON: { id, name, description, size: [sx, sy, sz], front: "-z", materials: { "<id>": "<role>" },
//   blocks: [[x, y, z, materialId, prefabId, rotationIndex, "#rrggbb"], ...], license, source, generator }
// Coordinates are game block cells (1 block = 1.2 m, BlockManager.BLOCK_SIZE). y is up. The front faces -z.
// Blocks are written bottom-up (y, then z, then x) because the plugin places them in file order.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { quantise, PALETTE_HEXES } from './palette.mjs';
import { SHAPES, insideBlock } from './shapes.mjs';
import { MATRICES } from './rotations.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
export const MATERIALS = JSON.parse(fs.readFileSync(path.join(HERE, 'materials.json'), 'utf8')).materials;
export const roleId = (role) => {
  const m = MATERIALS.find((x) => x.role === role);
  if (!m) throw new Error(`unknown material role "${role}" (see art/tools/sculptor/materials.json)`);
  return m.id;
};
export const roleOfId = (id) => (MATERIALS.find((x) => x.id === id) || {}).role || null;

export const FORMAT = 'realm-sculpture/1';
export const MAX_BLOCKS = 4000;           // plugins/RealmSculptor.cs MaxBlocksPerSculpture default
export const MAX_SIDE = 64;
export const SINGLE_BLOCK_PREFABS = new Set([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 15]);   // CubeInfo.IsPrefabTypeSingleBlock
export const LICENSES = new Set(['realm-original', 'CC0-1.0', 'CC-BY-4.0', 'MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'Unlicense']);

const K = (x, y, z) => `${x},${y},${z}`;
const styleKey = (s) => `${s.mat}|${s.color}`;

/** A style is { mat: '<material role>', color: '#rrggbb' | null }. Colours are quantised to art/palette.json; null
 * keeps the material's own look (the plugin does not paint it). */
export const style = (mat, color) => { roleId(mat); return { mat, color: color == null ? null : quantise(color) }; };

export class Grid {
  constructor() { this.cells = new Map(); }

  set(x, y, z, s, prefab = 0, rot = 0) {
    if (!s) { this.cells.delete(K(x, y, z)); return this; }
    this.cells.set(K(x, y, z), { x, y, z, mat: s.mat, color: s.color, prefab, rot: prefab === 0 ? 0 : rot });
    return this;
  }
  get(x, y, z) { return this.cells.get(K(x, y, z)) || null; }
  has(x, y, z) { return this.cells.has(K(x, y, z)); }
  del(x, y, z) { this.cells.delete(K(x, y, z)); return this; }
  get size() { return this.cells.size; }
  *[Symbol.iterator]() { yield* this.cells.values(); }

  /** Fills an inclusive box. */
  box(x0, y0, z0, x1, y1, z1, s) {
    for (let x = Math.min(x0, x1); x <= Math.max(x0, x1); x++)
      for (let y = Math.min(y0, y1); y <= Math.max(y0, y1); y++)
        for (let z = Math.min(z0, z1); z <= Math.max(z0, z1); z++) this.set(x, y, z, s);
    return this;
  }

  /** Recolours (and optionally re-materials) cells for which pick(cell) returns a style. */
  paint(pick) {
    for (const c of this.cells.values()) { const s = pick(c); if (s) { c.mat = s.mat; c.color = s.color; } }
    return this;
  }

  /**
   * Samples a solid. fn(x, y, z) gets continuous coordinates in block units (cell i spans [i, i + 1)) and returns
   * a style or null. Each cell is tested at sub^3 points. A full cell becomes a block, an empty one stays air, and
   * a partly filled cell becomes whichever of air, block or a fitted shape (shapes.json "fit") matches its points
   * best; a shape must beat air and block by `margin` of the points so flat surfaces stay plain blocks.
   */
  sample(fn, min, max, { sub = 4, fit = true, margin = 0.12, solidAt = 0.5, onlyEmpty = false } = {}) {
    const pts = subPoints(sub);
    const cands = fit ? fitCandidates(sub) : [];
    const n = pts.length;
    for (let x = min[0]; x <= max[0]; x++)
      for (let y = min[1]; y <= max[1]; y++)
        for (let z = min[2]; z <= max[2]; z++) {
          if (onlyEmpty && this.has(x, y, z)) continue;
          const bits = new Uint8Array(n);
          const votes = new Map();
          let c = 0;
          for (let i = 0; i < n; i++) {
            const s = fn(x + 0.5 + pts[i][0], y + 0.5 + pts[i][1], z + 0.5 + pts[i][2]);
            if (s) { bits[i] = 1; c++; const k = styleKey(s); const v = votes.get(k); if (v) v.n++; else votes.set(k, { n: 1, s }); }
          }
          if (c === 0) continue;
          let best = { prefab: 0, rot: 0 }, solid = c >= solidAt * n;
          if (c < n) {
            const base = Math.max(c, n - c) + margin * n;
            let bestAgree = -1;
            for (const cand of cands) {
              let agree = 0;
              for (let i = 0; i < n; i++) if (bits[i] === cand.bits[i]) agree++;
              if (agree > base && agree > bestAgree) { bestAgree = agree; best = cand; solid = true; }
            }
          }
          if (!solid) continue;
          let top = null;
          for (const v of votes.values()) if (!top || v.n > top.n) top = v;
          this.set(x, y, z, top.s, best.prefab, best.rot);
        }
    return this;
  }

  bounds() {
    let mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
    for (const c of this.cells.values()) {
      const p = [c.x, c.y, c.z];
      for (let i = 0; i < 3; i++) { mn[i] = Math.min(mn[i], p[i]); mx[i] = Math.max(mx[i], p[i]); }
    }
    return { min: mn, max: mx };
  }

  /** Cells that are not face-connected to the bottom layer (the game drops such chunks: BlockCollapsing). */
  floating() {
    if (!this.size) return [];
    const { min } = this.bounds();
    const seen = new Set();
    const queue = [];
    for (const c of this.cells.values()) if (c.y === min[1]) { seen.add(K(c.x, c.y, c.z)); queue.push(c); }
    while (queue.length) {
      const c = queue.pop();
      for (const [dx, dy, dz] of DIRS) {
        const k = K(c.x + dx, c.y + dy, c.z + dz);
        if (!seen.has(k) && this.cells.has(k)) { seen.add(k); queue.push(this.cells.get(k)); }
      }
    }
    return [...this.cells.values()].filter((c) => !seen.has(K(c.x, c.y, c.z)));
  }

  /** Removes floating cells and returns how many were removed. */
  dropFloating() { const f = this.floating(); for (const c of f) this.del(c.x, c.y, c.z); return f.length; }

  /** Writes the sculpture JSON object. */
  toSculpture({ id, name, description = '', license = 'realm-original', source, credit = '' }) {
    if (!this.size) throw new Error(`${id}: no blocks`);
    const { min, max } = this.bounds();
    const blocks = [...this.cells.values()].map((c) => [c.x - min[0], c.y - min[1], c.z - min[2], roleId(c.mat), c.prefab, c.rot, c.color]);
    blocks.sort((a, b) => a[1] - b[1] || a[2] - b[2] || a[0] - b[0]);
    const materials = {};
    for (const b of blocks) materials[b[3]] = roleOfId(b[3]);
    const out = {
      format: FORMAT, id, name, description,
      size: [max[0] - min[0] + 1, max[1] - min[1] + 1, max[2] - min[2] + 1],
      front: '-z', materials, license, source,
    };
    if (credit) out.credit = credit;
    out.blocks = blocks;
    return out;
  }
}

export const DIRS = [[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]];

export function subPoints(sub) {
  const pts = [];
  for (let i = 0; i < sub; i++) for (let j = 0; j < sub; j++) for (let k = 0; k < sub; k++)
    pts.push([(i + 0.5) / sub - 0.5, (j + 0.5) / sub - 0.5, (k + 0.5) / sub - 0.5]);
  return pts;
}

const candCache = new Map();
/** Every distinct (prefab, rotation) occupancy pattern of the shapes the fitter may use, cube excluded. */
export function fitCandidates(sub) {
  if (candCache.has(sub)) return candCache.get(sub);
  const pts = subPoints(sub);
  const seen = new Set();
  const out = [];
  for (const s of SHAPES) {
    if (!s.fit || s.prefabId === 0 || !s.kind) continue;
    for (let r = 0; r < MATRICES.length; r++) {
      const bits = Uint8Array.from(pts, (p) => (insideBlock(s.prefabId, r, p) ? 1 : 0));
      const key = bits.join('');
      if (seen.has(key)) continue;
      seen.add(key);
      out.push({ prefab: s.prefabId, rot: r, bits });
    }
  }
  candCache.set(sub, out);
  return out;
}

/** Problems with a sculpture object; an empty list means the plugin will accept it. */
export function validate(s) {
  const e = [];
  const P = (m) => e.push(`${s && s.id ? s.id : '?'}: ${m}`);
  if (!s || typeof s !== 'object') return ['not an object'];
  if (s.format !== FORMAT) P(`format must be "${FORMAT}"`);
  if (typeof s.id !== 'string' || !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(s.id) || s.id.length > 40) P('id must be lower-case words joined by "-" (at most 40 characters)');
  if (typeof s.name !== 'string' || !s.name.trim() || s.name.length > 60) P('name is missing or longer than 60 characters');
  if (!LICENSES.has(s.license)) P(`license "${s.license}" is not one of ${[...LICENSES].join(', ')}`);
  if (typeof s.source !== 'string' || !s.source.trim()) P('source is missing (the generator file, or the model URL for third-party work)');
  if (s.license !== 'realm-original' && !/^https?:\/\//.test(s.source || '')) P('third-party work needs the source URL it was taken from');
  if (s.license && s.license.startsWith('CC-BY') && !s.credit) P('CC BY work needs a credit line');
  if (s.front !== '-z') P('front must be "-z"');
  if (!Array.isArray(s.size) || s.size.length !== 3 || s.size.some((v) => !Number.isInteger(v) || v < 1 || v > MAX_SIDE)) { P(`size must be three whole numbers from 1 to ${MAX_SIDE}`); return e; }
  if (!Array.isArray(s.blocks) || !s.blocks.length) { P('no blocks'); return e; }
  if (s.blocks.length > MAX_BLOCKS) P(`${s.blocks.length} blocks is over the ${MAX_BLOCKS} limit`);
  const ids = new Set(MATERIALS.map((m) => m.id));
  const seen = new Set();
  const used = new Set();
  const maxSeen = [0, 0, 0];
  let lastY = -1, ordered = true;
  s.blocks.forEach((b, i) => {
    const at = `block ${i}`;
    if (!Array.isArray(b) || b.length !== 7) { P(`${at} must be [x, y, z, material, prefab, rotation, "#rrggbb"]`); return; }
    const [x, y, z, mat, prefab, rot, col] = b;
    if (![x, y, z, mat, prefab, rot].every(Number.isInteger)) { P(`${at} has a non-integer field`); return; }
    if (x < 0 || y < 0 || z < 0 || x >= s.size[0] || y >= s.size[1] || z >= s.size[2]) P(`${at} (${x},${y},${z}) is outside size`);
    maxSeen[0] = Math.max(maxSeen[0], x); maxSeen[1] = Math.max(maxSeen[1], y); maxSeen[2] = Math.max(maxSeen[2], z);
    if (!ids.has(mat)) P(`${at} uses material ${mat}, which materials.json does not list`);
    used.add(mat);
    if (!SINGLE_BLOCK_PREFABS.has(prefab)) P(`${at} uses prefab ${prefab}; only single-block shapes are allowed`);
    if (rot < 0 || rot > 23) P(`${at} rotation ${rot} is not 0-23`);
    if (prefab === 0 && rot !== 0) P(`${at} is a plain block with rotation ${rot}; plain blocks have no rotation`);
    if (col === null) { /* unpainted: the material's own colour */ }
    else if (typeof col !== 'string' || !/^#[0-9a-f]{6}$/.test(col)) P(`${at} colour must be "#rrggbb" in lower case, or null`);
    else if (!PALETTE_HEXES.has(col)) P(`${at} colour ${col} is not in art/palette.json`);
    const k = K(x, y, z);
    if (seen.has(k)) P(`${at} repeats position ${k}`);
    seen.add(k);
    if (y < lastY) ordered = false;
    lastY = y;
  });
  if (!ordered) P('blocks must be written bottom-up (y never decreases)');
  if (maxSeen.some((v, i) => v !== s.size[i] - 1)) P(`size ${s.size} does not match the blocks (${maxSeen.map((v) => v + 1)})`);
  for (const m of used) if (!s.materials || s.materials[m] !== roleOfId(m)) P(`materials must name material ${m} as "${roleOfId(m)}"`);
  const g = new Grid();
  for (const b of s.blocks) if (Array.isArray(b)) g.cells.set(K(b[0], b[1], b[2]), { x: b[0], y: b[1], z: b[2] });
  const fl = g.floating();
  if (fl.length) P(`${fl.length} block(s) are not connected to the ground layer by faces (the game would let them collapse), e.g. ${K(fl[0].x, fl[0].y, fl[0].z)}`);
  return e;
}

/** Block count per material role and per shape, for reports. */
export function stats(s) {
  const mats = {}, shapes = {};
  for (const b of s.blocks) {
    const r = roleOfId(b[3]) || b[3];
    mats[r] = (mats[r] || 0) + 1;
    const sh = (SHAPES.find((x) => x.prefabId === b[4]) || {}).name || b[4];
    shapes[sh] = (shapes[sh] || 0) + 1;
  }
  return { blocks: s.blocks.length, size: s.size, materials: mats, shapes };
}
