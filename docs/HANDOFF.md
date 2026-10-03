# Handoff: continuing Realm on the owner's PC

Read this first if you are a new session picking up the project. It records the state at the
end of the cloud sessions and what was learned on the owner's real Windows 11 PC. The plan from
here to 1.0 and the live-service year, with every UNVERIFIED item as a play-test checklist, is
[`ROADMAP.md`](ROADMAP.md).

## What Realm is

A medieval-politics community revival of **Reign of Kings** (Steam app 344760; dedicated
server app 381690). Original lore: the realm of Ostreval and six great houses
(`docs/community/lore.md`). Players use their own Steam copy; game files are never modified or
redistributed (EULA, Easy Anti-Cheat). Map of every system: `docs/realm-systems.md`.

- `plugins/`: 15 Oxide 2.0.3867 plugins on `claude/great-maxwell-wrksvt` (C# 3 only; cross-plugin
  methods must be non-public). 19 once the four plugin branches under "In flight" are merged.
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
| "The port 7350 is already being used" | a second server running: double Start (fixed by a per-server start lock), the game client, or most likely the game's **Server.exe watchdog** running elevated and relaunching ROK.exe ~2 s after it is ended | Start lock + no auto-restart into a port clash + explanation + pre-start detection with "Stop it cleanly" / "Adopt it" (commit f7ade43). UNVERIFIED on Windows: ROADMAP PT0.5, PT0.6 |
| New world each failed start (`Saves\Slot3`, 6, 8, 9, 10; "Could not load world N") | failed starts leave a new slot | Steward now remembers and pins the world (`docs/worlds.md`, commit f7ade43). UNVERIFIED on the real server: ROADMAP PT0.4 |
| Firewall "3 of 4 Realm rules" | one rule (likely the TCP 11000 block) missing | Owner should re-run Go Public > Add firewall rules (ROADMAP PT5.1) |
| `eac_usermode ... blocked from loading into LSA` notice | Windows 11 LSA protection notice | Harmless; do not disable LSA protection |
| `Lobby query failed` | CodeHatch's old lobby service is offline | Harmless; Realm uses its own signed server list |

## Landed since the first handoff

On `claude/great-maxwell-wrksvt`: launch reliability (real ready lines, leftover, watchdog and
game-client pre-checks, Stop it cleanly / Adopt it, same world every start; f7ade43), the remaining
exploit fixes (aa147a6), `Build-Realm.bat` with the 1.0.0 installers, `START-HERE.md` and
`docs/troubleshooting.md` (5a92e6f), the engraved art pack (b1f5899), the launcher UI pass (61af9a5),
and the in-game experience: one chat style, RealmHerald, popups and mood presets (f81bfbc, 845506a).

## In flight

Six team branches are finished and committed but **not merged** (2026-10-03). Each is based on
`claude/great-maxwell-wrksvt`. None has run on the real server.

| Branch | Commit | What it adds | Proof |
|---|---|---|---|
| `team/sculptor` | 716fba0 | `RealmSculptor.cs` (`/sculpt`): Realm's monuments built from the game's own blocks and colours; 11 sculptures in `art/sculptures/`, the tools in `art/tools/sculptor/` | Compile-checked; 159 mock checks; 16 tool tests |
| `team/sign-painter` | ff5dc1e | `RealmPainter.cs` (`/paint`): Realm's art and live boards on painted signs; `art/tools/painter/`, `art/paintings/RealmPainterArt.json` | Compile-checked; 122 mock checks plus 4 PNG decode checks; 12 tool tests |
| `team/ironbreaker` | 93b8cfd | `RealmLegendary.cs` (`/ironbreaker`): the one legendary blade, won at the Royal Tournament, taken by the bearer's slayer; a 15-line prize hook in `RealmEvents.cs`; the `legendary` exploit suite | Compile-checked; 142 mock checks; 48 exploit checks |
| `team/anti-cheat` | 8ac7c18 | `RealmSentinel.cs` (`/sentinel`): server-side cheat watch with evidence and a fading score; ships in watch mode | Compile-checked; 173 mock checks; 24 exploit checks (own runner) |
| `team/player-launcher` | a34ca4a | The player app as one screen: Play or Install, status, server picker, signed news (`lib/news.js`) and signed updates (`lib/updater.js`); `docs/player-launcher.md` | launcher 240 tests; 59 screenshot checks |
| `team/web-and-broadcast` | f303838 | The art pack in the portal, the Chronicle overlay and `/realm`, the stream scenes and the bot; coronation and rebellion moments; local fonts | portal 35, chronicle 15, streamkit 30, bot 87 (3 skipped) |

