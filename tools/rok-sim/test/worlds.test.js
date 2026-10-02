'use strict';

// World slots, the two ready lines, and the programs that can hold the game port (docs/worlds.md).

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const dgram = require('dgram');
const { spawn } = require('child_process');
const H = require('./helpers');
const { lockHeld, SlotManager } = require('../lib/slots');
const { Watchdog, GameClient, BIN } = require('../lib/watchdog');

const cfgSlot = (cfg) => /^worldSlot = '(-?\d+)'/m.exec(fs.readFileSync(cfg, 'utf8'))[1];
const infoLines = (dir) => H.readGameLog(dir).split('\n').filter((l) => /\[(Info|Warn|Error)\]/.test(l)).map((l) => l.replace(/^\[\d{6}-\d{6}\] \[\w+\]\s+/, '').trim());

test('ready lines: "Server for N players started on port P." then "Game has started."', async () => {
  const { dir, game } = await H.preparedFolder();
  const s = await H.startReady(dir);
  try {
    const lines = infoLines(dir);
    const a = lines.indexOf(`Server for 30 players started on port ${game}.`);
    const b = lines.indexOf('Game has started.');
    assert.ok(a >= 0 && b > a, lines.join('\n'));
    assert.ok(lines.indexOf('Save slot located at: Saves/Slot0') < a, 'the slot is chosen before the world loads');
  } finally {
    await H.stopSim(s);
  }
});

test('the same world is loaded again on the next start (worldSlot written back)', async () => {
  const { dir, cfg } = await H.preparedFolder();
  assert.equal(cfgSlot(cfg), '-1');
  const s1 = await H.startReady(dir);
  assert.equal(s1.worldSlot, 0);
  assert.equal(s1.worldIsNew, true);
  assert.equal(cfgSlot(cfg), '0');
  const lock = path.join(dir, 'Saves', 'Slot0', 'Session.lock');
  assert.equal(lockHeld(lock), true, 'a running server holds its slot');
  const e1 = H.onceEvent(s1, 'exit');
  s1.shutdown('test');
  await e1;
  assert.equal(fs.existsSync(lock), false, 'a clean stop removes Session.lock');
  const s2 = await H.startReady(dir);
  try {
    assert.equal(s2.worldSlot, 0);
    assert.equal(s2.worldIsNew, false);
    assert.deepEqual(new SlotManager(path.join(dir, 'Saves')).list(), [0]);
  } finally {
    await H.stopSim(s2);
  }
});

test('a crashed server leaves Session.lock behind but does not lock the world', async () => {
  const { dir } = await H.preparedFolder();
  const s1 = await H.startReady(dir);
  await H.stopSim(s1); // kill: no save, no unlock
  const lock = path.join(dir, 'Saves', 'Slot0', 'Session.lock');
  assert.ok(fs.existsSync(lock));
  assert.equal(lockHeld(lock), false);
  const s2 = await H.startReady(dir);
  try {
    assert.equal(s2.worldSlot, 0, 'the same world loads after a crash');
  } finally {
    await H.stopSim(s2);
  }
});

// The owner's problem (2026-10-02): a second server from the same folder (a leftover, or one the
// Server.exe watchdog relaunched) still holds the world. The new start logs "Could not load world N",
// creates a NEW slot, then dies on the busy port, and leaves worldSlot = -1 for every later start.
test('a world held by another server: "Could not load world N", a new slot is left behind, worldSlot = -1', async () => {
  const { dir, cfg } = await H.preparedFolder();
  const first = await H.startReady(dir);
  try {
    assert.equal(cfgSlot(cfg), '0');
    const second = H.makeSim(dir);
    const exit = H.onceEvent(second, 'exit');
    second.start();
    assert.equal(await exit, 0);
    const lines = infoLines(dir);
    assert.ok(lines.includes('Could not load world 0. Loading new world instead.'), lines.join('\n'));
    assert.ok(lines.includes('Save slot located at: Saves/Slot1'));
    assert.ok(lines.some((l) => /The port \d+ is already being used by another application\./.test(l)));
    assert.ok(!lines.includes('Game has started.') || lines.filter((l) => l === 'Game has started.').length === 1, 'only the first server started');
    assert.ok(fs.existsSync(path.join(dir, 'Saves', 'Slot1')), 'the failed start left Slot1 behind');
    assert.equal(cfgSlot(cfg), '-1', 'and the next start would create yet another world');
  } finally {
    await H.stopSim(first);
  }
  // With the clash gone, the game makes a new world (Slot2), unless someone puts worldSlot back.
  const third = await H.startReady(dir);
  try {
    assert.equal(third.worldSlot, 2);
    assert.equal(third.worldIsNew, true);
  } finally {
    await H.stopSim(third);
  }
});

test('--sim-failed-slot delete removes a new slot whose world never loaded', async () => {
  const { dir, game } = await H.preparedFolder();
  const blocker = dgram.createSocket('udp4');
  await new Promise((r) => blocker.bind(game, '0.0.0.0', r));
  try {
    const s = H.makeSim(dir, [], { failedSlot: 'delete' });
    const exit = H.onceEvent(s, 'exit');
    s.start();
    await exit;
    assert.equal(fs.existsSync(path.join(dir, 'Saves', 'Slot0')), false);
  } finally {
    blocker.close();
  }
});

