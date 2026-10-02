# Joining and scaling: auto-connect, 120 players, status and multi-instance hosting

This page answers four questions for the Realm launcher and the server host:

1. How can the player's own Reign of Kings client be told which server to join, so it connects with no typing?
2. Can a server take 120 players?
3. Does the server answer Steam status queries (A2S), and on which port?
4. Which ports does each server instance need, and how do you run up to four instances on one PC?

Each fact carries a tag:

| Tag | Meaning |
|---|---|
| **[IL]** | Read from the DLL's metadata or IL: TypeDef tokens, method RVAs, the user-string heap. |
| **[DEC]** | Read from a decompiled C# view of the same IL. Control flow, such as "if X then Y", comes from here. |
| **[SEC]** | From a secondary source, such as a guide snippet or a hosting template, or from generic Steam behaviour. |
| **UNVERIFIED** | Not provable from the DLL. It needs a test on a real PC. |

## 0. What was examined

| Item | Value |
|---|---|
| Source zip | `Oxide.ReignOfKings.zip`, release 2.0.3867, sha256 `6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8` (https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip) |
| File | `ROK_Data/Managed/Assembly-CSharp.dll`. This is the same file as in [mods-keys-from-dll.md](mods-keys-from-dll.md): sha256 `ffea7074…435e`, MVID `7ae521be-ddca-4bb1-9869-8132cf2a08fd` |
| Tools | `ilspycmd` 10.1.1.8388 (NuGet) produced a whole-assembly decompile. It was run under the .NET 10 runtime that `tools/plugin-compile-check/check.sh` already caches. Python `dnfile` 0.18.0 read the TypeDef tokens and method RVAs, and a raw UTF-16 scan read the user strings. Nothing in the DLL was executed. |
| Network | The research container's proxy blocked `store.codehatch.com` and `api.steamcmd.net`. The live lobby and Steam's launch configuration for app 344760 could not be checked from here. |

**Caveat that applies to everything below:** this DLL is the **server** assembly as patched by Oxide. Oxide's patcher only adds hook calls (`Interface.CallHook`) and does not remove code. The project brief says the client and the dedicated server share one Unity build and so one `Assembly-CSharp.dll`. The DLL itself supports this: it contains client-only code such as `JoinGameOnClick`, the lobby menu and EAC client start-up. **UNVERIFIED:** that the player's client build matches this one byte for byte, apart from Oxide's patches. The check below is read-only, so it does not modify anything. It reads the player's own DLL and prints `True` if the quick-join code is there:

```powershell
$dll = "C:\Program Files (x86)\Steam\steamapps\common\Reign Of Kings\ROK_Data\Managed\Assembly-CSharp.dll"   # adjust to the install
$t = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($dll))
'Attempting join game','Lobby Mirrors.cfg','Lobby History.bin' | % { "$_ : " + $t.Contains($_) }
```

---

## 1. Auto-connect: the client **does** have a command-line quick join

### 1.1 The parser

`CodeHatch.Engine.Core.Utility.Static.SystemUtil` (TypeDef `0x02000678`), `get_CommandLineProps` at RVA `0xc5408` [IL]. [DEC] It reads `Environment.GetCommandLineArgs()`. Every argument that starts with `-` becomes a key with the `-` removed. If the next argument does not start with `-`, it becomes that key's value. The result is a `Dictionary<string,string>`. **Arguments that start with `+` are ignored**, and so are bare words.

### 1.2 `QuickJoinBypass`: joins a server at start-up

`CodeHatch.QuickJoinBypass` (TypeDef `0x020004b0`). `Awake` is at RVA `0x86748` and `Start` at `0x868a4` [IL]. [DEC]

| Argument | Meaning | Default if missing or unparsable |
|---|---|---|
| `-ip <host>` | Server address. **It is required: this component turns itself on only if `ip` is present** (`enabled = CommandLineProps.ContainsKey("ip") \|\| Override`). | `localhost` |
| `-port <n>` | Game port (`portNumber` on the server) | `7350` |
| `-pass <pw>` | Server password | empty |
| `-char <n>` | Parsed into `characterIndex` but **not used** anywhere else in the class | `-1` |

