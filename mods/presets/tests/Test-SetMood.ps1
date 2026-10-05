<#
.SYNOPSIS
  Behaviour tests for server\Set-Mood.ps1 and the mood presets, against a FAKE server folder.

.DESCRIPTION
  Builds a throwaway "test copy" (marker file + Mods\ with a realistic Environment.defaults.cfg and
  a few unrelated override files), then runs Set-Mood.ps1 through every path: list, validation of
  every preset, swap, swap-back, overlay + return, clear, status, restore, Steam refusal, comma-decimal
  refusal, bad presets. Nothing outside -WorkDir is touched. No real server or game file is used.

  The handler name "Environment" is made up for the test: the real file name is UNVERIFIED and
  Set-Mood.ps1 finds it from the defaults files, which is exactly what is being tested.

  RealmCommon's Assert-RealmTestCopy refuses any path on C:. On Windows pass a -WorkDir on another
  drive (for example G:\RealmTest\mood-selftest). On Linux/macOS pwsh the default temp folder works.

.EXAMPLE
  pwsh -File mods/presets/tests/Test-SetMood.ps1
  powershell -File mods\presets\tests\Test-SetMood.ps1 -WorkDir G:\RealmTest\mood-selftest
#>
param(
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ('realm-mood-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$setMood = Join-Path (Join-Path $repo 'server') 'Set-Mood.ps1'
$presets = Join-Path (Join-Path $repo 'mods') 'presets'

$script:pass = 0
$script:fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:pass++; Write-Host "PASS  $Name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "FAIL  $Name  $Detail" -ForegroundColor Red }
}
function Run([hashtable]$A) {
    # Runs Set-Mood.ps1 with the fake folders; returns @{ Ok; Error; Out }.
    $A['ServerRoot'] = $server
    if (-not $A.ContainsKey('BackupDir')) { $A['BackupDir'] = $backups }
    if (-not $A.ContainsKey('PresetsDir')) { $A['PresetsDir'] = $presets }
    $A['Yes'] = $true
    try {
        $out = & $setMood @A *>&1 | Out-String
        return @{ Ok = $true; Error = ''; Out = $out }
    } catch {
        return @{ Ok = $false; Error = $_.Exception.Message; Out = '' }
    }
}
function Cfg([string]$Name) {
    $p = Join-Path $mods $Name
    if (-not (Test-Path -LiteralPath $p)) { return @() }
    return @(Get-Content -LiteralPath $p)
}
function ValueOf([string]$Name, [string]$Key) {
    foreach ($l in (Cfg $Name)) {
        $m = [regex]::Match($l, '^\s*' + [regex]::Escape($Key) + '\s+=\s*''([^'']*)''')
        if ($m.Success) { return $m.Groups[1].Value }
    }
    return $null
}
function CountKey([string]$Name, [string]$Key) {
    return @((Cfg $Name) | Where-Object { $_ -match ('^\s*' + [regex]::Escape($Key) + '\s+=') }).Count
}

$server = Join-Path $WorkDir 'server'
$backups = Join-Path $WorkDir 'backups'
$mods = Join-Path $server 'Mods'
New-Item -ItemType Directory -Force -Path $mods | Out-Null
Set-Content -LiteralPath (Join-Path $server '.realm-test-copy') -Value 'test'

# Defaults as the game's writer would produce them (padded keys, # header). Scene values made up,
# DaySpeed deliberately not 1 so the relative #@scale path is really exercised.
$defaults = @(
    '# -- Environment Defaults --',
    "Atmosphere.FogColor              = 'rgba(1,1,1,1)'",
    "Atmosphere.FogDensity            = '1'",
    "Atmosphere.IslandLatitude        = '45'",
    "Atmosphere.IslandLongitude       = '0'",
    "Atmosphere.MoonColor             = 'rgba(1,1,1,1)'",
    "Atmosphere.SunColor              = 'rgba(1,1,1,1)'",
    "Clock.DaySpeed                   = '2'",
    "Weather.ClearWeight              = '5'",
    "Weather.CloudyWeight             = '3'",
    "Weather.PrecipitateHeavyWeight   = '1'",
    "Weather.PrecipitateLowWeight     = '2'",
    "Weather.PrecipitateMediumWeight  = '1'"
)
Set-Content -LiteralPath (Join-Path $mods 'Environment.defaults.cfg') -Value $defaults
Set-Content -LiteralPath (Join-Path $mods 'Players.defaults.cfg') -Value @('# -- Players Defaults --', "MaxHealth = '100'")
# An unrelated override that must survive every swap, and a pre-existing mood key to be replaced.
Set-Content -LiteralPath (Join-Path $mods 'Players.cfg') -Value @("MaxHealth = '120'")
Set-Content -LiteralPath (Join-Path $mods 'Environment.cfg') -Value @('# owner note', "Weather.ClearWeight = '9'", "Weather.ClearWeight = '8'")
# An Apply-Preset.ps1 backup file: must never be read as a handler or changed.
Set-Content -LiteralPath (Join-Path $mods 'Environment.cfg.realm-backup') -Value @("Weather.ClearWeight = '1'")
$applyBackupHash = (Get-FileHash -LiteralPath (Join-Path $mods 'Environment.cfg.realm-backup')).Hash
$defaultsHash = (Get-FileHash -LiteralPath (Join-Path $mods 'Environment.defaults.cfg')).Hash

try {
    # ---- List / every preset validates -----------------------------------------------------------
    $r = Run @{ List = $true }
    Check 'List runs' $r.Ok $r.Error
    foreach ($id in @('grim-but-readable', 'long-winter', 'blood-moon', 'golden-summer', 'storm-season', 'ashfall',
                      'crown-night', 'truce', 'spring-rains', 'hunters-moon', 'first-frost')) {
        Check "List shows $id" ($r.Out -match [regex]::Escape($id)) ''
    }
    Check 'List prints exact storm odds' ($r.Out -match 'heavy rain 20\.0%') ''
    Check 'List prints exact grim odds (matches mods/README.md)' ($r.Out -match 'clear 23\.3%, cloudy 43\.3%') ''

    # ---- WhatIf writes nothing --------------------------------------------------------------------
    $before = (Get-FileHash -LiteralPath (Join-Path $mods 'Environment.cfg')).Hash
    $r = Run @{ Mood = 'long-winter'; WhatIf = $true }
    Check 'WhatIf runs' $r.Ok $r.Error
    Check 'WhatIf leaves Environment.cfg alone' ($before -eq (Get-FileHash -LiteralPath (Join-Path $mods 'Environment.cfg')).Hash) ''
    Check 'WhatIf makes no backup' (-not (Test-Path -LiteralPath $backups)) ''

    # ---- Apply long-winter -----------------------------------------------------------------------
    $r = Run @{ Mood = 'long-winter' }
    Check 'Apply long-winter' $r.Ok $r.Error
    Check 'FogDensity written' ((ValueOf 'Environment.cfg' 'Atmosphere.FogDensity') -eq '1.35') ''
    Check 'Duplicate ClearWeight collapsed to one line' ((CountKey 'Environment.cfg' 'Weather.ClearWeight') -eq 1) ''
    Check 'ClearWeight = 3' ((ValueOf 'Environment.cfg' 'Weather.ClearWeight') -eq '3') ''
    Check 'Owner comment kept' ((Cfg 'Environment.cfg') -contains '# owner note') ''
    Check 'Unrelated Players.cfg untouched' ((ValueOf 'Players.cfg' 'MaxHealth') -eq '120') ''
    Check 'No DaySpeed in long-winter' ($null -eq (ValueOf 'Environment.cfg' 'Clock.DaySpeed')) ''
    Check 'Every line keeps a space before =' (@((Cfg 'Environment.cfg') | Where-Object { $_ -match '[^\s]=' -and $_ -notmatch '^#' -and $_ -match '^[^'']*[^\s]=' }).Count -eq 0) ''
    Check 'Apply-Preset.ps1 backup untouched' ($applyBackupHash -eq (Get-FileHash -LiteralPath (Join-Path $mods 'Environment.cfg.realm-backup')).Hash) ''
    Check 'No stray handler file from the backup name' (-not (Test-Path -LiteralPath (Join-Path $mods 'Environment.cfg.realm-backup.cfg'))) ''
    Check 'defaults.cfg never written' ($defaultsHash -eq (Get-FileHash -LiteralPath (Join-Path $mods 'Environment.defaults.cfg')).Hash) ''
    $bk = @(Get-ChildItem -LiteralPath $backups -Directory)
    Check 'One backup made' ($bk.Count -eq 1) "got $($bk.Count)"
    if ($bk.Count -ge 1) {
        $orig = @(Get-Content -LiteralPath (Join-Path (Join-Path $bk[0].FullName 'Mods') 'Environment.cfg'))
        Check 'Backup holds the original Environment.cfg' ($orig -contains "Weather.ClearWeight = '9'") ''
    }
    $st = Get-Content -LiteralPath (Join-Path $server '.realm-mood.json') -Raw | ConvertFrom-Json
    Check 'State records long-winter' ($st.mood -eq 'long-winter') ''

    # ---- Swap to golden-summer: relative DaySpeed, odds, re-run is a no-op ------------------------
    Start-Sleep -Milliseconds 1100   # backup folder names have one-second resolution
    $r = Run @{ Mood = 'golden-summer' }
    Check 'Apply golden-summer' $r.Ok $r.Error
    Check 'DaySpeed = default 2 x 0.9 = 1.8' ((ValueOf 'Environment.cfg' 'Clock.DaySpeed') -eq '1.8') "got $(ValueOf 'Environment.cfg' 'Clock.DaySpeed')"
    Check 'FogDensity thinned to 0.85' ((ValueOf 'Environment.cfg' 'Atmosphere.FogDensity') -eq '0.85') ''
    Check 'Weights swapped (ClearWeight 8)' ((ValueOf 'Environment.cfg' 'Weather.ClearWeight') -eq '8') ''
    Check 'Odds printed' ($r.Out -match 'clear 80\.2%') ''
    $n = @(Get-ChildItem -LiteralPath $backups -Directory).Count
    $r = Run @{ Mood = 'golden-summer' }
    Check 'Re-apply same mood is a no-op' ($r.Ok -and $r.Out -match 'already match' -and @(Get-ChildItem -LiteralPath $backups -Directory).Count -eq $n) $r.Error

    # ---- Swap to storm-season: DaySpeed from the previous mood must go -----------------------------
    Start-Sleep -Milliseconds 1100
    $r = Run @{ Mood = 'storm-season' }
    Check 'Apply storm-season' $r.Ok $r.Error
    Check 'DaySpeed removed when the new mood does not set it' ($null -eq (ValueOf 'Environment.cfg' 'Clock.DaySpeed')) ''
    Check 'Heavy weight 5' ((ValueOf 'Environment.cfg' 'Weather.PrecipitateHeavyWeight') -eq '5') ''

    # ---- Event overlay and return ---------------------------------------------------------------
    Start-Sleep -Milliseconds 1100
    $r = Run @{ EventName = 'crown_night' }
    Check 'Event crown_night applies crown-night' ($r.Ok -and (ValueOf 'Environment.cfg' 'Atmosphere.MoonColor') -eq 'rgba(1,0.92,0.7,1)') $r.Error
    Start-Sleep -Milliseconds 1100
    $r = Run @{ EventName = 'war_arc' }
    Check 'Stacked overlay (war_arc -> ashfall)' ($r.Ok -and (ValueOf 'Environment.cfg' 'Atmosphere.FogDensity') -eq '1.45') $r.Error
    $st = Get-Content -LiteralPath (Join-Path $server '.realm-mood.json') -Raw | ConvertFrom-Json
    Check 'Stacked overlay still returns to the season mood' ($st.returnTo -eq 'storm-season') "returnTo=$($st.returnTo)"
    $r = Run @{ Status = $true }
    Check 'Status runs and reports ok lines' ($r.Ok -and $r.Out -match 'ashfall' -and $r.Out -notmatch 'MISSING') $r.Error
    Start-Sleep -Milliseconds 1100
    $r = Run @{ Return = $true }
    Check 'Return restores storm-season' ($r.Ok -and (ValueOf 'Environment.cfg' 'Weather.PrecipitateHeavyWeight') -eq '5') $r.Error
    $r = Run @{ Return = $true }
    Check 'Second Return refused (no overlay)' (-not $r.Ok) ''

    # ---- Season rotation --------------------------------------------------------------------------
    Start-Sleep -Milliseconds 1100
    $r = Run @{ Season = 7 }
    Check 'Season 7 -> long-winter (cycle of 4)' ($r.Ok -and $r.Out -match 'Season 7 -> long-winter') $r.Error

    # ---- Status detects drift (server commented a line) ------------------------------------------
    $p = Join-Path $mods 'Environment.cfg'
    (Get-Content -LiteralPath $p) | ForEach-Object { if ($_ -like 'Atmosphere.SunColor*') { '# ' + $_ } else { $_ } } | Set-Content -LiteralPath $p
    $r = Run @{ Status = $true }
    Check 'Status flags a commented line' ($r.Ok -and $r.Out -match 'MISSING') $r.Error

    # ---- Clear ------------------------------------------------------------------------------------
    Start-Sleep -Milliseconds 1100
    $r = Run @{ Clear = $true }
    Check 'Clear runs' $r.Ok $r.Error
    $left = @((Cfg 'Environment.cfg') | Where-Object { $_ -match '^\s*(Atmosphere|Weather|Clock)\.' })
    Check 'Clear removes every mood line' ($left.Count -eq 0) ($left -join ' | ')
    Check 'Clear keeps the commented line and the owner note' (((Cfg 'Environment.cfg') -contains '# owner note')) ''
    Check 'Clear removes the state file' (-not (Test-Path -LiteralPath (Join-Path $server '.realm-mood.json'))) ''

    # ---- RestoreLatest ----------------------------------------------------------------------------
    $r = Run @{ RestoreLatest = $true }
    Check 'RestoreLatest runs' $r.Ok $r.Error
    Check 'RestoreLatest brings back the pre-clear mood' ((ValueOf 'Environment.cfg' 'Weather.ClearWeight') -eq '3') "got $(ValueOf 'Environment.cfg' 'Weather.ClearWeight')"
    Check 'RestoreLatest brings back the state file' (Test-Path -LiteralPath (Join-Path $server '.realm-mood.json')) ''
    Check 'RestoreLatest made a pre-restore backup' (@(Get-ChildItem -LiteralPath $backups -Directory -Filter '*pre-restore*').Count -eq 1) ''

    # ---- Refusals ---------------------------------------------------------------------------------
    $steam = Join-Path (Join-Path (Join-Path $WorkDir 'steamapps') 'common') 'srv'
    New-Item -ItemType Directory -Force -Path (Join-Path $steam 'Mods') | Out-Null
    Set-Content -LiteralPath (Join-Path $steam '.realm-test-copy') -Value 'x'
    try { & $setMood -Mood long-winter -ServerRoot $steam -BackupDir $backups -PresetsDir $presets -Yes *>&1 | Out-Null; $ok = $false }
    catch { $ok = $_.Exception.Message -match 'Steam' }
    Check 'Refuses a Steam folder even with a marker' $ok ''

    $nomark = Join-Path $WorkDir 'nomarker'
    New-Item -ItemType Directory -Force -Path (Join-Path $nomark 'Mods') | Out-Null
    try { & $setMood -Mood long-winter -ServerRoot $nomark -BackupDir $backups -PresetsDir $presets -Yes *>&1 | Out-Null; $ok = $false }
    catch { $ok = $_.Exception.Message -match 'marker' }
    Check 'Refuses a folder without the test-copy marker' $ok ''

    $r = Run @{ Mood = 'long-winter'; BackupDir = (Join-Path $server 'bk') }
    Check 'Refuses a backup folder inside the server' ((-not $r.Ok) -and $r.Error -match 'outside') $r.Error

    $r = Run @{ Mood = 'no-such-mood' }
    Check 'Unknown mood refused' ((-not $r.Ok) -and $r.Error -match 'not found') $r.Error
    $r = Run @{ Mood = '..\..\etc' }
    Check 'Path-like mood id refused' ((-not $r.Ok) -and $r.Error -match 'not valid') $r.Error

    # Bad presets in a scratch presets folder.
    $bad = Join-Path $WorkDir 'badpresets'
    function BadPreset([string]$Id, [string[]]$Lines) {
        New-Item -ItemType Directory -Force -Path (Join-Path $bad $Id) | Out-Null
        Set-Content -LiteralPath (Join-Path (Join-Path $bad $Id) ($Id + '.cfg')) -Value $Lines
    }
    BadPreset 'unknown-key' @("Players.MaxHealth = '200'")
    BadPreset 'no-space' @("Atmosphere.FogDensity= '1.2'")
    BadPreset 'too-dark' @("Atmosphere.MoonColor = 'rgba(0.4,0.1,0.1,1)'")
    BadPreset 'too-foggy' @("Atmosphere.FogDensity = '2'")
    BadPreset 'comma' @("Atmosphere.FogDensity = '1,2'")
    BadPreset 'literal-dayspeed' @("Clock.DaySpeed = '0.5'")
    BadPreset 'dup' @("Weather.ClearWeight = '1'", "Weather.ClearWeight = '2'")
    BadPreset 'weight-range' @("Weather.ClearWeight = '11'")
    BadPreset 'all-zero' @("Weather.ClearWeight = '0'", "Weather.CloudyWeight = '0'")
    BadPreset 'three-part-color' @("Atmosphere.SunColor = 'rgba(1,1,1)'")
    foreach ($id in @('unknown-key', 'no-space', 'too-dark', 'too-foggy', 'comma', 'literal-dayspeed', 'dup', 'weight-range', 'all-zero', 'three-part-color')) {
        $r = Run @{ Mood = $id; PresetsDir = $bad }
        Check "Bad preset '$id' refused" (-not $r.Ok) $r.Out
    }
    $after = Get-Content -LiteralPath (Join-Path $mods 'Environment.cfg') -Raw
    Check 'Refused runs left Environment.cfg alone' ($after -match "Weather.ClearWeight = '3'") ''

    # Key the server does not declare: skipped, not guessed.
    BadPreset 'undeclared' @("Atmosphere.IslandLongitude = '10'", "Weather.ClearWeight = '4'")
    $edited = $defaults | Where-Object { $_ -notlike 'Atmosphere.IslandLongitude*' }
    Set-Content -LiteralPath (Join-Path $mods 'Environment.defaults.cfg') -Value $edited
    Start-Sleep -Milliseconds 1100
    $r = Run @{ Mood = 'undeclared'; PresetsDir = $bad }
    Check 'Undeclared key skipped, declared key written' ($r.Ok -and $null -eq (ValueOf 'Environment.cfg' 'Atmosphere.IslandLongitude') -and (ValueOf 'Environment.cfg' 'Weather.ClearWeight') -eq '4') $r.Error

    # Comma-decimal server: refuse everything.
    $comma = $defaults | ForEach-Object { $_ -replace "rgba\(1,1,1,1\)", 'rgba(1,0,1,0,1,0,1,0)' -replace "= '1'", "= '1,0'" }
    Set-Content -LiteralPath (Join-Path $mods 'Environment.defaults.cfg') -Value $comma
    $before = Get-Content -LiteralPath (Join-Path $mods 'Environment.cfg') -Raw
    $r = Run @{ Mood = 'long-winter' }
    Check 'Comma-decimal server refused' ((-not $r.Ok) -and $r.Error -match 'comma-decimal') $r.Error
    Check 'Comma-decimal refusal changed nothing' ($before -eq (Get-Content -LiteralPath (Join-Path $mods 'Environment.cfg') -Raw)) ''
}
finally {
    if (Test-Path -LiteralPath $WorkDir) { Remove-Item -LiteralPath $WorkDir -Recurse -Force }
}

Write-Host ''
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
