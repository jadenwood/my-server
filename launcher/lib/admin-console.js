'use strict';

// Client for the game's own admin console socket (SocketAdminConsole), used by Realm Steward to
// talk to each ROK.exe it runs. Everything below was read from the decompiled game code
// ([DEC], Assembly-CSharp of Oxide.ReignOfKings 2.0.3867); docs/admin-console.md has the details.
//
// Wire format ([DEC] CodeHatch.Engine.Sockets.Packet, little-endian BinaryWriter):
//   int32 size   = body length + 14 (it counts the whole frame, size field included)
//   int32 id
//   int32 type   0 Authentication, 1 Message, 2 Response ("Reponse" in the code), 3 Disconnect
//   body         ASCII bytes (Encoding.ASCII: anything above 0x7F becomes '?')
//   0x00 0x00    two zero bytes; anything else makes the reader throw
// A message is split into packets of at most 4082 body bytes (frame 4096). The reader keeps
// reading while a packet's size is exactly 4096, so a message whose length is an exact multiple
// of 4082 ends with an extra empty packet ([DEC] Packet.GetPackets / ReadPackets).
//
// Behaviour of the server side ([DEC] SocketAdminConsole, SocketServer, SocketClient):
// - Listens on 0.0.0.0:<-cport> (default 11000) whenever the game runs as a dedicated server.
// - Every packet that is not a Response is run as console input (Console.Submit). Input that does
//   not start with "/" is CHAT from the server, not a command. The reply is one Response packet
//   group with the same id. Its body is usually empty: command output is logged instead and
//   reaches us as Message packets ("[I] ...") before that Response, in order.
// - Every second it broadcasts an empty Message (keep-alive). Log lines arrive as Messages
//   prefixed [D] [I] [W] [E] [X] (exception + stack) and player chat as [C].
// - On exit with no restart wanted (/shutdown, first run) it sends a Disconnect packet first.
// - With -cport on the command line it shuts the server down if no client is connected 10 s
//   after the console starts. With or without -cport, it shuts the server down (saving the world)
//   when the LAST client disconnects. So closing this connection stops the server: we only ever
//   close it on purpose, as a stop.
// - There is no authentication. Never send an Authentication packet: its body would be run as a
//   command too. A malformed packet stops the server's reader for this client for good (it keeps
//   the socket open but never reads again), so the encoder below must be exact.

const net = require('net');
const { EventEmitter } = require('events');

const TYPE = Object.freeze({ AUTH: 0, MESSAGE: 1, RESPONSE: 2, DISCONNECT: 3 });
const TYPE_NAMES = ['Authentication', 'Message', 'Response', 'Disconnect'];
const HEADER = 12;
const TRAILER = 2;
const EMPTY_SIZE = 14;
const MAX_PACKET = 4096;
const MAX_BODY = 4082;
// Commands are single lines and short; this only guards against pasting a novel by mistake.
const MAX_COMMAND = 2000;
const LOOPBACK = '127.0.0.1';
// The ports Realm hands out as -cport, one per server slot. lib/fleet.js keeps 11000-11003 out of
// every other use, and Go Public blocks them inbound in Windows Firewall.
const CPORT_BASE = 11000;

class ProtocolError extends Error {}

function cportForSlot(slot) {
  if (!Number.isInteger(slot) || slot < 1 || slot > 4) throw new RangeError('slot must be 1 to 4');
  return CPORT_BASE + slot - 1;
}

function cportForId(id) {
  const m = /^s([1-4])$/.exec(String(id));
  if (!m) throw new RangeError('instance id must be s1 to s4');
  return cportForSlot(Number(m[1]));
}

// Encoding.ASCII.GetBytes: one byte per UTF-16 code unit, '?' for anything outside 0..0x7F.
function toAscii(text) {
  const s = String(text);
  const out = Buffer.alloc(s.length);
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    out[i] = c < 0x80 ? c : 0x3f;
  }
  return out;
}

