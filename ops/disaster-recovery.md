# Disaster recovery runbook

Use this page when something is wrong **now**. Find the scenario, follow it in order, and do not skip the "before you touch anything" step.

All commands run in an elevated **Windows PowerShell** on the game server, in `<repo>\ops\scripts`, with `$cfg = 'G:\RealmOps\realm-ops.json'`.

## Targets

| | Target | Why |
|---|---|---|
| **RPO** (data you can lose) | at most **24 h** of world. Usually much less, because Steward also zips the world locally at each daily restart. | The nightly offsite backup |
| **RTO** (time to get back online) | world rollback **30 min**. Full rebuild on a new machine **3 h**. | Measured by the drills in `restore-drill.md`; update this table with your real times. |

## Before you touch anything (every scenario)

1. **Announce** in Discord: what is down, and that you are on it. Players forgive downtime; they do not forgive silence.
2. **Write down the time** and keep notes as you go: what you saw, and what you did and when. They go into the post-incident note.
3. **Do not delete anything.** Every Realm script moves things aside rather than deleting them; do the same by hand.
4. If the world is still there but damaged, **take a copy first**, even a broken one:
   ```powershell
   .\Backup-RealmOffsite.ps1 -ConfigPath $cfg -Server 'Realm 1' -LocalOnly -Label 'incident'
   ```

---

## A. A server is down or crash-looping

Symptoms: Discord "Realm N is DOWN"; Steward shows crashes; players cannot connect.

1. Open Realm Steward > **Servers** and look at the console and crash count. Then **Doctor** > Run checks. The verdict names the cause in most cases.
2. Read the game's own log: `<instance>\Logs\Log[yyMMdd-hhmmss].txt`. That is where the server writes, **not** `-logFile` (README, "Where the errors really are").
3. Common causes:
   - **A plugin exception right after a deploy**: roll the plugin back (redeploy the previous version from git) and restart.
   - **Disk full**: `Get-PSDrive G`. Clear `G:\RealmOps\staging`, old Steward backups or `G:\RealmOps\backups` (keep the newest!), then restart.
   - **Port in use** after an unclean stop: `Get-NetUDPEndpoint -LocalPort 7350`. Find the stray `ROK.exe` and stop it with Task Manager.
   - **World file corrupt** (the server dies while loading the save): go to scenario B.
4. If Steward itself is gone (after a reboot), log in to the desktop session and start Steward. See `hosting-guide.md` 4.6.

## B. The world is damaged: corruption, a griefing exploit, a bad plugin wiped data

Goal: roll **one server** back to a known-good backup.

1. Stop that server in Steward.
2. Copy the current state (step 4 above).
3. Choose the backup: the newest one **before** the damage.
   ```powershell
   Get-ChildItem G:\RealmOps\backups\realm-1 -Filter *.zip | Sort-Object Name | Select-Object -Last 10 Name, Length
   # or at the bucket:
   G:\RealmOps\rclone\rclone.exe lsl realm-offsite:<bucket>/realm/realm-1 --config G:\RealmOps\secure\rclone.conf
   ```
   File names carry the UTC time: `realm-realm-1-20261002-042000Z.zip`.
4. **Drill it first** (2 minutes; it proves the backup is good and touches nothing live):
   ```powershell
   .\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server 'Realm 1' -ZipPath <zip>          # local zip
   .\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server 'Realm 1' -FromRemote -RemoteFile <name>   # from the bucket
   ```
