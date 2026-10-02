# Reign of Kings dedicated server: reference notes

Facts for running and configuring the Reign of Kings (RoK) dedicated server locally for the Realm project. Each fact cites its source.

**Status legend**
- **[VERIFIED-PRIMARY]**: read directly from a primary or machine-readable source (source code, or a maintained hosting template that writes these files), at the commit noted.
- **[VERIFIED-SECONDARY]**: taken from search-engine snippets of official or community guides. The research container's network proxy blocked steamcommunity.com, fandom.com, web.archive.org, umod.org, oxidemod.org, pingperfect.com and g-portal.com, so these pages could not be opened in full. Cross-check them on the owner's PC.
- **[UNVERIFIED]**: plausible but not confirmed. Do not rely on it until it has been checked against the real install.

Primary sources that were cloned and read:
- `OxideMod/Oxide.ReignOfKings` at commit `f690b9c2afa043a96f2e67cd3e1ba88260c7dbb6` (2024-04-28). Latest tag is `2.0.3867`. https://github.com/OxideMod/Oxide.ReignOfKings
- `CubeCoders/AMPTemplates` at commit `b1241980bfde434a0e9b9666aac9bf0c93469793`. This is a commercial panel's RoK template, and it writes `ServerSettings.cfg` and `ConsoleSettings.cfg`. https://github.com/CubeCoders/AMPTemplates (files `reign-of-kings.kvp`, `reign-of-kingsconfig.json`, `reign-of-kingsmetaconfig.json`, `reign-of-kingsports.json`, `reign-of-kingsserversettings.cfg`, `reign-of-kingsupdates.json`)

Official guides that are the authoritative references. They could not be fetched here, so read them on the owner's PC:
- "Starting Community Servers - Official Guide" (Code Hatch): https://steamcommunity.com/sharedfiles/filedetails/?id=406318745
- "Server-Side Modding": https://steamcommunity.com/sharedfiles/filedetails/?id=575826710
- "RCon Support": https://steamcommunity.com/sharedfiles/filedetails/?id=527128084
- "CodeHatch Remote Console (Client)": https://steamcommunity.com/sharedfiles/filedetails/?id=539933596
- "ROK Server Update Changelog": https://steamcommunity.com/sharedfiles/filedetails/?id=435654886
- Fandom wiki, Dedicated Server Setup: https://reignofkings.fandom.com/wiki/Dedicated_Server_Setup

---

## 1. Executables and command line

