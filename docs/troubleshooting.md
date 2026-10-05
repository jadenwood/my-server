# Troubleshooting: symptom, cause, fix

Every problem seen so far, in the order you would meet it: building, installing, starting the server, joining, going public, and the player app. The owner's path is [`START-HERE.md`](../START-HERE.md).

**Start with the Doctor.** In Realm Steward open **Doctor**, pick the server and press **Run checks**; in the player app press **Can't join?**. The top line names the problem and its fix. Paste a game popup into its box to have it matched against the game's own messages. **Copy report** gives a redacted text you can share. The Doctor only reads; it never changes the firewall, the router, a config file or a game file. How it works: [`connection-doctor.md`](connection-doctor.md).

**Where the real errors are.** The game writes its own messages to `Logs\Log[yyMMdd-hhmmss].txt` in its working folder, not to `-logFile`:
- server: `<server folder>\Logs\Log[...].txt` (for example `G:\RealmTest\server\Logs\`), also streamed into Steward's Servers console;
- game client: `<game install>\Logs\Log[...].txt`, and Steamworks errors in `<game install>\ROK_Data\output_log.txt`;
- most join refusals (password, full, banned, version) are only a popup on the player's screen. The server logs the matching `... denied connection because ...` line.

Status words: **Seen** means it happened on the owner's real Windows 11 server (2026-10-02). **UNVERIFIED** means the fix has not been seen working on real hardware.

## Building the installers (Build-Realm.bat)

`Build-Realm.bat` prints a `PROBLEM:` line and a `FIX:` line, then waits for a key. **UNVERIFIED:** the batch file has not been run on real Windows yet. Under wine on Linux, its checks, `npm ci` and `npm run check` ran in wine's `cmd.exe` with Windows Node.js, its build step made both installers with Windows Node.js, and the installers were installed and uninstalled.

| Symptom | Cause | Fix |
|---|---|---|
| `PROBLEM: Node.js is not installed, or this window cannot find it.` | Node.js is missing, or was installed after this window opened. | Install Node.js 22 LTS from <https://nodejs.org>, close the window, double-click `Build-Realm.bat` again. |
| `PROBLEM: Node.js 18.x is too old. Realm needs Node.js 22.` | An older Node.js is first on PATH. | Install Node.js 22 LTS (it replaces the old one). Node 23 or later is allowed with a note; use 22 if the build fails. |
| `PROBLEM: Git is not installed, ...` | Git for Windows is missing. | Install it from <https://git-scm.com/download/win> with the default options. |
| `PROBLEM: This Realm folder is on drive C:.` | Realm refuses server folders on C:, and the launcher tests make throwaway server folders. The tests get a temporary folder next to `Build-Realm.bat` (`launcher\.test-tmp`, removed afterwards), so the Realm folder must be off C:. With Windows Node.js and `%TEMP%` on C:, 12 of those tests fail (seen under wine). | Clone or move the Realm folder to the drive you use for the server, for example `G:\Realm`. |
| `PROBLEM: Build-Realm.bat is not next to the launcher folder.` | The .bat was copied somewhere else, for example the desktop. | Run it from the Realm folder you cloned or unzipped. A shortcut to it is fine. |
| `PROBLEM: npm ci failed.` with `EPERM` or `EBUSY` | A Realm, Realm Steward or Electron window started from this folder (`npm start`) holds files in `launcher\node_modules`. Antivirus scanning can do the same for a moment. | Close those windows and run again. |
| `PROBLEM: npm ci failed.` with `ENOTFOUND`, `ETIMEDOUT` or `ECONNRESET` | No internet, or a VPN or proxy blocks registry.npmjs.org. | Fix the connection, run again. |
| `PROBLEM: The launcher code check failed` or `A launcher test failed` | This copy of the code is broken, usually by a local edit or a half-finished pull. | Do not build from it. `git pull`, or undo your changes in `launcher\`, and run again. |
| `Cannot create symbolic link : A required privilege is not held by the client` | electron-builder's Windows tools archive (`winCodeSign-2.6.0`) contains two macOS symbolic links. Unpacking them needs administrator rights or Developer Mode. | Nothing: the build unpacks that archive itself without the macOS folder, before building or, if its own download fails, from the copy electron-builder left in the cache, and then tries once more (`Trying ... again with the Windows tools unpacked`). This failure and the recovery were reproduced with Windows Node.js under wine. If it still stops here: delete `%LOCALAPPDATA%\electron-builder\Cache\winCodeSign` and run again. Do not run the build as administrator. UNVERIFIED on real Windows. |
| `PROBLEM: building Realm-...exe failed.` with a download error | The first build downloads Electron (about 115 MB) and NSIS from github.com. | Check the connection, run again. Later builds use the cache. |
| `PROBLEM: only N MB free ...` or `ENOSPC` | The build needs about 2 GB free on the drive with the repository. | Free space, run again. |
| `Note: Realm-Setup has no published server list yet` | `launcher\player\player-config.json` has no key and list. | Normal before your first publish. Do START-HERE step 7, then build again before you send Realm-Setup to anyone. |
| `Note: this window runs as administrator.` | The .bat was started with "Run as administrator". | Not needed, and files it makes would then belong to the administrator. Close it and double-click instead. |

## Installing the apps

| Symptom | Cause | Fix |
|---|---|---|
| "Windows protected your PC" when starting `Realm-Steward-Setup-1.0.0.exe` or `Realm-Setup-1.0.0.exe` | The installers are not code-signed. Code signing is a separate, paid decision. | **More info > Run anyway**. Check the file against `SHA256SUMS.txt` first if it came over the internet: `Get-FileHash Realm-Setup-1.0.0.exe` in PowerShell. |
| You want to know what an uninstall removes | | It removes the program folder (`%LOCALAPPDATA%\Programs\Realm Steward` or `...\Realm`), its shortcuts and its entry in Installed apps. It keeps the settings (`%APPDATA%\Realm` with the signing key, `%APPDATA%\Realm Player`) and every server folder and world under `G:\RealmTest`. The player uninstaller also removes the `realm://` link handler when it still points at that install. Checked under wine; UNVERIFIED on Windows. |
| Edits to `config.json` or `news.json` next to `Realm Steward.exe` are gone after an update | Those two files belong to the install and are replaced on every install and update. | Keep your text in Steward's **Settings** (stored in `%APPDATA%\Realm`), or in the repository's `launcher\config.json` and `launcher\news.json` before you build. |

## Starting the server

| Symptom | Cause | Fix | Status |
|---|---|---|---|
| `spawn Server.exe EACCES` | `Server.exe` asks for administrator rights. | Steward runs `ROK.exe -batchmode -nographics -silentcrash` directly and remembers it (**Settings > Server program**). | Seen; fixed. |
| `The port 7350 is already being used by another application.` in the server log, or Steward refuses to start with "Another program already uses UDP 7350" | A second copy is running. Causes seen: Start pressed twice (Steward now has a per-server start lock); the Reign of Kings **game** itself; most likely the game's **`Server.exe` watchdog**, running elevated, which restarts `ROK.exe` about 2 s after you end it. | Read Steward's console: it names the holder. The game: close it, start the server first, then the game. A watchdog: Task Manager > Details, end **`Server.exe` first, then `ROK.exe`** (if Windows says access denied, open Task Manager as administrator). An older copy of this server: end it, press Start once. Steward never auto-restarts into a port clash. | Seen. Start lock and no-restart fixed; detection with "Stop it cleanly" / "Adopt it" is in flight (HANDOFF). |
| App crash `EBUSY ... Session.lock` | A backup read a file the running server keeps locked. | Backups now skip locked files. Update Steward. | Seen; fixed. |
| `Could not load world N`, and a new `Saves\SlotN` folder (Slot3, 6, 8, 9, 10 seen) after each failed start | Each failed start leaves a new world slot, so the next start may not load the world you played. | Under investigation (`docs/worlds.md`, in flight). Until then: fix what makes starts fail (usually the port clash above), do not delete `Saves\Slot*` folders, and back up with **Servers > Upkeep > Back up world** before experimenting. UNVERIFIED which slot the game picks. | Seen; open. |
| `TypeInitializationException` for `CodeHatch.Networking.Events.EventManager` in the server log, and the server stops at start-up | DLLs that the game's `Assembly-CSharp.dll` references are missing from the server's `ROK_Data\Managed` folder. `EventManager`'s static constructor walks Assembly-CSharp's exported types, which fails when a referenced assembly cannot be loaded. | Run the Doctor: its **Server game files** check names the missing DLLs and whether the Steam copy has them. Copy **only those** from the Steam copy of the dedicated server (`ROK_Data\Managed`) into the server's `ROK_Data\Managed`; never copy over the Oxide-patched `Assembly-CSharp.dll`. If a file is missing from the Steam copy too, run **Verify integrity of game files** on Reign of Kings Dedicated Server in Steam first. | Seen (an owner's server). The check is UNVERIFIED on a real server. |
| `This is the first time you have run this server.` and the server exits | A brand-new server folder writes its `Configuration\` files and exits on purpose. | Start it again. Steward then applies its settings; restart once more if Steward says so. The setup wizard counts this as success. | Expected. |
| The console stops at `Initialize engine version:` | That Unity line comes long before the world is loaded. | Wait for `Server for N players started on port P.` and then `Game has started.` (1 to 3 minutes). The Doctor's "Server log" step says when it is ready. | Seen. |
| `Could not start the server because an error occured. (...)` | The game could not open its network socket: `bindIP` is not an address of this PC, or a security product blocked it. | In Steward set **Network** to "This PC only" or "Public" (bindIP `127.0.0.1` or `0.0.0.0`) and start again. | From the DLL. |
| `The Steam game server could not initialize`, `Port already in use. Make sure you have allowed port N` | The Steam query port (`steamAuthPort`) is taken, or Steam's servers were unreachable. The server then runs without Steam auth. | Give every server its own query port (Steward does: 27015, 27025, ...), stop other servers using it, restart. UNVERIFIED whether players can join in this state. | From the DLL. |
| `eac_usermode ... blocked from loading into LSA` (a Windows notice) | Windows 11 Local Security Authority protection notice about the anti-cheat driver. | Harmless. Do not turn LSA protection off. | Seen; harmless. |
| `Lobby query failed` | Code Hatch's old lobby service is offline. | Harmless. Realm uses its own signed server list. | Seen; harmless. |

## Joining a server

The Doctor matches these lines in the game and server logs, or in a pasted popup. The text in the first column is the game's own wording, including its typos.

| Symptom (game's wording) | Cause | Fix |
|---|---|---|
| `Unable to resolve host name. (127.0.0.1:7350)` or `Joining 127.0.0.1:7350 : 7350` | "address:port" was typed in the address box. The game looks the whole text up as a host name. **Seen** on the owner's PC. | Type only the address (`127.0.0.1`) in the address box and the port (`7350`) in the separate port box. Steward's Copy button copies the address only. |
| `Unable to resolve host name. (<name>)` | Misspelled address, a space in it, or a DNS name that does not exist (yet). | Copy the address again from Realm (no spaces, no port), or use the plain IP. |
| `Invalid port value. Disconnecting from current game` | The port box is empty, zero or above 65535. | Enter the game port Realm shows (7350 for Server I). |
| `Unable to connect to '<ip>'.` | Windows refused the attempt before it reached the server: bad address or no network. | Check the address and that this PC is online. On the server PC use `127.0.0.1`. |
| `Connection timed out.`, `Took too long to connect to server` | Nothing answered on that address and UDP port: the server is down or still loading, the port is wrong, or a firewall or router drops UDP. | Run the Doctor on the server. On the server PC join `127.0.0.1`; on the same home network use the server's LAN address (most routers cannot loop back to their own public IP); from outside, check the forwards in [Going public](#going-public). |
| `Lost connection to the server.` | The server stopped or restarted, or the network dropped packets. | Check the server still runs, join again. |
| `Please run the game from Reign Of Kings.exe` | `ROK.exe` was started directly, without the Easy Anti-Cheat launcher. | Close the game, press Play in Steam (or Join in Realm). Never start `ROK.exe` from the game folder. |
| `EAC has not intiailized yet. Please try again.` | The join started before EAC finished loading. The game does not retry. | Wait a few seconds at the main menu, join again. If it keeps happening, verify the game files in Steam. |
| `User banned by EAC`, `Disconnected By EAC`, `EAC Violation` | EAC reported a problem with this client. | Verify the game files in Steam; close overlays or tools that inject into games. The LSA notice above is not the cause. |
| `You need to be logged into steam in order to join a game`, `SteamAPI_Init() failed`, `Could not authenticate steam` | Steam is not running or not signed in. | Start Steam and sign in (not offline mode), then start the game from Steam. |
| `Failed to authenticate ... k_EBeginAuthSessionResultGameMismatch` (server log) | The server registered with Steam as the dedicated-server tool (381690), so Steam refuses every player's Reign of Kings ticket. **Seen** on the owner's PC. | Update Steward: it starts `ROK.exe` with `SteamAppId=344760` (commit 1a77418). Restart the server. Without Steward: set the server folder's `steam_appid.txt` to `344760`. |
| `Failed to authenticate <name> with Steam. (k_...)` (other codes) | Steam did not accept that player's ticket. | The player restarts Steam and the game. Make sure the server PC's Steam is not in offline mode. |
| `The server is not running the same version as you.` / `... denied connection due to wrong version` | Game and server are different builds. | Let Steam update both (apps 344760 and 381690), then make a fresh Realm test copy of the server. |
| `Server is full.` | All slots are taken. | Join another server ("Join best server"), or wait. Owners: raise Max players (Realm allows up to 120; UNVERIFIED for performance). |
| `Waiting in queue... Position n/m.` | The server lets one player in every `timeBetweenPlayerJoin` seconds (default 10). | Stay on the loading screen. Owners: lower **Secs between joins** for busy evenings. |
| `Server is not fully loaded yet.` | The world is still loading. | Wait for `Server for N players started on port P.` and join again. |
| `You are banned from this server.` / `... denied connection because they were banned` | The Steam account is on the ban list. | Ask the owner. Owners: **Court** > ban list, or `/unban` in the console. |
| `You are not on the server whitelist.` | The server only admits whitelisted accounts. | Ask the owner to add you (**Court** > whitelist). |
| `Incorrect password.` / `... password provided was invalid` | The password does not match (it is case-sensitive). | Re-type it. Owners: `password` in `ServerSettings.cfg`; empty means none. |
| `You have been disconnected for failing to initialize with the ping system.` / `No players are connected to the ping system on port N` | With `enablePingLimit` on, the client must also reach the server on TCP (the ping port, same number as the game port). UDP got through, TCP did not. | Owners: allow and forward **TCP 7350** as well as UDP (Realm's firewall rules include it), or set `enablePingLimit = 'False'`. |
| `... providing an unreliable connection to the ping system` | The TCP ping connection kept dropping. | Wired connection, close downloads. Owners can turn `enablePingLimit` off. |
| `... exceeding the ping limit` | Average ping above `pingLimit`. | Pick a closer server. Owners: raise `pingLimit` or turn the limit off. |
| `Player logged in with the same steam id` | The account is already on the server (an old session or a second PC). | Close the other game, wait a minute, join again. |
| `You were kicked.` / `... was kicked from the server because ...` | An admin, a plugin or the server removed the player. | Read the reason; ask the owner if unclear. |
| `Unable to join a server because the level it is using does not exist` | Client files are incomplete, or the server runs a different map or build. | Verify the game files in Steam. |
| `Could not connect to the server. (code, reason)` | The network layer refused the connection. | Run the Doctor; send the owner the report. |

Good news lines the Doctor also shows: `Server for N players started on port P.` (ready), `Steam game server started. (IP: ...)` (Steam auth on, and the public IP Steam sees), `Attempting join game: <ip>:<port>` (Realm's one-click join reached the game), `Connecting to '<ip>:<port>'.` (the address resolved; a refusal after it shows only as a popup).

## Going public

| Symptom | Cause | Fix |
|---|---|---|
| Doctor: `3 of 4 Realm rules found. Windows Firewall may drop players from other PCs.` | One rule is missing, most likely `Realm s1 admin console BLOCK (TCP)` (TCP 11000-11003). **Seen** on the owner's PC. | **Public > Add firewall rules** again and accept the Windows prompt. Run the Doctor: it must say `4 Realm rules found`. |
| Doctor: `... Realm rules found, but some are disabled` | A rule was turned off in Windows Defender Firewall. | **Add firewall rules** again. |
| Doctor: `TCP 11000 is open on 0.0.0.0: the game's admin console accepts commands from anyone who reaches it.` | The admin console listens on every network card, has no password, and shuts the server down when its last client leaves. The block rule is missing. | Add the Realm firewall rules. **Never forward TCP 11000-11003** in the router. |
| Doctor: `The game socket only listens on 127.0.0.1 although the server is set to Public.` or `bindIP is '127.0.0.1': only this PC can join.` | The server has not been restarted since you chose Public. | **Servers > Restart**, so `bindIP` becomes `0.0.0.0`. |
| Doctor: `bindIP is '0.0.0.0' but the server is set to This PC only.` | The reverse: the setting changed and the server was not restarted. | Restart the server from Steward. |
| Friends outside cannot join; the Doctor is green on this PC | The router does not forward the ports. | Forward **UDP 7350, TCP 7350 and UDP 27015** (Server I; other servers use their own ports) to this PC's LAN address shown on **Public**. Never forward 11000-11003. Test with a friend outside your network: they should see the server Online in the player app. UNVERIFIED: no outside join yet. |
| Doctor: `<ip> is a carrier-grade NAT address: port forwarding cannot work on this connection.` (100.64.x.x) | Your provider shares one public IPv4 between customers. | Ask the provider for a public IPv4 address, or host the server on a rented machine (VPS). |
| Doctor: `Steam sees <ip>, a private address.` | Two routers in a row (for example the provider's modem and your own router). | Forward the ports on both, or put the first one in bridge mode. |
| Doctor: `TCP 7350 did not answer through it from this PC` (amber) | Many routers cannot reach their own public address from inside. | Normal. The real test is a friend outside your network. |
| Doctor: `The server has not logged "Steam game server started" yet, so its public address is unknown.` | Steam registration has not finished, or failed. | Wait a minute; if it never appears, see "The Steam game server could not initialize" above. |
| Doctor: `No answer on 127.0.0.1:27015` | The Steam query port does not answer. | Players can still join by address; only the online/ping display needs it. Give each server its own query port and restart. UNVERIFIED that the game answers A2S at all. |
| Two servers on one PC: the second fails on first start | Until its settings file exists, a new copy uses the game's default ports, which Server I holds. | Stop the other servers during a new server's first start. UNVERIFIED that two real servers run side by side. |

## The player app

| Symptom | Cause | Fix |
|---|---|---|
| No servers in the list | The player app was built before you published, so it has no key and list. | START-HERE step 7: copy `player-config.json` into `launcher\player\`, run `Build-Realm.bat`, send the new `Realm-Setup-1.0.0.exe`. |
| The list stopped updating, or says it expired | `servers.json` is signed with an expiry (30 days by default). | **Publish** again and upload the new `servers.json`. Player apps refuse a list older than the newest they have seen. |
| Join opens the game but it stops at the main menu | UNVERIFIED that Easy Anti-Cheat passes Steam's `-ip`/`-port` on to the game. | Use the card Realm shows: type the address and port in the game's direct-connect boxes. Report it to the owner so the join test (T1 to T3 in [`going-public.md`](going-public.md)) can be updated. |
| `realm://join/...` links do nothing in Discord | Discord does not make custom links clickable. | Post the https download page next to the link; players copy the link into the browser's address bar or press Join in the app. |
