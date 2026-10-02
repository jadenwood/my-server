// Unit tests for the scene data model (public/assets/model.js) against the fixtures. Run: npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import {
  buildWarBoard, layoutForest, chooseCountdown, nextOccurrence, nextSlot, claimWindow, parseSlot, parseWindowsParam,
  parseEventsParam, normalizeSchedule, parseDurationParam, splitDuration, formatUtc, AlertQueue, eventMentions,
  parseKitParams, parseCoronation, liveRealmEvent, DEFAULT_REBELLION_WINDOWS, DEFAULT_REALM_EVENTS, MINUTE, DAY,
} from '../public/assets/model.js';

const read = async (rel) => JSON.parse(await readFile(fileURLToPath(new URL(rel, import.meta.url)), 'utf8'));
const state = await read('../fixtures/state.json');
const events = await read('../fixtures/events.json');
const NOW = Date.parse('2026-10-01T21:05:00Z'); // a Thursday, the fixtures' "updated"
const iso = (ms) => new Date(ms).toISOString().replace('.000Z', 'Z');

test('war board: nodes, crown, liege lines', () => {
  const b = buildWarBoard(state, events, { now: NOW });
  assert.equal(b.nodes.length, 7);
  assert.equal(b.crownHouse, 'Varrow');
  assert.equal(b.king, 'Aldric Varrow');
  assert.deepEqual(b.nodes.filter((n) => n.crown).map((n) => n.name), ['Varrow']);
  assert.deepEqual(b.lieges.map((l) => `${l.vassal}>${l.liege}`).sort(), ['Ashgrove>Varrow', 'Dunmere>Corvane', 'Merrin>Varrow']);
  assert.equal(b.nodes.find((n) => n.name === 'Varrow').vassals, 2);
});

test('war board: treaties from the event wording, broken ones shown for a day', () => {
  const b = buildWarBoard(state, events, { now: NOW });
  const t = b.treaties.map((x) => `${x.a}|${x.b}|${x.status}`);
  assert.deepEqual(t, ['Merrin|Corvane|active', 'Ashgrove|Halloran|active', 'Corvane|Halloran|broken']);
  const ah = b.treaties[1];
  assert.equal(iso(ah.expiresAt), '2026-10-06T12:30:00Z'); // 5 days after signing
  assert.equal(b.treaties[2].breaker, 'Halloran');
  // 25 hours later the broken treaty has faded and Merrin-Corvane (3 days) is still up.
  const later = buildWarBoard(state, events, { now: NOW + 25 * 60 * MINUTE });
  assert.deepEqual(later.treaties.map((x) => x.status), ['active', 'active']);
  // After the 3-day term the treaty expires.
  const muchLater = buildWarBoard(state, events, { now: Date.parse('2026-10-04T17:00:00Z') });
  assert.deepEqual(muchLater.treaties.map((x) => `${x.a}|${x.b}`), ['Ashgrove|Halloran']);
});

test('war board: claims, windows and marks', () => {
  const b = buildWarBoard(state, events, { now: NOW });
  assert.equal(b.claims.length, 1, 'the Varrow claim ended with its rebellion');
  const c = b.claims[0];
  assert.equal(c.house, 'Dunmere');
  assert.equal(c.status, 'pending');
  assert.equal(c.against, 'Aldric Varrow');
  assert.equal(c.againstHouse, 'Varrow');
  assert.equal(iso(c.windowStart), '2026-10-03T19:00:00Z'); // "opens Saturday 19:00 UTC"
  assert.equal(iso(c.windowEnd), '2026-10-03T20:30:00Z'); // the Saturday window is 90 minutes
  const dun = b.nodes.find((n) => n.name === 'Dunmere');
  assert.equal(dun.oathsBroken, 1);
  assert.equal(dun.claiming, true);
  assert.equal(dun.betrayedRecently, true);
  assert.equal(b.nodes.find((n) => n.name === 'Halloran').treatiesBroken, 1);
  // During the window the claim reads as active even before the plugin logs rebellion_started.
  const during = buildWarBoard(state, events, { now: Date.parse('2026-10-03T19:10:00Z') });
  assert.equal(during.claims[0].status, 'active');
  // A claim the plugin never closed lapses after its window.
  assert.equal(buildWarBoard(state, events, { now: Date.parse('2026-10-03T21:00:00Z') }).claims.length, 0);
});

