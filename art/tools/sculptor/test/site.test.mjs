// Tests for site layouts (art/tools/sculptor/site.mjs) and the arrival site (docs/arrival-design.md section 4).
// Run: node --test art/tools/sculptor/test/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { placeBlocks, turnedSize, standFor, planCorner, validateSite, resolveSite, siteScene, formatSite, pieceCells, pieceSculpture, SITE_FORMAT } from '../site.mjs';
import { sceneSvg } from '../render.mjs';
import { projectView, planSvg, sitePreviewNames } from '../site-render.mjs';
import { pal, house } from '../palette.mjs';
import { Grid, style, roleId, validate } from '../voxel.mjs';
import { turnIndex } from '../rotations.mjs';
import { orient } from '../shapes.mjs';
import { fromSiteFrame } from '../../../sculptures/src/lib/kit.mjs';
import { GH, BAND, HEARTH } from '../../../sculptures/src/lib/arrival-site.mjs';
import { sculptureMap, loadSiteDefinitions, readSite, buildSite, SITES, PREVIEW } from '../cli.mjs';

const K = (x, y, z) => `${x},${y},${z}`;
const S = sculptureMap();
const site = readSite('arrival');
const roleOf = (s, b) => s.materials[b[3]];

// ------------------------------------------------------------------ the maths, against RealmSculptor's MakePlan

test('site: placing a piece with a turn matches /sculpt place (corner of the turned footprint, block rotations turned)', () => {
  const s = S.get('tournament-arch');
  for (let t = 0; t < 4; t++) {
    const cells = placeBlocks(s, t, [5, 2, -7]);
    const [wx, h, wz] = turnedSize(s, t);
    assert.equal(Math.min(...cells.map((c) => c[0])), 5);
    assert.equal(Math.min(...cells.map((c) => c[2])), -7);
    assert.equal(Math.max(...cells.map((c) => c[0])) - 5 + 1, wx);
    assert.equal(Math.max(...cells.map((c) => c[2])) + 7 + 1, wz);
    assert.equal(h, s.size[1]);
    const ramp = s.blocks.findIndex((b) => b[4] === 2);
    assert.equal(cells[ramp][5], turnIndex(s.blocks[ramp][5], t), 'a ramp turns with the piece');
  }
});

test('site: the stand spot is the inverse of RealmSculptor\'s plan for every facing', () => {
  for (const size of [[7, 3, 20], [20, 3, 7], [5, 1, 5], [4, 1, 9]]) for (let t = 0; t < 4; t++) {
    const at = [3, 0, -11];
    const st = standFor(at, size, t);
    assert.equal(st.facing, t);
    assert.deepEqual(planCorner(st.cell, st.facing, size), at, `size ${size} turn ${t}`);
  }
});

test('site: fromSiteFrame draws in the site frame and comes back there when placed with that turn', () => {
  const s0 = style('stone', pal('Iron 200'));
  const g = new Grid().box(0, 0, 0, 3, 0, 1, s0).set(1, 1, 0, s0, 2, orient([1, 0, 0], [0, -1, 0]));
  for (let t = 0; t < 4; t++) {
    const local = fromSiteFrame(g, t).toSculpture({ id: 'f', name: 'F', license: 'realm-original', source: 't' });
    const back = placeBlocks(local, t, [0, 0, 0]);
    const keys = new Set(back.map((b) => K(b[0], b[1], b[2])));
    for (const c of g) assert.ok(keys.has(K(c.x, c.y, c.z)), `turn ${t}: cell ${K(c.x, c.y, c.z)} comes back`);
    const ramp = back.find((b) => b[4] === 2);
    assert.equal(ramp[5], g.get(1, 1, 0).rot, `turn ${t}: the ramp keeps its site rotation`);
  }
});

