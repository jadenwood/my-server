// Tests for art/tools/sculptor. Run: node --test art/tools/sculptor/test/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { EULER, MATRICES, indexOfMatrix, turnIndex, turnPos, mul, yaw, apply } from '../rotations.mjs';
import { quantise, pal, house, PALETTE_HEXES, COLOURS, rgbToLab, hexToRgb } from '../palette.mjs';
import { SHAPES, insideBlock, blockFaces, orient, KINDS } from '../shapes.mjs';
import { Grid, style, validate, stats, fitCandidates, MATERIALS, roleId, FORMAT, MAX_BLOCKS } from '../voxel.mjs';
import { parseOBJ, parseMTL, voxelize, parseGLB, trianglesFromGLTF } from '../mesh.mjs';
import { classify } from '../masks.mjs';
import { projectScene, sceneSvg } from '../render.mjs';
import { formatSculpture, loadDefinitions, buildOne, sculptureFiles, readSculpture, rotationsFixture, ROTATIONS_FIXTURE, SCULPTURES } from '../cli.mjs';

const GREY = pal('Iron 200');
const stone = () => style('stone', GREY);

// ------------------------------------------------------------------ rotations

test('rotations: 24 different turns, closed under quarter-turns about the vertical', () => {
  assert.equal(EULER.length, 24);
  assert.equal(new Set(MATRICES.map((m) => m.flat().join())).size, 24);
  for (let i = 0; i < 24; i++) {
    assert.equal(turnIndex(i, 0), i);
    let j = i;
    for (let k = 0; k < 4; k++) j = turnIndex(j, 1);
    assert.equal(j, i, 'four quarter-turns come back');
    for (let k = 0; k < 4; k++) assert.ok(turnIndex(i, k) >= 0);
  }
  assert.equal(indexOfMatrix(yaw(1)), 1, 'index 1 is Euler(0, 90, 0)');
  assert.deepEqual(apply(MATRICES[1], [0, 0, 1]), [1, 0, 0], 'Unity: +90 about y turns forward (+z) to right (+x)');
  assert.deepEqual(apply(MATRICES[4], [0, 1, 0]), [0, -1, 0], 'index 4 is upside down');
  assert.deepEqual(turnPos([1, 0, 0], 1), [0, 0, -1]);
  assert.deepEqual(mul(yaw(2), yaw(2)), MATRICES[0]);
});

test('rotations: the plugin tests\' rotations.json is up to date', () => {
  assert.equal(fs.readFileSync(ROTATIONS_FIXTURE, 'utf8'), rotationsFixture(), 'run: node art/tools/sculptor/cli.mjs fixtures');
});

test('rotations: plugins/RealmSculptor.cs carries the same Euler and turn tables', () => {
  const src = fs.readFileSync(path.join(SCULPTURES, '..', '..', 'plugins', 'RealmSculptor.cs'), 'utf8');
  const block = (name) => [...src.match(new RegExp(`${name} = new int\\[,\\]\\s*\\{([\\s\\S]*?)\\};`))[1].matchAll(/\{([^{}]*)\}/g)].map((m) => m[1].split(',').map((v) => Number(v.trim())));
  assert.deepEqual(block('Euler'), EULER);
  assert.deepEqual(block('TurnTable'), EULER.map((_, i) => [0, 1, 2, 3].map((k) => turnIndex(i, k))));
});

// ------------------------------------------------------------------ palette

test('palette: palette colours map to themselves, others to the nearest', () => {
  for (const c of COLOURS) assert.equal(quantise(c.hex), c.hex);
  assert.equal(quantise('#d7a145'), pal('Ember'));
  assert.equal(quantise([140, 44, 35]), pal('Blood'));
  assert.ok(PALETTE_HEXES.has(house('varrow', 'field')));
  assert.throws(() => pal('Not a colour'));
  assert.throws(() => house('varrow', 'nope'));
  const [L] = rgbToLab(hexToRgb('#ffffff'));
  assert.ok(Math.abs(L - 100) < 0.01);
});

// ------------------------------------------------------------------ shapes

test('shapes: the cube is solid; the ramp is half; orient finds the rotation', () => {
  assert.ok(insideBlock(0, 0, [0.4, 0.4, 0.4]));
  assert.ok(insideBlock(2, 0, [0, -0.4, 0.4]), 'ramp at rotation 0 is solid low and at the back');
  assert.ok(!insideBlock(2, 0, [0, 0.4, -0.4]), 'and open high at the front');
  const r = orient([1, 0, 0], [0, -1, 0]);
  assert.ok(insideBlock(2, r, [0.4, 0.3, 0]) && !insideBlock(2, r, [-0.4, 0.3, 0]), 'a ramp oriented high +x is solid on +x');
  assert.equal(blockFaces(0, 0).length, 6);
  assert.equal(blockFaces(2, 5).length, KINDS.wedge.faces.length);
  assert.throws(() => orient([0, 1, 0], [0, -1, 0]), 'high and base cannot be opposite');
  assert.deepEqual(SHAPES.filter((s) => s.fit).map((s) => s.prefabId), [0, 2], 'only the cube and the ramp are fitted until the shape test is seen in game');
});

