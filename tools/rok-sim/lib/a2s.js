'use strict';

// Steam server queries (A2S) on UDP steamAuthPort, as the Steam game-server library inside ROK.exe
// would answer them. Packet layout: Valve developer wiki "Server queries" [SEC, generic Steam].
//
// What the game itself sets [DEC SteamServer.StartServer]: GameServer.Init(0, 8766, portNumber,
// steamAuthPort, AuthenticationAndSecure, "1.0.0.0"), SetDedicatedServer(true),
// SetProduct("Reign Of Kings"), SetGameDescription("ROK Server"), SetServerName("Another ROK Server").
// It never sets max players, map, password flag, rules or keywords, so A2S reports the fixed
// name, description "ROK Server", max players 0 and an empty map (docs/join-and-scale.md 3.1).
//
// UNVERIFIED (no real server answer was captured), all configurable:
//  - whether a challenge is required for A2S_INFO (Valve added it in 2020; the steamclient the RoK
//    server loads may be older). Default: required, which is the harder case for a client.
//  - the player count (players are registered through BeginAuthSession). Default: the simulated count.
//  - the folder (mod dir) string, the 16-bit app id field (default 344760 & 0xFFFF) and the EDF block.

const dgram = require('dgram');
const crypto = require('crypto');

const HEADER = Buffer.from([0xff, 0xff, 0xff, 0xff]);
const T = { INFO: 0x54, PLAYER: 0x55, RULES: 0x56, GETCHALLENGE: 0x57, S2C_CHALLENGE: 0x41, S2A_INFO: 0x49, S2A_PLAYER: 0x44, S2A_RULES: 0x45 };
const QUERY = Buffer.from('Source Engine Query\0', 'latin1');
const APP_ID = 344760;

const cstr = (s) => Buffer.concat([Buffer.from(String(s), 'utf8'), Buffer.from([0])]);

function buildInfo(o) {
  const head = Buffer.concat([HEADER, Buffer.from([T.S2A_INFO, 17]), cstr(o.name), cstr(o.map), cstr(o.folder), cstr(o.game)]);
  const fixed = Buffer.alloc(9);
  fixed.writeUInt16LE(o.shortAppId & 0xffff, 0);
  fixed[2] = Math.min(255, o.players);
  fixed[3] = Math.min(255, o.maxPlayers);
  fixed[4] = o.bots;
  fixed[5] = 'd'.charCodeAt(0);
  fixed[6] = 'w'.charCodeAt(0);
  fixed[7] = o.passworded ? 1 : 0;
  fixed[8] = o.vac ? 1 : 0;
  const edfParts = [];
  let edf = 0;
  if (o.gamePort != null) {
    edf |= 0x80;
    const b = Buffer.alloc(2);
    b.writeUInt16LE(o.gamePort);
    edfParts.push(b);
  }
  if (o.steamId != null) {
    edf |= 0x10;
    const b = Buffer.alloc(8);
    b.writeBigUInt64LE(BigInt(o.steamId));
    edfParts.push(b);
  }
  if (o.gameId != null) {
    edf |= 0x01;
    const b = Buffer.alloc(8);
    b.writeBigUInt64LE(BigInt(o.gameId));
    edfParts.push(b);
  }
  return Buffer.concat([head, fixed, cstr(o.version), Buffer.from([edf]), ...edfParts]);
}

function buildPlayers(players, nowMs) {
  const parts = [HEADER, Buffer.from([T.S2A_PLAYER, Math.min(255, players.length)])];
  players.slice(0, 255).forEach((p, i) => {
    const tail = Buffer.alloc(8);
    tail.writeInt32LE(0, 0); // score
    tail.writeFloatLE(Math.max(0, (nowMs - p.joinedAt) / 1000), 4);
    parts.push(Buffer.from([i]), cstr(p.name), tail);
  });
  return Buffer.concat(parts);
}

function buildRules(rules) {
  const n = Buffer.alloc(2);
  n.writeUInt16LE(rules.length);
  return Buffer.concat([HEADER, Buffer.from([T.S2A_RULES]), n, ...rules.flatMap(([k, v]) => [cstr(k), cstr(v)])]);
}

