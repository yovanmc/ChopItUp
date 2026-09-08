#requires -Version 7
# Gate: check out room/m<row> for the topmost READY row of ROADMAP.md. Idempotent: already on that
# branch = exit 0. A dirty tree = exit 3 (a run never starts on top of someone else's edits).
# With a remote, the default branch is RESET to origin's tip (the hub leaves an empty trail commit on
# whatever branch a spawn ended on, so the room clone's default branch is hub-owned and disposable),
# and gh auth + remote reachability are checked here so an auth defect parks at spawn 1 with nothing created.
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured 2026-09-07)
$root = (Get-Location).Path
$roadmap = Join-Path $root 'ROADMAP.md'
if (-not (Test-Path -LiteralPath $roadmap -PathType Leaf)) { Write-Host "start-branch: no ROADMAP.md in $root"; exit 2 }
$row = $null
foreach ($line in Get-Content -LiteralPath $roadmap) {
    if ($line -notmatch '^\|\s*(\d+)\s*\|') { continue }
    $cells = ($line.Trim() -replace '^\||\|$', '') -split '\|' | ForEach-Object { $_.Trim() }
    if ($cells.Count -ge 4 -and $cells[3] -eq 'READY') { $row = $cells[0]; break }
}
if (-not $row) { Write-Host 'start-branch: no READY row'; exit 2 }
$branch = "room/m$row"
$current = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: not a git repository'; exit 2 }
if ($current -eq $branch) { Write-Host "start-branch: already on $branch"; exit 0 }
$dirty = & git status --porcelain
if ($dirty) { Write-Host "start-branch: working tree is dirty on $current; refusing"; $dirty | Write-Host; exit 3 }
$hasOrigin = (& git remote) -contains 'origin'
if ($hasOrigin) {
    & gh auth status 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: gh is not authenticated in this process'; exit 3 }
    & git ls-remote --quiet --exit-code origin HEAD 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: origin is not reachable'; exit 3 }
    $default = (& git symbolic-ref --quiet --short refs/remotes/origin/HEAD); if ($default) { $default = $default -replace '^origin/', '' } else { $default = 'main' }
    if ($current -eq $default) {
        & git fetch --quiet origin; if ($LASTEXITCODE -ne 0) { Write-Host 'start-branch: git fetch failed'; exit 3 }
        & git checkout --quiet -B $default "origin/$default"; if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: reset of $default to origin failed"; exit 3 }
    }
}
$exists = & git rev-parse --verify --quiet "refs/heads/$branch"
if ($exists) { & git checkout --quiet $branch } else { & git checkout --quiet -b $branch }
if ($LASTEXITCODE -ne 0) { Write-Host "start-branch: checkout of $branch failed"; exit 3 }
Write-Host "start-branch: on $branch from $current"
exit 0
