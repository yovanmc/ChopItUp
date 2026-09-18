<#
.SYNOPSIS
    Row 37 Task 1: proves row 35's exchange worktrees end to end, with a real Claude spawn and a real
    Codex spawn, against a scratch hub and a scratch directory room.

.DESCRIPTION
    Rows 34/35 shipped worktree exchanges (ExchangeWorktrees.cs) with no live check: the unit suite
    stubs the CLI, so nothing had ever proven that a real `claude`/`codex` process, spawned into a
    linked worktree whose `.git` is a FILE (Row 35 lesson), can create a file there and have the hub
    merge it onto the room's default branch and clean up afterward. This script closes that gap for
    one Claude leg (`@sonnet`) and one Codex leg (`@gpt-5.6-terra`, the model the account's Codex
    config supports - `gpt-5.4-mini` measures a `400 invalid_request_error` from Codex, not a hub or
    worktree defect, see docs/verification.md), run sequentially in the same scratch directory room
    (row 35: the hub closes one worktree per room at a time).

    Each leg posts one owner message mentioning the participant and asking for ONE small file with
    fixed content (M5:100 shape) - no other tool use is requested, so a one-line reply concludes in
    about 10 s (LESSONS M16). It then asserts, with git run directly against the room directory,
    exactly what row 35's seams name:
      - `git worktree list --porcelain` shows the exchange's `x<root>` worktree WHILE the exchange is
        open, and no longer once it has closed;
      - the hub's close note for that exchange root appears in the room (whichever of the three forms
        `CloseAsync` can post: merged-with-hash, nothing-new-to-merge, or kept-with-a-reason);
      - when the note carries a merge hash, `git log --oneline -3` in the room directory contains it;
      - the requested file exists in the room directory afterward (the working tree of whatever branch
        the merge landed on - "the room repo's default branch");
      - `git branch --list chopitup/x<root>` is empty afterward (the merged branch was deleted).
    A FAIL on the file-presence check names only the leg (never a message body) - acceptance criterion
    3: a spawn's file missing from the default branch after the merge note is a real, nameable outcome
    (row 35 left the Codex side of this unverified), not a script defect to chase.

    `-SeedOnly` spends no model call: it starts the hub, binds the room, confirms `/health` reports
    schema 13 and that the hub itself ran `git init` in the room directory (RoomDirectories.cs:36-53),
    then stops. Nothing is posted and no exchange ever opens. `-SkipClaude` / `-SkipCodex` each drop
    one leg (e.g. to re-run only the side that FAILed). Spend: at most one Claude call and one Codex
    call per leg run, never a retry loop inside this script - a FAIL here gets one manual re-run first,
    same as any other live check (LESSONS M10), before it is called a defect.

    Never touches C:\Self Apps or any real data directory: -DataDir, -RoomsRoot and the room directory
    under it all default to fresh folders under $env:TEMP and are left behind with the log unless every
    check passed and -KeepArtifacts is absent. The hub is stopped by PID, always; before that, an open
    exchange is asked to stop over the API first so it kills its own spawned children, and every
    process whose command line names this run's -DataDir is swept in the same `finally` as defense in
    depth (M5's pattern). CI has neither CLI on PATH: this script is agent-run only, never automatic.

.NOTES
    Every check prints PASS/FAIL; the last line is "Results: n/m PASS"; exit 0 only when every check
    passed. `claude auth status` must report `loggedIn: true` before a full run (LESSONS Row 26); this
    script does not check that itself.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_row35check_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [string]$RoomDir = '',
    [int]$Port = 8832,
    [int]$SpawnTimeoutSeconds = 300,
    [string]$ClaudeParticipant = 'sonnet',
    [string]$CodexParticipant = 'gpt-5.6-terra',
    [switch]$SkipClaude,
    [switch]$SkipCodex,
    [switch]$SeedOnly,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
# Gate-script convention (the M20 script carries the same setting): read native exit codes from
# $LASTEXITCODE explicitly rather than letting a nonzero git exit throw, since every git assertion
# below is a native call.
$PSNativeCommandUseErrorActionPreference = $false

if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }       # a sibling: never under the data dir a deny rule protects
if (-not $RoomDir) { $RoomDir = Join-Path $RoomsRoot 'repo' }

