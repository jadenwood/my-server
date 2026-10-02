'use strict';

// Runs the dedicated server from the test copy with piped stdio (no console window), streams its
// output, tails the server's log files in <server>\Logs and stops it with "quit".

const { EventEmitter } = require('events');
const { spawn, execFile } = require('child_process');
const { StringDecoder } = require('string_decoder');
const fsp = require('fs/promises');
const path = require('path');
const S = require('./safety');
const N = require('./netcheck');
const G = require('./shared/gamelog');

const MAX_LINES = 3000;
const STOP_GRACE_MS = 60 * 1000;
const TAIL_AFTER_MS = 6000;
const TAIL_EVERY_MS = 1000;

class ServerManager extends EventEmitter {
  constructor({ platform = process.platform, tailAfterMs = TAIL_AFTER_MS, tailEveryMs = TAIL_EVERY_MS } = {}) {
    super();
    this.platform = platform;
    this.tailAfterMs = tailAfterMs;
    this.tailEveryMs = tailEveryMs;
    this.child = null;
    this.state = 'stopped';
    this.root = null;
    this.exe = null;
    this.startedAt = null;
    this.ready = false;
    this.players = new Set();
    this.lines = [];
    this.seq = 0;
    this.stdoutSeen = false;
    this.stopTimer = null;
    this.stopOverdue = false;
    this.tail = null;
    this.lastExit = null;
    // true once stop()/forceStop() was called for this run: the exit was asked for, not a crash.
    this.requested = false;
    this.readyAt = null;
    this.steam = null;
    this.listening = null;
  }

  status() {
    return {
      state: this.state,
      pid: this.child ? this.child.pid : null,
      exe: this.exe,
      root: this.root,
      startedAt: this.startedAt,
      ready: this.ready,
      players: [...this.players].slice(0, 200),
      stopOverdue: this.stopOverdue,
      lastExit: this.lastExit,
      readyAt: this.readyAt,
      steam: this.steam,
      listening: this.listening
    };
  }

  isRunning() {
    return !!this.child;
  }

  log(src, text) {
    const line = { id: ++this.seq, t: Date.now(), src, text: String(text).slice(0, 2000) };
    this.lines.push(line);
    if (this.lines.length > MAX_LINES) this.lines.splice(0, this.lines.length - MAX_LINES);
    this.emit('line', line);
    // Output read after the process exited (a final log drain, a late stdout flush) is shown but
    // must not mark a stopped server ready or add players.
    if (src !== 'sys' && this.child) this.inspect(line.text);
    return line;
  }

  inspect(text) {
    // [DEC] CoreServer logs "Server for N players started on port P." after Network.InitializeServer:
    // the world is loaded and players can join. ("Initialize engine version:" is only Unity's banner.)
    const listening = S.READY_LINE.test(text) ? N.parseListeningLine(text) : null;
    if (listening) {
      this.listening = listening;
      if (!this.ready) {
        this.ready = true;
        this.readyAt = new Date().toISOString();
      }
      this.emitStatus();
    }
    const steam = N.parseSteamLine(text);
    if (steam) {
      this.steam = steam;
      this.emitStatus();
    }
    const j = S.JOIN_LINE.exec(text);
    if (j) {
      this.players.add(j[1].slice(0, 64));
      this.emitStatus();
    }
    const l = S.LEAVE_LINE.exec(text);
    if (l && this.players.delete(l[1].slice(0, 64))) this.emitStatus();
  }

  emitStatus() {
    this.emit('status', this.status());
  }

  getLines(sinceId = 0) {
    return this.lines.filter((l) => l.id > sinceId);
  }

