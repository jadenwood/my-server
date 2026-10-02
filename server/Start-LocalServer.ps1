<#
.SYNOPSIS
  Starts the dedicated server from the Realm test copy, set up for local-only testing.

.DESCRIPTION
  Before launch, if the config files already exist (they are created on first run), this script sets
  verified keys for a local test [docs/server-reference.md section 4]:
    Configuration\ServerSettings.cfg : bindIP = <BindIP, default 127.0.0.1>, isPrivate = 'True'
    Configuration\ConsoleSettings.cfg: enableRCon = 'False' (unless -AllowRCon), rConPort = 27016
  The previous file is saved to <server>\_realm-backups\config first.

  Launch:
    -Exe Server (default): Server.exe, the console wrapper. No arguments are passed; no Server.exe
                           arguments are documented in any verified source.
    -Exe ROK             : ROK.exe -batchmode -nographics -silentcrash  (Oxide's _start-example.bat) [VERIFIED-PRIMARY]
  The working directory is the server root, so Oxide creates its folders at <server>\oxide.

  This script never runs netsh, never creates firewall rules and never touches the router.
  If Windows Defender Firewall asks whether to allow the server, choose Cancel / do not allow:
  connections to 127.0.0.1 from the same PC do not go through the firewall.

.EXAMPLE
  .\Start-LocalServer.ps1 -WhatIf
  .\Start-LocalServer.ps1
  .\Start-LocalServer.ps1 -Exe ROK
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$ServerRoot = 'G:\RealmTest\server',
    [ValidateSet('Server', 'ROK')][string]$Exe = 'Server',
    # UNVERIFIED: that the server still authenticates players with Steam when bound to 127.0.0.1.
    # If clients cannot join, try -BindIP 0.0.0.0 (still no firewall/router change: nothing outside can reach it).
    [string]$BindIP = '127.0.0.1',
    [int]$RConPort = 27016,
    [switch]$AllowRCon,
    [switch]$SkipConfig,
    # AMP sets SteamAppId=344760 for the server process [VERIFIED-PRIMARY]; whether the server needs it is UNVERIFIED.
    [switch]$SetSteamAppId,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$root = Assert-RealmTestCopy $ServerRoot
Assert-RealmServerStopped $root

$exePath = Get-RealmServerExe $root $Exe
if (-not $exePath) { throw "$Exe.exe not found in $root. Try -Exe $(if ($Exe -eq 'Server') { 'ROK' } else { 'Server' })." }
$exeArgs = @()
if ($Exe -eq 'ROK') { $exeArgs = @('-batchmode', '-nographics', '-silentcrash') }

$cfgDir = Join-Path $root 'Configuration'
$serverCfg = Join-Path $cfgDir 'ServerSettings.cfg'
$consoleCfg = Join-Path $cfgDir 'ConsoleSettings.cfg'
$cfgBackup = Join-Path $root '_realm-backups\config'

$port = Get-RealmCfgValue $serverCfg 'portNumber'
if (-not $port) { $port = '7350' }

Write-Host ''
Write-Host "  Server : $exePath $($exeArgs -join ' ')"
Write-Host "  Folder : $root"
Write-Host "  Connect: in the game's direct connect / 'Local Host' field use 127.0.0.1 port $port"
Write-Host "  Ready  : when the console shows a line starting 'Initialize engine version:'"
Write-Host "  Stop   : type 'quit' in the server console"
if (-not $SkipConfig) {
    $rconText = if ($AllowRCon) { "rConPort='$RConPort'" } else { "rConPort='$RConPort', enableRCon='False'" }
    Write-Host "  Config : ServerSettings.cfg bindIP='$BindIP', isPrivate='True'; ConsoleSettings.cfg $rconText (existing files only, backed up first)"
}
Write-Host ''

$question = "Apply local-only config and start $Exe.exe from the test copy at '$root'?"
if (-not (Confirm-RealmStep $PSCmdlet $exePath 'Start server process' $question $Yes)) { return }

if (-not $SkipConfig) {
    if (Test-Path -LiteralPath $serverCfg) {
        $wanted = @{ bindIP = $BindIP; isPrivate = 'True' }
        $changes = Set-RealmCfgValues $serverCfg $wanted $cfgBackup
        foreach ($c in $changes) { Write-Host "  ServerSettings.cfg  $c" }
    } else {
        Write-Warning ("$serverCfg does not exist yet (first run). The server will create it with bindIP '0.0.0.0'. " +
            'After this first run, stop the server and run this script again to apply local-only settings.')
    }
    if (Test-Path -LiteralPath $consoleCfg) {
        $wanted = @{ rConPort = "$RConPort" }
        if (-not $AllowRCon) { $wanted['enableRCon'] = 'False' }
        $changes = Set-RealmCfgValues $consoleCfg $wanted $cfgBackup
        foreach ($c in $changes) { Write-Host "  ConsoleSettings.cfg $c" }
        if ($AllowRCon -and (Get-RealmCfgValue $consoleCfg 'enableRCon') -eq 'True' -and -not (Get-RealmCfgValue $consoleCfg 'rConPassword')) {
            throw "RCON is enabled with an empty rConPassword in $consoleCfg. Set a password first."
        }
    }
}

$oldAppId = $env:SteamAppId
try {
    if ($SetSteamAppId) { $env:SteamAppId = '344760' }
    $startArgs = @{ FilePath = $exePath; WorkingDirectory = $root; PassThru = $true }
    if ($exeArgs.Count -gt 0) { $startArgs['ArgumentList'] = $exeArgs }
    $proc = Start-Process @startArgs
} finally {
    $env:SteamAppId = $oldAppId
}
Write-Host "Started $Exe.exe (pid $($proc.Id))." -ForegroundColor Green
