# Realm

**New here? Read [`START-HERE.md`](START-HERE.md).** It takes the owner from a fresh clone to a public server and a friend joining in about 10 minutes: double-click `Build-Realm.bat`, install Realm Steward, let the setup wizard run, Go Public, publish the server list and send `Realm-Setup` to your friends. When something goes wrong: [`docs/troubleshooting.md`](docs/troubleshooting.md).

Realm is a community revival of **Reign of Kings** (Code Hatch, 2015; Steam app 344760, dedicated server app 381690). It is built server-first and has its own original lore. Players form **houses** and swear allegiance to one another. One **crown** is contested. The king has a limited set of decrees and a council. **Rebellions** have to be declared in advance and are fought in scheduled windows. **Ransom** has an upper limit. A public **Realm Chronicle** records what happens and feeds livestream overlays. Two Realm desktop apps come from one codebase. **Realm Steward** lets the owner run up to four servers. **Realm**, the player app, sends players to their own Steam copy of the game.

Everything here runs on the server side or next to it. The game client is never modified.

## Legal boundary

- **Players use their own Steam copy of Reign of Kings.** This repo does not contain, patch, repackage or redistribute any game client or server files. The apps only hand allow-listed `steam://` URLs to Steam (`rungameid/344760`, `install/344760`, and `run/344760//-ip <host> -port <port>/` for joining directly) and show the address to type in. Do not add game binaries or assets to this repo.
- Gameplay changes come only from **server-side** mechanisms:
  - Oxide 2.0.3867 plugins. Oxide is MIT licensed, from `github.com/OxideMod/Oxide.ReignOfKings`, and you download it yourself.
  - The game's built-in `Mods\*.cfg` override system.
- Oxide replaces `Assembly-CSharp.dll` **in the server's test copy only** (`server/Install-Oxide.ps1`). Clients are never touched. This is meant to respect the EULA and Easy Anti-Cheat; the EULA text itself has not been read yet, so see [`docs/legal/eula-compliance-checklist.md`](docs/legal/eula-compliance-checklist.md) for what is still open.
- The lore is original. Do not use Westeros or other franchise names in houses, sigils, decrees or news.

## What Realm does now

Everything below is built and tested here (compile checks, mock tests, exploit suites, app tests). **None of the plugin features has been seen working in game yet**: the owner's server has proven join and plugin load only. Each feature's in-game tests are in the play-test checklist of [`docs/ROADMAP.md`](docs/ROADMAP.md) section 5. For players, [`docs/community/how-to-play.md`](docs/community/how-to-play.md) explains every system in plain words; every command is in [`docs/realm-commands.md`](docs/realm-commands.md); how the parts connect is in [`docs/realm-systems.md`](docs/realm-systems.md).

**In the game** (26 server-side Oxide plugins in `plugins/`; guides in `plugins/docs/`):

