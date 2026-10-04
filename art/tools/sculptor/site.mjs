// Site layouts (realm-site/1): several sculptures placed relative to one anchor, with the cell groups, points, boxes,
// zones and sign spots a plugin needs to run the place. A site is written by a generator in art/sculptures/src/sites/
// and saved as art/sculptures/sites/<id>.json; the schema is in art/sculptures/README.md.
//
// The site frame is the same grid as a sculpture (block cells, 1.2 m, y up). A piece is a sculpture turned `turn`
// quarter-turns about the vertical (exactly as /sculpt place turns it: plugins/RealmSculptor.cs TurnXZ, TurnTable)
// with the corner of its turned footprint at `at`. On the ground the whole site is turned R quarter-turns and moved
// to an anchor cell: world = anchor + turnPos(site cell, R). resolveSite() does that for every piece, group and point,
// and works out where an admin stands for /sculpt place so each piece lands on its cells.
import { turnPos, turnIndex } from './rotations.mjs';
import { validate as validateSculpture } from './voxel.mjs';

export const SITE_FORMAT = 'realm-site/1';
export const CELL_M = 1.2;
export const DISTANCE_AHEAD = 2;            // RealmSculptor config DistanceAhead default: empty cells between admin and piece
export const BY = ['sculptor', 'plugin', 'context'];

const q4 = (k) => ((k % 4) + 4) % 4;
const K = (x, y, z) => `${x},${y},${z}`;
const fix0 = (v) => (Object.is(v, -0) ? 0 : v);
const turnXZ = (x, z, k) => { const [a, , b] = turnPos([x, 0, z], q4(k)); return [fix0(a), fix0(b)]; };

/** The sculpture id a piece uses, with {house} filled from the site's draw (lot) when the piece has a slot. */
export function pieceSculpture(site, piece, draw = (site.lot && site.lot.preview) || {}) {
  if (!piece.slot) return piece.sculpture;
  const h = draw[piece.slot];
  if (!h) throw new Error(`${site.id}: slot ${piece.slot} has no house in the draw`);
  return piece.sculpture.replace('{house}', h);
}

/** A sculpture's blocks turned `turn` quarter-turns with the corner of the turned footprint at `at`. */
export function placeBlocks(s, turn, at) {
  const t = s.blocks.map((b) => { const [x, z] = turnXZ(b[0], b[2], turn); return [x, b[1], z, b]; });
  const mx = Math.min(...t.map((c) => c[0])), mz = Math.min(...t.map((c) => c[2]));
  return t.map(([x, y, z, b]) => [x - mx + at[0], y + at[1], z - mz + at[2], b[3], b[4], b[4] === 0 ? 0 : turnIndex(b[5], q4(turn)), b[6]]);
}

/** The footprint [wx, height, wz] of a sculpture after `turn`. */
export const turnedSize = (s, turn) => (q4(turn) % 2 ? [s.size[2], s.size[1], s.size[0]] : [...s.size]);

/** Where an admin stands (feet cell, facing quadrant 0 = +z, 1 = +x, 2 = -z, 3 = -x) so /sculpt place with that
 * turn builds a piece of turned footprint [wx, , wz] with its corner at `at` (RealmSculptor MakePlan, auto turn). */
export function standFor(at, size, turn, ahead = DISTANCE_AHEAD) {
  const [X0, Y0, Z0] = at, [wx, , wz] = size, gap = ahead + 1, q = q4(turn);
  const hx = Math.floor(wx / 2), hz = Math.floor(wz / 2);
  const cell = q === 0 ? [X0 + hx, Y0, Z0 - gap] : q === 1 ? [X0 - gap, Y0, Z0 + hz] : q === 2 ? [X0 + hx, Y0, Z0 + wz - 1 + gap] : [X0 + wx - 1 + gap, Y0, Z0 + hz];
  return { cell, facing: q };
}

/** The corner RealmSculptor would build at for an admin at `cell` facing `q` (the inverse of standFor; for tests). */
export function planCorner(cell, q, size, ahead = DISTANCE_AHEAD) {
  const [fx, fy, fz] = cell, [wx, , wz] = size, gap = ahead + 1;
  switch (q4(q)) {
    case 1: return [fx + gap, fy, fz - Math.floor(wz / 2)];
    case 2: return [fx - Math.floor(wx / 2), fy, fz - gap - wz + 1];
    case 3: return [fx - gap - wx + 1, fy, fz - Math.floor(wz / 2)];
    default: return [fx - Math.floor(wx / 2), fy, fz + gap];
  }
}

