#!/usr/bin/env node
// Realm Sculptor: builds, checks, previews and exports the sculptures that plugins/RealmSculptor.cs places in game.
//
//   node art/tools/sculptor/cli.mjs build [id...]      run art/sculptures/src/*.mjs, write art/sculptures/<id>.json
//   node art/tools/sculptor/cli.mjs check              validate every sculpture and that each is up to date with its generator
//   node art/tools/sculptor/cli.mjs preview [id...]    PNGs in art/sculptures/preview/ (headless Chromium)
//   node art/tools/sculptor/cli.mjs masks              re-rasterise the sigil masks (headless Chromium)
//   node art/tools/sculptor/cli.mjs voxelize <model.obj|.gltf|.glb> --id <id> --name "<name>" [--height 12]
//        [--material stone] [--license CC0-1.0] [--source <url>] [--credit "<text>"] [--up y|z] [--no-fit]
//   node art/tools/sculptor/cli.mjs export <oxide/data folder>   copy every sculpture to <folder>/RealmSculptor/
//   node art/tools/sculptor/cli.mjs info [id...]       block counts by material and shape
//
// Playwright comes from art/tools/node_modules or $ART_NODE_MODULES (see art/README.md).
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { ART } from '../lib.mjs';
import { validate, stats, FORMAT } from './voxel.mjs';

export const SCULPTURES = path.join(ART, 'sculptures');
export const SRC = path.join(SCULPTURES, 'src');
export const PREVIEW = path.join(SCULPTURES, 'preview');

/** Sculpture JSON text: the header as indented JSON, one block per line so diffs stay readable. */
export function formatSculpture(s) {
  const { blocks, ...head } = s;
  const top = JSON.stringify(head, null, 2).replace(/\n}$/, '');
  return `${top},\n  "blocks": [\n${blocks.map((b) => `    ${JSON.stringify(b)}`).join(',\n')}\n  ]\n}\n`;
}

/** Every sculpture definition from art/sculptures/src: [{ id, name, description, build, file }]. */
export async function loadDefinitions() {
  const defs = [];
  for (const f of fs.readdirSync(SRC).filter((x) => x.endsWith('.mjs')).sort()) {
    const mod = await import(pathToFileURL(path.join(SRC, f)).href);
    const list = Array.isArray(mod.default) ? mod.default : [mod.default];
    for (const d of list) defs.push({ ...d, file: `art/sculptures/src/${f}` });
  }
  const ids = defs.map((d) => d.id);
  const dup = ids.filter((id, i) => ids.indexOf(id) !== i);
  if (dup.length) throw new Error(`duplicate sculpture ids: ${dup.join(', ')}`);
  return defs;
}

export function buildOne(def) {
  const grid = def.build();
  const floating = grid.floating();
  if (floating.length) throw new Error(`${def.id}: ${floating.length} block(s) float (not face-connected to the ground layer)`);
  return grid.toSculpture({ id: def.id, name: def.name, description: def.description, license: 'realm-original', source: def.file });
}

export const readSculpture = (id) => JSON.parse(fs.readFileSync(path.join(SCULPTURES, `${id}.json`), 'utf8'));
export const sculptureFiles = () => fs.readdirSync(SCULPTURES).filter((f) => f.endsWith('.json')).sort();

function args(argv) {
  const o = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--no-')) o[a.slice(5)] = false;
    else if (a.startsWith('--')) o[a.slice(2)] = argv[i + 1] !== undefined && !argv[i + 1].startsWith('--') ? argv[++i] : true;
    else o._.push(a);
  }
  return o;
}

