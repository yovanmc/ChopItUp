<#
.SYNOPSIS
    Row 42 dry run: fabricates a v2 corpus, migrates it to v13 through the real built hub, and proves
    the backup, the pre-existing rows staying unmarked, an import of the five-turn AC1 transcript
    stored and flagged with no spawn and no note, a live control post that does move the room, and the
    flags surviving a hub restart. No model CLI is ever reachable while this runs.

.DESCRIPTION
    Mirrors Invoke-Row40MemoryEditCheck.ps1's frame: a param block with -HubExe/-DataDir (fresh, under
    $env:TEMP)/-Port, Add-Check, ChopTokenHelpers.ps1 seeding 'owner' before the hub's first start, the
    hub started by PID and stopped in a finally block, "Results: n/m PASS", exit 0 only when every
    check passes and the total is exactly 8. Every Invoke-RestMethod/Invoke-Api array is piped through
    ForEach-Object { $_ } before Where-Object (LESSONS M10: a bare top-level JSON array comes back as
    one nested Object[]).

    THE FIXTURE is built by the corpus exe itself (--schema-version 2), never a copy of anything real:
    a small v2 corpus (200 messages, 2 rooms). The corpus seeds 'owner', 'claude' and 'codex' (row 42
    claim 18); only 'owner' needs a bearer here, seeded into a fresh tokens.json AFTER the corpus build
    and BEFORE the hub's first start (safe: row 42 claim 28 — the token-seeding helper never opens the
    database, so it cannot race the corpus build).

    WHAT THE LEGS PROVE, SAID PLAINLY: this scratch hub has no skill installed and the corpus's rooms
    have no directory bound, so at HEAD the imported `/build-thing` and `/stop` turns would (if they
    reached the spawner) produce an unknown-skill note, and a run-start here is structurally impossible
    regardless — the run-start and run-stop suppression is proven by task 3's in-process tests, not by
    this script. What DOES bind here is the mention turns: at HEAD an imported `@opus` mention opens an
    exchange and launches a spawn attempt before the stripped PATH makes `CliResolver` throw
    `FileNotFoundException`, publishing a note and moving the exchange — so leg 6 (the barrier) and leg
    7 (the exact-arithmetic negatives) both fail at HEAD on the mention path and pass once the row 42
    guard (`SpawnerService.OnMessage`'s `if (m.Imported) return;`) is in place.

    PATH IS STRIPPED for each hub child (`$env:SystemRoot\System32;$env:SystemRoot` only) so neither
    `claude` nor `codex` can be found: a spawn attempt that escapes the guard shows up as an open
    exchange plus a "could not be started" hub note (SpawnerService.cs's `FileNotFoundException` path),
    never as a real model call. The script's own PATH is restored immediately after each Start-Process
    call, in a finally, so git/dotnet stay available to the rest of the script.

    Never touches C:\Self Apps or any real data directory: -DataDir defaults to a fresh folder under
    $env:TEMP and is left behind with the logs.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$CorpusExe = (Join-Path $PSScriptRoot '..\tools\ChopItUp.Corpus\bin\Debug\net10.0\ChopItUp.Corpus.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_row42dryrun_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8807,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
$script:Checks = New-Object System.Collections.Generic.List[object]
# A trailing separator would place a bare backslash before the closing quote in Start-Process's
# quoted -ArgumentList entry, which the child process's own argv parser reads as an escaped quote
# rather than a path terminator.
$DataDir = $DataDir.TrimEnd('\', '/')
$log = "$DataDir.row42-dryrun.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# One request, uniformly: GET has no -Body; a write carries $ownerAuth. Never throws on a non-2xx
# status (M23's Invoke-WebRequest -SkipHttpErrorCheck idiom) so a refusal is a value to assert on,
# not an exception to catch.
function Invoke-Api {
    param([string]$Method, [string]$Path, [hashtable]$Body, [hashtable]$Headers)
    $params = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = $TimeoutSeconds; SkipHttpErrorCheck = $true }
    if ($Headers) { $params.Headers = $Headers }
    if ($Body) { $params.ContentType = 'application/json'; $params.Body = ($Body | ConvertTo-Json -Depth 6) }
    $raw = Invoke-WebRequest @params
    $parsed = if ($raw.Content) { try { $raw.Content | ConvertFrom-Json } catch { $null } } else { $null }
    return [pscustomobject]@{ Status = [int]$raw.StatusCode; Body = $parsed }
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath $CorpusExe -PathType Leaf)) {
    Write-Error "Corpus exe not found at '$CorpusExe'. Build tools/ChopItUp.Corpus first, or pass -CorpusExe." -ErrorAction Continue
    exit 2
}
if (Test-Path -LiteralPath $DataDir) {
    Write-Error "-DataDir '$DataDir' already exists; this script only ever runs against a fresh directory." -ErrorAction Continue
    exit 2
}
Add-Content -Path $log -Value ("Row 42 dry run {0} hub={1} corpus={2} data={3} port={4}" -f (Get-Date -Format o), $HubExe, $CorpusExe, $DataDir, $Port)
Write-Host "Hub: $HubExe"
Write-Host "Corpus: $CorpusExe"
Write-Host "Data dir: $DataDir"

# The AC1 transcript: a labelled turn (mentions, a run-skill invocation, /stop and a build request),
# five messages, always resolved as the human roster row on import.
$ImportedHistory = "Owner: @opus what do you think of the plan?`nOpus: I think so. @gpt-6-astra, a second opinion?`nOwner: /build-thing @sonnet begin`nOwner: /stop`nOwner: @sonnet build the thing now"

$base = "http://127.0.0.1:$Port"
$hub = $null
$hub2 = $null
try {
    # ---- corpus first: a fresh v2 corpus, fabricated by the shared builder (never a copy of a real
    # database). The corpus builder creates $DataDir itself (CorpusBuilder.Build). ----
    $fp = Join-Path $DataDir 'corpus-fingerprint.json'
    $corpusOutLog = "$DataDir.corpus.out.log"
    $corpusErrLog = "$DataDir.corpus.err.log"
    $corpusArgs = @('--data', "`"$DataDir`"", '--messages', '200', '--rooms', '2', '--leave-in-wal', '20', '--fingerprint-out', "`"$fp`"", '--schema-version', '2')
    $corpusProc = Start-Process -FilePath $CorpusExe -ArgumentList $corpusArgs -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $corpusOutLog -RedirectStandardError $corpusErrLog
    $corpusOk = ($corpusProc.ExitCode -eq 0) -and (Test-Path -LiteralPath $fp)
    Add-Check -Name 'corpus.v2-built' -Passed $corpusOk -Detail "exit=$($corpusProc.ExitCode) fingerprint=$(Test-Path -LiteralPath $fp)"

    # Row 28: 'owner' is a host-file row -- seed its plaintext into tokens.json AFTER the corpus build
    # and BEFORE the hub's first start (ChopTokenHelpers.ps1). Never a real installation's credential.
    $script:PlaintextTokens = Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')
    $ownerAuth = New-ChopBearerHeaders -Token $script:PlaintextTokens.owner

    # Fail fast: a stale process already listening on $Port would either make the hub fail to bind (so
    # the readiness loop below burns its whole timeout waiting on a server that never starts) or, worse,
    # answer /health itself and let every leg run against the wrong process. Bind-and-release is a real
    # listen check, not a connect probe, so it also catches a port held by something that refuses
    # connections but still owns the socket.
    $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $portProbe.Start() } catch { throw "port $Port is already listening; pick a free -Port or stop whatever is using it." }
    finally { $portProbe.Stop() }

    # ---- hub, first start: PATH stripped so neither claude nor codex can be found. Only the child
    # inherits the stripped copy -- restored immediately after Start-Process, in a finally, so the rest
    # of this script keeps git/dotnet. ----
    $savedPath = $env:PATH
    try {
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
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

    # 2. health.schema-is-13
    Add-Check -Name 'health.schema-is-13' -Passed ($health.schema -eq 13) -Detail "schema=$($health.schema)"

    # 3. backup.count-is-one -- exactly one *.bak, named chopitup.db.v2.<stamp>.bak (ChopDb.cs's
    # BackupBeforeMigration: "{DatabasePath}.v{fromVersion}.{stamp}.bak", stamp = yyyyMMdd'T'HHmmss'Z').
    $baks = @(Get-ChildItem -LiteralPath $DataDir -Filter '*.bak' -File -ErrorAction SilentlyContinue)
    $bakName = if ($baks.Count -ge 1) { $baks[0].Name } else { '' }
    $bakNameOk = $bakName -match '^chopitup\.db\.v2\.\d{8}T\d{6}Z\.bak$'
    Add-Check -Name 'backup.count-is-one' -Passed ($baks.Count -eq 1 -and $bakNameOk) -Detail "count=$($baks.Count) name=$bakName"

    # 4. existing.not-imported -- the first corpus room, every pre-existing row imported == false.
    $roomsResp = Invoke-Api -Method Get -Path '/api/rooms'
    $rooms = @($roomsResp.Body | ForEach-Object { $_ })
    $room = $rooms[0].id
    $existingResp = Invoke-Api -Method Get -Path "/api/rooms/$room/messages?afterId=0&limit=200"
    $existingMessages = @($existingResp.Body.messages | ForEach-Object { $_ })
    $totalBefore = $existingMessages.Count
    $hubNotesBefore = @($existingMessages | Where-Object { $_.authorId -eq 'hub' }).Count
    $anyImportedBefore = @($existingMessages | Where-Object { $_.imported -eq $true }).Count
    Add-Check -Name 'existing.not-imported' -Passed ($roomsResp.Status -eq 200 -and $existingResp.Status -eq 200 -and $totalBefore -gt 0 -and $anyImportedBefore -eq 0) `
        -Detail "room=$room totalBefore=$totalBefore hubNotesBefore=$hubNotesBefore anyImportedBefore=$anyImportedBefore"

    # 5. import.201-five-flagged
    $importResp = Invoke-Api -Method Post -Path "/api/rooms/$room/import" -Headers $ownerAuth -Body @{ text = $ImportedHistory }
    $importedMsgs = @($importResp.Body.messages | ForEach-Object { $_ })
    $notFlagged = @($importedMsgs | Where-Object { $_.imported -ne $true }).Count
    $lastImportedId = if ($importedMsgs.Count -gt 0) { ($importedMsgs | Sort-Object id -Descending | Select-Object -First 1).id } else { -1 }
    Add-Check -Name 'import.201-five-flagged' -Passed ($importResp.Status -eq 201 -and $importedMsgs.Count -eq 5 -and $notFlagged -eq 0) `
        -Detail "status=$($importResp.Status) count=$($importedMsgs.Count) notFlagged=$notFlagged lastImportedId=$lastImportedId"

    # 6. control.live-mention-moves-the-room -- THE BARRIER. A live mention posted after the import: its
    # spec reaching the exchange, or a "could not be started" note (PATH is stripped), proves the FIFO
    # loop already drained the five imported PostedEvents ahead of it (row 42 claim 26).
    $controlResp = Invoke-Api -Method Post -Path "/api/rooms/$room/messages" -Headers $ownerAuth -Body @{ body = '@opus control post' }
    $controlId = $controlResp.Body.id
    $deadline = (Get-Date).AddSeconds(15)
    $controlOk = $false
    $exchangeRootSeen = $null
    $controlNoteFound = $false
    while ((Get-Date) -lt $deadline) {
        $exResp = Invoke-Api -Method Get -Path "/api/rooms/$room/exchange"
        if ($exResp.Status -eq 200 -and $exResp.Body.rootMessageId -eq $controlId) { $controlOk = $true; $exchangeRootSeen = $exResp.Body.rootMessageId; break }
        $sinceResp = Invoke-Api -Method Get -Path "/api/rooms/$room/messages?afterId=$lastImportedId&limit=50"
        $sinceMessages = @($sinceResp.Body.messages | ForEach-Object { $_ })
        if (@($sinceMessages | Where-Object { $_.authorId -eq 'hub' -and $_.body -like '*@opus could not be started*' }).Count -gt 0) {
            $controlOk = $true; $controlNoteFound = $true; break
        }
        Start-Sleep -Milliseconds 500
    }
    Add-Check -Name 'control.live-mention-moves-the-room' -Passed ($controlOk -and $controlId -eq ($lastImportedId + 1)) `
        -Detail "controlId=$controlId lastImportedId=$lastImportedId exchangeRootSeen=$exchangeRootSeen noteFound=$controlNoteFound"

    # 7. import.no-spawn-no-note -- THE NEGATIVES, exact arithmetic, asserted only after the barrier.
    $runResp = Invoke-Api -Method Get -Path "/api/rooms/$room/run"
    $runIs204 = $runResp.Status -eq 204
    $afterResp = Invoke-Api -Method Get -Path "/api/rooms/$room/messages?afterId=0&limit=300"
    $allAfter = @($afterResp.Body.messages | ForEach-Object { $_ })
    $hubNotesAfter = @($allAfter | Where-Object { $_.authorId -eq 'hub' })
    $noteCountSinceControl = $hubNotesAfter.Count - $hubNotesBefore
    $badNotes = @($hubNotesAfter | Where-Object { $_.body -match '/build-thing' -or $_.body -match 'No skill named' -or $_.body -match 'Steer noted' -or $_.body -match 'Run #' })
    $totalExpected = $totalBefore + 5 + 1 + $noteCountSinceControl
    $totalOk = $allAfter.Count -eq $totalExpected
    $exResp2 = Invoke-Api -Method Get -Path "/api/rooms/$room/exchange"
    $exchangeRootOk = $true
    if ($exResp2.Status -eq 200 -and $exResp2.Body.rootMessageId) { $exchangeRootOk = $exResp2.Body.rootMessageId -gt $lastImportedId }
    Add-Check -Name 'import.no-spawn-no-note' -Passed ($runIs204 -and $badNotes.Count -eq 0 -and ($noteCountSinceControl -eq 0 -or $noteCountSinceControl -eq 1) -and $totalOk -and $exchangeRootOk) `
        -Detail "run204=$runIs204 badNotes=$($badNotes.Count) noteCountSinceControl=$noteCountSinceControl total=$($allAfter.Count) expected=$totalExpected exchangeRoot=$($exResp2.Body.rootMessageId)"

    # 8. restart.flags-persist -- stop, restart (PATH stripped again, hub2.* logs so the first start's
    # migration record is not truncated), the imported ids still true and the corpus rows still false.
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue; $hub.WaitForExit(10000) | Out-Null }
    Start-Sleep -Milliseconds 300

    $savedPath2 = $env:PATH
    try {
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
        $hub2 = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port") -WindowStyle Hidden -PassThru `
            -RedirectStandardError (Join-Path $DataDir 'hub2.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub2.stdout.log')
    }
    finally { $env:PATH = $savedPath2 }

    $health2 = $null
    foreach ($i in 1..40) {
        if ($hub2.HasExited) { throw "hub2 exited early (code $($hub2.ExitCode)) while waiting for /health; see $(Join-Path $DataDir 'hub2.stderr.log')" }
        try { $health2 = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $health2) { throw "hub2 on port $Port did not become healthy (pid=$($hub2.Id))" }

    $finalResp = Invoke-Api -Method Get -Path "/api/rooms/$room/messages?afterId=0&limit=300"
    $allFinal = @($finalResp.Body.messages | ForEach-Object { $_ })
    $importedIds = @($importedMsgs | ForEach-Object { $_.id })
    $corpusIds = @($existingMessages | ForEach-Object { $_.id })
    $importedRowsFinal = @($allFinal | Where-Object { $_.id -in $importedIds })
    $importedStillTrue = ($importedRowsFinal.Count -eq $importedIds.Count) -and (@($importedRowsFinal | Where-Object { $_.imported -ne $true }).Count -eq 0)
    $corpusRowsFinal = @($allFinal | Where-Object { $_.id -in $corpusIds })
    $corpusStillFalse = ($corpusRowsFinal.Count -eq $corpusIds.Count) -and (@($corpusRowsFinal | Where-Object { $_.imported -ne $false }).Count -eq 0)
    Add-Check -Name 'restart.flags-persist' -Passed ($health2.schema -eq 13 -and $importedStillTrue -and $corpusStillFalse) `
        -Detail "schema2=$($health2.schema) importedStillTrue=$importedStillTrue corpusStillFalse=$corpusStillFalse"
}
finally {
    if ($hub2 -and -not $hub2.HasExited) { Stop-Process -Id $hub2.Id -Force -ErrorAction SilentlyContinue }
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }   # idempotent after leg 8's own stop
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -eq 8) { 0 } else { 1 })
