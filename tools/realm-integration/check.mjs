#!/usr/bin/env node
// Realm integration check: static cross-plugin wiring and Chronicle sync for plugins/*.cs.
//
//   node tools/realm-integration/check.mjs              check, print problems, exit 1 if any
//   node tools/realm-integration/check.mjs --commands   also print every chat command by plugin (Markdown)
//   node tools/realm-integration/check.mjs --json       print the whole analysis as JSON
//
// What it proves (source text only, nothing runs):
//   1. every [PluginReference] field names a plugin class that exists in plugins/;
//   2. every cross-plugin Call ("X.Call(\"M\", ...)" and the wrapper helpers the plugins use) names a
//      NON-PUBLIC instance method M in plugin X, with a matching number of arguments (Oxide 2.0.3867
//      registers only NonPublic|Instance methods as callable hooks);
//   3. every Chronicle event type a plugin passes as a literal is registered in all three places:
//      plugins/RealmChronicle.cs KnownTypes, chronicle/server.js EVENT_TYPES, chronicle/public/assets/common.js
//      TYPE_META; and no plugins/docs/*/EVENTS.json is left waiting for registration;
//   4. no two plugins register the same chat command, and none uses a command the game itself owns
//      (only the names this repo has evidence for, see GAME_COMMANDS).
// Argument TYPES are not compared (they need the compiler's view); the arity check catches most drift.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPO = join(HERE, '..', '..');

// Game-owned chat commands this repo has evidence for: docs/admin-console.md (CoreCommandHandler /kick, /ban,
// /unban, /banlist, /mute, /whitelist, /give, /notice, /popup, /list, /name, /logout, /quit, /restart, /shutdown)
// and attribute strings in the shipped patched Assembly-CSharp.dll (/mute, /suicide, /time, /videofly).
// The game's full command table lives in DLLs this repo does not have, so this list is NOT complete (UNVERIFIED).
export const GAME_COMMANDS = [
  'ban', 'banlist', 'give', 'kick', 'list', 'logout', 'mute', 'name', 'notice', 'popup', 'quit',
  'restart', 'shutdown', 'suicide', 'time', 'unban', 'videofly', 'whitelist',
];

const SNAKE = /^[a-z]+(?:_[a-z]+)*$/;

// Strip // and /* */ comments but keep string literals (and line count) intact.
export function stripComments(src) {
  let out = '';
  let i = 0;
  while (i < src.length) {
    const c = src[i], n = src[i + 1];
    if (c === '/' && n === '/') { while (i < src.length && src[i] !== '\n') i++; continue; }
    if (c === '/' && n === '*') {
      i += 2;
      while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) { if (src[i] === '\n') out += '\n'; i++; }
      i += 2; continue;
    }
    if (c === '@' && n === '"') {                       // verbatim string
      out += '@"'; i += 2;
      while (i < src.length) { if (src[i] === '"' && src[i + 1] === '"') { out += '""'; i += 2; continue; } if (src[i] === '"') break; out += src[i++]; }
      out += '"'; i++; continue;
    }
    if (c === '"' || c === "'") {
      const q = c; out += c; i++;
      while (i < src.length && src[i] !== q) { if (src[i] === '\\') { out += src[i++]; } out += src[i++]; }
      out += q; i++; continue;
    }
    out += c; i++;
  }
  return out;
}

// Given the index just after an opening '(', return { args: [raw strings], end } at top level.
export function splitArgs(src, start) {
  const args = [];
  let depth = 0, cur = '', i = start;
  for (; i < src.length; i++) {
    const c = src[i];
    if (c === '"' || c === "'") {
      const q = c; cur += c; i++;
      while (i < src.length && src[i] !== q) { if (src[i] === '\\') cur += src[i++]; cur += src[i]; i++; }
      cur += q; continue;
    }
    if (c === '(' || c === '[' || c === '{') depth++;
    if (c === ')' || c === ']' || c === '}') {
      if (depth === 0) break;
      depth--;
    }
    if (c === ',' && depth === 0) { args.push(cur.trim()); cur = ''; continue; }
    cur += c;
  }
  if (cur.trim()) args.push(cur.trim());
  return { args, end: i };
}

const lineAt = (src, idx) => src.slice(0, idx).split('\n').length;