test('site: resolving on the ground turns every piece, point and cell group with the site', () => {
  for (let R = 0; R < 4; R++) {
    const r = resolveSite(site, S, [1000, 40, -500], R);
    assert.equal(r.turn, R);
    const gh = r.pieces.find((p) => p.key === 'gatehouse');
    assert.equal(gh.turn, (2 + R) % 4);
    for (const p of r.pieces.filter((x) => x.by === 'sculptor')) {
      const s = S.get(p.sculpture);
      assert.deepEqual(planCorner(p.stand, ['+z', '+x', '-z', '-x'].indexOf(p.facing), turnedSize(s, p.turn)), p.corner, `${p.key} at R=${R}`);
    }
    assert.equal(r.cells.gate.length, 6);
    assert.equal(r.cells.emberBand.length, 24);
    const d = (a, b) => Math.hypot(a[0] - b[0], a[2] - b[2]);
    assert.ok(Math.abs(d(r.points.A2, r.points.hearth) - d(site.points.A2.cell, site.points.hearth.cell)) < 1e-9, 'distances survive the turn');
  }
});

// ------------------------------------------------------------------ the arrival site file

test('arrival site: valid, up to date with its generator, previewed', async () => {
  assert.equal(site.format, SITE_FORMAT);
  assert.deepEqual(validateSite(site, S), []);
  const def = (await loadSiteDefinitions()).find((d) => d.id === 'arrival');
  assert.equal(formatSite(buildSite(def, S)), fs.readFileSync(path.join(SITES, 'arrival.json'), 'utf8'), 'run: node art/tools/sculptor/cli.mjs build');
  assert.deepEqual(JSON.parse(formatSite(site)), site, 'the formatter round-trips');
  for (const v of sitePreviewNames(def)) assert.ok(fs.existsSync(path.join(PREVIEW, `site-arrival-${v}.png`)), v);
  for (const v of ['reveal-closed', 'reveal-open', 'plan', 'aerial']) assert.ok(sitePreviewNames(def).includes(v), v);
});

test('arrival site: every piece the design names, in route order, on bare ground when placed', () => {
  const ids = site.pieces.map((p) => p.sculpture);
  for (const id of ['gatehouse-unwritten', 'gatehouse-portcullis', 'processional-a', 'processional-b', 'house-{house}', 'pledge-stone-{house}', 'hearth-ring', 'heralds-pillar', 'wayboard'])
    assert.ok(ids.includes(id), id);
  assert.equal(site.pieces.filter((p) => p.sculpture === 'house-{house}').length, 6);
  assert.equal(site.pieces.filter((p) => p.sculpture === 'pledge-stone-{house}').length, 6);
  assert.equal(site.pieces.find((p) => p.key === 'portcullis').by, 'plugin', 'the plugin, not /sculpt, owns the portcullis');
  const orders = site.pieces.filter((p) => p.by === 'sculptor').map((p) => p.order);
  assert.deepEqual(orders, [...orders].sort((a, b) => a - b));
  assert.equal(new Set(orders).size, orders.length);
  for (const p of site.pieces.filter((x) => x.by === 'sculptor')) assert.match(p.stand.command, new RegExp(`^/sculpt place ${p.sculpture.replace(/[{}]/g, '\\$&')} ${p.turn}$`));
});

test('arrival site: every draw of the houses fits the slots, and the pairs face each other across the road', () => {
  const draw = Object.fromEntries(site.lot.slots.map((s, i) => [s, site.lot.houses[(i + 3) % 6]]));
  const cells = pieceCells(site, S, { draw });
  for (const n of [1, 2, 3]) {
    const l = site.pieces.find((p) => p.key === `p${n}-left-monument`), r = site.pieces.find((p) => p.key === `p${n}-right-monument`);
    assert.equal(l.at[2], r.at[2], `pair ${n} stands level`);
    assert.equal(l.turn, 3); assert.equal(r.turn, 1);
    assert.equal(l.at[0] + l.size[0] - 1, -GH.x1 + 2, 'left fronts at x -7');
    assert.equal(r.at[0], 7, 'right fronts at x 7');
    assert.ok(cells.get(`p${n}-left-monument`).size > 1000);
  }
  assert.equal(pieceSculpture(site, site.pieces.find((p) => p.key === 'p1-left-stone'), draw), `pledge-stone-${draw['p1-left']}`);
});

