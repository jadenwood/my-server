// Admin console client: framing against the decompiled format, splitting at 4082 bytes, partial
// reads, and a live session against an imitation of the game's socket (test/fake-admin-console.js).
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const net = require('net');
const AC = require('../lib/admin-console');
const { FakeAdminConsole, packets } = require('./fake-admin-console');

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(fn, ms = 3000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (await fn()) return true;
    await wait(20);
  }
  throw new Error('condition not met in time');
}

test('frame layout matches Packet.Serialize: size = body + 14, id, type, ASCII body, two zero bytes', () => {
  const f = AC.encodePacket(7, AC.TYPE.MESSAGE, Buffer.from('/list'));
  assert.equal(f.length, 5 + 14);
  assert.equal(f.readInt32LE(0), 19);
  assert.equal(f.readInt32LE(4), 7);
  assert.equal(f.readInt32LE(8), 1);
  assert.equal(f.subarray(12, 17).toString(), '/list');
  assert.deepEqual([...f.subarray(17)], [0, 0]);
  // identical to the independent fake implementation
  assert.deepEqual(f, packets(7, 1, '/list'));
  const empty = AC.encodeMessage(3, AC.TYPE.MESSAGE, null);
  assert.equal(empty.length, 14);
  assert.equal(empty.readInt32LE(0), 14);
  assert.throws(() => AC.encodePacket(1, 9), /0 to 3/);
  assert.throws(() => AC.encodePacket(1, 1, Buffer.alloc(4083)), /4082/);
});

test('ASCII like Encoding.ASCII: non-ASCII becomes ?', () => {
  assert.equal(AC.toAscii('Æthel ok').toString(), '?thel ok');
  assert.equal(AC.toAscii('😀').toString(), '??');
  assert.equal(AC.fromAscii(Buffer.from([0x41, 0xc3, 0x42])), 'A?B');
});

test('splitting at 4082 bytes mirrors Packet.GetPackets, including the empty closing packet', () => {
  for (const n of [0, 1, 4081, 4082, 4083, 8164, 9000]) {
    const text = 'x'.repeat(n);
    const mine = AC.encodeMessage(5, AC.TYPE.MESSAGE, text);
    assert.deepEqual(mine, packets(5, 1, text), `length ${n}`);
    const expectedPackets = Math.floor(n / 4082) + 1;
    let off = 0;
    let count = 0;
    while (off < mine.length) {
      const size = mine.readInt32LE(off);
      assert.ok(size >= 14 && size <= 4096);
      off += size;
      count++;
    }
    assert.equal(count, expectedPackets, `packets for ${n}`);
  }
});

test('decoder: partial reads, byte-by-byte, several frames per chunk, multi-packet messages', () => {
  const big = 'y'.repeat(4082 * 2 + 10);
  const stream = Buffer.concat([
    packets(1, 1, null),
    packets(2, 1, '[I] Online Players(2):'),
    packets(3, 1, big),
    packets(9, 2, ''),
    packets(4, 1, 'x'.repeat(4082)),
    packets(5, 3, null)
  ]);
  // 1) all at once
  let d = new AC.PacketDecoder();
  let msgs = d.push(stream);
  assert.deepEqual(msgs.map((m) => [m.id, m.type, m.body.length, m.packets]), [
    [1, 1, 0, 1],
    [2, 1, 22, 1],
    [3, 1, big.length, 3],
    [9, 2, 0, 1],
    [4, 1, 4082, 2],
    [5, 3, 0, 1]
  ]);
  // 2) one byte at a time
  d = new AC.PacketDecoder();
  msgs = [];
  for (const b of stream) msgs.push(...d.push(Buffer.from([b])));
  assert.equal(msgs.length, 6);
  assert.equal(msgs[2].body.toString(), big);
  assert.equal(d.pending, 0);
  // 3) random chunking
  for (let round = 0; round < 50; round++) {
    d = new AC.PacketDecoder();
    msgs = [];
    let i = 0;
    while (i < stream.length) {
      const n = 1 + Math.floor(Math.random() * 5000);
      msgs.push(...d.push(stream.subarray(i, i + n)));
      i += n;
    }
    assert.equal(msgs.length, 6);
    assert.equal(msgs[4].body.length, 4082);
  }
});

