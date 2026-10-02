<#
.SYNOPSIS
  Swaps the active Realm "world mood" (a Mods preset from mods\presets\) on the TEST server copy.

.DESCRIPTION
  A mood is a set of override lines for the game's built-in Mods system: fog, sun and moon colour,
  weather weights and (relative) day speed. Every key a mood may use is proven from the game's code
  (docs/mods-keys-from-dll.md); this script refuses any other key.

  What a swap does, in order:
    1. Refuses anything that is not the test copy made by New-TestServer.ps1, anything inside a Steam
       folder, and a running server (the server rewrites Mods\*.cfg when it starts).
    2. Reads the mood file and checks every value: proven key, right type, inside the readability
       floors (moon luminance >= 0.55, sun and fog colour >= 0.6, FogDensity 0.5..1.5).
    3. Looks every key up in the server's own Mods\*.defaults.cfg. The Mods file that holds the
       Atmosphere./Weather./Clock. keys is named by Unity scene data, not the DLL [UNVERIFIED], so it
       is found here and never guessed. A key the server does not list is skipped with a warning.
       Refuses if the defaults look comma-decimal (the game parses with the current culture).
    4. Copies every Mods\*.cfg override file and the mood state file to a timestamped backup folder
       OUTSIDE the server folder (default G:\RealmTest\backups\moods-<time>-<mood>\).
    5. Removes every mood-managed key line from every Mods\<Name>.cfg (so keys of the previous mood
       that the new mood does not set fall back to the server default), then writes the new lines.
       Lines for other keys are never touched. Mods\*.defaults.cfg is never written.
    6. Records the active mood in <server>\.realm-mood.json.

  Windows PowerShell 5.1 compatible. Changes nothing outside the test server folder and the backup
  folder. Never touches the game files or the Steam copy.

.PARAMETER Mood
  Mood id = folder name under mods\presets (for example long-winter). See -List.
.PARAMETER Season
  Season number (RealmSeasons numbering, 1 = first). Picks the mood from mods\presets\rotation.json
  seasonCycle: cycle[(Season - 1) mod length].
.PARAMETER EventName
  (alias -Event)
  Event key from rotation.json "events" (crown_night, season_finale, war_arc, royal_tournament).
  Applies that overlay and remembers the mood it replaced, so -Return can put it back.
.PARAMETER Return
  Re-applies the mood that was active before the last -Event overlay.
.PARAMETER Clear
  Removes every mood line, so the server defaults apply again.
.PARAMETER Status
  Read-only: shows the active mood and checks its lines are still in Mods\<Name>.cfg.
.PARAMETER List
  Read-only: lists the moods, their weather odds and the rotation.
.PARAMETER RestoreLatest
  Puts back the newest backup this script made (after first backing up the current state).

.EXAMPLE
  .\Set-Mood.ps1 -List
  .\Set-Mood.ps1 -Mood long-winter -WhatIf
  .\Set-Mood.ps1 -Mood long-winter
  .\Set-Mood.ps1 -Season 3
  .\Set-Mood.ps1 -Event crown_night      # Friday, before the restart
  .\Set-Mood.ps1 -Return                 # Sunday, after Crown Night
  .\Set-Mood.ps1 -Status
  .\Set-Mood.ps1 -RestoreLatest
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium', DefaultParameterSetName = 'Mood')]
param(
    [Parameter(ParameterSetName = 'Mood', Mandatory = $true, Position = 0)][string]$Mood,
    [Parameter(ParameterSetName = 'Season', Mandatory = $true)][int]$Season,
    [Parameter(ParameterSetName = 'Event', Mandatory = $true)][Alias('Event')][string]$EventName,
    [Parameter(ParameterSetName = 'Return', Mandatory = $true)][switch]$Return,
    [Parameter(ParameterSetName = 'Clear', Mandatory = $true)][switch]$Clear,
    [Parameter(ParameterSetName = 'Status', Mandatory = $true)][switch]$Status,
    [Parameter(ParameterSetName = 'List', Mandatory = $true)][switch]$List,
    [Parameter(ParameterSetName = 'Restore', Mandatory = $true)][switch]$RestoreLatest,
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$BackupDir = 'G:\RealmTest\backups',
    [string]$PresetsDir = (Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'mods') 'presets'),
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$Inv = [System.Globalization.CultureInfo]::InvariantCulture
$StateName = '.realm-mood.json'
$BackupPrefix = 'moods-'

