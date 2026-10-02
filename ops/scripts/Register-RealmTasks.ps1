<#
.SYNOPSIS
  Registers (or removes) the Windows Task Scheduler jobs for Realm ops: the nightly offsite backup and the uptime
  monitor. Run in an elevated Windows PowerShell.

.DESCRIPTION
  Creates two tasks in the \Realm\ folder of Task Scheduler, both running as SYSTEM whether or not anyone is logged in:
    Realm Nightly Backup : daily at -BackupTime (local time, default 04:20), Backup-RealmOffsite.ps1.
                           Up to 3 hours; retried twice 15 minutes apart if it fails; starts late if the machine
                           was off at the time.
    Realm Uptime Monitor : every -MonitorEveryMinutes (default 2), Watch-RealmUptime.ps1. Up to 5 minutes per run.
  -ProtectConfig also locks the ops config file (it holds the Discord webhook URL) and the rclone config file
  (it holds the bucket keys) to SYSTEM and Administrators only.
  -Unregister removes both tasks. Nothing else is changed.

  Because both tasks run as SYSTEM, the script refuses to register them while an ordinary account could change what
  they run: the ops\scripts folder and its files, the ops and repository folders above it, the config file and the
  rclone program and config it names. On a data drive (G:\ and so on) Windows gives "Authenticated Users" Modify by
  default, so a fresh clone usually fails this check. Fix it by limiting the repository folder to Administrators and
  SYSTEM (the error message prints the icacls command), or by copying ops\scripts to a folder only administrators can
  change. -ProtectConfig locks the config files itself, so they are not checked when it is given.
  -AllowWritableByUsers skips the check (only for a machine where every account is an administrator anyway).

  Pick a backup time that does NOT overlap Realm Steward's daily restart, or put it 10 minutes after the restart
  so the snapshot is taken from a freshly saved world.

