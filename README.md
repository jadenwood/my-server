# Realm

Realm is a community revival of **Reign of Kings** (Code Hatch, 2015; Steam app 344760, dedicated server app 381690). It is built server-first and has its own original lore. Players form **houses** and swear allegiance to one another. One **crown** is contested. The king has a limited set of decrees and a council. **Rebellions** have to be declared in advance and are fought in scheduled windows. **Ransom** has an upper limit. A public **Realm Chronicle** records what happens and feeds livestream overlays. A Realm-branded **launcher** sends players to their own Steam copy of the game.

Everything here runs on the server side or next to it. The game client is never modified.

## Legal boundary

- **Players use their own Steam copy of Reign of Kings.** This repo does not contain, patch, repackage or redistribute any game client or server files. The launcher only opens `steam://rungameid/344760` and shows the address to type in. Do not add game binaries or assets to this repo.
- Gameplay changes come only from **server-side** mechanisms:
  - Oxide 2.0.3867 plugins. Oxide is MIT licensed, from `github.com/OxideMod/Oxide.ReignOfKings`, and you download it yourself.
  - The game's built-in `Mods\*.cfg` override system.
- Oxide replaces `Assembly-CSharp.dll` **in the server's test copy only** (`server/Install-Oxide.ps1`). Clients are never touched. This respects the EULA and Easy Anti-Cheat.
- The lore is original. Do not use Westeros or other franchise names in houses, sigils, decrees or news.

## Repository layout

| Path | What it is |
|---|---|
| `plugins/` | Oxide C# plugins (namespace `Oxide.Plugins`, `[Info(name, "Realm", "0.1.0")]`). <br>• `RealmHouses.cs`: houses, oaths, treaties. Commands `/house`, `/swear`, `/renounce`, `/treaty`. <br>• `CrownAndConsequences.cs`: crown tracking, decrees, council, claims and rebellion windows, tax cap, bounded ransom. Commands `/crown`, `/decree`, `/council`, `/claim`, `/ransom`. <br>• `RealmChronicle.cs`: the event log and the realm snapshot. Command `/chronicle`. |
| `chronicle/` | A Node 22 service with no dependencies, bound to `127.0.0.1:8787`. It serves `GET /api/state`, `GET /api/events?since=<id>&limit=<n>`, `/healthz`, the OBS overlay `/overlay` and the public page `/realm`. It only reads data. See `chronicle/README.md`. |
| `launcher/` | **Realm**, the Electron desktop client (Windows installer `Realm-Setup-<version>.exe` and a portable exe). PLAY opens Steam; for the owner it also runs the setup wizard, starts and stops the test server with a live console, backs up and restores the world, hosts the Chronicle overlay in-process and edits `ServerSettings.cfg`. It re-implements the `server/*.ps1` safety rules in Node. See `launcher/README.md`. |
| `server/` | Windows PowerShell 5.1 scripts that work only on a **test copy** of the dedicated server: `New-TestServer`, `Start-LocalServer`, `Backup-Saves`, `Restore-Saves`, `Install-Oxide`, `Deploy-Plugins`, `Export-ModKeys`. |
| `mods/` | Notes on the built-in Mods system and a **template** for the "grim but readable" atmosphere preset. It holds placeholders only, because no Mods key names are verified yet. |
| `docs/` | `oxide-rok-api.md` (the plugin API reference, every claim tagged), `server-reference.md` (server files, ports, config keys), `smoke-test.md` (the step-by-step test plan), and screenshots in `img/`. |

## How the parts fit together

```
RealmHouses ─┐  Call("Log", type, title, detail, actors[])
Crown...     ├──────────────► RealmChronicle ──► oxide/data/RealmChronicle.json  (event array)
             │  GetHouseSummaries / GetKing*      └► oxide/data/RealmState.json      (snapshot)
             └────────────────────────────────────────────┐
                                     chronicle/server.js reads both files (read-only)
                                     ├─ /api/state, /api/events  ◄── launcher (main process)
                                     └─ /overlay (OBS), /realm (public page)
```

The data files follow this contract:

