# rok-sim: a fake Reign of Kings dedicated server for Linux and CI

`rok-sim` stands in for `ROK.exe -batchmode` on machines that cannot run the game: Linux CI runners and developer laptops. From the outside it behaves like the real dedicated server, so the Realm tools (Steward, the Doctor, the Court, the Chronicle service) can be tested end to end without Windows, Steam or a copy of the game.

**It is not the game.** It contains no game code and no game files, reads nothing from a game install, and must never be shipped to players. Every behaviour below was rebuilt from the decompiled `Assembly-CSharp.dll` of Oxide.ReignOfKings 2.0.3867 (zip sha256 `6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8`). The same evidence is cited in [docs/join-and-scale.md](../../docs/join-and-scale.md), [docs/admin-console.md](../../docs/admin-console.md) and [docs/connection-doctor.md](../../docs/connection-doctor.md). Anything not proven is marked **UNVERIFIED**.

Node 20 or newer (CI runs 20 and 22). No dependencies.

## Quick start

```bash
mkdir /tmp/srv && cd /tmp/srv
node ~/my-server/tools/rok-sim/bin/rok-sim.js -batchmode -nographics -logFile Logs/realm-server.log
#   first run: writes Configuration/*.cfg, logs "This is the first time...", exits 0 (like the game)
node ~/my-server/tools/rok-sim/bin/rok-sim.js -batchmode -nographics -logFile Logs/realm-server.log \
     -cport 11000 --sim-players "Aldric Varrow,Wren" --sim-chronicle demo
```

In a second terminal:

```bash
node tools/rok-sim/bin/rok-sim.js console 11000 /list "/sim.join Odo" /realm.players /shutdown
node tools/rok-sim/bin/rok-sim.js query 127.0.0.1 27015 info      # A2S_INFO as JSON
node chronicle/server.js --data /tmp/srv/oxide/data                # the real Chronicle on the fake data
```

The `-cport` rule applies here as it does in the game. If nothing connects within 10 s, the server stops. It also stops when the last console client disconnects. Keep a `console ... --hold` open, or leave out `-cport`.

## What it emulates