  // Start-LocalServer.ps1: Server.exe with no arguments (default) or ROK.exe -batchmode -nographics -silentcrash,
  // working directory = server root so Oxide creates <server>\oxide.
  async start(root, exeName = 'Server') {
    if (this.child) throw Object.assign(new Error('The server is already running.'), { friendly: true });
    const name = exeName === 'ROK' ? 'ROK' : 'Server';
    const exePath = path.join(root, `${name}.exe`);
    try {
      await fsp.access(exePath);
    } catch {
      throw Object.assign(new Error(`${name}.exe was not found in ${root}. Try the other server program in Settings.`), { friendly: true });
    }
    const args = name === 'ROK' ? ['-batchmode', '-nographics', '-silentcrash'] : [];
    if (name === 'ROK') {
      // Unity in -batchmode logs to a file, not stdout: point it into Logs\ so the console tail shows it.
      const logDir = G.logsDir(root);
      await fsp.mkdir(logDir, { recursive: true });
      const logFile = path.join(logDir, G.UNITY_LOG);
      // Unity overwrites -logFile on start. Keep the last run's copy aside so the tail reads the
      // new one from its first byte instead of from the old file's length.
      await fsp.rename(logFile, path.join(logDir, 'realm-server.prev.log')).catch(() => {});
      args.push('-logFile', logFile);
    }
    this.root = root;
    this.exe = name;
    this.ready = false;
    this.players.clear();
    this.stdoutSeen = false;
    this.stopOverdue = false;
    this.lastExit = null;
    this.requested = false;
    this.readyAt = null;
    this.steam = null;
    this.listening = null;
    this.startedAt = new Date().toISOString();
    this.state = 'starting';
    this.log('sys', `Starting ${name}.exe ${args.join(' ')} in ${root}`);

    const child = spawn(exePath, args, { cwd: root, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, env: process.env });
    this.child = child;
    this.hookStream(child.stdout, 'out');
    this.hookStream(child.stderr, 'err');
    child.stdin.on('error', () => {});
    child.once('spawn', () => {
      this.state = 'running';
      this.log('sys', `${name}.exe started (pid ${child.pid}).`);
      this.emitStatus();
    });
    child.once('error', (err) => {
      this.log('sys', `Could not run ${name}.exe: ${err.message}`);
      this.onExit(null, null, err);
      if (err.code !== 'EACCES') return;
      // On Windows, EACCES is how Node reports "this program requires administrator rights" or
      // "access blocked" (antivirus / Controlled Folder Access). Server.exe is only a wrapper
      // around ROK.exe, the actual game server, so try that instead.
      if (name === 'Server') {
        this.log('sys', 'Windows would not start Server.exe (it probably asks for administrator rights). Switching to ROK.exe, the game server itself.');
        this.emit('exe-fallback', 'ROK');
        this.start(root, 'ROK').catch((e) => this.log('sys', e.message));
      } else {
        this.log('sys', 'Windows refused to run ROK.exe. Either it needs administrator rights (close Realm, right-click it, choose "Run as administrator") ' +
          'or Windows Security blocked it (Windows Security > Virus & threat protection > Protection history / Controlled folder access: allow ROK.exe).');
      }
    });
    child.once('exit', (code, signal) => this.onExit(code, signal));
    this.startTail(root);
    this.emitStatus();
    return this.status();
  }

  hookStream(stream, src) {
    const dec = new StringDecoder('utf8');
    let buf = '';
    stream.on('data', (chunk) => {
      if (src === 'out' && chunk.length) this.stdoutSeen = true;
      buf += dec.write(chunk);
      const parts = buf.split(/\r?\n|\r/);
      buf = parts.pop();
      for (const p of parts) if (p.trim()) this.log(src, p);
      if (buf.length > 4000) {
        this.log(src, buf);
        buf = '';
      }
    });
    stream.on('end', () => {
      buf += dec.end();
      if (buf.trim()) this.log(src, buf);
    });
  }

  onExit(code, signal, err) {
    if (!this.child) return;
    this.child = null;
    clearTimeout(this.stopTimer);
    this.stopTimer = null;
    this.stopTail({ drain: true });
    this.state = 'stopped';
    this.ready = false;
    this.players.clear();
    this.stopOverdue = false;
    const uptimeMs = this.startedAt ? Date.now() - Date.parse(this.startedAt) : null;
    const loadMs = this.readyAt && this.startedAt ? Date.parse(this.readyAt) - Date.parse(this.startedAt) : null;
    this.lastExit = { code, signal: signal || null, error: err ? err.message : null, at: new Date().toISOString(), requested: this.requested, uptimeMs, loadMs };
    if (!err) this.log('sys', `Server stopped${code != null ? ` (exit code ${code})` : signal ? ` (${signal})` : ''}.`);
    this.emitStatus();
    this.emit('exit', this.lastExit);
  }

  // One line typed into the server console (e.g. oxide.version). Never more than one line.
  sendCommand(text) {
    if (!this.child) throw Object.assign(new Error('The server is not running.'), { friendly: true });
    const line = String(text).replace(/[\r\n]+/g, ' ').trim();
    if (!line) return false;
    this.log('sys', `> ${line}`);
    this.child.stdin.write(line + '\n');
    return true;
  }

  // Graceful stop: "quit" on stdin, then wait. After the grace period the UI offers Force stop.
  stop() {
    if (!this.child) return this.status();
    this.requested = true;
    if (this.state !== 'stopping') {
      this.state = 'stopping';
      this.log('sys', 'Sending "quit" to the server and waiting for it to save and close...');
      try {
        this.child.stdin.write('quit\n');
      } catch {
        /* stdin closed: the force option covers it */
      }
      this.stopTimer = setTimeout(() => {
        if (this.child) {
          this.stopOverdue = true;
          this.log('sys', 'The server has not closed yet. You can wait longer or use Force stop.');
          this.emitStatus();
        }
      }, STOP_GRACE_MS);
    }
    this.emitStatus();
    return this.status();
  }

