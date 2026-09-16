<#
.SYNOPSIS
    Row 26 owner probe: does a running Claude Code session actually read an exported
    `autoMemoryDirectory`, filter on `metadata.type`, and honour the vendor's 200-line/25,000-unit
    index cap. Drives the REAL built `ChopItUp.Hub.exe --export-memory` against a fabricated,
    scratch-only 198-entry store, then drives the REAL `claude -p` CLI against the export three (or
    four) times and records what each session says it can see.

.DESCRIPTION
    See .scratch/m26-memory-probe/brief.md (lite path, row 26). Modelled on
    tools/Invoke-M24DryRun.ps1: every path is rooted under a fresh $env:TEMP scratch directory with a
    GUID nonce, `Assert-ScratchOnly` runs before every path this script touches, and every path handed
    to a child process is `[IO.Path]::TrimEndingDirectorySeparator`-ed first.

    Deviation from M24, measured this run (see report): `Start-Process -ArgumentList <string[]>` does
    NOT give each array element its own argv slot on this host — it joins the array with spaces into a
    single `ProcessStartInfo.Arguments` string, which the child process's own argv parser then
    re-splits, silently dropping embedded double-quote characters and re-splitting a "one element, has
    spaces" token into several. Confirmed with a `node -e` echo-args probe: `'{"a":"b"}'` arrived as
    `{a:b}` (quotes stripped) and `'hello world'` arrived as two args. This breaks passing
    `--json-schema` (which needs literal embedded quotes) as a bare array element. Fix used here:
    `ConvertTo-Argv` pre-escapes each logical argument into a single, self-quoting Windows argv token
    (the same algorithm .NET's `ProcessStartInfo.ArgumentList` uses internally) before it goes into the
    array `Start-Process -ArgumentList` receives — so the call site still reads as an array of logical
    arguments, never a hand-joined string, and the actual bytes on the wire are correct regardless of
    what `-ArgumentList` does with them.

    This script does NOT build the hub exe. It only ever touches its own scratch directory, and it
    never runs `dotnet build` (the brief: never rebuild while a Debug hub might be running) — if the
    exe is missing it stops with a clear message instead of guessing.

.PARAMETER KeepEvidence
    Keep the scratch directory (fixture stores, exports, claude stdout/stderr logs, settings files)
    instead of deleting it at the end. Prints the directory path either way.
#>
[CmdletBinding()]
param(
    [switch]$KeepEvidence
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hubExe = Join-Path $repoRoot 'src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'
if (-not (Test-Path -LiteralPath $hubExe -PathType Leaf)) {
    throw "Expected hub exe not found at '$hubExe'. This script does not build it (brief: never rebuild while a Debug hub might be running) -- build it first, then re-run."
}

$claudeCmd = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claudeCmd) { throw "'claude' was not found on PATH." }
$claudeExe = $claudeCmd.Source

$nonce = [guid]::NewGuid().ToString('N')
$scratch = Join-Path $env:TEMP "chopitup_row26_$nonce"
New-Item -ItemType Directory -Path $scratch | Out-Null
$scratchFull = [IO.Path]::GetFullPath($scratch)

# --- Evidence log: PASS/FAIL lines for what this script controls, INFO lines for measured answers
# (the brief's own idiom: a nonce being absent is data, not a defect in this script). -----------------
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

function Add-Info {
    param([Parameter(Mandatory)][string]$Name, [string]$Detail = '')
    $line = "INFO  $Name  $Detail".TrimEnd()
    $checkLines.Add($line)
    Write-Host $line
}

# --- Safety: refuse any path outside this run's own scratch root, and any path with a '.claude'
# segment, no matter what a caller or a computed value passes in (M24's own guard, same reasoning). ---
function Assert-ScratchOnly {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($scratchFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing: '$full' is not under this run's own scratch root '$scratchFull'."
    }
    if ($full -match '(?i)(^|\\)\.claude(\\|$)') {
        throw "Refusing: '$full' looks like a real Claude Code directory (contains a '.claude' path segment)."
    }
}

# --- Pre-escapes one logical argument into a single, self-quoting Windows argv token (same algorithm
# .NET's ProcessStartInfo.ArgumentList uses internally -- CommandLineToArgvW's own escaping rules:
# a '"' is escaped as '\"' after doubling any backslashes immediately before it, and a run of trailing
# backslashes right before the closing quote is doubled too). Measured necessary on this host because
# Start-Process -ArgumentList does not do this itself (see .DESCRIPTION). --------------------------
function ConvertTo-Argv {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Arg)
    if ($Arg -eq '') { return '""' }
    if ($Arg -notmatch '[\s"]') { return $Arg }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $backslashes = 0
    foreach ($ch in $Arg.ToCharArray()) {
        if ($ch -eq '\') { $backslashes++; continue }
        if ($ch -eq '"') {
            [void]$sb.Append('\' * ($backslashes * 2 + 1))
            [void]$sb.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) { [void]$sb.Append('\' * $backslashes); $backslashes = 0 }
        [void]$sb.Append($ch)
    }
    if ($backslashes -gt 0) { [void]$sb.Append('\' * ($backslashes * 2)) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

# --- The same slug algorithm MemoryImport.Slugify uses (verified against
# src/ChopItUp.Hub/Memory/MemoryImport.cs:128-139), so this script can predict an exported file's name
# without parsing the hub's output. -----------------------------------------------------------------
function ConvertTo-ExportSlug {
    param([Parameter(Mandatory)][string]$S)
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $S.ToLowerInvariant().ToCharArray()) {
        if (($ch -ge 'a' -and $ch -le 'z') -or ($ch -ge '0' -and $ch -le '9')) { [void]$sb.Append($ch) }
        elseif ($sb.Length -gt 0 -and $sb[$sb.Length - 1] -ne '-') { [void]$sb.Append('-') }
    }
    $slug = $sb.ToString().Trim('-')
    if ($slug.Length -gt 64) { $slug = $slug.Substring(0, 64).TrimEnd('-') }
    return $slug
}

function New-Nonce { ([guid]::NewGuid().ToString('N')).Substring(0, 8) }

# --- Builds the source store: 198 live entries across 6 topics (feedback, filler, project,
# reference, room-general, user -- the brief's exact list), each entry carrying a unique nonce in its
# TITLE (so it lands on the rendered index line) and a DIFFERENT nonce on the second line of its BODY
# (so it exists only in that entry's exported topic file, never in the index). Zero core entries, so
# the export's live-entry count is exactly the topic total. Returns the landmark nonces/paths later
# steps and legs need. --------------------------------------------------------------------------------
function New-Row26Fixture {
    param([Parameter(Mandatory)][string]$MemoryDir)
    Assert-ScratchOnly -Path $MemoryDir
    $topicsDir = Join-Path $MemoryDir 'topics'
    New-Item -ItemType Directory -Path $topicsDir -Force | Out-Null
    $enc = New-Object System.Text.UTF8Encoding($false)

    # Core: prose only, no '## ' heading, so it contributes zero live entries.
    [System.IO.File]::WriteAllText((Join-Path $MemoryDir 'MEMORY.md'),
        "# Memory`n`nRow 26 probe fixture. Never real data.`n", $enc)

    $topics = @('feedback', 'filler', 'project', 'reference', 'room-general', 'user')
    $entriesPerTopic = 33   # 6 * 33 = 198

    $seenNonces = New-Object System.Collections.Generic.HashSet[string]
    $entriesByTopic = @{}

    foreach ($topic in $topics) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append("# $topic`n")
        $list = New-Object System.Collections.Generic.List[object]
        for ($e = 1; $e -le $entriesPerTopic; $e++) {
            $titleNonce = New-Nonce
            $bodyNonce = New-Nonce
            if (-not $seenNonces.Add($titleNonce)) { throw "nonce collision on title nonce '$titleNonce' (guid substring collided -- re-run)." }
            if (-not $seenNonces.Add($bodyNonce)) { throw "nonce collision on body nonce '$bodyNonce' (guid substring collided -- re-run)." }
            $title = "F$e $titleNonce"
            [void]$sb.Append("`n## $title`n<!-- row26 fixture -->`nhook $topic $e.`nnonce: $bodyNonce`n")
            $fileName = "$topic-" + (ConvertTo-ExportSlug -S $title) + ".md"
            $list.Add([pscustomobject]@{ Title = $title; TitleNonce = $titleNonce; BodyNonce = $bodyNonce; ExportFileName = $fileName })
        }
        [System.IO.File]::WriteAllText((Join-Path $topicsDir "$topic.md"), $sb.ToString(), $enc)
        $entriesByTopic[$topic] = $list
    }

    [pscustomobject]@{
        MemoryDir     = $MemoryDir
        Topics        = $topics
        EntriesByTopic = $entriesByTopic
        TotalEntries  = $topics.Count * $entriesPerTopic
        UserLandmark  = $entriesByTopic['user'][0]
        RoomLandmark  = $entriesByTopic['room-general'][0]
    }
}

# --- Drives the real hub exe's --export-memory verb. Plain path/flag arguments only (never a value
# containing a space or a quote), so this mirrors M24's own Start-Process usage directly -- the
# ConvertTo-Argv escaping this script needed for the claude legs is not needed here, but is applied
# anyway for uniformity and because it is a no-op on an argument with no space or quote. -------------
function Invoke-HubExport {
    param([Parameter(Mandatory)][string]$DataDir, [Parameter(Mandatory)][string]$TargetDir, [Parameter(Mandatory)][string]$LogTag)
    Assert-ScratchOnly -Path $DataDir
    Assert-ScratchOnly -Path $TargetDir

    $dataArg = [IO.Path]::TrimEndingDirectorySeparator($DataDir)
    $targetArg = [IO.Path]::TrimEndingDirectorySeparator($TargetDir)
    $rawArgs = @('--data', $dataArg, '--export-memory', $targetArg)
    $escaped = $rawArgs | ForEach-Object { ConvertTo-Argv -Arg $_ }

    $outLog = Join-Path $scratch "$LogTag.out.log"
    $errLog = Join-Path $scratch "$LogTag.err.log"
    $proc = Start-Process -FilePath $hubExe -ArgumentList $escaped -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    [pscustomobject]@{
        ExitCode = $proc.ExitCode
        StdOut   = (Get-Content -LiteralPath $outLog -Raw -ErrorAction SilentlyContinue)
        StdErr   = (Get-Content -LiteralPath $errLog -Raw -ErrorAction SilentlyContinue)
    }
}

function Get-ExportResult {
    param([string]$StdOut)
    if ([string]::IsNullOrEmpty($StdOut)) { return $null }
    $line = ($StdOut -split "`r?`n") | Where-Object { $_.StartsWith('EXPORT_RESULT: ') } | Select-Object -Last 1
    if (-not $line) { return $null }
    return ($line.Substring('EXPORT_RESULT: '.Length) | ConvertFrom-Json)
}

# --- The structured-output schema every leg asks for (single line: JSON.Schema validation, per
# `claude --help`'s own --json-schema example shape). 'present'/'absent' carry the exact nonce
# strings the prompt gave the session; 'error_seen' is free text or "". ------------------------------
$schema = '{"type":"object","properties":{"present":{"type":"array","items":{"type":"string"}},"absent":{"type":"array","items":{"type":"string"}},"error_seen":{"type":"string"}},"required":["present","absent","error_seen"]}'

# --- One `claude -p` call. Start-Process (per the brief), redirected stdout/stderr, a real 240 s
# timeout enforced with Process.WaitForExit(ms) (Start-Process itself has no timeout parameter) --
# a hung call is killed rather than left to run past this script's own life. -------------------------
function Invoke-ClaudeLeg {
    param(
        [Parameter(Mandatory)][string]$LogTag,
        [Parameter(Mandatory)][string]$Prompt,
        [Parameter(Mandatory)][string]$SettingsPath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Tools,
        [switch]$OmitSettingSources
    )
    Assert-ScratchOnly -Path $SettingsPath
    $settingsArg = [IO.Path]::TrimEndingDirectorySeparator($SettingsPath)

    $rawArgs = [System.Collections.Generic.List[string]]::new()
    $rawArgs.Add('-p')
    $rawArgs.Add($Prompt)
    $rawArgs.Add('--model'); $rawArgs.Add('sonnet')
    $rawArgs.Add('--permission-mode'); $rawArgs.Add('dontAsk')
    $rawArgs.Add('--strict-mcp-config')
    $rawArgs.Add('--no-session-persistence')
    $rawArgs.Add('--disable-slash-commands')
    $rawArgs.Add('--settings'); $rawArgs.Add($settingsArg)
    if (-not $OmitSettingSources) { $rawArgs.Add('--setting-sources'); $rawArgs.Add('') }
    $rawArgs.Add('--tools'); $rawArgs.Add($Tools)
    $rawArgs.Add('--output-format'); $rawArgs.Add('json')
    $rawArgs.Add('--json-schema'); $rawArgs.Add($schema)

    $escaped = $rawArgs | ForEach-Object { ConvertTo-Argv -Arg $_ }

    $outLog = Join-Path $scratch "$LogTag.out.log"
    $errLog = Join-Path $scratch "$LogTag.err.log"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $claudeExe -ArgumentList $escaped -WorkingDirectory $scratchFull -PassThru -NoNewWindow `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    $finished = $proc.WaitForExit(240000)
    $sw.Stop()
    if (-not $finished) {
        try { $proc.Kill() } catch { }
        return [pscustomobject]@{
            LogTag = $LogTag; TimedOut = $true; ExitCode = $null; WallMs = $sw.ElapsedMilliseconds; ProcessId = $proc.Id
            StdOut = (Get-Content -LiteralPath $outLog -Raw -ErrorAction SilentlyContinue)
            StdErr = (Get-Content -LiteralPath $errLog -Raw -ErrorAction SilentlyContinue)
        }
    }
    [pscustomobject]@{
        LogTag = $LogTag; TimedOut = $false; ExitCode = $proc.ExitCode; WallMs = $sw.ElapsedMilliseconds; ProcessId = $proc.Id
        StdOut = (Get-Content -LiteralPath $outLog -Raw -ErrorAction SilentlyContinue)
        StdErr = (Get-Content -LiteralPath $errLog -Raw -ErrorAction SilentlyContinue)
    }
}

# --- Parses --output-format json's envelope and pulls out structured_output (never prose). ----------
function Get-LegStructuredOutput {
    param([string]$StdOut)
    if ([string]::IsNullOrWhiteSpace($StdOut)) { return $null }
    try { $envelope = $StdOut | ConvertFrom-Json -ErrorAction Stop } catch { return $null }
    if ($null -eq $envelope) { return $null }
    return $envelope.structured_output
}

$exitCode = 1
$callCount = 0

try {
    # =============================================================================================
    # Fixture + export: 198 live entries, exported once (target is Absent, so a plain export is
    # the only state this needs -- no --force / --accept-new-source machinery from M24 is relevant
    # to a single clean export).
    # =============================================================================================
    $dataDir = Join-Path $scratch 'store\data'
    $fixture = New-Row26Fixture -MemoryDir (Join-Path $dataDir 'memory')
    Add-Check -Name 'fixture.total-entries-is-198' -Passed ($fixture.TotalEntries -eq 198) -Detail "actual=$($fixture.TotalEntries)"

    $target198 = Join-Path $scratch 'store\export198'
    $r198 = Invoke-HubExport -DataDir $dataDir -TargetDir $target198 -LogTag 'export198'
    Add-Check -Name 'export198.exit-zero' -Passed ($r198.ExitCode -eq 0) -Detail "exit=$($r198.ExitCode)"
    $result198 = Get-ExportResult -StdOut $r198.StdOut
    Add-Check -Name 'export198.export-result-present' -Passed ($null -ne $result198) -Detail $r198.StdOut
    if ($null -ne $result198) {
        Add-Check -Name 'export198.exported-count-is-198' -Passed ($result198.exported -eq 198) -Detail "actual=$($result198.exported)"
    }
    $fileCount198 = @(Get-ChildItem -LiteralPath $target198 -File -Filter '*.md' | Where-Object { $_.Name -ne 'MEMORY.md' }).Count
    Add-Check -Name 'export198.file-count-is-198' -Passed ($fileCount198 -eq 198) -Detail "actual=$fileCount198"

    $indexPath198 = Join-Path $target198 'MEMORY.md'
    $indexRaw198 = Get-Content -LiteralPath $indexPath198 -Raw
    $indexTrimmed198 = $indexRaw198.Trim()
    $indexLines198 = $indexTrimmed198 -split "`n"
    Add-Check -Name 'export198.index-line-count-is-200' -Passed ($indexLines198.Count -eq 200) -Detail "actual=$($indexLines198.Count) (200 = the vendor's own cap, 198 entries + 2-line header)"

    # entry 198 (the last index line) -- read back dynamically rather than assumed from topic-sort
    # order, so this does not depend on ListTopics' alphabetical ordering being what this script's
    # author reasoned it to be.
    $lastIndexLine = $indexLines198[-1]
    $entry198Match = [regex]::Match($lastIndexLine, '[0-9a-f]{8}')
    Add-Check -Name 'export198.entry198-nonce-found-on-last-line' -Passed $entry198Match.Success -Detail $lastIndexLine
    $entry198Nonce = if ($entry198Match.Success) { $entry198Match.Value } else { $null }

    # Sanity on the fixture itself: the room-general landmark's body-only nonce must NOT be in the
    # index (only the topic file), and MUST be in its own exported file.
    $roomLandmark = $fixture.RoomLandmark
    Add-Check -Name 'fixture.room-general-body-nonce-absent-from-index' -Passed (-not $indexTrimmed198.Contains($roomLandmark.BodyNonce))
    $roomFilePath198 = Join-Path $target198 $roomLandmark.ExportFileName
    $roomFileExists = Test-Path -LiteralPath $roomFilePath198 -PathType Leaf
    Add-Check -Name 'fixture.room-general-export-file-exists' -Passed $roomFileExists -Detail $roomFilePath198
    if ($roomFileExists) {
        $roomFileText = Get-Content -LiteralPath $roomFilePath198 -Raw
        Add-Check -Name 'fixture.room-general-body-nonce-in-own-file' -Passed ($roomFileText.Contains($roomLandmark.BodyNonce))
        Add-Check -Name 'fixture.room-general-type-is-room-general' -Passed ($roomFileText -match '(?m)^\s*type:\s*room-general\s*$')
    }

    $userLandmark = $fixture.UserLandmark
    Add-Check -Name 'fixture.user-title-nonce-in-index' -Passed ($indexTrimmed198.Contains($userLandmark.TitleNonce))
    Add-Check -Name 'fixture.room-general-title-nonce-in-index' -Passed ($indexTrimmed198.Contains($roomLandmark.TitleNonce))

    # =============================================================================================
    # Leg 2's fixture: a COPY of the 198-export with one hand-appended index line (199 entries) plus
    # a matching memory file, because MemoryExport.Render itself refuses over the 198-live-entry
    # floor (MaxMemories = MaxIndexLines - 2). Decision (brief's call): DELETE the copy's
    # .chopitup-export.json manifest, so this copy reads as a bare vendor-shape directory rather than
    # something ChopItUp's own export-verify tooling would recognise as one of its own exports.
    # =============================================================================================
    $target199 = Join-Path $scratch 'store\export199'
    Copy-Item -LiteralPath $target198 -Destination $target199 -Recurse
    $manifestCopy = Join-Path $target199 '.chopitup-export.json'
    $manifestExistedBeforeDelete = Test-Path -LiteralPath $manifestCopy -PathType Leaf
    Remove-Item -LiteralPath $manifestCopy -Force -ErrorAction SilentlyContinue
    Add-Info -Name 'leg2fixture.manifest-decision' -Detail "deleted .chopitup-export.json from the 199-copy (existed before delete: $manifestExistedBeforeDelete)"

    $nonce199 = New-Nonce
    $title199 = "F34 $nonce199"
    $fileName199 = 'user-' + (ConvertTo-ExportSlug -S $title199) + '.md'
    $indexLine199 = "- [$title199]($fileName199) — hook user 34."
    $indexPath199 = Join-Path $target199 'MEMORY.md'
    $indexRaw199Before = Get-Content -LiteralPath $indexPath199 -Raw
    [System.IO.File]::AppendAllText($indexPath199, "$indexLine199`n", (New-Object System.Text.UTF8Encoding($false)))
    $bodyNonce199 = New-Nonce
    $entryText199 = "---`nname: " + [IO.Path]::GetFileNameWithoutExtension($fileName199) + "`ndescription: $title199`nmetadata:`n  type: user`n---`n`nhook user 34.`nnonce: $bodyNonce199`n"
    [System.IO.File]::WriteAllText((Join-Path $target199 $fileName199), $entryText199, (New-Object System.Text.UTF8Encoding($false)))

    $indexRaw199After = Get-Content -LiteralPath $indexPath199 -Raw
    $rawLineCount199 = (($indexRaw199After.TrimEnd("`n")) -split "`n").Count
    Add-Check -Name 'leg2fixture.raw-index-line-count-is-201' -Passed ($rawLineCount199 -eq 201) -Detail "actual=$rawLineCount199 (200 original + 1 hand-appended = over the vendor's 200-line cap)"
    Add-Check -Name 'leg2fixture.nonce199-appended-to-raw-file' -Passed ($indexRaw199After.Contains($nonce199)) -Detail "before-length=$($indexRaw199Before.Length) after-length=$($indexRaw199After.Length)"

    # =============================================================================================
    # settings.json per fixture: {"autoMemoryDirectory": "<absolute export dir>"}.
    # =============================================================================================
    $settings198 = Join-Path $scratch 'settings198.json'
    $settings199 = Join-Path $scratch 'settings199.json'
    [System.IO.File]::WriteAllText($settings198, (@{ autoMemoryDirectory = [IO.Path]::GetFullPath($target198) } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText($settings199, (@{ autoMemoryDirectory = [IO.Path]::GetFullPath($target199) } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))

    # =============================================================================================
    # Leg 1: no tools at all, --setting-sources "" (isolated from real user/project/local settings),
    # asks (without reading anything -- there is nothing it COULD read) which of three codes it
    # already has loaded: the user-type landmark, the room-general-type landmark's TITLE nonce (index
    # only), and the entry-198 (last index line) nonce.
    # =============================================================================================
    $leg1Codes = @($userLandmark.TitleNonce, $roomLandmark.TitleNonce, $entry198Nonce)
    $leg1Prompt = "Do not use any tool -- this is a memory-recall check only. Answer strictly from whatever text is already loaded in your context before this message (system prompt / auto-loaded memory), nothing else. Here are three 8-character lowercase-hex codes: $($leg1Codes -join ', '). For each one, decide whether that exact code appears anywhere in what you already have loaded. Respond only via the structured output: present = the exact codes you can find already loaded, absent = the exact codes you cannot find, error_seen = any error text you noticed while checking (empty string if none)."
    $leg1 = Invoke-ClaudeLeg -LogTag 'leg1' -Prompt $leg1Prompt -SettingsPath $settings198 -Tools ''
    $callCount++
    Add-Check -Name 'leg1.completed-without-timeout' -Passed (-not $leg1.TimedOut) -Detail "wallMs=$($leg1.WallMs)"
    Add-Info -Name 'leg1.exit-code' -Detail "$($leg1.ExitCode)"
    $leg1Struct = Get-LegStructuredOutput -StdOut $leg1.StdOut
    Add-Check -Name 'leg1.structured-output-present' -Passed ($null -ne $leg1Struct) -Detail $(if ($null -eq $leg1Struct) { "stdout=$($leg1.StdOut) stderr=$($leg1.StdErr)" } else { '' })
    $leg1Present = if ($null -ne $leg1Struct) { @($leg1Struct.present) } else { @() }
    Add-Info -Name 'leg1.user-nonce-status' -Detail "$($userLandmark.TitleNonce) -> $(if ($leg1Present -contains $userLandmark.TitleNonce) { 'present' } else { 'absent' })"
    Add-Info -Name 'leg1.room-general-title-nonce-status' -Detail "$($roomLandmark.TitleNonce) -> $(if ($leg1Present -contains $roomLandmark.TitleNonce) { 'present' } else { 'absent' })"
    Add-Info -Name 'leg1.entry198-nonce-status' -Detail "$entry198Nonce -> $(if ($leg1Present -contains $entry198Nonce) { 'present' } else { 'absent' })"
    Add-Info -Name 'leg1.error-seen' -Detail "$(if ($null -ne $leg1Struct) { $leg1Struct.error_seen } else { '(no structured output)' })"

    # =============================================================================================
    # Leg 2: the 199-entry copy. Asks about entry198Nonce (should still be on the loaded index if the
    # 200-line cap truncates from the top, i.e. keeps the original 198 + header and drops the
    # hand-appended 199th) and nonce199 (predicted absent for the same reason).
    # =============================================================================================
    $leg2Codes = @($entry198Nonce, $nonce199)
    $leg2Prompt = "Do not use any tool -- this is a memory-recall check only. Answer strictly from whatever text is already loaded in your context before this message (system prompt / auto-loaded memory), nothing else. Here are two 8-character lowercase-hex codes: $($leg2Codes -join ', '). For each one, decide whether that exact code appears anywhere in what you already have loaded. Respond only via the structured output: present = the exact codes you can find already loaded, absent = the exact codes you cannot find, error_seen = any error text you noticed while checking (empty string if none)."
    $leg2 = Invoke-ClaudeLeg -LogTag 'leg2' -Prompt $leg2Prompt -SettingsPath $settings199 -Tools ''
    $callCount++
    Add-Check -Name 'leg2.completed-without-timeout' -Passed (-not $leg2.TimedOut) -Detail "wallMs=$($leg2.WallMs)"
    Add-Info -Name 'leg2.exit-code' -Detail "$($leg2.ExitCode)"
    $leg2Struct = Get-LegStructuredOutput -StdOut $leg2.StdOut
    Add-Check -Name 'leg2.structured-output-present' -Passed ($null -ne $leg2Struct) -Detail $(if ($null -eq $leg2Struct) { "stdout=$($leg2.StdOut) stderr=$($leg2.StdErr)" } else { '' })
    $leg2Present = if ($null -ne $leg2Struct) { @($leg2Struct.present) } else { @() }
    Add-Info -Name 'leg2.entry198-nonce-status' -Detail "$entry198Nonce -> $(if ($leg2Present -contains $entry198Nonce) { 'present' } else { 'absent' })"
    Add-Info -Name 'leg2.nonce199-status' -Detail "$nonce199 -> $(if ($leg2Present -contains $nonce199) { 'present' } else { 'absent' }) (predicted: absent, over the 200-line cap)"
    Add-Info -Name 'leg2.error-seen' -Detail "$(if ($null -ne $leg2Struct) { $leg2Struct.error_seen } else { '(no structured output)' })"

    # =============================================================================================
    # Leg 3: leg 1's fixture again, but with the Read tool available. Points it straight at the
    # room-general landmark's exported file and asks whether the BODY-ONLY nonce (never in the index)
    # is in there -- the only way to answer this is to actually read the file.
    # =============================================================================================
    $roomFileAbs198 = [IO.Path]::GetFullPath((Join-Path $target198 $roomLandmark.ExportFileName))
    $leg3Prompt = "You have the Read tool available and nothing else. Read this exact file: $roomFileAbs198 -- and tell me whether it contains this exact 8-character lowercase-hex code: $($roomLandmark.BodyNonce). Respond only via the structured output: present = [that code] if you found it after reading the file, absent = [that code] if you read the file and did not find it (or could not read it), error_seen = any error text you noticed (empty string if none)."
    $leg3 = Invoke-ClaudeLeg -LogTag 'leg3' -Prompt $leg3Prompt -SettingsPath $settings198 -Tools 'Read'
    $callCount++
    Add-Check -Name 'leg3.completed-without-timeout' -Passed (-not $leg3.TimedOut) -Detail "wallMs=$($leg3.WallMs)"
    Add-Info -Name 'leg3.exit-code' -Detail "$($leg3.ExitCode)"
    $leg3Struct = Get-LegStructuredOutput -StdOut $leg3.StdOut
    Add-Check -Name 'leg3.structured-output-present' -Passed ($null -ne $leg3Struct) -Detail $(if ($null -eq $leg3Struct) { "stdout=$($leg3.StdOut) stderr=$($leg3.StdErr)" } else { '' })
    $leg3Present = if ($null -ne $leg3Struct) { @($leg3Struct.present) } else { @() }
    Add-Info -Name 'leg3.room-general-body-nonce-status' -Detail "$($roomLandmark.BodyNonce) -> $(if ($leg3Present -contains $roomLandmark.BodyNonce) { 'present (read the file)' } else { 'absent (did not read it, or read and it was not there)' })"
    Add-Info -Name 'leg3.error-seen' -Detail "$(if ($null -ne $leg3Struct) { $leg3Struct.error_seen } else { '(no structured output)' })"

    # =============================================================================================
    # Leg 4 (conditional, AC5): only if leg 1 reported nothing present at all -- re-runs leg 1's exact
    # fixture WITHOUT --setting-sources "", to check whether that flag itself was suppressing
    # auto-memory (one of the two "could not verify" items in the brief). This leg lets the real
    # user-level settings scope load (cwd is the scratch dir, so project/local scope finds nothing
    # real); it is still --tools "" and --permission-mode dontAsk, so it cannot act on anything.
    # =============================================================================================
    $leg1ReportedNothing = ($leg1Present.Count -eq 0)
    if ($leg1ReportedNothing) {
        $leg4 = Invoke-ClaudeLeg -LogTag 'leg4' -Prompt $leg1Prompt -SettingsPath $settings198 -Tools '' -OmitSettingSources
        $callCount++
        Add-Check -Name 'leg4.completed-without-timeout' -Passed (-not $leg4.TimedOut) -Detail "wallMs=$($leg4.WallMs)"
        Add-Info -Name 'leg4.exit-code' -Detail "$($leg4.ExitCode)"
        $leg4Struct = Get-LegStructuredOutput -StdOut $leg4.StdOut
        Add-Check -Name 'leg4.structured-output-present' -Passed ($null -ne $leg4Struct) -Detail $(if ($null -eq $leg4Struct) { "stdout=$($leg4.StdOut) stderr=$($leg4.StdErr)" } else { '' })
        $leg4Present = if ($null -ne $leg4Struct) { @($leg4Struct.present) } else { @() }
        Add-Info -Name 'leg4.user-nonce-status' -Detail "$($userLandmark.TitleNonce) -> $(if ($leg4Present -contains $userLandmark.TitleNonce) { 'present' } else { 'absent' })"
        Add-Info -Name 'leg4.room-general-title-nonce-status' -Detail "$($roomLandmark.TitleNonce) -> $(if ($leg4Present -contains $roomLandmark.TitleNonce) { 'present' } else { 'absent' })"
        Add-Info -Name 'leg4.entry198-nonce-status' -Detail "$entry198Nonce -> $(if ($leg4Present -contains $entry198Nonce) { 'present' } else { 'absent' })"
        Add-Info -Name 'leg4.error-seen' -Detail "$(if ($null -ne $leg4Struct) { $leg4Struct.error_seen } else { '(no structured output)' })"
    }
    else {
        Add-Info -Name 'leg4.skipped' -Detail 'leg 1 reported at least one code present, so the --setting-sources "" fallback probe was not needed'
    }

    Add-Check -Name 'spend.calls-within-cap' -Passed ($callCount -le 4) -Detail "calls=$callCount"

    $exitCode = if ($failCount -eq 0) { 0 } else { 1 }
}
catch {
    Add-Check -Name 'unhandled-error' -Passed $false -Detail "$($_.Exception.Message) [line $($_.InvocationInfo.ScriptLineNumber)]"
    $exitCode = 1
}
finally {
    $logPath = Join-Path $scratch 'row26-probe.log'
    $checkLines | Set-Content -Path $logPath -Encoding utf8

    $passCount = ($checkLines | Where-Object { $_.StartsWith('PASS') }).Count
    $failCountFinal = ($checkLines | Where-Object { $_.StartsWith('FAIL') }).Count
    $infoCount = ($checkLines | Where-Object { $_.StartsWith('INFO') }).Count
    Write-Host ""
    Write-Host "Probe log: $logPath"
    Write-Host "Results: $passCount PASS / $failCountFinal FAIL / $infoCount INFO"

    if ($KeepEvidence) {
        Write-Host "Evidence kept at: $scratch"
    }
    else {
        try { Remove-Item -Path $scratch -Recurse -Force -ErrorAction Stop } catch { Write-Host "Warning: could not clean up '$scratch': $($_.Exception.Message)" }
    }
}

exit $exitCode
