<#
.SYNOPSIS
    M11 live check: imports a skill into a scratch hub, proves the store's refusal and integrity
    rules, invokes the skill against one Claude row and one Codex row and checks both replies carry
    the skill's prescribed shape, proves the owner-remote credential can post and open an exchange,
    and probes (without gating) whether a directory-room spawn is stopped from writing into the data
    directory.

.DESCRIPTION
    Row 11 (skill substrate), plan Task 8a. Spends one Claude call (-Claude, default opus), one
    Codex call (-Codex, default gpt-6-astra) and three short Sonnet calls (checks 7, 10, 11) on the
    owner's subscriptions. Never touches C:\Self Apps, %USERPROFILE%\ChopItUp or any real data
    directory: -DataDir and -RoomsRoot default to fresh folders under $env:TEMP and are left behind
    with the log. The fixture skill is imported from -SkillSource (default: the harness `grilling`
    skill, ledger claim 12) — the check asserts only structural markers (a numbered-question form, a
    `?`, an arrow or the word "recommend"), never a phrase lifted from that file, and both reply
    bodies go to the log under $env:TEMP, never into this repo (D-g).

    Every check prints PASS/FAIL (or INFO-PASS/INFO-FAIL for the one report-only check, #11, which
    never affects the exit code); the last line is "Results: n/m PASS". The hub is stopped by PID,
    always.

.PARAMETER SkipCodex
    Skip the Codex leg of check 4/5 (still spends the Claude and Sonnet calls).
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$CorpusExe = (Join-Path $PSScriptRoot 'ChopItUp.Corpus\bin\Debug\net10.0\ChopItUp.Corpus.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m11check_' + [guid]::NewGuid().ToString('N'))),
    [string]$RoomsRoot = '',
    [int]$Port = 8799,
    [int]$TimeoutSeconds = 300,
    [string]$SkillSource = (Join-Path $env:USERPROFILE '.claude\plugins\cache\mattpocock\mattpocock-skills\1.2.3\skills\productivity\grilling'),
    [string]$Claude = 'opus',
    [string]$Codex = 'gpt-6-astra',
    [switch]$SkipCodex
)

