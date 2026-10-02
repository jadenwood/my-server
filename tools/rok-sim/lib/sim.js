'use strict';

// RokSim: a stand-in for a Reign of Kings dedicated server process (ROK.exe -batchmode) that looks
// like the real one FROM THE OUTSIDE: files it writes, ports it opens, its admin console, its exit.
// Nothing here runs or reads game code. Behaviour and strings come from the decompiled
// Assembly-CSharp.dll of Oxide.ReignOfKings 2.0.3867 (zip sha256 6c35c623...c72c6c8) [DEC]; the
// class names in the log stack lines are the real call sites. Everything not proven is marked
// UNVERIFIED here and in README.md.
//
// Start-up order (UNVERIFIED where noted):
//   Unity log: preamble, "Initialize engine version: <v>"           (wording [SEC], version UNVERIFIED)
//   game log:  "<Product> is now in dedicated mode."                 DedicatedServerBypass.Awake
//              "Admin console enabled."                              SocketAdminConsole.OnEnable (+500 ms)
//              ServerSettings.cfg load/create                        DedicatedServerBypass.Start
//              world slot chosen and locked, "Save slot located at:"  DedicatedServerBypass.StartServer, Game.New/Load
//              ... world load (--sim-load-ms) ...
//              Users/Permissions/... created, "User ... loaded."     CoreServer.Start
//              first run: error rule, two info lines, exit          CoreServer.Start
//              "Steam game server started. (IP: ...)"                SteamServer (a loader; order UNVERIFIED)
//              "Server for N players started on port P."             CoreServer.Start
//              worldSlot written back, "Game has started."           DedicatedServerBypass.OnGameStart, Game.OnLoadingComplete
//              "Type /shutdown to shut down the server."             SocketAdminConsole.OnGameStart
// The two ready lines, "Server for N players started on port P." then "Game has started.", were seen
// in this order on the owner's real server (2026-10-02).

const fs = require('fs');
const path = require('path');
const dgram = require('dgram');
const net = require('net');
const crypto = require('crypto');
const { EventEmitter } = require('events');

const { gameProps, unityLogFile, tryParseInt, simOptions, SIM_DEFAULTS } = require('./cmdline');
const { loadServerSettings } = require('./props');
const { ensureConfigFiles, readWhitelistEnabled, LOAD_ORDER, LOADED_LINES } = require('./configs');
const { GameLogger, UnityLog } = require('./logs');
const { ConsoleServer } = require('./console-server');
const { A2SServer, APP_ID } = require('./a2s');
const { ChronicleWriter, GREAT_HOUSES, demoStory } = require('./chronicle');
const { SlotManager, slotName } = require('./slots');

const PRODUCT_NAME = 'Reign Of Kings'; // GameInfo.ProductName is scene data: UNVERIFIED
const F = {
  bypass: 'CodeHatch.Engine.Networking.DedicatedServerBypass.Awake()',
  core: 'CodeHatch.Engine.Networking.CoreServer.Start()',
  steam: 'CodeHatch.Engine.Internet.SteamServer.StartServer()',
  join: 'CodeHatch.Engine.Networking.CoreServer.NotifyOfPlayerJoin()',
  lost: 'CodeHatch.Engine.Networking.CoreServer.OnConnectionLost()',
  approve: 'CodeHatch.Engine.Networking.CoreServer.ProcessConnectionRequest()',
  console: 'CodeHatch.Engine.Core.Console.AddMessageFinal()',
  save: 'CodeHatch.Engine.Core.Gaming.Game.Save()',
  settings: 'CodeHatch.Engine.Networking.ServerSettingsFile.Load()',
  startServer: 'DedicatedServerBypass.StartServer()',
  gameNew: 'CodeHatch.Engine.Core.Gaming.Game.New()',
  gameLoad: 'CodeHatch.Engine.Core.Gaming.Game.Load()',
  gameEnd: 'CodeHatch.Engine.Core.Gaming.Game.End()',
  loaded: 'CodeHatch.Engine.Core.Gaming.Game.OnLoadingComplete()',
  ping: 'CodeHatch.Engine.Networking.PingGraphManager.OnGameStart()'
};

// Every console command the game registers ([Command("/...")] in the decompile, aliases included).
// Those the simulator does not emulate still "exist": they run silently instead of answering
// "Unknown command".
const REAL_COMMANDS = new Set(
  ('ban banlist build buildreport butcher categorylist clearinv config serverconfig settings debug fly give givecategory givefirst ' +
    'givesearch giveall godmode guild heal help hud hydrate imitate instantbuild itemlist kick killall killbyblueprint killbytype list ' +
    'online players local logout exit leave quit me menu mute notice nourish permission ping popup alert pos question registry report ' +
    'sendnews serverinfo shownametags shutdown restart stophunger stopthirst suicide threadsample time timeonline timesession timestress ' +
    'tp tpdelay unban videofly weather whisper whitelist').split(' ')
);

const STEAM_BASE = 76561197960265728n;
function steamIdFor(name) {
  const h = crypto.createHash('sha256').update(String(name)).digest();
  return (STEAM_BASE + BigInt(h.readUInt32LE(0))).toString();
}