test('war board: rebellion_started sets the end from "until HH:MM UTC"', () => {
  const extra = events.concat([{ id: 99, ts: '2026-10-03T19:00:03Z', type: 'rebellion_started', title: 'House Dunmere rises',
    detail: 'The rebellion of House Dunmere has begun. The throne may be contested until 20:30 UTC.', actors: ['Hale Dunmere'] }]);
  const b = buildWarBoard(state, extra, { now: Date.parse('2026-10-03T19:30:00Z') });
  assert.equal(b.claims[0].status, 'active');
  assert.equal(iso(b.claims[0].windowEnd), '2026-10-03T20:30:00Z');
});

test('war board: unknown houses, cycles and junk do not break it', () => {
  const b = buildWarBoard({ house: 'Nope', houses: [{ name: 'A', liege: 'B', members: 1 }, { name: 'B', liege: 'A', members: 1 }, null, {}] },
    [null, { id: 1, type: 'treaty_signed', title: 'House A and House Ghost sign a treaty' }, { id: 2, type: 'claim_declared', title: 'garbage' }],
    { now: NOW });
  assert.equal(b.nodes.length, 2);
  assert.equal(b.treaties.length, 0, 'treaties with a disbanded house are dropped');
  const pos = layoutForest(b.nodes);
  assert.ok(pos.A && pos.B, 'a liege cycle still gets positions');
  assert.deepEqual(buildWarBoard(null, null, { now: NOW }).nodes, []);
});

test('layout: crown realm in the middle, vassals below their liege', () => {
  const b = buildWarBoard(state, events, { now: NOW });
  const p = layoutForest(b.nodes);
  assert.equal(p.Varrow.y, 0);
  assert.equal(p.Ashgrove.y, 1);
  assert.equal(p.Dunmere.y, 1);
  assert.ok(p.Halloran.x < p.Varrow.x && p.Varrow.x < p.Corvane.x, 'the crown root sits between the others');
  assert.ok(p.Ashgrove.x < p.Merrin.x, 'larger vassal first');
  for (const v of Object.values(p)) assert.ok(v.x > 0 && v.x < 1);
});

test('schedule: slots, occurrences and the realm offset', () => {
  assert.deepEqual(parseSlot('Sat@19:00+90'), { day: 6, minuteOfDay: 1140, minutes: 90 });
  assert.equal(parseSlot('Funday@19:00'), null);
  assert.equal(parseSlot('Monday@25:00'), null);
  assert.equal(parseWindowsParam('Wednesday@19:00+60,bad,Saturday@19:00+90').length, 2);
  assert.deepEqual(parseEventsParam('Crown Night=Saturday@19:00+90;x').map((e) => e.name), ['Crown Night']);
  const wed = { day: 3, minuteOfDay: 19 * 60, minutes: 60 };
  assert.equal(iso(nextOccurrence(wed, NOW, 0)), '2026-10-07T19:00:00Z');
  // Realm time is UTC+2: Wednesday 19:00 realm time is 17:00 UTC.
  assert.equal(iso(nextOccurrence(wed, NOW, 2)), '2026-10-07T17:00:00Z');
  // Realm time UTC-5 crossing midnight: Thursday 02:00 realm is Thursday 07:00 UTC.
  assert.equal(iso(nextOccurrence({ day: 4, minuteOfDay: 120, minutes: 30 }, Date.parse('2026-10-01T06:00:00Z'), -5)), '2026-10-01T07:00:00Z');
  const open = nextSlot(DEFAULT_REBELLION_WINDOWS, Date.parse('2026-10-03T19:30:00Z'), 0);
  assert.equal(open.open, true);
  assert.equal(iso(open.end), '2026-10-03T20:30:00Z');
  const next = nextSlot(DEFAULT_REBELLION_WINDOWS, NOW, 0);
  assert.equal(next.open, false);
  assert.equal(iso(next.start), '2026-10-03T19:00:00Z');
});