test('decoder rejects frames the game could not send, and stays failed', () => {
  const bad = packets(1, 1, 'hello');
  bad[bad.length - 1] = 1;
  const d = new AC.PacketDecoder();
  assert.throws(() => d.push(bad), AC.ProtocolError);
  assert.throws(() => d.push(packets(2, 1, 'ok')), /last two bytes/);
  const tiny = Buffer.alloc(14);
  tiny.writeInt32LE(13, 0);
  assert.throws(() => new AC.PacketDecoder().push(tiny), /outside 14..4096/);
  const huge = Buffer.alloc(8);
  huge.writeInt32LE(5000, 0);
  assert.throws(() => new AC.PacketDecoder().push(huge), /outside/);
  const t = packets(1, 1, null);
  t.writeInt32LE(7, 8);
  assert.throws(() => new AC.PacketDecoder().push(t), /unknown packet type 7/);
});

test('log prefixes and argument quoting follow the game', () => {
  assert.deepEqual(AC.parseLogBody('[I] Admin console enabled.'), { level: 'I', kind: 'info', text: 'Admin console enabled.' });
  assert.equal(AC.parseLogBody('[C] Wren: hail').kind, 'chat');
  assert.equal(AC.parseLogBody('[X] NullReferenceException: x\n  at Foo').text, 'NullReferenceException: x\n  at Foo');
  assert.equal(AC.parseLogBody('plain').kind, 'raw');
  assert.equal(AC.quoteArg('Wren'), 'Wren');
  assert.equal(AC.quoteArg('Odo the Tall'), '"Odo the Tall"');
  assert.equal(AC.quoteArg('say "hi" now'), 'say\\ \\"hi\\"\\ now');
  assert.throws(() => AC.quoteArg("O'Brien"), /apostrophe/);
  assert.throws(() => AC.quoteArg('  '), /empty/);
  assert.deepEqual(AC.normalizeInput('list'), { line: '/list', chat: false });
  assert.deepEqual(AC.normalizeInput('/kick Wren'), { line: '/kick Wren', chat: false });
  assert.deepEqual(AC.normalizeInput('say Hail, realm!'), { line: 'Hail, realm!', chat: true });
  assert.equal(AC.normalizeInput('   '), null);
  assert.equal(AC.normalizeInput('/'), null);
});

test('cport plan: one loopback port per server slot inside the reserved 11000-11003 range', () => {
  assert.deepEqual(['s1', 's2', 's3', 's4'].map(AC.cportForId), [11000, 11001, 11002, 11003]);
  assert.throws(() => AC.cportForId('s5'));
  assert.throws(() => new AC.AdminConsole({ port: 11000, host: '192.168.1.5' }), /only ever reached on this PC/);
});

test('live session: connects while the server is still starting, runs commands, keeps alive, stops on purpose', async () => {
  // Reserve a port, then start the fake only after the client began retrying (server still loading).
  const probe = net.createServer();
  await new Promise((r) => probe.listen(0, '127.0.0.1', r));
  const port = probe.address().port;
  await new Promise((r) => probe.close(r));

  const con = new AC.AdminConsole({ port, retryMs: 50, staleMs: 400 });
  const lines = [];
  con.on('line', (l) => lines.push(l));
  con.attach();
  await wait(200);
  assert.equal(con.state, 'connecting');
  assert.ok(con.attempts >= 2, 'kept retrying while nothing listened');

  const fake = new FakeAdminConsole({ keepAliveMs: 100, cportRuleMs: 1000 });
  await fake.listen(port);
  await until(() => con.isConnected());

  const r = await con.send('/list');
  assert.equal(r.response, '');
  assert.deepEqual(r.lines.map((l) => l.text), ['Online Players(2):', 'Wren, Odo the Tall']);

  // queued commands do not mix their output
  const [a, b] = await Promise.all([con.send('/echo one'), con.send('/echo two')]);
  assert.deepEqual(a.lines.map((l) => l.text), ['one']);
  assert.deepEqual(b.lines.map((l) => l.text), ['two']);

  // chat (no slash) reaches the server as chat
  await con.send('Hail');
  assert.deepEqual(fake.chat, ['Hail']);

  // over-long input is refused before it reaches the wire
  await assert.rejects(con.send('/echo ' + 'z'.repeat(AC.MAX_COMMAND)), /limited to/);

  // a message longer than 4082 bytes, as encoded by the client, is reassembled by the server's
  // independent reader (second raw connection: the fake keeps running while one client remains)
  const long = '/echo ' + 'z'.repeat(5000);
  const raw = net.connect(port, '127.0.0.1');
  await new Promise((res) => raw.once('connect', res));
  raw.write(AC.encodeMessage(777, AC.TYPE.MESSAGE, long));
  await until(() => fake.received.some((x) => x.text === long));
  assert.equal(fake.received.find((x) => x.text === long).packets, 2);
  raw.destroy();

  // ping is an empty Message answered by an empty Response
  assert.ok((await con.ping()) >= 0);

  // keep-alives arrive; we survive past the cport window because we are connected
  await wait(1200);
  assert.equal(fake.shutdown, null);
  assert.ok(con.stats.keepAlives > 3);
  assert.equal(con.stale, false);

  // /shutdown: Disconnect packet, then the socket closes; the client knows the server said goodbye
  const bye = new Promise((res) => con.once('disconnected', res));
  await con.send('/shutdown');
  const ev = await bye;
  assert.equal(ev.serverSaidBye, true);
  assert.equal(con.state, 'closed');
  await fake.close();
});

