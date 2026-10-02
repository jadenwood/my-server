<#
.SYNOPSIS
  Restores a Realm backup made by Backup-RealmOffsite.ps1: as a drill into a spare folder (default), or live into a
  stopped server instance.

.DESCRIPTION
  Source (pick one; default is the newest local zip for the server):
    -ZipPath <file>         a zip on disk
    -FromRemote             the newest zip for the server at backup.remote (rclone), or -RemoteFile <name>
  Every restore first verifies the zip: its .sha256 sidecar, safe entry names, the realm-backup.json manifest, and
  the SHA-256 of every file after extraction. A backup that fails any check is never restored.

  Drill (default): extracts into -DrillDir (default <opsRoot>\drill\<server>-<stamp>) and writes a drill report to
  <opsRoot>\state\drill-<server>.json. The live server is not touched. See ops\restore-drill.md.

  -Live: restores into the server's root folder from the config. Refuses unless the folder is a Realm instance
  (.realm-test-copy marker) and the server is stopped. Then:
    1. takes a local safety backup of the current state (label pre-restore),
    2. moves the current Saves\ and oxide\data\ aside to <server>\_realm-backups\ops-restore-<stamp>\,
    3. copies the backup in. Saves\ and oxide\data\ are replaced whole; oxide\config\, oxide\plugins\,
       Configuration\ and Mods\*.cfg files from the backup overwrite their current versions, which are moved
       aside first. -WorldOnly restores only Saves\ and oxide\data\ (use this when the machine's ports or
       paths differ from the backed-up machine).
  Nothing is deleted.

.EXAMPLE
  .\Restore-RealmBackup.ps1 -ConfigPath G:\RealmOps\realm-ops.json -Server 'Realm 1' -FromRemote
  .\Restore-RealmBackup.ps1 -ConfigPath G:\RealmOps\realm-ops.json -Server 'Realm 1' -ZipPath G:\RealmOps\backups\realm-1\realm-realm-1-20261002-042000Z.zip -Live -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High', DefaultParameterSetName = 'Local')]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(ParameterSetName = 'Zip', Mandatory = $true)][string]$ZipPath,
    [Parameter(ParameterSetName = 'Remote', Mandatory = $true)][switch]$FromRemote,
    [Parameter(ParameterSetName = 'Remote')][string]$RemoteFile = '',
    [switch]$Live,
    [string]$DrillDir = '',
    [switch]$WorldOnly,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpsCommon.ps1')
if ($Yes) { $ConfirmPreference = 'None' }   # -Yes answers every prompt, including ShouldProcess's

$cfg = Read-OpsConfig $ConfigPath
Assert-OpsConfig $cfg 'Restore'
$opsRoot = Assert-OpsSafeDir ([string]$cfg.opsRoot) 'opsRoot'
$s = @(Get-OpsServers $cfg @($Server))[0]
$localDir = Get-OpsFullPath ([string](Get-OpsProp $cfg 'backup.localDir'))
Start-OpsLog $opsRoot 'restore' | Out-Null
$stamp = Get-OpsUtcStamp

