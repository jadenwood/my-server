# Shared helpers for the Realm server scripts. Dot-sourced by the other scripts; does nothing on its own.
# Windows PowerShell 5.1 compatible. Nothing in this folder touches the firewall, the router or the Steam copy.

Set-StrictMode -Version 2.0

$script:RealmSteamServer = 'G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server'
$script:RealmTestRoot    = 'G:\RealmTest'
$script:RealmTestServer  = 'G:\RealmTest\server'
$script:RealmBackupDir   = 'G:\RealmTest\backups'
$script:RealmMarkerName  = '.realm-test-copy'

function Get-RealmFullPath([string]$Path) {
    # Resolves a path even if it does not exist yet, and drops any trailing separator.
    $full = [System.IO.Path]::GetFullPath($Path)
    return $full.TrimEnd('\', '/')
}

function Test-RealmPathInside([string]$Child, [string]$Parent) {
    $c = (Get-RealmFullPath $Child) + '\'
    $p = (Get-RealmFullPath $Parent) + '\'
    return $c.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-RealmTestCopy([string]$ServerRoot) {
    # Every script that writes to a server folder calls this first. It refuses anything that is not
    # a copy made by New-TestServer.ps1, and anything under a Steam library.
    $root = Get-RealmFullPath $ServerRoot
    if ($root -match '\\steamapps(\\|$)') {
        throw "Refusing: '$root' is inside a Steam library. Realm scripts only modify the test copy (default $script:RealmTestServer)."
    }
    if ($root -match '(^|\\)Steam(\\|$)' -or $root -match '^[Cc]:') {
        throw "Refusing: '$root' is on C: or inside a Steam folder. Realm scripts only modify the test copy (default $script:RealmTestServer)."
    }
    if (Test-RealmPathInside $root $script:RealmSteamServer) {
        throw "Refusing: '$root' is the Steam server folder."
    }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Server folder '$root' does not exist. Run New-TestServer.ps1 first."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $root $script:RealmMarkerName))) {
        throw "Refusing: '$root' has no $script:RealmMarkerName marker, so it was not created by New-TestServer.ps1."
    }
    return $root
}

function Get-RealmServerExe([string]$ServerRoot, [ValidateSet('Server', 'ROK')][string]$Name = 'Server') {
    $exe = Join-Path $ServerRoot "$Name.exe"
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    return $null
}

function Get-RealmServerProcess([string]$ServerRoot) {
    # Server.exe (wrapper) and ROK.exe (game) both count as "running" when they live in this folder.
    $root = Get-RealmFullPath $ServerRoot
    Get-Process -Name 'Server', 'ROK' -ErrorAction SilentlyContinue | Where-Object {
        $p = $null
        try { $p = $_.Path } catch { }
        $p -and (Test-RealmPathInside $p $root)
    }
}

function Assert-RealmServerStopped([string]$ServerRoot) {
    $procs = @(Get-RealmServerProcess $ServerRoot)
    if ($procs.Count -gt 0) {
        $list = ($procs | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
        throw "The test server is running: $list. Stop it first (type 'quit' in its console, or close the window)."
    }
}

function Get-RealmManagedDirs([string]$ServerRoot) {
    # Unity keeps managed assemblies in <Exe>_Data\Managed. Oxide's zip assumes ROK_Data.
    Get-ChildItem -LiteralPath $ServerRoot -Directory -Filter '*_Data' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Managed') -PathType Container }
}

function Get-RealmOxideDir([string]$ServerRoot) {
    # Oxide.Core creates 'oxide' under the working directory [SRC OxideMod.cs:125-149]; Start-LocalServer.ps1
    # sets that to the server root. Community guides mention Saves\oxide [SECONDARY], so it is checked too.
    foreach ($rel in @('oxide', 'Saves\oxide')) {
        $p = Join-Path $ServerRoot $rel
        if (Test-Path -LiteralPath (Join-Path $p 'plugins') -PathType Container) { return $p }
    }
    return $null
}

function Get-RealmTimestamp { (Get-Date).ToString('yyyyMMdd-HHmmss') }

function Read-RealmTextFile([string]$Path) {
    # Returns @{ Text; Encoding } so a rewrite keeps the original encoding (BOM or not).
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $enc = New-Object System.Text.UTF8Encoding($false)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $enc = New-Object System.Text.UTF8Encoding($true)
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $enc = [System.Text.Encoding]::Unicode
    }
    $reader = New-Object System.IO.StreamReader($Path, $enc, $true)
    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
    return @{ Text = $text; Encoding = $enc }
}

function Get-RealmCfgValue([string]$Path, [string]$Key) {
    # Config format: key = 'value'  (AMP regex ^(?<key>.+?) = '(?<value>.*?)'.*$) [VERIFIED-PRIMARY]
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $pattern = '^\s*' + [regex]::Escape($Key) + "\s*=\s*'(?<v>.*?)'"
    foreach ($line in (Read-RealmTextFile $Path).Text -split "`r?`n") {
        $m = [regex]::Match($line, $pattern)
        if ($m.Success) { return $m.Groups['v'].Value }
    }
    return $null
}

function Set-RealmCfgValues([string]$Path, [hashtable]$Values, [string]$BackupDir) {
    # Rewrites existing key lines only. Returns a list of 'key: old -> new' strings for display.
    # Keys missing from the file are reported, never appended, because the game owns the file layout.
    $file = Read-RealmTextFile $Path
    $newline = if ($file.Text -match "`r`n") { "`r`n" } else { "`n" }
    $lines = $file.Text -split "`r?`n"
    $changes = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Values.Keys) {
        $pattern = '^(?<pre>\s*' + [regex]::Escape($key) + "\s*=\s*')(?<v>.*?)(?<post>'.*)$"
        $found = $false
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $m = [regex]::Match($lines[$i], $pattern)
            if (-not $m.Success) { continue }
            $found = $true
            if ($m.Groups['v'].Value -ne $Values[$key]) {
                $changes.Add("${key}: '$($m.Groups['v'].Value)' -> '$($Values[$key])'")
                $lines[$i] = $m.Groups['pre'].Value + $Values[$key] + $m.Groups['post'].Value
            }
        }
        if (-not $found) { Write-Warning "Key '$key' not found in $Path; left unchanged." }
    }
    if ($changes.Count -gt 0) {
        if ($BackupDir) {
            New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
            Copy-Item -LiteralPath $Path -Destination (Join-Path $BackupDir ((Split-Path $Path -Leaf) + '.' + (Get-RealmTimestamp) + '.bak'))
        }
        [System.IO.File]::WriteAllText($Path, ($lines -join $newline), $file.Encoding)
    }
    return $changes
}

function Format-RealmBytes([double]$Bytes) {
    if ($Bytes -ge 1GB) { return '{0:N1} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    return '{0:N0} KB' -f ($Bytes / 1KB)
}

function Confirm-RealmStep($Cmdlet, [string]$Target, [string]$Action, [string]$Question, [bool]$Yes) {
    # -WhatIf: ShouldProcess prints "What if: ..." and returns $false, so nothing runs.
    # Otherwise ask once with a plain-language question unless -Yes was given.
    if (-not $Cmdlet.ShouldProcess($Target, $Action)) { return $false }
    if ($Yes) { return $true }
    return $Cmdlet.ShouldContinue($Question, 'Realm')
}
