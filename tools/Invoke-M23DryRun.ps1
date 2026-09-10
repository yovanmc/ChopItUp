<#
.SYNOPSIS
    Row 23 (T9) dry run: proves the memory-consolidation composition — propose_rewrite, the diff/
    provenance-loss accounting on the approval list, the approve path's file+backup write, the
    duplicate-pending refusal, the non-core cap refusal, and re-approval idempotency — against a
    fabricated, at-scale memory store. No model is ever spawned.

.DESCRIPTION
    Mirrors Invoke-M18MemoryCheck.ps1's frame (param block, Add-Check, a fresh -DataDir under
    $env:TEMP, the hub started by PID and stopped in a finally block, "Results: n/m PASS", exit 0
    only when every check passes) and reuses its Invoke-McpTool helper verbatim. Drives /mcp itself
    as the 'claude' participant (kind=model, host=claude — one of propose_rewrite's permitted
    callers) with a bearer token per LESSONS M11: it never asks any spawned model to do anything.

    ALL corpus data is fabricated by this script directly on disk before the hub starts (12 topics
    of ~20 entries each, one near the 24,000-character topic cap, a core near the 6,000-character
    core cap, ~15% of entries superseded) — the same shape Invoke-M18MemoryCheck.ps1 seeds by hand,
    just at row 23's scale. Never touches C:\Self Apps or any real data directory: -DataDir defaults
    to a fresh folder under $env:TEMP and is left behind with the log.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_m23dryrun_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8804,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.m23-dryrun.log"

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
    $line = ('{0}  {1}  {2}' -f ($(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail))
    Write-Host $line
    Add-Content -Path $log -Value $line
}

# Copied from Invoke-M18MemoryCheck.ps1 verbatim (row 18): drives /mcp itself as the participant
# named. A JSON-RPC error envelope has no result (LESSONS M10 pass 2 P2-8a): surfaced as the
# failure text, never as a silent empty success. A tool-level McpException comes back as
# result.isError = true with the message in result.content, not as a JSON-RPC-level error.
function Invoke-McpTool([string]$Participant, [string]$Tool, [hashtable]$Arguments) {
    $token = $script:PlaintextTokens.$Participant
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json, text/event-stream' }
    $rpc = @{ jsonrpc = '2.0'; id = [guid]::NewGuid().ToString('N'); method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments } } | ConvertTo-Json -Depth 6 -Compress
    $raw = Invoke-WebRequest -Uri "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' -Body $rpc -TimeoutSec $TimeoutSeconds -SkipHttpErrorCheck
    Add-Content -Path $log -Value ("mcp {0} {1} -> {2}: {3}" -f $Participant, $Tool, $raw.StatusCode, ($raw.Content -replace "`r?`n", ' / '))
    $json = if ($raw.Content -match '(?m)^data:\s*(\{.*\})\s*$') { $Matches[1] } else { $raw.Content }   # SSE or plain JSON
    $envelope = $json | ConvertFrom-Json
    if ($envelope.error) { return [pscustomobject]@{ IsError = $true; Text = "$($envelope.error.code): $($envelope.error.message)"; Json = $null } }
    $text = ($envelope.result.content | Where-Object type -eq 'text' | Select-Object -First 1).text
    return [pscustomobject]@{ IsError = [bool]$envelope.result.isError; Text = $text; Json = $(try { $text | ConvertFrom-Json } catch { $null }) }
}

# ---- fixture helpers: build the exact bytes MemoryStore.Entry()/ComposeSupersede() would write,
# by hand, so the seeded files are indistinguishable from a real approval history. ----
function New-Provenance([string]$Tag) { "approved 2026-01-01T00:00:00Z proposal $Tag by owner in room general" }
function New-LiveBlock([string]$Title, [string]$Provenance, [string]$Body) { "`n## $Title`n<!-- $Provenance -->`n$Body`n" }
function New-TombstoneBlock([string]$Title, [string]$Provenance, [string]$SupersededProvenance) { "`n## $Title`n<!-- $Provenance -->`n<!-- superseded: $SupersededProvenance -->`n" }

