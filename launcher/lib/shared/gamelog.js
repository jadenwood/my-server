'use strict';

// Connection Doctor, shared by both editions: where Reign of Kings writes its logs, how to read
// them safely, and what the known failure lines mean in plain English. Read-only: nothing here
// writes, moves or deletes a game or server file. No child_process (the player edition test
// forbids it outside lib/shared/steam.js).
//
// WHERE THE LOGS ARE (evidence tags as in docs/join-and-scale.md):
//
// 1. The game's own logger. [DEC] CodeHatch...Debugging.Logger: LogToFile defaults to true and
//    CreateNewLogFile() opens  Logs/Log[<yyMMdd-hhmmss>].txt  (a path relative to the process
//    working directory; "hh" is a 12-hour clock). Every Logger.Info/Warning/Error line is written as
//    "[yyMMdd-HHmmss] [Error]  <message>" followed by "Context:" and stack-trace lines.
//    Important: while LogToFile is true, LogWithStacktrace() writes to that file INSTEAD of Unity's
//    Debug.Log, so these messages are NOT in Unity's log (output_log.txt, or the server's
//    -logFile Logs\realm-server.log). That is why the server's errors seemed "not logged".
//    - Server: <server>\Logs\Log[...].txt (Realm starts ROK.exe with the server folder as cwd).
//    - Client: <game install>\Logs\Log[...].txt. UNVERIFIED that Steam/EAC start ROK.exe with the
//      install folder as working directory; it is Steam's normal behaviour.
// 2. Unity's player log. [SEC] Unity 5.x standalone writes <exe>_Data\output_log.txt, so the client
//    has <game install>\ROK_Data\output_log.txt. It carries Unity-native lines such as the
//    Steamworks.NET "SteamAPI_Init() failed" error ([DEC] SteamManager uses Debug.LogError) and
//    exceptions. The server's equivalent is the -logFile path, <server>\Logs\realm-server.log.
//
// WHAT IS NOT IN ANY LOG: the client shows most join refusals only as a popup
// ([DEC] CoreServer.OnConnectionLost -> GetConnectionError -> ShowPopup, which does not log). The
// server logs the matching refusal ("Player X denied connection because ..."), and the Doctor lets
// the player paste the popup text, so both routes are classified by the same rules.

const fsp = require('fs/promises');
const path = require('path');

// ---------------------------------------------------------------- the rules

