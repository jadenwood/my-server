<#
.SYNOPSIS
  Restores Saves and oxide\data from a zip made by Backup-Saves.ps1 into the test server.

.DESCRIPTION
  1. Checks the zip: it must contain realm-backup.json, and every entry must be under Saves/ or oxide/data/
     (no absolute paths, no '..').
  2. Takes a fresh safety backup of the current state with Backup-Saves.ps1 (label 'pre-restore').
  3. Moves the current Saves and oxide\data aside to <server>\_realm-backups\pre-restore-<time>\ (nothing is deleted).
  4. Extracts the zip.
  Refuses while the server is running.

.EXAMPLE
  .\Restore-Saves.ps1 -ZipPath G:\RealmTest\backups\realm-saves-20261002-120000.zip -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$BackupDir = 'G:\RealmTest\backups',
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$root = Assert-RealmTestCopy $ServerRoot
Assert-RealmServerStopped $root
$zipFull = Get-RealmFullPath $ZipPath
if (-not (Test-Path -LiteralPath $zipFull -PathType Leaf)) { throw "Zip not found: $zipFull" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$entries = @{}
$topLevels = @{}
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
try {
    $hasManifest = $false
    foreach ($e in $zip.Entries) {
        $name = $e.FullName.Replace('\', '/')
        if ($name -eq 'realm-backup.json') { $hasManifest = $true; continue }
        if ($name -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))') { throw "Unsafe entry in zip: $name" }
        if ($name -notmatch '^(Saves|oxide/data)/') { throw "Unexpected entry in zip (not under Saves/ or oxide/data/): $name" }
        if ($name.EndsWith('/')) { continue }
        $topLevels[$(if ($name -like 'Saves/*') { 'Saves' } else { 'oxide\data' })] = $true
        $entries[$name] = $true
    }
    if (-not $hasManifest) { throw "Zip has no realm-backup.json; it was not made by Backup-Saves.ps1." }
} finally { $zip.Dispose() }
if ($entries.Count -eq 0) { throw 'Zip contains no files.' }

$stamp = Get-RealmTimestamp
$aside = Join-Path $root "_realm-backups\pre-restore-$stamp"
Write-Host "  Restore from : $zipFull ($($entries.Count) files)"
Write-Host "  Replaces     : $(@($topLevels.Keys) -join ', ') in $root"
Write-Host "  Current state: zipped to $BackupDir and moved to $aside"

$question = "Replace the test server's $(@($topLevels.Keys) -join ' and ') with the contents of this backup?"
if (-not (Confirm-RealmStep $PSCmdlet $root "Restore $zipFull" $question $Yes)) { return }

$existing = @($topLevels.Keys | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) })
if ($existing.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'Backup-Saves.ps1') -ServerRoot $root -BackupDir $BackupDir -Label 'pre-restore' -Yes -Confirm:$false
    foreach ($rel in $existing) {
        $target = Join-Path $aside $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Move-Item -LiteralPath (Join-Path $root $rel) -Destination $target
    }
}

$zip = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
try {
    foreach ($e in $zip.Entries) {
        $name = $e.FullName.Replace('\', '/')
        if (-not $entries.ContainsKey($name)) { continue }
        $dest = Join-Path $root ($name.Replace('/', '\'))
        if (-not (Test-RealmPathInside $dest $root)) { throw "Unsafe path: $dest" }
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true)
    }
} finally { $zip.Dispose() }

Write-Host "Restored $($entries.Count) files. Previous state kept in $aside" -ForegroundColor Green
