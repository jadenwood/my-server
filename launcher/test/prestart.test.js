'use strict';
// Pre-start detection (lib/prestart.js): who holds the port, and what Steward may do about it.
// The cases are the ones hit on the owner's Windows 11 PC on 2026-10-02 (docs/HANDOFF.md).
const { test } = require('node:test');
const assert = require('node:assert/strict');
const PS = require('../lib/prestart');

const ROOT = 'G:\\RealmTest\\server';
const GAME = 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings\\ROK.exe';
const busyGame = [{ proto: 'udp', port: 7350, what: 'game' }];
const holder = (pid, name, p) => ({ pid, name, path: p, port: 7350, proto: 'udp' });

test('nothing running: clear', () => {
  const r = PS.classify({ root: ROOT });
  assert.equal(r.verdict, 'clear');
  assert.equal(r.actions.stop.ok, false);
});

test('leftover server from this folder with its console: Stop it cleanly and Adopt it', () => {
  const r = PS.classify({
    root: ROOT,
    busy: busyGame,
    portHolders: [holder(4242, 'ROK', `${ROOT}\\ROK.exe`)],
    procs: [{ pid: 4242, name: 'ROK', path: `${ROOT}\\ROK.exe` }],
    consoles: [{ port: 11000, inUse: true, pids: [4242] }],
    plannedCport: 11000
  });
  assert.equal(r.verdict, 'leftover');
  assert.equal(r.holders.length, 1, 'one process, found both on the port and in the folder');
  assert.deepEqual(r.holders[0].ports, ['UDP 7350']);
  assert.deepEqual(r.actions.stop, { ok: true, port: 11000, why: r.actions.stop.why });
  assert.equal(r.actions.adopt.ok, true);
  assert.equal(r.actions.adopt.pid, 4242);
  assert.equal(r.actions.adopt.port, 11000);
  assert.match(r.message, /UDP 7350 \(game\) is already in use\. Found a server from this folder/);
});

test('leftover started by an old Steward on another slot\'s console port is matched by pid', () => {
  const r = PS.classify({
    root: ROOT,
    busy: busyGame,
    portHolders: [holder(77, 'ROK', `${ROOT}\\ROK.exe`)],
    procs: [{ pid: 77, name: 'ROK', path: `${ROOT}\\ROK.exe` }],
    consoles: [{ port: 11000, inUse: true, pids: [9] }, { port: 11002, inUse: true, pids: [77] }],
    plannedCport: 11000
  });
  assert.equal(r.actions.adopt.port, 11002);
});

test('leftover without an admin console: no clean way in, never a kill', () => {
  const r = PS.classify({ root: ROOT, busy: busyGame, portHolders: [holder(5, 'ROK', `${ROOT}\\ROK.exe`)], procs: [{ pid: 5, name: 'ROK', path: `${ROOT}\\ROK.exe` }], consoles: [] });
  assert.equal(r.verdict, 'leftover');
  assert.equal(r.actions.stop.ok, false);
  assert.equal(r.actions.adopt.ok, false);
  assert.match(r.actions.stop.why, /admin console .* is not listening/);
});

test('two leftovers from this folder: close all but one first', () => {
  const procs = [{ pid: 5, name: 'ROK', path: `${ROOT}\\ROK.exe` }, { pid: 6, name: 'ROK', path: `${ROOT}\\ROK.exe` }];
  const r = PS.classify({ root: ROOT, busy: busyGame, procs, consoles: [{ port: 11000, inUse: true, pids: [5] }] });
  assert.equal(r.actions.stop.ok, false);
  assert.match(r.actions.stop.why, /2 servers are running from this folder/);
});

test('Server.exe watchdog running elevated (paths hidden): explain, offer nothing', () => {
  const r = PS.classify({
    root: ROOT,
    busy: busyGame,
    portHolders: [holder(4356, 'ROK', '')],
    procs: [{ pid: 4356, name: 'ROK', path: '' }, { pid: 4300, name: 'Server', path: '' }],
    consoles: [{ port: 11000, inUse: true, pids: [4356] }],
    heldSlots: [0]
  });
  assert.equal(r.verdict, 'watchdog');
  assert.equal(r.watchdog.pid, 4300);
  assert.deepEqual(r.holders.map((h) => h.kind).sort(), ['watchdog', 'watchdog']);
  assert.equal(r.actions.stop.ok, false);
  assert.equal(r.actions.adopt.ok, false);
  assert.match(r.actions.stop.why, /Close the Server\.exe window first/);
  assert.match(r.actions.stop.why, /does not end administrator programs/);
});

