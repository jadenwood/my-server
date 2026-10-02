# Worlds: starting the same world every time

How a Reign of Kings dedicated server picks its world, why the owner's failed starts on 2026-10-02 made new worlds, and what Realm Steward now does about it. Code: `launcher/lib/worlds.js` (world choice), `launcher/lib/prestart.js` (what is already running), `launcher/lib/readiness.js` (ready lines). Fake server for the tests: `tools/rok-sim`.

Evidence tags as elsewhere in the repo: **[DEC]** read in the decompiled `Assembly-CSharp.dll` of Oxide.ReignOfKings 2.0.3867 (zip sha256 `6c35c623...c72c6c8`); **seen** means seen on the owner's real Windows 11 server (`G:\RealmTest\server`) on 2026-10-02. **UNVERIFIED** means neither.

## What was seen on the owner's server

- After several failed starts the server folder had new world folders `Saves\Slot3`, `Slot6`, `Slot8`, `Slot9` and `Slot10`, and the log said `Could not load world N. Loading new world instead.` (seen)
- Those starts failed with `The port 7350 is already being used by another application.` A second `ROK.exe` was running from the same folder: a double Start, or the game's `Server.exe` watchdog starting `ROK.exe` again about 2 s after it was ended. (seen)
- Steward's backup crashed with `EBUSY ... Session.lock` while the server ran: the running server holds that file so that no other program can open it. (seen)
- A good start logs `Server for N players started on port P.` and then `Game has started.` in `<server>\Logs\Log[yyMMdd-hhmmss].txt`. (seen)

## How the game picks a world [DEC]

| Step | Code | What happens |
|---|---|---|
| 1 | `ServerSettingsFile.Load` | Reads `saveLocation` (default `Saves/`) and `worldSlot` (default `-1`, "-1 creates a new world.") from `Configuration\ServerSettings.cfg`. |
| 2 | `DedicatedServerBypass.StartServer` | If `worldSlot >= 0` and that slot is **locked**, logs `Could not load world N. Loading new world instead.` (twice: through the in-game console and as a Warning) and sets `worldSlot` to `-1`, in memory and in the file. |
| 3 | same | `worldSlot < 0`, or no folder for that slot: `Game.New`. Otherwise `Game.Load`. The file is saved at the end of this step. |
| 4 | `Game.New` | A slot below 0 becomes the first free number (`GameSlotManager.GetNextAvailableSlot`: 0, 1, 2, ... until a folder is missing). |
| 5 | `Game.New` / `Game.Load` | `GameSlotManager.LockSlot` creates the folder `<saveLocation>\Slot<N>` if needed and opens `Session.lock` in it with `File.OpenWrite` (no sharing). Logs `Save slot located at: <saveLocation>Slot<N>`. All of this happens **before** the world loads and before the game port is opened. |
| 6 | `Game.Load` | No slot info in the folder: `Could not load game slot {N+1}.` and the game ends. A slot older than version 100018 sets `worldSlot` to `-1` and ends. |
| 7 | `CoreServer.Start` | Opens the game port. A busy port ends the game: `The port P is already being used by another application.` The new slot folder from step 5 stays behind. |
| 8 | `DedicatedServerBypass.OnGameStart` | Once the world has loaded, writes the running slot number into `worldSlot` and saves the file. |
| 9 | `Game.OnLoadingComplete` | Saves the world once, then logs `Game has started.` |
| 10 | `Game.OnDestroy`, unload | `UnlockCurrentSlot` closes and deletes `Session.lock`. A slot created in step 5 whose world never loaded is deleted by the unloader (`GameSlotInfo.Delete`), but only if unloading finishes before the program quits. |

**Locked** means another process holds `Session.lock` open (`GameSlotManager.SlotIsLocked` tries `File.OpenWrite` and treats any error as locked). A server that crashed or was killed leaves the file behind, but its handle is gone, so the world is **not** locked afterwards.

The folder name format `Slot{0}` is scene data, not code; `Slot3` and friends were seen.

