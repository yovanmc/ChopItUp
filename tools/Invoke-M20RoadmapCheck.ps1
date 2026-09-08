<#
.SYNOPSIS
    M20 live check: proves that the ported roadmap skill, run as a hub run, drives a scratch .NET
    repo's topmost READY row from `/roadmap @<conductor>` to a merged ping, with a Codex row as the
    only plumbing worker.

.DESCRIPTION
    Row 20 (roadmap-in-room), plan Task 6. Spends real model calls on the owner's subscription: the
    conductor (-Conductor, default opus, effort high per AC7 - opus is judge-classed) is re-spawned
    several times, and the worker (-Worker, default gpt-5.4-mini, a Codex row) once for the build
    phase. Never touches C:\Self Apps, %USERPROFILE%\ChopItUp or any real data directory: -DataDir
    and -RoomsRoot default to fresh folders under $env:TEMP and are left behind with the log (the
    scratch repo and data dir are themselves deleted at the end unless -KeepArtifacts is passed).

    Step 1 (the scratch-repo seed: git init, a classlib + xunit project, a whitelist-v3 ROADMAP.md, a
    seed commit, one warm `dotnet test`) lives in Initialize-ScratchRepo so it can be dry-run on its
    own via -SeedOnly, which does nothing else - no hub, no import, no spend - and exits 0 once the
    seed commit and the baseline test are green.

    Plan section "Task 6": the checks below split into HARD-GATING (the hub alone decides these: an
    import exit code, a class assignment, a health payload, a REST list, a room's directory, a run's
    status field, a fixed-form hub note) and MODEL-TRIGGERED (a record that only exists if the
    conductor or worker chose to act on it - M19's task 15d ambiguity: a FAIL is not distinguishable
    from a hub defect without the transcript). Every model-triggered check is wrapped in
    Add-ModelTriggeredCheck, which appends a diagnostic - whether the worker posted at all, and the
    run's live status - on FAIL only; it never affects PASS. The M10 lesson (2026-09-06) applies here
    too: one re-run before a model-triggered FAIL is called a defect is the ORCHESTRATOR's call, not
    this script's - this script always reports what it saw on this one run.

    Every check prints PASS/FAIL; the last line is "Results: n/m PASS", counting every check, hard and
    model-triggered together (plan Task 6, step 9): exit 0 only when every one of them passed. The hub
    is stopped by PID, always, and every orphan whose command line names this run's -DataDir is swept
    in the same `finally`.

    Cost: conductor opus x3-4 at effort high, Codex gpt-5.4-mini x1, plus one re-entry at most;
    ~10-20 minutes. Orchestrator-run only - never invoke `claude` or `codex` directly, and never run
    this against the real ChopItUp repo or a live hub's data directory.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m20check_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8803,
    [int]$TimeoutSeconds = 3600,
    [string]$SkillSource = (Join-Path $HOME '.claude\skills\roadmap'),
    [string]$OverlaySource = (Join-Path $PSScriptRoot 'skills\roadmap-hub'),
    [string]$Conductor = 'opus',
    [string]$Worker = 'gpt-5.4-mini',
    [switch]$KeepArtifacts,
    # Dry-run leg (no hub, no spend): seeds ONLY the scratch repo (step 1) at "$DataDir.repo" and
    # exits 0 once the seed commit exists and the baseline `dotnet test` is green.
    [switch]$SeedOnly
)

$ErrorActionPreference = 'Stop'
# Gate-script convention (docs/LESSONS.md, task 4 overlay scripts): read native exit codes from
# $LASTEXITCODE explicitly rather than letting a nonzero dotnet/git exit throw.
$PSNativeCommandUseErrorActionPreference = $false

