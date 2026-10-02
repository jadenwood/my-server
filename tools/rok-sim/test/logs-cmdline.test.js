'use strict';

// The game logger (file name, record format, duplicate suppression) and the command line parsers.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const { GameLogger, stamp, canLogWithCount, stripHexColor } = require('../lib/logs');
const { gameProps, unityLogFile, tryParseInt, simOptions } = require('../lib/cmdline');
const { tmpDir } = require('./helpers');

test('log file name uses a 12-hour clock, lines a 24-hour clock', () => {
  const d = new Date(2026, 9, 2, 21, 5, 9); // 21:05:09 local
  assert.equal(stamp(d, true), '261002-090509');
  assert.equal(stamp(d, false), '261002-210509');
  const noon = new Date(2026, 0, 1, 0, 0, 0);
  assert.equal(stamp(noon, true), '260101-120000');
  const dir = tmpDir();
  const lg = new GameLogger({ cwd: dir, now: () => d });
  lg.info('Server for {0} players started on port {1}.', [120, 7350], ['CodeHatch.Engine.Networking.CoreServer.Start()']);
  lg.error('Player {0} denied connection because they were banned.', ['Wren'], []);
  lg.exception('FormatException', 'Last two bytes of the packet were not zero.', ['A.B()', 'C.D()']);
  const files = fs.readdirSync(path.join(dir, 'Logs'));
  assert.deepEqual(files, ['Log[261002-090509].txt']);
  const text = fs.readFileSync(path.join(dir, 'Logs', files[0]), 'utf8');
  assert.equal(
    text,
    '[261002-210509] [Info]   Server for 120 players started on port 7350.\r\n    CodeHatch.Engine.Networking.CoreServer.Start()\r\n\n' +
      '[261002-210509] [Error]  Player Wren denied connection because they were banned.\r\n\n' +
      '[261002-210509] [Except] FormatException: Last two bytes of the packet were not zero.\r\n    A.B()\r\n    C.D()\n'
  );
});

test('duplicate suppression is per template and follows CanLogWithCount', () => {
  const dir = tmpDir();
  const lg = new GameLogger({ cwd: dir });
  const seen = [];
  lg.onLog((lvl, line) => seen.push(line));
  for (let i = 0; i < 130; i++) lg.info('{0} has connected. ({1})', [`P${i}`, i]); // different text, same template
  const written = lg.records.length;
  const expected = [...Array(130).keys()].map((i) => i + 1).filter((n) => n < 50 || canLogWithCount(n)).length;
  assert.equal(written, expected);
  assert.equal(written, 49 + 11 + 1); // 1..49, 50,55,..,100, 120
  assert.equal(seen.length, written, 'the console sees exactly what the file gets');
  lg.info('another template');
  assert.equal(lg.records.length, written + 1);
});

test('console tags and colour stripping', () => {
  const dir = tmpDir();
  const lg = new GameLogger({ cwd: dir });
  const seen = [];
  lg.onLog((lvl, line) => seen.push(line));
  lg.info('[23A9CF]Server[-] says hi');
  lg.warn('careful');
  lg.error('broken');
  lg.debugLine('hidden by default');
  lg.exception('IOException', 'boom', ['X.Y()']);
  assert.deepEqual(seen, ['[I] Server says hi', '[W] careful', '[E] broken', '[X] IOException: boom\n   at X.Y()']);
  assert.equal(stripHexColor('[FFFFFF]a[-]b'), 'ab');
});

test('CommandLineProps: dash keys, values, case, duplicates', () => {
  const p = gameProps(['ROK.exe', '-batchmode', '-nographics', '-cport', '11001', '-logFile', 'C:\\x\\log.txt', '+connect', 'x', 'bare', '-Port', '-5']);
  assert.equal(p.get('batchmode'), '');
  assert.equal(p.get('cport'), '11001');
  assert.equal(p.get('logFile'), 'C:\\x\\log.txt');
  assert.equal(p.get('Port'), ''); // "-5" starts with '-', so it is a key, not a value
  assert.ok(p.has('5'));
  assert.ok(!p.has('connect'));
  assert.throws(() => gameProps(['-cport', '1', '-cport', '2']), (e) => e.exceptionType === 'ArgumentException');
  assert.equal(tryParseInt('11000'), 11000);
  assert.equal(tryParseInt('abc'), 0);
  assert.equal(tryParseInt('99999999999'), 0);
  assert.equal(unityLogFile(['-batchmode', '-LOGFILE', 'a.log']), 'a.log');
  assert.equal(unityLogFile(['-logFile', '-batchmode']), '-');
  assert.equal(unityLogFile(['-batchmode']), null);
});

test('simulator options are separated from game arguments and validated', () => {
  const { sim, rest } = simOptions(['-batchmode', '--sim-boot-ms', '5', '--sim-players=A, B', '--sim-torn-writes', '--sim-a2s-challenge', 'off', '-cport', '1']);
  assert.deepEqual(rest, ['-batchmode', '-cport', '1']);
  assert.equal(sim.bootMs, 5);
  assert.deepEqual(sim.players, ['A', 'B']);
  assert.equal(sim.tornWrites, true);
  assert.equal(sim.a2sChallenge, false);
  assert.throws(() => simOptions(['--sim-nope']), /unknown simulator option/);
  assert.throws(() => simOptions(['--sim-steam', 'maybe']), /must be one of/);
  assert.throws(() => simOptions(['--sim-load-ms', 'x']), /needs a number/);
  assert.throws(() => simOptions(['--sim-load-ms']), /needs a value/);
});
