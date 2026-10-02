// Tests for check-saga.mjs. Run: node --test docs/saga/tools/check-saga.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  loadModel, runChecks, checkMention, checkHeraldLine, checkDiscordText, extractCommands, tokenize, sections,
  parseScheduleTable, compareSchedule, parseChronicleTypes, renderIndex, EXPECTED, INGAME_MAX
} from './check-saga.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const model = loadModel(ROOT);

function mention(raw) {
  const found = extractCommands('`' + raw + '`');
  assert.equal(found.length >= 1, true, 'no mention parsed from ' + raw);
  return checkMention(model, found[0]);
}

test('the saga pack in this repo passes every check', () => {
  const { errors, mentions } = runChecks(ROOT);
  assert.deepEqual(errors, []);
  assert.ok(mentions.length > 100, 'expected the pack to mention many commands, got ' + mentions.length);
});

test('COMMANDS.md matches what the sources generate', () => {
  const { model: m, mentions } = runChecks(ROOT);
  const onDisk = fs.readFileSync(path.join(ROOT, 'docs', 'saga', 'COMMANDS.md'), 'utf8');
  assert.equal(onDisk, renderIndex(m, mentions));
});

test('the source model reads the real plugins', () => {
  for (const c of ['claim', 'house', 'treaty', 'swear', 'ransom', 'crown', 'chronicle', 'contract']) assert.ok(model.chat.has(c), c);
  assert.ok(model.decrees.has('peace') && model.decrees.has('roads'));
  assert.ok(model.laws.has('kings_peace') && model.laws.has('bridge_toll'));
  assert.ok(model.eventKinds.has('tournament') && model.eventKinds.has('crown_night'));
  assert.ok(model.titles.some((t) => t.name === 'Champion of the Lists'));
  assert.ok(model.game.has('notice') && model.game.has('popup'));
  assert.ok(model.court.has('realm.save'));
  assert.ok(model.oxide.has('oxide.grant') && model.oxide.has('oxide.reload'));
  assert.ok(model.chronicleTypes.has('coronation') && model.chronicleTypes.has('tournament_champion'));
  assert.equal(model.chronicleTypes.has('legend_completed'), false);
  assert.ok(model.schedule.length >= 6);
});

test('known commands pass', () => {
  for (const raw of ['/claim declare', '/house found Varrow Iron Stag', '/decree peace', '/law proclaim kings_peace',
    '/event start tournament 60', '/season start 56 The Hollow Crown', '/titles set Champion of the Lists',
    '/raven admin approve <id>', '/law zone set Crown Market 60 town', '/court verdict <case> guilty', '/notice <text>',
    '/realm.save', 'oxide.grant user <steward> realmlaws.admin', '/renown top infamy']) {
    assert.deepEqual(mention(raw).errors, [], raw);
  }
});

test('unknown commands, subcommands and ids fail', () => {
  const bad = {
    '/teleport home': /unknown command/,
    '/claim usurp': /no subcommand "usurp"/,
    '/decree feast': /unknown decree id "feast"/,
    '/law proclaim no_spitting': /unknown law id "no_spitting"/,
    '/court accuse <player> no_spitting': /unknown law id/,
    '/event start joust': /unknown event kind "joust"/,
    '/event start tournament 999': /outside 1\.\.360/,
    '/season start 400 Forever': /outside 1\.\.365/,
    '/titles set Lord of Nowhere': /unknown title/,
    '/raven admin obliterate': /no option "obliterate"/,
    'oxide.nuke all': /unknown Oxide console command/
  };
  for (const [raw, re] of Object.entries(bad)) {
    const r = mention(raw);
    assert.ok(r.errors.some((e) => re.test(e)), raw + ' -> ' + JSON.stringify(r.errors));
  }
});

test('mentions are found in spans, alternatives, code blocks and herald blocks, but not in Discord blocks', () => {
  const md = [
    'Use `/treaty propose <house> [days] | accept <house>` and `/crown`.',
    '```',
    '/claim list',
    'not a command',
    '```',
    '```ingame',
    'See /crown. Then type /claim declare, or /season standings.',
    '```',
    '```discord',
    'Type /teleport now',
    '```'
  ].join('\n');
  const found = extractCommands(md).map((m) => m.cmd + (m.args[0] ? ' ' + m.args[0] : ''));
  assert.deepEqual(found, ['treaty propose', 'treaty accept', 'crown', 'claim list', 'crown', 'claim declare', 'season standings']);
});

test('tokenize keeps quotes and placeholders together', () => {
  assert.deepEqual(tokenize('/contract post bounty <player name> 50 "Iron Ingot" [hours]'),
    ['/contract', 'post', 'bounty', '<player name>', '50', '"Iron Ingot"', '[hours]']);
});

