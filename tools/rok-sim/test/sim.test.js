'use strict';

// The whole simulated server, in process, with real sockets on free ports and short timings.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const net = require('net');
const dgram = require('dgram');
const path = require('path');
const H = require('./helpers');
const P = require('../lib/packets');
const A = require('../lib/a2s');

test('first run: Unity log, game log, config files, Disconnect to a console client, exit 0', async () => {
  const dir = H.tmpDir();
  const cport = await H.freePort();
  const s = H.makeSim(dir, ['-logFile', path.join(dir, 'Logs', 'realm-server.log'), '-cport', String(cport)], { loadMs: 300 });
  const exit = H.onceEvent(s, 'exit');
  s.start();
  const c = await H.connectConsole(cport, 3000, { hello: false });
  assert.equal(await exit, 0);
  await H.until(() => c.closed, 2000, 'console closed');
  const types = c.groups.map((g) => g.type);
  assert.equal(types[types.length - 1], P.TYPE.DISCONNECT, 'the first run says goodbye');
  // CoreServer logs the three first-run lines BEFORE it sets SendLogs = false, so they do arrive.
  assert.ok(c.messages().includes('[I] This is the first time you have run this server.'), c.messages().join('|'));
  const log = H.readGameLog(dir);
  assert.match(log, /\[Info\] {3}Reign Of Kings is now in dedicated mode\./);
  assert.match(log, /\[Error\] {2}-{67}\r\n/);
  assert.match(log, /\[Info\] {3}This is the first time you have run this server\./);
  assert.match(log, /Please look over your config files under the Configuration\/ folder\./);
  assert.doesNotMatch(log, /Server for \d+ players started/);
  const unity = fs.readFileSync(path.join(dir, 'Logs', 'realm-server.log'), 'utf8');
  assert.match(unity, /^Initialize engine version: /m);
  assert.doesNotMatch(unity, /dedicated mode|first time/, 'game lines never go to the Unity log');
  for (const f of ['ServerSettings.cfg', 'Users.cfg', 'Permissions.cfg', 'DeathMessages.cfg', 'BannedPlayers.cfg', 'Whitelist.cfg', 'ChatMutes.cfg', 'VoiceMutes.cfg']) {
    assert.ok(fs.existsSync(path.join(dir, 'Configuration', f)), f);
  }
  c.close();
});

test('normal start: ports, A2S, console commands, world slot, /shutdown order and exit', async () => {
  const { dir, game, steam, cfg } = await H.preparedFolder({ maxPlayers: 120 });
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)], { players: ['Wren', 'Odo the Tall'], realmCourt: 'on' });
  const c = await H.connectConsole(cport);
  try {
    // Game UDP port is taken by the server; TCP ping port accepts.
    const u = dgram.createSocket('udp4');
    await assert.rejects(new Promise((res, rej) => {
      u.once('error', rej);
      u.bind(game, '0.0.0.0', res);
    }), /EADDRINUSE/);
    u.close();
    await new Promise((res, rej) => net.connect(game, '127.0.0.1').once('connect', function () {
      this.destroy();
      res();
    }).once('error', rej));

    await H.until(() => s.players.length === 2, 2000, 'players joined');
    const q = await A.query('127.0.0.1', steam, 'info');
    assert.equal(q.ok, true, q.error);
    assert.equal(q.value.players, 2);
    assert.equal(q.value.maxPlayers, 0);

    // The console: keep-alives, /list output BEFORE the response, unknown command as [I].
    await H.until(() => c.groups.some((g) => g.type === P.TYPE.MESSAGE && g.body.length === 0), 2000, 'keep-alive');
    const list = await c.command('/list');
    assert.deepEqual(list.lines, ['[I] Online Players(2):', '[I] Wren, Odo the Tall']);
    assert.equal(list.response, '');
    const bad = await c.command('/frobnicate now');
    assert.deepEqual(bad.lines, ["[I] Unknown command '/frobnicate now'. For help type /help"]);
    const chat = await c.command('Hail, Ostreval');
    assert.deepEqual(chat.lines, ['[C] Server: Hail, Ostreval']);
    const silent = await c.command('/weather');
    assert.deepEqual(silent.lines, []);
    const court = await c.command('/realm.players');
    assert.equal(court.lines[0], '[I] REALMCOURT|1|players|2');
    assert.equal(court.lines.length, 3);
    const auth = await c.command('/list', P.TYPE.AUTHENTICATION);
    assert.ok(auth.lines.includes('[I] Wren, Odo the Tall'), 'an Authentication packet is just a command');
    const kick = await c.command('/kick "Odo" for being\\ tall');
    assert.equal(kick.lines[1], '[I] Odo the Tall has disconnected.');
    assert.match(kick.lines[0], /^\[I\] Odo the Tall \(\d{17}\) was kicked from the server because forbeing tall\.$/);

    // A Response-type group is submitted without an answer.
    c.sendRaw(P.getPackets(9, P.TYPE.RESPONSE, '/list'));
    await H.until(() => c.messages().filter((m) => m === '[I] Wren').length >= 1, 2000, 'list via Response packet');

    assert.match(fs.readFileSync(cfg, 'utf8'), /^worldSlot = '0'/m, 'the new world slot is written back');

    const exit = H.onceEvent(s, 'exit');
    const before = c.groups.length;
    const id = 4242;
    c.sendRaw(P.getPackets(id, P.TYPE.MESSAGE, '/shutdown'));
    assert.equal(await exit, 0);
    await H.until(() => c.closed, 2000, 'console closed');
    const after = c.groups.slice(before).filter((g) => !(g.type === P.TYPE.MESSAGE && g.body.length === 0));
    assert.deepEqual(after.map((g) => [g.type, g.text]), [
      [P.TYPE.MESSAGE, '[I] Server has shut down the server.'],
      [P.TYPE.MESSAGE, '[I] Saving game...'],
      [P.TYPE.RESPONSE, ''],
      [P.TYPE.DISCONNECT, '']
    ]);
    assert.equal(after[2].id, id);
    assert.equal(JSON.parse(fs.readFileSync(path.join(dir, 'Saves', 'Slot0', 'rok-sim-world.json'), 'utf8')).saves, 1);
    assert.ok(!fs.existsSync(path.join(dir, 'Saves', 'Slot0', 'Session.lock')), 'a clean exit deletes Session.lock');
    const log = H.readGameLog(dir);
    assert.match(log, /Steam game server started\. \(IP: 203\.0\.113\.10, Logged: True, Secure: True\)/);
    assert.match(log, /Server for 120 players started on port \d+\./);
    assert.match(log, /Authentication verified for Wren \(\d{17}\)\./);
    assert.match(log, /Wren has connected\. \(\d{17}\)/);
  } finally {
    c.close();
    await H.stopSim(s);
  }
});