test('worldSlot pointing at a slot folder with nothing in it: "Could not load game slot N+1."', async () => {
  const { dir } = await H.preparedFolder({ worldSlot: 4 });
  fs.mkdirSync(path.join(dir, 'Saves', 'Slot4'), { recursive: true });
  const s = H.makeSim(dir);
  const exit = H.onceEvent(s, 'exit');
  s.start();
  assert.equal(await exit, 0);
  const lines = infoLines(dir);
  assert.ok(lines.includes('Could not load game slot 5.'), lines.join('\n'));
  assert.ok(lines.includes('Ending game...'));
});

test('worldSlot pointing at a missing folder creates a new world in exactly that slot', async () => {
  const { dir, cfg } = await H.preparedFolder({ worldSlot: 7 });
  const s = await H.startReady(dir);
  try {
    assert.equal(s.worldSlot, 7);
    assert.equal(s.worldIsNew, true);
    assert.equal(cfgSlot(cfg), '7');
  } finally {
    await H.stopSim(s);
  }
});

test('watchdog stand-in relaunches the server about relaunchMs after it ends', { timeout: 30000 }, async () => {
  const { dir, game } = await H.preparedFolder();
  const w = new Watchdog({ cwd: dir, argv: ['-batchmode', '-nographics', '--sim-boot-ms', '10', '--sim-load-ms', '30'], relaunchMs: 300 });
  const launches = [];
  w.on('launch', (l) => launches.push(l));
  try {
    w.start();
    await H.until(() => /Game has started\./.test(H.readGameLog(dir)), 8000, 'first server up');
    const firstPid = w.childPid;
    process.kill(firstPid, 'SIGKILL');
    const t0 = Date.now();
    await H.until(() => launches.length === 2, 5000, 'relaunch');
    assert.ok(Date.now() - t0 >= 250, 'not before relaunchMs');
    assert.notEqual(w.childPid, firstPid);
    await H.until(() => H.readGameLog(dir).split('Game has started.').length === 3, 8000, 'second server up');
    // The relaunched server loads the same world: the killed one left no live lock.
    assert.equal(/^worldSlot = '(-?\d+)'/m.exec(fs.readFileSync(path.join(dir, 'Configuration', 'ServerSettings.cfg'), 'utf8'))[1], '0');
    // And it holds the game port again.
    const probe = dgram.createSocket('udp4');
    const err = await new Promise((r) => {
      probe.once('error', (e) => r(e.code));
      probe.bind(game, '0.0.0.0', () => r(null));
    });
    try {
      probe.close();
    } catch {
      /* not bound */
    }
    assert.equal(err, 'EADDRINUSE');
  } finally {
    await w.stop();
  }
});

test('rok-sim watchdog and rok-sim client as programs', { timeout: 30000 }, async () => {
  const port = await H.freePort();
  const client = spawn(process.execPath, [BIN, 'client', String(port)], { stdio: ['ignore', 'pipe', 'ignore'] });
  let out = '';
  client.stdout.on('data', (c) => (out += c));
  try {
    await H.until(() => /CLIENT \d+ UDP \d+/.test(out), 5000, 'client up');
    const s = dgram.createSocket('udp4');
    const code = await new Promise((r) => {
      s.once('error', (e) => r(e.code));
      s.bind(port, '0.0.0.0', () => r(null));
    });
    try {
      s.close();
    } catch {
      /* not bound */
    }
    assert.equal(code, 'EADDRINUSE', 'the game client holds the port');
  } finally {
    client.kill('SIGTERM');
  }

  const { dir } = await H.preparedFolder();
  const wd = spawn(process.execPath, [BIN, 'watchdog', '-batchmode', '--sim-boot-ms', '10', '--sim-load-ms', '30', '--sim-relaunch-ms', '200'], { cwd: dir, stdio: ['ignore', 'pipe', 'ignore'] });
  let wout = '';
  wd.stdout.on('data', (c) => (wout += c));
  try {
    await H.until(() => /LAUNCH (\d+)/.test(wout), 5000, 'launch');
    assert.match(wout, new RegExp(`WATCHDOG ${wd.pid}`));
    const pid = Number(/LAUNCH (\d+)/.exec(wout)[1]);
    await H.until(() => /Game has started\./.test(H.readGameLog(dir)), 8000, 'server up');
    process.kill(pid, 'SIGKILL');
    await H.until(() => (wout.match(/LAUNCH \d+/g) || []).length === 2, 5000, 'relaunched');
  } finally {
    const done = new Promise((r) => wd.once('exit', r));
    wd.kill('SIGTERM');
    await done;
  }
});

test('GameClient stand-in makes a server start fail on the busy port', async () => {
  const { dir, game } = await H.preparedFolder();
  const c = await new GameClient({ port: game }).start();
  try {
    const s = H.makeSim(dir);
    const exit = H.onceEvent(s, 'exit');
    s.start();
    assert.equal(await exit, 0);
    assert.match(H.readGameLog(dir), new RegExp(`The port ${game} is already being used by another application\\.`));
  } finally {
    await c.close();
  }
});
