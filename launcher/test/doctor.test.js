// Connection Doctor check runners (steward lib/doctor.js and player lib/shared/joincheck.js),
// driven with fake facts so no PowerShell, socket or Steam is touched.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const DOC = require('../lib/doctor');
const JC = require('../lib/shared/joincheck');
const GL = require('../lib/shared/gamelog');

const inst = (over = {}) => ({ id: 's1', ports: { game: 7350, query: 27015, rcon: 27016 }, network: 'local', ...over });

function deps(over = {}) {
  return {
    listeners: async () => ({
      supported: true,
      list: [
        { proto: 'udp', address: '127.0.0.1', port: 7350, pid: 4242, process: 'ROK' },
        { proto: 'tcp', address: '0.0.0.0', port: 7350, pid: 4242, process: 'ROK' }
      ]
    }),
    a2s: async () => ({ ok: true, rttMs: 3, info: { name: 'Another ROK Server' } }),
    steam: async () => ({ running: true, signedIn: true }),
    game: async () => ({ installed: true, library: 'D:\\SteamLibrary' }),
    firewall: async () => ({ supported: true, present: [] }),
    tcp: async () => ({ ok: true }),
    ...over
  };
}

const readyScan = () => GL.scan('Server for 30 players started on port 7350.\nSteam game server started. (IP: 203.0.113.50, Logged: True, Secure: True)');
const byId = (r, id) => r.steps.find((s) => s.id === id);

test('listener script only takes validated port numbers, and its output is parsed', () => {
  const s = DOC.listenerScript([7350, 7350, 27015]);
  assert.match(s, /Get-NetUDPEndpoint -LocalPort 7350,27015 /);
  assert.match(s, /Get-NetTCPConnection -State Listen -LocalPort 7350,27015 /);
  assert.doesNotMatch(s, /New-|Set-|Remove-/);
  assert.throws(() => DOC.listenerScript(['7350; Remove-Item C:\\']), RangeError);
  assert.throws(() => DOC.listenerScript([70000]), RangeError);
  const parsed = DOC.parseListeners('udp|0.0.0.0|7350|4242|ROK\r\ntcp|0.0.0.0|11000|4242|ROK\r\ngarbage\r\nudp|::|x|1|y');
  assert.deepEqual(parsed, [
    { proto: 'udp', address: '0.0.0.0', port: 7350, pid: 4242, process: 'ROK' },
    { proto: 'tcp', address: '0.0.0.0', port: 11000, pid: 4242, process: 'ROK' }
  ]);
});

test('a healthy local server: all clear, and the address advice never says host:port', async () => {
  const r = await DOC.runChecks({ inst: inst(), name: 'Realm I', server: { running: true, pid: 4242, exe: 'ROK', ready: true }, cfg: { exists: true, bindIP: '127.0.0.1', portNumber: 7350 }, serverScan: readyScan(), clientScan: GL.scan("Connecting to '127.0.0.1:7350'."), lan: [] }, deps());
  assert.equal(r.verdict.status, 'ok', JSON.stringify(r.steps, null, 1));
  assert.equal(byId(r, 'process').status, 'ok');
  assert.equal(byId(r, 'ready').status, 'ok');
  assert.equal(byId(r, 'udp').status, 'ok');
  assert.equal(byId(r, 'tcp').status, 'ok');
  assert.equal(byId(r, 'a2s').status, 'ok');
  assert.equal(byId(r, 'client-log').status, 'info');
  assert.match(byId(r, 'client-log').detail, /only in a game popup/);
  assert.match(byId(r, 'address').detail, /address 127\.0\.0\.1, port 7350/);
  assert.match(byId(r, 'address').detail, /never "address:port"/);
});

test('server not running: one bad step with a concrete fix, socket steps skipped', async () => {
  const r = await DOC.runChecks({ inst: inst(), server: { running: false }, serverScan: { findings: [] }, clientScan: null }, deps());
  assert.equal(r.verdict.status, 'bad');
  assert.match(r.verdict.fix, /press Start/);
  assert.equal(byId(r, 'udp').status, 'skip');
  assert.equal(byId(r, 'a2s').status, 'skip');
});

test('the owner case: client log shows host:port in the address box', async () => {
  const client = GL.scan('[260930-201502] [Error]  Unable to resolve host name. (127.0.0.1:7350)');
  const r = await DOC.runChecks({ inst: inst(), server: { running: true, pid: 1, ready: true }, serverScan: readyScan(), clientScan: client, clientLogs: [{}] }, deps());
  const s = byId(r, 'client-log');
  assert.equal(s.status, 'bad');
  assert.match(s.fix, /only the address/);
  assert.equal(r.verdict.status, 'bad');
});

test('first-run exit and port clash in the server log are reported as the blocker', async () => {
  const scan = GL.scan('This is the first time you have run this server.');
  const r = await DOC.runChecks({ inst: inst(), server: { running: false }, serverScan: scan }, deps());
  assert.match(byId(r, 'ready').detail, /first run/);
  assert.match(byId(r, 'ready').fix, /Start the server again/);
  // An old failure followed by a successful start is not reported.
  const later = GL.scan('The port 7350 is already being used by another application.\nServer for 30 players started on port 7350.');
  const r2 = await DOC.runChecks({ inst: inst(), server: { running: true, pid: 1 }, serverScan: later }, deps());
  assert.equal(byId(r2, 'ready').status, 'ok');
});

