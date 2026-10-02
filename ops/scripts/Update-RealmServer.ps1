<#
.SYNOPSIS
  Updates the Reign of Kings Dedicated Server (app 381690) with SteamCMD, after a verified backup of every server.

.DESCRIPTION
  1. Takes the ops lock (no backup or other update can run at the same time).
  2. With -ApplyToInstances: checks every configured server is stopped before anything else happens.
  3. Runs Backup-RealmOffsite.ps1 for every server (offsite too, unless -LocalBackupOnly). If any backup fails the
     update stops here and nothing is changed.
  4. Runs SteamCMD app_update 381690 (anonymous) on the MASTER copy only (masterServerDir). The master never holds
     Oxide, so "validate" (-Validate) is safe there.
  5. Compares the build id in steamapps\appmanifest_381690.acf before and after.
  6. If the build changed (or -Force) and -ApplyToInstances is given, refreshes each instance from the master with
     server\New-TestServer.ps1 -Refresh (copies game files over; saves, oxide\ and Configuration\ that are not in
     the master are kept) and re-installs Oxide 2.0.3867 with server\Install-Oxide.ps1, because the refresh puts back
     the vanilla Assembly-CSharp.dll. Without -ApplyToInstances it prints those commands instead.

  Reign of Kings has not had a server build in years, so the usual result is "already up to date".
  Oxide 2.0.3867 matches the current build; a NEW game build might not work with it (UNVERIFIED until it happens):
  if the server fails to start after an update, restore the instance from the pre-update backup and stay on the old
  build (keep a copy of the old master folder; see ops\disaster-recovery.md).

.EXAMPLE
  .\Update-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json -WhatIf
  .\Update-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json
  .\Update-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json -ApplyToInstances
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [switch]$ApplyToInstances,
    [switch]$Validate,
    [switch]$Force,
    [switch]$LocalBackupOnly,
    [int]$TimeoutMinutes = 120,
    # For testing only.
    [string]$SteamCmdExe = '',
    [string]$RealmServerScripts = '',
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')
if ($Yes) { $ConfirmPreference = 'None' }   # -Yes answers every prompt, including ShouldProcess's

$cfg = Read-OpsConfig $ConfigPath
Assert-OpsConfig $cfg 'Update'
$opsRoot = Assert-OpsSafeDir ([string]$cfg.opsRoot) 'opsRoot'
$master = Get-OpsFullPath ([string]$cfg.masterServerDir)
$why = Get-OpsMasterDirProblem $master $cfg
if ($why) { throw "Refusing master folder: $why" }
if (-not (Test-OpsServerFolder $master)) { throw "No server in $master. Run Install-RealmServer.ps1 first." }
$exe = $SteamCmdExe
if (-not $exe) { $exe = Join-Path ([string]$cfg.steamcmdDir) 'steamcmd.exe' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "SteamCMD not found at $exe. Run Install-RealmServer.ps1 first." }
$serverScripts = $RealmServerScripts
if (-not $serverScripts) { $serverScripts = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'server' }
$oxideZip = [string](Get-OpsProp $cfg 'oxideZip' '')
$servers = @(Get-OpsServers $cfg)

$before = Get-OpsAppManifest $master
$beforeBuild = $null
if ($before) { $beforeBuild = $before.BuildId }

Write-Host ''
Write-Host "  Master   : $master (build $beforeBuild)"
Write-Host "  SteamCMD : $exe"
Write-Host ("  Backup   : {0} server(s), {1}" -f $servers.Count, $(if ($LocalBackupOnly) { 'local only' } else { 'local + offsite' }))
Write-Host ("  Instances: {0}" -f $(if ($ApplyToInstances) { 'refresh + re-install Oxide if the build changes' } else { 'not touched (commands printed)' }))
Write-Host ''

if (-not $PSCmdlet.ShouldProcess($master, "Back up all servers, then SteamCMD app_update $script:OpsAppId")) { return }
if (-not $Yes -and -not $PSCmdlet.ShouldContinue('Back up every server and then update the master server files with SteamCMD?', 'Realm')) { return }