test('closing the connection on purpose is how the game is told to shut down', async () => {
  const fake = new FakeAdminConsole({ keepAliveMs: 100 });
  const port = await fake.listen(0);
  const con = new AC.AdminConsole({ port, retryMs: 50 });
  con.attach();
  await until(() => con.isConnected());
  const gone = new Promise((res) => con.once('disconnected', res));
  con.close();
  const ev = await gone;
  assert.equal(ev.deliberate, true);
  await until(() => fake.shutdown === 'last admin client disconnected');
  await fake.close();
});

test('without a client in time the -cport rule stops the server (what Steward must prevent)', async () => {
  const fake = new FakeAdminConsole({ cportRuleMs: 150 });
  await fake.listen(0);
  await until(() => fake.shutdown === 'no admin client within the -cport window');
  await fake.close();
});

test('unexpected drop: reconnects; command timeout; stale link is reported, never closed', async () => {
  const fake = new FakeAdminConsole({ keepAliveMs: 50 });
  const port = await fake.listen(0);
  const con = new AC.AdminConsole({ port, retryMs: 50, staleMs: 300, commandTimeoutMs: 300 });
  const drops = [];
  con.on('disconnected', (e) => drops.push(e));
  con.attach();
  await until(() => con.isConnected());
  // Server-side drop of our socket without a Disconnect packet.
  for (const c of fake.clients) c.destroy();
  await until(() => drops.length === 1);
  assert.equal(drops[0].deliberate, false);
  assert.equal(drops[0].serverSaidBye, false);
  // the fake "shut down" because its last client left, but our client still tries to come back
  assert.equal(con.state, 'connecting');
  await fake.close();

  // A server that stops answering: commands time out, the link is flagged stale, the socket stays.
  const mute = net.createServer((s) => s.on('data', () => {}));
  await new Promise((r) => mute.listen(0, '127.0.0.1', r));
  const con2 = new AC.AdminConsole({ port: mute.address().port, retryMs: 50, staleMs: 150, commandTimeoutMs: 200 });
  con2.attach();
  await until(() => con2.isConnected());
  await assert.rejects(con2.send('/list'), /did not answer/);
  await until(() => con2.stale === true);
  assert.equal(con2.isConnected(), true);
  con2.close();
  con.detach();
  await new Promise((r) => mute.close(r));
});

test('a protocol error degrades the console but keeps the socket open', async () => {
  const srv = net.createServer((s) => {
    const bad = packets(1, 1, 'oops');
    bad[bad.length - 2] = 9;
    s.write(bad);
  });
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const con = new AC.AdminConsole({ port: srv.address().port, retryMs: 50 });
  const errs = [];
  con.on('protocol-error', (e) => errs.push(e));
  con.attach();
  await until(() => errs.length === 1);
  assert.match(con.status().degraded, /last two bytes/);
  assert.equal(con.isConnected(), true);
  con.close();
  await new Promise((r) => srv.close(r));
});
