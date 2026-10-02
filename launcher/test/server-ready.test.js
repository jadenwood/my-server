'use strict';
// The server is ready on CoreServer's "Server for N players started on port P." line, not on Unity's
// "Initialize engine version:" banner, and the tail reads both Logs\realm-server.log and the game's
// own Logs\Log[yyMMdd-hhmmss].txt without dropping lines.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { ServerManager } = require('../lib/server-process.js');
const G = require('../lib/shared/gamelog.js');

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function until(fn, ms = 5000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (fn()) return true;
    await wait(20);
  }
  return false;
}

test('ready and readyAt flip on the "Server for N players started on port P." line only', () => {
  const m = new ServerManager();
  m.child = { pid: 1 }; // inspect() only runs for a live process
  m.startedAt = new Date().toISOString();
  m.log('out', 'Initialize engine version: 5.6.7f1 (e80cc3114ac1)');
  m.log('out', 'Loading level CrownLand...');
  assert.strictEqual(m.ready, false);
  assert.strictEqual(m.readyAt, null);
  m.log('log', 'Server for 120 players started on port 7350.');
  assert.strictEqual(m.ready, true);
  assert.ok(Date.parse(m.readyAt) >= Date.parse(m.startedAt));
  assert.deepStrictEqual(m.listening, { maxPlayers: 120, port: 7350 });
  const first = m.readyAt;
  m.log('log', 'Server for 120 players started on port 7350.');
  assert.strictEqual(m.readyAt, first, 'readyAt keeps the first time');
});

test('output read after exit does not mark the server ready', () => {
  const m = new ServerManager();
  m.log('log', 'Server for 120 players started on port 7350.');
  assert.strictEqual(m.ready, false);
});

test('gamelog: candidates, resolveLogs and readFrom', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-gamelog-'));
  const dir = path.join(root, 'Logs');
  fs.mkdirSync(dir);
  fs.writeFileSync(path.join(dir, 'realm-server.log'), 'unity\n');
  fs.writeFileSync(path.join(dir, 'Log261002-010203.txt'), 'game\n');
  fs.writeFileSync(path.join(dir, 'notes.txt'), 'other\n');
  assert.deepStrictEqual(G.serverLogCandidates(root).map((e) => e.name).sort(), ['Log261002-010203.txt', 'realm-server.log']);
  const r = await G.resolveLogs(root);
  assert.strictEqual(r.unity.name, 'realm-server.log');
  assert.strictEqual(r.newestGame.name, 'Log261002-010203.txt');
  const p = path.join(dir, 'Log261002-010203.txt');
  const a = await G.readFrom(p, 0);
  assert.strictEqual(a.buf.toString(), 'game\n');
  assert.strictEqual(a.next, 5);
  const b = await G.readFrom(p, 100);
  assert.strictEqual(b.reset, true);
  assert.strictEqual(b.buf.toString(), 'game\n');
});

test('tail follows both log files, skips old content, and flips ready from the game log', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-tail-'));
  const dir = path.join(root, 'Logs');
  fs.mkdirSync(dir);
  const oldGame = path.join(dir, 'Log261001-090000.txt');
  fs.writeFileSync(oldGame, 'Server for 30 players started on port 7350.\n');
  fs.utimesSync(oldGame, new Date(Date.now() - 3600e3), new Date(Date.now() - 3600e3));

  const m = new ServerManager({ tailAfterMs: 0, tailEveryMs: 30 });
  m.child = { pid: 1 };
  m.startedAt = new Date().toISOString();
  const seen = [];
  m.on('line', (l) => { if (l.src === 'log') seen.push(l.text); });
  m.startTail(root);

  const unity = path.join(dir, 'realm-server.log');
  const game = path.join(dir, 'Log261002-120000.txt');
  fs.writeFileSync(unity, 'Initialize engine version: 5.6.7f1\r\n');
  fs.writeFileSync(game, 'Loading level CrownLand...\nhalf a ');
  assert.ok(await until(() => seen.includes('Loading level CrownLand...') && seen.some((t) => /Initialize engine/.test(t))));
  assert.strictEqual(m.ready, false, 'the engine banner does not mean ready');
  fs.appendFileSync(game, 'line\n');
  fs.appendFileSync(unity, 'Unity line 2\n');
  fs.appendFileSync(game, 'Server for 120 players started on port 7360.\n');
  assert.ok(await until(() => m.ready));
  assert.deepStrictEqual(m.listening, { maxPlayers: 120, port: 7360 });
  assert.ok(await until(() => seen.includes('Unity line 2')));
  assert.ok(seen.includes('half a line'), 'a line split across reads is joined');
  assert.ok(!seen.some((t) => /port 7350/.test(t)), 'the previous run\'s log is not replayed');

  // The last lines written right before exit are still read.
  fs.appendFileSync(game, 'Saving world...\nServer shut down.');
  m.child = null;
  await m.stopTail({ drain: true });
  assert.ok(seen.includes('Saving world...') && seen.includes('Server shut down.'));
  assert.strictEqual(new Set(seen).size, seen.length, 'no line is read twice');
});
