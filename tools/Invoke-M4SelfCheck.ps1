<#
.SYNOPSIS
    M4 self-check (HIGH-tier gate): proves the *published* ChopItUp.Hub exe -- not the build output --
    finds its data beside itself, serves the chat UI, speaks the MCP protocol, and survives a restart;
    then, given a real deploy target, proves what actually landed there matches staging byte-for-byte.

.DESCRIPTION
    See docs/superpowers/plans/m4-release.md, "Task 3 -- tools/Invoke-M4SelfCheck.ps1", and
    .scratch/m4-release/issues/03-self-check.md. Two stages:

      Stage 1 (always runs, needs -PublishDir): robocopy -PublishDir to a scratch location under
      $env:TEMP with a GUID nonce -- with `/E /XD data`, never a plain recursive copy, because a
      plain copy pointed at a real install would drag the owner's actual chopitup.db into %TEMP%,
      which is a real-database copy and is banned outright. Refuses to start if a `data\` folder
      already exists in that scratch copy before the first launch: C2's whole claim is that the
      database is *created* beside the exe by this run, and a pre-existing folder would let the
      check pass without proving anything. Then:
        C1 -- the published layout is exactly what release publish is supposed to produce.
        C2 -- launched with no --data and --port 0, from a *different* working directory than the
              exe's own, with CHOPITUP_DATA/CHOPITUP_PORT cleared from the child environment, the
              database appears beside the exe (AppContext.BaseDirectory) and nowhere in the working
              directory (ContentRoot). The different working directory matters because ContentRoot
              *is* the working directory -- a same-directory launch would never notice future drift
              from BaseDirectory to a ContentRoot-relative path.
        C3 -- /health reports schema 2; an MCP post_message (via `tools/ChopItUp.Corpus --mcp-check`,
              never a hand-rolled transport) lands and reads back, a repeat with the same client_key
              deduplicates; the web shell is fetched AND the script it references is fetched too and
              asserted to be real JavaScript, not the shell again -- see
              src/ChopItUp.Hub/Web/SpaFiles.cs: SpaFiles.MapFallback serves index.html with a 200 for
              *any* unreserved path, so a missing wwwroot\assets\ would make the script URL return the
              HTML shell and a tag-grep would pass over a blank page.
        Restart -- stop the hub by the PID this script started (after confirming its image path),
              WaitForExit (the hub lock is FileShare.None), delete hub.port (HubPortFile never
              deletes it and its own doc comment calls it "the running OR LAST-RUNNING" port -- M2
              precedent: tools/Invoke-M2DryRun.ps1:105-117), start again, and re-post the *same*
              client_key from C3: a deduplicated hit with the same message id proves the conversation
              survived the restart, not just that the process came back up.

      Stage 2 (only if -TargetDir is given): file-level assertions ONLY against the real deploy
      target -- SHA-256 of the exe, a recursive file-list-and-hash comparison of wwwroot against
      staging, zero loose *.dll in the target root, and whether `data\` exists (reported, never
      opened, listed, or hashed -- the privacy guard denies reads there in every session and this
      script declares no exemption). Stage 1's scratch copy cannot see what actually landed in the
      real target: a partial copy, a sharing violation, a torn write, or a missed wwwroot subfolder
      would leave Stage 1 honestly green. NEVER point this stage's launch checks at a live install --
      there aren't any; Stage 2 never starts a process, by design.

.PARAMETER PublishDir
    The staging/publish directory to check (what Deploy-ChopItUp.ps1 printed as `staging` in its
    DEPLOY_RESULT line, or a fresh `dotnet publish -c Release` output). Required.

.PARAMETER TargetDir
    The real deploy target (e.g. `C:\Self Apps\ChopItUp`). Optional -- omitting it runs Stage 1 only.
    Never read under `<TargetDir>\data`.

.PARAMETER KeepEvidence
    Keep the scratch directory (copied publish output, logs, mcp-check output) instead of deleting it
    at the end. Prints the directory path either way. The runtime's own extraction cache under
    `%TEMP%\.net\ChopItUp.Hub\<id>` is left behind regardless -- it is not this script's to remove.
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$TargetDir,
    [switch]$KeepEvidence
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    Write-Error "-PublishDir is required (the staging/publish directory to check)."
    exit 1
}
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    Write-Error "-PublishDir '$PublishDir' does not exist or is not a directory."
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$corpusProj = Join-Path $repoRoot 'tools\ChopItUp.Corpus\ChopItUp.Corpus.csproj'
$corpusExe = Join-Path $repoRoot 'tools\ChopItUp.Corpus\bin\Debug\net10.0\ChopItUp.Corpus.exe'

$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_m4selfcheck_$nonce"       # becomes the published-copy directory
$workDir = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_cwd" # a DIFFERENT cwd for the launched hub

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
    <# Polls for a file the hub writes on startup (hub.port, tokens.json), failing fast if the hub
       process exits first instead of spinning to the timeout for a reason that has nothing to do
       with the file. #>
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

function Wait-ForLockRelease {
    param([Parameter(Mandatory)][string]$LockPath, [int]$Retries = 20)
    for ($i = 0; $i -lt $Retries; $i++) {
        try {
            $fs = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Close()
            return $true
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    return $false
}

function Wait-ForProcessPath {
    <# Get-Process immediately after Start-Process can return a process whose .Path is not yet
       populated -- measured against this self-extracting single-file exe, which briefly leaves
       .Path empty right at launch. Poll instead of reading it once, so that race never surfaces as
       "cannot call a method on a null-valued expression" a few lines later. #>
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
    <# Stops a hub process this script itself started, by PID, only after re-confirming its image
       path still matches what we launched. Never Stop-Process -Name. #>
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

function Get-RelativeFileHashes {
    <# Maps every file under $Root, recursively, to its SHA-256 by path relative to $Root. Empty map
       for a missing directory (the caller decides whether that is itself a failure). #>
    param([Parameter(Mandatory)][string]$Root)

    $map = @{}
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $map }
    $rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    foreach ($f in (Get-ChildItem -LiteralPath $Root -File -Recurse)) {
        $rel = $f.FullName.Substring($rootFull.Length + 1)
        $map[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Invoke-McpCheck {
    <# Runs tools/ChopItUp.Corpus --mcp-check against a running hub. Never hand-rolls the MCP
       transport. The bearer token is passed via CHOPITUP_MCP_TOKEN and cleared in a finally so a
       failure never leaves a live token sitting in the environment; its value is never written to
       Write-Host, a check Detail, or a thrown message. #>
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][string]$ClientKey,
        [Parameter(Mandatory)][string]$OutPath,
        [Parameter(Mandatory)][string]$OutLog,
        [Parameter(Mandatory)][string]$ErrLog
    )
    $env:CHOPITUP_MCP_TOKEN = $Token
    try {
        $proc = Start-Process -FilePath $corpusExe -ArgumentList @(
            '--mcp-check',
            '--url', "http://127.0.0.1:$Port/",
            '--room', 'general',
            '--body', 'M4 self-check smoke test message (fabricated, no real conversation).',
            '--client-key', $ClientKey,
            '--out', $OutPath
        ) -PassThru -Wait -NoNewWindow -RedirectStandardOutput $OutLog -RedirectStandardError $ErrLog
    }
    finally {
        Remove-Item Env:\CHOPITUP_MCP_TOKEN -ErrorAction SilentlyContinue
    }
    if ($proc.ExitCode -ne 0) { throw "MCP check failed (exit $($proc.ExitCode)); see $ErrLog" }
    return (Get-Content -LiteralPath $OutPath -Raw | ConvertFrom-Json)
}

$hubProcess = $null
$hubExeFull = $null
$exitCode = 1
$origData = $null
$hadOrigData = $false
$origPort = $null
$hadOrigPort = $false

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    New-Item -ItemType Directory -Path $workDir | Out-Null

    # --- Build the corpus/mcp-check tool if it is not already built ------------------------------
    if (-not (Test-Path -LiteralPath $corpusExe)) {
        Write-Host "Building $corpusProj (Debug)..."
        & dotnet build $corpusProj -c Debug -v minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet build of ChopItUp.Corpus failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path -LiteralPath $corpusExe)) { throw "Expected corpus exe not found at '$corpusExe' after build." }
    }

    # === Stage 1: scratch copy of the publish output, launch checks =================================
    Write-Host "Copying '$PublishDir' to scratch '$scratch' (robocopy /E /XD data)..."
    robocopy $PublishDir $scratch /E /XD data /R:2 /W:2 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE copying '$PublishDir' -> '$scratch'." }

    # C2's whole claim is that the database is CREATED beside the exe by this run. A pre-existing
    # data\ folder (which should be impossible given the /XD data above, but this is the load-bearing
    # guard if this script is ever repointed) would let the check pass without proving anything.
    $dataDir = Join-Path $scratch 'data'
    if (Test-Path -LiteralPath $dataDir) {
        throw "Refusing to run launch checks: '$dataDir' already exists before the first launch. This would let C2 pass without proving the database was created by this run."
    }

    # --- C1: the published layout ------------------------------------------------------------------
    $exePath = Join-Path $scratch 'ChopItUp.Hub.exe'
    $exeExists = Test-Path -LiteralPath $exePath -PathType Leaf
    Add-Check -Name 'c1.exe-exists' -Passed $exeExists -Detail $exePath
    if ($exeExists) {
        $exeSize = (Get-Item -LiteralPath $exePath).Length
        Add-Check -Name 'c1.exe-size-floor-30mb' -Passed ($exeSize -ge 30MB) -Detail "size=$exeSize bytes"
    }
    else {
        Add-Check -Name 'c1.exe-size-floor-30mb' -Passed $false -Detail 'skipped: exe missing'
    }

    # The loose top-level set is exactly these four entries. Anything else present is a FAIL, not a
    # silently-accepted addition to the expected set -- STOP means report loudly, never expand what
    # "expected" means to match what got measured.
    $allowedTopLevel = @('ChopItUp.Hub.exe', 'ChopItUp.Hub.staticwebassets.endpoints.json', 'web.config', 'wwwroot')
    $topLevel = @(Get-ChildItem -LiteralPath $scratch -Force | ForEach-Object { $_.Name })
    $unexpected = @($topLevel | Where-Object { $allowedTopLevel -notcontains $_ })
    Add-Check -Name 'c1.loose-file-set-exact' -Passed ($unexpected.Count -eq 0) -Detail "measured=[$($topLevel -join ', ')] unexpected=[$($unexpected -join ', ')]"

    $looseDll = @(Get-ChildItem -LiteralPath $scratch -File -Filter '*.dll' -ErrorAction SilentlyContinue)
    $loosePdb = @(Get-ChildItem -LiteralPath $scratch -File -Filter '*.pdb' -ErrorAction SilentlyContinue)
    $looseDeps = @(Get-ChildItem -LiteralPath $scratch -File -Filter '*.deps.json' -ErrorAction SilentlyContinue)
    Add-Check -Name 'c1.zero-loose-dll' -Passed ($looseDll.Count -eq 0) -Detail "count=$($looseDll.Count)"
    Add-Check -Name 'c1.zero-loose-pdb' -Passed ($loosePdb.Count -eq 0) -Detail "count=$($loosePdb.Count)"
    Add-Check -Name 'c1.zero-loose-deps-json' -Passed ($looseDeps.Count -eq 0) -Detail "count=$($looseDeps.Count)"

    $indexPath = Join-Path $scratch 'wwwroot\index.html'
    Add-Check -Name 'c1.wwwroot-index-html-exists' -Passed (Test-Path -LiteralPath $indexPath -PathType Leaf) -Detail $indexPath

    $assetsDir = Join-Path $scratch 'wwwroot\assets'
    $assetFiles = @()
    if (Test-Path -LiteralPath $assetsDir -PathType Container) {
        $assetFiles = @(Get-ChildItem -LiteralPath $assetsDir -File -Recurse -ErrorAction SilentlyContinue)
    }
    Add-Check -Name 'c1.wwwroot-assets-non-empty' -Passed ($assetFiles.Count -ge 1) -Detail "count=$($assetFiles.Count)"

    if (-not $exeExists) { throw "Cannot continue to C2/C3: '$exePath' does not exist." }

    # --- C2: launch with no --data, --port 0, a DIFFERENT working directory, env cleared ------------
    if (Test-Path Env:\CHOPITUP_DATA) { $hadOrigData = $true; $origData = $env:CHOPITUP_DATA }
    if (Test-Path Env:\CHOPITUP_PORT) { $hadOrigPort = $true; $origPort = $env:CHOPITUP_PORT }
    Remove-Item Env:\CHOPITUP_DATA -ErrorAction SilentlyContinue
    Remove-Item Env:\CHOPITUP_PORT -ErrorAction SilentlyContinue

    $hubPortFile = Join-Path $dataDir 'hub.port'
    if (Test-Path -LiteralPath $hubPortFile) { Remove-Item -LiteralPath $hubPortFile -Force } # never trust a stale/last-running port

    $hub1OutLog = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_hub1.out.log"
    $hub1ErrLog = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_hub1.err.log"
    $hubExeFull = (Resolve-Path -LiteralPath $exePath).Path

    $hubProcess = Start-Process -FilePath $exePath -ArgumentList @('--port', '0') -WorkingDirectory $workDir -PassThru -NoNewWindow `
        -RedirectStandardOutput $hub1OutLog -RedirectStandardError $hub1ErrLog

    $confirmed = Wait-ForProcessPath -ProcessId $hubProcess.Id
    if ($confirmed.Path.ToLowerInvariant() -ne $hubExeFull.ToLowerInvariant()) {
        throw "Process $($hubProcess.Id) image path '$($confirmed.Path)' does not match the hub exe this script launched ('$hubExeFull'); refusing to treat it as ours."
    }

    Wait-ForHubFile -Path $hubPortFile -HubProcess $hubProcess -What 'hub.port'
    $portRaw = (Get-Content -LiteralPath $hubPortFile -Raw).Trim()
    if ($portRaw -notmatch '^\d+$') { throw "hub.port contained non-numeric content: '$portRaw'" }
    $port = [int]$portRaw

    $healthUrl = "http://127.0.0.1:$port/health"
    $health = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($hubProcess.HasExited) { throw "Hub exited early (code $($hubProcess.ExitCode)) while waiting for /health; see $hub1ErrLog" }
        try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 5; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $health) { throw "Hub /health did not respond within 30s at $healthUrl." }
    Add-Check -Name 'c2.hub-launched-different-cwd' -Passed $true -Detail "pid=$($hubProcess.Id) port=$port cwd=$workDir exeDir=$scratch"

    Add-Check -Name 'c2.database-created-beside-exe' -Passed (Test-Path -LiteralPath (Join-Path $dataDir 'chopitup.db') -PathType Leaf) -Detail (Join-Path $dataDir 'chopitup.db')
    Add-Check -Name 'c2.no-database-in-working-directory' -Passed (-not (Test-Path -LiteralPath (Join-Path $workDir 'data'))) -Detail (Join-Path $workDir 'data')

    # --- C3: health schema, MCP post/dedup, UI shell + real script fetch -----------------------------
    Add-Check -Name 'c3.health-schema-2' -Passed ($health.schema -eq 2) -Detail "schema=$($health.schema)"

    $tokensPath = Join-Path $dataDir 'tokens.json'
    Wait-ForHubFile -Path $tokensPath -HubProcess $hubProcess -What 'tokens.json'
    $tokens = Get-Content -LiteralPath $tokensPath -Raw | ConvertFrom-Json

    $clientKey = [guid]::NewGuid().ToString('N')
    $mcpOutPath = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_mcp1.json"
    $mcpOutLog = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_mcp1.out.log"
    $mcpErrLog = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_mcp1.err.log"
    $mcpResult = Invoke-McpCheck -Port $port -Token $tokens.claude -ClientKey $clientKey -OutPath $mcpOutPath -OutLog $mcpOutLog -ErrLog $mcpErrLog

    Add-Check -Name 'c3.post-message-lands' -Passed ($mcpResult.first_id -ge 1 -and -not $mcpResult.first_deduplicated) `
        -Detail "first_id=$($mcpResult.first_id) first_deduplicated=$($mcpResult.first_deduplicated)"
    Add-Check -Name 'c3.repeat-same-key-deduplicates' -Passed ($mcpResult.second_id -eq $mcpResult.first_id -and $mcpResult.second_deduplicated) `
        -Detail "second_id=$($mcpResult.second_id) second_deduplicated=$($mcpResult.second_deduplicated)"

    # The UI check must FETCH the script the shell references, not grep for its tag: a missing
    # wwwroot\assets\ must not be mistaken for a healthy UI just because SOME 200 came back for the
    # page. Measured behaviour for a missing asset file in this app is a bare 404 from the static-web-
    # assets endpoint (ChopItUp.Hub.staticwebassets.endpoints.json) that ASP.NET Core registers ahead
    # of SpaFiles' own MapFallback -- NOT a 200 carrying the HTML shell, which is what an earlier
    # reasoning pass in this plan predicted for a generic unreserved path (and which IS what a
    # never-existed path like /nonsense still gets, confirmed separately). Either failure shape must
    # be caught, so both fetches are wrapped: a thrown/non-JS/shell-identical response is a FAIL, not
    # a script-ending exception that would silently skip the restart check that follows.
    try {
        $indexResp = Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -TimeoutSec 10 -UseBasicParsing
        $scriptMatch = [regex]::Match($indexResp.Content, 'src="(/assets/[^"]+\.js)"')
        Add-Check -Name 'c3.ui-shell-served' -Passed ($indexResp.StatusCode -eq 200 -and $scriptMatch.Success) -Detail "status=$($indexResp.StatusCode) scriptMatch=$($scriptMatch.Success)"
    }
    catch {
        Add-Check -Name 'c3.ui-shell-served' -Passed $false -Detail "shell fetch failed: $($_.Exception.Message)"
        $scriptMatch = $null
    }
    if ($scriptMatch -and $scriptMatch.Success) {
        $scriptUrl = "http://127.0.0.1:$port$($scriptMatch.Groups[1].Value)"
        try {
            $scriptResp = Invoke-WebRequest -Uri $scriptUrl -TimeoutSec 10 -UseBasicParsing
            $contentType = [string]($scriptResp.Headers['Content-Type'] | Select-Object -First 1)
            Add-Check -Name 'c3.ui-script-is-javascript' -Passed ($contentType -like 'text/javascript*') -Detail "url=$scriptUrl content-type=$contentType"
            Add-Check -Name 'c3.ui-script-not-shell-bytes' -Passed ($scriptResp.Content -ne $indexResp.Content) -Detail "scriptBytes=$($scriptResp.RawContentLength) shellBytes=$($indexResp.RawContentLength)"
        }
        catch {
            Add-Check -Name 'c3.ui-script-is-javascript' -Passed $false -Detail "script fetch failed (url=$scriptUrl): $($_.Exception.Message)"
            Add-Check -Name 'c3.ui-script-not-shell-bytes' -Passed $false -Detail "script fetch failed (url=$scriptUrl): $($_.Exception.Message)"
        }
    }
    else {
        Add-Check -Name 'c3.ui-script-is-javascript' -Passed $false -Detail 'skipped: no script src found in shell'
        Add-Check -Name 'c3.ui-script-not-shell-bytes' -Passed $false -Detail 'skipped: no script src found in shell'
    }

    # --- Restart: stop by PID (WaitForExit), delete hub.port, relaunch, same client_key dedups ------
    $lockPath = Join-Path $dataDir 'hub.lock'
    Write-Host "Stopping hub pid $($hubProcess.Id) for the restart check..."
    Stop-VerifiedHub -Process $hubProcess -ExpectedImagePath $hubExeFull
    $lockFreed = Wait-ForLockRelease -LockPath $lockPath
    Add-Check -Name 'restart.lock-released-before-relaunch' -Passed $lockFreed -Detail $lockPath
    if (-not $lockFreed) { throw "Hub lock at '$lockPath' was not released after Stop-Process/WaitForExit; refusing to restart into a still-locked data dir." }
    $hubProcess = $null # cleanly stopped; nothing left for the finally block to do for this instance

    if (Test-Path -LiteralPath $hubPortFile) { Remove-Item -LiteralPath $hubPortFile -Force } # never trust the "last-running" port for the relaunch

    $hub2OutLog = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_hub2.out.log"
    $hub2ErrLog = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_hub2.err.log"
    $hubProcess = Start-Process -FilePath $exePath -ArgumentList @('--port', '0') -WorkingDirectory $workDir -PassThru -NoNewWindow `
        -RedirectStandardOutput $hub2OutLog -RedirectStandardError $hub2ErrLog

    $confirmed2 = Wait-ForProcessPath -ProcessId $hubProcess.Id
    if ($confirmed2.Path.ToLowerInvariant() -ne $hubExeFull.ToLowerInvariant()) {
        throw "Process $($hubProcess.Id) image path '$($confirmed2.Path)' does not match the hub exe this script launched ('$hubExeFull'); refusing to treat it as ours."
    }

    Wait-ForHubFile -Path $hubPortFile -HubProcess $hubProcess -What 'hub.port (restart)'
    $portRaw2 = (Get-Content -LiteralPath $hubPortFile -Raw).Trim()
    if ($portRaw2 -notmatch '^\d+$') { throw "hub.port (restart) contained non-numeric content: '$portRaw2'" }
    $port2 = [int]$portRaw2

    $health2 = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ($hubProcess.HasExited) { throw "Hub exited early (code $($hubProcess.ExitCode)) after restart while waiting for /health; see $hub2ErrLog" }
        try { $health2 = Invoke-RestMethod -Uri "http://127.0.0.1:$port2/health" -TimeoutSec 5; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $health2) { throw "Hub /health did not respond within 30s at http://127.0.0.1:$port2/health after restart." }
    Add-Check -Name 'restart.hub-relaunched' -Passed $true -Detail "pid=$($hubProcess.Id) port=$port2"

    # Re-post with the SAME client_key from C3: a deduplicated hit returning the SAME message id
    # proves the conversation this script created before the restart is still there, not just that
    # the process came back up.
    $mcpOutPath2 = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_mcp2.json"
    $mcpOutLog2 = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_mcp2.out.log"
    $mcpErrLog2 = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}_mcp2.err.log"
    $tokens2 = Get-Content -LiteralPath $tokensPath -Raw | ConvertFrom-Json # re-read: same file, but be explicit that it's post-restart
    $mcpResult2 = Invoke-McpCheck -Port $port2 -Token $tokens2.claude -ClientKey $clientKey -OutPath $mcpOutPath2 -OutLog $mcpOutLog2 -ErrLog $mcpErrLog2
    Add-Check -Name 'restart.original-message-persisted' -Passed ($mcpResult2.first_id -eq $mcpResult.first_id -and $mcpResult2.first_deduplicated) `
        -Detail "before_id=$($mcpResult.first_id) after_id=$($mcpResult2.first_id) after_deduplicated=$($mcpResult2.first_deduplicated)"

    Write-Host "Stopping hub pid $($hubProcess.Id) (end of stage 1)..."
    Stop-VerifiedHub -Process $hubProcess -ExpectedImagePath $hubExeFull
    Add-Check -Name 'hub.stopped-by-id-after-path-check' -Passed $true -Detail "pid=$($hubProcess.Id)"
    $hubProcess = $null

    # === Stage 2: file-level pass over the real deploy target (never reads under data\) =============
    if (-not [string]::IsNullOrWhiteSpace($TargetDir)) {
        $targetDir = $TargetDir.TrimEnd('\')
        Write-Host "Stage 2: file-level checks against real target '$targetDir' (staging = '$PublishDir')..."

        $stagingExe = Join-Path $PublishDir 'ChopItUp.Hub.exe'
        $targetExe = Join-Path $targetDir 'ChopItUp.Hub.exe'
        $targetExeExists = Test-Path -LiteralPath $targetExe -PathType Leaf
        Add-Check -Name 'post-deploy.exe-exists' -Passed $targetExeExists -Detail $targetExe
        if ($targetExeExists -and (Test-Path -LiteralPath $stagingExe -PathType Leaf)) {
            $stagingHash = (Get-FileHash -LiteralPath $stagingExe -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash
            Add-Check -Name 'post-deploy.exe-sha256-matches-staging' -Passed ($stagingHash -eq $targetHash) -Detail "staging=$stagingHash target=$targetHash"
        }
        else {
            Add-Check -Name 'post-deploy.exe-sha256-matches-staging' -Passed $false -Detail 'skipped: staging or target exe missing'
        }

        # Recursive on purpose: the interesting failure (the /E robocopy bug) lives one level down in
        # wwwroot\assets\, so a top-level-only comparison would reproduce the exact bug it exists to catch.
        $stagingWwwrootHashes = Get-RelativeFileHashes -Root (Join-Path $PublishDir 'wwwroot')
        $targetWwwrootHashes = Get-RelativeFileHashes -Root (Join-Path $targetDir 'wwwroot')
        $missingInTarget = @($stagingWwwrootHashes.Keys | Where-Object { -not $targetWwwrootHashes.ContainsKey($_) })
        $extraInTarget = @($targetWwwrootHashes.Keys | Where-Object { -not $stagingWwwrootHashes.ContainsKey($_) })
        $fileListMatches = ($stagingWwwrootHashes.Count -gt 0 -and $missingInTarget.Count -eq 0 -and $extraInTarget.Count -eq 0)
        Add-Check -Name 'post-deploy.wwwroot-file-list-matches-staging' -Passed $fileListMatches `
            -Detail "stagingCount=$($stagingWwwrootHashes.Count) targetCount=$($targetWwwrootHashes.Count) missingInTarget=[$($missingInTarget -join ', ')] extraInTarget=[$($extraInTarget -join ', ')]"

        $hashMismatches = @()
        if ($fileListMatches) {
            foreach ($rel in $stagingWwwrootHashes.Keys) {
                if ($stagingWwwrootHashes[$rel] -ne $targetWwwrootHashes[$rel]) { $hashMismatches += $rel }
            }
        }
        $hashesMatch = ($fileListMatches -and $hashMismatches.Count -eq 0)
        Add-Check -Name 'post-deploy.wwwroot-hashes-match-staging' -Passed $hashesMatch -Detail "mismatches=[$($hashMismatches -join ', ')]"

        $targetLooseDll = @(Get-ChildItem -LiteralPath $targetDir -File -Filter '*.dll' -ErrorAction SilentlyContinue)
        Add-Check -Name 'post-deploy.zero-loose-dll-in-target-root' -Passed ($targetLooseDll.Count -eq 0) -Detail "count=$($targetLooseDll.Count)"

        # Report only. Nothing inside data\ is opened, listed, or hashed -- ever.
        $targetDataPresent = Test-Path -LiteralPath (Join-Path $targetDir 'data') -PathType Container
        Add-Check -Name 'post-deploy.data-dir-present-report-only' -Passed $true -Detail "present=$targetDataPresent (not opened, listed, or hashed)"
    }
    else {
        Write-Host "No -TargetDir given; Stage 2 (post-deploy file checks) skipped."
    }

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())]"
    $exitCode = 1
}
finally {
    # Defensive stop: only reached if an earlier step threw before its own stop-by-id step ran.
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
                    $hubProcess.WaitForExit(15000) | Out-Null
                }
            }
        }
        catch { }
    }

    Remove-Item Env:\CHOPITUP_MCP_TOKEN -ErrorAction SilentlyContinue
    if ($hadOrigData) { $env:CHOPITUP_DATA = $origData } else { Remove-Item Env:\CHOPITUP_DATA -ErrorAction SilentlyContinue }
    if ($hadOrigPort) { $env:CHOPITUP_PORT = $origPort } else { Remove-Item Env:\CHOPITUP_PORT -ErrorAction SilentlyContinue }

    $logPath = Join-Path $env:TEMP "chopitup_m4selfcheck_${nonce}.m4-selfcheck.log"
    $checkLines | Set-Content -Path $logPath -Encoding utf8

    $passCount = ($checkLines | Where-Object { $_.StartsWith('PASS') }).Count
    $totalCount = $checkLines.Count
    Write-Host ""
    Write-Host "M4 self-check log: $logPath"
    Write-Host "Results: $passCount/$totalCount PASS"

    if ($KeepEvidence) {
        Write-Host "Evidence kept at: $scratch"
        Write-Host "(working-directory scratch also kept at: $workDir)"
    }
    else {
        try { Remove-Item -Path $scratch -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$scratch': $($_.Exception.Message)" }
        try { Remove-Item -Path $workDir -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$workDir': $($_.Exception.Message)" }
        Get-ChildItem -Path $env:TEMP -Filter "chopitup_m4selfcheck_${nonce}_*" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Note: the .NET runtime's own single-file extraction cache under %TEMP%\.net\ChopItUp.Hub\<id> is left behind by design; it is not this script's to remove."
}

exit $exitCode
