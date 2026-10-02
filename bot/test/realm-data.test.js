import assert from 'node:assert/strict';
import { copyFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { cleanActiveEvents, createRealmData, dayMatches, parseUtc, upcomingFromSchedule } from '../src/realm-data.js';
import { FIXTURES, NOW, tmpDir } from './helpers.js';

test('parseUtc treats zone-less Oxide timestamps as UTC and DateTime.MinValue as unset', () => {
  assert.equal(parseUtc('2026-10-01T10:00:00'), Date.parse('2026-10-01T10:00:00Z'));
  assert.equal(parseUtc('2026-10-01T10:00:00+02:00'), Date.parse('2026-10-01T08:00:00Z'));
  assert.equal(parseUtc('0001-01-01T00:00:00'), null);
  assert.equal(parseUtc(42), null);
});

test('dayMatches follows RealmEvents: weekday names, Daily, Weekdays, Weekends', () => {
  assert.ok(dayMatches(['Saturday'], 6));
  assert.ok(dayMatches([' saturday '], 6));
  assert.ok(!dayMatches(['Saturday'], 5));
  assert.ok(dayMatches(['Daily'], 3));
  assert.ok(dayMatches(['Weekdays'], 1) && !dayMatches(['Weekdays'], 0));
  assert.ok(dayMatches(['Weekends'], 0) && !dayMatches(['Weekends'], 3));
  assert.ok(!dayMatches(null, 3));
});

test('upcoming schedule from the fixture config (now = Friday 19:30 UTC)', async () => {
  const data = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config'), now: () => NOW });
  const ev = await data.realmEvents();
  assert.deepEqual(ev.upcoming.map((u) => [u.kind, new Date(u.start).toISOString()]), [
    ['truce', '2026-10-03T12:00:00.000Z'],
    ['crown_night', '2026-10-03T19:00:00.000Z'],
    ['tournament', '2026-10-09T19:00:00.000Z'], // today's 19:00 has already started
  ]);
  // kings_hunt is disabled by EnableKingsHunt=false; a bad StartUtc and a disabled slot are skipped.
  assert.equal(ev.upcoming[1].end - ev.upcoming[1].start, 90 * 60000);
});

test('active events: only running ones of known kinds, tournament leaders sorted', async () => {
  const data = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config'), now: () => NOW });
  const ev = await data.realmEvents();
  assert.equal(ev.known, true);
  assert.deepEqual(ev.active.map((a) => a.kind), ['tournament']); // the truce ended; dragon_raid is unknown
  assert.deepEqual(ev.active[0].leaders.map((l) => [l.name, l.kills]), [['Ysolde Corvane', 6], ['Aldric Varrow', 4], ['Tamsin', 1]]);
  assert.deepEqual(cleanActiveEvents(null, NOW), []);
});

test('houses file: leaders, officers, record, treaties; member ids are never exposed', async () => {
  const data = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config') });
  const { houses, treaties, error } = await data.houses();
  assert.equal(error, null);
  const v = houses.find((h) => h.name === 'Varrow');
  assert.deepEqual(v.leaders, ['Aldric Varrow']);
  assert.deepEqual(v.officers, ['Bryn']);
  assert.equal(v.members, 3);
  assert.equal(v.treatiesBroken, 1);
  assert.equal(v.swornSince, null);
  assert.equal(houses.find((h) => h.name === 'Ashgrove').founded, Date.parse('2026-09-27T20:15:02Z'));
  assert.equal(treaties.length, 2);
  assert.ok(!JSON.stringify(houses).includes('7656119'), 'Steam ids must not leak');
});

test('missing files are fine; a half-written file keeps the last good copy', async () => {
  const dir = tmpDir();
  const dataDir = join(dir, 'data');
  mkdirSync(dataDir);
  const data = createRealmData({ dataDir, configDir: join(dir, 'config'), now: () => NOW });
  const empty = await data.realmEvents();
  assert.deepEqual([empty.active, empty.upcoming, empty.known, empty.error], [[], [], false, null]);
  assert.equal((await data.houses()).error, 'missing');

  copyFileSync(join(FIXTURES, 'data', 'RealmHouses.json'), join(dataDir, 'RealmHouses.json'));
  assert.equal((await data.houses()).houses.length, 3);
  writeFileSync(join(dataDir, 'RealmHouses.json'), '{"Houses": [ {"Name": "Var');
  const after = await data.houses();
  assert.equal(after.houses.length, 3, 'last good copy kept');
  assert.match(after.error, /unreadable/);
});

test('upcomingFromSchedule ignores garbage config', () => {
  assert.deepEqual(upcomingFromSchedule(null, NOW), []);
  assert.deepEqual(upcomingFromSchedule({ Schedule: 'nope' }, NOW), []);
  assert.deepEqual(upcomingFromSchedule({ Schedule: [{ Event: 'truce', Days: ['Daily'], StartUtc: '7:05', DurationMinutes: 99999 }] }, NOW).map((u) => (u.end - u.start) / 60000), [1440]);
});
