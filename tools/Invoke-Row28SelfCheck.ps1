<#
.SYNOPSIS
    Row 28 Task 9 (issues/09-deploy-day-checks.md, required at HIGH): the deploy-day gate, run and
    read by whoever installs row 28 against the REAL hub -- not a scratch copy.

.DESCRIPTION
    DEPLOY-DAY ORDER (D-28-c/D-28-d; row 28 ships deliberately undeployed until this runs):
      1. Stop the hub (owner-verified PID, never `Stop-Process -Name`).
      2. Deploy the new build (`Deploy-ChopItUp.ps1` or equivalent) to -InstallDir.
      3. Start the hub.
      4. `ChopItUp.Hub.exe --data <install>\data --rotate-token owner` -- OWNER-TYPED ONLY, NEVER
         AGENT-RUN (D-28-d, docs/verification.md "Rotating a token"). It prints the new owner bearer
         once, to the owner's own terminal.
      5. The owner pastes that value into the browser's token prompt.
      6. Run THIS script, passing the same value as -OwnerToken.
    Steps 1-5 are all owner-only (a terminal the owner may not have had overnight is exactly why row
    28 shipped undeployed -- see the plan's D-28-c). This script performs none of them: it never stops
    or starts the hub, never runs --rotate-token, and never touches C:\Self Apps outside the read-only
    checks named below.

    WHAT IT CHECKS
      Data checks (need to read under <InstallDir>\data\; see PRIVACY BOUNDARY below):
        - tokens.json is the hashed {"id":{"sha256":"..."}} shape for every entry -- no entry is a
          bare plaintext string (the pre-row-28 shape).
        - No file under <InstallDir>\data\host-configs\ still carries a live-token-shaped run (43
          chars of the minted alphabet, TokenScan's own candidate shape) outside the {{TOKEN}}
          placeholder.
      Runnable checks (no protected read; independent of the data checks and of each other):
        - /health answers 200 with the expected schema.
        - An unauthenticated POST /api/rooms/{RoomId}/messages is refused (401) and leaves the room's
          message count unchanged.
        - The SAME POST, carrying "Authorization: Bearer <OwnerToken>", is accepted (201) -- the
          "refused-then-accepted pair" the ticket names, and the actual proof the owner can post from
          the browser after pasting the token.
        - The deployed ChopItUp.Hub.exe and wwwroot\ are byte-identical to what -PublishDir staged
          (SHA-256 per file), so the browser is never left on a build that cannot authenticate.

    PRIVACY BOUNDARY -- READ BEFORE RUNNING FROM AN AGENT SESSION
      This repo's own guard denies file reads under `C:\Self Apps\*\data` in an agent session, and
      that deny is correct and permanent -- it is not a nuisance this script routes around. The two
      data checks above are written to be run by an OPERATOR who has that access; an agent session
      cannot execute them against a real install and must not attempt to. Pass -SkipDataChecks to run
      only the checks that need no protected read (the four runnable checks above); that subset is
      independently useful (it is everything a session without data\ access can still prove) and is
      exactly what this task's author actually ran in this session, against a throwaway scratch
      install it built itself -- never against C:\Self Apps.

    DATABASE BOUNDARY: this script never opens, queries, or PRAGMAs chopitup.db, and the data checks
    above never read the database either -- only tokens.json and host-config text files, both outside
    the database file itself.

    BINARY LAUNCHES: none. This script starts and stops no process; every check is an HTTP call
    against a hub the operator already started (step 3 above) or a file-hash comparison. There is
    nothing to allowlist.

    CREDENTIAL HANDLING: -OwnerToken is read from the command line, held only in memory for the
    lifetime of this process, and is never written to the log, to stdout/Write-Host, to any file under
    <InstallDir>, or to a commit. It necessarily passes through the process command line and PowerShell
    history the same way `--rotate-token`'s printed value does (docs/verification.md already accepts
    that trade for a human typing a command by hand); this script adds no further exposure beyond that.

    RESUME SEMANTICS: a PASS is recorded per check name, keyed to a RunId derived from the deployed
    exe's mtime+size plus this harness's version. Re-running after a fresh deploy (new mtime/size)
    forces every check to run again; re-running against the SAME deploy skips checks already PASSed --
    load-bearing for the owner-token POST check, which would otherwise leave a duplicate verification
    message in the real room on every re-run while troubleshooting. Pass -Reset to ignore prior PASS
    rows and force everything to run again regardless of RunId.

.PARAMETER InstallDir
    The real install directory. Defaults to `C:\Self Apps\ChopItUp`.

.PARAMETER OwnerToken
    The value the owner pasted after `--rotate-token owner` (step 4 above). Required. Never read from
    disk, never logged.

.PARAMETER PublishDir
    The staging/publish directory that was actually deployed (what `Deploy-ChopItUp.ps1` printed as
    `staging`, or a fresh `dotnet publish -c Release` output) -- compared file-for-file against
    -InstallDir to prove the deployed bundle is the one that was just published.

.PARAMETER Port
    The hub's HTTP port. Defaults to 8790 (HubOptions.DefaultPort; docs/LESSONS.md M8 names this as
    "the live hub's" port).

