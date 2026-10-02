#!/usr/bin/env node
// check-permissions.mjs: keeps docs/community/ops/staff-roles-and-permissions.md in step with the
// permission names the plugins really register.
//
//   node docs/community/ops/tools/check-permissions.mjs [--plugins <dir>] [--doc <file>]
//
// It fails (exit 1) when:
//   - a permission registered in plugins/*.cs has no row in the matrix table, or
//   - the doc names a permission (`*.admin` or `realm.court`) that no plugin registers
//     (a typo in a grant command would silently grant nothing).
// Exit 2: a file or folder could not be read.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..', '..', '..');

// String constants that hold a permission, and literal RegisterPermission("...") calls.
const CONST_RE = /const\s+string\s+(Perm\w*|Permission)\s*=\s*"([a-z0-9_.]+)"/g;
const LITERAL_RE = /RegisterPermission\(\s*"([a-z0-9_.]+)"/g;

export function permissionsInSource(text) {
  const out = new Set();
  for (const m of text.matchAll(CONST_RE)) out.add(m[2]);
  for (const m of text.matchAll(LITERAL_RE)) out.add(m[1]);
  return out;
}

export function pluginPermissions(dir) {
  const map = new Map(); // permission -> plugin file
  for (const f of fs.readdirSync(dir).filter((n) => n.endsWith('.cs')).sort()) {
    for (const p of permissionsInSource(fs.readFileSync(path.join(dir, f), 'utf8'))) map.set(p, f);
  }
  return map;
}

// Permissions with a row of their own: a table line whose first cell is the backticked name.
export function matrixRows(doc) {
  const rows = new Set();
  for (const line of doc.split(/\r?\n/)) {
    const m = /^\|\s*`([a-z0-9_.]+)`\s*\|/.exec(line);
    if (m) rows.add(m[1]);
  }
  return rows;
}

// Every permission-shaped name anywhere in the doc.
export function namedInDoc(doc) {
  const out = new Set();
  for (const m of doc.matchAll(/\b([a-z][a-z0-9]*\.admin|realm\.court)\b/g)) out.add(m[1]);
  return out;
}

export function check(perms, doc) {
  const rows = matrixRows(doc);
  const named = namedInDoc(doc);
  const missingRows = [...perms.keys()].filter((p) => !rows.has(p)).sort();
  const unknown = [...named].filter((p) => !perms.has(p)).sort();
  return { missingRows, unknown, ok: missingRows.length === 0 && unknown.length === 0 };
}

export function main(argv, io = { out: (s) => process.stdout.write(s + '\n'), err: (s) => process.stderr.write(s + '\n') }) {
  let plugins = path.join(REPO, 'plugins');
  let docFile = path.join(REPO, 'docs', 'community', 'ops', 'staff-roles-and-permissions.md');
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--plugins' && argv[i + 1]) plugins = argv[++i];
    else if (argv[i] === '--doc' && argv[i + 1]) docFile = argv[++i];
    else { io.err(`check-permissions: unknown option ${argv[i]}`); return 2; }
  }
  let perms, doc;
  try {
    perms = pluginPermissions(plugins);
    doc = fs.readFileSync(docFile, 'utf8');
  } catch (e) {
    io.err(`check-permissions: ${e.message}`);
    return 2;
  }
  const r = check(perms, doc);
  io.out(`Permissions registered by plugins: ${perms.size}`);
  for (const [p, f] of [...perms].sort()) io.out(`  ${p.padEnd(30)} ${f}`);
  for (const p of r.missingRows) io.err(`MISSING ROW: ${p} (${perms.get(p)}) has no row in ${path.basename(docFile)}`);
  for (const p of r.unknown) io.err(`UNKNOWN: ${path.basename(docFile)} names ${p}, which no plugin registers`);
  io.out(r.ok ? 'OK: the matrix matches the plugins.' : 'FAIL');
  return r.ok ? 0 : 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
