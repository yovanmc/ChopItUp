<#
.SYNOPSIS
    Row 46 dry run: proves the git identity rule, the host trailers and the merge union end to end
    against the built hub, with stub Claude and Codex CLIs and no real model ever reachable.

.DESCRIPTION
    Mirrors Invoke-Row43MentionCheck.ps1's frame: a param block with -HubExe/-ScratchRoot (fresh, under
    $env:TEMP)/-Port/-TimeoutSeconds, ChopTokenHelpers.ps1 seeding 'owner' before the hub's first start,
    a stripped PATH so neither a real claude.exe nor a real codex.exe/codex.cmd is reachable, the hub
    started by PID and stopped by `taskkill /T /F /PID` in a finally block, a summary line and exit 0
    only when every check passes.

    Two stubs, `stub\claude.cmd` and `stub\codex.cmd`, both:

        @echo off
        if exist "%~dp0quiet" exit /b 0
        echo made by the stub> "%CD%\turn-%RANDOM%.txt"
        exit /b 0

    `%CD%` is the worktree (or room directory, on the run path) the hub launched them in; the marker
    file `stub\quiet` is created before the empty-turn leg and removed after, so the same two shims
    cover both "the turn changed a file" and "the turn changed nothing". The hub's own JSON parser sees
    no envelope on stdout from either shim, posts "replied without posting to the room", and still
    commits the tree exactly as row 46 describes — that commit is what every check below reads.

    A scratch directory room ('Lab', id 'lab') is created under $ScratchRoot\rooms (the parent the hub
    itself requires to already exist; the hub `git init`s 'lab' itself), then configured
    `Scratch Owner <scratch-owner@example.test>` by two plain `git config` calls run immediately after
    the 201 — an owner's real repository, from the hub's side, looks exactly like this.

    THREE LEGS, each a barrier on the hub's own notes (`Committed ` then `Exchange #<root> merged
    into`), never a bare timer:
      L1 — an owner edit (`owner.txt`) is written into the room directory, then `@sonnet make a file`
           (a claude-hosted participant) is posted. Checks (a)-(e): the trail note names the file count;
           the three newest commits (merge, turn, owner edit) all carry the repository's own configured
           identity as both author and committer, never the hub's; the turn commit's sole
           `Co-authored-by` trailer is the Claude line; the owner's own edit carries none; the merge
           carries the Claude line too.
      L2 — `@gpt-6-astra make a file` (a codex-hosted participant), with the tree already clean so no
           owner-edit commit intervenes. Checks (f)-(h): the turn and merge trailers are both the Codex
           line, and both commits still carry the repository identity.
      L3 — the `stub\quiet` marker is planted, then `@sonnet do nothing` is posted: the shim exits
           without writing anything, so the turn commit is empty. Checks (i)-(k): the trail note reports
           zero files changed, the empty turn carries no trailer, and neither does the merge (a branch
           whose only commit carries none merges with none — R7).

    Reads git directly against the room directory (`git -C <dir> log --date-order --format=... -n 1
    [--skip=N]`), newest-first: skip 0 is always the merge, skip 1 the turn commit, and (L1 only) skip 2
    the owner's pre-spawn edit.

    Expected: 11 PASS / 0 FAIL (AC9). The orchestrator's own mutation run — `RoomCommits.CoAuthorTrailer`
    made to return null for every host, rebuilt, re-run — is expected to fail exactly the four trailer
    checks (c, e, f, g) and is not part of this script; it is a separate build-mutate-rebuild step this
    script plays no part in.

    Never touches C:\Self Apps or any real data directory: every path this script writes — the hub's
    data dir, the stub PATH dir, the rooms root and the room itself — lives under one fresh
    -ScratchRoot ($env:TEMP\chopitup_row46attrcheck_<guid> by default), removed in the outer finally
    once the hub's own PID (if it ever started) has vanished, on both a clean run and a thrown one.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$ScratchRoot = (Join-Path $env:TEMP ('chopitup_row46attrcheck_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8807,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
$script:Checks = New-Object System.Collections.Generic.List[object]
# A trailing separator would place a bare backslash before the closing quote in Start-Process's
# quoted -ArgumentList entry (same trap Invoke-Row42/43ImportCheck.ps1 guard against).
$ScratchRoot = $ScratchRoot.TrimEnd('\', '/')
$DataDir = Join-Path $ScratchRoot 'data'
$StubDir = Join-Path $ScratchRoot 'stub'
$RoomsRoot = Join-Path $ScratchRoot 'rooms'
$QuietMarker = Join-Path $StubDir 'quiet'
# A sibling of the root, not under it, so the log survives the root's own removal in the finally below.
$log = "$ScratchRoot.row46-attrcheck.log"

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

function Wait-HubNotePrefix {
    param([long]$AfterId, [string]$Prefix, [int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $resp = Invoke-Api -Method Get -Path "/api/rooms/lab/messages?afterId=$AfterId&limit=50"
        $msgs = @($resp.Body.messages | ForEach-Object { $_ })
        $note = $msgs | Where-Object { $_.authorId -eq 'hub' -and $_.body.StartsWith($Prefix) } | Select-Object -First 1
        if ($note) { return $note }
        Start-Sleep -Milliseconds 400
    }
    return $null
}

# Newest-first git log against the room directory: skip 0 is always the merge, skip 1 the turn commit
# it merged, skip 2 (L1 only) an owner-authored pre-spawn edit. Throws on a git failure (infrastructure,
# not a leg outcome) rather than folding it into a check's Detail.
function Get-GitLogField {
    param([string]$Dir, [string]$Format, [int]$Skip = 0)
    $gitArgs = @('-C', $Dir, 'log', '--date-order', "--format=$Format", '-n', '1')
    if ($Skip -gt 0) { $gitArgs += @('--skip', "$Skip") }
    $out = & git @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git log failed in '$Dir' (skip=$Skip, format=$Format): $($out -join ' / ')" }
    return ($out -join "`n")
}