class A2SServer {
  // opts: port, host ('0.0.0.0'), challenge (true), info() -> fields, players() -> [{name, joinedAt}],
  // dropRate (0..1, chaos: ignore that share of requests), now()
  constructor(opts) {
    this.opts = opts;
    this.sock = null;
    this.tokens = new Map(); // "addr:port" -> { token, at }
    this.stats = { info: 0, player: 0, rules: 0, challenges: 0, dropped: 0, ignored: 0 };
  }

  start() {
    return new Promise((resolve, reject) => {
      const sock = dgram.createSocket({ type: 'udp4', reuseAddr: false });
      sock.once('error', reject);
      sock.on('message', (msg, rinfo) => this.onMessage(msg, rinfo));
      sock.bind(this.opts.port, this.opts.host || '0.0.0.0', () => {
        sock.removeListener('error', reject);
        sock.on('error', () => {});
        this.sock = sock;
        this.port = sock.address().port;
        resolve(this.port);
      });
    });
  }

  tokenFor(rinfo) {
    const k = `${rinfo.address}:${rinfo.port}`;
    let t = this.tokens.get(k);
    if (!t || Date.now() - t.at > 30000) {
      t = { token: crypto.randomBytes(4), at: Date.now() };
      if (t.token.equals(Buffer.from([0xff, 0xff, 0xff, 0xff]))) t.token[0] = 0;
      this.tokens.set(k, t);
      if (this.tokens.size > 4096) this.tokens.delete(this.tokens.keys().next().value);
    }
    return t.token;
  }

  reply(buf, rinfo) {
    if (this.sock) this.sock.send(buf, rinfo.port, rinfo.address, () => {});
  }

  challenge(rinfo) {
    this.stats.challenges++;
    this.reply(Buffer.concat([HEADER, Buffer.from([T.S2C_CHALLENGE]), this.tokenFor(rinfo)]), rinfo);
  }

  onMessage(msg, rinfo) {
    if (msg.length < 5 || !msg.subarray(0, 4).equals(HEADER)) return void this.stats.ignored++;
    if (this.opts.dropRate && Math.random() < this.opts.dropRate) return void this.stats.dropped++;
    const kind = msg[4];
    const valid = (c) => c && c.length === 4 && c.equals(this.tokenFor(rinfo));
    if (kind === T.INFO) {
      if (msg.length < 5 + QUERY.length || !msg.subarray(5, 5 + QUERY.length).equals(QUERY)) return void this.stats.ignored++;
      const c = msg.length >= 5 + QUERY.length + 4 ? msg.subarray(5 + QUERY.length, 5 + QUERY.length + 4) : null;
      if (this.opts.challenge !== false && !valid(c)) return this.challenge(rinfo);
      this.stats.info++;
      return this.reply(buildInfo(this.opts.info()), rinfo);
    }
    if (kind === T.PLAYER || kind === T.RULES) {
      const c = msg.length >= 9 ? msg.subarray(5, 9) : null;
      if (!valid(c)) return this.challenge(rinfo); // A2S_PLAYER / A2S_RULES always need the challenge
      if (kind === T.PLAYER) {
        this.stats.player++;
        return this.reply(buildPlayers(this.opts.players(), Date.now()), rinfo);
      }
      this.stats.rules++;
      return this.reply(buildRules([]), rinfo); // the game sets no key/values [DEC]
    }
    if (kind === T.GETCHALLENGE) return this.challenge(rinfo);
    this.stats.ignored++;
  }

  close() {
    return new Promise((resolve) => {
      if (!this.sock) return resolve();
      const s = this.sock;
      this.sock = null;
      try {
        s.close(() => resolve());
      } catch {
        resolve();
      }
    });
  }
}

// ---------------------------------------------------------------- client (tests, `rok-sim query`)

function readCString(buf, o) {
  const end = buf.indexOf(0, o);
  if (end < 0) throw new RangeError('unterminated string');
  return [buf.toString('utf8', o, end), end + 1];
}