// CommandInfo.Args, approximately: words split on spaces, "..." or '...' group words, "\ " is an
// escaped space (docs/admin-console.md).
function splitArgs(s) {
  const out = [];
  let cur = '';
  let quote = null;
  let has = false;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if (quote) {
      if (ch === quote) quote = null;
      else cur += ch;
      continue;
    }
    if (ch === '\\' && s[i + 1] === ' ') {
      cur += ' ';
      i++;
      has = true;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      has = true;
      continue;
    }
    if (ch === ' ') {
      if (has || cur) out.push(cur);
      cur = '';
      has = false;
      continue;
    }
    cur += ch;
  }
  if (has || cur) out.push(cur);
  return out;
}

// CoreServer: Regex.Replace(name, "[^-\p{L}0-9\._ ]", "").Trim('.', ' ', '\n'); empty -> "John".
function cleanName(name) {
  const n = String(name).replace(/[^-\p{L}0-9._ ]/gu, '').replace(/^[. \n]+|[. \n]+$/g, '');
  return n || 'John';
}

class RokSim extends EventEmitter {
  // opts: argv (ROK.exe arguments plus --sim-* switches), cwd, sim (overrides), now(), stdout, stderr
  constructor(opts = {}) {
    super();
    this.cwd = path.resolve(opts.cwd || process.cwd());
    const parsed = simOptions(opts.argv || []);
    this.sim = { ...parsed.sim, ...(opts.sim || {}) };
    this.argv = parsed.rest;
    this.now = opts.now || (() => new Date());
    this.stdout = opts.stdout || process.stdout;
    this.stderr = opts.stderr || process.stderr;
    this.state = 'new'; // new | booting | loading | running | stopping | exited
    this.players = []; // { name, id, joinedAt, house }
    this.queue = [];
    this.lastJoinAt = 0;
    this.restartAfterShutdown = true; // SocketAdminConsole.RestartAfterShutdown
    this.timers = new Set();
    this.sockets = {};
    this.saves = 0;
    this.lastSaveAt = 0;
    this.courtSeq = 0;
    this.demoStep = 0;
    this.exitCode = null;
  }

  // ------------------------------------------------------------------ helpers

  later(ms, fn) {
    const t = setTimeout(() => {
      this.timers.delete(t);
      fn();
    }, Math.max(0, ms));
    this.timers.add(t);
    return t;
  }

  every(ms, fn) {
    const t = setInterval(fn, Math.max(10, ms));
    this.timers.add(t);
    return t;
  }

  note(msg) {
    // Simulator diagnostics: stderr only, never into the game's logs.
    if (this.sim.echoLogs) this.stderr.write(`[rok-sim] ${msg}\n`);
    this.emit('note', msg);
  }

  status() {
    return {
      state: this.state,
      pid: process.pid,
      cwd: this.cwd,
      ports: {
        game: this.settings ? this.settings.portNumber : null,
        ping: this.settings ? this.settings.pingPort : null,
        steamAuth: this.settings ? this.settings.steamAuthPort : null,
        console: this.console ? this.console.port || null : null,
        a2s: this.a2s ? this.a2s.port : null
      },
      maxPlayers: this.settings ? this.settings.maxPlayers : null,
      players: this.players.map((p) => ({ name: p.name, id: p.id })),
      queued: this.queue.map((p) => p.name),
      consoleClients: this.console ? this.console.clients.size : 0,
      saves: this.saves,
      worldSlot: this.worldSlot == null ? null : this.worldSlot,
      worldIsNew: !!this.worldIsNew,
      gameLog: this.logger ? this.logger.file : null,
      unityLog: this.unityLogPath
    };
  }

  // ------------------------------------------------------------------ start-up

  start() {
    if (this.state !== 'new') throw new Error('already started');
    this.state = 'booting';
    let props;
    const unityTarget = unityLogFile(this.argv);
    this.unityLogPath = unityTarget === '-' ? null : path.resolve(this.cwd, unityTarget || path.join('ROK_Data', 'output_log.txt'));
    this.unity = new UnityLog(unityTarget === '-' ? '-' : this.unityLogPath, { stdout: this.stdout });
    this.logger = new GameLogger({ cwd: this.cwd, now: this.now, debug: this.sim.debugLogs, mirror: this.sim.echoLogs ? (l) => this.stderr.write(l + '\n') : null });
    try {
      props = gameProps(this.argv);
    } catch (e) {
      // SystemUtil.CommandLineProps throws on its first use. UNVERIFIED how Unity reports it.
      this.unity.line(`${e.exceptionType}: ${e.message}`);
      this.later(this.sim.bootMs, () => this.exit(1));
      return this;
    }
    this.props = props;
    this.later(this.sim.bootMs, () => this.boot());
    return this;
  }

