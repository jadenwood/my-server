// Model loaders (Wavefront OBJ with MTL colours, glTF 2.0 .gltf/.glb) and a scanline voxelizer that turns a closed
// triangle mesh into game blocks through Grid.sample (so slopes get fitted shapes too).
//
// Only closed (watertight) meshes voxelize correctly: inside is decided by counting crossings along +x.
// Third-party models must be CC0 or similarly permissive; voxelize records --license, --source and --credit in the
// sculpture and validate() refuses third-party work without a source URL.
import fs from 'node:fs';
import path from 'node:path';
import { Grid, style } from './voxel.mjs';
import { rgbToHex } from './palette.mjs';

const WHITE = [204, 204, 204];

// ---------------------------------------------------------------- OBJ

export function parseMTL(text) {
  const mats = {};
  let cur = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (line.startsWith('newmtl ')) { cur = line.slice(7).trim(); mats[cur] = WHITE; }
    else if (cur && /^Kd\s/.test(line)) {
      const [r, g, b] = line.split(/\s+/).slice(1, 4).map(Number);
      mats[cur] = [r, g, b].map((c) => Math.round(Math.max(0, Math.min(1, c)) * 255));
    }
  }
  return mats;
}

/** Triangles [{ a, b, c, color: [r,g,b] }] from OBJ text. mtl: a parsed MTL table (optional). */
export function parseOBJ(text, mtl = {}) {
  const v = [], vc = [], tris = [];
  let colour = WHITE;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line[0] === '#') continue;
    const parts = line.split(/\s+/);
    if (parts[0] === 'v') {
      v.push(parts.slice(1, 4).map(Number));
      vc.push(parts.length >= 7 ? parts.slice(4, 7).map((c) => Math.round(Number(c) * 255)) : null);
    } else if (parts[0] === 'usemtl') colour = mtl[parts[1]] || WHITE;
    else if (parts[0] === 'f') {
      const idx = parts.slice(1).map((p) => { const i = parseInt(p.split('/')[0], 10); return i < 0 ? v.length + i : i - 1; });
      for (let i = 1; i + 1 < idx.length; i++) {
        const ids = [idx[0], idx[i], idx[i + 1]];
        if (ids.some((k) => !v[k])) throw new Error(`OBJ face refers to a missing vertex: ${line}`);
        const cols = ids.map((k) => vc[k]).filter(Boolean);
        const color = cols.length === 3 ? [0, 1, 2].map((c) => Math.round((cols[0][c] + cols[1][c] + cols[2][c]) / 3)) : colour;
        tris.push({ a: v[ids[0]], b: v[ids[1]], c: v[ids[2]], color });
      }
    }
  }
  return tris;
}

// ---------------------------------------------------------------- glTF / GLB

const COMP = { 5120: [Int8Array, 1], 5121: [Uint8Array, 1], 5122: [Int16Array, 2], 5123: [Uint16Array, 2], 5125: [Uint32Array, 4], 5126: [Float32Array, 4] };
const NCOMP = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 };

function readAccessor(gltf, buffers, i) {
  const acc = gltf.accessors[i];
  const view = gltf.bufferViews[acc.bufferView];
  const [Arr, size] = COMP[acc.componentType];
  const n = NCOMP[acc.type];
  const buf = buffers[view.buffer];
  const stride = view.byteStride || size * n;
  const base = (view.byteOffset || 0) + (acc.byteOffset || 0);
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  const get = {
    5120: (o) => dv.getInt8(o), 5121: (o) => dv.getUint8(o), 5122: (o) => dv.getInt16(o, true),
    5123: (o) => dv.getUint16(o, true), 5125: (o) => dv.getUint32(o, true), 5126: (o) => dv.getFloat32(o, true),
  }[acc.componentType];
  const out = [];
  for (let e = 0; e < acc.count; e++) {
    const row = [];
    for (let c = 0; c < n; c++) {
      let x = get(base + e * stride + c * size);
      if (acc.normalized && Arr !== Float32Array) x /= { 1: 255, 2: 65535, 4: 4294967295 }[size];
      row.push(x);
    }
    out.push(n === 1 ? row[0] : row);
  }
  return out;
}

const lin2srgb = (c) => Math.round(255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055));
const m4mul = (a, b) => { const o = new Array(16).fill(0); for (let r = 0; r < 4; r++) for (let c = 0; c < 4; c++) for (let k = 0; k < 4; k++) o[c * 4 + r] += a[k * 4 + r] * b[c * 4 + k]; return o; };
const m4pt = (m, [x, y, z]) => [m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14]];
const IDENT = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

