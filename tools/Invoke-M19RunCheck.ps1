<#
.SYNOPSIS
    M19 live check: proves that a run — the hub's conductor loop from row 19 — gets a two-phase toy
    skill from start to its ping with no owner post between the phases, using real command-line
    models against a scratch hub.

.DESCRIPTION
    Row 19 (runs), plan Task 15. Spends real Claude calls on the owner's subscription: the conductor
    (-Conductor, default opus, re-spawned at least twice) and the worker (-Worker, default sonnet,
    once). Effort is passed high for both, per AC7 (conductor and judge classes) — opus is
    visible,judge so it gets --effort high as conductor regardless; sonnet does not, being plumbing
    only. Never touches C:\Self Apps, %USERPROFILE%\ChopItUp or any real data directory: -DataDir
    and -RoomsRoot default to fresh folders under $env:TEMP and are left behind with the log. The
    toy skill imported is -SkillSource (default: this repo's own tools\skills\toy-run) — a
    throwaway, not something anyone would slash-invoke for real work.

    Task 15d: run.two-phases, run.file-created, run.artifact-author and gate.ran-in-run are asserted
    on records the HUB wrote, but each record only exists if the conductor or worker chose to act on
    it — a FAIL on any of the four is ambiguous between a hub defect and a model that did not comply
    with the toy skill's instructions. Distinguishing the two needs the transcript. The one thing
    this script CAN do without reading it is say whether the worker exchange (the build phase's
    @sonnet mention) opened at all and whether the run reached a terminal state — which at least
    tells the orchestrator "the hub drove some spawns" versus "nothing happened after the first
    post". Every one of those four checks prints that diagnostic on FAIL; it never affects PASS.

    Every check prints PASS/FAIL; the last line is "Results: n/m PASS". The hub is stopped by PID,
    always, and every orphan whose command line names this run's -DataDir is swept in the same
    `finally`.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m19check_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8801,
    [int]$TimeoutSeconds = 900,
    [string]$SkillSource = (Join-Path $PSScriptRoot 'skills\toy-run'),
    [string]$Conductor = 'opus',
    [string]$Worker = 'sonnet',
    [string]$Gate = 'count-files'
)

$ErrorActionPreference = 'Stop'
if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }   # a sibling: never under the data dir a deny rule protects
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m19-check.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $line = ('{0}  {1}  {2}' -f $status, $Name, $Detail)
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# Task 15d: the four checks below read hub-written records that only exist if a model complied.
# On FAIL, append what the hub itself can say about whether anything ran at all, so a FAIL here does
# not require reading the transcript to triage as "hub bug" vs "conductor/worker did not comply".
function Add-ModelTriggeredCheck {
    param([string]$Name, [bool]$Passed, [string]$Detail, [string]$RoomId)
    if (-not $Passed) {
        $workerPosted = @(Get-Messages -RoomId $RoomId | Where-Object authorId -eq $Worker).Count -gt 0
        $runNow = $null
        try { $runNow = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/run" -TimeoutSec 10 } catch { }
        $diag = "ambiguous FAIL (hub-recorded but model-triggered, task 15d): worker posted at least once=$workerPosted; " `
            + "run.exchanges=$($runNow.exchanges) run.status=$($runNow.status) run.phase=$($runNow.phase)"
        $Detail = if ($Detail) { "$Detail | $diag" } else { $diag }
    }
    Add-Check -Name $Name -Passed $Passed -Detail $Detail
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
$sourceSkillMd = Join-Path $SkillSource 'SKILL.md'
if (-not (Test-Path -LiteralPath $sourceSkillMd -PathType Leaf)) {
    Write-Error "No SKILL.md at '$sourceSkillMd'. Pass -SkillSource pointing at a folder that holds one." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SkillSource "scripts\$Gate.ps1") -PathType Leaf)) {
    Write-Error "No 'scripts\$Gate.ps1' under '$SkillSource'. The toy skill declares gate '$Gate'; its script must ship with the import." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
$skillName = Split-Path -Leaf (([string]$SkillSource).TrimEnd('\', '/'))
Add-Content -Path $log -Value ("M19 run check {0} exe={1} data={2} rooms={3} port={4} skill={5} conductor={6} worker={7}" `
    -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $Port, $skillName, $Conductor, $Worker)

$base = "http://127.0.0.1:$Port"
$hub = $null
function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $args = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30 }
    if ($null -ne $Body) { $args.ContentType = 'application/json'; $args.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @args
}
function Wait-Exchange([string]$RoomId, [string]$Until, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/exchange" -TimeoutSec 10 }
        catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
        if ($state -and $state.status -in ($Until -split ',')) { return $state }
        Start-Sleep -Seconds 3
    }
    return $state
}
function Get-Messages([string]$RoomId, [long]$AfterId = 0, [int]$Limit = 500) {
    try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=$AfterId&limit=$Limit" -TimeoutSec 10).messages) }
    catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
}
# Row 19: polls the run record itself (never message text — M10's array-unwrap lesson applies here
# too, but a single-object GET needs none of that). Collects every distinct `phase` value seen along
# the way into $script:SeenPhases, because the run's OWN record of "which tags it entered" (run_phases)
# has no REST surface of its own — the room's current phase, sampled over the run's whole life, is the
# same information read a different way, and it is still hub state, never a model's wording.
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

