'use strict';

// The command line program as a real child process: what a launcher or CI job would run.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const path = require('path');
const { spawn, execFileSync, spawnSync } = require('child_process');
const H = require('./helpers');

const BIN = path.join(__dirname, '..', 'bin', 'rok-sim.js');

function run(cwd, args, opts = {}) {
  const child = spawn(process.execPath, [BIN, ...args], { cwd, stdio: ['pipe', 'pipe', 'pipe'], ...opts });
  let out = '';
  let err = '';
  child.stdout.on('data', (d) => (out += d));
  child.stderr.on('data', (d) => (err += d));
  const done = new Promise((resolve) => child.on('close', (code, signal) => resolve({ code, signal, out, err })));
  return { child, done };
}

test('--help lists the simulator options; a bad option exits 64', () => {
  const h = execFileSync(process.execPath, [BIN, '--help'], { encoding: 'utf8' });
  assert.match(h, /--sim-a2s-challenge/);
  const r = spawnSync(process.execPath, [BIN, '-batchmode', '--sim-bogus'], { encoding: 'utf8' });
  assert.equal(r.status, 64);
  assert.match(r.stderr, /unknown simulator option --sim-bogus/);
});

test('first run as a process: exit 0, files written, nothing on stdout', async () => {
  const dir = H.tmpDir();
  const r = await run(dir, ['-batchmode', '-nographics', '-silentcrash', '-logFile', path.join('Logs', 'realm-server.log'), '--sim-boot-ms', '10', '--sim-load-ms', '50']).done;
  assert.equal(r.code, 0, r.err);
  assert.equal(r.out, '', 'like ROK.exe -batchmode with -logFile, nothing on stdout');
  assert.ok(fs.existsSync(path.join(dir, 'Configuration', 'ServerSettings.cfg')));
  assert.match(fs.readFileSync(path.join(dir, 'Logs', 'realm-server.log'), 'utf8'), /Initialize engine version/);
  assert.match(H.readGameLog(dir), /This is the first time you have run this server\./);
});

test('running process: query and console subcommands, the game ignores stdin, SIGTERM exits without saving', async () => {
  const { dir, steam } = await H.preparedFolder();
  const cport = await H.freePort();
  const p = run(dir, ['-batchmode', '-nographics', '-cport', String(cport), '--sim-boot-ms', '10', '--sim-load-ms', '50', '--sim-players', 'Wren']);
  try {
    await H.until(() => /Server for \d+ players started/.test(H.readGameLog(dir)), 5000, 'server up');
    // Keep a console connection open so the -cport window and last-client rules do not stop it.
    const holder = await H.connectConsole(cport);
    const q = JSON.parse(execFileSync(process.execPath, [BIN, 'query', '127.0.0.1', String(steam)], { encoding: 'utf8' }));
    assert.equal(q.ok, true);
    assert.equal(q.value.name, 'Another ROK Server');
    const out = execFileSync(process.execPath, [BIN, 'console', String(cport), '/list'], { encoding: 'utf8' });
    assert.match(out, /\[I\] Online Players\(1\):\n\[I\] Wren/);
    // "quit" on stdin does nothing: the real game never reads stdin.
    p.child.stdin.write('quit\n');
    await H.wait(300);
    assert.equal(p.child.exitCode, null);
    p.child.kill('SIGTERM');
    const r = await p.done;
    assert.equal(r.code, 143);
    assert.ok(!fs.existsSync(path.join(dir, 'Saves')), 'a killed server does not save');
    holder.close();
  } finally {
    if (p.child.exitCode == null) p.child.kill('SIGKILL');
  }
});

test('make-exe writes an executable ROK.exe shim that runs the simulator', async () => {
  const dir = H.tmpDir();
  execFileSync(process.execPath, [BIN, 'make-exe', dir]);
  const exe = path.join(dir, 'ROK.exe');
  assert.ok(fs.statSync(exe).mode & 0o100);
  assert.match(fs.readFileSync(exe, 'utf8'), /rok-sim shim \(tests only, NOT the game\)/);
  if (process.platform === 'win32') return;
  const r = spawnSync(exe, ['-batchmode', '--sim-boot-ms', '10', '--sim-load-ms', '30'], { cwd: dir, encoding: 'utf8' });
  assert.equal(r.status, 0, r.stderr);
  assert.ok(fs.existsSync(path.join(dir, 'ROK_Data', 'output_log.txt')), 'no -logFile: Unity writes ROK_Data/output_log.txt');
});
