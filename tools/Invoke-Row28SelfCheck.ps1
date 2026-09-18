<#
.SYNOPSIS
    Row 28 Task 9 (issues/09-deploy-day-checks.md, required at HIGH): the deploy-day gate, run and
    read by whoever installs row 28 against the REAL hub -- not a scratch copy.

.DESCRIPTION
    DEPLOY-DAY ORDER (D-28-c/D-28-d; row 28 ships deliberately undeployed until this runs):
      1. Stop the hub (owner-verified PID, never `Stop-Process -Name`).
      2. Deploy the new build (`Deploy-ChopItUp.ps1` or equivalent) to -InstallDir.
      3. `ChopItUp.Hub.exe --data <install>\data --rotate-token owner` -- OWNER-TYPED ONLY, NEVER
         AGENT-RUN (D-28-d, docs/verification.md "Rotating a token"). It prints the new owner bearer
         once, to the owner's own terminal.
         ROTATE BEFORE THE HUB IS STARTED, NOT AFTER: HostCommands.RotateToken gates on
         HubLock.IsHeld and exits 5 against a live hub ("Stop it first -- rotating while it runs
         writes a new token that the running hub ignores, and the old token keeps working"), because
         a loaded TokenStore is a startup singleton (TokenStore.Load's remarks). An earlier revision
         of this list put the start before the rotate; following it deploy-day cost a stop/start
         cycle and produced an exit 5, so the order below is the product's, not a preference.
      4. Start the hub. It loads the rotated tokens.json at startup; nothing rotated after this point
         takes effect until the next start.
      5. The owner pastes that value into the browser's token prompt.
      6. Run THIS script, passing the same value as -OwnerToken.
    Steps 1-5 are all owner-only (a terminal the owner may not have had overnight is exactly why row
    28 shipped undeployed -- see the plan's D-28-c). This script performs none of them: it never stops
    or starts the hub, never runs --rotate-token, and never touches C:\Self Apps outside the read-only
    checks named below.

    WHAT IT CHECKS
      Agent-runnable checks (no protected read; every one below runs from a session with no owner
      token, no -PublishDir and no read under <InstallDir>\data\ -- see PRIVACY BOUNDARY below):
        - /health answers 200 with the expected schema.
        - An unauthenticated POST /api/rooms/{RoomId}/messages is refused (401) and leaves the room's
          message count unchanged.
        - hub.host-configs-sweep-clean: room 'general' holds no `hub`-authored message containing
          "still carries a live credential" created at or after the deployed exe's LastWriteTimeUtc.
          HostConfigs.SweepLiveTokens (Hosting/HubHost.cs) posts exactly that note when a host-config
          rewrite fails at startup, always to 'general' (HubHost.cs:87), regardless of -RoomId, so
          this is the room-observable half of the two data checks row 28 shipped with (deleted here;
          see PRIVACY BOUNDARY). Reads GET /api/rooms/general/messages?afterId=&limit=, paging on
          NextAfterId.
      Owner-only checks (need -OwnerToken; SKIPped with a reason naming the owner when it is omitted):
        - auth.owner-token-post-accepted-201 and its -ipv6 twin: the SAME POST above, carrying
          "Authorization: Bearer <OwnerToken>", is accepted (201) -- the "refused-then-accepted pair"
          the ticket names, and the actual proof the owner can post from the browser after pasting the
          token. This is the owner's own credential; nothing here mints or reads one.
      Bundle checks (need -PublishDir; SKIPped with a reason when it is omitted):
        - The deployed ChopItUp.Hub.exe and wwwroot\ are byte-identical to what -PublishDir staged
          (SHA-256 per file), so the browser is never left on a build that cannot authenticate.

    PRIVACY BOUNDARY -- READ BEFORE RUNNING FROM AN AGENT SESSION
      This repo's own guard denies file reads under `C:\Self Apps\*\data` in an agent session, and
      that deny is correct and permanent. This script reads nothing under <InstallDir>\data\ at all --
      there is no -SkipDataChecks switch because there is nothing left that switch would have skipped;
      the two data checks row 28 shipped with (a plaintext-token scan and a live-token-shape scan, both
      direct file reads under data\) are deleted, and hub.host-configs-sweep-clean above proves the
      same fact -- HostConfigs.SweepLiveTokens ran clean at this start -- over the room API instead of
      a file read. That makes every check above agent-runnable end to end except the two owner-only
      201 legs, which need -OwnerToken (the owner's own credential) and are SKIPped, never faked, when
      it is absent.

    DATABASE BOUNDARY: this script never opens, queries, or PRAGMAs chopitup.db -- every check above is
    an HTTP call against a hub the operator already started, or a file-hash comparison of the deployed
    binaries.

    BINARY LAUNCHES: none. This script starts and stops no process; every check is an HTTP call
    against a hub the operator already started (step 3 above) or a file-hash comparison. There is
    nothing to allowlist.

    CREDENTIAL HANDLING: -OwnerToken, when given, is read from the command line, held only in memory
    for the lifetime of this process, and is never written to the log, to stdout/Write-Host, to any
    file under <InstallDir>, or to a commit. It necessarily passes through the process command line and
    PowerShell history the same way `--rotate-token`'s printed value does (docs/verification.md already
    accepts that trade for a human typing a command by hand); this script adds no further exposure
    beyond that. Omitting -OwnerToken SKIPs the two checks that need it rather than running them with
    an empty credential.

    RESUME SEMANTICS: a PASS is recorded per check name, keyed to a RunId derived from the deployed
    exe's mtime+size plus this harness's version. Re-running after a fresh deploy (new mtime/size)
    forces every check to run again; re-running against the SAME deploy skips checks already PASSed --
    load-bearing for the owner-token POST check, which would otherwise leave a duplicate verification
    message in the real room on every re-run while troubleshooting. Pass -Reset to ignore prior PASS
    rows and force everything to run again regardless of RunId.

.PARAMETER InstallDir
    The real install directory. Defaults to `C:\Self Apps\ChopItUp`.

.PARAMETER OwnerToken
    The value the owner pasted after `--rotate-token owner` (step 4 above). Optional -- omit it to run
    every check except the two owner-only 201 legs, which SKIP with a reason naming the owner. Never
    read from disk, never logged.

.PARAMETER PublishDir
    The staging/publish directory that was actually deployed (what `Deploy-ChopItUp.ps1` printed as
    `staging`, or a fresh `dotnet publish -c Release` output) -- compared file-for-file against
    -InstallDir to prove the deployed bundle is the one that was just published. Optional -- omit it to
    SKIP the two bundle.* legs with a reason.

.PARAMETER Port
    The hub's HTTP port. Defaults to 8790 (HubOptions.DefaultPort; docs/LESSONS.md M8 names this as
    "the live hub's" port).

.PARAMETER RoomId
    The room the 401/201 pair posts against. Defaults to 'general'. hub.host-configs-sweep-clean does
    NOT read this parameter -- it always reads room 'general', since that is where HubHost.cs:87
    posts the sweep-failure note regardless of -RoomId. The 201 leaves one real, clearly-labelled
    verification message in -RoomId -- see RESUME SEMANTICS for why a re-run does not repeat it.

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
    [string]$OwnerToken,
    [string]$PublishDir,
    [int]$Port = 8790,
    [string]$RoomId = 'general',
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
# Agent-runnable checks -- no protected read under <InstallDir>\data\. Independent of each other.
# ======================================================================================================

Invoke-Check -Name 'health.responds-200-expected-schema' -Body {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 10
    $expectedSchema = 13   # ChopDb.LatestSchemaVersion as of row 42 (src/ChopItUp.Core/Storage/ChopDb.cs:10)
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

Invoke-Check -Name 'hub.host-configs-sweep-clean' -Body {
    # HostConfigs.SweepLiveTokens (Hosting/HubHost.cs) runs at every hub start; a rewrite failure never
    # stops the hub (AC4 of row 28 ticket 3) but posts a `hub`-authored note in room 'general' -- the
    # caller (HubHost.cs:87, messages.Post("general", ChopDb.HubParticipantId, ...)) posts there
    # unconditionally, regardless of -RoomId -- whose body contains "still carries a live credential"
    # (ChatApi.MapMessage / GetMessages, Web/ChatApi.cs: 43-46, 145-155). A note from BEFORE this deploy
    # (an old, already-handled failure) does not fail this leg -- only one at or after the deployed
    # exe's LastWriteTimeUtc does, since that is the note this exact start would have posted.
    $needle = 'still carries a live credential'
    $afterId = 0L
    $hasMore = $true
    $scanned = 0
    $hit = $null
    while ($hasMore) {
        $page = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/rooms/general/messages?afterId=$afterId&limit=200" -TimeoutSec 10
        # M10 (docs/LESSONS.md): pipe a top-level JSON array through ForEach-Object { $_ } first.
        $msgs = @($page.messages | ForEach-Object { $_ })
        foreach ($m in $msgs) {
            $scanned++
            if ($m.authorId -eq 'hub' -and $m.body -like "*$needle*") {
                $createdAtUtc = [DateTimeOffset]::Parse($m.createdAt).UtcDateTime
                if ($createdAtUtc -ge $exeInfo.LastWriteTimeUtc) { $hit = $m.id }
            }
        }
        $hasMore = [bool]$page.hasMore
        $afterId = $page.nextAfterId
    }
    @{ Passed = (-not $hit); Detail = "scanned=$scanned exeLastWriteUtc=$($exeInfo.LastWriteTimeUtc.ToString('o')) matchId=$hit" }
}

$ownerTokenSkipReason = if ([string]::IsNullOrWhiteSpace($OwnerToken)) {
    "-OwnerToken not given: rotate is owner-typed only, never agent-run (docs/verification.md 'Rotating a token'); this leg needs the owner's own credential."
} else { $null }

Invoke-Check -Name 'auth.owner-token-post-accepted-201' -SkipReason $ownerTokenSkipReason -Body {
    $headers = @{ Authorization = "Bearer $OwnerToken" }
    $marker = "[Row 28 deploy self-check] $stamp -- verifying the pasted owner token authenticates writes."
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/rooms/$RoomId/messages" -Method Post -Headers $headers -ContentType 'application/json' `
        -Body (@{ body = $marker } | ConvertTo-Json) -TimeoutSec 10 -SkipHttpErrorCheck
    # Detail never carries $OwnerToken -- only the status code.
    @{ Passed = ($resp.StatusCode -eq 201); Detail = "status=$($resp.StatusCode)" }
}

# Row 29 (G-4): the same accepted-write proof, over the browser's own loopback family. `localhost`
# resolves to ::1 first on Windows, so this is what actually proves the peer check has not locked the
# owner out of their own hub when posting from the browser. HubHost only adds the [::1] listener when
# the port is fixed (never for Port 0), which every deployed hub is -- but the probe below still checks
# live rather than assuming, and skips with a reason when it finds nothing listening (the hub itself
# logs "Not listening on [::1]" in exactly that case).
$ipv6SkipReason = $ownerTokenSkipReason
if (-not $ipv6SkipReason) {
    try {
        $ipv6Probe = Invoke-WebRequest -Uri "http://[::1]:$Port/health" -TimeoutSec 3 -SkipHttpErrorCheck
        if ($ipv6Probe.StatusCode -ne 200) { $ipv6SkipReason = "http://[::1]:$Port/health answered $($ipv6Probe.StatusCode), not 200" }
    }
    catch {
        $ipv6SkipReason = "http://[::1]:$Port is not reachable ($($_.Exception.Message)); the hub logs 'Not listening on [::1]' when IPv6 is unavailable"
    }
}

Invoke-Check -Name 'auth.owner-token-post-accepted-201-ipv6' -SkipReason $ipv6SkipReason -Body {
    $headers = @{ Authorization = "Bearer $OwnerToken" }
    $marker = "[Row 28 deploy self-check] $stamp -- verifying the pasted owner token authenticates writes over [::1]."
    $resp = Invoke-WebRequest -Uri "http://[::1]:$Port/api/rooms/$RoomId/messages" -Method Post -Headers $headers -ContentType 'application/json' `
        -Body (@{ body = $marker } | ConvertTo-Json) -TimeoutSec 10 -SkipHttpErrorCheck
    # Detail never carries $OwnerToken -- only the status code.
    @{ Passed = ($resp.StatusCode -eq 201); Detail = "status=$($resp.StatusCode)" }
}

$publishDirSkipReason = if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    "-PublishDir not given: nothing to compare the deployed bundle against."
} else { $null }

Invoke-Check -Name 'bundle.exe-sha256-matches-publish' -SkipReason $publishDirSkipReason -Body {
    $stagingExe = Join-Path $PublishDir 'ChopItUp.Hub.exe'
    if (-not (Test-Path -LiteralPath $stagingExe -PathType Leaf)) {
        return @{ Passed = $false; Detail = "staging exe not found: $stagingExe" }
    }
    $stagingHash = (Get-FileHash -LiteralPath $stagingExe -Algorithm SHA256).Hash
    $installHash = (Get-FileHash -LiteralPath $installExePath -Algorithm SHA256).Hash
    @{ Passed = ($stagingHash -eq $installHash); Detail = "staging=$stagingHash install=$installHash" }
}

Invoke-Check -Name 'bundle.wwwroot-matches-publish' -SkipReason $publishDirSkipReason -Body {
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