// Each rule: id, severity ('bad' stops a join, 'warn' may, 'info' is a useful fact, 'ok' is good
// news), where it appears, the exact strings from the DLL, a plain-English cause and one fix.
// `explain(m)` may refine cause/fix from the captured text.
const RULES = [
  {
    id: 'address-has-port',
    severity: 'bad',
    title: 'The address box contains a port',
    re: /Unable to resolve host name\.?\s*\(([^)\s]*:\d{1,5})\)|Joining (\S+:\d{1,5}) : \d+/i,
    evidence: '[DEC] Game.Join: End("Unable to resolve host name. (" + ip + ")"); Logger "Joining {0} : {1}"',
    cause: 'The game was given "address:port" in its address field. It looks the whole text up as a host name, which fails.',
    fix: 'Type only the address (for example 127.0.0.1) in the address box and the port (for example 7350) in the separate port box. Realm\'s Copy button copies the address only.'
  },
  {
    id: 'host-not-found',
    severity: 'bad',
    title: 'The server address could not be found',
    re: /Unable to resolve host name\.?\s*\(([^)]*)\)/i,
    evidence: '[DEC] Game.Join -> NetworkUtil.GetIpAddress (Dns.GetHostAddresses) returned null',
    cause: 'The address is misspelled, has a space in it, or the DNS name does not exist (yet).',
    fix: 'Copy the address again from Realm (no spaces, no port), or use the plain IP address.',
    explain(m) {
      const host = String(m[1] || '').trim();
      if (!host) return { cause: 'The address box was empty.', fix: 'Paste the server address into the address box, then the port into the port box.' };
      if (/\s/.test(host)) return { cause: `The address "${host}" contains a space.`, fix: 'Remove the spaces. Paste the address only.' };
      return null;
    }
  },
  {
    id: 'bad-port',
    severity: 'bad',
    title: 'The port number is not valid',
    re: /Invalid port value\. Disconnecting from current game/i,
    evidence: '[DEC] Game.Join: port > 65535 || port <= 0',
    cause: 'The port box is empty, zero, or above 65535.',
    fix: 'Enter the game port shown in Realm (7350 for Server 1).'
  },
  {
    id: 'cannot-connect',
    severity: 'bad',
    title: 'The game could not open a connection',
    re: /Unable to connect to '([^']*)'\./i,
    evidence: '[DEC] Game.Join: SocketException -> End("Unable to connect to \'{0}\'.")',
    cause: 'Windows refused the connection attempt before it reached the server (bad address or no network).',
    fix: 'Check the address, and that this PC is online. For a server on this PC use 127.0.0.1.'
  },
  {
    id: 'timeout',
    severity: 'bad',
    title: 'The server did not answer (timeout)',
    re: /Connection timed out\.|Took too long to connect to server|\bConnection ?Timeout\b|Server Authentication Timeout/i,
    evidence: '[DEC] CoreServer.GetConnectionError: ConnectionTimeout "Connection timed out.", ServerAuthenticationTimeout "Took too long to connect to server."',
    cause: 'Nothing answered on that address and UDP port: the server is not running or still loading, the port is wrong, or a firewall/router drops the traffic.',
    fix: 'Run the Doctor checks for the server. On the server PC itself join 127.0.0.1; on the same home network use the server\'s LAN address (most routers cannot loop back to their own public IP).'
  },
  {
    id: 'lost-connection',
    severity: 'warn',
    title: 'The connection dropped',
    re: /Lost connection to the server\./i,
    evidence: '[DEC] CoreServer.OnConnectionLost',
    cause: 'The server stopped, restarted, or the network dropped packets.',
    fix: 'Check that the server is still running, then join again.'
  },
  {
    id: 'eac-not-launched',
    severity: 'bad',
    title: 'The game was not started through Easy Anti-Cheat',
    re: /Please run the game from Reign Of Kings\.exe/i,
    evidence: '[DEC] Game.Join / EACIntegration.Awake: !Runtime.IsActive()',
    cause: 'ROK.exe was started directly. Joining only works when Steam starts the game through its EAC launcher.',
    fix: 'Close the game and press Play in Steam (or Join in Realm). Never start ROK.exe from the game folder.'
  },
  {
    id: 'eac-not-ready',
    severity: 'bad',
    title: 'Easy Anti-Cheat was not ready yet',
    re: /EAC has not (?:intiailized|initialized) yet\. Please try again|EAC has not initialized yet but we are trying/i,
    evidence: '[DEC] Game.Join: !Runtime.Initialized -> "EAC has not intiailized yet. Please try again." (the typo is in the game)',
    cause: 'The join started before EAC finished loading. A quick join right at start-up can hit this once; the game does not retry.',
    fix: 'Wait a few seconds at the main menu and join again. If it keeps happening, verify the game files in Steam.'
  },
  {
    id: 'eac-kick',
    severity: 'bad',
    title: 'Easy Anti-Cheat removed the player',
    re: /User banned by EAC|Banned By EAC|Disconnected By EAC|EAC Violation|Kicking client \S+ with connectionId: \S+ \| cause = (.*)/i,
    evidence: '[DEC] EACIntegration.OnClientStatusChange; ConnectionError BannedByEAC/DisconnectedByEAC/EACViolation',
    cause: 'EAC reported a problem with this game client (integrity check failed, or an EAC ban).',
    fix: 'Verify the game files in Steam and close overlays or tools that inject into games. The "eac_usermode blocked from loading into LSA" Windows notice is harmless and is not the cause.'
  },
  {
    id: 'steam-not-signed-in',
    severity: 'bad',
    title: 'Steam is not running or not signed in',
    re: /You need to be logged into steam in order to join a game|Unable to communicate with the steam app|SteamAPI_Init\(\) failed|Could not authenticate steam/i,
    evidence: '[DEC] Game.JoinWithEndpoint: !SteamManager.Initialized; SteamManager: "[Steamworks.NET] SteamAPI_Init() failed"; GetConnectionError AuthenticationFailed',
    cause: 'The game needs a running, signed-in Steam to create the join ticket, and the server checks it with Steam.',
    fix: 'Start Steam, sign in (not offline mode), then start the game from Steam.'
  },
  {
    id: 'version-mismatch',
    severity: 'bad',
    title: 'Game version does not match the server',
    re: /The server is not running the same version as you|denied connection due to wrong version|Incompatible ?Versions/i,
    evidence: '[DEC] CoreServer.ProcessConnectionRequest: loginData.Version != GameInfo.Version',
    cause: 'The game client and the dedicated server are different builds.',
    fix: 'Let Steam update both: the game (app 344760) and the dedicated server (app 381690), then make a fresh Realm test copy of the server.'
  },
  {
    id: 'server-full',
    severity: 'warn',
    title: 'The server is full',
    re: /Server is full\.|Too ?Many ?Connected ?Players|\bLimited ?Players\b/i,
    evidence: '[DEC] GetConnectionError TooManyConnectedPlayers "Server is full."; CoreServer.PlayerShouldBeQueued',
    cause: 'All slots are taken.',
    fix: 'Use "Join best server" in Realm, or wait in the queue. Owners: raise maxPlayers (Realm allows up to 120).'
  },
  {
    id: 'queued',
    severity: 'info',
    title: 'Waiting in the join queue',
    re: /Waiting in queue\.\.\. Position (\d+)\/(\d+)/i,
    evidence: '[DEC] CoreServer.OnPlayerQueueChange (loading-screen text, not logged)',
    cause: 'The server lets one player in every timeBetweenPlayerJoin seconds (default 10), or it is full.',
    fix: 'Stay on the loading screen; the position counts down. Owners: lower timeBetweenPlayerJoin for busy evenings.',
    explain(m) {
      return { title: `Waiting in the join queue (position ${m[1]} of ${m[2]})` };
    }
  },
  {
    id: 'server-loading',
    severity: 'warn',
    title: 'The server is still loading',
    re: /Server is not fully loaded yet\.|\bServer ?Not ?Ready\b/i,
    evidence: '[DEC] GetConnectionError ServerNotReady',
    cause: 'The server process is up but the world has not finished loading.',
    fix: 'Wait until the server log shows "Server for N players started on port P." and join again.'
  },
  {
    id: 'banned',
    severity: 'bad',
    title: 'This Steam account is banned on the server',
    re: /You are banned from this server\.|denied connection because they were banned|You were banned|was banned from the server|\bConnection ?Banned\b/i,
    evidence: '[DEC] CoreServer.ProcessConnectionRequest IdIsBanned; PlayerListener.OnPlayerBan',
    cause: 'The server\'s ban list contains this Steam account.',
    fix: 'Ask the server owner. Owners: /banlist and /unban in the server console.'
  },
  {
    id: 'not-whitelisted',
    severity: 'bad',
    title: 'Not on the server whitelist',
    re: /You are not on the server whitelist|not on the active whitelist|\bNot ?In ?Whitelist\b/i,
    evidence: '[DEC] CoreServer.ProcessConnectionRequest UserWhitelist',
    cause: 'The server only admits whitelisted Steam accounts.',
    fix: 'Ask the owner to add your Steam account to the whitelist.'
  },
  {
    id: 'wrong-password',
    severity: 'bad',
    title: 'Wrong server password',
    re: /Incorrect password\.|password provided was invalid|\bInvalid ?Password\b/i,
    evidence: '[DEC] CoreServer.ProcessConnectionRequest: loginData.Password != Data.Password',
    cause: 'The password typed does not match the server\'s password setting.',
    fix: 'Re-type the password (it is case-sensitive). Owners: check password in ServerSettings.cfg; an empty value means no password.'
  },
  {
    id: 'ping-port-blocked',
    severity: 'bad',
    title: 'Kicked: the ping check could not connect (TCP)',
    re: /failing to initialize with the ping system|No players are connected to the ping system on port (\d+)|Failed to complete ping initialization/i,
    evidence: '[DEC] PingGraphManager: Kick "You have been disconnected for failing to initialize with the ping system." (enablePingLimit)',
    cause: 'With enablePingLimit on, the client must also reach the server on TCP (pingPort, same number as the game port). UDP got through but TCP did not.',
    fix: 'Owners: allow and forward TCP on the game port as well as UDP (Realm\'s firewall rules include it), or set enablePingLimit = \'False\'.'
  },
  {
    id: 'ping-unreliable',
    severity: 'warn',
    title: 'Kicked: unstable connection to the ping check',
    re: /providing an unreliable connection to the ping system/i,
    evidence: '[DEC] PingGraphManager: GUI.Ping.Dialogues.Unreliable',
    cause: 'The TCP ping connection kept dropping.',
    fix: 'Use a wired connection or close downloads. Owners can turn enablePingLimit off.'
  },
  {
    id: 'ping-limit',
    severity: 'warn',
    title: 'Kicked: ping above the server\'s limit',
    re: /exceeding the ping limit|at risk of being kicked from this server for high latency/i,
    evidence: '[DEC] PingGraphManager: GUI.Ping.Dialogues.Disconnect; ServerSettings pingLimit (50..10000 ms, default 200)',
    cause: 'The average ping was above pingLimit.',
    fix: 'Pick a closer server. Owners: raise pingLimit or set enablePingLimit = \'False\'.'
  },
  {
    id: 'duplicate-login',
    severity: 'warn',
    title: 'Same Steam account joined twice',
    re: /Player logged in with the same steam id|Player connected to a server that is using the same steam id|Already ?Connected ?To ?Another ?Server|Detected ?Duplicate ?Player ?ID/i,
    evidence: '[DEC] CoreServer.ProcessConnectionRequest',
    cause: 'This Steam account is already on the server (an old session, or a second PC).',
    fix: 'Close the other game, wait a minute for the old session to time out, then join again.'
  },
  {
    id: 'kicked',
    severity: 'warn',
    title: 'Kicked from the server',
    re: /You were kicked\.|was kicked from the server(?: because (.*?))?\.?$/i,
    evidence: '[DEC] CoreServer.Kick; PlayerListener.OnPlayerKick',
    cause: 'An admin, a plugin or the server removed the player.',
    fix: 'Read the reason in the message. Ask the owner if it is unclear.',
    explain(m) {
      return m[1] ? { cause: `Reason given: ${m[1].slice(0, 200)}` } : null;
    }
  },
  {
    id: 'first-run-exit',
    severity: 'bad',
    title: 'The server stopped after its first run (normal once)',
    re: /This is the first time you have run this server\./i,
    evidence: '[DEC] CoreServer: SettingsFile.FirstCreate -> Program.Exit()',
    cause: 'A brand-new server folder writes its Configuration\\ files and exits on purpose.',
    fix: 'Start the server again. Realm then applies its settings; restart once more if Realm says so.'
  },
  {
    id: 'steam-auth-game-mismatch',
    severity: 'bad',
    title: 'Steam login rejected: the server is registered as the wrong Steam app',
    re: /Failed to authenticate .*k_EBeginAuthSessionResultGameMismatch/i,
    evidence: '[LOG] seen on the owner\'s server; SteamGameServer registered under app 381690 while players hold 344760 tickets',
    cause: 'The server was started without SteamAppId=344760, so Steam ties it to the dedicated-server tool (381690) and refuses every player\'s Reign of Kings login.',
    fix: 'Update Realm Steward (it now starts the server with SteamAppId=344760) and restart the server. Without Steward: set the server folder\'s steam_appid.txt to 344760.'
  },
  {
    id: 'steam-auth-failed',
    severity: 'bad',
    title: 'A player failed Steam authentication',
    re: /Failed to authenticate (.+?) with Steam\. \((k_\w+)\)/i,
    evidence: '[LOG] CoreServer.WaitForSteamAuthenicateThenApprove',
    cause: 'Steam did not accept the player\'s login ticket for this server.',
    fix: 'Check the reason code in the line. The player should restart Steam and the game; make sure the server is not in Steam offline mode.'
  },
  {
    id: 'game-port-taken',
    severity: 'bad',
    title: 'The game port is used by another program',
    re: /The port (\d+) is already being used by another application\./i,
    evidence: '[DEC] CoreServer: Network.InitializeServer returned -2',
    cause: 'Another server already holds that UDP port. Most often it is an older ROK.exe from this same folder that is still running (for example after Start was pressed twice).',
    fix: 'Open Task Manager > Details, end every ROK.exe (and Server.exe) from your server folder, then press Start once. If it is a different program, give this instance its own ports on the Servers screen.'
  },
  {
    id: 'server-start-error',
    severity: 'bad',
    title: 'The server could not start its network',
    re: /Could not start the server because an error occured\.\s*\(([^)]*)\)/i,
    evidence: '[DEC] CoreServer: Network.InitializeServer error',
    cause: 'uLink could not open the game socket (bindIP not on this PC, or a security product blocked it).',
    fix: 'Set Network to "This PC only" or "Public" in Realm (bindIP 127.0.0.1 or 0.0.0.0) and start again.'
  },
  {
    id: 'steam-server-failed',
    severity: 'warn',
    title: 'Steam server registration failed',
    re: /The Steam game server could not initialize|Port already in use\. Make sure you have allowed port (\d+)|could not login anonymously|could not be started in secure mode/i,
    evidence: '[DEC] SteamServer.StartServer (server keeps running without Steam auth)',
    cause: 'The Steam query port (steamAuthPort) is taken, or Steam\'s servers were unreachable. The server then runs without Steam auth and does not answer status queries.',
    fix: 'Give every instance its own steamAuthPort (Realm does: 27015, 27025, ...), stop other servers using it, then restart. UNVERIFIED: whether players can join in this state.'
  },
  {
    id: 'level-missing',
    severity: 'bad',
    title: 'The server uses a map this client does not have',
    re: /Unable to join a server because the level it is using does not exist|Could not load the game level/i,
    evidence: '[DEC] CoreServer / LevelLoader',
    cause: 'Client files are incomplete or the server runs a different map/build.',
    fix: 'Verify the game files in Steam.'
  },
  {
    id: 'connect-error-code',
    severity: 'bad',
    title: 'Connection refused by the network layer',
    re: /Could not connect to the server\. \((-?\d+), ([^)]+)\)/i,
    evidence: '[DEC] CoreServer.GetConnectionError default branch (uLink ConnectionError)',
    cause: 'uLink reported a connection error.',
    fix: 'Run the Doctor checks; share the report with the server owner.',
    explain(m) {
      return { title: `Connection refused: ${m[2].trim()} (code ${m[1]})` };
    }
  },
  {
    id: 'quickjoin-args',
    severity: 'info',
    title: 'Quick join arguments reached the game',
    re: /Attempting join game: (\S+):(\d+) with character/i,
    evidence: '[DEC] QuickJoinBypass.Start',
    cause: 'The game received -ip/-port from Steam, so Realm\'s one-click join path works on this PC.',
    fix: ''
  },
  {
    id: 'connecting',
    severity: 'info',
    title: 'The game started connecting',
    re: /Connecting to '([^']+)'\./i,
    evidence: '[DEC] Game.JoinWithEndpoint',
    cause: 'The address resolved and the game sent its join request.',
    fix: ''
  },
  {
    id: 'server-ready',
    severity: 'ok',
    title: 'Server is listening',
    re: /Server for (\d+) players started on port (\d+)\./i,
    evidence: '[DEC] CoreServer: after Network.InitializeServer',
    cause: 'The game server finished loading and is accepting players.',
    fix: '',
    explain(m) {
      return { title: `Server is listening on port ${m[2]} (${m[1]} slots)` };
    }
  },
  {
    id: 'steam-server-ok',
    severity: 'ok',
    title: 'Steam server registration worked',
    re: /Steam game server started\. \(IP: [^,]+, Logged: True, Secure: True\)/i,
    evidence: '[DEC] SteamServer.StartServer',
    cause: 'Steam auth and status queries are on.',
    fix: ''
  },
  {
    id: 'player-joined',
    severity: 'ok',
    title: 'A player joined',
    re: /Authentication verified for (.+?) \(\d+\)\./i,
    evidence: '[DEC] CoreServer (Steam auth callback)',
    cause: 'A join made it through Steam auth.',
    fix: ''
  },
  {
    id: 'admin-console',
    severity: 'info',
    title: 'Admin console is enabled',
    re: /Admin console enabled\./i,
    evidence: '[DEC] SocketAdminConsole.OnEnable (TCP 11000, no password)',
    cause: 'The game\'s admin console listens on TCP 11000 on all network cards.',
    fix: 'Never forward TCP 11000-11003. Realm\'s firewall rules block them inbound.'
  }
];

