# Seamless Realm: design

This is the plan for making Realm feel effortless in two ways:

- **(a) The owner** runs **1 to 4 servers** of up to **120 slots** each from the Realm app, without ever opening a command window.
- **(b) A player** installs the Realm client and gets into a server in as few clicks as possible.

It is a design, not an implementation. Every idea below lists player/owner value, feasibility given what Reign of Kings allows, security, effort, and an **MVP cut** (what ships now and what waits). The prioritized build list for the team is at the end (section 8).

Effort scale: **S** ≤ 1 day, **M** 2 to 5 days, **L** more than a week (one developer, including tests).

Evidence tags, as in `oxide-rok-api.md` and `mods-keys-from-dll.md`:

| Tag | Meaning |
|---|---|
| **[DEC]** | Read in a decompiled C# view of the shipped `Assembly-CSharp.dll` (section 1.1). This proves what the code does, not how a given scene is wired. |
| **[IL]** | Seen in the DLL's user-string heap or metadata (offsets given). |
| **[REPO]** | Already in this repository (code or docs). |
| **[GENERIC]** | Standard Steam, Windows or Electron behaviour, not specific to RoK. |
| **UNVERIFIED** | Inference or gap. It needs a test on the owner's PC before anything depends on it. |

---

## 0. Ground rules this design keeps

