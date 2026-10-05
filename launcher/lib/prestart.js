'use strict';

// Before Start: is anything already holding this server's ports, or running from its folder? If so,
// what is it, and what can Steward safely do about it? (docs/worlds.md, docs/HANDOFF.md "Real
// problems hit".) Steward never starts into a port clash, never kills a process it did not start,
// and never opens a connection to a game's admin console just to look: the game saves and shuts down
// when its LAST console client disconnects, so a "probe" connection would stop a running server.
//
// What can hold the game port (UDP/TCP 7350 by default), as seen on the owner's PC (2026-10-02):
// - leftover:    a ROK.exe server from THIS folder that Steward is not tracking (a double start, or a
//                server started outside Steward). Offers: Stop it cleanly (/shutdown over its admin
//                console), Adopt it.
// - watchdog:    the game's Server.exe. Started once as administrator, it relaunches ROK.exe about 2 s
//                after it ends, so a stopped server comes straight back. Steward cannot end an
//                elevated process and does not try: the owner closes Server.exe first.
// - game-client: the Reign of Kings game itself (ROK.exe is both the game and the server).
// - other-server: a ROK.exe running from another folder (another server copy).
// - hidden:      a ROK.exe whose path Windows will not show (it runs as administrator, or under Easy
//                Anti-Cheat) and nothing else tells which one it is.
// - unknown:     any other program.

const N = require('./netcheck');
const S = require('./safety');
const AC = require('./admin-console');

const CONSOLE_PORTS = [11000, 11001, 11002, 11003];
const GAME_DIR_RE = /[\\/]steamapps[\\/]common[\\/]reign of kings[\\/]/i;

const isRok = (p) => /^rok$/i.test(p.name);
const isServerExe = (p) => /^server$/i.test(p.name);
const inside = (p, root) => !!(p && p.path && root && S.isInside(p.path, root, 'win32'));

