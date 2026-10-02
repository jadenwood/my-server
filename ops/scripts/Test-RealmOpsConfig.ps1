<#
.SYNOPSIS
  Checks the Realm ops config and everything it points at, without changing anything outside opsRoot.

.DESCRIPTION
  Reports, one line each:
    - config problems (placeholders left in, bad ports, bad webhook URL shape),
    - folders: opsRoot, backup.localDir, masterServerDir, each server root (exists? Realm instance marker?),
    - SteamCMD present, master build id,
    - rclone present and its version; the remote reachable (rclone lsjson on backup.remote),
    - each server's A2S and ping port right now,
    - last backup per server, from the state files.
  -SendTest also posts one test message to the Discord webhook.
  Exit code 0 when nothing is marked FAIL.

.EXAMPLE
  .\Test-RealmOpsConfig.ps1 -ConfigPath G:\RealmOps\realm-ops.json
  .\Test-RealmOpsConfig.ps1 -ConfigPath G:\RealmOps\realm-ops.json -SendTest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [switch]$SendTest,
    [switch]$SkipNetwork
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')

$fails = 0
function Out-Check([string]$State, [string]$Text) {
    $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; INFO = 'Gray' }[$State]
    if ($State -eq 'FAIL') { $script:fails++ }
    Write-Host ('  [{0}] {1}' -f $State, (Protect-OpsText $Text)) -ForegroundColor $color
}

$cfg = Read-OpsConfig $ConfigPath
Write-Host "Config: $ConfigPath"
$problems = @(Test-OpsConfig $cfg 'All')
if ($problems.Count -eq 0) { Out-Check 'PASS' 'Config fields look complete.' } else { foreach ($p in $problems) { Out-Check 'FAIL' $p } }

$opsRoot = [string](Get-OpsProp $cfg 'opsRoot' '')
foreach ($pair in @(@('opsRoot', $opsRoot), @('backup.localDir', [string](Get-OpsProp $cfg 'backup.localDir' '')))) {
    if (Test-OpsPlaceholder $pair[1]) { continue }
    $why = Get-OpsUnsafeDirReason (Get-OpsFullPath $pair[1])
    if ($why) { Out-Check 'FAIL' "$($pair[0]): $why" }
    elseif (Test-Path -LiteralPath $pair[1] -PathType Container) { Out-Check 'PASS' "$($pair[0]) exists: $($pair[1])" }
    else { Out-Check 'INFO' "$($pair[0]) will be created: $($pair[1])" }
}

$master = [string](Get-OpsProp $cfg 'masterServerDir' '')
if (-not (Test-OpsPlaceholder $master)) {
    $why = Get-OpsMasterDirProblem $master $cfg
    if ($why) { Out-Check 'FAIL' "masterServerDir: $why" }
    elseif (Test-OpsServerFolder $master) {
        $mf = Get-OpsAppManifest $master
        $b = '?'
        if ($mf) { $b = $mf.BuildId }
        Out-Check 'PASS' "Master server files present (build $b): $master"
    } else { Out-Check 'WARN' "No server files in $master yet. Run Install-RealmServer.ps1." }
}
$sc = Join-Path ([string](Get-OpsProp $cfg 'steamcmdDir' '')) 'steamcmd.exe'
if (Test-Path -LiteralPath $sc) { Out-Check 'PASS' "SteamCMD present: $sc" } else { Out-Check 'WARN' "SteamCMD not installed yet ($sc)." }

