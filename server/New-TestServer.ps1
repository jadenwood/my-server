<#
.SYNOPSIS
  Copies the Steam dedicated server folder to a separate test folder so the Steam copy stays clean.

.DESCRIPTION
  Copies (never moves) "Reign Of Kings Dedicated Server" from the Steam library to G:\RealmTest\server
  and drops a .realm-test-copy marker there. Every other Realm script refuses to write to a folder
  without that marker. The Steam copy is only read.

  Refuses when the destination is on C:, inside a Steam library, overlaps the source, or the
  destination drive lacks free space for the copy plus a 2 GB margin.

.EXAMPLE
  .\New-TestServer.ps1 -WhatIf
  .\New-TestServer.ps1
  .\New-TestServer.ps1 -Refresh     # re-copy over an existing test copy (re-run Install-Oxide.ps1 afterwards)
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$Source = 'G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server',
    [string]$Destination = 'G:\RealmTest\server',
    [switch]$Refresh,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$src = Get-RealmFullPath $Source
$dst = Get-RealmFullPath $Destination

if (-not (Test-Path -LiteralPath $src -PathType Container)) { throw "Source folder not found: $src" }
if (-not ((Test-Path -LiteralPath (Join-Path $src 'Server.exe')) -or (Test-Path -LiteralPath (Join-Path $src 'ROK.exe')))) {
    throw "Neither Server.exe nor ROK.exe is in '$src'. Is this the Reign of Kings Dedicated Server folder?"
}
if ($dst -notmatch '^[A-Za-z]:\\') { throw "Destination must be a local drive path like G:\RealmTest\server, got '$dst'." }
if ($dst.Substring(0, 2) -ieq 'C:') { throw "Refusing: destination '$dst' is on C:. Use another drive (default G:\RealmTest\server)." }
if ($dst -match '\\steamapps(\\|$)') { throw "Refusing: destination '$dst' is inside a Steam library." }
if ((Test-RealmPathInside $dst $src) -or (Test-RealmPathInside $src $dst)) { throw "Refusing: source and destination overlap." }

$exists = Test-Path -LiteralPath $dst
if ($exists -and @(Get-ChildItem -LiteralPath $dst -Force).Count -gt 0) {
    if (-not $Refresh) { throw "Destination '$dst' already has files. Use -Refresh to copy over it, or pick another -Destination." }
    if (-not (Test-Path -LiteralPath (Join-Path $dst $script:RealmMarkerName))) {
        throw "Refusing to refresh '$dst': it has files but no $script:RealmMarkerName marker."
    }
    Assert-RealmServerStopped $dst
}

Write-Host "Measuring $src ..."
$srcBytes = (Get-ChildItem -LiteralPath $src -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
if (-not $srcBytes) { $srcBytes = 0 }
$drive = New-Object System.IO.DriveInfo($dst.Substring(0, 1))
if (-not $drive.IsReady) { throw "Drive $($drive.Name) is not ready." }
$margin = 2GB
# On a refresh most files already exist, but the check stays conservative.
if ($drive.AvailableFreeSpace -lt ($srcBytes + $margin)) {
    throw ("Not enough space on {0}: need {1} (copy) + {2} margin, have {3}." -f $drive.Name,
        (Format-RealmBytes $srcBytes), (Format-RealmBytes $margin), (Format-RealmBytes $drive.AvailableFreeSpace))
}

Write-Host ''
Write-Host "  From : $src  (read only)"
Write-Host "  To   : $dst"
Write-Host ("  Size : {0}; free on {1} {2}" -f (Format-RealmBytes $srcBytes), $drive.Name, (Format-RealmBytes $drive.AvailableFreeSpace))
if ($Refresh) { Write-Warning 'Refresh overwrites changed game files, including an Oxide-patched Assembly-CSharp.dll. Saves, oxide\ and Configuration\ files that do not exist in the Steam copy are kept.' }
Write-Host ''

$question = "Copy the dedicated server to '$dst'? The Steam copy is not modified."
if (-not (Confirm-RealmStep $PSCmdlet $dst "Copy server files from $src" $question $Yes)) { return }

New-Item -ItemType Directory -Force -Path $dst | Out-Null
# robocopy /E copies subfolders (no /MIR, so nothing in the destination is ever deleted).
$rcArgs = @($src, $dst, '/E', '/COPY:DAT', '/R:1', '/W:1', '/NFL', '/NDL', '/NP')
& robocopy.exe @rcArgs | Out-Host
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

$marker = Join-Path $dst $script:RealmMarkerName
Set-Content -LiteralPath $marker -Encoding ASCII -Value @(
    'Realm test copy. Created by New-TestServer.ps1; Realm scripts only write to folders with this file.'
    "source=$src"
    "copied=$((Get-Date).ToUniversalTime().ToString('o'))"
)

Write-Host ''
Write-Host "Done. Test copy ready at $dst" -ForegroundColor Green
Write-Host 'Next: .\Start-LocalServer.ps1   (vanilla smoke test, see docs\smoke-test.md)'
