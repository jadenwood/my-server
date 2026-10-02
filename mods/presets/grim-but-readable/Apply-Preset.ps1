<#
.SYNOPSIS
  Copies the "grim but readable" Mods override lines into the test server's Mods\<Name>.cfg files,
  or reverts them from the backups it made.

.DESCRIPTION
  The keys in grim-but-readable.cfg were proven from the game's code (docs/mods-keys-from-dll.md),
  but the Mods file they live in is named by Unity scene data that the DLL does not contain
  [UNVERIFIED]. So this script looks every key up in Mods\*.defaults.cfg (written by the server
  itself) and puts each line into the matching <Name>.cfg. Keys it cannot find are skipped and
  reported, never guessed.

  - Works only on the test copy made by server\New-TestServer.ps1 (same guard as the other scripts).
  - Refuses to run while that server is running: the server rewrites Mods\*.cfg when it starts.
  - Before the first change to a file it saves <Name>.cfg.realm-backup next to it. -Revert puts the
    backups back (and removes a <Name>.cfg that did not exist before).
  - Prints each key's server default next to the preset value and warns when the default is not
    the value the preset was tuned against.

.EXAMPLE
  .\Apply-Preset.ps1 -WhatIf
  .\Apply-Preset.ps1
  .\Apply-Preset.ps1 -Revert
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$ServerRoot = 'G:\RealmTest\server',
    [string]$PresetFile = (Join-Path $PSScriptRoot 'grim-but-readable.cfg'),
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
. (Join-Path $repoRoot 'server\RealmCommon.ps1')

$root = Assert-RealmTestCopy $ServerRoot
if (@(Get-RealmServerProcess $root).Count -gt 0) {
    throw "The server in '$root' is running. Stop it first: it rewrites Mods\*.cfg on start-up."
}
$modsDir = Join-Path $root 'Mods'
if (-not (Test-Path -LiteralPath $modsDir -PathType Container)) {
    throw "$modsDir does not exist. Start and stop the server twice so it generates the Mods folder."
}

$backupSuffix = '.realm-backup'
$noFileMarker = '# realm-backup: file did not exist before Apply-Preset.ps1'

if ($Revert) {
    $backups = @(Get-ChildItem -LiteralPath $modsDir -File -Filter "*.cfg$backupSuffix")
    if ($backups.Count -eq 0) { Write-Host 'No Realm backups found in Mods; nothing to revert.'; return }
    foreach ($b in $backups) {
        $target = $b.FullName.Substring(0, $b.FullName.Length - $backupSuffix.Length)
        $content = @(Get-Content -LiteralPath $b.FullName)
        if ($PSCmdlet.ShouldProcess($target, 'Restore from backup')) {
            if ($content.Count -ge 1 -and $content[0] -eq $noFileMarker) {
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
            } else {
                Copy-Item -LiteralPath $b.FullName -Destination $target -Force
            }
            Remove-Item -LiteralPath $b.FullName
            Write-Host "Restored $target" -ForegroundColor Green
        }
    }
    return
}

# The values the preset was tuned against: the C# field initializers in the DLL. The real scene
# defaults are UNVERIFIED, which is why they are compared here.
$assumedDefaults = @{
    'Atmosphere.FogDensity' = '1'
    'Atmosphere.FogColor'   = 'rgba(1,1,1,1)'
    'Atmosphere.MoonColor'  = 'rgba(1,1,1,1)'
    'Atmosphere.SunColor'   = 'rgba(1,1,1,1)'
}

# Parse "key = 'value'" lines the same way the game does (space before '=', quotes trimmed).
$lineRe = '^\s*([^#=\s][^=]*?)\s+=\s*''?([^''#]*)''?'
$preset = New-Object System.Collections.Generic.List[object]
foreach ($line in Get-Content -LiteralPath $PresetFile) {
    $m = [regex]::Match($line, $lineRe)
    if ($m.Success) { $preset.Add(@{ Key = $m.Groups[1].Value.Trim(); Value = $m.Groups[2].Value.Trim(); Line = $line.Trim() }) }
}
if ($preset.Count -eq 0) { throw "No key = 'value' lines found in $PresetFile." }

# Index every key the server declared in its .defaults.cfg files: key -> (handler name, default value).
$declared = @{}
foreach ($d in @(Get-ChildItem -LiteralPath $modsDir -File -Filter '*.defaults.cfg')) {
    $handler = $d.Name.Substring(0, $d.Name.Length - '.defaults.cfg'.Length)
    foreach ($line in Get-Content -LiteralPath $d.FullName) {
        $m = [regex]::Match($line, $lineRe)
        if ($m.Success) {
            $k = $m.Groups[1].Value.Trim()
            if (-not $declared.ContainsKey($k)) { $declared[$k] = @{ Handler = $handler; Default = $m.Groups[2].Value.Trim() } }
        }
    }
}

$byHandler = @{}
$skipped = 0
foreach ($p in $preset) {
    if (-not $declared.ContainsKey($p.Key)) {
        Write-Warning "$($p.Key): not declared in any Mods\*.defaults.cfg. Skipped (the server build may differ from the DLL this preset was proven against)."
        $skipped++
        continue
    }
    $info = $declared[$p.Key]
    $note = ''
    if ($assumedDefaults.ContainsKey($p.Key) -and ($info.Default -replace '\s', '') -ne $assumedDefaults[$p.Key]) {
        $note = "  <-- default is not $($assumedDefaults[$p.Key]); review the preset value"
    }
    Write-Host ("{0,-34} {1,-24} default {2,-26} preset {3}{4}" -f $p.Key, "[$($info.Handler).cfg]", $info.Default, $p.Value, $note)
    if (-not $byHandler.ContainsKey($info.Handler)) { $byHandler[$info.Handler] = New-Object System.Collections.Generic.List[object] }
    $byHandler[$info.Handler].Add($p)
}

foreach ($handler in $byHandler.Keys) {
    $target = Join-Path $modsDir "$handler.cfg"
    $backup = "$target$backupSuffix"
    $lines = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $target) { foreach ($l in Get-Content -LiteralPath $target) { $lines.Add($l) } }
    foreach ($p in $byHandler[$handler]) {
        $pattern = '^\s*' + [regex]::Escape($p.Key) + '\s+='
        $replaced = $false
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match $pattern) {
                if (-not $replaced) { $lines[$i] = $p.Line; $replaced = $true }
                else { $lines.RemoveAt($i); $i-- }   # a duplicate key would become "key2" and be ignored
            }
        }
        if (-not $replaced) { $lines.Add($p.Line) }
    }
    if ($PSCmdlet.ShouldProcess($target, "Write $($byHandler[$handler].Count) preset line(s)")) {
        if (-not (Test-Path -LiteralPath $backup)) {
            if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination $backup }
            else { Set-Content -LiteralPath $backup -Value $noFileMarker -Encoding ASCII }
        }
        # ASCII: the game reads the file with File.OpenText (UTF-8); every preset character is ASCII.
        $lines | Set-Content -LiteralPath $target -Encoding ASCII
        Write-Host "Wrote $target (backup: $backup)" -ForegroundColor Green
    }
}

if ($skipped -gt 0) { Write-Warning "$skipped key(s) skipped. See docs/mods-keys-from-dll.md." }
Write-Host 'Start the server, then check each line is still active in Mods\<Name>.cfg: the server rewrites the file on start-up, and a key it does not recognise comes back as a # comment [inferred from code, UNVERIFIED in practice].'
