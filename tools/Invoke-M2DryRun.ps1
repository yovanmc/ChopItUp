<#
.SYNOPSIS
    M2 self-check: fabricates a realistic v1 ChopItUp corpus, runs the real built hub against it,
    and proves the backup, the migration, the conversation and the emitted host configs all came
    out right. Fabricated data only — never a copy of anything real.

.DESCRIPTION
    See docs/superpowers/plans/m2-host-wiring.md, "Task 6 — Synthetic-corpus dry run (HIGH gate)".
    This is the milestone's self-check harness as well as its acceptance proof: M2 has no deploy
    step of its own.

    Never `dotnet run`s the hub: that executes it as a CHILD of the SDK driver, so the PID this
    script would hold is not the hub, and killing it does not reliably kill the hub. Instead this
    builds the solution once and Start-Process'es the built ChopItUp.Hub.exe directly, keeping the
    returned Process object so it can later be stopped by id — after confirming its image path is
    really the exe this script launched — and never by name.

.PARAMETER KeepEvidence
    Keep the scratch directory (corpus database, backup, host-configs, logs) instead of deleting it
    at the end. Prints the directory path either way it exits.
#>
[CmdletBinding()]
param(
    [switch]$KeepEvidence
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot 'ChopItUp.slnx'
$corpusExe = Join-Path $repoRoot 'tools\ChopItUp.Corpus\bin\Debug\net10.0\ChopItUp.Corpus.exe'
$hubExe = Join-Path $repoRoot 'src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'

# --- Scratch directory: a GUID nonce under $env:TEMP, never .data, never C:\Self Apps -------------
$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_dryrun_$nonce"
New-Item -ItemType Directory -Path $scratch | Out-Null
$dataDir = Join-Path $scratch 'data'
$logPath = Join-Path $scratch 'dry-run.log'

# --- Evidence log: one PASS/FAIL line per check, plus the measured value --------------------------
$checkLines = New-Object System.Collections.Generic.List[string]
$failCount = 0

function Add-Check {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Passed, [string]$Detail = '')
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    $line = "$status  $Name  $Detail".TrimEnd()
    $checkLines.Add($line)
    if (-not $Passed) { $script:failCount++ }
    Write-Host $line
}

# Tokens are captured into variables below but MUST NEVER be interpolated into Add-Check details,
# Write-Host, or thrown exception messages — this is what "no token value appears in the script's
# own output" means for this script, and knownTokens gives that an automated check at the end
# rather than relying on every call site remembering the rule.
$knownTokens = New-Object System.Collections.Generic.List[string]

$hubProcess = $null
$hubProcessVerifiedPath = $null
$exitCode = 1