// ------------------------------------------------------------------ voxel grid

test('grid: sampling a solid gives blocks, and ramps on a 45-degree slope', () => {
  const s = stone();
  const slope = (x, y, z) => (y <= z && x >= 0 && x < 4 && y >= 0 && z >= 0 && z < 4 ? s : null);
  const g = new Grid().sample(slope, [0, 0, 0], [3, 3, 3]);
  const ramps = [...g].filter((c) => c.prefab === 2);
  assert.ok(ramps.length >= 4, `the diagonal cells become ramps (${ramps.length})`);
  for (const c of ramps) assert.ok(insideBlock(2, c.rot, [0, -0.4, 0.4]), 'each ramp is solid where the slope is');
  const plain = new Grid().sample(slope, [0, 0, 0], [3, 3, 3], { fit: false });
  assert.ok([...plain].every((c) => c.prefab === 0), 'fit: false gives plain blocks only');
  const ball = new Grid().sample((x, y, z) => (Math.hypot(x - 3, y - 3, z - 3) < 2.6 ? s : null), [0, 0, 0], [6, 6, 6]);
  assert.ok(ball.size > 40 && ball.size < 100, `a ball of radius 2.6 (${ball.size} cells)`);
  assert.ok(fitCandidates(4).length >= 12, 'every distinct ramp orientation is a candidate');
});

test('grid: floating cells, bottom-up order and the sculpture object', () => {
  const s = stone();
  const g = new Grid().box(0, 0, 0, 2, 0, 1, s).set(1, 1, 0, s).set(1, 3, 0, s);
  assert.equal(g.floating().length, 1);
  assert.equal(g.dropFloating(), 1);
  g.set(-1, 0, 0, style('reinforced', pal('Iron 700')), 2, 1);
  const out = g.toSculpture({ id: 'tiny', name: 'Tiny', license: 'realm-original', source: 'test' });
  assert.equal(out.format, FORMAT);
  assert.deepEqual(out.size, [4, 2, 2]);
  assert.ok(out.blocks.every((b, i) => i === 0 || b[1] >= out.blocks[i - 1][1]), 'bottom-up');
  assert.deepEqual(out.materials, { [roleId('stone')]: 'stone', [roleId('reinforced')]: 'reinforced' });
  assert.deepEqual(validate(out), []);
  assert.equal(stats(out).blocks, out.blocks.length);
  const bare = new Grid().box(0, 0, 0, 1, 0, 0, style('stone', null)).toSculpture({ id: 'bare', name: 'Bare', license: 'realm-original', source: 'test' });
  assert.ok(bare.blocks.every((b) => b[6] === null) && validate(bare).length === 0, 'null colour = the material\'s own look');
  assert.ok(projectScene(bare, {}).polys.length > 0);
});

