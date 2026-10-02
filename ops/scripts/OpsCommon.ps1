# Shared helpers for the Realm ops scripts (ops/scripts). Dot-sourced by the other scripts; does nothing on its own.
# Windows PowerShell 5.1 compatible (no ternary, no ??, no PS6+ parameters). Also runs under PowerShell 7 so the
# test suite in ops/tests can exercise it on Linux.
#
# Rules this file enforces for every ops script:
#   - Never write into a Steam library or the game's own folders except through SteamCMD itself.
#   - Never hold secrets in the repo: the webhook URL and the rclone config live in local files the owner creates.
#   - Never print a webhook token: every log line goes through Protect-OpsText.

Set-StrictMode -Version 2.0

$script:OpsAppId = 381690          # Reign of Kings Dedicated Server (docs/server-reference.md section 1)
$script:OpsGameAppId = 344760      # Reign of Kings (client)
$script:OpsInstanceMarker = '.realm-test-copy'   # written by server/New-TestServer.ps1 and Realm Steward
$script:OpsMasterMarker = '.realm-ops-master'    # written by Install-RealmServer.ps1
$script:OpsSteamCmdZipUrl = 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip'
$script:OpsBackupFormat = 'realm-ops-backup/1'
$script:OpsIsWindows = ([System.IO.Path]::DirectorySeparatorChar -eq '\')

# ---------------------------------------------------------------- text, time, paths

function Protect-OpsText([string]$Text) {
    # Removes Discord webhook tokens and anything that looks like an S3 secret from text meant for logs or screens.
    if ($null -eq $Text) { return '' }
    $t = [regex]::Replace($Text, '(?i)(/api/webhooks/\d+/)[A-Za-z0-9_\-]+', '$1<redacted>')
    $t = [regex]::Replace($t, '(?i)((secret_access_key|access_key_id|session_token|password|pass)\s*[=:]\s*)\S+', '$1<redacted>')
    return $t
}

function Get-OpsUtcStamp([datetime]$When = [datetime]::UtcNow) {
    return $When.ToUniversalTime().ToString('yyyyMMdd-HHmmss') + 'Z'
}

function ConvertFrom-OpsUtcStamp([string]$Stamp) {
    # Parses the yyyyMMdd-HHmmssZ stamp used in backup names. Returns $null when it does not parse.
    $m = [regex]::Match([string]$Stamp, '(\d{8}-\d{6})Z')
    if (-not $m.Success) { return $null }
    $dt = [datetime]::MinValue
    $ok = [datetime]::TryParseExact($m.Groups[1].Value, 'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture,
        ([Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal), [ref]$dt)
    if (-not $ok) { return $null }
    return $dt
}

function Get-OpsIsoNow { return [datetime]::UtcNow.ToString('o') }

function Get-OpsSlug([string]$Name) {
    # File-name-safe, lower-case id for a server name ("Realm 1" -> "realm-1").
    $s = ([string]$Name).ToLowerInvariant()
    $s = [regex]::Replace($s, '[^a-z0-9]+', '-').Trim('-')
    if (-not $s) { throw "Server name '$Name' has no letters or digits; it cannot be used for file names." }
    if ($s.Length -gt 40) { $s = $s.Substring(0, 40).Trim('-') }
    return $s
}

function Get-OpsFullPath([string]$Path) {
    if (-not $Path) { throw 'Empty path.' }
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($full)
    while ($full.Length -gt $root.Length -and ($full.EndsWith('\') -or $full.EndsWith('/'))) { $full = $full.Substring(0, $full.Length - 1) }
    return $full
}

function Test-OpsPathInside([string]$Child, [string]$Parent) {
    # True when Child is Parent or below it. Case-insensitive on Windows.
    $sep = [string][System.IO.Path]::DirectorySeparatorChar
    $c = (Get-OpsFullPath $Child)
    $p = (Get-OpsFullPath $Parent)
    $cmp = [System.StringComparison]::Ordinal
    if ($script:OpsIsWindows) { $cmp = [System.StringComparison]::OrdinalIgnoreCase }
    if ([string]::Equals($c, $p, $cmp)) { return $true }
    if (-not $p.EndsWith($sep)) { $p = $p + $sep }
    return $c.StartsWith($p, $cmp)
}

function Get-OpsUnsafeDirReason([string]$FullPath) {
    # Pure check on a full path string (Windows or POSIX). Returns a plain-English reason, or $null when the folder
    # may be written by ops scripts (backups, staging, logs, SteamCMD, a restore drill).
    $p = [string]$FullPath
    if (-not $p) { return 'No folder given.' }
    $w = $p.Replace('/', '\').TrimEnd('\')
    if ($w -match '^[A-Za-z]:$' -or $w -eq '') { return "'$p' is the root of a drive. Use a folder such as G:\RealmOps." }
    if ($w -match '(?i)\\steamapps(\\|$)') { return "'$p' is inside a Steam library. Ops scripts never write there (only SteamCMD does, for the server install)." }
    if ($w -match '(?i)^[A-Za-z]:\\(Windows|Program Files|Program Files \(x86\)|ProgramData|Users|Recovery|System Volume Information|\$Recycle\.Bin)(\\|$)') {
        return "'$p' is a Windows system or profile folder. Use a data folder such as G:\RealmOps."
    }
    if ($w -match '(?i)\\(Reign Of Kings|Reign Of Kings Dedicated Server)(\\|$)' -and $w -match '(?i)\\common\\') {
        return "'$p' looks like a Steam game folder."
    }
    if ($p -match '^/(bin|boot|dev|etc|lib|lib64|proc|sbin|sys|usr)(/|$)' -or $p -eq '/') { return "'$p' is a system folder." }
    return $null
}

function Assert-OpsSafeDir([string]$Path, [string]$What = 'folder') {
    $full = Get-OpsFullPath $Path
    $why = Get-OpsUnsafeDirReason $full
    if ($why) { throw "Refusing to use $What '$full': $why" }
    return $full
}

# ---------------------------------------------------------------- ACLs (scripts that run as SYSTEM)

# Broad groups that include ordinary (non-admin) accounts. If one of them may change a file that a SYSTEM task runs
# or reads its program paths from, any local user can run code as SYSTEM.
$script:OpsBroadSids = @{
    'S-1-1-0' = 'Everyone'; 'S-1-5-11' = 'Authenticated Users'; 'S-1-5-32-545' = 'Users'; 'S-1-5-4' = 'Interactive'
    'S-1-5-32-546' = 'Guests'; 'S-1-5-2' = 'Network'; 'S-1-5-7' = 'Anonymous'
}
# FileSystemRights bits that let the holder change or replace the object: WriteData/CreateFiles, AppendData/
# CreateDirectories, DeleteSubdirectoriesAndFiles, Delete, ChangePermissions, TakeOwnership, GENERIC_WRITE, GENERIC_ALL.
$script:OpsWriteMask = 0x2 -bor 0x4 -bor 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000

function Get-OpsBroadWriters($Rules) {
    # Pure. Rules: objects with Sid (string), Rights (int FileSystemRights), Allow (bool), InheritOnly (bool).
    # Returns the names of broad groups that are allowed to modify the object itself. Deny rules are ignored
    # (conservative: a matching deny is rare and this check prefers a false alarm to a missed hole).
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($r in @($Rules)) {
        if ($null -eq $r -or -not $r.Allow -or $r.InheritOnly) { continue }
        $sid = [string]$r.Sid
        if (-not $script:OpsBroadSids.ContainsKey($sid)) { continue }
        if (([long]$r.Rights -band $script:OpsWriteMask) -eq 0) { continue }
        $name = $script:OpsBroadSids[$sid]
        if (-not $hits.Contains($name)) { $hits.Add($name) }
    }
    return $hits.ToArray()
}

function Get-OpsAclRules([string]$Path) {
    # Windows only: the access rules of a file or folder as plain objects for Get-OpsBroadWriters.
    $acl = Get-Acl -LiteralPath $Path
    $out = @()
    foreach ($a in $acl.Access) {
        $sid = $null
        try { $sid = $a.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $sid = [string]$a.IdentityReference }
        $out += New-Object PSObject -Property ([ordered]@{
            Sid = $sid; Rights = [long][int]$a.FileSystemRights
            Allow = ($a.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow)
            InheritOnly = (($a.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)
        })
    }
    return $out
}

function Get-OpsWritableByUsersProblems([string[]]$Paths) {
    # Windows only. Returns one sentence per path that a broad group (Users, Authenticated Users, Everyone...) may modify.
    $p = New-Object System.Collections.Generic.List[string]
    foreach ($path in $Paths) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        $who = @(Get-OpsBroadWriters (Get-OpsAclRules $path))
        if ($who.Count -gt 0) { $p.Add(("{0} can be changed by {1}." -f $path, ($who -join ', '))) }
    }
    return $p.ToArray()
}

function New-OpsDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
    return (Get-OpsFullPath $Path)
}

function Write-OpsJsonFile([string]$Path, $Object) {
    # UTF-8 without BOM, written to a temp file and moved into place so a crash never leaves half a file.
    $json = ConvertTo-Json -InputObject $Object -Depth 8
    $dir = Split-Path -Parent $Path
    if ($dir) { New-OpsDirectory $dir | Out-Null }
    $tmp = $Path + '.tmp'
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    Move-Item -LiteralPath $tmp -Destination $Path
}

function Read-OpsJsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $text = [System.IO.File]::ReadAllText($Path)
    if (-not $text.Trim()) { return $null }
    return ($text | ConvertFrom-Json)
}

function Get-OpsFileSha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $fs = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try { $bytes = $sha.ComputeHash($fs) } finally { $fs.Dispose(); $sha.Dispose() }
    return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
}

function Format-OpsBytes([double]$Bytes) {
    if ($Bytes -ge 1GB) { return '{0:N1} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    return '{0:N0} KB' -f ($Bytes / 1KB)
}

function Format-OpsDuration([TimeSpan]$Span) {
    if ($Span.TotalMinutes -lt 1) { return ('{0:N0} s' -f $Span.TotalSeconds) }
    if ($Span.TotalHours -lt 1) { return ('{0:N0} min' -f $Span.TotalMinutes) }
    if ($Span.TotalDays -lt 1) { return ('{0} h {1} min' -f [int][math]::Floor($Span.TotalHours), $Span.Minutes) }
    return ('{0} d {1} h' -f [int][math]::Floor($Span.TotalDays), $Span.Hours)
}

# ---------------------------------------------------------------- logging

$script:OpsLogFile = $null

function Start-OpsLog([string]$OpsRoot, [string]$Name) {
    $dir = New-OpsDirectory (Join-Path $OpsRoot 'logs')
    $script:OpsLogFile = Join-Path $dir ('{0}-{1}.log' -f $Name, [datetime]::UtcNow.ToString('yyyyMMdd'))
    return $script:OpsLogFile
}

function Write-OpsLog([string]$Message, [ValidateSet('INFO', 'WARN', 'ERROR', 'OK')][string]$Level = 'INFO') {
    $clean = Protect-OpsText $Message
    $line = '{0} [{1}] {2}' -f [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'), $Level, $clean
    if ($script:OpsLogFile) {
        try { [System.IO.File]::AppendAllText($script:OpsLogFile, $line + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false))) } catch { }
    }
    $color = 'Gray'
    if ($Level -eq 'WARN') { $color = 'Yellow' } elseif ($Level -eq 'ERROR') { $color = 'Red' } elseif ($Level -eq 'OK') { $color = 'Green' }
    Write-Host $clean -ForegroundColor $color
}

# ---------------------------------------------------------------- config

function Get-OpsProp($Object, [string]$Name, $Default = $null) {
    # Reads a dotted property path from a ConvertFrom-Json object; returns $Default when any part is missing or null.
    $cur = $Object
    foreach ($part in $Name.Split('.')) {
        if ($null -eq $cur) { return $Default }
        $prop = $cur.PSObject.Properties[$part]
        if ($null -eq $prop) { return $Default }
        $cur = $prop.Value
    }
    if ($null -eq $cur) { return $Default }
    return $cur
}

function Test-OpsPlaceholder($Value) {
    return ($null -eq $Value) -or ([string]$Value -eq '') -or ([string]$Value -match '(?i)REPLACE|<your|example\.invalid')
}

function Test-OpsDiscordWebhookUrl([string]$Url) {
    # Only real Discord webhook URLs are accepted, so a typo can never send the payload somewhere else.
    if (-not $Url) { return $false }
    return [regex]::IsMatch($Url, '^https://(discord\.com|discordapp\.com|ptb\.discord\.com|canary\.discord\.com)/api/webhooks/\d{5,25}/[A-Za-z0-9_\-]{20,120}$')
}

function Read-OpsConfig([string]$Path) {
    if (-not $Path) { throw 'Pass -ConfigPath (for example G:\RealmOps\realm-ops.json). Start from ops\config\realm-ops.example.json.' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Config file not found: $Path. Copy ops\config\realm-ops.example.json there and fill it in." }
    try { $cfg = Read-OpsJsonFile $Path } catch { throw "Config file $Path is not valid JSON: $($_.Exception.Message)" }
    if ($null -eq $cfg) { throw "Config file $Path is empty." }
    return $cfg
}

function Get-OpsServers($Config, [string[]]$Only = @()) {
    # Returns normalized server entries: Name, Slug, Root, Host, GamePort, PingPort, QueryPort, TcpCheck.
    $list = @()
    foreach ($s in @(Get-OpsProp $Config 'servers' @())) {
        $name = [string](Get-OpsProp $s 'name' '')
        $game = [int](Get-OpsProp $s 'gamePort' 7350)
        $entry = New-Object PSObject -Property ([ordered]@{
            Name = $name
            Slug = (Get-OpsSlug $name)
            Root = [string](Get-OpsProp $s 'root' '')
            Host = [string](Get-OpsProp $s 'host' '127.0.0.1')
            GamePort = $game
            PingPort = [int](Get-OpsProp $s 'pingPort' $game)
            QueryPort = [int](Get-OpsProp $s 'queryPort' 27015)
            TcpCheck = [bool](Get-OpsProp $s 'tcpCheck' $true)
            Monitor = [bool](Get-OpsProp $s 'monitor' $true)
            Backup = [bool](Get-OpsProp $s 'backup' $true)
        })
        if ($Only.Count -gt 0 -and -not (@($Only | Where-Object { $_ -ieq $entry.Name -or $_ -ieq $entry.Slug }).Count -gt 0)) { continue }
        $list += $entry
    }
    if ($Only.Count -gt 0 -and $list.Count -eq 0) { throw "No server in the config matches: $($Only -join ', ')." }
    return $list
}

function Test-OpsConfig($Config, [ValidateSet('Install', 'Update', 'Backup', 'Monitor', 'Restore', 'All')][string]$For = 'All') {
    # Returns a list of problems (empty when fine). Each problem is one plain-English sentence.
    $p = New-Object System.Collections.Generic.List[string]
    $opsRoot = Get-OpsProp $Config 'opsRoot'
    if (Test-OpsPlaceholder $opsRoot) { $p.Add('opsRoot is not set (for example G:\RealmOps).') }
    elseif (Get-OpsUnsafeDirReason (Get-OpsFullPath $opsRoot)) { $p.Add('opsRoot: ' + (Get-OpsUnsafeDirReason (Get-OpsFullPath $opsRoot))) }

    $servers = @()
    try { $servers = @(Get-OpsServers $Config) } catch { $p.Add($_.Exception.Message) }
    if ($For -ne 'Install' -and $servers.Count -eq 0) { $p.Add('servers is empty: list at least one server.') }
    if ($servers.Count -gt 4) { $p.Add('servers lists more than 4 entries; Realm Steward runs at most four.') }
    $seen = @{}
    foreach ($s in $servers) {
        if (-not $s.Name) { $p.Add('A server has no name.'); continue }
        if ($seen.ContainsKey($s.Slug)) { $p.Add("Two servers share the name/slug '$($s.Slug)'.") }
        $seen[$s.Slug] = $true
        foreach ($port in @($s.GamePort, $s.PingPort, $s.QueryPort)) {
            if ($port -lt 1 -or $port -gt 65535) { $p.Add("Server '$($s.Name)' has an invalid port $port.") }
        }
        if ($s.QueryPort -eq $s.GamePort) { $p.Add("Server '$($s.Name)': queryPort (steamAuthPort, UDP) must differ from gamePort.") }
        if ($For -in @('Backup', 'Update', 'Restore', 'All') -and $s.Backup) {
            if (Test-OpsPlaceholder $s.Root) { $p.Add("Server '$($s.Name)' has no root folder (for example G:\RealmTest\server).") }
        }
    }

    if ($For -in @('Install', 'Update', 'All')) {
        $master = Get-OpsProp $Config 'masterServerDir'
        if (Test-OpsPlaceholder $master) { $p.Add('masterServerDir is not set (the SteamCMD install of app 381690).') }
        if (Test-OpsPlaceholder (Get-OpsProp $Config 'steamcmdDir')) { $p.Add('steamcmdDir is not set.') }
    }
    if ($For -in @('Backup', 'Update', 'Restore', 'All')) {
        if (Test-OpsPlaceholder (Get-OpsProp $Config 'backup.localDir')) { $p.Add('backup.localDir is not set.') }
        $remote = Get-OpsProp $Config 'backup.remote'
        if (-not (Test-OpsPlaceholder $remote)) {
            if ($remote -notmatch '^[A-Za-z0-9_\-\. ]+:[^\s]*$') { $p.Add("backup.remote '$remote' must look like 'remotename:bucket/folder'.") }
            if (Test-OpsPlaceholder (Get-OpsProp $Config 'backup.rcloneExe')) { $p.Add('backup.rcloneExe is not set.') }
            if (Test-OpsPlaceholder (Get-OpsProp $Config 'backup.rcloneConfig')) { $p.Add('backup.rcloneConfig is not set (the rclone.conf you created with "rclone config").') }
        }
        $keep = [int](Get-OpsProp $Config 'backup.keepLocal' 14)
        if ($keep -lt 1) { $p.Add('backup.keepLocal must be at least 1.') }
        $minKeep = [int](Get-OpsProp $Config 'backup.remoteMinKeep' 7)
        if ($minKeep -lt 1) { $p.Add('backup.remoteMinKeep must be at least 1.') }
    }
    if ($For -in @('Monitor', 'All')) {
        $hook = Get-OpsProp $Config 'monitor.discordWebhookUrl'
        if (-not (Test-OpsPlaceholder $hook) -and -not (Test-OpsDiscordWebhookUrl $hook)) {
            $p.Add('monitor.discordWebhookUrl is not a Discord webhook URL (https://discord.com/api/webhooks/<id>/<token>).')
        }
        if ([int](Get-OpsProp $Config 'monitor.failuresBeforeAlert' 2) -lt 1) { $p.Add('monitor.failuresBeforeAlert must be at least 1.') }
    }
    return $p.ToArray()
}

function Assert-OpsConfig($Config, [string]$For) {
    $problems = @(Test-OpsConfig $Config $For)
    if ($problems.Count -gt 0) { throw ("Config problems:`n  - " + ($problems -join "`n  - ")) }
}

# ---------------------------------------------------------------- lock (one ops job at a time)

function Enter-OpsLock([string]$OpsRoot, [string]$Name) {
    # Creates <opsRoot>\state\ops.lock. Refuses while another live ops process holds it; takes over a stale lock.
    $dir = New-OpsDirectory (Join-Path $OpsRoot 'state')
    $path = Join-Path $dir 'ops.lock'
    for ($i = 0; $i -lt 2; $i++) {
        try {
            $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            $bytes = [System.Text.Encoding]::UTF8.GetBytes(('{0}|{1}|{2}' -f $PID, $Name, (Get-OpsIsoNow)))
            $fs.Write($bytes, 0, $bytes.Length)
            $fs.Dispose()
            return $path
        } catch [System.IO.IOException] {
            $held = ''
            try { $held = [System.IO.File]::ReadAllText($path) } catch { }
            $parts = $held.Split('|')
            $holder = 0
            [void][int]::TryParse($parts[0], [ref]$holder)
            $alive = $false
            if ($holder -gt 0 -and $holder -ne $PID) { $alive = $null -ne (Get-Process -Id $holder -ErrorAction SilentlyContinue) }
            if ($alive) {
                $what = 'another task'
                if ($parts.Length -gt 1) { $what = $parts[1] }
                throw "Another Realm ops job ($what, pid $holder) is running. Try again when it finishes. Lock: $path"
            }
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
    throw "Could not take the ops lock at $path."
}

function Exit-OpsLock([string]$LockPath) {
    if ($LockPath -and (Test-Path -LiteralPath $LockPath)) { Remove-Item -LiteralPath $LockPath -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- processes

function ConvertTo-OpsArgString([string[]]$Arguments) {
    # Joins arguments into one Windows command line using the MSVCRT / CommandLineToArgvW quoting rules,
    # so paths with spaces and quotes survive (ProcessStartInfo.ArgumentList does not exist in .NET Framework).
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($a in $Arguments) {
        if ($null -eq $a) { $a = '' }
        if ($a -ne '' -and $a -notmatch '[\s"]') { $out.Add($a); continue }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('"')
        $slashes = 0
        foreach ($ch in $a.ToCharArray()) {
            if ($ch -eq '\') { $slashes++; continue }
            if ($ch -eq '"') { [void]$sb.Append('\' * ($slashes * 2 + 1)); [void]$sb.Append('"'); $slashes = 0; continue }
            if ($slashes -gt 0) { [void]$sb.Append('\' * $slashes); $slashes = 0 }
            [void]$sb.Append($ch)
        }
        if ($slashes -gt 0) { [void]$sb.Append('\' * ($slashes * 2)) }
        [void]$sb.Append('"')
        $out.Add($sb.ToString())
    }
    return ($out -join ' ')
}

function Invoke-OpsProcess([string]$FilePath, [string[]]$Arguments = @(), [int]$TimeoutSeconds = 3600, [string]$WorkingDirectory = '') {
    # Runs a native program, captures stdout and stderr without deadlocking, and never turns stderr text into a
    # PowerShell error (Windows PowerShell 5.1 does that with 2>&1 under ErrorActionPreference Stop).
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = ConvertTo-OpsArgString $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    try { [void]$proc.Start() } catch { throw "Could not start '$FilePath': $($_.Exception.Message)" }
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $finished = $proc.WaitForExit($TimeoutSeconds * 1000)
    if (-not $finished) {
        try { $proc.Kill() } catch { }
        $proc.WaitForExit(5000) | Out-Null
    } else {
        $proc.WaitForExit()   # flushes the async readers
    }
    $stdout = ''
    $stderr = ''
    try { $stdout = $outTask.Result } catch { }
    try { $stderr = $errTask.Result } catch { }
    $code = -1
    if ($finished) { $code = $proc.ExitCode }
    $proc.Dispose()
    return New-Object PSObject -Property ([ordered]@{
        ExitCode = $code; TimedOut = (-not $finished); StdOut = $stdout; StdErr = $stderr
        Output = (($stdout, $stderr) | Where-Object { $_ }) -join [Environment]::NewLine
    })
}

function Get-OpsServerProcesses([string]$ServerRoot) {
    # ROK.exe (the game) and Server.exe (the wrapper) count as running when their exe lives in this folder.
    if (-not $ServerRoot) { return @() }
    $root = Get-OpsFullPath $ServerRoot
    $found = @(Get-Process -Name 'ROK', 'Server' -ErrorAction SilentlyContinue | Where-Object {
        $exe = $null
        try { $exe = $_.Path } catch { }
        $exe -and (Test-OpsPathInside $exe $root)
    })
    return $found
}

# ---------------------------------------------------------------- SteamCMD

function Get-OpsSteamCmdArgs([string]$InstallDir, [switch]$Validate) {
    # force_install_dir must come before login in current SteamCMD builds. Anonymous login works for 381690
    # (AMP template SteamUpdateAnonymousLogin=True; Oxide's build pulls 381690 with SteamLogin=anonymous).
    $a = @('+@ShutdownOnFailedCommand', '1', '+@NoPromptForPassword', '1', '+force_install_dir', $InstallDir, '+login', 'anonymous', '+app_update', [string]$script:OpsAppId)
    if ($Validate) { $a += 'validate' }
    $a += '+quit'
    return , $a
}

function Get-OpsSteamCmdResult([string]$Output, [int]$ExitCode) {
    # Classifies SteamCMD output. Success lines are SteamCMD's own wording for app_update.
    $id = [string]$script:OpsAppId
    $r = [ordered]@{ Ok = $false; State = 'unknown'; Message = '' }
    if ($Output -match ("Success! App '" + $id + "' already up to date")) { $r.Ok = $true; $r.State = 'up-to-date'; $r.Message = 'Already up to date.' }
    elseif ($Output -match ("Success! App '" + $id + "' fully installed")) { $r.Ok = $true; $r.State = 'installed'; $r.Message = 'Installed or updated.' }
    $err = [regex]::Match($Output, "ERROR! (?<m>[^\r\n]+)")
    if ($err.Success) { $r.Ok = $false; $r.State = 'error'; $r.Message = $err.Groups['m'].Value.Trim() }
    elseif (-not $r.Ok) {
        if ($Output -match 'No subscription') { $r.State = 'error'; $r.Message = 'No subscription: the app could not be fetched anonymously.' }
        elseif ($Output -match '(?i)Disk write failure|not enough disk space') { $r.State = 'error'; $r.Message = 'Disk write failure (disk full or folder not writable).' }
        elseif ($Output -match '(?i)Rate Limit Exceeded') { $r.State = 'error'; $r.Message = 'Steam rate limit: wait 30 minutes and try again.' }
        elseif ($ExitCode -eq -1) { $r.State = 'timeout'; $r.Message = 'SteamCMD did not finish in time.' }
        else { $r.State = 'error'; $r.Message = "SteamCMD exited with code $ExitCode and no success line." }
    }
    return New-Object PSObject -Property $r
}

function Get-OpsAppManifest([string]$InstallDir) {
    # Reads <dir>\steamapps\appmanifest_381690.acf that SteamCMD writes next to a force_install_dir install.
    $acf = Join-Path (Join-Path $InstallDir 'steamapps') ('appmanifest_{0}.acf' -f $script:OpsAppId)
    if (-not (Test-Path -LiteralPath $acf -PathType Leaf)) { return $null }
    return ConvertFrom-OpsAcf ([System.IO.File]::ReadAllText($acf))
}

function ConvertFrom-OpsAcf([string]$Text) {
    $get = {
        param($k)
        $m = [regex]::Match($Text, '"' + $k + '"\s+"(?<v>[^"]*)"', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($m.Success) { return $m.Groups['v'].Value }
        return $null
    }
    return New-Object PSObject -Property ([ordered]@{
        AppId = (& $get 'appid'); BuildId = (& $get 'buildid'); StateFlags = (& $get 'StateFlags')
        LastUpdated = (& $get 'LastUpdated'); SizeOnDisk = (& $get 'SizeOnDisk')
    })
}

function Get-OpsMasterDirProblem([string]$MasterDir, $Config = $null) {
    # The "master" is the pristine SteamCMD install of app 381690. It may sit in a Steam-library-shaped path (the
    # Realm default G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server, which Realm Steward finds on its
    # own), but it must never be a Realm instance (those carry Oxide and saves) or a system folder.
    $full = Get-OpsFullPath $MasterDir
    $w = $full.Replace('/', '\')
    if ($w -match '^[A-Za-z]:\\?$' -or $full -eq '/') { return "'$full' is the root of a drive." }
    if ($w -match '(?i)^[A-Za-z]:\\(Windows|Program Files|Program Files \(x86\)|ProgramData|Users)(\\|$)') { return "'$full' is a Windows system or profile folder." }
    if ($w -match '(?i)\\Steam\\steamapps\\') { return "'$full' is inside the Steam client's own library. Use a separate folder so the Steam client and SteamCMD never manage the same files (default G:\SteamLibrary\steamapps\common\Reign Of Kings Dedicated Server on a machine without the Steam client)." }
    if (Test-Path -LiteralPath (Join-Path $full $script:OpsInstanceMarker)) { return "'$full' is a Realm server instance ($script:OpsInstanceMarker found). SteamCMD would overwrite its Oxide files; update the master and refresh instances instead." }
    if ($null -ne $Config) {
        foreach ($s in @(Get-OpsServers $Config)) {
            if ($s.Root -and -not (Test-OpsPlaceholder $s.Root) -and ((Test-OpsPathInside $full $s.Root) -or (Test-OpsPathInside $s.Root $full))) {
                return "'$full' overlaps the server instance '$($s.Name)' ($($s.Root))."
            }
        }
    }
    return $null
}

function Test-OpsServerFolder([string]$Dir) {
    # True when the folder holds a Reign of Kings dedicated server (ROK.exe or Server.exe).
    return (Test-Path -LiteralPath (Join-Path $Dir 'ROK.exe')) -or (Test-Path -LiteralPath (Join-Path $Dir 'Server.exe'))
}

# ---------------------------------------------------------------- A2S (Steam server query) and TCP checks

function Get-OpsA2SRequest([byte[]]$Challenge = $null) {
    # A2S_INFO request (Valve "Server queries"): FF FF FF FF 'T' "Source Engine Query\0" [challenge].
    $list = New-Object System.Collections.Generic.List[byte]
    $list.AddRange([byte[]](0xFF, 0xFF, 0xFF, 0xFF, 0x54))
    $list.AddRange([System.Text.Encoding]::ASCII.GetBytes("Source Engine Query`0"))
    if ($null -ne $Challenge) {
        if ($Challenge.Length -ne 4) { throw 'An A2S challenge is 4 bytes.' }
        $list.AddRange($Challenge)
    }
    return , $list.ToArray()
}

function Read-OpsCString([byte[]]$Buf, [ref]$Offset) {
    $start = $Offset.Value
    $end = [Array]::IndexOf($Buf, [byte]0, $start)
    if ($end -lt 0) { throw 'A2S reply has an unterminated string.' }
    $Offset.Value = $end + 1
    $s = [System.Text.Encoding]::UTF8.GetString($Buf, $start, $end - $start)
    if ($s.Length -gt 256) { $s = $s.Substring(0, 256) }
    return $s
}

function ConvertFrom-OpsA2SResponse([byte[]]$Buf) {
    # Returns @{Type='challenge'; Challenge=byte[4]} or @{Type='info'; ...}. Throws on anything else.
    if ($null -eq $Buf -or $Buf.Length -lt 5) { throw 'A2S reply too short.' }
    if ($Buf[0] -eq 0xFE -and $Buf[1] -eq 0xFF -and $Buf[2] -eq 0xFF -and $Buf[3] -eq 0xFF) { throw 'A2S reply is a split packet (not expected for A2S_INFO).' }
    if (-not ($Buf[0] -eq 0xFF -and $Buf[1] -eq 0xFF -and $Buf[2] -eq 0xFF -and $Buf[3] -eq 0xFF)) { throw 'A2S reply has an unknown header.' }
    $type = $Buf[4]
    if ($type -eq 0x41) {
        if ($Buf.Length -lt 9) { throw 'A2S challenge too short.' }
        $c = New-Object byte[] 4
        [Array]::Copy($Buf, 5, $c, 0, 4)
        return New-Object PSObject -Property ([ordered]@{ Type = 'challenge'; Challenge = $c })
    }
    if ($type -ne 0x49) { throw ('Unexpected A2S reply type 0x{0:x2}.' -f $type) }
    $o = 5
    $protocol = $Buf[$o]; $o++
    $name = Read-OpsCString $Buf ([ref]$o)
    $map = Read-OpsCString $Buf ([ref]$o)
    $folder = Read-OpsCString $Buf ([ref]$o)
    $game = Read-OpsCString $Buf ([ref]$o)
    if ($Buf.Length -lt $o + 9) { throw 'A2S info reply truncated.' }
    $appId = [BitConverter]::ToUInt16($Buf, $o); $o += 2
    $players = $Buf[$o]; $o++
    $max = $Buf[$o]; $o++
    $bots = $Buf[$o]; $o++
    $stype = [string][char]$Buf[$o]; $o++
    $env = [string][char]$Buf[$o]; $o++
    $vis = $Buf[$o]; $o++
    $vac = $Buf[$o]; $o++
    $version = ''
    if ($o -lt $Buf.Length) { try { $version = Read-OpsCString $Buf ([ref]$o) } catch { $version = '' } }
    return New-Object PSObject -Property ([ordered]@{
        Type = 'info'; Protocol = $protocol; Name = $name; Map = $map; Folder = $folder; Game = $game; AppId = $appId
        Players = $players; MaxPlayers = $max; Bots = $bots; ServerType = $stype; Environment = $env
        Passworded = ($vis -eq 1); Vac = ($vac -eq 1); Version = $version
    })
}

function Invoke-OpsA2SQuery([string]$HostName, [int]$Port, [int]$TimeoutMs = 2000) {
    # Sends A2S_INFO over UDP and answers up to two challenges. Never throws: returns Ok/Error.
    $result = [ordered]@{ Ok = $false; RttMs = $null; Info = $null; Error = $null }
    $udp = $null
    try {
        $udp = New-Object System.Net.Sockets.UdpClient
        $udp.Client.ReceiveTimeout = $TimeoutMs
        $udp.Connect($HostName, $Port)
        $challenge = $null
        for ($round = 0; $round -lt 3; $round++) {
            $req = Get-OpsA2SRequest $challenge
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            [void]$udp.Send($req, $req.Length)
            $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
            $reply = $udp.Receive([ref]$remote)
            $sw.Stop()
            $parsed = ConvertFrom-OpsA2SResponse $reply
            if ($parsed.Type -eq 'challenge') { $challenge = $parsed.Challenge; continue }
            $result.Ok = $true
            $result.RttMs = [int]$sw.ElapsedMilliseconds
            $result.Info = $parsed
            break
        }
        if (-not $result.Ok -and -not $result.Error) { $result.Error = 'Too many A2S challenges.' }
    } catch [System.Net.Sockets.SocketException] {
        $se = $_.Exception
        if ($se.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) { $result.Error = "No A2S answer within $TimeoutMs ms." }
        elseif ($se.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionReset -or $se.SocketErrorCode -eq [System.Net.Sockets.SocketError]::ConnectionRefused) {
            $result.Error = 'Nothing is listening on the query port (ICMP port unreachable).'
        } elseif ($se.SocketErrorCode -eq [System.Net.Sockets.SocketError]::HostNotFound -or $se.SocketErrorCode -eq [System.Net.Sockets.SocketError]::NoData) {
            $result.Error = "Host name '$HostName' does not resolve."
        } else { $result.Error = 'Socket error: ' + $se.SocketErrorCode }
    } catch {
        $msg = $_.Exception.Message
        if ($_.Exception.InnerException) { $msg = $_.Exception.InnerException.Message }
        $result.Error = $msg
    } finally {
        if ($udp) { $udp.Close() }
    }
    return New-Object PSObject -Property $result
}

function Test-OpsTcpPort([string]$HostName, [int]$Port, [int]$TimeoutMs = 2000) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $ar = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($ar)
        return $client.Connected
    } catch { return $false } finally { $client.Close() }
}

function Get-OpsServerVerdict([bool]$A2SOk, [bool]$TcpOk, [bool]$ProcessRunning) {
    # up: Steam query answers. degraded: the game is alive (ping port or process) but A2S is silent, which happens when
    # the server's Steam login failed (docs/join-and-scale.md 3.1) or the query port is blocked. down: nothing answers.
    if ($A2SOk) { return 'up' }
    if ($TcpOk -or $ProcessRunning) { return 'degraded' }
    return 'down'
}

# ---------------------------------------------------------------- alerting state machine

function Get-OpsAlertDecision($Previous, [string]$Observed, [datetime]$NowUtc, [int]$FailuresBeforeAlert = 2, [int]$RemindEveryMinutes = 60) {
    # Pure. Previous: $null or an object with Status, Since, Fails, Alerted, AlertedStatus, LastAlert (ISO strings).
    # Returns Send (bool), Kind ('down'|'degraded'|'recovered'|'reminder'|$null), DownFor (TimeSpan or $null) and State.
    $prevStatus = 'up'; $since = $NowUtc; $fails = 0; $alerted = $false; $alertedStatus = $null; $lastAlert = $null
    if ($null -ne $Previous) {
        $prevStatus = [string](Get-OpsProp $Previous 'Status' 'up')
        $s = Get-OpsProp $Previous 'Since' $null
        if ($s) { $since = ([datetime]$s).ToUniversalTime() }
        $fails = [int](Get-OpsProp $Previous 'Fails' 0)
        $alerted = [bool](Get-OpsProp $Previous 'Alerted' $false)
        $alertedStatus = Get-OpsProp $Previous 'AlertedStatus' $null
        $la = Get-OpsProp $Previous 'LastAlert' $null
        if ($la) { $lastAlert = ([datetime]$la).ToUniversalTime() }
    }
    $send = $false; $kind = $null; $downFor = $null
    if ($Observed -eq 'up') {
        if ($alerted) { $send = $true; $kind = 'recovered'; $downFor = $NowUtc - $since }
        $state = [ordered]@{ Status = 'up'; Since = $NowUtc.ToString('o'); Fails = 0; Alerted = $false; AlertedStatus = $null; LastAlert = $null }
        if ($prevStatus -eq 'up') { $state.Since = $since.ToString('o') }
    } else {
        if ($prevStatus -eq 'up') { $since = $NowUtc; $fails = 0 }
        $fails++
        if ($fails -ge $FailuresBeforeAlert) {
            if (-not $alerted -or $alertedStatus -ne $Observed) { $send = $true; $kind = $Observed }
            elseif ($RemindEveryMinutes -gt 0 -and $null -ne $lastAlert -and ($NowUtc - $lastAlert).TotalMinutes -ge $RemindEveryMinutes) { $send = $true; $kind = 'reminder' }
        }
        if ($send) { $alerted = $true; $alertedStatus = $Observed; $lastAlert = $NowUtc }
        $downFor = $NowUtc - $since
        $la2 = $null
        if ($null -ne $lastAlert) { $la2 = $lastAlert.ToString('o') }
        $state = [ordered]@{ Status = $Observed; Since = $since.ToString('o'); Fails = $fails; Alerted = $alerted; AlertedStatus = $alertedStatus; LastAlert = $la2 }
    }
    return New-Object PSObject -Property ([ordered]@{ Send = $send; Kind = $kind; DownFor = $downFor; State = (New-Object PSObject -Property $state) })
}

# ---------------------------------------------------------------- Discord

function Send-OpsDiscordMessage([string]$WebhookUrl, $Payload, [int]$MaxAttempts = 3, [int]$TimeoutMs = 10000) {
    # Posts JSON with HttpWebRequest (same behaviour on PowerShell 5.1 and 7). Honors 429 retry_after.
    # Returns @{ Ok; Status; Error }. Never throws, never logs the URL.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    $json = ConvertTo-Json -InputObject $Payload -Depth 8
    $body = [System.Text.Encoding]::UTF8.GetBytes($json)
    $last = [ordered]@{ Ok = $false; Status = 0; Error = 'not sent' }
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $wait = 0
        try {
            $req = [System.Net.HttpWebRequest][System.Net.WebRequest]::Create($WebhookUrl)
            $req.Method = 'POST'
            $req.ContentType = 'application/json; charset=utf-8'
            $req.UserAgent = 'RealmOps (+uptime monitor)'
            $req.Timeout = $TimeoutMs
            $req.ContentLength = $body.Length
            $rs = $req.GetRequestStream()
            try { $rs.Write($body, 0, $body.Length) } finally { $rs.Dispose() }
            $resp = $req.GetResponse()
            $code = [int]([System.Net.HttpWebResponse]$resp).StatusCode
            $resp.Close()
            return New-Object PSObject -Property ([ordered]@{ Ok = $true; Status = $code; Error = $null })
        } catch {
            $ex = $_.Exception
            while ($ex -and -not ($ex -is [System.Net.WebException]) -and $ex.InnerException) { $ex = $ex.InnerException }
            $code = 0
            $text = ''
            if ($ex -is [System.Net.WebException] -and $ex.Response) {
                $code = [int]([System.Net.HttpWebResponse]$ex.Response).StatusCode
                try {
                    $sr = New-Object System.IO.StreamReader($ex.Response.GetResponseStream())
                    $text = $sr.ReadToEnd(); $sr.Dispose()
                } catch { }
                $ex.Response.Close()
            }
            $last = [ordered]@{ Ok = $false; Status = $code; Error = (Protect-OpsText $_.Exception.Message) }
            if ($code -eq 429) {
                $wait = 2
                try { $ra = ($text | ConvertFrom-Json).retry_after; if ($ra) { $wait = [double]$ra } } catch { }
                if ($wait -gt 1000) { $wait = $wait / 1000 }   # older API versions sent milliseconds
                if ($wait -gt 30) { $wait = 30 }
            } elseif ($code -ge 400 -and $code -lt 500) {
                if ($code -eq 404 -or $code -eq 401) { $last.Error = "Discord says HTTP ${code}: the webhook was deleted or the URL is wrong." }
                break   # other 4xx will not get better by retrying
            } else { $wait = 2 * $attempt }
        }
        if ($attempt -lt $MaxAttempts -and $wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
    }
    return New-Object PSObject -Property $last
}

function New-OpsDiscordPayload([string]$Title, [string]$Description, [ValidateSet('down', 'degraded', 'recovered', 'reminder', 'info', 'stale')][string]$Kind, $Fields = @(), [string]$Mention = '') {
    $colors = @{ down = 0xB3261E; degraded = 0xE8A317; recovered = 0x2E7D32; reminder = 0xB3261E; info = 0x4A6FA5; stale = 0xE8A317 }
    $trim = { param($s, $n) $s = [string]$s; if ($s.Length -gt $n) { return $s.Substring(0, $n - 1) + [char]0x2026 } return $s }
    $f = @()
    foreach ($x in @($Fields)) {
        if ($null -eq $x) { continue }
        $f += [ordered]@{ name = (& $trim $x.name 256); value = (& $trim $x.value 1024); inline = [bool]$x.inline }
    }
    $embed = [ordered]@{
        title = (& $trim $Title 256)
        description = (& $trim $Description 2000)
        color = $colors[$Kind]
        timestamp = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        footer = [ordered]@{ text = 'Realm ops watch' }
        fields = $f
    }
    $payload = [ordered]@{ username = 'Realm Watch'; embeds = @($embed) }
    # Server names come from config, A2S text from the network: no @everyone/@here unless the owner set a mention.
    $mentionOk = $Mention -and ($Mention -match '^(<@&?\d+>\s*)+$')
    if ($mentionOk -and ($Kind -eq 'down' -or $Kind -eq 'reminder')) {
        $payload.content = $Mention
        $payload.allowed_mentions = [ordered]@{ parse = @('roles', 'users') }
    } else {
        $payload.allowed_mentions = [ordered]@{ parse = @() }
    }
    return $payload
}

# ---------------------------------------------------------------- backup: snapshot, zip, verify

$script:OpsReplaceSections = @('Saves', 'oxide/data')                       # whole folders: replaced on restore
$script:OpsOverlaySections = @('oxide/config', 'oxide/plugins', 'Configuration', 'Mods')  # files: overwritten on restore

function Get-OpsBackupFileList([string]$Root, [bool]$IncludeConfig = $true) {
    # Returns @{ Rel = 'Saves/x'; Full = '...'; Length; Ticks } for every file a backup should hold.
    $items = New-Object System.Collections.Generic.List[object]
    $sections = @($script:OpsReplaceSections)
    if ($IncludeConfig) { $sections += $script:OpsOverlaySections }
    $Root = Get-OpsFullPath $Root   # absolute, so FullName below always starts with it
    $prefix = $Root.Length + 1
    foreach ($sec in $sections) {
        $dir = Join-Path $Root $sec
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        foreach ($f in @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force -ErrorAction SilentlyContinue)) {
            $rel = $f.FullName.Substring($prefix).Replace('\', '/')
            if ($sec -eq 'Mods') {
                # Only owner overrides: the game ships *.defaults.cfg and other Mods content, which is game data.
                if ($f.Name -notlike '*.cfg' -or $f.Name -like '*.defaults.cfg') { continue }
            }
            if ($rel -like 'oxide/logs/*' -or $f.Name -like '*.tmp') { continue }
            $items.Add((New-Object PSObject -Property ([ordered]@{ Rel = $rel; Full = $f.FullName; Length = $f.Length; Ticks = $f.LastWriteTimeUtc.Ticks })))
        }
    }
    return $items.ToArray()
}

function Get-OpsInventoryKey($Files) {
    $keys = @($Files | ForEach-Object { '{0}|{1}|{2}' -f $_.Rel, $_.Length, $_.Ticks } | Sort-Object)
    return ($keys -join "`n")
}

function Copy-OpsSharedFile([string]$Source, [string]$Destination) {
    # Copies a file the running game may hold open (FileShare ReadWrite|Delete), keeping its write time.
    $dir = Split-Path -Parent $Destination
    New-OpsDirectory $dir | Out-Null
    $in = New-Object System.IO.FileStream($Source, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
        ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    try {
        $out = New-Object System.IO.FileStream($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $in.CopyTo($out) } finally { $out.Dispose() }
    } finally { $in.Dispose() }
    [System.IO.File]::SetLastWriteTimeUtc($Destination, [System.IO.File]::GetLastWriteTimeUtc($Source))
}

function New-OpsSnapshot([string]$Root, [string]$StagingDir, [bool]$IncludeConfig = $true, [int]$MaxAttempts = 3, [int]$RetryDelaySeconds = 20) {
    # Copies the backup set to StagingDir. If any file changes while copying (the game saved mid-copy), the copy is
    # repeated, up to MaxAttempts. Consistent = the file list, sizes and write times were identical before and after.
    $attempt = 0
    $consistent = $false
    $files = @()
    $lastError = $null
    while ($attempt -lt $MaxAttempts -and -not $consistent) {
        $attempt++
        if (Test-Path -LiteralPath $StagingDir) { Remove-Item -LiteralPath $StagingDir -Recurse -Force }
        New-OpsDirectory $StagingDir | Out-Null
        $before = @(Get-OpsBackupFileList $Root $IncludeConfig)
        $lastError = $null
        foreach ($f in $before) {
            try { Copy-OpsSharedFile $f.Full (Join-Path $StagingDir $f.Rel) }
            catch { $lastError = "$($f.Rel): $($_.Exception.Message)"; break }
        }
        $after = @(Get-OpsBackupFileList $Root $IncludeConfig)
        $files = $before
        if (-not $lastError -and (Get-OpsInventoryKey $before) -eq (Get-OpsInventoryKey $after)) { $consistent = $true }
        elseif ($attempt -lt $MaxAttempts) {
            $why = 'files changed during the copy (the server saved)'
            if ($lastError) { $why = $lastError }
            Write-OpsLog "Snapshot attempt $attempt not clean: $why. Retrying in $RetryDelaySeconds s." 'WARN'
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
    if ($lastError) { throw "Could not copy the save files: $lastError" }
    return New-Object PSObject -Property ([ordered]@{ Files = $files; Consistent = $consistent; Attempts = $attempt })
}

function New-OpsBackupZip([string]$StagingDir, $Files, [string]$ZipPath, $Manifest) {
    # Builds the zip by hand so entry names always use '/' (ZipFile.CreateFromDirectory on .NET 4.5 writes '\').
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $entries = New-Object System.Collections.Generic.List[object]
    $total = [long]0
    foreach ($f in $Files) {
        $staged = Join-Path $StagingDir $f.Rel
        $hash = Get-OpsFileSha256 $staged
        $len = (Get-Item -LiteralPath $staged).Length
        $total += $len
        $entries.Add([ordered]@{ path = $f.Rel; size = $len; sha256 = $hash })
    }
    $Manifest.files = $entries.Count
    $Manifest.bytes = $total
    $Manifest.entries = $entries.ToArray()
    $tmp = $ZipPath + '.partial'
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    New-OpsDirectory (Split-Path -Parent $ZipPath) | Out-Null
    $zip = [System.IO.Compression.ZipFile]::Open($tmp, [System.IO.Compression.ZipArchiveMode]::Create)
    $ok = $false
    try {
        foreach ($f in $Files) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $StagingDir $f.Rel), $f.Rel,
                [System.IO.Compression.CompressionLevel]::Optimal)
        }
        $entry = $zip.CreateEntry('realm-backup.json')
        $w = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
        try { $w.Write((ConvertTo-Json -InputObject $Manifest -Depth 6)) } finally { $w.Dispose() }
        $ok = $true
    } finally {
        $zip.Dispose()
        if (-not $ok -and (Test-Path -LiteralPath $tmp)) { Remove-Item -LiteralPath $tmp -Force }
    }
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    Move-Item -LiteralPath $tmp -Destination $ZipPath
    $sha = Get-OpsFileSha256 $ZipPath
    $leaf = Split-Path -Leaf $ZipPath
    [System.IO.File]::WriteAllText($ZipPath + '.sha256', ('{0}  {1}' -f $sha, $leaf) + "`n", (New-Object System.Text.UTF8Encoding($false)))
    return $sha
}

function Test-OpsZipEntryName([string]$Name) {
    # Pure. True when a zip entry may be extracted: relative, no '..', no drive, and inside a known section.
    $n = $Name.Replace('\', '/')
    if ($n -eq 'realm-backup.json') { return $true }
    if ($n -match '^/|^[A-Za-z]:|(^|/)\.\.(/|$)|[\x00-\x1f]') { return $false }
    foreach ($sec in ($script:OpsReplaceSections + $script:OpsOverlaySections)) {
        if ($n.StartsWith($sec + '/', [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($sec -eq 'Mods' -and ($n -notlike '*.cfg' -or $n -like '*.defaults.cfg')) { return $false }
            return $true
        }
    }
    return $false
}

function Read-OpsBackupManifest([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $e = $zip.GetEntry('realm-backup.json')
        if ($null -eq $e) { throw "$ZipPath has no realm-backup.json: it was not made by the Realm backup scripts." }
        $sr = New-Object System.IO.StreamReader($e.Open())
        try { $m = $sr.ReadToEnd() | ConvertFrom-Json } finally { $sr.Dispose() }
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
    } finally { $zip.Dispose() }
    return New-Object PSObject -Property ([ordered]@{ Manifest = $m; EntryNames = $names })
}

function Test-OpsBackupZip([string]$ZipPath, [string]$ExtractTo = '') {
    # Full check: sidecar hash (when present), safe entry names, manifest present, and (when ExtractTo is given)
    # every file extracted and compared with its SHA-256 from the manifest. Returns a result object; never throws
    # for a bad backup (Ok = $false with Problems), only for missing files.
    $problems = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "Backup not found: $ZipPath" }
    $zipSha = Get-OpsFileSha256 $ZipPath
    $sidecar = $ZipPath + '.sha256'
    $sidecarOk = $null
    if (Test-Path -LiteralPath $sidecar) {
        $want = ([System.IO.File]::ReadAllText($sidecar).Trim() -split '\s+')[0].ToLowerInvariant()
        $sidecarOk = ($want -eq $zipSha)
        if (-not $sidecarOk) { $problems.Add("Zip SHA-256 $zipSha does not match its .sha256 file ($want).") }
    }
    $info = $null
    try { $info = Read-OpsBackupManifest $ZipPath } catch { $problems.Add($_.Exception.Message) }
    $checked = 0
    if ($info) {
        foreach ($n in $info.EntryNames) { if (-not (Test-OpsZipEntryName $n)) { $problems.Add("Unsafe or unexpected entry: $n") } }
        $fmt = [string](Get-OpsProp $info.Manifest 'format' '')
        if ($fmt -ne $script:OpsBackupFormat) { $problems.Add("Manifest format '$fmt' is not $script:OpsBackupFormat.") }
        $listed = @(Get-OpsProp $info.Manifest 'entries' @())
        $inZip = @{}
        foreach ($n in $info.EntryNames) { $inZip[$n] = $true }
        foreach ($e in $listed) { if (-not $inZip.ContainsKey([string]$e.path)) { $problems.Add("Manifest lists $($e.path) but the zip does not hold it.") } }
        if ($ExtractTo -and $problems.Count -eq 0) {
            $dest = Get-OpsFullPath $ExtractTo
            $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
            try {
                foreach ($ze in $zip.Entries) {
                    if ($ze.FullName.EndsWith('/')) { continue }
                    $target = Join-Path $dest $ze.FullName
                    if (-not (Test-OpsPathInside $target $dest)) { $problems.Add("Entry escapes the folder: $($ze.FullName)"); continue }
                    New-OpsDirectory (Split-Path -Parent $target) | Out-Null
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($ze, $target, $true)
                }
            } finally { $zip.Dispose() }
            foreach ($e in $listed) {
                $f = Join-Path $dest ([string]$e.path)
                if (-not (Test-Path -LiteralPath $f)) { $problems.Add("Missing after extract: $($e.path)"); continue }
                $h = Get-OpsFileSha256 $f
                if ($h -ne [string]$e.sha256) { $problems.Add("Hash mismatch: $($e.path)") } else { $checked++ }
            }
        }
    }
    $m = $null
    if ($info) { $m = $info.Manifest }
    return New-Object PSObject -Property ([ordered]@{
        Ok = ($problems.Count -eq 0); ZipSha256 = $zipSha; SidecarOk = $sidecarOk; FilesVerified = $checked
        Manifest = $m; Problems = $problems.ToArray()
    })
}

function Get-OpsLocalPrunePlan([string[]]$Names, [int]$Keep) {
    # Pure. Names are backup zip file names of one server; returns the ones to delete (oldest first), keeping $Keep newest.
    $zips = @($Names | Where-Object { $_ -match '^realm-.+-\d{8}-\d{6}Z\.zip$' } | Sort-Object { ConvertFrom-OpsUtcStamp $_ }, { $_ })
    if ($zips.Count -le $Keep) { return @() }
    return @($zips[0..($zips.Count - $Keep - 1)])
}

function Get-OpsRemotePrunePlan([string[]]$Names, [int]$KeepDays, [int]$MinKeep, [datetime]$NowUtc) {
    # Pure. Deletes zips older than KeepDays (by the UTC stamp in the name) but always keeps the MinKeep newest.
    # Sidecar .sha256 files follow their zip. KeepDays <= 0 disables remote pruning.
    if ($KeepDays -le 0) { return @() }
    $zips = @($Names | Where-Object { $_ -match '^realm-.+-\d{8}-\d{6}Z\.zip$' } | Sort-Object { ConvertFrom-OpsUtcStamp $_ } -Descending)
    $del = New-Object System.Collections.Generic.List[string]
    for ($i = $MinKeep; $i -lt $zips.Count; $i++) {
        $t = ConvertFrom-OpsUtcStamp $zips[$i]
        if ($null -ne $t -and ($NowUtc - $t).TotalDays -gt $KeepDays) {
            $del.Add($zips[$i])
            if ($Names -contains ($zips[$i] + '.sha256')) { $del.Add($zips[$i] + '.sha256') }
        }
    }
    return $del.ToArray()
}

# ---------------------------------------------------------------- rclone

function Invoke-OpsRclone($Config, [string[]]$Arguments, [int]$TimeoutSeconds = 7200) {
    $exe = [string](Get-OpsProp $Config 'backup.rcloneExe')
    $conf = [string](Get-OpsProp $Config 'backup.rcloneConfig')
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "rclone not found at $exe (backup.rcloneExe). Download it from rclone.org and check its SHA256SUMS." }
    if (-not (Test-Path -LiteralPath $conf -PathType Leaf)) { throw "rclone config not found at $conf (backup.rcloneConfig). Create it with: rclone config --config `"$conf`"" }
    $args2 = @($Arguments) + @('--config', $conf, '--retries', '3', '--low-level-retries', '10', '--contimeout', '30s', '--timeout', '5m')
    $extra = @(Get-OpsProp $Config 'backup.rcloneExtraArgs' @())
    if ($extra.Count -gt 0) { $args2 += @($extra | ForEach-Object { [string]$_ }) }
    $r = Invoke-OpsProcess $exe $args2 $TimeoutSeconds
    return $r
}

function Join-OpsRemote([string]$Remote, [string]$Child) {
    $r = $Remote.TrimEnd('/')
    if ($r.EndsWith(':')) { return $r + $Child }
    return $r + '/' + $Child
}

function Get-OpsRemoteListing($Config, [string]$RemoteDir) {
    # Returns file names (not folders) directly in RemoteDir. A missing folder is an empty list.
    $r = Invoke-OpsRclone $Config @('lsjson', $RemoteDir, '--files-only', '--no-mimetype') 600
    if ($r.ExitCode -ne 0) {
        if ($r.Output -match '(?i)directory not found|not found|NoSuchKey') { return @() }
        throw ("rclone lsjson failed (exit {0}): {1}" -f $r.ExitCode, (Protect-OpsText $r.Output))
    }
    if (-not $r.StdOut.Trim()) { return }
    # Assign first: Windows PowerShell 5.1's ConvertFrom-Json emits a JSON array as ONE object down the pipeline.
    $parsed = ConvertFrom-Json -InputObject $r.StdOut
    return @($parsed)
}

function Send-OpsBackupToRemote($Config, [string]$ZipPath, [string]$Slug) {
    # Uploads zip + sidecar to <remote>/<slug>/ and confirms the uploaded size. Returns the remote path.
    $remote = [string](Get-OpsProp $Config 'backup.remote')
    $dir = Join-OpsRemote $remote $Slug
    $leaf = Split-Path -Leaf $ZipPath
    foreach ($f in @($ZipPath, ($ZipPath + '.sha256'))) {
        $r = Invoke-OpsRclone $Config @('copyto', $f, (Join-OpsRemote $dir (Split-Path -Leaf $f)))
        if ($r.ExitCode -ne 0) { throw ("Upload of {0} failed (rclone exit {1}): {2}" -f (Split-Path -Leaf $f), $r.ExitCode, (Protect-OpsText $r.Output)) }
    }
    $listing = @(Get-OpsRemoteListing $Config $dir)
    $hit = @($listing | Where-Object { $_.Name -eq $leaf })
    $local = (Get-Item -LiteralPath $ZipPath).Length
    if ($hit.Count -ne 1) { throw "Uploaded $leaf but it is not listed at $dir." }
    if ([long]$hit[0].Size -ne [long]$local) { throw "Uploaded $leaf has $($hit[0].Size) bytes at the remote, $local locally." }
    if ([bool](Get-OpsProp $Config 'backup.verifyByDownload' $false)) {
        $tmpDir = New-OpsDirectory (Join-Path (Join-Path ([string](Get-OpsProp $Config 'opsRoot')) 'staging') 'verify')
        $tmp = Join-Path $tmpDir $leaf
        $r = Invoke-OpsRclone $Config @('copyto', (Join-OpsRemote $dir $leaf), $tmp)
        if ($r.ExitCode -ne 0) { throw "Download-back of $leaf failed (rclone exit $($r.ExitCode))." }
        $same = (Get-OpsFileSha256 $tmp) -eq (Get-OpsFileSha256 $ZipPath)
        Remove-Item -LiteralPath $tmp -Force
        if (-not $same) { throw "Downloaded copy of $leaf does not match the local zip." }
    }
    return (Join-OpsRemote $dir $leaf)
}