# ---- The only keys a mood may set: proven in docs/mods-keys-from-dll.md section 3 -----------------
# Type: float | color | weight. Min/Max: hard limits (readability floors included). Scale: may be
# given relatively with "#@scale" (the server default times a factor).
$MoodKeys = @{
    'Atmosphere.FogDensity'           = @{ Type = 'float';  Min = 0.5;  Max = 1.5; Scale = $true }
    'Atmosphere.FogColor'             = @{ Type = 'color';  MinLum = 0.6 }
    'Atmosphere.SunColor'             = @{ Type = 'color';  MinLum = 0.6 }
    'Atmosphere.MoonColor'            = @{ Type = 'color';  MinLum = 0.55 }
    'Atmosphere.IslandLatitude'       = @{ Type = 'float';  Min = -90;  Max = 90;  Scale = $false }
    'Atmosphere.IslandLongitude'      = @{ Type = 'float';  Min = -90;  Max = 90;  Scale = $false }
    'Weather.ClearWeight'             = @{ Type = 'weight' }
    'Weather.CloudyWeight'            = @{ Type = 'weight' }
    'Weather.PrecipitateLowWeight'    = @{ Type = 'weight' }
    'Weather.PrecipitateMediumWeight' = @{ Type = 'weight' }
    'Weather.PrecipitateHeavyWeight'  = @{ Type = 'weight' }
    'Clock.DaySpeed'                  = @{ Type = 'float';  Min = 0.25; Max = 4;   Scale = $true; ScaleOnly = $true; ScaleMin = 0.5; ScaleMax = 2 }
}
$WeightOrder = @('Weather.ClearWeight', 'Weather.CloudyWeight', 'Weather.PrecipitateLowWeight', 'Weather.PrecipitateMediumWeight', 'Weather.PrecipitateHeavyWeight')
# Values the colour/fog presets were tuned against (C# initializers in the DLL; real scene UNVERIFIED).
$AssumedDefaults = @{
    'Atmosphere.FogDensity' = 1.0
    'Atmosphere.FogColor'   = 'rgba(1,1,1,1)'
    'Atmosphere.SunColor'   = 'rgba(1,1,1,1)'
    'Atmosphere.MoonColor'  = 'rgba(1,1,1,1)'
}

# Game line: key = 'value' (space before '=', the game drops the character right before it).
$LineRe  = '^\s*([^#=\s][^=]*?)\s+=\s*''?([^''#]*)''?'
$ScaleRe = '^\s*#@scale\s+([^=\s]+)\s+=\s*''?([^''#]*)''?'

# ---------------------------------------------------------------------------------------------------
function Assert-MoodNotSteam([string]$Path, [string]$What) {
    # Separator-neutral on purpose (also covers paths typed with '/'). RealmCommon's
    # Assert-RealmTestCopy checks again with its own rules and requires the test-copy marker.
    $full = Get-RealmFullPath $Path
    if ($full -match '(?i)(^|[\\/])(steamapps|SteamLibrary|Steam)([\\/]|$)') {
        throw "Refusing: $What '$full' is inside a Steam folder. Set-Mood.ps1 only changes the test copy."
    }
    if ($full -match '(?i)Reign Of Kings Dedicated Server') {
        throw "Refusing: $What '$full' looks like the Steam dedicated server folder."
    }
    return $full
}

function Test-MoodPathInside([string]$Child, [string]$Parent) {
    $sep = [string][System.IO.Path]::DirectorySeparatorChar
    $c = (Get-RealmFullPath $Child) + $sep
    $p = (Get-RealmFullPath $Parent) + $sep
    return $c.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}

function ConvertTo-MoodNumber([string]$Text) {
    $d = 0.0
    $styles = [System.Globalization.NumberStyles]::Float
    if (-not [double]::TryParse($Text.Trim(), $styles, $Inv, [ref]$d)) { return $null }
    return $d
}

function Format-MoodNumber([double]$Value) { return $Value.ToString('0.####', $Inv) }

function ConvertFrom-MoodColor([string]$Text) {
    # Same rule as ColorModParseable.ParseFromString: trim "()rgba", split on ',', four floats.
    $t = $Text.Trim().Trim([char[]]'()rgba ')
    $parts = $t.Split(',')
    if ($parts.Length -ne 4) { return $null }
    $vals = @()
    foreach ($p in $parts) {
        $n = ConvertTo-MoodNumber $p
        if ($null -eq $n) { return $null }
        $vals += $n
    }
    return , $vals
}

