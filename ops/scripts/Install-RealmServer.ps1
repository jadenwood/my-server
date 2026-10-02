<#
.SYNOPSIS
  Installs SteamCMD and the Reign of Kings Dedicated Server (Steam app 381690, anonymous login) into chosen folders.

.DESCRIPTION
  1. Downloads Valve's steamcmd.zip into steamcmdDir (only if steamcmd.exe is not there yet), extracts it, and checks
     the Authenticode signature of steamcmd.exe on Windows.
  2. Runs SteamCMD once so it updates itself.
  3. Runs: steamcmd +force_install_dir <masterServerDir> +login anonymous +app_update 381690 validate +quit
     and retries up to 3 times (SteamCMD often fails its first app_update after a self-update).
  4. Confirms ROK.exe or Server.exe is present and records the build id in <opsRoot>\state\master.json.

  The result is the "master" copy: the clean server files. Nothing in this repo ever writes into it; Realm Steward
  (or server\New-TestServer.ps1) copies it to the instance folders and installs Oxide there. If you keep the default
  masterServerDir, G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server, Realm Steward finds it with no
  extra setup.

  Players never get these files: every player uses their own Steam copy of the game. This script downloads the
  dedicated server from Steam onto your own server only.

.EXAMPLE
  .\Install-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json -WhatIf
  .\Install-RealmServer.ps1 -ConfigPath G:\RealmOps\realm-ops.json
  .\Install-RealmServer.ps1 -SteamCmdDir G:\RealmOps\steamcmd -InstallDir 'G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server' -OpsRoot G:\RealmOps
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$ConfigPath = '',
    [string]$SteamCmdDir = '',
    [string]$InstallDir = '',
    [string]$OpsRoot = '',
    # Skip "validate" (only useful to save time when re-running on a folder you know is intact).
    [switch]$NoValidate,
    [int]$MinFreeGB = 10,
    [int]$TimeoutMinutes = 120,
    # For testing only: use this executable instead of <SteamCmdDir>\steamcmd.exe.
    [string]$SteamCmdExe = '',
    [string]$SteamCmdZipUrl = 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')

$cfg = $null
if ($ConfigPath) {
    $cfg = Read-OpsConfig $ConfigPath
    Assert-OpsConfig $cfg 'Install'
    if (-not $SteamCmdDir) { $SteamCmdDir = [string]$cfg.steamcmdDir }
    if (-not $InstallDir) { $InstallDir = [string]$cfg.masterServerDir }
    if (-not $OpsRoot) { $OpsRoot = [string]$cfg.opsRoot }
}
if (-not $SteamCmdDir -or -not $InstallDir -or -not $OpsRoot) { throw 'Pass -ConfigPath, or all of -SteamCmdDir, -InstallDir and -OpsRoot.' }

$opsRootFull = Assert-OpsSafeDir $OpsRoot 'opsRoot'
$steamCmdFull = Assert-OpsSafeDir $SteamCmdDir 'SteamCMD folder'
$master = Get-OpsFullPath $InstallDir
$why = Get-OpsMasterDirProblem $master $cfg
if ($why) { throw "Refusing install folder: $why" }
if ((Test-OpsPathInside $master $steamCmdFull) -or (Test-OpsPathInside $steamCmdFull $master)) { throw 'The SteamCMD folder and the install folder must not overlap.' }
if ((Test-Path -LiteralPath $master) -and @(Get-ChildItem -LiteralPath $master -Force).Count -gt 0 -and -not (Test-OpsServerFolder $master) -and -not (Test-Path -LiteralPath (Join-Path $master 'steamapps'))) {
    throw "Install folder '$master' already holds other files. Pick an empty folder."
}

if ($script:OpsIsWindows -and $master -match '^[A-Za-z]:') {
    $drive = New-Object System.IO.DriveInfo($master.Substring(0, 1))
    if ($drive.IsReady -and $drive.AvailableFreeSpace -lt ([long]$MinFreeGB * 1GB)) {
        throw ("Only {0} free on {1}; at least {2} GB is required (server size UNVERIFIED, Realm instances need more)." -f (Format-OpsBytes $drive.AvailableFreeSpace), $drive.Name, $MinFreeGB)
    }
}

$exe = $SteamCmdExe
if (-not $exe) { $exe = Join-Path $steamCmdFull 'steamcmd.exe' }
$validate = -not $NoValidate

Write-Host ''
Write-Host "  SteamCMD : $exe"
Write-Host "  Server   : $master  (Steam app $script:OpsAppId, anonymous)"
Write-Host "  Validate : $validate"
Write-Host "  Ops data : $opsRootFull"
Write-Host ''

if (-not $PSCmdlet.ShouldProcess($master, "Install SteamCMD and app $script:OpsAppId")) { return }