async function main() {
  const [cmd, ...rest] = process.argv.slice(2);
  const opt = args(rest);
  const want = (id) => !opt._.length || opt._.includes(id);
  if (cmd === 'build') {
    for (const d of await loadDefinitions()) {
      if (!want(d.id)) continue;
      const s = buildOne(d);
      const errs = validate(s);
      if (errs.length) { for (const e of errs) console.error(`error: ${e}`); process.exitCode = 1; continue; }
      fs.writeFileSync(path.join(SCULPTURES, `${d.id}.json`), formatSculpture(s));
      const st = stats(s);
      console.log(`${d.id}: ${st.blocks} blocks, ${st.size.join(' x ')}, ${Object.entries(st.shapes).map(([k, v]) => `${v} ${k}`).join(', ')}`);
    }
  } else if (cmd === 'check') {
    const defs = await loadDefinitions();
    const byId = new Map(defs.map((d) => [d.id, d]));
    let errors = 0;
    const files = sculptureFiles();
    for (const f of files) {
      let s;
      try { s = JSON.parse(fs.readFileSync(path.join(SCULPTURES, f), 'utf8')); } catch (e) { console.error(`error: ${f}: ${e.message}`); errors++; continue; }
      const errs = validate(s);
      if (s.id && `${s.id}.json` !== f) errs.push(`${f}: file name must be <id>.json`);
      const d = byId.get(s.id);
      if (s.license === 'realm-original') {
        if (!d) errs.push(`${s.id}: no generator in art/sculptures/src makes it`);
        else if (formatSculpture(buildOne(d)) !== fs.readFileSync(path.join(SCULPTURES, f), 'utf8')) errs.push(`${s.id}: out of date with ${d.file} (run: node art/tools/sculptor/cli.mjs build ${s.id})`);
      }
      for (const v of ['front', 'three-quarter', 'side', 'unpainted']) if (!fs.existsSync(path.join(PREVIEW, `${s.id}-${v}.png`))) errs.push(`${s.id}: preview/${s.id}-${v}.png is missing (run: node art/tools/sculptor/cli.mjs preview ${s.id})`);
      for (const e of errs) console.error(`error: ${e}`);
      errors += errs.length;
    }
    for (const d of defs) if (!files.includes(`${d.id}.json`)) { console.error(`error: ${d.id}.json is missing (run: node art/tools/sculptor/cli.mjs build ${d.id})`); errors++; }
    if (errors) { console.error(`sculptor check: ${errors} error(s)`); process.exitCode = 1; }
    else console.log(`sculptor check: ${files.length} sculptures (${FORMAT}) valid, up to date, previewed`);
  } else if (cmd === 'preview') {
    const { renderPreviews } = await import('./render.mjs');
    const list = sculptureFiles().map((f) => readSculpture(f.replace(/\.json$/, ''))).filter((s) => want(s.id));
    const files = await renderPreviews(list, PREVIEW, { contact: !opt._.length });
    console.log(`preview: ${files.length} PNGs in art/sculptures/preview`);
  } else if (cmd === 'masks') {
    const { buildMasks } = await import('./masks.mjs');
    const m = await buildMasks();
    console.log(`masks: ${Object.keys(m.masks).length} houses written to art/sculptures/src/sigil-masks.json`);
  } else if (cmd === 'voxelize') {
    const { loadModel, voxelize } = await import('./mesh.mjs');
    const file = opt._[0];
    if (!file || !opt.id || !opt.name) throw new Error('usage: voxelize <model> --id <id> --name "<name>" [--height 12] [--license CC0-1.0 --source <url>]');
    const grid = voxelize(loadModel(file), { height: Number(opt.height || 12), material: opt.material || 'stone', fit: opt.fit !== false, upAxis: opt.up || 'y' });
    const dropped = grid.dropFloating();
    const s = grid.toSculpture({ id: opt.id, name: opt.name, description: opt.description || '', license: opt.license || 'CC0-1.0', source: opt.source || file, credit: opt.credit || '' });
    const errs = validate(s);
    for (const e of errs) console.error(`error: ${e}`);
    const out = opt.out || path.join(SCULPTURES, `${opt.id}.json`);
    if (!errs.length) fs.writeFileSync(out, formatSculpture(s));
    console.log(`${opt.id}: ${s.blocks.length} blocks${dropped ? `, ${dropped} floating block(s) dropped` : ''}${errs.length ? ' (not written)' : ` -> ${path.relative(process.cwd(), out)}`}`);
    if (errs.length) process.exitCode = 1;
  } else if (cmd === 'export') {
    const dir = opt._[0];
    if (!dir) throw new Error('usage: export <oxide/data folder>');
    const to = path.join(dir, 'RealmSculptor');
    fs.mkdirSync(to, { recursive: true });
    for (const f of sculptureFiles()) fs.copyFileSync(path.join(SCULPTURES, f), path.join(to, f));
    console.log(`export: ${sculptureFiles().length} sculptures copied to ${to}`);
  } else if (cmd === 'info') {
    for (const f of sculptureFiles()) {
      const s = readSculpture(f.replace(/\.json$/, ''));
      if (!want(s.id)) continue;
      const st = stats(s);
      console.log(`${s.id} "${s.name}": ${st.blocks} blocks, ${st.size.join(' x ')} (${st.size.map((v) => (v * 1.2).toFixed(1)).join(' x ')} m)`);
      console.log(`  materials: ${Object.entries(st.materials).map(([k, v]) => `${k} ${v}`).join(', ')}; shapes: ${Object.entries(st.shapes).map(([k, v]) => `${k} ${v}`).join(', ')}`);
    }
  } else {
    console.log(fs.readFileSync(new URL(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).slice(1, 12).map((l) => l.slice(3)).join('\n'));
    if (cmd) process.exitCode = 1;
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((e) => { console.error(e.message); process.exitCode = 1; });
}