Merge notes:

- `tools/realm-integration/check.mjs` and its test: four branches each add `STAFF_COMMANDS`. Keep one
  list: `['sculpt', 'paint', 'ironbreaker', 'sentinel']`. After `team/ironbreaker` is in, remove the
  RealmLegendary entry from `PENDING_PLUGINS` (added by `team/sign-painter`).
- `plugins/docs/RealmHerald/logic-tests/Tests.cs`: the staff-command exemption is identical on
  `team/sign-painter` and `team/ironbreaker` (and the same idea on `team/anti-cheat`); `team/sculptor`
  needs it too. Keep one copy.
- `docs/community/ops/staff-roles-and-permissions.md`: four new permission rows
  (`realmsculptor.admin`, `realmpainter.admin`, `realmlegendary.admin`, `realmsentinel.admin`).
  `realmherald.admin` is already in the base (142dc82).
- `tools/exploit-review/run.sh`: `team/ironbreaker` adds the `legendary` case; the `sentinel` case is
  still to be added (ROADMAP SEN-1).
- `team/player-launcher` removed screens that `launcher/scripts/design-screens.mjs`,
  `onboarding-screens.mjs`, `launcher/README.md` and `docs/community/trailer-script.md` still mention
  (ROADMAP PLA-1).
- After `team/web-and-broadcast`, any change in `art/` needs `node portal/scripts/sync-art.mjs`, or the
  portal's drift test fails.
- Deploy does not yet copy `art/sculptures/*.json` or `art/paintings/RealmPainterArt.json` to the
  server (ROADMAP STW-1). Until it does, copy them by hand (ROADMAP PT0.3).

## Next steps

Follow [`ROADMAP.md`](ROADMAP.md). In short:

1. **M0 Integrate.** Merge the six branches above with the merge notes; do the M0 items (SRV-1,
   SRV-2, SEN-1, QA-1, SGN-8, STW-1, SGN-1, PLA-1, WEB-1); all checks green.
2. **M1 Owner proof.** On `G:\RealmTest\server`, run play-test sessions PT0 (build, install, run),
   PT1 (one player: chat, items, the crown, damaged files, moods), PT2 (signs and monuments) and PT3
   (two players). Write each result into the guide it settles.
3. **Then M2.** Fix the firewall rule, forward UDP 7350, TCP 7350 and UDP 27015 to 192.168.1.75,
   publish the signed list, and run the friends alpha (PT4, PT5) with RealmSentinel in watch mode.
4. Keep the play-test results, the systems map statuses and the roadmap's gates in step.

## How to check your work

```
tools\plugin-compile-check\check.sh        (bash; compiles all plugins at C# 3 against the shipped DLLs)
node tools\realm-integration\check.mjs     (cross-plugin calls, commands, chronicle types, chat style)
plugins\docs\<Plugin>\logic-tests\run.sh   (bash; each plugin against mocks)
tools\exploit-review\run.sh                (bash; exploit regression suites)
cd launcher && npm run check && npm test
cd chronicle && npm test                   (also portal, bot, streamkit, analytics, tools\rok-sim)
node art\tools\build.mjs check --skip-png   (and node --test art\tools\test\*.test.mjs)
node --test docs\saga\tools\check-saga.test.mjs docs\legal\tools\*.test.mjs docs\community\ops\tools\*.test.mjs
```
The full list is in `docs/realm-systems.md` section 7.
On the owner's PC you can also run the real server and read `G:\RealmTest\server\Logs\Log[...].txt`.
