<#
.SYNOPSIS
    Row 28 Task 8 (issues/08-dry-run.md, required at HIGH): fabricates a real pre-row-28 installation
    -- an OLD-shape plaintext tokens.json for the full seeded roster, plus host-config files carrying
    live tokens the way a real `--print-config` used to write them -- and runs the REAL, freshly
    published ChopItUp.Hub.exe against it, end to end. Unit tests prove pieces; this proves the
    composition, including the one branch (an unrewritable host-config file) no unit test or other
    live check exercises (pass 2 finding 12).

.DESCRIPTION
    Never touches `C:\Self Apps` or any real installation -- everything lives under a fresh
    $env:TEMP scratch directory with a GUID nonce, deleted at the end unless -KeepEvidence is passed.
    A guard at the top refuses to run if -PublishDir or the data directory it is about to create
    resolves under `C:\Self Apps`.

    Sequence:
      0. `dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal` -- the repo's own strictness
         gate, same as Invoke-M25DryRun.ps1's "Step 0" -- then `dotnet publish` the hub (Release; the
         csproj already sets RuntimeIdentifier/SelfContained/PublishSingleFile, so a plain publish
         produces the same single-file exe Deploy-ChopItUp.ps1 ships) into a scratch publish
         directory, unless -PublishDir was given (a pre-built publish output to reuse, the same seam
         Invoke-M4SelfCheck.ps1's -PublishDir is).
      1. Fabricate the pre-row-28 install: ChopTokenHelpers.ps1's Initialize-ChopScratchTokens seeds
         tokens.json as a flat plaintext map (participant id -> token string) for the FULL roster --
         every id in ChopDb.SeedRoster, including 'hub' (system) and every spawnable row -- which is
         exactly what the OLD TokenStore.Load(dataDir, IReadOnlyList<string> participantIds) wrote
         before this row split credentials into two classes. Three of those plaintext values (claude,
         codex, owner-remote) are then hand-embedded into fabricated host-config files in the same
         shapes HostConfigs.cs's ClaudeDesktop/Codex/SpawnCommands.ClaudeMcpConfigJson emit today,
         except carrying a live token instead of today's {{TOKEN}} placeholder -- exactly what a real
         pre-Task-3 `--print-config` run left on disk. One of those files (claude-desktop.json) is
         then opened with FileShare.None and held open by this script itself for the rest of the
         first hub run, reproducing "a generated file cannot be rewritten" (a deny-write ACL would
         work too, per the ticket, but a held handle needs no elevated rights and is exercised
         identically from HostConfigs.SweepLiveTokens's point of view: its own File.ReadAllText throws
         IOException the same way either failure would).
      2. Start the real published exe once (port 0, so HubHost's own ApplicationStarted handler
         records the bound port to hub.port -- same polling helpers as Invoke-M4SelfCheck.ps1).
         Assert: /health answers 200 despite the held-open file; the pre-migration owner token (never
         touched by anything after step 1) still yields 201 on POST /api/rooms/general/messages --
         AC3/the ticket's own words for "this is the check that matters most", an upgrade must never
         lock the owner out; tokens.json is now the hashed {"id":{"sha256":"..."}} shape for every
         host-file row (owner, claude, codex, owner-remote) with the correct digest
         (ChopTokenHelpers.ps1's Get-ChopTokenSha256, the one hash implementation every tools/ script
         shares) and holds NO entry at all for any spawnable or system row (TokenStore.Load's Kind ==
         "system"/IsSpawnable branches never write one back) and NO plaintext string anywhere in the
         file; codex-config.toml and claude-code-owner-remote.json (not held open) were rewritten to
         the {{TOKEN}} placeholder; claude-desktop.json (held open) still carries its live token
         because the rewrite failed, and a message naming its path landed in 'general' via
         HubHost.Build's own error-reporting path.
      3. Release the held-open file, stop the hub by the PID this script started (after confirming its
         image path, never Stop-Process -Name), snapshot tokens.json's SHA-256, and start it again.
         Assert: tokens.json is byte-identical to the snapshot (a second start re-migrates nothing);
         the SAME pre-migration owner token still authenticates; claude-desktop.json -- now
         reachable, since the lock is gone -- gets swept on THIS start, proving the failure in step 2
         was the held-open handle and nothing else.
      4. Stop the hub. Run `--print-config` (exit 0, host-config files regenerated, still only
         placeholders, nothing printed to stdout matches any live token) and `--rotate-token owner`
         (exit 0, prints a new value once, and tokens.json's owner entry hash changes to match it) --
         both against the now-migrated directory, with the hub stopped as both commands require.

    Never mints a real installation's credential, never reads or writes under any `C:\Self Apps\...`
    path, and never leaves a live token in a file this script does not itself delete: every plaintext
    value lives only in local script variables and the scratch data dir removed at the end (unless
    -KeepEvidence, in which case the caller owns disposing of it -- the same trade Invoke-M4SelfCheck.
    ps1's -KeepEvidence makes).

.PARAMETER PublishDir
    A pre-built publish output to reuse instead of publishing fresh (saves the Release publish cost on
    repeat runs while iterating on this script). Must already contain ChopItUp.Hub.exe. Refused if it
    resolves under `C:\Self Apps`.

.PARAMETER KeepEvidence
    Keep the scratch directory (fixture data dir, hub stdout/stderr logs, the fresh publish output
    when this script did its own publish) instead of deleting it at the end. Prints the directory
    either way.
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [switch]$KeepEvidence
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot 'ChopItUp.slnx'
$hubProj = Join-Path $repoRoot 'src\ChopItUp.Hub\ChopItUp.Hub.csproj'

function Test-UnderSelfApps([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    return $full.StartsWith('C:\Self Apps', [StringComparison]::OrdinalIgnoreCase)
}

if ($PublishDir -and (Test-UnderSelfApps $PublishDir)) {
    throw "Refusing to run: -PublishDir '$PublishDir' resolves under 'C:\Self Apps'. This script only ever runs against scratch directories it creates itself."
}

$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_row28dryrun_$nonce"
if (Test-UnderSelfApps $scratch) { throw "Refusing to run: scratch path '$scratch' resolves under 'C:\Self Apps'." }
New-Item -ItemType Directory -Path $scratch | Out-Null

$dataDir = Join-Path $scratch 'data'
$roomsRoot = Join-Path $scratch 'rooms-root'   # a SIBLING of $dataDir -- RoomPathRules refuses a room directory nested inside the data dir (M25SkillProposalCheck lesson)
$hostConfigsDir = Join-Path $dataDir 'host-configs'
$ownPublish = [string]::IsNullOrWhiteSpace($PublishDir)
if ($ownPublish) { $PublishDir = Join-Path $scratch 'publish' }
$exePath = Join-Path $PublishDir 'ChopItUp.Hub.exe'

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

function Wait-ForHubFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][System.Diagnostics.Process]$HubProcess,
        [Parameter(Mandatory)][string]$What,
        [int]$TimeoutSeconds = 30
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($HubProcess.HasExited) { throw "Hub exited early (code $($HubProcess.ExitCode)) before writing $What." }
        if (Test-Path -LiteralPath $Path) { return }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting $TimeoutSeconds`s for $What at '$Path'."
}

function Wait-ForProcessPath {
    param([Parameter(Mandatory)][int]$ProcessId, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $p -and -not [string]::IsNullOrEmpty($p.Path)) { return $p }
        Start-Sleep -Milliseconds 100
    }
    throw "Could not read a non-empty image path for PID $ProcessId within $TimeoutSeconds`s."
}

function Stop-VerifiedHub {
    param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [Parameter(Mandatory)][string]$ExpectedImagePath)
    if ($Process.HasExited) { return }
    $confirm = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    if ($null -eq $confirm -or [string]::IsNullOrEmpty($confirm.Path) -or $confirm.Path.ToLowerInvariant() -ne $ExpectedImagePath.ToLowerInvariant()) {
        throw "Refusing to stop PID $($Process.Id): its image path no longer matches '$ExpectedImagePath'. Not touching it."
    }
    Stop-Process -Id $Process.Id
    $Process.WaitForExit(15000) | Out-Null
    if (-not $Process.HasExited) { throw "Process $($Process.Id) did not exit within 15s of Stop-Process." }
}

function Start-Row28Hub {
    param([Parameter(Mandatory)][string]$Label, [int]$TimeoutSeconds = 30)
    $hubPortFile = Join-Path $dataDir 'hub.port'
    if (Test-Path -LiteralPath $hubPortFile) { Remove-Item -LiteralPath $hubPortFile -Force }   # never trust the "last-running" port
    $outLog = Join-Path $scratch "hub_$Label.out.log"
    $errLog = Join-Path $scratch "hub_$Label.err.log"
    $proc = Start-Process -FilePath $exePath -ArgumentList @('--data', "`"$dataDir`"", '--port', '0', '--rooms-root', "`"$roomsRoot`"") `
        -PassThru -NoNewWindow -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    $confirmed = Wait-ForProcessPath -ProcessId $proc.Id
    $exeFull = (Resolve-Path -LiteralPath $exePath).Path
    if ($confirmed.Path.ToLowerInvariant() -ne $exeFull.ToLowerInvariant()) {
        throw "Process $($proc.Id) image path '$($confirmed.Path)' does not match the hub exe this script launched ('$exeFull'); refusing to treat it as ours."
    }
    Wait-ForHubFile -Path $hubPortFile -HubProcess $proc -What "hub.port ($Label)" -TimeoutSeconds $TimeoutSeconds
    $portRaw = (Get-Content -LiteralPath $hubPortFile -Raw).Trim()
    if ($portRaw -notmatch '^\d+$') { throw "hub.port ($Label) contained non-numeric content: '$portRaw'" }
    $port = [int]$portRaw
    $base = "http://127.0.0.1:$port"
    $health = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "Hub ($Label) exited early (code $($proc.ExitCode)) while waiting for /health; see $errLog" }
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 5; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $health) { throw "Hub ($Label) /health did not respond within ${TimeoutSeconds}s at $base/health." }
    return [pscustomobject]@{ Process = $proc; Port = $port; Base = $base; Health = $health; ExeFull = $exeFull; OutLog = $outLog; ErrLog = $errLog }
}

