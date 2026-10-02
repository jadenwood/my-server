'use strict';

// The launcher's own code (read-only use) against the simulator: its admin console client, its log
// classifier and line patterns, and its ServerManager spawning a "ROK.exe" in a server folder.
// Every test is skipped when launcher/ is not in the tree. These tests do NOT edit launcher files.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const H = require('./helpers');

const L = path.resolve(__dirname, '../../../launcher/lib');
const has = (f) => fs.existsSync(path.join(L, f));
const skip = (f) => !has(f) && `launcher/lib/${f} not in this tree`;

test('launcher AdminConsole client: connect, run commands, close stops the server', { skip: skip('admin-console.js') }, async () => {
  const { AdminConsole } = require(path.join(L, 'admin-console.js'));
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)], { players: ['Wren'] });
  const ac = new AdminConsole({ port: cport, retryMs: 50, commandTimeoutMs: 3000, staleMs: 3000 });
  const lines = [];
  ac.on('line', (l) => lines.push(l));
  try {
    ac.attach();
    await H.until(() => ac.isConnected(), 3000, 'launcher client connected');
    await H.until(() => s.players.length === 1, 2000, 'Wren joined');
    const r = await ac.send('/list');
    assert.equal(r.response, '');
    const texts = r.lines.map((l) => l.text);
    assert.ok(texts.includes('Wren'), JSON.stringify(r.lines));
    const ms = await ac.ping();
    assert.ok(ms >= 0 && ms < 3000);
    await H.until(() => ac.status().stats.keepAlives >= 1, 2000, 'keep-alive counted');
    const exit = H.onceEvent(s, 'exit');
    ac.close();
    assert.equal(await exit, 0);
    assert.match(H.readGameLog(dir), /Admin console disconnected\.[\s\S]*Saving game\.\.\./);
  } finally {
    ac.close();
    await H.stopSim(s);
  }
});

test('launcher AdminConsole sees the Disconnect packet after /shutdown', { skip: skip('admin-console.js') }, async () => {
  const { AdminConsole } = require(path.join(L, 'admin-console.js'));
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)]);
  const ac = new AdminConsole({ port: cport, retryMs: 50, commandTimeoutMs: 3000 });
  try {
    ac.attach();
    await H.until(() => ac.isConnected(), 3000, 'connected');
    const exit = H.onceEvent(s, 'exit');
    const r = await ac.send('/shutdown');
    assert.ok(r.lines.some((l) => /has shut down the server/.test(l.text)));
    assert.equal(await exit, 0);
    await H.until(() => ac.status().serverSaidBye, 2000, 'serverSaidBye');
  } finally {
    ac.close();
    await H.stopSim(s);
  }
});

