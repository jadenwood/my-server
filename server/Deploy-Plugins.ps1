<#
.SYNOPSIS
  Copies the repo's plugins\*.cs into the test server's oxide\plugins folder, and the data files some
  plugins read into oxide\data.

.DESCRIPTION
  Source defaults to ..\plugins next to this script (the repo's plugins folder).
  Target is <server>\oxide\plugins, or <server>\Saves\oxide\plugins if that is where Oxide created it.
  Oxide creates the folder on its first successful start, so run the Oxide smoke test first
  (or pass -CreateTarget to create <server>\oxide\plugins).
  Unchanged files are skipped. Files that would be replaced are copied to
  <server>\_realm-backups\plugins-<time>\ first (outside oxide\, so Oxide does not load them).
  Plugins in the target that are not in the repo are listed, never deleted.
  Oxide hot-reloads changed .cs files, so the server may stay running.

  Data files (ROADMAP STW-1; the same sets and rules as Realm Steward's Update plugins, launcher/lib/realm.js
  dataSets), copied before the plugins:
    art\sculptures\*.json                    -> oxide\data\RealmSculptor\
    art\paintings\RealmPainterArt.json       -> oxide\data\
    plugins\docs\RealmQuests\content\*.json -> oxide\data\RealmQuests\
    art\sculptures\sites\arrival.json        -> oxide\data\RealmArrival\site.json (renamed)
  Every source file must parse as a JSON object; a damaged one is refused and the server's copy is left as
  it is. A changed file is saved to <server>\_realm-backups\data-<time>\ first. Files Realm does not ship
  (an owner's own sculpture) are listed, never touched. Each copy is written to <name>.realm-part and
  renamed into place. A running plugin reads its data when it loads: reload the plugins whose data
  changed (oxide.reload <Plugin> in the server console) unless their .cs changed too.

.EXAMPLE
  .\Deploy-Plugins.ps1 -WhatIf
  .\Deploy-Plugins.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$PluginSource = (Join-Path (Split-Path $PSScriptRoot -Parent) 'plugins'),
    # The repository root that holds art\ and plugins\docs\ (the data files' sources).
    [string]$DataSource = (Split-Path $PSScriptRoot -Parent),
    [switch]$CreateTarget,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$root = Assert-RealmTestCopy $ServerRoot
$srcDir = Get-RealmFullPath $PluginSource
$sources = @(Get-ChildItem -LiteralPath $srcDir -Filter '*.cs' -File -ErrorAction SilentlyContinue)
if ($sources.Count -eq 0) { throw "No .cs files in $srcDir." }

$oxideDir = Get-RealmOxideDir $root
if ($oxideDir) {
    $target = Join-Path $oxideDir 'plugins'
} elseif ($CreateTarget) {
    $target = Join-Path $root 'oxide\plugins'
} else {
    throw "No oxide\plugins folder in $root. Start the server once with Oxide installed (smoke test part B), or pass -CreateTarget."
}

$copy = @()
foreach ($f in $sources) {
    $dest = Join-Path $target $f.Name
    $state = 'new'
    if (Test-Path -LiteralPath $dest) {
        $same = (Get-FileHash -LiteralPath $f.FullName).Hash -eq (Get-FileHash -LiteralPath $dest).Hash
        $state = if ($same) { 'unchanged' } else { 'changed' }
    }
    Write-Host ('  {0,-10} {1}' -f $state, $f.Name)
    if ($state -ne 'unchanged') { $copy += [pscustomobject]@{ Source = $f.FullName; Dest = $dest; State = $state } }
}
$repoNames = @($sources | ForEach-Object { $_.Name })
$extra = @(Get-ChildItem -LiteralPath $target -Filter '*.cs' -File -ErrorAction SilentlyContinue | Where-Object { $repoNames -notcontains $_.Name })
foreach ($x in $extra) { Write-Host ('  {0,-10} {1} (not in repo; left alone)' -f 'other', $x.Name) }

# ---------- data files (ROADMAP STW-1) ----------
# Keep this table in step with launcher/lib/realm.js dataSets. Only: the only file names shipped from Src
# (empty = every *.json). As: a file written under another name on the server (source name -> server name).
$dataSets = @(
    @{ Label = 'Monuments'; Plugin = 'RealmSculptor'; Src = 'art\sculptures'; Dest = 'RealmSculptor'; Only = @(); As = @{} },
    @{ Label = 'Sign art bundle'; Plugin = 'RealmPainter'; Src = 'art\paintings'; Dest = ''; Only = @('RealmPainterArt.json'); As = @{} },
    @{ Label = 'Quests and deeds'; Plugin = 'RealmQuests'; Src = 'plugins\docs\RealmQuests\content'; Dest = 'RealmQuests'; Only = @(); As = @{} },
    @{ Label = 'Arrival site plan'; Plugin = 'RealmArrival'; Src = 'art\sculptures\sites'; Dest = 'RealmArrival'; Only = @('arrival.json'); As = @{ 'arrival.json' = 'site.json' } }
)
$dataNameRe = '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\.json$'

function Test-RealmDataFile([string]$Path) {
    # $null when the file parses as a JSON object (not an array, number or null), otherwise the reason.
    try {
        $fi = Get-Item -LiteralPath $Path
        if ($fi.Length -gt 16MB) { return 'larger than 16 MB' }
        $text = [System.IO.File]::ReadAllText($fi.FullName)
    } catch {
        return ('unreadable (' + $_.Exception.Message + ')')
    }
    try {
        # Assigned first: Windows PowerShell 5.1 emits a JSON array as one object.
        $j = ConvertFrom-Json -InputObject $text
    } catch {
        return 'damaged JSON'
    }
    if ($j -isnot [System.Management.Automation.PSCustomObject]) { return 'not a JSON object' }
    return $null
}

$dataSrcRoot = Get-RealmFullPath $DataSource
if ($oxideDir) { $dataDir = Join-Path $oxideDir 'data' } else { $dataDir = Join-Path $root 'oxide\data' }
$dataCopy = @()
$dataInvalid = 0
foreach ($d in $dataSets) {
    $setSrc = Join-Path $dataSrcRoot $d.Src
    $setDest = $dataDir
    if ($d.Dest) { $setDest = Join-Path $dataDir $d.Dest }
    $names = @(Get-ChildItem -LiteralPath $setSrc -Filter '*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $dataNameRe } | ForEach-Object { $_.Name } | Sort-Object)
    if ($d.Only.Count -gt 0) { $names = @($names | Where-Object { $d.Only -contains $_ }) }
    if ($names.Count -eq 0) { Write-Host ('  {0,-10} {1} for {2}: no files in {3}' -f 'missing', $d.Label, $d.Plugin, $setSrc) -ForegroundColor Yellow; continue }
    $destNames = @()
    foreach ($n in $names) {
        $dn = $n
        if ($d.As.ContainsKey($n) -and ([string]$d.As[$n]) -match $dataNameRe) { $dn = [string]$d.As[$n] }
        $destNames += $dn
        $src = Join-Path $setSrc $n
        $dest = Join-Path $setDest $dn
        $rel = $dn
        if ($d.Dest) { $rel = $d.Dest + '\' + $dn }
        $shown = 'data\' + $rel
        if ($dn -ne $n) { $shown = $shown + ' (from ' + $n + ')' }
        $why = Test-RealmDataFile $src
        $state = 'new'
        if ($why) {
            $state = 'invalid'
        } elseif (Test-Path -LiteralPath $dest -PathType Leaf) {
            $same = (Get-FileHash -LiteralPath $src).Hash -eq (Get-FileHash -LiteralPath $dest).Hash
            $state = if ($same) { 'unchanged' } else { 'changed' }
        }
        if ($state -eq 'invalid') {
            $dataInvalid++
            Write-Host ('  {0,-10} {1}: {2}; not copied, the copy on the server is left as it is' -f 'refused', $shown, $why) -ForegroundColor Yellow
        } else {
            Write-Host ('  {0,-10} {1}' -f $state, $shown)
        }
        if ($state -eq 'new' -or $state -eq 'changed') {
            $dataCopy += [pscustomobject]@{ Source = $src; Dest = $dest; Rel = $rel; State = $state; Plugin = $d.Plugin }
        }
    }
    if ($d.Dest) {
        $otherData = @(Get-ChildItem -LiteralPath $setDest -Filter '*.json' -File -ErrorAction SilentlyContinue | Where-Object { $destNames -notcontains $_.Name })
        foreach ($x in $otherData) { Write-Host ('  {0,-10} data\{1}\{2} (not shipped by Realm; left alone)' -f 'other', $d.Dest, $x.Name) }
    }
}

if ($copy.Count -eq 0 -and $dataCopy.Count -eq 0) {
    Write-Host 'Nothing to deploy; all plugins and their data files are up to date.' -ForegroundColor Green
    if ($dataInvalid -gt 0) { Write-Host "$dataInvalid data file(s) refused (see above): fix them in the repo and deploy again." -ForegroundColor Yellow }
    return
}
$running = @(Get-RealmServerProcess $root).Count -gt 0
if ($running) { Write-Host '  Server is running: Oxide should hot-reload the copied plugins.' }

$q = "Copy $($copy.Count) plugin file(s) from '$srcDir' to '$target' and $($dataCopy.Count) data file(s) to '$dataDir'?"
if (-not (Confirm-RealmStep $PSCmdlet $target "Deploy $($copy.Count) plugin(s) and $($dataCopy.Count) data file(s)" $q $Yes)) { return }

# Data first: a plugin that loads before its data says "nothing to show" until it is reloaded.
$dataBackupDir = Join-Path $root ('_realm-backups\data-' + (Get-RealmTimestamp))
foreach ($c in $dataCopy) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $c.Dest) | Out-Null
    if ($c.State -eq 'changed') {
        $b = Join-Path $dataBackupDir $c.Rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $b) | Out-Null
        Copy-Item -LiteralPath $c.Dest -Destination $b
    }
    $part = $c.Dest + '.realm-part'
    Copy-Item -LiteralPath $c.Source -Destination $part -Force
    Move-Item -LiteralPath $part -Destination $c.Dest -Force
}
if ($dataCopy.Count -gt 0) {
    Write-Host "Deployed $($dataCopy.Count) data file(s) to $dataDir" -ForegroundColor Green
    if (Test-Path -LiteralPath $dataBackupDir) { Write-Host "Replaced data files saved in $dataBackupDir" }
}
if ($dataInvalid -gt 0) { Write-Host "$dataInvalid data file(s) refused (see above): fix them in the repo and deploy again." -ForegroundColor Yellow }

if ($copy.Count -eq 0) {
    Write-Host 'All plugins are up to date.' -ForegroundColor Green
} else {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $backupDir = Join-Path $root ('_realm-backups\plugins-' + (Get-RealmTimestamp))
    foreach ($c in $copy) {
        if ($c.State -eq 'changed') {
            New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
            Copy-Item -LiteralPath $c.Dest -Destination $backupDir
        }
        Copy-Item -LiteralPath $c.Source -Destination $c.Dest -Force
    }
    Write-Host "Deployed $($copy.Count) plugin(s) to $target" -ForegroundColor Green
    if (Test-Path -LiteralPath $backupDir) { Write-Host "Replaced versions saved in $backupDir" }
    Write-Host "Check the server console / oxide\logs for 'Loaded plugin' or compile errors."
}

# A running plugin reads its data when it loads. Oxide reloads a plugin whose .cs changed by itself; the others
# need a reload (Realm Steward sends it over the admin console).
$changedCs = @($copy | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Dest) })
$needReload = @($dataCopy | ForEach-Object { $_.Plugin } | Sort-Object -Unique | Where-Object { $changedCs -notcontains $_ })
if ($needReload.Count -gt 0) {
    $verb = 'When the server next starts they load the new data.'
    if ($running) { $verb = 'Type in the server console:' }
    Write-Host "Data changed for $($needReload -join ', '). $verb"
    if ($running) { foreach ($p in $needReload) { Write-Host "  oxide.reload $p" } }
}
