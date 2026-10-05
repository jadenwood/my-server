'use strict';
// Ready detection and log-file choice (lib/readiness.js). The two ready lines and their order were
// seen on the owner's real server on 2026-10-02.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const RD = require('../lib/readiness');

test('ready only after "Server for N players started on port P." and then "Game has started."', () => {
  const t = new RD.ReadyTracker();
  assert.equal(t.feed('Initialize engine version: 5.1.2f1'), null, 'the Unity banner is far too early');
  assert.equal(t.feed('[251002-091500] [Info]   Game has started.'), null, 'out of order: not ready');
  assert.equal(t.ready, false);
  assert.equal(t.feed('[251002-091512] [Info]   Server for 30 players started on port 7350.'), 'listening');
  assert.deepEqual(t.listening, { maxPlayers: 30, port: 7350 });
  assert.equal(t.phase, 'listening');
  assert.equal(t.feed('[251002-091530] [Info]   Game has started.'), 'ready');
  assert.equal(t.ready, true);
  t.reset();
  assert.equal(t.phase, 'loading');
});

test('the admin console form of the lines counts too; chat does not', () => {
  const t = new RD.ReadyTracker();
  assert.equal(t.feed('Server for 120 players started on port 7360.'), 'listening');
  assert.equal(t.feed('[C] Wren: Game has started.'), null);
  assert.equal(t.feed('Wren: Game has started.'), null);
  assert.equal(t.feed('Game has started.'), 'ready');
});

test('the world slot and the "world in use" warning are read from the log', () => {
  const t = new RD.ReadyTracker();
  assert.equal(t.feed('[251002-091500] [Warn]   Could not load world 3. Loading new world instead.'), 'world-in-use');
  assert.equal(t.worldInUse, 3);
  assert.equal(t.feed('[251002-091500] [Info]   Save slot located at: Saves/Slot6'), 'slot');
  assert.equal(t.slot, 6);
  assert.equal(RD.slotFromPath('G:\\RealmTest\\server\\Saves\\Slot10\\'), 10);
  assert.equal(RD.slotFromPath('Saves/'), null);
});

test('pickGameLog: the newest Log[...].txt created after the start, never an old one or Unity\'s log', () => {
  const known = new Set(['Log[251002-115959].txt', 'Log[251001-080000].txt', 'realm-server.log']);
  const files = [
    // Older run whose 12-hour name sorts AFTER the new one ("11" > "01"): must not win.
    { name: 'Log[251002-115959].txt', birthtimeMs: 1000, mtimeMs: 9000 },
    { name: 'Log[251001-080000].txt', birthtimeMs: 500, mtimeMs: 500 },
    { name: 'realm-server.log', birthtimeMs: 9500, mtimeMs: 9900 },
    { name: 'Log[251002-010203].txt', birthtimeMs: 9600, mtimeMs: 9700 }
  ];
  assert.equal(RD.pickGameLog(files, { known }).name, 'Log[251002-010203].txt');
  // Nothing new yet.
  assert.equal(RD.pickGameLog(files.slice(0, 3), { known }), null);
  // Two new files (a server that restarted itself): the later one.
  const two = [...files, { name: 'Log[251002-010400].txt', birthtimeMs: 9800, mtimeMs: 9800 }];
  assert.equal(RD.pickGameLog(two, { known }).name, 'Log[251002-010400].txt');
  // No creation time on this file system: modification time decides.
  const nob = [{ name: 'Log[251002-120000].txt', birthtimeMs: 0, mtimeMs: 50 }, { name: 'Log[251002-010000].txt', birthtimeMs: 0, mtimeMs: 70 }];
  assert.equal(RD.pickGameLog(nob, {}).name, 'Log[251002-010000].txt');
  // Adopt: the newest existing log by modification time, old names allowed.
  assert.equal(RD.pickGameLog(files.slice(0, 3), { known, adopt: true }).name, 'Log[251002-115959].txt');
});