`Start()` logs `Attempting join game: {ip}:{port} with character {n}. (Password: Yes/No)` and then calls `CodeHatch.Engine.Core.Gaming.Game.Join(ip, port, pass)`. This is the same method the in-game direct-connect button (`JoinGameOnClick.OnClick`, RVA `0x3e840`) and the lobby list (`LobbyItem`) call [DEC].

On `ConnectionLostEvent`, the client exits **only if** `-batchmode` is also present [DEC]. A normal player is not affected.

### 1.3 How `-ip` changes start-up

`LoadLevelInBG.Start` (TypeDef `0x02000205`, RVA `0x3d4c0`) [DEC]:

```csharp
string text = Environment.CommandLine.ToLower();
bool flag  = text.Contains("batchmode");
bool flag2 = text.Contains("ip");
if (allowDedicated && flag)       LoadLevelAsync(dedicatedLevelName, …);
else if (allowQuickJoin && flag2) LoadLevelAsync(quickJoinLevelName, …);
else                              LoadLevelAsync(levelName, …);
```

- With `-ip`, the client loads a separate **quick-join scene** in place of the normal menu scene. The scene names are serialized Unity scene data, not code. **UNVERIFIED:** that `QuickJoinBypass` is placed in that scene in the retail client. The class exists, and `LoadLevelInBG` has a quick-join branch, so this is very likely the intended path.
- **Pitfall:** the test is a substring match on the **whole** lower-cased command line, including the exe path. Any path or argument that contains the letters `ip` (for example `D:\Shipping\...`) sends the client down the quick-join branch. Only `-ip` turns `QuickJoinBypass` on, though.
- **Pitfall for the server:** `DedicatedServerBypass.Enabled` (`get_Enabled`, RVA `0x8749c`) is `ContainsKey("batchmode") && !ContainsKey("ip")` before the component wakes [DEC]. **Never pass `-ip` to the server process.** It would stop the process being a dedicated server.

### 1.4 Gates in `Game.Join` (RVA `0x72dc0`) and `JoinWithEndpoint` (RVA `0x72f54`) [DEC]

1. If EAC is not active, the client shows `Please run the game from Reign Of Kings.exe`. The client has to be started through the EAC bootstrapper, which is how Steam starts it.
2. If EAC is active but not yet initialized, the client shows `EAC has not intiailized yet. Please try again.` (the typo is in the game). **UNVERIFIED and a real risk:** `QuickJoinBypass.Start` runs as soon as the quick-join scene loads. If EAC is not ready by then, the join fails once with this popup. The code does not retry.
3. The host is resolved with `NetworkUtil.GetIpAddress` → `Dns.GetHostAddresses` (RVA `0x189d90`). **A DNS name works as well as an IP.** The launcher can therefore ship a name such as `play.<your-domain>` and keep working when the home IP changes, for example through a dynamic-DNS name.
4. Steam must be logged in, because the client creates a Steam auth ticket. It then calls uLink `Network.Connect` with a `ConnectionLoginData` (version, name, SteamID, password, ticket).

### 1.5 The exact launch command

The player's own Steam starts the player's own copy, with extra arguments. No game file is read, patched or launched directly.

```
steam://run/344760//-ip <host> -port <gamePort>/
```

URL-encode the spaces, for example `steam://run/344760//-ip%20play.example.org%20-port%207350/`. Add `-pass <pw>` only for a passworded server.

