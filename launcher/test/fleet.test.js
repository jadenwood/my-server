// Instances, port allocation, supervisor decisions, firewall command construction, network parsing.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const FL = require('../lib/fleet');
const SV = require('../lib/supervisor');
const FW = require('../lib/firewall');
const N = require('../lib/netcheck');
const PK = require('../lib/shared/pick');
const NT = require('../lib/shared/notify');

test('port plan: four slots, all ports distinct, RCON never meets another query port', () => {
  assert.deepEqual(FL.portsForSlot(1), { game: 7350, query: 27015, rcon: 27016 });
  assert.deepEqual(FL.portsForSlot(4), { game: 7380, query: 27045, rcon: 27046 });
  assert.throws(() => FL.portsForSlot(5), /1 to 4/);
  const all = [1, 2, 3, 4].flatMap((s) => Object.values(FL.portsForSlot(s)));
  assert.equal(new Set(all).size, all.length);
  const list = [1, 2, 3, 4].map((s) => FL.makeInstance(s, { root: `G:\\RealmTest\\s${s}\\server` }));
  assert.deepEqual(FL.fleetProblems(list, 'win32'), []);
});

test('fleet problems: duplicates, reserved ports and overlapping folders', () => {
  const a = FL.makeInstance(1, { root: 'G:\\RealmTest\\server' });
  const b = FL.makeInstance(2, { root: 'G:\\RealmTest\\s2\\server', ports: { game: 7350, query: 27025, rcon: 27026 } });
  assert.match(FL.fleetProblems([a, b], 'win32').join(' '), /s2: port 7350 is already used by s1 game/);
  const c = FL.makeInstance(2, { root: 'G:\\RealmTest\\s2\\server', ports: { game: 11000, query: 27025, rcon: 27026 } });
  assert.match(FL.fleetProblems([a, c], 'win32').join(' '), /reserved for the game admin console/);
  const d = FL.makeInstance(2, { root: 'g:\\realmtest\\server\\inner' });
  assert.match(FL.fleetProblems([a, d], 'win32').join(' '), /overlaps s1/);
  const e = FL.makeInstance(2, { root: 'G:\\X', ports: { game: 80, query: 27025, rcon: 27026 } });
  assert.match(FL.fleetProblems([a, e], 'win32').join(' '), /1024 to 65535/);
});

test('instances: defaults, migration from the single-server settings and next free slot', () => {
  const list = FL.normalizeInstances(undefined, 'G:\\RealmTest\\server');
  assert.equal(list.length, 1);
  assert.equal(list[0].id, 's1');
  assert.equal(list[0].root, 'G:\\RealmTest\\server');
  assert.deepEqual(list[0].ports, { game: 7350, query: 27015, rcon: 27016 });
  assert.equal(list[0].autoRestart, true);
  assert.equal(list[0].network, 'local');
  const mixed = FL.normalizeInstances(
    [
      { id: 's3', root: 'G:\\R\\s3\\server', network: 'public', listed: true, dailyRestart: { enabled: true, time: '25:00' } },
      { id: 's1', root: 'IGNORED', ports: { game: 'x' } },
      { id: 's2' },
      { id: 'evil', root: 'C:\\' },
      { id: 's3', root: 'dup' }
    ],
    'G:\\RealmTest\\server'
  );
  assert.deepEqual(mixed.map((i) => i.id), ['s1', 's3']);
  assert.equal(mixed[0].root, 'G:\\RealmTest\\server');
  assert.equal(mixed[0].ports.game, 7350);
  assert.equal(mixed[1].network, 'public');
  assert.equal(mixed[1].dailyRestart.enabled, true);
  assert.equal(mixed[1].dailyRestart.time, '06:00');
  assert.equal(FL.nextFreeSlot(mixed), 2);
  assert.equal(FL.defaultRoot(2, 'G:\\RealmTest\\server', 'win32'), 'G:\\RealmTest\\s2\\server');
  assert.equal(FL.defaultRoot(1, 'G:\\RealmTest\\server', 'win32'), 'G:\\RealmTest\\server');
  assert.equal(FL.defaultName(3), 'Realm III');
});

