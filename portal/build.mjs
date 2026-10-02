#!/usr/bin/env node
// Realm Portal generator. Static output only: nothing here runs a server or uploads anything.
//
//   node build.mjs                                   sample data -> portal/dist
//   node build.mjs --data "G:\RealmTest\server\oxide\data" --config portal.config.json
//   node build.mjs --data <dir> --watch 60           rebuild when the data changes (checks every 60 s)
//
// Options: --data <dir> (repeatable; first dir that has a file wins), --config <portal.config.json>,
//          --oxide-config <oxide/config dir> (RealmEvents schedule; found automatically next to oxide/data),
//          --out <dir>, --docs <dir>, --watch [seconds], --quiet, --help

import { join, resolve } from 'node:path';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { buildSite, inputSignature, PORTAL_DIR, REPO_DIR } from './lib/generate.mjs';

export function parseArgs(argv) {
  const o = { dataDirs: [], configDirs: [], configFile: null, outDir: null, docsDir: null, watch: 0, quiet: false, help: false };
  for (let i = 0; i < argv.length; i++) {
    const [flag, inline] = argv[i].split(/=(.*)/s, 2);
    const val = () => (inline !== undefined ? inline : argv[++i]);
    if (flag === '--data') o.dataDirs.push(val());
    else if (flag === '--config') o.configFile = val();
    else if (flag === '--oxide-config') o.configDirs.push(val());
    else if (flag === '--out') o.outDir = val();
    else if (flag === '--docs') o.docsDir = val();
    else if (flag === '--watch') {
      const next = inline !== undefined ? inline : (argv[i + 1] && /^\d+$/.test(argv[i + 1]) ? argv[++i] : '60');
      o.watch = Math.max(10, Number(next) || 60);
    } else if (flag === '--quiet') o.quiet = true;
    else if (flag === '--help' || flag === '-h') o.help = true;
    else throw new Error(`Unknown option: ${argv[i]}`);
  }
  return o;
}

async function once(o) {
  const configFile = o.configFile || (existsSync(join(PORTAL_DIR, 'portal.config.json')) ? join(PORTAL_DIR, 'portal.config.json') : null);
  const t0 = Date.now();
  const r = await buildSite({ dataDirs: o.dataDirs, configDirs: o.configDirs, configFile, outDir: o.outDir, docsDir: o.docsDir });
  if (!o.quiet) {
    console.log(`[portal] built ${r.files.size} files into ${r.out} in ${Date.now() - t0} ms`);
    console.log(`[portal] ${r.model.events.length} events, ${r.model.houses.length} houses, ${r.model.reigns.length} reigns${r.model.state.king ? `, king: ${r.model.state.king}` : ''}`);
    if (!configFile) console.log('[portal] no portal.config.json: using defaults (copy portal.config.example.json to set the download link, address and streamers)');
  }
  for (const w of r.warnings) console.warn(`[portal] warning: ${w}`);
  return r;
}

async function main() {
  const o = parseArgs(process.argv.slice(2));
  if (o.help) {
    console.log('Usage: node build.mjs [--data <oxide/data dir>]... [--oxide-config <oxide/config dir>] [--config portal.config.json] [--out dist] [--docs docs/community] [--watch [seconds]]');
    return;
  }
  await once(o);
  if (!o.watch) return;
  const dirs = o.dataDirs.length ? o.dataDirs : [join(REPO_DIR, 'chronicle', 'sample-data')];
  const inputs = dirs.flatMap((d) => ['RealmChronicle.json', 'RealmState.json', 'RealmHouses.json', 'CrownAndConsequences.json', 'RealmSeasons.json', 'RealmLegends.json', 'RealmEvents.json'].map((f) => resolve(d, f)));
  if (o.configFile) inputs.push(resolve(o.configFile));
  let sig = await inputSignature(inputs);
  console.log(`[portal] watching ${dirs.join(', ')} every ${o.watch} s (Ctrl+C to stop)`);
  setInterval(async () => {
    const next = await inputSignature(inputs);
    if (next === sig) return;
    sig = next;
    try {
      await once({ ...o, quiet: true });
      console.log(`[portal] rebuilt at ${new Date().toISOString()}`);
    } catch (e) {
      console.error(`[portal] rebuild failed: ${e.message}`);
    }
  }, o.watch * 1000);
}

const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  main().catch((e) => {
    console.error(`[portal] ${e.friendly ? '' : 'build failed: '}${e.message}`);
    process.exit(1);
  });
}