| Fact | Status | Source |
|---|---|---|
| The dedicated server is Steam app **381690**, downloadable with an **anonymous** SteamCMD login. AMP runs `SteamCMD 381690` with the game app id `344760` and `SteamUpdateAnonymousLogin=True`. Oxide's build also pulls 381690 with `SteamLogin=anonymous`. | VERIFIED-PRIMARY | AMPTemplates `reign-of-kingsupdates.json`, `reign-of-kings.kvp`; Oxide `src/Oxide.ReignOfKings.csproj` |
| The install folder holds **two** executables. `ROK.exe` is the Unity game/server process. `Server.exe` is a console wrapper, added in Alpha 15, that "starts and monitors its server and restarts it if it freezes", can auto-update, and generates `Configuration/ConsoleSettings.cfg`. Oxide's csproj lists `GameExe = ROK.exe;Server.exe`. | PRIMARY (both exe names); SECONDARY (wrapper behaviour) | Oxide csproj; search snippets of guides 406318745 and 435654886 |
| `Server.exe` can download the server files. Putting a `steam_appid.txt` containing `381690` beside it skips the app-id prompt. | VERIFIED-SECONDARY | snippet of guide 435654886 / steamsolo mirror |
| The managed assemblies are in `ROK_Data\Managed\`. This is where Oxide's patched `Assembly-CSharp.dll` goes. | VERIFIED-PRIMARY | Oxide csproj (`ManagedDir = ROK_Data/Managed`) |
| Headless launch, from Oxide's own example batch file: `ROK.exe -batchmode -nographics -silentcrash`, run in a restart loop. | VERIFIED-PRIMARY | Oxide `resources/_start-example.bat` |
| AMP launches `ROK.exe -batchmode -nographics -silent-crashes`. Its spelling of the crash flag differs from Oxide's. `-silent-crashes` is the standard Unity player flag. Both forms appear in maintained launchers, so treat the flag as optional and harmless. | VERIFIED-PRIMARY (both spellings exist) | AMP `reign-of-kings.kvp` (`App.CommandLineArgs`) |
| AMP sets environment variable `SteamAppId=344760` for the server process. | VERIFIED-PRIMARY | AMP `reign-of-kings.kvp` |
| **No config-path command-line argument was found.** Configuration is read from the `Configuration\` folder next to the exe. AMP passes no config path, and its `App.CommandLineParameterFormat=-{0}={1}` is generic with no RoK parameters defined. | UNVERIFIED (absence) | AMP `reign-of-kings.kvp`, `reign-of-kingsmetaconfig.json` |
| The server is ready when the console prints a line matching `^Initialize engine version:.*$`. A join logs `Authentication verified for <name> (<steamid>).` and a leave logs `<name> has disconnected.` | VERIFIED-PRIMARY | AMP `reign-of-kings.kvp` (Console.* regexes) |
| AMP's graceful stop string is `quit`. | VERIFIED-PRIMARY (AMP config); exact in-game behaviour UNVERIFIED | AMP `reign-of-kings.kvp` (`App.ExitString`) |

Recommended local launch on the owner's PC. This is a reproduction of Oxide's example, not new syntax:

```bat
cd /d "G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server"
ROK.exe -batchmode -nographics -silentcrash
```

Running `Server.exe` instead gives the wrapper console, auto-restart and RCON settings. See the note in section 4 about Oxide and the console window.

## 2. Ports and protocols

| Port | Default | Protocol | Config key | Status | Source |
|---|---|---|---|---|---|
| Game | **7350** | UDP (guides say forward UDP; AMP opens "Both") | `portNumber` (ServerSettings.cfg) | VERIFIED-PRIMARY | AMP `reign-of-kingsports.json`, `reign-of-kingsserversettings.cfg`; Code Hatch guide snippet ("portNumber ... '7350' ... UDP") |
| Ping | **7350** (same as game by default) | UDP/TCP ("Both" in AMP) | `pingPort` (ServerSettings.cfg) | VERIFIED-PRIMARY | AMP `reign-of-kingsserversettings.cfg`, `reign-of-kingsconfig.json` |
| Steam auth / server query | **27015** | UDP/TCP ("Both") | `steamAuthPort` (ServerSettings.cfg) | VERIFIED-PRIMARY | AMP files above |
| RCON | **27015** by default (AMP maps it to the same "AuthPort") | TCP. AMP uses `AdminMethod=SourceRCON`, i.e. Source RCON protocol | `rConPort` (ConsoleSettings.cfg) | PRIMARY (keys and default); SECONDARY ("default rConPort 27015 ... change it if RCon enabled") | AMP `reign-of-kingsmetaconfig.json`, `reign-of-kingsports.json`, `reign-of-kings.kvp`; guide 527128084 snippet |

Note: `rConPort` and `steamAuthPort` both default to 27015. A guide snippet says "If you have RCon enabled you will need to change the rConPort". **For local testing, set `rConPort` to a different value, e.g. 27016.** AMP shares the two on purpose, but the Code Hatch guidance is to separate them.

The query protocol for a server browser or overlay is not documented beyond "Steam auth". Whether A2S queries work on 27015 is UNVERIFIED.

Local-only testing needs **no firewall or router changes**. Connect to `127.0.0.1` / `localhost` on 7350.

## 3. Connecting a client

| Fact | Status | Source |
|---|---|---|
| Connect by direct IP from the in-game menu: "direct connect to the ip of localhost and the port of 7350 (default)". Community posts describe typing the IP into the **"Local Host"** field of the in-game server UI. | VERIFIED-SECONDARY | Code Hatch guide 406318745 snippet; discussion https://steamcommunity.com/app/344760/discussions/4/611701999536176219/ |
| Past servers are recorded in `%USERPROFILE%\AppData\LocalLow\CodeHatch\ReignOfKings\profiles\<id>\` in a "lobby history" file. | VERIFIED-SECONDARY | https://steamcommunity.com/app/344760/discussions/4/611701999536220409/ |
| **No in-game console `connect <ip>` command was found** in any source. | UNVERIFIED (absence) | none |
| **No client launch argument that auto-connects (e.g. `+connect ip:port`) was confirmed for RoK.** A search surfaced a generic "put IP:port in Steam launch options" guide, but it is id 266770741 (2014, before RoK) and is probably for another game. Do not build the launcher around auto-connect until it has been tested. | UNVERIFIED | https://steamcommunity.com/sharedfiles/filedetails/?id=266770741 (likely not RoK) |
| `steam://rungameid/344760` is the generic Steam URL for launching app 344760. It is standard Steam client behaviour, not RoK-specific, and passes no connect info. Steam's `steam://connect/<ip>:<port>` works only for games that support it, which is unknown for RoK. | Generic Steam behaviour; RoK support for `steam://connect` UNVERIFIED | Valve Steam browser protocol docs: https://developer.valvesoftware.com/wiki/Steam_browser_protocol |

