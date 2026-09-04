<#
.SYNOPSIS
    Deploys the published ChopItUp.Hub release into a target directory without ever touching the
    data directory that sits beside it.

.DESCRIPTION
    See docs/superpowers/plans/m4-release.md, "Task 2 -- tools/Deploy-ChopItUp.ps1". This script's
    whole job is to be unable to destroy the thing it is standing next to: the install folder holds
    the program (replaced on every deploy) and `data\` (the owner's only copy of their room history,
    which must survive every deploy forever).

    Order of operations, in the order below -- the ordering IS the safety property:
      1. Refuse to run if any process's image path is inside -TargetDir. Aborts before anything is
         touched, naming the PID and path.
      2. Publish into -StagingDir (never straight into the target), unless -SkipPublish.
      3. Sanity-check the staging output (exe present and >= 30 MB, wwwroot\index.html present,
         wwwroot\assets\ non-empty) before it is allowed near the target.
      4. Copy the existing install aside (excluding `data` and `logs`) to a directory that is a
         SIBLING of -TargetDir, derived from -TargetDir itself -- never a hardcoded path, so driving
         this script at a scratch directory in tests never touches the owner's real install.
      5. Re-run the process check, then copy into the target additively (/E, excluding data/logs and
         the exe itself), and replace the exe last via copy-aside-and-rename so a kill mid-deploy
         never leaves a half-written executable under the name the owner double-clicks.
      6. Print a machine-readable "DEPLOY_RESULT: { ... }" JSON line as the last line of output:
         target, staging, whether/where a backup was written, the deployed exe's size, the current
         backup count, and how many running processes' image paths could not be read (a blind spot
         that must be reported, never silent). This step never verifies file contents -- that is
         tools/Invoke-M4SelfCheck.ps1's job, run separately against the staging path this script
         prints.

    -RestoreFrom runs the same pipeline (process guard, sanity check, backup-aside, guarded additive
    copy, atomic exe rename) with the restore source in place of a fresh publish, so rolling back is
    a mode of this script rather than a README paragraph telling someone to hand-copy files.

    /MIR is never used. Robocopy exit codes 0-7 are success; only >= 8 is treated as failure.

.PARAMETER TargetDir
    Where the install lives. Defaults to the real release location, so pass a scratch directory to
    exercise this script harmlessly.

.PARAMETER StagingDir
    Where to publish (or, with -SkipPublish, where to read a pre-staged publish from). Defaults to a
    fresh GUID-named directory under $env:TEMP. Always printed in the final report line, since
    Invoke-M4SelfCheck.ps1 needs it and a path this script invents and keeps to itself is unusable.

.PARAMETER SkipPublish
    Use the contents of -StagingDir as-is instead of running `dotnet publish`. Requires an explicit
    -StagingDir (there is nothing sensible to "skip publishing" into a directory this script just
    invented). This is the seam automated tests use to avoid paying for a Release publish per test.

.PARAMETER RestoreFrom
    Path to a previous backup directory (as written by a prior deploy). Copies it back over
    -TargetDir under the same guards as a normal deploy. Mutually exclusive with -StagingDir and
    -SkipPublish -- restoring never publishes.
#>
[CmdletBinding()]
param(
    [string]$TargetDir = 'C:\Self Apps\ChopItUp',
    [string]$StagingDir,
    [switch]$SkipPublish,
    [string]$RestoreFrom
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hubProj = Join-Path $repoRoot 'src\ChopItUp.Hub\ChopItUp.Hub.csproj'

# --- Argument validation (cheap, and does not touch anything, so it runs before step 1 too) -------
if ($SkipPublish -and -not $StagingDir) {
    Write-Error "-SkipPublish requires an explicit -StagingDir (nothing to skip publishing into otherwise)."
    exit 1
}
if ($PSBoundParameters.ContainsKey('RestoreFrom') -and [string]::IsNullOrWhiteSpace($RestoreFrom)) {
    # An empty/whitespace value is falsy in PowerShell, which would otherwise fall through to a
    # normal fresh-publish deploy -- silently doing the opposite of what an empty -RestoreFrom
    # probably meant. Fail loudly instead.
    Write-Error "-RestoreFrom was supplied with an empty value."
    exit 1
}
if ($RestoreFrom -and ($StagingDir -or $SkipPublish)) {
    Write-Error "-RestoreFrom cannot be combined with -StagingDir or -SkipPublish; restoring never publishes."
    exit 1
}
if ($RestoreFrom -and -not (Test-Path -LiteralPath $RestoreFrom -PathType Container)) {
    Write-Error "Restore source '$RestoreFrom' does not exist or is not a directory."
    exit 1
}

$targetDir = $TargetDir.TrimEnd('\')
$isRestore = [bool]$RestoreFrom

function Test-NoProcessRunningFromTarget {
    <# Returns @{ Hits = @(@{Id=;Path=}); UnreadableCount = <int> }. Matches on a trimmed target
       path plus a trailing backslash, so a bare prefix match on "C:\Self Apps\ChopItUp" does not
       also match "C:\Self Apps\ChopItUp.backup-2026...\ChopItUp.Hub.exe". Get-Process returns a
       null (or throwing) Path for processes this session cannot inspect -- those are logged as a
       count, never silently ignored, per the plan's "a blind spot that is reported is a caveat; one
       that is silent is a lie". #>
    param([Parameter(Mandatory)][string]$NormalizedTargetDir)

    $prefix = ($NormalizedTargetDir + '\').ToLowerInvariant()
    $hits = New-Object System.Collections.Generic.List[object]
    $unreadable = 0

    foreach ($proc in (Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $proc.Path } catch { $path = $null }
        if ([string]::IsNullOrEmpty($path)) { $unreadable++; continue }
        if ($path.ToLowerInvariant().StartsWith($prefix)) {
            $hits.Add([PSCustomObject]@{ Id = $proc.Id; Path = $path })
        }
    }

    return [PSCustomObject]@{ Hits = $hits; UnreadableCount = $unreadable }
}

function Assert-ProcessGuardClear {
    param([Parameter(Mandatory)][string]$NormalizedTargetDir, [Parameter(Mandatory)][string]$WhenLabel)

    $guard = Test-NoProcessRunningFromTarget -NormalizedTargetDir $NormalizedTargetDir
    if ($guard.Hits.Count -gt 0) {
        $named = ($guard.Hits | ForEach-Object { "PID $($_.Id) ('$($_.Path)')" }) -join '; '
        throw "Refusing to deploy ($WhenLabel): process(es) running from inside target '$NormalizedTargetDir': $named. Nothing has been changed. This script never stops a running process -- stop it yourself and re-run. ($($guard.UnreadableCount) other process path(s) could not be read and could not be checked.)"
    }
    Write-Host "Process guard clear ($WhenLabel). ($($guard.UnreadableCount) process path(s) could not be read and could not be checked.)"
    return $guard.UnreadableCount
}

function Test-StagingOutput {
    <# Returns $null if the staging/restore source is plausible, else a string describing why not.
       The 30 MB floor and the wwwroot\assets\ check both exist for the same reason: a build that
       looks fine but is missing the runtime (a non-self-contained build of this project is
       162,304 bytes) or missing the client bundle (index.html alone is served for every unreserved
       path by SpaFiles' MapFallback, so its presence proves nothing about assets\) must never reach
       the target. #>
    param([Parameter(Mandatory)][string]$Dir)

    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) {
        return "staging/restore directory '$Dir' does not exist."
    }
    $exePath = Join-Path $Dir 'ChopItUp.Hub.exe'
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        return "'$exePath' does not exist."
    }
    $exeSize = (Get-Item -LiteralPath $exePath).Length
    $floorBytes = 30MB
    if ($exeSize -lt $floorBytes) {
        return "'$exePath' is $exeSize bytes, below the $floorBytes-byte floor that separates a self-contained build from a stub."
    }
    $indexPath = Join-Path $Dir 'wwwroot\index.html'
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
        return "'$indexPath' does not exist."
    }
    $assetsDir = Join-Path $Dir 'wwwroot\assets'
    $assetFiles = @()
    if (Test-Path -LiteralPath $assetsDir -PathType Container) {
        $assetFiles = @(Get-ChildItem -LiteralPath $assetsDir -File -Recurse -ErrorAction SilentlyContinue)
    }
    if ($assetFiles.Count -eq 0) {
        return "'$assetsDir' has no files; index.html alone is served for every unreserved path, so this would ship a blank-page client."
    }
    return $null
}

function New-BackupDirPath {
    <# Sibling of -TargetDir, derived from the parameter -- never a hardcoded path, so tests driving
       -TargetDir at a scratch directory never write into the owner's real C:\Self Apps. #>
    param([Parameter(Mandatory)][string]$NormalizedTargetDir)

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $base = "$NormalizedTargetDir.backup-$stamp"
    $candidate = $base
    $suffix = 1
    while (Test-Path -LiteralPath $candidate) {
        $candidate = "$base-$suffix"
        $suffix++
    }
    return $candidate
}

function Invoke-BackupAside {
    <# Copies (never moves) the existing install aside, excluding data\ and logs\. No-op, reported,
       if there is nothing to back up (a first deploy). #>
    param([Parameter(Mandatory)][string]$NormalizedTargetDir)

    if (-not (Test-Path -LiteralPath $NormalizedTargetDir -PathType Container)) {
        Write-Host "No existing install at '$NormalizedTargetDir'; nothing to back up."
        return $null
    }
    $backupDir = New-BackupDirPath -NormalizedTargetDir $NormalizedTargetDir
    Write-Host "Backing up existing install to '$backupDir'..."
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    robocopy $NormalizedTargetDir $backupDir /E /XD data logs /R:2 /W:2 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy backup failed with exit code $LASTEXITCODE (source '$NormalizedTargetDir' -> '$backupDir')." }
    return $backupDir
}

function Invoke-GuardedCopyIn {
    <# Additive copy of $Source into $NormalizedTargetDir, excluding data\, logs\ and the exe (which
       is copied aside and renamed into place last so a kill mid-copy never leaves a half-written
       executable under the real name). /E is load-bearing: robocopy's default is top-level files
       only, and /XD data logs alone would leave wwwroot\ behind entirely -- the app would still
       start and /health would still be green while the owner got a blank page. #>
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$NormalizedTargetDir)

    robocopy $Source $NormalizedTargetDir /E /XD data logs /XF ChopItUp.Hub.exe /R:2 /W:2 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE (source '$Source' -> '$NormalizedTargetDir')." }

    $sourceExe = Join-Path $Source 'ChopItUp.Hub.exe'
    $stagedExe = Join-Path $NormalizedTargetDir 'ChopItUp.Hub.exe.new'
    $finalExe = Join-Path $NormalizedTargetDir 'ChopItUp.Hub.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $stagedExe -Force
    Move-Item -LiteralPath $stagedExe -Destination $finalExe -Force
}

try {
    # --- Step 1: refuse to run over a live install, before touching anything -----------------------
    $unreadable1 = Assert-ProcessGuardClear -NormalizedTargetDir $targetDir -WhenLabel 'before publish/restore'

    # --- Step 2: publish into staging (or point at the restore source), unless told to skip --------
    if ($isRestore) {
        $source = $RestoreFrom
        Write-Host "Restore mode: using '$source' in place of a fresh publish."
    }
    else {
        $source = if ($StagingDir) { $StagingDir } else { Join-Path $env:TEMP ([guid]::NewGuid().ToString('N')) }
        if (-not $SkipPublish) {
            New-Item -ItemType Directory -Path $source -Force | Out-Null
            Write-Host "Publishing $hubProj (Release) to '$source'..."
            & dotnet publish $hubProj -c Release -o $source
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
        }
        else {
            Write-Host "Skipping publish; using existing staging output at '$source'."
        }
    }

    # --- Step 3: sanity-check the source before it is allowed near the target -----------------------
    $sanityError = Test-StagingOutput -Dir $source
    if ($sanityError) { throw "Staging/restore output failed its sanity check: $sanityError Target untouched." }
    Write-Host "Sanity check passed for '$source'."

    # --- Step 4: back up the existing install aside (copy, never move) ------------------------------
    $backupDir = Invoke-BackupAside -NormalizedTargetDir $targetDir

    # --- Step 5: re-run the process guard immediately before writing, then copy in additively -------
    $unreadable2 = Assert-ProcessGuardClear -NormalizedTargetDir $targetDir -WhenLabel 'immediately before copy'
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }
    Invoke-GuardedCopyIn -Source $source -NormalizedTargetDir $targetDir

    # --- Step 6: machine-readable report, as the last line of output --------------------------------
    $finalExePath = Join-Path $targetDir 'ChopItUp.Hub.exe'
    $exeSize = (Get-Item -LiteralPath $finalExePath).Length
    $parent = Split-Path -Parent $targetDir
    $leaf = Split-Path -Leaf $targetDir
    $backupCount = 0
    if (Test-Path -LiteralPath $parent) {
        $backupCount = @(Get-ChildItem -LiteralPath $parent -Directory -Filter "$leaf.backup-*" -ErrorAction SilentlyContinue).Count
    }

    $result = [ordered]@{
        target                  = $targetDir
        staging                 = $source
        restore                 = $isRestore
        backup_made             = [bool]$backupDir
        backup_dir              = $backupDir
        exe_size_bytes          = $exeSize
        backup_count            = $backupCount
        unreadable_process_paths = $unreadable2
    }
    $json = $result | ConvertTo-Json -Compress
    Write-Host "DEPLOY_RESULT: $json"
    exit 0
}
catch {
    [Console]::Error.WriteLine("DEPLOY_FAILED: $($_.Exception.Message)")
    exit 1
}
