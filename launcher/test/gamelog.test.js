// Connection Doctor: the log classifier, log locations and report redaction.
// Message strings are copied verbatim from the decompiled Assembly-CSharp.dll (Oxide 2.0.3867 zip,
// sha256 6c35c623...c72c6c8); the file names in comments say where each one comes from.
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const GL = require('../lib/shared/gamelog');
const ST = require('../lib/shared/steam');

const id = (text) => (GL.classify(text) || {}).id || null;

test('real client messages map to the right cause', () => {
  // Game.cs Join(): End("Unable to resolve host name. (" + ipString + ")") with host:port typed in.
  assert.equal(id('Unable to resolve host name. (127.0.0.1:7350)'), 'address-has-port');
  // As the owner reported it (no full stop) and as pasted from the popup.
  assert.equal(id('Unable to resolve host name (127.0.0.1:7350)'), 'address-has-port');
  // Logger.InfoFormat("Joining {0} : {1}") shows the same mistake before the error.
  assert.equal(id('Joining 127.0.0.1:7350 : 7350'), 'address-has-port');
  assert.equal(id('Unable to resolve host name. (play.exmaple.org)'), 'host-not-found');
  assert.match(GL.classify('Unable to resolve host name. ()').cause, /empty/);
  assert.equal(id('Invalid port value. Disconnecting from current game. Try again.'), 'bad-port');
  assert.equal(id("Unable to connect to '10.0.0.5'."), 'cannot-connect');
  // EACIntegration.cs / Game.cs (the typo "intiailized" is the game's).
  assert.equal(id('Please run the game from Reign Of Kings.exe'), 'eac-not-launched');
  assert.equal(id('EAC has not intiailized yet. Please try again.'), 'eac-not-ready');
  assert.equal(id('User banned by EAC'), 'eac-kick');
  // Game.JoinWithEndpoint and SteamManager.
  assert.equal(id('You need to be logged into steam in order to join a game.'), 'steam-not-signed-in');
  assert.equal(id('Unable to communicate with the steam app.'), 'steam-not-signed-in');
  assert.equal(id('[Steamworks.NET] SteamAPI_Init() failed. Refer to Valve\'s documentation or the comment above this line for more information.'), 'steam-not-signed-in');
});

test('every popup text of CoreServer.GetConnectionError is recognised', () => {
  const cases = {
    'You are banned from this server.': 'banned',
    'You are not on the server whitelist.': 'not-whitelisted',
    'Connection timed out.': 'timeout',
    'The server is not running the same version as you. (Alpha 1.8)': 'version-mismatch',
    'Incorrect password.': 'wrong-password',
    'Server is full.': 'server-full',
    'Server is not fully loaded yet.': 'server-loading',
    'Could not authenticate steam.\nPlease try again later.': 'steam-not-signed-in',
    'Took too long to connect to server.\nPlease try again later.': 'timeout',
    'Could not connect to the server. (63, NAT Punchthrough Failed)\nPlease try again later.': 'connect-error-code',
    'Lost connection to the server.': 'lost-connection',
    'Waiting in queue... Position 3/7.': 'queued'
  };
  for (const [text, want] of Object.entries(cases)) assert.equal(id(text), want, text);
  assert.equal(GL.classify('Waiting in queue... Position 3/7.').title, 'Waiting in the join queue (position 3 of 7)');
  assert.match(GL.classify('Could not connect to the server. (63, NAT Punchthrough Failed)').title, /NAT Punchthrough Failed \(code 63\)/);
});