function Get-FileSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

# The full seeded roster (ChopDb.SeedRoster ids, verbatim) -- the OLD TokenStore.Load wrote a
# plaintext entry for every one of these, 'hub' (system) and every spawnable row included, because it
# took a flat list of ids with no notion of kind at all.
$fullRoster = @(
    'owner', 'claude', 'codex', 'opus', 'sonnet', 'fable',
    'gpt-6-astra', 'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna', 'gpt-5.5', 'gpt-5.4-mini',
    'hub', 'owner-remote'
)
$hostFileRows = @('owner', 'claude', 'codex', 'owner-remote')
$nonHostFileRows = $fullRoster | Where-Object { $hostFileRows -notcontains $_ }

$lockedStream = $null
$hub1 = $null
$hub2 = $null
$exitCode = 1

try {
    # === Step 0: the repo's own strictness gate, then publish the real exe ==========================
    Write-Host "Building $slnPath (Debug, -warnaserror)..."
    & dotnet build $slnPath -c Debug -warnaserror -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

    if ($ownPublish) {
        Write-Host "Publishing $hubProj (Release) to '$PublishDir'..."
        & dotnet publish $hubProj -c Release -o $PublishDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    }
    else {
        Write-Host "Reusing existing publish output at '$PublishDir' (-PublishDir given)."
    }
    Add-Check -Name 'publish.exe-exists' -Passed (Test-Path -LiteralPath $exePath -PathType Leaf) -Detail $exePath
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "Cannot continue: '$exePath' does not exist." }

    # === Step 1: fabricate the pre-row-28 install ====================================================
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    New-Item -ItemType Directory -Path $hostConfigsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $roomsRoot -Force | Out-Null

    $seeded = Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds $fullRoster
    Add-Check -Name 'fixture.tokens-json-old-shape-seeded' -Passed ($seeded.Count -eq $fullRoster.Count) -Detail "$($seeded.Count) participants, plaintext, including 'hub' and every spawnable row"

    $ownerToken = $seeded['owner']
    $claudeToken = $seeded['claude']
    $codexToken = $seeded['codex']
    $ownerRemoteToken = $seeded['owner-remote']

    $mcpUrl = 'http://127.0.0.1:8790/mcp'   # cosmetic only in these fixtures -- nothing below asserts on the port text
    $claudeDesktopPath = Join-Path $hostConfigsDir 'claude-desktop.json'
    $codexConfigPath = Join-Path $hostConfigsDir 'codex-config.toml'
    $ownerRemotePath = Join-Path $hostConfigsDir 'claude-code-owner-remote.json'
    $readmePath = Join-Path $hostConfigsDir 'README.md'

    # Same shape HostConfigs.ClaudeDesktop emits today, except a LIVE token where {{TOKEN}} now goes --
    # exactly what a pre-Task-3 `--print-config` left on disk.
    $claudeDesktopJson = @{
        mcpServers = @{
            chopitup = @{
                command = 'cmd'
                args = @('/c', 'npx', '-y', 'mcp-remote@0.8.3', $mcpUrl, '--allow-http', '--transport', 'http-only', '--header', 'Authorization:${CHOPITUP_TOKEN}')
                env = @{ CHOPITUP_TOKEN = "Bearer $claudeToken" }
            }
        }
    } | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($claudeDesktopPath, $claudeDesktopJson, (New-Object System.Text.UTF8Encoding($false)))

    $codexConfigToml = @"