  boot() {
    const dataDir = path.join(this.cwd, 'ROK_Data');
    this.unity.line(`Mono path[0] = '${path.join(dataDir, 'Managed').split(path.sep).join('/')}'`);
    this.unity.line(`Mono config path = '${path.join(dataDir, 'Mono', 'etc').split(path.sep).join('/')}'`);
    this.unity.line(`Initialize engine version: ${this.sim.unityVersion}`);
    this.unity.line('GfxDevice: creating device client; threaded=1');
    this.unity.line('NullGfxDevice:');
    this.unity.line('    Version:  NULL 1.0 [1.0]');
    this.unity.line('    Renderer: Null Device');
    this.unity.line('    Vendor:   Unity Technologies');
    this.unity.line('Begin MonoManager ReloadAssembly');
    this.unity.line('- Completed reload, in  0.512 seconds');
    this.emit('engine');

    // DedicatedServerBypass.Enabled = batchmode && !ip. Without it the real exe is a game client.
    const dedicated = this.props.has('batchmode') && !this.props.has('ip');
    if (!dedicated) {
      this.note(this.props.has('ip') ? '-ip was given: the real ROK.exe would start as a game CLIENT and try to join (docs/join-and-scale.md 1.3). Exiting.' : 'no -batchmode: the real ROK.exe would open the game menu. Exiting.');
      this.later(this.sim.exitDelayMs, () => this.exit(2));
      return;
    }
    this.logger.info('{0} is now in dedicated mode.', [PRODUCT_NAME], [F.bypass]);

    // SocketAdminConsole.OnEnable (Thread.Sleep(500) before its log line is folded into bootMs).
    const port = this.props.has('cport') ? tryParseInt(this.props.get('cport')) : 11000;
    this.console = new ConsoleServer({
      port,
      host: this.sim.consoleHost,
      keepAliveMs: this.sim.keepAliveMs,
      frameMs: this.sim.frameMs,
      logger: this.logger,
      cportRule: this.props.has('cport'),
      cportWindowMs: this.sim.cportWindowMs
    });
    this.console.on('submit', (text, collected) => this.submit(text, collected));
    this.console.on('shutdown-request', (why) => {
      this.note(`console asked for shutdown (${why})`);
      if (why === 'cport-window') {
        // enabled = false -> OnDisable: "Admin console disabled." and the socket server closes.
        this.logger.info('Admin console disabled.', [], ['CodeHatch.Engine.Administration.SocketAdminConsole.OnDisable()']);
        this.console.close();
      }
      // Server.Shutdown() returns at once unless the server is running (still loading: nothing happens).
      if (this.state === 'running') this.shutdown(why);
    });
    this.console.start().then(() => {
      this.emit('console', this.console.port || null);
      this.loadSettings();
    });
  }

  loadSettings() {
    const cfgDir = path.join(this.cwd, 'Configuration');
    const r = loadServerSettings(path.join(cfgDir, 'ServerSettings.cfg'));
    if (!r.ok) {
      // ServerSettingsFile.Load catches, logs the exception, returns false -> Program.Exit().
      this.logger.exception(r.error.exceptionType, r.error.message, [F.settings, 'CodeHatch.Engine.Networking.DedicatedServerBypass.Start()']);
      this.programExit(0, { restartAfterShutdown: true });
      return;
    }
    this.settings = r.settings;
    this.settingsFile = r.file;
    this.firstRun = r.firstCreate;
    this.emit('settings', { ...this.settings }, this.firstRun);
    if (!this.startServer()) return;
    this.state = 'loading';
    this.later(this.sim.loadMs, () => this.coreServerStart());
  }

  // DedicatedServerBypass.StartServer [DEC]: pick the world slot before the world loads. Returns false
  // when the game ends here (a slot that cannot be loaded).
  startServer() {
    const loc = this.settings.saveLocation || 'Saves/';
    this.slots = new SlotManager(path.resolve(this.cwd, loc));
    let slot = this.settings.worldSlot;
    if (slot >= 0 && this.slots.isLocked(slot)) {
      // Console.AddWarning (logged as Info, like AddError) and LogWarning: both reach the log.
      const text = `Could not load world ${slot}. Loading new world instead.`;
      this.logger.info(text, [], [F.console]);
      this.logger.warn(text, [], [F.startServer]);
      this.settingsFile.setValue('worldSlot', '-1');
      slot = -1;
    }
    const isNew = slot < 0 || !this.slots.exists(slot);
    if (isNew && slot < 0) slot = this.slots.nextAvailable();
    // Game.New / Game.Load are called with allowSaving: true, so the slot is always locked.
    this.slots.lock(slot);
    this.worldSlot = slot;
    this.worldIsNew = isNew;
    this.emit('world', { slot, isNew });
    // Path.Combine(saveLocation, "Slot<N>") on Windows.
    const shown = /[\\/]$/.test(loc) ? loc + slotName(slot) : `${loc}\\${slotName(slot)}`;
    this.logger.info(`Save slot located at: ${shown}`, [], [isNew ? F.gameNew : F.gameLoad]);
    const dir = this.slots.slotPath(slot);
    const info = path.join(dir, 'rok-sim-slotinfo.json');
    if (isNew) {
      // GameSlotInfo.Save: the real file format is not emulated; this marker stands in for it.
      if (!fs.existsSync(info)) fs.writeFileSync(info, JSON.stringify({ note: 'rok-sim marker, not a game save', slot, createdAt: this.now().toISOString() }) + '\n');
    } else if (!fs.readdirSync(dir).some((n) => n !== 'Session.lock')) {
      // GameSlotInfo.Load found nothing: Game.End("Could not load game slot {0}.", slot + 1).
      this.settingsFile.save();
      this.endGame(`Could not load game slot ${slot + 1}.`);
      return false;
    }
    this.settingsFile.save();
    return true;
  }

