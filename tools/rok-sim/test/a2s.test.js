'use strict';

// The A2S responder: challenge flow, the fixed values the game reports, players, rules, chaos drops,
// and (when the launcher is in the tree) the launcher's own A2S client against it.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const dgram = require('dgram');
const A = require('../lib/a2s');

function server(extra = {}) {
  const players = [{ name: 'Wren', joinedAt: Date.now() - 5000 }, { name: 'Odo the Tall', joinedAt: Date.now() - 1000 }];
  const s = new A.A2SServer({
    port: 0,
    host: '127.0.0.1',
    challenge: true,
    info: () => ({ name: 'Another ROK Server', map: '', folder: '', game: 'ROK Server', shortAppId: A.APP_ID & 0xffff, players: players.length, maxPlayers: 0, bots: 0, passworded: false, vac: true, version: '1.0.0.0', gamePort: 7350, steamId: '90000000000000001', gameId: A.APP_ID }),
    players: () => players,
    ...extra
  });
  return s;
}

test('A2S_INFO needs a challenge, then reports the fixed RoK values', async () => {
  const s = server();
  const port = await s.start();
  try {
    const r = await A.query('127.0.0.1', port, 'info');
    assert.equal(r.ok, true, r.error);
    assert.equal(r.challenged, 1);
    const v = r.value;
    assert.equal(v.name, 'Another ROK Server');
    assert.equal(v.game, 'ROK Server');
    assert.equal(v.map, '');
    assert.equal(v.maxPlayers, 0);
    assert.equal(v.players, 2);
    assert.equal(v.serverType, 'd');
    assert.equal(v.environment, 'w');
    assert.equal(v.vac, true);
    assert.equal(v.version, '1.0.0.0');
    assert.equal(v.gamePort, 7350);
    assert.equal(v.gameId, '344760');
    assert.equal(s.stats.challenges, 1);
  } finally {
    await s.close();
  }
});

test('without the challenge requirement INFO answers at once; PLAYER and RULES still need one', async () => {
  const s = server({ challenge: false });
  const port = await s.start();
  try {
    const i = await A.query('127.0.0.1', port, 'info');
    assert.equal(i.challenged, 0);
    const p = await A.query('127.0.0.1', port, 'players');
    assert.equal(p.ok, true);
    assert.equal(p.challenged, 1);
    assert.deepEqual(p.value.map((x) => x.name), ['Wren', 'Odo the Tall']);
    assert.ok(p.value[0].duration >= 4 && p.value[0].duration < 60);
    const r = await A.query('127.0.0.1', port, 'rules');
    assert.equal(r.ok, true);
    assert.deepEqual(r.value, {});
  } finally {
    await s.close();
  }
});

test('garbage is ignored and a wrong challenge is answered with a new challenge', async () => {
  const s = server();
  const port = await s.start();
  const c = dgram.createSocket('udp4');
  await new Promise((r) => c.bind(0, '127.0.0.1', r));
  const replies = [];
  c.on('message', (m) => replies.push(m));
  const send = (b) => new Promise((r) => c.send(b, port, '127.0.0.1', r));
  try {
    await send(Buffer.from('hello'));
    await send(Buffer.from([0xff, 0xff, 0xff, 0xff, 0x54, 0x41])); // INFO without the query string
    await send(Buffer.concat([A.HEADER, Buffer.from([0x54]), Buffer.from('Source Engine Query\0', 'latin1'), Buffer.from([1, 2, 3, 4])]));
    await new Promise((r) => setTimeout(r, 150));
    assert.equal(replies.length, 1);
    assert.equal(replies[0][4], 0x41);
    assert.equal(s.stats.ignored, 2);
  } finally {
    c.close();
    await s.close();
  }
});

test('drop rate 1 drops everything (chaos switch)', async () => {
  const s = server({ dropRate: 1 });
  const port = await s.start();
  try {
    const r = await A.query('127.0.0.1', port, 'info', { timeoutMs: 300 });
    assert.equal(r.ok, false);
    assert.match(r.error, /timeout/);
    assert.ok(s.stats.dropped >= 1);
  } finally {
    await s.close();
  }
});

const LAUNCHER_A2S = path.resolve(__dirname, '../../../launcher/lib/shared/a2s.js');
test('the launcher A2S client reads the simulator (cross-implementation)', { skip: !fs.existsSync(LAUNCHER_A2S) && 'launcher not in this tree' }, async () => {
  const L = require(LAUNCHER_A2S);
  const s = server();
  const port = await s.start();
  try {
    const r = await L.queryInfo('127.0.0.1', port, { timeoutMs: 2000 });
    assert.equal(r.ok, true, r.error);
    assert.equal(r.info.name, 'Another ROK Server');
    assert.equal(r.info.game, 'ROK Server');
    assert.equal(r.info.players, 2);
    assert.equal(r.info.maxPlayers, 0);
  } finally {
    await s.close();
  }
});