| Feature | Where | What it is for |
|---|---|---|
| **Houses, oaths and treaties** | `RealmHouses` | Factions with leaders and officers, oaths of fealty, timed treaties, and public oathbreaker and treaty-breaker marks. `/house`, `/swear`, `/renounce`, `/treaty` |
| **The crown** | `CrownAndConsequences` | The Old Throne, decrees, the council, claims and scheduled rebellion windows, a tax cap and bounded ransom. `/crown`, `/decree`, `/council`, `/claim`, `/ransom` |
| **Law and the court** | `RealmLaws` | Laws and zones, a public crime ledger, accusations, jury trials, trial by combat, fines, outlawry, exile and pardons. `/law`, `/court` |
| **Bloodlines, renown and titles** | `RealmDynasties`, `RealmRenown` | Heirs, succession and blood claims; renown and infamy from deeds, and titles worn in chat. `/dynasty`, `/renown`, `/titles` |
| **Coin** | `RealmTreasury` | Marks (a ledger currency), purses, a market with escrow, house vaults, the crown's treasury, mint and tithe, with a public audit. `/purse`, `/market`, `/vault`, `/treasury` |
| **Contracts** | `RealmContracts` | Bounties on public enemies, deliveries and mercenary work with real item escrow. `/contract` |
| **Seasons and the Hall of Kings** | `RealmSeasons` | Numbered seasons with house standings; every reign goes into a Hall of Kings that survives wipes. `/season` |
| **Realm events** | `RealmEvents` | Crown Night, the Royal Tournament, the King's Hunt and the Truce of the Realm, with countdown heralds and prizes never paid twice. `/events`, `/tourney`, `/hunt`, `/truce` |
| **Holdings and the War Hours** | `RealmDominion` | Seven named places houses take by holding the field in the War Hours; garrisons, a daily payday into house vaults, season points and a holdings board. `/dominion` |
| **Tasks, story and achievements** | `RealmQuests` | Daily and weekly tasks, the Season 1 story *The Hollow Crown*, 68 achievements and weekly house goals. `/quest`, `/achievements` |
| **Duels and the tavern** | `RealmArena` | Duels to the first fall with no death and no loot, stakes held by the treasury, a ladder and a weekly Champion of the Ring, team duels, brackets, trial by combat; Hearth Dice and Twenty-One. `/duel`, `/arena`, `/dice`, `/cards` |
| **Roads, waystones and kits** | `RealmTravel` | Waystones found on foot, travel between them by the game's own server-side teleport for a toll, a home in your crest zone, directions in chat, and starter, house and season kits. `/travel`, `/home`, `/road`, `/kit` |
| **Crafts and the guilds** | `RealmCrafts` | Eight professions with ranks and perks from what players really gather and make, a weekly Master Crafter, house workshops and a commission board. `/craft` |
| **The living world** | `RealmWorld` | Treasure hunts, the Blood Moon, the Merchant Caravan, Wandering Legends, the Harvest Fair and Midwinter, and the weekly Census. `/world`, `/treasure`, `/caravan`, `/festival` |
| **House colours and the realm's vote** | `RealmHeraldry` | Each house's colours on its game guild's banner, crest, armour and name tags; council elections each season and the crown's referendums, one vote per account. `/heraldry`, `/ballot`, `/vote` |
| **Letters and rumours** | `RealmRavens` | Letters between players and houses, interception by spies, moderated anonymous rumours. `/raven`, `/rumour` |
| **The Ironbreaker** | `RealmLegendary` | Exactly one legendary blade, won at the Royal Tournament and taken by the bearer's slayer. Staff `/ironbreaker` |
| **The Herald and the `/realm` hub** | `RealmHerald` | Welcome and first steps, every command by subject, tips, the message of the day, and the game's own popup windows with a chat fallback. One chat style across every plugin. `/realm` |
| **Monuments and painted signs** | `RealmSculptor`, `RealmPainter` | Realm's monuments built from the game's own blocks and colours, and Realm's art and live boards (Chronicle, wanted, standings, holdings, treasure clues) on painted signs. Staff `/sculpt`, `/paint`; sculptures in `art/sculptures/`, paintings in `art/paintings/` |
| **Fair play** | `RealmWarden`, `RealmSentinel`, `RealmStats` | New-player protection, raid hours, combat-log flags, reports and mutes; a server-side cheat watch with evidence that ships in watch mode and never touches the game's own anti-cheat; pseudonymous statistics with opt-out. `/warden`, staff `/sentinel`, `/stats` |
| **The Chronicle** | `RealmChronicle` | The realm's public record (45 event types) for the game, the overlay, the portal and Discord. `/chronicle` |
| **Atmosphere moods** | `mods/`, `server/Set-Mood.ps1` | The game's own `Mods\*.cfg` overrides: Long Winter, Blood Moon, Golden Summer, Storm Season, Ashfall, Harvest Fair, Midwinter and more, applied at a restart. |

**Next to the game:**

