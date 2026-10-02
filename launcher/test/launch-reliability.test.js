'use strict';
// Launch reliability end to end against the fake server (tools/rok-sim): ready lines, following the
// right log, a leftover server, the Server.exe watchdog, the game client on the port, adopting a
// running server, and starting the same world again after a failed start. Linux/macOS only (the
// ROK.exe shim is a script); skipped when tools/rok-sim is not in the tree.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const { spawn, execFileSync } = require('child_process');

const SIM = path.resolve(__dirname, '../../tools/rok-sim');
const skip = (!fs.existsSync(path.join(SIM, 'lib', 'sim.js')) && 'tools/rok-sim is not in this tree') || (process.platform === 'win32' && 'the ROK.exe shim is a script');
const H = skip ? null : require(path.join(SIM, 'test', 'helpers.js'));
const SL = skip ? null : require(path.join(SIM, 'lib', 'slots.js'));
const WD = skip ? null : require(path.join(SIM, 'lib', 'watchdog.js'));
const BIN = path.join(SIM, 'bin', 'rok-sim.js');

const { ServerManager } = require('../lib/server-process');
const { createCourt } = require('../lib/court-host');
const N = require('../lib/netcheck');
const PS = require('../lib/prestart');
const W = require('../lib/worlds');
const R = require('../lib/realm');

const FAST = ['--sim-boot-ms', '20', '--sim-load-ms', '60', '--sim-keepalive-ms', '200'];
const GAME_EXE = 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings\\ROK.exe';
const socketsOf = (game, steam) => [{ port: game, proto: 'udp', what: 'game' }, { port: game, proto: 'tcp', what: 'ping' }, { port: steam, proto: 'udp', what: 'Steam query' }];
const probeLock = async (f) => SL.lockHeld(f);

function spawnSim(dir, args) {
  const child = spawn(process.execPath, [BIN, '-batchmode', '-nographics', ...FAST, ...args], { cwd: dir, stdio: 'ignore' });
  child.done = new Promise((r) => child.once('exit', (code) => r(code)));
  return child;
}

async function killQuietly(child) {
  if (child && child.exitCode == null && child.signalCode == null) {
    child.kill('SIGKILL');
    await child.done;
  }
}

test('ServerManager: ready on the two ready lines, read from the newest log created after its start', { skip, timeout: 30000 }, async () => {
  const { dir } = await H.preparedFolder();
  execFileSync(process.execPath, [BIN, 'make-exe', dir]);
  // An older run's log whose 12-hour name sorts after today's, already holding both ready lines.
  fs.mkdirSync(path.join(dir, 'Logs'), { recursive: true });
  const old = 'Log[991231-115959].txt';
  fs.writeFileSync(path.join(dir, 'Logs', old), '[991231-235959] [Info]   Server for 30 players started on port 1.\r\n\n[991231-235959] [Info]   Game has started.\r\n\n');
  fs.writeFileSync(path.join(dir, 'Logs', 'realm-server.log'), 'Initialize engine version: from the last run\n');
  const yesterday = new Date(Date.now() - 86400000);
  fs.utimesSync(path.join(dir, 'Logs', 'realm-server.log'), yesterday, yesterday);
  const m = new ServerManager({ platform: 'linux', tailMs: 100 });
  const readyAt = [];
  m.on('ready', (st) => readyAt.push(st));
  try {
    await m.start(dir, 'ROK', { extraArgs: FAST });
    await H.until(() => m.ready, 15000, 'ready');
    const st = m.status();
    assert.equal(st.phase, 'ready');
    assert.equal(readyAt.length, 1);
    assert.notEqual(st.gameLog, path.join('Logs', old), 'never the older run\'s log');
    assert.match(st.gameLog, /^Logs[\\/]Log\[\d{6}-\d{6}\]\.txt$/);
    assert.equal(st.listening.maxPlayers, 30);
    assert.equal(st.world, 0, 'the slot from "Save slot located at:"');
    const text = m.lines.map((l) => l.text).join('\n');
    assert.ok(!/port 1\./.test(text), 'nothing from the old log was read');
    assert.ok(!/from the last run/.test(text), 'nothing from the last run\'s Unity log was read');
    assert.match(text, /The server is ready: "Game has started\." after "Server for 30 players started on port \d+\."/);
  } finally {
    await m.forceStop();
  }
});