test('-cport and nobody connects within the window: saved shutdown, no Disconnect needed', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)], { cportWindowMs: 300 });
  const code = await H.onceEvent(s, 'exit', 3000);
  assert.equal(code, 0);
  const log = H.readGameLog(dir);
  assert.match(log, /Admin console disabled\./);
  assert.match(log, /Saving game\.\.\./);
});

test('without -cport the console listens on the default and nothing shuts down by itself', async (t) => {
  const { dir } = await H.preparedFolder();
  // 11000 may be busy on a developer machine: then the bind error path is what we check.
  const s = await H.startReady(dir, [], { cportWindowMs: 200 });
  try {
    await H.wait(500);
    assert.equal(s.state, 'running');
    const log = H.readGameLog(dir);
    if (s.console.bindError) {
      t.diagnostic('port 11000 busy here; checked the bind-failure path instead');
      assert.match(log, /\[Except\] SocketException: /);
    } else assert.equal(s.console.port, 11000);
  } finally {
    await H.stopSim(s);
  }
});

test('the last console client leaving stops the server (noticed on the next keep-alive)', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)]);
  const c = await H.connectConsole(cport);
  await c.command('/list');
  const exit = H.onceEvent(s, 'exit');
  c.close();
  assert.equal(await exit, 0);
  const log = H.readGameLog(dir);
  assert.match(log, /Admin console disconnected\./);
  assert.match(log, /EndOfStreamException/);
  assert.match(log, /Saving game\.\.\./);
});

test('the -cport window during a long load only disables the console; the server comes up', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)], { cportWindowMs: 50, loadMs: 300 });
  try {
    assert.equal(s.state, 'running');
    assert.match(H.readGameLog(dir), /Admin console disabled\.[\s\S]*Server for \d+ players started/);
    await assert.rejects(new Promise((res, rej) => net.connect(cport, '127.0.0.1').once('connect', res).once('error', rej)), /ECONNREFUSED/);
  } finally {
    await H.stopSim(s);
  }
});

test('a bad frame kills that client\'s reader but the connection stays and the server runs', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)]);
  const c = await H.connectConsole(cport);
  try {
    const bad = P.getPackets(1, P.TYPE.MESSAGE, '/list');
    bad[bad.length - 1] = 7;
    c.sendRaw(bad);
    await H.until(() => c.messages().some((m) => m.startsWith('[X] FormatException: Last two bytes of the packet were not zero.')), 2000, '[X] line');
    const n = c.groups.length;
    c.sendRaw(P.getPackets(2, P.TYPE.MESSAGE, '/list'));
    await H.wait(300);
    assert.ok(!c.groups.slice(n).some((g) => g.type === P.TYPE.RESPONSE), 'nothing is read from this client any more');
    assert.ok(c.groups.slice(n).some((g) => g.type === P.TYPE.MESSAGE && g.body.length === 0), 'keep-alives still arrive');
    assert.equal(s.state, 'running');
  } finally {
    c.close();
    await H.stopSim(s);
  }
});

