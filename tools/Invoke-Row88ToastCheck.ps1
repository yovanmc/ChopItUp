#Requires -Version 7
<#
.SYNOPSIS
    Live check of run notifications through the real desktop shell and hub, on fabricated data, with
    a fake Claude CLI and no model call.

.DESCRIPTION
    Starts the given desktop shell on a scratch data folder under %TEMP%, so it starts its own hub
    there. The shell, and the hub it starts, run with a PATH that holds only a fake claude.cmd, Git,
    PowerShell and the Windows folders, so no real model CLI can be found. CHOPITUP_ROOMS points at a
    scratch folder. The fake CLI answers its first launch with a usage limit that names a reset and
    every later launch with one that names none.

    The check imports a run skill, creates a room, hides the shell window and starts a run. The run
    is held for the limit: the shell must show one held notification while hidden. A UIA click on
    that notification must open the room. A post resumes the run, the second limit parks it, and the
    shell must show one parked notification. The facts come from the shell's own desktop.log and the
    hub's HTTP API.

    Needs an interactive desktop with notifications allowed: a notification hidden by Do Not Disturb
    fails the click leg and says so. Every process this script starts is stopped by its PID.

    The last line is "Results: n/m PASS". Exit 0 only when every check passes.
#>
[CmdletBinding()]
param(
    [string]$ShellExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Desktop\bin\Debug\net10.0-windows\ChopItUp.Desktop.exe'),
    [string]$HubExe = (Join-Path $PSScriptRoot '..\src\ChopItUp.Hub\bin\Debug\net10.0\ChopItUp.Hub.exe')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ChopTokenHelpers.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

$ShellExe = (Resolve-Path -LiteralPath $ShellExe).Path
$HubExe = (Resolve-Path -LiteralPath $HubExe).Path
$scratch = Join-Path $env:TEMP ('chopitup_toastcheck_' + [guid]::NewGuid().ToString('N'))
$dataDir = "$scratch\data"; $roomsRoot = "$scratch\rooms"; $fakeBin = "$scratch\bin"; $skillDir = "$scratch\skill\build-thing"
New-Item -ItemType Directory -Force $roomsRoot, $fakeBin, $skillDir | Out-Null
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start(); $port = $l.LocalEndpoint.Port; $l.Stop()
$base = "http://127.0.0.1:$port"
$ownerToken = (Initialize-ChopScratchTokens -DataDir $dataDir -ParticipantIds @('owner')).owner
$headers = New-ChopBearerHeaders -Token $ownerToken
$log = "$dataDir\logs\desktop.log"
$fails = 0; $checks = 0; $skipped = $null
$started = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $script:checks++
    if (-not $Passed) { $script:fails++ }
    Write-Host ("{0,-5} {1}  {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail).TrimEnd()
}

function Get-LogCount([string]$Line) {
    if (-not (Test-Path -LiteralPath $log)) { return 0 }
    @(Get-Content -LiteralPath $log | Where-Object { $_ -like "*$Line*" }).Count
}

function Wait-LogLine([string]$Line, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if ((Get-LogCount $Line) -gt 0) { return $true }; Start-Sleep -Milliseconds 250 }
    return $false
}

function Invoke-Owner([string]$Method, [string]$Path, $Body) {
    Invoke-RestMethod -Uri "$base$Path" -Method $Method -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 30
}

