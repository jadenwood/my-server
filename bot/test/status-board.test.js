import assert from 'node:assert/strict';
import { join } from 'node:path';
import test from 'node:test';
import { createRealmData } from '../src/realm-data.js';
import { createStateStore } from '../src/state-store.js';
import { createStatusBoard } from '../src/status-board.js';
import { FIXTURES, NOW, SAMPLE_STATE, discordError, fakeChannel, fakeChronicle, fakeClient, quietLogger, testConfig } from './helpers.js';

function setup({ channel = fakeChannel(), chronicle = fakeChronicle(), cfg = testConfig(), t = { now: NOW }, store } = {}) {
  const logger = quietLogger();
  store ??= createStateStore(cfg.stateFile, { logger });
  const realmData = createRealmData({ dataDir: join(FIXTURES, 'data'), configDir: join(FIXTURES, 'config'), now: () => t.now });
  const timers = { set: [], setTimeout(fn, ms) { this.set.push(ms); return { fn, ms }; }, clearTimeout() {} };
  const board = createStatusBoard({ client: fakeClient(channel), cfg, chronicle, realmData, store, logger, now: () => t.now, timers });
  return { board, channel, chronicle, store, logger, t, timers, cfg };
}

test('first tick posts the status message and remembers its id', async () => {
  const s = setup();
  assert.equal(await s.board.tick(), 'posted');
  assert.equal(s.channel.sent.length, 1);
  assert.deepEqual(s.channel.sent[0].allowedMentions, { parse: [] });
  assert.match(JSON.stringify(s.channel.sent[0].embeds), /Aldric Varrow/);
  assert.equal(s.store.data.status.channelId, s.cfg.status.channelId);
  assert.ok(s.store.data.status.messageId);
});

test('unchanged content is not re-sent; a change edits the same message', async () => {
  const s = setup();
  await s.board.tick();
  assert.equal(await s.board.tick(), 'unchanged');
  s.chronicle.stateValue = { ...SAMPLE_STATE, updated: '2026-10-02T19:31:10Z' };
  assert.equal(await s.board.tick(), 'unchanged', 'only the timestamp moved');
  s.chronicle.stateValue = { ...SAMPLE_STATE, online: 24 };
  assert.equal(await s.board.tick(), 'edited');
  assert.equal(s.channel.sent.length, 1);
  assert.equal(s.channel.edits.length, 1);
  assert.match(JSON.stringify(s.channel.edits[0].body), /\*\*24\*\*/);
});

test('even unchanged, the message is refreshed every 10 minutes', async () => {
  const s = setup();
  await s.board.tick();
  s.t.now += 11 * 60000;
  assert.equal(await s.board.tick(), 'edited');
});

test('a restart edits the message from the state file instead of posting a new one', async () => {
  const channel = fakeChannel();
  const first = setup({ channel });
  await first.board.tick();
  await first.store.save();
  const store = createStateStore(first.store.path, { logger: quietLogger() });
  await store.load();
  const second = setup({ channel, store, cfg: first.cfg });
  assert.equal(await second.board.tick(), 'edited');
  assert.equal(channel.sent.length, 1);
});

test('a deleted status message is posted again', async () => {
  const s = setup();
  await s.board.tick();
  s.channel.stored.clear();
  s.t.now += 11 * 60000;
  assert.equal(await s.board.tick(), 'posted');
  assert.equal(s.channel.sent.length, 2);
});

test('a saved message written by someone else is never edited', async () => {
  const s = setup();
  await s.board.tick();
  const msg = s.channel.stored.get(s.store.data.status.messageId);
  msg.author = { id: 'someone-else' };
  s.t.now += 11 * 60000;
  assert.equal(await s.board.tick(), 'posted');
});

test('Chronicle down: shows "The ravens are late" and logs once', async () => {
  const s = setup({ chronicle: fakeChronicle({ down: true }) });
  await s.board.tick();
  assert.match(JSON.stringify(s.channel.sent[0].embeds), /ravens are late/);
  s.t.now += 11 * 60000;
  await s.board.tick();
  assert.equal(s.logger.lines.filter((l) => /Chronicle unavailable/.test(l)).length, 1);
  s.chronicle.down = false;
  assert.equal(await s.board.tick(), 'edited');
});

test('missing permissions back off (doubling, capped at 10 min) and log once', async () => {
  const s = setup();
  s.channel.failSend = discordError(50013, 'Missing Permissions');
  assert.equal(await s.board.tick(), 'error');
  assert.equal(await s.board.tick(), 'error');
  assert.equal(s.board.failures, 2);
  assert.equal(s.board.delayMs(), 60000 * 4);
  for (let k = 0; k < 6; k++) await s.board.tick();
  assert.equal(s.board.delayMs(), 10 * 60000);
  assert.equal(s.logger.lines.filter((l) => /Send Messages/.test(l)).length, 1);
  s.channel.failSend = null;
  assert.equal(await s.board.tick(), 'posted');
  assert.equal(s.board.delayMs(), 60000);
});

test('unknown channel and non-text channel are explained', async () => {
  const s = setup({ channel: null });
  assert.equal(await s.board.tick(), 'error');
  assert.ok(s.logger.lines.some((l) => /check REALM_STATUS_CHANNEL_ID/.test(l)));
  const voice = setup({ channel: { id: 'voice' } });
  await voice.board.tick();
  assert.ok(voice.logger.lines.some((l) => /not a text channel/.test(l)));
});

test('start() ticks then schedules; stop() ends it; no channel configured = off', async () => {
  const s = setup();
  assert.equal(await s.board.start(), true);
  assert.equal(s.channel.sent.length, 1);
  assert.deepEqual(s.timers.set, [60000]);
  s.board.stop();
  const off = setup({ cfg: testConfig({ status: { channelId: null, intervalSec: 60 } }) });
  assert.equal(await off.board.start(), false);
  assert.equal(off.channel.sent.length, 0);
});

test('overlapping ticks do not double-post', async () => {
  const s = setup();
  const [a, b] = await Promise.all([s.board.tick(), s.board.tick()]);
  assert.deepEqual([a, b].sort(), ['busy', 'posted']);
  assert.equal(s.channel.sent.length, 1);
});
