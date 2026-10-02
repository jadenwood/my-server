'use strict';

// Command line handling, split the way the real process sees it:
//
// gameProps(argv): SystemUtil.CommandLineProps [DEC]. Every argument that starts with '-' becomes a
//   key without its first '-'; the next argument becomes its value unless it also starts with '-'.
//   Keys are case-sensitive. A repeated key makes Dictionary.Add throw ArgumentException.
//   (Arguments starting with '+' and bare words are ignored.)
// unityLogFile(argv): Unity's own -logFile handling ([SEC] Unity 5 player command line; the match is
//   case-insensitive in Unity). Missing -logFile -> <exe>_Data/output_log.txt.
// simOptions(argv): the simulator's own switches, all spelled "--sim-..." so that they never look
//   like game arguments to a reader of the command line (the game would see them as keys "-sim-...").

function gameProps(argv) {
  const props = new Map();
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith('-')) continue;
    const key = a.substring(1);
    let value = '';
    if (i + 1 < argv.length && !argv[i + 1].startsWith('-')) {
      value = argv[i + 1];
      i++;
    }
    if (props.has(key)) {
      const e = new Error('An element with the same key already exists in the dictionary.');
      e.exceptionType = 'ArgumentException';
      e.key = key;
      throw e;
    }
    props.set(key, value);
  }
  return props;
}

function unityLogFile(argv) {
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].toLowerCase() === '-logfile') {
      const v = argv[i + 1];
      if (v == null || v.startsWith('-')) return '-'; // no path given: stdout
      return v;
    }
  }
  return null;
}

// int.TryParse semantics: failure leaves 0.
function tryParseInt(s) {
  const t = String(s == null ? '' : s).trim();
  if (!/^[+-]?\d+$/.test(t)) return 0;
  const n = Number(t);
  return n >= -2147483648 && n <= 2147483647 ? n : 0;
}

const SIM_DEFAULTS = {
  bootMs: 400, // process start -> "Initialize engine version"
  loadMs: 1200, // engine up -> "Server for N players started" (the world load)
  keepAliveMs: 1000,
  cportWindowMs: 10000,
  frameMs: 16,
  exitDelayMs: 150,
  unityVersion: '5.1.2f1', // UNVERIFIED: only "Unity 5.1" is proven
  publicIp: '203.0.113.10', // documentation range (RFC 5737)
  steam: 'ok', // ok | init-fail | no-login | no-secure
  steamWaitMs: 30000, // SleepUntil(30, ...) before a no-login/no-secure failure
  a2sChallenge: true,
  a2sPlayers: 'count', // count | zero
  a2sFolder: '',
  a2sDrop: 0,
  chronicle: 'off', // off | on | demo
  chronicleEveryMs: 5000,
  stateRefreshMs: 30000,
  oxideData: null, // default <cwd>/oxide/data
  tornWrites: false,
  players: [], // names that join right after start
  scenario: null,
  simCommands: true,
  realmCourt: 'auto', // auto (RealmCourt.cs present in oxide/plugins) | on | off
  echoLogs: false,
  debugLogs: false,
  consoleHost: '127.0.0.1', // the game binds 0.0.0.0; the sim's console has no auth and takes /sim commands, so loopback unless asked
  firstRunExitCode: 0,
  crashExitCode: 1
};

const FLAG_MAP = {
  '--sim-boot-ms': ['bootMs', 'int'],
  '--sim-load-ms': ['loadMs', 'int'],
  '--sim-keepalive-ms': ['keepAliveMs', 'int'],
  '--sim-cport-window-ms': ['cportWindowMs', 'int'],
  '--sim-frame-ms': ['frameMs', 'int'],
  '--sim-unity-version': ['unityVersion', 'str'],
  '--sim-public-ip': ['publicIp', 'str'],
  '--sim-steam': ['steam', 'str'],
  '--sim-steam-wait-ms': ['steamWaitMs', 'int'],
  '--sim-a2s-challenge': ['a2sChallenge', 'bool'],
  '--sim-a2s-players': ['a2sPlayers', 'str'],
  '--sim-a2s-folder': ['a2sFolder', 'str'],
  '--sim-a2s-drop': ['a2sDrop', 'num'],
  '--sim-chronicle': ['chronicle', 'str'],
  '--sim-chronicle-every-ms': ['chronicleEveryMs', 'int'],
  '--sim-state-refresh-ms': ['stateRefreshMs', 'int'],
  '--sim-oxide-data': ['oxideData', 'str'],
  '--sim-torn-writes': ['tornWrites', 'flag'],
  '--sim-players': ['players', 'list'],
  '--sim-scenario': ['scenario', 'str'],
  '--sim-commands': ['simCommands', 'bool'],
  '--sim-realm-court': ['realmCourt', 'str'],
  '--sim-echo-logs': ['echoLogs', 'flag'],
  '--sim-debug-logs': ['debugLogs', 'flag'],
  '--sim-console-host': ['consoleHost', 'str'],
  '--sim-first-run-exit-code': ['firstRunExitCode', 'int'],
  '--sim-crash-exit-code': ['crashExitCode', 'int']
};

const ENUMS = {
  steam: ['ok', 'init-fail', 'no-login', 'no-secure'],
  a2sPlayers: ['count', 'zero'],
  chronicle: ['off', 'on', 'demo'],
  realmCourt: ['auto', 'on', 'off']
};

// Returns { sim, rest } where rest are the arguments the "game" sees. Throws on a bad sim flag.
function simOptions(argv) {
  const sim = { ...SIM_DEFAULTS, players: [] };
  const rest = [];
  for (let i = 0; i < argv.length; i++) {
    let a = argv[i];
    let inline;
    if (a.startsWith('--sim-') && a.includes('=')) [a, inline] = [a.slice(0, a.indexOf('=')), a.slice(a.indexOf('=') + 1)];
    const spec = FLAG_MAP[a];
    if (!spec) {
      if (a.startsWith('--sim-')) throw new Error(`unknown simulator option ${a}`);
      rest.push(argv[i]);
      continue;
    }
    const [key, kind] = spec;
    if (kind === 'flag') {
      sim[key] = inline == null ? true : !/^(0|false|off|no)$/i.test(inline);
      continue;
    }
    const v = inline != null ? inline : argv[++i];
    if (v == null) throw new Error(`${a} needs a value`);
    if (kind === 'int' || kind === 'num') {
      const n = Number(v);
      if (!Number.isFinite(n) || (kind === 'int' && !Number.isInteger(n))) throw new Error(`${a} needs a number, got "${v}"`);
      sim[key] = n;
    } else if (kind === 'bool') sim[key] = !/^(0|false|off|no)$/i.test(v);
    else if (kind === 'list') sim[key] = v.split(',').map((s) => s.trim()).filter(Boolean);
    else sim[key] = v;
    if (ENUMS[key] && !ENUMS[key].includes(sim[key])) throw new Error(`${a} must be one of ${ENUMS[key].join(', ')}`);
  }
  return { sim, rest };
}

module.exports = { gameProps, unityLogFile, tryParseInt, simOptions, SIM_DEFAULTS, FLAG_MAP };
