<#
.SYNOPSIS
    Row 20, task 5: round-trips the three MCP timeout knobs (ledger 10/11/25) against a scratch hub,
    with real claude.exe calls, to prove which ones actually cut a long tool call and that the raised
    knobs (row 20, task 3) keep one alive past the CLI's documented 5-minute idle default.

.DESCRIPTION
    SPEND: four Sonnet calls, about 10 minutes wall clock. This script is NOT part of the automated
    suite - the orchestrator runs it by hand, once, after task 3 has merged to this branch, and records
    the result in docs\verification.md. Never invoke it from a builder subagent or a CI job.

    Never touches C:\Self Apps, %USERPROFILE%\ChopItUp or any real data directory: -DataDir, -RoomsRoot
    and the leg-4 scratch repo all default to fresh folders under $env:TEMP and are left behind with
    the log (a fresh hub is required every run; this script refuses to reuse an existing -DataDir).

    Five legs, numbered 0-4, each direct-spawn leg (0-3) builds its child's environment explicitly -
    REMOVING MCP_TOOL_TIMEOUT and CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT from the inherited environment
    before adding the one knob under test - so a leg can only be cut by the knob it names, never by
    something left over from the shell that launched this script:

      0. Control: no knob at all. Must take >= 35s and return a non-error tool result. Runs FIRST so a
         fast machine cannot fake the cut legs that follow it.
      1. MCP_TOOL_TIMEOUT=5000 in the env, no per-server "timeout" field.
      2. No env knob, "timeout": 5000 on the chopitup server entry in the per-leg mcp.json.
      Legs 1-2 PASS when the process exits in < 30s AND the stream-json output carries a tool_result
      content block marked as an error.
      3. CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT=5000 in the env, no other knob. Measured probe run 1: the
         installed CLI (2.1.220, Bun-compiled) polls its idle clock on a 30-second setInterval rather
         than cutting at the knob's own 5s value, so the cut lands on the first or second tick. Leg 3
         PASSes when the process exits with an error tool_result AND total wall clock is under 65s (one
         or two 30-s ticks) AND shorter than leg 0's own elapsed time.
      4. RESCUE LEG (pass-1 M5), the product path: legs 0-3 only prove a knob can shorten a call: this
         leg proves the opposite direction the design actually needs - a call the CLI's 5-minute idle
         default WOULD cut survives when the hub raises the knobs it sets on its own in-run spawns
         (task 3). Installs tools\skills\probe-sleep (SKILL.md + scripts\sleep.ps1, this task) into the
         probe's own scratch hub, binds a directory room to a fresh scratch git repository (`git init`
         plus one commit, made by this script - a room directory must be a repository root), posts
         "/probe-sleep @sonnet", and waits (<= 20 min) for the run to end. PASS when the run's own gate
         list records "sleep" at exit 0 (hub-controlled fact) and the run ended by the conductor's ping
         rather than a silence park. One Sonnet spawn: the conductor calls run_gate itself, in the same
         turn it posts the phase: ping (probe run 1's task 3b fix; the lite path's own shape).

      A leg-4 FAIL with legs 1-3 all passing means the hub's progress notifications (task 3b) are not
      reaching the client, even though the CLI's own timeout knobs are fine - that RE-OPENS TASK 3B, not
      this probe: nothing here should be "fixed" to make leg 4 pass on its own.

    Stream-json parsing (legs 0-3): the Claude Code CLI's `--output-format stream-json --verbose` shape
    is read as newline-delimited JSON events; a completed tool call is taken to surface as a
    `{"type":"tool_result", ..., "is_error":true|false}` content block inside one of those events. THIS
    SHAPE IS ASSUMED from the CLI's documented streaming format (ledger 11/25) and has not been
    confirmed against real output before this script's first live run - leg 0 is what confirms or
    refutes it (see Test-StreamJsonToolResult below).

    Every leg prints PASS/FAIL; the last line is "Results: n/5 PASS". The hub is stopped by PID,
    always, and every orphan whose command line names this run's -DataDir is swept in the same
    `finally`.