Start-OpsLog $opsRootFull 'install' | Out-Null
$lock = Enter-OpsLock $opsRootFull 'install'
try {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        New-OpsDirectory $steamCmdFull | Out-Null
        $zip = Join-Path $steamCmdFull 'steamcmd.zip'
        Write-OpsLog "Downloading SteamCMD from $SteamCmdZipUrl"
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
        $old = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'   # the progress bar makes Invoke-WebRequest very slow on 5.1
        try { Invoke-WebRequest -Uri $SteamCmdZipUrl -OutFile $zip -UseBasicParsing } finally { $ProgressPreference = $old }
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $z = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $names = @($z.Entries | ForEach-Object { $_.FullName })
            if (-not ($names -contains 'steamcmd.exe')) { throw "The downloaded zip has no steamcmd.exe (entries: $($names -join ', '))." }
            foreach ($e in $z.Entries) {
                if ($e.FullName -match '(^/|^[A-Za-z]:|\.\.)') { throw "Unsafe entry in steamcmd.zip: $($e.FullName)" }
                if ($e.FullName.EndsWith('/')) { continue }
                $target = Join-Path $steamCmdFull $e.FullName
                New-OpsDirectory (Split-Path -Parent $target) | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $true)
            }
        } finally { $z.Dispose() }
        Remove-Item -LiteralPath $zip -Force
        Write-OpsLog "SteamCMD extracted to $steamCmdFull" 'OK'
    }
    if ($script:OpsIsWindows -and -not $SteamCmdExe) {
        $sig = Get-AuthenticodeSignature -FilePath $exe
        $signer = ''
        if ($sig.SignerCertificate) { $signer = $sig.SignerCertificate.Subject }
        if ($sig.Status -eq 'Valid' -and $signer -match 'Valve') { Write-OpsLog "steamcmd.exe signature valid ($signer)." 'OK' }
        elseif ($sig.Status -eq 'NotSigned') { Write-OpsLog 'steamcmd.exe is not Authenticode-signed (UNVERIFIED whether Valve signs it). It came from Valve''s CDN over HTTPS.' 'WARN' }
        else { throw "steamcmd.exe signature is $($sig.Status) (signer '$signer'). Delete $steamCmdFull and try again." }
    }

    New-OpsDirectory $steamCmdFull | Out-Null
    Write-OpsLog 'Running SteamCMD once so it can update itself (this can take a few minutes the first time)...'
    $r = Invoke-OpsProcess $exe @('+@ShutdownOnFailedCommand', '1', '+quit') 900 $steamCmdFull
    Write-OpsLog ("SteamCMD self-update finished (exit {0})." -f $r.ExitCode)

    New-OpsDirectory $master | Out-Null
    $before = Get-OpsAppManifest $master
    $ok = $false
    $result = $null
    for ($attempt = 1; $attempt -le 3 -and -not $ok; $attempt++) {
        Write-OpsLog "app_update $script:OpsAppId (attempt $attempt of 3)..."
        $r = Invoke-OpsProcess $exe (Get-OpsSteamCmdArgs $master -Validate:$validate) ($TimeoutMinutes * 60) $steamCmdFull
        if ($script:OpsLogFile) { [System.IO.File]::AppendAllText($script:OpsLogFile, (Protect-OpsText $r.Output) + [Environment]::NewLine) }
        $result = Get-OpsSteamCmdResult $r.Output $r.ExitCode
        $ok = $result.Ok
        if (-not $ok) {
            Write-OpsLog ("SteamCMD: {0}" -f $result.Message) 'WARN'
            if ($result.Message -match 'No subscription|Disk write') { break }
            if ($attempt -lt 3) { Start-Sleep -Seconds 10 }
        }
    }
    if (-not $ok) { throw "SteamCMD could not install app $script:OpsAppId : $($result.Message). Full output: $script:OpsLogFile" }
    if (-not (Test-OpsServerFolder $master)) { throw "SteamCMD reported success but neither ROK.exe nor Server.exe is in $master." }
    $after = Get-OpsAppManifest $master
    $build = $null
    if ($after) { $build = $after.BuildId }
    $prevBuild = $null
    if ($before) { $prevBuild = $before.BuildId }
    Write-OpsJsonFile (Join-Path (Join-Path $opsRootFull 'state') 'master.json') ([ordered]@{
        masterServerDir = $master; appId = $script:OpsAppId; buildId = $build; previousBuildId = $prevBuild
        installedBy = 'Install-RealmServer.ps1'; at = (Get-OpsIsoNow); steamcmd = $result.State
    })
    Write-OpsLog ("Server files ready in {0} (build {1}, {2})." -f $master, $build, $result.State) 'OK'
} finally {
    Exit-OpsLock $lock
}

Write-Host ''
Write-Host 'Next steps (docs: ops\hosting-guide.md):' -ForegroundColor Cyan
Write-Host '  1. Install Realm Steward and run its setup wizard. It copies this master to G:\RealmTest\server,'
Write-Host '     installs Oxide in the copy and deploys the Realm plugins. Use Add server for instances 2-4.'
Write-Host "     (Without Steward: ..\..\server\New-TestServer.ps1 -Source '$master')"
Write-Host '  2. Open the firewall ports from ops\hosting-guide.md section 5 (never TCP 11000-11003).'
Write-Host '  3. Register the nightly backup and the uptime monitor: .\Register-RealmTasks.ps1 -ConfigPath <config>'