function fromAscii(buf) {
  // Encoding.ASCII.GetString maps bytes above 0x7F to '?'; the server never sends them anyway.
  let s = '';
  for (let i = 0; i < buf.length; i++) s += String.fromCharCode(buf[i] < 0x80 ? buf[i] : 0x3f);
  return s;
}

function encodePacket(id, type, body = Buffer.alloc(0)) {
  if (!Number.isInteger(id) || id < -0x80000000 || id > 0x7fffffff) throw new RangeError('packet id must be an int32');
  if (!Number.isInteger(type) || type < 0 || type > 3) throw new RangeError('packet type must be 0 to 3');
  if (body.length > MAX_BODY) throw new RangeError(`packet body is limited to ${MAX_BODY} bytes`);
  const buf = Buffer.alloc(body.length + EMPTY_SIZE);
  buf.writeInt32LE(body.length + EMPTY_SIZE, 0);
  buf.writeInt32LE(id, 4);
  buf.writeInt32LE(type, 8);
  body.copy(buf, HEADER);
  // the two trailing bytes are already zero
  return buf;
}

// Packet.GetPackets: null body -> one empty packet; otherwise floor(n/4082) full packets and then
// one packet with the remainder, which is empty when n is a multiple of 4082 (that empty packet is
// what ends the message for the reader).
function encodeMessage(id, type, payload) {
  if (payload == null) return encodePacket(id, type);
  const body = Buffer.isBuffer(payload) ? payload : toAscii(payload);
  const parts = [];
  const full = Math.floor(body.length / MAX_BODY);
  for (let i = 0; i < full; i++) parts.push(encodePacket(id, type, body.subarray(i * MAX_BODY, (i + 1) * MAX_BODY)));
  parts.push(encodePacket(id, type, body.subarray(full * MAX_BODY)));
  return Buffer.concat(parts);
}

// Incremental reader: feed it whatever TCP hands over (partial frames, several frames, frames
// split anywhere) and it returns whole messages. Throws ProtocolError on a frame the server could
// not have produced; after that the stream cannot be trusted and the decoder stays failed.
class PacketDecoder {
  constructor() {
    this.buf = Buffer.alloc(0);
    this.group = [];
    this.failed = null;
  }

  push(chunk) {
    if (this.failed) throw this.failed;
    this.buf = this.buf.length ? Buffer.concat([this.buf, chunk]) : Buffer.from(chunk);
    const out = [];
    for (;;) {
      if (this.buf.length < 4) break;
      const size = this.buf.readInt32LE(0);
      if (size < EMPTY_SIZE || size > MAX_PACKET) return this.fail(`packet size ${size} is outside ${EMPTY_SIZE}..${MAX_PACKET}`);
      if (this.buf.length < size) break;
      const id = this.buf.readInt32LE(4);
      const type = this.buf.readInt32LE(8);
      if (type < 0 || type > 3) return this.fail(`unknown packet type ${type}`);
      if (this.buf[size - 2] !== 0 || this.buf[size - 1] !== 0) return this.fail('the last two bytes of a packet were not zero');
      const body = Buffer.from(this.buf.subarray(HEADER, size - TRAILER));
      this.buf = this.buf.subarray(size);
      this.group.push({ id, type, body });
      if (size === MAX_PACKET) continue; // more packets of this message follow
      const first = this.group[0];
      out.push({ id: first.id, type: first.type, body: Buffer.concat(this.group.map((p) => p.body)), packets: this.group.length });
      this.group = [];
    }
    return out;
  }

  fail(msg) {
    this.failed = new ProtocolError(msg);
    throw this.failed;
  }

  get pending() {
    return this.buf.length + this.group.reduce((n, p) => n + p.body.length + EMPTY_SIZE, 0);
  }
}

// "[I] text" -> { level: 'I', text }. Exceptions ([X]) carry their stack on following lines.
const LEVELS = { D: 'debug', I: 'info', W: 'warning', E: 'error', X: 'exception', C: 'chat' };
function parseLogBody(text) {
  const m = /^\[([DIWEXC])\] ?([\s\S]*)$/.exec(text);
  if (!m) return { level: null, kind: 'raw', text };
  return { level: m[1], kind: LEVELS[m[1]], text: m[2] };
}