test('public server: bindIP loopback, missing firewall rules, open admin console, CGNAT', async () => {
  const pub = inst({ network: 'public' });
  const r = await DOC.runChecks(
    {
      inst: pub,
      server: { running: true, pid: 4242, ready: true, steam: { publicIp: '100.72.1.9' } },
      cfg: { exists: true, bindIP: '127.0.0.1', portNumber: 7350, enablePingLimit: true },
      serverScan: readyScan(),
      lan: [{ name: 'Ethernet', address: '192.168.1.20' }]
    },
    deps({
      listeners: async () => ({
        supported: true,
        list: [
          { proto: 'udp', address: '127.0.0.1', port: 7350, pid: 4242, process: 'ROK' },
          { proto: 'tcp', address: '0.0.0.0', port: 11000, pid: 4242, process: 'ROK' }
        ]
      })
    })
  );
  assert.equal(byId(r, 'udp').status, 'bad');
  assert.equal(byId(r, 'tcp').status, 'bad', 'ping port closed with enablePingLimit on');
  assert.equal(byId(r, 'bind').status, 'bad');
  assert.equal(byId(r, 'firewall').status, 'bad');
  assert.equal(byId(r, 'admin').status, 'bad');
  assert.equal(byId(r, 'public').status, 'bad');
  assert.match(byId(r, 'public').detail, /carrier-grade NAT/);
  assert.match(byId(r, 'address').detail, /Same home network \(Ethernet\): address 192\.168\.1\.20, port 7350/);
});

test('public server reachable: rules present, public TCP answers', async () => {
  const pub = inst({ network: 'public' });
  const rules = ['game', 'ping', 'query', 'admin console BLOCK'].map((n) => ({ name: `Realm s1 ${n}`, enabled: true, action: 'Allow' }));
  const r = await DOC.runChecks(
    { inst: pub, server: { running: true, pid: 4242, ready: true, steam: { publicIp: '203.0.113.50' } }, cfg: { exists: true, bindIP: '0.0.0.0', portNumber: 7350 }, serverScan: readyScan(), lan: [] },
    deps({
      listeners: async () => ({ supported: true, list: [{ proto: 'udp', address: '0.0.0.0', port: 7350, pid: 4242, process: 'ROK' }, { proto: 'tcp', address: '0.0.0.0', port: 7350, pid: 4242, process: 'ROK' }] }),
      firewall: async () => ({ supported: true, present: rules })
    })
  );
  assert.equal(r.verdict.status, 'ok', JSON.stringify(r.verdict));
  assert.equal(byId(r, 'public').status, 'ok');
  assert.match(byId(r, 'address').detail, /Over the internet: address 203\.0\.113\.50, port 7350/);
});

test('steam and game install problems give one fix each', async () => {
  const r = await DOC.runChecks({ inst: inst(), server: { running: false }, serverScan: { findings: [] } }, deps({ steam: async () => ({ running: false, signedIn: false }), game: async () => ({ installed: false, steam: true }) }));
  assert.equal(byId(r, 'steam').status, 'bad');
  assert.match(byId(r, 'steam').fix, /Start Steam/);
  assert.equal(byId(r, 'game').status, 'warn');
});

test('player checks: install, Steam, game log, servers via A2S', async () => {
  const servers = [
    { id: 's1', name: 'Realm I', address: 'play.example.org', port: 7350, queryPort: 27015 },
    { id: 's2', name: 'Realm II', address: 'play.example.org', port: 7360, queryPort: 27025 }
  ];
  const a2s = async (h, p) => (p === 27015 ? { ok: true, rttMs: 41 } : { ok: false, error: 'no answer (timeout)' });
  const r = await JC.runPlayerChecks(
    { servers, clientScan: GL.scan('Unable to resolve host name. (play.example.org:7350)'), clientLogs: [{}] },
    { game: async () => ({ installed: true, library: 'C:\\Steam' }), steam: async () => ({ running: true, signedIn: true }), a2s }
  );
  assert.equal(r.verdict.status, 'bad');
  assert.match(r.verdict.fix, /only the address/);
  assert.equal(r.steps.find((s) => s.id === 'server-s1').status, 'ok');
  assert.match(r.steps.find((s) => s.id === 'server-s1').detail, /address play\.example\.org, port 7350 \(separate boxes\)/);
  assert.equal(r.steps.find((s) => s.id === 'server-s2').status, 'warn');

  const ok = await JC.runPlayerChecks({ servers: servers.slice(0, 1), clientScan: GL.scan(''), clientLogs: [] }, { game: async () => ({ installed: true, library: 'C:\\Steam' }), steam: async () => ({ running: true, signedIn: true }), a2s });
  assert.equal(ok.verdict.status, 'ok');
  const noSteam = await JC.runPlayerChecks({ servers: [], clientScan: null }, { game: async () => ({ installed: null }), steam: async () => ({ running: false, signedIn: false }), a2s });
  assert.equal(noSteam.verdict.status, 'bad');
  assert.match(noSteam.verdict.fix, /Start Steam/);
});
