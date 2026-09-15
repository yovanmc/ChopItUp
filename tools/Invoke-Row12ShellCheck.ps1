#Requires -Version 7
<#
.SYNOPSIS
  Row 12 Task 9: end-to-end UIA verification harness for the desktop shell (ChopItUp.Desktop.exe).

.DESCRIPTION
  Adapted from ~\.claude\skills\roadmap\references\desk-check-template.ps1 -- this is a permanent,
  committed tool (not a one-off desk-check copy under .scratch\desk-checks\), so it keeps that
  template's CONVENTIONS rather than its literal frozen param block:
    - a RunId derived from the target exe's mtime+size+harness version, stamped on every log row;
    - one evidence directory PER RUN (never reused -- see -EvidenceRoot), so the "skip a prior PASS"
      caching the template does for a long-lived log file does not apply here and is not implemented;
    - a nested .gitignore ('*') is written into the run's own evidence dir before anything else, and
      the write REFUSES if that directory already holds git-tracked files (it never will -- it is a
      freshly stamped guid-suffixed dir -- but the refusal is kept as defence in depth);
    - every leg records an EXPLICIT $passed boolean via Add-Check (Row 29's Invoke-Row29PeerCheck.ps1
      convention, `Add-Check -Name -Passed:[bool] -Detail`), never inferred from a string;
    - shape allowlist for -Detail: booleans, counts, exit codes, ports, milliseconds -- never a
      message body, a token, a filename under the data dir, or any quoted log content upward. Every
      report line below honours "counts only, never quoting log content."
    - DATABASE BOUNDARY: this script never opens chopitup.db (no SQLite connection, no PRAGMA). Every
      fact about corpus/room/message state comes from the hub's own HTTP API or from file-level
      existence/lock checks (hub.lock, hub.port, desktop.log), exactly like the hub's own HubProbe.
    - binary launches are the explicit subject under test here (the shell, the hub, the corpus tool,
      a real `claude` CLI spawn triggered through the hub's own run machinery) rather than incidental
      utility calls, so there is no generic $LaunchAllowlist/Invoke-Allowlisted wrapper; each launch
      is commented with its justification and every PID this script starts is tracked and killed by
      PID in Finally, never by name (standing contract).

  Coordinator ruling (2026-09-15, this session) on top of the plan's Task 9 section:
    - seed the corpus with `--schema-version 2 --messages 200` (the corpus tool's newest shape; it
      only ever writes v1 or v2 -- CorpusBuilder.cs:64) and let the hub's real 2->11 migration run
      inside leg 1's 25 s readiness budget. Leg 1 keeps its 25 s assertion and ADDITIONALLY records
      the SESSION START -> HUB STATE Ready latency as evidence (reported, not asserted, so the
      migration's real cost is learned). This doubles as the tier's synthetic-corpus dry run, so the
      data dir is never swapped for an empty one.
    - legs 3 and 7 never hardcode room "general": they read `GET /api/rooms` and post/read against
      `rooms[0].id` (the client itself selects `loaded[0].id` on load -- App.tsx:279 -- so the
      composer the harness types into is always this same room).

  Legs 1-9 and the screenshot-sanity gate are exactly the plan's Task 9 section. The plan also asks
  for a screenshot "judged by a pinned sonnet subagent" -- a PowerShell script cannot dispatch a
  Claude subagent, and builder-subagent policy forbids using the Agent tool at all, so this script's
  own responsibility ends at capture + Test-CaptureSane.ps1's mechanical sanity gate; the visual
  judging pass is a follow-on action for whoever reviews this run's evidence directory.

.NOTES
  Windows-only (System.Windows.Automation, System.Drawing, System.Windows.Forms, P/Invoke). Refuses
  to start outside an interactive desktop session (UIA and window capture both need one).
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$TargetExe = (Join-Path $RepoRoot 'src\ChopItUp.Desktop\bin\Debug\net10.0-windows\ChopItUp.Desktop.exe'),
    [string]$HubExe = (Join-Path $RepoRoot 'src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe'),
    [string]$CorpusExe = (Join-Path $RepoRoot 'tools\ChopItUp.Corpus\bin\Debug\net10.0\ChopItUp.Corpus.exe'),
    [string]$EvidenceRoot = 'C:\Agent Projects\ChopItUp\.scratch\m12-desktop-shell\evidence',
    [int]$MessagesInCorpus = 200
)
$ErrorActionPreference = 'Stop'
$HarnessVersion = 'row12-harness-v1'

# ===== standard preamble (adapted from the desk-check template; see .DESCRIPTION) ==================

foreach ($exe in @($TargetExe, $HubExe, $CorpusExe)) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "DENIED: '$exe' not found. Build first: dotnet build ChopItUp.slnx -c Debug -warnaserror -v minimal"
    }
}

# Refuses outside an interactive desktop: UIA element trees and CopyFromScreen both need one, and a
# refusal here is cheaper than nine legs of confusing timeouts against a session with no desktop.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
if (-not [Environment]::UserInteractive) { throw "DENIED: this session is not interactive ([Environment]::UserInteractive is false); the harness needs a real desktop for UIA and screen capture." }
$virtualScreen = [System.Windows.Forms.SystemInformation]::VirtualScreen
if ($virtualScreen.Width -le 0 -or $virtualScreen.Height -le 0) { throw "DENIED: SystemInformation.VirtualScreen is ${$virtualScreen.Width}x${$virtualScreen.Height}; no usable desktop." }

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$evidenceDir = Join-Path $EvidenceRoot $stamp
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