## Why the failed starts made new worlds

1. A second server from the same folder (a leftover, or one the watchdog started) was still running world N and held `Saves\SlotN\Session.lock`.
2. Steward's Start launched another `ROK.exe`. The game found world N locked, logged `Could not load world N`, set `worldSlot = -1` in the file and created the next free slot.
3. That run then died on the busy port (step 7). Its new slot folder stayed behind.
4. `worldSlot` was now `-1` in the file. Every later start, even a successful one, created yet another new world.

So one port clash cost the owner their world for every start after it, not only for the failed one. The gaps in the slot numbers fit step 10: some failed slots were deleted on unload and some were not.

## What Steward does now

**It remembers the world.** When a server it runs reaches `Game has started.`, Steward reads `worldSlot` (the game has just written the running slot, step 8) and remembers it per server and folder (`worlds` in `realm-settings.json`). It does not remember a run that logged `Could not load world N`: that run is on a new world by accident.

**Before every start** (owner, crash restart or daily restart) it reads `worldSlot`, `saveLocation` and every `Slot<N>` folder, and decides (`planWorld`):

| Situation | Steward |
|---|---|
| `worldSlot` is the remembered world | Starts it. |
| `worldSlot` is `-1`, a missing slot, or an empty slot, and the remembered world exists | Writes `worldSlot` back to the remembered world first (the old file is backed up to `_realm-backups\config`), then starts. Says so in the console. |
| The world's `Session.lock` is held by another process | Does not start. A start would make the game skip it and create a new world. Offers Stop it cleanly / Adopt it (below). |
| The start would create a new world while saved worlds exist, nothing is remembered (or the remembered world is gone), or `worldSlot` names another saved world than the remembered one | Does not start. Asks the owner: one of the saved worlds (the most recently saved is first), or a new world on purpose (asked twice). A crash or daily restart in this situation stops and says why instead of asking. |
| No world exists yet (first start) | Lets the game create it, and remembers it once it has started. |

Steward never deletes, moves or renames a world folder. Empty slot folders left by failed starts can be removed by hand once the right world is running.

**An empty slot** is a slot folder with at most one file of at most 16 KB besides `Session.lock`: the slot info a failed start writes. UNVERIFIED: the real file layout of a saved world was not examined; a world that has been saved is assumed to hold more than that.

**Locked** is checked by opening `Session.lock` for reading for a moment. On Windows that fails with `EBUSY` while a server holds it (seen through the backup crash). Steward does this only before a start and in the pre-start check.

## Before Start: what is already running

`launcher/lib/prestart.js` runs before every start. It looks at the server's ports (UDP and TCP game port, UDP Steam query port) with a bind test, asks Windows which process owns them, lists every `Server.exe` and `ROK.exe` (with their paths, where Windows shows them), checks which admin console ports 11000-11003 are bound, and which world locks of this folder are held. It never connects to an admin console just to look: the game saves and shuts down when its last console client disconnects, so a look would stop the server.

