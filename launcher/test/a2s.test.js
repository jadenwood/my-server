// A2S_INFO packet building/parsing and a query against a local fake server. Run: npm test
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const dgram = require('dgram');
const A = require('../lib/shared/a2s');

test('A2S_INFO request bytes, with and without a challenge', () => {
  const r = A.buildInfoRequest();
  assert.equal(r.toString('hex'), 'ffffffff54' + Buffer.from('Source Engine Query\0', 'latin1').toString('hex'));
  const c = A.buildInfoRequest(Buffer.from([1, 2, 3, 4]));
  assert.equal(c.length, r.length + 4);
  assert.deepEqual([...c.subarray(-4)], [1, 2, 3, 4]);
  assert.throws(() => A.buildInfoRequest(Buffer.from([1, 2])), /4 bytes/);
});

test('parses a challenge and an info reply (the fixed RoK values)', () => {
  const ch = A.parseResponse(A.buildChallengeReply(Buffer.from('abcd')));
  assert.equal(ch.type, 'challenge');
  assert.equal(ch.challenge.toString(), 'abcd');
  const info = A.parseResponse(A.buildInfoReply({ name: 'Another ROK Server', game: 'ROK Server', players: 7, maxPlayers: 0 }));
  assert.equal(info.type, 'info');
  assert.equal(info.name, 'Another ROK Server');
  assert.equal(info.game, 'ROK Server');
  assert.equal(info.players, 7);
  assert.equal(info.maxPlayers, 0);
  assert.equal(info.serverType, 'd');
  assert.equal(info.passworded, false);
});

test('rejects short, split, unknown and truncated packets', () => {
  assert.throws(() => A.parseResponse(Buffer.from([0xff, 0xff])), /too short/);
  assert.throws(() => A.parseResponse(Buffer.from([0xfe, 0xff, 0xff, 0xff, 0x49, 0])), /split or unknown/);
  assert.throws(() => A.parseResponse(Buffer.from([0xff, 0xff, 0xff, 0xff, 0x6d, 0])), /unexpected reply/);
  const full = A.buildInfoReply();
  assert.throws(() => A.parseResponse(full.subarray(0, full.length - 3)), /truncated|unterminated/);
  assert.throws(() => A.parseResponse(Buffer.from([0xff, 0xff, 0xff, 0xff, 0x49, 17, 0x41])), /unterminated/);
});

function fakeServer({ challenge = true } = {}) {
  return new Promise((resolve) => {
    const sock = dgram.createSocket('udp4');
    const token = Buffer.from([9, 8, 7, 6]);
    sock.on('message', (msg, rinfo) => {
      const hasToken = msg.length === 29 && msg.subarray(25).equals(token);
      if (challenge && !hasToken) sock.send(A.buildChallengeReply(token), rinfo.port, rinfo.address);
      else sock.send(A.buildInfoReply({ players: 3 }), rinfo.port, rinfo.address);
    });
    sock.bind(0, '127.0.0.1', () => resolve({ sock, port: sock.address().port }));
  });
}

test('queryInfo answers the challenge and measures the round trip', async () => {
  const { sock, port } = await fakeServer({ challenge: true });
  try {
    const r = await A.queryInfo('127.0.0.1', port, { timeoutMs: 2000 });
    assert.equal(r.ok, true, r.error);
    assert.equal(r.info.players, 3);
    assert.ok(r.rttMs >= 0 && r.rttMs < 2000);
  } finally {
    sock.close();
  }
});

test('queryInfo times out cleanly when nothing answers', async () => {
  const quiet = dgram.createSocket('udp4');
  await new Promise((r) => quiet.bind(0, '127.0.0.1', r));
  const port = quiet.address().port;
  try {
    const r = await A.queryInfo('127.0.0.1', port, { timeoutMs: 300 });
    assert.equal(r.ok, false);
    assert.match(r.error, /timeout/);
  } finally {
    quiet.close();
  }
});
