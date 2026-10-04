#!/usr/bin/env node
// check-saga: proves that the saga pack in docs/saga/ only uses commands, ids, chronicle types and schedule
// slots that exist in the repo, and that every herald line can be pasted into the game as written.
//
//   node docs/saga/tools/check-saga.mjs            check everything (exit 1 on any error)
//   node docs/saga/tools/check-saga.mjs --write    also regenerate docs/saga/COMMANDS.md from the sources
//   node docs/saga/tools/check-saga.mjs --json     print the full report as JSON
//
// Sources it reads (never writes): plugins/*.cs, docs/admin-console.md (the game's own console commands),
// docs/oxide-rok-api.md (Oxide console commands), docs/saga/EVENTS.json (optional extra chronicle types).
// It only parses text; it does not run the game, so "exists in the source" is the strongest claim it makes.

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Plugins that another team is building in this same run. Their syntax may still move; the checker catches drift.
export const THIS_RUN = ['RealmDynasties', 'RealmLaws', 'RealmRavens', 'RealmTreasury', 'RealmRenown', 'RealmWarden',
  'RealmSeasons', 'RealmEvents'];

// Steward limits for text sent through the admin console (launcher/lib/moderation.js: MAX_MESSAGE = 300, and
// argText turns ' and " into a backtick for /notice and /popup). Herald lines must survive all three channels.
export const INGAME_MAX = 300;
export const DISCORD_MAX = 2000;
export const PLACEHOLDER_WIDTH = 24;            // a filled-in {placeholder} is assumed to be at most this long
export const PLACEHOLDERS = ['monarch', 'house', 'claimant', 'crown_house', 'champion', 'quarry', 'line', 'outlaw',
  'day', 'time', 'location', 'n'];

export const EXPECTED = { proclamations: 30, locations: 13, legends: 10, weeks: 8, acts: 4 };

const DAY_NAMES = { mon: 'Monday', tue: 'Tuesday', wed: 'Wednesday', thu: 'Thursday', fri: 'Friday', sat: 'Saturday',
  sun: 'Sunday' };

// ---------------------------------------------------------------------------------------------------------------
// Source model

export function parseChatCommands(files) {
  // files: { 'RealmHouses.cs': text, ... } -> Map(command -> { plugin, file, text })
  const map = new Map();
  for (const [file, text] of Object.entries(files)) {
    const plugin = file.replace(/\.cs$/, '');
    const re = /\[ChatCommand\("([^"]+)"\)\]/g;
    let m;
    while ((m = re.exec(text))) {
      const name = m[1].toLowerCase();
      if (!map.has(name)) map.set(name, { plugin, file, text });
    }
  }
  return map;
}

