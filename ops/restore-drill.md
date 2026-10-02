# Restore drill checklist

A backup that has never been restored is a hope, not a backup. Run the **monthly drill** on the first Sunday of every month and the **quarterly rebuild drill** once a quarter. Each takes about 20 minutes and 2 to 3 hours respectively. Neither touches the live servers.

Record every drill in the log table at the bottom (copy it into your staff notes).

## Monthly drill (all servers, about 20 minutes)

Run on the game server, in `<repo>\ops\scripts`, with `$cfg = 'G:\RealmOps\realm-ops.json'`.

| # | Step | Pass when | ✓ |
|---|---|---|---|
| 1 | `.\Test-RealmOpsConfig.ps1 -ConfigPath $cfg` | No `[FAIL]` lines. "Remote reachable" is PASS. Each server shows a last good backup **under 26 h** old. | ☐ |
| 2 | Check Task Scheduler > Realm: both tasks show **Last Run Result 0x0** and a recent last run time. | Both 0x0 | ☐ |
| 3 | For **each** server: `.\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server '<name>' -FromRemote` | `Backup verified.` and `Drill extract OK`. "Verified: N file hashes; zip sidecar matches". | ☐ |
| 4 | Open `G:\RealmOps\state\drill-<server>.json` | `ok: true`, `downloadedFromRemote: true`, `savesFiles` > 0, `oxideDataFiles` lists `RealmChronicle.json`, `RealmHouses.json` and the other plugin files you expect | ☐ |
| 5 | Spot-check the content: open `<drill>\oxide\data\RealmChronicle.json` | The newest event is from the night before the backup time | ☐ |
| 6 | Pick an **older** backup (about 2 weeks) and drill it with `-RemoteFile <name>` | Verifies too (proves retention keeps working copies, not only the newest) | ☐ |
| 7 | Check the bucket keeps what you expect: `rclone lsl <remote>/<server> --config <rclone.conf>` | About `remoteKeepDays` worth of zips, each with a `.sha256` | ☐ |
| 8 | Trigger the monitor: in Steward, stop one **test** instance (or set a wrong `queryPort` for a minute) and wait 5 minutes | Discord shows "DOWN", then "back up" after you undo it | ☐ |
| 9 | Delete the drill folders: `Remove-Item G:\RealmOps\drill\* -Recurse` | Disk space back | ☐ |

If step 3 fails for the newest backup: drill the next older one. Then go to `disaster-recovery.md` scenario E **today**. Your RPO is now the age of the newest backup that verifies.

### Optional: boot the drill copy (do this at least every quarter)

This proves the game itself loads the restored world, which hash checks cannot.

1. In Steward, use a **spare instance slot** (for example `s4` if you run three servers) or a separate test machine. Stop it.
2. Restore into it live: `.\Restore-RealmBackup.ps1 -ConfigPath $cfg -Server '<spare>' -ZipPath <zip from step 3> -Live -WorldOnly -Yes`
   (`-WorldOnly` keeps the spare's own ports and settings. Add the spare to `realm-ops.json` with `"monitor": false, "backup": false` first.)
3. Start it and wait for `Server for N players started on port P.` in the console.
4. Join it with your own game (direct connect `127.0.0.1` and the spare's port in separate boxes). Check one known structure and `/crown`, `/house list` and `/chronicle`.
5. Stop it. Restore the spare's previous state from `<spare>\_realm-backups\ops-restore-<UTC>\` if you need it.

| # | Boot check | Pass when | ✓ |
|---|---|---|---|
| B1 | Server starts on the restored world | Ready line within the usual load time | ☐ |
| B2 | Oxide and plugins load | No plugin errors in the console. `/chronicle` answers. | ☐ |
| B3 | A known base or structure is where it should be | Found | ☐ |
| B4 | Houses, crown and season standings match the portal or the Chronicle at backup time | Match | ☐ |

## Quarterly rebuild drill (2 to 3 hours, on a throwaway machine)

This is `disaster-recovery.md` scenario C, done for practice with a stopwatch. Rent the cheapest Windows VPS that can run one instance for a few hours, then delete it.

| # | Step | Time taken | ✓ |
|---|---|---|---|
| R1 | Start the stopwatch. Order the VPS. | | ☐ |
| R2 | Windows basics: updates can wait, but set the G: volume, UTC, and RDP limited to your IP (`hosting-guide.md` 4.1 and 4.2) | | ☐ |
| R3 | Get the repo. Recreate `realm-ops.json` and `rclone.conf` **from the password manager only** (this is the real test: is everything you need in there?) | | ☐ |
| R4 | `Install-RealmServer.ps1` | | ☐ |
| R5 | Realm Steward wizard: one instance | | ☐ |
| R6 | `Restore-RealmBackup.ps1 -FromRemote` (drill), then `-Live -Yes` | | ☐ |
| R7 | Start the server, join from home by IP, find a known base | | ☐ |
| R8 | Stop the stopwatch. Total = your real **RTO**. | | ☐ |
| R9 | **Delete the VPS** and anything it created at the bucket: nothing, if you only restored. Make sure no backup task was registered there. | | ☐ |

Anything you had to look up that was not in the password manager or in this repo is a gap: write it down and fix it before the next drill.

## Drill log

| Date (UTC) | Who | Type (monthly, boot, rebuild) | Servers | Newest verified backup age | Result | RTO measured | Gaps found / actions |
|---|---|---|---|---|---|---|---|
| | | | | | | | |