test('arrival site: the gate rows are the portcullis, 5 x 6, top row first, in the gatehouse\'s empty gap', () => {
  const g = site.cells.gate;
  assert.equal(g.rows.length, 6);
  assert.ok(g.rows.every((r) => r.length === 5));
  assert.deepEqual(g.rows.map((r) => r[0][1]), [6, 5, 4, 3, 2, 1], 'top first');
  assert.ok(g.rows.every((r) => r.every((c, i) => i === 0 || c[0] === r[i - 1][0] + 1)), 'each row left to right');
  const cells = pieceCells(site, S);
  const port = cells.get('portcullis'), gh = cells.get('gatehouse');
  assert.deepEqual(new Set(g.rows.flat().map((c) => K(...c))), new Set(port.keys()));
  for (const c of g.rows.flat()) assert.ok(!gh.has(K(...c)), 'the gatehouse leaves the gap empty');
  for (const b of port.values()) { assert.equal(b[6], pal('Iron 900')); assert.equal(b[3], roleId('reinforced')); }
  assert.equal(g.colour, pal('Iron 900'));
  assert.deepEqual(site.points.gateSet.cell, g.rows[5][0], 'gate set stands on the bottom-left cell');
});

test('arrival site: the ember band is 24 empty cells round the fire, resting on the dais, at the Hearth centre\'s height', () => {
  const b = site.cells.emberBand;
  assert.equal(b.cells.length, 24);
  assert.equal(new Set(b.cells.map((c) => K(...c))).size, 24);
  assert.equal(b.rest, pal('Ember deep'));
  assert.equal(b.flare, pal('Ember hot'));
  assert.equal(b.material, 'clay');
  assert.deepEqual(b.centre, site.points.hearth.cell);
  const cells = pieceCells(site, S);
  const ring = cells.get('hearth-ring');
  for (const c of b.cells) {
    assert.equal(c[1], b.centre[1]);
    assert.ok(!ring.has(K(...c)), 'left empty by the ring');
    assert.ok(ring.has(K(c[0], c[1] - 1, c[2])), 'rests on the dais');
    const r = Math.hypot(c[0] - b.centre[0], c[2] - b.centre[2]);
    assert.ok(r >= 4.2 && r <= 5.1, `about radius 5 (${r.toFixed(2)})`);
  }
  assert.equal(BAND.length, 24);
  for (const m of ['M1', 'M2', 'M3']) {
    const p = site.points[m].cell;
    assert.ok(Math.hypot(p[0] - HEARTH.x, p[2] - HEARTH.z) > 5.5, `${m} is outside the band`);
  }
});

test('arrival site: stones, gold line and points match the design distances', () => {
  const m = (a, b) => Math.hypot(a[0] - b[0], a[2] - b[2]) * 1.2;
  const stones = ['A1', 'A2', 'A3', 'A4', 'A5', 'A6'].map((k) => site.points[k].cell);
  const gh = pieceCells(site, S).get('gatehouse');
  for (const c of stones) {
    const floor = gh.get(K(c[0], 0, c[2]));
    assert.equal(floor[6], pal('Parchment'), 'a parchment inlay');
    for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [-1, -1]]) assert.equal(gh.get(K(c[0] + dx, 0, c[2] + dz))[6], pal('Iron 900'), 'in an Iron 900 border');
    assert.ok(m(c, site.points.threshold.cell) > 11 && m(c, site.points.threshold.cell) < 18, 'about 15 m to the gold line');
    assert.ok(m(c, site.points.hearth.cell) > 112 && m(c, site.points.hearth.cell) < 125, 'about 115-120 m to the fire');
  }
  for (let i = 0; i < 6; i++) for (let j = i + 1; j < 6; j++) assert.ok(m(stones[i], stones[j]) > 3, 'stones stand apart (1.5 m occupancy + 1.2 m jitter)');
  for (const c of site.cells.goldLine.cells) assert.equal(gh.get(K(...c))[6], pal('Ember hot'));
  assert.equal(site.cells.goldLine.cells.length, 5);
  assert.ok(Math.abs(m(site.points.E.cell, [0, 0, GH.z1 + 1]) - 4) < 0.8, 'E is about 4 m outside the gate');
  const hall = site.boxes.Z0;
  assert.ok((site.points.hearth.cell[2] - hall.max[2]) * 1.2 > 40, 'the hall box is outside the Hearth town zone (40 m)');
  const z = Object.fromEntries(site.zones.map((x) => [x.key, x]));
  assert.equal(z.Z1.radiusM, 2.5); assert.equal(z.Z2.radiusM, 9); assert.equal(z.Z2p.radiusM, 1.5); assert.equal(z.Z3q.radiusM, 40);
  assert.equal(z.Z3.radiusM, 12); assert.equal(z.Z4.radiusM, 8); assert.equal(z.Z5.radiusM, 70);
  assert.equal(site.signs.length, 16, 'G1-G4, P1-P6, W1-W5, H1');
  for (const s of site.signs.filter((x) => x.text)) assert.ok(s.text.length <= 180, `${s.key} notice is under 180 characters`);
  assert.equal(site.signs.find((s) => s.key === 'G1').binding, 'art the-crossing');
});