- **`RealmChronicle.json`** is a JSON array of events, each `{id, ts, type, title, detail, actors}`:
  - `id` is an increasing integer.
  - `ts` is an ISO-8601 UTC timestamp.
  - `type` is one of `coronation`, `abdication`, `claim_declared`, `rebellion_started`, `rebellion_ended`, `house_founded`, `oath_sworn`, `oath_broken`, `treaty_signed`, `treaty_broken`, `decree`, `ransom_set`, `ransom_paid`, `released`.
  - `actors` holds public player names only, never locations or inventory.
- **`RealmState.json`** is `{king, house, since, houses:[{name, sigil, liege, members}], online, maxPlayers, updated}`.
- **`/api/state`** returns that state plus `stale: bool`.

## Easiest way: run Realm-Setup.exe

1. Make sure Steam has **Reign Of Kings Dedicated Server** installed (Library > Tools).
2. Download **`Realm-Setup.exe`** (built from `launcher/` with `npm run dist:win`; the file is named `Realm-Setup-<version>.exe`) and run it. It installs for your Windows user only and adds a desktop and Start-menu shortcut.
3. Open **Realm**. On first run its setup wizard does the rest, with a progress bar for each step:
   finds the Steam server, copies it to the test folder (default `G:\RealmTest\server`), starts it once so the game writes its settings, downloads Oxide 2.0.3867 and checks its SHA-256, installs it with a backup of every replaced file, and deploys the Realm plugins. Finished steps are skipped; a failed step shows a plain-English message with **Copy details**.
4. In Realm: **Server** starts and stops the test server and shows its console, **Play** opens your own copy through Steam (direct connect to `127.0.0.1` port `7350`), **Overlay** gives the OBS address, and **Settings** changes the server name, player limit and port.

You never need a command window, and the firewall, the router and the Steam copy are never changed. Node.js is not needed: the Chronicle overlay runs inside the app.

**Fallback:** `Realm.bat` still works. Download this repo (green **Code** button > **Download ZIP**), unzip it on **G:**, double-click `Realm.bat` and pick **1 Set up everything**, then **2 Play** (**3** updates plugins, **4** backs up the world, **5** undoes Oxide). If something fails, the window prints `PROBLEM: ...`; nothing else is changed. The manual steps below do the same thing one script at a time.

## Quick start on the owner's PC (smoke-test order)

The full checklist with pass/fail tables is in **`docs/smoke-test.md`**. Do the stages in this order, and only move on when the current stage passes.

All scripts default to these locations:
- The Steam server is `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server`. It is **read only**, and only `New-TestServer.ps1` reads it.
- The test copy is `G:\RealmTest\server`.
- Backups go to `G:\RealmTest\backups`.

**Do not change the firewall or router.** Testing is local only, on `127.0.0.1`. If Windows asks to allow the server through the firewall, choose Cancel.

Open **Windows PowerShell** in the repo's `server\` folder, then run `Set-ExecutionPolicy -Scope Process Bypass`. If Windows blocks the scripts because they came from the internet, also run `Get-ChildItem *.ps1 | Unblock-File`. Run every script with `-WhatIf` first.

**A. Vanilla**

1. `.\New-TestServer.ps1`: copies the Steam server folder to the test copy with robocopy `/E`, and adds a `.realm-test-copy` marker. Every other script refuses to work on a folder that has no marker, or on any folder on C:, in a `steamapps` folder or in a `Steam` folder.
2. `.\Start-LocalServer.ps1`: first run. Wait for the `Initialize engine version:` line, then type `quit`.
3. `.\Start-LocalServer.ps1` again: this run sets `bindIP='127.0.0.1'`, `isPrivate='True'`, `rConPort='27016'` and `enableRCon='False'`, and backs up each file first.
4. Start Reign of Kings from Steam and use direct connect / "Local Host" with `127.0.0.1`, port `7350`. Build something, restart the server, and check that it is still there.
5. `.\Backup-Saves.ps1`. Optional: apply the atmosphere preset with `mods\presets\grim-but-readable\Apply-Preset.ps1` (see `mods/README.md`).

**B. Oxide**

6. Download `Oxide.ReignOfKings.zip` from the 2.0.3867 GitHub release.
7. `.\Install-Oxide.ps1 -ZipPath <zip>`. It checks the SHA-256 `6c35c623…c6c8`, backs up every file it overwrites, and can be undone with `-Rollback`.
8. Start the server and run `oxide.version` in its console.

**C. Plugins and Chronicle**