.PARAMETER RoomId
    The room the 401/201 pair posts against. Defaults to 'general'. The 201 leaves one real,
    clearly-labelled verification message in this room -- see RESUME SEMANTICS for why a re-run does
    not repeat it.

.PARAMETER SkipDataChecks
    Skip the two checks that read under <InstallDir>\data\ (see PRIVACY BOUNDARY). Required for any
    session whose file-read guard denies that path; the operator running this for real should omit it.

.PARAMETER LogDir
    Where the evidence log lives. Defaults to a directory under $env:TEMP -- NEVER pass a path under
    this repo's `tools\` directory: it is git-tracked, and the nested `.gitignore` this script writes
    (so its own log never gets committed) is refused outright when the target directory already holds
    tracked files, specifically to avoid repeating the trap in the desk-check template's own docs
    (a `.gitignore` containing `*` untracking six real files when a harness was once placed in `build\`).

.PARAMETER Reset
    Ignore prior PASS rows for this RunId and re-run every check, including the owner-token POST
    (which will then leave a second verification message in -RoomId).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'C:\Self Apps\ChopItUp',
    [Parameter(Mandatory)][string]$OwnerToken,
    [Parameter(Mandatory)][string]$PublishDir,
    [int]$Port = 8790,
    [string]$RoomId = 'general',
    [switch]$SkipDataChecks,
    [string]$LogDir,
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'
$HarnessVersion = 'row28-selfcheck-v1'

# --- RunId + log setup (desk-check-template.ps1 shape: RunId from the target exe, resume semantics,
#     a nested .gitignore that refuses to write into a directory holding tracked files) --------------
$installExePath = Join-Path $InstallDir 'ChopItUp.Hub.exe'
if (-not (Test-Path -LiteralPath $installExePath -PathType Leaf)) {
    Write-Error "'$installExePath' does not exist. Has the new build actually been deployed to -InstallDir (step 2 of the deploy-day order)?"
    exit 1
}
$exeInfo = Get-Item -LiteralPath $installExePath
$RunId = "{0:yyyyMMddTHHmmssZ}+{1}+{2}" -f $exeInfo.LastWriteTimeUtc, $exeInfo.Length, $HarnessVersion

$logDir = if ($LogDir) { $LogDir } else { Join-Path $env:TEMP 'chopitup-row28-selfcheck-logs' }
$logDir = (New-Item -ItemType Directory -Force -Path $logDir).FullName

$trackedHere = @()
try { $trackedHere = @(& git -C $logDir ls-files 2>$null) } catch { }
if ($trackedHere.Count -gt 0) {
    throw ("DENIED: '$logDir' holds $($trackedHere.Count) git-tracked file(s); a nested .gitignore " +
           "('*') here would untrack them. Pass -LogDir at an untracked directory outside this repo's " +
           "'tools\' (which IS tracked) -- the default under `$env:TEMP` or a session scratchpad.")
}
$giPath = Join-Path $logDir '.gitignore'
if (-not (Test-Path -LiteralPath $giPath)) { Set-Content -LiteralPath $giPath -Value '*' -Encoding ascii }

$scriptName = [IO.Path]::GetFileNameWithoutExtension($MyInvocation.MyCommand.Path)
$log = Join-Path $logDir "$scriptName.log"
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')

# keep-last-3 of any rotated logs
Get-ChildItem -LiteralPath $logDir -Filter "$scriptName.*.log" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -Skip 2 | Remove-Item -Force
if ($Reset -and (Test-Path -LiteralPath $log)) {
    Move-Item -LiteralPath $log -Destination (Join-Path $logDir "$scriptName.$stamp.log") -Force
}

$prior = @{}
if (Test-Path -LiteralPath $log) {
    Get-Content -LiteralPath $log | ForEach-Object {
        $parts = $_ -split "`t"
        if ($parts.Count -ge 3 -and $parts[2] -eq 'PASS') { $prior[$parts[1]] = $parts[0] }
    }
}

