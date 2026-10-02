'use strict';

// Stand-ins for the two other programs that can hold a Realm server's game port (docs/worlds.md and
// docs/HANDOFF.md "Real problems hit"). Tests only; neither is game code.
//
// Watchdog: the game's Server.exe. On the owner's PC (2026-10-02), once Server.exe had been started
// as administrator, a ROK.exe came back about 2 s after it was ended, and a Steward start then hit
// "The port 7350 is already being used". This stand-in relaunches the simulator --sim-relaunch-ms
// (default 2000) after EVERY exit until the watchdog itself is stopped. UNVERIFIED for the real
// Server.exe: whether /shutdown (an exit that wants no restart) also brings it back, what its
// ConsoleSettings.cfg keys change, and whether closing Server.exe ends its ROK.exe.
//
// GameClient: the game itself. ROK.exe is both the game and the server; the owner's game client
// held UDP 7350 while Steward tried to start the server (commit 28c3cc6). It has no admin console
// and writes nothing into the server folder.

const path = require('path');
const dgram = require('dgram');
const { spawn } = require('child_process');
const { EventEmitter } = require('events');

const BIN = path.join(__dirname, '..', 'bin', 'rok-sim.js');

class Watchdog extends EventEmitter {
  // opts: cwd (the server folder), argv (ROK.exe arguments + --sim-* switches), relaunchMs
  constructor({ cwd, argv = [], relaunchMs = 2000 } = {}) {
    super();
    this.cwd = cwd;
    this.argv = argv;
    this.relaunchMs = relaunchMs;
    this.child = null;
    this.launches = 0;
    this.stopped = false;
    this.timer = null;
  }

  start() {
    this.launch();
    return this;
  }

  launch() {
    if (this.stopped) return;
    const child = spawn(process.execPath, [BIN, ...this.argv], { cwd: this.cwd, stdio: 'ignore' });
    this.child = child;
    this.launches++;
    this.emit('launch', { pid: child.pid, n: this.launches });
    child.once('exit', (code, signal) => {
      if (this.child === child) this.child = null;
      this.emit('child-exit', { pid: child.pid, code, signal });
      if (!this.stopped) this.timer = setTimeout(() => this.launch(), this.relaunchMs);
    });
  }

  get childPid() {
    return this.child ? this.child.pid : null;
  }

  // Stops relaunching. killChild: also end the running server (tests clean up with it).
  stop({ killChild = true } = {}) {
    this.stopped = true;
    clearTimeout(this.timer);
    this.timer = null;
    if (killChild && this.child) {
      const c = this.child;
      return new Promise((resolve) => {
        c.once('exit', () => resolve());
        c.kill('SIGKILL');
      });
    }
    return Promise.resolve();
  }
}

class GameClient {
  constructor({ port = 7350, host = '0.0.0.0' } = {}) {
    this.port = port;
    this.host = host;
    this.sock = null;
  }

  start() {
    return new Promise((resolve, reject) => {
      const s = dgram.createSocket({ type: 'udp4', reuseAddr: false });
      s.once('error', reject);
      s.bind(this.port, this.host, () => {
        s.removeListener('error', reject);
        s.on('error', () => {});
        s.on('message', () => {}); // the game's traffic is not emulated
        this.sock = s;
        resolve(this);
      });
    });
  }

  close() {
    const s = this.sock;
    this.sock = null;
    return new Promise((resolve) => (s ? s.close(() => resolve()) : resolve()));
  }
}

module.exports = { Watchdog, GameClient, BIN };