.PARAMETER HubExe
    Path to the built ChopItUp.Hub.exe. Defaults to the Debug build under this repo.

.PARAMETER DataDir
    Fresh hub data directory for the probe's own scratch hub. Must not already exist.

.PARAMETER RoomsRoot
    Rooms root for the probe's own scratch hub. Defaults to a sibling of -DataDir.

.PARAMETER Port
    Loopback port the probe's scratch hub listens on.

.PARAMETER SleepSkillSource
    Folder holding the probe-sleep skill (SKILL.md + scripts\sleep.ps1). Defaults to
    tools\skills\probe-sleep next to this script.

.PARAMETER Leg4TimeoutSeconds
    How long to wait for leg 4's run to reach ended/parked before giving up.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_timeoutprobe_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8802,
    [string]$SleepSkillSource = (Join-Path $PSScriptRoot 'skills\probe-sleep'),
    [int]$Leg4TimeoutSeconds = 1200
)

$ErrorActionPreference = 'Stop'
if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }   # a sibling: never under the data dir a deny rule protects
$script:Checks = New-Object System.Collections.Generic.List[object]
$script:LegSeconds = @{}   # LegName -> elapsed seconds, recorded by Invoke-DirectLeg; leg 3 compares against leg 0's
$log = "$DataDir.timeout-probe.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
$sleepSkillMd = Join-Path $SleepSkillSource 'SKILL.md'
if (-not (Test-Path -LiteralPath $sleepSkillMd -PathType Leaf)) {
    Write-Error "No SKILL.md at '$sleepSkillMd'. Pass -SleepSkillSource pointing at the probe-sleep skill folder." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath (Join-Path $SleepSkillSource 'scripts\sleep.ps1') -PathType Leaf)) {
    Write-Error "No 'scripts\sleep.ps1' under '$SleepSkillSource'. The probe-sleep skill declares gate 'sleep'; its script must ship with the import." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
$claudeCmd = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claudeCmd) { Write-Error "claude must resolve on PATH." -ErrorAction Continue; exit 2 }
$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCmd) { Write-Error "git must resolve on PATH (leg 4's scratch repo, and the hub's own room commit trail)." -ErrorAction Continue; exit 2 }

New-Item -ItemType Directory -Path $DataDir | Out-Null
Add-Content -Path $log -Value ("MCP timeout probe {0} exe={1} data={2} rooms={3} port={4}" -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $Port)

$work = "$DataDir.work"
New-Item -ItemType Directory -Path $work -Force | Out-Null

# NOTE: the parameter is $argv, never $args - PowerShell reserves $args, and a helper that names a
# parameter $args silently receives nothing (Probe-SpawnCli.ps1, measured 2026-09-05).
#
# $RemoveEnv strips inherited knobs BEFORE $Env adds the one under test, so a leg can only be cut by
# the knob it names, never by whatever this script's own shell happened to have set.
function Invoke-Child {
    param([string]$FileName, [string[]]$argv, [hashtable]$Env, [string[]]$RemoveEnv, [string]$Stdin, [string]$WorkDir, [int]$KillAfterMs = 90000)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    foreach ($a in $argv) { $psi.ArgumentList.Add($a) }
    foreach ($k in $RemoveEnv) { $psi.Environment.Remove($k) }
    foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] }
    $psi.WorkingDirectory = $WorkDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.Write($Stdin)
    $p.StandardInput.Close()
    if (-not $p.WaitForExit($KillAfterMs)) { try { $p.Kill($true) } catch { } }
    $p.WaitForExit()   # parameterless: guarantees the async reads above are drained (LESSONS M4)
    [pscustomobject]@{ Exit = $p.ExitCode; Out = $out.Result; Err = $err.Result; Seconds = [double]$sw.Elapsed.TotalSeconds; ProcessId = $p.Id }
}

function New-ClaudeArgs {
    param([string]$McpJsonPath)
    @('-p', '--tools', '', '--strict-mcp-config', '--mcp-config', $McpJsonPath,
        '--allowedTools', 'mcp__chopitup__wait_for_message', '--no-session-persistence', '--model', 'sonnet',
        '--output-format', 'stream-json', '--verbose', '--disable-slash-commands', '--setting-sources', '')
}