# ---- choose the source zip
$zip = $null
$downloaded = $false
if ($PSCmdlet.ParameterSetName -eq 'Zip') {
    $zip = Get-OpsFullPath $ZipPath
} elseif ($PSCmdlet.ParameterSetName -eq 'Remote') {
    $remote = Get-OpsProp $cfg 'backup.remote'
    if (Test-OpsPlaceholder $remote) { throw 'backup.remote is not set in the config.' }
    $dir = Join-OpsRemote ([string]$remote) $s.Slug
    $names = @(Get-OpsRemoteListing $cfg $dir | ForEach-Object { [string]$_.Name } | Where-Object { $_ -match '^realm-.+-\d{8}-\d{6}Z\.zip$' })
    if ($names.Count -eq 0) { throw "No backups for '$($s.Name)' at $dir." }
    $pick = $RemoteFile
    if (-not $pick) { $pick = @($names | Sort-Object { ConvertFrom-OpsUtcStamp $_ } -Descending)[0] }
    elseif ($names -notcontains $pick) { throw "$pick is not at $dir. Available: $($names -join ', ')" }
    $dlDir = New-OpsDirectory (Join-Path (Join-Path $opsRoot 'restore') $s.Slug)
    $zip = Join-Path $dlDir $pick
    Write-OpsLog "Downloading $pick from $dir"
    foreach ($f in @($pick, ($pick + '.sha256'))) {
        $r = Invoke-OpsRclone $cfg @('copyto', (Join-OpsRemote $dir $f), (Join-Path $dlDir $f))
        if ($r.ExitCode -ne 0) {
            if ($f -like '*.sha256') { Write-OpsLog "No sidecar $f at the remote (rclone exit $($r.ExitCode)); the manifest hashes are still checked." 'WARN'; continue }
            throw ("Download failed (rclone exit {0}): {1}" -f $r.ExitCode, (Protect-OpsText $r.Output))
        }
    }
    $downloaded = $true
} else {
    $sdir = Join-Path $localDir $s.Slug
    $names = @(Get-ChildItem -LiteralPath $sdir -File -Filter 'realm-*.zip' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    if ($names.Count -eq 0) { throw "No local backups in $sdir. Use -FromRemote or -ZipPath." }
    $zip = Join-Path $sdir (@($names | Sort-Object { ConvertFrom-OpsUtcStamp $_ } -Descending)[0])
}
if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Backup not found: $zip" }

# ---- verify into a staging folder (always, even for -WhatIf: it changes nothing outside opsRoot)
$verifyDir = Join-Path (Join-Path $opsRoot 'staging') ("restore-{0}-{1}" -f $s.Slug, $stamp)
if (Test-Path -LiteralPath $verifyDir) { Remove-Item -LiteralPath $verifyDir -Recurse -Force }
$check = Test-OpsBackupZip $zip $verifyDir
$m = $check.Manifest
$created = Get-OpsProp $m 'created' ''
$age = $null
if ($created) { $age = [datetime]::UtcNow - ([datetime]$created).ToUniversalTime() }
Write-Host ''
Write-Host "  Backup   : $zip"
Write-Host ("  Made     : {0} on {1} from {2}{3}" -f $created, (Get-OpsProp $m 'machine' '?'), (Get-OpsProp $m 'source' '?'), $(if ($age) { ' (' + (Format-OpsDuration $age) + ' ago)' } else { '' }))
Write-Host ("  Contents : {0} files, {1}; consistent copy: {2}; server was running: {3}" -f (Get-OpsProp $m 'files' 0), (Format-OpsBytes ([double](Get-OpsProp $m 'bytes' 0))), (Get-OpsProp $m 'consistent' '?'), (Get-OpsProp $m 'serverRunning' '?'))
Write-Host ("  Verified : {0} file hashes; zip sidecar {1}" -f $check.FilesVerified, $(if ($null -eq $check.SidecarOk) { 'absent' } elseif ($check.SidecarOk) { 'matches' } else { 'MISMATCH' }))
if (-not $check.Ok) {
    Remove-Item -LiteralPath $verifyDir -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($p in $check.Problems) { Write-OpsLog $p 'ERROR' }
    throw 'This backup failed verification and will not be restored. Try the next older one.'
}
Write-OpsLog 'Backup verified.' 'OK'
if ((Get-OpsProp $m 'slug' '') -and (Get-OpsProp $m 'slug' '') -ne $s.Slug) {
    Write-OpsLog ("This backup was made for server '{0}', not '{1}'." -f (Get-OpsProp $m 'server' ''), $s.Name) 'WARN'
}

$reportPath = Join-Path (Join-Path $opsRoot 'state') ('drill-{0}.json' -f $s.Slug)
try {
    if (-not $Live) {
        # ---- drill
        $target = $DrillDir
        if (-not $target) { $target = Join-Path (Join-Path $opsRoot 'drill') ("{0}-{1}" -f $s.Slug, $stamp) }
        $target = Assert-OpsSafeDir $target 'drill folder'
        foreach ($srv in @(Get-OpsServers $cfg)) {
            if ($srv.Root -and -not (Test-OpsPlaceholder $srv.Root) -and ((Test-OpsPathInside $target $srv.Root) -or (Test-OpsPathInside $srv.Root $target))) {
                throw "The drill folder must not overlap the live server '$($srv.Name)' ($($srv.Root)). Use -Live for a real restore."
            }
        }
        if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) { throw "Drill folder $target is not empty." }
        $ConfirmPreference = 'None'   # a drill only writes into the empty drill folder: no prompt (-WhatIf still applies)
        if (-not $PSCmdlet.ShouldProcess($target, 'Extract verified backup for a restore drill')) { return }
        New-OpsDirectory (Split-Path -Parent $target) | Out-Null
        Move-Item -LiteralPath $verifyDir -Destination $target
        $saves = @(Get-ChildItem -LiteralPath (Join-Path $target 'Saves') -Recurse -File -ErrorAction SilentlyContinue).Count
        $data = @(Get-ChildItem -LiteralPath (Join-Path (Join-Path $target 'oxide') 'data') -File -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        Write-OpsJsonFile $reportPath ([ordered]@{
            at = (Get-OpsIsoNow); server = $s.Name; zip = $zip; downloadedFromRemote = $downloaded; backupCreated = $created
            filesVerified = $check.FilesVerified; savesFiles = $saves; oxideDataFiles = $data; drillDir = $target; ok = $true
        })
        Write-OpsLog ("Drill extract OK: {0} ({1} save files; oxide data: {2})" -f $target, $saves, ($data -join ', ')) 'OK'
        Write-Host 'Continue with ops\restore-drill.md (boot the drill copy on spare ports, or inspect the files).' -ForegroundColor Cyan
        return
    }

    # ---- live restore
    $root = Get-OpsFullPath $s.Root
    if (-not (Test-Path -LiteralPath (Join-Path $root $script:OpsInstanceMarker))) {
        throw "Refusing: $root has no $script:OpsInstanceMarker marker. Create the instance with Realm Steward (or server\New-TestServer.ps1) first, then restore into it."
    }
    $running = @(Get-OpsServerProcesses $root)
    if ($running.Count -gt 0) { throw "Server '$($s.Name)' is running. Stop it in Realm Steward first." }
    $sections = @($script:OpsReplaceSections)
    if (-not $WorldOnly) { $sections += $script:OpsOverlaySections }
    $aside = Join-Path (Join-Path $root '_realm-backups') ('ops-restore-' + $stamp)
    Write-Host "  Restore to: $root ($($sections -join ', '))"
    Write-Host "  Current state moved aside to: $aside"
    if (-not $PSCmdlet.ShouldProcess($root, "Restore $([IO.Path]::GetFileName($zip))")) { return }
    if (-not $Yes -and -not $PSCmdlet.ShouldContinue("Replace the world of '$($s.Name)' with this backup from $created?", 'Realm')) { return }

    $lock = Enter-OpsLock $opsRoot 'restore'
    try {
        & (Join-Path $PSScriptRoot 'Backup-RealmOffsite.ps1') -ConfigPath $ConfigPath -Server $s.Name -LocalOnly -NoPrune -NoLock -Label 'pre-restore' -Confirm:$false
        if ($LASTEXITCODE -ne 0) {
            $hasData = (Test-Path -LiteralPath (Join-Path $root 'Saves')) -or (Test-Path -LiteralPath (Join-Path (Join-Path $root 'oxide') 'data'))
            if ($hasData) { throw 'The pre-restore safety backup failed, so nothing was changed.' }
            Write-OpsLog 'No current world to back up (fresh instance).' 'WARN'
        }
        $moved = 0; $copied = 0
        foreach ($sec in $script:OpsReplaceSections) {
            $cur = Join-Path $root $sec
            if (Test-Path -LiteralPath $cur) {
                $dest = Join-Path $aside $sec
                New-OpsDirectory (Split-Path -Parent $dest) | Out-Null
                Move-Item -LiteralPath $cur -Destination $dest
                $moved++
            }
        }
        foreach ($e in @(Get-OpsProp $m 'entries' @())) {
            $rel = [string]$e.path
            $sec = $null
            foreach ($x in $sections) { if ($rel.StartsWith($x + '/', [System.StringComparison]::OrdinalIgnoreCase)) { $sec = $x; break } }
            if (-not $sec) { continue }
            $src = Join-Path $verifyDir $rel
            $dst = Join-Path $root $rel
            if (-not (Test-OpsPathInside $dst $root)) { throw "Unsafe path $rel" }
            if ($script:OpsOverlaySections -contains $sec -and (Test-Path -LiteralPath $dst)) {
                $keep = Join-Path $aside $rel
                New-OpsDirectory (Split-Path -Parent $keep) | Out-Null
                Move-Item -LiteralPath $dst -Destination $keep
                $moved++
            }
            New-OpsDirectory (Split-Path -Parent $dst) | Out-Null
            Copy-Item -LiteralPath $src -Destination $dst
            $copied++
        }
        Write-OpsJsonFile (Join-Path (Join-Path $opsRoot 'state') ('restore-{0}.json' -f $s.Slug)) ([ordered]@{
            at = (Get-OpsIsoNow); server = $s.Name; zip = $zip; backupCreated = $created; filesRestored = $copied; movedAside = $aside; sections = $sections
        })
        Write-OpsLog ("Restored {0} files into {1}. Previous state: {2}" -f $copied, $root, $aside) 'OK'
        Write-Host 'Next: start the server in Realm Steward, wait for the ready line, log in and check your base. See ops\disaster-recovery.md step 6.' -ForegroundColor Cyan
    } finally { Exit-OpsLock $lock }
} finally {
    if (Test-Path -LiteralPath $verifyDir) { Remove-Item -LiteralPath $verifyDir -Recurse -Force -ErrorAction SilentlyContinue }
}
