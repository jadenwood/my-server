<#
.SYNOPSIS
  One-stop menu for the Realm test server. Started by double-clicking Realm.bat.

.DESCRIPTION
  Wraps the other scripts in this folder so the owner never has to type commands:
    1  Set up   : test copy of the Steam server -> download + verify Oxide 2.0.3867 -> install it -> deploy plugins
    2  Play     : start the local server, the Chronicle service and the launcher
    3  Update   : copy changed plugins into the running server (Oxide hot-reloads them)
    4  Back up  : zip Saves + oxide\data
    5  Undo Oxide (restores the vanilla game files in the test copy)
  The Steam copy is only read. Nothing here touches the firewall or router.
#>
[CmdletBinding()]
param([string]$Action)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RealmCommon.ps1')

$RepoRoot   = Split-Path $PSScriptRoot -Parent
$OxideUrl   = 'https://github.com/OxideMod/Oxide.ReignOfKings/releases/download/2.0.3867/Oxide.ReignOfKings.zip'
$OxideZip   = $script:RealmTestRoot + '\downloads\Oxide.ReignOfKings-2.0.3867.zip'
$ServerRoot = $script:RealmTestServer

function Write-Step([string]$Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Yellow }

function Find-SteamServer {
    # Default location first, then every Steam library listed in libraryfolders.vdf.
    $name = 'Reign Of Kings Dedicated Server'
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($script:RealmSteamServer)
    $steam = $null
    try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath } catch { }
    if ($steam) {
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"(?<p>[^"]+)"')) {
                $candidates.Add((Join-Path ($m.Groups['p'].Value -replace '\\\\', '\') "steamapps\common\$name"))
            }
        }
    }
    foreach ($c in $candidates) {
        # String concat, not Join-Path: Join-Path throws when the drive does not exist.
        if (Test-Path -LiteralPath ($c.TrimEnd('\') + '\Server.exe')) { return $c }
    }
    return $null
}

function Find-Node {
    # Release bundles ship runtime\node.exe; otherwise use an installed Node.js.
    $bundled = Join-Path $RepoRoot 'runtime\node.exe'
    if (Test-Path -LiteralPath $bundled) { return $bundled }
    $cmd = Get-Command node -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Wait-ServerExit([int]$TimeoutMinutes) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-RealmServerProcess $ServerRoot).Count -eq 0) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Invoke-Setup {
    Write-Step 'Checking your PC'
    $source = Find-SteamServer
    if (-not $source) {
        throw ("Could not find the Reign of Kings Dedicated Server. In Steam, open Library > Tools, install " +
            "'Reign Of Kings Dedicated Server' (on G:), then run this again.")
    }
    Write-Host "  Steam server : $source"
    $drive = Split-Path -Qualifier $script:RealmTestRoot
    if (-not (Test-Path "$drive\")) { throw "Drive $drive does not exist. Edit RealmTestRoot in server\RealmCommon.ps1 to a drive you have (not C:)." }

    Write-Step 'Step 1/4  Test copy of the server'
    if (Test-Path -LiteralPath (Join-Path $ServerRoot $script:RealmMarkerName)) {
        Write-Host "  Already exists: $ServerRoot"
    } else {
        & (Join-Path $PSScriptRoot 'New-TestServer.ps1') -Source $source -Destination $ServerRoot -Yes
    }
    Assert-RealmServerStopped $ServerRoot

    Write-Step 'Step 2/4  Server settings'
    if (-not (Test-Path -LiteralPath (Join-Path $ServerRoot 'Configuration\ServerSettings.cfg'))) {
        Write-Host '  The server has never run here, so it needs one start to create its settings files.'
        Write-Host "  A server window will open. When it has finished loading, type  quit  in it and press Enter." -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'Start-LocalServer.ps1') -ServerRoot $ServerRoot -Yes
        if (-not (Wait-ServerExit 20)) { throw 'The server is still running after 20 minutes. Type quit in its window, then run Set up again.' }
    }
    Write-Host '  Settings files present.'

    Write-Step 'Step 3/4  Oxide mod framework'
    $managedHasOxide = @(Get-RealmManagedDirs $ServerRoot | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Managed\Oxide.Core.dll') }).Count -gt 0
    if ($managedHasOxide) {
        Write-Host '  Already installed.'
    } else {
        if (-not (Test-Path -LiteralPath $OxideZip)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $OxideZip) | Out-Null
            Write-Host "  Downloading $OxideUrl"
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $OxideUrl -OutFile "$OxideZip.part" -UseBasicParsing
            Move-Item -LiteralPath "$OxideZip.part" -Destination $OxideZip -Force
        }
        $dataFolder = 'ROK_Data'
        $dirs = @(Get-RealmManagedDirs $ServerRoot | ForEach-Object { $_.Name })
        if ($dirs.Count -gt 0 -and $dirs -notcontains 'ROK_Data') { $dataFolder = $dirs[0] }
        # Install-Oxide.ps1 checks the SHA-256 and backs up every file it overwrites.
        & (Join-Path $PSScriptRoot 'Install-Oxide.ps1') -ZipPath $OxideZip -ServerRoot $ServerRoot -DataFolder $dataFolder -Yes
    }

    Write-Step 'Step 4/4  Realm plugins'
    & (Join-Path $PSScriptRoot 'Deploy-Plugins.ps1') -ServerRoot $ServerRoot -CreateTarget -Yes

    if (-not (Find-Node)) {
        Write-Host ''
        Write-Host '  Node.js is needed for the launcher and the stream overlay (the server itself works without it).'
        $winget = Get-Command winget -ErrorAction SilentlyContinue
        if ($winget -and (Read-Host '  Install Node.js 22 LTS now with Windows Package Manager? (Y/N)') -match '^[Yy]') {
            & $winget.Source install --id OpenJS.NodeJS.22 --exact --accept-package-agreements --accept-source-agreements
            Write-Host '  Close this window and double-click Realm.bat again so Windows picks up Node.js.' -ForegroundColor Cyan
        } else {
            Write-Warning 'Install Node.js 22 LTS from https://nodejs.org, then double-click Realm.bat again.'
        }
    }
    Write-Host ''
    Write-Host 'Setup finished. Choose 2 (Play) next.' -ForegroundColor Green
}

function Invoke-Play {
    if (-not (Test-Path -LiteralPath ($ServerRoot + '\' + $script:RealmMarkerName))) {
        throw 'The test server is not set up yet. Choose 1 (Set up everything) first.'
    }
    Assert-RealmTestCopy $ServerRoot | Out-Null
    if (@(Get-RealmServerProcess $ServerRoot).Count -gt 0) {
        Write-Host '  The server is already running.'
    } else {
        Write-Step 'Starting the server'
        & (Join-Path $PSScriptRoot 'Start-LocalServer.ps1') -ServerRoot $ServerRoot -Yes
    }

    Write-Step 'Starting the Chronicle (stream overlay)'
    $node = Find-Node
    if ($node) {
        $oxideDir = Get-RealmOxideDir $ServerRoot
        if (-not $oxideDir) { $oxideDir = Join-Path $ServerRoot 'oxide' }
        $dataDir = Join-Path $oxideDir 'data'
        $chronicle = Join-Path $RepoRoot 'chronicle\server.js'
        Start-Process -FilePath $node -ArgumentList @("`"$chronicle`"", '--data', "`"$dataDir`"") -WorkingDirectory (Split-Path $chronicle)
        Write-Host '  Overlay for OBS : http://127.0.0.1:8787/overlay'
        Write-Host '  Chronicle page  : http://127.0.0.1:8787/realm'
    } else {
        Write-Warning 'Node.js not found; skipping the Chronicle. See Set up for how to get it.'
    }

    Write-Step 'Opening the launcher'
    $exe = Get-ChildItem -LiteralPath $RepoRoot -Filter 'RealmLauncher*.exe' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($exe) {
        Start-Process -FilePath $exe.FullName -WorkingDirectory $RepoRoot
    } elseif ((Get-Command npm -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $RepoRoot 'launcher\package.json'))) {
        $launcherDir = Join-Path $RepoRoot 'launcher'
        if (-not (Test-Path (Join-Path $launcherDir 'node_modules'))) {
            Write-Host '  First launch: installing launcher dependencies (one time, a few minutes)...'
            Push-Location $launcherDir
            try { & npm install --no-audit --no-fund } finally { Pop-Location }
        }
        Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', 'npm start' -WorkingDirectory $launcherDir -WindowStyle Minimized
    } else {
        Write-Host '  No launcher found. Start Reign of Kings from Steam instead.'
    }

    Write-Host ''
    Write-Host 'When the server window shows it has loaded: press PLAY in the launcher (or start the game in Steam),' -ForegroundColor Green
    Write-Host 'then use direct connect with 127.0.0.1 and port 7350. Type  quit  in the server window to stop it.' -ForegroundColor Green
    Write-Host 'If Windows asks to allow the server through the firewall, choose Cancel; it is not needed on this PC.'
}

$menu = [ordered]@{
    '1' = @('Set up everything (first time)', { Invoke-Setup })
    '2' = @('Play: start server + overlay + launcher', { Invoke-Play })
    '3' = @('Update plugins', { & (Join-Path $PSScriptRoot 'Deploy-Plugins.ps1') -ServerRoot $ServerRoot -CreateTarget -Yes })
    '4' = @('Back up the world', { & (Join-Path $PSScriptRoot 'Backup-Saves.ps1') -ServerRoot $ServerRoot -Yes })
    '5' = @('Undo Oxide (back to vanilla test server)', { & (Join-Path $PSScriptRoot 'Install-Oxide.ps1') -Rollback -ServerRoot $ServerRoot })
}

while ($true) {
    $choice = $Action
    if (-not $choice) {
        Write-Host ''
        Write-Host '  REALM  -  Reign of Kings community server' -ForegroundColor DarkYellow
        foreach ($k in $menu.Keys) { Write-Host "   $k  $($menu[$k][0])" }
        Write-Host '   Q  Quit'
        $choice = Read-Host '  Choose'
    }
    if ($choice -match '^[Qq]') { break }
    if (-not $menu.Contains($choice)) {
        Write-Host '  Pick a number from the list.'
        if ($Action) { break }
        continue
    }
    try {
        & $menu[$choice][1]
    } catch {
        Write-Host ''
        Write-Host "PROBLEM: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'Nothing else was changed. Copy this message to Claude if you are stuck.'
    }
    if ($Action) { break }
}
