# Handoff: continuing Realm on the owner's PC

Read this first if you are a new session picking up the project. It records the state at the
end of the cloud sessions and what was learned on the owner's real Windows 11 PC.

## What Realm is

A Game-of-Thrones-inspired community revival of **Reign of Kings** (Steam app 344760; dedicated
server app 381690). Original lore: the realm of Ostreval and six great houses
(`docs/community/lore.md`). Players use their own Steam copy; game files are never modified or
redistributed (EULA, Easy Anti-Cheat). Map of every system: `docs/realm-systems.md`.

- `plugins/`: 14 Oxide 2.0.3867 plugins (C# 3 only; cross-plugin methods must be non-public).
- `launcher/`: two Electron apps from one codebase: **Realm Steward** (owner/admin: setup wizard,
  servers, console, Court, Doctor, Go Public, Publish) and **Realm** (player client).
- `chronicle/`, `portal/`, `bot/`, `streamkit/`, `analytics/`, `ops/`, `mods/`, `art/`,
  `docs/saga/`, `docs/legal/`, `tools/rok-sim` (fake server for tests).

## Owner's PC

- Windows 11. Steam library `G:\SteamLibrary`. Game: `G:\SteamLibrary\steamapps\common\Reign Of Kings`.
- Steam server copy (read only): `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server`.
- Test server Realm manages: `G:\RealmTest\server` (Oxide installed, plugins deployed).
- LAN address 192.168.1.75. Router port forwarding not done yet.

## Proven on the real server (2026-10-02)

- The owner joined their own server and reached character creation. Steam auth, EAC, world
  load and all 14 plugins (compiled by Oxide, loaded, Season 1 auto-started) work.
- Server ready lines: `Server for N players started on port P.` then `Game has started.`
  (Unity's `Initialize engine version:` is far too early).
- The game's real log is `<server>\Logs\Log[yyMMdd-hhmmss].txt`; the client writes its own
  `Logs\Log[...].txt` in the game folder. `-logFile` only gets Unity's lines.
- The game never reads stdin. Commands go through the admin console socket (`-cport 11000`,
  unauthenticated, listens on all interfaces; the server shuts down when its last console client
  disconnects). Firewall rules must block TCP 11000-11003 inbound; never forward them.

## Real problems hit, and their status

| Problem | Cause | Status |
|---|---|---|
| `spawn Server.exe EACCES` | Server.exe needs administrator rights | Steward runs ROK.exe directly |
| "Unable to resolve host name (127.0.0.1:7350)" | `host:port` typed in the address box | Copy button copies the address only; Doctor explains |
| App crash `EBUSY ... Session.lock` | backup read a file the server locks | Backups skip locked files |
| `k_EBeginAuthSessionResultGameMismatch` on join | server registered with Steam as 381690 | ROK.exe is started with `SteamAppId=344760` (fixed, commit 1a77418) |
| "The port 7350 is already being used" | a second server running: double Start (fixed by a per-server start lock), the game client, or most likely the game's **Server.exe watchdog** running elevated and relaunching ROK.exe ~2 s after it is ended | Start lock + no auto-restart into a port clash + explanation. Shipping run adds pre-start detection with "Stop it cleanly" / "Adopt it" |
| New world each failed start (`Saves\Slot3`, 6, 8, 9, 10; "Could not load world N") | failed starts leave a new slot | Shipping run investigates slot selection (`docs/worlds.md`) |
| Firewall "3 of 4 Realm rules" | one rule (likely the TCP 11000 block) missing | Owner should re-run Go Public > Add firewall rules |
| `eac_usermode ... blocked from loading into LSA` notice | Windows 11 LSA protection notice | Harmless; do not disable LSA protection |
| `Lobby query failed` | CodeHatch's old lobby service is offline | Harmless; Realm uses its own signed server list |

## In flight when the cloud session ended

A "shipping" run was building: launch reliability (ready detection, leftover/watchdog/game-client
pre-checks, adopt/stop, world continuity), remaining exploit fixes (King's Hunt prize, tournament
alts, Seasons treaty farming, Chronicle flood budget), `Build-Realm.bat` (one click builds
`release\1.0.0\Realm-Steward-Setup-1.0.0.exe` and `Realm-Setup-1.0.0.exe`), `START-HERE.md` and
`docs/troubleshooting.md`. Check `git log` to see whether it landed.

## Next steps

1. Confirm the plugins answer players in chat: `/season`, `/house list`, `/crown`, `/events`.
2. Fix the firewall rule, forward UDP 7350, TCP 7350, UDP 27015 to 192.168.1.75, have a friend join.
3. Build both installers with `Build-Realm.bat`; install Steward over the old one.
4. Publish the server list (Steward > Publish), host `servers.json`, rebuild the player app with
   the published `player-config.json`, send `Realm-Setup` to friends.
5. Play-test the political systems with real people; tune the config knobs (claim/jury minimums
   are high for small servers).

## How to check your work

```
tools\plugin-compile-check\check.sh        (bash; compiles all plugins at C# 3 against the shipped DLLs)
node tools\realm-integration\check.mjs     (cross-plugin calls, commands, chronicle types)
cd launcher && npm run check && npm test
cd chronicle && npm test                   (also portal, bot, streamkit, analytics, tools\rok-sim)
```
On the owner's PC you can also run the real server and read `G:\RealmTest\server\Logs\Log[...].txt`.
