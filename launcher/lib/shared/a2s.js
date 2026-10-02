'use strict';

// Steam A2S_INFO, used for liveness and ping only.
//
// What the research proved (docs/join-and-scale.md §3.1, [DEC] SteamServer.StartServer): the RoK
// dedicated server calls GameServer.Init(0, 8766, portNumber, steamAuthPort, ...), so Steam's
// game-server library answers A2S on UDP **steamAuthPort** (the "query port"). The game never
// sets the real name or max players: A2S reports "Another ROK Server" and max players 0.
// So the Realm client takes names and slots from the signed list, and player counts from the
// Chronicle when one is listed. UNVERIFIED at run time: that A2S answers at all, and whether its
// player count is right; the client labels A2S player counts as approximate.
//
// Packet format: Valve developer wiki "Server queries" (generic Steam protocol). Since 2020 servers
// may answer the first request with S2C_CHALLENGE ('A' + 4 bytes); the request is then repeated
// with that challenge appended.

const dgram = require('dgram');

const HEADER = Buffer.from([0xff, 0xff, 0xff, 0xff]);
const A2S_INFO = 0x54;
const S2C_CHALLENGE = 0x41;
const S2A_INFO = 0x49;
const QUERY_STRING = Buffer.from('Source Engine Query\0', 'latin1');

function buildInfoRequest(challenge) {
  const parts = [HEADER, Buffer.from([A2S_INFO]), QUERY_STRING];
  if (challenge != null) {
    if (!Buffer.isBuffer(challenge) || challenge.length !== 4) throw new TypeError('challenge must be 4 bytes');
    parts.push(challenge);
  }
  return Buffer.concat(parts);
}

function readCString(buf, offset) {
  const end = buf.indexOf(0, offset);
  if (end < 0) throw new RangeError('unterminated string');
  return { value: buf.toString('utf8', offset, end).slice(0, 256), next: end + 1 };
}

// Returns { type: 'challenge', challenge } or { type: 'info', ...fields }. Throws on anything else.
function parseResponse(buf) {
  if (!Buffer.isBuffer(buf) || buf.length < 5) throw new RangeError('packet too short');
  if (!buf.subarray(0, 4).equals(HEADER)) throw new RangeError('split or unknown packet header');
  const type = buf[4];
  if (type === S2C_CHALLENGE) {
    if (buf.length < 9) throw new RangeError('short challenge');
    return { type: 'challenge', challenge: Buffer.from(buf.subarray(5, 9)) };
  }
  if (type !== S2A_INFO) throw new RangeError(`unexpected reply type 0x${type.toString(16)}`);
  let o = 5;
  const protocol = buf[o++];
  const name = readCString(buf, o);
  const map = readCString(buf, name.next);
  const folder = readCString(buf, map.next);
  const game = readCString(buf, folder.next);
  o = game.next;
  if (buf.length < o + 9) throw new RangeError('info reply truncated');
  const appId = buf.readUInt16LE(o);
  o += 2;
  const players = buf[o++];
  const maxPlayers = buf[o++];
  const bots = buf[o++];
  const serverType = String.fromCharCode(buf[o++]);
  const environment = String.fromCharCode(buf[o++]);
  const visibility = buf[o++];
  const vac = buf[o++];
  return { type: 'info', protocol, name: name.value, map: map.value, folder: folder.value, game: game.value, appId, players, maxPlayers, bots, serverType, environment, passworded: visibility === 1, vac: vac === 1 };
}

// Sends A2S_INFO (answering one challenge). Resolves { ok, rttMs, info } or { ok: false, error }.
function queryInfo(host, port, { timeoutMs = 1500, createSocket = () => dgram.createSocket('udp4') } = {}) {
  return new Promise((resolve) => {
    let sock;
    try {
      sock = createSocket();
    } catch (e) {
      resolve({ ok: false, error: e.message });
      return;
    }
    let done = false;
    let sentAt = 0;
    let challenges = 0;
    const finish = (res) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try {
        sock.close();
      } catch {
        /* already closed */
      }
      resolve(res);
    };
    const send = (challenge) => {
      sentAt = Date.now();
      sock.send(buildInfoRequest(challenge), port, host, (err) => {
        if (err) finish({ ok: false, error: err.message });
      });
    };
    const timer = setTimeout(() => finish({ ok: false, error: 'no answer (timeout)' }), timeoutMs);
    sock.on('error', (e) => finish({ ok: false, error: e.message }));
    sock.on('message', (msg) => {
      let r;
      try {
        r = parseResponse(msg);
      } catch (e) {
        finish({ ok: false, error: e.message });
        return;
      }
      if (r.type === 'challenge') {
        if (++challenges > 2) return finish({ ok: false, error: 'too many challenges' });
        send(r.challenge);
        return;
      }
      finish({ ok: true, rttMs: Date.now() - sentAt, info: r });
    });
    send(null);
  });
}

// Test helper and fake-server helper: the bytes a server would send for an info reply.
function buildInfoReply({ name = 'Another ROK Server', map = '', folder = 'rok', game = 'ROK Server', appId = 0, players = 0, maxPlayers = 0, bots = 0, serverType = 'd', environment = 'w', passworded = false, vac = true } = {}) {
  const str = (s) => Buffer.concat([Buffer.from(String(s), 'utf8'), Buffer.from([0])]);
  const id = Buffer.alloc(2);
  id.writeUInt16LE(appId & 0xffff);
  return Buffer.concat([
    HEADER,
    Buffer.from([S2A_INFO, 17]),
    str(name),
    str(map),
    str(folder),
    str(game),
    id,
    Buffer.from([players & 0xff, maxPlayers & 0xff, bots & 0xff, serverType.charCodeAt(0), environment.charCodeAt(0), passworded ? 1 : 0, vac ? 1 : 0])
  ]);
}

function buildChallengeReply(challenge) {
  return Buffer.concat([HEADER, Buffer.from([S2C_CHALLENGE]), challenge]);
}

module.exports = { buildInfoRequest, parseResponse, queryInfo, buildInfoReply, buildChallengeReply };