| Fact | Status |
|---|---|
| `steam://run/<appid>//<args>/` is Valve's documented form for launching with arguments (https://developer.valvesoftware.com/wiki/Steam_browser_protocol). An equivalent is `steam.exe -applaunch 344760 -ip <host> -port <port>`. | [SEC] generic Steam |
| Steam shows the player a confirmation dialog when a URL launches a game with custom arguments. | [SEC] generic Steam behaviour, **UNVERIFIED** for this account and client version |
| **The EAC bootstrapper (`Reign Of Kings.exe`) forwards these arguments to `ROK.exe`.** | **UNVERIFIED. This is the deciding test.** |
| `steam://connect/<ip>:<port>` makes Steam start the game with `+connect ip:port`. The game ignores `+` arguments (§1.1), and the DLL contains no `+connect`, `-connect`, `connect_lobby` or `steam://` string (UTF-16 and ASCII scan, 0 hits). **`steam://connect` does not auto-join.** | [IL] (absence) |
| No game code subscribes to `GameRichPresenceJoinRequested_t`, `GameServerChangeRequested_t` or `GameLobbyJoinRequested_t`, and none calls `SetRichPresence`. Those types exist only inside the bundled Steamworks.NET wrapper. "Join game" through the Steam friends list therefore does nothing. | [DEC] (no references outside `Steamworks/`) |

**Two-minute test on the owner's PC** (this changes no files):

1. Start the local test server and wait for `Server for N players started on port 7350.`
2. In Steam, open Reign of Kings → Properties → Launch Options and enter `-ip 127.0.0.1 -port 7350`. Then press Play in Steam.
3. Pass: the client skips the menu and joins. The client log contains `Attempting join game: 127.0.0.1:7350`. **UNVERIFIED:** the log location. On Unity 5 it is usually `ROK_Data\output_log.txt`.
4. Clear the launch options afterwards. Then try the launcher route, `steam://run/344760//-ip%20127.0.0.1%20-port%207350/`, and note whether Steam shows a dialog.

Launcher note: `launcher/main.js` currently lets only `steam://rungameid/<digits>` through `openExternalAllowed`. A quick-join button needs a second, equally strict pattern. It should allow only `steam://run/344760//` followed by `-ip <host> -port <digits>`, with the host checked against the configured server list.

### 1.6 What else is in the code that the launcher should **not** use

- `SimulateJoinServer` (RVA `0x193dbc`): `-rejoin <seconds>` together with `-ip/-port/-pass` joins, skips character customization (`FirstCharacterCreation.SkipCustomization = true`), leaves after N seconds and rejoins, over and over. It is a developer soak-test tool. It could be useful for a **local load test with test accounts**, but never for players. **UNVERIFIED:** whether it is in any shipped scene.
- PlayerPrefs: the DLL's `PlayerPrefs` keys are only language, sound, quality, monitor and cInput key bindings. **No PlayerPrefs key holds a last server.** The direct-connect fields are NGUI `UIInput` components. NGUI can save an input to PlayerPrefs through the scene-serialized `savedAs` field, but **UNVERIFIED:** whether RoK's direct-connect inputs set it. Because the command line works, this route is not needed. Note also that `JoinGameOnClick.DefaultPort` is `7250` in code. It is replaced whenever the scene's Port field is wired up.

### 1.7 A fallback that seeds the in-game browser (needs an owner decision; it writes files outside the game folder)

If the EAC bootstrapper drops the arguments, these two documented data files are the next best route. Both are written by the game itself, not shipped with it.

**a) `Lobby History.bin`: favourites shown in the server list.** `LobbyHistory` (TypeDef `0x02001770`, `GetLobbyHistory` RVA `0x2c6184`) [DEC]:

- Path: `Application.persistentDataPath\Profiles\<SteamID64>\Lobby History.bin`. `SerializationUtil.GetProfileReader` combines `SaveLocation.Profiles` (= `persistentDataPath + "/Profiles"`) with `User.SteamIDString`. On Windows, Unity's `persistentDataPath` is `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>`. A community post gives `...\LocalLow\CodeHatch\ReignOfKings\profiles\<id>\` ([server-reference.md §3](server-reference.md)). The company and product strings are Unity player settings, not code, so they are [SEC].
- Format: one .NET `BinaryWriter.Write(string)` (a 7-bit-encoded length prefix, then UTF-8). Records are separated by U+001E. Each record is five fields separated by U+001F: `Name, IPv4, IPv6, Port, Flag`. Flag bits: `1` visited, `2` previous, `4` **favourite**.
- History entries that the lobby download does not list are still added to the browser, with Version `History` (`Lobby.OnRefreshResult`, RVA `0x2c5024`). So a favourite appears even if no lobby service lists the server.
- `Match()` pairs a history entry with a lobby entry only when IP, port **and** name are all equal.

