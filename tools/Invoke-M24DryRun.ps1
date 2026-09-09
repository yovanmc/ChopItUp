<#
.SYNOPSIS
    Row 24 (M24) synthetic-corpus dry run (HIGH gate): drives the REAL built `ChopItUp.Hub.exe
    --export-memory` verb against fabricated, scratch-only corpora and asserts only what the tool
    controls -- its exit codes and the `EXPORT_RESULT:` line -- never a prose message a later edit
    could reword (M11).

.DESCRIPTION
    See docs/superpowers/plans/m24-memory-v1-1c-export.md, "T6 -- dry run, self-check, docs", and
    .scratch/m24-memory-v1-1c-export/issues/06-dry-run-and-the-owner-probe.md.

    Every path this script touches is rooted under a fresh $env:TEMP scratch directory with a GUID
    nonce -- never a real memory directory, never a real `.claude` directory. `Assert-ScratchOnly`
    is the load-bearing refusal: it is called before every `--data` or `--export-memory` argument
    this script builds, and throws rather than trusting the caller if a computed path ever resolved
    outside $env:TEMP or through a `.claude` path segment.

    Every argument to the real hub exe is quoted by passing it as its own element of an
    `-ArgumentList` array (never a hand-joined string), and every path is
    `[IO.Path]::TrimEndingDirectorySeparator`-ed before it is added to that array -- the 2026-09-07
    lesson (pass 2 M15b): a trailing backslash immediately before a closing quote escapes the quote
    and corrupts argv, and HubOptions.Parse's own trim for `--export-memory` runs AFTER argv is
    already built, so it cannot fix a corruption that happened on the way in.

    Fixtures are written directly as the markdown MemoryStore.ParseEntries reads (`# <topic>` then
    one `## <title>` / `<!-- provenance -->` / body block per entry) -- the same shape
    MemoryStore.Append produces -- so the real hub exe reads a real memory store, not a mock of one.

.PARAMETER KeepEvidence
    Keep the scratch directory (fixtures, exported directories, hub stdout/stderr logs) instead of
    deleting it at the end. Prints the directory path either way.