function Get-MoodLuminance($Rgba) { return 0.2126 * $Rgba[0] + 0.7152 * $Rgba[1] + 0.0722 * $Rgba[2] }

function Get-MoodWeatherOdds([int[]]$W) {
    # Exact odds of Weather.ChangeTheWeather [DEC]: each weight * Random.Range(0,100); the strictly
    # highest product wins; any tie keeps the current weather. Returns 6 numbers: 5 weathers + no change.
    $odds = @()
    for ($i = 0; $i -lt 5; $i++) {
        $p = 0.0
        for ($r = 0; $r -lt 100; $r++) {
            $v = $W[$i] * $r
            $q = 0.01
            for ($j = 0; $j -lt 5; $j++) {
                if ($j -eq $i) { continue }
                if ($v -eq 0) { $c = 0 }
                elseif ($W[$j] -eq 0) { $c = 100 }
                else { $c = [Math]::Min(100, [int][Math]::Ceiling($v / [double]$W[$j])) }
                $q = $q * $c / 100.0
            }
            $p += $q
        }
        $odds += $p
    }
    $sum = 0.0; foreach ($o in $odds) { $sum += $o }
    $odds += [Math]::Max(0.0, 1.0 - $sum)
    return , $odds
}

function Format-MoodOdds($Odds) {
    $names = @('clear', 'cloudy', 'light rain', 'medium rain', 'heavy rain', 'no change')
    $out = @()
    for ($i = 0; $i -lt 6; $i++) { $out += ('{0} {1}%' -f $names[$i], ($Odds[$i] * 100).ToString('0.0', $Inv)) }
    return $out -join ', '
}

function Get-MoodIds([string]$Dir) {
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { throw "Presets folder '$Dir' not found." }
    $ids = @()
    foreach ($d in @(Get-ChildItem -LiteralPath $Dir -Directory)) {
        if (Test-Path -LiteralPath (Join-Path $d.FullName ($d.Name + '.cfg')) -PathType Leaf) { $ids += $d.Name }
    }
    return $ids | Sort-Object
}

function Read-MoodRotation([string]$Dir) {
    $f = Join-Path $Dir 'rotation.json'
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "Rotation file '$f' not found." }
    return (Get-Content -LiteralPath $f -Raw) | ConvertFrom-Json
}