export const FACING_WORD = ['+z', '+x', '-z', '-x'];

/** Every cell a site's built pieces occupy, by piece key: Map(key -> Map(cellKey -> block)). */
export function pieceCells(site, sculptures, { draw, include = ['sculptor', 'plugin'] } = {}) {
  const out = new Map();
  for (const p of site.pieces) {
    if (!include.includes(p.by)) continue;
    const s = sculptures.get(pieceSculpture(site, p, draw));
    if (!s) throw new Error(`${site.id}: piece ${p.key} uses ${pieceSculpture(site, p, draw)}, which is not a sculpture`);
    const m = new Map();
    for (const b of placeBlocks(s, p.turn, p.at)) m.set(K(b[0], b[1], b[2]), b);
    out.set(p.key, m);
  }
  return out;
}

/** Problems with a site; an empty list means it is consistent with its sculptures. */
export function validateSite(site, sculptures) {
  const e = [];
  const P = (m) => e.push(`${site && site.id ? site.id : '?'}: ${m}`);
  if (!site || site.format !== SITE_FORMAT) { P(`format must be "${SITE_FORMAT}"`); return e; }
  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(site.id || '')) P('id must be lower-case words joined by "-"');
  if (!Array.isArray(site.pieces) || !site.pieces.length) { P('no pieces'); return e; }
  const keys = new Set();
  const draws = [site.lot && site.lot.preview].filter(Boolean);
  // Every house draw must work: try the preview draw and every rotation of the houses through the slots.
  if (site.lot) {
    const hs = site.lot.houses;
    for (let r = 1; r < hs.length; r++) draws.push(Object.fromEntries(site.lot.slots.map((sl, i) => [sl, hs[(i + r) % hs.length]])));
  }
  if (!draws.length) draws.push({});
  for (const p of site.pieces) {
    if (keys.has(p.key)) P(`piece key ${p.key} repeats`);
    keys.add(p.key);
    if (!BY.includes(p.by)) P(`${p.key}: by must be one of ${BY.join(', ')}`);
    if (![0, 1, 2, 3].includes(p.turn)) P(`${p.key}: turn must be 0-3`);
    if (!Array.isArray(p.at) || p.at.length !== 3 || !p.at.every(Number.isInteger)) P(`${p.key}: at must be three whole numbers`);
    for (const d of draws) {
      const id = pieceSculpture(site, p, d);
      const s = sculptures.get(id);
      if (!s) { P(`${p.key}: no sculpture ${id}`); continue; }
      if (validateSculpture(s).length) P(`${p.key}: sculpture ${id} is not valid`);
      const sz = turnedSize(s, p.turn);
      if (!p.slot && JSON.stringify(sz) !== JSON.stringify(p.size)) P(`${p.key}: size ${JSON.stringify(p.size)} should be ${JSON.stringify(sz)} (the turned footprint)`);
      if (p.slot && (sz[0] !== p.size[0] || sz[2] !== p.size[2] || sz[1] > p.size[1])) P(`${p.key}: ${id} turned is ${sz}, outside the slot size ${p.size}`);
    }
  }
  for (const d of draws) {
    let cells;
    try { cells = pieceCells(site, sculptures, { draw: d }); } catch (x) { P(x.message); continue; }
    // No two built pieces share a cell.
    const owner = new Map();
    for (const [key, m] of cells) for (const k of m.keys()) {
      if (owner.has(k)) { P(`${key} and ${owner.get(k)} both use cell ${k}`); break; }
      owner.set(k, key);
    }
    // Stand spots: on bare ground when the piece is placed (no earlier piece in that column).
    const placed = new Set();
    for (const p of [...site.pieces].filter((x) => x.by === 'sculptor').sort((a, b) => a.order - b.order)) {
      const st = p.stand;
      if (!st) { P(`${p.key}: no stand spot`); continue; }
      const s = sculptures.get(pieceSculpture(site, p, d));
      const want = standFor(p.at, turnedSize(s, p.turn), p.turn);
      if (JSON.stringify(want.cell) !== JSON.stringify(st.cell) || FACING_WORD[want.facing] !== st.facing) P(`${p.key}: stand should be ${JSON.stringify(want.cell)} facing ${FACING_WORD[want.facing]}`);
      for (const k of placed) {
        const [x, , z] = k.split(',').map(Number);
        if (x === st.cell[0] && z === st.cell[2]) { P(`${p.key}: the stand spot ${st.cell} is on an earlier piece (${owner.get(k)}); change the order or the turn`); break; }
      }
      for (const k of cells.get(p.key).keys()) placed.add(k);
    }
    const solid = (c) => owner.has(K(...c));
    const groundOrSolid = (c) => c[1] < 0 || solid(c);
    // Plugin-owned cells: empty in every sculptor piece and resting on something.
    for (const [name, g] of Object.entries(site.cells || {})) {
      const list = g.rows ? g.rows.flat() : g.cells;
      if (!Array.isArray(list) || !list.length) { P(`cells.${name} is empty`); continue; }
      const own = g.piece ? cells.get(g.piece) : null;
      for (const c of list) {
        const k = K(...c);
        if (own) { if (!own.has(k)) P(`cells.${name}: ${k} is not a cell of ${g.piece}`); continue; }
        if (owner.has(k)) P(`cells.${name}: ${k} is taken by ${owner.get(k)}`);
        if (g.restsOn && !solid([c[0], c[1] - 1, c[2]]) && !list.some((o) => o[0] === c[0] && o[1] === c[1] - 1 && o[2] === c[2])) P(`cells.${name}: ${k} has nothing under it`);
      }
    }
    // Points: a floor under the feet (a block, or the ground at y = 0) and two clear cells.
    for (const [name, pt] of Object.entries(site.points || {})) {
      const c = pt.cell;
      if (!Array.isArray(c) || c.length !== 3 || !c.every(Number.isInteger)) { P(`points.${name}: cell must be three whole numbers`); continue; }
      if (pt.kind === 'fire') continue;                            // the Hearth centre: the staff fire pit goes there
      if (pt.kind === 'gate') {                                    // stood on before the plugin's gate cells exist
        const g = (site.cells || {}).gate;
        if (!g || !g.rows.flat().some((c) => K(...c) === K(...pt.cell))) P(`points.${name}: ${K(...pt.cell)} is not a gate cell`);
        continue;
      }
      if (solid(c) || solid([c[0], c[1] + 1, c[2]])) P(`points.${name}: ${K(...c)} or the cell above it is not clear`);
      if (!groundOrSolid([c[0], c[1] - 1, c[2]])) P(`points.${name}: nothing to stand on under ${K(...c)}`);
    }
  }
  for (const [name, b] of Object.entries(site.boxes || {})) if (!b.min || !b.max || b.min.some((v, i) => v > b.max[i])) P(`boxes.${name}: min must be <= max`);
  for (const z of site.zones || []) {
    if (z.point && !(site.points || {})[z.point] && !String(z.point).startsWith('banner.')) P(`zones ${z.key}: no point ${z.point}`);
    if (z.box && !(site.boxes || {})[z.box]) P(`zones ${z.key}: no box ${z.box}`);
  }
  return e;
}