  // Game.End(reason): logs the reason as an error and "Ending game...". Before the world has loaded
  // DedicatedServerBypass.OnGameEnd then calls Program.Exit.
  endGame(reason) {
    if (reason) this.logger.error(reason, [], [F.gameEnd]);
    this.logger.info('Ending game...', [], [F.gameEnd]);
    this.programExit(0, { restartAfterShutdown: true });
  }

  async coreServerStart() {
    if (this.state !== 'loading') return;
    const cfgDir = path.join(this.cwd, 'Configuration');
    ensureConfigFiles(cfgDir);
    for (const name of LOAD_ORDER) if (LOADED_LINES[name]) this.logger.info(LOADED_LINES[name], [], [F.core]);

    if (this.firstRun) {
      this.logger.error('-------------------------------------------------------------------', [], [F.core]);
      this.logger.info('This is the first time you have run this server.', [], [F.core]);
      this.logger.info('Please look over your config files under the Configuration/ folder.', [], [F.core]);
      // SendLogs = false (nothing more reaches the console), RestartAfterShutdown = false (Disconnect).
      if (this.console) this.console.sendLogs = false;
      this.programExit(this.sim.firstRunExitCode, { restartAfterShutdown: false });
      return;
    }

    await this.startSteam();
    if (this.state !== 'loading') return;

    // Network.InitializeServer(int.MaxValue, Port) on bindIP when it parses as an IP.
    const bind = net.isIP(this.settings.bindIP) ? this.settings.bindIP : '0.0.0.0';
    try {
      this.sockets.game = await bindUdp(this.settings.portNumber, bind);
    } catch (e) {
      const reason = e.code === 'EADDRINUSE' ? `The port ${this.settings.portNumber} is already being used by another application.` : `Could not start the server because an error occured. (${e.code || e.message})`;
      // Game.End(reason) [DEC]. The new slot folder chosen above stays behind (--sim-failed-slot).
      this.endGame(reason);
      return;
    }
    this.sockets.game.on('message', () => {}); // uLink traffic is not emulated
    this.logger.info('Server for {0} players started on port {1}.', [this.settings.maxPlayers, this.settings.portNumber], [F.core]);
    this.state = 'running';
    this.startedAt = Date.now();
    await this.gameStart();
  }

  async startSteam() {
    const port = this.settings.steamAuthPort;
    const initFail = () => {
      this.logger.error('The Steam game server could not initialize.', [], [F.steam]);
      this.logger.error('1) Make sure you have allowed port {0} through your firewall and forwarded the port through your router if you have one.', [port], [F.steam]);
      this.logger.error('2) If you are running multiple servers on the same system, make sure the steamAuthPort in ServerSettings is different for each server.', [], [F.steam]);
      this.logger.error('3) Ensure the steam servers are not currently down.', [], [F.steam]);
    };
    this.steamOk = false;
    if (this.sim.steam === 'init-fail') return initFail();
    if (this.sim.steam === 'no-login' || this.sim.steam === 'no-secure') {
      await new Promise((r) => this.later(this.sim.steamWaitMs, r));
      this.logger.error(this.sim.steam === 'no-login' ? 'Could not start the Steam game server because the Steam server could not login anonymously.' : 'Could not start the Steam game server because the Steam server could not be started in secure mode.', [], [F.steam]);
      return;
    }
    this.a2s = new A2SServer({
      port,
      challenge: this.sim.a2sChallenge,
      dropRate: this.sim.a2sDrop,
      info: () => ({
        name: 'Another ROK Server',
        map: '',
        folder: this.sim.a2sFolder,
        game: 'ROK Server',
        shortAppId: APP_ID & 0xffff,
        players: this.sim.a2sPlayers === 'zero' ? 0 : this.players.length,
        maxPlayers: 0,
        bots: 0,
        passworded: false,
        vac: true,
        version: '1.0.0.0',
        gamePort: this.settings.portNumber,
        steamId: '90000000000000001',
        gameId: APP_ID
      }),
      players: () => (this.sim.a2sPlayers === 'zero' ? [] : this.players)
    });
    try {
      await this.a2s.start();
    } catch (e) {
      this.a2s = null;
      initFail();
      return;
    }
    this.steamOk = true;
    this.logger.info('Steam game server started. (IP: {0}, Logged: {1}, Secure: {2})', [this.sim.publicIp, 'True', 'True'], [F.steam]);
  }

