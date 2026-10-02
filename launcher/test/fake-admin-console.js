'use strict';

// Imitation of the game's admin console socket (SocketAdminConsole) for tests and the Court
// walk-through. It has its OWN frame reader and writer, written from the decompiled C#
// (Packet.Serialize / Deserialize / GetPackets / ReadPackets), so the tests check the client
// against an independent implementation rather than against itself.
//
// Imitated behaviour ([DEC]): keep-alive empty Message every second; command output as one
// "[I] ..." Message per line, then a Response with the request id; text without "/" is chat;
// /shutdown sends Disconnect and closes; with cportRule, shuts down if no client within the
// window; shuts down when the last client disconnects.

const net = require('net');
const { EventEmitter } = require('events');

function frame(id, type, body) {
  const b = body == null ? Buffer.alloc(0) : Buffer.from(body, 'latin1');
  const out = Buffer.alloc(b.length + 14);
  out.writeInt32LE(b.length + 14, 0);
  out.writeInt32LE(id, 4);
  out.writeInt32LE(type, 8);
  b.copy(out, 12);
  return out;
}

// GetPackets
function packets(id, type, text) {
  if (text == null) return frame(id, type, null);
  const b = Buffer.from(text, 'latin1');
  const parts = [];
  const n = Math.floor(b.length / 4082);
  for (let i = 0; i < n; i++) parts.push(frame(id, type, b.subarray(i * 4082, (i + 1) * 4082).toString('latin1')));
  parts.push(frame(id, type, b.subarray(n * 4082).toString('latin1')));
  return Buffer.concat(parts);
}

class FakeAdminConsole extends EventEmitter {
  constructor({ players = ['Wren', 'Odo the Tall'], bans = [], keepAliveMs = 1000, cportRuleMs = 0, log = null } = {}) {
    super();
    this.players = players.map((p, i) => (typeof p === 'string' ? { name: p, id: String(76561190000000002n + BigInt(i)) } : p));
    this.bans = bans.slice();
    this.keepAliveMs = keepAliveMs;
    this.cportRuleMs = cportRuleMs;
    this.clients = new Set();
    this.received = [];
    this.whitelist = false;
    this.saves = 0;
    this.chat = [];
    this.notices = [];
    this.shutdown = null;
    this.seq = 0;
    this.msgId = 0;
    this.log = log || (() => {});
    this.server = net.createServer((sock) => this.onClient(sock));
  }

  listen(port = 0, host = '127.0.0.1') {
    return new Promise((resolve, reject) => {
      this.server.once('error', reject);
      this.server.listen(port, host, () => {
        this.port = this.server.address().port;
        this.keepAlive = setInterval(() => this.broadcast(packets(this.msgId++, 1, null)), this.keepAliveMs);
        if (this.cportRuleMs) {
          this.cportTimer = setTimeout(() => {
            if (this.clients.size === 0) this.doShutdown('no admin client within the -cport window');
          }, this.cportRuleMs);
        }
        resolve(this.port);
      });
    });
  }

  broadcast(buf) {
    for (const c of this.clients) if (!c.destroyed) c.write(buf);
  }

  // Server.Log -> SocketAdminConsole.Send: one Message per line.
  logLine(level, text) {
    for (const line of String(text).split('\n')) this.broadcast(packets(this.msgId++, 1, `[${level}] ${line}`));
  }

  onClient(sock) {
    this.clients.add(sock);
    this.emit('client', sock);
    let buf = Buffer.alloc(0);
    let group = [];
    let reading = true;
    sock.on('data', (chunk) => {
      if (!reading) return;
      buf = Buffer.concat([buf, chunk]);
      while (buf.length >= 4) {
        const size = buf.readInt32LE(0);
        if (buf.length < size) break;
        const id = buf.readInt32LE(4);
        const type = buf.readInt32LE(8);
        const body = buf.subarray(12, size - 2);
        if (buf[size - 2] !== 0 || buf[size - 1] !== 0) {
          // The game's reader throws FormatException and never reads from this client again.
          reading = false;
          this.emit('format-error');
          return;
        }
        buf = buf.subarray(size);
        group.push({ id, type, body: Buffer.from(body) });
        if (size === 4096) continue;
        const pk = group;
        group = [];
        this.onPackets(sock, pk);
      }
    });
    sock.on('error', () => {});
    sock.on('close', () => {
      this.clients.delete(sock);
      this.emit('client-gone');
      // OnClientDisconnected: last client gone -> Server.Shutdown()
      if (this.clients.size === 0 && !this.shutdown) this.doShutdown('last admin client disconnected');
    });
  }