export function parseGameCommands(adminConsoleMd) {
  // The table "Commands the Court uses" names the game's own console commands as `/name ...`.
  const out = new Set();
  const start = adminConsoleMd.indexOf('## Commands the Court uses');
  const end = adminConsoleMd.indexOf('### The RealmCourt plugin');
  const section = start >= 0 ? adminConsoleMd.slice(start, end > start ? end : undefined) : '';
  for (const line of section.split('\n')) {
    if (!line.startsWith('|')) continue;
    const cells = line.split('|');
    const consoleCell = cells[2] || '';
    const re = /`\/([a-z][a-z.]*)/g;
    let m;
    while ((m = re.exec(consoleCell))) out.add(m[1]);
  }
  return out;
}

// RealmCourt writes console commands straight into the game's command table (private const string ...Label = "realm.x").
export function parseCourtCommands(courtSrc) {
  const out = new Set();
  const re = /private const string [A-Za-z]*Label = "([a-z][a-z.]*)";/g;
  let m;
  while ((m = re.exec(courtSrc))) out.add(m[1]);
  return out;
}

export function parseOxideCommands(oxideApiMd) {
  const out = new Set();
  const re = /\boxide\.([a-z]+)\b/g;
  let m;
  while ((m = re.exec(oxideApiMd))) out.add('oxide.' + m[1]);
  return out;
}

export function parseDecreeIds(crownSrc) {
  const ids = new Set();
  const re = /new DecreeDef \{ Id = "([a-z_]+)"/g;
  let m;
  while ((m = re.exec(crownSrc))) ids.add(m[1]);
  return ids;
}

export function parseLawIds(lawsSrc) {
  const ids = new Set();
  const re = /new LawDef \{ Id = "([a-z_]+)"/g;
  let m;
  while ((m = re.exec(lawsSrc))) ids.add(m[1]);
  return ids;
}

export function parseEventKinds(eventsSrc) {
  const kinds = new Map();               // const name -> kind
  const re = /private const string (K[A-Za-z]+) = "([a-z_]+)";/g;
  let m;
  while ((m = re.exec(eventsSrc))) kinds.set(m[1], m[2]);
  return kinds;
}

export function parseTitles(renownSrc) {
  const titles = [];
  const re = /T\("([a-z_]+)", "([^"]+)", "([^"]*)", (true|false), "([a-z_]+)", (\d+)\)/g;
  let m;
  while ((m = re.exec(renownSrc))) {
    titles.push({ id: m[1], name: m[2], description: m[3], infamous: m[4] === 'true', requires: m[5], min: +m[6] });
  }
  return titles;
}

export function parseChronicleTypes(chronicleSrc) {
  const start = chronicleSrc.indexOf('KnownTypes');
  if (start < 0) return new Set();
  const open = chronicleSrc.indexOf('{', start);
  const close = chronicleSrc.indexOf('};', open);
  const body = chronicleSrc.slice(open, close).replace(/\/\/[^\n]*/g, '');
  const out = new Set();
  const re = /"([a-z_]+)"/g;
  let m;
  while ((m = re.exec(body))) out.add(m[1]);
  return out;
}

export function parseIntConst(src, field) {
  const m = new RegExp('public int ' + field + ' = (\\d+);').exec(src);
  return m ? +m[1] : null;
}

function hhmmToMinutes(s) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(s);
  return m ? +m[1] * 60 + +m[2] : NaN;
}

function fullDay(d) {
  const k = String(d).slice(0, 3).toLowerCase();
  return DAY_NAMES[k] || d;
}

// The default weekly rhythm, read from the plugins' shipped defaults (a server's config can override them).
export function parseSchedule(sources) {
  const slots = [];
  const crown = sources['CrownAndConsequences.cs'] || '';
  let m;
  const rw = /new RebellionWindow \{ Day = "([A-Za-z]+)", Start = "(\d{1,2}:\d{2})", DurationMinutes = (\d+) \}/g;
  while ((m = rw.exec(crown))) slots.push({ slot: 'Rebellion window', day: fullDay(m[1]), start: m[2], minutes: +m[3], source: 'CrownAndConsequences' });

  const events = sources['RealmEvents.cs'] || '';
  const kinds = parseEventKinds(events);
  const names = { crown_night: 'Crown Night', tournament: 'Royal Tournament', kings_hunt: "King's Hunt", truce: 'Truce of the Realm' };
  const se = /new ScheduleEntry \{[^}]*?Event = (K[A-Za-z]+), Days = new List<string> \{([^}]*)\}, StartUtc = "(\d{1,2}:\d{2})", DurationMinutes = (\d+) \}/g;
  while ((m = se.exec(events))) {
    const kind = kinds.get(m[1]) || m[1];
    for (const d of m[2].match(/"([A-Za-z]+)"/g) || []) {
      slots.push({ slot: names[kind] || kind, day: fullDay(d.replace(/"/g, '')), start: m[3], minutes: +m[4], source: 'RealmEvents' });
    }
  }

  const warden = sources['RealmWarden.cs'] || '';
  const rd = /new RaidWindowDef \{ Days = new List<string> \{([^}]*)\}, Start = "(\d{1,2}:\d{2})", End = "(\d{1,2}:\d{2})" \}/g;
  while ((m = rd.exec(warden))) {
    const minutes = hhmmToMinutes(m[3]) - hhmmToMinutes(m[2]);
    for (const d of m[1].match(/"([A-Za-z]+)"/g) || []) {
      slots.push({ slot: 'Raid hours', day: fullDay(d.replace(/"/g, '')), start: m[2], minutes, source: 'RealmWarden' });
    }
  }
  return slots;
}

export function loadModel(root) {
  const pluginDir = path.join(root, 'plugins');
  const sources = {};
  for (const f of fs.readdirSync(pluginDir)) if (f.endsWith('.cs')) sources[f] = fs.readFileSync(path.join(pluginDir, f), 'utf8');
  const read = (p) => (fs.existsSync(path.join(root, p)) ? fs.readFileSync(path.join(root, p), 'utf8') : '');

  let tracked = null;
  try {
    tracked = new Set(execFileSync('git', ['ls-files', 'plugins'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
      .split('\n').filter(Boolean).map((p) => path.basename(p)));
  } catch (e) {
    tracked = null;
  }

  const chronicleTypes = parseChronicleTypes(sources['RealmChronicle.cs'] || '');
  const extraTypes = new Set();
  const eventsJson = path.join(root, 'docs', 'saga', 'EVENTS.json');
  if (fs.existsSync(eventsJson)) for (const e of JSON.parse(fs.readFileSync(eventsJson, 'utf8'))) extraTypes.add(e.type);

  return {
    sources,
    tracked,
    chat: parseChatCommands(sources),
    game: parseGameCommands(read('docs/admin-console.md')),
    court: parseCourtCommands(sources['RealmCourt.cs'] || ''),
    oxide: parseOxideCommands(read('docs/oxide-rok-api.md')),
    decrees: parseDecreeIds(sources['CrownAndConsequences.cs'] || ''),
    laws: parseLawIds(sources['RealmLaws.cs'] || ''),
    eventKinds: new Set(parseEventKinds(sources['RealmEvents.cs'] || '').values()),
    titles: parseTitles(sources['RealmRenown.cs'] || ''),
    chronicleTypes,
    extraTypes,
    seasonMaxDays: 365,
    eventMaxMinutes: parseIntConst(sources['RealmEvents.cs'] || '', 'MaxManualMinutes') || 360,
    ransomMax: (/RansomMaxAmount = (\d+),/.exec(sources['CrownAndConsequences.cs'] || '') || [])[1],
    schedule: parseSchedule(sources)
  };
}

export function pluginStatus(model, plugin) {
  if (THIS_RUN.indexOf(plugin) >= 0) return 'being built this run';
  if (model.tracked && model.tracked.has(plugin + '.cs')) return 'committed';
  if (model.tracked) return 'uncommitted in working tree';
  return 'unknown (no git)';
}

// ---------------------------------------------------------------------------------------------------------------
// Markdown scanning

// Splits a command line into tokens, keeping "quoted words" and <placeholders with spaces> together.
export function tokenize(s) {
  const out = [];
  const re = /"[^"]*"|<[^>]*>|\[[^\]]*\]|\{[^}]*\}|\S+/g;
  let m;
  while ((m = re.exec(s))) out.push(m[0]);
  return out;
}

const isLiteralWord = (t) => /^[a-z][a-z0-9_]*$/.test(t);

// Returns every command mention in a markdown text: { cmd, args, line, raw, ctx } where ctx is 'code' for code spans and
// code blocks, or 'herald' for in-game herald blocks (commands mentioned in prose there are checked too).
export function extractCommands(md) {
  const found = [];
  const lines = md.split('\n');
  let fence = null;               // info string of the open fence, or null
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const f = /^\s*```(\S*)/.exec(line);
    if (f) {
      fence = fence === null ? f[1] || 'code' : null;
      continue;
    }
    if (fence !== null) {
      if (fence === 'discord') continue;     // Discord text is checked for mentions only, not as commands
      if (fence === 'ingame') {
        const re = /(^|\s)(\/[a-z]+(?:\.[a-z]+)*(?:\s+[a-z][a-z0-9_]*)?)/g;
        let m;
        while ((m = re.exec(line))) addMention(found, m[2], i + 1, 'herald');
        continue;
      }
      const t = line.trim();
      if (t.startsWith('/') || /^oxide\.[a-z]+/.test(t)) addMention(found, t.replace(/\s+#.*$/, ''), i + 1, 'code');
      continue;
    }
    const re = /`([^`]+)`/g;
    let m;
    while ((m = re.exec(line))) {
      const span = m[1].trim();
      if (/^\/[a-z]/.test(span) || /^oxide\.[a-z]+/.test(span)) addMention(found, span, i + 1, 'code');
    }
  }
  return found;
}

function addMention(found, raw, line, ctx) {
  // A span may show alternatives "a | b"; each alternative that starts with / is its own mention.
  const parts = raw.split(/\s+\|\s+/);
  let head = null;
  for (const p of parts) {
    const s = p.trim();
    if (/^\/[a-z]/.test(s) || /^oxide\.[a-z]+/.test(s)) {
      const toks = tokenize(s);
      const cmd = toks[0].replace(/^\//, '').toLowerCase();
      head = cmd;
      found.push({ cmd, args: toks.slice(1), line, raw: s, ctx, oxide: s.startsWith('oxide.') });
    } else if (head && isLiteralWord(tokenize(s)[0] || '')) {
      // "/x a | b" means "/x a" and "/x b"
      found.push({ cmd: head, args: tokenize(s), line, raw: '/' + head + ' ' + s, ctx, oxide: false });
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Checks

export function checkMention(model, m) {
  const errors = [];
  if (m.oxide) {
    if (!model.oxide.has(m.cmd)) errors.push('unknown Oxide console command ' + m.cmd + ' (not in docs/oxide-rok-api.md)');
    return { errors, owner: 'Oxide console' };
  }
  const chat = model.chat.get(m.cmd);
  if (!chat) {
    if (model.court && model.court.has(m.cmd)) return { errors, owner: 'RealmCourt' };
    if (model.game.has(m.cmd)) return { errors, owner: 'game console' };
    errors.push('unknown command /' + m.cmd + ' (no [ChatCommand] in plugins/*.cs and not a game console command in docs/admin-console.md)');
    return { errors, owner: null };
  }
  const a = m.args;
  const sub = a[0];
  if (sub && isLiteralWord(sub) && chat.text.indexOf('"' + sub + '"') < 0) {
    errors.push('/' + m.cmd + ' has no subcommand "' + sub + '" in ' + chat.file);
  } else if (sub && isLiteralWord(sub) && a[1] && isLiteralWord(a[1]) && chat.text.indexOf('"' + a[1] + '"') < 0) {
    // A second literal word (/raven admin approve, /law zone set, /renown top infamy) must be a literal too.
    errors.push('/' + m.cmd + ' ' + sub + ' has no option "' + a[1] + '" in ' + chat.file);
  }
  // Id checks for commands whose next word must name something in a catalogue.
  const lit = (t) => t && isLiteralWord(t);
  if (m.cmd === 'decree' && lit(sub) && !model.decrees.has(sub)) errors.push('unknown decree id "' + sub + '"');
  if (m.cmd === 'law' && ['proclaim', 'repeal', 'info'].indexOf(sub) >= 0 && lit(a[1]) && !model.laws.has(a[1])) errors.push('unknown law id "' + a[1] + '"');
  if (m.cmd === 'court' && sub === 'accuse' && lit(a[2]) && !model.laws.has(a[2])) errors.push('unknown law id "' + a[2] + '" in /court accuse');
  if (m.cmd === 'event' && ['start', 'stop', 'cancel'].indexOf(sub) >= 0 && lit(a[1]) && !model.eventKinds.has(a[1])) errors.push('unknown event kind "' + a[1] + '"');
  if (m.cmd === 'event' && sub === 'start' && /^\d+$/.test(a[2] || '') && (+a[2] < 1 || +a[2] > model.eventMaxMinutes)) errors.push('event minutes ' + a[2] + ' outside 1..' + model.eventMaxMinutes);
  if (m.cmd === 'season' && sub === 'start' && /^\d+$/.test(a[1] || '') && (+a[1] < 1 || +a[1] > model.seasonMaxDays)) errors.push('season days ' + a[1] + ' outside 1..365');
  if (m.cmd === 'titles' && sub === 'set' && a.length > 1 && !/^[<[{]/.test(a[1])) {
    const want = a.slice(1).join(' ').toLowerCase();
    if (!model.titles.some((t) => t.name.toLowerCase() === want || t.id === want)) errors.push('unknown title "' + a.slice(1).join(' ') + '"');
  }
  return { errors, owner: chat.plugin };
}

export function expandPlaceholders(text) {
  const unknown = [];
  const out = text.replace(/\{([a-z_]+)\}/g, (all, name) => {
    if (PLACEHOLDERS.indexOf(name) < 0) unknown.push(name);
    return 'X'.repeat(PLACEHOLDER_WIDTH);
  });
  return { text: out, unknown };
}

export function checkHeraldLine(text) {
  const errors = [];
  const t = text.trim();
  if (!t) { errors.push('empty herald line'); return errors; }
  if (t.startsWith('/')) errors.push('herald line starts with "/" (the console would run it as a command)');
  if (/["'`]/.test(t)) errors.push('herald line contains a quote or apostrophe (Steward turns them into backticks in /notice and /popup)');
  if (/[^\x20-\x7e]/.test(t)) errors.push('herald line has a non-ASCII character (the game may show it as "?")');
  const { text: expanded, unknown } = expandPlaceholders(t);
  for (const u of unknown) errors.push('unknown placeholder {' + u + '}');
  if (expanded.length > INGAME_MAX) errors.push('herald line is ' + expanded.length + ' chars with placeholders filled (max ' + INGAME_MAX + ')');
  return errors;
}