function Get-MoodProp($Obj, [string]$Name) {
    # StrictMode-safe property read on a ConvertFrom-Json object.
    if ($null -eq $Obj) { return $null }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Read-MoodPreset([string]$Dir, [string]$Id) {
    # Parses and validates one mood file. Throws on any problem; returns an ordered list of entries
    # @{ Key; Value; Scale(bool) } where Value is the literal (or the factor when Scale).
    if ($Id -notmatch '^[a-z0-9][a-z0-9-]*$') { throw "Mood id '$Id' is not valid (lower-case letters, digits and '-')." }
    $file = Join-Path (Join-Path $Dir $Id) ($Id + '.cfg')
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Mood '$Id' not found ($file). Known moods: $((Get-MoodIds $Dir) -join ', ')."
    }
    $entries = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $n = 0
    foreach ($line in Get-Content -LiteralPath $file) {
        $n++
        $isScale = $false
        $m = [regex]::Match($line, $ScaleRe)
        if ($m.Success) { $isScale = $true }
        else {
            if ($line -match '^\s*(#|$)') { continue }
            if ($line -match '^\s*[^#=\s][^=]*[^\s=]=') { throw "${file}:${n}: no space before '=' (the game would drop the last letter of the key)." }
            $m = [regex]::Match($line, $LineRe)
            if (-not $m.Success) { throw "${file}:${n}: not a key = 'value' line: $line" }
        }
        $key = $m.Groups[1].Value.Trim()
        $val = $m.Groups[2].Value.Trim()
        if (-not $MoodKeys.ContainsKey($key)) {
            throw "${file}:${n}: '$key' is not a proven mood key (docs/mods-keys-from-dll.md). Allowed: $(($MoodKeys.Keys | Sort-Object) -join ', ')."
        }
        if ($seen.ContainsKey($key)) { throw "${file}:${n}: '$key' is set twice (the game would rename the second one and ignore it)." }
        $seen[$key] = $true
        $spec = $MoodKeys[$key]
        if ($isScale) {
            if (-not $spec.Scale) { throw "${file}:${n}: '$key' cannot be given with #@scale." }
            $f = ConvertTo-MoodNumber $val
            $lo = 0.5; $hi = 2.0
            if ($spec.ContainsKey('ScaleMin')) { $lo = $spec.ScaleMin; $hi = $spec.ScaleMax }
            if ($null -eq $f -or $f -lt $lo -or $f -gt $hi) { throw "${file}:${n}: #@scale factor for '$key' must be a number from $lo to $hi (got '$val')." }
            $entries.Add(@{ Key = $key; Value = (Format-MoodNumber $f); Scale = $true })
            continue
        }
        if ($spec.ContainsKey('ScaleOnly') -and $spec.ScaleOnly) {
            throw "${file}:${n}: '$key' must be relative: write  #@scale $key = '<factor>'. The scene's real value is UNVERIFIED, so a literal could make days many times longer or shorter."
        }
        switch ($spec.Type) {
            'float' {
                $d = ConvertTo-MoodNumber $val
                if ($null -eq $d -or $val -match ',') { throw "${file}:${n}: '$key' needs a dot-decimal number (got '$val')." }
                if ($d -lt $spec.Min -or $d -gt $spec.Max) { throw "${file}:${n}: '$key' = $val is outside $($spec.Min)..$($spec.Max) (readability floor)." }
            }
            'weight' {
                if ($val -notmatch '^\d{1,2}$' -or [int]$val -gt 10) { throw "${file}:${n}: '$key' must be a whole number 0..10 (got '$val')." }
            }
            'color' {
                if ($val -notmatch '^rgba\(') { throw "${file}:${n}: '$key' must be rgba(r,g,b,a) (got '$val')." }
                $c = ConvertFrom-MoodColor $val
                if ($null -eq $c) { throw "${file}:${n}: '$key' needs four dot-decimal numbers in rgba(...) (got '$val')." }
                foreach ($x in $c) { if ($x -lt 0 -or $x -gt 1) { throw "${file}:${n}: '$key' components must be 0..1 (got '$val')." } }
                $lum = Get-MoodLuminance $c
                if ($lum -lt $spec.MinLum) {
                    throw "${file}:${n}: '$key' luminance $($lum.ToString('0.00', $Inv)) is below the readability floor $($spec.MinLum)."
                }
            }
        }
        $entries.Add(@{ Key = $key; Value = $val; Scale = $false })
    }
    if ($entries.Count -eq 0) { throw "$file has no mood lines." }
    $hasWeight = $false; $nonZero = $false
    foreach ($e in $entries) {
        if ($WeightOrder -contains $e.Key) { $hasWeight = $true; if ([int]$e.Value -gt 0) { $nonZero = $true } }
    }
    if ($hasWeight -and -not $nonZero) { throw "${file}: every weather weight is 0, so the weather would never change." }
    return , $entries
}

function Get-MoodPresetOdds($Entries) {
    $w = @()
    foreach ($k in $WeightOrder) {
        $found = $null
        foreach ($e in $Entries) { if ($e.Key -eq $k) { $found = $e } }
        if ($null -eq $found) { return $null }
        $w += [int]$found.Value
    }
    return Get-MoodWeatherOdds $w
}

function Get-MoodDeclared([string]$ModsDir) {
    # key -> @{ Handler; Default } from every Mods\*.defaults.cfg the server wrote.
    $declared = @{}
    foreach ($d in @(Get-ChildItem -LiteralPath $ModsDir -File -Filter '*.defaults.cfg' | Where-Object { $_.Extension -eq '.cfg' })) {
        $handler = $d.Name.Substring(0, $d.Name.Length - '.defaults.cfg'.Length)
        foreach ($line in Get-Content -LiteralPath $d.FullName) {
            $m = [regex]::Match($line, $LineRe)
            if (-not $m.Success) { continue }
            $k = $m.Groups[1].Value.Trim()
            if (-not $declared.ContainsKey($k)) { $declared[$k] = @{ Handler = $handler; Default = $m.Groups[2].Value.Trim() } }
        }
    }
    return $declared
}