Launcher implication: the safe design is `steam://rungameid/344760`, then show the player the IP:port to type into the in-game direct-connect / "Local Host" field.

## 4. Configuration folder (`<server>\Configuration\`)

Files are created on first run. The format is one `key = 'value'` per line, with `#` comments and single-quoted values. The AMP regex is `^(?<key>.+?) = '(?<value>.*?)'.*$` [VERIFIED-PRIMARY, AMP `reign-of-kingsmetaconfig.json`].

Files reported in the folder: `ServerSettings.cfg`, `Permissions.cfg`, `Users.cfg`, `DeathMessages.cfg`, `Whitelist.cfg`, plus `ConsoleSettings.cfg`, which appears only when the server is run through `Server.exe` [VERIFIED-SECONDARY, snippets of guides 411957104, 406318745, 551218314; Fandom Permissions page].

### 4.1 ServerSettings.cfg [VERIFIED-PRIMARY: AMP template, every key below written verbatim]

```
# -- Server --
isPrivate = 'False'          # hide from lobby list
serverName = '...'
greeting = ''                # %user% / %server% placeholders; empty = off
maxPlayers = '30'
bindIP = '0.0.0.0'
portNumber = '7350'
password = ''
restartTime = '0'            # 0 = disabled
enableCommands = 'True'
flyDetection = 'False'
steamAuthPort = '27015'
timeBetweenPlayerJoin = '10'
targetFrameRate = '60'

# -- Ping Limit --
enablePingLimit = 'False'
pingPort = '7350'
pingLimit = '200'
pingGraphLength = '60'

# -- Automatic Report Ban --
autoReportBanEnabled = 'False'
reportThresholdSpam = '3'
daysBannedForSpam = '1'
reportThresholdExploit = '5'
daysBannedForExploit = '7'

# -- World --
saveLocation = 'Saves/'
worldSlot = '-1'             # -1 = create a new world
allowSaving = 'True'
levelName = 'CrownLand'

# -- Game --
decay = 'False'
blockDecay = '-1'
prefabDecay = '-1'
crestSiege = 'False'
blockCollapsing = 'True'
```
The meanings in the comments come from the AMP `reign-of-kingsconfig.json` descriptions.

The AMP template is hand-written and may not hold every key the game generates. A search snippet also mentions a `gameMode` key [VERIFIED-SECONDARY, unconfirmed]. **Always diff against the file the server writes on first run.**

**Autosave:** there is **no autosave-interval key** in any source found. `allowSaving` turns saving on and off. A key name for the autosave interval is UNVERIFIED. Do not invent one.

### 4.2 ConsoleSettings.cfg (created by Server.exe)