test('busy ports: game port ends the start, console port is logged, steam port fails Steam only', async () => {
  // Game port taken -> "The port N is already being used by another application." and exit.
  {
    const { dir, game } = await H.preparedFolder();
    const blocker = dgram.createSocket('udp4');
    await new Promise((r) => blocker.bind(game, '0.0.0.0', r));
    const s = H.makeSim(dir, []);
    const exit = H.onceEvent(s, 'exit');
    s.start();
    assert.equal(await exit, 0);
    blocker.close();
    assert.match(H.readGameLog(dir), new RegExp(`\\[Error\\]  The port ${game} is already being used by another application\\.`));
  }
  // Console and Steam ports taken -> logged, server still runs, no A2S.
  {
    const { dir, steam } = await H.preparedFolder();
    const cport = await H.freePort();
    const tcp = net.createServer();
    await new Promise((r) => tcp.listen(cport, '0.0.0.0', r));
    const udp = dgram.createSocket('udp4');
    await new Promise((r) => udp.bind(steam, '0.0.0.0', r));
    const s = await H.startReady(dir, ['-cport', String(cport)]);
    try {
      const log = H.readGameLog(dir);
      assert.match(log, /\[Except\] SocketException: Address already in use/);
      assert.match(log, /The Steam game server could not initialize\./);
      assert.match(log, /2\) If you are running multiple servers on the same system, make sure the steamAuthPort in ServerSettings is different for each server\./);
      assert.doesNotMatch(log, /Steam game server started/);
      assert.equal(s.state, 'running');
      // Joins fail Steam authentication when Steam is not up.
      assert.equal(s.join('Wren'), null);
      assert.match(H.readGameLog(dir), /Failed to authenticate Wren \(\d{17}\) with Steam\./);
    } finally {
      await H.stopSim(s);
      tcp.close();
      udp.close();
    }
  }
});

test('Steam modes: no-login waits and then reports, the server still starts', async () => {
  const { dir } = await H.preparedFolder();
  const t0 = Date.now();
  const s = await H.startReady(dir, [], { steam: 'no-login', steamWaitMs: 200 });
  try {
    assert.ok(Date.now() - t0 >= 200);
    assert.match(H.readGameLog(dir), /could not login anonymously[\s\S]*Server for \d+ players started/);
    assert.equal(s.a2s, undefined);
  } finally {
    await H.stopSim(s);
  }
});

test('-ip makes it a client (exit 2); a repeated argument crashes (exit 1); a bad cfg value exits', async () => {
  {
    const s = H.makeSim(H.tmpDir(), ['-ip', '127.0.0.1']);
    const exit = H.onceEvent(s, 'exit');
    s.start();
    assert.equal(await exit, 2);
  }
  {
    const dir = H.tmpDir();
    const s = H.makeSim(dir, ['-cport', '1', '-cport', '2']);
    const exit = H.onceEvent(s, 'exit');
    s.start();
    assert.equal(await exit, 1);
    assert.match(fs.readFileSync(path.join(dir, 'ROK_Data', 'output_log.txt'), 'utf8'), /ArgumentException/);
  }
  {
    const { dir } = await H.preparedFolder({ maxPlayers: 'many' });
    const s = H.makeSim(dir, []);
    const exit = H.onceEvent(s, 'exit');
    s.start();
    assert.equal(await exit, 0);
    assert.match(H.readGameLog(dir), /\[Except\] PropertyException: Tried to get a Int32 from 'many'/);
  }
});

test('join queue: player limit and timeBetweenPlayerJoin', async () => {
  const { dir } = await H.preparedFolder({ maxPlayers: 1, timeBetweenPlayerJoin: 0.3 });
  const s = await H.startReady(dir, []);
  try {
    s.join('Ysolde Corvane');
    s.join('Bram Halloran');
    assert.deepEqual(s.players.map((p) => p.name), ['Ysolde Corvane']);
    assert.deepEqual(s.queue.map((p) => p.name), ['Bram Halloran']);
    const t0 = Date.now();
    s.leave('Ysolde Corvane');
    await H.until(() => s.players.length === 1 && s.players[0].name === 'Bram Halloran', 2000, 'queued join');
    assert.ok(Date.now() - t0 >= 250, 'waited timeBetweenPlayerJoin');
    const log = H.readGameLog(dir);
    assert.match(log, /Ysolde Corvane has disconnected\./);
    assert.match(log, /Ending Steam auth session with \d{17}\./);
    // Name cleaning: CoreServer strips characters outside [-\p{L}0-9._ ].
    s.leave('Bram Halloran');
    assert.equal(s.join('<<>>'), 'John');
    assert.equal(s.join('Æthelred!'), 'Æthelred');
  } finally {
    await H.stopSim(s);
  }
});