function Assert-MoodDotDecimal($Declared) {
    # The game reads AND writes colours/floats with the current culture [DEC ColorModParseable,
    # SystemUtil.ConvertToType]. A comma-decimal server would misread every value this script writes.
    foreach ($k in $MoodKeys.Keys) {
        if (-not $Declared.ContainsKey($k)) { continue }
        $v = $Declared[$k].Default
        $bad = $false
        if ($MoodKeys[$k].Type -eq 'color') { if (($v.Split(',').Length) -ne 4) { $bad = $true } }
        elseif ($v -match ',') { $bad = $true }
        if ($bad) {
            throw "The server wrote '$k = $v' in $($Declared[$k].Handler).defaults.cfg: it is running with a comma-decimal locale. Run it under a dot-decimal format first (docs/mods-keys-from-dll.md section 3.4). Nothing was changed."
        }
    }
}

function Get-MoodOverrideFiles([string]$ModsDir) {
    # Mods\<Name>.cfg override files only (not *.defaults.cfg).
    # The Extension test guards against Windows' legacy wildcard matching (*.cfg also matching *.cfgx)
    # and keeps Apply-Preset.ps1's <Name>.cfg.realm-backup files out.
    @(Get-ChildItem -LiteralPath $ModsDir -File -Filter '*.cfg' | Where-Object { $_.Extension -eq '.cfg' -and $_.Name -notlike '*.defaults.cfg' })
}

function Read-MoodLines([string]$Path) {
    $list = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        foreach ($l in ((Read-RealmTextFile $Path).Text -split "`r?`n")) { $list.Add($l) }
        while ($list.Count -gt 0 -and $list[$list.Count - 1] -eq '') { $list.RemoveAt($list.Count - 1) }
    }
    return , $list
}

function Get-MoodLineKey([string]$Line) {
    $m = [regex]::Match($Line, $LineRe)
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return $null
}

function Write-MoodFile([string]$Path, $Lines) {
    # ASCII + CRLF: every mood character is ASCII; the game opens the file as UTF-8 (File.OpenText).
    $text = ''
    if ($Lines.Count -gt 0) { $text = ($Lines -join "`r`n") + "`r`n" }
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.Encoding]::ASCII)
}

function Read-MoodState([string]$Root) {
    $f = Join-Path $Root $StateName
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $f -Raw) | ConvertFrom-Json }
    catch { Write-Warning "$f is not valid JSON; treating the mood as unknown."; return $null }
}

function New-MoodBackup([string]$Root, [string]$ModsDir, [string]$BackupRoot, [string]$Label) {
    # Copies every Mods override file + the state file into a new folder. -RestoreLatest treats an
    # override file missing from the backup as "did not exist then" and removes it. Returns the folder.
    $safe = $Label -replace '[^A-Za-z0-9_-]', '_'
    $dest = Join-Path $BackupRoot ($BackupPrefix + (Get-RealmTimestamp) + '-' + $safe)
    $i = 1
    while (Test-Path -LiteralPath $dest) { $dest = Join-Path $BackupRoot ($BackupPrefix + (Get-RealmTimestamp) + '-' + $safe + '-' + $i); $i++ }
    New-Item -ItemType Directory -Force -Path (Join-Path $dest 'Mods') | Out-Null
    foreach ($f in (Get-MoodOverrideFiles $ModsDir)) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path (Join-Path $dest 'Mods') $f.Name) }
    $state = Join-Path $Root $StateName
    if (Test-Path -LiteralPath $state -PathType Leaf) { Copy-Item -LiteralPath $state -Destination (Join-Path $dest $StateName) }
    return $dest
}

# ---------------------------------------------------------------------------------------------------
# -List: read-only, needs no server.
if ($PSCmdlet.ParameterSetName -eq 'List') {
    $rot = Read-MoodRotation $PresetsDir
    $moods = Get-MoodProp $rot 'moods'
    foreach ($id in (Get-MoodIds $PresetsDir)) {
        $meta = Get-MoodProp $moods $id
        $name = $id; $kind = '?'; $summary = ''
        if ($null -ne $meta) { $name = Get-MoodProp $meta 'name'; $kind = Get-MoodProp $meta 'kind'; $summary = Get-MoodProp $meta 'summary' }
        $entries = Read-MoodPreset $PresetsDir $id
        Write-Host ("{0,-18} {1,-18} [{2}] {3}" -f $id, $name, $kind, $summary) -ForegroundColor Cyan
        $odds = Get-MoodPresetOdds $entries
        if ($null -ne $odds) { Write-Host ('    weather: ' + (Format-MoodOdds $odds)) }
        foreach ($e in $entries) {
            if ($e.Scale) { Write-Host ("    {0,-32} x {1} (server default times this)" -f $e.Key, $e.Value) }
            else { Write-Host ("    {0,-32} {1}" -f $e.Key, $e.Value) }
        }
    }
    $cycle = @(Get-MoodProp $rot 'seasonCycle')
    Write-Host ''
    Write-Host ('Season cycle: ' + ($cycle -join ' -> ') + ' -> (repeat)')
    $events = Get-MoodProp $rot 'events'
    if ($null -ne $events) { foreach ($p in $events.PSObject.Properties) { Write-Host ("Event {0,-18} -> {1}" -f $p.Name, $p.Value) } }
    return
}