$script:pass = 0; $script:fail = 0; $script:skip = 0

function Invoke-Check {
    <# Runs $Body (a scriptblock returning @{ Passed = [bool]; Detail = [string] }) unless a PASS is
       already on record for this exact RunId, or $SkipReason is given -- in which case it is not
       invoked at all (load-bearing for the owner-token POST: a resumed/skipped check never repeats
       whatever side effect it had). Detail must never carry a credential or raw file content -- only
       counts, booleans, entry/file NAMES, and hashes -- callers are responsible for that. #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [scriptblock]$Body,
        [string]$SkipReason
    )
    if ($SkipReason) {
        Write-Host ("{0,-6}{1}  {2}" -f 'SKIP', $Name, $SkipReason)
        $script:skip++
        Add-Content -LiteralPath $log -Value ("{0}`t{1}`t{2}`t{3}`t{4}" -f $RunId, $Name, 'SKIP', $SkipReason, (Get-Date -Format o))
        return
    }
    if ($prior[$Name] -eq $RunId) {
        Write-Host "skip  $Name (PASS already recorded for this RunId)"
        $script:pass++
        return
    }
    $status = 'FAIL'; $detail = ''
    try {
        $result = & $Body
        $status = if ($result.Passed) { 'PASS' } else { 'FAIL' }
        $detail = [string]$result.Detail
    }
    catch {
        $status = 'FAIL'
        $detail = "exception: $($_.Exception.Message)"
    }
    Write-Host ("{0,-6}{1}  {2}" -f $status, $Name, $detail)
    if ($status -eq 'PASS') { $script:pass++ } else { $script:fail++ }
    Add-Content -LiteralPath $log -Value ("{0}`t{1}`t{2}`t{3}`t{4}" -f $RunId, $Name, $status, $detail, (Get-Date -Format o))
}