// Pure: from the facts gathered by survey(), decide what each process is and what Steward offers.
// facts: { root, gameDir, busy: [{proto, port, what}], portHolders: [{pid,name,path}],
//          procs: [{pid,name,path}] (every Server.exe / ROK.exe), consoles: [{port, pids: []}],
//          heldSlots: [n], plannedCport }
function classify(facts) {
  const root = facts.root;
  const procs = facts.procs || [];
  const portHolders = facts.portHolders || [];
  const consoles = facts.consoles || [];
  const held = facts.heldSlots || [];
  const gameDir = facts.gameDir ? String(facts.gameDir).toLowerCase() : null;
  const watchdogs = procs.filter((p) => isServerExe(p) && (!p.path || inside(p, root)));
  const byPid = new Map();
  const add = (p, why) => {
    const cur = byPid.get(p.pid) || { pid: p.pid, name: p.name, path: p.path || '', ports: [], why: [] };
    if (why) cur.why.push(why);
    byPid.set(p.pid, cur);
    return cur;
  };
  for (const b of facts.busy || []) {
    for (const h of portHolders.filter((x) => x.port === b.port && x.proto === b.proto)) add(h).ports.push(`${b.proto.toUpperCase()} ${b.port}`);
  }
  for (const p of procs) if (isRok(p) && inside(p, root)) add(p, 'runs from this server folder');
  for (const w of watchdogs) add(w, 'the game\'s Server.exe watchdog');

  const consoleOf = (pid) => {
    const c = consoles.find((x) => (x.pids || []).includes(pid));
    return c ? c.port : null;
  };
  const holders = [];
  for (const h of byPid.values()) {
    let kind;
    const lower = (h.path || '').toLowerCase();
    if (isServerExe(h)) kind = 'watchdog';
    else if (!isRok(h)) kind = 'unknown';
    else if (inside(h, root)) kind = watchdogs.length ? 'watchdog' : 'leftover';
    else if (h.path && ((gameDir && lower.startsWith(gameDir)) || GAME_DIR_RE.test(h.path))) kind = 'game-client';
    else if (h.path) kind = 'other-server';
    else if (watchdogs.length) kind = 'watchdog';
    else if (held.length) kind = 'leftover';
    else kind = 'hidden';
    holders.push({ ...h, kind, consolePort: consoleOf(h.pid) });
  }

  const order = ['watchdog', 'leftover', 'game-client', 'other-server', 'hidden', 'unknown'];
  let verdict = 'clear';
  if (holders.length) verdict = order.find((k) => holders.some((h) => h.kind === k));
  else if ((facts.busy || []).length) verdict = held.length ? 'leftover' : 'unknown';
  else if (held.length) verdict = 'leftover';

  // A console port answering at all (bound) while no holder could be matched to it: on Windows the
  // owner can be read; elsewhere (tests) facts.consoles may carry no pids.
  const anyConsole = consoles.filter((c) => c.inUse);
  const leftovers = holders.filter((h) => h.kind === 'leftover');
  const rokLeft = leftovers.filter((h) => isRok(h));
  let consolePort = null;
  if (rokLeft.length === 1 && rokLeft[0].consolePort) consolePort = rokLeft[0].consolePort;
  else if (verdict === 'leftover') {
    // No pid match: the server's planned port first, then a single bound console port.
    const planned = anyConsole.find((c) => c.port === facts.plannedCport);
    if (planned) consolePort = planned.port;
    else if (anyConsole.length === 1) consolePort = anyConsole[0].port;
  }
  const leftoverPid = rokLeft.length === 1 ? rokLeft[0].pid : null;

  const actions = {
    stop: { ok: false, port: null, why: '' },
    adopt: { ok: false, pid: null, port: null, why: '' }
  };
  if (verdict === 'leftover') {
    if (rokLeft.length > 1) {
      actions.stop.why = actions.adopt.why = `${rokLeft.length} servers are running from this folder. Close all but one in Task Manager > Details first.`;
    } else if (consolePort) {
      actions.stop = { ok: true, port: consolePort, why: `Sends /shutdown to its admin console on 127.0.0.1:${consolePort}: the game saves the world and exits.` };
      if (leftoverPid) actions.adopt = { ok: true, pid: leftoverPid, port: consolePort, why: `Steward connects to its admin console on 127.0.0.1:${consolePort}, follows its log and shows it as running. Nothing is restarted.` };
      else actions.adopt.why = 'Steward cannot see the process id of this server (Windows hides it), so it cannot watch it. Stop it cleanly instead.';
    } else {
      actions.stop.why = actions.adopt.why = 'Its admin console (TCP 11000-11003) is not listening, so there is no clean way in. Close its window, or end ROK.exe in Task Manager > Details (the world is saved only when the game itself shuts down).';
    }
  } else if (verdict === 'watchdog') {
    actions.stop.why = actions.adopt.why = 'The game\'s Server.exe is running. It runs as administrator and starts ROK.exe again about 2 seconds after it ends, so stopping ROK.exe alone does not help. Close the Server.exe window first (or, in Task Manager opened as administrator, end Server.exe first, then ROK.exe). Steward does not end administrator programs.';
  } else if (verdict === 'game-client') {
    actions.stop.why = actions.adopt.why = 'This is the game, not a server. Close Reign of Kings, start the server, then launch the game.';
  } else if (verdict === 'other-server') {
    actions.stop.why = actions.adopt.why = 'This is a server from another folder. Stop it there, or give this server other ports on the Servers screen.';
  } else if (verdict === 'hidden') {
    actions.stop.why = actions.adopt.why = 'Windows hides where this ROK.exe runs from. It is either the game, or a server started by the game\'s Server.exe watchdog. Close the game; or open Task Manager as administrator > Details, end Server.exe first, then ROK.exe.';
  } else if (verdict === 'unknown') {
    actions.stop.why = actions.adopt.why = 'Another program uses this port. Close it, or give this server other ports on the Servers screen.';
  }

  return { verdict, holders, watchdog: watchdogs[0] || null, consolePort, actions, message: describe(verdict, holders, facts) };
}

const KIND_TEXT = {
  leftover: 'a server from this folder that Steward did not start (or lost track of)',
  watchdog: 'the game\'s Server.exe watchdog and the server it keeps restarting',
  'game-client': 'the Reign of Kings game',
  'other-server': 'a server running from another folder',
  hidden: 'a ROK.exe whose folder Windows hides (the game, or a server run by the Server.exe watchdog)',
  unknown: 'another program'
};

function describe(verdict, holders, facts) {
  if (verdict === 'clear') return 'Nothing else is using this server\'s ports or folder.';
  const busy = (facts.busy || []).map((b) => `${b.proto.toUpperCase()} ${b.port} (${b.what})`);
  const who = holders.length
    ? holders.map((h) => `${h.name}.exe (pid ${h.pid}${h.path ? ', ' + h.path : ''})${h.ports.length ? ' on ' + h.ports.join(', ') : ''}`).join('; ')
    : facts.heldSlots && facts.heldSlots.length
      ? `a process holding world ${facts.heldSlots.join(', ')} of this folder`
      : 'a program Steward cannot name';
  const ports = busy.length ? `${busy.join(', ')} ${busy.length === 1 ? 'is' : 'are'} already in use. ` : '';
  return `${ports}Found ${KIND_TEXT[verdict] || 'another program'}: ${who}.`;
}