# --import-skill runs BEFORE the hub starts, same order M11 uses (row 11's m6 precondition), and
# avoids a second process touching chopitup.db while the hub holds it. Not itself one of task 15c's
# named checks; a failure here is a setup problem, so it stays a hard guard (exit 2), matching the
# fresh-directory guards above.
$importOut = Join-Path $DataDir 'import.out.log'
$importErr = Join-Path $DataDir 'import.err.log'
# Every path below is quoted INSIDE the argument string. Start-Process joins -ArgumentList with
# spaces and quotes nothing, so an unquoted path containing a space arrives at the exe as two
# arguments: this script's own default -SkillSource lives under 'C:\Agent Projects', and unquoted
# it imported as the skill named 'Agent'. The M11 script this scaffolding came from has the same
# shape and never tripped it only because its default path has no space in it.
$import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--import-skill', "`"$SkillSource`"") -PassThru -Wait -NoNewWindow `
    -RedirectStandardOutput $importOut -RedirectStandardError $importErr
if ($import.ExitCode -ne 0) {
    Write-Error "Importing '$SkillSource' failed (exit $($import.ExitCode)); see $importErr." -ErrorAction Continue
    exit 2
}

try {
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
    Add-Check -Name 'health.schema-is-10' -Passed ($health.schema -eq 10) -Detail "schema=$($health.schema)"

    # --- run.no-directory-refused: the 'general' room has no directory (seeded that way), so the
    # same invocation there must refuse rather than start a run (AC2). Checked BEFORE the real toy
    # room exists, so there is no active run anywhere yet to confuse the refusal path with. -----------
    $noDirPosted = Invoke-Api POST '/api/rooms/general/messages' @{ body = "/$skillName @$Conductor" }
    $noDirNote = $null
    foreach ($i in 1..15) {
        $msgs = Get-Messages -RoomId 'general' -AfterId $noDirPosted.id
        $noDirNote = $msgs | Where-Object { $_.authorId -eq 'hub' -and $_.body -like '*needs a room bound to a directory*' } | Select-Object -First 1
        if ($noDirNote) { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'run.no-directory-refused' -Passed ($null -ne $noDirNote) -Detail ($noDirNote.body ?? 'not seen')
    $generalExchange = Wait-Exchange -RoomId 'general' -Until 'open' -Seconds 5
    if ($generalExchange -and $generalExchange.status -eq 'open') {
        Add-Content -Path $log -Value "WARNING: general room opened an exchange after a no-directory run-start; that should never happen."
    }

    # --- create the toy room, WITH a directory, and invoke the skill once -------------------------
    $room = Invoke-Api POST '/api/rooms' @{ name = 'Toy run' }
    $roomId = $room.id
    $roomDir = $room.directory
    Add-Content -Path $log -Value "toy room: id=$roomId directory=$roomDir"

    $invokePosted = Invoke-Api POST "/api/rooms/$roomId/messages" @{ body = "/$skillName @$Conductor" }

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

    # --- run the whole thing to its end, spending real conductor/worker turns ----------------------
    $finalRun = Wait-Run -RoomId $roomId -Until 'ended,parked' -Seconds $TimeoutSeconds
    Add-Content -Path $log -Value ("final run: " + ($finalRun | ConvertTo-Json -Compress))
    # phaseHistory is the run_phases table as the hub recorded it, so this is a RECORD proof. The
    # polled union below it is kept only as log context: a phase the run entered and left inside one
    # 3-second poll gap is invisible to polling, and a check that FAILed on that would be
    # indistinguishable from a conductor that never entered the phase at all - which is the exact
    # ambiguity 15d exists to remove.
    $recorded = @()
    if ($finalRun.phaseHistory) { $recorded = @($finalRun.phaseHistory.PSObject.Properties.Name) }
    Add-Content -Path $log -Value ("phases recorded: " + ($recorded -join ',') + " | phases polled: " + ($script:SeenPhases -join ','))

    $sawBuild = $recorded -contains 'build'
    $sawPing = $recorded -contains 'ping'
    Add-ModelTriggeredCheck -Name 'run.two-phases' -Passed ($sawBuild -and $sawPing) `
        -Detail "phases recorded: $($recorded -join ',')" -RoomId $roomId

    Add-Check -Name 'run.exchanges-at-least-three' -Passed ($finalRun.exchanges -ge 3) -Detail "exchanges=$($finalRun.exchanges)"

    Add-Check -Name 'run.ended-not-parked' -Passed ($finalRun.status -eq 'ended') -Detail "status=$($finalRun.status) reason=$($finalRun.reason)"

    # --- run.one-human-message: exactly one message in the WHOLE room came from a human row ---------
    $participants = @(Invoke-Api GET '/api/participants' | ForEach-Object { $_ })
    $humanIds = @($participants | Where-Object kind -eq 'human' | ForEach-Object id)
    $allMessages = Get-Messages -RoomId $roomId -AfterId 0
    $humanMessages = @($allMessages | Where-Object { $_.authorId -in $humanIds })
    Add-Check -Name 'run.one-human-message' -Passed ($humanMessages.Count -eq 1) `
        -Detail "count=$($humanMessages.Count) authors=$(($humanMessages | ForEach-Object authorId) -join ',')"

    # --- run.file-created: hello.txt landed in the room's own directory ----------------------------
    $helloPath = Join-Path $roomDir 'hello.txt'
    $helloExists = Test-Path -LiteralPath $helloPath -PathType Leaf
    Add-ModelTriggeredCheck -Name 'run.file-created' -Passed $helloExists -Detail $helloPath -RoomId $roomId

    # --- run.artifact-author: the hub's OWN record of who wrote it, from the spawn's git diff -------
    $helloArtifact = @($finalRun.artifacts | Where-Object { $_.path -eq 'hello.txt' -and $_.authorId -eq $Worker })
    Add-ModelTriggeredCheck -Name 'run.artifact-author' -Passed ($helloArtifact.Count -eq 1) `
        -Detail "artifacts=$(($finalRun.artifacts | ForEach-Object { "$($_.path):$($_.authorId)" }) -join ' | ')" -RoomId $roomId

    # --- run.no-failure-notes: same fixed substrings M11's live check already trusts ----------------
    $hubNotes = @($allMessages | Where-Object authorId -eq 'hub')
    Add-Check -Name 'run.no-failure-notes' -Passed (-not ($hubNotes | Where-Object { $_.body -match 'did not reply|without posting|could not be started|exited with code' })) `
        -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')

    # --- gate.ran-in-run: a run_gate_runs row for count-files, exit 0, plus its fixed-form note -----
    $gateRun = @($finalRun.gateRuns | Where-Object { $_.gate -eq $Gate -and $_.exitCode -eq 0 })
    $gateNote = $hubNotes | Where-Object { $_.body -like "run_gate $Gate by @*: exit 0" } | Select-Object -First 1
    Add-ModelTriggeredCheck -Name 'gate.ran-in-run' -Passed ($gateRun.Count -eq 1 -and $null -ne $gateNote) `
        -Detail "gateRuns=$(($finalRun.gateRuns | ForEach-Object { "$($_.gate):$($_.outcome)" }) -join ' | '); note=$($gateNote.body ?? 'not seen')" -RoomId $roomId
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $orphans = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $orphans) { Write-Host "orphan from this check, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }

    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
