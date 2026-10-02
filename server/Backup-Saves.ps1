<#
.SYNOPSIS
  Zips the test server's Saves folder and Oxide data folder into a timestamped backup.

.DESCRIPTION
  Creates <BackupDir>\realm-saves-<yyyyMMdd-HHmmss>.zip containing:
    Saves\...           (world saves; Configuration\ServerSettings.cfg saveLocation defaults to 'Saves/')
    oxide\data\...      (Oxide plugin data, when Oxide lives at <server>\oxide)
    realm-backup.json   (manifest: source folder, time, file count)
  If Oxide lives under Saves\oxide instead, its data is already inside Saves.
  Only reads the server folder. Refuses while the server is running unless -AllowWhileRunning,
  because a save written mid-backup can be inconsistent.

.EXAMPLE
  .\Backup-Saves.ps1 -WhatIf
  .\Backup-Saves.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$BackupDir = 'G:\RealmTest\backups',
    [string]$Label = '',
    [switch]$AllowWhileRunning,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$root = Assert-RealmTestCopy $ServerRoot
if (-not $AllowWhileRunning) { Assert-RealmServerStopped $root }
$backupFull = Get-RealmFullPath $BackupDir
if (Test-RealmPathInside $backupFull $root) { throw "Refusing: -BackupDir must be outside the server folder." }

$sources = @()
foreach ($rel in @('Saves', 'oxide\data')) {
    $p = Join-Path $root $rel
    if (Test-Path -LiteralPath $p -PathType Container) { $sources += $rel }
}
if ($sources.Count -eq 0) { throw "Nothing to back up: neither Saves nor oxide\data exists in $root (has the server run yet?)." }

$files = @()
foreach ($rel in $sources) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $rel) -Recurse -File -Force
}
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
$suffix = if ($Label) { '-' + ($Label -replace '[^A-Za-z0-9_-]', '_') } else { '' }
$zipPath = Join-Path $backupFull ("realm-saves-{0}{1}.zip" -f (Get-RealmTimestamp), $suffix)

Write-Host "  Folders: $($sources -join ', ')  ($($files.Count) files, $(Format-RealmBytes $bytes))"
Write-Host "  Zip    : $zipPath"

if (-not (Confirm-RealmStep $PSCmdlet $zipPath 'Create backup zip' "Create backup '$zipPath'?" $Yes)) { return }

New-Item -ItemType Directory -Force -Path $backupFull | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$rootPrefix = $root.Length + 1
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
$ok = $false
try {
    foreach ($f in $files) {
        $entryName = $f.FullName.Substring($rootPrefix).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $entryName,
            [System.IO.Compression.CompressionLevel]::Optimal)
    }
    $manifest = [ordered]@{
        tool = 'Backup-Saves.ps1'; source = $root; created = (Get-Date).ToUniversalTime().ToString('o')
        folders = $sources; files = $files.Count; bytes = $bytes
    } | ConvertTo-Json
    $entry = $zip.CreateEntry('realm-backup.json')
    $w = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
    try { $w.Write($manifest) } finally { $w.Dispose() }
    $ok = $true
} finally {
    $zip.Dispose()
    if (-not $ok -and (Test-Path -LiteralPath $zipPath)) { Remove-Item -LiteralPath $zipPath -Force }
}

Write-Host "Backup written: $zipPath ($(Format-RealmBytes (Get-Item -LiteralPath $zipPath).Length))" -ForegroundColor Green
