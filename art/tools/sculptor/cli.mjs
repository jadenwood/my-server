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
//   node art/tools/sculptor/cli.mjs fixtures           rewrite the plugin tests' rotations.json from rotations.mjs
//   node art/tools/sculptor/cli.mjs site <id> [--anchor x,y,z] [--turn 0-3] [--draw slot=house,...]
//        a site's run-sheet on the ground: stand spots and /sculpt place commands (sites: art/sculptures/sites)
//
// build also writes art/sculptures/sites/<id>.json from art/sculptures/src/sites/<id>.mjs; preview [id] renders a
// site's views too (--views a,b to pick some); check validates sites against the sculptures.
//
// Playwright comes from art/tools/node_modules or $ART_NODE_MODULES (see art/README.md).
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { ART } from '../lib.mjs';
import { validate, stats, FORMAT } from './voxel.mjs';
import { EULER, turnIndex } from './rotations.mjs';
import { validateSite, formatSite, resolveSite, SITE_FORMAT } from './site.mjs';

export const SCULPTURES = path.join(ART, 'sculptures');
export const SRC = path.join(SCULPTURES, 'src');
export const PREVIEW = path.join(SCULPTURES, 'preview');
export const SITES = path.join(SCULPTURES, 'sites');
export const SITE_SRC = path.join(SRC, 'sites');
export const ROTATIONS_FIXTURE = path.join(ART, '..', 'plugins', 'docs', 'RealmSculptor', 'logic-tests', 'rotations.json');

/** The rotation table both sides check: Euler angles and the index after k quarter-turns about the vertical axis. */
export function rotationsFixture() {
  const turn = EULER.map((_, i) => [0, 1, 2, 3].map((k) => turnIndex(i, k)));
  return `{\n  "note": "Written by node art/tools/sculptor/cli.mjs fixtures from art/tools/sculptor/rotations.mjs. plugins/RealmSculptor.cs must carry the same tables.",\n`
    + `  "euler": [\n${EULER.map((e) => `    ${JSON.stringify(e)}`).join(',\n')}\n  ],\n`
    + `  "turn": [\n${turn.map((t) => `    ${JSON.stringify(t)}`).join(',\n')}\n  ]\n}\n`;
}

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

/** Every committed sculpture, by id. */
export const sculptureMap = () => new Map(sculptureFiles().map((f) => { const s = JSON.parse(fs.readFileSync(path.join(SCULPTURES, f), 'utf8')); return [s.id, s]; }));