# The notification is a toast in the shell's notification popup; it is found by its title text.
function Find-Toast([string]$Title, [int]$Seconds = 15) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'New notification')
        foreach ($popup in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
            foreach ($el in $popup.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                if ($el.Current.Name -like "*$Title*") { return $el }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

try {
    # The fake CLI: a usage limit with a reset on the first launch, one with no reset after that.
    Set-Content -LiteralPath "$fakeBin\claude.cmd" -Encoding ascii -Value @(
        '@echo off'
        'if exist "%~dp0launched" goto spend'
        'type nul > "%~dp0launched"'
        'echo {"type":"result","is_error":true,"result":"You''ve hit your 5-hour limit - resets 11pm (America/Chicago)"}'
        'exit /b 1'
        ':spend'
        'echo {"type":"result","is_error":true,"result":"You''ve hit your monthly spend limit."}'
        'exit /b 1'
    )
    Set-Content -LiteralPath "$skillDir\SKILL.md" -Encoding utf8NoBOM -Value "---`nname: build-thing`nrun: true`n---`n`n# Build Thing`n`nBuild the thing.`n"
    $import = Start-Process -FilePath $HubExe -ArgumentList @('--data', "`"$dataDir`"", '--import-skill', "`"$skillDir`"") -PassThru -Wait -NoNewWindow `
        -RedirectStandardOutput "$scratch\import.out.log" -RedirectStandardError "$scratch\import.err.log"
    Add-Check 'skill.imported' ($import.ExitCode -eq 0) "exit=$($import.ExitCode)"

    # The shell and its hub see only the fake CLI.
    $savedPath = $env:PATH; $savedRooms = $env:CHOPITUP_ROOMS
    $env:PATH = "$fakeBin;$env:SystemRoot\System32;$env:SystemRoot;$env:ProgramFiles\Git\cmd;$env:ProgramFiles\PowerShell\7"
    $env:CHOPITUP_ROOMS = $roomsRoot
    try { $shell = Start-Process -FilePath $ShellExe -ArgumentList @('--data', "`"$dataDir`"", '--port', "$port", '--hub', "`"$HubExe`"") -PassThru }
    finally { $env:PATH = $savedPath; $env:CHOPITUP_ROOMS = $savedRooms }
    $started.Add($shell)

    $health = $null
    foreach ($i in 1..80) { try { $health = Invoke-RestMethod "$base/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } }
    Add-Check 'hub.ready' ($null -ne $health) "port=$port"
    Add-Check 'shell.listening' (Wait-LogLine 'NOTICES connected')

    $room = Invoke-Owner Post '/api/rooms' @{ name = 'Toast Lab' }
    Add-Check 'room.created' ([bool]$room.id)

    # Hide the window, as the close button does.
    $win = $null
    foreach ($i in 1..40) {
        $cond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $shell.Id)
        $win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($win) { break }; Start-Sleep -Milliseconds 250
    }
    if ($win) { ([System.Windows.Automation.WindowPattern]$win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close() }
    Add-Check 'shell.hidden' (Wait-LogLine 'HIDE' 10)

    # The run is held for the first limit.
    Invoke-Owner Post "/api/rooms/$($room.id)/messages" @{ body = '/build-thing @sonnet begin' } | Out-Null
    Add-Check 'notice.held-shown' (Wait-LogLine 'NOTICE shown kind=held run=1' 45)

    # With Windows notifications off for this user, Windows draws no notification at all, so the click
    # leg is reported as not run, never as a pass.
    $toastsOff = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\PushNotifications' -ErrorAction SilentlyContinue).ToastEnabled -eq 0
    $toast = $null
    if ($toastsOff) { $script:skipped = 'toast.click-opens-room: Windows notifications are off for this user (ToastEnabled=0), so the click is not verified' }
    else {
        $toast = Find-Toast 'Run #1 in Toast Lab held for a usage limit'
        Add-Check 'toast.found' ($null -ne $toast) $(if (-not $toast) { 'notification not on screen (Do Not Disturb?)' } else { '' })
    }
    if ($toast) {
        $invoke = $null
        if ($toast.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) { $invoke.Invoke() }
        else { $toast.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
        Add-Check 'toast.click-opens-room' (Wait-LogLine 'OPEN ROOM from notice' 15)
    }

    # A post resumes it; the second limit names no reset and parks it.
    Invoke-Owner Post "/api/rooms/$($room.id)/messages" @{ body = 'carry on' } | Out-Null
    Add-Check 'notice.parked-shown' (Wait-LogLine 'NOTICE shown kind=parked run=1' 45)
    Add-Check 'notice.one-each' ((Get-LogCount 'NOTICE shown kind=held') -eq 1 -and (Get-LogCount 'NOTICE shown kind=parked') -eq 1)
}
catch {
    Add-Check 'run' $false $_.Exception.Message
}
finally {
    # The shell's own Quit stops the hub it started; anything still alive is stopped by PID.
    try { Start-Process -FilePath $ShellExe -ArgumentList @('--data', "`"$dataDir`"", '--quit') -Wait -NoNewWindow | Out-Null } catch { }
    foreach ($p in $started) { if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit(10000) | Out-Null } }
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:skipped) { Write-Host "SKIP  $script:skipped" }
Write-Host "Results: $($checks - $fails)/$checks PASS"
exit ($(if ($fails -gt 0) { 1 } else { 0 }))