**b) `Lobby Mirrors.cfg`: a custom server-list URL.** `LobbyMirrors` (TypeDef `0x02001779`) and `MirrorFile.Load` (RVA `0x2c8414`) [DEC]:

- Path: `GameInfo.ConfigFolder + "/Lobby Mirrors.cfg"`. `ConfigFolder` is scene data. `ServerSettings.cfg` uses the same folder and is known to sit in `Configuration\` next to the exe, so the file is very likely `<install>\Configuration\Lobby Mirrors.cfg`. This is **UNVERIFIED** for the client.
- Keys: `UseCustomMirrors` (default True), list `CustomMirrors`, `UseDefaultMirrors` (default True; it rewrites `DefaultMirrors` to `http://store.codehatch.com/rok/lobby.php`), `RequestIP`, `RequestHost`, `ServerShow`, `ServerHide`, `ServerUpdate`.
- The client chooses **one mirror at random** from the custom and default lists (`Lobby.RefreshCoroutine`, RVA `0x2c50a8`). After two failed downloads it falls back to the hard-coded CodeHatch URL. To get a predictable list, set `UseDefaultMirrors = 'False'`.
- The response format the client parses: the reply starts with `0x06` (ACK). The records follow after a `0x1C`. Each record is `0x1E` + fields separated by `0x1F` + `0x1D`. A record has 15 fields (Alpha 0.1) or 17 fields (Alpha 1.8): `ID, Name, Version, IP, Port, IsSecure, Type, PlayerLimit, PlayerCount, [PingLimit, IsPingLimited], IsDedicated, IsOfficial, IsPrivate, IsOnline, DateUpdated, DateCreated`, with booleans written as `"1"`.
- Whether `http://store.codehatch.com/rok/lobby.php` still answers is **UNVERIFIED**, because the host was blocked from here.

**Rule check:** neither file is a game binary or asset. Both are user data that the game creates and is designed to have edited ("Download the lobby using custom servers."). Writing them is still **writing into the player's game folder or profile**. The owner should decide whether that is acceptable under the project's "never modify game files" rule before any launcher writes them. **UNVERIFIED:** whether EAC objects to either file.

---

## 2. Player capacity: 120 is allowed by the code