[mcp_servers.chopitup]
url = "$mcpUrl"
http_headers = { Authorization = "Bearer $codexToken" }
startup_timeout_sec = 20
tool_timeout_sec = 60
"@
    [System.IO.File]::WriteAllText($codexConfigPath, $codexConfigToml, (New-Object System.Text.UTF8Encoding($false)))

    # Same shape SpawnCommands.ClaudeMcpConfigJson emits for a direct-dial Claude Code MCP entry.
    $ownerRemoteJson = @{
        mcpServers = @{
            chopitup = @{
                type    = 'http'
                url     = $mcpUrl
                headers = @{ Authorization = "Bearer $ownerRemoteToken" }
            }
        }
    } | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($ownerRemotePath, $ownerRemoteJson, (New-Object System.Text.UTF8Encoding($false)))

    [System.IO.File]::WriteAllText($readmePath, "# Host configs for Chop It Up`n`nFabricated pre-row-28 README for this dry run. Never a token.`n", (New-Object System.Text.UTF8Encoding($false)))

    Add-Check -Name 'fixture.host-configs-with-live-tokens-seeded' -Passed $true -Detail $hostConfigsDir
    Add-Check -Name 'fixture.claude-desktop-json-carries-live-token-before-sweep' -Passed ((Get-Content -LiteralPath $claudeDesktopPath -Raw).Contains($claudeToken)) -Detail $claudeDesktopPath

    # Hold claude-desktop.json open with FileShare.None: SweepLiveTokens' own File.ReadAllText will
    # throw IOException on this file the moment the hub tries to sweep it, the same failure shape a
    # deny-write ACL or another process's open handle would produce -- reproducing "a generated file
    # cannot be rewritten" (ticket 08, AC4's second half) without needing elevated rights.
    $lockedStream = [System.IO.File]::Open($claudeDesktopPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
    Add-Check -Name 'fixture.claude-desktop-json-held-open-exclusively' -Passed $true -Detail 'FileShare.None, held by this script'

    # === Step 2: start the real published exe once ===================================================
    $hub1 = Start-Row28Hub -Label 'run1'
    Add-Check -Name 'run1.hub-started' -Passed $true -Detail "pid=$($hub1.Process.Id) port=$($hub1.Port)"
    Add-Check -Name 'run1.health-200-despite-unwritable-file' -Passed ($hub1.Health.ok -eq $true) -Detail "health=$($hub1.Health | ConvertTo-Json -Compress)"

    # The acceptance criterion that matters most: the plaintext seeded BEFORE this hub ever started
    # still authenticates an owner-attributed write after the migration -- an upgrade must never lock
    # the owner out of their own install.
    $ownerAuth = New-ChopBearerHeaders -Token $ownerToken
    $postRun1 = Invoke-WebRequest -Uri "$($hub1.Base)/api/rooms/general/messages" -Method Post -Headers $ownerAuth -ContentType 'application/json' `
        -Body (@{ body = "row28 dry run ($nonce) run1 message, pre-migration owner token" } | ConvertTo-Json) -TimeoutSec 15 -SkipHttpErrorCheck
    Add-Check -Name 'run1.pre-migration-owner-token-posts-201' -Passed ($postRun1.StatusCode -eq 201) -Detail "status=$($postRun1.StatusCode)"

    $tokensJsonPath = Join-Path $dataDir 'tokens.json'
    $tokensAfterRun1Raw = Get-Content -LiteralPath $tokensJsonPath -Raw
    $tokensAfterRun1 = $tokensAfterRun1Raw | ConvertFrom-Json
    $presentNames = @($tokensAfterRun1.PSObject.Properties.Name)

    $hashedShapeOk = $true
    $hashesCorrect = $true
    foreach ($id in $hostFileRows) {
        $entry = $tokensAfterRun1.$id
        if ($null -eq $entry -or $null -eq $entry.sha256 -or $entry.sha256 -notmatch '^[0-9a-f]{64}$') { $hashedShapeOk = $false; continue }
        if ($entry.sha256 -ne (Get-ChopTokenSha256 -Plaintext $seeded[$id])) { $hashesCorrect = $false }
    }
    Add-Check -Name 'run1.tokens-json-hashed-shape-for-host-file-rows' -Passed $hashedShapeOk -Detail "present=[$($presentNames -join ', ')]"
    Add-Check -Name 'run1.tokens-json-hashes-match-seeded-plaintext' -Passed $hashesCorrect -Detail 'sha256(seeded plaintext) == stored hash for owner/claude/codex/owner-remote'

    $noStrayEntries = -not ($nonHostFileRows | Where-Object { $presentNames -contains $_ })
    Add-Check -Name 'run1.tokens-json-holds-no-entry-for-spawnable-or-system-rows' -Passed $noStrayEntries -Detail "checked absent: [$($nonHostFileRows -join ', ')]"

    $anyPlaintextLeaked = $false
    foreach ($id in $fullRoster) { if ($tokensAfterRun1Raw.Contains($seeded[$id])) { $anyPlaintextLeaked = $true } }
    Add-Check -Name 'run1.tokens-json-no-plaintext-remains' -Passed (-not $anyPlaintextLeaked) -Detail 'none of the 14 seeded plaintext values appear in tokens.json'

    $codexContent = Get-Content -LiteralPath $codexConfigPath -Raw
    Add-Check -Name 'run1.codex-config-rewritten-to-placeholder' -Passed ($codexContent.Contains('{{TOKEN}}') -and -not $codexContent.Contains($codexToken)) -Detail $codexConfigPath
    $ownerRemoteContent = Get-Content -LiteralPath $ownerRemotePath -Raw
    Add-Check -Name 'run1.owner-remote-config-rewritten-to-placeholder' -Passed ($ownerRemoteContent.Contains('{{TOKEN}}') -and -not $ownerRemoteContent.Contains($ownerRemoteToken)) -Detail $ownerRemotePath

    # Read through the SAME handle this script is holding open (FileShare.None means even this
    # process's own Get-Content -- a second, independent open of the path -- would fail with a
    # sharing violation) rather than opening the path again.
    $lockedStream.Position = 0
    $lockedReader = New-Object System.IO.StreamReader($lockedStream, [System.Text.Encoding]::UTF8, $false, 1024, $true)
    $claudeDesktopContentRun1 = $lockedReader.ReadToEnd()
    $lockedReader.Dispose()
    Add-Check -Name 'run1.locked-file-still-carries-live-token' -Passed ($claudeDesktopContentRun1.Contains($claudeToken)) -Detail 'rewrite failed while this script held it open, exactly as expected'

    $errText = Get-Content -LiteralPath $hub1.ErrLog -Raw -ErrorAction SilentlyContinue
    Add-Check -Name 'run1.unwritable-file-reported-on-stderr' -Passed ($null -ne $errText -and $errText.Contains('Could not remove the live token') -and $errText.Contains($claudeDesktopPath)) -Detail $hub1.ErrLog

    $messagesRun1 = Invoke-RestMethod -Uri "$($hub1.Base)/api/rooms/general/messages" -TimeoutSec 15
    $roomNote = $messagesRun1.messages | ForEach-Object { $_ } | Where-Object { $_.body -and $_.body.Contains($claudeDesktopPath) }
    Add-Check -Name 'run1.room-note-names-unwritable-file' -Passed ($null -ne $roomNote) -Detail "found=$([bool]$roomNote)"

    # === Step 3: release the lock, restart, prove idempotence + stability ===========================
    $lockedStream.Dispose()
    $lockedStream = $null
    Add-Check -Name 'fixture.locked-file-released' -Passed $true -Detail $claudeDesktopPath

    $tokensBytes1Hash = Get-FileSha256 $tokensJsonPath

    Write-Host "Stopping hub pid $($hub1.Process.Id) (run1)..."
    Stop-VerifiedHub -Process $hub1.Process -ExpectedImagePath $hub1.ExeFull
    Add-Check -Name 'run1.stopped-by-id-after-path-check' -Passed $true -Detail "pid=$($hub1.Process.Id)"

    $hub2 = Start-Row28Hub -Label 'run2'
    Add-Check -Name 'run2.hub-restarted' -Passed $true -Detail "pid=$($hub2.Process.Id) port=$($hub2.Port)"

    $tokensBytes2Hash = Get-FileSha256 $tokensJsonPath
    Add-Check -Name 'run2.tokens-json-byte-identical-to-run1' -Passed ($tokensBytes1Hash -eq $tokensBytes2Hash) -Detail "run1=$tokensBytes1Hash run2=$tokensBytes2Hash"

    $postRun2 = Invoke-WebRequest -Uri "$($hub2.Base)/api/rooms/general/messages" -Method Post -Headers $ownerAuth -ContentType 'application/json' `
        -Body (@{ body = "row28 dry run ($nonce) run2 message, same pre-migration owner token after a restart" } | ConvertTo-Json) -TimeoutSec 15 -SkipHttpErrorCheck
    Add-Check -Name 'run2.pre-migration-owner-token-still-posts-201-after-restart' -Passed ($postRun2.StatusCode -eq 201) -Detail "status=$($postRun2.StatusCode)"

    # The file that failed to rewrite in run1 is no longer held open -- this start's own sweep should
    # now succeed, proving the earlier failure was the held-open handle and nothing else in the sweep
    # logic.
    $claudeDesktopContentRun2 = Get-Content -LiteralPath $claudeDesktopPath -Raw
    Add-Check -Name 'run2.previously-unwritable-file-now-swept' -Passed ($claudeDesktopContentRun2.Contains('{{TOKEN}}') -and -not $claudeDesktopContentRun2.Contains($claudeToken)) -Detail $claudeDesktopPath

    Write-Host "Stopping hub pid $($hub2.Process.Id) (run2)..."
    Stop-VerifiedHub -Process $hub2.Process -ExpectedImagePath $hub2.ExeFull
    Add-Check -Name 'run2.stopped-by-id-after-path-check' -Passed $true -Detail "pid=$($hub2.Process.Id)"
    $hub2 = $null

    # === Step 4: --print-config / --rotate-token against the migrated directory, hub stopped ========
    $printConfigOut = Join-Path $scratch 'print-config.out.log'
    $printConfigErr = Join-Path $scratch 'print-config.err.log'
    $printConfigProc = Start-Process -FilePath $exePath -ArgumentList @('--data', "`"$dataDir`"", '--print-config') `
        -PassThru -NoNewWindow -Wait -RedirectStandardOutput $printConfigOut -RedirectStandardError $printConfigErr
    Add-Check -Name 'printconfig.exit-0' -Passed ($printConfigProc.ExitCode -eq 0) -Detail "exit=$($printConfigProc.ExitCode)"

    $printConfigOutText = Get-Content -LiteralPath $printConfigOut -Raw -ErrorAction SilentlyContinue
    $anyLiveTokenInPrintConfigOutput = $false
    foreach ($id in $fullRoster) { if ($printConfigOutText -and $printConfigOutText.Contains($seeded[$id])) { $anyLiveTokenInPrintConfigOutput = $true } }
    Add-Check -Name 'printconfig.stdout-carries-no-live-token' -Passed (-not $anyLiveTokenInPrintConfigOutput) -Detail $printConfigOut

    $codexAfterPrintConfig = Get-Content -LiteralPath $codexConfigPath -Raw
    $ownerRemoteAfterPrintConfig = Get-Content -LiteralPath $ownerRemotePath -Raw
    $claudeDesktopAfterPrintConfig = Get-Content -LiteralPath $claudeDesktopPath -Raw
    $allPlaceholdersOnly = $codexAfterPrintConfig.Contains('{{TOKEN}}') -and $ownerRemoteAfterPrintConfig.Contains('{{TOKEN}}') -and $claudeDesktopAfterPrintConfig.Contains('{{TOKEN}}') `
        -and -not ($codexAfterPrintConfig.Contains($codexToken) -or $ownerRemoteAfterPrintConfig.Contains($ownerRemoteToken) -or $claudeDesktopAfterPrintConfig.Contains($claudeToken))
    Add-Check -Name 'printconfig.regenerated-files-carry-only-placeholders' -Passed $allPlaceholdersOnly -Detail $hostConfigsDir

    $rotateOut = Join-Path $scratch 'rotate-token.out.log'
    $rotateErr = Join-Path $scratch 'rotate-token.err.log'
    $rotateProc = Start-Process -FilePath $exePath -ArgumentList @('--data', "`"$dataDir`"", '--rotate-token', 'owner') `
        -PassThru -NoNewWindow -Wait -RedirectStandardOutput $rotateOut -RedirectStandardError $rotateErr
    Add-Check -Name 'rotate.exit-0' -Passed ($rotateProc.ExitCode -eq 0) -Detail "exit=$($rotateProc.ExitCode)"

    $rotateOutLines = Get-Content -LiteralPath $rotateOut -ErrorAction SilentlyContinue
    # HostCommands.RotateToken prints "New token for 'owner':" then the value on the next line.
    $newOwnerToken = $null
    for ($i = 0; $i -lt $rotateOutLines.Count - 1; $i++) {
        if ($rotateOutLines[$i] -like "New token for*") { $newOwnerToken = $rotateOutLines[$i + 1].Trim(); break }
    }
    Add-Check -Name 'rotate.printed-a-new-value-once' -Passed ($null -ne $newOwnerToken -and $newOwnerToken.Length -eq 43 -and $newOwnerToken -ne $ownerToken) -Detail "length=$(if ($newOwnerToken) { $newOwnerToken.Length } else { 0 })"

    $tokensAfterRotate = (Get-Content -LiteralPath $tokensJsonPath -Raw) | ConvertFrom-Json
    $rotatedHashMatches = $null -ne $newOwnerToken -and $tokensAfterRotate.owner.sha256 -eq (Get-ChopTokenSha256 -Plaintext $newOwnerToken)
    $oldHashGone = $tokensAfterRotate.owner.sha256 -ne (Get-ChopTokenSha256 -Plaintext $ownerToken)
    Add-Check -Name 'rotate.tokens-json-owner-hash-updated-to-new-value' -Passed ($rotatedHashMatches -and $oldHashGone) -Detail 'sha256(new) stored; sha256(old) no longer stored'

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())]"
    $exitCode = 1
}
finally {
    if ($null -ne $lockedStream) { try { $lockedStream.Dispose() } catch { } }

    foreach ($hub in @($hub1, $hub2)) {
        if ($null -eq $hub) { continue }
        try {
            if (-not $hub.Process.HasExited) {
                $stillMatches = $false
                try {
                    $recheck = Get-Process -Id $hub.Process.Id -ErrorAction Stop
                    $stillMatches = $hub.ExeFull -and ($recheck.Path.ToLowerInvariant() -eq $hub.ExeFull.ToLowerInvariant())
                }
                catch { $stillMatches = $false }
                if ($stillMatches) {
                    Write-Host "Cleanup: stopping hub pid $($hub.Process.Id) after an earlier failure..."
                    Stop-Process -Id $hub.Process.Id -Force -ErrorAction SilentlyContinue
                    $hub.Process.WaitForExit(15000) | Out-Null
                }
            }
        }
        catch { }
    }

    $logPath = Join-Path $env:TEMP "chopitup_row28dryrun_$nonce.log"
    $checkLines | Set-Content -Path $logPath -Encoding utf8

    $passCount = ($checkLines | Where-Object { $_.StartsWith('PASS') }).Count
    $totalCount = $checkLines.Count
    Write-Host ""
    Write-Host "Row 28 dry run log: $logPath"
    Write-Host "Results: $passCount/$totalCount PASS"

    if ($KeepEvidence) {
        Write-Host "Evidence kept at: $scratch"
    }
    else {
        try { Remove-Item -Path $scratch -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$scratch': $($_.Exception.Message)" }
    }
}

exit $exitCode