test('schedule: the example file equals the plugin defaults', async () => {
  const s = normalizeSchedule(await read('../schedule.example.json'));
  assert.equal(s.offsetHours, 0);
  assert.deepEqual(s.windows, DEFAULT_REBELLION_WINDOWS);
  assert.deepEqual(s.events.map(({ name, day, minuteOfDay, minutes }) => ({ name, day, minuteOfDay, minutes })),
    DEFAULT_REALM_EVENTS.map(({ name, day, minuteOfDay, minutes }) => ({ name, day, minuteOfDay, minutes })));
  assert.deepEqual(normalizeSchedule({ utcOffsetHours: 99, rebellionWindows: [{ day: 'x' }], events: 'no' }), { offsetHours: 0, windows: [], events: null });
});

test('claimWindow handles missing or odd wording', () => {
  assert.deepEqual(claimWindow({ ts: '2026-10-01T00:00:00Z', detail: 'The rebellion window opens at dusk.' }), { start: null, end: null });
  const w = claimWindow({ ts: '2026-10-01T00:00:00Z', detail: 'The rebellion opens Wednesday 19:00 UTC.' });
  assert.equal(iso(w.start), '2026-10-07T19:00:00Z');
  assert.equal(w.end - w.start, 60 * MINUTE);
});

test('countdown: claim first, then the soonest window or event', () => {
  const board = buildWarBoard(state, events, { now: NOW });
  const auto = chooseCountdown({ board, events, now: NOW });
  assert.equal(auto.kind, 'claim');
  assert.equal(auto.house, 'Dunmere');
  assert.equal(iso(auto.at), '2026-10-03T19:00:00Z');
  const ev = chooseCountdown({ board, events, now: NOW, mode: 'event' });
  assert.equal(ev.kind, 'event');
  assert.match(ev.title, /Royal Tournament/);
  assert.equal(iso(ev.at), '2026-10-02T19:00:00Z');
  // Filtered to Halloran (no claim): the next window.
  const reb = chooseCountdown({ board, events, now: NOW, mode: 'rebellion', houses: ['Halloran'] });
  assert.equal(reb.kind, 'window');
  // Auto without claims picks the soonest: Friday's tournament comes before Saturday's window.
  const soon = chooseCountdown({ board: { claims: [] }, events: [], now: NOW });
  assert.equal(soon.kind, 'event');
  const custom = chooseCountdown({ board, events, now: NOW, mode: 'custom', custom: { at: NOW + 10 * MINUTE, label: 'We ride' } });
  assert.equal(custom.title, 'We ride');
  assert.equal(chooseCountdown({ board, events, now: NOW, mode: 'custom' }), null);
  // An open window outranks future events.
  const live = chooseCountdown({ board: { claims: [] }, events: [], now: Date.parse('2026-10-07T19:10:00Z') });
  assert.equal(live.kind, 'window-live');
});

test('countdown: a live realm event counts to its end', () => {
  const evs = [{ id: 1, ts: '2026-10-03T19:00:00Z', type: 'event_started', title: 'Crown Night begins' }];
  const live = liveRealmEvent(evs, DEFAULT_REALM_EVENTS, Date.parse('2026-10-03T19:30:00Z'));
  assert.equal(iso(live.end), '2026-10-03T20:30:00Z');
  const ended = evs.concat([{ id: 2, ts: '2026-10-03T20:00:00Z', type: 'event_ended', title: 'Crown Night ends' }]);
  assert.equal(liveRealmEvent(ended, DEFAULT_REALM_EVENTS, Date.parse('2026-10-03T20:01:00Z')), null);
  const c = chooseCountdown({ board: { claims: [] }, events: evs, now: Date.parse('2026-10-03T19:30:00Z'), mode: 'event' });
  assert.equal(c.kind, 'event-live');
});

test('durations and formatting', () => {
  assert.equal(parseDurationParam('15m'), 15 * MINUTE);
  assert.equal(parseDurationParam('1h30m'), 90 * MINUTE);
  assert.equal(parseDurationParam('45'), 45 * MINUTE);
  assert.equal(parseDurationParam('2d'), 2 * DAY);
  assert.equal(parseDurationParam('soon'), null);
  assert.equal(parseDurationParam('5m tomorrow'), null);
  assert.deepEqual(splitDuration(DAY + 3661000), { d: 1, h: 1, m: 1, s: 1 });
  assert.deepEqual(splitDuration(-5), { d: 0, h: 0, m: 0, s: 0 });
  assert.equal(formatUtc(Date.parse('2026-10-03T19:00:00Z')), 'Saturday 19:00 UTC');
});

