'use strict';

// The game's admin console socket, CodeHatch.Engine.Administration.SocketAdminConsole +
// CodeHatch.Engine.Sockets.SocketServer, emulated from the decompiled code [DEC]:
//
//  - Binds 0.0.0.0 on -cport (11000 without it; an unparsable value becomes 0, int.TryParse).
//    A bind failure is caught and logged; the game runs on without a console.
//  - Every second it broadcasts an empty Message (keep-alive, PingClients).
//  - Each received packet group whose type is not Response is ASCII-decoded and passed to
//    Console.Submit on the next frame; afterwards a Response with the request id goes back to the
//    sender, carrying only ConsoleAddMessageEvent text (in practice empty). Command output reaches
//    every client as "[I] ..." Messages BEFORE that Response. A Response-type group is submitted
//    without an answer. An Authentication packet is just another command (no authentication exists).
//  - A client that closes is only noticed when the next keep-alive write fails, about a second later
//    (the receive thread gets EndOfStreamException, which only gets logged).
//  - When the last client is gone: Server.Shutdown() (a saved stop).
//  - With -cport: if nobody is connected 10 s after the console started, Server.Shutdown().
//  - A bad frame kills that client's receive loop; the socket itself stays open.
//  - The Disconnect packet (type 3) is broadcast only from OnProgramExit when RestartAfterShutdown is
//    false (/shutdown, /logout and friends, and the first run).

const net = require('net');
const { EventEmitter } = require('events');
const P = require('./packets');

class ConsoleServer extends EventEmitter {
  // opts: port, host ('0.0.0.0'), keepAliveMs (1000), frameMs (16, the deferred-action delay),
  //       logger (GameLogger), cportRule (bool), cportWindowMs (10000)
  constructor(opts) {
    super();
    this.opts = opts;
    this.clients = new Set();
    this.packetId = 0; // Packet.PacketId, shared by all broadcasts
    this.server = null;
    this.enabled = false;
    this.sendLogs = true; // SocketAdminConsole.SendLogs
    this.unsubscribe = null;
    this.clientSeq = 0;
  }

  start() {
    const log = this.opts.logger;
    return new Promise((resolve) => {
      const srv = net.createServer((sock) => this.onClient(sock));
      srv.once('error', (e) => {
        // SocketServer.Start catches and reports through ExceptionOccurred -> Logger.Exception
        log.exception('SocketException', e.code === 'EADDRINUSE' ? 'Address already in use' : e.message, ['CodeHatch.Engine.Sockets.SocketServer.Start()']);
        this.bindError = e;
        this.afterEnable();
        resolve(false);
      });
      srv.listen(this.opts.port, this.opts.host || '0.0.0.0', () => {
        this.server = srv;
        this.port = srv.address().port;
        this.afterEnable();
        resolve(true);
      });
    });
  }

  // The rest of OnEnable runs whether or not the bind worked.
  afterEnable() {
    const log = this.opts.logger;
    this.enabled = true;
    this.unsubscribe = log.onLog((level, line) => {
      if (this.sendLogs) this.send(line);
    });
    log.info('Admin console enabled.', [], ['CodeHatch.Engine.Administration.SocketAdminConsole.OnEnable()']);
    this.keepAlive = setInterval(() => this.pingClients(), this.opts.keepAliveMs || 1000);
    this.cportTimer = setTimeout(() => {
      if (this.opts.cportRule && this.clients.size === 0) {
        this.enabled = false;
        this.emit('shutdown-request', 'cport-window');
      }
    }, this.opts.cportWindowMs == null ? 10000 : this.opts.cportWindowMs);
  }

  pingClients() {
    if (!this.enabled) return;
    const frame = P.getPackets(this.packetId++, P.TYPE.MESSAGE, null);
    for (const c of [...this.clients]) {
      if (c.gone) {
        this.dropClient(c);
        continue;
      }
      c.sock.write(frame);
    }
  }

