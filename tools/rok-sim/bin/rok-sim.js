#!/usr/bin/env node
'use strict';

// rok-sim: a fake Reign of Kings dedicated server for Linux and CI. See ../README.md.
//
//   rok-sim [ROK.exe arguments] [--sim-* options]      run a fake server in the current folder
//   rok-sim query <host> <port> [info|players|rules]   send an A2S query, print JSON
//   rok-sim console <port> [command ...]               send console lines, print what comes back
//   rok-sim make-exe <folder>                          write an executable ROK.exe shim there
//   rok-sim watchdog [ROK.exe arguments]               a Server.exe stand-in that relaunches the server
//   rok-sim client [port]                              a game-client stand-in holding UDP <port>
//   rok-sim --help

const fs = require('fs');
const path = require('path');
const net = require('net');
const { RokSim } = require('../lib/sim');
const A2S = require('../lib/a2s');
const P = require('../lib/packets');
const { FLAG_MAP, SIM_DEFAULTS, simOptions } = require('../lib/cmdline');
const { Watchdog, GameClient } = require('../lib/watchdog');

const HELP = `rok-sim: fake Reign of Kings dedicated server (tests and CI only; it is not the game)

Run (in the server folder, like ROK.exe):
  rok-sim -batchmode -nographics -silentcrash [-logFile <path>] [-cport <port>] [--sim-* ...]

Simulator options (defaults in brackets):
${Object.keys(FLAG_MAP)
  .map((f) => `  ${f.padEnd(28)} [${JSON.stringify(SIM_DEFAULTS[FLAG_MAP[f][0]])}]`)
  .join('\n')}

Other commands:
  rok-sim query <host> <port> [info|players|rules]
  rok-sim console <port> [line ...]      (lines without "/" are chat; add --hold to stay connected)
  rok-sim make-exe <folder>              writes <folder>/ROK.exe, a script that runs this simulator
  rok-sim watchdog [ROK.exe args]        Server.exe stand-in: runs the simulator in this folder and
                                         starts it again --sim-relaunch-ms after every exit
                                         (prints "WATCHDOG <pid>" and "LAUNCH <pid>" lines)
  rok-sim client [port]                  game-client stand-in: holds UDP <port> (7350), no console
`;

async function runServer(argv) {
  let sim;
  try {
    sim = new RokSim({ argv, cwd: process.cwd() });
  } catch (e) {
    process.stderr.write(`rok-sim: ${e.message}\n`);
    process.exit(64);
  }
  sim.on('exit', (code) => process.exit(code));
  const stop = (sig) => {
    // A killed Windows process gets no chance to save: same here.
    process.stderr.write(`rok-sim: ${sig}, exiting without saving\n`);
    sim.crash(sig === 'SIGINT' ? 130 : 143);
  };
  process.on('SIGINT', () => stop('SIGINT'));
  process.on('SIGTERM', () => stop('SIGTERM'));
  sim.start();
}

async function runQuery([host, port, kind = 'info']) {
  if (!host || !port) throw new Error('usage: rok-sim query <host> <port> [info|players|rules]');
  const r = await A2S.query(host, Number(port), kind, { timeoutMs: 2000 });
  process.stdout.write(JSON.stringify(r, null, 2) + '\n');
  process.exit(r.ok ? 0 : 1);
}

function runConsole(args) {
  const hold = args.includes('--hold');
  const rest = args.filter((a) => a !== '--hold');
  const port = Number(rest.shift());
  if (!port) throw new Error('usage: rok-sim console <port> [line ...] [--hold]');
  const sock = net.connect({ host: '127.0.0.1', port });
  const reader = new P.PacketReader();
  let id = 1;
  const pending = [...rest];
  let waiting = null;
  const next = () => {
    if (!pending.length) {
      if (!hold) sock.end();
      return;
    }
    waiting = id++;
    sock.write(P.getPackets(waiting, P.TYPE.MESSAGE, pending.shift()));
  };
  sock.on('connect', next);
  sock.on('data', (chunk) => {
    for (const g of reader.push(chunk)) {
      const text = P.fromAscii(g.body);
      if (g.type === P.TYPE.MESSAGE && text) process.stdout.write(text + '\n');
      if (g.type === P.TYPE.DISCONNECT) process.stdout.write('<disconnect>\n');
      if (g.type === P.TYPE.RESPONSE && g.id === waiting) {
        if (text) process.stdout.write(`<response> ${text}\n`);
        next();
      }
    }
  });
  sock.on('error', (e) => {
    process.stderr.write(`rok-sim console: ${e.message}\n`);
    process.exit(1);
  });
  sock.on('close', () => process.exit(0));
}

function makeExe([folder]) {
  if (!folder) throw new Error('usage: rok-sim make-exe <folder>');
  fs.mkdirSync(folder, { recursive: true });
  const target = path.join(folder, 'ROK.exe');
  const me = path.resolve(__filename);
  // A text script, not a Windows program: it lets Linux tools that spawn "<folder>/ROK.exe" run the
  // simulator. It is not a game file and must never be shipped.
  fs.writeFileSync(target, `#!/usr/bin/env node\n// rok-sim shim (tests only, NOT the game). Runs ${me}\nprocess.argv.splice(1, 1, ${JSON.stringify(me)});\nrequire(${JSON.stringify(me)});\n`, { mode: 0o755 });
  process.stdout.write(target + '\n');
}

function runWatchdog(argv) {
  const { sim } = simOptions(argv);
  const w = new Watchdog({ cwd: process.cwd(), argv: argv.filter((a, i) => a !== '--sim-relaunch-ms' && argv[i - 1] !== '--sim-relaunch-ms' && !a.startsWith('--sim-relaunch-ms=')), relaunchMs: sim.relaunchMs });
  process.stdout.write(`WATCHDOG ${process.pid}\n`);
  w.on('launch', (l) => process.stdout.write(`LAUNCH ${l.pid}\n`));
  w.on('child-exit', (e) => process.stdout.write(`EXIT ${e.pid} ${e.code == null ? e.signal : e.code}\n`));
  const stop = () => w.stop().then(() => process.exit(0));
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);
  w.start();
}

async function runClient([port]) {
  const c = await new GameClient({ port: Number(port) || 7350 }).start();
  process.stdout.write(`CLIENT ${process.pid} UDP ${c.port}\n`);
  const stop = () => c.close().then(() => process.exit(0));
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);
}

async function main() {
  const argv = process.argv.slice(2);
  const sub = argv[0];
  if (sub === '--help' || sub === '-h' || sub === 'help') return process.stdout.write(HELP);
  if (sub === 'query') return runQuery(argv.slice(1));
  if (sub === 'console') return runConsole(argv.slice(1));
  if (sub === 'make-exe') return makeExe(argv.slice(1));
  if (sub === 'watchdog') return runWatchdog(argv.slice(1));
  if (sub === 'client') return runClient(argv.slice(1));
  return runServer(argv);
}

main().catch((e) => {
  process.stderr.write(`rok-sim: ${e.message}\n`);
  process.exit(64);
});