| Key | Example / default | Status | Source |
|---|---|---|---|
| `enableRCon` | `'False'` by default, set to `'True'` | PRIMARY + SECONDARY | AMP metaconfig; guide 527128084 snippet |
| `rConPassword` | `''` by default. Always set it. | PRIMARY + SECONDARY | same |
| `rConPort` | `'27015'` by default | PRIMARY + SECONDARY | same |
| `autoUpdate` | `'False'` | VERIFIED-SECONDARY | guide 406318745 snippet |
| `autoRestartTime` | `'0'` (seconds; e.g. `'18000'` = 5 h) | VERIFIED-SECONDARY | guide 406318745 snippet |

Other sections exist (crash protection, batch scripts on restart), but their key names were not found and are UNVERIFIED.

RCON clients: Code Hatch published a remote console client (guide 539933596). AMP talks to it as Source RCON.

### 4.3 Whitelist.cfg

Contains whitelisted players and an `enabled = 'True'|'False'` switch. When it is enabled, players who are not listed cannot connect. In-game, `/whitelist` adds players [VERIFIED-SECONDARY, guide 406318745 snippet, discussion https://steamcommunity.com/app/344760/discussions/4/611701999532923951]. The exact line format for player entries is UNVERIFIED.

### 4.4 Permissions.cfg

Auto-generated on first start if missing. Ranks and permissions are defined here [VERIFIED-SECONDARY, https://steamcommunity.com/sharedfiles/filedetails/?id=551218314, https://reignofkings.fandom.com/wiki/Permissions].

## 5. Built-in server-side modding (`<server>\Mods\`)

| Fact | Status | Source |
|---|---|---|
| Introduced with the 1.0 update as a "server-side modding framework" for operators. | VERIFIED-SECONDARY | Changelog https://reignofkings.fandom.com/wiki/Changelog (snippet) |
| You must **start the server at least twice** before the `Mods/` folder is created. | VERIFIED-SECONDARY | guide 575826710 snippet |
| Each category has a **`<Name>.defaults.cfg`** (reference values) and a **`<Name>.cfg`** (overrides). Copy **the entire line** of a value from the defaults file into the non-default file, edit it, then restart. Example given: `Players.cfg` / `Players.defaults.cfg`. | VERIFIED-SECONDARY | guide 575826710 snippet |
| Values are server-authoritative: "the player will then see the changes when they play the game". No client files are modified. | VERIFIED-SECONDARY | guide 575826710 snippet |
| Categories described in the guide: **Players** (head/torso/leg max health, inventory slots, movement speed/stability, fall damage), **Environment / "Weather & Day-Night"** (colour of fog, sun and moon, speed of the day-night cycle, how often it is cloudy/raining/clear, atmosphere colours and fog density), **block tinting**, **Crafting** (amount crafted, craft time, craftable on/off, required resources), **Armor** (damage reduction, speed penalty, recoil reduction), **Blocks** (damageable by weapons, salvageable, salvage return %, salvage damage), **loot drops**. | VERIFIED-SECONDARY (category descriptions only) | guide 575826710 snippets |

### Exact key names: NOT FOUND

The research turned up **no** verifiable key names for fog, sun/moon colour, day length, weather frequency, block tint, crafting, gather or building. It also could not confirm any file name other than `Players.cfg` / `Players.defaults.cfg`. Names such as `Environment.cfg` or `Crafting.cfg` are **guesses and are UNVERIFIED**. Get the real names from the generated `*.defaults.cfg` files on the owner's PC:

```powershell
$srv = "G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server"
Get-ChildItem "$srv\Mods" -Recurse -Filter *.defaults.cfg | Select-Object FullName
Select-String -Path "$srv\Mods\*.defaults.cfg" -Pattern 'fog|sun|moon|day|weather|rain|cloud|tint|craft|gather|resource|salvage' |
  ForEach-Object { "$($_.Filename): $($_.Line.Trim())" } | Out-File "$srv\mods-keys.txt"
```
Copy `mods-keys.txt` back into the repo, e.g. `docs/mods-keys.txt`, before writing any Realm presets.

## 6. Oxide (legacy 2.0.x) for RoK

| Fact | Status | Source |
|---|---|---|
| Repo `OxideMod/Oxide.ReignOfKings` is MIT licensed, targets **.NET 3.5** (`net35`), and builds against server app **381690**. Latest tag is **2.0.3867**. Releases ship `Oxide.ReignOfKings.zip` as a GitHub release asset. | VERIFIED-PRIMARY | csproj, `.github/workflows/release.yaml`, `git ls-remote --tags`; AMP `reign-of-kingsupdates.json` (downloads `Oxide.ReignOfKings.zip` from GitHub releases) |
| **Install:** stop the server, then extract the zip **into the server root** (the folder with `ROK.exe`) and overwrite. The zip holds `ROK_Data\Managed\Oxide*.dll` plus a patched `Assembly-CSharp.dll`. AMP unzips it into the base dir with overwrite. | PRIMARY (AMP unzip to base dir; patched Assembly-CSharp in bundle) + SECONDARY (umod/oxidemod how-tos) | AMP updates.json; Oxide csproj `PatchedFiles`; https://oxidemod.org/threads/install-steps-for-oxide-on-dedicated.15517/ |
| **Uninstall:** delete `Oxide.Compiler.exe`, `ROK_Data\Managed\Oxide*` and the `oxide` folder (AMP), **and** restore the original `Assembly-CSharp.dll` (community answer), or re-validate the server through Steam. | PRIMARY (AMP) + SECONDARY | AMP updates.json; https://oxidemod.org/threads/uninstalling-oxide.8230/ |
| Plugins go in **`Saves\oxide\plugins`**. Oxide creates the folders after its first successful run. | VERIFIED-SECONDARY | oxidemod.org / umod community snippets |
| Plugins may only reference assemblies `Assembly-CSharp, mscorlib, Oxide.Core, System, System.Core, UnityEngine` and namespaces `CodeHatch, Steamworks, System.Collections, System.Security.Cryptography, System.Text, UnityEngine` (sandbox whitelist). | VERIFIED-PRIMARY | `src/ReignOfKingsExtension.cs` |
| Useful server APIs used by Oxide: `DedicatedServerBypass.Settings.ServerName`, `Server.PlayerLimit`, `Server.PlayerCount`, `CodeHatch.Engine.Core.Gaming.Game.ServerData.Port`, `Game.Save()`, `GameClock.Instance.TimeOfDay/DaySpeed`, `TimeSetEvent`, `Weather.Instance.CurrentWeather`, `Server.Ban/Unban/BroadcastMessage`. | VERIFIED-PRIMARY | `src/Libraries/Covalence/ReignOfKingsServer.cs`, `src/ReignOfKingsExtension.cs` |
| Oxide's `ReignOfKingsServer.Address` calls `http://api.ipify.org` to resolve the public IP. Note this for "local-only" testing. | VERIFIED-PRIMARY | `ReignOfKingsServer.cs` |
| Compatibility: Oxide replaces `Assembly-CSharp.dll` **on the server only**. Steam updates to 381690 overwrite it, so re-extract Oxide after every server update. AMP re-runs its Oxide stage after SteamCMD for this reason. The Oxide console window may not show when the wrong exe is used (forum thread title "Server console doesn't show (using wrong exe)"). Which exe it means is UNVERIFIED. Test both `ROK.exe` and `Server.exe`. | PRIMARY (update ordering) + SECONDARY | AMP updates.json; https://oxidemod.org/threads/server-console-doesnt-show-using-wrong-exe.9440/ |
| Client files are never touched. Oxide and the Mods/ system are server-side, which is consistent with the no-client-modification / EAC constraint. Whether EAC runs on community servers is not covered in these sources. | UNVERIFIED (EAC) | none |

## 7. Open items to verify on the owner's PC

1. Which exe the Steam install actually contains (`ROK.exe`, `Server.exe`, or both), and which one Oxide's console attaches to.
2. The full list of keys `ServerSettings.cfg` writes on first run, diffed against section 4.1. Look especially for an autosave interval and `gameMode`.
3. The real `Mods\*.defaults.cfg` file names and key names (script in section 5).
4. Whether any client launch option or `steam://connect/127.0.0.1:7350` auto-connects.
5. Whether `rConPort` can share 27015 with `steamAuthPort`. Recommendation: use 27016.
6. The `Whitelist.cfg` entry format.
