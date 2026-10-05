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
  'player/main.js',
  'player/preload.js',
  ...readdirSync(path.join(root, 'lib')).filter((f) => f.endsWith('.js')).map((f) => `lib/${f}`),
  ...readdirSync(path.join(root, 'lib', 'shared')).filter((f) => f.endsWith('.js')).map((f) => `lib/shared/${f}`),
  'renderer/app.js',
  'renderer/player.js',
  'renderer/coach.js',
  'renderer/mock-preload.js',
  // Views that build their own markup (Connection Doctor, Court, Discord card, Home dashboard strip).
  'renderer/doctor.js',
  'renderer/court.js',
  'renderer/discord.js',
  'renderer/dashboard.js',
  'renderer/publish-feeds.js',
  'renderer/sentinel.js',
  'renderer/features.js',
  ...readdirSync(path.join(root, 'build')).filter((f) => f.endsWith('.js')).map((f) => `build/${f}`),
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

// Every element id a renderer script looks up must exist in its page.
let resolved = 0;
for (const [page, script] of [['index.html', 'app.js'], ['player.html', 'player.js'], ['coach.html', 'coach.js']]) {
  const html = readFileSync(path.join(root, 'renderer', page), 'utf8');
  const js = readFileSync(path.join(root, 'renderer', script), 'utf8');
  const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]));
  const used = new Set([...js.matchAll(/\$\('([A-Za-z0-9_-]+)'\)/g)].map((m) => m[1]));
  resolved += used.size;
  for (const id of used) {
    if (!ids.has(id)) {
      failed++;
      console.error(`renderer/${script} uses #${id}, which is missing from ${page}`);
    }
  }
}

if (failed) {
  console.error(`check failed (${failed} problem(s))`);
  process.exit(1);
}
console.log(`check ok: ${files.length} files parsed, ${resolved} element ids resolved`);
