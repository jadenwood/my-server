'use strict';

// Shared test helpers: temp server folders, free ports, a fast RokSim, and a tiny console client
// built on lib/packets.js.

const fs = require('fs');
const os = require('os');
const path = require('path');
const net = require('net');
const dgram = require('dgram');
const { EventEmitter } = require('events');
const { RokSim } = require('../lib/sim');
const { loadServerSettings } = require('../lib/props');
const P = require('../lib/packets');

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function until(fn, ms = 4000, what = 'condition') {
  const end = Date.now() + ms;
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await wait(10);
  }
}

const made = [];
process.on('exit', () => {
  if (process.env.ROK_SIM_KEEP_TMP) return;
  for (const d of made) {
    try {
      fs.rmSync(d, { recursive: true, force: true });
    } catch {
      /* best effort */
    }
  }
});

// A fresh temp folder, removed when the test file finishes (set ROK_SIM_KEEP_TMP=1 to keep them).
function tmpDir(tag = 'rok-sim') {
  const d = fs.mkdtempSync(path.join(os.tmpdir(), `${tag}-`));
  made.push(d);
  return d;
}

// A port that is free for TCP and UDP on 0.0.0.0 right now. Chosen below the usual ephemeral ranges
// (Linux 32768+, Windows/macOS 49152+), so that an outgoing connection of another test cannot be
// handed the same number between this check and the simulator's bind.
const handedOut = new Set();
async function freePort() {
  for (let i = 0; i < 200; i++) {
    const port = 20000 + Math.floor(Math.random() * 12000);
    if (handedOut.has(port)) continue;
    const srv = net.createServer();
    const listening = await new Promise((r) => {
      srv.once('error', () => r(false));
      srv.listen(port, '0.0.0.0', () => r(true));
    });
    if (!listening) continue;
    const u = dgram.createSocket('udp4');
    const ok = await new Promise((r) => {
      u.once('error', () => r(false));
      u.bind(port, '0.0.0.0', () => r(true));
    });
    await new Promise((r) => srv.close(r));
    if (ok) {
      await new Promise((r) => u.close(r));
      handedOut.add(port);
      return port;
    }
    try {
      u.close();
    } catch {
      /* not bound */
    }
  }
  throw new Error('no free port');
}

// A server folder that has already had its first run, with test ports and overrides.
async function preparedFolder(overrides = {}) {
  const dir = tmpDir();
  const cfg = path.join(dir, 'Configuration', 'ServerSettings.cfg');
  loadServerSettings(cfg); // creates the file exactly as the first run would
  const game = await freePort();
  const steam = await freePort();
  const values = { portNumber: game, pingPort: game, steamAuthPort: steam, timeBetweenPlayerJoin: 0, ...overrides };
  let text = fs.readFileSync(cfg, 'utf8');
  for (const [k, v] of Object.entries(values)) {
    const re = new RegExp(`^${k} = '[^']*'`, 'm');
    if (re.test(text)) text = text.replace(re, `${k} = '${v}'`);
    else text += `${k} = '${v}'\r\n`;
  }
  fs.writeFileSync(cfg, text);
  return { dir, cfg, game, steam };
}

// cportWindowMs: how long the console waits for its first client before it closes (the game's -cport rule).
// 600 ms was too tight on a busy CI runner (the console closed before connectConsole got there); tests of the
// window itself pass their own short value.
const FAST = { bootMs: 10, loadMs: 40, keepAliveMs: 100, cportWindowMs: 5000, frameMs: 5, exitDelayMs: 10, stateRefreshMs: 60000 };

function makeSim(dir, argv = [], sim = {}) {
  return new RokSim({ cwd: dir, argv: ['-batchmode', '-nographics', '-silentcrash', ...argv], sim: { ...FAST, ...sim }, stdout: devNull(), stderr: devNull() });
}

function devNull() {
  return { write() {} };
}

function onceEvent(em, name, ms = 4000) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`timed out waiting for "${name}"`)), ms);
    em.once(name, (...a) => {
      clearTimeout(t);
      resolve(a.length > 1 ? a : a[0]);
    });
  });
}

// Starts a sim and resolves on 'ready' (or rejects when it exits first).
async function startReady(dir, argv = [], sim = {}) {
  const s = makeSim(dir, argv, sim);
  const ready = new Promise((resolve, reject) => {
    s.once('ready', () => resolve(s));
    s.once('exit', (code) => reject(new Error(`sim exited with ${code} before ready; game log tail:\n${readGameLog(dir).split('\n').slice(-12).join('\n')}`)));
  });
  s.start();
  return ready;
}

async function stopSim(s) {
  if (!s || s.state === 'exited') return;
  const done = onceEvent(s, 'exit', 4000).catch(() => {});
  s.kill();
  await done;
}

// Minimal admin console client: records every packet group, sends lines, waits for responses.
class ConsoleClient extends EventEmitter {
  constructor(port) {
    super();
    this.port = port;
    this.groups = [];
    this.reader = new P.PacketReader();
    this.nextId = 100;
    this.closed = false;
  }

  connect() {
    return new Promise((resolve, reject) => {
      this.sock = net.connect({ host: '127.0.0.1', port: this.port });
      this.sock.once('connect', () => resolve(this));
      this.sock.once('error', reject);
      this.sock.on('data', (c) => {
        for (const g of this.reader.push(c)) {
          const rec = { ...g, text: P.fromAscii(g.body), at: Date.now() };
          this.groups.push(rec);
          this.emit('group', rec);
        }
      });
      this.sock.on('close', () => {
        this.closed = true;
        this.emit('close');
      });
    });
  }

  messages() {
    return this.groups.filter((g) => g.type === P.TYPE.MESSAGE && g.text).map((g) => g.text);
  }

  sendRaw(buf) {
    this.sock.write(buf);
  }

  // Sends one line; resolves { response, lines, index } once the Response with that id arrives.
  command(text, type = P.TYPE.MESSAGE, ms = 3000) {
    const id = this.nextId++;
    const start = this.groups.length;
    this.sock.write(P.getPackets(id, type, text));
    return until(() => {
      const i = this.groups.findIndex((g, j) => j >= start && g.type === P.TYPE.RESPONSE && g.id === id);
      if (i < 0) return null;
      return { response: this.groups[i].text, lines: this.groups.slice(start, i).filter((g) => g.type === P.TYPE.MESSAGE && g.text).map((g) => g.text), index: i };
    }, ms, `response to ${JSON.stringify(text)}`);
  }

  close() {
    if (this.sock) this.sock.destroy();
  }
}

// Retries until the console listens. By default also waits for the game's own
// "Admin console connected." line so that it cannot land inside a later command's output.
async function connectConsole(port, ms = 3000, { hello = true } = {}) {
  const c = await until(async () => {
    const x = new ConsoleClient(port);
    try {
      await x.connect();
      return x;
    } catch {
      return null;
    }
  }, ms, `console on ${port}`);
  if (hello) await until(() => c.messages().includes('[I] Admin console connected.'), ms, 'Admin console connected.');
  return c;
}

function readGameLog(dir) {
  const logs = path.join(dir, 'Logs');
  const files = fs.existsSync(logs) ? fs.readdirSync(logs).filter((f) => /^Log\[\d{6}-\d{6}\]\.txt$/.test(f)).sort() : [];
  return files.map((f) => fs.readFileSync(path.join(logs, f), 'utf8')).join('');
}

module.exports = { wait, until, tmpDir, freePort, preparedFolder, makeSim, startReady, stopSim, onceEvent, ConsoleClient, connectConsole, readGameLog, FAST, devNull };