  async gameStart() {
    // PingGraphManager.OnGameStart: TCP on IPAddress.Any, pingPort, backlog 100.
    try {
      this.sockets.ping = await listenTcp(this.settings.pingPort, '0.0.0.0', (sock) => {
        sock.on('error', () => {});
        sock.on('data', () => {}); // the ping protocol is not emulated; connections are held
      });
    } catch (e) {
      this.logger.exception('SocketException', e.code === 'EADDRINUSE' ? 'Address already in use' : e.message, [F.ping]);
    }
    // GameStartEvent: the console line, the world slot written back to ServerSettings.cfg.
    if (this.console) this.console.defer(() => this.logger.info('Type /shutdown to shut down the server.', [], ['CodeHatch.Engine.Administration.SocketAdminConsole.Update()']));
    // DedicatedServerBypass.OnGameStart: worldSlot = the slot actually running, saved at once.
    this.settingsFile.setValue('worldSlot', String(this.worldSlot));
    this.settingsFile.save();
    // Game.OnLoadingComplete: Save(), then "Game has started." The start-up save is written without
    // its log line or save count, so /realm.save's 10-second rule is not affected.
    this.writeWorldMarker();
    this.hasLoaded = true;
    this.logger.info('Game has started.', [], [F.loaded]);
    this.whitelist = readWhitelistEnabled(path.join(this.cwd, 'Configuration'));

    this.queueTimer = this.every(250, () => this.joinQueued());
    if (this.settings.restartTime > 0) {
      // CoreServer's daily restart: a stop that "wants a restart" (no Disconnect packet). Log text UNVERIFIED.
      this.later(this.settings.restartTime * 1000, () => this.shutdown('restartTime', { restart: true }));
    }
    this.startChronicle();
    this.state = 'running';
    this.emit('ready', this.status());
    for (const n of this.sim.players) this.join(n);
    if (this.sim.scenario) this.runScenario(this.sim.scenario);
  }

  // ------------------------------------------------------------------ chronicle

  startChronicle() {
    if (this.sim.chronicle === 'off') return;
    this.chronicle = new ChronicleWriter({
      dataDir: path.resolve(this.cwd, this.sim.oxideData || path.join('oxide', 'data')),
      tornWrites: this.sim.tornWrites,
      now: this.now
    });
    this.houses = GREAT_HOUSES.map((h) => ({ ...h, liege: null, members: 0 }));
    this.refreshState();
    this.every(this.sim.stateRefreshMs, () => this.refreshState());
    if (this.sim.chronicle === 'demo') {
      const tick = () => {
        const s = demoStory(this.demoStep++);
        if (s.liege) {
          const h = this.houses.find((x) => x.name === s.liege[0]);
          if (h) h.liege = s.liege[1];
        }
        if (s.crown) this.chronicle.setCrown(s.crown[0], s.crown[1]);
        this.chronicleEvent(...s.ev);
        this.refreshState();
      };
      tick();
      this.every(this.sim.chronicleEveryMs, tick);
    }
  }

  refreshState() {
    if (!this.chronicle) return;
    for (const h of this.houses) h.members = this.players.filter((p) => p.house === h.name).length;
    this.chronicle.setHouses(this.houses);
    this.chronicle.writeState(this.players.length, this.settings.maxPlayers);
    this.emit('state-written');
  }

  queueStateRefresh() {
    if (!this.chronicle || this.stateQueued) return;
    this.stateQueued = true;
    this.later(250, () => {
      this.stateQueued = false;
      this.refreshState();
    });
  }

  chronicleEvent(type, title, detail, actors) {
    if (!this.chronicle) {
      this.note('chronicle event ignored: start with --sim-chronicle on|demo');
      return null;
    }
    const ev = this.chronicle.log(type, title, detail, actors);
    if (ev) this.emit('chronicle', ev);
    return ev;
  }

  // ------------------------------------------------------------------ players

  // A player connects. Returns the player name actually used.
  join(rawName, id) {
    if (this.state !== 'running') return null;
    const name = cleanName(rawName);
    const steamId = id ? String(id) : steamIdFor(name);
    if (this.players.some((p) => p.id === steamId) || this.queue.some((p) => p.id === steamId)) return null;
    const ip = `198.51.100.${(this.players.length + this.queue.length + 7) % 250}`; // documentation range
    this.logger.info('Processing new connection... ({0})', [ip], [F.approve]);
    if (this.whitelist && !this.whitelistHas(steamId)) {
      this.logger.error('Player {0} denied connection because they were not on the active whitelist.', [name], [F.approve]);
      return null;
    }
    this.logger.info('Beginning Steam authentication with {0} ({1}).', [name, steamId], [F.approve]);
    if (!this.steamOk) {
      // Without Steam the auth callback reports failure ([DEC] SteamServer.BeginAuthSession). UNVERIFIED at run time.
      this.logger.error('Failed to authenticate {0} ({1}) with Steam.', [name, steamId], ['CodeHatch.Engine.Networking.CoreServer.OnAuthenticated()']);
      return null;
    }
    this.logger.info('Authentication verified for {0} ({1}).', [name, steamId], ['CodeHatch.Engine.Networking.CoreServer.OnAuthenticated()']);
    const p = { name, id: steamId, house: GREAT_HOUSES[parseInt(steamId.slice(-3), 10) % GREAT_HOUSES.length].name };
    this.queue.push(p);
    this.joinQueued();
    if (this.queue.includes(p)) this.logger.debugLine('{0} has joined the queue.', [name], ['CodeHatch.Engine.Networking.CoreServer.PlayerShouldBeQueued()']);
    return name;
  }

  whitelistHas(id) {
    try {
      return fs.readFileSync(path.join(this.cwd, 'Configuration', 'Whitelist.cfg'), 'utf8').includes(id);
    } catch {
      return false;
    }
  }