# --- Step 1: scratch repo (factored so -SeedOnly can dry-run it alone) -----------------------------
function Initialize-ScratchRepo {
    param([Parameter(Mandatory)][string]$RepoPath)

    New-Item -ItemType Directory -Path $RepoPath | Out-Null
    Push-Location $RepoPath
    try {
        git init -b main | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git init failed (exit $LASTEXITCODE)" }

        dotnet new classlib -n Scratch.Lib -o src/Scratch.Lib | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet new classlib failed (exit $LASTEXITCODE)" }

        dotnet new xunit -n Scratch.Lib.Tests -o tests/Scratch.Lib.Tests | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet new xunit failed (exit $LASTEXITCODE)" }

        dotnet add tests/Scratch.Lib.Tests/Scratch.Lib.Tests.csproj reference src/Scratch.Lib/Scratch.Lib.csproj | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet add reference failed (exit $LASTEXITCODE)" }

        # Task 6 step 1: try --format slnx first; fall back to plain .sln if the SDK rejects it.
        $usedSlnx = $true
        dotnet new sln -n Scratch --format slnx | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $usedSlnx = $false
            dotnet new sln -n Scratch | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "dotnet new sln failed (exit $LASTEXITCODE) even with the .sln fallback" }
        }
        $slnFile = if ($usedSlnx) { 'Scratch.slnx' } else { 'Scratch.sln' }

        dotnet sln $slnFile add src/Scratch.Lib/Scratch.Lib.csproj tests/Scratch.Lib.Tests/Scratch.Lib.Tests.csproj | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet sln add failed (exit $LASTEXITCODE)" }

        Set-Content -LiteralPath 'src/Scratch.Lib/Greeter.cs' -Value @(
            'public static class Greeter',
            '{',
            '    public static string Greet(string name) => "";',
            '}'
        )
        Set-Content -LiteralPath 'tests/Scratch.Lib.Tests/GreeterTests.cs' -Value @(
            'using Xunit;',
            '',
            'public class GreeterTests',
            '{',
            '    [Fact]',
            '    public void Greet_returns_a_string()',
            '    {',
            '        var result = Greeter.Greet("Ada");',
            '        Assert.IsType<string>(result);',
            '    }',
            '}'
        )
        Set-Content -LiteralPath '.gitignore' -Value @('bin/', 'obj/', '.scratch/')

        Set-Content -LiteralPath 'CLAUDE.md' -Value @(
            '# Scratch - M20 roadmap-in-room check',
            '',
            'LOCAL-ONLY repo, thrown away after the check runs.',
            '',
            "Test command: dotnet test $slnFile -c Debug --nologo -v minimal",
            '',
            'No deploy dir.',
            '',
            'Gate: pwsh -NoProfile -File $HOME\.claude\skills\roadmap\preflight\Check-RoadmapBudget.ps1 -RoadmapPath ROADMAP.md -RequireSchema -RepoRoot .'
        )

        # UTF-8 explicitly (not the shell's default) so the legend glyphs below survive on disk exactly
        # as they read in this repo's own ROADMAP.md; the repo.row-flipped check depends on matching a
        # literal checkmark glyph after the run.
        Set-Content -LiteralPath 'ROADMAP.md' -Encoding utf8 -Value @(
            '# Scratch - ROADMAP',
            '<!-- roadmap-schema: whitelist-v3 -->',
            '',
            '## Definition',
            'Scratch repo for the M20 roadmap-in-room check. Repo: LOCAL-ONLY.',
            '',
            '## Milestones',
            '| # | Title | Status | Ready | Plan | Notes |',
            '|---|-------|--------|-------|------|-------|',
            '| 1 | Greeter.Greet returns "Hello, <name>!" | [ ] | READY | -- | LOW. Acceptance: Greet("Ada") == "Hello, Ada!"; a test proves it. |',
            '',
            '**Legend:** ✅ Merged · 📝 Plan ready · 🔬 Researching/Planning · [ ] Not started',
            '',
            '## Pointers',
            '- Conventions: [CLAUDE.md](CLAUDE.md) - Lessons: [docs/LESSONS.md](docs/LESSONS.md)'
        )

        New-Item -ItemType Directory -Path 'docs' -Force | Out-Null
        Set-Content -LiteralPath 'docs/LESSONS.md' -Value '# Lessons'

        git add -A | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git add failed (exit $LASTEXITCODE)" }
        git -c user.name='M20 check' -c user.email='m20check@localhost' commit -m 'seed' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
        $seedHash = (git rev-parse HEAD).Trim()

        # Not itself a check (plan step 1): warms the build cache and proves the seed is green before
        # anything is spent on a run against it.
        dotnet test $slnFile -c Debug --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "baseline 'dotnet test $slnFile' failed (exit $LASTEXITCODE); the seed itself is broken" }

        return [pscustomobject]@{ SeedHash = $seedHash; SlnFile = $slnFile; UsedSlnx = $usedSlnx }
    }
    finally {
        Pop-Location
    }
}

