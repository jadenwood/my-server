'use strict';

// Runs the dedicated server from the test copy with piped stdio (no console window), streams its
// output, follows the game's own log (<server>\Logs\Log[yyMMdd-hhmmss].txt, the newest one created
// after Steward started the server) and stops it with "quit". It can also adopt a server that is
// already running from the folder (started before Steward, or by an earlier Steward): it then follows
// that server's newest log and watches its process id, but never owns or kills the process.

const { EventEmitter } = require('events');
const { spawn, execFile } = require('child_process');

const GAME_APP_ID = 344760; // Reign of Kings (the game players own), not 381690 (the server tool)
const { StringDecoder } = require('string_decoder');
const fs = require('fs');
const fsp = require('fs/promises');
const path = require('path');
const S = require('./safety');
const N = require('./netcheck');
const RD = require('./readiness');

const MAX_LINES = 3000;
const STOP_GRACE_MS = 60 * 1000;
const TAIL_MS = 1000;
// When adopting, read this much of the end of the running server's log to learn its state.
const ADOPT_BACKLOG_BYTES = 512 * 1024;

// Is this process still there? EPERM means it exists but belongs to another user or runs as
// administrator (UNVERIFIED on Windows that libuv reports an elevated process that way).
function processAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return e.code === 'EPERM';
  }
}

class ServerManager extends EventEmitter {
  // tailMs and isAlive exist for tests.
  constructor({ platform = process.platform, tailMs = TAIL_MS, isAlive = processAlive } = {}) {
    super();
    this.platform = platform;
    this.tailMs = tailMs;
    this.isAlive = isAlive;
    this.tracker = new RD.ReadyTracker();
    // Set while a server Steward did not start is attached: { pid }.
    this.adopted = null;
    this.liveTimer = null;
    // Set by lib/court-host.js while the admin console is connected: its lines are then shown in the
    // console instead of the same lines from the log file (they are still read for readiness).
    this.consoleFeed = false;
    this.gameLog = null;
    this.world = null;
    this.worldInUse = null;
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
      pid: this.child ? this.child.pid : this.adopted ? this.adopted.pid : null,
      adopted: !!this.adopted,
      phase: this.tracker.phase,
      world: this.world,
      worldInUse: this.worldInUse,
      gameLog: this.gameLog,
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
    return !!this.child || !!this.adopted;
  }

  // A line read from a log file: always checked for readiness, shown only when asked.
  ingest(src, text, show) {
    if (show) this.log(src, text);
    else this.inspect(String(text).slice(0, 2000));
  }

  log(src, text) {
    const line = { id: ++this.seq, t: Date.now(), src, text: String(text).slice(0, 2000) };
    this.lines.push(line);
    if (this.lines.length > MAX_LINES) this.lines.splice(0, this.lines.length - MAX_LINES);
    this.emit('line', line);
    if (src !== 'sys') this.inspect(line.text);
    return line;
  }