- Players use **their own Steam copy**. Nothing here patches, repackages, redistributes or launches game files directly. Every launch of the game goes **through Steam** (Steam starts the game's own Easy Anti-Cheat launcher).
- Oxide and every Realm plugin run **only on the owner's server copies**.
- The client never modifies a game binary or asset. Section 6 lists three **config/profile files the game itself creates** that this design proposes to touch. Each is opt-in and gated by an explicit owner decision, because it sits close to the "never modify game files" rule.
- Nothing is published online by the build team. Features that need hosting (a signed server list, a landing page) are built so that the owner publishes them later, by hand or with a button the owner presses.
- Today the app never touches the firewall, the router or `netsh` [REPO `launcher/main.js:6`, `lib/realm.js:5`]. The **Go Public** feature (O6) changes that **only** with explicit, per-action owner consent, elevated, scoped and reversible. It is a policy change and section 6 flags it as such.

---

## 1. New facts from the game DLL that change what is possible

Before this pass, the README listed client auto-connect as **UNVERIFIED and deliberately not used**, and the A2S query as **UNVERIFIED**. A read of the game code turned up much stronger levers.

### 1.1 What was read

| Item | Value |
|---|---|
| File | `ROK_Data/Managed/Assembly-CSharp.dll` from the Oxide 2.0.3867 release zip (zip sha256 `6c35c623…c6c8`), file sha256 `ffea7074f49b3533d9ca86ccea259f44226bb31a90560b4a129609a99953435e`. This is the same file as in `mods-keys-from-dll.md`. |
| Method | User-string heap dump (`dnfile`) plus a decompiled view (`ilspycmd`). Nothing was executed. |
| Caveat | **UNVERIFIED: that the player's client build runs the same code.** The task brief notes that the game install also contains `Server.exe`/`ROK.exe`, which suggests one shared Unity build. However, this DLL is the **Oxide-patched server** copy from early 2024, and scene wiring (which components exist in which scene, and their serialized flags) is not in the DLL. Every client-side lever below has to pass the "connect probe" test (section 7, T1–T4) before it becomes the default. |

### 1.2 Findings

| # | Finding | Evidence |
|---|---|---|
| F1 | **Command-line quick join.** `CodeHatch.QuickJoinBypass.Awake` reads `-ip <host>`, `-port <n>` (default 7350), `-pass <pw>` and `-char <index>` from the command line. The component enables itself when `ip` is present, and `Start` calls `Game.Join(ip, port, pass)`. `LoadLevelInBG.Start` loads a separate "quick join" level when the lower-cased command line contains `"ip"`. | [DEC] `CodeHatch/QuickJoinBypass.cs`, `LoadLevelInBG.cs`; parser `CodeHatch.Engine.Core.Utility.Static/SystemUtil.CommandLineProps` (`-key value` pairs) |
| F2 | `Game.Join` resolves **host names** (`NetworkUtil.GetIpAddress`), so a DNS name works as well as an IP. It refuses unless the EAC runtime is active (`"Please run the game from Reign Of Kings.exe"`), so the game must be started the normal way, through Steam. | [DEC] `CodeHatch.Engine.Core.Gaming/Game.cs` `Join` (around line 693); [IL] `0x85dc` |
| F3 | `-rejoin` (`SimulateJoinServer`) is a **test harness**: it auto-completes character creation and fakes spawn events. **Never pass it.** | [DEC] `CodeHatch.Engine.Networking/SimulateJoinServer.cs` |
| F4 | **The in-game server browser reads a user-editable mirror list.** `LobbyMirrors` loads `<ConfigFolder>\Lobby Mirrors.cfg` (`ConfigFolder` defaults to `"Configuration"`). Keys: `UseCustomMirrors` (default true), list `CustomMirrors`, `UseDefaultMirrors` (default true), list `DefaultMirrors`, and the server-side POST URLs `RequestIP`, `RequestHost`, `ServerShow`, `ServerHide`, `ServerUpdate`. The game creates the file if it is missing and rewrites it on load. For each refresh the client picks **one mirror at random** from the combined list. After 2 failures in a row it falls back to the hard-coded `http://store.codehatch.com/rok/lobby.php`. | [DEC] `CodeHatch.UserInterface.Menus.Lobby/LobbyMirrors.cs`, `Lobby.cs` `RefreshCoroutine`; `CodeHatch.Build/GameInfo.cs:12`; `SimplifiedProperties/PropertyFile.cs` `Load` (creates the file); [IL] `0x71597`, `0x70e0a` |
| F5 | **The lobby wire format is simple and fully known.** A GET returns `0x06` (ACK), an optional message, `0x1C` (FS), then records. Each record is `0x1E` (RS), 17 fields joined by `0x1F` (US), then `0x1D` (GS). Field order: `ID, Name, Version, IP, Port, IsSecure("1" = password), Type, PlayerLimit, PlayerCount, PingLimit, IsPingLimited, IsDedicated, IsOfficial, IsPrivate, IsOnline, DateUpdated, DateCreated` (dates `yyyy-MM-dd HH:mm:ss`). A 15-field legacy form omits the two ping fields. The client caches the last good list in `Local\Resources\Lobby Cache.txt`. | [DEC] `Lobby.cs` `OnRefreshResult`, `ServerLobbyModule.TryGetResult` |
| F6 | **The dedicated server announces itself over HTTP.** `ServerLobbyModule` POSTs form fields `ID NM VN IP PN PW TP PL PC NL NE DE OF PR ON` to `ServerShow` at game start, polls every 30 s (it sends `ServerUpdate` when data changed), heartbeats every 150 s, and sends `ServerHide` at game end. The reply is `0x06<serverId>` or `0x15<error>`. If its IP is `127.0.0.1` it GETs `RequestIP`, expecting the public IP as plain text. The URLs come from **the server's own** `Configuration\Lobby Mirrors.cfg`. | [DEC] `CodeHatch.Engine.Networking/ServerLobbyModule.cs` |
| F7 | **The client remembers servers in a small profile file.** `Lobby History.bin` lives in `<persistentDataPath>\Profiles\<SteamID64>\`. It holds one .NET `BinaryWriter.Write(string)` value (a 7-bit length prefix, then UTF-8): items joined by RS, each `Name␟IPv4␟IPv6␟Port␟Flag`, where Flag bit 1 = visited, 2 = previous, 4 = **favourite**. History entries that are not in the lobby still appear in the browser, with the prompt "This server could not be found in the lobby. It has been loaded from your history." | [DEC] `LobbyHistory.cs`, `Lobby.cs`, `LobbyMenu.cs:335,479`; `CodeHatch.Engine.Serialization/SerializationUtil.cs`; [IL] `0x7116f` |
| F8 | **There is a built-in join queue.** When the server is full or the join throttle is active, a joining player is queued, and the client shows `Waiting in queue... Position {n}/{total}.` Permissions `codehatch.login.ignore.playerqueue` and `codehatch.login.ignore.playerlimit` skip it. The transport is opened with `Network.InitializeServer(int.MaxValue, port)`, so the only cap is `maxPlayers`. | [DEC] `CodeHatch.Engine.Networking/CoreServer.cs` `PlayerShouldBeQueued`, `QueuedPlayerChecker`, line 407, line 955 |
| F9 | **There is a join throttle.** `timeBetweenPlayerJoin` (default 10 s) is "the seconds the server waits before allowing a player since the last player joined". With 120 people reconnecting after a restart, the queue would take about **20 minutes** to drain. | [DEC] `ServerSettingsFile.cs:214`, `CoreServer.cs:1176` |
| F10 | **There is a built-in restart countdown.** If `restartTime` (in seconds, counted from game start) is above 0, `AutoRestart` broadcasts `Server.Notice("The server will be restarting in …")` at 60, 30, 15 and 5 minutes and at 30 s, then calls `Server.Shutdown()`. It does **not** relaunch the process. A supervisor has to do that. | [DEC] `CodeHatch.Engine.Core.Resetting/AutoRestart.cs` |
| F11 | **The Steam query (A2S) port is `steamAuthPort`.** The server calls `GameServer.Init(0, 8766, portNumber, steamAuthPort, AuthenticationAndSecure, "1.0.0.0")`, enables heartbeats, and sets the server name to the constant **"Another ROK Server"**. It never calls `SetMaxPlayerCount`. **A2S will therefore not report the real name or slots.** It is still useful for **ping** and liveness. It logs `Steam game server started. (IP: x, Logged: True, Secure: True)` with the public IP that Steam sees. The local Steam port **8766 is hard-coded**, so whether 4 servers can share one PC is **UNVERIFIED**. The error text itself says that multiple servers need different `steamAuthPort`s. | [DEC] `CodeHatch.Engine.Internet/SteamServer.cs` |
| F12 | `isPrivate` only means "Hides from the lobby if true". It is not access control. | [DEC] `ServerSettingsFile.cs:203` |

**UNVERIFIED and important:** whether `store.codehatch.com/rok/lobby.php` still answers. It could not be checked from the build environment. The owner can open that URL in a browser. A live lobby returns a body that starts with an invisible control character and then server names. If it is dead, the in-game browser shows only cached and history entries for everyone. In that case Realm's own mirror (X2/P6) is the **only** way any RoK server list works in-game, which is a strong community draw on its own.

---

## 2. Architecture in one picture

```
 OWNER PC (Realm app, owner mode)                                    PLAYERS (Realm app, player mode)
 ┌──────────────────────────────────────────────────────────┐         ┌──────────────────────────────┐
 │ Fleet supervisor  s1..s4  (ROK.exe -batchmode ...)        │         │ Signed manifest  ◄─ https ───┼── GitHub Pages / gist
 │   ├ crash restart + backoff, hang watchdog                │         │   verify ed25519, pin key    │   (owner uploads)
 │   ├ restartTime trick → wall-clock restarts (F10)         │         │ Best-server picker           │
 │   └ backup before every restart                           │         │   ping: A2S on steamAuthPort │
 │ Registry 127.0.0.1:8786  ◄─ POST server_show/update (F6)  │         │   pop:  Beacon /status       │
 │ Chronicle hosts 127.0.0.1:8787..8790 (per server)          │         │ PLAY → steam://run/344760//  │
 │ Beacon  0.0.0.0:<beaconPort>  READ-ONLY, rate-limited ────┼── http ─►   -ip host -port n/  (F1)    │
 │   /status.json  /lobby (F5 format)  /events.json          │         │ Fallback: favourite seeded   │
 │ Signer (ed25519 key in DPAPI safeStorage)                 │         │   in Lobby History.bin (F7)  │
 │ Go Public: firewall rule (elevated, opt-in), UPnP opt-in  │         │ Tray: Chronicle notifications│
 └──────────────────────────────────────────────────────────┘         └──────────────────────────────┘
```

There are three new owner-side pieces: the **Registry** (local), the **Beacon** (public, read-only) and the **Signer**. The rest extends what the launcher already does [REPO `launcher/lib/*.js`].

---

## 3. Shared foundations (both sides depend on these)

### X1. Signed realm manifest (`realm-manifest.json`)

The single source of truth that the player client trusts: which servers exist and where they are.

```json
{
  "schema": 1,
  "realm": "The Realm",
  "seq": 42,
  "issued": "2026-10-02T18:00:00Z",
  "expires": "2026-10-16T18:00:00Z",
  "servers": [
    { "id": "s1", "name": "Realm I - Ashveil", "host": "realm.example.net", "port": 7350,
      "queryPort": 27015, "beacon": "http://realm.example.net:7390", "maxPlayers": 120,
      "region": "EU", "tags": ["main"], "status": "open" }
  ],
  "launcher": { "version": "0.3.0", "url": "https://…/Realm-Setup-0.3.0.exe", "sha256": "…" },
  "links": { "discord": "https://discord.gg/…", "chronicle": "https://…" }
}
```

It is sent as `realm-manifest.json` plus `realm-manifest.json.sig`, a detached Ed25519 signature over the exact bytes.

- **Value:** players can never be pointed at a fake server by a tampered gist, a hijacked Pages site, a DNS trick or a pasted link. The owner changes IPs or adds s2..s4 without shipping a new launcher.
- **Feasibility:** high. Node's `crypto.sign(null, data, key)` / `crypto.verify` support Ed25519 natively, with no dependencies [GENERIC]. Hosting it is a static file.
- **Security:**
  - The public key is **compiled into the launcher**, and the client refuses any manifest that fails verification.
  - **Anti-rollback:** the client stores the highest `seq` it has seen and rejects lower ones.
  - **Freshness:** it rejects a manifest after `expires`, keeping the last good one for 7 days with a "stale" badge.
  - The private key lives only on the owner PC, encrypted with Electron `safeStorage` (Windows DPAPI) [GENERIC]. Export is offered once, to an offline backup.
  - **Key rotation:** the manifest may carry `nextKey`, signed by the current key. Two keys can be embedded during a transition.
  - **Never** accept a host or port from a deep link or the beacon. Those only *refer* to manifest ids.
  - **Privacy:** a manifest with a home IP is public. Prefer a DNS name (dynamic DNS) so the IP can change, and section 6 flags the DDoS exposure.
- **Effort:** S (sign/verify module and tests) + S (owner UI "Sign and export").
- **MVP cut:** *now*: schema, sign/verify, anti-rollback, owner "Export signed manifest" that writes both files to a folder, with the owner uploading by hand. *Later*: a one-click publish to a gist or GitHub Pages using a token stored in `safeStorage`.

### X2. Realm Beacon (a public, read-only status endpoint on the owner PC)

A tiny HTTP listener, separate from the 127.0.0.1-only Chronicle and the admin IPC. It serves:

| Path | Content |
|---|---|
| `/status.json` | Per server: `{id, online, players, maxPlayers, queueHint, restartAt, rebellionWindow, king, house, updated}` |
| `/lobby` | The same servers in the game's **lobby wire format** (F5), so the in-game browser can use it as a mirror (P6) |
| `/events.json?since=` | The public Chronicle events (names only, as the Chronicle contract already guarantees [REPO README "data files"]) |

- **Value:** live population and story for players, tray notifications, best-server picking, and a working in-game browser even if Code Hatch's lobby is gone.
- **Feasibility:** high. Data comes from the Registry (X3) and the per-server `RealmState.json` [REPO `plugins/RealmChronicle.cs`: `online`, `maxPlayers`]. It is plain Node `http`.
- **Security:**
  - GET only, a fixed set of paths, no query parsing beyond `since` (integer), responses built from allow-listed fields.
  - Per-IP token-bucket rate limit (for example 30 req/min) and a small response cache, so a flood costs almost nothing.
  - Binds a **separate port** that only opens when Go Public is on. Never serves `/overlay` admin views or `/api/*` raw files.
  - It exposes the same IP players connect to anyway. Its data is not signed, which is acceptable because the beacon can only report numbers. Where to connect always comes from the signed manifest.
- **Effort:** M.
- **MVP cut:** *now*: `/status.json` and `/lobby`, built and unit-tested, listening on loopback. *Later*: public exposure through Go Public, and `/events.json`.

### X3. Registry: let the game report its own status (F6)

Point each **server copy's** `Configuration\Lobby Mirrors.cfg` POST URLs (`ServerShow`, `ServerUpdate`, `ServerHide`, `RequestIP`) at `http://127.0.0.1:8786/rok/…`, served by the Realm app.

- **Value:** the game itself pushes name, version, IP, port, slots, player count and online flag every 30 to 150 s, **with no plugin involved**. Health checking and population stay accurate even if Oxide fails to load. `RequestIP` can return the address the owner wants advertised.
- **Feasibility:** high, if the server scene contains `ServerLobbyModule` (**UNVERIFIED**; T5 checks it). The file sits in the owner's **test copy**, which the app already edits (`ServerSettings.cfg`) under the marker rules [REPO `launcher/README.md` "How the server is run"]. That is the same category of change. Edit only the five URL lines with the existing "rewrite existing `key = '…'` lines only" rule [REPO `lib/safety.js`]. If the file is missing, start the server once so that the game writes it (F4: it creates and saves the file itself).
- **Security:** loopback only. Accept form posts from 127.0.0.1 only. Ignore unknown fields. Values are length-capped.
- **Effort:** S.
- **MVP cut:** *now* (it is the cheapest accurate population feed). Keep a fallback to `RealmState.json` when the Registry is silent.

---

## 4. Owner side: one app, up to 4 servers

### O1. Fleet manager (1 → 4 servers)

**What:** a "Servers" list in the app. **Add server** clones the template, assigns ports from a fixed plan, deploys Oxide and the plugins, and starts it. Start all / Stop all, with staggered starts (60 s apart).

**Port plan** (each server needs unique `portNumber`/`pingPort`, `steamAuthPort` and, if enabled, `rConPort`; F11 and [REPO `docs/server-reference.md` §2]):

| Server | Folder | portNumber = pingPort | steamAuthPort (A2S) | rConPort (RCON off) | Chronicle | maxPlayers |
|---|---|---|---|---|---|---|
| s1 | `G:\RealmTest\server` (existing) | 7350 | 27015 | 27016 | 8787 | 120 |
| s2 | `G:\RealmTest\s2\server` | 7360 | 27025 | 27026 | 8788 | 120 |
| s3 | `G:\RealmTest\s3\server` | 7370 | 27035 | 27036 | 8789 | 120 |
| s4 | `G:\RealmTest\s4\server` | 7380 | 27045 | 27046 | 8790 | 120 |

(Registry on 127.0.0.1:8786, Beacon on TCP 7390. Final numbers to be fixed in code as one table, `lib/fleet-ports.js`.)

- **Value:** adding a second server takes one click, with no port clashes and no hand-edited cfg.
- **Feasibility:** medium.
  - Config is read from `Configuration\` relative to the working directory, so each server needs **its own copy** [REPO `server-reference.md` §1 "No config-path argument"].
  - Disk use is roughly 4× the server size (**UNVERIFIED** size). Hard-link de-duplication is tempting but **unsafe**: Oxide overwrites `Assembly-CSharp.dll`, and an in-place write through a hard link would corrupt every copy and could reach the Steam folder. *Do not hard-link.*
  - **UNVERIFIED:** that two or more servers run side by side on one PC, given the hard-coded Steam port 8766 (F11). T6 tests it.
  - **UNVERIFIED:** CPU/RAM per server at 120 players.
- **Security:** each copy gets its own `.realm-test-copy` marker, and the existing path-refusal rules apply to every folder [REPO `lib/safety.js`]. The Chronicle and the Registry stay on loopback.
- **Effort:** L (data model, migration of the single-server settings, UI, per-server Chronicle hosts).
- **MVP cut:** *now*: the multi-server **data model and port table**, with s1 working exactly as today and **max players 120** written through the existing Settings path (validation already allows 1 to 200 [REPO `lib/safety.js:403`]). *Later*: the **Add server** clone flow, once T6 proves two servers coexist.

### O2. Supervisor: crash restart with backoff, and a hang watchdog

- **What:**
  - On an exit that the owner did not request, restart after 10 s, 30 s, 60 s, 2 min, then 5 min (cap).
  - Reset the backoff after 30 min of healthy uptime.
  - **Crash-loop breaker:** after 5 crashes in 15 min, stop and alert.
  - **Hang watchdog:** if ALL of these hold for 3 min (no console or log output, the Registry heartbeat is missing (150 s cadence, F6), and an A2S ping on `steamAuthPort` fails), treat it as a hang. Force-stop, back up (O4), then restart.
- **Value:** the owner sleeps; the realm stays up. `Server.exe` has its own freeze-restart, but `ROK.exe` (what the owner actually uses, because `Server.exe` hits EACCES) has none.
- **Feasibility:** high. `server-process.js` already records `lastExit` and emits `exit` [REPO `lib/server-process.js:168-181`].
- **Security:** never restart a server whose folder fails the marker check. Log every automatic action to `_realm-backups\supervisor.log`.
- **Effort:** M.
- **MVP cut:** *now*: crash restart with backoff and the loop breaker. *Later*: the hang watchdog (it needs A2S and the Registry in place first).

### O3. Scheduled restarts with an in-game countdown, at no plugin cost

- **What (the trick):** before each start, the app computes `seconds until the next restart slot` (for example 06:00 local) and writes it into the existing `restartTime` key. The game's own `AutoRestart` then runs the countdown and the shutdown (F10). The supervisor sees a *requested* exit and restarts after the backup.
- **Rebellion-aware:** if a slot falls inside a rebellion window (read from `oxide\config\CrownAndConsequences.json` [REPO README "Known limitations"]), move it to after the window.
- **Fleet-aware:** stagger s1..s4 by 15 min so the realm is never entirely down.
- **Value:** daily restarts fight memory leaks; players get 60/30/15/5-minute warnings in their own UI.
- **Feasibility:** high. `restartTime` is a real key [REPO `server-reference.md` §4.1; DEC F10].
  - It counts from **game start**, so the restart lands late by the world-load time. Compensate by subtracting the last measured load time.
  - **UNVERIFIED:** that `Server.Shutdown()` makes `ROK.exe` exit (T7).
  - **UNVERIFIED:** that the world is saved on shutdown. Add a belt-and-braces save: a tiny `RealmOps` plugin command, `realm.save`, calling `Game.Save()` [REPO `server-reference.md` §6, API list], sent on stdin 60 s before the slot.
- **Security:** none beyond the existing cfg-write rules.
- **Effort:** S (scheduler and cfg write) + S (`RealmOps` save command).
- **MVP cut:** *now*: daily slot, `restartTime` trick, save command. *Later*: the rebellion-window shift and fleet stagger UI, plus a "restart now with N-minute countdown" button (needs a `RealmOps` countdown command that uses `Server.Notice`/`BroadcastMessage` [REPO `oxide-rok-api.md` §4.2]).

### O4. Automatic backup before every restart

- **What:** on every requested or crash exit, *before* the restart, run the existing **Back up world** (zip `Saves\` and `oxide\data\`, written as `.part` then renamed) [REPO `launcher/README.md`]. Verify the zip by re-reading its central directory. Retention: last 24, plus 1 per day for 14 days, plus 1 per week for 8 weeks. Show the free disk space and refuse to back up if space is under 2 GB (and say so loudly).
- **Value:** a griefing incident or a corrupt save costs at most one restart cycle.
- **Feasibility:** high; it reuses existing code.
- **Security:** backups stay under `<realm folder>\backups`, using the existing paths.
- **Effort:** S.
- **MVP cut:** *now*, all of it.

### O5. Health dashboard

- **What:** one card per server: state, uptime, players `n/120` (Registry → Chronicle fallback), last restart and its reason, the next scheduled restart, last backup, crash count over 24 h, CPU% and RAM (from a hidden `Get-Process` sample every 10 s, as the app already does for the process check [REPO `launcher/README.md`]), free disk, and a reachability badge (O6). Plus a red banner for anything needing attention.
- **Value:** the owner sees at a glance whether the realm is healthy.
- **Feasibility:** high. The queue length is **not** exposed to plugins (there is no hook for `PlayerQueueChangeEvent`), so show "full, players queueing" when count ≥ limit.
- **Effort:** M.
- **MVP cut:** *now*: state, uptime, players, last/next restart, last backup, crash count. *Later*: CPU/RAM graph and reachability.

### O6. "Go Public" wizard

This is a guided, reversible, logged sequence. Each step needs its own **explicit owner click**, and nothing runs silently.

1. **Settings:** set `bindIP = '0.0.0.0'` and choose `isPrivate` (F12: it only hides the server from the lobby). Make sure a whitelist or password is in place for the closed test [REPO `community/launch-plan.md` Phase 1].
2. **Firewall rule (opt-in, elevated):** one rule per server, **scoped to the full path of that copy's `ROK.exe`**, inbound UDP+TCP on exactly that server's `portNumber` and `steamAuthPort` (plus the Beacon port on TCP). The rule is named `Realm s1` and so on, and the **Undo** button removes exactly those names. It is implemented as a fixed-template `netsh advfirewall firewall add rule …` run through a single UAC prompt. No user text goes into the command line: the path comes from the validated test folder, and the ports come from the port table.
3. **UPnP (opt-in):** discover the router with SSDP, then `AddPortMapping` for the same ports with a 1-hour lease, renewed while the server runs and deleted on stop. Read `GetExternalIPAddress`. If the router's WAN address is private (`10/8`, `172.16/12`, `192.168/16`, `100.64/10`), report **CGNAT: port forwarding cannot work from home**, and suggest a VPS or tunnel instead.
4. **Reachability self-test:** there is no Realm-owned echo server, so the test layers three signals:
   - (a) the game's own log line `Steam game server started. (IP: …, Logged: True, Secure: True)` (F11) proves that outbound Steam auth works and gives the public IP Steam sees;
   - (b) an **owner-chosen external echo** (optional: any small TCP-connect checker the owner trusts, configured as a URL template; TCP only, so it cannot prove UDP);
   - (c) a **friend probe**: the wizard creates a `realm://probe/s1` link. When a friend clicks it, their launcher runs an A2S ping (UDP `steamAuthPort`) and a Beacon GET, and shows a short result code that the friend pastes back to the owner. This is the only check that proves **UDP from outside**.
5. **Publish:** export the signed manifest (X1), with the host set to the DNS name or public IP.

- **Value:** this turns the hardest part of community hosting into a checklist with green ticks.
- **Feasibility:** medium. Firewall and UPnP are standard [GENERIC]. **UNVERIFIED:** that `ROK.exe` (not `Server.exe`) is the process that owns the sockets. It should be, because `Server.exe` is a wrapper [REPO `server-reference.md` §1], but T8 checks it.
- **Security:** this **changes the current "never touch the firewall/router" rule** (section 6, decision D2).
  - Rules are per program and per port, never "allow all".
  - UPnP mappings are leased and removed on stop.
  - Everything is logged and listed on one **Public exposure** panel with "Undo all".
  - RCON stays off.
  - Never expose the Chronicle `/api` or the Registry.
- **Effort:** L.
- **MVP cut:** *now*: step 1 (already mostly there), step 4a (parse the Steam log line, which is free and needs no network), and a **read-only "what Go Public would do" preview**. *Later*: steps 2 and 3 after decision D2, step 4c, and step 5 auto-publish.

### O7. Fleet-wide plugin, config and update sync

- **What:**
  - **Update plugins** deploys to every server, with a per-server override folder.
  - **Update day:** after a Steam update to app 381690 (detected through `appmanifest_381690.acf` `buildid` compared with the build recorded in each copy's marker), it backs up, re-copies, re-applies Oxide (Steam overwrites `Assembly-CSharp.dll` [REPO `server-reference.md` §6]), redeploys the plugins, and restarts one server at a time.
- **Value:** game updates stop being a dreaded evening of manual steps.
- **Feasibility:** medium. All the building blocks exist [REPO `lib/realm.js`].
- **Effort:** M.
- **MVP cut:** *later*, except "Update plugins → all servers", which is S and ships now with O1's data model.

### O8. Discord webhook alerts (outbound only)

- **What:** the owner pastes a webhook URL (stored with `safeStorage`). The app posts "s1 is up", "s1 restarting in 15 min", "s1 crashed, restarting (2/5)", "crash loop, stopped", and daily "the crown passed to …" items from the Chronicle.
- **Value:** the owner and the community both know what is going on without anyone watching the app.
- **Feasibility:** high. It is one HTTPS POST [GENERIC]. **Discord only turns `http(s)` URLs into links**, not `realm://`, so join buttons have to point at the https landing page (P4).
- **Security:** a webhook URL is a write secret, so never log it. Posts carry no IPs unless the owner opts in.
- **Effort:** S.
- **MVP cut:** *later* (it needs a decision on what is posted publicly), but cheap.

---

## 5. Player side: download → playing in the fewest steps

Target flow for a first-time player:
**Install Realm → (Steam check) → 60-second intro → "Join Realm I" → Steam starts the game → lands in the server (or its queue).**

That is one click after installing, *if* T1–T4 pass. Otherwise the fallback is two clicks: the server sits pinned in the in-game Favourites (P5).

### P1. Install and readiness detection

- **What:** before showing PLAY, check that (1) Steam is installed (`HKCU\Software\Valve\Steam\SteamPath`, a lookup already implemented [REPO `launcher/README.md`]), and (2) app 344760 is installed (`steamapps\appmanifest_344760.acf` in any library from `libraryfolders.vdf`, parsed with the existing vdf parser [REPO `lib/safety.js`]).
  - If it is missing: **Install Reign of Kings** → `steam://install/344760` [GENERIC].
  - If Steam is missing: the official Steam download page (https).
  - Also: "Steam is not running" (Steam starts itself on a `steam://` link anyway), and "the game is already running" (switch to it rather than launch twice).
- **Value:** no dead-end PLAY button.
- **Feasibility:** high. **UNVERIFIED:** that `steam://install/344760` behaves for a delisted or old title the same way as for others. The owner can test it on a second Steam account.
- **Security:** read-only registry and file reads. Allow-list `steam://install/<digits>` in `openExternalAllowed` next to `rungameid` [REPO `main.js:79-82`].
- **Effort:** S.
- **MVP cut:** *now*.

### P2. One-click join through Steam launch arguments (F1)

- **What:** PLAY on a server card opens `steam://run/344760//-ip <host> -port <port>/` [GENERIC Steam browser protocol]. An alternative avoids the URL dialog: run `<SteamPath>\steam.exe -applaunch 344760 -ip <host> -port <port>` [GENERIC]. Both go through Steam, so Steam starts the game's own EAC launcher (F2 requires that).
  - Never pass `-pass` (it would be visible in the process list) or `-rejoin` (F3).
  - `host` and `port` come **only** from the verified manifest.
- **Value:** "click → you're in", the single biggest seamlessness win.
- **Feasibility:** proven in code (F1), but **four UNVERIFIEDs** need the owner PC (T1–T4):
  - (T1) the client build has `QuickJoinBypass` and `LoadLevelInBG.allowQuickJoin` wired in its scenes;
  - (T2) Steam passes the arguments through the EAC bootstrapper to the game;
  - (T3) what Steam shows for `steam://run` with arguments. Steam usually asks the user to confirm launch options; `-applaunch` may avoid that;
  - (T4) what happens on failure (server down or full): back to the menu, or a stuck loading screen.
  - Note that `LoadLevelInBG` tests for the substring `"ip"` anywhere in the command line, so any user launch option containing "ip" would also switch level. The launcher cannot control that, but it should be recorded in the troubleshooting text.
- **Security:** allow-list exactly `steam://run/344760//-ip <hostname-or-ipv4> -port <1-65535>/`. The host must match `^[A-Za-z0-9.-]{1,253}$`, and the app validates in the main process [REPO pattern `main.js` IPC validation]. The `-applaunch` variant uses `execFile` with an argument array, never a shell string.
- **Effort:** S (code) + the T1–T4 test session.
- **MVP cut:** *now*, behind a setting **"Connect automatically (experimental)"** that is **off by default**. Today's flow (`rungameid` plus showing the address with Copy) stays the default until T1–T4 pass. Then flip the default.

### P3. "Join best server"

- **What:** with up to 4 servers, one button picks for the player. Score = ping + fill + house affinity:
  - **ping** from an A2S_INFO round trip on `steamAuthPort` (F11; plain UDP from Node `dgram`, with no dependencies), falling back to the Beacon HTTP round trip;
  - **fill** prefers 30–90% full and avoids `≥ maxPlayers`, where the native queue (F8) would apply;
  - **house affinity** (P8).
  - Show *why*: "Realm II: 42 ms, 71/120, your house is here".
- **Value:** players spread across servers without a server-browser decision.
- **Feasibility:** high for ping. The name and slots in A2S are wrong by design (F11), so population must come from the Beacon (X2). **UNVERIFIED:** that A2S replies at all (it depends on the Steam GameServer library answering on the query port; T9).
- **Security:** only probe hosts in the manifest. Cap at 4 probes per refresh.
- **Effort:** M.
- **MVP cut:** *later* (with 1 server it is not needed). *Now*: build the A2S ping helper, because O2 and O6 need it too.

### P4. Deep links: `realm://join/<serverId>` and an https landing page

- **What:**
  - Register the `realm://` protocol per user (`app.setAsDefaultProtocolClient('realm')` plus `second-instance` and `open-url` handling) [GENERIC Electron].
  - `realm://join/s2` opens Realm, shows "Join Realm II?" with live population, then runs P2.
  - Because Discord and most chat apps will not linkify custom schemes, publish a static **landing page** (`/join/?s=s2`) next to the manifest. It tries `realm://join/s2` and otherwise shows "Get Realm" with steps.
- **Value:** "click the link in Discord → you're in game".
- **Feasibility:** high.
- **Security:**
  - The link carries **only a manifest id** (`^s[1-9]$` or a short slug), never a host, port or password.
  - Always show a confirmation screen; links never auto-launch the game.
  - Ignore unknown actions. `realm://probe/<id>` (O6) shows its own explicit screen.
- **Effort:** S (protocol) + S (landing page, built but not published).
- **MVP cut:** *now*: protocol and `join`. *Later*: the landing page goes live when the owner publishes it.

### P5. Favourites seeding (the fallback that works without launch arguments) (F7)

- **What:** an opt-in setting, **"Add Realm servers to my in-game Favourites"**. While the game is **not running**, the launcher:
  - finds `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Profiles\<SteamID64>\Lobby History.bin`. Do not hard-code the names; scan `LocalLow` for a `Profiles\<17 digits>\Lobby History.bin`. The community-reported path is `CodeHatch\ReignOfKings` [REPO `server-reference.md` §3];
  - backs it up;
  - reads it with the F7 format and **merges** one entry per manifest server with `Flag |= 4` (favourite), keeping every existing entry;
  - writes it atomically.
  - In game, the Favourites/History pane then lists "Realm I" one click from Join, even if no lobby works.
- **Value:** a reliable 2-click join that does not depend on T1–T4.
- **Feasibility:** medium. The format is fully known (F7). **UNVERIFIED:** the persistentDataPath folder names; that a host name (not an IP literal) works in the `IPv4` field, since `Game.Join` resolves names (F2) but the field is named IPv4; that the game reads the file at menu time (it caches it once per run, so only write while the game is closed).
- **Security:** this is a **player profile file written by the game**, not an install file. It is still the player's game data, so it needs an explicit opt-in, a backup, a length cap, refusal if parsing fails (never overwrite what we cannot parse), and an **Undo** that restores the backup. Section 6, decision D1.
- **Effort:** M (format codec with round-trip tests, discovery, UI).
- **MVP cut:** *now*: the codec and its tests plus a dry-run ("this is what would be added"). *Later*: the write, after decision D1 and test T10.

### P6. Realm in the in-game server browser (Lobby mirror) (F4, F5)

- **What:** two parts.
  - **Owner side:** the Beacon serves `/lobby` in the game's format.
  - **Player side (opt-in):** the launcher adds the Beacon URL to `CustomMirrors` in the client's `Configuration\Lobby Mirrors.cfg`. Because the client picks one mirror at random (F4), the setting has two modes. **"Realm only"** sets `UseDefaultMirrors='False'`. **"Realm + official"** leaves it, and then the Beacon's `/lobby` should *also* proxy the official list so that the random pick does not matter. That works only if Code Hatch's lobby is alive (§1 UNVERIFIED).
- **Value:** players who never touch the launcher again still see Realm, with live population, in the normal in-game browser. If the official lobby is dead, Realm's mirror revives the browser for **every** RoK server that registers with it, which is a community magnet.
- **Feasibility:** medium.
  - **UNVERIFIED:** that the client's relative `Configuration` folder resolves to the game install folder when Steam launches it.
  - **UNVERIFIED:** that Unity's `WWW` here follows https redirects. The default mirrors are plain `http://`, so serve the Beacon over plain HTTP; integrity is not at stake because joining still goes to the listed IP.
  - **UNVERIFIED:** that EAC ignores `Configuration\*.cfg`.
- **Security:**
  - This file is **inside the Steam install folder** (not shipped by Steam, but created by the game). That is the closest any idea here comes to the "never modify game files" rule, so it needs **owner decision D3** and **player opt-in**, a backup, editing only the `CustomMirrors`/`UseDefaultMirrors` lines, and an Undo.
  - A malicious mirror could list fake servers, which is why the launcher only ever writes the **manifest's** beacon URL.
- **Effort:** M (Beacon `/lobby` is shared with X2; the client cfg edit is S).
- **MVP cut:** *now*: Beacon `/lobby` (owner side, loopback) with a golden-file test against the F5 parser rules. *Later*: the client cfg edit, after D3 and T11.

### P7. Queue awareness and "tell me when there's room"

- **What:** the server card shows `118/120`. When the server is full, the button reads **"Join queue"** and explains that the game holds the player in line ("Waiting in queue… Position n/total", F8). For players who would rather not wait in a loading screen, offer **"Notify me when a slot opens"**: the tray app polls the Beacon every 30 s, and when `players < maxPlayers - 2` it shows a Windows toast "Realm I has room. Join now" that runs P2.
- **Value:** no one bounces off a full server.
- **Feasibility:** high. The native queue does the hard part. Slots cannot be truly reserved from outside (the reserve permissions in F8 are game permissions; **UNVERIFIED** how to grant them per player at runtime). Show the toast as "room is likely", not a promise.
- **Effort:** S once the Beacon exists.
- **MVP cut:** *later* (it needs X2 public). The **owner-side half ships now**: set `timeBetweenPlayerJoin` to **3** (F9; down from 10, so a 120-player refill takes about 6 min, not 20). **UNVERIFIED:** server load from faster joins, so make it a Settings field (validate 1–30) and test it in the creator night.

### P8. House-aware suggestion

- **What:** the player types their in-game name once (optional). The launcher looks it up in each server's public houses list (Chronicle state `houses[].members` [REPO README data contract]) and pins "your house plays on Realm II".
- **Value:** friends and houses converge on the same server, which matters a lot with 4 servers.
- **Feasibility:** medium. Names are not unique or verified. Treat it as a hint only.
- **Security:** only names that the Chronicle already publishes. Nothing stored remotely. The name stays in local settings.
- **Effort:** S once the Beacon serves state.
- **MVP cut:** *later*.

### P9. Tray presence and Chronicle notifications

- **What:** Realm minimizes to the tray (opt-in "Run in background"). It polls `/events.json` and `/status.json` (X2) every 60 s and shows toasts for: **coronation** ("A new king: …"), **rebellion window opens in 30 min / now open**, claims or rebellions involving the player's house (P8), "your server restarts in 15 min", and "your server is back up". Clicking a toast runs P2. There is a quiet-hours setting and per-type toggles.
- **Value:** the realm's story pulls players back at the moments that matter. Scheduled rebellion windows are designed for exactly this.
- **Feasibility:** high. The Electron `Tray` and `Notification` APIs [GENERIC] and the event types already exist [REPO README event types].
- **Security:** read-only. Toasts render plain text (names can contain anything). The tray app has no admin features in player mode.
- **Effort:** M.
- **MVP cut:** *later* (it needs the Beacon public). *Now*: the event-to-toast mapping and its tests against `chronicle/sample-data`.

### P10. A 60-second first-time onboarding

- **What:**
  - **In the launcher:** 5 swipe cards (Houses → Oaths → The Crown → Rebellion windows → Ransom), each one sentence plus one command, using text from `docs/community/how-to-play.md` and original lore only [REPO `docs/community`], then "Join Realm I". Skippable, and shown once.
  - **In game:** on a player's first spawn, a `RealmOps` plugin shows one popup with `player.ShowPopup(title, message)` [REPO `oxide-rok-api.md` §3: `ShowPopup`] ("Welcome to the Realm. Type /house to begin") and logs `firstJoin` so it never repeats.
- **Value:** new players understand the house and crown loop before their first death.
- **Feasibility:** high. **UNVERIFIED:** the default parameters of `ShowPopup` [REPO `oxide-rok-api.md`].
- **Effort:** S (launcher) + S (plugin).
- **MVP cut:** *now*: the launcher cards. *Later*: the in-game popup (it needs a live plugin test).

### P11. Launcher self-update

- **What:** the signed manifest (X1) carries `launcher.version`, `url` and `sha256`. On start, if newer, the app shows "Update ready". On click it downloads the file through `net.fetch` (as the Oxide download already does [REPO `launcher/README.md`]), verifies the **sha256 from the signed manifest**, and runs the NSIS installer, then quits.
- **Value:** players stay current without revisiting a download page.
- **Feasibility:** high. electron-updater is not needed, and `build.publish` stays `null` [REPO].
- **Security:** the trust chain is Ed25519 manifest → sha256 → installer. The installer has no Authenticode signature, so **Windows SmartScreen will warn** on first install [GENERIC]. Code signing is a later, paid decision. Never auto-run without a click. Refuse downgrades.
- **Effort:** M.
- **MVP cut:** *later*, but put the manifest field into the schema now.

### P12. Pre-flight and version match

- **What:** before launch, compare the client's `buildid` (from `appmanifest_344760.acf`) with the server's `Version` from the Registry/Beacon (F6 `VN`). If the server would reject the client ("denied connection due to wrong version" [IL `0x3e1d3`]), say "Steam is updating Reign of Kings, try again after it finishes" instead of letting the player hit an in-game error.
- **Feasibility:** **UNVERIFIED:** how `buildid` relates to the game's own version string. Collect both on the owner PC (T12) before building a comparison.
- **Effort:** S once mapped.
- **MVP cut:** *later*.

### P13. Restart hand-off

- **What:** when the Beacon reports `restartAt` within 15 min, the tray toasts the warning. After the server comes back, it toasts "Realm I is back. Rejoin", one click into P2.
- **Value:** the nightly restart stops costing players.
- **Effort:** S on top of P9.
- **MVP cut:** *later*.

---

## 6. Decisions the owner must make (rule-boundary items)

| # | Decision | Recommendation |
|---|---|---|
| D1 | May the client, **with player opt-in**, write the game's **profile** file `Lobby History.bin` (F7) to add Realm favourites? It is not an install file; the game writes it for the player. | Yes, opt-in only, with backup and Undo, after T10. |
| D2 | May the owner app, **with per-action owner consent and UAC**, add scoped Windows firewall rules and UPnP mappings (O6)? This reverses today's "never touches the firewall/router" rule [REPO]. | Yes, limited to the exact rule names and ports in the port table, with "Undo all". Keep it off until Phase 1 of the launch plan. |
| D3 | May the client, **with player opt-in**, edit `Configuration\Lobby Mirrors.cfg` **inside the game install folder** (F4)? It is a game-generated config meant for this purpose, but it lives in the Steam folder. | Defer. First run owner-side X3 (server copy only) and P5. Revisit after confirming that EAC ignores the file (T11) and that the README rules can be worded to allow "game-generated user config, never binaries or assets". |
| D4 | Publish a home IP in a public manifest, or use a DNS name / VPS? | Use a DNS name from day one. Plan a VPS before going beyond the closed test, because a public home IP invites DDoS. |
| D5 | `timeBetweenPlayerJoin`: 10 (default) or lower? (F9) | 3 for tests, then tune. |

---

## 7. Tests to run on the owner's PC before flipping defaults

| ID | Test | Unblocks |
|---|---|---|
| T1 | In Steam, set the RoK launch options to `-ip 127.0.0.1 -port 7350` and press Play while the local server runs. Does the game skip the menu and join? | P2 |
| T2 | Same as T1, but via `steam://run/344760//-ip 127.0.0.1 -port 7350/` in Win+R, then via `steam.exe -applaunch 344760 -ip 127.0.0.1 -port 7350`. Record any Steam dialog. | P2 |
| T3 | Repeat with a host name (`localhost`). | P2, X1 host names |
| T4 | Repeat with the server stopped, and with `maxPlayers = 1` plus a second account. What does the player see? | P2, P7 |
| T5 | Start the server copy with its `Lobby Mirrors.cfg` POST URLs pointed at a loopback listener. Do the `server_show`/`server_update` posts arrive (F6)? | X3, O2 |
| T6 | Run two server copies with the port table (s1, s2). Do both log `Steam game server started … Secure: True`, and can a client join each? | O1 |
| T7 | Set `restartTime = 120`. Are the notices seen in game, and does `ROK.exe` exit afterwards? Is the world saved? | O3 |
| T8 | While the server runs, run `netstat -ano` and confirm that the PID owning UDP 7350 and 27015 is `ROK.exe`. | O6 |
| T9 | Send an A2S_INFO query to 127.0.0.1:27015. Does it reply? What name and slots does it report? | P3, O2 |
| T10 | Make a backup copy of `Lobby History.bin`, add a favourite in game, then decode it with the P5 codec (dry-run). Does it round-trip byte for byte? | P5 |
| T11 | Find where the client creates `Lobby Mirrors.cfg`, and whether it is listed in the Steam depot (after "Verify integrity of game files", is it untouched?). | P6 / D3 |
| T12 | Record the server's `VN` (from T5) and the client `buildid`. | P12 |
| T13 | Open `http://store.codehatch.com/rok/lobby.php` in a browser. Is it alive? | P6 priority |

---

## 8. Prioritized MVP: what the build team implements now

Ordered by value ÷ effort, given that only s1 exists today. Everything stays local: nothing is published, and the firewall and router are untouched.

1. **Supervisor: crash restart with backoff and loop breaker (O2 MVP)** (M). Extend `lib/server-process.js` with an "expected stop" flag and the backoff schedule. Unit-test the schedule and the breaker with the existing imitation server.
2. **Backup before every restart, with retention (O4)** (S). Hook into the supervisor's restart path. Test the retention pruning on a temp folder.
3. **Scheduled daily restart via the `restartTime` trick and a `realm.save` command (O3 MVP)** (S+S). Settings: slot time, on/off. A new minimal `plugins/RealmOps.cs` (C# 3, compile-checked by `tools/plugin-compile-check/check.sh`) with `realm.save` → `Game.Save()`.
4. **120 slots and join throttle (O1 data model, P7 owner half)** (S). Settings fields `maxPlayers` (default 120) and `timeBetweenPlayerJoin` (default 3, range 1–30). Both are existing keys, written with the existing rewrite-only rule.
5. **Multi-server data model and port table (O1 MVP)** (M). `lib/fleet-ports.js` holds the table. Settings migrate to `servers: [s1]`. The UI shows one server card. "Update plugins" targets all servers. No clone flow yet.
6. **Registry on 127.0.0.1:8786 (X3)** (S). Implements `server_show`/`server_update`/`server_hide`/`get_ip`/`get_host` with ACK/NAK replies. An owner-side **Setup step** points the server copy's `Lobby Mirrors.cfg` POST URLs at it (existing lines only; the file is created by the game on first run). Test with recorded form posts.
7. **Health dashboard (O5 MVP)** (M). State, uptime, players (Registry → `RealmState.json` fallback), last/next restart, last backup, crash count over 24 h.
8. **Signed manifest: schema, Ed25519 sign/verify, anti-rollback, owner "Export signed manifest" (X1 MVP)** (S+S). Key in `safeStorage`. Tests: tampered byte, wrong key, lower `seq` and expired manifest are all rejected.
9. **Player readiness: Steam and game install detection and `steam://install/344760` (P1)** (S). Add `steam://install/<digits>` to the `openExternalAllowed` allow-list, with a test.
10. **Deep link `realm://join/<id>` with a confirmation screen (P4 MVP)** (S). Ids only; validated in main. Tests for malformed links.
11. **Experimental auto-connect PLAY via Steam launch arguments, off by default (P2 MVP)** (S). Strict allow-list regex and `execFile` for the `-applaunch` variant. The existing "copy address" flow stays the default.
12. **First-time 60-second onboarding cards (P10 launcher half)** (S).
13. **Beacon `/status.json` and `/lobby` on loopback, with a lobby-format encoder and golden tests (X2 + P6 owner half)** (M). It is not exposed publicly yet.
14. **A2S ping helper (`dgram`, no dependencies) and the P5 `Lobby History.bin` codec in dry-run mode** (S+S). Building blocks for P3, O2-watchdog and P5, each with unit tests (round trip on a synthetic file).
15. **"Go Public" preview (O6 MVP)** (S). Parse the Steam log line for public IP, Logged and Secure. Show exactly which settings, firewall rules and ports *would* change, but make no changes yet (D2 pending).

Each item ships with `npm run check` and `npm test` passing, README updates for anything user-visible, and every unproven assumption marked **UNVERIFIED** in code comments, as the repo already does.

**Explicitly later:** the Add-server clone flow (after T6), firewall/UPnP (D2), the friend probe, public Beacon, tray notifications, queue toasts, house affinity, Join best server, favourites write (D1/T10), client `Lobby Mirrors.cfg` edit (D3/T11), self-update, Discord alerts, version pre-flight, and the auto-publish of the manifest.
