<#
.SYNOPSIS
  Installs Oxide 2.0.3867 for Reign of Kings into the Realm TEST copy only, backing up every file it overwrites.

.DESCRIPTION
  You download the zip yourself from
    https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip
  and pass its path. The script:
    1. Checks the SHA-256 (6c35c623...c6c8, 11,939,101 bytes; docs/oxide-rok-api.md section 0).
    2. Checks every zip entry is under ROK_Data/ with no '..' (the 2.0.3867 zip holds only ROK_Data/Managed/...).
    3. Copies each server file it would overwrite to <server>\_realm-backups\oxide-<time>\ and writes
       install.json there, listing overwritten and newly created files.
    4. Extracts into the test copy. The Steam copy is never touched.
  -Rollback undoes the most recent install from that backup (restores originals, deletes files Oxide added).

  The zip targets ROK_Data\Managed. If the test copy has a different *_Data\Managed folder (for example
  Server_Data), pass -DataFolder <name> to redirect. UNVERIFIED: which data folder the owner's install has
  (docs/oxide-rok-api.md section 6.2).

.EXAMPLE
  .\Install-Oxide.ps1 -ZipPath "$env:USERPROFILE\Downloads\Oxide.ReignOfKings.zip" -WhatIf
  .\Install-Oxide.ps1 -ZipPath "$env:USERPROFILE\Downloads\Oxide.ReignOfKings.zip"
  .\Install-Oxide.ps1 -Rollback
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')][string]$ZipPath,
    [Parameter(ParameterSetName = 'Rollback')][switch]$Rollback,
    [string]$ServerRoot = 'G:\RealmTest\server',
    [Parameter(ParameterSetName = 'Install')][string]$DataFolder = 'ROK_Data',
    [Parameter(ParameterSetName = 'Install')][string]$ExpectedSha256 = '6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8',
    # UNVERIFIED: Oxide's CalculateOriginalHash patch makes the game hash Managed\Assembly-CSharp_Original.dll,
    # which the zip does not ship (docs/oxide-rok-api.md section 2.9). This switch saves the vanilla DLL under
    # that name. Only use it if the smoke test shows hash/version errors without it.
    [Parameter(ParameterSetName = 'Install')][switch]$WriteOriginalHashCopy,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$root = Assert-RealmTestCopy $ServerRoot
Assert-RealmServerStopped $root
$backupBase = Join-Path $root '_realm-backups'

if ($Rollback) {
    $last = Get-ChildItem -LiteralPath $backupBase -Directory -Filter 'oxide-*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'install.json') } |
        Sort-Object Name | Select-Object -Last 1
    if (-not $last) { throw "No Oxide install backup found under $backupBase." }
    $log = Get-Content -LiteralPath (Join-Path $last.FullName 'install.json') -Raw | ConvertFrom-Json
    Write-Host "  Backup   : $($last.FullName)"
    Write-Host "  Restores : $(@($log.overwritten).Count) original files"
    Write-Host "  Deletes  : $(@($log.created).Count) files that Oxide added"
    $q = 'Roll back the Oxide install in the test copy? (oxide\ plugin/data folders are left in place.)'
    if (-not (Confirm-RealmStep $PSCmdlet $root 'Roll back Oxide install' $q $Yes)) { return }
    foreach ($rel in @($log.overwritten)) {
        if (-not $rel) { continue }
        Copy-Item -LiteralPath (Join-Path $last.FullName "files\$rel") -Destination (Join-Path $root $rel) -Force
    }
    foreach ($rel in @($log.created)) {
        if (-not $rel) { continue }
        $p = Join-Path $root $rel
        if (Test-Path -LiteralPath $p -PathType Leaf) { Remove-Item -LiteralPath $p -Force }
    }
    Rename-Item -LiteralPath $last.FullName -NewName ($last.Name + '-rolledback')
    Write-Host 'Rollback complete. The test copy is back to its pre-Oxide game files.' -ForegroundColor Green
    return
}

$zipFull = Get-RealmFullPath $ZipPath
if (-not (Test-Path -LiteralPath $zipFull -PathType Leaf)) { throw "Zip not found: $zipFull" }
$hash = (Get-FileHash -LiteralPath $zipFull -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $ExpectedSha256.ToLowerInvariant()) {
    throw "SHA-256 mismatch.`n  expected $ExpectedSha256`n  actual   $hash`nRe-download Oxide.ReignOfKings.zip from the 2.0.3867 GitHub release."
}
Write-Host "  SHA-256 OK: $hash"

