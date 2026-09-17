<#
.SYNOPSIS
    Row 40 dry run: proves the editor routes end to end — list order and caps, an uncut over-cap
    read, CRLF read as LF, the unauthenticated refusal, the preview count, a save with its file,
    backup, carried provenance, note and commit, the stale refusal, the core cap refusal at two
    sizes, the no-heading refusal, shrinking an over-cap topic, a CRLF save, the .bak restore
    committed with the hub stopped, and an empty undecided list — against a fabricated memory
    store on a scratch hub. No model is ever spawned; nothing here spends.

.DESCRIPTION
    Mirrors Invoke-M23DryRun.ps1's frame: a param block with -HubExe/-DataDir (fresh, under
    $env:TEMP)/-Port, Add-Check, ChopTokenHelpers.ps1 seeding 'owner' before the hub's first start,
    the hub started by PID and stopped in a finally block (idempotent; this row also stops it
    inline for the restore leg), "Results: n/m PASS", exit 0 only when every check passes. Every
    Invoke-RestMethod array is piped through ForEach-Object { $_ } first (LESSONS M10: a bare
    top-level JSON array comes back as one nested Object[]).

    The corpus is fabricated on disk before the hub starts: a core with five '## ' entries near
    5,000 characters; topic 'alpha' (six entries, each with a provenance line); topic 'beta' (two
    entries); topic 'huge' (one entry padded past the 24,000-character topic cap); topic 'crlf'
    (two entries written with \r\n line endings). Never touches C:\Self Apps or any real data
    directory: -DataDir defaults to a fresh folder under $env:TEMP and is left behind with the log.
