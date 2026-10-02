# Test suite for ops/scripts. Runs on PowerShell 7 (Linux, macOS or Windows) with Node 18+ on PATH.
#   pwsh -NoProfile -File ops/tests/Run-OpsTests.ps1 [-Rclone <path to rclone>]
# With -Rclone, the backup/restore tests talk S3 to a local "rclone serve s3" endpoint (a real S3 protocol
# round trip, no internet). Without it those tests are reported as SKIPPED.
# Everything is created under a temp folder and removed at the end (-Keep keeps it).
param(
    [string]$Rclone = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$scripts = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts'
. (Join-Path $scripts 'OpsCommon.ps1')
$pwshExe = (Get-Process -Id $PID).Path
$node = (Get-Command node -ErrorAction Stop).Source
$fakes = Join-Path $PSScriptRoot 'fakes.mjs'
$work = Join-Path ([IO.Path]::GetTempPath()) ('realm-ops-tests-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

$script:pass = 0; $script:fail = 0; $script:skip = 0
$script:procs = New-Object System.Collections.Generic.List[object]

function Ok([bool]$Cond, [string]$Name, [string]$Detail = '') {
    if ($Cond) { $script:pass++; Write-Host "  ok   $Name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL $Name $Detail" -ForegroundColor Red }
}
function Eq($Actual, $Expected, [string]$Name) { Ok ($Actual -eq $Expected) $Name ("(expected '{0}', got '{1}')" -f $Expected, $Actual) }
function Skip([string]$Name, [string]$Why) { $script:skip++; Write-Host "  skip $Name ($Why)" -ForegroundColor Yellow }
function Section([string]$Name) { Write-Host ''; Write-Host "== $Name" -ForegroundColor Cyan }
function Throws([scriptblock]$Block, [string]$Pattern, [string]$Name) {
    $msg = $null
    try { & $Block | Out-Null } catch { $msg = $_.Exception.Message }
    Ok ($null -ne $msg -and $msg -match $Pattern) $Name "(message: $msg)"
}
function Get-FreePort([switch]$Udp) {
    if ($Udp) { $u = New-Object System.Net.Sockets.UdpClient(0); $p = $u.Client.LocalEndPoint.Port; $u.Close(); return $p }
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}
function Start-Bg([string]$Exe, [string[]]$Arguments, [string]$Ready = 'ready', [int]$WaitMs = 15000) {
    $id = [Guid]::NewGuid().ToString('N').Substring(0, 6)
    $out = Join-Path $work "bg-$id.out"; $err = Join-Path $work "bg-$id.err"
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $script:procs.Add($p)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $WaitMs) {
        $txt = ''
        foreach ($f in @($out, $err)) { if (Test-Path $f) { $txt += [IO.File]::ReadAllText($f) } }
        if ($txt -match $Ready) { return $p }
        if ($p.HasExited) { throw "Background process exited early: $Exe $Arguments`n$txt" }
        Start-Sleep -Milliseconds 100
    }
    throw "Background process not ready: $Exe $Arguments"
}
function Invoke-Script([string]$Name, [string[]]$Arguments) {
    $out = & $pwshExe -NoProfile -NonInteractive -File (Join-Path $scripts $Name) @Arguments *>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
}
function Write-Json([string]$Path, $Obj) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Obj -Depth 8)) }
function New-FakeServerRoot([string]$Root) {
    foreach ($d in @('Saves/World', 'oxide/data', 'oxide/config', 'oxide/plugins', 'oxide/logs', 'Configuration', 'Mods', 'ROK_Data/Managed')) { New-Item -ItemType Directory -Force -Path (Join-Path $Root $d) | Out-Null }
    [IO.File]::WriteAllText((Join-Path $Root 'Saves/World/world.sav'), 'world-v1 ' + ('#' * 5000))
    [IO.File]::WriteAllText((Join-Path $Root 'Saves/World/players.dat'), 'players-v1')
    [IO.File]::WriteAllText((Join-Path $Root 'oxide/data/RealmChronicle.json'), '[{"id":1,"type":"coronation","title":"Varrow takes the crown of Ostreval"}]')
    [IO.File]::WriteAllText((Join-Path $Root 'oxide/data/RealmHouses.json'), '{"houses":["Varrow","Ashgrove","Corvane"]}')
    [IO.File]::WriteAllText((Join-Path $Root 'oxide/config/CrownAndConsequences.json'), '{"TaxCap":10}')
    [IO.File]::WriteAllText((Join-Path $Root 'oxide/plugins/RealmHouses.cs'), '// plugin')
    [IO.File]::WriteAllText((Join-Path $Root 'oxide/logs/oxide.log'), 'log noise')
    [IO.File]::WriteAllText((Join-Path $Root 'Configuration/ServerSettings.cfg'), "portNumber = '7350'`nmaxPlayers = '120'")
    [IO.File]::WriteAllText((Join-Path $Root 'Mods/Atmosphere.cfg'), "Fog = '0.4'")
    [IO.File]::WriteAllText((Join-Path $Root 'Mods/Atmosphere.defaults.cfg'), "Fog = '0.1'")
    [IO.File]::WriteAllText((Join-Path $Root 'Mods/texture.bin'), 'game asset')
    [IO.File]::WriteAllText((Join-Path $Root 'ROK_Data/Managed/Assembly-CSharp.dll'), 'binary')
    [IO.File]::WriteAllText((Join-Path $Root '.realm-test-copy'), 'marker')
}