function nodeMatrix(node) {
  if (node.matrix) return node.matrix;
  const [tx, ty, tz] = node.translation || [0, 0, 0];
  const [qx, qy, qz, qw] = node.rotation || [0, 0, 0, 1];
  const [sx, sy, sz] = node.scale || [1, 1, 1];
  const r = [1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy + qz * qw), 2 * (qx * qz - qy * qw), 0,
    2 * (qx * qy - qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz + qx * qw), 0,
    2 * (qx * qz + qy * qw), 2 * (qy * qz - qx * qw), 1 - 2 * (qx * qx + qy * qy), 0, 0, 0, 0, 1];
  return [r[0] * sx, r[1] * sx, r[2] * sx, 0, r[4] * sy, r[5] * sy, r[6] * sy, 0, r[8] * sz, r[9] * sz, r[10] * sz, 0, tx, ty, tz, 1];
}

/** Triangles from a parsed glTF JSON and its binary buffers (Uint8Array per gltf.buffers entry). */
export function trianglesFromGLTF(gltf, buffers) {
  const tris = [];
  const visit = (ni, parent) => {
    const node = gltf.nodes[ni];
    const m = m4mul(parent, nodeMatrix(node));
    if (node.mesh !== undefined) {
      for (const prim of gltf.meshes[node.mesh].primitives) {
        if ((prim.mode ?? 4) !== 4) continue;
        const pos = readAccessor(gltf, buffers, prim.attributes.POSITION).map((p) => m4pt(m, p));
        const cols = prim.attributes.COLOR_0 !== undefined ? readAccessor(gltf, buffers, prim.attributes.COLOR_0) : null;
        const mat = prim.material !== undefined ? gltf.materials[prim.material] : null;
        const f = mat && mat.pbrMetallicRoughness && mat.pbrMetallicRoughness.baseColorFactor;
        const matCol = f ? f.slice(0, 3).map(lin2srgb) : WHITE;
        const idx = prim.indices !== undefined ? readAccessor(gltf, buffers, prim.indices) : pos.map((_, i) => i);
        for (let i = 0; i + 2 < idx.length; i += 3) {
          const ids = [idx[i], idx[i + 1], idx[i + 2]];
          const color = cols ? [0, 1, 2].map((c) => lin2srgb(ids.reduce((s, k) => s + cols[k][c], 0) / 3) * (matCol[c] / 255)) : matCol;
          tris.push({ a: pos[ids[0]], b: pos[ids[1]], c: pos[ids[2]], color: color.map(Math.round) });
        }
      }
    }
    for (const c of node.children || []) visit(c, m);
  };
  const scene = gltf.scenes ? gltf.scenes[gltf.scene || 0] : { nodes: gltf.nodes.map((_, i) => i) };
  for (const n of scene.nodes) visit(n, IDENT);
  return tris;
}

export function parseGLB(buf) {
  const dv = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
  if (dv.getUint32(0, true) !== 0x46546c67) throw new Error('not a GLB file (bad magic)');
  let off = 12, json = null, bin = null;
  while (off < buf.byteLength) {
    const len = dv.getUint32(off, true), type = dv.getUint32(off + 4, true);
    const chunk = buf.subarray(off + 8, off + 8 + len);
    if (type === 0x4e4f534a) json = JSON.parse(Buffer.from(chunk).toString('utf8'));
    else if (type === 0x004e4942) bin = chunk;
    off += 8 + len;
  }
  if (!json) throw new Error('GLB has no JSON chunk');
  return trianglesFromGLTF(json, (json.buffers || []).map(() => bin));
}

/** Triangles from a .obj, .gltf or .glb file. */
export function loadModel(file) {
  const ext = path.extname(file).toLowerCase();
  if (ext === '.obj') {
    const text = fs.readFileSync(file, 'utf8');
    const lib = (text.match(/^mtllib\s+(.+)$/m) || [])[1];
    const mtlFile = lib ? path.join(path.dirname(file), lib.trim()) : null;
    return parseOBJ(text, mtlFile && fs.existsSync(mtlFile) ? parseMTL(fs.readFileSync(mtlFile, 'utf8')) : {});
  }
  if (ext === '.glb') return parseGLB(fs.readFileSync(file));
  if (ext === '.gltf') {
    const gltf = JSON.parse(fs.readFileSync(file, 'utf8'));
    const buffers = (gltf.buffers || []).map((b) => {
      if (b.uri.startsWith('data:')) return Buffer.from(b.uri.split(',')[1], 'base64');
      return fs.readFileSync(path.join(path.dirname(file), decodeURIComponent(b.uri)));
    });
    return trianglesFromGLTF(gltf, buffers);
  }
  throw new Error(`unsupported model type ${ext} (use .obj, .gltf or .glb)`);
}