$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.row35-check.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $line = ('{0}  {1}  {2}' -f $status, $Name, $Detail)
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot, $RoomDir)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
# RoomDirectories.PrepareAsync requires the room directory's PARENT to already exist when a typed
# (non-blank) directory is posted - it creates the room directory itself and runs `git init` there
# (RoomDirectories.cs:36-53). $RoomDir itself is left absent on purpose.
New-Item -ItemType Directory -Path $RoomsRoot | Out-Null
Add-Content -Path $log -Value ("Row 35 live check {0} exe={1} data={2} rooms={3} room={4} port={5}" `
    -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $RoomDir, $Port)

# Row 28: every non-GET /api route now needs an owner-class bearer -- seed one into this scratch hub's
# own tokens.json before it ever starts (ChopTokenHelpers.ps1). Never a real installation's.
$ownerToken = (Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')).owner

$claude = Get-Command claude -ErrorAction SilentlyContinue
$codex = Get-Command codex -ErrorAction SilentlyContinue
Add-Check -Name 'cli.claude-on-path' -Passed ([bool]$claude) -Detail ($claude.Source ?? 'not found')
Add-Check -Name 'cli.codex-on-path' -Passed ([bool]$codex) -Detail ($codex.Source ?? 'not found')

$base = "http://127.0.0.1:$Port"
$hub = $null
$roomId = $null

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $callArgs = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30; Headers = (New-ChopBearerHeaders -Token $ownerToken) }
    if ($null -ne $Body) { $callArgs.ContentType = 'application/json'; $callArgs.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @callArgs
}
function Read-Room {
    try { @((Invoke-RestMethod -Uri "$base/api/rooms/$roomId/messages?afterId=0&limit=200" -TimeoutSec 10).messages) }
    catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
}
function Wait-Exchange([string]$Until, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$roomId/exchange" -TimeoutSec 10 }
        catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
        if ($state -and $state.status -in ($Until -split ',')) { return $state }
        Start-Sleep -Seconds 2
    }
    return $state
}
# Row 35: `git worktree list --porcelain` lines a plain worktree the hub owns as "worktree <path>";
# match on the leaf folder name ("x<root>") rather than the whole path so backslash/forward-slash
# spelling never matters.
function Test-WorktreeOpen([long]$Root) {
    $leaf = "x$Root"
    $out = & git -C $RoomDir worktree list --porcelain 2>$null
    foreach ($line in $out) {
        if ($line.StartsWith('worktree ')) {
            $path = $line.Substring(9)
            if ((Split-Path -Leaf $path) -ieq $leaf) { return $true }
        }
    }
    return $false
}
# Everything carrying our data dir on its command line except the hub and this script itself (M5's
# pattern): a caller who passed -DataDir has it on pwsh's own command line, excluded by $PID.
function Our-Processes { @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $hub.Id -and $_.ProcessId -ne $PID }) }

# One leg: post the trigger, catch the worktree while open, wait for the exchange to conclude, catch
# the hub's close note and classify it, then assert what's left on disk. Never retries - LESSONS M10's
# "one re-run before calling a FAIL a defect" is the orchestrator's call, not this script's.
function Invoke-ExchangeLeg {
    param([string]$Label, [string]$Participant, [string]$FileName, [string]$Content)

    $body = "@$Participant Create a file named $FileName in this repository containing exactly this one line: $Content Then reply with one short line confirming you created it."
    $posted = Invoke-Api POST "/api/rooms/$roomId/messages" @{ body = $body }
    $root = $posted.id
    Add-Check -Name "post.$Label-message" -Passed ($root -ge 1) -Detail "root=$root"

    # While open (row 35 seam): the worktree is registered under <roomDir>.worktrees for the life of
    # the exchange. Poll for up to 90 s - real spawn latency is measured at ~10 s for a one-line ask
    # (LESSONS M16), so this window is generous, not tight.
    $openDeadline = (Get-Date).AddSeconds(90)
    $sawOpen = $false
    while ((Get-Date) -lt $openDeadline) {
        if (Test-WorktreeOpen -Root $root) { $sawOpen = $true; break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name "worktree.$Label-created" -Passed $sawOpen -Detail "root=$root"

    $state = Wait-Exchange -Until 'concluded,stopped' -Seconds $SpawnTimeoutSeconds
    Add-Check -Name "exchange.$Label-concluded" -Passed ($state.status -in @('concluded', 'stopped')) -Detail "root=$root status=$($state.status)"
    Add-Content -Path $log -Value ("leg $Label exchange: " + ($state | ConvertTo-Json -Compress -Depth 4))

    # The close (merge-or-keep, worktree removal, branch deletion) runs off-thread after the exchange
    # concludes (SpawnerService.cs:329-361), so the note is polled separately, after Wait-Exchange
    # returns - git operations are fast, but this is not on the same tick as the status flip.
    $noteDeadline = (Get-Date).AddSeconds(90)
    $note = $null
    while ((Get-Date) -lt $noteDeadline) {
        $note = @(Read-Room | Where-Object { $_.authorId -eq 'hub' -and $_.body -match "^Exchange #$root\b" }) | Select-Object -Last 1
        if ($note) { break }
        Start-Sleep -Seconds 2
    }
    Add-Check -Name "hubnote.$Label-posted" -Passed ([bool]$note) -Detail "root=$root"

    $hash = $null
    $form = 'none'
    if ($note) {
        if ($note.body -match "^Exchange #$root merged into \S+ as (?<hash>[0-9a-f]+)\.") { $hash = $Matches.hash; $form = 'merged' }
        elseif ($note.body -match "^Exchange #$root had nothing new to merge into \S+\.") { $form = 'nothing-new' }
        elseif ($note.body -match "^Exchange #$root was not merged: ") { $form = 'kept' }
    }
    Add-Content -Path $log -Value "leg $Label close form: $form"

    # Only meaningful when the note actually carried a hash (form=merged): a "nothing new"/"kept" close
    # never advances the default branch, so there is no hash for the log to contain.
    if ($hash) {
        $recent = @(& git -C $RoomDir log --oneline -3 2>$null)
        Add-Check -Name "log.$Label-contains-merge-hash" -Passed (($recent -join "`n") -match [regex]::Escape($hash)) -Detail "root=$root hash=$hash"
    }

    # Acceptance criterion 3: the file's presence on the default branch is asserted whichever close
    # form landed - Detail below names only the leg and the file, never the note or reply body.
    $filePresent = Test-Path -LiteralPath (Join-Path $RoomDir $FileName)
    Add-Check -Name "file.$Label-present-on-default-branch" -Passed $filePresent -Detail "leg=$Label file=$FileName present=$filePresent"

    $stillOpen = Test-WorktreeOpen -Root $root
    Add-Check -Name "worktree.$Label-gone" -Passed (-not $stillOpen) -Detail "root=$root"
    $branchList = (& git -C $RoomDir branch --list "chopitup/x$root" 2>$null) -join ''
    Add-Check -Name "branch.$Label-gone" -Passed ([string]::IsNullOrWhiteSpace($branchList)) -Detail "root=$root"
}