test('breaking news queue: filter, de-duplicate, urgent first, cap', () => {
  const q = new AlertQueue({ max: 3 });
  // events.json has: claim 12, rebellion_started 13, abdication 14, rebellion_ended 16, oath_broken 19, treaty_broken 21, claim 22
  assert.equal(q.push(events), 7);
  assert.equal(q.length, 3);
  assert.equal(q.dropped, 4);
  // The three plain alerts go first, then the oldest urgent one.
  assert.deepEqual(q.items.map((e) => e.type), ['abdication', 'oath_broken', 'treaty_broken']);
  assert.equal(q.push(events), 0, 'already seen');
  const h = new AlertQueue({ houses: ['Halloran'] });
  h.push(events);
  assert.deepEqual(h.items.map((e) => e.id), [21]);
  const t = new AlertQueue({ types: ['decree'] });
  t.push(events);
  assert.deepEqual(t.items.map((e) => e.id), [24]);
});

test('house filter matches whole names only', () => {
  assert.equal(eventMentions({ title: 'House Varrow rises' }, ['varrow']), true);
  assert.equal(eventMentions({ title: 'House Varrowmere rises' }, ['Varrow']), false);
  assert.equal(eventMentions({ title: 'x', actors: ['Hale Dunmere'] }, ['Dunmere']), true);
  assert.equal(eventMentions({ title: 'anything' }, []), true);
  assert.equal(eventMentions({ title: 'House C.R.O.W rises' }, ['C.R.O.W']), true, 'regex characters are escaped');
});

test('coronation parsing: plugin wording and the older sample wording', () => {
  assert.deepEqual(parseCoronation({ title: 'Aldric Varrow of Varrow takes the throne' }), { king: 'Aldric Varrow', house: 'Varrow' });
  assert.deepEqual(parseCoronation({ title: 'Wat of no house takes the throne' }), { king: 'Wat', house: null });
  assert.deepEqual(parseCoronation({ title: 'Aldric Varrow takes the throne', detail: 'House Varrow now holds the crown.' }), { king: 'Aldric Varrow', house: 'Varrow' });
});

test('URL params: scale, transparent, house, motion, clamps', () => {
  const k = parseKitParams('?scale=9&transparent=0&house=House Varrow, Corvane&motion=0&hold=1&replay=3&now=2026-10-01T21:05:00Z');
  assert.equal(k.scale, 4);
  assert.equal(k.transparent, false);
  assert.deepEqual(k.houses, ['Varrow', 'Corvane']);
  assert.equal(k.reducedMotion, true);
  assert.equal(k.hold, 3);
  assert.equal(k.replay, 3);
  assert.equal(k.frozenNow, NOW);
  const d = parseKitParams('', { transparent: false, hold: 14 });
  assert.equal(d.scale, 1);
  assert.equal(d.transparent, false);
  assert.equal(d.hold, 14);
  assert.equal(d.reducedMotion, false);
  assert.equal(d.frozenNow, null);
  assert.equal(parseKitParams('?scale=abc').scale, 1);
});

test('house colours match the chronicle overlay (chronicle/public/assets/common.js)', async () => {
  globalThis.location = { search: '' }; // common.js reads location.search at import time
  const chronicle = await import('../../chronicle/public/assets/common.js');
  const kit = await import('../public/assets/kit.js');
  for (const name of ['Varrow', 'Ashgrove', 'Corvane', 'Dunmere', 'Halloran', 'Merrin', 'Thornwick', 'é', '', 'House of Ünicode']) {
    assert.deepEqual(kit.dyeFor(name), chronicle.dyeFor(name), name);
  }
});

test('sigil keywords', async () => {
  const { sigilKind } = await import('../public/assets/sigils.js');
  assert.equal(sigilKind('Iron Stag'), 'stag');
  assert.equal(sigilKind('White Oak'), 'oak');
  assert.equal(sigilKind('Black Raven'), 'raven');
  assert.equal(sigilKind('Drowned Bell'), 'bell');
  assert.equal(sigilKind('Ember Hound'), 'hound');
  assert.equal(sigilKind('Silver Eel'), 'eel');
  assert.equal(sigilKind('Grey Heron'), null);
  assert.equal(sigilKind('Ashen Hound'), 'hound');
  assert.equal(sigilKind(null), null);
});