  // Force stop: the whole process tree (Server.exe may start ROK.exe).
  forceStop() {
    const child = this.child;
    if (!child) return Promise.resolve(this.status());
    this.requested = true;
    this.log('sys', 'Force stopping the server.');
    return new Promise((resolve) => {
      const done = () => resolve(this.status());
      if (this.platform === 'win32' && child.pid) {
        execFile('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true }, () => {
          if (this.child === child) child.kill();
          setTimeout(done, 300);
        });
      } else {
        child.kill('SIGKILL');
        setTimeout(done, 300);
      }
    });
  }

  waitForExit(ms) {
    if (!this.child) return Promise.resolve(true);
    return new Promise((resolve) => {
      const t = setTimeout(() => {
        this.off('exit', on);
        resolve(false);
      }, ms);
      const on = () => {
        clearTimeout(t);
        resolve(true);
      };
      this.once('exit', on);
    });
  }

  // Follows both server logs in <server>\Logs from where they were when the server started:
  //  - the game's own Log[yyMMdd-hhmmss].txt, always: the game writes its messages (including the
  //    ready line) there and not to Unity's -logFile;
  //  - Unity's realm-server.log, only while stdout stays empty (with -batchmode Unity logs to that
  //    file instead of stdout; otherwise it would repeat what stdout already showed).
  // Each file keeps its own offset, so switching between them never skips or repeats a line.
  startTail(root) {
    this.stopTail();
    const startMs = Date.now();
    const offsets = new Map(G.serverLogCandidates(root).map((e) => [e.path, e.size]));
    const files = new Map();
    let chain = Promise.resolve();
    let busy = false;

    const follow = async (entry) => {
      let f = files.get(entry.path);
      if (!f) {
        f = { offset: offsets.has(entry.path) ? offsets.get(entry.path) : 0, dec: new StringDecoder('utf8'), pending: '' };
        files.set(entry.path, f);
        this.log('sys', `Following ${path.relative(root, entry.path)}`);
      }
      for (;;) {
        const r = await G.readFrom(entry.path, f.offset);
        if (r.reset) {
          f.dec = new StringDecoder('utf8');
          f.pending = '';
        }
        f.offset = r.next;
        if (!r.buf.length) break;
        const parts = (f.pending + f.dec.write(r.buf)).split(/\r?\n|\r/);
        f.pending = parts.pop();
        for (const p of parts) if (p.trim()) this.log('log', p);
        if (r.next >= r.size) break;
      }
    };

    const tick = async (final = false) => {
      const logs = await G.resolveLogs(root, startMs - 2000);
      const list = [...logs.game];
      const unityDue = !this.stdoutSeen && (final || Date.now() - startMs >= this.tailAfterMs);
      if (logs.unity && unityDue && (files.has(logs.unity.path) || logs.unity.mtimeMs >= startMs - 2000)) list.unshift(logs.unity);
      for (const e of list) {
        try {
          await follow(e);
        } catch {
          /* file locked or removed; try again next tick */
        }
      }
      if (final) {
        for (const f of files.values()) {
          const rest = f.pending + f.dec.end();
          if (rest.trim()) this.log('log', rest);
        }
      }
    };

    // Ticks never overlap: each read waits for the previous one, the final drain included.
    const run = (final) => (chain = chain.then(() => tick(final)).catch(() => {}));
    const timer = setInterval(() => {
      if (busy) return;
      busy = true;
      run(false).finally(() => { busy = false; });
    }, this.tailEveryMs);
    this.tail = { timer, drain: () => run(true) };
  }

  // drain: read what the server wrote just before it exited, so the last lines are not lost.
  stopTail({ drain = false } = {}) {
    const t = this.tail;
    this.tail = null;
    if (!t) return Promise.resolve();
    clearInterval(t.timer);
    return drain ? t.drain() : Promise.resolve();
  }
}

// Get-RealmServerProcess: Server.exe / ROK.exe processes whose image lives in this folder,
// including ones the client did not start. Windows only; elsewhere returns [].
function findServerProcesses(root, { platform = process.platform } = {}) {
  if (platform !== 'win32') return Promise.resolve([]);
  const script = "Get-Process -Name 'Server','ROK' -ErrorAction SilentlyContinue | ForEach-Object { try { '' + $_.Id + '|' + $_.ProcessName + '|' + $_.Path } catch { } }";
  return new Promise((resolve) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script], { windowsHide: true, timeout: 15000 }, (err, stdout) => {
      if (err) return resolve([]);
      const out = [];
      for (const line of String(stdout).split(/\r?\n/)) {
        const [pid, name, p] = line.split('|');
        if (pid && p && S.isInside(p.trim(), root, 'win32')) out.push({ pid: Number(pid), name, path: p.trim() });
      }
      resolve(out);
    });
  });
}

module.exports = { ServerManager, findServerProcesses };