  // CoreServer.PlayerShouldBeQueued: below the limit, timeBetweenPlayerJoin since the last join, first in line.
  joinQueued() {
    if (this.state !== 'running' || !this.queue.length) return;
    const gap = (this.settings.timeBetweenPlayerJoin || 0) * 1000;
    if (this.players.length >= this.settings.maxPlayers) return;
    if (this.lastJoinAt && Date.now() - this.lastJoinAt < gap) return;
    const p = this.queue.shift();
    p.joinedAt = Date.now();
    this.players.push(p);
    this.lastJoinAt = Date.now();
    this.logger.info('{0} has connected. ({1})', [p.name, p.id], [F.join]);
    this.emit('join', { name: p.name, id: p.id });
    this.queueStateRefresh();
  }

  leave(name, { kickedBecause = null, kicked = false } = {}) {
    const i = this.players.findIndex((p) => p.name === name);
    if (i < 0) {
      const q = this.queue.findIndex((p) => p.name === name);
      if (q >= 0) {
        const [qp] = this.queue.splice(q, 1);
        this.logger.debugLine('{0} has left the queue.', [qp.name], [F.lost]);
        return true;
      }
      return false;
    }
    const [p] = this.players.splice(i, 1);
    if (kicked) {
      if (kickedBecause) this.logger.info('{0} ({1}) was kicked from the server because {2}.', [p.name, p.id, kickedBecause], ['CodeHatch.Engine.Networking.CoreServer.Kick()']);
      else this.logger.info('{0} ({1}) was kicked from the server.', [p.name, p.id], ['CodeHatch.Engine.Networking.CoreServer.Kick()']);
    }
    this.logger.info('{0} has disconnected.', [p.name], [F.lost]);
    if (this.steamOk) this.logger.info('Ending Steam auth session with {0}.', [p.id], ['CodeHatch.Engine.Internet.SteamServer.EndAuthSession()']);
    this.emit('leave', { name: p.name, id: p.id });
    this.queueStateRefresh();
    this.joinQueued();
    return true;
  }

  // Server.MatchFirstPlayerByName, approximately: exact name first, then the first prefix match (case-insensitive).
  matchPlayer(text) {
    const t = String(text).toLowerCase();
    return this.players.find((p) => p.name.toLowerCase() === t) || this.players.find((p) => p.name.toLowerCase().startsWith(t)) || null;
  }

  // ------------------------------------------------------------------ console commands

  // Console.Submit(text) as the server player. `collected` gathers ConsoleAddMessageEvent text for the
  // Response body; the game raises none for normal command output, so it stays empty.
  submit(text, collected) {
    this.emit('command', text);
    if (!text || text === '/') return;
    if (!text.startsWith('/')) {
      // Chat from the server. UNVERIFIED: the exact [C] text (ChatFormat is player data).
      if (this.console) this.console.send(`[C] Server: ${text}`);
      return;
    }
    const say = (s) => {
      // player.SendMessage -> Console.AddMessage: one Logger.Info per line.
      for (const line of String(s).split('\n')) this.logger.info(line, [], [F.console]);
    };
    const body = text.slice(1);
    const sp = body.indexOf(' ');
    const label = (sp < 0 ? body : body.slice(0, sp)).toLowerCase();
    const rest = sp < 0 ? '' : body.slice(sp + 1);
    const args = splitArgs(rest);

    if (label.startsWith('sim.') && this.sim.simCommands) return this.simCommand(label.slice(4), rest, args);
    if ((label === 'realm.save' || label === 'realm.players') && this.realmCourtOn()) return this.realmCourt(label, say);

    switch (label) {
      case 'list':
      case 'online':
      case 'players':
        // ThronesCommandHandler.list (dedicated: ClientPlayers)
        if (this.players.length) say(`Online Players(${this.players.length}):\n${this.players.map((p) => p.name).join(', ')}`);
        else say('There are no players online.');
        return;
      case 'shutdown':
      case 'restart':
      case 'logout':
      case 'exit':
      case 'leave':
      case 'quit': {
        // CoreCommandHandler.Shutdown (Logout calls it for the server player).
        say('Server has shut down the server.');
        const restart = label === 'restart';
        if (!restart) this.restartAfterShutdown = false;
        // Server.Shutdown() runs inside the command; the Response follows; then the program exits.
        this.shutdown(`/${label}`, { restart, deferExit: true });
        return;
      }
      case 'kick': {
        const who = args[0] || '';
        const p = this.matchPlayer(who);
        if (!who || !p) return say(`${who} was not found on the server.`);
        const reason = args.length > 1 ? args.slice(1).join('') : null; // string.Join(string.Empty, ...)
        this.leave(p.name, { kicked: true, kickedBecause: reason });
        return;
      }
      case 'notice':
      case 'popup':
      case 'alert':
        if (!rest.trim()) say('Please enter a message.');
        else this.emit('notice', { kind: label, text: rest });
        return;
      default:
        if (REAL_COMMANDS.has(label)) {
          this.note(`/${label} is a real command the simulator does not emulate; it ran silently`);
          return;
        }
        // PlayerListener.OnPlayerCommand -> SendError -> Console.AddError -> Logger.Info ("[I]", not "[E]").
        say(`Unknown command '${text}'. For help type /help`);
    }
  }

  realmCourtOn() {
    if (this.sim.realmCourt === 'on') return true;
    if (this.sim.realmCourt === 'off') return false;
    return fs.existsSync(path.join(this.cwd, 'oxide', 'plugins', 'RealmCourt.cs'));
  }