| Fact | Evidence |
|---|---|
| The key is `maxPlayers`. It is read as `int`, with code default `30`. It has **no clamp**: `ServerSettings` clamps only `pingLimit` (50..10000), `pingGraphLength` (10..3600) and the report-ban values. | [DEC] `ServerSettingsFile.Load` (TypeDef `0x020004c0`, RVA `0x89380`) and the `ServerSettings` property setters |
| `DedicatedServerBypass.StartServer` (RVA `0x876e8`) stores it as `serverDataModule.PlayerLimit = (ushort)Settings.MaxPlayers`, a range of 0..65535. A live change through `SetConfigValue("maxPlayers")` is cast the same way in `CoreServer.OnServerSettingsUpdate`. | [DEC] |
| The uLink listener is opened with `Network.InitializeServer(int.MaxValue, Port)`. **The transport has no connection cap.** | [DEC] `CoreServer`, the line that logs `Server for {0} players started on port {1}.` |
| The limit is enforced only in `CoreServer.PlayerShouldBeQueued` (RVA `0x186cb0`). A player joins at once if `PlayerCount < PlayerLimit`, at least `timeBetweenPlayerJoin` seconds have passed since the last join, and the player is first in the queue. Otherwise the player waits in the **built-in queue**. | [DEC] |
| Permissions that bypass this: `codehatch.login.ignore.playerqueue` (skips the queue and the join delay) and `codehatch.login.ignore.playerlimit` (may join above the limit). | [DEC] `PlayerShouldBeQueued` |
| `timeBetweenPlayerJoin` defaults to **10 s**. Filling 120 slots at the default takes at least about 20 minutes, because each join waits 10 s after the previous one. For a launch event, lower it (for example to `2`). **UNVERIFIED:** what the server can actually handle. | [DEC] `ServerSettings.TimeBetweenPlayerJoin = 10f` |
| The ping server's `Listen(100)` is a **TCP backlog**, not a player cap. | [DEC] `PingGraphManager.OnGameStart` (RVA `0x18ae08`) → `SimpleSocketServer.Start` |
| The EAC server is created as `new EasyAntiCheatServer<Client>(OnClientStatusChange, 60, "EasyAntiCheat.Game.Server")`. **UNVERIFIED:** what `60` means. The EAC SDK assembly is not in the zip. It is very likely an update interval rather than a client cap, but this has not been checked. | [DEC] `EACIntegration.InitializeServer` |
| **UNVERIFIED:** CPU, RAM and bandwidth at 120 players. The game was designed for much smaller servers (code default 30). Only a load test can show whether 120 is playable. | — |

`gameMode` is **not** a `ServerSettings.cfg` key. The `GameMode` field exists, but `Load()` never reads it. This corrects the unconfirmed snippet in [server-reference.md §4.1](server-reference.md). Optional keys that are read only if already present in the file: `webApiUrl`, `webApiKey`, `debugFillPages`, `profileToFile`, `dumpHeatmapsToFile` and `forceExitOnShutdown`.

---

## 3. Discovery and status

### 3.1 Steam game server: A2S answers on `steamAuthPort` (UDP), with poor data

`CodeHatch.Engine.Internet.SteamServer.StartServer` (TypeDef `0x02000457`, RVA `0x7aa78`) [DEC]. Steamworks.NET is 7.0.0, on Steamworks SDK 1.34 (`Steamworks.Version`) [DEC].

```csharp
GameServer.Init(0, 8766, Settings.PortNumber, (ushort)Settings.SteamAuthPort,
                EServerMode.eServerModeAuthenticationAndSecure, "1.0.0.0");
SteamGameServer.SetDedicatedServer(true);
SteamGameServer.SetProduct("Reign Of Kings");
SteamGameServer.SetGameDescription("ROK Server");
SteamGameServer.SetServerName("Another ROK Server");
SteamGameServer.LogOnAnonymous();
SteamGameServer.EnableHeartbeats(true);
```

- The SDK parameters are `(unIP, usSteamPort, usGamePort, usQueryPort, …)`. So **`steamAuthPort` is the Steam query port.** Steam's game-server library answers A2S_INFO, A2S_PLAYER and A2S_RULES there over UDP, and heartbeats to the master server. The game port, 7350, does not answer A2S.
- `usSteamPort` is **hard-coded to 8766**. It is the local port the Steam library uses to talk out to Steam. It needs no port forward. **UNVERIFIED:** whether four instances sharing 8766 conflict. The game's own error text says only "make sure the steamAuthPort in ServerSettings is different for each server", which suggests that Code Hatch ran several servers per host.
- Before `Init`, the server checks `NetworkUtil.IsTCPPortOpen(steamAuthPort)`. That check only looks for an **active TCP connection** on the port number. If one exists, Steam is skipped with "Port already in use…".
- **The data A2S reports is mostly fixed** [DEC]. Outside the `Steamworks/` wrapper, the only `SteamGameServer.*` calls in the whole assembly are the ones above, plus Begin/EndAuthSession, SendUserDisconnect and GetPublicIP. Nothing calls `SetServerName` with the real name, `SetMaxPlayerCount`, `SetMapName`, `SetPasswordProtected`, `BUpdateUserData` or `SetKeyValue`. So every RoK server should appear as **"Another ROK Server"**, description "ROK Server", with **max players 0** and no map. **UNVERIFIED:** the player count Steam reports. Players are registered through `BeginAuthSession`, but whether that counts them in A2S has not been checked.
- On success the server logs `Steam game server started. (IP: <public ip>, Logged: True, Secure: True)`. The launcher can read the **public IP** from this line in the console.
- If any of `GameServer.Init`, `BLoggedOn` or `BSecure` (30 s each) fails, the server logs an error and keeps running **without Steam auth**. `SteamServer.BeginAuthSession` then returns OK and calls back `false`. **UNVERIFIED:** whether players can still join in that state.