/** The world cells of everything in a site placed at `anchor` (a world grid cell) turned R quarter-turns. */
export function resolveSite(site, sculptures, anchor = [0, 0, 0], R = 0, draw) {
  const w = (c) => { const [x, z] = turnXZ(c[0], c[2], R); return [anchor[0] + x, anchor[1] + c[1], anchor[2] + z]; };
  const pieces = site.pieces.map((p) => {
    const s = sculptures.get(pieceSculpture(site, p, draw));
    const cells = placeBlocks(s, p.turn, p.at).map((b) => w(b));
    const min = [0, 1, 2].map((i) => Math.min(...cells.map((c) => c[i])));
    const turn = q4(p.turn + R);
    const size = turnedSize(s, turn);
    const stand = standFor(min, size, turn);
    return { key: p.key, sculpture: s.id, by: p.by, order: p.order, turn, corner: min, stand: stand.cell, facing: FACING_WORD[stand.facing],
      command: p.by === 'sculptor' ? `/sculpt place ${s.id} ${turn}` : null };
  });
  const cells = {};
  for (const [name, g] of Object.entries(site.cells || {})) cells[name] = g.rows ? g.rows.map((r) => r.map(w)) : g.cells.map(w);
  const points = Object.fromEntries(Object.entries(site.points || {}).map(([k, p]) => [k, w(p.cell)]));
  return { anchor, turn: q4(R), pieces, cells, points };
}