# ---- Everything below touches (or reads) the test server ------------------------------------------
$null = Assert-MoodNotSteam $ServerRoot 'ServerRoot'
$root = Assert-RealmTestCopy $ServerRoot
$modsDir = Join-Path $root 'Mods'
if (-not (Test-Path -LiteralPath $modsDir -PathType Container)) {
    throw "$modsDir does not exist. Start and stop the test server twice so it generates the Mods folder."
}
$state = Read-MoodState $root

if ($PSCmdlet.ParameterSetName -eq 'Status') {
    if ($null -eq $state) { Write-Host 'No mood recorded by Set-Mood.ps1 on this server (server defaults, or set by hand / Apply-Preset.ps1).'; return }
    Write-Host ("Active mood: {0}  (set {1})" -f (Get-MoodProp $state 'mood'), (Get-MoodProp $state 'appliedAtUtc')) -ForegroundColor Cyan
    $ret = Get-MoodProp $state 'returnTo'
    if ($ret) { Write-Host "Overlay for event '$(Get-MoodProp $state 'event')'. -Return goes back to: $ret" }
    $drift = 0
    $written = Get-MoodProp $state 'written'
    if ($null -ne $written) {
        foreach ($w in @($written)) {
            $target = Join-Path $modsDir ((Get-MoodProp $w 'handler') + '.cfg')
            $found = $null
            foreach ($l in (Read-MoodLines $target)) { if ((Get-MoodLineKey $l) -eq (Get-MoodProp $w 'key')) { $found = [regex]::Match($l, $LineRe).Groups[2].Value.Trim() } }
            $mark = 'ok'
            if ($null -eq $found) { $mark = 'MISSING (server dropped or commented it?)'; $drift++ }
            elseif ($found -ne (Get-MoodProp $w 'value')) { $mark = "CHANGED to '$found'"; $drift++ }
            Write-Host ("  {0,-32} {1,-26} [{2}.cfg] {3}" -f (Get-MoodProp $w 'key'), (Get-MoodProp $w 'value'), (Get-MoodProp $w 'handler'), $mark)
        }
    }
    if ($drift -gt 0) { Write-Warning "$drift line(s) differ from what Set-Mood.ps1 wrote." }
    $running = @(Get-RealmServerProcess $root).Count -gt 0
    if ($running) { Write-Host 'Server is running: values apply as written at its last start.' }
    return
}

Assert-RealmServerStopped $root
$backupRoot = Assert-MoodNotSteam $BackupDir 'BackupDir'
if (Test-MoodPathInside $backupRoot $root) { throw "Refusing: -BackupDir must be outside the server folder." }

if ($PSCmdlet.ParameterSetName -eq 'Restore') {
    if (-not (Test-Path -LiteralPath $backupRoot -PathType Container)) { throw "No backups in $backupRoot." }
    $latest = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Filter ($BackupPrefix + '*') |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Mods') -PathType Container } |
        Sort-Object Name -Descending)
    $latest = @($latest | Where-Object { $_.Name -notlike '*-pre-restore*' })
    if ($latest.Count -eq 0) { throw "No Set-Mood backups (${BackupPrefix}*) in $backupRoot." }
    $src = $latest[0].FullName
    if (-not (Confirm-RealmStep $PSCmdlet $modsDir "Restore Mods override files from $src" "Restore the Mods override files from ${src}? The current ones are backed up first." $Yes.IsPresent)) { return }
    $pre = New-MoodBackup $root $modsDir $backupRoot 'pre-restore'
    Write-Host "Current state backed up to $pre"
    $srcMods = Join-Path $src 'Mods'
    $keep = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $srcMods -File)) { $keep[$f.Name] = $true; Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $modsDir $f.Name) -Force }
    foreach ($f in (Get-MoodOverrideFiles $modsDir)) {
        if (-not $keep.ContainsKey($f.Name)) { Remove-Item -LiteralPath $f.FullName; Write-Host "Removed $($f.Name) (it did not exist at backup time)" }
    }
    $srcState = Join-Path $src $StateName
    $dstState = Join-Path $root $StateName
    if (Test-Path -LiteralPath $srcState) { Copy-Item -LiteralPath $srcState -Destination $dstState -Force }
    elseif (Test-Path -LiteralPath $dstState) { Remove-Item -LiteralPath $dstState -Force }
    Write-Host "Restored Mods override files from $src" -ForegroundColor Green
    return
}