Start-OpsLog $opsRoot 'update' | Out-Null
$lock = Enter-OpsLock $opsRoot 'update'
try {
    if ($ApplyToInstances) {
        if ($ApplyToInstances -and (-not $oxideZip -or -not (Test-Path -LiteralPath $oxideZip -PathType Leaf))) {
            throw "oxideZip in the config must point to Oxide.ReignOfKings 2.0.3867 (the zip Install-Oxide.ps1 checks) when using -ApplyToInstances."
        }
        foreach ($s in $servers) {
            $running = @(Get-OpsServerProcesses $s.Root)
            if ($running.Count -gt 0) { throw "Server '$($s.Name)' is running (pid $(($running | ForEach-Object { $_.Id }) -join ', ')). Stop it in Realm Steward first." }
        }
    }

    Write-OpsLog '== Step 1: backup before update'
    $backupArgs = @{ ConfigPath = $ConfigPath; NoLock = $true; Label = 'pre-update' }
    if ($LocalBackupOnly) { $backupArgs.LocalOnly = $true }
    & (Join-Path $PSScriptRoot 'Backup-RealmOffsite.ps1') @backupArgs -Confirm:$false
    if ($LASTEXITCODE -ne 0) { throw "Backup failed (exit $LASTEXITCODE). Nothing was updated." }

    Write-OpsLog "== Step 2: SteamCMD app_update $script:OpsAppId on the master copy"
    $ok = $false
    $result = $null
    for ($attempt = 1; $attempt -le 3 -and -not $ok; $attempt++) {
        $r = Invoke-OpsProcess $exe (Get-OpsSteamCmdArgs $master -Validate:$Validate) ($TimeoutMinutes * 60) (Split-Path -Parent $exe)
        if ($script:OpsLogFile) { [System.IO.File]::AppendAllText($script:OpsLogFile, (Protect-OpsText $r.Output) + [Environment]::NewLine) }
        $result = Get-OpsSteamCmdResult $r.Output $r.ExitCode
        $ok = $result.Ok
        if (-not $ok) {
            Write-OpsLog ("SteamCMD attempt {0}: {1}" -f $attempt, $result.Message) 'WARN'
            if ($result.Message -match 'No subscription|Disk write') { break }
            if ($attempt -lt 3) { Start-Sleep -Seconds 10 }
        }
    }
    if (-not $ok) { throw "SteamCMD update failed: $($result.Message). The instances were not touched." }
    $after = Get-OpsAppManifest $master
    $afterBuild = $null
    if ($after) { $afterBuild = $after.BuildId }
    $changed = ($beforeBuild -ne $afterBuild)
    Write-OpsJsonFile (Join-Path (Join-Path $opsRoot 'state') 'master.json') ([ordered]@{
        masterServerDir = $master; appId = $script:OpsAppId; buildId = $afterBuild; previousBuildId = $beforeBuild
        installedBy = 'Update-RealmServer.ps1'; at = (Get-OpsIsoNow); steamcmd = $result.State
    })
    if ($changed) { Write-OpsLog "Master updated: build $beforeBuild -> $afterBuild." 'OK' }
    else { Write-OpsLog "Master already current (build $afterBuild)." 'OK' }

    if (-not $changed -and -not $Force) { Write-OpsLog 'Instances need nothing.' 'OK'; return }

    Write-OpsLog '== Step 3: instances'
    foreach ($s in $servers) {
        $refresh = Join-Path $serverScripts 'New-TestServer.ps1'
        $oxide = Join-Path $serverScripts 'Install-Oxide.ps1'
        if (-not $ApplyToInstances) {
            Write-OpsLog ("{0}: stop it, then run:" -f $s.Name) 'WARN'
            Write-Host ("    & '{0}' -Source '{1}' -Destination '{2}' -Refresh" -f $refresh, $master, $s.Root)
            Write-Host ("    & '{0}' -ServerRoot '{1}' -ZipPath '<Oxide.ReignOfKings 2.0.3867 zip>'" -f $oxide, $s.Root)
            continue
        }
        Write-OpsLog "$($s.Name): refreshing game files from the master"
        & $refresh -Source $master -Destination $s.Root -Refresh -Yes -Confirm:$false
        Write-OpsLog "$($s.Name): re-installing Oxide 2.0.3867"
        & $oxide -ServerRoot $s.Root -ZipPath $oxideZip -Yes -Confirm:$false
        Write-OpsLog "$($s.Name): done. Start it in Realm Steward and check the console for the ready line." 'OK'
    }
} finally {
    Exit-OpsLock $lock
}
