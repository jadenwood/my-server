'use strict';

// Connection Doctor, steward edition: a step-by-step check of one server instance, each step with
// a verdict and one concrete fix, plus the IPC that the Doctor view uses.
//
// Everything here is READ-ONLY: it lists processes, sockets and firewall rules, sends one A2S
// status query, reads log and config files. It never changes the firewall, the router, a config
// file or a game file. PowerShell is used only for Get-NetUDPEndpoint / Get-NetTCPConnection /
// Get-Process / Get-NetFirewallRule, with scripts built from validated integers and fixed names.
//
// runChecks() takes every outside fact through `deps`, so tests run it with fakes.

const path = require('path');
const fsp = require('fs/promises');
const { execFile } = require('child_process');
const GL = require('./shared/gamelog');
const ST = require('./shared/steam');
const A2S = require('./shared/a2s');
const N = require('./netcheck');
const FW = require('./firewall');
const S = require('./safety');
const GF = require('./gamefiles');

const ADMIN_PORTS = [11000, 11001, 11002, 11003];

// ---------------------------------------------------------------- sockets (Windows, read-only)

function checkPorts(ports) {
  const list = [...new Set(ports)];
  for (const p of list) if (!Number.isInteger(p) || p < 1 || p > 65535) throw new RangeError(`bad port ${p}`);
  return list;
}

// One "proto|address|port|pid|process" line per socket bound to one of `ports`.
function listenerScript(ports) {
  const list = checkPorts(ports).join(',');
  return [
    "$ErrorActionPreference = 'SilentlyContinue'",
    '$names = @{}; Get-Process | ForEach-Object { $names[[int]$_.Id] = $_.ProcessName }',
    `Get-NetUDPEndpoint -LocalPort ${list} | ForEach-Object { 'udp|' + $_.LocalAddress + '|' + $_.LocalPort + '|' + $_.OwningProcess + '|' + $names[[int]$_.OwningProcess] }`,
    `Get-NetTCPConnection -State Listen -LocalPort ${list} | ForEach-Object { 'tcp|' + $_.LocalAddress + '|' + $_.LocalPort + '|' + $_.OwningProcess + '|' + $names[[int]$_.OwningProcess] }`
  ].join('\r\n');
}

function parseListeners(stdout) {
  const out = [];
  for (const line of String(stdout || '').split(/\r?\n/)) {
    const [proto, address, port, pid, name] = line.trim().split('|');
    if (!/^(udp|tcp)$/.test(proto || '') || !/^\d+$/.test(port || '')) continue;
    out.push({ proto, address: address || '', port: Number(port), pid: /^\d+$/.test(pid || '') ? Number(pid) : null, process: (name || '').slice(0, 64) });
  }
  return out;
}

function powershell(script, timeout = 20000) {
  return new Promise((resolve) => {
    execFile('powershell.exe', FW.plainArgs(script), { windowsHide: true, timeout }, (err, stdout) => resolve(err && !stdout ? null : String(stdout || '')));
  });
}

// { supported, list } ; elsewhere than Windows, falls back to "is the port bound" via a test bind.
async function listeners(ports, { platform = process.platform } = {}) {
  if (platform === 'win32') {
    const out = await powershell(listenerScript(ports));
    if (out == null) return { supported: false, list: [], error: 'PowerShell did not answer' };
    return { supported: true, list: parseListeners(out) };
  }
  const list = [];
  for (const p of checkPorts(ports)) {
    if ((await N.probePort(p, 'udp')) === 'in-use') list.push({ proto: 'udp', address: '?', port: p, pid: null, process: '' });
    if ((await N.probePort(p, 'tcp')) === 'in-use') list.push({ proto: 'tcp', address: '?', port: p, pid: null, process: '' });
  }
  return { supported: true, approximate: true, list };
}

async function firewallRules(id, { platform = process.platform } = {}) {
  if (platform !== 'win32') return { supported: false, present: [] };
  const out = await powershell(FW.listScript(id));
  return { supported: out != null, present: FW.parseList(out) };
}

// ---------------------------------------------------------------- config + logs