# ---- Work out which mood to apply -----------------------------------------------------------------
$rotation = $null
$eventKey = $null
$returnTo = $null
switch ($PSCmdlet.ParameterSetName) {
    'Mood'   { $target = $Mood }
    'Clear'  { $target = $null }
    'Season' {
        if ($Season -lt 1) { throw '-Season must be 1 or more.' }
        $rotation = Read-MoodRotation $PresetsDir
        $cycle = @(Get-MoodProp $rotation 'seasonCycle')
        if ($cycle.Count -eq 0) { throw 'rotation.json has an empty seasonCycle.' }
        $target = [string]$cycle[($Season - 1) % $cycle.Count]
        Write-Host "Season $Season -> $target"
    }
    'Event'  {
        $rotation = Read-MoodRotation $PresetsDir
        $target = Get-MoodProp (Get-MoodProp $rotation 'events') $EventName
        if (-not $target) { throw "Unknown event '$EventName'. See .\Set-Mood.ps1 -List." }
        $eventKey = $EventName
        $returnTo = ''
        if ($null -ne $state) {
            # Keep the original season mood across stacked overlays.
            $returnTo = Get-MoodProp $state 'returnTo'
            if (-not $returnTo) { $returnTo = Get-MoodProp $state 'mood' }
        }
        if (-not $returnTo) { $returnTo = '(none)' }
        Write-Host "Event $EventName -> $target (afterwards -Return goes back to $returnTo)"
    }
    'Return' {
        if ($null -eq $state -or -not (Get-MoodProp $state 'returnTo')) { throw 'No event overlay is recorded; nothing to return from.' }
        $target = Get-MoodProp $state 'returnTo'
        if ($target -eq '(none)') { $target = $null }
        Write-Host "Returning to $(if ($target) { $target } else { 'server defaults' })"
    }
}

$entries = New-Object System.Collections.Generic.List[object]
if ($target) { $entries = Read-MoodPreset $PresetsDir $target }

$declared = Get-MoodDeclared $modsDir
Assert-MoodDotDecimal $declared

# Resolve every entry to a literal line in a known handler file.
$plan = New-Object System.Collections.Generic.List[object]
$skipped = 0
foreach ($e in $entries) {
    if (-not $declared.ContainsKey($e.Key)) {
        Write-Warning "$($e.Key): not declared in any Mods\*.defaults.cfg on this server. Skipped (never guessed)."
        $skipped++
        continue
    }
    $info = $declared[$e.Key]
    $value = $e.Value
    $note = ''
    if ($e.Scale) {
        $def = ConvertTo-MoodNumber $info.Default
        if ($null -eq $def) { Write-Warning "$($e.Key): server default '$($info.Default)' is not a number; skipped."; $skipped++; continue }
        $value = Format-MoodNumber ($def * (ConvertTo-MoodNumber $e.Value))
        $spec = $MoodKeys[$e.Key]
        $v = ConvertTo-MoodNumber $value
        if ($v -lt $spec.Min -or $v -gt $spec.Max) { throw "$($e.Key): default $($info.Default) x $($e.Value) = $value is outside $($spec.Min)..$($spec.Max). Nothing was changed." }
        $note = "  (= default x $($e.Value))"
    } elseif ($AssumedDefaults.ContainsKey($e.Key)) {
        $a = $AssumedDefaults[$e.Key]
        $d = $info.Default -replace '\s', ''
        $differs = $false
        if ($a -is [double]) { $dn = ConvertTo-MoodNumber $d; $differs = ($null -eq $dn -or [Math]::Abs($dn - $a) -gt 0.0001) }
        else {
            $dc = ConvertFrom-MoodColor $d; $ac = ConvertFrom-MoodColor $a
            if ($null -eq $dc) { $differs = $true } else { for ($k = 0; $k -lt 4; $k++) { if ([Math]::Abs($dc[$k] - $ac[$k]) -gt 0.0001) { $differs = $true } } }
        }
        if ($differs) { $note = "  <-- server default is not $a; the mood was tuned for $a, check the look in game" }
    }
    Write-Host ("{0,-32} {1,-22} default {2,-24} mood {3}{4}" -f $e.Key, "[$($info.Handler).cfg]", $info.Default, $value, $note)
    $plan.Add(@{ Key = $e.Key; Value = $value; Handler = $info.Handler; Line = ("{0} = '{1}'" -f $e.Key, $value) })
}
if ($target -and $plan.Count -eq 0) { throw "None of the keys of '$target' are declared by this server. Nothing was changed. See docs/mods-keys-from-dll.md section 5." }
$odds = $null
if ($target) { $odds = Get-MoodPresetOdds $entries }
if ($null -ne $odds) { Write-Host ('Weather odds per change: ' + (Format-MoodOdds $odds)) }

