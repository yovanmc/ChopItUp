<#
.SYNOPSIS
    M5 live check: starts a hub on a scratch data directory, posts one owner message that mentions
    @sonnet and asks it to hand the turn to gpt-5.4-mini, and waits for the exchange to conclude.

.DESCRIPTION
    Proves the composition the unit tests cannot: the real claude.exe and codex.cmd, signed in on
    this machine, spawned by the hub with the verified command lines, posting back through the hub's
    own MCP endpoint, under the real budget and timeout. Costs two to four short model calls on the
    owner's subscriptions. Never touches C:\Self Apps or any real data directory: -DataDir defaults
    to a fresh folder under $env:TEMP and is left behind with the log.

    Every check prints PASS/FAIL; the last line is "Results: n/m PASS"; exit 0 only when all pass.
    The hub it starts is stopped by PID at the end, always.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m5check_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8797,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m5-check.log"

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
if (Test-Path -LiteralPath $DataDir) {
    Write-Error "-DataDir '$DataDir' already exists; this script only ever runs against a fresh directory." -ErrorAction Continue
    exit 2
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
Add-Content -Path $log -Value ("M5 spawn check {0} exe={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)

$claude = Get-Command claude -ErrorAction SilentlyContinue
$codex = Get-Command codex -ErrorAction SilentlyContinue
Add-Check -Name 'cli.claude-on-path' -Passed ([bool]$claude) -Detail ($claude.Source ?? 'not found')
Add-Check -Name 'cli.codex-on-path' -Passed ([bool]$codex) -Detail ($codex.Source ?? 'not found')

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--port', "$Port") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'hub.health-schema' -Passed ($health.schema -eq 9) -Detail "schema=$($health.schema)"

    # Polls never throw ($ErrorActionPreference is Stop): a transient hub error is logged and the
    # check that reads the result fails by name, so the run always ends with a Results line.
    function Wait-Exchange([string]$Until, [int]$Seconds) {
        $deadline = (Get-Date).AddSeconds($Seconds)
        $state = $null
        while ((Get-Date) -lt $deadline) {
            try { $state = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 }
            catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
            if ($state -and $state.status -in ($Until -split ',')) { return $state }
            Start-Sleep -Seconds 3
        }
        return $state
    }
    function Read-Room {
        try { @((Invoke-RestMethod -Uri "$base/api/rooms/general/messages?afterId=0&limit=200" -TimeoutSec 10).messages) }
        catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
    }
    # Everything carrying our data dir on its command line except the hub and this script itself
    # (a caller who passed -DataDir has it on pwsh's own command line).
    function Our-Processes { @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $hub.Id -and $_.ProcessId -ne $PID }) }

    # --- Leg 1: one owner message, a chain to a conclusion. An LLM must volunteer the second mention,
    #     so a chain that stops after sonnet is INCONCLUSIVE once and retried; only two misses fail.
    $body = 'In one short line, name one thing worth checking in a deploy script, @sonnet. Then hand the turn to gpt-5.4-mini for one line of pushback, by mentioning it with an @ in front of its id.'
    $attempts = 0; $state = $null; $messages = @(); $mini = @()
    do {
        $attempts++
        $posted = Invoke-RestMethod -Uri "$base/api/rooms/general/messages" -Method Post -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json)
        Add-Check -Name "post.owner-message-$attempts" -Passed ($posted.id -ge 1) -Detail "id=$($posted.id)"
        $state = Wait-Exchange -Until 'concluded,stopped' -Seconds $TimeoutSeconds
        $messages = Read-Room
        $mini = @($messages | Where-Object { $_.authorId -eq 'gpt-5.4-mini' -and $_.id -gt $posted.id })
        if ($mini.Count -eq 0 -and $attempts -lt 2) { Add-Content -Path $log -Value "attempt $attempts INCONCLUSIVE: no gpt-5.4-mini reply; retrying" }
    } while ($mini.Count -eq 0 -and $attempts -lt 2)
    Add-Content -Path $log -Value ("exchange: " + ($state | ConvertTo-Json -Compress))
    foreach ($m in $messages) { Add-Content -Path $log -Value ("#{0} {1}: {2}" -f $m.id, $m.authorId, ($m.body -replace "`r?`n", ' / ')) }

    $sonnet = @($messages | Where-Object authorId -eq 'sonnet')
    $hubNotes = @($messages | Where-Object authorId -eq 'hub')
    Add-Check -Name 'spawn.sonnet-replied' -Passed ($sonnet.Count -ge 1) -Detail "count=$($sonnet.Count)"
    Add-Check -Name 'spawn.gpt-5.4-mini-replied' -Passed ($mini.Count -ge 1) -Detail "count=$($mini.Count) attempts=$attempts"
    Add-Check -Name 'exchange.concluded' -Passed ($state.status -eq 'concluded') -Detail "status=$($state.status) turnsUsed=$($state.turnsUsed)"
    Add-Check -Name 'exchange.concluded-note' -Passed ([bool]($hubNotes | Where-Object body -like 'Exchange concluded:*')) -Detail "hubNotes=$($hubNotes.Count)"
    Add-Check -Name 'exchange.turns-within-budget' -Passed ($state.turnsUsed -ge 1 -and $state.turnsUsed -le 4) -Detail "turnsUsed=$($state.turnsUsed)"
    Add-Check -Name 'exchange.no-failure-notes' -Passed (-not ($hubNotes | Where-Object { $_.body -match 'did not reply|without posting|could not be started|exited with code' })) -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')

    # --- Leg 2: the owner's stop kills a real CLI tree. Ask for a slow reply, stop it mid-flight, then
    #     assert nothing that carries our data dir on its command line is still running.
    $slow = Invoke-RestMethod -Uri "$base/api/rooms/general/messages" -Method Post -ContentType 'application/json' -Body (@{ body = '@sonnet write twelve numbered lines, one per line, each a different deploy-script check, then post them.' } | ConvertTo-Json)
    $inFlight = $null
    foreach ($i in 1..20) {
        try { $inFlight = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 } catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
        if ($inFlight -and $inFlight.inFlight.Count -gt 0) { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'stop.spawn-in-flight' -Passed ($inFlight.inFlight.Count -gt 0) -Detail ("inFlight=" + ($inFlight.inFlight -join ','))
    $live = Our-Processes
    Add-Check -Name 'stop.real-process-running' -Passed ($live.Count -gt 0) -Detail ("pids=" + (($live | ForEach-Object ProcessId) -join ','))
    $stopReply = $null
    try { $stopReply = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange/stop" -Method Post } catch { }
    $stopped = Wait-Exchange -Until 'stopped' -Seconds 30
    Start-Sleep -Seconds 3
    $orphans = Our-Processes
    Add-Check -Name 'stop.exchange-stopped' -Passed ($stopped.status -eq 'stopped') -Detail "status=$($stopped.status)"
    Add-Check -Name 'stop.no-orphaned-cli' -Passed ($orphans.Count -eq 0) -Detail ("pids=" + (($orphans | ForEach-Object ProcessId) -join ','))
    $messages = Read-Room
    Add-Check -Name 'stop.note-posted' -Passed ([bool]($messages | Where-Object { $_.authorId -eq 'hub' -and $_.body -like 'Exchange stopped by the owner:*' })) -Detail ''

    $tokens = (Get-Content -LiteralPath (Join-Path $DataDir 'tokens.json') -Raw | ConvertFrom-Json).PSObject.Properties.Value
    $leak = $messages | Where-Object { $b = $_.body; $tokens | Where-Object { $b.Contains($_) } }
    Add-Check -Name 'privacy.no-token-in-any-message' -Passed (-not $leak) -Detail "messages=$($messages.Count)"
    $spawnDirs = @(Get-ChildItem -LiteralPath (Join-Path $DataDir 'spawns') -Directory -ErrorAction SilentlyContinue)
    Add-Check -Name 'spawns.workdirs-cleaned' -Passed ($spawnDirs.Count -eq 0) -Detail "leftover=$($spawnDirs.Count)"
}
finally {
    # Stop through the hub first so it kills its own children; only then the hub by PID.
    try { Invoke-RestMethod -Uri "$base/api/rooms/general/exchange/stop" -Method Post -TimeoutSec 10 | Out-Null } catch { }
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $left = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $left) { Write-Host "orphan from this check, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
}

$passed = @($script:Checks | Where-Object Passed).Count
$total = $script:Checks.Count
Write-Host "M5 spawn check log: $log"
Write-Host "Results: $passed/$total PASS"
if ($passed -eq $total) { exit 0 } else { exit 1 }