const SEVERITY_RANK = { bad: 4, warn: 3, info: 2, ok: 1 };

// Classifies one line of text. Returns the first matching rule's finding, or null.
// (Rule order matters: specific rules sit above general ones.)
function classify(text) {
  const s = String(text == null ? '' : text);
  if (!s.trim()) return null;
  for (const r of RULES) {
    const m = r.re.exec(s);
    if (!m) continue;
    const extra = (r.explain && r.explain(m)) || {};
    return {
      id: r.id,
      severity: r.severity,
      title: extra.title || r.title,
      cause: extra.cause || r.cause,
      fix: extra.fix != null ? extra.fix : r.fix,
      evidence: r.evidence,
      match: m[0].slice(0, 300)
    };
  }
  return null;
}

// ---------------------------------------------------------------- reading log text

// [DEC] Logger file format: "[yyMMdd-HHmmss] [Error]  message". The admin console prefixes
// "[D] [I] [W] [E]"; Oxide writes "[Oxide] HH:mm [Info] ...".
const LOGGER_LINE = /^\[(\d{6})-(\d{6})\]\s+\[(Debug|Info|Warn|Error|Except|Assert)\]\s+(.*)$/;

function parseLine(raw) {
  const line = String(raw == null ? '' : raw).replace(/\r$/, '');
  const m = LOGGER_LINE.exec(line);
  if (m) {
    const d = m[1];
    const t = m[2];
    const time = `20${d.slice(0, 2)}-${d.slice(2, 4)}-${d.slice(4, 6)} ${t.slice(0, 2)}:${t.slice(2, 4)}:${t.slice(4, 6)}`;
    const lv = m[3].toLowerCase();
    return { time, level: lv === 'warn' ? 'warning' : lv === 'except' ? 'error' : lv, text: m[4] };
  }
  const c = /^\[([DIWE])\]\s?(.*)$/.exec(line);
  if (c) return { time: null, level: { D: 'debug', I: 'info', W: 'warning', E: 'error' }[c[1]], text: c[2] };
  return { time: null, level: null, text: line };
}