test('cfg values written before each start', () => {
  const local = FL.cfgValuesFor(FL.makeInstance(2));
  assert.deepEqual(local.server, { bindIP: '127.0.0.1', isPrivate: 'True', portNumber: '7360', pingPort: '7360', steamAuthPort: '27025', restartTime: '0' });
  assert.deepEqual(local.console, { rConPort: '27026', enableRCon: 'False' });
  const pub = FL.cfgValuesFor(FL.makeInstance(1, { network: 'public', listed: false }), { restartTime: 3600.4 });
  assert.equal(pub.server.bindIP, '0.0.0.0');
  assert.equal(pub.server.isPrivate, 'True');
  assert.equal(pub.server.restartTime, '3600');
  assert.equal(FL.cfgValuesFor(FL.makeInstance(1, { network: 'public', listed: true })).server.isPrivate, 'False');
  assert.equal(FL.cfgValuesFor(FL.makeInstance(1, { network: 'local', listed: true })).server.isPrivate, 'True');
  assert.equal(FL.MAX_PLAYERS_CAP, 120);
});

test('crash restarts: backoff ladder, reset after a healthy run, loop breaker', () => {
  const t0 = 1_000_000_000;
  let d = SV.crashDecision([], t0, 60_000);
  assert.deepEqual([d.action, d.delayMs, d.attempt], ['restart', 10_000, 1]);
  d = SV.crashDecision(d.history, t0 + 60_000, 30_000);
  assert.deepEqual([d.delayMs, d.attempt], [30_000, 2]);
  d = SV.crashDecision(d.history, t0 + 120_000, 30_000);
  assert.equal(d.delayMs, 60_000);
  d = SV.crashDecision(d.history, t0 + 180_000, 30_000);
  assert.equal(d.delayMs, 120_000);
  d = SV.crashDecision(d.history, t0 + 240_000, 30_000);
  assert.equal(d.action, 'halt');
  assert.match(d.reason, /5 crashes within 15 minutes/);
  const healthy = SV.crashDecision([t0, t0 + 1000], t0 + 2000, 31 * 60_000);
  assert.deepEqual([healthy.attempt, healthy.delayMs], [1, 10_000]);
  const old = SV.crashDecision([t0, t0 + 1, t0 + 2, t0 + 3], t0 + 20 * 60_000, 1000);
  assert.equal(old.action, 'restart');
  assert.equal(old.attempt, 1);
  assert.equal(SV.crashDecision([t0, t0 + 1, t0 + 2, t0 + 3, t0 + 4, t0 + 5], t0 + 6, 1000).delayMs, undefined);
});

test('daily restart: next slot, restartTime seconds and scheduled-exit window', () => {
  const now = new Date(2026, 9, 2, 5, 0, 0);
  assert.equal(SV.nextSlot('06:00', now).getHours(), 6);
  assert.equal(SV.nextSlot('06:00', now).getDate(), 2);
  assert.equal(SV.nextSlot('05:05', now).getDate(), 3, 'less than 10 minutes away moves to tomorrow');
  assert.equal(SV.restartTimeFor('06:00', now).seconds, 3600);
  assert.equal(SV.restartTimeFor('06:00', now, 120).seconds, 3480);
  assert.equal(SV.restartTimeFor('05:20', now, 900).seconds, 600, 'never below 10 minutes');
  assert.throws(() => SV.nextSlot('6am', now), /06:00/);
  const at = new Date(2026, 9, 2, 6, 0, 0).toISOString();
  assert.equal(SV.isScheduledExit(at, Date.parse(at) - 60_000), true);
  assert.equal(SV.isScheduledExit(at, Date.parse(at) + 60_000), true);
  assert.equal(SV.isScheduledExit(at, Date.parse(at) - 3 * 3600_000), false);
  assert.equal(SV.isScheduledExit(null), false);
});

test('backup retention prunes only automatic backups, oldest first', () => {
  const names = ['realm-saves-20261001-010000-auto.zip', 'realm-saves-20261001-020000-auto.zip', 'realm-saves-20261001-030000-auto.zip', 'realm-saves-20261001-040000.zip', 'realm-saves-20261001-050000-pre-restore.zip'];
  assert.deepEqual(SV.backupsToPrune(names, 'auto', 2), ['realm-saves-20261001-010000-auto.zip']);
  assert.deepEqual(SV.backupsToPrune(names, 'auto', 24), []);
});