### 3.2 The in-game server list is CodeHatch's HTTP lobby, not Steam

- `ServerLobbyModule` (TypeDef `0x020004b7`): when the game starts on a local server, it POSTs a form to `ServerShow`. It POSTs again to `ServerUpdate` every 150 s, and polls every 30 s. On shutdown it POSTs to `ServerHide`. The URLs come from the **server's own** `Configuration\Lobby Mirrors.cfg`, with defaults `http://store.codehatch.com/rok/server_show.php`, `server_update.php` and `server_hide.php` [DEC] (`GetFullForm` RVA `0x888d0`). The form fields are `ID NM VN IP PN PW TP PL PC NL NE DE OF PR ON`. `PW` sends only TRUE or FALSE, never the password.
- **What `isPrivate` does:** it is written as `PR=TRUE` in that same POST. The server **still posts to the lobby**, and hiding the server is left to the lobby service. The client parser reads `IsPrivate`, but the client UI has no filter on it [DEC]. `isPrivate` is **not** passed to Steam: `SteamServer` never reads it, and heartbeats are always on. So a private server can still appear in Steam's master list as "Another ROK Server". **UNVERIFIED** at run time.
- Pointing a server's `Lobby Mirrors.cfg` at a Realm URL is a **server-side config change**. It is allowed under the project rules. It lets a small Realm lobby service collect live name, players, max and port for each instance, with no plugin.

### 3.3 Status options for the launcher (best first)

1. **An Oxide plugin status feed**, through the existing Realm Chronicle path. `Server.PlayerCount`, `Server.PlayerLimit` and the server name are already available to plugins ([oxide-rok-api.md](oxide-rok-api.md)). This is the most accurate source and is fully server-side.
2. **A Realm lobby endpoint** that receives the `ServerShow` and `ServerUpdate` POSTs from §3.2. It needs no plugin and works for every instance.
3. **Fix the A2S data with a plugin.** The Oxide sandbox whitelists the `Steamworks` namespace ([server-reference.md §6](server-reference.md)). A plugin could call `SteamGameServer.SetServerName(...)`, `SetMaxPlayerCount(Server.PlayerLimit)` and `SetPasswordProtected(...)` after start-up, so that A2S on `steamAuthPort` returns real values. **UNVERIFIED:** whether this works when called from a plugin.

---

## 4. Ports, firewall and up to four instances on one PC

### 4.1 What each instance opens