# Refuse to write the nested .gitignore over a directory that already holds tracked files (template
# rule). A freshly stamped guid dir never does, but the check stays as defence in depth.
$trackedHere = @()
try { $trackedHere = @(& git -C $evidenceDir ls-files 2>$null) } catch { }
if ($trackedHere.Count -gt 0) {
    throw "DENIED: '$evidenceDir' holds $($trackedHere.Count) git-tracked file(s); refusing to write '*' over it."
}
Set-Content -LiteralPath (Join-Path $evidenceDir '.gitignore') -Value '*' -Encoding ascii

# Keep the last 3 run directories under $EvidenceRoot (template's "keep last 3 logs", applied at the
# per-run-directory granularity this script uses instead of per-log-file).
Get-ChildItem -LiteralPath $EvidenceRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d{8}-\d{6}$' } |
    Sort-Object Name -Descending | Select-Object -Skip 3 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }

$log = Join-Path $evidenceDir 'Invoke-Row12ShellCheck.log'
$targetFi = Get-Item -LiteralPath $TargetExe
$RunId = "{0:yyyyMMddTHHmmssZ}+{1}+{2}" -f $targetFi.LastWriteTimeUtc, $targetFi.Length, $HarnessVersion

$script:pass = 0; $script:fail = 0
$script:AllChecks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    # Explicit boolean in, never inferred from a value string (dispatch requirement). -Detail is
    # shape-restricted: booleans/counts/exit codes/ports/ms only -- never message content or a token.
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Passed,
        [string]$Detail = ''
    )
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    Add-Content -LiteralPath $log -Value ("{0}`t{1}`t{2}`t{3}`t{4}" -f $RunId, $Name, $status, $Detail, (Get-Date -Format o))
    if ($Passed) { $script:pass++ } else { $script:fail++ }
    Write-Host ("{0,-6} {1}  {2}" -f $status, $Name, $Detail)
    $script:AllChecks.Add([pscustomobject]@{ Name = $Name; Passed = $Passed; Detail = $Detail })
}

function Add-Metric {
    # Informational only -- never counted toward pass/fail. Used for the "reported, not asserted"
    # timings the coordinator's ruling and the plan's leg 1 both ask for.
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)
    Add-Content -LiteralPath $log -Value ("{0}`t{1}`tMETRIC`t{2}`t{3}" -f $RunId, $Name, $Value, (Get-Date -Format o))
    Write-Host ("METRIC {0}  {1}" -f $Name, $Value)
}

# ===== native / UIA plumbing ========================================================================

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