  inspect(text) {
    const ev = this.tracker.feed(text);
    if (ev === 'ready' && !this.ready) {
      this.ready = true;
      this.readyAt = new Date().toISOString();
      this.log('sys', `The server is ready: "Game has started." after "Server for ${this.tracker.listening.maxPlayers} players started on port ${this.tracker.listening.port}."`);
      this.emitStatus();
      this.emit('ready', this.status());
    } else if (ev === 'slot') {
      this.world = this.tracker.slot;
    } else if (ev === 'world-in-use') {
      this.worldInUse = this.tracker.worldInUse;
      this.log('sys', `WARNING: the game could not open world ${this.worldInUse} because another running server holds it (Saves\\Slot${this.worldInUse}\\Session.lock), so it is making a NEW world. Stop this server, close the other one, then start again: Steward puts world ${this.worldInUse} back.`);
      this.emitStatus();
      this.emit('world-in-use', this.worldInUse);
    }
    const steam = N.parseSteamLine(text);
    if (steam) {
      this.steam = steam;
      this.emitStatus();
    }
    const listening = N.parseListeningLine(text);
    if (listening) this.listening = listening;
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
  // opts.extraArgs: extra ROK.exe arguments (the live console adds -cport, lib/court-host.js).
  async start(root, exeName = 'Server', opts = {}) {
    if (this.isRunning()) throw Object.assign(new Error('The server is already running.'), { friendly: true });
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
      const logDir = path.join(root, 'Logs');
      await fsp.mkdir(logDir, { recursive: true });
      args.push('-logFile', path.join(logDir, 'realm-server.log'));
      if (opts && Array.isArray(opts.extraArgs)) args.push(...opts.extraArgs.map(String));
    }
    this.root = root;
    this.exe = name;
    this.ready = false;
    this.tracker.reset();
    this.world = null;
    this.worldInUse = null;
    this.gameLog = null;
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

    // Players' Steam tickets are issued for the GAME (344760). Without this the server registers with
    // Steam under the dedicated-server app (381690, from its steam_appid.txt) and every join fails with
    // "Failed to authenticate ... (k_EBeginAuthSessionResultGameMismatch)". Hosting panels set the same.
    const env = Object.assign({}, process.env, { SteamAppId: String(GAME_APP_ID), SteamGameId: String(GAME_APP_ID) });
    const child = spawn(exePath, args, { cwd: root, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true, env });
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
    child.once('exit', (code, signal) => this.exitAfterLastLines(code, signal, child));
    this.startTail(root, { unityLog: name === 'ROK' ? path.join(root, 'Logs', 'realm-server.log') : null });
    this.emitStatus();
    return this.status();
  }

  // Attach to a server that is already running from this folder (lib/prestart.js decides when that
  // is safe). Steward does not own the process: it follows the newest game log, watches the pid, and
  // talks to the server through its admin console (lib/court-host.js attachConsole). It never kills it.
  adopt(root, { pid, exe = 'ROK' } = {}) {
    if (this.isRunning()) throw Object.assign(new Error('The server is already running.'), { friendly: true });
    if (!Number.isInteger(pid) || pid <= 0) throw new TypeError('pid must be a positive integer');
    this.root = root;
    this.exe = exe === 'Server' ? 'Server' : 'ROK';
    this.adopted = { pid };
    this.ready = false;
    this.tracker.reset();
    this.world = null;
    this.worldInUse = null;
    this.gameLog = null;
    this.players.clear();
    this.stdoutSeen = false;
    this.stopOverdue = false;
    this.lastExit = null;
    this.requested = false;
    this.readyAt = null;
    this.steam = null;
    this.listening = null;
    this.startedAt = new Date().toISOString();
    this.state = 'running';
    this.log('sys', `Adopted ${this.exe}.exe (pid ${pid}), already running from ${root}. Steward did not start it: it reads its log and talks to it through its admin console.`);
    this.startTail(root, { adopt: true });
    clearInterval(this.liveTimer);
    this.liveTimer = setInterval(() => {
      if (this.adopted && !this.adopted.gone && !this.isAlive(this.adopted.pid)) {
        this.adopted.gone = true;
        clearInterval(this.liveTimer);
        this.exitAfterLastLines(null, null);
      }
    }, Math.max(50, this.tailMs));
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
    if (!this.child && !this.adopted) return;
    const adopted = !!this.adopted;
    this.child = null;
    this.adopted = null;
    clearInterval(this.liveTimer);
    this.liveTimer = null;
    this.consoleFeed = false;
    clearTimeout(this.stopTimer);
    this.stopTimer = null;
    this.stopTail();
    this.state = 'stopped';
    this.ready = false;
    this.players.clear();
    this.stopOverdue = false;
    const uptimeMs = this.startedAt ? Date.now() - Date.parse(this.startedAt) : null;
    const loadMs = this.readyAt && this.startedAt ? Date.parse(this.readyAt) - Date.parse(this.startedAt) : null;
    this.lastExit = { code, signal: signal || null, error: err ? err.message : null, at: new Date().toISOString(), requested: this.requested, uptimeMs, loadMs, adopted };
    if (!err) this.log('sys', `Server stopped${code != null ? ` (exit code ${code})` : signal ? ` (${signal})` : ''}${adopted ? ' (the adopted process has ended)' : ''}.`);
    this.emitStatus();
    this.emit('exit', this.lastExit);
  }

