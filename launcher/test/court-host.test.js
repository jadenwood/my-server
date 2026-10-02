// Court host: how the live console is wired around a server process. Uses a stand-in for
// ServerManager and the fake game console; checks -cport, connect-at-start, command routing,
// stop via /shutdown, the close-to-stop fallback, and the "never reached" safety switch.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const net = require('net');
const { EventEmitter } = require('events');
const { createCourt } = require('../lib/court-host');
const { FakeAdminConsole } = require('./fake-admin-console');

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, ms = 4000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (await fn()) return;
    await wait(20);
  }
  throw new Error('condition not met in time');
}
async function freePort() {
  const s = net.createServer();
  await new Promise((r) => s.listen(0, '127.0.0.1', r));
  const p = s.address().port;
  await new Promise((r) => s.close(r));
  return p;
}

// Stand-in for lib/server-process.js ServerManager: records the arguments, "spawns" a fake game
// whose admin console starts listening a little later (as the real one does while loading).
class StandInManager extends EventEmitter {
  constructor({ consoleDelayMs = 200, consoleOpts = {}, listen = true } = {}) {
    super();
    this.state = 'stopped';
    this.lines = [];
    this.stdin = [];
    this.child = null;
    this.consoleDelayMs = consoleDelayMs;
    this.consoleOpts = consoleOpts;
    this.listen = listen;
  }
  log(src, text) {
    this.lines.push({ src, text });
  }
  isRunning() {
    return !!this.child;
  }
  async start(root, exeName, opts = {}) {
    this.args = opts.extraArgs || [];
    this.exe = exeName;
    this.child = { pid: 1 };
    this.state = 'running';
    this.requested = false;
    this.startedAt = Date.now();
    const i = this.args.indexOf('-cport');
    if (i >= 0 && this.listen) {
      const port = Number(this.args[i + 1]);
      setTimeout(async () => {
        this.game = new FakeAdminConsole({ keepAliveMs: 100, cportRuleMs: 1000, ...this.consoleOpts });
        this.game.on('shutdown', () => setTimeout(() => this.exit(0), 30));
        await this.game.listen(port);
      }, this.consoleDelayMs);
    }
    return { state: 'running' };
  }
  stop() {
    this.requested = true;
    this.state = 'stopping';
    this.stdin.push('quit');
    return { state: 'stopping' };
  }
  sendCommand(text) {
    this.stdin.push(text);
    return true;
  }
  exit(code) {
    if (!this.child) return;
    this.child = null;
    this.state = 'stopped';
    this.emit('exit', { code, requested: this.requested, uptimeMs: Date.now() - this.startedAt });
  }
}

function makeCourt(port, extra = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'court-host-'));
  const exe = { v: 'ROK' };
  const court = createCourt({
    userData: dir,
    settings: { get: (k) => (k === 'serverExe' ? exe.v : null) },
    instOf: (id) => ({ id }),
    rootOf: () => null,
    cportFor: () => port,
    ...extra
  });
  return { court, dir, exe };
}

test('ROK.exe gets -cport, Steward connects while it loads, commands go over the console', async () => {
  const port = await freePort();
  const { court, dir } = makeCourt(port);
  const m = new StandInManager();
  court.adopt('s1', m);
  await m.start('/srv', 'ROK');
  assert.deepEqual(m.args, ['-cport', String(port)]);
  await until(() => court._state.get('s1').con && court._state.get('s1').con.isConnected());
  // past the game's -cport window: still up because Steward is connected
  await wait(1200);
  assert.equal(m.game.shutdown, null);

  // The game's own warnings and exceptions (which never reach stdout or -logFile) land in the server log.
  m.game.logLine('X', 'NullReferenceException: Object reference not set');
  m.game.logLine('W', 'Steam server registration slow');
  await until(() => m.lines.some((l) => l.src === 'err' && l.text === '[X] NullReferenceException: Object reference not set'));
  assert.ok(m.lines.some((l) => l.src === 'con' && l.text === '[W] Steam server registration slow'));

  const r = await m.sendCommand('list');
  assert.equal(r.via, 'console');
  assert.ok(m.lines.some((l) => l.src === 'con' && l.text === 'Wren, Odo the Tall'));
  assert.deepEqual(m.stdin, [], 'nothing went to stdin');

  const st = await court.status('s1');
  assert.equal(st.console.state, 'connected');
  const players = await court.refreshPlayers('s1');
  assert.deepEqual(players.players.map((p) => p.name), ['Wren', 'Odo the Tall']);

  const kick = await court.act('s1', 'kick', { name: 'Wren', reason: 'afk' });
  assert.equal(kick.ok, true);
  const rolls = await court.courtLog.read();
  assert.equal(rolls[0].action, 'kick');
  assert.equal(rolls[0].target, 'Wren');

  // a failing command is logged as failed
  const bad = await court.act('s1', 'unban', { name: 'Nobody' });
  assert.equal(bad.ok, false);

  // Stop: /shutdown over the console, the game says goodbye and exits
  m.stop();
  await until(() => !m.isRunning());
  assert.equal(m.game.shutdown, '/shutdown');
  assert.ok(m.lines.some((l) => /Sending \/shutdown/.test(l.text)));
  fs.rmSync(dir, { recursive: true, force: true });
});