function New-McpJson {
    param([string]$McpUrl, [string]$Token, $ToolTimeoutMs = $null)
    $server = [ordered]@{ type = 'http'; url = $McpUrl; headers = [ordered]@{ Authorization = "Bearer $Token" } }
    if ($null -ne $ToolTimeoutMs) { $server['timeout'] = $ToolTimeoutMs }
    ([ordered]@{ mcpServers = [ordered]@{ chopitup = $server } } | ConvertTo-Json -Depth 6 -Compress)
}

# See the .DESCRIPTION note on stream-json parsing above - this shape is ASSUMED, not yet verified
# against real CLI output. A block is read as a tool_result if it carries `type: "tool_result"`,
# wherever it sits in an event's `content` array (top-level or nested under `message.content`).
function Test-StreamJsonToolResult {
    param([string]$StreamOut)
    $sawToolResult = $false
    $isError = $false
    foreach ($line in ($StreamOut -split "`r?`n")) {
        if (-not $line.Trim()) { continue }
        $evt = $null
        try { $evt = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        $content = $null
        if ($evt.PSObject.Properties['message'] -and $evt.message.PSObject.Properties['content']) { $content = $evt.message.content }
        elseif ($evt.PSObject.Properties['content']) { $content = $evt.content }
        if (-not $content) { continue }
        foreach ($block in @($content)) {
            if ($block.PSObject.Properties['type'] -and $block.type -eq 'tool_result') {
                $sawToolResult = $true
                if ($block.PSObject.Properties['is_error'] -and $block.is_error -eq $true) { $isError = $true }
            }
        }
    }
    [pscustomobject]@{ SawToolResult = $sawToolResult; IsError = $isError }
}

$prompt = 'Call wait_for_message with room_id "general" and timeout_seconds 40 exactly once, then reply with the tool''s result text verbatim.'
$mcpUrl = "http://127.0.0.1:$Port/mcp"
$knobEnvVars = @('MCP_TOOL_TIMEOUT', 'CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT')

function Invoke-DirectLeg {
    param([string]$LegName, [hashtable]$EnvKnobs, $ToolTimeoutMs, [bool]$ExpectCut, [string]$Token, [double]$MaxCutSeconds = 30, $MustBeShorterThanSeconds = $null)
    $legDir = Join-Path $work $LegName
    New-Item -ItemType Directory -Path $legDir -Force | Out-Null
    $mcpJsonPath = Join-Path $legDir 'mcp.json'
    Set-Content -LiteralPath $mcpJsonPath -Value (New-McpJson -McpUrl $mcpUrl -Token $Token -ToolTimeoutMs $ToolTimeoutMs) -NoNewline -Encoding utf8
    $r = Invoke-Child -FileName $claudeCmd.Source -argv (New-ClaudeArgs -McpJsonPath $mcpJsonPath) `
        -Env $EnvKnobs -RemoveEnv $knobEnvVars -Stdin $prompt -WorkDir $legDir
    $script:LegSeconds[$LegName] = $r.Seconds
    $parsed = Test-StreamJsonToolResult -StreamOut $r.Out
    $passed = if ($ExpectCut) {
        ($r.Seconds -lt $MaxCutSeconds) -and $parsed.SawToolResult -and $parsed.IsError -and `
            (($null -eq $MustBeShorterThanSeconds) -or ($r.Seconds -lt $MustBeShorterThanSeconds))
    } else {
        ($r.Seconds -ge 35) -and $parsed.SawToolResult -and (-not $parsed.IsError)
    }
    Add-Check -Name $LegName -Passed $passed `
        -Detail "exit=$($r.Exit) seconds=$([math]::Round($r.Seconds,1)) sawToolResult=$($parsed.SawToolResult) isError=$($parsed.IsError)"
    if (-not $passed) {
        $tail = $r.Err
        if ($tail.Length -gt 600) { $tail = $tail.Substring($tail.Length - 600) }
        Add-Content -Path $log -Value "$LegName stderr tail: $($tail.Replace($Token, '<token>'))"
    }
}

$base = "http://127.0.0.1:$Port"
function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $callArgs = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30 }
    if ($null -ne $Body) { $callArgs.ContentType = 'application/json'; $callArgs.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @callArgs
}
function Get-Messages([string]$RoomId, [long]$AfterId = 0, [int]$Limit = 500) {
    try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=$AfterId&limit=$Limit" -TimeoutSec 10).messages) }
    catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
}
function Wait-Run([string]$RoomId, [string]$Until, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/run" -TimeoutSec 10 } catch { Add-Content -Path $log -Value "poll run failed: $($_.Exception.Message)" }
        if ($state -and $state.status -in ($Until -split ',')) { return $state }
        Start-Sleep -Seconds 5
    }
    return $state
}

# --import-skill runs BEFORE the hub starts, same order M19/M20 use (row 11's m6 precondition): a
# second process must never touch chopitup.db while the hub holds it. A setup failure here is not one
# of the five named legs, so it stays a hard guard (exit 2), same as the fresh-directory checks above.
$importOut = Join-Path $DataDir 'import.out.log'
$importErr = Join-Path $DataDir 'import.err.log'
# Every path below is quoted INSIDE the argument string (LESSONS, 2026-09-07): Start-Process joins
# -ArgumentList with spaces and quotes nothing, and this repo lives under 'C:\Agent Projects'.
$import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--import-skill', "`"$SleepSkillSource`"") -PassThru -Wait -NoNewWindow `
    -RedirectStandardOutput $importOut -RedirectStandardError $importErr
if ($import.ExitCode -ne 0) {
    Write-Error "Importing '$SleepSkillSource' failed (exit $($import.ExitCode)); see $importErr." -ErrorAction Continue
    exit 2
}

$hub = $null
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

    $tokensPath = Join-Path $DataDir 'tokens.json'
    if (-not (Test-Path -LiteralPath $tokensPath -PathType Leaf)) {
        Write-Error "No tokens.json under '$DataDir' after the hub's first start." -ErrorAction Continue
        exit 2
    }
    $tokens = Get-Content -LiteralPath $tokensPath -Raw | ConvertFrom-Json
    $sonnetToken = $tokens.sonnet
    if (-not $sonnetToken) {
        Write-Error "tokens.json has no token for 'sonnet'." -ErrorAction Continue
        exit 2
    }

    # --- Leg 0: control, no knob at all -------------------------------------------------------------
    Invoke-DirectLeg -LegName 'leg0.control-no-knob' -EnvKnobs @{} -ToolTimeoutMs $null -ExpectCut $false -Token $sonnetToken

    # --- Leg 1: MCP_TOOL_TIMEOUT env var only -------------------------------------------------------
    Invoke-DirectLeg -LegName 'leg1.env-MCP_TOOL_TIMEOUT' -EnvKnobs @{ MCP_TOOL_TIMEOUT = '5000' } -ToolTimeoutMs $null -ExpectCut $true -Token $sonnetToken

    # --- Leg 2: per-server "timeout" field only ------------------------------------------------------
    Invoke-DirectLeg -LegName 'leg2.server-timeout-field' -EnvKnobs @{} -ToolTimeoutMs 5000 -ExpectCut $true -Token $sonnetToken

    # --- Leg 3: CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT env var only ----------------------------------------
    # Measured probe run 1: the installed CLI polls its idle clock on a 30-second setInterval, so the
    # cut lands at one or two ticks (up to ~60s), not at the knob's own 5s value. PASS needs the error
    # result, wall clock under 65s, and shorter than leg 0's own control time.
    Invoke-DirectLeg -LegName 'leg3.env-idle-timeout' -EnvKnobs @{ CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT = '5000' } -ToolTimeoutMs $null -ExpectCut $true -Token $sonnetToken -MaxCutSeconds 65 -MustBeShorterThanSeconds $script:LegSeconds['leg0.control-no-knob']

    # --- Leg 4: rescue leg, the product path ----------------------------------------------------------
    # A room directory must be a repository root (RoomDirectories.PrepareAsync): git init it and make
    # one commit ourselves, rather than let the hub's own auto-init be the only thing that ever ran it.
    $repoDir = Join-Path $work 'leg4-repo'
    New-Item -ItemType Directory -Path $repoDir -Force | Out-Null

    # Each git step's own exit code is checked explicitly (native command exit codes are not
    # terminating errors here) so a broken seed reports itself as leg 4's own FAIL, naming which git
    # step failed, instead of the room bind or the run wait failing later for an opaque reason.
    $seedFailStep = $null
    & $gitCmd.Source -C $repoDir init -b main *> (Join-Path $work 'leg4-git-init.log')
    if ($LASTEXITCODE -ne 0) { $seedFailStep = "git init (exit $LASTEXITCODE)" }

    if (-not $seedFailStep) {
        Set-Content -LiteralPath (Join-Path $repoDir 'README.md') -Value "Scratch repo for the row 20 task 5 MCP timeout probe rescue leg.`n" -NoNewline -Encoding utf8
        & $gitCmd.Source -C $repoDir add -A *> (Join-Path $work 'leg4-git-add.log')
        if ($LASTEXITCODE -ne 0) { $seedFailStep = "git add (exit $LASTEXITCODE)" }
    }

    if (-not $seedFailStep) {
        & $gitCmd.Source -C $repoDir -c user.name='probe' -c user.email='probe@example.invalid' commit -m 'seed' *> (Join-Path $work 'leg4-git-commit.log')
        if ($LASTEXITCODE -ne 0) { $seedFailStep = "git commit (exit $LASTEXITCODE)" }
    }

    if ($seedFailStep) {
        Add-Check -Name 'leg4.rescue-sleep-gate' -Passed $false -Detail "leg4 seed repo failed: $seedFailStep; see leg4-git-*.log under $work"
    } else {
        $room = Invoke-Api POST '/api/rooms' @{ name = 'Sleep probe'; directory = $repoDir }
        $roomId = $room.id
        Add-Content -Path $log -Value "leg4 room: id=$roomId directory=$($room.directory)"

        $invokePosted = Invoke-Api POST "/api/rooms/$roomId/messages" @{ body = '/probe-sleep @sonnet' }
        $startedRun = $null
        foreach ($i in 1..20) {
            try { $startedRun = Invoke-RestMethod -Uri "$base/api/rooms/$roomId/run" -TimeoutSec 10 } catch { }
            if ($startedRun -and $startedRun.status -eq 'active') { break }
            Start-Sleep -Seconds 1
        }
        if (-not ($startedRun -and $startedRun.status -eq 'active')) {
            Add-Check -Name 'leg4.rescue-sleep-gate' -Passed $false -Detail "run never started: status=$($startedRun.status ?? 'none')"
        } else {
            $finalRun = Wait-Run -RoomId $roomId -Until 'ended,parked' -Seconds $Leg4TimeoutSeconds
            Add-Content -Path $log -Value ("leg4 final run: " + ($finalRun | ConvertTo-Json -Compress -Depth 6))
            $sleepGate = @($finalRun.gateRuns | Where-Object { $_.gate -eq 'sleep' -and $_.exitCode -eq 0 })
            $endedByPing = ($finalRun.status -eq 'ended') -and ($finalRun.reason -match 'pinged')
            $passed = ($sleepGate.Count -eq 1) -and $endedByPing
            Add-Check -Name 'leg4.rescue-sleep-gate' -Passed $passed `
                -Detail "status=$($finalRun.status) reason=$($finalRun.reason) gateRuns=$(($finalRun.gateRuns | ForEach-Object { "$($_.gate):$($_.outcome):$($_.exitCode)" }) -join ' | ')"
            if (-not $passed) {
                $hubNotes = @((Get-Messages -RoomId $roomId) | Where-Object authorId -eq 'hub')
                Add-Content -Path $log -Value ("leg4 hub notes: " + (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | '))
            }
        }
    }
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $orphans = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $orphans) { Write-Host "orphan from this probe, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue

    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -eq 5) { 0 } else { 1 })