if ($DataFolder -notmatch '^[A-Za-z0-9_]+_Data$') { throw "-DataFolder must look like 'ROK_Data' or 'Server_Data'." }
$managed = Join-Path $root "$DataFolder\Managed"
if (-not (Test-Path -LiteralPath $managed -PathType Container)) {
    $found = @(Get-RealmManagedDirs $root | ForEach-Object { $_.Name })
    throw "$managed does not exist. Data folders with a Managed subfolder in the test copy: $(if ($found) { $found -join ', ' } else { 'none' }). Pass -DataFolder <name>."
}
if (Test-Path -LiteralPath (Join-Path $managed 'Oxide.Core.dll')) {
    throw "Oxide already appears to be installed ($managed\Oxide.Core.dll exists). Run .\Install-Oxide.ps1 -Rollback first so the backup holds vanilla files."
}
if ($DataFolder -ne 'ROK_Data') { Write-Warning "Redirecting the zip's ROK_Data\ to $DataFolder\ (UNVERIFIED that this is correct for this install)." }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$plan = @()
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
try {
    foreach ($e in $zip.Entries) {
        $name = $e.FullName.Replace('\', '/')
        if ($name -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$))') { throw "Unsafe entry in zip: $name" }
        if ($name -notmatch '^ROK_Data/') { throw "Unexpected entry outside ROK_Data/: $name. This is not the expected Oxide 2.0.3867 RoK zip." }
        if ($name.EndsWith('/')) { continue }
        $rel = ($DataFolder + $name.Substring('ROK_Data'.Length)).Replace('/', '\')
        $plan += [pscustomobject]@{ Entry = $e.FullName; Rel = $rel; Exists = (Test-Path -LiteralPath (Join-Path $root $rel)) }
    }
} finally { $zip.Dispose() }

$overwrite = @($plan | Where-Object { $_.Exists })
$create = @($plan | Where-Object { -not $_.Exists })
$backupDir = Join-Path $backupBase ('oxide-' + (Get-RealmTimestamp))
Write-Host "  Target    : $root  (test copy)"
Write-Host "  Overwrites: $($overwrite.Count) files (backed up first), e.g. $DataFolder\Managed\Assembly-CSharp.dll"
Write-Host "  Adds      : $($create.Count) files"
Write-Host "  Backup to : $backupDir"

$q = "Install Oxide 2.0.3867 into the test copy at '$root'? Originals are backed up and -Rollback can undo it."
if (-not (Confirm-RealmStep $PSCmdlet $root 'Install Oxide 2.0.3867' $q $Yes)) { return }

foreach ($item in $overwrite) {
    $dest = Join-Path $backupDir "files\$($item.Rel)"
    New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $item.Rel) -Destination $dest
}
$created = @($create | ForEach-Object { $_.Rel })
$vanilla = Join-Path $managed 'Assembly-CSharp.dll'
$originalCopy = Join-Path $managed 'Assembly-CSharp_Original.dll'
if ($WriteOriginalHashCopy -and -not (Test-Path -LiteralPath $originalCopy)) {
    # UNVERIFIED: see the parameter comment above.
    Copy-Item -LiteralPath $vanilla -Destination $originalCopy
    $created += "$DataFolder\Managed\Assembly-CSharp_Original.dll"
}
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
[ordered]@{
    tool = 'Install-Oxide.ps1'; zip = $zipFull; sha256 = $hash; dataFolder = $DataFolder
    installed = (Get-Date).ToUniversalTime().ToString('o')
    overwritten = @($overwrite | ForEach-Object { $_.Rel }); created = $created
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDir 'install.json') -Encoding UTF8

$zip = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
try {
    $byName = @{}
    foreach ($e in $zip.Entries) { $byName[$e.FullName] = $e }
    foreach ($item in $plan) {
        $dest = Join-Path $root $item.Rel
        if (-not (Test-RealmPathInside $dest $root)) { throw "Unsafe path: $dest" }
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($byName[$item.Entry], $dest, $true)
    }
} finally { $zip.Dispose() }

Write-Host "Oxide 2.0.3867 installed into the test copy. Backup: $backupDir" -ForegroundColor Green
Write-Host "Next: .\Start-LocalServer.ps1, then check the console for 'Oxide.ReignOfKings 2.0.3867' (docs\smoke-test.md, part B)."