test('grid: validate refuses what the plugin would refuse', () => {
  const ok = new Grid().box(0, 0, 0, 1, 1, 0, stone()).toSculpture({ id: 'v', name: 'V', license: 'realm-original', source: 'test' });
  const bad = (mut, re) => {
    const s = JSON.parse(JSON.stringify(ok));
    mut(s);
    const e = validate(s).join('\n');
    assert.match(e, re);
  };
  bad((s) => { s.blocks[0][6] = '#123456'; }, /not in art\/palette.json/);
  bad((s) => { s.blocks[0][6] = 'red'; }, /#rrggbb/);
  bad((s) => { s.blocks[0][0] = 9; }, /outside size/);
  bad((s) => { s.blocks[1] = [...s.blocks[0]]; }, /repeats position/);
  bad((s) => { s.blocks[0][4] = 10; }, /single-block shapes/);
  bad((s) => { s.blocks[0][5] = 3; }, /plain block with rotation/);
  bad((s) => { s.blocks[0][3] = 77; }, /materials.json does not list/);
  bad((s) => { s.id = 'Bad Id'; }, /lower-case words/);
  bad((s) => { s.license = 'CC0-1.0'; }, /source URL/);
  bad((s) => { s.license = 'CC-BY-4.0'; s.source = 'https://example.org/m'; }, /credit line/);
  bad((s) => { s.license = 'All rights reserved'; }, /license/);
  bad((s) => { s.blocks.reverse(); }, /bottom-up/);
  bad((s) => { s.blocks.push([0, 3, 0, roleId('stone'), 0, 0, GREY]); s.size[1] = 4; }, /not connected to the ground/);
  bad((s) => { s.blocks = Array.from({ length: MAX_BLOCKS + 1 }, (_, i) => [i % 64, 0, Math.floor(i / 64) % 64, roleId('stone'), 0, 0, GREY]); s.size = [64, 1, 64]; }, /over the 4000 limit/);
});

// ------------------------------------------------------------------ models

const CUBE_OBJ = `mtllib cube.mtl
v 0 0 0
v 1 0 0
v 1 1 0
v 0 1 0
v 0 0 1
v 1 0 1
v 1 1 1
v 0 1 1
usemtl red
f 1 4 3 2
f 5 6 7 8
f 1 2 6 5
f 4 8 7 3
f 1 5 8 4
f 2 3 7 6
`;

test('models: an OBJ cube with an MTL colour voxelizes to a solid block of the nearest palette colour', () => {
  const tris = parseOBJ(CUBE_OBJ, parseMTL('newmtl red\nKd 0.55 0.17 0.13\n'));
  assert.equal(tris.length, 12);
  const g = voxelize(tris, { height: 4, material: 'stone' });
  assert.equal(g.size, 64);
  assert.ok([...g].every((c) => c.prefab === 0 && c.color === pal('Blood')));
});

test('models: a sloped OBJ wedge gets ramps', () => {
  const wedge = `v 0 0 0\nv 4 0 0\nv 4 0 4\nv 0 0 4\nv 0 4 4\nv 4 4 4\nf 1 2 3 4\nf 4 3 6 5\nf 1 5 6 2\nf 1 4 5\nf 2 6 3\n`;
  const g = voxelize(parseOBJ(wedge), { height: 4, material: 'stone' });
  assert.ok([...g].some((c) => c.prefab === 2), 'the slope is fitted with ramps');
  assert.ok(new Grid().sample(() => null, [0, 0, 0], [1, 1, 1]).size === 0);
});

function glbCube() {
  const pos = new Float32Array([0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1]);
  const idx = new Uint16Array([0, 3, 2, 0, 2, 1, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5]);
  const bin = Buffer.concat([Buffer.from(pos.buffer), Buffer.from(idx.buffer)]);
  const json = {
    asset: { version: '2.0' }, scene: 0, scenes: [{ nodes: [0] }], nodes: [{ mesh: 0, scale: [2, 2, 2] }],
    meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1, material: 0 }] }],
    materials: [{ pbrMetallicRoughness: { baseColorFactor: [0.69, 0.35, 0.03, 1] } }],
    buffers: [{ byteLength: bin.length }],
    bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: pos.byteLength }, { buffer: 0, byteOffset: pos.byteLength, byteLength: idx.byteLength }],
    accessors: [{ bufferView: 0, componentType: 5126, count: 8, type: 'VEC3' }, { bufferView: 1, componentType: 5123, count: 36, type: 'SCALAR' }],
  };
  const pad = (b, c) => Buffer.concat([b, Buffer.alloc((4 - (b.length % 4)) % 4, c)]);
  const j = pad(Buffer.from(JSON.stringify(json)), 0x20), b = pad(bin, 0);
  const head = Buffer.alloc(12); head.writeUInt32LE(0x46546c67, 0); head.writeUInt32LE(2, 4); head.writeUInt32LE(12 + 8 + j.length + 8 + b.length, 8);
  const ch = (len, type) => { const h = Buffer.alloc(8); h.writeUInt32LE(len, 0); h.writeUInt32LE(type, 4); return h; };
  return { glb: Buffer.concat([head, ch(j.length, 0x4e4f534a), j, ch(b.length, 0x004e4942), b]), json, bin };
}

test('models: GLB and glTF with node transforms and material colours', () => {
  const { glb, json, bin } = glbCube();
  const tris = parseGLB(glb);
  assert.equal(tris.length, 12);
  assert.ok(tris.some((t) => t.a.includes(2) || t.b.includes(2) || t.c.includes(2)), 'node scale is applied');
  const g = voxelize(tris, { height: 3 });
  assert.equal(g.size, 27);
  assert.ok([...g].every((c) => c.color === quantise([220, 160, 60]) || PALETTE_HEXES.has(c.color)));
  assert.equal(trianglesFromGLTF(json, [bin]).length, 12);
  assert.throws(() => parseGLB(Buffer.from('not a glb at all')), /magic/);
});

// ------------------------------------------------------------------ masks, render

test('masks: coverage and brightness classify cells', () => {
  const cells = [{ cover: 0.1, lum: 0.9 }, { cover: 0.9, lum: 0.9 }, { cover: 0.9, lum: 0.1 }, { cover: 0.5, lum: 0.5 }];
  assert.deepEqual(classify(cells, 2), ['.#', '+#']);
});