try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port", '--rooms-root', "`"$RoomsRoot`"") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'hub.health-schema' -Passed ($health.schema -eq 13) -Detail "schema=$($health.schema)"

    $room = Invoke-Api POST '/api/rooms' @{ name = 'Row 35 live check'; directory = $RoomDir }
    $roomId = $room.id
    Add-Check -Name 'room.bound' -Passed ($null -ne $roomId -and $room.directory -eq $RoomDir) -Detail "id=$roomId"

    # RoomDirectories.PrepareAsync runs `git init` itself when the directory it is handed is not
    # already a repository (RoomDirectories.cs:52-53); a plain repo's `.git` is a folder (only a
    # worktree's is a file, row 35's first lesson).
    $gitEntry = Test-Path -LiteralPath (Join-Path $RoomDir '.git') -PathType Container
    Add-Check -Name 'repo.initialised-by-hub' -Passed $gitEntry -Detail "path=$RoomDir"

    if (-not $SeedOnly) {
        if (-not $SkipClaude) { Invoke-ExchangeLeg -Label 'claude' -Participant $ClaudeParticipant -FileName 'row35-claude.txt' -Content 'row35-claude-ok' }
        if (-not $SkipCodex) { Invoke-ExchangeLeg -Label 'codex' -Participant $CodexParticipant -FileName 'row35-codex.txt' -Content 'row35-codex-ok' }
    }
}
finally {
    # Stop through the hub first so it kills its own spawned children (M5's pattern); only then the
    # hub by PID, then sweep anything that survived either step.
    if ($roomId) {
        try { Invoke-RestMethod -Uri "$base/api/rooms/$roomId/exchange/stop" -Method Post -Headers (New-ChopBearerHeaders -Token $ownerToken) -TimeoutSec 10 | Out-Null }
        catch { Add-Content -Path $log -Value "final exchange stop failed (best-effort, hub-stop-by-pid follows regardless): $($_.Exception.Message)" }
    }
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $orphans = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $orphans) { Write-Host "orphan from this check, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }

    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"

    # Artifacts are deleted ONLY when every check passed and -KeepArtifacts is absent; on any FAIL (or
    # a -SeedOnly run, where $total can be as low as 4) they are kept, because the log this script
    # names (hub stdout/stderr under $DataDir, the room directory's own git history) is where the
    # failure lives.
    $allPassed = ($total -gt 0) -and ($passed -eq $total)
    if ($allPassed -and -not $KeepArtifacts) {
        foreach ($d in @($DataDir, $RoomsRoot)) {
            if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    else {
        Write-Host "Artifacts kept: data=$DataDir rooms=$RoomsRoot"
    }
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