function Get-GitLogFields {
    param([string]$Dir, [string]$Format, [int]$Count)
    $gitArgs = @('-C', $Dir, 'log', '--date-order', "--format=$Format", '-n', "$Count")
    $out = @(& git @gitArgs)
    if ($LASTEXITCODE -ne 0) { throw "git log failed in '$Dir' (count=$Count, format=$Format): $($out -join ' / ')" }
    return $out
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
    Add-Content -Path $log -Value ("Row 46 attribution check {0} hub={1} root={2} port={3}" -f (Get-Date -Format o), $HubExe, $ScratchRoot, $Port)
    Write-Host "Hub: $HubExe"
    Write-Host "Scratch root: $ScratchRoot"

    New-Item -ItemType Directory -Path $DataDir | Out-Null
    New-Item -ItemType Directory -Path $StubDir | Out-Null
    New-Item -ItemType Directory -Path $RoomsRoot | Out-Null

    $stubShim = @'
@echo off
if exist "%~dp0quiet" exit /b 0
echo made by the stub> "%CD%\turn-%RANDOM%.txt"
exit /b 0
'@
    $stubShim | Set-Content -LiteralPath (Join-Path $StubDir 'claude.cmd') -Encoding ascii
    $stubShim | Set-Content -LiteralPath (Join-Path $StubDir 'codex.cmd') -Encoding ascii

    # Row 28: 'owner' is a host-file row -- seed its plaintext into tokens.json AFTER the directories
    # above and BEFORE the hub's first start.
    $script:PlaintextTokens = Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')
    $ownerAuth = New-ChopBearerHeaders -Token $script:PlaintextTokens.owner

    # Fail fast: a stale process already listening on $Port would either make the hub fail to bind or
    # answer /health itself and let every leg run against the wrong process.
    $portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $portProbe.Start() } catch { throw "port $Port is already listening; pick a free -Port or stop whatever is using it." }
    finally { $portProbe.Stop() }

    # ---- hub, stripped PATH: stub\ (claude.cmd, codex.cmd) first, then git's own directory (a room
    # directory needs git for its commit trail; CliResolver.Resolve("git") walks this same PATH), then
    # System32/SystemRoot only -- so neither a real claude nor a real codex is reachable, but the hub's
    # own git usage still works. Restored immediately after Start-Process, in a finally, so the rest of
    # this script keeps git/dotnet. ----
    $gitCmd = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitCmd) { throw "git was not found on this script's own PATH; the hub needs it for every room directory." }
    $gitDir = Split-Path $gitCmd.Source -Parent
    $savedPath = $env:PATH
    try {
        $env:PATH = "$StubDir;$gitDir;$env:SystemRoot\System32;$env:SystemRoot"
        $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port", '--rooms-root', "`"$RoomsRoot`"") -WindowStyle Hidden -PassThru `
            -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    }
    finally { $env:PATH = $savedPath }

    $health = $null
    foreach ($i in 1..40) {
        if ($hub.HasExited) { throw "hub exited early (code $($hub.ExitCode)) while waiting for /health; see $(Join-Path $DataDir 'hub.stderr.log')" }
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($null -eq $health) { throw "hub on port $Port did not become healthy (pid=$($hub.Id))" }

    # A scratch directory room, configured like an owner's real repository right after it exists.
    $roomResp = Invoke-Api -Method Post -Path '/api/rooms' -Headers $ownerAuth -Body @{ name = 'Lab'; directory = (Join-Path $RoomsRoot 'lab') }
    if ($roomResp.Status -ne 201) { throw "room creation failed: status=$($roomResp.Status) body=$($roomResp.Body | ConvertTo-Json -Compress -Depth 4)" }
    $roomDir = $roomResp.Body.directory
    if (-not (Test-Path -LiteralPath $roomDir -PathType Container)) { throw "room directory '$roomDir' was not created" }
    $cfgName = & git -C $roomDir config user.name 'Scratch Owner' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git config user.name failed in '$roomDir': $cfgName" }
    $cfgEmail = & git -C $roomDir config user.email 'scratch-owner@example.test' 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git config user.email failed in '$roomDir': $cfgEmail" }

    $scratchOwnerPair = 'Scratch Owner <scratch-owner@example.test>|Scratch Owner <scratch-owner@example.test>'

    # ---- L1: an owner edit, then a claude-hosted turn that changes a file. ----
    Set-Content -LiteralPath (Join-Path $roomDir 'owner.txt') -Encoding utf8 -Value "written before the spawn`n"
    $post1 = Invoke-Api -Method Post -Path '/api/rooms/lab/messages' -Headers $ownerAuth -Body @{ body = '@sonnet make a file' }
    if ($post1.Status -ne 201) { throw "L1 post failed: status=$($post1.Status)" }
    $root1 = $post1.Body.id
    $committed1 = Wait-HubNotePrefix -AfterId $root1 -Prefix 'Committed ' -Seconds $TimeoutSeconds
    $merged1 = Wait-HubNotePrefix -AfterId $root1 -Prefix "Exchange #$root1 merged into" -Seconds $TimeoutSeconds

    # (a) the trail note names the participant and the file count.
    Add-Check -Name 'L1.a.trail-note-one-file' `
        -Passed ($null -ne $committed1 -and $committed1.body -like '* for sonnet: 1 file(s) changed*') `
        -Detail ($committed1.body ?? '<none>')

    # (b) the three newest commits (merge, turn, owner edit) all carry the repository identity.
    $ident3 = Get-GitLogFields -Dir $roomDir -Format '%an <%ae>|%cn <%ce>' -Count 3
    Add-Check -Name 'L1.b.identity-is-repository-owner' `
        -Passed ($null -ne $merged1 -and $ident3.Count -eq 3 -and ($ident3 | Where-Object { $_ -ne $scratchOwnerPair }).Count -eq 0) `
        -Detail ($ident3 -join ' || ')

    # (c) the turn commit's sole trailer is the Claude line (sonnet is a claude-hosted participant).
    $trailer1Turn = (Get-GitLogField -Dir $roomDir -Format '%(trailers:key=Co-authored-by,valueonly)' -Skip 1).Trim()
    Add-Check -Name 'L1.c.turn-trailer-claude' -Passed ($trailer1Turn -eq 'Claude <noreply@anthropic.com>') -Detail $trailer1Turn

    # (d) an owner's own pre-spawn edit credits nobody.
    $body1Owner = Get-GitLogField -Dir $roomDir -Format '%B' -Skip 2
    Add-Check -Name 'L1.d.owner-edit-no-trailer' -Passed ($body1Owner -notmatch 'Co-authored-by') -Detail ($body1Owner -replace "`n", ' / ')

    # (e) the merge carries the Claude line too.
    $trailer1Merge = (Get-GitLogField -Dir $roomDir -Format '%(trailers:key=Co-authored-by,valueonly)' -Skip 0).Trim()
    Add-Check -Name 'L1.e.merge-trailer-claude' -Passed ($trailer1Merge -eq 'Claude <noreply@anthropic.com>') -Detail $trailer1Merge

    # ---- L2: a codex-hosted turn, tree already clean so no owner-edit commit intervenes. ----
    $post2 = Invoke-Api -Method Post -Path '/api/rooms/lab/messages' -Headers $ownerAuth -Body @{ body = '@gpt-6-astra make a file' }
    if ($post2.Status -ne 201) { throw "L2 post failed: status=$($post2.Status)" }
    $root2 = $post2.Body.id
    $null = Wait-HubNotePrefix -AfterId $root2 -Prefix 'Committed ' -Seconds $TimeoutSeconds
    $merged2 = Wait-HubNotePrefix -AfterId $root2 -Prefix "Exchange #$root2 merged into" -Seconds $TimeoutSeconds

    # (f) the turn commit's trailer is the Codex line.
    $trailer2Turn = (Get-GitLogField -Dir $roomDir -Format '%(trailers:key=Co-authored-by,valueonly)' -Skip 1).Trim()
    Add-Check -Name 'L2.f.turn-trailer-codex' -Passed ($null -ne $merged2 -and $trailer2Turn -eq 'Codex <noreply@openai.com>') -Detail $trailer2Turn

    # (g) the merge carries the Codex line too.
    $trailer2Merge = (Get-GitLogField -Dir $roomDir -Format '%(trailers:key=Co-authored-by,valueonly)' -Skip 0).Trim()
    Add-Check -Name 'L2.g.merge-trailer-codex' -Passed ($trailer2Merge -eq 'Codex <noreply@openai.com>') -Detail $trailer2Merge

    # (h) both commits still carry the repository identity, never the hub's.
    $ident2Turn = Get-GitLogField -Dir $roomDir -Format '%an <%ae>|%cn <%ce>' -Skip 1
    $ident2Merge = Get-GitLogField -Dir $roomDir -Format '%an <%ae>|%cn <%ce>' -Skip 0
    Add-Check -Name 'L2.h.identity-is-repository-owner' `
        -Passed ($ident2Turn -eq $scratchOwnerPair -and $ident2Merge -eq $scratchOwnerPair) `
        -Detail "turn=$ident2Turn merge=$ident2Merge"

    # ---- L3: the shim is silenced, so the turn is empty and credits nobody. ----
    New-Item -ItemType File -Path $QuietMarker | Out-Null
    $post3 = Invoke-Api -Method Post -Path '/api/rooms/lab/messages' -Headers $ownerAuth -Body @{ body = '@sonnet do nothing' }
    if ($post3.Status -ne 201) { throw "L3 post failed: status=$($post3.Status)" }
    $root3 = $post3.Body.id
    $committed3 = Wait-HubNotePrefix -AfterId $root3 -Prefix 'Committed ' -Seconds $TimeoutSeconds
    $merged3 = Wait-HubNotePrefix -AfterId $root3 -Prefix "Exchange #$root3 merged into" -Seconds $TimeoutSeconds
    Remove-Item -LiteralPath $QuietMarker -Force -ErrorAction SilentlyContinue

    # (i) the trail note reports zero files changed.
    Add-Check -Name 'L3.i.trail-note-zero-files' `
        -Passed ($null -ne $committed3 -and $committed3.body -like '* for sonnet: 0 file(s) changed*') `
        -Detail ($committed3.body ?? '<none>')

    # (j) the empty turn carries no trailer.
    $body3Turn = Get-GitLogField -Dir $roomDir -Format '%B' -Skip 1
    Add-Check -Name 'L3.j.empty-turn-no-trailer' -Passed ($null -ne $merged3 -and $body3Turn -notmatch 'Co-authored-by') -Detail ($body3Turn -replace "`n", ' / ')

    # (k) a branch whose only commit carries none merges with none either (R7).
    $body3Merge = Get-GitLogField -Dir $roomDir -Format '%B' -Skip 0
    Add-Check -Name 'L3.k.merge-no-trailer' -Passed ($body3Merge -notmatch 'Co-authored-by') -Detail ($body3Merge -replace "`n", ' / ')

    $hubPid = $hub.Id
    $killProc = Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T', '/F', '/PID', "$hubPid") -PassThru -Wait -NoNewWindow
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $hubPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 300
    }
    $hub = $null   # already ended above; the finally block's own kill-and-wait becomes a no-op
}
finally {
    # Only reached on an early throw where the hub had started but was never ended above (health never
    # came up, a leg's own assertion threw): end it by PID, never by name, and wait the same way.
    if ($hub -and -not $hub.HasExited) {
        Start-Process -FilePath 'taskkill.exe' -ArgumentList @('/T', '/F', '/PID', "$($hub.Id)") -PassThru -Wait -NoNewWindow -ErrorAction SilentlyContinue | Out-Null
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $hub.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 300 }
    }
    # The scratch root, removed once its hub's PID (if any) has vanished. Retried: a vanished PID does
    # not mean every handle under the root (log files, the SQLite file) is closed the same instant.
    $removed = $true
    if (Test-Path -LiteralPath $ScratchRoot) {
        if (-not $ScratchRoot.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to remove ${ScratchRoot}: not under TEMP" }
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
    $failed = $total - $passed
    $line = "Row 46 attribution check: $passed PASS / $failed FAIL"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($failed -eq 0 -and $total -gt 0) { 0 } else { 1 })