| Purpose | Setting | Default | Protocol and bind | Forward to the internet? | Evidence |
|---|---|---|---|---|---|
| Game traffic (uLink) | `portNumber` | 7350 | UDP, on `bindIP` (`Network.config.localIP` if it parses as an IP) | **Yes, UDP** | [DEC] `CoreServer`; config comment `[Port-forward as UDP]` |
| Ping system | `pingPort` | 7350 | **TCP**, always `IPAddress.Any`; it ignores `bindIP` | **Yes, TCP.** With `enablePingLimit = True`, a player who cannot reach it is kicked with "failing to initialize with the ping system". | [DEC] `PingGraphManager`; config comment `[Port-forward as TCP] … can be shared with the gameplay port` |
| Steam query, A2S and master-server heartbeat | `steamAuthPort` | 27015 | UDP (Steam library) | Yes, UDP, if the server should appear in Steam's list or answer A2S from outside | [DEC] `SteamServer.StartServer` |
| Steam client port | none (hard-coded) | 8766 | UDP, outbound to Steam | No | [DEC] |
| **Admin console** | command-line `-cport <n>` | **11000** | TCP on **all interfaces**. Any connected client's text is run as a server console command, and **no authentication was found in the code**. | **Never.** Block it inbound in Windows Firewall. | [DEC] `SocketAdminConsole.OnEnable` (TypeDef `0x020000b8`, RVA `0x1d174`) |
| RCON (only through the `Server.exe` wrapper) | `rConPort` in `ConsoleSettings.cfg` | 27015 | TCP | Only if remote admin is wanted | [SEC] [server-reference.md §2](server-reference.md) |

**Warnings about the admin console** [DEC]. **UNVERIFIED** at run time: whether this component is active in the dedicated scene. If the log shows `Admin console enabled.`, it is.

- When the **last admin client disconnects**, it calls `Server.Shutdown()` (`OnClientDisconnected`, RVA `0x1d784`). If port 11000 were reachable from outside, a stranger could connect, run admin commands, and shut the server down just by disconnecting.
- If `-cport` is passed and **no client connects within 10 s**, the server shuts itself down. `Server.exe` probably connects here. **Do not pass `-cport` when running `ROK.exe -batchmode` directly** unless something connects within 10 s.
- If the port is already in use, the bind error is caught and logged, and the server runs without a console. Running several instances with no `-cport` should therefore be harmless: the first instance takes 11000 and the others log an error. **UNVERIFIED** at run time.
- Protocol, for a future launcher console: TCP frames of `int32 size` (body length + 14), `int32 id`, `int32 type` (0 Authentication, 1 Message, 2 Response, 3 Disconnect), the body in ASCII, then two zero bytes. Bodies are split at 4082 bytes. The server sends an empty Message as a keep-alive every second, and log lines arrive prefixed `[D]`, `[I]`, `[W]` or `[E]`. [DEC] `CodeHatch.Engine.Sockets.Packet` and `SocketAdminConsole`. A launcher holding this connection would get a live console and command channel for every `ROK.exe` instance without needing `Server.exe` or admin rights. Closing that connection **stops the server**, so the launcher must keep it open for the server's whole run. **UNVERIFIED:** whether there is an Authentication handshake. None is checked in `OnMessageReceived`, which runs every packet whose type is not Response.

### 4.2 Four instances: a suggested layout using only proven keys

