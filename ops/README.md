# Realm ops: running Realm on a real server

This folder covers everything needed to run the realm of Ostreval for real players on a rented Windows machine: sizing, setup, the firewall, updates, offsite backups, uptime alerts, and what to do when things break.

| File | What it is |
|---|---|
| [`hosting-guide.md`](hosting-guide.md) | Choosing a Windows VPS or dedicated server, sized for 1 and for 4 servers at 120 players. Covers Windows Server setup, installing the server with SteamCMD (app 381690, anonymous login), ports, DDoS, and setting up backups and monitoring. |
| [`disaster-recovery.md`](disaster-recovery.md) | The runbook. For each kind of failure, from a griefed world to a lost VPS, it gives the steps and commands that get the realm back. |
| [`restore-drill.md`](restore-drill.md) | A monthly and quarterly checklist that proves the backups really restore. |
| `config/realm-ops.example.json` | The config template. Copy it **outside the repo** (for example `G:\RealmOps\realm-ops.json`) and fill it in. |
| `scripts/Install-RealmServer.ps1` | Installs SteamCMD and the dedicated server into folders you choose. |
| `scripts/Update-RealmServer.ps1` | Backs up every server, then updates the server files with SteamCMD. It can also refresh the instances and re-install Oxide. |
| `scripts/Backup-RealmOffsite.ps1` | Takes a snapshot, verifies it and makes a zip, then copies it to any S3-compatible bucket through rclone. Old copies are pruned. |
| `scripts/Restore-RealmBackup.ps1` | Restores a backup from disk or from the bucket, either as a drill into a spare folder or into a live instance. |
| `scripts/Register-RealmTasks.ps1` | Adds the Task Scheduler jobs: the nightly backup and the uptime monitor every 2 minutes. |
| `scripts/Watch-RealmUptime.ps1` | The uptime monitor. It checks A2S on each query port, the ping port and the process, and posts to a Discord webhook. It also warns when a backup is too old. |
| `scripts/Test-RealmOpsConfig.ps1` | Checks the config and everything it points to. `-SendTest` posts a test message to Discord. |
| `scripts/OpsCommon.ps1` | Shared helpers that the scripts dot-source. |
| `tests/` | The test suite (`Run-OpsTests.ps1`), the Windows PowerShell 5.1 syntax checker (`Test-PS51Syntax.ps1`) and the network fakes (`fakes.mjs`). |

## How the pieces fit

```
 Steam (app 381690, anonymous)
        │ SteamCMD  (Install-/Update-RealmServer.ps1)
        ▼
 G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server   ← "master": clean files, no Oxide, never run
        │ Realm Steward setup wizard / Add server (or server\New-TestServer.ps1)
        ▼
 G:\RealmTest\server   G:\Realm\s2   G:\Realm\s3   G:\Realm\s4     ← instances: Oxide + Realm plugins + saves
        │ Backup-RealmOffsite.ps1 (nightly task, 04:20)                │ Watch-RealmUptime.ps1 (every 2 min)
        ▼                                                              ▼
 G:\RealmOps\backups\<server>\realm-<server>-<UTC>.zip  ──rclone──►  S3 bucket   Discord channel
```

The master stays clean. Realm Steward copies it into each instance and installs Oxide only in those copies. So the update script refreshes the copies from the master and patches them again; it never runs SteamCMD on an instance (`validate` would undo Oxide there).

## Quick start (on the server, in an elevated Windows PowerShell 5.1)

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd <repo>\ops\scripts
Get-ChildItem *.ps1 | Unblock-File
New-Item -ItemType Directory G:\RealmOps | Out-Null
Copy-Item ..\config\realm-ops.example.json G:\RealmOps\realm-ops.json
notepad G:\RealmOps\realm-ops.json                      # fill in, see hosting-guide.md section 8