test('server-side refusals and kicks', () => {
  // CoreServer.ProcessConnectionRequest
  assert.equal(id('Player Wren denied connection because they were banned.'), 'banned');
  assert.equal(id('Player Wren denied connection because they were not on the active whitelist.'), 'not-whitelisted');
  assert.equal(id('Player Wren denied connection due to wrong version.'), 'version-mismatch');
  assert.equal(id('Player Wren denied connection because the password provided was invalid.'), 'wrong-password');
  assert.equal(id('Player logged in with the same steam id.'), 'duplicate-login');
  // PingGraphManager kicks, as logged by CoreServer.Kick ("{0} ({1}) was kicked from the server because {2}.").
  assert.equal(id('Wren (76561190000000002) was kicked from the server because You have been disconnected for failing to initialize with the ping system..'), 'ping-port-blocked');
  assert.equal(id('Ping connection from Wren failed. No players are connected to the ping system on port 7350.'), 'ping-port-blocked');
  assert.equal(id('You have been disconnected for providing an unreliable connection to the ping system.'), 'ping-unreliable');
  assert.equal(id('You have been disconnected for exceeding the ping limit.'), 'ping-limit');
  assert.equal(id('Wren (76561190000000002) was kicked from the server because spamming.'), 'kicked');
  assert.match(GL.classify('Wren (76561190000000002) was kicked from the server because spamming.').cause, /spamming/);
  // CoreServer start-up
  assert.equal(id('This is the first time you have run this server.'), 'first-run-exit');
  assert.equal(id('The port 7350 is already being used by another application.'), 'game-port-taken');
  assert.equal(id('Could not start the server because an error occured. (IncorrectParameters)'), 'server-start-error');
  // SteamServer.StartServer
  assert.equal(id('Port already in use. Make sure you have allowed port 27015 through your firewall and forwarded the port through your router if you have one.'), 'steam-server-failed');
  assert.equal(id('The Steam game server could not initialize.'), 'steam-server-failed');
  assert.equal(id('Could not start the Steam game server because the Steam server could not login anonymously.'), 'steam-server-failed');
  // Good news lines
  assert.equal(id('Server for 120 players started on port 7360.'), 'server-ready');
  assert.equal(GL.classify('Server for 120 players started on port 7360.').title, 'Server is listening on port 7360 (120 slots)');
  assert.equal(id('Steam game server started. (IP: 203.0.113.50, Logged: True, Secure: True)'), 'steam-server-ok');
  assert.equal(id('Attempting join game: 127.0.0.1:7350 with character -1. (Password: No)'), 'quickjoin-args');
  assert.equal(id('Admin console enabled.'), 'admin-console');
  // Unrelated lines stay unclassified.
  assert.equal(id('Loading level CrownLand...'), null);
  assert.equal(id(''), null);
  assert.equal(id('Initialize engine version: 5.1.2f1'), null);
});

test('the Logger file format is parsed and the newest failure wins', () => {
  // Logger.FileFormat: "[yyMMdd-HHmmss] [Error]  message", then Context and stack lines.
  const log = [
    '[260930-201501] [Info]   SteamManager created.',
    '[260930-201502] [Info]   Joining 127.0.0.1:7350 : 7350',
    '[260930-201502] [Error]  Unable to resolve host name. (127.0.0.1:7350)',
    'Context: Game (12345)',
    '  at CodeHatch.Engine.Core.Gaming.Game.End (System.String reason)',
    '[260930-201630] [Info]   Joining 127.0.0.1 : 7350',
    "[260930-201630] [Info]   Connecting to '127.0.0.1:7350'.",
    '[260930-201631] [Info]   [Login Data] Version: 1.0, Name: Wren, Id: 76561190000000002, Auth:20,0,0,0,51,12'
  ].join('\r\n');
  assert.deepEqual(GL.parseLine('[260930-201502] [Error]  Unable to resolve host name. (x)'), { time: '2026-09-30 20:15:02', level: 'error', text: 'Unable to resolve host name. (x)' });
  assert.deepEqual(GL.parseLine('[W] Something'), { time: null, level: 'warning', text: 'Something' });
  const s = GL.scan(log, { source: 'game' });
  const ids = s.findings.map((f) => f.id);
  assert.deepEqual(ids, ['connecting', 'address-has-port']);
  const hp = s.findings.find((f) => f.id === 'address-has-port');
  assert.equal(hp.count, 2);
  assert.equal(hp.time, '2026-09-30 20:15:02');
  assert.equal(GL.verdict(s.findings).id, 'address-has-port');
  // The later "Connecting to" line shows the address was fixed since.
  assert.equal(GL.verdict(s.findings, { resolvedBy: ['connecting'] }), null);
  assert.equal(s.lines.find((l) => /Unable to resolve/.test(l.text)).finding.severity, 'bad');
  // Arrays of console line objects work too.
  assert.equal(GL.scan([{ text: 'Server is full.' }]).findings[0].id, 'server-full');
  assert.equal(GL.verdict([]), null);
});