try {
    # --- Step: build once (the CI/repo strictness gate) -------------------------------------------
    Write-Host "Building $slnPath (Debug, -warnaserror)..."
    & dotnet build $slnPath -c Debug -warnaserror -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path $corpusExe)) { throw "Expected corpus builder exe not found at '$corpusExe' after build." }
    if (-not (Test-Path $hubExe)) { throw "Expected hub exe not found at '$hubExe' after build." }

    # --- Step 2: seed the corpus via the shared builder --------------------------------------------
    $beforeFingerprintPath = Join-Path $scratch 'before.json'
    $corpusOutLog = Join-Path $scratch 'corpus.out.log'
    $corpusErrLog = Join-Path $scratch 'corpus.err.log'
    $corpusArgs = @(
        '--data', $dataDir,
        '--messages', '10000',
        '--rooms', '3',
        '--leave-in-wal', '500',
        '--fingerprint-out', $beforeFingerprintPath
    )
    $corpusProc = Start-Process -FilePath $corpusExe -ArgumentList $corpusArgs -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $corpusOutLog -RedirectStandardError $corpusErrLog
    if ($corpusProc.ExitCode -ne 0) {
        throw "Corpus builder exited $($corpusProc.ExitCode); see $corpusErrLog"
    }
    if (-not (Test-Path $beforeFingerprintPath)) { throw "Corpus builder did not write a fingerprint to '$beforeFingerprintPath'." }
    $beforeFingerprint = Get-Content $beforeFingerprintPath -Raw
    Add-Check -Name 'corpus.seeded' -Passed $true -Detail "10000 messages, 3 rooms, 500 left in the WAL, at $dataDir"

    # --- Step 4: run the REAL hub, launched directly (never dotnet run) ---------------------------
    $hubOutLog = Join-Path $scratch 'hub.out.log'
    $hubErrLog = Join-Path $scratch 'hub.err.log'
    $hubProcess = Start-Process -FilePath $hubExe -ArgumentList @('--data', $dataDir, '--port', '0') -PassThru -NoNewWindow `
        -RedirectStandardOutput $hubOutLog -RedirectStandardError $hubErrLog

    $hubExeFull = (Resolve-Path $hubExe).Path
    $confirmed = Get-Process -Id $hubProcess.Id -ErrorAction Stop
    if ($confirmed.Path.ToLowerInvariant() -ne $hubExeFull.ToLowerInvariant()) {
        throw "Process $($hubProcess.Id) image path '$($confirmed.Path)' does not match the hub exe this script launched ('$hubExeFull'); refusing to treat it as ours."
    }
    $hubProcessVerifiedPath = $confirmed.Path

    # Wait for the hub to record the port it actually bound (--port 0 = ephemeral).
    $hubPortFile = Join-Path $dataDir 'hub.port'
    $port = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($hubProcess.HasExited) { throw "Hub exited early (code $($hubProcess.ExitCode)) before recording hub.port; see $hubErrLog" }
        if (Test-Path $hubPortFile) {
            $raw = (Get-Content $hubPortFile -Raw).Trim()
            if ($raw -match '^\d+$') { $port = [int]$raw; break }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $port) { throw "Hub did not record hub.port within 30s; see $hubOutLog / $hubErrLog" }

    # Wait for readiness by polling /health.
    $healthUrl = "http://127.0.0.1:$port/health"
    $health = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($hubProcess.HasExited) { throw "Hub exited early (code $($hubProcess.ExitCode)) while waiting for /health; see $hubErrLog" }
        try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $health) { throw "Hub /health did not respond within 30s at $healthUrl." }
    Add-Check -Name 'hub.launched-directly-not-dotnet-run' -Passed $true -Detail "pid=$($hubProcess.Id) port=$port"

    # --- Step 5: assertions ------------------------------------------------------------------------

    # /health reports schema 2.
    Add-Check -Name 'health.schema' -Passed ($health.schema -eq 2) -Detail "schema=$($health.schema)"

    # Exactly one .bak exists, sound, correctly versioned, and its fingerprint (including the 500
    # WAL-only rows) equals the pre-migration fingerprint.
    $bakFiles = @(Get-ChildItem -Path $dataDir -Filter '*.bak' -File)
    Add-Check -Name 'backup.count-is-one' -Passed ($bakFiles.Count -eq 1) -Detail "count=$($bakFiles.Count)"
    if ($bakFiles.Count -eq 1) {
        $bakPath = $bakFiles[0].FullName
        $bakInspectPath = Join-Path $scratch 'backup-inspect.json'
        $inspectOutLog = Join-Path $scratch 'inspect-backup.out.log'
        $inspectErrLog = Join-Path $scratch 'inspect-backup.err.log'
        $inspectProc = Start-Process -FilePath $corpusExe -ArgumentList @('--inspect', '--db', $bakPath, '--fingerprint-out', $bakInspectPath) `
            -PassThru -Wait -NoNewWindow -RedirectStandardOutput $inspectOutLog -RedirectStandardError $inspectErrLog
        if ($inspectProc.ExitCode -ne 0) { throw "Inspecting the backup failed (exit $($inspectProc.ExitCode)); see $inspectErrLog" }
        $bakInspect = Get-Content $bakInspectPath -Raw | ConvertFrom-Json

        Add-Check -Name 'backup.quick-check-ok' -Passed ($bakInspect.quick_check -eq 'ok') -Detail "quick_check=$($bakInspect.quick_check)"
        Add-Check -Name 'backup.stamped-v1' -Passed ($bakInspect.user_version -eq 1) -Detail "user_version=$($bakInspect.user_version)"

        $bakFingerprintJson = ($bakInspect.fingerprint | ConvertTo-Json -Depth 10 -Compress)
        $beforeFingerprintCompact = ($beforeFingerprint | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress)
        Add-Check -Name 'backup.fingerprint-matches-pre-migration' -Passed ($bakFingerprintJson -eq $beforeFingerprintCompact) `
            -Detail "count=$($bakInspect.fingerprint.Count)"
    }
    else {
        Add-Check -Name 'backup.quick-check-ok' -Passed $false -Detail 'skipped: backup.count-is-one failed'
        Add-Check -Name 'backup.stamped-v1' -Passed $false -Detail 'skipped: backup.count-is-one failed'
        Add-Check -Name 'backup.fingerprint-matches-pre-migration' -Passed $false -Detail 'skipped: backup.count-is-one failed'
    }

    # The migrated database's fingerprint (nothing lost, nothing added) and every cursor.
    $migratedDbPath = Join-Path $dataDir 'chopitup.db'
    $migratedInspectPath = Join-Path $scratch 'migrated-inspect.json'
    $migratedOutLog = Join-Path $scratch 'inspect-migrated.out.log'
    $migratedErrLog = Join-Path $scratch 'inspect-migrated.err.log'
    $migratedProc = Start-Process -FilePath $corpusExe -ArgumentList @('--inspect', '--db', $migratedDbPath, '--fingerprint-out', $migratedInspectPath) `
        -PassThru -Wait -NoNewWindow -RedirectStandardOutput $migratedOutLog -RedirectStandardError $migratedErrLog
    if ($migratedProc.ExitCode -ne 0) { throw "Inspecting the migrated database failed (exit $($migratedProc.ExitCode)); see $migratedErrLog" }
    $migratedInspect = Get-Content $migratedInspectPath -Raw | ConvertFrom-Json
    $migratedFingerprintJson = ($migratedInspect.fingerprint | ConvertTo-Json -Depth 10 -Compress)
    $beforeFingerprintCompact = ($beforeFingerprint | ConvertFrom-Json | ConvertTo-Json -Depth 10 -Compress)
    Add-Check -Name 'migrated.fingerprint-matches-pre-migration' -Passed ($migratedFingerprintJson -eq $beforeFingerprintCompact) `
        -Detail "count=$($migratedInspect.fingerprint.Count)"
    # Cursors are part of the fingerprint dumped above; call it out as its own line since it is its
    # own acceptance bullet ("cursors survived"), not just an implicit part of the count match.
    $beforeCursorsJson = (($beforeFingerprint | ConvertFrom-Json).Cursors | ConvertTo-Json -Depth 10 -Compress)
    $migratedCursorsJson = ($migratedInspect.fingerprint.Cursors | ConvertTo-Json -Depth 10 -Compress)
    Add-Check -Name 'migrated.cursors-survived' -Passed ($beforeCursorsJson -eq $migratedCursorsJson) -Detail ''

    # A message can be posted and re-posted with the same retry key for one stored message, over the
    # real MCP transport; list_rooms reflects the new total.
    $tokensPath = Join-Path $dataDir 'tokens.json'
    $tokensBeforeRotate = Get-Content $tokensPath -Raw
    $tokens = $tokensBeforeRotate | ConvertFrom-Json
    $knownTokens.Add($tokens.owner)
    $knownTokens.Add($tokens.claude)
    $knownTokens.Add($tokens.codex)

    $mcpOutPath = Join-Path $scratch 'mcp-check.json'
    $mcpOutLog = Join-Path $scratch 'mcp-check.out.log'
    $mcpErrLog = Join-Path $scratch 'mcp-check.err.log'
    $mcpClientKey = [guid]::NewGuid().ToString('N')
    $env:CHOPITUP_MCP_TOKEN = $tokens.claude
    try {
        $mcpProc = Start-Process -FilePath $corpusExe -ArgumentList @(
            '--mcp-check',
            '--url', "http://127.0.0.1:$port/",
            '--room', 'general',
            '--body', 'Dry-run smoke test message (fabricated, no real conversation).',
            '--client-key', $mcpClientKey,
            '--out', $mcpOutPath
        ) -PassThru -Wait -NoNewWindow -RedirectStandardOutput $mcpOutLog -RedirectStandardError $mcpErrLog
    }
    finally {
        Remove-Item Env:\CHOPITUP_MCP_TOKEN -ErrorAction SilentlyContinue
    }
    if ($mcpProc.ExitCode -ne 0) { throw "MCP check failed (exit $($mcpProc.ExitCode)); see $mcpErrLog" }
    $mcpResult = Get-Content $mcpOutPath -Raw | ConvertFrom-Json

    Add-Check -Name 'mcp.post-message-appends-10001' -Passed ($mcpResult.first_id -eq 10001 -and -not $mcpResult.first_deduplicated) `
        -Detail "first_id=$($mcpResult.first_id) first_deduplicated=$($mcpResult.first_deduplicated)"
    Add-Check -Name 'mcp.repeat-with-same-key-deduplicates' -Passed ($mcpResult.second_id -eq $mcpResult.first_id -and $mcpResult.second_deduplicated) `
        -Detail "second_id=$($mcpResult.second_id) second_deduplicated=$($mcpResult.second_deduplicated)"
    Add-Check -Name 'mcp.list-rooms-shows-10001' -Passed ($mcpResult.total_message_count -eq 10001) -Detail "total_message_count=$($mcpResult.total_message_count)"

    # --print-config: three files, and the emitted URL carries the port the script actually started
    # the hub on (the MINOR-13 check — without this the config assertion cannot fail).
    $printOutLog = Join-Path $scratch 'print-config.out.log'
    $printErrLog = Join-Path $scratch 'print-config.err.log'
    $printProc = Start-Process -FilePath $hubExe -ArgumentList @('--data', $dataDir, '--print-config') -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $printOutLog -RedirectStandardError $printErrLog
    $hostConfigsDir = Join-Path $dataDir 'host-configs'
    $expectedFiles = @('claude-desktop.json', 'codex-config.toml', 'README.md')
    $filesPresent = $true
    foreach ($f in $expectedFiles) { if (-not (Test-Path (Join-Path $hostConfigsDir $f))) { $filesPresent = $false } }
    Add-Check -Name 'print-config.exit-zero' -Passed ($printProc.ExitCode -eq 0) -Detail "exit=$($printProc.ExitCode)"
    Add-Check -Name 'print-config.three-files-written' -Passed $filesPresent -Detail "dir=$hostConfigsDir"

    $expectedUrl = "http://127.0.0.1:$port/mcp"
    $urlMatches = $true
    if ($filesPresent) {
        foreach ($f in $expectedFiles) {
            $text = Get-Content (Join-Path $hostConfigsDir $f) -Raw
            if ($text -notlike "*$expectedUrl*") { $urlMatches = $false }
        }
    }
    else {
        $urlMatches = $false
    }
    Add-Check -Name 'print-config.url-carries-actual-port' -Passed $urlMatches -Detail "expected substring: $expectedUrl"

    # No token value in the three emitted files' bytes leaking into what THIS script reads back for
    # the URL check above is fine (that is the files' own job, covered by Task 5's tests); this
    # script's own obligation is that no token appears in ITS OWN output, checked below.

    # --rotate-token against the STILL-RUNNING hub exits 5 and leaves tokens.json unchanged.
    $rotateOutLog = Join-Path $scratch 'rotate.out.log'
    $rotateErrLog = Join-Path $scratch 'rotate.err.log'
    $rotateProc = Start-Process -FilePath $hubExe -ArgumentList @('--data', $dataDir, '--rotate-token', 'claude') -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $rotateOutLog -RedirectStandardError $rotateErrLog
    $tokensAfterRotate = Get-Content $tokensPath -Raw
    Add-Check -Name 'rotate-token.refused-while-hub-running' -Passed ($rotateProc.ExitCode -eq 5) -Detail "exit=$($rotateProc.ExitCode)"
    Add-Check -Name 'rotate-token.tokens-unchanged' -Passed ($tokensAfterRotate -eq $tokensBeforeRotate) -Detail ''

    # --- Step 6: stop only the process this script started, by id, after confirming its identity ---
    Write-Host "Stopping hub pid $($hubProcess.Id) ($hubProcessVerifiedPath)..."
    Stop-Process -Id $hubProcess.Id
    $hubProcess.WaitForExit(15000) | Out-Null
    if (-not $hubProcess.HasExited) { throw "Hub process $($hubProcess.Id) did not exit within 15s of Stop-Process." }
    Add-Check -Name 'hub.stopped-by-id-after-path-check' -Passed $true -Detail "pid=$($hubProcess.Id)"

    # Prove the directory lock is actually free by opening it exclusively.
    $lockPath = Join-Path $dataDir 'hub.lock'
    $lockFreed = $false
    for ($i = 0; $i -lt 20 -and -not $lockFreed; $i++) {
        try {
            $fs = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Close()
            $lockFreed = $true
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    Add-Check -Name 'hub.lock-file-released' -Passed $lockFreed -Detail "path=$lockPath"
    $hubProcess = $null   # stopped cleanly; nothing left for the finally block to do

    # --- No-token-leak self-check: scan everything this script itself printed/logged -------------
    $allOutputText = ($checkLines -join "`n")
    $leaked = $false
    foreach ($t in $knownTokens) {
        if ($t -and $allOutputText.Contains($t)) { $leaked = $true }
    }
    Add-Check -Name 'no-token-leaked-into-script-output' -Passed (-not $leaked) -Detail ''

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail $_.Exception.Message
    $exitCode = 1
}
finally {
    # Defensive stop: only reached if an earlier step threw before the normal stop-by-id step ran.
    if ($null -ne $hubProcess) {
        try {
            if (-not $hubProcess.HasExited) {
                $stillMatches = $false
                try {
                    $recheck = Get-Process -Id $hubProcess.Id -ErrorAction Stop
                    $stillMatches = $hubExeFull -and ($recheck.Path.ToLowerInvariant() -eq $hubExeFull.ToLowerInvariant())
                }
                catch { $stillMatches = $false }
                if ($stillMatches) {
                    Write-Host "Cleanup: stopping hub pid $($hubProcess.Id) after an earlier failure..."
                    Stop-Process -Id $hubProcess.Id -Force
                }
            }
        }
        catch { }
    }

    $checkLines | Set-Content -Path $logPath -Encoding utf8

    $passCount = ($checkLines | Where-Object { $_.StartsWith('PASS') }).Count
    $totalCount = $checkLines.Count
    Write-Host ""
    Write-Host "Dry run log: $logPath"
    Write-Host "Results: $passCount/$totalCount PASS"

    if ($KeepEvidence) {
        Write-Host "Evidence kept at: $scratch"
    }
    else {
        try { Remove-Item -Path $scratch -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$scratch': $($_.Exception.Message)" }
    }
}

exit $exitCode
