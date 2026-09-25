#requires -Version 7
# Gate: check out room/m<row> for the topmost READY row that is not planned or in progress (📝 or 🔨),
# has no room/m<row> branch yet and is not flipped on another origin branch, where a native session
# keeps its open run. A run ends on its room branch with the row at 🔨 for a native session to merge,
# so the next run first returns to the default branch, reset to origin's tip when there is a remote
# (the hub leaves an empty trail commit on whatever branch a spawn ended on, so the room clone's
# default branch is hub-owned and disposable). A room branch whose row is still unflipped stays when
# it holds work, and is dropped and cut again from the tip when it holds only empty commits. A dirty
# tree = exit 3 (a run never starts on top of someone else's edits).
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured)
$root = (Get-Location).Path
$roadmap = Join-Path $root 'ROADMAP.md'

function Get-Rows([string[]]$Lines) {
    foreach ($line in $Lines) {
        if ($line -notmatch '^\|\s*(\d+)\s*\|') { continue }
        $cells = ($line.Trim() -replace '^\||\|$', '') -split '\|' | ForEach-Object { $_.Trim() }
        if ($cells.Count -ge 4) { [pscustomobject]@{ Row = $cells[0]; Status = $cells[2]; Ready = $cells[3] } }
    }
}

# A file at a git ref, decoded as UTF-8 whatever the console code page, so the 📝 and 🔨 cells survive.
function Read-GitFile([string]$Spec) {
    $psi = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($a in @('show', $Spec)) { $psi.ArgumentList.Add($a) }
    $psi.WorkingDirectory = $root
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $proc = [Diagnostics.Process]::Start($psi)
    $stderr = $proc.StandardError.ReadToEndAsync()
    $text = $proc.StandardOutput.ReadToEnd()
    $proc.WaitForExit()
    $null = $stderr.Result
    if ($proc.ExitCode -ne 0) { return @() }
    $text -split "`r?`n"
}

if (-not (Test-Path -LiteralPath $roadmap -PathType Leaf)) { Write-Host "start-branch: no ROADMAP.md in $root"; exit 2 }
$current = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: not a git repository'; exit 2 }

$dirty = & git status --porcelain
if ($dirty) { Write-Host "start-branch: working tree is dirty on $current; refusing"; $dirty | Write-Host; exit 3 }

$hasOrigin = (& git remote) -contains 'origin'
if ($hasOrigin) {
    & git ls-remote --quiet --exit-code origin HEAD 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: origin is not reachable'; exit 3 }
    $default = (& git symbolic-ref --quiet --short refs/remotes/origin/HEAD); if ($default) { $default = $default -replace '^origin/', '' } else { $default = 'main' }
} else {
    $default = @('main', 'master') | Where-Object { & git rev-parse --verify --quiet "refs/heads/$_" } | Select-Object -First 1
    if (-not $default) { Write-Host 'start-branch: no main or master branch'; exit 2 }
}

$drop = $null
if ($current -match '^room/m(\d+)$') {
    $own = Get-Rows (Get-Content -LiteralPath $roadmap) | Where-Object Row -eq $Matches[1] | Select-Object -First 1
    if ($own -and $own.Ready -eq 'READY' -and $own.Status -notmatch '^(📝|🔨)') {
        # The run parked before its flip. Work on the branch is resumed where it is, but a branch of
        # the hub's empty trail commits alone is cut again, so the row starts from today's tip.
        $fork = & git merge-base HEAD $default
        if ($LASTEXITCODE -eq 0) { & git diff --quiet $fork HEAD }
        if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: already on $current, which holds work"; exit 0 }
        $drop = $current
    }
    & git checkout --quiet $default; if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: checkout of $default failed"; exit 3 }
    if ($drop) {
        & git branch --quiet -D $drop; if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: could not drop $drop"; exit 3 }
        Write-Host "start-branch: dropped $drop, which held no work"
    }
    $current = $default
}
$held = @{}
if ($hasOrigin -and $current -eq $default) {
    & git fetch --quiet --prune origin; if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: git fetch failed'; exit 3 }
    & git checkout --quiet -B $default "origin/$default"; if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: reset of $default to origin failed"; exit 3 }
    foreach ($ref in & git for-each-ref --format='%(refname)' refs/remotes/origin) {
        if ($ref -in @('refs/remotes/origin/HEAD', "refs/remotes/origin/$default")) { continue }
        foreach ($r in Get-Rows (Read-GitFile "${ref}:ROADMAP.md")) {
            if ($r.Status -match '^(📝|🔨)' -and -not $held.ContainsKey($r.Row)) { $held[$r.Row] = $ref -replace '^refs/remotes/', '' }
        }
    }
}

$row = $null
foreach ($r in Get-Rows (Get-Content -LiteralPath $roadmap)) {
    if ($r.Ready -ne 'READY' -or $r.Status -match '^(📝|🔨)') { continue }
    if ($held.ContainsKey($r.Row)) { Write-Host "start-branch: row $($r.Row) is open on $($held[$r.Row]); skipping"; continue }
    & git rev-parse --verify --quiet "refs/heads/room/m$($r.Row)" | Out-Null
    if ($LASTEXITCODE -eq 0) { continue }
    $row = $r.Row; break
}
if (-not $row) { Write-Host 'start-branch: no READY row without a room branch'; exit 2 }
$branch = "room/m$row"
& git checkout --quiet -b $branch
if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: checkout of $branch failed"; exit 3 }
Write-Host "start-branch: on $branch from $current"
exit 0