  // One line typed into the server console (e.g. oxide.version). Never more than one line.
  sendCommand(text) {
    if (this.adopted) throw Object.assign(new Error('Commands to an adopted server go through its admin console, which is not connected.'), { friendly: true });
    if (!this.child) throw Object.assign(new Error('The server is not running.'), { friendly: true });
    const line = String(text).replace(/[\r\n]+/g, ' ').trim();
    if (!line) return false;
    this.log('sys', `> ${line}`);
    this.child.stdin.write(line + '\n');
    return true;
  }

  // Graceful stop: "quit" on stdin, then wait. After the grace period the UI offers Force stop.
  stop() {
    if (!this.isRunning()) return this.status();
    this.requested = true;
    if (this.state !== 'stopping') {
      this.state = 'stopping';
      if (this.child) {
        this.log('sys', 'Sending "quit" to the server and waiting for it to save and close...');
        try {
          this.child.stdin.write('quit\n');
        } catch {
          /* stdin closed: the force option covers it */
        }
      } else {
        this.log('sys', 'Waiting for the adopted server to save and close (Steward has no other way in than its admin console)...');
      }
      this.stopTimer = setTimeout(() => {
        if (this.isRunning()) {
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
    if (this.adopted && !this.child) {
      // Never a blind kill of a process Steward did not start: it may run as administrator, and the
      // game's Server.exe watchdog would start it again anyway.
      this.log('sys', `Steward does not force-end a server it did not start (pid ${this.adopted.pid}). Use Stop (it sends /shutdown over the admin console), or end it yourself in Task Manager > Details.`);
      return Promise.resolve(this.status());
    }
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
    if (!this.isRunning()) return Promise.resolve(true);
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

  // Follows the game's own log: the newest Logs\Log[...].txt created after this start (lib/readiness.js
  // pickGameLog), from its first line, switching if a newer one appears. With adopt, the newest
  // existing one, from near its end. unityLog: Unity's -logFile, shown as well (a crash before the
  // game's logger starts ends up only there). Lines that the admin console also delivers are read for
  // readiness but not shown twice while the console is connected.
  startTail(root, { adopt = false, unityLog = null } = {}) {
    this.stopTail();
    const dir = path.join(root, 'Logs');
    const known = new Set();
    try {
      for (const n of fs.readdirSync(dir)) known.add(n);
    } catch {
      /* no Logs folder yet */
    }
    const startMs = Date.now();
    // Unity truncates its log when it starts; until the file changes it still holds the last run.
    const files = { game: null, unity: unityLog ? { p: unityLog, offset: 0, pending: '', since: startMs - 1000 } : null };
    const readMore = async (f, src, show) => {
      let st;
      try {
        st = await fsp.stat(f.p);
      } catch {
        return;
      }
      if (f.since) {
        if (st.mtimeMs < f.since) return;
        f.since = 0;
        f.offset = 0;
      }
      if (st.size < f.offset) {
        f.offset = 0;
        f.pending = '';
      }
      if (st.size <= f.offset) return;
      const fh = await fsp.open(f.p, 'r');
      try {
        const len = Math.min(st.size - f.offset, 256 * 1024);
        const buf = Buffer.alloc(len);
        await fh.read(buf, 0, len, f.offset);
        f.offset += len;
        let text = f.pending + buf.toString('utf8');
        if (f.skipPartial) {
          // Started mid-file (adopt): drop the cut-off first line.
          const i = text.indexOf('\n');
          if (i < 0) {
            f.pending = text;
            return;
          }
          text = text.slice(i + 1);
          f.skipPartial = false;
        }
        const parts = text.split(/\r?\n/);
        f.pending = parts.pop();
        for (const p of parts) if (p.trim()) this.ingest(src, p, show());
      } finally {
        await fh.close();
      }
    };
    const doTick = async () => {
      try {
        const list = [];
        let names = [];
        try {
          names = await fsp.readdir(dir);
        } catch {
          /* no Logs folder yet */
        }
        for (const name of names) {
          if (!RD.GAME_LOG_NAME_RE.test(name)) continue;
          try {
            const st = await fsp.stat(path.join(dir, name));
            if (st.isFile()) list.push({ name, birthtimeMs: st.birthtimeMs, mtimeMs: st.mtimeMs, size: st.size });
          } catch {
            /* removed meanwhile */
          }
        }
        const pick = RD.pickGameLog(list, { known, adopt });
        if (pick && (!files.game || files.game.name !== pick.name)) {
          const first = !files.game;
          const offset = adopt && first ? Math.max(0, pick.size - ADOPT_BACKLOG_BYTES) : 0;
          files.game = { name: pick.name, p: path.join(dir, pick.name), offset, pending: '', skipPartial: offset > 0 };
          this.gameLog = path.join('Logs', pick.name);
          this.log('sys', `Following ${this.gameLog} (the game's own log${adopt && first ? ', from its recent lines' : ''}).`);
          this.emitStatus();
        }
        if (files.game) await readMore(files.game, 'log', () => !this.consoleFeed);
        if (files.unity) await readMore(files.unity, 'log', () => true);
      } catch {
        /* file locked or gone; try again next tick */
      }
    };
    // One read at a time; a timer tick that finds one running is skipped.
    let running = null;
    const tick = () => {
      if (!running) running = doTick().finally(() => (running = null));
      return running;
    };
    // The last read when the server exits: waits for a read in progress, then reads once more, so
    // the final lines ("The port ... is already being used", "Could not load world ...") are seen.
    this.tailFinal = async () => {
      if (running) await running;
      await tick();
    };
    this.tail = setInterval(tick, this.tailMs);
    tick();
  }

  stopTail() {
    if (this.tail) clearInterval(this.tail);
    this.tail = null;
    this.tailFinal = null;
  }

  // Reads what is left in the log, then reports the exit (at most 3 s later).
  exitAfterLastLines(code, signal, who = null) {
    // A late exit of an earlier child (the Server.exe -> ROK.exe fallback) must not end the new run.
    if (who && this.child !== who) return;
    const last = this.tailFinal;
    if (this.tail) clearInterval(this.tail);
    this.tail = null;
    if (!last) return this.onExit(code, signal);
    const timeout = new Promise((r) => {
      const t = setTimeout(r, 3000);
      if (t.unref) t.unref();
    });
    Promise.race([last().catch(() => {}), timeout]).then(() => {
      if (who && this.child !== who) return;
      this.onExit(code, signal);
    });
  }
}

// Every Server.exe / ROK.exe on this PC: [{ pid, name, path }]. path is '' when Windows will not say
// (a process running as administrator, or the game under Easy Anti-Cheat). Windows only; [] elsewhere.
function listGameProcesses({ platform = process.platform } = {}) {
  if (platform !== 'win32') return Promise.resolve([]);
  const script = "Get-Process -Name 'Server','ROK' -ErrorAction SilentlyContinue | ForEach-Object { $path = $null; try { $path = $_.Path } catch { }; if (-not $path) { $c = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $_.Id) -ErrorAction SilentlyContinue; if ($c) { $path = $c.ExecutablePath } }; '' + $_.Id + '|' + $_.ProcessName + '|' + $path }";
  return new Promise((resolve) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', script], { windowsHide: true, timeout: 15000 }, (err, stdout) => {
      if (err) return resolve([]);
      const out = [];
      for (const line of String(stdout).split(/\r?\n/)) {
        const [pid, name, p] = line.split('|');
        if (pid && /^\d+$/.test(pid.trim()) && name) out.push({ pid: Number(pid), name: name.trim(), path: (p || '').trim() });
      }
      resolve(out);
    });
  });
}

// Get-RealmServerProcess: Server.exe / ROK.exe processes whose image lives in this folder,
// including ones the client did not start. Windows only; elsewhere returns [].
async function findServerProcesses(root, { platform = process.platform } = {}) {
  const all = await listGameProcesses({ platform });
  return all.filter((p) => p.path && S.isInside(p.path, root, 'win32'));
}

module.exports = { ServerManager, findServerProcesses, listGameProcesses, processAlive, ADOPT_BACKLOG_BYTES };