test('whitelist enabled: players not listed are denied with the game line', async () => {
  const { dir } = await H.preparedFolder();
  fs.writeFileSync(path.join(dir, 'Configuration', 'Whitelist.cfg'), "# Whitelisted Players\r\nenabled = 'True'\r\n");
  const s = await H.startReady(dir, []);
  try {
    assert.equal(s.join('Wren'), null);
    assert.match(H.readGameLog(dir), /Player Wren denied connection because they were not on the active whitelist\./);
  } finally {
    await H.stopSim(s);
  }
});

test('RealmCourt commands exist only when the plugin is deployed (auto)', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)]);
  const c = await H.connectConsole(cport);
  try {
    assert.match((await c.command('/realm.save')).lines.join('\n'), /Unknown command/);
    fs.mkdirSync(path.join(dir, 'oxide', 'plugins'), { recursive: true });
    fs.writeFileSync(path.join(dir, 'oxide', 'plugins', 'RealmCourt.cs'), '// stand-in');
    assert.deepEqual((await c.command('/realm.save')).lines, ['[I] Saving game...', '[I] REALMCOURT|save|ok|World saved.']);
    assert.deepEqual((await c.command('/realm.save')).lines, ['[I] REALMCOURT|save|wait|The world was saved less than 10 seconds ago.']);
  } finally {
    c.close();
    await H.stopSim(s);
  }
});

test('simulator commands and scenario steps drive joins, chat, logs and crashes', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  fs.writeFileSync(path.join(dir, 'scenario.json'), JSON.stringify({ steps: [{ at: 0, do: 'join', name: 'Petra Halloran' }, { at: 50, do: 'log', level: 'warn', text: 'scenario warning' }] }));
  const s = await H.startReady(dir, ['-cport', String(cport)], { scenario: 'scenario.json' });
  const c = await H.connectConsole(cport);
  try {
    assert.deepEqual((await c.command('/sim.echo ping')).lines.filter((l) => l.startsWith('[I] ping')), ['[I] ping']);
    await c.command('/sim.join Wren');
    const st = JSON.parse((await c.command('/sim.status')).lines.find((l) => l.startsWith('[I] SIMSTATUS ')).slice('[I] SIMSTATUS '.length));
    assert.deepEqual(st.players.map((p) => p.name).sort(), ['Petra Halloran', 'Wren']);
    assert.deepEqual((await c.command('/sim.chat Wren Long live the Charter')).lines.slice(0, 1), ['[C] Wren: Long live the Charter']);
    await c.command('/sim.log error injected failure');
    await H.until(() => /scenario warning/.test(H.readGameLog(dir)), 2000, 'scenario log');
    assert.match(H.readGameLog(dir), /\[Error\]  injected failure/);
    assert.doesNotMatch(H.readGameLog(dir), /SIMSTATUS|ping/, 'simulator replies stay out of the game log');
    const exit = H.onceEvent(s, 'exit');
    c.command('/sim.crash 3').catch(() => {});
    assert.equal(await exit, 3);
    await H.until(() => c.closed, 2000, 'closed');
    assert.ok(!c.groups.some((g) => g.type === P.TYPE.DISCONNECT), 'a crash sends no Disconnect');
    assert.doesNotMatch(H.readGameLog(dir), /Saving game\.\.\./, 'a crash does not save');
  } finally {
    c.close();
    await H.stopSim(s);
  }
});

test('simulator commands can be switched off (then they are unknown, as in the game)', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)], { simCommands: false });
  const c = await H.connectConsole(cport);
  try {
    assert.deepEqual((await c.command('/sim.echo x')).lines, ["[I] Unknown command '/sim.echo x'. For help type /help"]);
  } finally {
    c.close();
    await H.stopSim(s);
  }
});

test('a long console line is split into 4096-byte frames and reassembled', async () => {
  const { dir } = await H.preparedFolder();
  const cport = await H.freePort();
  const s = await H.startReady(dir, ['-cport', String(cport)]);
  const c = await H.connectConsole(cport);
  try {
    const word = 'x'.repeat(9000);
    const r = await c.command(`/sim.echo ${word}`);
    assert.equal(r.lines.find((l) => l.length > 100), `[I] ${word}`);
    const g = c.groups.find((x) => x.text === `[I] ${word}`);
    assert.equal(g.packets, 3);
  } finally {
    c.close();
    await H.stopSim(s);
  }
});