export function checkDiscordText(text) {
  const errors = [];
  if (text.length > DISCORD_MAX) errors.push('Discord text is ' + text.length + ' chars (max ' + DISCORD_MAX + ')');
  if (/@everyone|@here|<@[!&]?\d+>/.test(text)) errors.push('Discord text pings (@everyone, @here or a mention)');
  const { unknown } = expandPlaceholders(text);
  for (const u of unknown) errors.push('unknown placeholder {' + u + '} in Discord text');
  return errors;
}

// Returns [{ info, body, line }] for every fenced block.
export function fencedBlocks(md) {
  const blocks = [];
  const lines = md.split('\n');
  let open = null;
  for (let i = 0; i < lines.length; i++) {
    const f = /^\s*```(\S*)/.exec(lines[i]);
    if (!f) { if (open) open.body.push(lines[i]); continue; }
    if (open) { blocks.push({ info: open.info, body: open.body.join('\n'), line: open.line }); open = null; }
    else open = { info: f[1], body: [], line: i + 1 };
  }
  return blocks;
}

// Splits a document into sections by "### <ID> ..." headings, where ID matches the given prefix (P, L, Q).
export function sections(md, prefix) {
  const out = [];
  const lines = md.split('\n');
  let cur = null;
  const re = new RegExp('^### (' + prefix + '\\d{2})\\b(.*)$');
  for (let i = 0; i < lines.length; i++) {
    const m = re.exec(lines[i]);
    if (m) { cur = { id: m[1], title: m[2].trim(), line: i + 1, body: [] }; out.push(cur); continue; }
    if (/^#{1,3} /.test(lines[i])) { cur = null; continue; }
    if (cur) cur.body.push(lines[i]);
  }
  for (const s of out) s.body = s.body.join('\n');
  return out;
}

function expectSequence(errors, file, items, prefix, count) {
  const ids = items.map((s) => s.id);
  for (let n = 1; n <= count; n++) {
    const id = prefix + String(n).padStart(2, '0');
    if (ids.indexOf(id) < 0) errors.push(file + ': missing ' + id);
  }
  if (items.length !== count) errors.push(file + ': expected ' + count + ' entries, found ' + items.length);
  const seen = new Set();
  for (const id of ids) { if (seen.has(id)) errors.push(file + ': duplicate ' + id); seen.add(id); }
}

export function parseScheduleTable(md) {
  const a = md.indexOf('<!-- schedule:begin -->');
  const b = md.indexOf('<!-- schedule:end -->');
  if (a < 0 || b < a) return null;
  const rows = [];
  for (const line of md.slice(a, b).split('\n')) {
    if (!line.startsWith('|') || /^\|\s*-/.test(line) || /\|\s*Slot\s*\|/.test(line)) continue;
    const c = line.split('|').slice(1, -1).map((x) => x.trim());
    if (c.length < 5) continue;
    rows.push({ slot: c[0], day: c[1], start: c[2], minutes: +c[3], source: c[4] });
  }
  return rows;
}

const slotKey = (s) => [s.slot, s.day, s.start, s.minutes, s.source].join('|');

export function compareSchedule(docRows, srcRows) {
  const errors = [];
  const doc = new Set(docRows.map(slotKey));
  const src = new Set(srcRows.map(slotKey));
  for (const k of src) if (!doc.has(k)) errors.push('schedule slot in plugin defaults but not in the calendar: ' + k);
  for (const k of doc) if (!src.has(k)) errors.push('schedule slot in the calendar but not in plugin defaults: ' + k);
  return errors;
}

export function listSagaFiles(sagaDir) {
  const out = [];
  const walk = (d) => {
    for (const f of fs.readdirSync(d)) {
      const p = path.join(d, f);
      if (fs.statSync(p).isDirectory()) { if (f !== 'tools') walk(p); }
      else if (f.endsWith('.md') && f !== 'COMMANDS.md') out.push(p);
    }
  };
  walk(sagaDir);
  return out.sort();
}

export function runChecks(root) {
  const model = loadModel(root);
  const sagaDir = path.join(root, 'docs', 'saga');
  const errors = [];
  const mentions = [];
  const rel = (p) => path.relative(root, p).split(path.sep).join('/');

  for (const file of listSagaFiles(sagaDir)) {
    const md = fs.readFileSync(file, 'utf8');
    for (const m of extractCommands(md)) {
      const r = checkMention(model, m);
      for (const e of r.errors) errors.push(rel(file) + ':' + m.line + ': ' + e + '  [' + m.raw + ']');
      mentions.push({ file: rel(file), line: m.line, cmd: m.cmd, sub: m.args[0] && isLiteralWord(m.args[0]) ? m.args[0] : '', owner: r.owner, oxide: m.oxide });
    }
    for (const b of fencedBlocks(md)) {
      if (b.info === 'ingame') for (const e of checkHeraldLine(b.body.replace(/\n/g, ' '))) errors.push(rel(file) + ':' + b.line + ': ' + e);
      if (b.info === 'discord') for (const e of checkDiscordText(b.body)) errors.push(rel(file) + ':' + b.line + ': ' + e);
    }
    // Chronicle types named on "**Chronicle:**" lines must be registered.
    md.split('\n').forEach((line, i) => {
      if (!/\*\*Chronicle:\*\*/.test(line)) return;
      const re = /`([a-z_]+)`/g;
      let m;
      while ((m = re.exec(line))) {
        if (!model.chronicleTypes.has(m[1]) && !model.extraTypes.has(m[1])) errors.push(rel(file) + ':' + (i + 1) + ': chronicle type `' + m[1] + '` is not in RealmChronicle KnownTypes or docs/saga/EVENTS.json');
      }
    });
  }

  // Deliverable shape.
  const readSaga = (f) => { const p = path.join(sagaDir, f); return fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : null; };
  const procl = readSaga('proclamations.md');
  if (procl === null) errors.push('docs/saga/proclamations.md is missing');
  else {
    const ps = sections(procl, 'P');
    expectSequence(errors, 'proclamations.md', ps, 'P', EXPECTED.proclamations);
    for (const s of ps) {
      const bl = fencedBlocks(s.body);
      if (bl.filter((b) => b.info === 'ingame').length !== 1) errors.push('proclamations.md ' + s.id + ': needs exactly one ```ingame block');
      if (bl.filter((b) => b.info === 'discord').length !== 1) errors.push('proclamations.md ' + s.id + ': needs exactly one ```discord block');
      if (!/\*\*When:\*\*/.test(s.body)) errors.push('proclamations.md ' + s.id + ': needs a **When:** line');
    }
  }
  const locs = readSaga('locations.md');
  if (locs === null) errors.push('docs/saga/locations.md is missing');
  else {
    const ls = sections(locs, 'L');
    expectSequence(errors, 'locations.md', ls, 'L', EXPECTED.locations);
    for (const s of ls) if (!/\*\*Place it at:\*\*/.test(s.body)) errors.push('locations.md ' + s.id + ': needs a **Place it at:** line');
  }
  const legends = readSaga('legends.md');
  if (legends === null) errors.push('docs/saga/legends.md is missing');
  else {
    const qs = sections(legends, 'Q');
    expectSequence(errors, 'legends.md', qs, 'Q', EXPECTED.legends);
    for (const s of qs) {
      for (const field of ['Goal', 'Steps', 'Proof', 'Chronicle']) if (!new RegExp('\\*\\*' + field + ':\\*\\*').test(s.body)) errors.push('legends.md ' + s.id + ': needs a **' + field + ':** line');
    }
  }
  const cal = readSaga('calendar-8-weeks.md');
  if (cal === null) errors.push('docs/saga/calendar-8-weeks.md is missing');
  else {
    for (let w = 1; w <= EXPECTED.weeks; w++) if (!new RegExp('^## Week ' + w + '\\b', 'm').test(cal)) errors.push('calendar-8-weeks.md: missing "## Week ' + w + '"');
    const rows = parseScheduleTable(cal);
    if (!rows) errors.push('calendar-8-weeks.md: missing the <!-- schedule:begin --> table');
    else for (const e of compareSchedule(rows, model.schedule)) errors.push('calendar-8-weeks.md: ' + e);
  }
  for (let a = 1; a <= EXPECTED.acts; a++) {
    const dir = path.join(sagaDir, 'runsheets');
    const hit = fs.existsSync(dir) && fs.readdirSync(dir).some((f) => f.startsWith('act-' + a + '-') && f.endsWith('.md'));
    if (!hit) errors.push('docs/saga/runsheets/act-' + a + '-*.md is missing');
  }

  return { model, errors, mentions };
}