$nativeSrc = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Row12Native
{
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // WebView2's top-level child HWND class (Chromium's own window class name), used to find the
    // WebView2 content HWND under the shell's top-level window for the resize-margin measurement.
    public static IntPtr FindChromeWidgetChild(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (hwnd, lparam) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "Chrome_WidgetWin_0") { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
Add-Type -TypeDefinition $nativeSrc -Language CSharp

$AE = [System.Windows.Automation.AutomationElement]
$TreeScope = [System.Windows.Automation.TreeScope]
$Automation = [System.Windows.Automation.Automation]

function Get-Win32Rect([IntPtr]$hwnd) {
    $r = New-Object Row12Native+RECT
    if (-not [Row12Native]::GetWindowRect($hwnd, [ref]$r)) { throw "GetWindowRect failed for handle $hwnd" }
    return $r
}

function Find-TopWindowByPid([int]$procId, [int]$timeoutMs = 10000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
        $el = $AE::RootElement.FindFirst($TreeScope::Children, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-DescendantByName([System.Windows.Automation.AutomationElement]$parent, [string]$name, [int]$timeoutMs = 10000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
        $el = $parent.FindFirst($TreeScope::Descendants, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-UiaElement([System.Windows.Automation.AutomationElement]$el) {
    $pattern = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
        return $true
    }
    return $false
}

# ===== process / port / log helpers =================================================================

$script:TrackedPids = New-Object System.Collections.Generic.List[int]

function Start-Tracked {
    # System.Diagnostics.ProcessStartInfo.ArgumentList (not Start-Process -ArgumentList) so every path
    # argument is quoted correctly by .NET's own argv marshalling -- Start-Process's array form is NOT
    # reliably per-element-quoted on Windows (measured elsewhere in this repo: tools\Invoke-Row29PeerCheck.ps1
    # manually double-quotes every path argument for exactly this reason). Mirrors ProcessHubFactory.cs's
    # own shape.
    param([Parameter(Mandatory)][string]$Exe, [string[]]$Arguments = @())
    $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $script:TrackedPids.Add($p.Id)
    return $p
}

function Stop-TrackedPid([int]$procId) {
    try {
        $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if ($p -and -not $p.HasExited) { $p.Kill($true) }
    } catch { }
}

function Get-FreePort {
    param([int[]]$Avoid = @(8790, 8795))
    while ($true) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        if ($Avoid -notcontains $port) { return $port }
    }
}

function Get-LogLines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    for ($i = 0; $i -lt 5; $i++) {
        try { return @(Get-Content -LiteralPath $Path -Encoding utf8) }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return @()
}

function Wait-ForLogPattern {
    # Polls $Path for a NEW line (after $SinceCount) matching $Pattern within $TimeoutMs. Returns
    # @{ Found; Line; ElapsedMs; LineCount } -- LineCount is the total line count AFTER the wait, for
    # the caller to use as the next leg's $SinceCount baseline.
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Pattern, [int]$SinceCount = 0, [int]$TimeoutMs = 25000)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $all = Get-LogLines -Path $Path
        if ($all.Count -gt $SinceCount) {
            for ($i = $SinceCount; $i -lt $all.Count; $i++) {
                if ($all[$i] -match $Pattern) {
                    return [pscustomobject]@{ Found = $true; Line = $all[$i]; ElapsedMs = $sw.ElapsedMilliseconds; LineCount = $all.Count }
                }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    $all = Get-LogLines -Path $Path
    return [pscustomobject]@{ Found = $false; Line = $null; ElapsedMs = $sw.ElapsedMilliseconds; LineCount = $all.Count }
}

function Get-LogTimestamp([string]$line) {
    # ShellLog.Append prefixes every line "yyyy-MM-ddTHH:mm:ss.fffZ <line>".
    if ($line -match '^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)\s') {
        return [DateTimeOffset]::Parse($Matches.ts, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    }
    return $null
}

# ===== HTTP helpers ==================================================================================

function Invoke-OwnerJson {
    param([string]$Method = 'GET', [Parameter(Mandatory)][string]$Uri, [string]$BearerToken, $Body)
    $headers = @{}
    if ($BearerToken) { $headers['Authorization'] = "Bearer $BearerToken" }
    if ($null -ne $Body) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json) -TimeoutSec 15
    }
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -TimeoutSec 15
}

function Test-Health([string]$BaseUrl) {
    try {
        $h = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 5
        return ($null -ne $h) -and ($h.ok -eq $true)
    } catch { return $false }
}

# ===== setup: ports, corpus, owner token seed ========================================================

. (Join-Path $RepoRoot 'tools\ChopTokenHelpers.ps1')

$D = Join-Path $env:TEMP ("chopitup_row12_" + [guid]::NewGuid().ToString('N'))
$P = Get-FreePort
$P2 = Get-FreePort -Avoid @(8790, 8795, $P)
$base = "http://127.0.0.1:$P"
$localhostBase = "http://localhost:$P"   # POST/import over `localhost`, matching Row29PeerCheck.ps1's own D9 note (Windows resolves [::1] first)

Write-Host "Evidence: $evidenceDir"
Write-Host "Data dir: $D  Port: $P  Attach port: $P2"

$setupOk = $true

try {
    & $CorpusExe --data $D --messages $MessagesInCorpus --rooms 3 --leave-in-wal 0 --schema-version 2 | Out-Null
    $corpusExit = $LASTEXITCODE
} catch { $corpusExit = 1 }
$corpusDb = Join-Path $D 'chopitup.db'
Add-Check -Name 'setup.corpus-seeded' -Passed:(($corpusExit -eq 0) -and (Test-Path -LiteralPath $corpusDb)) -Detail "exit=$corpusExit"
if ($corpusExit -ne 0 -or -not (Test-Path -LiteralPath $corpusDb)) { $setupOk = $false }

$ownerToken = $null
if ($setupOk) {
    try {
        $seeded = Initialize-ChopScratchTokens -DataDir $D -ParticipantIds @('owner')
        $ownerToken = $seeded.owner
        Add-Check -Name 'setup.owner-token-seeded' -Passed:([bool]$ownerToken) -Detail 'OK'
    } catch {
        Add-Check -Name 'setup.owner-token-seeded' -Passed:$false -Detail 'exit=1'
        $setupOk = $false
    }
}

if (-not $setupOk) { throw "DENIED: setup failed; see evidence log at $log. Not proceeding to the legs." }

$shellProc = $null
$hubPid = $null
$sinceCount = 0   # desktop.log line-count baseline; advanced after each leg that waits on new lines.
$chromeRect = $null   # captured in leg 2, reused by leg 5's rect-unchanged assertion.
$firstRoomId = $null
$firstRoomName = $null

# ===== leg 1: start ===================================================================================

try {
    $sessionStartedAt = Get-Date
    $shellProc = Start-Tracked -Exe $TargetExe -Arguments @('--data', $D, '--hub', $HubExe, '--port', "$P")
    $desktopLog = Join-Path $D 'logs\desktop.log'

    $bootShown = Wait-ForLogPattern -Path $desktopLog -Pattern 'BOOT SHOWN' -SinceCount 0 -TimeoutMs 15000
    $ready = Wait-ForLogPattern -Path $desktopLog -Pattern 'HUB STATE Ready\b' -SinceCount 0 -TimeoutMs 25000
    Add-Check -Name 'leg1.hub-state-ready' -Passed:$ready.Found -Detail "ms=$($ready.ElapsedMs)"

    if ($ready.Found) {
        $sessionLine = (Get-LogLines -Path $desktopLog) | Where-Object { $_ -match 'SESSION START' } | Select-Object -First 1
        $sessionTs = Get-LogTimestamp $sessionLine
        $readyTs = Get-LogTimestamp $ready.Line
        if ($sessionTs -and $readyTs) {
            $migrationMs = [int](($readyTs - $sessionTs).TotalMilliseconds)
            Add-Metric -Name 'leg1.session-to-ready-ms' -Value "$migrationMs"
        }
        if ($bootShown.Found -and $sessionTs) {
            $bootTs = Get-LogTimestamp $bootShown.Line
            if ($bootTs) { Add-Metric -Name 'leg1.session-to-boot-shown-ms' -Value "$([int](($bootTs - $sessionTs).TotalMilliseconds))" }
        }
    }

    $nav = Wait-ForLogPattern -Path $desktopLog -Pattern ([regex]::Escape("NAV True http://127.0.0.1:$P")) -SinceCount 0 -TimeoutMs 15000
    Add-Check -Name 'leg1.nav-true' -Passed:$nav.Found -Detail "ms=$($nav.ElapsedMs)"

    $startLine = (Get-LogLines -Path $desktopLog) | Where-Object { $_ -match 'HUB START pid=(\d+)' } | Select-Object -First 1
    if ($startLine -and ($startLine -match 'HUB START pid=(?<pid>\d+)')) {
        $hubPid = [int]$Matches.pid
        $script:TrackedPids.Add($hubPid)
        $cmdLine = $null
        try { $cmdLine = (Get-CimInstance Win32_Process -Filter "ProcessId=$hubPid" -ErrorAction Stop).CommandLine } catch { }
        $portMatch = $cmdLine -and ($cmdLine -match [regex]::Escape("--port $P"))
        Add-Check -Name 'leg1.child-hub-pid-has-port' -Passed:([bool]$portMatch) -Detail "pid=$hubPid"
    } else {
        Add-Check -Name 'leg1.child-hub-pid-has-port' -Passed:$false -Detail 'exit=1'
    }

    Add-Check -Name 'leg1.health-ok' -Passed:(Test-Health $base) -Detail 'OK'

    $sinceCount = (Get-LogLines -Path $desktopLog).Count
} catch {
    Add-Check -Name 'leg1.start' -Passed:$false -Detail 'exit=1'
}

$desktopLog = Join-Path $D 'logs\desktop.log'

# ===== leg 2: chrome ===================================================================================

$windowEl = $null
$chromeButtons = @{}
try {
    $windowEl = Find-TopWindowByPid -procId $shellProc.Id -timeoutMs 10000
    Add-Check -Name 'leg2.window-found' -Passed:($null -ne $windowEl) -Detail 'OK'

    foreach ($name in @('Minimize', 'Maximize', 'Close')) {
        $el = if ($windowEl) { Find-DescendantByName -parent $windowEl -name $name -timeoutMs 10000 } else { $null }
        $chromeButtons[$name] = $el
        Add-Check -Name "leg2.button-$($name.ToLowerInvariant())" -Passed:($null -ne $el) -Detail 'OK'
    }

    $noNonclientWarning = -not ((Get-LogLines -Path $desktopLog) -join "`n" -match 'NONCLIENT unsupported')
    Add-Check -Name 'leg2.no-nonclient-warning' -Passed:$noNonclientWarning -Detail 'OK'

    $topHwnd = [IntPtr]($windowEl.Current.NativeWindowHandle)
    $childHwnd = [Row12Native]::FindChromeWidgetChild($topHwnd)
    if ($topHwnd -ne [IntPtr]::Zero -and $childHwnd -ne [IntPtr]::Zero) {
        $topRect = Get-Win32Rect $topHwnd
        $childRect = Get-Win32Rect $childHwnd
        $dpi = [Row12Native]::GetDpiForWindow($topHwnd)
        $scale = $dpi / 96.0
        $expected = [Math]::Round(6 * $scale)
        $diffs = @(
            ($childRect.Left - $topRect.Left),
            ($childRect.Top - $topRect.Top),
            ($topRect.Right - $childRect.Right),
            ($topRect.Bottom - $childRect.Bottom)
        )
        $marginOk = ($diffs | ForEach-Object { [Math]::Abs($_ - $expected) -le 1 }) -notcontains $false
        Add-Check -Name 'leg2.webview-margin-6dip' -Passed:$marginOk -Detail "expected=$expected dpi=$dpi"
        $chromeRect = $topRect
    } else {
        Add-Check -Name 'leg2.webview-margin-6dip' -Passed:$false -Detail 'exit=1'
    }
} catch {
    Add-Check -Name 'leg2.chrome' -Passed:$false -Detail 'exit=1'
}

# ===== screenshot capture + sanity gate (after leg 2, before further UI churn) =========================

$screenshotPath = Join-Path $evidenceDir 'shell.png'
try {
    if ($chromeRect) {
        $width = $chromeRect.Right - $chromeRect.Left
        $height = $chromeRect.Bottom - $chromeRect.Top
        $bmp = New-Object System.Drawing.Bitmap($width, $height)
        $gfx = [System.Drawing.Graphics]::FromImage($bmp)
        $gfx.CopyFromScreen($chromeRect.Left, $chromeRect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
        $bmp.Save($screenshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $gfx.Dispose(); $bmp.Dispose()
        $captureOk = Test-Path -LiteralPath $screenshotPath
        Add-Check -Name 'capture.screenshot-saved' -Passed:$captureOk -Detail 'OK'
        if ($captureOk) {
            $saneScript = Join-Path $HOME '.claude\skills\roadmap\helpers\Test-CaptureSane.ps1'
            & pwsh -NoProfile -File $saneScript $screenshotPath | Out-Null
            Add-Check -Name 'capture.sanity-gate' -Passed:($LASTEXITCODE -eq 0) -Detail "exit=$LASTEXITCODE"
        }
    } else {
        Add-Check -Name 'capture.screenshot-saved' -Passed:$false -Detail 'exit=1'
    }
} catch {
    Add-Check -Name 'capture.screenshot-saved' -Passed:$false -Detail 'exit=1'
}

# ===== room discovery (legs 3 and 7 never hardcode "general" -- coordinator ruling) ====================

try {
    $rooms = Invoke-OwnerJson -Uri "$base/api/rooms"
    if ($rooms -and $rooms.Count -gt 0) {
        $firstRoomId = $rooms[0].id
        $firstRoomName = $rooms[0].name
    }
    Add-Check -Name 'setup.first-room-discovered' -Passed:([bool]$firstRoomId) -Detail 'OK'
} catch {
    Add-Check -Name 'setup.first-room-discovered' -Passed:$false -Detail 'exit=1'
}

function Wait-ComposerFocused {
    # Polls up to $TimeoutMs for AutomationElement.FocusedElement to be $Composer, retrying
    # SetForegroundWindow (+ ShowWindow SW_RESTORE, in case the window got minimized/hidden by an
    # earlier leg) up to $MaxAttempts times. SetForegroundWindow can be silently denied by the
    # foreground-lock rule, which is exactly the ambiguity this polling closes: a denied call still
    # returns without throwing, so only checking FocusedElement afterward proves focus actually moved.
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Composer,
        [Parameter(Mandatory)][IntPtr]$TopHwnd,
        [int]$MaxAttempts = 3,
        [int]$TimeoutMs = 2000
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        [Row12Native]::ShowWindow($TopHwnd, 9) | Out-Null   # SW_RESTORE
        [Row12Native]::SetForegroundWindow($TopHwnd) | Out-Null
        Start-Sleep -Milliseconds 200
        try { $Composer.SetFocus() } catch { }

        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
            try {
                $focused = $AE::FocusedElement
                if ($focused) {
                    if ($Automation::Compare($focused, $Composer)) { return $true }
                    if ($focused.Current.NativeWindowHandle -ne 0 -and $focused.Current.NativeWindowHandle -eq $Composer.Current.NativeWindowHandle) { return $true }
                    if ($focused.Current.AutomationId -and ($focused.Current.AutomationId -eq $Composer.Current.AutomationId) -and ($focused.Current.Name -eq $Composer.Current.Name)) { return $true }
                }
            } catch { }
            Start-Sleep -Milliseconds 150
        }
    }
    return $false
}

function Send-ComposerMessageAndVerify {
    # UIA-focus the composer for $RoomName (aria-label "Message <name>", Composer.tsx), type $Text via
    # SendKeys (a controlled React textarea needs real keystrokes, not ValuePattern.SetValue, to fire
    # onChange), press Enter, then poll the room's messages for it landing with authorId owner. $Text
    # must contain no SendKeys special characters (the caller uses a hex GUID for this reason).
    #
    # Returns a stage name rather than a bare boolean, so a flake can say WHERE it failed:
    #   'composer-not-found'  -- Find-DescendantByName never found "Message $RoomName"
    #   'focus-not-acquired'  -- FocusedElement never matched the composer within Wait-ComposerFocused's budget
    #   'not-landed'          -- typed (possibly twice) but the API never saw the body land as an owner message
    #   'landed'              -- success
    # One retry of the whole type-and-verify cycle if the message has not landed after the first 4s,
    # re-acquiring focus first; the verify loop continues to the original $VerifyTimeoutMs budget overall
    # (the retry does not add extra time on top of $VerifyTimeoutMs).
    param(
        [Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$WindowEl,
        [Parameter(Mandatory)][IntPtr]$TopHwnd,
        [Parameter(Mandatory)][string]$RoomId,
        [Parameter(Mandatory)][string]$RoomName,
        [Parameter(Mandatory)][string]$Text,
        [int]$VerifyTimeoutMs = 8000
    )
    $beforeId = 0
    try {
        $roomsNow = Invoke-OwnerJson -Uri "$base/api/rooms"
        $match = $roomsNow | Where-Object { $_.id -eq $RoomId } | Select-Object -First 1
        if ($match) { $beforeId = [int64]$match.lastMessageId }
    } catch { }

    $composer = Find-DescendantByName -parent $WindowEl -name "Message $RoomName" -timeoutMs 10000
    if (-not $composer) { return 'composer-not-found' }

    if (-not (Wait-ComposerFocused -Composer $composer -TopHwnd $TopHwnd)) { return 'focus-not-acquired' }

    $overallSw = [Diagnostics.Stopwatch]::StartNew()
    $firstAttemptBudgetMs = [Math]::Min(4000, $VerifyTimeoutMs)

    [System.Windows.Forms.SendKeys]::SendWait($Text)
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

    $landed = $false
    while ($overallSw.ElapsedMilliseconds -lt $firstAttemptBudgetMs) {
        try {
            $page = Invoke-OwnerJson -Uri "$base/api/rooms/$RoomId/messages?afterId=$beforeId&limit=20"
            $match2 = $page.messages | Where-Object { $_.body -eq $Text -and $_.authorId -eq 'owner' }
            if ($match2) { $landed = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 300
    }

    if (-not $landed -and $overallSw.ElapsedMilliseconds -lt $VerifyTimeoutMs) {
        # Re-acquire focus before the retry -- the first attempt may have lost the foreground window
        # just as easily as it may simply not have landed yet. Select-all + delete clears whatever may
        # have partially typed before retyping the same $Text (the API check matches the exact body, so
        # a fresh GUID is not needed -- only a clean composer is).
        if (-not (Wait-ComposerFocused -Composer $composer -TopHwnd $TopHwnd)) { return 'focus-not-acquired' }

        [System.Windows.Forms.SendKeys]::SendWait('^a{DEL}')
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait($Text)
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

        while ($overallSw.ElapsedMilliseconds -lt $VerifyTimeoutMs) {
            try {
                $page = Invoke-OwnerJson -Uri "$base/api/rooms/$RoomId/messages?afterId=$beforeId&limit=20"
                $match2 = $page.messages | Where-Object { $_.body -eq $Text -and $_.authorId -eq 'owner' }
                if ($match2) { $landed = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 300
        }
    }

    if ($landed) { return 'landed' }
    return 'not-landed'
}

# ===== leg 3: owner-token (the real path, end to end) ===================================================

$tokensJsonPath = Join-Path $D 'tokens.json'
$tokensBeforeLeg3 = $null
try { $tokensBeforeLeg3 = Get-Content -LiteralPath $tokensJsonPath -Raw -ErrorAction Stop } catch { }

if ($windowEl -and $firstRoomId) {
    $leg3Text = "shellcheck" + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $topHwnd = [IntPtr]($windowEl.Current.NativeWindowHandle)
    $leg3Stage = Send-ComposerMessageAndVerify -WindowEl $windowEl -TopHwnd $topHwnd -RoomId $firstRoomId -RoomName $firstRoomName -Text $leg3Text -VerifyTimeoutMs 8000
    Add-Check -Name 'leg3.owner-token-post-lands' -Passed:($leg3Stage -eq 'landed') -Detail $leg3Stage

    $tokensAfterLeg3 = $null
    try { $tokensAfterLeg3 = Get-Content -LiteralPath $tokensJsonPath -Raw -ErrorAction Stop } catch { }
    $tokensUnchanged = ($tokensBeforeLeg3 -ne $null) -and ($tokensBeforeLeg3 -eq $tokensAfterLeg3)
    Add-Check -Name 'leg3.tokens-json-byte-identical' -Passed:$tokensUnchanged -Detail 'OK'
} else {
    Add-Check -Name 'leg3.owner-token-post-lands' -Passed:$false -Detail 'exit=1'
    Add-Check -Name 'leg3.tokens-json-byte-identical' -Passed:$false -Detail 'exit=1'
}

# ===== leg 4: hide =====================================================================================

try {
    $sinceCount4 = (Get-LogLines -Path $desktopLog).Count
    if ($chromeButtons['Close']) { Invoke-UiaElement $chromeButtons['Close'] | Out-Null } else { throw 'no Close button' }
    Start-Sleep -Milliseconds 500
    $hideOk = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 2000) {
        $topHwnd = [IntPtr]($windowEl.Current.NativeWindowHandle)
        if (-not [Row12Native]::IsWindowVisible($topHwnd)) { $hideOk = $true; break }
        Start-Sleep -Milliseconds 200
    }
    Add-Check -Name 'leg4.window-hidden' -Passed:$hideOk -Detail 'OK'
    Add-Check -Name 'leg4.hub-pid-alive' -Passed:((Get-Process -Id $hubPid -ErrorAction SilentlyContinue) -ne $null) -Detail "pid=$hubPid"
    Add-Check -Name 'leg4.health-still-ok' -Passed:(Test-Health $base) -Detail 'OK'

    # Defect fix verification: the Close click's page->host bridge message must land as a numeric-id
    # request the host actually dispatches, logged by MainWindow.OnWebMessage as "BRIDGE cmd=close
    # ok=True" (desktop.log, under the data dir -- same file HUB STATE lines above already read from).
    $bridgeClose = Wait-ForLogPattern -Path $desktopLog -Pattern 'BRIDGE cmd=close ok=True' -SinceCount $sinceCount4 -TimeoutMs 5000
    Add-Check -Name 'leg4.bridge-close-logged' -Passed:$bridgeClose.Found -Detail "ms=$($bridgeClose.ElapsedMs)"
} catch {
    Add-Check -Name 'leg4.hide' -Passed:$false -Detail 'exit=1'
}

# ===== leg 5: show =====================================================================================

try {
    $showProc = Start-Tracked -Exe $TargetExe -Arguments @('--data', $D, '--show')
    $showProc.WaitForExit(10000) | Out-Null

    $showOk = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $rectMatches = $false
    while ($sw.ElapsedMilliseconds -lt 5000) {
        $el = Find-TopWindowByPid -procId $shellProc.Id -timeoutMs 1000
        if ($el) {
            $topHwnd = [IntPtr]($el.Current.NativeWindowHandle)
            if ([Row12Native]::IsWindowVisible($topHwnd)) {
                $showOk = $true
                if ($chromeRect) {
                    $r = Get-Win32Rect $topHwnd
                    $rectMatches = ($r.Left -eq $chromeRect.Left) -and ($r.Top -eq $chromeRect.Top) -and ($r.Right -eq $chromeRect.Right) -and ($r.Bottom -eq $chromeRect.Bottom)
                }
                break
            }
        }
        Start-Sleep -Milliseconds 250
    }
    Add-Check -Name 'leg5.window-visible-again' -Passed:$showOk -Detail 'OK'
    Add-Check -Name 'leg5.rect-unchanged' -Passed:$rectMatches -Detail 'OK'
} catch {
    Add-Check -Name 'leg5.show' -Passed:$false -Detail 'exit=1'
}

# ===== leg 6: maximize / restore =========================================================================

try {
    $windowEl = Find-TopWindowByPid -procId $shellProc.Id -timeoutMs 5000
    $topHwnd = [IntPtr]($windowEl.Current.NativeWindowHandle)
    $dpi = [Row12Native]::GetDpiForWindow($topHwnd)
    $scale = $dpi / 96.0

    # Each sub-check below logs its own explicit FAIL rather than throwing on a missing button, so a
    # product defect that leaves the window unmaximized (no glyph toggle -> no "Restore" button ever
    # appears) still produces clean, individually named FAIL rows instead of one generic outer-catch.
    $maxBtn = Find-DescendantByName -parent $windowEl -name 'Maximize' -timeoutMs 5000
    if ($maxBtn) {
        Invoke-UiaElement $maxBtn | Out-Null
        Start-Sleep -Milliseconds 800
    } else {
        Add-Check -Name 'leg6.maximize-button-found' -Passed:$false -Detail 'exit=1'
    }

    $workArea = [System.Windows.Forms.Screen]::FromHandle($topHwnd).WorkingArea
    $maxRect = Get-Win32Rect $topHwnd
    $expectedLeft = [Math]::Round($workArea.Left * $scale)
    $expectedTop = [Math]::Round($workArea.Top * $scale)
    $expectedRight = [Math]::Round($workArea.Right * $scale)
    $expectedBottom = [Math]::Round($workArea.Bottom * $scale)
    $maximizeOk = ([Math]::Abs($maxRect.Left - $expectedLeft) -le 2) -and ([Math]::Abs($maxRect.Top - $expectedTop) -le 2) `
        -and ([Math]::Abs($maxRect.Right - $expectedRight) -le 2) -and ([Math]::Abs($maxRect.Bottom - $expectedBottom) -le 2)
    Add-Check -Name 'leg6.maximized-fits-workarea' -Passed:$maximizeOk -Detail 'OK'

    $childHwnd = [Row12Native]::FindChromeWidgetChild($topHwnd)
    $marginZero = $false
    if ($childHwnd -ne [IntPtr]::Zero) {
        $childRect = Get-Win32Rect $childHwnd
        $marginZero = ($childRect.Left -eq $maxRect.Left) -and ($childRect.Top -eq $maxRect.Top) -and ($childRect.Right -eq $maxRect.Right) -and ($childRect.Bottom -eq $maxRect.Bottom)
    }
    Add-Check -Name 'leg6.margin-zero-maximized' -Passed:$marginZero -Detail 'OK'

    $restoreBtn = Find-DescendantByName -parent $windowEl -name 'Restore' -timeoutMs 5000
    if ($restoreBtn) {
        Invoke-UiaElement $restoreBtn | Out-Null
        Start-Sleep -Milliseconds 800
        $restoredRect = Get-Win32Rect $topHwnd
        $restoredW = $restoredRect.Right - $restoredRect.Left
        $restoredH = $restoredRect.Bottom - $restoredRect.Top
        $expectedW = [Math]::Round(1280 * $scale)
        $expectedH = [Math]::Round(800 * $scale)
        $restoreOk = ([Math]::Abs($restoredW - $expectedW) -le 2) -and ([Math]::Abs($restoredH - $expectedH) -le 2)
        Add-Check -Name 'leg6.restored-1280x800-scaled' -Passed:$restoreOk -Detail "w=$restoredW h=$restoredH"
    } else {
        # Expected fallout when the window never actually maximized (leg6.maximized-fits-workarea
        # above already recorded that): no glyph toggle means no "Restore" button ever appears. Still
        # an explicit, named FAIL rather than a thrown exception.
        Add-Check -Name 'leg6.restored-1280x800-scaled' -Passed:$false -Detail 'exit=1'
    }
} catch {
    Add-Check -Name 'leg6.maximize' -Passed:$false -Detail 'exit=1'
}

# ===== leg 7: spawn-peer (mandatory) ======================================================================
# One live tracked spawn (SpawnJobs.LiveCount > 0), reusing the row 29 peer-check-vehicle skill and
# run_gate mechanism (tools/skills/peer-check-vehicle, tools/Invoke-Row29PeerCheck.ps1's own pattern)
# rather than inventing a new one. Unlike Row29's own script this never plants a stolen credential --
# it only needs a live spawn window to exist; present-header.ps1's own gate outcome is irrelevant here
# (it fails fast with no stolen-mcp.json, which is fine: the CLAUDE.EXE directory spawn itself is what
# SpawnJobs tracks, for its whole lifetime, independent of what its one gate call does).
#
# --import-skill runs AFTER leg 1, not before: ImportSkill's HostCommand calls ChopDb.EnsureDatabase()
# itself (HostCommands.cs:186-187), which would silently pre-migrate the corpus's v2 database before
# the shell's hub ever started, zeroing out leg 1's SESSION START -> HUB STATE Ready migration-cost
# measurement -- exactly what the coordinator's ruling asked to keep honest. HostCommands.ImportSkill
# does not call HubLock.IsHeld the way RotateToken/SetClasses do, so running it against a data dir a
# live hub already holds is the tool's own supported shape, not a race this script invented.
try {
    $skillSource = Join-Path $RepoRoot 'tools\skills\peer-check-vehicle'
    $skillName = 'peer-check-vehicle'
    $skillScratchRoot = "$D.skillsrc"
    $skillScratchDir = Join-Path $skillScratchRoot $skillName
    New-Item -ItemType Directory -Path $skillScratchDir -Force | Out-Null
    Copy-Item -Path (Join-Path $skillSource '*') -Destination $skillScratchDir -Recurse -Force
    $skillMdPath = Join-Path $skillScratchDir 'SKILL.md'
    $skillMd = (Get-Content -LiteralPath $skillMdPath -Raw).Replace('__BASE_URL__', $localhostBase).Replace('__ROOM_ID__', 'peer')
    Set-Content -LiteralPath $skillMdPath -Value $skillMd -NoNewline -Encoding utf8

    & $HubExe --data $D --import-skill "$skillScratchDir" | Out-Null
    $importExit = $LASTEXITCODE
    Add-Check -Name 'leg7.skill-imported' -Passed:($importExit -eq 0) -Detail "exit=$importExit"

    $peerRoomDir = "$D.peerroom"
    New-Item -ItemType Directory -Path $peerRoomDir -Force | Out-Null
    $roomResp = Invoke-WebRequest -Uri "$base/api/rooms" -Method Post -Headers @{ Authorization = "Bearer $ownerToken" } -ContentType 'application/json' `
        -Body (@{ name = 'peer'; directory = $peerRoomDir } | ConvertTo-Json) -TimeoutSec 15 -SkipHttpErrorCheck
    $peerRoomOk = $roomResp.StatusCode -eq 201
    Add-Check -Name 'leg7.peer-room-bound' -Passed:$peerRoomOk -Detail "status=$($roomResp.StatusCode)"

    $runActive = $false
    if ($importExit -eq 0 -and $peerRoomOk) {
        Invoke-OwnerJson -Method Post -Uri "$localhostBase/api/rooms/peer/messages" -BearerToken $ownerToken -Body @{ body = "/$skillName @sonnet" } | Out-Null

        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 60000) {
            try {
                $run = Invoke-OwnerJson -Uri "$base/api/rooms/peer/run"
                if ($run -and $run.status -eq 'active') { $runActive = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 500
        }
    }
    Add-Check -Name 'leg7.spawn-live' -Passed:$runActive -Detail 'OK'

    if ($runActive -and $windowEl -and $firstRoomId) {
        $leg7Text = "shellcheck" + [guid]::NewGuid().ToString('N').Substring(0, 12)
        $topHwnd = [IntPtr]($windowEl.Current.NativeWindowHandle)
        $leg7Stage = Send-ComposerMessageAndVerify -WindowEl $windowEl -TopHwnd $topHwnd -RoomId $firstRoomId -RoomName $firstRoomName -Text $leg7Text -VerifyTimeoutMs 8000
        Add-Check -Name 'leg7.owner-post-lands-with-live-spawn' -Passed:($leg7Stage -eq 'landed') -Detail $leg7Stage
    } else {
        Add-Check -Name 'leg7.owner-post-lands-with-live-spawn' -Passed:$false -Detail 'exit=1'
    }

    # Best-effort drain so the spawn does not outlive this leg; not asserted (the credential check above
    # already ran during its live window).
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 120000) {
        try {
            $run = Invoke-OwnerJson -Uri "$base/api/rooms/peer/run"
            if (-not $run -or $run.status -in @('ended', 'parked')) { break }
        } catch { break }
        Start-Sleep -Seconds 2
    }
} catch {
    Add-Check -Name 'leg7.spawn-peer' -Passed:$false -Detail 'exit=1'
}

# ===== leg 8: quit =========================================================================================

try {
    $quitProc = Start-Tracked -Exe $TargetExe -Arguments @('--data', $D, '--quit')
    $exited = $quitProc.WaitForExit(10000)
    Add-Check -Name 'leg8.quit-exit-0' -Passed:($exited -and $quitProc.ExitCode -eq 0) -Detail "exit=$($quitProc.ExitCode)"

    $hubGone = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 10000) {
        if (-not (Get-Process -Id $hubPid -ErrorAction SilentlyContinue)) { $hubGone = $true; break }
        Start-Sleep -Milliseconds 300
    }
    Add-Check -Name 'leg8.hub-pid-gone' -Passed:$hubGone -Detail "pid=$hubPid"

    $lockPath = Join-Path $D 'hub.lock'
    $lockReleased = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 5000) {
        try { $fs = [System.IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None'); $fs.Close(); $lockReleased = $true; break }
        catch { Start-Sleep -Milliseconds 300 }
    }
    Add-Check -Name 'leg8.hub-lock-released' -Passed:$lockReleased -Detail 'OK'
} catch {
    Add-Check -Name 'leg8.quit' -Passed:$false -Detail 'exit=1'
}

# ===== leg 9: attach =======================================================================================

$handHub = $null
try {
    $handHub = Start-Tracked -Exe $HubExe -Arguments @('--data', $D, '--port', "$P2")
    $handHubReady = $false
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 20000) {
        if (Test-Health "http://127.0.0.1:$P2") { $handHubReady = $true; break }
        Start-Sleep -Milliseconds 300
    }
    Add-Check -Name 'leg9.hand-started-hub-ready' -Passed:$handHubReady -Detail "pid=$($handHub.Id)"

    $sinceCount9 = (Get-LogLines -Path $desktopLog).Count
    $attachProc = Start-Tracked -Exe $TargetExe -Arguments @('--data', $D, '--hub', $HubExe, '--port', "$P")
    $attached = Wait-ForLogPattern -Path $desktopLog -Pattern 'HUB STATE Attached' -SinceCount $sinceCount9 -TimeoutMs 25000
    $portInLog = $attached.Found -and ($attached.Line -match [regex]::Escape(":$P2"))
    Add-Check -Name 'leg9.attached-to-hand-started-hub' -Passed:([bool]$portInLog) -Detail 'OK'

    $nav9 = Wait-ForLogPattern -Path $desktopLog -Pattern ([regex]::Escape("NAV True http://127.0.0.1:$P2")) -SinceCount $sinceCount9 -TimeoutMs 15000
    Add-Check -Name 'leg9.nav-to-attached-port' -Passed:$nav9.Found -Detail 'OK'

    $noChild = -not ((Get-LogLines -Path $desktopLog) | Select-Object -Skip $sinceCount9 | Where-Object { $_ -match 'HUB START pid=' })
    Add-Check -Name 'leg9.no-child-spawned' -Passed:$noChild -Detail 'OK'

    $attachWindowEl = Find-TopWindowByPid -procId $attachProc.Id -timeoutMs 10000
    $chipEl = if ($attachWindowEl) { Find-DescendantByName -parent $attachWindowEl -name 'hub · attached' -timeoutMs 10000 } else { $null }
    Add-Check -Name 'leg9.chip-reads-attached' -Passed:($null -ne $chipEl) -Detail 'OK'

    $quit9 = Start-Tracked -Exe $TargetExe -Arguments @('--data', $D, '--quit')
    $quit9.WaitForExit(10000) | Out-Null
    Add-Check -Name 'leg9.attach-quit-exit-0' -Passed:($quit9.ExitCode -eq 0) -Detail "exit=$($quit9.ExitCode)"

    Add-Check -Name 'leg9.hand-hub-survives-quit' -Passed:(Test-Health "http://127.0.0.1:$P2") -Detail "pid=$($handHub.Id)"
} catch {
    Add-Check -Name 'leg9.attach' -Passed:$false -Detail 'exit=1'
} finally {
    if ($handHub) { Stop-TrackedPid $handHub.Id }
}

# ===== cleanup ==============================================================================================

foreach ($procId in $script:TrackedPids) { Stop-TrackedPid $procId }

try { Remove-Item -LiteralPath $D -Recurse -Force -ErrorAction SilentlyContinue } catch { }
try { Remove-Item -LiteralPath "$D.skillsrc" -Recurse -Force -ErrorAction SilentlyContinue } catch { }
try { Remove-Item -LiteralPath "$D.peerroom" -Recurse -Force -ErrorAction SilentlyContinue } catch { }

# ===== summary ===============================================================================================

Write-Host ('-' * 60)
Write-Host ("RunId {0}: {1} PASS, {2} FAIL — log: {3}" -f $RunId, $script:pass, $script:fail, $log)
Write-Host "Evidence directory: $evidenceDir"
exit ($(if ($script:fail -gt 0) { 1 } else { 0 }))
