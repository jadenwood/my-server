'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const FD = require('../lib/shared/feed.js');

const NOW = Date.parse('2026-10-02T12:00:00Z');

test('nextFromState reads next / nextEvent and refuses past, far or malformed entries', () => {
  assert.deepEqual(FD.nextFromState({ next: { title: 'Crown Night', at: '2026-10-03T19:00:00Z' } }, NOW), { title: 'Crown Night', at: '2026-10-03T19:00:00.000Z' });
  assert.deepEqual(FD.nextFromState({ nextEvent: { name: 'Tourney', startsAt: '2026-10-02T13:00:00Z' } }, NOW), { title: 'Tourney', at: '2026-10-02T13:00:00.000Z' });
  assert.equal(FD.nextFromState({ next: { title: 'Old', at: '2026-10-01T00:00:00Z' } }, NOW), null);
  assert.equal(FD.nextFromState({ next: { title: 'Far', at: '2027-06-01T00:00:00Z' } }, NOW), null);
  assert.equal(FD.nextFromState({ next: { title: '', at: '2026-10-03T00:00:00Z' } }, NOW), null);
  assert.equal(FD.nextFromState({ next: { title: 'x', at: 'soon' } }, NOW), null);
  assert.equal(FD.nextFromState({ next: [] }, NOW), null);
  assert.equal(FD.nextFromState(null, NOW), null);
  assert.equal(FD.nextFromState({ king: 'A' }, NOW), null);
});

test('nextFromState strips control characters', () => {
  const n = FD.nextFromState({ next: { title: 'Crown\u0000\nNight', at: '2026-10-03T19:00:00Z' } }, NOW);
  assert.equal(n.title, 'Crown Night');
});

test('mergeFeed merges servers newest first, tags the server and keeps the soonest next event', () => {
  const r = FD.mergeFeed(
    [
      { server: { id: 's1', name: 'Realm I' }, events: [{ id: 1, ts: '2026-10-01T10:00:00Z', type: 'coronation', title: 'A is crowned', detail: 'House A' }, { id: 2, ts: '2026-10-02T09:00:00Z', type: 'oath_sworn', title: 'B swears to A' }], state: { next: { title: 'Crown Night', at: '2026-10-04T19:00:00Z' } } },
      { server: { id: 's2', name: 'Realm II' }, events: [{ id: 7, ts: '2026-10-02T11:00:00Z', type: 'decree', title: 'Open Roads' }, { id: 'bad', ts: 'x', type: 'decree', title: 'skip' }], state: { next: { title: 'Tourney', at: '2026-10-03T19:00:00Z' } } },
      { server: { id: 's3', name: 'Realm III' }, events: null, state: null },
      null
    ],
    { limit: 2, now: NOW }
  );
  assert.deepEqual(r.events.map((e) => e.key), ['s2:7', 's1:2']);
  assert.equal(r.events[0].serverName, 'Realm II');
  assert.equal(r.next.title, 'Tourney');
  assert.equal(r.next.serverId, 's2');
});

test('mergeFeed with nothing gives an empty feed', () => {
  assert.deepEqual(FD.mergeFeed([], { now: NOW }), { events: [], next: null });
  assert.deepEqual(FD.mergeFeed(undefined, { now: NOW }), { events: [], next: null });
});