test('launcher log classifier and line patterns recognise the simulator logs', { skip: skip('shared/gamelog.js') }, async () => {
  const GL = require(path.join(L, 'shared', 'gamelog.js'));
  const S = has('safety.js') ? require(path.join(L, 'safety.js')) : null;
  const N = has('netcheck.js') ? require(path.join(L, 'netcheck.js')) : null;

  // First run.
  const first = H.tmpDir();
  const s1 = H.makeSim(first, ['-logFile', path.join(first, 'Logs', 'realm-server.log')]);
  const e1 = H.onceEvent(s1, 'exit');
  s1.start();
  await e1;
  const ids1 = GL.scan(H.readGameLog(first)).findings.map((f) => f.id);
  assert.ok(ids1.includes('first-run-exit'), ids1.join());
  if (S) assert.ok(fs.readFileSync(path.join(first, 'Logs', 'realm-server.log'), 'utf8').split('\n').some((l) => S.READY_LINE.test(l)));

  // A good start with a join and a leave.
  const { dir } = await H.preparedFolder();
  const s2 = await H.startReady(dir, [], { players: ['Wren'] });
  try {
    await H.until(() => s2.players.length === 1, 2000, 'join');
    s2.leave('Wren');
    const log = H.readGameLog(dir);
    const ids = GL.scan(log).findings.map((f) => f.id);
    for (const id of ['server-ready', 'steam-server-ok', 'player-joined', 'admin-console']) assert.ok(ids.includes(id), `${id} in ${ids.join()}`);
    const lines = log.split(/\r?\n/).map((l) => GL.parseLine(l)).filter((p) => p.level);
    assert.ok(lines.length > 5 && lines.every((p) => p.time), 'every record has a timestamp the launcher parses');
    if (S) {
      assert.ok(lines.some((p) => S.JOIN_LINE.test(p.text) && S.JOIN_LINE.exec(p.text)[1] === 'Wren'));
      assert.ok(lines.some((p) => S.LEAVE_LINE.test(p.text) && S.LEAVE_LINE.exec(p.text)[1] === 'Wren'));
    }
    if (N) {
      const steam = lines.map((p) => N.parseSteamLine(p.text)).find(Boolean);
      assert.deepEqual(steam, { publicIp: '203.0.113.10', logged: true, secure: true });
      const listening = lines.map((p) => N.parseListeningLine(p.text)).find(Boolean);
      assert.equal(listening.maxPlayers, 30);
    }
  } finally {
    await H.stopSim(s2);
  }

  // Failures the Doctor should name.
  const busy = await H.preparedFolder();
  const blocker = require('dgram').createSocket('udp4');
  await new Promise((r) => blocker.bind(busy.game, '0.0.0.0', r));
  const s3 = H.makeSim(busy.dir, [], { steam: 'init-fail' });
  const e3 = H.onceEvent(s3, 'exit');
  s3.start();
  await e3;
  blocker.close();
  const ids3 = GL.scan(H.readGameLog(busy.dir)).findings.map((f) => f.id);
  assert.ok(ids3.includes('game-port-taken'), ids3.join());
  assert.ok(ids3.includes('steam-server-failed'), ids3.join());
});

test('launcher ServerManager spawns the ROK.exe shim and reads it like the real server', { skip: (skip('server-process.js') || process.platform === 'win32') && 'needs launcher/lib/server-process.js on Linux/macOS', timeout: 30000 }, async (t) => {
  const { ServerManager } = require(path.join(L, 'server-process.js'));
  const { AdminConsole } = require(path.join(L, 'admin-console.js'));
  const { dir } = await H.preparedFolder();
  execFileSync(process.execPath, [path.join(__dirname, '..', 'bin', 'rok-sim.js'), 'make-exe', dir]);
  const cport = await H.freePort();
  const mgr = new ServerManager({ platform: 'linux' });
  const ac = new AdminConsole({ port: cport, retryMs: 100, commandTimeoutMs: 3000 });
  try {
    await mgr.start(dir, 'ROK', { extraArgs: ['-cport', String(cport)] });
    ac.attach(); // as lib/court-host.js does: connect at spawn, inside the 10 s window
    await H.until(() => ac.isConnected(), 8000, 'console connected');
    // The game writes nothing to stdout, so the manager falls back to tailing the NEWEST file in
    // Logs\ after 6 s. Both the Unity log and the game's Log[...].txt live there.
    await H.until(() => mgr.lines.some((l) => l.src === 'log'), 15000, 'manager tailing a log file');
    t.diagnostic(`manager follows: ${mgr.lines.filter((l) => /following/.test(l.text)).map((l) => l.text).join(' | ')}; ready=${mgr.status().ready}`);
    assert.match(mgr.lines.map((l) => l.text).join('\n'), /-batchmode -nographics -silentcrash -logFile .*realm-server\.log -cport \d+/);
    const exited = new Promise((r) => mgr.once('exit', r));
    await ac.send('/shutdown');
    const last = await exited;
    assert.equal(last.code, 0);
    assert.match(H.readGameLog(dir), /Server for \d+ players started/);
  } finally {
    ac.close();
    if (mgr.isRunning()) mgr.forceStop();
  }
});