try {
    # ------------------------------------------------------------------ pure helpers
    Section 'text, names, stamps'
    $hook = 'https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz_0123456789-abcdefghijklmnop'
    $red = Protect-OpsText "posting to $hook now; secret_access_key = abc123"
    Ok ($red -notmatch 'AbCdEf' -and $red -match '/api/webhooks/123456789012345678/<redacted>') 'webhook token redacted'
    Ok ($red -notmatch 'abc123') 'S3 secret redacted'
    Eq (Get-OpsSlug 'Realm 1') 'realm-1' 'slug of "Realm 1"'
    Eq (Get-OpsSlug '  Ostreval: Varrow Keep!! ') 'ostreval-varrow-keep' 'slug strips punctuation'
    Throws { Get-OpsSlug '***' } 'no letters' 'slug refuses empty result'
    $when = [datetime]::new(2026, 10, 2, 4, 20, 5, [DateTimeKind]::Utc)
    Eq (Get-OpsUtcStamp $when) '20261002-042005Z' 'UTC stamp format'
    Eq ((ConvertFrom-OpsUtcStamp 'realm-realm-1-20261002-042005Z.zip').ToString('o')) $when.ToString('o') 'stamp parses back from a file name'
    Eq (ConvertFrom-OpsUtcStamp 'nope.zip') $null 'bad stamp is null'
    Eq (Format-OpsDuration ([TimeSpan]::FromMinutes(135))) '2 h 15 min' 'duration format'

    Section 'path guards (Windows path strings)'
    Ok ($null -ne (Get-OpsUnsafeDirReason 'G:\')) 'drive root refused'
    Ok ($null -ne (Get-OpsUnsafeDirReason 'C:\Windows\Temp\x')) 'C:\Windows refused'
    Ok ($null -ne (Get-OpsUnsafeDirReason 'C:\Program Files (x86)\Steam\x')) 'Program Files refused'
    Ok ($null -ne (Get-OpsUnsafeDirReason 'G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server\backups')) 'Steam library refused for ops data'
    Ok ($null -ne (Get-OpsUnsafeDirReason 'C:\Users\owner\Desktop')) 'user profile refused'
    Eq (Get-OpsUnsafeDirReason 'G:\RealmOps\backups') $null 'G:\RealmOps allowed'
    Eq (Get-OpsUnsafeDirReason 'C:\RealmOps') $null 'C:\RealmOps allowed (single-disk VPS)'
    Ok ($null -ne (Get-OpsUnsafeDirReason '/etc/realm')) 'POSIX system folder refused'

    Section 'master folder guard'
    $m1 = Join-Path $work 'master'; New-Item -ItemType Directory -Path $m1 | Out-Null
    Eq (Get-OpsMasterDirProblem $m1) $null 'plain folder accepted as master'
    $inst = Join-Path $work 'inst-marker'; New-Item -ItemType Directory -Path $inst | Out-Null; Set-Content (Join-Path $inst '.realm-test-copy') 'x'
    Ok ((Get-OpsMasterDirProblem $inst) -match 'Realm server instance') 'instance folder refused as master'
    $cfgOverlap = [pscustomobject]@{ servers = @([pscustomobject]@{ name = 'Realm 1'; root = (Join-Path $m1 'inner') }) }
    Ok ((Get-OpsMasterDirProblem $m1 $cfgOverlap) -match 'overlaps') 'master overlapping an instance refused'

    Section 'process runner and Windows argument quoting'
    $tricky = @('plain', 'two words', 'q"uote', 'trail\', '', 'C:\Path With Space\', 'a\\"b', 'x y\\')
    $r = Invoke-OpsProcess $node (@('-e', 'console.log(JSON.stringify(process.argv.slice(1)))') + $tricky) 30
    $got = @($r.StdOut | ConvertFrom-Json)
    Eq $got.Count $tricky.Count 'argument count survives quoting'
    $allSame = $true
    for ($i = 0; $i -lt $tricky.Count; $i++) { if ($got[$i] -ne $tricky[$i]) { $allSame = $false; Write-Host "     arg $i : '$($tricky[$i])' -> '$($got[$i])'" } }
    Ok $allSame 'every tricky argument arrives unchanged'
    $r = Invoke-OpsProcess $node @('-e', 'console.error("to stderr"); console.log("to stdout"); process.exit(3)') 30
    Ok ($r.ExitCode -eq 3 -and $r.StdErr -match 'to stderr' -and $r.StdOut -match 'to stdout') 'stderr captured, exit code kept, no PowerShell error'
    $r = Invoke-OpsProcess $node @('-e', 'setTimeout(()=>{}, 10000)') 1
    Ok ($r.TimedOut -and $r.ExitCode -eq -1) 'timeout kills the process'

    Section 'SteamCMD output and app manifest'
    $okOut = "Steam Console Client (c) Valve Corporation`nLogging in user 'anonymous' to Steam Public...OK`n Update state (0x61) downloading, progress: 50.00`nSuccess! App '381690' fully installed."
    $r1 = Get-OpsSteamCmdResult $okOut 0
    Ok ($r1.Ok -and $r1.State -eq 'installed') 'fully installed recognised'
    $r2 = Get-OpsSteamCmdResult "Success! App '381690' already up to date." 0
    Ok ($r2.Ok -and $r2.State -eq 'up-to-date') 'already up to date recognised'
    $r3 = Get-OpsSteamCmdResult "ERROR! Failed to install app '381690' (No subscription)" 8
    Ok ((-not $r3.Ok) -and $r3.Message -match 'No subscription') 'No subscription is an error'
    $r4 = Get-OpsSteamCmdResult "Redirecting stderr to 'logs\stderr.txt'" 0
    Ok (-not $r4.Ok) 'no success line means failure even with exit 0'
    Eq ((Get-OpsSteamCmdArgs 'G:\M' -Validate) -join ' ') "+@ShutdownOnFailedCommand 1 +@NoPromptForPassword 1 +force_install_dir G:\M +login anonymous +app_update 381690 validate +quit" 'SteamCMD arguments: install dir before login, anonymous, 381690'
    $acf = "`"AppState`"`n{`n`t`"appid`"`t`t`"381690`"`n`t`"StateFlags`"`t`t`"4`"`n`t`"buildid`"`t`t`"1234567`"`n}"
    $mf = ConvertFrom-OpsAcf $acf
    Ok ($mf.AppId -eq '381690' -and $mf.BuildId -eq '1234567' -and $mf.StateFlags -eq '4') 'appmanifest parsed'

    Section 'A2S packets'
    $req = Get-OpsA2SRequest
    Eq ([BitConverter]::ToString($req)) 'FF-FF-FF-FF-54-53-6F-75-72-63-65-20-45-6E-67-69-6E-65-20-51-75-65-72-79-00' 'A2S_INFO request bytes'
    Eq (Get-OpsA2SRequest ([byte[]](1, 2, 3, 4))).Length 29 'request with challenge is 29 bytes'
    $ch = ConvertFrom-OpsA2SResponse ([byte[]](0xFF, 0xFF, 0xFF, 0xFF, 0x41, 9, 8, 7, 6))
    Ok ($ch.Type -eq 'challenge' -and ($ch.Challenge -join ',') -eq '9,8,7,6') 'challenge parsed'
    Throws { ConvertFrom-OpsA2SResponse ([byte[]](0xFF, 0xFF, 0xFF, 0xFF, 0x49, 17, 65)) } 'unterminated|truncated' 'truncated info rejected'
    Throws { ConvertFrom-OpsA2SResponse ([byte[]](0xFE, 0xFF, 0xFF, 0xFF, 1, 2, 3)) } 'split' 'split packet rejected'

    Section 'A2S and TCP against local fakes'
    $pInfo = Get-FreePort -Udp; $pChal = Get-FreePort -Udp; $pGarb = Get-FreePort -Udp; $pSilent = Get-FreePort -Udp; $pTcp = Get-FreePort
    Start-Bg $node @($fakes, 'a2s', $pInfo, 'info') | Out-Null
    Start-Bg $node @($fakes, 'a2s', $pChal, 'challenge') | Out-Null
    Start-Bg $node @($fakes, 'a2s', $pGarb, 'garbage') | Out-Null
    Start-Bg $node @($fakes, 'tcp', $pTcp) | Out-Null
    $q = Invoke-OpsA2SQuery '127.0.0.1' $pInfo 1500
    Ok ($q.Ok -and $q.Info.Name -eq 'Another ROK Server' -and $q.Info.Players -eq 7 -and $q.Info.MaxPlayers -eq 0 -and $q.Info.Version -eq '1.0.0.0') 'A2S info from a RoK-like server' $q.Error
    $q = Invoke-OpsA2SQuery '127.0.0.1' $pChal 1500
    Ok ($q.Ok -and $q.Info.Players -eq 7) 'A2S challenge answered and retried' $q.Error
    $q = Invoke-OpsA2SQuery '127.0.0.1' $pGarb 1500
    Ok ((-not $q.Ok) -and $q.Error -match 'Unexpected A2S reply') 'garbage reply reported' $q.Error
    $q = Invoke-OpsA2SQuery '127.0.0.1' $pSilent 800
    Ok ((-not $q.Ok) -and $q.Error -match 'ICMP|No A2S answer') 'closed UDP port reported, no exception' $q.Error
    $q = Invoke-OpsA2SQuery 'no-such-host.invalid' 27015 800
    Ok (-not $q.Ok) 'unresolvable host reported, no exception' $q.Error
    Ok (Test-OpsTcpPort '127.0.0.1' $pTcp 1000) 'TCP ping port open'
    Ok (-not (Test-OpsTcpPort '127.0.0.1' (Get-FreePort) 500)) 'TCP closed port'

    Section 'verdict and alert state machine'
    Eq (Get-OpsServerVerdict $true $false $false) 'up' 'A2S ok = up'
    Eq (Get-OpsServerVerdict $false $true $false) 'degraded' 'ping port only = degraded'
    Eq (Get-OpsServerVerdict $false $false $true) 'degraded' 'process only = degraded'
    Eq (Get-OpsServerVerdict $false $false $false) 'down' 'nothing = down'
    $t0 = [datetime]::new(2026, 10, 2, 12, 0, 0, [DateTimeKind]::Utc)
    $d = Get-OpsAlertDecision $null 'up' $t0 2 60; Ok (-not $d.Send) 'first run up: silent'
    $d = Get-OpsAlertDecision $d.State 'down' $t0.AddMinutes(2) 2 60; Ok (-not $d.Send) 'first failure: silent (debounce)'
    $d = Get-OpsAlertDecision $d.State 'down' $t0.AddMinutes(4) 2 60; Ok ($d.Send -and $d.Kind -eq 'down') 'second failure: DOWN alert'
    $d = Get-OpsAlertDecision $d.State 'down' $t0.AddMinutes(6) 2 60; Ok (-not $d.Send) 'still down: no spam'
    $d = Get-OpsAlertDecision $d.State 'down' $t0.AddMinutes(65) 2 60; Ok ($d.Send -and $d.Kind -eq 'reminder') 'reminder after 60 min'
    $d = Get-OpsAlertDecision $d.State 'degraded' $t0.AddMinutes(67) 2 60; Ok ($d.Send -and $d.Kind -eq 'degraded') 'status change down->degraded alerts'
    $d = Get-OpsAlertDecision $d.State 'up' $t0.AddMinutes(70) 2 60
    Ok ($d.Send -and $d.Kind -eq 'recovered' -and [int]$d.DownFor.TotalMinutes -eq 68) 'recovered with downtime since first failure' ("kind $($d.Kind) downFor $($d.DownFor)")
    $d = Get-OpsAlertDecision $d.State 'down' $t0.AddMinutes(72) 2 60; Ok (-not $d.Send) 'blip after recovery: silent'
    $d = Get-OpsAlertDecision $d.State 'up' $t0.AddMinutes(74) 2 60; Ok (-not $d.Send) 'one-run blip never alerted, never "recovered"'
    $json = ConvertTo-Json $d.State | ConvertFrom-Json
    $d2 = Get-OpsAlertDecision $json 'down' $t0.AddMinutes(80) 1 60
    Ok ($d2.Send -and $d2.Kind -eq 'down') 'state survives a JSON round trip'

    Section 'Discord payload and posting'
    Ok (Test-OpsDiscordWebhookUrl $hook) 'valid webhook URL accepted'
    Ok (-not (Test-OpsDiscordWebhookUrl 'http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz')) 'http refused'
    Ok (-not (Test-OpsDiscordWebhookUrl 'https://discord.com.evil.example/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz')) 'look-alike host refused'
    Ok (-not (Test-OpsDiscordWebhookUrl 'REPLACE_WITH_DISCORD_WEBHOOK_URL')) 'placeholder refused'
    $pl = New-OpsDiscordPayload 'Realm 1 is DOWN' 'desc' 'down' @(@{ name = 'Checks'; value = 'x'; inline = $false }) '<@&123456789>'
    Ok ($pl.content -eq '<@&123456789>' -and ($pl.allowed_mentions.parse -join ',') -eq 'roles,users') 'role mention only on down'
    $pl = New-OpsDiscordPayload 'Realm 1 is back up' 'desc' 'recovered' @() '<@&123456789>'
    Ok ((-not $pl.Contains('content')) -and @($pl.allowed_mentions.parse).Count -eq 0) 'no mention on recovery; mentions disabled'
    $pl = New-OpsDiscordPayload 'x' 'y' 'down' @() '@everyone'
    Ok ((-not $pl.Contains('content')) -and @($pl.allowed_mentions.parse).Count -eq 0) '@everyone is never allowed'
    $pl = New-OpsDiscordPayload ('T' * 300) ('D' * 3000) 'info'
    Ok ($pl.embeds[0].title.Length -eq 256 -and $pl.embeds[0].description.Length -eq 2000) 'embed limits enforced'
    $wlog = Join-Path $work 'webhook.log'
    $pW = Get-FreePort; Start-Bg $node @($fakes, 'webhook', $pW, $wlog, '429once') | Out-Null
    $res = Send-OpsDiscordMessage "http://127.0.0.1:$pW/api/webhooks/1/tok" (New-OpsDiscordPayload 'Ostreval watch: Varrow Keep — up' 'désc' 'info')
    $lines = @(Get-Content $wlog)
    Ok ($res.Ok -and $res.Status -eq 204 -and $lines.Count -eq 2) 'rate limit (429) retried after retry_after, then delivered' ("ok=$($res.Ok) status=$($res.Status) requests=$($lines.Count)")
    $body = ($lines[1] | ConvertFrom-Json)
    Ok ($body.ct -match 'application/json' -and ($body.body | ConvertFrom-Json).embeds[0].title -eq 'Ostreval watch: Varrow Keep — up') 'JSON body is UTF-8 with embeds'
    $wlog2 = Join-Path $work 'webhook404.log'
    $pW2 = Get-FreePort; Start-Bg $node @($fakes, 'webhook', $pW2, $wlog2, '404') | Out-Null
    $res = Send-OpsDiscordMessage "http://127.0.0.1:$pW2/api/webhooks/1/tok" (New-OpsDiscordPayload 't' 'd' 'info')
    Ok ((-not $res.Ok) -and $res.Status -eq 404 -and @(Get-Content $wlog2).Count -eq 1 -and $res.Error -match 'deleted or the URL is wrong') '404 not retried, plain message'
    $res = Send-OpsDiscordMessage "http://127.0.0.1:$(Get-FreePort)/api/webhooks/1/tok" (New-OpsDiscordPayload 't' 'd' 'info') 1 2000
    Ok ((-not $res.Ok)) 'connection refused reported, no exception'

    Section 'retention plans and zip entry rules'
    $names = @('realm-realm-1-20260901-042000Z.zip', 'realm-realm-1-20260902-042000Z.zip', 'realm-realm-1-20260903-042000Z.zip', 'notes.txt')
    Eq ((Get-OpsLocalPrunePlan $names 2) -join ',') 'realm-realm-1-20260901-042000Z.zip' 'local prune keeps the newest N'
    Eq @(Get-OpsLocalPrunePlan $names 5).Count 0 'local prune with room: nothing'
    $rn = @('realm-r-20200101-000000Z.zip', 'realm-r-20200101-000000Z.zip.sha256', 'realm-r-20200102-000000Z.zip', 'realm-r-20260930-000000Z.zip', 'realm-r-20261001-000000Z.zip')
    $plan = @(Get-OpsRemotePrunePlan $rn 30 2 ([datetime]::new(2026, 10, 2, 0, 0, 0, [DateTimeKind]::Utc)))
    Eq ($plan -join ',') 'realm-r-20200102-000000Z.zip,realm-r-20200101-000000Z.zip,realm-r-20200101-000000Z.zip.sha256' 'remote prune: old ones and their sidecars'
    $plan = @(Get-OpsRemotePrunePlan @('realm-r-20200101-000000Z.zip', 'realm-r-20200102-000000Z.zip') 30 2 ([datetime]::UtcNow))
    Eq $plan.Count 0 'remote prune never goes below the minimum, however old'
    Eq @(Get-OpsRemotePrunePlan $rn 0 1 ([datetime]::UtcNow)).Count 0 'remoteKeepDays 0 disables pruning'
    Ok (Test-OpsZipEntryName 'Saves/World/world.sav') 'Saves entry allowed'
    Ok (Test-OpsZipEntryName 'Mods/Atmosphere.cfg') 'Mods override allowed'
    Ok (-not (Test-OpsZipEntryName 'Mods/Atmosphere.defaults.cfg')) 'Mods defaults refused'
    Ok (-not (Test-OpsZipEntryName 'ROK_Data/Managed/Assembly-CSharp.dll')) 'game binary refused'
    Ok (-not (Test-OpsZipEntryName 'Saves/../../evil.txt')) 'path traversal refused'
    Ok (-not (Test-OpsZipEntryName '/etc/passwd')) 'absolute path refused'
    Ok (-not (Test-OpsZipEntryName 'C:/Windows/x')) 'drive path refused'

    Section 'ops lock'
    $lr = Join-Path $work 'lockroot'
    $l1 = Enter-OpsLock $lr 'test'
    $holder = Start-Process -FilePath $node -ArgumentList @('-e', 'setTimeout(()=>{},20000)') -PassThru
    $script:procs.Add($holder)
    Exit-OpsLock $l1
    [IO.File]::WriteAllText((Join-Path $lr 'state/ops.lock'), "$($holder.Id)|backup|now")
    Throws { Enter-OpsLock $lr 'update' } 'Another Realm ops job \(backup' 'lock held by a live process refuses'
    [IO.File]::WriteAllText((Join-Path $lr 'state/ops.lock'), '999999|backup|old')
    $l2 = Enter-OpsLock $lr 'update'
    Ok ((Get-Content $l2) -match "^$PID\|update") 'stale lock taken over'
    Exit-OpsLock $l2
    Ok (-not (Test-Path $l2)) 'lock released'

    Section 'config validation'
    $ex = Read-OpsConfig (Join-Path (Split-Path -Parent $PSScriptRoot) 'config/realm-ops.example.json')
    $probs = @(Test-OpsConfig $ex 'All')
    Eq $probs.Count 0 ('example config is structurally valid (placeholders mean "not set yet"): ' + ($probs -join ' | '))
    $bad = ConvertFrom-Json '{"opsRoot":"/etc","servers":[{"name":"A","root":"G:\\x","gamePort":7350,"queryPort":7350},{"name":"a","root":"G:\\y"}],"backup":{"localDir":"G:\\b","remote":"no colon here"},"monitor":{"discordWebhookUrl":"https://example.com/hook"}}'
    $probs = @(Test-OpsConfig $bad 'All') -join "`n"
    Ok ($probs -match 'opsRoot: .*system folder') 'system-folder opsRoot reported'
    Ok ($probs -match 'queryPort .* must differ') 'query port = game port reported'
    Ok ($probs -match "share the name/slug 'a'") 'duplicate server names reported'
    Ok ($probs -match "backup.remote 'no colon here'") 'bad remote reported'
    Ok ($probs -match 'not a Discord webhook URL') 'non-Discord webhook reported'
    Ok ($probs -match 'masterServerDir is not set') 'missing master reported'

    # ------------------------------------------------------------------ integration
    Section 'snapshot consistency (a server saving during the copy)'
    $busy = Join-Path $work 'busy'; New-FakeServerRoot $busy
    for ($i = 0; $i -lt 300; $i++) { [IO.File]::WriteAllText((Join-Path $busy "Saves/World/chunk$i.dat"), ('c' * 4000)) }
    Start-Bg $node @($fakes, 'touch', (Join-Path $busy 'Saves/World/world.sav'), '5', '20000') | Out-Null
    $snap = New-OpsSnapshot $busy (Join-Path $work 'busy-stage') $true 3 1
    Ok ((-not $snap.Consistent) -and $snap.Attempts -eq 3) 'changing file detected, copy repeated 3 times, marked inconsistent' ("consistent=$($snap.Consistent) attempts=$($snap.Attempts)")
    $quiet = Join-Path $work 'quiet'; New-FakeServerRoot $quiet
    $snap = New-OpsSnapshot $quiet (Join-Path $work 'quiet-stage') $true 3 1
    Ok ($snap.Consistent -and $snap.Attempts -eq 1) 'idle server: consistent on the first copy'
    $rels = @($snap.Files | ForEach-Object { $_.Rel }) -join ','
    Ok ($rels -match 'Saves/World/world.sav' -and $rels -match 'oxide/data/RealmChronicle.json' -and $rels -match 'Configuration/ServerSettings.cfg' -and $rels -match 'Mods/Atmosphere.cfg') 'backup set holds world, data, config, Mods override'
    Ok ($rels -notmatch 'defaults.cfg|texture.bin|oxide/logs|Assembly-CSharp') 'backup set leaves out game files, defaults and logs'

    if (-not $Rclone -or -not (Test-Path $Rclone)) {
        Skip 'backup/restore with S3' 'pass -Rclone <path to rclone>'
    } else {
        Section 'backup -> S3 (rclone serve s3) -> restore'
        $s3dir = Join-Path $work 's3'; New-Item -ItemType Directory -Path (Join-Path $s3dir 'realm-bucket') -Force | Out-Null
        $pS3 = Get-FreePort
        Start-Bg $Rclone @('serve', 's3', $s3dir, '--addr', "127.0.0.1:$pS3", '--auth-key', 'TESTKEYID,TESTSECRETKEY', '--log-level', 'INFO') 'Starting s3 server|Serving|listening|s3 server' | Out-Null
        Start-Sleep -Milliseconds 500
        $ops = Join-Path $work 'opsroot'
        $secure = Join-Path $ops 'secure'; New-Item -ItemType Directory -Path $secure -Force | Out-Null
        $rcConf = Join-Path $secure 'rclone.conf'
        [IO.File]::WriteAllText($rcConf, "[offsite]`ntype = s3`nprovider = Other`naccess_key_id = TESTKEYID`nsecret_access_key = TESTSECRETKEY`nendpoint = http://127.0.0.1:$pS3`nregion = us-east-1`nforce_path_style = true`n")
        $live = Join-Path $work 'live1'; New-FakeServerRoot $live
        $pA = Get-FreePort -Udp; Start-Bg $node @($fakes, 'a2s', $pA, 'info') | Out-Null
        $pT = Get-FreePort; Start-Bg $node @($fakes, 'tcp', $pT) | Out-Null
        $cfgPath = Join-Path $work 'realm-ops.json'
        $cfgObj = [ordered]@{
            opsRoot = $ops; steamcmdDir = (Join-Path $ops 'steamcmd'); masterServerDir = (Join-Path $work 'master2'); oxideZip = (Join-Path $work 'oxide.zip')
            servers = @(
                [ordered]@{ name = 'Realm 1'; root = $live; host = '127.0.0.1'; gamePort = $pT; queryPort = $pA },
                [ordered]@{ name = 'Realm 2'; root = (Join-Path $work 'not-here'); host = '127.0.0.1'; gamePort = (Get-FreePort); queryPort = (Get-FreePort -Udp); backup = $false })
            backup = [ordered]@{ localDir = (Join-Path $ops 'backups'); keepLocal = 2; rcloneExe = $Rclone; rcloneConfig = $rcConf; remote = 'offsite:realm-bucket/realm'; remoteKeepDays = 30; remoteMinKeep = 2; verifyByDownload = $true; snapshotRetrySeconds = 1 }
            monitor = [ordered]@{ discordWebhookUrl = ''; failuresBeforeAlert = 2; remindEveryMinutes = 60; timeoutMs = 800; backupMaxAgeHours = 26 }
        }
        Write-Json $cfgPath $cfgObj

        # old backups, local and remote, to prove pruning
        $ldir = Join-Path $ops 'backups/realm-1'; New-Item -ItemType Directory -Path $ldir -Force | Out-Null
        foreach ($n in @('realm-realm-1-20200101-000000Z.zip', 'realm-realm-1-20200102-000000Z.zip', 'realm-realm-1-20200103-000000Z.zip')) { [IO.File]::WriteAllText((Join-Path $ldir $n), 'old'); [IO.File]::WriteAllText((Join-Path $ldir ($n + '.sha256')), 'x') }
        foreach ($n in @('realm-realm-1-20200101-000000Z.zip', 'realm-realm-1-20200102-000000Z.zip', 'realm-realm-1-20200103-000000Z.zip')) {
            $tmpf = Join-Path $work $n; [IO.File]::WriteAllText($tmpf, 'old remote')
            $rr = Invoke-OpsProcess $Rclone @('copyto', $tmpf, "offsite:realm-bucket/realm/realm-1/$n", '--config', $rcConf) 60
            if ($rr.ExitCode -ne 0) { throw "seeding remote failed: $($rr.Output)" }
        }

        $wi = Invoke-Script 'Backup-RealmOffsite.ps1' @('-ConfigPath', $cfgPath, '-WhatIf')
        Ok ($wi.Code -eq 0 -and $wi.Out -match 'offsite: offsite:realm-bucket/realm/realm-1' -and -not (Test-Path (Join-Path $ops 'state/backup-realm-1.json'))) '-WhatIf shows the plan and writes nothing'

        $b = Invoke-Script 'Backup-RealmOffsite.ps1' @('-ConfigPath', $cfgPath)
        Ok ($b.Code -eq 0) 'backup exit code 0' $b.Out
        $zips = @(Get-ChildItem $ldir -Filter 'realm-*.zip' | Sort-Object Name)
        Eq $zips.Count 2 'local retention keepLocal=2'
        $newZip = $zips[-1].FullName
        Ok ($zips[-1].Name -match '^realm-realm-1-\d{8}-\d{6}Z\.zip$' -and (Test-Path ($newZip + '.sha256'))) 'zip name and sidecar'
        Ok (-not (Test-Path (Join-Path $ldir 'realm-realm-1-20200102-000000Z.zip.sha256'))) 'pruned zip took its sidecar with it'
        $chk = Test-OpsBackupZip $newZip (Join-Path $work 'verify1')
        Ok ($chk.Ok -and $chk.SidecarOk -and $chk.FilesVerified -eq 8) 'zip verifies: sidecar + 8 file hashes' (($chk.Problems -join '; ') + " verified=$($chk.FilesVerified)")
        Ok ($chk.Manifest.consistent -eq $true -and $chk.Manifest.server -eq 'Realm 1' -and $chk.Manifest.format -eq 'realm-ops-backup/1') 'manifest fields'
        $remoteNames = @((Invoke-OpsProcess $Rclone @('lsf', 'offsite:realm-bucket/realm/realm-1', '--config', $rcConf) 60).StdOut -split "`n" | Where-Object { $_ })
        Ok ($remoteNames -contains $zips[-1].Name -and $remoteNames -contains ($zips[-1].Name + '.sha256')) 'zip and sidecar are at the S3 remote'
        Ok (($remoteNames -contains 'realm-realm-1-20200103-000000Z.zip') -and -not ($remoteNames -contains 'realm-realm-1-20200101-000000Z.zip') -and -not ($remoteNames -contains 'realm-realm-1-20200102-000000Z.zip')) 'remote retention: old removed, newest 2 kept' ($remoteNames -join ',')
        $st = Get-Content (Join-Path $ops 'state/backup-realm-1.json') -Raw | ConvertFrom-Json
        Ok ($st.lastOffsite -and $st.lastSuccess -and $null -eq $st.lastError) 'state file records the offsite success'
        Ok ((Get-ChildItem (Join-Path $ops 'logs') -Filter 'backup-*.log' | Get-Content -Raw) -notmatch 'TESTSECRETKEY') 'no secret in the backup log'
        Ok (-not (Test-Path (Join-Path $ops 'staging/realm-1'))) 'staging cleaned up'

        Section 'restore drill from the S3 remote'
        $dr = Invoke-Script 'Restore-RealmBackup.ps1' @('-ConfigPath', $cfgPath, '-Server', 'Realm 1', '-FromRemote', '-DrillDir', (Join-Path $work 'drill1'))
        Ok ($dr.Code -eq 0 -and $dr.Out -match 'Drill extract OK') 'drill from remote succeeds' $dr.Out
        Ok ((Get-Content (Join-Path $work 'drill1/Saves/World/world.sav') -Raw) -eq (Get-Content (Join-Path $live 'Saves/World/world.sav') -Raw)) 'drill copy matches the live world'
        $rep = Get-Content (Join-Path $ops 'state/drill-realm-1.json') -Raw | ConvertFrom-Json
        Ok ($rep.ok -and $rep.downloadedFromRemote -and $rep.filesVerified -eq 8) 'drill report written'
        $dr2 = Invoke-Script 'Restore-RealmBackup.ps1' @('-ConfigPath', $cfgPath, '-Server', 'Realm 1', '-DrillDir', (Join-Path $live 'Saves/drill'))
        Ok ($dr2.Code -ne 0 -and $dr2.Out -match 'must not overlap the live server') 'drill into the live server folder refused'

        Section 'tampered backup is refused'
        $bad = Join-Path $work 'tampered.zip'; Copy-Item $newZip $bad; Copy-Item ($newZip + '.sha256') ($bad + '.sha256')
        $bytes = [IO.File]::ReadAllBytes($bad); $bytes[100] = $bytes[100] -bxor 0xFF; [IO.File]::WriteAllBytes($bad, $bytes)
        $tr = Invoke-Script 'Restore-RealmBackup.ps1' @('-ConfigPath', $cfgPath, '-Server', 'Realm 1', '-ZipPath', $bad, '-DrillDir', (Join-Path $work 'drill-bad'))
        Ok ($tr.Code -ne 0 -and $tr.Out -match 'failed verification' -and -not (Test-Path (Join-Path $work 'drill-bad'))) 'corrupted zip never restored'

        Section 'live restore'
        [IO.File]::WriteAllText((Join-Path $live 'Saves/World/world.sav'), 'GRIEFED WORLD')
        [IO.File]::WriteAllText((Join-Path $live 'Saves/World/stray.dat'), 'stray')
        [IO.File]::WriteAllText((Join-Path $live 'oxide/config/CrownAndConsequences.json'), '{"TaxCap":99}')
        $lr1 = Invoke-Script 'Restore-RealmBackup.ps1' @('-ConfigPath', $cfgPath, '-Server', 'Realm 1', '-ZipPath', $newZip, '-Live', '-WhatIf')
        Ok ((Get-Content (Join-Path $live 'Saves/World/world.sav') -Raw) -eq 'GRIEFED WORLD') '-WhatIf live restore changes nothing'
        $lr2 = Invoke-Script 'Restore-RealmBackup.ps1' @('-ConfigPath', $cfgPath, '-Server', 'Realm 1', '-ZipPath', $newZip, '-Live', '-Yes')
        Ok ($lr2.Code -eq 0 -and $lr2.Out -match 'Restored 8 files') 'live restore succeeds' $lr2.Out
        Ok ((Get-Content (Join-Path $live 'Saves/World/world.sav') -Raw) -match '^world-v1') 'world is back'
        Ok (-not (Test-Path (Join-Path $live 'Saves/World/stray.dat'))) 'Saves replaced whole (stray file gone from live)'
        Ok ((Get-Content (Join-Path $live 'oxide/config/CrownAndConsequences.json') -Raw) -eq '{"TaxCap":10}') 'config file restored'
        $aside = @(Get-ChildItem (Join-Path $live '_realm-backups') -Directory -Filter 'ops-restore-*')[0].FullName
        Ok ((Test-Path (Join-Path $aside 'Saves/World/stray.dat')) -and (Get-Content (Join-Path $aside 'oxide/config/CrownAndConsequences.json') -Raw) -eq '{"TaxCap":99}') 'previous state kept aside, nothing deleted'
        $pre = @(Get-ChildItem $ldir -Filter 'realm-*.zip' | Sort-Object Name)[-1].FullName
        $preM = (Read-OpsBackupManifest $pre).Manifest
        Ok ($preM.label -eq 'pre-restore') 'pre-restore safety backup taken first'
        $nm = Join-Path $work 'nomarker'; New-FakeServerRoot $nm; Remove-Item -Force (Join-Path $nm '.realm-test-copy')
        $cfgObj.servers[0].root = $nm; Write-Json $cfgPath $cfgObj
        $lr3 = Invoke-Script 'Restore-RealmBackup.ps1' @('-ConfigPath', $cfgPath, '-Server', 'Realm 1', '-ZipPath', $newZip, '-Live', '-Yes')
        Ok ($lr3.Code -ne 0 -and $lr3.Out -match 'no \.realm-test-copy marker') 'live restore refuses a folder without the instance marker'
        $cfgObj.servers[0].root = $live; Write-Json $cfgPath $cfgObj

        Section 'backup failure path'
        $cfgObj.servers[0].root = (Join-Path $work 'gone'); Write-Json $cfgPath $cfgObj
        $bf = Invoke-Script 'Backup-RealmOffsite.ps1' @('-ConfigPath', $cfgPath, '-LocalOnly')
        $st = Get-Content (Join-Path $ops 'state/backup-realm-1.json') -Raw | ConvertFrom-Json
        Ok ($bf.Code -eq 1 -and $st.lastError -match 'not found' -and $st.lastOffsite) 'missing server folder: exit 1, error recorded, last good offsite kept'
        $cfgObj.servers[0].root = $live; Write-Json $cfgPath $cfgObj

        Section 'uptime monitor (end to end, no webhook)'
        $w1 = Invoke-Script 'Watch-RealmUptime.ps1' @('-ConfigPath', $cfgPath)
        Ok ($w1.Code -eq 0 -and $w1.Out -match 'Realm 1: UP' -and $w1.Out -match 'Realm 2: DOWN') 'run 1: Realm 1 up, Realm 2 down' $w1.Out
        Ok ($w1.Out -notmatch 'Alert') 'run 1: no alert yet (debounce)'
        $w2 = Invoke-Script 'Watch-RealmUptime.ps1' @('-ConfigPath', $cfgPath)
        Ok ($w2.Out -match 'Alert \(no webhook configured\): Realm 2 is DOWN') 'run 2: DOWN alert for Realm 2' $w2.Out
        $ms = Get-Content (Join-Path $ops 'state/monitor.json') -Raw | ConvertFrom-Json
        Ok ($ms.targets.'server:realm-2'.Alerted -eq $true -and $ms.targets.'server:realm-1'.Status -eq 'up' -and $ms.targets.'backup:realm-1'.Status -eq 'up') 'monitor state saved (alerted, up, backup fresh)'
        $w3 = Invoke-Script 'Watch-RealmUptime.ps1' @('-ConfigPath', $cfgPath, '-DryRun')
        Ok ($w3.Out -notmatch 'would post') 'run 3: still down, no repeat'
        $st = Get-Content (Join-Path $ops 'state/backup-realm-1.json') -Raw | ConvertFrom-Json
        $st.lastOffsite = [datetime]::UtcNow.AddHours(-30).ToString('o'); Write-Json (Join-Path $ops 'state/backup-realm-1.json') $st
        $w4 = Invoke-Script 'Watch-RealmUptime.ps1' @('-ConfigPath', $cfgPath)
        Ok ($w4.Out -match 'No fresh backup for Realm 1') 'stale offsite backup (30 h) alerted' $w4.Out
        $cfgObj.monitor.discordWebhookUrl = 'https://discord.com/api/webhooks/1/x'; Write-Json $cfgPath $cfgObj
        $w5 = Invoke-Script 'Watch-RealmUptime.ps1' @('-ConfigPath', $cfgPath)
        Ok ($w5.Code -eq 2 -and $w5.Out -match 'not a Discord webhook URL') 'malformed webhook refused before any check'
        $cfgObj.monitor.discordWebhookUrl = ''; Write-Json $cfgPath $cfgObj

        Section 'config checker'
        $tc = Invoke-Script 'Test-RealmOpsConfig.ps1' @('-ConfigPath', $cfgPath)
        Ok ($tc.Out -match '\[PASS\] Remote reachable' -and $tc.Out -match '\[PASS\] Realm 1: A2S' -and $tc.Out -match 'rclone v') 'checker reaches the remote and the server' $tc.Out
        Ok ($tc.Out -notmatch 'TESTSECRETKEY') 'checker prints no secret'

        Section 'SteamCMD install and update (fake steamcmd)'
        $fake = Join-Path $work 'fake-steamcmd.sh'
        $calls = Join-Path $work 'steamcmd-calls.log'
        $fakeSrc = @'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_STEAMCMD_LOG"
dir=""; upd=0
while [ $# -gt 0 ]; do case "$1" in +force_install_dir) dir="$2"; shift;; +app_update) upd=1;; esac; shift; done
echo "Steam Console Client (c) Valve Corporation - version 1727000000"
[ "$upd" = 1 ] || exit 0
n=$(cat "$FAKE_STEAMCMD_LOG.count" 2>/dev/null || echo 0); n=$((n+1)); echo $n > "$FAKE_STEAMCMD_LOG.count"
echo "Logging in user 'anonymous' to Steam Public...OK"
case "$FAKE_STEAMCMD_MODE" in
  failfirst) if [ "$n" = 1 ]; then echo "ERROR! Failed to install app '381690' (Timeout)"; exit 8; fi;;
  nosub) echo "ERROR! Failed to install app '381690' (No subscription)"; exit 8;;
  uptodate) echo "Success! App '381690' already up to date."; exit 0;;
esac
mkdir -p "$dir/steamapps" "$dir/ROK_Data/Managed"
echo exe > "$dir/ROK.exe"
printf '"AppState"\n{\n\t"appid"\t\t"381690"\n\t"StateFlags"\t\t"4"\n\t"buildid"\t\t"%s"\n}\n' "${FAKE_BUILD:-1000}" > "$dir/steamapps/appmanifest_381690.acf"
echo " Update state (0x61) downloading, progress: 100.00 (1 / 1)"
echo "Success! App '381690' fully installed."
'@
        [IO.File]::WriteAllText($fake, $fakeSrc.Replace("`r`n", "`n"))
        & chmod +x $fake
        $env:FAKE_STEAMCMD_LOG = $calls; $env:FAKE_STEAMCMD_MODE = 'failfirst'; $env:FAKE_BUILD = '1000'
        $in = Invoke-Script 'Install-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake)
        $ms = Get-Content (Join-Path $ops 'state/master.json') -Raw | ConvertFrom-Json
        Ok ($in.Code -eq 0 -and (Test-Path (Join-Path $work 'master2/ROK.exe')) -and $ms.buildId -eq '1000') 'install: retried after a failed first app_update, build recorded' $in.Out
        $callText = Get-Content $calls -Raw
        Ok ($callText -match '\+force_install_dir .*master2 \+login anonymous \+app_update 381690 validate \+quit') 'install used force_install_dir, anonymous login and validate'
        $env:FAKE_STEAMCMD_MODE = 'nosub'
        Remove-Item "$calls.count" -ErrorAction SilentlyContinue
        $in2 = Invoke-Script 'Install-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake)
        Ok ($in2.Code -ne 0 -and $in2.Out -match 'No subscription' -and (Get-Content "$calls.count") -eq '1') 'No subscription: stops without pointless retries'
        $instM = Join-Path $work 'live1'
        $cfgObj.masterServerDir = $instM; Write-Json $cfgPath $cfgObj
        $in3 = Invoke-Script 'Install-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake)
        Ok ($in3.Code -ne 0 -and $in3.Out -match 'Refusing install folder') 'install refuses an instance folder as master'
        $cfgObj.masterServerDir = (Join-Path $work 'master2'); Write-Json $cfgPath $cfgObj

        # real download path: zip with a steamcmd.exe (unix exec bit set) served over HTTP
        $zipSrc = Join-Path $work 'steamcmd.zip'
        & python3 -c "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'w'); i=zipfile.ZipInfo('steamcmd.exe'); i.external_attr=(0o100755<<16); z.writestr(i, open(sys.argv[2]).read()); z.close()" $zipSrc $fake
        $pF = Get-FreePort; Start-Bg $node @($fakes, 'file', $pF, $zipSrc) | Out-Null
        $env:FAKE_STEAMCMD_MODE = 'uptodate'
        $in4 = Invoke-Script 'Install-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdZipUrl', "http://127.0.0.1:$pF/steamcmd.zip")
        Ok ($in4.Code -eq 0 -and (Test-Path (Join-Path $ops 'steamcmd/steamcmd.exe')) -and -not (Test-Path (Join-Path $ops 'steamcmd/steamcmd.zip')) -and $in4.Out -match 'SteamCMD extracted') 'install downloads and extracts SteamCMD' $in4.Out

        Section 'update: backup first, then SteamCMD'
        $fakeServer = Join-Path $work 'fake-server-scripts'; New-Item -ItemType Directory -Path $fakeServer | Out-Null
        $applyLog = Join-Path $work 'apply.log'
        [IO.File]::WriteAllText((Join-Path $fakeServer 'New-TestServer.ps1'), "param(`$Source,`$Destination,[switch]`$Refresh,[switch]`$Yes) Add-Content '$applyLog' ""refresh `$Destination from `$Source refresh=`$Refresh""")
        [IO.File]::WriteAllText((Join-Path $fakeServer 'Install-Oxide.ps1'), "param(`$ServerRoot,`$ZipPath,[switch]`$Yes) Add-Content '$applyLog' ""oxide `$ServerRoot zip=`$ZipPath""")
        [IO.File]::WriteAllText((Join-Path $work 'oxide.zip'), 'zip')
        $env:FAKE_STEAMCMD_MODE = 'uptodate'
        $before = @(Get-ChildItem $ldir -Filter 'realm-*.zip').Count
        $u1 = Invoke-Script 'Update-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake, '-LocalBackupOnly', '-Yes', '-RealmServerScripts', $fakeServer)
        $newest = @(Get-ChildItem $ldir -Filter 'realm-*.zip' | Sort-Object Name)[-1].FullName
        Ok ($u1.Code -eq 0 -and $u1.Out -match 'already current' -and (Read-OpsBackupManifest $newest).Manifest.label -eq 'pre-update') 'up to date: backup taken (label pre-update), nothing else' $u1.Out
        $env:FAKE_STEAMCMD_MODE = ''; $env:FAKE_BUILD = '2000'
        $u2 = Invoke-Script 'Update-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake, '-LocalBackupOnly', '-Yes', '-RealmServerScripts', $fakeServer)
        Ok ($u2.Out -match 'build 1000 -> 2000' -and $u2.Out -match 'New-TestServer.ps1.*-Refresh' -and -not (Test-Path $applyLog)) 'new build without -ApplyToInstances: prints the refresh commands only' $u2.Out
        $env:FAKE_BUILD = '3000'
        $u3 = Invoke-Script 'Update-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake, '-LocalBackupOnly', '-Yes', '-ApplyToInstances', '-RealmServerScripts', $fakeServer)
        $al = Get-Content $applyLog -Raw
        Ok ($u3.Code -eq 0 -and $al -match [regex]::Escape("refresh $live from") -and $al -match 'refresh=True' -and $al -match [regex]::Escape("oxide $live zip=")) '-ApplyToInstances refreshes every instance and re-installs Oxide' ($u3.Out + $al)
        $cfgObj.servers[0].root = (Join-Path $work 'gone'); Write-Json $cfgPath $cfgObj
        $countBefore = (Get-Content "$calls.count")
        $u4 = Invoke-Script 'Update-RealmServer.ps1' @('-ConfigPath', $cfgPath, '-SteamCmdExe', $fake, '-LocalBackupOnly', '-Yes', '-RealmServerScripts', $fakeServer)
        Ok ($u4.Code -ne 0 -and $u4.Out -match 'Backup failed' -and (Get-Content "$calls.count") -eq $countBefore) 'failed backup stops the update before SteamCMD runs'
        $cfgObj.servers[0].root = $live; Write-Json $cfgPath $cfgObj
    }

    Section 'SYSTEM task ACL check (pure)'
    $rule = { param($Sid, $Rights, $Allow = $true, $InheritOnly = $false) [pscustomobject]@{ Sid = $Sid; Rights = [long]$Rights; Allow = $Allow; InheritOnly = $InheritOnly } }
    # FileSystemRights: Modify 0x301BF, ReadAndExecute 0x200A9, FullControl 0x1F01FF, GENERIC_WRITE 0x40000000, Delete 0x10000
    $dataDriveDefault = @((& $rule 'S-1-5-18' 0x1F01FF), (& $rule 'S-1-5-32-544' 0x1F01FF), (& $rule 'S-1-5-11' 0x301BF), (& $rule 'S-1-5-32-545' 0x200A9))
    Eq ((Get-OpsBroadWriters $dataDriveDefault) -join ',') 'Authenticated Users' 'data-drive default ACL: Authenticated Users can modify'
    $locked = @((& $rule 'S-1-5-18' 0x1F01FF), (& $rule 'S-1-5-32-544' 0x1F01FF), (& $rule 'S-1-5-32-545' 0x200A9))
    Eq @(Get-OpsBroadWriters $locked).Count 0 'admins + SYSTEM full, Users read/execute: no broad writer'
    Eq ((Get-OpsBroadWriters @((& $rule 'S-1-1-0' 0x40000000))) -join ',') 'Everyone' 'GENERIC_WRITE for Everyone flagged'
    Eq ((Get-OpsBroadWriters @((& $rule 'S-1-5-32-545' 0x10000))) -join ',') 'Users' 'Delete alone flagged (file can be replaced)'
    Eq @(Get-OpsBroadWriters @((& $rule 'S-1-5-11' 0x301BF $false))).Count 0 'deny rules are not writers'
    Eq @(Get-OpsBroadWriters @((& $rule 'S-1-5-11' 0x301BF $true $true))).Count 0 'inherit-only rules do not apply to the object itself'
    Eq @(Get-OpsBroadWriters @((& $rule 'S-1-5-21-1-2-3-1001' 0x1F01FF))).Count 0 'a single named account is not a broad group'
    Eq ((Get-OpsBroadWriters @((& $rule 'S-1-5-11' 0x301BF), (& $rule 'S-1-5-11' 0x2))) -join ',') 'Authenticated Users' 'each group reported once'
    if ($script:OpsIsWindows) { Skip 'live ACL check on Windows' 'covered by Register-RealmTasks on Windows only' }
    else { Skip 'Get-OpsAclRules / Register-RealmTasks ACL refusal' 'Windows only (Get-Acl on NTFS); UNVERIFIED on real Windows' }

    Section 'Task Scheduler registration (cmdlets mocked)'
    $global:Registered = @()
    function global:New-ScheduledTaskPrincipal { param($UserId, $LogonType, $RunLevel) [pscustomobject]@{ UserId = $UserId; LogonType = $LogonType; RunLevel = $RunLevel } }
    function global:New-ScheduledTaskAction { param($Execute, $Argument, $WorkingDirectory) [pscustomobject]@{ Execute = $Execute; Argument = $Argument } }
    function global:New-ScheduledTaskTrigger { param([switch]$Daily, $At, [switch]$Once, $RepetitionInterval, [switch]$AtStartup) [pscustomobject]@{ Daily = [bool]$Daily; At = $At; Once = [bool]$Once; Every = $RepetitionInterval; Boot = [bool]$AtStartup } }
    function global:New-ScheduledTaskSettingsSet { param([switch]$StartWhenAvailable, $ExecutionTimeLimit, $MultipleInstances, $RestartCount, $RestartInterval, [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries) [pscustomobject]@{ Limit = $ExecutionTimeLimit; Multi = $MultipleInstances; Restarts = $RestartCount } }
    function global:Register-ScheduledTask { param($TaskPath, $TaskName, $Action, $Trigger, $Settings, $Principal, $Description, [switch]$Force) $global:Registered += [pscustomobject]@{ Path = $TaskPath; Name = $TaskName; Action = $Action; Trigger = $Trigger; Settings = $Settings; Principal = $Principal } }
    $regCfgDir = Join-Path $work 'regcfg'; New-Item -ItemType Directory -Path $regCfgDir | Out-Null
    $regCfg = Join-Path $regCfgDir 'realm-ops.json'
    Write-Json $regCfg ([ordered]@{ opsRoot = (Join-Path $work 'opsreg'); steamcmdDir = (Join-Path $work 'sc'); masterServerDir = (Join-Path $work 'm'); servers = @([ordered]@{ name = 'Realm 1'; root = (Join-Path $work 'r1') }); backup = [ordered]@{ localDir = (Join-Path $work 'bk') }; monitor = [ordered]@{} })
    $env:SystemRoot = Join-Path $work 'WINDOWS'
    & (Join-Path $scripts 'Register-RealmTasks.ps1') -ConfigPath $regCfg -WhatIf 6>&1 | Out-Null
    Eq $global:Registered.Count 0 '-WhatIf registers nothing'
    & (Join-Path $scripts 'Register-RealmTasks.ps1') -ConfigPath $regCfg -BackupTime '05:10' -MonitorEveryMinutes 3 -SkipElevationCheck 6>&1 | Out-Null
    Eq $global:Registered.Count 2 'two tasks registered'
    $bt = $global:Registered | Where-Object { $_.Name -eq 'Realm Nightly Backup' }
    $mt = $global:Registered | Where-Object { $_.Name -eq 'Realm Uptime Monitor' }
    Ok ($bt.Path -eq '\Realm\' -and $bt.Trigger.Daily -and $bt.Trigger.At.ToString('HH:mm') -eq '05:10' -and $bt.Principal.UserId -eq 'NT AUTHORITY\SYSTEM' -and $bt.Settings.Restarts -eq 2) 'backup task: daily 05:10, SYSTEM, retries'
    Ok ($bt.Action.Execute -match 'WindowsPowerShell.v1\.0.powershell\.exe$' -and $bt.Action.Argument -match '-NonInteractive -ExecutionPolicy Bypass -File ".*Backup-RealmOffsite\.ps1" -ConfigPath ".*realm-ops\.json"') 'backup task runs Windows PowerShell 5.1 with the config path'
    Ok (@($mt.Trigger).Count -eq 2 -and @($mt.Trigger)[0].Every.TotalMinutes -eq 3 -and @($mt.Trigger)[1].Boot -and $mt.Settings.Limit.TotalMinutes -eq 5 -and "$($mt.Settings.Multi)" -eq 'IgnoreNew') 'monitor task: every 3 min + at boot, 5 min limit, no overlap'
    $inRepo = Join-Path (Split-Path -Parent $PSScriptRoot) 'config/realm-ops.example.json'
    Throws { & (Join-Path $scripts 'Register-RealmTasks.ps1') -ConfigPath $inRepo -SkipElevationCheck 6>&1 | Out-Null } 'inside the repository' 'config inside the repo refused'
} finally {
    foreach ($p in $script:procs) { try { if (-not $p.HasExited) { $p.Kill() } } catch { } }
    if (-not $Keep) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "Work folder kept: $work" }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed, {2} skipped" -f $script:pass, $script:fail, $script:skip) -ForegroundColor $(if ($script:fail -gt 0) { 'Red' } else { 'Green' })
exit $script:fail