// Parameters of a C# method declaration: returns { count, hasParams }.
function paramShape(paramText) {
  const t = paramText.trim();
  if (!t) return { count: 0, hasParams: false, optional: 0 };
  const { args } = splitArgs(t + ')', 0);
  return {
    count: args.length,
    hasParams: args.some((a) => /^params\s/.test(a)),
    optional: args.filter((a) => a.includes('=')).length,
  };
}

export function analysePlugin(file, raw) {
  const src = stripComments(raw);
  const name = basename(file, '.cs');
  const cls = (src.match(/class\s+(\w+)\s*:\s*(?:ReignOfKingsPlugin|CSharpPlugin|RustPlugin|CovalencePlugin)/) || [])[1] || null;

  // Methods with their visibility (instance methods only; static ones are not hooks).
  const methods = {};
  const declRe = /^[ \t]*((?:(?:private|protected|internal|public|static|override|virtual|new)[ \t]+)*)([\w<>\[\],. \t]+?)[ \t]+(\w+)[ \t]*\(([^)]*)\)\s*(?:\{|$)/gm;
  let m;
  while ((m = declRe.exec(src))) {
    const mods = m[1];
    const ret = m[2].trim();
    if (/^(return|new|else|if|while|for|foreach|switch|using|throw|case|lock)$/.test(ret) || /\b(return|new|throw)\b/.test(ret)) continue;
    const isPublic = /\bpublic\b/.test(mods);
    const isStatic = /\bstatic\b/.test(mods);
    (methods[m[3]] = methods[m[3]] || []).push({ isPublic, isStatic, line: lineAt(src, m.index), ...paramShape(m[4]) });
  }

  const refs = [];
  const refRe = /\[PluginReference(?:\("(\w+)"\))?\]\s*(?:private|protected|internal)?\s*Plugin\s+(\w+)\s*;/g;
  while ((m = refRe.exec(src))) refs.push({ field: m[2], target: m[1] || m[2], line: lineAt(src, m.index) });
  const refTarget = Object.fromEntries(refs.map((r) => [r.field, r.target]));

  // Wrapper helpers that forward a method name to a plugin reference:
  //   Foo(Plugin plugin, string method, params object[] args) { ... plugin.Call(method, args) }  -> plugin is arg 0
  //   Foo(string method, string house) { ... RealmHouses.Call(method, house) }                  -> fixed reference
  const wrappers = {};
  const wrapRe = /(?:private|protected|internal)\s+[\w<>\[\],\s]+?\s+(\w+)\s*\(([^)]*\bstring\s+method\b[^)]*)\)\s*\{/g;
  while ((m = wrapRe.exec(src))) {
    const body = src.slice(m.index, m.index + 600);
    const params = splitArgs(m[2] + ')', 0).args;
    const pluginParamIdx = params.findIndex((p) => /^Plugin\s+\w+$/.test(p));
    const methodIdx = params.findIndex((p) => /^string\s+method$/.test(p));
    let call = body.match(/(\w+)\.Call\(\s*method\s*(,[^;]*)?\)/);
    // Delegating wrapper: CallString(plugin, method, args) { CallSafe(plugin, method, args) }
    if (!call && pluginParamIdx >= 0) call = body.match(/(\w+)\(\s*\w+\s*,\s*method\s*,\s*args\s*\)/) && ['', 'plugin', ', args'];
    if (!call) continue;
    const fwd = call[2] ? splitArgs(call[2].slice(1) + ')', 0).args : [];
    const variadic = fwd.length === 1 && fwd[0] === 'args';
    wrappers[m[1]] = {
      pluginParamIdx, methodIdx,
      fixedRef: pluginParamIdx >= 0 ? null : call[1],
      forwarded: variadic ? null : fwd.length,
    };
  }

  const calls = [];
  const callRe = /\b(\w+)\.Call\s*(?:<[^>]+>)?\s*\(\s*"(\w+)"/g;
  while ((m = callRe.exec(src))) {
    const { args } = splitArgs(src, m.index + m[0].length);
    const rest = args.length && args[0] === '' ? args.slice(1) : args;
    calls.push({ ref: m[1], method: m[2], arity: rest.length, line: lineAt(src, m.index) });
  }
  for (const [w, info] of Object.entries(wrappers)) {
    const re = new RegExp('\\b' + w + '\\s*\\(', 'g');
    while ((m = re.exec(src))) {
      const { args } = splitArgs(src, m.index + m[0].length);
      const methodArg = args[info.methodIdx];
      if (!methodArg || !/^"\w+"$/.test(methodArg)) continue;           // the declaration itself, or a variable
      const ref = info.fixedRef || args[info.pluginParamIdx];
      const arity = info.forwarded != null ? info.forwarded : args.length - info.methodIdx - 1;
      calls.push({ ref, method: methodArg.slice(1, -1), arity, line: lineAt(src, m.index), via: w });
    }
  }
  for (const c of calls) c.target = refTarget[c.ref] || (c.ref === cls ? cls : null);

  // Chat commands.
  const commands = [];
  const cmdRe = /\[ChatCommand\("([^"]+)"\)\]/g;
  while ((m = cmdRe.exec(src))) commands.push({ name: m[1].toLowerCase(), line: lineAt(src, m.index) });
  const addRe = /AddChatCommand\(\s*"([^"]+)"/g;
  while ((m = addRe.exec(src))) commands.push({ name: m[1].toLowerCase(), line: lineAt(src, m.index) });
  // RealmCourt writes straight into the game's own command table; its labels are constants named *Label.
  const labelRe = /const\s+string\s+\w+Label\s*=\s*"([a-z.]+)"/g;
  while ((m = labelRe.exec(src))) commands.push({ name: m[1], line: lineAt(src, m.index), gameTable: true });

  // Chronicle event types passed as literals.
  const types = [];
  const push = (t, idx) => { if (SNAKE.test(t)) types.push({ type: t, line: lineAt(src, idx) }); };
  const chronRe = /\bChronicle\s*\(\s*"([a-z_]+)"\s*,\s*("([^"]*)")?/g;
  while ((m = chronRe.exec(src))) { push(m[1], m.index); if (m[3] && SNAKE.test(m[3]) && /\bChronicle\s*\(\s*string\s+type\s*,\s*string\s+fallback/.test(src)) push(m[3], m.index); }
  const logRe = /\.Call\s*\(\s*"Log"\s*,\s*"([a-z_]+)"/g;
  while ((m = logRe.exec(src))) push(m[1], m.index);
  const safeLogRe = /\(\s*RealmChronicle\s*,\s*"Log"\s*,\s*"([a-z_]+)"/g;
  while ((m = safeLogRe.exec(src))) push(m[1], m.index);
  const constRe = /const\s+string\s+\w*(?:EventType|ChronicleType)\w*\s*=\s*"([a-z_]+)"/g;
  while ((m = constRe.exec(src))) push(m[1], m.index);
  if (name === 'RealmChronicle') {
    const own = /\bLog\s*\(\s*"([a-z_]+)"/g;
    while ((m = own.exec(src))) push(m[1], m.index);
  }

  return { file, name, cls, methods, refs, calls, commands, types };
}

export function registeredTypes(repo = REPO) {
  const cs = readFileSync(join(repo, 'plugins/RealmChronicle.cs'), 'utf8');
  const block = (cs.match(/KnownTypes[^{]*\{([\s\S]*?)\};/) || [])[1] || '';
  const plugin = new Set([...stripComments(block).matchAll(/"([a-z_]+)"/g)].map((x) => x[1]));
  const sj = readFileSync(join(repo, 'chronicle/server.js'), 'utf8');
  const sblock = (sj.match(/EVENT_TYPES\s*=\s*new Set\(\[([\s\S]*?)\]\)/) || [])[1] || '';
  const server = new Set([...sblock.replace(/\/\/.*$/gm, '').matchAll(/'([a-z_]+)'/g)].map((x) => x[1]));
  const cj = readFileSync(join(repo, 'chronicle/public/assets/common.js'), 'utf8');
  const cblock = (cj.match(/TYPE_META\s*=\s*\{([\s\S]*?)\n\};/) || [])[1] || '';
  const page = new Set([...cblock.matchAll(/^\s*([a-z_]+)\s*:/gm)].map((x) => x[1]));
  return { plugin, server, page };
}

export function pendingEventFiles(repo = REPO) {
  const out = [];
  const walk = (dir, depth) => {
    if (depth > 4 || !existsSync(dir)) return;
    for (const e of readdirSync(dir, { withFileTypes: true })) {
      if (e.name === 'node_modules' || e.name.startsWith('.')) continue;
      const p = join(dir, e.name);
      if (e.isDirectory()) walk(p, depth + 1);
      else if (e.name === 'EVENTS.json') out.push(p.slice(repo.length + 1));
    }
  };
  for (const d of ['plugins', 'bot', 'analytics', 'streamkit', 'portal', 'tools', 'ops', 'mods']) walk(join(repo, d), 0);
  return out;
}

export function analyse(repo = REPO) {
  const dir = join(repo, 'plugins');
  const plugins = readdirSync(dir).filter((f) => f.endsWith('.cs')).sort()
    .map((f) => analysePlugin(join('plugins', f), readFileSync(join(dir, f), 'utf8')));
  const byClass = Object.fromEntries(plugins.filter((p) => p.cls).map((p) => [p.cls, p]));
  const problems = [];
  const P = (file, line, msg) => problems.push(`${file}:${line}: ${msg}`);

  for (const p of plugins) {
    if (p.cls && p.cls !== p.name) P(p.file, 1, `class ${p.cls} does not match the file name (Oxide loads by file name)`);
    for (const r of p.refs) if (!byClass[r.target]) P(p.file, r.line, `[PluginReference] ${r.field} names no plugin in plugins/`);
    for (const c of p.calls) {
      if (!c.target) continue;                                   // not a plugin reference (e.g. a local helper)
      const t = byClass[c.target];
      if (!t) { P(p.file, c.line, `${c.ref}.Call("${c.method}") targets missing plugin ${c.target}`); continue; }
      const decls = (t.methods[c.method] || []).filter((d) => !d.isStatic);
      if (!decls.length) { P(p.file, c.line, `${c.target} has no method ${c.method} (Call returns null)`); continue; }
      const callable = decls.filter((d) => !d.isPublic);
      if (!callable.length) { P(p.file, c.line, `${c.target}.${c.method} is public; Oxide only calls non-public methods`); continue; }
      const ok = callable.some((d) => (d.hasParams ? c.arity >= d.count - 1 : c.arity <= d.count && c.arity >= d.count - d.optional));
      if (!ok) P(p.file, c.line, `${c.target}.${c.method} takes ${callable.map((d) => d.count).join('/')} argument(s), called with ${c.arity}`);
    }
  }

  const reg = registeredTypes(repo);
  const all = new Set([...reg.plugin, ...reg.server, ...reg.page]);
  for (const t of all) {
    const missing = ['plugin', 'server', 'page'].filter((k) => !reg[k].has(t));
    if (missing.length) P('plugins/RealmChronicle.cs', 1, `event type ${t} is missing from: ${missing.join(', ')}`);
  }
  for (const p of plugins) for (const t of p.types) if (!reg.plugin.has(t.type)) P(p.file, t.line, `Chronicle type "${t.type}" is not registered`);
  for (const f of pendingEventFiles(repo)) P(f, 1, 'EVENTS.json still waiting: register its types in the three Chronicle lists, then delete it');

  const owners = {};
  for (const p of plugins) for (const c of p.commands) (owners[c.name] = owners[c.name] || []).push(`${p.file}:${c.line}`);
  for (const [name, where] of Object.entries(owners)) {
    if (where.length > 1) P(where[1].split(':')[0], +where[1].split(':')[1], `chat command /${name} is also registered at ${where[0]}`);
    if (GAME_COMMANDS.includes(name)) P(where[0].split(':')[0], +where[0].split(':')[1], `chat command /${name} is a game command`);
  }

  return { plugins, registered: { plugin: [...reg.plugin], server: [...reg.server], page: [...reg.page] }, commands: owners, problems };
}

export function commandsMarkdown(result) {
  const rows = [];
  for (const p of result.plugins) for (const c of p.commands) rows.push(`| \`/${c.name}\`${c.gameTable ? ' (game command table, console only)' : ''} | ${p.name} | \`${p.file}:${c.line}\` |`);
  return ['| Command | Plugin | Declared at |', '|---|---|---|', ...rows].join('\n');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const result = analyse();
  if (process.argv.includes('--json')) {
    console.log(JSON.stringify({ ...result, plugins: result.plugins.map(({ methods, ...rest }) => rest) }, null, 2));
  } else {
    const calls = result.plugins.reduce((n, p) => n + p.calls.filter((c) => c.target).length, 0);
    const cmds = Object.keys(result.commands).length;
    if (process.argv.includes('--commands')) console.log(commandsMarkdown(result) + '\n');
    for (const msg of result.problems) console.log('PROBLEM ' + msg);
    console.log(`${result.plugins.length} plugins, ${calls} cross-plugin calls, ${cmds} chat commands, `
      + `${result.registered.plugin.length} Chronicle types: ${result.problems.length ? result.problems.length + ' problem(s)' : 'OK'}`);
  }
  process.exit(result.problems.length ? 1 : 0);
}
