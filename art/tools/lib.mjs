// Small shared helpers for the art tools. No dependencies beyond Node 18+.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const ART = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

// Resolves an npm module from art/tools/node_modules, then from $ART_NODE_MODULES (a node_modules folder),
// then from the global install. Nothing is vendored into the repo.
export async function loadModule(name) {
  const norm = (m) => (m && m.default && typeof m.default === 'object' ? { ...m.default, ...m } : m);
  const bases = [path.join(ART, 'tools', 'package.json')];
  if (process.env.ART_NODE_MODULES) bases.push(path.join(path.resolve(process.env.ART_NODE_MODULES), '..', 'package.json'));
  for (const base of bases) {
    try {
      const resolved = createRequire(base).resolve(name);
      return norm(await import(pathToFileURL(resolved).href));
    } catch { /* try the next location */ }
  }
  try { return norm(await import(name)); } catch { /* fall through */ }
  // last resort: a global npm install (npm root -g), as on CI images that ship Playwright globally
  try {
    const { execSync } = await import('node:child_process');
    const root = execSync('npm root -g', { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    return norm(await import(pathToFileURL(createRequire(path.join(root, 'noop.js')).resolve(name)).href));
  } catch { /* fall through */ }
  throw new Error(`Cannot find module "${name}". Run "npm install" in art/tools, or set ART_NODE_MODULES to a node_modules folder that has it.`);
}

export function walk(dir, filter = () => true) {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) out.push(...walk(p, filter));
    else if (filter(p)) out.push(p);
  }
  return out.sort();
}

export const rel = (p) => path.relative(ART, p).split(path.sep).join('/');

// Returns the inner markup of the first <g id="..."> element, honouring nested <g> tags.
export function innerGroup(svg, id) {
  const open = svg.search(new RegExp(`<g\\b[^>]*\\bid="${id}"[^>]*>`));
  if (open < 0) throw new Error(`no <g id="${id}">`);
  const startTag = svg.slice(open, svg.indexOf('>', open) + 1);
  let depth = 1, i = open + startTag.length;
  const re = /<(\/?)g\b[^>]*?(\/?)>/g;
  re.lastIndex = i;
  for (let m; (m = re.exec(svg));) {
    if (m[2] === '/') continue;
    depth += m[1] ? -1 : 1;
    if (depth === 0) return { startTag, inner: svg.slice(i, m.index), attrs: attrsOf(startTag) };
  }
  throw new Error(`unbalanced <g id="${id}">`);
}

export function attrsOf(tag) {
  const o = {};
  for (const m of tag.matchAll(/([\w:-]+)="([^"]*)"/g)) o[m[1]] = m[2];
  return o;
}

export const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
