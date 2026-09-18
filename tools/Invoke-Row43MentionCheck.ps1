<#
.SYNOPSIS
    Row 43 dry run: proves the leading-mention rule (D5) end to end against the built hub, with a
    stub Codex CLI that holds every spawn open and no real model ever reachable.

.DESCRIPTION
    Mirrors Invoke-Row42ImportCheck.ps1's frame: a param block with -HubExe/-DataDir (fresh, under
    $env:TEMP)/-Port/-TimeoutSeconds, Add-Check, ChopTokenHelpers.ps1 seeding 'owner' before the hub's
    first start, the hub started by PID and stopped in a finally block, "Results: n/m PASS", exit 0
    only when every check passes and the total is exactly 7. Every array read off Invoke-RestMethod/
    Invoke-Api is piped through ForEach-Object { $_ } before Where-Object (LESSONS M10: a bare
    top-level JSON array comes back as one nested Object[]).

    PATH for the child hub is a scratch 'stub\' directory holding codex.cmd (the Row 34 shape:
    @echo off / ping -n 600 127.0.0.1 >nul / exit /b 0) plus $env:SystemRoot\System32 and
    $env:SystemRoot only, so neither a real claude.exe nor a real codex.exe/codex.cmd is reachable
    (CliResolver takes name.exe anywhere on PATH before a shim, then wraps a .cmd as
    "cmd.exe /d /c <shim>"). Every participant this script mentions (gpt-6-astra, gpt-5.6-sol,
    gpt-5.6-terra, gpt-5.6-luna) is a Codex-hosted row in ChopDb.SeedRoster, so the stub answers every
    spawn attempt the legs below can trigger and holds each open for ten minutes -- ample for a script
    that finishes in well under a minute and explicitly stops everything in leg 7. The script's own
    PATH is saved before Start-Process and restored in a finally, so git/dotnet stay available
    afterward regardless of how the run ends.

    THE SEVEN LEGS, in order, each a barrier on a hub note or on the exchange snapshot's own state,
    never a bare timer (Row 42 lesson): health.ok (a plain /health poll); leading.opens-exchange (an
    owner post whose body starts with @gpt-5.6-terra roots an exchange with gpt-5.6-terra in flight);
    inline.no-exchange-reference-note (a post with no leading recipient and an inline
    @gpt-5.6-sol draws the "Nobody was addressed" note and roots no exchange); unknown.note (a leading
    word nobody answers to draws "No participant named @nobody." naming the addressable roster and
    roots no exchange); bracket.no-exchange (a bracketed prefix is prose, same shape as the inline
    leg, for @gpt-5.6-luna); slash.dispatches (importing a one-line skill first, then invoking it with
    a leading @gpt-6-astra roots an exchange and draws the skill's in-force note); stop.cleanup (the
    room's own stop endpoint, then this script's own hub process tree ended by PID, never by name).

    Because leg 2 leaves gpt-5.6-terra's exchange open for the rest of the run, every "no exchange
    rooted here" assertion below reads the snapshot's exchanges[] array for a match on that message's
    own id, never the top-level fields (which the newest open exchange, not necessarily this message,
    still owns).

    NEGATIVE LEG (AC7, M24 lesson: a guard only binds something once its mechanism has been reverted
    and seen to fail). With Mentions.Leading temporarily rewritten, right after its empty-body guard,
    to `return new LeadingMentions(Find(body), []);` -- i.e. every roster id anywhere in the body
    counts as a recipient and no leading word is ever unknown -- rebuilt, and this script re-run
    against the same stub PATH:

        measured 2026-09-18, reverted build, 4/7 PASS. FAILED: inline.no-exchange-reference-note
        (an exchange rooted at the "please ask @gpt-5.6-sol something" post: reverted Leading treats
        the inline @gpt-5.6-sol as a recipient, so it spawns instead of drawing a reference note),
        bracket.no-exchange (an exchange rooted at the "[trial] @gpt-5.6-luna hi" post for the same
        reason), unknown.note (the "No participant named @nobody." note never appears: reverted
        Leading returns an empty Unknown list unconditionally, since Find only ever reports roster
        ids it recognises). health.ok, leading.opens-exchange, slash.dispatches and stop.cleanup kept
        passing: every mention those three legs use is already a leading one, so Find(body) agrees
        with the real Leading() there regardless of which implementation is running.

    Restored, rebuilt, re-run: 7/7 PASS (measured 2026-09-18).

    Never touches C:\Self Apps or any real data directory: -DataDir defaults to a fresh folder under
    $env:TEMP and is removed by leg 7 on a clean run; a thrown run leaves it behind next to the log
    for inspection.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_row43dryrun_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8808,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
