#!/usr/bin/env node
// Realm integration check: static cross-plugin wiring and Chronicle sync for plugins/*.cs.
//
//   node tools/realm-integration/check.mjs              check, print problems, exit 1 if any
//   node tools/realm-integration/check.mjs --commands   also print every chat command by plugin (Markdown)
//   node tools/realm-integration/check.mjs --json       print the whole analysis as JSON
//
// What it proves (source text only, nothing runs):
//   1. every [PluginReference] field names a plugin class that exists in plugins/ (or one another team is still
//      building, listed with its agreed methods in PENDING_PLUGINS);
//   2. every cross-plugin Call ("X.Call(\"M\", ...)" and the wrapper helpers the plugins use) names a
//      NON-PUBLIC instance method M in plugin X, with a matching number of arguments (Oxide 2.0.3867
//      registers only NonPublic|Instance methods as callable hooks);
//   3. every Chronicle event type a plugin passes as a literal is registered in all three places:
//      plugins/RealmChronicle.cs KnownTypes, chronicle/server.js EVENT_TYPES, chronicle/public/assets/common.js
//      TYPE_META; and no plugins/docs/*/EVENTS.json is left waiting for registration;
//   4. no two plugins register the same chat command, and none uses a command the game itself owns
//      (only the names this repo has evidence for, see GAME_COMMANDS).
//   5. chat style (docs/realm-commands.md): every plugin that registers lang messages has the chat style block with
//      the palette's tone colours, a "Speaker" key and the one Herald voice; every colour tag in a string is in the
//      chat palette (house tints read from art/palette.json); no lang string is over 200 visible characters; every
//      /command a lang string mentions is registered by some plugin and drawn in the command colour;
//   6. RealmHerald's /realm catalogue lists every chat command exactly once, under the plugin that registers it,
//      with a "Cmd.<command>" description (staff-only commands, STAFF_COMMANDS, are left out of the player hub).
//   7. popups (docs/realm-commands.md, "Popups"): the game's ShowPopup / ShowConfirmPopup / ShowInputPopup are called
//      only inside a plugin's "Popups" region, inside try, with every argument spelled out and broadcast = true; the
//      plugin has a UsePopups config switch and a PopupsFor gate, and one that waits for answers ignores them after
//      Unload (popupsClosed = true).
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

// Plugins another team is building that a plugin here already calls. While plugins/<Name>.cs does not exist, a
// [PluginReference] to it is allowed (the caller must treat null as "not available") and each call must match the
// method and argument count agreed here. Once the file lands, the normal checks apply and its entry goes.
export const PENDING_PLUGINS = {};

// Staff-only chat commands: the plugin serves only holders of its admin permission (others get one pointer line), so
// the player hub (/realm) does not list them. Each plugin guide documents its own.
export const STAFF_COMMANDS = ['sentinel', 'paint', 'ironbreaker'];

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

// ---- Chat style (docs/realm-commands.md, "Chat style") ----------------------------------------------------------
// The colours every Realm chat line may use. Tone colours open a reply's speaker; the command colour marks a /command;
// muted is for timestamps and quiet voices (tips, rumours); house tints come from art/palette.json "discordRole".
export const CHAT_PALETTE = {
  gold: 'D6A043', ok: '8FC97A', warn: 'E8913A', error: 'E86A5C', command: 'F4C96D', muted: 'A3A6AD', text: 'FFFFFF',
};
export const HOUSE_ORDER = ['varrow', 'ashgrove', 'corvane', 'dunmere', 'halloran', 'merrin'];
export const MAX_CHAT_LINE = 200;                       // visible characters of one lang string
const CHAT_EXEMPT = new Set(['RealmCourt']);           // speaks a console protocol to Realm Steward, not to players
const TAG = /\[([0-9A-Fa-f]{6})\]/g;

export function houseTints(repo = REPO) {
  const f = join(repo, 'art/palette.json');
  if (!existsSync(f)) return null;
  const pal = JSON.parse(readFileSync(f, 'utf8'));
  return HOUSE_ORDER.map((h) => ((pal.houses[h] || {}).discordRole || '').replace('#', '').toUpperCase());
}

const unescape = (s) => s.replace(/\\(.)/g, '$1');
export const visible = (s) => unescape(s).replace(TAG, '').replace(/\[-\]/g, '');