| Found | Means | Steward offers |
|---|---|---|
| `ROK.exe` from this folder, or a hidden-path `ROK.exe` while this folder's world is locked | A leftover server (double start, or started outside Steward) | **Stop it cleanly**: `/shutdown` over its admin console (the game saves and exits), then waits until the port is free and watches 5 s more for a relaunch. **Adopt it**: Steward connects to its console, follows its newest log, shows it as running, and watches its process id. |
| `Server.exe` (from this folder, or with a hidden path) | The game's watchdog. Started once as administrator, it starts `ROK.exe` again about 2 s after it ends (seen). | Nothing to click: close the `Server.exe` window first, or end `Server.exe` and then `ROK.exe` in Task Manager opened as administrator. Steward does not end administrator programs. |
| `ROK.exe` in `steamapps\common\Reign Of Kings\` | The game client | Close the game, start the server, then launch the game. |
| `ROK.exe` elsewhere | A server from another folder | Stop it there, or change ports. |
| `ROK.exe` with a hidden path and nothing else | The game, or a watchdog-run server | Explains both. |
| Any other program | Unknown | Close it, or change ports. |

Without a listening admin console there is no clean way in, and Steward says so instead of killing anything. Force stop never ends an adopted server. Steward never auto-restarts into a port clash (since commit cfee55c), and now also stops restarting after a run that fell onto a new world.

**Adopting** makes Steward a console client of that server. From then on, closing Steward also stops that server (the game shuts down when its last console client leaves), as for servers Steward starts itself.

## Ready, and which log

Steward follows `<server>\Logs\Log[yyMMdd-hhmmss].txt`, the game's own log. It takes the newest such file that did **not** exist when Steward pressed Start (by creation time where the file system has one), so it never reads an older run's log. The names use a 12-hour clock and do not sort by time. Unity's `-logFile` (`Logs\realm-server.log`) is shown too, from the moment it changes, because a crash before the game's logger starts only shows there.

Ready means `Server for N players started on port P.` and then `Game has started.`, in that order. `Initialize engine version:` is not used any more.

Limits: two servers started in the same second write to the same log file name; Steward then cannot tell the second one's log from the first. When a server exits Steward reads the rest of its log (up to 3 s) before it reports the exit, so the last lines (`already being used`, `Could not load world`) are seen.

## Proven and UNVERIFIED

| Statement | Status |
|---|---|
| Ready lines and their order; the game log's location | Seen |
| Failed starts left `Saves\Slot3`, 6, 8, 9, 10 and logged `Could not load world N` | Seen |
| `Session.lock` is held exclusively while a server runs (`EBUSY` for other programs) | Seen (backup crash) |
| `Server.exe` relaunches `ROK.exe` about 2 s after it ends, once started as administrator | Seen (inferred from the owner's logs; Server.exe's own rules are not known) |
| How `worldSlot`, the slot lock, `Game.New` / `Game.Load` and the write-back work | [DEC], not seen step by step |
| A failed start's slot is deleted only if unloading finishes first | [DEC] plus the gaps in the owner's slot numbers; the race itself is UNVERIFIED |
| Steward's world memory, pin, block and choice on the real server | UNVERIFIED (tested against the simulator only) |
| Pre-start classification, Stop it cleanly and Adopt it on Windows (process paths, `portOwners`, an elevated `ROK.exe` answering `process.kill(pid, 0)` with EPERM) | UNVERIFIED (tested against the simulator only) |
| Whether `Server.exe` also relaunches after a clean `/shutdown` | UNVERIFIED |
| The "empty slot" size rule | UNVERIFIED |
| Opening `Session.lock` for a moment cannot make a starting server think its world is locked | UNVERIFIED: the window is a few milliseconds and only before Steward's own start, but a watchdog relaunch at that exact moment would hit it |

## Getting the owner's world back on `G:\RealmTest\server`

1. Stop everything: close the `Server.exe` window if there is one, then make sure no `ROK.exe` runs (Task Manager > Details, as administrator).
2. Back up the folder (Steward: Servers > Backup).
3. In Steward press Start. With several saved worlds and nothing remembered yet, Steward asks which world to start, the most recently saved first. Pick the world where Season 1 began (the most recently saved world is usually it; check the dates in `Saves\`).
4. Once the console says `Game has started.`, Steward remembers that world and starts it every time from now on.
5. Only then, if wanted, delete the empty `Slot` folders by hand.

## Tests

- `launcher/test/worlds.test.js`, `prestart.test.js`, `readiness.test.js`: the decisions.
- `launcher/test/launch-reliability.test.js`: end to end against `tools/rok-sim`: ready lines and the right log, a leftover stopped cleanly, the watchdog relaunch, the game client on the port, adopt, and the owner's failed start replayed with the world put back.
- `tools/rok-sim/test/worlds.test.js`: the simulator's slot rules, the watchdog and the game-client stand-ins.