test('render: hidden faces between blocks are culled; the SVG draws every visible face', () => {
  const one = { id: 'a', size: [1, 1, 1], blocks: [[0, 0, 0, 2, 0, 0, GREY]] };
  const two = { id: 'b', size: [2, 1, 1], blocks: [[0, 0, 0, 2, 0, 0, GREY], [1, 0, 0, 2, 0, 0, GREY]] };
  const v1 = projectScene(one, { yaw: 38, pitch: 24 }).polys.length;
  const v2 = projectScene(two, { yaw: 38, pitch: 24 }).polys.length;
  assert.equal(v1, 3, 'a cube seen at three-quarters shows three faces');
  assert.equal(v2, 5, 'two cubes side by side show five (the shared faces are culled)');
  const svg = sceneSvg(two, 'front');
  assert.match(svg, /^<svg/);
  assert.ok((svg.match(/<polygon/g) || []).length >= 5);
  const unpainted = projectScene(one, { mode: 'unpainted' }).polys[0].fill;
  assert.notEqual(unpainted, projectScene(one, {}).polys[0].fill);
});

// ------------------------------------------------------------------ the sculptures

test('sculptures: every generator builds a valid sculpture that matches its committed file', async () => {
  const defs = await loadDefinitions();
  const ids = defs.map((d) => d.id).sort();
  for (const want of ['heralds-pillar', 'ironbreaker', 'old-throne', 'tournament-arch', 'shape-test',
    'house-varrow', 'house-ashgrove', 'house-corvane', 'house-dunmere', 'house-halloran', 'house-merrin']) assert.ok(ids.includes(want), want);
  assert.deepEqual(sculptureFiles().map((f) => f.replace(/\.json$/, '')).sort(), ids);
  for (const d of defs) {
    const s = buildOne(d);
    assert.deepEqual(validate(s), [], d.id);
    assert.equal(formatSculpture(s), fs.readFileSync(path.join(SCULPTURES, `${d.id}.json`), 'utf8'), `${d.id} is up to date (node art/tools/sculptor/cli.mjs build)`);
    assert.equal(s.license, 'realm-original');
    for (const v of ['front', 'three-quarter', 'side', 'unpainted']) assert.ok(fs.existsSync(path.join(SCULPTURES, 'preview', `${d.id}-${v}.png`)), `${d.id}-${v}.png`);
  }
});

test('sculptures: the brief', () => {
  const ib = readSculpture('ironbreaker');
  assert.ok(ib.size[1] >= 12 && ib.size[1] <= 16, `the Ironbreaker is 12-16 blocks tall (${ib.size[1]})`);
  const roles = (s) => new Set(s.blocks.map((b) => s.materials[b[3]]));
  assert.ok(roles(ib).has('reinforced') && roles(ib).has('log') && roles(ib).has('stone'), 'iron blade, leather (log) grip, stone plinth');
  const hp = readSculpture('heralds-pillar');
  assert.ok(hp.blocks.length < 200, 'the first-test piece is small');
  for (const key of ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin']) {
    const s = readSculpture(`house-${key}`);
    const cols = new Set(s.blocks.map((b) => b[6]));
    assert.ok(cols.has(house(key, 'field')) && cols.has(house(key, 'metal')), `${key} carries its field and metal colours`);
  }
  const arch = readSculpture('tournament-arch');
  const archCols = new Set(arch.blocks.map((b) => b[6]));
  for (const key of ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin']) assert.ok(archCols.has(house(key, 'field')), `the arch hangs ${key}'s banner`);
  const throne = readSculpture('old-throne');
  const tc = new Set(throne.blocks.map((b) => b[6]));
  assert.ok(tc.has(pal('Iron 900')) && tc.has(pal('Ember')) && tc.has(pal('Blood')), 'black stone, gold, red');
  for (const f of sculptureFiles()) {
    const s = JSON.parse(fs.readFileSync(path.join(SCULPTURES, f), 'utf8'));
    assert.ok(s.blocks.length <= MAX_BLOCKS && s.size.every((v) => v <= 64), f);
    assert.ok(s.blocks.every((b) => MATERIALS.some((m) => m.id === b[3])), f);
  }
});

test('cli: formatSculpture writes one block per line and parses back', () => {
  const s = new Grid().box(0, 0, 0, 1, 0, 0, stone()).toSculpture({ id: 'f', name: 'F', license: 'realm-original', source: 't' });
  const text = formatSculpture(s);
  assert.deepEqual(JSON.parse(text), s);
  assert.equal(text.split('\n').filter((l) => l.trim().startsWith('[')).length, 2);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sculptor-'));
  fs.writeFileSync(path.join(dir, 'f.json'), text);
  assert.ok(fs.statSync(path.join(dir, 'f.json')).size > 0);
  fs.rmSync(dir, { recursive: true, force: true });
});