// Lang strings: { "Key", "value" }, m["Key"] = "value" and m.Add("Key", "value"), with + concatenation.
export function langStrings(src) {
  const out = [];
  const re = /(?:\{\s*"([A-Za-z0-9_.]+)",\s*|m\["([A-Za-z0-9_.]+)"\]\s*=\s*|m\.Add\("([A-Za-z0-9_.]+)",\s*)("(?:[^"\\]|\\.)*"(?:\s*\+\s*"(?:[^"\\]|\\.)*")*)/g;
  if (!/lang\.RegisterMessages/.test(src)) return out;
  let m;
  while ((m = re.exec(src))) {
    const value = [...m[4].matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((x) => x[1]).join('');
    out.push({ key: m[1] || m[2] || m[3], value, line: lineAt(src, m.index) });
  }
  return out;
}

// Every "/word" mention in a text that is not part of a path ("oxide/data").
export function commandMentions(text) {
  const out = [];
  const re = /(^|[^\w/])\/([a-z]+)/g;
  let m;
  while ((m = re.exec(text))) out.push({ name: m[2], index: m.index + m[1].length });
  return out;
}

export function chatStyleProblems(p, raw, ctx) {
  const problems = [];
  if (CHAT_EXEMPT.has(p.name)) return problems;
  const src = stripComments(raw);
  const strings = langStrings(src);
  if (!strings.length) return problems;
  const P = (line, msg) => problems.push({ line, msg });
  const allowed = new Set([...Object.values(CHAT_PALETTE), ...(ctx.tints || [])]);

  if (!/#region Chat style/.test(src)) P(1, 'has lang messages but no "Chat style" block (copy it from any Realm plugin)');
  for (const [name, key] of [['ChatGold', 'gold'], ['ChatOk', 'ok'], ['ChatWarn', 'warn'], ['ChatError', 'error']]) {
    const c = src.match(new RegExp(`const\\s+string\\s+${name}\\s*=\\s*"([0-9A-Fa-f]{6})"`));
    if (!c) P(1, `chat style constant ${name} is missing`);
    else if (c[1].toUpperCase() !== CHAT_PALETTE[key]) P(lineAt(src, c.index), `${name} is ${c[1]}, the chat palette says ${CHAT_PALETTE[key]}`);
  }
  if (/HouseTintColours/.test(src)) {
    const names = (src.match(/HouseTintNames\s*=\s*\{([^}]*)\}/) || [])[1] || '';
    const cols = (src.match(/HouseTintColours\s*=\s*\{([^}]*)\}/) || [])[1] || '';
    const n = [...names.matchAll(/"([a-z]+)"/g)].map((x) => x[1]);
    const c = [...cols.matchAll(/"([0-9A-Fa-f]{6})"/g)].map((x) => x[1].toUpperCase());
    if (n.join() !== HOUSE_ORDER.join()) P(1, `HouseTintNames must list ${HOUSE_ORDER.join(', ')}`);
    if (ctx.tints && c.join() !== ctx.tints.join()) P(1, `HouseTintColours ${c.join(' ')} differ from art/palette.json discordRole ${ctx.tints.join(' ')}`);
  }
  // Every colour tag in any string literal of the plugin.
  for (const lit of src.matchAll(/"((?:[^"\\]|\\.)*)"/g)) {
    for (const t of lit[1].matchAll(TAG)) {
      if (!allowed.has(t[1].toUpperCase())) P(lineAt(src, lit.index), `colour [${t[1]}] is not in the chat palette`);
    }
  }
  const keys = new Set(strings.map((s) => s.key));
  if (!keys.has('Speaker')) P(1, 'lang has no "Speaker" key (the name that opens its replies)');
  for (const s of strings) {
    if (s.key === 'Herald' && s.value !== `[${CHAT_PALETTE.gold}]Herald[FFFFFF]: `) P(s.line, `"Herald" must be "[${CHAT_PALETTE.gold}]Herald[FFFFFF]: "`);
    const v = visible(s.value);
    if (v.length > MAX_CHAT_LINE) P(s.line, `"${s.key}" is ${v.length} characters; split it (chat style: at most ${MAX_CHAT_LINE})`);
    for (const c of commandMentions(s.value)) {
      if (!ctx.commands.has(c.name)) { P(s.line, `"${s.key}" mentions /${c.name}, which no plugin registers`); continue; }
      if (!s.value.slice(0, c.index).endsWith(`[${CHAT_PALETTE.command}]`)) P(s.line, `"${s.key}" mentions /${c.name} without the command colour [${CHAT_PALETTE.command}]`);
    }
  }
  return problems;
}