9. `.\Deploy-Plugins.ps1`: copies `plugins\*.cs` into the test copy's `oxide\plugins`, where Oxide hot-loads them. Fix any compile errors in the repo and deploy again.
10. Grant yourself admin with `oxide.grant user <name> realmhouses.admin` and `oxide.grant user <name> crownandconsequences.admin`.
11. In chat, try `/chronicle`, `/house found "Ashveil" Grey Heron`, `/house list`, `/crown`, `/claim list` and `/ransom list`.
12. Start the Chronicle:
    ```powershell
    cd ..\chronicle
    node server.js --data "G:\RealmTest\server\oxide\data"
    ```
    If Oxide created its folder under `Saves\`, use `...\Saves\oxide\data` instead. Then open `http://127.0.0.1:8787/realm`, `/overlay` and `/healthz`.
13. Launcher:
    ```powershell
    cd ..\launcher
    npm install
    npm start
    ```
    Build the installer and the portable exe with `npm run dist:win` (output in `launcher\dist\`).

To try the Chronicle without a game server, run this from `chronicle/`, then open `http://127.0.0.1:8787/overlay?replay=1&bg=1`:

```
npm run sample
```

## Verified vs untested

| Area | Status |
|---|---|
| Oxide hook names, signatures, event members and game APIs | **Verified** from Oxide source, the patch manifest and the metadata of the shipped 2.0.3867 DLLs (`docs/oxide-rok-api.md`; each claim is tagged). |
| Oxide zip | **Verified**: SHA-256 and entry list checked against the real 2.0.3867 release. |
| Config keys in `ServerSettings.cfg` and `ConsoleSettings.cfg` used by the scripts | **Verified (primary)** from a maintained hosting template. The scripts only edit keys that already exist and never add new ones. |
| Ports (7350 game, 27015 Steam auth) and the direct-connect method | Ports are verified (primary). The connect UI wording is **secondary**. |
| Chronicle service | **Tested here**: `npm test` (6/6 pass), every endpoint checked with curl, and the pages rendered in Chromium. |
| Realm client (`launcher/`) | **Tested here** on Linux only: unit tests for the path, cfg, zip, vdf and hash rules, plus an automated walk through every screen under Electron with an imitation `Server.exe` (see `launcher/README.md`). The Windows installer and portable exe were **built** here but **never run on Windows**. Real `Server.exe` behaviour with piped input/output (console text, `quit` on stdin), the registry lookup and the Windows process check are **unverified**. |
| Plugins (`plugins/*.cs`) | **Compile-checked, never run in game.** All four plugins compile with 0 errors at C# 3 against the shipped Oxide 2.0.3867 and patched game DLLs (`tools/plugin-compile-check/check.sh`). Behaviour on a live server is untested; spots that rely on unverified behaviour are marked `// UNVERIFIED:`. |
| PowerShell scripts | **Never run or parsed**, because there is no PowerShell here. Always use `-WhatIf` first. |
| Mods atmosphere preset | **Key names read from the server DLL** (`docs/mods-keys-from-dll.md`). Which Mods file holds them, and whether a headless server applies fog, is untested; `Apply-Preset.ps1` checks against the server's own `*.defaults.cfg`. |
| Which exe to run (`Server.exe` or `ROK.exe`), the `ROK_Data`/`Server_Data` folder, the Oxide data path (`oxide\` or `Saves\oxide\`), `Assembly-CSharp_Original.dll`, and whether `bindIP=127.0.0.1` still lets clients authenticate | **Unverified.** The smoke test resolves each of these. |
| Client auto-connect (`steam://connect`, launch arguments) | **Unverified.** It is deliberately not used. |

## Known limitations

- Chronicle event ids continue from the highest id still in the file. If `RealmChronicle.json` is deleted, ids restart at 1. Overlays and the launcher that are already open then need a reload to see new events.
- The status pill in the launcher shows whether the Chronicle is fresh. It does not show whether the game port is reachable.
- Ransom payment is honour-based: the captor confirms it. Contracts use real item escrow (untested in game; `ItemEscrow: false` switches to honour mode). Harvest Tithe was replaced by Royal Stores because gathering happens client-side and no server hook can boost it.
- The default rebellion windows are Wednesday 19:00 and Saturday 19:00 **UTC**. Change them in `oxide\config\CrownAndConsequences.json` to suit your time zone.