/** Every block of a site in site cells, for previews: built pieces, plugin cells in a chosen state, context pieces. */
export function siteScene(site, sculptures, { gate = 'closed', band = 'rest', context = true, draw } = {}) {
  const blocks = [];
  const used = new Set();
  const add = (b) => { const k = K(b[0], b[1], b[2]); if (!used.has(k)) { used.add(k); blocks.push(b); } };
  for (const p of site.pieces) {
    if (p.by === 'context' && !context) continue;
    if (p.key === (site.cells.gate || {}).piece && gate === 'open') continue;
    const s = sculptures.get(pieceSculpture(site, p, draw));
    for (const b of placeBlocks(s, p.turn, p.at)) add(b);
  }
  if (site.cells.emberBand && band !== 'off') {
    const g = site.cells.emberBand;
    for (const c of g.cells) add([...c, 3, 0, 0, band === 'flare' ? g.flare : g.rest]);
  }
  if (context) for (const t of site.terrain || []) for (const b of terrainBlocks(t, used)) add(b);
  return blocks;
}

/** Preview-only ground shapes (never built): a mound of sod, stepped one cell at a time, under what stands on it. */
export function terrainBlocks(t, used = new Set()) {
  const out = [];
  if (t.kind === 'fire') {
    const [cx, cz] = t.centre;
    for (let dx = -1; dx <= 1; dx++) for (let dz = -1; dz <= 1; dz++) out.push([cx + dx, 0, cz + dz, 5, 0, 0, t.ember]);
    for (const [dx, dz] of [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1]]) out.push([cx + dx, 1, cz + dz, 5, 0, 0, t.colour]);
    out.push([cx, 2, cz, 5, 0, 0, t.colour]);
    return out.filter((b) => !used.has(K(b[0], b[1], b[2])));
  }
  if (t.kind !== 'mound') return out;
  const [cx, cz] = t.centre, r = t.radius, h = t.height, top = t.top || 0;
  for (let x = Math.floor(cx - r); x <= Math.ceil(cx + r); x++) for (let z = Math.floor(cz - r); z <= Math.ceil(cz + r); z++) {
    const d = Math.hypot(x - cx, z - cz);
    if (d > r) continue;
    const y1 = d <= top ? h - 1 : Math.round((h - 1) * (1 - (d - top) / (r - top)) ** 1.6);
    for (let y = 0; y <= y1; y++) if (!used.has(K(x, y, z))) out.push([x, y, z, 4, 0, 0, t.colour]);
  }
  return out;
}

// JSON text: two-space indents, but any array of numbers (a cell) and any array of cells stays on one line, so the
// file reads as a table and diffs stay small.
export function formatSite(site) {
  const flat = (v) => Array.isArray(v) && v.every((x) => typeof x === 'number' || (Array.isArray(x) && x.every((y) => typeof y === 'number')));
  const words = (v) => Array.isArray(v) && v.every((x) => typeof x === 'string') && JSON.stringify(v).length <= 110;
  const out = (v, ind) => {
    if (flat(v)) return JSON.stringify(v).replace(/,/g, ', ');
    if (words(v)) return JSON.stringify(v).replace(/","/g, '", "');
    if (Array.isArray(v)) return v.length ? `[\n${v.map((x) => `${ind}  ${out(x, `${ind}  `)}`).join(',\n')}\n${ind}]` : '[]';
    if (v && typeof v === 'object') {
      const ks = Object.keys(v);
      return ks.length ? `{\n${ks.map((k) => `${ind}  ${JSON.stringify(k)}: ${out(v[k], `${ind}  `)}`).join(',\n')}\n${ind}}` : '{}';
    }
    return JSON.stringify(v);
  };
  return `${out(site, '')}\n`;
}
