<#
.SYNOPSIS
  Uptime monitor for the Realm servers: checks each server's Steam query port (A2S), its ping port and process,
  and posts to a Discord webhook when a server goes down, stays down, or comes back. Also warns when the newest
  offsite backup is too old.

.DESCRIPTION
  One run checks every server in the ops config once and exits (Register-RealmTasks.ps1 runs it every few minutes).
  -Loop keeps it running in a console instead.

  Checks per server:
    A2S_INFO over UDP to host:queryPort. queryPort is the server's steamAuthPort (default 27015): the game
      passes it to Steam's GameServer.Init as the query port (docs/join-and-scale.md 3.1). The game port does
      NOT answer A2S. A2S always reports the name "Another ROK Server" and max players 0, so names come from
      the config and the player count is shown as approximate.
    TCP connect to host:pingPort (default = gamePort, 7350): the game's ping listener, which ignores bindIP.
    Process: ROK.exe or Server.exe running from the server's root folder (only when the folder is on this machine).
  Verdict: up (A2S answers), degraded (game alive but A2S silent), down (nothing answers).

  Alerts go out after monitor.failuresBeforeAlert failed runs in a row (default 2), then every
  monitor.remindEveryMinutes (default 60) while the problem lasts, and once when the server recovers.
  The webhook URL is read from the local config file only. It is never written to logs or screens.

.EXAMPLE
  .\Watch-RealmUptime.ps1 -ConfigPath G:\RealmOps\realm-ops.json
  .\Watch-RealmUptime.ps1 -ConfigPath G:\RealmOps\realm-ops.json -SendTest
  .\Watch-RealmUptime.ps1 -ConfigPath G:\RealmOps\realm-ops.json -Loop -IntervalSeconds 60
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [switch]$Loop,
    [ValidateRange(15, 3600)][int]$IntervalSeconds = 60,
    # Posts one test message to the webhook and exits.
    [switch]$SendTest,
    # Checks and prints, but never posts to Discord and never changes the alert state.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')

try {
    $cfg = Read-OpsConfig $ConfigPath
    Assert-OpsConfig $cfg 'Monitor'
    $opsRoot = Assert-OpsSafeDir ([string]$cfg.opsRoot) 'opsRoot'
    Start-OpsLog $opsRoot 'monitor' | Out-Null
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 2
}

$hook = [string](Get-OpsProp $cfg 'monitor.discordWebhookUrl' '')
$hasHook = Test-OpsDiscordWebhookUrl $hook
$mention = [string](Get-OpsProp $cfg 'monitor.mention' '')
$timeout = [int](Get-OpsProp $cfg 'monitor.timeoutMs' 2000)
$failN = [int](Get-OpsProp $cfg 'monitor.failuresBeforeAlert' 2)
$remind = [int](Get-OpsProp $cfg 'monitor.remindEveryMinutes' 60)
$maxBackupH = [double](Get-OpsProp $cfg 'monitor.backupMaxAgeHours' 26)
$logDays = [int](Get-OpsProp $cfg 'monitor.logRetentionDays' 30)
$statePath = Join-Path (Join-Path $opsRoot 'state') 'monitor.json'
$offsiteConfigured = -not (Test-OpsPlaceholder (Get-OpsProp $cfg 'backup.remote'))

if ($SendTest) {
    if (-not $hasHook) { Write-OpsLog 'monitor.discordWebhookUrl is not set to a Discord webhook URL.' 'ERROR'; exit 2 }
    $names = @(Get-OpsServers $cfg | ForEach-Object { $_.Name }) -join ', '
    $payload = New-OpsDiscordPayload 'Realm Watch is set up' ("This channel will hear when a Realm server falls silent or returns. Watching: {0}." -f $names) 'info' @(
        @{ name = 'Machine'; value = [Environment]::MachineName; inline = $true })
    $r = Send-OpsDiscordMessage $hook $payload
    if ($r.Ok) { Write-OpsLog "Test message posted (HTTP $($r.Status))." 'OK'; exit 0 }
    Write-OpsLog "Test message failed: $($r.Error)" 'ERROR'
    exit 1
}

function Get-TargetState($All, [string]$Key) {
    if ($null -eq $All) { return $null }
    return Get-OpsProp $All $Key $null
}

