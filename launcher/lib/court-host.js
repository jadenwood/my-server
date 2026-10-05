'use strict';

// Main-process glue for the live admin console and the Court (Realm Steward only).
//
// Design (docs/admin-console.md has the reasoning and the decompiled sources):
// - ROK.exe is started with -cport <11000 + slot - 1>, so each server has its own console port
//   and Steward never talks to the wrong server. Steward starts connecting the moment the process
//   is spawned and keeps retrying until the game's console listens, well inside the game's 10 s
//   rule. The connection is held for the whole run.
// - Closing that connection makes the game save and shut down. Steward therefore only closes it
//   as part of a stop. If Steward itself dies, its servers save and stop instead of running on
//   unsupervised.
// - Stop = "/shutdown" over the console (save + exit, the game's own path). If the game is still
//   up 25 s later, Steward closes the console, which triggers the same save + shutdown. The
//   existing "quit on stdin" and Force stop stay as fallbacks.
// - If the console is never reached and the server exits within 2 minutes, -cport is turned off
//   for that server for the rest of the session and the reason is logged.
// - Server.exe (the wrapper) is never given -cport: it manages the console itself. Commands then
//   keep going to stdin.
// - An adopted server (lib/prestart.js, ServerManager.adopt) gets the same held connection through
//   attachConsole(): Steward connects to the console port it already listens on. From then on the
//   same rule applies: Steward is a console client, so closing Steward stops that server too.

const fsp = require('fs/promises');
const path = require('path');
const AC = require('./admin-console');
const MOD = require('./moderation');
const N = require('./netcheck');

const FEED_MAX = 1500;
const STOP_CLOSE_AFTER_MS = 25 * 1000;
const EARLY_EXIT_MS = 2 * 60 * 1000;
const ID_RE = /^s[1-4]$/;