async function readCfg(root) {
  try {
    const { text } = S.decodeText(await fsp.readFile(path.join(root, 'Configuration', 'ServerSettings.cfg')));
    const get = (k) => S.getCfgValue(text, k);
    const num = (k) => (/^\d+$/.test(get(k) || '') ? Number(get(k)) : null);
    return {
      exists: true,
      bindIP: get('bindIP'),
      portNumber: num('portNumber'),
      pingPort: num('pingPort'),
      steamAuthPort: num('steamAuthPort'),
      enablePingLimit: /^true$/i.test(get('enablePingLimit') || ''),
      hasPassword: !!(get('password') || '').length
    };
  } catch {
    return { exists: false };
  }
}

// ---------------------------------------------------------------- the checks

const step = (id, label, status, detail, fix = '') => ({ id, label, status, detail, fix });

// What a player types, by where they are. The address box takes the address ONLY; the port has
// its own box ([DEC] Game.Join resolves the whole text as a host name).
function addressAdvice({ network, port, lan = [], publicIp = null }) {
  const out = [{ who: 'On this PC', address: '127.0.0.1', port }];
  if (network === 'public') {
    for (const l of lan.slice(0, 2)) out.push({ who: `Same home network (${l.name})`, address: l.address, port });
    if (publicIp) out.push({ who: 'Over the internet', address: publicIp, port });
  }
  return out;
}

function sockFor(list, proto, port) {
  return list.filter((l) => l.proto === proto && l.port === port);
}