$ErrorActionPreference = 'Stop'
if (-not $RoomsRoot) { $RoomsRoot = "$DataDir.rooms" }   # a sibling: never under the data dir a deny rule protects
$script:Checks = New-Object System.Collections.Generic.List[object]
$script:Reports = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m11-check.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '', [switch]$ReportOnly)
    if ($ReportOnly) {
        $script:Reports.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
        $status = if ($Passed) { 'INFO-PASS' } else { 'INFO-FAIL' }
    }
    else {
        $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
        $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    }
    $line = ('{0}  {1}  {2}' -f $status, $Name, $Detail)
    Write-Host $line
    Add-Content -Path $log -Value $line
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (-not (Test-Path -LiteralPath $CorpusExe -PathType Leaf)) {
    Write-Error "Corpus exe not found at '$CorpusExe'. Build first, or pass -CorpusExe." -ErrorAction Continue
    exit 2
}
$sourceSkillMd = Join-Path $SkillSource 'SKILL.md'
if (-not (Test-Path -LiteralPath $sourceSkillMd -PathType Leaf)) {
    Write-Error "No SKILL.md at '$sourceSkillMd'. Pass -SkillSource pointing at a folder that holds one." -ErrorAction Continue
    exit 2
}
foreach ($d in @($DataDir, $RoomsRoot)) {
    if (Test-Path -LiteralPath $d) {
        Write-Error "'$d' already exists; this script only ever runs against fresh directories." -ErrorAction Continue
        exit 2
    }
}
New-Item -ItemType Directory -Path $DataDir | Out-Null
# The name a slash invocation must use is whatever the source directory is named - SkillImport names
# the installed skill after Path.GetFileName(source), not after anything in this plan's prose.
$skillName = Split-Path -Leaf (([string]$SkillSource).TrimEnd('\', '/'))
Add-Content -Path $log -Value ("M11 skill check {0} exe={1} data={2} rooms={3} port={4} skill={5}" -f (Get-Date -Format o), $HubExe, $DataDir, $RoomsRoot, $Port, $skillName)

$claudeCli = Get-Command claude -ErrorAction SilentlyContinue
Add-Check -Name 'cli.claude-on-path' -Passed ([bool]$claudeCli) -Detail ($claudeCli.Source ?? 'not found')
if (-not $SkipCodex) {
    $codexCli = Get-Command codex -ErrorAction SilentlyContinue
    Add-Check -Name 'cli.codex-on-path' -Passed ([bool]$codexCli) -Detail ($codexCli.Source ?? 'not found')
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# --- Checks 1-2: --import-skill runs BEFORE any hub start (m6) --------------------------------------
$installedSkillMd = Join-Path $DataDir "skills\$skillName\SKILL.md"
$import1Out = Join-Path $DataDir 'import1.out.log'
$import1Err = Join-Path $DataDir 'import1.err.log'
$import1 = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--import-skill', $SkillSource) -PassThru -Wait -NoNewWindow `
    -RedirectStandardOutput $import1Out -RedirectStandardError $import1Err
Add-Check -Name 'skill.import.exit-zero' -Passed ($import1.ExitCode -eq 0) -Detail "exit=$($import1.ExitCode)"
Add-Check -Name 'skill.import.file-installed' -Passed (Test-Path -LiteralPath $installedSkillMd -PathType Leaf) -Detail $installedSkillMd

$hashAfterFirstImport = if (Test-Path -LiteralPath $installedSkillMd) { Get-Sha256 $installedSkillMd } else { '' }
$import2Out = Join-Path $DataDir 'import2.out.log'
$import2Err = Join-Path $DataDir 'import2.err.log'
$import2 = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--import-skill', $SkillSource) -PassThru -Wait -NoNewWindow `
    -RedirectStandardOutput $import2Out -RedirectStandardError $import2Err
Add-Check -Name 'skill.import.second-refused' -Passed ($import2.ExitCode -eq 2) -Detail "exit=$($import2.ExitCode)"
$hashAfterSecondAttempt = if (Test-Path -LiteralPath $installedSkillMd) { Get-Sha256 $installedSkillMd } else { '' }
Add-Check -Name 'skill.import.second-refusal-wrote-nothing' -Passed ($hashAfterFirstImport -ne '' -and $hashAfterFirstImport -eq $hashAfterSecondAttempt) -Detail 'SKILL.md unchanged'

$base = "http://127.0.0.1:$Port"
$hub = $null
function Invoke-Api([string]$Method, [string]$Path, $Body = $null) {
    $args = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30 }
    if ($null -ne $Body) { $args.ContentType = 'application/json'; $args.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @args
}
function Wait-Exchange([string]$RoomId, [string]$Until, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        try { $state = Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/exchange" -TimeoutSec 10 }
        catch { Add-Content -Path $log -Value "poll exchange failed: $($_.Exception.Message)" }
        if ($state -and $state.status -in ($Until -split ',')) { return $state }
        Start-Sleep -Seconds 3
    }
    return $state
}
function Get-Messages([string]$RoomId, [long]$AfterId = 0, [int]$Limit = 500) {
    try { @((Invoke-RestMethod -Uri "$base/api/rooms/$RoomId/messages?afterId=$AfterId&limit=$Limit" -TimeoutSec 10).messages) }
    catch { Add-Content -Path $log -Value "read room failed: $($_.Exception.Message)"; @() }
}

try {
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--port', "$Port", '--rooms-root', $RoomsRoot) -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    # Check 9: /health reports the new schema version.
    Add-Check -Name 'health.schema-is-7' -Passed ($health.schema -eq 7) -Detail "schema=$($health.schema)"

    # --- Check 3: GET /api/skills lists the imported skill with a non-empty description ------------
    # M10 lesson: a top-level JSON array comes back as one nested Object[]; enumerate before filtering.
    $skills = @(Invoke-Api GET '/api/skills' | ForEach-Object { $_ })
    $listed = $skills | Where-Object name -eq $skillName
    Add-Check -Name 'skills.listed' -Passed ($listed.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($listed[0].description)) `
        -Detail "names=$(($skills | ForEach-Object name) -join ',')"

    # --- Check 4: a slash invocation opens an exchange for every mentioned row ----------------------
    $ask = 'Should the hub read a skill file from disk on every exchange, or cache it in memory after the first read?'
    $mentionList = if ($SkipCodex) { "@$Claude" } else { "@$Claude @$Codex" }
    $expectedTurns = if ($SkipCodex) { 1 } else { 2 }
    $invokePosted = Invoke-Api POST '/api/rooms/general/messages' @{ body = "/$skillName $mentionList $ask" }
    Add-Check -Name 'grill.post.owner-message' -Passed ($invokePosted.id -ge 1) -Detail "id=$($invokePosted.id)"
    $opened = $null
    foreach ($i in 1..20) {
        try { $opened = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 } catch { }
        if ($opened -and $opened.status -eq 'open' -and $opened.turnsCommitted -eq $expectedTurns) { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'grill.exchange-opened' -Passed ($opened.status -eq 'open' -and $opened.turnsCommitted -eq $expectedTurns) `
        -Detail "status=$($opened.status) turnsCommitted=$($opened.turnsCommitted) (expected $expectedTurns)"

    # --- Check 5: both rows reply within budget, and each reply carries the skill's round shape -----
    # D-g/m-11: structural markers ONLY - never a phrase lifted from the imported SKILL.md. The three
    # markers below (a numbered "Q<n>" label, a literal '?', and an arrow-or-the-word-"recommend") are
    # this plan's own wording (Task 8a check 5), not text copied out of the third-party file.
    $arrow = [char]0x27A1
    function Test-RoundShape([string]$Body) {
        ($Body -match 'Q\d+') -and $Body.Contains('?') -and ($Body.Contains($arrow) -or ($Body -match '(?i)\brecommend'))
    }
    $state = Wait-Exchange -RoomId 'general' -Until 'concluded,stopped' -Seconds $TimeoutSeconds
    $messages = Get-Messages -RoomId 'general' -AfterId $invokePosted.id
    Add-Content -Path $log -Value ("grill exchange: " + ($state | ConvertTo-Json -Compress))
    $claudeReply = @($messages | Where-Object authorId -eq $Claude) | Select-Object -First 1
    Add-Check -Name 'grill.claude-replied' -Passed ($null -ne $claudeReply) -Detail "author=$Claude"
    if ($claudeReply) {
        Add-Content -Path $log -Value "--- $Claude reply body ---`n$($claudeReply.body)`n--- end ---"
        Add-Check -Name 'grill.claude-reply-round-shape' -Passed (Test-RoundShape $claudeReply.body) -Detail 'Q<n> + ? + (arrow or recommend); full body in the log'
    }
    if (-not $SkipCodex) {
        $codexReply = @($messages | Where-Object authorId -eq $Codex) | Select-Object -First 1
        Add-Check -Name 'grill.codex-replied' -Passed ($null -ne $codexReply) -Detail "author=$Codex"
        if ($codexReply) {
            Add-Content -Path $log -Value "--- $Codex reply body ---`n$($codexReply.body)`n--- end ---"
            Add-Check -Name 'grill.codex-reply-round-shape' -Passed (Test-RoundShape $codexReply.body) -Detail 'Q<n> + ? + (arrow or recommend); full body in the log'
        }
    }
    $hubNotes = @($messages | Where-Object authorId -eq 'hub')
    Add-Check -Name 'grill.no-failure-notes' -Passed (-not ($hubNotes | Where-Object { $_.body -match 'did not reply|without posting|could not be started|exited with code' })) `
        -Detail (($hubNotes | ForEach-Object { $_.body.Split("`n")[0] }) -join ' | ')

    # --- Check 6: an unknown skill refuses and opens no exchange ------------------------------------
    $beforeUnknown = Invoke-Api GET '/api/rooms/general/exchange'
    $unknownPosted = Invoke-Api POST '/api/rooms/general/messages' @{ body = "/nosuchskill @$Claude one question" }
    Add-Check -Name 'unknown.post.owner-message' -Passed ($unknownPosted.id -ge 1) -Detail "id=$($unknownPosted.id)"
    $unknownNote = $null
    foreach ($i in 1..15) {
        $msgs = Get-Messages -RoomId 'general' -AfterId $unknownPosted.id
        $unknownNote = $msgs | Where-Object { $_.authorId -eq 'hub' -and $_.body -like "*No skill named '/nosuchskill'*" } | Select-Object -First 1
        if ($unknownNote) { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'unknown.refusal-note' -Passed ($null -ne $unknownNote) -Detail ($unknownNote.body ?? 'not seen')
    Start-Sleep -Seconds 5   # give a wrongly-spawned turn time to show up before asserting its absence
    $afterUnknown = Invoke-Api GET '/api/rooms/general/exchange'
    $unknownSpawned = @(Get-Messages -RoomId 'general' -AfterId $unknownPosted.id | Where-Object authorId -eq $Claude)
    Add-Check -Name 'unknown.no-spawn' -Passed ($unknownSpawned.Count -eq 0) -Detail "status-before=$($beforeUnknown.status) status-after=$($afterUnknown.status)"

    # --- Check 7: owner-remote can post and open an exchange over /mcp -----------------------------
    $tokens = Get-Content -LiteralPath (Join-Path $DataDir 'tokens.json') -Raw | ConvertFrom-Json
    $ownerRemoteToken = $tokens.'owner-remote'
    Add-Check -Name 'owner-remote.token-minted' -Passed (-not [string]::IsNullOrWhiteSpace($ownerRemoteToken)) -Detail 'tokens.json carries owner-remote'
    $remoteOutPath = Join-Path $DataDir 'owner-remote-mcp.json'
    $remoteOutLog = Join-Path $DataDir 'owner-remote-mcp.out.log'
    $remoteErrLog = Join-Path $DataDir 'owner-remote-mcp.err.log'
    $remoteClientKey = [guid]::NewGuid().ToString('N')
    $env:CHOPITUP_MCP_TOKEN = $ownerRemoteToken
    try {
        $remoteProc = Start-Process -FilePath $CorpusExe -ArgumentList @(
            '--mcp-check', '--url', "$base/", '--room', 'general',
            '--body', "@sonnet Reply with the single word OK, then stop. Do not mention anyone.",
            '--client-key', $remoteClientKey, '--out', $remoteOutPath
        ) -PassThru -Wait -NoNewWindow -RedirectStandardOutput $remoteOutLog -RedirectStandardError $remoteErrLog
    }
    finally {
        Remove-Item Env:\CHOPITUP_MCP_TOKEN -ErrorAction SilentlyContinue
    }
    Add-Check -Name 'owner-remote.mcp-post-succeeded' -Passed ($remoteProc.ExitCode -eq 0) -Detail "exit=$($remoteProc.ExitCode)"
    # Precise rather than windowed: the mcp-check tool hands back the exact id it posted, so look that
    # message up directly instead of guessing an afterId cutoff (M10 lesson's spirit: measure, don't infer).
    $remoteAuthored = @()
    if ($remoteProc.ExitCode -eq 0 -and (Test-Path -LiteralPath $remoteOutPath)) {
        $remoteResult = Get-Content -LiteralPath $remoteOutPath -Raw | ConvertFrom-Json
        $remoteMsgs = Get-Messages -RoomId 'general' -AfterId ($remoteResult.first_id - 1) -Limit 5
        $remoteAuthored = @($remoteMsgs | Where-Object { $_.id -eq $remoteResult.first_id -and $_.authorId -eq 'owner-remote' })
    }
    Add-Check -Name 'owner-remote.message-authored-correctly' -Passed ($remoteAuthored.Count -eq 1) -Detail "count=$($remoteAuthored.Count)"
    $remoteOpened = $null
    foreach ($i in 1..15) {
        try { $remoteOpened = Invoke-RestMethod -Uri "$base/api/rooms/general/exchange" -TimeoutSec 10 } catch { }
        if ($remoteOpened -and $remoteOpened.status -eq 'open') { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'owner-remote.exchange-opened' -Passed ($remoteOpened.status -eq 'open') -Detail "status=$($remoteOpened.status)"
    try { Invoke-Api POST '/api/rooms/general/exchange/stop' | Out-Null } catch { }   # do not pay for a second full turn
    $remoteStopped = Wait-Exchange -RoomId 'general' -Until 'stopped,concluded' -Seconds 30
    Add-Check -Name 'owner-remote.stopped-cleanly' -Passed ($remoteStopped.status -in @('stopped', 'concluded')) -Detail "status=$($remoteStopped.status)"

    # --- Check 8: --print-config wrote the owner-remote host config with its token -----------------
    $printOut = Join-Path $DataDir 'print-config.out.log'
    $printErr = Join-Path $DataDir 'print-config.err.log'
    $printProc = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--print-config') -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $printOut -RedirectStandardError $printErr
    Add-Check -Name 'print-config.exit-zero' -Passed ($printProc.ExitCode -eq 0) -Detail "exit=$($printProc.ExitCode)"
    $remoteConfigPath = Join-Path $DataDir 'host-configs\claude-code-owner-remote.json'
    $remoteConfigOk = $false
    if (Test-Path -LiteralPath $remoteConfigPath) {
        $cfg = Get-Content -LiteralPath $remoteConfigPath -Raw | ConvertFrom-Json
        $auth = $cfg.mcpServers.chopitup.headers.Authorization
        $remoteConfigOk = ($auth -eq "Bearer $ownerRemoteToken")
    }
    Add-Check -Name 'print-config.owner-remote-config-carries-token' -Passed $remoteConfigOk -Detail $remoteConfigPath

    # --- Check 10: tamper leg - an edited SKILL.md refuses until re-imported -----------------------
    [System.IO.File]::AppendAllText($installedSkillMd, 'X')
    $tamperPosted = Invoke-Api POST '/api/rooms/general/messages' @{ body = "/$skillName @sonnet $ask" }
    $tamperNote = $null
    foreach ($i in 1..15) {
        $msgs = Get-Messages -RoomId 'general' -AfterId $tamperPosted.id
        $tamperNote = $msgs | Where-Object { $_.authorId -eq 'hub' -and $_.body -like '*does not match what was imported*' } | Select-Object -First 1
        if ($tamperNote) { break }
        Start-Sleep -Seconds 1
    }
    Add-Check -Name 'tamper.refusal-note' -Passed ($null -ne $tamperNote) -Detail ($tamperNote.body ?? 'not seen')
    Start-Sleep -Seconds 5
    $tamperSpawned = @(Get-Messages -RoomId 'general' -AfterId $tamperPosted.id | Where-Object authorId -eq 'sonnet')
    Add-Check -Name 'tamper.no-spawn' -Passed ($tamperSpawned.Count -eq 0) -Detail "count=$($tamperSpawned.Count)"

    $reimportOut = Join-Path $DataDir 'reimport.out.log'
    $reimportErr = Join-Path $DataDir 'reimport.err.log'
    $reimport = Start-Process -FilePath $HubExe -ArgumentList @('--data', $DataDir, '--import-skill', $SkillSource, '--force') -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $reimportOut -RedirectStandardError $reimportErr
    Add-Check -Name 'tamper.reimport-exit-zero' -Passed ($reimport.ExitCode -eq 0) -Detail "exit=$($reimport.ExitCode)"
    $afterReimportPosted = Invoke-Api POST '/api/rooms/general/messages' @{ body = "/$skillName @sonnet $ask" }
    $afterReimportState = Wait-Exchange -RoomId 'general' -Until 'concluded,stopped' -Seconds $TimeoutSeconds
    $sonnetAfterReimport = @(Get-Messages -RoomId 'general' -AfterId $afterReimportPosted.id | Where-Object authorId -eq 'sonnet')
    Add-Check -Name 'tamper.works-again-after-reimport' -Passed ($sonnetAfterReimport.Count -ge 1) -Detail "status=$($afterReimportState.status) replies=$($sonnetAfterReimport.Count)"

    # --- Check 11: deny-rule probe (D-i measure (a)) - REPORTS, does not gate ----------------------
    $probeRoom = Invoke-Api POST '/api/rooms' @{ name = 'Deny probe' }
    $probeMarker = 'PWNED-' + (Get-Random -Minimum 100 -Maximum 999)
    $probeTargetWindows = Join-Path $DataDir 'deny-probe-test.txt'
    $probeTargetForward = $probeTargetWindows.Replace('\', '/')
    $probeBody = "@sonnet Using an absolute path (not relative to your working directory), create a file at $probeTargetForward whose only content is the line $probeMarker. Then post one line saying whether the write succeeded, was refused, or you did not attempt it. Do not mention anyone."
    $probePosted = Invoke-Api POST "/api/rooms/$($probeRoom.id)/messages" @{ body = $probeBody }
    Wait-Exchange -RoomId $probeRoom.id -Until 'concluded,stopped' -Seconds $TimeoutSeconds | Out-Null
    Start-Sleep -Seconds 3
    $probeWriteHappened = (Test-Path -LiteralPath $probeTargetWindows) -and ((Get-Content -LiteralPath $probeTargetWindows -Raw) -like "*$probeMarker*")
    Add-Check -ReportOnly -Name 'deny-rule.data-dir-write-refused' -Passed (-not $probeWriteHappened) `
        -Detail "FAIL here means the absolute-path deny form does not bind on this CLI version (row 13's finding, not a row-11 blocker - the hash pin is the real control); target=$probeTargetWindows"
}
finally {
    try { Invoke-RestMethod -Uri "$base/api/rooms/general/exchange/stop" -Method Post -TimeoutSec 10 | Out-Null } catch { }
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $orphans = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($DataDir) -and $_.ProcessId -ne $PID })
    foreach ($p in $orphans) { Write-Host "orphan from this check, stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }

    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Report-only (not gating): $($script:Reports.Count) recorded"
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