function Build-GenericTopic([string]$Slug, [int]$Count, [int[]]$SupersededIdx) {
    $text = "# $Slug`n"
    foreach ($i in 1..$Count) {
        $title = "$Slug Fact $i"
        if ($SupersededIdx -contains $i) { $text += New-TombstoneBlock -Title $title -Provenance (New-Provenance "$Slug-$i") -SupersededProvenance (New-Provenance "$Slug-s$i") }
        else { $text += New-LiveBlock -Title $title -Provenance (New-Provenance "$Slug-$i") -Body "Synthetic fact number $i for topic $Slug, fabricated fixture text, not a real conversation." }
    }
    return $text
}

# A topic sized near the 24,000-character cap (plan T9): build the skeleton with empty bodies
# first, measure it, then pad every live entry's body to land close to -TargetChars without going
# over the cap.
function Build-NearCapTopic([string]$Slug, [int]$Count, [int[]]$SupersededIdx, [int]$TargetChars) {
    $skeleton = "# $Slug`n"
    $liveCount = 0
    foreach ($i in 1..$Count) {
        $title = "$Slug Fact $i"
        if ($SupersededIdx -contains $i) { $skeleton += New-TombstoneBlock -Title $title -Provenance (New-Provenance "$Slug-$i") -SupersededProvenance (New-Provenance "$Slug-s$i") }
        else { $skeleton += New-LiveBlock -Title $title -Provenance (New-Provenance "$Slug-$i") -Body ''; $liveCount++ }
    }
    $padPer = [Math]::Max(0, [Math]::Floor(($TargetChars - $skeleton.Length) / $liveCount))
    $text = "# $Slug`n"
    foreach ($i in 1..$Count) {
        $title = "$Slug Fact $i"
        if ($SupersededIdx -contains $i) { $text += New-TombstoneBlock -Title $title -Provenance (New-Provenance "$Slug-$i") -SupersededProvenance (New-Provenance "$Slug-s$i") }
        else { $text += New-LiveBlock -Title $title -Provenance (New-Provenance "$Slug-$i") -Body ('q' * $padPer) }
    }
    return $text
}