.\Install-RealmServer.ps1   -ConfigPath G:\RealmOps\realm-ops.json -WhatIf
.\Install-RealmServer.ps1   -ConfigPath G:\RealmOps\realm-ops.json
# ... set up the instances with Realm Steward (hosting-guide.md section 6) ...
.\Test-RealmOpsConfig.ps1   -ConfigPath G:\RealmOps\realm-ops.json -SendTest
.\Backup-RealmOffsite.ps1   -ConfigPath G:\RealmOps\realm-ops.json      # first backup, by hand
.\Register-RealmTasks.ps1   -ConfigPath G:\RealmOps\realm-ops.json -ProtectConfig
.\Restore-RealmBackup.ps1   -ConfigPath G:\RealmOps\realm-ops.json -Server 'Realm 1' -FromRemote   # first drill
```

Every script that changes something supports `-WhatIf`.

## Rules these scripts keep

- **No game files are modified, patched or redistributed.** SteamCMD downloads the dedicated server onto your own server, and nothing here writes into the master. Backups hold only worlds, plugin data, configs and your own `Mods\*.cfg` overrides. Game binaries, `*.defaults.cfg` and Mods assets are left out, and restore refuses them. Players use their own Steam copy of the game.
- **No secrets in the repo.** The webhook URL lives in your local `realm-ops.json`, and the bucket keys live in the `rclone.conf` you create. `ops/.gitignore` ignores both names. `Register-RealmTasks.ps1` refuses a config file that sits inside the repo, and `-ProtectConfig` limits both files to SYSTEM and Administrators. Because the tasks run as SYSTEM, `Register-RealmTasks.ps1` also refuses to register them while Users, Authenticated Users or Everyone can change the `ops\scripts` files, the folders above them, the config or the rclone program (otherwise any local account could run code as SYSTEM). A fresh clone on a data drive usually fails this check, because Windows gives Authenticated Users Modify there; the error prints the `icacls` command that fixes it. This ACL check is tested with sample rules on Linux and is **UNVERIFIED on real Windows**. Logs pass through a redactor, so webhook tokens and S3 secrets are never printed.
- **Nothing is deleted on restore.** The current state is moved aside to `<server>\_realm-backups\ops-restore-<UTC>\`, and a safety backup is taken first.
- **One ops job at a time.** Backup, update, restore and install share a lock file in `<opsRoot>\state\ops.lock`.

## Chronicle events

These scripts add **no** new Chronicle event types. Ops alerts go to the owner's Discord channel through the webhook, not into the public Chronicle.

## Testing and what is proven

Run the suite with PowerShell 7 and Node 18+ on any OS:

```
pwsh -NoProfile -File ops/tests/Test-PS51Syntax.ps1          # 0 problems required
pwsh -NoProfile -File ops/tests/Run-OpsTests.ps1 -Rclone <path to rclone>
```

| Area | Status |
|---|---|
| Parsing for Windows PowerShell 5.1 | **Checked here.** All 10 `.ps1` files parse with 0 errors. No PS7-only syntax (`?:`, `??`, `?.`, `&&`/`\|\|`) and no PS6+ cmdlet parameters are used (`Test-PS51Syntax.ps1`). **Not run on Windows PowerShell 5.1 itself.** |
| Backup → S3 → restore | **Tested here (Linux, PowerShell 7.4.6, rclone v1.68.2; the whole suite is 144 checks, 0 failing)** against a real S3 endpoint (`rclone serve s3`, with signature-v4 keys). The suite covers zip and manifest hashes, the sidecar, local and remote retention, download-back verification, a drill restore from the bucket, a live restore (whole folders replaced, overlay files kept aside, a pre-restore safety backup), refusal of a tampered zip, refusal of a folder without the instance marker, and refusal of a drill folder inside a live server. |
| Hot snapshot | **Tested here.** A fake server rewrites its save every 5 ms during the copy: the copy is retried 3 times and the backup is marked `consistent: false`. An idle server gives a consistent copy on the first try. **UNVERIFIED** on Windows: whether `ROK.exe` holds its save files with a share mode that lets them be read while it runs. If not, the backup fails with a clear error. In that case, schedule it right after Steward's daily restart. |
| A2S monitor | **Tested here** against a fake RoK server (fixed name "Another ROK Server", max 0), including the S2C challenge, garbage replies, a closed port (ICMP), DNS failure and the TCP ping port. The alert state machine is tested: debounce, reminders, the down→degraded change, recovery with downtime, and state surviving JSON. **UNVERIFIED** against a real `ROK.exe` (see `docs/join-and-scale.md` section 6, test 5). |
| Discord posting | **Tested here** against a fake webhook: a 429 retried after `retry_after`, a 404 not retried, UTF-8 JSON embeds, `allowed_mentions` locked down. **No post to a real Discord webhook was made.** |
| SteamCMD install and update | **Tested here with a fake `steamcmd`.** Covered: argument order, a retry after a failed first `app_update`, no retry on "No subscription", downloading and extracting `steamcmd.zip` over HTTP, build-id change detection, backup before update, an update stopped when the backup fails, and `-ApplyToInstances` calling `New-TestServer.ps1 -Refresh` and `Install-Oxide.ps1`. **The real SteamCMD was not run** (the research proxy blocks Valve's CDN). That 381690 downloads anonymously is VERIFIED-PRIMARY from the AMP template and Oxide's build (`docs/server-reference.md` section 1). The Authenticode signer of `steamcmd.exe` is UNVERIFIED. |
| Task Scheduler | **Tested here with the ScheduledTasks cmdlets mocked.** Covered: the tasks, triggers, SYSTEM principal, limits and the exact `powershell.exe` command line. **UNVERIFIED on real Windows**, including whether a repeating trigger with no `RepetitionDuration` repeats indefinitely on Windows Server 2016 and later, as is commonly reported (Server 2012 R2 may need a duration). |
| Windows argument quoting | **Tested here.** Tricky arguments (spaces, quotes, trailing backslashes, empty strings) reach a child process unchanged through `ConvertTo-OpsArgString`, using .NET's Windows-compatible command-line parser. |
