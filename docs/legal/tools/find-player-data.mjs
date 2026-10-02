#!/usr/bin/env node
// find-player-data.mjs: READ-ONLY helper for data access and deletion requests.
//
// It answers one question: "in which files, and where in each file, does this player appear?"
// It prints file paths, JSON paths and line numbers only. It never prints the values it found,
// and never prints anything about any other player. It never writes, moves or deletes anything.
//
//   node docs/legal/tools/find-player-data.mjs --steamid 7656119XXXXXXXXXX [--name "Old Tom"]
//        --dir <server>\oxide\data [--dir <server>\oxide\logs] [--dir ...] [--json]
//
// Exit codes: 0 = scan finished (matches or not), 1 = a --dir could not be read, 2 = bad options.
// See docs/legal/data-deletion-process.md for how the result is used.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const TEXT_EXT = new Set(['.json', '.jsonl', '.txt', '.log', '.cfg', '.csv']);
export const MAX_FILE_BYTES = 64 * 1024 * 1024;
export const MAX_DEPTH = 12;
export const MAX_PATHS_PER_FILE = 50;
const STEAM_ID_RE = /^7656119\d{10}$/;

export function parseArgs(argv) {
  const out = { dirs: [], names: [], steamid: null, json: false, help: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) throw new Error(`${a} needs a value`);
      return argv[++i];
    };
    if (a === '--dir') out.dirs.push(next());
    else if (a === '--steamid') out.steamid = next().trim();
    else if (a === '--name') out.names.push(next());
    else if (a === '--json') out.json = true;
    else if (a === '-h' || a === '--help') out.help = true;
    else throw new Error(`unknown option ${a}`);
  }
  if (out.help) return out;
  if (!out.steamid && out.names.length === 0) throw new Error('give --steamid and/or --name');
  if (out.steamid && !STEAM_ID_RE.test(out.steamid)) throw new Error('--steamid must be a 17-digit SteamID64 starting 7656119');
  for (const n of out.names) if (n.trim().length < 2) throw new Error('--name must be at least 2 characters');
  if (out.dirs.length === 0) throw new Error('give at least one --dir');
  out.names = out.names.map((n) => n.trim());
  return out;
}

// JSON.parse loses precision on SteamID64 numbers (they are larger than 2^53), so every bare
// integer of 16+ digits is quoted first. Strings are matched first by the alternation, so digits
// inside strings are left alone.
export function quoteBigInts(text) {
  return text.replace(/"(?:[^"\\]|\\.)*"|-?\d{16,}(?![\d.eE])/g, (m) => (m[0] === '"' ? m : `"${m}"`));
}

function jsonPathJoin(base, key) {
  if (typeof key === 'number') return `${base}[${key}]`;
  return /^[A-Za-z_$][\w$]*$/.test(key) ? `${base}.${key}` : `${base}[${JSON.stringify(key)}]`;
}

// Returns the JSON paths whose key or scalar value refers to the player.
export function findInJson(value, matcher, base = '$', out = [], seen = { n: 0 }) {
  if (out.length >= MAX_PATHS_PER_FILE) { seen.n++; return out; }
  if (Array.isArray(value)) {
    value.forEach((v, i) => findInJson(v, matcher, jsonPathJoin(base, i), out, seen));
  } else if (value && typeof value === 'object') {
    for (const k of Object.keys(value)) {
      const p = jsonPathJoin(base, k);
      if (matcher.key(k)) push(out, seen, `${p} (key)`);
      findInJson(value[k], matcher, p, out, seen);
    }
  } else if (value != null && matcher.value(String(value))) {
    push(out, seen, base);
  }
  return out;
}
function push(out, seen, p) {
  if (out.length < MAX_PATHS_PER_FILE) out.push(p);
  else seen.n++;
}

export function makeMatcher({ steamid, names }) {
  const lowNames = names.map((n) => n.toLowerCase());
  return {
    // A key is the player's when it is the id (dictionaries keyed by id) or an exact name.
    key: (k) => (steamid && k.includes(steamid)) || lowNames.includes(k.toLowerCase()),
    // A value refers to the player when it contains the id, or equals a name exactly
    // (case-insensitive). Names are matched exactly to avoid flagging every "Tom" in a log.
    value: (s) => (steamid && s.includes(steamid)) || lowNames.includes(s.trim().toLowerCase()),
    // Lines of plain text: the id anywhere, or a name as a whole word.
    line: (line) => {
      if (steamid && line.includes(steamid)) return true;
      const low = line.toLowerCase();
      return lowNames.some((n) => wholeWord(low, n));
    }
  };
}

function wholeWord(hay, needle) {
  let at = hay.indexOf(needle);
  while (at !== -1) {
    const before = at === 0 ? '' : hay[at - 1];
    const after = hay[at + needle.length] || '';
    if (!/[\p{L}\p{N}_]/u.test(before) && !/[\p{L}\p{N}_]/u.test(after)) return true;
    at = hay.indexOf(needle, at + 1);
  }
  return false;
}

