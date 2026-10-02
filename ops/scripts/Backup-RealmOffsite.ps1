<#
.SYNOPSIS
  Nightly Realm backup: snapshot each server's world and settings, zip and verify it, then copy it offsite with rclone.

.DESCRIPTION
  For every server in the ops config (or only those named with -Server):
    1. Copies Saves\, oxide\data\ and (unless backup.includeConfig is false) oxide\config\, oxide\plugins\,
       Configuration\ and the owner's Mods\*.cfg overrides to a staging folder. The copy reads files with
       sharing enabled, so it works while the server runs. If any file changes during the copy (the game saved),
       the copy is repeated up to 3 times; the manifest records whether the final copy was consistent.
    2. Writes <backup.localDir>\<server>\realm-<server>-<yyyyMMdd-HHmmss>Z.zip with a realm-backup.json manifest
       (SHA-256 of every file) and a .sha256 sidecar, then re-opens the zip, extracts it to staging and checks
       every hash.
    3. Unless -LocalOnly or backup.remote is not set: uploads the zip and sidecar with
       "rclone copyto" to <backup.remote>/<server>/ and confirms the size at the remote
       (backup.verifyByDownload = true also downloads it back and compares SHA-256).
    4. Prunes local zips beyond backup.keepLocal, and remote zips older than backup.remoteKeepDays, always keeping
       the newest backup.remoteMinKeep at the remote. Set remoteKeepDays to 0 to leave remote retention to the
       bucket's lifecycle rules (recommended when the rclone key cannot delete).
    5. Records the result in <opsRoot>\state\backup-<server>.json, which Watch-RealmUptime.ps1 reads to warn when
       the newest offsite backup is too old.

  Only reads the server folders. Credentials live in the rclone config file the owner made; none are in this repo.
  Exit code: 0 all servers backed up, 1 at least one failed, 2 config or lock problem.

.EXAMPLE
  .\Backup-RealmOffsite.ps1 -ConfigPath G:\RealmOps\realm-ops.json -WhatIf
  .\Backup-RealmOffsite.ps1 -ConfigPath G:\RealmOps\realm-ops.json
  .\Backup-RealmOffsite.ps1 -ConfigPath G:\RealmOps\realm-ops.json -Server 'Realm 2' -LocalOnly
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [string[]]$Server = @(),
    [switch]$LocalOnly,
    [switch]$NoPrune,
    [string]$Label = '',
    # Used by Update-RealmServer.ps1, which already holds the ops lock.
    [switch]$NoLock
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')