| Feature | Where | What it is for |
|---|---|---|
| **Realm (player app)** | `launcher/`, player build | One screen: Play (joins through the player's own Steam) or Install, server status with who holds the throne, a server picker, signed news and signed updates, and "Can't join?". [`docs/player-launcher.md`](docs/player-launcher.md) |
| **Realm Steward (owner app)** | `launcher/`, Steward build | Setup wizard, up to four servers, crash and daily restarts with backups, Go Public and the signed server list. **On `team/steward-integration`, merging next:** deploys the plugins' data files, a Realm features screen with every plugin's switches, a Sentinel screen with Kick and Ban, Publish news and Publish update, and every plugin's staff commands in the Court. |
| **Connection Doctor** | Steward: **Doctor** screen. Player app: **Can't join?** | Answers "why can't I connect?" in one verdict with one fix. It checks the server process, the real ready line, the UDP/TCP ports, A2S, Steam sign-in, the address advice (address and port in **separate** boxes), firewall rules, the admin-console exposure and public reachability. It reads the game's own logs (see below), highlights 34 known messages taken word for word from the game DLL, explains a pasted error popup, and copies a redacted report. [`docs/connection-doctor.md`](docs/connection-doctor.md) |
| **Where the errors really are** | Doctor, Servers console | The game's own logger writes every message to `Logs\Log[yyMMdd-hhmmss].txt` in the working folder, **not** to `-logFile`; that file only gets Unity's lines. Server errors are therefore in `<server>\Logs\Log[...].txt`, client errors in `<game install>\Logs\Log[...].txt`, Steamworks errors in `ROK_Data\output_log.txt`. Most join refusals (password, full, version, ban) are only a popup on the player's screen. |
| **Live admin console** | Steward: Servers console | Steward starts `ROK.exe` with `-cport 11000..11003` and holds the game's admin-console socket on `127.0.0.1`. Commands get real answers, and every warning, error and exception the game logs streams into the Servers console as it happens. Stop sends `/shutdown` (the game saves, then exits). [`docs/admin-console.md`](docs/admin-console.md) |
| **The Court** | Steward: **Court** screen | Online players, kick, mute, ban with reason and days, the ban list, notices, chat as the server, whitelist, Save world (via the `RealmCourt.cs` plugin), a live chat/log feed and a moderation log on disk. |
| **Owner dashboard** | Steward: Home | Servers up, players online, last crash, last backup and next restart at a glance. |
| **Chronicle overlay and `/realm` page** | `chronicle/` | A read-only local service: the OBS overlay with coronation and rebellion moments, and a public page of the realm. [`chronicle/README.md`](chronicle/README.md) |
| **Realm Portal** | `portal/` | A static website generator: home with the current monarch and season, the Chronicle, every house, the Hall of Kings, how to play, rules, lore, download, an Atom feed and `status.json`. No server, no dependencies, strict CSP. [`portal/README.md`](portal/README.md) |
| **Discord herald and bot** | Steward: Settings; `bot/` | Opt-in relay of new Chronicle events to a Discord webhook as rich embeds in house colours, and a bot with `/realm` slash commands, a live status message and house roles. [`docs/discord-herald.md`](docs/discord-herald.md), [`bot/README.md`](bot/README.md) |
| **Stream scenes** | `streamkit/` | Five OBS browser scenes (War Board, Throne Room, Breaking News, Countdown, Starting Soon) in the Realm art pack. |
| **Art pack** | `art/` | Sigils, banners, shields, logos, an icon for every Chronicle type, badges for titles and seasons, key art and the palette, checked by `art/tools/build.mjs check`. [`docs/brand.md`](docs/brand.md) |

## If you cannot connect to your own server

1. Open Realm Steward and go to **Doctor**. Pick the server and press **Run checks**. The verdict at the top names the problem and the fix.
2. In the game's direct-connect window type **`127.0.0.1` in the address box and `7350` in the port box**. Typing `127.0.0.1:7350` into the address box gives "Unable to resolve host name".
3. If the game shows a popup, paste its text into the Doctor's box: it is matched against the game's own messages.
4. To see the server's own errors, pick **Server game log** in the Doctor's log view, or start the server with the live console on (the default with `ROK.exe`) and watch the Servers console.
5. **Copy report** gives a text you can share: public IPs, Steam IDs, the Steam auth ticket, passwords and your Windows user name are removed.

The Windows notice "eac_usermode blocked from loading into LSA" is harmless and unrelated. Every problem seen so far, with its fix, is in [`docs/troubleshooting.md`](docs/troubleshooting.md).

## Repository layout

| Path | What it is |
|---|---|
| `plugins/` | Oxide C# plugins (namespace `Oxide.Plugins`, `[Info(name, "Realm", "0.1.0")]`). <br>• `RealmHouses.cs`: houses, oaths, treaties. Commands `/house`, `/swear`, `/renounce`, `/treaty`. <br>• `CrownAndConsequences.cs`: crown tracking, decrees, council, claims and rebellion windows, tax cap, bounded ransom. Commands `/crown`, `/decree`, `/council`, `/claim`, `/ransom`. <br>• `RealmChronicle.cs`: the event log and the realm snapshot. Command `/chronicle`. <br>• `RealmContracts.cs`, `RealmSeasons.cs`, `RealmEvents.cs`, `RealmLaws.cs`, `RealmDynasties.cs`, `RealmRenown.cs`, `RealmTreasury.cs`, `RealmRavens.cs`, `RealmWarden.cs`, `RealmStats.cs`, `RealmCourt.cs`: see `plugins/docs/`. |
| `chronicle/` | A Node 22 service with no dependencies, bound to `127.0.0.1:8787`. It serves `GET /api/state`, `GET /api/events?since=<id>&limit=<n>`, `/healthz`, the OBS overlay `/overlay` and the public page `/realm`. It only reads data. See `chronicle/README.md`. |
| `launcher/` | The two Electron desktop apps from one codebase. <br>• **Realm Steward** (`Realm-Steward-Setup-<version>.exe`, `npm run dist:steward` or `dist:win`): the owner app. Setup wizard, up to four server copies with their own ports and up to 120 players each, live consoles, crash and daily restarts with backups, world backup and restore, the in-process Chronicle overlay, **Go Public** (opt-in firewall rules, router ports, checks) and **Publish server list** (a signed `servers.json`, written locally). <br>• **Realm** (`Realm-Setup-<version>.exe`, `npm run dist:player`): the player app. Signed server list, live status, Join and Join best server through the player's own Steam, `realm://join/<id>` links. It has no server controls. <br>It re-implements the `server/*.ps1` safety rules in Node. See `launcher/README.md` and `docs/going-public.md`. |
| `server/` | Windows PowerShell 5.1 scripts that work only on a **test copy** of the dedicated server: `New-TestServer`, `Start-LocalServer`, `Backup-Saves`, `Restore-Saves`, `Install-Oxide`, `Deploy-Plugins`, `Export-ModKeys`. |
| `mods/` | Notes on the built-in Mods system and a **template** for the "grim but readable" atmosphere preset. It holds placeholders only, because no Mods key names are verified yet. |
| `portal/` | The Realm Portal static site generator (`node build.mjs --data <server>\\oxide\\data`). Writes `portal/dist`. See `portal/README.md`. |
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
  - `type` is one of the types in `RealmChronicle.cs` `KnownTypes` (the same list as `chronicle/server.js` `EVENT_TYPES`; a test keeps them in step): the crown, war, house and ransom types (`coronation`, `abdication`, `claim_declared`, `rebellion_started`, `rebellion_ended`, `house_founded`, `oath_sworn`, `oath_broken`, `treaty_signed`, `treaty_broken`, `decree`, `ransom_set`, `ransom_paid`, `released`), contracts, seasons and events, law and dynasty, treasury and `rumour`.
  - `actors` holds public player names only, never locations or inventory.
- **`RealmState.json`** is `{king, house, since, houses:[{name, sigil, liege, members}], online, maxPlayers, updated}`.
- **`/api/state`** returns that state plus `stale: bool`.

## Easiest way: Build-Realm.bat, then Realm-Steward-Setup

1. Make sure Steam has **Reign Of Kings Dedicated Server** installed (Library > Tools).
2. Double-click **`Build-Realm.bat`** in this folder. It needs Node.js 22 and Git for Windows, never administrator rights. It checks both, runs `npm ci`, the launcher checks and tests, then writes `release\1.0.0\Realm-Steward-Setup-1.0.0.exe` (the owner app), `Realm-Setup-1.0.0.exe` (the player app) and `SHA256SUMS.txt`. If a step fails it prints `PROBLEM:` and `FIX:` and waits. (`cd launcher && npm run release` does the same build without the checks; `npm run dist:steward` and `npm run dist:player` still build into `launcher\dist\`, with portable versions.)
3. Run `Realm-Steward-Setup-1.0.0.exe`. It installs for your Windows user only, adds a desktop and Start-menu shortcut, and installs over an older Realm Steward. Uninstalling keeps your settings (`%APPDATA%\Realm`) and your worlds.
4. Open **Realm Steward**. On first run its setup wizard does the rest, with a progress bar for each step:
   finds the Steam server, copies it to the test folder (default `G:\RealmTest\server`), starts it once so the game writes its settings, downloads Oxide 2.0.3867 and checks its SHA-256, installs it with a backup of every replaced file, and deploys the Realm plugins. Finished steps are skipped; a failed step shows a plain-English message with **Copy details**.
5. In Realm Steward:
   - **Servers** starts and stops each server copy, shows its console and its settings (name, up to 120 players, ports, crash and daily restarts). **Add server** sets up a second, third or fourth copy.
   - **Play** opens your own copy through Steam (direct connect to `127.0.0.1` port `7350`).
   - **Overlay** gives the OBS address.
   - **Public** and **Publish** take a server online and produce the signed list for the player app; see [`docs/going-public.md`](docs/going-public.md).

Once the installer is built you never need a command window. The router and the Steam copy are never changed, and the firewall changes only when you press **Add firewall rules** on the Public screen and accept the Windows prompt. Node.js is not needed: the Chronicle overlay runs inside the app.

**Fallback:** `Realm.bat` still works. Download this repo (green **Code** button > **Download ZIP**), unzip it on **G:**, double-click `Realm.bat` and pick **1 Set up everything**, then **2 Play** (**3** updates plugins, **4** backs up the world, **5** undoes Oxide). If something fails, the window prints `PROBLEM: ...`; nothing else is changed. The manual steps below do the same thing one script at a time.

## Quick start on the owner's PC (smoke-test order)

The full checklist with pass/fail tables is in **`docs/smoke-test.md`**. Do the stages in this order, and only move on when the current stage passes.

All scripts default to these locations:
- The Steam server is `G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server`. It is **read only**, and only `New-TestServer.ps1` reads it.
- The test copy is `G:\RealmTest\server`.
- Backups go to `G:\RealmTest\backups`.

**Do not change the firewall or router** for these local tests (going public is in [`docs/going-public.md`](docs/going-public.md)). Testing is local only, on `127.0.0.1`. If Windows asks to allow the server through the firewall, choose Cancel.

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

9. `.\Deploy-Plugins.ps1`: copies `plugins\*.cs` into the test copy's `oxide\plugins`, where Oxide hot-loads them, and the data files some plugins read (sculptures, sign art, quest content, RealmArrival's site plan as `RealmArrival\site.json`) into `oxide\data`. Fix any compile errors in the repo and deploy again.
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
    Build the owner installer with `npm run dist:steward` (or `dist:win`) and the player installer with `npm run dist:player` (output in `launcher\dist\`).

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
| Chronicle service | **Tested here**: `npm test` (8/8 pass), every endpoint checked with curl, and the pages rendered in Chromium. |
| Realm Steward and Realm (`launcher/`) | **Tested here** on Linux only. <br>• Unit tests cover the path, cfg, zip, vdf and hash rules, Ed25519 list signing and verification, A2S, deep links, the `steam://` allow-list, ports, restart logic and firewall command construction. <br>• Automated walks through both apps ran under Electron, the steward against an imitation server that answers A2S and the player against a signed list and imitation servers (see `launcher/README.md`). <br>• A test proves the player app carries no server-control code. <br>An earlier Steward build **ran on the owner's Windows 11 PC on 2026-10-02**: setup, start, and the owner joining their own server with all 14 plugins loaded ([`docs/HANDOFF.md`](docs/HANDOFF.md)). The 1.0.0 installers were built here and installed and uninstalled under wine (per-user folder, shortcuts, uninstall entry, settings kept); **`Build-Realm.bat` and the 1.0.0 installers have not run on Windows yet**. **Unverified:** real `Server.exe` piped I/O, the registry lookup, the process check, the firewall scripts and their UAC prompt, DPAPI key storage, the `realm://` registration, and two or more real servers on one PC. |
| Plugins (`plugins/*.cs`) | **Compile-checked, never run in game.** All plugins compile with 0 errors at C# 3 against the shipped Oxide 2.0.3867 and patched game DLLs (`tools/plugin-compile-check/check.sh`). Behaviour on a live server is untested; spots that rely on unverified behaviour are marked `// UNVERIFIED:`. |
| Connection Doctor (`launcher/lib/doctor.js`, `lib/shared/gamelog.js`) | **Tested here** (unit tests, and an Electron walk-through against an imitation `ROK.exe` and game install). Every classifier string is copied from the decompiled game DLL. **Unverified on Windows:** the PowerShell socket and firewall listings, the Steam `ActiveProcess` registry read, that the client writes `Logs\Log[...].txt` in the install folder, and the location of `output_log.txt` for Unity 5.1. |
| Live admin console and Court (`lib/admin-console.js`, `lib/court-host.js`, `RealmCourt.cs`) | **Wire protocol proven from the decompiled DLL and tested here** byte for byte against an independent fake. **Never run against the real `ROK.exe`.** Unverified: that the dedicated server enables the console ("Admin console enabled." in its log), that Steward connects within the game's 10 s window, the exact wording of kick/ban replies, and `RealmCourt.cs` in game. If the console is never reached and the server exits early, Steward turns `-cport` off for that server automatically. **Behaviour change:** with the console on, closing Steward makes its servers save and stop. |
| RealmSeasons, RealmEvents and the other new plugins | **Compile-checked** at C# 3 against the 2.0.3867 DLLs and **behaviour-tested against mocks** (`plugins/docs/*/logic-tests/run.sh`: 89 to 140 checks each). **Never run in game.** Smoke tests: `docs/smoke-test.md` part E and each plugin doc. |
| Realm Portal (`portal/`) | **Tested here**: `npm test` (28 tests, including hostile-text escaping and no Steam IDs on any page) and screenshots. Hosting is left to the owner. |
| Discord herald (`launcher/lib/discord.js`) | **Tested here with a stubbed network** (URL validation, token redaction, rate limits, retries, 404 shut-off). **No post to a real webhook has been made.** DPAPI encryption on Windows is untested. |
| Player onboarding, Realm feed, Steward dashboard | **Tested here** in Electron walk-throughs with mock data. The feed's next-event countdown needs a plugin to publish the next event; no plugin does yet, so only the latest events show. The "Game starting / Joining" steps are timed guidance, not read from the game. |
| PowerShell scripts | **Parse-checked in CI** (PowerShell 7, plus the Windows PowerShell 5.1 syntax rules for `ops/`); never run on Windows. Always use `-WhatIf` first. |
| Mods atmosphere preset | **Key names read from the server DLL** (`docs/mods-keys-from-dll.md`). Which Mods file holds them, and whether a headless server applies fog, is untested; `Apply-Preset.ps1` checks against the server's own `*.defaults.cfg`. |
| Which exe to run (`Server.exe` or `ROK.exe`), the `ROK_Data`/`Server_Data` folder, the Oxide data path (`oxide\` or `Saves\oxide\`), `Assembly-CSharp_Original.dll`, and whether `bindIP=127.0.0.1` still lets clients authenticate | **Unverified.** The smoke test resolves each of these. |
| Client auto-connect | The game's own quick join (`-ip <host> -port <n>` → `Game.Join`) is **proven in the DLL code** ([`docs/join-and-scale.md`](docs/join-and-scale.md) §1). The player app uses it through `steam://run/344760//-ip … -port …/`, always with a paste-the-address fallback card. **Unverified:** that the EAC launcher forwards the arguments. `steam://connect` does not work (the game ignores `+connect`). |

## Known limitations

- Chronicle event ids continue from the highest id still in the file. If `RealmChronicle.json` is deleted, ids restart at 1. Overlays and the launcher that are already open then need a reload to see new events.
- The status pill in the launcher shows whether the Chronicle is fresh. It does not show whether the game port is reachable; the **Doctor** screen does.
- Steward's "ready" flag still fires on Unity's `Initialize engine version:` banner, which comes before the world is loaded. The game's real ready line ("Server for N players started on port P.") is in its own log file and, with the live console, in the Servers console; the Doctor checks for it.
- RealmEvents schedules in UTC and CrownAndConsequences in realm time (UTC + `UtcOffsetHours`); neither follows daylight saving. If you set an offset, shift RealmEvents' `StartUtc` times too (the plugin logs the corrected time at start-up).
- Ransom payment is honour-based: the captor confirms it. Contracts use real item escrow (untested in game; `ItemEscrow: false` switches to honour mode). Harvest Tithe was replaced by Royal Stores because gathering happens client-side and no server hook can boost it.
- The default rebellion windows are Wednesday 19:00 and Saturday 19:00 **UTC**. Change them in `oxide\config\CrownAndConsequences.json` to suit your time zone.