// Scans a block of log text (or an array of lines). Returns { lines, findings }.
// lines: [{ n, text, level, finding }] for the last `keep` lines; findings: one entry per rule id,
// most recent occurrence first, with a count.
function scan(input, { source = 'log', keep = 400 } = {}) {
  const raw = Array.isArray(input) ? input.map((l) => (l && typeof l === 'object' ? l.text : l)) : String(input || '').split(/\r?\n/);
  const byId = new Map();
  const out = [];
  const start = Math.max(0, raw.length - keep);
  for (let i = 0; i < raw.length; i++) {
    const p = parseLine(raw[i]);
    if (!p.text.trim()) continue;
    const f = classify(p.text);
    if (f) {
      const prev = byId.get(f.id);
      byId.set(f.id, { ...f, source, line: i + 1, time: p.time, count: prev ? prev.count + 1 : 1 });
    }
    if (i >= start) out.push({ n: i + 1, text: p.text.slice(0, 2000), level: p.level, finding: f ? { id: f.id, severity: f.severity } : null });
  }
  const findings = [...byId.values()].sort((a, b) => b.line - a.line);
  return { lines: out, findings };
}

// Picks the one finding that best explains a failure: the newest "bad", else the newest "warn".
// resolvedBy: ids of good-news lines (e.g. 'connecting'); a failure older than the newest of them
// was fixed since and is ignored.
function verdict(findings, { resolvedBy = [] } = {}) {
  const list = Array.isArray(findings) ? findings : [];
  const since = Math.max(-1, ...list.filter((f) => resolvedBy.includes(f.id)).map((f) => f.line));
  const worst = list.filter((f) => (f.severity === 'bad' || f.severity === 'warn') && f.line > since).sort((a, b) => SEVERITY_RANK[b.severity] - SEVERITY_RANK[a.severity] || b.line - a.line);
  return worst[0] || null;
}