export function scanText(text, matcher) {
  const lines = [];
  const all = text.split(/\r?\n/);
  for (let i = 0; i < all.length; i++) if (matcher.line(all[i])) lines.push(i + 1);
  return lines;
}

export function scanFile(file, matcher) {
  const st = fs.lstatSync(file);
  if (st.size > MAX_FILE_BYTES) return { file, skipped: `larger than ${MAX_FILE_BYTES} bytes; check it by hand` };
  const text = fs.readFileSync(file, 'utf8');
  const ext = path.extname(file).toLowerCase();
  if (ext === '.json') {
    try {
      const seen = { n: 0 };
      const paths = findInJson(JSON.parse(quoteBigInts(text)), matcher, '$', [], seen);
      if (paths.length === 0) return null;
      return { file, kind: 'json', paths, more: seen.n };
    } catch {
      // Damaged JSON: fall back to a line scan so nothing is missed.
      const lines = scanText(text, matcher);
      return lines.length ? { file, kind: 'text (JSON did not parse)', lines } : null;
    }
  }
  const lines = scanText(text, matcher);
  return lines.length ? { file, kind: 'text', lines } : null;
}

export function walk(dir, files = [], depth = 0, notes = []) {
  if (depth > MAX_DEPTH) { notes.push(`${dir}: deeper than ${MAX_DEPTH} levels, not scanned`); return files; }
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (e) {
    notes.push(`${dir}: cannot read (${e.code || e.message})`);
    return files;
  }
  entries.sort((a, b) => a.name.localeCompare(b.name));
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isSymbolicLink()) { notes.push(`${p}: symbolic link, not followed`); continue; }
    if (e.isDirectory()) walk(p, files, depth + 1, notes);
    else if (e.isFile() && TEXT_EXT.has(path.extname(e.name).toLowerCase())) files.push(p);
    else if (e.isFile() && /\.(zip|7z|bak)$/i.test(e.name)) notes.push(`${p}: archive or backup, not opened; it may hold the player's data until it rotates out`);
  }
  return files;
}

export function scan({ dirs, steamid, names }) {
  const matcher = makeMatcher({ steamid, names });
  const notes = [];
  const results = [];
  let filesScanned = 0;
  let unreadable = false;
  for (const d of dirs) {
    if (!fs.existsSync(d) || !fs.statSync(d).isDirectory()) {
      notes.push(`${d}: not a folder`);
      unreadable = true;
      continue;
    }
    for (const f of walk(d, [], 0, notes)) {
      filesScanned++;
      try {
        const r = scanFile(f, matcher);
        if (r) results.push(r);
      } catch (e) {
        notes.push(`${f}: cannot read (${e.code || e.message})`);
      }
    }
  }
  return { filesScanned, results, notes, unreadable };
}

export function formatReport(rep, { steamid, names }) {
  const who = [steamid ? `SteamID64 ${steamid.slice(0, 7)}...${steamid.slice(-4)}` : null, ...names.map((n) => `name "${n}"`)]
    .filter(Boolean)
    .join(', ');
  const out = [`Player data search (read-only) for ${who}`, `Files scanned: ${rep.filesScanned}. Files with matches: ${rep.results.length}.`, ''];
  for (const r of rep.results) {
    out.push(`${r.file}  [${r.kind || 'skipped'}]`);
    if (r.skipped) out.push(`    skipped: ${r.skipped}`);
    if (r.paths) {
      for (const p of r.paths) out.push(`    ${p}`);
      if (r.more) out.push(`    ... and ${r.more} more`);
    }
    if (r.lines) out.push(`    lines: ${r.lines.slice(0, 40).join(', ')}${r.lines.length > 40 ? ` ... (${r.lines.length} lines)` : ''}`);
  }
  if (rep.notes.length) {
    out.push('', 'Notes:');
    for (const n of rep.notes) out.push(`  - ${n}`);
  }
  out.push('', 'Values are not printed. Open each file yourself to review or remove the entries (see data-deletion-process.md).');
  return out.join('\n');
}

const HELP = `find-player-data: read-only search for one player's data in Realm server files.

  node find-player-data.mjs --steamid <SteamID64> [--name <name>]... --dir <folder> [--dir <folder>]... [--json]

Prints file paths, JSON paths and line numbers only. Never prints values or other players' data.`;

export function main(argv, io = { out: (s) => process.stdout.write(s + '\n'), err: (s) => process.stderr.write(s + '\n') }) {
  let opts;
  try {
    opts = parseArgs(argv);
  } catch (e) {
    io.err(`find-player-data: ${e.message}\n\n${HELP}`);
    return 2;
  }
  if (opts.help) { io.out(HELP); return 0; }
  const rep = scan(opts);
  if (opts.json) io.out(JSON.stringify({ filesScanned: rep.filesScanned, results: rep.results, notes: rep.notes }, null, 2));
  else io.out(formatReport(rep, opts));
  return rep.unreadable ? 1 : 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
