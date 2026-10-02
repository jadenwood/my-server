<#
.SYNOPSIS
  Copies the repo's plugins\*.cs into the test server's oxide\plugins folder.

.DESCRIPTION
  Source defaults to ..\plugins next to this script (the repo's plugins folder).
  Target is <server>\oxide\plugins, or <server>\Saves\oxide\plugins if that is where Oxide created it.
  Oxide creates the folder on its first successful start, so run the Oxide smoke test first
  (or pass -CreateTarget to create <server>\oxide\plugins).
  Unchanged files are skipped. Files that would be replaced are copied to
  <server>\_realm-backups\plugins-<time>\ first (outside oxide\, so Oxide does not load them).
  Plugins in the target that are not in the repo are listed, never deleted.
  Oxide hot-reloads changed .cs files, so the server may stay running.

.EXAMPLE
  .\Deploy-Plugins.ps1 -WhatIf
  .\Deploy-Plugins.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$PluginSource = (Join-Path (Split-Path $PSScriptRoot -Parent) 'plugins'),
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

if ($copy.Count -eq 0) { Write-Host 'Nothing to deploy; all plugins are up to date.' -ForegroundColor Green; return }
$running = @(Get-RealmServerProcess $root).Count -gt 0
if ($running) { Write-Host '  Server is running: Oxide should hot-reload the copied plugins.' }

$q = "Copy $($copy.Count) plugin file(s) from '$srcDir' to '$target'?"
if (-not (Confirm-RealmStep $PSCmdlet $target "Deploy $($copy.Count) plugin(s)" $q $Yes)) { return }

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