test('firewall: exact rules scoped to ROK.exe and the instance ports, admin console blocked', () => {
  const inst = FL.makeInstance(2);
  const exe = 'G:\\RealmTest\\s2\\server\\ROK.exe';
  const rules = FW.rulesFor(inst, exe);
  assert.deepEqual(
    rules.map((r) => [r.name, r.action, r.protocol, r.ports]),
    [
      ['Realm s2 game (UDP)', 'Allow', 'UDP', '7360'],
      ['Realm s2 ping (TCP)', 'Allow', 'TCP', '7360'],
      ['Realm s2 Steam query (UDP)', 'Allow', 'UDP', '27025'],
      ['Realm s2 admin console BLOCK (TCP)', 'Block', 'TCP', '11000-11003']
    ]
  );
  assert.ok(rules.every((r) => r.program === exe));
  assert.deepEqual(FW.ruleTable(inst).map((r) => r.ports), ['7360', '7360', '27025', '11000-11003']);
  const add = FW.addScript(inst, exe);
  assert.match(add, /^\$ErrorActionPreference = 'Stop'/);
  assert.match(add, /Remove-NetFirewallRule -DisplayName 'Realm s2 game \(UDP\)','Realm s2 ping \(TCP\)','Realm s2 Steam query \(UDP\)','Realm s2 admin console BLOCK \(TCP\)' -ErrorAction SilentlyContinue/);
  assert.match(add, /New-NetFirewallRule -DisplayName 'Realm s2 game \(UDP\)' -Group 'Realm' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 7360 -Program 'G:\\RealmTest\\s2\\server\\ROK\.exe' -Profile Any \| Out-Null/);
  assert.match(add, /-Action Block -Protocol TCP -LocalPort 11000-11003/);
  assert.equal((add.match(/New-NetFirewallRule/g) || []).length, 4);
  assert.doesNotMatch(add, /netsh|Set-NetFirewallProfile|-Enabled False|Any -Action Allow -Protocol Any/);
  const rm = FW.removeScript('s2');
  assert.match(rm, /^Remove-NetFirewallRule -DisplayName 'Realm s2 game \(UDP\)'/);
  assert.doesNotMatch(rm, /Group|\*/);
  assert.throws(() => FW.removeScript('s1; Remove-NetFirewallRule -All'), /bad instance id/);
});

