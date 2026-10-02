'use strict';

// Admin console framing, against the rules read from Packet.cs [DEC].

const test = require('node:test');
const assert = require('node:assert/strict');
const P = require('../lib/packets');

function frames(buf) {
  const out = [];
  let o = 0;
  while (o < buf.length) {
    const size = buf.readInt32LE(o);
    out.push({ size, id: buf.readInt32LE(o + 4), type: buf.readInt32LE(o + 8), body: buf.subarray(o + 12, o + size - 2), tail: [buf[o + size - 2], buf[o + size - 1]] });
    o += size;
  }
  return out;
}

test('an empty message is one 14-byte frame', () => {
  const b = P.getPackets(7, P.TYPE.MESSAGE, null);
  assert.equal(b.length, 14);
  assert.deepEqual(frames(b)[0], { size: 14, id: 7, type: 1, body: Buffer.alloc(0), tail: [0, 0] });
  assert.equal(P.getPackets(1, P.TYPE.MESSAGE, '').length, 14);
});

test('GetPackets splits at 4082 body bytes and always appends the remainder packet', () => {
  for (const [len, sizes] of [
    [4081, [4095]],
    [4082, [4096, 14]],
    [4083, [4096, 15]],
    [8164, [4096, 4096, 14]],
    [8165, [4096, 4096, 15]]
  ]) {
    const f = frames(P.getPackets(3, P.TYPE.RESPONSE, 'x'.repeat(len)));
    assert.deepEqual(f.map((x) => x.size), sizes, `length ${len}`);
    assert.ok(f.every((x) => x.id === 3 && x.type === 2 && x.tail[0] === 0 && x.tail[1] === 0));
    assert.equal(Buffer.concat(f.map((x) => x.body)).toString('latin1'), 'x'.repeat(len));
  }
});

test('Encoding.ASCII both ways: non-ASCII becomes "?"', () => {
  assert.equal(P.toAscii('Æthel ñ 😀!').toString('latin1'), '?thel ? ?!');
  assert.equal(P.fromAscii(Buffer.from([0x41, 0xc3, 0xa9, 0x7f, 0x80])), 'A??\x7f?');
});

test('the reader groups 4096-byte frames, byte by byte and in random chunks', () => {
  const msgs = ['', 'a', 'y'.repeat(4082), 'z'.repeat(9000), '/list'];
  const stream = Buffer.concat(msgs.map((m, i) => P.getPackets(i + 1, P.TYPE.MESSAGE, m)));
  const byByte = new P.PacketReader();
  const got = [];
  for (const b of stream) got.push(...byByte.push(Buffer.from([b])));
  assert.deepEqual(got.map((g) => P.fromAscii(g.body)), msgs);
  assert.deepEqual(got.map((g) => g.packets), [1, 1, 2, 3, 1]);
  for (let round = 0; round < 25; round++) {
    const r = new P.PacketReader();
    const out = [];
    let o = 0;
    while (o < stream.length) {
      const n = 1 + Math.floor(Math.random() * 5000);
      out.push(...r.push(stream.subarray(o, o + n)));
      o += n;
    }
    assert.deepEqual(out.map((g) => g.id), [1, 2, 3, 4, 5]);
  }
});

test('a bad trailer kills the reader (FormatException) but earlier groups are kept', () => {
  const good = P.getPackets(1, P.TYPE.MESSAGE, '/list');
  const bad = P.getPackets(2, P.TYPE.MESSAGE, 'oops');
  bad[bad.length - 1] = 1;
  const r = new P.PacketReader();
  const g = r.push(Buffer.concat([good, bad, P.getPackets(3, P.TYPE.MESSAGE, 'later')]));
  assert.deepEqual(g.map((x) => x.id), [1]);
  assert.equal(r.dead.exceptionType, 'FormatException');
  assert.equal(r.dead.message, 'Last two bytes of the packet were not zero.');
  assert.deepEqual(r.push(P.getPackets(4, P.TYPE.MESSAGE, 'x')), []);
});

test('a size below 14 is an OverflowException', () => {
  const b = Buffer.alloc(14);
  b.writeInt32LE(3, 0);
  const r = new P.PacketReader();
  assert.deepEqual(r.push(b), []);
  assert.equal(r.dead.exceptionType, 'OverflowException');
});