foreach ($s in @(Get-OpsServers $cfg)) {
    if ($s.Root -and (Test-Path -LiteralPath $s.Root -PathType Container)) {
        if (Test-Path -LiteralPath (Join-Path $s.Root $script:OpsInstanceMarker)) { Out-Check 'PASS' "$($s.Name): instance folder $($s.Root)" }
        else { Out-Check 'WARN' "$($s.Name): $($s.Root) has no $script:OpsInstanceMarker marker (restore will refuse it)." }
    } else { Out-Check 'WARN' "$($s.Name): root folder $($s.Root) not found on this machine." }
    $bs = $null
    if (-not (Test-OpsPlaceholder $opsRoot)) { $bs = Read-OpsJsonFile (Join-Path (Join-Path $opsRoot 'state') ('backup-{0}.json' -f $s.Slug)) }
    $last = Get-OpsProp $bs 'lastSuccess' $null
    if ($last) { Out-Check 'INFO' ("{0}: last good backup {1} ago; offsite {2}" -f $s.Name, (Format-OpsDuration ([datetime]::UtcNow - ([datetime]$last).ToUniversalTime())), (Get-OpsProp $bs 'lastOffsite' 'never')) }
    else { Out-Check 'INFO' "$($s.Name): no backup recorded yet." }
    if (-not $SkipNetwork) {
        $a = Invoke-OpsA2SQuery $s.Host $s.QueryPort ([int](Get-OpsProp $cfg 'monitor.timeoutMs' 2000))
        if ($a.Ok) { Out-Check 'PASS' ("{0}: A2S {1}:{2} answers ({3} ms, name '{4}', ~{5} players)" -f $s.Name, $s.Host, $s.QueryPort, $a.RttMs, $a.Info.Name, $a.Info.Players) }
        else { Out-Check 'WARN' ("{0}: A2S {1}:{2}: {3}" -f $s.Name, $s.Host, $s.QueryPort, $a.Error) }
        if ($s.TcpCheck) {
            if (Test-OpsTcpPort $s.Host $s.PingPort 2000) { Out-Check 'PASS' "$($s.Name): ping port $($s.Host):$($s.PingPort)/TCP open" }
            else { Out-Check 'WARN' "$($s.Name): ping port $($s.Host):$($s.PingPort)/TCP closed" }
        }
    }
}

$remote = Get-OpsProp $cfg 'backup.remote'
if (Test-OpsPlaceholder $remote) { Out-Check 'WARN' 'backup.remote not set: backups will not leave this machine.' }
else {
    $exe = [string](Get-OpsProp $cfg 'backup.rcloneExe' '')
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { Out-Check 'FAIL' "rclone not found at $exe" }
    else {
        $v = Invoke-OpsProcess $exe @('version') 60
        Out-Check 'PASS' ('rclone: ' + (($v.StdOut -split "`n")[0]).Trim())
        if (-not $SkipNetwork) {
            try {
                $r = Invoke-OpsRclone $cfg @('lsjson', [string]$remote, '--max-depth', '1') 120
                if ($r.ExitCode -eq 0) { Out-Check 'PASS' "Remote reachable: $remote" }
                elseif ($r.Output -match '(?i)directory not found') { Out-Check 'PASS' "Remote reachable, folder not created yet: $remote" }
                else { Out-Check 'FAIL' ("Remote {0}: rclone exit {1}: {2}" -f $remote, $r.ExitCode, (($r.Output -split "`n") | Select-Object -Last 3) -join ' ') }
            } catch { Out-Check 'FAIL' $_.Exception.Message }
        }
    }
}

$hook = [string](Get-OpsProp $cfg 'monitor.discordWebhookUrl' '')
if (Test-OpsDiscordWebhookUrl $hook) { Out-Check 'PASS' 'Discord webhook URL has the right shape.' }
else { Out-Check 'WARN' 'No Discord webhook set: the monitor will only write logs.' }
if ($SendTest -and (Test-OpsDiscordWebhookUrl $hook)) {
    $r = Send-OpsDiscordMessage $hook (New-OpsDiscordPayload 'Realm ops check' 'Test-RealmOpsConfig.ps1 can reach this channel.' 'info')
    if ($r.Ok) { Out-Check 'PASS' "Test message posted (HTTP $($r.Status))." } else { Out-Check 'FAIL' "Test message failed: $($r.Error)" }
}

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host 'No failures.' -ForegroundColor Green
exit 0