test('leftover server from this folder: found, stopped cleanly over its console, world saved', { skip, timeout: 30000 }, async () => {
  const { dir, game, steam } = await H.preparedFolder();
  const cport = await H.freePort();
  const child = spawnSim(dir, ['-cport', String(cport), '--sim-cport-window-ms', '30000']);
  try {
    await H.until(() => /Game has started\./.test(H.readGameLog(dir)), 10000, 'leftover up');
    const deps = { listProcesses: async () => [{ pid: child.pid, name: 'ROK', path: path.join(dir, 'ROK.exe') }], heldSlots: (r) => W.heldSlots(r, { probeLock }) };
    const r = await PS.survey({ root: dir, sockets: socketsOf(game, steam), plannedCport: cport, consolePorts: [cport] }, deps);
    assert.equal(r.clear, false);
    assert.equal(r.verdict, 'leftover');
    assert.ok(r.busy.some((b) => b.port === game && b.proto === 'udp'), 'UDP game port seen busy by a bind test');
    assert.deepEqual(r.heldSlots, [0], 'its world lock is held');
    assert.equal(r.actions.stop.ok, true);
    assert.equal(r.actions.adopt.ok, true);
    const w = W.planWorld(await W.readWorlds(dir, { probeLock }), 0);
    assert.equal(w.action, 'blocked', 'a start now would have made a new world');

    const res = await PS.stopCleanly({ port: r.actions.stop.port, gamePort: game, watchMs: 600, pollMs: 100 });
    assert.deepEqual([res.stopped, res.relaunched], [true, false], res.message);
    assert.equal(await child.done, 0);
    assert.match(H.readGameLog(dir), /Server has shut down the server\.[\s\S]*Saving game\.\.\./);
    const after = await PS.survey({ root: dir, sockets: socketsOf(game, steam), consolePorts: [cport] }, { listProcesses: async () => [], heldSlots: (r2) => W.heldSlots(r2, { probeLock }) });
    assert.equal(after.clear, true);
  } finally {
    await killQuietly(child);
  }
});

test('Server.exe watchdog: classified, no stop offered; a stop is undone by the relaunch and reported', { skip, timeout: 40000 }, async () => {
  const { dir, game, steam } = await H.preparedFolder();
  const cport = await H.freePort();
  const w = new WD.Watchdog({ cwd: dir, argv: ['-batchmode', '-nographics', ...FAST, '-cport', String(cport), '--sim-cport-window-ms', '30000'], relaunchMs: 300 });
  try {
    w.start();
    await H.until(() => /Game has started\./.test(H.readGameLog(dir)), 10000, 'watchdog server up');
    // Elevated on the owner's PC: Steward cannot read either path.
    const procs = async () => [{ pid: process.pid, name: 'Server', path: '' }, { pid: w.childPid, name: 'ROK', path: '' }];
    const r = await PS.survey({ root: dir, sockets: socketsOf(game, steam), plannedCport: cport, consolePorts: [cport] }, { listProcesses: procs, heldSlots: (x) => W.heldSlots(x, { probeLock }) });
    assert.equal(r.verdict, 'watchdog');
    assert.equal(r.actions.stop.ok, false);
    assert.equal(r.actions.adopt.ok, false);
    assert.match(r.actions.stop.why, /starts ROK\.exe again about 2 seconds after it ends/);
    // What would happen if Steward stopped ROK.exe anyway: it comes back.
    const res = await PS.stopCleanly({ port: cport, gamePort: game, watchMs: 8000, pollMs: 100 });
    assert.equal(res.stopped, true);
    assert.equal(res.relaunched, true, res.message);
    assert.match(res.message, /Server\.exe watchdog started it again/);
    assert.equal(w.launches >= 2, true);
  } finally {
    await w.stop();
  }
});

test('the game client holding the game port', { skip, timeout: 20000 }, async () => {
  const { dir, game, steam } = await H.preparedFolder();
  const c = await new WD.GameClient({ port: game }).start();
  try {
    const r = await PS.survey(
      { root: dir, sockets: socketsOf(game, steam), consolePorts: [] },
      { portOwners: async (port, proto) => (port === game && proto === 'udp' ? [{ pid: process.pid, name: 'ROK', path: GAME_EXE }] : []), listProcesses: async () => [{ pid: process.pid, name: 'ROK', path: GAME_EXE }], heldSlots: async () => [] }
    );
    assert.equal(r.verdict, 'game-client');
    assert.equal(r.actions.stop.ok, false);
    assert.match(r.actions.stop.why, /Close Reign of Kings, start the server, then launch the game/);
    assert.equal(await N.probePort(game, 'udp'), 'in-use');
  } finally {
    await c.close();
  }
});