test('arrival site: the Pilgrim\'s Stair climbs from the court to a ledge two cells above the ground outside', () => {
  const gh = pieceCells(site, S).get('gatehouse');
  const solid = (x, y, z) => gh.has(K(x, y, z));
  const floorTop = (x, z) => { let y = -1; while (solid(x, y + 1, z)) y++; return y; };   // highest solid from the ground up
  const path = [[-7, 18], [-7, 19], [-7, 20], [-7, 21], [-8, 21], [-9, 21]];
  let prev = 0;
  for (const [x, z] of path) {
    const top = floorTop(x, z);
    assert.ok(top - prev <= 1, `${x},${z}: at most one step up`);
    assert.ok(!solid(x, top + 1, z) && !solid(x, top + 2, z), `${x},${z}: two cells of headroom`);
    prev = top;
  }
  // A piece's bottom layer stands on the ground, so the ground outside is at height 0 and a block at y n has its top at
  // height n + 1. The design: the ledge is 2 cells above the drop pad.
  assert.equal(floorTop(-9, 21) + 1, 2, 'the ledge (the sill block at y 1) has its top two cells above the ground outside');
  assert.equal(floorTop(-10, 21), -1, 'nothing is built outside the wall under the ledge');
  const pad = site.boxes.Z0b;
  assert.ok(pad.max[0] < GH.x0 && pad.min[2] <= 21 && pad.max[2] >= 21, 'the drop pad is outside the left wall under the ledge');
});

// ------------------------------------------------------------------ the pieces, against the design's tables

