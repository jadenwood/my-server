// node --test docs/legal/tools/*.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { parseArgs, quoteBigInts, findInJson, makeMatcher, scan, main } from './find-player-data.mjs';

const ME = '76561198000000001';
const OTHER = '76561198000000002';
const SCRIPT = path.join(path.dirname(fileURLToPath(import.meta.url)), 'find-player-data.mjs');

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-fpd-'));
  const data = path.join(root, 'data');
  const logs = path.join(root, 'logs', 'RealmWarden');
  fs.mkdirSync(data, { recursive: true });
  fs.mkdirSync(logs, { recursive: true });
  // A RealmHouses-like file: member ids as dictionary keys and as a bare big number.
  fs.writeFileSync(path.join(data, 'RealmHouses.json'),
    `{"Houses":{"Varrow":{"Leader":${ME},"Members":{"${ME}":"Old Tom","${OTHER}":"Mira"}},"Ashgrove":{"Leader":${OTHER}}}}`);
  // A Chronicle-like file: public names in actors.
  fs.writeFileSync(path.join(data, 'RealmChronicle.json'),
    JSON.stringify([{ id: 1, type: 'oath_sworn', title: 'Old Tom swears to Varrow', actors: ['Old Tom'] }, { id: 2, type: 'house_founded', actors: ['Mira'] }]));
  // A file with nothing about the player.
  fs.writeFileSync(path.join(data, 'RealmSeasons.json'), JSON.stringify({ Season: 3, Houses: ['Ashgrove'] }));
  // Damaged JSON must still be scanned as text.
  fs.writeFileSync(path.join(data, 'RealmWarden.json'), `{"Players":{"${ME}": {"Flags": 2}, `);
  // A log file: id on line 2, the name as a whole word on line 3, and a near-miss on line 4.
  fs.writeFileSync(path.join(logs, 'realmwarden_evidence-2026-09-30.txt'),
    `header\nflag combat_log ${ME}\nreport against Old Tom by Mira\nOld Tomas did nothing\n`);
  fs.writeFileSync(path.join(data, 'backup-2026-09-01.zip'), 'not really a zip');
  return { root, data, logs };
}

test('options: id format, required pieces, help', () => {
  assert.throws(() => parseArgs(['--dir', 'x']), /--steamid and\/or --name/);
  assert.throws(() => parseArgs(['--steamid', '123', '--dir', 'x']), /17-digit/);
  assert.throws(() => parseArgs(['--steamid', ME]), /--dir/);
  assert.throws(() => parseArgs(['--name', 'a', '--dir', 'x']), /at least 2/);
  assert.throws(() => parseArgs(['--bogus']), /unknown option/);
  const o = parseArgs(['--steamid', ME, '--name', ' Old Tom ', '--dir', 'a', '--dir', 'b', '--json']);
  assert.deepEqual(o.dirs, ['a', 'b']);
  assert.deepEqual(o.names, ['Old Tom']);
  assert.equal(o.json, true);
  assert.equal(parseArgs(['--help']).help, true);
});

test('big integers keep full precision; digits inside strings untouched', () => {
  const t = `{"a":${ME},"b":"x ${OTHER} y","c":12,"d":1.5e3,"e":[${OTHER}]}`;
  const v = JSON.parse(quoteBigInts(t));
  assert.equal(v.a, ME);
  assert.equal(v.b, `x ${OTHER} y`);
  assert.equal(v.c, 12);
  assert.equal(v.d, 1500);
  assert.deepEqual(v.e, [OTHER]);
  // Without the quoting step the id would be corrupted, which is why it exists.
  assert.notEqual(String(JSON.parse(`{"a":${ME}}`).a), ME);
});

test('JSON paths: keys and values, other players never matched', () => {
  const m = makeMatcher({ steamid: ME, names: ['Old Tom'] });
  const doc = JSON.parse(quoteBigInts(fs.readFileSync(path.join(fixture().data, 'RealmHouses.json'), 'utf8')));
  const paths = findInJson(doc, m);
  assert.deepEqual(paths.sort(), [
    '$.Houses.Varrow.Leader',
    `$.Houses.Varrow.Members["${ME}"]`,
    `$.Houses.Varrow.Members["${ME}"] (key)`
  ].sort());
  assert.ok(!paths.some((p) => p.includes(OTHER)));
});

test('full scan: finds every file, line scan for damaged JSON and logs, notes archives', () => {
  const f = fixture();
  const rep = scan({ dirs: [f.data, path.join(f.root, 'logs')], steamid: ME, names: ['Old Tom'] });
  const byName = Object.fromEntries(rep.results.map((r) => [path.basename(r.file), r]));
  assert.deepEqual(Object.keys(byName).sort(), ['RealmChronicle.json', 'RealmHouses.json', 'RealmWarden.json', 'realmwarden_evidence-2026-09-30.txt']);
  assert.deepEqual(byName['RealmChronicle.json'].paths, ['$[0].actors[0]']);
  assert.equal(byName['RealmWarden.json'].kind, 'text (JSON did not parse)');
  assert.deepEqual(byName['realmwarden_evidence-2026-09-30.txt'].lines, [2, 3]);
  assert.ok(rep.notes.some((n) => n.includes('backup-2026-09-01.zip') && n.includes('archive')));
  assert.equal(rep.unreadable, false);
});

test('report never prints values or other players', () => {
  const f = fixture();
  const lines = [];
  const code = main(['--steamid', ME, '--name', 'Old Tom', '--dir', f.data, '--dir', path.join(f.root, 'logs')], { out: (s) => lines.push(s), err: (s) => lines.push(s) });
  const text = lines.join('\n');
  assert.equal(code, 0);
  assert.ok(!text.includes(OTHER), 'other player id leaked');
  assert.ok(!text.includes('Mira'), 'other player name leaked');
  assert.ok(!text.includes('swears to Varrow'), 'a value leaked');
  // The requester's own id may appear inside a JSON path (a dictionary keyed by it); the header shortens it.
  assert.ok(!lines[0].split('\n')[0].includes(ME), 'the full id should be shortened in the header');
  assert.match(text, /Files with matches: 4/);
});

test('missing folder exits 1; bad options exit 2; files are never changed', () => {
  const f = fixture();
  const before = fs.readdirSync(f.data).map((n) => [n, fs.readFileSync(path.join(f.data, n), 'utf8')]);
  const sink = { out() {}, err() {} };
  assert.equal(main(['--steamid', ME, '--dir', path.join(f.root, 'nope')], sink), 1);
  assert.equal(main(['--steamid', 'x', '--dir', f.data], sink), 2);
  main(['--steamid', ME, '--dir', f.data], sink);
  const after = fs.readdirSync(f.data).map((n) => [n, fs.readFileSync(path.join(f.data, n), 'utf8')]);
  assert.deepEqual(after, before);
});

test('CLI end to end with --json', () => {
  const f = fixture();
  const r = spawnSync(process.execPath, [SCRIPT, '--steamid', ME, '--dir', f.data, '--json'], { encoding: 'utf8' });
  assert.equal(r.status, 0, r.stderr);
  const j = JSON.parse(r.stdout);
  assert.equal(j.results.length, 2); // Houses (by id) and the damaged Warden file; the Chronicle holds names only
  assert.ok(!r.stdout.includes(OTHER));
});