// Gathers the facts (read-only) and classifies them. deps are injectable for tests:
//   probePort(port, proto) -> 'free'|'in-use'|'unknown'      (lib/netcheck.js: a bind test)
//   portOwners(port, proto) -> [{pid,name,path}]             (Windows; [] elsewhere)
//   listProcesses() -> [{pid,name,path}]                     (every Server.exe / ROK.exe)
//   heldSlots(root) -> [n]                                   (lib/worlds.js: Session.lock held)
//   gameDir: the game install folder, when known
// inst: { id, ports: { game, query, ... } } as lib/fleet.js keeps it; sockets: FL.socketsOf(inst).
async function survey({ root, sockets, plannedCport = null, gameDir = null, ownPids = [], consolePorts = CONSOLE_PORTS }, deps) {
  const probe = deps.probePort || N.probePort;
  const owners = deps.portOwners || N.portOwners;
  const busy = [];
  const portHolders = [];
  for (const sock of sockets || []) {
    if ((await probe(sock.port, sock.proto)) !== 'in-use') continue;
    busy.push(sock);
    for (const o of await owners(sock.port, sock.proto)) portHolders.push({ ...o, port: sock.port, proto: sock.proto });
  }
  const consoles = [];
  for (const port of consolePorts) {
    if ((await probe(port, 'tcp')) !== 'in-use') continue;
    consoles.push({ port, inUse: true, pids: (await owners(port, 'tcp')).map((o) => o.pid) });
  }
  const own = new Set(ownPids.filter(Boolean));
  const procs = ((await (deps.listProcesses ? deps.listProcesses() : [])) || []).filter((p) => !own.has(p.pid));
  const held = deps.heldSlots ? await deps.heldSlots(root) : [];
  const facts = { root, gameDir, busy, portHolders: portHolders.filter((p) => !own.has(p.pid)), procs, consoles, heldSlots: held, plannedCport };
  const r = classify(facts);
  return { clear: r.verdict === 'clear', ...r, busy, consoles: consoles.map((c) => c.port), heldSlots: held, at: new Date().toISOString() };
}

// "Stop it cleanly": /shutdown over the server's admin console, then wait until the game port is
// free, then watch a little longer to catch a Server.exe watchdog bringing it back.
// Resolves { stopped, relaunched, message }.
async function stopCleanly({ port, gamePort, gameProto = 'udp', waitMs = 90000, watchMs = 5000, pollMs = 500 }, deps = {}) {
  const probe = deps.probePort || N.probePort;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const ac = new AC.AdminConsole({ port, retryMs: 200, commandTimeoutMs: 15000, connectFn: deps.connectFn });
  const log = deps.log || (() => {});
  try {
    ac.attach();
    const t0 = Date.now();
    while (!ac.isConnected()) {
      if (Date.now() - t0 > 5000) return { stopped: false, relaunched: false, message: `Nothing answered on 127.0.0.1:${port}, the server's admin console. Close the server's window, or end ROK.exe in Task Manager > Details.` };
      await sleep(100);
    }
    log(`Connected to the admin console on 127.0.0.1:${port}; sending /shutdown (the game saves, then exits).`);
    try {
      await ac.send('/shutdown', { timeoutMs: 15000 });
    } catch {
      /* the game may close the connection before it answers; the port check below decides */
    }
  } finally {
    // After /shutdown, closing is harmless: it is also the game's own "last client left" shutdown.
    ac.close();
  }
  const t1 = Date.now();
  while ((await probe(gamePort, gameProto)) === 'in-use') {
    if (Date.now() - t1 > waitMs) return { stopped: false, relaunched: false, message: `The server was asked to shut down but still holds ${gameProto.toUpperCase()} ${gamePort} after ${Math.round(waitMs / 1000)} s. It may still be saving; check again shortly.` };
    await sleep(pollMs);
  }
  const t2 = Date.now();
  while (Date.now() - t2 < watchMs) {
    await sleep(pollMs);
    if ((await probe(gamePort, gameProto)) === 'in-use') {
      return { stopped: true, relaunched: true, message: `The server shut down, but ${gameProto.toUpperCase()} ${gamePort} was taken again within ${Math.round((Date.now() - t2) / 1000) + 1} s: the game's Server.exe watchdog started it again. Close Server.exe first (it runs as administrator), then try again.` };
    }
  }
  return { stopped: true, relaunched: false, message: `The server saved and shut down; ${gameProto.toUpperCase()} ${gamePort} is free.` };
}

module.exports = { classify, survey, stopCleanly, CONSOLE_PORTS, KIND_TEXT };