// ---------------------------------------------------------------------------------------------------------------
// COMMANDS.md (generated)

export function renderIndex(model, mentions) {
  const by = new Map();
  for (const m of mentions) {
    if (!m.owner) continue;
    const key = (m.oxide ? '' : '/') + m.cmd + (m.sub ? ' ' + m.sub : '');
    if (!by.has(key)) by.set(key, { key, owner: m.owner, files: new Set() });
    by.get(key).files.add(m.file.replace(/^docs\/saga\//, ''));
  }
  const rows = [...by.values()].sort((a, b) => a.key.localeCompare(b.key));
  const status = (owner) => {
    if (owner === 'game console') return 'game built-in, read in decompiled code [DEC]; UNVERIFIED at run time';
    if (owner === 'RealmCourt') return pluginStatus(model, 'RealmCourt') + '; console only; UNVERIFIED at run time';
    if (owner === 'Oxide console') return 'Oxide built-in (docs/oxide-rok-api.md)';
    return pluginStatus(model, owner);
  };
  const out = [];
  out.push('# Saga command index (generated)');
  out.push('');
  out.push('Generated by `node docs/saga/tools/check-saga.mjs --write` from `plugins/*.cs`, `docs/admin-console.md` and');
  out.push('`docs/oxide-rok-api.md`. Do not edit by hand. Every command the saga pack mentions is listed with the plugin that');
  out.push('defines it. "being built this run" means another team is still writing that plugin: re-run the checker before each');
  out.push('act, because its syntax can still move.');
  out.push('');
  out.push('| Command | Defined by | Status | Used in |');
  out.push('|---|---|---|---|');
  for (const r of rows) out.push('| `' + r.key + '` | ' + r.owner + ' | ' + status(r.owner) + ' | ' + [...r.files].sort().join(', ') + ' |');
  out.push('');
  out.push('## Weekly rhythm read from the plugins\' shipped defaults');
  out.push('');
  out.push('A server config can override these. Times are UTC.');
  out.push('');
  out.push('| Slot | Day | Start (UTC) | Minutes | Source |');
  out.push('|---|---|---|---|---|');
  for (const s of model.schedule) out.push('| ' + [s.slot, s.day, s.start, s.minutes, s.source].join(' | ') + ' |');
  out.push('');
  out.push('## Catalogues read from the plugins');
  out.push('');
  out.push('- Decrees (`/decree <id>`): ' + [...model.decrees].map((d) => '`' + d + '`').join(', '));
  out.push('- Laws (`/law proclaim <id>`): ' + [...model.laws].map((d) => '`' + d + '`').join(', '));
  out.push('- Realm events (`/event start <kind>`): ' + [...model.eventKinds].map((d) => '`' + d + '`').join(', '));
  out.push('- Titles (`/titles set <title>`): ' + model.titles.map((t) => t.name).join(', '));
  out.push('- Chronicle types: ' + [...model.chronicleTypes].map((d) => '`' + d + '`').join(', '));
  out.push('');
  return out.join('\n');
}

// ---------------------------------------------------------------------------------------------------------------

function main() {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const root = path.resolve(here, '..', '..', '..');
  const argv = process.argv.slice(2);
  const { model, errors, mentions } = runChecks(root);
  const indexPath = path.join(root, 'docs', 'saga', 'COMMANDS.md');
  const index = renderIndex(model, mentions);
  if (argv.indexOf('--write') >= 0) fs.writeFileSync(indexPath, index);
  else if (!fs.existsSync(indexPath) || fs.readFileSync(indexPath, 'utf8') !== index) errors.push('docs/saga/COMMANDS.md is out of date: run with --write');

  if (argv.indexOf('--json') >= 0) {
    console.log(JSON.stringify({ errors, mentions: mentions.length, commands: [...new Set(mentions.map((m) => m.cmd))].sort() }, null, 2));
  } else {
    const cmds = new Set(mentions.map((m) => m.cmd));
    console.log('saga check: ' + mentions.length + ' command mentions (' + cmds.size + ' distinct commands), ' + errors.length + ' error(s)');
    for (const e of errors) console.log('  ERROR ' + e);
  }
  process.exit(errors.length ? 1 : 0);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
