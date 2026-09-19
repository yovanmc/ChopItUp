<#
.SYNOPSIS
    Row 44 dry run: proves the turns: token, the range-note refusal and /continue end to end against
    the built hub, with a stub Codex CLI that holds every spawn open and no real model ever reachable.

.DESCRIPTION
    Mirrors Invoke-Row43MentionCheck.ps1's frame (itself mirroring Invoke-Row42ImportCheck.ps1): a
    param block with -HubExe/-ScratchRoot (fresh, under $env:TEMP)/-Port/-TimeoutSeconds, Add-Check,
    ChopTokenHelpers.ps1 seeding 'owner' before the hub's first start, the hub started by PID and
    stopped in a finally block, "Results: n/m PASS", exit 0 only when every check passes and the
    total is exactly 7. Every array read off Invoke-RestMethod/Invoke-Api is piped through
    ForEach-Object { $_ } before Where-Object (LESSONS M10: a bare top-level JSON array comes back as
    one nested Object[]).

    Every scratch path this script writes -- the hub's data dir, the stub PATH dir -- lives under one
    root, -ScratchRoot ($env:TEMP\chopitup_row44dryrun_<guid> by default): data\ and stub\ underneath
    it. The pre-existence guard checks only that root, and the whole run (directory creation, the
    token seed, the port probe, the hub start and all seven legs) runs inside one try whose finally
    removes the root once the hub's own process (if it ever started) has vanished, so a throw before
    the hub even starts leaves nothing behind either.

    PATH for the child hub is a scratch 'stub\' directory holding codex.cmd (the Row 34 shape: @echo
    off / ping -n 600 127.0.0.1 >nul / exit /b 0) plus $env:SystemRoot\System32 and $env:SystemRoot
    only, so neither a real claude.exe nor a real codex.exe/codex.cmd is reachable (CliResolver takes
    name.exe anywhere on PATH before a shim, then wraps a .cmd as "cmd.exe /d /c <shim>"). Every
    participant this script mentions (gpt-5.6-terra, gpt-5.6-sol) is a Codex-hosted row in
    ChopDb.SeedRoster, so the stub answers every spawn attempt the legs below can trigger and holds
    each open for ten minutes -- ample for a script that finishes in well under a minute and
    explicitly stops everything in leg 7. The script's own PATH is saved before Start-Process and
    restored in a finally, so git/dotnet stay available afterward regardless of how the run ends.

    THE SEVEN LEGS, in order, each a barrier on a hub note or on the exchange snapshot's own state,
    never a bare timer (Row 42 lesson):
      1. health.ok -- a plain /health poll.
      2. turns.override -- 'turns: 2 @gpt-5.6-terra hold this open' opens an exchange whose own
         exchanges[] entry shows budget 2 with gpt-5.6-terra in flight (the stub holds it there).
      3. turns.range-note -- 'turns: 99 @gpt-5.6-sol hold this open' (99 is out of range) draws the
         note 'turns: must be a whole number from 1 to 16; the default 8 applies.' and that root's
         entry shows budget 8.
      4. continue.open-refused -- '/continue' as a reply to leg 2's root, while gpt-5.6-terra is still
         in flight there, draws 'Exchange started at #<root2> is still open with 1 turn(s) left;
         /continue once it has concluded.' and spawns nothing new.
      5. continue.after-stop -- POST .../exchanges/<root2>/stop ends leg 2's exchange (its entry goes
         'stopped' with inFlight empty and continuable true), then '/continue' as a reply to root2
         draws 'Exchange started at #<root2> continued: 8 more turn(s), 10 in all; queued
         @gpt-5.6-terra.' and the entry goes back to 'open' with budget 10 and gpt-5.6-terra pending
         or in flight.
      6. continue.nothing -- '/continue' as a reply to the hub note from leg 3 (a hub note roots no
         exchange) draws 'Reply to #<noteId>: that message is in no exchange this hub still holds, so
         there is nothing to continue.'
      7. stop.cleanup -- the room's own stop, then this script's own hub process tree ended by PID
         (never by name) and waited for; the scratch root itself is removed afterward, in the outer
         finally, once that PID has vanished.

    Because legs 2, 4 and 5 keep reusing the same root (root2), every assertion below reads the
    snapshot's exchanges[] array for a match on that message's own id, never the top-level fields
    (which the newest open exchange, not necessarily this one, still owns) -- same discipline as Row
    43's script.

    NEGATIVE LEG (AC7, M24 lesson: a guard only binds something once its mechanism has been reverted
    and seen to fail). With the `if (ExchangeCommands.IsContinue(m.Body) && ...)` block commented out
    of SpawnerService.OnMessage, rebuilt, and this script re-run against the same stub PATH:

        measured 2026-09-18, reverted build, 4/7 PASS. FAILED: continue.open-refused, continue.after-
        stop, continue.nothing (each /continue post now falls through to skill resolution and draws
        "No skill named '/continue'" instead of any of the three notes above). health.ok,
        turns.override and turns.range-note kept passing: neither leg touches /continue.

    Restored, rebuilt, re-run: 7/7 PASS (measured 2026-09-18).

    Never touches C:\Self Apps or any real data directory: -ScratchRoot defaults to a fresh folder
    under $env:TEMP and the outer finally removes it once the hub's own PID (if any was ever started)
    has vanished, on both a clean run and a thrown one.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$ScratchRoot = (Join-Path $env:TEMP ('chopitup_row44dryrun_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8809,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
$script:Checks = New-Object System.Collections.Generic.List[object]
# A trailing separator would place a bare backslash before the closing quote in Start-Process's
# quoted -ArgumentList entry, which the child process's own argv parser reads as an escaped quote
# rather than a path terminator (same trap Invoke-Row42ImportCheck.ps1 guards against).
$ScratchRoot = $ScratchRoot.TrimEnd('\', '/')
$DataDir = Join-Path $ScratchRoot 'data'
$StubDir = Join-Path $ScratchRoot 'stub'
# A sibling of the root, not under it, so the log survives the root's own removal in the finally below.
$log = "$ScratchRoot.row44-dryrun.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# One request, uniformly: GET has no -Body; a write carries $ownerAuth. Never throws on a non-2xx
# status (Invoke-WebRequest -SkipHttpErrorCheck) so a refusal is a value to assert on, not an
# exception to catch.
function Invoke-Api {
    param([string]$Method, [string]$Path, [hashtable]$Body, [hashtable]$Headers)
    $params = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = $TimeoutSeconds; SkipHttpErrorCheck = $true }
    if ($Headers) { $params.Headers = $Headers }
    if ($Body) { $params.ContentType = 'application/json'; $params.Body = ($Body | ConvertTo-Json -Depth 6) }
    $raw = Invoke-WebRequest @params
    $parsed = if ($raw.Content) { try { $raw.Content | ConvertFrom-Json } catch { $null } } else { $null }
    return [pscustomobject]@{ Status = [int]$raw.StatusCode; Body = $parsed }
}

# The room's own exchange snapshot: exchanges[] (never the top-level fields -- root2's exchange is
# reused across legs 2, 4 and 5, so the newest-open top-level view is not necessarily it).
function Get-Exchanges {
    $resp = Invoke-Api -Method Get -Path '/api/rooms/general/exchange'
    return @($resp.Body.exchanges | ForEach-Object { $_ })
}

function Wait-ExchangeState {
    param([long]$RootMessageId, [scriptblock]$Predicate, [int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $entry = Get-Exchanges | Where-Object { $_.rootMessageId -eq $RootMessageId } | Select-Object -First 1
        if ($entry -and (& $Predicate $entry)) { return $entry }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

function Wait-HubNotePrefix {
    param([long]$AfterId, [string]$Prefix, [int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $resp = Invoke-Api -Method Get -Path "/api/rooms/general/messages?afterId=$AfterId&limit=50"
        $msgs = @($resp.Body.messages | ForEach-Object { $_ })
        $note = $msgs | Where-Object { $_.authorId -eq 'hub' -and $_.body.StartsWith($Prefix) } | Select-Object -First 1
        if ($note) { return $note }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (Test-Path -LiteralPath $ScratchRoot) {
    Write-Error "-ScratchRoot '$ScratchRoot' already exists; this script only ever runs against a fresh root." -ErrorAction Continue
    exit 2
}

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
    Add-Content -Path $log -Value ("Row 44 continue dry run {0} hub={1} root={2} port={3}" -f (Get-Date -Format o), $HubExe, $ScratchRoot, $Port)
    Write-Host "Hub: $HubExe"
    Write-Host "Scratch root: $ScratchRoot"

    New-Item -ItemType Directory -Path $DataDir | Out-Null
    New-Item -ItemType Directory -Path $StubDir | Out-Null

    # The Row 34 stub: a Codex row's CLI is a .cmd shim, and CliResolver wraps that as
    # "cmd.exe /d /c <shim>". Holding the process open for ten minutes means every spawn this script
    # triggers stays in flight (SpawnLimits.Default.Timeout is 5 minutes before the runner would kill
    # the tree on its own) for well longer than this script needs.
    @'
@echo off
ping -n 600 127.0.0.1 >nul
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $StubDir 'codex.cmd') -Encoding ascii

    # Row 28: 'owner' is a host-file row -- seed its plaintext into tokens.json AFTER directory
    # creation and BEFORE the hub's first start.
    $script:PlaintextTokens = Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')
    $ownerAuth = New-ChopBearerHeaders -Token $script:PlaintextTokens.owner

    # Fail fast (Row 40 lesson): a stale process already listening on $Port would either make the hub
    # fail to bind (burning this whole run's timeout waiting on a server that never starts) or, worse,
    # answer /health itself and let every leg run against the wrong process.
    $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $portProbe.Start() } catch { throw "port $Port is already listening; pick a free -Port or stop whatever is using it." }
    finally { $portProbe.Stop() }

    # ---- hub, stripped PATH: stub\ (codex.cmd) plus System32/SystemRoot only, so neither claude.exe
    # nor a real codex is reachable. Restored immediately after Start-Process, in a finally, so the
    # rest of this script keeps git/dotnet. ----
    $savedPath = $env:PATH
    try {
        $env:PATH = "$StubDir;$env:SystemRoot\System32;$env:SystemRoot"
        $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port") -WindowStyle Hidden -PassThru `
            -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    }
    finally { $env:PATH = $savedPath }

    $health = $null
    foreach ($i in 1..40) {
        if ($hub.HasExited) { throw "hub exited early (code $($hub.ExitCode)) while waiting for /health; see $(Join-Path $DataDir 'hub.stderr.log')" }
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    # A health-check failure here is infrastructure, not a leg outcome: it throws immediately rather
    # than becoming another Add-Check, so a broken build fails loudly instead of quietly costing one
    # more FAIL in the tally.
    if ($null -eq $health) { throw "hub on port $Port did not become healthy (pid=$($hub.Id))" }

    # 1. health.ok
    Add-Check -Name 'health.ok' -Passed ($health.ok -eq $true) -Detail "ok=$($health.ok) schema=$($health.schema)"

    # 2. turns.override -- 'turns: 2' among the leading mentions sets the exchange's own budget to 2,
    # not the hub's default of 8; the stub holds gpt-5.6-terra in flight so the poll below sees it.
    $post1 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = 'turns: 2 @gpt-5.6-terra hold this open' }
    $root2 = $post1.Body.id
    $entry2 = Wait-ExchangeState -RootMessageId $root2 -Seconds $TimeoutSeconds -Predicate {
        param($e) $e.budget -eq 2 -and ((@($e.inFlight | ForEach-Object { $_ })) -contains 'gpt-5.6-terra')
    }
    Add-Check -Name 'turns.override' -Passed ($post1.Status -eq 201 -and $null -ne $entry2) `
        -Detail "root=$root2 status=$($post1.Status) budget=$($entry2.budget) inFlight=$(($entry2.inFlight | ForEach-Object { $_ }) -join ',')"

    # 3. turns.range-note -- 99 is outside 1..16: one hub note, and the exchange still opens with the
    # hub's default budget (8), not 99 and not refused outright.
    $post2 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = 'turns: 99 @gpt-5.6-sol hold this open' }
    $root3 = $post2.Body.id
    $note3 = Wait-HubNotePrefix -AfterId $root3 -Prefix 'turns: must be a whole number from 1 to 16; the default 8 applies.' -Seconds $TimeoutSeconds
    $entry3 = Wait-ExchangeState -RootMessageId $root3 -Seconds $TimeoutSeconds -Predicate { param($e) $e.budget -eq 8 }
    Add-Check -Name 'turns.range-note' -Passed ($null -ne $note3 -and $null -ne $entry3) `
        -Detail "root=$root3 note=$($note3.body ?? '<none>') budget=$($entry3.budget)"

    # 4. continue.open-refused -- root2's exchange is still open (gpt-5.6-terra still held in flight
    # by the stub): /continue as a reply to it is refused with the still-open note, and Refused/Pending
    # are untouched, so no new spawn appears anywhere in the room. I-m2 (hub F5): asserted directly --
    # inFlight still gpt-5.6-terra, pending empty, exchanges.Count unchanged -- not a budget number that
    # would hold true even if the refusal had silently re-recorded something.
    $exchangesBeforeLeg4 = (Get-Exchanges).Count
    $post3 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '/continue'; replyToId = $root2 }
    $note4 = Wait-HubNotePrefix -AfterId $post3.Body.id -Prefix "Exchange started at #$root2 is still open with 1 turn(s) left; /continue once it has concluded." -Seconds $TimeoutSeconds
    $stillOpen = Wait-ExchangeState -RootMessageId $root2 -Seconds 3 -Predicate {
        param($e) ((@($e.inFlight | ForEach-Object { $_ })) -contains 'gpt-5.6-terra') -and (@($e.pending | ForEach-Object { $_ })).Count -eq 0
    }
    $exchangesAfterLeg4 = (Get-Exchanges).Count
    Add-Check -Name 'continue.open-refused' -Passed ($post3.Status -eq 201 -and $null -ne $note4 -and $null -ne $stillOpen -and $exchangesAfterLeg4 -eq $exchangesBeforeLeg4) `
        -Detail "note=$($note4.body ?? '<none>') inFlight=$(($stillOpen.inFlight | ForEach-Object { $_ }) -join ',') pending=$(($stillOpen.pending | ForEach-Object { $_ }) -join ',') exchanges=$exchangesBeforeLeg4->$exchangesAfterLeg4"

    # 5. continue.after-stop -- stopping root2's exchange ends the in-flight gpt-5.6-terra spawn and
    # marks the entry continuable; /continue against it then adds the default budget (8) to what was
    # left (2), re-queuing the addressee (gpt-5.6-terra, the only participant this exchange ever had).
    $stopResp = Invoke-Api -Method Post -Path "/api/rooms/general/exchanges/$root2/stop" -Headers $ownerAuth
    $stoppedEntry = Wait-ExchangeState -RootMessageId $root2 -Seconds $TimeoutSeconds -Predicate {
        param($e) $e.status -eq 'stopped' -and (@($e.inFlight | ForEach-Object { $_ })).Count -eq 0
    }
    $continuableAfterStop = $null -ne $stoppedEntry -and $stoppedEntry.continuable -eq $true
    $post4 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '/continue'; replyToId = $root2 }
    $note5 = Wait-HubNotePrefix -AfterId $post4.Body.id -Prefix "Exchange started at #$root2 continued: 8 more turn(s), 10 in all; queued @gpt-5.6-terra." -Seconds $TimeoutSeconds
    $reopened = Wait-ExchangeState -RootMessageId $root2 -Seconds $TimeoutSeconds -Predicate {
        param($e) $e.status -eq 'open' -and $e.budget -eq 10 -and (
            (@($e.pending | ForEach-Object { $_ })) -contains 'gpt-5.6-terra' -or
            (@($e.inFlight | ForEach-Object { $_ })) -contains 'gpt-5.6-terra')
    }
    Add-Check -Name 'continue.after-stop' -Passed ($stopResp.Status -eq 200 -and $continuableAfterStop -and $null -ne $note5 -and $null -ne $reopened) `
        -Detail "stopStatus=$($stopResp.Status) continuable=$continuableAfterStop note=$($note5.body ?? '<none>') status=$($reopened.status) budget=$($reopened.budget)"

    # 6. continue.nothing -- leg 3's range note is a hub note, so it roots no exchange: /continue as a
    # reply to it finds nothing to continue.
    $post5 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '/continue'; replyToId = $note3.id }
    $note6 = Wait-HubNotePrefix -AfterId $post5.Body.id -Prefix "Reply to #$($note3.id): that message is in no exchange this hub still holds, so there is nothing to continue." -Seconds $TimeoutSeconds
    Add-Check -Name 'continue.nothing' -Passed ($post5.Status -eq 201 -and $null -ne $note6) -Detail "replyTo=$($note3.id) note=$($note6.body ?? '<none>')"

    # 7. stop.cleanup -- the room's own stop ends every open exchange and cancels every in-flight
    # spawn in it (SpawnerService.OnStop); then this script's own hub process tree, never a PID it did
    # not start, is ended by PID (never by name) and waited for, so the stub's cmd.exe/ping.exe
    # grandchildren cannot outlive a plain Stop-Process. The scratch root itself is removed once that
    # PID has vanished in the outer finally below, uniformly for every exit path -- not here -- so a
    # throw before this leg ever runs still cleans up.
    $roomStop = Invoke-Api -Method Post -Path '/api/rooms/general/exchange/stop' -Headers $ownerAuth
    $stopOk = $roomStop.Status -eq 200
    $hubPid = $hub.Id
    $killProc = Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T', '/F', '/PID', "$hubPid") -PassThru -Wait -NoNewWindow
    $vanished = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $hubPid -ErrorAction SilentlyContinue)) { $vanished = $true; break }
        Start-Sleep -Milliseconds 300
    }
    Add-Check -Name 'stop.cleanup' -Passed ($stopOk -and $killProc.ExitCode -eq 0 -and $vanished) `
        -Detail "stopStatus=$($roomStop.Status) taskkillExit=$($killProc.ExitCode) vanished=$vanished"
    $hub = $null   # already ended above; the finally block's own kill-and-wait becomes a no-op
}
finally {
    # Only reached on an early throw where the hub had started but leg 7 never ran to end it (health
    # never came up, a leg's own assertion threw): end it by PID, never by name, and wait the same way
    # leg 7 does, so the scratch root below is never removed out from under a still-running hub.
    if ($hub -and -not $hub.HasExited) {
        Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T', '/F', '/PID', "$($hub.Id)") -PassThru -Wait -NoNewWindow -ErrorAction SilentlyContinue | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $hub.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 300 }
    }
    # The scratch root, removed once its hub's PID (if any) has vanished: covers leg 7's clean-run
    # path (the hub is already gone above) and any throw after New-Item created the root, including
    # one before the hub itself ever started (a taken port). Retried: a vanished PID does not mean
    # every handle under the root (log files, the SQLite file) is closed the same instant taskkill
    # returns.
    $removed = $true
    if (Test-Path -LiteralPath $ScratchRoot) {
        $removed = $false
        $lastRemoveError = $null
        for ($attempt = 1; $attempt -le 10 -and -not $removed; $attempt++) {
            try { Remove-Item -LiteralPath $ScratchRoot -Recurse -Force -ErrorAction Stop } catch { $lastRemoveError = $_.Exception.Message }
            $removed = -not (Test-Path -LiteralPath $ScratchRoot)
            if (-not $removed) { Start-Sleep -Milliseconds 300 }
        }
        if (-not $removed) { Write-Warning "could not remove scratch root '$ScratchRoot': $lastRemoveError" }
    }
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS (scratch root removed: $removed)"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -eq 7) { 0 } else { 1 })