function Get-RelativeFileHashes {
    <# Maps every file under $Root, recursively, to its SHA-256 by path relative to $Root. Empty map
       for a missing directory. Matches Invoke-M4SelfCheck.ps1's helper of the same name. #>
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

# ======================================================================================================
# Data checks -- read under <InstallDir>\data\. See PRIVACY BOUNDARY above. Guarded by -SkipDataChecks.
# ======================================================================================================

$dataSkipReason = if ($SkipDataChecks) { "-SkipDataChecks: this run does not read under '$InstallDir\data\'" } else { $null }

Invoke-Check -Name 'data.tokens-json-hashed-shape-no-plaintext' -SkipReason $dataSkipReason -Body {
    $tokensPath = Join-Path $InstallDir 'data\tokens.json'
    if (-not (Test-Path -LiteralPath $tokensPath -PathType Leaf)) {
        return @{ Passed = $false; Detail = "not found: $tokensPath" }
    }
    $json = (Get-Content -LiteralPath $tokensPath -Raw) | ConvertFrom-Json
    $names = @($json.PSObject.Properties.Name)
    $plaintextEntries = @()
    $badShapeEntries = @()
    foreach ($n in $names) {
        $entry = $json.$n
        if ($entry -is [string]) { $plaintextEntries += $n; continue }
        if ($null -eq $entry.sha256 -or $entry.sha256 -notmatch '^[0-9a-f]{64}$') { $badShapeEntries += $n }
    }
    $ok = ($names.Count -gt 0 -and $plaintextEntries.Count -eq 0 -and $badShapeEntries.Count -eq 0)
    @{ Passed = $ok; Detail = "entries=$($names.Count) plaintext=[$($plaintextEntries -join ', ')] badShape=[$($badShapeEntries -join ', ')]" }
}

Invoke-Check -Name 'data.host-configs-carry-no-live-token' -SkipReason $dataSkipReason -Body {
    $hostConfigsDir = Join-Path $InstallDir 'data\host-configs'
    if (-not (Test-Path -LiteralPath $hostConfigsDir -PathType Container)) {
        return @{ Passed = $true; Detail = "no host-configs directory at $hostConfigsDir" }
    }
    $files = @(Get-ChildItem -LiteralPath $hostConfigsDir -File -Recurse -ErrorAction SilentlyContinue)
    # TokenScan's own candidate shape: 43 chars from the minted-token alphabet (base64url, no padding).
    # {{TOKEN}} itself is 8 characters and never matches.
    $liveTokenFiles = @()
    foreach ($f in $files) {
        $content = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if ($content -and [regex]::IsMatch($content, '[A-Za-z0-9_-]{43}')) { $liveTokenFiles += $f.Name }
    }
    @{ Passed = ($liveTokenFiles.Count -eq 0); Detail = "checked=$($files.Count) files; live-token-shaped string found in=[$($liveTokenFiles -join ', ')]" }
}

# ======================================================================================================
# Runnable checks -- no protected read. Independent of the data checks and of each other.
# ======================================================================================================

Invoke-Check -Name 'health.responds-200-expected-schema' -Body {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 10
    $expectedSchema = 10   # ChopDb.LatestSchemaVersion as of row 28 (src/ChopItUp.Core/Storage/ChopDb.cs:10)
    @{ Passed = ($health.ok -eq $true -and $health.schema -eq $expectedSchema); Detail = "ok=$($health.ok) schema=$($health.schema) expected=$expectedSchema" }
}

Invoke-Check -Name 'auth.no-credential-post-refused-401' -Body {
    # M10 (docs/LESSONS.md): Invoke-RestMethod hands back a top-level JSON array as one wrapper
    # object -- pipe through ForEach-Object { $_ } before counting/filtering, every time.
    $before = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/rooms/$RoomId/messages" -TimeoutSec 10
    $beforeCount = @($before.messages | ForEach-Object { $_ }).Count
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/rooms/$RoomId/messages" -Method Post -ContentType 'application/json' `
        -Body (@{ body = '[row28-selfcheck] unauthenticated probe -- must be refused, never stored.' } | ConvertTo-Json) `
        -TimeoutSec 10 -SkipHttpErrorCheck
    $after = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/rooms/$RoomId/messages" -TimeoutSec 10
    $afterCount = @($after.messages | ForEach-Object { $_ }).Count
    @{ Passed = ($resp.StatusCode -eq 401 -and $afterCount -eq $beforeCount); Detail = "status=$($resp.StatusCode) beforeCount=$beforeCount afterCount=$afterCount" }
}

Invoke-Check -Name 'auth.owner-token-post-accepted-201' -Body {
    $headers = @{ Authorization = "Bearer $OwnerToken" }
    $marker = "[Row 28 deploy self-check] $stamp -- verifying the pasted owner token authenticates writes."
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/rooms/$RoomId/messages" -Method Post -Headers $headers -ContentType 'application/json' `
        -Body (@{ body = $marker } | ConvertTo-Json) -TimeoutSec 10 -SkipHttpErrorCheck
    # Detail never carries $OwnerToken -- only the status code.
    @{ Passed = ($resp.StatusCode -eq 201); Detail = "status=$($resp.StatusCode)" }
}

Invoke-Check -Name 'bundle.exe-sha256-matches-publish' -Body {
    $stagingExe = Join-Path $PublishDir 'ChopItUp.Hub.exe'
    if (-not (Test-Path -LiteralPath $stagingExe -PathType Leaf)) {
        return @{ Passed = $false; Detail = "staging exe not found: $stagingExe" }
    }
    $stagingHash = (Get-FileHash -LiteralPath $stagingExe -Algorithm SHA256).Hash
    $installHash = (Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash
    @{ Passed = ($stagingHash -eq $installHash); Detail = "staging=$stagingHash install=$installHash" }
}

Invoke-Check -Name 'bundle.wwwroot-matches-publish' -Body {
    $stagingHashes = Get-RelativeFileHashes -Root (Join-Path $PublishDir 'wwwroot')
    $installHashes = Get-RelativeFileHashes -Root (Join-Path $InstallDir 'wwwroot')
    $missing = @($stagingHashes.Keys | Where-Object { -not $installHashes.ContainsKey($_) })
    $extra = @($installHashes.Keys | Where-Object { -not $stagingHashes.ContainsKey($_) })
    $mismatched = @($stagingHashes.Keys | Where-Object { $installHashes.ContainsKey($_) -and $installHashes[$_] -ne $stagingHashes[$_] })
    $ok = ($stagingHashes.Count -gt 0 -and $missing.Count -eq 0 -and $extra.Count -eq 0 -and $mismatched.Count -eq 0)
    @{ Passed = $ok; Detail = "stagingCount=$($stagingHashes.Count) installCount=$($installHashes.Count) missing=$($missing.Count) extra=$($extra.Count) mismatched=$($mismatched.Count)" }
}

# ======================================================================================================
Write-Host ""
Write-Host ("RunId {0}: {1} PASS, {2} SKIP, {3} FAIL -- log: {4}" -f $RunId, $script:pass, $script:skip, $script:fail, $log)
exit ($(if ($script:fail -gt 0) { 1 } else { 0 }))