// input: { inst:{id,ports,network}, name, platform, server:{running,pid,exe,ready,steam,listening,externals[]},
//          cfg, serverScan:{findings}, clientScan:{findings}|null, clientLogs:[...], lan:[] }
// deps:  { listeners(ports), a2s(host,port), steam(), game(), firewall(), tcp(host,port), gameFiles() }
async function runChecks(input, deps) {
  const { inst, server = {}, cfg = { exists: false }, platform = process.platform } = input;
  const n = inst.id.slice(1);
  const game = inst.ports.game;
  const ping = cfg.pingPort || game;
  const query = inst.ports.query;
  const steps = [];
  const running = !!server.running || (server.externals || []).length > 0;

  // 0. game files: a server copy missing a DLL that Assembly-CSharp.dll references stops at start-up
  // (TypeInitializationException for EventManager), so this comes first and wins the verdict.
  if (deps.gameFiles) {
    let gf;
    try {
      gf = await deps.gameFiles();
    } catch (e) {
      gf = { status: 'skip', detail: `Could not check: ${e.message}`, fix: '' };
    }
    steps.push(step('game-files', 'Server game files', gf.status, gf.detail, gf.fix || ''));
  }

  // 1. process
  if (server.running) steps.push(step('process', 'Server process', 'ok', `Server ${n} (${input.name || inst.id}) runs as ${server.exe || 'ROK'}.exe, pid ${server.pid}, started by Realm.`));
  else if ((server.externals || []).length) steps.push(step('process', 'Server process', 'warn', `Server ${n} runs outside Realm: ${server.externals.map((p) => `${p.name}.exe pid ${p.pid}`).join(', ')}. Realm cannot see its console.`, 'Stop it (type quit in its window) and start it from Realm\'s Servers screen so its log and restarts are handled.'));
  else steps.push(step('process', 'Server process', 'bad', `Server ${n} is not running.`, `Open Servers, pick Server ${n} and press Start. Then run the Doctor again.`));

  // 2. log ready
  const sf = (input.serverScan && input.serverScan.findings) || [];
  const ready = sf.find((f) => f.id === 'server-ready');
  const blocker = GL.verdict(sf.filter((f) => ['first-run-exit', 'game-port-taken', 'server-start-error', 'version-mismatch', 'missing-game-files'].includes(f.id) && (!ready || f.line > ready.line)));
  if (blocker) steps.push(step('ready', 'Server log', 'bad', `${blocker.title}: "${blocker.match}"`, blocker.fix));
  else if (ready && running) steps.push(step('ready', 'Server log', 'ok', `"${ready.match}"`));
  else if (running) steps.push(step('ready', 'Server log', 'warn', 'The log does not show "Server for N players started on port P." yet. The world may still be loading (1 to 3 minutes).', 'Wait, then run the checks again. If it never appears, open the server log below and look for red lines.'));
  else steps.push(step('ready', 'Server log', 'skip', ready ? `Last run reached "${ready.match}"` : 'No server log lines yet.'));
  if (ready && cfg.portNumber && Number(/port (\d+)/.exec(ready.match)?.[1]) !== game) {
    steps.push(step('port-match', 'Port in the log', 'warn', `The server says port ${/port (\d+)/.exec(ready.match)[1]}, but Realm expects ${game}.`, 'Restart the server from Realm so it writes the right portNumber into ServerSettings.cfg.'));
  }
  const steamFail = sf.find((f) => f.id === 'steam-server-failed');
  if (steamFail && running) steps.push(step('steam-server', 'Steam registration', 'warn', `"${steamFail.match}"`, steamFail.fix));

  // 3 + 4. sockets
  let socks = { supported: false, list: [] };
  if (running) {
    try {
      socks = await deps.listeners([game, ping, query, ...ADMIN_PORTS]);
    } catch (e) {
      socks = { supported: false, list: [], error: e.message };
    }
    const note = socks.approximate ? ' (checked by a test bind; owner process unknown on this OS)' : '';
    const who = (l) => (l.pid ? ` by ${l.process || 'pid'}${l.process ? '.exe' : ''} (pid ${l.pid})` : '');
    const on = (ls) => (ls.every((l) => l.address === '?') ? 'In use' : `Open on ${ls.map((l) => l.address).join(', ')}`);
    if (!socks.supported) {
      steps.push(step('udp', `UDP ${game} (game)`, 'skip', `Could not list sockets: ${socks.error || 'not supported'}.`));
    } else {
      const u = sockFor(socks.list, 'udp', game);
      if (u.length) {
        const local = u.every((l) => l.address === '127.0.0.1');
        const st = inst.network === 'public' && local ? 'bad' : 'ok';
        steps.push(step('udp', `UDP ${game} (game)`, st, `${on(u)}${who(u[0])}${note}.`, st === 'bad' ? 'The game socket only listens on 127.0.0.1 although the server is set to Public. Restart it from Realm so bindIP becomes 0.0.0.0.' : ''));
        if (server.pid && u[0].pid && u[0].pid !== server.pid && !/^rok$/i.test(u[0].process)) {
          steps.push(step('udp-owner', 'Port owner', 'warn', `UDP ${game} belongs to ${u[0].process || 'another program'} (pid ${u[0].pid}), not this server.`, 'Close that program or change this server\'s ports.'));
        }
      } else steps.push(step('udp', `UDP ${game} (game)`, 'bad', `Nothing listens on UDP ${game}${note}.`, server.ready ? 'The server runs but has no game socket: check the server log for "already being used".' : 'Wait until the server log shows it is listening, then check again.'));
      const t = sockFor(socks.list, 'tcp', ping);
      if (t.length) steps.push(step('tcp', `TCP ${ping} (ping check)`, 'ok', `${on(t)}${who(t[0])}${note}.`));
      else steps.push(step('tcp', `TCP ${ping} (ping check)`, cfg.enablePingLimit ? 'bad' : 'warn', `Nothing listens on TCP ${ping}${note}.`, cfg.enablePingLimit ? 'With enablePingLimit on, players are kicked when the ping port is unreachable. Wait for the server to finish loading; if it stays closed, set enablePingLimit = \'False\'.' : 'The ping graph will not work; joining is not affected while enablePingLimit is off.'));
    }
  } else {
    steps.push(step('udp', `UDP ${game} (game)`, 'skip', 'Start the server first.'));
    steps.push(step('tcp', `TCP ${ping} (ping check)`, 'skip', 'Start the server first.'));
  }

  // 5. A2S loopback
  if (running) {
    const r = await deps.a2s('127.0.0.1', query);
    if (r.ok) steps.push(step('a2s', `Steam query, UDP ${query}`, 'ok', `Answered on 127.0.0.1 in ${r.rttMs} ms as "${r.info.name}" (the game always reports that name).`));
    else steps.push(step('a2s', `Steam query, UDP ${query}`, 'warn', `No answer on 127.0.0.1:${query} (${r.error}).`, steamFail ? steamFail.fix : 'Players can still join by address; only the online/ping display in the Realm app needs this. If the server log shows a Steam error, give each server its own steamAuthPort and restart. UNVERIFIED that the game answers A2S at all.'));
  } else steps.push(step('a2s', `Steam query, UDP ${query}`, 'skip', 'Start the server first.'));

  // 6. Steam client
  const steam = await deps.steam();
  if (steam.running === true && steam.signedIn !== false) steps.push(step('steam', 'Steam client', 'ok', `Steam is running${steam.signedIn ? ' and signed in' : ''}.`));
  else if (steam.running === true) steps.push(step('steam', 'Steam client', 'bad', 'Steam is running but nobody is signed in.', 'Sign in to Steam (not offline mode). The game needs it to create its join ticket.'));
  else if (steam.running === false) steps.push(step('steam', 'Steam client', 'bad', 'Steam is not running on this PC.', 'Start Steam and sign in, then start the game from Steam.'));
  else steps.push(step('steam', 'Steam client', 'skip', 'Only checked on Windows.'));

  // 7. game installed
  const g = await deps.game();
  if (g.installed === true) steps.push(step('game', 'Reign of Kings installed', 'ok', `Found in ${g.library}.`));
  else if (g.installed === false) steps.push(step('game', 'Reign of Kings installed', g.steam === false ? 'bad' : 'warn', g.steam === false ? 'Steam is not installed for this Windows user.' : 'The game (app 344760) is not installed in any Steam library on this PC.', 'Install Reign of Kings in Steam to play on this PC. The server does not need it.'));
  else steps.push(step('game', 'Reign of Kings installed', 'skip', 'Only checked on Windows.'));

  // 8. address
  const adv = addressAdvice({ network: inst.network, port: game, lan: input.lan || [], publicIp: server.steam && server.steam.publicIp });
  steps.push(step('address', 'What to type in the game', 'info', adv.map((a) => `${a.who}: address ${a.address}, port ${a.port}`).join(' | ') + '. Address and port go in separate boxes: never "address:port".', ''));
  if (cfg.exists && inst.network === 'local' && cfg.bindIP && cfg.bindIP !== '127.0.0.1') steps.push(step('bind', 'Network setting', 'warn', `bindIP is '${cfg.bindIP}' but the server is set to This PC only.`, 'Restart the server from Realm to apply the setting.'));
  if (cfg.exists && inst.network === 'public' && cfg.bindIP === '127.0.0.1') steps.push(step('bind', 'Network setting', 'bad', "bindIP is '127.0.0.1': only this PC can join.", 'Restart the server from Realm (Go Public saved) so bindIP becomes 0.0.0.0.'));
  if (cfg.hasPassword) steps.push(step('password', 'Server password', 'info', 'This server has a password. Players get "Incorrect password." if it does not match exactly.'));

  // 9. firewall
  const fw = await deps.firewall();
  if (!fw.supported) steps.push(step('firewall', 'Firewall rules', 'skip', 'Only checked on Windows.'));
  else if (fw.present.length >= 4) steps.push(step('firewall', 'Firewall rules', fw.present.every((r) => r.enabled) ? 'ok' : 'warn', `${fw.present.length} Realm rules found${fw.present.every((r) => r.enabled) ? '' : ', but some are disabled'}.`, fw.present.every((r) => r.enabled) ? '' : 'Open Go Public and press Add firewall rules again.'));
  else if (inst.network === 'public') steps.push(step('firewall', 'Firewall rules', 'bad', `${fw.present.length} of 4 Realm rules found. Windows Firewall may drop players from other PCs.`, 'Open Go Public and press Add firewall rules (one Windows permission prompt).'));
  else steps.push(step('firewall', 'Firewall rules', 'info', 'No Realm rules. Not needed while the server is for this PC only.'));

  // 10. admin console exposure
  if (socks.supported) {
    const admin = socks.list.filter((l) => l.proto === 'tcp' && ADMIN_PORTS.includes(l.port));
    const open = admin.filter((l) => l.address !== '127.0.0.1' && l.address !== '::1');
    const blocked = fw.present.some((r) => /admin console BLOCK/.test(r.name) && r.enabled);
    if (open.length && !blocked && inst.network === 'public') steps.push(step('admin', 'Admin console port', 'bad', `TCP ${open[0].port} is open on ${open[0].address}: the game's admin console accepts commands from anyone who reaches it.`, 'Add the Realm firewall rules (they block TCP 11000-11003) and never forward these ports.'));
    else if (open.length) steps.push(step('admin', 'Admin console port', 'info', `TCP ${open[0].port} is open${blocked ? ' and blocked inbound by the Realm rule' : ''}. Never forward it.`));
  }

  // 11. public reachability
  if (inst.network === 'public') {
    const ip = server.steam && server.steam.publicIp;
    if (!ip) steps.push(step('public', 'Public address', running ? 'warn' : 'skip', running ? 'The server has not logged "Steam game server started" yet, so its public address is unknown.' : 'Start the server first.', running ? 'Wait a minute. If it never appears, see the Steam registration step.' : ''));
    else if (N.isCgnat(ip)) steps.push(step('public', 'Public address', 'bad', `${ip} is a carrier-grade NAT address: port forwarding cannot work on this connection.`, 'Ask your internet provider for a public IPv4 address, or host on a VPS.'));
    else if (N.isPrivateIPv4(ip)) steps.push(step('public', 'Public address', 'warn', `Steam sees ${ip}, a private address.`, 'There may be two routers in a row. Forward the ports on both, or put the first one in bridge mode.'));
    else {
      const t = await deps.tcp(ip, ping);
      if (t.ok) steps.push(step('public', 'Public address', 'ok', `Steam sees ${ip}; TCP ${ping} answered through it.`));
      else steps.push(step('public', 'Public address', 'warn', `Steam sees ${ip}; TCP ${ping} did not answer through it from this PC (${t.error}).`, `Forward UDP ${game}, TCP ${ping} and UDP ${query} to this PC in the router. Many routers cannot loop back, so the final test is a friend outside your network.`));
    }
  }

  // 12. client game log
  const cf = input.clientScan && input.clientScan.findings;
  if (cf) {
    const v = GL.verdict(cf, { resolvedBy: ['connecting'] });
    const ok = cf.find((f) => f.id === 'connecting');
    if (v) steps.push(step('client-log', 'Your game log', v.severity, `${v.title}: "${v.match}"${v.time ? ` (${v.time})` : ''}`, v.fix));
    else if (ok) steps.push(step('client-log', 'Your game log', 'info', `The last join got as far as "${ok.match}"${ok.time ? ` (${ok.time})` : ''}. Refusals after this point (password, full, banned, version, timeout) are shown only in a game popup, not in the log: paste that message below.`));
    else steps.push(step('client-log', 'Your game log', 'info', (input.clientLogs || []).length ? 'No known error in the game log.' : 'No game log found yet. It appears after the game has been started once.'));
  }

  return { at: new Date().toISOString(), verdict: pickVerdict(steps), steps };
}