test('if /shutdown is ignored, closing the console makes the game stop', async () => {
  const port = await freePort();
  const { court, dir } = makeCourt(port, { stopCloseAfterMs: 300 });
  const m = new StandInManager();
  court.adopt('s2', m);
  await m.start('/srv', 'ROK');
  await until(() => court._state.get('s2').con && court._state.get('s2').con.isConnected());
  // make the fake ignore /shutdown
  const orig = m.game.onPackets.bind(m.game);
  m.game.onPackets = (sock, pk) => (Buffer.concat(pk.map((p) => p.body)).toString() === '/shutdown' ? undefined : orig(sock, pk));
  m.stop();
  await until(() => !m.isRunning(), 3000);
  assert.equal(m.game.shutdown, 'last admin client disconnected');
  fs.rmSync(dir, { recursive: true, force: true });
});

test('Server.exe, or the console turned off: no -cport, commands stay on stdin', async () => {
  const port = await freePort();
  const { court, dir } = makeCourt(port);
  const m = new StandInManager();
  court.adopt('s1', m);
  await m.start('/srv', 'Server');
  assert.deepEqual(m.args, []);
  assert.equal(m.sendCommand('oxide.version'), true);
  assert.deepEqual(m.stdin, ['oxide.version']);
  m.exit(0);
  await court.prefs.set('s1', false);
  await m.start('/srv', 'ROK');
  assert.deepEqual(m.args, []);
  assert.ok(m.lines.some((l) => /Live console off for this run: the live console is turned off/.test(l.text)));
  m.exit(0);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('a game that exits before its console answers turns -cport off for the session', async () => {
  const port = await freePort();
  const { court, dir } = makeCourt(port);
  const m = new StandInManager({ listen: false });
  court.adopt('s3', m);
  await m.start('/srv', 'ROK');
  assert.deepEqual(m.args, ['-cport', String(port)]);
  await wait(300);
  m.exit(1);
  assert.match((await court.status('s3')).cportOff, /exited before its admin console/);
  await m.start('/srv', 'ROK');
  assert.deepEqual(m.args, [], 'next start without -cport');
  m.exit(0);
  // turning the console back on clears the switch
  await court.prefs.set('s3', true);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('a busy console port is never used', async () => {
  const blocker = net.createServer();
  await new Promise((r) => blocker.listen(0, '0.0.0.0', r));
  const port = blocker.address().port;
  const { court, dir } = makeCourt(port);
  const m = new StandInManager();
  court.adopt('s4', m);
  await m.start('/srv', 'ROK');
  assert.deepEqual(m.args, []);
  assert.ok(m.lines.some((l) => /already in use/.test(l.text)));
  m.exit(0);
  await new Promise((r) => blocker.close(r));
  fs.rmSync(dir, { recursive: true, force: true });
});

test('Court actions need a live console and validate their input', async () => {
  const { court, dir } = makeCourt(1);
  await assert.rejects(court.act('s1', 'kick', { name: 'Wren' }), /not running/);
  await assert.rejects(court.act('s1', 'explode', {}), /Unknown Court action/);
  await assert.rejects(court.act('s1', 'ban', { name: 'Wren', days: -3 }), /whole number/);
  fs.rmSync(dir, { recursive: true, force: true });
});