# Build the new content of every affected Mods\<Name>.cfg: strip all mood-managed keys, add the plan.
$newContent = @{}
$handlers = @{}
foreach ($f in (Get-MoodOverrideFiles $modsDir)) { $handlers[$f.Name.Substring(0, $f.Name.Length - 4)] = $true }
foreach ($p in $plan) { $handlers[$p.Handler] = $true }
$removedCount = 0
foreach ($h in $handlers.Keys) {
    $path = Join-Path $modsDir ($h + '.cfg')
    $old = Read-MoodLines $path
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($l in $old) {
        $k = Get-MoodLineKey $l
        if ($null -ne $k -and $MoodKeys.ContainsKey($k)) { $removedCount++; continue }
        $lines.Add($l)
    }
    foreach ($p in $plan) { if ($p.Handler -eq $h) { $lines.Add($p.Line) } }
    $exists = Test-Path -LiteralPath $path -PathType Leaf
    $same = $exists -and ($old.Count -eq $lines.Count)
    if ($same) { for ($i = 0; $i -lt $lines.Count; $i++) { if ($old[$i] -ne $lines[$i]) { $same = $false; break } } }
    if (-not $same) { $newContent[$path] = $lines }
}

$label = 'clear'
if ($target) { $label = $target }
$desc = "Apply mood '$label': $($plan.Count) line(s) in $($newContent.Count) file(s), removing $removedCount old mood line(s)"
if ($newContent.Count -eq 0) {
    Write-Host "Mods files already match mood '$label'; nothing to write." -ForegroundColor Green
} else {
    if (-not (Confirm-RealmStep $PSCmdlet $modsDir $desc "$desc. A backup is made first. Continue?" $Yes.IsPresent)) { return }
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $bk = New-MoodBackup $root $modsDir $backupRoot $label
    Write-Host "Backup: $bk"
    foreach ($path in $newContent.Keys) {
        Write-MoodFile $path $newContent[$path]
        Write-Host "Wrote $path" -ForegroundColor Green
    }
}

# Record the active mood (also when nothing changed, so -Status is accurate).
if ($newContent.Count -gt 0 -or $PSCmdlet.ShouldProcess((Join-Path $root $StateName), 'Record active mood')) {
    $statePath = Join-Path $root $StateName
    if ($target) {
        $written = @()
        foreach ($p in $plan) { $written += New-Object PSObject -Property @{ key = $p.Key; value = $p.Value; handler = $p.Handler } }
        $obj = New-Object PSObject -Property @{
            mood = $target
            appliedAtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
            event = $eventKey
            returnTo = $returnTo
            written = $written
        }
        ($obj | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $statePath -Encoding ASCII
    } elseif (Test-Path -LiteralPath $statePath) {
        Remove-Item -LiteralPath $statePath -Force
    }
}

if ($skipped -gt 0) { Write-Warning "$skipped key(s) skipped. See docs/mods-keys-from-dll.md section 5." }
Write-Host "Start the server, then run .\Set-Mood.ps1 -Status: the server rewrites Mods\<Name>.cfg on start-up and a key it does not recognise comes back as a # comment [inferred from code, UNVERIFIED in practice]. Players get the values when they join."