// ---- Popups (docs/realm-commands.md, "Popups") ------------------------------------------------------------------
// The game's popup windows, with the full parameter lists read from the 2.0.3867 Assembly-CSharp.dll metadata
// (CodeHatch.Common.PlayerExtensions). The last parameter, broadcast, must be true or a dedicated server never sends
// the window to the client.
export const POPUP_METHODS = {
  ShowPopup: ['title', 'message', 'buttonText', 'handler', 'interupt', 'broadcast'],
  ShowConfirmPopup: ['title', 'message', 'confirmText', 'cancelText', 'handler', 'interupt', 'broadcast'],
  ShowInputPopup: ['title', 'message', 'initialInput', 'confirmText', 'cancelText', 'handler', 'interupt', 'broadcast'],
};

export function popupProblems(p, raw) {
  const problems = [];
  const src = stripComments(raw);
  const P = (line, msg) => problems.push({ line, msg });
  const region = src.match(/#region Popups\b[\s\S]*?#endregion/);
  const start = region ? region.index : -1;
  const end = region ? start + region[0].length : -1;
  let calls = 0;
  let handlers = 0;
  const re = /\.(ShowPopup|ShowConfirmPopup|ShowInputPopup)\s*\(/g;
  let m;
  while ((m = re.exec(src))) {
    calls++;
    const line = lineAt(src, m.index);
    if (m.index < start || m.index > end) P(line, `${m[1]} outside the "Popups" region (every window goes through its helpers and chat fallback)`);
    const { args } = splitArgs(src, m.index + m[0].length);
    const want = POPUP_METHODS[m[1]];
    if (args.length !== want.length) { P(line, `${m[1]} needs all ${want.length} arguments spelled out (${want.join(', ')}); it has ${args.length}`); continue; }
    if (args[want.length - 1] !== 'true') P(line, `${m[1]}: broadcast must be true, or a dedicated server never sends the window`);
    if (args[want.indexOf('handler')] !== 'null') handlers++;
    const before = src.slice(Math.max(0, m.index - 400), m.index);
    if (before.lastIndexOf('try') < 0 || before.lastIndexOf('try') < before.lastIndexOf('catch')) P(line, `${m[1]} is not inside try: a failed window must fall back to chat`);
  }
  if (!calls) return problems;
  if (!/\bbool\s+UsePopups\s*=/.test(src)) P(1, 'opens popups but its config has no UsePopups switch');
  if (!/\bPopupsFor\s*\(/.test(src)) P(1, 'opens popups without a PopupsFor(player) gate');
  if (handlers) {
    const unload = (src.match(/void\s+Unload\s*\(\s*\)\s*\{([\s\S]*?)\n\s*\}/) || [])[1] || '';
    if (!/popupsClosed\s*=\s*true/.test(unload)) P(1, 'waits for popup answers but Unload does not set popupsClosed = true');
  }
  return problems;
}

// RealmHerald's /realm hub must list every chat command once, under the plugin that registers it, with a description.
export function catalogueProblems(raw, owners) {
  const problems = [];
  const src = stripComments(raw);
  const entries = [...src.matchAll(/new Entry\("([a-z]+)",\s*"([a-z]+)",\s*"(\w+)"\)/g)].map((m) => ({ cmd: m[1], subject: m[2], plugin: m[3], line: lineAt(src, m.index) }));
  const subjects = [...(((src.match(/Subjects\s*=\s*\{([^}]*)\}/) || [])[1] || '').matchAll(/"([a-z]+)"/g))].map((m) => m[1]);
  const keys = new Set(langStrings(src).map((s) => s.key));
  const seen = new Set();
  for (const e of entries) {
    if (seen.has(e.cmd)) problems.push({ line: e.line, msg: `/realm catalogue lists /${e.cmd} twice` });
    seen.add(e.cmd);
    const owner = owners[e.cmd];
    if (!owner) problems.push({ line: e.line, msg: `/realm catalogue lists /${e.cmd}, which no plugin registers` });
    else if (owner !== e.plugin) problems.push({ line: e.line, msg: `/realm catalogue says /${e.cmd} belongs to ${e.plugin}; ${owner} registers it` });
    if (!subjects.includes(e.subject)) problems.push({ line: e.line, msg: `/realm catalogue puts /${e.cmd} under unknown subject "${e.subject}"` });
    if (!keys.has('Cmd.' + e.cmd)) problems.push({ line: e.line, msg: `lang key "Cmd.${e.cmd}" (its one-line description) is missing` });
  }
  for (const s of subjects) if (!keys.has('Subject.' + s)) problems.push({ line: 1, msg: `lang key "Subject.${s}" is missing` });
  for (const cmd of Object.keys(owners)) if (!seen.has(cmd) && !STAFF_COMMANDS.includes(cmd)) problems.push({ line: 1, msg: `/${cmd} (${owners[cmd]}) is missing from the /realm catalogue` });
  return { problems, entries: entries.length };
}

export function analyse(repo = REPO, pending = PENDING_PLUGINS) {
  const dir = join(repo, 'plugins');
  const plugins = readdirSync(dir).filter((f) => f.endsWith('.cs')).sort()
    .map((f) => analysePlugin(join('plugins', f), readFileSync(join(dir, f), 'utf8')));
  const byClass = Object.fromEntries(plugins.filter((p) => p.cls).map((p) => [p.cls, p]));
  const problems = [];
  const P = (file, line, msg) => problems.push(`${file}:${line}: ${msg}`);

  for (const p of plugins) {
    if (p.cls && p.cls !== p.name) P(p.file, 1, `class ${p.cls} does not match the file name (Oxide loads by file name)`);
    for (const r of p.refs) if (!byClass[r.target] && !pending[r.target]) P(p.file, r.line, `[PluginReference] ${r.field} names no plugin in plugins/`);
    for (const c of p.calls) {
      if (!c.target) continue;                                   // not a plugin reference (e.g. a local helper)
      const t = byClass[c.target];
      const pendingCalls = !t && pending[c.target];
      if (pendingCalls) {
        if (!(c.method in pendingCalls)) P(p.file, c.line, `${c.target} is not built yet and ${c.method} is not among its agreed methods (PENDING_PLUGINS)`);
        else if (pendingCalls[c.method] !== c.arity) P(p.file, c.line, `${c.target}.${c.method} is agreed with ${pendingCalls[c.method]} argument(s), called with ${c.arity}`);
        continue;
      }
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

  // Chat style in every plugin that talks to players, and the /realm catalogue in RealmHerald.
  const ownerOf = {};
  for (const p of plugins) for (const c of p.commands) if (!c.gameTable) ownerOf[c.name] = p.name;
  const ctx = { commands: new Set(Object.keys(ownerOf)), tints: houseTints(repo) };
  let langCount = 0;
  const popupPlugins = [];
  for (const p of plugins) {
    const raw = readFileSync(join(repo, p.file), 'utf8');
    langCount += langStrings(stripComments(raw)).length;
    for (const q of chatStyleProblems(p, raw, ctx)) P(p.file, q.line, q.msg);
    for (const q of popupProblems(p, raw)) P(p.file, q.line, q.msg);
    if (/\.(ShowPopup|ShowConfirmPopup|ShowInputPopup)\s*\(/.test(stripComments(raw))) popupPlugins.push(p.name);
  }
  let catalogue = null;
  const herald = plugins.find((p) => p.name === 'RealmHerald');
  if (herald) {
    const c = catalogueProblems(readFileSync(join(repo, herald.file), 'utf8'), ownerOf);
    catalogue = c.entries;
    for (const q of c.problems) P(herald.file, q.line, q.msg);
  }

  return { plugins, registered: { plugin: [...reg.plugin], server: [...reg.server], page: [...reg.page] }, commands: owners,
    chat: { langStrings: langCount, catalogue, popupPlugins }, problems };
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
      + `${result.registered.plugin.length} Chronicle types, ${result.chat.langStrings} chat lines`
      + `${result.chat.catalogue != null ? `, /realm lists ${result.chat.catalogue} commands` : ''}`
      + `${result.chat.popupPlugins.length ? `, popups in ${result.chat.popupPlugins.join(' ')}` : ''}: `
      + `${result.problems.length ? result.problems.length + ' problem(s)' : 'OK'}`);
  }
  process.exit(result.problems.length ? 1 : 0);
}
