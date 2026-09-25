#requires -Version 7
# Gate: check out room/m<row> for the topmost READY row that is not planned or in progress (📝 or 🔨)
# and has no room/m<row> branch yet. A run ends on its room branch with the row at 🔨 for a native
# session to merge, so the next run first returns to the default branch, reset to origin's tip when
# there is a remote (the hub leaves an empty trail commit on whatever branch a spawn ended on, so the
# room clone's default branch is hub-owned and disposable). Rerunning on a room branch whose row is
# still unflipped stays there, exit 0. A dirty tree = exit 3 (a run never starts on top of someone
# else's edits).
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured)
$root = (Get-Location).Path
$roadmap = Join-Path $root 'ROADMAP.md'

function Get-Rows {
    if (-not (Test-Path -LiteralPath $roadmap -PathType Leaf)) { return @() }
    foreach ($line in Get-Content -LiteralPath $roadmap) {
        if ($line -notmatch '^\|\s*(\d+)\s*\|') { continue }
        $cells = ($line.Trim() -replace '^\||\|$', '') -split '\|' | ForEach-Object { $_.Trim() }
        if ($cells.Count -ge 4) { [pscustomobject]@{ Row = $cells[0]; Status = $cells[2]; Ready = $cells[3] } }
    }
}

if (-not (Test-Path -LiteralPath $roadmap -PathType Leaf)) { Write-Host "start-branch: no ROADMAP.md in $root"; exit 2 }
$current = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: not a git repository'; exit 2 }

if ($current -match '^room/m(\d+)$') {
    $own = Get-Rows | Where-Object Row -eq $Matches[1] | Select-Object -First 1
    if ($own -and $own.Ready -eq 'READY' -and $own.Status -notmatch '^(📝|🔨)') { Write-Host "start-branch: already on $current"; exit 0 }
}

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

if ($current -match '^room/m') {
    & git checkout --quiet $default; if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: checkout of $default failed"; exit 3 }
    $current = $default
}
if ($hasOrigin -and $current -eq $default) {
    & git fetch --quiet origin; if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: git fetch failed'; exit 3 }
    & git checkout --quiet -B $default "origin/$default"; if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: reset of $default to origin failed"; exit 3 }
}

$row = $null
foreach ($r in Get-Rows) {
    if ($r.Ready -ne 'READY' -or $r.Status -match '^(📝|🔨)') { continue }
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