/** Every site definition from art/sculptures/src/sites: [{ id, name, description, previews, build(sculptures), file }]. */
export async function loadSiteDefinitions() {
  if (!fs.existsSync(SITE_SRC)) return [];
  const defs = [];
  for (const f of fs.readdirSync(SITE_SRC).filter((x) => x.endsWith('.mjs')).sort()) {
    const mod = await import(pathToFileURL(path.join(SITE_SRC, f)).href);
    defs.push({ ...mod.default, file: `art/sculptures/src/sites/${f}` });
  }
  return defs;
}
export const siteFiles = () => (fs.existsSync(SITES) ? fs.readdirSync(SITES).filter((f) => f.endsWith('.json')).sort() : []);
export const readSite = (id) => JSON.parse(fs.readFileSync(path.join(SITES, `${id}.json`), 'utf8'));
export const buildSite = (def, sculptures = sculptureMap()) => def.build(sculptures);

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
    // Sites are built from the sculptures just written, so they are always rebuilt last.
    const sculptures = sculptureMap();
    for (const d of await loadSiteDefinitions()) {
      if (!want(d.id) && opt._.length && !opt._.some((id) => sculptures.has(id))) continue;
      const site = buildSite(d, sculptures);
      const errs = validateSite(site, sculptures);
      if (errs.length) { for (const e of errs) console.error(`error: ${e}`); process.exitCode = 1; continue; }
      fs.mkdirSync(SITES, { recursive: true });
      fs.writeFileSync(path.join(SITES, `${d.id}.json`), formatSite(site));
      console.log(`site ${d.id}: ${site.pieces.length} pieces, ${Object.keys(site.cells).length} cell groups, ${Object.keys(site.points).length} points, ${site.signs.length} signs`);
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
    // Sites: valid against the committed sculptures, up to date with their generators, previewed.
    const sculptures = sculptureMap();
    const siteDefs = await loadSiteDefinitions();
    const { sitePreviewNames } = await import('./site-render.mjs');
    for (const d of siteDefs) {
      const f = path.join(SITES, `${d.id}.json`);
      const errs = [];
      if (!fs.existsSync(f)) errs.push(`sites/${d.id}.json is missing (run: node art/tools/sculptor/cli.mjs build)`);
      else {
        let site;
        try { site = JSON.parse(fs.readFileSync(f, 'utf8')); } catch (e) { errs.push(`sites/${d.id}.json: ${e.message}`); }
        if (site) {
          errs.push(...validateSite(site, sculptures));
          if (formatSite(buildSite(d, sculptures)) !== fs.readFileSync(f, 'utf8')) errs.push(`sites/${d.id}.json is out of date with ${d.file} (run: node art/tools/sculptor/cli.mjs build)`);
        }
      }
      for (const v of sitePreviewNames(d)) if (!fs.existsSync(path.join(PREVIEW, `site-${d.id}-${v}.png`))) errs.push(`preview/site-${d.id}-${v}.png is missing (run: node art/tools/sculptor/cli.mjs preview ${d.id})`);
      for (const e of errs) console.error(`error: ${e}`);
      errors += errs.length;
    }
    for (const f of siteFiles()) if (!siteDefs.some((d) => `${d.id}.json` === f)) { console.error(`error: sites/${f} has no generator in art/sculptures/src/sites`); errors++; }
    if (errors) { console.error(`sculptor check: ${errors} error(s)`); process.exitCode = 1; }
    else console.log(`sculptor check: ${files.length} sculptures (${FORMAT}) and ${siteDefs.length} site(s) (${SITE_FORMAT}) valid, up to date, previewed`);
  } else if (cmd === 'preview') {
    const { renderPreviews } = await import('./render.mjs');
    const list = sculptureFiles().map((f) => readSculpture(f.replace(/\.json$/, ''))).filter((s) => want(s.id));
    const files = list.length ? await renderPreviews(list, PREVIEW, { contact: !opt._.length }) : [];
    const { renderSitePreviews } = await import('./site-render.mjs');
    const sculptures = sculptureMap();
    for (const d of await loadSiteDefinitions()) {
      if (!want(d.id)) continue;
      const only = opt.views ? String(opt.views).split(',') : null;
      files.push(...await renderSitePreviews(readSite(d.id), sculptures, PREVIEW, d, only));
    }
    console.log(`preview: ${files.length} PNGs in art/sculptures/preview`);
  } else if (cmd === 'site') {
    // The run-sheet for a site on the ground: where each piece goes and where to stand, for an anchor and a turn.
    const id = opt._[0];
    if (!id) throw new Error('usage: site <id> [--anchor x,y,z] [--turn 0-3] [--draw p1-left=varrow,...]');
    const site = readSite(id);
    const anchor = opt.anchor ? String(opt.anchor).split(',').map(Number) : [0, 0, 0];
    const draw = { ...(site.lot ? site.lot.preview : {}) };
    if (opt.draw) for (const kv of String(opt.draw).split(',')) { const [k, v] = kv.split('='); draw[k] = v; }
    const r = resolveSite(site, sculptureMap(), anchor, Number(opt.turn || 0), draw);
    console.log(`${site.name}: anchor ${anchor.join(',')} turn ${r.turn}`);
    for (const p of r.pieces.filter((x) => x.by === 'sculptor').sort((a, b) => a.order - b.order)) console.log(`  ${String(p.order).padStart(2)}. ${p.key.padEnd(18)} stand ${p.stand.join(',')} facing ${p.facing}: ${p.command}   (corner ${p.corner.join(',')})`);
    for (const p of r.pieces.filter((x) => x.by === 'plugin')) console.log(`  plugin  ${p.key}: corner ${p.corner.join(',')}`);
    for (const [k, c] of Object.entries(r.points)) console.log(`  point ${k}: ${c.join(',')}`);
    for (const l of site.lights || []) console.log(`  light ${l.key} (${l.kind}, staff-built): ${r.lights[l.key].join(',')}`);
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
  } else if (cmd === 'fixtures') {
    fs.mkdirSync(path.dirname(ROTATIONS_FIXTURE), { recursive: true });
    fs.writeFileSync(ROTATIONS_FIXTURE, rotationsFixture());
    console.log(`fixtures: ${path.relative(process.cwd(), ROTATIONS_FIXTURE)}`);
  } else if (cmd === 'info') {
    for (const f of sculptureFiles()) {
      const s = readSculpture(f.replace(/\.json$/, ''));
      if (!want(s.id)) continue;
      const st = stats(s);
      console.log(`${s.id} "${s.name}": ${st.blocks} blocks, ${st.size.join(' x ')} (${st.size.map((v) => (v * 1.2).toFixed(1)).join(' x ')} m)`);
      console.log(`  materials: ${Object.entries(st.materials).map(([k, v]) => `${k} ${v}`).join(', ')}; shapes: ${Object.entries(st.shapes).map(([k, v]) => `${k} ${v}`).join(', ')}`);
    }
  } else {
    console.log(fs.readFileSync(new URL(import.meta.url), 'utf8').split('\n').filter((l) => l.startsWith('//')).slice(1, 19).map((l) => l.slice(3)).join('\n'));
    if (cmd) process.exitCode = 1;
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((e) => { console.error(e.message); process.exitCode = 1; });
}