function parseInfo(buf) {
  if (buf.length < 6 || !buf.subarray(0, 4).equals(HEADER) || buf[4] !== T.S2A_INFO) throw new RangeError('not an S2A_INFO packet');
  let o = 5;
  const r = { protocol: buf[o++] };
  [r.name, o] = readCString(buf, o);
  [r.map, o] = readCString(buf, o);
  [r.folder, o] = readCString(buf, o);
  [r.game, o] = readCString(buf, o);
  if (buf.length < o + 9) throw new RangeError('truncated');
  r.shortAppId = buf.readUInt16LE(o);
  o += 2;
  r.players = buf[o++];
  r.maxPlayers = buf[o++];
  r.bots = buf[o++];
  r.serverType = String.fromCharCode(buf[o++]);
  r.environment = String.fromCharCode(buf[o++]);
  r.passworded = buf[o++] === 1;
  r.vac = buf[o++] === 1;
  [r.version, o] = readCString(buf, o);
  if (o < buf.length) {
    const edf = buf[o++];
    if (edf & 0x80) {
      r.gamePort = buf.readUInt16LE(o);
      o += 2;
    }
    if (edf & 0x10) {
      r.steamId = buf.readBigUInt64LE(o).toString();
      o += 8;
    }
    if (edf & 0x40) {
      r.tvPort = buf.readUInt16LE(o);
      o += 2;
      [r.tvName, o] = readCString(buf, o);
    }
    if (edf & 0x20) [r.keywords, o] = readCString(buf, o);
    if (edf & 0x01) {
      r.gameId = buf.readBigUInt64LE(o).toString();
      o += 8;
    }
  }
  return r;
}

function parsePlayers(buf) {
  if (buf[4] !== T.S2A_PLAYER) throw new RangeError('not an S2A_PLAYER packet');
  const n = buf[5];
  let o = 6;
  const out = [];
  for (let i = 0; i < n; i++) {
    const index = buf[o++];
    let name;
    [name, o] = readCString(buf, o);
    const score = buf.readInt32LE(o);
    const duration = buf.readFloatLE(o + 4);
    o += 8;
    out.push({ index, name, score, duration });
  }
  return out;
}

function parseRules(buf) {
  if (buf[4] !== T.S2A_RULES) throw new RangeError('not an S2A_RULES packet');
  const n = buf.readUInt16LE(5);
  let o = 7;
  const out = {};
  for (let i = 0; i < n; i++) {
    let k;
    let v;
    [k, o] = readCString(buf, o);
    [v, o] = readCString(buf, o);
    out[k] = v;
  }
  return out;
}

// kind: 'info' | 'players' | 'rules'. Answers up to 3 challenges. Resolves { ok, value, challenged, rttMs } or { ok:false, error }.
function query(host, port, kind = 'info', { timeoutMs = 1500 } = {}) {
  return new Promise((resolve) => {
    const sock = dgram.createSocket('udp4');
    let challenged = 0;
    let sentAt = 0;
    let done = false;
    const finish = (r) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try {
        sock.close();
      } catch {
        /* closed */
      }
      resolve(r);
    };
    const req = (c) => {
      if (kind === 'info') return Buffer.concat([HEADER, Buffer.from([T.INFO]), QUERY, c || Buffer.alloc(0)]);
      return Buffer.concat([HEADER, Buffer.from([kind === 'players' ? T.PLAYER : T.RULES]), c || HEADER]);
    };
    const send = (c) => {
      sentAt = Date.now();
      sock.send(req(c), port, host, (err) => err && finish({ ok: false, error: err.message }));
    };
    const timer = setTimeout(() => finish({ ok: false, error: 'timeout' }), timeoutMs);
    sock.on('error', (e) => finish({ ok: false, error: e.message }));
    sock.on('message', (msg) => {
      try {
        if (msg[4] === T.S2C_CHALLENGE) {
          if (++challenged > 3) return finish({ ok: false, error: 'too many challenges' });
          return send(msg.subarray(5, 9));
        }
        const value = kind === 'info' ? parseInfo(msg) : kind === 'players' ? parsePlayers(msg) : parseRules(msg);
        finish({ ok: true, value, challenged, rttMs: Date.now() - sentAt });
      } catch (e) {
        finish({ ok: false, error: e.message });
      }
    });
    send(null);
  });
}

module.exports = { A2SServer, query, buildInfo, parseInfo, parsePlayers, parseRules, APP_ID, HEADER, T };