// cportFor and stopCloseAfterMs exist for tests; Steward always uses the fixed plan (11000 + slot - 1).
function createCourt({ userData, settings, instOf, rootOf, clipboard, shell, platform = process.platform, log = () => {}, cportFor = AC.cportForId, stopCloseAfterMs = STOP_CLOSE_AFTER_MS }) {
  const dir = path.join(userData, 'court');
  const prefs = new MOD.ConsolePrefs(dir);
  const courtLog = new MOD.CourtLog(dir);
  const state = new Map(); // id -> { con, cport, m, feed, seq, cportOff, players, whitelist, ... }

  function st(id) {
    if (!state.has(id)) state.set(id, { con: null, cport: null, m: null, feed: [], seq: 0, cportOff: null, players: null, playersAt: null, whitelist: null, stopTimer: null, staleLogged: false, spawnedAt: null });
    return state.get(id);
  }

  function feedPush(s, line) {
    s.feed.push({ n: ++s.seq, at: line.at || Date.now(), kind: line.kind || 'raw', level: line.level || null, text: String(line.text).slice(0, 4000) });
    if (s.feed.length > FEED_MAX) s.feed.splice(0, s.feed.length - FEED_MAX);
  }

  function sys(s, text) {
    if (s.m) s.m.log('sys', text);
    feedPush(s, { kind: 'steward', text });
  }

  function wireConsole(id, s, con) {
    con.on('connected', () => {
      s.staleLogged = false;
      // The console now delivers the game's lines; the log-file tail stops showing the same ones.
      if (s.m) s.m.consoleFeed = true;
      sys(s, `Admin console connected (127.0.0.1:${con.port}). Commands now go straight to the game, with its answers.`);
    });
    con.on('line', (line) => {
      // RealmCourt's machine-readable roster lines are for Steward, not for the hall.
      if (!/^REALMCOURT\|/.test(line.text)) feedPush(s, line);
      // The game's own logger writes to <server>\Logs\Log[...].txt, not to stdout or -logFile, so
      // without this the Servers console never shows the game's warnings, errors and exceptions.
      // Every console line (except the Court's background roster polls) goes to the server log too.
      if (s.m && !/^REALMCOURT\|/.test(line.text) && !isRosterPoll(s, con)) {
        const tag = line.level && line.level !== 'I' ? `[${line.level}] ` : '';
        s.m.log(line.level === 'E' || line.level === 'X' ? 'err' : 'con', tag + line.text);
      }
      // Join and leave lines also come through here; keep the roster fresh without polling.
      if (line.kind === 'info' && /has disconnected\.$|^Authentication verified for /.test(line.text)) s.playersAt = null;
    });
    con.on('state', (status) => {
      if (status.stale && !s.staleLogged) {
        s.staleLogged = true;
        sys(s, 'The game has not sent its 1-second keep-alive for a while: it is busy (loading or saving) or frozen. Steward keeps the connection open.');
      } else if (!status.stale) s.staleLogged = false;
    });
    con.on('server-disconnect', () => sys(s, 'The game said goodbye on the admin console: it is shutting down and does not want a restart.'));
    con.on('protocol-error', (e) => {
      sys(s, `The admin console sent data Realm cannot read (${e.message}). The connection is kept open so the server keeps running; console output falls back to the log file.`);
      log('warn', `[court ${id}] protocol error: ${e.message}`);
    });
    con.on('disconnected', (ev) => {
      if (s.m) s.m.consoleFeed = false;
      if (ev.deliberate) sys(s, 'Admin console closed by Steward (the game saves and shuts down when its last console client leaves).');
      else if (ev.serverSaidBye) sys(s, 'Admin console closed by the game.');
      else if (s.m && s.m.isRunning()) sys(s, `Admin console connection lost${ev.error ? ` (${ev.error})` : ''}. The game shuts down when its last console client leaves, so the server is probably stopping; Steward is reconnecting in case it is not.`);
    });
  }

  // Lines answering the Court's own roster refresh (every 20 s while the Court is open) are not
  // copied into the server log; a /list typed by the owner still is.
  function isRosterPoll(s, con) {
    const job = con.inflight;
    return !!(job && job.sent && s.pollIds && s.pollIds.has(job.line) && s.polling > 0);
  }

  async function cportUsable(id, s) {
    if (!prefs.enabled(id)) return { ok: false, why: 'the live console is turned off for this server (Court screen)' };
    if (s.cportOff) return { ok: false, why: s.cportOff };
    const port = cportFor(id);
    const probe = await N.probePort(port, 'tcp');
    if (probe === 'in-use') return { ok: false, why: `TCP ${port} is already in use by another program` };
    return { ok: true, port };
  }

  // Called for every ServerManager main.js creates.
  function adopt(id, m) {
    if (!ID_RE.test(id) || m.__court) return;
    m.__court = true;
    const s = st(id);
    s.m = m;
    const origStart = m.start.bind(m);
    const origStop = m.stop.bind(m);
    const origSend = m.sendCommand.bind(m);

    m.start = async (root, exeName, opts = {}) => {
      if (s.con) {
        s.con.close();
        s.con = null;
      }
      clearTimeout(s.stopTimer);
      s.cport = null;
      s.players = null;
      s.playersAt = null;
      let extraArgs = opts.extraArgs || [];
      let use = null;
      if (exeName === 'ROK') {
        use = await cportUsable(id, s);
        if (use.ok) extraArgs = [...extraArgs, '-cport', String(use.port)];
      }
      const res = await origStart(root, exeName, { ...opts, extraArgs });
      if (exeName === 'ROK' && use && use.ok && m.isRunning()) {
        s.cport = use.port;
        s.spawnedAt = Date.now();
        const con = new AC.AdminConsole({ port: use.port });
        s.con = con;
        wireConsole(id, s, con);
        con.attach();
        m.log('sys', `Live console: connecting to the game's admin console on 127.0.0.1:${use.port} (-cport). Steward holds this connection while the server runs.`);
      } else if (exeName === 'ROK' && use && !use.ok) {
        m.log('sys', `Live console off for this run: ${use.why}. Commands go to the server's standard input, which the game may ignore.`);
      }
      return res;
    };

    m.stop = () => {
      const con = s.con;
      if (con && con.isConnected() && m.isRunning() && m.state !== 'stopping') {
        m.log('sys', 'Sending /shutdown over the admin console (the game saves, then exits).');
        con.send('/shutdown', { timeoutMs: 15000 }).catch(() => {});
        clearTimeout(s.stopTimer);
        s.stopTimer = setTimeout(() => {
          if (m.isRunning() && s.con === con && con.isConnected()) {
            m.log('sys', 'The server is still running after /shutdown. Closing the admin console, which also makes the game save and shut down.');
            con.close();
          }
        }, stopCloseAfterMs);
      }
      return origStop();
    };

    m.sendCommand = (text) => {
      const con = s.con;
      if (!con || !con.isConnected()) return origSend(text);
      const input = AC.normalizeInput(text);
      if (!input) return false;
      m.log('sys', `> ${input.line}${input.chat ? '   (chat)' : ''}`);
      return con.send(input.line).then(
        (r) => {
          // r.lines were already written to the log as they arrived (see wireConsole).
          if (r.response) m.log('con', r.response);
          if (!r.lines.length && !r.response) m.log('con', '(done, no output)');
          return { via: 'console', lines: r.lines.length };
        },
        (e) => {
          m.log('sys', `Console: ${e.message}`);
          return { via: 'console', error: e.message };
        }
      );
    };

    m.on('exit', (ex) => {
      clearTimeout(s.stopTimer);
      const con = s.con;
      if (!con) return;
      const never = !con.everConnected;
      const quick = s.spawnedAt && Date.now() - s.spawnedAt < EARLY_EXIT_MS;
      con.detach();
      if (con.socket) con.close(); // the process is gone; nothing left to keep alive
      s.con = null;
      if (never && quick && !(ex && ex.requested)) {
        s.cportOff = 'the game exited before its admin console could be reached, so -cport is off until Steward restarts';
        m.log('sys', 'Live console: the game exited before its admin console answered. With -cport the game shuts itself down when no console client is connected within 10 s, so Steward starts this server WITHOUT -cport for the rest of this session. Check the log above for the real cause; if it is the console, tell us what the log says.');
        log('warn', `[court ${id}] console never connected; cport disabled for the session`);
      }
    });
  }

  // Adopt: hold the admin console of a server Steward did not start. Never closes an open connection
  // (that would stop the server); refuses when one is already held.
  function attachConsole(id, port) {
    if (!ID_RE.test(id)) throw new TypeError('server id is not one of s1, s2, s3, s4');
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new RangeError('port must be 1..65535');
    const s = st(id);
    if (!s.m || !s.m.isRunning()) throw Object.assign(new Error('Adopt the server first.'), { friendly: true });
    if (s.con && s.con.isConnected()) throw Object.assign(new Error('The live console is already connected to this server.'), { friendly: true });
    if (s.con) s.con.detach();
    clearTimeout(s.stopTimer);
    s.cport = port;
    s.spawnedAt = null; // not our spawn: the "never reached the console" rule does not apply
    s.players = null;
    s.playersAt = null;
    const con = new AC.AdminConsole({ port });
    s.con = con;
    wireConsole(id, s, con);
    con.attach();
    s.m.log('sys', `Live console: connecting to the adopted server's admin console on 127.0.0.1:${port}. Steward holds this connection from now on; the game shuts down (and saves) when its last console client leaves, so closing Steward stops this server too.`);
    return status(id);
  }

  function conOf(id) {
    const s = st(id);
    if (!s.con || !s.con.isConnected()) {
      const e = new Error(s.m && s.m.isRunning()
        ? 'The live console is not connected to this server. Start the server with ROK.exe (Settings) and the live console on, then try again.'
        : 'This server is not running.');
      e.friendly = true;
      throw e;
    }
    return s.con;
  }

  async function pluginInstalled(id) {
    try {
      const root = rootOf(id);
      if (!root) return false;
      await fsp.access(path.join(root, 'oxide', 'plugins', 'RealmCourt.cs'));
      return true;
    } catch {
      return false;
    }
  }

  async function refreshPlayers(id) {
    const s = st(id);
    const con = conOf(id);
    let players = null;
    let source = 'list';
    s.polling = (s.polling || 0) + 1;
    s.pollIds = s.pollIds || new Set([MOD.build('list').command, MOD.build('roster').command]);
    try {
      if (await pluginInstalled(id)) {
        const r = await con.send(MOD.build('roster').command);
        const roster = MOD.parseRoster(r.lines);
        if (roster) {
          players = roster.players.map((p) => ({ name: p.name, id: p.id }));
          source = 'RealmCourt';
        }
      }
      if (!players) {
        const r = await con.send(MOD.build('list').command);
        const list = MOD.parsePlayerList(r.lines);
        if (!list) throw Object.assign(new Error('The game did not return a player list.'), { friendly: true });
        players = list.names.map((name) => ({ name, id: null }));
      }
    } finally {
      s.polling--;
    }
    s.players = players;
    s.playersAt = new Date().toISOString();
    return { players, source, at: s.playersAt };
  }

  async function status(id) {
    const s = st(id);
    const running = !!(s.m && s.m.isRunning());
    return {
      id,
      running,
      exe: settings.get('serverExe'),
      enabled: prefs.enabled(id),
      cport: s.cport,
      plannedCport: cportFor(id),
      cportOff: s.cportOff,
      console: s.con ? s.con.status() : null,
      players: s.players,
      playersAt: s.playersAt,
      whitelist: s.whitelist,
      plugin: await pluginInstalled(id)
    };
  }

  async function act(id, action, args) {
    if (typeof action !== 'string' || !MOD.ACTIONS.includes(action)) throw Object.assign(new Error('Unknown Court action.'), { friendly: true });
    const s = st(id);
    let built;
    try {
      built = MOD.build(action, args);
    } catch (e) {
      e.friendly = true;
      throw e;
    }
    const con = conOf(id);
    let r;
    let ok = true;
    let result = '';
    try {
      r = await con.send(built.command, { timeoutMs: action === 'save' ? 60000 : undefined });
      result = r.lines.map((l) => l.text).join('\n') || r.response || '';
      if (MOD.unknownCommand(r.lines)) {
        ok = false;
        if (built.needsPlugin) result = `The ${built.needsPlugin} plugin is not loaded on this server. Use "Update plugins" on the Servers screen, then try again.\n${result}`;
      } else if (r.lines.some((l) => l.level === 'E')) ok = false;
    } catch (e) {
      ok = false;
      result = e.message;
    }
    if (action === 'whitelist' && ok) s.whitelist = !!(args && args.on);
    if (['kick', 'ban'].includes(action) && ok) s.playersAt = null;
    const target = args && (args.name || args.plugin || null);
    const reason = args && (args.reason || args.message || null);
    if (!['list', 'banlist', 'roster'].includes(action)) {
      await courtLog.append({ server: id, action, target, reason, command: built.command, ok, result }).catch((e) => log('warn', `[court] log write failed: ${e.message}`));
    }
    if (s.m) s.m.log('sys', `Court: ${built.summary}${ok ? '' : ' (failed)'}`);
    const out = { ok, summary: built.summary, command: built.command, result, lines: r ? r.lines.map((l) => ({ level: l.level, text: l.text })) : [] };
    if (action === 'list') out.parsed = MOD.parsePlayerList(r ? r.lines : []);
    if (action === 'banlist') out.parsed = MOD.parseBanList(r ? r.lines : []);
    if (action === 'roster') out.parsed = MOD.parseRoster(r ? r.lines : []);
    return out;
  }

  function feed(id, since) {
    const s = st(id);
    const n = Number.isInteger(since) && since >= 0 ? since : 0;
    return { lines: s.feed.filter((l) => l.n > n).slice(-500), last: s.seq };
  }

  function register(handle) {
    const idOf = (id) => instOf(id === undefined ? 's1' : id).id;
    handle('court:status', (id) => status(idOf(id)));
    handle('court:act', (id, action, args) => act(idOf(id), action, args && typeof args === 'object' ? args : {}));
    handle('court:players', (id) => refreshPlayers(idOf(id)));
    handle('court:feed', (id, since) => feed(idOf(id), since));
    handle('court:log', async (id) => courtLog.read({ server: id ? idOf(id) : null, limit: 300 }));
    handle('court:setConsole', async (id, enabled) => {
      const sid = idOf(id);
      const on = await prefs.set(sid, enabled === true);
      if (on) st(sid).cportOff = null;
      return status(sid);
    });
    handle('court:pluginCommands', () => MOD.PLUGIN_ADMIN.map((d, i) => ({ index: i, ...d })));
    handle('court:copyPluginCommand', (index, values) => {
      if (!Number.isInteger(index)) throw new TypeError('index must be a number');
      let text;
      try {
        text = MOD.pluginCommand(index, values && typeof values === 'object' ? values : {});
      } catch (e) {
        e.friendly = true;
        throw e;
      }
      if (clipboard) clipboard.writeText(text);
      return text;
    });
    handle('court:openLog', async () => {
      await fsp.mkdir(dir, { recursive: true });
      if (shell) await shell.openPath(dir);
      return dir;
    });
  }

  // True when commands for this server go straight to the game over the admin console.
  function consoleReady(id) {
    const s = state.get(id);
    return !!(s && s.con && s.con.isConnected() && s.m && s.m.isRunning());
  }

  // Window closing: nothing special. m.stop() (wrapped above) already sends /shutdown.
  return { adopt, attachConsole, register, status, act, feed, refreshPlayers, consoleReady, prefs, courtLog, dir, _state: state, platform };
}

module.exports = { createCourt, STOP_CLOSE_AFTER_MS };