test('firewall: refuses unsafe program paths and ports, encodes for -EncodedCommand', () => {
  const inst = FL.makeInstance(1);
  for (const bad of ["G:\\x'; Remove-Item C:\\ -Recurse #\\ROK.exe", 'G:\\$env:x\\ROK.exe', 'G:\\a`b\\ROK.exe', 'relative\\ROK.exe', 'G:\\server\\Server.exe', 'G:\\s\\ROK.exe\r\nGet-Process', 'G:\\[a]\\ROK.exe', 'G:\\%TEMP%\\ROK.exe']) {
    assert.throws(() => FW.rulesFor(inst, bad), /program path|ROK\.exe/, bad);
  }
  assert.throws(() => FW.rulesFor({ ...inst, ports: { ...inst.ports, game: 80 } }, 'G:\\s\\ROK.exe'), /bad port/);
  assert.equal(FW.psQuote("it's"), "'it''s'");
  const script = FW.listScript('s1');
  assert.equal(Buffer.from(FW.encode(script), 'base64').toString('utf16le'), script);
  const args = FW.elevatedArgs('exit 0');
  assert.deepEqual(args.slice(0, 5), ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command']);
  assert.match(args[5], /Start-Process -FilePath 'powershell\.exe' -Verb RunAs -Wait -PassThru/);
  assert.match(args[5], /exit 1223/);
  assert.deepEqual(FW.parseList('Realm s1 game (UDP)|True|Allow\r\nOther rule|True|Allow\r\nRealm s1 admin console BLOCK (TCP)|True|Block\r\n'), [
    { name: 'Realm s1 game (UDP)', enabled: true, action: 'Allow' },
    { name: 'Realm s1 admin console BLOCK (TCP)', enabled: true, action: 'Block' }
  ]);
});

test('network facts from the console and addresses', () => {
  assert.deepEqual(N.parseSteamLine('[I] Steam game server started. (IP: 203.0.113.9, Logged: True, Secure: False)'), { publicIp: '203.0.113.9', logged: true, secure: false });
  assert.equal(N.parseSteamLine('Steam game server failed'), null);
  assert.deepEqual(N.parseListeningLine('Server for 120 players started on port 7360.'), { maxPlayers: 120, port: 7360 });
  assert.equal(N.isCgnat('100.64.1.1'), true);
  assert.equal(N.isCgnat('100.128.0.1'), false);
  assert.equal(N.isPrivateIPv4('192.168.1.20'), true);
  assert.equal(N.isPrivateIPv4('172.32.0.1'), false);
  assert.equal(N.isPrivateIPv4('203.0.113.9'), false);
});

test('port probe sees a bound port as in use', async () => {
  const net = require('net');
  const srv = net.createServer();
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const port = srv.address().port;
  try {
    assert.equal(await N.probePort(port, 'tcp', '127.0.0.1'), 'in-use');
  } finally {
    srv.close();
  }
  assert.equal(await N.probePort(port, 'tcp', '127.0.0.1'), 'free');
});

test('join best server: never offline, avoid full, prefer lively then low ping', () => {
  const servers = [
    { id: 's1', maxPlayers: 120 },
    { id: 's2', maxPlayers: 120 },
    { id: 's3', maxPlayers: 120 },
    { id: 's4', maxPlayers: 120 }
  ];
  const st = {
    s1: { online: true, players: 120, maxPlayers: 120, pingMs: 10 },
    s2: { online: true, players: 60, maxPlayers: 120, pingMs: 80 },
    s3: { online: false, players: 0, pingMs: 5 },
    s4: { online: true, players: 2, maxPlayers: 120, pingMs: 20 }
  };
  const best = PK.pickBest(servers, st);
  assert.equal(best.id, 's2');
  assert.match(best.why, /60\/120 players, 80 ms/);
  assert.equal(PK.pickBest(servers, { s3: { online: false } }).id, 's1', 'unknown status beats known offline');
  assert.equal(PK.pickBest([{ id: 's3', maxPlayers: 1 }], { s3: { online: false } }), null);
  assert.equal(PK.pickBest(servers, { ...st, s2: { online: true, players: 60, maxPlayers: 120, pingMs: 900 } }).id, 's4');
  assert.match(PK.scoreServer(servers[0], st.s1).why, /full/);
});

test('chronicle notifications: only crown and rebellion events, first poll is a baseline', () => {
  const evs = [
    { id: 4, type: 'coronation', title: 'Aldric is crowned', detail: 'House Varrow' },
    { id: 5, type: 'ransom_paid', title: 'x' },
    { id: 6, type: 'rebellion_started', title: 'House Corvane rebels\u0007', detail: '' }
  ];
  assert.deepEqual(NT.freshEvents(evs, null), { events: [], lastId: 6 });
  const r = NT.freshEvents(evs, 4);
  assert.deepEqual(r.events.map((e) => e.id), [5, 6]);
  assert.deepEqual(NT.eventToNotification(evs[0], 'Realm I'), { title: 'Realm I: A new king', body: 'Aldric is crowned. House Varrow' });
  assert.equal(NT.eventToNotification(evs[1], 'Realm I'), null);
  assert.equal(NT.eventToNotification({ type: '__proto__' }), null);
  assert.equal(NT.eventToNotification(evs[2], 'Realm II').body, 'House Corvane rebels');
});

test('crash breaker: spread-out crashes still halt after a streak without a healthy run', () => {
  const t0 = Date.parse('2026-10-02T00:00:00Z');
  let hist = [];
  let streak = 0;
  let d;
  let t = t0;
  // A server that dies 4 minutes after every start, with the 5-minute backoff: about 2 crashes per
  // 15 minutes, so the 15-minute window alone would never trip.
  for (let i = 0; i < 20; i++) {
    d = SV.crashDecision(hist, t, 4 * 60_000, streak);
    hist = d.history;
    streak = d.streak;
    if (d.action === 'halt') break;
    t += d.delayMs + 4 * 60_000;
  }
  assert.equal(d.action, 'halt');
  assert.equal(streak, SV.STREAK_MAX);
  assert.match(d.reason, /in a row/);
  // One healthy run resets the streak.
  const ok = SV.crashDecision([], t + 3600_000, 31 * 60_000, SV.STREAK_MAX - 1);
  assert.deepEqual([ok.action, ok.streak], ['restart', 1]);
});

test('daily restart: a short run near the slot is a crash, and a used slot is not planned again', () => {
  const at = new Date(2026, 9, 2, 6, 0, 0).toISOString();
  assert.equal(SV.isScheduledExit(at, Date.parse(at) - 60_000, 30_000), false, 'start-up crash near the slot');
  assert.equal(SV.isScheduledExit(at, Date.parse(at) + 30_000, 6 * 3600_000), true);
  const early = new Date(Date.parse(at) - 15 * 60_000);
  assert.equal(SV.restartTimeFor('06:00', early).at, at, 'without skip the same slot comes back');
  const next = SV.restartTimeFor('06:00', early, 0, { skip: at });
  assert.equal(Date.parse(next.at) - Date.parse(at), 24 * 3600_000);
});