test('log locations: client install dir from the app manifest, newest Logger file, tail reads', async () => {
  const acf = '"AppState"\n{\n\t"appid"\t\t"344760"\n\t"installdir"\t\t"Reign Of Kings"\n\t"StateFlags"\t\t"4"\n}\n';
  assert.equal(GL.installDirFromAcf(acf, ST.parseVdf), 'Reign Of Kings');
  assert.equal(GL.installDirFromAcf('"AppState" { "installdir" "..\\\\evil" }', ST.parseVdf), null);
  const dir = await GL.findGameDir({ library: 'D:\\SteamLibrary' }, { parseVdf: ST.parseVdf, readFile: async () => acf });
  assert.equal(dir, 'D:\\SteamLibrary\\steamapps\\common\\Reign Of Kings');
  assert.equal(await GL.findGameDir({ library: null }), null);
  const c = GL.clientLogCandidates(dir);
  assert.equal(c[0].dir, 'D:\\SteamLibrary\\steamapps\\common\\Reign Of Kings\\Logs');
  assert.equal(c[1].file, 'D:\\SteamLibrary\\steamapps\\common\\Reign Of Kings\\ROK_Data\\output_log.txt');

  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-gamelog-'));
  try {
    const logs = path.join(tmp, 'Logs');
    fs.mkdirSync(logs);
    fs.writeFileSync(path.join(logs, 'Log[260929-101010].txt'), '[260929-101010] [Info]   old\n');
    const newest = path.join(logs, 'Log[260930-080000].txt');
    fs.writeFileSync(newest, '[260930-080000] [Error]  The port 7350 is already being used by another application.\n');
    fs.utimesSync(path.join(logs, 'Log[260929-101010].txt'), new Date(Date.now() - 86400000), new Date(Date.now() - 86400000));
    fs.writeFileSync(path.join(logs, 'realm-server.log'), 'Initialize engine version: 5.1\n');
    fs.writeFileSync(path.join(logs, 'notes.txt'), 'not a log');
    const found = await GL.resolveLogs(GL.serverLogCandidates(tmp));
    assert.deepEqual(found.map((f) => path.basename(f.path)), ['Log[260930-080000].txt', 'realm-server.log']);
    assert.match(found[0].label, /Log\[260930-080000\]\.txt/);
    assert.ok(GL.isLogPathAllowed(newest, found));
    assert.ok(!GL.isLogPathAllowed(path.join(tmp, 'Configuration', 'ServerSettings.cfg'), found));

    const r1 = await GL.readFrom(newest, null);
    assert.match(r1.text, /already being used/);
    fs.appendFileSync(newest, '[260930-080001] [Info]   Server for 30 players started on port 7350.\n');
    const r2 = await GL.readFrom(newest, r1.offset);
    assert.equal(r2.text.trim(), '[260930-080001] [Info]   Server for 30 players started on port 7350.');
    fs.writeFileSync(newest, 'x\n'); // rotated / truncated
    const r3 = await GL.readFrom(newest, r2.offset);
    assert.equal(r3.reset, true);
    assert.equal(r3.text, 'x\n');
    // A tail read that starts mid-file drops the partial first line.
    fs.writeFileSync(newest, 'aaaaaaaaaa\nbbbb\ncccc\n');
    assert.equal((await GL.readFrom(newest, null, { tail: 8 })).text, 'cccc\n');
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('report redaction removes public IPs, Steam IDs, the auth ticket, passwords and the user name', () => {
  const raw = [
    'Steam game server started. (IP: 203.0.113.50, Logged: True, Secure: True)',
    'Authentication verified for Wren (76561190000000002).',
    '[Login Data] Version: 1.0, Name: Wren, Id: 76561198012345678, Auth:20,0,0,0,51,12,99',
    'Joining 198.51.100.7 : 7350',
    'Server on 192.168.1.20, 10.0.0.3 and 127.0.0.1 port 7350; CGNAT 100.72.1.9',
    "password = 'hunter2'",
    "rConPassword = 'secret'",
    'steam://run/344760//-ip 1.2.3.4 -port 7350 -pass opensesame',
    'C:\\Users\\AldricV\\AppData\\LocalLow\\CodeHatch',
    '/home/aldric/RealmTest/server',
    'Oxide 2.0.3867 loaded'
  ].join('\n');
  const out = GL.redact(raw, { publicIps: ['203.0.113.50'] });
  for (const secret of ['203.0.113.50', '76561190000000002', '76561198012345678', '20,0,0,0,51', '198.51.100.7', '100.72.1.9', 'hunter2', 'secret', 'opensesame', 'AldricV', 'aldric', '1.2.3.4']) {
    assert.ok(!out.includes(secret), `still contains ${secret}:\n${out}`);
  }
  assert.match(out, /IP: \[public-ip\]/);
  assert.match(out, /Wren \(\[steam-id\]\)/);
  assert.match(out, /Auth:\[removed\]/);
  // LAN and loopback addresses stay: they help diagnose and identify nobody.
  assert.match(out, /192\.168\.1\.20, 10\.0\.0\.3 and 127\.0\.0\.1 port 7350/);
  assert.match(out, /C:\\Users\\\[user\]\\AppData/);
  assert.match(out, /Oxide 2\.0\.3867 loaded/);
});

test('buildReport lists steps and findings and is redacted', () => {
  const findings = GL.scan('Authentication verified for Wren (76561190000000002).\nIncorrect password.', { source: 'pasted' }).findings;
  const text = GL.buildReport(
    {
      app: 'Realm Steward',
      version: '0.3.0',
      platform: 'win32',
      target: 'Server 1',
      verdict: { status: 'bad', title: 'Public address: Steam sees 203.0.113.50', fix: 'Forward the ports.' },
      steps: [
        { label: 'Server process', status: 'ok', detail: 'pid 4242' },
        { label: 'Firewall rules', status: 'bad', detail: '0 of 4', fix: 'Add firewall rules.' }
      ],
      findings,
      logs: [{ label: 'Server game log', lines: ['Steam game server started. (IP: 203.0.113.50, Logged: True, Secure: True)'] }],
      pasted: 'Incorrect password.'
    },
    { publicIps: ['203.0.113.50'] }
  );
  assert.match(text, /^Realm Steward Connection Doctor report/);
  assert.match(text, /\[OK  \] Server process: pid 4242/);
  assert.match(text, /\[FAIL\] Firewall rules: 0 of 4\n {7}Fix: Add firewall rules\./);
  assert.match(text, /Wrong server password/);
  assert.match(text, /--- last lines of Server game log ---/);
  assert.ok(!text.includes('203.0.113.50'));
  assert.ok(!text.includes('76561190000000002'));
});

test('steam running check reads ActiveProcess from the registry (read-only)', async () => {
  const out = 'HKEY_CURRENT_USER\\Software\\Valve\\Steam\\ActiveProcess\r\n    pid    REG_DWORD    0x1a2c\r\n    ActiveUser    REG_DWORD    0x12345\r\n';
  assert.deepEqual(ST.parseActiveProcess(out), { pid: 6700, activeUser: 0x12345 });
  const calls = [];
  const fake = (file, args, _o, cb) => (calls.push([file, ...args]), cb(null, out));
  assert.deepEqual(await ST.steamState({ platform: 'win32', execFileImpl: fake, isAlive: () => true }), { running: true, signedIn: true, pid: 6700 });
  assert.deepEqual(calls[0], ['reg', 'query', 'HKCU\\Software\\Valve\\Steam\\ActiveProcess']);
  assert.deepEqual(await ST.steamState({ platform: 'win32', execFileImpl: fake, isAlive: () => false }), { running: false, signedIn: false, pid: null });
  const signedOut = (f, a, o, cb) => cb(null, out.replace('0x12345', '0x0'));
  assert.equal((await ST.steamState({ platform: 'win32', execFileImpl: signedOut, isAlive: () => true })).signedIn, false);
  assert.deepEqual(await ST.steamState({ platform: 'linux' }), { running: null, signedIn: null, pid: null });
});

test('report redaction also covers quoted, URL-encoded and config-style passwords, slash paths and IPv6', () => {
  const raw = [
    'steam://run/344760//-ip%208.8.8.8%20-port%207350%20-pass%20s3cret',
    '-pass "two words" -port 7350',
    '-password hunter2',
    '"password": "hunter2", ServerPassword=abc',
    'C:/Users/SomeOne/AppData/LocalLow',
    'peer 2001:4860:4860::8888 local fe80::1',
    'Joining 192.168.1.20:7350 : 7350'
  ].join('\n');
  const out = GL.redact(raw);
  for (const leak of ['s3cret', 'two words', 'hunter2', 'abc', 'SomeOne', '8.8.8.8', '2001:4860']) assert.ok(!out.includes(leak), `leaked ${leak}: ${out}`);
  assert.match(out, /192\.168\.1\.20:7350/);
  assert.match(out, /fe80::1/);
  assert.match(out, /-port%207350/);
});