$repoPath = "$DataDir.repo"

if ($SeedOnly) {
    if (Test-Path -LiteralPath $repoPath) {
        Write-Error "'$repoPath' already exists; -SeedOnly only ever runs against a fresh path." -ErrorAction Continue
        exit 2
    }
    try {
        $seed = Initialize-ScratchRepo -RepoPath $repoPath
        Write-Host "seed.done  hash=$($seed.SeedHash)  slnFile=$($seed.SlnFile)  usedSlnx=$($seed.UsedSlnx)  repo=$repoPath"
        exit 0
    }
    catch {
        Write-Error "SeedOnly failed: $($_.Exception.Message)" -ErrorAction Continue
        exit 2
    }
}

if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }   # a sibling: never under the data dir a deny rule protects
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m20-check.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $line = ('{0}  {1}  {2}' -f $status, $Name, $Detail)
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# Plan Task 6 step 9 / M19 task 15d: a model-triggered check reads a hub-written record that only
# exists if the conductor or worker chose to act on it. On FAIL, append what the hub can say about
# whether anything ran at all, so triage does not require reading the transcript.
function Add-ModelTriggeredCheck {
    param([string]$Name, [bool]$Passed, [string]$Detail, [string]$RoomId)
    if (-not $Passed) {
        $workerPosted = @(Get-Messages -RoomId $RoomId | Where-Object authorId -eq $Worker).Count -gt 0
        $runNow = $null
        try { $runNow = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/run" -TimeoutSec 10 } catch { }
        $diag = "ambiguous FAIL (hub-recorded but model-triggered): worker posted at least once=$workerPosted; " `
            + "run.exchanges=$($runNow.exchanges) run.status=$($runNow.status) run.phase=$($runNow.phase)"
        $Detail = if ($Detail) { "$Detail | $diag" } else { $diag }
    }
    Add-Check -Name $Name -Passed $Passed -Detail $Detail
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SkillSource 'SKILL.md') -PathType Leaf)) {
    Write-Error "No SKILL.md at '$SkillSource'. Pass -SkillSource pointing at the canonical roadmap skill folder." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $OverlaySource 'OVERLAY.md') -PathType Leaf)) {
    Write-Error "No OVERLAY.md at '$OverlaySource'. Pass -OverlaySource pointing at tools\skills\roadmap-hub." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot, $repoPath)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
Add-Content -Path $log -Value ("M20 roadmap-in-room check {0} exe={1} data={2} rooms={3} port={4} conductor={5} worker={6}" `
    -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $Port, $Conductor, $Worker)

# --- Step 1: seed the scratch repo, record its HEAD as the pre-run baseline ------------------------
$seed = Initialize-ScratchRepo -RepoPath $repoPath
$seedHash = $seed.SeedHash
Add-Content -Path $log -Value "seed: hash=$seedHash slnFile=$($seed.SlnFile) usedSlnx=$($seed.UsedSlnx) repo=$repoPath"

$base = "http://127.0.0.1:$Port"
$hub = $null

function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $callArgs = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30 }
    if ($null -ne $Body) { $callArgs.ContentType = 'application/json'; $callArgs.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @callArgs
}
function Get-Messages([string]$RoomId, [long]$AfterId = 0, [int]$Limit = 500) {
    try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=$AfterId&limit=$Limit" -TimeoutSec 10).messages) }
    catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
}
# Same M10-lesson polling shape as Invoke-M19RunCheck.ps1: the run's own phaseHistory record (read
# once, after Wait-Run returns) is the proof; SeenPhases is log context only, not a check input.
$script:SeenPhases = New-Object System.Collections.Generic.HashSet[string]
function Wait-Run([string]$RoomId, [string]$Until, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/run" -TimeoutSec 10 }
        catch { Add-Content -Path $log -Value "poll run failed: $($_.Exception.Message)" }
        if ($state) {
            if ($state.phase) { [void]$script:SeenPhases.Add($state.phase) }
            if ($state.status -in ($Until -split ',')) { return $state }
        }
        Start-Sleep -Seconds 3
    }
    return $state
}