  // plugins/RealmCourt.cs behaviour (it adds these two commands to the game's command table).
  realmCourt(label, say) {
    if (label === 'realm.save') {
      if (this.lastSaveAt && Date.now() - this.lastSaveAt < 10000) return say('REALMCOURT|save|wait|The world was saved less than 10 seconds ago.');
      this.saveGame();
      return say('REALMCOURT|save|ok|World saved.');
    }
    this.courtSeq++;
    const lines = [`REALMCOURT|${this.courtSeq}|players|${this.players.length}`];
    for (const p of this.players) lines.push(`REALMCOURT|${this.courtSeq}|p|${p.id}|${p.name.replace(/\|/g, '/')}`);
    say(lines.join('\n'));
  }

  // Simulator-only commands (not in the game). Answers go to the console only, never to the game log.
  simCommand(cmd, rest, args) {
    const reply = (s) => this.console && this.console.send(`[I] ${s}`);
    switch (cmd) {
      case 'echo':
        return reply(rest);
      case 'status':
        return reply(`SIMSTATUS ${JSON.stringify(this.status())}`);
      case 'join': {
        const n = this.join(args[0] || 'John', args[1]);
        return reply(n ? `SIM join ${n}` : 'SIM join refused');
      }
      case 'leave':
        return reply(this.leave(args.join(' ')) ? 'SIM leave ok' : 'SIM leave: no such player');
      case 'chat': {
        const who = args[0] || 'John';
        const msg = rest.slice(rest.indexOf(args[0]) + (args[0] || '').length).trim();
        if (this.console) this.console.send(`[C] ${who}: ${msg}`);
        return reply('SIM chat ok');
      }
      case 'event': {
        const [type, title, detail, actors] = splitEventLine(rest);
        const ev = this.chronicleEvent(type, title, detail, actors);
        return reply(ev ? `SIM event ${ev.id}` : 'SIM event refused');
      }
      case 'crown':
        if (!this.chronicle) return reply('SIM crown: chronicle off');
        if (!args[0] || args[0] === 'none') this.chronicle.setCrown(null, null);
        else this.chronicle.setCrown(args[0], args[1] || null);
        this.refreshState();
        return reply('SIM crown ok');
      case 'log': {
        const level = ['info', 'warn', 'error'].includes(args[0]) ? args[0] : args[0] === 'exception' ? 'exception' : 'info';
        const msg = rest.slice((args[0] || '').length).trim();
        if (level === 'exception') this.logger.exception('Exception', msg, ['RokSim.Inject()']);
        else this.logger.log(level, msg, [], ['RokSim.Inject()']);
        return reply('SIM log ok');
      }
      case 'crash':
        reply('SIM crash');
        return this.later(20, () => this.crash(args[0] != null ? tryParseInt(args[0]) : this.sim.crashExitCode));
      default:
        return reply(`SIM unknown command sim.${cmd}`);
    }
  }

  // ------------------------------------------------------------------ scenario

  runScenario(file) {
    let steps;
    try {
      const doc = JSON.parse(fs.readFileSync(path.resolve(this.cwd, file), 'utf8'));
      steps = Array.isArray(doc) ? doc : doc.steps;
      if (!Array.isArray(steps)) throw new Error('expected an array or { steps: [] }');
    } catch (e) {
      this.note(`scenario ${file}: ${e.message}`);
      return;
    }
    for (const s of steps) {
      this.later(Number(s.at) || 0, () => {
        if (this.state !== 'running') return;
        switch (s.do) {
          case 'join':
            return this.join(s.name, s.id);
          case 'leave':
            return this.leave(s.name);
          case 'kick':
            return this.submit(`/kick "${s.name}" ${s.reason || ''}`.trim(), []);
          case 'chat':
            return this.console && this.console.send(`[C] ${s.name}: ${s.text}`);
          case 'event':
            return this.chronicleEvent(s.type, s.title, s.detail || '', s.actors || []);
          case 'crown':
            if (this.chronicle) {
              this.chronicle.setCrown(s.king || null, s.house || null);
              this.refreshState();
            }
            return;
          case 'log':
            return s.level === 'exception' ? this.logger.exception(s.type || 'Exception', s.text, s.frames || []) : this.logger.log(s.level || 'info', s.text, [], s.frames || []);
          case 'command':
            return this.submit(s.text, []);
          case 'crash':
            return this.crash(s.code == null ? this.sim.crashExitCode : s.code);
          case 'shutdown':
            this.restartAfterShutdown = false;
            return this.shutdown('scenario');
          default:
            this.note(`scenario: unknown step ${JSON.stringify(s)}`);
        }
      });
    }
  }

  // ------------------------------------------------------------------ stopping

  saveGame() {
    // Game.Save: "Saving game..." and the save files under saveLocation. The simulator writes one
    // small JSON marker, NOT a real save (the real format is not emulated).
    this.logger.info('Saving game...', [], [F.save]);
    this.saves++;
    this.lastSaveAt = Date.now();
    this.writeWorldMarker();
    this.emit('saved', this.saves);
  }

