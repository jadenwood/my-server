'use strict';

// The admin console wire format, written from the decompiled game code (Oxide.ReignOfKings 2.0.3867
// zip sha256 6c35c623...c72c6c8, Assembly-CSharp.dll): CodeHatch.Engine.Sockets.Packet
// (Serialize, Deserialize, GetPackets, ReadPackets) and SocketAdminConsole (Encoding.ASCII both ways).
// This file is written independently of launcher/lib/admin-console.js so that tests can play one
// against the other.
//
//   int32 LE size   = body length + 14 (the whole frame, this field included)
//   int32 LE id
//   int32 LE type   0 Authentication, 1 Message, 2 Response ("Reponse" in the game), 3 Disconnect
//   body            ASCII
//   0x00 0x00       anything else: FormatException("Last two bytes of the packet were not zero.")
//
// GetPackets cuts a message into bodies of 4082 bytes (frames of 4096) and ALWAYS appends the
// remainder packet, even when it is empty. ReadPackets keeps reading while a frame's size is 4096.

const TYPE = Object.freeze({ AUTHENTICATION: 0, MESSAGE: 1, RESPONSE: 2, DISCONNECT: 3 });
const MAX_PACKET_SIZE = 4096;
const MAX_BODY_SIZE = 4082;
const EMPTY_PACKET_SIZE = 14;
// Not in the game: a guard so that a garbage size field cannot make the simulator buffer gigabytes.
// The game would try to read that many bytes (BinaryReader), which for a test tool is the same as hanging.
const SIM_MAX_FRAME = 16 * 1024 * 1024;

// Encoding.ASCII.GetBytes: every char above 0x7F becomes '?'. A surrogate pair is one '?'
// (the .NET replacement fallback runs once per unknown code point).
function toAscii(text) {
  if (text == null) return null;
  const out = [];
  for (const ch of String(text)) {
    const c = ch.codePointAt(0);
    out.push(c <= 0x7f ? c : 0x3f);
  }
  return Buffer.from(out);
}

// Encoding.ASCII.GetString: every byte above 0x7F becomes '?'.
function fromAscii(buf) {
  let s = '';
  for (const b of buf) s += b <= 0x7f ? String.fromCharCode(b) : '?';
  return s;
}

function encodePacket(id, type, body) {
  const b = body ? body : Buffer.alloc(0);
  const out = Buffer.alloc(b.length + EMPTY_PACKET_SIZE);
  out.writeInt32LE(b.length + EMPTY_PACKET_SIZE, 0);
  out.writeInt32LE(id | 0, 4);
  out.writeInt32LE(type | 0, 8);
  b.copy(out, 12);
  return out; // last two bytes already zero
}

// Packet.GetPackets(id, type, messageBuffer) concatenated into one write.
// body: null (one empty packet), a Buffer, or a string (converted with toAscii).
function getPackets(id, type, body) {
  if (body == null) return encodePacket(id, type, null);
  const buf = Buffer.isBuffer(body) ? body : toAscii(body);
  const parts = [];
  const full = Math.floor(buf.length / MAX_BODY_SIZE);
  for (let i = 0; i < full; i++) parts.push(encodePacket(id, type, buf.subarray(i * MAX_BODY_SIZE, (i + 1) * MAX_BODY_SIZE)));
  parts.push(encodePacket(id, type, buf.subarray(full * MAX_BODY_SIZE)));
  return Buffer.concat(parts);
}

class FrameError extends Error {
  constructor(exceptionType, message) {
    super(message);
    this.exceptionType = exceptionType; // the .NET exception the game's reader would throw
  }
}

// Incremental ReadPackets. push(chunk) returns the complete packet groups it finished:
// [{ id, type, body: Buffer, packets }]. On a bad frame it sets this.dead to a FrameError (groups
// finished before it are still returned) and from then on ignores everything, like the game's
// receive loop for that client.
class PacketReader {
  constructor() {
    this.buf = Buffer.alloc(0);
    this.group = [];
    this.dead = null;
  }

  push(chunk) {
    if (this.dead) return [];
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : Buffer.from(chunk);
    const done = [];
    while (this.buf.length >= 4) {
      const size = this.buf.readInt32LE(0);
      if (size < EMPTY_PACKET_SIZE) {
        // new byte[size - 14] with a negative length
        this.dead = new FrameError('OverflowException', 'Arithmetic operation resulted in an overflow.');
        break;
      }
      if (size > SIM_MAX_FRAME) {
        this.dead = new FrameError('OutOfMemoryException', `rok-sim refuses a ${size}-byte frame (simulator guard, not game behaviour).`);
        break;
      }
      if (this.buf.length < size) break;
      const id = this.buf.readInt32LE(4);
      const type = this.buf.readInt32LE(8);
      if (this.buf[size - 2] !== 0 || this.buf[size - 1] !== 0) {
        this.dead = new FrameError('FormatException', 'Last two bytes of the packet were not zero.');
        break;
      }
      const body = Buffer.from(this.buf.subarray(12, size - 2));
      this.buf = this.buf.subarray(size);
      this.group.push({ id, type, body, size });
      if (size === MAX_PACKET_SIZE) continue;
      const g = this.group;
      this.group = [];
      done.push({ id: g[0].id, type: g[0].type, body: Buffer.concat(g.map((p) => p.body)), packets: g.length });
    }
    return done;
  }
}

module.exports = {
  TYPE,
  MAX_PACKET_SIZE,
  MAX_BODY_SIZE,
  EMPTY_PACKET_SIZE,
  toAscii,
  fromAscii,
  encodePacket,
  getPackets,
  PacketReader,
  FrameError
};