# 2026-09-07 lesson: any path passed through -ArgumentList is quoted INSIDE the argument string,
# always - Start-Process joins -ArgumentList with spaces and quotes nothing itself, and this repo's
# own default paths (and -SkillSource) live under 'C:\Agent Projects', which has a space in it.
function Invoke-HubHostCommand {
    param([string[]]$Arguments, [string]$Label)
    $outLog = Join-Path $DataDir "$Label.out.log"
    $errLog = Join-Path $DataDir "$Label.err.log"
    $p = Start-Process -FilePath $HubExe -ArgumentList $Arguments -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    return $p.ExitCode
}

try {
    # --- Step 2: import the canonical skill + overlay, BEFORE the hub starts (same ordering M11/M19
    # use: no second process touches chopitup.db while the hub holds it) -----------------------------
    $importExit = Invoke-HubHostCommand -Label 'import' -Arguments @(
        '--data', "`"$DataDir`"", '--import-skill', "`"$SkillSource`"", '--overlay', "`"$OverlaySource`""
    )
    Add-Check -Name 'import.roadmap-with-overlay' -Passed ($importExit -eq 0) -Detail "exit=$importExit"

    # --- Step 3: classes, hub stopped. opus keeps judge (conducts and is the verify judge); sonnet
    # loses plumbing/visible for this scratch hub only, so build can only go to Codex (plan R7). -----
    $classExitWorker = Invoke-HubHostCommand -Label 'setclasses-worker' -Arguments @('--data', "`"$DataDir`"", '--set-classes', "`"$Worker=plumbing`"")
    $classExitSonnet = Invoke-HubHostCommand -Label 'setclasses-sonnet' -Arguments @('--data', "`"$DataDir`"", '--set-classes', '"sonnet="')
    $classExitOpus = Invoke-HubHostCommand -Label 'setclasses-opus' -Arguments @('--data', "`"$DataDir`"", '--set-classes', '"opus=judge"')
    Add-Check -Name 'classes.set' -Passed ($classExitWorker -eq 0 -and $classExitSonnet -eq 0 -and $classExitOpus -eq 0) `
        -Detail "worker=$classExitWorker sonnet=$classExitSonnet opus=$classExitOpus"

    # --- Step 4: start the hub -----------------------------------------------------------------------
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port", '--rooms-root', "`"$RoomsRoot`"") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $health) {
        Write-Error "Hub never answered /health on pid $($hub.Id); see $(Join-Path $DataDir 'hub.stderr.log')." -ErrorAction Continue
        exit 2
    }
    Add-Check -Name 'health.schema-is-8' -Passed ($health.schema -eq 8) -Detail "schema=$($health.schema)"

    $skills = @(Invoke-RestMethod -Uri "$base/api/skills" -TimeoutSec 10 | ForEach-Object { $_ })   # M10: unwrap the array
    $roadmapSkill = $skills | Where-Object { $_.name -eq 'roadmap' } | Select-Object -First 1
    Add-Check -Name 'skills.api-lists-roadmap-as-run' -Passed ($null -ne $roadmapSkill -and $roadmapSkill.isRun -eq $true) `
        -Detail "found=$($null -ne $roadmapSkill) isRun=$($roadmapSkill.isRun)"

    # --- Step 5: room bound to the scratch repo (must be a repository root; it is, per step 1) ------
    $room = Invoke-Api POST '/api/rooms' @{ name = 'Scratch run'; directory = $repoPath }
    $roomId = $room.id
    Add-Check -Name 'room.bound' -Passed ($null -ne $roomId -and $room.directory -eq $repoPath) -Detail "id=$roomId directory=$($room.directory)"
    Add-Content -Path $log -Value "room: id=$roomId directory=$($room.directory)"

    # --- Step 6: post /roadmap @<conductor>, authored as the owner (ChatApi.PostMessage always stamps
    # participants.OwnerId() - the same surface Invoke-M19RunCheck.ps1 posts through) ----------------
    $invokePosted = Invoke-Api POST "/api/rooms/$roomId/messages" @{ body = "/roadmap @$Conductor" }

    $startedRun = $null
    foreach ($i in 1..20) {
        try { $startedRun = Invoke-RestMethod -Uri "$base/api/rooms/$roomId/run" -TimeoutSec 10 } catch { }
        if ($startedRun -and $startedRun.status -eq 'active') { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'run.started' -Passed ($startedRun.status -eq 'active') -Detail "status=$($startedRun.status) id=$($startedRun.id)"

    $startMessages = Get-Messages -RoomId $roomId -AfterId $invokePosted.id
    $startNote = $startMessages | Where-Object { $_.authorId -eq 'hub' -and $_.body -match '^Run #\d+ started' } | Select-Object -First 1
    Add-Check -Name 'run.start-note' -Passed ($null -ne $startNote) -Detail ($startNote.body ?? 'not seen')

    # --- Step 7: run to a terminal state --------------------------------------------------------------
    $finalRun = Wait-Run -RoomId $roomId -Until 'ended,parked' -Seconds $TimeoutSeconds
    Add-Content -Path $log -Value ("final run: " + ($finalRun | ConvertTo-Json -Compress -Depth 6))
    Add-Content -Path $log -Value ("phases polled (log context only): " + ($script:SeenPhases -join ','))
    if (-not $finalRun -or $finalRun.status -notin @('ended', 'parked')) {
        # Distinct from parked, per plan Task 6 params note: the run is still going at the deadline.
        Write-Host "run still active at timeout (distinct from parked): status=$($finalRun.status)"
        Add-Content -Path $log -Value "run still active at timeout (distinct from parked): status=$($finalRun.status)"
    }

    $recorded = @()
    if ($finalRun.phaseHistory) { $recorded = @($finalRun.phaseHistory.PSObject.Properties.Name) }

    # --- Step 8: hard-gating checks (the hub alone decides these) -------------------------------------
    $participants = @(Invoke-Api GET '/api/participants' | ForEach-Object { $_ })
    $humanIds = @($participants | Where-Object kind -eq 'human' | ForEach-Object id)
    $allMessages = Get-Messages -RoomId $roomId -AfterId 0
    $humanMessages = @($allMessages | Where-Object { $_.authorId -in $humanIds })
    Add-Check -Name 'run.one-human-message' -Passed ($humanMessages.Count -eq 1) `
        -Detail "count=$($humanMessages.Count) authors=$(($humanMessages | ForEach-Object authorId) -join ',')"

    $hubNotes = @($allMessages | Where-Object authorId -eq 'hub')
    Add-Check -Name 'run.no-failure-notes' -Passed (-not ($hubNotes | Where-Object { $_.body -match 'could not be started|parked: skill' })) `
        -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')

    # --- Step 8: model-triggered checks (M19-style, diagnostic on FAIL) -------------------------------
    Add-ModelTriggeredCheck -Name 'run.ended-not-parked' -Passed ($finalRun.status -eq 'ended') `
        -Detail "status=$($finalRun.status) reason=$($finalRun.reason)" -RoomId $roomId

    Add-ModelTriggeredCheck -Name 'run.reason-is-pinged' -Passed ($finalRun.reason -like '*pinged*') `
        -Detail "reason=$($finalRun.reason)" -RoomId $roomId

    # phaseHistory keys are "<kind>/<slug>" or bare "<kind>" (measured on the live run: e.g.
    # "build/greeter-greet", "ping"); match on the kind, the text before the first "/".
    $recordedKinds = @($recorded | ForEach-Object { ($_ -split '/', 2)[0] })
    $sawBuild = $recordedKinds -contains 'build'
    $sawPing = $recordedKinds -contains 'ping'
    Add-ModelTriggeredCheck -Name 'run.phases-include-build-and-ping' -Passed ($sawBuild -and $sawPing) `
        -Detail "phases recorded: $($recorded -join ',')" -RoomId $roomId

    foreach ($gateName in @('start-branch', 'test', 'board-gate', 'finish-branch')) {
        $gateHits = @($finalRun.gateRuns | Where-Object { $_.gate -eq $gateName -and $_.exitCode -eq 0 })
        Add-ModelTriggeredCheck -Name "gate.$gateName-exit-0" -Passed ($gateHits.Count -ge 1) `
            -Detail "gateRuns=$(($finalRun.gateRuns | ForEach-Object { "$($_.gate):$($_.outcome):$($_.exitCode)" }) -join ' | ')" -RoomId $roomId
    }

    $codexPosted = @($allMessages | Where-Object { $_.authorId -eq $Worker })
    Add-ModelTriggeredCheck -Name 'run.codex-worker-posted' -Passed ($codexPosted.Count -gt 0) `
        -Detail "count=$($codexPosted.Count)" -RoomId $roomId

    $mainNow = (git -C $repoPath rev-parse main).Trim()
    Add-ModelTriggeredCheck -Name 'repo.main-advanced' -Passed ($mainNow -ne $seedHash) `
        -Detail "seedHash=$seedHash mainNow=$mainNow" -RoomId $roomId

    $roadmapAfter = Get-Content -LiteralPath (Join-Path $repoPath 'ROADMAP.md') -Raw
    $rowFlipped = $roadmapAfter -match '\|\s*1\s*\|[^|]*\|\s*✅[^|]*\|\s*DONE'
    Add-ModelTriggeredCheck -Name 'repo.row-flipped' -Passed ([bool]$rowFlipped) -Detail "row 1 status/ready cells flipped=$rowFlipped" -RoomId $roomId

    $greeterAfter = Get-Content -LiteralPath (Join-Path $repoPath 'src/Scratch.Lib/Greeter.cs') -Raw
    Add-ModelTriggeredCheck -Name 'repo.greeter-implemented' -Passed ($greeterAfter -like '*Hello, *') -Detail 'looked for the literal "Hello, " in Greeter.cs' -RoomId $roomId
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $orphans = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $orphans) { Write-Host "orphan from this check, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }

    # --- Step 9: results line ------------------------------------------------------------------------
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"

    # Artifacts are deleted ONLY when every check passed and -KeepArtifacts is absent; on any FAIL they
    # are kept regardless of -KeepArtifacts, because the log this script tells the operator to read
    # (hub stdout/stderr, transcripts under $DataDir) lives inside them.
    $allPassed = ($total -gt 0) -and ($passed -eq $total)
    if ($allPassed -and -not $KeepArtifacts) {
        foreach ($d in @($DataDir, $RoomsRoot, $repoPath)) {
            if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    else {
        Write-Host "Artifacts kept: data=$DataDir rooms=$RoomsRoot repo=$repoPath"
    }
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