  // SocketAdminConsole.Send: one Message (split into 4082-byte bodies) to every client.
  send(text) {
    const frame = P.getPackets(this.packetId++, P.TYPE.MESSAGE, text);
    for (const c of this.clients) if (!c.gone) c.sock.write(frame);
  }

  onClient(sock) {
    const c = { sock, reader: new P.PacketReader(), gone: false, id: ++this.clientSeq };
    this.clients.add(c);
    sock.setNoDelay(true);
    this.emit('client', c);
    this.defer(() => this.opts.logger.info('Admin console connected.', [], ['CodeHatch.Engine.Administration.SocketAdminConsole.Update()']));
    sock.on('data', (chunk) => {
      const groups = c.reader.push(chunk);
      for (const g of groups) this.onGroup(c, g);
      if (c.reader.dead && !c.reportedDead) {
        c.reportedDead = true;
        this.defer(() => this.opts.logger.exception(c.reader.dead.exceptionType, c.reader.dead.message, ['CodeHatch.Engine.Sockets.Packet.Deserialize()', 'CodeHatch.Engine.Sockets.SocketClient.ReceiveLoop()']));
      }
    });
    sock.on('error', () => {});
    const gone = () => {
      if (c.gone) return;
      c.gone = true;
      // EndOfStreamException on the receive thread: logged, not treated as a disconnect.
      if (!c.reader.dead && this.enabled) {
        this.defer(() => this.opts.logger.exception('EndOfStreamException', 'Failed to read past end of stream.', ['System.IO.BinaryReader.ReadInt32()', 'CodeHatch.Engine.Sockets.SocketClient.ReceiveLoop()']));
      }
      if (!this.enabled) this.dropClient(c);
    };
    sock.on('end', gone);
    sock.on('close', gone);
  }

  dropClient(c) {
    if (!this.clients.delete(c)) return;
    c.sock.destroy();
    this.emit('client-gone', c);
    if (!this.enabled) return;
    this.defer(() => this.opts.logger.info('Admin console disconnected.', [], ['CodeHatch.Engine.Administration.SocketAdminConsole.Update()']));
    if (this.clients.size === 0) this.defer(() => this.emit('shutdown-request', 'last-client'));
  }

  onGroup(c, g) {
    const text = P.fromAscii(g.body);
    this.emit('received', { client: c.id, id: g.id, type: g.type, text, packets: g.packets });
    if (g.type === P.TYPE.RESPONSE) {
      this.defer(() => this.emit('submit', text, null));
      return;
    }
    this.defer(() => {
      const collected = [];
      this.emit('submit', text, collected); // the handler runs the command synchronously
      if (!c.gone) c.sock.write(P.getPackets(g.id, P.TYPE.RESPONSE, collected.join('\n')));
    });
  }

  // DifferAction: runs on the next Update(), in order.
  defer(fn) {
    setTimeout(fn, this.opts.frameMs == null ? 16 : this.opts.frameMs);
  }

  // OnProgramExit: Disconnect packet only when no restart is wanted, then close.
  programExit(restartAfterShutdown) {
    if (!restartAfterShutdown) {
      const frame = P.encodePacket(this.packetId++, P.TYPE.DISCONNECT, null);
      for (const c of this.clients) if (!c.gone) c.sock.write(frame);
    }
    this.close();
  }

  close() {
    this.enabled = false;
    clearInterval(this.keepAlive);
    clearTimeout(this.cportTimer);
    if (this.unsubscribe) this.unsubscribe();
    this.unsubscribe = null;
    for (const c of this.clients) c.sock.end();
    this.clients.clear();
    return new Promise((resolve) => {
      if (!this.server) return resolve();
      const s = this.server;
      this.server = null;
      s.close(() => resolve());
      setTimeout(resolve, 500).unref();
    });
  }
}

module.exports = { ConsoleServer };