  // <saveLocation>/Slot<N>/rok-sim-world.json: a marker, NOT a game save (the format is not emulated).
  writeWorldMarker() {
    if (!this.settings || !this.settings.allowSaving || !this.slots || this.worldSlot == null) return;
    const dir = this.slots.slotPath(this.worldSlot);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'rok-sim-world.json'), JSON.stringify({ note: 'rok-sim marker, not a game save', slot: this.worldSlot, saves: this.saves, savedAt: this.now().toISOString(), players: this.players.map((p) => p.name) }, null, 2));
  }

  // Game.OnDestroy -> UnlockCurrentSlot. A new slot whose world never loaded is deleted by the game's
  // unloader (GameSlotInfo.Delete) only if unloading finishes before the process quits; the owner's
  // server kept several such folders, so the default is to keep it (--sim-failed-slot delete).
  releaseSlot() {
    if (!this.slots) return;
    const slot = this.slots.current;
    this.slots.unlock();
    if (this.sim.failedSlot === 'delete' && !this.hasLoaded && this.worldIsNew && slot >= 0) {
      fs.rmSync(this.slots.slotPath(slot), { recursive: true, force: true });
    }
  }

  // Server.Shutdown -> DisconnectAllPlayers, Game.Save, then the program exits (GameEnd -> Program.Exit).
  shutdown(why, { restart = false, deferExit = false } = {}) {
    if (this.state === 'stopping' || this.state === 'exited') return;
    const wasRunning = this.state === 'running';
    this.state = 'stopping';
    this.note(`shutdown: ${why}`);
    this.emit('shutdown', why);
    if (wasRunning) {
      this.players = [];
      this.queue = [];
      this.saveGame();
    }
    const go = () => this.programExit(0, { restartAfterShutdown: restart ? true : this.restartAfterShutdown });
    if (deferExit) this.later((this.sim.frameMs || 0) * 2 + 20, go);
    else go();
  }

  // Program.Exit -> ProgramExitEvent: Disconnect packet only when no restart is wanted.
  async programExit(code, { restartAfterShutdown }) {
    if (this.state === 'exited' || this.exiting) return;
    this.exiting = true;
    if (this.state !== 'stopping') this.state = 'stopping';
    if (this.console) await this.console.programExit(restartAfterShutdown);
    await this.closeSockets();
    this.releaseSlot();
    if (this.chronicle) this.chronicle.flush();
    this.later(this.sim.exitDelayMs, () => this.exit(code));
  }

  // A crash: no save, no Disconnect packet, sockets torn down.
  async crash(code) {
    if (this.state === 'exited' || this.exiting) return;
    this.exiting = true;
    this.state = 'stopping';
    if (this.console) {
      for (const c of this.console.clients) c.sock.destroy();
      this.console.clients.clear();
      await this.console.close();
    }
    await this.closeSockets();
    if (this.slots) this.slots.release(); // the lock file stays, its handle is gone
    this.exit(code);
  }

  async closeSockets() {
    if (this.a2s) await this.a2s.close();
    if (this.sockets.game) {
      try {
        this.sockets.game.close();
      } catch {
        /* closed */
      }
    }
    if (this.sockets.ping) {
      const srv = this.sockets.ping;
      for (const c of srv.held) c.destroy();
      await new Promise((r) => srv.close(() => r()));
    }
    this.sockets = {};
  }

  exit(code) {
    if (this.state === 'exited') return;
    for (const t of this.timers) {
      clearTimeout(t);
      clearInterval(t);
    }
    this.timers.clear();
    if (this.console) this.console.close();
    if (this.a2s) this.a2s.close();
    if (this.slots && this.slots.token) this.slots.release();
    this.state = 'exited';
    this.exitCode = code;
    this.emit('exit', code);
  }

  // For tests and embedders: stop as if the process were killed (no save, no Disconnect).
  async kill() {
    return this.crash(137);
  }
}

// "/sim.event <type> <title> | <detail> | <actor>, <actor>"
function splitEventLine(rest) {
  const t = String(rest).trim();
  const sp = t.indexOf(' ');
  const type = sp < 0 ? t : t.slice(0, sp);
  const parts = (sp < 0 ? '' : t.slice(sp + 1)).split('|').map((s) => s.trim());
  return [type, parts[0] || '', parts[1] || '', parts[2] ? parts[2].split(',').map((s) => s.trim()).filter(Boolean) : []];
}

function bindUdp(port, host) {
  return new Promise((resolve, reject) => {
    const s = dgram.createSocket({ type: 'udp4', reuseAddr: false });
    s.once('error', reject);
    s.bind(port, host, () => {
      s.removeListener('error', reject);
      s.on('error', () => {});
      resolve(s);
    });
  });
}

function listenTcp(port, host, onConn) {
  return new Promise((resolve, reject) => {
    const srv = net.createServer((sock) => {
      srv.held.add(sock);
      sock.on('close', () => srv.held.delete(sock));
      onConn(sock);
    });
    srv.held = new Set();
    srv.once('error', reject);
    srv.listen({ port, host, backlog: 100 }, () => {
      srv.removeListener('error', reject);
      resolve(srv);
    });
  });
}

module.exports = { RokSim, splitArgs, cleanName, steamIdFor, splitEventLine, REAL_COMMANDS, SIM_DEFAULTS };