test('arrival pieces: sizes, materials and palette colours from the design', () => {
  const g = S.get('gatehouse-unwritten');
  assert.deepEqual(g.size, [19, 13, 23]);
  assert.ok(g.blocks.length > 1500 && g.blocks.length <= 4000);
  const has = (s, role, hex) => s.blocks.some((b) => roleOf(s, b) === role && b[6] === hex);
  for (const [role, name] of [['cobblestone', 'Iron 700'], ['stone', 'Parchment 2'], ['stone', 'Parchment edge'], ['clay', 'Ember'], ['reinforced', 'Iron 600'], ['stone', 'Iron 800'], ['wood', 'Ink soft'], ['clay', 'Parchment'], ['clay', 'Iron 900'], ['clay', 'Ember hot']])
    assert.ok(has(g, role, pal(name)), `gatehouse: ${role} ${name}`);
  const wallTop = Math.max(...placeBlocks(g, 2, [GH.x0, 0, GH.z0]).filter((b) => b[0] === GH.x1 && b[2] === 11).map((b) => b[1]));
  assert.equal(wallTop, 8, 'side walls 9 high');
  assert.deepEqual(S.get('gatehouse-portcullis').size, [5, 6, 1]);
  assert.equal(S.get('gatehouse-portcullis').blocks.length, 30);
  assert.deepEqual(S.get('processional-a').size, [7, 1, 39]);
  assert.deepEqual(S.get('processional-b').size, [7, 1, 38]);
  for (const id of ['processional-a', 'processional-b']) {
    const s = S.get(id);
    assert.ok(s.blocks.every((b) => (roleOf(s, b) === 'cobblestone' && b[6] === null) || (roleOf(s, b) === 'stone' && b[6] === pal('Iron 600'))), `${id}: unpainted cobbles, Iron 600 kerbs`);
  }
  for (const key of ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin']) {
    const s = S.get(`pledge-stone-${key}`);
    assert.deepEqual(s.size, [3, 2, 3]);
    assert.equal(s.blocks.length, 10);
    assert.equal(s.blocks.filter((b) => b[6] === house(key, 'fieldDark') && roleOf(s, b) === 'stone').length, 9);
    assert.ok(s.blocks.some((b) => b[1] === 1 && b[6] === house(key, 'metal') && roleOf(s, b) === 'clay'));
  }
  const ring = S.get('hearth-ring');
  assert.deepEqual(ring.size, [15, 2, 15]);
  assert.ok(ring.blocks.every((b) => roleOf(ring, b) === 'stone'));
  assert.ok(has(ring, 'stone', pal('Iron 600')) && has(ring, 'stone', pal('Iron 700')));
  const w = S.get('wayboard');
  assert.deepEqual(w.size, [15, 5, 2]);
  assert.ok(has(w, 'stone', pal('Iron 600')) && has(w, 'spruce', pal('Ink')) && has(w, 'clay', pal('Ember')));
  for (const s of [g, ring, w]) assert.deepEqual(validate(s), []);
});

// ------------------------------------------------------------------ validation and rendering

test('site: validation catches overlaps, a band cell inside a piece, a blocked stand spot and an unclear point', () => {
  const clone = () => JSON.parse(JSON.stringify(site));
  let s = clone(); s.pieces.find((p) => p.key === 'wayboard').at = [-7, 0, 100];
  assert.match(validateSite(s, S).join('\n'), /both use cell|stand should be/);
  s = clone(); s.cells.emberBand.cells[0] = [0, 1, 102];
  assert.match(validateSite(s, S).join('\n'), /emberBand: 0,1,102 is taken by hearth-ring/);
  s = clone(); const gh = s.pieces.find((p) => p.key === 'gatehouse'); gh.order = 99;
  assert.match(validateSite(s, S).join('\n'), /on an earlier piece/);
  s = clone(); s.points.A1.cell = [-4, 0, 9];
  assert.match(validateSite(s, S).join('\n'), /points.A1/);
  s = clone(); s.signs[0].cell = [0, 1, 0];
  assert.match(validateSite(s, S).join('\n'), /signs G1: .* inside a piece/);
  s = clone(); s.format = 'nope';
  assert.match(validateSite(s, S).join('\n'), /format/);
});

test('site render: the reveal sees the throne through the open gate and not through the closed one', () => {
  const eye = [0.5, 1 + 1.6 / 1.2, 9.5];
  const cam = { kind: 'persp', eye, yaw: 0, pitch: -2, fov: 30 };
  const nearestInMiddle = (state) => {
    const blocks = siteScene(site, S, state);
    const pr = projectView(blocks, cam, { width: 400, height: 300 });
    // How far away is the nearest thing drawn over the middle of the view?
    const mid = pr.polys.filter((p) => p.pts.some((q) => Math.abs(q[0] - 200) < 40 && q[1] > 60 && q[1] < 160));
    return mid.length ? mid[mid.length - 1].depth : 0;
  };
  assert.ok(nearestInMiddle({ gate: 'open', band: 'flare' }) > 80, 'open: nothing on the 100 m axis hides the middle of the view before the Hearth');
  assert.ok(nearestInMiddle({ gate: 'closed', band: 'rest' }) < 15, 'closed: the portcullis fills the middle');
  const plan = planSvg(site, S);
  assert.ok(plan.startsWith('<svg') && plan.includes('>A1<') && plan.includes('Z0 hall box') && plan.includes('>W3<'), 'the plan labels stones, boxes and signs');
  assert.match(sceneSvg(S.get('hearth-ring'), 'front'), /^<svg/);
});