$script:Checks = New-Object System.Collections.Generic.List[object]
# A trailing separator would place a bare backslash before the closing quote in Start-Process's
# quoted -ArgumentList entry, which the child process's own argv parser reads as an escaped quote
# rather than a path terminator (same trap Invoke-Row42ImportCheck.ps1 guards against).
$DataDir = $DataDir.TrimEnd('\', '/')
$StubDir = "$DataDir.stub"
$SkillSourceDir = Join-Path "$DataDir.skillsrc" 'row43check'
$log = "$DataDir.row43-dryrun.log"

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

# The room's own exchange snapshot: exchanges[] (never the top-level fields -- leg 2 leaves its own
# exchange open for the rest of the run, so the newest-open top-level view is not necessarily the
# message a later leg is asking about).
function Get-Exchanges {
    $resp = Invoke-Api -Method Get -Path '/api/rooms/general/exchange'
    return @($resp.Body.exchanges | ForEach-Object { $_ })
}

function Test-ExchangeRootedWithInFlight {
    param([long]$MessageId, [string]$ParticipantId, [int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $hit = Get-Exchanges | Where-Object {
            $_.rootMessageId -eq $MessageId -and ((@($_.inFlight | ForEach-Object { $_ })) -contains $ParticipantId)
        }
        if ($hit) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Test-NoExchangeRootedHere {
    param([long]$MessageId)
    return -not (Get-Exchanges | Where-Object { $_.rootMessageId -eq $MessageId })
}

function Test-ParticipantNeverInFlight {
    param([string]$ParticipantId)
    return -not (Get-Exchanges | Where-Object { (@($_.inFlight | ForEach-Object { $_ })) -contains $ParticipantId })
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
if ((Test-Path -LiteralPath $DataDir) -or (Test-Path -LiteralPath $StubDir)) {
    Write-Error "-DataDir '$DataDir' (or its stub sibling) already exists; this script only ever runs against fresh directories." -ErrorAction Continue
    exit 2
}
Add-Content -Path $log -Value ("Row 43 mention dry run {0} hub={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)
Write-Host "Hub: $HubExe"
Write-Host "Data dir: $DataDir"

New-Item -ItemType Directory -Path $DataDir | Out-Null
New-Item -ItemType Directory -Path $StubDir | Out-Null
New-Item -ItemType Directory -Path $SkillSourceDir | Out-Null

# The Row 34 stub: a Codex row's CLI is a .cmd shim, and CliResolver wraps that as
# "cmd.exe /d /c <shim>". Holding the process open for ten minutes means every spawn this script
# triggers stays in flight (SpawnLimits.Default.Timeout is 5 minutes before the runner would kill the
# tree on its own) for well longer than this script needs.
@'
@echo off
ping -n 600 127.0.0.1 >nul
exit /b 0
'@ | Set-Content -LiteralPath (Join-Path $StubDir 'codex.cmd') -Encoding ascii

# A minimal one-line skill: no `run: true`, so invoking it needs no directory bound to the room.
@'
---
name: row43check
description: Row 43 dry run fixture; never installed anywhere but this script's own scratch data dir.
---
# row43check

Say a short acknowledgement, then stop.
'@ | Set-Content -LiteralPath (Join-Path $SkillSourceDir 'SKILL.md') -Encoding utf8

$importOut = Join-Path "$DataDir.skillsrc" 'import.out.log'
$importErr = Join-Path "$DataDir.skillsrc" 'import.err.log'
$importProc = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--import-skill', "`"$SkillSourceDir`"") -PassThru -Wait -NoNewWindow `
    -RedirectStandardOutput $importOut -RedirectStandardError $importErr
if ($importProc.ExitCode -ne 0) { throw "skill import failed (exit=$($importProc.ExitCode)); see $importErr" }

# Row 28: 'owner' is a host-file row -- seed its plaintext into tokens.json AFTER the skill import
# (which never touches tokens.json) and BEFORE the hub's first start.
$script:PlaintextTokens = Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')
$ownerAuth = New-ChopBearerHeaders -Token $script:PlaintextTokens.owner

# Fail fast (Row 40 lesson): a stale process already listening on $Port would either make the hub
# fail to bind (burning this whole run's timeout waiting on a server that never starts) or, worse,
# answer /health itself and let every leg run against the wrong process.
$portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try { $portProbe.Start() } catch { throw "port $Port is already listening; pick a free -Port or stop whatever is using it." }
finally { $portProbe.Stop() }

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
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

    # 2. leading.opens-exchange -- a leading mention roots an exchange with that participant in flight.
    $post1 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '@gpt-5.6-terra hello' }
    $id1 = $post1.Body.id
    $rooted1 = Test-ExchangeRootedWithInFlight -MessageId $id1 -ParticipantId 'gpt-5.6-terra' -Seconds $TimeoutSeconds
    Add-Check -Name 'leading.opens-exchange' -Passed ($post1.Status -eq 201 -and $rooted1) -Detail "id=$id1 status=$($post1.Status)"

    # 3. inline.no-exchange-reference-note -- no leading recipient, one inline spawnable id: a
    # reference note, no exchange, and the referenced id never appears in-flight anywhere.
    $post2 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = 'please ask @gpt-5.6-sol something' }
    $id2 = $post2.Body.id
    $note2 = Wait-HubNotePrefix -AfterId $id2 -Prefix 'Nobody was addressed: @gpt-5.6-sol' -Seconds $TimeoutSeconds
    $noExchange2 = Test-NoExchangeRootedHere -MessageId $id2
    $neverInFlight2 = Test-ParticipantNeverInFlight -ParticipantId 'gpt-5.6-sol'
    Add-Check -Name 'inline.no-exchange-reference-note' -Passed ($null -ne $note2 -and $noExchange2 -and $neverInFlight2) `
        -Detail "id=$id2 note=$($note2.body ?? '<none>') noExchange=$noExchange2 neverInFlight=$neverInFlight2"

    # 4. unknown.note -- a leading word nobody answers to draws its own note naming the addressable
    # roster, and roots no exchange (there is no other leading recipient in this body to accept).
    $post3 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '@nobody hello' }
    $id3 = $post3.Body.id
    $note3 = Wait-HubNotePrefix -AfterId $id3 -Prefix 'No participant named @nobody.' -Seconds $TimeoutSeconds
    $addressableOk = $null -ne $note3 -and $note3.body.Contains('@gpt-5.6-terra')
    $noExchange3 = Test-NoExchangeRootedHere -MessageId $id3
    Add-Check -Name 'unknown.note' -Passed ($null -ne $note3 -and $addressableOk -and $noExchange3) `
        -Detail "id=$id3 note=$($note3.body ?? '<none>') noExchange=$noExchange3"

    # 5. bracket.no-exchange -- a bracketed prefix is prose, not a recognised command prefix, so the
    # whole body is past the recipient region: same shape as leg 3, for a different participant.
    $post4 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '[trial] @gpt-5.6-luna hi' }
    $id4 = $post4.Body.id
    $note4 = Wait-HubNotePrefix -AfterId $id4 -Prefix 'Nobody was addressed: @gpt-5.6-luna' -Seconds $TimeoutSeconds
    $noExchange4 = Test-NoExchangeRootedHere -MessageId $id4
    $neverInFlight4 = Test-ParticipantNeverInFlight -ParticipantId 'gpt-5.6-luna'
    Add-Check -Name 'bracket.no-exchange' -Passed ($null -ne $note4 -and $noExchange4 -and $neverInFlight4) `
        -Detail "id=$id4 note=$($note4.body ?? '<none>') noExchange=$noExchange4 neverInFlight=$neverInFlight4"

    # 6. slash.dispatches -- a /skill invocation with a leading mention still dispatches: the in-force
    # note lands and the exchange roots with the mentioned participant in flight.
    $post5 = Invoke-Api -Method Post -Path '/api/rooms/general/messages' -Headers $ownerAuth -Body @{ body = '/row43check @gpt-6-astra go' }
    $id5 = $post5.Body.id
    $note5 = Wait-HubNotePrefix -AfterId $id5 -Prefix 'Skill /row43check is in force for this exchange' -Seconds $TimeoutSeconds
    $rooted5 = Test-ExchangeRootedWithInFlight -MessageId $id5 -ParticipantId 'gpt-6-astra' -Seconds $TimeoutSeconds
    Add-Check -Name 'slash.dispatches' -Passed ($null -ne $note5 -and $rooted5) -Detail "id=$id5 note=$($note5.body ?? '<none>') rooted=$rooted5"

    # 7. stop.cleanup -- the room's own stop ends every open exchange and cancels every in-flight
    # spawn in it (SpawnerService.OnStop); then this script's own hub process tree, never a PID it did
    # not start, is ended so the stub's cmd.exe/ping.exe grandchildren cannot outlive a plain
    # Stop-Process and keep handles under $DataDir.
    $stopResp = Invoke-Api -Method Post -Path '/api/rooms/general/exchange/stop' -Headers $ownerAuth
    $stopOk = $stopResp.Status -eq 200
    $hubPid = $hub.Id
    $killProc = Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T', '/F', '/PID', "$hubPid") -PassThru -Wait -NoNewWindow
    $vanished = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $hubPid -ErrorAction SilentlyContinue)) { $vanished = $true; break }
        Start-Sleep -Milliseconds 300
    }
    # A vanished PID does not mean every handle under $DataDir (the hub's own log files, the SQLite
    # file) is closed the same instant taskkill returns -- retry the delete a few times rather than
    # treating one race as a real cleanup failure.
    $removed = $false
    $lastRemoveError = $null
    if ($vanished) {
        for ($attempt = 1; $attempt -le 10 -and -not $removed; $attempt++) {
            try { Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction Stop } catch { $lastRemoveError = $_.Exception.Message }
            $removed = -not (Test-Path -LiteralPath $DataDir)
            if (-not $removed) { Start-Sleep -Milliseconds 300 }
        }
    }
    Add-Check -Name 'stop.cleanup' -Passed ($stopOk -and $killProc.ExitCode -eq 0 -and $vanished -and $removed) `
        -Detail "stopStatus=$($stopResp.Status) taskkillExit=$($killProc.ExitCode) vanished=$vanished dataDirRemoved=$removed lastRemoveError=$lastRemoveError"
    $hub = $null   # already ended above; the finally block's own Stop-Process becomes a no-op
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }   # only reached on an early throw
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -eq 7) { 0 } else { 1 })