.EXAMPLE
  .\Register-RealmTasks.ps1 -ConfigPath G:\RealmOps\realm-ops.json -WhatIf
  .\Register-RealmTasks.ps1 -ConfigPath G:\RealmOps\realm-ops.json -BackupTime 05:10 -ProtectConfig
  .\Register-RealmTasks.ps1 -Unregister
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Register')]
param(
    [Parameter(ParameterSetName = 'Register', Mandatory = $true)][string]$ConfigPath,
    [Parameter(ParameterSetName = 'Register')][ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')][string]$BackupTime = '04:20',
    [Parameter(ParameterSetName = 'Register')][ValidateRange(1, 60)][int]$MonitorEveryMinutes = 2,
    [Parameter(ParameterSetName = 'Register')][switch]$NoMonitor,
    [Parameter(ParameterSetName = 'Register')][switch]$NoBackup,
    [Parameter(ParameterSetName = 'Register')][switch]$ProtectConfig,
    # Registers even when ordinary accounts may change the scripts or config the SYSTEM tasks run. Unsafe on a shared machine.
    [Parameter(ParameterSetName = 'Register')][switch]$AllowWritableByUsers,
    [Parameter(ParameterSetName = 'Unregister', Mandatory = $true)][switch]$Unregister,
    # For the test suite only (ops/tests): skips the administrator check.
    [switch]$SkipElevationCheck
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')

$taskPath = '\Realm\'
$backupTask = 'Realm Nightly Backup'
$monitorTask = 'Realm Uptime Monitor'

function Test-OpsElevated {
    if (-not $script:OpsIsWindows) { return $false }
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not $WhatIfPreference -and -not $SkipElevationCheck -and -not (Test-OpsElevated)) { throw 'Run this in an elevated Windows PowerShell (Run as administrator).' }

if ($Unregister) {
    foreach ($t in @($backupTask, $monitorTask)) {
        $existing = Get-ScheduledTask -TaskPath $taskPath -TaskName $t -ErrorAction SilentlyContinue
        if (-not $existing) { Write-Host "  $taskPath$t is not registered."; continue }
        if ($PSCmdlet.ShouldProcess("$taskPath$t", 'Unregister scheduled task')) {
            Unregister-ScheduledTask -TaskPath $taskPath -TaskName $t -Confirm:$false
            Write-Host "  Removed $taskPath$t" -ForegroundColor Green
        }
    }
    return
}

$cfgFull = Get-OpsFullPath $ConfigPath
$cfg = Read-OpsConfig $cfgFull
$problems = @(Test-OpsConfig $cfg 'All')
if ($problems.Count -gt 0) { throw ("Fix the config first:`n  - " + ($problems -join "`n  - ")) }
$repoMarker = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'README.md'
if ((Test-Path -LiteralPath $repoMarker) -and (Test-OpsPathInside $cfgFull (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))) {
    throw "The config file is inside the repository folder. Keep it outside (for example G:\RealmOps\realm-ops.json) so the webhook URL is never committed."
}

# The tasks run as SYSTEM: anything an ordinary account can change here is a way to run code as SYSTEM.
if ($script:OpsIsWindows -and -not $AllowWritableByUsers) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $check = @($PSScriptRoot, (Split-Path -Parent $PSScriptRoot), $repoRoot)
    $check += @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.ps1' | ForEach-Object { $_.FullName })
    $rcExe = [string](Get-OpsProp $cfg 'backup.rcloneExe' '')
    if ($rcExe -and -not (Test-OpsPlaceholder $rcExe)) { $check += @($rcExe, (Split-Path -Parent $rcExe)) }
    if (-not $ProtectConfig) {
        $check += $cfgFull
        $rcConf = [string](Get-OpsProp $cfg 'backup.rcloneConfig' '')
        if ($rcConf -and -not (Test-OpsPlaceholder $rcConf)) { $check += $rcConf }
    }
    $aclProblems = @(Get-OpsWritableByUsersProblems $check)
    if ($aclProblems.Count -gt 0) {
        throw ("The scheduled tasks run as SYSTEM, but ordinary accounts can change what they would run:`n  - " + ($aclProblems -join "`n  - ") +
            "`nLimit the repository to Administrators and SYSTEM (elevated):`n  icacls `"$repoRoot`" /inheritance:r /grant:r `"*S-1-5-32-544:(OI)(CI)F`" `"*S-1-5-18:(OI)(CI)F`" `"*S-1-5-32-545:(OI)(CI)RX`" /T" +
            "`nand use -ProtectConfig for the config files (lock the rclone folder the same way). See ops\README.md. -AllowWritableByUsers skips this check.")
    }
}

$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$common = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File'

function New-OpsTaskArgs([string]$Script) {
    return ('{0} "{1}" -ConfigPath "{2}"' -f $common, (Join-Path $PSScriptRoot $Script), $cfgFull)
}

$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest

if (-not $NoBackup) {
    $at = [datetime]::ParseExact($BackupTime, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    $action = New-ScheduledTaskAction -Execute $ps -Argument (New-OpsTaskArgs 'Backup-RealmOffsite.ps1') -WorkingDirectory $PSScriptRoot
    $trigger = New-ScheduledTaskTrigger -Daily -At $at
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 3) -MultipleInstances IgnoreNew `
        -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 15) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    if ($PSCmdlet.ShouldProcess("$taskPath$backupTask", "Register daily at $BackupTime (local time) as SYSTEM")) {
        Register-ScheduledTask -TaskPath $taskPath -TaskName $backupTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
            -Description 'Realm: snapshot, verify and copy every server''s world offsite (ops\scripts\Backup-RealmOffsite.ps1).' -Force | Out-Null
        Write-Host "  Registered $taskPath$backupTask (daily $BackupTime)" -ForegroundColor Green
    }
}

if (-not $NoMonitor) {
    $action = New-ScheduledTaskAction -Execute $ps -Argument (New-OpsTaskArgs 'Watch-RealmUptime.ps1') -WorkingDirectory $PSScriptRoot
    # No RepetitionDuration: on Windows Server 2016 and later that means "indefinitely". (Passing TimeSpan.MaxValue
    # fails there.) UNVERIFIED on Windows Server 2012 R2, which may require a duration.
    $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).Date.AddMinutes(1)) -RepetitionInterval (New-TimeSpan -Minutes $MonitorEveryMinutes)
    $boot = New-ScheduledTaskTrigger -AtStartup
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    if ($PSCmdlet.ShouldProcess("$taskPath$monitorTask", "Register every $MonitorEveryMinutes min as SYSTEM")) {
        Register-ScheduledTask -TaskPath $taskPath -TaskName $monitorTask -Action $action -Trigger @($trigger, $boot) -Settings $settings -Principal $principal `
            -Description 'Realm: A2S/ping/process check of every server, alerts to Discord (ops\scripts\Watch-RealmUptime.ps1).' -Force | Out-Null
        Write-Host "  Registered $taskPath$monitorTask (every $MonitorEveryMinutes min)" -ForegroundColor Green
    }
}

if ($ProtectConfig) {
    $files = @($cfgFull)
    $rc = [string](Get-OpsProp $cfg 'backup.rcloneConfig' '')
    if ($rc -and -not (Test-OpsPlaceholder $rc) -and (Test-Path -LiteralPath $rc)) { $files += (Get-OpsFullPath $rc) }
    foreach ($f in $files) {
        # SIDs, not names, so this works on non-English Windows: S-1-5-18 SYSTEM, S-1-5-32-544 Administrators.
        if ($PSCmdlet.ShouldProcess($f, 'Restrict access to SYSTEM and Administrators')) {
            $r = Invoke-OpsProcess (Join-Path $env:SystemRoot 'System32\icacls.exe') @($f, '/inheritance:r', '/grant:r', '*S-1-5-18:(F)', '*S-1-5-32-544:(F)') 60
            if ($r.ExitCode -ne 0) { throw "icacls failed on $f : $($r.Output)" }
            Write-Host "  Locked down $f" -ForegroundColor Green
        }
    }
}

Write-Host ''
Write-Host 'Check them in Task Scheduler > Task Scheduler Library > Realm, or run one now:'
Write-Host "  Start-ScheduledTask -TaskPath '$taskPath' -TaskName '$backupTask'"
