#requires -Version 7
# Gate: commit what is pending on the room/* branch, then merge per the repo's flow. With an origin:
# push, PR, gh pr checks --watch, squash merge with branch delete, then pull the default branch.
# Without one: --no-ff merge into the default branch and delete the room branch. Prints
# "finish-branch: merged <hash>" on success. Never force-pushes, never rewrites history. Re-runnable
# after a park: an open PR for the branch is reused, never re-created.
[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false   # native exit codes are read from $LASTEXITCODE; 'Stop' must not throw on them (pwsh 7.6.5 default False, measured 2026-09-07)
function Fail([string]$Message, [int]$Code) { Write-Host "finish-branch: $Message"; exit $Code }
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($LASTEXITCODE -ne 0) { Fail 'not a git repository' 2 }
if ($branch -notmatch '^room/m\d+$') { Fail "HEAD is '$branch', not a room/m<row> branch" 3 }
$dirty = & git status --porcelain
if ($dirty) {
    & git add -A
    & git commit --quiet -m "Room run: $branch board flip"
    if ($LASTEXITCODE -ne 0) { Fail 'commit of pending changes failed' 3 }
}
$hasOrigin = (& git remote) -contains 'origin'
$default = 'main'
if ($hasOrigin) {
    $head = & git symbolic-ref --quiet --short refs/remotes/origin/HEAD
    if ($head) { $default = ($head -replace '^origin/', '') }
} elseif (-not (& git rev-parse --verify --quiet refs/heads/main)) {
    if (& git rev-parse --verify --quiet refs/heads/master) { $default = 'master' } else { Fail 'no main or master branch' 3 }
}
if ($hasOrigin) {
    & gh auth status 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { Fail 'gh is not authenticated in this process' 3 }
    & git ls-remote --quiet --exit-code origin HEAD 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { Fail 'origin is not reachable' 3 }
    & git push --quiet -u origin $branch; if ($LASTEXITCODE -ne 0) { Fail 'push failed' 3 }
    $url = (& gh pr list --head $branch --state open --json url --jq '.[0].url')
    if (-not $url) {
        $url = & gh pr create --base $default --head $branch --title "Room run $branch" --body "Merged by the hub's finish-branch gate from room branch $branch."
        if ($LASTEXITCODE -ne 0) { Fail 'gh pr create failed' 3 }
    }
    $seen = $false
    for ($i = 0; $i -lt 12 -and -not $seen; $i++) {   # up to 2 min for the first check run to register
        $count = (& gh pr checks $branch --json name --jq 'length' 2>$null)
        if ($count -and [int]$count -gt 0) { $seen = $true } else { Start-Sleep -Seconds 10 }
    }
    if (-not $seen) { Fail "no checks registered on $url after 2 min" 4 }
    & gh pr checks $branch --watch; if ($LASTEXITCODE -ne 0) { Fail "checks did not pass on $url" 4 }
    & gh pr merge $branch --squash --delete-branch; if ($LASTEXITCODE -ne 0) { Fail "merge failed on $url" 4 }
    & git checkout --quiet $default; if ($LASTEXITCODE -ne 0) { Fail "checkout $default failed after merge" 3 }
    & git pull --ff-only --quiet; if ($LASTEXITCODE -ne 0) { Fail "pull of $default failed after merge" 3 }
    if (& git rev-parse --verify --quiet "refs/heads/$branch") { & git branch -D --quiet $branch }
} else {
    & git checkout --quiet $default; if ($LASTEXITCODE -ne 0) { Fail "checkout $default failed" 3 }
    & git merge --no-ff --quiet -m "Merge $branch" $branch; if ($LASTEXITCODE -ne 0) { Fail "merge of $branch failed" 4 }
    & git branch -d --quiet $branch
}
$hash = (& git rev-parse --short HEAD).Trim()
Write-Host "finish-branch: merged $hash"
exit 0
