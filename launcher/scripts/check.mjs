// npm run check: syntax-checks every script and confirms that each element id the renderer
// looks up exists in index.html.
import { execFileSync } from 'node:child_process';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const files = [
  'main.js',
  'preload.js',
  ...readdirSync(path.join(root, 'lib')).filter((f) => f.endsWith('.js')).map((f) => `lib/${f}`),
  'renderer/app.js',
  'renderer/mock-preload.js',
  ...readdirSync(path.join(root, 'scripts')).filter((f) => f.endsWith('.mjs')).map((f) => `scripts/${f}`),
  ...readdirSync(path.join(root, 'test')).filter((f) => f.endsWith('.js')).map((f) => `test/${f}`)
];

let failed = 0;
for (const f of files) {
  try {
    execFileSync(process.execPath, ['--check', path.join(root, f)], { stdio: 'pipe' });
  } catch (e) {
    failed++;
    console.error(`syntax error in ${f}:\n${e.stderr}`);
  }
}

const html = readFileSync(path.join(root, 'renderer', 'index.html'), 'utf8');
const app = readFileSync(path.join(root, 'renderer', 'app.js'), 'utf8');
const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]));
const used = new Set([...app.matchAll(/\$\('([A-Za-z0-9_-]+)'\)/g)].map((m) => m[1]));
for (const id of used) {
  if (!ids.has(id)) {
    failed++;
    console.error(`renderer/app.js uses #${id}, which is missing from index.html`);
  }
}

if (failed) {
  console.error(`check failed (${failed} problem(s))`);
  process.exit(1);
}
console.log(`check ok: ${files.length} files parsed, ${used.size} element ids resolved`);