// ---------------------------------------------------------------- log locations

const LOGGER_FILE_RE = /^Log\[\d{6}-\d{6}\]\.txt$/i;

// Reads appmanifest_344760.acf's "installdir" (Steam's folder name under steamapps\common).
function installDirFromAcf(text, parseVdf) {
  try {
    const doc = parseVdf(text);
    const key = Object.keys(doc).find((k) => /^appstate$/i.test(k));
    const st = key ? doc[key] : null;
    const dir = st && typeof st.installdir === 'string' ? st.installdir : null;
    return dir && !/[\\/]|\.\./.test(dir) ? dir : null;
  } catch {
    return null;
  }
}

// The client install folder from detectGameInstall()'s library, or null.
async function findGameDir(install, { appId = 344760, readFile = (p) => fsp.readFile(p, 'utf8'), parseVdf, P = path.win32 } = {}) {
  if (!install || !install.library) return null;
  const lib = String(install.library).replace(/\//g, '\\');
  let dir = 'Reign Of Kings';
  if (parseVdf) {
    try {
      dir = installDirFromAcf(await readFile(P.join(lib, 'steamapps', `appmanifest_${appId}.acf`)), parseVdf) || dir;
    } catch {
      /* fall back to Steam's usual folder name */
    }
  }
  return P.join(lib, 'steamapps', 'common', dir);
}

// Candidate client log files, most useful first. Each { file|dir, kind, label, evidence }.
function clientLogCandidates(gameDir, { P = path.win32 } = {}) {
  if (!gameDir) return [];
  return [
    { dir: P.join(gameDir, 'Logs'), match: 'logger', kind: 'client', label: 'Game log (Logs\\Log[...].txt)', evidence: '[DEC] Logger.CreateNewLogFile; UNVERIFIED working directory' },
    { file: P.join(gameDir, 'ROK_Data', 'output_log.txt'), kind: 'client', label: 'Unity log (ROK_Data\\output_log.txt)', evidence: '[SEC] Unity 5.x player log location' }
  ];
}

// Candidate server log files for one test copy.
function serverLogCandidates(root, { P = path } = {}) {
  if (!root) return [];
  return [
    { dir: P.join(root, 'Logs'), match: 'logger', kind: 'server', label: 'Server game log (Logs\\Log[...].txt)', evidence: '[DEC] Logger.CreateNewLogFile' },
    { file: P.join(root, 'Logs', 'realm-server.log'), kind: 'server', label: 'Server Unity log (Logs\\realm-server.log)', evidence: 'Realm starts ROK.exe with -logFile here' }
  ];
}

// Resolves candidates to existing files: { path, kind, label, evidence, size, mtimeMs }.
// For a "logger" folder only the newest Log[...].txt is returned.
async function resolveLogs(candidates, { stat = (p) => fsp.stat(p), readdir = (p) => fsp.readdir(p), P = path } = {}) {
  const out = [];
  for (const c of candidates) {
    try {
      if (c.file) {
        const st = await stat(c.file);
        if (st.isFile()) out.push({ path: c.file, kind: c.kind, label: c.label, evidence: c.evidence, size: st.size, mtimeMs: st.mtimeMs });
        continue;
      }
      let newest = null;
      for (const n of await readdir(c.dir)) {
        if (!LOGGER_FILE_RE.test(n)) continue;
        const p = P.join(c.dir, n);
        const st = await stat(p);
        if (st.isFile() && (!newest || st.mtimeMs > newest.mtimeMs)) newest = { path: p, size: st.size, mtimeMs: st.mtimeMs, name: n };
      }
      if (newest) out.push({ path: newest.path, kind: c.kind, label: c.label.replace('Log[...].txt', newest.name), evidence: c.evidence, size: newest.size, mtimeMs: newest.mtimeMs });
    } catch {
      /* not there */
    }
  }
  return out;
}

const MAX_READ = 512 * 1024;

// Reads a file from `offset` (or its last `tail` bytes when offset is null). Never more than 512 KB.
// Returns { text, offset (new end), size, reset } ; reset is true when the file shrank (rotated).
async function readFrom(file, offset = null, { tail = 128 * 1024 } = {}) {
  const st = await fsp.stat(file);
  let from = offset == null ? Math.max(0, st.size - tail) : offset;
  let reset = false;
  if (from > st.size) {
    from = 0;
    reset = true;
  }
  const len = Math.min(st.size - from, MAX_READ);
  if (len <= 0) return { text: '', offset: from, size: st.size, reset };
  const fh = await fsp.open(file, 'r');
  try {
    const buf = Buffer.alloc(len);
    await fh.read(buf, 0, len, from);
    let text = buf.toString('utf8');
    // When starting mid-file, drop the partial first line.
    if (offset == null && from > 0) text = text.slice(text.indexOf('\n') + 1);
    return { text, offset: from + len, size: st.size, reset };
  } finally {
    await fh.close();
  }
}

// Only paths returned by resolveLogs() may be read through IPC: the renderer passes an index, never a path.
function isLogPathAllowed(p, allowed) {
  return typeof p === 'string' && Array.isArray(allowed) && allowed.some((a) => a.path === p);
}

// ---------------------------------------------------------------- report redaction

function isPrivateOrLocal(ip) {
  const p = ip.split('.').map(Number);
  if (p.length !== 4 || p.some((n) => !Number.isInteger(n) || n > 255)) return true; // not an address
  return p[0] === 10 || p[0] === 127 || p[0] === 0 || p[0] >= 224 || (p[0] === 172 && p[1] >= 16 && p[1] <= 31) || (p[0] === 192 && p[1] === 168) || (p[0] === 169 && p[1] === 254);
}

// Removes what should not leave the PC in a shared report:
// - public IPv4 addresses (private/LAN and loopback stay: they help and identify nobody), and any
//   extra addresses passed in (e.g. the server's public IP from the Steam line);
// - SteamID64 values (17 digits starting 7656119) and Steam IDs in "Id: <n>" login data;
// - the Steam auth ticket the client logs in "[Login Data] ... Auth:<bytes>";
// - passwords (-pass <pw>, password = '<pw>', rConPassword);
// - the Windows/Linux user name inside profile paths.
function redact(text, { publicIps = [] } = {}) {
  let s = String(text == null ? '' : text);
  for (const ip of publicIps) {
    if (typeof ip === 'string' && /^\d{1,3}(\.\d{1,3}){3}$/.test(ip)) s = s.split(ip).join('[public-ip]');
  }
  s = s.replace(/Auth:[^\r\n]*/g, 'Auth:[removed]');
  s = s.replace(/\b7656119\d{10}\b/g, '[steam-id]');
  s = s.replace(/(\bId:\s*)\d{5,20}\b/g, '$1[steam-id]');
  // Command-line passwords, plain or URL-encoded (steam://run/...//-pass%20secret), quoted or not.
  s = s.replace(/((?:^|[\s"'/]|%20)[-+](?:pass|password)(?:\s+|%20|=))(?:"[^"\r\n]*"|'[^'\r\n]*'|(?:(?!%20)[^\s"'&])+)/gi, '$1[removed]');
  // Config / JSON / log style: password = 'x', "password": "x", ServerPassword=x, rConPassword: x.
  s = s.replace(/((?:rCon|Server|Admin)?[Pp]ass(?:word)?"?\s*[:=]\s*)(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;}\r\n]+)/g, (all, pre) => `${pre}[removed]`);
  s = s.replace(/([A-Za-z]:[\\/]Users[\\/])[^\\/\r\n]+/gi, '$1[user]');
  s = s.replace(/(\/(?:home|Users)\/)[^/\s]+/g, '$1[user]');
  // Global IPv6 addresses (2000::/3); link-local, ULA and ::1 stay.
  s = s.replace(/(^|[^0-9A-Fa-f:])([23][0-9A-Fa-f]{3}:[0-9A-Fa-f:]{2,}[0-9A-Fa-f])(?![0-9A-Fa-f:])/g, (all, pre, ip) => (ip.split(':').length >= 3 ? `${pre}[public-ip]` : all));
  s = s.replace(/(^|%20|[^\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.]*\d)/g, (all, pre, ip) => (isPrivateOrLocal(ip) ? all : `${pre}[public-ip]`));
  return s;
}

const MARK = { ok: 'OK  ', warn: 'WARN', bad: 'FAIL', skip: 'SKIP', info: 'INFO' };

// Plain-text report for "Copy report". Everything goes through redact() at the end.
function buildReport({ app = 'Realm', version = '', platform = process.platform, title = 'Connection Doctor', target = '', verdict: v = null, steps = [], findings = [], logs = [], pasted = '' } = {}, { publicIps = [] } = {}) {
  const out = [];
  out.push(`${app} ${title} report`);
  out.push(`App: ${app} ${version} on ${platform}`);
  out.push(`Time: ${new Date().toISOString()}`);
  if (target) out.push(`Checked: ${target}`);
  if (v) out.push(`Verdict: ${v.title}${v.fix ? ` -> ${v.fix}` : ''}`);
  if (steps.length) {
    out.push('', 'Checks:');
    for (const st of steps) {
      out.push(`[${MARK[st.status] || st.status}] ${st.label}: ${st.detail || ''}`);
      if (st.fix && (st.status === 'bad' || st.status === 'warn')) out.push(`       Fix: ${st.fix}`);
    }
  }
  if (findings.length) {
    out.push('', 'Known messages found in the logs:');
    for (const f of findings.slice(0, 20)) {
      out.push(`[${MARK[f.severity] || f.severity}] ${f.title}${f.count > 1 ? ` (x${f.count})` : ''} [${f.source || 'log'}${f.time ? ' ' + f.time : ''}]`);
      out.push(`       Line: ${f.match}`);
      if (f.fix) out.push(`       Fix: ${f.fix}`);
    }
  }
  if (pasted) out.push('', 'Message the player saw:', pasted.slice(0, 1000));
  for (const l of logs) {
    out.push('', `--- last lines of ${l.label} ---`);
    for (const line of (l.lines || []).slice(-60)) out.push(typeof line === 'string' ? line : line.text);
  }
  return redact(out.join('\n'), { publicIps });
}

module.exports = {
  RULES,
  classify,
  parseLine,
  scan,
  verdict,
  installDirFromAcf,
  findGameDir,
  clientLogCandidates,
  serverLogCandidates,
  resolveLogs,
  readFrom,
  isLogPathAllowed,
  redact,
  buildReport,
  LOGGER_FILE_RE
};