| Area | Behaviour | Evidence |
|---|---|---|
| Command line | `SystemUtil.CommandLineProps`: `-key [value]`, case-sensitive keys; a repeated key throws `ArgumentException` (exit 1). `-batchmode` without `-ip` makes a dedicated server. `-ip` makes it a client, so the simulator exits 2. `-cport` is parsed with `int.TryParse`, so junk becomes port 0. | [DEC] |
| Unity log | `-logFile <path>` (or `ROK_Data/output_log.txt`): a Unity preamble and `Initialize engine version: <v>`. It gets **only** Unity's lines. | wording [SEC]; version `5.1.2f1` is **UNVERIFIED** (only "Unity 5.1" is proven) |
| Game log | `Logs/Log[yyMMdd-hhmmss].txt` (12-hour clock in the name). Records are `[yyMMdd-HHmmss] [Info]   msg\r\n    Stack.Frame()\r\n` + `\n`. `[Except]` records hold the exception type and frames. Duplicate suppression is per message template: 49 lines, then every 5th up to 100, every 20th up to 500, and so on, with counters reset every 300 s. | [DEC] `Logger` |
| Start-up lines | `Reign Of Kings is now in dedicated mode.`, `Admin console enabled.`, `Save slot located at: Saves/Slot<N>`, `User registration loaded.`, `User bans loaded.`, `User whitelist loaded.`, `Steam game server started. (IP: …, Logged: True, Secure: True)`, `Server for N players started on port P.`, `Game has started.`, `Type /shutdown to shut down the server.` | strings [DEC]; `Server for …` then `Game has started.` was **seen** in that order on the owner's server (2026-10-02); the other lines' **relative order is UNVERIFIED** (Steam is a loader) |
| World slots | `<saveLocation>/Slot<N>` folders. `worldSlot >= 0` whose `Session.lock` is held by another running server: `Could not load world N. Loading new world instead.` (Info and Warn), `worldSlot = '-1'` saved. `-1` or a missing folder: a new world (`-1` takes the first free number). The slot is chosen and locked **before** the world loads, so a start that then fails on a busy port leaves a new slot folder behind (`--sim-failed-slot delete` removes it instead). A folder with nothing in it: `Could not load game slot N+1.` After the world loads `worldSlot` is written back. A clean exit deletes `Session.lock`; a crash leaves it, unheld. Linux has no Windows share modes, so the simulator writes `rok-sim lock pid=… token=…` into `Session.lock` and counts it as held while that process (or in-process `RokSim`) lives; `lib/slots.js` `lockHeld()` applies the same rule for tests. The real file is empty. | [DEC] `DedicatedServerBypass.StartServer`, `Game.New/Load`, `GameSlotManager`; `Slot3` … folder names **seen** |
| First run | Creates `ServerSettings.cfg` exactly as `ServerSettingsFile.Load` writes it (sections, comments, CRLF, code defaults), plus `Users`, `Permissions`, `DeathMessages`, `BannedPlayers`, `Whitelist`, `ChatMutes`, `VoiceMutes.cfg`. Logs the 67-dash error rule and the two first-run lines, sends the console Disconnect packet, exits 0. | [DEC]; `Permissions.cfg` and `DeathMessages.cfg` content is a **stand-in** (the real defaults are long and not reproduced); first-run exit code 0 is **UNVERIFIED** |
| Config quirks | Values are converted as the game converts them. A bad value is logged as `PropertyException: Tried to get a Int32 from 'x'` and the program exits. The character before `=` is dropped from the key, so write `key = 'v'`. `#` starts a comment and needs escaping as `\#`. Keys are case-insensitive, and repeats become `key2`. Keys the game does not read are **rewritten as comments** on the next save. The file is re-saved on every start, and `worldSlot` is set to the new slot. | [DEC] `PropertyFile`, `ServerSettingsFile` |
| Ports | UDP `portNumber` on `bindIP`; TCP `pingPort` on 0.0.0.0 (held open, ping protocol not emulated); UDP `steamAuthPort` answering A2S; TCP admin console on 0.0.0.0:`-cport` (11000 without it; the simulator binds it to 127.0.0.1 unless `--sim-console-host` says otherwise). A busy game port logs `The port N is already being used by another application.` and exits. A busy Steam port logs the four `The Steam game server could not initialize.` lines and the server runs on without Steam. A busy console port logs a `SocketException` and the server runs on without a console. | [DEC]; exit code after a busy game port is **UNVERIFIED** |
| A2S | `A2S_INFO`: name `Another ROK Server`, game `ROK Server`, map empty, max players 0, type `d`, env `w`, VAC 1, version `1.0.0.0`, EDF with game port, a fake server SteamID and game id 344760. `A2S_PLAYER` and `A2S_RULES` (no rules) need the challenge. | fixed values [DEC]; challenge on `A2S_INFO`, player count, folder string, the 16-bit app id field and the EDF are **UNVERIFIED** (all switchable) |
| Admin console | The `Packet` framing (size+14, id, type, ASCII, `00 00`; 4082-byte split with the trailing empty packet; grouping while size is 4096). A keep-alive every second. Command output arrives as `[I]` Messages **before** the Response, and the Response body is empty. Text without `/` is chat. `Authentication` packets are run as commands. A Response-type packet is run with no answer. A bad frame kills that client's reader but leaves its socket open. A closed client is noticed on the next keep-alive (`EndOfStreamException` is logged). When the last client leaves, the server saves and stops. With `-cport` and no client after 10 s, it shuts down (if the world is still loading, only the console is disabled). The Disconnect packet is sent only after `/shutdown`, `/logout` (and aliases) or on the first run. | [DEC] `SocketAdminConsole`, `SocketServer`, `Packet` |
| Commands | `/list` (`online`, `players`), `/shutdown`, `/restart`, `/logout` (`exit`, `leave`, `quit`), `/kick "<name>" <reason…>` (reason words joined **without spaces**, as the game does), `/notice`, `/popup`. Unknown commands get `[I] Unknown command '/x'. For help type /help`. That is `[I]`, not `[E]`: `SendError` goes through `Console.AddError`, which logs as Info. Other real commands (`/ban`, `/weather`, …) run silently. `/realm.save` and `/realm.players` behave as `plugins/RealmCourt.cs` does, when `oxide/plugins/RealmCourt.cs` exists (or with `--sim-realm-court on`). | [DEC]; the `[C]` chat text format is **UNVERIFIED** |
| Players | `--sim-players`, `/sim.join`, or scenario steps. The log shows `Processing new connection...`, `Beginning Steam authentication with X (id).`, `Authentication verified for X (id).`, `X has connected. (id)`, then `X has disconnected.` and `Ending Steam auth session with id.` when they leave. `maxPlayers` and `timeBetweenPlayerJoin` drive the built-in queue. Names are cleaned with the game's regex (empty → `John`). An enabled `Whitelist.cfg` denies unlisted players with the game's line. Without Steam, authentication fails. | [DEC]; the exact order of the join lines is **UNVERIFIED** |
| Stop | `/shutdown`: `[I] Server has shut down the server.`, `[I] Saving game...`, the Response, then Disconnect, and exit 0. Saves write `Saves/Slot<n>/rok-sim-world.json` (also once, silently, when the world has loaded; a new slot gets `rok-sim-slotinfo.json`). These are **markers, not game saves**. SIGTERM/SIGINT/`/sim.crash`: no save, no Disconnect, exit 143/130/`<code>`. The game ignores stdin, and so does the simulator. | [DEC]; exit codes **UNVERIFIED** |
| Other programs on the port | `rok-sim watchdog [args]`: a `Server.exe` stand-in that runs the simulator in the current folder and starts it again `--sim-relaunch-ms` (2000) after **every** exit. `rok-sim client [port]`: a game-client stand-in that holds UDP `port` (7350) and has no console. Both are also `Watchdog` / `GameClient` in `lib/watchdog.js`. | relaunch after about 2 s **seen** (owner's logs); whether the real `Server.exe` also relaunches after `/shutdown` is **UNVERIFIED**; the game client holding UDP 7350 **seen** |
| Chronicle | `--sim-chronicle on|demo` writes `oxide/data/RealmChronicle.json` and `RealmState.json` in the shapes `plugins/RealmChronicle.cs` writes (limits 140/400/48×8, 500-event retention, 5-minute duplicate window, UTC `…Z` timestamps, ids that carry on after a restart). `demo` tells a lore story with the six great houses. `--sim-torn-writes` leaves half-written files for 150 ms. | shapes from the plugin; JSON whitespace **UNVERIFIED** |

## Options

Game arguments are passed through as the game would see them. Simulator switches all start with `--sim-`; `rok-sim --help` lists them with their defaults.

| Option | Default | Meaning |
|---|---|---|
| `--sim-boot-ms`, `--sim-load-ms` | 400, 1200 | time to the Unity banner, then the world-load time |
| `--sim-keepalive-ms`, `--sim-cport-window-ms`, `--sim-frame-ms` | 1000, 10000, 16 | console timings (the game's values are 1 s and 10 s) |
| `--sim-steam ok\|init-fail\|no-login\|no-secure`, `--sim-steam-wait-ms` | ok, 30000 | Steam start-up outcome |
| `--sim-a2s-challenge on\|off`, `--sim-a2s-players count\|zero`, `--sim-a2s-folder`, `--sim-a2s-drop 0..1` | on, count, "", 0 | the UNVERIFIED A2S details, plus a packet-loss switch |
| `--sim-public-ip` | 203.0.113.10 | IP shown in the Steam line (documentation range) |
| `--sim-players a,b` | none | players who join right after start |
| `--sim-scenario file.json` | none | timed steps (below) |
| `--sim-chronicle off\|on\|demo`, `--sim-chronicle-every-ms`, `--sim-state-refresh-ms`, `--sim-oxide-data`, `--sim-torn-writes` | off, 5000, 30000 | fake RealmChronicle output |
| `--sim-commands on\|off` | on | the `/sim.*` console commands; off makes them unknown, as in the game |
| `--sim-realm-court auto\|on\|off` | auto | the RealmCourt commands |
| `--sim-echo-logs`, `--sim-debug-logs` | off | mirror game log lines to stderr; also write Debug lines |
| `--sim-console-host` | 127.0.0.1 | bind address for the console. The game always uses 0.0.0.0; the simulator defaults to loopback because its console has no authentication and also accepts the `/sim` test commands. Pass `0.0.0.0` for full fidelity on a trusted network. |
| `--sim-first-run-exit-code`, `--sim-crash-exit-code` | 0, 1 | exit codes |
| `--sim-failed-slot keep\|delete` | keep | a new slot whose world never loaded: left behind (as on the owner's server) or deleted (when the game's unloader wins the race) |
| `--sim-relaunch-ms` | 2000 | `rok-sim watchdog` only: delay before it starts the server again |

### Simulator-only console commands

They are not in the game. Their answers go to the console only, never into the game log.

```
/sim.echo <text>                        /sim.status                (JSON after "SIMSTATUS ")
/sim.join <name> [steamId]              /sim.leave <name>          /sim.chat <name> <text>
/sim.event <type> <title> | <detail> | <actor>, <actor>
/sim.crown <king> <house>   |  /sim.crown none
/sim.log info|warn|error|exception <text>                       /sim.crash [exit code]
```

### Scenario file

```json
{ "steps": [
  { "at": 0,    "do": "join",  "name": "Aldric Varrow" },
  { "at": 500,  "do": "event", "type": "coronation", "title": "Aldric Varrow takes the Old Throne", "actors": ["Aldric Varrow"] },
  { "at": 800,  "do": "crown", "king": "Aldric Varrow", "house": "Varrow" },
  { "at": 1500, "do": "log",   "level": "error", "text": "Something to classify" },
  { "at": 2000, "do": "kick",  "name": "Aldric", "reason": "testing" },
  { "at": 3000, "do": "shutdown" }
] }
```

`at` is in milliseconds after the server is ready. The steps are `join`, `leave`, `kick`, `chat`, `event`, `crown`, `log`, `command`, `crash` and `shutdown`.

## Using it from other tests

```js
const { RokSim } = require('../tools/rok-sim');
const sim = new RokSim({ cwd: serverFolder, argv: ['-batchmode', '-cport', '11000', '--sim-load-ms', '50'] });
sim.on('ready', () => { /* ports are up */ });
sim.on('exit', (code) => {});
sim.start();
```

To let a launcher spawn `<folder>/ROK.exe` on Linux, run `rok-sim make-exe <folder>`. It writes an executable script named `ROK.exe` that runs the simulator. It is a test shim, never a game file.

```js
const { Watchdog, GameClient, slots } = require('../tools/rok-sim');
const w = new Watchdog({ cwd: serverFolder, argv: ['-batchmode', '-cport', '11000'], relaunchMs: 2000 }).start();
const client = await new GameClient({ port: 7350 }).start();     // holds UDP 7350 like the game
slots.lockHeld(path.join(serverFolder, 'Saves', 'Slot0', 'Session.lock'));   // is that world in use?
```

## Tests

`npm test` in this folder runs 62 tests:

- the framing, the config reader and writer, the logger, the command line, A2S and the Chronicle writer;
- world slots, the ready lines, the watchdog and game-client stand-ins (`test/worlds.test.js`);
- the whole server in process on free ports;
- the program as a child process.

They also run read-only checks against code that lives elsewhere in the repo:

- the launcher's own `AdminConsole` client, log classifier, line patterns and `ServerManager`, spawning the `ROK.exe` shim;
- the real `chronicle/server.js` serving the fake data;
- the closed event-type lists in `plugins/RealmChronicle.cs`, `chronicle/server.js` and `chronicle/public/assets/common.js`.

These tests are skipped when those folders are missing.

## Findings for other teams (from running their code against the simulator)

- **Fixed: Steward's "ready" flag.** It used to wait for Unity's `Initialize engine version:` in whichever file in `Logs\` was newest. Steward now follows the game's own `Log[...].txt` created after its start and is ready on `Server for N players started on port P.` then `Game has started.` (seen on the owner's server). The compatibility test checks it.
- **`Unknown command` and `… was not found on the server.` arrive as `[I]`, not `[E]`.** `launcher/test/fake-admin-console.js` sends `[E]`. Code that keys on the level would misread the real game.
- **The first-run lines do reach the console.** `CoreServer` logs them before it sets `SendLogs = false`.