#>
[CmdletBinding()]
param(
    [switch]$KeepEvidence
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot 'ChopItUp.slnx'
$hubExe = Join-Path $repoRoot 'src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'

$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_m24dryrun_$nonce"
New-Item -ItemType Directory -Path $scratch | Out-Null
$scratchFull = [IO.Path]::GetFullPath($scratch)

# --- Evidence log: one PASS/FAIL line per check (M2/M4 idiom) -------------------------------------
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

# --- Safety: never touch anything outside this run's own scratch root, and never anything under a
# real `.claude` directory, no matter what a caller passes in (T6 acceptance: "refuses to operate on
# a real memory directory" / "refuses a target under a real .claude directory rather than trusting
# the caller"). -------------------------------------------------------------------------------------
function Assert-ScratchOnly {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($scratchFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing: '$full' is not under this run's own scratch root '$scratchFull'. This script only ever touches its own TEMP-rooted scratch directory."
    }
    if ($full -match '(?i)(^|\\)\.claude(\\|$)') {
        throw "Refusing: '$full' looks like a real Claude Code directory (contains a '.claude' path segment). Never point this script at a real memory or config directory."
    }
}

# --- Fixture writer: hand-writes MemoryStore's own on-disk shape directly (no dependency on the
# hub or a running store) -- '# <topic>' header, then one '## <title>' / provenance / body block per
# live entry, exactly what MemoryStore.Append produces and MemoryStore.ParseEntries reads. --------
function New-MemoryFixture {
    param(
        [Parameter(Mandatory)][string]$MemoryDir,
        [Parameter(Mandatory)][string[]]$TopicSlugs,
        [Parameter(Mandatory)][int]$EntriesPerTopic,
        [int]$CoreEntries = 2,
        [int]$BulkTopicEntries = 0
    )
    Assert-ScratchOnly -Path $MemoryDir
    $topicsDir = Join-Path $MemoryDir 'topics'
    New-Item -ItemType Directory -Path $topicsDir -Force | Out-Null
    $enc = New-Object System.Text.UTF8Encoding($false)

    $core = New-Object System.Text.StringBuilder
    [void]$core.Append("# Memory`n`nFabricated core seed for the M24 dry run (row 24 T6). Never real data.`n")
    for ($i = 1; $i -le $CoreEntries; $i++) {
        [void]$core.Append("`n## Core Fact $i`n<!-- dry-run fixture -->`nBody text for core fact $i.`n")
    }
    [System.IO.File]::WriteAllText((Join-Path $MemoryDir 'MEMORY.md'), $core.ToString(), $enc)

    foreach ($slug in $TopicSlugs) {
        $count = if ($BulkTopicEntries -gt 0) { $BulkTopicEntries } else { $EntriesPerTopic }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append("# $slug`n")
        for ($e = 1; $e -le $count; $e++) {
            [void]$sb.Append("`n## Fact $e`n<!-- dry-run fixture -->`nBody text for topic $slug fact $e.`n")
        }
        [System.IO.File]::WriteAllText((Join-Path $topicsDir "$slug.md"), $sb.ToString(), $enc)
    }
}

# --- Drives the real hub exe. Every path is trimmed of a trailing separator BEFORE being placed in
# the -ArgumentList array; each array element is its own argv entry (never a hand-joined string), so
# quoting is never manual. -------------------------------------------------------------------------
function Invoke-HubExport {
    param(
        [Parameter(Mandatory)][string]$DataDir,
        [Parameter(Mandatory)][string]$TargetDir,
        [switch]$Force,
        [switch]$AcceptNewSource,
        [Parameter(Mandatory)][string]$LogTag
    )
    Assert-ScratchOnly -Path $DataDir
    Assert-ScratchOnly -Path $TargetDir

    $dataArg = [IO.Path]::TrimEndingDirectorySeparator($DataDir)
    $targetArg = [IO.Path]::TrimEndingDirectorySeparator($TargetDir)

    $argList = [System.Collections.Generic.List[string]]::new()
    $argList.Add('--data'); $argList.Add($dataArg)
    $argList.Add('--export-memory'); $argList.Add($targetArg)
    if ($Force) { $argList.Add('--force') }
    if ($AcceptNewSource) { $argList.Add('--accept-new-source') }

    $outLog = Join-Path $scratch "$LogTag.out.log"
    $errLog = Join-Path $scratch "$LogTag.err.log"
    $proc = Start-Process -FilePath $hubExe -ArgumentList $argList -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    [pscustomobject]@{
        ExitCode = $proc.ExitCode
        StdOut   = (Get-Content -LiteralPath $outLog -Raw -ErrorAction SilentlyContinue)
        StdErr   = (Get-Content -LiteralPath $errLog -Raw -ErrorAction SilentlyContinue)
    }
}

# --- The one machine-readable line this tool controls (M11): parse it, never the surrounding prose.
function Get-ExportResult {
    param([string]$StdOut)
    if ([string]::IsNullOrEmpty($StdOut)) { return $null }
    $line = ($StdOut -split "`r?`n") | Where-Object { $_.StartsWith('EXPORT_RESULT: ') } | Select-Object -Last 1
    if (-not $line) { return $null }
    return ($line.Substring('EXPORT_RESULT: '.Length) | ConvertFrom-Json)
}

# --- D2/AC6's affected-paths list is a tested, byte-stable machine contract ("  " + relative path
# per line under "affected paths:") -- checked against THAT line shape, never against the sentence
# wording around it, which a later edit is free to reword. ------------------------------------------
function Test-AffectedPathsContains {
    param([string]$Text, [string]$RelativePath)
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    $needle = "  $RelativePath"
    return (($Text -split "`n") | Where-Object { $_.TrimEnd("`r") -eq $needle }).Count -gt 0
}

$exitCode = 1

try {
    # --- Build once ----------------------------------------------------------------------------
    Write-Host "Building $slnPath (Debug, -warnaserror)..."
    & dotnet build $slnPath -c Debug -warnaserror -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $hubExe)) { throw "Expected hub exe not found at '$hubExe' after build." }

    # =============================================================================================
    # Scenario 1: a store of 12 topics; export; file count and index line count.
    # =============================================================================================
    $topicSlugs = 1..12 | ForEach-Object { "topic-{0:d2}" -f $_ }
    $entriesPerTopic = 4
    $coreEntries = 2
    $dataDir1 = Join-Path $scratch 's1\data'
    New-MemoryFixture -MemoryDir (Join-Path $dataDir1 'memory') -TopicSlugs $topicSlugs -EntriesPerTopic $entriesPerTopic -CoreEntries $coreEntries
    $expectedTotal1 = $coreEntries + ($topicSlugs.Count * $entriesPerTopic)   # 2 + 12*4 = 50

    $target1 = Join-Path $scratch 's1\export'
    $r1 = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target1 -LogTag 's1'
    Add-Check -Name 's1.exit-zero' -Passed ($r1.ExitCode -eq 0) -Detail "exit=$($r1.ExitCode)"
    $result1 = Get-ExportResult -StdOut $r1.StdOut
    Add-Check -Name 's1.export-result-present' -Passed ($null -ne $result1) -Detail ($r1.StdOut)
    if ($null -ne $result1) {
        Add-Check -Name 's1.exported-count-matches-fixture' -Passed ($result1.exported -eq $expectedTotal1) -Detail "expected=$expectedTotal1 actual=$($result1.exported)"
    }
    $fileCount1 = @(Get-ChildItem -LiteralPath $target1 -File -Filter '*.md' | Where-Object { $_.Name -ne 'MEMORY.md' }).Count
    Add-Check -Name 's1.file-count-matches-fixture' -Passed ($fileCount1 -eq $expectedTotal1) -Detail "expected=$expectedTotal1 actual=$fileCount1"
    $indexPath1 = Join-Path $target1 'MEMORY.md'
    $indexTrimmed1 = (Get-Content -LiteralPath $indexPath1 -Raw).Trim()
    $indexLineCount1 = ($indexTrimmed1 -split "`n").Count
    $expectedIndexLines1 = $expectedTotal1 + 2   # '# Memories' + blank line, then one line per file
    Add-Check -Name 's1.index-line-count' -Passed ($indexLineCount1 -eq $expectedIndexLines1) -Detail "expected=$expectedIndexLines1 actual=$indexLineCount1"

    # =============================================================================================
    # Scenario 2: three exports in a row against the same target all exit 0, exactly one reusable
    # previous-export directory afterward (AC12, claim 22/pass 2 B1: the THIRD run is the one that
    # dies on Directory.Move onto an existing destination if step 3 is missing).
    # =============================================================================================
    $target2 = Join-Path $scratch 's2\export'
    $reusablePrevious2 = $target2 + '.chopitup-export-previous'
    $threeExitCodes = @()
    for ($i = 1; $i -le 3; $i++) {
        $r = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target2 -LogTag "s2-run$i"
        $threeExitCodes += $r.ExitCode
    }
    Add-Check -Name 's2.three-runs-all-exit-zero' -Passed (($threeExitCodes | Where-Object { $_ -ne 0 }).Count -eq 0) -Detail "exits=[$($threeExitCodes -join ', ')]"
    Add-Check -Name 's2.exactly-one-reusable-previous' -Passed (Test-Path -LiteralPath $reusablePrevious2 -PathType Container) -Detail $reusablePrevious2
    $timestampedGlob2 = (Split-Path -Leaf $target2) + '.chopitup-export-previous-*'
    $timestampedCount2 = @(Get-ChildItem -LiteralPath (Split-Path -Parent $target2) -Directory -Filter $timestampedGlob2 -ErrorAction SilentlyContinue).Count
    Add-Check -Name 's2.no-timestamped-previous' -Passed ($timestampedCount2 -eq 0) -Detail "count=$timestampedCount2"

    # =============================================================================================
    # Scenario 3+4: hand-edit an exported file -> refusal names it; --force replaces it and the
    # edited file survives in a TIMESTAMPED previous (Drifted is never the reusable-name case, D8).
    # =============================================================================================
    $target3 = Join-Path $scratch 's3\export'
    $r3a = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target3 -LogTag 's3-initial'
    Add-Check -Name 's3.initial-export-exit-zero' -Passed ($r3a.ExitCode -eq 0) -Detail "exit=$($r3a.ExitCode)"

    $editedRelPath = 'topic-01-fact-1.md'
    $editedFull = Join-Path $target3 $editedRelPath
    Add-Check -Name 's3.edited-file-exists-before-edit' -Passed (Test-Path -LiteralPath $editedFull -PathType Leaf) -Detail $editedFull
    $editedContent = "--- hand-edited by the M24 dry run, never a real memory ---`n"
    [System.IO.File]::WriteAllText($editedFull, $editedContent, (New-Object System.Text.UTF8Encoding($false)))

    $r3b = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target3 -LogTag 's3-drifted-no-force'
    Add-Check -Name 's3.drifted-refusal-exit-six' -Passed ($r3b.ExitCode -eq 6) -Detail "exit=$($r3b.ExitCode)"
    Add-Check -Name 's3.refusal-names-edited-file' -Passed (Test-AffectedPathsContains -Text $r3b.StdErr -RelativePath $editedRelPath) -Detail $r3b.StdErr

    $r3c = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target3 -Force -LogTag 's3-forced'
    Add-Check -Name 's4.force-replace-exit-zero' -Passed ($r3c.ExitCode -eq 0) -Detail "exit=$($r3c.ExitCode)"
    $result3c = Get-ExportResult -StdOut $r3c.StdOut
    $timestampedPrev3 = $null
    if ($null -ne $result3c -and $result3c.previous_dir) {
        $timestampedPrev3 = $result3c.previous_dir
        Add-Check -Name 's4.previous-dir-is-timestamped' -Passed ($timestampedPrev3 -match [regex]::Escape($target3) + '\.chopitup-export-previous-\d{8}T\d{6}Z$') -Detail $timestampedPrev3
        $rescuedFile = Join-Path $timestampedPrev3 $editedRelPath
        $rescuedContent = if (Test-Path -LiteralPath $rescuedFile) { Get-Content -LiteralPath $rescuedFile -Raw } else { $null }
        Add-Check -Name 's4.edited-file-survives-in-timestamped-previous' -Passed ($rescuedContent -eq $editedContent) -Detail $rescuedFile
    }
    else {
        Add-Check -Name 's4.previous-dir-is-timestamped' -Passed $false -Detail 'EXPORT_RESULT carried no previous_dir'
        Add-Check -Name 's4.edited-file-survives-in-timestamped-previous' -Passed $false -Detail 'skipped: no previous_dir'
    }

    # =============================================================================================
    # Scenario 5: exporting a DIFFERENT store into a target Clean and bound to store 1's root
    # refuses naming both roots; --force cannot override it (D9, not re-asserted here -- T3's own
    # tests own that); --accept-new-source proceeds and prints the (possibly empty) drift list
    # through the same formatter a plain refusal would use (D2/AC6).
    #
    # Uses its OWN fresh target5, never target3: target3 already carries a TIMESTAMPED previous-
    # export directory from scenario 4 (its name is a whole-second UTC timestamp), and this run's own
    # override also lands in the timestamped case (DifferentSource is never the reusable slot). Two
    # independent replacements of the SAME target landing on the SAME target within the same UTC
    # second collide on that name and `Directory.Move` throws onto an existing destination -- observed
    # directly while authoring this script (a real, reproducible finding against the already-landed
    # T3 code, reported separately; not this task's file to fix). A fresh target's first-ever
    # timestamped move cannot collide with a pre-existing directory of that name, so scenario 5 is
    # deliberately independent of target3 to stay deterministic.
    # =============================================================================================
    $dataDir5 = Join-Path $scratch 's5\data'
    New-MemoryFixture -MemoryDir (Join-Path $dataDir5 'memory') -TopicSlugs @('other-topic') -EntriesPerTopic 3 -CoreEntries 1
    $storeRoot1 = [IO.Path]::GetFullPath((Join-Path $dataDir1 'memory'))
    $storeRoot5 = [IO.Path]::GetFullPath((Join-Path $dataDir5 'memory'))

    $target5 = Join-Path $scratch 's5\export'
    $r5init = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target5 -LogTag 's5-initial'
    Add-Check -Name 's5.initial-export-exit-zero' -Passed ($r5init.ExitCode -eq 0) -Detail "exit=$($r5init.ExitCode)"

    $r5a = Invoke-HubExport -DataDir $dataDir5 -TargetDir $target5 -LogTag 's5-different-source'
    Add-Check -Name 's5.different-source-refusal-exit-six' -Passed ($r5a.ExitCode -eq 6) -Detail "exit=$($r5a.ExitCode)"
    Add-Check -Name 's5.refusal-names-both-roots' -Passed ($r5a.StdErr -match [regex]::Escape($storeRoot1) -and $r5a.StdErr -match [regex]::Escape($storeRoot5)) -Detail $r5a.StdErr

    $r5b = Invoke-HubExport -DataDir $dataDir5 -TargetDir $target5 -AcceptNewSource -LogTag 's5-accept-new-source'
    Add-Check -Name 's5.accept-new-source-exit-zero' -Passed ($r5b.ExitCode -eq 0) -Detail "exit=$($r5b.ExitCode)"
    Add-Check -Name 's5.accept-new-source-printed-drift-list' -Passed ($r5b.StdOut -match 'affected paths:') -Detail $r5b.StdOut

    # =============================================================================================
    # Scenario 6: a corrupted manifest refuses naming the override (--force) and the exit code.
    # =============================================================================================
    $target6 = Join-Path $scratch 's6\export'
    $r6a = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target6 -LogTag 's6-initial'
    Add-Check -Name 's6.initial-export-exit-zero' -Passed ($r6a.ExitCode -eq 0) -Detail "exit=$($r6a.ExitCode)"
    $manifestPath6 = Join-Path $target6 '.chopitup-export.json'
    [System.IO.File]::WriteAllText($manifestPath6, 'not valid json {{{', (New-Object System.Text.UTF8Encoding($false)))

    $r6b = Invoke-HubExport -DataDir $dataDir1 -TargetDir $target6 -LogTag 's6-corrupt-manifest'
    Add-Check -Name 's6.corrupt-manifest-refusal-exit-six' -Passed ($r6b.ExitCode -eq 6) -Detail "exit=$($r6b.ExitCode)"
    Add-Check -Name 's6.refusal-names-force-override' -Passed ($r6b.StdErr -match [regex]::Escape('--force')) -Detail $r6b.StdErr

    # =============================================================================================
    # Scenario 7: a store whose rendered index crosses the cap exits 6, not 3 (the render refusal
    # is the FIRST step, before the target is ever looked at -- so the target must stay absent).
    # =============================================================================================
    $dataDir7 = Join-Path $scratch 's7\data'
    New-MemoryFixture -MemoryDir (Join-Path $dataDir7 'memory') -TopicSlugs @('bulk') -EntriesPerTopic 1 -CoreEntries 0 -BulkTopicEntries 250
    $target7 = Join-Path $scratch 's7\export'
    $r7 = Invoke-HubExport -DataDir $dataDir7 -TargetDir $target7 -LogTag 's7'
    Add-Check -Name 's7.over-cap-exit-six-not-three' -Passed ($r7.ExitCode -eq 6) -Detail "exit=$($r7.ExitCode)"
    Add-Check -Name 's7.target-not-created' -Passed (-not (Test-Path -LiteralPath $target7)) -Detail $target7

    # =============================================================================================
    # Scenario 8: a data directory with no memory directory exits 4 and creates nothing.
    # =============================================================================================
    $dataDir8 = Join-Path $scratch 's8\data'
    New-Item -ItemType Directory -Path $dataDir8 | Out-Null   # the data dir exists; 'memory' under it deliberately does not
    $target8 = Join-Path $scratch 's8\export'
    $r8 = Invoke-HubExport -DataDir $dataDir8 -TargetDir $target8 -LogTag 's8'
    Add-Check -Name 's8.no-memory-dir-exit-four' -Passed ($r8.ExitCode -eq 4) -Detail "exit=$($r8.ExitCode)"
    Add-Check -Name 's8.target-not-created' -Passed (-not (Test-Path -LiteralPath $target8)) -Detail $target8
    Add-Check -Name 's8.no-memory-dir-was-not-created' -Passed (-not (Test-Path -LiteralPath (Join-Path $dataDir8 'memory'))) -Detail (Join-Path $dataDir8 'memory')

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $exitCode = 1
}
finally {
    $logPath = Join-Path $scratch 'm24-dryrun.log'
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
