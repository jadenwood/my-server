'use strict';
// Reproduces the owner's first Windows run: spawning Server.exe fails with EACCES
// (Windows' "requires elevation"), so the manager must fall back to ROK.exe with a log file.
const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { ServerManager } = require('../lib/server-process.js');

test('EACCES on Server.exe falls back to ROK.exe and logs to Logs\\realm-server.log', { skip: process.platform === 'win32' }, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'realm-eacces-'));
  fs.writeFileSync(path.join(root, 'Server.exe'), 'not runnable', { mode: 0o644 });
  fs.writeFileSync(path.join(root, 'ROK.exe'), `#!${process.execPath}\nconsole.log('ARGS ' + process.argv.slice(2).join(' '));\n`, { mode: 0o755 });
  const m = new ServerManager();
  const lines = [];
  m.on('line', (l) => lines.push(typeof l === 'string' ? l : l.text));
  let fellBackTo = null;
  m.on('exe-fallback', (exe) => { fellBackTo = exe; });
  const exited = new Promise((resolve) => m.on('exit', (e) => { if (!e.error) resolve(e); }));
  await m.start(root, 'Server');
  const e = await exited;
  assert.strictEqual(fellBackTo, 'ROK');
  assert.strictEqual(e.code, 0);
  const all = lines.join('\n');
  assert.match(all, /Could not run Server\.exe: .*EACCES/);
  assert.match(all, /Switching to ROK\.exe/);
  assert.match(all, /ARGS -batchmode -nographics -silentcrash -logFile .*Logs.realm-server\.log/);
  assert.ok(fs.existsSync(path.join(root, 'Logs')));
  m.removeAllListeners();
});