Each instance needs its **own folder**, a full copy of the server install. `Configuration\` (via `GameInfo.ConfigFolder`), `saveLocation = 'Saves/'` and `Mods\` are all relative to the working directory. Start each `ROK.exe` with its own folder as the working directory. **UNVERIFIED:** whether one shared install could serve several instances through different working directories. Copies are the safe choice, at the cost of disk space.

| Instance | Folder | `portNumber` (UDP) | `pingPort` (TCP) | `steamAuthPort` (UDP) | `-cport` (local only) |
|---|---|---|---|---|---|
| 1 (now) | `G:\RealmTest\server` | 7350 | 7350 | 27015 | not passed (11000) |
| 2 | `G:\Realm\s2` | 7351 | 7351 | 27016 | not passed |
| 3 | `G:\Realm\s3` | 7352 | 7352 | 27017 | not passed |
| 4 | `G:\Realm\s4` | 7353 | 7353 | 27018 | not passed |

- The table shares `pingPort` with `portNumber`. The config comment says this is allowed: TCP and UDP sockets on the same number do not collide.
- Keep each instance's `steamAuthPort` different from any `rConPort` in use.
- The **launch command** is the same for each instance and is run from its own folder: `ROK.exe -batchmode -nographics -silentcrash`. There is **no config-path argument**: the DLL's only command-line keys are `batchmode`, `ip`, `port`, `pass`, `char`, `rejoin`, `cport`, `logFPS` and `profileToFile`.
- **First run exits.** When `ServerSettings.cfg` did not exist, `CoreServer` logs "This is the first time you have run this server." and calls `Program.Exit()` [DEC]. Either let each new folder run once, or have the launcher write a complete `ServerSettings.cfg` first.
- Router: forward UDP 7350–7353, TCP 7350–7353, and UDP 27015–27018 to the server PC. In Windows Firewall, allow those ports for `ROK.exe` and **block TCP 11000–11003 inbound**.

```powershell
# Run once in an elevated PowerShell. These only change Windows Firewall rules.
New-NetFirewallRule -DisplayName "Realm game (UDP)"  -Direction Inbound -Protocol UDP -LocalPort 7350-7353   -Action Allow
New-NetFirewallRule -DisplayName "Realm ping (TCP)"  -Direction Inbound -Protocol TCP -LocalPort 7350-7353   -Action Allow
New-NetFirewallRule -DisplayName "Realm query (UDP)" -Direction Inbound -Protocol UDP -LocalPort 27015-27018 -Action Allow
New-NetFirewallRule -DisplayName "Realm admin console BLOCK" -Direction Inbound -Protocol TCP -LocalPort 11000-11003 -Action Block
```

**Note for players on the same LAN as the server:** joining the public address from inside the same network needs NAT loopback on the router. Players on the LAN should use the server's LAN IP. This is generic networking behaviour and is not specific to RoK.

---

## 5. Ideas for a seamless setup, ranked by how much is proven

1. **One-click join.** The launcher lists the four Realm servers. Clicking one opens `steam://run/344760//-ip <host> -port <port>/` (§1.5). It needs no file writes and no client changes. Proven in code; whether the arguments survive the EAC bootstrapper is UNVERIFIED.
2. **A DNS name instead of an IP** (§1.4, item 3). The address never needs updating when the home IP changes.
3. **"Join the least full" button.** The launcher reads player counts from the status feed (§3.3, option 1 or 2) and picks the instance with the most room. It can also fall back to the next server if the chosen one is full, because the built-in queue would otherwise hold the player.
4. **Faster fills and staff bypass.** Lower `timeBetweenPlayerJoin` for launch events. Give staff `codehatch.login.ignore.playerqueue` and `codehatch.login.ignore.playerlimit` (§2).
5. **A Realm-only server list in the game's own browser.** Point each server's `Lobby Mirrors.cfg` at a Realm lobby endpoint (server-side, allowed). Clients see that list only if their own `Lobby Mirrors.cfg` points to it, which needs the owner decision in §1.7.
6. **Launcher as the admin console** through the `-cport` protocol (§4.1). It gives live logs and commands for every instance without `Server.exe` or admin rights. Closing the connection stops the server, which also makes a clean "Stop" button.
7. **A2S that shows the truth.** A small plugin sets the Steam server name and max players (§3.3, option 3), so Steam's own server browser and third-party trackers show Realm correctly.
8. **Favourites seeding** (§1.7a) as a last resort if quick join fails. It needs the owner's sign-off.

## 6. Tests still needed on the owner's PC

1. Steam launch options `-ip 127.0.0.1 -port 7350` → does the client join with no input? Does the EAC popup appear?
2. `steam://run/344760//-ip%20127.0.0.1%20-port%207350/` from the launcher → is there a Steam dialog, and does it join?
3. The read-only DLL string check in §0, on the player client's `Assembly-CSharp.dll`.
4. Server log: does `Admin console enabled.` appear? Does `netstat -ano | findstr :11000` show a listener?
5. An A2S_INFO query to `<host>:27015` (UDP) → what name, players and max are returned?
6. A second instance with the ports from §4.2 → do both start, and do both log `Steam game server started`?
7. A load test toward 120 players. Until it is done, treat 120 as a configuration value, not a proven capacity.
