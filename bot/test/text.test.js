import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { DYES, LIMITS, TYPE_META, clampEmbed, dyeFor, dyeHexFor, embedSize, esc, loadChronicleLabels, loadExtraTypes, metaFor, plain, sameHouse, truncate, ts } from '../src/text.js';

// common.js is browser code (it touches `location` at load), so it is read as text, never imported.
const COMMON_PATH = fileURLToPath(new URL('../../chronicle/public/assets/common.js', import.meta.url));
const COMMON = readFileSync(COMMON_PATH, 'utf8');

test('built-in labels match the Chronicle pages for every original type', () => {
  for (const [type, meta] of Object.entries(TYPE_META)) {
    const m = new RegExp(`^\\s*${type}:\\s*\\{\\s*label:\\s*'((?:[^'\\\\]|\\\\.)*)'`, 'm').exec(COMMON);
    if (!m) continue; // a type the Chronicle dropped: the built-in label is simply unused
    assert.equal(meta.label, m[1].replace(/\\(.)/g, '$1'), `label drift for ${type}`);
  }
});

test('every type registered in common.js gets its label once loadChronicleLabels() has run', () => {
  const n = loadChronicleLabels(COMMON_PATH);
  assert.ok(n >= Object.keys(TYPE_META).length - 2, `read only ${n} types from common.js`);
  const types = [...COMMON.slice(COMMON.indexOf('TYPE_META = {')).split('\n};')[0].matchAll(/^\s*([a-z_]+):\s*\{\s*label:/gm)].map((m) => m[1]);
  for (const type of types) assert.notEqual(metaFor(type).label, 'Chronicle', `no label for ${type}`);
  assert.equal(metaFor('hunt_kill').label, "The King's Hunt", 'escaped quotes are unescaped');
  assert.equal(loadChronicleLabels('/definitely/not/here.js'), 0);
});

test('house colours use the overlay banner dye (same palette and hash as common.js)', () => {
  const dyes = /const DYES = (\[[^\]]*\])/.exec(COMMON);
  assert.ok(dyes, 'DYES not found in common.js');
  assert.deepEqual(JSON.parse(dyes[1].replace(/'/g, '"')), DYES);
  assert.match(COMMON, /let h = 2166136261;/, 'common.js hash changed');
  assert.match(COMMON, /Math\.imul\(h \^ ch\.codePointAt\(0\), 16777619\)/, 'common.js hash changed');
  assert.match(COMMON, /DYES\[hash\(name \|\| ''\) % DYES\.length\]/, 'common.js dye pick changed');
  // lore.md documents these overlay dyes.
  assert.equal(dyeHexFor('Varrow'), '#4a2347');
  assert.equal(dyeHexFor('Merrin'), '#24472d');
  assert.equal(dyeFor('Varrow'), 0x4a2347);
  assert.equal(DYES.length, 10);
});

test('esc() escapes Markdown and defuses mentions', () => {
  const out = esc('@everyone **bold** [link](https://x) <@123> `code` ~~s~~ _i_ > quote # h');
  assert.ok(!out.includes('@everyone'));
  assert.ok(out.includes('@​everyone'));
  // Every Markdown character is preceded by a backslash.
  for (let i = 0; i < out.length; i++) {
    if ('*_~`|>#[]()<'.includes(out[i])) assert.equal(out[i - 1], '\\', `unescaped ${out[i]} at ${i}: ${out}`);
  }
  assert.ok(!out.includes('<@123>'));
  assert.equal(esc('a\u0000b‮c'), 'abc');
  assert.equal(esc('x'.repeat(500), 10).length, 10);
});

test('plain() strips @ and control characters for role names and choices', () => {
  assert.equal(plain(' @here Varrow\n'), 'here Varrow');
});

test('sameHouse ignores case and a leading "House"', () => {
  assert.ok(sameHouse('house varrow', 'Varrow'));
  assert.ok(!sameHouse('Varrow', 'Varrows'));
  assert.ok(!sameHouse(null, 'Varrow'));
});

test('truncate never leaves a dangling escape backslash', () => {
  const s = truncate('ab\\*cd', 4);
  assert.ok(!/(^|[^\\])\\…$/.test(s), s);
  assert.equal(truncate('short', 10), 'short');
});

test('ts() renders Discord timestamps, and survives junk', () => {
  assert.equal(ts('2026-10-01T00:00:00Z'), '<t:1790812800:R>');
  assert.equal(ts('nope'), 'unknown time');
});

test('clampEmbed keeps every embed inside Discord limits', () => {
  const huge = {
    title: 'T'.repeat(400),
    description: 'D'.repeat(5000),
    footer: { text: 'F'.repeat(3000) },
    fields: Array.from({ length: 40 }, (_, i) => ({ name: `N${i}`.repeat(100), value: 'V'.repeat(2000) })),
  };
  const e = clampEmbed(huge);
  assert.ok(e.title.length <= LIMITS.title);
  assert.ok(e.description.length <= LIMITS.description);
  assert.ok(e.fields.length <= LIMITS.fields);
  for (const f of e.fields) assert.ok(f.name.length <= LIMITS.fieldName && f.value.length <= LIMITS.fieldValue);
  assert.ok(embedSize(e) <= LIMITS.total, `size ${embedSize(e)}`);
});

test('pending event types from plugins/docs/*/EVENTS.json get their labels', () => {
  assert.equal(metaFor('no_such_pending_type').label, 'Chronicle');
  // The repo's own EVENTS.json files are deleted once their types are registered, so use a scratch layout.
  const dir = mkdtempSync(join(tmpdir(), 'bot-events-'));
  try {
    mkdirSync(join(dir, 'RealmExample'));
    writeFileSync(join(dir, 'RealmExample', 'EVENTS.json'), JSON.stringify([{ type: 'pending_example', label: 'A Pending Example', icon: 'seal' }]));
    mkdirSync(join(dir, 'RealmBroken'));
    writeFileSync(join(dir, 'RealmBroken', 'EVENTS.json'), '{ not json');
    assert.equal(loadExtraTypes(dir), 1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
  assert.equal(metaFor('pending_example').label, 'A Pending Example');
  assert.equal(loadExtraTypes(fileURLToPath(new URL('../../plugins/docs/', import.meta.url))) >= 0, true);
  assert.equal(metaFor('coronation').label, 'Coronation', 'built-in labels are never overridden');
  assert.equal(loadExtraTypes('/definitely/not/here'), 0);
});