5. Restore live:
   ```powershell
   .\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server 'Realm 1' -ZipPath <zip> -Live -WhatIf
   .\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server 'Realm 1' -ZipPath <zip> -Live
   ```
   It refuses if the server is running or the folder is not a Realm instance. It takes a `pre-restore` backup, then moves the current `Saves\` and `oxide\data\` aside to `<instance>\_realm-backups\ops-restore-<UTC>\`, and puts the backup in.
   - Add `-WorldOnly` to keep the current configs and plugins and roll back only the world and plugin data.
6. Start the server. Wait for `Server for N players started on port P.`, log in, and check a known base and `/chronicle`.
7. Tell players exactly what time the world was rolled back to.

Steward's own restart backups (`Backup-Saves.ps1` format, under the Steward backup folder) restore with Steward's **Restore** button or `server\Restore-Saves.ps1`. Ops zips restore **only** with `Restore-RealmBackup.ps1`.

## C. The machine is gone: provider outage, account closed, disk lost, server compromised

Goal: the same realm on a **new** machine. The world comes from the bucket.

1. **Order a new server** (`hosting-guide.md` 2 and 3), possibly at another provider. Set up Windows (4.1 to 4.5), the G: volume and the firewall (section 5).
2. **Get the repo** onto the new server (git clone or ZIP), the same version as before if possible.
3. **Recreate the secrets** from your password manager. They were never in the repo:
   - `G:\RealmOps\realm-ops.json` from `ops\config\realm-ops.example.json` (server names must match the old ones: they name the bucket folders),
   - `G:\RealmOps\secure\rclone.conf` (bucket keys, plus crypt passwords if you used crypt),
   - the Discord webhook URL (or make a new webhook).
4. Install rclone and check it can see the backups:
   ```powershell
   .\Test-RealmOpsConfig.ps1 -ConfigPath $cfg
   ```
5. Install the server and set up instances: `Install-RealmServer.ps1`, then the Steward wizard and **Add server** for each instance (`hosting-guide.md` section 6). Start each instance **once** so it creates its folders, then stop it.
6. For each server, drill and then restore live from the bucket:
   ```powershell
   .\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server 'Realm 1' -FromRemote
   .\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server 'Realm 1' -FromRemote -Live -Yes
   ```
   A full restore brings back `Configuration\` too. If the new machine's ports differ, use `-WorldOnly` and set the ports in Steward.
7. Point `play.<domain>` at the new IP (TTL 300 means about 5 minutes). If you publish a signed server list, republish it from Steward with the new address.
8. Register the tasks again (`Register-RealmTasks.ps1 -ProtectConfig`) and run one manual backup.
9. If the old machine was **compromised**: rotate everything before step 3. That means a new bucket key (and revoking the old one), a new Discord webhook, new RDP and admin passwords, and the Steward list-signing key if it was on that machine. Do not restore the `oxide\plugins` folder from a backup taken after the compromise: redeploy plugins from git.

## D. An update broke the server

Symptoms: right after `Update-RealmServer.ps1 -ApplyToInstances` or a plugin deploy, the server will not start or Oxide does not load.

- **Plugin deploy**: redeploy the previous plugin version from git. Data is untouched.
- **Game build changed** and Oxide 2.0.3867 no longer loads (**UNVERIFIED** that this can happen; no new build has appeared since the research):
  1. Stop all instances.
  2. Put back the previous master (you copied it before updating, `hosting-guide.md` 9): `robocopy G:\RealmOps\master-previous <master> /MIR`.
  3. Refresh each instance from it and re-install Oxide (`Update-RealmServer.ps1` prints the two commands), or restore the `pre-update` backup into each instance (scenario B, step 5).
  4. Stay on the old build. Do not run `Update-RealmServer.ps1` again until someone confirms Oxide works with the new build.

## E. Backups are failing ("No fresh backup" or "Backup failed" in Discord)

1. Read `G:\RealmOps\logs\backup-<date>.log` and `G:\RealmOps\state\backup-<server>.json` (`lastError`).
2. Run it by hand to see it live: `.\Backup-RealmOffsite.ps1 -ConfigPath $cfg`.
3. Typical causes:
   - **rclone errors 403/401**: the key was revoked or expired. Make a new key and update `rclone.conf`.
   - **"Another Realm ops job is running"**: a stuck job. Check that nothing ops-related is running in Task Manager; the lock clears itself when its process has ended.
   - **Disk full**: lower `backup.keepLocal`, clear `G:\RealmOps\staging`.
   - **"Could not copy the save files"**: the game held a file exclusively. Move the backup time to 10 minutes after Steward's daily restart.
4. Until it is fixed, the local Steward restart backups are your only copies. Copy the newest one somewhere off the machine by hand.

## F. Under DDoS

Symptoms: everyone lags or drops at once. The monitor flaps or shows down from outside but up from inside. The provider sends a mitigation notice.

1. Do **not** restart the server repeatedly; that does not help.
2. Open a ticket with the provider and ask for always-on or stricter mitigation for UDP 7350-7353 and 27015-27018.
3. If the provider null-routes your IP, ask for a new IP (or move to a backup machine with scenario C) and update `play.<domain>`.
4. Afterwards, check that only the ports in `hosting-guide.md` section 5 are open, and consider closing the A2S query ports to the public (section 7).
5. Tell players in Discord. Do not name or accuse anyone publicly. Moderation evidence goes to staff only.

## G. A secret leaked (webhook URL, bucket key, rclone.conf, RDP password)

1. Revoke it at the source: delete the Discord webhook, delete the bucket key, change the password.
2. Create a new one and put it in the local file (`realm-ops.json` or `rclone.conf`). Run `Register-RealmTasks.ps1 -ProtectConfig` again.
3. If it was ever committed to git, rewriting history is **not enough**: revoke the old secret anyway.
4. With bucket versioning or object lock on (`hosting-guide.md` 8.1), check that no backups were deleted or overwritten. If they were, restore old versions from the bucket's console.

---

## After the incident

Write a short note in the staff channel:
- what happened, when it was detected and how (monitor, players, provider),
- what was done, and the real RPO and RTO you achieved,
- what changes now (a config value, a provider ticket, a new check in `restore-drill.md`).

Update the **Targets** table at the top if reality was different.