function Invoke-WatchOnce {
    $now = [datetime]::UtcNow
    $all = $null
    try { $all = Get-OpsProp (Read-OpsJsonFile $statePath) 'targets' $null } catch { Write-OpsLog "Monitor state unreadable, starting fresh: $($_.Exception.Message)" 'WARN' }
    $newTargets = [ordered]@{}
    if ($null -ne $all) { foreach ($p in $all.PSObject.Properties) { $newTargets[$p.Name] = $p.Value } }
    $events = New-Object System.Collections.Generic.List[object]

    foreach ($s in @(Get-OpsServers $cfg | Where-Object { $_.Monitor })) {
        $a2s = Invoke-OpsA2SQuery $s.Host $s.QueryPort $timeout
        $tcp = $false
        if ($s.TcpCheck) { $tcp = Test-OpsTcpPort $s.Host $s.PingPort $timeout }
        $procState = 'n/a'
        $procRunning = $false
        if ($s.Root -and -not (Test-OpsPlaceholder $s.Root) -and (Test-Path -LiteralPath $s.Root -PathType Container)) {
            $procRunning = @(Get-OpsServerProcesses $s.Root).Count -gt 0
            $procState = 'stopped'
            if ($procRunning) { $procState = 'running' }
        }
        $verdict = Get-OpsServerVerdict $a2s.Ok $tcp $procRunning
        $a2sText = 'no answer'
        if ($a2s.Ok) { $a2sText = ('{0} ms, ~{1} players' -f $a2s.RttMs, $a2s.Info.Players) } elseif ($a2s.Error) { $a2sText = $a2s.Error }
        $tcpText = 'not checked'
        if ($s.TcpCheck) { if ($tcp) { $tcpText = 'open' } else { $tcpText = 'closed' } }
        $level = 'OK'
        if ($verdict -eq 'degraded') { $level = 'WARN' } elseif ($verdict -eq 'down') { $level = 'ERROR' }
        Write-OpsLog ("{0}: {1} | A2S {2}:{3} {4} | ping {2}:{5}/TCP {6} | process {7}" -f $s.Name, $verdict.ToUpper(), $s.Host, $s.QueryPort, $a2sText, $s.PingPort, $tcpText, $procState) $level

        $key = 'server:' + $s.Slug
        $prev = Get-TargetState $all $key
        $d = Get-OpsAlertDecision $prev $verdict $now $failN $remind
        $newTargets[$key] = $d.State
        if ($d.Send) {
            $checks = "A2S {0}:{1}/UDP: {2}`nPing {0}:{3}/TCP: {4}`nProcess: {5}" -f $s.Host, $s.QueryPort, $a2sText, $s.PingPort, $tcpText, $procState
            $title = ''; $desc = ''; $kind = $d.Kind
            switch ($d.Kind) {
                'down' { $title = "$($s.Name) is DOWN"; $desc = 'No answer from the Steam query port, the game ping port or the process. Players cannot join.' }
                'degraded' { $title = "$($s.Name) is DEGRADED"; $desc = "The game is running but Steam's query port does not answer. The Realm app shows it offline. If the server's Steam login failed, players may be unable to join (UNVERIFIED). Look for 'Steam game server started' in the server log, or restart it at a quiet moment." }
                'recovered' { $title = "$($s.Name) is back up"; $desc = ('Answering again after {0}.' -f (Format-OpsDuration $d.DownFor)) }
                'reminder' { $title = ("$($s.Name) is still {0}" -f ([string]$d.State.Status).ToUpper()); $desc = ('Problem for {0} now.' -f (Format-OpsDuration $d.DownFor)) }
            }
            $events.Add(@{ Key = $key; Prev = $prev; Title = $title; Desc = $desc; Kind = $kind; Fields = @(
                @{ name = 'Checks'; value = $checks; inline = $false },
                @{ name = 'Machine'; value = [Environment]::MachineName; inline = $true }) })
        }
    }

    if ($maxBackupH -gt 0) {
        foreach ($s in @(Get-OpsServers $cfg | Where-Object { $_.Backup })) {
            $bs = Read-OpsJsonFile (Join-Path (Join-Path $opsRoot 'state') ('backup-{0}.json' -f $s.Slug))
            $field = 'lastLocal'
            if ($offsiteConfigured) { $field = 'lastOffsite' }
            $last = Get-OpsProp $bs $field $null
            $age = $null
            if ($last) { $age = $now - ([datetime]$last).ToUniversalTime() }
            $fresh = ($null -ne $age) -and ($age.TotalHours -le $maxBackupH)
            $status = 'down'
            if ($fresh) { $status = 'up' }
            $key = 'backup:' + $s.Slug
            $prev = Get-TargetState $all $key
            $d = Get-OpsAlertDecision $prev $status $now 1 1440
            $newTargets[$key] = $d.State
            $ageText = 'never'
            if ($null -ne $age) { $ageText = (Format-OpsDuration $age) + ' ago' }
            if (-not $fresh) { Write-OpsLog ("{0}: newest {1} backup {2} (limit {3} h)" -f $s.Name, $(if ($offsiteConfigured) { 'offsite' } else { 'local' }), $ageText, $maxBackupH) 'WARN' }
            if ($d.Send) {
                $kind = 'stale'; $title = "No fresh backup for $($s.Name)"
                $desc = ('The newest {0} backup is {1} (limit {2} h). Check the "Realm Nightly Backup" task and {3}.' -f $(if ($offsiteConfigured) { 'offsite' } else { 'local' }), $ageText, $maxBackupH, 'logs\backup-*.log')
                if ($d.Kind -eq 'recovered') { $kind = 'recovered'; $title = "Backups for $($s.Name) are current again"; $desc = "Newest backup: $ageText." }
                $lastErr = Get-OpsProp $bs 'lastError' $null
                $fields = @(@{ name = 'Machine'; value = [Environment]::MachineName; inline = $true })
                if ($lastErr -and $kind -ne 'recovered') { $fields += @{ name = 'Last error'; value = [string]$lastErr; inline = $false } }
                $events.Add(@{ Key = $key; Prev = $prev; Title = $title; Desc = $desc; Kind = $kind; Fields = $fields })
            }
        }
    }

    foreach ($e in $events) {
        if ($DryRun) { Write-OpsLog ("[dry run] would post: {0}" -f $e.Title); continue }
        if (-not $hasHook) { Write-OpsLog ("Alert (no webhook configured): {0}" -f $e.Title) 'WARN'; continue }
        $m = ''
        if ($e.Kind -eq 'down' -or $e.Kind -eq 'reminder') { $m = $mention }
        $r = Send-OpsDiscordMessage $hook (New-OpsDiscordPayload $e.Title $e.Desc $e.Kind $e.Fields $m)
        if ($r.Ok) { Write-OpsLog ("Posted to Discord: {0}" -f $e.Title) 'OK' }
        else {
            Write-OpsLog ("Discord post failed ({0}); will retry next run: {1}" -f $r.Error, $e.Title) 'WARN'
            # Keep the previous alert flags so the next run tries again instead of believing it was delivered.
            $st = $newTargets[$e.Key]
            if ($e.Kind -ne 'recovered') {
                $st.Alerted = [bool](Get-OpsProp $e.Prev 'Alerted' $false)
                $st.AlertedStatus = Get-OpsProp $e.Prev 'AlertedStatus' $null
                $st.LastAlert = Get-OpsProp $e.Prev 'LastAlert' $null
            } else {
                $newTargets[$e.Key] = $e.Prev
            }
        }
    }

    if (-not $DryRun) { Write-OpsJsonFile $statePath ([ordered]@{ lastRun = $now.ToString('o'); targets = $newTargets }) }

    if ($logDays -gt 0) {
        $cut = [datetime]::UtcNow.AddDays(-$logDays)
        Get-ChildItem -LiteralPath (Join-Path $opsRoot 'logs') -File -Filter '*.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -lt $cut } | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

do {
    try { Invoke-WatchOnce } catch { Write-OpsLog ("Monitor run failed: {0}" -f $_.Exception.Message) 'ERROR'; if (-not $Loop) { exit 1 } }
    if ($Loop) { Start-Sleep -Seconds $IntervalSeconds }
} while ($Loop)
exit 0