  onPackets(sock, pk) {
    const text = Buffer.concat(pk.map((p) => p.body)).toString('latin1');
    const id = pk[0].id;
    this.received.push({ id, type: pk[0].type, text, packets: pk.length });
    this.emit('received', { id, type: pk[0].type, text, packets: pk.length });
    if (pk[0].type === 2) return; // Response from client: submitted, no reply
    const reply = () => {
      if (!sock.destroyed) sock.write(packets(id, 2, ''));
    };
    if (!text) return reply();
    if (!text.startsWith('/')) {
      this.chat.push(text);
      this.broadcast(packets(this.msgId++, 1, `[C] Server: ${text}`));
      return reply();
    }
    const [label, ...args] = text.slice(1).split(' ');
    const out = (s) => this.logLine('I', s);
    switch (label.toLowerCase()) {
      case 'list':
      case 'online':
      case 'players':
        if (this.players.length) {
          out(`Online Players(${this.players.length}):`);
          out(this.players.map((p) => p.name).join(', '));
        } else out('There are no players online.');
        break;
      case 'kick': {
        const name = args.join(' ').replace(/^"([^"]*)".*$/, '$1');
        const i = this.players.findIndex((p) => name.startsWith(p.name));
        if (i < 0) this.logLine('E', `${args[0]} was not found on the server.`);
        else {
          const [p] = this.players.splice(i, 1);
          out(`${p.name} was kicked from the server.`);
        }
        break;
      }
      case 'ban': {
        const m = /^"([^"]+)"|^(\S+)/.exec(args.join(' '));
        const name = m ? m[1] || m[2] : '';
        const p = this.players.find((x) => x.name === name) || { name, id: '76561190000000099' };
        this.bans.push({ name: p.name, id: p.id });
        this.players = this.players.filter((x) => x !== p);
        out(`${p.name} has been banned.`);
        break;
      }
      case 'banlist':
        if (!this.bans.length) this.logLine('E', 'There are no banned players.');
        else this.bans.forEach((b, i) => out(`#${i} ${b.name} |${b.id}| (203.0.113.9) <infinite days left>`));
        break;
      case 'unban': {
        const who = args.join(' ').replace(/^"|"$/g, '');
        const i = this.bans.findIndex((b, j) => b.name === who || b.id === who || String(j) === who);
        if (i < 0) this.logLine('E', `Cannot find banned player with name matching '${who}'.`);
        else out(`${this.bans.splice(i, 1)[0].name} was unbanned.`);
        break;
      }
      case 'whitelist':
        this.whitelist = args[0] === 'enable';
        out(`The whitelist is now ${this.whitelist ? 'enabled' : 'disabled'}.`);
        break;
      case 'notice':
        this.notices.push(args.join(' '));
        break;
      case 'popup':
        this.notices.push('popup: ' + args.join(' '));
        break;
      case 'realm.save':
        this.saves++;
        out('Saving game...');
        out('REALMCOURT|save|ok|World saved.');
        break;
      case 'realm.players':
        this.seq++;
        out(`REALMCOURT|${this.seq}|players|${this.players.length}`);
        for (const p of this.players) out(`REALMCOURT|${this.seq}|p|${p.id}|${p.name}`);
        break;
      case 'shutdown':
        reply();
        out('Server is shutting down...');
        this.doShutdown('/shutdown', true);
        return;
      case 'echo':
        out(args.join(' '));
        break;
      default:
        this.logLine('E', `Unknown command '${text}'. For help type /help`);
    }
    reply();
  }

  doShutdown(why, bye = false) {
    if (this.shutdown) return;
    this.shutdown = why;
    this.log(`fake console: shutting down (${why})`);
    if (bye) this.broadcast(packets(this.msgId++, 3, null));
    this.emit('shutdown', why);
    setTimeout(() => this.close(), 50);
  }

  close() {
    clearInterval(this.keepAlive);
    clearTimeout(this.cportTimer);
    for (const c of this.clients) c.destroy();
    return new Promise((r) => this.server.close(() => r()));
  }
}

module.exports = { FakeAdminConsole, frame, packets };