$lock = $null
$failures = 0
try {
    $cfg = Read-OpsConfig $ConfigPath
    Assert-OpsConfig $cfg 'Backup'
    $opsRoot = Assert-OpsSafeDir ([string]$cfg.opsRoot) 'opsRoot'
    $localDir = Assert-OpsSafeDir ([string](Get-OpsProp $cfg 'backup.localDir')) 'backup.localDir'
    $servers = @(Get-OpsServers $cfg $Server | Where-Object { $_.Backup })
    $remote = Get-OpsProp $cfg 'backup.remote'
    $offsite = (-not $LocalOnly) -and -not (Test-OpsPlaceholder $remote)
    $includeConfig = [bool](Get-OpsProp $cfg 'backup.includeConfig' $true)
    $keepLocal = [int](Get-OpsProp $cfg 'backup.keepLocal' 14)
    $keepDays = [int](Get-OpsProp $cfg 'backup.remoteKeepDays' 30)
    $minKeep = [int](Get-OpsProp $cfg 'backup.remoteMinKeep' 7)
    $retryDelay = [int](Get-OpsProp $cfg 'backup.snapshotRetrySeconds' 20)
    Start-OpsLog $opsRoot 'backup' | Out-Null
    foreach ($s in $servers) {
        if (Test-OpsPathInside $localDir $s.Root) { throw "backup.localDir must be outside the server folder $($s.Root)." }
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 2
}

if (-not $offsite -and -not $LocalOnly) { Write-OpsLog 'backup.remote is not set: backups stay on this machine only. That is NOT an offsite backup.' 'WARN' }

if (-not $PSCmdlet.ShouldProcess(($servers | ForEach-Object { $_.Name }) -join ', ', 'Back up (snapshot, zip, verify' + $(if ($offsite) { ', upload' } else { '' }) + ')')) {
    foreach ($s in $servers) {
        Write-Host ("  {0}: {1} -> {2}" -f $s.Name, $s.Root, (Join-Path $localDir $s.Slug))
        if ($offsite) { Write-Host ("      offsite: {0}" -f (Join-OpsRemote ([string]$remote) $s.Slug)) }
    }
    exit 0
}

if (-not $NoLock) {
    try { $lock = Enter-OpsLock $opsRoot 'backup' } catch { Write-OpsLog $_.Exception.Message 'ERROR'; exit 2 }
}

try {
    foreach ($s in $servers) {
        $stamp = Get-OpsUtcStamp
        $statePath = Join-Path (Join-Path $opsRoot 'state') ('backup-{0}.json' -f $s.Slug)
        $state = Read-OpsJsonFile $statePath
        if ($null -eq $state) { $state = New-Object PSObject }
        $stateHash = [ordered]@{}
        foreach ($p in $state.PSObject.Properties) { $stateHash[$p.Name] = $p.Value }
        $stage = Join-Path (Join-Path $opsRoot 'staging') $s.Slug
        $verifyDir = Join-Path (Join-Path $opsRoot 'staging') ($s.Slug + '-verify')
        $stateHash.lastAttempt = Get-OpsIsoNow
        try {
            Write-OpsLog "== $($s.Name): $($s.Root)"
            if (-not (Test-Path -LiteralPath $s.Root -PathType Container)) { throw "Server folder not found: $($s.Root)" }
            if (-not (Test-Path -LiteralPath (Join-Path $s.Root $script:OpsInstanceMarker))) {
                Write-OpsLog "No $script:OpsInstanceMarker marker in $($s.Root); backing up anyway (read only)." 'WARN'
            }
            $running = @(Get-OpsServerProcesses $s.Root).Count -gt 0
            $snap = New-OpsSnapshot $s.Root $stage $includeConfig 3 $retryDelay
            if (@($snap.Files).Count -eq 0) { throw 'Nothing to back up: no Saves or oxide\data yet (has the server run?).' }
            if (-not $snap.Consistent) { Write-OpsLog 'Files kept changing during all 3 copies; the backup was kept but may mix two saves. Prefer the backup taken at the daily restart.' 'WARN' }

            # -Label goes into the manifest only, so file names stay sortable by their UTC stamp.
            $zipName = 'realm-{0}-{1}.zip' -f $s.Slug, $stamp
            $zipPath = Join-Path (Join-Path $localDir $s.Slug) $zipName
            $manifest = [ordered]@{
                format = $script:OpsBackupFormat; tool = 'Backup-RealmOffsite.ps1'; server = $s.Name; slug = $s.Slug
                source = $s.Root; machine = [Environment]::MachineName; created = (Get-OpsIsoNow); label = $Label
                serverRunning = $running; consistent = $snap.Consistent; attempts = $snap.Attempts
                includeConfig = $includeConfig; files = 0; bytes = 0; entries = @()
            }
            $sha = New-OpsBackupZip $stage $snap.Files $zipPath $manifest
            $check = Test-OpsBackupZip $zipPath $verifyDir
            if (-not $check.Ok) { throw ('Backup zip failed verification: ' + ($check.Problems -join '; ')) }
            $zipBytes = (Get-Item -LiteralPath $zipPath).Length
            Write-OpsLog ("Local backup OK: {0} ({1} files, {2}, {3} verified, consistent={4}, server running={5})" -f $zipPath, $manifest.files, (Format-OpsBytes $zipBytes), $check.FilesVerified, $snap.Consistent, $running) 'OK'
            $stateHash.lastLocal = Get-OpsIsoNow
            $stateHash.lastLocalZip = $zipPath
            $stateHash.lastLocalSha256 = $sha
            $stateHash.lastConsistent = $snap.Consistent

            if ($offsite) {
                $remotePath = Send-OpsBackupToRemote $cfg $zipPath $s.Slug
                Write-OpsLog "Offsite copy OK: $remotePath" 'OK'
                $stateHash.lastOffsite = Get-OpsIsoNow
                $stateHash.lastOffsitePath = $remotePath
            }

            if (-not $NoPrune) {
                $names = @(Get-ChildItem -LiteralPath (Join-Path $localDir $s.Slug) -File -Filter 'realm-*.zip' | ForEach-Object { $_.Name })
                foreach ($old in @(Get-OpsLocalPrunePlan $names $keepLocal)) {
                    $p = Join-Path (Join-Path $localDir $s.Slug) $old
                    Remove-Item -LiteralPath $p -Force
                    if (Test-Path -LiteralPath ($p + '.sha256')) { Remove-Item -LiteralPath ($p + '.sha256') -Force }
                    Write-OpsLog "Pruned local $old"
                }
                if ($offsite -and $keepDays -gt 0) {
                    $dir = Join-OpsRemote ([string]$remote) $s.Slug
                    $listing = @(Get-OpsRemoteListing $cfg $dir)
                    $plan = @(Get-OpsRemotePrunePlan @($listing | ForEach-Object { [string]$_.Name }) $keepDays $minKeep ([datetime]::UtcNow))
                    foreach ($old in $plan) {
                        $r = Invoke-OpsRclone $cfg @('deletefile', (Join-OpsRemote $dir $old)) 600
                        if ($r.ExitCode -ne 0) { Write-OpsLog ("Could not prune remote {0} (rclone exit {1}); continuing. A key without delete rights does this: use bucket lifecycle rules instead." -f $old, $r.ExitCode) 'WARN' }
                        else { Write-OpsLog "Pruned remote $old" }
                    }
                }
            }
            $stateHash.lastSuccess = Get-OpsIsoNow
            $stateHash.lastError = $null
        } catch {
            $failures++
            $stateHash.lastError = Protect-OpsText $_.Exception.Message
            Write-OpsLog ("Backup of {0} FAILED: {1}" -f $s.Name, $_.Exception.Message) 'ERROR'
            $hook = Get-OpsProp $cfg 'monitor.discordWebhookUrl'
            if ([bool](Get-OpsProp $cfg 'monitor.notifyBackupFailures' $true) -and (Test-OpsDiscordWebhookUrl $hook)) {
                $payload = New-OpsDiscordPayload ("Backup failed: {0}" -f $s.Name) (Protect-OpsText $_.Exception.Message) 'down' @(
                    @{ name = 'Machine'; value = [Environment]::MachineName; inline = $true },
                    @{ name = 'Log'; value = [string]$script:OpsLogFile; inline = $false })
                $sent = Send-OpsDiscordMessage $hook $payload
                if (-not $sent.Ok) { Write-OpsLog ("Discord notice not sent: {0}" -f $sent.Error) 'WARN' }
            }
        } finally {
            foreach ($d in @($stage, $verifyDir)) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } }
            try { Write-OpsJsonFile $statePath $stateHash } catch { Write-OpsLog "Could not write $statePath : $($_.Exception.Message)" 'WARN' }
        }
    }
} finally {
    Exit-OpsLock $lock
}

if ($failures -gt 0) { Write-OpsLog "$failures server(s) failed. See $script:OpsLogFile" 'ERROR'; exit 1 }
Write-OpsLog 'All backups done.' 'OK'
exit 0