if (-not (Test-Path -LiteralPath $HubExe -PathType Leaf)) {
    Write-Error "Hub exe not found at '$HubExe'. Build first, or pass -HubExe." -ErrorAction Continue
    exit 2
}
if (Test-Path -LiteralPath $DataDir) {
    Write-Error "-DataDir '$DataDir' already exists; this script only ever runs against a fresh directory." -ErrorAction Continue
    exit 2
}
New-Item -ItemType Directory -Path (Join-Path $DataDir 'memory\topics') -Force | Out-Null
Add-Content -Path $log -Value ("M23 dry run {0} exe={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)
Write-Host "Binary: $HubExe"
Write-Host "Data dir: $DataDir"

# Row 28: 'claude' (Invoke-McpTool) and 'owner' (the approve calls) are host-file rows -- seed
# plaintexts for them into tokens.json BEFORE the hub's first start (ChopTokenHelpers.ps1). Never a
# real installation's credential.
$script:PlaintextTokens = Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('claude', 'owner')

$utf8 = New-Object System.Text.UTF8Encoding($false)

# ---- corpus: 12 topics, ~20 entries each, ~15% superseded, one near the 24,000 cap; the core near
# the 6,000 cap. topic-01 additionally carries three hand-named entries the consolidation targets:
# "Kept Entry" (survives unchanged), "Old Name" (renamed -> provenance lost), "Dead Fact" (dropped).
$kept = New-LiveBlock -Title 'Kept Entry' -Provenance (New-Provenance '9001') -Body 'This fact must survive consolidation unchanged.'
$oldName = New-LiveBlock -Title 'Old Name' -Provenance (New-Provenance '9002') -Body 'This fact will be renamed by the consolidation.'
$deadFact = New-LiveBlock -Title 'Dead Fact' -Provenance (New-Provenance '9003') -Body 'This fact will be dropped by the consolidation.'
$fillerBlocks = ''
$fillerLiveIdx = @()
foreach ($i in 1..17) {
    $title = "Filler Fact $i"
    if ($i -in 6, 11, 16) { $fillerBlocks += New-TombstoneBlock -Title $title -Provenance (New-Provenance "f$i") -SupersededProvenance (New-Provenance "s$i") }
    else { $fillerBlocks += New-LiveBlock -Title $title -Provenance (New-Provenance "f$i") -Body "Synthetic filler fact number $i for corpus scale. Fabricated fixture text, not a real memory."; $fillerLiveIdx += $i }
}
$topic01Text = "# topic-01`n" + $kept + $oldName + $deadFact + $fillerBlocks
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\topics\topic-01.md'), $topic01Text, $utf8)

foreach ($n in 2..11) {
    $slug = "topic-{0:D2}" -f $n
    $text = Build-GenericTopic -Slug $slug -Count 20 -SupersededIdx @(6, 12, 18)
    [System.IO.File]::WriteAllText((Join-Path $DataDir "memory\topics\$slug.md"), $text, $utf8)
}

$bigText = Build-NearCapTopic -Slug 'topic-12' -Count 20 -SupersededIdx @(6, 12, 18) -TargetChars 23500
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\topics\topic-12.md'), $bigText, $utf8)

$coreFiller = 'x' * 5950
$coreText = "# Memory`n`n$coreFiller`n"
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\MEMORY.md'), $coreText, $utf8)

Add-Content -Path $log -Value ("seed: topic-01={0}b topic-12={1}b core={2}b" -f $topic01Text.Length, $bigText.Length, $coreText.Length)

# The rewrite body for topic-01: keeps "Kept Entry" and every non-superseded filler heading
# byte-identical (so the hub carries their provenance forward), renames "Old Name" -> "New Name"
# (provenance cannot follow a changed heading), drops "Dead Fact" outright, and — per the
# consolidate-memory skill's own rule — writes no comment lines itself.
$rewriteLines = New-Object System.Collections.Generic.List[string]
$rewriteLines.Add('# topic-01') | Out-Null
$rewriteLines.Add('## Kept Entry') | Out-Null
$rewriteLines.Add('This fact must survive consolidation unchanged.') | Out-Null
$rewriteLines.Add('') | Out-Null
$rewriteLines.Add('## New Name') | Out-Null
$rewriteLines.Add('This fact will be renamed by the consolidation.') | Out-Null
$rewriteLines.Add('') | Out-Null
foreach ($i in $fillerLiveIdx) {
    $rewriteLines.Add("## Filler Fact $i") | Out-Null
    $rewriteLines.Add("Synthetic filler fact number $i for corpus scale. Fabricated fixture text, not a real memory.") | Out-Null
    $rewriteLines.Add('') | Out-Null
}
$topic01RewriteBody = ($rewriteLines -join "`n").TrimEnd() + "`n"

$overCapBody = "## Cap Buster`n" + ('z' * 25000)

$base = "http://127.0.0.1:$Port"
$hub = $null
try {
    $git = Get-Command git -ErrorAction SilentlyContinue
    Add-Check -Name 'cli.git-on-path' -Passed ([bool]$git) -Detail ($git.Source ?? 'not found')

    # Every path element quoted (2026-09-07 lesson, reproduced for real while writing the sibling
    # self-check script: Start-Process -ArgumentList space-joins its array rather than using
    # ProcessStartInfo.ArgumentList, so an unquoted path containing a space gets word-split by the
    # child process's own argv parser).
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    Add-Check -Name 'hub.started' -Passed ($null -ne $health) -Detail "pid=$($hub.Id)"
    Add-Check -Name 'health.schema-is-10' -Passed ($health.schema -eq 10) -Detail "schema=$($health.schema)"

    # Row 28: 'claude' and 'owner' were seeded into tokens.json BEFORE this Start-Process call
    # (right after $DataDir was created, below); the file itself now holds only their SHA-256.
    $ownerAuth = New-ChopBearerHeaders -Token $script:PlaintextTokens.owner

    # Leg: the seeded corpus is what the hub sees — 12 topics, topic-01's 17 live titles (3 named +
    # 14 surviving fillers; 3 fillers are tombstoned and correctly absent from Titles()).
    $recall = Invoke-McpTool -Participant 'claude' -Tool 'recall' -Arguments @{}
    $topics = @($recall.Json.topics)
    $topic01 = $topics | Where-Object slug -eq 'topic-01'
    $topic01Titles = @($topic01.titles)
    Add-Check -Name 'seed.twelve-topics' -Passed (-not $recall.IsError -and $topics.Count -eq 12) -Detail "topics=$($topics.Count)"
    Add-Check -Name 'seed.topic01-live-titles' -Passed ($topic01Titles.Count -eq 17 -and ($topic01Titles -contains 'Kept Entry') -and ($topic01Titles -contains 'Old Name') -and ($topic01Titles -contains 'Dead Fact')) `
        -Detail "count=$($topic01Titles.Count)"
    Add-Check -Name 'seed.topic-near-cap' -Passed ($bigText.Length -ge 20000 -and $bigText.Length -lt 24000) -Detail "topic-12=$($bigText.Length) chars, cap=24000"
    Add-Check -Name 'seed.core-near-cap' -Passed ($coreText.Length -ge 5500 -and $coreText.Length -lt 6000) -Detail "core=$($coreText.Length) chars, cap=6000"

    # Leg: propose_rewrite on topic-01 -> a pending "rewrite" proposal.
    $rewriteA = Invoke-McpTool -Participant 'claude' -Tool 'propose_rewrite' -Arguments @{ room_id = 'general'; topic = 'topic-01'; body = $topic01RewriteBody }
    Add-Check -Name 'propose.rewrite' -Passed (-not $rewriteA.IsError -and $rewriteA.Json.kind -eq 'rewrite' -and $null -eq $rewriteA.Json.replaces) -Detail "id=$($rewriteA.Json.id) kind=$($rewriteA.Json.kind)"
    $idA = $rewriteA.Json.id

    # Leg: a second rewrite of the same topic while the first is pending is refused, naming #idA,
    # not silently handed back as a "duplicate" of a different body (pass 2 finding C).
    $rewriteB = Invoke-McpTool -Participant 'claude' -Tool 'propose_rewrite' -Arguments @{ room_id = 'general'; topic = 'topic-01'; body = "# topic-01`n## Something Else`nDifferent body entirely.`n" }
    Add-Check -Name 'propose.second-rewrite-refused' -Passed ($rewriteB.IsError -and $rewriteB.Text -match "already pending \(#$idA\)") -Detail $rewriteB.Text

    # Leg: a body that composes past the cap at an ORDINARY (non-core) topic is refused, not only
    # at core (claim 5 / pass 2 finding A).
    $overCap = Invoke-McpTool -Participant 'claude' -Tool 'propose_rewrite' -Arguments @{ room_id = 'general'; topic = 'topic-02'; body = $overCapBody }
    Add-Check -Name 'propose.over-cap-refused-non-core' -Passed ($overCap.IsError -and $overCap.Text -match '24000') -Detail $overCap.Text

    # Leg: the pending rewrite's list entry carries the diff, removed/added titles and the
    # provenance-loss count (T5) - all before any approval.
    $pendingList = @(Invoke-RestMethod -Uri "$base/api/memory/proposals?room=general&status=pending" -TimeoutSec 10 | ForEach-Object { $_ })
    $rowA = $pendingList | Where-Object id -eq $idA
    $diff = @($rowA.diff)
    $removed = @($rowA.removedTitles)
    $added = @($rowA.addedTitles)
    Add-Check -Name 'list.diff-non-empty' -Passed ($diff.Count -gt 0 -and (@($diff | Where-Object op -eq 'add').Count -gt 0) -and (@($diff | Where-Object op -eq 'del').Count -gt 0)) `
        -Detail "lines=$($diff.Count) add=$(@($diff | Where-Object op -eq 'add').Count) del=$(@($diff | Where-Object op -eq 'del').Count)"
    Add-Check -Name 'list.removed-titles' -Passed (($removed -contains 'Old Name') -and ($removed -contains 'Dead Fact')) -Detail "removed=$($removed -join ',')"
    Add-Check -Name 'list.added-titles' -Passed ($added -contains 'New Name') -Detail "added=$($added -join ',')"
    Add-Check -Name 'list.provenance-lost' -Passed ($rowA.provenanceLost -ge 2) -Detail "provenanceLost=$($rowA.provenanceLost)"
    Add-Check -Name 'list.git-available' -Passed ($rowA.gitAvailable -eq [bool]$git) -Detail "gitAvailable=$($rowA.gitAvailable)"

    # Snapshot the pre-approval bytes straight off disk - this is what the per-proposal backup must
    # match exactly.
    $topic01Path = Join-Path $DataDir 'memory\topics\topic-01.md'
    $preBytes = [System.IO.File]::ReadAllText($topic01Path, $utf8)

    # Leg: approve - the file is replaced whole, the backup is written, written_to and a commit hash
    # are recorded.
    $approved = Invoke-RestMethod -Uri "$base/api/memory/proposals/$idA/approve" -Method Post -Headers $ownerAuth -TimeoutSec $TimeoutSeconds
    Add-Check -Name 'approve.rewrite' -Passed ($approved.status -eq 'approved' -and $approved.kind -eq 'rewrite' -and $null -ne $approved.writtenTo) `
        -Detail "status=$($approved.status) kind=$($approved.kind) writtenTo=$($approved.writtenTo) commitHash=$($approved.commitHash)"

    $postBytes = [System.IO.File]::ReadAllText($topic01Path, $utf8)
    $keptProvenanceLine = "<!-- $(New-Provenance '9001') -->"
    Add-Check -Name 'approve.file-changed' -Passed ($postBytes -ne $preBytes -and $postBytes -notmatch '(?m)^## Old Name$' -and $postBytes -match '(?m)^## New Name$' -and $postBytes -notmatch '(?m)^## Dead Fact$') `
        -Detail "len before=$($preBytes.Length) after=$($postBytes.Length)"
    Add-Check -Name 'approve.provenance-carried' -Passed ($postBytes.Contains($keptProvenanceLine) -and $postBytes -match '(?m)^## Kept Entry$') -Detail 'Kept Entry keeps its original provenance comment'

    $backupPath = "$topic01Path.rewrite-$idA.bak"
    $backupBytes = if (Test-Path -LiteralPath $backupPath) { [System.IO.File]::ReadAllText($backupPath, $utf8) } else { $null }
    Add-Check -Name 'approve.backup-matches-prestate' -Passed ($null -ne $backupBytes -and $backupBytes -eq $preBytes) -Detail "backup=$backupPath"

    $gitLog = & git -C (Join-Path $DataDir 'memory') log --oneline 2>&1
    Add-Check -Name 'approve.one-commit' -Passed (@($gitLog).Count -eq 1) -Detail (($gitLog | Select-Object -First 1) -join '')

    # Leg: re-approval is a no-op - the row is already approved+written, the second call is refused
    # and touches neither the file nor the backup.
    $reapprove = Invoke-WebRequest -Uri "$base/api/memory/proposals/$idA/approve" -Method Post -Headers $ownerAuth -TimeoutSec $TimeoutSeconds -SkipHttpErrorCheck
    $postBytes2 = [System.IO.File]::ReadAllText($topic01Path, $utf8)
    $backupBytes2 = [System.IO.File]::ReadAllText($backupPath, $utf8)
    Add-Check -Name 'approve.reapprove-noop' -Passed ($reapprove.StatusCode -eq 409 -and $postBytes2 -eq $postBytes -and $backupBytes2 -eq $backupBytes) `
        -Detail "status=$($reapprove.StatusCode)"
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