function pickVerdict(steps) {
  const bad = steps.find((s) => s.status === 'bad');
  if (bad) return { status: 'bad', title: `${bad.label}: ${bad.detail}`, fix: bad.fix };
  const warn = steps.find((s) => s.status === 'warn');
  if (warn) return { status: 'warn', title: `${warn.label}: ${warn.detail}`, fix: warn.fix };
  return { status: 'ok', title: 'No problem found. Players should be able to join with the address shown.', fix: '' };
}

// ---------------------------------------------------------------- IPC (steward)

// ctx: { handle, app, clipboard, fleet, instOf, mgr, rootCheckFor, externalProcesses, gameInstall, steamServer }
function registerSteward(ctx) {
  let allowed = [];
  let lastReport = null;

  async function gameState() {
    const install = await ctx.gameInstall();
    // Development only (automated screenshots on Linux): a folder that imitates the game install.
    if (ctx.devGameDir) return { install: { steam: true, installed: true, library: path.dirname(ctx.devGameDir) }, dir: ctx.devGameDir };
    const dir = await GL.findGameDir(install, { parseVdf: ST.parseVdf });
    return { install, dir };
  }

  async function logsFor(inst) {
    const c = ctx.rootCheckFor(inst, null);
    const root = c.ok ? c.root : inst.root;
    const { install, dir } = await gameState();
    const server = await GL.resolveLogs(GL.serverLogCandidates(root));
    const client = await GL.resolveLogs(GL.clientLogCandidates(dir, ctx.devGameDir ? { P: path } : undefined));
    allowed = [...client, ...server];
    return { root, install, gameDir: dir, client, server };
  }

  async function scanFile(entry) {
    try {
      const r = await GL.readFrom(entry.path, null, { tail: 256 * 1024 });
      return GL.scan(r.text, { source: entry.kind === 'client' ? 'game' : 'server' });
    } catch {
      return { lines: [], findings: [] };
    }
  }

  ctx.handle('doctor:run', async (id) => {
    const inst = ctx.instOf(id);
    const m = ctx.mgr(inst.id);
    const logs = await logsFor(inst);
    const serverScans = await Promise.all(logs.server.map(scanFile));
    // The manager's own console lines (stdout or the tail it already follows) count too.
    serverScans.push(GL.scan(m.getLines(0), { source: 'console' }));
    const merge = (scans) => {
      let line = 0;
      const all = [];
      for (const s of scans) {
        for (const f of s.findings) all.push({ ...f, line: f.line + line });
        line += 1e6;
      }
      return { findings: all.sort((a, b) => b.line - a.line) };
    };
    const clientScans = await Promise.all(logs.client.map(scanFile));
    const st = m.status();
    const ext = await ctx.externalProcesses(inst.id, logs.root, true).catch(() => []);
    const result = await runChecks(
      {
        inst,
        name: inst.id,
        platform: process.platform,
        server: { running: m.isRunning(), pid: st.pid, exe: st.exe, ready: st.ready, steam: st.steam, listening: st.listening, externals: ext },
        cfg: await readCfg(logs.root),
        serverScan: merge(serverScans),
        clientScan: merge(clientScans),
        clientLogs: logs.client,
        lan: N.lanAddresses()
      },
      {
        listeners: (ports) => listeners(ports),
        a2s: (h, p) => A2S.queryInfo(h, p, { timeoutMs: 1500 }),
        steam: () => ST.steamState(),
        game: async () => logs.install,
        firewall: () => firewallRules(inst.id),
        tcp: (h, p) => N.tcpConnect(h, p, 2500),
        gameFiles: async () => GF.checkGameFiles(logs.root, { steamRoot: ctx.steamServer ? await ctx.steamServer().catch(() => null) : null })
      }
    );
    const findings = merge([...clientScans, ...serverScans]).findings;
    lastReport = {
      app: 'Realm Steward',
      version: ctx.app.getVersion(),
      target: `Server ${inst.id.slice(1)} (${inst.network}, game ${inst.ports.game}, query ${inst.ports.query})`,
      verdict: result.verdict,
      steps: result.steps,
      findings,
      logs: [],
      publicIps: st.steam && st.steam.publicIp ? [st.steam.publicIp] : []
    };
    return {
      ...result,
      findings,
      logs: allowed.map((l, i) => ({ index: i, kind: l.kind, label: l.label, evidence: l.evidence, size: l.size, mtime: new Date(l.mtimeMs).toISOString(), path: l.path })),
      gameDir: logs.gameDir
    };
  });

  // Live log tail: the renderer passes the index from doctor:run and its last offset.
  ctx.handle('doctor:log', async (req) => {
    const index = req && Number.isInteger(req.index) ? req.index : -1;
    const entry = allowed[index];
    if (!entry) throw Object.assign(new Error('Run the checks first, then pick a log.'), { friendly: true });
    const offset = req.offset == null ? null : Number.isInteger(req.offset) && req.offset >= 0 ? req.offset : null;
    const r = await GL.readFrom(entry.path, offset);
    const s = GL.scan(r.text, { source: entry.kind, keep: 2000 });
    return { index, offset: r.offset, size: r.size, reset: r.reset, lines: s.lines, findings: s.findings };
  });

  ctx.handle('doctor:classify', (text) => {
    const t = S.asString(text, 4000, 'message');
    const findings = GL.scan(t, { source: 'pasted' }).findings;
    return { findings, verdict: GL.verdict(findings) };
  });

  ctx.handle('doctor:copyReport', async (extra) => {
    if (!lastReport) throw Object.assign(new Error('Run the checks first.'), { friendly: true });
    const pasted = extra && typeof extra.pasted === 'string' ? extra.pasted.slice(0, 1000) : '';
    const logLines = [];
    if (extra && Number.isInteger(extra.logIndex) && allowed[extra.logIndex]) {
      const entry = allowed[extra.logIndex];
      try {
        const r = await GL.readFrom(entry.path, null, { tail: 32 * 1024 });
        logLines.push({ label: entry.label, lines: r.text.split(/\r?\n/).filter((l) => l.trim()) });
      } catch {
        /* unreadable: leave it out */
      }
    }
    const text = GL.buildReport({ ...lastReport, platform: process.platform, logs: logLines, pasted }, { publicIps: lastReport.publicIps });
    ctx.clipboard.writeText(text);
    return { chars: text.length, text };
  });
}

module.exports = { ADMIN_PORTS, listenerScript, parseListeners, listeners, firewallRules, readCfg, addressAdvice, runChecks, pickVerdict, registerSteward };