// One argument for the game's command parser ([DEC] CommandInfo.Args): words split on white
// space; "double" or 'single' quotes group words but the quoted text may not contain either quote
// character; "\ " is an escaped space. A single quote can never survive the parser intact (the
// game turns "\'" into " Q"), so such names are refused rather than sent garbled.
function quoteArg(value) {
  const s = String(value).replace(/[\r\n\t]+/g, ' ').trim();
  if (!s) throw new Error('An empty name or value cannot be sent.');
  if (s.includes("'")) throw new Error(`"${s}" contains an apostrophe, which the game's command parser cannot handle. Use the Steam ID or ban-list number instead.`);
  if (!/[\s"]/.test(s)) return s;
  if (!s.includes('"')) return `"${s}"`;
  return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/ /g, '\\ ');
}

// Normalizes what the owner typed into a single console line. Commands need a leading "/"
// ([DEC] Console.Submit); without it the text would be said in chat by the server. "say <text>"
// is the explicit way to chat.
function normalizeInput(text) {
  const line = String(text == null ? '' : text).replace(/[\r\n]+/g, ' ').trim();
  if (!line) return null;
  if (line.length > MAX_COMMAND) throw new Error(`A console line is limited to ${MAX_COMMAND} characters.`);
  const say = /^say\s+(.+)$/i.exec(line);
  if (say) return { line: say[1], chat: true };
  if (line === '/') return null;
  return { line: line.startsWith('/') ? line : '/' + line, chat: false };
}

class AdminConsole extends EventEmitter {
  // opts: port (required), host (loopback only), retryMs, commandTimeoutMs, staleMs, connectFn (tests)
  constructor(opts = {}) {
    super();
    if (!Number.isInteger(opts.port) || opts.port < 1 || opts.port > 65535) throw new RangeError('port must be 1..65535');
    const host = opts.host || LOOPBACK;
    if (host !== LOOPBACK && host !== 'localhost' && host !== '::1') throw new Error('The admin console is only ever reached on this PC (127.0.0.1).');
    this.host = host === 'localhost' ? LOOPBACK : host;
    this.port = opts.port;
    this.retryMs = opts.retryMs || 250;
    this.commandTimeoutMs = opts.commandTimeoutMs || 10000;
    this.staleMs = opts.staleMs || 5000;
    this.connectFn = opts.connectFn || ((o) => net.connect(o));
    this.state = 'idle'; // idle | connecting | connected | closed
    this.socket = null;
    this.decoder = null;
    this.wanted = false; // true between attach() and detach()/close(): keep (re)connecting
    this.retryTimer = null;
    this.staleTimer = null;
    this.nextId = 1;
    this.queue = [];
    this.inflight = null;
    this.lastPacketAt = null;
    this.connectedAt = null;
    this.everConnected = false;
    this.attempts = 0;
    this.degraded = null; // protocol error text: socket kept open (closing would stop the server)
    this.serverSaidBye = false;
    this.stale = false;
    this.stats = { messages: 0, keepAlives: 0, responses: 0, sent: 0, bytesIn: 0, bytesOut: 0 };
  }

  status() {
    return {
      state: this.state,
      port: this.port,
      connectedAt: this.connectedAt,
      everConnected: this.everConnected,
      attempts: this.attempts,
      lastPacketAt: this.lastPacketAt,
      stale: this.stale,
      degraded: this.degraded,
      serverSaidBye: this.serverSaidBye,
      queued: this.queue.length + (this.inflight ? 1 : 0),
      stats: { ...this.stats }
    };
  }

  isConnected() {
    return this.state === 'connected' && !!this.socket;
  }

  setState(state, extra) {
    if (this.state === state && !extra) return;
    this.state = state;
    this.emit('state', { ...this.status(), ...(extra || {}) });
  }

  // Start trying to connect, and keep trying (every retryMs) until connected or detached. Call it
  // right after the server process is spawned: with -cport the game gives us 10 s from the moment
  // its console starts listening.
  attach() {
    if (this.wanted) return;
    this.wanted = true;
    this.serverSaidBye = false;
    this.tryConnect();
  }

  tryConnect() {
    clearTimeout(this.retryTimer);
    this.retryTimer = null;
    if (!this.wanted || this.socket) return;
    this.attempts++;
    this.setState('connecting');
    const sock = this.connectFn({ host: this.host, port: this.port });
    this.socket = sock;
    this.decoder = new PacketDecoder();
    this.degraded = null;
    sock.setNoDelay && sock.setNoDelay(true);
    sock.setKeepAlive && sock.setKeepAlive(true, 5000);
    let opened = false;
    sock.once('connect', () => {
      opened = true;
      this.everConnected = true;
      this.connectedAt = new Date().toISOString();
      this.lastPacketAt = Date.now();
      this.stale = false;
      this.setState('connected');
      this.emit('connected', this.status());
      this.armStale();
      this.pump();
    });
    sock.on('data', (chunk) => this.onData(chunk));
    sock.on('error', (err) => {
      this.lastError = err;
    });
    sock.once('close', () => {
      if (this.socket !== sock) return;
      this.socket = null;
      clearTimeout(this.staleTimer);
      this.staleTimer = null;
      const err = this.lastError;
      this.lastError = null;
      if (!opened) {
        // Not listening yet (ECONNREFUSED while the game loads): try again shortly.
        if (this.wanted) this.retryTimer = setTimeout(() => this.tryConnect(), this.retryMs);
        else this.setState('closed');
        return;
      }
      this.failAll(new Error('The admin console connection closed.'));
      const deliberate = !this.wanted;
      this.setState(this.wanted ? 'connecting' : 'closed');
      this.emit('disconnected', { deliberate, serverSaidBye: this.serverSaidBye, error: err ? err.message : null });
      // Unexpected drop: the game shuts down when its last console client leaves, so the server is
      // probably stopping already. Reconnect anyway; it costs nothing and helps if it survived.
      if (this.wanted && !this.serverSaidBye) this.retryTimer = setTimeout(() => this.tryConnect(), this.retryMs);
      else if (this.wanted) {
        this.wanted = false;
        this.setState('closed');
      }
    });
  }

  armStale() {
    clearTimeout(this.staleTimer);
    this.staleTimer = setTimeout(() => {
      if (!this.isConnected()) return;
      // No keep-alive for staleMs: the game's main loop is stalled (a long save, a hang) or the
      // link is gone. We do NOT close: that would stop a server that might only be busy.
      this.stale = true;
      this.emit('state', this.status());
      this.armStale();
    }, this.staleMs);
  }

  onData(chunk) {
    this.stats.bytesIn += chunk.length;
    let msgs;
    try {
      msgs = this.decoder.push(chunk);
    } catch (e) {
      if (!this.degraded) {
        this.degraded = e.message;
        this.emit('protocol-error', e);
        this.emit('state', this.status());
      }
      return;
    }
    if (!msgs.length) return;
    this.lastPacketAt = Date.now();
    if (this.stale) {
      this.stale = false;
      this.emit('state', this.status());
    }
    this.armStale();
    for (const m of msgs) this.onMessage(m);
  }

  onMessage(m) {
    if (m.type === TYPE.MESSAGE) {
      if (!m.body.length) {
        this.stats.keepAlives++;
        return;
      }
      this.stats.messages++;
      const parsed = parseLogBody(fromAscii(m.body));
      const line = { ...parsed, at: Date.now(), id: m.id };
      if (this.inflight && this.inflight.sent) this.inflight.lines.push(line);
      this.emit('line', line);
    } else if (m.type === TYPE.RESPONSE) {
      this.stats.responses++;
      const job = this.inflight;
      if (job && job.id === m.id) {
        this.inflight = null;
        clearTimeout(job.timer);
        const response = fromAscii(m.body);
        job.resolve({ id: job.id, command: job.line, response, lines: job.lines, ms: Date.now() - job.sentAt });
        this.pump();
      } else {
        this.emit('stray-response', { id: m.id, body: fromAscii(m.body) });
      }
    } else if (m.type === TYPE.DISCONNECT) {
      // [DEC] OnProgramExit: sent only when the game is exiting and no restart is wanted.
      this.serverSaidBye = true;
      this.emit('server-disconnect', { at: new Date().toISOString() });
    }
  }

  // Sends one line of console input. Resolves with { response, lines, ms } once the game has run
  // it (its Response packet arrived). Lines are the log Messages that arrived in between: the
  // command's output, possibly with an unrelated log line the game wrote in the same frame.
  // Commands are sent one at a time so output is never mixed between two commands.
  send(text, { timeoutMs } = {}) {
    const line = String(text == null ? '' : text).replace(/[\r\n]+/g, ' ');
    if (line.length > MAX_COMMAND) return Promise.reject(new Error(`A console line is limited to ${MAX_COMMAND} characters.`));
    if (!this.wanted) return Promise.reject(new Error('The admin console is not attached.'));
    return new Promise((resolve, reject) => {
      this.queue.push({ line, resolve, reject, timeoutMs: timeoutMs || this.commandTimeoutMs, lines: [] });
      this.pump();
    });
  }

  // Round trip with an empty Message: the game runs Console.Submit("") (a no-op) and answers.
  ping() {
    return this.send('', { timeoutMs: 5000 }).then((r) => r.ms);
  }

  pump() {
    if (this.inflight || !this.queue.length || !this.isConnected()) return;
    const job = this.queue.shift();
    job.id = this.nextId;
    this.nextId = this.nextId >= 0x7fffffff ? 1 : this.nextId + 1;
    const frame = job.line === '' ? encodeMessage(job.id, TYPE.MESSAGE, null) : encodeMessage(job.id, TYPE.MESSAGE, job.line);
    this.inflight = job;
    job.sent = true;
    job.sentAt = Date.now();
    job.timer = setTimeout(() => {
      if (this.inflight !== job) return;
      this.inflight = null;
      job.reject(Object.assign(new Error('The server did not answer the command in time. It may be busy (saving, loading) or frozen.'), { timeout: true, lines: job.lines }));
      this.pump();
    }, job.timeoutMs);
    this.stats.sent++;
    this.stats.bytesOut += frame.length;
    this.socket.write(frame);
  }

  failAll(err) {
    const jobs = this.inflight ? [this.inflight, ...this.queue] : [...this.queue];
    this.inflight = null;
    this.queue = [];
    for (const j of jobs) {
      clearTimeout(j.timer);
      j.reject(err);
    }
  }

  // Stop trying to connect, WITHOUT closing an open connection (closing would stop the server).
  // Used when the process has already exited.
  detach() {
    this.wanted = false;
    clearTimeout(this.retryTimer);
    this.retryTimer = null;
    if (!this.socket) this.setState('closed');
  }

  // Deliberately close the connection. If Steward is the game's only console client, the game
  // then saves and shuts down ([DEC] SocketAdminConsole.OnClientDisconnected). Callers must mark
  // the stop as requested first.
  close() {
    this.wanted = false;
    clearTimeout(this.retryTimer);
    clearTimeout(this.staleTimer);
    this.retryTimer = null;
    this.staleTimer = null;
    this.failAll(new Error('The admin console was closed.'));
    if (this.socket) this.socket.destroy();
    else this.setState('closed');
  }
}

module.exports = {
  TYPE,
  TYPE_NAMES,
  EMPTY_SIZE,
  MAX_PACKET,
  MAX_BODY,
  MAX_COMMAND,
  CPORT_BASE,
  ProtocolError,
  PacketDecoder,
  AdminConsole,
  cportForSlot,
  cportForId,
  toAscii,
  fromAscii,
  encodePacket,
  encodeMessage,
  parseLogBody,
  quoteArg,
  normalizeInput
};