test('Server.exe from this folder with a visible path is the watchdog too', () => {
  const r = PS.classify({ root: ROOT, procs: [{ pid: 1, name: 'Server', path: `${ROOT}\\Server.exe` }, { pid: 2, name: 'ROK', path: `${ROOT}\\ROK.exe` }] });
  assert.equal(r.verdict, 'watchdog');
});

test('the game client on the port', () => {
  const r = PS.classify({ root: ROOT, busy: busyGame, portHolders: [holder(4356, 'ROK', GAME)], procs: [{ pid: 4356, name: 'ROK', path: GAME }] });
  assert.equal(r.verdict, 'game-client');
  assert.match(r.actions.stop.why, /This is the game, not a server/);
  assert.match(r.message, /the Reign of Kings game/);
});

test('a hidden-path ROK.exe that holds this folder\'s world is a leftover; otherwise it stays unknown', () => {
  const base = { root: ROOT, busy: busyGame, portHolders: [holder(9, 'ROK', '')], procs: [{ pid: 9, name: 'ROK', path: '' }], consoles: [{ port: 11000, inUse: true, pids: [9] }] };
  const held = PS.classify({ ...base, heldSlots: [2] });
  assert.equal(held.verdict, 'leftover');
  assert.equal(held.actions.stop.ok, true, '/shutdown works on an elevated server too');
  const hidden = PS.classify(base);
  assert.equal(hidden.verdict, 'hidden');
  assert.match(hidden.actions.stop.why, /either the game, or a server started by the game's Server\.exe watchdog/);
});

test('another server folder, and a program that is not the game', () => {
  assert.equal(PS.classify({ root: ROOT, busy: busyGame, portHolders: [holder(3, 'ROK', 'D:\\Other\\server\\ROK.exe')] }).verdict, 'other-server');
  // "Reign Of Kings Dedicated Server" is not the game folder.
  assert.equal(PS.classify({ root: ROOT, busy: busyGame, portHolders: [holder(3, 'ROK', 'G:\\SteamLibrary\\steamapps\\common\\Reign Of Kings Dedicated Server\\ROK.exe')] }).verdict, 'other-server');
  const u = PS.classify({ root: ROOT, busy: busyGame, portHolders: [holder(3, 'svchost', 'C:\\Windows\\System32\\svchost.exe')] });
  assert.equal(u.verdict, 'unknown');
  // A busy port and no owner Steward can read (not Windows, or no rights).
  assert.equal(PS.classify({ root: ROOT, busy: busyGame }).verdict, 'unknown');
  assert.equal(PS.classify({ root: ROOT, busy: busyGame, heldSlots: [0] }).verdict, 'leftover');
});

test('survey: gathers facts through its deps, skips Steward\'s own process, never connects to a console', async () => {
  const probed = [];
  const deps = {
    probePort: async (port, proto) => {
      probed.push(`${proto}${port}`);
      return (port === 7350 && proto === 'udp') || port === 11000 ? 'in-use' : 'free';
    },
    portOwners: async (port) => (port === 7350 ? [{ pid: 50, name: 'ROK', path: `${ROOT}\\ROK.exe` }] : port === 11000 ? [{ pid: 50, name: 'ROK', path: '' }] : []),
    listProcesses: async () => [{ pid: 50, name: 'ROK', path: `${ROOT}\\ROK.exe` }, { pid: 60, name: 'ROK', path: `${ROOT}\\ROK.exe` }],
    heldSlots: async () => [0]
  };
  const sockets = [{ port: 7350, proto: 'udp', what: 'game' }, { port: 7350, proto: 'tcp', what: 'ping' }, { port: 27015, proto: 'udp', what: 'Steam query' }];
  const r = await PS.survey({ root: ROOT, sockets, plannedCport: 11000, ownPids: [60] }, deps);
  assert.equal(r.clear, false);
  assert.equal(r.verdict, 'leftover');
  assert.deepEqual(r.consoles, [11000]);
  assert.equal(r.actions.adopt.pid, 50, 'pid 60 is Steward\'s own server and is ignored');
  assert.ok(probed.includes('tcp11003'), 'console ports are checked by a bind test only');
});