test('herald lines follow the console rules', () => {
  assert.deepEqual(checkHeraldLine('The seat is taken by {monarch} of House {crown_house}.'), []);
  assert.match(checkHeraldLine("The King's Peace").join(), /apostrophe/);
  assert.match(checkHeraldLine('He said "no"').join(), /quote/);
  assert.match(checkHeraldLine('/claim declare now').join(), /starts with/);
  assert.match(checkHeraldLine('Café of the realm').join(), /non-ASCII/);
  assert.match(checkHeraldLine('Hail {emperor}').join(), /unknown placeholder \{emperor\}/);
  assert.match(checkHeraldLine('x'.repeat(INGAME_MAX + 1)).join(), /chars/);
  // Placeholders are counted at their filled width, so a line that looks short can still be too long.
  const many = Array(13).fill('{monarch}').join(' ');
  assert.ok(many.length < INGAME_MAX);
  assert.match(checkHeraldLine(many).join(), /chars with placeholders filled/);
});

test('Discord text never pings and fits a message', () => {
  assert.deepEqual(checkDiscordText('**Hail** {house}'), []);
  assert.match(checkDiscordText('@everyone the crown falls').join(), /pings/);
  assert.match(checkDiscordText('hi <@123456789>').join(), /pings/);
  assert.match(checkDiscordText('x'.repeat(2001)).join(), /max 2000/);
});

test('schedule drift between the calendar and the plugin defaults is caught', () => {
  const cal = fs.readFileSync(path.join(ROOT, 'docs', 'saga', 'calendar-8-weeks.md'), 'utf8');
  const rows = parseScheduleTable(cal);
  assert.deepEqual(compareSchedule(rows, model.schedule), []);
  const moved = rows.map((r) => (r.slot === 'Royal Tournament' ? Object.assign({}, r, { start: '20:00' }) : r));
  const errs = compareSchedule(moved, model.schedule);
  assert.equal(errs.length, 2);
  assert.match(errs.join('\n'), /Royal Tournament\|Friday\|19:00/);
});

test('chronicle KnownTypes parsing ignores comments', () => {
  const src = 'private static readonly string[] KnownTypes =\n{\n "a_b", // "not_me"\n "c"\n};';
  assert.deepEqual([...parseChronicleTypes(src)], ['a_b', 'c']);
});

test('sections() splits numbered entries and stops at the next heading', () => {
  const s = sections('### P01 One\nbody1\n### P02 Two\nbody2\n## Act\n### Not one\n', 'P');
  assert.deepEqual(s.map((x) => [x.id, x.title, x.body.trim()]), [['P01', 'One', 'body1'], ['P02', 'Two', 'body2']]);
});

// Copies the parts of the repo the checker reads into a temp dir, so the shape checks can be broken on purpose.
function sandbox() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'saga-check-'));
  fs.cpSync(path.join(ROOT, 'plugins'), path.join(dir, 'plugins'), { recursive: true, filter: (p) => !p.includes(path.sep + 'docs') });
  fs.mkdirSync(path.join(dir, 'docs'), { recursive: true });
  for (const f of ['admin-console.md', 'oxide-rok-api.md']) fs.copyFileSync(path.join(ROOT, 'docs', f), path.join(dir, 'docs', f));
  fs.cpSync(path.join(ROOT, 'docs', 'saga'), path.join(dir, 'docs', 'saga'), { recursive: true });
  return dir;
}

test('shape checks: a missing proclamation, legend proof, act and an unknown chronicle type all fail', () => {
  const dir = sandbox();
  try {
    const saga = path.join(dir, 'docs', 'saga');
    const p = path.join(saga, 'proclamations.md');
    fs.writeFileSync(p, fs.readFileSync(p, 'utf8').replace('### P30 The Crown Is Weighed', '### Epilogue'));
    const q = path.join(saga, 'legends.md');
    fs.writeFileSync(q, fs.readFileSync(q, 'utf8').replace('**Chronicle:** `coronation`', '**Chronicle:** `legend_completed`'));
    fs.rmSync(path.join(saga, 'runsheets', 'act-4-the-reckoning.md'));
    const { errors } = runChecks(dir);
    const all = errors.join('\n');
    assert.match(all, /proclamations\.md: missing P30/);
    assert.match(all, new RegExp('expected ' + EXPECTED.proclamations + ' entries, found 29'));
    assert.match(all, /chronicle type `legend_completed` is not in RealmChronicle KnownTypes/);
    assert.match(all, /act-4-\*\.md is missing/);

    // Registering the type in docs/saga/EVENTS.json makes it legal.
    fs.writeFileSync(path.join(saga, 'EVENTS.json'), JSON.stringify([{ type: 'legend_completed', label: 'Legend', icon: 'star' }]));
    assert.doesNotMatch(runChecks(dir).errors.join('\n'), /legend_completed/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a plugin that renames a subcommand breaks the pack (drift detection)', () => {
  const dir = sandbox();
  try {
    const f = path.join(dir, 'plugins', 'CrownAndConsequences.cs');
    fs.writeFileSync(f, fs.readFileSync(f, 'utf8').split('"declare"').join('"announce"'));
    const all = runChecks(dir).errors.join('\n');
    assert.match(all, /\/claim has no subcommand "declare"/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
