<#
.SYNOPSIS
  Read-only: lists the real Mods\*.defaults.cfg files and their atmosphere-related lines, so the
  mods\templates presets can be filled in with verified key names.

.DESCRIPTION
  The built-in Mods folder appears only after the server has been started at least twice
  [docs/server-reference.md section 5]. This script reads the test copy and writes a text report
  (default: ..\docs\mods-keys.txt in the repo). It changes nothing in the server folder.

.EXAMPLE
  .\Export-ModKeys.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$OutFile = (Join-Path (Split-Path $PSScriptRoot -Parent) 'docs\mods-keys.txt'),
    [string]$Pattern = 'fog|sun|moon|day|night|dusk|dawn|weather|rain|storm|cloud|thunder|light|ambient|sky|atmos|colou?r|tint'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$root = Get-RealmFullPath $ServerRoot
$modsDir = Join-Path $root 'Mods'
if (-not (Test-Path -LiteralPath $modsDir -PathType Container)) {
    throw "$modsDir does not exist. Start and stop the server twice so it generates the Mods folder."
}

$defaults = @(Get-ChildItem -LiteralPath $modsDir -Recurse -File -Filter '*.defaults.cfg')
$report = New-Object System.Collections.Generic.List[string]
$report.Add("# Mods key report from $modsDir, $((Get-Date).ToUniversalTime().ToString('o'))")
$report.Add('# Files:')
foreach ($d in $defaults) { $report.Add('#   ' + $d.FullName.Substring($modsDir.Length + 1)) }
foreach ($d in $defaults) {
    $hits = @(Select-String -LiteralPath $d.FullName -Pattern $Pattern)
    if ($hits.Count -eq 0) { continue }
    $report.Add('')
    $report.Add("[$($d.FullName.Substring($modsDir.Length + 1))]")
    foreach ($h in $hits) { $report.Add($h.Line.TrimEnd()) }
}

if ($PSCmdlet.ShouldProcess($OutFile, 'Write Mods key report')) {
    New-Item -ItemType Directory -Force -Path (Split-Path $OutFile -Parent) | Out-Null
    $report | Set-Content -LiteralPath $OutFile -Encoding UTF8
    Write-Host "Found $($defaults.Count) defaults file(s). Report: $OutFile" -ForegroundColor Green
}