test('adopt: Steward attaches to a running server, reads its state, talks over its console and stops it', { skip, timeout: 40000 }, async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const child = spawnSim(dir, ['-cport', String(cport), '--sim-cport-window-ms', '30000', '--sim-players', 'Wren']);
  const m = new ServerManager({ platform: 'linux', tailMs: 100 });
  const court = createCourt({ userData: H.tmpDir('realm-court'), settings: { get: (k) => (k === 'serverExe' ? 'ROK' : null) }, instOf: (id) => ({ id }), rootOf: () => dir });
  court.adopt('s1', m);
  try {
    await H.until(() => /Wren has connected\./.test(H.readGameLog(dir)), 10000, 'server up with a player');
    m.adopt(dir, { pid: child.pid });
    assert.equal(m.isRunning(), true);
    court.attachConsole('s1', cport);
    await H.until(() => m.ready, 8000, 'ready from the running server\'s log');
    await H.until(() => m.consoleFeed, 8000, 'console connected');
    const st = m.status();
    assert.equal(st.adopted, true);
    assert.equal(st.pid, child.pid);
    assert.equal(st.world, 0);
    assert.deepEqual(st.players, ['Wren']);
    assert.throws(() => court.attachConsole('s1', cport), /already connected/);
    const reply = await m.sendCommand('/list');
    assert.equal(reply.via, 'console');
    // Force stop never kills a process Steward did not start.
    await m.forceStop();
    assert.equal(child.exitCode, null);
    assert.ok(m.lines.some((l) => /does not force-end a server it did not start/.test(l.text)));
    const exited = new Promise((r) => m.once('exit', r));
    m.stop(); // court-host sends /shutdown over the console
    const ex = await exited;
    assert.equal(ex.adopted, true);
    assert.equal(ex.requested, true);
    assert.equal(await child.done, 0);
    assert.equal(m.isRunning(), false);
  } finally {
    await killQuietly(child);
    if (m.isRunning()) m.onExit(null, null);
  }
});

// The owner's failed starts, replayed: a second server from the same folder makes the game skip the
// world in use, create Slot1 and reset worldSlot to -1. Steward sees it, and the next start loads
// world 0 again instead of making Slot2.
test('world continuity: "Could not load world 0" is caught, and the next start loads world 0 again', { skip, timeout: 40000 }, async () => {
  const { dir, cfg } = await H.preparedFolder();
  execFileSync(process.execPath, [BIN, 'make-exe', dir]);
  const first = await H.startReady(dir); // in this test process; holds Slot0
  const remembered = await W.runningSlot(dir);
  assert.equal(remembered, 0);
  const m = new ServerManager({ platform: 'linux', tailMs: 100 });
  const inUse = [];
  m.on('world-in-use', (n) => inUse.push(n));
  try {
    assert.equal(W.planWorld(await W.readWorlds(dir, { probeLock }), remembered).action, 'blocked');
    // The game names its log by the second; let the second server get its own file.
    await H.wait(1100);
    // Started anyway (as on the owner's PC): the second server falls onto a new world, then dies on the port.
    const exited = new Promise((r) => m.once('exit', r));
    await m.start(dir, 'ROK', { extraArgs: FAST });
    await exited;
    await H.until(() => inUse.length === 1, 3000, 'world-in-use seen');
    assert.equal(m.status().worldInUse, 0);
    assert.ok(m.lines.some((l) => /could not open world 0 because another running server holds it/.test(l.text)));
    assert.ok(fs.existsSync(path.join(dir, 'Saves', 'Slot1')), 'the failed start left Slot1');
    assert.match(fs.readFileSync(cfg, 'utf8'), /^worldSlot = '-1'/m);
  } finally {
    await H.stopSim(first);
  }
  // Next start: Steward's plan puts world 0 back.
  const w = await W.readWorlds(dir, { probeLock });
  const plan = W.planWorld(w, remembered);
  assert.equal(plan.action, 'pin');
  assert.equal(plan.slot, 0);
  assert.deepEqual(w.slots.map((s) => [s.slot, s.empty]), [[0, false], [1, true]]);
  await W.setWorldSlot(dir, plan.slot, R.applyCfg);
  const again = await H.startReady(dir);
  try {
    assert.equal(again.worldSlot, 0);
    assert.equal(again.worldIsNew, false, 'the same world, not Slot2');
    assert.equal(fs.existsSync(path.join(dir, 'Saves', 'Slot2')), false);
  } finally {
    await H.stopSim(again);
  }
  // Without Steward's pin the game would have made yet another world: shown by the plan for a
  // server with no memory of its world.
  assert.equal(W.planWorld({ ...w, cfgSlot: -1 }, null).action, 'choose');
});