#>
[CmdletBinding()]
param(
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$DataDir = (Join-Path $env:TEMP ('chopitup_row40dryrun_' + [guid]::NewGuid().ToString('N'))),
    [int]$Port = 8806,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
$script:Checks = New-Object System.Collections.Generic.List[object]
$log = "$DataDir.row40-dryrun.log"

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

function New-Provenance([string]$Tag) { "approved 2026-01-01T00:00:00.0000000+00:00 proposal $Tag by owner in room general" }
function New-LiveBlock([string]$Title, [string]$Provenance, [string]$Body) { "`n## $Title`n<!-- $Provenance -->`n$Body`n" }

# A topic built from titles alone, its live entries padded (evenly) to land near -TargetChars —
# Invoke-M23DryRun.ps1's Build-NearCapTopic, generalised over the H1 label.
function Build-PaddedTopic([string]$H1, [string[]]$Titles, [int]$TargetChars) {
    $skeleton = "# $H1`n"
    foreach ($t in $Titles) { $skeleton += New-LiveBlock -Title $t -Provenance (New-Provenance $t) -Body '' }
    $padPer = [Math]::Max(10, [Math]::Floor(($TargetChars - $skeleton.Length) / $Titles.Count))
    $text = "# $H1`n"
    foreach ($t in $Titles) { $text += New-LiveBlock -Title $t -Provenance (New-Provenance $t) -Body ('q' * $padPer) }
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
Add-Content -Path $log -Value ("Row 40 dry run {0} exe={1} data={2} port={3}" -f (Get-Date -Format o), $HubExe, $DataDir, $Port)
Write-Host "Binary: $HubExe"
Write-Host "Data dir: $DataDir"

# Row 28: 'owner' is a host-file row -- seed its plaintext into tokens.json BEFORE the hub's first
# start (ChopTokenHelpers.ps1). Never a real installation's credential.
$script:PlaintextTokens = Initialize-ChopScratchTokens -DataDir $DataDir -ParticipantIds @('owner')
$ownerAuth = New-ChopBearerHeaders -Token $script:PlaintextTokens.owner

$utf8 = New-Object System.Text.UTF8Encoding($false)

# ---- corpus: core near 5,000 chars (five entries); alpha (six, each provenanced); beta (two);
# huge (one entry padded past the 24,000 topic cap); crlf (two, written with \r\n). ----
$coreTitles = 1..5 | ForEach-Object { "Core Fact $_" }
$coreText = Build-PaddedTopic -H1 'Memory' -Titles $coreTitles -TargetChars 5000
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\MEMORY.md'), $coreText, $utf8)

$alphaTitles = 1..6 | ForEach-Object { "Alpha Fact $_" }
$alphaBodyOf = { param($t) "Synthetic fact for $t, fabricated fixture text, not a real conversation." }
$alphaText = "# alpha`n"
foreach ($t in $alphaTitles) { $alphaText += New-LiveBlock -Title $t -Provenance (New-Provenance $t) -Body (& $alphaBodyOf $t) }
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\topics\alpha.md'), $alphaText, $utf8)

$betaTitles = @('Beta Fact 1', 'Beta Fact 2')
$betaText = Build-PaddedTopic -H1 'beta' -Titles $betaTitles -TargetChars 400
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\topics\beta.md'), $betaText, $utf8)

$hugeBody = 'z' * 24500
$hugeText = "# huge`n" + (New-LiveBlock -Title 'Huge Fact' -Provenance (New-Provenance 'huge') -Body $hugeBody)
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\topics\huge.md'), $hugeText, $utf8)

$crlfLf = "# crlf`n" + (New-LiveBlock -Title 'Crlf Fact 1' -Provenance (New-Provenance 'crlf1') -Body 'One, typed in Notepad.') + (New-LiveBlock -Title 'Crlf Fact 2' -Provenance (New-Provenance 'crlf2') -Body 'Two, also CRLF.')
$crlfSeedText = $crlfLf -replace "`n", "`r`n"
[System.IO.File]::WriteAllText((Join-Path $DataDir 'memory\topics\crlf.md'), $crlfSeedText, $utf8)

Add-Content -Path $log -Value ("seed: core={0}b alpha={1}b beta={2}b huge={3}b crlf={4}b" -f $coreText.Length, $alphaText.Length, $betaText.Length, $hugeText.Length, $crlfSeedText.Length)

$base = "http://127.0.0.1:$Port"
$hub = $null
$hubStopped = $false
try {
    # Every path element quoted (2026-09-07 lesson): Start-Process -ArgumentList space-joins its
    # array rather than using ProcessStartInfo.ArgumentList, so an unquoted path containing a space
    # gets word-split by the child process's own argv parser.
    $hub = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$DataDir`"", '--port', "$Port") -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $DataDir 'hub.stderr.log') -RedirectStandardOutput (Join-Path $DataDir 'hub.stdout.log')
    $health = $null
    foreach ($i in 1..40) {
        try { $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    # Infra sanity, not one of the row's 16 legs (the gate is 16/16): a failure here is fatal, so it
    # throws rather than becoming a 17th Add-Check the ticket's count would then never match.
    if ($null -eq $health) { throw "hub on port $Port did not become healthy (pid=$($hub.Id))" }
    if ($health.schema -ne 12) { throw "hub schema is $($health.schema), expected 12" }

    # 1. list.order-and-caps
    $r1 = Invoke-Api -Method Get -Path '/api/memory/topics'
    $list = @($r1.Body | ForEach-Object { $_ })
    $slugs = @($list | ForEach-Object { $_.slug })
    $slugsOk = ($slugs -join ',') -eq 'core,alpha,beta,crlf,huge'
    $restCapsOk = -not (@($list | Select-Object -Skip 1 | ForEach-Object { $_.cap -eq 24000 }) -contains $false)
    $hugeRow = $list | Where-Object slug -eq 'huge'
    Add-Check -Name 'list.order-and-caps' -Passed ($r1.Status -eq 200 -and $slugsOk -and $list[0].cap -eq 6000 -and $restCapsOk -and $hugeRow.chars -gt 24000) `
        -Detail "slugs=$($slugs -join ',') coreCap=$($list[0].cap) hugeChars=$($hugeRow.chars)"

    # 2. get.uncut
    $r2 = Invoke-Api -Method Get -Path '/api/memory/topics/huge'
    Add-Check -Name 'get.uncut' -Passed ($r2.Status -eq 200 -and $r2.Body.text.Length -eq $r2.Body.chars -and $r2.Body.chars -gt 24000 -and $r2.Body.hash -match '^[0-9a-f]{64}$') `
        -Detail "chars=$($r2.Body.chars) hash=$($r2.Body.hash)"

    # 3. get.crlf-as-lf
    $r3 = Invoke-Api -Method Get -Path '/api/memory/topics/crlf'
    $crlfHash = $r3.Body.hash
    $crlfText = $r3.Body.text
    Add-Check -Name 'get.crlf-as-lf' -Passed ($r3.Status -eq 200 -and -not $crlfText.Contains([char]13)) -Detail "containsCR=$($crlfText.Contains([char]13))"

    # 4. put.unauthenticated-401
    $r4 = Invoke-Api -Method Put -Path '/api/memory/topics/alpha' -Body @{ roomId = 'general'; text = "# alpha`n## X`nY`n"; baseHash = 'deadbeef' }
    Add-Check -Name 'put.unauthenticated-401' -Passed ($r4.Status -eq 401) -Detail "status=$($r4.Status)"

    # 5. preview.counts-bookkeeping
    $alphaGet = Invoke-Api -Method Get -Path '/api/memory/topics/alpha'
    $alphaBaseHash = $alphaGet.Body.hash
    $alphaOriginalText = $alphaGet.Body.text
    $alphaNoProv = (($alphaOriginalText -split "`n") | Where-Object { $_ -notmatch '^<!-- ' }) -join "`n"
    $r5 = Invoke-Api -Method Post -Path '/api/memory/topics/alpha/preview' -Headers $ownerAuth -Body @{ roomId = 'general'; text = $alphaNoProv }
    Add-Check -Name 'preview.counts-bookkeeping' -Passed ($r5.Status -eq 200 -and $r5.Body.chars -gt $alphaNoProv.Length -and $r5.Body.over -eq $false) `
        -Detail "sent=$($alphaNoProv.Length) chars=$($r5.Body.chars) over=$($r5.Body.over)"

    # 6. put.alpha-200 — drop 'Alpha Fact 1' outright, keep the rest with their provenance lines deleted.
    $survivingAlphaTitles = $alphaTitles | Where-Object { $_ -ne 'Alpha Fact 1' }
    $editedAlphaLines = New-Object System.Collections.Generic.List[string]
    $editedAlphaLines.Add('# alpha') | Out-Null
    foreach ($t in $survivingAlphaTitles) {
        $editedAlphaLines.Add("## $t") | Out-Null
        $editedAlphaLines.Add((& $alphaBodyOf $t)) | Out-Null
        $editedAlphaLines.Add('') | Out-Null
    }
    $editedAlpha = ($editedAlphaLines -join "`n").TrimEnd() + "`n"
    $r6 = Invoke-Api -Method Put -Path '/api/memory/topics/alpha' -Headers $ownerAuth -Body @{ roomId = 'general'; text = $editedAlpha; baseHash = $alphaBaseHash }
    $alphaProposalId = $r6.Body.proposal.id
    Add-Check -Name 'put.alpha-200' -Passed ($r6.Status -eq 200 -and $r6.Body.proposal.kind -eq 'rewrite' -and $r6.Body.proposal.status -eq 'approved' -and $r6.Body.proposal.source -eq 'editor' `
            -and $r6.Body.proposal.authorId -eq 'owner' -and $r6.Body.proposal.commitHash -match '^[0-9a-f]{7,}$' -and $r6.Body.backup -eq "topics/alpha.md.rewrite-$alphaProposalId.bak") `
        -Detail "status=$($r6.Status) id=$alphaProposalId commit=$($r6.Body.proposal.commitHash) backup=$($r6.Body.backup)"

    # 7. put.alpha-file-bak-and-carry
    $alphaDiskPath = Join-Path $DataDir 'memory\topics\alpha.md'
    $alphaBackupPath = Join-Path $DataDir "memory\topics\alpha.md.rewrite-$alphaProposalId.bak"
    $alphaWrittenOnDisk = [System.IO.File]::ReadAllText($alphaDiskPath, $utf8)
    $alphaBackupOnDisk = if (Test-Path -LiteralPath $alphaBackupPath) { [System.IO.File]::ReadAllText($alphaBackupPath, $utf8) } else { $null }
    $missingProv = @($survivingAlphaTitles | Where-Object { $alphaWrittenOnDisk -notmatch [regex]::Escape("<!-- $(New-Provenance $_) -->") })
    Add-Check -Name 'put.alpha-file-bak-and-carry' -Passed ($alphaWrittenOnDisk -eq $r6.Body.text -and $alphaBackupOnDisk -eq $alphaOriginalText -and $missingProv.Count -eq 0) `
        -Detail "fileMatches=$($alphaWrittenOnDisk -eq $r6.Body.text) bakMatches=$($alphaBackupOnDisk -eq $alphaOriginalText) missingProv=$($missingProv -join ',')"

    # 8. put.alpha-note
    $msgs8 = Invoke-Api -Method Get -Path '/api/rooms/general/messages?afterId=0&limit=200'
    $messages8 = @($msgs8.Body.messages | ForEach-Object { $_ })
    $lastMsg8 = $messages8[-1]
    Add-Check -Name 'put.alpha-note' -Passed ($lastMsg8.authorId -eq 'hub' -and $lastMsg8.body -match "^Memory proposal #$alphaProposalId approved: edited memory/topics/alpha\.md, removing '.+' \(commit [0-9a-f]{7,}\)\.`$") `
        -Detail $lastMsg8.body

    # 9. put.alpha-git — the repository is created lazily by the first approval: exactly one commit.
    $memoryRepo = Join-Path $DataDir 'memory'
    $gitLog9 = & git -C $memoryRepo log --oneline 2>&1
    Add-Check -Name 'put.alpha-git' -Passed (@($gitLog9).Count -eq 1) -Detail (($gitLog9 | Select-Object -First 1) -join '')

    # 10. put.stale-409 — resend leg 6's body with leg 6's ORIGINAL (now stale) hash.
    $r10 = Invoke-Api -Method Put -Path '/api/memory/topics/alpha' -Headers $ownerAuth -Body @{ roomId = 'general'; text = $editedAlpha; baseHash = $alphaBaseHash }
    $alphaAfterStale = [System.IO.File]::ReadAllText($alphaDiskPath, $utf8)
    Add-Check -Name 'put.stale-409' -Passed ($r10.Status -eq 409 -and $r10.Body.error -eq 'The file changed since you opened it. Reload it and apply your edit again.' -and $alphaAfterStale -eq $alphaWrittenOnDisk) `
        -Detail "status=$($r10.Status) error=$($r10.Body.error)"

    # 11. put.core-over-cap-409 — a 409 just above the cap AND far above it; nothing written either time.
    $coreGet = Invoke-Api -Method Get -Path '/api/memory/topics/core'
    $coreHash = $coreGet.Body.hash
    $coreDiskPath = Join-Path $DataDir 'memory\MEMORY.md'
    $coreBeforeText = [System.IO.File]::ReadAllText($coreDiskPath, $utf8)
    $bakCountBefore = @(Get-ChildItem -Path $memoryRepo -Recurse -Filter '*.bak').Count
    $undecidedBefore11 = @((Invoke-Api -Method Get -Path '/api/memory/proposals?room=general&status=all').Body | ForEach-Object { $_ }).Count
    $overCapA = Invoke-Api -Method Put -Path '/api/memory/topics/core' -Headers $ownerAuth -Body @{ roomId = 'general'; text = ("# Memory`n`n## Big`n" + ('y' * 6100) + "`n"); baseHash = $coreHash }
    $overCapB = Invoke-Api -Method Put -Path '/api/memory/topics/core' -Headers $ownerAuth -Body @{ roomId = 'general'; text = ("# Memory`n`n## Big`n" + ('y' * 60000) + "`n"); baseHash = $coreHash }
    $coreAfterText = [System.IO.File]::ReadAllText($coreDiskPath, $utf8)
    $bakCountAfter = @(Get-ChildItem -Path $memoryRepo -Recurse -Filter '*.bak').Count
    $undecidedAfter11 = @((Invoke-Api -Method Get -Path '/api/memory/proposals?room=general&status=all').Body | ForEach-Object { $_ }).Count
    Add-Check -Name 'put.core-over-cap-409' -Passed ($overCapA.Status -eq 409 -and $overCapB.Status -eq 409 -and $overCapA.Body.cap -eq 6000 -and $overCapB.Body.cap -eq 6000 `
            -and $coreAfterText -eq $coreBeforeText -and $bakCountAfter -eq $bakCountBefore -and $undecidedAfter11 -eq $undecidedBefore11) `
        -Detail "statusA=$($overCapA.Status) statusB=$($overCapB.Status) capA=$($overCapA.Body.cap) bakBefore=$bakCountBefore bakAfter=$bakCountAfter"

    # 12. put.no-heading-400
    $betaGet = Invoke-Api -Method Get -Path '/api/memory/topics/beta'
    $r12 = Invoke-Api -Method Put -Path '/api/memory/topics/beta' -Headers $ownerAuth -Body @{ roomId = 'general'; text = 'just prose, no heading at all.'; baseHash = $betaGet.Body.hash }
    Add-Check -Name 'put.no-heading-400' -Passed ($r12.Status -eq 400) -Detail "status=$($r12.Status) error=$($r12.Body.error)"

    # 13. put.huge-shrink-200 — the editor is the one door that can shrink a topic already over its cap.
    $hugeGet13 = Invoke-Api -Method Get -Path '/api/memory/topics/huge'
    $r13 = Invoke-Api -Method Put -Path '/api/memory/topics/huge' -Headers $ownerAuth -Body @{ roomId = 'general'; text = "# huge`n`n## Short`nShort now.`n"; baseHash = $hugeGet13.Body.hash }
    Add-Check -Name 'put.huge-shrink-200' -Passed ($r13.Status -eq 200 -and $r13.Body.chars -lt 200) -Detail "status=$($r13.Status) chars=$($r13.Body.chars)"

    # 14. put.crlf-200 — saved with leg 3's hash, one appended entry; the file on disk has no \r.
    $crlfAppend = $crlfText + "`n## Crlf Fact 3`nThird, appended via a CRLF-sourced save.`n"
    $r14 = Invoke-Api -Method Put -Path '/api/memory/topics/crlf' -Headers $ownerAuth -Body @{ roomId = 'general'; text = $crlfAppend; baseHash = $crlfHash }
    $crlfDiskPath = Join-Path $DataDir 'memory\topics\crlf.md'
    $crlfOnDisk = [System.IO.File]::ReadAllText($crlfDiskPath, $utf8)
    Add-Check -Name 'put.crlf-200' -Passed ($r14.Status -eq 200 -and -not $crlfOnDisk.Contains([char]13)) -Detail "status=$($r14.Status) containsCR=$($crlfOnDisk.Contains([char]13))"

    # Captured BEFORE the stop (leg 16's own claim): no proposal is left undecided at this point —
    # every save so far went straight to approved, so the panel's default filter is empty.
    $undecidedBeforeStop = @((Invoke-Api -Method Get -Path '/api/memory/proposals?room=general').Body | ForEach-Object { $_ })

    # 15. restore.bak-round-trip — stop the hub inline (by the PID this script started), copy leg 6's
    # .bak back over topics/alpha.md, and commit the restore before anything else could sweep it into
    # a later approval's `git add -A` (R10; the doc's own recipe, executed here). The plan's leg text
    # names "two commits" (alpha's, then the restore); by this point legs 13 and 14 have also each
    # written and committed (huge's shrink, crlf's save), so the true pre-restore count is higher —
    # asserted here as "one more than whatever came before", which is what the recipe actually
    # guarantees and holds regardless of how many successful saves preceded it.
    $commitsBeforeRestore = @(& git -C $memoryRepo log --oneline).Count
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue; $hub.WaitForExit(10000) | Out-Null }
    $hubStopped = $true
    Start-Sleep -Milliseconds 300
    Copy-Item -LiteralPath $alphaBackupPath -Destination $alphaDiskPath -Force
    $restoredText = [System.IO.File]::ReadAllText($alphaDiskPath, $utf8)
    $statusBefore15 = (& git -C $memoryRepo status --porcelain) -join "`n"
    & git -C $memoryRepo add -A | Out-Null
    & git -C $memoryRepo commit -m "restore topics/alpha.md from alpha.md.rewrite-$alphaProposalId.bak" | Out-Null
    $statusAfter15 = (& git -C $memoryRepo status --porcelain) -join "`n"
    $gitLog15 = & git -C $memoryRepo log --oneline
    Add-Check -Name 'restore.bak-round-trip' -Passed ($restoredText -eq $alphaOriginalText -and -not [string]::IsNullOrWhiteSpace($statusBefore15) -and [string]::IsNullOrWhiteSpace($statusAfter15) -and @($gitLog15).Count -eq ($commitsBeforeRestore + 1)) `
        -Detail "restored=$($restoredText -eq $alphaOriginalText) dirtyBefore=$(-not [string]::IsNullOrWhiteSpace($statusBefore15)) cleanAfter=$([string]::IsNullOrWhiteSpace($statusAfter15)) commitsBefore=$commitsBeforeRestore commitsAfter=$(@($gitLog15).Count)"

    # 16. proposals.no-undecided — the data gathered above, before the stop.
    Add-Check -Name 'proposals.no-undecided' -Passed ($undecidedBeforeStop.Count -eq 0) -Detail "count=$($undecidedBeforeStop.Count)"
}
finally {
    if ($hub -and -not $hub.HasExited) { Stop-Process -Id $hub.Id -Force -ErrorAction SilentlyContinue }   # idempotent: a no-op after leg 15's own stop
    $passed = @($script:Checks | Where-Object Passed).Count
    $total = $script:Checks.Count
    $line = "Results: $passed/$total PASS"
    Write-Host $line
    Add-Content -Path $log -Value $line
    Write-Host "Log: $log"
}
exit $(if ($passed -eq $total -and $total -gt 0) { 0 } else { 1 })