// ---------------------------------------------------------------- voxelize

/**
 * Voxelizes triangles into a Grid. The model is scaled so its height is `height` blocks (y up), stood on y = 0 and
 * centred in x and z. Each block is tested at sub^3 points, so shapes are fitted where the surface slopes.
 * `material` is the role every block gets; colours come from the model and are quantised to the palette.
 */
export function voxelize(tris, { height = 12, material = 'stone', sub = 4, fit = true, upAxis = 'y' } = {}) {
  if (!tris.length) throw new Error('model has no triangles');
  const swap = upAxis === 'z' ? (p) => [p[0], p[2], -p[1]] : (p) => p;
  const T = tris.map((t) => ({ a: swap(t.a), b: swap(t.b), c: swap(t.c), color: t.color }));
  const mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
  for (const t of T) for (const p of [t.a, t.b, t.c]) for (let i = 0; i < 3; i++) { mn[i] = Math.min(mn[i], p[i]); mx[i] = Math.max(mx[i], p[i]); }
  const k = height / (mx[1] - mn[1] || 1);
  const w = Math.ceil((mx[0] - mn[0]) * k), d = Math.ceil((mx[2] - mn[2]) * k);
  const map = (p) => [(p[0] - (mn[0] + mx[0]) / 2) * k + w / 2, (p[1] - mn[1]) * k, (p[2] - (mn[2] + mx[2]) / 2) * k + d / 2];
  const S = T.map((t) => {
    const a = map(t.a), b = map(t.b), c = map(t.c);
    return { a, b, c, color: t.color, y0: Math.min(a[1], b[1], c[1]), y1: Math.max(a[1], b[1], c[1]), z0: Math.min(a[2], b[2], c[2]), z1: Math.max(a[2], b[2], c[2]) };
  });
  // Bucket triangles by whole-block y so each scanline tests few triangles.
  const buckets = new Map();
  for (const t of S) for (let y = Math.floor(t.y0); y <= Math.floor(t.y1); y++) { if (!buckets.has(y)) buckets.set(y, []); buckets.get(y).push(t); }
  const styles = new Map();
  const styleFor = (rgb) => { const h = rgbToHex(rgb); if (!styles.has(h)) styles.set(h, style(material, h)); return styles.get(h); };
  const rows = new Map();
  const row = (y, z) => {
    const key = `${y.toFixed(5)},${z.toFixed(5)}`;
    if (rows.has(key)) return rows.get(key);
    const hits = [];
    y += 1.37e-7; z += 2.11e-7;                     // keeps sample rows off shared edges and vertices
    for (const t of buckets.get(Math.floor(y)) || []) {
      if (y < t.y0 || y > t.y1 || z < t.z0 || z > t.z1) continue;
      const x = rayX(t, y, z);
      if (x !== null) hits.push({ x, t });
    }
    hits.sort((p, q) => p.x - q.x);
    rows.set(key, hits);
    return hits;
  };
  const fn = (x, y, z) => {
    const hits = row(y, z);
    let n = 0;
    while (n < hits.length && hits[n].x < x) n++;
    if (n % 2 === 0) return null;
    const before = hits[n - 1], after = hits[n];
    const near = !after || x - before.x <= after.x - x ? before : after;
    return styleFor(near.t.color);
  };
  return new Grid().sample(fn, [0, 0, 0], [w, Math.ceil(height), d], { sub, fit });
}

// Where the line { y, z } (along +x) crosses triangle t, or null. Barycentric test in the y-z plane.
function rayX(t, y, z) {
  const { a, b, c } = t;
  const d = (b[1] - c[1]) * (a[2] - c[2]) + (c[2] - b[2]) * (a[1] - c[1]);
  if (Math.abs(d) < 1e-12) return null;
  const l1 = ((b[1] - c[1]) * (z - c[2]) + (c[2] - b[2]) * (y - c[1])) / d;
  const l2 = ((c[1] - a[1]) * (z - c[2]) + (a[2] - c[2]) * (y - c[1])) / d;
  const l3 = 1 - l1 - l2;
  if (l1 < 0 || l2 < 0 || l3 < 0) return null;
  return l1 * a[0] + l2 * b[0] + l3 * c[0];
}
